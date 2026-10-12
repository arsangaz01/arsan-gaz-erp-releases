using System.Text.Json;

namespace ArsanTupTakip.Mobile.Services;

// RequestKey is the key of the first (unanswered) attempt; resending with it never records the movement twice.
// Codes is set for a bulk movement, which is resent as one bulk request.
public sealed record PendingMove(string Code, int Status, int? CustomerId, string? Note, string Label, DateTime SavedAt, string? Plate = null, bool? IsFull = null,
    string? RequestKey = null, List<string>? Codes = null);

// Cylinder movements recorded without a connection; they are sent in order once the computer is reachable.
public static class OfflineMoves
{
    private const string Key = "offline_moves";
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static event Action? Changed;

    public static IReadOnlyList<PendingMove> Pending => Load();

    public static void Add(PendingMove move)
    {
        List<PendingMove> moves = Load();
        moves.Add(move with { RequestKey = move.RequestKey ?? ApiClient.NewRequestKey() });
        Save(moves);
    }

    // Returns how many movements were sent; stops at the first failure so order is preserved.
    // Offline deliveries already happened physically, so a customer change is recorded and reported instead of blocked.
    public static async Task<(int Sent, string? Error, string? Warning)> FlushAsync()
    {
        if (!await Gate.WaitAsync(0))
        {
            return (0, null, null);
        }

        List<string> warnings = [];
        string? Warning() => warnings.Count == 0 ? null : string.Join("\n", warnings);
        try
        {
            int sent = 0;
            List<PendingMove> moves = Load();
            while (moves.Count > 0)
            {
                PendingMove move = moves[0];
                string note = $"{move.Note} (telefonda çevrimdışı kaydedildi: {move.SavedAt:dd.MM.yyyy HH:mm})".Trim();
                // Recording a cylinder that the system shows at another customer needs its own permission.
                bool confirm = ApiClient.Instance.Can(Perm.MoveOverride);
                try
                {
                    if (move.Codes is { Count: > 0 } codes)
                    {
                        CylinderBulkResponse bulk = await ApiClient.Instance.MoveCylindersAsync(codes, move.Status, move.CustomerId, note, move.Plate, move.IsFull,
                            confirmCustomerChange: confirm, requestKey: move.RequestKey);
                        warnings.AddRange(bulk.Results.Where(r => !r.Ok || r.Message.Contains('⚠')).Select(r => $"{r.Code} – {move.Label}: {r.Message}"));
                    }
                    else
                    {
                        string message = await ApiClient.Instance.MoveCylinderAsync(move.Code, move.Status, move.CustomerId, note, move.Plate, move.IsFull,
                            confirmCustomerChange: confirm, requestKey: move.RequestKey);
                        if (message.Contains('⚠'))
                        {
                            warnings.Add($"{move.Code} – {move.Label}: {message}");
                        }
                    }
                }
                catch (ServerUnreachableException)
                {
                    return (sent, null, Warning());
                }
                catch (ApiException exception) when (!IsPermanentRejection(exception))
                {
                    return (sent, $"{move.Code} – {move.Label}: {exception.Message} (kayıt kuyrukta bekliyor)", Warning());
                }
                catch (ApiException exception)
                {
                    // The server rejected this movement (e.g. unknown barcode); drop it so later ones are not blocked.
                    moves.RemoveAt(0);
                    Save(moves);
                    return (sent, $"{move.Code} – {move.Label}: {exception.Message}", Warning());
                }

                moves.RemoveAt(0);
                Save(moves);
                sent++;
            }

            return (sent, null, Warning());
        }
        finally
        {
            Gate.Release();
        }
    }

    // Kept in the queue: expired login (sent after the next login), forced password change, a parallel change by
    // another user (409 stale, retried) and rate limits. A missing permission or a refused movement is permanent.
    private static bool IsPermanentRejection(ApiException exception) =>
        exception.StatusCode is >= 400 and < 500 and not (401 or 408 or 429)
        && !exception.MustChangePassword && !exception.IsStale;

    private static List<PendingMove> Load()
    {
        string json = Preferences.Default.Get(Key, string.Empty);
        if (json.Length == 0)
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<PendingMove>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void Save(List<PendingMove> moves)
    {
        Preferences.Default.Set(Key, JsonSerializer.Serialize(moves));
        Changed?.Invoke();
    }
}
