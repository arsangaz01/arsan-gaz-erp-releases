using System.IO;
using System.Text.Json;
using ArsanGazERP.Models;

namespace ArsanGazERP.Services;

public interface ICampaignTransport
{
    Task<IReadOnlyList<CampaignSenderOption>> GetAvailableSendersAsync(
        CampaignChannel channel,
        CancellationToken cancellationToken);

    Task<ProviderReadiness> CheckReadinessAsync(
        CampaignChannel channel,
        string senderIdentity,
        CancellationToken cancellationToken);

    Task<WhatsAppTemplateApproval> VerifyWhatsAppTemplateAsync(
        string name,
        string languageCode,
        CancellationToken cancellationToken);

    Task<CampaignSendResult> SendAsync(CampaignSendRequest request, CancellationToken cancellationToken);
    Task<CampaignGraphToken> RequestMailSendConsentAsync(string accountId, CancellationToken cancellationToken);
}

public sealed class CampaignService
{
    private const int MaximumNameLength = 160;
    private const int MaximumSubjectLength = 255;
    private const int MaximumContentLength = 50_000;
    private const int MaximumWhatsAppParameterCount = 20;
    private readonly CampaignStore store;
    private readonly ICampaignTransport transport;
    private readonly object executionGate = new();
    private volatile bool executionEnabled;

    public CampaignService(CampaignStore store, ICampaignTransport transport)
    {
        this.store = store;
        this.transport = transport;
    }

    public bool ExecutionEnabled => executionEnabled;
    internal CampaignStore Store => store;

    public async Task<CampaignDraft> SaveDraftAsync(
        Guid? campaignId,
        CampaignDraftInput input,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        await ValidateDraftAsync(input, cancellationToken, allowMissingDocument: true);
        return await store.SaveDraftAsync(campaignId, input, expectedRevision, cancellationToken);
    }

    public Task<IReadOnlyList<CampaignContactOption>> GetAudienceContactsAsync(
        CampaignPopulation population,
        string? search = null,
        int limit = 200,
        CancellationToken cancellationToken = default) =>
        store.GetAudienceContactsAsync(population, search, limit, cancellationToken);

    public Task<IReadOnlyList<CampaignGroup>> ListGroupsAsync(CancellationToken cancellationToken = default) =>
        store.ListGroupsAsync(cancellationToken);

    public Task<CampaignGroup> SaveGroupAsync(
        Guid? groupId,
        string name,
        CampaignPopulation population,
        IReadOnlyList<int> contactIds,
        CancellationToken cancellationToken = default) =>
        store.SaveGroupAsync(groupId, name, population, contactIds, cancellationToken);

    public Task DeleteGroupAsync(Guid groupId, CancellationToken cancellationToken = default) =>
        store.DeleteGroupAsync(groupId, cancellationToken);

    public async Task<IReadOnlyList<CampaignContactOption>> GetGroupMembersAsync(Guid groupId, CancellationToken cancellationToken = default)
    {
        CampaignGroup group = await store.GetGroupAsync(groupId, cancellationToken);
        return await store.GetContactsByIdsAsync(group.Population, group.ContactIds, cancellationToken);
    }

    public async Task<ConsentBatchResult> RecordGroupConsentAsync(
        Guid groupId,
        CampaignChannel channel,
        bool granted,
        string evidence,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(evidence))
            throw new ArgumentException("İzin kanıtı/açıklaması zorunludur; izin varsayılmaz.", nameof(evidence));
        CampaignGroup group = await store.GetGroupAsync(groupId, cancellationToken);
        int recorded = 0, missing = 0, failed = 0, withdrawn = 0;
        HashSet<int> withdrawnContacts = granted
            ? (await store.GetConsentsAsync(group.Population, null, cancellationToken))
                .Where(item => item.Channel == channel)
                .GroupBy(item => item.ContactId)
                .Where(items => items.OrderBy(item => item.RecordedAtUtc).Last().IsWithdrawal)
                .Select(items => items.Key)
                .ToHashSet()
            : [];
        foreach (CampaignContactOption contact in await store.GetContactsByIdsAsync(group.Population, group.ContactIds, cancellationToken))
        {
            string endpoint = channel == CampaignChannel.Email ? contact.Email : contact.Phone;
            if (string.IsNullOrEmpty(CampaignStore.NormalizeEndpoint(channel, endpoint ?? string.Empty)))
            {
                missing++;
                continue;
            }
            if (withdrawnContacts.Contains(contact.ContactId))
            {
                withdrawn++;
                continue;
            }
            try
            {
                await store.SetConsentAsync(group.Population, contact.ContactId, channel, endpoint!, granted, evidence, cancellationToken);
                recorded++;
            }
            catch (CrmOptOutException)
            {
                withdrawn++;
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
            {
                failed++;
            }
        }
        return new ConsentBatchResult(recorded, missing, failed, withdrawn);
    }

