using ArsanGazERP.Modules.AI.Models;
namespace ArsanGazERP.Modules.AI.Services;
public interface IAgentService { Task<string> ExecuteAsync(string command, CancellationToken ct = default); }
public interface ICrmAnalyticsService { Task<DashboardStatisticsDto> GetDashboardStatisticsAsync(CancellationToken ct = default); }
public interface IMicrosoft365SummaryService { Task<string> CreateSummaryAsync(CancellationToken ct = default); Task<int> GetTodayMeetingCountAsync(CancellationToken ct = default); }
public interface ITaskPlannerService { Task<IReadOnlyList<string>> CreatePlanAsync(CancellationToken ct = default); }
public interface ISalesForecastService { Task<SalesForecastDto> ForecastAsync(CancellationToken ct = default); }
public interface IAgentOrchestrator { Task<AgentRunResult> RunFullAnalysisAsync(CancellationToken ct = default); }
