using ArsanGazERP.Modules.AI.Common;
using ArsanGazERP.Modules.AI.Models;
namespace ArsanGazERP.Modules.AI.Services;
public sealed class CrmAnalyticsService : ICrmAnalyticsService
{
    public Task<DashboardStatisticsDto> GetDashboardStatisticsAsync(CancellationToken ct = default)
        => Task.FromResult(new DashboardStatisticsDto(0, 0, 0, 0m));
}
public sealed class Microsoft365SummaryService : IMicrosoft365SummaryService
{
    public Task<string> CreateSummaryAsync(CancellationToken ct = default)
        => Task.FromResult("Microsoft 365 bağlantısı hazır. Graph verileri bağlandığında özet burada gösterilecek.");
    public Task<int> GetTodayMeetingCountAsync(CancellationToken ct = default) => Task.FromResult(0);
}
public sealed class TaskPlannerService : ITaskPlannerService
{
    public Task<IReadOnlyList<string>> CreatePlanAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(["CRM verilerini kontrol et", "Açık teklifleri incele", "Microsoft 365 özetini oluştur"]);
}
public sealed class SalesForecastService : ISalesForecastService
{
    public Task<SalesForecastDto> ForecastAsync(CancellationToken ct = default)
        => Task.FromResult(new SalesForecastDto(0m, 0m, "Satış geçmişi bağlandığında tahmin üretilecek."));
}
public sealed class AgentOrchestrator(ICrmAnalyticsService crm, IMicrosoft365SummaryService m365, ITaskPlannerService planner, ISalesForecastService forecast) : IAgentOrchestrator
{
    public async Task<AgentRunResult> RunFullAnalysisAsync(CancellationToken ct = default)
    {
        var c = await crm.GetDashboardStatisticsAsync(ct);
        var m = await m365.CreateSummaryAsync(ct);
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
        AgentCommand.FullAnalysis => (await orchestrator.RunFullAnalysisAsync(ct)).Summary,
        AgentCommand.CrmAnalysis => $"CRM analizi tamamlandı: {(await crm.GetDashboardStatisticsAsync(ct)).TotalCustomers} müşteri.",
        AgentCommand.M365Summary => await m365.CreateSummaryAsync(ct),
        AgentCommand.TaskPlanning => string.Join(Environment.NewLine, await planner.CreatePlanAsync(ct)),
        AgentCommand.SalesAnalysis => (await forecast.ForecastAsync(ct)).Summary,
        _ => "Bilinmeyen Agent komutu."
    };
}
