using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ArsanGazERP.AgentWatcher;

internal static class CopilotBridgeTimeouts
{
    public static readonly TimeSpan ReportWait = Timeout.InfiniteTimeSpan;

    public static bool HasExpired(DateTimeOffset startedUtc, DateTimeOffset nowUtc) =>
        false;

    public static bool HasWorkflowAttemptExpired(
        bool submitted,
        DateTime? deadlineUtc,
        DateTime nowUtc) =>
        submitted && deadlineUtc is DateTime deadline && nowUtc > deadline;
}

internal sealed record CopilotSessionReportAuthorization(string SessionId, DateTimeOffset NotBeforeUtc);

internal sealed record CopilotSessionReport(
    string EventId,
    DateTimeOffset TimestampUtc,
    string Text,
    string? TelegramTaskId = null);

internal sealed record CopilotSessionPendingReply(
    string EventId,
    DateTimeOffset TimestampUtc,
    string Text,
    string Origin,
    string? TelegramTaskId);

internal sealed record CopilotTelegramPendingStart(
    string EventId,
    DateTimeOffset TimestampUtc,
    string TelegramTaskId);

// One per Telegram request attempt: Copilot began the turn that carries the request reference.
internal sealed record CopilotTelegramStartNotice(
    string TelegramTaskId,
    string EventId,
    DateTimeOffset TimestampUtc,
    DateTimeOffset AttemptStartedUtc);

internal enum CopilotBridgeTaskStatus
{
    Unknown,
    Running,
    Completed,
    Failed
}

internal sealed record CopilotBridgeTaskOutcome(
    CopilotBridgeTaskStatus Status,
    DateTimeOffset? StartedUtc = null,
    DateTimeOffset? CompletedUtc = null);

internal sealed class CopilotSessionReportState
{
    public string SessionId { get; set; } = "";
    public long Offset { get; set; }
    public string Origin { get; set; } = CopilotSessionReportForwarder.UnknownOrigin;
    public string? TelegramTaskId { get; set; }
    public string? LastForwardedText { get; set; }
    public List<string> ForwardedEventIds { get; set; } = [];
    // A final answer is held until it is known not to be superseded by an autopilot continuation or task summary.
    public CopilotSessionPendingReply? PendingFinal { get; set; }
    // Telegram-origin replies wait here until the Watcher queue proves whether the bridge delivered them.
    public List<CopilotSessionPendingReply> PendingTelegram { get; set; } = [];
    public long LinesSinceRecognizedEvent { get; set; }
    // Set once the current Telegram-origin turn has produced its first root turn start.
    public bool TelegramTurnStarted { get; set; }
    public List<CopilotTelegramPendingStart> PendingTelegramStarts { get; set; } = [];
    public List<string> StartNoticeKeys { get; set; } = [];
}

internal sealed record CopilotSessionScanResult(
    IReadOnlyList<CopilotSessionReport> Reports,
    CopilotSessionReportState State,
    IReadOnlyList<string> Diagnostics,
    IReadOnlyList<CopilotTelegramStartNotice> StartNotices);
