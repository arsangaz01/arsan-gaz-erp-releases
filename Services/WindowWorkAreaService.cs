using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ArsanGazERP.Services;

public static class WindowWorkAreaService
{
    private const double EdgeMargin = 8;
    private const uint MonitorDefaultToNearest = 2;

    // WindowChrome with no glass draws to the native window edges. The default
    // maximized resize-frame offset would therefore shift visible content too.
    // Use only for that custom frame, not for standard captioned windows.
    public static void UseBorderlessMaximizedWorkArea(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        HwndSource source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)
            ?? throw new InvalidOperationException("Pencere kaynağı henüz oluşturulmadı.");
        source.AddHook((IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            const int WmGetMinMaxInfo = 0x0024;
            if (message != WmGetMinMaxInfo)
            {
                return IntPtr.Zero;
            }

            IntPtr monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            MonitorInfo info = new() { Size = Marshal.SizeOf<MonitorInfo>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
            {
                return IntPtr.Zero;
            }

            MinMaxInfo limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            limits.MaxPosition.X = info.Work.Left - info.Monitor.Left;
            limits.MaxPosition.Y = info.Work.Top - info.Monitor.Top;
            limits.MaxSize.X = info.Work.Right - info.Work.Left;
            limits.MaxSize.Y = info.Work.Bottom - info.Work.Top;
            // Preserve the system/WPF tracking limits and normal restore bounds.
            Marshal.StructureToPtr(limits, lParam, false);
            handled = true;
            return IntPtr.Zero;
        });
    }

    public static void Fit(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        double requestedMaxWidth = window.MaxWidth;
        double requestedMaxHeight = window.MaxHeight;
        double requestedMinWidth = window.MinWidth;
        double requestedMinHeight = window.MinHeight;
        bool adjustingPosition = false;

        void KeepInsideWorkArea()
        {
            if (adjustingPosition || !window.IsLoaded || window.WindowState != WindowState.Normal)
            {
                return;
            }

            // Native maximize can precede WPF's WindowState update during
            // LocationChanged. Do not move an already-maximized native window.
            IntPtr handle = new WindowInteropHelper(window).Handle;
            if (handle != IntPtr.Zero && IsZoomed(handle))
            {
                return;
            }

            Rect currentWorkArea = GetCurrentMonitorWorkArea(window);
            double edgeMargin = EdgeMargin;
            double availableWidth = Math.Max(1, currentWorkArea.Width - edgeMargin * 2);
            double availableHeight = Math.Max(1, currentWorkArea.Height - edgeMargin * 2);
            double maxWidth = Math.Min(requestedMaxWidth, availableWidth);
            double maxHeight = Math.Min(requestedMaxHeight, availableHeight);

            adjustingPosition = true;
            try
            {
                // Fit the normal bounds without imposing native maximize tracking
                // limits. A captioned window's maximized outer frame extends beyond
                // the work area; a work-area MaxWidth/MaxHeight clips that frame.
                window.MaxWidth = requestedMaxWidth;
                window.MaxHeight = requestedMaxHeight;
                window.MinWidth = Math.Min(requestedMinWidth, maxWidth);
                window.MinHeight = Math.Min(requestedMinHeight, maxHeight);

                if (double.IsFinite(window.Width) || window.ActualWidth > maxWidth)
                {
                    window.Width = Math.Min(double.IsFinite(window.Width) ? window.Width : window.ActualWidth, maxWidth);
                }

                if (double.IsFinite(window.Height) || window.ActualHeight > maxHeight)
                {
                    window.Height = Math.Min(double.IsFinite(window.Height) ? window.Height : window.ActualHeight, maxHeight);
                }
            }
            finally
            {
                adjustingPosition = false;
            }

            double horizontalMargin = Math.Min(EdgeMargin, currentWorkArea.Width / 2);
            double verticalMargin = Math.Min(EdgeMargin, currentWorkArea.Height / 2);
            double minLeft = currentWorkArea.Left + horizontalMargin;
            double minTop = currentWorkArea.Top + verticalMargin;
            double maxLeft = Math.Max(minLeft, currentWorkArea.Right - horizontalMargin - window.ActualWidth);
            double maxTop = Math.Max(minTop, currentWorkArea.Bottom - verticalMargin - window.ActualHeight);
            double currentLeft = double.IsFinite(window.Left) ? window.Left : currentWorkArea.Left;
            double currentTop = double.IsFinite(window.Top) ? window.Top : currentWorkArea.Top;
            double left = Math.Clamp(currentLeft, minLeft, maxLeft);
            double top = Math.Clamp(currentTop, minTop, maxTop);
            if (Math.Abs(window.Left - left) < 0.5 && Math.Abs(window.Top - top) < 0.5)
            {
                return;
            }

            adjustingPosition = true;
            try
            {
                window.Left = left;
                window.Top = top;
            }
            finally
            {
                adjustingPosition = false;
            }
        }

        window.Loaded += (_, _) => KeepInsideWorkArea();
        window.LocationChanged += (_, _) => KeepInsideWorkArea();
        window.SizeChanged += (_, _) => KeepInsideWorkArea();
    }

    private static Rect GetCurrentMonitorWorkArea(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        IntPtr monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        MonitorInfo info = new() { Size = Marshal.SizeOf<MonitorInfo>() };
        HwndSource? source = PresentationSource.FromVisual(window) as HwndSource;

        if (monitor == IntPtr.Zero ||
            !GetMonitorInfo(monitor, ref info) ||
            source?.CompositionTarget is null)
        {
            return SystemParameters.WorkArea;
        }

        Matrix fromDevice = source.CompositionTarget.TransformFromDevice;
        Point topLeft = fromDevice.Transform(new Point(info.Work.Left, info.Work.Top));
        Point bottomRight = fromDevice.Transform(new Point(info.Work.Right, info.Work.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
