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

    public static string ConfigPath => SettingsManager.DataDirectory;
    private static string SettingsFile => SettingsManager.ActiveSettingsFile;
    private static string SessionFile => SettingsManager.SessionPath;

    public static bool IsLoggedIn => _user != null;
    public static bool IsPremium => _user != null && (((uint)_user.flags & (1u << 28)) != 0);

    public static void ResetClient()
    {
        try
        {
            Logger.Log("Resetting TelegramManager client...");
            _client?.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Log($"Error disposing client: {ex.Message}");
        }
        finally
        {
            _client = null;
            _user = null;
            _chatsCache.Clear();
        }
    }

    private static string? GetSetting(string key) => SettingsManager.GetSetting(key);

    private static void SaveSetting(string key, string value) => SettingsManager.SaveSetting(key, value);

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

    public static async Task DeleteChannelAsync(long channelId)
    {
        try
        {
            if (_client == null || _user == null)
            {
                await LoginAsync(silent: true);
                if (_client == null || _user == null) return;
            }

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

            if (chat is Channel channel)
            {
                Logger.Log($"Deleting Telegram channel {channelId} ({channel.title})...");
                await _client.Channels_DeleteChannel(new InputChannel(channel.id, channel.access_hash));
                _chatsCache.TryRemove(channelId, out _);
                Logger.Log($"Telegram channel {channelId} successfully deleted.");
            }
            else if (chat is Chat smallGroup)
            {
                Logger.Log($"Deleting Telegram small group chat {channelId}...");
                await _client.Messages_DeleteChatUser(smallGroup.id, _user);
                _chatsCache.TryRemove(channelId, out _);
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to delete Telegram channel {channelId}: {ex.Message}");
        }
    }

    public static async Task DeleteMessageAsync(long channelId, int messageId)
    {
        await DeleteMessagesAsync(channelId, new[] { messageId });
    }

    public static async Task DeleteMessagesAsync(long channelId, int[] messageIds)
    {
        if (messageIds == null || messageIds.Length == 0) return;
        try
        {
            if (_client == null || _user == null)
            {
                await LoginAsync(silent: true);
                if (_client == null || _user == null) return;
            }

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

            for (int i = 0; i < messageIds.Length; i += 100)
            {
                var chunk = messageIds.Skip(i).Take(100).ToArray();
                if (chat is Channel channel)
                {
                    var inputChannel = new InputChannel(channel.id, channel.access_hash);
                    await _client.Channels_DeleteMessages(inputChannel, chunk);
                    Logger.Log($"Deleted batch of {chunk.Length} messages from Telegram channel {channelId}.");
                }
                else if (chat is Chat smallGroup)
                {
                    await _client.Messages_DeleteMessages(chunk, revoke: true);
                    Logger.Log($"Deleted batch of {chunk.Length} messages from Telegram group {channelId}.");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to delete messages from channel {channelId}: {ex.Message}");
        }
    }

    public static async Task<int> UploadAndSendFileAsync(
        long channelId, 
        string localPath, 
        string fileName, 
        string relativeCaption, 
        Func<long, long, bool>? onProgress = null,
        CancellationToken cancellationToken = default,
        ManualResetEventSlim? pauseGate = null)
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
        FileStream fileStream = new FileStream(
            localPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 65536,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        Stream effectiveStream = (pauseGate != null)
            ? new PausableStream(fileStream, pauseGate, cancellationToken)
            : fileStream;

        InputFileBase inputFile;
        try
        {
            inputFile = await _client.UploadFileAsync(effectiveStream, fileName, (progress, total) =>
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
        }
        finally
        {
            try { effectiveStream.Dispose(); } catch { }
            try { fileStream.Dispose(); } catch { }
        }

        cancellationToken.ThrowIfCancellationRequested();

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

    public static async Task DownloadFileAsync(
        long channelId,
        int messageId,
        string targetLocalPath,
        Func<long, long, bool>? onProgress = null,
        CancellationToken cancellationToken = default,
        ManualResetEventSlim? pauseGate = null)
    {
        if (_client == null || _user == null)
        {
            Logger.Log("DownloadFileAsync: Client not logged in, attempting silent login...");
            await LoginAsync(silent: true);
            if (_client == null || _user == null)
            {
                throw new InvalidOperationException("Not logged in to Telegram.");
            }
        }

        Logger.Log($"Resolving channel {channelId} for download...");
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

        if (chat is not Channel channel)
        {
            throw new Exception($"Chat {channelId} is not a broadcast/megagroup channel.");
        }

        var inputChannel = new InputChannel(channel.id, channel.access_hash);

        Logger.Log($"Fetching message {messageId} from channel {channelId}...");
        var messagesBase = await _client.Channels_GetMessages(inputChannel, new InputMessage[] { new InputMessageID { id = messageId } });

        TL.Message? targetMsg = null;
        if (messagesBase is Messages_ChannelMessages channelMessages)
        {
            targetMsg = channelMessages.messages?.OfType<TL.Message>().FirstOrDefault(m => m.id == messageId);
        }
        else if (messagesBase is Messages_Messages regularMessages)
        {
            targetMsg = regularMessages.messages?.OfType<TL.Message>().FirstOrDefault(m => m.id == messageId);
        }

        if (targetMsg == null || targetMsg.media == null)
        {
            throw new FileNotFoundException($"Message {messageId} or its media not found in channel {channelId}.");
        }

        Document? docToDownload = null;
        Photo? photoToDownload = null;
        long expectedTotalBytes = 0;

        if (targetMsg.media is MessageMediaDocument docMedia && docMedia.document is Document doc)
        {
            docToDownload = doc;
            expectedTotalBytes = doc.size;
        }
        else if (targetMsg.media is MessageMediaPhoto photoMedia && photoMedia.photo is Photo photo)
        {
            photoToDownload = photo;
            expectedTotalBytes = 0; // Для фото точный размер может варьироваться
        }
        else
        {
            throw new NotSupportedException($"Unsupported media type in message {messageId}: {targetMsg.media.GetType().Name}");
        }

        // Подготовка временного файла .tgpart
        string targetDir = Path.GetDirectoryName(targetLocalPath) ?? "";
        if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        string partPath = targetLocalPath + ".tgpart";
        long existingBytes = 0;

        if (File.Exists(partPath))
        {
            try
            {
                var fi = new FileInfo(partPath);
                existingBytes = fi.Length;
                if (expectedTotalBytes > 0 && existingBytes >= expectedTotalBytes)
                {
                    // Файл был полностью скачан, но не успел переименоваться
                    Logger.Log($"Existing .tgpart file is already fully downloaded ({existingBytes} bytes). Overwriting part file.");
                    existingBytes = 0;
                }
                else
                {
                    Logger.Log($"Found existing .tgpart file: {existingBytes} / {expectedTotalBytes} bytes. Resuming download...");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error checking existing .tgpart file: {ex.Message}. Starting fresh.");
                existingBytes = 0;
            }
        }

        FileStream fileStream = new FileStream(
            partPath, 
            existingBytes > 0 ? FileMode.OpenOrCreate : FileMode.Create, 
            FileAccess.ReadWrite, 
            FileShare.None);

        Stream effectiveStream = (pauseGate != null)
            ? new PausableStream(fileStream, pauseGate, cancellationToken)
            : fileStream;

        try
        {
            InputFileLocationBase? fileLocation = null;
            int dc_id = 0;

            if (docToDownload != null)
            {
                fileLocation = docToDownload.ToFileLocation((PhotoSizeBase?)null);
                dc_id = docToDownload.dc_id;
            }
            else if (photoToDownload != null)
            {
                var photoSize = photoToDownload.LargestPhotoSize;
                fileLocation = photoToDownload.ToFileLocation(photoSize);
                dc_id = photoToDownload.dc_id;
            }

            if (fileLocation != null)
            {
                await DownloadFileResumableInternalAsync(fileLocation, effectiveStream, dc_id, expectedTotalBytes, existingBytes, onProgress, cancellationToken);
            }
        }
        finally
        {
            effectiveStream.Flush();
            effectiveStream.Dispose();
        }

        Logger.Log($"Download completed into .tgpart ({partPath}). Moving to target '{targetLocalPath}'...");

        if (File.Exists(targetLocalPath))
        {
            File.Delete(targetLocalPath);
        }
        File.Move(partPath, targetLocalPath);
        Logger.Log($"File successfully downloaded and moved to: {targetLocalPath}");
    }
    private static async Task DownloadFileResumableInternalAsync(
        InputFileLocationBase fileLocation, Stream outputStream,
        int dc_id, long expectedTotalBytes, long existingBytes, 
        Func<long, long, bool>? onProgress, CancellationToken cancellationToken)
    {
        if (_client == null) throw new InvalidOperationException("Not logged in to Telegram.");

        int filePartSize = _client.FilePartSize;
        long fileOffset = existingBytes;

        long remainder = fileOffset % filePartSize;
        if (remainder != 0)
        {
            fileOffset -= remainder; // Откатываемся на начало чанка для безопасной докачки
            outputStream.Seek(fileOffset, SeekOrigin.Begin);
        }
        else if (existingBytes > 0)
        {
            outputStream.Seek(existingBytes, SeekOrigin.Begin);
        }

        var currentClient = dc_id == 0 ? _client : await _client.GetClientForDC(-dc_id, true);

        long transmitted = fileOffset;
        if (onProgress != null)
        {
            if (onProgress(transmitted, expectedTotalBytes > 0 ? expectedTotalBytes : transmitted))
                throw new OperationCanceledException("Download cancelled by user in Total Commander.");
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            Upload_FileBase? fileBase = null;
            try
            {
                fileBase = await currentClient.Upload_GetFile(fileLocation, fileOffset, filePartSize);
            }
            catch (TL.RpcException ex) when (ex.Code == 303 && ex.Message.StartsWith("FILE_MIGRATE_"))
            {
                currentClient = await _client.GetClientForDC(-ex.X, true);
                fileBase = await currentClient.Upload_GetFile(fileLocation, fileOffset, filePartSize);
            }
            catch (TL.RpcException ex) when (ex.Code == 400 && ex.Message == "OFFSET_INVALID")
            {
                break; // End of file
            }

            if (fileBase is not Upload_File fileData)
                throw new Exception("Upload_GetFile returned unsupported " + fileBase?.GetType().Name);

            if (fileData.bytes.Length == 0)
                break; // End of file

            await outputStream.WriteAsync(fileData.bytes, 0, fileData.bytes.Length, cancellationToken);
            fileOffset += fileData.bytes.Length;
            transmitted = fileOffset;

            if (onProgress != null)
            {
                long reportedTotal = expectedTotalBytes > 0 ? expectedTotalBytes : transmitted;
                bool userAborted = onProgress(transmitted, reportedTotal);
                if (userAborted)
                {
                    throw new OperationCanceledException("Download cancelled by user in Total Commander.");
                }
            }

            if (fileData.bytes.Length < filePartSize)
                break; // Last part downloaded
        }
    }
}