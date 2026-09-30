using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace ArsanGazERP.AgentWatcher;
internal static class Program
{
    [STAThread] static void Main(){ApplicationConfiguration.Initialize();using Mutex m=new(true,"Local\\ArsanGazERP.AgentWatcher",out bool first);if(first)Application.Run(new WatcherForm());}
}
internal sealed class WatcherForm:Form
{
    const string Project=@"C:\ArsanGazERP\ArsanGazERP_V2_Duzeltilmis";
    const string Root=@"C:\ArsanGazERP\ArsanGazERP_V2_Duzeltilmis\Tools\AgentWatcherData";
    const string Model="qwen2.5-coder:1.5b";
    readonly string Queue=Path.Combine(Root,"Queue"),Logs=Path.Combine(Root,"Logs"),Reports=Path.Combine(Root,"Reports");
    readonly Label connection=new(){Text="Microsoft 365 (Bağlı değil) | Excel (Seçilmedi)",Dock=DockStyle.Top,Height=34,Padding=new(10,9,0,0)};
    readonly Label taskStatus=new(){Text="Hazır",Dock=DockStyle.Top,Height=28,Padding=new(10,6,0,0)};
    readonly ProgressBar progress=new(){Dock=DockStyle.Top,Height=22,Minimum=0,Maximum=100};
    readonly TextBox output=new(){Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,Dock=DockStyle.Fill};
    readonly TextBox input=new(){Multiline=true,Height=70,Dock=DockStyle.Bottom};
    readonly LinkLabel reportLink=new(){Text="Rapor konumu: Henüz rapor yok",Dock=DockStyle.Bottom,Height=28,Padding=new(10,6,0,0)};
    readonly Button signOut=new(){Text="Microsoft 365 Çıkış Yap",Height=38,Dock=DockStyle.Bottom};
    readonly Button openErp=new(){Text="Güncel ERP'yi Aç",Height=38,Dock=DockStyle.Bottom};
    readonly Button add=new(){Text="Görev Ekle / Sohbet Et",Height=42,Dock=DockStyle.Bottom};
    readonly Button setup=new(){Text="ERP + Microsoft 365 + Excel Tek Seferde Hazırla",Height=44,Dock=DockStyle.Bottom};
    readonly System.Windows.Forms.Timer timer=new(){Interval=15000};
    readonly HttpClient ollama=new(){BaseAddress=new Uri("http://localhost:11434"),Timeout=TimeSpan.FromMinutes(12)};
    bool busy;string lastErrorHash="",lastReport="";SharedState state=SharedState.Load();
    public WatcherForm()
    {
        Text="Arsan Gaz ERP AgentWatcher V7.4.0";Width=980;Height=760;StartPosition=FormStartPosition.CenterScreen;
        Controls.Add(output);Controls.Add(progress);Controls.Add(taskStatus);Controls.Add(connection);Controls.Add(reportLink);Controls.Add(signOut);Controls.Add(openErp);Controls.Add(add);Controls.Add(setup);Controls.Add(input);
        Directory.CreateDirectory(Queue);Directory.CreateDirectory(Logs);Directory.CreateDirectory(Reports);
        setup.Click+=async(_,_)=>await SetupAllAsync();add.Click+=async(_,_)=>await AddTaskAsync();openErp.Click+=(_,_)=>OpenCurrentErp();signOut.Click+=async(_,_)=>await SignOutAsync();reportLink.LinkClicked+=(_,_)=>OpenLastReport();timer.Tick+=async(_,_)=>await CheckAsync();
        Load+=async(_,_)=>{timer.Start();UpdateConnectionLabel();Append("AgentWatcher hazır. Görev işlemcisi etkin.");await TryRestoreMicrosoftSessionAsync();await CheckAsync();};FormClosing+=(_,e)=>{e.Cancel=true;Hide();};
    }
    async Task SetupAllAsync()
    {
        try{SetProgress(5,"Microsoft 365 oturumu hazırlanıyor");string account=await MicrosoftGraph.ConnectAsync(false);state.Account=account;SetProgress(45,"Excel çalışma kitabı denetleniyor");if(string.IsNullOrWhiteSpace(state.ExcelPath)||!File.Exists(state.ExcelPath)){using OpenFileDialog d=new(){Title="ERP Excel çalışma kitabını seç",Filter="Excel çalışma kitapları (*.xlsm;*.xlsx)|*.xlsm;*.xlsx",CheckFileExists=true};if(d.ShowDialog(this)==DialogResult.OK)state.ExcelPath=d.FileName;}state.Save();SetProgress(75,"Güncel ERP açılıyor");OpenCurrentErp();SetProgress(100,"Tüm bağlantılar hazır");UpdateConnectionLabel();Append("Tek seferlik hazırlık tamamlandı. Microsoft 365 ve Excel seçimi çıkış yapana kadar korunur.");}
        catch(Exception ex){SetProgress(0,"Hazırlık tamamlanamadı");Append("Hazırlık hatası: "+ex.GetBaseException().Message);}
    }
    async Task TryRestoreMicrosoftSessionAsync(){try{string? account=await MicrosoftGraph.TryConnectSilentAsync();if(!string.IsNullOrWhiteSpace(account)){state.Account=account;state.Save();UpdateConnectionLabel();Append("Microsoft 365 oturumu önbellekten geri yüklendi.");}}catch(Exception ex){Append("Sessiz oturum kontrolü: "+ex.GetBaseException().Message);}}
    async Task SignOutAsync(){await MicrosoftGraph.SignOutAsync();state.Account="";state.Save();UpdateConnectionLabel();Append("Microsoft 365 oturumu kullanıcı isteğiyle kapatıldı.");}
    void UpdateConnectionLabel(){string m=string.IsNullOrWhiteSpace(state.Account)?"Bağlı değil":"Bağlı: "+state.Account;string x=string.IsNullOrWhiteSpace(state.ExcelPath)?"Seçilmedi":"Seçili: "+Path.GetFileName(state.ExcelPath);connection.Text=$"Microsoft 365 ({m}) | Excel ({x})";}
    async Task AddTaskAsync(){string text=input.Text.Trim();if(text.Length==0)return;input.Clear();string f=Path.Combine(Queue,$"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.task.txt");await File.WriteAllTextAsync(f,text,new UTF8Encoding(false));Append("Görev kuyruğa eklendi: "+text);SetProgress(0,"Görev bekliyor");await CheckAsync();}
    async Task CheckAsync(){if(busy||AvailableMemory()<2UL*1024*1024*1024||Process.GetProcessesByName("devenv").Length>0)return;string? task=Directory.EnumerateFiles(Queue,"*.task.txt").OrderBy(x=>x).FirstOrDefault();if(task!=null){busy=true;try{await RunTaskAsync(task);}catch(Exception ex){Append("Görev hatası: "+ex.GetBaseException().Message);SetProgress(0,"Görev başarısız");}finally{busy=false;}return;}string errors=CollectErrors();if(errors.Length==0)return;busy=true;try{await AnalyzeAsync(errors);}finally{busy=false;}}
    async Task RunTaskAsync(string file){SetProgress(10,"Görev okunuyor");string request=await File.ReadAllTextAsync(file);SetProgress(30,"Görev analiz ediliyor");string result=IsProjectStatusRequest(request)?BuildProjectStatusReport(request):await AskAsync("Arsan Gaz ERP .NET 10 WPF sistemi için güvenli ve uygulanabilir görev planı üret. Silme, ödeme, e-posta gönderme, fatura ve yetki yükseltme yapma. İstek: "+request);SetProgress(80,"Rapor kaydediliyor");lastReport=Path.Combine(Reports,$"task-{DateTime.Now:yyyyMMdd-HHmmss}.txt");await File.WriteAllTextAsync(lastReport,result,new UTF8Encoding(false));File.Move(file,file.Replace(".task.txt",".done.txt"),true);SetProgress(100,"Görev tamamlandı");UpdateReportLink();Append("Görev tamamlandı. Rapor: "+lastReport);Append(result);await UnloadAsync();}
    static bool IsProjectStatusRequest(string t)=>t.Contains("proje durum",StringComparison.OrdinalIgnoreCase)||t.Contains("publish",StringComparison.OrdinalIgnoreCase)||t.Contains("EXE",StringComparison.OrdinalIgnoreCase)||t.Contains("kontrol",StringComparison.OrdinalIgnoreCase);
    static string BuildProjectStatusReport(string request){StringBuilder s=new();s.AppendLine("ARSAN GAZ ERP PROJE DURUM RAPORU");s.AppendLine("Oluşturma: "+DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss"));s.AppendLine("İstek: "+request);s.AppendLine();string cs=Path.Combine(Project,"ArsanGazERP.csproj");s.AppendLine("Proje dosyası: "+(File.Exists(cs)?"VAR":"YOK")+" | "+cs);string cur=Path.Combine(Project,"publish","current","ArsanGazERP.exe");s.AppendLine("Sabit güncel EXE: "+(File.Exists(cur)?"VAR":"YOK")+" | "+cur);var exes=Directory.EnumerateFiles(Project,"ArsanGazERP.exe",SearchOption.AllDirectories).Where(x=>!x.Contains("\\bin\\")&&!x.Contains("\\obj\\")&&!x.Contains("\\Tools\\")).Select(x=>new FileInfo(x)).OrderByDescending(x=>x.LastWriteTime).ToList();if(exes.Count>0){s.AppendLine("En yeni ERP EXE: "+exes[0].FullName);s.AppendLine("Değiştirme: "+exes[0].LastWriteTime.ToString("dd.MM.yyyy HH:mm:ss"));}else s.AppendLine("ERP EXE bulunamadı.");return s.ToString();}
    void OpenCurrentErp(){string cur=Path.Combine(Project,"publish","current","ArsanGazERP.exe");string? exe=File.Exists(cur)?cur:Directory.EnumerateFiles(Project,"ArsanGazERP.exe",SearchOption.AllDirectories).Where(x=>!x.Contains("\\bin\\")&&!x.Contains("\\obj\\")&&!x.Contains("\\Tools\\")).OrderByDescending(File.GetLastWriteTime).FirstOrDefault();if(exe is null){Append("Güncel ERP EXE bulunamadı.");return;}Process.Start(new ProcessStartInfo(exe){UseShellExecute=true,WorkingDirectory=Path.GetDirectoryName(exe)!});Append("ERP açıldı: "+exe);}
    void OpenLastReport(){if(File.Exists(lastReport))Process.Start(new ProcessStartInfo(lastReport){UseShellExecute=true});else Process.Start(new ProcessStartInfo(Reports){UseShellExecute=true});}
    void UpdateReportLink(){reportLink.Text="Rapor konumu: "+lastReport;reportLink.Links.Clear();reportLink.Links.Add(0,reportLink.Text.Length,lastReport);}
    void SetProgress(int value,string text){if(InvokeRequired){BeginInvoke(()=>SetProgress(value,text));return;}progress.Value=Math.Clamp(value,0,100);taskStatus.Text=$"{text} (%{progress.Value})";}
    async Task AnalyzeAsync(string errors){SetProgress(20,"Derleme hatası analiz ediliyor");string result=await AskAsync("Aşağıdaki .NET 10 WPF derleme hatalarını teşhis et. Dosya silmeden gerekli düzeltmeleri belirt. Hatalar:\n"+errors);SetProgress(80,"Analiz raporu kaydediliyor");lastReport=Path.Combine(Reports,$"build-{DateTime.Now:yyyyMMdd-HHmmss}.txt");await File.WriteAllTextAsync(lastReport,result,new UTF8Encoding(false));SetProgress(100,"Analiz tamamlandı");UpdateReportLink();Append("Otomatik teşhis tamamlandı. Rapor: "+lastReport);await UnloadAsync();}
    string CollectErrors(){StringBuilder s=new();foreach(string f in Directory.EnumerateFiles(Project,"*.log",SearchOption.TopDirectoryOnly)){if(DateTime.Now-File.GetLastWriteTime(f)>TimeSpan.FromMinutes(10))continue;foreach(string l in File.ReadLines(f).TakeLast(120))if(l.Contains("error ",StringComparison.OrdinalIgnoreCase)||l.Contains("başarısız",StringComparison.OrdinalIgnoreCase)||l.Contains("basarisiz",StringComparison.OrdinalIgnoreCase))s.AppendLine(l);}string t=s.ToString();if(t.Length==0)return"";string h=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(t)));if(h==lastErrorHash)return"";lastErrorHash=h;return t;}
    async Task<string> AskAsync(string prompt){await EnsureOllamaAsync();string body=JsonSerializer.Serialize(new{model=Model,messages=new[]{new{role="user",content=prompt}},stream=false,keep_alive="0s",options=new{temperature=0,num_ctx=4096}});using HttpResponseMessage r=await ollama.PostAsync("/api/chat",new StringContent(body,Encoding.UTF8,"application/json"));string raw=await r.Content.ReadAsStringAsync();r.EnsureSuccessStatusCode();using JsonDocument d=JsonDocument.Parse(raw);return d.RootElement.GetProperty("message").GetProperty("content").GetString()??"";}
    async Task EnsureOllamaAsync(){try{using var r=await ollama.GetAsync("/api/tags");if(r.IsSuccessStatusCode)return;}catch{}string exe=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","Ollama","ollama.exe");if(File.Exists(exe))Process.Start(new ProcessStartInfo(exe){UseShellExecute=true});for(int i=0;i<15;i++){await Task.Delay(1000);try{using var r=await ollama.GetAsync("/api/tags");if(r.IsSuccessStatusCode)return;}catch{}}throw new InvalidOperationException("Ollama başlatılamadı.");}
    async Task UnloadAsync(){try{Process.Start(new ProcessStartInfo("ollama","stop "+Model){UseShellExecute=false,CreateNoWindow=true});await Task.Delay(1000);}catch{}}
    void Append(string text){if(InvokeRequired){BeginInvoke(()=>Append(text));return;}string line=$"[{DateTime.Now:HH:mm}] {text}{Environment.NewLine}{Environment.NewLine}";output.AppendText(line);try{File.AppendAllText(Path.Combine(Logs,$"agentwatcher-{DateTime.Today:yyyyMMdd}.log"),line,new UTF8Encoding(false));}catch{}}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Auto)]struct MEMORYSTATUSEX{public uint dwLength,dwMemoryLoad;public ulong ullTotalPhys,ullAvailPhys,ullTotalPageFile,ullAvailPageFile,ullTotalVirtual,ullAvailVirtual,ullAvailExtendedVirtual;}[DllImport("kernel32.dll",CharSet=CharSet.Auto,SetLastError=true)]static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX v);static ulong AvailableMemory(){MEMORYSTATUSEX m=new(){dwLength=(uint)Marshal.SizeOf<MEMORYSTATUSEX>()};return GlobalMemoryStatusEx(ref m)?m.ullAvailPhys:0;}
}
internal sealed class SharedState
{
    static readonly string Dir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ArsanGazERP");static readonly string FilePath=Path.Combine(Dir,"connection-state.json");public string Account{get;set;}="";public string ExcelPath{get;set;}="";
    public static SharedState Load(){try{return File.Exists(FilePath)?JsonSerializer.Deserialize<SharedState>(File.ReadAllText(FilePath))??new():new();}catch{return new();}}
    public void Save(){Directory.CreateDirectory(Dir);File.WriteAllText(FilePath,JsonSerializer.Serialize(this,new JsonSerializerOptions{WriteIndented=true}),new UTF8Encoding(false));}
}
internal static class MicrosoftGraph
{
    const string ClientId="3eaf1f8a-fbc8-40f0-9759-e134542606bf";static readonly string[] Scopes=["User.Read","Tasks.ReadWrite","Files.ReadWrite"];
    static IPublicClientApplication? app;static MsalCacheHelper? helper;
    static async Task<IPublicClientApplication> GetAppAsync(){if(app!=null)return app;app=PublicClientApplicationBuilder.Create(ClientId).WithAuthority(AadAuthorityAudience.AzureAdAndPersonalMicrosoftAccount).WithRedirectUri("http://localhost").Build();string dir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ArsanGazERP","Auth");Directory.CreateDirectory(dir);var props=new StorageCreationPropertiesBuilder("msalcache.bin3",dir).Build();helper=await MsalCacheHelper.CreateAsync(props);helper.RegisterCache(app.UserTokenCache);return app;}
    public static async Task<string> ConnectAsync(bool force){var a=await GetAppAsync();IAccount? account=(await a.GetAccountsAsync()).FirstOrDefault();AuthenticationResult auth;try{if(force||account==null)throw new MsalUiRequiredException("interactive","Interactive required");auth=await a.AcquireTokenSilent(Scopes,account).ExecuteAsync();}catch{auth=await a.AcquireTokenInteractive(Scopes).WithPrompt(force?Prompt.Consent:Prompt.SelectAccount).ExecuteAsync();}return auth.Account.Username;}
    public static async Task<string?> TryConnectSilentAsync(){var a=await GetAppAsync();IAccount? account=(await a.GetAccountsAsync()).FirstOrDefault();if(account==null)return null;await a.AcquireTokenSilent(Scopes,account).ExecuteAsync();return account.Username;}
    public static async Task SignOutAsync(){var a=await GetAppAsync();foreach(IAccount x in await a.GetAccountsAsync())await a.RemoveAsync(x);}
}
