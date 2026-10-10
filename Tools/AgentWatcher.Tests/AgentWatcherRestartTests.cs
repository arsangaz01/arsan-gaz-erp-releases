using System.Diagnostics;
using ArsanGazERP.AgentWatcher;

namespace ArsanGazERP.AgentWatcher.Tests;

internal static class AgentWatcherRestartTests
{
    internal static void Run()
    {
        string executablePath = Path.Combine(
            Path.GetTempPath(),
            "ArsanGazERP.AgentWatcher",
            "ArsanGazERP.AgentWatcher.exe");
        ProcessStartInfo backgroundStart = AgentWatcherRestart.CreateStartInfo(executablePath, backgroundMode: true);
        if (backgroundStart.FileName != Path.GetFullPath(executablePath) ||
            backgroundStart.WorkingDirectory != Path.GetDirectoryName(Path.GetFullPath(executablePath)) ||
            !backgroundStart.ArgumentList.SequenceEqual(["--background"]) ||
            backgroundStart.UseShellExecute ||
            !backgroundStart.CreateNoWindow ||
            backgroundStart.WindowStyle != ProcessWindowStyle.Hidden)
        {
            throw new InvalidOperationException("AgentWatcher arka plan yeniden başlatma ayarları beklenenlerle uyuşmuyor.");
        }

        ProcessStartInfo foregroundStart = AgentWatcherRestart.CreateStartInfo(executablePath, backgroundMode: false);
        if (foregroundStart.ArgumentList.Count != 0 ||
            !foregroundStart.UseShellExecute ||
            foregroundStart.CreateNoWindow ||
            foregroundStart.WindowStyle != ProcessWindowStyle.Normal)
        {
            throw new InvalidOperationException("AgentWatcher ön plan yeniden başlatması arka plan bayrağını korumamalı.");
        }

        Console.WriteLine("PASS: AgentWatcher restart preserves foreground/background launch mode.");
    }
}
