using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ArsanGazERP.Data;
using ArsanGazERP.Models;
using Microsoft.EntityFrameworkCore;

namespace ArsanGazERP.Services;

public sealed class AgentV3Service
{
	public async Task<IReadOnlyList<AgentFinding>> AnalyzeAsync()
	{
		DateTime startedAt = DateTime.UtcNow;
		await using ArsanGazDbContext db = new();

		try
		{
			List<AgentFinding> findings = new();
			DateTime today = DateTime.Today;
			List<Invoice> overdueInvoices = await db.Invoices
				.Where(invoice => invoice.DueDate < today && invoice.PaidAmount < invoice.TotalAmount)
				.ToListAsync();

			if (overdueInvoices.Count > 0)
			{
				decimal outstanding = overdueInvoices.Sum(invoice => invoice.TotalAmount - invoice.PaidAmount);
				findings.Add(new AgentFinding(
					"Kritik",
					"Tahsilat",
					$"{overdueInvoices.Count} gecikmiş fatura, {outstanding:N2} TL bakiye."));
			}

			int quotesToFollowUp = await db.Quotes.CountAsync(quote =>
				quote.FollowUpDate <= today && quote.Status != "Kazanıldı" && quote.Status != "Kaybedildi");
			if (quotesToFollowUp > 0)
			{
				findings.Add(new AgentFinding("Uyarı", "Satış", $"Takip bekleyen {quotesToFollowUp} teklif."));
			}

			int cylindersWithCustomer = await db.Cylinders.CountAsync(cylinder =>
				cylinder.Status == CylinderStatus.Customer && cylinder.LastMovementAt < today.AddDays(-30));
			if (cylindersWithCustomer > 0)
			{
				findings.Add(new AgentFinding(
					"Uyarı",
					"Tüp",
					$"Müşteride 30 günü aşan {cylindersWithCustomer} tüp."));
			}

			int testsDueSoon = await db.Cylinders.CountAsync(cylinder => cylinder.TestDueDate <= today.AddDays(30));
			if (testsDueSoon > 0)
			{
				findings.Add(new AgentFinding(
					"Kritik",
					"Test",
					$"Test tarihi 30 gün içinde olan/geçen {testsDueSoon} tüp."));
			}

			int dormantCustomers = await db.Customers.CountAsync(customer =>
				customer.IsActive && !db.Quotes.Any(quote =>
					quote.CustomerId == customer.Id && quote.QuoteDate >= today.AddDays(-90)));
			if (dormantCustomers > 0)
			{
				findings.Add(new AgentFinding(
					"Bilgi",
					"CRM",
					$"Son 90 günde teklif verilmeyen {dormantCustomers} aktif müşteri."));
			}

			if (findings.Count == 0)
			{
				findings.Add(new AgentFinding("Başarılı", "Ajan V3", "Kritik uyarı bulunmadı."));
			}

			findings.Add(new AgentFinding(
				"Bilgi",
				"Onay",
				"Ajan öneri üretir; mail gönderme, fiyat onayı, fatura ve para işlemleri kullanıcı onayı gerektirir."));

			int criticalCount = findings.Count(finding => finding.Severity == "Kritik");
			db.AgentRuns.Add(new AgentRun
			{
				StartedAtUtc = startedAt,
				CompletedAtUtc = DateTime.UtcNow,
				Succeeded = true,
				FindingCount = findings.Count,
				CriticalCount = criticalCount,
				Summary = $"{findings.Count} bulgu · {criticalCount} kritik",
				FindingsJson = JsonSerializer.Serialize(findings)
			});
			await db.SaveChangesAsync();
			return findings;
		}
		catch (Exception exception)
		{
			try
			{
				await using ArsanGazDbContext auditDb = new();
				auditDb.AgentRuns.Add(new AgentRun
				{
					StartedAtUtc = startedAt,
					CompletedAtUtc = DateTime.UtcNow,
					Succeeded = false,
					Summary = "Ajan analizi tamamlanamadı: " + exception.GetBaseException().GetType().Name,
					FindingsJson = "[]"
				});
				await auditDb.SaveChangesAsync();
			}
			catch
			{
			}

			throw;
		}
	}

	public async Task<IReadOnlyList<AgentRun>> RecentRunsAsync(int count = 25)
	{
		await using ArsanGazDbContext db = new();
		int take = Math.Clamp(count, 1, 100);
		return await db.AgentRuns.AsNoTracking()
			.OrderByDescending(run => run.StartedAtUtc)
			.Take(take)
			.ToListAsync();
	}
}