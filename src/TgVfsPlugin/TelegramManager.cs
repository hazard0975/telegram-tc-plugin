using System;
using System.IO;
using System.Threading.Tasks;
using WTelegram;
using TL;

namespace TgVfsPlugin;

public static class TelegramManager
{
    private static Client? _client;
    private static User? _user;
    
    public static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), 
        "TelegramVFS");

    private static readonly string ApiCredentialsFile = Path.Combine(ConfigPath, "api_credentials.txt");
    private static readonly string SessionFile = Path.Combine(ConfigPath, "WTelegram.session");

    public static bool IsLoggedIn => _user != null;

    private static string Config(string what)
    {
        switch (what)
        {
            case "api_id": return GetApiId();
            case "api_hash": return GetApiHash();
            case "phone_number": return InputDialog.Show("Enter your phone number (with +):", "Telegram Login") ?? "";
            case "verification_code": return InputDialog.Show("Enter the verification code sent to your Telegram app:", "Telegram Login") ?? "";
            case "password": return InputDialog.Show("Enter your 2FA password:", "Telegram Login", isPassword: true) ?? "";
            case "session_pathname": return SessionFile;
            default: return "";
        }
    }

    private static string GetApiId()
    {
        EnsureCredentialsExist();
        var lines = File.ReadAllLines(ApiCredentialsFile);
        return lines.Length > 0 ? lines[0].Trim() : "";
    }

    private static string GetApiHash()
    {
        EnsureCredentialsExist();
        var lines = File.ReadAllLines(ApiCredentialsFile);
        return lines.Length > 1 ? lines[1].Trim() : "";
    }

    private static void EnsureCredentialsExist()
    {
        bool isValid = false;
        if (File.Exists(ApiCredentialsFile))
        {
            var lines = File.ReadAllLines(ApiCredentialsFile);
            if (lines.Length >= 2 && !string.IsNullOrWhiteSpace(lines[0]) && !string.IsNullOrWhiteSpace(lines[1]))
            {
                isValid = true;
            }
        }

        if (!isValid)
        {
            Logger.Log("Credentials missing or invalid. Prompting user...");
            string? apiId = InputDialog.Show("Enter your Telegram API_ID (get it from my.telegram.org):", "Initial Setup");
            string? apiHash = InputDialog.Show("Enter your Telegram API_HASH:", "Initial Setup");
            
            if (string.IsNullOrWhiteSpace(apiId) || string.IsNullOrWhiteSpace(apiHash))
            {
                throw new Exception("API ID and API Hash are required to use Telegram VFS.");
            }

            Directory.CreateDirectory(ConfigPath);
            File.WriteAllLines(ApiCredentialsFile, new[] { apiId.Trim(), apiHash.Trim() });
        }
    }

    public static async Task<bool> LoginAsync()
    {
        try
        {
            if (_client == null)
            {
                // WTelegramClient logs a lot by default, we can redirect it to our Logger
                Helpers.Log = (lvl, str) => Logger.Log($"[WTelegram] {lvl}: {str}");
                _client = new Client(Config);
            }

            _user = await _client.LoginUserIfNeeded();
            Logger.Log($"Successfully logged in as {_user.username ?? _user.first_name}");
            
            // TODO: Here we should look for "TGvfs" channel, create it if missing, and sync DB.
            return true;
        }
        catch (Exception ex)
        {
            Logger.Log($"Login failed: {ex.Message}");
            if (ex.Message.Contains("session file") || ex.Message.Contains("rgbKey") || ex.Message.Contains("algorithm"))
            {
                Logger.Log("Detected corrupted session or invalid API_HASH. Nuking credentials to force reset.");
                if (File.Exists(SessionFile)) File.Delete(SessionFile);
                if (File.Exists(ApiCredentialsFile)) File.Delete(ApiCredentialsFile);
                _client?.Dispose();
                _client = null;
            }
            return false;
        }
    }
}
