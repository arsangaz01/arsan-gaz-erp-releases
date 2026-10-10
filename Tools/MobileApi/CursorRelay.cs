using System.Text;
using System.Text.Json;
using ArsanGazERP.Services;

namespace ArsanGazERP.MobileApi;

internal sealed record CursorRequestStatus(string Id, string Status, string? Stage, string? Report);

// Phone requests use the same CopilotInbox as Telegram: the open Cursor chat (ide-watch) or bridge.js answers them.
internal static class CursorRelay
{
    public const string IdPrefix = "mobile-";
    private const int MaximumLength = 4000;
    private static readonly UTF8Encoding Utf8 = new(false);

    public static string Inbox => Path.Combine(AgentWatcherPaths.DataRoot, "CopilotInbox");

    public static string CreatePendingId() =>
        IdPrefix + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];

    public static string Enqueue(
        string text,
        string deviceName,
        IReadOnlyList<SavedMobileAttachment>? attachments = null,
        string? idOverride = null)
    {
        string message = text.Trim();
        if (message.Length is 0 or > MaximumLength)
        {
            throw new ArgumentException($"Mesaj 1-{MaximumLength} karakter olmalıdır.");
        }

        Directory.CreateDirectory(Inbox);
        string id = idOverride ?? CreatePendingId();
        if (!IsValidId(id))
        {
            throw new ArgumentException("İstek kimliği geçersiz.");
        }
        object? attachmentPayload = attachments?.Count > 0
            ? attachments.Select(a => new { fileName = a.FileName, localPath = a.LocalPath, kind = a.Kind }).ToArray()
            : null;
        if (attachmentPayload is not null)
        {
            foreach (SavedMobileAttachment attachment in attachments!)
            {
                string root = Path.GetFullPath(MobileAttachmentStore.Root);
                string attachmentPath = Path.GetFullPath(attachment.LocalPath);
                if (!attachmentPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || !File.Exists(attachmentPath))
                {
                    throw new ArgumentException("Ek dosyası doğrulanamadı.");
                }
            }
        }

        var request = new
        {
            Id = id,
            Text = message,
            CreatedUtc = DateTimeOffset.UtcNow.ToString("o"),
            Context = $"Bu istek Telegram'dan değil, Arsan Gaz ERP mobil uygulamasından ({deviceName}) geldi. " +
                      "Rapor telefonda okunacak; kısa ve sade yaz. Onay gereken işlerde 'Onaylıyor musun? Evet/Hayır yazın' diye sor.",
            TaskId = id,
            Attachments = attachmentPayload
        };
        string path = Path.Combine(Inbox, id + ".request.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(request, new JsonSerializerOptions { WriteIndented = true }), Utf8);
        File.Move(path + ".tmp", path);
        MobileLog.Write("Cursor ajanına mobil istek yazıldı: " + id);
        return id;
    }

    public static CursorRequestStatus? GetStatus(string id)
    {
        if (!IsValidId(id))
        {
            return null;
        }

        string completion = Path.Combine(Inbox, id + ".completion.json");
        if (File.Exists(completion))
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(completion, Utf8));
            string? report = document.RootElement.TryGetProperty("report", out JsonElement value) ? value.GetString() : null;
            string status = document.RootElement.TryGetProperty("status", out JsonElement state) ? state.GetString() ?? "completed" : "completed";
            return new CursorRequestStatus(id, status == "completed" ? "completed" : "failed", null, report);
        }

        if (!File.Exists(Path.Combine(Inbox, id + ".request.json")))
        {
            return null;
        }

        string? stage = File.Exists(Path.Combine(Inbox, id + ".ide-claim.json")) ? "Cursor sohbeti üzerinde çalışıyor" : null;
        string progress = Path.Combine(Inbox, id + ".progress.json");
        if (stage is null && File.Exists(progress))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(progress, Utf8));
                stage = document.RootElement.TryGetProperty("stage", out JsonElement value) ? value.GetString() : null;
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
            }
        }

        return new CursorRequestStatus(id, stage is null ? "queued" : "working", stage, null);
    }

    public static bool IsValidId(string id) =>
        (id.StartsWith(IdPrefix, StringComparison.Ordinal) || id.StartsWith(CursorEscalationService.IdPrefix, StringComparison.Ordinal))
        && id.Length <= 80
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}

// Mirrors finished phone requests to Telegram so the conversation stays visible there too.
internal sealed class CursorRelayNotifier : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                if (!Directory.Exists(CursorRelay.Inbox))
                {
                    continue;
                }

                IEnumerable<string> completions = Directory.EnumerateFiles(CursorRelay.Inbox, CursorRelay.IdPrefix + "*.completion.json")
                    .Concat(Directory.EnumerateFiles(CursorRelay.Inbox, CursorEscalationService.IdPrefix + "*.completion.json"));
                foreach (string completion in completions)
                {
                    string id = Path.GetFileName(completion)[..^".completion.json".Length];
                    string marker = Path.Combine(CursorRelay.Inbox, id + ".mobile-notified");
                    if (File.Exists(marker) || File.GetLastWriteTimeUtc(completion) < DateTime.UtcNow.AddDays(-1))
                    {
                        continue;
                    }

                    File.WriteAllText(marker, DateTime.UtcNow.ToString("o"));
                    CursorRequestStatus? status = CursorRelay.GetStatus(id);
                    if (status?.Report is { Length: > 0 } report)
                    {
                        TelegramNotifier.Send((id.StartsWith(CursorEscalationService.IdPrefix, StringComparison.Ordinal)
                            ? "🤖 Jarvis'in Cursor'a aktardığı isteğin sonucu:\n\n"
                            : "📱 Telefondan gelen isteğin sonucu (Cursor):\n\n") + report);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                MobileLog.Write("Mobil Cursor sonuçları Telegram'a aktarılamadı: " + exception.Message);
            }
        }
    }
}
