using System.IO;
using System.Runtime.InteropServices;

namespace ArsanGazERP.Services;

public static class DesktopShortcutService
{
    public const string ErpShortcutName = "Arsan Gaz ERP";
    public const string WatcherShortcutName = "Arsan Gaz AgentWatcher";
    public const string WatcherShortcutArguments = "--background";

    public static IReadOnlyList<string> EnsureCurrentShortcuts(string applicationBaseDirectory)
    {
        string? erpExecutable = AgentWatcherPaths.ResolveCurrentPublishedErpExecutablePath(applicationBaseDirectory);
        if (erpExecutable is null || !File.Exists(erpExecutable))
        {
            return [];
        }

        string currentDirectory = Path.GetDirectoryName(Path.GetFullPath(erpExecutable))!;
        string runningDirectory = Path.GetFullPath(applicationBaseDirectory);
        if (!runningDirectory.StartsWith(currentDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        string watcherExecutable = Path.Combine(currentDirectory, "AgentWatcher", "ArsanGazERP.AgentWatcher.exe");
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(desktop) || !Directory.Exists(desktop))
        {
            return [];
        }

        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null)
        {
            return [];
        }

        List<string> updated = [];
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            if (EnsureShortcut(shell, Path.Combine(desktop, ErpShortcutName + ".lnk"), erpExecutable, string.Empty, ErpShortcutName))
            {
                updated.Add(ErpShortcutName);
            }

            if (File.Exists(watcherExecutable) &&
                EnsureShortcut(shell, Path.Combine(desktop, WatcherShortcutName + ".lnk"), watcherExecutable, WatcherShortcutArguments, WatcherShortcutName))
            {
                updated.Add(WatcherShortcutName);
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }

        return updated;
    }

    private static bool EnsureShortcut(dynamic shell, string shortcutPath, string targetPath, string arguments, string description)
    {
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        try
        {
            bool upToDate = File.Exists(shortcutPath) &&
                string.Equals((string)shortcut.TargetPath, targetPath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals((string)shortcut.Arguments, arguments, StringComparison.Ordinal);
            if (upToDate)
            {
                return false;
            }

            shortcut.TargetPath = targetPath;
            shortcut.Arguments = arguments;
            shortcut.WorkingDirectory = Path.GetDirectoryName(targetPath);
            shortcut.IconLocation = targetPath + ",0";
            shortcut.Description = description;
            shortcut.Save();
            return true;
        }
        finally
        {
            Marshal.FinalReleaseComObject(shortcut);
        }
    }
}
