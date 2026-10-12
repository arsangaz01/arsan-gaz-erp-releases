using ArsanGazERP.Data;
using ArsanGazERP.Models;
using ArsanGazERP.Services;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace ArsanGazERP.MobileApi;

// --self-test: permissions, logins, session lifetimes, endpoint coverage and concurrency, run against a temporary data
// folder and a temporary SQLite database (never the real users, devices or ERP data). Exit code 0 = all passed.
internal static class MobileApiSelfTest
{
    private const string Password = "Test-Sifre-2026!";
    private static int _failed;

    internal static async Task<int> RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "arsangaz-mobileapi-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("ARSANGAZ_MOBILEAPI_DIR", root);
        Environment.SetEnvironmentVariable("ARSANGAZ_DB_PROVIDER", "sqlite");
        Environment.SetEnvironmentVariable("ARSANGAZ_DB_CONFIG", Path.Combine(root, "db-config.json"));
        Environment.SetEnvironmentVariable("ARSANGAZ_ERP_DB", Path.Combine(root, "erp.db"));
        Environment.SetEnvironmentVariable("ARSANGAZ_CRM_DB", Path.Combine(root, "crm.db"));
        TelegramNotifier.Disabled = true;
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (MobileApiPaths.Root != root)
        {
            Console.Error.WriteLine("Self-test veri klasörü ayarlanamadı; durduruldu.");
            return 2;
        }

