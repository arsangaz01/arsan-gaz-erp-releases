using Android.App;
using Android.Content;
using Android.Content.PM;

namespace ArsanGazMobile.Services;

public static class WhatsAppShare
{
    private const string Authority = "com.arsangaz.erpmobile.updates";
    public const string Personal = "com.whatsapp";
    public const string Business = "com.whatsapp.w4b";

    public static IReadOnlyList<string> InstalledPackages()
    {
        PackageManager? manager = Platform.CurrentActivity?.PackageManager ?? Platform.AppContext.PackageManager;
        List<string> installed = [];
        foreach (string package in new[] { Business, Personal })
        {
            try
            {
                manager?.GetPackageInfo(package, PackageInfoFlags.Activities);
                installed.Add(package);
            }
            catch (PackageManager.NameNotFoundException)
            {
            }
        }

        return installed;
    }

    /// <summary>Copies a picked PDF into the shareable cache folder and returns its path.</summary>
    public static async Task<string> StagePdfAsync(FileResult file)
    {
        string directory = Path.Combine(FileSystem.CacheDirectory, "campaign");
        Directory.CreateDirectory(directory);
        string name = string.Concat(Path.GetFileNameWithoutExtension(file.FileName)
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        string target = Path.Combine(directory, (name.Length == 0 ? "tanitim" : name) + ".pdf");
        await using Stream source = await file.OpenReadAsync();
        await using FileStream destination = File.Create(target);
        await source.CopyToAsync(destination);
        return target;
    }

    /// <summary>Opens the given number's chat with the text (and PDF) ready; the user presses send.</summary>
    public static void Open(string package, string phoneDigits, string text, string? pdfPath)
    {
        Activity activity = Platform.CurrentActivity ?? throw new InvalidOperationException("Uygulama penceresi hazır değil.");
        if (pdfPath is null || !File.Exists(pdfPath))
        {
            Intent view = new(Intent.ActionView, Android.Net.Uri.Parse(
                $"https://api.whatsapp.com/send?phone={phoneDigits}&text={Uri.EscapeDataString(text)}"));
            view.SetPackage(package);
            view.AddFlags(ActivityFlags.NewTask);
            activity.StartActivity(view);
            return;
        }

        Android.Net.Uri uri = AndroidX.Core.Content.FileProvider.GetUriForFile(activity, Authority, new Java.IO.File(pdfPath))!;
        Intent send = new(Intent.ActionSend);
        send.SetType("application/pdf");
        send.PutExtra(Intent.ExtraStream, uri);
        send.PutExtra(Intent.ExtraText, text);
        send.PutExtra("jid", phoneDigits + "@s.whatsapp.net");
        send.SetPackage(package);
        send.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);
        activity.StartActivity(send);
    }
}
