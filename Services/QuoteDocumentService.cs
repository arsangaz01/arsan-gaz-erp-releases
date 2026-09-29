using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using ArsanGazERP.Data;
using Microsoft.EntityFrameworkCore;
namespace ArsanGazERP.Services;
public sealed class QuoteDocumentService
{
 public async Task<string> CreateHtmlAsync(int quoteId)
 {
  await using var db=new ArsanGazDbContext();var q=await db.Quotes.Include(x=>x.Customer).Include(x=>x.Lines).ThenInclude(x=>x.Product).FirstOrDefaultAsync(x=>x.Id==quoteId)??throw new InvalidOperationException("Teklif bulunamadı.");
  string dir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),"ArsanGazERP","Teklifler");Directory.CreateDirectory(dir);string path=Path.Combine(dir,$"{Safe(q.QuoteNumber)}.html");
  var b=new StringBuilder();b.Append("<!doctype html><meta charset='utf-8'><title>Teklif</title><style>body{font-family:Segoe UI;margin:40px;color:#222}h1{color:#0F4C5C}table{width:100%;border-collapse:collapse}th,td{border:1px solid #bbb;padding:8px;text-align:left}.total{text-align:right;font-weight:bold;font-size:18px}</style>");b.Append($"<h1>ARSAN GAZ TEKLİFİ</h1><p><b>Teklif No:</b> {H(q.QuoteNumber)}<br><b>Müşteri:</b> {H(q.Customer.CompanyName)}<br><b>Tarih:</b> {q.QuoteDate:dd.MM.yyyy}<br><b>Geçerlilik:</b> {q.ValidUntil:dd.MM.yyyy}</p><table><tr><th>Ürün</th><th>Miktar</th><th>Birim Fiyat</th><th>KDV</th><th>Toplam</th></tr>");foreach(var x in q.Lines)b.Append($"<tr><td>{H(x.Product.Name)}</td><td>{x.Quantity:N2}</td><td>{x.UnitPrice:N2}</td><td>%{x.VatRate:N0}</td><td>{x.LineTotal:N2}</td></tr>");b.Append($"</table><p class='total'>Genel Toplam: {q.TotalAmount:N2} {H(q.Currency)}</p><p>Bu belge taslaktır. Resmîleştirme insan onayı gerektirir.</p>");await File.WriteAllTextAsync(path,b.ToString(),Encoding.UTF8);return path;
 }
 public void Open(string path)=>Process.Start(new ProcessStartInfo(path){UseShellExecute=true});
 private static string H(string? s)=>WebUtility.HtmlEncode(s??"");private static string Safe(string s)=>string.Concat(s.Select(c=>Path.GetInvalidFileNameChars().Contains(c)?'_':c));
}