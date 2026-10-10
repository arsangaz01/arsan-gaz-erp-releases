using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ArsanGazERP.Models;

public enum CampaignChannel { Email, WhatsApp }
public enum CampaignPopulation { CrmCompanies, ErpCustomers }
public enum CampaignState { Draft, Approved, Scheduled, Active, Paused, CancelRequested, Cancelled, Completed, Faulted }
public enum CampaignRecipientState { Pending, Claimed, Sending, Accepted, Rejected, Unknown, Skipped, Cancelled }
public enum CampaignExclusionReason { MissingConsent, OptedOut, InvalidEndpoint, InactiveCustomer, DuplicateEndpoint, ConflictingConsent, Cooldown }
public enum CampaignSendDisposition { Accepted, DefinitelyRejected, Unknown }

public sealed record AudienceSelection(CampaignPopulation Population, IReadOnlyList<int> ContactIds, bool SelectAllEligible);
public sealed record CampaignDailyWindow(TimeOnly Start, TimeOnly End);
public sealed record WhatsAppTemplateComponent(string Type, IReadOnlyList<string> Parameters);
public sealed record WhatsAppTemplateApproval(
    string Name,
    string LanguageCode,
    bool IsVerifiedMarketing,
    bool HasDocumentHeader,
    int BodyParameterCount,
    DateTimeOffset? VerifiedAtUtc,
    bool SchemaSupported = false);
public sealed record CampaignAttachment(Guid Id, string FileName, string ContentType, long Length, string Sha256, string StagedPath);

public sealed record CampaignDraftInput(
    string Name,
    CampaignChannel Channel,
    AudienceSelection Audience,
    string SenderIdentity,
    string Subject,
    string Content,
    WhatsAppTemplateApproval? WhatsAppTemplate,
    IReadOnlyList<WhatsAppTemplateComponent> WhatsAppComponents,
    IReadOnlyList<Guid> AttachmentIds,
    string TimeZoneId,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset? EndsAtUtc,
    IReadOnlyList<CampaignDailyWindow> DailyWindows,
    TimeSpan MinimumSpacing,
    int MaximumRecipients,
    decimal EstimatedCost,
    bool CostAcknowledged,
    int RepeatEveryDays = 0);

public sealed record CampaignGroup(
    Guid Id,
    string Name,
    CampaignPopulation Population,
    IReadOnlyList<int> ContactIds,
    DateTimeOffset UpdatedAtUtc);

public sealed record GroupCampaignRequest(
    Guid GroupId,
    string Name,
    CampaignChannel Channel,
    string SenderIdentity,
    string Subject,
    string Content,
    WhatsAppTemplateApproval? WhatsAppTemplate,
    IReadOnlyList<WhatsAppTemplateComponent> WhatsAppComponents,
    IReadOnlyList<string> AttachmentPaths,
    string TimeZoneId,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset? EndsAtUtc,
    IReadOnlyList<CampaignDailyWindow> DailyWindows,
    TimeSpan MinimumSpacing,
    int RepeatEveryDays);

public sealed record GroupCampaignPreparation(CampaignSnapshot Draft, ApprovalPreviewSummary Summary);
public sealed record ApprovalPreviewSummary(
    int GroupSize,
    int EligibleCount,
    IReadOnlyList<AudienceExclusion> Exclusions,
    IReadOnlyList<string> AttachmentNames,
    string ApprovalManifestHash);
public sealed record ConsentBatchResult(int Recorded, int MissingEndpoint, int Failed, int SkippedWithdrawn = 0);

public sealed record CampaignDraft(Guid Id, long Revision, CampaignState State, CampaignDraftInput Input, DateTimeOffset UpdatedAtUtc);
public sealed record CampaignRecipient(
    string ContactKey,
    CampaignPopulation Population,
    int ContactId,
    string DisplayName,
    string Endpoint,
    string NormalizedEndpoint,
    string ContentHash,
    CampaignRecipientState State,
    int Attempts,
    DateTimeOffset? NextAttemptAtUtc,
    string? LastOutcomeCode,
    string? ProviderMessageId,
    DateTimeOffset? AcceptedAtUtc,
    bool? DeliveryConfirmed);

public sealed record AudienceExclusion(CampaignExclusionReason Reason, int Count);
public sealed record AudiencePreview(IReadOnlyList<CampaignRecipient> Recipients, IReadOnlyList<AudienceExclusion> Exclusions)
{
    public int EligibleCount => Recipients.Count;
}

public sealed record CampaignApprovalManifest(
    Guid CampaignId,
    long Revision,
    CampaignDraftInput Draft,
    IReadOnlyList<CampaignAttachment> Attachments,
    IReadOnlyList<CampaignRecipient> Recipients,
    DateTimeOffset PreparedAtUtc,
    string Hash);

