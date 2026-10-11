using System.Text;
using ArsanGazERP.Data;
using ArsanGazERP.Models;
using Microsoft.EntityFrameworkCore;

namespace ArsanGazERP.Services;

public sealed record BriefingSection(string Title, string Body, bool Succeeded);

public static class DailyBriefingService
{
    public const string DefaultCity = "Adana";

    public static async Task<IReadOnlyList<BriefingSection>> BuildAsync(
        string? city,
        CancellationToken cancellationToken = default)
    {
        string targetCity = string.IsNullOrWhiteSpace(city) ? DefaultCity : city.Trim();
        Task<BriefingSection> work = Section("Bugünkü işler", BuildErpWorkAsync, cancellationToken);
        Task<BriefingSection> weather = Section(
            $"Hava durumu · {targetCity}",
            token => LiveInfoService.GetWeatherAsync(targetCity, token),
            cancellationToken,
            LiveInfoKind.Weather);
        Task<BriefingSection> rates = Section(
            "Döviz kurları",
            LiveInfoService.GetDefaultCurrencyRatesAsync,
            cancellationToken,
            LiveInfoKind.Currency);
        Task<BriefingSection> news = Section(
            "Gündem",
            token => LiveInfoService.GetNewsAsync(null, 6, token),
            cancellationToken,
            LiveInfoKind.News);
        Task<BriefingSection> energy = Section(
            "Enerji ve LPG haberleri",
            token => LiveInfoService.GetNewsAsync("Enerji ve LPG", 4, token),
            cancellationToken,
            LiveInfoKind.News);
        return await Task.WhenAll(work, weather, rates, news, energy);
    }

    public static async Task<string> BuildTextAsync(string? city, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<BriefingSection> sections = await BuildAsync(city, cancellationToken);
        StringBuilder text = new();
        text.AppendLine($"Günaydın! {DateTime.Now:dd MMMM yyyy dddd} günlük özetiniz:");
        foreach (BriefingSection section in sections)
        {
            text.AppendLine();
            text.AppendLine("▸ " + section.Title);
            text.AppendLine(section.Body.TrimEnd());
        }

        return text.ToString().TrimEnd();
    }

    private static async Task<BriefingSection> Section(
        string title,
        Func<CancellationToken, Task<string>> load,
        CancellationToken cancellationToken,
        LiveInfoKind kind = LiveInfoKind.None)
    {
        try
        {
            return new BriefingSection(title, await load(cancellationToken), true);
        }
        catch (Exception exception) when (kind != LiveInfoKind.None &&
                                          LiveInfoService.IsLiveDataFailure(exception, cancellationToken))
        {
            return new BriefingSection(title, LiveInfoService.DescribeFailure(kind, exception), false);
        }
        catch (Exception exception) when (kind == LiveInfoKind.None && !cancellationToken.IsCancellationRequested)
        {
            return new BriefingSection(title, "ERP verisi okunamadı: " + exception.GetBaseException().Message, false);
        }
    }

    private static async Task<string> BuildErpWorkAsync(CancellationToken cancellationToken)
    {
        DateTime today = DateTime.Today;
        StringBuilder text = new();

        await using (ArsanGazDbContext erp = new())
        {
            List<ErpTask> tasks = await erp.Tasks
                .AsNoTracking()
                .Where(task => task.Status == "Açık" && task.DueDate != null && task.DueDate < today.AddDays(1))
                .OrderBy(task => task.DueDate)
                .Take(8)
                .ToListAsync(cancellationToken);
            text.AppendLine(tasks.Count == 0
                ? "• Bugün için veya gecikmiş açık görev yok."
                : $"• Bugün/gecikmiş görev: {tasks.Count}");
            foreach (ErpTask task in tasks)
            {
                string late = task.DueDate!.Value.Date < today ? " (GECİKMİŞ)" : string.Empty;
                text.AppendLine($"   – {task.Title} · {task.DueDate:dd.MM}{late}");
            }

            List<CollectionReminder> reminders = await erp.CollectionReminders
                .AsNoTracking()
                .Where(reminder => reminder.Status == CollectionReminder.PendingStatus)
                .ToListAsync(cancellationToken);
            List<CollectionReminder> dueSoon = reminders
                .Where(reminder => reminder.DueDate.Date <= today.AddDays(3))
                .OrderBy(reminder => reminder.DueDate)
                .Take(8)
                .ToList();
            text.AppendLine(dueSoon.Count == 0
                ? "• 3 gün içinde vadesi gelen tahsilat yok."
                : $"• 3 gün içinde/gecikmiş tahsilat: {dueSoon.Count}");
            foreach (CollectionReminder reminder in dueSoon)
            {
                text.AppendLine("   – " + CollectionReminderService.Describe(reminder, today));
            }

            DateTime longLimit = today.AddDays(-CylinderTracking.LongAtCustomerDays);
            var longAtCustomer = await erp.Cylinders
                .AsNoTracking()
                .Where(cylinder => cylinder.IsActive && cylinder.Status == CylinderStatus.Customer &&
                                   cylinder.LastMovementAt != null && cylinder.LastMovementAt < longLimit)
                .OrderBy(cylinder => cylinder.LastMovementAt)
                .Select(cylinder => new
                {
                    cylinder.SerialNumber,
                    cylinder.LastMovementAt,
                    Customer = cylinder.CurrentCustomer != null ? cylinder.CurrentCustomer.CompanyName : null
                })
                .ToListAsync(cancellationToken);
            text.AppendLine(longAtCustomer.Count == 0
                ? $"• {CylinderTracking.LongAtCustomerDays} günden uzun müşteride kalan tüp yok."
                : $"• {CylinderTracking.LongAtCustomerDays} günden uzun müşteride kalan tüp: {longAtCustomer.Count}");
            foreach (var cylinder in longAtCustomer.Take(5))
            {
                text.AppendLine($"   – {cylinder.Customer ?? "müşteri yok"} · {cylinder.SerialNumber} · {CylinderTracking.DaysSince(cylinder.LastMovementAt, today)} gün");
            }
        }

        await using CrmDbContext crm = new();
        DateTime endOfTodayUtc = today.AddDays(1).ToUniversalTime();
        List<CrmCompany> followUps = await crm.CrmCompanies
            .AsNoTracking()
            .Where(company => company.FollowUpAtUtc != null && company.FollowUpAtUtc < endOfTodayUtc &&
                              company.Status != "Müşteri Oldu" && company.Status != "Kaybedildi")
            .OrderBy(company => company.FollowUpAtUtc)
            .Take(8)
            .ToListAsync(cancellationToken);
        text.AppendLine(followUps.Count == 0
            ? "• Bugün yapılacak CRM takibi yok."
            : $"• Bugün/gecikmiş CRM takibi: {followUps.Count}");
        foreach (CrmCompany company in followUps)
        {
            text.AppendLine($"   – {company.CompanyName} ({company.City ?? "il yok"})");
        }

        return text.ToString().TrimEnd();
    }
}
