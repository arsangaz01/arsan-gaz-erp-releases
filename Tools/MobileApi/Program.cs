using System.Text.Json;
using ArsanGazERP.MobileApi;

if (args is ["--yerel-eslestir", { Length: > 0 } localDeviceName, { Length: > 0 } tokenFile])
{
    MobileApiPaths.Ensure();
    File.WriteAllText(tokenFile, new DeviceStore().Add(localDeviceName));
    MobileLog.Write("Yerel komutla cihaz eşleştirildi: " + localDeviceName);
    return;
}

// --panel-sifre-ayarla <user>: sets the password (read from standard input) of an existing panel user, or creates the
// user as Yönetici. --panel-kullanici-ekle <user> <role>: adds a new user with that role.
if (args is ["--panel-sifre-ayarla", { Length: > 0 }] or ["--panel-kullanici-ekle", { Length: > 0 }, { Length: > 0 }])
{
    MobileApiPaths.Ensure();
    WebPanelUserStore.EnsureMigrated();
    string panelUser = args[1].Trim();
    bool addOnly = args[0] == "--panel-kullanici-ekle";
    string? panelRole = addOnly ? PanelRoles.Normalize(args[2]) : PanelRoles.Admin;
    string? panelPassword = Console.In.ReadLine();
    string? problem = WebPanelUserStore.UserNameProblem(panelUser)
        ?? (panelRole is null ? "Yetki Yönetici, Operatör veya İzleyici olmalı." : null)
        ?? WebPanelUserStore.PasswordProblem(panelPassword, panelUser);
    if (problem is null)
    {
        problem = WebPanelUserStore.Update(users =>
        {
            WebPanelUser? existing = users.FirstOrDefault(u => string.Equals(u.UserName, panelUser, StringComparison.OrdinalIgnoreCase));
            if (existing is not null && addOnly)
            {
                return "Bu kullanıcı adı zaten var.";
            }

            if (existing is null)
            {
                existing = new WebPanelUser
                {
                    Id = users.Count == 0 ? 1 : users.Max(u => u.Id) + 1,
                    UserName = panelUser,
                    DisplayName = panelUser,
                    Role = panelRole!,
                    CreatedBy = "Komut satırı"
                };
                users.Add(existing);
            }

            WebPanelUserStore.SetPassword(existing, panelPassword!);
            existing.MustChangePassword = false;
            return null;
        });
    }

    if (problem is not null)
    {
        Console.Error.WriteLine(problem + " (Şifre standart girişten okunur.)");
        Environment.ExitCode = 1;
        return;
    }

    MobileLog.Write($"Web panel kullanıcısı komut satırından ayarlandı: {panelUser} ({panelRole})");
    WebPanelAudit.Write("Komut satırı", addOnly ? "Kullanıcı oluşturuldu" : "Şifre ayarlandı", panelUser, panelRole, "yerel");
    return;
}

using Mutex singleInstance = new(true, @"Local\ArsanGazERP.MobileApi", out bool firstInstance);
if (!firstInstance)
{
    return;
}

MobileApiPaths.Ensure();
WebPanelTrustedIps.EnsureConfigured();
try
{
    WebPanelUserStore.EnsureMigrated();
}
catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException)
{
    MobileLog.Write("Web paneli kullanıcıları hazırlanamadı: " + exception.Message);
}

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
