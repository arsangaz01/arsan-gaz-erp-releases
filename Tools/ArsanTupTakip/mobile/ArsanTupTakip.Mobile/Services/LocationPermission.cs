namespace ArsanTupTakip.Mobile.Services;

// Users of the trip plan (approach warning, saving a customer's location) are asked for location permission every
// time the app is opened until they grant it. Coming back within a few minutes (e.g. from the phone's settings)
// does not ask again.
public static class LocationPermission
{
    private static readonly TimeSpan AskInterval = TimeSpan.FromMinutes(15);
    private static DateTime _askedUtc = DateTime.MinValue;
    private static bool _asking;

    // Perm.CustomerLocation is only granted while the trip plan feature is on.
    public static bool Needed => ApiClient.Instance.Can(Perm.CustomerLocation);

    public static async Task EnsureAsync(Page page)
    {
        if (_asking || !Needed || DateTime.UtcNow - _askedUtc < AskInterval)
        {
            return;
        }

        _asking = true;
        try
        {
            if (await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>() == PermissionStatus.Granted)
            {
                await AskNotificationsAsync();
                return;
            }

            _askedUtc = DateTime.UtcNow;
            if (await Permissions.RequestAsync<Permissions.LocationWhenInUse>() == PermissionStatus.Granted)
            {
                await AskNotificationsAsync(force: true);
                return;
            }

            // After "Bir daha sorma" (or two refusals) Android no longer shows its dialog; only the settings can grant it.
            bool blocked = !Permissions.ShouldShowRationale<Permissions.LocationWhenInUse>();
            string text = "Sefer planında müşteriye yaklaşınca uyarı ve müşterinin konumunu kaydetmek için konum izni gerekiyor. "
                + "Konumunuz sunucuya gönderilmez. İzin verilene kadar uygulama her açılışta sorar.";
            if (blocked)
            {
                if (await page.DisplayAlertAsync("Konum izni gerekli",
                        text + "\n\nAyarlar'da İzinler > Konum > \"Uygulama kullanılırken izin ver\"i seçin.", "Ayarları aç", "Sonra"))
                {
                    AppInfo.Current.ShowSettingsUI();
                }
            }
            else
            {
                await page.DisplayAlertAsync("Konum izni gerekli", text, "Tamam");
            }
        }
        catch (Exception exception)
        {
            CrashLog.Write(exception, "LocationPermission.Ensure");
        }
        finally
        {
            _asking = false;
        }
    }

    // The approach warning's notification; Android 13+ asks for it separately.
    private static async Task AskNotificationsAsync(bool force = false)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(33)
            && await Permissions.CheckStatusAsync<Permissions.PostNotifications>() != PermissionStatus.Granted
            && (force || DateTime.UtcNow - _askedUtc >= AskInterval))
        {
            _askedUtc = DateTime.UtcNow;
            await Permissions.RequestAsync<Permissions.PostNotifications>();
        }
    }
}
