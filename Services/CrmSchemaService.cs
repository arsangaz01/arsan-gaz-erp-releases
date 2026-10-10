using System.IO;
using System.Security.Principal;
using ArsanGazERP.Data;
using ArsanGazERP.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;

namespace ArsanGazERP.Services;

public sealed class CrmSchemaService
{
    private const string SchemaSql = """
CREATE TABLE IF NOT EXISTS CrmCompanies (
 Id INTEGER NOT NULL CONSTRAINT PK_CrmCompanies PRIMARY KEY AUTOINCREMENT,
 SourceProspectId INTEGER NULL,
 CompanyName TEXT NOT NULL,
 City TEXT NULL, Sector TEXT NULL, Phone TEXT NULL, Email TEXT NULL,
 Website TEXT NULL, Address TEXT NULL, Score INTEGER NOT NULL DEFAULT 0,
 Status TEXT NOT NULL DEFAULT 'Yeni', AssignedTo TEXT NULL,
 ExpectedValue TEXT NOT NULL DEFAULT '0', FollowUpAtUtc TEXT NULL,
 WhatsAppMarketingOptIn INTEGER NOT NULL DEFAULT 0,
 WhatsAppOptInAtUtc TEXT NULL, WhatsAppOptInEvidence TEXT NULL,
 WhatsAppOptOutAtUtc TEXT NULL,
 CreatedAtUtc TEXT NOT NULL, LastContactAtUtc TEXT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS IX_CrmCompanies_SourceProspectId ON CrmCompanies(SourceProspectId) WHERE SourceProspectId IS NOT NULL;
CREATE INDEX IF NOT EXISTS IX_CrmCompanies_Status_Score ON CrmCompanies(Status, Score);
CREATE TABLE IF NOT EXISTS CrmActivities (
 Id INTEGER NOT NULL CONSTRAINT PK_CrmActivities PRIMARY KEY AUTOINCREMENT,
 CrmCompanyId INTEGER NOT NULL, ActivityType TEXT NOT NULL,
 Description TEXT NOT NULL, ActivityAtUtc TEXT NOT NULL, UserName TEXT NOT NULL,
 CONSTRAINT FK_CrmActivities_CrmCompanies_CrmCompanyId FOREIGN KEY(CrmCompanyId) REFERENCES CrmCompanies(Id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS IX_CrmActivities_CrmCompanyId_ActivityAtUtc ON CrmActivities(CrmCompanyId, ActivityAtUtc);
CREATE TABLE IF NOT EXISTS CrmNotes (
 Id INTEGER NOT NULL CONSTRAINT PK_CrmNotes PRIMARY KEY AUTOINCREMENT,
 CrmCompanyId INTEGER NOT NULL, NoteText TEXT NOT NULL, CreatedAtUtc TEXT NOT NULL,
 CONSTRAINT FK_CrmNotes_CrmCompanies_CrmCompanyId FOREIGN KEY(CrmCompanyId) REFERENCES CrmCompanies(Id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS IX_CrmNotes_CrmCompanyId_CreatedAtUtc ON CrmNotes(CrmCompanyId, CreatedAtUtc);
CREATE TABLE IF NOT EXISTS CrmWhatsAppSendCooldowns (
 CrmCompanyId INTEGER NOT NULL CONSTRAINT PK_CrmWhatsAppSendCooldowns PRIMARY KEY,
 LastAttemptAtUtc TEXT NOT NULL,
 CONSTRAINT FK_CrmWhatsAppSendCooldowns_CrmCompanies_CrmCompanyId FOREIGN KEY(CrmCompanyId) REFERENCES CrmCompanies(Id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS CrmImportMarkers (
 UserSid TEXT NOT NULL CONSTRAINT PK_CrmImportMarkers PRIMARY KEY,
 ImportedAtUtc TEXT NOT NULL
);
""";

    public async Task EnsureAsync()
    {
        await using (ArsanGazDbContext legacyDb = new())
        {
            await legacyDb.Database.ExecuteSqlRawAsync(SchemaSql);
            await EnsureCrmColumnsAsync(legacyDb);
            await EnsureCrmIndexesAsync(legacyDb);
        }

        string sharedDirectory = Path.GetDirectoryName(CrmDbContext.SharedDatabasePath)!;
        try
        {
            Directory.CreateDirectory(sharedDirectory);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new InvalidOperationException(
                "Ortak CRM klasörüne erişilemiyor. Arsan Gaz ERP'yi yönetici yetkisiyle kurup yeniden deneyin.",
                exception);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException("Ortak CRM klasörü oluşturulamadı.", exception);
        }

        await using CrmDbContext sharedDb = new();
        await sharedDb.Database.OpenConnectionAsync();
        await sharedDb.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
        await sharedDb.Database.ExecuteSqlRawAsync(SchemaSql);
        await EnsureCrmColumnsAsync(sharedDb);
        await EnsureCrmIndexesAsync(sharedDb);

        string userSid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("Windows kullanıcı kimliği alınamadı; CRM verisi içe aktarılamadı.");

        await using var transaction = await sharedDb.Database.BeginTransactionAsync();
        bool alreadyImported = await sharedDb.CrmImportMarkers.AnyAsync(marker => marker.UserSid == userSid);
        if (!alreadyImported)
        {
            await ImportCurrentUserDataAsync(sharedDb);
            sharedDb.CrmImportMarkers.Add(new CrmImportMarker { UserSid = userSid });
            await sharedDb.SaveChangesAsync();
        }

        await transaction.CommitAsync();
    }

