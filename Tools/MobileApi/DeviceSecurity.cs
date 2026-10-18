using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ArsanGazERP.MobileApi;

// A signed-in phone. Tokens issued since 1.8.0 carry the panel user, the user's security stamp and an absolute expiry;
// rows without a user are pre-1.8.0 pairings, which no longer grant access and are only kept so the admin sees them.
internal sealed record PairedDevice(
    string Id, string Name, string TokenHash, DateTime CreatedUtc, DateTime? LastSeenUtc,
    int? UserId = null, DateTime? UserCreatedAt = null, int? Stamp = null, DateTime? ExpiresUtc = null, string? Ip = null);

// devices.json. Every login adds its own row with its own random token, so the same user can be signed in on several
// phones at once and a new login never replaces another one.
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

    public IReadOnlyList<PairedDevice> All()
    {
        lock (_gate)
        {
            ReloadIfChanged();
            return [.. _devices];
        }
    }

    public string AddForUser(string name, WebPanelUser user, DateTime nowUtc, TimeSpan lifetime, string ip, out PairedDevice device)
    {
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        device = new(Guid.NewGuid().ToString("N"), name, Hash(token), nowUtc, nowUtc, user.Id, user.CreatedAt, user.SecurityStamp, nowUtc + lifetime, ip);
        lock (_gate)
        {
            ReloadIfChanged();
            _devices.RemoveAll(d => d.ExpiresUtc is DateTime expires && expires < nowUtc);
            _devices.Add(device);
            Save();
        }

        return token;
    }

    public PairedDevice? Validate(string token)
    {
        if (token.Length is 0 or > 200)
        {
            return null;
        }

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

    public bool UpdateStamp(string id, int stamp) => Change(devices =>
    {
        int index = devices.FindIndex(d => d.Id == id);
        if (index < 0)
        {
            return false;
        }

        devices[index] = devices[index] with { Stamp = stamp };
        return true;
    });

    public bool Remove(string id) => Change(devices => devices.RemoveAll(device => device.Id == id) > 0);

    public int RemoveForUser(int userId, string? exceptId) =>
        Change(devices => devices.RemoveAll(d => d.UserId == userId && d.Id != exceptId));

    private T Change<T>(Func<List<PairedDevice>, T> change)
    {
        lock (_gate)
        {
            ReloadIfChanged();
            int before = _devices.Count;
            List<PairedDevice> snapshot = [.. _devices];
            T result = change(_devices);
            if (_devices.Count != before || !_devices.SequenceEqual(snapshot))
            {
                Save();
            }

            return result;
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
        string temporary = MobileApiPaths.DevicesFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_devices, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, MobileApiPaths.DevicesFile, overwrite: true);
        _loadedWriteUtc = LastWriteUtc();
    }
}

// Endpoint metadata: any signed-in user may call it (no specific permission needed).
internal sealed class SignedInOnly;

// Endpoint metadata: the handler additionally checks tup-hareket / tup-cikis / tup-iade for each cylinder it moves.
internal sealed class MovePermissionChecked;

// Checks the phone's token, the user behind it, the 24-hour expiry, the forced password change and the endpoint's
// permission on every /api call. Endpoints without permission metadata are refused.
internal sealed class DeviceAuthMiddleware(RequestDelegate next, DeviceStore devices, WebPanelAuth auth)
{
    internal static bool IsPublic(PathString path) =>
        path.StartsWithSegments("/api/pair")
        || path.StartsWithSegments("/api/merhaba")
        || path.StartsWithSegments("/api/guncelleme")
        || path.StartsWithSegments("/api/giris");

    public async Task InvokeAsync(HttpContext context)
    {
        PathString path = context.Request.Path;
        if (!path.StartsWithSegments("/api") || IsPublic(path))
        {
            await next(context);
            return;
        }

        string header = context.Request.Headers.Authorization.ToString();
        PairedDevice? device = header.StartsWith("Bearer ", StringComparison.Ordinal)
            ? devices.Validate(header["Bearer ".Length..].Trim())
            : null;
        (WebPanelUser? user, string? error) = auth.CheckDevice(device, devices);
        if (user is null)
        {
            await Task.Delay(device is null ? 400 : 0);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error, loginRequired = true });
            return;
        }

        EndpointMetadataCollection? metadata = context.GetEndpoint()?.Metadata;
        if (user.MustChangePassword && metadata?.GetMetadata<PanelOpenBeforePasswordChange>() is null)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "Devam etmeden önce şifrenizi değiştirmelisiniz.", mustChangePassword = true });
            return;
        }

        if (PermissionProblem(metadata, user) is string problem)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = problem, forbidden = true });
            return;
        }

        context.Items["device"] = device;
        context.Items["panelAccount"] = user;
        context.Items["panelUser"] = user.UserName;
        await next(context);
    }

    // Shared with the panel filter: null when allowed, otherwise the message to show.
    internal static string? PermissionProblem(EndpointMetadataCollection? metadata, WebPanelUser user)
    {
        if (metadata?.GetMetadata<PanelPermission>() is { } required)
        {
            string[] keys = [required.Name, .. required.OrAny ?? []];
            if (keys.Any(key => PanelRoles.Has(user, key)))
            {
                return null;
            }

            FeatureInfo? feature = keys.Select(Features.OwnerOf).FirstOrDefault(f => f is not null && !Features.IsOn(f.Key));
            return feature is not null
                ? $"\"{feature.Label}\" özelliği şu an kapalı. Yönetici web panelde Yönetim > Özellikler bölümünden açabilir."
                : MissingPermission(required.Name);
        }

        return metadata?.GetMetadata<SignedInOnly>() is not null ? null : "Bu işlem tanımlı bir yetkiye bağlı değil; erişim kapalı.";
    }

    internal static string MissingPermission(string key) =>
        $"Bu işlem için yetkiniz yok ({PanelPermissions.Catalog.FirstOrDefault(p => p.Key == key)?.Label ?? key}). Yöneticinizden yetki isteyin.";

    // Moves need a permission that depends on where each cylinder is now; null when all of them are allowed.
    internal static async Task<string?> MoveProblemAsync(WebPanelUser user, IEnumerable<string?> codes, ArsanGazERP.Models.CylinderStatus target)
    {
        ArsanGazERP.Services.CrudService crud = new();
        foreach (string code in ArsanGazERP.Services.CylinderTracking.CleanCodes(codes))
        {
            if (await crud.FindCylinderByCodeAsync(code) is { } cylinder)
            {
                string needed = PanelPermissions.ForMove((int)cylinder.Status, (int)target);
                if (!PanelRoles.Has(user, needed))
                {
                    return MissingPermission(needed) + $" Tüp: {cylinder.SerialNumber}.";
                }
            }
        }

        return null;
    }
}

internal static class TelegramNotifier
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    // Set by --self-test so test logins never reach the real chat; the texts are kept in Captured instead.
    internal static bool Disabled { get; set; }

    internal static System.Collections.Concurrent.ConcurrentQueue<string> Captured { get; } = new();

    public static void Send(string text)
    {
        if (Disabled)
        {
            Captured.Enqueue(text);
            return;
        }

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
