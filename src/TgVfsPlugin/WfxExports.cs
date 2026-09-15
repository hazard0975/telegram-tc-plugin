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

    private static bool _isBatchOperation = false;
    private static System.Threading.Timer? _debouncedCheckpointTimer;
    private static bool _processExitHooked = false;

    private static void OnProcessExit(object? sender, EventArgs e)
    {
        try
        {
            Logger.Log("ProcessExit event triggered. Performing final WAL checkpoint (TRUNCATE) and clearing connection pools.");
            if (_db != null)
            {
                _db.Checkpoint(truncate: true);
                _db.Dispose();
                _db = null;
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
        catch (Exception ex)
        {
            Logger.Log($"Error during ProcessExit shutdown: {ex.Message}");
        }
    }

    /// <summary>
    /// Инициирует фоновый или отложенный сброс WAL-журнала в основную базу данных.
    /// Во время пакетной операции (загрузка списка файлов) одиночные сбросы пропускаются,
    /// а единственный сброс произойдет по сигналу FS_STATUS_END от Total Commander.
    /// </summary>
    public static void TriggerCheckpoint(bool immediate = false)
    {
        if (_db == null) return;

        if (_isBatchOperation && !immediate)
        {
            // Идет пакетная передача файлов — сбросим 1 раз по завершении списка (FS_STATUS_END)
            return;
        }

        if (immediate)
        {
            _debouncedCheckpointTimer?.Dispose();
            _debouncedCheckpointTimer = null;
            System.Threading.Tasks.Task.Run(() =>
            {
                _db?.Checkpoint(truncate: false);
            });
        }
        else
        {
            // Отложенный сброс (1.5 сек debounce для одиночных операций)
            _debouncedCheckpointTimer?.Dispose();
            _debouncedCheckpointTimer = new System.Threading.Timer(_ =>
            {
                _db?.Checkpoint(truncate: false);
            }, null, 1500, System.Threading.Timeout.Infinite);
        }
    }

    // Вспомогательный метод для конвертации DateTime в FILETIME
    private static Win32Api.FILETIME DateTimeToFileTime(DateTime time)
    {
        // Total Commander в WIN32_FIND_DATA ожидает FILETIME (в формате UTC),
        // после чего сам нативно переводит его в локальное системное время пользователя.
        DateTime utcTime = (time.Kind == DateTimeKind.Utc) 
            ? time 
            : time.ToUniversalTime();

        long fileTime = utcTime.ToFileTimeUtc();
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

    public static string NormalizeVfsPath(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return "";
        string clean = rawPath.TrimStart('\\', '/').TrimEnd('\\', '/');
        if (string.IsNullOrEmpty(clean)) return "";

        string[] parts = clean.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "";

        if (_db != null)
        {
            if (parts.Length > 1)
            {
                var firstMount = _db.GetMountByName(parts[0]);
                if (firstMount == null)
                {
                    var secondMount = _db.GetMountByName(parts[1]);
                    if (secondMount != null)
                    {
                        // Первый элемент был именем виртуального тома плагина (например \\\tgvfsplugin\)
                        return string.Join("\\", parts.Skip(1));
                    }
                }
            }
            else if (parts.Length == 1)
            {
                if (parts[0].Equals("tgvfsplugin", StringComparison.OrdinalIgnoreCase) ||
                    parts[0].Equals("wfx_tgvfsplugin", StringComparison.OrdinalIgnoreCase))
                {
                    return "";
                }
            }
        }

        return string.Join("\\", parts);
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

        string cleanPath = NormalizeVfsPath(dirPath);
        Logger.Log($"Parsed directory path for search: '{cleanPath}'");
        
        var state = new FindState();
        try
        {
            if (string.IsNullOrEmpty(cleanPath))
            {
                _lastMirrorPath = null;
                // Корень: возвращаем каналы и служебные триггеры
                Logger.Log("Fetching channels for root.");
                state.Items.Add(new VfsDatabase.VfsItem 
                { 
                    Name = "[📁+] Создать папку", 
                    IsDirectory = false, 
                    Size = 0,
                    Date = DateTime.Now 
                });
                state.Items.Add(new VfsDatabase.VfsItem 
                { 
                    Name = "[❌] Удалить папку", 
                    IsDirectory = false, 
                    Size = 0,
                    Date = DateTime.Now 
                });
                state.Items.Add(new VfsDatabase.VfsItem 
                { 
                    Name = "[⚙] Настройки", 
                    IsDirectory = false, 
                    Size = 0,
                    Date = DateTime.Now 
                });
                state.Items.AddRange(_db.GetMounts());
            }
            else
            {
                // Внутри канала (наш путь начинается с названия канала, например "Work Chat")
                string[] parts = cleanPath.Split('\\');
                string channelTitle = parts[0];
                string? parentSubPath = null;
                if (parts.Length > 1)
                {
                    parentSubPath = string.Join("\\", parts, 1, parts.Length - 1);
                }
                
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
                
                Logger.Log($"Fetching files for channel: '{channelTitle}', parent: '{parentSubPath}'");
                state.Items = _db.GetFiles(channelTitle, parentSubPath);
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

            if (!_processExitHooked)
            {
                _processExitHooked = true;
                AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
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

        if (path.EndsWith("[📁+] Создать папку") || path.EndsWith("[+] Создать папку"))
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
                        TriggerCheckpoint(immediate: true);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"Create folder error: {ex}");
                }
            }).GetAwaiter().GetResult();
            
            return 0; // FS_EXEC_OK
        }

        if (path.EndsWith("[❌] Удалить папку") || path.EndsWith("[-] Удалить папку"))
        {
            System.Threading.Tasks.Task.Run(() => 
            {
                try
                {
                    var mounts = _db?.GetMounts() ?? new List<VfsDatabase.VfsItem>();
                    var folderNames = mounts.Select(m => m.Name).ToList();
                    string? selectedFolder = DeleteFolderDialog.Show(folderNames);
                    if (!string.IsNullOrEmpty(selectedFolder))
                    {
                        var mount = _db?.GetMountByName(selectedFolder);
                        if (mount != null)
                        {
                            var confirm = System.Windows.Forms.MessageBox.Show(
                                $"Вы действительно хотите удалить виртуальную папку '{selectedFolder}'?\n\n" +
                                $"⚠️ ВНИМАНИЕ: Это приведёт к удалению связанного канала и всех хранящихся в нём файлов в Telegram!",
                                "Подтверждение удаления папки",
                                System.Windows.Forms.MessageBoxButtons.YesNo,
                                System.Windows.Forms.MessageBoxIcon.Warning,
                                System.Windows.Forms.MessageBoxDefaultButton.Button2);

                            if (confirm == System.Windows.Forms.DialogResult.Yes)
                            {
                                if (mount.ChannelId != 0)
                                {
                                    TelegramManager.DeleteChannelAsync(mount.ChannelId).GetAwaiter().GetResult();
                                }

                                _db?.DeleteMount(mount.Id);
                                Logger.Log($"Folder '{selectedFolder}' and Telegram channel {mount.ChannelId} deleted.");

                                Win32Api.RefreshActivePanel();

                                System.Windows.Forms.MessageBox.Show(
                                    $"Папка '{selectedFolder}' и её канал в Telegram успешно удалены.",
                                    "Удаление завершено",
                                    System.Windows.Forms.MessageBoxButtons.OK,
                                    System.Windows.Forms.MessageBoxIcon.Information);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"Delete folder error: {ex}");
                }
            }).GetAwaiter().GetResult();

            return 0; // FS_EXEC_OK
        }

        if (path.EndsWith("[⚙] Настройки") || path.EndsWith("[*] Настройки плагина"))
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

                            try
                            {
                                System.Windows.Forms.MessageBox.Show(
                                    $"Настройки успешно применены!\n\nПапка данных:\n{newDir}",
                                    "Telegram VFS",
                                    System.Windows.Forms.MessageBoxButtons.OK,
                                    System.Windows.Forms.MessageBoxIcon.Information);
                            }
                            catch { }
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

        string cleanRemote = NormalizeVfsPath(remotePath);
        int firstSlash = cleanRemote.IndexOfAny(new[] { '\\', '/' });
        if (firstSlash <= 0)
        {
            Logger.Log($"FsPutFile: Destination is root or invalid. Cannot copy directly to root: '{remotePath}' (cleanRemote='{cleanRemote}')");
            return Win32Api.FS_FILE_NOTSUPPORTED;
        }

        string channelName = cleanRemote.Substring(0, firstSlash);
        string subPath = cleanRemote.Substring(firstSlash + 1).Replace('/', '\\');
        string fileName = System.IO.Path.GetFileName(subPath);
        string? parentSubPath = System.IO.Path.GetDirectoryName(subPath);
        if (string.IsNullOrEmpty(parentSubPath)) parentSubPath = null;

        var mount = _db.GetMountByName(channelName);
        if (mount == null)
        {
            Logger.Log($"FsPutFile: Channel '{channelName}' not found in database.");
            return Win32Api.FS_FILE_NOTFOUND;
        }

        _db.EnsureParentDirectoriesExist(mount.Id, parentSubPath);

        var existingFile = _db.GetFile(mount.Id, fileName, parent: parentSubPath);
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
                Parent = parentSubPath,
                MTime = fileInfo.LastWriteTimeUtc,
                Size = fileInfo.Length,
                TgMessageId = messageId,
                InTrash = 0,
                Ver = ver
            });

            Logger.Log($"FsPutFile: File '{fileName}' successfully added to database.");
            TriggerCheckpoint(immediate: false);

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

        string cleanRemote = NormalizeVfsPath(remotePath);
        int firstSlash = cleanRemote.IndexOfAny(new[] { '\\', '/' });
        if (firstSlash <= 0)
        {
            Logger.Log($"FsGetFile: Invalid remote path or root directory: '{remotePath}' (cleanRemote='{cleanRemote}')");
            return Win32Api.FS_FILE_NOTSUPPORTED;
        }

        string channelFolderName = cleanRemote.Substring(0, firstSlash);
        string subPath = cleanRemote.Substring(firstSlash + 1).Replace('/', '\\').TrimStart('\\');
        string fileName = Path.GetFileName(subPath);

        // Игнорируем служебные элементы
        if (fileName == "[📁+] Создать папку" || fileName == "[+] Создать папку" || 
            fileName == "[❌] Удалить папку" || fileName == "[⚙] Настройки" || 
            fileName == "[*] Настройки плагина" || fileName == "[ Login required.txt ]")
        {
            return Win32Api.FS_FILE_NOTSUPPORTED;
        }

        var mount = _db.GetMountByName(channelFolderName);
        if (mount == null)
        {
            Logger.Log($"FsGetFile: Mount '{channelFolderName}' not found.");
            return Win32Api.FS_FILE_NOTFOUND;
        }

        string? parentSubPath = Path.GetDirectoryName(subPath);
        if (string.IsNullOrEmpty(parentSubPath)) parentSubPath = null;

        // Поиск файла в базе данных
        var fileRecord = _db.GetFile(mount.Id, fileName, parent: parentSubPath);

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

        if (infoStartEnd == Win32Api.FS_STATUS_START)
        {
            _isBatchOperation = true;
        }
        else if (infoStartEnd == Win32Api.FS_STATUS_END)
        {
            _isBatchOperation = false;
            Logger.Log("FsStatusInfo: Batch operation completed. Executing WAL checkpoint.");
            TriggerCheckpoint(immediate: true);
        }
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

    // Создание каталога (ANSI) - Вызывается при нажатии F7 в Total Commander
    [UnmanagedCallersOnly(EntryPoint = "FsMkDir", CallConvs = [typeof(CallConvStdcall)])]
    public static int FsMkDir(byte* path)
    {
        string dirPath = Marshal.PtrToStringAnsi((IntPtr)path) ?? "";
        return HandleMkDir(dirPath);
    }

    // Создание каталога (Unicode) - Вызывается при нажатии F7 в Total Commander
    [UnmanagedCallersOnly(EntryPoint = "FsMkDirW", CallConvs = [typeof(CallConvStdcall)])]
    public static int FsMkDirW(char* path)
    {
        string dirPath = Marshal.PtrToStringUni((IntPtr)path) ?? "";
        return HandleMkDir(dirPath);
    }

    private static int HandleMkDir(string dirPath)
    {
        Logger.Log($"FsMkDir called for: '{dirPath}'");
        if (_db == null) return 0; // false

        string cleanPath = NormalizeVfsPath(dirPath);
        if (string.IsNullOrEmpty(cleanPath)) return 0;

        int firstSlash = cleanPath.IndexOfAny(new[] { '\\', '/' });
        if (firstSlash <= 0)
        {
            // Нельзя создать канал через FsMkDir без диалога
            return 0;
        }

        string channelName = cleanPath.Substring(0, firstSlash);
        string subPath = cleanPath.Substring(firstSlash + 1).Replace('/', '\\');

        var mount = _db.GetMountByName(channelName);
        if (mount == null) return 0;

        int lastSlash = subPath.LastIndexOf('\\');
        string dirName = lastSlash >= 0 ? subPath.Substring(lastSlash + 1) : subPath;
        string? parent = lastSlash >= 0 ? subPath.Substring(0, lastSlash) : null;

        _db.EnsureParentDirectoriesExist(mount.Id, parent);
        _db.AddDirectoryRecord(mount.Id, dirName, parent);
        
        Logger.Log($"FsMkDir: Directory '{dirName}' created in parent '{parent}' for channel '{channelName}'.");
        Win32Api.RefreshActivePanel();
        TriggerCheckpoint(immediate: true);

        return 1; // true (success)
    }

    // Удаление файла / объекта (ANSI) - Обязательный экспорт для F8 / Del в Total Commander
    [UnmanagedCallersOnly(EntryPoint = "FsDeleteFile", CallConvs = [typeof(CallConvStdcall)])]
    public static int FsDeleteFile(byte* remoteName)
    {
        string path = Marshal.PtrToStringAnsi((IntPtr)remoteName) ?? "";
        return HandleDeleteFile(path);
    }

    // Удаление файла / объекта (Unicode) - Обязательный экспорт для F8 / Del в Total Commander
    [UnmanagedCallersOnly(EntryPoint = "FsDeleteFileW", CallConvs = [typeof(CallConvStdcall)])]
    public static int FsDeleteFileW(char* remoteName)
    {
        string path = Marshal.PtrToStringUni((IntPtr)remoteName) ?? "";
        return HandleDeleteFile(path);
    }

    private static int HandleDeleteFile(string remotePath)
    {
        Logger.Log($"FsDeleteFile called for: '{remotePath}'");

        if (_db == null) return 0; // false

        string cleanPath = NormalizeVfsPath(remotePath);
        if (string.IsNullOrEmpty(cleanPath)) return 0;

        // Игнорируем и защищаем от удаления служебные триггеры
        if (cleanPath.Contains("[📁+] Создать папку") || cleanPath.Contains("[+] Создать папку") ||
            cleanPath.Contains("[❌] Удалить папку") || cleanPath.Contains("[-] Удалить папку") ||
            cleanPath.Contains("[⚙] Настройки") || cleanPath.Contains("[*] Настройки плагина"))
        {
            Logger.Log($"Protected trigger item, skipping deletion: '{cleanPath}'");
            return 0; // false
        }

        int firstSlash = cleanPath.IndexOfAny(new[] { '\\', '/' });
        if (firstSlash < 0)
        {
            // Это имя корневой папки монтирования (канала)
            return HandleRemoveDir(cleanPath);
        }
        else
        {
            // Это элемент внутри канала (например "Work Chat\ai-tour-optimization\doc.pdf" или "Work Chat\subfolder")
            string channelName = cleanPath.Substring(0, firstSlash);
            string subPath = cleanPath.Substring(firstSlash + 1).Replace('/', '\\');
            string fileName = Path.GetFileName(subPath);
            string? parentSubPath = Path.GetDirectoryName(subPath);
            if (string.IsNullOrEmpty(parentSubPath)) parentSubPath = null;

            var mount = _db.GetMountByName(channelName);
            if (mount != null)
            {
                var fileRecord = _db.GetFile(mount.Id, fileName, parent: parentSubPath);
                if (fileRecord != null)
                {
                    if (fileRecord.IsDir)
                    {
                        _db.MoveDirectoryToTrash(mount.Id, subPath);
                    }
                    else
                    {
                        _db.MoveFileToTrash(fileRecord.Uid);
                    }
                    Logger.Log($"Item '{subPath}' in channel '{channelName}' moved to trash via FsDeleteFile.");
                    Win32Api.RefreshActivePanel();
                    TriggerCheckpoint(immediate: true);
                    return 1; // true
                }
            }
        }

        return 0; // false
    }

    // Удаление каталога (ANSI) - Вызывается при нажатии F8 / Del в Total Commander
    [UnmanagedCallersOnly(EntryPoint = "FsRemoveDir", CallConvs = [typeof(CallConvStdcall)])]
    public static int FsRemoveDir(byte* remoteDir)
    {
        string dirPath = Marshal.PtrToStringAnsi((IntPtr)remoteDir) ?? "";
        return HandleRemoveDir(dirPath);
    }

    // Удаление каталога (Unicode) - Вызывается при нажатии F8 / Del в Total Commander
    [UnmanagedCallersOnly(EntryPoint = "FsRemoveDirW", CallConvs = [typeof(CallConvStdcall)])]
    public static int FsRemoveDirW(char* remoteDir)
    {
        string dirPath = Marshal.PtrToStringUni((IntPtr)remoteDir) ?? "";
        return HandleRemoveDir(dirPath);
    }

    private static int HandleRemoveDir(string dirPath)
    {
        Logger.Log($"FsRemoveDir called for: '{dirPath}'");

        if (_db == null) return 0; // false

        string cleanPath = NormalizeVfsPath(dirPath);
        if (string.IsNullOrEmpty(cleanPath)) return 0; // нельзя удалить корень

        // Защищаем служебные триггеры
        if (cleanPath.Contains("[📁+] Создать папку") || cleanPath.Contains("[+] Создать папку") ||
            cleanPath.Contains("[❌] Удалить папку") || cleanPath.Contains("[-] Удалить папку") ||
            cleanPath.Contains("[⚙] Настройки") || cleanPath.Contains("[*] Настройки плагина"))
        {
            return 0;
        }

        int firstSlash = cleanPath.IndexOfAny(new[] { '\\', '/' });
        if (firstSlash < 0)
        {
            // Это корневая папка монтирования (канал)
            string channelName = cleanPath;
            var mount = _db.GetMountByName(channelName);
            if (mount != null)
            {
                var dialogRes = System.Windows.Forms.MessageBox.Show(
                    $"Вы действительно хотите удалить виртуальную папку '{channelName}'?\n\n" +
                    $"⚠️ ВНИМАНИЕ: Это приведёт к удалению связанного канала и всех хранящихся в нём файлов в Telegram!",
                    "Подтверждение удаления папки",
                    System.Windows.Forms.MessageBoxButtons.YesNo,
                    System.Windows.Forms.MessageBoxIcon.Warning,
                    System.Windows.Forms.MessageBoxDefaultButton.Button2);

                if (dialogRes == System.Windows.Forms.DialogResult.Yes)
                {
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        if (mount.ChannelId != 0)
                        {
                            TelegramManager.DeleteChannelAsync(mount.ChannelId).GetAwaiter().GetResult();
                        }
                        _db.DeleteMount(mount.Id);
                        Logger.Log($"Mount '{channelName}' deleted via FsRemoveDir.");
                        Win32Api.RefreshActivePanel();
                        TriggerCheckpoint(immediate: true);
                    }).GetAwaiter().GetResult();

                    return 1; // true (успех)
                }
                return 0; // пользователь отменил
            }
        }
        else
        {
            // Это виртуальная подпапка внутри канала (например "Work Chat\ai-tour-optimization")
            string channelName = cleanPath.Substring(0, firstSlash);
            string subPath = cleanPath.Substring(firstSlash + 1).Replace('/', '\\');

            var mount = _db.GetMountByName(channelName);
            if (mount != null)
            {
                _db.MoveDirectoryToTrash(mount.Id, subPath);
                Logger.Log($"Virtual directory '{subPath}' in channel '{channelName}' moved to trash via FsRemoveDir.");
                Win32Api.RefreshActivePanel();
                TriggerCheckpoint(immediate: true);
                return 1; // true
            }
        }

        return 0;
    }

    // Переименование / перемещение / копирование файлов и папок (ANSI) - F5 / F6 в Total Commander
    [UnmanagedCallersOnly(EntryPoint = "FsRenMovFile", CallConvs = [typeof(CallConvStdcall)])]
    public static int FsRenMovFile(byte* oldName, byte* newName, int moveFlags, int overwriteFlags, Win32Api.RemoteInfoStruct* ri)
    {
        string oldPath = Marshal.PtrToStringAnsi((IntPtr)oldName) ?? "";
        string newPath = Marshal.PtrToStringAnsi((IntPtr)newName) ?? "";
        bool isMove = moveFlags != 0;
        bool overwrite = overwriteFlags != 0 || (moveFlags & Win32Api.FS_COPYFLAGS_OVERWRITE) != 0;
        return HandleRenMovFile(oldPath, newPath, isMove, overwrite);
    }

    // Переименование / перемещение / копирование файлов и папок (Unicode) - F5 / F6 в Total Commander
    [UnmanagedCallersOnly(EntryPoint = "FsRenMovFileW", CallConvs = [typeof(CallConvStdcall)])]
    public static int FsRenMovFileW(char* oldName, char* newName, int moveFlags, int overwriteFlags, Win32Api.RemoteInfoStruct* ri)
    {
        string oldPath = Marshal.PtrToStringUni((IntPtr)oldName) ?? "";
        string newPath = Marshal.PtrToStringUni((IntPtr)newName) ?? "";
        bool isMove = moveFlags != 0;
        bool overwrite = overwriteFlags != 0 || (moveFlags & Win32Api.FS_COPYFLAGS_OVERWRITE) != 0;
        return HandleRenMovFile(oldPath, newPath, isMove, overwrite);
    }

    private static int HandleRenMovFile(string oldPath, string newPath, bool isMove, bool overwrite)
    {
        Logger.Log($"FsRenMovFile called from '{oldPath}' to '{newPath}', isMove={isMove}, overwrite={overwrite}");
        if (_db == null) return Win32Api.FS_FILE_NOTFOUND;

        string cleanOld = NormalizeVfsPath(oldPath);
        string cleanNew = NormalizeVfsPath(newPath);
        if (string.IsNullOrEmpty(cleanOld) || string.IsNullOrEmpty(cleanNew))
            return Win32Api.FS_FILE_NOTFOUND;

        int oldFirstSlash = cleanOld.IndexOfAny(new[] { '\\', '/' });
        int newFirstSlash = cleanNew.IndexOfAny(new[] { '\\', '/' });

        if (oldFirstSlash <= 0 || newFirstSlash <= 0)
        {
            // Переименование/копирование корневого канала не поддерживается через FsRenMovFile
            return Win32Api.FS_FILE_NOTFOUND;
        }

        string oldChannel = cleanOld.Substring(0, oldFirstSlash);
        string oldSub = cleanOld.Substring(oldFirstSlash + 1).Replace('/', '\\');

        string newChannel = cleanNew.Substring(0, newFirstSlash);
        string newSub = cleanNew.Substring(newFirstSlash + 1).Replace('/', '\\');

        var oldMount = _db.GetMountByName(oldChannel);
        var newMount = _db.GetMountByName(newChannel);
        if (oldMount == null || newMount == null)
            return Win32Api.FS_FILE_NOTFOUND;

        int oldLastSlash = oldSub.LastIndexOf('\\');
        string oldItemName = oldLastSlash >= 0 ? oldSub.Substring(oldLastSlash + 1) : oldSub;
        string? oldParent = oldLastSlash >= 0 ? oldSub.Substring(0, oldLastSlash) : null;

        int newLastSlash = newSub.LastIndexOf('\\');
        string newItemName = newLastSlash >= 0 ? newSub.Substring(newLastSlash + 1) : newSub;
        string? newParent = newLastSlash >= 0 ? newSub.Substring(0, newLastSlash) : null;

        // Проверяем запись источника
        var sourceRecord = _db.GetFile(oldMount.Id, oldItemName, oldParent);
        if (sourceRecord == null)
        {
            return Win32Api.FS_FILE_NOTFOUND;
        }

        // Проверяем на циклический путь, если это директория
        if (sourceRecord.IsDir && oldMount.Id == newMount.Id)
        {
            if (newSub.Equals(oldSub, StringComparison.OrdinalIgnoreCase) ||
                newSub.StartsWith(oldSub + "\\", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Log($"Cannot move/copy directory '{oldSub}' into itself or its subfolder '{newSub}'.");
                return Win32Api.FS_FILE_EXISTS;
            }
        }

        // Проверяем наличие целевого элемента
        var targetRecord = _db.GetFile(newMount.Id, newItemName, newParent);
        if (targetRecord != null)
        {
            if (!overwrite)
            {
                return Win32Api.FS_FILE_EXISTS;
            }
            // Перезапись - перемещаем старый целевой файл в корзину
            _db.MoveFileToTrash(targetRecord.Uid);
        }

        _db.EnsureParentDirectoriesExist(newMount.Id, newParent);

        if (isMove)
        {
            // === РЕЖИМ ПЕРЕМЕЩЕНИЯ (F6 / Shift + Drag) ===
            if (oldMount.Id == newMount.Id)
            {
                // Перемещение / переименование ВНУТРИ одного канала (быстро в SQLite)
                if (sourceRecord.IsDir)
                {
                    _db.RenameMoveDirectory(oldMount.Id, oldSub, newItemName, newParent, newMount.Id);
                    Logger.Log($"Directory '{oldSub}' moved/renamed in channel '{oldChannel}' to '{newSub}'.");
                }
                else
                {
                    _db.RenameMoveFile(sourceRecord.Uid, newItemName, newParent, newMount.Id);
                    Logger.Log($"File '{oldSub}' moved/renamed in channel '{oldChannel}' to '{newSub}'.");
                }
            }
            else
            {
                // Физическое перемещение МЕЖДУ разными каналами (скачивание -> загрузка -> удаление старого сообщения в Telegram)
                try
                {
                    if (sourceRecord.IsDir)
                    {
                        PerformPhysicalMoveDirectoryAcrossChannels(oldMount, newMount, oldSub, newItemName, newParent);
                    }
                    else
                    {
                        PerformPhysicalMoveFileAcrossChannels(sourceRecord, oldMount, newMount, newItemName, newParent);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"Error physically moving '{oldPath}' to '{newPath}': {ex.Message}");
                    return Win32Api.FS_FILE_WRITEERROR;
                }
            }
        }
        else
        {
            // === РЕЖИМ КОПИРОВАНИЯ (F5 / Drag без Shift) ===
            if (oldMount.Id == newMount.Id)
            {
                // Виртуальное мгновенное копирование ВНУТРИ одного канала (дублирование записи в SQLite)
                if (sourceRecord.IsDir)
                {
                    PerformCopyDirectoryWithinChannel(oldMount.Id, oldSub, newItemName, newParent);
                    Logger.Log($"Directory '{oldSub}' virtually copied in channel '{oldChannel}' to '{newSub}'.");
                }
                else
                {
                    var copyRecord = new VfsDatabase.FileRecord
                    {
                        Uid = Guid.NewGuid().ToString("N"),
                        MountId = oldMount.Id,
                        IsDir = false,
                        Name = newItemName,
                        Parent = newParent,
                        MTime = DateTime.UtcNow,
                        Size = sourceRecord.Size,
                        TgMessageId = sourceRecord.TgMessageId,
                        InTrash = 0,
                        Ver = 1
                    };
                    _db.AddFile(copyRecord);
                    Logger.Log($"File '{oldSub}' virtually copied in channel '{oldChannel}' to '{newSub}'.");
                }
            }
            else
            {
                // Физическое копирование МЕЖДУ разными каналами (скачивание -> загрузка в целевой канал БЕЗ удаления исходного сообщения)
                try
                {
                    if (sourceRecord.IsDir)
                    {
                        PerformPhysicalCopyDirectoryAcrossChannels(oldMount, newMount, oldSub, newItemName, newParent);
                    }
                    else
                    {
                        PerformPhysicalCopyFileAcrossChannels(sourceRecord, oldMount, newMount, newItemName, newParent);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"Error physically copying '{oldPath}' to '{newPath}': {ex.Message}");
                    return Win32Api.FS_FILE_WRITEERROR;
                }
            }
        }

        Win32Api.RefreshActivePanel();
        TriggerCheckpoint(immediate: true);
        return Win32Api.FS_FILE_OK;
    }

    private static void PerformCopyDirectoryWithinChannel(
        string mountId,
        string oldSubPath,
        string newItemName,
        string? newParent)
    {
        string cleanOldSub = oldSubPath.Trim('\\', '/').Replace('/', '\\');
        string cleanNewParent = string.IsNullOrEmpty(newParent) ? "" : newParent.Trim('\\', '/').Replace('/', '\\');
        string newSubPath = string.IsNullOrEmpty(cleanNewParent) ? newItemName : cleanNewParent + "\\" + newItemName;

        _db!.AddDirectoryRecord(mountId, newItemName, newParent);

        var subItems = _db.GetSubTreeItems(mountId, cleanOldSub);
        foreach (var item in subItems)
        {
            string itemRelativeParent = item.Parent ?? "";
            string recalculatedParent;
            if (itemRelativeParent.Equals(cleanOldSub, StringComparison.OrdinalIgnoreCase))
            {
                recalculatedParent = newSubPath;
            }
            else if (itemRelativeParent.StartsWith(cleanOldSub + "\\", StringComparison.OrdinalIgnoreCase))
            {
                recalculatedParent = newSubPath + itemRelativeParent.Substring(cleanOldSub.Length);
            }
            else
            {
                recalculatedParent = newSubPath;
            }

            if (item.IsDir)
            {
                _db.AddDirectoryRecord(mountId, item.Name, recalculatedParent);
            }
            else
            {
                var copySub = new VfsDatabase.FileRecord
                {
                    Uid = Guid.NewGuid().ToString("N"),
                    MountId = mountId,
                    IsDir = false,
                    Name = item.Name,
                    Parent = recalculatedParent,
                    MTime = item.MTime,
                    Size = item.Size,
                    TgMessageId = item.TgMessageId,
                    InTrash = 0,
                    Ver = 1
                };
                _db.AddFile(copySub);
            }
        }
    }

    private static void PerformPhysicalCopyFileAcrossChannels(
        VfsDatabase.FileRecord fileRecord,
        VfsDatabase.MountInfo oldMount,
        VfsDatabase.MountInfo newMount,
        string newItemName,
        string? newParent)
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"tgvfs_cp_{Guid.NewGuid():N}_{fileRecord.Name}");
        try
        {
            Logger.Log($"[Physical Copy] Downloading file '{fileRecord.Name}' from channel {oldMount.ChannelName} ({oldMount.ChannelId}), msg={fileRecord.TgMessageId}...");
            Task.Run(() => TelegramManager.DownloadFileAsync(oldMount.ChannelId, fileRecord.TgMessageId, tempPath)).GetAwaiter().GetResult();

            string relativeCaption = string.IsNullOrEmpty(newParent) ? newItemName : newParent + "\\" + newItemName;
            Logger.Log($"[Physical Copy] Uploading file '{newItemName}' to channel {newMount.ChannelName} ({newMount.ChannelId})...");
            int newMsgId = Task.Run(() => TelegramManager.UploadAndSendFileAsync(newMount.ChannelId, tempPath, newItemName, relativeCaption)).GetAwaiter().GetResult();

            Logger.Log($"[Physical Copy] Creating SQLite record for new file in mount={newMount.Id}, msg={newMsgId}...");
            var newRecord = new VfsDatabase.FileRecord
            {
                Uid = Guid.NewGuid().ToString("N"),
                MountId = newMount.Id,
                IsDir = false,
                Name = newItemName,
                Parent = newParent,
                MTime = DateTime.UtcNow,
                Size = fileRecord.Size,
                TgMessageId = newMsgId,
                InTrash = 0,
                Ver = 1
            };
            _db!.AddFile(newRecord);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }

    private static void PerformPhysicalCopyDirectoryAcrossChannels(
        VfsDatabase.MountInfo oldMount,
        VfsDatabase.MountInfo newMount,
        string oldSubPath,
        string newItemName,
        string? newParent)
    {
        string cleanOldSub = oldSubPath.Trim('\\', '/').Replace('/', '\\');
        string cleanNewParent = string.IsNullOrEmpty(newParent) ? "" : newParent.Trim('\\', '/').Replace('/', '\\');
        string newSubPath = string.IsNullOrEmpty(cleanNewParent) ? newItemName : cleanNewParent + "\\" + newItemName;

        _db!.AddDirectoryRecord(newMount.Id, newItemName, newParent);

        var subItems = _db.GetSubTreeItems(oldMount.Id, cleanOldSub);
        foreach (var item in subItems)
        {
            string itemRelativeParent = item.Parent ?? "";
            string recalculatedParent;
            if (itemRelativeParent.Equals(cleanOldSub, StringComparison.OrdinalIgnoreCase))
            {
                recalculatedParent = newSubPath;
            }
            else if (itemRelativeParent.StartsWith(cleanOldSub + "\\", StringComparison.OrdinalIgnoreCase))
            {
                recalculatedParent = newSubPath + itemRelativeParent.Substring(cleanOldSub.Length);
            }
            else
            {
                recalculatedParent = newSubPath;
            }

            if (item.IsDir)
            {
                _db.AddDirectoryRecord(newMount.Id, item.Name, recalculatedParent);
            }
            else
            {
                PerformPhysicalCopyFileAcrossChannels(item, oldMount, newMount, item.Name, recalculatedParent);
            }
        }
    }

    private static void PerformPhysicalMoveFileAcrossChannels(
        VfsDatabase.FileRecord fileRecord,
        VfsDatabase.MountInfo oldMount,
        VfsDatabase.MountInfo newMount,
        string newItemName,
        string? newParent)
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"tgvfs_mv_{Guid.NewGuid():N}_{fileRecord.Name}");
        try
        {
            Logger.Log($"[Physical Move] Downloading file '{fileRecord.Name}' from channel {oldMount.ChannelName} ({oldMount.ChannelId}), msg={fileRecord.TgMessageId}...");
            Task.Run(() => TelegramManager.DownloadFileAsync(oldMount.ChannelId, fileRecord.TgMessageId, tempPath)).GetAwaiter().GetResult();

            string relativeCaption = string.IsNullOrEmpty(newParent) ? newItemName : newParent + "\\" + newItemName;
            Logger.Log($"[Physical Move] Uploading file '{newItemName}' to channel {newMount.ChannelName} ({newMount.ChannelId})...");
            int newMsgId = Task.Run(() => TelegramManager.UploadAndSendFileAsync(newMount.ChannelId, tempPath, newItemName, relativeCaption)).GetAwaiter().GetResult();

            Logger.Log($"[Physical Move] Updating SQLite record for UID={fileRecord.Uid} with new mount={newMount.Id}, msg={newMsgId}...");
            _db!.UpdateFileMessageAndMount(fileRecord.Uid, newMount.Id, newMsgId, newItemName, newParent);

            Logger.Log($"[Physical Move] Deleting old message {fileRecord.TgMessageId} from old channel {oldMount.ChannelName}...");
            Task.Run(() => TelegramManager.DeleteMessageAsync(oldMount.ChannelId, fileRecord.TgMessageId)).GetAwaiter().GetResult();
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }

    private static void PerformPhysicalMoveDirectoryAcrossChannels(
        VfsDatabase.MountInfo oldMount,
        VfsDatabase.MountInfo newMount,
        string oldSubPath,
        string newItemName,
        string? newParent)
    {
        string cleanOldSub = oldSubPath.Trim('\\', '/').Replace('/', '\\');
        string cleanNewParent = string.IsNullOrEmpty(newParent) ? "" : newParent.Trim('\\', '/').Replace('/', '\\');
        string newSubPath = string.IsNullOrEmpty(cleanNewParent) ? newItemName : cleanNewParent + "\\" + newItemName;

        _db!.AddDirectoryRecord(newMount.Id, newItemName, newParent);

        var subItems = _db.GetSubTreeItems(oldMount.Id, cleanOldSub);
        foreach (var item in subItems)
        {
            string itemRelativeParent = item.Parent ?? "";
            string recalculatedParent;
            if (itemRelativeParent.Equals(cleanOldSub, StringComparison.OrdinalIgnoreCase))
            {
                recalculatedParent = newSubPath;
            }
            else if (itemRelativeParent.StartsWith(cleanOldSub + "\\", StringComparison.OrdinalIgnoreCase))
            {
                recalculatedParent = newSubPath + itemRelativeParent.Substring(cleanOldSub.Length);
            }
            else
            {
                recalculatedParent = newSubPath;
            }

            if (item.IsDir)
            {
                _db.AddDirectoryRecord(newMount.Id, item.Name, recalculatedParent);
            }
            else
            {
                PerformPhysicalMoveFileAcrossChannels(item, oldMount, newMount, item.Name, recalculatedParent);
            }
        }

        _db.RenameMoveDirectory(oldMount.Id, cleanOldSub, newItemName, newParent, newMount.Id);
    }

    // Вызывается Total Commander при выгрузке плагина или закрытии программы
    [UnmanagedCallersOnly(EntryPoint = "FsContentPluginUnload", CallConvs = [typeof(CallConvStdcall)])]
    public static void FsContentPluginUnload()
    {
        Logger.Log("FsContentPluginUnload called. Delegating to OnProcessExit.");
        OnProcessExit(null, EventArgs.Empty);
    }
}
