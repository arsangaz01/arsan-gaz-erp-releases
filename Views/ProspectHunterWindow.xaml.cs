using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ArsanGazERP.Data;
using ArsanGazERP.Services;
using Microsoft.EntityFrameworkCore;

namespace ArsanGazERP.Views;

public partial class ProspectHunterWindow : Window
{
    private readonly ProspectDiscoveryService _service = new();
    private CancellationTokenSource? _scanCancellation;

    public ProspectHunterWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        await using ArsanGazDbContext db = new();
        ProspectsGrid.ItemsSource = await db.Prospects
            .AsNoTracking()
            .OrderByDescending(item => item.OpportunityScore)
            .ThenByDescending(item => item.FoundAtUtc)
            .ToListAsync();
        StatusText.Text = $"Toplam aday: {await db.Prospects.CountAsync()} · Son yenileme {DateTime.Now:HH:mm}";
    }

    private void SaveApiKey_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ProspectDiscoveryService.SaveApiKey(ApiKeyBox.Password);
            ApiKeyBox.Clear();
            StatusText.Text = "API anahtarı Windows kullanıcı ayarına kaydedildi. Günlük otomatik tarama etkin.";
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.GetBaseException().Message;
        }
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_scanCancellation is not null) return;
        _scanCancellation = new CancellationTokenSource();
        ScanButton.IsEnabled = false;
        CancelButton.Visibility = Visibility.Visible;
        try
        {
            Progress<string> progress = new(message => StatusText.Text = "Taranıyor · " + message);
            ProspectScanResult result = await _service.ScanAsync(progress, _scanCancellation.Token);
            StatusText.Text = result.ToString();
            await LoadAsync();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Tarama kullanıcı tarafından durduruldu.";
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.GetBaseException().Message;
        }
        finally
        {
            _scanCancellation.Dispose();
            _scanCancellation = null;
            ScanButton.IsEnabled = true;
            CancelButton.Visibility = Visibility.Collapsed;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _scanCancellation?.Cancel();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();
}
