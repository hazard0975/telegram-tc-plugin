using System;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace TgVfsPlugin;

/// <summary>
/// Экспортируемые функции для Total Commander (WFX API).
/// Вызываются через Native AOT.
/// </summary>
public static unsafe class WfxExports
{
    private static VfsDatabase? _db;
    private static int _pluginNumber;
    private static IntPtr _pProgressProc;
    private static Win32Api.ProgressProc? _progressProcDelegate;
    private static string? _lastMirrorPath;

    // Класс для хранения состояния поиска
    private class FindState
    {
        public List<VfsDatabase.VfsItem> Items { get; set; } = new();
        public int CurrentIndex { get; set; } = 0;
    }

    private static readonly ConcurrentDictionary<IntPtr, FindState> _searchStates = new();
    private static int _nextHandle = 1;

    // Вспомогательный метод для конвертации DateTime в FILETIME
    private static Win32Api.FILETIME DateTimeToFileTime(DateTime time)
    {
        // Total Commander в WIN32_FIND_DATA ожидает FILETIME в UTC,
        // после чего сам нативно переводит его в локальное системное время пользователя.
        // Даты из SQLite и Telegram являются UTC (или Unspecified), поэтому используем ToFileTimeUtc(),
        // чтобы .NET не производил ошибочное вычитание локального часового пояса.
        long fileTime = (time.Kind == DateTimeKind.Local) ? time.ToFileTime() : time.ToFileTimeUtc();
        return new Win32Api.FILETIME
        {
            dwLowDateTime = (uint)(fileTime & 0xFFFFFFFF),
            dwHighDateTime = (uint)(fileTime >> 32)
        };
    }

    // Вспомогательный метод для заполнения WIN32_FIND_DATAA
    private static void FillFindDataA(Win32Api.WIN32_FIND_DATAA* data, VfsDatabase.VfsItem item)
    {
        *data = default;
        data->dwFileAttributes = item.IsDirectory ? Win32Api.FILE_ATTRIBUTE_DIRECTORY : Win32Api.FILE_ATTRIBUTE_NORMAL;
        data->nFileSizeHigh = (uint)(item.Size >> 32);
        data->nFileSizeLow = (uint)(item.Size & 0xFFFFFFFF);
        
        var ft = DateTimeToFileTime(item.Date);
        data->ftCreationTime = ft;
        data->ftLastAccessTime = ft;
        data->ftLastWriteTime = ft;

        byte[] nameBytes = System.Text.Encoding.Default.GetBytes(item.Name);
        for (int i = 0; i < nameBytes.Length && i < Win32Api.MAX_PATH - 1; i++)
        {
            data->cFileName[i] = nameBytes[i];
        }
    }

    // Вспомогательный метод для заполнения WIN32_FIND_DATAW
    private static void FillFindDataW(Win32Api.WIN32_FIND_DATAW* data, VfsDatabase.VfsItem item)
    {
        *data = default;
        data->dwFileAttributes = item.IsDirectory ? Win32Api.FILE_ATTRIBUTE_DIRECTORY : Win32Api.FILE_ATTRIBUTE_NORMAL;
        data->nFileSizeHigh = (uint)(item.Size >> 32);
        data->nFileSizeLow = (uint)(item.Size & 0xFFFFFFFF);
        
        var ft = DateTimeToFileTime(item.Date);
        data->ftCreationTime = ft;
        data->ftLastAccessTime = ft;
        data->ftLastWriteTime = ft;

        string name = item.Name;
        for (int i = 0; i < name.Length && i < Win32Api.MAX_PATH - 1; i++)
        {
            data->cFileName[i] = name[i];
        }
    }

