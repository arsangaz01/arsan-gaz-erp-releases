using ArsanGazERP.Modules.AI.Common;
using ArsanGazERP.Modules.AI.Models;
using ArsanGazERP.Data;
using ArsanGazERP.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Client;
namespace ArsanGazERP.Modules.AI.Services;
public sealed class CrmAnalyticsService : ICrmAnalyticsService
{
    public async Task<DashboardStatisticsDto> GetDashboardStatisticsAsync(CancellationToken ct = default)
    {
        await using ArsanGazDbContext erp = new();
        int customers = await erp.Customers.CountAsync(ct);
        int activeOffers = await erp.Quotes.CountAsync(
            quote => quote.Status != "Kazanıldı" && quote.Status != "Kaybedildi",
            ct);
        int openTasks = await erp.Tasks.CountAsync(task => task.Status == "Açık", ct);
        DateTime monthStart = new(DateTime.Today.Year, DateTime.Today.Month, 1);
        decimal monthlySales = await erp.Payments
            .Where(payment => payment.PaymentDate >= monthStart)
            .SumAsync(payment => (decimal?)payment.Amount, ct) ?? 0m;
        return new DashboardStatisticsDto(customers, activeOffers, openTasks, monthlySales);
    }
}
public sealed class Microsoft365SummaryService : IMicrosoft365SummaryService
{
    private readonly AuthService _auth = new();
    private readonly GraphService _graph;

    public Microsoft365SummaryService() => _graph = new GraphService(_auth);

