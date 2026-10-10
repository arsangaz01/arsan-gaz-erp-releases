using ArsanGazERP.AgentWatcher;
using System.Text.Json;

internal static class GozcuAutonomyTests
{
    const string Source = "https://learn.microsoft.com/en-us/dotnet/";

    public static async Task RunAsync()
    {
        await RunningQueueStatusIsPersisted();
        await DefaultsAndScheduling();
        await ResearchQueriesRotateAndPersist();
        await JudgmentRequiresAnExplicitActionabilityConclusion();
        await HierarchyAndRosterRotation();
        HierarchyRejectsInvalidDelegationAndEvidence();
        await DailyCounterDoesNotBlockCalls();
        await LegacyBudgetWaitResumesImmediately();
        await CycleBudgetIncludesFailures();
        await RetryBackoffAndResume();
        await RetryLimitSurvivesRestart();
        await CancelAndPause();
        await InterruptedReservationIsNotRefunded();
        await SingleFlight();
        await UnavailableAndEmptyEvidence();
        await InvalidModelResponseStopsHierarchy();
        await DisabledQueuedCycleDoesNotCall();
        CorruptStateFailsClosed();
        await DiskFailurePreventsCalls();
        await PolicyAndClockBoundaries();
        await CloudflareInternalRetriesAreBudgeted();
        await TruncatedSourceCannotBecomeEvidence();
        await ShutdownCancellationResumesWithoutRefund();
        await PromptTransportLimits();
        Console.WriteLine("PASS: Gozcu search rotation, actionability, hierarchy, evidence, budget, persistence, cancellation and offline regressions (mock only).");
    }

    static async Task RunningQueueStatusIsPersisted()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "gozcu-running-status-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            TaskItem item = new()
            {
                Id = "gozcu-cycle-00000001",
                Status = "Bekliyor",
                Error = "previous error"
            };
            DateTime started = DateTime.Now;
            item.MarkRunning(started);
            string path = Path.Combine(directory, item.Id + ".task.json");
            await WorkflowQueuePersistence.WriteAsync(path, item);
            TaskItem persisted = JsonSerializer.Deserialize<TaskItem>(await File.ReadAllTextAsync(path))
                ?? throw new InvalidDataException("The persisted queue item was null.");

