using System.Globalization;
using System.IO;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ArsanGazERP.Models;
using Microsoft.Data.Sqlite;

namespace ArsanGazERP.Services;

public sealed class CampaignStore
{
    public const int MaximumAudienceSize = 1000;
    public const int MaximumEmailAttachmentBytes = 2 * 1024 * 1024;
    public const int MaximumWhatsAppAttachmentBytes = 5 * 1024 * 1024;
    private const int ClaimLeaseSeconds = 120;
    private const int WorkerLeaseSeconds = 120;
    private readonly string workerId = Guid.NewGuid().ToString("N");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string crmDatabasePath;
    private readonly string? erpDatabasePath;
    private readonly string attachmentRoot;
    private readonly string ownerSid;
    private readonly Func<DateTimeOffset> utcNow;
    private readonly SemaphoreSlim initializationGate = new(1, 1);

    public CampaignStore(
        string crmDatabasePath,
        string? erpDatabasePath = null,
        string? attachmentRoot = null,
        string? ownerSid = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        this.crmDatabasePath = Path.GetFullPath(crmDatabasePath);
        this.erpDatabasePath = erpDatabasePath is null ? null : Path.GetFullPath(erpDatabasePath);
        this.attachmentRoot = Path.GetFullPath(attachmentRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ArsanGazERP", "CampaignAttachments"));
        this.ownerSid = ownerSid ?? WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("Windows owner SID is unavailable; campaign data cannot be isolated.");
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        OwnerSid = this.ownerSid;
    }

    public async Task<bool> GetExecutionEnabledAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(crmDatabasePath))
            return false;
        await using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = crmDatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            DefaultTimeout = 5
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='CampaignRuntimeSettings');";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 1)
            return false;
        command.CommandText = "SELECT COALESCE((SELECT ExecutionEnabled FROM CampaignRuntimeSettings WHERE OwnerSid=$owner),0);";
        command.Parameters.AddWithValue("$owner", ownerSid);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
    }

    public async Task SetExecutionEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        DateTimeOffset now = UtcNow;
        await using SqliteConnection connection = await OpenCrmAsync(cancellationToken);
        await ExecuteAsync(connection, null, """
INSERT INTO CampaignRuntimeSettings(OwnerSid, ExecutionEnabled, UpdatedUtc)
VALUES($owner,$enabled,$now)
ON CONFLICT(OwnerSid) DO UPDATE SET ExecutionEnabled=excluded.ExecutionEnabled, UpdatedUtc=excluded.UpdatedUtc;
""", cancellationToken, ("$owner", ownerSid), ("$enabled", enabled ? 1 : 0), ("$now", Stamp(now)));
    }

    public static CampaignStore CreateForCurrentWindowsUser()
    {
        string crmDatabasePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments),
            "ArsanGazERP", "CRM", "arsangaz-crm.db");
        string erpDatabasePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ArsanGazERP", "Data", "arsangaz-erp-v3.db");
        return new CampaignStore(crmDatabasePath, erpDatabasePath);
    }

    public string OwnerSid { get; }
    public DateTimeOffset UtcNow => utcNow().ToUniversalTime();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await initializationGate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(crmDatabasePath))
                throw new FileNotFoundException("The shared CRM database must exist before campaign storage can be initialized.", crmDatabasePath);

            await using SqliteConnection connection = await OpenCrmAsync(cancellationToken);
            await using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = """
PRAGMA journal_mode=WAL;
CREATE TABLE IF NOT EXISTS Campaigns(
 Id TEXT NOT NULL PRIMARY KEY,
 OwnerSid TEXT NOT NULL,
 Revision INTEGER NOT NULL,
 State TEXT NOT NULL,
 DraftJson TEXT NOT NULL,
 SnapshotJson TEXT NULL,
 ManifestHash TEXT NULL,
 LastErrorCode TEXT NULL,
 CreatedUtc TEXT NOT NULL,
 UpdatedUtc TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_Campaigns_Owner_State ON Campaigns(OwnerSid, State, UpdatedUtc);
CREATE TABLE IF NOT EXISTS CampaignWorkerLeases(
 OwnerSid TEXT NOT NULL PRIMARY KEY,
 WorkerId TEXT NOT NULL,
 ExpiresUtc TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS CampaignConsents(
 Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
 OwnerSid TEXT NOT NULL,
 Population TEXT NOT NULL,
 ContactId INTEGER NOT NULL,
 Channel TEXT NOT NULL,
 Endpoint TEXT NOT NULL,
 NormalizedEndpoint TEXT NOT NULL,
 Granted INTEGER NOT NULL,
 Evidence TEXT NOT NULL,
 RecordedUtc TEXT NOT NULL,
 IsWithdrawal INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_CampaignConsents_Lookup
 ON CampaignConsents(OwnerSid, Population, ContactId, Channel, NormalizedEndpoint, Id);
CREATE TABLE IF NOT EXISTS CampaignVerifiedWhatsAppTemplates(
 OwnerSid TEXT NOT NULL,
 Name TEXT NOT NULL,
 LanguageCode TEXT NOT NULL,
 TemplateJson TEXT NOT NULL,
 VerifiedUtc TEXT NOT NULL,
 PRIMARY KEY(OwnerSid, Name, LanguageCode)
);
CREATE TABLE IF NOT EXISTS CampaignRecipients(
 Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
 CampaignId TEXT NOT NULL,
 ContactKey TEXT NOT NULL,
 RecipientJson TEXT NOT NULL,
 State TEXT NOT NULL,
 Attempts INTEGER NOT NULL DEFAULT 0,
 NextAttemptUtc TEXT NULL,
 ClaimToken TEXT NULL,
 ClaimExpiresUtc TEXT NULL,
 LastOutcomeCode TEXT NULL,
 ProviderMessageId TEXT NULL,
 AcceptedUtc TEXT NULL,
 DeliveryConfirmed INTEGER NULL,
 UNIQUE(CampaignId, ContactKey),
 FOREIGN KEY(CampaignId) REFERENCES Campaigns(Id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS IX_CampaignRecipients_Queue ON CampaignRecipients(CampaignId, State, NextAttemptUtc);
CREATE TABLE IF NOT EXISTS CampaignAttempts(
 Id TEXT NOT NULL PRIMARY KEY,
 CampaignId TEXT NOT NULL,
 RecipientId INTEGER NOT NULL,
 RequestHash TEXT NOT NULL,
 State TEXT NOT NULL,
 AuthorizedUtc TEXT NOT NULL,
 CompletedUtc TEXT NULL,
 OutcomeCode TEXT NULL,
 ProviderMessageId TEXT NULL,
 FOREIGN KEY(CampaignId) REFERENCES Campaigns(Id) ON DELETE CASCADE,
 FOREIGN KEY(RecipientId) REFERENCES CampaignRecipients(Id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS CampaignAttachments(
 Id TEXT NOT NULL PRIMARY KEY,
 OwnerSid TEXT NOT NULL,
 FileName TEXT NOT NULL,
 ContentType TEXT NOT NULL,
 Length INTEGER NOT NULL,
 Sha256 TEXT NOT NULL,
 StagedPath TEXT NOT NULL,
 CreatedUtc TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS CampaignEndpointReservations(
 OwnerSid TEXT NOT NULL,
 Channel TEXT NOT NULL,
 NormalizedEndpoint TEXT NOT NULL,
 LastAttemptUtc TEXT NOT NULL,
 CampaignId TEXT NOT NULL,
 PRIMARY KEY(OwnerSid, Channel, NormalizedEndpoint)
);
CREATE TABLE IF NOT EXISTS CampaignRuntimeSettings(
 OwnerSid TEXT NOT NULL PRIMARY KEY,
 ExecutionEnabled INTEGER NOT NULL DEFAULT 0,
 UpdatedUtc TEXT NOT NULL
);
""";
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally
        {
            initializationGate.Release();
        }
    }

    public async Task<CampaignDraft> SaveDraftAsync(Guid? campaignId, CampaignDraftInput input, long expectedRevision, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        DateTimeOffset now = UtcNow;
        Guid id = campaignId ?? Guid.NewGuid();
        await using SqliteConnection connection = await OpenCrmAsync(cancellationToken);
        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        CampaignDraft? existing = await ReadDraftAsync(connection, transaction, id, cancellationToken);
        if (existing is null)
        {
            if (campaignId.HasValue || expectedRevision != 0)
                throw new InvalidOperationException("Campaign revision conflict: the draft does not exist.");
            CampaignDraft created = new(id, 1, CampaignState.Draft, input, now);
            await ExecuteAsync(connection, transaction, """
INSERT INTO Campaigns(Id, OwnerSid, Revision, State, DraftJson, CreatedUtc, UpdatedUtc)
VALUES($id,$owner,1,'Draft',$draft,$now,$now);
""", cancellationToken, ("$id", id.ToString("D")), ("$owner", ownerSid), ("$draft", Serialize(input)), ("$now", Stamp(now)));
            await transaction.CommitAsync(cancellationToken);
            return created;
        }

        if (existing.Revision != expectedRevision)
            throw new InvalidOperationException($"Campaign revision conflict: expected {expectedRevision}, current revision is {existing.Revision}.");
        if (existing.State is not (CampaignState.Draft or CampaignState.Approved or CampaignState.Paused))
            throw new InvalidOperationException("Only a draft, an unstarted approval, or a paused campaign can be revised.");
        await using (SqliteCommand attempts = connection.CreateCommand())
        {
            attempts.Transaction = transaction;
            attempts.CommandText = "SELECT COALESCE(SUM(Attempts),0), SUM(CASE WHEN State='Sending' THEN 1 ELSE 0 END) FROM CampaignRecipients WHERE CampaignId=$id;";
            attempts.Parameters.AddWithValue("$id", id.ToString("D"));
            await using SqliteDataReader reader = await attempts.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            if (reader.GetInt64(0) > 0 || !reader.IsDBNull(1) && reader.GetInt64(1) > 0)
                throw new InvalidOperationException("A campaign with dispatch attempts cannot be edited; create a new campaign to preserve its immutable send history.");
        }
        CampaignDraft updated = new(id, existing.Revision + 1, CampaignState.Draft, input, now);
        int changed = await ExecuteAsync(connection, transaction, """
UPDATE Campaigns SET Revision=$revision, State='Draft', DraftJson=$draft,
 SnapshotJson=NULL, ManifestHash=NULL, LastErrorCode=NULL, UpdatedUtc=$now
WHERE Id=$id AND OwnerSid=$owner AND Revision=$expected;
""", cancellationToken, ("$revision", updated.Revision), ("$draft", Serialize(input)), ("$now", Stamp(now)),
            ("$id", id.ToString("D")), ("$owner", ownerSid), ("$expected", expectedRevision));
        if (changed != 1)
            throw new InvalidOperationException("Campaign revision conflict while saving the draft.");
        await ExecuteAsync(connection, transaction, "DELETE FROM CampaignRecipients WHERE CampaignId=$id;", cancellationToken, ("$id", id.ToString("D")));
        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    public async Task<CampaignDraft> GetDraftAsync(Guid id, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using SqliteConnection connection = await OpenCrmAsync(cancellationToken);
        CampaignDraft? draft = await ReadDraftAsync(connection, null, id, cancellationToken);
        return draft ?? throw new KeyNotFoundException("Campaign draft was not found for this Windows owner.");
    }

    public async Task<IReadOnlyList<CampaignAttachment>> GetAttachmentsAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        if (ids.Count == 0)
            return [];
        await using SqliteConnection connection = await OpenCrmAsync(cancellationToken);
        List<CampaignAttachment> results = [];
        foreach (Guid id in ids.Distinct())
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
SELECT FileName, ContentType, Length, Sha256, StagedPath
FROM CampaignAttachments WHERE Id=$id AND OwnerSid=$owner;
""";
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            command.Parameters.AddWithValue("$owner", ownerSid);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("A selected attachment is missing or belongs to another Windows owner.");
            results.Add(new CampaignAttachment(id, reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3), reader.GetString(4)));
        }
        return results;
    }

    public async Task<CampaignAttachment> StageAttachmentAsync(Guid campaignId, string sourcePath, CancellationToken cancellationToken)
    {
        CampaignDraft draft = await GetDraftAsync(campaignId, cancellationToken);
        if (draft.State != CampaignState.Draft)
            throw new InvalidOperationException("Attachments can only be added to a draft.");
        string extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        string contentType = CampaignAttachmentPolicy.ContentType(sourcePath, draft.Input.Channel);
        int maximum = draft.Input.Channel == CampaignChannel.Email ? MaximumEmailAttachmentBytes : MaximumWhatsAppAttachmentBytes;
        FileInfo source = new(sourcePath);
        if (!source.Exists || source.Length is <= 0 || source.Length > maximum)
            throw new InvalidDataException($"Attachment must be between 1 byte and {maximum} bytes.");
        if (draft.Input.Channel == CampaignChannel.WhatsApp &&
            draft.Input.WhatsAppTemplate?.HasDocumentHeader != true)
            throw new InvalidDataException("WhatsApp PDF attachments require a verified template with a document header.");

        string ownerDirectory = Path.Combine(attachmentRoot, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ownerSid))));
        Directory.CreateDirectory(ownerDirectory);
        Guid id = Guid.NewGuid();
        string stagedPath = Path.Combine(ownerDirectory, id.ToString("N") + extension);
        string temporaryPath = stagedPath + ".tmp";
        string hash;
        long stagedLength;
        try
        {
            await using (FileStream input = new(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (FileStream output = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            using (IncrementalHash incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                byte[] buffer = new byte[81920];
                int read;
                long copied = 0;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    copied += read;
                    if (copied > maximum)
                        throw new InvalidDataException($"Attachment exceeds the {maximum}-byte limit.");
                    incrementalHash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                await output.FlushAsync(cancellationToken);
                stagedLength = copied;
                hash = Convert.ToHexString(incrementalHash.GetHashAndReset());
            }
            CampaignAttachmentPolicy.ValidateContent(sourcePath, draft.Input.Channel, await File.ReadAllBytesAsync(temporaryPath, cancellationToken));
            File.Move(temporaryPath, stagedPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }

        CampaignAttachment attachment = new(id, Path.GetFileName(sourcePath), contentType, stagedLength, hash, stagedPath);
        await InitializeAsync(cancellationToken);
        try
        {
            await using SqliteConnection connection = await OpenCrmAsync(cancellationToken);
            await ExecuteAsync(connection, null, """
INSERT INTO CampaignAttachments(Id, OwnerSid, FileName, ContentType, Length, Sha256, StagedPath, CreatedUtc)
VALUES($id,$owner,$name,$type,$length,$hash,$path,$now);
""", cancellationToken, ("$id", id.ToString("D")), ("$owner", ownerSid), ("$name", attachment.FileName),
                ("$type", attachment.ContentType), ("$length", attachment.Length), ("$hash", attachment.Sha256),
                ("$path", attachment.StagedPath), ("$now", Stamp(UtcNow)));
            return attachment;
        }
        catch
        {
            if (File.Exists(stagedPath))
                File.Delete(stagedPath);
            throw;
        }
    }

    public async Task<CampaignAttachmentContent> ReadAttachmentAsync(CampaignAttachment attachment, CampaignChannel channel, CancellationToken cancellationToken)
    {
        CampaignAttachmentPolicy.ValidateDescriptor(attachment, channel);
        int maximum = channel == CampaignChannel.Email ? MaximumEmailAttachmentBytes : MaximumWhatsAppAttachmentBytes;
        string ownerDirectory = Path.Combine(attachmentRoot, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ownerSid))));
        string fullPath = Path.GetFullPath(attachment.StagedPath);
        if (!fullPath.StartsWith(ownerDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Attachment path is outside the current owner's campaign storage.");
        FileInfo file = new(fullPath);
        if (!file.Exists || file.Length != attachment.Length || file.Length > maximum)
            throw new InvalidDataException("Staged attachment size changed; dispatch is blocked.");
        byte[] bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken);
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(hash), Convert.FromHexString(attachment.Sha256)))
            throw new InvalidDataException("Staged attachment hash changed; dispatch is blocked.");
        CampaignAttachmentPolicy.ValidateContent(attachment.FileName, channel, bytes);
        return new CampaignAttachmentContent(attachment, bytes);
    }

    public async Task<AudiencePreview> PreviewAudienceAsync(AudienceSelection selection, CampaignChannel channel, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        List<AudienceCandidate> candidates = await ReadCandidatesAsync(selection.Population, cancellationToken);
        Dictionary<CampaignExclusionReason, int> excluded = [];
        List<CampaignRecipient> eligible = [];
        Dictionary<string, List<string>> crmCooldowns = channel == CampaignChannel.WhatsApp
                ? await ReadCrmCooldownsAsync(cancellationToken)
                : new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        HashSet<int> requestedIds = selection.ContactIds.ToHashSet();
        if (!selection.SelectAllEligible && requestedIds.Count == 0)
            throw new ArgumentException("Select eligible contacts or explicitly select one or more contact IDs.", nameof(selection));

        foreach (IGrouping<string, AudienceCandidate> endpointGroup in candidates
            .Where(candidate => selection.SelectAllEligible || requestedIds.Contains(candidate.Id))
            .GroupBy(candidate => NormalizeEndpoint(channel, candidate.EndpointFor(channel)), StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(endpointGroup.Key))
            {
                foreach (AudienceCandidate item in endpointGroup)
                    AddExclusion(excluded, CampaignExclusionReason.InvalidEndpoint);
                continue;
            }

            ConsentGroup consentGroup = await ReadConsentGroupAsync(selection.Population, channel, endpointGroup.Key, cancellationToken);
            if (consentGroup.HasConflictingWithdrawal)
            {
                foreach (AudienceCandidate item in endpointGroup)
                    AddExclusion(excluded, CampaignExclusionReason.OptedOut);
                continue;
            }

            List<AudienceCandidate> permitted = [];
            foreach (AudienceCandidate candidate in endpointGroup)
            {
                if (!candidate.IsActive)
                {
                    AddExclusion(excluded, CampaignExclusionReason.InactiveCustomer);
                    continue;
                }

                ConsentDecision decision = GetConsentDecision(candidate, channel, endpointGroup.Key, consentGroup);
                if (decision == ConsentDecision.OptedOut)
                {
                    AddExclusion(excluded, CampaignExclusionReason.OptedOut);
                    continue;
                }
                if (decision == ConsentDecision.Conflicting)
                {
                    AddExclusion(excluded, CampaignExclusionReason.ConflictingConsent);
                    continue;
                }
                if (decision == ConsentDecision.Missing)
                {
                    AddExclusion(excluded, CampaignExclusionReason.MissingConsent);
                    continue;
                }
                if (crmCooldowns.TryGetValue(endpointGroup.Key, out List<string>? cooldowns))
                {
                    bool coolingDown = false;
                    foreach (string value in cooldowns)
                    {
                        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset lastAttempt))
                            throw new InvalidDataException("CRM WhatsApp cooldown contains an invalid timestamp; audience eligibility is blocked.");
                        if (lastAttempt.ToUniversalTime() > UtcNow.AddHours(-24))
                        {
                            coolingDown = true;
                            break;
                        }
                    }
                    if (coolingDown)
                    {
                        AddExclusion(excluded, CampaignExclusionReason.Cooldown);
                        continue;
                    }
                }
                permitted.Add(candidate);
            }

            if (permitted.Count == 0)
                continue;
            permitted.Sort((left, right) => left.Id.CompareTo(right.Id));
            AudienceCandidate selected = permitted[0];
            foreach (AudienceCandidate duplicate in permitted.Skip(1))
                AddExclusion(excluded, CampaignExclusionReason.DuplicateEndpoint);
            string contentHash = CampaignContentHash.Compute($"{channel}\n{endpointGroup.Key}\n{selected.Id}");
            eligible.Add(new CampaignRecipient(
                $"{selection.Population}:{selected.Id}", selection.Population, selected.Id,
                selected.DisplayName, selected.EndpointFor(channel), endpointGroup.Key, contentHash,
                CampaignRecipientState.Pending, 0, null, null, null, null, null));
        }

        if (eligible.Count > MaximumAudienceSize)
            throw new InvalidOperationException($"Campaign audience exceeds the {MaximumAudienceSize}-recipient safety cap.");
        if (selection.ContactIds.Count > 0)
        {
            foreach (int missingId in requestedIds.Except(candidates.Select(candidate => candidate.Id)))
                AddExclusion(excluded, CampaignExclusionReason.InactiveCustomer);
        }
        return new AudiencePreview(eligible, excluded
            .Select(item => new AudienceExclusion(item.Key, item.Value))
            .OrderBy(item => item.Reason).ToArray());

    }

    public async Task<IReadOnlyList<CampaignContactOption>> GetAudienceContactsAsync(
        CampaignPopulation population,
        string? search,
        int limit,
        CancellationToken cancellationToken)
    {
        List<AudienceCandidate> candidates = await ReadCandidatesAsync(population, cancellationToken);
        string query = search?.Trim() ?? string.Empty;
        Dictionary<int, string> segments = query.Length == 0
            ? []
            : await ReadAudienceSegmentsAsync(population, cancellationToken);
        return candidates
            .Where(candidate => query.Length == 0 ||
                candidate.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                candidate.Email.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                candidate.Phone.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (segments.TryGetValue(candidate.Id, out string? segment) &&
                    segment.Contains(query, StringComparison.CurrentCultureIgnoreCase)))
            .Take(Math.Clamp(limit, 1, MaximumAudienceSize))
            .Select(candidate => new CampaignContactOption(
                candidate.Population, candidate.Id, candidate.DisplayName, candidate.Email, candidate.Phone, candidate.IsActive))
            .ToArray();
    }

    public async Task<CampaignConsent> SetConsentAsync(
        CampaignPopulation population,
        int contactId,
        CampaignChannel channel,
        string endpoint,
        bool granted,
        string evidence,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(evidence) || evidence.Trim().Length > 2000)
            throw new ArgumentException("Consent evidence is required and must not exceed 2000 characters.", nameof(evidence));
        string normalized = NormalizeEndpoint(channel, endpoint);
        if (string.IsNullOrEmpty(normalized))
            throw new ArgumentException("A valid channel endpoint is required.", nameof(endpoint));
        AudienceCandidate? contact = (await ReadCandidatesAsync(population, cancellationToken)).SingleOrDefault(item => item.Id == contactId);
        if (contact is null || !contact.IsActive)
            throw new InvalidOperationException("Consent can only be recorded for an existing active contact.");
        if (!string.Equals(NormalizeEndpoint(channel, contact.EndpointFor(channel)), normalized, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Consent must be bound to the contact's current channel endpoint.");

        await InitializeAsync(cancellationToken);
        DateTimeOffset now = UtcNow;
        await using SqliteConnection connection = await OpenCrmAsync(cancellationToken);
        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        if (population == CampaignPopulation.CrmCompanies && channel == CampaignChannel.WhatsApp)
        {
            AudienceCandidate? current = await ReadCandidateAsync(connection, transaction, population, contactId, cancellationToken);
            if (current is null || !current.IsActive ||
                !string.Equals(NormalizeEndpoint(channel, current.EndpointFor(channel)), normalized, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("CRM WhatsApp endpoint changed before consent could be committed.");
        }
        await ExecuteAsync(connection, transaction, """
INSERT INTO CampaignConsents(OwnerSid, Population, ContactId, Channel, Endpoint, NormalizedEndpoint, Granted, Evidence, RecordedUtc, IsWithdrawal)
VALUES($owner,$population,$contact,$channel,$endpoint,$normalized,$granted,$evidence,$now,$withdrawal);
""", cancellationToken, ("$owner", ownerSid), ("$population", population.ToString()), ("$contact", contactId),
            ("$channel", channel.ToString()), ("$endpoint", endpoint.Trim()), ("$normalized", normalized),
            ("$granted", granted ? 1 : 0), ("$evidence", evidence.Trim()), ("$now", Stamp(now)), ("$withdrawal", granted ? 0 : 1));
        if (population == CampaignPopulation.CrmCompanies && channel == CampaignChannel.WhatsApp)
        {
            await ExecuteAsync(connection, transaction, """
UPDATE CrmCompanies SET WhatsAppMarketingOptIn=$granted,
 WhatsAppOptInAtUtc=CASE WHEN $granted=1 THEN $now ELSE WhatsAppOptInAtUtc END,
 WhatsAppOptInEvidence=CASE WHEN $granted=1 THEN $evidence ELSE WhatsAppOptInEvidence END,
 WhatsAppOptOutAtUtc=CASE WHEN $granted=1 THEN NULL ELSE $now END
WHERE Id=$contact;
""", cancellationToken, ("$granted", granted ? 1 : 0), ("$now", Stamp(now)),
                ("$evidence", evidence.Trim()), ("$contact", contactId));
        }
        await transaction.CommitAsync(cancellationToken);
        return new CampaignConsent(population, contactId, channel, endpoint.Trim(), granted, evidence.Trim(), now, !granted);
    }

    internal async Task RecordVerifiedWhatsAppTemplateAsync(
        WhatsAppTemplateApproval template,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = UtcNow;
        if (!template.IsVerifiedMarketing ||
            string.IsNullOrWhiteSpace(template.Name) ||
            string.IsNullOrWhiteSpace(template.LanguageCode) ||
            template.BodyParameterCount is < 0 or > 20 ||
            template.VerifiedAtUtc is null ||
            template.VerifiedAtUtc > now ||
            template.VerifiedAtUtc < now.AddDays(-1))
            throw new InvalidDataException("Only a recent provider-verified approved marketing template can be recorded.");

        await InitializeAsync(cancellationToken);
        await using SqliteConnection connection = await OpenCrmAsync(cancellationToken);
        await ExecuteAsync(connection, null, """
INSERT INTO CampaignVerifiedWhatsAppTemplates(OwnerSid, Name, LanguageCode, TemplateJson, VerifiedUtc)
VALUES($owner,$name,$language,$template,$verified)
ON CONFLICT(OwnerSid,Name,LanguageCode) DO UPDATE SET
 TemplateJson=excluded.TemplateJson, VerifiedUtc=excluded.VerifiedUtc;
""", cancellationToken,
            ("$owner", ownerSid),
            ("$name", template.Name),
            ("$language", template.LanguageCode),
            ("$template", Serialize(template)),
            ("$verified", Stamp(template.VerifiedAtUtc.Value)));
    }

    internal async Task<WhatsAppTemplateApproval?> GetVerifiedWhatsAppTemplateAsync(
        string name,
        string languageCode,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using SqliteConnection connection = await OpenCrmAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
SELECT TemplateJson, VerifiedUtc FROM CampaignVerifiedWhatsAppTemplates
WHERE OwnerSid=$owner AND Name=$name COLLATE NOCASE AND LanguageCode=$language COLLATE NOCASE;
""";
        command.Parameters.AddWithValue("$owner", ownerSid);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$language", languageCode);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        WhatsAppTemplateApproval template = Deserialize<WhatsAppTemplateApproval>(reader.GetString(0));
        DateTimeOffset verifiedAt = ParseStamp(reader.GetString(1));
        return template.VerifiedAtUtc == verifiedAt ? template : null;
    }

    public async Task<IReadOnlyList<CampaignConsent>> GetConsentsAsync(CampaignPopulation? population, int? contactId, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using SqliteConnection connection = await OpenCrmAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
SELECT Population, ContactId, Channel, Endpoint, Granted, Evidence, RecordedUtc, IsWithdrawal
FROM CampaignConsents WHERE OwnerSid=$owner
 AND ($population IS NULL OR Population=$population)
 AND ($contact IS NULL OR ContactId=$contact)
ORDER BY Id DESC;
""";
        command.Parameters.AddWithValue("$owner", ownerSid);
        command.Parameters.AddWithValue("$population", population?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$contact", contactId ?? (object)DBNull.Value);
        List<CampaignConsent> items = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            items.Add(new CampaignConsent(Enum.Parse<CampaignPopulation>(reader.GetString(0)), reader.GetInt32(1),
                Enum.Parse<CampaignChannel>(reader.GetString(2)), reader.GetString(3), reader.GetInt32(4) == 1,
                reader.GetString(5), ParseStamp(reader.GetString(6)), reader.GetInt32(7) == 1));
        return items;
    }

    public async Task<CampaignApprovalManifest> PrepareApprovalAsync(Guid campaignId, long expectedRevision, CancellationToken cancellationToken)
    {
        CampaignDraft draft = await GetDraftAsync(campaignId, cancellationToken);
        if (draft.Revision != expectedRevision || draft.State != CampaignState.Draft)
            throw new InvalidOperationException("Campaign revision changed or is not editable; reload before preparing approval.");
        AudiencePreview audience = await PreviewAudienceAsync(draft.Input.Audience, draft.Input.Channel, cancellationToken);
        IReadOnlyList<CampaignAttachment> attachments = await GetAttachmentsAsync(draft.Input.AttachmentIds, cancellationToken);
        int maxAttachmentBytes = draft.Input.Channel == CampaignChannel.Email
            ? MaximumEmailAttachmentBytes : MaximumWhatsAppAttachmentBytes;
        if (attachments.Sum(item => item.Length) > maxAttachmentBytes)
            throw new InvalidDataException($"Combined attachments exceed the {maxAttachmentBytes}-byte channel limit.");
        foreach (CampaignAttachment attachment in attachments)
            _ = await ReadAttachmentAsync(attachment, draft.Input.Channel, cancellationToken);
        IReadOnlyList<CampaignRecipient> frozenRecipients = audience.Recipients.Select(recipient => recipient with
        {
            ContentHash = CampaignContentHash.Compute(Serialize(new
            {
                draft.Input.Channel,
                recipient.ContactKey,
                recipient.Endpoint,
                draft.Input.Subject,
                draft.Input.Content,
                draft.Input.WhatsAppTemplate,
                draft.Input.WhatsAppComponents,
                Attachments = attachments.Select(item => new { item.Id, item.Sha256 })
            }))
        }).ToArray();
        if (frozenRecipients.Count == 0)
            throw new InvalidOperationException("Approval is blocked because the selected audience has no eligible recipients.");
        if (frozenRecipients.Count > draft.Input.MaximumRecipients)
            throw new InvalidOperationException("Eligible audience exceeds the configured campaign recipient cap.");
        string hash = CampaignContentHash.ComputeManifest(draft.Input, attachments, frozenRecipients);
        return new CampaignApprovalManifest(campaignId, draft.Revision, draft.Input, attachments, frozenRecipients, UtcNow, hash);
    }

    public async Task<CampaignSnapshot> ApproveAsync(CampaignApprovalManifest manifest, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenCrmAsync(cancellationToken);
        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        CampaignDraft? draft = await ReadDraftAsync(connection, transaction, manifest.CampaignId, cancellationToken);
        if (draft is null || draft.Revision != manifest.Revision || draft.State != CampaignState.Draft ||
            !string.Equals(Serialize(draft.Input), Serialize(manifest.Draft), StringComparison.Ordinal) ||
            !string.Equals(CampaignContentHash.ComputeManifest(manifest.Draft, manifest.Attachments, manifest.Recipients), manifest.Hash, StringComparison.Ordinal))
            throw new InvalidOperationException("Campaign changed after approval preview; prepare a new immutable snapshot.");
        await ExecuteAsync(connection, transaction, "DELETE FROM CampaignRecipients WHERE CampaignId=$id;", cancellationToken,
            ("$id", manifest.CampaignId.ToString("D")));
        foreach (CampaignRecipient recipient in manifest.Recipients)
        {
            await ExecuteAsync(connection, transaction, """
INSERT INTO CampaignRecipients(CampaignId, ContactKey, RecipientJson, State)
VALUES($campaign,$contact,$json,'Pending');
""", cancellationToken, ("$campaign", manifest.CampaignId.ToString("D")),
                ("$contact", recipient.ContactKey), ("$json", Serialize(recipient)));
        }
        DateTimeOffset now = UtcNow;
        string snapshot = Serialize(manifest);
        int changed = await ExecuteAsync(connection, transaction, """
UPDATE Campaigns SET State='Approved', SnapshotJson=$snapshot, ManifestHash=$hash, UpdatedUtc=$now
WHERE Id=$id AND OwnerSid=$owner AND Revision=$revision AND State='Draft';
""", cancellationToken, ("$snapshot", snapshot), ("$hash", manifest.Hash), ("$now", Stamp(now)),
            ("$id", manifest.CampaignId.ToString("D")), ("$owner", ownerSid), ("$revision", manifest.Revision));
        if (changed != 1)
            throw new InvalidOperationException("Campaign changed while saving its approval snapshot.");
        await transaction.CommitAsync(cancellationToken);
        return new CampaignSnapshot(manifest.CampaignId, manifest.Revision, CampaignState.Approved, ownerSid,
            manifest.Draft, manifest.Hash, now);
    }

    public async Task<CampaignSnapshot> ChangeStateAsync(Guid id, long expectedRevision, CampaignState target, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using SqliteConnection connection = await OpenCrmAsync(cancellationToken);
        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        CampaignSnapshot current = await ReadSnapshotAsync(connection, transaction, id, cancellationToken)
            ?? throw new KeyNotFoundException("Campaign was not found for this Windows owner.");
        if (current.Revision != expectedRevision)
            throw new InvalidOperationException($"Campaign revision conflict: current revision is {current.Revision}.");
        bool allowed = (target, current.State) switch
        {
            (CampaignState.Active or CampaignState.Scheduled, CampaignState.Approved or CampaignState.Paused) => true,
            (CampaignState.Active, CampaignState.Scheduled) => true,
            (CampaignState.Paused, CampaignState.Active or CampaignState.Scheduled) => true,
            (CampaignState.CancelRequested, CampaignState.Approved or CampaignState.Active or CampaignState.Scheduled or CampaignState.Paused) => true,
            (CampaignState.Cancelled, CampaignState.CancelRequested) => true,
            (CampaignState.Completed, CampaignState.Active or CampaignState.Scheduled) => true,
            _ => false
        };
        if (!allowed)
            throw new InvalidOperationException($"Campaign cannot transition from {current.State} to {target}.");
        if (target == CampaignState.Completed &&
            await HasRetryableRecipientsAsync(connection, transaction, id, cancellationToken))
            return current;
        DateTimeOffset now = UtcNow;
        if (target == CampaignState.CancelRequested)
        {
            await ExecuteAsync(connection, transaction, """
UPDATE CampaignRecipients SET State='Cancelled', ClaimToken=NULL, ClaimExpiresUtc=NULL
WHERE CampaignId=$id AND State IN ('Pending','Claimed','Rejected');
""", cancellationToken, ("$id", id.ToString("D")));
        }
        int changed = await ExecuteAsync(connection, transaction, """
UPDATE Campaigns SET State=$state, Revision=Revision+1, UpdatedUtc=$now
WHERE Id=$id AND OwnerSid=$owner AND Revision=$revision;
""", cancellationToken, ("$state", target.ToString()), ("$now", Stamp(now)),
            ("$id", id.ToString("D")), ("$owner", ownerSid), ("$revision", expectedRevision));
        if (changed != 1)
            throw new InvalidOperationException("Campaign changed while applying its state transition.");
        await transaction.CommitAsync(cancellationToken);
        return current with { Revision = current.Revision + 1, State = target, UpdatedAtUtc = now };
    }

    public async Task<CampaignSnapshot?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using SqliteConnection connection = await OpenCrmAsync(cancellationToken);
        return await ReadSnapshotAsync(connection, null, id, cancellationToken);
    }

    public async Task<CampaignPage> ListAsync(CampaignQuery query, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        int offset = Math.Max(0, query.Offset);
        int limit = Math.Clamp(query.Limit, 1, 200);
        await using SqliteConnection connection = await OpenCrmAsync(cancellationToken);
        int total;
        await using (SqliteCommand count = connection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM Campaigns WHERE OwnerSid=$owner AND ($state IS NULL OR State=$state);";
            count.Parameters.AddWithValue("$owner", ownerSid);
            count.Parameters.AddWithValue("$state", query.State?.ToString() ?? (object)DBNull.Value);
            total = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
SELECT Id, Revision, State, DraftJson, ManifestHash, UpdatedUtc, LastErrorCode
FROM Campaigns WHERE OwnerSid=$owner AND ($state IS NULL OR State=$state)
ORDER BY UpdatedUtc DESC LIMIT $limit OFFSET $offset;
""";
        command.Parameters.AddWithValue("$owner", ownerSid);
        command.Parameters.AddWithValue("$state", query.State?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);
        List<CampaignSnapshot> results = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            results.Add(new CampaignSnapshot(Guid.Parse(reader.GetString(0)), reader.GetInt64(1),
                Enum.Parse<CampaignState>(reader.GetString(2)), ownerSid,
                Deserialize<CampaignDraftInput>(reader.GetString(3)), reader.IsDBNull(4) ? null : reader.GetString(4),
                ParseStamp(reader.GetString(5)), reader.IsDBNull(6) ? null : reader.GetString(6)));
        return new CampaignPage(results, total);
    }

    public async Task<RecipientPage> GetRecipientsAsync(Guid campaignId, int offset, int limit, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using SqliteConnection connection = await OpenCrmAsync(cancellationToken);
        int safeOffset = Math.Max(0, offset);
        int safeLimit = Math.Clamp(limit, 1, 500);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
SELECT r.RecipientJson, r.State, r.Attempts, r.NextAttemptUtc, r.LastOutcomeCode, r.ProviderMessageId, r.AcceptedUtc, r.DeliveryConfirmed
FROM CampaignRecipients r JOIN Campaigns c ON c.Id=r.CampaignId
WHERE r.CampaignId=$id AND c.OwnerSid=$owner ORDER BY r.Id LIMIT $limit OFFSET $offset;
""";
        command.Parameters.AddWithValue("$id", campaignId.ToString("D"));
        command.Parameters.AddWithValue("$owner", ownerSid);
        command.Parameters.AddWithValue("$limit", safeLimit);
        command.Parameters.AddWithValue("$offset", safeOffset);
        {
            List<CampaignRecipient> items = [];
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                CampaignRecipient saved = Deserialize<CampaignRecipient>(reader.GetString(0));
                items.Add(saved with
                {
                    State = Enum.Parse<CampaignRecipientState>(reader.GetString(1)),
                    Attempts = reader.GetInt32(2),
                    NextAttemptAtUtc = reader.IsDBNull(3) ? null : ParseStamp(reader.GetString(3)),
                    LastOutcomeCode = reader.IsDBNull(4) ? null : reader.GetString(4),
                    ProviderMessageId = reader.IsDBNull(5) ? null : reader.GetString(5),
                    AcceptedAtUtc = reader.IsDBNull(6) ? null : ParseStamp(reader.GetString(6)),
                    DeliveryConfirmed = reader.IsDBNull(7) ? null : reader.GetInt32(7) == 1
                });
            }
            await reader.DisposeAsync();
            await using SqliteCommand count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM CampaignRecipients r JOIN Campaigns c ON c.Id=r.CampaignId WHERE r.CampaignId=$id AND c.OwnerSid=$owner;";
            count.Parameters.AddWithValue("$id", campaignId.ToString("D"));
            count.Parameters.AddWithValue("$owner", ownerSid);
            int total = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            return new RecipientPage(items, total);
        }
    }

    public async Task<IReadOnlyList<CampaignSnapshot>> GetRunnableCampaignsAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using SqliteConnection connection = await OpenCrmAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Revision, State, DraftJson, ManifestHash, UpdatedUtc, LastErrorCode FROM Campaigns WHERE OwnerSid=$owner AND State IN ('Active','Scheduled','CancelRequested') ORDER BY UpdatedUtc;";
        command.Parameters.AddWithValue("$owner", ownerSid);
        List<CampaignSnapshot> results = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            results.Add(new CampaignSnapshot(Guid.Parse(reader.GetString(0)), reader.GetInt64(1),
                Enum.Parse<CampaignState>(reader.GetString(2)), ownerSid,
                Deserialize<CampaignDraftInput>(reader.GetString(3)), reader.IsDBNull(4) ? null : reader.GetString(4),
                ParseStamp(reader.GetString(5)), reader.IsDBNull(6) ? null : reader.GetString(6)));
        return results;
    }

    public async Task<CampaignApprovalManifest> GetApprovedManifestAsync(Guid id, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using SqliteConnection connection = await OpenCrmAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT SnapshotJson FROM Campaigns WHERE Id=$id AND OwnerSid=$owner AND SnapshotJson IS NOT NULL;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$owner", ownerSid);
        object? value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is not string json)
            throw new InvalidOperationException("Approved campaign snapshot is unavailable.");
        return Deserialize<CampaignApprovalManifest>(json);
    }

    public async Task<IReadOnlyList<CampaignRecipient>> GetPendingRecipientsAsync(Guid campaignId, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenCrmAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
SELECT RecipientJson FROM CampaignRecipients r JOIN Campaigns c ON c.Id=r.CampaignId
WHERE r.CampaignId=$id AND c.OwnerSid=$owner
 AND (r.State='Pending' OR r.State='Rejected' AND r.NextAttemptUtc IS NOT NULL AND r.NextAttemptUtc <= $now)
ORDER BY r.Id;
""";
        command.Parameters.AddWithValue("$id", campaignId.ToString("D"));
        command.Parameters.AddWithValue("$owner", ownerSid);
        command.Parameters.AddWithValue("$now", Stamp(UtcNow));
        List<CampaignRecipient> results = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            results.Add(Deserialize<CampaignRecipient>(reader.GetString(0)));
        return results;
    }

    public async Task<string?> TryClaimAsync(Guid campaignId, string contactKey, long revision, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenCrmAsync(cancellationToken);
        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        string token = Guid.NewGuid().ToString("N");
        int changed = await ExecuteAsync(connection, transaction, """
UPDATE CampaignRecipients
SET State='Claimed', ClaimToken=$token, ClaimExpiresUtc=$expiry
WHERE CampaignId=$campaign AND ContactKey=$contact
 AND (State='Pending' OR State='Rejected' AND NextAttemptUtc IS NOT NULL AND NextAttemptUtc <= $now)
 AND EXISTS(SELECT 1 FROM Campaigns c WHERE c.Id=$campaign AND c.OwnerSid=$owner AND c.Revision=$revision AND c.State='Active');
""", cancellationToken, ("$token", token), ("$expiry", Stamp(now.AddSeconds(ClaimLeaseSeconds))),
            ("$campaign", campaignId.ToString("D")), ("$contact", contactKey), ("$now", Stamp(now)),
            ("$owner", ownerSid), ("$revision", revision));
        await transaction.CommitAsync(cancellationToken);
        return changed == 1 ? token : null;
    }

    public async Task RecoverInterruptedAsync(CancellationToken cancellationToken)
    {
        if (!await TryAcquireWorkerLeaseAsync(cancellationToken))
            return;
        DateTimeOffset now = UtcNow;
        await using SqliteConnection connection = await OpenCrmAsync(cancellationToken);
        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        await ExecuteAsync(connection, transaction, """
UPDATE CampaignRecipients SET State='Pending', ClaimToken=NULL, ClaimExpiresUtc=NULL
WHERE State='Claimed' AND ClaimExpiresUtc <= $now
 AND CampaignId IN (SELECT Id FROM Campaigns WHERE OwnerSid=$owner);
""", cancellationToken, ("$now", Stamp(now)), ("$owner", ownerSid));
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<bool> TryAcquireWorkerLeaseAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using SqliteConnection connection = await OpenCrmAsync(cancellationToken);
        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        DateTimeOffset now = UtcNow;
        bool abandoned = true;
        await using (SqliteCommand read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT WorkerId, ExpiresUtc FROM CampaignWorkerLeases WHERE OwnerSid=$owner;";
            read.Parameters.AddWithValue("$owner", ownerSid);
            await using SqliteDataReader row = await read.ExecuteReaderAsync(cancellationToken);
            if (await row.ReadAsync(cancellationToken) && ParseStamp(row.GetString(1)) > now)
            {
                if (row.GetString(0) != workerId)
                    return false;
                abandoned = false;
            }
        }
        if (abandoned)
        {
            // A live leader cannot be quarantined. Takeover is serialized with authorization.
            // Authorized sends from an expired leader remain uncertain and are never resent.
            await ExecuteAsync(connection, transaction, """
UPDATE CampaignRecipients SET State='Unknown', ClaimToken=NULL, ClaimExpiresUtc=NULL,
 LastOutcomeCode='OutcomeUnknownAfterLeaseExpiration'
WHERE State='Sending' AND CampaignId IN (SELECT Id FROM Campaigns WHERE OwnerSid=$owner);
UPDATE CampaignAttempts SET State='Unknown', CompletedUtc=$now, OutcomeCode='OutcomeUnknownAfterLeaseExpiration'
WHERE State='Sending' AND CampaignId IN (SELECT Id FROM Campaigns WHERE OwnerSid=$owner);
""", cancellationToken, ("$owner", ownerSid), ("$now", Stamp(now)));
        }
        await ExecuteAsync(connection, transaction, """
INSERT INTO CampaignWorkerLeases(OwnerSid,WorkerId,ExpiresUtc) VALUES($owner,$worker,$expiry)
ON CONFLICT(OwnerSid) DO UPDATE SET WorkerId=excluded.WorkerId, ExpiresUtc=excluded.ExpiresUtc;
""", cancellationToken, ("$owner", ownerSid), ("$worker", workerId), ("$expiry", Stamp(now.AddSeconds(WorkerLeaseSeconds))));
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> RenewWorkerLeaseAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenCrmAsync(cancellationToken);
        DateTimeOffset now = UtcNow;
        return await ExecuteAsync(connection, null, """
UPDATE CampaignWorkerLeases SET ExpiresUtc=$expiry
WHERE OwnerSid=$owner AND WorkerId=$worker AND ExpiresUtc>$now;
""", cancellationToken, ("$owner", ownerSid), ("$worker", workerId),
            ("$now", Stamp(now)), ("$expiry", Stamp(now.AddSeconds(WorkerLeaseSeconds)))) == 1;
    }

    public async Task ReleaseWorkerLeaseAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(crmDatabasePath))
            return;
        await using SqliteConnection connection = await OpenCrmAsync(cancellationToken);
        await ExecuteAsync(connection, null, """
UPDATE CampaignWorkerLeases SET ExpiresUtc=$now WHERE OwnerSid=$owner AND WorkerId=$worker;
""", cancellationToken, ("$owner", ownerSid), ("$worker", workerId), ("$now", Stamp(UtcNow)));
    }

    public async Task ReleaseClaimAsync(Guid campaignId, string contactKey, string token, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenCrmAsync(cancellationToken);
        await ExecuteAsync(connection, null, """
UPDATE CampaignRecipients SET State='Pending', ClaimToken=NULL, ClaimExpiresUtc=NULL
WHERE CampaignId=$campaign AND ContactKey=$contact AND State='Claimed' AND ClaimToken=$token
 AND CampaignId IN (SELECT Id FROM Campaigns WHERE OwnerSid=$owner);
""", cancellationToken, ("$campaign", campaignId.ToString("D")), ("$contact", contactKey),
            ("$token", token), ("$owner", ownerSid));
    }

    public async Task<bool> HasRetryableRecipientsAsync(Guid campaignId, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenCrmAsync(cancellationToken);
        return await HasRetryableRecipientsAsync(connection, null, campaignId, cancellationToken);
    }

    private async Task<bool> HasRetryableRecipientsAsync(SqliteConnection connection, SqliteTransaction? transaction,
        Guid campaignId, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
SELECT EXISTS(SELECT 1 FROM CampaignRecipients r JOIN Campaigns c ON c.Id=r.CampaignId
 WHERE c.OwnerSid=$owner AND r.CampaignId=$campaign AND
 (r.State IN ('Pending','Claimed','Sending') OR r.State='Rejected' AND r.Attempts<3 AND r.NextAttemptUtc IS NOT NULL));
""";
        command.Parameters.AddWithValue("$owner", ownerSid);
        command.Parameters.AddWithValue("$campaign", campaignId.ToString("D"));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
    }

    public async Task<CampaignSendAuthorization?> AuthorizeDispatchAsync(
        CampaignSnapshot campaign,
        CampaignRecipient recipient,
        string claimToken,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!await TryAcquireWorkerLeaseAsync(cancellationToken))
        {
            await ReleaseClaimAsync(campaign.Id, recipient.ContactKey, claimToken, cancellationToken);
            return null;
        }
        await using SqliteConnection connection = await OpenCrmAsync(cancellationToken);
        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        await using (SqliteCommand leader = connection.CreateCommand())
        {
            leader.Transaction = transaction;
            leader.CommandText = "SELECT EXISTS(SELECT 1 FROM CampaignWorkerLeases WHERE OwnerSid=$owner AND WorkerId=$worker AND ExpiresUtc>$now);";
            leader.Parameters.AddWithValue("$owner", ownerSid);
            leader.Parameters.AddWithValue("$worker", workerId);
            leader.Parameters.AddWithValue("$now", Stamp(now));
            if (Convert.ToInt32(await leader.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                await ReleaseClaimAsync(campaign.Id, recipient.ContactKey, claimToken, cancellationToken);
                return null;
            }
        }
        CampaignSnapshot? current = await ReadSnapshotAsync(connection, transaction, campaign.Id, cancellationToken);
        if (current is null || current.OwnerSid != ownerSid || current.State != CampaignState.Active ||
            current.Revision != campaign.Revision || current.ManifestHash is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            await ReleaseClaimAsync(campaign.Id, recipient.ContactKey, claimToken, cancellationToken);
            return null;
        }
        await using (SqliteCommand execution = connection.CreateCommand())
        {
            execution.Transaction = transaction;
            execution.CommandText = "SELECT ExecutionEnabled FROM CampaignRuntimeSettings WHERE OwnerSid=$owner;";
            execution.Parameters.AddWithValue("$owner", ownerSid);
            if (Convert.ToInt32(await execution.ExecuteScalarAsync(cancellationToken) ?? 0, CultureInfo.InvariantCulture) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                await ReleaseClaimAsync(campaign.Id, recipient.ContactKey, claimToken, cancellationToken);
                return null;
            }
        }

        CampaignApprovalManifest manifest = await ReadManifestAsync(connection, transaction, campaign.Id, cancellationToken);
        if (manifest.Revision > current.Revision || manifest.Hash != current.ManifestHash ||
            !string.Equals(Serialize(manifest.Draft), Serialize(current.Input), StringComparison.Ordinal) ||
            !manifest.Recipients.Any(item => item.ContactKey == recipient.ContactKey))
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new InvalidDataException("Approved campaign snapshot failed its integrity check.");
        }

        CampaignRecipient? frozen = manifest.Recipients.SingleOrDefault(item => item.ContactKey == recipient.ContactKey);
        if (frozen is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new InvalidDataException("Recipient is outside the immutable approved audience.");
        }

        await using SqliteCommand rowCommand = connection.CreateCommand();
        rowCommand.Transaction = transaction;
        rowCommand.CommandText = """
SELECT Id, State, ClaimToken, ClaimExpiresUtc, Attempts FROM CampaignRecipients
WHERE CampaignId=$campaign AND ContactKey=$contact;
""";
        rowCommand.Parameters.AddWithValue("$campaign", campaign.Id.ToString("D"));
        rowCommand.Parameters.AddWithValue("$contact", recipient.ContactKey);
        await using SqliteDataReader row = await rowCommand.ExecuteReaderAsync(cancellationToken);
        if (!await row.ReadAsync(cancellationToken) || row.GetString(1) != "Claimed" ||
            row.GetString(2) != claimToken || row.IsDBNull(3) || ParseStamp(row.GetString(3)) <= now)
        {
            await row.DisposeAsync();
            await transaction.RollbackAsync(cancellationToken);
            await ReleaseClaimAsync(campaign.Id, recipient.ContactKey, claimToken, cancellationToken);
            return null;
        }
        long recipientId = row.GetInt64(0);
        int attemptCount = row.GetInt32(4);
        await row.DisposeAsync();

        if (!await IsStillEligibleAsync(connection, transaction, frozen, manifest.Draft.Channel, cancellationToken))
        {
            await ExecuteAsync(connection, transaction, """
UPDATE CampaignRecipients SET State='Skipped', ClaimToken=NULL, ClaimExpiresUtc=NULL, LastOutcomeCode='ConsentOrEndpointChanged'
WHERE Id=$id AND State='Claimed' AND ClaimToken=$token;
""", cancellationToken, ("$id", recipientId), ("$token", claimToken));
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        CampaignDraftInput input = manifest.Draft;
        TimeZoneInfo timezone = CampaignWindowSchedule.ResolveTimeZone(input.TimeZoneId);
        if (!CampaignWindowSchedule.IsWithinWindow(timezone, input.DailyWindows, now) ||
            input.EndsAtUtc is not null && now >= input.EndsAtUtc.Value ||
            input.StartsAtUtc > now)
        {
            await ExecuteAsync(connection, transaction, """
UPDATE CampaignRecipients SET State='Pending', ClaimToken=NULL, ClaimExpiresUtc=NULL
WHERE Id=$id AND State='Claimed' AND ClaimToken=$token;
""", cancellationToken, ("$id", recipientId), ("$token", claimToken));
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        List<int> crmContactIds = [];
        if (input.Channel == CampaignChannel.WhatsApp)
        {
            CrmCooldownState cooldown = await ReadCrmCooldownStateAsync(
                connection, transaction, recipient.NormalizedEndpoint, cancellationToken);
            crmContactIds = cooldown.ContactIds.ToList();
            if (cooldown.LatestAttemptUtc is DateTimeOffset lastAttempt &&
                lastAttempt > now.AddHours(-24))
            {
                await ExecuteAsync(connection, transaction, """
UPDATE CampaignRecipients SET State='Pending', ClaimToken=NULL, ClaimExpiresUtc=NULL,
 NextAttemptUtc=$next, LastOutcomeCode='Cooldown'
WHERE Id=$recipient AND State='Claimed' AND ClaimToken=$token;
""", cancellationToken, ("$next", Stamp(lastAttempt.AddHours(24))), ("$recipient", recipientId), ("$token", claimToken));
                await transaction.CommitAsync(cancellationToken);
                return null;
            }
        }

        DateTimeOffset? nextAllowed = await ReadEndpointReservationAsync(connection, transaction, recipient, input.Channel, input.MinimumSpacing, cancellationToken);
        if (nextAllowed is not null && nextAllowed > now)
        {
            await ExecuteAsync(connection, transaction, """
UPDATE CampaignRecipients SET State='Pending', ClaimToken=NULL, ClaimExpiresUtc=NULL,
 NextAttemptUtc=$next, LastOutcomeCode='Pacing'
WHERE Id=$recipient AND State='Claimed' AND ClaimToken=$token;
""", cancellationToken, ("$next", Stamp(nextAllowed.Value)), ("$recipient", recipientId), ("$token", claimToken));
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        DateTimeOffset? campaignNext = await ReadCampaignSpacingAsync(connection, transaction, campaign.Id, cancellationToken);
        if (campaignNext is not null && campaignNext > now)
        {
            await ExecuteAsync(connection, transaction, """
UPDATE CampaignRecipients SET State='Pending', ClaimToken=NULL, ClaimExpiresUtc=NULL,
 NextAttemptUtc=$next, LastOutcomeCode='Pacing'
WHERE Id=$recipient AND State='Claimed' AND ClaimToken=$token;
""", cancellationToken, ("$next", Stamp(campaignNext.Value)), ("$recipient", recipientId), ("$token", claimToken));
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        string attemptId = Guid.NewGuid().ToString("N");
        string requestHash = frozen.ContentHash;
        int started = await ExecuteAsync(connection, transaction, """
UPDATE CampaignRecipients SET State='Sending', Attempts=Attempts+1
WHERE Id=$recipient AND State='Claimed' AND ClaimToken=$token;
""", cancellationToken, ("$recipient", recipientId), ("$token", claimToken));
        if (started != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }
        await ExecuteAsync(connection, transaction, """
INSERT INTO CampaignAttempts(Id, CampaignId, RecipientId, RequestHash, State, AuthorizedUtc)
VALUES($attempt,$campaign,$recipient,$hash,'Sending',$now);
""", cancellationToken, ("$attempt", attemptId), ("$campaign", campaign.Id.ToString("D")),
            ("$recipient", recipientId), ("$hash", requestHash), ("$now", Stamp(now)));
        await ExecuteAsync(connection, transaction, """
INSERT INTO CampaignEndpointReservations(OwnerSid, Channel, NormalizedEndpoint, LastAttemptUtc, CampaignId)
VALUES($owner,$channel,$endpoint,$now,$campaign)
ON CONFLICT(OwnerSid,Channel,NormalizedEndpoint) DO UPDATE SET LastAttemptUtc=excluded.LastAttemptUtc, CampaignId=excluded.CampaignId;
""", cancellationToken, ("$owner", ownerSid), ("$channel", input.Channel.ToString()),
            ("$endpoint", recipient.NormalizedEndpoint), ("$now", Stamp(now)), ("$campaign", campaign.Id.ToString("D")));
        if (crmContactIds.Count > 0)
        {
            foreach (int contactId in crmContactIds)
            {
                await ExecuteAsync(connection, transaction, """
INSERT INTO CrmWhatsAppSendCooldowns(CrmCompanyId, LastAttemptAtUtc) VALUES($contact,$now)
ON CONFLICT(CrmCompanyId) DO UPDATE SET LastAttemptAtUtc=excluded.LastAttemptAtUtc;
""", cancellationToken, ("$contact", contactId), ("$now", Stamp(now)));
            }
            if (recipient.Population == CampaignPopulation.CrmCompanies)
                await ExecuteAsync(connection, transaction, """
INSERT INTO CrmActivities(CrmCompanyId, ActivityType, Description, ActivityAtUtc, UserName)
VALUES($contact,'WhatsAppCampaignAttempt','Campaign attempt authorized', $now, $username);
""", cancellationToken, ("$contact", recipient.ContactId), ("$now", Stamp(now)), ("$username", Environment.UserName));
        }
        await transaction.CommitAsync(cancellationToken);
        return new CampaignSendAuthorization(attemptId, manifest, frozen, attemptCount + 1);
    }

    public async Task CompleteAttemptAsync(
        CampaignSendAuthorization authorization,
        CampaignSendResult result,
        DateTimeOffset completedAtUtc,
        TimeSpan minimumSpacing,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenCrmAsync(cancellationToken);
        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        bool recoveredAttempt;
        await using (SqliteCommand attempt = connection.CreateCommand())
        {
            attempt.Transaction = transaction;
            attempt.CommandText = """
SELECT a.State,a.OutcomeCode FROM CampaignAttempts a JOIN Campaigns c ON c.Id=a.CampaignId
WHERE a.Id=$attempt AND c.OwnerSid=$owner AND a.CampaignId=$campaign;
""";
            attempt.Parameters.AddWithValue("$attempt", authorization.AttemptId);
            attempt.Parameters.AddWithValue("$owner", ownerSid);
            attempt.Parameters.AddWithValue("$campaign", authorization.Manifest.CampaignId.ToString("D"));
            await using SqliteDataReader row = await attempt.ExecuteReaderAsync(cancellationToken);
            if (!await row.ReadAsync(cancellationToken))
                throw new InvalidOperationException("Campaign attempt is not owned by this worker's Windows owner.");
            recoveredAttempt = row.GetString(0) == "Unknown" && !row.IsDBNull(1) &&
                row.GetString(1) == "OutcomeUnknownAfterLeaseExpiration";
        }
        string state = result.Disposition switch
        {
            CampaignSendDisposition.Accepted => CampaignRecipientState.Accepted.ToString(),
            CampaignSendDisposition.DefinitelyRejected when result.SafeToRetry && authorization.AttemptNumber < 3 => CampaignRecipientState.Rejected.ToString(),
            CampaignSendDisposition.DefinitelyRejected => CampaignRecipientState.Rejected.ToString(),
            _ => CampaignRecipientState.Unknown.ToString()
        };
        DateTimeOffset? nextAttempt = result.Disposition == CampaignSendDisposition.DefinitelyRejected &&
            result.SafeToRetry && authorization.AttemptNumber < 3 && !recoveredAttempt
                ? completedAtUtc + (result.RetryAfter ?? TimeSpan.FromSeconds(Math.Pow(2, authorization.AttemptNumber) * 30))
                : null;
        int recipientUpdated = await ExecuteAsync(connection, transaction, """
UPDATE CampaignRecipients SET State=$state, ClaimToken=NULL, ClaimExpiresUtc=NULL,
 NextAttemptUtc=$next, LastOutcomeCode=$code, ProviderMessageId=$messageId,
 AcceptedUtc=$accepted, DeliveryConfirmed=$delivered
WHERE CampaignId=$campaign AND ContactKey=$contact
 AND (State='Sending' OR State='Unknown' AND LastOutcomeCode='OutcomeUnknownAfterLeaseExpiration')
 AND EXISTS(SELECT 1 FROM CampaignAttempts a WHERE a.Id=$attempt AND a.RecipientId=CampaignRecipients.Id
 AND (a.State='Sending' OR a.State='Unknown' AND a.OutcomeCode='OutcomeUnknownAfterLeaseExpiration'));
""", cancellationToken, ("$state", state), ("$next", nextAttempt is null ? DBNull.Value : Stamp(nextAttempt.Value)),
            ("$code", SafeCode(result.OutcomeCode)), ("$messageId", result.ProviderMessageId ?? (object)DBNull.Value),
            ("$accepted", result.Disposition == CampaignSendDisposition.Accepted ? Stamp(completedAtUtc) : DBNull.Value),
            ("$delivered", DBNull.Value), ("$campaign", authorization.Manifest.CampaignId.ToString("D")),
            ("$contact", authorization.Recipient.ContactKey), ("$attempt", authorization.AttemptId));
        int attemptUpdated = await ExecuteAsync(connection, transaction, """
UPDATE CampaignAttempts SET State=$state, CompletedUtc=$now, OutcomeCode=$code, ProviderMessageId=$messageId
WHERE Id=$attempt AND (State='Sending' OR State='Unknown' AND OutcomeCode='OutcomeUnknownAfterLeaseExpiration');
""", cancellationToken, ("$state", state), ("$code", SafeCode(result.OutcomeCode)), ("$messageId", result.ProviderMessageId ?? (object)DBNull.Value),
            ("$now", Stamp(completedAtUtc)), ("$attempt", authorization.AttemptId));
        if (recipientUpdated != 1 || attemptUpdated != 1)
            throw new InvalidOperationException("Campaign attempt outcome could not be durably recorded.");
        await ExecuteAsync(connection, transaction, """
UPDATE CampaignEndpointReservations SET LastAttemptUtc=$next
WHERE OwnerSid=$owner AND Channel=$channel AND NormalizedEndpoint=$endpoint AND CampaignId=$campaign;
""", cancellationToken, ("$next", Stamp(completedAtUtc)), ("$owner", ownerSid),
            ("$channel", authorization.Manifest.Draft.Channel.ToString()), ("$endpoint", authorization.Recipient.NormalizedEndpoint),
            ("$campaign", authorization.Manifest.CampaignId.ToString("D")));
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<DateTimeOffset?> GetNextDispatchUtcAsync(Guid campaignId, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenCrmAsync(cancellationToken);
        return await ReadCampaignSpacingAsync(connection, null, campaignId, cancellationToken);
    }

    public async Task<bool> HasOutstandingSendsAsync(Guid campaignId, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenCrmAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM CampaignRecipients WHERE CampaignId=$campaign AND State IN ('Claimed','Sending'));";
        command.Parameters.AddWithValue("$campaign", campaignId.ToString("D"));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
    }

    public async Task SkipClaimAsync(Guid campaignId, string contactKey, string claimToken, string outcomeCode, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenCrmAsync(cancellationToken);
        int changed = await ExecuteAsync(connection, null, """
UPDATE CampaignRecipients SET State='Skipped', ClaimToken=NULL, ClaimExpiresUtc=NULL, LastOutcomeCode=$code
WHERE CampaignId=$campaign AND ContactKey=$contact AND State='Claimed' AND ClaimToken=$token
 AND EXISTS(SELECT 1 FROM Campaigns WHERE Id=$campaign AND OwnerSid=$owner);
""", cancellationToken, ("$code", SafeCode(outcomeCode)), ("$campaign", campaignId.ToString("D")),
            ("$contact", contactKey), ("$token", claimToken), ("$owner", ownerSid));
        if (changed != 1)
            throw new InvalidOperationException("Claim could not be safely released as skipped.");
    }

    public async Task CloseExpiredCampaignAsync(Guid campaignId, long expectedRevision, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenCrmAsync(cancellationToken);
        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        CampaignSnapshot? current = await ReadSnapshotAsync(connection, transaction, campaignId, cancellationToken);
        if (current is null || current.Revision != expectedRevision || current.State is not (CampaignState.Active or CampaignState.Scheduled))
        {
            await transaction.RollbackAsync(cancellationToken);
            return;
        }
        await ExecuteAsync(connection, transaction, """
UPDATE CampaignRecipients SET State='Skipped', ClaimToken=NULL, ClaimExpiresUtc=NULL, LastOutcomeCode='ScheduleEnded'
WHERE CampaignId=$campaign AND State IN ('Pending','Rejected');
""", cancellationToken, ("$campaign", campaignId.ToString("D")));
        await ExecuteAsync(connection, transaction, """
UPDATE Campaigns SET State='Completed', Revision=Revision+1, UpdatedUtc=$now
WHERE Id=$campaign AND OwnerSid=$owner AND Revision=$revision;
""", cancellationToken, ("$now", Stamp(UtcNow)), ("$campaign", campaignId.ToString("D")),
            ("$owner", ownerSid), ("$revision", expectedRevision));
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task MarkCampaignFaultedAsync(Guid campaignId, long expectedRevision, string errorCode, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenCrmAsync(cancellationToken);
        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        CampaignSnapshot? current = await ReadSnapshotAsync(connection, transaction, campaignId, cancellationToken);
        if (current is null || current.Revision != expectedRevision || current.State is not (CampaignState.Active or CampaignState.Scheduled))
        {
            await transaction.RollbackAsync(cancellationToken);
            return;
        }
        await ExecuteAsync(connection, transaction, """
UPDATE CampaignRecipients SET State='Skipped', ClaimToken=NULL, ClaimExpiresUtc=NULL, LastOutcomeCode=$code
WHERE CampaignId=$campaign AND State IN ('Pending','Claimed','Rejected');
UPDATE Campaigns SET State='Faulted', Revision=Revision+1, LastErrorCode=$code, UpdatedUtc=$now
WHERE Id=$campaign AND OwnerSid=$owner AND Revision=$revision;
""", cancellationToken, ("$code", SafeCode(errorCode)), ("$campaign", campaignId.ToString("D")),
            ("$now", Stamp(UtcNow)), ("$owner", ownerSid), ("$revision", expectedRevision));
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<bool> IsCurrentConsentValidAsync(CampaignRecipient recipient, CampaignChannel channel, CancellationToken cancellationToken)
    {
        AudienceCandidate? current = (await ReadCandidatesAsync(recipient.Population, cancellationToken))
            .SingleOrDefault(item => item.Id == recipient.ContactId);
        if (current is null || !current.IsActive ||
            !string.Equals(NormalizeEndpoint(channel, current.EndpointFor(channel)), recipient.NormalizedEndpoint, StringComparison.OrdinalIgnoreCase))
            return false;
        ConsentGroup group = await ReadConsentGroupAsync(recipient.Population, channel, recipient.NormalizedEndpoint, cancellationToken);
        return GetConsentDecision(current, channel, recipient.NormalizedEndpoint, group) == ConsentDecision.Granted;
    }

    private async Task<bool> IsStillEligibleAsync(SqliteConnection connection, SqliteTransaction transaction, CampaignRecipient recipient, CampaignChannel channel, CancellationToken cancellationToken)
    {
        AudienceCandidate? current = await ReadCandidateAsync(connection, transaction, recipient.Population, recipient.ContactId, cancellationToken);
        if (current is null || !current.IsActive ||
            !string.Equals(NormalizeEndpoint(channel, current.EndpointFor(channel)), recipient.NormalizedEndpoint, StringComparison.OrdinalIgnoreCase))
            return false;
        ConsentGroup group = await ReadConsentGroupAsync(connection, transaction, recipient.Population, channel, recipient.NormalizedEndpoint, cancellationToken);
        return GetConsentDecision(current, channel, recipient.NormalizedEndpoint, group) == ConsentDecision.Granted;
    }

    private async Task<ConsentGroup> ReadConsentGroupAsync(CampaignPopulation population, CampaignChannel channel, string normalizedEndpoint, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenCrmAsync(cancellationToken);
        return await ReadConsentGroupAsync(connection, null, population, channel, normalizedEndpoint, cancellationToken);
    }

    private async Task<Dictionary<string, List<string>>> ReadCrmCooldownsAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenCrmAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
SELECT company.Phone, cooldown.LastAttemptAtUtc
FROM CrmWhatsAppSendCooldowns cooldown
JOIN CrmCompanies company ON company.Id=cooldown.CrmCompanyId;
""";
        Dictionary<string, List<string>> results = new(StringComparer.OrdinalIgnoreCase);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.IsDBNull(0))
                continue;
            string endpoint = NormalizePhone(reader.GetString(0));
            if (endpoint.Length == 0)
                continue;
            if (!results.TryGetValue(endpoint, out List<string>? values))
                results[endpoint] = values = [];
            values.Add(reader.GetString(1));
        }
        return results;
    }

    private async Task<CrmCooldownState> ReadCrmCooldownStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string normalizedEndpoint,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
