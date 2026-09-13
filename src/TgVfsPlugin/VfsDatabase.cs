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
    private static readonly string DbPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), 
        "TelegramVFS", 
        "vfs_cache.db");

    public VfsDatabase()
    {
        // Убедимся, что директория для БД существует
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);

        _connection = new SqliteConnection($"Data Source={DbPath}");
        _connection.Open();
        
        InitializeSchema();
    }

    private void InitializeSchema()
    {
        var command = _connection.CreateCommand();
        command.CommandText = @"
            -- Для прототипа: очищаем старую структуру, если она есть, 
            -- чтобы применить новую (Adjacency List + Mounts)
            DROP TABLE IF EXISTS files;
            DROP TABLE IF EXISTS channels;

            CREATE TABLE IF NOT EXISTS mounts (
                id TEXT PRIMARY KEY,
                local_path TEXT,
                channel_id INTEGER,
                channel_name TEXT,
                mode INTEGER,
                created_at DATETIME
            );

            CREATE TABLE IF NOT EXISTS files (
                uid TEXT PRIMARY KEY,
                mount_id TEXT,
                isdir INTEGER,
                name TEXT NOT NULL,
                parent TEXT,
                mtime DATETIME,
                size INTEGER,
                tg_message_id INTEGER,
                ver INTEGER,
                FOREIGN KEY(mount_id) REFERENCES mounts(id)
            );
        ";
        command.ExecuteNonQuery();
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
        cmd.Parameters.AddWithValue("@dt", DateTime.Now);
        cmd.ExecuteNonQuery();
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
                Date = reader.GetDateTime(1)
            });
        }
        return items;
    }

    public System.Collections.Generic.List<VfsItem> GetFiles(string channelName)
    {
        var items = new System.Collections.Generic.List<VfsItem>();
        var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            SELECT f.name, f.size, f.mtime, f.isdir 
            FROM files f
            JOIN mounts m ON f.mount_id = m.id
            WHERE m.channel_name = @cname AND (f.parent IS NULL OR f.parent = 'false')
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
                Date = reader.GetDateTime(2)
            });
        }
        return items;
    }

    public void Dispose()
    {
        _connection?.Dispose();
    }
}
