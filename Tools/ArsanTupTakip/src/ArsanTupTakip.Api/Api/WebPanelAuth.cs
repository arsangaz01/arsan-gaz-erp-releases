using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace ArsanTupTakip.Api;

// UserCreatedAt guards against a restored users file handing an old session's id to a different account.
internal sealed record WebPanelSession(int UserId, DateTime UserCreatedAt, int Stamp, DateTime ExpiresUtc, string Ip);

// Username/password login for the browser panel plus the administrator's user management.
internal sealed class WebPanelAuth
{
    internal const string CookieName = "att_oturum";
    internal const string CsrfHeader = "X-Panel-Istek";
    private const int MaxFailuresPerIp = 5;
    private const int MaxFailuresPerHour = 20;
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);
    private static readonly TimeSpan LockTime = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, WebPanelSession> _sessions = new();
    private readonly ConcurrentDictionary<string, (int Failures, DateTime LockedUntilUtc)> _failures = new();
    private readonly ConcurrentQueue<DateTime> _recentFailures = new();
    private DateTime _globalLockUntilUtc;

    internal static string CookiePath(HttpContext context) => context.Request.PathBase + "/panel";

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
    internal async Task<string?> LoginAsync(HttpContext context, string? userName, string? password)
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

        if (!WebPanelUserStore.All().Any())
        {
            return "Panel girişi henüz yapılandırılmadı.";
        }

        bool wellFormed = !string.IsNullOrEmpty(userName) && !string.IsNullOrEmpty(password) && userName.Length <= 100 && password.Length <= 200;
        WebPanelUser? user = wellFormed ? WebPanelUserStore.FindByName(userName) : null;
        bool ok = false;
        if (user is not null)
        {
            ok = WebPanelUserStore.VerifyPassword(user, password!);
        }
        else if (wellFormed)
        {
            WebPanelUserStore.VerifyDummy(password!);
        }

        if (!ok)
        {
            await Task.Delay(700);
            RegisterFailure(ip, now);
            WebPanelAudit.Write(userName ?? "?", "Hatalı giriş denemesi", ip: ip);
            return "Kullanıcı adı veya şifre hatalı.";
        }

        _failures.TryRemove(ip, out _);
        if (!user!.IsActive)
        {
            WebPanelAudit.Write(user.UserName, "Pasif hesapla giriş denemesi", ip: ip);
            return "Bu hesap pasif durumda. Yöneticinize başvurun.";
        }

        WebPanelUser updated = WebPanelUserStore.Update(users =>
        {
            WebPanelUser target = users.First(u => u.Id == user.Id);
            target.LastLoginAt = now;
            target.LastLoginIp = ip;
            return target;
        });

        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        _sessions[TokenKey(token)] = new WebPanelSession(updated.Id, updated.CreatedAt, updated.SecurityStamp, now + SessionLifetime, ip);
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
            Path = CookiePath(context),
            MaxAge = SessionLifetime
        });
        AppLog.Write($"Web paneline giriş yapıldı: {updated.UserName} ({ip}).");
        WebPanelAudit.Write(updated.UserName, "Giriş", ip: ip);
        return null;
    }

    // Returns null on success or the message to show. The user's other sessions are signed out after a change.
    internal async Task<string?> ChangePasswordAsync(HttpContext context, string? currentPassword, string? newPassword, string? repeat)
    {
        if (Current(context) is not (WebPanelSession session, WebPanelUser user))
        {
            return "Oturum kapalı; yeniden giriş yapın.";
        }

        string ip = ClientIp(context);
        DateTime now = DateTime.UtcNow;
        if (_failures.TryGetValue(ip, out var state) && now < state.LockedUntilUtc)
        {
            return $"Çok sayıda hatalı deneme. {Math.Ceiling((state.LockedUntilUtc - now).TotalMinutes)} dakika sonra tekrar deneyin.";
        }

        if (string.IsNullOrEmpty(currentPassword) || currentPassword.Length > 200 || !WebPanelUserStore.VerifyPassword(user, currentPassword))
        {
            await Task.Delay(700);
            RegisterFailure(ip, now);
            return "Mevcut şifre hatalı.";
        }

        if (WebPanelUserStore.PasswordProblem(newPassword, user.UserName) is string problem)
        {
            return problem;
        }

        if (newPassword != repeat)
        {
            return "Yeni şifre ile tekrarı aynı değil.";
        }

        if (newPassword == currentPassword)
        {
            return "Yeni şifre eskisiyle aynı olamaz.";
        }

        int stamp = WebPanelUserStore.Update(users =>
        {
            WebPanelUser target = users.First(u => u.Id == user.Id);
            WebPanelUserStore.SetPassword(target, newPassword!);
            target.MustChangePassword = false;
            return target.SecurityStamp;
        });

        string currentKey = TokenKey(context.Request.Cookies[CookieName] ?? string.Empty);
        _sessions[currentKey] = session with { Stamp = stamp };
        DropSessions(user.Id, except: currentKey);
        AppLog.Write($"Web paneli şifresi değiştirildi: {user.UserName} ({ip}).");
        WebPanelAudit.Write(user.UserName, "Şifre değiştirildi", user.UserName, ip: ip);
        return null;
    }

    // Administrator actions. Each returns null on success or the message to show.
    internal string? CreateUser(WebPanelUser actor, string ip, string? userName, string? displayName, string? role, string? password, bool mustChange)
    {
        string name = (userName ?? string.Empty).Trim();
        if (WebPanelUserStore.UserNameProblem(name) is string nameProblem)
        {
            return nameProblem;
        }

        if (PanelRoles.Normalize(role) is not string normalizedRole)
        {
            return "Geçerli bir yetki seçin.";
        }

        if (WebPanelUserStore.PasswordProblem(password, name) is string passwordProblem)
        {
            return passwordProblem;
        }

        string display = CleanDisplayName(displayName, name);
        string? error = WebPanelUserStore.Update(users =>
        {
            if (users.Any(u => string.Equals(u.UserName, name, StringComparison.OrdinalIgnoreCase)))
            {
                return "Bu kullanıcı adı zaten var (pasif hesaplar dahil).";
            }

            WebPanelUser created = new()
            {
                Id = users.Count == 0 ? 1 : users.Max(u => u.Id) + 1,
                UserName = name,
                DisplayName = display,
                Role = normalizedRole,
                CreatedBy = actor.UserName,
                MustChangePassword = mustChange
            };
            WebPanelUserStore.SetPassword(created, password!);
            users.Add(created);
            return null;
        });
        if (error is null)
        {
            Notify(actor, ip, "Kullanıcı oluşturuldu", name, $"{display}, {normalizedRole}" + (mustChange ? ", ilk girişte şifre değiştirecek" : string.Empty));
        }

        return error;
    }

    internal string? UpdateUser(WebPanelUser actor, string ip, int id, string? displayName, string? role, bool active)
    {
        if (PanelRoles.Normalize(role) is not string normalizedRole)
        {
            return "Geçerli bir yetki seçin.";
        }

        List<string> changes = [];
        string? targetName = null;
        string? error = WebPanelUserStore.Update(users =>
        {
            WebPanelUser? target = users.FirstOrDefault(u => u.Id == id);
            if (target is null)
            {
                return "Kullanıcı bulunamadı.";
            }

            targetName = target.UserName;
            bool losesAdmin = target.IsActive && target.Role == PanelRoles.Admin && (!active || normalizedRole != PanelRoles.Admin);
            if (losesAdmin && target.Id == actor.Id)
            {
                return "Kendi hesabınızı pasife alamaz veya yönetici yetkinizi kaldıramazsınız. Bunu başka bir yönetici yapmalı.";
            }

            if (losesAdmin && !users.Any(u => u.Id != target.Id && u.IsActive && u.Role == PanelRoles.Admin))
            {
                return "Son aktif yönetici pasife alınamaz veya yetkisi düşürülemez. Önce başka bir yöneticiyi aktif edin.";
            }

            string display = CleanDisplayName(displayName, target.UserName);
            if (display != target.DisplayName)
            {
                changes.Add($"ad: {display}");
                target.DisplayName = display;
            }

            if (normalizedRole != target.Role)
            {
                changes.Add($"yetki: {target.Role} → {normalizedRole}");
                target.Role = normalizedRole;
                target.SecurityStamp++;
            }

            if (active != target.IsActive)
            {
                changes.Add(active ? "aktif edildi" : "pasife alındı");
                target.IsActive = active;
                target.SecurityStamp++;
            }

            return null;
        });

        if (error is null && changes.Count > 0)
        {
            DropSessions(id, except: null, onlyStale: true);
            Notify(actor, ip, "Kullanıcı güncellendi", targetName, string.Join(", ", changes));
        }

        return error;
    }

    internal string? ResetPassword(WebPanelUser actor, string ip, int id, string? password, bool mustChange)
    {
        WebPanelUser? existing = WebPanelUserStore.Find(id);
        if (existing is null)
        {
            return "Kullanıcı bulunamadı.";
        }

        if (WebPanelUserStore.PasswordProblem(password, existing.UserName) is string problem)
        {
            return problem;
        }

        WebPanelUserStore.Update(users =>
        {
            WebPanelUser target = users.First(u => u.Id == id);
            WebPanelUserStore.SetPassword(target, password!);
            target.MustChangePassword = mustChange;
            return 0;
        });
        DropSessions(id, except: null, onlyStale: true);
        Notify(actor, ip, "Şifre sıfırlandı", existing.UserName, mustChange ? "geçici şifre; ilk girişte değiştirecek" : "kalıcı şifre");
        return null;
    }

    internal static void Notify(WebPanelUser actor, string ip, string action, string? target, string detail)
    {
        AppLog.Write($"Web paneli yönetimi: {action} {target} ({detail}) — {actor.UserName}, {ip}.");
        WebPanelAudit.Write(actor.UserName, action, target, detail, ip);
    }

    private static string CleanDisplayName(string? displayName, string fallback)
    {
        string text = string.Join(' ', (displayName ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length == 0 ? fallback : text.Length > 80 ? text[..80] : text;
    }

    private void RegisterFailure(string ip, DateTime now)
    {
        var updated = _failures.AddOrUpdate(ip, _ => (1, DateTime.MinValue), (_, old) => (old.Failures + 1, old.LockedUntilUtc));
        if (updated.Failures >= MaxFailuresPerIp)
        {
            _failures[ip] = (0, now + LockTime);
            AppLog.Write($"Web paneli: {ip} adresi hatalı denemeler nedeniyle 15 dakika kilitlendi.");
        }

        _recentFailures.Enqueue(now);
        while (_recentFailures.TryPeek(out DateTime oldest) && oldest < now.AddHours(-1))
        {
            _recentFailures.TryDequeue(out _);
        }

        if (_recentFailures.Count >= MaxFailuresPerHour && now >= _globalLockUntilUtc)
        {
            _globalLockUntilUtc = now + LockTime;
            AppLog.Write("Web paneli: son bir saatte çok sayıda hatalı giriş; panel girişi 15 dakika kapatıldı.");
        }
    }

    // The session and its account, or null when signed out, expired, deactivated or changed since login.
    internal (WebPanelSession Session, WebPanelUser User)? Current(HttpContext context)
    {
        string token = context.Request.Cookies[CookieName] ?? string.Empty;
        if (token.Length is 0 or > 100 || !_sessions.TryGetValue(TokenKey(token), out WebPanelSession? session))
        {
            return null;
        }

        WebPanelUser? user = WebPanelUserStore.Find(session.UserId);
        if (session.ExpiresUtc < DateTime.UtcNow || user is null || !user.IsActive || user.CreatedAt != session.UserCreatedAt)
        {
            _sessions.TryRemove(TokenKey(token), out _);
            return null;
        }

        // A stale stamp is refused but not removed here: ChangePasswordAsync re-stamps its own session right after saving.
        return user.SecurityStamp == session.Stamp ? (session, user) : null;
    }

    internal void Logout(HttpContext context)
    {
        string token = context.Request.Cookies[CookieName] ?? string.Empty;
        if (token.Length > 0 && _sessions.TryRemove(TokenKey(token), out WebPanelSession? session)
            && WebPanelUserStore.Find(session.UserId) is { } user)
        {
            WebPanelAudit.Write(user.UserName, "Çıkış", ip: ClientIp(context));
        }

        context.Response.Cookies.Delete(CookieName, new CookieOptions { Path = CookiePath(context) });
    }

    internal int ActiveSessionCount(int userId) => _sessions.Values.Count(s => s.UserId == userId && s.ExpiresUtc > DateTime.UtcNow);

    private void DropSessions(int userId, string? except, bool onlyStale = false)
    {
        int stamp = WebPanelUserStore.Find(userId)?.SecurityStamp ?? -1;
        foreach ((string key, WebPanelSession session) in _sessions)
        {
            if (session.UserId == userId && key != except && (!onlyStale || session.Stamp != stamp))
            {
                _sessions.TryRemove(key, out _);
            }
        }
    }

    private static string TokenKey(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
