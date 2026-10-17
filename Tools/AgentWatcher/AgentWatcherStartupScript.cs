using System.Text;

namespace ArsanGazERP.AgentWatcher;

internal static class AgentWatcherStartupScript
{
    internal const string StartupFileName = "ArsanGazERP-AgentWatcher.vbs";
    internal const string LegacyCmdFileName = "ArsanGazERP-AgentWatcher.cmd";

    /// <summary>
    /// Builds a hidden VBScript for the Windows Startup folder.
    /// A .cmd with <c>timeout</c> leaves a visible/hanging console at logon; VBS runs with no window.
    /// </summary>
    internal static string Build(
        string watcherExecutable,
        string? erpExecutable,
        bool startErp,
        bool startMinimized)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(watcherExecutable);

        string watcher = Path.GetFullPath(watcherExecutable);
        StringBuilder script = new();
        script.AppendLine("' Arsan Gaz ERP Windows startup (hidden; no cmd window)");
        script.AppendLine("Set sh = CreateObject(\"WScript.Shell\")");
        script.AppendLine("Set fso = CreateObject(\"Scripting.FileSystemObject\")");
        // Give VPN / network / Postgres time after logon.
        script.AppendLine("WScript.Sleep 20000");
        script.AppendLine($"watcher = \"{EscapeVbs(watcher)}\"");
        script.AppendLine("If Not fso.FileExists(watcher) Then WScript.Quit 1");
        script.AppendLine("sh.CurrentDirectory = fso.GetParentFolderName(watcher)");
        script.AppendLine("sh.Run \"\"\"\" & watcher & \"\"\" --background\", 0, False");

        if (startErp && !string.IsNullOrWhiteSpace(erpExecutable))
        {
            string erp = Path.GetFullPath(erpExecutable);
            int style = startMinimized ? 7 : 1; // 7 = minimized, 1 = normal, 0 = hidden
            script.AppendLine($"erp = \"{EscapeVbs(erp)}\"");
            script.AppendLine("If fso.FileExists(erp) Then");
            script.AppendLine("  WScript.Sleep 40000");
            script.AppendLine("  sh.CurrentDirectory = fso.GetParentFolderName(erp)");
            script.AppendLine($"  sh.Run \"\"\"\" & erp & \"\"\"\", {style}, False");
            script.AppendLine("End If");
        }

        return script.ToString();
    }

    private static string EscapeVbs(string value) => value.Replace("\"", "\"\"", StringComparison.Ordinal);
}
