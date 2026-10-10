using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ArsanGazERP.Models;

namespace ArsanGazERP.Services;

public sealed class WhatsAppBusinessService
{
    private const string SettingsPathName = "whatsapp-business.bin";
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ArsanGazERP",
        "WhatsApp",
        SettingsPathName);
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly HttpClient http;
    private readonly string settingsPath;
    private static readonly Regex ApiVersionPattern = new(@"^v\d{1,2}\.\d$", RegexOptions.CultureInvariant);
    private static readonly Regex PhoneNumberIdPattern = new(@"^\d{5,30}$", RegexOptions.CultureInvariant);
    private static readonly Regex TemplateNamePattern = new(@"^[a-z0-9_]{1,512}$", RegexOptions.CultureInvariant);
    private static readonly Regex LanguagePattern = new(@"^[a-z]{2}(_[A-Z]{2})?$", RegexOptions.CultureInvariant);
    private static readonly Regex E164Pattern = new(@"^\+?[1-9]\d{7,14}$", RegexOptions.CultureInvariant);

    public WhatsAppBusinessService(HttpClient? httpClient = null, string? settingsPath = null)
    {
        http = httpClient ?? SharedHttp;
        this.settingsPath = settingsPath ?? SettingsPath;
    }

    public sealed record Settings(
        string AccessToken,
        string PhoneNumberId,
        string ApiVersion,
        string TemplateName,
        string LanguageCode,
        string BusinessAccountId = "");

    public bool IsConfigured => File.Exists(settingsPath);

    public Settings LoadSettings()
    {
        if (!File.Exists(settingsPath))
        {
            throw new InvalidOperationException("WhatsApp Business API ayarları yapılmamış.");
        }

        byte[] protectedBytes = File.ReadAllBytes(settingsPath);
        byte[] clearBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
        return JsonSerializer.Deserialize<Settings>(clearBytes)
            ?? throw new InvalidOperationException("WhatsApp Business API ayar dosyası okunamadı.");
    }

    public void SaveSettings(string accessToken, string phoneNumberId, string apiVersion, string templateName, string languageCode)
    {
        string resolvedAccessToken = string.IsNullOrWhiteSpace(accessToken)
            ? IsConfigured
                ? LoadSettings().AccessToken
                : string.Empty
            : accessToken.Trim();
        Settings settings = new(
            resolvedAccessToken,
            phoneNumberId.Trim(),
            apiVersion.Trim(),
            templateName.Trim(),
            languageCode.Trim(),
            IsConfigured ? LoadSettings().BusinessAccountId : string.Empty);

        if (string.IsNullOrWhiteSpace(settings.AccessToken))
            throw new ArgumentException("Meta erişim token'ı boş olamaz.", nameof(accessToken));
        if (!PhoneNumberIdPattern.IsMatch(settings.PhoneNumberId))
            throw new ArgumentException("Phone Number ID yalnızca 5-30 rakamdan oluşmalıdır.", nameof(phoneNumberId));
        if (!ApiVersionPattern.IsMatch(settings.ApiVersion))
            throw new ArgumentException("Graph API sürümü v25.0 biçiminde olmalıdır.", nameof(apiVersion));
        if (!TemplateNamePattern.IsMatch(settings.TemplateName))
            throw new ArgumentException("Şablon adı yalnızca küçük harf, rakam ve alt çizgi içerebilir.", nameof(templateName));
        if (!LanguagePattern.IsMatch(settings.LanguageCode))
            throw new ArgumentException("Dil kodu tr veya en_US biçiminde olmalıdır.", nameof(languageCode));

        PersistSettings(settings);
    }

    /// <summary>Sets the WABA used for template verification without changing any other setting.</summary>
    public void SetBusinessAccountId(string businessAccountId)
    {
        string id = businessAccountId?.Trim() ?? string.Empty;
        if (!PhoneNumberIdPattern.IsMatch(id))
            throw new ArgumentException("WhatsApp Business Account ID must contain 5-30 digits.", nameof(businessAccountId));
        PersistSettings(LoadSettings() with { BusinessAccountId = id });
    }

    private void PersistSettings(Settings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        byte[] clearBytes = JsonSerializer.SerializeToUtf8Bytes(settings);
        byte[] protectedBytes = ProtectedData.Protect(clearBytes, null, DataProtectionScope.CurrentUser);
        string temporaryPath = settingsPath + ".tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, protectedBytes);
            File.Move(temporaryPath, settingsPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public void RemoveSettings()
    {
        if (File.Exists(settingsPath))
            File.Delete(settingsPath);
    }

    public async Task<string> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        Settings settings = LoadSettings();
        using HttpRequestMessage request = CreateRequest(
            HttpMethod.Get,
            $"{settings.ApiVersion}/{settings.PhoneNumberId}?fields=verified_name,display_phone_number",
            settings.AccessToken);
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        using JsonDocument document = ParseResponse(body, response.StatusCode, response.ReasonPhrase);

        string verifiedName = document.RootElement.TryGetProperty("verified_name", out JsonElement name)
            ? name.GetString() ?? string.Empty
            : string.Empty;
        string displayNumber = document.RootElement.TryGetProperty("display_phone_number", out JsonElement number)
            ? number.GetString() ?? string.Empty
            : string.Empty;
        return string.IsNullOrWhiteSpace(verifiedName) && string.IsNullOrWhiteSpace(displayNumber)
            ? "Meta bağlantısı doğrulandı."
            : $"Meta bağlantısı doğrulandı: {verifiedName} {displayNumber}".Trim();
    }

    public async Task<string> SendApprovedTemplateAsync(string recipient, CancellationToken cancellationToken = default)
    {
        Settings settings = LoadSettings();
        string normalizedRecipient = NormalizeE164(recipient);
        return await SendTemplateAsync(
            settings,
            normalizedRecipient,
            settings.TemplateName,
            settings.LanguageCode,
            cancellationToken);
    }

    public async Task<string> SendSelfTestTemplateAsync(string recipient, CancellationToken cancellationToken = default)
    {
        Settings settings = LoadSettings();
        string normalizedRecipient = NormalizeE164(recipient);
        return await SendTemplateAsync(settings, normalizedRecipient, "hello_world", "en_US", cancellationToken);
    }

    private async Task<string> SendTemplateAsync(
        Settings settings,
        string normalizedRecipient,
        string templateName,
        string languageCode,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(
            HttpMethod.Post,
            $"{settings.ApiVersion}/{settings.PhoneNumberId}/messages",
            settings.AccessToken);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                messaging_product = "whatsapp",
                recipient_type = "individual",
                to = normalizedRecipient,
                type = "template",
                template = new
                {
                    name = templateName,
                    language = new { code = languageCode }
                }
            }),
            Encoding.UTF8,
            "application/json");

        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        using JsonDocument document = ParseResponse(body, response.StatusCode, response.ReasonPhrase);
        if (!document.RootElement.TryGetProperty("messages", out JsonElement messages) ||
            messages.ValueKind != JsonValueKind.Array ||
            messages.GetArrayLength() == 0 ||
            !messages[0].TryGetProperty("id", out JsonElement messageId) ||
            string.IsNullOrWhiteSpace(messageId.GetString()))
        {
            throw new InvalidOperationException("Meta isteği kabul etti ancak mesaj kimliği döndürmedi; teslimat doğrulanamadı.");
        }

        return messageId.GetString()!;
    }

    public async Task<WhatsAppTemplateApproval> VerifyMarketingTemplateAsync(
        Settings settings,
        string templateName,
        string languageCode,
        CancellationToken cancellationToken = default)
    {
        if (!TemplateNamePattern.IsMatch(templateName) || !LanguagePattern.IsMatch(languageCode))
            throw new ArgumentException("Template name or language code is invalid.");
        if (!PhoneNumberIdPattern.IsMatch(settings.BusinessAccountId))
            throw new InvalidOperationException("Configure a valid WhatsApp Business Account ID (WABA) before template verification.");
        string query = $"{settings.ApiVersion}/{settings.BusinessAccountId}/message_templates?name={Uri.EscapeDataString(templateName)}&fields=name,language,status,category,components";
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, query, settings.AccessToken);
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        using JsonDocument document = ParseResponse(body, response.StatusCode, response.ReasonPhrase);
        if (!document.RootElement.TryGetProperty("data", out JsonElement templates) ||
            templates.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Meta template verification response did not contain a template list.");

        foreach (JsonElement template in templates.EnumerateArray())
        {
            if (!TryGetString(template, "name", out string name) || name != templateName ||
                !TryGetString(template, "language", out string language) || language != languageCode ||
                !TryGetString(template, "status", out string status) || status != "APPROVED" ||
                !TryGetString(template, "category", out string category) || category != "MARKETING" ||
                !template.TryGetProperty("components", out JsonElement components) ||
                components.ValueKind != JsonValueKind.Array)
                continue;

            int bodyParameters = 0;
            bool documentHeader = false;
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
            foreach (JsonElement component in components.EnumerateArray())
            {
                if (!TryGetString(component, "type", out string type) || !seen.Add(type))
                    throw new InvalidDataException("Template contains missing or repeated component types.");
                switch (type.ToUpperInvariant())
                {
                    case "BODY":
                        if (!TryGetString(component, "text", out string text))
                            throw new InvalidDataException("Template body text is missing.");
                        MatchCollection placeholders = Regex.Matches(text, @"\{\{([1-9]\d*)\}\}");
                        string remaining = Regex.Replace(text, @"\{\{([1-9]\d*)\}\}", "");
                        if (remaining.Contains("{{", StringComparison.Ordinal) || remaining.Contains("}}", StringComparison.Ordinal))
                            throw new InvalidDataException("Only positional body parameters are supported.");
                        string[] positions = placeholders.Select(match => match.Groups[1].Value).Distinct().ToArray();
                        bodyParameters = positions.Length;
                        if (bodyParameters > 20 || !Enumerable.Range(1, bodyParameters).All(n => positions.Contains(n.ToString(System.Globalization.CultureInfo.InvariantCulture))))
                            throw new InvalidDataException("Template body positions must be contiguous from 1.");
                        break;
                    case "HEADER":
                        if (!TryGetString(component, "format", out string format))
                            throw new InvalidDataException("Template header format is missing.");
                        documentHeader = format.Equals("DOCUMENT", StringComparison.OrdinalIgnoreCase);
                        if (!documentHeader && (!format.Equals("TEXT", StringComparison.OrdinalIgnoreCase) ||
                            !TryGetString(component, "text", out string header) || header.Contains("{{", StringComparison.Ordinal)))
                            throw new InvalidDataException("Only document or static text headers are supported.");
                        break;
                    case "FOOTER":
                        if (!TryGetString(component, "text", out string footer) || footer.Contains("{{", StringComparison.Ordinal))
                            throw new InvalidDataException("Only static template footers are supported.");
                        break;
                    default:
                        throw new InvalidDataException("Template buttons and other component schemas are not supported.");
                }
            }
            if (!seen.Contains("BODY"))
                throw new InvalidDataException("Template must contain a supported body.");
            return new WhatsAppTemplateApproval(templateName, languageCode, true, documentHeader, bodyParameters, DateTimeOffset.UtcNow, true);
        }
        throw new InvalidOperationException("The selected WhatsApp template is not an approved marketing template in the requested language.");
    }

    public async Task<CampaignSendResult> SendCampaignTemplateAsync(
        Settings settings,
        string recipient,
        WhatsAppTemplateApproval template,
        IReadOnlyList<WhatsAppTemplateComponent> components,
        CampaignAttachmentContent? attachment,
        CancellationToken cancellationToken = default)
    {
        if (!template.IsVerifiedMarketing || !template.SchemaSupported || template.VerifiedAtUtc is null ||
            (attachment is not null) != template.HasDocumentHeader ||
            components.Any(component => !string.Equals(component.Type, "body", StringComparison.OrdinalIgnoreCase)) ||
            components.Sum(component => component.Parameters.Count) != template.BodyParameterCount)
            throw new InvalidOperationException("The WhatsApp campaign template is not verified for its selected message content.");
        string normalizedRecipient = NormalizeE164(recipient);
        string? uploadedMediaId = null;
        if (attachment is not null)
        {
            if (attachment.Bytes.Length != attachment.Descriptor.Length ||
                attachment.Descriptor.ContentType != "application/pdf" ||
                attachment.Bytes.Length > CampaignStore.MaximumWhatsAppAttachmentBytes)
                throw new InvalidDataException("WhatsApp PDF attachment failed size or type validation.");
            try
            {
                uploadedMediaId = await UploadDocumentAsync(settings, attachment, cancellationToken);
            }
            catch (WhatsAppHttpRejectionException exception)
            {
                return new CampaignSendResult(CampaignSendDisposition.DefinitelyRejected,
                    $"MediaUploadHttp{(int)exception.ProviderStatusCode}", RetryAfter: exception.RetryAfter,
                    SafeToRetry: exception.ProviderStatusCode == System.Net.HttpStatusCode.TooManyRequests ||
                        exception.ProviderStatusCode == System.Net.HttpStatusCode.RequestTimeout || (int)exception.ProviderStatusCode >= 500);
            }
            catch (HttpRequestException)
            {
                return new CampaignSendResult(CampaignSendDisposition.DefinitelyRejected, "MediaUploadNotAccepted", SafeToRetry: true);
            }
            catch (OperationCanceledException)
            {
                return new CampaignSendResult(CampaignSendDisposition.DefinitelyRejected, "MediaUploadTimedOut", SafeToRetry: true);
            }
            catch (Exception exception) when (exception is InvalidDataException or JsonException)
            {
                return new CampaignSendResult(CampaignSendDisposition.DefinitelyRejected, "MediaUploadInvalidResponse", SafeToRetry: true);
            }
        }

        List<object> templateComponents = [];
        foreach (WhatsAppTemplateComponent component in components)
        {
            if (!string.Equals(component.Type, "body", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Only verified WhatsApp body parameters are supported.");
            templateComponents.Add(new
            {
                type = "body",
                parameters = component.Parameters.Select(value => (object)new { type = "text", text = value }).ToArray()
            });
        }
        if (attachment is not null && uploadedMediaId is not null)
        {
            templateComponents.Add(new
            {
                type = "header",
                parameters = new[]
                {
                    (object)new
                    {
                        type = "document",
                        document = new { id = uploadedMediaId, filename = attachment.Descriptor.FileName }
                    }
                }
            });
        }

        using HttpRequestMessage message = CreateRequest(
            HttpMethod.Post, $"{settings.ApiVersion}/{settings.PhoneNumberId}/messages", settings.AccessToken);
        message.Content = new StringContent(JsonSerializer.Serialize(new
        {
            messaging_product = "whatsapp",
            recipient_type = "individual",
            to = normalizedRecipient,
            type = "template",
            template = new
            {
                name = template.Name,
                language = new { code = template.LanguageCode },
                components = templateComponents
            }
        }), Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await http.SendAsync(message, cancellationToken);
        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if ((int)response.StatusCode is >= 400 and < 500 && response.StatusCode != System.Net.HttpStatusCode.RequestTimeout)
            return new CampaignSendResult(CampaignSendDisposition.DefinitelyRejected,
                $"MessageHttp{(int)response.StatusCode}", RetryAfter: ReadRetryAfter(response),
                SafeToRetry: response.StatusCode == System.Net.HttpStatusCode.TooManyRequests);
        if (!response.IsSuccessStatusCode)
            return new CampaignSendResult(CampaignSendDisposition.Unknown, $"MessageHttp{(int)response.StatusCode}");
        try
        {
            using JsonDocument result = JsonDocument.Parse(responseBody);
            if (result.RootElement.TryGetProperty("messages", out JsonElement messages) &&
                messages.ValueKind == JsonValueKind.Array && messages.GetArrayLength() > 0 &&
                TryGetString(messages[0], "id", out string messageId) && !string.IsNullOrWhiteSpace(messageId))
                return new CampaignSendResult(CampaignSendDisposition.Accepted, "ProviderAccepted", messageId);
        }
        catch (JsonException)
        {
            return new CampaignSendResult(CampaignSendDisposition.Unknown, "AcceptedResponseMalformed");
        }
        return new CampaignSendResult(CampaignSendDisposition.Unknown, "AcceptedResponseMissingMessageId");
    }

    private async Task<string> UploadDocumentAsync(Settings settings, CampaignAttachmentContent attachment, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(
            HttpMethod.Post, $"{settings.ApiVersion}/{settings.PhoneNumberId}/media", settings.AccessToken);
        using MultipartFormDataContent form = new();
        form.Add(new StringContent("whatsapp"), "messaging_product");
        form.Add(new StringContent("application/pdf"), "type");
        ByteArrayContent fileContent = new(attachment.Bytes);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
        form.Add(fileContent, "file", attachment.Descriptor.FileName);
        request.Content = form;
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new WhatsAppHttpRejectionException(response.StatusCode, ReadRetryAfter(response));
        using JsonDocument result = JsonDocument.Parse(body);
        if (!TryGetString(result.RootElement, "id", out string id) || string.IsNullOrWhiteSpace(id))
            throw new InvalidDataException("Meta accepted the media upload but returned no media ID.");
        return id;
    }

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta;
        if (retryAfter is null && response.Headers.RetryAfter?.Date is DateTimeOffset date)
            retryAfter = date - DateTimeOffset.UtcNow;
        return retryAfter is { } value && value > TimeSpan.Zero
            ? TimeSpan.FromSeconds(Math.Min(value.TotalSeconds, 3600))
            : null;
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out JsonElement property) && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString() ?? string.Empty;
            return true;
        }
        value = string.Empty;
        return false;
    }

    private sealed class WhatsAppHttpRejectionException(
        System.Net.HttpStatusCode providerStatusCode,
        TimeSpan? retryAfter) : HttpRequestException
    {
        public System.Net.HttpStatusCode ProviderStatusCode { get; } = providerStatusCode;
        public TimeSpan? RetryAfter { get; } = retryAfter;
    }

    public static string NormalizeE164(string phone)
    {
        string normalized = new(phone.Where(character => character is not (' ' or '-' or '(' or ')' or '.')).ToArray());
        if (!E164Pattern.IsMatch(normalized))
            throw new ArgumentException("Telefon numarasını ülke koduyla E.164 biçiminde girin (ör. +905XXXXXXXXX).", nameof(phone));
        return normalized.TrimStart('+');
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string relativePath, string accessToken)
    {
        HttpRequestMessage request = new(method, new Uri($"https://graph.facebook.com/{relativePath}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private static JsonDocument ParseResponse(string body, System.Net.HttpStatusCode statusCode, string? reasonPhrase)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"Meta geçerli JSON döndürmedi (HTTP {(int)statusCode} {reasonPhrase}).",
                exception);
        }

        if ((int)statusCode is < 200 or >= 300)
        {
            string message = document.RootElement.TryGetProperty("error", out JsonElement error) &&
                             error.TryGetProperty("message", out JsonElement errorMessage)
                ? errorMessage.GetString() ?? "Meta isteği reddetti."
                : "Meta isteği reddetti.";
            document.Dispose();
            throw new InvalidOperationException($"Meta WhatsApp API hatası (HTTP {(int)statusCode}): {message}");
        }

        return document;
    }
}
