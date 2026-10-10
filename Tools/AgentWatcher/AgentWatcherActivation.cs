using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ArsanGazERP.AgentWatcher;

internal static class AgentWatcherActivation
{
    internal const string SignalName = "Local\\ArsanGazERP.AgentWatcher.ShowWindow.V760";
    private const int RestoreWindow = 9;

    internal static EventWaitHandle CreateListener(string name)
        => new(false, EventResetMode.AutoReset, name);

    internal static void RequestShow(string name, TimeSpan timeout, Func<bool>? activateExistingWindow = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        Stopwatch elapsed = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                using EventWaitHandle signal = EventWaitHandle.OpenExisting(name);
                signal.Set();
                return;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                if (elapsed.Elapsed >= timeout)
                {
                    if (activateExistingWindow?.Invoke() == true)
                        return;
                    throw;
                }

                Thread.Sleep(50);
            }
        }
    }

    internal static bool TryActivateRunningWindow()
    {
        string executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("AgentWatcher executable path is unavailable.");
        string processName = Path.GetFileNameWithoutExtension(executablePath);
        foreach (Process process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId)
                    continue;

                IntPtr window = FindWindow(process.Id);
                if (window == IntPtr.Zero)
                    continue;

                ShowWindow(window, RestoreWindow);
                BringWindowToTop(window);
                SetForegroundWindow(window);
                if (IsWindowVisible(window))
                    return true;
            }
        }

        return false;
    }

    private static IntPtr FindWindow(int processId)
    {
        IntPtr result = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint ownerProcessId);
            if (ownerProcessId == processId && GetWindow(window, 4) == IntPtr.Zero)
            {
                result = window;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return result;
    }

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);
}
