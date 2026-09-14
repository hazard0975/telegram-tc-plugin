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
    private static bool _isUnicode = true;
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
                // Корень: возвращаем каналы и служебные триггеры
                Logger.Log("Fetching channels for root.");
                state.Items.Add(new VfsDatabase.VfsItem 
                { 
                    Name = "[+] Создать папку", 
                    IsDirectory = false, 
                    Size = 0,
                    Date = DateTime.Now 
                });
                state.Items.Add(new VfsDatabase.VfsItem 
                { 
                    Name = "[*] Настройки плагина", 
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

    // Обязательная функция: инициализация плагина (ANSI)
    [UnmanagedCallersOnly(EntryPoint = "FsInit", CallConvs = [typeof(CallConvStdcall)])]
    public static int FsInit(int pluginNumber, IntPtr pProgressProc, IntPtr pLogProc, IntPtr pRequestProc)
    {
        _isUnicode = false;
        return HandleInit(pluginNumber, pProgressProc, pLogProc, pRequestProc);
    }

    // Обязательная функция: инициализация плагина (Unicode)
    [UnmanagedCallersOnly(EntryPoint = "FsInitW", CallConvs = [typeof(CallConvStdcall)])]
    public static int FsInitW(int pluginNumber, IntPtr pProgressProc, IntPtr pLogProc, IntPtr pRequestProc)
    {
        _isUnicode = true;
        return HandleInit(pluginNumber, pProgressProc, pLogProc, pRequestProc);
    }

    private static int HandleInit(int pluginNumber, IntPtr pProgressProc, IntPtr pLogProc, IntPtr pRequestProc)
    {
        try
        {
            string initMode = _isUnicode ? "FsInitW (Unicode)" : "FsInit (ANSI)";
            Logger.Log($"Plugin initialization: {initMode}, PluginNumber={pluginNumber}");
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
            Logger.Log($"Critical Exception in HandleInit: {ex}");
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

        if (path.EndsWith("[*] Настройки плагина"))
        {
            System.Threading.Tasks.Task.Run(() => 
            {
                try
                {
                    string oldDir = SettingsManager.DataDirectory;
                    var result = SettingsDialog.Show();
                    if (result != null)
                    {
                        if (result.StorageLocationChanged)
                        {
                            Logger.Log($"Storage location change requested. New mode: {result.SelectedStorageMode}, CustomPath: '{result.CustomPath}'");

                            // 1. Закрываем и сбрасываем текущие ресурсы базы данных и TelegramClient
                            try
                            {
                                _db?.Dispose();
                            }
                            catch (Exception dbEx)
                            {
                                Logger.Log($"Error disposing DB: {dbEx.Message}");
                            }
                            finally
                            {
                                _db = null;
                                // Очищаем пулы подключений SQLite, чтобы освободить дескрипторы файлов
                                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                            }

                            TelegramManager.ResetClient();

                            string newDir = result.SelectedStorageMode switch
                            {
                                StorageMode.Portable => SettingsManager.PortableDirectory,
                                StorageMode.Custom => result.CustomPath,
                                _ => SettingsManager.DefaultAppDataDirectory
                            };

                            // 2. Если выбран перенос файлов - перемещаем файлы
                            if (result.MigrateExistingFiles)
                            {
                                SettingsManager.MigrateDataFiles(oldDir, newDir);
                            }

                            // 3. Сохраняем новые настройки
                            SettingsManager.SetStorageLocation(result.SelectedStorageMode, result.CustomPath);

                            // 4. Переинициализируем базу данных по новому пути
                            try
                            {
                                _db = new VfsDatabase();
                                Logger.Log("VfsDatabase re-initialized at new location.");
                            }
                            catch (Exception ex)
                            {
                                Logger.Log($"Failed to re-initialize DB: {ex.Message}");
                            }

                            // 5. Оповещаем и обновляем список папок в Total Commander
                            Logger.Log("Settings applied. Requesting panel refresh.");
                            Win32Api.RefreshActivePanel();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"Settings dialog execution error: {ex}");
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

    private static int ReportProgress(string sourceName, string targetName, int percentDone)
    {
        if (_progressProcDelegate == null && _pProgressProc != IntPtr.Zero)
        {
            try
            {
                _progressProcDelegate = Marshal.GetDelegateForFunctionPointer<Win32Api.ProgressProc>(_pProgressProc);
            }
            catch { }
        }

        if (_progressProcDelegate == null) return 0;

        try
        {
            IntPtr pSrc = _isUnicode ? Marshal.StringToHGlobalUni(sourceName) : Marshal.StringToHGlobalAnsi(sourceName);
            IntPtr pDst = _isUnicode ? Marshal.StringToHGlobalUni(targetName) : Marshal.StringToHGlobalAnsi(targetName);
            try
            {
                int res = _progressProcDelegate(_pluginNumber, pSrc, pDst, percentDone);
                if (res != 0 && res != 1)
                {
                    Logger.Log($"ProgressProc returned non-standard code: {res} (percentDone={percentDone})");
                }
                return res;
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
            return 0;
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

            long currentPercent = 0;
            bool userAborted = false;
            int messageId = 0;

            // Начальное отображение прогресса для инициализации окна Total Commander
            int initialRes = ReportProgress(localPath, remotePath, 0);
            if (initialRes == 1)
            {
                userAborted = true;
            }

            if (!userAborted)
            {
                using var cts = new System.Threading.CancellationTokenSource();
                using var pauseGate = new System.Threading.ManualResetEventSlim(true);
                long lastReportTime = Environment.TickCount64;

                var uploadTask = System.Threading.Tasks.Task.Run(() =>
                    TelegramManager.UploadAndSendFileAsync(
                        mount.ChannelId,
                        localPath,
                        fileName,
                        subPath,
                        onProgress: (sent, total) =>
                        {
                            int pct = total > 0 ? (int)((sent * 100) / total) : 0;
                            if (pct > 100) pct = 100;
                            System.Threading.Interlocked.Exchange(ref currentPercent, pct);
                            return cts.IsCancellationRequested;
                        },
                        cancellationToken: cts.Token,
                        pauseGate: pauseGate
                    )
                );

                // Watchdog task to detect if ReportProgress is blocking (TC is paused)
                using var watchdogCts = new System.Threading.CancellationTokenSource();
                var watchdogTask = System.Threading.Tasks.Task.Run(() =>
                {
                    while (!uploadTask.IsCompleted && !watchdogCts.IsCancellationRequested && !cts.IsCancellationRequested)
                    {
                        long elapsed = Environment.TickCount64 - System.Threading.Interlocked.Read(ref lastReportTime);
                        if (elapsed > 500)
                        {
                            Logger.Log($"Upload paused (TC blocking ReportProgress detected). Pausing stream.");
                            pauseGate.Reset();
                        }
                        System.Threading.Thread.Sleep(100);
                    }
                });

                int pauseCheckCounter = 0;

                while (!uploadTask.IsCompleted && !userAborted)
                {
                    bool finished = uploadTask.Wait(50);
                    if (finished) break;

                    int pct = (int)System.Threading.Interlocked.Read(ref currentPercent);

                    System.Threading.Interlocked.Exchange(ref lastReportTime, Environment.TickCount64);
                    int progressRes = ReportProgress(localPath, remotePath, pct);
                    System.Threading.Interlocked.Exchange(ref lastReportTime, Environment.TickCount64);

                    if (progressRes == 1)
                    {
                        userAborted = true;
                        try { cts.Cancel(); } catch { }
                        pauseGate.Set(); // unblock stream if waiting
                        break;
                    }

                    bool tcPaused = (progressRes != 0 && progressRes != 1);
                    pauseCheckCounter++;
                    if (!tcPaused && pauseCheckCounter % 4 == 0)
                    {
                        tcPaused = Win32Api.IsTotalCommanderPaused();
                    }

                    if (tcPaused)
                    {
                        Logger.Log($"Upload paused (TC pause detected, progress={pct}%). Pausing stream.");
                        pauseGate.Reset();

                        while (!userAborted)
                        {
                            int pausedPct = (int)System.Threading.Interlocked.Read(ref currentPercent);
                            System.Threading.Interlocked.Exchange(ref lastReportTime, Environment.TickCount64);
                            int pauseRes = ReportProgress(localPath, remotePath, pausedPct);
                            System.Threading.Interlocked.Exchange(ref lastReportTime, Environment.TickCount64);

                            if (pauseRes == 1)
                            {
                                userAborted = true;
                                try { cts.Cancel(); } catch { }
                                pauseGate.Set();
                                break;
                            }

                            bool stillPaused = (pauseRes != 0 && pauseRes != 1) || Win32Api.IsTotalCommanderPaused();
                            if (!stillPaused)
                            {
                                Logger.Log("Upload resumed by TC. Resuming stream.");
                                pauseGate.Set();
                                break;
                            }
                            System.Threading.Thread.Sleep(100);
                        }
                    }
                    else
                    {
                        // Если TC не на паузе, но шлюз был сброшен сторожевым таймером во время блокировки ReportProgress — открываем шлюз
                        if (!pauseGate.IsSet)
                        {
                            Logger.Log("Upload unblocked / resumed. Setting pauseGate.");
                            pauseGate.Set();
                        }
                    }
                }

                try { watchdogCts.Cancel(); } catch { }
                try { watchdogTask.Wait(500); } catch { }

                // Гарантируем, что шлюз открыт, чтобы фоновый таск не завис перед завершением
                pauseGate.Set();

                try
                {
                    messageId = uploadTask.GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    // Загрузка была отменена пользователем
                }
                catch (Exception ex) when (ex.InnerException is OperationCanceledException)
                {
                    // Аналогично
                }
                catch (Exception ex)
                {
                    if (!userAborted)
                    {
                        Logger.Log($"FsPutFile error: {ex}");
                        return Win32Api.FS_FILE_WRITEERROR;
                    }
                }
            }

            if (userAborted)
            {
                Logger.Log($"FsPutFile: Upload was cancelled by user.");
                return Win32Api.FS_FILE_USERABORT;
            }

            if (messageId <= 0)
            {
                Logger.Log($"FsPutFile: Upload failed (no message ID).");
                return Win32Api.FS_FILE_WRITEERROR;
            }

            // Финальный рапорт 100%
            ReportProgress(localPath, remotePath, 100);

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
        if (fileName == "[+] Создать папку" || fileName == "[*] Настройки плагина" || fileName == "[ Login required.txt ]")
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

            long currentPercent = 0;
            bool userAborted = false;
            bool isFinished = false;

            // Начальное отображение прогресса для инициализации окна Total Commander
            int initialRes = ReportProgress(remotePath, localPath, 0);
            if (initialRes == 1)
            {
                userAborted = true;
            }

            while (!isFinished && !userAborted)
            {
                using var cts = new System.Threading.CancellationTokenSource();
                long lastReportTime = Environment.TickCount64;

                // Запускаем асинхронное скачивание в пуле потоков
                var downloadTask = System.Threading.Tasks.Task.Run(() =>
                    TelegramManager.DownloadFileAsync(
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
                        // pauseGate больше не передаем
                    )
                );

                // Watchdog task to detect if ReportProgress is blocking (TC is paused)
                using var watchdogCts = new System.Threading.CancellationTokenSource();
                var watchdogTask = System.Threading.Tasks.Task.Run(() =>
                {
                    while (!downloadTask.IsCompleted && !watchdogCts.IsCancellationRequested && !cts.IsCancellationRequested)
                    {
                        long elapsed = Environment.TickCount64 - System.Threading.Interlocked.Read(ref lastReportTime);
                        if (elapsed > 500)
                        {
                            // ReportProgress is blocking for more than 500ms! TC must be paused.
                            Logger.Log($"Download paused (TC blocking ReportProgress detected). Cancelling network task.");
                            try { cts.Cancel(); } catch { }
                            break;
                        }
                        System.Threading.Thread.Sleep(100);
                    }
                });

                int pauseCheckCounter = 0;

                // Активный цикл ожидания в вызывающем потоке Total Commander:
                while (!downloadTask.IsCompleted && !userAborted)
                {
                    bool finished = downloadTask.Wait(50);
                    if (finished) break;

                    int pct = (int)System.Threading.Interlocked.Read(ref currentPercent);

                    System.Threading.Interlocked.Exchange(ref lastReportTime, Environment.TickCount64);
                    int progressRes = ReportProgress(remotePath, localPath, pct);
                    System.Threading.Interlocked.Exchange(ref lastReportTime, Environment.TickCount64);

                    if (progressRes == 1)
                    {
                        userAborted = true;
                        try { cts.Cancel(); } catch { }
                        break;
                    }

                    // Проверяем состояние паузы
                    bool tcPaused = (progressRes != 0 && progressRes != 1);
                    pauseCheckCounter++;
                    if (!tcPaused && pauseCheckCounter % 4 == 0)
                    {
                        tcPaused = Win32Api.IsTotalCommanderPaused();
                    }

                    if (tcPaused)
                    {
                        // Пользователь нажал "Пауза"
                        Logger.Log($"Download paused (TC pause detected, progress={pct}%). Cancelling network task.");
                        try { cts.Cancel(); } catch { }
                        break;
                    }
                }

                try { watchdogCts.Cancel(); } catch { }
                try { watchdogTask.Wait(500); } catch { }

                try
                {
                    downloadTask.GetAwaiter().GetResult();
                    if (!cts.IsCancellationRequested && !userAborted)
                    {
                        isFinished = true; // Загрузка успешно завершена
                    }
                }
                catch (OperationCanceledException)
                {
                    // Загрузка была отменена (из-за паузы или прерывания пользователем)
                }
                catch (Exception ex) when (ex.InnerException is OperationCanceledException)
                {
                    // Аналогично
                }
                catch (Exception ex)
                {
                    if (!userAborted)
                    {
                        Logger.Log($"FsGetFile error: {ex}");
                        return Win32Api.FS_FILE_READERROR;
                    }
                }

                if (userAborted) break;

                if (!isFinished)
                {
                    // Мы на паузе. Ждем, пока пользователь не отожмет паузу
                    Logger.Log("Download is paused. Waiting for resume signal from TC...");
                    while (!userAborted)
                    {
                        int pct = (int)System.Threading.Interlocked.Read(ref currentPercent);
                        
                        System.Threading.Interlocked.Exchange(ref lastReportTime, Environment.TickCount64);
                        int progressRes = ReportProgress(remotePath, localPath, pct);
                        System.Threading.Interlocked.Exchange(ref lastReportTime, Environment.TickCount64);

                        if (progressRes == 1)
                        {
                            userAborted = true;
                            break;
                        }

                        bool tcPaused = (progressRes != 0 && progressRes != 1) || Win32Api.IsTotalCommanderPaused();
                        if (!tcPaused)
                        {
                            Logger.Log("Download resumed by TC. Restarting network task.");
                            break; // Выходим из цикла ожидания паузы, внешний цикл перезапустит скачивание (докачку)
                        }
                        System.Threading.Thread.Sleep(100);
                    }
                }
            }

            if (userAborted)
            {
                Logger.Log($"FsGetFile: Download cancelled by user.");
                return Win32Api.FS_FILE_USERABORT;
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

    // Оповещение о начале/завершении операций плагина (ANSI)
    [UnmanagedCallersOnly(EntryPoint = "FsStatusInfo", CallConvs = [typeof(CallConvStdcall)])]
    public static void FsStatusInfo(byte* remoteDir, int infoStartEnd, int infoOperation)
    {
        string dir = Marshal.PtrToStringAnsi((IntPtr)remoteDir) ?? "";
        HandleStatusInfo(dir, infoStartEnd, infoOperation);
    }

    // Оповещение о начале/завершении операций плагина (Unicode)
    [UnmanagedCallersOnly(EntryPoint = "FsStatusInfoW", CallConvs = [typeof(CallConvStdcall)])]
    public static void FsStatusInfoW(char* remoteDir, int infoStartEnd, int infoOperation)
    {
        string dir = Marshal.PtrToStringUni((IntPtr)remoteDir) ?? "";
        HandleStatusInfo(dir, infoStartEnd, infoOperation);
    }

    private static void HandleStatusInfo(string remoteDir, int infoStartEnd, int infoOperation)
    {
        string startEndStr = infoStartEnd == Win32Api.FS_STATUS_START ? "START" : "END";
        Logger.Log($"FsStatusInfo: {startEndStr} for Dir='{remoteDir}', Operation={infoOperation}");
    }

    // Поддержка фонового копирования и очереди в Total Commander (ANSI)
    [UnmanagedCallersOnly(EntryPoint = "FsGetBackgroundFlags", CallConvs = [typeof(CallConvStdcall)])]
    public static int FsGetBackgroundFlags()
    {
        Logger.Log("FsGetBackgroundFlags called -> Returning BG_DOWNLOAD | BG_UPLOAD | BG_ASK_USER (7)");
        return Win32Api.BG_DOWNLOAD | Win32Api.BG_UPLOAD | Win32Api.BG_ASK_USER;
    }

    // Поддержка фонового копирования и очереди в Total Commander (Unicode)
    [UnmanagedCallersOnly(EntryPoint = "FsGetBackgroundFlagsW", CallConvs = [typeof(CallConvStdcall)])]
    public static int FsGetBackgroundFlagsW()
    {
        Logger.Log("FsGetBackgroundFlagsW called -> Returning BG_DOWNLOAD | BG_UPLOAD | BG_ASK_USER (7)");
        return Win32Api.BG_DOWNLOAD | Win32Api.BG_UPLOAD | Win32Api.BG_ASK_USER;
    }
}