// Forwards exactly one terminal reply per root turn of one explicitly authorized Copilot session.
// Telegram-origin replies are forwarded only when the Watcher queue shows the bridge did not deliver them.
internal static partial class CopilotSessionReportForwarder
{
    public const string AuthorizationFileName = "copilot-session-report.json";
    public const string StateFileName = "copilot-session-report.state.json";
    public const string UnknownOrigin = "unknown";
    public const string LocalOrigin = "local";
    public const string TelegramOrigin = "telegram";
    // Must match the first line written by Tools/VSCodeTelegramBridge/prompt-builder.js.
    public const string TelegramBridgePromptPrefix =
        "Arsan Gaz ERP kullanıcısının izinli Telegram sohbetinden ilettiği isteği bu Copilot sohbetinde ele al.";
    public const string TelegramTaskReferencePrefix = "Telegram istek referansı: ";
    public const string ReportHeader = "VS Code Copilot oturum yanıtı (bilgisayardan gelen istek):";
    public const string LateTelegramReportHeaderFormat =
        "VS Code Copilot yanıtı (Telegram isteği {0}; köprü bu yanıtı süresi içinde teslim edemedi):";
    public static readonly TimeSpan FinalAnswerSettleDelay = TimeSpan.FromMinutes(2);
    // A start notice is only useful while it is current; older ones are dropped rather than sent late.
    public static readonly TimeSpan StartNoticeMaxAge = TimeSpan.FromMinutes(10);
    public const string StartNoticeFormat =
        "Copilot isteğinizi aldı ve çalışmaya başladı ({0}). Onay/soru kartları ve son rapor ayrıca gönderilecek.";
    public const long UnrecognizedFormatLineThreshold = 2000;
    const int MaxRememberedEventIds = 500;

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static CopilotSessionReportAuthorization? LoadAuthorization(string dataRoot)
    {
        string path = Path.Combine(dataRoot, AuthorizationFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = document.RootElement;
        string sessionId = root.TryGetProperty("sessionId", out JsonElement session) && session.ValueKind == JsonValueKind.String
            ? session.GetString() ?? ""
            : "";
        if (!Guid.TryParseExact(sessionId, "D", out _))
        {
            throw new InvalidDataException("Copilot oturum raporu yetkisinde geçerli bir oturum kimliği yok.");
        }

        if (!root.TryGetProperty("notBeforeUtc", out JsonElement notBefore)
            || notBefore.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(
                notBefore.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset notBeforeUtc))
        {
            throw new InvalidDataException("Copilot oturum raporu yetkisinde geçerli notBeforeUtc zamanı yok.");
        }

        return new CopilotSessionReportAuthorization(sessionId.ToLowerInvariant(), notBeforeUtc);
    }

    public static string GetEventsPath(string sessionStateRoot, CopilotSessionReportAuthorization authorization) =>
        Path.Combine(sessionStateRoot, authorization.SessionId, "events.jsonl");

    public static string DefaultSessionStateRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot", "session-state");

    public static CopilotSessionReportState LoadState(string dataRoot, CopilotSessionReportAuthorization authorization)
    {
        string path = Path.Combine(dataRoot, StateFileName);
        if (!File.Exists(path))
        {
            return new CopilotSessionReportState { SessionId = authorization.SessionId };
        }

        CopilotSessionReportState state = JsonSerializer.Deserialize<CopilotSessionReportState>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("Copilot oturum raporu durum dosyası boş.");
        return string.Equals(state.SessionId, authorization.SessionId, StringComparison.OrdinalIgnoreCase)
            ? state
            : new CopilotSessionReportState { SessionId = authorization.SessionId };
    }

    public static void SaveState(string dataRoot, CopilotSessionReportState state)
    {
        string path = Path.Combine(dataRoot, StateFileName);
        string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, JsonOptions), new UTF8Encoding(false));
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static CopilotSessionScanResult Scan(
        string eventsPath,
        CopilotSessionReportAuthorization authorization,
        CopilotSessionReportState previousState,
        DateTimeOffset nowUtc,
        Func<string, CopilotBridgeTaskOutcome> resolveTelegramTask)
    {
        CopilotSessionReportState state = new()
        {
            SessionId = authorization.SessionId,
            Offset = previousState.Offset,
            Origin = previousState.Origin,
            TelegramTaskId = previousState.TelegramTaskId,
            LastForwardedText = previousState.LastForwardedText,
            ForwardedEventIds = [.. previousState.ForwardedEventIds],
            PendingFinal = previousState.PendingFinal,
            PendingTelegram = [.. previousState.PendingTelegram],
            LinesSinceRecognizedEvent = previousState.LinesSinceRecognizedEvent,
            TelegramTurnStarted = previousState.TelegramTurnStarted,
            PendingTelegramStarts = [.. previousState.PendingTelegramStarts],
            StartNoticeKeys = [.. previousState.StartNoticeKeys]
        };
        ScanContext context = new(authorization, state, new HashSet<string>(state.ForwardedEventIds, StringComparer.Ordinal));

        using (FileStream stream = new(eventsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            if (stream.Length < state.Offset)
            {
                // The log was replaced; rescan it and rely on notBeforeUtc plus remembered ids to avoid history.
                state.Offset = 0;
                state.Origin = UnknownOrigin;
                state.TelegramTaskId = null;
                state.PendingFinal = null;
                state.TelegramTurnStarted = false;
                context.Diagnostics.Add("Copilot oturum olay günlüğü kısaldı veya değiştirildi; günlük baştan yeniden tarandı.");
            }

            // Only bytes appended since the persisted cursor are read, so steady-state ticks stay cheap on large logs.
            stream.Seek(state.Offset, SeekOrigin.Begin);
            using MemoryStream pending = new();
            byte[] buffer = new byte[81920];
            int read;
            long lineStart = state.Offset;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (int index = 0; index < read; index++)
                {
                    if (buffer[index] != (byte)'\n')
                    {
                        pending.WriteByte(buffer[index]);
                        continue;
                    }

                    long lineLength = pending.Length + 1;
                    ProcessLine(pending.GetBuffer().AsSpan(0, (int)pending.Length), context);
                    pending.SetLength(0);
                    lineStart += lineLength;
                    state.Offset = lineStart;
                }
            }
        }

        if (state.PendingFinal is { } settled && nowUtc - settled.TimestampUtc >= FinalAnswerSettleDelay)
        {
            state.PendingFinal = null;
            Emit(settled, context);
        }

        ResolvePendingTelegramStarts(context, nowUtc, resolveTelegramTask);
        ResolvePendingTelegramReplies(context, resolveTelegramTask);

        if (context.MalformedLines > 0)
        {
            context.Diagnostics.Add(
                $"Copilot oturum olay günlüğünde {context.MalformedLines} satır JSON olarak ayrıştırılamadı ve atlandı.");
        }

        if (context.UnrecognizedFinalAnswers > 0)
        {
            context.Diagnostics.Add(
                $"Copilot oturumunda {context.UnrecognizedFinalAnswers} yanıt tanınan son yanıt biçiminde değil (phase alanı yok); olay günlüğü biçimi değişmiş olabilir.");
        }

        if (context.IncompleteTerminalEvents > 0)
        {
            context.Diagnostics.Add(
                $"Copilot oturumunda {context.IncompleteTerminalEvents} son yanıt olayında kimlik, zaman veya metin eksik; bu yanıtlar aktarılamadı.");
        }

        if (state.LinesSinceRecognizedEvent >= UnrecognizedFormatLineThreshold)
        {
            context.Diagnostics.Add(
                $"Copilot oturum olay günlüğünün son {state.LinesSinceRecognizedEvent} satırında tanınan kullanıcı/yanıt olayı yok; günlük biçimi desteklenmiyor olabilir.");
        }

        if (context.IncompleteStartEvents > 0)
        {
            context.Diagnostics.Add(
                $"Copilot oturumunda {context.IncompleteStartEvents} Telegram tur başlangıcı olayında kimlik veya zaman eksik; başlangıç bildirimi gönderilemedi.");
        }

        if (state.ForwardedEventIds.Count > MaxRememberedEventIds)
        {
            state.ForwardedEventIds.RemoveRange(0, state.ForwardedEventIds.Count - MaxRememberedEventIds);
        }

        if (state.StartNoticeKeys.Count > MaxRememberedEventIds)
        {
            state.StartNoticeKeys.RemoveRange(0, state.StartNoticeKeys.Count - MaxRememberedEventIds);
        }

        return new CopilotSessionScanResult(context.Reports, state, context.Diagnostics, context.StartNotices);
    }

