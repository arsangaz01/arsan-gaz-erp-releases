using ArsanGazERP.AgentWatcher;

namespace ArsanGazERP.AgentWatcher.Tests;

internal static class AgentWatcherStartupScriptTests
{
    internal static void Run()
    {
        string watcherExecutable = Path.Combine(
            Path.GetTempPath(),
            "Arsan Gaz ERP",
            "AgentWatcher",
            "ArsanGazERP.AgentWatcher.exe");
        string erpExecutable = Path.Combine(
            Path.GetTempPath(),
            "Arsan Gaz ERP",
            "ArsanGazERP.exe");

        string withErp = AgentWatcherStartupScript.Build(
            watcherExecutable,
            erpExecutable,
            startErp: true,
            startMinimized: true);
        if (!withErp.Contains($"set \"WATCHER_EXE={Path.GetFullPath(watcherExecutable)}\"", StringComparison.Ordinal) ||
            !withErp.Contains("\"%WATCHER_EXE%\" --background", StringComparison.Ordinal) ||
            !withErp.Contains($"set \"ERP_EXE={Path.GetFullPath(erpExecutable)}\"", StringComparison.Ordinal) ||
            !withErp.Contains("start \"\" /MIN /D \"%ERP_DIR%\" \"%ERP_EXE%\"", StringComparison.Ordinal) ||
            !withErp.Contains("if \"%WATCHER_DIR:~-1%\"==\"\\\" set \"WATCHER_DIR=%WATCHER_DIR:~0,-1%\"", StringComparison.Ordinal) ||
            !withErp.Contains("if \"%ERP_DIR:~-1%\"==\"\\\" set \"ERP_DIR=%ERP_DIR:~0,-1%\"", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Windows startup must launch the background Watcher and optionally start ERP minimized without trailing-backslash /D quoting bugs.");
        }

        string watcherOnly = AgentWatcherStartupScript.Build(
            watcherExecutable,
            erpExecutable: null,
            startErp: false,
            startMinimized: false);
        if (!watcherOnly.Contains("\"%WATCHER_EXE%\" --background", StringComparison.Ordinal) ||
            watcherOnly.Contains("ERP_EXE", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Windows startup must be able to launch only AgentWatcher.");
        }

        string watcherWhenErpMissing = AgentWatcherStartupScript.Build(
            watcherExecutable,
            erpExecutable: null,
            startErp: true,
            startMinimized: true);
        if (!watcherWhenErpMissing.Contains("\"%WATCHER_EXE%\" --background", StringComparison.Ordinal) ||
            watcherWhenErpMissing.Contains("ERP_EXE", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A missing ERP executable must not prevent AgentWatcher from starting with Windows.");
        }

        Console.WriteLine("PASS: Windows startup launches AgentWatcher in background and honors ERP startup settings.");
    }
}
