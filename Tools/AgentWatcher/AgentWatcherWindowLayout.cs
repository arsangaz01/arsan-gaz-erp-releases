using System.Drawing;

namespace ArsanGazERP.AgentWatcher;

internal readonly record struct AgentWatcherWindowLayoutResult(Rectangle Bounds, Size MinimumSize);

internal static class AgentWatcherWindowLayout
{
    private const int EdgeMargin = 8;

    internal static AgentWatcherWindowLayoutResult Fit(
        Rectangle bounds,
        Rectangle workingArea,
        Size requestedMinimumSize)
    {
        int availableWidth = Math.Max(1, workingArea.Width - EdgeMargin * 2);
        int availableHeight = Math.Max(1, workingArea.Height - EdgeMargin * 2);
        Size minimumSize = new(
            Math.Min(Math.Max(1, requestedMinimumSize.Width), availableWidth),
            Math.Min(Math.Max(1, requestedMinimumSize.Height), availableHeight));
        int width = Math.Clamp(bounds.Width, minimumSize.Width, availableWidth);
        int height = Math.Clamp(bounds.Height, minimumSize.Height, availableHeight);
        int left = Math.Clamp(
            bounds.Left,
            workingArea.Left + EdgeMargin,
            workingArea.Right - EdgeMargin - width);
        int top = Math.Clamp(
            bounds.Top,
            workingArea.Top + EdgeMargin,
            workingArea.Bottom - EdgeMargin - height);

        return new AgentWatcherWindowLayoutResult(
            new Rectangle(left, top, width, height),
            minimumSize);
    }
}
