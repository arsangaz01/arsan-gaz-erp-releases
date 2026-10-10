using ArsanGazERP.AgentWatcher;

internal static class AgentWatcherWorkspaceTests
{
    public static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "arsangaz-workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string package = Path.Combine(directory, "publish", "current");
            string watcher = Path.Combine(package, "AgentWatcher");
            string tests = Path.Combine(directory, "Tools", "AgentWatcher.Tests");
            Directory.CreateDirectory(watcher);
            Directory.CreateDirectory(tests);
            File.WriteAllText(Path.Combine(directory, "ArsanGazERP.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(package, "ArsanGazERP.exe"), "synthetic");
            File.WriteAllText(Path.Combine(tests, "ArsanGazERP.AgentWatcher.Tests.csproj"), "<Project />");

            string sourceRoot = AgentWatcherWorkspace.FindSourceRoot(watcher)
                ?? throw new InvalidDataException("Published watcher must find its actual source workspace.");
            WorkflowActivationFixture.Check(sourceRoot == directory,
                "The published ERP executable must not hide the source workspace for offline testing.");
            var command = LocalTestAutomation.BuildTestStartInfo(sourceRoot,
                Path.Combine(directory, "synthetic-dotnet.exe"));
            WorkflowActivationFixture.Check(command.WorkingDirectory == directory
                && command.ArgumentList.Contains(Path.Combine(tests, "ArsanGazERP.AgentWatcher.Tests.csproj")),
                "The production fixed runner must point to the canonical test project, not publish/current/Tools.");

            File.Delete(Path.Combine(directory, "ArsanGazERP.csproj"));
            WorkflowActivationFixture.Check(AgentWatcherWorkspace.FindSourceRoot(watcher) is null,
                "A package alone is not a source workspace and cannot validate code.");
            File.WriteAllText(Path.Combine(directory, "ArsanGazERP.sln"), "synthetic solution");
            WorkflowActivationFixture.Check(AgentWatcherWorkspace.FindSourceRoot(watcher) == directory,
                "Existing solution-based workspace discovery must remain supported.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
        Console.WriteLine("PASS: published watcher resolves the real source root for the fixed offline test runner (offline).");
    }
}
