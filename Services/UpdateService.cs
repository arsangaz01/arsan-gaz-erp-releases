using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
namespace ArsanGazERP.Services;
public sealed class UpdateService
{
 private static readonly HttpClient Http=new();
 public async Task<string> CheckAsync()
 {
  Http.DefaultRequestHeaders.UserAgent.ParseAdd("ArsanGazERP/4.0"); using HttpResponseMessage r=await Http.GetAsync("https://api.github.com/repos/arsangaz01/arsan-gaz-erp-releases/releases/latest");
    if (r.StatusCode == System.Net.HttpStatusCode.NotFound)
     return "Henüz yayınlanmış bir GitHub sürümü bulunamadı.";
  if(!r.IsSuccessStatusCode)return $"GitHub güncelleme kontrolü başarısız: {(int)r.StatusCode}";
  using JsonDocument d=JsonDocument.Parse(await r.Content.ReadAsStringAsync()); string tag=d.RootElement.GetProperty("tag_name").GetString()??"0.0.0"; string page=d.RootElement.GetProperty("html_url").GetString()??""; Version current=Assembly.GetExecutingAssembly().GetName().Version??new(0,0); Version.TryParse(tag.TrimStart('v','V'),out Version? latest);
  return latest!=null&&latest>current?$"Yeni sürüm hazır: {latest}. Yayın: {page}":$"Uygulama güncel: {current}";
 }
}