using System.Threading.Tasks;
using ArsanGazERP.Data;
using Microsoft.EntityFrameworkCore;

namespace ArsanGazERP.Services;

public sealed class CrmSchemaService
{
    public async Task EnsureAsync()
    {
        await using ArsanGazDbContext db = new();
        await db.Database.ExecuteSqlRawAsync("""
CREATE TABLE IF NOT EXISTS CrmCompanies (
 Id INTEGER NOT NULL CONSTRAINT PK_CrmCompanies PRIMARY KEY AUTOINCREMENT,
 SourceProspectId INTEGER NULL,
 CompanyName TEXT NOT NULL,
 City TEXT NULL, Sector TEXT NULL, Phone TEXT NULL, Email TEXT NULL,
 Website TEXT NULL, Address TEXT NULL, Score INTEGER NOT NULL DEFAULT 0,
 Status TEXT NOT NULL DEFAULT 'Yeni', AssignedTo TEXT NULL,
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
""");
    }
}
