using System.Text.Json;
using ArsanGazERP.MobileApi;

// Linux: every LocalApplicationData path (MobileApi, AgentWatcher data, CloudAgent, Gmail...) resolves under
// $XDG_DATA_HOME, so ARSANGAZ_DATA_DIR relocates them all. Must run before any path class is touched.
if (!OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("ARSANGAZ_DATA_DIR") is { Length: > 0 } dataDirectory)
{
    Environment.SetEnvironmentVariable("XDG_DATA_HOME", dataDirectory);
}

if (args is ["--self-test"])
{
    Environment.ExitCode = await MobileApiSelfTest.RunAsync();
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
        ?? (panelRole is null ? "Yetki Yönetici, Operatör, Saha veya İzleyici olmalı." : null)
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

            if (existing?.DeletedAt is not null)
            {
                return "Bu kullanıcı adı silinmiş bir hesaba ait; başka bir ad seçin.";
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

WebApplication app = MobileApiHost.Build(args, hostedServices: true);

try
{
    await app.RunAsync();
}
catch (Exception exception)
{
    MobileLog.Write("Mobil sunucu durdu: " + exception.GetBaseException().Message);
    throw;
}
