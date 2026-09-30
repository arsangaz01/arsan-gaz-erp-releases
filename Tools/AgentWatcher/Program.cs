using Microsoft.Identity.Client;
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
    [STAThread] static void Main()
    {
        ApplicationConfiguration.Initialize();
        using Mutex mutex = new(true, "Local\\ArsanGazERP.AgentWatcher", out bool first);
        if (!first) return;
        Application.Run(new WatcherForm());
    }
}

internal sealed class WatcherForm : Form
{
    const string Project = @"C:\ArsanGazERP\ArsanGazERP_V2_Duzeltilmis";
    const string Root = @"C:\ArsanGazERP\ArsanGazERP_V2_Duzeltilmis\Tools\AgentWatcherData";
    const string Model = "qwen2.5-coder:1.5b";
    readonly string Queue = Path.Combine(Root, "Queue");
    readonly string Logs = Path.Combine(Root, "Logs");
    readonly string Reports = Path.Combine(Root, "Reports");
    readonly TextBox output = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    readonly TextBox input = new() { Multiline = true, Height = 70, Dock = DockStyle.Bottom };
    readonly Button add = new() { Text = "Görev Ekle / Sohbet Et", Height = 42, Dock = DockStyle.Bottom };
    readonly Button connect = new() { Text = "Microsoft 365 Bağlan ve Görevleri Eşitle", Height = 42, Dock = DockStyle.Bottom };
    readonly Button openErp = new() { Text = "Güncel ERP'yi Aç", Height = 42, Dock = DockStyle.Bottom };
    readonly Label status = new() { Text = "Başlatılıyor", Height = 34, Dock = DockStyle.Top, Padding = new Padding(10, 9, 0, 0) };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 15000 };
    readonly HttpClient ollama = new() { BaseAddress = new Uri("http://localhost:11434"), Timeout = TimeSpan.FromMinutes(12) };
    bool busy; string lastErrorHash = "";

    public WatcherForm()
    {
        Text = "Arsan Gaz ERP AgentWatcher V7.3.4"; Width = 940; Height = 720; StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(output); Controls.Add(status); Controls.Add(connect); Controls.Add(add); Controls.Add(openErp); Controls.Add(input);
        Directory.CreateDirectory(Queue); Directory.CreateDirectory(Logs); Directory.CreateDirectory(Reports);
        add.Click += async (_, _) => await AddTaskAsync();
        connect.Click += async (_, _) => await SyncMicrosoft365Async();
        openErp.Click += (_, _) => OpenCurrentErp();
        timer.Tick += async (_, _) => await CheckAsync();
        Load += async (_, _) => { timer.Start(); Append("AgentWatcher hazır. Görev işlemcisi etkin."); status.Text = "Hazır"; await CheckAsync(); };
        FormClosing += (_, e) => { e.Cancel = true; Hide(); };
    }

    async Task AddTaskAsync()
    {
        string text = input.Text.Trim(); if (text.Length == 0) return; input.Clear();
        string file = Path.Combine(Queue, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.task.txt");
        await File.WriteAllTextAsync(file, text, new UTF8Encoding(false)); Append("Görev kuyruğa eklendi: " + text);
        await CheckAsync();
    }

    async Task SyncMicrosoft365Async()
    {
        try
        {
            status.Text = "Microsoft 365 bağlantısı kuruluyor";
            string json = await MicrosoftGraph.ReadTasksAsync();
            await File.WriteAllTextAsync(Path.Combine(Reports, "m365-tasks-latest.json"), json, new UTF8Encoding(false));
            Append("Microsoft 365 görev listeleri eşitlendi."); status.Text = "Microsoft 365 bağlı";
        }
        catch (Exception ex) { Append("Microsoft 365 hatası: " + ex.GetBaseException().Message); status.Text = "Microsoft 365 bağlantısı tamamlanamadı"; }
    }

    async Task CheckAsync()
    {
        if (busy || AvailableMemory() < 2UL * 1024 * 1024 * 1024 || Process.GetProcessesByName("devenv").Length > 0) return;
        string? task = Directory.EnumerateFiles(Queue, "*.task.txt").OrderBy(x => x).FirstOrDefault();
        if (task != null) { busy = true; try { await RunTaskAsync(task); } catch(Exception ex) { Append("Görev hatası: " + ex.GetBaseException().Message); } finally { busy = false; status.Text = "Hazır"; } return; }
        string errors = CollectErrors(); if (errors.Length == 0) return;
        busy = true; try { await AnalyzeAsync(errors); } catch(Exception ex) { Append("Analiz hatası: " + ex.GetBaseException().Message); } finally { busy = false; status.Text = "Hazır"; }
    }

    async Task RunTaskAsync(string file)
    {
        string request = await File.ReadAllTextAsync(file); status.Text = "Görev işleniyor";
        string result = IsProjectStatusRequest(request) ? BuildProjectStatusReport(request) : await AskAsync("Arsan Gaz ERP .NET 10 WPF sistemi için güvenli ve uygulanabilir görev planı üret. Silme, ödeme, e-posta gönderme, fatura ve yetki yükseltme yapma. İstek: " + request);
        string report = Path.Combine(Reports, $"task-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        await File.WriteAllTextAsync(report, result, new UTF8Encoding(false));
        File.Move(file, file.Replace(".task.txt", ".done.txt"), true); Append("Görev tamamlandı. Rapor: " + report); Append(result);
        await UnloadAsync();
    }

    static bool IsProjectStatusRequest(string text) => text.Contains("proje durum", StringComparison.OrdinalIgnoreCase) || text.Contains("publish", StringComparison.OrdinalIgnoreCase) || text.Contains("EXE", StringComparison.OrdinalIgnoreCase);

    static string BuildProjectStatusReport(string request)
    {
        StringBuilder s = new(); s.AppendLine("ARSAN GAZ ERP PROJE DURUM RAPORU"); s.AppendLine("Oluşturma: " + DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss")); s.AppendLine("İstek: " + request); s.AppendLine();
        string csproj = Path.Combine(Project, "ArsanGazERP.csproj"); s.AppendLine("Proje dosyası: " + (File.Exists(csproj) ? "VAR" : "YOK") + " | " + csproj);
        string current = Path.Combine(Project, "publish", "current", "ArsanGazERP.exe"); s.AppendLine("Sabit güncel EXE: " + (File.Exists(current) ? "VAR" : "YOK") + " | " + current);
        var exes = Directory.EnumerateFiles(Project, "ArsanGazERP.exe", SearchOption.AllDirectories).Where(x => !x.Contains("\\bin\\") && !x.Contains("\\obj\\") && !x.Contains("\\Tools\\")).Select(x => new FileInfo(x)).OrderByDescending(x => x.LastWriteTime).ToList();
        if(exes.Count == 0) s.AppendLine("Çalıştırılabilir ERP EXE bulunamadı."); else { s.AppendLine("En yeni ERP EXE: " + exes[0].FullName); s.AppendLine("Değiştirme: " + exes[0].LastWriteTime.ToString("dd.MM.yyyy HH:mm:ss")); }
        var publishes = Directory.EnumerateDirectories(Project, "publish*", SearchOption.TopDirectoryOnly).OrderBy(x => x).ToList(); s.AppendLine("Publish klasörü sayısı: " + publishes.Count); foreach(string p in publishes)s.AppendLine(" - " + p);
        return s.ToString();
    }

    void OpenCurrentErp()
    {
        string current = Path.Combine(Project, "publish", "current", "ArsanGazERP.exe");
        string? exe = File.Exists(current) ? current : Directory.EnumerateFiles(Project, "ArsanGazERP.exe", SearchOption.AllDirectories).Where(x => !x.Contains("\\bin\\") && !x.Contains("\\obj\\") && !x.Contains("\\Tools\\")).OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
        if(exe is null){Append("Güncel ERP EXE bulunamadı.");return;} Process.Start(new ProcessStartInfo(exe){UseShellExecute=true,WorkingDirectory=Path.GetDirectoryName(exe)!}); Append("ERP açıldı: "+exe);
    }

    async Task AnalyzeAsync(string errors)
    {
        status.Text = "Derleme hatası analiz ediliyor";
        string result = await AskAsync("Aşağıdaki .NET 10 WPF derleme hatalarını teşhis et. Dosya silmeden gerekli düzeltmeleri belirt. Hatalar:\n" + errors);
        string report=Path.Combine(Reports, $"build-{DateTime.Now:yyyyMMdd-HHmmss}.txt"); await File.WriteAllTextAsync(report, result, new UTF8Encoding(false));
        Append("Otomatik teşhis tamamlandı. Rapor: "+report); await UnloadAsync();
    }

    string CollectErrors()
    {
        StringBuilder sb = new(); foreach (string file in Directory.EnumerateFiles(Project, "*.log", SearchOption.TopDirectoryOnly)) { if (DateTime.Now - File.GetLastWriteTime(file) > TimeSpan.FromMinutes(10)) continue; foreach (string line in File.ReadLines(file).TakeLast(120)) if (line.Contains("error ", StringComparison.OrdinalIgnoreCase) || line.Contains("başarısız", StringComparison.OrdinalIgnoreCase) || line.Contains("basarisiz", StringComparison.OrdinalIgnoreCase)) sb.AppendLine(line); }
        string text = sb.ToString(); if (text.Length == 0) return ""; string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))); if (hash == lastErrorHash) return ""; lastErrorHash = hash; return text;
    }

    async Task<string> AskAsync(string prompt)
    {
        await EnsureOllamaAsync();
        string body = JsonSerializer.Serialize(new { model = Model, messages = new[] { new { role = "user", content = prompt } }, stream = false, keep_alive = "0s", options = new { temperature = 0, num_ctx = 4096 } });
        using HttpResponseMessage response = await ollama.PostAsync("/api/chat", new StringContent(body, Encoding.UTF8, "application/json")); string raw = await response.Content.ReadAsStringAsync(); response.EnsureSuccessStatusCode(); using JsonDocument doc = JsonDocument.Parse(raw); return doc.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "";
    }

    async Task EnsureOllamaAsync()
    {
        try { using var r=await ollama.GetAsync("/api/tags"); if(r.IsSuccessStatusCode)return; } catch { }
        string exe=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","Ollama","ollama.exe"); if(File.Exists(exe)) Process.Start(new ProcessStartInfo(exe){UseShellExecute=true}); else Process.Start(new ProcessStartInfo("ollama","serve"){UseShellExecute=false,CreateNoWindow=true});
        for(int i=0;i<15;i++){await Task.Delay(1000);try{using var r=await ollama.GetAsync("/api/tags");if(r.IsSuccessStatusCode)return;}catch{}}
        throw new InvalidOperationException("Ollama yerel API başlatılamadı.");
    }

    async Task UnloadAsync(){try{Process.Start(new ProcessStartInfo("ollama", "stop " + Model){UseShellExecute=false,CreateNoWindow=true});await Task.Delay(1000);}catch{}}
    void Append(string text){if(InvokeRequired){BeginInvoke(()=>Append(text));return;}string line=$"[{DateTime.Now:HH:mm}] {text}{Environment.NewLine}{Environment.NewLine}";output.AppendText(line);try{File.AppendAllText(Path.Combine(Logs,$"agentwatcher-{DateTime.Today:yyyyMMdd}.log"),line,new UTF8Encoding(false));}catch{}}
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)] struct MEMORYSTATUSEX{public uint dwLength;public uint dwMemoryLoad;public ulong ullTotalPhys;public ulong ullAvailPhys;public ulong ullTotalPageFile;public ulong ullAvailPageFile;public ulong ullTotalVirtual;public ulong ullAvailVirtual;public ulong ullAvailExtendedVirtual;}
    [DllImport("kernel32.dll",CharSet=CharSet.Auto,SetLastError=true)]static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX value);
    static ulong AvailableMemory(){MEMORYSTATUSEX m=new(){dwLength=(uint)Marshal.SizeOf<MEMORYSTATUSEX>()};return GlobalMemoryStatusEx(ref m)?m.ullAvailPhys:0;}
}

