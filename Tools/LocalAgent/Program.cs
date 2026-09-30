using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ArsanGazERP.LocalAgent;
internal static class Program
{
    [STAThread] static async Task Main()
    {
        using Mutex mutex=new(true,"Local\\ArsanGazERP.LocalAgent",out bool created); if(!created)return;
        string project=@"C:\ArsanGazERP\ArsanGazERP_V2_Duzeltilmis";
        string root=@"C:\ArsanGazERP\LocalAgentData";
        string logs=Path.Combine(root,"Logs"), reports=Path.Combine(root,"Reports");
        Directory.CreateDirectory(logs);Directory.CreateDirectory(reports);
        string log=Path.Combine(logs,$"local-agent-{DateTime.Today:yyyyMMdd}.log");
        await File.AppendAllTextAsync(log,$"{DateTime.Now:O} V7.2.1 LocalAgent baslatildi.{Environment.NewLine}");
        using HttpClient http=new(){BaseAddress=new Uri("http://localhost:11434"),Timeout=TimeSpan.FromMinutes(10)};
        HashSet<string> handled=[];
        while(true)
        {
            try
            {
                ProcessResult build=await Run("dotnet",$"build \"{Path.Combine(project,"ArsanGazERP.csproj")}\" -c Release",project);
                if(build.ExitCode!=0)
                {
                    string errors=build.Error+Environment.NewLine+build.Output;
                    string fp=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(errors)));
                    if(handled.Add(fp))
                    {
                        string prompt="Arsan Gaz ERP .NET 10 WPF derleme hatasini tani et. Dosya degistirme. Kisa ve uygulanabilir oneri ver. Hatalar:\n"+errors;
                        string body=JsonSerializer.Serialize(new{model="gemma3:4b",messages=new[]{new{role="user",content=prompt}},stream=false,keep_alive="0s"});
                        using HttpResponseMessage response=await http.PostAsync("/api/chat",new StringContent(body,Encoding.UTF8,"application/json"));
                        string answer=await response.Content.ReadAsStringAsync();
                        await File.WriteAllTextAsync(Path.Combine(reports,$"report-{DateTime.Now:yyyyMMdd-HHmmss}.json"),answer);
                        await File.AppendAllTextAsync(log,$"{DateTime.Now:O} Ollama HTTP {(int)response.StatusCode}.{Environment.NewLine}");
                    }
                }
                else await File.AppendAllTextAsync(log,$"{DateTime.Now:O} ERP build basarili.{Environment.NewLine}");
            }
            catch(Exception ex){await File.AppendAllTextAsync(log,$"{DateTime.Now:O} Hata: {ex.GetBaseException().Message}{Environment.NewLine}");}
            await Task.Delay(TimeSpan.FromMinutes(5));
        }
    }
    static async Task<ProcessResult> Run(string file,string args,string wd)
    {try{var s=new ProcessStartInfo(file,args){WorkingDirectory=wd,UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};using Process p=Process.Start(s)!;Task<string> o=p.StandardOutput.ReadToEndAsync(),e=p.StandardError.ReadToEndAsync();await p.WaitForExitAsync();return new(p.ExitCode,await o,await e);}catch(Exception ex){return new(-1,"",ex.Message);}}
}
internal sealed record ProcessResult(int ExitCode,string Output,string Error);
