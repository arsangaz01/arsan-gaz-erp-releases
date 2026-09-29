using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using ArsanGazERP.Data;
using ArsanGazERP.Models;
using Microsoft.EntityFrameworkCore;
namespace ArsanGazERP.Views;
public partial class DatabaseManagementWindow:Window
{
 public DatabaseManagementWindow(){InitializeComponent();Loaded+=async(_,_)=>await LoadAsync();}
 private async Task LoadAsync(){await using ArsanGazDbContext db=new(); CustomersGrid.ItemsSource=await db.Customers.AsNoTracking().ToListAsync(); ProductsGrid.ItemsSource=await db.Products.AsNoTracking().ToListAsync(); CylindersGrid.ItemsSource=await db.Cylinders.AsNoTracking().ToListAsync(); QuotesGrid.ItemsSource=await db.Quotes.AsNoTracking().ToListAsync(); InvoicesGrid.ItemsSource=await db.Invoices.AsNoTracking().ToListAsync(); TasksGrid.ItemsSource=await db.Tasks.AsNoTracking().ToListAsync(); Summary.Text=$"Müşteri: {await db.Customers.CountAsync()} | Ürün: {await db.Products.CountAsync()} | Tüp: {await db.Cylinders.CountAsync()} | Teklif: {await db.Quotes.CountAsync()}";}
 private async void Refresh_Click(object s,RoutedEventArgs e)=>await LoadAsync();
 private async void AddCustomer_Click(object s,RoutedEventArgs e){await using ArsanGazDbContext db=new(); string c="MUS-"+DateTime.Now.ToString("HHmmss"); db.Customers.Add(new Customer{Code=c,CompanyName="Yeni Müşteri "+c,City="Adana"}); await db.SaveChangesAsync(); await LoadAsync();}
 private async void AddCylinder_Click(object s,RoutedEventArgs e){await using ArsanGazDbContext db=new(); string c=DateTime.Now.ToString("yyyyMMddHHmmss"); db.Cylinders.Add(new Cylinder{SerialNumber="SN"+c,Barcode="BC"+c,GasType="Oksijen",Capacity=50}); await db.SaveChangesAsync(); await LoadAsync();}
}