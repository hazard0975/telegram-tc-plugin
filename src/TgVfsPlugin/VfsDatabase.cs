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
        cmd.CommandText = "SELECT id, local_path, channel_id, mode FROM mounts WHERE channel_name = @cname";
        cmd.Parameters.AddWithValue("@cname", name);
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            return new MountInfo
            {
                Id = reader.GetString(0),
                LocalPath = reader.GetString(1),
                ChannelId = reader.GetInt64(2),
                ChannelName = name,
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
        if (string.IsNullOrEmpty(parent))
        {
            cmd.CommandText = @"
                SELECT uid, mount_id, isdir, name, parent, mtime, size, tg_message_id, in_trash, ver
                FROM files
                WHERE mount_id = @mid AND name = @name AND (parent IS NULL OR parent = 'false') AND (in_trash IS NULL OR in_trash = 0)
                LIMIT 1
            ";
        }
        else
        {
            cmd.CommandText = @"
                SELECT uid, mount_id, isdir, name, parent, mtime, size, tg_message_id, in_trash, ver
                FROM files
                WHERE mount_id = @mid AND name = @name AND parent = @parent AND (in_trash IS NULL OR in_trash = 0)
                LIMIT 1
            ";
            cmd.Parameters.AddWithValue("@parent", parent);
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

    public void MoveFileToTrash(string uid)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE files SET in_trash = 1 WHERE uid = @uid";
        cmd.Parameters.AddWithValue("@uid", uid);
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

    public System.Collections.Generic.List<VfsItem> GetFiles(string channelName)
    {
        var items = new System.Collections.Generic.List<VfsItem>();
        var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            SELECT f.name, f.size, f.mtime, f.isdir 
            FROM files f
            JOIN mounts m ON f.mount_id = m.id
            WHERE m.channel_name = @cname 
              AND (f.parent IS NULL OR f.parent = 'false')
              AND (f.in_trash IS NULL OR f.in_trash = 0)
        ";
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

    public void Dispose()
    {
        _connection?.Dispose();
    }
}
