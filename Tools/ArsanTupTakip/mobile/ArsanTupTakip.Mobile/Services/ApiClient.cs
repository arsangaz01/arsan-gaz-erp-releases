using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArsanTupTakip.Mobile.Services;

public sealed record CustomerItem(int Id, string Code, string Name, string? City, string? Phone, bool IsActive, int Cylinders)
{
    public string Subtitle => $"{Code} · {City ?? "-"} · müşteride {Cylinders} tüp";
}

public sealed record CustomerCreated(string Message, CustomerItem Customer);


// Version is the server's row version; sending it back lets the server refuse a move if someone else changed the cylinder meanwhile.
public sealed record CylinderInfo(int Id, string Barcode, string SerialNumber, int Status, string StatusName, string? Customer, string Detail, uint? Version = null);

// The signed-in user as the server sees it; Permissions decide which tabs and buttons are shown.
public sealed record AccountInfo(
    string User,
    string DisplayName,
    string Role,
    string RoleText,
    List<string> Permissions,
    bool MustChangePassword,
    DateTime ExpiresUtc,
    string Device,
    string? FixedPlate = null)
{
    public const string DriverRole = "Şoför";

    // Driver layout on the home screen: the Şoför role, or someone who may run trips but neither register cylinders,
    // record movements nor manage vehicles.
    public bool IsDriver => Role == DriverRole
        || (Can(Perm.TripStart) && !CanAny([Perm.CylinderRegister, Perm.VehicleAdd, Perm.Summary, Perm.Reports, .. Perm.AnyMove]));

    // Same role and permissions (ignoring order) and the same fixed vehicle.
    public bool SameAccess(AccountInfo other) =>
        Role == other.Role && FixedPlate == other.FixedPlate && MustChangePassword == other.MustChangePassword
        && Permissions.Order(StringComparer.Ordinal).SequenceEqual(other.Permissions.Order(StringComparer.Ordinal));

    public bool Can(string permission) => Permissions.Contains(permission);

    public bool CanAny(params string[] permissions) => permissions.Any(Can);
}

public sealed record SimulationUser(int Id, string UserName, string DisplayName, string Role, bool IsActive, string? FixedPlate)
{
    public string Text => $"{DisplayName} ({UserName}) · {Role}" + (IsActive ? string.Empty : " · pasif");
}

public sealed record SimulationPermission(string Key, string Label, string Description, bool Granted, bool RoleDefault);

public sealed record SimulationGroup(string Name, List<SimulationPermission> Permissions);

public sealed record SimulationView(
    int Id, string User, string DisplayName, string Role, string RoleText, bool IsActive, string? FixedPlate, bool Editable,
    List<string> Permissions, List<string> Changes, List<SimulationGroup> Groups);

// Permission keys (same as the server catalog).
public static class Perm
{
    public const string Summary = "ozet";
    public const string Reports = "raporlar";
    public const string CylinderView = "tup-goruntule";
    public const string CylinderRegister = "tup-kayit";
    public const string CylinderAudit = "tup-sayim";
    public const string StockCount = "depo-sayim";
    public const string FillRecord = "dolum-kaydi";
    public const string MoveFullness = "hareket-dolu-bos";
    public const string MoveBulk = "hareket-toplu";
    public const string MoveOverride = "hareket-cakisma";
    public const string MovementHistory = "hareket-gecmisi";
    public const string Export = "disa-aktar";
    public const string CustomerView = "musteri-goruntule";
    public const string CustomerAdd = "musteri-ekle";
    public const string TripView = "sefer-goruntule";
    public const string TripStart = "sefer-baslat";
    public const string TripLoad = "sefer-yukle";
    public const string TripDeliver = "sefer-teslim";
    public const string TripReturn = "sefer-donus";
    public const string TripClose = "sefer-kapat";
    public const string VehicleView = "arac-goruntule";
    public const string VehicleAdd = "arac-ekle";
    public const string VehicleEdit = "arac-duzenle";
    public const string VehicleDeactivate = "arac-pasif";

    public static readonly IReadOnlyDictionary<int, string> MoveByStatus = new Dictionary<int, string>
    {
        [1] = "hareket-iade", [2] = "hareket-arac", [3] = "hareket-teslim", [4] = "hareket-dolum", [5] = "hareket-servis", [6] = "hareket-hurda"
    };

    public static readonly string[] AnyMove = [.. MoveByStatus.Values];

    public static readonly string[] CustomerPicker = [CustomerView, CustomerAdd, CylinderView, CylinderRegister, CylinderAudit, MovementHistory, "hareket-teslim", TripDeliver];
}
public sealed record TripStopItem(int Id, int Position, string Plate, int CustomerId, string CustomerName, string? Address, string? District, string? City, string? Phone,
    bool Done, DateTime? DoneAt, int Delivered, int Collected, string? Note, string? MapUrl);
