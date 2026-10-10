using System.Text.Json;
using ArsanGazERP.AgentWatcher;

namespace ArsanGazERP.AgentWatcher.Tests;

internal static class TelegramWorkflowNoticeTests
{
    internal static async Task RunAsync()
    {
        NoticeIdentityIsStableAndStageSpecific();
        RecommendationActionabilityRequiresAnExactFinalMarker();
        RecommendationForwardingSettingAndDailyLimitPersist();
        await AutomaticRecommendationDispatchRemainsNarrowAndUnapprovedAsync();
        RecommendationFailuresDoNotPauseUnrelatedQueueWork();
        BridgeVersionAndProtocolMismatchFailFast();
        NoticePayloadContainsNoTaskContent();
        WorkflowNoticesRequireExplicitOptInAndExistingTelegramTask();
        ApprovalNoticesRemainRequiredAndChatScoped();
        WorkflowNoticeRateLimitIsBounded();
        await NoticePersistsOnceForTheExistingChatAsync();
        await ApprovalNoticePersistsOnceForTheExistingChatAsync();
        await WorkflowOrchestrationRunsCorrelatedReviewAndReworkAsync();
        await WorkflowOrchestrationFailsClosedOnReplayAndLimitsAsync();
        await WorkflowEvidenceVerifierUsesMeasuredLocalResultsAsync();
        RejectsInvalidRecipientAndTaskIds();
    }

    private static void BridgeVersionAndProtocolMismatchFailFast()
    {
        BridgeCompatibilityResult installedOld = BridgeCompatibility.Check("0.2.16", protocolVersion: 2);
        Assert(!installedOld.IsCompatible
            && installedOld.Detail.Contains("installed 0.2.16", StringComparison.Ordinal)
            && installedOld.Detail.Contains("minimum supported 0.2.17", StringComparison.Ordinal),
            "The observed installed bridge version must be rejected explicitly instead of waiting for a completion timeout.");
        Assert(!BridgeCompatibility.Check("0.2.14", protocolVersion: 1).IsCompatible,
            "Matching extension version with a stale protocol must fail readiness.");
        Assert(BridgeCompatibility.Check("0.2.17", protocolVersion: 2).IsCompatible,
            "The minimum supported extension version and bridge protocol are dispatchable.");
        BridgeCompatibilityResult deliveryBlocked = BridgeCompatibility.Check(
            "0.2.17",
            protocolVersion: 2,
            dispatchBlocked: true);
        Assert(!deliveryBlocked.IsCompatible
            && deliveryBlocked.Detail.Contains("belirsiz önceki teslim", StringComparison.Ordinal),
            "An unresolved bridge delivery must block dispatch without being mistaken for an extension-version mismatch.");
        Assert(BridgeCompatibility.Check("0.2.18", protocolVersion: 2).IsCompatible,
            "A newer extension version with the required bridge protocol must remain dispatchable.");
        Assert(!BridgeCompatibility.Check("not-a-version", protocolVersion: 2).IsCompatible,
            "An invalid extension version must fail readiness.");
        Assert(!BridgeCompatibility.Check(null, protocolVersion: 0).IsCompatible,
            "A legacy heartbeat without version/protocol markers must fail readiness.");
        WorkflowJob blocked = WorkflowOrchestrator.FromTelegramRequest(
            "telegram-bridge-version", "Run a request after bridge compatibility is confirmed.");
        WorkflowOrchestrator.BeginAttempt(
            blocked, "request-bridge-version", DateTime.UtcNow);
        WorkflowOrchestrator.BlockBeforeDispatch(
            blocked, installedOld.Detail);
        Assert(blocked.State == WorkflowState.Blocked
            && !WorkflowOrchestrator.IsDispatchable(blocked)
            && blocked.Blocker!.Contains("installed 0.2.16", StringComparison.Ordinal),
            "A known bridge mismatch must produce an explicit pre-dispatch blocker, not an ambiguous timeout or retry.");
    }

    private static void RecommendationActionabilityRequiresAnExactFinalMarker()
    {
        Assert(AgentRecommendationPolicy.GetActionabilityDecision(
                "Kanıta dayalı yerel öneri.\nACTIONABLE: yes") == "ACTIONABLE: yes",
            "Only a final exact yes marker authorizes forwarding.");
        Assert(AgentRecommendationPolicy.GetActionabilityDecision(
                "Kanıta dayalı yerel öneri.\nACTIONABLE: no") == "ACTIONABLE: no",
            "An explicit no marker remains report-only.");
        foreach (string invalid in new[]
        {
            "ACTIONABLE: yes\nDaha sonra açıklama eklendi.",
            "ACTIONABLE: maybe",
            "Karar belirtilmedi."
        })
        {
            Assert(AgentRecommendationPolicy.GetActionabilityDecision(invalid) == "ACTIONABLE: missing/invalid",
                "Missing, malformed, or non-final decisions must fail closed.");
        }
    }

