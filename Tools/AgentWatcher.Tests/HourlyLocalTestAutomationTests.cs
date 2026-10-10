using System.Text.Json;
using ArsanGazERP.AgentWatcher;

namespace ArsanGazERP.AgentWatcher.Tests;

internal static class HourlyLocalTestAutomationTests
{
    internal static async Task RunAsync()
    {
        await DefaultsToOffAndRunsOnlyAtTheNextHourAsync();
        await LimitsRunsAndSpacesSuccessfulRunsAsync();
        await StopsOnFailureAndTimeoutAsync();
        await StopCancelsTheActiveRunAsync();
        await RestartAndCorruptStateFailClosedAsync();
        await ExplicitCommandsStartAndStopOnlyTheHourlyModeAsync();
        await SharedWorkGateGivesQueuedWorkPriorityAndPreventsOverlapAsync();
    }

    internal static async Task RunProductionFixedRunnerAsync(string workspaceRoot)
    {
        string directory = NewDirectory();
        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            string path = Path.Combine(directory, "production-hourly.json");
            HourlyLocalAutomationState dueState = new()
            {
                Enabled = true,
                RequestedAtUtc = now,
                WindowStartedUtc = now,
                NextRunUtc = now.AddSeconds(-1),
                LastOutcome = "Armed",
                LastReport = "Offline production-runner integration test.",
                RunSequence = 0
            };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(dueState));

