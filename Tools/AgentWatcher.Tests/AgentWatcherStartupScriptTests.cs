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
        string watcherFull = Path.GetFullPath(watcherExecutable);
        string erpFull = Path.GetFullPath(erpExecutable);
        if (!withErp.Contains($"watcher = \"{watcherFull}\"", StringComparison.Ordinal) ||
            !withErp.Contains("sh.Run \"\"\"\" & watcher & \"\"\" --background\", 0, False", StringComparison.Ordinal) ||
            !withErp.Contains($"erp = \"{erpFull}\"", StringComparison.Ordinal) ||
            !withErp.Contains("sh.Run \"\"\"\" & erp & \"\"\"\", 7, False", StringComparison.Ordinal) ||
            !withErp.Contains("WScript.Sleep 20000", StringComparison.Ordinal) ||
            !withErp.Contains("WScript.Sleep 40000", StringComparison.Ordinal) ||
            withErp.Contains("timeout /t", StringComparison.Ordinal) ||
            withErp.Contains("@echo off", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Windows startup must be a hidden VBS that launches Watcher/ERP without a cmd console.");
        }

        string watcherOnly = AgentWatcherStartupScript.Build(
            watcherExecutable,
            erpExecutable: null,
            startErp: false,
            startMinimized: false);
        if (!watcherOnly.Contains("\"\"\"\" & watcher & \"\"\" --background\"", StringComparison.Ordinal) ||
            watcherOnly.Contains("erp =", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Windows startup must be able to launch only AgentWatcher.");
        }

        string watcherWhenErpMissing = AgentWatcherStartupScript.Build(
            watcherExecutable,
            erpExecutable: null,
            startErp: true,
            startMinimized: true);
        if (!watcherWhenErpMissing.Contains("--background", StringComparison.Ordinal) ||
            watcherWhenErpMissing.Contains("erp =", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A missing ERP executable must not prevent AgentWatcher from starting with Windows.");
        }

        Console.WriteLine("PASS: Windows startup launches AgentWatcher in background and honors ERP startup settings.");
    }
}
