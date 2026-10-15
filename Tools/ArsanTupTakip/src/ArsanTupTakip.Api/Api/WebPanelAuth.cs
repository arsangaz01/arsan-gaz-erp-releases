using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace ArsanTupTakip.Api;

// UserCreatedAt guards against a restored users file handing an old session's id to a different account.
internal sealed record WebPanelSession(int UserId, DateTime UserCreatedAt, int Stamp, DateTime ExpiresUtc, string Ip);

// Username/password login shared by the browser panel (cookie, 1 hour) and the mobile app (token, 24 hours), plus the
// user management. Every login gets its own random session; logins of the same or other users never end each other.
// Failed attempts are counted per IP and globally for both entry points together.
internal sealed class WebPanelAuth
{
    internal const string CookieName = "att_oturum";
    internal const string CsrfHeader = "X-Panel-Istek";
    internal static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(24);
    private const int MaxFailuresPerIp = 5;
    private const int MaxFailuresPerHour = 20;
    private static readonly TimeSpan LockTime = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, WebPanelSession> _sessions = new();
    private readonly ConcurrentDictionary<string, (int Failures, DateTime LockedUntilUtc)> _failures = new();
    private readonly ConcurrentQueue<DateTime> _recentFailures = new();
    private readonly Lock _failureGate = new();
    private readonly Func<DateTime> _clock;
    private DateTime _globalLockUntilUtc;

    public WebPanelAuth() : this(() => DateTime.UtcNow)
    {
    }

    internal WebPanelAuth(Func<DateTime> clock) => _clock = clock;

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

