using System.Text.Json;
using ArsanGazERP.MobileApi;

if (args is ["--yerel-eslestir", { Length: > 0 } localDeviceName, { Length: > 0 } tokenFile])
{
    MobileApiPaths.Ensure();
    File.WriteAllText(tokenFile, new DeviceStore().Add(localDeviceName));
    MobileLog.Write("Yerel komutla cihaz eşleştirildi: " + localDeviceName);
    return;
}

using Mutex singleInstance = new(true, @"Local\ArsanGazERP.MobileApi", out bool firstInstance);
if (!firstInstance)
{
    return;
}

MobileApiPaths.Ensure();
MobileLog.Write("Mobil sunucu başlatılıyor.");

WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
builder.Logging.ClearProviders();
builder.WebHost.UseUrls($"http://127.0.0.1:{MobileApiPaths.Port}");
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 256 * 1024);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
});
builder.Services.AddSingleton<DeviceStore>();
builder.Services.AddSingleton<PairingService>();
builder.Services.AddSingleton<MobileChatService>();
builder.Services.AddHostedService<TunnelService>();
builder.Services.AddHostedService<CursorRelayNotifier>();

WebApplication app = builder.Build();
app.UseMiddleware<DeviceAuthMiddleware>();
MobileEndpoints.Map(app);

try
{
    await app.RunAsync();
}
catch (Exception exception)
{
    MobileLog.Write("Mobil sunucu durdu: " + exception.GetBaseException().Message);
    throw;
}
