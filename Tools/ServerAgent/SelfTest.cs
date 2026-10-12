using System.Net;
using System.Text;
using System.Text.Json;
using ArsanGazERP.AgentWatcher;

namespace ArsanGazERP.ServerAgent;

// `--self-test`: exercises the outbox sender, the Cursor queue and the completion round trip against a fake Telegram
// API in a temporary data folder. Needs no token and touches no production data.
internal static class SelfTest
{
    private const long ChatId = 424242;

    public static async Task<int> RunAsync()
    {
        FakeTelegram telegram = new();
        using TelegramBotService bot = new("123:selftest", ChatId, telegram, Path.Combine(ServerPaths.StateRoot, "offset.txt"));
        ServerPaths.Ensure();
        string cursorAgentStatus = Path.Combine(ServerPaths.StateRoot, "cursoragent-status.json");
        File.Delete(cursorAgentStatus);
        Environment.SetEnvironmentVariable("CURSOR_AGENT_STATUS_FILE", cursorAgentStatus);
        TelegramCopilotInbox inbox = new(ServerPaths.CopilotInbox, ServerPaths.TelegramOutbox);
        OutboxSender sender = new(bot, ChatId, inbox);
        ServerCommands commands = new(inbox, () => sender.Pending);
        List<string> failures = [];
        void Expect(bool condition, string name)
        {
            Console.WriteLine((condition ? "PASS " : "FAIL ") + name);
            if (!condition) failures.Add(name);
        }

        string due = WriteOutbox("mobile-api-due", ChatId, "Mobil bildirim ğüşiöç", DateTime.UtcNow.AddMinutes(-1));
        string later = WriteOutbox("mobile-api-later", ChatId, "sonra", DateTime.UtcNow.AddHours(1));
        string foreign = WriteOutbox("mobile-api-foreign", 999, "başka sohbet", DateTime.UtcNow.AddMinutes(-1));
        string stale = WriteOutbox("mobile-api-stale", ChatId, "eski bildirim", DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddDays(-2));
        await sender.RunOnceAsync(CancellationToken.None);
        Expect(!File.Exists(stale) && File.Exists(Path.Combine(ServerPaths.ExpiredOutbox, Path.GetFileName(stale)))
               && !telegram.Texts.Contains("eski bildirim"), "items older than 24h are archived unsent");
        Expect(!File.Exists(due) && File.Exists(Path.Combine(ServerPaths.SentOutbox, Path.GetFileName(due))), "due outbox item delivered and archived");
        Expect(telegram.Texts.Contains("Mobil bildirim ğüşiöç"), "outbox text sent unchanged (UTF-8)");
        Expect(File.Exists(later) && File.Exists(foreign), "future and foreign-chat items are held");

        string reply = await commands.HandleAsync(77, "rapor hazırla", ChatId, CancellationToken.None);
        string requestId = "telegram-direct-" + 77.ToString("D20");
        Expect(File.Exists(Path.Combine(ServerPaths.CopilotInbox, requestId + ".request.json")), "plain text queued for Cursor");
        Expect(reply.Contains("kuyruğa", StringComparison.Ordinal), "offline PC reply mentions queue");
        Expect((await commands.HandleAsync(77, "rapor hazırla", ChatId, CancellationToken.None)).Contains("zaten", StringComparison.Ordinal),
            "duplicate update is not queued twice");

        await File.WriteAllTextAsync(Path.Combine(ServerPaths.CopilotInbox, requestId + ".completion.json"),
            JsonSerializer.Serialize(new { id = requestId, taskId = requestId, status = "completed", report = "Cursor raporu" }));
        await sender.RunOnceAsync(CancellationToken.None);
        Expect(telegram.Texts.Contains("Cursor raporu"), "Cursor completion forwarded to Telegram");
        await sender.RunOnceAsync(CancellationToken.None);
        Expect(telegram.Texts.Count(text => text == "Cursor raporu") == 1, "completion forwarded exactly once");

        Expect(ServerCommands.CursorAgentState() is (false, "kurulu değil"), "missing server Cursor agent reported");
        await File.WriteAllTextAsync(cursorAgentStatus, JsonSerializer.Serialize(new { ready = false, reason = "CURSOR_API_KEY eksik", updatedUtc = DateTime.UtcNow.ToString("o") }));
        Expect(ServerCommands.CursorAgentState() is (false, "bekliyor: CURSOR_API_KEY eksik"), "waiting server Cursor agent shows its reason");
        await File.WriteAllTextAsync(cursorAgentStatus, JsonSerializer.Serialize(new { ready = true, updatedUtc = DateTime.UtcNow.AddMinutes(-5).ToString("o") }));
        Expect(!ServerCommands.CursorAgentState().Ready, "stale server Cursor agent status is not ready");
        await File.WriteAllTextAsync(cursorAgentStatus, JsonSerializer.Serialize(new { ready = true, updatedUtc = DateTime.UtcNow.ToString("o") }));
        Expect((await commands.HandleAsync(81, "sunucu testi", ChatId, CancellationToken.None)).Contains("sunucudaki Cursor ajanına", StringComparison.Ordinal),
            "offline PC + ready server agent reply says the server handles it");

        Expect((await commands.HandleAsync(78, "/yardim", ChatId, CancellationToken.None)).Contains("/ozet", StringComparison.Ordinal), "/yardim lists commands");
        Expect((await commands.HandleAsync(79, "/gorev analiz", ChatId, CancellationToken.None)).Contains("AgentWatcher", StringComparison.Ordinal), "PC-only command explained");
        Expect((await commands.HandleAsync(80, "/emir", ChatId, CancellationToken.None)).StartsWith("Kullanım", StringComparison.Ordinal), "/emir without text shows usage");

        Console.WriteLine(failures.Count == 0 ? "SELFTEST OK" : $"SELFTEST FAILED ({failures.Count})");
        return failures.Count == 0 ? 0 : 1;
    }

    private static string WriteOutbox(string id, long chatId, string report, DateTime nextAttemptUtc, DateTime? createdUtc = null)
    {
        string path = Path.Combine(ServerPaths.TelegramOutbox, id + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            Id = id,
            Kind = "telegram-copilot-direct-response",
            TaskId = id,
            ChatId = chatId,
            Report = report,
            CreatedUtc = (createdUtc ?? DateTime.UtcNow).ToString("o"),
            NextAttemptUtc = nextAttemptUtc.ToString("o"),
            Attempts = 0,
            DeliveredChunkCount = 0,
            Receipts = Array.Empty<object>(),
            FilePaths = Array.Empty<string>(),
            DeliveredFileCount = 0,
            LastError = (string?)null
        }), new UTF8Encoding(false));
        return path;
    }

    private sealed class FakeTelegram : HttpMessageHandler
    {
        private int messageId;
        public List<string> Texts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("text", out JsonElement text))
            {
                Texts.Add(text.GetString() ?? string.Empty);
            }

            string json = JsonSerializer.Serialize(new { ok = true, result = new { message_id = ++messageId, chat = new { id = ChatId } } });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
