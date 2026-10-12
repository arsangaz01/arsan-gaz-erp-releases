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

        await ReleaseCommandsAsync(inbox, Expect);

        Console.WriteLine(failures.Count == 0 ? "SELFTEST OK" : $"SELFTEST FAILED ({failures.Count})");
        return failures.Count == 0 ? 0 : 1;
    }

    // /yayin, /yayin_onay and the APK follow-up against a fake root runner on a temporary spool folder.
    private static async Task ReleaseCommandsAsync(TelegramCopilotInbox inbox, Action<bool, string> expect)
    {
        string spool = Path.Combine(ServerPaths.StateRoot, "release-spool");
        Directory.CreateDirectory(Path.Combine(spool, "requests"));
        Directory.CreateDirectory(Path.Combine(spool, "results"));
        List<string> notes = [];
        List<string> requests = [];
        using CancellationTokenSource stop = new();
        Task runner = FakeReleaseRunnerAsync(spool, requests, stop.Token);
        ServerCommands commands = new(inbox, () => 0, new ReleaseClient(spool), text =>
        {
            lock (notes) notes.Add(text);
            return true;
        }, TimeSpan.FromMilliseconds(100));

        string pending = await commands.HandleAsync(90, "/yayin", ChatId, CancellationToken.None);
        expect(pending.Contains("bekleyen 1 commit", StringComparison.Ordinal) && pending.Contains("/yayin_onay abc1234", StringComparison.Ordinal)
               && !pending.Contains("@@", StringComparison.Ordinal), "/yayin shows pending work and the approval command");
        expect((await commands.HandleAsync(91, "/yayin_onay", ChatId, CancellationToken.None)).StartsWith("Kullanım", StringComparison.Ordinal),
            "/yayin_onay without a commit shows usage");
        expect((await commands.HandleAsync(92, "/yayin_onay abc1234; rm -rf /", ChatId, CancellationToken.None)).StartsWith("Kullanım", StringComparison.Ordinal),
            "/yayin_onay rejects anything but a commit id and 'zorla'");
        expect((await commands.HandleAsync(93, "/yayin_onay abc1234", ChatId, CancellationToken.None)).Contains("başladı", StringComparison.Ordinal),
            "/yayin_onay starts the release in the background");
        expect((await commands.HandleAsync(94, "/yayin_onay abc1234", ChatId, CancellationToken.None)).Contains("zaten sürüyor", StringComparison.Ordinal),
            "a second release is refused while one runs");
        await commands.BackgroundRelease!.WaitAsync(TimeSpan.FromSeconds(30));
        expect(notes.Any(note => note.StartsWith("✅ Yayın tamamlandı", StringComparison.Ordinal) && note.Contains("MobileApi yayında", StringComparison.Ordinal)),
            "release result is sent to Telegram");
        expect(notes.Any(note => note.Contains("Yeni APK yayında", StringComparison.Ordinal)), "APK is followed until it is published");
        lock (requests)
        {
            expect(requests.SequenceEqual(["pending:", "publish:abc1234", "apk:" + new string('f', 40), "apk:" + new string('f', 40)]),
                "runner received exactly the expected whitelisted requests");
        }

        await stop.CancelAsync();
        await runner.ContinueWith(_ => { });
    }

    private static async Task FakeReleaseRunnerAsync(string spool, List<string> seen, CancellationToken cancellationToken)
    {
        int apkCalls = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            foreach (string request in Directory.GetFiles(Path.Combine(spool, "requests"), "*.req"))
            {
                Dictionary<string, string> fields = File.ReadAllLines(request)
                    .Select(line => line.Split('=', 2)).Where(pair => pair.Length == 2).ToDictionary(pair => pair[0], pair => pair[1]);
                string id = Path.GetFileNameWithoutExtension(request);
                File.Delete(request);
                lock (seen) seen.Add(fields["action"] + ":" + fields["arg1"]);
                string output = fields["action"] switch
                {
                    "pending" => "📦 bekleyen 1 commit\n@@TARGET=abc1234" + new string('0', 33) + "\n@@PENDING=1\n@@MOBILEAPI=1\n@@CI=success\n",
                    "publish" => "🌐 MobileApi yayında\n@@APK=needed\n@@MAIN=" + new string('f', 40) + "\n",
                    "apk" => ++apkCalls == 1 ? "📱 APK derlemesi sürüyor.\n@@APK_STATE=waiting\n" : "📱 Yeni APK yayında: 1.7.5\n@@APK_STATE=published\n",
                    _ => "?\n"
                };
                if (fields["action"] == "publish")
                {
                    await Task.Delay(500, cancellationToken);
                }

                await File.WriteAllTextAsync(Path.Combine(spool, "results", id + ".out"), output, cancellationToken);
                await File.WriteAllTextAsync(Path.Combine(spool, "results", id + ".done"), "0\n", cancellationToken);
            }

            await Task.Delay(50, cancellationToken).ContinueWith(_ => { });
        }
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
