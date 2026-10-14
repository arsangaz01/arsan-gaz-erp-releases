using System.Text.Json;
using ArsanTupTakip.Api;

namespace ArsanTupTakip;

// Builds the web application; Program.cs runs it and the tests start it on a free local port.
internal static class ApiHost
{
    internal static WebApplication Build(string[] args, string url, string pathBase)
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

        builder.WebHost.UseUrls(url);
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 10 * 1024 * 1024);
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.SerializerOptions.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
        });

        // One instance each: panel and phone logins share the lockout counters and the session stores.
        builder.Services.AddSingleton<DeviceStore>();
        builder.Services.AddSingleton<WebPanelAuth>();

        WebApplication app = builder.Build();
        app.Use(ConcurrencyErrors.Handle);
        if (pathBase.Length > 0)
        {
            app.UsePathBase(pathBase);
        }

        app.UseRouting();
        app.UseMiddleware<DeviceAuthMiddleware>();
        app.UseMiddleware<IdempotencyMiddleware>();
        MobileEndpoints.Map(app);
        CylinderTripEndpoints.Map(app);
        VehicleEndpoints.Map(app);
        WebPanelEndpoints.Map(app);
        CustomerPortalEndpoints.Map(app);
        return app;
    }
}
