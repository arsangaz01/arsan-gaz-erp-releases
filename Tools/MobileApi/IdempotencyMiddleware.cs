using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace ArsanGazERP.MobileApi;

// A POST to /api or /panel/api carrying an Idempotency-Key header runs once per user / panel session: a retry with the same
// key (flaky mobile network, offline queue, double tap) gets the first answer back instead of saving the movement again.
// A duplicate that arrives while the first one is still running waits for it. Server errors are not remembered.
internal sealed class IdempotencyMiddleware(RequestDelegate next)
{
    internal const string Header = "Idempotency-Key";
    internal const string ReplayHeader = "Idempotent-Replay";
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
    private const int MaxEntries = 20_000;

    private sealed record Saved(int Status, string? ContentType, byte[] Body);

    private sealed class Entry
    {
        public readonly TaskCompletionSource<Saved?> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly DateTime CreatedUtc = DateTime.UtcNow;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    public async Task InvokeAsync(HttpContext context)
    {
        string key = context.Request.Headers[Header].ToString();
        // Phones are scoped to the user (set by DeviceAuthMiddleware), so a queued scan resent after signing in again is
        // still recognised; panel requests are authenticated later, by the endpoint filter, and use the session cookie.
        string scope = context.Items["panelAccount"] is WebPanelUser user
            ? "user:" + user.Id
            : context.Request.Cookies[WebPanelAuth.CookieName] ?? string.Empty;

        if (!HttpMethods.IsPost(context.Request.Method) || key.Length is 0 or > 100 || scope.Length == 0
            || !(context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/panel/api")))
        {
            await next(context);
            return;
        }

        string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope + "\n" + context.Request.Path + "\n" + key)));
        Entry fresh = new();
        Entry entry = _entries.GetOrAdd(id, fresh);
        if (!ReferenceEquals(entry, fresh))
        {
            Saved? saved;
            try
            {
                saved = await entry.Done.Task.WaitAsync(TimeSpan.FromSeconds(120), context.RequestAborted);
            }
            catch (TimeoutException)
            {
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                await context.Response.WriteAsJsonAsync(new { error = "Aynı istek hâlâ işleniyor; birazdan tekrar deneyin." });
                return;
            }

            if (saved is null)
            {
                await next(context);
                return;
            }

            context.Response.StatusCode = saved.Status;
            context.Response.ContentType = saved.ContentType;
            context.Response.Headers[ReplayHeader] = "true";
            await context.Response.Body.WriteAsync(saved.Body, context.RequestAborted);
            return;
        }

        Purge();
        Stream original = context.Response.Body;
        using MemoryStream buffer = new();
        context.Response.Body = buffer;
        try
        {
            await next(context);
        }
        catch
        {
            _entries.TryRemove(id, out _);
            entry.Done.TrySetResult(null);
            throw;
        }
        finally
        {
            context.Response.Body = original;
        }

        byte[] body = buffer.ToArray();
        int status = context.Response.StatusCode;
        if (status >= 500 || status is 401 or 429)
        {
            _entries.TryRemove(id, out _);
            entry.Done.TrySetResult(null);
        }
        else
        {
            entry.Done.TrySetResult(new Saved(status, context.Response.ContentType, body));
        }

        await original.WriteAsync(body, context.RequestAborted);
    }

    private void Purge()
    {
        DateTime limit = DateTime.UtcNow - Lifetime;
        if (_entries.Count < 500 && _entries.Values.All(e => e.CreatedUtc > limit))
        {
            return;
        }

        foreach ((string key, Entry value) in _entries)
        {
            if (value.CreatedUtc < limit && value.Done.Task.IsCompleted)
            {
                _entries.TryRemove(key, out _);
            }
        }

        if (_entries.Count > MaxEntries)
        {
            foreach ((string key, _) in _entries.Where(e => e.Value.Done.Task.IsCompleted).OrderBy(e => e.Value.CreatedUtc).Take(_entries.Count - MaxEntries))
            {
                _entries.TryRemove(key, out _);
            }
        }
    }
}