    public async Task<GroupCampaignPreparation> PrepareGroupCampaignAsync(
        GroupCampaignRequest request,
        CancellationToken cancellationToken = default)
    {
        CampaignGroup group = await store.GetGroupAsync(request.GroupId, cancellationToken);
        if (group.ContactIds.Count == 0)
            throw new InvalidOperationException("Grupta müşteri yok. Önce gruba müşteri ekleyin.");
        int maximumAttachments = request.Channel == CampaignChannel.Email ? CampaignStore.MaximumEmailAttachmentCount : 1;
        if (request.AttachmentPaths.Count > maximumAttachments)
            throw new InvalidOperationException(request.Channel == CampaignChannel.Email
                ? $"E-postaya en fazla {maximumAttachments} dosya eklenebilir."
                : "WhatsApp şablonuna yalnızca 1 PDF eklenebilir.");
        if (request.Channel == CampaignChannel.WhatsApp && request.AttachmentPaths.Count > 0 &&
            request.WhatsAppTemplate?.HasDocumentHeader != true)
            throw new InvalidOperationException("Bu WhatsApp şablonu belge (PDF) başlığı içermiyor; ek gönderilemez.");

        ProviderReadiness readiness = await transport.CheckReadinessAsync(request.Channel, request.SenderIdentity, cancellationToken);
        if (!readiness.Ready)
            throw new InvalidOperationException("Gönderici hazır değil: " + (readiness.BlockReason ?? "bilinmeyen neden"));

        AudienceSelection audience = new(group.Population, group.ContactIds, false);
        AudiencePreview preview = await store.PreviewAudienceAsync(audience, request.Channel, cancellationToken);
        if (preview.EligibleCount == 0)
            throw new InvalidOperationException("Gruptaki hiçbir müşteriye bu kanaldan gönderilemiyor. " +
                DescribeExclusions(preview.Exclusions));

        CampaignDraftInput input = new(
            request.Name,
            request.Channel,
            audience,
            request.SenderIdentity,
            request.Channel == CampaignChannel.Email ? request.Subject : string.Empty,
            request.Channel == CampaignChannel.Email ? request.Content : string.Empty,
            request.Channel == CampaignChannel.WhatsApp ? request.WhatsAppTemplate : null,
            request.Channel == CampaignChannel.WhatsApp ? request.WhatsAppComponents : [],
            [],
            request.TimeZoneId,
            request.StartsAtUtc,
            request.EndsAtUtc,
            request.DailyWindows,
            request.MinimumSpacing,
            Math.Clamp(group.ContactIds.Count, 1, CampaignStore.MaximumAudienceSize),
            0m,
            false, // cost acknowledged only after the group-send Confirm dialog → Launch Approve ConfirmCost
            request.RepeatEveryDays);
        CampaignDraft draft = await SaveDraftAsync(null, input, 0, cancellationToken);
        if (request.AttachmentPaths.Count > 0)
        {
            List<CampaignAttachment> staged = [];
            foreach (string path in request.AttachmentPaths)
                staged.Add(await store.StageAttachmentAsync(draft.Id, path, cancellationToken));
            long total = staged.Sum(item => item.Length);
            int limit = request.Channel == CampaignChannel.Email
                ? CampaignStore.MaximumEmailAttachmentBytes : CampaignStore.MaximumWhatsAppAttachmentBytes;
            if (total > limit)
                throw new InvalidOperationException($"Eklerin toplam boyutu {total / 1024} KB; sınır {limit / 1024} KB. Dosyaları küçültün veya azaltın.");
            draft = await SaveDraftAsync(draft.Id, input with { AttachmentIds = staged.Select(item => item.Id).ToArray() },
                draft.Revision, cancellationToken);
        }
        ApprovalPreview approval = await PrepareApprovalAsync(draft.Id, draft.Revision, cancellationToken);
        CampaignSnapshot snapshot = await store.GetAsync(draft.Id, cancellationToken)
            ?? throw new InvalidOperationException("Hazırlanan kampanya okunamadı.");
        return new GroupCampaignPreparation(snapshot, new ApprovalPreviewSummary(
            group.ContactIds.Count,
            approval.Audience.EligibleCount,
            approval.Audience.Exclusions,
            approval.Attachments.Select(item => item.FileName).ToArray(),
            approval.ApprovalManifestHash));
    }

    public async Task<CampaignSnapshot> LaunchGroupCampaignAsync(
        Guid campaignId,
        long expectedRevision,
        string approvalManifestHash,
        CancellationToken cancellationToken = default)
    {
        // Gate before Approve so a failed launch leaves the draft editable.
        if (!await RefreshExecutionStateAsync(cancellationToken))
            throw new InvalidOperationException(
                "Gerçek müşteri gönderimi kapalı. Kampanya penceresinde Gelişmiş sekmesinden «Genel gönderim iznini aç» onayını verin, sonra tekrar deneyin.");
        CampaignSnapshot approved = await ApproveAsync(campaignId, expectedRevision, approvalManifestHash,
            new ApprovalAcknowledgement(true, true, true), cancellationToken);
        return await ActivateAsync(campaignId, approved.Revision, new ActivationAcknowledgement(true, true, true), cancellationToken);
    }

