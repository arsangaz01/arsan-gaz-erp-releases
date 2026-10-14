using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArsanGazMobile.Services;

public sealed record CustomerItem(int Id, string Code, string Name, string? City, string? Phone, bool IsActive, int Cylinders)
{
    public string Subtitle => $"{Code} · {City ?? "-"} · müşteride {Cylinders} tüp";
}

public sealed record CustomerCreated(string Message, CustomerItem Customer);

public sealed record CrmItem(int Id, string Name, string? City, string? Sector, string? Phone, string Status, int Score, string? FollowUp, int? FollowUpDays)
{
    public string Subtitle => $"{City ?? "-"} · {Status} · puan {Score}";

    public string FollowUpText => FollowUpDays switch
    {
        null => string.Empty,
        < 0 => $"Takip {FollowUp} · {-FollowUpDays} gün gecikti",
        0 => "Takip bugün",
        _ => $"Takip {FollowUp} · {FollowUpDays} gün kaldı"
    };
}

public sealed record CylinderInfo(int Id, string Barcode, string SerialNumber, int Status, string StatusName, string? Customer, string Detail);
public sealed record CylinderCard(
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
    int? TestDays,
    bool IsActive);

public sealed record CylinderStatusCount(int Status, string Name, int Total, int Full, int Empty);
public sealed record CylinderPlateCount(string Plate, int Count);
public sealed record CylinderOverview(List<CylinderStatusCount> Statuses, List<CylinderPlateCount> Vehicles, int LongAtCustomer, int LongAtCustomerDays);

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

public sealed record CylinderBulkResult(string Code, bool Ok, string Message);
public sealed record CylinderBulkResponse(string Message, int Ok, int Failed, List<CylinderBulkResult> Results);
public sealed record CylinderAuditResponse(string Message, int Expected, int Found, List<string> Missing, List<string> Unexpected, List<string> Unknown);

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
    int PickedUpTotal = 0,
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

public sealed record TripLoadResponse(string Message, int Added, int Skipped, List<string> Messages, List<string>? Refused = null);

public sealed record TripCloseResponse(string Message, int TripId, TripReconcileResponse Reconcile);

public sealed record TripMissingReasonItem(string Key, string Label, bool NoteRequired, bool AsksCustomer = false);

public sealed record TripOpenCylinder(
    int CylinderId, string SerialNumber, string GasType, string Plate, bool Missing, string? Reason, string? ReasonLabel, string? Note, string? ReasonBy, bool Checked,
    bool IsPickup = false, string? PickupCustomer = null);

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
    string Created)
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
    public string Title => (Alarm ? "⚠ " : string.Empty) + Barcode + (Barcode == SerialNumber ? string.Empty : $" (seri {SerialNumber})");

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

public sealed record VehicleForm(string? Plate, string? Name, string? VehicleType, string? DriverName, int? CylinderCapacity, string? Note, bool? IsActive);
public sealed record ChatTurn(string Role, string Content);
public sealed record AgentReply(string Agent, string Role, string Text, bool Ok);

public sealed record CursorQueued(string Id);

public sealed record CursorStatus(string Id, string Status, string? Stage, string? Report);

public sealed record UpdateInfo(
    string ApiVersion,
    string PublishedMobileVersion,
    int PublishedMobileBuild,
    bool ApkReady,
    long? ApkSizeBytes,
    string DownloadUrl);

public sealed record HubStatus(
    bool ErpRunning,
    bool WatcherRunning,
    bool AgentHostRunning,
    bool TelegramConfigured,
    bool CursorBridgeActive,
    string? CursorBridgeStage,
    bool MobileTunnelReady,
    string? PublicServerUrl,
    string MobileApiVersion,
    string IntegrationHint);

public sealed record CampaignStatusInfo(
    string? Email,
    string? EmailIdentity,
    string? EmailSetupHint,
    bool WhatsAppConfigured,
    string WhatsAppHint,
    bool ExecutionEnabled,
    int MaxEmailAttachmentKb,
    string OptOut);

public sealed record NameCount(string Name, int Count);

public sealed record CampaignFilters(int Total, int WithEmail, int WithMobile, List<NameCount> Cities, List<NameCount> Sectors, List<NameCount> Statuses);

