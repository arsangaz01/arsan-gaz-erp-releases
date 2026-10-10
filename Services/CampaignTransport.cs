using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArsanGazERP.Models;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace ArsanGazERP.Services;

public sealed class CampaignTransport : ICampaignTransport, IDisposable
{
    private const string PublicClientId = "3eaf1f8a-fbc8-40f0-9759-e134542606bf";
    private static readonly Uri GraphBaseUri = new("https://graph.microsoft.com/v1.0/");
    private readonly HttpClient graphHttp;
    private readonly bool ownsGraphHttp;
    private readonly Func<string, CancellationToken, Task<CampaignGraphToken>> getGraphTokenAsync;
    private readonly Func<string, CancellationToken, Task<CampaignGraphToken>>? requestGraphConsentAsync;
    private readonly Func<CancellationToken, Task<IReadOnlyList<CampaignSenderOption>>>? getGraphSendersAsync;
    private readonly WhatsAppBusinessService whatsApp;
    private readonly GmailSenderService? gmail;

    public CampaignTransport(
        Func<string, CancellationToken, Task<CampaignGraphToken>> getGraphTokenAsync,
        WhatsAppBusinessService whatsApp,
        HttpClient? graphHttp = null,
        Func<CancellationToken, Task<IReadOnlyList<CampaignSenderOption>>>? getGraphSendersAsync = null,
        Func<string, CancellationToken, Task<CampaignGraphToken>>? requestGraphConsentAsync = null,
        GmailSenderService? gmail = null)
    {
        this.getGraphTokenAsync = getGraphTokenAsync;
        this.getGraphSendersAsync = getGraphSendersAsync;
        this.requestGraphConsentAsync = requestGraphConsentAsync;
        this.whatsApp = whatsApp;
        this.gmail = gmail;
        this.graphHttp = graphHttp ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        ownsGraphHttp = graphHttp is null;
    }

