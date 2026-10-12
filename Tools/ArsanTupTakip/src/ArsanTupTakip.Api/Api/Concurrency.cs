using System.Text;
using ArsanTupTakip.Database;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ArsanTupTakip.Api;

internal sealed record StoredResponse(int StatusCode, string ContentType, string Body);

internal enum ClaimResult { Claimed, Completed, InProgress }

// Keys of already processed write requests (Idempotency-Key header), per user. A phone that resends a scan after a
// lost answer gets the first answer back instead of a second movement.
internal interface IRequestKeyStore
{
    (ClaimResult Result, StoredResponse? Response) Claim(int userId, string key);
    void Complete(int userId, string key, StoredResponse response);
    void Release(int userId, string key);
}

internal sealed class MemoryRequestKeyStore : IRequestKeyStore
{
    private readonly Dictionary<(int, string), (DateTime At, StoredResponse? Response)> _keys = [];

    public (ClaimResult Result, StoredResponse? Response) Claim(int userId, string key)
    {
        lock (_keys)
        {
            DateTime now = DateTime.UtcNow;
            foreach (var old in _keys.Where(p => now - p.Value.At > IdempotencyMiddleware.KeyLifetime).Select(p => p.Key).ToList())
            {
                _keys.Remove(old);
            }

            if (_keys.TryGetValue((userId, key), out var existing))
            {
                return existing.Response is null ? (ClaimResult.InProgress, null) : (ClaimResult.Completed, existing.Response);
            }

            _keys[(userId, key)] = (now, null);
            return (ClaimResult.Claimed, null);
        }
    }

    public void Complete(int userId, string key, StoredResponse response)
    {
        lock (_keys)
        {
            _keys[(userId, key)] = (DateTime.UtcNow, response);
        }
    }

    public void Release(int userId, string key)
    {
        lock (_keys)
        {
            _keys.Remove((userId, key));
        }
    }
}

// Table tuptakip.request_keys; the primary key makes the claim atomic across concurrent requests.
internal sealed class PostgresRequestKeyStore : IRequestKeyStore
{
    private DateTime _lastCleanupUtc;

    public (ClaimResult Result, StoredResponse? Response) Claim(int userId, string key)
    {
        using NpgsqlConnection connection = new(TupTakipDatabase.ConnectionString);
        connection.Open();
        if (DateTime.UtcNow - _lastCleanupUtc > TimeSpan.FromHours(1))
        {
            _lastCleanupUtc = DateTime.UtcNow;
            using NpgsqlCommand cleanup = new("DELETE FROM request_keys WHERE created_at < now() - interval '2 days'", connection);
            cleanup.ExecuteNonQuery();
        }

        using (NpgsqlCommand insert = new("INSERT INTO request_keys (user_id, request_key) VALUES (@u, @k) ON CONFLICT DO NOTHING", connection))
        {
            insert.Parameters.AddWithValue("u", userId);
            insert.Parameters.AddWithValue("k", key);
            if (insert.ExecuteNonQuery() == 1)
            {
                return (ClaimResult.Claimed, null);
            }
        }

        using NpgsqlCommand select = new("SELECT status_code, content_type, response FROM request_keys WHERE user_id = @u AND request_key = @k", connection);
        select.Parameters.AddWithValue("u", userId);
        select.Parameters.AddWithValue("k", key);
        using NpgsqlDataReader reader = select.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(0))
        {
            return (ClaimResult.InProgress, null);
        }

        return (ClaimResult.Completed, new StoredResponse(reader.GetInt32(0), reader.IsDBNull(1) ? "application/json" : reader.GetString(1), reader.IsDBNull(2) ? string.Empty : reader.GetString(2)));
    }

    public void Complete(int userId, string key, StoredResponse response)
    {
        using NpgsqlConnection connection = new(TupTakipDatabase.ConnectionString);
        connection.Open();
        using NpgsqlCommand update = new("UPDATE request_keys SET status_code = @s, content_type = @c, response = @r WHERE user_id = @u AND request_key = @k", connection);
        update.Parameters.AddWithValue("s", response.StatusCode);
        update.Parameters.AddWithValue("c", response.ContentType);
        update.Parameters.AddWithValue("r", response.Body);
        update.Parameters.AddWithValue("u", userId);
        update.Parameters.AddWithValue("k", key);
        update.ExecuteNonQuery();
    }

    public void Release(int userId, string key)
    {
        using NpgsqlConnection connection = new(TupTakipDatabase.ConnectionString);
        connection.Open();
        using NpgsqlCommand delete = new("DELETE FROM request_keys WHERE user_id = @u AND request_key = @k AND status_code IS NULL", connection);
        delete.Parameters.AddWithValue("u", userId);
        delete.Parameters.AddWithValue("k", key);
        delete.ExecuteNonQuery();
    }
}

