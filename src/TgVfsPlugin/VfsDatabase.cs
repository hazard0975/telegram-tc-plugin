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

    static VfsDatabase()
    {
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(SQLitePCL.raw).Assembly, (libraryName, assembly, searchPath) =>
            {
                if (libraryName == "e_sqlite3" || libraryName == "sqlite3")
                {
                    // Для плагинов Total Commander AppContext.BaseDirectory указывает на папку установки TC (c:\totalcmd),
                    // а не на папку плагина. Поэтому нам нужно получить путь к самой DLL плагина.
                    string pluginPath = Assembly.GetExecutingAssembly().Location;
                    if (string.IsNullOrEmpty(pluginPath))
                    {
                        // В Native AOT Location может быть пустой, используем путь текущего процесса (хотя это TC) 
                        // или специальный трюк для Native AOT
                        pluginPath = AppContext.BaseDirectory; 
                    }
                    
                    string basePath = Path.GetDirectoryName(pluginPath) ?? AppContext.BaseDirectory;
                    
                    // Хак для Native AOT в Total Commander: если мы в папке TC, ищем в подпапке плагина
                    // Но так как мы не знаем точного пути установки, лучше искать относительно нашей DLL.
                    // Для Native AOT Assembly.Location возвращает пустую строку, поэтому мы используем путь
                    // откуда была загружена текущая библиотека.
                    
                    // Получаем путь к нашему загруженному модулю (TgVfsPlugin.wfx)
                    using var processModule = System.Diagnostics.Process.GetCurrentProcess().Modules.Cast<System.Diagnostics.ProcessModule>()
                        .FirstOrDefault(m => m.ModuleName != null && m.ModuleName.StartsWith("TgVfsPlugin", StringComparison.OrdinalIgnoreCase));
                        
                    if (processModule != null && !string.IsNullOrEmpty(processModule.FileName))
                    {
                        basePath = Path.GetDirectoryName(processModule.FileName) ?? basePath;
                    }

                    string arch = IntPtr.Size == 8 ? "x64" : "x86";
                    string libPath = Path.Combine(basePath, arch, "e_sqlite3.dll");
                    
                    Logger.Log($"Attempting to load sqlite from: {libPath}");
                    
                    if (NativeLibrary.TryLoad(libPath, out IntPtr handle))
                    {
                        Logger.Log($"Successfully loaded e_sqlite3.dll from {arch}");
                        return handle;
                    }
                    Logger.Log($"Failed to load e_sqlite3.dll from specific path: {libPath}");
                }
                return IntPtr.Zero;
            });
        }
        catch (Exception ex)
        {
            Logger.Log($"Error setting DllImportResolver: {ex.Message}");
        }
    }

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
        
        SeedFakeData();
    }

    private void SeedFakeData()
    {
        var countCmd = _connection.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM channels";
        var count = (long)countCmd.ExecuteScalar()!;

        if (count == 0)
        {
            var insertCmd = _connection.CreateCommand();
            insertCmd.CommandText = @"
                INSERT INTO channels (id, username, title, updated_at) VALUES 
                (1, 'work_chat', 'Work Chat', '2026-09-12 10:00:00'),
                (2, 'memes_daily', 'Memes Daily', '2026-09-12 12:00:00');

                INSERT INTO files (id, channel_id, message_id, name, size, created_at) VALUES 
                (1, 1, 100, 'Q3_Report.pdf', 1048576, '2026-09-10 09:30:00'),
                (2, 1, 101, 'presentation.pptx', 5242880, '2026-09-11 14:15:00'),
                (3, 2, 200, 'funny_cat.mp4', 15728640, '2026-09-12 11:20:00'),
                (4, 2, 201, 'meme.jpg', 256000, '2026-09-12 11:25:00');
            ";
            insertCmd.ExecuteNonQuery();
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

    public System.Collections.Generic.List<VfsItem> GetChannels()
    {
        var items = new System.Collections.Generic.List<VfsItem>();
        var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT title, updated_at FROM channels";
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

    public System.Collections.Generic.List<VfsItem> GetFiles(string channelTitle)
    {
        var items = new System.Collections.Generic.List<VfsItem>();
        var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            SELECT f.name, f.size, f.created_at 
            FROM files f
            JOIN channels c ON f.channel_id = c.id
            WHERE c.title = @title
        ";
        cmd.Parameters.AddWithValue("@title", channelTitle);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            items.Add(new VfsItem 
            { 
                Name = reader.GetString(0), 
                IsDirectory = false, 
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
