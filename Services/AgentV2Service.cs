using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ArsanGazERP.Data;
using ArsanGazERP.Models;
using Microsoft.EntityFrameworkCore;
namespace ArsanGazERP.Services;
public sealed class AgentV2Service
{
 public async Task<IReadOnlyList<AgentFinding>> AnalyzeAsync()
 {
  await using ArsanGazDbContext db=new(); DateTime today=DateTime.Today; List<AgentFinding> f=new();
  var overdue=await db.Invoices.Where(x=>x.DueDate<today&&x.PaidAmount<x.TotalAmount).ToListAsync();
  if(overdue.Count>0)f.Add(new("Kritik","Tahsilat",$"Vadesi geçmiş {overdue.Count} fatura var. Kalan: {overdue.Sum(x=>x.TotalAmount-x.PaidAmount):N2} TL"));
  int follow=await db.Quotes.CountAsync(x=>x.FollowUpDate<=today&&x.Status!="Kazanıldı"&&x.Status!="Kaybedildi");
  if(follow>0)f.Add(new("Uyarı","Teklif",$"Takip tarihi gelen {follow} teklif var."));
  int longStay=await db.Cylinders.CountAsync(x=>x.Status==CylinderStatus.Customer&&x.LastMovementAt<today.AddDays(-30));
  if(longStay>0)f.Add(new("Uyarı","Tüp",$"Müşteride 30 günden uzun kalan {longStay} tüp var."));
  int tests=await db.Cylinders.CountAsync(x=>x.TestDueDate<=today.AddDays(30));
  if(tests>0)f.Add(new("Kritik","Tüp Testi",$"30 gün içinde test tarihi gelen/geçen {tests} tüp var."));
  int tasks=await db.Tasks.CountAsync(x=>x.Status=="Açık"&&x.DueDate<=today);
  if(tasks>0)f.Add(new("Uyarı","Görev",$"Son tarihi gelen {tasks} açık görev var."));
  if(f.Count==0)f.Add(new("Başarılı","Ajan V2","Kritik operasyonel uyarı bulunmadı."));
  f.Add(new("Bilgi","Güvenlik","Ajan yalnızca analiz ve taslak üretir; kritik işlemler insan onayı gerektirir."));
  return f;
 }
}