public sealed record TripStopSuggestionItem(int CustomerId, string CustomerName, string? City, string Reason);
public sealed record FillLineItem(string Code, bool Ok, string Message, bool TestOverdue);
public sealed record FillResponse(string Message, int Filled, List<FillLineItem> Lines, List<string> NeedsTestConfirmation);
public sealed record FillItem(int Id, int CylinderId, string SerialNumber, string GasType, string FilledAt, string FilledBy, string? LotNumber, string? Note, bool TestOverdue);
public sealed record CylinderStatusCount(int Status, string Name, int Total, int Full, int Empty);
public sealed record CylinderPlateCount(string Plate, int Count);
public sealed record CylinderOverview(List<CylinderStatusCount> Statuses, List<CylinderPlateCount> Vehicles, int LongAtCustomer, int LongAtCustomerDays);

public sealed record CustomerStockTotals(int Count, int Full, int Empty, int Unknown, int CustomerOwned, int? MaxDays, int? AvgDays, int Long, DateTime? LastDelivery);

public sealed record CustomerStockGroupItem(string GasType, string Capacity, int Count, int Full, int Empty, int? MinDays, int? MaxDays, int? AvgDays, int Long)
{
    public string Title => $"{GasType} {Capacity}";

    public string Subtitle => $"dolu {Full} · boş {Empty} · {DaysRange(MinDays, MaxDays)}" + (Long > 0 ? $" · ⚠ {Long} tüp uzun süredir" : string.Empty);

    internal static string DaysRange(int? min, int? max) =>
        max is null ? "teslim tarihi yok" : min == max ? $"{max} gün" : $"{min}–{max} gün";
}

public sealed record CustomerStockCylinderItem(
    int Id,
    string SerialNumber,
    string Barcode,
    string? ManufacturerSerial,
    string GasType,
    string Capacity,
    string Fullness,
    string? Since,
    int? Days,
    bool CustomerOwned,
    string? TestDue,
    bool TestOverdue,
    bool NeedsReview)
{
    public string Title => $"{(NeedsReview ? "⚠ " : string.Empty)}{SerialNumber} · {GasType} {Capacity}";

    public string Subtitle => string.Join(" · ", new[]
    {
        Fullness == "-" ? null : Fullness,
        Since is null ? "teslim tarihi yok" : $"teslim {Since}",
        Days is int days ? $"{days} gündür" : null,
        TestDue is null ? null : (TestOverdue ? "test geçmiş " : "test ") + TestDue,
        CustomerOwned ? "müşteri malı" : null
    }.Where(part => !string.IsNullOrEmpty(part)));

    public string Codes => string.Join(" · ", new[]
    {
        Barcode == SerialNumber ? null : "QR " + Barcode,
        ManufacturerSerial is null ? null : "üretici seri " + ManufacturerSerial
    }.Where(part => !string.IsNullOrEmpty(part)));
}

public sealed record CustomerStockResponse(
    int Id,
    string Name,
    string Code,
    string? City,
    string? District,
    string? Phone,
    bool IsActive,
    int LongDays,
    CustomerStockTotals Totals,
    List<CustomerStockGroupItem> Groups,
    List<CustomerStockCylinderItem> Cylinders);

public sealed record CylinderListItem(
    string SerialNumber,
    string Barcode,
    string GasType,
    string Capacity,
    int Status,
    string StatusName,
    string Fullness,
    string? Customer,
    string? Plate,
    int? DaysThere,
    string? TestDue)
{
    public string Title => $"{SerialNumber} · {GasType} {Capacity}";

    public string Subtitle => string.Join(" · ", new[]
    {
        StatusName,
        Fullness == "-" ? null : Fullness,
        Customer,
        Plate,
        DaysThere is int days ? $"{days} gündür" : null,
        TestDue is null ? null : "test " + TestDue
    }.Where(part => !string.IsNullOrEmpty(part)));
}

// Read-only answer of /api/tup/bilgi for the "Tüp Bilgisi" screen.
public sealed record CylinderStatusInfo(
    string SerialNumber,
    string Barcode,
    string GasType,
    string Capacity,
    int Status,
    string StatusName,
    bool? IsFull,
    string Fullness,
    string Location,
    string? Customer,
    string? Plate,
    string? Trip,
    int? DaysThere,
    string? LastMovementDate,
    string? LastMovementType,
    string? TestDue,
    bool TestOverdue,
    bool IsActive,
    string? ManufacturerSerial = null,
    string? ProductName = null,
    bool NeedsReview = false);

public sealed record CylinderBulkResult(string Code, bool Ok, string Message);
public sealed record CylinderBulkResponse(string Message, int Ok, int Failed, List<CylinderBulkResult> Results);
public sealed record CylinderAuditResponse(string Message, int Expected, int Found, List<string> Missing, List<string> Unexpected, List<string> Unknown);

public sealed record StockCountGroupInfo(string Title, string GasType, string Capacity, int Expected, int Counted, int Matched, int Missing, int Extra, int Full, int Empty);

public sealed record StockCountCylinderInfo(int Id, string SerialNumber, string Barcode, string Group, string Fullness, string Place, string? At);