    public static string DescribeExclusions(IReadOnlyList<AudienceExclusion> exclusions)
    {
        if (exclusions.Count == 0)
            return "Hariç tutulan yok.";
        return "Hariç tutulanlar: " + string.Join(", ", exclusions.Select(item => $"{item.Count} {item.Reason switch
        {
            CampaignExclusionReason.MissingConsent => "izin kaydı yok",
            CampaignExclusionReason.OptedOut => "izni geri çekmiş",
            CampaignExclusionReason.InvalidEndpoint => "e-posta/telefon eksik veya geçersiz",
            CampaignExclusionReason.InactiveCustomer => "pasif/silinmiş müşteri",
            CampaignExclusionReason.DuplicateEndpoint => "aynı adres/numara tekrarı",
            CampaignExclusionReason.ConflictingConsent => "çelişkili izin",
            CampaignExclusionReason.Cooldown => "son 24 saatte WhatsApp gönderilmiş",
            _ => item.Reason.ToString()
        }}")) + ".";
    }

    public Task<AudiencePreview> PreviewAudienceAsync(
        AudienceSelection selection,
        CampaignChannel channel,
        CancellationToken cancellationToken = default) =>
        store.PreviewAudienceAsync(selection, channel, cancellationToken);

    public Task<CampaignAttachment> StageAttachmentAsync(
        Guid campaignId,
        string sourcePath,
        CancellationToken cancellationToken = default) =>
        store.StageAttachmentAsync(campaignId, sourcePath, cancellationToken);

    public async Task<WhatsAppTemplateApproval> VerifyWhatsAppTemplateAsync(
        string name,
        string languageCode,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(languageCode))
            throw new ArgumentException("Template name and language are required.");
        WhatsAppTemplateApproval template = await transport.VerifyWhatsAppTemplateAsync(
            name.Trim(), languageCode.Trim(), cancellationToken);
        await store.RecordVerifiedWhatsAppTemplateAsync(template, cancellationToken);
        return template;
    }

    public async Task<ApprovalPreview> PrepareApprovalAsync(
        Guid campaignId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        CampaignDraft draft = await store.GetDraftAsync(campaignId, cancellationToken);
        await ValidateDraftAsync(draft.Input, cancellationToken);
        AudiencePreview currentPreview = await store.PreviewAudienceAsync(draft.Input.Audience, draft.Input.Channel, cancellationToken);
        CampaignApprovalManifest manifest = await store.PrepareApprovalAsync(campaignId, expectedRevision, cancellationToken);
        AudiencePreview preview = new(manifest.Recipients, currentPreview.Exclusions);
        return new ApprovalPreview(manifest, preview, manifest.Attachments, manifest.Hash);
    }

