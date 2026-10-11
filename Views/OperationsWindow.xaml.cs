using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ArsanGazERP.Models;
using ArsanGazERP.Services;

namespace ArsanGazERP.Views;

public partial class OperationsWindow : Window
{
	private readonly CrudService _crud = new();
	private readonly CollectionReminderService _reminders = new();
	private readonly CultureInfo _turkishCulture = CultureInfo.GetCultureInfo("tr-TR");
	private List<ErpTask> _tasks = [];
	private List<Cylinder> _cylinders = [];

	public OperationsWindow(bool openCollections = false)
	{
		InitializeComponent();
        WindowWorkAreaService.Fit(this);
		DataGridColumnLocalizer.Attach(Customers, Products, Cylinders);
		ReminderDueDate.SelectedDate = DateTime.Today.AddDays(7);
		if (openCollections)
		{
			CollectionsTab.IsSelected = true;
		}

		Loaded += async (_, _) => await LoadAsync();
	}

	private sealed record ReminderRow(
		int Id,
		string CustomerName,
		string Amount,
		DateTime DueDate,
		string Timing,
		string Repeat,
		string Status,
		string? Note);

	private async Task LoadAsync()
	{
		try
		{
			List<Customer> customers = await _crud.CustomersAsync();
			Customers.ItemsSource = customers;
			object? selectedScanCustomer = ScanCustomer.SelectedItem;
			ScanCustomer.ItemsSource = customers;
			if (selectedScanCustomer is Customer previous)
			{
				ScanCustomer.SelectedItem = customers.FirstOrDefault(customer => customer.Id == previous.Id);
			}

			Products.ItemsSource = await _crud.ProductsAsync();
			_cylinders = await _crud.CylindersAsync();
			ApplyCylinderFilter();
			string plateText = ScanPlate.Text;
			ScanPlate.ItemsSource = await _crud.KnownVehiclePlatesAsync();
			ScanPlate.Text = plateText;
			await LoadForecastsAsync();
			OverdueInvoices.ItemsSource = await _crud.OverdueInvoicesAsync();
			_tasks = await _crud.TasksAsync();
			ApplyTaskFilters();
			await LoadRemindersAsync();
			SetStatus($"Veriler yenilendi · {DateTime.Now:HH:mm}");
		}
		catch (Exception exception)
		{
			SetStatus(exception.GetBaseException().Message, true);
		}
	}

	private async Task LoadRemindersAsync()
	{
		DateTime today = DateTime.Today;
		List<CollectionReminder> reminders = await _reminders.ListAsync(ShowClosedReminders.IsChecked == true);
		Reminders.ItemsSource = reminders.Select(reminder => new ReminderRow(
			reminder.Id,
			reminder.CustomerName,
			CollectionReminderService.FormatAmount(reminder.Amount, reminder.Currency),
			reminder.DueDate,
			reminder.Status == CollectionReminder.PendingStatus ? DescribeTiming(reminder.DueDate, today) : "—",
			reminder.RepeatMonthly ? "Aylık" : "Tek sefer",
			reminder.Status,
			reminder.Note)).ToList();

		List<CollectionReminder> pending = reminders.Where(reminder => reminder.Status == CollectionReminder.PendingStatus).ToList();
		int overdue = pending.Count(reminder => reminder.DueDate.Date < today);
		int dueSoon = pending.Count(reminder => CollectionReminderService.IsDue(reminder, today));
		ReminderSummary.Text = $"{pending.Count} bekleyen · {dueSoon} hatırlatma zamanı gelmiş · {overdue} gecikmiş";
	}

	private static string DescribeTiming(DateTime dueDate, DateTime today)
	{
		int days = (dueDate.Date - today).Days;
		return days switch
		{
			0 => "Bugün",
			> 0 => $"{days} gün kaldı",
			_ => $"{-days} gün gecikti"
		};
	}