public sealed record GroupPreview(int Count, int WithEmail, int WithMobile, List<string> Sample);

public sealed record CampaignGroupItem(Guid Id, string Name, int Count, string? Updated)
{
    public override string ToString() => $"{Name} ({Count} firma)";
}

public sealed record WhatsAppContact(int Id, string Name, string Phone);

public sealed record WhatsAppList(List<WhatsAppContact> Contacts, int SkippedOptOut, int SkippedNoMobile);

public sealed record UploadedPdf(string Token, string Name, long SizeKb, bool EmailOk);

public sealed record PreparedCampaign(
    Guid CampaignId,
    long Revision,
    string ManifestHash,
    int GroupSize,
    int Eligible,
    string Exclusions,
    int OptedOutSkipped,
    List<string> Attachments,
    string Sender,
    string StartsAt,
    string Window,
    int SpacingSeconds,
    int EstimatedDays,
    string Preview);

public sealed record CampaignListItem(
    Guid Id,
    string Name,
    string Channel,
    string State,
    string StateCode,
    long Revision,
    string StartsAt,
    int Total,
    int Sent,
    int Failed,
    int Pending,
    string? Error)
{
    public string Summary => $"{Channel} · {State} · {Sent}/{Total} gönderildi" +
        (Failed > 0 ? $" · {Failed} başarısız" : string.Empty) + $" · başlangıç {StartsAt}";
}

// The signed-in panel user as last reported by the server; permissions decide which tabs and buttons are shown.
public sealed record UserSession(
    int Id,
    string UserName,
    string DisplayName,
    string Role,
    string RoleName,
    List<string> Permissions,
    bool MustChangePassword,
    DateTime? ExpiresUtc,
    string Device)
{
    public bool Can(string permission) => permission.Split('|').Any(Permissions.Contains);

    public string Caption => (DisplayName == UserName ? UserName : $"{DisplayName} ({UserName})") + " · " + Role;
}

public class ApiException(string message, int? statusCode = null, string? content = null) : Exception(message)
{
    public int? StatusCode { get; } = statusCode;

    public string? Content { get; } = content;

    public bool IsCustomerConflict => StatusCode == 409;

    // 403 because the user lacks the permission (not because of a pending password change).
    public bool IsForbidden => StatusCode == 403 && HasFlag("forbidden");

    public bool HasFlag(string name)
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

// The computer or tunnel could not be reached; the request never got an answer from the ERP.
public sealed class ServerUnreachableException(string message) : ApiException(message);

public sealed class ApiClient
{
    private const string ServerKey = "server_url";
    private const string TokenKey = "device_token";
    private const string SessionKey = "user_session";
    private const string IdempotencyHeader = "Idempotency-Key";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(200) };
    private string? _token;
    private UserSession? _session;

    public static ApiClient Instance { get; } = new();

    public string ServerUrl
    {
        get => Preferences.Default.Get(ServerKey, string.Empty);
        set => Preferences.Default.Set(ServerKey, NormalizeUrl(value));
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

    public async Task<HubStatus> GetHubAsync()
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Get, "/api/hub", null);
        return await ReadAsync<HubStatus>(response);
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

