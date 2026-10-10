namespace ArsanGazERP.AgentWatcher;

internal static class AgentWatcherWorkspace
{
    internal static string? FindSourceRoot(string startingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startingDirectory);
        for (DirectoryInfo? directory = new(Path.GetFullPath(startingDirectory));
            directory is not null;
            directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ArsanGazERP.csproj")) ||
                File.Exists(Path.Combine(directory.FullName, "ArsanGazERP.sln")))
                return directory.FullName;
        }
        return null;
    }
}
