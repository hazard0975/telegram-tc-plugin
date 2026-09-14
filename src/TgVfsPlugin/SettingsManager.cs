using System;
using System.IO;
using System.Reflection;

namespace TgVfsPlugin;

public enum StorageMode
{
    DefaultAppData = 0, // %APPDATA%\TelegramVFS
    Portable = 1,       // <PluginDir>\Data
    Custom = 2          // Custom user folder
}

public static class SettingsManager
{
    private static string? _cachedPluginDir;
    private static string? _customDataPath;
    private static StorageMode _storageMode = StorageMode.DefaultAppData;

    public static string PluginDirectory
    {
        get
        {
            if (_cachedPluginDir == null)
            {
                try
                {
                    using var proc = System.Diagnostics.Process.GetCurrentProcess();
                    var module = proc.Modules.Cast<System.Diagnostics.ProcessModule>()
                        .FirstOrDefault(m => m.ModuleName != null && m.ModuleName.StartsWith("TgVfsPlugin", StringComparison.OrdinalIgnoreCase));

                    if (module != null && !string.IsNullOrEmpty(module.FileName))
                    {
                        _cachedPluginDir = Path.GetDirectoryName(module.FileName);
                    }
                }
                catch { }

                if (string.IsNullOrEmpty(_cachedPluginDir))
                {
                    _cachedPluginDir = AppContext.BaseDirectory;
                }
            }
            return _cachedPluginDir;
        }
    }

    public static string DefaultAppDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TelegramVFS");

    public static string PortableDirectory => Path.Combine(PluginDirectory, "Data");

    public static string GlobalSettingsFile => Path.Combine(DefaultAppDataDirectory, "settings.ini");
    public static string PortableSettingsFile => Path.Combine(PluginDirectory, "settings.ini");

    public static StorageMode CurrentStorageMode => _storageMode;

    /// <summary>
    /// Файл настроек, откуда реально загружена конфигурация
    /// </summary>
    public static string ActiveSettingsFile
    {
        get
        {
            if (_storageMode == StorageMode.Portable || File.Exists(PortableSettingsFile))
            {
                return PortableSettingsFile;
            }
            return GlobalSettingsFile;
        }
    }

    /// <summary>
    /// Текущая активная директория для данных (база SQLite, сессия, логи)
    /// </summary>
    public static string DataDirectory
    {
        get
        {
            switch (_storageMode)
            {
                case StorageMode.Portable:
                    return PortableDirectory;
                case StorageMode.Custom:
                    return !string.IsNullOrEmpty(_customDataPath) ? _customDataPath : DefaultAppDataDirectory;
                case StorageMode.DefaultAppData:
                default:
                    return DefaultAppDataDirectory;
            }
        }
    }

    public static string DbPath => Path.Combine(DataDirectory, "vfs_cache.db");
    public static string SessionPath => Path.Combine(DataDirectory, "WTelegram.session");
    public static string LogPath => Path.Combine(DataDirectory, "plugin_log.txt");

    static SettingsManager()
    {
        LoadSettings();
    }

    public static void LoadSettings()
    {
        try
        {
            // 1. Проверяем settings.ini рядом с DLL (Portable)
            string iniFile = File.Exists(PortableSettingsFile) ? PortableSettingsFile : GlobalSettingsFile;

            if (File.Exists(iniFile))
            {
                var lines = File.ReadAllLines(iniFile);
                string? storageModeStr = null;
                string? customPathStr = null;

                foreach (var line in lines)
                {
                    var parts = line.Split('=', 2);
                    if (parts.Length == 2)
                    {
                        var k = parts[0].Trim();
                        var v = parts[1].Trim();
                        if (k.Equals("storage_mode", StringComparison.OrdinalIgnoreCase)) storageModeStr = v;
                        if (k.Equals("data_path", StringComparison.OrdinalIgnoreCase)) customPathStr = v;
                    }
                }

                if (!string.IsNullOrEmpty(storageModeStr) && Enum.TryParse<StorageMode>(storageModeStr, true, out var mode))
                {
                    _storageMode = mode;
                }
                else if (File.Exists(PortableSettingsFile))
                {
                    _storageMode = StorageMode.Portable;
                }
                else
                {
                    _storageMode = StorageMode.DefaultAppData;
                }

                _customDataPath = customPathStr;
            }
            else
            {
                _storageMode = StorageMode.DefaultAppData;
                _customDataPath = null;
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"Error loading settings: {ex.Message}");
        }
    }

    public static string? GetSetting(string key)
    {
        try
        {
            string iniFile = ActiveSettingsFile;
            if (!File.Exists(iniFile))
            {
                // Fallback: если нет в активном, проверим глобальный
                if (File.Exists(GlobalSettingsFile)) iniFile = GlobalSettingsFile;
                else return null;
            }

            foreach (var line in File.ReadAllLines(iniFile))
            {
                var parts = line.Split('=', 2);
                if (parts.Length == 2 && parts[0].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    return parts[1].Trim();
                }
            }
        }
        catch { }
        return null;
    }

    public static void SaveSetting(string key, string value)
    {
        try
        {
            string targetIni = ActiveSettingsFile;
            Directory.CreateDirectory(Path.GetDirectoryName(targetIni)!);

            var lines = File.Exists(targetIni) ? File.ReadAllLines(targetIni).ToList() : new List<string>();
            bool found = false;

            for (int i = 0; i < lines.Count; i++)
            {
                var parts = lines[i].Split('=', 2);
                if (parts.Length >= 1 && parts[0].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = $"{key}={value}";
                    found = true;
                    break;
                }
            }

            if (!found) lines.Add($"{key}={value}");
            File.WriteAllLines(targetIni, lines);
        }
        catch (Exception ex)
        {
            Logger.Log($"Error saving setting '{key}': {ex.Message}");
        }
    }

    public static void SetStorageLocation(StorageMode mode, string? customPath)
    {
        _storageMode = mode;
        _customDataPath = customPath;

        // Сохраняем в активный INI
        SaveSetting("storage_mode", mode.ToString());
        if (!string.IsNullOrEmpty(customPath))
        {
            SaveSetting("data_path", customPath);
        }

        // Если включен Portable режим, создаем settings.ini рядом с DLL
        if (mode == StorageMode.Portable)
        {
            try
            {
                if (!File.Exists(PortableSettingsFile))
                {
                    File.WriteAllText(PortableSettingsFile, $"storage_mode=Portable\n");
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Перемещение всех файлов данных (.db, .session, .log) из старой директории в новую
    /// </summary>
    public static void MigrateDataFiles(string oldDir, string newDir)
    {
        if (string.Equals(oldDir.TrimEnd('\\', '/'), newDir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(newDir);
            Logger.Log($"Migrating data files from '{oldDir}' to '{newDir}'...");

            string[] filesToMigrate = new[]
            {
                "vfs_cache.db",
                "vfs_cache.db-wal",
                "vfs_cache.db-shm",
                "WTelegram.session",
                "plugin_log.txt",
                "settings.ini"
            };

            foreach (var file in filesToMigrate)
            {
                string src = Path.Combine(oldDir, file);
                string dst = Path.Combine(newDir, file);

                if (File.Exists(src))
                {
                    try
                    {
                        if (File.Exists(dst)) File.Delete(dst);
                        File.Move(src, dst);
                        Logger.Log($"Moved: {file} -> {dst}");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Warning: Failed to move file '{file}': {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"Error during MigrateDataFiles: {ex.Message}");
        }
    }
}
