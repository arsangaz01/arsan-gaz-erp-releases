using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ArsanGazERP.AgentWatcher;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        using Mutex m = new(true, "Local\\ArsanGazERP.AgentWatcher.V760", out bool first);
        if (!first) return;
        Application.ThreadException += (_, e) => AppLog.Write("UI", e.Exception.ToString());
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Write("FATAL", e.ExceptionObject?.ToString() ?? "Unknown");
        Application.Run(new ControlCenterForm());
    }
}

internal static class Paths
{
    public const string Project = @"C:\ArsanGazERP\ArsanGazERP_V2_Duzeltilmis";
    public static readonly string Root = Path.Combine(Project, "Tools", "AgentWatcherData");
    public static readonly string Queue = Path.Combine(Root, "Queue");
    public static readonly string Reports = Path.Combine(Root, "Reports");
    public static readonly string Logs = Path.Combine(Root, "Logs");
    public static readonly string Settings = Path.Combine(Root, "settings.json");
    public static readonly string State = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArsanGazERP", "connection-state.json");
    public static readonly string ErpExe = Path.Combine(Project, "publish", "current", "ArsanGazERP.exe");
    public static void Ensure() { Directory.CreateDirectory(Queue); Directory.CreateDirectory(Reports); Directory.CreateDirectory(Logs); Directory.CreateDirectory(Path.GetDirectoryName(State)!); }
}