    private static Task<int> EnsureCrmIndexesAsync(DbContext db) =>
        db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_CrmCompanies_FollowUpAtUtc ON CrmCompanies(FollowUpAtUtc);");

    private static async Task EnsureCrmColumnsAsync(DbContext db)
    {
        SqliteConnection connection = (SqliteConnection)db.Database.GetDbConnection();
        bool openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync();
        }

        try
        {
            HashSet<string> columns = new(StringComparer.OrdinalIgnoreCase);
            await using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA table_info('CrmCompanies');";
                await using SqliteDataReader reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    columns.Add(reader.GetString(1));
                }
            }

            if (!columns.Contains(nameof(CrmCompany.ExpectedValue)))
            {
                await db.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE CrmCompanies ADD COLUMN ExpectedValue TEXT NOT NULL DEFAULT '0';");
            }

            if (!columns.Contains(nameof(CrmCompany.FollowUpAtUtc)))
            {
                await db.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE CrmCompanies ADD COLUMN FollowUpAtUtc TEXT NULL;");
            }

            if (!columns.Contains(nameof(CrmCompany.WhatsAppMarketingOptIn)))
            {
                await db.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE CrmCompanies ADD COLUMN WhatsAppMarketingOptIn INTEGER NOT NULL DEFAULT 0;");
            }

            if (!columns.Contains(nameof(CrmCompany.WhatsAppOptInAtUtc)))
            {
                await db.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE CrmCompanies ADD COLUMN WhatsAppOptInAtUtc TEXT NULL;");
            }

            if (!columns.Contains(nameof(CrmCompany.WhatsAppOptInEvidence)))
            {
                await db.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE CrmCompanies ADD COLUMN WhatsAppOptInEvidence TEXT NULL;");
            }

            if (!columns.Contains(nameof(CrmCompany.WhatsAppOptOutAtUtc)))
            {
                await db.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE CrmCompanies ADD COLUMN WhatsAppOptOutAtUtc TEXT NULL;");
            }
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static async Task ImportCurrentUserDataAsync(CrmDbContext sharedDb)
    {
        await using ArsanGazDbContext legacyDb = new();
        List<CrmCompany> legacyCompanies = await legacyDb.CrmCompanies
            .AsNoTracking()
            .Include(company => company.Activities)
            .Include(company => company.Notes)
            .ToListAsync();

        foreach (CrmCompany legacyCompany in legacyCompanies)
        {
            CrmCompany? sharedCompany = legacyCompany.SourceProspectId is int prospectId
                ? await sharedDb.CrmCompanies.FirstOrDefaultAsync(company => company.SourceProspectId == prospectId)
                : null;

            if (sharedCompany is null)
            {
                sharedCompany = new CrmCompany
                {
                    SourceProspectId = legacyCompany.SourceProspectId,
                    CompanyName = legacyCompany.CompanyName,
                    City = legacyCompany.City,
                    Sector = legacyCompany.Sector,
                    Phone = legacyCompany.Phone,
                    Email = legacyCompany.Email,
                    Website = legacyCompany.Website,
                    Address = legacyCompany.Address,
                    Score = legacyCompany.Score,
                    Status = legacyCompany.Status,
                    AssignedTo = legacyCompany.AssignedTo,
                    ExpectedValue = legacyCompany.ExpectedValue,
                    FollowUpAtUtc = legacyCompany.FollowUpAtUtc,
                    WhatsAppMarketingOptIn = legacyCompany.WhatsAppMarketingOptIn,
                    WhatsAppOptInAtUtc = legacyCompany.WhatsAppOptInAtUtc,
                    WhatsAppOptInEvidence = legacyCompany.WhatsAppOptInEvidence,
                    WhatsAppOptOutAtUtc = legacyCompany.WhatsAppOptOutAtUtc,
                    CreatedAtUtc = legacyCompany.CreatedAtUtc,
                    LastContactAtUtc = legacyCompany.LastContactAtUtc
                };
                sharedDb.CrmCompanies.Add(sharedCompany);
                await sharedDb.SaveChangesAsync();
            }

            foreach (CrmActivity activity in legacyCompany.Activities)
            {
                sharedDb.CrmActivities.Add(new CrmActivity
                {
                    CrmCompanyId = sharedCompany.Id,
                    ActivityType = activity.ActivityType,
                    Description = activity.Description,
                    ActivityAtUtc = activity.ActivityAtUtc,
                    UserName = activity.UserName
                });
            }

            foreach (CrmNote note in legacyCompany.Notes)
            {
                sharedDb.CrmNotes.Add(new CrmNote
                {
                    CrmCompanyId = sharedCompany.Id,
                    NoteText = note.NoteText,
                    CreatedAtUtc = note.CreatedAtUtc
                });
            }
        }

        await sharedDb.SaveChangesAsync();
    }
}
