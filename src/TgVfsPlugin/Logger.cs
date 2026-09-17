using System;
using System.Diagnostics;
using System.IO;

namespace TgVfsPlugin;

public static class Logger
{
    private static readonly object _lock = new();
    private static string LogPath => SettingsManager.LogPath;

    static Logger()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            string arch = Environment.Is64BitProcess ? "x64 (64-bit)" : "x86 (32-bit)";
            string dotnetVer = Environment.Version.ToString();
            string osVer = Environment.OSVersion.ToString();

            string hostInfo = "Unknown Host";
            try
            {
                using var currentProcess = Process.GetCurrentProcess();
                var mainModule = currentProcess.MainModule;
                if (mainModule != null)
                {
                    string procName = mainModule.ModuleName ?? currentProcess.ProcessName;
                    var verInfo = mainModule.FileVersionInfo;
                    string desc = !string.IsNullOrWhiteSpace(verInfo.FileDescription) ? verInfo.FileDescription : procName;
                    string prodVer = !string.IsNullOrWhiteSpace(verInfo.ProductVersion) ? verInfo.ProductVersion : verInfo.FileVersion ?? "";
                    
                    hostInfo = string.IsNullOrWhiteSpace(prodVer) ? $"{desc} ({procName})" : $"{desc} v{prodVer} ({procName})";
                }
            }
            catch
            {
                try
                {
                    hostInfo = Process.GetCurrentProcess().ProcessName;
                }
                catch { }
            }
            
            string header = $"--- Telegram VFS Log started at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---\n" +
                            $"[Environment] Host: {hostInfo}\n" +
                            $"[Environment] Process: {arch}, .NET: {dotnetVer}, OS: {osVer}\n" +
                            $"------------------------------------------------------------\n";
            lock (_lock)
            {
                File.WriteAllText(LogPath, header);
            }
        }
        catch { }
    }

    public static void Info(string tag, string message) => WriteEntry("INFO", tag, message);
    public static void Warn(string tag, string message) => WriteEntry("WARN", tag, message);
    public static void Error(string tag, string message, Exception? ex = null)
    {
        string fullMsg = ex != null ? $"{message}: {ex.Message}" : message;
        WriteEntry("ERROR", tag, fullMsg);
    }
    public static void Debug(string tag, string message) => WriteEntry("DEBUG", tag, message);

    public static void Log(string message)
    {
        // Fallback backward-compatible method
        if (string.IsNullOrWhiteSpace(message)) return;
        
        string level = "INFO";
        string tag = "SYS";
        string text = message.Trim();

        if (text.StartsWith("[WTelegram]", StringComparison.OrdinalIgnoreCase))
        {
            tag = "TG";
            text = text.Substring("[WTelegram]".Length).Trim();
        }
        else if (text.StartsWith("[Physical Copy]", StringComparison.OrdinalIgnoreCase))
        {
            tag = "WFX";
        }
        else if (text.StartsWith("[Physical Move]", StringComparison.OrdinalIgnoreCase))
        {
            tag = "WFX";
        }
        else if (text.StartsWith("Error", StringComparison.OrdinalIgnoreCase) || text.Contains("failed", StringComparison.OrdinalIgnoreCase) || text.Contains("Exception", StringComparison.OrdinalIgnoreCase))
        {
            level = "ERROR";
        }
        else if (text.StartsWith("Warning", StringComparison.OrdinalIgnoreCase))
        {
            level = "WARN";
        }

        WriteEntry(level, tag, text);
    }

    private static void WriteEntry(string level, string tag, string message)
    {
        try
        {
            string formattedLevel = level.PadRight(5);
            string formattedTag = tag.PadRight(5);
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] [{formattedLevel}] [{formattedTag}] {message}\n";

            lock (_lock)
            {
                File.AppendAllText(LogPath, line);
            }
        }
        catch { }
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "0 B";
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        int counter = 0;
        decimal number = bytes;
        while (Math.Round(number / 1024m) >= 1 && counter < suffixes.Length - 1)
        {
            number /= 1024m;
            counter++;
        }
        return counter == 0 ? $"{bytes} B" : $"{number:n2} {suffixes[counter]} ({bytes:n0} bytes)";
    }
}