        try
        {
            Run("Yetki kataloğu ve rol varsayılanları", Catalog);
            Run("Kullanıcıya özel yetkiler", Overrides);
            Run("Tüp hareketi yetkisi", MovePermissions);
            await RunAsync("Kullanıcı yönetimi ve son yönetici koruması", UserManagementAsync);
            await RunAsync("Panel 1 saat / mobil 24 saat oturum süresi", LifetimesAsync);
            await RunAsync("Aynı anda girişler birbirini bozmaz", ParallelLoginsAsync);
            await RunAsync("Mobil şifre değişimi ve kilitleme", PasswordAndLockoutAsync);
            await RunAsync("Mobil istek yetki denetimi", MiddlewareAsync);
            Run("Her uç nokta bir yetkiye bağlı", EndpointCoverage);
            await RunAsync("Tekrarlanan okutma bir kez uygulanır", IdempotencyAsync);
            await RunAsync("Aynı tüpe eşzamanlı hareket", ConcurrentMovesAsync);
        }
        finally
        {
            SqliteCleanup();
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        Console.WriteLine(_failed == 0 ? "MobileApi self-test: tümü geçti." : $"MobileApi self-test: {_failed} test başarısız.");
        return _failed == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            Console.WriteLine("OK   " + name);
        }
        catch (Exception exception)
        {
            _failed++;
            Console.WriteLine($"FAIL {name}: {exception.Message}");
        }
    }

    private static async Task RunAsync(string name, Func<Task> test)
    {
        try
        {
            await test();
            Console.WriteLine("OK   " + name);
        }
        catch (Exception exception)
        {
            _failed++;
            Console.WriteLine($"FAIL {name}: {exception.GetBaseException().Message}");
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Catalog()
    {
        Check(PanelPermissions.All.Distinct().Count() == PanelPermissions.Catalog.Count, "yinelenen yetki anahtarı");
        Check(PanelPermissions.Catalog.All(p => p.Group.Length > 0 && p.Label.Length > 0), "etiketsiz yetki");
        Check(PanelPermissions.Catalog.Count >= 25, "katalog beklenenden küçük");
        Check(PanelRoles.Effective(PanelRoles.Admin, null, PanelPermissions.All).Count == PanelPermissions.All.Count, "Yönetici daraltılabildi");
        foreach (string role in PanelRoles.All.Where(r => r != PanelRoles.Admin))
        {
            Check(PanelRoles.Defaults(role).All(PanelPermissions.IsKnown), role + " bilinmeyen yetki içeriyor");
            Check(!PanelRoles.Defaults(role).Any(PanelPermissions.IsAdminOnly), role + " yalnızca yöneticiye ait yetki içeriyor");
        }

        string[] writes =
        [
            PanelPermissions.CylinderRegister, PanelPermissions.CylinderEdit, PanelPermissions.CylinderMove, PanelPermissions.CylinderDeliver,
            PanelPermissions.CylinderReturn, PanelPermissions.CustomerCreate, PanelPermissions.CrmWrite, PanelPermissions.TripStart,
            PanelPermissions.TripLoad, PanelPermissions.TripClose, PanelPermissions.VehicleManage, PanelPermissions.CampaignManage
        ];
        Check(!PanelRoles.Defaults(PanelRoles.Viewer).Intersect(writes).Any(), "İzleyici kayıt değiştirebiliyor");
        Check(PanelRoles.Defaults(PanelRoles.Driver).Contains(PanelPermissions.TripLoad)
            && !PanelRoles.Defaults(PanelRoles.Driver).Contains(PanelPermissions.CylinderEdit), "Saha varsayılanları hatalı");
        Check(!PanelRoles.Defaults(PanelRoles.Operator).Contains(PanelPermissions.ManageUsers), "Operatör kullanıcı yönetebiliyor");
        Check(PanelRoles.Normalize("şoför") == PanelRoles.Driver && PanelRoles.Normalize("izleyici") == PanelRoles.Viewer
            && PanelRoles.Normalize("yok") is null, "rol adı eşlemesi hatalı");
    }

    private static void Overrides()
    {
        List<string> wanted = [.. PanelRoles.Defaults(PanelRoles.Operator).Where(k => k != PanelPermissions.CylinderEdit),
            PanelPermissions.CursorTask, PanelPermissions.ManageUsers];
        (List<string> extra, List<string> removed) = PanelRoles.Overrides(PanelRoles.Operator, wanted);
        Check(extra.SequenceEqual([PanelPermissions.CursorTask]), "ek yetki listesi: " + string.Join(",", extra));
        Check(removed.SequenceEqual([PanelPermissions.CylinderEdit]), "çıkarılan yetki listesi: " + string.Join(",", removed));
        IReadOnlyList<string> effective = PanelRoles.Effective(PanelRoles.Operator, extra, removed);
        Check(effective.Contains(PanelPermissions.CursorTask) && !effective.Contains(PanelPermissions.CylinderEdit), "etkin yetkiler hatalı");
        Check(!PanelRoles.Effective(PanelRoles.Viewer, [PanelPermissions.ManageUsers, "uydurma"], null).Contains(PanelPermissions.ManageUsers),
            "yalnızca yöneticiye ait yetki elle eklenebildi");
    }

    private static void MovePermissions()
    {
        Check(PanelPermissions.ForMove((int)CylinderStatus.Vehicle, (int)CylinderStatus.Customer) == PanelPermissions.CylinderDeliver, "teslim");
        Check(PanelPermissions.ForMove((int)CylinderStatus.Customer, (int)CylinderStatus.Warehouse) == PanelPermissions.CylinderReturn, "iade");
        Check(PanelPermissions.ForMove((int)CylinderStatus.Warehouse, (int)CylinderStatus.Filling) == PanelPermissions.CylinderMove, "hareket");
    }

    private static WebPanelAuth NewAuth(Func<DateTime>? clock = null) =>
        new() { UtcNow = clock ?? (() => DateTime.UtcNow), FailureDelay = TimeSpan.Zero };

    private static WebPanelUser AddUser(string name, string role, bool mustChange = false) => WebPanelUserStore.Update(users =>
    {
        users.RemoveAll(u => u.UserName == name);
        WebPanelUser user = new()
        {
            Id = users.Count == 0 ? 1 : users.Max(u => u.Id) + 1,
            UserName = name,
            DisplayName = name,
            Role = role,
            CreatedBy = "self-test",
            MustChangePassword = mustChange
        };
        WebPanelUserStore.SetPassword(user, Password);
        users.Add(user);
        return user;
    });

    private static Task UserManagementAsync()
    {
        WebPanelAuth auth = NewAuth();
        WebPanelUser admin = AddUser("yonetici1", PanelRoles.Admin);
        WebPanelUser driver = AddUser("sofor1", PanelRoles.Driver);

        Check(auth.SetPermissions(admin, "yerel", admin.Id, [PanelPermissions.Summary], false) is not null, "Yönetici daraltılabildi");
        Check(auth.SetPermissions(admin, "yerel", driver.Id, [PanelPermissions.ManageUsers], false) is not null, "kullanıcı yönetimi Saha'ya verildi");
        Check(auth.SetPermissions(admin, "yerel", driver.Id, ["uydurma"], false) is not null, "bilinmeyen yetki kabul edildi");

        List<string> wanted = [.. PanelRoles.Defaults(PanelRoles.Driver).Where(k => k != PanelPermissions.CylinderReturn), PanelPermissions.CustomerCreate];
        Check(auth.SetPermissions(admin, "yerel", driver.Id, wanted, false) is null, "yetki kaydı başarısız");
        WebPanelUser saved = WebPanelUserStore.Find(driver.Id)!;
        Check(PanelRoles.Has(saved, PanelPermissions.CustomerCreate) && !PanelRoles.Has(saved, PanelPermissions.CylinderReturn), "özel yetkiler kalıcı değil");
        Check(File.ReadAllText(WebPanelUserStore.FilePath).Contains(PanelPermissions.CustomerCreate, StringComparison.Ordinal), "özel yetki dosyaya yazılmadı");

        Check(auth.SetPermissions(admin, "yerel", driver.Id, null, true) is null, "varsayılana dönüş başarısız");
        Check(PanelRoles.Effective(WebPanelUserStore.Find(driver.Id)!).SequenceEqual(PanelRoles.Defaults(PanelRoles.Driver)), "varsayılana dönülmedi");

        auth.SetPermissions(admin, "yerel", driver.Id, wanted, false);
        Check(auth.UpdateUser(admin, "yerel", driver.Id, "sofor1", PanelRoles.Viewer, true) is null, "rol değişimi başarısız");
        WebPanelUser viewer = WebPanelUserStore.Find(driver.Id)!;
        Check(viewer.ExtraPermissions.Count == 0 && viewer.RemovedPermissions.Count == 0, "rol değişince özel yetkiler temizlenmedi");

        Check(auth.UpdateUser(admin, "yerel", admin.Id, "yonetici1", PanelRoles.Operator, true) is not null, "yönetici kendini düşürebildi");
        Check(auth.UpdateUser(viewer, "yerel", admin.Id, "yonetici1", PanelRoles.Operator, true) is not null, "son aktif yönetici düşürülebildi");
        Check(auth.UpdateUser(viewer, "yerel", admin.Id, "yonetici1", PanelRoles.Admin, false) is not null, "son aktif yönetici pasife alınabildi");
        Check(WebPanelUserStore.Find(admin.Id)!.Role == PanelRoles.Admin, "yönetici rolü değişti");
        return Task.CompletedTask;
    }

    private static async Task LifetimesAsync()
    {
        DateTime now = DateTime.UtcNow;
        WebPanelAuth auth = NewAuth(() => now);
        DeviceStore devices = new();
        WebPanelUser user = AddUser("sure1", PanelRoles.Operator);

        string session = auth.CreateSession(user, "yerel");
        now = now.AddMinutes(59);
        Check(auth.CurrentByToken(session) is not null, "panel oturumu 59. dakikada kapandı");
        now = now.AddMinutes(1);
        Check(auth.CurrentByToken(session) is null, "panel oturumu 1 saatten uzun sürdü");

        (MobileLogin? login, string? error) = await auth.MobileLoginAsync("yerel", "sure1", Password, "Telefon", devices);
        Check(login is not null, "mobil giriş başarısız: " + error);
        Check(login!.Device.ExpiresUtc == now + TimeSpan.FromHours(24), "mobil bitiş zamanı 24 saat değil");
        now = now.AddHours(23).AddMinutes(59);
        Check(auth.CheckDevice(devices.Validate(login.Token), devices).User is not null, "mobil oturum 24 saatten önce kapandı");
        now = now.AddMinutes(1);
        Check(auth.CheckDevice(devices.Validate(login.Token), devices).User is null, "mobil oturum 24 saatten uzun sürdü");
        Check(devices.Validate(login.Token) is null, "süresi dolan mobil oturum silinmedi");

        WebPanelUser lifetimeAdmin = AddUser("sureyonetici", PanelRoles.Admin);
        (MobileLogin? adminLogin, string? adminError) = await auth.MobileLoginAsync("yerel", "sureyonetici", Password, "Telefon", devices);
        Check(adminLogin is not null, "yönetici mobil girişi başarısız: " + adminError);
        Check(adminLogin!.Device.ExpiresUtc == now + WebPanelAuth.AdminMobileLifetime, "yönetici mobil oturumu çıkışa kadar sürmüyor");
        now = now.AddDays(30);
        Check(auth.CheckDevice(devices.Validate(adminLogin.Token), devices).User?.Id == lifetimeAdmin.Id, "yönetici mobil oturumu 30 gün sonra kapandı");
        now = now.AddDays(-30);
    }

    private static async Task ParallelLoginsAsync()
    {
        WebPanelAuth auth = NewAuth();
        DeviceStore devices = new();
        WebPanelUser first = AddUser("paralel1", PanelRoles.Driver);
        WebPanelUser second = AddUser("paralel2", PanelRoles.Viewer);
        int before = devices.All().Count;

        Task<(MobileLogin? Login, string? Error)>[] phones = Enumerable.Range(0, 12)
            .Select(i => Task.Run(() => auth.MobileLoginAsync($"10.0.0.{i % 2 + 1}", i % 2 == 0 ? "paralel1" : "paralel2", Password, "Telefon " + i, devices)))
            .ToArray();
        string[] browsers = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() => auth.CreateSession(i % 2 == 0 ? first : second, "yerel"))));
        (MobileLogin? Login, string? Error)[] results = await Task.WhenAll(phones);

        Check(results.All(r => r.Login is not null), "eşzamanlı mobil girişlerden biri başarısız: " + results.FirstOrDefault(r => r.Login is null).Error);
        string[] tokens = [.. results.Select(r => r.Login!.Token), .. browsers];
        Check(tokens.Distinct().Count() == tokens.Length, "aynı oturum anahtarı iki kez üretildi");
        Check(devices.All().Count == before + 12, "eşzamanlı girişlerde cihaz kaydı kayboldu");
        Check(results.All(r => auth.CheckDevice(devices.Validate(r.Login!.Token), devices).User is not null), "yeni giriş başka bir telefonun oturumunu kapattı");
        Check(browsers.All(t => auth.CurrentByToken(t) is not null), "yeni giriş başka bir tarayıcının oturumunu kapattı");
        Check(WebPanelUserStore.Find(first.Id)!.LastLoginIp == "10.0.0.1" && WebPanelUserStore.Find(second.Id)!.LastLoginIp == "10.0.0.2",
            "son giriş bilgisi başka kullanıcıya yazıldı");
    }

    private static async Task PasswordAndLockoutAsync()
    {
        WebPanelAuth auth = NewAuth();
        DeviceStore devices = new();
        AddUser("sifre1", PanelRoles.Driver, mustChange: true);
        (MobileLogin? phoneA, _) = await auth.MobileLoginAsync("yerel", "sifre1", Password, "A", devices);
        (MobileLogin? phoneB, _) = await auth.MobileLoginAsync("yerel", "sifre1", Password, "B", devices);
        Check(phoneA is not null && phoneB is not null && phoneA.User.MustChangePassword, "zorunlu şifre değişimi işaretli değil");

        (string? error, int stamp) = await auth.ChangeOwnPasswordAsync(phoneA!.User, "yerel", Password, "Yeni-Sifre-2026!", "Yeni-Sifre-2026!", "mobil");
        Check(error is null, "şifre değişimi başarısız: " + error);
        devices.UpdateStamp(phoneA.Device.Id, stamp);
        devices.RemoveForUser(phoneA.User.Id, exceptId: phoneA.Device.Id);
        Check(auth.CheckDevice(devices.Validate(phoneA.Token), devices).User is { MustChangePassword: false }, "şifreyi değiştiren telefonun oturumu kapandı");
        Check(auth.CheckDevice(devices.Validate(phoneB!.Token), devices).User is null, "diğer telefonun oturumu açık kaldı");

        for (int i = 0; i < 5; i++)
        {
            await auth.MobileLoginAsync("10.9.9.9", "sifre1", "yanlis-sifre", "C", devices);
        }

        (MobileLogin? locked, string? lockError) = await auth.MobileLoginAsync("10.9.9.9", "sifre1", "Yeni-Sifre-2026!", "C", devices);
        Check(locked is null && lockError!.Contains("dakika", StringComparison.Ordinal), "5 hatalı denemeden sonra kilitlenmedi");
    }

    private static async Task MiddlewareAsync()
    {
        DateTime now = DateTime.UtcNow;
        WebPanelAuth auth = NewAuth(() => now);
        DeviceStore devices = new();
        AddUser("izleyici1", PanelRoles.Viewer);
        AddUser("zorunlu1", PanelRoles.Operator, mustChange: true);
        (MobileLogin? viewer, _) = await auth.MobileLoginAsync("yerel", "izleyici1", Password, "Telefon", devices);
        (MobileLogin? forced, _) = await auth.MobileLoginAsync("yerel", "zorunlu1", Password, "Telefon", devices);

        Check(await CallAsync(auth, devices, viewer!.Token, new PanelPermission(PanelPermissions.CylinderView)) == 200, "görüntüleme reddedildi");
        Check(await CallAsync(auth, devices, viewer.Token, new PanelPermission(PanelPermissions.CylinderRegister)) == 403, "izleyici tüp kaydedebildi");
        Check(await CallAsync(auth, devices, viewer.Token) == 403, "yetkisiz tanımlı uç nokta açıldı");
        Check(await CallAsync(auth, devices, "uydurma-anahtar", new SignedInOnly()) == 401, "geçersiz anahtar kabul edildi");
        Check(await CallAsync(auth, devices, forced!.Token, new PanelPermission(PanelPermissions.CylinderView)) == 403, "şifre değişimi atlanabildi");
        Check(await CallAsync(auth, devices, forced.Token, new SignedInOnly(), new PanelOpenBeforePasswordChange()) == 200, "şifre değişimi ekranı açılmadı");

        List<PairedDevice> rows = System.Text.Json.JsonSerializer.Deserialize<List<PairedDevice>>(File.ReadAllText(MobileApiPaths.DevicesFile))!;
        rows.Add(new PairedDevice("eski", "Eski telefon", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("eski-token"u8.ToArray())),
            DateTime.UtcNow.AddDays(-30), null));
        File.WriteAllText(MobileApiPaths.DevicesFile, System.Text.Json.JsonSerializer.Serialize(rows));
        Check(await CallAsync(auth, new DeviceStore(), "eski-token", new SignedInOnly()) == 401, "eski eşleştirme anahtarı hâlâ çalışıyor");

        now = now.AddHours(24);
        Check(await CallAsync(auth, devices, viewer.Token, new SignedInOnly()) == 401, "süresi dolan mobil anahtar kabul edildi");
    }

    private static async Task<int> CallAsync(WebPanelAuth auth, DeviceStore devices, string token, params object[] metadata)
    {
        DefaultHttpContext context = new();
        context.Request.Path = "/api/deneme";
        context.Request.Method = "GET";
        context.Request.Headers.Authorization = "Bearer " + token;
        context.Response.Body = new MemoryStream();
        context.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(metadata), "deneme"));
        DeviceAuthMiddleware middleware = new(_ => Task.CompletedTask, devices, auth);
        await middleware.InvokeAsync(context);
        return context.Response.StatusCode;
    }

    private static void EndpointCoverage()
    {
        WebApplication app = MobileApiHost.Build([], hostedServices: false);
        List<string> missing = [];
        int checkedCount = 0;
        foreach (RouteEndpoint endpoint in ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>())
        {
            string pattern = "/" + (endpoint.RoutePattern.RawText ?? string.Empty).TrimStart('/');
            bool guarded = pattern.StartsWith("/api/", StringComparison.Ordinal) && !DeviceAuthMiddleware.IsPublic(pattern)
                || pattern.StartsWith("/panel/api/", StringComparison.Ordinal);
            if (!guarded)
            {
                continue;
            }

            checkedCount++;
            if (endpoint.Metadata.GetMetadata<PanelPermission>() is null && endpoint.Metadata.GetMetadata<SignedInOnly>() is null)
            {
                missing.Add(pattern);
            }

            if (endpoint.Metadata.GetMetadata<PanelPermission>() is { } permission && !PanelPermissions.IsKnown(permission.Name))
            {
                missing.Add(pattern + " (bilinmeyen yetki " + permission.Name + ")");
            }
        }

        Check(checkedCount > 60, $"yalnızca {checkedCount} uç nokta bulundu");
        Check(missing.Count == 0, "yetkisiz uç noktalar: " + string.Join(", ", missing));
    }

    private static async Task IdempotencyAsync()
    {
        int executed = 0;
        IdempotencyMiddleware middleware = new(async context =>
        {
            Interlocked.Increment(ref executed);
            await Task.Delay(150);
            context.Response.StatusCode = 200;
            await context.Response.WriteAsync("kaydedildi");
        });
        WebPanelUser user = new() { Id = 77, UserName = "idem" };

        DefaultHttpContext NewContext(string key)
        {
            DefaultHttpContext context = new();
            context.Request.Method = "POST";
            context.Request.Path = "/api/tup/hareket";
            context.Request.Headers[IdempotencyMiddleware.Header] = key;
            context.Items["panelAccount"] = user;
            context.Response.Body = new MemoryStream();
            return context;
        }

        DefaultHttpContext[] contexts = [.. Enumerable.Range(0, 6).Select(_ => NewContext("okutma-1"))];
        await Task.WhenAll(contexts.Select(c => Task.Run(() => middleware.InvokeAsync(c))));
        Check(executed == 1, $"aynı okutma {executed} kez uygulandı");
        Check(contexts.Count(c => c.Response.Headers.ContainsKey(IdempotencyMiddleware.ReplayHeader)) == 5, "tekrarlar ilk cevabı almadı");
        Check(contexts.All(c => System.Text.Encoding.UTF8.GetString(((MemoryStream)c.Response.Body).ToArray()) == "kaydedildi"), "tekrar cevabı farklı");

        await middleware.InvokeAsync(NewContext("okutma-2"));
        Check(executed == 2, "farklı okutma uygulanmadı");
    }

    private static async Task ConcurrentMovesAsync()
    {
        await using (ArsanGazDbContext db = new())
        {
            await db.Database.EnsureCreatedAsync();
        }

        await CylinderSchemaService.EnsureAsync();
        int cylinderId;
        await using (ArsanGazDbContext db = new())
        {
            db.Customers.Add(new Customer { Code = "T1", CompanyName = "Test Müşteri" });
            Cylinder cylinder = new() { SerialNumber = "SELF-1", Barcode = "SELF-1", GasType = "Oksijen", Capacity = 40, IsFull = true };
            db.Cylinders.Add(cylinder);
            await db.SaveChangesAsync();
            cylinderId = cylinder.Id;
        }

        CylinderStatus[] targets = [CylinderStatus.Vehicle, CylinderStatus.Filling, CylinderStatus.Service, CylinderStatus.Warehouse];
        int ok = 0;
        int stale = 0;
        List<string> unexpected = [];
        await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(async () =>
        {
            try
            {
                await new CrudService().MoveCylinderAsync(cylinderId, targets[i % targets.Length], null, "self-test " + i, vehiclePlate: "34 ABC 01");
                Interlocked.Increment(ref ok);
            }
            catch (StaleRecordException)
            {
                Interlocked.Increment(ref stale);
            }
            catch (Exception exception)
            {
                lock (unexpected)
                {
                    unexpected.Add(exception.GetBaseException().Message);
                }
            }
        })));

        Check(unexpected.Count == 0, "beklenmeyen hata: " + string.Join(" | ", unexpected.Distinct()));
        await using (ArsanGazDbContext db = new())
        {
            List<CylinderMovement> moves = await db.CylinderMovements.AsNoTracking().Where(m => m.CylinderId == cylinderId).OrderBy(m => m.Id).ToListAsync();
            Cylinder final = await db.Cylinders.AsNoTracking().FirstAsync(c => c.Id == cylinderId);
            Check(moves.Count == ok, $"{ok} başarılı hareket ama {moves.Count} kayıt");
            Check(moves[0].FromStatus == CylinderStatus.Warehouse, "ilk hareketin başlangıç durumu hatalı");
            for (int i = 1; i < moves.Count; i++)
            {
                Check(moves[i].FromStatus == moves[i - 1].ToStatus, "bir hareket diğerinin üzerine sessizce yazıldı");
            }

            Check(final.Status == moves[^1].ToStatus, "tüpün son durumu son hareketle uyuşmuyor");

            CylinderTrip trip = new() { Title = "Self-test seferi" };
            db.CylinderTrips.Add(trip);
            await db.SaveChangesAsync();
            db.CylinderTripCylinders.Add(new CylinderTripCylinder { TripId = trip.Id, CylinderId = cylinderId, VehiclePlate = "34 ABC 01", SerialNumber = "SELF-1" });
            await db.SaveChangesAsync();
            db.CylinderTripCylinders.Add(new CylinderTripCylinder { TripId = trip.Id, CylinderId = cylinderId, VehiclePlate = "34 ABC 02", SerialNumber = "SELF-1" });
            bool rejected = false;
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                rejected = true;
            }

            Check(rejected, "aynı tüp aynı sefere iki kez yüklenebildi");
        }

        Console.WriteLine($"     ({ok} hareket uygulandı, {stale} çakışma reddedildi)");
    }

    private static void SqliteCleanup()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
        catch (TypeInitializationException)
        {
        }
    }
}
