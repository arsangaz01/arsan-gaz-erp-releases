using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
namespace ArsanGazERP.Services;
public sealed class GraphService
{
 private readonly AuthService _authService; private readonly HttpClient _httpClient=new(){BaseAddress=new Uri("https://graph.microsoft.com/v1.0/")};
 public GraphService(AuthService authService)=>_authService=authService;
 public Task<string> GetProfileAsync()=>GetAsync("me?$select=displayName,mail,userPrincipalName");
 public Task<string> ListOneDriveFilesAsync()=>GetAsync("me/drive/root/children?$select=id,name,size,lastModifiedDateTime,webUrl");
 public Task<string> ListRecentMailAsync()=>GetAsync("me/messages?$top=10&$select=subject,from,receivedDateTime,isRead");
 public Task<string> ListContactsAsync()=>GetAsync("me/contacts?$top=20&$select=displayName,emailAddresses,businessPhones");
 public Task<string> ListCalendarAsync()=>GetAsync("me/events?$top=20&$select=subject,start,end,location");
 public async Task SendMailAsync(string to,string subject,string body)
 {
  await AuthorizeAsync(); object payload=new{message=new{subject,body=new{contentType="Text",content=body},toRecipients=new[]{new{emailAddress=new{address=to}}}},saveToSentItems=true};
  using StringContent c=new(JsonSerializer.Serialize(payload),Encoding.UTF8,"application/json"); using HttpResponseMessage r=await _httpClient.PostAsync("me/sendMail",c); string t=await r.Content.ReadAsStringAsync(); if(!r.IsSuccessStatusCode)throw new InvalidOperationException($"Mail hatası {(int)r.StatusCode}: {t}");
 }
 public async Task<string> UploadBackupAsync(string path)
 {
  await AuthorizeAsync(); string name=Uri.EscapeDataString(Path.GetFileName(path)); byte[] bytes=await File.ReadAllBytesAsync(path); using ByteArrayContent c=new(bytes); c.Headers.ContentType=new MediaTypeHeaderValue("application/octet-stream"); using HttpResponseMessage r=await _httpClient.PutAsync($"me/drive/root:/ArsanGazERP_Backups/{name}:/content",c); string t=await r.Content.ReadAsStringAsync(); if(!r.IsSuccessStatusCode)throw new InvalidOperationException($"OneDrive yedek hatası {(int)r.StatusCode}: {t}"); return "OneDrive yedeği oluşturuldu: "+Path.GetFileName(path);
 }
 private async Task AuthorizeAsync(){_httpClient.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",await _authService.GetAccessTokenAsync());}
 private async Task<string> GetAsync(string path){await AuthorizeAsync();using HttpResponseMessage r=await _httpClient.GetAsync(path);string t=await r.Content.ReadAsStringAsync();if(!r.IsSuccessStatusCode)throw new InvalidOperationException($"Graph hatası {(int)r.StatusCode}: {t}");using JsonDocument d=JsonDocument.Parse(t);return JsonSerializer.Serialize(d,new JsonSerializerOptions{WriteIndented=true});}
}