SELECT company.Id, company.Phone, cooldown.LastAttemptAtUtc
FROM CrmCompanies company
LEFT JOIN CrmWhatsAppSendCooldowns cooldown ON cooldown.CrmCompanyId=company.Id;
""";
        List<int> matchingContactIds = [];
        DateTimeOffset? latest = null;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.IsDBNull(1) || NormalizePhone(reader.GetString(1)) != normalizedEndpoint)
                continue;
            matchingContactIds.Add(reader.GetInt32(0));
            if (reader.IsDBNull(2))
                continue;
            if (!DateTimeOffset.TryParse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset attemptedAt))
                throw new InvalidDataException("CRM WhatsApp cooldown contains an invalid timestamp; dispatch is blocked.");
            DateTimeOffset normalizedAttempt = attemptedAt.ToUniversalTime();
            if (latest is null || normalizedAttempt > latest)
                latest = normalizedAttempt;
        }
        return new CrmCooldownState(matchingContactIds, latest);
    }

    private async Task<ConsentGroup> ReadConsentGroupAsync(SqliteConnection connection, SqliteTransaction? transaction, CampaignPopulation population, CampaignChannel channel, string normalizedEndpoint, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
SELECT ContactId, Granted, IsWithdrawal FROM CampaignConsents
WHERE OwnerSid=$owner AND Population=$population AND Channel=$channel AND NormalizedEndpoint=$endpoint
ORDER BY Id;
""";
        command.Parameters.AddWithValue("$owner", ownerSid);
        command.Parameters.AddWithValue("$population", population.ToString());
        command.Parameters.AddWithValue("$channel", channel.ToString());
        command.Parameters.AddWithValue("$endpoint", normalizedEndpoint);
        Dictionary<int, bool> latestByContact = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            latestByContact[reader.GetInt32(0)] = reader.GetInt32(1) == 1 && reader.GetInt32(2) == 0;
        await reader.DisposeAsync();
        bool crmWithdrawal = false;
        if (channel == CampaignChannel.WhatsApp)
        {
            // CRM card withdrawals are shared endpoint suppression, even for an ERP audience
            // or a duplicate contact that was not selected for this campaign.
            command.Parameters.Clear();
            command.CommandText = "SELECT Phone FROM CrmCompanies WHERE WhatsAppOptOutAtUtc IS NOT NULL;";
            await using SqliteDataReader withdrawals = await command.ExecuteReaderAsync(cancellationToken);
            while (await withdrawals.ReadAsync(cancellationToken))
            {
                if (!withdrawals.IsDBNull(0) && NormalizeEndpoint(channel, withdrawals.GetString(0)) == normalizedEndpoint)
                    crmWithdrawal = true;
            }
        }
        return new ConsentGroup(latestByContact, crmWithdrawal || latestByContact.Any(item => !item.Value));
    }

    private ConsentDecision GetConsentDecision(AudienceCandidate candidate, CampaignChannel channel, string normalizedEndpoint, ConsentGroup consents)
    {
        if (candidate.Population == CampaignPopulation.CrmCompanies &&
            channel == CampaignChannel.WhatsApp && candidate.WhatsAppOptOutAtUtc is not null)
            return ConsentDecision.OptedOut;
        if (consents.HasConflictingWithdrawal)
            return ConsentDecision.OptedOut;
        if (consents.LatestByContact.TryGetValue(candidate.Id, out bool granted))
            return granted ? ConsentDecision.Granted : ConsentDecision.OptedOut;
        // Old CRM opt-in has no historical endpoint binding. Only the campaign ledger grants consent.
        return ConsentDecision.Missing;
    }

    private async Task<List<AudienceCandidate>> ReadCandidatesAsync(CampaignPopulation population, CancellationToken cancellationToken)
    {
        if (population == CampaignPopulation.CrmCompanies)
        {
            await using SqliteConnection connection = await OpenCrmAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
SELECT Id, CompanyName, Phone, Email, WhatsAppMarketingOptIn, WhatsAppOptInAtUtc,
 WhatsAppOptInEvidence, WhatsAppOptOutAtUtc
FROM CrmCompanies ORDER BY Id;
""";
            List<AudienceCandidate> results = [];
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                results.Add(ReadCrmCandidate(reader));
            return results;
        }

        await using SqliteConnection erp = await OpenErpReadOnlyAsync(cancellationToken);
        await using SqliteCommand erpCommand = erp.CreateCommand();
        erpCommand.CommandText = "SELECT Id, CompanyName, Phone, Email, IsActive FROM Customers ORDER BY Id;";
        List<AudienceCandidate> erpResults = [];
        await using SqliteDataReader erpReader = await erpCommand.ExecuteReaderAsync(cancellationToken);
        while (await erpReader.ReadAsync(cancellationToken))
            erpResults.Add(new AudienceCandidate(CampaignPopulation.ErpCustomers, erpReader.GetInt32(0),
                erpReader.GetString(1), erpReader.IsDBNull(2) ? string.Empty : erpReader.GetString(2),
                erpReader.IsDBNull(3) ? string.Empty : erpReader.GetString(3), erpReader.GetInt32(4) == 1,
                false, null, null, null));
        return erpResults;
    }

    private async Task<AudienceCandidate?> ReadCandidateAsync(SqliteConnection crm, SqliteTransaction transaction, CampaignPopulation population, int id, CancellationToken cancellationToken)
    {
        if (population == CampaignPopulation.CrmCompanies)
        {
            await using SqliteCommand command = crm.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
SELECT Id, CompanyName, Phone, Email, WhatsAppMarketingOptIn, WhatsAppOptInAtUtc,
 WhatsAppOptInEvidence, WhatsAppOptOutAtUtc FROM CrmCompanies WHERE Id=$id;
""";
            command.Parameters.AddWithValue("$id", id);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadCrmCandidate(reader) : null;
        }

        await using SqliteConnection erp = await OpenErpReadOnlyAsync(cancellationToken);
        await using SqliteCommand erpCommand = erp.CreateCommand();
        erpCommand.CommandText = "SELECT Id, CompanyName, Phone, Email, IsActive FROM Customers WHERE Id=$id;";
        erpCommand.Parameters.AddWithValue("$id", id);
        await using SqliteDataReader erpReader = await erpCommand.ExecuteReaderAsync(cancellationToken);
        return await erpReader.ReadAsync(cancellationToken)
            ? new AudienceCandidate(CampaignPopulation.ErpCustomers, erpReader.GetInt32(0), erpReader.GetString(1),
                erpReader.IsDBNull(2) ? string.Empty : erpReader.GetString(2), erpReader.IsDBNull(3) ? string.Empty : erpReader.GetString(3),
                erpReader.GetInt32(4) == 1, false, null, null, null)
            : null;
    }

    private async Task<Dictionary<int, string>> ReadAudienceSegmentsAsync(
        CampaignPopulation population,
        CancellationToken cancellationToken)
    {
        Dictionary<int, string> segments = [];
        try
        {
            await using SqliteConnection connection = population == CampaignPopulation.CrmCompanies
                ? await OpenCrmAsync(cancellationToken)
                : await OpenErpReadOnlyAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = population == CampaignPopulation.CrmCompanies
                ? "SELECT Id, COALESCE(City,'') || ' ' || COALESCE(Sector,'') || ' ' || COALESCE(Status,'') FROM CrmCompanies;"
                : "SELECT Id, COALESCE(City,'') FROM Customers;";
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                segments[reader.GetInt32(0)] = reader.GetString(1);
        }
        catch (SqliteException)
        {
            // Older schemas without city/sector/status columns fall back to name, email and phone search.
            segments.Clear();
        }
        return segments;
    }

    private static AudienceCandidate ReadCrmCandidate(SqliteDataReader reader) =>
        new(CampaignPopulation.CrmCompanies, reader.GetInt32(0), reader.GetString(1),
            reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
            reader.IsDBNull(3) ? string.Empty : reader.GetString(3), true,
            !reader.IsDBNull(4) && reader.GetInt32(4) == 1,
            reader.IsDBNull(5) ? null : ParseConsentDate(reader.GetString(5)),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : ParseConsentDate(reader.GetString(7)));

    private async Task<SqliteConnection> OpenCrmAsync(CancellationToken cancellationToken)
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = crmDatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 30,
            ForeignKeys = true
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private async Task<SqliteConnection> OpenErpReadOnlyAsync(CancellationToken cancellationToken)
    {
        if (erpDatabasePath is null || !File.Exists(erpDatabasePath))
            throw new FileNotFoundException("The selected ERP customer database is missing; it was not created.", erpDatabasePath);
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = erpDatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            DefaultTimeout = 10
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private async Task<CampaignDraft?> ReadDraftAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid id, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Revision, State, DraftJson, UpdatedUtc FROM Campaigns WHERE Id=$id AND OwnerSid=$owner;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$owner", ownerSid);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new CampaignDraft(id, reader.GetInt64(0), Enum.Parse<CampaignState>(reader.GetString(1)),
                Deserialize<CampaignDraftInput>(reader.GetString(2)), ParseStamp(reader.GetString(3)))
            : null;
    }

    private async Task<CampaignSnapshot?> ReadSnapshotAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid id, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