    private static FindState? CreateStateForPath(string pathStr)
    {
        Logger.Log($"Requested path: '{pathStr}'");

        if (!TelegramManager.IsLoggedIn)
        {
            // Попытка тихо авторизоваться, если есть сессия
            try
            {
                if (System.IO.File.Exists(TelegramManager.ConfigPath + "\\WTelegram.session"))
                {
                    Logger.Log("Found session file, attempting silent login...");
                    // Вызываем синхронно, передаем true для тихого режима
                    System.Threading.Tasks.Task.Run(() => TelegramManager.LoginAsync(true)).GetAwaiter().GetResult();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Silent login failed: {ex}");
            }

            if (!TelegramManager.IsLoggedIn)
            {
                Logger.Log("User is not logged in. Returning [ Login required.txt ].");
                var loginState = new FindState();
                loginState.Items.Add(new VfsDatabase.VfsItem 
                { 
                    Name = "[ Login required.txt ]", 
                    IsDirectory = false, 
                    Size = 100, 
                    Date = DateTime.Now 
                });
                return loginState;
            }
        }

        if (_db == null)
        {
            Logger.Log("Error: Database is null.");
            return null;
        }

        string dirPath = pathStr;
        
        // Удаляем маску поиска (например *.*) если она есть в конце пути
        int lastSlash = dirPath.LastIndexOf('\\');
        if (lastSlash == -1) lastSlash = dirPath.LastIndexOf('/');
        if (lastSlash >= 0)
        {
            string lastPart = dirPath.Substring(lastSlash + 1);
            if (lastPart.Contains("*") || lastPart.Contains("?"))
            {
                dirPath = dirPath.Substring(0, lastSlash);
            }
        }

        dirPath = dirPath.TrimEnd('\\', '/');
        if (string.IsNullOrEmpty(dirPath)) dirPath = "\\";

        Logger.Log($"Parsed directory path for search: '{dirPath}'");
        
        var state = new FindState();
        try
        {
            if (dirPath == "\\" || dirPath == "/")
            {
                _lastMirrorPath = null;
                // Корень: возвращаем каналы
                Logger.Log("Fetching channels for root.");
                state.Items.Add(new VfsDatabase.VfsItem 
                { 
                    Name = "[+] Создать папку", 
                    IsDirectory = false, 
                    Size = 0,
                    Date = DateTime.Now 
                });
                state.Items.AddRange(_db.GetMounts());
            }
            else
            {
                // Внутри канала (наш путь начинается с \ или /, например \Work Chat)
                string fullPath = dirPath.TrimStart('\\', '/');
                string[] parts = fullPath.Split(new[] { '\\', '/' });
                string channelTitle = parts[0];
                
                var mount = _db.GetMountByName(channelTitle);
                if (mount != null && mount.Mode == 0 && !string.IsNullOrEmpty(mount.LocalPath))
                {
                    string localPathToSet = mount.LocalPath;
                    if (parts.Length > 1)
                    {
                        string[] subParts = new string[parts.Length - 1];
                        Array.Copy(parts, 1, subParts, 0, parts.Length - 1);
                        localPathToSet = System.IO.Path.Combine(mount.LocalPath, System.IO.Path.Combine(subParts));
                    }
                    
                    if (!string.Equals(_lastMirrorPath, localPathToSet, StringComparison.OrdinalIgnoreCase))
                    {
                        _lastMirrorPath = localPathToSet;
                        Logger.Log($"Entering Mirror folder '{channelTitle}'. Sending CD '{localPathToSet}' to target panel.");
                        // Cannot use async/await in unsafe context, use ContinueWith or thread pool
                        System.Threading.Tasks.Task.Delay(100).ContinueWith(_ => {
                            Win32Api.ChangeInactivePanelDir(localPathToSet);
                        });
                    }
                }
                
                Logger.Log($"Fetching files for channel: '{channelTitle}'");
                state.Items = _db.GetFiles(channelTitle);
            }

            Logger.Log($"Found {state.Items.Count} items.");
        }
        catch (Exception ex)
        {
            Logger.Log($"Exception in CreateStateForPath: {ex}");
            return null;
        }

        if (state.Items.Count == 0) return null;
        return state;
    }

    // Обязательная функция: инициализация плагина
    [UnmanagedCallersOnly(EntryPoint = "FsInit", CallConvs = [typeof(CallConvStdcall)])]
    public static int FsInit(int pluginNumber, IntPtr pProgressProc, IntPtr pLogProc, IntPtr pRequestProc)
    {
        try
        {
            Logger.Log($"FsInit called (Plugin Number: {pluginNumber})");
            _pluginNumber = pluginNumber;
            _pProgressProc = pProgressProc;
            if (_pProgressProc != IntPtr.Zero)
            {
                try
                {
                    _progressProcDelegate = Marshal.GetDelegateForFunctionPointer<Win32Api.ProgressProc>(_pProgressProc);
                }
                catch (Exception ex)
                {
                    Logger.Log($"Failed to get ProgressProc delegate: {ex.Message}");
                }
            }
            
            // Принудительно загружаем DLL в память процесса до того, как к ней обратится SQLite
            try
            {
                string basePath = AppContext.BaseDirectory;
                
                using var processModule = System.Diagnostics.Process.GetCurrentProcess().Modules.Cast<System.Diagnostics.ProcessModule>()
                    .FirstOrDefault(m => m.ModuleName != null && m.ModuleName.StartsWith("TgVfsPlugin", StringComparison.OrdinalIgnoreCase));
                    
                if (processModule != null && !string.IsNullOrEmpty(processModule.FileName))
                {
                    basePath = System.IO.Path.GetDirectoryName(processModule.FileName) ?? basePath;
                }

                string arch = IntPtr.Size == 8 ? "x64" : "x86";
                string libPath = System.IO.Path.Combine(basePath, arch, "e_sqlite3.dll");
                
                Logger.Log($"Manually loading SQLite DLL from: {libPath}");
                
                if (System.IO.File.Exists(libPath))
                {
                    IntPtr libHandle = System.Runtime.InteropServices.NativeLibrary.Load(libPath);
                    Logger.Log($"Load successful, handle: {libHandle}");
                }
                else
                {
                    Logger.Log($"ERROR: File does not exist at {libPath}");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Manual DLL load failed: {ex}");
            }

            // Инициализируем базу данных при запуске плагина
            if (_db == null)
            {
                _db = new VfsDatabase();
                Logger.Log("VfsDatabase successfully initialized.");
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"Critical Exception in FsInit: {ex}");
        }
        return 0; // успех
    }

    // Обязательная функция: начало поиска файлов (ANSI)
    [UnmanagedCallersOnly(EntryPoint = "FsFindFirst", CallConvs = [typeof(CallConvStdcall)])]
    public static IntPtr FsFindFirst(byte* path, IntPtr findFileData)
    {
        string pathStr = Marshal.PtrToStringAnsi((IntPtr)path) ?? "";
        
        var state = CreateStateForPath(pathStr);
        if (state == null || state.Items.Count == 0) 
        {
            Win32Api.SetLastError(Win32Api.ERROR_NO_MORE_FILES);
            return new IntPtr(Win32Api.INVALID_HANDLE_VALUE);
        }

        Win32Api.WIN32_FIND_DATAA* data = (Win32Api.WIN32_FIND_DATAA*)findFileData;
        FillFindDataA(data, state.Items[0]);
        state.CurrentIndex = 1;

        IntPtr handle = new IntPtr(System.Threading.Interlocked.Increment(ref _nextHandle));
        _searchStates[handle] = state;
        return handle;
    }

    // Обязательная функция: продолжение поиска файлов (ANSI)
    [UnmanagedCallersOnly(EntryPoint = "FsFindNext", CallConvs = [typeof(CallConvStdcall)])]
    public static int FsFindNext(IntPtr hdl, IntPtr findFileData)
    {
        if (_searchStates.TryGetValue(hdl, out var state))
        {
            if (state.CurrentIndex < state.Items.Count)
            {
                Win32Api.WIN32_FIND_DATAA* data = (Win32Api.WIN32_FIND_DATAA*)findFileData;
                FillFindDataA(data, state.Items[state.CurrentIndex]);
                state.CurrentIndex++;
                return 1; // true
            }
        }
        Win32Api.SetLastError(Win32Api.ERROR_NO_MORE_FILES);
        return 0; // false (больше нет файлов)
    }

    // Обязательная функция: начало поиска файлов (Unicode)
    [UnmanagedCallersOnly(EntryPoint = "FsFindFirstW", CallConvs = [typeof(CallConvStdcall)])]
    public static IntPtr FsFindFirstW(char* path, IntPtr findFileData)
    {
        string pathStr = Marshal.PtrToStringUni((IntPtr)path) ?? "";

        var state = CreateStateForPath(pathStr);
        if (state == null || state.Items.Count == 0)
        {
            Win32Api.SetLastError(Win32Api.ERROR_NO_MORE_FILES);
            return new IntPtr(Win32Api.INVALID_HANDLE_VALUE);
        }

        Win32Api.WIN32_FIND_DATAW* data = (Win32Api.WIN32_FIND_DATAW*)findFileData;
        FillFindDataW(data, state.Items[0]);
        state.CurrentIndex = 1;

        IntPtr handle = new IntPtr(System.Threading.Interlocked.Increment(ref _nextHandle));
        _searchStates[handle] = state;
        return handle;
    }

    // Обязательная функция: продолжение поиска файлов (Unicode)
    [UnmanagedCallersOnly(EntryPoint = "FsFindNextW", CallConvs = [typeof(CallConvStdcall)])]
    public static int FsFindNextW(IntPtr hdl, IntPtr findFileData)
    {
        if (_searchStates.TryGetValue(hdl, out var state))
        {
            if (state.CurrentIndex < state.Items.Count)
            {
                Win32Api.WIN32_FIND_DATAW* data = (Win32Api.WIN32_FIND_DATAW*)findFileData;
                FillFindDataW(data, state.Items[state.CurrentIndex]);
                state.CurrentIndex++;
                return 1; // true
            }
        }
        Win32Api.SetLastError(Win32Api.ERROR_NO_MORE_FILES);
        return 0; // false
    }

    // Обязательная функция: завершение поиска
    [UnmanagedCallersOnly(EntryPoint = "FsFindClose", CallConvs = [typeof(CallConvStdcall)])]
    public static int FsFindClose(IntPtr hdl)
    {
        _searchStates.TryRemove(hdl, out _);
        return 0; // успех
    }

    // Выполнение файла (пользователь нажал Enter на файле)
    [UnmanagedCallersOnly(EntryPoint = "FsExecuteFile", CallConvs = [typeof(CallConvStdcall)])]
    public static int FsExecuteFile(IntPtr MainWin, IntPtr RemoteName, IntPtr Verb)
    {
        string path = Marshal.PtrToStringAnsi(RemoteName) ?? "";
        string verbStr = Marshal.PtrToStringAnsi(Verb) ?? "";
        return HandleExecuteFile(path, verbStr);
    }

    // Выполнение файла (Unicode)
    [UnmanagedCallersOnly(EntryPoint = "FsExecuteFileW", CallConvs = [typeof(CallConvStdcall)])]
    public static int FsExecuteFileW(IntPtr MainWin, IntPtr RemoteName, IntPtr Verb)
    {
        string path = Marshal.PtrToStringUni(RemoteName) ?? "";
        string verbStr = Marshal.PtrToStringUni(Verb) ?? "";
        return HandleExecuteFile(path, verbStr);
    }

    private static int HandleExecuteFile(string path, string verb)
    {
        Logger.Log($"FsExecuteFile called for: {path} (Verb: {verb})");

        if (verb != "open" && verb != "") return 2; // FS_EXEC_ERROR

        if (path.EndsWith("[+] Создать папку"))
        {
            System.Threading.Tasks.Task.Run(() => 
            {
                try
                {
                    var result = CreateFolderDialog.Show();
                    if (result != null)
                    {
                        string cname = "[TC] " + result.Name;
                        long cid = TelegramManager.CreateChannelAsync(cname, "TelegramVFS channel").GetAwaiter().GetResult();
                        
                        _db!.AddMount(new VfsDatabase.MountInfo {
                            Id = "tg-fldr-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                            ChannelId = cid,
                            ChannelName = result.Name, // сохраняем без префикса для удобства
                            Mode = result.Mode,
                            LocalPath = result.LocalPath
                        });
                        
                        Logger.Log($"Folder created successfully: {result.Name}");
                        Win32Api.RefreshActivePanel();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"Create folder error: {ex}");
                }
            }).GetAwaiter().GetResult();
            
            return 0; // FS_EXEC_OK
        }

        if (path.EndsWith("[ Login required.txt ]"))
        {
            // Запускаем асинхронный логин в синхронном контексте без await (Task.Run)
            System.Threading.Tasks.Task.Run(() => 
            {
                try
                {
                    bool success = TelegramManager.LoginAsync().GetAwaiter().GetResult();
                    if (success)
                    {
                        Logger.Log("Login successful! Requesting panel refresh.");
                        Win32Api.RefreshActivePanel();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"Login task failed: {ex}");
                }
            }).GetAwaiter().GetResult();
            
            return 0; // FS_EXEC_OK
        }

        return 2; // FS_EXEC_ERROR
    }

    private static bool ReportProgress(string sourceName, string targetName, int percentDone)
    {
        if (_progressProcDelegate == null && _pProgressProc != IntPtr.Zero)
        {
            try
            {
                _progressProcDelegate = Marshal.GetDelegateForFunctionPointer<Win32Api.ProgressProc>(_pProgressProc);
            }
            catch { }
        }

        if (_progressProcDelegate == null) return false;

        try
        {
            IntPtr pSrc = Marshal.StringToHGlobalAnsi(sourceName);
            IntPtr pDst = Marshal.StringToHGlobalAnsi(targetName);
            try
            {
                int res = _progressProcDelegate(_pluginNumber, pSrc, pDst, percentDone);
                return res == 1; // 1 = пользователь нажал Отмена или [X]
            }
            finally
            {
                Marshal.FreeHGlobal(pSrc);
                Marshal.FreeHGlobal(pDst);
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"Progress callback error: {ex.Message}");
            return false;
        }
    }

    private static int HandlePutFile(string localPath, string remotePath, int copyFlags)
    {
        Logger.Log($"FsPutFile called: Local='{localPath}', Remote='{remotePath}', Flags={copyFlags}");

        if (!System.IO.File.Exists(localPath))
        {
            Logger.Log($"FsPutFile: Local file does not exist: '{localPath}'");
            return Win32Api.FS_FILE_NOTFOUND;
        }

        if (_db == null)
        {
            Logger.Log("FsPutFile: Database is not initialized.");
            return Win32Api.FS_FILE_WRITEERROR;
        }

        string cleanRemote = remotePath.TrimStart('\\', '/');
        int firstSlash = cleanRemote.IndexOfAny(new[] { '\\', '/' });
        if (firstSlash <= 0)
        {
            Logger.Log($"FsPutFile: Destination is root or invalid. Cannot copy directly to root: '{remotePath}'");
            return Win32Api.FS_FILE_NOTSUPPORTED;
        }

        string channelName = cleanRemote.Substring(0, firstSlash);
        string subPath = cleanRemote.Substring(firstSlash + 1).Replace('/', '\\');
        string fileName = System.IO.Path.GetFileName(subPath);

        var mount = _db.GetMountByName(channelName);
        if (mount == null)
        {
            Logger.Log($"FsPutFile: Channel '{channelName}' not found in database.");
            return Win32Api.FS_FILE_NOTFOUND;
        }

        var existingFile = _db.GetFile(mount.Id, fileName, parent: null);
        if (existingFile != null)
        {
            bool overwrite = (copyFlags & Win32Api.FS_COPYFLAGS_OVERWRITE) != 0;
            if (!overwrite)
            {
                Logger.Log($"FsPutFile: File '{fileName}' already exists in '{channelName}' and OVERWRITE flag is not set.");
                return Win32Api.FS_FILE_EXISTS;
            }
        }

        try
        {
            var fileInfo = new System.IO.FileInfo(localPath);
            long limitBytes = TelegramManager.IsPremium ? 4294967296L : 2147483648L;
            if (fileInfo.Length > limitBytes)
            {
                Logger.Log($"FsPutFile: File size {fileInfo.Length} bytes exceeds limit of {limitBytes} bytes.");
                return Win32Api.FS_FILE_WRITEERROR;
            }

            int messageId = 0;
            try
            {
                messageId = TelegramManager.UploadAndSendFileAsync(
                    mount.ChannelId,
                    localPath,
                    fileName,
                    subPath,
                    onProgress: (sent, total) =>
                    {
                        int pct = total > 0 ? (int)((sent * 100) / total) : 0;
                        if (pct > 100) pct = 100;
                        return ReportProgress(localPath, remotePath, pct);
                    }
                ).GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is OperationCanceledException || ex.InnerException is OperationCanceledException)
            {
                Logger.Log($"FsPutFile: Upload was cancelled by user.");
                return Win32Api.FS_FILE_USERABORT;
            }

            if (messageId <= 0)
            {
                Logger.Log($"FsPutFile: Upload failed (no message ID).");
                return Win32Api.FS_FILE_WRITEERROR;
            }

            // Обработка перезаписи: переносим старую версию в корзину и инкрементируем версию
            int ver = 1;
            if (existingFile != null)
            {
                _db.MoveFileToTrash(existingFile.Uid);
                ver = existingFile.Ver + 1;
                Logger.Log($"FsPutFile: Existing file '{fileName}' marked in_trash=1. New version: {ver}");
            }

            _db.AddFile(new VfsDatabase.FileRecord
            {
                Uid = Guid.NewGuid().ToString("N"),
                MountId = mount.Id,
                IsDir = false,
                Name = fileName,
                Parent = null,
                MTime = fileInfo.LastWriteTimeUtc,
                Size = fileInfo.Length,
                TgMessageId = messageId,
                InTrash = 0,
                Ver = ver
            });

            Logger.Log($"FsPutFile: File '{fileName}' successfully added to database.");

            return Win32Api.FS_FILE_OK;
        }
        catch (Exception ex)
        {
            Logger.Log($"FsPutFile error: {ex}");
            return Win32Api.FS_FILE_WRITEERROR;
        }
    }

    // Загрузка файла в Telegram (ANSI)
    [UnmanagedCallersOnly(EntryPoint = "FsPutFile", CallConvs = [typeof(CallConvStdcall)])]
    public static int FsPutFile(byte* localName, byte* remoteName, int copyFlags)
    {
        string localPath = Marshal.PtrToStringAnsi((IntPtr)localName) ?? "";
        string remotePath = Marshal.PtrToStringAnsi((IntPtr)remoteName) ?? "";
        return HandlePutFile(localPath, remotePath, copyFlags);
    }

    // Загрузка файла в Telegram (Unicode)
    [UnmanagedCallersOnly(EntryPoint = "FsPutFileW", CallConvs = [typeof(CallConvStdcall)])]
    public static int FsPutFileW(char* localName, char* remoteName, int copyFlags)
    {
        string localPath = Marshal.PtrToStringUni((IntPtr)localName) ?? "";
        string remotePath = Marshal.PtrToStringUni((IntPtr)remoteName) ?? "";
        return HandlePutFile(localPath, remotePath, copyFlags);
    }

    private static int HandleGetFile(string remotePath, string localPath, int copyFlags, Win32Api.RemoteInfoStruct* ri)
    {
        Logger.Log($"FsGetFile called: Remote='{remotePath}', Local='{localPath}', Flags={copyFlags}");

        if (_db == null)
        {
            Logger.Log("FsGetFile: Database is not initialized.");
            return Win32Api.FS_FILE_READERROR;
        }

        string cleanRemote = remotePath.TrimStart('\\', '/');
        int firstSlash = cleanRemote.IndexOfAny(new[] { '\\', '/' });
        if (firstSlash <= 0)
        {
            Logger.Log($"FsGetFile: Invalid remote path or root directory: '{remotePath}'");
            return Win32Api.FS_FILE_NOTSUPPORTED;
        }

        string channelFolderName = cleanRemote.Substring(0, firstSlash);
        string subPath = cleanRemote.Substring(firstSlash + 1).Replace('/', '\\').TrimStart('\\');
        string fileName = Path.GetFileName(subPath);

        // Игнорируем служебные элементы
        if (fileName == "[+] Создать папку" || fileName == "[ Login required.txt ]")
        {
            return Win32Api.FS_FILE_NOTSUPPORTED;
        }

        var mount = _db.GetMountByName(channelFolderName);
        if (mount == null)
        {
            Logger.Log($"FsGetFile: Mount '{channelFolderName}' not found.");
            return Win32Api.FS_FILE_NOTFOUND;
        }

        // Поиск файла в базе данных
        var fileRecord = _db.GetFile(mount.Id, fileName, parent: null);

        if (fileRecord == null || fileRecord.IsDir || fileRecord.TgMessageId <= 0)
        {
            Logger.Log($"FsGetFile: File '{fileName}' not found in DB or has no Telegram Message ID.");
            return Win32Api.FS_FILE_NOTFOUND;
        }

        try
        {
            // Проверка существующего локального файла
            if (File.Exists(localPath))
            {
                var existingInfo = new FileInfo(localPath);
                bool sameSize = existingInfo.Length == fileRecord.Size;
                bool sameTime = Math.Abs((existingInfo.LastWriteTimeUtc - fileRecord.MTime).TotalSeconds) < 2;

                if (sameSize && sameTime)
                {
                    Logger.Log($"FsGetFile: Local file '{localPath}' already exists with identical size ({fileRecord.Size}) and mtime. Skipping download.");
                    return Win32Api.FS_FILE_OK;
                }

                bool canOverwrite = (copyFlags & Win32Api.FS_COPYFLAGS_OVERWRITE) != 0;
                bool canResume = (copyFlags & Win32Api.FS_COPYFLAGS_RESUME) != 0;

                if (!canOverwrite && !canResume)
                {
                    Logger.Log($"FsGetFile: Target file '{localPath}' exists but is different and OVERWRITE flag not set.");
                    return Win32Api.FS_FILE_EXISTS;
                }
            }

            Logger.Log($"FsGetFile: Starting download of '{fileName}' (TgMessageId: {fileRecord.TgMessageId}, Size: {fileRecord.Size} bytes)...");

            int currentPercent = 0;
            using var cts = new System.Threading.CancellationTokenSource();

            // Запускаем асинхронное скачивание в пуле потоков
            var downloadTask = System.Threading.Tasks.Task.Run(async () =>
            {
                await TelegramManager.DownloadFileAsync(
                    mount.ChannelId,
                    fileRecord.TgMessageId,
                    localPath,
                    onProgress: (progress, total) =>
                    {
                        int pct = total > 0 ? (int)((progress * 100) / total) : 0;
                        if (pct > 100) pct = 100;
                        System.Threading.Interlocked.Exchange(ref currentPercent, pct);
                        return cts.IsCancellationRequested;
                    },
                    cancellationToken: cts.Token
                );
            });

            bool userAborted = false;

            // Начальное отображение прогресса для инициализации окна Total Commander
            if (ReportProgress(remotePath, localPath, 0))
            {
                userAborted = true;
                cts.Cancel();
            }

            // Активный цикл ожидания в вызывающем потоке Total Commander:
            // Каждые 50 мс вызываем ReportProgress прямо из потока Total Commander,
            // чтобы окно TC не зависало и мгновенно реагировало на клики по "Отмена", "Пауза" и крестику [X]
            while (!downloadTask.IsCompleted && !userAborted)
            {
                bool finished = downloadTask.Wait(50);
                if (finished) break;

                int pct = System.Threading.Volatile.Read(ref currentPercent);
                if (ReportProgress(remotePath, localPath, pct))
                {
                    userAborted = true;
                    cts.Cancel();
                    break;
                }
            }

            if (userAborted)
            {
                try
                {
                    // Даем задаче короткое время на корректное закрытие FileStream
                    downloadTask.Wait(1500);
                }
                catch { }

                Logger.Log($"FsGetFile: Download cancelled by user.");
                return Win32Api.FS_FILE_USERABORT;
            }

            // Если задача завершилась с ошибкой, распаковываем исключение
            if (downloadTask.IsFaulted)
            {
                var baseEx = downloadTask.Exception?.GetBaseException() ?? downloadTask.Exception!;
                throw baseEx;
            }

            // Финальный рапорт 100%
            ReportProgress(remotePath, localPath, 100);

            // Восстанавливаем точную дату изменения файла в соответствии с Telegram
            if (File.Exists(localPath))
            {
                try
                {
                    File.SetLastWriteTimeUtc(localPath, fileRecord.MTime);
                }
                catch (Exception ex)
                {
                    Logger.Log($"Warning: Failed to set LastWriteTimeUtc on '{localPath}': {ex.Message}");
                }
            }

            Logger.Log($"FsGetFile: Successfully downloaded '{fileName}' -> '{localPath}'.");
            return Win32Api.FS_FILE_OK;
        }
        catch (OperationCanceledException)
        {
            Logger.Log($"FsGetFile: Download cancelled by user (OperationCanceledException).");
            return Win32Api.FS_FILE_USERABORT;
        }
        catch (FileNotFoundException ex)
        {
            Logger.Log($"FsGetFile FileNotFound: {ex.Message}");
            return Win32Api.FS_FILE_NOTFOUND;
        }
        catch (Exception ex)
        {
            Logger.Log($"FsGetFile error: {ex}");
            return Win32Api.FS_FILE_READERROR;
        }
    }

    // Скачивание файла из Telegram (ANSI)
    [UnmanagedCallersOnly(EntryPoint = "FsGetFile", CallConvs = [typeof(CallConvStdcall)])]
    public static int FsGetFile(byte* remoteName, byte* localName, int copyFlags, Win32Api.RemoteInfoStruct* ri)
    {
        string remotePath = Marshal.PtrToStringAnsi((IntPtr)remoteName) ?? "";
        string localPath = Marshal.PtrToStringAnsi((IntPtr)localName) ?? "";
        return HandleGetFile(remotePath, localPath, copyFlags, ri);
    }

    // Скачивание файла из Telegram (Unicode)
    [UnmanagedCallersOnly(EntryPoint = "FsGetFileW", CallConvs = [typeof(CallConvStdcall)])]
    public static int FsGetFileW(char* remoteName, char* localName, int copyFlags, Win32Api.RemoteInfoStruct* ri)
    {
        string remotePath = Marshal.PtrToStringUni((IntPtr)remoteName) ?? "";
        string localPath = Marshal.PtrToStringUni((IntPtr)localName) ?? "";
        return HandleGetFile(remotePath, localPath, copyFlags, ri);
    }
}
