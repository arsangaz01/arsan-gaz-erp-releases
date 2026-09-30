using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using ArsanGazERP.Models;
using ArsanGazERP.Services;

namespace ArsanGazERP.Views;

public partial class OperationsWindow : Window
{
	private readonly CrudService _crud = new();
	private readonly CultureInfo _turkishCulture = CultureInfo.GetCultureInfo("tr-TR");

	public OperationsWindow()
	{
		InitializeComponent();
		Loaded += async (_, _) => await LoadAsync();
	}

	private async Task LoadAsync()
	{
		try
		{
			Customers.ItemsSource = await _crud.CustomersAsync();
			Products.ItemsSource = await _crud.ProductsAsync();
			Cylinders.ItemsSource = await _crud.CylindersAsync();
			OverdueInvoices.ItemsSource = await _crud.OverdueInvoicesAsync();
			SetStatus($"Veriler yenilendi · {DateTime.Now:HH:mm}");
		}
		catch (Exception exception)
		{
			SetStatus(exception.GetBaseException().Message, true);
		}
	}

	private void SetStatus(string message, bool isError = false)
	{
		Status.Text = message;
		StatusIndicator.Fill = (Brush)FindResource(isError ? "DangerBrush" : "AccentBrush");
	}

	private async Task RunChangeAsync(Func<Task> action, string successMessage)
	{
		try
		{
			await action();
			await LoadAsync();
			SetStatus(successMessage);
		}
		catch (Exception exception)
		{
			SetStatus(exception.GetBaseException().Message, true);
		}
	}

	private async void AddCustomer_Click(object sender, RoutedEventArgs e)
	{
		if (string.IsNullOrWhiteSpace(CustomerName.Text))
		{
			SetStatus("Firma adı zorunludur.", true);
			CustomerName.Focus();
			return;
		}

		string code = string.IsNullOrWhiteSpace(CustomerCode.Text)
			? $"MUS-{DateTime.Now:yyyyMMddHHmmss}"
			: CustomerCode.Text.Trim();

		await RunChangeAsync(
			() => _crud.SaveCustomerAsync(new Customer
			{
				Code = code,
				CompanyName = CustomerName.Text.Trim(),
				Phone = CustomerPhone.Text.Trim(),
				Email = CustomerEmail.Text.Trim(),
				City = CustomerCity.Text.Trim()
			}),
			$"{CustomerName.Text.Trim()} müşteri listesine eklendi.");

		CustomerCode.Clear();
		CustomerName.Clear();
		CustomerPhone.Clear();
		CustomerEmail.Clear();
		CustomerCity.Clear();
	}

	private async void DeleteCustomer_Click(object sender, RoutedEventArgs e)
	{
		if (Customers.SelectedItem is not Customer customer)
		{
			SetStatus("Silmek için müşteri tablosundan bir kayıt seçin.", true);
			return;
		}

		if (MessageBox.Show(this, $"'{customer.CompanyName}' müşterisi silinsin mi?", "Müşteriyi sil", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
		{
			return;
		}

		await RunChangeAsync(() => _crud.DeleteCustomerAsync(customer.Id), "Müşteri kaydı silindi.");
	}

	private async void AddProduct_Click(object sender, RoutedEventArgs e)
	{
		if (string.IsNullOrWhiteSpace(ProductCode.Text) || string.IsNullOrWhiteSpace(ProductName.Text))
		{
			SetStatus("Ürün kodu ve ürün adı zorunludur.", true);
			return;
		}

		if (!decimal.TryParse(ProductPrice.Text, NumberStyles.Number, _turkishCulture, out decimal price) || price < 0)
		{
			SetStatus("Birim fiyatı geçerli ve sıfırdan küçük olmayan bir tutar olmalıdır.", true);
			ProductPrice.Focus();
			return;
		}

		if (ProductCurrency.SelectedItem is not System.Windows.Controls.ComboBoxItem { Content: string currency })
		{
			SetStatus("Para birimi seçin.", true);
			return;
		}

		await RunChangeAsync(
			() => _crud.SaveProductAsync(new Product
			{
				Code = ProductCode.Text.Trim(),
				Name = ProductName.Text.Trim(),
				SalesPrice = price,
				Currency = currency,
				VatRate = 20
			}),
			$"{ProductName.Text.Trim()} ürün kataloğuna eklendi.");

		ProductCode.Clear();
		ProductName.Clear();
		ProductPrice.Clear();
	}

	private async void DeleteProduct_Click(object sender, RoutedEventArgs e)
	{
		if (Products.SelectedItem is not Product product)
		{
			SetStatus("Silmek için ürün tablosundan bir kayıt seçin.", true);
			return;
		}

		if (MessageBox.Show(this, $"'{product.Name}' ürünü silinsin mi?", "Ürünü sil", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
		{
			return;
		}

		await RunChangeAsync(() => _crud.DeleteProductAsync(product.Id), "Ürün kaydı silindi.");
	}

	private async void AddCylinder_Click(object sender, RoutedEventArgs e)
	{
		if (string.IsNullOrWhiteSpace(CylinderSerial.Text) || string.IsNullOrWhiteSpace(CylinderGas.Text))
		{
			SetStatus("Tüp seri numarası ve gaz türü zorunludur.", true);
			return;
		}

		if (!decimal.TryParse(CylinderCapacity.Text, NumberStyles.Number, _turkishCulture, out decimal capacity) || capacity <= 0)
		{
			SetStatus("Tüp kapasitesi sıfırdan büyük geçerli bir sayı olmalıdır.", true);
			CylinderCapacity.Focus();
			return;
		}

		await RunChangeAsync(
			() => _crud.SaveCylinderAsync(new Cylinder
			{
				SerialNumber = CylinderSerial.Text.Trim(),
				Barcode = CylinderBarcode.Text.Trim(),
				GasType = CylinderGas.Text.Trim(),
				Capacity = capacity,
				Status = CylinderStatus.Warehouse
			}),
			"Tüp envantere eklendi.");

		CylinderSerial.Clear();
		CylinderBarcode.Clear();
		CylinderGas.Clear();
		CylinderCapacity.Clear();
	}

	private async void MoveWarehouse_Click(object sender, RoutedEventArgs e)
	{
		if (Cylinders.SelectedItem is not Cylinder cylinder)
		{
			SetStatus("Depoya almak için tüp tablosundan bir kayıt seçin.", true);
			return;
		}

		await RunChangeAsync(
			() => _crud.MoveCylinderAsync(cylinder.Id, CylinderStatus.Warehouse, null, "Operasyon ekranından depoya alındı"),
			$"{cylinder.SerialNumber} numaralı tüp depoya alındı.");
	}
}