using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using ArsanGazMobile.Services;

namespace ArsanGazMobile;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
[IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable }, DataScheme = "arsangazerp", DataHost = "server")]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        try
        {
            ApplyDeepLink(Intent);
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu
                && CheckSelfPermission(Android.Manifest.Permission.PostNotifications) != Permission.Granted)
            {
                RequestPermissions([Android.Manifest.Permission.PostNotifications], 4713);
            }

            _ = SafeStartupAsync();
        }
        catch (Exception exception)
        {
            CrashLog.Write(exception, "MainActivity");
        }
    }

    private async Task SafeStartupAsync()
    {
        try
        {
            FollowUpReminder.Schedule(this);
        }
        catch (Exception exception)
        {
            CrashLog.Write(exception, "Takip zamanlayıcı");
        }

        try
        {
            await FollowUpReminder.CheckAsync(ApplicationContext!, onceToday: true);
        }
        catch (Exception exception)
        {
            CrashLog.Write(exception, "Takip hatırlatma");
        }
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        ApplyDeepLink(intent);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        SpeechInput.OnActivityResult(requestCode, resultCode, data);
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