	private async void AddReminder_Click(object sender, RoutedEventArgs e)
	{
		if (!CollectionReminderService.TryParseAmount(ReminderAmount.Text, out decimal amount))
		{
			SetStatus("Tutar geçerli bir sayı olmalıdır (ör. 12.500,00).", true);
			ReminderAmount.Focus();
			return;
		}

		if (ReminderDueDate.SelectedDate is not DateTime dueDate)
		{
			SetStatus("Ödeme gününü seçin.", true);
			return;
		}

		if (!int.TryParse(ReminderDaysBefore.Text, NumberStyles.Integer, _turkishCulture, out int daysBefore))
		{
			SetStatus($"Kaç gün önce hatırlatılacağı 0-{CollectionReminderService.MaximumRemindDaysBefore} arasında bir sayı olmalıdır.", true);
			return;
		}

		string currency = (ReminderCurrency.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "TRY";
		string customer = ReminderCustomer.Text.Trim();
		await RunChangeAsync(
			() => _reminders.AddAsync(new CollectionReminderInput(
				customer,
				amount,
				currency,
				dueDate,
				daysBefore,
				ReminderRepeatMonthly.IsChecked == true,
				ReminderNote.Text)),
			$"{customer} için {dueDate:dd.MM.yyyy} tahsilat hatırlatması eklendi.");

		ReminderCustomer.Clear();
		ReminderAmount.Clear();
		ReminderNote.Clear();
		ReminderRepeatMonthly.IsChecked = false;
	}

	private async void CollectReminder_Click(object sender, RoutedEventArgs e)
	{
		if (Reminders.SelectedItem is not ReminderRow row)
		{
			SetStatus("Tahsil edildi olarak işaretlemek için bir hatırlatma seçin.", true);
			return;
		}

		string message = string.Empty;
		await RunChangeAsync(async () => message = await _reminders.MarkCollectedAsync(row.Id), string.Empty);
		if (message.Length > 0)
		{
			SetStatus(message);
		}
	}

	private async void PostponeReminder_Click(object sender, RoutedEventArgs e)
	{
		if (Reminders.SelectedItem is not ReminderRow row)
		{
			SetStatus("Ertelemek için bir hatırlatma seçin.", true);
			return;
		}

		await RunChangeAsync(() => _reminders.PostponeAsync(row.Id, 7), $"{row.CustomerName} tahsilatı 7 gün ertelendi.");
	}

	private async void CancelReminder_Click(object sender, RoutedEventArgs e)
	{
		if (Reminders.SelectedItem is not ReminderRow row)
		{
			SetStatus("İptal etmek için bir hatırlatma seçin.", true);
			return;
		}

		if (MessageBox.Show(this, $"'{row.CustomerName}' tahsilat hatırlatması iptal edilsin mi?", "Hatırlatmayı iptal et", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
		{
			return;
		}

		await RunChangeAsync(() => _reminders.CancelAsync(row.Id), "Tahsilat hatırlatması iptal edildi.");
	}

	private async void ShowClosedReminders_Changed(object sender, RoutedEventArgs e)
	{
		if (!IsLoaded)
		{
			return;
		}

		try
		{
			await LoadRemindersAsync();
		}
		catch (Exception exception)
		{
			SetStatus("Tahsilat listesi yenilenemedi: " + exception.GetBaseException().Message, true);
		}
	}

	private void TaskSearch_TextChanged(object sender, TextChangedEventArgs e)
	{
		if (IsLoaded)
		{
			ApplyTaskFilters();
		}
	}

	private void TaskFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (IsLoaded)
		{
			ApplyTaskFilters();
		}
	}

	private async void RefreshTasks_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			_tasks = await _crud.TasksAsync();
			ApplyTaskFilters();
			SetStatus($"Görevler yenilendi · {DateTime.Now:HH:mm}");
		}
		catch (Exception exception)
		{
			SetStatus("Görev listesi yenilenemedi: " + exception.GetBaseException().Message, true);
		}
	}

