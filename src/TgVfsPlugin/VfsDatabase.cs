using System;
using System.IO;
using Microsoft.Data.Sqlite;

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
            CREATE TABLE IF NOT EXISTS channels (
                id INTEGER PRIMARY KEY,
                username TEXT,
                title TEXT,
                updated_at DATETIME
            );

            CREATE TABLE IF NOT EXISTS files (
                id INTEGER PRIMARY KEY,
                channel_id INTEGER,
                message_id INTEGER,
                name TEXT NOT NULL,
                size INTEGER,
                created_at DATETIME,
                FOREIGN KEY(channel_id) REFERENCES channels(id)
            );
        ";
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _connection?.Dispose();
    }
}
