namespace ArsanGazERP.Modules.AI.Models;
public sealed record AgentActivityModel(DateTime Time, string Category, string Description);
public sealed record DashboardStatisticsDto(int TotalCustomers, int ActiveOffers, int OpenTasks, decimal MonthlySales);
public sealed record SalesForecastDto(decimal ExpectedSales, decimal Confidence, string Summary);
public sealed record AgentRunResult(bool Success, string Summary, IReadOnlyList<string> Steps);
