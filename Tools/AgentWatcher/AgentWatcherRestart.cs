using System.Diagnostics;

namespace ArsanGazERP.AgentWatcher;

internal static class AgentWatcherRestart
{
    internal static ProcessStartInfo CreateStartInfo(string executablePath, bool backgroundMode)
    {
        return CreateStartInfo(executablePath, backgroundMode ? ["--background"] : []);
    }

    internal static ProcessStartInfo CreateStartInfo(string executablePath, IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        string fullPath = Path.GetFullPath(executablePath);
        bool backgroundMode = arguments.Any(argument =>
            string.Equals(argument, "--background", StringComparison.OrdinalIgnoreCase));
        ProcessStartInfo startInfo = new(fullPath)
        {
            WorkingDirectory = Path.GetDirectoryName(fullPath)!,
            UseShellExecute = !backgroundMode,
            CreateNoWindow = backgroundMode,
            WindowStyle = backgroundMode ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }
}
