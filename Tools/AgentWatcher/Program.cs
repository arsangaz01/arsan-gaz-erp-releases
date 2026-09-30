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
    const string Root = @"C:\ArsanGazERP\AgentWatcherData";
    const string Model = "qwen2.5-coder:1.5b";
    readonly string Queue = Path.Combine(Root, "Queue");
    readonly string Logs = Path.Combine(Root, "Logs");
    readonly string Reports = Path.Combine(Root, "Reports");
    readonly TextBox output = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    readonly TextBox input = new() { Multiline = true, Height = 70, Dock = DockStyle.Bottom };
    readonly Button add = new() { Text = "Görev Ekle / Sohbet Et", Height = 42, Dock = DockStyle.Bottom };
    readonly Button connect = new() { Text = "Microsoft 365 Bağlan ve Görevleri Eşitle", Height = 42, Dock = DockStyle.Bottom };
    readonly Label status = new() { Text = "Başlatılıyor", Height = 34, Dock = DockStyle.Top, Padding = new Padding(10, 9, 0, 0) };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 30000 };
    readonly HttpClient ollama = new() { BaseAddress = new Uri("http://localhost:11434"), Timeout = TimeSpan.FromMinutes(12) };
    bool busy; string lastErrorHash = "";

    public WatcherForm()
    {
        Text = "Arsan Gaz ERP AgentWatcher V7.3.1"; Width = 940; Height = 680; StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(output); Controls.Add(status); Controls.Add(connect); Controls.Add(add); Controls.Add(input);
        Directory.CreateDirectory(Queue); Directory.CreateDirectory(Logs); Directory.CreateDirectory(Reports);
        add.Click += async (_, _) => await AddTaskAsync();
        connect.Click += async (_, _) => await SyncMicrosoft365Async();
        timer.Tick += async (_, _) => await CheckAsync();
        Load += (_, _) => { timer.Start(); Append("AgentWatcher hazır. Ağır model yalnızca yeni görev/hata olduğunda ve bilgisayar boşta iken çalışır."); status.Text = "Hazır"; };
        FormClosing += (_, e) => { e.Cancel = true; Hide(); };
    }

    async Task AddTaskAsync()
    {
        string text = input.Text.Trim(); if (text.Length == 0) return; input.Clear();
        string file = Path.Combine(Queue, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.task.txt");
        await File.WriteAllTextAsync(file, text, new UTF8Encoding(false)); Append("Görev kuyruğa eklendi: " + text);
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
        if (busy || IdleSeconds() < 180 || AvailableMemory() < 3UL * 1024 * 1024 * 1024 || Process.GetProcessesByName("devenv").Length > 0) return;
        string? task = Directory.EnumerateFiles(Queue, "*.task.txt").OrderBy(x => x).FirstOrDefault();
        if (task != null) { busy = true; try { await RunTaskAsync(task); } finally { busy = false; } return; }
        string errors = CollectErrors(); if (errors.Length == 0) return;
        busy = true; try { await AnalyzeAsync(errors); } finally { busy = false; }
    }

    async Task RunTaskAsync(string file)
    {
        string request = await File.ReadAllTextAsync(file); status.Text = "Yerel AI görev üzerinde çalışıyor";
        string result = await AskAsync("Arsan Gaz ERP .NET 10 WPF sistemi için güvenli ve uygulanabilir görev planı üret. Silme, ödeme, e-posta gönderme, fatura ve yetki yükseltme yapma. İstek: " + request);
        string report = Path.Combine(Reports, $"task-{DateTime.Now:yyyyMMdd-HHmmss}.txt"); await File.WriteAllTextAsync(report, result);
        File.Move(file, file.Replace(".task.txt", ".done.txt"), true); Append(result); await UnloadAsync(); status.Text = "Hazır";
    }

    async Task AnalyzeAsync(string errors)
    {
        status.Text = "Derleme hatası analiz ediliyor";
        string result = await AskAsync("Aşağıdaki .NET 10 WPF derleme hatalarını teşhis et. Dosya silmeden gerekli düzeltmeleri belirt. Hatalar:\n" + errors);
        await File.WriteAllTextAsync(Path.Combine(Reports, $"build-{DateTime.Now:yyyyMMdd-HHmmss}.txt"), result);
        Append("Otomatik teşhis tamamlandı ve rapor kaydedildi."); await UnloadAsync(); status.Text = "Hazır";
    }

    string CollectErrors()
    {
        StringBuilder sb = new();
        foreach (string file in Directory.EnumerateFiles(Project, "*.log", SearchOption.TopDirectoryOnly))
        {
            if (DateTime.Now - File.GetLastWriteTime(file) > TimeSpan.FromMinutes(10)) continue;
            foreach (string line in File.ReadLines(file).TakeLast(120))
                if (line.Contains("error ", StringComparison.OrdinalIgnoreCase) || line.Contains("başarısız", StringComparison.OrdinalIgnoreCase) || line.Contains("basarisiz", StringComparison.OrdinalIgnoreCase)) sb.AppendLine(line);
        }
        string text = sb.ToString(); if (text.Length == 0) return "";
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))); if (hash == lastErrorHash) return ""; lastErrorHash = hash; return text;
    }

    async Task<string> AskAsync(string prompt)
    {
        string body = JsonSerializer.Serialize(new { model = Model, messages = new[] { new { role = "user", content = prompt } }, stream = false, keep_alive = "0s", options = new { temperature = 0, num_ctx = 4096 } });
        using HttpResponseMessage response = await ollama.PostAsync("/api/chat", new StringContent(body, Encoding.UTF8, "application/json"));
        string raw = await response.Content.ReadAsStringAsync(); response.EnsureSuccessStatusCode();
        using JsonDocument doc = JsonDocument.Parse(raw); return doc.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "";
    }

    async Task UnloadAsync()
    {
        try { string body = JsonSerializer.Serialize(new { model = Model, keep_alive = 0 }); await ollama.PostAsync("/api/generate", new StringContent(body, Encoding.UTF8, "application/json")); } catch { }
        try { Process.Start(new ProcessStartInfo("ollama", "stop " + Model) { UseShellExecute = false, CreateNoWindow = true }); } catch { }
    }

    void Append(string text) { if (InvokeRequired) { BeginInvoke(() => Append(text)); return; } output.AppendText($"[{DateTime.Now:HH:mm}] {text}{Environment.NewLine}{Environment.NewLine}"); }
    [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
    static uint IdleSeconds() { LASTINPUTINFO i = new() { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() }; return GetLastInputInfo(ref i) ? ((uint)Environment.TickCount - i.dwTime) / 1000 : 0; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)] struct MEMORYSTATUSEX { public uint dwLength; public uint dwMemoryLoad; public ulong ullTotalPhys; public ulong ullAvailPhys; public ulong ullTotalPageFile; public ulong ullAvailPageFile; public ulong ullTotalVirtual; public ulong ullAvailVirtual; public ulong ullAvailExtendedVirtual; }
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX value);
    static ulong AvailableMemory() { MEMORYSTATUSEX m = new() { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() }; return GlobalMemoryStatusEx(ref m) ? m.ullAvailPhys : 0; }
}