internal static class MicrosoftGraph
{
    const string ClientId = "3eaf1f8a-fbc8-40f0-9759-e134542606bf"; static readonly string[] Scopes = ["User.Read", "Tasks.ReadWrite"];
    public static async Task<string> ReadTasksAsync()
    {
        IPublicClientApplication app = PublicClientApplicationBuilder.Create(ClientId).WithAuthority(AadAuthorityAudience.AzureAdAndPersonalMicrosoftAccount).WithRedirectUri("http://localhost").Build(); IAccount? account = (await app.GetAccountsAsync()).FirstOrDefault(); AuthenticationResult auth; try { auth = await app.AcquireTokenSilent(Scopes, account).ExecuteAsync(); } catch { auth = await app.AcquireTokenInteractive(Scopes).WithPrompt(Prompt.SelectAccount).ExecuteAsync(); }
        using HttpClient http = new(){BaseAddress=new Uri("https://graph.microsoft.com/v1.0/")}; http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",auth.AccessToken); using HttpResponseMessage listsResponse=await http.GetAsync("me/todo/lists");string listsRaw=await listsResponse.Content.ReadAsStringAsync();listsResponse.EnsureSuccessStatusCode();using JsonDocument listsDoc=JsonDocument.Parse(listsRaw);List<object> result=[];
        foreach(JsonElement list in listsDoc.RootElement.GetProperty("value").EnumerateArray()){string id=list.GetProperty("id").GetString()??"";string name=list.GetProperty("displayName").GetString()??"";using HttpResponseMessage tasksResponse=await http.GetAsync($"me/todo/lists/{id}/tasks?$filter=status ne 'completed'");string tasksRaw=await tasksResponse.Content.ReadAsStringAsync();if(tasksResponse.IsSuccessStatusCode){using JsonDocument tasksDoc=JsonDocument.Parse(tasksRaw);result.Add(new{list=name,tasks=tasksDoc.RootElement.Clone()});}}
        return JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true});
    }
}