    public async Task<bool> TryApplyTelegramClipboardUrlAsync()
    {
        string? text = await Clipboard.Default.GetTextAsync();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        System.Text.RegularExpressions.Match deepLink = System.Text.RegularExpressions.Regex.Match(
            text,
            @"arsangazerp://server\?[^\s]+",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (deepLink.Success
            && Uri.TryCreate(deepLink.Value, UriKind.Absolute, out Uri? uri)
            && ServerUrlCoordinator.TryApplyDeepLink(uri))
        {
            try
            {
                await HelloAsync();
                return true;
            }
            catch (ApiException)
            {
                return false;
            }
        }

        System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(
            text,
            @"https://[a-z0-9-]+\.trycloudflare\.com",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return false;
        }

        ServerUrl = match.Value;
        try
        {
            await HelloAsync();
            return true;
        }
        catch (ApiException)
        {
            return false;
        }
    }

    // Same user name and password as the web panel. The server issues a token for this phone, valid for 24 hours.
    public async Task<UserSession> LoginAsync(string userName, string password, string deviceName)
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Post, "/api/giris",
            new { kullanici = userName.Trim(), sifre = password, cihaz = deviceName }, authorize: false);
        JsonElement json = await ReadAsync<JsonElement>(response);
        string token = json.GetProperty("token").GetString() ?? throw new ApiException("Sunucu oturum anahtarı döndürmedi.");
        UserSession session = json.GetProperty("user").Deserialize<UserSession>(Json) ?? throw new ApiException("Sunucu kullanıcı bilgisi döndürmedi.");
        await SecureStorage.Default.SetAsync(TokenKey, token);
        _token = token;
        SaveSession(session);
        return session;
    }

    public async Task<UserSession> RefreshSessionAsync()
    {
        UserSession session = await GetAsync<UserSession>("/api/ben");
        SaveSession(session);
        return session;
    }

    public async Task<string> ChangePasswordAsync(string current, string next, string repeat)
    {
        string message = await PostMessageAsync("/api/sifre", new { mevcut = current, yeni = next, yeniTekrar = repeat });
        await RefreshSessionAsync();
        return message;
    }

    public async Task LogoutAsync()
    {
        try
        {
            if (await GetTokenAsync() is not null && ServerUrl.Length > 0)
            {
                using HttpResponseMessage response = await SendAsync(HttpMethod.Post, "/api/cikis", new { });
            }
        }
        catch (ApiException)
        {
        }

        ClearSession();
    }

    // Signed in with a token that has not passed its 24-hour expiry (the server has the final word).
    public async Task<bool> IsSignedInAsync() =>
        Session is { } session && (session.ExpiresUtc is not DateTime expires || expires > DateTime.UtcNow) && !string.IsNullOrEmpty(await GetTokenAsync());

    public UserSession? Session
    {
        get
        {
            if (_session is null && Preferences.Default.Get(SessionKey, string.Empty) is { Length: > 0 } json)
            {
                try
                {
                    _session = JsonSerializer.Deserialize<UserSession>(json, Json);
                }
                catch (JsonException)
                {
                    _session = null;
                }
            }

            return _session;
        }
    }

    public bool Can(string permission) => Session?.Can(permission) == true;

    // Raised (on any thread) when the server refuses the token: expired, signed out by the administrator, password
    // or role changed. The app returns to the sign-in screen with the message.
    public event Action<string>? SessionEnded;

    // Raised when the server requires a new password before anything else.
    public event Action? PasswordChangeRequired;

    private void SaveSession(UserSession session)
    {
        _session = session;
        Preferences.Default.Set(SessionKey, JsonSerializer.Serialize(session, Json));
    }

    private void ClearSession()
    {
        SecureStorage.Default.Remove(TokenKey);
        Preferences.Default.Remove(SessionKey);
        _token = null;
        _session = null;
    }

    public Task<string> TextAsync(string path) => GetTextAsync(path);

    public async Task<List<CustomerItem>> CustomersAsync(string query) =>
        await GetAsync<List<CustomerItem>>("/api/musteriler?q=" + Uri.EscapeDataString(query));

    public Task<CustomerCreated> CreateCustomerAsync(object customer) => PostAsync<CustomerCreated>("/api/musteri", customer);

    public async Task<List<CrmItem>> CrmAsync(string query) =>
        await GetAsync<List<CrmItem>>("/api/crm?q=" + Uri.EscapeDataString(query));

    public async Task<List<CrmItem>> FollowUpsAsync(int days) =>
        await GetAsync<List<CrmItem>>("/api/takipler?gun=" + days);

    public async Task<CylinderInfo> CylinderAsync(string code) =>
        await GetAsync<CylinderInfo>("/api/tup?kod=" + Uri.EscapeDataString(code));

    public Task<CylinderCard> CylinderCardAsync(string code) =>
        GetAsync<CylinderCard>("/api/tup/bilgi?kod=" + Uri.EscapeDataString(code));

    // requestId: the offline queue keeps one per scan so a resend after a lost answer is not saved twice.
    public Task<string> MoveCylinderAsync(string code, int status, int? customerId, string? note, string? plate = null, bool? isFull = null, bool confirmCustomerChange = false, string? requestId = null) =>
        PostMessageAsync("/api/tup/hareket", new { code, status, customerId, note, plate, isFull, confirmCustomerChange }, requestId);

    public Task<CylinderBulkResponse> MoveCylindersAsync(IReadOnlyList<string> codes, int status, int? customerId, string? note, string? plate, bool? isFull, bool confirmCustomerChange = false) =>
        PostAsync<CylinderBulkResponse>("/api/tup/toplu-hareket", new { codes, status, customerId, note, plate, isFull, confirmCustomerChange });

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

    public Task<List<ActiveTripSummary>> ActiveTripsAsync() => GetAsync<List<ActiveTripSummary>>("/api/sefer/aktif");

    public Task<TripStartResponse> StartTripAsync(string? note, IReadOnlyList<string>? plates) =>
        PostAsync<TripStartResponse>("/api/sefer/baslat", new { note, plates });

    public Task<string> AddTripVehicleAsync(int tripId, string plate) =>
        PostMessageAsync("/api/sefer/arac", new { tripId, plate });

    public Task<TripLoadResponse> LoadTripCylindersAsync(int tripId, string plate, IReadOnlyList<string> codes) =>
        PostAsync<TripLoadResponse>("/api/sefer/yukle", new { tripId, plate, codes });

    public Task<string> DeliverTripCylindersAsync(int tripId, int customerId, IReadOnlyList<string> codes, string? note) =>
        PostMessageAsync("/api/sefer/teslim", new { tripId, customerId, codes, note });

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

    public Task<TripLoadResponse> PickupTripEmptiesAsync(int tripId, string plate, IReadOnlyList<string> codes) =>
        PostAsync<TripLoadResponse>("/api/sefer/bos-al", new { tripId, plate, codes });

    public Task<TripLoadView> TripLoadAsync(int tripId, string? plate) =>
        GetAsync<TripLoadView>("/api/sefer/" + tripId + "/yuk" + (string.IsNullOrEmpty(plate) ? string.Empty : "?plaka=" + Uri.EscapeDataString(plate)));

    public Task<List<VehicleItem>> VehiclesAsync(bool includeInactive) =>
        GetAsync<List<VehicleItem>>("/api/arac-kayit" + (includeInactive ? "?pasif=true" : string.Empty));

    public Task<VehicleDetailResponse> VehicleDetailAsync(int id) => GetAsync<VehicleDetailResponse>("/api/arac-kayit/" + id);

    public Task<VehicleSaved> SaveVehicleAsync(int? id, VehicleForm form) =>
        PostAsync<VehicleSaved>("/api/arac-kayit" + (id is int value ? "/" + value : string.Empty), form);

    public Task<string> AddCrmNoteAsync(int id, string text) =>
        PostMessageAsync($"/api/crm/{id}/not", new { text });

    public Task<string> SetFollowUpAsync(int id, DateTime date, string? note) =>
        PostMessageAsync($"/api/crm/{id}/takip", new { date = date.ToString("yyyy-MM-dd"), note });

    public async Task<List<AgentReply>> ChatAsync(string message, IEnumerable<ChatTurn> history)
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Post, "/api/sohbet", new { message, history });
        return await ReadAsync<List<AgentReply>>(response);
    }

    public async Task<string> SendToCursorAsync(string message, IReadOnlyList<string>? attachmentPaths = null)
    {
        if (attachmentPaths is { Count: > 0 })
        {
            using MultipartFormDataContent content = new();
            content.Add(new StringContent(message), "message");
            List<FileStream> streams = [];
            try
            {
                foreach (string path in attachmentPaths)
                {
                    if (!File.Exists(path))
                    {
                        continue;
                    }

                    FileStream stream = File.OpenRead(path);
                    streams.Add(stream);
                    content.Add(new StreamContent(stream), "files", Path.GetFileName(path));
                }

                if (streams.Count == 0)
                {
                    throw new ApiException("Seçilen ek dosyaları okunamadı.");
                }

                using HttpResponseMessage response = await SendMultipartAsync("/api/cursor", content);
                return (await ReadAsync<CursorQueued>(response)).Id;
            }
            finally
            {
                foreach (FileStream stream in streams)
                {
                    await stream.DisposeAsync();
                }
            }
        }

        using HttpResponseMessage jsonResponse = await SendAsync(HttpMethod.Post, "/api/cursor", new { message });
        return (await ReadAsync<CursorQueued>(jsonResponse)).Id;
    }

    private async Task<HttpResponseMessage> SendMultipartAsync(string path, HttpContent content)
    {
        string server = ServerUrl;
        if (server.Length == 0)
        {
            throw new ApiException("Sunucu adresi girilmemiş. ana menüdeki Hesap ve Bağlantı ekranından adresi girin.");
        }

        string token = await GetTokenAsync() ?? throw new ApiException("Giriş yapılmamış. Kullanıcı adı ve şifrenizle giriş yapın.");
        using HttpRequestMessage request = new(HttpMethod.Post, server + path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add(IdempotencyHeader, Guid.NewGuid().ToString("N"));
        try
        {
            return await _http.SendAsync(request);
        }
        catch (HttpRequestException exception)
        {
            throw new ApiException("Sunucuya ulaşılamadı: " + exception.Message);
        }
        catch (TaskCanceledException)
        {
            throw new ApiException("Sunucu zamanında yanıt vermedi.");
        }
    }

    public Task<CursorStatus> CursorStatusAsync(string id) => GetAsync<CursorStatus>("/api/cursor/" + Uri.EscapeDataString(id));

    public Task<CampaignStatusInfo> CampaignStatusAsync() => GetAsync<CampaignStatusInfo>("/api/kampanya/durum");

    public Task<CampaignFilters> CampaignFiltersAsync() => GetAsync<CampaignFilters>("/api/kampanya/filtreler");

    public Task<List<CampaignGroupItem>> CampaignGroupsAsync() => GetAsync<List<CampaignGroupItem>>("/api/kampanya/gruplar");

    public Task<GroupPreview> PreviewGroupAsync(object filter) => PostAsync<GroupPreview>("/api/kampanya/grup/onizle", filter);

    public Task<CampaignGroupItem> CreateGroupAsync(object filter) => PostAsync<CampaignGroupItem>("/api/kampanya/grup", filter);

    public Task<WhatsAppList> GroupWhatsAppAsync(Guid groupId) => GetAsync<WhatsAppList>($"/api/kampanya/grup/{groupId}/whatsapp");

    public Task<string> MarkWhatsAppSentAsync(int contactId, string? note) =>
        PostMessageAsync("/api/kampanya/whatsapp-gonderildi", new { contactId, note });

    public Task<string> MarkOptOutAsync(int contactId, string? channel) =>
        PostMessageAsync("/api/kampanya/ret", new { contactId, channel });

    public Task<PreparedCampaign> PrepareCampaignAsync(object request) => PostAsync<PreparedCampaign>("/api/kampanya/hazirla", request);

    public Task<string> LaunchCampaignAsync(Guid id, long revision, string manifestHash) =>
        PostMessageAsync($"/api/kampanya/{id}/baslat", new { revision, manifestHash, confirm = true });

    public Task<List<CampaignListItem>> CampaignsAsync() => GetAsync<List<CampaignListItem>>("/api/kampanya/liste");

    public Task<string> ChangeCampaignAsync(Guid id, string action, long revision) =>
        PostMessageAsync($"/api/kampanya/{id}/{action}", new { revision });

    public async Task<UploadedPdf> UploadCampaignPdfAsync(string path)
    {
        using MultipartFormDataContent content = new();
        await using FileStream stream = File.OpenRead(path);
        StreamContent file = new(stream);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content.Add(file, "file", Path.GetFileName(path));
        using HttpResponseMessage response = await SendMultipartAsync("/api/kampanya/ek", content);
        return await ReadAsync<UploadedPdf>(response);
    }

    private async Task<T> PostAsync<T>(string path, object body)
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Post, path, body);
        return await ReadAsync<T>(response);
    }

    private async Task<string> GetTextAsync(string path)
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Get, path, null);
        return (await ReadAsync<JsonElement>(response)).GetProperty("text").GetString() ?? string.Empty;
    }

    private async Task<string> PostMessageAsync(string path, object body, string? idempotencyKey = null)
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Post, path, body, idempotencyKey: idempotencyKey);
        return (await ReadAsync<JsonElement>(response)).GetProperty("message").GetString() ?? "Kaydedildi.";
    }

    private async Task<T> GetAsync<T>(string path)
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Get, path, null);
        return await ReadAsync<T>(response);
    }

    private async Task<string?> GetTokenAsync() => _token ??= await SecureStorage.Default.GetAsync(TokenKey);

    // Every POST carries an Idempotency-Key, reused when the request is resent, so the server applies it only once.
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, bool authorize = true, string? idempotencyKey = null)
    {
        string server = ServerUrl;
        if (server.Length == 0)
        {
            throw new ApiException("Sunucu adresi girilmemiş. ana menüdeki Hesap ve Bağlantı ekranından adresi girin.");
        }

        string? key = method == HttpMethod.Post ? idempotencyKey ?? Guid.NewGuid().ToString("N") : null;
        using HttpRequestMessage request = new(method, server + path);
        if (key is not null)
        {
            request.Headers.Add(IdempotencyHeader, key);
        }

        if (authorize)
        {
            string token = await GetTokenAsync() ?? throw new ApiException("Giriş yapılmamış. Kullanıcı adı ve şifrenizle giriş yapın.");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        try
        {
            return await _http.SendAsync(request);
        }
        catch (HttpRequestException exception)
        {
            await ServerUrlCoordinator.TryRecoverAfterFailureAsync();
            if (!string.Equals(server, ServerUrl, StringComparison.OrdinalIgnoreCase))
            {
                using HttpRequestMessage retry = new(method, ServerUrl + path);
                if (key is not null)
                {
                    retry.Headers.Add(IdempotencyHeader, key);
                }

                if (authorize)
                {
                    string token = await GetTokenAsync() ?? throw new ApiException("Giriş yapılmamış.");
                    retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                }

                if (body is not null)
                {
                    retry.Content = JsonContent.Create(body, options: Json);
                }

                try
                {
                    return await _http.SendAsync(retry);
                }
                catch (Exception retryException) when (retryException is HttpRequestException or TaskCanceledException)
                {
                    throw new ServerUnreachableException("Sunucuya yeni adresten de ulaşılamadı. (" + retryException.Message + ")");
                }
            }

            throw new ServerUnreachableException("Sunucuya ulaşılamadı. Telegram'daki tek dokunuş bağlantısını deneyin veya uygulamayı yeniden açın. (" + exception.Message + ")");
        }
        catch (TaskCanceledException)
        {
            throw new ServerUnreachableException("Sunucu zamanında yanıt vermedi.");
        }
    }

    private async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        string content = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            string? error = null;
            bool loginRequired = false;
            bool mustChangePassword = false;
            try
            {
                using JsonDocument document = JsonDocument.Parse(content);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    error = document.RootElement.TryGetProperty("error", out JsonElement value) ? value.GetString() : null;
                    loginRequired = document.RootElement.TryGetProperty("loginRequired", out JsonElement login) && login.ValueKind == JsonValueKind.True;
                    mustChangePassword = document.RootElement.TryGetProperty("mustChangePassword", out JsonElement change) && change.ValueKind == JsonValueKind.True;
                }
            }
            catch (JsonException)
            {
            }

            if (loginRequired)
            {
                ClearSession();
                SessionEnded?.Invoke(error ?? "Oturum kapandı; yeniden giriş yapın.");
            }
            else if (mustChangePassword)
            {
                PasswordChangeRequired?.Invoke();
            }

            if (error is null && response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadGateway
                    or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout or (HttpStatusCode)530)
            {
                throw new ServerUnreachableException(response.StatusCode == HttpStatusCode.NotFound
                    ? "Sunucu bulunamadı (404). Adres eski olabilir; Telegram'daki son adresi girin."
                    : "Bilgisayardaki ERP sunucusuna ulaşılamıyor. Bilgisayar kapalı veya adres değişmiş olabilir.");
            }

            throw new ApiException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => error ?? "Oturum geçersiz; yeniden giriş yapın.",
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
