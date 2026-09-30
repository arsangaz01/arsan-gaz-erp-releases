using Microsoft.Extensions.DependencyInjection;
namespace ArsanGazERP.Modules.AI.Services;
public static class AgentServiceRegistration
{
    public static IServiceCollection AddAgentDashboard(this IServiceCollection services)
    {
        services.AddSingleton<ICrmAnalyticsService, CrmAnalyticsService>();
        services.AddSingleton<IMicrosoft365SummaryService, Microsoft365SummaryService>();
        services.AddSingleton<ITaskPlannerService, TaskPlannerService>();
        services.AddSingleton<ISalesForecastService, SalesForecastService>();
        services.AddSingleton<IAgentOrchestrator, AgentOrchestrator>();
        services.AddSingleton<IAgentService, AgentService>();
        services.AddTransient<ArsanGazERP.Modules.AI.ViewModels.AgentDashboardViewModel>();
        return services;
    }
}
