using System.Text.Json;

namespace ArsanTupTakip.Mobile.Services;

// Kind: OfflineTripOps.Load, Deliver or Pickup. RequestKey is the key of the first attempt, so a resend that the
// server already processed is answered from its stored reply and never recorded twice.
public sealed record PendingTripOp(string Kind, int TripId, string? Plate, int? CustomerId, string? CustomerName, List<string> Codes,
    DateTime SavedAt, string RequestKey);

// Trip loads, deliveries and empty pickups saved on the phone while the server was unreachable (no signal at the
// customer). They are sent in the order they were made: a delivery needs its load to be on the server first, so a
// new trip operation is queued behind waiting ones instead of being sent ahead of them.
public static class OfflineTripOps
{
    public const string Load = "yukle";
    public const string Deliver = "teslim";
    public const string Pickup = "bos-alim";

    private const string Key = "offline_trip_ops";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _watching;

    public static event Action? Changed;

    public static IReadOnlyList<PendingTripOp> Pending => Read();

    public static int PendingFor(int tripId) => Read().Count(x => x.TripId == tripId);

    public static string Label(PendingTripOp op) => op.Kind switch
    {
        Load => $"{op.Plate} aracına yükleme · {op.Codes.Count} tüp",
        Deliver => $"{op.CustomerName} teslim · {op.Codes.Count} tüp",
        _ => $"{op.CustomerName} boş alım · {op.Codes.Count} tüp"
    };

    public static void Add(PendingTripOp op)
    {
        List<PendingTripOp> ops = Read();
        ops.Add(op);
        Write(ops);
    }

    // Sends waiting operations as soon as the phone is back online, from whichever page is open.
    public static void Watch()
    {
        if (_watching)
        {
            return;
        }

        _watching = true;
        Connectivity.Current.ConnectivityChanged += async (_, e) =>
        {
            if (e.NetworkAccess == NetworkAccess.Internet && Read().Count > 0 && ApiClient.Instance.Account is not null)
            {
                await FlushAsync();
            }
        };
    }

    // Stops at the first operation that cannot be sent yet so the order is kept. Rejected: operations the server
    // refused for good (e.g. the trip was closed meanwhile); they are dropped and reported to the user.
    public static async Task<(int Sent, List<string> Results, List<string> Rejected)> FlushAsync()
    {
        List<string> results = [];
        List<string> rejected = [];
        if (!await Gate.WaitAsync(0))
        {
            return (0, results, rejected);
        }

        try
        {
            int sent = 0;
            List<PendingTripOp> ops = Read();
            while (ops.Count > 0)
            {
                PendingTripOp op = ops[0];
                try
                {
                    string message = op.Kind switch
                    {
                        Load => (await ApiClient.Instance.LoadTripCylindersAsync(op.TripId, op.Plate!, op.Codes, op.SavedAt, op.RequestKey)).Message,
                        Deliver => await ApiClient.Instance.DeliverTripCylindersAsync(op.TripId, op.CustomerId!.Value, op.Codes, null, op.SavedAt, op.RequestKey),
                        _ => await ApiClient.Instance.PickupTripCylindersAsync(op.TripId, op.Plate!, op.CustomerId!.Value, op.Codes, null, op.SavedAt, op.RequestKey)
                    };
                    results.Add($"{Label(op)}: {message.Split('\n')[0]}");
                }
                catch (ServerUnreachableException)
                {
                    return (sent, results, rejected);
                }
                catch (ApiException exception) when (!IsPermanentRejection(exception))
                {
                    return (sent, results, rejected);
                }
                catch (ApiException exception)
                {
                    rejected.Add($"{Label(op)} ({op.SavedAt:dd.MM HH:mm}): {exception.Message}");
                }

                ops.RemoveAt(0);
                Write(ops);
                sent++;
            }

            return (sent, results, rejected);
        }
        finally
        {
            Gate.Release();
        }
    }

    // Same rule as OfflineMoves: an expired login, a forced password change, a parallel change and rate limits are
    // retried later; any other 4xx answer means the server will never accept this operation.
    private static bool IsPermanentRejection(ApiException exception) =>
        exception.StatusCode is >= 400 and < 500 and not (401 or 408 or 429)
        && !exception.MustChangePassword && !exception.IsStale;

    private static List<PendingTripOp> Read()
    {
        string json = Preferences.Default.Get(Key, string.Empty);
        if (json.Length == 0)
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<PendingTripOp>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void Write(List<PendingTripOp> ops)
    {
        Preferences.Default.Set(Key, JsonSerializer.Serialize(ops));
        Changed?.Invoke();
    }
}