    private static void RecommendationForwardingSettingAndDailyLimitPersist()
    {
        string directory = Path.Combine(Path.GetTempPath(), "AgentWatcher.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "settings.json");
        string queue = Path.Combine(directory, "Queue");
        Directory.CreateDirectory(queue);
        try
        {
            AppSettings settings = AppSettings.Load(path);
            Assert(settings.AutoForwardAgentRecommendations
                && !AgentRecommendationPolicy.RequiresApproval(settings),
                "Automatic forwarding must be the default for bounded Gozcu/Ar-Ge recommendations.");
            DateTime localDay = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Local);
            for (int index = 0; index < AgentRecommendationPolicy.DailyAutoForwardLimit; index++)
            {
                Assert(AgentRecommendationPolicy.TryReserveDailyAutoForward(settings, queue, localDay),
                    "Each of the first six daily recommendations may reserve one durable slot.");
                settings = AppSettings.Load(path);
            }
            Assert(!AgentRecommendationPolicy.TryReserveDailyAutoForward(settings, queue, localDay),
                "The daily limit must remain enforced after reloading persisted settings.");
            settings.AutoForwardAgentRecommendations = false;
            settings.Save();
            AppSettings manual = AppSettings.Load(path);
            Assert(AgentRecommendationPolicy.RequiresApproval(manual),
                "Disabling automatic forwarding must restore the existing per-recommendation approval path.");
            manual.AutoForwardAgentRecommendations = true;
            manual.Save();
            AppSettings restored = AppSettings.Load(path);
            Assert(AgentRecommendationPolicy.TryReserveDailyAutoForward(
                    restored, queue, localDay.AddDays(1)),
                "A new local calendar day receives a separate six-item limit.");

            restored.AutoForwardAgentRecommendations = true;
            restored.AutoForwardAgentRecommendationsDate = localDay.AddDays(2).ToString("yyyyMMdd");
            restored.AutoForwardAgentRecommendationsCount = 0;
            restored.Save();
            TaskItem retained = new()
            {
                Id = "recommendation-retained",
                Created = localDay.AddDays(2),
                Workflow = WorkflowOrchestrator.FromRecommendation(
                    "recommendation-retained",
                    WorkflowSource.ArGe,
                    "research-retained",
                    "ACTIONABLE: yes\nRetained automatic recommendation.",
                    ["https://learn.microsoft.com/en-us/dotnet/"],
                    "Inspect a narrow local code change.",
                    "Ar-Ge",
                    requiresApproval: false)
            };
            File.WriteAllText(
                Path.Combine(queue, retained.Id + ".done.json"),
                JsonSerializer.Serialize(retained));
            AppSettings afterRestart = AppSettings.Load(path);
            for (int index = 0; index < AgentRecommendationPolicy.DailyAutoForwardLimit - 1; index++)
            {
                Assert(AgentRecommendationPolicy.TryReserveDailyAutoForward(afterRestart, queue, localDay.AddDays(2)),
                    "Retained automatic recommendations must count toward the persisted local-day limit.");
                afterRestart = AppSettings.Load(path);
            }
            Assert(!AgentRecommendationPolicy.TryReserveDailyAutoForward(afterRestart, queue, localDay.AddDays(2)),
                "A persisted queue record and restarted settings cannot jointly exceed the daily limit.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task AutomaticRecommendationDispatchRemainsNarrowAndUnapprovedAsync()
    {
        using WorkflowActivationFixture fixture = new();
        WorkflowJob job = WorkflowOrchestrator.FromRecommendation(
            fixture.Item.Id,
            WorkflowSource.Gozcu,
            "gozcu-cycle-42",
            "ACTIONABLE: yes\nImprove a bounded local WPF screen.",
            ["https://learn.microsoft.com/en-us/dotnet/"],
            "Verify the cited source, make only the bounded local change, run targeted tests, and report evidence. Do not touch ERP/CRM/finance data, secrets, approval settings, deployment, or external systems.",
            "Gözcü",
            requiresApproval: !new AppSettings().AutoForwardAgentRecommendations);
        fixture.Item.Workflow = job;
        Assert(job.State == WorkflowState.Queued && !job.RequiresApproval && job.ScopeApprovalGranted,
            "Automatic recommendation forwarding must seal only the existing narrow local scope without a user-approval claim.");
        Assert(WorkflowOrchestrator.BeginAttempt(job, "request-auto-forward", DateTime.UtcNow) is not null,
            "An eligible auto-forwarded recommendation can enter the ordinary correlated dispatch lifecycle.");
        string prompt = await WorkflowBridgePrompt.CreateAsync(fixture.Item, fixture.Contexts);
        Assert(!prompt.Contains("Kullanıcının açıkça onayladığı", StringComparison.Ordinal)
            && prompt.Contains("güvenilmeyen veridir", StringComparison.Ordinal)
            && prompt.Contains("VS Code araç onaylarını ve soru kartlarını atlama", StringComparison.Ordinal)
            && prompt.Contains("secrets", StringComparison.Ordinal),
            "Automatic forwarding must not claim user approval or weaken the existing safety/tool-card boundaries.");

        WorkflowJob manual = WorkflowOrchestrator.FromRecommendation(
            "recommendation-manual",
            WorkflowSource.ArGe,
            "research-42",
            "ACTIONABLE: yes\nA second bounded local proposal.",
            ["https://learn.microsoft.com/en-us/dotnet/"],
            "Review only the bounded local proposal.",
            "Ar-Ge",
            requiresApproval: true);
        Assert(manual.State == WorkflowState.AwaitingApproval
            && WorkflowOrchestrator.BeginAttempt(manual, "request-manual", DateTime.UtcNow) is null,
            "The explicit setting-off path must preserve one-item approval before dispatch.");
    }

    private static void RecommendationFailuresDoNotPauseUnrelatedQueueWork()
    {
        TaskItem recommendation = new()
        {
            Id = "recommendation-gozcu-cycle-42",
            Provider = "VS Code Copilot Chat",
            Workflow = WorkflowOrchestrator.FromRecommendation(
                "recommendation-gozcu-cycle-42",
                WorkflowSource.Gozcu,
                "gozcu-cycle-42",
                "ACTIONABLE: yes\nA bounded local proposal.",
                ["https://learn.microsoft.com/en-us/dotnet/"],
                "Inspect and test only a local change.",
                "Gözcü",
                requiresApproval: false)
        };
        Assert(!AgentRecommendationPolicy.ShouldPauseQueueForFailure(recommendation),
            "A failed recommendation must not pause unrelated queue work.");
        TaskItem argeRecommendation = new()
        {
            Id = "recommendation-arge-42",
            Workflow = WorkflowOrchestrator.FromRecommendation(
                "recommendation-arge-42",
                WorkflowSource.ArGe,
                "erp-research-42",
                "ACTIONABLE: yes\nA bounded local proposal.",
                ["https://learn.microsoft.com/en-us/dotnet/"],
                "Inspect and test only a local change.",
                "Ar-Ge",
                requiresApproval: false)
        };
        Assert(!AgentRecommendationPolicy.ShouldPauseQueueForFailure(argeRecommendation),
            "An interrupted Ar-Ge recommendation must not pause unrelated queue work.");
        TaskItem autonomy = new()
        {
            Id = "gozcu-cycle-42",
            Type = GozcuAutonomy.TaskType
        };
        Assert(!AgentRecommendationPolicy.ShouldPauseQueueForFailure(autonomy),
            "An interrupted Gözcü analysis must not pause unrelated queue work.");
        TaskItem legacyResearch = new()
        {
            Id = "erp-research-42"
        };
        Assert(!AgentRecommendationPolicy.ShouldPauseQueueForFailure(legacyResearch),
            "An interrupted Ar-Ge research task must not pause unrelated queue work.");
        TaskItem telegram = new()
        {
            Id = "telegram-42",
            Workflow = WorkflowOrchestrator.FromTelegramRequest("telegram-42", "Run a local task.")
        };
        Assert(AgentRecommendationPolicy.ShouldPauseQueueForFailure(telegram),
            "The queue must retain existing fail-stop behavior for ordinary Telegram work.");
    }

    private static void NoticeIdentityIsStableAndStageSpecific()
    {
        TelegramWorkflowNotice first = TelegramWorkflowNotice.Create(
            "telegram-13", 4242, TelegramWorkflowStage.Queued);
        TelegramWorkflowNotice repeated = TelegramWorkflowNotice.Create(
            "telegram-13", 4242, TelegramWorkflowStage.Queued);
        TelegramWorkflowNotice nextStage = TelegramWorkflowNotice.Create(
            "telegram-13", 4242, TelegramWorkflowStage.Running);

        Assert(first.Id == repeated.Id, "The same task/stage must produce a stable outbox ID.");
        Assert(first.Id != nextStage.Id, "Different workflow stages must have distinct outbox IDs.");
    }

    private static void NoticePayloadContainsNoTaskContent()
    {
        TelegramWorkflowNotice notice = TelegramWorkflowNotice.Create(
            "telegram-13", 4242, TelegramWorkflowStage.CopilotSubmitted);

        Assert(notice.Report.Contains("Copilot köprüsüne iletildi", StringComparison.Ordinal),
            "Progress reports must use a fixed, reviewable stage message.");
        Assert(notice.TaskId != "telegram-13", "The outbox must not persist the raw Telegram update ID.");
        Assert(notice.Report.Contains("telegram-13", StringComparison.Ordinal) == false,
            "Progress reports must not expose the raw Telegram update ID.");
    }

    private static void WorkflowNoticesRequireExplicitOptInAndExistingTelegramTask()
    {
        AppSettings defaults = new();
        Assert(!defaults.EnableWorkflowProgressNotifications,
            "Progress notifications must be disabled for default/new settings.");
        Assert(!TelegramQueuePolicy.ShouldQueueWorkflowNotice(
                defaults.EnableWorkflowProgressNotifications, "telegram-14", 4242),
            "Default configuration must not enqueue progress notices.");
        Assert(!TelegramQueuePolicy.ShouldQueueWorkflowNotice(true, "scheduled-20261004", 4242),
            "Automatic and local jobs must not receive user-Telegram stage notifications.");
        Assert(!TelegramQueuePolicy.ShouldQueueWorkflowNotice(true, "telegram-14", null),
            "A task without its already-authorized chat must not receive a stage notification.");
        Assert(TelegramQueuePolicy.ShouldQueueWorkflowNotice(true, "telegram-14", 4242),
            "Explicit opt-in may enqueue notifications only for the task's existing Telegram chat.");
        Assert(TelegramQueuePolicy.ShouldSuppressWorkflowNotice(false),
            "Queued stage notifications must not be delivered after opt-out.");
        Assert(!TelegramQueuePolicy.ShouldSuppressWorkflowNotice(true),
            "Explicit opt-in permits the existing outbox delivery path to proceed.");
    }

    private static void WorkflowNoticeRateLimitIsBounded()
    {
        DateTime sentUtc = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
        DateTime allowedUtc = TelegramWorkflowNotice.NextSendAllowedUtc(sentUtc);

        Assert(
            TelegramWorkflowNotice.RateLimitDelay(sentUtc.AddSeconds(1), allowedUtc) == TimeSpan.FromSeconds(2),
            "A workflow notice must wait the remaining three-second interval.");
        Assert(
            TelegramWorkflowNotice.RateLimitDelay(allowedUtc, allowedUtc) == TimeSpan.Zero,
            "No rate-limit delay should remain after the interval expires.");
        Assert(
            TelegramWorkflowNotice.NextSendAllowedUtc(sentUtc) == sentUtc.AddSeconds(3),
            "Workflow notices must be capped at one message per three seconds.");
        Assert(!TelegramWorkflowNotice.IsRetryLimitReached(6)
            && TelegramWorkflowNotice.IsRetryLimitReached(7),
            "Workflow notice delivery must fail-stop after seven attempts.");
        string redacted = TelegramWorkflowNotice.RedactDiagnostic(
            "Authorization: Bearer hidden-token api_key=hidden-key");
        Assert(!redacted.Contains("hidden-token", StringComparison.Ordinal)
            && !redacted.Contains("hidden-key", StringComparison.Ordinal)
            && redacted.Contains("[REDACTED]", StringComparison.Ordinal),
            "Workflow notification delivery diagnostics must redact secret-like values.");
    }

    private static void ApprovalNoticesRemainRequiredAndChatScoped()
    {
        TelegramWorkflowNotice notice = TelegramWorkflowNotice.CreateApproval(
            "workflow-approval-31", 4242, "sealed-contract-hash");

        Assert(notice.Kind == TelegramWorkflowNotice.ApprovalOutboxKind,
            "Approval requests must use the dedicated approval outbox kind.");
        Assert(notice.ChatId == 4242,
            "Approval requests must target only the Telegram chat already attached to the workflow.");
        Assert(notice.Report.Contains("/workflow-onay workflow-approval-31", StringComparison.Ordinal),
            "The approval notice must identify the explicit, per-workflow approval command.");
        Assert(!TelegramQueuePolicy.ShouldQueueWorkflowApprovalNotice(null)
            && !TelegramQueuePolicy.ShouldQueueWorkflowApprovalNotice(0)
            && TelegramQueuePolicy.ShouldQueueWorkflowApprovalNotice(4242),
            "Approval notices must be queued only for a valid existing authorized chat.");
        Assert(!TelegramQueuePolicy.ShouldSuppressNonErrorNotice(TelegramWorkflowNotice.ApprovalOutboxKind),
            "General routine-notice suppression must not suppress human approval requests.");
    }

    private static async Task NoticePersistsOnceForTheExistingChatAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "AgentWatcher.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            TelegramWorkflowNotice notice = TelegramWorkflowNotice.Create(
                "telegram-13", 4242, TelegramWorkflowStage.Completed);
            bool firstWrite = await TelegramWorkflowNotice.WriteAsync(directory, notice);
            bool duplicateWrite = await TelegramWorkflowNotice.WriteAsync(directory, notice);
            string path = Path.Combine(directory, notice.Id + ".json");
            using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(path));

            Assert(firstWrite, "The first notice must be durably enqueued.");
            Assert(!duplicateWrite, "Repeating the same stage must not create a second outbox record.");
            Assert(document.RootElement.GetProperty("Kind").GetString() == TelegramWorkflowNotice.OutboxKind,
                "The notice must use the outbox's recognized workflow kind.");
            Assert(document.RootElement.GetProperty("ChatId").GetInt64() == 4242,
                "The outbox must retain the task's existing authorized chat target.");
            Assert(document.RootElement.GetProperty("Receipts").GetArrayLength() == 0,
                "A queued notice must not claim that Telegram accepted it.");
            Assert(!Directory.EnumerateFiles(directory, "*.tmp").Any(),
                "Atomic persistence must remove temporary files.");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static async Task ApprovalNoticePersistsOnceForTheExistingChatAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "AgentWatcher.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            TelegramWorkflowNotice notice = TelegramWorkflowNotice.CreateApproval(
                "workflow-approval-31", 4242, "sealed-contract-hash");
            bool firstWrite = await TelegramWorkflowNotice.WriteAsync(directory, notice);
            bool duplicateWrite = await TelegramWorkflowNotice.WriteAsync(directory, notice);
            string path = Path.Combine(directory, notice.Id + ".json");
            using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(path));

            Assert(firstWrite && !duplicateWrite,
                "Approval notice enqueueing must be durable and idempotent for the same workflow contract.");
            Assert(document.RootElement.GetProperty("Kind").GetString()
                    == TelegramWorkflowNotice.ApprovalOutboxKind
                && document.RootElement.GetProperty("ChatId").GetInt64() == 4242,
                "The approval outbox record must preserve its dedicated kind and authorized chat.");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static async Task WorkflowOrchestrationRunsCorrelatedReviewAndReworkAsync()
    {
        DateTime now = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
        string queue = Path.Combine(Path.GetTempPath(), "AgentWatcher.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(queue);
        string reviewPath = Path.Combine(queue, "workflow-77.review.json");
        string taskPath = Path.Combine(queue, "workflow-77.task.json");
        WorkflowJob job = WorkflowOrchestrator.FromRecommendation(
            taskId: "workflow-77",
            source: WorkflowSource.ArGe,
            sourceId: "research-9",
            recommendation: "Narrow recommendation",
            citations: ["https://docs.example.org/ui?access_token=not-for-storage#section"],
            scope: "Review only this UI pattern; no ERP data changes.",
            reviewerRole: "Gözcü",
            maxAttempts: 2);

        await WorkflowQueuePersistence.WriteAsync(reviewPath, job);
        job = JsonSerializer.Deserialize<WorkflowJob>(await File.ReadAllTextAsync(reviewPath))
            ?? throw new InvalidDataException("The persisted workflow candidate could not be loaded.");
        Assert(job.State == WorkflowState.AwaitingApproval,
            "Research recommendations must not authorize code changes.");
        Assert(job.Source == WorkflowSource.ArGe
            && job.SourceId == "research-9"
            && job.ReviewerRole == "Gözcü"
            && job.Criteria.Count >= 1
            && job.Scope.Contains("no ERP data changes", StringComparison.Ordinal),
            "The originating role, source evidence, reviewer, scope and acceptance criteria must remain correlated.");
        Assert(!job.SourceEvidence.Contains("access_token", StringComparison.OrdinalIgnoreCase)
            && !job.SourceEvidence.Contains("#section", StringComparison.Ordinal),
            "Research citations must not retain query tokens or fragments.");
        WorkflowJob widened = JsonSerializer.Deserialize<WorkflowJob>(JsonSerializer.Serialize(job))!;
        widened.Scope += " Also modify financial records.";
        AssertThrows(() => WorkflowOrchestrator.Approve(widened, authorized: true));
        Assert(widened.ContractHash != widened.ApprovedContractHash,
            "Expanded scope must not inherit the original candidate approval.");
        Assert(!WorkflowOrchestrator.Approve(job, authorized: false),
            "An unauthenticated approval cannot release a workflow.");
        Assert(WorkflowOrchestrator.BeginAttempt(job, "request-1", now) is null,
            "A waiting workflow cannot dispatch before approval.");
        Assert(WorkflowOrchestrator.Approve(job, authorized: true),
            "An authorized human approval should release the candidate.");
        Assert(job.ScopeApprovalGranted && job.ApprovedContractHash == job.ContractHash,
            "The human approval must be bound to the exact immutable contract before any dispatch.");

        WorkflowJob changedScopeAfterApproval = JsonSerializer.Deserialize<WorkflowJob>(
            JsonSerializer.Serialize(job))!;
        changedScopeAfterApproval.Scope += " Also change customer records.";
        AssertThrows(() => WorkflowOrchestrator.Validate(changedScopeAfterApproval));
        WorkflowJob changedCommandAfterApproval = JsonSerializer.Deserialize<WorkflowJob>(
            JsonSerializer.Serialize(job))!;
        changedCommandAfterApproval.SourceEvidence += " Also run an unapproved command.";
        AssertThrows(() => WorkflowOrchestrator.Validate(changedCommandAfterApproval));
        WorkflowJob changedCriteriaAfterApproval = JsonSerializer.Deserialize<WorkflowJob>(
            JsonSerializer.Serialize(job))!;
        changedCriteriaAfterApproval.Criteria[0] =
            changedCriteriaAfterApproval.Criteria[0] with { Text = "Expanded acceptance scope." };
        AssertThrows(() => WorkflowOrchestrator.Validate(changedCriteriaAfterApproval));
        WorkflowJob changedPermissionsAfterApproval = JsonSerializer.Deserialize<WorkflowJob>(
            JsonSerializer.Serialize(job))!;
        changedPermissionsAfterApproval.RequiresApproval = false;
        AssertThrows(() => WorkflowOrchestrator.Validate(changedPermissionsAfterApproval));
        WorkflowJob changedAttemptCapAfterApproval = JsonSerializer.Deserialize<WorkflowJob>(
            JsonSerializer.Serialize(job))!;
        changedAttemptCapAfterApproval.MaxAttempts = 1;
        AssertThrows(() => WorkflowOrchestrator.Validate(changedAttemptCapAfterApproval));
        WorkflowJob changedReviewerAfterApproval = JsonSerializer.Deserialize<WorkflowJob>(
            JsonSerializer.Serialize(job))!;
        changedReviewerAfterApproval.ReviewerRole = "Jarvis";
        AssertThrows(() => WorkflowOrchestrator.Validate(changedReviewerAfterApproval));

        await WorkflowQueuePersistence.WriteAsync(reviewPath, job);
        WorkflowJob approvedCheckpoint = JsonSerializer.Deserialize<WorkflowJob>(
            await File.ReadAllTextAsync(reviewPath))
            ?? throw new InvalidDataException("The approved workflow checkpoint could not be loaded.");
        Assert(approvedCheckpoint.State == WorkflowState.Queued
            && !WorkflowOrchestrator.Approve(approvedCheckpoint, authorized: true),
            "A crash after persisting approval must not replay the stale approval transition.");
        await WorkflowQueuePersistence.WriteAsync(taskPath, job, overwrite: false);
        File.Delete(reviewPath);

        WorkflowDispatch first = WorkflowOrchestrator.BeginAttempt(job, "request-1", now)
            ?? throw new InvalidOperationException("The approved first attempt was not dispatched.");
        await WorkflowQueuePersistence.WriteAsync(taskPath, job);
        Assert(first is { Attempt: 1, TaskId: "workflow-77" },
            "The first attempt must preserve the stable job ID.");
        AssertThrows(() => WorkflowOrchestrator.RecordBridgeCompletion(
            job,
            new WorkflowBridgeCompletion("request-1", "wrong-task", "completed", "report"),
            now));
        AssertThrows(() => WorkflowOrchestrator.RecordBridgeCompletion(
            job,
            new WorkflowBridgeCompletion("wrong-request", "workflow-77", "completed", "report"),
            now));

        Assert(WorkflowOrchestrator.RecordBridgeCompletion(
            job,
            new WorkflowBridgeCompletion(first.RequestId, first.TaskId, "completed", "first result"),
            now.AddMinutes(1)),
            "A new correlated result should be stored.");
        await WorkflowQueuePersistence.WriteAsync(taskPath, job);
        WorkflowQueuePersistence.Move(taskPath, reviewPath, overwrite: true);
        Assert(job.State == WorkflowState.AwaitingReview,
            "A terminal Copilot event must create a review state, not acceptance.");

        WorkflowCriterion[] criteria = job.Criteria.ToArray();
        WorkflowCriterionProof[] rejected = criteria.Select(criterion => new WorkflowCriterionProof(
            criterion.Id, WorkflowCriterionStatus.NotMet, "CopilotClaim", "not-independent", "Not independently verified.")).ToArray();
        Assert(WorkflowOrchestrator.ApplyReview(job, "Gözcü", rejected, authorizedHuman: false,
            static (_, _, _) => false) == WorkflowState.Queued,
            "A failed review may rework only inside the immutable scope already approved for bounded attempts.");
        Assert(job.ApprovedContractHash == job.ContractHash && job.Attempt == 1 && job.MaxAttempts == 2,
            "The original human approval may cover only the unchanged contract and remaining bounded attempt.");
        Assert(job.UnmetCriterionIds.SequenceEqual(criteria.Select(criterion => criterion.Id)),
            "Every unmet criterion must remain attached to the same workflow.");
        await WorkflowQueuePersistence.WriteAsync(reviewPath, job);

        TelegramWorkflowNotice reworkNotice = TelegramWorkflowNotice.Create(
            job.TaskId, 4242, TelegramWorkflowStage.Rework, job.Attempt);
        TelegramWorkflowNotice reviewNotice = TelegramWorkflowNotice.Create(
            job.TaskId, 4242, TelegramWorkflowStage.Review, job.Attempt);
        string outbox = Path.Combine(Path.GetTempPath(), "AgentWatcher.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            Assert(await TelegramWorkflowNotice.WriteAsync(outbox, reviewNotice),
                "The review stage must enter the durable outbox.");
            Assert(await TelegramWorkflowNotice.WriteAsync(outbox, reworkNotice),
                "The rework stage must enter the durable outbox.");
            Assert(!await TelegramWorkflowNotice.WriteAsync(outbox, reworkNotice),
                "Repeated rework-stage emission must be idempotent.");

                Assert(!WorkflowOrchestrator.Approve(job, authorized: true),
                    "The agent must not manufacture a new approval for the already authorized fixed scope.");
                await WorkflowQueuePersistence.WriteAsync(taskPath, job, overwrite: false);
            File.Delete(reviewPath);
            WorkflowDispatch second = WorkflowOrchestrator.BeginAttempt(job, "request-2", now.AddMinutes(2))
                ?? throw new InvalidOperationException("The approved rework attempt was not dispatched.");
            await WorkflowQueuePersistence.WriteAsync(taskPath, job);
            Assert(second is { Attempt: 2, TaskId: "workflow-77" },
                "Rework must retain the job ID and allocate a new request ID.");

            WorkflowBridgeCompletion secondResult = new(
                second.RequestId, second.TaskId, "completed", "second result");
            Assert(WorkflowOrchestrator.RecordBridgeCompletion(
                job, secondResult, now.AddMinutes(3)),
                "The second correlated result should be stored.");
            await WorkflowQueuePersistence.WriteAsync(taskPath, job);
            WorkflowQueuePersistence.Move(taskPath, reviewPath, overwrite: true);
            Assert(!WorkflowOrchestrator.RecordBridgeCompletion(
                job, secondResult, now.AddMinutes(4)),
                "A duplicate identical completion must be idempotent.");
            AssertThrows(() => WorkflowOrchestrator.RecordBridgeCompletion(
                job,
                secondResult with { Report = "conflicting replay" },
                now.AddMinutes(4)));

            WorkflowCriterionProof[] accepted = criteria.Select(criterion => new WorkflowCriterionProof(
                criterion.Id,
                WorkflowCriterionStatus.Met,
                "TestRun",
                "passed:" + criterion.Id,
                "Matching independently recorded local assertion passed.")).ToArray();
            Assert(WorkflowOrchestrator.ApplyReview(
                job,
                "Gözcü",
                accepted,
                authorizedHuman: false,
                (current, criterion, proof) =>
                    current.TaskId == "workflow-77"
                    && current.CurrentRequestId == "request-2"
                    && proof.EvidenceReference == "passed:" + criterion.Id)
                == WorkflowState.Completed,
                "Only per-criterion independently verified evidence can complete the workflow.");
            string donePath = Path.Combine(queue, "workflow-77.done.json");
            await WorkflowQueuePersistence.WriteAsync(donePath, job, overwrite: true);
            File.Delete(reviewPath);
            WorkflowJob completed = JsonSerializer.Deserialize<WorkflowJob>(await File.ReadAllTextAsync(donePath))
                ?? throw new InvalidDataException("The terminal workflow checkpoint could not be loaded.");
            Assert(completed.State == WorkflowState.Completed,
                "The completed state must survive durable queue transition and reload.");

            TelegramWorkflowNotice completedNotice = TelegramWorkflowNotice.Create(
                job.TaskId, 4242, TelegramWorkflowStage.Completed, job.Attempt);
            Assert(await TelegramWorkflowNotice.WriteAsync(outbox, completedNotice),
                "The completed stage must be durably reported.");
            Assert(!await TelegramWorkflowNotice.WriteAsync(outbox, completedNotice),
                "A replayed completed stage must not create duplicate outbox notices.");
            Assert(reworkNotice.Id != completedNotice.Id,
                "Outbox correlation must distinguish rework from completion for the same job.");
        }
        finally
        {
            if (Directory.Exists(outbox))
            {
                Directory.Delete(outbox, recursive: true);
            }
            if (Directory.Exists(queue))
            {
                Directory.Delete(queue, recursive: true);
            }
        }

        string[] priority = TelegramQueuePolicy.OrderTasks(
            ["scheduled.task.json", "telegram-9.task.json", "telegram-10.task.json"],
            path => path.StartsWith("scheduled", StringComparison.Ordinal) ? DateTime.MinValue : DateTime.MaxValue,
            _ => "Normal")
            .ToArray();
        Assert(priority.SequenceEqual(["telegram-9.task.json", "telegram-10.task.json", "scheduled.task.json"]),
            "A newly queued user Telegram request must outrank scheduled work at the next safe boundary.");
    }

    private static async Task WorkflowOrchestrationFailsClosedOnReplayAndLimitsAsync()
    {
        DateTime now = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
        WorkflowJob healthFinding = WorkflowOrchestrator.FromSupervisorFinding(
            "telegram-90",
            "health-90",
            "Local database connectivity",
            isHealthy: false,
            "Synthetic failure detail.");
        Assert(healthFinding.Source == WorkflowSource.SupervisorHealth
            && healthFinding.State == WorkflowState.AwaitingApproval
            && healthFinding.RequiresApproval,
            "A genuine unhealthy local supervisor finding must create a provenance-bound approval candidate.");
        AssertThrows(() => WorkflowOrchestrator.FromSupervisorFinding(
            "telegram-90", "health-90", "healthy", isHealthy: true, "not a failure"));
        WorkflowJob approvedHealthContract = WorkflowOrchestrator.FromSupervisorFinding(
            "telegram-health-target", "health-target", "Local service health", isHealthy: false,
            "Synthetic failure detail.");
        Assert(WorkflowOrchestrator.Approve(approvedHealthContract, authorized: true),
            "A human may approve the exact bounded supervisor-remediation contract.");
        WorkflowJob changedTarget = JsonSerializer.Deserialize<WorkflowJob>(
            JsonSerializer.Serialize(approvedHealthContract))!;
        changedTarget.Criteria[0] = changedTarget.Criteria[0] with { ValidationTarget = "Different service" };
        AssertThrows(() => WorkflowOrchestrator.Validate(changedTarget));
        WorkflowJob unknownValidator = JsonSerializer.Deserialize<WorkflowJob>(
            JsonSerializer.Serialize(approvedHealthContract))!;
        unknownValidator.Criteria[0] = unknownValidator.Criteria[0] with { ValidatorId = "unknown-action" };
        AssertThrows(() => WorkflowOrchestrator.Validate(unknownValidator));

        WorkflowJob interrupted = WorkflowOrchestrator.FromTelegramRequest(
            "telegram-91", "Fix the issue and validate it.", maxAttempts: 2);
        WorkflowOrchestrator.BeginAttempt(interrupted, "request-crash", now);
        WorkflowJob recovered = JsonSerializer.Deserialize<WorkflowJob>(
            JsonSerializer.Serialize(interrupted))!;
        WorkflowOrchestrator.RecoverInterrupted(recovered);
        Assert(recovered.State == WorkflowState.Unknown,
            "An interrupted in-flight request must be marked unknown after restart.");
        Assert(WorkflowOrchestrator.BeginAttempt(recovered, "request-replay", now.AddMinutes(1)) is null,
            "An unknown result must not be automatically replayed.");

        WorkflowJob timedOut = WorkflowOrchestrator.FromTelegramRequest(
            "telegram-94", "Run bounded local checks.", maxAttempts: 2);
        WorkflowOrchestrator.BeginAttempt(timedOut, "request-timeout", now);
        Assert(!WorkflowOrchestrator.Expire(timedOut, now.AddMinutes(19))
            && WorkflowOrchestrator.Expire(timedOut, now.AddMinutes(21))
            && timedOut.State == WorkflowState.Blocked
            && WorkflowOrchestrator.BeginAttempt(timedOut, "request-timeout-replay", now.AddMinutes(22)) is null,
            "A 20-minute attempt timeout must fail-stop without replaying an uncertain dispatch.");

        WorkflowJob delayedSubmission = WorkflowOrchestrator.FromTelegramRequest(
            "telegram-95", "Wait for VS Code to accept the request.", maxAttempts: 2);
        WorkflowOrchestrator.BeginAttempt(delayedSubmission, "request-delayed", now);
        DateTime submittedUtc = now.AddMinutes(35);
        WorkflowOrchestrator.MarkSubmitted(delayedSubmission, "request-delayed", submittedUtc);
        Assert(delayedSubmission.AttemptDeadlineUtc == submittedUtc.Add(WorkflowOrchestrator.MaximumAttemptDuration)
            && !WorkflowOrchestrator.Expire(delayedSubmission, submittedUtc.AddMinutes(19))
            && WorkflowOrchestrator.Expire(delayedSubmission, submittedUtc.AddMinutes(21)),
            "Time spent waiting for bridge submission must not consume the 20-minute execution window.");

        WorkflowJob capped = WorkflowOrchestrator.FromTelegramRequest(
            "telegram-92", "Fix the issue and validate it.", maxAttempts: 1);
        WorkflowOrchestrator.BeginAttempt(capped, "request-cap", now);
        WorkflowOrchestrator.RecordCompletion(
            capped, capped.TaskId, "request-cap", "request-cap", "completed", "response", now.AddMinutes(1));
        WorkflowCriterionProof[] failed = capped.Criteria.Select(criterion => new WorkflowCriterionProof(
            criterion.Id, WorkflowCriterionStatus.Partial, "CopilotClaim", "unverified", "Evidence is incomplete.")).ToArray();
        Assert(WorkflowOrchestrator.ApplyReview(
            capped, "Gözcü", failed, authorizedHuman: false, static (_, _, _) => false) == WorkflowState.Failed,
            "An unmet criterion at the attempt cap must fail-stop.");
        Assert(WorkflowOrchestrator.BeginAttempt(capped, "request-over-cap", now.AddMinutes(2)) is null,
            "No attempt may run beyond the persisted cap.");

        WorkflowJob cancelled = WorkflowOrchestrator.FromTelegramRequest(
            "telegram-93", "Investigate a local issue.", maxAttempts: 2);
        WorkflowOrchestrator.Cancel(cancelled);
        Assert(cancelled.State == WorkflowState.Cancelled
            && WorkflowOrchestrator.BeginAttempt(cancelled, "request-cancelled", now) is null,
            "Cancellation must be terminal and prevent dispatch.");

        AssertThrows(() => WorkflowOrchestrator.FromRecommendation(
            "workflow-invalid", WorkflowSource.ArGe, "research-invalid", "Unsupported claim",
            ["file:///private/secret"], "Unbounded scope", "Gözcü"));
    }

    private static async Task WorkflowEvidenceVerifierUsesMeasuredLocalResultsAsync()
    {
        DateTime resultUtc = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
        WorkflowJob testJob = WorkflowOrchestrator.FromTelegramRequest(
            "telegram-evidence", "Implement and run the fixed AgentWatcher regression suite.");
        WorkflowDispatch dispatch = WorkflowOrchestrator.BeginAttempt(testJob, "request-evidence", resultUtc)!;
        WorkflowOrchestrator.RecordCompletion(
            testJob, dispatch.TaskId, dispatch.RequestId, dispatch.RequestId,
            "completed", "Implementation result", resultUtc.AddMinutes(1));
        WorkflowCriterion testCriterion = testJob.Criteria.Single(
            criterion => criterion.ValidatorId == WorkflowOrchestrator.AgentWatcherRegressionValidator);

        string directory = Path.Combine(Path.GetTempPath(), "AgentWatcher.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            DateTimeOffset testStarted = new(resultUtc.AddMinutes(2));
            using LocalTestAutomation automation = new(
                Path.Combine(directory, "automation.json"),
                directory,
                _ => Task.FromResult("PASS fixed offline suite"),
                () => testStarted,
                TimeSpan.FromMinutes(5));
            automation.EnableNewTrial();
            await automation.TickAsync(CancellationToken.None);
            LocalTestAutomationStatus measuredStatus = automation.Status;
            string testReference = WorkflowEvidenceVerifier.GetTestRunReference(
                testJob, testCriterion, measuredStatus)
                ?? throw new InvalidOperationException("The measured fixed-suite run was not recognized.");
            WorkflowCriterionProof measuredProof = new(
                testCriterion.Id,
                WorkflowCriterionStatus.Met,
                "TestRun",
                testReference,
                "LocalTestAutomation persisted a successful fixed-suite run.");
            Assert(WorkflowEvidenceVerifier.Verify(
                    testJob,
                    testCriterion,
                    measuredProof,
                    measuredStatus,
                    Path.Combine(directory, "missing-health.json")),
                "Production TestRun validation must use a passed LocalTestAutomation result newer than this job.");

            LocalTestAutomationStatus staleStatus = measuredStatus with
            {
                LastRunStartedUtc = new DateTimeOffset(resultUtc.AddSeconds(-1))
            };
            Assert(!WorkflowEvidenceVerifier.Verify(
                    testJob, testCriterion, measuredProof, staleStatus, Path.Combine(directory, "missing-health.json")),
                "A test run predating the correlated result must remain unverified.");
            foreach (string unsuccessfulOutcome in new[] { "Failed", "TimedOut", "Stopped", "Running" })
            {
                LocalTestAutomationStatus unsuccessfulStatus = measuredStatus with
                {
                    LastOutcome = unsuccessfulOutcome
                };
                Assert(WorkflowEvidenceVerifier.GetTestRunReference(
                        testJob, testCriterion, unsuccessfulStatus) is null
                    && !WorkflowEvidenceVerifier.Verify(
                        testJob,
                        testCriterion,
                        measuredProof,
                        unsuccessfulStatus,
                        Path.Combine(directory, "missing-health.json")),
                    $"{unsuccessfulOutcome} local test runs must remain unverified.");
            }
            WorkflowCriterionProof humanClaim = measuredProof with
            {
                EvidenceKind = "HumanReview",
                EvidenceReference = "telegram-human-review"
            };
            Assert(!WorkflowEvidenceVerifier.Verify(
                    testJob, testCriterion, humanClaim, measuredStatus, Path.Combine(directory, "missing-health.json")),
                "A locally verifiable criterion cannot be marked met by a generic human claim in place of TestRun evidence.");

            WorkflowJob healthJob = WorkflowOrchestrator.FromSupervisorFinding(
                "telegram-health-proof",
                "health-proof",
                "Local database connectivity",
                isHealthy: false,
                "Synthetic fixture only.",
                maxAttempts: 1);
            WorkflowOrchestrator.Approve(healthJob, authorized: true);
            WorkflowDispatch healthDispatch = WorkflowOrchestrator.BeginAttempt(
                healthJob, "request-health-proof", resultUtc)!;
            WorkflowOrchestrator.RecordCompletion(
                healthJob,
                healthDispatch.TaskId,
                healthDispatch.RequestId,
                healthDispatch.RequestId,
                "completed",
                "Remediation report",
                resultUtc.AddMinutes(1));
            WorkflowCriterion healthCriterion = healthJob.Criteria[0];
            DateTime freshHealthTime = resultUtc.AddMinutes(2);
            string healthPath = Path.Combine(directory, "latest-health.json");
            await File.WriteAllTextAsync(
                healthPath,
                JsonSerializer.Serialize(new
                {
                    CheckedAtLocal = freshHealthTime,
                    Checks = new[]
                    {
                        new { Name = healthCriterion.ValidationTarget, IsHealthy = true, Details = "synthetic" }
                    }
                }));
            string healthReference = WorkflowEvidenceVerifier.GetSupervisorHealthReference(
                healthJob, healthCriterion, healthPath)
                ?? throw new InvalidOperationException("The fresh healthy report was not recognized.");
            WorkflowCriterionProof healthProof = new(
                healthCriterion.Id,
                WorkflowCriterionStatus.Met,
                "TestRun",
                healthReference,
                "Fresh local health report confirms the exact check is healthy.");
            Assert(WorkflowEvidenceVerifier.Verify(
                    healthJob, healthCriterion, healthProof, measuredStatus, healthPath),
                "Supervisor health criteria must be measured from a newer persisted report for the exact check.");

            await File.WriteAllTextAsync(
                healthPath,
                JsonSerializer.Serialize(new
                {
                    CheckedAtLocal = freshHealthTime.AddMinutes(1),
                    Checks = new[]
                    {
                        new { Name = healthCriterion.ValidationTarget, IsHealthy = false, Details = "synthetic" }
                    }
                }));
            Assert(!WorkflowEvidenceVerifier.Verify(
                    healthJob, healthCriterion, healthProof, measuredStatus, healthPath),
                "A stale or now-unhealthy report must not satisfy the stored health proof.");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    internal static async Task RunProductionLocalTestEvidenceAsync(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        string root = Path.GetFullPath(workspaceRoot);
        string directory = Path.Combine(Path.GetTempPath(), "AgentWatcher.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            DateTime startedUtc = DateTime.UtcNow.AddMinutes(-1);
            WorkflowJob job = WorkflowOrchestrator.FromTelegramRequest(
                "telegram-production-runner",
                "Run the fixed local AgentWatcher regression suite and report only measured evidence.");
            WorkflowDispatch dispatch = WorkflowOrchestrator.BeginAttempt(
                job, "request-production-runner", startedUtc)
                ?? throw new InvalidOperationException("The synthetic local evidence fixture did not dispatch.");
            if (!WorkflowOrchestrator.RecordCompletion(
                    job,
                    dispatch.TaskId,
                    dispatch.RequestId,
                    dispatch.RequestId,
                    "completed",
                    "Synthetic correlated fixture result.",
                    startedUtc.AddSeconds(1)))
            {
                throw new InvalidOperationException("The synthetic local evidence fixture result was rejected.");
            }

            using LocalTestAutomation automation = new(
                Path.Combine(directory, "production-runner-state.json"),
                root);
            automation.EnableNewTrial();
            await automation.TickAsync(CancellationToken.None);
            LocalTestAutomationStatus status = automation.Status;
            if (status.LastOutcome != "Passed")
            {
                throw new InvalidOperationException(
                    $"The actual fixed local regression runner did not pass (outcome: {status.LastOutcome}).");
            }

            WorkflowCriterion criterion = job.Criteria.Single(
                candidate => candidate.ValidatorId == WorkflowOrchestrator.AgentWatcherRegressionValidator);
            string reference = WorkflowEvidenceVerifier.GetTestRunReference(job, criterion, status)
                ?? throw new InvalidOperationException("The actual runner status did not produce fresh TestRun evidence.");
            WorkflowCriterionProof proof = new(
                criterion.Id,
                WorkflowCriterionStatus.Met,
                "TestRun",
                reference,
                "The production LocalTestAutomation runner completed its fixed local test command.");
            if (!WorkflowEvidenceVerifier.Verify(
                    job,
                    criterion,
                    proof,
                    status,
                    Path.Combine(directory, "unused-health-report.json")))
            {
                throw new InvalidOperationException("The production TestRun verifier rejected the actual runner evidence.");
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static void RejectsInvalidRecipientAndTaskIds()
    {
        AssertThrows(() => TelegramWorkflowNotice.Create("telegram-1", 0, TelegramWorkflowStage.Queued));
        AssertThrows(() => TelegramWorkflowNotice.Create("../private", 4242, TelegramWorkflowStage.Queued));
    }

    private static void AssertThrows(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
        {
            return;
        }

        throw new InvalidOperationException("Invalid notice input must be rejected.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
