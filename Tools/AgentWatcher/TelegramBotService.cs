using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace ArsanGazERP.AgentWatcher;

internal sealed record TelegramAttachment(string LocalPath, string FileName, string? MimeType, long Size);

internal sealed class TelegramBotService : IDisposable
{
    private const long AttachmentSizeLimit = 20 * 1024 * 1024;
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(30);
    // Telegram may hold getUpdates open for 25 seconds; leave time for the response to arrive.
    private static readonly TimeSpan DefaultLongPollTimeout = TimeSpan.FromSeconds(35);
    private static readonly TimeSpan DownloadRequestTimeout = TimeSpan.FromSeconds(60);
    // Uploads are budgeted for a slow uplink (~64 KB/s) so large files such as APKs can finish.
    private const long MinimumUploadBytesPerSecond = 64 * 1024;
    private static readonly TimeSpan MaximumUploadTimeout = TimeSpan.FromMinutes(15);
    // Data files only; executables and scripts stay blocked.
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".heic",
        ".pdf", ".txt", ".csv", ".md", ".json", ".xml", ".rtf",
        ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".zip",
        ".ogg", ".oga", ".mp3", ".m4a", ".wav", ".mp4", ".mov",
        ".glb", ".gltf", ".fbx", ".obj"
    };
    private static readonly (string Property, string DefaultName)[] AttachmentKinds =
    [
        ("document", "dosya.bin"),
        ("voice", "sesli-mesaj.ogg"),
        ("audio", "ses.mp3"),
        ("video", "video.mp4"),
        ("video_note", "video-not.mp4"),
        ("animation", "animasyon.mp4")
    ];
    private const int MessageChunkSize = 3500;
    private const long PhotoSizeLimit = 10 * 1024 * 1024;
    private const long DocumentSizeLimit = 50 * 1024 * 1024;
    private static readonly HashSet<string> PhotoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".webp" };
    private static readonly string OffsetPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ArsanGazERP",
        "AgentWatcher",
        "telegram-offset.txt");

    private readonly HttpClient _httpClient;
    private readonly string _token;
    private readonly long _allowedChatId;
    private readonly string _offsetPath;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeSpan _longPollTimeout;
    private readonly Func<TimeSpan, CancellationToken, Task> _retryDelay;
    private long _offset;
    private readonly object _statusLock = new();
    private DateTime _lastSuccessUtc;
    private string? _lastError;

    public long AllowedChatId => _allowedChatId;

    public bool IsConnected
    {
        get
        {
            lock (_statusLock)
            {
                return _lastError is null && DateTime.UtcNow - _lastSuccessUtc < TimeSpan.FromMinutes(1);
            }
        }
    }

    public string StatusText
    {
        get
        {
            lock (_statusLock)
            {
                if (_lastError is not null) return "Hata: " + _lastError;
                return _lastSuccessUtc == default
                    ? "İlk Telegram yanıtı bekleniyor"
                    : $"Son başarılı kontrol: {_lastSuccessUtc.ToLocalTime():HH:mm:ss}";
            }
        }
    }

    public TelegramBotService(string token, long allowedChatId)
        : this(token, allowedChatId, new HttpClientHandler(), OffsetPath)
    {
    }

    internal TelegramBotService(
        string token,
        long allowedChatId,
        HttpMessageHandler handler,
        string offsetPath,
        TimeSpan? requestTimeout = null,
        TimeSpan? longPollTimeout = null,
        Func<TimeSpan, CancellationToken, Task>? retryDelay = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentException.ThrowIfNullOrWhiteSpace(offsetPath);
        if (allowedChatId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(allowedChatId), "Telegram chat ID sıfır olamaz.");
        }

        _token = token;
        _allowedChatId = allowedChatId;
        _offsetPath = offsetPath;
        _requestTimeout = ValidateTimeout(requestTimeout ?? DefaultRequestTimeout, nameof(requestTimeout));
        _longPollTimeout = ValidateTimeout(longPollTimeout ?? DefaultLongPollTimeout, nameof(longPollTimeout));
        _retryDelay = retryDelay ?? (static (delay, token) => Task.Delay(delay, token));
        _offset = LoadOffset();
        _httpClient = new HttpClient(handler) { Timeout = MaximumUploadTimeout };
    }

    public async Task<string> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        using JsonDocument response = await CallAsync("getMe", null, cancellationToken);
        JsonElement result = response.RootElement.GetProperty("result");
        return $"Bot bağlantısı başarılı: @{result.GetProperty("username").GetString()}";
    }

    public async Task SendMessageAsync(string text, CancellationToken cancellationToken = default)
    {
        _ = await SendMessageWithReceiptAsync(text, cancellationToken);
    }

    public async Task<IReadOnlyList<TelegramDeliveryReceipt>> SendMessageWithReceiptAsync(
        string text,
        CancellationToken cancellationToken = default,
        int startChunkIndex = 0,
        Func<TelegramDeliveryReceipt, CancellationToken, Task>? onChunkDelivered = null)
    {
        if (startChunkIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startChunkIndex));
        }

        List<TelegramDeliveryReceipt> receipts = [];
        int chunkIndex = 0;
        foreach (string chunk in SplitMessage(text))
        {
            if (chunkIndex++ < startChunkIndex)
            {
                continue;
            }

            using JsonDocument response = await CallAsync(
                "sendMessage",
                new Dictionary<string, object>
                {
                    ["chat_id"] = _allowedChatId,
                    ["text"] = chunk,
                    ["disable_web_page_preview"] = true
                },
                cancellationToken);

            JsonElement result = response.RootElement.GetProperty("result");
            long actualChatId = result.GetProperty("chat").GetProperty("id").GetInt64();
            if (actualChatId != _allowedChatId)
            {
                throw new InvalidOperationException(
                    "Telegram kabul yanıtındaki sohbet kimliği yapılandırılmış izinli sohbetle eşleşmiyor.");
            }

            TelegramDeliveryReceipt receipt = new(
                actualChatId,
                result.GetProperty("message_id").GetInt64());
            receipts.Add(receipt);
            if (onChunkDelivered is not null)
            {
                await onChunkDelivered(receipt, cancellationToken);
            }
        }

        return receipts;
    }

    internal static int GetMessageChunkCount(string text) => SplitMessage(text).Count();

    internal static TimeSpan GetUploadTimeout(long bytes)
    {
        TimeSpan budget = TimeSpan.FromSeconds(30 + Math.Max(0, bytes) / (double)MinimumUploadBytesPerSecond);
        return budget < DownloadRequestTimeout ? DownloadRequestTimeout
            : budget > MaximumUploadTimeout ? MaximumUploadTimeout
            : budget;
    }

    public async Task<TelegramDeliveryReceipt> SendFileAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        FileInfo file = new(filePath);
        if (!file.Exists || file.Length == 0 || file.Length > DocumentSizeLimit)
        {
            throw new InvalidDataException("Telegram'a gönderilecek dosya bulunamadı, boş veya 50 MB'tan büyük.");
        }

        bool asPhoto = PhotoExtensions.Contains(file.Extension) && file.Length <= PhotoSizeLimit;
        string method = asPhoto ? "sendPhoto" : "sendDocument";
        using MultipartFormDataContent content = new();
        content.Add(new StringContent(_allowedChatId.ToString(CultureInfo.InvariantCulture)), "chat_id");
        await using FileStream stream = file.OpenRead();
        content.Add(new StreamContent(stream), asPhoto ? "photo" : "document", file.Name);
        TimeSpan uploadTimeout = GetUploadTimeout(file.Length);
        using CancellationTokenSource upload = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        upload.CancelAfter(uploadTimeout);
        using JsonDocument response = await CallWithContentAsync(method, content, upload.Token, uploadTimeout);
        JsonElement result = response.RootElement.GetProperty("result");
        long actualChatId = result.GetProperty("chat").GetProperty("id").GetInt64();
        if (actualChatId != _allowedChatId)
        {
            throw new InvalidOperationException(
                "Telegram kabul yanıtındaki sohbet kimliği yapılandırılmış izinli sohbetle eşleşmiyor.");
        }

        return new TelegramDeliveryReceipt(actualChatId, result.GetProperty("message_id").GetInt64());
    }

    public async Task SendMessageWithButtonsAsync(
        string text,
        IReadOnlyList<IReadOnlyList<TelegramInlineButton>> rows,
        CancellationToken cancellationToken = default)
    {
        if (rows.Count == 0 || rows.Any(row => row.Count == 0))
        {
            throw new ArgumentException("Telegram inline keyboard rows cannot be empty.", nameof(rows));
        }

        string[] chunks = SplitMessage(text).ToArray();
        for (int index = 0; index < chunks.Length; index++)
        {
            Dictionary<string, object> parameters = new()
            {
                ["chat_id"] = _allowedChatId,
                ["text"] = chunks[index],
                ["disable_web_page_preview"] = true
            };
            if (index == chunks.Length - 1)
            {
                parameters["reply_markup"] = new Dictionary<string, object>
                {
                    ["inline_keyboard"] = rows.Select(row => row.Select(button =>
                    {
                        if (Encoding.UTF8.GetByteCount(button.CallbackData) > 64)
                        {
                            throw new ArgumentException("Telegram callback data exceeds 64 bytes.", nameof(rows));
                        }

                        return new Dictionary<string, string>
                        {
                            ["text"] = button.Text,
                            ["callback_data"] = button.CallbackData
                        };
                    }).ToArray()).ToArray()
                };
            }

            await CallAsync("sendMessage", parameters, cancellationToken);
        }
    }

    public async Task RunAsync(
        Func<long, string, long, CancellationToken, Task<string>> handleCommand,
        Func<long, string, long, CancellationToken, Task<string>> handleCallback,
        Action<string> log,
        CancellationToken cancellationToken,
        Func<long, string, long, IReadOnlyList<TelegramAttachment>, CancellationToken, Task<string>>? handleAttachments = null)
    {
        ArgumentNullException.ThrowIfNull(handleCommand);
        ArgumentNullException.ThrowIfNull(handleCallback);
        ArgumentNullException.ThrowIfNull(log);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using JsonDocument response = await CallAsync(
                    "getUpdates",
                    new Dictionary<string, object>
                    {
                        ["offset"] = _offset,
                        ["timeout"] = 25,
                        ["allowed_updates"] = new[] { "message", "callback_query" }
                    },
                    cancellationToken);
                lock (_statusLock)
                {
                    _lastSuccessUtc = DateTime.UtcNow;
                    _lastError = null;
                }

                List<JsonElement> updates = [.. response.RootElement.GetProperty("result").EnumerateArray()];
                // Telegram delivers each album item as its own update; fetch the rest so the album becomes one request.
                using JsonDocument? albumTail = handleAttachments is not null && updates.Count > 0
                    && GetMediaGroupId(updates[^1]) is not null
                        ? await CallAsync(
                            "getUpdates",
                            new Dictionary<string, object>
                            {
                                ["offset"] = updates[^1].GetProperty("update_id").GetInt64() + 1,
                                ["timeout"] = 2,
                                ["allowed_updates"] = new[] { "message", "callback_query" }
                            },
                            cancellationToken)
                        : null;
                if (albumTail is not null)
                {
                    updates.AddRange(albumTail.RootElement.GetProperty("result").EnumerateArray());
                }

                for (int index = 0; index < updates.Count;)
                {
                    string? group = handleAttachments is null ? null : GetMediaGroupId(updates[index]);
                    int end = index + 1;
                    while (group is not null && end < updates.Count && GetMediaGroupId(updates[end]) == group)
                    {
                        end++;
                    }

                    if (end - index > 1)
                    {
                        await ProcessMediaGroupAsync(updates.GetRange(index, end - index), log, cancellationToken, handleAttachments!);
                    }
                    else
                    {
                        long updateId = updates[index].GetProperty("update_id").GetInt64();
                        await ProcessUpdateAsync(updates[index], updateId, handleCommand, handleCallback, log, cancellationToken, handleAttachments);
                    }

                    _offset = updates[end - 1].GetProperty("update_id").GetInt64() + 1;
                    SaveOffset(_offset);
                    index = end;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                string message = Redact(exception.GetBaseException().Message);
                lock (_statusLock)
                {
                    _lastError = message;
                }

                log("Telegram bağlantı hatası: " + message);
                await _retryDelay(TimeSpan.FromSeconds(10), cancellationToken);
            }
        }
    }

    private static string? GetMediaGroupId(JsonElement update) =>
        update.TryGetProperty("message", out JsonElement message)
        && message.ValueKind == JsonValueKind.Object
        && message.TryGetProperty("media_group_id", out JsonElement group)
        && group.ValueKind == JsonValueKind.String
            ? group.GetString()
            : null;

    private static bool HasAttachment(JsonElement message) =>
        message.TryGetProperty("photo", out _)
        || AttachmentKinds.Any(candidate => message.TryGetProperty(candidate.Property, out _));

    private bool TryGetAllowedMessage(JsonElement update, Action<string> log, out JsonElement message, out long chatId)
    {
        chatId = 0;
        if (!update.TryGetProperty("message", out message)
            || !message.TryGetProperty("chat", out JsonElement chat)
            || !chat.TryGetProperty("id", out JsonElement chatIdValue)
            || !chatIdValue.TryGetInt64(out chatId))
        {
            return false;
        }

        if (chatId != _allowedChatId)
        {
            log($"Yetkisiz Telegram sohbetinden gelen güncelleme yok sayıldı (chat ID: {chatId}).");
            return false;
        }

        return true;
    }

    private async Task<(TelegramAttachment File, string Caption)> DownloadMessageAttachmentAsync(
        JsonElement message, long updateId, CancellationToken cancellationToken)
    {
        (string Property, string DefaultName) kind = AttachmentKinds.FirstOrDefault(
            candidate => message.TryGetProperty(candidate.Property, out _));
        JsonElement attachment;
        string defaultName = "image.jpg";
        if (kind.Property is not null)
        {
            attachment = message.GetProperty(kind.Property);
            if (attachment.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Telegram dosya eki geçersiz.");
            defaultName = kind.DefaultName;
        }
        else
        {
            JsonElement photos = message.GetProperty("photo");
            if (photos.ValueKind != JsonValueKind.Array || photos.GetArrayLength() == 0)
                throw new InvalidDataException("Telegram fotoğraf eki geçersiz.");
            attachment = photos[photos.GetArrayLength() - 1];
            if (attachment.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Telegram fotoğraf eki geçersiz.");
        }

        string caption = string.Empty;
        if (message.TryGetProperty("caption", out JsonElement captionValue))
        {
            if (captionValue.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                throw new InvalidDataException("Telegram ek açıklaması geçersiz.");
            caption = captionValue.GetString() ?? string.Empty;
        }

        return (await DownloadAttachmentAsync(attachment, defaultName, updateId, cancellationToken), caption);
    }

    private async Task ProcessMediaGroupAsync(
        IReadOnlyList<JsonElement> updates,
        Action<string> log,
        CancellationToken cancellationToken,
        Func<long, string, long, IReadOnlyList<TelegramAttachment>, CancellationToken, Task<string>> handleAttachments)
    {
        try
        {
            List<TelegramAttachment> files = [];
            List<string> captions = [];
            long firstUpdateId = updates[0].GetProperty("update_id").GetInt64();
            long chatId = 0;
            foreach (JsonElement update in updates)
            {
                if (!TryGetAllowedMessage(update, log, out JsonElement message, out chatId) || !HasAttachment(message))
                    continue;
                (TelegramAttachment file, string caption) = await DownloadMessageAttachmentAsync(
                    message, update.GetProperty("update_id").GetInt64(), cancellationToken);
                files.Add(file);
                if (!string.IsNullOrWhiteSpace(caption))
                    captions.Add(caption.Trim());
            }

            if (files.Count == 0)
                return;

            string text = captions.Count > 0 ? string.Join("\n", captions) : $"Ekteki {files.Count} dosyayı incele.";
            string result = await handleAttachments(firstUpdateId, text, chatId, files, cancellationToken);
            if (!string.IsNullOrWhiteSpace(result))
            {
                await SendMessageAsync(result, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            log("Telegram albümü işlenemedi: " + Redact(exception.GetBaseException().Message));
            await SendMessageAsync("Albüm işlenemedi: " + Redact(exception.GetBaseException().Message), cancellationToken);
        }
    }

    private async Task ProcessUpdateAsync(
        JsonElement update,
        long updateId,
        Func<long, string, long, CancellationToken, Task<string>> handleCommand,
        Func<long, string, long, CancellationToken, Task<string>> handleCallback,
        Action<string> log,
        CancellationToken cancellationToken,
        Func<long, string, long, IReadOnlyList<TelegramAttachment>, CancellationToken, Task<string>>? handleAttachments)
    {
        if (update.TryGetProperty("callback_query", out JsonElement callback))
        {
            await ProcessCallbackAsync(callback, updateId, handleCallback, log, cancellationToken);
            return;
        }

        if (!TryGetAllowedMessage(update, log, out JsonElement message, out long chatId))
        {
            return;
        }

        try
        {
            string result;
            if (HasAttachment(message))
            {
                if (handleAttachments is null)
                    throw new InvalidOperationException("Dosya alımı bu Watcher sürümünde etkin değil.");

                (TelegramAttachment file, string caption) = await DownloadMessageAttachmentAsync(message, updateId, cancellationToken);
                result = await handleAttachments(updateId,
                    string.IsNullOrWhiteSpace(caption) ? "Ekteki dosyayı incele." : caption,
                    chatId, [file], cancellationToken);
            }
            else
            {
                if (!message.TryGetProperty("text", out JsonElement textValue)
                    || textValue.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(textValue.GetString())) return;
                result = await handleCommand(updateId, textValue.GetString()!, chatId, cancellationToken);
            }
            if (!string.IsNullOrWhiteSpace(result))
            {
                await SendMessageAsync(result, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            log("Telegram komutu işlenemedi: " + Redact(exception.GetBaseException().Message));
            await SendMessageAsync(
                "Komut işlenemedi: " + Redact(exception.GetBaseException().Message),
                cancellationToken);
        }
    }

    private async Task<TelegramAttachment> DownloadAttachmentAsync(
                JsonElement attachment, string defaultName, long updateId, CancellationToken cancellationToken)
            {
                string fileId = attachment.GetProperty("file_id").GetString()
                    ?? throw new InvalidDataException("Telegram dosya kimliği eksik.");
                if (string.IsNullOrWhiteSpace(fileId))
                    throw new InvalidDataException("Telegram dosya kimliği eksik.");
                string name = attachment.TryGetProperty("file_name", out JsonElement nameValue)
                    ? nameValue.GetString() ?? string.Empty : defaultName;
                string extension = Path.GetExtension(name);
                if (!AllowedExtensions.Contains(extension))
                    throw new InvalidDataException(
                        "Desteklenmeyen dosya türü. Fotoğraf, PDF, Office belgesi, metin, ZIP, ses veya video gönderin.");
                long? expectedSize = null;
                if (attachment.TryGetProperty("file_size", out JsonElement declaredSize))
                {
                    if (declaredSize.ValueKind != JsonValueKind.Number
                        || !declaredSize.TryGetInt64(out long declaredBytes) || declaredBytes <= 0
                        || declaredBytes > AttachmentSizeLimit)
                        throw new InvalidDataException("Telegram dosya boyutu geçersiz veya 20 MB sınırını aşıyor.");
                    expectedSize = declaredBytes;
                }
                using JsonDocument metadata = await CallAsync("getFile",
                    new Dictionary<string, object> { ["file_id"] = fileId }, cancellationToken);
                string remotePath = metadata.RootElement.GetProperty("result").GetProperty("file_path").GetString()
                    ?? throw new InvalidDataException("Telegram dosya yolu eksik.");
                if (remotePath.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '/' or '.' or '_' or '-'))
                    || remotePath.Split('/').Any(part => part is "" or "." or ".."))
                    throw new InvalidDataException("Telegram dosya yolu geçersiz.");
                string directory = Path.Combine(Path.GetDirectoryName(_offsetPath)!, "telegram-attachments");
                EnsureAttachmentDirectoryIsNotRedirected(directory);
                Directory.CreateDirectory(directory);
                EnsureAttachmentDirectoryIsNotRedirected(directory);
                string destination = Path.Combine(directory, updateId.ToString(CultureInfo.InvariantCulture) + extension.ToLowerInvariant());
                string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using CancellationTokenSource downloadCancellation =
                        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    downloadCancellation.CancelAfter(DownloadRequestTimeout);
                    using HttpResponseMessage response = await _httpClient.GetAsync(
                        $"https://api.telegram.org/file/bot{_token}/{remotePath}",
                        HttpCompletionOption.ResponseHeadersRead, downloadCancellation.Token);
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength > AttachmentSizeLimit)
                        throw new InvalidDataException("Telegram dosyası 20 MB sınırını aşıyor.");
                    await using Stream source = await response.Content.ReadAsStreamAsync(downloadCancellation.Token);
                    long size = 0;
                    await using (FileStream target = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        byte[] buffer = new byte[81920];
                        int count;
                        while ((count = await source.ReadAsync(buffer, downloadCancellation.Token)) != 0)
                        {
                            size += count;
                            if (size > AttachmentSizeLimit)
                                throw new InvalidDataException("Telegram dosyası 20 MB sınırını aşıyor.");
                            await target.WriteAsync(buffer.AsMemory(0, count), downloadCancellation.Token);
                        }
                    }
                    if (size == 0) throw new InvalidDataException("Telegram dosyası boş.");
                    if (expectedSize is long expected && size != expected)
                        throw new InvalidDataException("Telegram dosya boyutu kayıtla eşleşmiyor.");
                    File.Move(temporary, destination, true);
                    return new TelegramAttachment(destination, Path.GetFileName(name),
                        attachment.TryGetProperty("mime_type", out JsonElement mime) ? mime.GetString() : null, size);
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            }
    static void EnsureAttachmentDirectoryIsNotRedirected(string path)
    {
        for (DirectoryInfo? directory = new(Path.GetFullPath(path)); directory is not null; directory = directory.Parent)
        {
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Telegram ek klasörü başka bir konuma yönlendiriliyor.");
        }
    }

    private async Task ProcessCallbackAsync(
        JsonElement callback,
        long updateId,
        Func<long, string, long, CancellationToken, Task<string>> handleCallback,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        string? callbackId = callback.TryGetProperty("id", out JsonElement idValue)
            ? idValue.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(callbackId))
        {
            log("Telegram callback sorgusu geçersiz; callback ID bulunamadı.");
            return;
        }

        if (!callback.TryGetProperty("message", out JsonElement message)
            || !message.TryGetProperty("chat", out JsonElement chat)
            || !chat.TryGetProperty("id", out JsonElement chatIdValue)
            || !chatIdValue.TryGetInt64(out long chatId)
            || !callback.TryGetProperty("data", out JsonElement dataValue)
            || dataValue.ValueKind != JsonValueKind.String)
        {
            await AnswerCallbackAsync(callbackId, "Bu işlem artık kullanılamıyor.", log, cancellationToken);
            return;
        }

        if (chatId != _allowedChatId)
        {
            log($"Yetkisiz Telegram callback'i yok sayıldı (chat ID: {chatId}).");
            await AnswerCallbackAsync(callbackId, "Bu sohbet için yetki yok.", log, cancellationToken);
            return;
        }

        try
        {
            string result = await handleCallback(
                updateId,
                dataValue.GetString() ?? string.Empty,
                chatId,
                cancellationToken);
            await AnswerCallbackAsync(callbackId, result, log, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            string errorText = Redact(exception.GetBaseException().Message);
            log("Telegram callback işlenemedi: " + errorText);
            await AnswerCallbackAsync(callbackId, "İşlem başarısız: " + errorText, log, cancellationToken);
        }
    }

    private async Task AnswerCallbackAsync(
        string callbackId,
        string text,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        string safeText = text.Length <= 180 ? text : text[..180];
        try
        {
            using JsonDocument _ = await CallAsync(
                "answerCallbackQuery",
                new Dictionary<string, object>
                {
                    ["callback_query_id"] = callbackId,
                    ["text"] = safeText,
                    ["show_alert"] = !string.IsNullOrWhiteSpace(text)
                },
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Telegram rejects answers to callbacks older than ~15 s; the offset must still advance past this update.
            log("Telegram callback yanıtı gönderilemedi: " + Redact(exception.GetBaseException().Message));
        }
    }

    private Task<JsonDocument> CallAsync(
        string method,
        IReadOnlyDictionary<string, object>? parameters,
        CancellationToken cancellationToken) =>
        CallWithContentAsync(
            method,
            parameters is null
                ? null
                : new StringContent(JsonSerializer.Serialize(parameters), Encoding.UTF8, "application/json"),
            cancellationToken);

    private async Task<JsonDocument> CallWithContentAsync(
        string method,
        HttpContent? content,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        using HttpRequestMessage request = new(
            content is null ? HttpMethod.Get : HttpMethod.Post,
            $"https://api.telegram.org/bot{_token}/{method}");
        request.Content = content;

        using CancellationTokenSource requestCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestCancellation.CancelAfter(timeout ?? (method == "getUpdates" ? _longPollTimeout : _requestTimeout));
        using HttpResponseMessage response = await _httpClient.SendAsync(request, requestCancellation.Token);
        string payload = await response.Content.ReadAsStringAsync(requestCancellation.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Telegram {method} HTTP {(int)response.StatusCode}. Bot tokenı, chat ID veya webhook/long-polling durumunu kontrol edin.");
        }

        JsonDocument document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("ok", out JsonElement ok) || !ok.GetBoolean())
        {
            document.Dispose();
            throw new InvalidOperationException($"Telegram {method} isteği başarısız oldu.");
        }

        return document;
    }

    private static TimeSpan ValidateTimeout(TimeSpan timeout, string parameterName)
    {
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Telegram request timeouts must be finite and positive.");
        }

        return timeout;
    }

    private string Redact(string value) => value.Replace(_token, "[TOKEN GİZLENDİ]", StringComparison.Ordinal);

    private long LoadOffset()
    {
        if (!File.Exists(_offsetPath)
            || !long.TryParse(File.ReadAllText(_offsetPath), NumberStyles.Integer, CultureInfo.InvariantCulture, out long offset))
        {
            return 0;
        }

        return Math.Max(0, offset);
    }

    private void SaveOffset(long offset)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_offsetPath)!);
        string temporaryPath = _offsetPath + ".tmp";
        File.WriteAllText(temporaryPath, offset.ToString(CultureInfo.InvariantCulture), Encoding.ASCII);
        File.Move(temporaryPath, _offsetPath, overwrite: true);
    }

    private static IEnumerable<string> SplitMessage(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield return " ";
            yield break;
        }

        for (int offset = 0; offset < text.Length;)
        {
            int length = Math.Min(MessageChunkSize, text.Length - offset);
            if (offset + length < text.Length && char.IsHighSurrogate(text[offset + length - 1]))
            {
                length--;
            }

            yield return text.Substring(offset, length);
            offset += length;
        }
    }

    public void Dispose() => _httpClient.Dispose();
}

internal sealed record TelegramDeliveryReceipt(long ChatId, long MessageId);

internal sealed record TelegramInlineButton(string Text, string CallbackData);
