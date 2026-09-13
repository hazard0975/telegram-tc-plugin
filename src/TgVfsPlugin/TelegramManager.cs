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
        Logger.Log($"Checking credentials in: {ApiCredentialsFile}");
        bool isValid = false;
        if (File.Exists(ApiCredentialsFile))
        {
            var lines = File.ReadAllLines(ApiCredentialsFile);
            if (lines.Length >= 2 && !string.IsNullOrWhiteSpace(lines[0]) && !string.IsNullOrWhiteSpace(lines[1]))
            {
                // API Hash in Telegram must be a 32-character hex string
                if (lines[1].Trim().Length == 32)
                {
                    isValid = true;
                    Logger.Log("Found valid existing api_credentials.txt");
                }
                else
                {
                    Logger.Log("Existing API_HASH is invalid (must be 32 chars). Forcing prompt.");
                }
            }
        }

        if (!isValid)
        {
            Logger.Log("Credentials missing or invalid. Prompting user via UI...");
            string? apiId = InputDialog.Show("Enter your Telegram API_ID (get it from my.telegram.org):", "Initial Setup");
            string? apiHash = InputDialog.Show("Enter your Telegram API_HASH (32 chars):", "Initial Setup");
            
            if (string.IsNullOrWhiteSpace(apiId) || string.IsNullOrWhiteSpace(apiHash))
            {
                throw new Exception("API ID and API Hash are required to use Telegram VFS.");
            }

            apiId = apiId.Trim();
            apiHash = apiHash.Trim();

            if (apiHash.Length != 32)
            {
                throw new Exception("API_HASH must be exactly 32 characters long. Please check your credentials.");
            }

            Directory.CreateDirectory(ConfigPath);
            File.WriteAllLines(ApiCredentialsFile, new[] { apiId, apiHash });
            Logger.Log("Successfully saved new credentials to file.");
        }
    }

    public static async Task<bool> LoginAsync()
    {
        try
        {
            Logger.Log("Starting LoginAsync...");
            EnsureCredentialsExist();
            
            if (_client == null)
            {
                Helpers.Log = (lvl, str) => Logger.Log($"[WTelegram] {lvl}: {str}");
                Logger.Log("Creating new WTelegramClient instance...");
                _client = new Client(Config);
            }

            Logger.Log("Calling LoginUserIfNeeded...");
            _user = await _client.LoginUserIfNeeded();
            Logger.Log($"Successfully logged in as {_user.username ?? _user.first_name}");
            
            return true;
        }
        catch (Exception ex)
        {
            Logger.Log($"Login failed: {ex.Message}");
            if (ex.Message.Contains("session file") || ex.Message.Contains("rgbKey") || ex.Message.Contains("algorithm"))
            {
                Logger.Log("Detected corrupted session or invalid API_HASH. Nuking credentials to force reset.");
                
                try { _client?.Dispose(); } catch (Exception e) { Logger.Log($"Dispose error: {e.Message}"); }
                _client = null;
                
                try { if (File.Exists(SessionFile)) { File.Delete(SessionFile); Logger.Log("Deleted WTelegram.session"); } } catch (Exception e) { Logger.Log($"Delete session error: {e.Message}"); }
                try { if (File.Exists(ApiCredentialsFile)) { File.Delete(ApiCredentialsFile); Logger.Log("Deleted api_credentials.txt"); } } catch (Exception e) { Logger.Log($"Delete credentials error: {e.Message}"); }
            }
            return false;
        }
    }
}
