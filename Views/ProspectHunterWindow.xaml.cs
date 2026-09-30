using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using ArsanGazERP.Data;
using ArsanGazERP.Services;
using Microsoft.EntityFrameworkCore;
namespace ArsanGazERP.Views;
public partial class ProspectHunterWindow : Window
{
    private readonly ProspectDiscoveryService _service = new();
    private bool _busy;
    public ProspectHunterWindow(){InitializeComponent();Loaded += async (_,_) => await LoadAsync();}
    private async Task LoadAsync(){await using ArsanGazDbContext db = new(); ProspectsGrid.ItemsSource = await db.Prospects.AsNoTracking().OrderByDescending(x=>x.OpportunityScore).ThenByDescending(x=>x.FoundAtUtc).ToListAsync(); StatusText.Text = $"Toplam aday: {await db.Prospects.CountAsync()} · Son yenileme {DateTime.Now:HH:mm}";}
    private async void Scan_Click(object sender, RoutedEventArgs e){if(_busy)return;_busy=true;try{StatusText.Text="Çevrimiçi potansiyel müşteri taraması çalışıyor...";StatusText.Text=await _service.ScanAsync();await LoadAsync();}catch(Exception ex){StatusText.Text=ex.GetBaseException().Message;}finally{_busy=false;}}
    private async void Refresh_Click(object sender, RoutedEventArgs e)=>await LoadAsync();
}