public sealed record StockCountUnknownInfo(string Code, string At, string ScannedBy);

public sealed record StockCountGroupCylinder(int Id, string SerialNumber, string Barcode, string GasType, string Capacity, string Fullness, string Place, string? At, string? ScannedBy);

public sealed record StockCountGroupDetailInfo(string Title, List<StockCountGroupCylinder> Matched, List<StockCountGroupCylinder> Missing, List<StockCountGroupCylinder> Extra);

public sealed record StockCountInfo(
    int Id,
    string Location,
    string Started,
    string StartedBy,
    bool Open,
    int Expected,
    int Counted,
    int Matched,
    int Missing,
    int Extra,
    int Unknown,
    string Text,
    List<StockCountGroupInfo> Groups,
    List<StockCountCylinderInfo> Recent,
    List<StockCountUnknownInfo> UnknownCodes);

public sealed record StockCountListInfo(int Id, string Location, string Started, string StartedBy, string? Closed, bool Open,
    int Expected, int Counted, int Matched, int Missing, int Extra, int Unknown);

public sealed record StockCountActiveResponse(StockCountInfo? Count);

public sealed record StockCountResponse(string Message, StockCountInfo? Count);

public sealed record StockCountScanResponse(string Outcome, string Message, string? Group, StockCountInfo Count);

public sealed record TripVehicleSummary(string Plate, int LoadedCount, bool OnTrip, DateTime? ReturnedAt);

public sealed record ActiveTripSummary(
    int Id,
    string Title,
    DateTime StartedAt,
    string? Note,
    List<TripVehicleSummary> Vehicles,
    int LoadedTotal,
    int DeliveredTotal,
    int ReturnedTotal,
    bool RequireReasons = false);

public sealed record TripGasShortage(string GasType, int Count);

public sealed record TripReconcileResponse(
    string Message,
    bool HasAlerts,
    List<string> NotDeliveredNotReturned,
    List<TripGasShortage> ShortagesByGas,
    List<string> UnexpectedReturnCodes,
    List<string> LoadedButNotAccountedFor);

public sealed record TripStartResponse(int Id, string Title, DateTime StartedAt, string Message);

public sealed record TripLoadResponse(string Message, int Added, int Skipped, List<string> Messages);

public sealed record TripCloseResponse(string Message, int TripId, TripReconcileResponse Reconcile);

public sealed record TripMissingReasonItem(string Key, string Label, bool NoteRequired, bool AsksCustomer = false);

public sealed record TripOpenCylinder(
    int CylinderId, string SerialNumber, string GasType, string Plate, bool Missing, string? Reason, string? ReasonLabel, string? Note, string? ReasonBy, bool Checked);

public sealed record VehicleItem(
    int Id,
    string Plate,
    string? Name,
    string? VehicleType,
    string? DriverName,
    int? CylinderCapacity,
    string? Note,
    bool IsActive,
    int CylindersOnBoard,
    int? ActiveTripId,
    string? ActiveTripTitle,
    string Created,
    uint Version = 0)
{
    public string Caption => string.Join(" · ", new[] { Name, VehicleType, DriverName is null ? null : "şoför " + DriverName }.Where(x => !string.IsNullOrEmpty(x)));

    public string Title => "🚚 " + Plate + (IsActive ? string.Empty : " (pasif)");

    public string Subtitle => string.Join(" · ", new[]
    {
        Caption.Length == 0 ? null : Caption,
        $"araçta {CylindersOnBoard} tüp" + (CylinderCapacity is int max ? $" / {max}" : string.Empty),
        ActiveTripTitle is null ? null : "seferde: " + ActiveTripTitle
    }.Where(x => !string.IsNullOrEmpty(x)));
}

public sealed record LoadCylinder(
    int CylinderId,
    string SerialNumber,
    string Barcode,
    string GasType,
    string Capacity,
    string Fullness,
    string StatusName,
    string? Customer,
    string? Plate,
    string? LastMovement,
    string? TestDue,
    bool TestOverdue,
    string? Loaded,
    string State,
    bool Alarm)
{
    public string Title => (Alarm ? "⚠ " : string.Empty) + SerialNumber;

    public string Subtitle => string.Join(Environment.NewLine, new[]
    {
        $"{GasType} · {Capacity} · {Fullness}",
        State + (State == StatusName ? string.Empty : $" · {StatusName}"),
        Customer is null ? null : "Müşteri: " + Customer,
        Plate is null ? null : "Araç: " + Plate,
        Loaded is null ? null : "Yükleme: " + Loaded,
        LastMovement is null ? null : "Son hareket: " + LastMovement,
        TestDue is null ? null : "Test tarihi: " + TestDue + (TestOverdue ? " (geçmiş!)" : string.Empty)
    }.Where(x => !string.IsNullOrEmpty(x)));
}

public sealed record GasGroup(string GasType, int Count, int Full, int Empty, int Alarm, string Summary, List<LoadCylinder> Cylinders);

public sealed record VehicleTrip(int TripId, string Title, bool Active, string Started, string? Closed, string? VehicleReturned, int Loaded, int Delivered, int Returned, int Missing);

