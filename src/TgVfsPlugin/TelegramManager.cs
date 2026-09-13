using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using WTelegram;
using TL;

namespace TgVfsPlugin;

public static class TelegramManager
{
    private static Client? _client;
    private static User? _user;
    private static readonly ConcurrentDictionary<long, ChatBase> _chatsCache = new();
    
    static TelegramManager()
    {
        AppDomain.CurrentDomain.ProcessExit += (s, e) => 
        {
            Logger.Log("ProcessExit triggered, disposing WTelegramClient to flush session...");
            _client?.Dispose();
        };
    }

    public static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), 
        "TelegramVFS");

    private static readonly string SettingsFile = Path.Combine(ConfigPath, "settings.ini");
    private static readonly string SessionFile = Path.Combine(ConfigPath, "WTelegram.session");

    public static bool IsLoggedIn => _user != null;
    public static bool IsPremium => _user != null && (((uint)_user.flags & (1u << 28)) != 0);

    private static string? GetSetting(string key)
    {
        if (!File.Exists(SettingsFile)) return null;
        try
        {
            foreach (var line in File.ReadAllLines(SettingsFile))
            {
                var parts = line.Split('=', 2);
                if (parts.Length == 2 && parts[0].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    return parts[1].Trim();
                }
            }
        }
        catch { }
        return null;
    }

    private static void SaveSetting(string key, string value)
    {
        try
        {
            Directory.CreateDirectory(ConfigPath);
            var lines = File.Exists(SettingsFile) ? File.ReadAllLines(SettingsFile).ToList() : new List<string>();
            bool found = false;
            
            for (int i = 0; i < lines.Count; i++)
            {
                var parts = lines[i].Split('=', 2);
                if (parts.Length >= 1 && parts[0].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = $"{key}={value}";
                    found = true;
                    break;
                }
            }

            if (!found) lines.Add($"{key}={value}");
            
            File.WriteAllLines(SettingsFile, lines);
        }
        catch (Exception ex)
        {
            Logger.Log($"Error saving setting {key}: {ex}");
        }
    }

    public static async Task<bool> LoginAsync(bool silent = false)
    {
        try
        {
            Logger.Log($"Starting LoginAsync (silent: {silent})...");
            EnsureSettingsExist(requirePhone: !silent);
            
            if (File.Exists(SessionFile))
            {
                var fi = new FileInfo(SessionFile);
                if (fi.Length == 0)
                {
                    Logger.Log("Found 0-byte session file! Deleting it proactively before WTelegramClient touches it.");
                    File.Delete(SessionFile);
                }
            }

            if (_client == null)
            {
                Helpers.Log = (lvl, str) => Logger.Log($"[WTelegram] {lvl}: {str}");
                Logger.Log("Creating new WTelegramClient instance...");
                _client = new Client(what => Config(what));
            }

            // Устанавливаем флаг перед вызовом
            _isSilentLogin = silent;
            
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
                Logger.Log("Detected corrupted session or invalid API_HASH. Resetting client only, preserving credentials.");
                
                try { _client?.Dispose(); } catch (Exception e) { Logger.Log($"Dispose error: {e.Message}"); }
                _client = null;
                
                try { if (File.Exists(SessionFile)) { File.Delete(SessionFile); Logger.Log("Deleted WTelegram.session"); } } catch (Exception e) { Logger.Log($"Delete session error: {e.Message}"); }
            }
            return false;
        }
        finally
        {
            _isSilentLogin = false; // сбрасываем обратно
        }
    }

    private static bool _isSilentLogin = false;

    private static string? Config(string what)
    {
        Logger.Log($"[WTelegram Config] Requested: {what}");
        string? result = null;
        switch (what)
        {
            case "api_id": result = GetApiId(); break;
            case "api_hash": result = GetApiHash(); break;
            case "phone_number": 
                result = GetSetting("phone_number");
                if (!string.IsNullOrEmpty(result))
                {
                    Logger.Log("Returning cached phone_number from settings.ini");
                    break;
                }

                if (_isSilentLogin) 
                {
                    Logger.Log("Silent login requested, returning null for phone_number to prevent UI prompt.");
                    return null; 
                }
                result = InputDialog.Show("Enter your phone number (with +):", "Telegram Login"); 
                if (!string.IsNullOrEmpty(result))
                {
                    SaveSetting("phone_number", result);
                }
                break;
            case "verification_code": 
                if (_isSilentLogin) return null;
                result = InputDialog.Show("Enter the verification code sent to your Telegram app:", "Telegram Login"); 
                break;
            case "password": 
                if (_isSilentLogin) return null;
                result = InputDialog.Show("Enter your 2FA password:", "Telegram Login", isPassword: true); 
                break;
            case "session_pathname": result = SessionFile; break;
        }

        if (what == "api_hash" || what == "password" || what == "phone_number")
            Logger.Log($"[WTelegram Config] Returning for {what}: {(string.IsNullOrEmpty(result) ? "EMPTY/NULL" : "***")}");
        else
            Logger.Log($"[WTelegram Config] Returning for {what}: {result ?? "null"}");

        return result;
    }

    private static string GetApiId()
    {
        return GetSetting("api_id") ?? "";
    }

    private static string GetApiHash()
    {
        return GetSetting("api_hash") ?? "";
    }

    private static void EnsureSettingsExist(bool requirePhone)
    {
        Logger.Log($"Checking credentials in: {SettingsFile}");
        string? apiId = GetSetting("api_id");
        string? apiHash = GetSetting("api_hash");
        string? phone = GetSetting("phone_number");

        bool isValidApi = !string.IsNullOrWhiteSpace(apiId) && !string.IsNullOrWhiteSpace(apiHash) && apiHash.Length == 32;

        if (!isValidApi)
        {
            Logger.Log("Credentials missing or invalid. Prompting user via UI...");
            apiId = InputDialog.Show("Enter your Telegram API_ID (get it from my.telegram.org):", "Initial Setup");
            apiHash = InputDialog.Show("Enter your Telegram API_HASH (32 chars):", "Initial Setup");
            
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

            SaveSetting("api_id", apiId);
            SaveSetting("api_hash", apiHash);
            Logger.Log($"Successfully saved new credentials to settings.ini");
        }

        if (requirePhone && string.IsNullOrWhiteSpace(phone))
        {
            phone = InputDialog.Show("Enter your phone number (with +):", "Telegram Login");
            if (!string.IsNullOrWhiteSpace(phone))
            {
                SaveSetting("phone_number", phone.Trim());
                Logger.Log($"Successfully saved phone number to settings.ini");
            }
            else
            {
                throw new Exception("Phone number is required for login.");
            }
        }
    }

    public static async Task<long> CreateChannelAsync(string title, string description = "")
    {
        if (_client == null || _user == null) throw new Exception("Not logged in");
        Logger.Log($"Creating channel: {title}");
        
        var updatesBase = await _client.Channels_CreateChannel(title, description, broadcast: true);
        
        ChatBase? chat = null;
        if (updatesBase is Updates updates)
        {
            chat = updates.chats?.Values.FirstOrDefault();
        }
        else if (updatesBase is UpdatesCombined combined)
        {
            chat = combined.chats?.Values.FirstOrDefault();
        }
        
        if (chat == null) throw new Exception("Failed to get channel ID after creation.");
        _chatsCache[chat.ID] = chat;
        return chat.ID;
    }

    public static async Task<int> UploadAndSendFileAsync(
        long channelId, 
        string localPath, 
        string fileName, 
        string relativeCaption, 
        Func<long, long, bool>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (_client == null || _user == null)
        {
            Logger.Log("UploadAndSendFileAsync: Client not logged in, attempting silent login...");
            await LoginAsync(silent: true);
            if (_client == null || _user == null)
            {
                throw new InvalidOperationException("Not logged in to Telegram.");
            }
        }

        Logger.Log($"Resolving channel {channelId} for upload...");
        if (!_chatsCache.TryGetValue(channelId, out var chat))
        {
            var allChats = await _client.Messages_GetAllChats();
            if (allChats?.chats != null)
            {
                foreach (var kvp in allChats.chats)
                {
                    _chatsCache[kvp.Key] = kvp.Value;
                }
            }
            _chatsCache.TryGetValue(channelId, out chat);
        }

        if (chat == null)
        {
            throw new Exception($"Channel with ID {channelId} not found in Telegram account chats.");
        }

        Logger.Log($"Uploading file '{localPath}' to Telegram servers...");
        InputFileBase inputFile = await _client.UploadFileAsync(localPath, (progress, total) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (onProgress != null)
            {
                bool abort = onProgress(progress, total);
                if (abort)
                {
                    throw new OperationCanceledException("Upload cancelled by user in Total Commander.");
                }
            }
        });

        Logger.Log($"Sending uploaded file '{fileName}' as document to channel {channelId}...");
        var media = new InputMediaUploadedDocument
        {
            file = inputFile,
            mime_type = "application/octet-stream",
            attributes = new DocumentAttribute[]
            {
                new DocumentAttributeFilename { file_name = fileName }
            }
        };

        var message = await _client.SendMessageAsync(chat, relativeCaption, media);
        Logger.Log($"File uploaded successfully! Telegram Message ID: {message.id}");
        return message.id;
    }
}
