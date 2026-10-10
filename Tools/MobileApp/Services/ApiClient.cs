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

public class ApiException(string message) : Exception(message);

// The computer or tunnel could not be reached; the request never got an answer from the ERP.
public sealed class ServerUnreachableException(string message) : ApiException(message);

public sealed class ApiClient
{
    private const string ServerKey = "server_url";
    private const string TokenKey = "device_token";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(200) };
    private string? _token;

    public static ApiClient Instance { get; } = new();

    public string ServerUrl
    {
        get => Preferences.Default.Get(ServerKey, string.Empty);
        set => Preferences.Default.Set(ServerKey, NormalizeUrl(value));
    }

    public async Task<bool> IsPairedAsync() => !string.IsNullOrEmpty(await GetTokenAsync());

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

    public async Task<string> RequestPairingCodeAsync()
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Post, "/api/pair/request", new { }, authorize: false);
        return (await ReadAsync<JsonElement>(response)).GetProperty("message").GetString() ?? string.Empty;
    }

    public async Task PairAsync(string code, string deviceName)
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Post, "/api/pair", new { code, deviceName }, authorize: false);
        string token = (await ReadAsync<JsonElement>(response)).GetProperty("token").GetString()
            ?? throw new ApiException("Sunucu anahtar döndürmedi.");
        await SecureStorage.Default.SetAsync(TokenKey, token);
        _token = token;
    }

    public void Unpair()
    {
        SecureStorage.Default.Remove(TokenKey);
        _token = null;
    }

    public Task<string> TextAsync(string path) => GetTextAsync(path);

    public async Task<List<CustomerItem>> CustomersAsync(string query) =>
        await GetAsync<List<CustomerItem>>("/api/musteriler?q=" + Uri.EscapeDataString(query));

    public async Task<List<CrmItem>> CrmAsync(string query) =>
        await GetAsync<List<CrmItem>>("/api/crm?q=" + Uri.EscapeDataString(query));

    public async Task<List<CrmItem>> FollowUpsAsync(int days) =>
        await GetAsync<List<CrmItem>>("/api/takipler?gun=" + days);

    public async Task<CylinderInfo> CylinderAsync(string code) =>
        await GetAsync<CylinderInfo>("/api/tup?kod=" + Uri.EscapeDataString(code));

    public Task<string> MoveCylinderAsync(string code, int status, int? customerId, string? note) =>
        PostMessageAsync("/api/tup/hareket", new { code, status, customerId, note });

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
            throw new ApiException("Sunucu adresi girilmemiş. Bağlantı sekmesinden adresi girin.");
        }

        string token = await GetTokenAsync() ?? throw new ApiException("Bu telefon henüz eşleşmedi.");
        using HttpRequestMessage request = new(HttpMethod.Post, server + path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
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

    private async Task<string> PostMessageAsync(string path, object body)
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Post, path, body);
        return (await ReadAsync<JsonElement>(response)).GetProperty("message").GetString() ?? "Kaydedildi.";
    }

    private async Task<T> GetAsync<T>(string path)
    {
        using HttpResponseMessage response = await SendAsync(HttpMethod.Get, path, null);
        return await ReadAsync<T>(response);
    }

    private async Task<string?> GetTokenAsync() => _token ??= await SecureStorage.Default.GetAsync(TokenKey);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, bool authorize = true)
    {
        string server = ServerUrl;
        if (server.Length == 0)
        {
            throw new ApiException("Sunucu adresi girilmemiş. Bağlantı sekmesinden adresi girin.");
        }

        using HttpRequestMessage request = new(method, server + path);
        if (authorize)
        {
            string token = await GetTokenAsync() ?? throw new ApiException("Bu telefon henüz eşleşmedi. Bağlantı sekmesinden eşleştirin.");
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
                if (authorize)
                {
                    string token = await GetTokenAsync() ?? throw new ApiException("Bu telefon henüz eşleşmedi.");
                    retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                }

                if (body is not null)
                {
                    retry.Content = JsonContent.Create(body, options: Json);
                }

                return await _http.SendAsync(retry);
            }

            throw new ServerUnreachableException("Sunucuya ulaşılamadı. Telegram'daki tek dokunuş bağlantısını deneyin veya uygulamayı yeniden açın. (" + exception.Message + ")");
        }
        catch (TaskCanceledException)
        {
            throw new ServerUnreachableException("Sunucu zamanında yanıt vermedi.");
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
                    ? "Sunucu bulunamadı (404). Adres eski olabilir; Telegram'daki son adresi girin."
                    : "Bilgisayardaki ERP sunucusuna ulaşılamıyor. Bilgisayar kapalı veya adres değişmiş olabilir.");
            }

            throw new ApiException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => error ?? "Eşleşme geçersiz. Bağlantı'dan yeniden eşleştirin.",
                _ => error ?? $"Sunucu hatası ({(int)response.StatusCode})."
            });
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