public sealed record VehicleDetailResponse(VehicleItem Vehicle, List<GasGroup> OnBoard, string? CapacityWarning, List<VehicleTrip> Trips);

public sealed record TripLoadView(int TripId, string Title, bool Active, string? Plate, int Total, int OnVehicle, int Missing, List<GasGroup> Groups);

public sealed record VehicleSaved(string Message, int Id, string Plate);

public sealed record VehicleForm(string? Plate, string? Name, string? VehicleType, string? DriverName, int? CylinderCapacity, string? Note, bool? IsActive, uint? Version = null);
public sealed record UpdateInfo(
    string ApiVersion,
    string PublishedMobileVersion,
    int PublishedMobileBuild,
    bool ApkReady,
    long? ApkSizeBytes,
    string DownloadUrl);

public class ApiException(string message, int? statusCode = null, string? content = null) : Exception(message)
{
    public int? StatusCode { get; } = statusCode;

    public string? Content { get; } = content;

    // 409 with "stale": another user changed the record after it was read; reload and try again.
    public bool IsStale => StatusCode == 409 && Flag("stale");

    public bool IsCustomerConflict => StatusCode == 409 && !IsStale;

    public bool MustChangePassword => StatusCode == 403 && Flag("mustChangePassword");

    private bool Flag(string name)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(Content ?? "{}");
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // The record a new-customer request collided with (same name), or null for other errors.
    public CustomerItem? ExistingCustomer()
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(Content ?? "{}");
            return StatusCode == 409 && document.RootElement.TryGetProperty("existing", out JsonElement existing)
                ? existing.Deserialize<CustomerItem>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Codes the server refused because the cylinder is registered to another customer.
    public IReadOnlyList<string> ConflictCodes()
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(Content ?? "{}");
            return document.RootElement.TryGetProperty("conflicts", out JsonElement conflicts) && conflicts.ValueKind == JsonValueKind.Array
                ? conflicts.EnumerateArray()
                    .Select(c => c.TryGetProperty("code", out JsonElement code) ? code.GetString() : null)
                    .OfType<string>()
                    .ToList()
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

// The server could not be reached; the request never got an answer.
public sealed class ServerUnreachableException(string message) : ApiException(message);

// The login expired (24 hours) or was closed; the app has already switched to the login screen.
public sealed class SessionExpiredException(string message, string? content) : ApiException(message, 401, content);

public sealed class ApiClient
{
    private const string ServerKey = "server_url";
    private const string TokenKey = "session_token";
    private const string LegacyTokenKey = "device_token";
    private const string AccountKey = "session_account";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(200) };
    private string? _token;

    public static ApiClient Instance { get; } = new();

    // Raised (once per expiry) when the server refuses the session; the message is shown on the login screen.
    public static event Action<string>? SessionEnded;

    private ApiClient()
    {
        SecureStorage.Default.Remove(LegacyTokenKey);
        try
        {
            string json = Preferences.Default.Get(AccountKey, string.Empty);
            _account = json.Length == 0 ? null : JsonSerializer.Deserialize<AccountInfo>(json, Json);
        }
        catch (JsonException)
        {
            _account = null;
        }
    }

    private AccountInfo? _account;

    // While a Yönetici simulates another user, screens are drawn with that user's permissions; requests still use
    // the Yönetici's own session. Kept in memory only, so restarting the app always ends it. The comparison is the
    // role name the server sends ("Yönetici").
    public SimulationView? Simulation { get; private set; }

    // Raised on start, end and permission changes; the home screen compares it to redraw itself when shown again.
    public int SimulationVersion { get; private set; }

    public AccountInfo? Account => Simulation is { } s && _account is { } real
        ? real with { User = s.User, DisplayName = s.DisplayName, Role = s.Role, RoleText = s.RoleText, Permissions = s.Permissions, MustChangePassword = false, FixedPlate = s.FixedPlate }
        : _account;

    // The signed-in user, also during a simulation.
    public AccountInfo? RealAccount => _account;

    public bool IsAdmin => _account?.Role == "Yönetici";

    public void StartSimulation(SimulationView view)
    {
        Simulation = view;
        SimulationVersion++;
    }

    public void EndSimulation()
    {
        if (Simulation is not null)
        {
            Simulation = null;
            SimulationVersion++;
        }
    }

    public Task<List<SimulationUser>> SimulationUsersAsync() => GetAsync<List<SimulationUser>>("/api/simulasyon/kullanicilar");

    public Task<SimulationView> SimulationViewAsync(int userId) => GetAsync<SimulationView>("/api/simulasyon/" + userId);

    public async Task<SimulationView> SetSimulationPermissionAsync(int userId, string key, bool granted)
    {
        SimulationView view = await PostAsync<SimulationView>($"/api/simulasyon/{userId}/yetki", new { key, granted });
        if (Simulation?.Id == userId)
        {
            StartSimulation(view);
        }

        return view;
    }

