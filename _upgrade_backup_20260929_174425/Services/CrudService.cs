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
 public async Task<List<Customer>> CustomersAsync()=>await new ArsanGazDbContext().Customers.AsNoTracking().OrderBy(x=>x.CompanyName).ToListAsync();
 public async Task<Customer> SaveCustomerAsync(Customer customer){await using var db=new ArsanGazDbContext();if(customer.Id==0)db.Customers.Add(customer);else db.Customers.Update(customer);await db.SaveChangesAsync();return customer;}
 public async Task DeleteCustomerAsync(int id){await using var db=new ArsanGazDbContext();var e=await db.Customers.FindAsync(id);if(e!=null){db.Customers.Remove(e);await db.SaveChangesAsync();}}
 public async Task<List<Product>> ProductsAsync()=>await new ArsanGazDbContext().Products.AsNoTracking().OrderBy(x=>x.Name).ToListAsync();
 public async Task<Product> SaveProductAsync(Product product){await using var db=new ArsanGazDbContext();if(product.Id==0)db.Products.Add(product);else db.Products.Update(product);await db.SaveChangesAsync();return product;}
 public async Task DeleteProductAsync(int id){await using var db=new ArsanGazDbContext();var e=await db.Products.FindAsync(id);if(e!=null){db.Products.Remove(e);await db.SaveChangesAsync();}}
 public async Task<List<Cylinder>> CylindersAsync()=>await new ArsanGazDbContext().Cylinders.AsNoTracking().Include(x=>x.CurrentCustomer).OrderBy(x=>x.Barcode).ToListAsync();
 public async Task<Cylinder> SaveCylinderAsync(Cylinder cylinder){await using var db=new ArsanGazDbContext();if(cylinder.Id==0)db.Cylinders.Add(cylinder);else db.Cylinders.Update(cylinder);await db.SaveChangesAsync();return cylinder;}
 public async Task MoveCylinderAsync(int cylinderId,CylinderStatus target,int? customerId,string note){await using var db=new ArsanGazDbContext();var c=await db.Cylinders.FindAsync(cylinderId)??throw new InvalidOperationException("Tüp bulunamadı.");var old=c.Status;c.Status=target;c.CurrentCustomerId=target==CylinderStatus.Customer?customerId:null;c.LastMovementAt=DateTime.Now;db.CylinderMovements.Add(new CylinderMovement{CylinderId=c.Id,CustomerId=c.CurrentCustomerId,FromStatus=old,ToStatus=target,MovementType="Durum Değişikliği",Description=note,MovementDate=DateTime.Now});await db.SaveChangesAsync();}
 public async Task<List<Invoice>> OverdueInvoicesAsync()=>await new ArsanGazDbContext().Invoices.AsNoTracking().Include(x=>x.Customer).Where(x=>x.DueDate<DateTime.Today&&x.PaidAmount<x.TotalAmount).OrderBy(x=>x.DueDate).ToListAsync();
}