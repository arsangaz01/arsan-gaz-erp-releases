using System.IO;
using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArsanGazERP.Models;

namespace ArsanGazERP.Services;

/// <summary>Google Workspace / Gmail SMTP sender used by campaigns. Settings are DPAPI-protected per Windows user.</summary>
public sealed class GmailSenderService
{
    public const string IdentityPrefix = "gmail:";
    public const string SmtpHost = "smtp.gmail.com";
    public const int SmtpPort = 587;
    public const string OptOutFooterMarker = "Bu tür iletileri almak istemiyorsanız";

    private static readonly string DefaultSettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ArsanGazERP", "Gmail", "gmail-sender.bin");

    private readonly string settingsPath;

    public GmailSenderService(string? settingsPath = null)
    {
        this.settingsPath = settingsPath ?? DefaultSettingsPath;
    }

    public sealed record Settings(string Address, string AppPassword, string DisplayName);

    public bool IsConfigured => File.Exists(settingsPath);

    public static bool IsGmailIdentity(string? identity) =>
        identity is not null && identity.StartsWith(IdentityPrefix, StringComparison.OrdinalIgnoreCase);

    public Settings LoadSettings()
    {
        if (!File.Exists(settingsPath))
            throw new InvalidOperationException("Gmail gönderici ayarları yapılmamış (Tools\\Set-GmailSender.ps1).");
        byte[] clearBytes = ProtectedData.Unprotect(File.ReadAllBytes(settingsPath), null, DataProtectionScope.CurrentUser);
        Settings settings = JsonSerializer.Deserialize<Settings>(clearBytes)
            ?? throw new InvalidOperationException("Gmail gönderici ayar dosyası okunamadı.");
        if (!MailAddress.TryCreate(settings.Address, out _) || string.IsNullOrWhiteSpace(settings.AppPassword))
            throw new InvalidOperationException("Gmail gönderici ayarları eksik.");
        return settings;
    }

    public void SaveSettings(string address, string appPassword, string displayName)
    {
        if (!MailAddress.TryCreate(address?.Trim(), out MailAddress? parsed) || parsed is null)
            throw new ArgumentException("Geçerli bir e-posta adresi girin.", nameof(address));
        string password = new((appPassword ?? string.Empty).Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (password.Length < 8)
            throw new ArgumentException("Uygulama şifresi boş veya çok kısa.", nameof(appPassword));
        Settings settings = new(parsed.Address, password, string.IsNullOrWhiteSpace(displayName) ? parsed.Address : displayName.Trim());
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        byte[] protectedBytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(settings), null, DataProtectionScope.CurrentUser);
        string temporaryPath = settingsPath + ".tmp";
        File.WriteAllBytes(temporaryPath, protectedBytes);
        File.Move(temporaryPath, settingsPath, true);
    }

    public string? ConfiguredIdentity()
    {
        try
        {
            return IsConfigured ? IdentityPrefix + LoadSettings().Address : null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            return null;
        }
    }

    public CampaignSenderOption? GetSenderOption()
    {
        try
        {
            if (!IsConfigured)
                return null;
            Settings settings = LoadSettings();
            return new CampaignSenderOption(CampaignChannel.Email, IdentityPrefix + settings.Address,
                $"{settings.DisplayName} <{settings.Address}> (Google Workspace)");
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            return null;
        }
    }

    public ProviderReadiness CheckReadiness(string senderIdentity)
    {
        try
        {
            if (!IsConfigured)
                return new ProviderReadiness(CampaignChannel.Email, false,
                    "Gmail gönderici ayarlanmamış. Bilgisayarda Tools\\Set-GmailSender.ps1 çalıştırılmalı.", senderIdentity);
            Settings settings = LoadSettings();
            bool same = string.Equals(IdentityPrefix + settings.Address, senderIdentity, StringComparison.OrdinalIgnoreCase);
            return new ProviderReadiness(CampaignChannel.Email, same,
                same ? null : "Kayıtlı Gmail adresi onaylı gönderici ile aynı değil.", IdentityPrefix + settings.Address);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            return new ProviderReadiness(CampaignChannel.Email, false, "Gmail gönderici ayarları okunamadı.", senderIdentity);
        }
    }