    public async Task<CampaignSnapshot> ApproveAsync(
        Guid campaignId,
        long expectedRevision,
        string approvalManifestHash,
        ApprovalAcknowledgement acknowledgement,
        CancellationToken cancellationToken = default)
    {
        if (!acknowledgement.ConfirmExactAudience || !acknowledgement.ConfirmSender || !acknowledgement.ConfirmCost)
            throw new InvalidOperationException("Approval requires explicit confirmation of the exact audience, sender and campaign cost.");
        CampaignDraft draft = await store.GetDraftAsync(campaignId, cancellationToken);
        await ValidateDraftAsync(draft.Input, cancellationToken);
        // Cost gate is acknowledgement.ConfirmCost (required above). Draft CostAcknowledged is UI hint only.
        CampaignApprovalManifest manifest = await store.PrepareApprovalAsync(campaignId, expectedRevision, cancellationToken);
        if (!string.Equals(manifest.Hash, approvalManifestHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Approval preview changed. Review the current audience and prepare approval again.");
        return await store.ApproveAsync(manifest, cancellationToken);
    }

    public async Task<CampaignSnapshot> ActivateAsync(
        Guid campaignId,
        long expectedRevision,
        ActivationAcknowledgement acknowledgement,
        CancellationToken cancellationToken = default)
    {
        await RefreshExecutionStateAsync(cancellationToken);
        EnsureActivationAcknowledged(acknowledgement);
        CampaignSnapshot campaign = await store.GetAsync(campaignId, cancellationToken)
            ?? throw new KeyNotFoundException("Campaign was not found for this Windows owner.");
        if (campaign.Revision != expectedRevision || campaign.State is not (CampaignState.Approved or CampaignState.Paused))
            throw new InvalidOperationException("Only the current approved campaign revision can be activated.");
        ValidateDraft(campaign.Input);
        if (!ExecutionEnabled)
            throw new InvalidOperationException("Campaign execution is disabled by default. An explicit runtime enablement acknowledgement is required first.");
        // Cost was already confirmed at Approve (ConfirmCost). Draft CostAcknowledged is optional UI state.
        ProviderReadiness readiness = await transport.CheckReadinessAsync(
            campaign.Input.Channel, campaign.Input.SenderIdentity, cancellationToken);
        if (!readiness.Ready)
            throw new InvalidOperationException(readiness.BlockReason ?? "The selected sender is not ready for silent campaign execution.");
        CampaignApprovalManifest manifest = await store.GetApprovedManifestAsync(campaignId, cancellationToken);
        ValidateApprovedAttachments(manifest);
        CampaignState target = campaign.Input.StartsAtUtc > store.UtcNow ? CampaignState.Scheduled : CampaignState.Active;
        return await store.ChangeStateAsync(campaignId, expectedRevision, target, cancellationToken);
    }

    public Task<CampaignSnapshot> ScheduleAsync(
        Guid campaignId,
        long expectedRevision,
        ActivationAcknowledgement acknowledgement,
        CancellationToken cancellationToken = default) =>
        ActivateAsync(campaignId, expectedRevision, acknowledgement, cancellationToken);

    public Task<CampaignSnapshot> PauseAsync(Guid campaignId, long expectedRevision, CancellationToken cancellationToken = default) =>
        store.ChangeStateAsync(campaignId, expectedRevision, CampaignState.Paused, cancellationToken);

    public async Task<CampaignSnapshot> ResumeAsync(
        Guid campaignId,
        long expectedRevision,
        ActivationAcknowledgement acknowledgement,
        CancellationToken cancellationToken = default) =>
        await ActivateAsync(campaignId, expectedRevision, acknowledgement, cancellationToken);

    public async Task<CampaignSnapshot> CancelAsync(Guid campaignId, long expectedRevision, CancellationToken cancellationToken = default)
    {
        CampaignSnapshot cancelled = await store.ChangeStateAsync(campaignId, expectedRevision, CampaignState.CancelRequested, cancellationToken);
        if (await store.HasOutstandingSendsAsync(campaignId, cancellationToken))
            return cancelled;
        return await store.ChangeStateAsync(campaignId, cancelled.Revision, CampaignState.Cancelled, cancellationToken);
    }

    public Task<CampaignSnapshot?> GetAsync(Guid campaignId, CancellationToken cancellationToken = default) =>
        store.GetAsync(campaignId, cancellationToken);

    public Task<CampaignPage> ListAsync(CampaignQuery query, CancellationToken cancellationToken = default) =>
        store.ListAsync(query, cancellationToken);

    public Task<RecipientPage> GetRecipientsAsync(
        Guid campaignId,
        int offset = 0,
        int limit = 200,
        CancellationToken cancellationToken = default) =>
        store.GetRecipientsAsync(campaignId, offset, limit, cancellationToken);

    public Task<CampaignConsent> RecordConsentAsync(
        CampaignPopulation population,
        int contactId,
        CampaignChannel channel,
        string endpoint,
        bool granted,
        string evidence,
        CancellationToken cancellationToken = default,
        bool overrideCrmOptOut = false) =>
        // Default false: a normal consent click must not clear CRM WhatsApp opt-out (KVKK).
        // Pass overrideCrmOptOut:true only after an explicit heavier confirmation in the UI/tests.
        store.SetConsentAsync(population, contactId, channel, endpoint, granted, evidence, cancellationToken, overrideCrmOptOut);

    public Task<IReadOnlyList<CampaignConsent>> GetConsentLedgerAsync(
        CampaignPopulation? population = null,
        int? contactId = null,
        CancellationToken cancellationToken = default) =>
        store.GetConsentsAsync(population, contactId, cancellationToken);

    public async Task<IReadOnlyList<ProviderReadiness>> GetProviderReadinessAsync(CancellationToken cancellationToken = default)
    {
        List<ProviderReadiness> results = [];
        foreach (CampaignChannel channel in Enum.GetValues<CampaignChannel>())
        {
            ProviderReadiness result = await transport.CheckReadinessAsync(channel, string.Empty, cancellationToken);
            results.Add(result);
        }
        return results;
    }

    public Task<ProviderReadiness> GetProviderReadinessAsync(
        CampaignChannel channel,
        string senderIdentity,
        CancellationToken cancellationToken = default) =>
        transport.CheckReadinessAsync(channel, senderIdentity, cancellationToken);

    public Task<IReadOnlyList<CampaignSenderOption>> GetAvailableSendersAsync(
        CampaignChannel channel,
        CancellationToken cancellationToken = default) =>
        transport.GetAvailableSendersAsync(channel, cancellationToken);

    public Task<CampaignGraphToken> RequestMailSendConsentAsync(
        string accountId,
        CancellationToken cancellationToken = default) =>
        transport.RequestMailSendConsentAsync(accountId, cancellationToken);

    public async Task<DateTimeOffset?> GetNextEligibleTimeAsync(Guid campaignId, CancellationToken cancellationToken = default)
    {
        CampaignSnapshot campaign = await RequireCampaignAsync(campaignId, cancellationToken);
        if (campaign.State is not (CampaignState.Active or CampaignState.Scheduled))
            return null;
        DateTimeOffset now = store.UtcNow;
        DateTimeOffset candidate = campaign.Input.StartsAtUtc > now ? campaign.Input.StartsAtUtc : now;
        TimeZoneInfo zone = CampaignWindowSchedule.ResolveTimeZone(campaign.Input.TimeZoneId);
        DateTimeOffset? window = CampaignWindowSchedule.GetNextWindowStartUtc(zone, campaign.Input.DailyWindows, candidate);
        if (window is null || campaign.Input.EndsAtUtc is not null && window >= campaign.Input.EndsAtUtc)
            return null;
        DateTimeOffset? spacing = await store.GetNextDispatchUtcAsync(campaignId, cancellationToken);
        if (spacing is not null && spacing > window)
            window = CampaignWindowSchedule.GetNextWindowStartUtc(zone, campaign.Input.DailyWindows, spacing.Value);
        return window is not null && (campaign.Input.EndsAtUtc is null || window < campaign.Input.EndsAtUtc) ? window : null;
    }

    public async Task EnableExecutionAsync(
        ExecutionEnablementAcknowledgement acknowledgement,
        CancellationToken cancellationToken = default)
    {
        if (!acknowledgement.ConfirmRealCustomerDispatch ||
            !acknowledgement.ConfirmNoModelInference ||
            !acknowledgement.ConfirmNoInteractiveAuthentication)
            throw new InvalidOperationException("Enabling campaign execution requires explicit confirmation of dispatch, deterministic processing and silent authentication.");
        await store.SetExecutionEnabledAsync(true, cancellationToken);
        lock (executionGate)
            executionEnabled = true;
    }

    public async Task DisableExecutionAsync(CancellationToken cancellationToken = default)
    {
        await store.SetExecutionEnabledAsync(false, cancellationToken);
        lock (executionGate)
            executionEnabled = false;
    }

    public async Task<bool> RefreshExecutionStateAsync(CancellationToken cancellationToken = default)
    {
        bool enabled = await store.GetExecutionEnabledAsync(cancellationToken);
        lock (executionGate)
            executionEnabled = enabled;
        return enabled;
    }

    internal async Task RecoverInterruptedAsync(CancellationToken cancellationToken) =>
        await store.RecoverInterruptedAsync(cancellationToken);

    internal async Task<bool> DispatchOneAsync(CancellationToken cancellationToken)
    {
        await RefreshExecutionStateAsync(cancellationToken);
        if (!ExecutionEnabled || !await store.TryAcquireWorkerLeaseAsync(cancellationToken))
            return false;
        using CancellationTokenSource heartbeatStop = new();
        using CancellationTokenSource dispatch = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task heartbeat = KeepWorkerLeaseAsync(heartbeatStop.Token, dispatch);
        try
        {
            await store.RecoverInterruptedAsync(dispatch.Token);
            return await DispatchAsLeaderAsync(dispatch.Token);
        }
        finally
        {
            heartbeatStop.Cancel();
            await heartbeat;
        }
    }

    private async Task KeepWorkerLeaseAsync(CancellationToken stop, CancellationTokenSource dispatch)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(stop))
            {
                using CancellationTokenSource renewal = new(TimeSpan.FromSeconds(10));
                if (!await store.RenewWorkerLeaseAsync(renewal.Token))
                {
                    dispatch.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch
        {
            // Without a heartbeat no further authorization is safe.
            dispatch.Cancel();
        }
    }

    private async Task<bool> DispatchAsLeaderAsync(CancellationToken cancellationToken)
    {
        await RefreshExecutionStateAsync(cancellationToken);
        if (!ExecutionEnabled)
            return false;
        DateTimeOffset now = store.UtcNow;
        foreach (CampaignSnapshot queuedCampaign in await store.GetRunnableCampaignsAsync(cancellationToken))
        {
            CampaignSnapshot campaign = queuedCampaign;
            if (!ExecutionEnabled)
                return false;
            if (campaign.State == CampaignState.CancelRequested)
            {
                if (!await store.HasOutstandingSendsAsync(campaign.Id, cancellationToken))
                    await store.ChangeStateAsync(campaign.Id, campaign.Revision, CampaignState.Cancelled, cancellationToken);
                continue;
            }
            if (campaign.State == CampaignState.Scheduled)
            {
                if (campaign.Input.StartsAtUtc > now)
                    continue;
                campaign = await store.ChangeStateAsync(campaign.Id, campaign.Revision, CampaignState.Active, cancellationToken);
            }
            if (campaign.Input.EndsAtUtc is not null && now >= campaign.Input.EndsAtUtc.Value)
            {
                await store.CloseExpiredCampaignAsync(campaign.Id, campaign.Revision, cancellationToken);
                continue;
            }
            if (campaign.State != CampaignState.Active)
                continue;

            TimeZoneInfo zone;
            try
            {
                ValidateDraft(campaign.Input);
                zone = CampaignWindowSchedule.ResolveTimeZone(campaign.Input.TimeZoneId);
            }
            catch (ArgumentException)
            {
                await store.MarkCampaignFaultedAsync(campaign.Id, campaign.Revision, "CampaignPolicyInvalid", cancellationToken);
                return false;
            }
            catch (InvalidOperationException)
            {
                await store.MarkCampaignFaultedAsync(campaign.Id, campaign.Revision, "CampaignPolicyInvalid", cancellationToken);
                return false;
            }
            if (!CampaignWindowSchedule.IsWithinWindow(zone, campaign.Input.DailyWindows, now))
                continue;
            CampaignApprovalManifest manifest;
            IReadOnlyList<CampaignAttachmentContent> attachmentContents;
            try
            {
                manifest = await store.GetApprovedManifestAsync(campaign.Id, cancellationToken);
                List<CampaignAttachmentContent> staged = [];
                foreach (CampaignAttachment attachment in manifest.Attachments)
                    staged.Add(await store.ReadAttachmentAsync(attachment, manifest.Draft.Channel, cancellationToken));
                attachmentContents = staged;
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                await store.MarkCampaignFaultedAsync(campaign.Id, campaign.Revision, "AttachmentIntegrityFailure", cancellationToken);
                return false;
            }
            foreach (CampaignRecipient recipient in await store.GetPendingRecipientsAsync(campaign.Id, cancellationToken))
            {
                if (!ExecutionEnabled)
                    return false;
                if (await DispatchRecipientAsync(campaign, recipient, attachmentContents, cancellationToken))
                    return true;
            }

            if (!await store.HasRetryableRecipientsAsync(campaign.Id, cancellationToken))
            {
                DateTimeOffset? nextRound = GetNextRoundUtc(campaign.Input, store.UtcNow);
                if (nextRound is null || !await store.StartNextRoundAsync(campaign.Id, campaign.Revision, nextRound.Value, cancellationToken))
                    await store.ChangeStateAsync(campaign.Id, campaign.Revision, CampaignState.Completed, cancellationToken);
            }
        }
        return false;
    }

    private async Task<CampaignSnapshot> RequireCampaignAsync(Guid campaignId, CancellationToken cancellationToken) =>
        await store.GetAsync(campaignId, cancellationToken)
        ?? throw new KeyNotFoundException("Campaign was not found for this Windows owner.");

    private async Task<bool> DispatchRecipientAsync(
        CampaignSnapshot campaign,
        CampaignRecipient recipient,
        IReadOnlyList<CampaignAttachmentContent> attachmentContents,
        CancellationToken cancellationToken)
    {
        string? claim = await store.TryClaimAsync(
            campaign.Id, recipient.ContactKey, campaign.Revision, store.UtcNow, cancellationToken);
        if (claim is null)
            return false;

        CampaignSendAuthorization? authorization = null;
        try
        {
            authorization = await store.AuthorizeDispatchAsync(
                campaign, recipient, claim, store.UtcNow, cancellationToken);
        }
        finally
        {
            if (authorization is null)
            {
                using CancellationTokenSource release = new(TimeSpan.FromSeconds(10));
                await store.ReleaseClaimAsync(campaign.Id, recipient.ContactKey, claim, release.Token);
            }
        }

        if (authorization is null)
            return false;

        CampaignSendResult result;
        try
        {
            result = await transport.SendAsync(new CampaignSendRequest(
                campaign.Id,
                authorization.Manifest.Draft.Channel == CampaignChannel.WhatsApp
                    ? "+" + authorization.Recipient.NormalizedEndpoint
                    : authorization.Recipient.Endpoint,
                authorization.Manifest.Draft,
                attachmentContents,
                authorization.AttemptId), cancellationToken);
        }
        catch (Exception exception)
        {
            result = new CampaignSendResult(
                CampaignSendDisposition.Unknown,
                exception is OperationCanceledException ? "CancelledAfterDispatchAuthorization" : "TransportOutcomeUnknown");
        }

        using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(15));
        await store.CompleteAttemptAsync(
            authorization,
            result,
            store.UtcNow,
            authorization.Manifest.Draft.MinimumSpacing,
            cleanup.Token);
        return true;
    }

    private static void EnsureActivationAcknowledged(ActivationAcknowledgement acknowledgement)
    {
        if (!acknowledgement.ConfirmRealCustomerSending || !acknowledgement.ConfirmCost || !acknowledgement.ConfirmSender)
            throw new InvalidOperationException("Activation requires explicit confirmation of real customer sends, cost and sender identity.");
    }

    private async Task ValidateDraftAsync(CampaignDraftInput input, CancellationToken cancellationToken, bool allowMissingDocument = false)
    {
        ValidateDraft(input, allowMissingDocument);
        if (input.Channel != CampaignChannel.WhatsApp)
            return;

        WhatsAppTemplateApproval requested = input.WhatsAppTemplate
            ?? throw new ArgumentException("WhatsApp campaigns require an explicitly verified marketing template.", nameof(input));
        if (requested.VerifiedAtUtc < store.UtcNow.AddDays(-1))
            throw new InvalidOperationException("WhatsApp template verification is too old for draft approval.");
        WhatsAppTemplateApproval? recorded = await store.GetVerifiedWhatsAppTemplateAsync(
            requested.Name, requested.LanguageCode, cancellationToken);
        if (recorded is null ||
            !string.Equals(recorded.Name, requested.Name, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(recorded.LanguageCode, requested.LanguageCode, StringComparison.OrdinalIgnoreCase) ||
            recorded.IsVerifiedMarketing != requested.IsVerifiedMarketing ||
            !recorded.SchemaSupported || !requested.SchemaSupported ||
            recorded.HasDocumentHeader != requested.HasDocumentHeader ||
            recorded.BodyParameterCount != requested.BodyParameterCount ||
            recorded.VerifiedAtUtc != requested.VerifiedAtUtc ||
            recorded.VerifiedAtUtc < store.UtcNow.AddDays(-1))
            throw new InvalidOperationException("WhatsApp template must be freshly verified through the explicit provider-verification action before saving or approving this campaign.");
    }

    private void ValidateDraft(CampaignDraftInput input, bool allowMissingDocument = false)
    {
        ValidateCommonDraft(input);
        if (input.Channel == CampaignChannel.Email)
        {
            ValidateEmailDraft(input);
            return;
        }

        if (input.Channel == CampaignChannel.WhatsApp)
        {
            ValidateWhatsAppDraft(input, allowMissingDocument);
            return;
        }

        throw new ArgumentOutOfRangeException(nameof(input.Channel));
    }

    private static void ValidateCommonDraft(CampaignDraftInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Trim().Length > MaximumNameLength)
            throw new ArgumentException($"Campaign name is required and must be at most {MaximumNameLength} characters.");
        if (input.Audience is null || input.Audience.ContactIds is null || input.Audience.ContactIds.Any(id => id <= 0) ||
            input.Audience.ContactIds.Distinct().Count() != input.Audience.ContactIds.Count)
            throw new ArgumentException("Campaign audience selection is invalid.");
        if (string.IsNullOrWhiteSpace(input.SenderIdentity) || input.SenderIdentity.Trim().Length > 512)
            throw new ArgumentException("A pinned provider sender identity is required.");
        if (input.StartsAtUtc.Offset != TimeSpan.Zero || input.EndsAtUtc is not null && input.EndsAtUtc.Value.Offset != TimeSpan.Zero)
            throw new ArgumentException("Campaign schedule instants must use UTC.");
        if (input.EndsAtUtc is not null && input.EndsAtUtc <= input.StartsAtUtc)
            throw new ArgumentException("Campaign end time must be later than its start time.");
        _ = CampaignWindowSchedule.ResolveTimeZone(input.TimeZoneId);
        CampaignWindowSchedule.ValidateWindows(input.DailyWindows);
        if (input.MinimumSpacing < TimeSpan.FromSeconds(30) || input.MinimumSpacing > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(input.MinimumSpacing), "Campaign spacing must be between 30 seconds and 24 hours.");
        if (input.MaximumRecipients is < 1 or > CampaignStore.MaximumAudienceSize)
            throw new ArgumentOutOfRangeException(nameof(input.MaximumRecipients), $"Campaign recipient cap must be between 1 and {CampaignStore.MaximumAudienceSize}.");
        if (input.EstimatedCost < 0)
            throw new ArgumentOutOfRangeException(nameof(input.EstimatedCost), "Estimated cost cannot be negative.");
        int maximumAttachments = input.Channel == CampaignChannel.Email ? CampaignStore.MaximumEmailAttachmentCount : 1;
        if (input.AttachmentIds is null || input.AttachmentIds.Count > maximumAttachments ||
            input.AttachmentIds.Distinct().Count() != input.AttachmentIds.Count)
            throw new ArgumentException($"Select at most {maximumAttachments} attachment(s) and do not repeat them.");
        if (input.RepeatEveryDays is < 0 or > CampaignStore.MaximumRepeatEveryDays)
            throw new ArgumentOutOfRangeException(nameof(input.RepeatEveryDays), $"Repeat interval must be between 0 and {CampaignStore.MaximumRepeatEveryDays} days.");
    }

    internal static DateTimeOffset? GetNextRoundUtc(CampaignDraftInput input, DateTimeOffset now)
    {
        if (input.RepeatEveryDays <= 0)
            return null;
        TimeSpan interval = TimeSpan.FromDays(input.RepeatEveryDays);
        long elapsedRounds = now <= input.StartsAtUtc ? 0 : (now - input.StartsAtUtc).Ticks / interval.Ticks;
        DateTimeOffset next = input.StartsAtUtc + TimeSpan.FromTicks(interval.Ticks * (elapsedRounds + 1));
        return input.EndsAtUtc is not null && next >= input.EndsAtUtc.Value ? null : next;
    }

    private static void ValidateEmailDraft(CampaignDraftInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Subject) || input.Subject.Length > MaximumSubjectLength)
            throw new ArgumentException($"Email subject is required and must be at most {MaximumSubjectLength} characters.");
        if (string.IsNullOrWhiteSpace(input.Content) || input.Content.Length > MaximumContentLength)
            throw new ArgumentException($"Email content is required and must be at most {MaximumContentLength} characters.");
        if (input.WhatsAppTemplate is not null || input.WhatsAppComponents.Count > 0)
            throw new ArgumentException("WhatsApp template data is not valid for an email campaign.");
    }

    private void ValidateWhatsAppDraft(CampaignDraftInput input, bool allowMissingDocument)
    {
        if (input.WhatsAppComponents is null ||
            input.WhatsAppComponents.Count > 1 ||
            input.WhatsAppComponents.Any(component =>
                component is null ||
                component.Parameters is null ||
                component.Parameters.Any(parameter => parameter is null)))
            throw new ArgumentException("WhatsApp template components must contain at most one valid body component.");
        WhatsAppTemplateApproval template = input.WhatsAppTemplate
            ?? throw new ArgumentException("WhatsApp campaigns require an explicitly verified marketing template.");
        if (!template.IsVerifiedMarketing || !template.SchemaSupported || template.VerifiedAtUtc is null ||
            template.VerifiedAtUtc > store.UtcNow ||
            string.IsNullOrWhiteSpace(template.Name) || string.IsNullOrWhiteSpace(template.LanguageCode))
            throw new ArgumentException("WhatsApp template is not verified as an approved marketing template.");
        if (!string.IsNullOrWhiteSpace(input.Content) || !string.IsNullOrWhiteSpace(input.Subject))
            throw new ArgumentException("WhatsApp authored text must be mapped into approved template parameters, not sent as free-form text.");
        int bodyCount = input.WhatsAppComponents
            .Where(component => string.Equals(component.Type, "body", StringComparison.OrdinalIgnoreCase))
            .Sum(component => component.Parameters.Count);
        int totalParameters = input.WhatsAppComponents.Sum(component => component.Parameters.Count);
        int totalParameterCharacters = input.WhatsAppComponents
            .SelectMany(component => component.Parameters)
            .Sum(parameter => parameter.Length);
        if (bodyCount != template.BodyParameterCount ||
            totalParameters > MaximumWhatsAppParameterCount ||
            totalParameterCharacters > MaximumContentLength ||
            input.WhatsAppComponents.Any(component => !string.Equals(component.Type, "body", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("WhatsApp components do not match the verified template schema or exceed the message safety limits.");
        if (input.AttachmentIds.Count > 0 && !template.HasDocumentHeader)
            throw new ArgumentException("WhatsApp PDF attachments require an approved document-header template.");
        if (!allowMissingDocument && template.HasDocumentHeader && input.AttachmentIds.Count != 1)
            throw new ArgumentException("A document-header template requires exactly one staged PDF attachment.");
    }

    private static void ValidateApprovedAttachments(CampaignApprovalManifest manifest)
    {
        foreach (CampaignAttachment attachment in manifest.Attachments)
        {
            CampaignAttachmentPolicy.ValidateDescriptor(attachment, manifest.Draft.Channel);
            int maximum = manifest.Draft.Channel == CampaignChannel.Email
                ? CampaignStore.MaximumEmailAttachmentBytes : CampaignStore.MaximumWhatsAppAttachmentBytes;
            if (attachment.Length is <= 0 || attachment.Length > maximum)
                throw new InvalidDataException("Approved campaign attachment exceeds the channel size limit.");
        }
    }
}

public sealed record ApprovalPreview(
    CampaignApprovalManifest Manifest,
    AudiencePreview Audience,
    IReadOnlyList<CampaignAttachment> Attachments,
    string ApprovalManifestHash);
