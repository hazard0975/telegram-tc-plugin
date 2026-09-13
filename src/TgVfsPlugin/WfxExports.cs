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
        long fileTime = time.ToFileTime();
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

        if (_db == null)
        {
            Logger.Log("Error: Database is null.");
            return null;
        }

        pathStr = pathStr.TrimEnd('\\', '/');
        
        var state = new FindState();
        try
        {
            if (string.IsNullOrEmpty(pathStr) || pathStr == "\\" || pathStr == "/")
            {
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
                string channelTitle = pathStr.TrimStart('\\', '/');
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
        if (state == null || state.Items.Count == 0) return new IntPtr(Win32Api.INVALID_HANDLE_VALUE);

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
        return 0; // false (больше нет файлов)
    }

    // Обязательная функция: начало поиска файлов (Unicode)
    [UnmanagedCallersOnly(EntryPoint = "FsFindFirstW", CallConvs = [typeof(CallConvStdcall)])]
    public static IntPtr FsFindFirstW(char* path, IntPtr findFileData)
    {
        string pathStr = Marshal.PtrToStringUni((IntPtr)path) ?? "";

        var state = CreateStateForPath(pathStr);
        if (state == null || state.Items.Count == 0) return new IntPtr(Win32Api.INVALID_HANDLE_VALUE);

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

    [UnmanagedCallersOnly(EntryPoint = "FsSetDirectory", CallConvs = [typeof(CallConvStdcall)])]
    public static int FsSetDirectory(IntPtr RemoteName, int OpMode)
    {
        string pathStr = Marshal.PtrToStringAnsi(RemoteName) ?? "";
        return HandleSetDirectory(pathStr) ? 1 : 0;
    }

    [UnmanagedCallersOnly(EntryPoint = "FsSetDirectoryW", CallConvs = [typeof(CallConvStdcall)])]
    public static int FsSetDirectoryW(IntPtr RemoteName, int OpMode)
    {
        string pathStr = Marshal.PtrToStringUni(RemoteName) ?? "";
        return HandleSetDirectory(pathStr) ? 1 : 0;
    }

    private static bool HandleSetDirectory(string pathStr)
    {
        Logger.Log($"FsSetDirectory called for: {pathStr}");
        if (_db == null) return false;
        
        pathStr = pathStr.TrimEnd('\\', '/');
        
        if (string.IsNullOrEmpty(pathStr))
        {
            return true; // Корень всегда разрешен
        }
        
        // Получаем имя канала
        string channelTitle = pathStr.TrimStart('\\', '/');
        var mount = _db.GetMountByName(channelTitle);
        if (mount != null)
        {
            // Если это зеркало и есть локальный путь, пытаемся открыть его во второй панели
            if (mount.Mode == 0 && !string.IsNullOrEmpty(mount.LocalPath))
            {
                Logger.Log($"Entering Mirror folder. Sending CD to {mount.LocalPath}");
                Win32Api.ChangeInactivePanelDir(mount.LocalPath);
            }
            return true; // Успешный вход в папку
        }

        // Если не нашли mount - разрешаем вход, если есть такие папки внутри (вложенные папки)
        return true;
    }
}