    /// <summary>Appends the opt-out line unless the text already contains one.</summary>
    public static string WithOptOutFooter(string content, string replyAddress)
    {
        string body = (content ?? string.Empty).TrimEnd();
        if (body.Contains(OptOutFooterMarker, StringComparison.OrdinalIgnoreCase))
            return body;
        return body + "\n\n--\n" + OptOutFooterMarker + $" bu e-postayı \"RET\" yazarak yanıtlayın veya {replyAddress} adresine bildirin; listeden çıkarılırsınız.";
    }

    public async Task<CampaignSendResult> SendAsync(CampaignSendRequest campaign, CancellationToken cancellationToken)
    {
        Settings settings;
        try
        {
            settings = LoadSettings();
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            return new CampaignSendResult(CampaignSendDisposition.DefinitelyRejected, "GmailSettingsUnavailable");
        }
        if (!string.Equals(IdentityPrefix + settings.Address, campaign.Draft.SenderIdentity, StringComparison.OrdinalIgnoreCase))
            return new CampaignSendResult(CampaignSendDisposition.DefinitelyRejected, "SenderAccountMismatch");
        if (!MailAddress.TryCreate(campaign.Recipient, out MailAddress? recipient) || recipient is null ||
            !string.Equals(recipient.Address, campaign.Recipient, StringComparison.OrdinalIgnoreCase))
            return new CampaignSendResult(CampaignSendDisposition.DefinitelyRejected, "InvalidApprovedEmailEndpoint");

        using MailMessage message = new()
        {
            From = new MailAddress(settings.Address, settings.DisplayName, Encoding.UTF8),
            Subject = campaign.Draft.Subject,
            SubjectEncoding = Encoding.UTF8,
            Body = WithOptOutFooter(campaign.Draft.Content, settings.Address),
            BodyEncoding = Encoding.UTF8,
            IsBodyHtml = false
        };
        message.To.Add(recipient);
        message.Headers.Add("List-Unsubscribe", $"<mailto:{settings.Address}?subject=RET>");
        List<MemoryStream> streams = [];
        try
        {
            foreach (var attachment in campaign.Attachments)
            {
                MemoryStream stream = new(attachment.Bytes, writable: false);
                streams.Add(stream);
                message.Attachments.Add(new Attachment(stream, attachment.Descriptor.FileName, attachment.Descriptor.ContentType));
            }

            using SmtpClient client = new(SmtpHost, SmtpPort)
            {
                EnableSsl = true,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                UseDefaultCredentials = false,
                Credentials = new NetworkCredential(settings.Address, settings.AppPassword),
                Timeout = 60_000
            };
            await client.SendMailAsync(message, cancellationToken);
            return new CampaignSendResult(CampaignSendDisposition.Accepted, "ProviderAccepted");
        }
        catch (SmtpFailedRecipientException)
        {
            return new CampaignSendResult(CampaignSendDisposition.DefinitelyRejected, "GmailRecipientRejected");
        }
        catch (SmtpException exception) when (IsAuthenticationFailure(exception))
        {
            return new CampaignSendResult(CampaignSendDisposition.DefinitelyRejected, "GmailAuthenticationFailed");
        }
        catch (SmtpException exception) when (exception.InnerException is SocketException or IOException && exception.StatusCode == SmtpStatusCode.GeneralFailure)
        {
            return new CampaignSendResult(CampaignSendDisposition.DefinitelyRejected, "GmailConnectionFailed", SafeToRetry: true);
        }
        catch (SmtpException exception)
        {
            return new CampaignSendResult(CampaignSendDisposition.Unknown, $"GmailSmtp{(int)exception.StatusCode}");
        }
        finally
        {
            foreach (MemoryStream stream in streams)
                stream.Dispose();
        }
    }

    private static bool IsAuthenticationFailure(SmtpException exception) =>
        exception.StatusCode is SmtpStatusCode.ClientNotPermitted or SmtpStatusCode.MustIssueStartTlsFirst ||
        exception.Message.Contains("5.7.8", StringComparison.Ordinal) ||
        exception.Message.Contains("5.7.0", StringComparison.Ordinal) ||
        exception.Message.Contains("Authentication", StringComparison.OrdinalIgnoreCase);
}
