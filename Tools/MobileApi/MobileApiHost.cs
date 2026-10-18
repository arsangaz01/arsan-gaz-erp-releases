using System.Text.Json;

namespace ArsanGazERP.MobileApi;

// Builds the web application; shared by the service and the self-test so the test sees the real endpoint table.
internal static class MobileApiHost
{
    internal static WebApplication Build(string[] args, bool hostedServices)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.Logging.ClearProviders();
        if (!OperatingSystem.IsWindows())
        {
            builder.Logging.AddSimpleConsole(options => options.SingleLine = true).SetMinimumLevel(LogLevel.Warning);
        }

        builder.WebHost.UseUrls($"http://127.0.0.1:{MobileApiPaths.Port}");
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 80 * 1024 * 1024);
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.SerializerOptions.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
        });
        builder.Services.AddSingleton<DeviceStore>();
        builder.Services.AddSingleton<WebPanelAuth>();
        builder.Services.AddSingleton<MobileChatService>();
        if (hostedServices)
        {
            if (OperatingSystem.IsWindows())
            {
                builder.Services.AddHostedService<TunnelService>();
            }
            else
            {
                MobileLog.Write("Linux sunucu modu: tünel (cloudflared) ayrı systemd servisi olarak çalışır; PC kontrol komutları kapalı.");
            }

            builder.Services.AddHostedService<CursorRelayNotifier>();
            builder.Services.AddHostedService<TupTakipSyncHostedService>();
        }

        WebApplication app = builder.Build();
        app.UseMiddleware<DeviceAuthMiddleware>();
        app.UseMiddleware<IdempotencyMiddleware>();
        MobileEndpoints.Map(app);
        FillEndpoints.Map(app);
        StockCountEndpoints.MapMobile(app);
        TripPlanEndpoints.MapMobile(app);
        CylinderTripEndpoints.Map(app);
        VehicleEndpoints.Map(app);
        MobileCampaignEndpoints.Map(app);
        WebPanelEndpoints.Map(app);
        CustomerPortalEndpoints.Map(app);
        return app;
    }
}