public sealed record ApprovalAcknowledgement(bool ConfirmExactAudience, bool ConfirmCost, bool ConfirmSender);
public sealed record ActivationAcknowledgement(bool ConfirmRealCustomerSending, bool ConfirmCost, bool ConfirmSender);
public sealed record CampaignSnapshot(
    Guid Id,
    long Revision,
    CampaignState State,
    string OwnerSid,
    CampaignDraftInput Input,
    string? ManifestHash,
    DateTimeOffset UpdatedAtUtc,
    string? LastErrorCode = null);
public sealed record CampaignQuery(int Offset = 0, int Limit = 100, CampaignState? State = null);
public sealed record CampaignPage(IReadOnlyList<CampaignSnapshot> Items, int TotalCount);
public sealed record RecipientPage(IReadOnlyList<CampaignRecipient> Items, int TotalCount);
public sealed record CampaignConsent(
    CampaignPopulation Population,
    int ContactId,
    CampaignChannel Channel,
    string Endpoint,
    bool Granted,
    string Evidence,
    DateTimeOffset RecordedAtUtc,
    bool IsWithdrawal);
public sealed record CampaignContactOption(
    CampaignPopulation Population,
    int ContactId,
    string DisplayName,
    string Email,
    string Phone,
    bool IsActive);
public sealed record ProviderReadiness(CampaignChannel Channel, bool Ready, string? BlockReason, string SenderIdentity);
public sealed record CampaignSenderOption(CampaignChannel Channel, string Identity, string DisplayName);
public sealed record ExecutionEnablementAcknowledgement(bool ConfirmRealCustomerDispatch, bool ConfirmNoModelInference, bool ConfirmNoInteractiveAuthentication);

public sealed record CampaignSendRequest(
    Guid CampaignId,
    string Recipient,
    CampaignDraftInput Draft,
    IReadOnlyList<CampaignAttachmentContent> Attachments,
    string IdempotencyKey);
public sealed record CampaignAttachmentContent(CampaignAttachment Descriptor, byte[] Bytes);
public sealed record CampaignSendResult(
    CampaignSendDisposition Disposition,
    string OutcomeCode,
    string? ProviderMessageId = null,
    TimeSpan? RetryAfter = null,
    bool SafeToRetry = false);
public sealed record CampaignGraphToken(string AccessToken, string AccountId);
public sealed class CampaignAuthenticationException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public static class CampaignContentHash
{
    public static string Compute(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static string ComputeManifest(CampaignDraftInput input, IReadOnlyList<CampaignAttachment> attachments, IReadOnlyList<CampaignRecipient> recipients)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new { input, attachments, recipients });
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}

public static class CampaignAttachmentPolicy
    {
        public static string ContentType(string fileName, CampaignChannel channel)
        {
            string extension = System.IO.Path.GetExtension(fileName).ToLowerInvariant();
            if (channel == CampaignChannel.WhatsApp && extension != ".pdf")
                throw new System.IO.InvalidDataException("WhatsApp attachments must be PDF documents.");
            return extension switch
            {
                ".pdf" => "application/pdf",
                ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
                ".txt" => "text/plain",
                ".csv" => "text/csv",
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                _ => throw new System.IO.InvalidDataException("Unsupported attachment. Use PDF, DOCX, XLSX, PPTX, TXT, CSV, PNG or JPEG for email.")
            };
        }

        public static void ValidateDescriptor(CampaignAttachment attachment, CampaignChannel channel)
        {
            if (attachment.ContentType != ContentType(attachment.FileName, channel))
                throw new System.IO.InvalidDataException("Attachment MIME type does not match its approved filename.");
        }

        public static void ValidateContent(string fileName, CampaignChannel channel, byte[] bytes)
        {
            string mime = ContentType(fileName, channel);
            bool matches;
            if (mime == "application/pdf") matches = bytes.AsSpan().StartsWith("%PDF-"u8);
            else if (mime == "image/png") matches = bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
            else if (mime == "image/jpeg") matches = bytes.AsSpan().StartsWith(new byte[] { 255, 216, 255 });
            else if (mime is "text/plain" or "text/csv")
            {
                string text;
                try { text = new UTF8Encoding(false, true).GetString(bytes); }
                catch (DecoderFallbackException exception) { throw new System.IO.InvalidDataException("Text attachment must contain UTF-8 text.", exception); }
                matches = !text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'));
            }
            else
            {
                using var stream = new System.IO.MemoryStream(bytes, writable: false);
                using var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read);
                string main = System.IO.Path.GetExtension(fileName).ToLowerInvariant() switch
                {
                    ".docx" => "word/document.xml",
                    ".xlsx" => "xl/workbook.xml",
                    ".pptx" => "ppt/presentation.xml",
                    _ => throw new System.IO.InvalidDataException("Unsupported Office document.")
                };
                matches = archive.GetEntry("[Content_Types].xml") is not null && archive.GetEntry(main) is not null &&
                    !archive.Entries.Any(entry => entry.FullName.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase));
            }
            if (!matches) throw new System.IO.InvalidDataException("Attachment content does not match the supported document format.");
        }
}
