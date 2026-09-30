using System.Diagnostics;
using System.Drawing.Imaging;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace ArsanGazERP.AgentBridge;

internal static class Program
{
    [STAThread]
    private static async Task Main()
    {
        ApplicationConfiguration.Initialize();
        using Mutex mutex = new(true, "Local\\ArsanGazERP.AgentBridge", out bool created);
        if (!created) return;
        BridgePaths paths = BridgePaths.Create();
        string? key = Environment.GetEnvironmentVariable("ARSANGAZ_AI_API_KEY", EnvironmentVariableTarget.User);
        if (string.IsNullOrWhiteSpace(key))
        {
            using KeyForm form = new();
            if (form.ShowDialog() != DialogResult.OK || string.IsNullOrWhiteSpace(form.ApiKey)) return;
            key = form.ApiKey.Trim();
            Environment.SetEnvironmentVariable("ARSANGAZ_AI_API_KEY", key, EnvironmentVariableTarget.User);
        }
        await new BridgeWorker(paths, key).RunAsync();
    }
}

internal sealed record BridgePaths(string Project,string Root,string Logs,string Screens,string Reports,string Backups)
{
    public static BridgePaths Create()
    {
        string project=@"C:\ArsanGazERP\ArsanGazERP_V2_Duzeltilmis";
        string root=Path.Combine(project,"Tools","AgentBridgeData");
        var p=new BridgePaths(project,root,Path.Combine(root,"Logs"),Path.Combine(root,"Screens"),Path.Combine(root,"Reports"),Path.Combine(root,"Backups"));
        Directory.CreateDirectory(p.Logs);Directory.CreateDirectory(p.Screens);Directory.CreateDirectory(p.Reports);Directory.CreateDirectory(p.Backups);return p;
    }
}

internal sealed class KeyForm:Form
{
    readonly TextBox box=new(){UseSystemPasswordChar=true,Dock=DockStyle.Top};
    public string ApiKey=>box.Text;
    public KeyForm()
    {
        Text="Arsan Gaz ERP AI Baglantisi";Width=560;Height=180;StartPosition=FormStartPosition.CenterScreen;TopMost=true;
        var label=new Label{Text="OpenAI API anahtarini bir kez girin. Anahtar kullanici ortam degiskeninde saklanir.",Dock=DockStyle.Top,Height=45};
        var ok=new Button{Text="Kaydet ve Baslat",Dock=DockStyle.Bottom,DialogResult=DialogResult.OK,Height=40};
        Controls.Add(ok);Controls.Add(box);Controls.Add(label);AcceptButton=ok;
    }
}

