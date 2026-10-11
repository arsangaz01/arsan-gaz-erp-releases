using System.Diagnostics;
using System.Net;
using System.Text.Json;
using ArsanGazERP.Models;
using ArsanGazERP.Services;
using Microsoft.Data.Sqlite;

namespace ArsanGazERP.AgentWatcher.Tests;

internal static class CampaignTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 4, 17, 30, 0, TimeSpan.Zero);

    public static async Task RunAsync()
    {
        await ScheduledCampaignStartsWhenDueAsync();
        await CompletionExaminesEntireAudienceAsync();
        await LegacyConsentIsNotEndpointEvidenceAsync();
        await ClaimsRecoverAfterLeaseExpiryAndPauseAsync();
        await LiveLeadershipAndAbandonedSendsAreFencedAsync();
        await LateRejectionNeverResendsAnAbandonedAttemptAsync();
        await CampaignPacingSurvivesReservationOverwriteAsync();
        await WhatsAppApprovalSurvivesMultipleDaysAsync();
        await DuplicateCrmOptOutSuppressesBothPopulationsAsync();
        await ErpWhatsAppSharesManualCrmCooldownAsync();
        await WhatsAppUnsupportedSchemasAndRequiredDocumentAreRejectedAsync();
        await MediaUploadFailuresAreSafeBoundedRetriesAsync();
        await KnownOutcomeSurvivesShutdownCancellationAsync();
        await WabaConfigurationPreservesOtherSettingsAsync();
        await SchemaUpgradeIsAdditiveAndRepeatableAsync();
        await ChannelAndPopulationConsentAreIndependentAsync();
        await ConsentIsEndpointBoundAndWithdrawalWinsAsync();
        await ConsentWithdrawalAfterApprovalFencesDispatchAsync();
        await WhatsAppTemplateMustBeVerifiedThroughExplicitActionAsync();
        await ApprovalFreezesAudienceAndDraftRevisionAsync();
        await OwnersCannotReadEachOthersCampaignsAsync();
        await ExecutionIsDisabledWithoutTouchingStorageAsync();
        await ClaimsAreExclusiveAsync();
        await RuntimeGateIsSharedAndFencesDispatchAsync();
        await GraphUsesAnIndividualRequestAndDoesNotClaimDeliveryAsync();
        await UnknownOutcomesAreQuarantinedAcrossRestartAsync();
        await InterruptedDispatchBecomesUnknownAsync();
        await SafeRejectionsRetryOnlyWithProviderEvidenceAsync();
        await CancellationBeforeActivationCannotDispatchAsync();
        await AttachmentsAreOwnerStagedAndHashCheckedAsync();
        await WhatsAppVerificationAndDocumentPayloadUseMockHttpAsync();
        WindowsRespectHalfOpenOvernightAndDstBoundaries();
        await SchedulerDrainsWhenCancelledAsync();
        await SchedulerSurvivesFailureReporterExceptionAsync();
        await GroupsPersistAndValidateMembersAsync();
        await GroupCampaignSendsIndividuallyWithSeveralAttachmentsAsync();
        await GroupWhatsAppUsesNormalizedLocalPhoneAsync();
        await GroupConsentNeverOverridesWithdrawalAsync();
        await GmailSenderSettingsAndReadinessAsync();
        await RecurringCampaignRunsRoundsUntilEndAsync();
    }

    private static GroupCampaignRequest GroupRequest(Fixture fixture, Guid groupId, IReadOnlyList<string>? attachments = null) => new(
        groupId, "Group campaign", CampaignChannel.Email, "test-account-id", "Kampanya", "Merhaba",
        null, [], attachments ?? [], "UTC", fixture.Clock.Now.AddMinutes(-1), null,
        [new CampaignDailyWindow(new TimeOnly(0, 0), new TimeOnly(23, 59))], TimeSpan.FromSeconds(30), 0);

    private static async Task GroupsPersistAndValidateMembersAsync()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        CampaignService service = fixture.Service(new StubTransport());
        CampaignGroup saved = await service.SaveGroupAsync(null, " Bayiler ", CampaignPopulation.CrmCompanies, [2, 1]);
        Check(saved.Name == "Bayiler" && saved.ContactIds.SequenceEqual([1, 2]), "Group names are trimmed and members stored in order.");
        await ThrowsAsync<InvalidOperationException>(() => service.SaveGroupAsync(null, "bayiler", CampaignPopulation.CrmCompanies, [1]),
            "Group names must be unique per owner, case-insensitively.");
        await ThrowsAsync<InvalidOperationException>(() => service.SaveGroupAsync(null, "Hayalet", CampaignPopulation.CrmCompanies, [999]),
            "Groups cannot reference unknown customers.");
        await ThrowsAsync<ArgumentException>(() => service.SaveGroupAsync(null, "Tekrar", CampaignPopulation.CrmCompanies, [1, 1]),
            "Groups cannot contain duplicate members.");
        CampaignGroup renamed = await service.SaveGroupAsync(saved.Id, "Bayiler 2", CampaignPopulation.CrmCompanies, [3]);
        IReadOnlyList<CampaignGroup> listed = await service.ListGroupsAsync();
        Check(listed.Count == 1 && listed[0].Name == "Bayiler 2" && listed[0].ContactIds.SequenceEqual([3]),
            "Saving an existing group updates it in place.");
        Check((await service.GetGroupMembersAsync(renamed.Id)).Single().DisplayName == "CRM Three", "Group members resolve to customers.");
        CampaignStore otherOwner = new(fixture.CrmPath, fixture.ErpPath, fixture.AttachmentPath, "S-1-5-21-other", () => fixture.Clock.Now);
        Check((await otherOwner.ListGroupsAsync(default)).Count == 0, "Groups are isolated per Windows owner.");
        await service.DeleteGroupAsync(saved.Id);
        Check((await service.ListGroupsAsync()).Count == 0, "Deleted groups disappear.");
    }

    private static async Task GroupCampaignSendsIndividuallyWithSeveralAttachmentsAsync()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.InsertCrmContactAsync(10, "Bayi A", "a@example.test", "");
        await fixture.InsertCrmContactAsync(11, "Bayi B", "b@example.test", "");
        await fixture.InsertCrmContactAsync(12, "Bayi C", "c@example.test", "");
        StubTransport transport = new();
        CampaignService service = fixture.Service(transport);
        CampaignGroup group = await service.SaveGroupAsync(null, "Bayiler", CampaignPopulation.CrmCompanies, [10, 11, 12]);
        CampaignGroup noConsent = await service.SaveGroupAsync(null, "Izinsiz", CampaignPopulation.CrmCompanies, [2]);
        await ThrowsAsync<InvalidOperationException>(() => service.PrepareGroupCampaignAsync(GroupRequest(fixture, noConsent.Id)),
            "A group without recorded consent must not be prepared for sending.");
        Check(transport.SendCount == 0, "Preparation never sends.");

        ConsentBatchResult consent = await service.RecordGroupConsentAsync(group.Id, CampaignChannel.Email, true, "Synthetic signed form");
        Check(consent.Recorded == 3 && consent.MissingEndpoint == 0 && consent.Failed == 0, "Group consent records every member endpoint.");

        string pdf = Path.Combine(fixture.Root, "katalog.pdf");
        string png = Path.Combine(fixture.Root, "afis.png");
        string csv = Path.Combine(fixture.Root, "fiyat.csv");
        await File.WriteAllBytesAsync(pdf, "%PDF-1.4\n% synthetic\n"u8.ToArray());
        await File.WriteAllBytesAsync(png, [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 0]);
        await File.WriteAllTextAsync(csv, "urun;fiyat\ntup;100\n");
        GroupCampaignPreparation prepared = await service.PrepareGroupCampaignAsync(GroupRequest(fixture, group.Id, [pdf, png, csv]));
        Check(prepared.Summary.EligibleCount == 3 && prepared.Summary.AttachmentNames.Count == 3 && transport.SendCount == 0,
            "Preparation freezes the group audience and all attachments without sending.");
        CampaignSnapshot launched = await service.LaunchGroupCampaignAsync(prepared.Draft.Id, prepared.Draft.Revision, prepared.Summary.ApprovalManifestHash);
        Check(launched.State == CampaignState.Active && service.ExecutionEnabled, "One confirmation approves and activates the group campaign.");
        for (int i = 0; i < 3; i++)
        {
            Check(await service.DispatchOneAsync(default), "Each group member must receive a dispatch.");
            fixture.Clock.Advance(TimeSpan.FromSeconds(31));
        }
        await service.DispatchOneAsync(default);
        Check(transport.Requests.Select(item => item.Recipient).OrderBy(item => item)
                .SequenceEqual(["a@example.test", "b@example.test", "c@example.test"]) &&
            transport.Requests.All(item => item.Attachments.Count == 3),
            "Every customer gets an individual message carrying all attachments.");
        Check((await service.GetAsync(launched.Id))!.State == CampaignState.Completed, "A one-time group campaign completes.");
    }

    private static async Task GroupConsentNeverOverridesWithdrawalAsync()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.InsertCrmContactAsync(30, "Ret Diyen", "ret@example.test", "");
        await fixture.InsertCrmContactAsync(31, "Yeni Firma", "yeni@example.test", "");
        await fixture.InsertCrmContactAsync(32, "Postasiz", "", "");
        Check(CampaignStore.NormalizeEndpoint(CampaignChannel.Email, "") == string.Empty &&
            CampaignStore.NormalizeEndpoint(CampaignChannel.Email, "   ") == string.Empty, "Blank e-mail endpoints normalize to empty instead of throwing.");
        CampaignService service = fixture.Service(new StubTransport());
        await service.RecordConsentAsync(CampaignPopulation.CrmCompanies, 30, CampaignChannel.Email, "ret@example.test", false, "Synthetic RET reply");
        CampaignGroup group = await service.SaveGroupAsync(null, "Karma", CampaignPopulation.CrmCompanies, [30, 31, 32]);
        ConsentBatchResult consent = await service.RecordGroupConsentAsync(group.Id, CampaignChannel.Email, true, "Synthetic B2B basis");
        Check(consent.Recorded == 1 && consent.SkippedWithdrawn == 1 && consent.MissingEndpoint == 1,
            "A bulk consent must skip withdrawn contacts and report members without e-mail.");
        AudiencePreview preview = await service.PreviewAudienceAsync(
            new AudienceSelection(CampaignPopulation.CrmCompanies, [30, 31, 32], false), CampaignChannel.Email);
        Check(preview.Recipients.Select(item => item.ContactId).SequenceEqual([31]), "An opted-out firm never re-enters the audience through a group consent.");

        await fixture.InsertCrmContactAsync(33, "Kartta Ret", "", "0532 333 44 55");
        await fixture.InsertCrmContactAsync(34, "Kartta Izinli", "", "0532 333 44 66");
        await fixture.ExecuteCrmAsync("UPDATE CrmCompanies SET WhatsAppOptOutAtUtc='2026-10-04T17:30:00.0000000+00:00' WHERE Id=33;");
        CampaignGroup whatsApp = await service.SaveGroupAsync(null, "Kart reddi", CampaignPopulation.CrmCompanies, [33, 34]);
        ConsentBatchResult cardConsent = await service.RecordGroupConsentAsync(whatsApp.Id, CampaignChannel.WhatsApp, true, "Synthetic B2B basis");
        Check(cardConsent.Recorded == 1 && cardConsent.SkippedWithdrawn == 1,
            "A group WhatsApp consent must skip firms whose CRM card records an opt-out.");
        AudiencePreview cardPreview = await service.PreviewAudienceAsync(
            new AudienceSelection(CampaignPopulation.CrmCompanies, [33, 34], false), CampaignChannel.WhatsApp);
        Check(cardPreview.Recipients.Select(item => item.ContactId).SequenceEqual([34]),
            "A CRM card opt-out must survive a group consent and keep the firm out of the audience.");
        await service.RecordConsentAsync(CampaignPopulation.CrmCompanies, 33, CampaignChannel.WhatsApp, "0532 333 44 55", true, "Synthetic explicit re-consent");
        AudiencePreview reconsented = await service.PreviewAudienceAsync(
            new AudienceSelection(CampaignPopulation.CrmCompanies, [33, 34], false), CampaignChannel.WhatsApp);
        Check(reconsented.Recipients.Select(item => item.ContactId).OrderBy(id => id).SequenceEqual([33, 34]),
            "An explicit single-firm consent may still clear an earlier CRM card opt-out.");
    }

    private static async Task GmailSenderSettingsAndReadinessAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "gmail-sender-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            GmailSenderService gmail = new(Path.Combine(root, "gmail-sender.bin"));
            using CampaignTransport transport = CampaignTransport.CreateWithoutMicrosoft(gmail);
            Check((await transport.GetAvailableSendersAsync(CampaignChannel.Email, default)).Count == 0, "No Gmail sender is offered before setup.");
            Check(!(await transport.CheckReadinessAsync(CampaignChannel.Email, "gmail:ozelgazlar@example.test", default)).Ready,
                "An unconfigured Gmail sender is not ready.");
            gmail.SaveSettings("ozelgazlar@example.test", "abcd efgh ijkl mnop", "Arsan Gaz");
            byte[] raw = await File.ReadAllBytesAsync(Path.Combine(root, "gmail-sender.bin"));
            Check(!System.Text.Encoding.UTF8.GetString(raw).Contains("abcdefgh", StringComparison.Ordinal), "The app password is never stored in clear text.");
            Check(gmail.LoadSettings().AppPassword == "abcdefghijklmnop", "Spaces Google shows in app passwords are removed.");
            IReadOnlyList<CampaignSenderOption> senders = await transport.GetAvailableSendersAsync(CampaignChannel.Email, default);
            Check(senders.Count == 1 && senders[0].Identity == "gmail:ozelgazlar@example.test", "The configured Gmail sender is offered.");
            Check((await transport.CheckReadinessAsync(CampaignChannel.Email, "gmail:ozelgazlar@example.test", default)).Ready &&
                !(await transport.CheckReadinessAsync(CampaignChannel.Email, "gmail:baska@example.test", default)).Ready,
                "Readiness is pinned to the configured address.");
            string once = GmailSenderService.WithOptOutFooter("Merhaba", "ozelgazlar@example.test");
            Check(once.Contains("RET", StringComparison.Ordinal) && GmailSenderService.WithOptOutFooter(once, "x@example.test") == once,
                "The opt-out footer is appended exactly once.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    private static async Task GroupWhatsAppUsesNormalizedLocalPhoneAsync()
    {
        Check(CampaignStore.NormalizeEndpoint(CampaignChannel.WhatsApp, "0532 111 22 33") == "905321112233" &&
            CampaignStore.NormalizeEndpoint(CampaignChannel.WhatsApp, "532 111 22 33") == "905321112233" &&
            CampaignStore.NormalizeEndpoint(CampaignChannel.WhatsApp, "0090 532 111 22 33") == "905321112233" &&
            CampaignStore.NormalizeEndpoint(CampaignChannel.WhatsApp, "0322 111 22") == string.Empty,
            "Turkish local mobile numbers normalize to E.164 without guessing short numbers.");
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.InsertCrmContactAsync(20, "Yerel", "", "0532 111 22 33");
        await fixture.InsertCrmContactAsync(21, "Telefonsuz", "", "");
        StubTransport transport = new();
        CampaignService service = fixture.Service(transport);
        CampaignGroup group = await service.SaveGroupAsync(null, "Yerel", CampaignPopulation.CrmCompanies, [20, 21]);
        ConsentBatchResult consent = await service.RecordGroupConsentAsync(group.Id, CampaignChannel.WhatsApp, true, "Synthetic opt-in");
        Check(consent.Recorded == 1 && consent.MissingEndpoint == 1, "Members without a phone are reported, not guessed.");
        WhatsAppTemplateApproval template = await service.VerifyWhatsAppTemplateAsync("approved_offer", "en_US");
        GroupCampaignRequest request = GroupRequest(fixture, group.Id) with
        {
            Channel = CampaignChannel.WhatsApp, SenderIdentity = "test-whatsapp-sender", WhatsAppTemplate = template
        };
        GroupCampaignPreparation prepared = await service.PrepareGroupCampaignAsync(request);
        await service.LaunchGroupCampaignAsync(prepared.Draft.Id, prepared.Draft.Revision, prepared.Summary.ApprovalManifestHash);
        Check(await service.DispatchOneAsync(default) && transport.Requests.Single().Recipient == "+905321112233",
            "WhatsApp dispatch uses the normalized international number.");
    }

    private static async Task RecurringCampaignRunsRoundsUntilEndAsync()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.GrantCrmEmailConsentAsync(1);
        await fixture.GrantCrmEmailConsentAsync(2);
        StubTransport transport = new();
        CampaignService service = fixture.Service(transport);
        DateTimeOffset start = fixture.Clock.Now.AddMinutes(-1);
        CampaignDraftInput input = fixture.EmailInput() with
        {
            StartsAtUtc = start,
            EndsAtUtc = start.AddDays(2.5),
            RepeatEveryDays = 1
        };
        CampaignSnapshot campaign = await fixture.CreateCampaignAsync(service, input);
        async Task RunRoundAsync()
        {
            Check(await service.DispatchOneAsync(default), "First member of the round sends.");
            fixture.Clock.Advance(TimeSpan.FromSeconds(31));
            Check(await service.DispatchOneAsync(default), "Second member of the round sends.");
            fixture.Clock.Advance(TimeSpan.FromSeconds(31));
            Check(!await service.DispatchOneAsync(default), "Round end schedules the next round without sending.");
        }

        await RunRoundAsync();
        RecipientPage waiting = await service.GetRecipientsAsync(campaign.Id);
        Check(transport.SendCount == 2 && (await service.GetAsync(campaign.Id))!.State == CampaignState.Active &&
            waiting.Items.All(item => item.State == CampaignRecipientState.Pending && item.NextAttemptAtUtc == start.AddDays(1)),
            "A recurring campaign stays active and waits for the next round.");
        fixture.Clock.Advance(TimeSpan.FromHours(1));
        Check(!await service.DispatchOneAsync(default) && transport.SendCount == 2, "Waiting recipients are not sent early.");
        CampaignSnapshot current = (await service.GetAsync(campaign.Id))!;
        CampaignSnapshot paused = await service.PauseAsync(campaign.Id, current.Revision);
        await ThrowsAsync<InvalidOperationException>(() => service.SaveDraftAsync(campaign.Id, input, paused.Revision),
            "A campaign with send history cannot be edited even after its counters reset.");
        await service.ResumeAsync(campaign.Id, paused.Revision, new ActivationAcknowledgement(true, true, true));

        fixture.Clock.Advance(start.AddDays(1) - fixture.Clock.Now);
        await RunRoundAsync();
        fixture.Clock.Advance(start.AddDays(2) - fixture.Clock.Now);
        await RunRoundAsync();
        Check(transport.SendCount == 6 && (await service.GetAsync(campaign.Id))!.State == CampaignState.Completed,
            "Recurring rounds stop at the campaign end date.");
    }

    private static async Task ScheduledCampaignStartsWhenDueAsync()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.GrantCrmEmailConsentAsync(2);
        StubTransport transport = new();
        CampaignService service = fixture.Service(transport);
        CampaignSnapshot scheduled = await fixture.CreateCampaignAsync(service,
            fixture.EmailInput() with { StartsAtUtc = fixture.Clock.Now.AddMinutes(1) });
        Check(scheduled.State == CampaignState.Scheduled && !await service.DispatchOneAsync(default),
            "A future campaign must wait without sending.");
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        Check(await service.DispatchOneAsync(default) && transport.SendCount == 1,
            "Scheduled -> Active must be legal when the clock reaches the start.");
    }

    private static async Task CompletionExaminesEntireAudienceAsync()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await using (SqliteConnection connection = fixture.OpenCrm())
        {
            await using SqliteCommand seed = connection.CreateCommand();
            seed.CommandText = """
WITH RECURSIVE n(id) AS (SELECT 10 UNION ALL SELECT id+1 FROM n WHERE id<609)
INSERT INTO CrmCompanies(Id,CompanyName,Email,Phone,WhatsAppMarketingOptIn)
SELECT id,'Synthetic '||id,'bulk.'||id||'@example.test','',0 FROM n;
INSERT INTO CampaignConsents(OwnerSid,Population,ContactId,Channel,Endpoint,NormalizedEndpoint,Granted,Evidence,RecordedUtc,IsWithdrawal)
SELECT $owner,'CrmCompanies',Id,'Email',Email,Email,1,'Synthetic consent',$now,0 FROM CrmCompanies WHERE Id>=10;
""";
            seed.Parameters.AddWithValue("$owner", Fixture.OwnerSid);
            seed.Parameters.AddWithValue("$now", fixture.Clock.Now.ToString("O"));
            await seed.ExecuteNonQueryAsync();
        }
        StubTransport transport = new();
        CampaignService service = fixture.Service(transport);
        CampaignSnapshot active = await fixture.CreateCampaignAsync(service,
            fixture.EmailInput() with { MaximumRecipients = 1000 });
        await using (SqliteConnection connection = fixture.OpenCrm())
        {
            await using SqliteCommand terminalize = connection.CreateCommand();
            terminalize.CommandText = """
UPDATE CampaignRecipients SET State='Accepted' WHERE Id IN
(SELECT Id FROM CampaignRecipients WHERE CampaignId=$id ORDER BY Id LIMIT 500);
""";
            terminalize.Parameters.AddWithValue("$id", active.Id.ToString("D"));
            await terminalize.ExecuteNonQueryAsync();
        }
        Check(await service.DispatchOneAsync(default), "Recipient 501 must dispatch.");
        Check(!await service.DispatchOneAsync(default), "Pacing must hold the next recipient.");
        Check((await service.GetAsync(active.Id))!.State == CampaignState.Active,
            "A terminal first page must not complete an audience larger than 500.");
        for (int i = 1; i < 99; i++)
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(31));
            Check(await service.DispatchOneAsync(default), "Remaining audience must dispatch after pacing.");
        }
        DateTimeOffset retryAt = fixture.Clock.Now.AddMinutes(2);
        await using (SqliteConnection connection = fixture.OpenCrm())
        {
            await using SqliteCommand delayed = connection.CreateCommand();
            delayed.CommandText = """
UPDATE CampaignRecipients SET State='Rejected',Attempts=1,NextAttemptUtc=$retry
WHERE CampaignId=$id AND State='Pending';
""";
            delayed.Parameters.AddWithValue("$id", active.Id.ToString("D"));
            delayed.Parameters.AddWithValue("$retry", retryAt.ToString("O"));
            await delayed.ExecuteNonQueryAsync();
        }
        Check(!await service.DispatchOneAsync(default) && (await service.GetAsync(active.Id))!.State == CampaignState.Active,
            "A future retry beyond the first 500 rows must prevent completion.");
        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        Check(await service.DispatchOneAsync(default), "A delayed recipient beyond page 500 must remain runnable.");
        await service.DispatchOneAsync(default);
        Check((await service.GetAsync(active.Id))!.State == CampaignState.Completed && transport.SendCount == 100,
            "Completion must wait for the entire audience.");
    }

    private static async Task LegacyConsentIsNotEndpointEvidenceAsync()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        AudiencePreview preview = await fixture.Store.PreviewAudienceAsync(
            new AudienceSelection(CampaignPopulation.CrmCompanies, [1], false), CampaignChannel.WhatsApp, default);
        Check(preview.EligibleCount == 0, "Legacy company-bound opt-in is not endpoint-bound campaign consent.");
    }

    private static async Task ClaimsRecoverAfterLeaseExpiryAndPauseAsync()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.GrantCrmEmailConsentAsync(2);
        StubTransport transport = new();
        CampaignService service = fixture.Service(transport);
        CampaignSnapshot active = await fixture.CreateActiveEmailCampaignAsync(service);
        CampaignRecipient recipient = (await fixture.Store.GetPendingRecipientsAsync(active.Id, default)).Single();
        string claim = (await fixture.Store.TryClaimAsync(active.Id, recipient.ContactKey, active.Revision, fixture.Clock.Now, default))!;
        CampaignSnapshot paused = await service.PauseAsync(active.Id, active.Revision);
        Check(await fixture.Store.AuthorizeDispatchAsync(active, recipient, claim, fixture.Clock.Now, default) is null,
            "Pause must fence authorization.");
        Check((await service.GetRecipientsAsync(active.Id)).Items.Single().State == CampaignRecipientState.Pending,
            "Failed authorization must release its pre-send claim.");
        active = await service.ResumeAsync(active.Id, paused.Revision, new ActivationAcknowledgement(true, true, true));
        claim = (await fixture.Store.TryClaimAsync(active.Id, recipient.ContactKey, active.Revision, fixture.Clock.Now, default))!;
        CampaignStore restarted = new(fixture.CrmPath, fixture.ErpPath, fixture.AttachmentPath, Fixture.OwnerSid, () => fixture.Clock.Now);
        await restarted.RecoverInterruptedAsync(default);
        Check((await service.GetRecipientsAsync(active.Id)).Items.Single().State == CampaignRecipientState.Claimed,
            "Restart inside the claim lease must not prematurely reclaim it.");
        fixture.Clock.Advance(TimeSpan.FromSeconds(121));
        CampaignService resumed = new(restarted, transport);
        Check(await resumed.DispatchOneAsync(default) && transport.SendCount == 1,
            "Periodic recovery must reclaim a claim that expired after startup.");
    }

    private static async Task LiveLeadershipAndAbandonedSendsAreFencedAsync()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.GrantCrmEmailConsentAsync(2);
        CampaignService service = fixture.Service(new StubTransport());
        CampaignSnapshot active = await fixture.CreateActiveEmailCampaignAsync(service);
        CampaignRecipient recipient = (await fixture.Store.GetPendingRecipientsAsync(active.Id, default)).Single();
        string claim = (await fixture.Store.TryClaimAsync(active.Id, recipient.ContactKey, active.Revision, fixture.Clock.Now, default))!;
        CampaignSendAuthorization authorized = (await fixture.Store.AuthorizeDispatchAsync(active, recipient, claim, fixture.Clock.Now, default))!;
        CampaignStore other = new(fixture.CrmPath, fixture.ErpPath, fixture.AttachmentPath, Fixture.OwnerSid, () => fixture.Clock.Now);
        await other.RecoverInterruptedAsync(default);
        Check(!await other.TryAcquireWorkerLeaseAsync(default) &&
            (await service.GetRecipientsAsync(active.Id)).Items.Single().State == CampaignRecipientState.Sending,
            "A second live worker cannot acquire leadership or quarantine an in-flight send.");
        fixture.Clock.Advance(TimeSpan.FromSeconds(119));
        Check(await fixture.Store.RenewWorkerLeaseAsync(default), "A live worker must renew its durable lease.");
        fixture.Clock.Advance(TimeSpan.FromSeconds(119));
        await other.RecoverInterruptedAsync(default);
        Check((await service.GetRecipientsAsync(active.Id)).Items.Single().State == CampaignRecipientState.Sending,
            "Renewed leadership must remain protected across the original expiry.");
        fixture.Clock.Advance(TimeSpan.FromSeconds(121));
        await other.RecoverInterruptedAsync(default);
        Check(!await fixture.Store.RenewWorkerLeaseAsync(default), "A stale worker cannot revive a taken-over lease.");
        Check((await service.GetRecipientsAsync(active.Id)).Items.Single().State == CampaignRecipientState.Unknown,
            "Only abandoned Sending may become Unknown.");
        StubTransport otherTransport = new();
        await new CampaignService(other, otherTransport).DispatchOneAsync(default);
        Check(otherTransport.SendCount == 0, "An abandoned send must never auto-resend.");
        await fixture.Store.CompleteAttemptAsync(authorized,
            new CampaignSendResult(CampaignSendDisposition.Accepted, "LateKnownAcceptance", "late-id"),
            fixture.Clock.Now, active.Input.MinimumSpacing, default);
        Check((await service.GetRecipientsAsync(active.Id)).Items.Single().State == CampaignRecipientState.Accepted,
            "A late known outcome must still finalize its exact quarantined attempt.");
        await ThrowsAsync<InvalidOperationException>(() => fixture.Store.CompleteAttemptAsync(authorized,
            new CampaignSendResult(CampaignSendDisposition.Unknown, "DuplicateCompletion"),
            fixture.Clock.Now, active.Input.MinimumSpacing, default), "An already-finalized attempt must be fenced.");
    }

    private static async Task CampaignPacingSurvivesReservationOverwriteAsync()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.GrantCrmEmailConsentAsync(1);
        await fixture.GrantCrmEmailConsentAsync(2);
        StubTransport transport = new();
        CampaignService service = fixture.Service(transport);
        CampaignSnapshot a = await fixture.CreateCampaignAsync(service, fixture.EmailInput() with { MinimumSpacing = TimeSpan.FromMinutes(10) });
        await service.DispatchOneAsync(default);
        CampaignSnapshot paused = await service.PauseAsync(a.Id, a.Revision);
        await fixture.CreateCampaignAsync(service, fixture.EmailInput() with
        {
            Audience = new AudienceSelection(CampaignPopulation.CrmCompanies, [1], false)
        });
        fixture.Clock.Advance(TimeSpan.FromSeconds(31));
        Check(await service.DispatchOneAsync(default) && transport.SendCount == 2,
            "Campaign B may send after its endpoint spacing.");
        await service.ResumeAsync(a.Id, paused.Revision, new ActivationAcknowledgement(true, true, true));
        Check(await service.GetNextEligibleTimeAsync(a.Id) == FixedNow.AddMinutes(10),
            "The public next-eligible API must use the same independent campaign pacing journal.");
        Check(!await service.DispatchOneAsync(default) && transport.SendCount == 2,
            "B overwriting X's reservation must not erase campaign A's ten-minute pacing.");
        fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        Check(await service.DispatchOneAsync(default) && transport.SendCount == 3,
            "A may send to Y after its independent campaign pacing.");
    }

    private static async Task LateRejectionNeverResendsAnAbandonedAttemptAsync()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.GrantCrmEmailConsentAsync(2);
        CampaignService service = fixture.Service(new StubTransport());
        CampaignSnapshot active = await fixture.CreateActiveEmailCampaignAsync(service);
        CampaignRecipient recipient = (await fixture.Store.GetPendingRecipientsAsync(active.Id, default)).Single();
        string claim = (await fixture.Store.TryClaimAsync(active.Id, recipient.ContactKey, active.Revision, fixture.Clock.Now, default))!;
        CampaignSendAuthorization authorization = (await fixture.Store.AuthorizeDispatchAsync(active, recipient, claim, fixture.Clock.Now, default))!;
        fixture.Clock.Advance(TimeSpan.FromSeconds(121));
        CampaignStore replacement = new(fixture.CrmPath, fixture.ErpPath, fixture.AttachmentPath, Fixture.OwnerSid, () => fixture.Clock.Now);
        await replacement.RecoverInterruptedAsync(default);
        await fixture.Store.CompleteAttemptAsync(authorization,
            new CampaignSendResult(CampaignSendDisposition.DefinitelyRejected, "LateHttp429", SafeToRetry: true),
            fixture.Clock.Now, active.Input.MinimumSpacing, default);
        CampaignRecipient finalized = (await service.GetRecipientsAsync(active.Id)).Items.Single();
        Check(finalized.State == CampaignRecipientState.Rejected && finalized.NextAttemptAtUtc is null,
            "A late known rejection can finalize quarantine but must not revive an abandoned attempt for automatic retry.");
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        StubTransport transport = new();
        await new CampaignService(replacement, transport).DispatchOneAsync(default);
        Check(transport.SendCount == 0, "Abandoned Sending must never auto-resend, including after a late safe rejection.");
    }

    private static async Task WhatsAppApprovalSurvivesMultipleDaysAsync()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.GrantWhatsAppConsentAsync(CampaignPopulation.CrmCompanies, 1, "+905551111111");
        await fixture.GrantWhatsAppConsentAsync(CampaignPopulation.CrmCompanies, 2, "+905552222222");
        StubTransport transport = new();
        CampaignService service = fixture.Service(transport);
        WhatsAppTemplateApproval template = await service.VerifyWhatsAppTemplateAsync("approved_offer", "en_US");
        CampaignDraftInput input = fixture.WhatsAppInput(template) with { EndsAtUtc = fixture.Clock.Now.AddDays(5) };
        CampaignSnapshot active = await fixture.CreateCampaignAsync(service, input);
        Check(await service.DispatchOneAsync(default), "An approved WhatsApp campaign may begin.");
        fixture.Clock.Advance(TimeSpan.FromDays(2));
        await ThrowsAsync<InvalidOperationException>(() => service.SaveDraftAsync(null, input, 0),
            "An old verification may not approve new content.");
        CampaignSnapshot paused = await service.PauseAsync(active.Id, active.Revision);
        await service.ResumeAsync(active.Id, paused.Revision, new ActivationAcknowledgement(true, true, true));
        Check(await service.DispatchOneAsync(default) && transport.SendCount == 2 &&
            (await service.GetAsync(active.Id))!.State == CampaignState.Active,
            "Frozen approval must survive runtime aging and multi-day pause/resume.");
    }

    private static async Task DuplicateCrmOptOutSuppressesBothPopulationsAsync()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.GrantWhatsAppConsentAsync(CampaignPopulation.CrmCompanies, 1, "+905551111111");
        StubTransport transport = new();
        CampaignService service = fixture.Service(transport);
        WhatsAppTemplateApproval template = await service.VerifyWhatsAppTemplateAsync("approved_offer", "en_US");
        CampaignSnapshot active = await fixture.CreateCampaignAsync(service, fixture.WhatsAppInput(template) with
        { Audience = new AudienceSelection(CampaignPopulation.CrmCompanies, [1], false) });
        await fixture.ExecuteCrmAsync("UPDATE CrmCompanies SET WhatsAppOptOutAtUtc='2026-10-04T17:30:00.0000000+00:00' WHERE Id=3;");
        AudiencePreview crm = await fixture.Store.PreviewAudienceAsync(
            new AudienceSelection(CampaignPopulation.CrmCompanies, [1], false), CampaignChannel.WhatsApp, default);
        Check(crm.EligibleCount == 0 && crm.Exclusions.Any(x => x.Reason == CampaignExclusionReason.OptedOut),
            "An unselected duplicate CRM withdrawal must suppress preview.");
        await service.DispatchOneAsync(default);
        Check(transport.SendCount == 0 && (await service.GetRecipientsAsync(active.Id)).Items.Single().State == CampaignRecipientState.Skipped,
            "A duplicate CRM withdrawal after approval must fence authorization.");
        await fixture.ExecuteErpAsync("UPDATE Customers SET Phone='+90 (555) 111 11 11' WHERE Id=1;");
        await fixture.GrantWhatsAppConsentAsync(CampaignPopulation.ErpCustomers, 1, "+905551111111");
        AudiencePreview erp = await fixture.Store.PreviewAudienceAsync(
            new AudienceSelection(CampaignPopulation.ErpCustomers, [1], false), CampaignChannel.WhatsApp, default);
        Check(erp.EligibleCount == 0, "CRM endpoint withdrawal also suppresses an independently consented ERP endpoint.");
    }

    private static async Task ErpWhatsAppSharesManualCrmCooldownAsync()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.ExecuteErpAsync("UPDATE Customers SET Phone='+90 (555) 111 11 11' WHERE Id=1;");
        await fixture.GrantWhatsAppConsentAsync(CampaignPopulation.ErpCustomers, 1, "+905551111111");
        StubTransport transport = new();
        CampaignService service = fixture.Service(transport);
        WhatsAppTemplateApproval template = await service.VerifyWhatsAppTemplateAsync("approved_offer", "en_US");
        CampaignSnapshot active = await fixture.CreateCampaignAsync(service, fixture.WhatsAppInput(template) with
        {
            Audience = new AudienceSelection(CampaignPopulation.ErpCustomers, [1], false),
            EndsAtUtc = fixture.Clock.Now.AddDays(3)
        });
        await fixture.AddCooldownAsync(3, fixture.Clock.Now);
        AudiencePreview preview = await service.PreviewAudienceAsync(active.Input.Audience, CampaignChannel.WhatsApp);
        Check(preview.EligibleCount == 0 && preview.Exclusions.Any(x => x.Reason == CampaignExclusionReason.Cooldown),
            "ERP preview must observe manual CRM endpoint cooldown.");
        Check(!await service.DispatchOneAsync(default) && transport.SendCount == 0 &&
            (await service.GetAsync(active.Id))!.State == CampaignState.Active,
            "Manual CRM cooldown created after approval must fence ERP authorization without completing the campaign.");
        fixture.Clock.Advance(TimeSpan.FromHours(24) + TimeSpan.FromMinutes(1));
        Check(await service.DispatchOneAsync(default), "ERP sending may resume after the manual cooldown.");
        await using SqliteConnection connection = fixture.OpenCrm();
        Check(await ScalarAsync(connection, "SELECT COUNT(*) FROM CrmWhatsAppSendCooldowns WHERE CrmCompanyId IN (1,3) AND LastAttemptAtUtc='2026-10-05T17:31:00.0000000+00:00';") == 2,
            "ERP authorization must reserve every matching normalized CRM endpoint for manual sending.");
        Check(await ScalarAsync(connection, "SELECT COUNT(*) FROM CrmActivities;") == 0,
            "ERP customer ID must never be used as a CRM activity foreign key.");
    }

    private static async Task WhatsAppUnsupportedSchemasAndRequiredDocumentAreRejectedAsync()
    {
        WhatsAppBusinessService.Settings settings = new("fake", "1234567890", "v20.0", "approved_offer", "en_US", "9876543210");
        string[] unsupported =
        [
            """[{"type":"BODY","text":"Offer"},{"type":"HEADER","format":"TEXT","text":"Hello {{1}}"}]""",
            """[{"type":"BODY","text":"Offer"},{"type":"HEADER","format":"IMAGE"}]""",
            """[{"type":"BODY","text":"Offer"},{"type":"HEADER","format":"VIDEO"}]""",
            """[{"type":"BODY","text":"Offer"},{"type":"BUTTONS","buttons":[{"type":"URL","url":"https://example.test/{{1}}"}]}]""",
            """[{"type":"BODY","text":"Offer {{2}}"}]""",
            """[{"type":"BODY","text":"Offer {{name}}"}]""",
            """[{"type":"CAROUSEL"}]"""
        ];
        foreach (string components in unsupported)
        {
            RecordingHandler handler = new(_ => JsonResponse(HttpStatusCode.OK,
                """{"data":[{"name":"approved_offer","language":"en_US","status":"APPROVED","category":"MARKETING","components":""" + components + "}]}"));
            using HttpClient http = new(handler);
            await ThrowsAsync<InvalidDataException>(() => new WhatsAppBusinessService(http).VerifyMarketingTemplateAsync(settings, "approved_offer", "en_US"),
                "Unsupported required schemas must be rejected during verification.");
        }
        RecordingHandler noNetwork = new(_ => throw new InvalidOperationException("Invalid WABA must be rejected before HTTP."));
        using HttpClient noNetworkHttp = new(noNetwork);
        await ThrowsAsync<InvalidOperationException>(() => new WhatsAppBusinessService(noNetworkHttp).VerifyMarketingTemplateAsync(
            settings with { BusinessAccountId = "" }, "approved_offer", "en_US"), "Verification requires an explicit WABA, not the Phone Number ID.");
        await using Fixture fixture = await Fixture.CreateAsync();
        CampaignService service = fixture.Service(new StubTransport { VerifiedTemplate = new("approved_offer", "en_US", true, true, 0, FixedNow, true) });
        WhatsAppTemplateApproval template = await service.VerifyWhatsAppTemplateAsync("approved_offer", "en_US");
        CampaignDraft incomplete = await service.SaveDraftAsync(null, fixture.WhatsAppInput(template), 0);
        await ThrowsAsync<ArgumentException>(() => service.PrepareApprovalAsync(incomplete.Id, incomplete.Revision),
            "Drafts may await a PDF, but document-header approval must require it.");
        string document = Path.Combine(fixture.Root, "required.pdf");
        await File.WriteAllBytesAsync(document, "%PDF-1.7\nsynthetic"u8.ToArray());
        CampaignAttachment attached = await service.StageAttachmentAsync(incomplete.Id, document);
        CampaignDraft complete = await service.SaveDraftAsync(incomplete.Id,
            incomplete.Input with { AttachmentIds = [attached.Id] }, incomplete.Revision);
        Check(complete.Input.AttachmentIds.Single() == attached.Id,
            "Document campaign must support draft, stage PDF, and save without a circular validation dependency.");
        await ThrowsAsync<InvalidOperationException>(() => new WhatsAppBusinessService(noNetworkHttp).SendCampaignTemplateAsync(
            settings, "+905551111111", template, [], null), "The transport must independently require the document header.");
    }

    private static async Task MediaUploadFailuresAreSafeBoundedRetriesAsync()
    {
        byte[] pdf = "%PDF-1.7\nmock"u8.ToArray();
        CampaignAttachmentContent attachment = new(new CampaignAttachment(Guid.NewGuid(), "mock.pdf", "application/pdf", pdf.Length, "hash", "local"), pdf);
        WhatsAppTemplateApproval template = new("approved_offer", "en_US", true, true, 0, FixedNow, true);
        WhatsAppBusinessService.Settings settings = new("fake", "1234567890", "v20.0", "approved_offer", "en_US");
        foreach ((HttpStatusCode status, string body) in new[]
        {
            (HttpStatusCode.OK, "{}"), (HttpStatusCode.OK, "invalid JSON"),
            (HttpStatusCode.OK, "[]"), (HttpStatusCode.OK, """{"id":123}"""),
            (HttpStatusCode.InternalServerError, "{}"), (HttpStatusCode.RequestTimeout, "{}"),
            (HttpStatusCode.TooManyRequests, "{}")
        })
        {
            RecordingHandler handler = new(_ => JsonResponse(status, body));
            using HttpClient http = new(handler);
            CampaignSendResult result = await new WhatsAppBusinessService(http).SendCampaignTemplateAsync(settings,
                "+905551111111", template, [], attachment);
            Check(result.Disposition == CampaignSendDisposition.DefinitelyRejected && result.SafeToRetry &&
                handler.Records.Count == 1 && handler.Records[0].Uri.AbsolutePath == "/v20.0/1234567890/media",
                "Upload-only failure is safe to retry and must never invoke /messages.");
        }
        RecordingHandler cancelled = new(_ => throw new OperationCanceledException());
        using HttpClient cancelledHttp = new(cancelled);
        CampaignSendResult cancellation = await new WhatsAppBusinessService(cancelledHttp).SendCampaignTemplateAsync(settings,
            "+905551111111", template, [], attachment);
        Check(cancellation.SafeToRetry && cancellation.Disposition == CampaignSendDisposition.DefinitelyRejected,
            "Upload-only cancellation is not customer-send uncertainty.");
        RecordingHandler messageFailure = new(request => request.RequestUri!.AbsolutePath.EndsWith("/media", StringComparison.Ordinal)
            ? JsonResponse(HttpStatusCode.OK, """{"id":"mock-media-id"}""")
            : JsonResponse(HttpStatusCode.InternalServerError, "{}"));
        using HttpClient messageHttp = new(messageFailure);
        CampaignSendResult uncertainMessage = await new WhatsAppBusinessService(messageHttp).SendCampaignTemplateAsync(settings,
            "+905551111111", template, [], attachment);
        Check(uncertainMessage.Disposition == CampaignSendDisposition.Unknown && !uncertainMessage.SafeToRetry &&
            messageFailure.Records.Count == 2 && messageFailure.Records[1].Uri.AbsolutePath == "/v20.0/1234567890/messages",
            "Once /messages is invoked a 5xx remains customer-send uncertainty, unlike upload failure.");
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.GrantCrmEmailConsentAsync(2);
        StubTransport retries = new() { SendResult = new(CampaignSendDisposition.DefinitelyRejected, "MediaUploadInvalidResponse", SafeToRetry: true) };
        CampaignService service = fixture.Service(retries);
        CampaignSnapshot active = await fixture.CreateActiveEmailCampaignAsync(service);
        for (int i = 0; i < 3; i++)
        {
            Check(await service.DispatchOneAsync(default), "Safe pre-message failures may retry up to the budget.");
            fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        }
        await service.DispatchOneAsync(default);
        Check(retries.SendCount == 3 && (await service.GetAsync(active.Id))!.State == CampaignState.Completed &&
            (await service.GetRecipientsAsync(active.Id)).Items.Single().NextAttemptAtUtc is null,
            "Upload retries must stop after three attempts, not quarantine or retry indefinitely.");
    }

    private static async Task KnownOutcomeSurvivesShutdownCancellationAsync()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.GrantCrmEmailConsentAsync(2);
        using CancellationTokenSource shutdown = new();
        StubTransport transport = new() { OnSend = () => shutdown.Cancel() };
        CampaignService service = fixture.Service(transport);
        CampaignSnapshot active = await fixture.CreateActiveEmailCampaignAsync(service);
        Check(await service.DispatchOneAsync(shutdown.Token), "An authorized send drains even after shutdown.");
        Check((await service.GetRecipientsAsync(active.Id)).Items.Single().State == CampaignRecipientState.Accepted,
            "A known result must persist with a separate cleanup token.");
        await using SqliteConnection connection = fixture.OpenCrm();
        Check(await ScalarAsync(connection, "SELECT COUNT(*) FROM CampaignAttempts WHERE State='Accepted' AND CompletedUtc IS NOT NULL;") == 1,
            "Recipient and journal must both durably finalize after cancellation.");
    }

    private static async Task WabaConfigurationPreservesOtherSettingsAsync()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        WhatsAppBusinessService service = new(settingsPath: Path.Combine(fixture.Root, "synthetic-whatsapp.bin"));
        service.SaveSettings("fake-token", "1234567890", "v20.0", "approved_offer", "en_US");
        WhatsAppBusinessService.Settings before = service.LoadSettings();
        service.SetBusinessAccountId("9876543210");
        Check(service.LoadSettings() == before with { BusinessAccountId = "9876543210" },
            "Setting WABA must preserve token, phone ID, API version, template and language.");
        service.SaveSettings("", "1234567890", "v20.0", "approved_offer", "en_US");
        Check(service.LoadSettings().BusinessAccountId == "9876543210",
            "The existing WhatsApp settings editor must not erase the new WABA setting.");
        Throws<ArgumentException>(() => service.SetBusinessAccountId("not-an-id"), "WABA must be validated.");
    }

    private static async Task RuntimeGateIsSharedAndFencesDispatchAsync()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.GrantCrmEmailConsentAsync(2);
        StubTransport transport = new();
        CampaignService service = fixture.Service(transport);
        CampaignSnapshot active = await fixture.CreateActiveEmailCampaignAsync(service);
        CampaignStore otherProcessStore = new(fixture.CrmPath, fixture.ErpPath, fixture.AttachmentPath,
            Fixture.OwnerSid, () => fixture.Clock.Now);
        CampaignService otherProcess = new(otherProcessStore, transport);
        Check(await otherProcess.RefreshExecutionStateAsync(), "Explicit execution enablement must be visible across ERP and Watcher services.");

        CampaignRecipient recipient = (await fixture.Store.GetPendingRecipientsAsync(active.Id, default)).Single();
        string claim = (await fixture.Store.TryClaimAsync(active.Id, recipient.ContactKey, active.Revision, fixture.Clock.Now, default))!;
        await otherProcess.DisableExecutionAsync();
        CampaignSendAuthorization? authorized = await fixture.Store.AuthorizeDispatchAsync(
            active, recipient, claim, fixture.Clock.Now, default);
        Check(authorized is null && transport.SendCount == 0,
            "A persisted disable that wins before dispatch authorization must fence the provider call.");
        Check(!await service.RefreshExecutionStateAsync(), "Disablement must propagate to the independent scheduler process.");
        Check((await service.GetRecipientsAsync(active.Id)).Items.Single().State == CampaignRecipientState.Pending,
            "Disable-before-authorization must immediately release its token-fenced claim.");
    }

    private static async Task SchemaUpgradeIsAdditiveAndRepeatableAsync()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.Store.InitializeAsync();
        await fixture.Store.InitializeAsync();
        await using SqliteConnection connection = fixture.OpenCrm();
        long companies = await ScalarAsync(connection, "SELECT COUNT(*) FROM CrmCompanies;");
        long campaignTable = await ScalarAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='Campaigns';");
        Check(companies == 3 && campaignTable == 1, "Campaign schema upgrade must preserve CRM contacts and be repeatable.");
    }

    private static async Task ChannelAndPopulationConsentAreIndependentAsync()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        AudiencePreview crmEmail = await fixture.Store.PreviewAudienceAsync(
            new AudienceSelection(CampaignPopulation.CrmCompanies, [], true), CampaignChannel.Email, default);
        Check(crmEmail.EligibleCount == 0, "CRM WhatsApp opt-in must not imply email marketing consent.");

        await fixture.GrantWhatsAppConsentAsync(CampaignPopulation.CrmCompanies, 1, "+905551111111");
        await fixture.GrantWhatsAppConsentAsync(CampaignPopulation.CrmCompanies, 3, "+905551111111");
        AudiencePreview crmWhatsApp = await fixture.Store.PreviewAudienceAsync(
            new AudienceSelection(CampaignPopulation.CrmCompanies, [], true), CampaignChannel.WhatsApp, default);
        Check(crmWhatsApp.EligibleCount == 1 && crmWhatsApp.Recipients[0].ContactId == 1,
            "Explicit CRM WhatsApp consent applies only to its recorded endpoint.");
        Check(crmWhatsApp.Exclusions.Any(reason => reason.Reason == CampaignExclusionReason.DuplicateEndpoint),
            "Duplicate CRM endpoints must be deduplicated within a channel campaign.");
        await fixture.AddCooldownAsync(3, fixture.Clock.Now.AddHours(-2));
        AudiencePreview cooledDown = await fixture.Store.PreviewAudienceAsync(
            new AudienceSelection(CampaignPopulation.CrmCompanies, [], true), CampaignChannel.WhatsApp, default);
        Check(cooledDown.EligibleCount == 0 &&
              cooledDown.Exclusions.Any(reason => reason.Reason == CampaignExclusionReason.Cooldown),
            "CRM WhatsApp preview must share the manual-send cooldown across duplicate phone records.");

        AudiencePreview erpEmailBeforeConsent = await fixture.Store.PreviewAudienceAsync(
            new AudienceSelection(CampaignPopulation.ErpCustomers, [], true), CampaignChannel.Email, default);
        Check(erpEmailBeforeConsent.EligibleCount == 0, "ERP activity and email address must not imply marketing consent.");
        await fixture.Store.SetConsentAsync(CampaignPopulation.ErpCustomers, 1, CampaignChannel.Email,
            "erp.one@example.test", true, "Customer consent recorded in test fixture", default);
        AudiencePreview erpEmailAfterConsent = await fixture.Store.PreviewAudienceAsync(
            new AudienceSelection(CampaignPopulation.ErpCustomers, [], true), CampaignChannel.Email, default);
        Check(erpEmailAfterConsent.EligibleCount == 1 && erpEmailAfterConsent.Recipients[0].ContactId == 1,
            "ERP ID 1 consent must remain separate from CRM ID 1.");
    }

    private static async Task ConsentIsEndpointBoundAndWithdrawalWinsAsync()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.Store.SetConsentAsync(CampaignPopulation.CrmCompanies, 2, CampaignChannel.Email,
            "email.two@example.test", true, "Explicit email consent", default);
        AudiencePreview granted = await fixture.Store.PreviewAudienceAsync(
            new AudienceSelection(CampaignPopulation.CrmCompanies, [2], false), CampaignChannel.Email, default);
        Check(granted.EligibleCount == 1, "Explicit endpoint-bound email consent should grant eligibility.");

        await fixture.Store.SetConsentAsync(CampaignPopulation.CrmCompanies, 2, CampaignChannel.Email,
            "email.two@example.test", false, "Customer withdrew consent", default);
        AudiencePreview withdrawn = await fixture.Store.PreviewAudienceAsync(
            new AudienceSelection(CampaignPopulation.CrmCompanies, [2], false), CampaignChannel.Email, default);
        Check(withdrawn.EligibleCount == 0 && withdrawn.Exclusions.Any(reason => reason.Reason == CampaignExclusionReason.OptedOut),
            "A later withdrawal must suppress an endpoint.");

        await fixture.Store.SetConsentAsync(CampaignPopulation.CrmCompanies, 3, CampaignChannel.Email,
            "email.three@example.test", true, "Consent bound to the old address", default);
        await fixture.ChangeCrmEmailAsync(3, "changed@example.test");
        AudiencePreview changedEndpoint = await fixture.Store.PreviewAudienceAsync(
            new AudienceSelection(CampaignPopulation.CrmCompanies, [3], false), CampaignChannel.Email, default);
        Check(changedEndpoint.EligibleCount == 0 &&
              changedEndpoint.Exclusions.Any(reason => reason.Reason == CampaignExclusionReason.MissingConsent),
            "Changing an endpoint must require fresh channel consent.");
    }

    private static async Task ConsentWithdrawalAfterApprovalFencesDispatchAsync()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.GrantCrmEmailConsentAsync(2);
        StubTransport transport = new();
        CampaignService service = fixture.Service(transport);
        CampaignSnapshot active = await fixture.CreateActiveEmailCampaignAsync(service);
        await service.RecordConsentAsync(CampaignPopulation.CrmCompanies, 2, CampaignChannel.Email,
            "email.two@example.test", false, "Synthetic withdrawal after approval");

        Check(!await service.DispatchOneAsync(default) && transport.SendCount == 0,
            "A withdrawal committed after approval must fence the provider call.");
        RecipientPage recipients = await service.GetRecipientsAsync(active.Id);
        Check(recipients.Items.Single().State == CampaignRecipientState.Skipped,
            "A recipient whose consent was withdrawn must remain visibly skipped.");
    }

    private static async Task WhatsAppTemplateMustBeVerifiedThroughExplicitActionAsync()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        CampaignService service = fixture.Service(new StubTransport());
        WhatsAppTemplateApproval forged = new("approved_offer", "en_US", true, false, 0, FixedNow, true);
        CampaignDraftInput input = new(
            "Synthetic WhatsApp campaign",
            CampaignChannel.WhatsApp,
            new AudienceSelection(CampaignPopulation.CrmCompanies, [2], false),
            "test-whatsapp-sender",
            "",
            "",
            forged,
            [],
            [],
            "UTC",
            fixture.Clock.Now.AddMinutes(-1),
            fixture.Clock.Now.AddDays(1),
            [new CampaignDailyWindow(new TimeOnly(0, 0), new TimeOnly(23, 59))],
            TimeSpan.FromSeconds(30),
            50,
            0m,
            true);

        await ThrowsAsync<InvalidOperationException>(
            () => service.SaveDraftAsync(null, input, 0),
            "A caller-supplied verification-shaped object must not be trusted.");
        WhatsAppTemplateApproval verified = await service.VerifyWhatsAppTemplateAsync("approved_offer", "en_US");
        CampaignDraft saved = await service.SaveDraftAsync(null, input with { WhatsAppTemplate = verified }, 0);
        Check(saved.State == CampaignState.Draft && verified.IsVerifiedMarketing,
            "The explicit provider-verification action should authorize a matching draft.");
    }

    private static async Task ApprovalFreezesAudienceAndDraftRevisionAsync()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.GrantCrmEmailConsentAsync(2);
        CampaignService service = fixture.Service(new StubTransport());
        CampaignDraft draft = await service.SaveDraftAsync(null, fixture.EmailInput(), 0);
        ApprovalPreview preview = await service.PrepareApprovalAsync(draft.Id, draft.Revision);
        CampaignSnapshot approved = await service.ApproveAsync(draft.Id, draft.Revision, preview.ApprovalManifestHash,
            new ApprovalAcknowledgement(true, true, true));

        await fixture.InsertCrmContactAsync(4, "Late contact", "late@example.test", "+905551234567");
        await fixture.Store.SetConsentAsync(CampaignPopulation.CrmCompanies, 4, CampaignChannel.Email,
            "late@example.test", true, "Consent after approval", default);
        RecipientPage frozen = await service.GetRecipientsAsync(draft.Id);
        Check(frozen.TotalCount == 1 && frozen.Items[0].ContactId == 2,
            "Contacts becoming eligible after approval must not enter the frozen audience.");

        CampaignDraftInput edited = fixture.EmailInput() with { Content = "New text requires new approval." };
        CampaignDraft revised = await service.SaveDraftAsync(draft.Id, edited, approved.Revision);
        Check(revised.Revision == approved.Revision + 1 && revised.State == CampaignState.Draft,
            "Changing approved content must invalidate approval and advance the revision.");
        RecipientPage cleared = await service.GetRecipientsAsync(draft.Id);
        Check(cleared.TotalCount == 0, "An unstarted approval revision must clear its old frozen recipient set.");
    }

    private static async Task OwnersCannotReadEachOthersCampaignsAsync()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        CampaignDraft draft = await fixture.Store.SaveDraftAsync(null, fixture.EmailInput(), 0, default);
        CampaignStore otherOwner = new(fixture.CrmPath, fixture.ErpPath, fixture.AttachmentPath, "S-1-5-21-other", () => fixture.Clock.Now);
        await otherOwner.InitializeAsync();
        Check(await otherOwner.GetAsync(draft.Id, default) is null,
            "Campaign query results must be scoped to the current Windows owner SID.");
        IReadOnlyList<CampaignConsent> consents = await otherOwner.GetConsentsAsync(null, null, default);
        Check(consents.Count == 0, "Consent ledger reads must be owner-scoped.");
    }

    private static async Task ExecutionIsDisabledWithoutTouchingStorageAsync()
    {
        string missingDatabase = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.db");
        CampaignStore store = new(missingDatabase, ownerSid: "S-1-5-21-disabled", utcNow: () => FixedNow);
        StubTransport transport = new();
        CampaignService service = new(store, transport);
        Check(!service.ExecutionEnabled, "Execution must be disabled by default.");
        Check(!await service.DispatchOneAsync(default), "Disabled execution must not dispatch.");
        Check(!File.Exists(missingDatabase) && transport.SendCount == 0,
            "Disabled execution must not initialize campaign storage or contact a provider.");
    }

    private static async Task ClaimsAreExclusiveAsync()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.GrantCrmEmailConsentAsync(2);
        CampaignService service = fixture.Service(new StubTransport());
        CampaignSnapshot active = await fixture.CreateActiveEmailCampaignAsync(service);
        CampaignRecipient recipient = (await fixture.Store.GetPendingRecipientsAsync(active.Id, default)).Single();
        Task<string?> first = fixture.Store.TryClaimAsync(active.Id, recipient.ContactKey, active.Revision, fixture.Clock.Now, default);
        Task<string?> second = fixture.Store.TryClaimAsync(active.Id, recipient.ContactKey, active.Revision, fixture.Clock.Now, default);
        string?[] claims = await Task.WhenAll(first, second);
        Check(claims.Count(value => value is not null) == 1,
            "Concurrent workers must not claim the same campaign recipient.");
        await fixture.Store.ReleaseClaimAsync(active.Id, recipient.ContactKey, "wrong-token", default);
        Check((await service.GetRecipientsAsync(active.Id)).Items.Single().State == CampaignRecipientState.Claimed,
            "Claim release must not clear another claim token.");
    }

    private static async Task GraphUsesAnIndividualRequestAndDoesNotClaimDeliveryAsync()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.GrantCrmEmailConsentAsync(2);
        RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.Accepted));
        using HttpClient graph = new(handler);
        int authCalls = 0;
        CampaignTransport transport = new((account, _) =>
        {
            authCalls++;
            return Task.FromResult(new CampaignGraphToken("fake-token", account));
        }, new WhatsAppBusinessService(new HttpClient(new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)))), graph);
        CampaignService service = fixture.Service(transport);
        CampaignSnapshot active = await fixture.CreateActiveEmailCampaignAsync(service);
        await service.DispatchOneAsync(default);

        Check(handler.Records.Count == 1 && handler.Records[0].Method == HttpMethod.Post &&
              handler.Records[0].Uri.AbsolutePath == "/v1.0/me/sendMail",
            "Email campaigns must issue a single provider request per recipient.");
        using JsonDocument body = JsonDocument.Parse(handler.Records[0].Content);
        JsonElement message = body.RootElement.GetProperty("message");
        Check(message.GetProperty("toRecipients").GetArrayLength() == 1 &&
              !message.TryGetProperty("ccRecipients", out _) && !message.TryGetProperty("bccRecipients", out _),
            "Graph request must contain exactly one recipient and no CC/BCC.");
        RecipientPage sent = await service.GetRecipientsAsync(active.Id);
        Check(sent.Items.Single().State == CampaignRecipientState.Accepted &&
              sent.Items.Single().DeliveryConfirmed is null,
            "Graph acceptance must remain distinct from delivery confirmation.");
        Check(authCalls >= 2, "Activation and dispatch must use the pinned silent sender identity.");
    }

    private static async Task UnknownOutcomesAreQuarantinedAcrossRestartAsync()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.GrantCrmEmailConsentAsync(2);
        StubTransport transport = new()
        {
            SendResult = new CampaignSendResult(CampaignSendDisposition.Unknown, "NetworkOutcomeUnknown")
        };
        CampaignService service = fixture.Service(transport);
        CampaignSnapshot active = await fixture.CreateActiveEmailCampaignAsync(service);
        await service.DispatchOneAsync(default);
        await fixture.Store.RecoverInterruptedAsync(default);
        bool sentAgain = await service.DispatchOneAsync(default);
        RecipientPage recipients = await service.GetRecipientsAsync(active.Id);
        Check(!sentAgain && transport.SendCount == 1 &&
              recipients.Items.Single().State == CampaignRecipientState.Unknown,
            "Unknown provider outcomes must remain quarantined after recovery and must never auto-resend.");
    }

    private static async Task InterruptedDispatchBecomesUnknownAsync()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.GrantCrmEmailConsentAsync(2);
        CampaignService service = fixture.Service(new StubTransport());
        CampaignSnapshot active = await fixture.CreateActiveEmailCampaignAsync(service);
        CampaignRecipient recipient = (await fixture.Store.GetPendingRecipientsAsync(active.Id, default)).Single();
        string claim = (await fixture.Store.TryClaimAsync(active.Id, recipient.ContactKey, active.Revision, fixture.Clock.Now, default))!;
        CampaignSendAuthorization authorized = (await fixture.Store.AuthorizeDispatchAsync(
            active, recipient, claim, fixture.Clock.Now, default))!;
        Check(authorized.AttemptId.Length > 0, "Dispatch authorization must journal the attempt before provider invocation.");

        CampaignStore restarted = new(fixture.CrmPath, fixture.ErpPath, fixture.AttachmentPath, Fixture.OwnerSid, () => fixture.Clock.Now);
        fixture.Clock.Advance(TimeSpan.FromSeconds(121));
        await restarted.RecoverInterruptedAsync(default);
        CampaignService restartedService = new(restarted, new StubTransport());
        RecipientPage recipients = await restartedService.GetRecipientsAsync(active.Id);
        Check(recipients.Items.Single().State == CampaignRecipientState.Unknown,
            "A crash after the durable Sending boundary must recover to Unknown.");
    }

    private static async Task SafeRejectionsRetryOnlyWithProviderEvidenceAsync()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.GrantCrmEmailConsentAsync(2);
        StubTransport transport = new()
        {
            SendResults = new Queue<CampaignSendResult>([
                new CampaignSendResult(CampaignSendDisposition.DefinitelyRejected, "GraphHttp429", RetryAfter: TimeSpan.FromSeconds(45), SafeToRetry: true),
                new CampaignSendResult(CampaignSendDisposition.Accepted, "ProviderAccepted")
            ])
        };
        CampaignService service = fixture.Service(transport);
        CampaignSnapshot active = await fixture.CreateActiveEmailCampaignAsync(service);
        await service.DispatchOneAsync(default);
        RecipientPage rejected = await service.GetRecipientsAsync(active.Id);
        Check(rejected.Items.Single().State == CampaignRecipientState.Rejected &&
              rejected.Items.Single().NextAttemptAtUtc == fixture.Clock.Now.AddSeconds(45),
            "Only a positive provider rejection may schedule bounded retry.");
        Check(!await service.DispatchOneAsync(default) && transport.SendCount == 1,
            "The retry delay must prevent an immediate resend.");
        fixture.Clock.Advance(TimeSpan.FromSeconds(46));
        await service.DispatchOneAsync(default);
        RecipientPage accepted = await service.GetRecipientsAsync(active.Id);
        Check(transport.SendCount == 2 && accepted.Items.Single().State == CampaignRecipientState.Accepted,
            "An explicitly safe retry may proceed after its durable delay.");
    }

    private static async Task CancellationBeforeActivationCannotDispatchAsync()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.GrantCrmEmailConsentAsync(2);
        StubTransport transport = new();
        CampaignService service = fixture.Service(transport);
        CampaignDraft draft = await service.SaveDraftAsync(null, fixture.EmailInput(), 0);
        ApprovalPreview preview = await service.PrepareApprovalAsync(draft.Id, draft.Revision);
        CampaignSnapshot approved = await service.ApproveAsync(draft.Id, draft.Revision, preview.ApprovalManifestHash,
            new ApprovalAcknowledgement(true, true, true));
        CampaignSnapshot cancelled = await service.CancelAsync(draft.Id, approved.Revision);
        Check(cancelled.State == CampaignState.Cancelled, "An approved but unactivated campaign can be cancelled.");
        Check(!await service.DispatchOneAsync(default) && transport.SendCount == 0,
            "Cancelled campaigns must not dispatch.");
    }

    private static async Task AttachmentsAreOwnerStagedAndHashCheckedAsync()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        string source = Path.Combine(fixture.Root, "offer.pdf");
        await File.WriteAllBytesAsync(source, "%PDF-1.7\nsynthetic document"u8.ToArray());
        CampaignDraftInput input = fixture.EmailInput();
        CampaignDraft draft = await fixture.Store.SaveDraftAsync(null, input, 0, default);
        CampaignAttachment staged = await fixture.Store.StageAttachmentAsync(draft.Id, source, default);
        CampaignAttachmentContent contents = await fixture.Store.ReadAttachmentAsync(staged, CampaignChannel.Email, default);
        Check(contents.Bytes.Length == staged.Length && contents.Descriptor.Sha256 == staged.Sha256,
            "Staged attachment must be owned, locally copied and hashed.");
        await File.WriteAllBytesAsync(staged.StagedPath, "%PDF-1.7\naltered document"u8.ToArray());
        await ThrowsAsync<InvalidDataException>(
            () => fixture.Store.ReadAttachmentAsync(staged, CampaignChannel.Email, default),
            "Staged attachment hash mismatch must block approval and dispatch.");
        foreach (string extension in new[] { ".txt", ".png", ".jpg", ".jpeg", ".docx", ".xlsx", ".pptx" })
        {
            byte[] bytes;
            if (extension == ".txt") bytes = "Synthetic introduction"u8.ToArray();
            else if (extension == ".png") bytes = [137, 80, 78, 71, 13, 10, 26, 10, 0];
            else if (extension is ".jpg" or ".jpeg") bytes = [255, 216, 255, 0];
            else
            {
                using var memory = new MemoryStream();
                using (var zip = new System.IO.Compression.ZipArchive(memory, System.IO.Compression.ZipArchiveMode.Create, true))
                {
                    zip.CreateEntry("[Content_Types].xml");
                    zip.CreateEntry(extension == ".docx" ? "word/document.xml" : extension == ".xlsx" ? "xl/workbook.xml" : "ppt/presentation.xml");
                }
                bytes = memory.ToArray();
            }
            string path = Path.Combine(fixture.Root, "offer" + extension);
            await File.WriteAllBytesAsync(path, bytes);
            var file = await fixture.Store.StageAttachmentAsync(draft.Id, path, default);
            var read = await fixture.Store.ReadAttachmentAsync(file, CampaignChannel.Email, default);
            Check(file.ContentType == CampaignAttachmentPolicy.ContentType(path, CampaignChannel.Email) && read.Bytes.SequenceEqual(bytes),
                "Supported email documents and images must preserve bytes and correct MIME.");
            await ThrowsAsync<InvalidDataException>(() => fixture.Store.ReadAttachmentAsync(file, CampaignChannel.WhatsApp, default),
                "WhatsApp may not reuse non-PDF email attachments.");
            RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.Accepted));
            using var http = new HttpClient(handler);
            using var mail = new CampaignTransport((_, _) => Task.FromResult(new CampaignGraphToken("synthetic-token", input.SenderIdentity)),
                new WhatsAppBusinessService(), http);
            var result = await mail.SendAsync(new(Guid.NewGuid(), "customer@example.test", input, [read], "synthetic"), default);
            using JsonDocument payload = JsonDocument.Parse(handler.Records.Single().Content);
            Check(result.Disposition == CampaignSendDisposition.Accepted &&
                payload.RootElement.GetProperty("message").GetProperty("attachments")[0].GetProperty("contentType").GetString() == file.ContentType,
                "Graph must serialize each supported attachment with its actual MIME type.");
        }
        string executable = Path.Combine(fixture.Root, "offer.exe");
        await File.WriteAllBytesAsync(executable, "MZsynthetic"u8.ToArray());
        await ThrowsAsync<InvalidDataException>(() => fixture.Store.StageAttachmentAsync(draft.Id, executable, default),
            "Executable attachments are not supported.");
        string disguised = Path.Combine(fixture.Root, "offer.png");
        await File.WriteAllBytesAsync(disguised, "MZsynthetic"u8.ToArray());
        await ThrowsAsync<InvalidDataException>(() => fixture.Store.StageAttachmentAsync(draft.Id, disguised, default),
            "A filename cannot disguise an unsupported binary as an image.");
    }

    private static async Task WhatsAppVerificationAndDocumentPayloadUseMockHttpAsync()
    {
        RecordingHandler handler = new(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/v20.0/9876543210/message_templates")
                return JsonResponse(HttpStatusCode.OK,
                    """{"data":[{"name":"approved_offer","language":"en_US","status":"APPROVED","category":"MARKETING","components":[{"type":"BODY","text":"Offer {{1}}"},{"type":"HEADER","format":"DOCUMENT"}]}]}""");
            if (request.RequestUri.AbsolutePath.EndsWith("/media", StringComparison.Ordinal))
                return JsonResponse(HttpStatusCode.OK, """{"id":"media-test-id"}""");
            return JsonResponse(HttpStatusCode.OK, """{"messages":[{"id":"wamid.test"}]}""");
        });
        using HttpClient client = new(handler);
        WhatsAppBusinessService whatsapp = new(client);
        WhatsAppBusinessService.Settings settings = new("test-token", "1234567890", "v20.0", "approved_offer", "en_US", "9876543210");
        WhatsAppTemplateApproval template = await whatsapp.VerifyMarketingTemplateAsync(settings, "approved_offer", "en_US");
        byte[] pdf = "%PDF-1.7\nfake"u8.ToArray();
        CampaignAttachment descriptor = new(Guid.NewGuid(), "offer.pdf", "application/pdf", pdf.Length, "hash", "local");
        CampaignSendResult result = await whatsapp.SendCampaignTemplateAsync(
            settings, "+905551234567", template,
            [new WhatsAppTemplateComponent("body", ["15% discount"])],
            new CampaignAttachmentContent(descriptor, pdf));

        Check(template.IsVerifiedMarketing && template.HasDocumentHeader && template.BodyParameterCount == 1,
            "Template verification must require approved marketing category and capture supported components.");
        Check(handler.Records[0].Uri.AbsoluteUri ==
            "https://graph.facebook.com/v20.0/9876543210/message_templates?name=approved_offer&fields=name,language,status,category,components",
            "Template verification must target the exact WABA URL, not the sending Phone Number ID.");
        Check(result.Disposition == CampaignSendDisposition.Accepted && handler.Records.Count == 3,
            "WhatsApp mock should verify, upload one local PDF and send one individual template message.");
        using JsonDocument body = JsonDocument.Parse(handler.Records[2].Content);
        JsonElement root = body.RootElement;
        Check(root.GetProperty("to").GetString() == "905551234567" &&
              root.GetProperty("template").GetProperty("components").GetArrayLength() == 2 &&
              root.GetProperty("template").GetProperty("components")[1].GetProperty("parameters")[0]
                  .GetProperty("document").GetProperty("id").GetString() == "media-test-id",
            "WhatsApp payload must bind authored text to the approved body and PDF to its document header.");
    }

    private static void WindowsRespectHalfOpenOvernightAndDstBoundaries()
    {
        TimeZoneInfo eastern = CampaignWindowSchedule.ResolveTimeZone("Eastern Standard Time");
        CampaignDailyWindow overnight = new(new TimeOnly(22, 0), new TimeOnly(3, 0));
        Check(CampaignWindowSchedule.IsWithinWindow(eastern, [overnight],
                new DateTimeOffset(2026, 10, 5, 6, 30, 0, TimeSpan.Zero)),
            "Overnight windows include their post-midnight portion.");
        Check(!CampaignWindowSchedule.IsWithinWindow(eastern, [overnight],
                new DateTimeOffset(2026, 10, 5, 7, 0, 0, TimeSpan.Zero)),
            "Daily windows are half-open at the local end time.");
        DateTimeOffset? next = CampaignWindowSchedule.GetNextWindowStartUtc(eastern, [overnight],
            new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.Zero));
        Check(next == new DateTimeOffset(2026, 10, 6, 2, 0, 0, TimeSpan.Zero),
            "The next overnight window must be calculated in the selected timezone.");

        Throws<ArgumentException>(() => CampaignWindowSchedule.ResolveStartInstant(
            eastern, new DateTime(2026, 3, 8, 2, 30, 0)),
            "A start instant in a DST gap must be rejected.");
        Throws<ArgumentException>(() => CampaignWindowSchedule.ResolveStartInstant(
            eastern, new DateTime(2026, 11, 1, 1, 30, 0)),
            "A folded local start must require an explicit UTC-offset choice.");
        TimeSpan[] offsets = eastern.GetAmbiguousTimeOffsets(new DateTime(2026, 11, 1, 1, 30, 0));
        Check(offsets.All(offset => CampaignWindowSchedule.ResolveStartInstant(
                eastern, new DateTime(2026, 11, 1, 1, 30, 0), offset).Offset == TimeSpan.Zero),
            "An explicitly selected DST fold interpretation must resolve to UTC.");
    }

    private static async Task SchedulerDrainsWhenCancelledAsync()
    {
        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.db");
        CampaignStore store = new(missing, ownerSid: "S-1-5-21-shutdown", utcNow: () => FixedNow);
        CampaignService service = new(store, new StubTransport());
        CampaignScheduler scheduler = new(service, _ => throw new InvalidOperationException("Unexpected scheduler error."),
            TimeSpan.FromSeconds(1));
        using CancellationTokenSource cancellation = new();
        Task pump = scheduler.RunAsync(cancellation.Token);
        cancellation.Cancel();
        await pump;
        Check(!File.Exists(missing), "A disabled scheduler must drain without opening or creating its campaign database.");
    }

    private static async Task SchedulerSurvivesFailureReporterExceptionAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "AgentWatcher.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string databasePath = Path.Combine(root, "campaigns.db");
        await File.WriteAllBytesAsync(databasePath, [0x00, 0x01, 0x02, 0x03]);

        CampaignStore store = new(databasePath, ownerSid: "S-1-5-21-reporting-failure", utcNow: () => FixedNow);
        CampaignService service = new(store, new StubTransport());
        using CancellationTokenSource cancellation = new();
        int reportCount = 0;
        using StringWriter traceOutput = new();
        using TextWriterTraceListener traceListener = new(traceOutput);
        Trace.Listeners.Add(traceListener);
        CampaignScheduler scheduler = new(service, _ =>
        {
            if (Interlocked.Increment(ref reportCount) >= 2)
                cancellation.Cancel();
            throw new InvalidOperationException("The test reporting sink is unavailable.");
        }, TimeSpan.FromSeconds(1));

        Task pump = scheduler.RunAsync(cancellation.Token);
        Exception? pumpError = null;
        try
        {
            await pump.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception exception)
        {
            pumpError = exception;
        }
        finally
        {
            cancellation.Cancel();
            Trace.Listeners.Remove(traceListener);
            traceListener.Flush();
            try
            {
                await pump;
            }
            catch
            {
                // Preserve the original result for the assertion below.
            }

            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }

        Check(pumpError is null && Volatile.Read(ref reportCount) >= 2 &&
              traceOutput.ToString().Contains("reporting sink is unavailable", StringComparison.Ordinal),
            "A failure-reporting exception must be surfaced without terminating the scheduler before it can retry.");
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string content) =>
        new(status) { Content = new StringContent(content, System.Text.Encoding.UTF8, "application/json") };

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void Throws<TException>(Action action, string message) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new InvalidOperationException(message);
    }

    private static async Task ThrowsAsync<TException>(Func<Task> action, string message) where TException : Exception
    {
        try { await action(); }
        catch (TException) { return; }
        throw new InvalidOperationException(message);
    }

    private sealed class TestClock(DateTimeOffset now)
    {
        public DateTimeOffset Now { get; private set; } = now;
        public void Advance(TimeSpan duration) => Now += duration;
    }

    private sealed class StubTransport : ICampaignTransport
    {
        public CampaignSendResult SendResult { get; set; } = new(CampaignSendDisposition.Accepted, "ProviderAccepted", "fake-provider-id");
        public Queue<CampaignSendResult>? SendResults { get; set; }
        public int SendCount { get; private set; }
        public List<CampaignSendRequest> Requests { get; } = [];
        public Action? OnSend { get; set; }
        public WhatsAppTemplateApproval VerifiedTemplate { get; set; } = new("approved_offer", "en_US", true, false, 0, FixedNow, true);

        public Task<IReadOnlyList<CampaignSenderOption>> GetAvailableSendersAsync(CampaignChannel channel, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CampaignSenderOption>>([]);

        public Task<ProviderReadiness> CheckReadinessAsync(CampaignChannel channel, string senderIdentity, CancellationToken cancellationToken) =>
            Task.FromResult(new ProviderReadiness(channel, true, null, senderIdentity));

        public Task<WhatsAppTemplateApproval> VerifyWhatsAppTemplateAsync(string name, string languageCode, CancellationToken cancellationToken) =>
            Task.FromResult(VerifiedTemplate with { Name = name, LanguageCode = languageCode });

        public Task<CampaignSendResult> SendAsync(CampaignSendRequest request, CancellationToken cancellationToken)
        {
            SendCount++;
            Requests.Add(request);
            OnSend?.Invoke();
            return Task.FromResult(SendResults is { Count: > 0 } ? SendResults.Dequeue() : SendResult);
        }

        public Task<CampaignGraphToken> RequestMailSendConsentAsync(string accountId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Tests must not launch interactive authentication.");
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<RequestRecord> Records { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string content = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Records.Add(new RequestRecord(request.Method, request.RequestUri!, content));
            return responseFactory(request);
        }
    }

    private sealed record RequestRecord(HttpMethod Method, Uri Uri, string Content);

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(string root, TestClock clock)
        {
            Root = root;
            Clock = clock;
            CrmPath = Path.Combine(root, "crm.db");
            ErpPath = Path.Combine(root, "erp.db");
            AttachmentPath = Path.Combine(root, "attachments");
            Store = new CampaignStore(CrmPath, ErpPath, AttachmentPath, OwnerSid, () => Clock.Now);
        }

        public const string OwnerSid = "S-1-5-21-test-owner";
        public string Root { get; }
        public string CrmPath { get; }
        public string ErpPath { get; }
        public string AttachmentPath { get; }
        public TestClock Clock { get; }
        public CampaignStore Store { get; }

        public static async Task<Fixture> CreateAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), "AgentWatcherCampaignTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Fixture fixture = new(root, new TestClock(FixedNow));
            await fixture.InitializeDatabasesAsync();
            await fixture.Store.InitializeAsync();
            return fixture;
        }

        public SqliteConnection OpenCrm()
        {
            SqliteConnection connection = new(new SqliteConnectionStringBuilder
            {
                DataSource = CrmPath,
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Shared
            }.ToString());
            connection.Open();
            return connection;
        }

        public CampaignService Service(ICampaignTransport transport) => new(Store, transport);

        public CampaignDraftInput WhatsAppInput(WhatsAppTemplateApproval template) => EmailInput() with
        {
            Channel = CampaignChannel.WhatsApp,
            SenderIdentity = "test-whatsapp-sender",
            Subject = "",
            Content = "",
            WhatsAppTemplate = template
        };

        public Task GrantWhatsAppConsentAsync(CampaignPopulation population, int id, string phone) =>
            Store.SetConsentAsync(population, id, CampaignChannel.WhatsApp, phone, true, "Synthetic endpoint-bound opt-in", default);

        public async Task ExecuteCrmAsync(string sql)
        {
            await using SqliteConnection connection = OpenCrm();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        public async Task ExecuteErpAsync(string sql)
        {
            await using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = ErpPath }.ToString());
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        public CampaignDraftInput EmailInput() => new(
            "Synthetic campaign",
            CampaignChannel.Email,
            new AudienceSelection(CampaignPopulation.CrmCompanies, [], true),
            "test-account-id",
            "Synthetic subject",
            "Synthetic content",
            null,
            [],
            [],
            "UTC",
            Clock.Now.AddMinutes(-1),
            Clock.Now.AddDays(1),
            [new CampaignDailyWindow(new TimeOnly(0, 0), new TimeOnly(23, 59))],
            TimeSpan.FromSeconds(30),
            50,
            0m,
            true);

        public async Task<CampaignSnapshot> CreateActiveEmailCampaignAsync(CampaignService service)
            => await CreateCampaignAsync(service, EmailInput());

        public async Task<CampaignSnapshot> CreateCampaignAsync(CampaignService service, CampaignDraftInput input)
        {
            await service.EnableExecutionAsync(new ExecutionEnablementAcknowledgement(true, true, true));
            CampaignDraft draft = await service.SaveDraftAsync(null, input, 0);
            ApprovalPreview preview = await service.PrepareApprovalAsync(draft.Id, draft.Revision);
            CampaignSnapshot approved = await service.ApproveAsync(draft.Id, draft.Revision, preview.ApprovalManifestHash,
                new ApprovalAcknowledgement(true, true, true));
            return await service.ActivateAsync(draft.Id, approved.Revision,
                new ActivationAcknowledgement(true, true, true));
        }

        public async Task GrantCrmEmailConsentAsync(int id)
        {
            string email = id switch
            {
                1 => "email.one@example.test",
                2 => "email.two@example.test",
                3 => "email.three@example.test",
                _ => throw new ArgumentOutOfRangeException(nameof(id))
            };
            await Store.SetConsentAsync(CampaignPopulation.CrmCompanies, id, CampaignChannel.Email,
                email, true, "Explicit synthetic email marketing consent", default);
        }

        public async Task ChangeCrmEmailAsync(int id, string email)
        {
            await using SqliteConnection connection = OpenCrm();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE CrmCompanies SET Email=$email WHERE Id=$id;";
            command.Parameters.AddWithValue("$email", email);
            command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync();
        }

        public async Task AddCooldownAsync(int contactId, DateTimeOffset attemptedAt)
        {
            await using SqliteConnection connection = OpenCrm();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "INSERT INTO CrmWhatsAppSendCooldowns(CrmCompanyId, LastAttemptAtUtc) VALUES($id,$time);";
            command.Parameters.AddWithValue("$id", contactId);
            command.Parameters.AddWithValue("$time", attemptedAt.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        public async Task InsertCrmContactAsync(int id, string name, string email, string phone)
        {
            await using SqliteConnection connection = OpenCrm();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
INSERT INTO CrmCompanies(Id, CompanyName, Phone, Email, WhatsAppMarketingOptIn)
VALUES($id,$name,$phone,$email,0);
""";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$phone", phone);
            command.Parameters.AddWithValue("$email", email);
            await command.ExecuteNonQueryAsync();
        }

        private async Task InitializeDatabasesAsync()
        {
            await using (SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = CrmPath }.ToString()))
            {
                await connection.OpenAsync();
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandText = """
CREATE TABLE CrmCompanies(
 Id INTEGER PRIMARY KEY,
 CompanyName TEXT NOT NULL,
 Phone TEXT NULL,
 Email TEXT NULL,
 WhatsAppMarketingOptIn INTEGER NOT NULL DEFAULT 0,
 WhatsAppOptInAtUtc TEXT NULL,
 WhatsAppOptInEvidence TEXT NULL,
 WhatsAppOptOutAtUtc TEXT NULL
);
CREATE TABLE CrmActivities(
 Id INTEGER PRIMARY KEY AUTOINCREMENT,
 CrmCompanyId INTEGER NOT NULL,
 ActivityType TEXT NOT NULL,
 Description TEXT NOT NULL,
 ActivityAtUtc TEXT NOT NULL,
 UserName TEXT NOT NULL
);
CREATE TABLE CrmWhatsAppSendCooldowns(
 CrmCompanyId INTEGER PRIMARY KEY,
 LastAttemptAtUtc TEXT NOT NULL
);
INSERT INTO CrmCompanies VALUES
(1,'CRM One','+905551111111','email.one@example.test',1,'2026-10-02T17:30:00.0000000+00:00','Web signup',NULL),
(2,'CRM Two','+905552222222','email.two@example.test',0,NULL,NULL,NULL),
(3,'CRM Three','+905551111111','email.three@example.test',1,'2026-10-02T17:30:00.0000000+00:00','Web signup',NULL);
""";
                await command.ExecuteNonQueryAsync();
            }
            await using (SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = ErpPath }.ToString()))
            {
                await connection.OpenAsync();
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandText = """
CREATE TABLE Customers(Id INTEGER PRIMARY KEY, CompanyName TEXT NOT NULL, Phone TEXT NULL, Email TEXT NULL, IsActive INTEGER NOT NULL);
INSERT INTO Customers VALUES(1,'ERP One','+905559999999','erp.one@example.test',1);
INSERT INTO Customers VALUES(2,'ERP Inactive','+905558888888','erp.inactive@example.test',0);
""";
                await command.ExecuteNonQueryAsync();
            }
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
