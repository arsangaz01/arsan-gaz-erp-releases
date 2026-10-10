using System.Text;
using ArsanGazERP.Data;
using ArsanGazERP.Models;
using Microsoft.EntityFrameworkCore;

namespace ArsanGazERP.Services;

public static class ErpContextSnapshotService
{
    public const int MaximumLength = 1800;
    private static readonly HashSet<string> EligibleAgents = new(StringComparer.Ordinal)
    {
        "crm", "sales", "finance", "planner", "jarvis"
    };

    public static bool IsEligible(string agentId) => EligibleAgents.Contains(agentId);

    public static async Task<string> BuildAsync(string agentId, CancellationToken cancellationToken = default)
    {
        DateTime today = DateTime.Today;
        StringBuilder text = new();
        text.AppendLine($"ERP özeti (salt-okunur, {today:dd.MM.yyyy} itibarıyla, yerel uygulama tarafından eklendi):");

        await using (ArsanGazDbContext erp = new())
        {
            if (agentId is "finance" or "planner" or "jarvis" or "sales")
            {
                List<CollectionReminder> pending = await erp.CollectionReminders
                    .AsNoTracking()
                    .Where(reminder => reminder.Status == CollectionReminder.PendingStatus)
                    .ToListAsync(cancellationToken);
                List<CollectionReminder> upcoming = pending
                    .Where(reminder => reminder.DueDate.Date <= today.AddDays(14))
                    .OrderBy(reminder => reminder.DueDate)
                    .ToList();
                text.AppendLine($"- Bekleyen tahsilat: {pending.Count}; 14 gün içinde/gecikmiş: {upcoming.Count}.");
                foreach (CollectionReminder reminder in upcoming.Take(8))
                {
                    text.AppendLine("  • " + CollectionReminderService.Describe(reminder, today));
                }
            }

            List<ErpTask> openTasks = await erp.Tasks
                .AsNoTracking()
                .Where(task => task.Status == "Açık")
                .ToListAsync(cancellationToken);
            text.AppendLine($"- Açık ERP görevi: {openTasks.Count}; gecikmiş: {openTasks.Count(task => task.DueDate?.Date < today)}.");
            if (agentId is "planner" or "jarvis")
            {
                foreach (ErpTask task in openTasks.OrderBy(task => task.DueDate ?? DateTime.MaxValue).Take(5))
                {
                    text.AppendLine($"  • {task.Title}" + (task.DueDate is DateTime due ? $" (son tarih {due:dd.MM.yyyy})" : string.Empty));
                }
            }

            if (agentId is "sales" or "crm" or "jarvis")
            {
                var prospectsByCity = await erp.Prospects
                    .AsNoTracking()
                    .GroupBy(prospect => prospect.City)
                    .Select(group => new { City = group.Key, Count = group.Count() })
                    .ToListAsync(cancellationToken);
                if (prospectsByCity.Count > 0)
                {
                    text.AppendLine("- Bulunan aday firmalar: " + string.Join(", ",
                        prospectsByCity.OrderByDescending(item => item.Count).Select(item => $"{item.City} {item.Count}")) + ".");
                }
            }
        }

        if (agentId is "crm" or "sales" or "jarvis" or "planner")
        {
            await using CrmDbContext crm = new();
            List<CrmCompany> companies = await crm.CrmCompanies.AsNoTracking().ToListAsync(cancellationToken);
            text.AppendLine("- CRM firmaları: " + (companies.Count == 0
                ? "kayıt yok."
                : string.Join(", ", companies.GroupBy(company => company.Status)
                    .OrderByDescending(group => group.Count())
                    .Select(group => $"{group.Key} {group.Count()}")) + "."));
            DateTime endOfToday = today.AddDays(1).ToUniversalTime();
            List<CrmCompany> dueFollowUps = companies
                .Where(company => company.FollowUpAtUtc is DateTime followUp && followUp < endOfToday &&
                                  company.Status is not ("Müşteri Oldu" or "Kaybedildi"))
                .OrderBy(company => company.FollowUpAtUtc)
                .ToList();
            text.AppendLine($"- Bugün veya önce yapılması gereken CRM takibi: {dueFollowUps.Count}.");
            foreach (CrmCompany company in dueFollowUps.Take(5))
            {
                text.AppendLine($"  • {company.CompanyName} ({company.City ?? "il yok"}, {company.Sector ?? "sektör yok"}, skor {company.Score})");
            }
        }

        string result = text.ToString().TrimEnd();
        return result.Length <= MaximumLength ? result : result[..MaximumLength] + " …";
    }
}
