using System.Text.Json;

namespace ArsanGazMobile.Services;

public sealed record PendingMove(string Code, int Status, int? CustomerId, string? Note, string Label, DateTime SavedAt);

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
        moves.Add(move);
        Save(moves);
    }

    // Returns how many movements were sent; stops at the first failure so order is preserved.
    public static async Task<(int Sent, string? Error)> FlushAsync()
    {
        if (!await Gate.WaitAsync(0))
        {
            return (0, null);
        }

        try
        {
            int sent = 0;
            List<PendingMove> moves = Load();
            while (moves.Count > 0)
            {
                PendingMove move = moves[0];
                string note = $"{move.Note} (telefonda çevrimdışı kaydedildi: {move.SavedAt:dd.MM.yyyy HH:mm})".Trim();
                try
                {
                    await ApiClient.Instance.MoveCylinderAsync(move.Code, move.Status, move.CustomerId, note);
                }
                catch (ServerUnreachableException)
                {
                    return (sent, null);
                }
                catch (ApiException exception) when (!IsPermanentRejection(exception.StatusCode))
                {
                    return (sent, $"{move.Code} – {move.Label}: {exception.Message} (kayıt kuyrukta bekliyor)");
                }
                catch (ApiException exception)
                {
                    // The ERP rejected this movement (e.g. unknown barcode); drop it so later ones are not blocked.
                    moves.RemoveAt(0);
                    Save(moves);
                    return (sent, $"{move.Code} – {move.Label}: {exception.Message}");
                }

                moves.RemoveAt(0);
                Save(moves);
                sent++;
            }

            return (sent, null);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static bool IsPermanentRejection(int? statusCode) =>
        statusCode is >= 400 and < 500 and not (401 or 403 or 408 or 429);

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
