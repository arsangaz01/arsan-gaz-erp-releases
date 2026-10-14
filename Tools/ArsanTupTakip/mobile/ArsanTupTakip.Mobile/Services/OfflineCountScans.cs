using System.Text.Json;

namespace ArsanTupTakip.Mobile.Services;

public sealed record PendingCountScan(int CountId, string Code, DateTime ScannedAt);

// Depot count scans read without a connection; "Senkronize et" (or the next successful scan) sends them in order.
// The server ignores a cylinder that is already in the count, so sending one twice is harmless.
public static class OfflineCountScans
{
    private const string Key = "offline_count_scans";
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static IReadOnlyList<PendingCountScan> Pending => Load();

    public static int PendingFor(int countId) => Load().Count(x => x.CountId == countId);

    public static void Add(int countId, string code)
    {
        List<PendingCountScan> scans = Load();
        if (!scans.Any(x => x.CountId == countId && string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase)))
        {
            scans.Add(new PendingCountScan(countId, code, DateTime.Now));
            Save(scans);
        }
    }

    // Returns how many scans reached the server, the last count state and the codes the server refused
    // (e.g. the count was finished meanwhile); stops while the server is unreachable.
    public static async Task<(int Sent, StockCountInfo? Last, List<string> Rejected)> FlushAsync()
    {
        List<string> rejected = [];
        if (!await Gate.WaitAsync(0))
        {
            return (0, null, rejected);
        }

        try
        {
            int sent = 0;
            StockCountInfo? last = null;
            List<PendingCountScan> scans = Load();
            while (scans.Count > 0)
            {
                PendingCountScan scan = scans[0];
                try
                {
                    last = (await ApiClient.Instance.ScanStockCountAsync(scan.CountId, scan.Code, scan.ScannedAt)).Count;
                    sent++;
                }
                catch (ServerUnreachableException)
                {
                    return (sent, last, rejected);
                }
                catch (ApiException exception) when (exception.StatusCode is 401 or 408 or 429 || exception.MustChangePassword)
                {
                    return (sent, last, rejected);
                }
                catch (ApiException exception)
                {
                    rejected.Add($"{scan.Code}: {exception.Message}");
                }

                scans.RemoveAt(0);
                Save(scans);
            }

            return (sent, last, rejected);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static List<PendingCountScan> Load()
    {
        string json = Preferences.Default.Get(Key, string.Empty);
        if (json.Length == 0)
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<PendingCountScan>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void Save(List<PendingCountScan> scans) => Preferences.Default.Set(Key, JsonSerializer.Serialize(scans));
}
