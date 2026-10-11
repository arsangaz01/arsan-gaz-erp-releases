using System.IO;
using System.Text;
using System.Text.Json;

namespace ArsanGazERP.Services;

/// <summary>
/// Makes the Cursor agent (Tools/CursorTelegramBridge/bridge.js, Cursor SDK) Jarvis's brain: the chat message is written
/// to CopilotInbox, the bridge streams tool steps into the progress file and the answer comes back as the completion.
/// </summary>
internal static class CursorJarvisBrain
{
    public const string IdPrefix = "jarvischat-";
    private const int MaximumHistoryTurns = 8;
    private const int MaximumTurnLength = 800;
    private const int MaximumSnapshotLength = 24_000;
    private static readonly TimeSpan BridgeHeartbeatMaximumAge = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(700);
    // Stays below AgentChatService's 180 s turn timeout and the phone's 200 s HTTP timeout.
    private static readonly TimeSpan ChatWait = TimeSpan.FromSeconds(160);
    private static readonly TimeSpan BackgroundWait = TimeSpan.FromMinutes(45);
    private static readonly UTF8Encoding Utf8 = new(false);

    private static string Inbox => CursorEscalationService.Inbox;
    private static string SettingsPath => Path.Combine(AgentWatcherPaths.DataRoot, "jarvis-brain.json");

    public static bool IsEnabled
    {
        get
        {
            try
            {
                if (!File.Exists(SettingsPath)) return true;
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(SettingsPath, Utf8));
                return !document.RootElement.TryGetProperty("Brain", out JsonElement brain) ||
                       !string.Equals(brain.GetString(), "local", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                return true;
            }
        }
    }

    /// <summary>Tüm ajan sohbeti (/) Cursor köprüsüne gider; yerel LLM/Jarvis araçları devre dışı.</summary>
    public static bool IsCursorOnlyMode
    {
        get
        {
            try
            {
                if (!File.Exists(SettingsPath)) return false;
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(SettingsPath, Utf8));
                return document.RootElement.TryGetProperty("Mode", out JsonElement mode) &&
                       string.Equals(mode.GetString(), "cursor-only", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                return false;
            }
        }
    }

    public static void SetEnabled(bool enabled) => SaveSettings(enabled ? "cursor" : "local", IsCursorOnlyMode ? "cursor-only" : "cursor-first");

    public static void SetCursorOnlyMode(bool cursorOnly) =>
        SaveSettings(IsEnabled ? "cursor" : "local", cursorOnly ? "cursor-only" : "cursor-first");