    public static CopilotBridgeTaskOutcome ResolveQueuedTask(string queueDirectory, string taskId)
    {
        if (!SafeTaskId().IsMatch(taskId))
        {
            return new CopilotBridgeTaskOutcome(CopilotBridgeTaskStatus.Unknown);
        }

        string Queued(string suffix) => Path.Combine(queueDirectory, taskId + suffix);
        if (File.Exists(Queued(".task.json")))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Queued(".task.json")));
                return new CopilotBridgeTaskOutcome(
                    CopilotBridgeTaskStatus.Running,
                    ReadQueueTime(document.RootElement, "Started"));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                return new CopilotBridgeTaskOutcome(CopilotBridgeTaskStatus.Running);
            }
        }

        if (File.Exists(Queued(".done.json")))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Queued(".done.json")));
                return new CopilotBridgeTaskOutcome(
                    CopilotBridgeTaskStatus.Completed,
                    ReadQueueTime(document.RootElement, "Started"),
                    ReadQueueTime(document.RootElement, "Completed"));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                // A done file being written or locked is retried on the next tick instead of being guessed.
                return new CopilotBridgeTaskOutcome(CopilotBridgeTaskStatus.Running);
            }
        }

        return File.Exists(Queued(".error.json")) || File.Exists(Queued(".cancelled.json"))
            ? new CopilotBridgeTaskOutcome(CopilotBridgeTaskStatus.Failed)
            : new CopilotBridgeTaskOutcome(CopilotBridgeTaskStatus.Unknown);
    }

    public static string FormatReport(CopilotSessionReport report) =>
        (report.TelegramTaskId is null
            ? ReportHeader
            : string.Format(CultureInfo.InvariantCulture, LateTelegramReportHeaderFormat, report.TelegramTaskId))
        + "\n\n" + report.Text;

    public static string GetOutboxId(CopilotSessionReportAuthorization authorization, CopilotSessionReport report) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes("copilot-session:" + authorization.SessionId + ":" + report.EventId)))[..32];

    public static string FormatStartNotice(CopilotTelegramStartNotice notice) =>
        string.Format(CultureInfo.InvariantCulture, StartNoticeFormat, notice.TelegramTaskId);

    public static string GetStartNoticeKey(CopilotTelegramStartNotice notice) =>
        notice.TelegramTaskId + ":" + notice.AttemptStartedUtc.UtcTicks.ToString(CultureInfo.InvariantCulture);

    public static string GetStartNoticeOutboxId(CopilotSessionReportAuthorization authorization, CopilotTelegramStartNotice notice) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes("copilot-telegram-start:" + authorization.SessionId + ":" + GetStartNoticeKey(notice))))[..32];

    public static string RedactCredentials(string text) => CredentialPattern().Replace(text, "[gizli bilgi kaldırıldı]");

    sealed class ScanContext(
        CopilotSessionReportAuthorization authorization,
        CopilotSessionReportState state,
        HashSet<string> forwarded)
    {
        public CopilotSessionReportAuthorization Authorization { get; } = authorization;
        public CopilotSessionReportState State { get; } = state;
        public HashSet<string> Forwarded { get; } = forwarded;
        public List<CopilotSessionReport> Reports { get; } = [];
        public List<string> Diagnostics { get; } = [];
        public List<CopilotTelegramStartNotice> StartNotices { get; } = [];
        public int MalformedLines { get; set; }
        public int IncompleteStartEvents { get; set; }
        public int UnrecognizedFinalAnswers { get; set; }
        public int IncompleteTerminalEvents { get; set; }
    }

    static void ProcessLine(ReadOnlySpan<byte> line, ScanContext context)
    {
        CopilotSessionReportState state = context.State;
        if (line.IsEmpty)
        {
            return;
        }

        if (!ContainsRelevantType(line))
        {
            state.LinesSinceRecognizedEvent++;
            return;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line.ToArray());
        }
        catch (JsonException)
        {
            // A corrupt line must not pin the cursor forever; it is skipped and surfaced as a diagnostic.
            context.MalformedLines++;
            return;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            string type = root.ValueKind == JsonValueKind.Object ? GetString(root, "type") ?? "" : "";
            if (type is not ("user.message" or "assistant.message" or "session.task_complete" or "assistant.turn_start"))
            {
                state.LinesSinceRecognizedEvent++;
                return;
            }

            state.LinesSinceRecognizedEvent = 0;

            // Sub-agent events carry an agentId; only the user-visible root conversation is reported.
            if (root.TryGetProperty("agentId", out JsonElement agent) && agent.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                return;
            }

            if (type == "assistant.turn_start")
            {
                RecordTelegramTurnStart(root, context);
                return;
            }

            if (!root.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Object)
            {
                if (type != "user.message")
                {
                    context.IncompleteTerminalEvents++;
                }

                return;
            }

            if (type == "user.message")
            {
                bool autopilotContinuation = GetString(data, "source") == "autopilot"
                    || (data.TryGetProperty("isAutopilotContinuation", out JsonElement autopilot) && autopilot.ValueKind == JsonValueKind.True);
                if (autopilotContinuation || GetString(data, "delivery") == "steering")
                {
                    // The held answer was not terminal: the turn continues and produces a newer reply or summary.
                    state.PendingFinal = null;
                    return;
                }

                FlushPendingFinal(context);
                string content = GetString(data, "content") ?? "";
                bool telegram = content.StartsWith(TelegramBridgePromptPrefix, StringComparison.Ordinal);
                state.Origin = telegram ? TelegramOrigin : LocalOrigin;
                state.TelegramTaskId = telegram ? ExtractTelegramTaskId(content) : null;
                state.TelegramTurnStarted = false;
                state.LastForwardedText = null;
                return;
            }

            string? text;
            if (type == "assistant.message")
            {
                bool hasToolRequests = data.TryGetProperty("toolRequests", out JsonElement tools)
                    && tools.ValueKind == JsonValueKind.Array
                    && tools.GetArrayLength() > 0;
                text = GetString(data, "content");
                if (hasToolRequests || string.IsNullOrWhiteSpace(text))
                {
                    return;
                }

                string? phase = GetString(data, "phase");
                if (phase is null)
                {
                    context.UnrecognizedFinalAnswers++;
                    return;
                }

                if (phase != "final_answer")
                {
                    return;
                }
            }
            else
            {
                text = GetString(data, "summary");
            }

            string? eventId = GetString(root, "id");
            if (string.IsNullOrWhiteSpace(text)
                || string.IsNullOrWhiteSpace(eventId)
                || !DateTimeOffset.TryParse(
                    GetString(root, "timestamp"),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out DateTimeOffset timestampUtc))
            {
                if (type == "session.task_complete" && string.IsNullOrWhiteSpace(text))
                {
                    // A summary-less task completion still ends the turn with the held final answer.
                    FlushPendingFinal(context);
                    return;
                }

                context.IncompleteTerminalEvents++;
                return;
            }

            CopilotSessionPendingReply reply = new(eventId, timestampUtc, text, state.Origin, state.TelegramTaskId);
            if (type == "session.task_complete")
            {
                // The task summary is the turn's single terminal representation; it supersedes the held answer.
                state.PendingFinal = null;
                Emit(reply, context);
                return;
            }

            FlushPendingFinal(context);
            state.PendingFinal = reply;
        }
    }

    static void RecordTelegramTurnStart(JsonElement root, ScanContext context)
    {
        CopilotSessionReportState state = context.State;
        // Autopilot continuations and later model calls start further turns; only the first one is the real start.
        if (state.Origin != TelegramOrigin || state.TelegramTaskId is not { } taskId || state.TelegramTurnStarted)
        {
            return;
        }

        state.TelegramTurnStarted = true;
        string? eventId = GetString(root, "id");
        if (string.IsNullOrWhiteSpace(eventId)
            || !DateTimeOffset.TryParse(
                GetString(root, "timestamp"),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset timestampUtc))
        {
            context.IncompleteStartEvents++;
            return;
        }

        if (timestampUtc >= context.Authorization.NotBeforeUtc)
        {
            state.PendingTelegramStarts.Add(new CopilotTelegramPendingStart(eventId, timestampUtc, taskId));
        }
    }

    static void ResolvePendingTelegramStarts(
        ScanContext context,
        DateTimeOffset nowUtc,
        Func<string, CopilotBridgeTaskOutcome> resolveTelegramTask)
    {
        CopilotSessionReportState state = context.State;
        for (int index = 0; index < state.PendingTelegramStarts.Count;)
        {
            CopilotTelegramPendingStart start = state.PendingTelegramStarts[index];
            if (nowUtc - start.TimestampUtc > StartNoticeMaxAge)
            {
                state.PendingTelegramStarts.RemoveAt(index);
                context.Diagnostics.Add(
                    $"Telegram isteği {start.TelegramTaskId} için Copilot başlangıç bildirimi zamanında doğrulanamadı; geç bildirim gönderilmedi.");
                continue;
            }

            CopilotBridgeTaskOutcome outcome = resolveTelegramTask(start.TelegramTaskId);
            if (outcome.Status == CopilotBridgeTaskStatus.Unknown
                || (outcome.Status == CopilotBridgeTaskStatus.Running && outcome.StartedUtc is null))
            {
                // The queue record may still be written or locked; retry on the next tick until the age limit.
                index++;
                continue;
            }

            state.PendingTelegramStarts.RemoveAt(index);
            // A finished attempt already has its final report; a turn older than the attempt belongs to a previous try.
            if (outcome.Status != CopilotBridgeTaskStatus.Running || start.TimestampUtc < outcome.StartedUtc)
            {
                continue;
            }

            CopilotTelegramStartNotice notice = new(
                start.TelegramTaskId,
                start.EventId,
                start.TimestampUtc,
                outcome.StartedUtc!.Value);
            string key = GetStartNoticeKey(notice);
            if (state.StartNoticeKeys.Contains(key, StringComparer.Ordinal))
            {
                continue;
            }

            state.StartNoticeKeys.Add(key);
            context.StartNotices.Add(notice);
        }
    }

    static void FlushPendingFinal(ScanContext context)
    {
        if (context.State.PendingFinal is { } pending)
        {
            context.State.PendingFinal = null;
            Emit(pending, context);
        }
    }

    static void Emit(CopilotSessionPendingReply reply, ScanContext context)
    {
        CopilotSessionReportState state = context.State;
        if (reply.TimestampUtc < context.Authorization.NotBeforeUtc || !context.Forwarded.Add(reply.EventId))
        {
            return;
        }

        state.ForwardedEventIds.Add(reply.EventId);
        if (reply.Origin == TelegramOrigin)
        {
            if (reply.TelegramTaskId is null)
            {
                return;
            }

            state.PendingTelegram.Add(reply);
            return;
        }

        if (reply.Origin != LocalOrigin || string.Equals(state.LastForwardedText, reply.Text, StringComparison.Ordinal))
        {
            return;
        }

        state.LastForwardedText = reply.Text;
        context.Reports.Add(new CopilotSessionReport(reply.EventId, reply.TimestampUtc, RedactCredentials(reply.Text)));
    }

    static void ResolvePendingTelegramReplies(
        ScanContext context,
        Func<string, CopilotBridgeTaskOutcome> resolveTelegramTask)
    {
        List<CopilotSessionPendingReply> pendingReplies = context.State.PendingTelegram;
        for (int index = 0; index < pendingReplies.Count;)
        {
            CopilotSessionPendingReply reply = pendingReplies[index];
            CopilotBridgeTaskOutcome outcome = resolveTelegramTask(reply.TelegramTaskId!);
            bool forward;
            switch (outcome.Status)
            {
                case CopilotBridgeTaskStatus.Running:
                    index++;
                    continue;
                case CopilotBridgeTaskStatus.Completed:
                    // The bridge captured replies written during the delivered attempt; earlier or later replies were never sent.
                    bool deliveredByBridge = outcome.CompletedUtc is not { } completedUtc
                        || ((outcome.StartedUtc is not { } startedUtc || reply.TimestampUtc >= startedUtc)
                            && reply.TimestampUtc <= completedUtc);
                    forward = !deliveredByBridge;
                    break;
                case CopilotBridgeTaskStatus.Failed:
                    forward = true;
                    break;
                default:
                    context.Diagnostics.Add(
                        $"Telegram isteği {reply.TelegramTaskId} için Gözcü kuyruğunda teslim kanıtı henüz bulunamadı; yanıt kalıcı olarak beklemede tutuluyor ve yinelenme riskine karşı gönderilmedi.");
                    index++;
                    continue;
            }

            pendingReplies.RemoveAt(index);
            if (forward)
            {
                context.Reports.Add(new CopilotSessionReport(
                    reply.EventId,
                    reply.TimestampUtc,
                    RedactCredentials(reply.Text),
                    reply.TelegramTaskId));
            }
        }
    }

    static string? ExtractTelegramTaskId(string content)
    {
        foreach (string line in content.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith(TelegramTaskReferencePrefix, StringComparison.Ordinal))
            {
                string taskId = trimmed[TelegramTaskReferencePrefix.Length..].Trim();
                return SafeTaskId().IsMatch(taskId) ? taskId : null;
            }
        }

        return null;
    }

    static DateTimeOffset? ReadQueueTime(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(
            value.GetString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset parsed)
            ? parsed
            : null;
    static bool ContainsRelevantType(ReadOnlySpan<byte> line) =>
        line.IndexOf("\"user.message\""u8) >= 0
        || line.IndexOf("\"assistant.message\""u8) >= 0
        || line.IndexOf("\"session.task_complete\""u8) >= 0
        || line.IndexOf("\"assistant.turn_start\""u8) >= 0;

    static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    [GeneratedRegex(
        @"\b\d{8,10}:[A-Za-z0-9_-]{35}\b|\bgh[pousr]_[A-Za-z0-9]{36,}\b|\bgithub_pat_[A-Za-z0-9_]{22,}\b|\bnvapi-[A-Za-z0-9_-]{20,}\b|\bsk-[A-Za-z0-9_-]{20,}\b|\bAIza[0-9A-Za-z_-]{35}\b|\bEAA[A-Za-z0-9]{40,}\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex CredentialPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeTaskId();
}
