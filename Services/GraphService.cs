using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
namespace ArsanGazERP.Services;
public sealed record MailSummary(string Id,string Subject,string FromName,string FromAddress,DateTimeOffset Received,bool IsRead,string Preview);
public sealed class GraphService
{
 private readonly AuthService _authService; private readonly HttpClient _httpClient=new(){BaseAddress=new Uri("https://graph.microsoft.com/v1.0/")};
 public GraphService(AuthService authService)=>_authService=authService;
 public Task<string> GetProfileAsync()=>GetAsync("me?$select=displayName,mail,userPrincipalName");
 public Task<string> ListOneDriveFilesAsync()=>GetAsync("me/drive/root/children?$select=id,name,size,lastModifiedDateTime,webUrl");
 public Task<string> ListRecentMailAsync()=>GetAsync("me/messages?$top=10&$select=subject,from,receivedDateTime,isRead");
 public Task<string> ListContactsAsync()=>GetAsync("me/contacts?$top=20&$select=displayName,emailAddresses,businessPhones");
 public Task<string> ListCalendarAsync()=>GetAsync("me/events?$top=20&$select=subject,start,end,location");
 public Task<string> ListTodayCalendarAsync()
 {
  DateTimeOffset now=DateTimeOffset.Now;
  DateTimeOffset start=new(now.Year,now.Month,now.Day,0,0,0,now.Offset);
  DateTimeOffset end=start.AddDays(1);
  string query=$"me/calendarView?startDateTime={Uri.EscapeDataString(start.ToString("yyyy-MM-dd'T'HH:mm:sszzz"))}&endDateTime={Uri.EscapeDataString(end.ToString("yyyy-MM-dd'T'HH:mm:sszzz"))}&$select=start,end";
  return GetAsync(query, silentOnly:true);
 }
 public async Task SendMailAsync(string to,string subject,string body)
 {
  await AuthorizeAsync(); object payload=new{message=new{subject,body=new{contentType="Text",content=body},toRecipients=new[]{new{emailAddress=new{address=to}}}},saveToSentItems=true};
  using StringContent c=new(JsonSerializer.Serialize(payload),Encoding.UTF8,"application/json"); using HttpResponseMessage r=await _httpClient.PostAsync("me/sendMail",c); string t=await r.Content.ReadAsStringAsync(); if(!r.IsSuccessStatusCode)throw new InvalidOperationException($"Mail hatası {(int)r.StatusCode}: {t}");
 }
 public async Task<IReadOnlyList<MailSummary>> ListInboxAsync(int top=25)
 {
  await AuthorizeAsync();
  using HttpResponseMessage r=await _httpClient.GetAsync($"me/mailFolders/inbox/messages?$top={top}&$orderby=receivedDateTime%20desc&$select=id,subject,from,receivedDateTime,isRead,bodyPreview");
  string t=await r.Content.ReadAsStringAsync(); if(!r.IsSuccessStatusCode)throw new InvalidOperationException($"Graph hatası {(int)r.StatusCode}: {t}");
  using JsonDocument d=JsonDocument.Parse(t);
  List<MailSummary> items=[];
  foreach(JsonElement m in d.RootElement.GetProperty("value").EnumerateArray())
  {
   JsonElement address=m.TryGetProperty("from",out JsonElement from)&&from.ValueKind==JsonValueKind.Object&&from.TryGetProperty("emailAddress",out JsonElement e)?e:default;
   items.Add(new MailSummary(
    m.GetProperty("id").GetString()!,
    m.TryGetProperty("subject",out JsonElement s)?s.GetString()??"(konu yok)":"(konu yok)",
    address.ValueKind==JsonValueKind.Object&&address.TryGetProperty("name",out JsonElement n)?n.GetString()??"":"",
    address.ValueKind==JsonValueKind.Object&&address.TryGetProperty("address",out JsonElement a)?a.GetString()??"":"",
    m.TryGetProperty("receivedDateTime",out JsonElement rd)&&rd.TryGetDateTimeOffset(out DateTimeOffset received)?received.ToLocalTime():DateTimeOffset.MinValue,
    m.TryGetProperty("isRead",out JsonElement ir)&&ir.ValueKind==JsonValueKind.True,
    m.TryGetProperty("bodyPreview",out JsonElement p)?p.GetString()??"":""));
  }
  return items;
 }
 public async Task<string> GetMailBodyTextAsync(string messageId)
 {
  await AuthorizeAsync();
  using HttpRequestMessage request=new(HttpMethod.Get,$"me/messages/{Uri.EscapeDataString(messageId)}?$select=body");
  request.Headers.Add("Prefer","outlook.body-content-type=\"text\"");
  using HttpResponseMessage r=await _httpClient.SendAsync(request); string t=await r.Content.ReadAsStringAsync(); if(!r.IsSuccessStatusCode)throw new InvalidOperationException($"Graph hatası {(int)r.StatusCode}: {t}");
  using JsonDocument d=JsonDocument.Parse(t);
  return d.RootElement.GetProperty("body").GetProperty("content").GetString()??"";
 }
 public async Task ReplyToMailAsync(string messageId,string comment)
 {
  await AuthorizeAsync();
  using StringContent c=new(JsonSerializer.Serialize(new{comment}),Encoding.UTF8,"application/json");
  using HttpResponseMessage r=await _httpClient.PostAsync($"me/messages/{Uri.EscapeDataString(messageId)}/reply",c); string t=await r.Content.ReadAsStringAsync(); if(!r.IsSuccessStatusCode)throw new InvalidOperationException($"Yanıt gönderilemedi {(int)r.StatusCode}: {t}");
 }
 public async Task<string> UploadBackupAsync(string path)
 {
  await AuthorizeAsync(); string name=Uri.EscapeDataString(Path.GetFileName(path)); await using FileStream file=new(path,FileMode.Open,FileAccess.Read,FileShare.Read); using StreamContent c=new(file); c.Headers.ContentType=new MediaTypeHeaderValue("application/octet-stream"); using HttpResponseMessage r=await _httpClient.PutAsync($"me/drive/root:/ArsanGazERP_Backups/{name}:/content",c); string t=await r.Content.ReadAsStringAsync(); if(!r.IsSuccessStatusCode)throw new InvalidOperationException($"OneDrive yedek hatası {(int)r.StatusCode}: {t}"); return "OneDrive yedeği oluşturuldu: "+Path.GetFileName(path);
 }
 private async Task AuthorizeAsync(){_httpClient.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",await _authService.GetAccessTokenAsync());}
 private async Task<string> GetAsync(string path,bool silentOnly=false){await AuthorizeAsync(silentOnly);using HttpResponseMessage r=await _httpClient.GetAsync(path);string t=await r.Content.ReadAsStringAsync();if(!r.IsSuccessStatusCode)throw new InvalidOperationException($"Graph hatası {(int)r.StatusCode}: {t}");using JsonDocument d=JsonDocument.Parse(t);return JsonSerializer.Serialize(d,new JsonSerializerOptions{WriteIndented=true});}
 private async Task AuthorizeAsync(bool silentOnly=false){_httpClient.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",silentOnly?await _authService.GetCachedAccessTokenAsync():await _authService.GetAccessTokenAsync());}
}