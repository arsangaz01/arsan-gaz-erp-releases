using System.IO;

namespace ArsanGazERP.Services;

public static class AgentWatcherPaths
{
    public static string DataRoot { get; } = ResolveDataRoot(
        AppContext.BaseDirectory,
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ArsanGazERP",
            "AgentWatcherData"));
    public static string QueueDirectory => Path.Combine(DataRoot, "Queue");
    public static string ReportsDirectory => Path.Combine(DataRoot, "Reports");
    public static string LogsDirectory => Path.Combine(DataRoot, "Logs");
    public static string SettingsFile => Path.Combine(DataRoot, "settings.json");

    internal static string ResolveDataRoot(string applicationBaseDirectory, string localDataRoot)
    {
        if (Directory.Exists(localDataRoot))
        {
            return localDataRoot;
        }

        for (DirectoryInfo? directory = new(applicationBaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ArsanGazERP.csproj")))
            {
                string relativePath = Path.GetRelativePath(directory.FullName, applicationBaseDirectory);
                if (relativePath.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return Path.Combine(directory.FullName, "Tools", "AgentWatcherData");
                }

                break;
            }
        }

        return localDataRoot;
    }

    internal static string? ResolveCurrentPublishedErpExecutablePath(string applicationBaseDirectory)
    {
        string? versionedBundleExecutable = null;

        for (DirectoryInfo? directory = new(applicationBaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (directory.Name.Equals("current", StringComparison.OrdinalIgnoreCase) &&
                directory.Parent?.Name.Equals("publish", StringComparison.OrdinalIgnoreCase) == true)
            {
                return Path.Combine(directory.FullName, "ArsanGazERP.exe");
            }

            if (directory.Parent?.Name.Equals("publish", StringComparison.OrdinalIgnoreCase) == true &&
                File.Exists(Path.Combine(directory.FullName, "ArsanGazERP.exe")))
            {
                versionedBundleExecutable ??= Path.Combine(directory.FullName, "ArsanGazERP.exe");
            }

            if (File.Exists(Path.Combine(directory.FullName, "ArsanGazERP.csproj")))
            {
                string currentBundleExecutable =
                    Path.Combine(directory.FullName, "publish", "current", "ArsanGazERP.exe");
                return File.Exists(currentBundleExecutable)
                    ? currentBundleExecutable
                    : versionedBundleExecutable ?? currentBundleExecutable;
            }
        }

        return versionedBundleExecutable;
    }

    internal static string? ResolveCurrentPublishedWatcherExecutablePath(string applicationBaseDirectory)
    {
        string? versionedBundleExecutable = null;

        for (DirectoryInfo? directory = new(applicationBaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (directory.Name.Equals("current", StringComparison.OrdinalIgnoreCase) &&
                directory.Parent?.Name.Equals("publish", StringComparison.OrdinalIgnoreCase) == true)
            {
                return Path.Combine(directory.FullName, "AgentWatcher", "ArsanGazERP.AgentWatcher.exe");
            }

            if (directory.Parent?.Name.Equals("publish", StringComparison.OrdinalIgnoreCase) == true)
            {
                string candidate = Path.Combine(directory.FullName, "AgentWatcher", "ArsanGazERP.AgentWatcher.exe");
                if (File.Exists(candidate))
                {
                    versionedBundleExecutable ??= candidate;
                }
            }

            if (File.Exists(Path.Combine(directory.FullName, "ArsanGazERP.csproj")))
            {
                string currentBundleExecutable = Path.Combine(
                    directory.FullName,
                    "publish",
                    "current",
                    "AgentWatcher",
                    "ArsanGazERP.AgentWatcher.exe");
                return versionedBundleExecutable is not null && File.Exists(currentBundleExecutable)
                    ? currentBundleExecutable
                    : versionedBundleExecutable;
            }
        }

        return versionedBundleExecutable;
    }

    internal static string? ResolveStartupErpExecutable(string applicationBaseDirectory, string fallbackExecutable)
    {
        string? currentExecutable = ResolveCurrentPublishedErpExecutablePath(applicationBaseDirectory);
        if (currentExecutable is not null && File.Exists(currentExecutable))
        {
            return currentExecutable;
        }

        return File.Exists(fallbackExecutable) ? Path.GetFullPath(fallbackExecutable) : null;
    }

    internal static string? ResolveStartupWatcherExecutable(string applicationBaseDirectory, string fallbackExecutable)
    {
        string? currentExecutable = ResolveCurrentPublishedWatcherExecutablePath(applicationBaseDirectory);
        if (currentExecutable is not null && File.Exists(currentExecutable))
        {
            return currentExecutable;
        }

        return File.Exists(fallbackExecutable) ? Path.GetFullPath(fallbackExecutable) : null;
    }
}
