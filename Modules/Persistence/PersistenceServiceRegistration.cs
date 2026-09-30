using Microsoft.Extensions.DependencyInjection;

namespace ArsanGazERP.Modules.Persistence;

public static class PersistenceServiceRegistration
{
    public static IServiceCollection AddArsanGazPersistentSession(this IServiceCollection services)
    {
        services.AddSingleton<AppPersistentState>();
        services.AddSingleton<Microsoft365PersistentSessionService>();
        services.AddSingleton<PersistentStartupCoordinator>();
        return services;
    }
}