internal sealed class BridgeWorker
{
    readonly BridgePaths p; readonly string key; readonly HttpClient http=new(){Timeout=TimeSpan.FromMinutes(5)}; string? lastFingerprint;
    public BridgeWorker(BridgePaths paths,string apiKey){p=paths;key=apiKey;http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",key);}
    public async Task RunAsync()
    {
        await Log("Agent Bridge baslatildi.");
        await InspectBuildAsync();
        while(true)
        {
            try
            {
                string errors=CollectRecentErrors();
                if(!string.IsNullOrWhiteSpace(errors)) await AnalyzeAndRepairAsync(errors);
            }
            catch(Exception ex){await Log("Dongu hatasi: "+ex.GetBaseException().Message);}
            await Task.Delay(TimeSpan.FromSeconds(30));
        }
    }
    async Task InspectBuildAsync()
    {
        ProcessResult r=await Run("dotnet",$"build \"{Path.Combine(p.Project,"ArsanGazERP.csproj")}\" -c Release",p.Project);
        if(r.ExitCode!=0) await AnalyzeAndRepairAsync(r.Error+Environment.NewLine+r.Output);
    }
    string CollectRecentErrors()
    {
        var sb=new StringBuilder();
        foreach(string f in Directory.EnumerateFiles(p.Project,"*.log",SearchOption.TopDirectoryOnly))
        {
            var info=new FileInfo(f);if(DateTime.Now-info.LastWriteTime>TimeSpan.FromMinutes(5))continue;
            string[] lines=File.ReadAllLines(f);foreach(string line in lines.TakeLast(120))if(line.Contains("error ",StringComparison.OrdinalIgnoreCase)||line.Contains("BASARISIZ",StringComparison.OrdinalIgnoreCase))sb.AppendLine(line);
        }
        string value=sb.ToString();string fp=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        if(string.IsNullOrWhiteSpace(value)||fp==lastFingerprint)return string.Empty;lastFingerprint=fp;return value;
    }
    async Task AnalyzeAndRepairAsync(string errors)
    {
        string screenshot=Capture();string prompt="Arsan Gaz ERP .NET 10 WPF projesinde hata var. Hata metnini ve varsa ekran goruntusunu incele. Yalnizca derleme hatasini duzeltecek tam dosya iceriklerini JSON olarak ver. Bicim: {\\\"summary\\\":\\\"...\\\",\\\"files\\\":[{\\\"path\\\":\\\"Services/X.cs\\\",\\\"content\\\":\\\"tam dosya\\\"}]}. Silme, odeme, dis iletisim, gizli anahtar, git reset veya yetki yukseltme yapma. Hatalar:\n"+errors;
        string answer=await Ask(prompt,screenshot);await File.WriteAllTextAsync(Path.Combine(p.Reports,$"ai-{DateTime.Now:yyyyMMdd-HHmmss}.txt"),answer);
        RepairPlan? plan=ParsePlan(answer);if(plan?.Files is null||plan.Files.Count==0){await Log("AI tani raporu kaydedildi; uygulanabilir dosya yok.");return;}
        string backup=Path.Combine(p.Backups,DateTime.Now.ToString("yyyyMMdd-HHmmss"));Directory.CreateDirectory(backup);var changed=new List<(string target,string backup,bool existed)>();
        foreach(var item in plan.Files)
        {
            string full=Path.GetFullPath(Path.Combine(p.Project,item.Path));if(!full.StartsWith(Path.GetFullPath(p.Project),StringComparison.OrdinalIgnoreCase)||!AllowedExtension(full))continue;
            string rel=Path.GetRelativePath(p.Project,full);string bak=Path.Combine(backup,rel);Directory.CreateDirectory(Path.GetDirectoryName(bak)!);bool existed=File.Exists(full);if(existed)File.Copy(full,bak,true);Directory.CreateDirectory(Path.GetDirectoryName(full)!);await File.WriteAllTextAsync(full,item.Content,new UTF8Encoding(false));changed.Add((full,bak,existed));
        }
        if(changed.Count==0)return;
        ProcessResult build=await Run("dotnet",$"build \"{Path.Combine(p.Project,"ArsanGazERP.csproj")}\" -c Release",p.Project);
        if(build.ExitCode!=0){foreach(var c in changed){if(c.existed)File.Copy(c.backup,c.target,true);else if(File.Exists(c.target))File.Delete(c.target);}await Log("AI onarimi derlenmedi; otomatik geri alindi.");await AnalyzeOnly(build.Error+Environment.NewLine+build.Output);return;}
        await Log("AI onarimi derlendi ve korundu: "+plan.Summary);await Run("git","add .",p.Project);await Run("git","commit -m \"AI verified repair\"",p.Project);await Run("git","push origin HEAD",p.Project);
    }
    async Task AnalyzeOnly(string errors){string s=Capture();string a=await Ask("Onarim sonrasi kalan hatalari tani et, kod calistirma:\n"+errors,s);await File.WriteAllTextAsync(Path.Combine(p.Reports,$"remaining-{DateTime.Now:yyyyMMdd-HHmmss}.txt"),a);}
    async Task<string> Ask(string prompt,string screenshot)
    {
        var content=new List<object>{new{type="input_text",text=prompt}};if(File.Exists(screenshot))content.Add(new{type="input_image",image_url="data:image/png;base64,"+Convert.ToBase64String(await File.ReadAllBytesAsync(screenshot))});
        string body=JsonSerializer.Serialize(new{model="gpt-5.4",input=new[]{new{role="user",content}},max_output_tokens=12000});using var req=new HttpRequestMessage(HttpMethod.Post,"https://api.openai.com/v1/responses"){Content=new StringContent(body,Encoding.UTF8,"application/json")};using HttpResponseMessage res=await http.SendAsync(req);string json=await res.Content.ReadAsStringAsync();if(!res.IsSuccessStatusCode){await Log("AI API hatasi: "+(int)res.StatusCode);return json;}using JsonDocument d=JsonDocument.Parse(json);var sb=new StringBuilder();foreach(var o in d.RootElement.GetProperty("output").EnumerateArray())if(o.TryGetProperty("content",out var cs))foreach(var c in cs.EnumerateArray())if(c.TryGetProperty("text",out var t))sb.Append(t.GetString());return sb.ToString();
    }
    static RepairPlan? ParsePlan(string text){try{int a=text.IndexOf('{'),b=text.LastIndexOf('}');if(a<0||b<=a)return null;return JsonSerializer.Deserialize<RepairPlan>(text[a..(b+1)],new JsonSerializerOptions{PropertyNameCaseInsensitive=true});}catch{return null;}}
    static bool AllowedExtension(string f)=>new[]{".cs",".xaml",".csproj",".json",".xml",".config"}.Contains(Path.GetExtension(f),StringComparer.OrdinalIgnoreCase);
    string Capture(){try{Rectangle r=Screen.PrimaryScreen?.Bounds??Rectangle.Empty;if(r.IsEmpty)return string.Empty;string f=Path.Combine(p.Screens,$"error-{DateTime.Now:yyyyMMdd-HHmmss}.png");using var b=new Bitmap(r.Width,r.Height);using(var g=Graphics.FromImage(b))g.CopyFromScreen(r.Location,Point.Empty,r.Size);b.Save(f,ImageFormat.Png);return f;}catch{return string.Empty;}}
    static async Task<ProcessResult> Run(string file,string args,string wd){var psi=new ProcessStartInfo(file,args){WorkingDirectory=wd,UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};using var x=Process.Start(psi)!;var o=x.StandardOutput.ReadToEndAsync();var e=x.StandardError.ReadToEndAsync();await x.WaitForExitAsync();return new(x.ExitCode,await o,await e);}
    Task Log(string m)=>File.AppendAllTextAsync(Path.Combine(p.Logs,$"bridge-{DateTime.Today:yyyyMMdd}.log"),$"{DateTime.Now:O} {m}{Environment.NewLine}");
}
internal sealed record ProcessResult(int ExitCode,string Output,string Error);
internal sealed class RepairPlan{public string Summary{get;set;}="";public List<RepairFile> Files{get;set;}=[];}
internal sealed class RepairFile{public string Path{get;set;}="";public string Content{get;set;}="";}