internal sealed class ControlCenterForm : Form
{
    readonly AppSettings settings = AppSettings.Load();
    readonly SharedState state = SharedState.Load();
    readonly HttpClient ollama = new() { BaseAddress = new Uri("http://localhost:11434"), Timeout = TimeSpan.FromMinutes(12) };
    readonly HttpClient nvidia = new() { BaseAddress = new Uri("https://integrate.api.nvidia.com/v1/"), Timeout = TimeSpan.FromMinutes(8) };
    readonly CancellationTokenSource closing = new();
    readonly System.Windows.Forms.Timer timer = new() { Interval = 5000 };
    readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    readonly Label overall = new() { Dock = DockStyle.Top, Height = 36, Font = new Font("Segoe UI", 10, FontStyle.Bold), Padding = new Padding(12, 10, 0, 0) };
    readonly DataGridView queueGrid = NewGrid();
    readonly TextBox logs = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Font = new Font("Consolas", 9), WordWrap = false };
    readonly TextBox taskInput = new() { Multiline = true, Height = 100, Dock = DockStyle.Top, PlaceholderText = "Görev açıklamasını yazın" };
    readonly ComboBox taskType = NewCombo("ERP Analizi", "CRM Analizi", "Microsoft 365 Özeti", "Excel Analizi", "Kod Analizi", "Satış Tahmini", "Görev Planlama", "Genel AI Görevi");
    readonly ComboBox priority = NewCombo("Normal", "Yüksek", "Acil");
    readonly ComboBox provider = NewCombo("Otomatik", "NVIDIA NIM", "Ollama");
    readonly Label taskStatus = new() { AutoSize = true, Text = "Hazır (%0)", Padding = new Padding(8) };
    readonly ProgressBar progress = new() { Dock = DockStyle.Top, Height = 22 };
    readonly Dictionary<string, Label> cards = new();
    bool paused, busy, allowExit;
    string? activeTask, lastReport;

    public ControlCenterForm()
    {
        Paths.Ensure();
        Text = "Arsan Gaz ERP V7.6.0 Agent Control Center";
        Width = 1240; Height = 820; MinimumSize = new Size(1050, 700); StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(tabs); Controls.Add(overall);
        tabs.TabPages.Add(BuildGeneral()); tabs.TabPages.Add(BuildTasks()); tabs.TabPages.Add(BuildConnections()); tabs.TabPages.Add(BuildErp()); tabs.TabPages.Add(BuildLogs()); tabs.TabPages.Add(BuildSettings());
        timer.Tick += async (_, _) => { await RefreshStatusAsync(); await ProcessQueueAsync(); };
        Shown += async (_, _) => { Append("Agent Control Center V7.6.0 başlatıldı."); timer.Start(); await MicrosoftGraph.TrySilentAsync(state); await RefreshStatusAsync(); RefreshQueue(); };
        FormClosing += (_, e) => { if (!allowExit && settings.RunInBackground) { e.Cancel = true; Hide(); Notify("AgentWatcher arka planda çalışmaya devam ediyor."); } };
    }

    TabPage BuildGeneral()
    {
        TabPage page = new("Genel");
        TableLayoutPanel grid = new() { Dock = DockStyle.Top, Height = 300, ColumnCount = 4, RowCount = 2, Padding = new Padding(18) };
        grid.ColumnStyles.Clear(); for (int i=0;i<4;i++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,25));
        foreach (string key in new[] { "ERP", "AgentWatcher", "Microsoft 365", "İzinler", "Excel", "NVIDIA NIM", "Ollama", "Görev Kuyruğu" }) grid.Controls.Add(StatusCard(key));
        FlowLayoutPanel actions = Flow(Button("Durumları Yenile", async () => await RefreshStatusAsync()), Button("Test Görevi", CreateTestTask), Button("Rapor Klasörü", () => Open(Paths.Reports)), Button("Log Klasörü", () => Open(Paths.Logs)));
        page.Controls.Add(actions); page.Controls.Add(grid); return page;
    }

    Control StatusCard(string name)
    {
        Panel p = new() { Margin = new Padding(8), Padding = new Padding(12), BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Dock = DockStyle.Fill };
        Label n = new() { Text = name, Dock = DockStyle.Top, Height = 28, Font = new Font("Segoe UI", 10, FontStyle.Bold) };
        Label s = new() { Text = "Denetleniyor", Dock = DockStyle.Fill, Font = new Font("Segoe UI", 10), ForeColor = Color.DarkGoldenrod };
        p.Controls.Add(s); p.Controls.Add(n); cards[name] = s; return p;
    }

    TabPage BuildTasks()
    {
        TabPage page = new("Görevler");
        Panel top = new() { Dock = DockStyle.Top, Height = 220, Padding = new Padding(14) };
        FlowLayoutPanel choices = Flow(Labeled("Görev türü", taskType), Labeled("Öncelik", priority), Labeled("AI sağlayıcısı", provider));
        choices.Dock = DockStyle.Top; choices.Height = 62;
        FlowLayoutPanel buttons = Flow(Button("Çalıştır", async () => await AddTaskAsync()), Button("Duraklat", () => { paused=true; Append("Görev işleme duraklatıldı."); }), Button("Devam Et", () => { paused=false; Append("Görev işleme devam ediyor."); }), Button("Aktif Görevi İptal Et", CancelActive), Button("Başarısızları Tekrar Dene", RetryFailed), Button("Kuyruğu Yenile", RefreshQueue));
        buttons.Dock = DockStyle.Bottom; buttons.Height = 48;
        top.Controls.Add(buttons); top.Controls.Add(taskInput); top.Controls.Add(choices);
        Panel status = new() { Dock = DockStyle.Top, Height = 52 }; status.Controls.Add(taskStatus); status.Controls.Add(progress);
        page.Controls.Add(queueGrid); page.Controls.Add(status); page.Controls.Add(top); return page;
    }

    TabPage BuildConnections()
    {
        TabPage page = new("Bağlantılar");
        TableLayoutPanel layout = new() { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(16), AutoScroll = true };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));
        layout.Controls.Add(Group("Microsoft 365", Button("Giriş Yap", async () => { state.Account=await MicrosoftGraph.ConnectAsync(false); state.Save(); await RefreshStatusAsync(); }), Button("İzinleri Yenile", async () => { state.Account=await MicrosoftGraph.ConnectAsync(true); state.Save(); await RefreshStatusAsync(); }), Button("Profil Testi", async () => ShowResult(await MicrosoftGraph.GetAsync("me?$select=displayName,mail,userPrincipalName"))), Button("Günlük Özet", async () => ShowResult(await MicrosoftGraph.SummaryAsync())), Button("Oturumu Kapat", async () => { await MicrosoftGraph.SignOutAsync(); state.Account="";state.Save();await RefreshStatusAsync(); })));
        layout.Controls.Add(Group("Excel", Button("Excel Seç", SelectExcel), Button("Bağlantıyı Test Et", TestExcel), Button("Sayfaları Listele", ListExcelSheets), Button("Excel Klasörünü Aç", () => { if(File.Exists(state.ExcelPath)) Open(Path.GetDirectoryName(state.ExcelPath)!); }), Button("Bağlantıyı Kaldır", () => { state.ExcelPath="";state.Save();_ = RefreshStatusAsync(); })));
        layout.Controls.Add(Group("NVIDIA NIM", Labeled("API anahtarı", NewNvidiaKeyBox()), Labeled("Model", NewNvidiaModelBox()), Button("Kaydet ve Test Et", async () => await SaveAndTestNvidiaAsync()), Button("Bağlantıyı Test Et", async () => ShowResult(await AskNvidiaAsync("Yalnızca TAMAM yaz."))), Button("Anahtarı Kaldır", RemoveNvidia)));
        layout.Controls.Add(Group("Ollama", Button("Ollama Başlat", async () => { await EnsureOllamaAsync(); await RefreshStatusAsync(); }), Button("Ollama Durdur", StopOllama), Button("Model Listesi", async () => ShowResult(await GetOllamaModelsAsync())), Button("Yerel AI Testi", async () => ShowResult(await AskOllamaAsync("Yalnızca TAMAM yaz.")))));
        page.Controls.Add(layout); return page;
    }

    TextBox? nvidiaKey; ComboBox? nvidiaModel;
    TextBox NewNvidiaKeyBox() => nvidiaKey = new TextBox { Width=330, UseSystemPasswordChar=true, PlaceholderText="nvapi-..." };
    ComboBox NewNvidiaModelBox() => nvidiaModel = NewCombo(settings.NvidiaModel, "meta/llama-3.1-8b-instruct", "microsoft/phi-4-mini-instruct", "qwen/qwen2.5-coder-32b-instruct");

    TabPage BuildErp()
    {
        TabPage p=new("ERP");
        FlowLayoutPanel f=Flow(Button("ERP'yi Aç", OpenErp),Button("ERP'yi Kapat", StopErp),Button("ERP'yi Yeniden Başlat", RestartErp),Button("Publish Klasörünü Aç",()=>Open(Path.GetDirectoryName(Paths.ErpExe)!)),Button("Proje Durum Raporu",ProjectReport),Button("CRM Analiz Görevi",()=>QueuePreset("CRM Analizi","CRM fırsatları, riskli müşteriler ve bekleyen takipler için analiz raporu hazırla.")),Button("Satış Tahmini Görevi",()=>QueuePreset("Satış Tahmini","Mevcut ERP verilerine göre güvenli satış tahmini ve takip planı hazırla.")));
        p.Controls.Add(f);return p;
    }

    TabPage BuildLogs()
    {
        TabPage p=new("Günlükler");
        FlowLayoutPanel f=Flow(Button("Yenile",LoadLogs),Button("Yalnızca Hatalar",LoadErrors),Button("Temizle",()=>logs.Clear()),Button("Log Dosyasını Aç",()=>Open(AppLog.Current)),Button("Log Klasörünü Aç",()=>Open(Paths.Logs)),Button("Dışa Aktar",ExportLogs));
        p.Controls.Add(logs);p.Controls.Add(f);LoadLogs();return p;
    }

    TabPage BuildSettings()
    {
        TabPage p=new("Ayarlar");
        CheckBox startWindows=new(){Text="Windows ile başlat",Checked=settings.StartWithWindows,AutoSize=true};
        CheckBox startErp=new(){Text="ERP ile birlikte başlat",Checked=settings.StartErp,AutoSize=true};
        CheckBox minimized=new(){Text="Simge durumunda başlat",Checked=settings.StartMinimized,AutoSize=true};
        CheckBox background=new(){Text="Kapatınca arka planda çalış",Checked=settings.RunInBackground,AutoSize=true};
        NumericUpDown interval=new(){Minimum=3,Maximum=300,Value=Math.Clamp(settings.PollSeconds,3,300),Width=100};
        ComboBox defaultProvider=NewCombo(settings.DefaultProvider,"Otomatik","NVIDIA NIM","Ollama");
        FlowLayoutPanel f=Flow(startWindows,startErp,minimized,background,Labeled("Tarama aralığı (sn)",interval),Labeled("Varsayılan sağlayıcı",defaultProvider),Button("Ayarları Kaydet",()=>{settings.StartWithWindows=startWindows.Checked;settings.StartErp=startErp.Checked;settings.StartMinimized=minimized.Checked;settings.RunInBackground=background.Checked;settings.PollSeconds=(int)interval.Value;settings.DefaultProvider=defaultProvider.Text;settings.Save();timer.Interval=settings.PollSeconds*1000;StartupManager.Apply(settings);Append("Ayarlar kaydedildi.");}),Button("Watcher'ı Tamamen Kapat",()=>{allowExit=true;Application.Exit();}));
        p.Controls.Add(f);return p;
    }

    async Task RefreshStatusAsync()
    {
        bool erp=Process.GetProcessesByName("ArsanGazERP").Any(); bool watcher=true; bool excel=File.Exists(state.ExcelPath); bool nv=!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NVIDIA_API_KEY",EnvironmentVariableTarget.User)); bool ol=await IsOllamaAsync();
        SetCard("ERP",erp?"Çalışıyor":"Kapalı",erp);SetCard("AgentWatcher",watcher?"Çalışıyor":"Kapalı",watcher);SetCard("Microsoft 365",string.IsNullOrWhiteSpace(state.Account)?"Bağlı değil":"Bağlı: "+state.Account,!string.IsNullOrWhiteSpace(state.Account));SetCard("İzinler",string.IsNullOrWhiteSpace(state.Account)?"Eksik":"Verildi",!string.IsNullOrWhiteSpace(state.Account));SetCard("Excel",excel?"Bağlı: "+Path.GetFileName(state.ExcelPath):"Seçilmedi",excel);SetCard("NVIDIA NIM",nv?"Yapılandırıldı":"Bağlı değil",nv);SetCard("Ollama",ol?"Çalışıyor":"Kapalı",ol);int q=Directory.EnumerateFiles(Paths.Queue,"*.task.json").Count();SetCard("Görev Kuyruğu",q+" bekleyen",true);overall.Text=$"ERP {(erp?"çalışıyor":"kapalı")}  |  M365 {(string.IsNullOrWhiteSpace(state.Account)?"bağlı değil":"bağlı")}  |  Excel {(excel?"bağlı":"seçilmedi")}  |  NVIDIA {(nv?"hazır":"yok")}  |  Ollama {(ol?"hazır":"kapalı")}";
    }

    void SetCard(string key,string text,bool ok){if(cards.TryGetValue(key,out Label? l)){l.Text=text+"\nSon kontrol: "+DateTime.Now.ToString("HH:mm:ss");l.ForeColor=ok?Color.DarkGreen:Color.Firebrick;}}
    async Task AddTaskAsync(){if(string.IsNullOrWhiteSpace(taskInput.Text))return;TaskItem item=new(){Id=Guid.NewGuid().ToString("N"),Title=taskInput.Text.Trim(),Type=taskType.Text,Priority=priority.Text,Provider=provider.Text,Status="Bekliyor",Created=DateTime.Now};await SaveTaskAsync(item);taskInput.Clear();RefreshQueue();await ProcessQueueAsync();}
    void QueuePreset(string type,string text){taskType.Text=type;taskInput.Text=text;_ = AddTaskAsync();}
    void CreateTestTask(){taskType.Text="Genel AI Görevi";taskInput.Text="AgentWatcher bağlantı ve görev işleme testi. Yalnızca test sonucu üret.";_ = AddTaskAsync();}
    async Task SaveTaskAsync(TaskItem item){string f=Path.Combine(Paths.Queue,item.Id+".task.json");await File.WriteAllTextAsync(f,JsonSerializer.Serialize(item,new JsonSerializerOptions{WriteIndented=true}),new UTF8Encoding(false));Append("Görev kuyruğa eklendi: "+item.Title);}
    async Task ProcessQueueAsync(){if(paused||busy)return;string? f=Directory.EnumerateFiles(Paths.Queue,"*.task.json").OrderBy(x=>x).FirstOrDefault();if(f==null)return;busy=true;activeTask=f;try{TaskItem item=JsonSerializer.Deserialize<TaskItem>(await ReadSharedAsync(f))??throw new InvalidOperationException("Görev okunamadı.");SetProgress(10,"Görev okunuyor");item.Status="Çalışıyor";item.Started=DateTime.Now;await File.WriteAllTextAsync(f,JsonSerializer.Serialize(item));SetProgress(30,"Analiz ediliyor");string result=await ExecuteTaskAsync(item);SetProgress(80,"Rapor yazılıyor");lastReport=Path.Combine(Paths.Reports,$"{DateTime.Now:yyyyMMdd-HHmmss}-{item.Id}.txt");await File.WriteAllTextAsync(lastReport,result,new UTF8Encoding(false));item.Status="Tamamlandı";item.Completed=DateTime.Now;item.Report=lastReport;string done=f.Replace(".task.json",".done.json");await File.WriteAllTextAsync(done,JsonSerializer.Serialize(item,new JsonSerializerOptions{WriteIndented=true}));File.Delete(f);SetProgress(100,"Tamamlandı");Append("Görev tamamlandı: "+item.Title);Append(result);await Task.Delay(800);}catch(Exception ex){Append("Görev hatası: "+ex.GetBaseException().Message);try{if(activeTask!=null&&File.Exists(activeTask))File.Move(activeTask,activeTask.Replace(".task.json",".error.json"),true);}catch{}SetProgress(0,"Başarısız");}finally{busy=false;activeTask=null;RefreshQueue();SetProgress(0,"Hazır");}}
    async Task<string> ExecuteTaskAsync(TaskItem item){string prompt=$"Arsan Gaz ERP için güvenli ve uygulanabilir rapor üret. Görev türü: {item.Type}. Öncelik: {item.Priority}. Silme, ödeme, e-posta gönderme, fatura kesme veya yetki yükseltme yapma. İstek: {item.Title}";string p=item.Provider=="Otomatik"?settings.DefaultProvider:item.Provider;if(p=="Otomatik")p=!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NVIDIA_API_KEY",EnvironmentVariableTarget.User))?"NVIDIA NIM":"Ollama";if(p=="NVIDIA NIM"){try{return await AskNvidiaAsync(prompt);}catch(Exception ex){Append("NVIDIA başarısız, Ollama yedeğine geçiliyor: "+ex.Message);return await AskOllamaAsync(prompt);}}return await AskOllamaAsync(prompt);}
    void CancelActive(){if(activeTask==null){Append("Aktif görev yok.");return;}try{File.Move(activeTask,activeTask.Replace(".task.json",".cancelled.json"),true);Append("Aktif görev iptal edildi.");}catch(Exception ex){Append(ex.Message);}}
    void RetryFailed(){foreach(string f in Directory.EnumerateFiles(Paths.Queue,"*.error.json"))File.Move(f,f.Replace(".error.json",".task.json"),true);RefreshQueue();Append("Başarısız görevler yeniden kuyruğa alındı.");}
    void RefreshQueue(){var rows=Directory.EnumerateFiles(Paths.Queue,"*.json").Select(f=>{try{TaskItem? x=JsonSerializer.Deserialize<TaskItem>(File.ReadAllText(f));return new{x?.Created,x?.Type,x?.Priority,x?.Provider,x?.Status,x?.Title,File=Path.GetFileName(f)};}catch{return new{Created=(DateTime?)null,Type=(string?)"Hatalı",Priority=(string?)"",Provider=(string?)"",Status=(string?)"Okunamadı",Title=(string?)Path.GetFileName(f),File=Path.GetFileName(f)};}}).OrderByDescending(x=>x.Created).ToList();queueGrid.DataSource=rows;}
    void SelectExcel(){using OpenFileDialog d=new(){Title="ERP Excel çalışma kitabını seç",Filter="Excel (*.xlsm;*.xlsx)|*.xlsm;*.xlsx",CheckFileExists=true};if(d.ShowDialog(this)==DialogResult.OK){state.ExcelPath=d.FileName;state.Save();TestExcel();}}
    void TestExcel(){ShowResult(File.Exists(state.ExcelPath)?"Excel bağlantısı hazır: "+state.ExcelPath:"Excel dosyası bulunamadı.");}
    void ListExcelSheets(){if(!File.Exists(state.ExcelPath)){TestExcel();return;}ShowResult("Excel dosyası bağlı. Sayfa listesi ERP içindeki OpenXML servisi tarafından doğrulanır.\n"+state.ExcelPath);}
    async Task SaveAndTestNvidiaAsync(){if(nvidiaKey!=null&&!string.IsNullOrWhiteSpace(nvidiaKey.Text))Environment.SetEnvironmentVariable("NVIDIA_API_KEY",nvidiaKey.Text.Trim(),EnvironmentVariableTarget.User);if(nvidiaModel!=null&&!string.IsNullOrWhiteSpace(nvidiaModel.Text))settings.NvidiaModel=nvidiaModel.Text.Trim();settings.Save();nvidiaKey?.Clear();ShowResult(await AskNvidiaAsync("Yalnızca TAMAM yaz."));await RefreshStatusAsync();}
    void RemoveNvidia(){Environment.SetEnvironmentVariable("NVIDIA_API_KEY",null,EnvironmentVariableTarget.User);Append("NVIDIA API anahtarı kaldırıldı.");_ = RefreshStatusAsync();}
    async Task<string> AskNvidiaAsync(string prompt){string key=Environment.GetEnvironmentVariable("NVIDIA_API_KEY",EnvironmentVariableTarget.User)??throw new InvalidOperationException("NVIDIA API anahtarı ayarlanmamış.");using HttpRequestMessage req=new(HttpMethod.Post,"chat/completions");req.Headers.Authorization=new AuthenticationHeaderValue("Bearer",key);req.Content=new StringContent(JsonSerializer.Serialize(new{model=settings.NvidiaModel,messages=new[]{new{role="user",content=Sanitize(prompt)}},temperature=0.1,max_tokens=2500,stream=false}),Encoding.UTF8,"application/json");using HttpResponseMessage res=await nvidia.SendAsync(req);string raw=await res.Content.ReadAsStringAsync();if(!res.IsSuccessStatusCode)throw new InvalidOperationException($"NVIDIA HTTP {(int)res.StatusCode}: {raw}");using JsonDocument d=JsonDocument.Parse(raw);return d.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()??"";}
    async Task<string> AskOllamaAsync(string prompt){await EnsureOllamaAsync();string body=JsonSerializer.Serialize(new{model=settings.OllamaModel,messages=new[]{new{role="user",content=Sanitize(prompt)}},stream=false,keep_alive="0s",options=new{temperature=0,num_ctx=4096}});using HttpResponseMessage r=await ollama.PostAsync("/api/chat",new StringContent(body,Encoding.UTF8,"application/json"));string raw=await r.Content.ReadAsStringAsync();r.EnsureSuccessStatusCode();using JsonDocument d=JsonDocument.Parse(raw);return d.RootElement.GetProperty("message").GetProperty("content").GetString()??"";}
    async Task EnsureOllamaAsync(){if(await IsOllamaAsync())return;string exe=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","Ollama","ollama.exe");if(File.Exists(exe))Process.Start(new ProcessStartInfo(exe){UseShellExecute=true});for(int i=0;i<20;i++){await Task.Delay(750);if(await IsOllamaAsync())return;}throw new InvalidOperationException("Ollama başlatılamadı.");}
    async Task<bool> IsOllamaAsync(){try{using HttpResponseMessage r=await ollama.GetAsync("/api/tags");return r.IsSuccessStatusCode;}catch{return false;}}
    async Task<string> GetOllamaModelsAsync(){await EnsureOllamaAsync();return await ollama.GetStringAsync("/api/tags");}
    void StopOllama(){foreach(Process p in Process.GetProcessesByName("ollama")){try{p.Kill(true);}catch{}}Append("Ollama durduruldu.");_ = RefreshStatusAsync();}
    void OpenErp(){if(Process.GetProcessesByName("ArsanGazERP").Any()){Append("ERP zaten çalışıyor.");return;}if(!File.Exists(Paths.ErpExe)){Append("ERP EXE bulunamadı: "+Paths.ErpExe);return;}Process.Start(new ProcessStartInfo(Paths.ErpExe){UseShellExecute=true,WorkingDirectory=Path.GetDirectoryName(Paths.ErpExe)!});Append("ERP açıldı.");}
    void StopErp(){foreach(Process p in Process.GetProcessesByName("ArsanGazERP")){try{p.CloseMainWindow();if(!p.WaitForExit(5000))p.Kill(true);}catch{}}Append("ERP kapatıldı.");}
    void RestartErp(){StopErp();Task.Delay(800).ContinueWith(_=>BeginInvoke(OpenErp));}
    void ProjectReport(){StringBuilder s=new();s.AppendLine("ARSAN GAZ ERP PROJE DURUM RAPORU");s.AppendLine("Tarih: "+DateTime.Now);s.AppendLine("Proje: "+(File.Exists(Path.Combine(Paths.Project,"ArsanGazERP.csproj"))?"VAR":"YOK"));s.AppendLine("ERP EXE: "+(File.Exists(Paths.ErpExe)?"VAR":"YOK"));s.AppendLine("Watcher: ÇALIŞIYOR");s.AppendLine("Excel: "+(File.Exists(state.ExcelPath)?state.ExcelPath:"SEÇİLMEDİ"));lastReport=Path.Combine(Paths.Reports,$"project-{DateTime.Now:yyyyMMdd-HHmmss}.txt");File.WriteAllText(lastReport,s.ToString(),new UTF8Encoding(false));ShowResult(s.ToString());}
    void LoadLogs(){try{logs.Text=File.Exists(AppLog.Current)?ReadTail(AppLog.Current,500):"Henüz log yok.";}catch(Exception ex){logs.Text=ex.Message;}}
    void LoadErrors(){try{logs.Text=string.Join(Environment.NewLine,ReadTail(AppLog.Current,1000).Split('\n').Where(x=>x.Contains("HATA",StringComparison.OrdinalIgnoreCase)||x.Contains("ERROR",StringComparison.OrdinalIgnoreCase)||x.Contains("FATAL",StringComparison.OrdinalIgnoreCase)));}catch(Exception ex){logs.Text=ex.Message;}}
    void ExportLogs(){using SaveFileDialog d=new(){Filter="Metin (*.txt)|*.txt",FileName=$"AgentWatcher-Log-{DateTime.Now:yyyyMMdd-HHmmss}.txt"};if(d.ShowDialog(this)==DialogResult.OK)File.WriteAllText(d.FileName,logs.Text,new UTF8Encoding(false));}
    void SetProgress(int value,string text){if(InvokeRequired){BeginInvoke(()=>SetProgress(value,text));return;}progress.Value=Math.Clamp(value,0,100);taskStatus.Text=$"{text} (%{progress.Value})";}
    void Append(string text){if(InvokeRequired){BeginInvoke(()=>Append(text));return;}string line=$"[{DateTime.Now:HH:mm:ss}] {text}";logs.AppendText(line+Environment.NewLine);AppLog.Write("INFO",text);}
    void ShowResult(string text){if(InvokeRequired){BeginInvoke(()=>ShowResult(text));return;}logs.Text=text;tabs.SelectedTab=tabs.TabPages[4];}
    static async Task<string> ReadSharedAsync(string f){using FileStream fs=new(f,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);using StreamReader sr=new(fs,Encoding.UTF8,true);return await sr.ReadToEndAsync();}
    static string ReadTail(string f,int lines){if(!File.Exists(f))return"";using FileStream fs=new(f,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);using StreamReader sr=new(fs,Encoding.UTF8,true);Queue<string> q=new();while(sr.ReadLine() is string line){q.Enqueue(line);if(q.Count>lines)q.Dequeue();}return string.Join(Environment.NewLine,q);}
    static string Sanitize(string s){s=System.Text.RegularExpressions.Regex.Replace(s,@"nvapi-[A-Za-z0-9_-]+","[API_KEY_REMOVED]");s=System.Text.RegularExpressions.Regex.Replace(s,@"(?i)(password|secret|client_secret|connectionstring)\s*[:=]\s*[^\s;]+","$1=[REMOVED]");return s;}
    static void Open(string? p){if(!string.IsNullOrWhiteSpace(p)&&Directory.Exists(p))Process.Start(new ProcessStartInfo("explorer.exe",p){UseShellExecute=true});else if(!string.IsNullOrWhiteSpace(p)&&File.Exists(p))Process.Start(new ProcessStartInfo(p){UseShellExecute=true});}
    static void Notify(string text)=>MessageBox.Show(text,"Arsan Gaz ERP",MessageBoxButtons.OK,MessageBoxIcon.Information);
    static Button Button(string text,Action action){Button b=new(){Text=text,AutoSize=true,Height=38,Margin=new Padding(6),Padding=new Padding(10,0,10,0)};b.Click+=(_,_)=>action();return b;}
    static Button Button(string text,Func<Task> action){Button b=new(){Text=text,AutoSize=true,Height=38,Margin=new Padding(6),Padding=new Padding(10,0,10,0)};b.Click+=async(_,_)=>{try{b.Enabled=false;await action();}catch(Exception ex){MessageBox.Show(ex.GetBaseException().Message,"Hata");}finally{b.Enabled=true;}};return b;}
    static FlowLayoutPanel Flow(params Control[] c){FlowLayoutPanel f=new(){Dock=DockStyle.Top,AutoSize=true,WrapContents=true,Padding=new Padding(10)};f.Controls.AddRange(c);return f;}
    static Control Labeled(string label,Control c){Panel p=new(){Width=Math.Max(190,c.Width+20),Height=58,Margin=new Padding(5)};Label l=new(){Text=label,Dock=DockStyle.Top,Height=20};c.Dock=DockStyle.Bottom;p.Controls.Add(c);p.Controls.Add(l);return p;}
    static GroupBox Group(string title,params Control[] c){GroupBox g=new(){Text=title,Dock=DockStyle.Top,Height=Math.Max(190,55*c.Length),Padding=new Padding(12),Margin=new Padding(8)};FlowLayoutPanel f=Flow(c);f.Dock=DockStyle.Fill;f.FlowDirection=FlowDirection.TopDown;g.Controls.Add(f);return g;}
    static ComboBox NewCombo(params string[] values){ComboBox c=new(){Width=190,DropDownStyle=ComboBoxStyle.DropDownList};foreach(string v in values.Where(x=>!string.IsNullOrWhiteSpace(x)).Distinct())c.Items.Add(v);if(c.Items.Count>0)c.SelectedIndex=0;return c;}
    static DataGridView NewGrid()=>new(){Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,SelectionMode=DataGridViewSelectionMode.FullRowSelect,BackgroundColor=Color.White};
}

internal sealed class TaskItem{public string Id{get;set;}="";public string Title{get;set;}="";public string Type{get;set;}="";public string Priority{get;set;}="";public string Provider{get;set;}="";public string Status{get;set;}="";public DateTime Created{get;set;}public DateTime? Started{get;set;}public DateTime? Completed{get;set;}public string? Report{get;set;}}
internal sealed class SharedState{public string Account{get;set;}="";public string ExcelPath{get;set;}="";public static SharedState Load(){try{return File.Exists(Paths.State)?JsonSerializer.Deserialize<SharedState>(File.ReadAllText(Paths.State))??new():new();}catch{return new();}}public void Save(){Paths.Ensure();File.WriteAllText(Paths.State,JsonSerializer.Serialize(this,new JsonSerializerOptions{WriteIndented=true}),new UTF8Encoding(false));}}
internal sealed class AppSettings{public bool StartWithWindows{get;set;}=true;public bool StartErp{get;set;}=true;public bool StartMinimized{get;set;}=false;public bool RunInBackground{get;set;}=true;public int PollSeconds{get;set;}=5;public string DefaultProvider{get;set;}="Ollama";public string NvidiaModel{get;set;}="NVIDIA_CLOUD_UNAVAILABLE";public string OllamaModel{get;set;}="qwen2.5-coder:1.5b";public static AppSettings Load(){try{return File.Exists(Paths.Settings)?JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Paths.Settings))??new():new();}catch{return new();}}public void Save(){Paths.Ensure();File.WriteAllText(Paths.Settings,JsonSerializer.Serialize(this,new JsonSerializerOptions{WriteIndented=true}),new UTF8Encoding(false));}}
internal static class AppLog{public static string Current=>Path.Combine(Paths.Logs,$"agentwatcher-{DateTime.Today:yyyyMMdd}.log");public static void Write(string level,string text){try{Paths.Ensure();File.AppendAllText(Current,$"{DateTime.Now:O} [{level}] {text}{Environment.NewLine}",new UTF8Encoding(false));}catch{}}}
internal static class StartupManager{public static void Apply(AppSettings s){string startup=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup),"ArsanGazERP-AgentWatcher.cmd");if(!s.StartWithWindows){if(File.Exists(startup))File.Delete(startup);return;}string exe=Environment.ProcessPath??"";StringBuilder b=new("@echo off\r\ntimeout /t 8 /nobreak >nul\r\n");if(s.StartErp)b.AppendLine($"if exist \"{Paths.ErpExe}\" start \"\" /D \"{Path.GetDirectoryName(Paths.ErpExe)}\" \"{Paths.ErpExe}\"");b.AppendLine($"if exist \"{exe}\" start \"\" /D \"{Path.GetDirectoryName(exe)}\" \"{exe}\"");File.WriteAllText(startup,b.ToString(),Encoding.ASCII);}}
internal static class MicrosoftGraph
{
    const string ClientId="3eaf1f8a-fbc8-40f0-9759-e134542606bf";static readonly string[] Scopes={"User.Read","Tasks.ReadWrite","Files.ReadWrite","Mail.Read","Contacts.Read","Calendars.ReadWrite"};static IPublicClientApplication? app;static readonly HttpClient http=new(){BaseAddress=new Uri("https://graph.microsoft.com/v1.0/")};
    static async Task<IPublicClientApplication> GetAppAsync(){if(app!=null)return app;app=PublicClientApplicationBuilder.Create(ClientId).WithAuthority(AadAuthorityAudience.AzureAdAndPersonalMicrosoftAccount).WithRedirectUri("http://localhost").Build();string dir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ArsanGazERP","Auth");Directory.CreateDirectory(dir);MsalCacheHelper helper=await MsalCacheHelper.CreateAsync(new StorageCreationPropertiesBuilder("msalcache.bin3",dir).Build());helper.RegisterCache(app.UserTokenCache);return app;}
    public static async Task<string> ConnectAsync(bool force){var a=await GetAppAsync();IAccount? account=(await a.GetAccountsAsync()).FirstOrDefault();AuthenticationResult auth;try{if(force||account==null)throw new MsalUiRequiredException("interactive","Interactive required");auth=await a.AcquireTokenSilent(Scopes,account).ExecuteAsync();}catch{auth=await a.AcquireTokenInteractive(Scopes).WithPrompt(force?Prompt.Consent:Prompt.SelectAccount).ExecuteAsync();}return auth.Account.Username;}
    public static async Task TrySilentAsync(SharedState s){try{var a=await GetAppAsync();IAccount? account=(await a.GetAccountsAsync()).FirstOrDefault();if(account==null)return;await a.AcquireTokenSilent(Scopes,account).ExecuteAsync();s.Account=account.Username;s.Save();}catch{}}
    static async Task<string> TokenAsync(){var a=await GetAppAsync();IAccount? account=(await a.GetAccountsAsync()).FirstOrDefault();if(account==null)throw new InvalidOperationException("Önce Microsoft 365 giriş yapın.");return (await a.AcquireTokenSilent(Scopes,account).ExecuteAsync()).AccessToken;}
    public static async Task<string> GetAsync(string path){http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",await TokenAsync());using HttpResponseMessage r=await http.GetAsync(path);string t=await r.Content.ReadAsStringAsync();if(!r.IsSuccessStatusCode)throw new InvalidOperationException($"Graph HTTP {(int)r.StatusCode}: {t}");return JsonSerializer.Serialize(JsonDocument.Parse(t),new JsonSerializerOptions{WriteIndented=true});}
    public static async Task<string> SummaryAsync(){string profile=await GetAsync("me?$select=displayName,mail,userPrincipalName");string mail=await GetAsync("me/messages?$top=5&$select=subject,receivedDateTime,isRead");string calendar=await GetAsync("me/events?$top=5&$select=subject,start,end");return "PROFİL\r\n"+profile+"\r\n\r\nSON E-POSTALAR\r\n"+mail+"\r\n\r\nTAKVİM\r\n"+calendar;}
    public static async Task SignOutAsync(){var a=await GetAppAsync();foreach(IAccount x in await a.GetAccountsAsync())await a.RemoveAsync(x);}
}