internal static class MicrosoftGraph
{
    const string ClientId = "3eaf1f8a-fbc8-40f0-9759-e134542606bf";
    static readonly string[] Scopes = ["User.Read", "Tasks.ReadWrite"];
    public static async Task<string> ReadTasksAsync()
    {
        IPublicClientApplication app = PublicClientApplicationBuilder.Create(ClientId).WithAuthority(AadAuthorityAudience.AzureAdAndPersonalMicrosoftAccount).WithRedirectUri("http://localhost").Build();
        IAccount? account = (await app.GetAccountsAsync()).FirstOrDefault(); AuthenticationResult auth;
        try { auth = await app.AcquireTokenSilent(Scopes, account).ExecuteAsync(); }
        catch { auth = await app.AcquireTokenInteractive(Scopes).WithPrompt(Prompt.SelectAccount).ExecuteAsync(); }
        using HttpClient http = new() { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") }; http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        using HttpResponseMessage listsResponse = await http.GetAsync("me/todo/lists"); string listsRaw = await listsResponse.Content.ReadAsStringAsync(); listsResponse.EnsureSuccessStatusCode();
        using JsonDocument listsDoc = JsonDocument.Parse(listsRaw); List<object> result = [];
        foreach (JsonElement list in listsDoc.RootElement.GetProperty("value").EnumerateArray())
        {
            string id = list.GetProperty("id").GetString() ?? ""; string name = list.GetProperty("displayName").GetString() ?? "";
            using HttpResponseMessage tasksResponse = await http.GetAsync($"me/todo/lists/{id}/tasks?$filter=status ne 'completed'"); string tasksRaw = await tasksResponse.Content.ReadAsStringAsync();
            if (tasksResponse.IsSuccessStatusCode) { using JsonDocument tasksDoc = JsonDocument.Parse(tasksRaw); result.Add(new { list = name, tasks = tasksDoc.RootElement.Clone() }); }
        }
        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    }
}
