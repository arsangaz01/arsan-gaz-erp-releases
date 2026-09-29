using System;
using System.IO;
using ArsanGazERP.Models;
using Microsoft.EntityFrameworkCore;
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
 public string DatabasePath { get; }
 public ArsanGazDbContext()
 {
  string d=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ArsanGazERP","Data");
  Directory.CreateDirectory(d); DatabasePath=Path.Combine(d,"arsangaz-erp-v3.db");
 }
 protected override void OnConfiguring(DbContextOptionsBuilder b)=>b.UseSqlite($"Data Source={DatabasePath};Foreign Keys=True");
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
  b.Entity<Payment>().HasOne(x=>x.Invoice).WithMany(x=>x.Payments).HasForeignKey(x=>x.InvoiceId).OnDelete(DeleteBehavior.Restrict);
 }
}