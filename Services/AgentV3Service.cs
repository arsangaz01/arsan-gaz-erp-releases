using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ArsanGazERP.Data;
using ArsanGazERP.Models;
using Microsoft.EntityFrameworkCore;
namespace ArsanGazERP.Services;
public sealed class AgentV3Service
{
 public async Task<IReadOnlyList<AgentFinding>> AnalyzeAsync(){await using var db=new ArsanGazDbContext();var f=new List<AgentFinding>();DateTime t=DateTime.Today;var inv=await db.Invoices.Where(x=>x.DueDate<t&&x.PaidAmount<x.TotalAmount).ToListAsync();if(inv.Any())f.Add(new("Kritik","Tahsilat",$"{inv.Count} gecikmiş fatura, {inv.Sum(x=>x.TotalAmount-x.PaidAmount):N2} TL bakiye."));int q=await db.Quotes.CountAsync(x=>x.FollowUpDate<=t&&x.Status!="Kazanıldı"&&x.Status!="Kaybedildi");if(q>0)f.Add(new("Uyarı","Satış",$"Takip bekleyen {q} teklif."));int c=await db.Cylinders.CountAsync(x=>x.Status==CylinderStatus.Customer&&x.LastMovementAt<t.AddDays(-30));if(c>0)f.Add(new("Uyarı","Tüp",$"Müşteride 30 günü aşan {c} tüp."));int test=await db.Cylinders.CountAsync(x=>x.TestDueDate<=t.AddDays(30));if(test>0)f.Add(new("Kritik","Test",$"Test tarihi 30 gün içinde olan/geçen {test} tüp."));int dormant=await db.Customers.CountAsync(x=>x.IsActive&&!db.Quotes.Any(q=>q.CustomerId==x.Id&&q.QuoteDate>=t.AddDays(-90)));if(dormant>0)f.Add(new("Bilgi","CRM",$"Son 90 günde teklif verilmeyen {dormant} aktif müşteri."));if(!f.Any())f.Add(new("Başarılı","Ajan V3","Kritik uyarı bulunmadı."));f.Add(new("Bilgi","Onay","Ajan öneri üretir; mail gönderme, fiyat onayı, fatura ve para işlemleri kullanıcı onayı gerektirir."));return f;}
}