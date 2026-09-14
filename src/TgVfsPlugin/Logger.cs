using System;
using System.Diagnostics;
using System.IO;

namespace TgVfsPlugin;

public static class Logger
{
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
