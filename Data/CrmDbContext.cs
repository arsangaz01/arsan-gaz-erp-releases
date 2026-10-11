using System.IO;
using ArsanGazERP.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ArsanGazERP.Data;

internal sealed class CrmDbContext : DbContext
{
    // SQLite file path of the shared CRM database (unused when ArsanDatabase.IsPostgres).
    public static string SharedDatabasePath => ArsanDatabase.SqlitePath(DatabaseKind.Crm);

    public DbSet<CrmCompany> CrmCompanies => Set<CrmCompany>();
    public DbSet<CrmActivity> CrmActivities => Set<CrmActivity>();
    public DbSet<CrmNote> CrmNotes => Set<CrmNote>();
    public DbSet<CrmImportMarker> CrmImportMarkers => Set<CrmImportMarker>();

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        ArsanDbEf.Configure(options, DatabaseKind.Crm, $"Data Source={SharedDatabasePath};Foreign Keys=True;Default Timeout=30");
        options.ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning));
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CrmCompany>().HasIndex(company => company.SourceProspectId).IsUnique();
        modelBuilder.Entity<CrmCompany>().HasIndex(company => new { company.Status, company.Score });
        modelBuilder.Entity<CrmCompany>().HasIndex(company => company.FollowUpAtUtc);
        modelBuilder.Entity<CrmCompany>()
            .HasMany(company => company.Activities)
            .WithOne(activity => activity.Company)
            .HasForeignKey(activity => activity.CrmCompanyId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<CrmCompany>()
            .HasMany(company => company.Notes)
            .WithOne(note => note.Company)
            .HasForeignKey(note => note.CrmCompanyId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<CrmImportMarker>().HasKey(marker => marker.UserSid);
        ArsanDbEf.ApplyPostgresConventions(modelBuilder, DatabaseKind.Crm);
    }
}