	private void ApplyTaskFilters()
	{
		string search = TaskSearch.Text.Trim();
		string priority = GetSelection(TaskPriorityFilter);
		string dueRange = GetSelection(TaskDueFilter);
		string status = GetSelection(TaskStatusFilter);
		DateTime today = DateTime.Today;

		IEnumerable<ErpTask> filtered = _tasks;
		if (status.Equals("Açık", StringComparison.OrdinalIgnoreCase))
		{
			filtered = filtered.Where(task => task.Status.Equals("Açık", StringComparison.OrdinalIgnoreCase));
		}

		if (!priority.Equals("Tüm öncelikler", StringComparison.OrdinalIgnoreCase))
		{
			filtered = filtered.Where(task => task.Priority.Equals(priority, StringComparison.OrdinalIgnoreCase));
		}

		filtered = dueRange switch
		{
			"Geciken" => filtered.Where(task => task.DueDate is DateTime dueDate && dueDate.Date < today),
			"Bugün" => filtered.Where(task => task.DueDate is DateTime dueDate && dueDate.Date == today),
			"7 gün içinde" => filtered.Where(task =>
				task.DueDate is DateTime dueDate && dueDate.Date >= today && dueDate.Date <= today.AddDays(7)),
			"Tarihi olmayan" => filtered.Where(task => task.DueDate is null),
			_ => filtered
		};

		if (search.Length > 0)
		{
			filtered = filtered.Where(task =>
				task.Title.Contains(search, StringComparison.CurrentCultureIgnoreCase)
				|| task.Category.Contains(search, StringComparison.CurrentCultureIgnoreCase)
				|| (task.Description?.Contains(search, StringComparison.CurrentCultureIgnoreCase) ?? false));
		}

		List<ErpTask> displayedTasks = filtered
			.OrderBy(task => GetPriorityOrder(task.Priority))
			.ThenBy(task => task.DueDate ?? DateTime.MaxValue)
			.ThenBy(task => task.Title, StringComparer.CurrentCultureIgnoreCase)
			.ToList();
		PriorityTasks.ItemsSource = displayedTasks;

		int openCount = _tasks.Count(task => task.Status.Equals("Açık", StringComparison.OrdinalIgnoreCase));
		int overdueCount = _tasks.Count(task =>
			task.Status.Equals("Açık", StringComparison.OrdinalIgnoreCase)
			&& task.DueDate is DateTime dueDate
			&& dueDate.Date < today);
		TaskSummary.Text = $"{displayedTasks.Count} görev gösteriliyor · {openCount} açık · {overdueCount} geciken";
	}

	private static string GetSelection(ComboBox comboBox) =>
		(comboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? string.Empty;

	private static int GetPriorityOrder(string priority) => priority switch
	{
		"Acil" => 0,
		"Yüksek" => 1,
		"Normal" => 2,
		"Düşük" => 3,
		_ => 4
	};

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

		string serialNumber = CylinderSerial.Text.Trim();
		await RunChangeAsync(
			() => _crud.SaveCylinderAsync(new Cylinder
			{
				SerialNumber = serialNumber,
				Barcode = string.IsNullOrWhiteSpace(CylinderBarcode.Text) ? serialNumber : CylinderBarcode.Text.Trim(),
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

	private async Task LoadForecastsAsync()
	{
		List<ReorderForecast> forecasts = await _crud.ReorderForecastsAsync();
		Forecasts.ItemsSource = forecasts;
		int overdue = forecasts.Count(forecast => forecast.IsOverdue);
		int thisWeek = forecasts.Count(forecast => forecast.DaysUntilExpected is >= 0 and <= 7);
		ForecastSummary.Text = forecasts.Count == 0
			? $"Tahmin için en az {ReorderPrediction.MinimumDeliveryDays} farklı günde teslim kaydı olan müşteri yok. Teslimler \"Barkodla hızlı hareket\" ile kaydedildikçe liste dolar."
			: $"{forecasts.Count} müşteri · {overdue} gecikmiş · {thisWeek} bu hafta bekleniyor. Tahmin, müşterinin geçmiş teslimleri arasındaki tipik (ortanca) gün sayısına dayanır.";
	}

	private async void RefreshForecasts_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			await LoadForecastsAsync();
			SetStatus($"Sipariş tahmini yenilendi · {DateTime.Now:HH:mm}");
		}
		catch (Exception exception)
		{
			SetStatus("Sipariş tahmini hesaplanamadı: " + exception.GetBaseException().Message, true);
		}
	}

	private void ScanAction_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (ScanCustomer is not null)
		{
			ScanCustomer.IsEnabled = SelectedScanTarget() == CylinderStatus.Customer;
		}

		if (ScanPlate is not null)
		{
			ScanPlate.IsEnabled = SelectedScanTarget() == CylinderStatus.Vehicle;
		}
	}

	private void CylinderFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (Cylinders is not null)
		{
			ApplyCylinderFilter();
		}
	}

