using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ArsanGazERP.MobileApi;

internal sealed record PairedDevice(string Id, string Name, string TokenHash, DateTime CreatedUtc, DateTime? LastSeenUtc);

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

    private static DateTime LastWriteUtc() =>
        File.Exists(MobileApiPaths.DevicesFile) ? File.GetLastWriteTimeUtc(MobileApiPaths.DevicesFile) : DateTime.MinValue;

    private void ReloadIfChanged()
    {
        DateTime current = LastWriteUtc();
        if (current != _loadedWriteUtc)
        {
            _devices = Load();
            _loadedWriteUtc = current;
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _devices.Count;
            }
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

    public bool Remove(string id)
    {
        lock (_gate)
        {
            ReloadIfChanged();
            int removed = _devices.RemoveAll(device => device.Id == id);
            if (removed > 0)
            {
                Save();
            }

            return removed > 0;
        }
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static List<PairedDevice> Load()
    {
        try
        {
            return File.Exists(MobileApiPaths.DevicesFile)
                ? JsonSerializer.Deserialize<List<PairedDevice>>(File.ReadAllText(MobileApiPaths.DevicesFile)) ?? []
                : [];
        }
        catch (JsonException exception)
        {
            MobileLog.Write("Cihaz listesi okunamadı; eşleşmeler sıfırlandı: " + exception.Message);
            return [];
        }
    }

    private void Save()
    {
        string temporary = MobileApiPaths.DevicesFile + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_devices, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, MobileApiPaths.DevicesFile, overwrite: true);
        _loadedWriteUtc = LastWriteUtc();
    }
}

internal sealed class PairingService(DeviceStore devices)
{
    private const int MaximumAttempts = 5;
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RequestInterval = TimeSpan.FromSeconds(60);

    private readonly Lock _gate = new();
    private string? _code;
    private DateTime _expiresUtc;
    private int _attempts;
    private DateTime _lastRequestUtc = DateTime.MinValue;
    private int _requestsToday;
    private DateOnly _requestDay;

    public string RequestCode()
    {
        string code;
        lock (_gate)
        {
            DateTime now = DateTime.UtcNow;
            if (now - _lastRequestUtc < RequestInterval)
            {
                return "Eşleştirme kodu az önce gönderildi; Telegram'ı kontrol edin veya 1 dakika sonra tekrar deneyin.";
            }

            DateOnly today = DateOnly.FromDateTime(now);
            if (_requestDay != today)
            {
                _requestDay = today;
                _requestsToday = 0;
            }

            if (++_requestsToday > 10)
            {
                return "Bugün için eşleştirme kodu sınırına ulaşıldı.";
            }

            code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
            _code = code;
            _expiresUtc = now + CodeLifetime;
            _attempts = 0;
            _lastRequestUtc = now;
        }

        string? serverUrl = MobileConnectionHub.ReadPublicUrl();
        string addressLine = serverUrl is { Length: > 0 }
            ? $"\nSunucu adresi (uygulamada Bağlantı > Telegram'dan bağlan):\n{serverUrl}"
            : string.Empty;
        TelegramNotifier.Send(
            $"📱 Mobil ERP eşleştirme kodu: {code}\n" +
            "10 dakika geçerli, tek kullanımlık. Bu isteği siz yapmadıysanız kodu kimseyle paylaşmayın." +
            addressLine);
        MobileLog.Write("Eşleştirme kodu Telegram'a gönderildi.");
        return "Eşleştirme kodu Telegram'a gönderildi.";
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
                MobileLog.Write("Eşleştirme kodu çok fazla hatalı denemeden sonra iptal edildi.");
                return null;
            }

            string candidate = code.Trim();
            bool matches = candidate.Length == _code.Length && CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(candidate),
                Encoding.ASCII.GetBytes(_code));
            if (!matches)
            {
                return null;
            }

            _code = null;
        }

        string name = string.IsNullOrWhiteSpace(deviceName) ? "Telefon" : deviceName.Trim()[..Math.Min(deviceName.Trim().Length, 60)];
        string token = devices.Add(name);
        TelegramNotifier.Send($"✅ Mobil ERP'ye yeni cihaz eşleşti: {name}. Bu siz değilseniz bilgisayarda devices.json dosyasından cihazı silin.");
        MobileLog.Write("Yeni cihaz eşleşti: " + name);
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

internal static class TelegramNotifier
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static void Send(string text)
    {
        if (!OperatingSystem.IsWindows()
            && Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN") is { Length: > 0 } botToken
            && Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID") is { Length: > 0 } chatIdText)
        {
            _ = SendDirectAsync(botToken, chatIdText, text);
            return;
        }

        try
        {
            string data = Path.GetDirectoryName(ArsanGazERP.Services.AgentWatcherPaths.SettingsFile)!;
            long? chatId = ArsanGazERP.Services.AgentChatService.GetTelegramNotificationChatId();
            if (chatId is null)
            {
                MobileLog.Write("Telegram chat ID yapılandırılmadığı için bildirim gönderilemedi.");
                return;
            }

            string outbox = Path.Combine(data, "TelegramOutbox");
            Directory.CreateDirectory(outbox);
            string id = "mobile-api-" + Guid.NewGuid().ToString("N");
            string now = DateTime.UtcNow.ToString("o");
            var item = new
            {
                Id = id,
                Kind = "telegram-copilot-direct-response",
                TaskId = id,
                ChatId = chatId.Value,
                Report = text,
                CreatedUtc = now,
                NextAttemptUtc = now,
                Attempts = 0,
                DeliveredChunkCount = 0,
                Receipts = Array.Empty<object>(),
                FilePaths = Array.Empty<string>(),
                DeliveredFileCount = 0,
                LastError = (string?)null
            };
            string destination = Path.Combine(outbox, id + ".json");
            File.WriteAllText(destination + ".tmp", JsonSerializer.Serialize(item), new UTF8Encoding(false));
            File.Move(destination + ".tmp", destination);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            MobileLog.Write("Telegram bildirimi kuyruğa yazılamadı: " + exception.GetBaseException().Message);
        }
    }

    // The Linux server has no AgentWatcher outbox, so notifications go straight to the Bot API.
    private static async Task SendDirectAsync(string botToken, string chatId, string text)
    {
        try
        {
            using FormUrlEncodedContent body = new(new Dictionary<string, string>
            {
                ["chat_id"] = chatId,
                ["text"] = text.Length > 4000 ? text[..4000] : text,
                ["disable_web_page_preview"] = "true"
            });
            using HttpResponseMessage response = await Http.PostAsync($"https://api.telegram.org/bot{botToken}/sendMessage", body);
            if (!response.IsSuccessStatusCode)
            {
                MobileLog.Write($"Telegram bildirimi gönderilemedi (HTTP {(int)response.StatusCode}).");
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            MobileLog.Write("Telegram bildirimi gönderilemedi: " + exception.GetBaseException().Message);
        }
    }
}