            using HourlyLocalTestAutomation automation = new(path, workspaceRoot);
            Assert(await automation.TickAsync(CancellationToken.None)
                && automation.Status.Enabled
                && automation.Status.LastOutcome == "Passed"
                && automation.Status.RunsInWindow == 1
                && automation.Status.NextRunUtc is { } nextRun
                && nextRun > DateTimeOffset.UtcNow,
                "The production hourly constructor must execute the fixed offline runner and persist success only after exit code zero.");
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    private static async Task DefaultsToOffAndRunsOnlyAtTheNextHourAsync()
    {
        string directory = NewDirectory();
        try
        {
            DateTimeOffset now = new(2026, 10, 6, 1, 17, 0, TimeSpan.Zero);
            int runs = 0;
            string path = Path.Combine(directory, "hourly.json");
            using HourlyLocalTestAutomation automation = new(
                path,
                directory,
                _ =>
                {
                    runs++;
                    return Task.FromResult("synthetic suite passed");
                },
                () => now);

            Assert(!automation.Status.Enabled && automation.Status.NextRunUtc is null,
                "The new hourly policy must be disabled and unscheduled by default.");
            Assert(!await automation.TickAsync(CancellationToken.None) && runs == 0,
                "A disabled hourly policy must not run its test harness.");
            Assert(automation.EnableHourly(), "The explicit opt-in must arm the hourly mode.");
            Assert(automation.Status.NextRunUtc == new DateTimeOffset(2026, 10, 6, 2, 0, 0, TimeSpan.Zero),
                "The first due time must be the next UTC hour boundary.");
            Assert(!await automation.TickAsync(CancellationToken.None) && runs == 0,
                "The harness must not run before its persisted due time.");

            now = automation.Status.NextRunUtc!.Value;
            Assert(await automation.TickAsync(CancellationToken.None)
                && automation.Status.LastOutcome == "Passed"
                && automation.Status.NextRunUtc == now.AddHours(1)
                && runs == 1,
                "A successful run must persist one result and space the next start by one full hour.");
            Assert(!await automation.TickAsync(CancellationToken.None) && runs == 1,
                "Repeated timer ticks in the same hour must not start duplicate runs.");
            automation.Stop();
            Assert(automation.EnableHourly() && automation.Status.RunsInWindow == 1,
                "Stopping and explicitly rearming must preserve the persisted 24-hour run cap.");

            using HourlyLocalTestAutomation restarted = new(
                path,
                directory,
                _ =>
                {
                    runs++;
                    return Task.FromResult("synthetic suite passed");
                },
                () => now);
            Assert(!await restarted.TickAsync(CancellationToken.None) && runs == 1,
                "Restart at the same checkpoint must not replay a completed run.");
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    private static async Task LimitsRunsAndSpacesSuccessfulRunsAsync()
    {
        string directory = NewDirectory();
        try
        {
            DateTimeOffset now = new(2026, 10, 6, 0, 5, 0, TimeSpan.Zero);
            int runs = 0;
            using HourlyLocalTestAutomation automation = new(
                Path.Combine(directory, "hourly.json"),
                directory,
                _ =>
                {
                    runs++;
                    return Task.FromResult("synthetic suite passed");
                },
                () => now);
            automation.EnableHourly();

            for (int index = 0; index < HourlyLocalTestAutomation.MaximumRunsPerWindow; index++)
            {
                now = automation.Status.NextRunUtc!.Value;
                Assert(await automation.TickAsync(CancellationToken.None),
                    $"Hourly run {index + 1} should execute exactly once when due.");
                Assert(automation.Status.RunsInWindow <= HourlyLocalTestAutomation.MaximumRunsPerWindow,
                    "The 24-run rolling-window cap must never be exceeded.");
            }

            Assert(runs == 24 && automation.Status.RunsInWindow == 24,
                "At most 24 runs may be recorded inside a 24-hour window.");
            now = automation.Status.NextRunUtc!.Value;
            Assert(await automation.TickAsync(CancellationToken.None)
                && runs == 25
                && automation.Status.RunsInWindow == 1,
                "After the prior 24-hour window expires, one new run may start without catch-up replay.");
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    private static async Task StopsOnFailureAndTimeoutAsync()
    {
        string directory = NewDirectory();
        try
        {
            DateTimeOffset now = new(2026, 10, 6, 1, 0, 0, TimeSpan.Zero);
            int failures = 0;
            using HourlyLocalTestAutomation failing = new(
                Path.Combine(directory, "failed.json"),
                directory,
                _ =>
                {
                    failures++;
                    throw new InvalidOperationException("synthetic failure");
                },
                () => now);
            failing.EnableHourly();
            now = failing.Status.NextRunUtc!.Value;
            Assert(await failing.TickAsync(CancellationToken.None)
                && !failing.Status.Enabled
                && failing.Status.LastOutcome == "Failed",
                "A failed test run must disable the schedule and preserve its failure status.");
            now = now.AddHours(2);
            Assert(!await failing.TickAsync(CancellationToken.None) && failures == 1,
                "A failed run must not be retried automatically.");

            using HourlyLocalTestAutomation timedOut = new(
                Path.Combine(directory, "timeout.json"),
                directory,
                async token =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return "unreachable";
                },
                () => now,
                TimeSpan.FromMilliseconds(20));
            timedOut.EnableHourly();
            now = timedOut.Status.NextRunUtc!.Value;
            Assert(await timedOut.TickAsync(CancellationToken.None)
                && !timedOut.Status.Enabled
                && timedOut.Status.LastOutcome == "TimedOut",
                "A run exceeding its configured test deadline must stop without retry.");
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    private static async Task StopCancelsTheActiveRunAsync()
    {
        string directory = NewDirectory();
        try
        {
            DateTimeOffset now = new(2026, 10, 6, 1, 0, 0, TimeSpan.Zero);
            TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using HourlyLocalTestAutomation automation = new(
                Path.Combine(directory, "stop.json"),
                directory,
                async token =>
                {
                    started.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return "unreachable";
                },
                () => now,
                TimeSpan.FromMinutes(1));
            automation.EnableHourly();
            now = automation.Status.NextRunUtc!.Value;
            Task run = automation.TickAsync(CancellationToken.None);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            automation.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(!automation.Status.Enabled && automation.Status.LastOutcome == "Stopped",
                "The stop switch must cancel the active run and persist a stopped state.");
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    private static async Task RestartAndCorruptStateFailClosedAsync()
    {
        string directory = NewDirectory();
        try
        {
            string path = Path.Combine(directory, "restart.json");
            DateTimeOffset now = new(2026, 10, 6, 1, 0, 0, TimeSpan.Zero);
            HourlyLocalAutomationState inFlight = new()
            {
                Enabled = true,
                RequestedAtUtc = now,
                WindowStartedUtc = now,
                RunsInWindow = 1,
                LastRunStartedUtc = now,
                NextRunUtc = now.AddHours(1),
                LastOutcome = "Running",
                RunSequence = 1,
                ActiveRunId = "hourly-local-1"
            };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(inFlight));
            int runs = 0;
            using HourlyLocalTestAutomation recovered = new(
                path,
                directory,
                _ =>
                {
                    runs++;
                    return Task.FromResult("unexpected");
                },
                () => now.AddHours(2));
            Assert(!recovered.Status.Enabled
                && recovered.Status.LastOutcome == "Uncertain"
                && !await recovered.TickAsync(CancellationToken.None)
                && runs == 0,
                "A process restart during a run must record uncertainty and must never replay it.");

            string stoppingPath = Path.Combine(directory, "stopping.json");
            HourlyLocalAutomationState stopping = new()
            {
                Enabled = false,
                RequestedAtUtc = now,
                WindowStartedUtc = now,
                RunsInWindow = 1,
                LastRunStartedUtc = now,
                LastOutcome = "Stopping",
                RunSequence = 1,
                ActiveRunId = "hourly-local-1"
            };
            await File.WriteAllTextAsync(stoppingPath, JsonSerializer.Serialize(stopping));
            using HourlyLocalTestAutomation stoppedDuringRun = new(
                stoppingPath,
                directory,
                _ =>
                {
                    runs++;
                    return Task.FromResult("unexpected");
                },
                () => now);
            Assert(stoppedDuringRun.Status.LastOutcome == "Uncertain" &&
                !stoppedDuringRun.Status.Enabled &&
                !await stoppedDuringRun.TickAsync(CancellationToken.None) &&
                runs == 0,
                "A restart while the stop switch was cancelling a runner must fail closed as uncertain.");

            string corruptPath = Path.Combine(directory, "corrupt.json");
            const string corruptContent = "{invalid";
            await File.WriteAllTextAsync(corruptPath, corruptContent);
            using HourlyLocalTestAutomation corrupt = new(
                corruptPath,
                directory,
                _ => Task.FromResult("unexpected"),
                () => now);
            Assert(corrupt.Status.IsUnavailable && !corrupt.Status.Enabled,
                "Corrupt scheduler state must remain disabled and fail closed.");
            AssertThrows(() => corrupt.EnableHourly());
            Assert(await File.ReadAllTextAsync(corruptPath) == corruptContent,
                "Corrupt state must be preserved, not replaced with a fresh enabled schedule.");
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    private static async Task SharedWorkGateGivesQueuedWorkPriorityAndPreventsOverlapAsync()
    {
        using AgentWatcherWorkGate gate = new();
        bool hasQueuedWork = true;
        int scheduledRuns = 0;
        bool skipped = !await gate.TryRunScheduledAsync(
            () => hasQueuedWork,
            _ =>
            {
                scheduledRuns++;
                return Task.CompletedTask;
            },
            CancellationToken.None);
        Assert(skipped && scheduledRuns == 0,
            "Queued user work must prevent a scheduled test from starting.");

        hasQueuedWork = false;
        TaskCompletionSource scheduledStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource finishScheduled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<string> order = [];
        Task<bool> scheduled = gate.TryRunScheduledAsync(
            () => hasQueuedWork,
            async _ =>
            {
                order.Add("scheduled-start");
                scheduledRuns++;
                scheduledStarted.SetResult();
                await finishScheduled.Task;
                order.Add("scheduled-finish");
            },
            CancellationToken.None);
        await scheduledStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task queue = gate.RunQueueAsync(
            () =>
            {
                order.Add("telegram-queue");
                return Task.CompletedTask;
            },
            CancellationToken.None);
        await Task.Yield();
        Assert(!queue.IsCompleted,
            "The real queue processor must not overlap a current fixed local test run.");
        finishScheduled.SetResult();
        Assert(await scheduled.WaitAsync(TimeSpan.FromSeconds(5)),
            "The scheduled work should finish while holding the shared gate.");
        await queue.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(order.SequenceEqual(["scheduled-start", "scheduled-finish", "telegram-queue"]),
            "Queued work must take the shared gate immediately after the current scheduled run reaches its safe boundary.");
    }

    private static async Task ExplicitCommandsStartAndStopOnlyTheHourlyModeAsync()
    {
        string directory = NewDirectory();
        try
        {
            using HourlyLocalTestAutomation automation = new(
                Path.Combine(directory, "commands.json"),
                directory,
                _ => Task.FromResult("synthetic suite passed"),
                () => new DateTimeOffset(2026, 10, 6, 1, 17, 0, TimeSpan.Zero));

            Assert(HourlyLocalAutomationCommands.TryHandle(
                    HourlyLocalAutomationCommands.StartCommand,
                    "unexpected-argument",
                    automation)?.Contains("parametre almaz", StringComparison.Ordinal) == true
                && !automation.Status.Enabled,
                "Start commands with unexpected arguments must not enable the schedule.");
            Assert(HourlyLocalAutomationCommands.TryHandle(
                    HourlyLocalAutomationCommands.StartCommand,
                    "",
                    automation)?.Contains("kuruldu", StringComparison.Ordinal) == true
                && automation.Status.Enabled,
                "The explicit start command must arm only the bounded hourly mode.");
            Assert(HourlyLocalAutomationCommands.TryHandle(
                    HourlyLocalAutomationCommands.StopCommand,
                    "",
                    automation)?.Contains("durduruldu", StringComparison.Ordinal) == true
                && !automation.Status.Enabled,
                "The explicit stop command must disable the hourly schedule.");
            Assert(HourlyLocalAutomationCommands.TryHandle("/not-hourly", "", automation) is null,
                "Unrelated Telegram commands must not be intercepted by the hourly controls.");
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    private static string NewDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "AgentWatcher.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static void AssertThrows(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            return;
        }

        throw new InvalidOperationException("The invalid operation should have been rejected.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
