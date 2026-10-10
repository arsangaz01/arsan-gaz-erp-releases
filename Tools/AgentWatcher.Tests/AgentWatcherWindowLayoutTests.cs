using System.Drawing;
using ArsanGazERP.AgentWatcher;

namespace ArsanGazERP.AgentWatcher.Tests;

internal static class AgentWatcherWindowLayoutTests
{
    internal static void Run()
    {
        FitsInitialWatcherWindowAboveTaskbar();
        KeepsWindowInsideNegativeOriginMonitor();
        ReducesMinimumSizeWhenWorkAreaIsSmall();
    }

    private static void FitsInitialWatcherWindowAboveTaskbar()
    {
        Rectangle workArea = new(0, 0, 1366, 728);
        AgentWatcherWindowLayoutResult result = AgentWatcherWindowLayout.Fit(
            new Rectangle(63, -46, 1240, 820),
            workArea,
            new Size(1050, 700));

        Assert(result.Bounds == new Rectangle(63, 8, 1240, 712),
            "The initial window must be resized into the work area without covering the taskbar.");
        Assert(result.MinimumSize == new Size(1050, 700),
            "The requested minimum size should be preserved when the work area permits it.");
    }

    private static void KeepsWindowInsideNegativeOriginMonitor()
    {
        Rectangle workArea = new(-1920, 0, 1920, 1040);
        AgentWatcherWindowLayoutResult result = AgentWatcherWindowLayout.Fit(
            new Rectangle(400, 350, 1240, 820),
            workArea,
            new Size(1050, 700));

        Assert(result.Bounds == new Rectangle(-1248, 212, 1240, 820),
            "A window moved to a monitor with a negative origin must be brought into that monitor's work area.");
    }

    private static void ReducesMinimumSizeWhenWorkAreaIsSmall()
    {
        Rectangle workArea = new(0, 0, 800, 500);
        AgentWatcherWindowLayoutResult result = AgentWatcherWindowLayout.Fit(
            new Rectangle(-100, -100, 1240, 820),
            workArea,
            new Size(1050, 700));

        Assert(result.MinimumSize == new Size(784, 484),
            "Minimum dimensions must not prevent the form from fitting on a small display.");
        Assert(result.Bounds == new Rectangle(8, 8, 784, 484),
            "The form must remain entirely within a small display work area.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
