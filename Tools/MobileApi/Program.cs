using System.Text.Json;
using ArsanGazERP.MobileApi;

if (args is ["--yerel-eslestir", { Length: > 0 } localDeviceName, { Length: > 0 } tokenFile])
{
    MobileApiPaths.Ensure();
    File.WriteAllText(tokenFile, new DeviceStore().Add(localDeviceName));
    MobileLog.Write("Yerel komutla cihaz eşleştirildi: " + localDeviceName);
    return;
}

if (args is ["--panel-sifre-ayarla", { Length: > 0 } panelUser])
{
    MobileApiPaths.Ensure();
    string? panelPassword = Console.In.ReadLine();
    if (string.IsNullOrEmpty(panelPassword) || panelPassword.Length < 8)
    {
        Console.Error.WriteLine("Şifre en az 8 karakter olmalı (standart girişten okunur).");
        Environment.ExitCode = 1;
        return;
    }

    WebPanelAuth.SaveCredentials(WebPanelAuth.CreateCredentials(panelUser, panelPassword));
    MobileLog.Write("Web panel kullanıcısı ayarlandı: " + panelUser);
    return;
}

using Mutex singleInstance = new(true, @"Local\ArsanGazERP.MobileApi", out bool firstInstance);
if (!firstInstance)
{
    return;
}

MobileApiPaths.Ensure();
TunnelService.ApplyPreferredPublicUrl();
MobileLog.Write("Mobil sunucu başlatılıyor.");
try
{
    await ArsanGazERP.Services.CylinderSchemaService.EnsureAsync();
}
catch (Exception exception)
{
    MobileLog.Write("Tüp takip şeması hazırlanamadı: " + exception.GetBaseException().Message);
}

WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
builder.Logging.ClearProviders();
builder.WebHost.UseUrls($"http://127.0.0.1:{MobileApiPaths.Port}");
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 80 * 1024 * 1024);
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
MobileCampaignEndpoints.Map(app);
WebPanelEndpoints.Map(app);

try
{
    await app.RunAsync();
}
catch (Exception exception)
{
    MobileLog.Write("Mobil sunucu durdu: " + exception.GetBaseException().Message);
    throw;
}
