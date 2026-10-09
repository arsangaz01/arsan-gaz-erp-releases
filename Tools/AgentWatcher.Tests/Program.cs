using ArsanGazERP.AgentWatcher;
using ArsanGazERP.Services;
using System.Text.Json;

if (args.Contains("--startup-paths-only", StringComparer.Ordinal))
{
    StartupErpResolutionPrefersCurrentPublishedBundle();
    VersionedWatcherUsesCanonicalCurrentBundle();
    ArsanGazERP.AgentWatcher.Tests.AgentWatcherStartupPathTests.Run();
    ArsanGazERP.AgentWatcher.Tests.AgentWatcherStartupScriptTests.Run();
    Console.WriteLine("PASS: startup launches AgentWatcher in background and preserves optional ERP startup behavior.");
    return;
}

if (args.Length == 2 && args[0] == "--watcher-activation-send")
{
    AgentWatcherActivation.RequestShow(args[1], TimeSpan.FromSeconds(2));
    return;
}

if (args.Contains("--watcher-activation-only", StringComparer.Ordinal))
{
    await ArsanGazERP.AgentWatcher.Tests.AgentWatcherActivationTests.RunAsync();
    ArsanGazERP.AgentWatcher.Tests.AgentWatcherRestartTests.Run();
    return;
}

if (args.Contains("--watcher-restart-only", StringComparer.Ordinal))
{
    ArsanGazERP.AgentWatcher.Tests.AgentWatcherRestartTests.Run();
    return;
}

if (args.Contains("--hourly-local-automation-only", StringComparer.Ordinal))
{
    AgentWatcherWorkspaceTests.Run();
    await ArsanGazERP.AgentWatcher.Tests.HourlyLocalTestAutomationTests.RunAsync();
    Console.WriteLine("PASS: hourly local automation is opt-in, hourly spaced, bounded, fail-stop, restart-safe and shares queue priority (offline).");
    return;
}

if (args.Contains("--production-hourly-runner-only", StringComparer.Ordinal))
{
    string sourceRoot = AgentWatcherWorkspace.FindSourceRoot(Directory.GetCurrentDirectory())
        ?? throw new DirectoryNotFoundException("The production runner source workspace was not found.");
    await ArsanGazERP.AgentWatcher.Tests.HourlyLocalTestAutomationTests
        .RunProductionFixedRunnerAsync(sourceRoot);
    Console.WriteLine("PASS: production hourly constructor executed its fixed local test runner; success was persisted only after exit code zero (offline).");
    return;
}

if (args.Contains("--prepare-hourly-local-opt-in", StringComparer.Ordinal))
{
    string stateDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ArsanGazERP",
        "AgentWatcherData",
        "Reports");
    string statePath = Path.Combine(stateDirectory, "hourly-local-test-state.json");
    if (File.Exists(statePath))
    {
        throw new InvalidOperationException("An hourly runtime state already exists; it was not overwritten.");
    }

    using HourlyLocalTestAutomation pending = new(statePath, Directory.GetCurrentDirectory());
    if (!pending.EnableHourly())
    {
        throw new InvalidOperationException("The hourly opt-in was not created.");
    }

    HourlyLocalAutomationStatus pendingStatus = pending.Status;
    Console.WriteLine($"PASS: prepared opt-in only; runtime remains inactive until a compatible AgentWatcher starts. First due UTC: {pendingStatus.NextRunUtc:O}.");
    return;
}

if (args.Contains("--production-local-test-evidence-only", StringComparer.Ordinal))
{
    await ArsanGazERP.AgentWatcher.Tests.TelegramWorkflowNoticeTests
        .RunProductionLocalTestEvidenceAsync(Directory.GetCurrentDirectory());
    Console.WriteLine("PASS: the production LocalTestAutomation runner produced fresh, verifier-approved TestRun evidence (offline).");
    return;
}

if (args.Contains("--agent-chat-only", StringComparer.Ordinal))
{
    AgentChatRoutingTests.Run();
    LiveInfoTests.Run();
    JarvisAssistantTests.Run();
    await ArsanGazERP.AgentWatcher.Tests.AgentWatcherActivationTests.RunAsync();
    return;
}

if (args.Contains("--telegram-direct-routing-only", StringComparer.Ordinal))
{
    await TelegramDirectRoutingTests.RunAsync();
    Console.WriteLine("PASS: direct Telegram-to-VS Code inbox routing, ordering, idempotency and Telegram completion outbox (offline).");
    return;
}

if (args.Contains("--local-test-automation-only", StringComparer.Ordinal))
{
    await LocalTestAutomationTests.RunAsync();
    AgentWatcherWorkspaceTests.Run();
    await ArsanGazERP.AgentWatcher.Tests.HourlyLocalTestAutomationTests.RunAsync();
    return;
}

if (args.Contains("--workflow-orchestration-only", StringComparer.Ordinal))
{
    await WorkflowApprovalTests.RunAsync();
    await WorkflowDispatchPromptTests.RunAsync();
    await WorkflowActivationIntegrationTests.RunAsync();
    await ArsanGazERP.AgentWatcher.Tests.TelegramWorkflowNoticeTests.RunAsync();
    Console.WriteLine("PASS: durable queue -> correlated bridge -> review/rework -> evidence-gated completion and outbox (offline).");
    return;
}

AgentChatRoutingTests.Run();
LiveInfoTests.Run();
JarvisAssistantTests.Run();
ArsanGazERP.AgentWatcher.Tests.TelegramPriorityTests.Run();
await TelegramDirectRoutingTests.RunAsync();
await ShutdownWaitsForActiveWorkAsync();
await ShutdownContinuesCleanupAfterPollingFailureAsync();
await ShutdownDrainsAfterCancellationCallbackFailureAsync();
AgentWatcherDataRootResolutionPrefersExistingLocalData();
StartupErpResolutionPrefersCurrentPublishedBundle();
VersionedWatcherUsesCanonicalCurrentBundle();
await ErpShutdownWaitsForBackgroundScannerAsync();
await ErpShutdownCancelsAndDrainsScannerAsync();
SettingsRejectCorruptFilesWithoutOverwriting();
SettingsRejectUnreadablePath();
SettingsRoundTripPreservesAutonomyOptions();
ChatPromptIncludesRecentHistoryWithinProviderLimit();
await TelegramRetriesResumeAtFirstUndeliveredChunkAsync();
await TelegramHttpDeadlineTests.RunAsync();
await TelegramAttachmentTests.RunAsync();
SessionReportsForwardLocalOriginFinalResponses();
SessionReportsLeaveTimelyTelegramRepliesToBridge();
SessionReportsForwardLateTelegramRepliesOnce();
SessionReportsRetainMoreThanTwoHundredUnknownReplies();
SessionReportsResolveBridgeOutcomeFromQueueEvidence();
SessionReportsSendOneTerminalReplyPerTurn();
SessionReportsDeduplicateAndSkipHistory();
SessionReportsDiagnoseUnsupportedLogFormat();
SessionReportsStayWithinAuthorizedScope();
SessionReportsAnnounceTelegramTurnStartOnce();
SessionReportsDoNotAnnounceHandOffOrStaleStarts();
SessionReportsAnnounceEachRetryAttemptAndSurviveRestart();
SessionReportsResolveRunningAttemptStart();
CopilotReportWaitIsUnlimited();
TelegramArrivalOrderIsNumeric();
TelegramQueueSurvivesPauseAndRestart();
await TelegramPollingProcessesUpdatesSequentiallyAsync();
await GozcuAutonomyTests.RunAsync();
await LocalTestAutomationTests.RunAsync();
ArsanGazERP.AgentWatcher.Tests.AgentQueueManagementTests.Run();
ArsanGazERP.AgentWatcher.Tests.AgentWatcherLogTests.Run();
ArsanGazERP.AgentWatcher.Tests.AgentWatcherWindowLayoutTests.Run();
ArsanGazERP.AgentWatcher.Tests.AgentWatcherRestartTests.Run();
ArsanGazERP.AgentWatcher.Tests.TelegramPriorityTests.Run();
await ArsanGazERP.AgentWatcher.Tests.TelegramWorkflowNoticeTests.RunAsync();
await WorkflowApprovalTests.RunAsync();
await WorkflowDispatchPromptTests.RunAsync();
await WorkflowActivationIntegrationTests.RunAsync();
await ArsanGazERP.AgentWatcher.Tests.CampaignTests.RunAsync();
Console.WriteLine("PASS: existing ERP/AgentWatcher regressions and expanded offline campaign correctness/safety/transport regressions.");