	private void ApplyCylinderFilter()
	{
		DateTime longLimit = DateTime.Today.AddDays(-CylinderTracking.LongAtCustomerDays);
		bool IsLong(Cylinder cylinder) =>
			cylinder.Status == CylinderStatus.Customer && cylinder.LastMovementAt is DateTime at && at < longLimit;

		string? tag = (CylinderFilter.SelectedItem as ComboBoxItem)?.Tag as string;
		Cylinders.ItemsSource = tag switch
		{
			null => _cylinders,
			"Long" => _cylinders.Where(IsLong).OrderBy(cylinder => cylinder.LastMovementAt).ToList(),
			_ when Enum.TryParse(tag, out CylinderStatus status) => _cylinders.Where(cylinder => cylinder.Status == status).ToList(),
			_ => _cylinders
		};

		List<Cylinder> active = _cylinders.Where(cylinder => cylinder.IsActive).ToList();
		IEnumerable<string> parts = CylinderTracking.AllStatuses
			.Select(status => (Status: status, Items: active.Where(cylinder => (int)cylinder.Status == status).ToList()))
			.Where(group => group.Items.Count > 0)
			.Select(group =>
			{
				int full = group.Items.Count(cylinder => cylinder.IsFull == true);
				int empty = group.Items.Count(cylinder => cylinder.IsFull == false);
				string split = full + empty > 0 ? $" (dolu {full} · boş {empty})" : string.Empty;
				return $"{CylinderTracking.StatusName(group.Status)} {group.Items.Count}{split}";
			});
		int longCount = active.Count(IsLong);
		CylinderSummary.Text = active.Count == 0
			? "Henüz tüp kaydı yok."
			: string.Join(" · ", parts) + (longCount > 0 ? $" · ⚠ {longCount} tüp {CylinderTracking.LongAtCustomerDays}+ gündür müşteride" : string.Empty);
	}

	private bool? SelectedFullness() =>
		(ScanFullness.SelectedItem as ComboBoxItem)?.Tag is string tag && bool.TryParse(tag, out bool value) ? value : null;

	private CylinderStatus SelectedScanTarget() =>
		(ScanAction.SelectedItem as ComboBoxItem)?.Tag is string tag && Enum.TryParse(tag, out CylinderStatus status)
			? status
			: CylinderStatus.Customer;

	private async void ScanCode_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
	{
		if (e.Key == System.Windows.Input.Key.Enter)
		{
			e.Handled = true;
			await ApplyScanAsync();
		}
	}

	private async void ApplyScan_Click(object sender, RoutedEventArgs e) => await ApplyScanAsync();

	private async Task ApplyScanAsync()
	{
		string code = ScanCode.Text.Trim();
		if (code.Length == 0)
		{
			SetStatus("Önce tüp barkodunu okutun veya seri numarasını yazın.", true);
			ScanCode.Focus();
			return;
		}

		CylinderStatus target = SelectedScanTarget();
		Customer? customer = ScanCustomer.SelectedItem as Customer;
		if (target == CylinderStatus.Customer && customer is null)
		{
			SetStatus("Müşteriye teslim için müşteri seçin.", true);
			ScanCustomer.Focus();
			return;
		}

		string action = (ScanAction.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? target.ToString();
		string? plate = null;
		if (target == CylinderStatus.Vehicle)
		{
			try
			{
				plate = CylinderTracking.NormalizePlate(ScanPlate.Text);
			}
			catch (ArgumentException exception)
			{
				SetStatus(exception.Message, true);
				ScanPlate.Focus();
				return;
			}
		}

		bool? fullness = SelectedFullness();
		try
		{
			Cylinder? cylinder = await _crud.FindCylinderByCodeAsync(code);
			if (cylinder is null)
			{
				SetStatus($"'{code}' barkodlu veya seri numaralı tüp bulunamadı.", true);
				ScanCode.SelectAll();
				ScanCode.Focus();
				return;
			}

			string message = target switch
			{
				CylinderStatus.Customer => $"{cylinder.SerialNumber} → {customer!.CompanyName} teslim edildi.",
				CylinderStatus.Vehicle when plate is not null => $"{cylinder.SerialNumber} → {plate} plakalı araca yüklendi.",
				_ => $"{cylinder.SerialNumber}: {action} kaydedildi."
			};
			await RunChangeAsync(
				() => _crud.MoveCylinderAsync(
					cylinder.Id,
					target,
					target == CylinderStatus.Customer ? customer!.Id : null,
					"Barkodla hızlı hareket",
					action,
					plate,
					fullness),
				message);
		}
		catch (Exception exception)
		{
			SetStatus(exception.GetBaseException().Message, true);
		}

		ScanCode.Clear();
		ScanCode.Focus();
	}

	private void PrintCylinderLabel_Click(object sender, RoutedEventArgs e)
	{
		if (Cylinders.SelectedItem is not Cylinder cylinder)
		{
			SetStatus("Etiket için tüp tablosundan bir kayıt seçin.", true);
			return;
		}

		try
		{
			new CylinderLabelWindow(cylinder) { Owner = this }.ShowDialog();
		}
		catch (Exception exception)
		{
			SetStatus("Etiket oluşturulamadı: " + exception.GetBaseException().Message, true);
		}
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