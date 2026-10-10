using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ArsanGazERP.Services;

public static class WindowThemeService
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmUseImmersiveDarkModeBefore20H1 = 19;
    private static bool registered;

    public static void Register()
    {
        if (registered)
            return;
        registered = true;
        // Implicit styles only match the exact Window type, never derived windows such as CampaignWindow.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded));
    }

    public static void Apply(Window window)
    {
        if (window.Style is null && Application.Current?.TryFindResource(typeof(Window)) is Style style)
            window.Style = style;
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return;
        int enabled = 1;
        if (DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
            _ = DwmSetWindowAttribute(handle, DwmUseImmersiveDarkModeBefore20H1, ref enabled, sizeof(int));
    }

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window && ReferenceEquals(e.OriginalSource, window))
            Apply(window);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
