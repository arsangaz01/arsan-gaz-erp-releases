using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using ArsanGazERP.Data;
using ArsanGazERP.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Win32;

namespace ArsanGazERP;

public partial class MainWindow : Window
{
	private readonly AuthService _auth = new();
	private readonly GraphService _graph;
	private readonly ExcelErpService _excel = new();
	private readonly ExcelSyncService _sync = new();
	private readonly AgentV3Service _agent = new();
	private readonly UpdateService _update = new();
	private bool _isBusy;

	public MainWindow()
	{
		InitializeComponent();
		_graph = new GraphService(_auth);
		Loaded += async (_, _) => await LoadDashboardAsync();
	}

	private async Task LoadDashboardAsync()
	{
		TodayLabel.Text = DateTime.Now.ToString("dddd, d MMMM yyyy", CultureInfo.GetCultureInfo("tr-TR"));
		VersionText.Text = $"v{typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "5.1.0"}";
		WorkbookStatus.Text = string.IsNullOrWhiteSpace(_excel.WorkbookPath)
			? "Çalışma kitabı seçilmedi"
			: Path.GetFileName(_excel.WorkbookPath);

		try
		{
			await using ArsanGazDbContext db = new();
			CustomerCount.Text = (await db.Customers.CountAsync()).ToString("N0", CultureInfo.GetCultureInfo("tr-TR"));
			ProductCount.Text = (await db.Products.CountAsync()).ToString("N0", CultureInfo.GetCultureInfo("tr-TR"));
			CylinderCount.Text = (await db.Cylinders.CountAsync()).ToString("N0", CultureInfo.GetCultureInfo("tr-TR"));
			OverdueCount.Text = (await db.Invoices.CountAsync(invoice => invoice.DueDate < DateTime.Today && invoice.PaidAmount < invoice.TotalAmount))
				.ToString("N0", CultureInfo.GetCultureInfo("tr-TR"));
			DatabaseSummary.Text = $"SQLite hazır · Son yenileme {DateTime.Now:HH:mm}";
			StatusText.Text = "Sistem hazır";
		}
		catch (Exception exception)
		{
			DatabaseSummary.Text = "SQLite bağlantısı denetlenemedi";
			StatusText.Text = "Veritabanı durumu alınamadı";
			Output.Text = exception.GetBaseException().Message;
		}
	}

	private async Task RunAsync(Func<Task<string>> action)
	{
		if (_isBusy)
		{
			return;
		}

		_isBusy = true;
		Mouse.OverrideCursor = Cursors.Wait;
		StatusText.Text = "İşlem sürüyor";

		try
		{
			Output.Text = await action();
			StatusText.Text = "İşlem tamamlandı";
		}
		catch (Exception exception)
		{
			Output.Text = exception.GetBaseException().Message;
			StatusText.Text = "İşlem tamamlanamadı";
		}
		finally
		{
			Mouse.OverrideCursor = null;
			_isBusy = false;
		}
	}

	private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadDashboardAsync();

	private async void Login_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
		$"Oturum açıldı: {(await _auth.SignInAsync()).Account.Username}");

	private async void Consent_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
		$"İzinler yenilendi: {(await _auth.SignInAsync(true)).Account.Username}");

	private async void Profile_Click(object sender, RoutedEventArgs e) => await RunAsync(_graph.GetProfileAsync);
	private async void OneDrive_Click(object sender, RoutedEventArgs e) => await RunAsync(_graph.ListOneDriveFilesAsync);
	private async void Mail_Click(object sender, RoutedEventArgs e) => await RunAsync(_graph.ListRecentMailAsync);
	private async void Contacts_Click(object sender, RoutedEventArgs e) => await RunAsync(_graph.ListContactsAsync);
	private async void Calendar_Click(object sender, RoutedEventArgs e) => await RunAsync(_graph.ListCalendarAsync);

	private void Excel_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			OpenFileDialog dialog = new()
			{
				Title = "ERP çalışma kitabını seç",
				Filter = "Excel makrolu çalışma kitabı (*.xlsm)|*.xlsm",
				FileName = "ARSAN_AI_ERP_v1.0.xlsm",
				CheckFileExists = true
			};

			if (dialog.ShowDialog(this) == true)
			{
				_excel.SelectWorkbook(dialog.FileName);
				WorkbookStatus.Text = Path.GetFileName(dialog.FileName);
				WorkbookStatus.ToolTip = dialog.FileName;
				Output.Text = "ERP çalışma kitabı bağlandı:" + Environment.NewLine
					+ dialog.FileName + Environment.NewLine
					+ "Sayfalar: " + string.Join(", ", _excel.GetSheetNames());
				StatusText.Text = "ERP çalışma kitabı hazır";
			}
		}
		catch (Exception exception)
		{
			Output.Text = exception.GetBaseException().Message;
			StatusText.Text = "Çalışma kitabı açılamadı";
		}
	}

	private async void ExcelSync_Click(object sender, RoutedEventArgs e)
	{
		await RunAsync(() => _sync.ImportProductsAsync(
			_excel.WorkbookPath ?? throw new InvalidOperationException("Önce ERP Excel dosyasını seçin.")));
		await LoadDashboardAsync();
	}

	private async void Operations_Click(object sender, RoutedEventArgs e)
	{
		new ArsanGazERP.Views.OperationsWindow { Owner = this }.ShowDialog();
		await LoadDashboardAsync();
	}

	private async void Database_Click(object sender, RoutedEventArgs e)
	{
		new ArsanGazERP.Views.DatabaseManagementWindow { Owner = this }.ShowDialog();
		await LoadDashboardAsync();
	}

	private async void AgentV3_Click(object sender, RoutedEventArgs e) => await RunAsync(async () => string.Join(
		Environment.NewLine,
		(await _agent.AnalyzeAsync()).Select(finding => $"[{finding.Severity}] {finding.Category}: {finding.Message}")));

	private async void Backup_Click(object sender, RoutedEventArgs e) => await RunAsync(
		() => Task.FromResult("Excel yedeği oluşturuldu:" + Environment.NewLine + _excel.CreateBackup()));

	private async void CloudBackup_Click(object sender, RoutedEventArgs e) => await RunAsync(
		() => new DatabaseBackupService(_graph).BackupToOneDriveAsync());

	private async void Update_Click(object sender, RoutedEventArgs e) => await RunAsync(_update.CheckAsync);

	private async void Logout_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
	{
		await _auth.SignOutAsync();
		return "Microsoft 365 oturumu kapatıldı.";
	});

	private void ClearOutput_Click(object sender, RoutedEventArgs e)
	{
		Output.Clear();
		StatusText.Text = "İşlem günlüğü temizlendi";
	}
}