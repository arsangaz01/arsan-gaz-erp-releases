using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using ArsanGazERP.Services;
using Microsoft.Win32;
namespace ArsanGazERP;
public partial class MainWindow:Window
{
 private readonly AuthService _auth=new();private readonly GraphService _graph;private readonly ExcelErpService _excel=new();private readonly ExcelSyncService _sync=new();private readonly AgentV3Service _agent=new();private readonly UpdateService _update=new();
 public MainWindow(){InitializeComponent();_graph=new GraphService(_auth);}
 private async Task RunAsync(Func<Task<string>> a){try{Output.Text=await a();}catch(Exception ex){Output.Text=ex.ToString();}}
 private async void Login_Click(object s,RoutedEventArgs e)=>await RunAsync(async()=>"Giriş başarılı: "+(await _auth.SignInAsync()).Account.Username);
 private async void Consent_Click(object s,RoutedEventArgs e)=>await RunAsync(async()=>"İzinler yenilendi: "+(await _auth.SignInAsync(true)).Account.Username);
 private async void Profile_Click(object s,RoutedEventArgs e)=>await RunAsync(_graph.GetProfileAsync);private async void OneDrive_Click(object s,RoutedEventArgs e)=>await RunAsync(_graph.ListOneDriveFilesAsync);private async void Mail_Click(object s,RoutedEventArgs e)=>await RunAsync(_graph.ListRecentMailAsync);private async void Contacts_Click(object s,RoutedEventArgs e)=>await RunAsync(_graph.ListContactsAsync);private async void Calendar_Click(object s,RoutedEventArgs e)=>await RunAsync(_graph.ListCalendarAsync);
 private void Excel_Click(object s,RoutedEventArgs e){try{OpenFileDialog d=new(){Filter="Excel Makrolu Çalışma Kitabı|*.xlsm",FileName="ARSAN_AI_ERP_v1.0.xlsm"};if(d.ShowDialog()==true){_excel.SelectWorkbook(d.FileName);Output.Text="ERP bağlandı:"+Environment.NewLine+d.FileName+Environment.NewLine+"Sayfalar: "+string.Join(", ",_excel.GetSheetNames());}}catch(Exception ex){Output.Text=ex.ToString();}}
 private async void ExcelSync_Click(object s,RoutedEventArgs e)=>await RunAsync(()=>_sync.ImportProductsAsync(_excel.WorkbookPath??throw new InvalidOperationException("Önce ERP Excel dosyasını seçin.")));
 private void Operations_Click(object s,RoutedEventArgs e)=>new ArsanGazERP.Views.OperationsWindow{Owner=this}.ShowDialog();private void Database_Click(object s,RoutedEventArgs e)=>new ArsanGazERP.Views.DatabaseManagementWindow{Owner=this}.ShowDialog();
 private async void AgentV3_Click(object s,RoutedEventArgs e){try{Output.Text=string.Join(Environment.NewLine,(await _agent.AnalyzeAsync()).Select(x=>$"[{x.Severity}] {x.Category}: {x.Message}"));}catch(Exception ex){Output.Text=ex.ToString();}}
 private void Backup_Click(object s,RoutedEventArgs e){try{Output.Text="Excel yedeği: "+_excel.CreateBackup();}catch(Exception ex){Output.Text=ex.ToString();}}private async void CloudBackup_Click(object s,RoutedEventArgs e)=>await RunAsync(()=>new DatabaseBackupService(_graph).BackupToOneDriveAsync());private async void Update_Click(object s,RoutedEventArgs e)=>await RunAsync(_update.CheckAsync);private async void Logout_Click(object s,RoutedEventArgs e){await _auth.SignOutAsync();Output.Text="Oturum kapatıldı.";}
}