using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ArsanGazERP.Models;
using ArsanGazERP.Services;
namespace ArsanGazERP.Views;
public partial class OperationsWindow:Window
{
 private readonly CrudService _crud=new();
 public OperationsWindow(){InitializeComponent();Loaded+=async(_,_)=>await LoadAsync();}
 private async Task LoadAsync(){Customers.ItemsSource=await _crud.CustomersAsync();Products.ItemsSource=await _crud.ProductsAsync();Cylinders.ItemsSource=await _crud.CylindersAsync();OverdueInvoices.ItemsSource=await _crud.OverdueInvoicesAsync();Status.Text="Veriler yenilendi: "+DateTime.Now.ToString("HH:mm:ss");}
 private async void AddCustomer_Click(object s,RoutedEventArgs e){try{if(string.IsNullOrWhiteSpace(CustomerName.Text))throw new InvalidOperationException("Firma adı gerekli.");await _crud.SaveCustomerAsync(new Customer{Code=string.IsNullOrWhiteSpace(CustomerCode.Text)?"MUS-"+DateTime.Now.ToString("HHmmss"):CustomerCode.Text.Trim(),CompanyName=CustomerName.Text.Trim(),Phone=CustomerPhone.Text.Trim(),Email=CustomerEmail.Text.Trim(),City=CustomerCity.Text.Trim()});await LoadAsync();}catch(Exception ex){MessageBox.Show(ex.Message);}}
 private async void DeleteCustomer_Click(object s,RoutedEventArgs e){if(Customers.SelectedItem is Customer x&&MessageBox.Show("Müşteri silinsin mi?","Onay",MessageBoxButton.YesNo)==MessageBoxResult.Yes){await _crud.DeleteCustomerAsync(x.Id);await LoadAsync();}}
 private async void AddProduct_Click(object s,RoutedEventArgs e){try{if(!decimal.TryParse(ProductPrice.Text,NumberStyles.Any,CultureInfo.GetCultureInfo("tr-TR"),out decimal p))p=0;string cur=((ComboBoxItem)ProductCurrency.SelectedItem).Content.ToString()!;await _crud.SaveProductAsync(new Product{Code=ProductCode.Text.Trim(),Name=ProductName.Text.Trim(),SalesPrice=p,Currency=cur,VatRate=20});await LoadAsync();}catch(Exception ex){MessageBox.Show(ex.Message);}}
 private async void DeleteProduct_Click(object s,RoutedEventArgs e){if(Products.SelectedItem is Product x&&MessageBox.Show("Ürün silinsin mi?","Onay",MessageBoxButton.YesNo)==MessageBoxResult.Yes){await _crud.DeleteProductAsync(x.Id);await LoadAsync();}}
 private async void AddCylinder_Click(object s,RoutedEventArgs e){try{decimal.TryParse(CylinderCapacity.Text,NumberStyles.Any,CultureInfo.GetCultureInfo("tr-TR"),out decimal cap);await _crud.SaveCylinderAsync(new Cylinder{SerialNumber=CylinderSerial.Text.Trim(),Barcode=CylinderBarcode.Text.Trim(),GasType=CylinderGas.Text.Trim(),Capacity=cap,Status=CylinderStatus.Warehouse});await LoadAsync();}catch(Exception ex){MessageBox.Show(ex.Message);}}
 private async void MoveWarehouse_Click(object s,RoutedEventArgs e){if(Cylinders.SelectedItem is Cylinder x){await _crud.MoveCylinderAsync(x.Id,CylinderStatus.Warehouse,null,"Operasyon ekranından depoya alındı");await LoadAsync();}}
}