// For POST requests carrying an Idempotency-Key header (after authentication): the first request runs, a repeat with
// the same key gets the stored successful answer (header Idempotent-Replay: 1) and changes nothing. Failed requests
// release the key so a corrected retry can run.
internal sealed class IdempotencyMiddleware(RequestDelegate next)
{
    internal const string Header = "Idempotency-Key";
    internal static readonly TimeSpan KeyLifetime = TimeSpan.FromDays(2);
    internal static IRequestKeyStore Store { get; set; } = new PostgresRequestKeyStore();

    public async Task InvokeAsync(HttpContext context, WebPanelAuth auth)
    {
        string raw = context.Request.Headers[Header].ToString().Trim();
        WebPanelUser? user = context.Items["panelAccount"] as WebPanelUser
            ?? (context.Request.Path.StartsWithSegments("/panel/api") && auth.Current(context) is (_, WebPanelUser panelUser) ? panelUser : null);
        if (!HttpMethods.IsPost(context.Request.Method) || raw.Length == 0 || user is null)
        {
            await next(context);
            return;
        }

        if (raw.Length > 100)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = "Geçersiz istek anahtarı." });
            return;
        }

        string key = context.Request.Path.Value + "|" + raw;
        (ClaimResult result, StoredResponse? stored) = Store.Claim(user.Id, key);
        for (int wait = 0; result == ClaimResult.InProgress && wait < 40; wait++)
        {
            await Task.Delay(250);
            (result, stored) = Store.Claim(user.Id, key);
            if (result == ClaimResult.Claimed)
            {
                break;
            }
        }

        if (result == ClaimResult.InProgress)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await context.Response.WriteAsJsonAsync(new { error = "Aynı istek hâlâ işleniyor; birkaç saniye sonra listeyi yenileyin.", inProgress = true });
            return;
        }

        if (result == ClaimResult.Completed && stored is not null)
        {
            context.Response.StatusCode = stored.StatusCode;
            context.Response.ContentType = stored.ContentType;
            context.Response.Headers["Idempotent-Replay"] = "1";
            await context.Response.WriteAsync(stored.Body);
            return;
        }

        Stream original = context.Response.Body;
        using MemoryStream buffer = new();
        context.Response.Body = buffer;
        bool completed = false;
        try
        {
            await next(context);
            buffer.Position = 0;
            if (context.Response.StatusCode is >= 200 and < 300)
            {
                string body = Encoding.UTF8.GetString(buffer.ToArray());
                Store.Complete(user.Id, key, new StoredResponse(context.Response.StatusCode, context.Response.ContentType ?? "application/json", body));
                completed = true;
            }

            await buffer.CopyToAsync(original);
        }
        finally
        {
            context.Response.Body = original;
            if (!completed)
            {
                Store.Release(user.Id, key);
            }
        }
    }
}

// The saved row changed between reading and writing (another user or device); nothing was saved.
public sealed class RecordChangedException() : Exception(Text)
{
    public const string Text = "Bu kayıt başka bir kullanıcı tarafından değiştirildi, yenileyip tekrar deneyin.";
}

internal static class ConcurrencyErrors
{
    // A unique index violation here means another user wrote the same row at the same moment (e.g. both loaded one cylinder).
    internal static bool IsConflict(Exception exception) => exception is RecordChangedException or DbUpdateConcurrencyException
        || (exception is DbUpdateException { InnerException: Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation } });

    // Outermost middleware: a concurrency conflict anywhere becomes 409 with a clear message instead of a 500.
    internal static async Task Handle(HttpContext context, RequestDelegate next)
    {
        try
        {
            await next(context);
        }
        catch (Exception exception) when (IsConflict(exception) && !context.Response.HasStarted)
        {
            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await context.Response.WriteAsJsonAsync(new { error = RecordChangedException.Text, stale = true });
        }
    }
}
