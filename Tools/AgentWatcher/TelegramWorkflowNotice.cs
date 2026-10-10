using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace ArsanGazERP.AgentWatcher;

internal enum TelegramWorkflowStage
{
    Queued,
    Running,
    CopilotSubmitted,
    Review,
    Rework,
    Blocked,
    Completed
}

internal sealed record TelegramWorkflowNotice(
    string Id,
    string Kind,
    string TaskId,
    long ChatId,
    string Report,
    DateTime CreatedUtc,
    DateTime NextAttemptUtc,
    int Attempts,
    int DeliveredChunkCount,
    List<TelegramDeliveryReceipt> Receipts,
    string? LastError)
{
    internal const string OutboxKind = "workflow-progress";
    internal const string ApprovalOutboxKind = "workflow-approval";
    internal const int MaximumDeliveryAttempts = 7;
    internal static readonly TimeSpan MinimumSendInterval = TimeSpan.FromSeconds(3);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static TelegramWorkflowNotice Create(
        string taskId,
        long chatId,
        TelegramWorkflowStage stage,
        int attempt = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        if (taskId.Length > 80 || taskId.Any(character =>
            !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new ArgumentException("Task ID bildirimi geçerli bir görev kimliği olmalıdır.", nameof(taskId));
        }

        if (chatId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chatId), "Telegram chat ID sıfır olamaz.");
        }

        string stageText = stage switch
        {
            TelegramWorkflowStage.Queued => "Kullanıcı görevi sıraya alındı",
            TelegramWorkflowStage.Running => "Görev çalışmaya başladı",
            TelegramWorkflowStage.CopilotSubmitted => "İstek VS Code Copilot köprüsüne iletildi",
            TelegramWorkflowStage.Review => "Sonuç kabul ölçütleri için incelemeye alındı",
            TelegramWorkflowStage.Rework => "Eksik kabul ölçütleri için sınırlı yeniden çalışma bekliyor",
            TelegramWorkflowStage.Blocked => "İş kanıt veya izin doğrulaması beklediği için durduruldu",
            TelegramWorkflowStage.Completed => "Görev tamamlandı; yerel rapor hazır",
            _ => throw new ArgumentOutOfRangeException(nameof(stage))
        };
        string identity = taskId + "\n" + stage + "\n" + attempt;
        string noticeId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..32];
        string shortTaskId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(taskId)))[..12];
        DateTime now = DateTime.UtcNow;

        return new(
            noticeId,
            OutboxKind,
            shortTaskId,
            chatId,
            $"AgentWatcher görev {shortTaskId}: {stageText}.",
            now,
            now,
            0,
            0,
            [],
            null);
    }

    internal static TelegramWorkflowNotice CreateApproval(
        string taskId,
        long chatId,
        string contractHash,
        bool approvalAlreadyGranted = false)
    {
        ValidateTaskId(taskId);
        if (chatId == 0)
            throw new ArgumentOutOfRangeException(nameof(chatId), "Telegram chat ID sıfır olamaz.");
        ArgumentException.ThrowIfNullOrWhiteSpace(contractHash);

        string identity = taskId + "\napproval\n" + contractHash;
        string noticeId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..32];
        DateTime now = DateTime.UtcNow;
        string state = approvalAlreadyGranted
            ? "Onay kaydı güvenli kuyruğa aktarılmayı bekliyor"
            : "İşlem insan onayınızı bekliyor";
        return new(
            noticeId,
            ApprovalOutboxKind,
            taskId,
            chatId,
            $"{state}. Ayrıntıları AgentWatcher'daki Onaylar sekmesinde inceleyin. Devam etmek için /workflow-onay {taskId} gönderin.",
            now,
            now,
            0,
            0,
            [],
            null);
    }

    private static void ValidateTaskId(string taskId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        if (taskId.Length > 80 || taskId.Any(character =>
            !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new ArgumentException("Task ID bildirimi geçerli bir görev kimliği olmalıdır.", nameof(taskId));
        }
    }

    internal static async Task<bool> WriteAsync(
        string outboxDirectory,
        TelegramWorkflowNotice notice,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outboxDirectory);
        ArgumentNullException.ThrowIfNull(notice);
        Directory.CreateDirectory(outboxDirectory);
        string destination = Path.Combine(outboxDirectory, notice.Id + ".json");
        if (File.Exists(destination))
        {
            return false;
        }

        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporary,
                JsonSerializer.Serialize(notice, JsonOptions),
                new UTF8Encoding(false),
                cancellationToken);

            try
            {
                File.Move(temporary, destination, false);
                return true;
            }
            catch (IOException) when (File.Exists(destination))
            {
                return false;
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    internal static TimeSpan RateLimitDelay(DateTime nowUtc, DateTime allowedUtc) =>
        allowedUtc > nowUtc ? allowedUtc - nowUtc : TimeSpan.Zero;

    internal static DateTime NextSendAllowedUtc(DateTime sentUtc) =>
        sentUtc.Add(MinimumSendInterval);

    internal static bool IsRetryLimitReached(int attempts) =>
        attempts >= MaximumDeliveryAttempts;

    internal static string RedactDiagnostic(string diagnostic)
    {
        string redacted = Regex.Replace(
            diagnostic,
            @"\bBearer\s+[A-Za-z0-9._~+/-]+=*",
            "Bearer [REDACTED]",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        redacted = Regex.Replace(
            redacted,
            @"\b(api[_-]?key|access[_-]?token|password|secret)\s*[:=]\s*[""']?[^\s,""'}]+",
            "$1=[REDACTED]",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return redacted.Length <= 300 ? redacted : redacted[..300];
    }
}