    private static void SaveSettings(string brain, string mode)
    {
        Directory.CreateDirectory(AgentWatcherPaths.DataRoot);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new { Brain = brain, Mode = mode }), Utf8);
    }

    public static bool IsBridgeReady(out string reason)
    {
        string statusPath = Path.Combine(Inbox, "bridge-status.json");
        try
        {
            if (!File.Exists(statusPath))
            {
                reason = "Cursor köprüsü hiç çalışmamış";
                return false;
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(statusPath, Utf8));
            JsonElement root = document.RootElement;
            bool ready = root.TryGetProperty("ready", out JsonElement readyElement) && readyElement.ValueKind == JsonValueKind.True;
            string bridge = root.TryGetProperty("bridge", out JsonElement bridgeElement) ? bridgeElement.GetString() ?? "" : "";
            DateTimeOffset updated = root.TryGetProperty("updatedUtc", out JsonElement updatedElement) &&
                                     DateTimeOffset.TryParse(updatedElement.GetString(), out DateTimeOffset parsed)
                ? parsed
                : DateTimeOffset.MinValue;
            if (DateTimeOffset.UtcNow - updated > BridgeHeartbeatMaximumAge)
            {
                reason = "Cursor köprüsü çalışmıyor";
                return false;
            }

            if (!ready || !string.Equals(bridge, "cursor", StringComparison.OrdinalIgnoreCase))
            {
                reason = "Cursor köprüsü hazır değil";
                return false;
            }

            reason = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            reason = "Cursor köprüsünün durumu okunamadı";
            return false;
        }
    }

    public static async Task<string> RunAsync(
        IReadOnlyList<ErpChatTurn> history,
        string userMessage,
        Action<string>? progress,
        CancellationToken cancellationToken)
    {
        string id = await EnqueueAsync(history, userMessage, cancellationToken);
        progress?.Invoke("🤖 Cursor'a iletildi; düşünüyor…");
        DateTime deadline = DateTime.UtcNow + ChatWait;
        int reportedSteps = 0;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(PollInterval, cancellationToken);
            try
            {
                if (TryReadOutcome(id) is { } outcome)
                {
                    return outcome;
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
                continue;
            }

            IReadOnlyList<string> steps = ReadSteps(id);
            for (; reportedSteps < steps.Count; reportedSteps++)
            {
                progress?.Invoke(steps[reportedSteps]);
            }
        }

        _ = Task.Run(() => ForwardLateAnswerAsync(id));
        return "Cursor bu iş üzerinde hâlâ çalışıyor. Sonuç hazır olunca Telegram'a gönderilecek (kayıt: " + id + ").";
    }

    private static async Task<string> EnqueueAsync(
        IReadOnlyList<ErpChatTurn> history,
        string userMessage,
        CancellationToken cancellationToken)
    {
        string message = userMessage.Trim();
        if (message.Length > 4000) message = message[..4000];
        string id = IdPrefix + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        bool newConversation = !history.Any(turn => turn.Role == "user");
        string context = await BuildContextAsync(history, cancellationToken);

        Directory.CreateDirectory(Inbox);
        // The dispatch marker keeps the IDE chat watcher (ide-watch.ps1) from claiming chat messages; bridge.js answers them.
        WriteAtomically(Path.Combine(Inbox, id + ".dispatch.json"), JsonSerializer.Serialize(new
        {
            id,
            status = "dispatching",
            source = "jarvis-chat",
            createdUtc = DateTime.UtcNow.ToString("o")
        }));
        WriteAtomically(Path.Combine(Inbox, id + ".request.json"), JsonSerializer.Serialize(new
        {
            Id = id,
            TaskId = id,
            Text = message,
            Context = context,
            NewConversation = newConversation,
            CreatedUtc = DateTimeOffset.UtcNow.ToString("o")
        }, new JsonSerializerOptions { WriteIndented = true }));
        return id;
    }

    private static async Task<string> BuildContextAsync(IReadOnlyList<ErpChatTurn> history, CancellationToken cancellationToken)
    {
        StringBuilder context = new();
        context.AppendLine("Tarih: " + DateTime.Now.ToString("dd.MM.yyyy HH:mm, dddd", new System.Globalization.CultureInfo("tr-TR")));
        List<ErpChatTurn> recent = history
            .Where(turn => turn.Role is "user" or "assistant" && !string.IsNullOrWhiteSpace(turn.Content))
            .TakeLast(MaximumHistoryTurns)
            .ToList();
        if (recent.Count > 0)
        {
            context.AppendLine().AppendLine("Önceki sohbet:");
            foreach (ErpChatTurn turn in recent)
            {
                string content = turn.Content.Trim();
                if (content.Length > MaximumTurnLength) content = content[..MaximumTurnLength] + "…";
                context.AppendLine((turn.Role == "user" ? "Kullanıcı: " : "Jarvis: ") + content);
            }
        }

        if (CloudAgentSettingsStore.GetPermissions("jarvis").CanReadErpSummary)
        {
            try
            {
                string snapshot = await ErpContextSnapshotService.BuildAsync("jarvis", cancellationToken);
                if (snapshot.Length > MaximumSnapshotLength) snapshot = snapshot[..MaximumSnapshotLength] + "…";
                context.AppendLine().AppendLine("ERP özeti:").AppendLine(snapshot);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                context.AppendLine().AppendLine("ERP özeti alınamadı: " + exception.GetBaseException().Message);
            }
        }
        else
        {
            context.AppendLine().AppendLine("ERP özeti izni kapalı (Ajan Yetkileri → Jarvis → \"ERP özetini okuyabilir\").");
        }

        return context.ToString();
    }

    private static string? TryReadOutcome(string id)
    {
        string completion = Path.Combine(Inbox, id + ".completion.json");
        if (File.Exists(completion))
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(completion, Utf8));
            string report = document.RootElement.TryGetProperty("report", out JsonElement value) ? value.GetString() ?? "" : "";
            return report.Trim().Length > 0 ? report.Trim() : "Cursor işi bitirdi ancak metin cevabı üretmedi.";
        }

        string acknowledgement = Path.Combine(Inbox, id + ".ack.json");
        if (File.Exists(acknowledgement))
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(acknowledgement, Utf8));
            if (document.RootElement.TryGetProperty("status", out JsonElement status) && status.GetString() == "failed")
            {
                string error = document.RootElement.TryGetProperty("error", out JsonElement detail) ? detail.GetString() ?? "" : "";
                throw new InvalidOperationException(error.Length > 0 ? error : "Cursor isteği işleyemedi.");
            }
        }

        return null;
    }

    private static IReadOnlyList<string> ReadSteps(string id)
    {
        string progressPath = Path.Combine(Inbox, id + ".progress.json");
        try
        {
            if (!File.Exists(progressPath)) return [];
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(progressPath, Utf8));
            return document.RootElement.TryGetProperty("steps", out JsonElement steps) && steps.ValueKind == JsonValueKind.Array
                ? steps.EnumerateArray().Select(step => step.GetString() ?? "").Where(step => step.Length > 0).ToList()
                : [];
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return [];
        }
    }

    private static async Task ForwardLateAnswerAsync(string id)
    {
        DateTime deadline = DateTime.UtcNow + BackgroundWait;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            try
            {
                if (TryReadOutcome(id) is { } answer)
                {
                    TelegramOutbox.TryQueue("🤖 Jarvis (Cursor) sonucu:\n\n" + answer);
                    return;
                }
            }
            catch (InvalidOperationException exception)
            {
                TelegramOutbox.TryQueue("🤖 Jarvis (Cursor) isteği tamamlanamadı: " + exception.Message);
                return;
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
            }
        }
    }

    private static void WriteAtomically(string path, string json)
    {
        File.WriteAllText(path + ".tmp", json, Utf8);
        File.Move(path + ".tmp", path, overwrite: true);
    }
}
