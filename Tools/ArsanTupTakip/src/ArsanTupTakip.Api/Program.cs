using ArsanTupTakip;
using ArsanTupTakip.Api;
using ArsanTupTakip.Database;

// Installer commands; passwords are read from standard input so they never appear in the process list.
//   --sema-hazirla                 creates the database schema and tables (empty database only gets tables once)
//   --ilk-yonetici <kullanıcı>     creates the first Yönetici, who must change the password at first login
//   --sifre-sifirla <kullanıcı>    sets a temporary password for an existing user (must change at next login)
if (args is ["--sema-hazirla"])
{
    await TupTakipDatabase.EnsureSchemaAsync();
    Console.WriteLine("Veritabanı şeması hazır.");
    return;
}

if (args is ["--ilk-yonetici", { Length: > 0 } adminName])
{
    string? problem = WebPanelUserStore.CreateInitialAdmin(adminName.Trim(), Console.In.ReadLine() ?? string.Empty);
    if (problem is not null)
    {
        Console.Error.WriteLine(problem);
        Environment.ExitCode = 1;
        return;
    }

    WebPanelAudit.Write("Kurulum", "İlk yönetici oluşturuldu", adminName.Trim(), "ilk girişte şifre değiştirecek", "yerel");
    Console.WriteLine("İlk yönetici oluşturuldu.");
    return;
}

if (args is ["--sifre-sifirla", { Length: > 0 } resetName])
{
    string user = resetName.Trim();
    string password = Console.In.ReadLine() ?? string.Empty;
    string? problem = WebPanelUserStore.FindByName(user) is null ? "Kullanıcı bulunamadı." : WebPanelUserStore.PasswordProblem(password, user);
    if (problem is null)
    {
        WebPanelUserStore.Update(users =>
        {
            WebPanelUser target = users.First(u => string.Equals(u.UserName, user, StringComparison.OrdinalIgnoreCase));
            WebPanelUserStore.SetPassword(target, password);
            target.MustChangePassword = true;
            return 0;
        });
        WebPanelAudit.Write("Komut satırı", "Şifre sıfırlandı", user, "ilk girişte değiştirecek", "yerel");
        Console.WriteLine("Şifre sıfırlandı; kullanıcı ilk girişte yeni şifre belirleyecek.");
        return;
    }

    Console.Error.WriteLine(problem);
    Environment.ExitCode = 1;
    return;
}

int port = int.TryParse(Environment.GetEnvironmentVariable("TUPTAKIP_PORT"), out int configuredPort) && configuredPort is > 0 and < 65536 ? configuredPort : 5080;
string pathBase = (Environment.GetEnvironmentVariable("TUPTAKIP_PATH_BASE") ?? "/tuptakip").Trim().TrimEnd('/');

AppLog.Write($"ArsanTüpTakip sunucusu başlatılıyor (127.0.0.1:{port}, yol öneki '{pathBase}').");
await TupTakipDatabase.EnsureSchemaAsync();

WebApplication app = ApiHost.Build(args, $"http://127.0.0.1:{port}", pathBase);

try
{
    await app.RunAsync();
}
catch (Exception exception)
{
    AppLog.Write("Sunucu durdu: " + exception.GetBaseException().Message);
    throw;
}
