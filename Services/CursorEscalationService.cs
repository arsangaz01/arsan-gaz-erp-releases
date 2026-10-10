using System.IO;
using System.Text;
using System.Text.Json;

namespace ArsanGazERP.Services;

/// <summary>Queues a message for the authorized Telegram chat; the running AgentWatcher delivers it.</summary>
internal static class TelegramOutbox
{
    public static bool TryQueue(string text)
    {
        try
        {
            long? chatId = AgentChatService.GetTelegramNotificationChatId();
            if (chatId is null || string.IsNullOrWhiteSpace(text)) return false;
            string outbox = Path.Combine(AgentWatcherPaths.DataRoot, "TelegramOutbox");
            Directory.CreateDirectory(outbox);
            string id = "erp-agent-" + Guid.NewGuid().ToString("N");
            string now = DateTime.UtcNow.ToString("o");
            string json = JsonSerializer.Serialize(new
            {
                Id = id,
                Kind = "telegram-copilot-direct-response",
                TaskId = id,
                ChatId = chatId.Value,
                Report = text.Trim(),
                CreatedUtc = now,
                NextAttemptUtc = now,
                Attempts = 0,
                DeliveredChunkCount = 0,
                Receipts = Array.Empty<object>(),
                FilePaths = Array.Empty<string>(),
                DeliveredFileCount = 0,
                LastError = (string?)null
            });
            string destination = Path.Combine(outbox, id + ".json");
            File.WriteAllText(destination + ".tmp", json, new UTF8Encoding(false));
            File.Move(destination + ".tmp", destination);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            return false;
        }
    }
}

/// <summary>
/// Hands a request the ERP agents cannot handle to the Cursor coding agent through the same CopilotInbox
/// used by Telegram and the phone. The request text is always the user's own message, never model output,
/// so web content read by the agent cannot inject instructions.
/// </summary>
internal static class CursorEscalationService
{
    public const string IdPrefix = "jarvis-";
    private const int MaximumLength = 4000;

    public static string Inbox => Path.Combine(AgentWatcherPaths.DataRoot, "CopilotInbox");

    public static string Enqueue(string userMessage, string reason)
    {
        string message = userMessage.Trim();
        if (message.Length == 0) throw new ArgumentException("Aktarılacak mesaj boş.", nameof(userMessage));
        if (message.Length > MaximumLength) message = message[..MaximumLength];
        Directory.CreateDirectory(Inbox);
        string id = IdPrefix + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        string note = reason.Trim();
        if (note.Length > 400) note = note[..400];
        var request = new
        {
            Id = id,
            Text = message,
            CreatedUtc = DateTimeOffset.UtcNow.ToString("o"),
            Context = "Bu istek ERP içindeki Jarvis ajanından aktarıldı; Jarvis kendi araçlarıyla yapamadı" +
                      (note.Length > 0 ? " (gerekçe: " + note + ")" : string.Empty) +
                      ". Kullanıcı isteği yukarıdaki metindir. Rapor Telegram'da okunacak; kısa ve sade yaz. " +
                      "Gerekirse ERP ajanına bu işi yapacak aracı ekle. Onay gereken işlerde 'Onaylıyor musun? Evet/Hayır yazın' diye sor.",
            TaskId = id
        };
        string path = Path.Combine(Inbox, id + ".request.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(request, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
        File.Move(path + ".tmp", path);
        return id;
    }

    internal static bool LooksLikeRefusal(string answer)
    {
        string text = answer.Trim().ToLowerInvariant();
        if (text.Length > 600) return false;
        string[] phrases =
        [
            "yerine getiremiyorum", "yerine getiremem", "yapamıyorum", "yapamam", "yetkim yok", "erişimim yok",
            "bu konuda yardımcı olamıyorum", "yardımcı olamam", "mümkün değil", "desteklemiyorum", "i can't", "i cannot"
        ];
        return phrases.Any(phrase => text.Contains(phrase, StringComparison.Ordinal));
    }
}
