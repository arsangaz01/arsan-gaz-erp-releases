namespace ArsanTupTakip.Mobile.Services;

// "Senkronize et": checks the server and the login, then sends the movements kept on the phone. The web panel reads the
// same server database, so whatever reaches the server is visible on the web too.
public static class SyncNow
{
    private const string LastSyncKey = "last_sync_utc";
    private static bool _running;

    public static DateTime? LastSyncUtc
    {
        get
        {
            long ticks = Preferences.Default.Get(LastSyncKey, 0L);
            return ticks > 0 ? new DateTime(ticks, DateTimeKind.Utc) : null;
        }
    }

    public static string StatusText()
    {
        int pending = OfflineMoves.Pending.Count;
        string last = LastSyncUtc is DateTime at ? "Son: " + at.ToLocalTime().ToString("dd.MM HH:mm") : "Henüz senkronize edilmedi";
        return pending > 0
            ? $"Telefonda bekleyen {pending} hareket var · {last}"
            : $"Bekleyen hareket yok · {last}";
    }

    public static async Task RunAsync(Page page)
    {
        if (_running)
        {
            return;
        }

        _running = true;
        try
        {
            int before = OfflineMoves.Pending.Count;
            try
            {
                await ApiClient.Instance.RefreshAccountAsync();
            }
            catch (ServerUnreachableException)
            {
                await page.DisplayAlertAsync("Senkronizasyon",
                    "Sunucuya ulaşılamadı. " + (before > 0 ? $"{before} hareket telefonda güvende bekliyor; bağlantı gelince tekrar deneyin." : "İnternet bağlantısını kontrol edin."), "Tamam");
                return;
            }
            catch (ApiException exception)
            {
                await page.DisplayAlertAsync("Senkronizasyon", exception.Message
                    + (before > 0 ? $"\n\n{before} hareket telefonda bekliyor; tekrar giriş yaptıktan sonra gönderilir." : string.Empty), "Tamam");
                return;
            }

            (int sent, string? error, string? warning) = await OfflineMoves.FlushAsync();
            int left = OfflineMoves.Pending.Count;
            if (error is null && left == 0)
            {
                Preferences.Default.Set(LastSyncKey, DateTime.UtcNow.Ticks);
            }

            if (warning is not null)
            {
                await page.DisplayAlertAsync("Gönderilen hareketlerde uyarı",
                    "Bağlantısızken kaydedilen hareketlerin bazılarında uyarı var (başka müşteride görünen veya kaydedilemeyen tüpler); kontrol edin:\n\n" + warning, "Tamam");
            }

            string summary = error is not null
                ? $"{sent} hareket gönderildi.\n\nSorun: {error}"
                : left > 0
                    ? $"{sent} hareket gönderildi; {left} hareket bağlantı bekliyor. Biraz sonra tekrar deneyin."
                    : sent > 0
                        ? $"{sent} hareket sunucuya gönderildi. Web panelde de görünür."
                        : "Telefonda bekleyen hareket yok; tüm kayıtlar sunucuda ve web panelde.";
            await page.DisplayAlertAsync("Senkronizasyon", summary, "Tamam");
        }
        catch (Exception exception)
        {
            CrashLog.Write(exception, "Senkronizasyon");
            await page.DisplayAlertAsync("Senkronizasyon", "Senkronizasyon tamamlanamadı: " + exception.Message, "Tamam");
        }
        finally
        {
            _running = false;
        }
    }
}
