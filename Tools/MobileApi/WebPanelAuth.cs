using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace ArsanGazERP.MobileApi;

// Format of the single-user web-panel.json used before multi-user support; read only for the one-time migration.
internal sealed record WebPanelCredentials(string User, string Salt, string Hash, int Iterations);

// UserCreatedAt guards against a restored users file handing an old session's id to a different account.
internal sealed record WebPanelSession(int UserId, DateTime UserCreatedAt, int Stamp, DateTime ExpiresUtc, string Ip);

internal sealed record MobileLogin(string Token, WebPanelUser User, PairedDevice Device);

// Username/password login for the browser panel and the mobile app, plus the administrator's user management.
// One instance (DI singleton) serves both, so they share the lockout counters. Panel sessions last 1 hour and phone
// tokens 24 hours, both counted from login (absolute, not extended by activity). Each login gets its own random token;
// the same account may be signed in on several browsers and phones at once.
internal sealed class WebPanelAuth
{
    internal const string CookieName = "agp_oturum";
    internal const string CsrfHeader = "X-Panel-Istek";
    internal static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(1);
    internal static readonly TimeSpan MobileLifetime = TimeSpan.FromHours(24);
    // Yönetici phones stay signed in until they log out (or password/role change, or revoke).
    internal static readonly TimeSpan AdminMobileLifetime = TimeSpan.FromDays(3650);

    internal static TimeSpan MobileLifetimeFor(WebPanelUser user) =>
        user.Role == PanelRoles.Admin ? AdminMobileLifetime : MobileLifetime;
    private const int MaxFailuresPerIp = 5;
    private const int MaxFailuresPerHour = 20;
    private static readonly TimeSpan LockTime = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, WebPanelSession> _sessions = new();
    private readonly ConcurrentDictionary<string, (int Failures, DateTime LockedUntilUtc)> _failures = new();
    private readonly ConcurrentQueue<DateTime> _recentFailures = new();
    private readonly Lock _failureGate = new();
    private DateTime _globalLockUntilUtc;

    // Replaced by the self-test to move time forward.
    internal Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;

    internal TimeSpan FailureDelay { get; init; } = TimeSpan.FromMilliseconds(700);

    internal static string LegacyCredentialsFile => Path.Combine(MobileApiPaths.Root, "web-panel.json");

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

    // Password check with lockout; returns the account (already marked as logged in) or the message to show.
    internal async Task<(WebPanelUser? User, string? Error)> AuthenticateAsync(string ip, string? userName, string? password, string channel)
    {
        DateTime now = UtcNow();
        if (now < _globalLockUntilUtc)
        {
            return (null, "Çok sayıda hatalı deneme yapıldı; giriş geçici olarak kapalı. Biraz sonra tekrar deneyin.");
        }

        if (_failures.TryGetValue(ip, out var state) && now < state.LockedUntilUtc)
        {
            return (null, $"Çok sayıda hatalı deneme. {Math.Ceiling((state.LockedUntilUtc - now).TotalMinutes)} dakika sonra tekrar deneyin.");
        }

        if (!WebPanelUserStore.All().Any())
        {
            return (null, "Panel girişi henüz yapılandırılmadı.");
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
            await Task.Delay(FailureDelay);
            RegisterFailure(ip, now);
            WebPanelAudit.Write(userName ?? "?", $"Hatalı giriş denemesi ({channel})", ip: ip);
            return (null, "Kullanıcı adı veya şifre hatalı.");
        }

        _failures.TryRemove(ip, out _);
        if (!user!.IsActive)
        {
            WebPanelAudit.Write(user.UserName, $"Pasif hesapla giriş denemesi ({channel})", ip: ip);
            return (null, "Bu hesap pasif durumda. Yöneticinize başvurun.");
        }

        WebPanelUser updated = WebPanelUserStore.Update(users =>
        {
            WebPanelUser target = users.First(u => u.Id == user.Id);
            target.LastLoginAt = now;
            target.LastLoginIp = ip;
            return target;
        });
        return (updated, null);
    }