    // Checks the password with the lockout rules. Returns the active account or the message to show.
    internal async Task<(WebPanelUser? User, string? Error)> AuthenticateAsync(string ip, string? userName, string? password, string channel)
    {
        DateTime now = _clock();
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
            return (null, "Giriş henüz yapılandırılmadı.");
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

    // Browser login. Returns null on success or the message to show.
    internal async Task<string?> LoginAsync(HttpContext context, string? userName, string? password)
    {
        string ip = ClientIp(context);
        (WebPanelUser? user, string? error) = await AuthenticateAsync(ip, userName, password, "panel");
        if (user is null)
        {
            return error;
        }

        if (!Perm.Has(user, Perm.PanelLogin))
        {
            WebPanelAudit.Write(user.UserName, "Panel girişi reddedildi (yetki yok)", ip: ip);
            return "Web paneline giriş yetkiniz yok. Yöneticinize başvurun.";
        }

        DateTime now = _clock();
        string token = NewToken();
        _sessions[TokenKey(token)] = new WebPanelSession(user.Id, user.CreatedAt, user.SecurityStamp, now + SessionLifetime, ip);
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
        AppLog.Write($"Web paneline giriş yapıldı: {user.UserName} ({ip}).");
        WebPanelAudit.Write(user.UserName, "Giriş (panel)", ip: ip);
        return null;
    }

    // Phone login with the same account. Returns the token or the message to show.
    internal async Task<(string? Token, MobileSession? Session, WebPanelUser? User, string? Error)> MobileLoginAsync(
        HttpContext context, DeviceStore devices, string? userName, string? password, string? deviceName)
    {
        string ip = ClientIp(context);
        (WebPanelUser? user, string? error) = await AuthenticateAsync(ip, userName, password, "mobil");
        if (user is null)
        {
            return (null, null, null, error);
        }

        if (!Perm.Has(user, Perm.MobileLogin))
        {
            WebPanelAudit.Write(user.UserName, "Mobil giriş reddedildi (yetki yok)", ip: ip);
            return (null, null, null, "Mobil uygulamaya giriş yetkiniz yok. Yöneticinize başvurun.");
        }

        (string token, MobileSession session) = devices.Add(user, deviceName ?? string.Empty, ip);
        AppLog.Write($"Mobil giriş: {user.UserName} ({session.Name}, {ip}).");
        WebPanelAudit.Write(user.UserName, "Giriş (mobil)", session.Name, "24 saat geçerli", ip);
        return (token, session, user, null);
    }

    // Shared password change. Returns the error or the account's new security stamp.
    internal async Task<(string? Error, int Stamp)> ChangePasswordCoreAsync(WebPanelUser user, string ip, string? currentPassword, string? newPassword, string? repeat)
    {
        DateTime now = _clock();
        if (_failures.TryGetValue(ip, out var state) && now < state.LockedUntilUtc)
        {
            return ($"Çok sayıda hatalı deneme. {Math.Ceiling((state.LockedUntilUtc - now).TotalMinutes)} dakika sonra tekrar deneyin.", 0);
        }

        if (string.IsNullOrEmpty(currentPassword) || currentPassword.Length > 200 || !WebPanelUserStore.VerifyPassword(user, currentPassword))
        {
            await Task.Delay(700);
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
        AppLog.Write($"Şifre değiştirildi: {user.UserName} ({ip}).");
        WebPanelAudit.Write(user.UserName, "Şifre değiştirildi", user.UserName, ip: ip);
        return (null, stamp);
    }

    // Browser password change; the user's other browser sessions and phones are signed out.
    internal async Task<string?> ChangePasswordAsync(HttpContext context, string? currentPassword, string? newPassword, string? repeat)
    {
        if (Current(context) is not (WebPanelSession session, WebPanelUser user))
        {
            return "Oturum kapalı; yeniden giriş yapın.";
        }

        (string? error, int stamp) = await ChangePasswordCoreAsync(user, ClientIp(context), currentPassword, newPassword, repeat);
        if (error is not null)
        {
            return error;
        }

        string currentKey = TokenKey(context.Request.Cookies[CookieName] ?? string.Empty);
        _sessions[currentKey] = session with { Stamp = stamp };
        DropSessions(user.Id, except: currentKey);
        return null;
    }

    // Administrator actions. Each returns null on success or the message to show.
    // A user manager who is not a Yönetici cannot touch Yönetici accounts, cannot create or promote one, cannot change
    // the own role or permissions and cannot grant a permission they do not have themselves.
    internal string? CreateUser(WebPanelUser actor, string ip, string? userName, string? displayName, string? role, string? password, bool mustChange,
        IReadOnlyList<string>? permissions = null, string? fixedPlate = null)
    {
        string name = (userName ?? string.Empty).Trim();
        if (WebPanelUserStore.UserNameProblem(name) is string nameProblem)
        {
            return nameProblem;
        }

        if (CleanPlate(fixedPlate, out string? plate) is string plateProblem)
        {
            return plateProblem;
        }

        if (PanelRoles.Normalize(role) is not string normalizedRole)
        {
            return "Geçerli bir yetki düzeyi seçin.";
        }

        if (actor.Role != PanelRoles.Admin && normalizedRole == PanelRoles.Admin)
        {
            return "Yönetici hesabını yalnızca bir Yönetici oluşturabilir.";
        }

        if (WebPanelUserStore.PasswordProblem(password, name) is string passwordProblem)
        {
            return passwordProblem;
        }

        Dictionary<string, bool> overrides = normalizedRole == PanelRoles.Admin || permissions is null ? [] : Perm.Diff(normalizedRole, permissions);
        if (GrantProblem(actor, [], Perm.Effective(normalizedRole, overrides), normalizedRole, null) is string grantProblem)
        {
            return grantProblem;
        }

        string display = CleanDisplayName(displayName, name);
        int createdId = 0;
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
                MustChangePassword = mustChange,
                FixedPlate = plate
            };
            WebPanelUserStore.SetPassword(created, password!);
            users.Add(created);
            createdId = created.Id;
            return null;
        });
        if (error is not null)
        {
            return error;
        }

