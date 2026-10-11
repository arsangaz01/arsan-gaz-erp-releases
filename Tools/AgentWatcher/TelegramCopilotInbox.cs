using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ArsanGazERP.AgentWatcher;

internal sealed record BridgeRequest(
    string Id,
    string Text,
    DateTimeOffset CreatedUtc,
    string? Context = null,
    string? TaskId = null,
    IReadOnlyList<TelegramAttachment>? Attachments = null);

internal sealed record TelegramCopilotEnqueueResult(string Id, bool AlreadyQueued);

internal sealed class TelegramCopilotInbox(string inboxDirectory, string outboxDirectory, Action<string>? log = null)
{
    private readonly HashSet<string> reportedBadRecords = new(StringComparer.OrdinalIgnoreCase);

    public const string DirectResponseKind = "telegram-copilot-direct-response";
    public const string DirectTrackingSuffix = ".delivery.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public async Task<TelegramCopilotEnqueueResult> EnqueueAsync(
        long updateId,
        string text,
        long chatId,
        string? context,
        IReadOnlyList<TelegramAttachment>? attachments = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (updateId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(updateId), "Telegram update kimliği negatif olamaz.");
        }

        if (chatId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chatId), "Telegram hedef sohbeti geçersiz.");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("VS Code isteği boş olamaz.", nameof(text));
        }

        string requestId = "telegram-direct-" + updateId.ToString("D20", CultureInfo.InvariantCulture);
        string trackingPath = GetTrackingPath(requestId);
        string requestPath = Path.Combine(inboxDirectory, requestId + ".request.json");
        TelegramDirectTracking tracking;
        bool alreadyQueued = File.Exists(trackingPath);
        if (alreadyQueued)
        {
            tracking = JsonSerializer.Deserialize<TelegramDirectTracking>(
                await File.ReadAllTextAsync(trackingPath, cancellationToken),
                JsonOptions) ?? throw new InvalidDataException("Telegram doğrudan yönlendirme kaydı boş.");
            if (tracking.UpdateId != updateId || tracking.ChatId != chatId || tracking.Request.Id != requestId)
            {
                throw new InvalidDataException("Telegram update kimliği mevcut doğrudan yönlendirme kaydıyla çakışıyor.");
            }
        }
        else
        {
            BridgeRequest request = new(
                requestId,
                text,
                DateTimeOffset.UtcNow,
                string.IsNullOrWhiteSpace(context) ? null : context,
                requestId,
                attachments);
            tracking = new TelegramDirectTracking
            {
                UpdateId = updateId,
                ChatId = chatId,
                Request = request
            };
            await WriteJsonAtomicallyAsync(trackingPath, tracking, overwrite: false, cancellationToken);
        }

        string completionPath = Path.Combine(inboxDirectory, requestId + ".completion.json");
        string acknowledgementPath = Path.Combine(inboxDirectory, requestId + ".ack.json");
        if (!tracking.Delivered
            && !File.Exists(requestPath)
            && !File.Exists(completionPath)
            && !File.Exists(acknowledgementPath))
        {
            await WriteJsonAtomicallyAsync(requestPath, tracking.Request, overwrite: false, cancellationToken);
        }

        return new TelegramCopilotEnqueueResult(requestId, alreadyQueued);
    }

    public async Task<int> ForwardCompletionsAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(inboxDirectory))
        {
            return 0;
        }

        Directory.CreateDirectory(outboxDirectory);
        int forwarded = 0;
        foreach (string trackingPath in Directory.EnumerateFiles(inboxDirectory, "telegram-direct-*" + DirectTrackingSuffix)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await ForwardCompletionAsync(trackingPath, cancellationToken))
                {
                    forwarded++;
                }
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException
                                              or InvalidOperationException or KeyNotFoundException or FormatException)
            {
                if (reportedBadRecords.Add(trackingPath))
                {
                    log?.Invoke($"Telegram doğrudan yönlendirme kaydı atlandı ({Path.GetFileName(trackingPath)}): {exception.Message}");
                }
            }
        }

        return forwarded;
    }

    private async Task<bool> ForwardCompletionAsync(string trackingPath, CancellationToken cancellationToken)
    {
        TelegramDirectTracking tracking = JsonSerializer.Deserialize<TelegramDirectTracking>(
            await File.ReadAllTextAsync(trackingPath, cancellationToken),
            JsonOptions) ?? throw new InvalidDataException(
                $"Telegram doğrudan yönlendirme kaydı boş: {Path.GetFileName(trackingPath)}");
        if (tracking.Delivered)
        {
            return false;
        }

        (string Status, string? Report)? outcome = await ReadOutcomeAsync(tracking, cancellationToken);
        if (outcome is not { } result)
        {
            return false;
        }

        bool succeeded = string.Equals(result.Status, "completed", StringComparison.Ordinal);
        string report = succeeded
            ? result.Report ?? throw new InvalidDataException("VS Code tamamlanma raporu boş.")
            : "VS Code isteği Copilot'a gönderemedi: " + result.Report;
        string outboxId = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(tracking.Request.TaskId ?? tracking.Request.Id)))[..32];
        string outboxPath = Path.Combine(outboxDirectory, outboxId + ".json");
        if (File.Exists(outboxPath))
        {
            using JsonDocument existing = JsonDocument.Parse(
                await File.ReadAllTextAsync(outboxPath, cancellationToken));
            JsonElement root = existing.RootElement;
            if (!root.TryGetProperty("TaskId", out JsonElement existingTaskId)
                || existingTaskId.GetString() != (tracking.Request.TaskId ?? tracking.Request.Id)
                || !root.TryGetProperty("ChatId", out JsonElement existingChatId)
                || existingChatId.GetInt64() != tracking.ChatId)
            {
                throw new InvalidDataException("Telegram giden kutusu kimlik çakışması; kayıt korunmuştur.");
            }
        }
        else
        {
            await WriteJsonAtomicallyAsync(outboxPath, new
            {
                Id = outboxId,
                Kind = succeeded ? DirectResponseKind : TelegramQueuePolicy.ErrorNoticeKind,
                TaskId = tracking.Request.TaskId ?? tracking.Request.Id,
                ChatId = tracking.ChatId,
                Report = report,
                CreatedUtc = DateTime.UtcNow,
                NextAttemptUtc = DateTime.UtcNow,
                Attempts = 0,
                DeliveredChunkCount = 0,
                Receipts = Array.Empty<object>(),
                LastError = (string?)null
            }, overwrite: false, cancellationToken);
        }

        tracking.Delivered = true;
        await WriteJsonAtomicallyAsync(trackingPath, tracking, overwrite: true, cancellationToken);
        return true;
    }

    private async Task<(string Status, string? Report)?> ReadOutcomeAsync(
        TelegramDirectTracking tracking,
        CancellationToken cancellationToken)
    {
        string requestId = tracking.Request.Id;
        string completionPath = Path.Combine(inboxDirectory, requestId + ".completion.json");
        if (File.Exists(completionPath))
        {
            BridgeCompletion? completion = JsonSerializer.Deserialize<BridgeCompletion>(
                await File.ReadAllTextAsync(completionPath, cancellationToken),
                JsonOptions);
            if (completion is null
                || completion.Id != requestId
                || completion.TaskId != (tracking.Request.TaskId ?? requestId))
            {
                throw new InvalidDataException("VS Code tamamlanma kaydı istek kimliğiyle eşleşmiyor.");
            }

            return (completion.Status, completion.Report);
        }

        string acknowledgementPath = Path.Combine(inboxDirectory, requestId + ".ack.json");
        if (File.Exists(acknowledgementPath))
        {
            BridgeAcknowledgement? acknowledgement = JsonSerializer.Deserialize<BridgeAcknowledgement>(
                await File.ReadAllTextAsync(acknowledgementPath, cancellationToken),
                JsonOptions);
            if (acknowledgement?.Status == "failed")
            {
                return ("failed", acknowledgement.Error);
            }
        }

        return null;
    }

    private string GetTrackingPath(string requestId) =>
        Path.Combine(inboxDirectory, requestId + DirectTrackingSuffix);

    private static async Task WriteJsonAtomicallyAsync<T>(
        string filePath,
        T value,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(filePath)
            ?? throw new InvalidDataException("Telegram köprü dosya yolu geçersiz.");
        Directory.CreateDirectory(directory);
        string temporaryPath = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(value, JsonOptions),
                new UTF8Encoding(false),
                cancellationToken);
            File.Move(temporaryPath, filePath, overwrite);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}

internal sealed class TelegramDirectTracking
{
    public long UpdateId { get; set; }
    public long ChatId { get; set; }
    public BridgeRequest Request { get; set; } = new("", "", DateTimeOffset.MinValue);
    public bool Delivered { get; set; }
}

internal sealed class BridgeCompletion
{
    public string Id { get; set; } = "";
    public string TaskId { get; set; } = "";
    public string Status { get; set; } = "";
    public string? Report { get; set; }
}

internal sealed class BridgeAcknowledgement
{
    public string Status { get; set; } = "";
    public string? Error { get; set; }
}
