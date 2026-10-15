using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.View;
using ArsanTupTakip.Mobile.Services;

namespace ArsanTupTakip.Mobile;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "arsantuptakip", DataHost = "server")]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        try
        {
            // Navy strip behind the clock/battery; ContentPage.SafeAreaEdges keeps UI below it.
            if (Window is not null)
            {
                Window.SetStatusBarColor(Android.Graphics.Color.ParseColor("#0B1324"));
                WindowCompat.GetInsetsController(Window, Window.DecorView)?.SetAppearanceLightStatusBars(false);
            }

            ApplyDeepLink(Intent);
        }
        catch (Exception exception)
        {
            CrashLog.Write(exception, "MainActivity");
        }
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        ApplyDeepLink(intent);
    }

    static void ApplyDeepLink(Intent? intent)
    {
        Android.Net.Uri? data = intent?.Data;
        if (data is null)
        {
            return;
        }

        if (Uri.TryCreate(data.ToString(), UriKind.Absolute, out Uri? uri))
        {
            ServerUrlCoordinator.TryApplyDeepLink(uri);
        }
    }
}