SELECT Revision, State, DraftJson, ManifestHash, UpdatedUtc, LastErrorCode
FROM Campaigns WHERE Id=$id AND OwnerSid=$owner;
""";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$owner", ownerSid);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new CampaignSnapshot(id, reader.GetInt64(0), Enum.Parse<CampaignState>(reader.GetString(1)), ownerSid,
                Deserialize<CampaignDraftInput>(reader.GetString(2)), reader.IsDBNull(3) ? null : reader.GetString(3),
                ParseStamp(reader.GetString(4)), reader.IsDBNull(5) ? null : reader.GetString(5))
            : null;
    }

    private static async Task<CampaignApprovalManifest> ReadManifestAsync(SqliteConnection connection, SqliteTransaction transaction, Guid id, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT SnapshotJson FROM Campaigns WHERE Id=$id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        object? value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string json ? Deserialize<CampaignApprovalManifest>(json)
            : throw new InvalidDataException("Approved campaign snapshot is missing.");
    }

    private async Task<DateTimeOffset?> ReadEndpointReservationAsync(SqliteConnection connection, SqliteTransaction transaction, CampaignRecipient recipient, CampaignChannel channel, TimeSpan minimumSpacing, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT LastAttemptUtc FROM CampaignEndpointReservations WHERE OwnerSid=$owner AND Channel=$channel AND NormalizedEndpoint=$endpoint;";
        command.Parameters.AddWithValue("$owner", ownerSid);
        command.Parameters.AddWithValue("$channel", channel.ToString());
        command.Parameters.AddWithValue("$endpoint", recipient.NormalizedEndpoint);
        object? value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string stamp ? ParseStamp(stamp).Add(minimumSpacing) : null;
    }

    private async Task<DateTimeOffset?> ReadCampaignSpacingAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid id, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
SELECT MAX(COALESCE(a.CompletedUtc,a.AuthorizedUtc)) FROM CampaignAttempts a
JOIN Campaigns c ON c.Id=a.CampaignId WHERE c.OwnerSid=$owner AND a.CampaignId=$campaign;
""";
        command.Parameters.AddWithValue("$owner", ownerSid);
        command.Parameters.AddWithValue("$campaign", id.ToString("D"));
        object? value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is not string stamp)
            return null;
        DateTimeOffset last = ParseStamp(stamp);
        CampaignSnapshot? snapshot = await ReadSnapshotAsync(connection, transaction, id, cancellationToken);
        return snapshot is null ? null : last.Add(snapshot.Input.MinimumSpacing);
    }

    private async Task<int> ExecuteAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string NormalizeEndpoint(CampaignChannel channel, string endpoint)
    {
        if (channel == CampaignChannel.Email)
            return NormalizeEmail(endpoint);
        try { return NormalizePhone(endpoint); }
        catch (ArgumentException) { return string.Empty; }
    }

    private static string NormalizeEmail(string endpoint)
    {
        try
        {
            MailAddress address = new(endpoint.Trim());
            if (!string.Equals(address.Address, endpoint.Trim(), StringComparison.OrdinalIgnoreCase))
                return string.Empty;
            return address.Address.ToLowerInvariant();
        }
        catch (FormatException)
        {
            return string.Empty;
        }
    }

    private static string NormalizePhone(string endpoint)
    {
        string normalized = new(endpoint.Where(character => character is not (' ' or '-' or '(' or ')' or '.')).ToArray());
        if (normalized.StartsWith('+'))
            normalized = normalized[1..];
        return normalized.Length is >= 8 and <= 15 && normalized.All(char.IsAsciiDigit) && normalized[0] != '0'
            ? normalized
            : string.Empty;
    }

    private static DateTimeOffset? ParseConsentDate(string value)
    {
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed))
            return null;
        return parsed.ToUniversalTime();
    }

    private static string Stamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseStamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime();
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
    private static T Deserialize<T>(string value) => JsonSerializer.Deserialize<T>(value, JsonOptions)
        ?? throw new InvalidDataException($"Campaign database contains an invalid {typeof(T).Name} record.");
    private static string SafeCode(string code) => new(code.Where(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-').Take(80).ToArray());
    private static void AddExclusion(Dictionary<CampaignExclusionReason, int> excluded, CampaignExclusionReason reason)
    {
        excluded[reason] = excluded.GetValueOrDefault(reason) + 1;
    }

    private sealed record AudienceCandidate(
        CampaignPopulation Population,
        int Id,
        string DisplayName,
        string Phone,
        string Email,
        bool IsActive,
        bool WhatsAppOptIn,
        DateTimeOffset? WhatsAppOptInAtUtc,
        string? WhatsAppOptInEvidence,
        DateTimeOffset? WhatsAppOptOutAtUtc)
    {
        public string EndpointFor(CampaignChannel channel) => channel == CampaignChannel.Email ? Email : Phone;
    }

    private sealed record ConsentGroup(Dictionary<int, bool> LatestByContact, bool HasConflictingWithdrawal);
    private sealed record CrmCooldownState(IReadOnlyList<int> ContactIds, DateTimeOffset? LatestAttemptUtc);
    private enum ConsentDecision { Granted, Missing, OptedOut, Conflicting }
}

public sealed record CampaignSendAuthorization(string AttemptId, CampaignApprovalManifest Manifest, CampaignRecipient Recipient, int AttemptNumber);
