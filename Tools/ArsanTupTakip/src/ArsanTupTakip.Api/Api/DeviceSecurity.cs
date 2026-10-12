using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ArsanTupTakip.Api;

internal sealed record PairedDevice(string Id, string Name, string TokenHash, DateTime CreatedUtc, DateTime? LastSeenUtc);

// Phones allowed to call /api, stored in <data folder>/devices.json. Only SHA-256 hashes of the access tokens are kept.
internal sealed class DeviceStore
{
    private readonly Lock _gate = new();
    private List<PairedDevice> _devices;
    private DateTime _loadedWriteUtc;

    public DeviceStore()
    {
        _devices = Load();
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
            _devices = Load();
            _loadedWriteUtc = current;
        }
    }

    public IReadOnlyList<PairedDevice> All()
    {
        lock (_gate)
        {
            ReloadIfChanged();
            return [.. _devices];
        }
    }

    public string Add(string name)
    {
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        PairedDevice device = new(Guid.NewGuid().ToString("N"), name, Hash(token), DateTime.UtcNow, null);
        lock (_gate)
        {
            ReloadIfChanged();
            _devices.Add(device);
            Save();
        }

        return token;
    }

    public PairedDevice? Validate(string token)
    {
        byte[] candidate = Convert.FromHexString(Hash(token));
        lock (_gate)
        {
            ReloadIfChanged();
            PairedDevice? match = null;
            foreach (PairedDevice device in _devices)
            {
                if (CryptographicOperations.FixedTimeEquals(candidate, Convert.FromHexString(device.TokenHash)))
                {
                    match = device;
                }
            }

            if (match is not null && (match.LastSeenUtc is null || DateTime.UtcNow - match.LastSeenUtc > TimeSpan.FromMinutes(10)))
            {
                int index = _devices.IndexOf(match);
                match = match with { LastSeenUtc = DateTime.UtcNow };
                _devices[index] = match;
                Save();
            }

            return match;
        }
    }

    public PairedDevice? Remove(string id)
    {
        lock (_gate)
        {
            ReloadIfChanged();
            PairedDevice? device = _devices.FirstOrDefault(d => d.Id == id);
            if (device is not null)
            {
                _devices.Remove(device);
                Save();
            }

            return device;
        }
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static List<PairedDevice> Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<List<PairedDevice>>(File.ReadAllText(FilePath)) ?? []
                : [];
        }
        catch (JsonException exception)
        {
            AppLog.Write("Cihaz listesi okunamadı; eşleşmeler sıfırlandı: " + exception.Message);
            return [];
        }
    }

    private void Save()
    {
        string temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_devices, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, FilePath, overwrite: true);
        _loadedWriteUtc = LastWriteUtc();
    }
}

internal sealed record PairingCode(string Code, DateTime ExpiresUtc);

// A Yönetici creates a single-use 6-digit code in the panel (Yönetim > Cihazlar); the phone sends it to /api/pair.
internal sealed class PairingService(DeviceStore devices)
{
    internal const int MaximumAttempts = 5;
    internal static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);

    private readonly Lock _gate = new();
    private string? _code;
    private DateTime _expiresUtc;
    private int _attempts;

    internal const string RequestMessage =
        "Eşleştirme kodunu yönetici web panelinde Yönetim > Cihazlar bölümünden oluşturur. Kodu yöneticinizden alıp buraya girin.";

    public PairingCode CreateCode()
    {
        lock (_gate)
        {
            _code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
            _expiresUtc = DateTime.UtcNow + CodeLifetime;
            _attempts = 0;
            return new PairingCode(_code, _expiresUtc);
        }
    }

    public void CancelCode()
    {
        lock (_gate)
        {
            _code = null;
        }
    }

    public DateTime? ActiveCodeExpiresUtc()
    {
        lock (_gate)
        {
            return _code is not null && DateTime.UtcNow <= _expiresUtc ? _expiresUtc : null;
        }
    }

    public string? TryPair(string code, string deviceName)
    {
        lock (_gate)
        {
            if (_code is null || DateTime.UtcNow > _expiresUtc)
            {
                _code = null;
                return null;
            }

            if (++_attempts > MaximumAttempts)
            {
                _code = null;
                AppLog.Write("Eşleştirme kodu çok fazla hatalı denemeden sonra iptal edildi.");
                return null;
            }

            string candidate = (code ?? string.Empty).Trim();
            bool matches = candidate.Length == _code.Length && CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(candidate),
                Encoding.ASCII.GetBytes(_code));
            if (!matches)
            {
                return null;
            }

            _code = null;
        }

        string trimmed = (deviceName ?? string.Empty).Trim();
        string name = trimmed.Length == 0 ? "Telefon" : trimmed[..Math.Min(trimmed.Length, 60)];
        string token = devices.Add(name);
        AppLog.Write("Yeni cihaz eşleşti: " + name);
        WebPanelAudit.Write("cihaz", "Cihaz eşleşti", name);
        return token;
    }
}

internal sealed class DeviceAuthMiddleware(RequestDelegate next, DeviceStore devices)
{
    public async Task InvokeAsync(HttpContext context)
    {
        PathString path = context.Request.Path;
        if (!path.StartsWithSegments("/api")
            || path.StartsWithSegments("/api/pair")
            || path.StartsWithSegments("/api/merhaba")
            || path.StartsWithSegments("/api/guncelleme"))
        {
            await next(context);
            return;
        }

        string header = context.Request.Headers.Authorization.ToString();
        PairedDevice? device = header.StartsWith("Bearer ", StringComparison.Ordinal)
            ? devices.Validate(header["Bearer ".Length..].Trim())
            : null;
        if (device is null)
        {
            await Task.Delay(400);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Cihaz eşleşmemiş veya erişim anahtarı geçersiz." });
            return;
        }

        context.Items["device"] = device;
        await next(context);
    }
}