static async Task ShutdownWaitsForActiveWorkAsync()
{
    using CancellationTokenSource closing = new();
    using CancellationTokenSource activeWork = new();
    TaskCompletionSource telegramStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    TaskCompletionSource scheduledWork = new(TaskCreationOptions.RunContinuationsAsynchronously);
    TaskCompletionSource queuedWork = new(TaskCreationOptions.RunContinuationsAsynchronously);
    List<string> events = [];

    Task shutdown = AgentWatcherShutdown.DrainAsync(
        () => events.Add("scheduler-stopped"),
        closing,
        () =>
        {
            activeWork.Cancel();
            events.Add("active-work-cancelled");
        },
        () =>
        {
            events.Add("telegram-stop-requested");
            return telegramStopped.Task;
        },
        () => Task.CompletedTask,
        () => scheduledWork.Task,
        () => queuedWork.Task,
        () =>
        {
            events.Add("runtime-disposed");
            return Task.CompletedTask;
        },
        () => events.Add("http-disposed"));

    Assert(closing.IsCancellationRequested, "Shutdown must cancel shared background work.");
    Assert(activeWork.IsCancellationRequested, "Shutdown must cancel the active queue operation.");
    Assert(!shutdown.IsCompleted, "Shutdown must wait for Telegram polling to stop.");

    telegramStopped.SetResult();
    await Task.Yield();
    Assert(!events.Contains("runtime-disposed"), "The runtime must not be disposed while queue work is active.");

    scheduledWork.SetResult();
    await Task.Yield();
    Assert(!events.Contains("runtime-disposed"), "Shutdown must also wait for the scheduled cycle.");

    queuedWork.SetResult();
    await shutdown;

    Assert(events.IndexOf("scheduler-stopped") < events.IndexOf("active-work-cancelled"),
        "Scheduling must stop before active work is cancelled.");
    Assert(events.IndexOf("runtime-disposed") > events.IndexOf("telegram-stop-requested"),
        "The runtime must be disposed only after Telegram polling has stopped.");
    Assert(events.IndexOf("http-disposed") > events.IndexOf("runtime-disposed"),
        "HTTP clients must be disposed after the runtime.");
}

static async Task ErpShutdownWaitsForBackgroundScannerAsync()
{
    CancellationTokenSource cancellation = new();
    TaskCompletionSource scannerStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Task scan = scannerStopped.Task;

    Task shutdown = BackgroundWorkShutdown.CancelAndDrainAsync(cancellation, scan);

    Assert(cancellation.IsCancellationRequested, "ERP shutdown must request cancellation of the background scan.");
    Assert(!shutdown.IsCompleted, "ERP shutdown must wait for the prospect scan to stop.");
    scannerStopped.SetResult();
    await shutdown;
    Assert(shutdown.IsCompletedSuccessfully, "ERP shutdown must complete after the prospect scan drains.");
}

static void AgentWatcherDataRootResolutionPrefersExistingLocalData()
{
    string testRoot = Path.Combine(Path.GetTempPath(), "AgentWatcher.Tests", Guid.NewGuid().ToString("N"));
    string projectRoot = Path.Combine(testRoot, "project");
    string localDataRoot = Path.Combine(testRoot, "local", "ArsanGazERP", "AgentWatcherData");
    string applicationBaseDirectory = Path.Combine(projectRoot, "bin", "Debug", "net10.0-windows");
    Directory.CreateDirectory(projectRoot);
    File.WriteAllText(Path.Combine(projectRoot, "ArsanGazERP.csproj"), string.Empty);

    try
    {
        Directory.CreateDirectory(localDataRoot);
        string resolvedExistingLocalRoot = AgentWatcherPaths.ResolveDataRoot(applicationBaseDirectory, localDataRoot);
        Assert(
            Path.GetFullPath(resolvedExistingLocalRoot).Equals(Path.GetFullPath(localDataRoot), StringComparison.OrdinalIgnoreCase),
            "An existing LocalAppData root must be preferred over the development data directory.");

        Directory.Delete(localDataRoot, recursive: true);
        string resolvedDevelopmentRoot = AgentWatcherPaths.ResolveDataRoot(applicationBaseDirectory, localDataRoot);
        string expectedDevelopmentRoot = Path.Combine(projectRoot, "Tools", "AgentWatcherData");
        Assert(
            Path.GetFullPath(resolvedDevelopmentRoot).Equals(Path.GetFullPath(expectedDevelopmentRoot), StringComparison.OrdinalIgnoreCase),
            "A development data root must be used when LocalAppData has not been initialized.");

        string externalApplicationBase = Path.Combine(testRoot, "published", "app");
        string resolvedPublishedRoot = AgentWatcherPaths.ResolveDataRoot(externalApplicationBase, localDataRoot);
        Assert(
            Path.GetFullPath(resolvedPublishedRoot).Equals(Path.GetFullPath(localDataRoot), StringComparison.OrdinalIgnoreCase),
            "A published application must use LocalAppData when no data root exists yet.");
    }
    finally
    {
        if (Directory.Exists(testRoot))
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }
}

static void VersionedWatcherUsesCanonicalCurrentBundle()
{
    string testRoot = Path.Combine(Path.GetTempPath(), "AgentWatcher.Tests", Guid.NewGuid().ToString("N"));
    string projectRoot = Path.Combine(testRoot, "project");
    string releaseRoot = Path.Combine(projectRoot, "publish", "release-test");
    string applicationBaseDirectory = Path.Combine(releaseRoot, "AgentWatcher");
    string releaseExecutable = Path.Combine(releaseRoot, "ArsanGazERP.exe");
    string currentExecutable = Path.Combine(projectRoot, "publish", "current", "ArsanGazERP.exe");
    Directory.CreateDirectory(applicationBaseDirectory);
    Directory.CreateDirectory(Path.GetDirectoryName(currentExecutable)!);
    File.WriteAllText(Path.Combine(projectRoot, "ArsanGazERP.csproj"), string.Empty);
    File.WriteAllText(releaseExecutable, string.Empty);
    File.WriteAllText(currentExecutable, string.Empty);

    try
    {
        Assert(
            Path.GetFullPath(currentExecutable).Equals(
                AgentWatcherPaths.ResolveStartupErpExecutable(applicationBaseDirectory, currentExecutable),
                StringComparison.OrdinalIgnoreCase),
            "A versioned Watcher must use the canonical current bundle for startup.");
        Assert(
            Path.GetFullPath(currentExecutable).Equals(
                AgentWatcherPaths.ResolveCurrentPublishedErpExecutablePath(releaseRoot),
                StringComparison.OrdinalIgnoreCase),
            "A versioned Watcher must resolve the canonical current ERP bundle.");

        File.Delete(currentExecutable);
        Assert(
            Path.GetFullPath(releaseExecutable).Equals(
                AgentWatcherPaths.ResolveStartupErpExecutable(applicationBaseDirectory, currentExecutable),
                StringComparison.OrdinalIgnoreCase),
            "If the current bundle is missing, startup must fall back to the versioned bundle.");
        File.Delete(releaseExecutable);
        Assert(
            AgentWatcherPaths.ResolveStartupErpExecutable(applicationBaseDirectory, currentExecutable) is null,
            "Startup must not return an executable when neither candidate exists.");
    }
    finally
    {
        Directory.Delete(testRoot, recursive: true);
    }
}

static void StartupErpResolutionPrefersCurrentPublishedBundle()
{
    string testRoot = Path.Combine(Path.GetTempPath(), "AgentWatcher.Tests", Guid.NewGuid().ToString("N"));
    string projectRoot = Path.Combine(testRoot, "project");
    string applicationBaseDirectory = Path.Combine(projectRoot, "Tools", "AgentWatcher", "bin");
    string currentExecutable = Path.Combine(projectRoot, "publish", "current", "ArsanGazERP.exe");
    string fallbackExecutable = Path.Combine(testRoot, "legacy", "ArsanGazERP.exe");
    Directory.CreateDirectory(applicationBaseDirectory);
    Directory.CreateDirectory(Path.GetDirectoryName(currentExecutable)!);
    Directory.CreateDirectory(Path.GetDirectoryName(fallbackExecutable)!);
    File.WriteAllText(Path.Combine(projectRoot, "ArsanGazERP.csproj"), string.Empty);
    File.WriteAllText(currentExecutable, string.Empty);
    File.WriteAllText(fallbackExecutable, string.Empty);

    try
    {
        string? preferred = AgentWatcherPaths.ResolveStartupErpExecutable(applicationBaseDirectory, fallbackExecutable);
        Assert(
            Path.GetFullPath(currentExecutable).Equals(preferred, StringComparison.OrdinalIgnoreCase),
            "Startup must prefer the executable in the current published bundle over a legacy fallback.");

        File.Delete(currentExecutable);
        string? fallback = AgentWatcherPaths.ResolveStartupErpExecutable(applicationBaseDirectory, fallbackExecutable);
        Assert(
            Path.GetFullPath(fallbackExecutable).Equals(fallback, StringComparison.OrdinalIgnoreCase),
            "Startup must use the existing fallback only when the current published executable is unavailable.");

        string? currentBundlePath =
            AgentWatcherPaths.ResolveCurrentPublishedErpExecutablePath(applicationBaseDirectory);
        Assert(
            Path.GetFullPath(currentExecutable).Equals(currentBundlePath, StringComparison.OrdinalIgnoreCase),
            "The startup script must retain the stable current-bundle path so later publications are picked up.");
    }
    finally
    {
        if (Directory.Exists(testRoot))
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }
}

