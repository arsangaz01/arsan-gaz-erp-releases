namespace ArsanGazMobile.Services;

public static class ServerUrlCoordinator
{
    private const string LastRefreshKey = "server_url_last_refresh_utc";
    private const string AutoManagedKey = "server_url_auto_managed";

    public static bool AutoManaged
    {
        get => Preferences.Default.Get(AutoManagedKey, true);
        set => Preferences.Default.Set(AutoManagedKey, value);
    }

    public static async Task BootstrapAsync()
    {
        if (ApiClient.Instance.ServerUrl.Length == 0)
        {
            await ApiClient.Instance.TryApplyTelegramClipboardUrlAsync();
        }

        if (await ApiClient.Instance.IsSignedInAsync())
        {
            await RefreshIfDueAsync(force: true);
        }
    }

    public static async Task RefreshIfDueAsync(bool force = false)
    {
        if (!AutoManaged || !await ApiClient.Instance.IsSignedInAsync())
        {
            return;
        }

        if (ApiClient.Instance.ServerUrl.Length == 0)
        {
            force = true;
        }

        if (!force)
        {
            string last = Preferences.Default.Get(LastRefreshKey, string.Empty);
            if (DateTime.TryParse(last, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime when)
                && DateTime.UtcNow - when < TimeSpan.FromMinutes(3))
            {
                return;
            }
        }

        try
        {
            await ApiClient.Instance.TryRefreshServerUrlFromPcAsync();
            Preferences.Default.Set(LastRefreshKey, DateTime.UtcNow.ToString("o"));
        }
        catch (ApiException)
        {
        }
    }

    private static int _recovering;

    public static async Task TryRecoverAfterFailureAsync()
    {
        // The refresh below goes through ApiClient.SendAsync, whose failure path calls back into this method.
        if (Interlocked.Exchange(ref _recovering, 1) == 1)
        {
            return;
        }

        try
        {
            if (await ApiClient.Instance.TryApplyTelegramClipboardUrlAsync())
            {
                return;
            }

            if (await ApiClient.Instance.IsSignedInAsync())
            {
                await RefreshIfDueAsync(force: true);
            }
        }
        finally
        {
            Volatile.Write(ref _recovering, 0);
        }
    }

    public static bool TryApplyDeepLink(Uri? uri)
    {
        if (uri is null)
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, "arsangazerp", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, "server", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string? encoded = null;
        foreach (string part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith("u=", StringComparison.OrdinalIgnoreCase))
            {
                encoded = part[2..];
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(encoded))
        {
            return false;
        }

        ApiClient.Instance.ServerUrl = Uri.UnescapeDataString(encoded);
        AutoManaged = true;
        return true;
    }
}