    public bool Can(string permission) => Account?.Can(permission) == true;

    public bool CanAny(params string[] permissions) => Account?.CanAny(permissions) == true;

    public string ServerUrl
    {
        get => Preferences.Default.Get(ServerKey, string.Empty);
        set => Preferences.Default.Set(ServerKey, NormalizeUrl(value));
    }

    // A stored login that has not reached its 24-hour limit; the server still has the final word.
    public async Task<bool> HasSessionAsync() =>
        !string.IsNullOrEmpty(await GetTokenAsync()) && _account is { } account && account.ExpiresUtc > DateTime.UtcNow;

    public async Task<AccountInfo> LoginAsync(string userName, string password)
    {
        string device = $"{DeviceInfo.Current.Manufacturer} {DeviceInfo.Current.Model}".Trim();
        using HttpResponseMessage response = await SendAsync(HttpMethod.Post, "/api/giris",
            new { kullanici = userName.Trim(), sifre = password, cihazAdi = device }, authorize: false);
        JsonElement json = await ReadAsync<JsonElement>(response);
        string token = json.GetProperty("token").GetString() ?? throw new ApiException("Sunucu oturum anahtarı döndürmedi.");
        AccountInfo account = json.GetProperty("account").Deserialize<AccountInfo>(Json) ?? throw new ApiException("Sunucu kullanıcı bilgisini döndürmedi.");
        await SecureStorage.Default.SetAsync(TokenKey, token);
        _token = token;
        SetAccount(account);
        return account;
    }

    public async Task<AccountInfo> RefreshAccountAsync()
    {
        AccountInfo account = await GetAsync<AccountInfo>("/api/ben");
        SetAccount(account);
        return account;
    }

    public async Task<string> ChangePasswordAsync(string current, string newPassword, string repeat)
    {
        string message = await PostMessageAsync("/api/sifre", new { mevcut = current, yeni = newPassword, tekrar = repeat });
        await RefreshAccountAsync();
        return message;
    }

    public async Task LogoutAsync()
    {
        try
        {
            if (!string.IsNullOrEmpty(await GetTokenAsync()))
            {
                using HttpResponseMessage response = await SendAsync(HttpMethod.Post, "/api/cikis", new { });
            }
        }
        catch (ApiException)
        {
        }

        ClearSession();
    }

    public void ClearSession()
    {
        SecureStorage.Default.Remove(TokenKey);
        Preferences.Default.Remove(AccountKey);
        _token = null;
        _account = null;
        Simulation = null;
    }

    private void SetAccount(AccountInfo account)
    {
        _account = account;
        if (account.Role != "Yönetici")
        {
            Simulation = null;
        }

        Preferences.Default.Set(AccountKey, JsonSerializer.Serialize(account, Json));
    }

    public static string NormalizeUrl(string value)
    {
        string url = value.Trim().TrimEnd('/');
        if (url.Length > 0 && !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }

        return url;
    }

