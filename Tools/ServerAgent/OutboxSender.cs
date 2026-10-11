using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArsanGazERP.AgentWatcher;

namespace ArsanGazERP.ServerAgent;

// Delivers the AgentWatcher-format outbox (MobileApi notifications, collection reminders, Cursor completions).
// Progress is written back after every chunk so a restart never resends delivered parts.
internal sealed class OutboxSender(TelegramBotService bot, long chatId, TelegramCopilotInbox copilotInbox)
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);
    private readonly HashSet<string> reported = new(StringComparer.Ordinal);

    public int Pending => Directory.Exists(ServerPaths.TelegramOutbox)
        ? Directory.EnumerateFiles(ServerPaths.TelegramOutbox, "*.json").Count()
        : 0;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(5));
        do
        {
            try
            {
                await RunOnceAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Log.Write("OUTBOX", "Giden kutusu okunamadı: " + exception.Message);
            }
        }
        while (await WaitAsync(timer, cancellationToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await copilotInbox.ForwardCompletionsAsync(cancellationToken);
        await DeliverAllAsync(cancellationToken);
    }

    private async Task DeliverAllAsync(CancellationToken cancellationToken)
    {
        foreach (string path in Directory.EnumerateFiles(ServerPaths.TelegramOutbox, "*.json").Order(StringComparer.Ordinal).ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            JsonObject item;
            try
            {
                item = JsonNode.Parse(await File.ReadAllTextAsync(path, Utf8, cancellationToken))?.AsObject()
                       ?? throw new InvalidDataException("boş kayıt");
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException or InvalidOperationException)
            {
                if (reported.Add(path))
                {
                    Log.Write("OUTBOX", $"Kayıt okunamadı ({Path.GetFileName(path)}): {exception.Message}");
                }

                continue;
            }

            // Items that waited a day (e.g. while no bot token was configured) are archived instead of flooding the chat.
            if (Get<DateTime?>(item, "CreatedUtc") is DateTime created && DateTime.UtcNow - created.ToUniversalTime() > MaxAge
                && (Get<int?>(item, "DeliveredChunkCount") ?? 0) == 0)
            {
                Directory.CreateDirectory(ServerPaths.ExpiredOutbox);
                File.Move(path, Path.Combine(ServerPaths.ExpiredOutbox, Path.GetFileName(path)), overwrite: true);
                reported.Remove(path);
                Log.Write("OUTBOX", $"24 saatten eski kayıt gönderilmeden arşivlendi: {Path.GetFileName(path)}");
                continue;
            }

            if (Get<DateTime?>(item, "NextAttemptUtc") is DateTime next && next.ToUniversalTime() > DateTime.UtcNow)
            {
                continue;
            }

            long target = Get<long?>(item, "ChatId") ?? 0;
            if (target != chatId)
            {
                if (reported.Add(path))
                {
                    Log.Write("OUTBOX", $"Yetkili sohbet dışındaki kayıt bekletildi: {Path.GetFileName(path)}");
                }

                continue;
            }

            await DeliverAsync(path, item, cancellationToken);
        }
    }

    private async Task DeliverAsync(string path, JsonObject item, CancellationToken cancellationToken)
    {
        try
        {
            string report = Get<string>(item, "Report") ?? string.Empty;
            int delivered = Get<int?>(item, "DeliveredChunkCount") ?? 0;
            if (report.Trim().Length > 0 && delivered < TelegramBotService.GetMessageChunkCount(report))
            {
                await bot.SendMessageWithReceiptAsync(report, cancellationToken, delivered, async (_, token) =>
                {
                    item["DeliveredChunkCount"] = ++delivered;
                    await SaveAsync(path, item, token);
                });
            }

            List<string> files = item["FilePaths"] is JsonArray array
                ? array.Select(node => node?.GetValue<string>()).OfType<string>().ToList()
                : [];
            int deliveredFiles = Get<int?>(item, "DeliveredFileCount") ?? 0;
            for (int index = deliveredFiles; index < files.Count; index++)
            {
                if (File.Exists(files[index]))
                {
                    await bot.SendFileAsync(files[index], cancellationToken);
                }

                item["DeliveredFileCount"] = index + 1;
                await SaveAsync(path, item, cancellationToken);
            }

            string sent = Path.Combine(ServerPaths.SentOutbox, Path.GetFileName(path));
            File.Move(path, sent, overwrite: true);
            reported.Remove(path);
            Log.Write("OUTBOX", $"Telegram'a gönderildi: {Get<string>(item, "Kind") ?? "mesaj"} {Path.GetFileName(path)}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            int attempts = (Get<int?>(item, "Attempts") ?? 0) + 1;
            item["Attempts"] = attempts;
            item["LastError"] = exception.GetBaseException().Message;
            item["NextAttemptUtc"] = DateTime.UtcNow.AddSeconds(Math.Min(900, 15 * Math.Pow(2, Math.Min(attempts, 6))));
            await SaveAsync(path, item, cancellationToken);
            Log.Write("OUTBOX", $"Gönderilemedi ({Path.GetFileName(path)}, deneme {attempts}): {exception.GetBaseException().Message}");
        }
    }

    private static async Task SaveAsync(string path, JsonObject item, CancellationToken cancellationToken)
    {
        string temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, item.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Utf8, cancellationToken);
        File.Move(temporary, path, overwrite: true);
    }

    private static T? Get<T>(JsonObject item, string name)
    {
        Type type = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        if (item[name] is not JsonValue value)
        {
            return default;
        }

        object? result = type == typeof(string) ? (value.TryGetValue(out string? text) ? text : null)
            : type == typeof(long) ? (value.TryGetValue(out long number) ? number : value.TryGetValue(out string? s1) && long.TryParse(s1, out long n1) ? n1 : null)
            : type == typeof(int) ? (value.TryGetValue(out int small) ? small : null)
            : type == typeof(DateTime) ? (value.TryGetValue(out DateTime date) ? date : value.TryGetValue(out string? s2) && DateTime.TryParse(s2, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime d2) ? d2 : null)
            : null;
        return result is null ? default : (T)result;
    }
}