    public static async Task<CampaignTransport> CreateForCurrentWindowsUserAsync(
        WhatsAppBusinessService? whatsApp = null,
        HttpClient? graphHttp = null,
        CancellationToken cancellationToken = default)
    {
        IPublicClientApplication application = PublicClientApplicationBuilder
            .Create(PublicClientId)
            .WithAuthority(AadAuthorityAudience.AzureAdAndPersonalMicrosoftAccount)
            .WithRedirectUri("http://localhost")
            .Build();
        string cacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArsanGazERP", "Auth");
        Directory.CreateDirectory(cacheDirectory);
        MsalCacheHelper cache = await MsalCacheHelper.CreateAsync(
            new StorageCreationPropertiesBuilder("msalcache.bin3", cacheDirectory).Build());
        cache.RegisterCache(application.UserTokenCache);

        async Task<CampaignGraphToken> GetPinnedTokenAsync(string expectedAccountId, CancellationToken tokenCancellation)
        {
            if (string.IsNullOrWhiteSpace(expectedAccountId))
                throw new CampaignAuthenticationException("An approved Microsoft account ID is required.");
            IAccount? account = (await application.GetAccountsAsync()).SingleOrDefault(item =>
                string.Equals(item.HomeAccountId?.Identifier, expectedAccountId, StringComparison.Ordinal));
            if (account is null)
                throw new CampaignAuthenticationException("The approved Microsoft account is absent from the silent token cache.");
            try
            {
                AuthenticationResult token = await application.AcquireTokenSilent(["Mail.Send"], account)
                    .ExecuteAsync(tokenCancellation);
                string actualAccountId = token.Account.HomeAccountId?.Identifier ?? string.Empty;
                if (!string.Equals(actualAccountId, expectedAccountId, StringComparison.Ordinal))
                    throw new CampaignAuthenticationException("The silent token belongs to a different approved sender account.");
                return new CampaignGraphToken(token.AccessToken, actualAccountId);
            }
            catch (MsalException exception)
            {
                throw new CampaignAuthenticationException("Mail.Send is unavailable through silent authentication.", exception);
            }
        }

        async Task<IReadOnlyList<CampaignSenderOption>> GetSendersAsync(CancellationToken tokenCancellation)
        {
            IEnumerable<IAccount> accounts = await application.GetAccountsAsync();
            return accounts
                .Where(account => !string.IsNullOrWhiteSpace(account.HomeAccountId?.Identifier))
                .Select(account => new CampaignSenderOption(
                    CampaignChannel.Email,
                    account.HomeAccountId!.Identifier,
                    account.Username ?? account.HomeAccountId.Identifier))
                .ToArray();
        }

        async Task<CampaignGraphToken> RequestConsentAsync(string expectedAccountId, CancellationToken tokenCancellation)
        {
            IAccount? account = (await application.GetAccountsAsync()).SingleOrDefault(item =>
                string.Equals(item.HomeAccountId?.Identifier, expectedAccountId, StringComparison.Ordinal));
            if (account is null)
                throw new CampaignAuthenticationException("Select an account already present in the silent token cache before granting Mail.Send.");
            try
            {
                AuthenticationResult existing = await application.AcquireTokenSilent(["Mail.Send"], account)
                    .ExecuteAsync(tokenCancellation);
                if (string.Equals(existing.Account.HomeAccountId?.Identifier, expectedAccountId, StringComparison.Ordinal))
                    return new CampaignGraphToken(existing.AccessToken, expectedAccountId);
            }
            catch (MsalUiRequiredException)
            {
            }
            AuthenticationResult consent = await application.AcquireTokenInteractive(["Mail.Send"])
                .WithAccount(account)
                .WithPrompt(Prompt.Consent)
                .ExecuteAsync(tokenCancellation);
            string actualAccountId = consent.Account.HomeAccountId?.Identifier ?? string.Empty;
            if (!string.Equals(actualAccountId, expectedAccountId, StringComparison.Ordinal))
                throw new CampaignAuthenticationException("Interactive consent returned a different Microsoft sender account.");
            return new CampaignGraphToken(consent.AccessToken, actualAccountId);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new CampaignTransport(GetPinnedTokenAsync, whatsApp ?? new WhatsAppBusinessService(), graphHttp,
            GetSendersAsync, RequestConsentAsync, new GmailSenderService());
    }

    /// <summary>Transport for hosts without Microsoft sign-in: Gmail for e-mail, WhatsApp Business for WhatsApp.</summary>
    public static CampaignTransport CreateWithoutMicrosoft(GmailSenderService? gmail = null, WhatsAppBusinessService? whatsApp = null) =>
        new((_, _) => throw new CampaignAuthenticationException("Microsoft sender is unavailable in this host."),
            whatsApp ?? new WhatsAppBusinessService(), gmail: gmail ?? new GmailSenderService());

    public async Task<IReadOnlyList<CampaignSenderOption>> GetAvailableSendersAsync(
        CampaignChannel channel,
        CancellationToken cancellationToken)
    {
        if (channel == CampaignChannel.WhatsApp)
        {
            if (!whatsApp.IsConfigured)
                return [];
            WhatsAppBusinessService.Settings settings = whatsApp.LoadSettings();
            return [new CampaignSenderOption(channel, settings.PhoneNumberId, settings.PhoneNumberId)];
        }
        List<CampaignSenderOption> senders = [];
        if (gmail?.GetSenderOption() is { } gmailSender)
            senders.Add(gmailSender);
        if (getGraphSendersAsync is null)
        {
            if (gmail is not null)
                return senders;
            throw new InvalidOperationException("Microsoft sender discovery is unavailable in this host.");
        }
        senders.AddRange(await getGraphSendersAsync(cancellationToken));
        return senders;
    }

    public async Task<CampaignGraphToken> RequestMailSendConsentAsync(string accountId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accountId) || requestGraphConsentAsync is null)
            throw new InvalidOperationException("Explicit Mail.Send consent is unavailable for the selected host or account.");
        return await requestGraphConsentAsync(accountId, cancellationToken);
    }

    public async Task<ProviderReadiness> CheckReadinessAsync(
        CampaignChannel channel,
        string senderIdentity,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(senderIdentity))
            return new ProviderReadiness(channel, false, "Choose and pin a provider sender identity before approval or activation.", string.Empty);
        if (channel == CampaignChannel.WhatsApp)
        {
            if (!whatsApp.IsConfigured)
                return new ProviderReadiness(channel, false, "WhatsApp Business credentials are not configured for this Windows user.", senderIdentity);
            try
            {
                WhatsAppBusinessService.Settings settings = whatsApp.LoadSettings();
                bool sameSender = string.Equals(settings.PhoneNumberId, senderIdentity, StringComparison.Ordinal);
                return new ProviderReadiness(channel, sameSender,
                    sameSender ? null : "Configured WhatsApp Phone Number ID does not match the approved sender identity.",
                    settings.PhoneNumberId);
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException or CryptographicException or JsonException)
            {
                return new ProviderReadiness(channel, false, "WhatsApp credentials could not be read for this Windows owner.", senderIdentity);
            }
        }
        if (GmailSenderService.IsGmailIdentity(senderIdentity))
        {
            return gmail?.CheckReadiness(senderIdentity)
                ?? new ProviderReadiness(channel, false, "Gmail sender is unavailable in this host.", senderIdentity);
        }

        try
        {
            CampaignGraphToken token = await getGraphTokenAsync(senderIdentity, cancellationToken);
            bool sameAccount = string.Equals(token.AccountId, senderIdentity, StringComparison.Ordinal);
            return new ProviderReadiness(channel, sameAccount,
                sameAccount ? null : "Silent Microsoft account does not match the approved sender account ID.",
                token.AccountId);
        }
        catch (Exception exception) when (exception is CampaignAuthenticationException or InvalidOperationException or IOException or HttpRequestException)
        {
            return new ProviderReadiness(channel, false,
                "Silent Microsoft authentication is unavailable. Sign in and grant Mail.Send interactively before activation.",
                senderIdentity);
        }
    }

    public async Task<WhatsAppTemplateApproval> VerifyWhatsAppTemplateAsync(
        string name,
        string languageCode,
        CancellationToken cancellationToken)
    {
        WhatsAppBusinessService.Settings settings = whatsApp.LoadSettings();
        return await whatsApp.VerifyMarketingTemplateAsync(settings, name, languageCode, cancellationToken);
    }

    public async Task<CampaignSendResult> SendAsync(CampaignSendRequest request, CancellationToken cancellationToken)
    {
        return request.Draft.Channel switch
        {
            CampaignChannel.Email => await SendEmailAsync(request, cancellationToken),
            CampaignChannel.WhatsApp => await SendWhatsAppAsync(request, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(request), "Unsupported campaign channel.")
        };
    }

    private async Task<CampaignSendResult> SendEmailAsync(CampaignSendRequest campaign, CancellationToken cancellationToken)
    {
        if (GmailSenderService.IsGmailIdentity(campaign.Draft.SenderIdentity))
        {
            if (gmail is null)
                return new CampaignSendResult(CampaignSendDisposition.DefinitelyRejected, "GmailSenderUnavailable");
            return await gmail.SendAsync(campaign, cancellationToken);
        }
        CampaignGraphToken token;
        try
        {
            token = await getGraphTokenAsync(campaign.Draft.SenderIdentity, cancellationToken);
        }
        catch (Exception exception) when (exception is CampaignAuthenticationException or InvalidOperationException or IOException or HttpRequestException)
        {
            return new CampaignSendResult(CampaignSendDisposition.DefinitelyRejected, "SilentAuthenticationUnavailable");
        }
        if (!string.Equals(token.AccountId, campaign.Draft.SenderIdentity, StringComparison.Ordinal))
            return new CampaignSendResult(CampaignSendDisposition.DefinitelyRejected, "SenderAccountMismatch");
        if (!MailAddress.TryCreate(campaign.Recipient, out MailAddress? parsedAddress) || parsedAddress is null ||
            !string.Equals(parsedAddress.Address, campaign.Recipient, StringComparison.OrdinalIgnoreCase))
            return new CampaignSendResult(CampaignSendDisposition.DefinitelyRejected, "InvalidApprovedEmailEndpoint");
        long totalBytes = campaign.Attachments.Sum(attachment => (long)attachment.Bytes.Length);
        if (totalBytes > CampaignStore.MaximumEmailAttachmentBytes || campaign.Attachments.Count > CampaignStore.MaximumEmailAttachmentCount)
            return new CampaignSendResult(CampaignSendDisposition.DefinitelyRejected, "AttachmentLimitOrTypeMismatch");
        try
        {
            foreach (var attachment in campaign.Attachments)
            {
                CampaignAttachmentPolicy.ValidateDescriptor(attachment.Descriptor, CampaignChannel.Email);
                CampaignAttachmentPolicy.ValidateContent(attachment.Descriptor.FileName, CampaignChannel.Email, attachment.Bytes);
            }
        }
        catch (InvalidDataException)
        {
            return new CampaignSendResult(CampaignSendDisposition.DefinitelyRejected, "AttachmentLimitOrTypeMismatch");
        }

        object[] attachments = campaign.Attachments.Select(attachment => (object)new Dictionary<string, object>
        {
            ["@odata.type"] = "#microsoft.graph.fileAttachment",
            ["name"] = attachment.Descriptor.FileName,
            ["contentType"] = attachment.Descriptor.ContentType,
            ["contentBytes"] = Convert.ToBase64String(attachment.Bytes)
        }).ToArray();
        var payload = new
        {
            message = new
            {
                subject = campaign.Draft.Subject,
                body = new { contentType = "Text", content = campaign.Draft.Content },
                toRecipients = new[]
                {
                    new { emailAddress = new { address = parsedAddress.Address } }
                },
                attachments
            },
            saveToSentItems = true
        };

        using HttpRequestMessage request = new(HttpMethod.Post, new Uri(GraphBaseUri, "me/sendMail"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new StringContent(JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await graphHttp.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Accepted)
            return new CampaignSendResult(CampaignSendDisposition.Accepted, "ProviderAccepted");
        if ((int)response.StatusCode is >= 400 and < 500 && response.StatusCode != HttpStatusCode.RequestTimeout)
            return new CampaignSendResult(CampaignSendDisposition.DefinitelyRejected,
                $"GraphHttp{(int)response.StatusCode}", RetryAfter: ReadRetryAfter(response),
                SafeToRetry: response.StatusCode == HttpStatusCode.TooManyRequests);
        return new CampaignSendResult(CampaignSendDisposition.Unknown, $"GraphHttp{(int)response.StatusCode}");
    }

    private async Task<CampaignSendResult> SendWhatsAppAsync(CampaignSendRequest campaign, CancellationToken cancellationToken)
    {
        WhatsAppBusinessService.Settings settings;
        try
        {
            settings = whatsApp.LoadSettings();
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            return new CampaignSendResult(CampaignSendDisposition.DefinitelyRejected, "WhatsAppCredentialsUnavailable");
        }
        if (!string.Equals(settings.PhoneNumberId, campaign.Draft.SenderIdentity, StringComparison.Ordinal))
            return new CampaignSendResult(CampaignSendDisposition.DefinitelyRejected, "WhatsAppSenderMismatch");

        CampaignAttachmentContent? attachment = campaign.Attachments.SingleOrDefault();
        return await whatsApp.SendCampaignTemplateAsync(
            settings,
            campaign.Recipient,
            campaign.Draft.WhatsAppTemplate ?? throw new InvalidDataException("Approved WhatsApp template is missing."),
            campaign.Draft.WhatsAppComponents,
            attachment,
            cancellationToken);
    }

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        TimeSpan? value = response.Headers.RetryAfter?.Delta;
        if (value is null && response.Headers.RetryAfter?.Date is DateTimeOffset date)
            value = date - DateTimeOffset.UtcNow;
        return value is { } delay && delay > TimeSpan.Zero
            ? TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds, 3600))
            : null;
    }

    public void Dispose()
    {
        if (ownsGraphHttp)
            graphHttp.Dispose();
    }
}
