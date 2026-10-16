using System;
using System.IO;
using ArsanGazERP.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
namespace ArsanGazERP.Data;
public sealed class ArsanGazDbContext : DbContext
{
 public DbSet<Customer> Customers => Set<Customer>();
 public DbSet<Product> Products => Set<Product>();
 public DbSet<Cylinder> Cylinders => Set<Cylinder>();
 public DbSet<CylinderMovement> CylinderMovements => Set<CylinderMovement>();
 public DbSet<CylinderAudit> CylinderAudits => Set<CylinderAudit>();
 public DbSet<CylinderTrip> CylinderTrips => Set<CylinderTrip>();
 public DbSet<CylinderTripVehicle> CylinderTripVehicles => Set<CylinderTripVehicle>();
 public DbSet<CylinderTripCylinder> CylinderTripCylinders => Set<CylinderTripCylinder>();
 public DbSet<Vehicle> Vehicles => Set<Vehicle>();
 public DbSet<Quote> Quotes => Set<Quote>();
 public DbSet<QuoteLine> QuoteLines => Set<QuoteLine>();
 public DbSet<Invoice> Invoices => Set<Invoice>();
 public DbSet<Payment> Payments => Set<Payment>();
 public DbSet<ErpTask> Tasks => Set<ErpTask>();
 public DbSet<AgentRun> AgentRuns => Set<AgentRun>();
 public DbSet<Prospect> Prospects => Set<Prospect>();
 public DbSet<CrmCompany> CrmCompanies => Set<CrmCompany>();
 public DbSet<CrmActivity> CrmActivities => Set<CrmActivity>();
 public DbSet<CrmNote> CrmNotes => Set<CrmNote>();
 public DbSet<CollectionReminder> CollectionReminders => Set<CollectionReminder>();
 // SQLite file path; on PostgreSQL a description of the server database (no secrets).
 public string DatabasePath { get; }
 public ArsanGazDbContext()
 {
  if(ArsanDatabase.IsPostgres){DatabasePath=ArsanDatabase.Describe(DatabaseKind.Erp);return;}
  DatabasePath=ArsanDatabase.SqlitePath(DatabaseKind.Erp);
  if(Environment.GetEnvironmentVariable("ARSANGAZ_ERP_DB") is not { Length: > 0 })Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
 }
 protected override void OnConfiguring(DbContextOptionsBuilder b)
    {
        ArsanDbEf.Configure(b, DatabaseKind.Erp, $"Data Source={DatabasePath};Foreign Keys=True");
        b.ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning));
    }
 protected override void OnModelCreating(ModelBuilder b)
 {
  b.Entity<Customer>().HasIndex(x=>x.Code).IsUnique();
  b.Entity<Product>().HasIndex(x=>x.Code).IsUnique();
  b.Entity<Product>().Property(x=>x.SalesPrice).HasPrecision(18,4);
  b.Entity<Cylinder>().HasIndex(x=>x.SerialNumber).IsUnique();
  b.Entity<Cylinder>().HasIndex(x=>x.Barcode).IsUnique();
  b.Entity<Cylinder>().HasOne(x=>x.CurrentCustomer).WithMany(x=>x.Cylinders).HasForeignKey(x=>x.CurrentCustomerId).OnDelete(DeleteBehavior.SetNull);
  b.Entity<Cylinder>().HasOne(x=>x.OwnerCustomer).WithMany().HasForeignKey(x=>x.OwnerCustomerId).OnDelete(DeleteBehavior.SetNull);
  b.Entity<Cylinder>().HasIndex(x=>x.Status);
  b.Entity<CylinderAudit>().HasIndex(x=>new { x.CustomerId, x.AuditedAt });
  b.Entity<CylinderTrip>().HasIndex(x=>x.Status);
  b.Entity<Vehicle>().HasIndex(x=>x.Plate).IsUnique();
  b.Entity<CylinderTripVehicle>().HasIndex(x=>new { x.TripId, x.Plate });
  b.Entity<CylinderTripCylinder>().HasIndex(x=>new { x.TripId, x.CylinderId });
  b.Entity<CylinderTripCylinder>().HasOne(x=>x.Cylinder).WithMany().HasForeignKey(x=>x.CylinderId).OnDelete(DeleteBehavior.Restrict);
  b.Entity<CylinderTripVehicle>().HasOne(x=>x.Trip).WithMany(x=>x.Vehicles).HasForeignKey(x=>x.TripId).OnDelete(DeleteBehavior.Cascade);
  b.Entity<CylinderTripCylinder>().HasOne(x=>x.Trip).WithMany(x=>x.Cylinders).HasForeignKey(x=>x.TripId).OnDelete(DeleteBehavior.Cascade);
  b.Entity<CylinderMovement>().HasOne(x=>x.Cylinder).WithMany(x=>x.Movements).HasForeignKey(x=>x.CylinderId).OnDelete(DeleteBehavior.Restrict);
  b.Entity<Quote>().HasIndex(x=>x.QuoteNumber).IsUnique();
  b.Entity<Invoice>().HasIndex(x=>x.InvoiceNumber).IsUnique();
    b.Entity<AgentRun>().HasIndex(x=>x.StartedAtUtc);
  b.Entity<Prospect>().HasIndex(x=>x.ProviderPlaceId).IsUnique();
  b.Entity<Prospect>().HasIndex(x=>new { x.City, x.OpportunityScore });
  b.Entity<CrmCompany>().HasIndex(x=>x.SourceProspectId).IsUnique();
  b.Entity<CrmCompany>().HasIndex(x=>new { x.Status, x.Score });
  b.Entity<CrmCompany>().HasIndex(x=>x.FollowUpAtUtc);
  b.Entity<CrmCompany>().HasMany(x=>x.Activities).WithOne(x=>x.Company).HasForeignKey(x=>x.CrmCompanyId).OnDelete(DeleteBehavior.Cascade);
  b.Entity<CrmCompany>().HasMany(x=>x.Notes).WithOne(x=>x.Company).HasForeignKey(x=>x.CrmCompanyId).OnDelete(DeleteBehavior.Cascade);
  b.Entity<Payment>().HasOne(x=>x.Invoice).WithMany(x=>x.Payments).HasForeignKey(x=>x.InvoiceId).OnDelete(DeleteBehavior.Restrict);
  ArsanDbEf.ApplyPostgresConventions(b, DatabaseKind.Erp);
 }
}
