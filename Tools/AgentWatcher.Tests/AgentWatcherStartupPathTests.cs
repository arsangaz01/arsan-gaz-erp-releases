using ArsanGazERP.AgentWatcher;
using ArsanGazERP.Services;
using System.Diagnostics;

namespace ArsanGazERP.AgentWatcher.Tests;

internal static class AgentWatcherStartupPathTests
{
    internal static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "AgentWatcherStartupPathTests", Guid.NewGuid().ToString("N"));
        string currentExecutable = Path.Combine(
            root,
            "publish",
            "current",
            "AgentWatcher",
            "ArsanGazERP.AgentWatcher.exe");
        string versionedExecutable = Path.Combine(
            root,
            "publish",
            "release-previous",
            "AgentWatcher",
            "ArsanGazERP.AgentWatcher.exe");
        string developmentExecutable = Path.Combine(
            root,
            "Tools",
            "AgentWatcher",
            "bin",
            "Release",
            "net10.0-windows",
            "ArsanGazERP.AgentWatcher.exe");

        Directory.CreateDirectory(Path.GetDirectoryName(currentExecutable)!);
        Directory.CreateDirectory(Path.GetDirectoryName(versionedExecutable)!);
        Directory.CreateDirectory(Path.GetDirectoryName(developmentExecutable)!);
        File.WriteAllText(Path.Combine(root, "ArsanGazERP.csproj"), "<Project />");
        File.WriteAllText(currentExecutable, string.Empty);
        File.WriteAllText(versionedExecutable, string.Empty);
        File.WriteAllText(developmentExecutable, string.Empty);

        try
        {
            AssertPath(
                currentExecutable,
                AgentWatcherPaths.ResolveCurrentPublishedWatcherExecutablePath(
                    Path.GetDirectoryName(currentExecutable)!),
                "Current bundle must resolve to itself.");
            AssertPath(
                currentExecutable,
                AgentWatcherPaths.ResolveCurrentPublishedWatcherExecutablePath(
                    Path.GetDirectoryName(versionedExecutable)!),
                "A versioned bundle must resolve to the current bundle.");
            AssertPath(
                currentExecutable,
                AgentWatcherPaths.ResolveStartupWatcherExecutable(
                    Path.GetDirectoryName(versionedExecutable)!,
                    Path.Combine(root, "missing-watcher.exe")),
                "Startup must prefer the current published AgentWatcher bundle.");
            if (AgentWatcherPaths.ResolveCurrentPublishedWatcherExecutablePath(
                    Path.GetDirectoryName(developmentExecutable)!) is not null)
            {
                throw new InvalidOperationException("Development builds must not be redirected to the published bundle.");
            }
            AssertPath(
                developmentExecutable,
                AgentWatcherPaths.ResolveStartupWatcherExecutable(
                    Path.GetDirectoryName(developmentExecutable)!,
                    developmentExecutable),
                "A development build must fall back to its own AgentWatcher executable.");

            File.Delete(currentExecutable);
            AssertPath(
                versionedExecutable,
                AgentWatcherPaths.ResolveCurrentPublishedWatcherExecutablePath(
                    Path.GetDirectoryName(versionedExecutable)!),
                "A versioned bundle must remain usable when no current bundle exists.");
            AssertPath(
                versionedExecutable,
                AgentWatcherPaths.ResolveStartupWatcherExecutable(
                    Path.GetDirectoryName(versionedExecutable)!,
                    Path.Combine(root, "missing-watcher.exe")),
                "Startup must fall back to an existing versioned AgentWatcher bundle.");

            string[] forwardedArguments = ["--background", "--custom-value", "two words"];
            ProcessStartInfo startInfo = AgentWatcherRestart.CreateStartInfo(versionedExecutable, forwardedArguments);
            if (!startInfo.ArgumentList.SequenceEqual(forwardedArguments) ||
                startInfo.UseShellExecute ||
                !startInfo.CreateNoWindow ||
                startInfo.WindowStyle != ProcessWindowStyle.Hidden)
            {
                throw new InvalidOperationException("Redirected launch must preserve arguments and background mode.");
            }

            Console.WriteLine("PASS: stale AgentWatcher paths redirect to current and preserve launch arguments.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertPath(string expected, string? actual, string message)
    {
        if (!string.Equals(Path.GetFullPath(expected), actual is null ? null : Path.GetFullPath(actual), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(message);
        }
    }
}
