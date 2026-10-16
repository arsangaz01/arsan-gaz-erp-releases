using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ArsanTupTakip.Api;

// Roles are presets of the permissions in Perm; per-user overrides are applied on top (except for Yönetici).
internal static class PanelRoles
{
    internal const string Admin = "Yönetici";
    internal const string Operator = "Operatör";
    internal const string Viewer = "İzleyici";
    internal const string Driver = "Şoför";
    internal static readonly string[] All = [Admin, Operator, Viewer, Driver];

    // Customer portal accounts (WebPanelUser.CustomerId). Deliberately not in All and not accepted by Normalize: they
    // have no staff permission at all, cannot be made from the staff user editor and only open /musteri.
    internal const string Customer = "Müşteri";

    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    internal static string Description(string role) => role switch
    {
        Admin => "Her şey, her zaman: tüm işlemler, kullanıcı/yetki ve cihaz yönetimi, denetim kaydı. Yöneticinin yetkileri kısıtlanamaz.",
        Operator => "Varsayılan: tüp, hareket, sefer, araç ve müşteri işlemleri, raporlar, Excel; yönetim yok. Tek tek değiştirilebilir. Depoda aracı boşaltma ve seferi kapatma yalnızca Operatör'de olabilir; bunları yalnızca Yönetici açıp kapatır.",
        Viewer => "Varsayılan: yalnızca görüntüleme, raporlar ve Excel; kayıt değiştiremez. Tek tek yetki eklenebilir.",
        Driver => "Yalnızca mobil: sevkiyat (sefer başlat, yükle, müşteriye teslim, boş alım), tüp bilgisi ve depo sayımı. Panel girişi, tüp kaydı, hareket, müşteri stoku, araç, rapor ve yönetim yok. Sabit aracı varsa sefer o araçla başlar. Aracı boşaltma ve seferi kapatma depoda operatörün işidir; şoföre verilemez. Tek tek yetki eklenip çıkarılabilir.",
        _ => string.Empty
    };

    internal static string? Normalize(string? role)
    {
        string text = new((role ?? string.Empty).Trim().ToUpper(Turkish).Select(c => c switch
        {
            'İ' => 'I', 'Ö' => 'O', 'Ü' => 'U', 'Ç' => 'C', 'Ş' => 'S', 'Ğ' => 'G', _ => c
        }).ToArray());
        return text switch
        {
            "YONETICI" or "ADMIN" => Admin,
            "OPERATOR" => Operator,
            "IZLEYICI" or "VIEWER" => Viewer,
            "SOFOR" or "DRIVER" => Driver,
            _ => null
        };
    }
}

internal sealed class WebPanelUser
{
    public int Id { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Role { get; set; } = PanelRoles.Viewer;
    public string Salt { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
    public int Iterations { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime? LastLoginAt { get; set; }
    public string? LastLoginIp { get; set; }
    public DateTime? PasswordChangedAt { get; set; }
    public bool MustChangePassword { get; set; }

    // Normalized plate of the vehicle this user always drives; trips of the user start with it and no other vehicle.
    public string? FixedPlate { get; set; }

    // Only for PanelRoles.Customer: the customer whose cylinders the portal account sees.
    public int? CustomerId { get; set; }

    // Raised on deactivation, role change and password change; sessions carrying an older value are refused.
    public int SecurityStamp { get; set; }

    // Deleted accounts stay in the file without a usable password so their user name and id are never handed to a new
    // account (movements and the audit log refer to users by name). WebPanelUserStore.All() leaves them out.
    public DateTime? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }
}

// Panel accounts in <data folder>/web-panel-users.json. Only PBKDF2 hashes are stored.
internal static class WebPanelUserStore
{
    private const int HashIterations = 210_000;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly Regex UserNamePattern = new("^[A-Za-z0-9._-]{3,40}$", RegexOptions.CultureInvariant);
    private static readonly object Gate = new();
    private static List<WebPanelUser> _cache = [];
    private static (DateTime WriteUtc, long Length) _cacheStamp;

    internal static string FilePath => Path.Combine(AppPaths.Root, "web-panel-users.json");

    internal static IReadOnlyList<WebPanelUser> All()
    {
        lock (Gate)
        {
            Refresh();
            return _cache;
        }
    }

    internal static WebPanelUser? Find(int id) => All().FirstOrDefault(u => u.Id == id);

    internal static WebPanelUser? FindByName(string? userName) =>
        All().FirstOrDefault(u => string.Equals(u.UserName, (userName ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase));

    internal static bool IsConfigured => All().Any(u => u.IsActive && u.Role == PanelRoles.Admin);

    // Applies a change to a fresh copy read from disk and saves it; the named mutex also covers the command-line tools.
    internal static T Update<T>(Func<List<WebPanelUser>, T> change)
    {
        using Mutex mutex = new(false, @"Local\ArsanTupTakip.WebPanelUsers");
        bool owned = false;
        try
        {
            try
            {
                owned = mutex.WaitOne(TimeSpan.FromSeconds(10));
            }
            catch (AbandonedMutexException)
            {
                owned = true;
            }

            lock (Gate)
            {
                List<WebPanelUser> users = ReadFile() ?? [];
                T result = change(users);
                string temp = FilePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(users, Json));
                File.Move(temp, FilePath, overwrite: true);
                _cache = Live(users);
                _cacheStamp = FileStamp();
                return result;
            }
        }
        finally
        {
            if (owned)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    internal static void SetPassword(WebPanelUser user, string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, HashIterations, HashAlgorithmName.SHA256, 32);
        user.Salt = Convert.ToBase64String(salt);
        user.Hash = Convert.ToBase64String(hash);
        user.Iterations = HashIterations;
        user.PasswordChangedAt = DateTime.UtcNow;
        user.SecurityStamp++;
    }

    internal static bool VerifyPassword(WebPanelUser user, string password)
    {
        byte[] expected = Convert.FromBase64String(user.Hash);
        byte[] actual = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), Convert.FromBase64String(user.Salt), user.Iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    // Spends the same time as a real check so unknown user names cannot be told apart by response time.
    internal static void VerifyDummy(string password) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), new byte[16], HashIterations, HashAlgorithmName.SHA256, 32);

    internal static string? UserNameProblem(string? userName) =>
        UserNamePattern.IsMatch(userName ?? string.Empty) ? null : "Kullanıcı adı 3-40 karakter olmalı; yalnızca harf (Türkçe karakter olmadan), rakam, nokta, tire ve alt çizgi içerebilir.";

    internal static string? PasswordProblem(string? password, string userName)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 8 || password.Length > 200)
        {
            return "Şifre 8-200 karakter olmalı.";
        }