        PermissionOverrides.Replace(createdId, overrides, actor.UserName);
        Notify(actor, ip, "Kullanıcı oluşturuldu", name, $"{display}, {normalizedRole}" + OverrideText(overrides)
            + (plate is null ? string.Empty : $", sabit araç {plate}") + (mustChange ? ", ilk girişte şifre değiştirecek" : string.Empty));
        return null;
    }

    // permissions null = leave the per-user permissions as they are (a role change resets them to the new role).
    // fixedPlate null = leave the fixed vehicle as it is, empty = remove it.
    internal string? UpdateUser(WebPanelUser actor, string ip, int id, string? displayName, string? role, bool active,
        IReadOnlyList<string>? permissions = null, string? fixedPlate = null)
    {
        if (PanelRoles.Normalize(role) is not string normalizedRole)
        {
            return "Geçerli bir yetki düzeyi seçin.";
        }

        string? plate = null;
        if (fixedPlate is not null && CleanPlate(fixedPlate, out plate) is string plateProblem)
        {
            return plateProblem;
        }

        WebPanelUser? existing = WebPanelUserStore.Find(id);
        if (existing is null)
        {
            return "Kullanıcı bulunamadı.";
        }

        if (existing.Role == PanelRoles.Customer)
        {
            return "Müşteri hesapları Yönetim > Müşteri hesapları bölümünden düzenlenir.";
        }

        bool actorIsAdmin = actor.Role == PanelRoles.Admin;
        if (!actorIsAdmin && (existing.Role == PanelRoles.Admin || normalizedRole == PanelRoles.Admin))
        {
            return "Yönetici hesaplarını yalnızca bir Yönetici değiştirebilir.";
        }

        bool roleChanges = normalizedRole != existing.Role;
        Dictionary<string, bool> currentOverrides = new(PermissionOverrides.For(id));
        Dictionary<string, bool> overrides = normalizedRole == PanelRoles.Admin ? []
            : permissions is not null ? Perm.Diff(normalizedRole, permissions)
            : roleChanges ? []
            : currentOverrides;
        bool overridesChange = !SameOverrides(overrides, currentOverrides);
        if (!actorIsAdmin && existing.Id == actor.Id && (roleChanges || overridesChange || !active))
        {
            return "Kendi yetki düzeyinizi, yetkilerinizi veya hesap durumunuzu değiştiremezsiniz. Bunu bir Yönetici yapmalı.";
        }

        if (GrantProblem(actor, Perm.Effective(existing.Role, currentOverrides), Perm.Effective(normalizedRole, overrides), normalizedRole, existing.Role) is string grantProblem)
        {
            return grantProblem;
        }

        List<string> changes = [];
        string? error = WebPanelUserStore.Update(users =>
        {
            WebPanelUser? target = users.FirstOrDefault(u => u.Id == id && u.DeletedAt is null);
            if (target is null)
            {
                return "Kullanıcı bulunamadı.";
            }

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
                changes.Add($"yetki düzeyi: {target.Role} → {normalizedRole}");
                target.Role = normalizedRole;
                target.SecurityStamp++;
            }

            if (active != target.IsActive)
            {
                changes.Add(active ? "aktif edildi" : "pasife alındı");
                target.IsActive = active;
                target.SecurityStamp++;
            }

            if (fixedPlate is not null && plate != target.FixedPlate)
            {
                changes.Add(plate is null ? "sabit araç kaldırıldı" : $"sabit araç: {plate}");
                target.FixedPlate = plate;
            }

            return null;
        });

        if (error is not null)
        {
            return error;
        }

        if (overridesChange)
        {
            PermissionOverrides.Replace(id, overrides, actor.UserName);
            changes.Add(overrides.Count == 0 ? "yetkiler rol varsayılanına döndü" : "yetkiler:" + OverrideText(overrides));
        }

        if (changes.Count > 0)
        {
            DropSessions(id, except: null, onlyStale: true);
            Notify(actor, ip, "Kullanıcı güncellendi", existing.UserName, string.Join(", ", changes));
        }

        return null;
    }

    internal string? ResetPassword(WebPanelUser actor, string ip, int id, string? password, bool mustChange)
    {
        WebPanelUser? existing = WebPanelUserStore.Find(id);
        if (existing is null)
        {
            return "Kullanıcı bulunamadı.";
        }

        if (actor.Role != PanelRoles.Admin && existing.Role == PanelRoles.Admin)
        {
            return "Yönetici hesaplarının şifresini yalnızca bir Yönetici sıfırlayabilir.";
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

    // Permanent removal; confirmUserName must repeat the account's user name. Every browser session and phone of the
    // account ends at once and its per-user permissions are dropped. Returns null on success or the message to show;
    // summary describes what was closed.
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
        int phones = devices.RemoveForUser(id);
        if (PermissionOverrides.For(id).Count > 0)
        {
            PermissionOverrides.Replace(id, new Dictionary<string, bool>(), actor.UserName);
        }

        summary = $"{sessions} panel oturumu ve {phones} telefon oturumu kapatıldı";
        Notify(actor, ip, "Kullanıcı silindi", existing.UserName, $"{existing.DisplayName}, {existing.Role}; {summary}");
        return null;
    }

    // Customer portal account for one customer. Only a Yönetici reaches this (panel /yonetim); the customer must exist.
    internal string? CreateCustomerAccount(WebPanelUser actor, string ip, string? userName, string? displayName, int customerId, string customerName,
        string? password, bool mustChange)
    {
        string name = (userName ?? string.Empty).Trim();
        if (WebPanelUserStore.UserNameProblem(name) is string nameProblem)
        {
            return nameProblem;
        }

        if (WebPanelUserStore.PasswordProblem(password, name) is string passwordProblem)
        {
            return passwordProblem;
        }

        string display = CleanDisplayName(displayName, customerName);
        string? error = WebPanelUserStore.Update(users =>
        {
            if (users.Any(u => string.Equals(u.UserName, name, StringComparison.OrdinalIgnoreCase)))
            {
                return "Bu kullanıcı adı zaten var veya silinmiş bir hesaba ait; başka bir ad seçin.";
            }

            WebPanelUser created = new()
            {
                Id = users.Count == 0 ? 1 : users.Max(u => u.Id) + 1,
                UserName = name,
                DisplayName = display,
                Role = PanelRoles.Customer,
                CustomerId = customerId,
                CreatedBy = actor.UserName,
                MustChangePassword = mustChange
            };
            WebPanelUserStore.SetPassword(created, password!);
            users.Add(created);
            return null;
        });
        if (error is null)
        {
            Notify(actor, ip, "Müşteri hesabı oluşturuldu", name, $"{customerName}" + (mustChange ? ", ilk girişte şifre değiştirecek" : string.Empty));
        }

        return error;
    }

    internal string? UpdateCustomerAccount(WebPanelUser actor, string ip, int id, string? displayName, bool active)
    {
        List<string> changes = [];
        string? error = WebPanelUserStore.Update(users =>
        {
            WebPanelUser? target = users.FirstOrDefault(u => u.Id == id && u.DeletedAt is null && u.Role == PanelRoles.Customer);
            if (target is null)
            {
                return "Müşteri hesabı bulunamadı.";
            }

            string display = CleanDisplayName(displayName, target.DisplayName);
            if (display != target.DisplayName)
            {
                changes.Add($"ad: {display}");
                target.DisplayName = display;
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
            Notify(actor, ip, "Müşteri hesabı güncellendi", WebPanelUserStore.Find(id)?.UserName, string.Join(", ", changes));
        }

        return error;
    }

    internal const string CustomerCookieName = "att_musteri";

    internal static string CustomerCookiePath(HttpContext context) => context.Request.PathBase + "/musteri";

    // Portal login: only active customer accounts while the portal feature is on. Returns null or the message to show.
    internal async Task<string?> CustomerLoginAsync(HttpContext context, string? userName, string? password)
    {
        if (!Features.IsOn(Features.CustomerPortal))
        {
            return "Müşteri girişi şu an kapalı.";
        }

        string ip = ClientIp(context);
        (WebPanelUser? user, string? error) = await AuthenticateAsync(ip, userName, password, "müşteri portalı");
        if (user is null)
        {
            return error;
        }

        if (user.Role != PanelRoles.Customer || user.CustomerId is null)
        {
            WebPanelAudit.Write(user.UserName, "Müşteri portalı girişi reddedildi (müşteri hesabı değil)", ip: ip);
            return "Bu adres yalnızca müşteri hesapları içindir. Personel girişi için web panelini kullanın.";
        }

        DateTime now = _clock();
        string token = NewToken();
        _sessions[TokenKey(token)] = new WebPanelSession(user.Id, user.CreatedAt, user.SecurityStamp, now + SessionLifetime, ip);
        context.Response.Cookies.Append(CustomerCookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = IsSecureRequest(context),
            SameSite = SameSiteMode.Strict,
            Path = CustomerCookiePath(context),
            MaxAge = SessionLifetime
        });
        AppLog.Write($"Müşteri portalına giriş: {user.UserName} ({ip}).");
        WebPanelAudit.Write(user.UserName, "Giriş (müşteri portalı)", ip: ip);
        return null;
    }

    // The signed-in customer account, or null when signed out, expired, deactivated, changed or the portal is off.
    internal (WebPanelSession Session, WebPanelUser User)? CurrentCustomer(HttpContext context)
    {
        string token = context.Request.Cookies[CustomerCookieName] ?? string.Empty;
        if (token.Length is 0 or > 100 || !_sessions.TryGetValue(TokenKey(token), out WebPanelSession? session))
        {
            return null;
        }

        WebPanelUser? user = WebPanelUserStore.Find(session.UserId);
        if (session.ExpiresUtc <= _clock() || user is null || !user.IsActive || user.CreatedAt != session.UserCreatedAt)
        {
            _sessions.TryRemove(TokenKey(token), out _);
            return null;
        }

        return user.SecurityStamp == session.Stamp && user.Role == PanelRoles.Customer && user.CustomerId is not null && Features.IsOn(Features.CustomerPortal)
            ? (session, user)
            : null;
    }

    internal void CustomerLogout(HttpContext context)
    {
        string token = context.Request.Cookies[CustomerCookieName] ?? string.Empty;
        if (token.Length > 0 && _sessions.TryRemove(TokenKey(token), out WebPanelSession? session)
            && WebPanelUserStore.Find(session.UserId) is { } user)
        {
            WebPanelAudit.Write(user.UserName, "Çıkış (müşteri portalı)", ip: ClientIp(context));
        }

        context.Response.Cookies.Delete(CustomerCookieName, new CookieOptions { Path = CustomerCookiePath(context) });
    }

    internal async Task<string?> CustomerChangePasswordAsync(HttpContext context, string? currentPassword, string? newPassword, string? repeat)
    {
        if (CurrentCustomer(context) is not (WebPanelSession session, WebPanelUser user))
        {
            return "Oturum kapalı; yeniden giriş yapın.";
        }

        (string? error, int stamp) = await ChangePasswordCoreAsync(user, ClientIp(context), currentPassword, newPassword, repeat);
        if (error is not null)
        {
            return error;
        }

        string currentKey = TokenKey(context.Request.Cookies[CustomerCookieName] ?? string.Empty);
        _sessions[currentKey] = session with { Stamp = stamp };
        DropSessions(user.Id, except: currentKey);
        return null;
    }

    private static string? GrantProblem(WebPanelUser actor, HashSet<string> before, HashSet<string> after, string role, string? previousRole)
    {
        if (actor.Role == PanelRoles.Admin)
        {
            return null;
        }

        // A new Operatör gets the role default and a role change away from Operatör drops them; on an existing Operatör
        // only a Yönetici turns them on or off.
        bool stays = previousRole == PanelRoles.Operator && role == PanelRoles.Operator;
        IReadOnlyList<string> defaults = stays ? [] : Perm.RoleDefaults(role);
        List<string> adminOnly = Perm.AdminChosen
            .Where(k => after.Contains(k) && !before.Contains(k) && !defaults.Contains(k)
                || stays && before.Contains(k) && !after.Contains(k))
            .Select(k => Perm.ByKey[k].Label).ToList();
        if (adminOnly.Count > 0)
        {
            return "Bu yetkiyi yalnızca bir Yönetici açıp kapatabilir: " + string.Join(", ", adminOnly) + ".";
        }

        HashSet<string> mine = Perm.Assigned(actor);
        List<string> notMine = after.Except(before).Where(k => !mine.Contains(k)).Select(k => Perm.ByKey[k].Label).ToList();
        return notMine.Count == 0 ? null : "Kendinizde olmayan yetkiyi veremezsiniz: " + string.Join(", ", notMine) + ".";
    }

    private static bool SameOverrides(IReadOnlyDictionary<string, bool> a, IReadOnlyDictionary<string, bool> b) =>
        a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out bool v) && v == p.Value);

    private static string OverrideText(IReadOnlyDictionary<string, bool> overrides) =>
        overrides.Count == 0 ? string.Empty
            : " " + string.Join(", ", overrides.OrderBy(p => p.Key).Select(p => (p.Value ? "+" : "−") + Perm.ByKey[p.Key].Label));

    internal static void Notify(WebPanelUser actor, string ip, string action, string? target, string detail)
    {
        AppLog.Write($"Web paneli yönetimi: {action} {target} ({detail}) — {actor.UserName}, {ip}.");
        WebPanelAudit.Write(actor.UserName, action, target, detail, ip);
    }

    private static string? CleanPlate(string? fixedPlate, out string? plate)
    {
        try
        {
            plate = ArsanTupTakip.Services.CylinderTracking.NormalizePlate(fixedPlate);
            return null;
        }
        catch (ArgumentException exception)
        {
            plate = null;
            return "Sabit araç: " + exception.Message;
        }
    }

    private static string CleanDisplayName(string? displayName, string fallback)
    {
        string text = string.Join(' ', (displayName ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length == 0 ? fallback : text.Length > 80 ? text[..80] : text;
    }

    private void RegisterFailure(string ip, DateTime now)
    {
        lock (_failureGate)
        {
            var updated = _failures.AddOrUpdate(ip, _ => (1, DateTime.MinValue), (_, old) => (old.Failures + 1, old.LockedUntilUtc));
            if (updated.Failures >= MaxFailuresPerIp)
            {
                _failures[ip] = (0, now + LockTime);
                AppLog.Write($"Giriş: {ip} adresi hatalı denemeler nedeniyle 15 dakika kilitlendi.");
            }

            _recentFailures.Enqueue(now);
            while (_recentFailures.TryPeek(out DateTime oldest) && oldest < now.AddHours(-1))
            {
                _recentFailures.TryDequeue(out _);
            }

            if (_recentFailures.Count >= MaxFailuresPerHour && now >= _globalLockUntilUtc)
            {
                _globalLockUntilUtc = now + LockTime;
                AppLog.Write("Giriş: son bir saatte çok sayıda hatalı deneme; giriş 15 dakika kapatıldı.");
            }
        }
    }

    // The session and its account, or null when signed out, expired (1 hour after login), deactivated, without
    // panel permission or changed since login.
    internal (WebPanelSession Session, WebPanelUser User)? Current(HttpContext context)
    {
        string token = context.Request.Cookies[CookieName] ?? string.Empty;
        if (token.Length is 0 or > 100 || !_sessions.TryGetValue(TokenKey(token), out WebPanelSession? session))
        {
            return null;
        }

        WebPanelUser? user = WebPanelUserStore.Find(session.UserId);
        if (session.ExpiresUtc <= _clock() || user is null || !user.IsActive || user.CreatedAt != session.UserCreatedAt)
        {
            _sessions.TryRemove(TokenKey(token), out _);
            return null;
        }

        // A stale stamp is refused but not removed here: ChangePasswordAsync re-stamps its own session right after saving.
        return user.SecurityStamp == session.Stamp && Perm.Has(user, Perm.PanelLogin) ? (session, user) : null;
    }

    internal void Logout(HttpContext context)
    {
        string token = context.Request.Cookies[CookieName] ?? string.Empty;
        if (token.Length > 0 && _sessions.TryRemove(TokenKey(token), out WebPanelSession? session)
            && WebPanelUserStore.Find(session.UserId) is { } user)
        {
            WebPanelAudit.Write(user.UserName, "Çıkış (panel)", ip: ClientIp(context));
        }

        context.Response.Cookies.Delete(CookieName, new CookieOptions { Path = CookiePath(context) });
    }

    internal int ActiveSessionCount(int userId)
    {
        DateTime now = _clock();
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

    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string TokenKey(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
