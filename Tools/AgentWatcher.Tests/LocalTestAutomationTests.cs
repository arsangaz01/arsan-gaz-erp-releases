using ArsanGazERP.AgentWatcher;
using System.Diagnostics;

internal static class LocalTestAutomationTests
{
    public static async Task RunAsync()
    {
        await DefaultsOffAndOneRunPerUtcDay();
        await SevenRunAndSevenDayCaps();
        await FailedRunIsReportedAndConsumesItsDailySlot();
        await StopCancelsActiveRun();
        await RunDeadlineIsEnforced();
        await PersistedStatePreventsSameDayReplay();
        await ExpiredWindowDoesNotRun();
        FixedCommandCannotBeReplacedByUserInput();
        Console.WriteLine("PASS: bounded local test automation defaults, caps, stop, deadline, persistence and fixed command.");
    }

    static async Task DefaultsOffAndOneRunPerUtcDay()
    {
        using Fixture fixture = new();
        using LocalTestAutomation automation = fixture.Create();

        await automation.TickAsync(CancellationToken.None);
        Check(!automation.Status.Enabled && fixture.Calls == 0, "A fresh installation must not run tests.");

        automation.EnableNewTrial();
        await automation.TickAsync(CancellationToken.None);
        await automation.TickAsync(CancellationToken.None);
        Check(fixture.Calls == 1, "Only one local suite may run per UTC day.");
        Check(automation.Status.LastOutcome == "Passed" && automation.Status.Report.Contains("synthetic"),
            "The local report must record a successful synthetic test result.");
    }

    static async Task SevenRunAndSevenDayCaps()
    {
        using Fixture fixture = new();
        using LocalTestAutomation automation = fixture.Create();
        automation.EnableNewTrial();

        for (int day = 0; day < 7; day++)
        {
            await automation.TickAsync(CancellationToken.None);
            fixture.Now = fixture.Now.AddDays(1);
        }

        await automation.TickAsync(CancellationToken.None);
        Check(fixture.Calls == 7 && automation.Status.RunsStarted == 7,
            "The trial must stop before an eighth test suite.");
        Check(!automation.Status.Enabled, "The seven-run cap must turn automation off.");
    }

    static async Task FailedRunIsReportedAndConsumesItsDailySlot()
    {
        using Fixture fixture = new();
        using LocalTestAutomation automation = fixture.Create(
            _ => throw new InvalidOperationException("synthetic test failed API_KEY=do-not-store"));
        automation.EnableNewTrial();

        await automation.TickAsync(CancellationToken.None);
        await automation.TickAsync(CancellationToken.None);

        Check(fixture.Calls == 1 && automation.Status.LastOutcome == "Failed",
            "A failed test must be reported and must not be retried repeatedly on the same day.");
        Check(automation.Status.Report.Contains("synthetic test failed") &&
              !automation.Status.Report.Contains("do-not-store"),
            "The local failure report must retain actionable detail while redacting credentials.");
    }

    static async Task StopCancelsActiveRun()
    {
        using Fixture fixture = new();
        TaskCompletionSource<CancellationToken> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using LocalTestAutomation automation = fixture.Create(async token =>
        {
            started.TrySetResult(token);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return "unreachable";
        });
        automation.EnableNewTrial();

        Task run = automation.TickAsync(CancellationToken.None);
        CancellationToken activeToken = await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        automation.Stop();
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        Check(activeToken.IsCancellationRequested, "Stop must cancel the active test process token.");
        Check(!automation.Status.Enabled && automation.Status.LastOutcome == "Stopped",
            "Stop must persist an off state and a stopped report.");
    }

    static async Task RunDeadlineIsEnforced()
    {
        using Fixture fixture = new();
        using LocalTestAutomation automation = fixture.Create(
            async token =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return "unreachable";
            },
            maximumRunTime: TimeSpan.FromMilliseconds(50));
        automation.EnableNewTrial();

        await automation.TickAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

        Check(automation.Status.LastOutcome == "TimedOut",
            "A test suite that exceeds its deadline must be stopped and reported as timed out.");
    }

    static async Task PersistedStatePreventsSameDayReplay()
    {
        using Fixture fixture = new();
        using (LocalTestAutomation first = fixture.Create())
        {
            first.EnableNewTrial();
            await first.TickAsync(CancellationToken.None);
        }

        using LocalTestAutomation restarted = fixture.Create();
        await restarted.TickAsync(CancellationToken.None);
        Check(fixture.Calls == 1 && restarted.Status.RunsStarted == 1,
            "A restart must not repeat a test already started on the same UTC day.");
    }

    static async Task ExpiredWindowDoesNotRun()
    {
        using Fixture fixture = new();
        using LocalTestAutomation automation = fixture.Create();
        automation.EnableNewTrial();
        fixture.Now = fixture.Now.AddDays(7);

        await automation.TickAsync(CancellationToken.None);

        Check(fixture.Calls == 0 && !automation.Status.Enabled,
            "An expired seven-day window must stop without launching tests.");
    }

    static void FixedCommandCannotBeReplacedByUserInput()
    {
        using Fixture fixture = new();
        string project = Path.Combine(
            fixture.DirectoryPath,
            "Tools",
            "AgentWatcher.Tests",
            "ArsanGazERP.AgentWatcher.Tests.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        File.WriteAllText(project, "<Project />");

        ProcessStartInfo start = LocalTestAutomation.BuildTestStartInfo(
            fixture.DirectoryPath,
            @"C:\Program Files\dotnet\dotnet.exe");

        Check(!start.UseShellExecute, "The test runner must not invoke a shell.");
        Check(start.WorkingDirectory == fixture.DirectoryPath, "The test runner must remain in the workspace.");
        Check(start.ArgumentList.SequenceEqual(
            [
                "run",
                "--project",
                project,
                "--no-restore",
                "--configuration",
                "Release",
                "--no-launch-profile"
            ]), "Only the fixed local synthetic test project and no-restore arguments may run.");
    }

    static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    sealed class Fixture : IDisposable
    {
        readonly string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "ArsanGazERP-LocalAutomationTests-" + Guid.NewGuid().ToString("N"));

        public string DirectoryPath => temporaryDirectory;
        public string StatePath => Path.Combine(temporaryDirectory, "automation-state.json");
        public DateTimeOffset Now { get; set; } = new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        public int Calls { get; private set; }

        public Fixture()
        {
            Directory.CreateDirectory(temporaryDirectory);
        }

        public LocalTestAutomation Create(
            Func<CancellationToken, Task<string>>? run = null,
            TimeSpan? maximumRunTime = null) =>
            new(
                StatePath,
                temporaryDirectory,
                async token =>
                {
                    Calls++;
                    if (run is not null)
                    {
                        return await run(token);
                    }

                    token.ThrowIfCancellationRequested();
                    return "synthetic local test suite passed";
                },
                () => Now,
                maximumRunTime);

        public void Dispose()
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }
}
