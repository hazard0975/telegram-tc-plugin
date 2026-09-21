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
    public const string TrashDirName = "[🗑] Корзина";
    public static bool IsTrashFolder(string name) =>
        name.Equals(".[🗑] Корзина", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("[🗑] Корзина", StringComparison.OrdinalIgnoreCase);

    public static void ParseVfsPath(string cleanPath, out string channelName, out string subPath, out bool isInTrash)
    {
        channelName = "";
        subPath = "";
        isInTrash = false;

        if (string.IsNullOrWhiteSpace(cleanPath)) return;

        string[] parts = cleanPath.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;

        if (IsTrashFolder(parts[0]))
        {
            isInTrash = true;
            if (parts.Length > 1)
            {
                channelName = parts[1];
                if (parts.Length > 2)
                {
                    subPath = string.Join("\\", parts, 2, parts.Length - 2);
                }
            }
        }
        else
        {
            channelName = parts[0];
            if (parts.Length > 1)
            {
                subPath = string.Join("\\", parts, 1, parts.Length - 1);
            }
        }
    }

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

    private class PendingTrashItem
    {
        public string MountId { get; set; } = "";
        public long ChannelId { get; set; }
        public string Uid { get; set; } = "";
        public int TgMessageId { get; set; }
        public string FileName { get; set; } = "";
    }

    private static readonly List<PendingTrashItem> _pendingTrashDeletions = new();
    private static readonly object _trashDeleteLock = new();

    public static void PurgeTrashRecords(string mountId, long channelId, List<VfsDatabase.FileRecord> trashRecords)
    {
        try
        {
            if (trashRecords == null || trashRecords.Count == 0 || _db == null) return;

            var itemsWithMsg = trashRecords.Where(x => x.TgMessageId > 0).ToList();
            var itemsWithoutMsg = trashRecords.Where(x => x.TgMessageId <= 0).ToList();

            // Виртуальные папки и элементы без сообщения сразу удаляем из БД
            if (itemsWithoutMsg.Count > 0)
            {
                _db.DeleteFilesByUids(itemsWithoutMsg.Select(x => x.Uid));
            }

            if (itemsWithMsg.Count > 0 && channelId != 0)
            {
                // Безопасная группировка: одно сообщение может быть привязано к нескольким записям (копии/дубликаты)
                var msgToUids = itemsWithMsg
                    .GroupBy(x => x.TgMessageId)
                    .ToDictionary(g => g.Key, g => g.Select(x => x.Uid).ToList());

                // Проверяем ссылки: удаляем сообщение из Telegram ТОЛЬКО если на него больше нет АКТИВНЫХ ссылок в файловой системе!
                var safeToDeleteFromTg = _db.FilterMessagesWithoutActiveReferences(mountId, msgToUids.Keys);

                var distinctMsgIds = msgToUids.Keys.ToList();
                int total = distinctMsgIds.Count;
                int processed = 0;

                for (int i = 0; i < total; i += 100)
                {
                    var chunk = distinctMsgIds.Skip(i).Take(100).ToArray();
                    int batchNum = (i / 100) + 1;
                    int totalBatches = (int)Math.Ceiling((double)total / 100.0);
                    int percentDone = total > 0 ? (int)((processed * 100.0) / total) : 0;

                    string statusMsg = $"Очистка корзины ({processed}/{total})";
                    int progressRes = ReportProgress(".[🗑] Корзина", statusMsg, percentDone);
                    if (progressRes == 1)
                    {
                        Logger.Warn("WFX", $"[TRASH PURGE CANCELLED] User pressed Cancel in Total Commander at batch {batchNum}/{totalBatches}. Stopping further deletion.");
                        break;
                    }

                    // В Telegram отправляем только те ID, у которых нет живых оригиналов
                    var tgDeleteBatch = chunk.Where(id => safeToDeleteFromTg.Contains(id)).ToArray();

                    if (tgDeleteBatch.Length > 0)
                    {
                        Logger.Info("WFX", $"[TRASH PURGE BATCH {batchNum}/{totalBatches}] Requesting Telegram to delete {tgDeleteBatch.Length} messages...");

                        try
                        {
                            var deletedMsgIds = System.Threading.Tasks.Task.Run(() => 
                                TelegramManager.DeleteMessagesAsync(
                                    channelId, 
                                    tgDeleteBatch, 
                                    onProgress: null, 
                                    cancellationToken: System.Threading.CancellationToken.None
                                )).GetAwaiter().GetResult();
                        }
                        catch (Exception ex)
                        {
                            Logger.Error("WFX", $"Ошибка пакетного удаления {tgDeleteBatch.Length} сообщений из TG: {ex.Message}");
                        }
                    }

                    // Из локальной базы SQLite удаляем записи ВСЕГДА (раз пользователь очищает корзину)
                    var uidsToRemove = chunk.SelectMany(id => msgToUids[id]).Distinct().ToList();
                    _db.DeleteFilesByUids(uidsToRemove);
                    Logger.Info("DB", $"[DB PURGE SUCCESS] Deleted {uidsToRemove.Count} items from SQLite database.");

                    processed += chunk.Length;

                    if (i + 100 < total)
                    {
                        System.Threading.Thread.Sleep(250);
                    }
                }
            }

            _db.DeleteEmptyTrashDirectories(mountId);
            ReportProgress(".[🗑] Корзина", "Очистка завершена", 100);
            TriggerCheckpoint(immediate: true);
        }
        catch (Exception ex)
        {
            Logger.Error("WFX", $"Критическая ошибка при выполнении PurgeTrashRecords: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private static void ProcessPendingTrashDeletions()
    {
        List<PendingTrashItem> itemsToProcess;
        lock (_trashDeleteLock)
        {
            if (_pendingTrashDeletions.Count == 0) return;
            itemsToProcess = new List<PendingTrashItem>(_pendingTrashDeletions);
            _pendingTrashDeletions.Clear();
        }

        Logger.Info("WFX", $"[BATCH TRASH DELETE START] Processing {itemsToProcess.Count} pending trash items...");

        var groupedByChannel = itemsToProcess.GroupBy(x => (x.MountId, x.ChannelId));
        foreach (var group in groupedByChannel)
        {
            string mountId = group.Key.MountId;
            long channelId = group.Key.ChannelId;
            var trashRecords = group.Select(x => new VfsDatabase.FileRecord
            {
                Uid = x.Uid,
                MountId = x.MountId,
                TgMessageId = x.TgMessageId,
                Name = x.FileName
            }).ToList();

            PurgeTrashRecords(mountId, channelId, trashRecords);
        }

        Logger.Info("WFX", $"[BATCH TRASH DELETE FINISHED] Processed batch trash deletions.");
    }

    private static void OnProcessExit(object? sender, EventArgs e)
    {
        try
        {
            Logger.Info("DB", "ProcessExit event triggered. Performing final WAL checkpoint (TRUNCATE) and clearing connection pools.");
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
            Logger.Error("DB", $"Error during ProcessExit shutdown: {ex.Message}", ex);
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
                if (firstMount == null && !IsTrashFolder(parts[0]))
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

    /// <summary>
    /// Проверяет, является ли локальный путь временным файлом редактирования/просмотра (например Total Commander _tc или %TEMP%)
    /// </summary>
    public static bool IsTemporaryLocalPath(string? localPath)
    {
        if (string.IsNullOrWhiteSpace(localPath)) return false;

        try
        {
            string fullPath = Path.GetFullPath(localPath).Replace('/', '\\');

            // 1. Папка %TEMP% или %TMP%
            string tempDir = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\').Replace('/', '\\');
            if (fullPath.StartsWith(tempDir + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // 2. Специфические папки Total Commander (_tc, _tc\...)
            if (fullPath.IndexOf("\\_tc\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                fullPath.IndexOf("\\_tc", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }
        catch { }

        return false;
    }

    private static FindState? CreateStateForPath(string pathStr)
    {
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
        string displayPath = string.IsNullOrEmpty(cleanPath) ? "\\" : $"\\{cleanPath}";
        Logger.Info("WFX", $"[DIR OPEN] Opened folder '{displayPath}'");

        if (!TelegramManager.IsLoggedIn)
        {
            // Попытка тихо авторизоваться, если есть сессия
            try
            {
                if (System.IO.File.Exists(TelegramManager.ConfigPath + "\\WTelegram.session"))
                {
                    Logger.Info("TG", "[AUTH] Found session file, attempting silent login...");
                    // Вызываем синхронно, передаем true для тихого режима
                    System.Threading.Tasks.Task.Run(() => TelegramManager.LoginAsync(true)).GetAwaiter().GetResult();
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("TG", $"[AUTH WARN] Silent login failed: {ex.Message}");
            }

            if (!TelegramManager.IsLoggedIn)
            {
                Logger.Info("WFX", "[AUTH REQUIRED] User is not logged in. Returning '[ Login required.txt ]'.");
                var loginState = new FindState();
                loginState.Items.Add(new VfsDatabase.VfsItem 
                { 
                    Name = "[ Login required ]", 
                    IsDirectory = false, 
                    Size = 100, 
                    Date = DateTime.Now 
                });
                return loginState;
            }
        }

        if (_db == null)
        {
            Logger.Error("WFX", "[DB ERROR] Database is null in CreateStateForPath.");
            return null;
        }

        var state = new FindState();
        try
        {
            if (string.IsNullOrEmpty(cleanPath))
            {
                if (!_isBatchOperation)
                {
                    _lastMirrorPath = null;
                }
                // Корень: возвращаем каналы и служебные триггеры
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
                state.Items.Add(new VfsDatabase.VfsItem 
                { 
                    Name = TrashDirName, 
                    IsDirectory = true, 
                    Size = 0,
                    Date = DateTime.Now 
                });
                state.Items.AddRange(_db.GetMounts());
                Logger.Info("WFX", $"[DIR LIST] Root directory: Found {state.Items.Count} item(s) (Channels, Triggers & Trash)");
            }
            else
            {
                ParseVfsPath(cleanPath, out string channelTitle, out string parentSubPath, out bool isInTrash);

                if (isInTrash)
                {
                    if (string.IsNullOrEmpty(channelTitle))
                    {
                        // Корень Корзины: выводим список всех доступных каналов
                        state.Items.AddRange(_db.GetMounts());
                        Logger.Info("WFX", $"[TRASH LIST] Trash Root: Found {state.Items.Count} channel folder(s)");
                    }
                    else
                    {
                        var mount = _db.GetMountByName(channelTitle);
                        if (mount != null)
                        {
                            string? trashParent = string.IsNullOrEmpty(parentSubPath) ? null : parentSubPath;
                            var trashFiles = _db.GetTrashFiles(mount.Id, trashParent);
                            foreach (var tf in trashFiles)
                            {
                                string displayName = tf.IsDir ? tf.Name : VfsDatabase.GetVersionedFileName(tf.Name, tf.Ver);
                                state.Items.Add(new VfsDatabase.VfsItem
                                {
                                    Name = displayName,
                                    IsDirectory = tf.IsDir,
                                    Size = tf.Size,
                                    Date = tf.MTime
                                });
                            }
                        }

                        if (state.Items.Count == 0 && string.IsNullOrEmpty(parentSubPath))
                        {
                            state.Items.Add(new VfsDatabase.VfsItem
                            {
                                Name = "[ Корзина пуста ]",
                                IsDirectory = false,
                                Size = 0,
                                Date = DateTime.Now
                            });
                        }
                        Logger.Info("WFX", $"[TRASH LIST] Channel '{channelTitle}' Trash Subpath '{parentSubPath}': Found {state.Items.Count} item(s)");
                    }
                }
                else
                {
                    var mount = _db.GetMountByName(channelTitle);
                    if (mount != null && mount.Mode == 0 && !string.IsNullOrEmpty(mount.LocalPath))
                    {
                        string localPathToSet = mount.LocalPath;
                        if (!string.IsNullOrEmpty(parentSubPath))
                        {
                            localPathToSet = System.IO.Path.Combine(mount.LocalPath, parentSubPath);
                        }
                        
                        if (!_isBatchOperation && !string.Equals(_lastMirrorPath, localPathToSet, StringComparison.OrdinalIgnoreCase))
                        {
                            _lastMirrorPath = localPathToSet;
                            Logger.Info("WFX", $"[MIRROR] Syncing target panel to local folder '{localPathToSet}'");
                            System.Threading.Tasks.Task.Delay(100).ContinueWith(_ => {
                                Win32Api.ChangeInactivePanelDir(localPathToSet);
                            });
                        }
                    }
                    
                    var channelFiles = _db.GetFiles(channelTitle, string.IsNullOrEmpty(parentSubPath) ? null : parentSubPath);
                    state.Items.AddRange(channelFiles);
                    Logger.Info("WFX", $"[DIR LIST] Channel '{channelTitle}' Active Subpath '{parentSubPath}': Found {state.Items.Count} item(s)");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("WFX", "[DIR ERROR] Exception in CreateStateForPath", ex);
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
            Logger.Info("WFX", $"Plugin initialization: {initMode}, PluginNumber={pluginNumber}");
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
                    Logger.Warn("WFX", $"Failed to get ProgressProc delegate: {ex.Message}");
                }
            }
            
            // Принудительно загружаем DLL в память процесса до того, как к ней обратится SQLite
            try
            {
                string basePath = AppContext.BaseDirectory;
                
                string modPath = Win32Api.GetCurrentModulePath();
                if (!string.IsNullOrEmpty(modPath))
                {
                    basePath = System.IO.Path.GetDirectoryName(modPath) ?? basePath;
                }

                string arch = IntPtr.Size == 8 ? "x64" : "x86";
                string libPath = System.IO.Path.Combine(basePath, arch, "e_sqlite3.dll");
                
                Logger.Debug("DB", $"Manually loading SQLite DLL from: {libPath}");
                
                if (System.IO.File.Exists(libPath))
                {
                    IntPtr libHandle = System.Runtime.InteropServices.NativeLibrary.Load(libPath);
                    Logger.Debug("DB", $"Load successful, handle: {libHandle}");
                }
                else
                {
                    Logger.Error("DB", $"File does not exist at {libPath}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("DB", "Manual DLL load failed", ex);
            }

            if (!_processExitHooked)
            {
                _processExitHooked = true;
                AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            }

            Win32Api.EnsureVisualStyles();

            // Инициализируем базу данных при запуске плагина
            if (_db == null)
            {
                _db = new VfsDatabase();
                Logger.Info("DB", "VfsDatabase successfully initialized.");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("WFX", "Critical Exception in HandleInit", ex);
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
        Logger.Info("WFX", $"FsExecuteFile: '{path}' (Verb: '{verb}')");

        // Обработка запроса свойств (Alt+Enter в Total Commander)
        if (verb.Equals("properties", StringComparison.OrdinalIgnoreCase))
        {
            string cleanVfs = NormalizeVfsPath(path);
            if (string.IsNullOrEmpty(cleanVfs))
            {
                return Win32Api.FS_EXEC_OK; // Корень плагина
            }

            string[] parts = cleanVfs.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && _db != null)
            {
                ParseVfsPath(cleanVfs, out string channelName, out string subPath, out bool isInTrash);

                if (isInTrash)
                {
                    if (string.IsNullOrEmpty(channelName))
                    {
                        return Win32Api.FS_EXEC_OK; 
                    }

                    var mount = _db.GetMountByName(channelName);
                    if (mount != null)
                    {
                        if (string.IsNullOrEmpty(subPath))
                        {
                            // Свойства корзины конкретного канала: [🗑] Корзина\ChannelName
                            FilePropertiesDialog.ShowTrashProperties(mount.ChannelName, mount.ChannelId, mount.Id, _db);
                            return Win32Api.FS_EXEC_OK;
                        }

                        // Свойства файла/папки внутри корзины: [🗑] Корзина\ChannelName\folder\file_v1.txt
                        string[] subParts = subPath.Split('\\');
                        string versionedName = subParts[^1];
                        string? trashSubParent = subParts.Length > 1 ? string.Join("\\", subParts, 0, subParts.Length - 1) : null;

                        if (versionedName == "..")
                        {
                            if (string.IsNullOrEmpty(trashSubParent))
                            {
                                // ".." в корне корзины канала -> свойства корзины этого канала
                                FilePropertiesDialog.ShowTrashProperties(mount.ChannelName, mount.ChannelId, mount.Id, _db);
                                return Win32Api.FS_EXEC_OK;
                            }
                            else
                            {
                                // ".." в подпапке корзины -> свойства текущей подпапки корзины
                                int lastSlash = trashSubParent.LastIndexOf('\\');
                                string currentDirName = lastSlash >= 0 ? trashSubParent.Substring(lastSlash + 1) : trashSubParent;
                                string? currentDirParent = lastSlash >= 0 ? trashSubParent.Substring(0, lastSlash) : null;
                                var currentTrashDir = _db.GetTrashFileByVersionedName(mount.Id, currentDirName, currentDirParent);
                                if (currentTrashDir != null)
                                {
                                    FilePropertiesDialog.Show(mount.ChannelName, mount.ChannelId, trashSubParent, currentTrashDir, _db);
                                    return Win32Api.FS_EXEC_OK;
                                }
                            }
                        }

                        var trashFile = _db.GetTrashFileByVersionedName(mount.Id, versionedName, trashSubParent);
                        if (trashFile != null)
                        {
                            FilePropertiesDialog.Show(mount.ChannelName, mount.ChannelId, subPath, trashFile, _db);
                            return Win32Api.FS_EXEC_OK;
                        }
                    }
                }
                else
                {
                    var mount = _db.GetMountByName(channelName);
                    if (mount != null)
                    {
                        if (string.IsNullOrEmpty(subPath))
                        {
                            // Свойства самого тома/канала
                            var mountAsFile = new VfsDatabase.FileRecord
                            {
                                Uid = mount.Id,
                                MountId = mount.Id,
                                IsDir = true,
                                Name = mount.ChannelName,
                                Parent = null,
                                MTime = DateTime.UtcNow,
                                Size = 0,
                                TgMessageId = 0,
                                InTrash = 0,
                                Ver = 1
                            };
                            FilePropertiesDialog.Show(mount.ChannelName, mount.ChannelId, "", mountAsFile, _db);
                            return Win32Api.FS_EXEC_OK;
                        }

                        string[] subParts = subPath.Split('\\');
                        string fileName = subParts[^1];
                        string? parent = subParts.Length > 1 ? string.Join("\\", subParts, 0, subParts.Length - 1) : null;

                        // Если свойства запрошены для элемента ".." (переход наверх)
                        if (fileName == "..")
                        {
                            if (string.IsNullOrEmpty(parent))
                            {
                                // ".." в корне канала -> свойства текущего канала/тома
                                var mountAsFile = new VfsDatabase.FileRecord
                                {
                                    Uid = mount.Id,
                                    MountId = mount.Id,
                                    IsDir = true,
                                    Name = mount.ChannelName,
                                    Parent = null,
                                    MTime = DateTime.UtcNow,
                                    Size = 0,
                                    TgMessageId = 0,
                                    InTrash = 0,
                                    Ver = 1
                                };
                                FilePropertiesDialog.Show(mount.ChannelName, mount.ChannelId, "", mountAsFile, _db);
                                return Win32Api.FS_EXEC_OK;
                            }
                            else
                            {
                                // ".." в подпапке -> свойства текущей открытой подпапки (которая является parent для "..")
                                int lastSlash = parent.LastIndexOf('\\');
                                string currentDirName = lastSlash >= 0 ? parent.Substring(lastSlash + 1) : parent;
                                string? currentDirParent = lastSlash >= 0 ? parent.Substring(0, lastSlash) : null;

                                var currentDirRecord = _db.GetFile(mount.Id, currentDirName, currentDirParent);
                                if (currentDirRecord != null)
                                {
                                    FilePropertiesDialog.Show(mount.ChannelName, mount.ChannelId, parent, currentDirRecord, _db);
                                    return Win32Api.FS_EXEC_OK;
                                }
                            }
                        }

                        var fileRecord = _db.GetFile(mount.Id, fileName, parent);
                        if (fileRecord != null)
                        {
                            FilePropertiesDialog.Show(mount.ChannelName, mount.ChannelId, subPath, fileRecord, _db);
                            return Win32Api.FS_EXEC_OK;
                        }
                    }
                }
            }

            return Win32Api.FS_EXEC_OK;
        }

        if (verb != "open" && verb != "") return Win32Api.FS_EXEC_ERROR;

        if (path.EndsWith("[📁+] Создать папку") || path.EndsWith("[+] Создать папку"))
        {
            try
            {
                var result = CreateFolderDialog.Show();
                if (result != null)
                {
                    string cname = "[TC] " + result.Name;
                    long cid = System.Threading.Tasks.Task.Run(() => TelegramManager.CreateChannelAsync(cname, "TelegramVFS channel")).GetAwaiter().GetResult();
                    
                    _db!.AddMount(new VfsDatabase.MountInfo {
                        Id = "tg-fldr-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                        ChannelId = cid,
                        ChannelName = result.Name, // сохраняем без префикса для удобства
                        Mode = result.Mode,
                        LocalPath = result.LocalPath
                    });
                    
                    Logger.Info("WFX", $"[FOLDER CREATED] Channel/folder '{result.Name}' created successfully.");
                    Win32Api.RefreshActivePanel();
                    TriggerCheckpoint(immediate: true);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("WFX", "Create folder error", ex);
            }
            
            return Win32Api.FS_EXEC_OK;
        }

        if (path.EndsWith("[❌] Удалить папку") || path.EndsWith("[-] Удалить папку"))
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
                                System.Threading.Tasks.Task.Run(() => TelegramManager.DeleteChannelAsync(mount.ChannelId)).GetAwaiter().GetResult();
                            }

                            _db?.DeleteMount(mount.Id);
                            Logger.Info("WFX", $"[FOLDER DELETED] Folder '{selectedFolder}' and Telegram channel {mount.ChannelId} deleted.");

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
                Logger.Error("WFX", "Delete folder error", ex);
            }

            return Win32Api.FS_EXEC_OK;
        }

        string execClean = NormalizeVfsPath(path);
        if (execClean.Equals("[⚙] Настройки", StringComparison.OrdinalIgnoreCase) || 
            path.EndsWith("[⚙] Настройки") || 
            path.EndsWith("[⚙] Настройки\\") || 
            path.EndsWith("[⚙] Настройки/") || 
            path.EndsWith("[*] Настройки плагина"))
        {
            try
            {
                string oldDir = SettingsManager.DataDirectory;
                var result = SettingsDialog.Show();
                if (result != null)
                {
                    if (result.StorageLocationChanged)
                    {
                        Logger.Info("CFG", $"Storage location change requested. New mode: {result.SelectedStorageMode}, CustomPath: '{result.CustomPath}'");

                        // 1. Закрываем и сбрасываем текущие ресурсы базы данных и TelegramClient
                        try
                        {
                            _db?.Dispose();
                        }
                        catch (Exception dbEx)
                        {
                            Logger.Warn("DB", $"Error disposing DB: {dbEx.Message}");
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
                            Logger.Info("DB", "VfsDatabase re-initialized at new location.");
                        }
                        catch (Exception ex)
                        {
                            Logger.Error("DB", $"Failed to re-initialize DB: {ex.Message}");
                        }

                        // 5. Оповещаем и обновляем список папок в Total Commander
                        Logger.Info("CFG", "Settings applied. Requesting panel refresh.");
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
                Logger.Error("CFG", "Settings dialog execution error", ex);
            }
            
            return Win32Api.FS_EXEC_OK;
        }

        if (path.EndsWith("[ Login required ]") || path.EndsWith("[ Login required.txt ]"))
        {
            // Запускаем асинхронный логин в синхронном контексте без await (Task.Run)
            System.Threading.Tasks.Task.Run(() => 
            {
                try
                {
                    bool success = TelegramManager.LoginAsync().GetAwaiter().GetResult();
                    if (success)
                    {
                        Logger.Info("TG", "Login successful! Requesting panel refresh.");
                        Win32Api.RefreshActivePanel();
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("TG", "Login task failed", ex);
                }
            }).GetAwaiter().GetResult();
            
            return Win32Api.FS_EXEC_OK;
        }

        // Для обычных файлов возвращаем FS_EXEC_YOURSELF (-1), чтобы Total Commander
        // скачал файл во временную директорию и запустил его ассоциированным приложением.
        return Win32Api.FS_EXEC_YOURSELF;
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
                    Logger.Debug("WFX", $"ProgressProc returned code: {res} (percentDone={percentDone})");
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
            Logger.Warn("WFX", $"Progress callback error: {ex.Message}");
            return 0;
        }
    }

    private static int HandlePutFile(string localPath, string remotePath, int copyFlags)
    {
        Logger.Info("WFX", $"[PUT FILE START] Local='{localPath}' -> Remote='{remotePath}' (Flags={copyFlags})");

        if (!System.IO.File.Exists(localPath))
        {
            Logger.Error("WFX", $"FsPutFile: Local file does not exist: '{localPath}'");
            return Win32Api.FS_FILE_NOTFOUND;
        }

        if (_db == null)
        {
            Logger.Error("WFX", "FsPutFile: Database is not initialized.");
            return Win32Api.FS_FILE_WRITEERROR;
        }

        string cleanRemote = NormalizeVfsPath(remotePath);
        ParseVfsPath(cleanRemote, out string channelName, out string subPath, out bool isInTrash);

        if (isInTrash)
        {
            Logger.Warn("WFX", $"[PUT FILE BLOCKED] Uploading to Trash is prohibited: '{remotePath}'");
            System.Windows.Forms.MessageBox.Show(
                "Загрузка и копирование файлов в Корзину запрещены.\n\nДля удаления объектов используйте клавишу F8 / Delete.",
                "Операция заблокирована",
                System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Warning);
            return Win32Api.FS_FILE_NOTSUPPORTED;
        }

        if (string.IsNullOrEmpty(channelName))
        {
            Logger.Warn("WFX", $"FsPutFile: Cannot copy directly to root: '{remotePath}'");
            return Win32Api.FS_FILE_NOTSUPPORTED;
        }

        string fileName = System.IO.Path.GetFileName(subPath);
        string? parentSubPath = System.IO.Path.GetDirectoryName(subPath);
        if (string.IsNullOrEmpty(parentSubPath)) parentSubPath = null;

        var mount = _db.GetMountByName(channelName);
        if (mount == null)
        {
            Logger.Error("WFX", $"FsPutFile: Channel '{channelName}' not found in database.");
            return Win32Api.FS_FILE_NOTFOUND;
        }

        _db.EnsureParentDirectoriesExist(mount.Id, parentSubPath);

        var existingFile = _db.GetFile(mount.Id, fileName, parent: parentSubPath);
        if (existingFile != null)
        {
            bool overwrite = (copyFlags & Win32Api.FS_COPYFLAGS_OVERWRITE) != 0;
            if (!overwrite)
            {
                Logger.Warn("WFX", $"FsPutFile: File '{fileName}' already exists in '{channelName}' and OVERWRITE is not set.");
                return Win32Api.FS_FILE_EXISTS;
            }
        }

        try
        {
            var fileInfo = new System.IO.FileInfo(localPath);
            long limitBytes = TelegramManager.IsPremium ? 4294967296L : 2147483648L;
            if (fileInfo.Length > limitBytes)
            {
                Logger.Error("WFX", $"FsPutFile: File size {Logger.FormatBytes(fileInfo.Length)} exceeds limit of {Logger.FormatBytes(limitBytes)}.");
                return Win32Api.FS_FILE_WRITEERROR;
            }

            long currentPercent = 0;
            bool userAborted = false;
            int messageId = 0;

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
                            Logger.Info("WFX", "[UPLOAD PAUSED] Stream paused (TC blocking ReportProgress).");
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
                        Logger.Info("WFX", $"[UPLOAD PAUSED] Stream paused (TC pause detected, progress={pct}%).");
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
                                Logger.Info("WFX", "[UPLOAD RESUMED] Stream resumed by TC.");
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
                            Logger.Info("WFX", "[UPLOAD RESUMED] Setting pauseGate.");
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
                        Logger.Error("WFX", "FsPutFile error", ex);
                        return Win32Api.FS_FILE_WRITEERROR;
                    }
                }

                // Если сетевая задача завершилась успешно и вернула ID сообщения,
                // значит файл гарантированно передан в Telegram (любые последующие или запоздалые флаги отмены игнорируются)
                if (messageId > 0)
                {
                    userAborted = false;
                }
            }

            if (userAborted)
            {
                Logger.Warn("WFX", "[UPLOAD CANCELLED] User cancelled upload in Total Commander.");
                return Win32Api.FS_FILE_USERABORT;
            }

            if (messageId <= 0)
            {
                Logger.Error("WFX", "FsPutFile: Upload failed (no Telegram message ID returned).");
                return Win32Api.FS_FILE_WRITEERROR;
            }

            // Финальный рапорт 100%
            ReportProgress(localPath, remotePath, 100);

            // Обработка перезаписи: переносим старую версию в корзину и инкрементируем версию
            int ver = 1;
            string? preservedSourcePath = null;
            if (existingFile != null)
            {
                _db.MoveFileToTrash(existingFile.Uid);
                ver = existingFile.Ver + 1;
                preservedSourcePath = existingFile.SourcePath;
                Logger.Info("DB", $"[DB WRITE] Old version of '{fileName}' marked in_trash=1. New version: {ver}");
            }

            // Определение source_path: если файл редактировался во временной папке (Total Commander F4 / _tc / %TEMP%),
            // мы НЕ затираем оригинальный путь на диске мусорным временным путем, а сохраняем существующий source_path!
            string? finalSourcePath;
            bool isTemp = IsTemporaryLocalPath(localPath);
            if (isTemp)
            {
                finalSourcePath = preservedSourcePath;
                Logger.Info("WFX", $"FsPutFile: Uploading from temp editor location '{localPath}'. Preserving existing source_path: '{finalSourcePath ?? "<none>"}'");
            }
            else
            {
                finalSourcePath = Path.GetFullPath(localPath);
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
                Ver = ver,
                SourcePath = finalSourcePath
            });

            Logger.Info("DB", $"[DB WRITE] File '{fileName}' successfully recorded in DB.");
            TriggerCheckpoint(immediate: false);

            if ((copyFlags & Win32Api.FS_COPYFLAGS_MOVE) != 0)
            {
                try
                {
                    if (System.IO.File.Exists(localPath))
                    {
                        System.IO.File.Delete(localPath);
                        Logger.Info("WFX", $"[FILE MOVED] Deleted source local file '{localPath}' after upload.");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn("WFX", $"Note on deleting source local file '{localPath}': {ex.Message}");
                }
            }

            return Win32Api.FS_FILE_OK;
        }
        catch (Exception ex)
        {
            Logger.Error("WFX", "FsPutFile error", ex);
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
        Logger.Info("WFX", $"[GET FILE START] Remote='{remotePath}' -> Local='{localPath}' (Flags={copyFlags})");

        if (_db == null)
        {
            Logger.Error("WFX", "FsGetFile: Database is not initialized.");
            return Win32Api.FS_FILE_READERROR;
        }

        string cleanRemote = NormalizeVfsPath(remotePath);
        ParseVfsPath(cleanRemote, out string channelFolderName, out string subPath, out bool isInTrash);

        if (string.IsNullOrEmpty(channelFolderName))
        {
            Logger.Warn("WFX", $"FsGetFile: Invalid remote path: '{remotePath}'");
            return Win32Api.FS_FILE_NOTSUPPORTED;
        }

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
            Logger.Error("WFX", $"FsGetFile: Channel '{channelFolderName}' not found.");
            return Win32Api.FS_FILE_NOTFOUND;
        }

        string? parentSubPath = Path.GetDirectoryName(subPath);
        if (string.IsNullOrEmpty(parentSubPath)) parentSubPath = null;

        // Поиск файла в базе данных (с поддержкой корзины)
        VfsDatabase.FileRecord? fileRecord = null;
        if (isInTrash)
        {
            fileRecord = _db.GetTrashFileByVersionedName(mount.Id, fileName, parentSubPath);
        }
        else
        {
            fileRecord = _db.GetFile(mount.Id, fileName, parent: parentSubPath);
        }

        if (fileRecord == null || fileRecord.IsDir || fileRecord.TgMessageId <= 0)
        {
            Logger.Error("WFX", $"FsGetFile: File '{fileName}' not found in DB or has no Telegram Message ID.");
            return Win32Api.FS_FILE_NOTFOUND;
        }

        // Очищаем суффикс версии (_v1, _v2) для имени сохраняемого локального файла,
        // но ТОЛЬКО если это НЕ временная папка Total Commander (иначе открытие/просмотр по Enter/F3 выдаст "Файл не найден")
        string localDir = Path.GetDirectoryName(localPath) ?? "";
        string localFileName = Path.GetFileName(localPath);

        string sysTemp = Path.GetTempPath();
        string userTemp = Environment.GetEnvironmentVariable("TEMP") ?? "";
        string userTmp = Environment.GetEnvironmentVariable("TMP") ?? "";

        bool isTempTarget = !string.IsNullOrEmpty(localPath) && (
            (!string.IsNullOrEmpty(sysTemp) && localPath.StartsWith(sysTemp, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(userTemp) && localPath.StartsWith(userTemp, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(userTmp) && localPath.StartsWith(userTmp, StringComparison.OrdinalIgnoreCase)) ||
            localPath.IndexOf("\\_tc\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
            localPath.IndexOf("\\_tc_", StringComparison.OrdinalIgnoreCase) >= 0 ||
            localPath.IndexOf("\\AppData\\Local\\Temp\\", StringComparison.OrdinalIgnoreCase) >= 0
        );

        if (!isTempTarget && !string.IsNullOrEmpty(localDir) && !string.IsNullOrEmpty(localFileName))
        {
            if (!localFileName.Equals(fileRecord.Name, StringComparison.OrdinalIgnoreCase))
            {
                string cleanLocalPath = Path.Combine(localDir, fileRecord.Name);
                Logger.Info("WFX", $"[GET FILE] Stripping version suffix for local file: '{localPath}' -> '{cleanLocalPath}'");
                localPath = cleanLocalPath;
            }
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
                    Logger.Info("WFX", $"FsGetFile: Local file '{localPath}' is identical ({Logger.FormatBytes(fileRecord.Size)}). Skipping download.");
                    return Win32Api.FS_FILE_OK;
                }

                bool canOverwrite = (copyFlags & Win32Api.FS_COPYFLAGS_OVERWRITE) != 0;
                bool canResume = (copyFlags & Win32Api.FS_COPYFLAGS_RESUME) != 0;

                // Total Commander при FsExecuteFile (FS_EXEC_YOURSELF) может предварительно создать пустой (0 байт) файл
                // или скачивать во временный каталог пользователя Path.GetTempPath(). В этих случаях разрешаем перезапись.
                bool isTempOrZeroByte = existingInfo.Length == 0 || isTempTarget;

                if (!canOverwrite && !canResume && !isTempOrZeroByte)
                {
                    Logger.Warn("WFX", $"FsGetFile: Target file '{localPath}' exists and OVERWRITE is not set.");
                    return Win32Api.FS_FILE_EXISTS;
                }
            }

            Logger.Info("WFX", $"[DOWNLOAD START] File '{fileName}' (MsgId: {fileRecord.TgMessageId}, Size: {Logger.FormatBytes(fileRecord.Size)})");

            long currentPercent = 0;
            bool userAborted = false;
            bool isFinished = false;

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
                            Logger.Info("WFX", "[DOWNLOAD PAUSED] TC blocking ReportProgress detected.");
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
                        Logger.Info("WFX", $"[DOWNLOAD PAUSED] TC pause button detected ({pct}%).");
                        try { cts.Cancel(); } catch { }
                        break;
                    }
                }

                try { watchdogCts.Cancel(); } catch { }
                try { watchdogTask.Wait(500); } catch { }

                try
                {
                    downloadTask.GetAwaiter().GetResult();
                    if (!cts.IsCancellationRequested)
                    {
                        isFinished = true; // Загрузка успешно завершена
                        userAborted = false; // Файл уже скачан целиком на диск
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
                        Logger.Error("WFX", "FsGetFile error", ex);
                        return Win32Api.FS_FILE_READERROR;
                    }
                }

                if (userAborted) break;

                if (!isFinished)
                {
                    // Мы на паузе. Ждем, пока пользователь не отожмет паузу
                    Logger.Info("WFX", "[DOWNLOAD WAITING] Waiting for TC resume signal...");
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
                            Logger.Info("WFX", "[DOWNLOAD RESUMED] Resuming download task.");
                            break; // Выходим из цикла ожидания паузы, внешний цикл перезапустит скачивание (докачку)
                        }
                        System.Threading.Thread.Sleep(100);
                    }
                }
            }

            if (userAborted)
            {
                Logger.Warn("WFX", "[DOWNLOAD CANCELLED] Download cancelled by user.");
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
                    Logger.Warn("WFX", $"Failed to set LastWriteTimeUtc on '{localPath}': {ex.Message}");
                }
            }

            Logger.Info("WFX", $"[DOWNLOAD FINISHED] Successfully downloaded '{fileName}' -> '{localPath}'");

            if ((copyFlags & Win32Api.FS_COPYFLAGS_MOVE) != 0)
            {
                _db.MoveFileToTrash(fileRecord.Uid);
                Logger.Info("DB", $"[DB WRITE] Remote file '{fileName}' marked in_trash=1 per MOVE.");
                TriggerCheckpoint(immediate: true);
            }

            return Win32Api.FS_FILE_OK;
        }
        catch (OperationCanceledException)
        {
            Logger.Warn("WFX", "[DOWNLOAD CANCELLED] OperationCanceledException");
            return Win32Api.FS_FILE_USERABORT;
        }
        catch (FileNotFoundException ex)
        {
            Logger.Error("WFX", $"FsGetFile FileNotFound: {ex.Message}");
            return Win32Api.FS_FILE_NOTFOUND;
        }
        catch (Exception ex)
        {
            Logger.Error("WFX", "FsGetFile error", ex);
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
        try
        {
            string dir = Marshal.PtrToStringAnsi((IntPtr)remoteDir) ?? "";
            HandleStatusInfo(dir, infoStartEnd, infoOperation);
        }
        catch (Exception ex)
        {
            Logger.Error("WFX", $"Необработанное исключение в FsStatusInfo: {ex.Message}\n{ex.StackTrace}");
        }
    }

    // Оповещение о начале/завершении операций плагина (Unicode)
    [UnmanagedCallersOnly(EntryPoint = "FsStatusInfoW", CallConvs = [typeof(CallConvStdcall)])]
    public static void FsStatusInfoW(char* remoteDir, int infoStartEnd, int infoOperation)
    {
        try
        {
            string dir = Marshal.PtrToStringUni((IntPtr)remoteDir) ?? "";
            HandleStatusInfo(dir, infoStartEnd, infoOperation);
        }
        catch (Exception ex)
        {
            Logger.Error("WFX", $"Необработанное исключение в FsStatusInfoW: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private static void HandleStatusInfo(string remoteDir, int infoStartEnd, int infoOperation)
    {
        try
        {
            string opName = infoOperation switch
            {
                Win32Api.FS_STATUS_OP_LIST => "LIST_DIR",
                Win32Api.FS_STATUS_OP_GET_SINGLE => "GET_FILE",
                Win32Api.FS_STATUS_OP_GET_MULTI => "GET_MULTI",
                Win32Api.FS_STATUS_OP_PUT_SINGLE => "PUT_FILE",
                Win32Api.FS_STATUS_OP_PUT_MULTI => "PUT_MULTI",
                Win32Api.FS_STATUS_OP_RENMOV_SINGLE => "RENMOV",
                Win32Api.FS_STATUS_OP_RENMOV_MULTI => "RENMOV_MULTI",
                Win32Api.FS_STATUS_OP_DELETE => "DELETE",
                Win32Api.FS_STATUS_OP_ATTRIB => "ATTRIB",
                Win32Api.FS_STATUS_OP_MKDIR => "MKDIR",
                _ => $"OP_{infoOperation}"
            };

            string cleanDir = NormalizeVfsPath(remoteDir);
            string displayDir = string.IsNullOrEmpty(cleanDir) ? "\\" : $"\\{cleanDir}";

            if (infoStartEnd == Win32Api.FS_STATUS_START)
            {
                Logger.Info("WFX", $"[STATUS START] Operation: {opName} | Dir: '{displayDir}'");
            }
            else
            {
                Logger.Info("WFX", $"[STATUS END]   Operation: {opName} | Dir: '{displayDir}'");
            }

            bool isBatchOp = infoOperation == Win32Api.FS_STATUS_OP_GET_MULTI ||
                             infoOperation == Win32Api.FS_STATUS_OP_PUT_MULTI ||
                             infoOperation == Win32Api.FS_STATUS_OP_RENMOV_MULTI ||
                             infoOperation == Win32Api.FS_STATUS_OP_DELETE;

            if (infoStartEnd == Win32Api.FS_STATUS_START)
            {
                if (isBatchOp)
                {
                    _isBatchOperation = true;
                }
            }
            else if (infoStartEnd == Win32Api.FS_STATUS_END)
            {
                if (_isBatchOperation)
                {
                    _isBatchOperation = false;
                    if (infoOperation == Win32Api.FS_STATUS_OP_DELETE)
                    {
                        ProcessPendingTrashDeletions();
                    }
                    Logger.Info("DB", "[WAL CHECKPOINT] Batch operation completed. Executing SQLite checkpoint.");
                    TriggerCheckpoint(immediate: true);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("WFX", $"Ошибка в HandleStatusInfo: {ex.Message}\n{ex.StackTrace}");
        }
    }

    // Поддержка фонового копирования и очереди в Total Commander (ANSI)
    [UnmanagedCallersOnly(EntryPoint = "FsGetBackgroundFlags", CallConvs = [typeof(CallConvStdcall)])]
    public static int FsGetBackgroundFlags()
    {
        Logger.Debug("WFX", "FsGetBackgroundFlags: BG_DOWNLOAD | BG_UPLOAD | BG_ASK_USER");
        return Win32Api.BG_DOWNLOAD | Win32Api.BG_UPLOAD | Win32Api.BG_ASK_USER;
    }

    // Поддержка фонового копирования и очереди в Total Commander (Unicode)
    [UnmanagedCallersOnly(EntryPoint = "FsGetBackgroundFlagsW", CallConvs = [typeof(CallConvStdcall)])]
    public static int FsGetBackgroundFlagsW()
    {
        Logger.Debug("WFX", "FsGetBackgroundFlagsW: BG_DOWNLOAD | BG_UPLOAD | BG_ASK_USER");
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
        Logger.Info("WFX", $"[MKDIR START] Request: '{dirPath}'");
        if (_db == null) return 0; // false

        string cleanPath = NormalizeVfsPath(dirPath);
        if (string.IsNullOrEmpty(cleanPath)) return 0;

        ParseVfsPath(cleanPath, out string channelName, out string subPath, out bool isInTrash);

        if (isInTrash)
        {
            Logger.Warn("WFX", $"[MKDIR BLOCKED] Creating directories in Trash is prohibited: '{dirPath}'");
            System.Windows.Forms.MessageBox.Show(
                "Создание папок в Корзине запрещено.",
                "Операция заблокирована",
                System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Warning);
            return 0; // false
        }

        if (string.IsNullOrEmpty(channelName))
        {
            // Нельзя создать канал через FsMkDir без диалога
            return 0;
        }

        var mount = _db.GetMountByName(channelName);
        if (mount == null) return 0;

        int lastSlash = subPath.LastIndexOf('\\');
        string dirName = lastSlash >= 0 ? subPath.Substring(lastSlash + 1) : subPath;
        string? parent = lastSlash >= 0 ? subPath.Substring(0, lastSlash) : null;

        _db.EnsureParentDirectoriesExist(mount.Id, parent);
        _db.AddDirectoryRecord(mount.Id, dirName, parent);
        
        Logger.Info("DB", $"[DB WRITE / MKDIR] Directory '{dirName}' created in '{channelName}/{parent}'");
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
        Logger.Info("WFX", $"[DELETE FILE START] Request: '{remotePath}'");

        if (_db == null) return 0; // false

        string cleanPath = NormalizeVfsPath(remotePath);
        if (string.IsNullOrEmpty(cleanPath)) return 0;

        // Игнорируем и защищаем от удаления служебные триггеры
        if (cleanPath.Contains("[📁+] Создать папку") || cleanPath.Contains("[+] Создать папку") ||
            cleanPath.Contains("[❌] Удалить папку") || cleanPath.Contains("[-] Удалить папку") ||
            cleanPath.Contains("[⚙] Настройки") || cleanPath.Contains("[*] Настройки плагина"))
        {
            Logger.Warn("WFX", $"Protected trigger item, skipping deletion: '{cleanPath}'");
            return 0; // false
        }

        ParseVfsPath(cleanPath, out string channelName, out string subPath, out bool isInTrash);

        if (string.IsNullOrEmpty(channelName))
        {
            // Это корневая папка монтирования (канал) или корень корзины
            return HandleRemoveDir(cleanPath);
        }

        var mount = _db.GetMountByName(channelName);
        if (mount != null)
        {
            string fileName = Path.GetFileName(subPath);
            string? parentSubPath = Path.GetDirectoryName(subPath);
            if (string.IsNullOrEmpty(parentSubPath)) parentSubPath = null;

            if (isInTrash)
            {
                // Удаление элемента из корзины (удаление навсегда без дублирующего окна - пользователь уже подтвердил в Total Commander)
                var trashFile = _db.GetTrashFileByVersionedName(mount.Id, fileName, parentSubPath);
                if (trashFile != null)
                {
                    if (_isBatchOperation)
                    {
                        lock (_trashDeleteLock)
                        {
                            _pendingTrashDeletions.Add(new PendingTrashItem
                            {
                                MountId = mount.Id,
                                ChannelId = mount.ChannelId,
                                Uid = trashFile.Uid,
                                TgMessageId = trashFile.TgMessageId,
                                FileName = fileName
                            });
                        }
                        return 1;
                    }
                    else
                    {
                        PurgeTrashRecords(mount.Id, mount.ChannelId, new List<VfsDatabase.FileRecord> { trashFile });
                        return 1;
                    }
                }
                return 1;
            }
            else
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
                    Logger.Info("DB", $"[FILE DELETED / TRASH] '{subPath}' in channel '{channelName}' moved to trash.");
                    TriggerCheckpoint(immediate: true);
                    return 1; // true
                }
                else
                {
                    // Элемент уже удален или перемещен (например, в FsGetFile по флагу FS_COPYFLAGS_MOVE)
                    Logger.Info("DB", $"[FILE DELETED] '{subPath}' in channel '{channelName}' already marked deleted.");
                    return 1; // true (успех для Total Commander)
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
        Logger.Info("WFX", $"[REMOVEDIR START] Request: '{dirPath}'");

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

        ParseVfsPath(cleanPath, out string channelName, out string subPath, out bool isInTrash);

        if (string.IsNullOrEmpty(channelName))
        {
            // Это корневой каталог корзины [🗑] Корзина или попытка удалить его
            return 0; 
        }

        if (string.IsNullOrEmpty(subPath))
        {
            if (isInTrash)
            {
                // Попытка удалить всю корзину конкретного канала, например [🗑] Корзина\Channel
                var mount = _db.GetMountByName(channelName);
                if (mount != null)
                {
                    _db.GetTrashStats(mount.Id, out int filesCount, out int dirsCount, out long totalSize);
                    if (filesCount + dirsCount == 0)
                    {
                        return 1;
                    }

                    var trashRecords = _db.GetTrashFileRecords(mount.Id);
                    PurgeTrashRecords(mount.Id, mount.ChannelId, trashRecords);
                    return 1;
                }
            }
            else
            {
                // Это корневая папка монтирования (активный канал)
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
                            Logger.Info("DB", $"[FOLDER DELETED] Mount/channel '{channelName}' deleted via FsRemoveDir.");
                            Win32Api.RefreshActivePanel();
                            TriggerCheckpoint(immediate: true);
                        }).GetAwaiter().GetResult();

                        return 1; // true (успех)
                    }
                    return 0; // пользователь отменил
                }
            }
        }
        else
        {
            var mount = _db.GetMountByName(channelName);
            if (mount != null)
            {
                if (isInTrash)
                {
                    // Удаление подпапки ВНУТРИ корзины навсегда
                    var trashRecords = _db.GetTrashSubTreeFileRecords(mount.Id, subPath);
                    PurgeTrashRecords(mount.Id, mount.ChannelId, trashRecords);
                    return 1;
                }
                else
                {
                    _db.MoveDirectoryToTrash(mount.Id, subPath);
                    Logger.Info("DB", $"[FOLDER DELETED / TRASH] Virtual directory '{subPath}' in channel '{channelName}' moved to trash.");
                    TriggerCheckpoint(immediate: true);
                    return 1; // true
                }
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
        Logger.Info("WFX", $"[RENAME/MOVE START] '{oldPath}' -> '{newPath}' (isMove={isMove}, overwrite={overwrite})");
        if (_db == null) return Win32Api.FS_FILE_NOTFOUND;

        string cleanOld = NormalizeVfsPath(oldPath);
        string cleanNew = NormalizeVfsPath(newPath);
        if (string.IsNullOrEmpty(cleanOld) || string.IsNullOrEmpty(cleanNew))
            return Win32Api.FS_FILE_NOTFOUND;

        ParseVfsPath(cleanOld, out string oldChannel, out string oldSub, out bool oldIsInTrash);
        ParseVfsPath(cleanNew, out string newChannel, out string newSub, out bool newIsInTrash);

        if (string.IsNullOrEmpty(oldChannel) || string.IsNullOrEmpty(newChannel))
        {
            // Переименование/копирование корневого канала не поддерживается через FsRenMovFile
            return Win32Api.FS_FILE_NOTFOUND;
        }

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

        // Проверяем запись источника (с поддержкой элементов из Корзины)
        VfsDatabase.FileRecord? sourceRecord = null;
        if (oldIsInTrash)
        {
            sourceRecord = _db.GetTrashFileByVersionedName(oldMount.Id, oldItemName, oldParent);
        }
        else
        {
            sourceRecord = _db.GetFile(oldMount.Id, oldItemName, oldParent);
        }

        if (sourceRecord == null)
        {
            return Win32Api.FS_FILE_NOTFOUND;
        }

        if (newIsInTrash)
        {
            Logger.Warn("WFX", $"[RENMOV BLOCKED] Copying or moving to Trash is prohibited: '{newPath}'");
            System.Windows.Forms.MessageBox.Show(
                "Загрузка и копирование файлов в Корзину запрещены.\n\nДля удаления объектов используйте клавишу F8 / Delete.",
                "Операция заблокирована",
                System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Warning);
            return Win32Api.FS_FILE_NOTSUPPORTED;
        }

        if (oldIsInTrash && !newIsInTrash)
        {
            _db.RestoreFile(sourceRecord);

            // Имя восстанавливаемого файла должно быть чистым (без суффикса _v1)
            string cleanTargetName = sourceRecord.IsDir ? newItemName : sourceRecord.Name;

            _db.EnsureParentDirectoriesExist(newMount.Id, newParent);

            if (sourceRecord.IsDir)
            {
                _db.RenameMoveDirectory(oldMount.Id, oldSub, cleanTargetName, newParent, newMount.Id);
                Logger.Info("DB", $"[RESTORE MOVED] Restored folder '{sourceRecord.Name}' from Trash to '{newChannel}\\{newParent}'");
            }
            else
            {
                _db.RenameMoveFile(sourceRecord.Uid, cleanTargetName, newParent, newMount.Id);
                Logger.Info("DB", $"[RESTORE MOVED] Restored file '{sourceRecord.Name}' from Trash to '{newChannel}\\{newParent}'");
            }

            Win32Api.RefreshActivePanel();
            TriggerCheckpoint(immediate: true);
            return Win32Api.FS_FILE_OK;
        }

        // Проверяем на циклический путь, если это директория
        if (sourceRecord.IsDir && oldMount.Id == newMount.Id)
        {
            if (newSub.Equals(oldSub, StringComparison.OrdinalIgnoreCase) ||
                newSub.StartsWith(oldSub + "\\", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Warn("WFX", $"Cannot move/copy directory '{oldSub}' into itself or subfolder '{newSub}'.");
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
                    Logger.Info("DB", $"[FOLDER RENAMED/MOVED] Directory '{oldSub}' -> '{newSub}' in channel '{oldChannel}'");
                }
                else
                {
                    _db.RenameMoveFile(sourceRecord.Uid, newItemName, newParent, newMount.Id);
                    Logger.Info("DB", $"[FILE RENAMED/MOVED] File '{oldSub}' -> '{newSub}' in channel '{oldChannel}'");
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
                    Logger.Error("WFX", $"Error physically moving '{oldPath}' to '{newPath}': {ex.Message}");
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
                    Logger.Info("DB", $"[FOLDER COPIED] Directory '{oldSub}' -> '{newSub}' in channel '{oldChannel}'");
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
                    Logger.Info("DB", $"[FILE COPIED] File '{oldSub}' -> '{newSub}' in channel '{oldChannel}'");
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
                    Logger.Error("WFX", $"Error physically copying '{oldPath}' to '{newPath}': {ex.Message}");
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
            Logger.Info("WFX", $"[CROSS-CHANNEL COPY] Downloading '{fileRecord.Name}' from {oldMount.ChannelName} (MsgId: {fileRecord.TgMessageId})...");
            Task.Run(() => TelegramManager.DownloadFileAsync(oldMount.ChannelId, fileRecord.TgMessageId, tempPath)).GetAwaiter().GetResult();

            string relativeCaption = string.IsNullOrEmpty(newParent) ? newItemName : newParent + "\\" + newItemName;
            Logger.Info("WFX", $"[CROSS-CHANNEL COPY] Uploading '{newItemName}' to {newMount.ChannelName} ({newMount.ChannelId})...");
            int newMsgId = Task.Run(() => TelegramManager.UploadAndSendFileAsync(newMount.ChannelId, tempPath, newItemName, relativeCaption)).GetAwaiter().GetResult();

            Logger.Info("DB", $"[DB WRITE / CROSS-COPY] Creating record in mount={newMount.Id}, msg={newMsgId}");
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
            Logger.Info("WFX", $"[CROSS-CHANNEL MOVE] Downloading '{fileRecord.Name}' from {oldMount.ChannelName} (MsgId: {fileRecord.TgMessageId})...");
            Task.Run(() => TelegramManager.DownloadFileAsync(oldMount.ChannelId, fileRecord.TgMessageId, tempPath)).GetAwaiter().GetResult();

            string relativeCaption = string.IsNullOrEmpty(newParent) ? newItemName : newParent + "\\" + newItemName;
            Logger.Info("WFX", $"[CROSS-CHANNEL MOVE] Uploading '{newItemName}' to {newMount.ChannelName} ({newMount.ChannelId})...");
            int newMsgId = Task.Run(() => TelegramManager.UploadAndSendFileAsync(newMount.ChannelId, tempPath, newItemName, relativeCaption)).GetAwaiter().GetResult();

            Logger.Info("DB", $"[DB WRITE / CROSS-MOVE] Updating record UID={fileRecord.Uid} with new mount={newMount.Id}, msg={newMsgId}");
            _db!.UpdateFileMessageAndMount(fileRecord.Uid, newMount.Id, newMsgId, newItemName, newParent);

            Logger.Info("TG", $"[CROSS-CHANNEL MOVE] Deleting old msg {fileRecord.TgMessageId} from {oldMount.ChannelName}...");
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

    #region WFX Content Plugin API (Custom Columns & Tooltips)

    private static readonly (string Name, int Type)[] SupportedFields = new[]
    {
        ("Version", Win32Api.FT_NUMERIC_32),
        ("VersionName", Win32Api.FT_STRINGW),
        ("FileName", Win32Api.FT_STRINGW),
        ("TgMessageId", Win32Api.FT_NUMERIC_32),
        ("SourcePath", Win32Api.FT_STRINGW)
    };

    private static void CopyStringToPtrW(string src, char* dest, int maxLen)
    {
        if (dest == null || maxLen <= 0) return;
        int maxChars = maxLen;
        int copyLen = Math.Min(src.Length, maxChars - 1);
        for (int i = 0; i < copyLen; i++)
        {
            dest[i] = src[i];
        }
        dest[copyLen] = '\0';
    }

    private static void CopyStringToPtrA(string src, byte* dest, int maxLen)
    {
        if (dest == null || maxLen <= 0) return;
        byte[] bytes = System.Text.Encoding.Default.GetBytes(src);
        int copyLen = Math.Min(bytes.Length, maxLen - 1);
        for (int i = 0; i < copyLen; i++)
        {
            dest[i] = bytes[i];
        }
        dest[copyLen] = 0;
    }

    private static int WriteStringFieldValue(string text, void* fieldValue, int maxLen, bool isUnicode)
    {
        if (fieldValue == null || maxLen <= 0) return Win32Api.FT_FILEERROR;

        if (isUnicode)
        {
            CopyStringToPtrW(text, (char*)fieldValue, maxLen);
            return Win32Api.FT_STRINGW;
        }
        else
        {
            CopyStringToPtrA(text, (byte*)fieldValue, maxLen);
            return Win32Api.FT_STRING;
        }
    }

    private static int HandleContentGetValue(string rawPath, int fieldIndex, int unitIndex, void* fieldValue, int maxLen, int flags, bool isUnicode)
    {
        if (_db == null || string.IsNullOrWhiteSpace(rawPath))
        {
            return Win32Api.FT_FILEERROR;
        }

        string cleanPath = NormalizeVfsPath(rawPath);
        if (string.IsNullOrEmpty(cleanPath))
        {
            return Win32Api.FT_FIELDEMPTY;
        }

        ParseVfsPath(cleanPath, out string channelName, out string subPath, out bool isInTrash);

        if (string.IsNullOrEmpty(channelName))
        {
            return Win32Api.FT_FIELDEMPTY;
        }

        var mount = _db.GetMountByName(channelName);
        if (mount == null)
        {
            return Win32Api.FT_FILEERROR;
        }

        if (string.IsNullOrEmpty(subPath))
        {
            // Корневая папка канала
            if (fieldIndex == 2) // FileName
            {
                return WriteStringFieldValue(mount.ChannelName, fieldValue, maxLen, isUnicode);
            }
            return Win32Api.FT_FIELDEMPTY;
        }

        int lastSlash = subPath.LastIndexOf('\\');
        string itemName = lastSlash >= 0 ? subPath.Substring(lastSlash + 1) : subPath;
        string? parentSubPath = lastSlash >= 0 ? subPath.Substring(0, lastSlash) : null;

        VfsDatabase.FileRecord? record = null;
        if (isInTrash)
        {
            record = _db.GetTrashFileByVersionedName(mount.Id, itemName, parentSubPath);
        }
        else
        {
            record = _db.GetFile(mount.Id, itemName, parentSubPath);
        }

        if (record == null)
        {
            // Проверка на виртуальную директорию
            if (_db.ActiveFolderExists(mount.Id, subPath))
            {
                if (fieldIndex == 2) // FileName
                {
                    return WriteStringFieldValue(itemName, fieldValue, maxLen, isUnicode);
                }
                return Win32Api.FT_FIELDEMPTY;
            }
            return Win32Api.FT_FILEERROR;
        }

        if (record.IsDir)
        {
            if (fieldIndex == 2) // FileName
            {
                return WriteStringFieldValue(record.Name, fieldValue, maxLen, isUnicode);
            }
            return Win32Api.FT_FIELDEMPTY;
        }

        // Запрос метаданных файла
        switch (fieldIndex)
        {
            case 0: // Version (числовой)
                if (maxLen >= sizeof(int))
                {
                    *(int*)fieldValue = record.Ver;
                    return Win32Api.FT_NUMERIC_32;
                }
                return Win32Api.FT_FILEERROR;

            case 1: // VersionName ("v1", "v2"...)
                return WriteStringFieldValue($"v{record.Ver}", fieldValue, maxLen, isUnicode);

            case 2: // FileName (оригинальное имя)
                return WriteStringFieldValue(record.Name, fieldValue, maxLen, isUnicode);

            case 3: // TgMessageId
                if (maxLen >= sizeof(int))
                {
                    *(int*)fieldValue = record.TgMessageId;
                    return Win32Api.FT_NUMERIC_32;
                }
                return Win32Api.FT_FILEERROR;

            case 4: // SourcePath
                if (string.IsNullOrEmpty(record.SourcePath))
                {
                    return Win32Api.FT_FIELDEMPTY;
                }
                return WriteStringFieldValue(record.SourcePath, fieldValue, maxLen, isUnicode);

            default:
                return Win32Api.FT_NOSUCHFIELD;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "FsContentGetSupportedField", CallConvs = [typeof(CallConvStdcall)])]
    public static int FsContentGetSupportedField(int fieldIndex, byte* fieldName, byte* units, int maxLen)
    {
        if (fieldIndex < 0 || fieldIndex >= SupportedFields.Length)
        {
            return Win32Api.FT_NOMOREFIELDS;
        }

        var field = SupportedFields[fieldIndex];
        CopyStringToPtrA(field.Name, fieldName, maxLen);
        if (units != null && maxLen > 0) units[0] = 0;

        int type = field.Type == Win32Api.FT_STRINGW ? Win32Api.FT_STRING : field.Type;
        return type;
    }

    [UnmanagedCallersOnly(EntryPoint = "FsContentGetSupportedFieldW", CallConvs = [typeof(CallConvStdcall)])]
    public static int FsContentGetSupportedFieldW(int fieldIndex, char* fieldName, char* units, int maxLen)
    {
        if (fieldIndex < 0 || fieldIndex >= SupportedFields.Length)
        {
            return Win32Api.FT_NOMOREFIELDS;
        }

        var field = SupportedFields[fieldIndex];
        CopyStringToPtrW(field.Name, fieldName, maxLen);
        if (units != null && maxLen > 0) units[0] = '\0';

        return field.Type;
    }

    [UnmanagedCallersOnly(EntryPoint = "FsContentGetSupportedFieldFlags", CallConvs = [typeof(CallConvStdcall)])]
    public static int FsContentGetSupportedFieldFlags(int fieldIndex)
    {
        if (fieldIndex < 0 || fieldIndex >= SupportedFields.Length)
        {
            return 0;
        }
        return Win32Api.CONTFLAGS_OPTIONAL;
    }

    [UnmanagedCallersOnly(EntryPoint = "FsContentGetDefaultSortOrder", CallConvs = [typeof(CallConvStdcall)])]
    public static int FsContentGetDefaultSortOrder(int fieldIndex)
    {
        return 1; // По возрастанию (1..N)
    }

    [UnmanagedCallersOnly(EntryPoint = "FsContentGetValue", CallConvs = [typeof(CallConvStdcall)])]
    public static int FsContentGetValue(byte* fileName, int fieldIndex, int unitIndex, void* fieldValue, int maxLen, int flags)
    {
        string path = Marshal.PtrToStringAnsi((IntPtr)fileName) ?? "";
        return HandleContentGetValue(path, fieldIndex, unitIndex, fieldValue, maxLen, flags, isUnicode: false);
    }

    [UnmanagedCallersOnly(EntryPoint = "FsContentGetValueW", CallConvs = [typeof(CallConvStdcall)])]
    public static int FsContentGetValueW(char* fileName, int fieldIndex, int unitIndex, void* fieldValue, int maxLen, int flags)
    {
        string path = Marshal.PtrToStringUni((IntPtr)fileName) ?? "";
        return HandleContentGetValue(path, fieldIndex, unitIndex, fieldValue, maxLen, flags, isUnicode: true);
    }

    [UnmanagedCallersOnly(EntryPoint = "FsContentGetDefaultView", CallConvs = [typeof(CallConvStdcall)])]
    public static int FsContentGetDefaultView(byte* viewContents, byte* viewHeaders, byte* viewWidths, byte* viewOptions, int maxLen)
    {
        string contents = "[=<fs>.VersionName]\\n[=tc.size]\\n[=tc.writedate]";
        string headers = "Версия\\nРазмер\\nДата";
        string widths = "270,25,-15,-48,-60";
        string options = "-1|0";

        CopyStringToPtrA(contents, viewContents, maxLen);
        CopyStringToPtrA(headers, viewHeaders, maxLen);
        CopyStringToPtrA(widths, viewWidths, maxLen);
        CopyStringToPtrA(options, viewOptions, maxLen);
        return 1;
    }

    [UnmanagedCallersOnly(EntryPoint = "FsContentGetDefaultViewW", CallConvs = [typeof(CallConvStdcall)])]
    public static int FsContentGetDefaultViewW(char* viewContents, char* viewHeaders, char* viewWidths, char* viewOptions, int maxLen)
    {
        string contents = "[=<fs>.VersionName]\\n[=tc.size]\\n[=tc.writedate]";
        string headers = "Версия\\nРазмер\\nДата";
        string widths = "270,25,-15,-48,-60";
        string options = "-1|0";

        CopyStringToPtrW(contents, viewContents, maxLen);
        CopyStringToPtrW(headers, viewHeaders, maxLen);
        CopyStringToPtrW(widths, viewWidths, maxLen);
        CopyStringToPtrW(options, viewOptions, maxLen);
        return 1;
    }

    #endregion

    // Вызывается Total Commander при выгрузке плагина или закрытии программы
    [UnmanagedCallersOnly(EntryPoint = "FsContentPluginUnload", CallConvs = [typeof(CallConvStdcall)])]
    public static void FsContentPluginUnload()
    {
        Logger.Info("WFX", "FsContentPluginUnload called. Delegating to OnProcessExit.");
        OnProcessExit(null, EventArgs.Empty);
    }
}
