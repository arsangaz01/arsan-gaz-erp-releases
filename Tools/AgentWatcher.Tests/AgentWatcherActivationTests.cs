using System.Diagnostics;
using ArsanGazERP.AgentWatcher;

namespace ArsanGazERP.AgentWatcher.Tests;

internal static class AgentWatcherActivationTests
{
    internal static async Task RunAsync()
    {
        string name = "Local\\ArsanGazERP.ActivationTest." + Guid.NewGuid().ToString("N");
        using (EventWaitHandle listener = AgentWatcherActivation.CreateListener(name))
        {
            if (listener.WaitOne(0))
                throw new InvalidOperationException("A new listener must not open the window.");
            AgentWatcherActivation.RequestShow(name, TimeSpan.FromSeconds(1));
            if (!listener.WaitOne(1000) || listener.WaitOne(0))
                throw new InvalidOperationException("An activation must be consumed exactly once.");

            string executable = Environment.ProcessPath
                ?? throw new InvalidOperationException("Test executable path is unavailable.");
            ProcessStartInfo start = new(executable) { UseShellExecute = false };
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(typeof(AgentWatcherActivationTests).Assembly.Location);
            start.ArgumentList.Add("--watcher-activation-send");
            start.ArgumentList.Add(name);
            using Process child = Process.Start(start)
                ?? throw new InvalidOperationException("Activation test child could not start.");
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            if (child.ExitCode != 0 || !listener.WaitOne(1000) || listener.WaitOne(0))
                throw new InvalidOperationException("A separate process must signal the existing listener exactly once.");
        }

        Task request = Task.Run(() => AgentWatcherActivation.RequestShow(name, TimeSpan.FromSeconds(2)));
        await Task.Delay(100);
        using (EventWaitHandle listener = AgentWatcherActivation.CreateListener(name))
        {
            await request.WaitAsync(TimeSpan.FromSeconds(3));
            if (!listener.WaitOne(1000))
                throw new InvalidOperationException("Activation must tolerate the first instance starting its listener.");
        }

        try
        {
            AgentWatcherActivation.RequestShow(name, TimeSpan.FromMilliseconds(100));
            throw new InvalidOperationException("An unavailable listener must not report success.");
        }
        catch (WaitHandleCannotBeOpenedException)
        {
        }

        bool fallbackAttempted = false;
        AgentWatcherActivation.RequestShow(name, TimeSpan.FromMilliseconds(100), () =>
        {
            fallbackAttempted = true;
            return true;
        });
        if (!fallbackAttempted)
            throw new InvalidOperationException("A missing listener must invoke the legacy-window fallback.");

        try
        {
            AgentWatcherActivation.RequestShow(name, TimeSpan.FromMilliseconds(100), () => false);
            throw new InvalidOperationException("A failed window fallback must not report success.");
        }
        catch (WaitHandleCannotBeOpenedException)
        {
        }

        Console.WriteLine("PASS: activation is one-shot, cross-process, startup-race tolerant, and falls back explicitly when a legacy listener is absent.");
    }
}