    public async Task<string> HelloAsync()
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Get, "/api/merhaba", null, authorize: false);
        return (await ReadAsync<JsonElement>(response)).GetProperty("version").GetString() ?? "?";
    }

    public async Task<UpdateInfo> GetUpdateInfoAsync()
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Get, "/api/guncelleme", null, authorize: false);
        JsonElement json = await ReadAsync<JsonElement>(response);
        return new UpdateInfo(
            json.GetProperty("apiVersion").GetString() ?? "?",
            json.GetProperty("publishedMobileVersion").GetString() ?? "?",
            json.GetProperty("publishedMobileBuild").GetInt32(),
            json.GetProperty("apkReady").GetBoolean(),
            json.TryGetProperty("apkSizeBytes", out JsonElement size) && size.ValueKind != JsonValueKind.Null
                ? size.GetInt64()
                : null,
            json.GetProperty("downloadUrl").GetString() ?? "/indir");
    }

    public async Task TryRefreshServerUrlFromPcAsync()
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Get, "/api/yapilandirma", null);
        JsonElement json = await ReadAsync<JsonElement>(response);
        if (json.TryGetProperty("serverUrl", out JsonElement urlElement)
            && urlElement.ValueKind == JsonValueKind.String)
        {
            string? url = urlElement.GetString();
            if (!string.IsNullOrWhiteSpace(url))
            {
                ServerUrl = url;
            }
        }

        if (json.TryGetProperty("autoManaged", out JsonElement auto) && auto.ValueKind == JsonValueKind.True)
        {
            ServerUrlCoordinator.AutoManaged = true;
        }
    }

    public Task<string> TextAsync(string path) => GetTextAsync(path);

    public async Task<List<CustomerItem>> CustomersAsync(string query) =>
        await GetAsync<List<CustomerItem>>("/api/musteriler?q=" + Uri.EscapeDataString(query));

    public Task<CustomerStockResponse> CustomerStockAsync(int customerId) => GetAsync<CustomerStockResponse>("/api/musteri/" + customerId + "/stok");

    public Task<CustomerCreated> CreateCustomerAsync(object customer) => PostAsync<CustomerCreated>("/api/musteri", customer);

    public async Task<CylinderInfo> CylinderAsync(string code) =>
        await GetAsync<CylinderInfo>("/api/tup?kod=" + Uri.EscapeDataString(code));

    public Task<CylinderStatusInfo> CylinderStatusInfoAsync(string code) =>
        GetAsync<CylinderStatusInfo>("/api/tup/bilgi?kod=" + Uri.EscapeDataString(code));

    // requestKey: the same key for the same logical movement (also when it is resent from the offline queue), so a
    // request that reached the server but whose answer was lost is not applied twice.
    public Task<string> MoveCylinderAsync(string code, int status, int? customerId, string? note, string? plate = null, bool? isFull = null,
        bool confirmCustomerChange = false, uint? version = null, string? requestKey = null) =>
        PostMessageAsync("/api/tup/hareket", new { code, status, customerId, note, plate, isFull, confirmCustomerChange, version }, requestKey);

    public Task<CylinderBulkResponse> MoveCylindersAsync(IReadOnlyList<string> codes, int status, int? customerId, string? note, string? plate, bool? isFull,
        bool confirmCustomerChange = false, string? requestKey = null) =>
        PostAsync<CylinderBulkResponse>("/api/tup/toplu-hareket", new { codes, status, customerId, note, plate, isFull, confirmCustomerChange }, requestKey);

    public static string NewRequestKey() => Guid.NewGuid().ToString("N");

    public Task<CylinderOverview> CylinderOverviewAsync() => GetAsync<CylinderOverview>("/api/tup/durum");

    public Task<List<CylinderListItem>> CylinderListAsync(int? status, string? plate, bool longAtCustomer)
    {
        List<string> query = [];
        if (status is int value)
        {
            query.Add("durum=" + value);
        }

        if (!string.IsNullOrEmpty(plate))
        {
            query.Add("plaka=" + Uri.EscapeDataString(plate));
        }

        if (longAtCustomer)
        {
            query.Add("uzun=true");
        }

        return GetAsync<List<CylinderListItem>>("/api/tup/liste" + (query.Count == 0 ? string.Empty : "?" + string.Join("&", query)));
    }

    public Task<List<string>> VehiclePlatesAsync() => GetAsync<List<string>>("/api/araclar");

    public Task<List<string>> GasTypesAsync() => GetAsync<List<string>>("/api/tup/gaz-turleri");

    public Task<string> RegisterCylinderAsync(string serialNumber, string? barcode, string gasType, decimal capacity, string capacityUnit, bool? isFull, DateTime? testDueDate, int? customerId = null) =>
        PostMessageAsync("/api/tup/yeni", new { serialNumber, barcode, gasType, capacity, capacityUnit, isFull, testDueDate = testDueDate?.ToString("yyyy-MM-dd"), customerId });

    public Task<CylinderAuditResponse> AuditCustomerAsync(int customerId, IReadOnlyList<string> codes, string? note) =>
        PostAsync<CylinderAuditResponse>("/api/tup/sayim", new { customerId, codes, note });

    public Task<StockCountActiveResponse> ActiveStockCountAsync() => GetAsync<StockCountActiveResponse>("/api/depo-sayim/aktif");

    public Task<StockCountResponse> StartStockCountAsync(string? note) =>
        PostAsync<StockCountResponse>("/api/depo-sayim/baslat", new { note });

    public Task<StockCountScanResponse> ScanStockCountAsync(int id, string code, DateTime? at = null) =>
        PostAsync<StockCountScanResponse>($"/api/depo-sayim/{id}/okut", new { code, at });

    public Task<StockCountGroupDetailInfo> StockCountGroupAsync(int id, string gasType, string capacity) =>
        GetAsync<StockCountGroupDetailInfo>($"/api/depo-sayim/{id}/grup?gaz={Uri.EscapeDataString(gasType)}&boyut={Uri.EscapeDataString(capacity)}");

    public Task<List<StockCountListInfo>> StockCountsAsync() => GetAsync<List<StockCountListInfo>>("/api/depo-sayim/liste");

    // File download (Excel/PDF); the name comes from the server's Content-Disposition.
    public async Task<(byte[] Bytes, string FileName)> DownloadAsync(string path, string fallbackName)
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Get, path, null);
        if (!response.IsSuccessStatusCode)
        {
            await ReadAsync<JsonElement>(response);
        }

        byte[] bytes = await response.Content.ReadAsByteArrayAsync();
        string? name = response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"');
        return (bytes, string.IsNullOrWhiteSpace(name) ? fallbackName : Path.GetFileName(name));
    }

    public Task<StockCountResponse> UndoStockCountAsync(int id, string code) =>
        PostAsync<StockCountResponse>($"/api/depo-sayim/{id}/geri-al", new { code });

    public Task<StockCountResponse> CloseStockCountAsync(int id) =>
        PostAsync<StockCountResponse>($"/api/depo-sayim/{id}/bitir", new { });

    public Task<List<ActiveTripSummary>> ActiveTripsAsync() => GetAsync<List<ActiveTripSummary>>("/api/sefer/aktif");

    public Task<TripStartResponse> StartTripAsync(string? note, IReadOnlyList<string>? plates) =>
        PostAsync<TripStartResponse>("/api/sefer/baslat", new { note, plates });

    public Task<string> AddTripVehicleAsync(int tripId, string plate) =>
        PostMessageAsync("/api/sefer/arac", new { tripId, plate });

    public Task<TripLoadResponse> LoadTripCylindersAsync(int tripId, string plate, IReadOnlyList<string> codes) =>
        PostAsync<TripLoadResponse>("/api/sefer/yukle", new { tripId, plate, codes });

    public Task<string> DeliverTripCylindersAsync(int tripId, int customerId, IReadOnlyList<string> codes, string? note) =>
        PostMessageAsync("/api/sefer/teslim", new { tripId, customerId, codes, note });

    public Task<FillResponse> FillAsync(IReadOnlyList<string> codes, string? lot, string? note, bool allowOverdueTest) =>
        PostAsync<FillResponse>("/api/dolum", new { codes, lot, note, allowOverdueTest }, Guid.NewGuid().ToString("N"));

    public Task<List<FillItem>> RecentFillsAsync() => GetAsync<List<FillItem>>("/api/dolum/son?gun=1");

    public Task<List<TripStopItem>> TripRouteAsync(int tripId, string plate) =>
        GetAsync<List<TripStopItem>>("/api/sefer/" + tripId + "/rota?plaka=" + Uri.EscapeDataString(plate));

    public Task<List<TripStopSuggestionItem>> TripStopSuggestionsAsync(int tripId, string plate) =>
        GetAsync<List<TripStopSuggestionItem>>("/api/sefer/" + tripId + "/rota/oneriler?plaka=" + Uri.EscapeDataString(plate));

    public Task<string> AddTripStopAsync(int tripId, string plate, int customerId) =>
        PostMessageAsync("/api/sefer/rota/ekle", new { tripId, plate, customerId });

    public Task<string> MoveTripStopAsync(int tripId, int stopId, int direction) =>
        PostMessageAsync("/api/sefer/rota/sira", new { tripId, stopId, direction });

    public Task<string> RemoveTripStopAsync(int tripId, int stopId) =>
        PostMessageAsync("/api/sefer/rota/sil", new { tripId, stopId });

    public Task<string> SetTripStopDoneAsync(int tripId, int stopId, bool done) =>
        PostMessageAsync("/api/sefer/rota/tamam", new { tripId, stopId, done });

    public Task<string> PickupTripCylindersAsync(int tripId, string plate, int customerId, IReadOnlyList<string> codes, string? note) =>
        PostMessageAsync("/api/sefer/bos-alim", new { tripId, plate, customerId, codes, note });

    public Task<TripReconcileResponse> ReturnTripCylindersAsync(int tripId, string plate, IReadOnlyList<string> codes, string? note) =>
        PostAsync<TripReconcileResponse>("/api/sefer/donus", new { tripId, plate, codes, note });

    public Task<TripReconcileResponse> CheckTripAsync(int tripId, string? plate) =>
        GetAsync<TripReconcileResponse>("/api/sefer/" + tripId + "/kontrol" + (string.IsNullOrEmpty(plate) ? string.Empty : "?plaka=" + Uri.EscapeDataString(plate)));

    public Task<TripCloseResponse> CloseTripAsync(int tripId) =>
        PostAsync<TripCloseResponse>("/api/sefer/kapat", new { tripId });

    public Task<List<TripMissingReasonItem>> TripMissingReasonsAsync() => GetAsync<List<TripMissingReasonItem>>("/api/sefer/gerekceler");

    public Task<List<TripOpenCylinder>> TripOpenCylindersAsync(int tripId) => GetAsync<List<TripOpenCylinder>>("/api/sefer/" + tripId + "/eksikler");

    public Task<string> ExplainTripMissingAsync(int tripId, IReadOnlyList<int> cylinderIds, string reason, string? note, int? customerId = null) =>
        PostMessageAsync("/api/sefer/gerekce", new { tripId, cylinderIds, reason, note, customerId });

    public Task<TripLoadView> TripLoadAsync(int tripId, string? plate) =>
        GetAsync<TripLoadView>("/api/sefer/" + tripId + "/yuk" + (string.IsNullOrEmpty(plate) ? string.Empty : "?plaka=" + Uri.EscapeDataString(plate)));

    public Task<List<VehicleItem>> VehiclesAsync(bool includeInactive) =>
        GetAsync<List<VehicleItem>>("/api/arac-kayit" + (includeInactive ? "?pasif=true" : string.Empty));

    public Task<VehicleDetailResponse> VehicleDetailAsync(int id) => GetAsync<VehicleDetailResponse>("/api/arac-kayit/" + id);

    public Task<VehicleSaved> SaveVehicleAsync(int? id, VehicleForm form) =>
        PostAsync<VehicleSaved>("/api/arac-kayit" + (id is int value ? "/" + value : string.Empty), form);

    private async Task<T> PostAsync<T>(string path, object body, string? requestKey = null)
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Post, path, body, requestKey: requestKey);
        return await ReadAsync<T>(response);
    }

    private async Task<string> GetTextAsync(string path)
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Get, path, null);
        return (await ReadAsync<JsonElement>(response)).GetProperty("text").GetString() ?? string.Empty;
    }

    private async Task<string> PostMessageAsync(string path, object body, string? requestKey = null)
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Post, path, body, requestKey: requestKey);
        return (await ReadAsync<JsonElement>(response)).GetProperty("message").GetString() ?? "Kaydedildi.";
    }

    private async Task<T> GetAsync<T>(string path)
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Get, path, null);
        return await ReadAsync<T>(response);
    }

    private async Task<string?> GetTokenAsync() => _token ??= await SecureStorage.Default.GetAsync(TokenKey);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, bool authorize = true, string? requestKey = null)
    {
        string server = ServerUrl;
        if (server.Length == 0)
        {
            throw new ApiException("Sunucu adresi girilmemiş. Giriş ekranındaki sunucu adresini kontrol edin.");
        }

        string? token = null;
        if (authorize)
        {
            token = await GetTokenAsync();
            if (string.IsNullOrEmpty(token))
            {
                EndSession("Oturum açık değil. Kullanıcı adı ve şifrenizle giriş yapın.", null);
            }
        }

        // One key per logical request; the retry below reuses it so the server never applies it twice.
        string? key = method == HttpMethod.Post && authorize ? requestKey ?? NewRequestKey() : null;
        HttpRequestMessage Build(string baseUrl)
        {
            HttpRequestMessage request = new(method, baseUrl + path);
            if (token is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            if (key is not null)
            {
                request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
            }

            if (body is not null)
            {
                request.Content = JsonContent.Create(body, options: Json);
            }

            return request;
        }

        HttpResponseMessage response;
        try
        {
            using HttpRequestMessage request = Build(server);
            response = await _http.SendAsync(request);
        }
        catch (Exception exception) when (exception is HttpRequestException or WebException or IOException)
        {
            await ServerUrlCoordinator.TryRecoverAfterFailureAsync();
            if (string.Equals(server, ServerUrl, StringComparison.OrdinalIgnoreCase))
            {
                throw new ServerUnreachableException("Sunucuya ulaşılamadı. İnternet bağlantınızı ve sunucu adresini kontrol edin. (" + exception.Message + ")");
            }

            try
            {
                using HttpRequestMessage retry = Build(ServerUrl);
                response = await _http.SendAsync(retry);
            }
            catch (Exception retryException) when (retryException is HttpRequestException or WebException or IOException or TaskCanceledException)
            {
                throw new ServerUnreachableException("Sunucuya yeni adresten de ulaşılamadı. (" + retryException.Message + ")");
            }
        }
        catch (TaskCanceledException)
        {
            throw new ServerUnreachableException("Sunucu zamanında yanıt vermedi.");
        }

        if (authorize && response.StatusCode == HttpStatusCode.Unauthorized)
        {
            string content = await response.Content.ReadAsStringAsync();
            response.Dispose();
            EndSession(ErrorText(content) ?? "Oturumunuz sona erdi. Kullanıcı adı ve şifrenizle yeniden giriş yapın.", content);
        }

        return response;
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private void EndSession(string message, string? content)
    {
        bool hadSession = Account is not null || _token is not null;
        ClearSession();
        if (hadSession)
        {
            MainThread.BeginInvokeOnMainThread(() => SessionEnded?.Invoke(message));
        }

        throw new SessionExpiredException(message, content);
    }

    private static string? ErrorText(string content)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(content);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("error", out JsonElement value)
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        string content = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            string? error = null;
            try
            {
                using JsonDocument document = JsonDocument.Parse(content);
                error = document.RootElement.TryGetProperty("error", out JsonElement value) ? value.GetString() : null;
            }
            catch (JsonException)
            {
            }

            if (error is null && response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadGateway
                    or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout or (HttpStatusCode)530)
            {
                throw new ServerUnreachableException(response.StatusCode == HttpStatusCode.NotFound
                    ? "Sunucu bulunamadı (404). Bağlantı ekranındaki sunucu adresini kontrol edin."
                    : "ArsanTüpTakip sunucusuna ulaşılamıyor. Sunucu kapalı veya adres yanlış olabilir.");
            }

            throw new ApiException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => error ?? "Kullanıcı adı veya şifre hatalı.",
                _ => error ?? $"Sunucu hatası ({(int)response.StatusCode})."
            }, (int)response.StatusCode, content);
        }

        try
        {
            return JsonSerializer.Deserialize<T>(content, Json) ?? throw new ApiException("Sunucudan boş yanıt geldi.");
        }
        catch (JsonException)
        {
            throw new ApiException("Sunucu yanıtı anlaşılamadı. Adres doğru mu?");
        }
    }
}
