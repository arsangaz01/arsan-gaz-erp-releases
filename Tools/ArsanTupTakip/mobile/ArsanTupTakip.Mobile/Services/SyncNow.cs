namespace ArsanTupTakip.Mobile.Services;

// "Senkronize et": checks the server and the login, then sends the movements kept on the phone. The web panel reads the
// same server database, so whatever reaches the server is visible on the web too. The update check is a separate button:
// an update must never start while records are still being sent.
public static class SyncNow
{
    private const string LastSyncKey = "last_sync_utc";
    private static bool _running;

    public static bool IsRunning => _running;

    public static int PendingCount => OfflineTripOps.Pending.Count + OfflineMoves.Pending.Count + OfflineCountScans.Pending.Count;

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
        int trips = OfflineTripOps.Pending.Count;
        int pending = OfflineMoves.Pending.Count;
        int scans = OfflineCountScans.Pending.Count;
        string last = LastSyncUtc is DateTime at ? "Son: " + at.ToLocalTime().ToString("dd.MM HH:mm") : "Henüz senkronize edilmedi";
        List<string> waiting = [];
        if (trips > 0)
        {
            waiting.Add($"{trips} sefer kaydı");
        }

        if (pending > 0)
        {
            waiting.Add($"{pending} hareket");
        }

        if (scans > 0)
        {
            waiting.Add($"{scans} sayım okutması");
        }

        return waiting.Count > 0
            ? $"Telefonda bekleyen {string.Join(", ", waiting)} var · {last}"
            : $"Bekleyen kayıt yok · {last}";
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
            int before = PendingCount;
            try
            {
                await ApiClient.Instance.RefreshAccountAsync();
            }
            catch (ServerUnreachableException)
            {
                await page.DisplayAlertAsync("Senkronizasyon",
                    "Sunucuya ulaşılamadı. " + (before > 0 ? $"{before} kayıt telefonda güvende bekliyor; bağlantı gelince tekrar deneyin." : "İnternet bağlantısını kontrol edin."), "Tamam");
                return;
            }
            catch (ApiException exception)
            {
                await page.DisplayAlertAsync("Senkronizasyon", exception.Message
                    + (before > 0 ? $"\n\n{before} kayıt telefonda bekliyor; tekrar giriş yaptıktan sonra gönderilir." : string.Empty), "Tamam");
                return;
            }

            (int tripsSent, List<string> tripResults, List<string> tripsRejected) = await OfflineTripOps.FlushAsync();
            int tripsLeft = OfflineTripOps.Pending.Count;
            (int sent, string? error, string? warning) = await OfflineMoves.FlushAsync();
            int left = OfflineMoves.Pending.Count;
            (int scansSent, _, List<string> scansRejected) = await OfflineCountScans.FlushAsync();
            int scansLeft = OfflineCountScans.Pending.Count;
            if (error is null && left == 0 && scansLeft == 0 && tripsLeft == 0)
            {
                Preferences.Default.Set(LastSyncKey, DateTime.UtcNow.Ticks);
            }

            if (tripsSent + tripsLeft + tripsRejected.Count > 0)
            {
                string tripText = $"Sefer kayıtları: {tripsSent} gönderildi"
                    + (tripsLeft > 0 ? $", {tripsLeft} bağlantı bekliyor" : string.Empty) + "."
                    + (tripResults.Count > 0 ? "\n\n" + string.Join("\n", tripResults.Take(10)) : string.Empty)
                    + (tripsRejected.Count > 0 ? "\n\nKabul edilmeyenler:\n" + string.Join("\n", tripsRejected.Take(10)) : string.Empty);
                await page.DisplayAlertAsync("Senkronizasyon", tripText, "Tamam");
            }

            if (scansSent + scansLeft + scansRejected.Count > 0)
            {
                string scanText = $"Depo sayımı: {scansSent} okutma sunucuya gönderildi"
                    + (scansLeft > 0 ? $", {scansLeft} okutma bağlantı bekliyor" : string.Empty) + "."
                    + (scansRejected.Count > 0 ? "\n\nKabul edilmeyenler:\n" + string.Join("\n", scansRejected.Take(10)) : string.Empty);
                await page.DisplayAlertAsync("Senkronizasyon", scanText, "Tamam");
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
