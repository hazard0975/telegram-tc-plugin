using System;
using System.IO;

namespace TgVfsPlugin;

public static class Logger
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), 
        "TelegramVFS", 
        "plugin_log.txt");

    static Logger()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            string arch = Environment.Is64BitProcess ? "x64 (64-bit)" : "x86 (32-bit)";
            string dotnetVer = Environment.Version.ToString();
            string osVer = Environment.OSVersion.ToString();
            
            string header = $"--- Telegram VFS Log started at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---\n" +
                            $"[Environment] Process: {arch}, .NET: {dotnetVer}, OS: {osVer}\n" +
                            $"------------------------------------------------------------\n";
            File.WriteAllText(LogPath, header);
        }
        catch { }
    }

    public static void Log(string message)
    {
        try
        {
            File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
        }
        catch { }
    }
}