        if (!password.Any(char.IsLetter) || !password.Any(char.IsDigit))
        {
            return "Şifre en az bir harf ve bir rakam içermeli.";
        }

        return userName.Length >= 3 && password.Contains(userName, StringComparison.OrdinalIgnoreCase)
            ? "Şifre kullanıcı adını içermemeli."
            : null;
    }

    // Removes the account for good: it can no longer sign in, is left out of every list and its name stays reserved.
    internal static void MarkDeleted(WebPanelUser user, string actor)
    {
        user.DeletedAt = DateTime.UtcNow;
        user.DeletedBy = actor;
        user.IsActive = false;
        user.MustChangePassword = false;
        user.Salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        user.Hash = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        user.Iterations = HashIterations;
        user.SecurityStamp++;
    }

    private static List<WebPanelUser> Live(List<WebPanelUser> users) => users.Where(u => u.DeletedAt is null).ToList();

    // Used once by the installer: creates the first Yönetici, who must change the password at first login.
    internal static string? CreateInitialAdmin(string userName, string password)
    {
        if (UserNameProblem(userName) is string nameProblem)
        {
            return nameProblem;
        }

        if (PasswordProblem(password, userName) is string passwordProblem)
        {
            return passwordProblem;
        }

        return Update(users =>
        {
            if (users.Any(u => u.IsActive && u.Role == PanelRoles.Admin))
            {
                return "Zaten aktif bir yönetici var; ilk yönetici yeniden oluşturulmadı.";
            }

            if (users.Any(u => string.Equals(u.UserName, userName, StringComparison.OrdinalIgnoreCase)))
            {
                return "Bu kullanıcı adı zaten var.";
            }

            WebPanelUser admin = new()
            {
                Id = users.Count == 0 ? 1 : users.Max(u => u.Id) + 1,
                UserName = userName,
                DisplayName = userName,
                Role = PanelRoles.Admin,
                CreatedBy = "Kurulum",
                MustChangePassword = true
            };
            SetPassword(admin, password);
            users.Add(admin);
            return (string?)null;
        });
    }

    private static void Refresh()
    {
        (DateTime, long) stamp = FileStamp();
        if (stamp == _cacheStamp)
        {
            return;
        }

        try
        {
            _cache = Live(ReadFile() ?? []);
            _cacheStamp = stamp;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            AppLog.Write("Web paneli kullanıcı dosyası okunamadı: " + exception.Message);
        }
    }

    private static (DateTime, long) FileStamp()
    {
        FileInfo info = new(FilePath);
        return info.Exists ? (info.LastWriteTimeUtc, info.Length) : (DateTime.MinValue, 0);
    }

    private static List<WebPanelUser>? ReadFile()
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return JsonSerializer.Deserialize<List<WebPanelUser>>(File.ReadAllText(FilePath), Json) ?? [];
            }
            catch (IOException) when (attempt < 3)
            {
                Thread.Sleep(50);
            }
        }
    }

}

internal sealed record WebPanelAuditEntry(DateTime At, string Actor, string Action, string? Target, string? Detail, string? Ip);

// Append-only log of logins and administrative actions: <data folder>/web-panel-audit.jsonl
internal static class WebPanelAudit
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal static string FilePath => Path.Combine(AppPaths.Root, "web-panel-audit.jsonl");

    internal static void Write(string actor, string action, string? target = null, string? detail = null, string? ip = null)
    {
        string line = JsonSerializer.Serialize(new WebPanelAuditEntry(DateTime.UtcNow, Trim(actor, 60) ?? "?", action, Trim(target, 80), Trim(detail, 300), Trim(ip, 64)), Json);
        lock (Gate)
        {
            try
            {
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch (IOException exception)
            {
                AppLog.Write("Panel denetim kaydı yazılamadı: " + exception.Message);
            }
        }
    }

    internal static List<WebPanelAuditEntry> Latest(int take)
    {
        Queue<string> lines = new();
        lock (Gate)
        {
            if (!File.Exists(FilePath))
            {
                return [];
            }

            foreach (string line in File.ReadLines(FilePath))
            {
                lines.Enqueue(line);
                if (lines.Count > take)
                {
                    lines.Dequeue();
                }
            }
        }

        List<WebPanelAuditEntry> entries = [];
        foreach (string line in lines.Reverse())
        {
            try
            {
                if (JsonSerializer.Deserialize<WebPanelAuditEntry>(line, Json) is { } entry)
                {
                    entries.Add(entry);
                }
            }
            catch (JsonException)
            {
            }
        }

        return entries;
    }

    private static string? Trim(string? value, int max)
    {
        if (value is null)
        {
            return null;
        }

        string text = value.Replace('\r', ' ').Replace('\n', ' ');
        return text.Length > max ? text[..max] : text;
    }
}