    // Returns null on success or the message to show.
    internal async Task<string?> LoginAsync(HttpContext context, string? userName, string? password)
    {
        string ip = ClientIp(context);
        (WebPanelUser? updated, string? error) = await AuthenticateAsync(ip, userName, password, "panel");
        if (updated is null)
        {
            return error;
        }

        string token = CreateSession(updated, ip);
        context.Response.Cookies.Append(CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = IsSecureRequest(context),
            SameSite = SameSiteMode.Strict,
            Path = "/panel",
            MaxAge = SessionLifetime
        });
        MobileLog.Write($"Web paneline giriş yapıldı: {updated.UserName} ({ip}).");
        WebPanelAudit.Write(updated.UserName, "Giriş", ip: ip);
        if (!WebPanelTrustedIps.IsTrusted(ip))
        {
            TelegramNotifier.Send($"🌐 Tüp takip paneline giriş yapıldı. Kullanıcı: {updated.UserName} ({updated.Role}). IP: {ip}. Bu beklenmiyorsa hesabı Yönetim sekmesinden pasife alın.");
        }

        return null;
    }

    // Adds an independent session; other sessions of the same user are left alone.
    internal string CreateSession(WebPanelUser user, string ip)
    {
        DateTime now = UtcNow();
        string token = NewToken();
        _sessions[TokenKey(token)] = new WebPanelSession(user.Id, user.CreatedAt, user.SecurityStamp, now + SessionLifetime, ip);
        foreach ((string key, WebPanelSession session) in _sessions)
        {
            if (session.ExpiresUtc < now)
            {
                _sessions.TryRemove(key, out _);
            }
        }

        return token;
    }

    // Phone login with the panel credentials. Each call issues a new token valid for 24 hours (Yönetici: until logout).
    internal async Task<(MobileLogin? Login, string? Error)> MobileLoginAsync(string ip, string? userName, string? password, string? deviceName, DeviceStore devices)
    {
        (WebPanelUser? user, string? error) = await AuthenticateAsync(ip, userName, password, "mobil");
        if (user is null)
        {
            return (null, error);
        }

        string name = string.IsNullOrWhiteSpace(deviceName) ? "Telefon" : deviceName.Trim()[..Math.Min(deviceName.Trim().Length, 60)];
        string token = devices.AddForUser(name, user, UtcNow(), MobileLifetimeFor(user), ip, out PairedDevice device);
        MobileLog.Write($"Mobil uygulamaya giriş yapıldı: {user.UserName} / {name} ({ip}).");
        WebPanelAudit.Write(user.UserName, "Mobil giriş", name, ip: ip);
        if (!WebPanelTrustedIps.IsTrusted(ip))
        {
            TelegramNotifier.Send($"📱 Mobil uygulamaya giriş yapıldı. Kullanıcı: {user.UserName} ({user.Role}), cihaz: {name}. IP: {ip}. Bu beklenmiyorsa Yönetim > Cihazlar'dan oturumu kapatın.");
        }

        return (new MobileLogin(token, user, device), null);
    }

    // The account behind a phone token, or the reason it is refused. Expired and outdated tokens are removed.
    internal (WebPanelUser? User, string? Error) CheckDevice(PairedDevice? device, DeviceStore devices)
    {
        if (device is null)
        {
            return (null, "Oturum geçersiz veya süresi doldu; kullanıcı adı ve şifrenizle yeniden giriş yapın.");
        }

        if (device.UserId is not int userId)
        {
            return (null, "Bu telefon eski eşleştirme yöntemiyle bağlı. Uygulamayı güncelleyip (1.8.0) kullanıcı adı ve şifrenizle giriş yapın.");
        }

        if (device.ExpiresUtc is not DateTime expires || expires <= UtcNow())
        {
            devices.Remove(device.Id);
            return (null, "Oturum süresi doldu (24 saat). Kullanıcı adı ve şifrenizle yeniden giriş yapın.");
        }

        WebPanelUser? user = WebPanelUserStore.Find(userId);
        if (user is null || !user.IsActive || user.CreatedAt != device.UserCreatedAt || user.SecurityStamp != device.Stamp)
        {
            devices.Remove(device.Id);
            return (null, user is { IsActive: false }
                ? "Hesabınız pasife alındı. Yöneticinize başvurun."
                : "Şifreniz veya yetkiniz değiştiği için oturum kapandı; yeniden giriş yapın.");
        }

        return (user, null);
    }

    // Returns null on success or the message to show. The user's other sessions are signed out after a change.
    internal async Task<string?> ChangePasswordAsync(HttpContext context, string? currentPassword, string? newPassword, string? repeat)
    {
        if (Current(context) is not (WebPanelSession session, WebPanelUser user))
        {
            return "Oturum kapalı; yeniden giriş yapın.";
        }

        string currentKey = TokenKey(context.Request.Cookies[CookieName] ?? string.Empty);
        (string? error, int stamp) = await ChangeOwnPasswordAsync(user, ClientIp(context), currentPassword, newPassword, repeat, "panel");
        if (error is null)
        {
            _sessions[currentKey] = session with { Stamp = stamp };
            DropSessions(user.Id, except: currentKey);
        }

        return error;
    }

    // Shared by the panel and the phone. Returns the new security stamp so the caller can keep its own session.
    internal async Task<(string? Error, int Stamp)> ChangeOwnPasswordAsync(WebPanelUser user, string ip, string? currentPassword, string? newPassword, string? repeat, string channel)
    {
        DateTime now = UtcNow();
        if (_failures.TryGetValue(ip, out var state) && now < state.LockedUntilUtc)
        {
            return ($"Çok sayıda hatalı deneme. {Math.Ceiling((state.LockedUntilUtc - now).TotalMinutes)} dakika sonra tekrar deneyin.", 0);
        }

        if (string.IsNullOrEmpty(currentPassword) || currentPassword.Length > 200 || !WebPanelUserStore.VerifyPassword(user, currentPassword))
        {
            await Task.Delay(FailureDelay);
            RegisterFailure(ip, now);
            return ("Mevcut şifre hatalı.", 0);
        }

        if (WebPanelUserStore.PasswordProblem(newPassword, user.UserName) is string problem)
        {
            return (problem, 0);
        }

        if (newPassword != repeat)
        {
            return ("Yeni şifre ile tekrarı aynı değil.", 0);
        }

        if (newPassword == currentPassword)
        {
            return ("Yeni şifre eskisiyle aynı olamaz.", 0);
        }

        int stamp = WebPanelUserStore.Update(users =>
        {
            WebPanelUser target = users.First(u => u.Id == user.Id);
            WebPanelUserStore.SetPassword(target, newPassword!);
            target.MustChangePassword = false;
            return target.SecurityStamp;
        });

        DropSessions(user.Id, except: null, onlyStale: true);
        MobileLog.Write($"Şifre değiştirildi ({channel}): {user.UserName} ({ip}).");
        WebPanelAudit.Write(user.UserName, $"Şifre değiştirildi ({channel})", user.UserName, ip: ip);
        if (!WebPanelTrustedIps.IsTrusted(ip))
        {
            TelegramNotifier.Send($"🔑 Tüp takip şifresi değiştirildi ({channel}). Kullanıcı: {user.UserName}. IP: {ip}. Diğer açık oturumlar kapatıldı. Bu siz değilseniz hemen haber verin.");
        }

        return (null, stamp);
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
                return "Bu kullanıcı adı zaten var veya silinmiş bir hesaba ait (pasif ve silinmiş hesaplar dahil); başka bir ad seçin.";
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
            Notify(actor, ip, "Kullanıcı oluşturuldu", name, $"{display}, {normalizedRole}" + (mustChange ? ", ilk girişte şifre değiştirecek" : string.Empty),
                $"👤 Tüp takip paneline yeni kullanıcı eklendi: {name} ({normalizedRole}). Ekleyen: {actor.UserName}.");
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
            WebPanelUser? target = users.FirstOrDefault(u => u.Id == id && u.DeletedAt is null);
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
                changes.Add($"yetki: {target.Role} → {normalizedRole}" + (target.ExtraPermissions.Count + target.RemovedPermissions.Count > 0 ? " (özel yetkiler sıfırlandı)" : string.Empty));
                target.Role = normalizedRole;
                target.ExtraPermissions = [];
                target.RemovedPermissions = [];
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
            string summary = string.Join(", ", changes);
            bool important = changes.Any(c => c.StartsWith("yetki", StringComparison.Ordinal) || c.Contains("aktif", StringComparison.Ordinal) || c.Contains("pasif", StringComparison.Ordinal));
            Notify(actor, ip, "Kullanıcı güncellendi", targetName, summary,
                important ? $"🛡 Tüp takip paneli kullanıcısı güncellendi: {targetName} ({summary}). Yapan: {actor.UserName}." : null);
        }

        return error;
    }

    // Stores the ticked permissions as changes to the role defaults (reset = back to the defaults). Takes effect on the
    // user's next request; sessions stay open.
    internal string? SetPermissions(WebPanelUser actor, string ip, int id, IEnumerable<string>? wanted, bool reset)
    {
        List<string> requested = (wanted ?? []).Distinct().ToList();
        if (!reset && requested.FirstOrDefault(k => !PanelPermissions.IsKnown(k)) is string unknown)
        {
            return $"Bilinmeyen yetki: {unknown}";
        }

        string? targetName = null;
        string detail = string.Empty;
        string? error = WebPanelUserStore.Update(users =>
        {
            WebPanelUser? target = users.FirstOrDefault(u => u.Id == id && u.DeletedAt is null);
            if (target is null)
            {
                return "Kullanıcı bulunamadı.";
            }

            targetName = target.UserName;
            if (target.Role == PanelRoles.Admin)
            {
                return "Yönetici rolü her yetkiye sahiptir; daraltmak için önce rolünü değiştirin.";
            }

            if (!reset && requested.FirstOrDefault(PanelPermissions.IsAdminOnly) is string adminOnly)
            {
                return $"\"{PanelPermissions.Catalog.First(p => p.Key == adminOnly).Label}\" yalnızca Yönetici rolüne verilebilir.";
            }

            (List<string> extra, List<string> removed) = reset ? ([], []) : PanelRoles.Overrides(target.Role, requested);
            target.ExtraPermissions = extra;
            target.RemovedPermissions = removed;
            detail = reset || extra.Count + removed.Count == 0
                ? "rol varsayılanı"
                : string.Join(", ", extra.Select(k => "+" + k).Concat(removed.Select(k => "-" + k)));
            return null;
        });

        if (error is null)
        {
            Notify(actor, ip, "Yetkiler güncellendi", targetName, detail,
                $"🛡 Tüp takip: {targetName} kullanıcısının yetkileri değişti ({detail}). Yapan: {actor.UserName}.");
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
        Notify(actor, ip, "Şifre sıfırlandı", existing.UserName, mustChange ? "geçici şifre; ilk girişte değiştirecek" : "kalıcı şifre",
            $"🔑 Tüp takip paneli: {existing.UserName} kullanıcısının şifresi yönetici tarafından sıfırlandı. Yapan: {actor.UserName}.");
        return null;
    }

    // Permanent removal; confirmUserName must repeat the account's user name. Every browser session and phone of the
    // account ends at once. Returns null on success or the message to show; summary describes what was closed.
    internal string? DeleteUser(WebPanelUser actor, string ip, int id, string? confirmUserName, DeviceStore devices, out string summary)
    {
        summary = string.Empty;
        WebPanelUser? existing = WebPanelUserStore.Find(id);
        if (existing is null)
        {
            return "Kullanıcı bulunamadı (zaten silinmiş olabilir).";
        }

        if (existing.Id == actor.Id)
        {
            return "Kendi hesabınızı silemezsiniz. Bunu başka bir yönetici yapmalı.";
        }

        if (actor.Role != PanelRoles.Admin && existing.Role == PanelRoles.Admin)
        {
            return "Yönetici hesaplarını yalnızca bir Yönetici silebilir.";
        }

        if (!string.Equals((confirmUserName ?? string.Empty).Trim(), existing.UserName, StringComparison.OrdinalIgnoreCase))
        {
            return $"Onay için kullanıcı adını ({existing.UserName}) aynen yazın.";
        }

        string? error = WebPanelUserStore.Update(users =>
        {
            WebPanelUser? target = users.FirstOrDefault(u => u.Id == id && u.DeletedAt is null);
            if (target is null)
            {
                return "Kullanıcı bulunamadı (zaten silinmiş olabilir).";
            }

            if (target.IsActive && target.Role == PanelRoles.Admin && !users.Any(u => u.Id != target.Id && u.IsActive && u.Role == PanelRoles.Admin))
            {
                return "Son aktif yönetici silinemez. Önce başka bir yöneticiyi aktif edin.";
            }

            WebPanelUserStore.MarkDeleted(target, actor.UserName);
            return null;
        });
        if (error is not null)
        {
            return error;
        }

        int sessions = ActiveSessionCount(id);
        DropSessions(id, except: null);
        int phones = devices.RemoveForUser(id, exceptId: null);
        summary = $"{sessions} panel oturumu ve {phones} telefon oturumu kapatıldı";
        Notify(actor, ip, "Kullanıcı silindi", existing.UserName, $"{existing.DisplayName}, {existing.Role}; {summary}",
            $"🗑 Tüp takip paneli kullanıcısı silindi: {existing.UserName} ({existing.Role}). Yapan: {actor.UserName}.");
        return null;
    }

    private static void Notify(WebPanelUser actor, string ip, string action, string? target, string detail, string? telegram)
    {
        MobileLog.Write($"Web paneli yönetimi: {action} {target} ({detail}) — {actor.UserName}, {ip}.");
        WebPanelAudit.Write(actor.UserName, action, target, detail, ip);
        if (telegram is not null && !WebPanelTrustedIps.IsTrusted(ip))
        {
            TelegramNotifier.Send(telegram + $" IP: {ip}.");
        }
    }

    private static string CleanDisplayName(string? displayName, string fallback)
    {
        string text = string.Join(' ', (displayName ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length == 0 ? fallback : text.Length > 80 ? text[..80] : text;
    }

    private void RegisterFailure(string ip, DateTime now)
    {
        bool lockedIp = false;
        bool lockedAll = false;
        lock (_failureGate)
        {
            var updated = _failures.AddOrUpdate(ip, _ => (1, DateTime.MinValue), (_, old) => (old.Failures + 1, old.LockedUntilUtc));
            if (updated.Failures >= MaxFailuresPerIp)
            {
                _failures[ip] = (0, now + LockTime);
                lockedIp = true;
            }

            _recentFailures.Enqueue(now);
            while (_recentFailures.TryPeek(out DateTime oldest) && oldest < now.AddHours(-1))
            {
                _recentFailures.TryDequeue(out _);
            }

            if (_recentFailures.Count >= MaxFailuresPerHour && now >= _globalLockUntilUtc)
            {
                _globalLockUntilUtc = now + LockTime;
                lockedAll = true;
            }
        }

        if (lockedIp)
        {
            MobileLog.Write($"Giriş: {ip} adresi hatalı denemeler nedeniyle 15 dakika kilitlendi.");
            TelegramNotifier.Send($"⚠ Tüp takip girişine {ip} adresinden {MaxFailuresPerIp} hatalı deneme; adres 15 dakika kilitlendi.");
        }

        if (lockedAll)
        {
            TelegramNotifier.Send("⚠ Tüp takip girişine son bir saatte çok sayıda hatalı deneme yapıldı; giriş 15 dakika kapatıldı.");
        }
    }

    // The session and its account, or null when signed out, expired, deactivated or changed since login.
    internal (WebPanelSession Session, WebPanelUser User)? Current(HttpContext context) =>
        CurrentByToken(context.Request.Cookies[CookieName] ?? string.Empty);

    internal (WebPanelSession Session, WebPanelUser User)? CurrentByToken(string token)
    {
        if (token.Length is 0 or > 100 || !_sessions.TryGetValue(TokenKey(token), out WebPanelSession? session))
        {
            return null;
        }

        WebPanelUser? user = WebPanelUserStore.Find(session.UserId);
        if (session.ExpiresUtc <= UtcNow() || user is null || !user.IsActive || user.CreatedAt != session.UserCreatedAt)
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

        context.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/panel" });
    }

    internal int ActiveSessionCount(int userId)
    {
        DateTime now = UtcNow();
        return _sessions.Values.Count(s => s.UserId == userId && s.ExpiresUtc > now);
    }

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

    private static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string TokenKey(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
