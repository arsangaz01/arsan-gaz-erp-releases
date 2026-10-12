using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
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
