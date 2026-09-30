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
 public string DatabasePath { get; }
 public ArsanGazDbContext()
 {
  string d=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ArsanGazERP","Data");
  Directory.CreateDirectory(d); DatabasePath=Path.Combine(d,"arsangaz-erp-v3.db");
 }
 protected override void OnConfiguring(DbContextOptionsBuilder b)
    {
        b
            .UseSqlite($"Data Source={DatabasePath};Foreign Keys=True")
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning));
    }
 protected override void OnModelCreating(ModelBuilder b)
 {
  b.Entity<Customer>().HasIndex(x=>x.Code).IsUnique();
  b.Entity<Product>().HasIndex(x=>x.Code).IsUnique();
  b.Entity<Product>().Property(x=>x.SalesPrice).HasPrecision(18,4);
  b.Entity<Cylinder>().HasIndex(x=>x.SerialNumber).IsUnique();
  b.Entity<Cylinder>().HasIndex(x=>x.Barcode).IsUnique();
  b.Entity<Cylinder>().HasOne(x=>x.CurrentCustomer).WithMany(x=>x.Cylinders).HasForeignKey(x=>x.CurrentCustomerId).OnDelete(DeleteBehavior.SetNull);
  b.Entity<CylinderMovement>().HasOne(x=>x.Cylinder).WithMany(x=>x.Movements).HasForeignKey(x=>x.CylinderId).OnDelete(DeleteBehavior.Restrict);
  b.Entity<Quote>().HasIndex(x=>x.QuoteNumber).IsUnique();
  b.Entity<Invoice>().HasIndex(x=>x.InvoiceNumber).IsUnique();
    b.Entity<AgentRun>().HasIndex(x=>x.StartedAtUtc);
  b.Entity<Prospect>().HasIndex(x=>x.ProviderPlaceId).IsUnique();
  b.Entity<Prospect>().HasIndex(x=>new { x.City, x.OpportunityScore });
  b.Entity<CrmCompany>().HasIndex(x=>x.SourceProspectId).IsUnique();
  b.Entity<CrmCompany>().HasIndex(x=>new { x.Status, x.Score });
  b.Entity<CrmCompany>().HasMany(x=>x.Activities).WithOne(x=>x.Company).HasForeignKey(x=>x.CrmCompanyId).OnDelete(DeleteBehavior.Cascade);
  b.Entity<CrmCompany>().HasMany(x=>x.Notes).WithOne(x=>x.Company).HasForeignKey(x=>x.CrmCompanyId).OnDelete(DeleteBehavior.Cascade);
  b.Entity<Payment>().HasOne(x=>x.Invoice).WithMany(x=>x.Payments).HasForeignKey(x=>x.InvoiceId).OnDelete(DeleteBehavior.Restrict);
 }
}