static async Task ErpShutdownCancelsAndDrainsScannerAsync()
{
    CancellationTokenSource cancellation = new();
    TaskCompletionSource scannerStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Task scanner = Task.Run(async () =>
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            scannerStopped.SetResult();
        }
    });

    await BackgroundWorkShutdown.CancelAndDrainAsync(cancellation, scanner);

    Assert(scannerStopped.Task.IsCompleted, "ERP shutdown must await cooperative scanner cancellation.");
}

static void SettingsRejectCorruptFilesWithoutOverwriting()
{
    string directory = Path.Combine(Path.GetTempPath(), "AgentWatcher.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    string path = Path.Combine(directory, "settings.json");
    const string invalidSettings = "{\"EnableDailyResearch\":";
    File.WriteAllText(path, invalidSettings);

    try
    {
        try
        {
            _ = AppSettings.Load(path);
            throw new InvalidOperationException("Corrupt settings must not silently reset to autonomous defaults.");
        }
        catch (InvalidDataException)
        {
        }

        Assert(File.ReadAllText(path) == invalidSettings, "Invalid settings must remain intact for recovery.");
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static async Task ShutdownContinuesCleanupAfterPollingFailureAsync()
{
    using CancellationTokenSource closing = new();
    List<string> events = [];

    Task shutdown = AgentWatcherShutdown.DrainAsync(
        () => events.Add("scheduler-stopped"),
        closing,
        () => events.Add("active-work-cancelled"),
        () => Task.FromException(new IOException("simulated polling shutdown failure")),
        () => Task.CompletedTask,
        () => Task.CompletedTask,
        () => Task.CompletedTask,
        () =>
        {
            events.Add("runtime-disposed");
            return Task.CompletedTask;
        },
        () => events.Add("http-disposed"));

    try
    {
        await shutdown;
        throw new InvalidOperationException("Shutdown must surface polling cleanup failures.");
    }
    catch (AggregateException exception)
    {
        Assert(exception.InnerExceptions.Count == 1, "The polling cleanup failure must remain visible.");
    }

    Assert(events.Contains("runtime-disposed"), "A polling failure must not skip runtime cleanup.");
    Assert(events.Contains("http-disposed"), "A polling failure must not skip HTTP cleanup.");
    Assert(closing.IsCancellationRequested, "Shutdown must cancel background work before cleanup.");
}

static async Task ShutdownDrainsAfterCancellationCallbackFailureAsync()
{
    CancellationTokenSource closing = new();
    using CancellationTokenRegistration registration = closing.Token.Register(
        () => throw new InvalidOperationException("simulated cancellation callback failure"));
    TaskCompletionSource queueStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    List<string> events = [];

    Task shutdown = AgentWatcherShutdown.DrainAsync(
        () => events.Add("scheduler-stopped"),
        closing,
        () => events.Add("active-work-cancelled"),
        () => Task.CompletedTask,
        () => Task.CompletedTask,
        () => Task.CompletedTask,
        () => queueStopped.Task,
        () =>
        {
            events.Add("runtime-disposed");
            return Task.CompletedTask;
        },
        () => events.Add("http-disposed"));

    await Task.Yield();
    Assert(!events.Contains("runtime-disposed"), "Cancellation callback failures must not skip queue draining.");
    queueStopped.SetResult();
    try
    {
        await shutdown;
        throw new InvalidOperationException("The cancellation callback failure must remain visible.");
    }
    catch (AggregateException exception)
    {
        Assert(exception.InnerExceptions.Count == 1, "The callback failure must be surfaced once.");
    }

    Assert(events.Contains("runtime-disposed"), "Runtime cleanup must follow queue draining.");
    Assert(events.Contains("http-disposed"), "HTTP cleanup must still complete.");
}

static void SettingsRejectUnreadablePath()
{
    string directory = Path.Combine(Path.GetTempPath(), "AgentWatcher.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    string path = Path.Combine(directory, "settings.json");
    Directory.CreateDirectory(path);

    try
    {
        try
        {
            _ = AppSettings.Load(path);
            throw new InvalidOperationException("An unreadable settings path must not silently reset autonomous defaults.");
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void SettingsRoundTripPreservesAutonomyOptions()
{
    string directory = Path.Combine(Path.GetTempPath(), "AgentWatcher.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    string path = Path.Combine(directory, "settings.json");

    try
    {
        AppSettings settings = AppSettings.Load(path);
        settings.EnableDailyResearch = false;
        settings.EnableDailyReview = true;
        settings.LastDailyReviewQueueDate = "20261009";
        settings.TelegramChatId = "test-chat-id";
        settings.AgentInstructions["crm"] = "Önce müşteri takip risklerini değerlendir.";
        settings.Save();

        AppSettings restored = AppSettings.Load(path);
        Assert(!restored.EnableDailyResearch, "Saved research opt-out must survive restart.");
        Assert(restored.EnableDailyReview, "Saved daily review option must survive restart.");
        Assert(restored.LastDailyReviewQueueDate == "20261009",
            "The daily review queue marker must survive restart.");
        Assert(restored.TelegramChatId == "test-chat-id", "Saved Telegram destination must survive restart.");
        Assert(
            restored.AgentInstructions.TryGetValue("CRM", out string? instruction)
            && instruction == "Önce müşteri takip risklerini değerlendir.",
            "Per-agent instructions must survive restart and use case-insensitive agent ids.");
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
}

static void ChatPromptIncludesRecentHistoryWithinProviderLimit()
{
    ErpChatTurn[] history =
    [
        new("user", "Earlier question"),
        new("assistant", "Earlier answer")
    ];
    string prompt = AgentChatPromptBuilder.Build(history, "Continue from that answer.");

    Assert(prompt.Contains("Earlier question", StringComparison.Ordinal), "The prior user turn must be sent to agents.");
    Assert(prompt.Contains("Earlier answer", StringComparison.Ordinal), "The prior assistant turn must be sent to agents.");
    Assert(prompt.EndsWith("Continue from that answer.", StringComparison.Ordinal),
        "The current user message must remain intact and last.");

    string largeHistory = AgentChatPromptBuilder.Build(
        [new ErpChatTurn("assistant", new string('x', AgentChatPromptBuilder.MaximumMessageLength))],
        "Current request");
    Assert(largeHistory.Length <= AgentChatPromptBuilder.MaximumMessageLength,
        "Conversation context must respect the Cloudflare request limit.");
    Assert(largeHistory.Contains("Current request", StringComparison.Ordinal),
        "Truncating history must not discard the current request.");
}

static async Task TelegramRetriesResumeAtFirstUndeliveredChunkAsync()
{
    using SequencedTelegramHandler handler = new();
    string offsetPath = Path.Combine(
        Path.GetTempPath(),
        "AgentWatcher.Tests",
        Guid.NewGuid().ToString("N"),
        "telegram-offset.txt");
    using TelegramBotService telegram = new("test-token", 4242, handler, offsetPath);
    List<TelegramDeliveryReceipt> receipts = [];
    string report = new string('a', 3500) + new string('b', 3500);

    try
    {
        await telegram.SendMessageWithReceiptAsync(
            report,
            startChunkIndex: 0,
            onChunkDelivered: (receipt, _) =>
            {
                receipts.Add(receipt);
                return Task.CompletedTask;
            });
        throw new InvalidOperationException("The simulated second Telegram chunk must fail.");
    }
    catch (InvalidOperationException exception) when (
        exception.Message.Contains("HTTP 500", StringComparison.Ordinal))
    {
    }

    Assert(receipts.Count == 1, "The first accepted Telegram chunk must be checkpointed.");
    Assert(TelegramBotService.GetMessageChunkCount(report) == 2, "The mocked report should contain two chunks.");

    await telegram.SendMessageWithReceiptAsync(
        report,
        startChunkIndex: receipts.Count,
        onChunkDelivered: (receipt, _) =>
        {
            receipts.Add(receipt);
            return Task.CompletedTask;
        });

    Assert(receipts.Count == 2, "The retry must checkpoint the remaining Telegram chunk.");
    Assert(handler.SentChunks.Count(chunk => chunk == new string('a', 3500)) == 1,
        "A previously accepted Telegram chunk must not be sent again.");
    Assert(handler.SentChunks.Count(chunk => chunk == new string('b', 3500)) == 2,
        "Only the failed Telegram chunk should be retried.");
}

static string SessionEvent(string type, string id, string timestamp, string data, string? agentId = null) =>
    "{\"type\":\"" + type + "\",\"data\":" + data + ",\"id\":\"" + id + "\",\"timestamp\":\"" + timestamp + "\""
    + (agentId is null ? "" : ",\"agentId\":\"" + agentId + "\"") + "}\n";

static string UserEvent(string id, string timestamp, string content, string extra = "") =>
    SessionEvent("user.message", id, timestamp,
        "{\"content\":" + JsonSerializer.Serialize(content) + ",\"delivery\":\"idle\"" + extra + "}");

static string FinalEvent(string id, string timestamp, string content, string? agentId = null) =>
    SessionEvent("assistant.message", id, timestamp,
        "{\"content\":" + JsonSerializer.Serialize(content) + ",\"phase\":\"final_answer\",\"toolRequests\":[]}", agentId);

static string CompleteEvent(string id, string timestamp, string summary) =>
    SessionEvent("session.task_complete", id, timestamp,
        "{\"success\":true,\"summary\":" + JsonSerializer.Serialize(summary) + "}");

static CopilotSessionReportAuthorization SessionAuthorization() =>
    new("047243b4-126e-4d3a-9511-99d9315a6bad", DateTimeOffset.Parse("2026-10-04T12:00:00Z"));

static string TempEventsFile(string contents)
{
    string directory = Path.Combine(Path.GetTempPath(), "AgentWatcher.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    string path = Path.Combine(directory, "events.jsonl");
    File.WriteAllText(path, contents);
    return path;
}

static DateTimeOffset SettledNow() => DateTimeOffset.Parse("2026-10-04T13:00:00Z");

static CopilotBridgeTaskOutcome NoTelegramTask(string taskId) =>
    throw new InvalidOperationException("No Telegram task was expected: " + taskId);

static CopilotSessionScanResult ScanSession(
    string path,
    CopilotSessionReportState state,
    DateTimeOffset? now = null,
    Func<string, CopilotBridgeTaskOutcome>? resolve = null) =>
    CopilotSessionReportForwarder.Scan(path, SessionAuthorization(), state, now ?? SettledNow(), resolve ?? NoTelegramTask);

static CopilotSessionReportState FreshSessionState() => new() { SessionId = SessionAuthorization().SessionId };

static string TelegramPrompt(string taskId) =>
    CopilotSessionReportForwarder.TelegramBridgePromptPrefix + "\nÖnce talimatları incele.\n"
    + CopilotSessionReportForwarder.TelegramTaskReferencePrefix + taskId + "\n\nGüncel Telegram isteği:\nDurum nedir?";

static string ContinuationEvent(string id, string timestamp) =>
    UserEvent(id, timestamp, "", ",\"source\":\"autopilot\",\"isAutopilotContinuation\":true");

static void SessionReportsForwardLocalOriginFinalResponses()
{
    string path = TempEventsFile(
        UserEvent("u1", "2026-10-04T12:01:00Z", "Bilgisayardan istek")
        + SessionEvent("assistant.message", "c1", "2026-10-04T12:01:05Z",
            "{\"content\":\"Ara durum\",\"phase\":\"commentary\",\"toolRequests\":[{\"name\":\"view\"}]}")
        + SessionEvent("tool.execution_complete", "t1", "2026-10-04T12:01:06Z", "{\"result\":\"assistant.message gizli araç çıktısı\"}")
        + FinalEvent("s1", "2026-10-04T12:01:07Z", "Alt ajan yanıtı", "subagent-1")
        + FinalEvent("f1", "2026-10-04T12:01:10Z", "Son yanıt"));
    try
    {
        CopilotSessionScanResult early = ScanSession(path, FreshSessionState(), DateTimeOffset.Parse("2026-10-04T12:01:40Z"));
        Assert(early.Reports.Count == 0 && early.State.PendingFinal?.EventId == "f1",
            "A fresh final answer must be held briefly in case autopilot continues the turn.");
        Assert(early.State.Offset == new FileInfo(path).Length, "The scan cursor must advance past processed events.");

        CopilotSessionScanResult result = ScanSession(path, early.State, DateTimeOffset.Parse("2026-10-04T12:03:11Z"));
        Assert(result.Reports.Select(report => report.EventId).SequenceEqual(["f1"]),
            "A settled root final answer from a local request must be reported; tool and sub-agent output must not.");
        Assert(result.Reports[0].Text == "Son yanıt" && result.Reports[0].TelegramTaskId is null, "Final response text must be preserved.");
        Assert(CopilotSessionReportForwarder.FormatReport(result.Reports[0])
                == CopilotSessionReportForwarder.ReportHeader + "\n\nSon yanıt",
            "The Telegram report must contain the final response after the local-request header.");
        Assert(result.Diagnostics.Count == 0, "A supported log must not produce diagnostics.");
    }
    finally
    {
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }
}

static void SessionReportsLeaveTimelyTelegramRepliesToBridge()
{
    string path = TempEventsFile(
        UserEvent("u1", "2026-10-04T12:01:00Z", TelegramPrompt("telegram-41"))
        + FinalEvent("f1", "2026-10-04T12:02:00Z", "Telegram yanıtı")
        + ContinuationEvent("u2", "2026-10-04T12:02:05Z")
        + CompleteEvent("k1", "2026-10-04T12:03:00Z", "Telegram görev özeti")
        + UserEvent("u3", "2026-10-04T12:04:00Z", "Yerel istek")
        + FinalEvent("f2", "2026-10-04T12:05:00Z", "Yerel yanıt"));
    try
    {
        List<string> resolved = [];
        CopilotSessionScanResult result = ScanSession(path, FreshSessionState(), resolve: taskId =>
        {
            resolved.Add(taskId);
            return new CopilotBridgeTaskOutcome(
                CopilotBridgeTaskStatus.Completed,
                DateTimeOffset.Parse("2026-10-04T12:00:59Z"),
                DateTimeOffset.Parse("2026-10-04T12:03:02Z"));
        });

        Assert(resolved.SequenceEqual(["telegram-41"]), "The Telegram reply must be checked against its queued bridge task.");
        Assert(result.Reports.Select(report => report.EventId).SequenceEqual(["f2"]),
            "A Telegram reply the bridge delivered on time must not be sent again.");
        Assert(result.State.PendingTelegram.Count == 0, "A delivered Telegram reply must stop being tracked.");
    }
    finally
    {
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }
}

static void SessionReportsForwardLateTelegramRepliesOnce()
{
    string path = TempEventsFile(
        UserEvent("u1", "2026-10-04T12:01:00Z", TelegramPrompt("telegram-42"))
        + CompleteEvent("k1", "2026-10-04T13:05:00Z", "Bir saatten sonra biten yanıt"));
    try
    {
        CopilotBridgeTaskStatus status = CopilotBridgeTaskStatus.Running;
        Func<string, CopilotBridgeTaskOutcome> resolve = _ => new CopilotBridgeTaskOutcome(status);
        DateTimeOffset now = DateTimeOffset.Parse("2026-10-04T13:06:00Z");

        CopilotSessionScanResult running = ScanSession(path, FreshSessionState(), now, resolve);
        Assert(running.Reports.Count == 0 && running.State.PendingTelegram.Count == 1,
            "A Telegram reply must wait while the bridge task may still deliver it.");

        status = CopilotBridgeTaskStatus.Failed;
        CopilotSessionScanResult late = ScanSession(path, running.State, now, resolve);
        Assert(late.Reports.Select(report => report.EventId).SequenceEqual(["k1"]),
            "A Telegram reply that arrives after the bridge deadline must be forwarded from the session log.");
        Assert(late.Reports[0].TelegramTaskId == "telegram-42"
                && CopilotSessionReportForwarder.FormatReport(late.Reports[0]).Contains("telegram-42", StringComparison.Ordinal),
            "A late Telegram reply must identify the request it answers.");

        CopilotSessionScanResult again = ScanSession(path, late.State, now.AddHours(1), resolve);
        Assert(again.Reports.Count == 0 && again.State.PendingTelegram.Count == 0, "A late Telegram reply must be forwarded once.");

        File.AppendAllText(path,
            UserEvent("u2", "2026-10-04T13:10:00Z", TelegramPrompt("telegram-43"))
            + CompleteEvent("k2", "2026-10-04T13:11:00Z", "Önceki denemenin yanıtı")
            + UserEvent("u3", "2026-10-04T13:20:00Z", TelegramPrompt("telegram-44"))
            + CompleteEvent("k3", "2026-10-04T13:21:00Z", "Kanıtsız yanıt"));
        CopilotSessionScanResult mixed = ScanSession(path, again.State, DateTimeOffset.Parse("2026-10-05T14:00:00Z"), taskId => taskId switch
        {
            "telegram-43" => new CopilotBridgeTaskOutcome(
                CopilotBridgeTaskStatus.Completed,
                DateTimeOffset.Parse("2026-10-04T13:15:00Z"),
                DateTimeOffset.Parse("2026-10-04T13:16:00Z")),
            _ => new CopilotBridgeTaskOutcome(CopilotBridgeTaskStatus.Unknown)
        });
        Assert(mixed.Reports.Select(report => report.EventId).SequenceEqual(["k2"]),
            "A reply from an earlier failed attempt must still be forwarded after a later retry succeeds.");
        Assert(mixed.State.PendingTelegram is [{ EventId: "k3", TelegramTaskId: "telegram-44" }]
                && mixed.Diagnostics.Any(diagnostic => diagnostic.Contains("telegram-44", StringComparison.Ordinal)
                    && diagnostic.Contains("beklemede tutuluyor", StringComparison.Ordinal)),
            "A reply without queue evidence must remain pending with a diagnostic, not be dropped or sent blindly.");

        string stateRoot = Path.GetDirectoryName(path)!;
        CopilotSessionReportForwarder.SaveState(stateRoot, mixed.State);
        CopilotSessionReportState restarted = CopilotSessionReportForwarder.LoadState(stateRoot, SessionAuthorization());
        Assert(restarted.PendingTelegram is [{ EventId: "k3", TelegramTaskId: "telegram-44" }],
            "An unknown Telegram outcome must remain pending across Watcher restarts.");

        CopilotSessionScanResult resolved = ScanSession(
            path,
            restarted,
            DateTimeOffset.Parse("2026-10-15T14:00:00Z"),
            _ => new CopilotBridgeTaskOutcome(CopilotBridgeTaskStatus.Failed));
        Assert(resolved.Reports.Select(report => report.EventId).SequenceEqual(["k3"])
                && resolved.State.PendingTelegram.Count == 0,
            "A late report must become deliverable once failure evidence exists, even after ten days.");
        CopilotSessionScanResult deliveredAgain = ScanSession(
            path,
            resolved.State,
            DateTimeOffset.Parse("2026-10-15T14:01:00Z"),
            _ => new CopilotBridgeTaskOutcome(CopilotBridgeTaskStatus.Failed));
        Assert(deliveredAgain.Reports.Count == 0,
            "Resolving an indefinitely retained reply must not produce a duplicate report.");
    }
    finally
    {
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }
}

static void SessionReportsResolveBridgeOutcomeFromQueueEvidence()
{
    string queue = Path.Combine(Path.GetTempPath(), "AgentWatcher.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(queue);
    try
    {
        Assert(CopilotSessionReportForwarder.ResolveQueuedTask(queue, "telegram-1").Status == CopilotBridgeTaskStatus.Unknown,
            "A missing queue record must be unknown.");
        File.WriteAllText(Path.Combine(queue, "telegram-1.task.json"), "{}");
        Assert(CopilotSessionReportForwarder.ResolveQueuedTask(queue, "telegram-1").Status == CopilotBridgeTaskStatus.Running,
            "A queued or active task must be running.");
        File.Move(Path.Combine(queue, "telegram-1.task.json"), Path.Combine(queue, "telegram-1.error.json"));
        Assert(CopilotSessionReportForwarder.ResolveQueuedTask(queue, "telegram-1").Status == CopilotBridgeTaskStatus.Failed,
            "A timed-out or failed bridge task must be failed.");
        File.WriteAllText(Path.Combine(queue, "telegram-2.done.json"),
            "{\"Started\":\"2026-10-04T15:00:00+03:00\",\"Completed\":\"2026-10-04T15:20:00+03:00\"}");
        CopilotBridgeTaskOutcome done = CopilotSessionReportForwarder.ResolveQueuedTask(queue, "telegram-2");
        Assert(done.Status == CopilotBridgeTaskStatus.Completed
                && done.StartedUtc == DateTimeOffset.Parse("2026-10-04T12:00:00Z")
                && done.CompletedUtc == DateTimeOffset.Parse("2026-10-04T12:20:00Z"),
            "A delivered bridge task must expose its delivery window.");
        Assert(CopilotSessionReportForwarder.ResolveQueuedTask(queue, "..\\telegram-2").Status == CopilotBridgeTaskStatus.Unknown,
            "Task references from the log must not escape the queue directory.");
    }
    finally
    {
        Directory.Delete(queue, recursive: true);
    }
}

static void SessionReportsRetainMoreThanTwoHundredUnknownReplies()
{
    string events = string.Concat(Enumerable.Range(0, 205).Select(index =>
        UserEvent($"u{index}", "2026-10-04T12:01:00Z", TelegramPrompt($"telegram-{1000 + index}"))
        + CompleteEvent($"k{index}", "2026-10-04T12:02:00Z", $"Yanıt {index}")));
    string path = TempEventsFile(events);
    string dataRoot = Path.GetDirectoryName(path)!;
    try
    {
        CopilotSessionScanResult scan = ScanSession(
            path,
            FreshSessionState(),
            DateTimeOffset.Parse("2026-10-04T12:10:00Z"),
            _ => new CopilotBridgeTaskOutcome(CopilotBridgeTaskStatus.Unknown));
        Assert(scan.Reports.Count == 0 && scan.State.PendingTelegram.Count == 205,
            "Unknown Telegram outcomes must retain every pending reply instead of evicting older reports at a fixed limit.");

        CopilotSessionReportForwarder.SaveState(dataRoot, scan.State);
        CopilotSessionReportState restarted = CopilotSessionReportForwarder.LoadState(dataRoot, SessionAuthorization());
        Assert(restarted.PendingTelegram.Count == 205,
            "All pending Telegram replies must remain durable across restart.");
    }
    finally
    {
        Directory.Delete(dataRoot, recursive: true);
    }
}

static void SessionReportsSendOneTerminalReplyPerTurn()
{
    string path = TempEventsFile(
        UserEvent("u1", "2026-10-04T12:01:00Z", "Otomatik pilot isteği")
        + FinalEvent("f1", "2026-10-04T12:02:00Z", "Ara son yanıt")
        + ContinuationEvent("u2", "2026-10-04T12:02:04Z")
        + FinalEvent("f2", "2026-10-04T12:03:00Z", "İkinci ara yanıt")
        + ContinuationEvent("u3", "2026-10-04T12:03:04Z")
        + CompleteEvent("k1", "2026-10-04T12:04:00Z", "Görev özeti")
        + UserEvent("u4", "2026-10-04T12:10:00Z", "Normal istek")
        + FinalEvent("f3", "2026-10-04T12:11:00Z", "İlk yanıt")
        + FinalEvent("f4", "2026-10-04T12:20:00Z", "Bildirim sonrası yanıt"));
    try
    {
        CopilotSessionScanResult result = ScanSession(path, FreshSessionState());
        Assert(result.Reports.Select(report => report.EventId).SequenceEqual(["k1", "f3", "f4"]),
            "Each autopilot turn must send only its task summary, while separate replies to later notifications are still sent.");
    }
    finally
    {
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }
}

static void SessionReportsDeduplicateAndSkipHistory()
{
    string path = TempEventsFile(
        UserEvent("u0", "2026-10-04T11:00:00Z", "Eski istek")
        + FinalEvent("old", "2026-10-04T11:01:00Z", "Geçmiş yanıt")
        + UserEvent("u1", "2026-10-04T12:01:00Z", "Yeni istek")
        + FinalEvent("f1", "2026-10-04T12:02:00Z", "Yeni yanıt"));
    string dataRoot = Path.GetDirectoryName(path)!;
    try
    {
        CopilotSessionReportAuthorization authorization = SessionAuthorization();
        CopilotSessionScanResult first = ScanSession(path, CopilotSessionReportForwarder.LoadState(dataRoot, authorization));
        Assert(first.Reports.Select(report => report.EventId).SequenceEqual(["f1"]),
            "Responses written before authorization must not be forwarded as history.");
        CopilotSessionReportForwarder.SaveState(dataRoot, first.State);

        string partial = UserEvent("u2", "2026-10-04T12:03:00Z", "Sonraki istek")
            + FinalEvent("f2", "2026-10-04T12:04:00Z", "Sonraki yanıt");
        File.AppendAllText(path, partial[..^1]);
        CopilotSessionScanResult second = ScanSession(path, CopilotSessionReportForwarder.LoadState(dataRoot, authorization));
        Assert(second.Reports.Count == 0, "Already forwarded responses and incomplete lines must not be reported.");
        CopilotSessionReportForwarder.SaveState(dataRoot, second.State);

        File.AppendAllText(path, "\n");
        CopilotSessionScanResult third = ScanSession(path, CopilotSessionReportForwarder.LoadState(dataRoot, authorization));
        Assert(third.Reports.Select(report => report.EventId).SequenceEqual(["f2"]),
            "A completed line must be reported exactly once after it is flushed.");
        CopilotSessionReportForwarder.SaveState(dataRoot, third.State);

        CopilotSessionScanResult idle = ScanSession(path, CopilotSessionReportForwarder.LoadState(dataRoot, authorization));
        Assert(idle.Reports.Count == 0 && idle.State.Offset == third.State.Offset,
            "An idle tick must resume at the saved cursor instead of rescanning the log.");

        CopilotSessionScanResult replay = ScanSession(path, new CopilotSessionReportState
        {
            SessionId = authorization.SessionId,
            ForwardedEventIds = third.State.ForwardedEventIds
        });
        Assert(replay.Reports.Count == 0, "A rescanned log must not resend remembered events.");
        Assert(CopilotSessionReportForwarder.GetOutboxId(authorization, third.Reports[0])
                == CopilotSessionReportForwarder.GetOutboxId(authorization, third.Reports[0] with { Text = "farklı" }),
            "Outbox identity must depend on the session event, not mutable text.");
    }
    finally
    {
        Directory.Delete(dataRoot, recursive: true);
    }
}

static void SessionReportsDiagnoseUnsupportedLogFormat()
{
    string path = TempEventsFile(
        "{\"type\":\"user.message\",\"data\":{\"content\":\"bozuk\n"
        + UserEvent("u1", "2026-10-04T12:01:00Z", "Yerel istek")
        + SessionEvent("assistant.message", "x1", "2026-10-04T12:01:30Z",
            "{\"content\":\"Biçimi değişmiş yanıt\",\"toolRequests\":[]}")
        + FinalEvent("f1", "2026-10-04T12:02:00Z", "Geçerli yanıt"));
    try
    {
        CopilotSessionScanResult result = ScanSession(path, FreshSessionState());
        Assert(result.Reports.Select(report => report.EventId).SequenceEqual(["f1"]),
            "A corrupt line must not block later valid replies.");
        Assert(result.State.Offset == new FileInfo(path).Length, "A corrupt line must not pin the scan cursor.");
        Assert(result.Diagnostics.Any(diagnostic => diagnostic.Contains("ayrıştırılamadı", StringComparison.Ordinal)),
            "Malformed log lines must be reported explicitly.");
        Assert(result.Diagnostics.Any(diagnostic => diagnostic.Contains("phase", StringComparison.Ordinal)),
            "Replies in an unrecognized shape must be reported explicitly instead of being dropped silently.");

        File.AppendAllText(path, string.Concat(Enumerable.Range(0, (int)CopilotSessionReportForwarder.UnrecognizedFormatLineThreshold)
            .Select(index => "{\"kind\":\"conversation.reply\",\"seq\":" + index + "}\n")));
        CopilotSessionScanResult unknownFormat = ScanSession(path, result.State);
        Assert(unknownFormat.Reports.Count == 0
                && unknownFormat.Diagnostics.Any(diagnostic => diagnostic.Contains("biçimi desteklenmiyor", StringComparison.Ordinal)),
            "A log with no recognizable events must produce an explicit unsupported-format diagnostic.");
    }
    finally
    {
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }
}
static void SessionReportsStayWithinAuthorizedScope()
{
    string dataRoot = Path.Combine(Path.GetTempPath(), "AgentWatcher.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dataRoot);
    string authorizationPath = Path.Combine(dataRoot, CopilotSessionReportForwarder.AuthorizationFileName);
    try
    {
        Assert(CopilotSessionReportForwarder.LoadAuthorization(dataRoot) is null,
            "Session reporting must stay disabled without an explicit authorization file.");

        foreach (string invalid in new[]
        {
            "{\"sessionId\":\"..\\\\other\",\"notBeforeUtc\":\"2026-10-04T12:00:00Z\"}",
            "{\"sessionId\":\"047243b4-126e-4d3a-9511-99d9315a6bad\"}"
        })
        {
            File.WriteAllText(authorizationPath, invalid);
            try
            {
                _ = CopilotSessionReportForwarder.LoadAuthorization(dataRoot);
                throw new InvalidOperationException("Invalid session authorization must be rejected.");
            }
            catch (InvalidDataException)
            {
            }
        }

        File.WriteAllText(authorizationPath,
            "{\"sessionId\":\"047243B4-126E-4D3A-9511-99D9315A6BAD\",\"notBeforeUtc\":\"2026-10-04T12:00:00Z\"}");
        CopilotSessionReportAuthorization authorization = CopilotSessionReportForwarder.LoadAuthorization(dataRoot)!;
        Assert(CopilotSessionReportForwarder.GetEventsPath("C:\\state", authorization)
                == Path.Combine("C:\\state", "047243b4-126e-4d3a-9511-99d9315a6bad", "events.jsonl"),
            "Only the authorized session event log may be read.");

        CopilotSessionReportForwarder.SaveState(dataRoot, new CopilotSessionReportState
        {
            SessionId = "11111111-1111-1111-1111-111111111111",
            Offset = 999,
            ForwardedEventIds = ["x"]
        });
        CopilotSessionReportState state = CopilotSessionReportForwarder.LoadState(dataRoot, authorization);
        Assert(state.Offset == 0 && state.ForwardedEventIds.Count == 0,
            "Cursor state from another session must not be reused.");

        string redacted = CopilotSessionReportForwarder.RedactCredentials(
            "token 123456789:" + new string('A', 35) + " ve ghp_" + new string('b', 36));
        Assert(!redacted.Contains("AAAA", StringComparison.Ordinal) && !redacted.Contains("ghp_", StringComparison.Ordinal),
            "Obvious credentials must be removed before a session response enters the outbox.");
    }
    finally
    {
        Directory.Delete(dataRoot, recursive: true);
    }
}

static string TurnStartEvent(string id, string timestamp, string? agentId = null) =>
    SessionEvent("assistant.turn_start", id, timestamp, "{\"turnId\":\"0\",\"interactionId\":\"i\"}", agentId);

static Func<string, CopilotBridgeTaskOutcome> RunningAttempt(string started) =>
    _ => new CopilotBridgeTaskOutcome(CopilotBridgeTaskStatus.Running, DateTimeOffset.Parse(started));

static void SessionReportsAnnounceTelegramTurnStartOnce()
{
    string path = TempEventsFile(
        UserEvent("u1", "2026-10-04T12:01:00Z", TelegramPrompt("telegram-50"))
        + TurnStartEvent("t1", "2026-10-04T12:01:01Z")
        + TurnStartEvent("t2", "2026-10-04T12:01:20Z")
        + TurnStartEvent("s1", "2026-10-04T12:01:30Z", "subagent-1")
        + ContinuationEvent("u2", "2026-10-04T12:02:00Z")
        + TurnStartEvent("t3", "2026-10-04T12:02:01Z"));
    try
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-10-04T12:02:05Z");
        CopilotSessionScanResult first = ScanSession(path, FreshSessionState(), now, RunningAttempt("2026-10-04T12:00:58Z"));
        Assert(first.StartNotices.Count == 1
                && first.StartNotices[0].TelegramTaskId == "telegram-50"
                && first.StartNotices[0].EventId == "t1",
            "Exactly one start notice must be produced from the first root turn start of a Telegram request.");
        string text = CopilotSessionReportForwarder.FormatStartNotice(first.StartNotices[0]);
        Assert(text.Contains("telegram-50", StringComparison.Ordinal)
                && text.Contains("başladı", StringComparison.Ordinal)
                && !text.Contains("Durum nedir", StringComparison.Ordinal)
                && text.Length < 200,
            "The start notice must be short, name the request and not echo the request text.");
        Assert(first.Reports.Count == 0 && first.State.PendingTelegramStarts.Count == 0,
            "A start notice must not be treated as a final report.");
        Assert(CopilotSessionReportForwarder.GetStartNoticeOutboxId(SessionAuthorization(), first.StartNotices[0])
                != CopilotSessionReportForwarder.GetOutboxId(
                    SessionAuthorization(),
                    new CopilotSessionReport("t1", first.StartNotices[0].TimestampUtc, "x", "telegram-50")),
            "The start notice must use its own outbox slot so it never replaces the final report.");

        CopilotSessionScanResult idle = ScanSession(path, first.State, now.AddSeconds(5), RunningAttempt("2026-10-04T12:00:58Z"));
        Assert(idle.StartNotices.Count == 0, "A periodic tick must not repeat the start notice.");

        CopilotSessionReportState replayState = idle.State;
        replayState.Offset = 0;
        replayState.TelegramTurnStarted = false;
        CopilotSessionScanResult replay = ScanSession(path, replayState, now.AddSeconds(10), RunningAttempt("2026-10-04T12:00:58Z"));
        Assert(replay.StartNotices.Count == 0, "Replaying the same log must not announce the same attempt again.");

        File.AppendAllText(path,
            CompleteEvent("k1", "2026-10-04T12:05:00Z", "Telegram görev özeti")
            + UserEvent("u3", "2026-10-04T12:06:00Z", "Yerel istek")
            + TurnStartEvent("t4", "2026-10-04T12:06:01Z")
            + FinalEvent("f2", "2026-10-04T12:06:30Z", "Yerel yanıt"));
        CopilotSessionScanResult final = ScanSession(path, replay.State, DateTimeOffset.Parse("2026-10-04T12:20:00Z"), _ =>
            new CopilotBridgeTaskOutcome(
                CopilotBridgeTaskStatus.Failed));
        Assert(final.StartNotices.Count == 0, "A local request must never produce a Telegram start notice.");
        Assert(final.Reports.Select(report => report.EventId).OrderBy(id => id, StringComparer.Ordinal).SequenceEqual(["f2", "k1"])
                && final.Reports.Single(report => report.EventId == "k1").TelegramTaskId == "telegram-50",
            "Final and late Telegram reports must continue unchanged after a start notice.");
    }
    finally
    {
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }
}

static void SessionReportsDoNotAnnounceHandOffOrStaleStarts()
{
    string handOff = TempEventsFile(UserEvent("u1", "2026-10-04T12:01:00Z", TelegramPrompt("telegram-51")));
    string finished = TempEventsFile(
        UserEvent("u1", "2026-10-04T12:01:00Z", TelegramPrompt("telegram-52"))
        + TurnStartEvent("t1", "2026-10-04T12:01:01Z"));
    string previousAttempt = TempEventsFile(
        UserEvent("u1", "2026-10-04T12:01:00Z", TelegramPrompt("telegram-53"))
        + TurnStartEvent("t1", "2026-10-04T12:01:01Z"));
    string historical = TempEventsFile(
        UserEvent("u1", "2026-10-04T11:59:00Z", TelegramPrompt("telegram-54"))
        + TurnStartEvent("t1", "2026-10-04T11:59:01Z"));
    string incomplete = TempEventsFile(
        UserEvent("u1", "2026-10-04T12:01:00Z", TelegramPrompt("telegram-55"))
        + "{\"type\":\"assistant.turn_start\",\"data\":{}}\n");
    try
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-10-04T12:01:05Z");
        CopilotSessionScanResult queuedOnly = ScanSession(handOff, FreshSessionState(), now);
        Assert(queuedOnly.StartNotices.Count == 0 && queuedOnly.State.PendingTelegramStarts.Count == 0,
            "A delivered prompt without a Copilot turn start must not be reported as started.");

        foreach (CopilotBridgeTaskStatus status in new[] { CopilotBridgeTaskStatus.Completed, CopilotBridgeTaskStatus.Failed })
        {
            CopilotSessionScanResult done = ScanSession(finished, FreshSessionState(), now, _ => new CopilotBridgeTaskOutcome(status));
            Assert(done.StartNotices.Count == 0 && done.State.PendingTelegramStarts.Count == 0,
                "A request that already finished must get only its final/failure report, not a late start notice.");
        }

        CopilotSessionScanResult older = ScanSession(previousAttempt, FreshSessionState(), now, RunningAttempt("2026-10-04T12:01:03Z"));
        Assert(older.StartNotices.Count == 0, "A turn from a previous attempt must not announce the current attempt.");

        CopilotSessionScanResult beforeAuthorization = ScanSession(historical, FreshSessionState(), now, RunningAttempt("2026-10-04T11:58:00Z"));
        Assert(beforeAuthorization.StartNotices.Count == 0 && beforeAuthorization.State.PendingTelegramStarts.Count == 0,
            "History before the authorization window must not be announced.");

        CopilotSessionScanResult missing = ScanSession(incomplete, FreshSessionState(), now);
        Assert(missing.StartNotices.Count == 0
                && missing.Diagnostics.Any(diagnostic => diagnostic.Contains("tur başlangıcı", StringComparison.Ordinal)),
            "An unusable turn-start event must be surfaced as a diagnostic.");

        CopilotSessionScanResult locked = ScanSession(finished, FreshSessionState(), now, _ =>
            new CopilotBridgeTaskOutcome(CopilotBridgeTaskStatus.Running));
        Assert(locked.StartNotices.Count == 0 && locked.State.PendingTelegramStarts.Count == 1,
            "An unreadable attempt start must be retried, not guessed.");
        CopilotSessionScanResult expired = ScanSession(
            finished,
            locked.State,
            now.Add(CopilotSessionReportForwarder.StartNoticeMaxAge),
            RunningAttempt("2026-10-04T12:00:59Z"));
        Assert(expired.StartNotices.Count == 0
                && expired.State.PendingTelegramStarts.Count == 0
                && expired.Diagnostics.Any(diagnostic => diagnostic.Contains("telegram-52", StringComparison.Ordinal)),
            "A start notice that could not be verified in time must be dropped with a diagnostic, not sent late.");
    }
    finally
    {
        foreach (string path in new[] { handOff, finished, previousAttempt, historical, incomplete })
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}

static void SessionReportsAnnounceEachRetryAttemptAndSurviveRestart()
{
    string path = TempEventsFile(
        UserEvent("u1", "2026-10-04T12:01:00Z", TelegramPrompt("telegram-56"))
        + TurnStartEvent("t1", "2026-10-04T12:01:01Z"));
    string dataRoot = Path.GetDirectoryName(path)!;
    CopilotSessionReportAuthorization authorization = SessionAuthorization();
    try
    {
        CopilotSessionScanResult first = ScanSession(path, FreshSessionState(), DateTimeOffset.Parse("2026-10-04T12:01:05Z"),
            RunningAttempt("2026-10-04T12:00:59Z"));
        Assert(first.StartNotices.Count == 1, "The first attempt must be announced.");
        CopilotSessionReportForwarder.SaveState(dataRoot, first.State);

        CopilotSessionReportState restarted = CopilotSessionReportForwarder.LoadState(dataRoot, authorization);
        restarted.Offset = 0;
        CopilotSessionScanResult afterRestart = ScanSession(path, restarted, DateTimeOffset.Parse("2026-10-04T12:01:10Z"),
            RunningAttempt("2026-10-04T12:00:59Z"));
        Assert(afterRestart.StartNotices.Count == 0, "Persisted keys must prevent a duplicate notice after a Watcher restart.");

        File.AppendAllText(path,
            UserEvent("u2", "2026-10-04T12:10:00Z", TelegramPrompt("telegram-56"))
            + TurnStartEvent("t2", "2026-10-04T12:10:01Z"));
        CopilotSessionScanResult retry = ScanSession(path, afterRestart.State, DateTimeOffset.Parse("2026-10-04T12:10:05Z"),
            RunningAttempt("2026-10-04T12:09:59Z"));
        Assert(retry.StartNotices.Count == 1 && retry.StartNotices[0].EventId == "t2",
            "An explicit retry is a new attempt and must get its own single start notice.");
        Assert(CopilotSessionReportForwarder.GetStartNoticeOutboxId(authorization, retry.StartNotices[0])
                != CopilotSessionReportForwarder.GetStartNoticeOutboxId(authorization, first.StartNotices[0])
                && CopilotSessionReportForwarder.GetStartNoticeOutboxId(authorization, first.StartNotices[0])
                    == CopilotSessionReportForwarder.GetStartNoticeOutboxId(authorization, first.StartNotices[0] with { EventId = "other" }),
            "Start notice outbox ids must be stable per attempt and distinct across attempts.");
    }
    finally
    {
        Directory.Delete(dataRoot, recursive: true);
    }
}

static void SessionReportsResolveRunningAttemptStart()
{
    string queue = Path.Combine(Path.GetTempPath(), "AgentWatcher.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(queue);
    try
    {
        File.WriteAllText(Path.Combine(queue, "telegram-60.task.json"), "{\"Started\":\"2026-10-04T15:00:00+03:00\"}");
        CopilotBridgeTaskOutcome running = CopilotSessionReportForwarder.ResolveQueuedTask(queue, "telegram-60");
        Assert(running.Status == CopilotBridgeTaskStatus.Running
                && running.StartedUtc == DateTimeOffset.Parse("2026-10-04T12:00:00Z"),
            "An active task must expose the start of its current attempt.");
        File.WriteAllText(Path.Combine(queue, "telegram-61.task.json"), "{\"Started\":null}");
        File.WriteAllText(Path.Combine(queue, "telegram-62.task.json"), "yarım");
        Assert(CopilotSessionReportForwarder.ResolveQueuedTask(queue, "telegram-61") is { Status: CopilotBridgeTaskStatus.Running, StartedUtc: null }
                && CopilotSessionReportForwarder.ResolveQueuedTask(queue, "telegram-62") is { Status: CopilotBridgeTaskStatus.Running, StartedUtc: null },
            "A waiting or partially written task must stay running without an attempt start.");
    }
    finally
    {
        Directory.Delete(queue, recursive: true);
    }
}

static void CopilotReportWaitIsUnlimited()
{
    DateTimeOffset started = DateTimeOffset.Parse("2026-10-04T12:00:00Z");
    Assert(CopilotBridgeTimeouts.ReportWait == Timeout.InfiniteTimeSpan, "The Copilot report wait must be unlimited.");
    Assert(!CopilotBridgeTimeouts.HasExpired(started, started.AddMinutes(59).AddSeconds(59)),
        "A long-running Copilot response must still be awaited before one hour.");
    Assert(!CopilotBridgeTimeouts.HasExpired(started, started.AddYears(10)),
        "The Copilot report wait must not expire even after former deadlines.");
    DateTime workflowDeadline = DateTime.UtcNow.AddMinutes(-1);
    Assert(!CopilotBridgeTimeouts.HasWorkflowAttemptExpired(false, workflowDeadline, DateTime.UtcNow),
        "A workflow waiting for bridge submission must not expire before Copilot accepts it.");
    Assert(CopilotBridgeTimeouts.HasWorkflowAttemptExpired(true, workflowDeadline, DateTime.UtcNow),
        "A submitted workflow must still honor its execution deadline.");
}

static void TelegramArrivalOrderIsNumeric()
{
    string[] tasks = ["telegram-100.task.json", "telegram-99.task.json", "telegram-101.task.json"];
    Assert(tasks.OrderBy(TelegramQueuePolicy.ArrivalOrder).SequenceEqual(
        ["telegram-99.task.json", "telegram-100.task.json", "telegram-101.task.json"]),
        "Simultaneous Telegram tasks must retain numeric update order, not lexical filename order.");
    Assert(TelegramQueuePolicy.ArrivalOrder("local-123.task.json") == long.MaxValue
        && TelegramQueuePolicy.ArrivalOrder("telegram-5.done.json") == long.MaxValue,
        "Non-Telegram and terminal records cannot supply Telegram arrival order.");
}

static void TelegramQueueSurvivesPauseAndRestart()
{
    string root = Path.Combine(Path.GetTempPath(), "AgentWatcher.Tests", Guid.NewGuid().ToString("N"));
    string queue = Path.Combine(root, "queue");
    string outbox = Path.Combine(root, "outbox");
    string delivered = Path.Combine(outbox, "delivered");
    Directory.CreateDirectory(queue);
    Directory.CreateDirectory(delivered);
    try
    {
        Dictionary<string, DateTime> createdTimes = new(StringComparer.Ordinal)
        {
            ["telegram-101.task.json"] = DateTime.Parse("2026-10-04T12:00:00Z"),
            ["telegram-100.task.json"] = DateTime.Parse("2026-10-04T12:01:00Z"),
            ["local.task.json"] = DateTime.Parse("2026-10-04T12:01:30Z"),
            ["telegram-99.task.json"] = DateTime.Parse("2026-10-04T12:02:00Z")
        };
        foreach ((string name, DateTime created) in createdTimes)
            File.WriteAllText(Path.Combine(queue, name), JsonSerializer.Serialize(new { Created = created }));

        DateTime ReadCreated(string path)
        {
            using JsonDocument task = JsonDocument.Parse(File.ReadAllText(path));
            return task.RootElement.GetProperty("Created").GetDateTime();
        }

        string[] expected =
        [
            "telegram-99.task.json",
            "telegram-100.task.json",
            "local.task.json",
            "telegram-101.task.json"
        ];
        string[] pausedOrder = TelegramQueuePolicy.OrderTasks(
            Directory.EnumerateFiles(queue, "*.task.json"), ReadCreated)
            .Select(Path.GetFileName).ToArray()!;
        Assert(pausedOrder.SequenceEqual(expected), "A paused queue must preserve numeric Telegram arrival order.");
        Assert(File.Exists(Path.Combine(queue, expected[0])),
            "Pausing must retain the first durable task instead of acknowledging it as executed.");

        string[] restartedOrder = TelegramQueuePolicy.OrderTasks(
            Directory.EnumerateFiles(queue, "*.task.json"), ReadCreated)
            .Select(Path.GetFileName).ToArray()!;
        Assert(restartedOrder.SequenceEqual(expected), "Restarting must recover the same FIFO order from persisted task files.");

        string completed = Path.Combine(queue, expected[0]);
        File.Move(completed, completed.Replace(".task.json", ".done.json", StringComparison.Ordinal));
        string nextAfterRestart = TelegramQueuePolicy.OrderTasks(
            Directory.EnumerateFiles(queue, "*.task.json"), ReadCreated).First();
        Assert(Path.GetFileName(nextAfterRestart) == expected[1],
            "Completing one task after restart must expose exactly the next Telegram update.");

        Assert(TelegramQueuePolicy.ShouldSuppressNonErrorNotice("copilot-start")
                && TelegramQueuePolicy.ShouldSuppressNonErrorNotice("queued")
                && TelegramQueuePolicy.ShouldSuppressNonErrorNotice("success")
                && TelegramQueuePolicy.ShouldSuppressNonErrorNotice("cancelled")
                && TelegramQueuePolicy.ShouldSuppressNonErrorNotice("copilot-session")
                && TelegramQueuePolicy.ShouldSuppressNonErrorNotice(null)
                && !TelegramQueuePolicy.ShouldSuppressNonErrorNotice(TelegramQueuePolicy.ErrorNoticeKind)
                && !TelegramQueuePolicy.ShouldSuppressNonErrorNotice(TelegramWorkflowNotice.OutboxKind)
                && !TelegramQueuePolicy.ShouldSuppressNonErrorNotice(TelegramCopilotInbox.DirectResponseKind),
            "Only routine notices are suppressed; errors, direct Copilot replies and explicitly opt-in workflow progress notices reach the outbox delivery gate.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static async Task TelegramPollingProcessesUpdatesSequentiallyAsync()
{
    string root = Path.Combine(Path.GetTempPath(), "AgentWatcher.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    using CancellationTokenSource cancellation = new();
    BlockingUpdatesTelegramHandler handler = new();
    using TelegramBotService bot = new("offline-test-token", 4242, handler, Path.Combine(root, "offset.txt"));
    int handledCount = 0;
    TaskCompletionSource secondUpdateStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Task polling = bot.RunAsync(
        (_, _, _, token) =>
        {
            if (Interlocked.Increment(ref handledCount) == 1)
            {
                handler.FirstUpdateStarted.TrySetResult();
                return handler.ReleaseFirstUpdate.Task.WaitAsync(token);
            }

            secondUpdateStarted.TrySetResult();
            return Task.FromResult(string.Empty);
        },
        (_, _, _, _) => Task.FromResult(string.Empty),
        _ => { },
        cancellation.Token);

    try
    {
        await handler.FirstUpdateStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Task completed = await Task.WhenAny(secondUpdateStarted.Task, Task.Delay(TimeSpan.FromMilliseconds(150)));
        Assert(!ReferenceEquals(completed, secondUpdateStarted.Task) && handledCount == 1,
            "Telegram polling must not start the next update while the current command handler is still running.");

        handler.ReleaseFirstUpdate.TrySetResult(string.Empty);
        await secondUpdateStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert(handledCount == 2, "The next Telegram update must start after the active command finishes.");
    }
    finally
    {
        handler.ReleaseFirstUpdate.TrySetResult(string.Empty);
        cancellation.Cancel();
        await polling.WaitAsync(TimeSpan.FromSeconds(3));
        Directory.Delete(root, recursive: true);
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed class BlockingUpdatesTelegramHandler : HttpMessageHandler
{
    private int _updatesServed;

    public TaskCompletionSource FirstUpdateStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<string> ReleaseFirstUpdate { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        string method = request.RequestUri?.AbsolutePath.Split('/').LastOrDefault() ?? string.Empty;
        if (method == "getUpdates" && Interlocked.Increment(ref _updatesServed) == 1)
        {
            const string updates =
                "{\"ok\":true,\"result\":["
                + "{\"update_id\":100,\"message\":{\"message_id\":1,\"chat\":{\"id\":4242},\"text\":\"first\"}},"
                + "{\"update_id\":101,\"message\":{\"message_id\":2,\"chat\":{\"id\":4242},\"text\":\"second\"}}]}";
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(updates)
            });
        }

        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("{\"ok\":true,\"result\":[]}")
        });
    }
}

sealed class SequencedTelegramHandler : HttpMessageHandler
{
    public List<string> SentChunks { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using JsonDocument body = JsonDocument.Parse(
            await request.Content!.ReadAsStringAsync(cancellationToken));
        SentChunks.Add(body.RootElement.GetProperty("text").GetString() ?? string.Empty);
        if (SentChunks.Count == 2)
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("simulated failure")
            };
        }

        string response =
            "{\"ok\":true,\"result\":{\"message_id\":" + SentChunks.Count + ",\"chat\":{\"id\":4242}}}";
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(response)
        };
    }
}