    public async Task<string> CreateSummaryAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(await _auth.GetCachedAccountNameAsync()))
        {
            return "Microsoft 365 oturumu açık değil. Takvim özeti için önce ERP'de Microsoft Giriş işlemini tamamlayın.";
        }

        try
        {
            int meetings = await GetTodayMeetingCountAsync(ct);
            return $"Bugün Microsoft 365 takviminde {meetings} etkinlik var. Yalnızca etkinlik sayısı alındı; başlık, katılımcı veya açıklama NVIDIA NIM'e gönderilmedi.";
        }
        catch (MsalUiRequiredException)
        {
            return "Microsoft 365 oturumu yenileme istiyor. Arka plan ajanı giriş penceresi açmaz; ERP'deki Microsoft Giriş düğmesiyle yeniden bağlanın.";
        }
    }

    public async Task<int> GetTodayMeetingCountAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(await _auth.GetCachedAccountNameAsync()))
        {
            throw new InvalidOperationException("Microsoft 365 takvimini okumak için önce ERP'de Microsoft Giriş işlemini tamamlayın.");
        }

        string response = await _graph.ListTodayCalendarAsync();
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(response);
        return document.RootElement.GetProperty("value").GetArrayLength();
    }
}
public sealed class TaskPlannerService : ITaskPlannerService
{
    public async Task<IReadOnlyList<string>> CreatePlanAsync(CancellationToken ct = default)
    {
        await using CrmDbContext crm = new();
        await using ArsanGazDbContext erp = new();
        List<string> plan = [];
        int overdueFollowUps = await crm.CrmCompanies.CountAsync(
            company => company.FollowUpAtUtc != null && company.FollowUpAtUtc < DateTime.UtcNow &&
                       company.Status != "Müşteri Oldu" && company.Status != "Kaybedildi",
            ct);
        if (overdueFollowUps > 0)
        {
            plan.Add($"{overdueFollowUps} gecikmiş CRM takibini ele al.");
        }

        int dueQuotes = await erp.Quotes.CountAsync(
            quote => quote.FollowUpDate <= DateTime.Today &&
                     quote.Status != "Kazanıldı" && quote.Status != "Kaybedildi",
            ct);
        if (dueQuotes > 0)
        {
            plan.Add($"{dueQuotes} takip tarihi gelen teklifi gözden geçir.");
        }

        int overdueInvoices = await erp.Invoices.CountAsync(
            invoice => invoice.DueDate < DateTime.Today && invoice.PaidAmount < invoice.TotalAmount,
            ct);
        if (overdueInvoices > 0)
        {
            plan.Add($"{overdueInvoices} vadesi geçmiş faturayı incele; tahsilat işlemi için kullanıcı onayı gerekir.");
        }

        DateTime collectionHorizon = DateTime.Today.AddDays(7);
        int dueCollections = await erp.CollectionReminders.CountAsync(
            reminder => reminder.Status == ArsanGazERP.Models.CollectionReminder.PendingStatus && reminder.DueDate <= collectionHorizon,
            ct);
        if (dueCollections > 0)
        {
            plan.Add($"{dueCollections} tahsilat hatırlatmasının vadesi 7 gün içinde veya geçmiş; müşterileri ara ve sonucu Operasyonlar > Tahsilat hatırlatmaları bölümüne işle.");
        }

        int openTasks = await erp.Tasks.CountAsync(task => task.Status == "Açık", ct);
        if (openTasks > 0)
        {
            plan.Add($"{openTasks} açık ERP görevi arasından bugünün önceliklerini seç.");
        }

        if (plan.Count == 0)
        {
            plan.Add("Bugün için gecikmiş CRM takibi, teklif veya fatura bulunamadı; satış hattını ve yeni adayları gözden geçir.");
        }

        return plan;
    }
}
public sealed class SalesForecastService : ISalesForecastService
{
    public async Task<SalesForecastDto> ForecastAsync(CancellationToken ct = default)
    {
        await using ArsanGazDbContext db = new();
        DateTime today = DateTime.Today;
        DateTime threeMonthsAgo = today.AddMonths(-3);
        decimal collectedInPeriod = await db.Payments
            .Where(payment => payment.PaymentDate >= threeMonthsAgo)
            .SumAsync(payment => (decimal?)payment.Amount, ct) ?? 0m;
        decimal openQuoteValue = await db.Quotes
            .Where(quote => quote.Currency == "TRY" &&
                            quote.Status != "Kazanıldı" && quote.Status != "Kaybedildi")
            .SumAsync(quote => (decimal?)quote.TotalAmount, ct) ?? 0m;
        int paymentCount = await db.Payments.CountAsync(payment => payment.PaymentDate >= threeMonthsAgo, ct);
        decimal historicalMonthlyAverage = collectedInPeriod / 3m;
        decimal expected = historicalMonthlyAverage + (openQuoteValue * 0.30m);
        decimal confidence = paymentCount < 3 ? 0.25m : paymentCount < 10 ? 0.45m : 0.60m;
        string summary = paymentCount == 0 && openQuoteValue == 0m
            ? "Henüz yeterli tahsilat veya açık teklif verisi yok; güvenilir tahmin üretilemedi."
            : $"Önümüzdeki ay için yaklaşık {expected:N2} TRY. Hesap: son 3 ayın aylık ortalama tahsilatı ({historicalMonthlyAverage:N2} TRY) + açık tekliflerin ihtiyatlı %30 ağırlığı ({openQuoteValue:N2} TRY). Güven: {confidence:P0}; bu bir istatistiksel garanti değildir.";
        return new SalesForecastDto(expected, confidence, summary);
    }
}
public sealed class AgentOrchestrator(ICrmAnalyticsService crm, IMicrosoft365SummaryService m365, ITaskPlannerService planner, ISalesForecastService forecast) : IAgentOrchestrator
{
    public async Task<AgentRunResult> RunFullAnalysisAsync(CancellationToken ct = default)
    {
        var c = await crm.GetDashboardStatisticsAsync(ct);
        string m;
        try
        {
            m = await m365.CreateSummaryAsync(ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            m = "Microsoft 365 özeti alınamadı: " + exception.Message;
        }
        var p = await planner.CreatePlanAsync(ct);
        var f = await forecast.ForecastAsync(ct);
        var steps = new List<string> { $"CRM: {c.TotalCustomers} müşteri, {c.ActiveOffers} aktif teklif", m, $"Satış tahmini: {f.Summary}" };
        steps.AddRange(p.Select(x => $"Görev: {x}"));
        return new AgentRunResult(true, "Birleşik AI Agent analizi tamamlandı.", steps);
    }
}
public sealed class AgentService(IAgentOrchestrator orchestrator, ICrmAnalyticsService crm, IMicrosoft365SummaryService m365, ITaskPlannerService planner, ISalesForecastService forecast) : IAgentService
{
    public async Task<string> ExecuteAsync(string command, CancellationToken ct = default) => command switch
    {
        AgentCommand.FullAnalysis => FormatRun(await orchestrator.RunFullAnalysisAsync(ct)),
        AgentCommand.CrmAnalysis => $"CRM analizi tamamlandı: {(await crm.GetDashboardStatisticsAsync(ct)).TotalCustomers} müşteri.",
        AgentCommand.M365Summary => await m365.CreateSummaryAsync(ct),
        AgentCommand.TaskPlanning => string.Join(Environment.NewLine, await planner.CreatePlanAsync(ct)),
        AgentCommand.SalesAnalysis => (await forecast.ForecastAsync(ct)).Summary,
        _ => "Bilinmeyen Agent komutu."
    };

    private static string FormatRun(AgentRunResult result) =>
        string.Join(Environment.NewLine, new[] { result.Summary }.Concat(result.Steps.Select(step => "• " + step)));
}
