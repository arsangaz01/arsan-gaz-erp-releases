using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ArsanGazERP.MobileApi;

internal sealed record WebPanelCredentials(string User, string Salt, string Hash, int Iterations);

internal sealed record WebPanelSession(string User, DateTime ExpiresUtc, string Ip);

// Username/password login for the browser panel. Only a PBKDF2 hash of the password is stored on disk.
internal sealed class WebPanelAuth
{
    internal const string CookieName = "agp_oturum";
    internal const string CsrfHeader = "X-Panel-Istek";
    private const int MaxFailuresPerIp = 5;
    private const int MaxFailuresPerHour = 20;
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);
    private static readonly TimeSpan LockTime = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, WebPanelSession> _sessions = new();
    private readonly ConcurrentDictionary<string, (int Failures, DateTime LockedUntilUtc)> _failures = new();
    private readonly ConcurrentQueue<DateTime> _recentFailures = new();
    private DateTime _globalLockUntilUtc;

    internal static string CredentialsFile => Path.Combine(MobileApiPaths.Root, "web-panel.json");

    internal static bool IsConfigured => File.Exists(CredentialsFile);

    internal static WebPanelCredentials CreateCredentials(string user, string password, int iterations = 210_000)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);
        return new WebPanelCredentials(user.Trim(), Convert.ToBase64String(salt), Convert.ToBase64String(hash), iterations);
    }

    internal static bool Verify(WebPanelCredentials credentials, string user, string password)
    {
        byte[] expected = Convert.FromBase64String(credentials.Hash);
        byte[] actual = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), Convert.FromBase64String(credentials.Salt), credentials.Iterations, HashAlgorithmName.SHA256, expected.Length);
        bool userMatches = CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(user.Trim().ToLowerInvariant())),
            SHA256.HashData(Encoding.UTF8.GetBytes(credentials.User.ToLowerInvariant())));
        return CryptographicOperations.FixedTimeEquals(expected, actual) & userMatches;
    }

    internal static void SaveCredentials(WebPanelCredentials credentials) =>
        File.WriteAllText(CredentialsFile, JsonSerializer.Serialize(credentials));

    private static WebPanelCredentials? Load()
    {
        try
        {
            return IsConfigured ? JsonSerializer.Deserialize<WebPanelCredentials>(File.ReadAllText(CredentialsFile)) : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            MobileLog.Write("Web paneli giriş dosyası okunamadı: " + exception.Message);
            return null;
        }
    }

    internal static string ClientIp(HttpContext context)
    {
        string forwarded = context.Request.Headers["CF-Connecting-IP"].ToString();
        return forwarded.Length is > 0 and <= 64 ? forwarded : context.Connection.RemoteIpAddress?.ToString() ?? "?";
    }

    internal static bool IsSecureRequest(HttpContext context)
    {
        string host = context.Request.Host.Host;
        return !(host is "localhost" or "127.0.0.1" or "::1");
    }

    // Returns null on success or the message to show.
    internal async Task<string?> LoginAsync(HttpContext context, string? user, string? password)
    {
        string ip = ClientIp(context);
        DateTime now = DateTime.UtcNow;
        if (now < _globalLockUntilUtc)
        {
            return "Çok sayıda hatalı deneme yapıldı; giriş geçici olarak kapalı. Biraz sonra tekrar deneyin.";
        }

        if (_failures.TryGetValue(ip, out var state) && now < state.LockedUntilUtc)
        {
            return $"Çok sayıda hatalı deneme. {Math.Ceiling((state.LockedUntilUtc - now).TotalMinutes)} dakika sonra tekrar deneyin.";
        }

        WebPanelCredentials? credentials = Load();
        if (credentials is null)
        {
            return "Panel girişi henüz yapılandırılmadı.";
        }

        bool ok = !string.IsNullOrEmpty(user) && !string.IsNullOrEmpty(password) && user.Length <= 100 && password.Length <= 200
            && Verify(credentials, user, password);
        if (!ok)
        {
            await Task.Delay(700);
            RegisterFailure(ip, now);
            return "Kullanıcı adı veya şifre hatalı.";
        }

        _failures.TryRemove(ip, out _);
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        _sessions[TokenKey(token)] = new WebPanelSession(credentials.User, now + SessionLifetime, ip);
        foreach ((string key, WebPanelSession session) in _sessions)
        {
            if (session.ExpiresUtc < now)
            {
                _sessions.TryRemove(key, out _);
            }
        }

        context.Response.Cookies.Append(CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = IsSecureRequest(context),
            SameSite = SameSiteMode.Strict,
            Path = "/panel",
            MaxAge = SessionLifetime
        });
        MobileLog.Write($"Web paneline giriş yapıldı ({ip}).");
        TelegramNotifier.Send($"🌐 Tüp takip paneline giriş yapıldı. IP: {ip}. Bu siz değilseniz panel şifresini değiştirin.");
        return null;
    }

    // Returns null on success or the message to show. Other sessions are signed out after a change.
    internal async Task<string?> ChangePasswordAsync(HttpContext context, string? currentPassword, string? newPassword, string? repeat)
    {
        WebPanelSession? session = Current(context);
        WebPanelCredentials? credentials = Load();
        if (session is null || credentials is null)
        {
            return "Oturum kapalı; yeniden giriş yapın.";
        }

        string ip = ClientIp(context);
        DateTime now = DateTime.UtcNow;
        if (_failures.TryGetValue(ip, out var state) && now < state.LockedUntilUtc)
        {
            return $"Çok sayıda hatalı deneme. {Math.Ceiling((state.LockedUntilUtc - now).TotalMinutes)} dakika sonra tekrar deneyin.";
        }

        if (string.IsNullOrEmpty(currentPassword) || currentPassword.Length > 200 || !Verify(credentials, credentials.User, currentPassword))
        {
            await Task.Delay(700);
            RegisterFailure(ip, now);
            return "Mevcut şifre hatalı.";
        }

        if (string.IsNullOrEmpty(newPassword) || newPassword.Length < 8 || newPassword.Length > 200)
        {
            return "Yeni şifre 8-200 karakter olmalı.";
        }

        if (newPassword != repeat)
        {
            return "Yeni şifre ile tekrarı aynı değil.";
        }

        if (newPassword == currentPassword)
        {
            return "Yeni şifre eskisiyle aynı olamaz.";
        }

        SaveCredentials(CreateCredentials(credentials.User, newPassword));
        string currentKey = TokenKey(context.Request.Cookies[CookieName] ?? string.Empty);
        foreach (string key in _sessions.Keys)
        {
            if (key != currentKey)
            {
                _sessions.TryRemove(key, out _);
            }
        }

        MobileLog.Write($"Web paneli şifresi değiştirildi ({ip}).");
        TelegramNotifier.Send($"🔑 Tüp takip paneli şifresi değiştirildi. IP: {ip}. Diğer açık oturumlar kapatıldı. Bu siz değilseniz hemen haber verin.");
        return null;
    }

    private void RegisterFailure(string ip, DateTime now)
    {
        var updated = _failures.AddOrUpdate(ip, _ => (1, DateTime.MinValue), (_, old) => (old.Failures + 1, old.LockedUntilUtc));
        if (updated.Failures >= MaxFailuresPerIp)
        {
            _failures[ip] = (0, now + LockTime);
            MobileLog.Write($"Web paneli: {ip} adresi hatalı denemeler nedeniyle 15 dakika kilitlendi.");
            TelegramNotifier.Send($"⚠ Tüp takip paneline {ip} adresinden {MaxFailuresPerIp} hatalı giriş denemesi; adres 15 dakika kilitlendi.");
        }

        _recentFailures.Enqueue(now);
        while (_recentFailures.TryPeek(out DateTime oldest) && oldest < now.AddHours(-1))
        {
            _recentFailures.TryDequeue(out _);
        }

        if (_recentFailures.Count >= MaxFailuresPerHour && now >= _globalLockUntilUtc)
        {
            _globalLockUntilUtc = now + LockTime;
            TelegramNotifier.Send("⚠ Tüp takip paneline son bir saatte çok sayıda hatalı giriş denemesi yapıldı; panel girişi 15 dakika kapatıldı.");
        }
    }

    internal WebPanelSession? Current(HttpContext context)
    {
        string token = context.Request.Cookies[CookieName] ?? string.Empty;
        if (token.Length is 0 or > 100 || !_sessions.TryGetValue(TokenKey(token), out WebPanelSession? session))
        {
            return null;
        }

        if (session.ExpiresUtc < DateTime.UtcNow)
        {
            _sessions.TryRemove(TokenKey(token), out _);
            return null;
        }

        return session;
    }

    internal void Logout(HttpContext context)
    {
        string token = context.Request.Cookies[CookieName] ?? string.Empty;
        if (token.Length > 0)
        {
            _sessions.TryRemove(TokenKey(token), out _);
        }

        context.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/panel" });
    }

    private static string TokenKey(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