            Check(persisted.Status == TaskItem.RunningStatus
                && persisted.Started == started
                && persisted.Error is null,
                "The queue item must persist its running state so the queue UI does not show active analysis as waiting.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    static async Task ResearchQueriesRotateAndPersist()
    {
        using Fixture f = new();
        AutonomyCycle first = f.Start();
        string firstQuery = first.Query;
        Check(firstQuery == GozcuAutonomy.ResearchQueries[0],
            "The first cycle must use the first configured business research query.");
        f.Reload();
        Check(f.Engine.State.Cycle!.Query == firstQuery,
            "A restart must resume the exact query already persisted for the active cycle.");
        await f.Run();

        f.Now = f.Now.AddHours(12);
        AutonomyCycle second = f.Start();
        Check(second.Query != firstQuery
            && second.Query == GozcuAutonomy.ResearchQueries[1],
            "A due cycle must deterministically rotate to the next configured research query.");
        await f.Run();
        Check(f.Queries.SequenceEqual(new[] { firstQuery, second.Query }),
            "Search must use each persisted cycle query, not a shared fixed query.");
    }

    static async Task JudgmentRequiresAnExplicitActionabilityConclusion()
    {
        using Fixture f = new();
        f.JudgmentConclusion = "ACTIONABLE: yes";
        f.Start();
        AutonomyCycle actionable = await f.Run();
        Check(actionable.ActionabilityDecision == "ACTIONABLE: yes",
            "Only the final judgment's explicit yes marker may make a recommendation actionable.");
        Check(f.Prompts.Last().Contains("ACTIONABLE: yes", StringComparison.Ordinal)
            && f.Prompts.Last().Contains("ACTIONABLE: no", StringComparison.Ordinal),
            "The final judgment prompt must require an explicit yes/no decision.");
        Check(GozcuAutonomy.Report(actionable).EndsWith("ACTIONABLE: yes", StringComparison.Ordinal),
            "The durable report must retain the final actionability decision.");

        f.Now = f.Now.AddHours(12);
        f.JudgmentConclusion = "ACTIONABLE: yes\nAdditional text after the decision";
        AutonomyCycle ambiguous = f.Start();
        await f.Run();
        Check(ambiguous.ActionabilityDecision == "ACTIONABLE: missing/invalid",
            "A decision marker followed by more content must not authorize a recommendation.");

        f.Now = f.Now.AddHours(12);
        f.JudgmentConclusion = "ACTIONABLE: no";
        AutonomyCycle notActionable = f.Start();
        await f.Run();
        Check(notActionable.ActionabilityDecision == "ACTIONABLE: no",
            "An explicit no decision must remain report-only.");
    }

    static async Task DefaultsAndScheduling()
    {
        using Fixture f = new();
        f.Policy.Enabled = false;
        Check(f.Engine.EnsureCycle() is null && !File.Exists(f.Path), "Disabled defaults must not enqueue or persist a new cycle.");
        Check(!new AppSettings().EnableDailyResearch && !new AppSettings().GozcuAutonomy.Enabled,
            "Internet/model autonomy must default off.");
        f.Policy.Enabled = true;
        AutonomyCycle cycle = f.Start();
        Check(ReferenceEquals(cycle, f.Engine.EnsureCycle()), "Duplicate ticks must share one durable cycle.");
        await f.Run();
        Check(f.Engine.EnsureCycle() is null, "No new cycle before interval.");
        f.Now = f.Now.AddHours(12);
        Check(f.Start().Id != cycle.Id, "A due period must produce a distinct cycle.");
    }

    static async Task HierarchyAndRosterRotation()
    {
        using Fixture f = new();
        f.Start();
        AutonomyCycle cycle = await f.Run();
        Check(cycle.Status == "Completed" && cycle.Calls == 9 && f.Searches == 1 && f.Roles.Count == 8,
            "Four experts require exactly one search and eight model requests.");
        Check(f.Roles.SequenceEqual(new[] { "supervisor", "jarvis", "crm", "sales", "finance", "planner", "jarvis", "supervisor" }),
            "Only Gozcu commands, Jarvis coordinates/synthesizes, Gozcu judges last.");
        Check(cycle.Results.Values.All(result => result.Citations.SequenceEqual(new[] { Source })), "All stages must cite supplied evidence.");
        Check(f.Prompts.All(prompt => prompt.Contains(Source) && !prompt.Contains("PRIVATE_CUSTOMER")), "Shared public evidence only.");
        Check(f.Prompts.All(prompt => prompt.Contains(
                "İzin verilen kaynak URL'leri (aynen kopyala; yalnızca bunları kullan):\n" + Source,
                StringComparison.Ordinal)),
            "Every model stage must receive the exact allow-listed citation URLs.");
        Check(f.Prompts[0].Contains(
                "\"assignments\":[{\"agentId\":\"crm\",\"task\":\"analysis\"},{\"agentId\":\"sales\",\"task\":\"analysis\"},{\"agentId\":\"finance\",\"task\":\"analysis\"},{\"agentId\":\"planner\",\"task\":\"analysis\"}]",
                StringComparison.Ordinal),
            "The supervisor prompt must include the exact constrained assignment JSON shape.");
        Check(f.Prompts.Skip(1).All(prompt => !prompt.Contains("\"assignments\":", StringComparison.Ordinal)),
            "Non-planning prompts must not grant assignment/delegation fields.");
        Check(GozcuAutonomy.Report(cycle).Contains("judgment") && GozcuAutonomy.Report(cycle).Contains(Source), "Persistent report includes final judgment and evidence.");
        f.Now = f.Now.AddHours(12);
        AutonomyCycle next = f.Start();
        await f.Run();
        Check(cycle.Roster.Concat(next.Roster).Distinct().Count() == 5 && f.Roles.Contains("researcher"),
            "Every specialist in the seven-profile roster must be reached across cycles.");
        int calls = f.Roles.Count;
        await f.Run();
        Check(f.Roles.Count == calls, "Completed cycles must not repeat paid calls.");
    }

    static void HierarchyRejectsInvalidDelegationAndEvidence()
    {
        string Plan(params object[] tasks) => JsonSerializer.Serialize(new { analysis = "Evidence-backed reasoning", citations = new[] { Source }, assignments = tasks });
        object Assignment(string agent, string task = "analysis") => new { agentId = agent, task };
        foreach (string role in new[] { "jarvis", "supervisor", "unknown", "CRM" })
            Throws<InvalidDataException>(() => GozcuAutonomy.ParseFinding(Plan(Assignment(role)), [Source], ["crm"]));
        Throws<InvalidDataException>(() => GozcuAutonomy.ParseFinding(Plan(Assignment("crm", "execute-code")), [Source], ["crm"]));
        Throws<InvalidDataException>(() => GozcuAutonomy.ParseFinding(Plan(Assignment("crm"), Assignment("crm")), [Source], ["crm", "sales"]));
        Throws<InvalidDataException>(() => GozcuAutonomy.ParseFinding(Plan(Assignment("crm")), [Source], ["crm", "sales"]));
        Throws<InvalidDataException>(() => GozcuAutonomy.ParseFinding(Plan(Assignment("crm")), [Source], null));
        Throws<InvalidDataException>(() => GozcuAutonomy.ParseFinding("{\"analysis\":\"x\",\"citations\":[\"https://invented.invalid/\"]}", [Source], null));
        var singleCitation = GozcuAutonomy.ParseFinding(
            "{\"analysis\":\"x\",\"citations\":\"" + Source + "\"}",
            [Source],
            null);
        Check(singleCitation.Finding.Citations.SequenceEqual(new[] { Source }),
            "A single citation string is accepted only as an exact supplied source URL.");
        var wrappedCitation = GozcuAutonomy.ParseFinding(
            "{\"analysis\":\"x\",\"citations\":\"Microsoft Learn: " + Source + "\"}",
            [Source],
            null);
        Check(wrappedCitation.Finding.Citations.SequenceEqual(new[] { Source }),
            "Citation prose is normalized only when its sole URL is an exact supplied source.");
        const string secondSource = "https://learn.microsoft.com/en-us/aspnet/core/";
        var combinedCitations = GozcuAutonomy.ParseFinding(
            "{\"analysis\":\"x\",\"citations\":\"Kaynaklar: " + Source + " " + secondSource + "\"}",
            [Source, secondSource],
            null);
        Check(combinedCitations.Finding.Citations.SequenceEqual(new[] { Source, secondSource }),
            "Multiple URLs in one citation field normalize only when every URL is supplied.");
        Throws<InvalidDataException>(() => GozcuAutonomy.ParseFinding(
            "{\"analysis\":\"x\",\"citations\":\"https://invented.invalid/\"}",
            [Source],
            null));
        Throws<InvalidDataException>(() => GozcuAutonomy.ParseFinding(
            "{\"analysis\":\"x\",\"citations\":\"" + Source + " https://invented.invalid/\"}",
            [Source],
            null));
        Throws<InvalidDataException>(() => GozcuAutonomy.ParseFinding("{\"analysis\":\"x\",\"citations\":[]}", [Source], null));
        Throws<InvalidDataException>(() => GozcuAutonomy.ParseFinding("{\"analysis\":\"x\",\"citations\":[\"" + Source + "\"],\"tools\":[\"shell\"]}", [Source], null));
        Throws<InvalidDataException>(() => GozcuAutonomy.ParseFinding("{\"analysis\":\"x\",\"analysis\":\"y\",\"citations\":[\"" + Source + "\"]}", [Source], null));
        Throws<JsonException>(() => GozcuAutonomy.ParseFinding("not JSON", [Source], null));
        var reordered = GozcuAutonomy.ParseFinding(Plan(Assignment("sales"), Assignment("crm")), [Source], ["crm", "sales"]);
        Check(reordered.Assignments.Select(task => task.AgentId).SequenceEqual(new[] { "crm", "sales" }),
            "Valid out-of-order model assignments must normalize for deterministic restart validation.");
        var fenced = GozcuAutonomy.ParseFinding(
            "```json\n" + Plan(Assignment("crm")) + "\n```",
            [Source],
            ["crm"]);
        Check(fenced.Assignments.Single().AgentId == "crm",
            "A single JSON markdown fence must not discard an otherwise valid constrained plan.");
        Throws<JsonException>(() => GozcuAutonomy.ParseFinding(
            "```xml\n" + Plan(Assignment("crm")) + "\n```",
            [Source],
            ["crm"]));
        Throws<JsonException>(() => GozcuAutonomy.ParseFinding(
            "```json\n" + Plan(Assignment("crm")) + "\n```\nextra",
            [Source],
            ["crm"]));
    }

    static async Task DailyCounterDoesNotBlockCalls()
    {
        using Fixture f = new();
        f.Start();
        f.Engine.State.DailyCalls = 21;
        AutonomyCycle cycle = await f.Run();
        Check(cycle.Status == "Completed" && cycle.Calls == 9 && f.Roles.Count == 8,
            "Daily usage must not stop Copilot calls at an application-imposed limit.");
        Check(f.Engine.State.DailyCalls == 30, "Daily usage remains informational and can exceed the former limit.");
        Check(f.Engine.CanDispatch(cycle.Id), "A completed cycle remains terminal and does not block other queue work.");
        Check(f.Engine.CanDispatch("existing-telegram-job"), "Legacy queue work remains eligible while autonomy waits.");
        f.Reload();
        f.Now = f.Now.AddDays(1);
        AutonomyCycle? next = f.Engine.EnsureCycle();
        Check(next is not null && f.Engine.State.DailyCalls == 0,
            "A new scheduled cycle starts regardless of prior usage; daily counter resets for reporting only.");
    }

    static async Task LegacyBudgetWaitResumesImmediately()
    {
        using Fixture f = new();
        AutonomyCycle cycle = f.Start();
        cycle.Status = "BudgetWait";
        cycle.RetryAfterUtc = f.Now.AddDays(1);
        cycle.Detail = "UTC günlük çağrı bütçesi doldu; ertesi gün sürdürülür.";
        File.WriteAllText(f.Path, JsonSerializer.Serialize(f.Engine.State));

        f.Reload();
        Check(f.Engine.State.Cycle!.Status == "Queued" && f.Engine.State.Cycle.RetryAfterUtc == default,
            "A previously budget-blocked active cycle resumes immediately after removing the app daily cap.");
        cycle = await f.Run();
        Check(cycle.Status == "Completed" && cycle.Calls == 9,
            "The recovered cycle continues with only the provider's own plan limits.");
    }

    static async Task CycleBudgetIncludesFailures()
    {
        using Fixture f = new();
        f.Policy.MaxCallsPerCycle = 9;
        f.Start();
        f.Failures = 1;
        Check((await f.Run()).Status == "Backoff", "Transient request must back off.");
        f.Now = f.Now.AddMinutes(5);
        AutonomyCycle cycle = await f.Run();
        Check(cycle.Status == "Failed" && cycle.Calls == 9 && !cycle.Results.ContainsKey("judgment"),
            "Failed attempts consume cycle budget; do not fake a completed judgment.");
    }

    static async Task RetryBackoffAndResume()
    {
        using Fixture f = new();
        f.Start();
        f.Failures = 1;
        AutonomyCycle cycle = await f.Run();
        Check(cycle.Status == "Backoff" && cycle.RetryAfterUtc == f.Now.AddMinutes(5), "First failure backoff is exactly five minutes.");
        int attempted = f.Roles.Count;
        await f.Run();
        Check(f.Roles.Count == attempted, "No retry before deadline.");
        f.Reload();
        f.Now = f.Now.AddMinutes(5);
        cycle = await f.Run();
        Check(cycle.Status == "Completed" && cycle.Calls == 10 && f.Searches == 1, "Retry only missing step, keep consumed call.");
    }

    static async Task RetryLimitSurvivesRestart()
    {
        using Fixture f = new();
        f.Start();
        f.Failures = 10;
        await f.Run();
        f.Reload();
        f.Now = f.Now.AddMinutes(5);
        AutonomyCycle cycle = await f.Run();
        Check(cycle.RetryAfterUtc == f.Now.AddMinutes(10), "Second failure backoff is ten minutes.");
        f.Reload();
        f.Now = f.Now.AddMinutes(10);
        cycle = await f.Run();
        Check(cycle.Status == "Failed" && cycle.Calls == 4 && f.Roles.Count == 3, "Three attempts per step across restarts, no retry storm.");
    }

    static async Task CancelAndPause()
    {
        using Fixture f = new();
        f.Start();
        using CancellationTokenSource cts = new();
        f.OnModel = () => { f.Policy.Paused = true; cts.Cancel(); };
        AutonomyCycle cycle = await f.Run(cts.Token);
        Check(cycle.Status == "Paused" && cycle.Calls == 2 && cycle.Results.Count == 0, "Pause retains reservation but discards cancelled output.");
        f.Reload();
        Check(!f.Engine.CanDispatch(cycle.Id), "Persisted policy pause prevents dispatch.");
        f.Policy.Paused = false;
        f.OnModel = null;
        cycle = await f.Run();
        Check(cycle.Status == "Completed" && f.Searches == 1, "Resume keeps completed public search.");
        f.Now = f.Now.AddHours(12);
        cycle = f.Start();
        f.OnModel = f.Engine.Cancel;
        cycle = await f.Run();
        Check(cycle.Status == "Cancelled", "Explicit cancel is terminal, not automatic retry.");
        int attempted = f.Roles.Count;
        await f.Run();
        Check(f.Roles.Count == attempted, "Cancelled cycle must never call a provider.");
    }

    static async Task InterruptedReservationIsNotRefunded()
    {
        using Fixture f = new();
        AutonomyCycle cycle = f.Start();
        f.OnModel = () =>
        {
            GozcuAutonomy restarted = new(f.Path, () => f.Policy, () => f.Now);
            Check(restarted.State.Cycle!.Calls == 2 && restarted.State.Cycle.Status == "Backoff", "In-flight reservation must already be durable before model call.");
            Check(restarted.State.Cycle.Results.Count == 0 && restarted.State.Cycle.Evidence.Length > 0, "Interrupted stage has no false result.");
            Check(!restarted.CanDispatch(cycle.Id), "Restart recovery imposes five-minute backoff.");
        };
        f.Failures = 1;
        await f.Run();
        Check(f.Engine.State.DailyCalls == 2, "In-flight external request cannot be refunded.");
    }

    static async Task SingleFlight()
    {
        using Fixture f = new();
        f.Start();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.BeforeModel = async () => { entered.TrySetResult(); await release.Task; };
        Task<AutonomyCycle> first = f.Run();
        await entered.Task;
        await ThrowsAsync<InvalidOperationException>(async () => await f.Run());
        Check(f.Engine.EnsureCycle() is null && !f.Engine.CanDispatch(f.Engine.State.Cycle!.Id), "Timer overlap cannot schedule another cycle.");
        release.SetResult();
        Check((await first).Status == "Completed", "Original single flight must complete.");
    }

    static async Task UnavailableAndEmptyEvidence()
    {
        using Fixture f = new();
        f.Start();
        f.Unavailable = "Offline / no configured provider";
        AutonomyCycle cycle = await f.Run();
        Check(cycle.Status == "Unavailable" && cycle.Calls == 0 && f.Searches == 0 && f.Roles.Count == 0, "No provider/offline must make zero calls and never claim success.");
        f.Unavailable = null;
        f.Now = f.Now.AddMinutes(15);
        f.Evidence = "No search matches";
        cycle = await f.Run();
        Check(cycle.Status == "Failed" && cycle.Calls == 1 && f.Roles.Count == 0, "No evidence means no analysis, not fabricated citations.");
    }

    static async Task InvalidModelResponseStopsHierarchy()
    {
        using Fixture f = new();
        f.Start();
        f.ModelResponse = "{\"analysis\":\"Do it\",\"citations\":[\"" + Source + "\"],\"assignments\":[{\"agentId\":\"supervisor\",\"task\":\"analysis\"}]}";
        AutonomyCycle cycle = await f.Run();
        Check(cycle.Status == "Failed" && f.Roles.Count == 1 && !cycle.Results.ContainsKey("plan"), "Invalid Gozcu plan must prevent all delegation.");
    }

    static async Task DisabledQueuedCycleDoesNotCall()
    {
        using Fixture f = new();
        AutonomyCycle cycle = f.Start();
        f.Policy.Enabled = false;
        Check(!f.Engine.CanDispatch(cycle.Id), "Disabling must also gate already queued cycles.");
        await f.Run();
        Check(f.Searches == 0 && f.Roles.Count == 0, "Direct dispatch also checks enabled policy.");
        f.Policy.Enabled = true;
        await f.Run();
        Check(f.Engine.State.Cycle!.Status == "Completed", "Explicit re-enable resumes bounded cycle.");
    }

    static void CorruptStateFailsClosed()
    {
        using Fixture f = new();
        foreach (string payload in new[] { "{broken", "null", "{\"Version\":99}", "{\"DailyCalls\":-1}", "{\"Cycle\":{\"Roster\":null}}" })
        {
            File.WriteAllText(f.Path, payload);
            try { f.Reload(); throw new Exception("Corrupt state was accepted."); }
            catch (Exception exception) when (exception is JsonException or InvalidDataException) { }
            Check(File.ReadAllText(f.Path) == payload, "Corrupt state must be preserved, never replaced with fresh budget.");
        }
    }

    static async Task DiskFailurePreventsCalls()
    {
        using Fixture f = new();
        f.Start();
        Directory.CreateDirectory(f.Path + ".tmp");
        await ThrowsAsync<UnauthorizedAccessException>(async () => await f.Run());
        Check(f.Searches == 0 && f.Roles.Count == 0 && !f.Engine.CanDispatch(f.Engine.State.Cycle!.Id),
            "Failure to persist reservation prevents provider calls and blocks later ticks.");
        Directory.Delete(f.Path + ".tmp");
    }

    static async Task PolicyAndClockBoundaries()
    {
        using Fixture f = new();
        f.Policy.MaxTasks = 5;
        Throws<InvalidDataException>(() => f.Start());
        f.Policy.MaxTasks = 4;
        f.Policy.Provider = "Otomatik";
        Throws<InvalidDataException>(() => f.Start());
        f.Policy.Provider = "Copilot Pro";
        f.Start();
        f.Engine.State.DailyCalls = 23;
        await f.Run();
        f.Now = f.Now.AddDays(-1);
        f.Reload();
        await f.Run();
        Check(f.Searches == 1 && f.Roles.Count == 8 && f.Engine.State.DailyCalls == 32,
            "Clock rollback must not reset informational usage or rerun a completed cycle.");
    }

    static async Task CloudflareInternalRetriesAreBudgeted()
    {
        using Fixture f = new();
        f.Policy.Provider = "Cloudflare Workers AI";
        Throws<InvalidDataException>(() => f.Start());
        f.Policy.MaxCallsPerCycle = 24;
        Throws<InvalidDataException>(() => f.Start());
        f.Policy.MaxCallsPerCycle = 25;
        f.Start();
        f.Engine.State.DailyCalls = 4;
        AutonomyCycle cycle = await f.Run();
        Check(cycle.Status == "Completed" && cycle.Calls == 25 && f.Roles.Count == 8,
            "Cloudflare usage is no longer blocked by a local daily counter; cycle and retry safeguards remain.");
        Check(cycle.Status == "Completed" && cycle.Calls == 25 && f.Searches == 1 && f.Roles.Count == 8,
            "Cloudflare worst-case internal model retries are reserved durably and only missing final judgment resumes.");
    }

    static async Task TruncatedSourceCannotBecomeEvidence()
    {
        using Fixture f = new();
        f.Start();
        f.Evidence = new string('x', 1980) + " https://learn.microsoft.com/very-long-path";
        AutonomyCycle cycle = await f.Run();
        Check(cycle.Status == "Failed" && f.Roles.Count == 0, "A URL cut by the shared evidence bound must not become a fabricated source.");
    }

    static async Task ShutdownCancellationResumesWithoutRefund()
    {
        using Fixture f = new();
        f.Start();
        using CancellationTokenSource closing = new();
        f.OnModel = closing.Cancel;
        AutonomyCycle cycle = await f.Run(closing.Token);
        Check(cycle.Status == "Paused" && cycle.Calls == 2 && !f.Policy.Paused, "Shutdown cancellation pauses work without changing settings or refunding the request.");
        f.OnModel = null;
        f.Reload();
        cycle = await f.Run();
        Check(cycle.Status == "Completed" && cycle.Calls == 10 && f.Searches == 1, "Next launch resumes only missing analysis.");
    }

    static async Task PromptTransportLimits()
    {
        using Fixture f = new();
        f.Evidence += new string('x', 2000 - f.Evidence.Length);
        f.Analysis = new string('a', 1200);
        f.Start();
        Check((await f.Run()).Status == "Completed", "Maximum-size analysis and evidence must fit provider limits.");
        f.Now = f.Now.AddHours(12);
        f.Start();
        Check((await f.Run()).Status == "Completed" && f.Roles.Contains("researcher"), "Long researcher profile must also fit transport limits.");
    }

    static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    sealed class Fixture : IDisposable
    {
        readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gozcu-tests-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(directory, "state.json");
        public AutonomyPolicy Policy { get; } = new() { Enabled = true };
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-10-04T10:00:00Z");
        public GozcuAutonomy Engine { get; private set; }
        public List<string> Roles { get; } = [];
        public List<string> Prompts { get; } = [];
        public List<string> Queries { get; } = [];
        public int Searches { get; private set; }
        public int Failures { get; set; }
        public string Evidence { get; set; } = "Public documentation\nKaynak: " + Source + "\nÖzet: planning and risk guidance";
        public string? Unavailable { get; set; }
        public string? ModelResponse { get; set; }
        public string? JudgmentConclusion { get; set; }
        public string Analysis { get; set; } = "Evidence, uncertainty and testable advice grounded in supplied snippets.";
        public Action? OnModel { get; set; }
        public Func<Task>? BeforeModel { get; set; }

        public Fixture()
        {
            Directory.CreateDirectory(directory);
            Engine = new(Path, () => Policy, () => Now);
        }

        public void Reload() => Engine = new(Path, () => Policy, () => Now);
        public AutonomyCycle Start() => Engine.EnsureCycle() ?? throw new InvalidOperationException("Cycle not due.");
        public Task<AutonomyCycle> Run(CancellationToken token = default) =>
            Engine.RunAsync(Engine.State.Cycle!.Id, Search, Complete, () => Unavailable, token);

        Task<string> Search(string query, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Check(query == Engine.State.Cycle!.Query, "Search must use the durable query selected for this cycle.");
            Check(File.ReadAllText(Path).Contains("\"InFlight\": \"evidence\""), "Search reservation must be persisted first.");
            Queries.Add(query);
            Searches++;
            return Task.FromResult(Evidence);
        }

        async Task<string> Complete(string role, string system, string input, CancellationToken token)
        {
            Roles.Add(role);
            Prompts.Add(system + input);
            Check(system.Length + input.Length + 2 <= 6000, "Every role prompt must fit the existing Cloudflare transport limit.");
            string analysis = Analysis;
            if (input.Contains("Son denetimi ve kararı ver", StringComparison.Ordinal)
                && JudgmentConclusion is not null)
            {
                analysis += "\n" + JudgmentConclusion;
            }
            OnModel?.Invoke();
            if (BeforeModel is not null) await BeforeModel();
            token.ThrowIfCancellationRequested();
            if (Failures-- > 0) throw new HttpRequestException("Mock network unavailable");
            if (ModelResponse is not null) return ModelResponse;
            if (!Engine.State.Cycle!.Results.ContainsKey("plan"))
                return JsonSerializer.Serialize(new
                {
                    analysis,
                    citations = new[] { Source },
                    assignments = Engine.State.Cycle.Roster.Select(agent => new { agentId = agent, task = "analysis" })
                });
            return JsonSerializer.Serialize(new { analysis, citations = new[] { Source } });
        }

        public void Dispose() => Directory.Delete(directory, recursive: true);
    }
}
