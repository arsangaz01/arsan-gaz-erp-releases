using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ArsanGazERP.Data;
using ArsanGazERP.Models;
using Microsoft.EntityFrameworkCore;
namespace ArsanGazERP.Services;
public sealed class CrudService
{
 public async Task<List<Customer>> CustomersAsync(){await using var db=new ArsanGazDbContext();return await db.Customers.AsNoTracking().OrderBy(x=>x.CompanyName).ToListAsync();}
 public async Task<Customer> SaveCustomerAsync(Customer customer){await using var db=new ArsanGazDbContext();if(customer.Id==0)db.Customers.Add(customer);else db.Customers.Update(customer);await db.SaveChangesAsync();return customer;}
 public async Task DeleteCustomerAsync(int id){await using var db=new ArsanGazDbContext();var e=await db.Customers.FindAsync(id);if(e!=null){db.Customers.Remove(e);await db.SaveChangesAsync();}}
 public async Task<List<Product>> ProductsAsync(){await using var db=new ArsanGazDbContext();return await db.Products.AsNoTracking().OrderBy(x=>x.Name).ToListAsync();}
 public async Task<Product> SaveProductAsync(Product product){await using var db=new ArsanGazDbContext();if(product.Id==0)db.Products.Add(product);else db.Products.Update(product);await db.SaveChangesAsync();return product;}
 public async Task DeleteProductAsync(int id){await using var db=new ArsanGazDbContext();var e=await db.Products.FindAsync(id);if(e!=null){db.Products.Remove(e);await db.SaveChangesAsync();}}
 public async Task<List<Cylinder>> CylindersAsync(){await using var db=new ArsanGazDbContext();return await db.Cylinders.AsNoTracking().Include(x=>x.CurrentCustomer).OrderBy(x=>x.Barcode).ToListAsync();}
 public async Task<Cylinder> SaveCylinderAsync(Cylinder cylinder){await using var db=new ArsanGazDbContext();if(cylinder.Id==0)db.Cylinders.Add(cylinder);else db.Cylinders.Update(cylinder);await db.SaveChangesAsync();return cylinder;}
 public async Task<Cylinder?> FindCylinderByCodeAsync(string code)
 {
  string value=code.Trim();
  if(value.Length==0)return null;
  await using var db=new ArsanGazDbContext();
  return await db.Cylinders.AsNoTracking().Include(x=>x.CurrentCustomer).FirstOrDefaultAsync(x=>x.Barcode==value||x.SerialNumber==value)
   ??(await db.Cylinders.AsNoTracking().Include(x=>x.CurrentCustomer).ToListAsync())
     .FirstOrDefault(x=>string.Equals(x.Barcode,value,StringComparison.OrdinalIgnoreCase)||string.Equals(x.SerialNumber,value,StringComparison.OrdinalIgnoreCase));
 }
 public async Task<List<ReorderForecast>> ReorderForecastsAsync()
 {
  await using var db=new ArsanGazDbContext();
  var deliveries=await db.CylinderMovements.AsNoTracking()
   .Where(x=>x.ToStatus==CylinderStatus.Customer&&x.CustomerId!=null)
   .Select(x=>new CustomerDelivery(x.CustomerId!.Value,x.Customer!.CompanyName,x.MovementDate))
   .ToListAsync();
  return ReorderPrediction.Compute(deliveries,DateTime.Today);
 }
 public async Task MoveCylinderAsync(int cylinderId,CylinderStatus target,int? customerId,string note,string movementType="Durum Değişikliği")
 {
  if(!Enum.IsDefined(target))throw new ArgumentOutOfRangeException(nameof(target),"Geçersiz tüp durumu.");
  int? movementCustomerId=null;
  await using var db=new ArsanGazDbContext();
  if(target==CylinderStatus.Customer)
  {
   int id=customerId??throw new ArgumentException("Müşteriye teslim durumunda müşteri seçilmelidir.",nameof(customerId));
   if(!await db.Customers.AnyAsync(x=>x.Id==id))throw new InvalidOperationException($"Müşteri bulunamadı: {id}.");
   movementCustomerId=id;
  }

  var c=await db.Cylinders.FindAsync(cylinderId)??throw new InvalidOperationException("Tüp bulunamadı.");
  var old=c.Status;
  int? previousCustomerId=c.CurrentCustomerId;
  c.Status=target;
  c.CurrentCustomerId=movementCustomerId;
  c.LastMovementAt=DateTime.Now;
  db.CylinderMovements.Add(new CylinderMovement{CylinderId=c.Id,CustomerId=movementCustomerId??previousCustomerId,FromStatus=old,ToStatus=target,MovementType=movementType,Description=note,MovementDate=DateTime.Now});
  await db.SaveChangesAsync();
 }
 public async Task<List<Invoice>> OverdueInvoicesAsync(){await using var db=new ArsanGazDbContext();return await db.Invoices.AsNoTracking().Include(x=>x.Customer).Where(x=>x.DueDate<DateTime.Today&&x.PaidAmount<x.TotalAmount).OrderBy(x=>x.DueDate).ToListAsync();}
 public async Task<List<ErpTask>> TasksAsync(){await using var db=new ArsanGazDbContext();return await db.Tasks.AsNoTracking().OrderBy(x=>x.DueDate).ThenBy(x=>x.Title).ToListAsync();}
}