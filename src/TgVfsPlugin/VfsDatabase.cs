using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Linq;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace TgVfsPlugin;

/// <summary>
/// Класс для работы с локальным кэшем иерархии файловой системы на базе SQLite.
/// </summary>
public class VfsDatabase : IDisposable
{
    private readonly SqliteConnection _connection;
    private bool _disposed;
    private static string DbPath => SettingsManager.DbPath;

    public VfsDatabase()
    {
        // Убедимся, что директория для БД существует
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);

        _connection = new SqliteConnection($"Data Source={DbPath}");
        _connection.Open();

        // Включаем WAL режим для параллельного чтения и записи
        using (var walCmd = _connection.CreateCommand())
        {
            walCmd.CommandText = "PRAGMA journal_mode=WAL;";
            walCmd.ExecuteNonQuery();
        }
        
        InitializeSchema();
    }

    private void InitializeSchema()
    {
        var command = _connection.CreateCommand();
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS mounts (
                id TEXT PRIMARY KEY,
                local_path TEXT,
                channel_id INTEGER,
                channel_name TEXT,
                mode INTEGER,
                created_at INTEGER
            );

            CREATE TABLE IF NOT EXISTS files (
                uid TEXT PRIMARY KEY,
                mount_id TEXT,
                isdir INTEGER,
                name TEXT NOT NULL,
                parent TEXT,
                mtime INTEGER,
                size INTEGER,
                tg_message_id INTEGER,
                in_trash INTEGER DEFAULT 0,
                ver INTEGER,
                FOREIGN KEY(mount_id) REFERENCES mounts(id)
            );

            CREATE INDEX IF NOT EXISTS idx_files_mount_parent ON files(mount_id, parent, in_trash);
        ";
        command.ExecuteNonQuery();

        // Миграция: если таблица files уже была создана ранее без колонки in_trash
        try
        {
            using var alterCmd = _connection.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE files ADD COLUMN in_trash INTEGER DEFAULT 0;";
            alterCmd.ExecuteNonQuery();
        }
        catch
        {
            // Колонка уже существует
        }

        // Автоматическая очистка дубликатов активных файлов
        CleanupDuplicateActiveFilesAll();
    }

    // Вспомогательный класс для представления элементов ФС
    public class VfsItem
    {
        public string Name { get; set; } = "";
        public bool IsDirectory { get; set; }
        public long Size { get; set; }
        public DateTime Date { get; set; }
    }

    public class MountInfo
    {
        public string Id { get; set; } = "";
        public string LocalPath { get; set; } = "";
        public long ChannelId { get; set; }
        public string ChannelName { get; set; } = "";
        public int Mode { get; set; }
    }

    private static DateTime ReadDateTime(SqliteDataReader reader, int index)
    {
        if (reader.IsDBNull(index)) return DateTime.UtcNow;
        object val = reader.GetValue(index);
        if (val is long longVal)
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(longVal).UtcDateTime;
        }
        if (val is string strVal && DateTime.TryParse(strVal, out DateTime dt))
        {
            return dt.ToUniversalTime();
        }
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(val)).UtcDateTime;
        }
        catch
        {
            return DateTime.UtcNow;
        }
    }

    public void AddMount(MountInfo mount)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO mounts (id, local_path, channel_id, channel_name, mode, created_at)
            VALUES (@id, @path, @cid, @cname, @mode, @dt)
        ";
        cmd.Parameters.AddWithValue("@id", mount.Id);
        cmd.Parameters.AddWithValue("@path", mount.LocalPath);
        cmd.Parameters.AddWithValue("@cid", mount.ChannelId);
        cmd.Parameters.AddWithValue("@cname", mount.ChannelName);
        cmd.Parameters.AddWithValue("@mode", mount.Mode);
        cmd.Parameters.AddWithValue("@dt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.ExecuteNonQuery();
    }

    public void DeleteMount(string mountId)
    {
        using (var cmdFiles = _connection.CreateCommand())
        {
            cmdFiles.CommandText = "DELETE FROM files WHERE mount_id = @mid";
            cmdFiles.Parameters.AddWithValue("@mid", mountId);
            cmdFiles.ExecuteNonQuery();
        }

        using (var cmdMount = _connection.CreateCommand())
        {
            cmdMount.CommandText = "DELETE FROM mounts WHERE id = @mid";
            cmdMount.Parameters.AddWithValue("@mid", mountId);
            cmdMount.ExecuteNonQuery();
        }
    }

    public MountInfo? GetMountByName(string name)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id, local_path, channel_id, mode, channel_name FROM mounts WHERE channel_name = @cname COLLATE NOCASE";
        cmd.Parameters.AddWithValue("@cname", name);
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            return new MountInfo
            {
                Id = reader.GetString(0),
                LocalPath = reader.GetString(1),
                ChannelId = reader.GetInt64(2),
                ChannelName = reader.GetString(4),
                Mode = reader.GetInt32(3)
            };
        }
        return null;
    }

    public System.Collections.Generic.List<VfsItem> GetMounts()
    {
        var items = new System.Collections.Generic.List<VfsItem>();
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT channel_name, created_at FROM mounts";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            items.Add(new VfsItem 
            { 
                Name = reader.GetString(0), 
                IsDirectory = true, 
                Size = 0,
                Date = ReadDateTime(reader, 1)
            });
        }
        return items;
    }

    public class FileRecord
    {
        public string Uid { get; set; } = "";
        public string MountId { get; set; } = "";
        public bool IsDir { get; set; }
        public string Name { get; set; } = "";
        public string? Parent { get; set; }
        public DateTime MTime { get; set; }
        public long Size { get; set; }
        public int TgMessageId { get; set; }
        public int InTrash { get; set; }
        public int Ver { get; set; } = 1;
    }

    public FileRecord? GetFile(string mountId, string fileName, string? parent = null)
    {
        var cmd = _connection.CreateCommand();
        string cleanParent = string.IsNullOrEmpty(parent) ? "" : parent.Trim('\\', '/').Replace('/', '\\');

        if (string.IsNullOrEmpty(cleanParent))
        {
            cmd.CommandText = @"
                SELECT uid, mount_id, isdir, name, parent, mtime, size, tg_message_id, in_trash, ver
                FROM files
                WHERE mount_id = @mid AND name = @name COLLATE NOCASE AND (parent IS NULL OR parent = '' OR parent = 'false') AND (in_trash IS NULL OR in_trash = 0)
                ORDER BY ver DESC, mtime DESC
                LIMIT 1
            ";
        }
        else
        {
            cmd.CommandText = @"
                SELECT uid, mount_id, isdir, name, parent, mtime, size, tg_message_id, in_trash, ver
                FROM files
                WHERE mount_id = @mid AND name = @name COLLATE NOCASE AND parent = @parent COLLATE NOCASE AND (in_trash IS NULL OR in_trash = 0)
                ORDER BY ver DESC, mtime DESC
                LIMIT 1
            ";
            cmd.Parameters.AddWithValue("@parent", cleanParent);
        }

        cmd.Parameters.AddWithValue("@mid", mountId);
        cmd.Parameters.AddWithValue("@name", fileName);

        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            return new FileRecord
            {
                Uid = reader.GetString(0),
                MountId = reader.GetString(1),
                IsDir = reader.GetInt32(2) == 1,
                Name = reader.GetString(3),
                Parent = reader.IsDBNull(4) ? null : reader.GetString(4),
                MTime = ReadDateTime(reader, 5),
                Size = reader.GetInt64(6),
                TgMessageId = reader.GetInt32(7),
                InTrash = reader.IsDBNull(8) ? 0 : reader.GetInt32(8),
                Ver = reader.IsDBNull(9) ? 1 : reader.GetInt32(9)
            };
        }
        return null;
    }

    public static string GetVersionedFileName(string fileName, int ver)
    {
        if (ver <= 0) ver = 1;
        string ext = Path.GetExtension(fileName);
        string nameNoExt = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrEmpty(nameNoExt)) nameNoExt = fileName;
        return $"{nameNoExt}_v{ver}{ext}";
    }

    public void MoveFileToTrash(string uid)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE files SET in_trash = 1 WHERE uid = @uid";
        cmd.Parameters.AddWithValue("@uid", uid);
        cmd.ExecuteNonQuery();
    }

    public List<FileRecord> GetTrashFiles(string mountId, string? parent = null)
    {
        var list = new List<FileRecord>();
        string cleanParent = string.IsNullOrEmpty(parent) ? "" : parent.Trim('\\', '/').Replace('/', '\\');

        // 1. Извлекаем все выбывшие файлы и явно удаленные папки для данного родительского пути
        var cmd = _connection.CreateCommand();
        if (string.IsNullOrEmpty(cleanParent))
        {
            cmd.CommandText = @"
                SELECT uid, mount_id, isdir, name, parent, mtime, size, tg_message_id, in_trash, ver
                FROM files
                WHERE mount_id = @mid AND in_trash = 1
                  AND (parent IS NULL OR parent = '' OR parent = 'false')
                ORDER BY isdir DESC, ver DESC, mtime DESC";
        }
        else
        {
            cmd.CommandText = @"
                SELECT uid, mount_id, isdir, name, parent, mtime, size, tg_message_id, in_trash, ver
                FROM files
                WHERE mount_id = @mid AND in_trash = 1
                  AND parent = @parent COLLATE NOCASE
                ORDER BY isdir DESC, ver DESC, mtime DESC";
            cmd.Parameters.AddWithValue("@parent", cleanParent);
        }
        cmd.Parameters.AddWithValue("@mid", mountId);

        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                list.Add(new FileRecord
                {
                    Uid = reader.GetString(0),
                    MountId = reader.GetString(1),
                    IsDir = reader.GetInt32(2) == 1,
                    Name = reader.GetString(3),
                    Parent = reader.IsDBNull(4) ? null : reader.GetString(4),
                    MTime = ReadDateTime(reader, 5),
                    Size = reader.GetInt64(6),
                    TgMessageId = reader.GetInt32(7),
                    InTrash = reader.GetInt32(8),
                    Ver = reader.IsDBNull(9) ? 1 : reader.GetInt32(9)
                });
            }
        }

        // 2. Ищем родительские виртуальные папки, в которых находятся удаленные подфайлы
        var existingDirNames = new HashSet<string>(
            list.Where(x => x.IsDir).Select(x => x.Name),
            StringComparer.OrdinalIgnoreCase
        );

        using (var cmdSubDirs = _connection.CreateCommand())
        {
            if (string.IsNullOrEmpty(cleanParent))
            {
                cmdSubDirs.CommandText = @"
                    SELECT DISTINCT parent
                    FROM files
                    WHERE mount_id = @mid AND in_trash = 1
                      AND (parent IS NOT NULL AND parent != '' AND parent != 'false')";
            }
            else
            {
                cmdSubDirs.CommandText = @"
                    SELECT DISTINCT parent
                    FROM files
                    WHERE mount_id = @mid AND in_trash = 1
                      AND (parent LIKE @parentPrefix COLLATE NOCASE)";
                cmdSubDirs.Parameters.AddWithValue("@parentPrefix", cleanParent + "\\%");
            }
            cmdSubDirs.Parameters.AddWithValue("@mid", mountId);

            using var reader = cmdSubDirs.ExecuteReader();
            while (reader.Read())
            {
                string p = reader.GetString(0);
                string relativePath = string.IsNullOrEmpty(cleanParent)
                    ? p
                    : p.Substring(cleanParent.Length).TrimStart('\\');

                int slashIdx = relativePath.IndexOf('\\');
                string immediateDir = slashIdx >= 0 ? relativePath.Substring(0, slashIdx) : relativePath;

                if (!string.IsNullOrEmpty(immediateDir) && existingDirNames.Add(immediateDir))
                {
                    list.Insert(0, new FileRecord
                    {
                        Uid = Guid.NewGuid().ToString("N"),
                        MountId = mountId,
                        IsDir = true,
                        Name = immediateDir,
                        Parent = string.IsNullOrEmpty(cleanParent) ? null : cleanParent,
                        MTime = DateTime.UtcNow,
                        Size = 0,
                        TgMessageId = 0,
                        InTrash = 1,
                        Ver = 1
                    });
                }
            }
        }

        return list;
    }

    public FileRecord? GetTrashFileByVersionedName(string mountId, string versionedName, string? parent = null)
    {
        var trashFiles = GetTrashFiles(mountId, parent);

        // 1. Проход 1: Точное совпадение по версионному имени (vName = "plugin_log_v2.txt") или имени папки
        foreach (var f in trashFiles)
        {
            if (f.IsDir)
            {
                if (f.Name.Equals(versionedName, StringComparison.OrdinalIgnoreCase))
                    return f;
            }
            else
            {
                string vName = GetVersionedFileName(f.Name, f.Ver);
                if (vName.Equals(versionedName, StringComparison.OrdinalIgnoreCase))
                {
                    return f;
                }
            }
        }

        // 2. Проход 2: Fallback — если запрошено чистое имя без версии "plugin_log.txt", берём первое совпадение
        foreach (var f in trashFiles)
        {
            if (!f.IsDir && f.Name.Equals(versionedName, StringComparison.OrdinalIgnoreCase))
            {
                return f;
            }
        }

        // 3. Fallback 2: Поиск среди всех элементов Корзины канала
        if (!string.IsNullOrEmpty(parent))
        {
            var allTrash = GetTrashFileRecords(mountId);
            foreach (var f in allTrash)
            {
                if (f.IsDir)
                {
                    if (f.Name.Equals(versionedName, StringComparison.OrdinalIgnoreCase))
                        return f;
                }
                else
                {
                    string vName = GetVersionedFileName(f.Name, f.Ver);
                    if (vName.Equals(versionedName, StringComparison.OrdinalIgnoreCase))
                    {
                        return f;
                    }
                }
            }
            foreach (var f in allTrash)
            {
                if (!f.IsDir && f.Name.Equals(versionedName, StringComparison.OrdinalIgnoreCase))
                {
                    return f;
                }
            }
        }

        return null;
    }

    public List<int> DeleteTrashSubTree(string mountId, string subPath)
    {
        string cleanPath = subPath.Trim('\\', '/').Replace('/', '\\');
        int lastSlash = cleanPath.LastIndexOf('\\');
        string dirName = lastSlash >= 0 ? cleanPath.Substring(lastSlash + 1) : cleanPath;
        string? parent = lastSlash >= 0 ? cleanPath.Substring(0, lastSlash) : null;

        var msgIds = new List<int>();

        using (var cmdSelect = _connection.CreateCommand())
        {
            cmdSelect.CommandText = @"
                SELECT tg_message_id FROM files 
                WHERE mount_id = @mid AND in_trash = 1 
                  AND (parent = @exactPath OR parent LIKE @prefixPath OR (name = @dirName COLLATE NOCASE AND (parent = @parent OR (@parent IS NULL AND (parent IS NULL OR parent = '')))))
                  AND tg_message_id > 0
            ";
            cmdSelect.Parameters.AddWithValue("@mid", mountId);
            cmdSelect.Parameters.AddWithValue("@exactPath", cleanPath);
            cmdSelect.Parameters.AddWithValue("@prefixPath", cleanPath + "\\%");
            cmdSelect.Parameters.AddWithValue("@dirName", dirName);
            cmdSelect.Parameters.AddWithValue("@parent", (object?)parent ?? DBNull.Value);
            using var reader = cmdSelect.ExecuteReader();
            while (reader.Read())
            {
                msgIds.Add(reader.GetInt32(0));
            }
        }

        using (var cmdDelete = _connection.CreateCommand())
        {
            cmdDelete.CommandText = @"
                DELETE FROM files 
                WHERE mount_id = @mid AND in_trash = 1 
                  AND (parent = @exactPath OR parent LIKE @prefixPath OR (name = @dirName COLLATE NOCASE AND (parent = @parent OR (@parent IS NULL AND (parent IS NULL OR parent = '')))))
            ";
            cmdDelete.Parameters.AddWithValue("@mid", mountId);
            cmdDelete.Parameters.AddWithValue("@exactPath", cleanPath);
            cmdDelete.Parameters.AddWithValue("@prefixPath", cleanPath + "\\%");
            cmdDelete.Parameters.AddWithValue("@dirName", dirName);
            cmdDelete.Parameters.AddWithValue("@parent", (object?)parent ?? DBNull.Value);
            cmdDelete.ExecuteNonQuery();
        }

        return msgIds;
    }

    public void GetTrashStats(string mountId, out int filesCount, out int dirsCount, out long totalSize)
    {
        filesCount = 0;
        dirsCount = 0;
        totalSize = 0;
        var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            SELECT 
                COUNT(CASE WHEN isdir = 0 THEN 1 END),
                COUNT(CASE WHEN isdir = 1 THEN 1 END),
                COALESCE(SUM(size), 0)
            FROM files
            WHERE mount_id = @mid AND in_trash = 1
        ";
        cmd.Parameters.AddWithValue("@mid", mountId);
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            filesCount = reader.GetInt32(0);
            dirsCount = reader.GetInt32(1);
            totalSize = reader.GetInt64(2);
        }
    }

    public void GetDirectoryStats(string mountId, string dirSubPath, bool inTrash, out int filesCount, out int dirsCount, out long totalSize)
    {
        filesCount = 0;
        dirsCount = 0;
        totalSize = 0;

        string normalizedDir = dirSubPath.Replace('/', '\\').Trim('\\');
        string prefixPattern = string.IsNullOrEmpty(normalizedDir) 
            ? "%" 
            : normalizedDir + "\\%";

        var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            SELECT 
                COUNT(CASE WHEN isdir = 0 THEN 1 END),
                COUNT(CASE WHEN isdir = 1 THEN 1 END),
                COALESCE(SUM(size), 0)
            FROM files
            WHERE mount_id = @mid 
              AND (parent = @dir OR parent LIKE @prefix)
              AND in_trash = @trash
        ";
        cmd.Parameters.AddWithValue("@mid", mountId);
        cmd.Parameters.AddWithValue("@dir", normalizedDir);
        cmd.Parameters.AddWithValue("@prefix", prefixPattern);
        cmd.Parameters.AddWithValue("@trash", inTrash ? 1 : 0);

        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            filesCount = reader.GetInt32(0);
            dirsCount = reader.GetInt32(1);
            totalSize = reader.GetInt64(2);
        }
    }

    public FileRecord? GetFileByUid(string uid)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            SELECT uid, mount_id, isdir, name, parent, mtime, size, tg_message_id, in_trash, ver
            FROM files
            WHERE uid = @uid
            LIMIT 1
        ";
        cmd.Parameters.AddWithValue("@uid", uid);
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            return new FileRecord
            {
                Uid = reader.GetString(0),
                MountId = reader.GetString(1),
                IsDir = reader.GetInt32(2) == 1,
                Name = reader.GetString(3),
                Parent = reader.IsDBNull(4) ? null : reader.GetString(4),
                MTime = ReadDateTime(reader, 5),
                Size = reader.GetInt64(6),
                TgMessageId = reader.GetInt32(7),
                InTrash = reader.GetInt32(8),
                Ver = reader.IsDBNull(9) ? 1 : reader.GetInt32(9)
            };
        }
        return null;
    }

    public bool RestoreFile(string uid)
    {
        var file = GetFileByUid(uid);
        if (file == null)
        {
            Logger.Warn("DB", $"[RESTORE WARN] Item with UID '{uid}' not found in SQLite.");
            return false;
        }

        EnsureParentDirectoriesExist(file.MountId, file.Parent);

        var activeConflict = GetFile(file.MountId, file.Name, file.Parent);
        if (activeConflict != null && activeConflict.Uid != uid)
        {
            MoveFileToTrash(activeConflict.Uid);
        }

        var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE files SET in_trash = 0 WHERE uid = @uid";
        cmd.Parameters.AddWithValue("@uid", uid);
        cmd.ExecuteNonQuery();

        if (file.IsDir)
        {
            string dirSubPath = string.IsNullOrEmpty(file.Parent) 
                ? file.Name 
                : file.Parent.Trim('\\', '/') + "\\" + file.Name;

            using var cmdSub = _connection.CreateCommand();
            cmdSub.CommandText = @"
                UPDATE files 
                SET in_trash = 0 
                WHERE uid IN (
                    SELECT uid FROM (
                        SELECT uid, ROW_NUMBER() OVER (PARTITION BY parent, name ORDER BY isdir DESC, ver DESC, mtime DESC) as rn
                        FROM files
                        WHERE mount_id = @mid 
                          AND (parent = @exactPath OR parent LIKE @prefixPath)
                          AND in_trash = 1
                    ) WHERE rn = 1
                )
            ";
            cmdSub.Parameters.AddWithValue("@mid", file.MountId);
            cmdSub.Parameters.AddWithValue("@exactPath", dirSubPath);
            cmdSub.Parameters.AddWithValue("@prefixPath", dirSubPath + "\\%");
            int nestedRestored = cmdSub.ExecuteNonQuery();

            Logger.Info("DB", $"[RESTORE OK] Restored folder '{file.Name}' and {nestedRestored} nested item(s) from Trash");
        }
        else
        {
            Logger.Info("DB", $"[RESTORE OK] Restored file '{file.Name}' from Trash");
        }

        CleanupDuplicateActiveFiles(file.MountId);

        return true;
    }

    public void CleanupDuplicateActiveFiles(string mountId)
    {
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                UPDATE files 
                SET in_trash = 1 
                WHERE mount_id = @mid 
                  AND uid IN (
                      SELECT uid FROM (
                          SELECT uid, ROW_NUMBER() OVER (PARTITION BY parent, name ORDER BY isdir DESC, ver DESC, mtime DESC) as rn
                          FROM files
                          WHERE mount_id = @mid AND (in_trash IS NULL OR in_trash = 0)
                      ) WHERE rn > 1
                  );
            ";
            cmd.Parameters.AddWithValue("@mid", mountId);
            int cleaned = cmd.ExecuteNonQuery();
            if (cleaned > 0)
            {
                Logger.Info("DB", $"[CLEANUP] Moved {cleaned} duplicate active file(s) back to Trash in mount '{mountId}'.");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("DB", $"CleanupDuplicateActiveFiles error: {ex.Message}");
        }
    }

    public void CleanupDuplicateActiveFilesAll()
    {
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                UPDATE files 
                SET in_trash = 1 
                WHERE uid IN (
                    SELECT uid FROM (
                        SELECT uid, ROW_NUMBER() OVER (PARTITION BY parent, name ORDER BY isdir DESC, ver DESC, mtime DESC) as rn
                        FROM files
                        WHERE in_trash IS NULL OR in_trash = 0
                    ) WHERE rn > 1
                );
            ";
            int cleaned = cmd.ExecuteNonQuery();
            if (cleaned > 0)
            {
                Logger.Info("DB", $"[CLEANUP] Cleaned {cleaned} duplicate active file(s) across all mounts.");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("DB", $"CleanupDuplicateActiveFilesAll error: {ex.Message}");
        }
    }

    public void DeleteFilePermanently(string uid)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM files WHERE uid = @uid";
        cmd.Parameters.AddWithValue("@uid", uid);
        cmd.ExecuteNonQuery();
    }

    public List<FileRecord> GetTrashFileRecords(string mountId)
    {
        var list = new List<FileRecord>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            SELECT uid, mount_id, isdir, name, parent, mtime, size, tg_message_id, in_trash, ver
            FROM files
            WHERE mount_id = @mid AND in_trash = 1
            ORDER BY isdir DESC, ver DESC, mtime DESC";
        cmd.Parameters.AddWithValue("@mid", mountId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new FileRecord
            {
                Uid = reader.GetString(0),
                MountId = reader.GetString(1),
                IsDir = reader.GetInt32(2) == 1,
                Name = reader.GetString(3),
                Parent = reader.IsDBNull(4) ? null : reader.GetString(4),
                MTime = ReadDateTime(reader, 5),
                Size = reader.GetInt64(6),
                TgMessageId = reader.GetInt32(7),
                InTrash = reader.IsDBNull(8) ? 0 : reader.GetInt32(8),
                Ver = reader.IsDBNull(9) ? 1 : reader.GetInt32(9)
            });
        }
        return list;
    }

    public List<FileRecord> GetTrashSubTreeFileRecords(string mountId, string trashSubPath)
    {
        string cleanPath = trashSubPath.Trim('\\', '/').Replace('/', '\\');
        if (string.IsNullOrEmpty(cleanPath)) return GetTrashFileRecords(mountId);

        int lastSlash = cleanPath.LastIndexOf('\\');
        string dirName = lastSlash >= 0 ? cleanPath.Substring(lastSlash + 1) : cleanPath;
        string? parent = lastSlash >= 0 ? cleanPath.Substring(0, lastSlash) : null;

        var list = new List<FileRecord>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            SELECT uid, mount_id, isdir, name, parent, mtime, size, tg_message_id, in_trash, ver
            FROM files
            WHERE mount_id = @mid 
              AND in_trash = 1
              AND (
                  (name = @dirName COLLATE NOCASE AND (parent = @parent OR (@parent IS NULL AND (parent IS NULL OR parent = ''))))
                  OR parent = @cleanPath
                  OR parent LIKE @prefixPath
              )";
        cmd.Parameters.AddWithValue("@mid", mountId);
        cmd.Parameters.AddWithValue("@dirName", dirName);
        cmd.Parameters.AddWithValue("@parent", (object?)parent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@cleanPath", cleanPath);
        cmd.Parameters.AddWithValue("@prefixPath", cleanPath + "\\%");

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new FileRecord
            {
                Uid = reader.GetString(0),
                MountId = reader.GetString(1),
                IsDir = reader.GetInt32(2) == 1,
                Name = reader.GetString(3),
                Parent = reader.IsDBNull(4) ? null : reader.GetString(4),
                MTime = ReadDateTime(reader, 5),
                Size = reader.GetInt64(6),
                TgMessageId = reader.GetInt32(7),
                InTrash = reader.IsDBNull(8) ? 0 : reader.GetInt32(8),
                Ver = reader.IsDBNull(9) ? 1 : reader.GetInt32(9)
            });
        }
        return list;
    }

    public void DeleteFilesByTgMessageIds(string mountId, IEnumerable<int> messageIds)
    {
        if (messageIds == null) return;
        var list = messageIds.Where(id => id > 0).Distinct().ToList();
        if (list.Count == 0) return;

        using var transaction = _connection.BeginTransaction();
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = transaction;

        for (int i = 0; i < list.Count; i += 500)
        {
            var chunk = list.Skip(i).Take(500).ToList();
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("@mid", mountId);

            var paramNames = new List<string>();
            for (int j = 0; j < chunk.Count; j++)
            {
                string pName = "@msg" + j;
                paramNames.Add(pName);
                cmd.Parameters.AddWithValue(pName, chunk[j]);
            }

            cmd.CommandText = $"DELETE FROM files WHERE mount_id = @mid AND tg_message_id IN ({string.Join(", ", paramNames)})";
            cmd.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void DeleteFilesByUids(IEnumerable<string> uids)
    {
        if (uids == null) return;
        var list = uids.Where(u => !string.IsNullOrEmpty(u)).Distinct().ToList();
        if (list.Count == 0) return;

        using var transaction = _connection.BeginTransaction();
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = transaction;

        for (int i = 0; i < list.Count; i += 500)
        {
            var chunk = list.Skip(i).Take(500).ToList();
            cmd.Parameters.Clear();

            var paramNames = new List<string>();
            for (int j = 0; j < chunk.Count; j++)
            {
                string pName = "@u" + j;
                paramNames.Add(pName);
                cmd.Parameters.AddWithValue(pName, chunk[j]);
            }

            cmd.CommandText = $"DELETE FROM files WHERE uid IN ({string.Join(", ", paramNames)})";
            cmd.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void DeleteEmptyTrashDirectories(string mountId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM files WHERE mount_id = @mid AND in_trash = 1 AND isdir = 1";
        cmd.Parameters.AddWithValue("@mid", mountId);
        cmd.ExecuteNonQuery();
    }

    public List<int> EmptyTrash(string mountId)
    {
        var msgIds = new List<int>();
        using (var cmdSelect = _connection.CreateCommand())
        {
            cmdSelect.CommandText = "SELECT tg_message_id FROM files WHERE mount_id = @mid AND in_trash = 1 AND tg_message_id > 0";
            cmdSelect.Parameters.AddWithValue("@mid", mountId);
            using var reader = cmdSelect.ExecuteReader();
            while (reader.Read())
            {
                msgIds.Add(reader.GetInt32(0));
            }
        }

        using (var cmdDelete = _connection.CreateCommand())
        {
            cmdDelete.CommandText = "DELETE FROM files WHERE mount_id = @mid AND in_trash = 1";
            cmdDelete.Parameters.AddWithValue("@mid", mountId);
            cmdDelete.ExecuteNonQuery();
        }

        return msgIds;
    }

    public List<FileRecord> GetSubTreeItems(string mountId, string subPath)
    {
        string cleanPath = subPath.Trim('\\', '/').Replace('/', '\\');
        var list = new List<FileRecord>();
        var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            SELECT uid, mount_id, isdir, name, parent, mtime, size, tg_message_id, in_trash, ver
            FROM files
            WHERE mount_id = @mid 
              AND (parent = @exactPath OR parent LIKE @prefixPath)
              AND (in_trash IS NULL OR in_trash = 0)
        ";
        cmd.Parameters.AddWithValue("@mid", mountId);
        cmd.Parameters.AddWithValue("@exactPath", cleanPath);
        cmd.Parameters.AddWithValue("@prefixPath", cleanPath + "\\%");

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new FileRecord
            {
                Uid = reader.GetString(0),
                MountId = reader.GetString(1),
                IsDir = reader.GetInt32(2) == 1,
                Name = reader.GetString(3),
                Parent = reader.IsDBNull(4) ? null : reader.GetString(4),
                MTime = ReadDateTime(reader, 5),
                Size = reader.GetInt64(6),
                TgMessageId = reader.GetInt32(7),
                InTrash = reader.IsDBNull(8) ? 0 : reader.GetInt32(8),
                Ver = reader.IsDBNull(9) ? 1 : reader.GetInt32(9)
            });
        }
        return list;
    }

    public void MoveDirectoryToTrash(string mountId, string dirRelativePath)
    {
        string cleanPath = dirRelativePath.Trim('\\', '/').Replace('/', '\\');
        if (string.IsNullOrEmpty(cleanPath)) return;

        int lastSlash = cleanPath.LastIndexOf('\\');
        string dirName = lastSlash >= 0 ? cleanPath.Substring(lastSlash + 1) : cleanPath;
        string? parent = lastSlash >= 0 ? cleanPath.Substring(0, lastSlash) : null;

        var dirRecord = GetFile(mountId, dirName, parent);
        if (dirRecord != null)
        {
            MoveFileToTrash(dirRecord.Uid);
        }

        var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            UPDATE files 
            SET in_trash = 1 
            WHERE mount_id = @mid 
              AND (parent = @exactPath OR parent LIKE @prefixPath)
        ";
        cmd.Parameters.AddWithValue("@mid", mountId);
        cmd.Parameters.AddWithValue("@exactPath", cleanPath);
        cmd.Parameters.AddWithValue("@prefixPath", cleanPath + "\\%");
        cmd.ExecuteNonQuery();
    }

    public void AddFile(FileRecord file)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO files (uid, mount_id, isdir, name, parent, mtime, size, tg_message_id, in_trash, ver)
            VALUES (@uid, @mid, @isdir, @name, @parent, @mtime, @size, @msgid, @trash, @ver)
        ";
        cmd.Parameters.AddWithValue("@uid", file.Uid);
        cmd.Parameters.AddWithValue("@mid", file.MountId);
        cmd.Parameters.AddWithValue("@isdir", file.IsDir ? 1 : 0);
        cmd.Parameters.AddWithValue("@name", file.Name);
        cmd.Parameters.AddWithValue("@parent", (object?)file.Parent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@mtime", new DateTimeOffset(file.MTime.ToUniversalTime()).ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("@size", file.Size);
        cmd.Parameters.AddWithValue("@msgid", file.TgMessageId);
        cmd.Parameters.AddWithValue("@trash", file.InTrash);
        cmd.Parameters.AddWithValue("@ver", file.Ver);
        cmd.ExecuteNonQuery();
    }

    public void UpdateFileMessageAndMount(string uid, string newMountId, int newTgMessageId, string newName, string? newParent)
    {
        string cleanParent = string.IsNullOrEmpty(newParent) ? "" : newParent.Trim('\\', '/').Replace('/', '\\');
        string? parentValue = string.IsNullOrEmpty(cleanParent) ? null : cleanParent;

        var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            UPDATE files
            SET mount_id = @newMountId,
                tg_message_id = @newMsgId,
                name = @name,
                parent = @parent
            WHERE uid = @uid
        ";
        cmd.Parameters.AddWithValue("@newMountId", newMountId);
        cmd.Parameters.AddWithValue("@newMsgId", newTgMessageId);
        cmd.Parameters.AddWithValue("@name", newName);
        cmd.Parameters.AddWithValue("@parent", (object?)parentValue ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@uid", uid);
        cmd.ExecuteNonQuery();
    }

    public void RenameMoveFile(string uid, string newName, string? newParent, string? newMountId)
    {
        string cleanParent = string.IsNullOrEmpty(newParent) ? "" : newParent.Trim('\\', '/').Replace('/', '\\');
        string? parentValue = string.IsNullOrEmpty(cleanParent) ? null : cleanParent;

        var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            UPDATE files
            SET name = @name,
                parent = @parent,
                mount_id = COALESCE(@newMountId, mount_id)
            WHERE uid = @uid
        ";
        cmd.Parameters.AddWithValue("@name", newName);
        cmd.Parameters.AddWithValue("@parent", (object?)parentValue ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@newMountId", (object?)newMountId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@uid", uid);
        cmd.ExecuteNonQuery();
    }

    public void RenameMoveDirectory(string oldMountId, string oldSubPath, string newName, string? newParent, string? newMountId)
    {
        string cleanOld = oldSubPath.Trim('\\', '/').Replace('/', '\\');
        string cleanNewParent = string.IsNullOrEmpty(newParent) ? "" : newParent.Trim('\\', '/').Replace('/', '\\');
        string newSubPath = string.IsNullOrEmpty(cleanNewParent) ? newName : cleanNewParent + "\\" + newName;
        string targetMountId = newMountId ?? oldMountId;

        int lastSlash = cleanOld.LastIndexOf('\\');
        string oldDirName = lastSlash >= 0 ? cleanOld.Substring(lastSlash + 1) : cleanOld;
        string? oldParent = lastSlash >= 0 ? cleanOld.Substring(0, lastSlash) : null;

        var dirRecord = GetFile(oldMountId, oldDirName, oldParent);
        if (dirRecord != null)
        {
            RenameMoveFile(dirRecord.Uid, newName, newParent, targetMountId);
        }

        var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            UPDATE files 
            SET parent = CASE 
                    WHEN parent = @exactOld THEN @newSubPath
                    ELSE @newSubPath || SUBSTR(parent, LENGTH(@exactOld) + 1)
                END,
                mount_id = @targetMountId
            WHERE mount_id = @oldMountId 
              AND (parent = @exactOld OR parent LIKE @prefixOldEscaped)
        ";
        cmd.Parameters.AddWithValue("@exactOld", cleanOld);
        cmd.Parameters.AddWithValue("@newSubPath", newSubPath);
        cmd.Parameters.AddWithValue("@prefixOldEscaped", cleanOld + "\\%");
        cmd.Parameters.AddWithValue("@targetMountId", targetMountId);
        cmd.Parameters.AddWithValue("@oldMountId", oldMountId);
        cmd.ExecuteNonQuery();
    }

    public void AddDirectoryRecord(string mountId, string dirName, string? parent)
    {
        string cleanParent = string.IsNullOrEmpty(parent) ? "" : parent.Trim('\\', '/').Replace('/', '\\');
        string? parentValue = string.IsNullOrEmpty(cleanParent) ? null : cleanParent;

        var existing = GetFile(mountId, dirName, parentValue);
        if (existing != null)
        {
            if (existing.InTrash == 1)
            {
                var cmdRestore = _connection.CreateCommand();
                cmdRestore.CommandText = "UPDATE files SET in_trash = 0 WHERE uid = @uid";
                cmdRestore.Parameters.AddWithValue("@uid", existing.Uid);
                cmdRestore.ExecuteNonQuery();
            }
            return;
        }

        AddFile(new FileRecord
        {
            Uid = Guid.NewGuid().ToString("N"),
            MountId = mountId,
            IsDir = true,
            Name = dirName,
            Parent = parentValue,
            MTime = DateTime.UtcNow,
            Size = 0,
            TgMessageId = 0,
            InTrash = 0,
            Ver = 1
        });
    }

    public void EnsureParentDirectoriesExist(string mountId, string? parentPath)
    {
        if (string.IsNullOrEmpty(parentPath)) return;
        string cleanPath = parentPath.Trim('\\', '/').Replace('/', '\\');
        if (string.IsNullOrEmpty(cleanPath)) return;

        string[] parts = cleanPath.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
        string currentParent = "";
        for (int i = 0; i < parts.Length; i++)
        {
            string dirName = parts[i];
            string? parentOfCurrent = string.IsNullOrEmpty(currentParent) ? null : currentParent;
            AddDirectoryRecord(mountId, dirName, parentOfCurrent);
            currentParent = string.IsNullOrEmpty(currentParent) ? dirName : currentParent + "\\" + dirName;
        }
    }

    public System.Collections.Generic.List<VfsItem> GetFiles(string channelName, string? parent = null)
    {
        var items = new System.Collections.Generic.List<VfsItem>();
        var cmd = _connection.CreateCommand();
        string cleanParent = string.IsNullOrEmpty(parent) ? "" : parent.Trim('\\', '/').Replace('/', '\\');

        if (string.IsNullOrEmpty(cleanParent))
        {
            cmd.CommandText = @"
                SELECT f.name, f.size, f.mtime, f.isdir 
                FROM files f
                JOIN mounts m ON f.mount_id = m.id
                WHERE m.channel_name = @cname 
                  AND (f.parent IS NULL OR f.parent = '' OR f.parent = 'false')
                  AND (f.in_trash IS NULL OR f.in_trash = 0)
            ";
        }
        else
        {
            cmd.CommandText = @"
                SELECT f.name, f.size, f.mtime, f.isdir 
                FROM files f
                JOIN mounts m ON f.mount_id = m.id
                WHERE m.channel_name = @cname 
                  AND f.parent = @parent
                  AND (f.in_trash IS NULL OR f.in_trash = 0)
            ";
            cmd.Parameters.AddWithValue("@parent", cleanParent);
        }
        cmd.Parameters.AddWithValue("@cname", channelName);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            items.Add(new VfsItem 
            { 
                Name = reader.GetString(0), 
                IsDirectory = reader.GetInt32(3) == 1, 
                Size = reader.GetInt64(1),
                Date = ReadDateTime(reader, 2)
            });
        }
        return items;
    }

    /// <summary>
    /// Выполняет чекпоинт WAL-журнала, сбрасывая изменения из файла .db-wal в основной файл базы данных .db.
    /// truncate = false (PASSIVE mode, без блокировки читающих потоков TC).
    /// truncate = true (TRUNCATE mode, полный сброс и ужимка журнала до 0 байт).
    /// </summary>
    public void Checkpoint(bool truncate = false)
    {
        try
        {
            if (_connection != null && _connection.State == System.Data.ConnectionState.Open)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = truncate ? "PRAGMA wal_checkpoint(TRUNCATE);" : "PRAGMA wal_checkpoint(PASSIVE);";
                cmd.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            Logger.Error("DB", $"Failed executing WAL checkpoint (truncate={truncate})", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Checkpoint(truncate: true);
        _connection?.Dispose();
    }
}
