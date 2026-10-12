using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ArsanTupTakip.Api;

// One signed-in phone. Every login creates its own random token, so the same user may be signed in on several
// phones at once; a new login never ends another one. UserCreatedAt + Stamp tie the token to the account state at
// login: deactivation, role change and password change (SecurityStamp) end it, as does ExpiresUtc (absolute).
internal sealed record MobileSession(
    string Id,
    string Name,
    string TokenHash,
    DateTime CreatedUtc,
    DateTime ExpiresUtc,
    DateTime? LastSeenUtc,
    int UserId,
    string UserName,
    DateTime UserCreatedAt,
    int Stamp,
    string? Ip);

// Signed-in phones in <data folder>/devices.json. Only SHA-256 hashes of the access tokens are kept.
internal sealed class DeviceStore
{
    internal static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(24);
    // Yönetici phones stay signed in until they log out (or password/role change, or revoke).
    internal static readonly TimeSpan AdminSessionLifetime = TimeSpan.FromDays(3650);

    internal static TimeSpan LifetimeFor(WebPanelUser user) =>
        user.Role == PanelRoles.Admin ? AdminSessionLifetime : SessionLifetime;

    private readonly Lock _gate = new();
    private readonly Func<DateTime> _clock;
    private List<MobileSession> _sessions;
    private DateTime _loadedWriteUtc;

    public DeviceStore() : this(() => DateTime.UtcNow)
    {
    }

    internal DeviceStore(Func<DateTime> clock)
    {
        _clock = clock;
        _sessions = Load();
        _loadedWriteUtc = LastWriteUtc();
    }

    internal static string FilePath => Path.Combine(AppPaths.Root, "devices.json");

    private static DateTime LastWriteUtc() =>
        File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : DateTime.MinValue;

    private void ReloadIfChanged()
    {
        DateTime current = LastWriteUtc();
        if (current != _loadedWriteUtc)
        {
            _sessions = Load();
            _loadedWriteUtc = current;
        }
    }

    public IReadOnlyList<MobileSession> All()
    {
        lock (_gate)
        {
            ReloadIfChanged();
            DateTime now = _clock();
            return [.. _sessions.Where(s => s.ExpiresUtc > now)];
        }
    }

    // Returns the plain token (shown to the phone once) and the stored session.
    public (string Token, MobileSession Session) Add(WebPanelUser user, string deviceName, string? ip)
    {
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string trimmed = (deviceName ?? string.Empty).Trim();
        string name = trimmed.Length == 0 ? "Telefon" : trimmed[..Math.Min(trimmed.Length, 60)];
        DateTime now = _clock();
        MobileSession session = new(Guid.NewGuid().ToString("N"), name, Hash(token), now, now + LifetimeFor(user), now,
            user.Id, user.UserName, user.CreatedAt, user.SecurityStamp, ip);
        lock (_gate)
        {
            ReloadIfChanged();
            _sessions.RemoveAll(s => s.ExpiresUtc <= now);
            _sessions.Add(session);
            Save();
        }

        return (token, session);
    }

    // The session behind a token, or null when unknown or expired. Account checks are done by the caller.
    public MobileSession? Validate(string token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 100)
        {
            return null;
        }

        byte[] candidate = Convert.FromHexString(Hash(token));
        lock (_gate)
        {
            ReloadIfChanged();
            MobileSession? match = null;
            foreach (MobileSession session in _sessions)
            {
                if (CryptographicOperations.FixedTimeEquals(candidate, Convert.FromHexString(session.TokenHash)))
                {
                    match = session;
                }
            }

            DateTime now = _clock();
            if (match is null || match.ExpiresUtc <= now)
            {
                return null;
            }

            if (match.LastSeenUtc is null || now - match.LastSeenUtc > TimeSpan.FromMinutes(10))
            {
                int index = _sessions.IndexOf(match);
                match = match with { LastSeenUtc = now };
                _sessions[index] = match;
                Save();
            }

            return match;
        }
    }

    // After the user changed the password on this phone: keeps this session, the others end through the stamp.
    public void Restamp(string id, int stamp)
    {
        lock (_gate)
        {
            ReloadIfChanged();
            int index = _sessions.FindIndex(s => s.Id == id);
            if (index >= 0)
            {
                _sessions[index] = _sessions[index] with { Stamp = stamp };
                Save();
            }
        }
    }

    public MobileSession? Remove(string id)
    {
        lock (_gate)
        {
            ReloadIfChanged();
            MobileSession? session = _sessions.FirstOrDefault(d => d.Id == id);
            if (session is not null)
            {
                _sessions.Remove(session);
                Save();
            }

            return session;
        }
    }

    public int RemoveForUser(int userId)
    {
        lock (_gate)
        {
            ReloadIfChanged();
            int removed = _sessions.RemoveAll(s => s.UserId == userId);
            if (removed > 0)
            {
                Save();
            }

            return removed;
        }
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static List<MobileSession> Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<List<MobileSession>>(File.ReadAllText(FilePath))?.Where(s => s.UserId > 0 && s.TokenHash.Length > 0).ToList() ?? []
                : [];
        }
        catch (JsonException exception)
        {
            AppLog.Write("Mobil oturum listesi okunamadı; oturumlar sıfırlandı: " + exception.Message);
            return [];
        }
    }

    private void Save()
    {
        string temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_sessions, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, FilePath, overwrite: true);
        _loadedWriteUtc = LastWriteUtc();
    }
}

// Every /api call except the public ones needs a valid phone session of an active user whose account did not change
// since login; the endpoint's permission metadata is then checked against the user's effective permissions.
internal sealed class DeviceAuthMiddleware(RequestDelegate next, DeviceStore devices)
{
    internal static readonly string[] PublicPaths = ["/api/giris", "/api/merhaba", "/api/guncelleme"];

    public async Task InvokeAsync(HttpContext context)
    {
        PathString path = context.Request.Path;
        if (!path.StartsWithSegments("/api") || PublicPaths.Any(p => path.StartsWithSegments(p)))
        {
            await next(context);
            return;
        }

        string header = context.Request.Headers.Authorization.ToString();
        MobileSession? session = header.StartsWith("Bearer ", StringComparison.Ordinal)
            ? devices.Validate(header["Bearer ".Length..].Trim())
            : null;
        WebPanelUser? user = session is null ? null : WebPanelUserStore.Find(session.UserId);
        if (session is null || user is null || !user.IsActive || user.CreatedAt != session.UserCreatedAt || user.SecurityStamp != session.Stamp)
        {
            await Task.Delay(300);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "Oturumunuz sona erdi (mobil giriş 24 saat geçerlidir) veya kapatıldı. Kullanıcı adı ve şifrenizle yeniden giriş yapın.",
                loginRequired = true
            });
            return;
        }

        if (!Perm.Has(user, Perm.MobileLogin))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Mobil uygulamaya giriş yetkiniz kaldırıldı. Yöneticinize başvurun.", loginRequired = true });
            return;
        }

        if (PermissionEndpointExtensions.Check(context, user) is IResult denied)
        {
            await denied.ExecuteAsync(context);
            return;
        }

        context.Items["device"] = session;
        context.Items["panelAccount"] = user;
        context.Items["panelUser"] = user.UserName;
        await next(context);
    }
}
