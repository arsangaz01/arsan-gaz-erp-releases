using ArsanGazMobile.Pages;
using ArsanGazMobile.Services;

namespace ArsanGazMobile;

// A single home screen with section tiles; each tile pushes its page, the back button returns home.
public partial class AppShell : Shell
{
    public AppShell(UserSession session)
    {
        InitializeComponent();
        HomePage home = new(session);
        ShellContent content = new() { Title = "Ana menü", Content = home };
        Shell.SetTabBarIsVisible(home, false);
        Items.Add(content);
        CurrentItem = content;
    }
}
