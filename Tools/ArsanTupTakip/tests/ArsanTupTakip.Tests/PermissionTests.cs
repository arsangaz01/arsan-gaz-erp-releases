using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ArsanTupTakip.Api;
using ArsanTupTakip.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ArsanTupTakip.Tests;

// Permission catalog, role defaults, per-user overrides, escalation rules, session lifetimes, parallel logins and
// the server-side checks over real HTTP. Needs the initial "yonetici" from ApiLogicTests.
internal static class PermissionTests
{
    private const string Password = "Deneme-Sifre-2026";

    internal static void Run()
    {
        PermissionOverrides.UseStore(new MemoryPermissionOverrideStore());
        IdempotencyMiddleware.Store = new MemoryRequestKeyStore();
        Features.UseMemory(Features.Catalog.ToDictionary(f => f.Key, _ => true));
        CatalogIsConsistent();
        RoleDefaultsAndOverrides();
        DriverRoleAndFixedVehicle();
        UserManagementRules();
        SessionLifetimesAreAbsolute().GetAwaiter().GetResult();
        ParallelLoginsStayIndependent().GetAwaiter().GetResult();
        WrongPasswordsLockTheAddress().GetAwaiter().GetResult();
        HttpChecks().GetAwaiter().GetResult();
        DeletingUsers().GetAwaiter().GetResult();
        Console.WriteLine("PASS: yetki kataloğu, rol varsayılanları, şoför rolü ve sabit araç kuralı, kullanıcıya özel yetkiler, son yönetici koruması, kullanıcı silme, oturum süreleri (panel 24 saat, mobil 24 saat), eşzamanlı girişler ve sunucu tarafı yetki denetimi.");
    }

    private static void CatalogIsConsistent()
    {
        Check(Perm.All.Length == Perm.All.Distinct().Count(), "Yetki anahtarları benzersiz olmalı.");
        Check(Perm.Catalog.SelectMany(g => g.Permissions).All(p => p.Label.Length > 0 && p.Description.Length > 0), "Her yetkinin Türkçe adı ve açıklaması olmalı.");
        Check(Perm.Catalog.SelectMany(g => g.Permissions).SelectMany(p => p.Requires).All(Perm.ByKey.ContainsKey), "Ön koşul yetkileri katalogda olmalı.");
        Check(PanelRoles.All.All(r => Perm.RoleDefaults(r).All(Perm.ByKey.ContainsKey)), "Rol varsayılanları katalogdaki yetkilerden oluşmalı.");
        Check(Perm.MoveByStatus.Values.All(Perm.ByKey.ContainsKey) && Perm.MoveByStatus.Count == 6, "Her tüp durumu için hareket yetkisi olmalı.");
        Check(Perm.All.Length >= 35, "Yetki kataloğu ayrıntılı olmalı.");
        foreach (string role in PanelRoles.All)
        {
            HashSet<string> defaults = [.. Perm.RoleDefaults(role)];
            Check(defaults.SetEquals(Perm.Effective(role, null)), $"{role} varsayılanları kendi ön koşullarını içermeli.");
        }
    }

    private static void RoleDefaultsAndOverrides()
    {
        Check(Perm.Effective(PanelRoles.Admin, Perm.All.ToDictionary(k => k, _ => false)).SetEquals(Perm.All), "Yönetici her zaman tüm yetkilere sahip olmalı.");
        HashSet<string> op = Perm.Effective(PanelRoles.Operator, null);
        Check(op.Contains(Perm.MoveDeliver) && op.Contains(Perm.TripStart) && !op.Contains(Perm.ManageUsers) && !op.Contains(Perm.AuditLog), "Operatör varsayılanı: işlemler var, yönetim yok.");
        Check(Perm.Effective(PanelRoles.Admin, null).Contains(Perm.VehicleDelete) && !op.Contains(Perm.VehicleDelete)
            && Perm.Effective(PanelRoles.Operator, new Dictionary<string, bool> { [Perm.VehicleDelete] = true }).Contains(Perm.VehicleDelete),
            "Araç silme varsayılan olarak yalnızca Yönetici'de olmalı; yönetici başkasına verebilmeli.");
        Check(op.Contains(Perm.LabelPrint) && Perm.Effective(PanelRoles.Admin, null).Contains(Perm.LabelPrint)
            && !Perm.Effective(PanelRoles.Operator, new Dictionary<string, bool> { [Perm.LabelPrint] = false }).Contains(Perm.LabelPrint)
            && Perm.Effective(PanelRoles.Viewer, new Dictionary<string, bool> { [Perm.LabelPrint] = true }).Contains(Perm.LabelPrint)
            && !Perm.Effective(PanelRoles.Operator, new Dictionary<string, bool> { [Perm.CylinderView] = false }).Contains(Perm.LabelPrint),
            "Barkod yazdırma Yönetici ve Operatör'de varsayılan olmalı; yönetici kişiye özel ekleyip çıkarabilmeli; tüp görüntüleme olmadan çalışmamalı.");
        HashSet<string> viewer = Perm.Effective(PanelRoles.Viewer, null);
        Check(!viewer.Contains(Perm.LabelPrint), "İzleyici varsayılan olarak barkod yazdıramamalı.");
        Check(Perm.Effective(PanelRoles.Admin, null).Contains(Perm.CustomerStockTotals) && !op.Contains(Perm.CustomerStockTotals) && !viewer.Contains(Perm.CustomerStockTotals)
            && op.Contains(Perm.CylinderView)
            && Perm.Effective(PanelRoles.Operator, new Dictionary<string, bool> { [Perm.CustomerStockTotals] = true }).Contains(Perm.CustomerStockTotals),
            "Müşteri stoğu toplamları varsayılan olarak yalnızca Yönetici'de olmalı (diğerleri müşteri bazında görür); yönetici kişiye verebilmeli.");
        Check(viewer.Contains(Perm.CylinderView) && Perm.AnyMove.All(m => !viewer.Contains(m)) && !viewer.Contains(Perm.VehicleAdd), "İzleyici varsayılanı yalnızca görüntüleme olmalı.");
        Check(Perm.Effective("bilinmeyen", new Dictionary<string, bool> { [Perm.Summary] = true }).Count == 0, "Bilinmeyen rol hiçbir yetki almamalı.");
        Check(PanelRoles.Normalize("yönetici") == PanelRoles.Admin && PanelRoles.Normalize("IZLEYICI") == PanelRoles.Viewer && PanelRoles.Normalize("x") is null, "Rol adları Türkçe karakterli/karaktersiz eşleşmeli.");

        HashSet<string> granted = Perm.Effective(PanelRoles.Viewer, new Dictionary<string, bool> { [Perm.MoveDeliver] = true, ["uydurma"] = true });
        Check(granted.Contains(Perm.MoveDeliver) && !granted.Contains("uydurma"), "İzleyiciye tek yetki eklenebilmeli; bilinmeyen anahtar yok sayılmalı.");
        HashSet<string> revoked = Perm.Effective(PanelRoles.Operator, new Dictionary<string, bool> { [Perm.MoveScrap] = false, [Perm.CylinderView] = false });
        Check(!revoked.Contains(Perm.MoveScrap) && !revoked.Contains(Perm.MoveDeliver) && !revoked.Contains(Perm.CylinderEdit) && revoked.Contains(Perm.CylinderRegister),
            "Kaldırılan yetkiye bağlı yetkiler de kalkmalı, bağımsız olanlar kalmalı.");
        HashSet<string> tripless = Perm.Effective(PanelRoles.Operator, new Dictionary<string, bool> { [Perm.TripView] = false });
        Check(!tripless.Contains(Perm.TripStart) && !tripless.Contains(Perm.TripClose) && !tripless.Contains(Perm.TripLossCheck), "Sefer görüntüleme kalkınca sefer işlemleri de kalkmalı.");
        Check(Perm.Effective(PanelRoles.Admin, null).Contains(Perm.TripLossCheck) && !op.Contains(Perm.TripLossCheck) && !viewer.Contains(Perm.TripLossCheck)
            && Perm.Effective(PanelRoles.Operator, new Dictionary<string, bool> { [Perm.TripLossCheck] = true }).Contains(Perm.TripLossCheck),
            "Kayıp kontrolü onayı varsayılan olarak yalnızca Yönetici'de olmalı; yönetici başkasına verebilmeli.");

        HashSet<string> wanted = Perm.Effective(PanelRoles.Operator, new Dictionary<string, bool> { [Perm.MoveScrap] = false, [Perm.AuditLog] = true });
        foreach (string role in new[] { PanelRoles.Operator, PanelRoles.Viewer })
        {
            HashSet<string> forRole = role == PanelRoles.Operator ? wanted : [.. wanted.Except(Perm.AdminChosen)];
            Dictionary<string, bool> diff = Perm.Diff(role, forRole);
            Check(Perm.Effective(role, diff).SetEquals(forRole), $"{role} için seçilen yetkiler farklar olarak saklanıp aynen geri gelmeli.");
        }

        Check(Perm.Diff(PanelRoles.Operator, Perm.RoleDefaults(PanelRoles.Operator)).Count == 0, "Rol varsayılanı seçiliyse fark saklanmamalı.");
    }

    private static void DriverRoleAndFixedVehicle()
    {
        HashSet<string> driver = Perm.Effective(PanelRoles.Driver, null);
        string[] expected = [Perm.MobileLogin, Perm.CylinderView, Perm.StockCount, Perm.TripView, Perm.TripStart, Perm.TripLoad, Perm.TripDeliver, Perm.TripPickup, Perm.TripRoute,
            Perm.CustomerLocation];
        Check(driver.SetEquals(expected), "Şoför varsayılanı yalnızca mobil giriş, tüp bilgisi/müşteri stoku, depo sayımı ve sefer akışı olmalı: " + string.Join(", ", driver.Order()));
        Check(!driver.Contains(Perm.PanelLogin) && !driver.Contains(Perm.ManageUsers) && !driver.Contains(Perm.ManageDevices) && !driver.Contains(Perm.AuditLog),
            "Şoför panele giremez ve yönetim yetkisi yoktur.");
        Check(!driver.Contains(Perm.CylinderRegister) && !driver.Contains(Perm.CylinderEdit) && !driver.Contains(Perm.CylinderAudit) && Perm.AnyMove.All(m => !driver.Contains(m))
            && !driver.Contains(Perm.VehicleAdd) && !driver.Contains(Perm.VehicleView) && !driver.Contains(Perm.Reports) && !driver.Contains(Perm.Summary)
            && !driver.Contains(Perm.Export) && !driver.Contains(Perm.LabelPrint) && !driver.Contains(Perm.CustomerView) && !driver.Contains(Perm.TripLossCheck),
            "Şoförde tüp kaydı, hareket, müşteride sayım, araç, rapor, Excel, barkod ve müşteri telefonları olmamalı.");
        Check(!CylinderTripEndpoints.ReasonWriters.Any(driver.Contains), "Şoför okutulmayan tüplere gerekçe yazamamalı; gerekçeyi aracı depoda boşaltan operatör yazar.");
        Dictionary<string, bool> both = Perm.AdminChosen.ToDictionary(k => k, _ => true);
        HashSet<string> op = Perm.Effective(PanelRoles.Operator, null);
        Check(Perm.AdminChosen.All(op.Contains) && Perm.AdminChosen.All(Perm.Effective(PanelRoles.Admin, null).Contains)
            && !Perm.AdminChosen.Any(Perm.Effective(PanelRoles.Driver, both).Contains) && !Perm.AdminChosen.Any(Perm.Effective(PanelRoles.Viewer, both).Contains)
            && !Perm.Effective(PanelRoles.Operator, new Dictionary<string, bool> { [Perm.TripReturn] = false }).Contains(Perm.TripReturn),
            "Aracı boşaltma ve seferi kapatma yalnızca Yönetici ve Operatör'de olabilmeli; Operatör'den kaldırılabilmeli, şoföre ve izleyiciye verilememeli.");
        Check(Perm.CustomerPicker.Any(driver.Contains), "Şoför müşteri seçebilmeli (teslim ve müşteri stoku).");
        Check(PanelRoles.Normalize("SOFOR") == PanelRoles.Driver && PanelRoles.Normalize("şoför") == PanelRoles.Driver && PanelRoles.Normalize("Şoför") == PanelRoles.Driver,
            "Şoför rol adı Türkçe karakterli/karaktersiz eşleşmeli.");
        Check(PanelRoles.Description(PanelRoles.Driver).Length > 0 && PanelRoles.All.Contains(PanelRoles.Driver), "Şoför rolü listede ve açıklamalı olmalı.");

        HashSet<string> plus = Perm.Effective(PanelRoles.Driver, Perm.Diff(PanelRoles.Driver, [.. driver, Perm.CylinderAudit]));
        HashSet<string> minus = Perm.Effective(PanelRoles.Driver, Perm.Diff(PanelRoles.Driver, driver.Where(p => p != Perm.StockCount)));
        Check(plus.Contains(Perm.CylinderAudit) && !minus.Contains(Perm.StockCount) && minus.Contains(Perm.TripStart),
            "Yönetici şoföre tek tek yetki ekleyip çıkarabilmeli.");

        Check(DriverVehicle.StartPlates("34 ABC 123", null) is ["34 ABC 123"] && DriverVehicle.StartPlates("34 ABC 123", ["34abc123"]) is ["34 ABC 123"],
            "Sabit araçlı şoförün seferi araç seçmeden sabit araçla başlamalı.");
        Check(Throws(() => DriverVehicle.StartPlates("34 ABC 123", ["06 XY 99"])), "Sabit araçlı şoför başka araçla sefer başlatamamalı.");
        Check(Throws(() => DriverVehicle.StartPlates("34 ABC 123", ["34 ABC 123", "06 XY 99"])), "Sabit araçlı şoför sefere ikinci araç ekleyememeli.");
        Check(DriverVehicle.PlateFor("34 ABC 123", null) == "34 ABC 123" && Throws(() => DriverVehicle.PlateFor("34 ABC 123", "06 XY 99")),
            "Yükleme/dönüşte plaka verilmezse sabit araç kullanılmalı, başka plaka reddedilmeli.");
        Check(DriverVehicle.StartPlates(null, ["06 XY 99", "34 ABC 123"])!.Count == 2 && DriverVehicle.PlateFor(null, "06 XY 99") == "06 XY 99",
            "Sabit aracı olmayan kullanıcı eskisi gibi araç seçebilmeli.");
        ActiveTripSummary open = new(5, "Sefer", DateTime.Now, null, [new TripVehicleSummary("34 ABC 123", 3, true, null)], 3, 0, 0);
        ActiveTripSummary returned = new(6, "Sefer", DateTime.Now, null, [new TripVehicleSummary("34 ABC 123", 3, false, DateTime.Now)], 3, 0, 3);
        Check(DriverVehicle.OpenTrip([open], "34 ABC 123")?.Id == 5 && DriverVehicle.OpenTrip([returned], "34 ABC 123") is null && DriverVehicle.OpenTrip([open], "06 XY 99") is null,
            "Sabit araç açık seferdeyken ikinci sefer açılmamalı; araç döndüyse açılabilmeli.");
    }

    private static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static void UserManagementRules()
    {
        WebPanelAuth auth = new();
        WebPanelUser admin = WebPanelUserStore.FindByName("yonetici")!;
        const string ip = "127.0.0.1";

        List<string> managerPermissions = [.. Perm.Effective(PanelRoles.Operator, null).Where(p => p != Perm.MoveScrap), Perm.ManageUsers];
        Check(auth.CreateUser(admin, ip, "sorumlu", "Depo Sorumlusu", "Operatör", Password, false, managerPermissions) is null, "Yönetici yetkileri seçerek kullanıcı açabilmeli.");
        WebPanelUser manager = WebPanelUserStore.FindByName("sorumlu")!;
        HashSet<string> managerEffective = Perm.Effective(manager);
        Check(managerEffective.Contains(Perm.ManageUsers) && !managerEffective.Contains(Perm.MoveScrap), "Seçilen yetkiler kullanıcıya uygulanmalı.");

        Check(auth.CreateUser(manager, ip, "yeniyonetici", null, "Yönetici", Password, false) is not null, "Yönetici olmayan, Yönetici hesabı açamamalı.");
        Check(auth.CreateUser(manager, ip, "hurdaci", null, "Operatör", Password, false) is not null, "Kendinde olmayan yetkiyi (hurda) veren kullanıcı açılamamalı.");
        Check(auth.CreateUser(manager, ip, "saha1", "Saha", "İzleyici", Password, false, [.. Perm.Effective(PanelRoles.Viewer, null), Perm.MoveDeliver]) is null,
            "Kendinde olan yetkiyi verebilmeli.");
        WebPanelUser field = WebPanelUserStore.FindByName("saha1")!;
        Check(Perm.Has(field, Perm.MoveDeliver) && !Perm.Has(field, Perm.MoveReturn), "Verilen tek yetki işlenmeli.");
        Check(auth.UpdateUser(manager, ip, manager.Id, manager.DisplayName, manager.Role, true, [.. managerEffective, Perm.MoveScrap]) is not null, "Kullanıcı kendi yetkilerini artıramamalı.");
        Check(auth.UpdateUser(manager, ip, admin.Id, admin.DisplayName, "Operatör", true) is not null, "Yönetici olmayan, Yönetici hesabına dokunamamalı.");

        Check(auth.CreateUser(manager, ip, "depocu", "Depocu", "Operatör", Password, false, [.. Perm.Effective(PanelRoles.Operator, null).Where(p => p != Perm.MoveScrap)]) is null,
            "Kullanıcı yöneticisi rol varsayılanıyla operatör açabilmeli (aracı boşaltma dahil).");
        WebPanelUser depot = WebPanelUserStore.FindByName("depocu")!;
        HashSet<string> depotPerms = Perm.Effective(depot);
        string? notAdmin = auth.UpdateUser(manager, ip, depot.Id, depot.DisplayName, depot.Role, true, [.. depotPerms.Where(p => p != Perm.TripReturn)]);
        Check(notAdmin is not null && notAdmin.Contains("yalnızca bir Yönetici", StringComparison.Ordinal) && Perm.Has(WebPanelUserStore.Find(depot.Id)!, Perm.TripReturn),
            "Yönetici olmayan, operatörden aracı boşaltma yetkisini kaldıramamalı.");
        Check(auth.UpdateUser(admin, ip, depot.Id, depot.DisplayName, depot.Role, true, [.. depotPerms.Where(p => p != Perm.TripReturn)]) is null
            && !Perm.Has(WebPanelUserStore.Find(depot.Id)!, Perm.TripReturn) && Perm.Has(WebPanelUserStore.Find(depot.Id)!, Perm.TripClose),
            "Yönetici operatörden aracı boşaltmayı tek başına kaldırabilmeli.");
        string? regrant = auth.UpdateUser(manager, ip, depot.Id, depot.DisplayName, depot.Role, true, [.. Perm.Effective(WebPanelUserStore.Find(depot.Id)!), Perm.TripReturn]);
        Check(regrant is not null && !Perm.Has(WebPanelUserStore.Find(depot.Id)!, Perm.TripReturn), "Yönetici olmayan, operatöre aracı boşaltmayı geri veremez.");
        Check(auth.UpdateUser(admin, ip, depot.Id, depot.DisplayName, depot.Role, true, [.. Perm.Effective(WebPanelUserStore.Find(depot.Id)!), Perm.TripReturn]) is null
            && Perm.Has(WebPanelUserStore.Find(depot.Id)!, Perm.TripReturn), "Yönetici operatöre aracı boşaltmayı geri verebilmeli.");
        Check(auth.UpdateUser(manager, ip, depot.Id, depot.DisplayName, PanelRoles.Driver, true) is null && !Perm.Has(WebPanelUserStore.Find(depot.Id)!, Perm.TripReturn),
            "Şoföre çevrilen kullanıcıda aracı boşaltma kalmamalı.");

        PermissionOverrides.Replace(admin.Id, Perm.All.ToDictionary(k => k, _ => false), "test");
        Check(Perm.Assigned(WebPanelUserStore.Find(admin.Id)!).SetEquals(Perm.All), "Yöneticiye yazılmış kısıtlamalar etkisiz olmalı.");
        PermissionOverrides.Replace(admin.Id, new Dictionary<string, bool>(), "test");

        Check(auth.UpdateUser(admin, ip, admin.Id, admin.DisplayName, "Operatör", true) is not null, "Yönetici kendi yönetici yetkisini kaldıramamalı.");
        Check(auth.CreateUser(admin, ip, "yonetici2", null, "Yönetici", Password, false) is null, "İkinci yönetici açılabilmeli.");
        WebPanelUser second = WebPanelUserStore.FindByName("yonetici2")!;
        Check(auth.UpdateUser(second, ip, admin.Id, admin.DisplayName, PanelRoles.Admin, false) is null, "Başka aktif yönetici varken yönetici pasife alınabilmeli.");
        string? last = auth.UpdateUser(WebPanelUserStore.Find(admin.Id)!, ip, second.Id, second.DisplayName, PanelRoles.Admin, false);
        Check(last is not null && last.Contains("Son aktif yönetici", StringComparison.Ordinal), "Son aktif yönetici pasife alınamamalı.");
        Check(auth.UpdateUser(second, ip, admin.Id, admin.DisplayName, PanelRoles.Admin, true) is null, "Yönetici yeniden aktif edilebilmeli.");
        Check(auth.UpdateUser(admin, ip, second.Id, second.DisplayName, PanelRoles.Admin, false) is null, "Fazladan yönetici pasife alınabilmeli.");
        Check(WebPanelUserStore.IsConfigured && WebPanelUserStore.Find(admin.Id)!.IsActive, "İlk yönetici aktif kalmalı.");
    }

    private static async Task SessionLifetimesAreAbsolute()
    {
        DateTime now = new(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);
        WebPanelAuth auth = new(() => now);
        DefaultHttpContext login = Context("10.1.0.1");
        Check(await auth.LoginAsync(login, "sorumlu", Password) is null, "Panel girişi yapılabilmeli.");
        DefaultHttpContext request = WithCookie(login);
        now = now.AddHours(23).AddMinutes(59);
        Check(auth.Current(request) is not null, "Panel oturumu 23 saat 59. dakikada geçerli olmalı.");
        now = now.AddMinutes(1).AddSeconds(1);
        Check(auth.Current(request) is null, "Panel oturumu girişten 24 saat sonra kullanım olsa bile bitmeli.");

        now = new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);
        DeviceStore devices = new(() => now);
        var (token, session, _, error) = await auth.MobileLoginAsync(Context("10.1.0.2"), devices, "saha1", Password, "Test telefonu");
        Check(error is null && token is not null && session is not null, "Mobil giriş panel şifresiyle yapılabilmeli.");
        Check(session!.ExpiresUtc == now + TimeSpan.FromHours(24), "Mobil oturum girişten tam 24 saat geçerli olmalı.");
        now = now.AddHours(23).AddMinutes(59);
        Check(devices.Validate(token!) is not null, "Mobil oturum 23:59'da geçerli olmalı.");
        now = now.AddMinutes(1).AddSeconds(1);
        Check(devices.Validate(token!) is null, "Mobil oturum 24 saat dolunca geçersiz olmalı.");
        Check(devices.All().All(s => s.Id != session.Id), "Süresi dolan mobil oturum listede görünmemeli.");

        WebPanelUser rootAdmin = WebPanelUserStore.FindByName("yonetici")!;
        Check(auth.CreateUser(rootAdmin, "127.0.0.1", "yonetici3", "Telefon Yöneticisi", PanelRoles.Admin, Password, false, null) is null, "İkinci yönetici açılabilmeli.");
        var (adminToken, adminSession, _, adminError) = await auth.MobileLoginAsync(Context("10.1.0.3"), devices, "yonetici3", Password, "Yönetici telefonu");
        Check(adminError is null && adminToken is not null, "Yönetici mobil girişi yapılabilmeli.");
        Check(adminSession!.ExpiresUtc == now + DeviceStore.AdminSessionLifetime, "Yönetici mobil oturumu çıkış yapılana kadar sürmeli.");
        now = now.AddDays(30);
        Check(devices.Validate(adminToken!) is not null, "Yönetici mobil oturumu 30 gün sonra da geçerli olmalı.");
    }

    private static async Task ParallelLoginsStayIndependent()
    {
        WebPanelAuth auth = new();
        DeviceStore devices = new();
        DefaultHttpContext[] panels = [.. Enumerable.Range(0, 4).Select(i => Context("10.2.0." + i))];
        string?[] panelErrors = await Task.WhenAll(panels.Select(c => auth.LoginAsync(c, "sorumlu", Password)));
        var phones = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            auth.MobileLoginAsync(Context("10.3.0." + i), devices, i % 2 == 0 ? "sorumlu" : "saha1", Password, "Telefon " + i)));

        Check(panelErrors.All(e => e is null) && phones.All(p => p.Error is null), "Aynı anda yapılan girişlerin hepsi başarılı olmalı.");
        List<string> cookies = [.. panels.Select(CookieValue)];
        Check(cookies.Distinct().Count() == cookies.Count && phones.Select(p => p.Token).Distinct().Count() == phones.Length, "Her giriş ayrı bir oturum anahtarı almalı.");
        Check(panels.All(c => auth.Current(WithCookie(c)) is not null), "Yeni girişler önceki tarayıcı oturumlarını kapatmamalı.");
        Check(phones.All(p => devices.Validate(p.Token!) is { } s && s.UserName == p.User!.UserName), "Her telefon kendi kullanıcısının oturumunu almalı.");
        Check(auth.ActiveSessionCount(WebPanelUserStore.FindByName("sorumlu")!.Id) == 4, "Aynı kullanıcının dört tarayıcı oturumu birlikte açık kalmalı.");

        var later = await auth.MobileLoginAsync(Context("10.3.0.9"), devices, "sorumlu", Password, "Yedek telefon");
        Check(later.Error is null && phones.All(p => devices.Validate(p.Token!) is not null), "Yeni telefon girişi diğer telefonları kapatmamalı.");
        Check(devices.Remove(later.Session!.Id) is not null && devices.Validate(later.Token!) is null && devices.Validate(phones[0].Token!) is not null,
            "Yönetici tek bir telefon oturumunu kapatabilmeli; diğerleri sürmeli.");
    }

    private static async Task WrongPasswordsLockTheAddress()
    {
        WebPanelAuth auth = new();
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => auth.AuthenticateAsync("10.9.9.9", "saha1", "Yanlis-Sifre-1", "mobil")));
        var (user, error) = await auth.AuthenticateAsync("10.9.9.9", "saha1", Password, "mobil");
        Check(user is null && error is not null && error.Contains("dakika", StringComparison.Ordinal), "5 hatalı denemeden sonra adres (doğru şifreyle de) kilitlenmeli.");
        Check((await auth.AuthenticateAsync("10.9.9.8", "saha1", Password, "mobil")).User is not null, "Kilit yalnızca o adresi etkilemeli.");
    }

    private static async Task HttpChecks()
    {
        WebApplication app = ApiHost.Build([], "http://127.0.0.1:0", "/tuptakip");
        await app.StartAsync();
        try
        {
            CheckEveryEndpointHasPermission(app);
            using HttpClient http = new(new HttpClientHandler { UseCookies = false }) { BaseAddress = new Uri(app.Urls.First() + "/tuptakip/") };
            WebPanelAuth auth = app.Services.GetRequiredService<WebPanelAuth>();
            DeviceStore devices = app.Services.GetRequiredService<DeviceStore>();
            WebPanelUser admin = WebPanelUserStore.FindByName("yonetici")!;

            using (HttpResponseMessage wrong = await http.PostAsJsonAsync("api/giris", new { kullanici = "saha1", sifre = "Yanlis-Sifre-2", cihazAdi = "x" }))
            {
                Check(wrong.StatusCode == HttpStatusCode.Unauthorized && (await Json(wrong)).GetProperty("error").GetString() == "Kullanıcı adı veya şifre hatalı.", "Hatalı mobil giriş 401 ve Türkçe mesaj vermeli.");
            }

            (string token, JsonElement account) = await MobileLogin(http, "saha1");
            string[] permissions = [.. account.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!)];
            Check(permissions.Contains(Perm.MoveDeliver) && !permissions.Contains(Perm.MoveReturn) && account.GetProperty("role").GetString() == PanelRoles.Viewer,
                "Mobil giriş etkin yetkileri döndürmeli.");
            Check(account.GetProperty("expiresUtc").GetDateTime() > DateTime.UtcNow.AddHours(23.9), "Mobil oturum bitişi 24 saat sonrası olmalı.");

            using (HttpResponseMessage me = await Send(http, HttpMethod.Get, "api/ben", token))
            {
                Check(me.StatusCode == HttpStatusCode.OK, "Geçerli anahtarla /api/ben açılmalı.");
            }

            using (HttpResponseMessage denied = await Send(http, HttpMethod.Post, "api/tup/hareket", token, new { code = "X1", status = 1 }))
            {
                Check(denied.StatusCode == HttpStatusCode.Forbidden && (await Json(denied)).GetProperty("error").GetString()!.Contains("yetkiniz yok", StringComparison.Ordinal),
                    "Yetkisi olmayan hareket sunucuda reddedilmeli (depoya iade).");
            }

            using (HttpResponseMessage anonymous = await Send(http, HttpMethod.Get, "api/ozet", null))
            {
                Check(anonymous.StatusCode == HttpStatusCode.Unauthorized && (await Json(anonymous)).GetProperty("loginRequired").GetBoolean(), "Anahtarsız istek 401 ve giriş istemeli.");
            }

            using (HttpResponseMessage handheld = await Send(http, HttpMethod.Get, "api/guncelleme/el-terminali", null))
            {
                JsonElement info = await Json(handheld);
                Check(handheld.StatusCode == HttpStatusCode.OK && info.GetProperty("downloadUrl").GetString() == "/indir/el-terminali"
                    && (File.Exists(AppUpdates.HandheldApkFile) || !info.GetProperty("apkReady").GetBoolean()),
                    "El terminali sürüm bilgisi oturumsuz okunabilmeli (Android 4.4 uygulaması giriş öncesi denetler).");
            }

            if (!File.Exists(AppUpdates.HandheldApkFile))
            {
                using HttpResponseMessage download = await Send(http, HttpMethod.Get, "indir/el-terminali", null);
                Check(download.StatusCode == HttpStatusCode.NotFound, "El terminali APK'sı yokken indirme 404 dönmeli.");
            }

            foreach ((HttpMethod method, string path) in new[]
            {
                (HttpMethod.Get, "api/sefer/gerekceler"), (HttpMethod.Get, "api/sefer/1/eksikler"), (HttpMethod.Post, "api/sefer/gerekce"),
                (HttpMethod.Get, "panel/api/sefer-eksikler"), (HttpMethod.Get, "panel/api/sefer-gerekceler"),
                (HttpMethod.Post, "panel/api/sefer/1/gerekce"), (HttpMethod.Post, "panel/api/sefer/1/gerekce-kontrol")
            })
            {
                using HttpResponseMessage closed = await Send(http, method, path, null, method == HttpMethod.Post ? new { } : null);
                Check(closed.StatusCode == HttpStatusCode.Unauthorized, $"Oturumsuz {path} 401 dönmeli.");
            }

            using (HttpResponseMessage reasons = await Send(http, HttpMethod.Get, "api/sefer/gerekceler", token))
            {
                JsonElement list = await Json(reasons);
                Check(reasons.StatusCode == HttpStatusCode.OK && list.GetArrayLength() == TripMissingReasons.All.Count
                    && list.EnumerateArray().Any(r => r.GetProperty("asksCustomer").GetBoolean()), "Sefer görüntüleyen mobil kullanıcı gerekçe listesini almalı.");
            }

            using (HttpResponseMessage noReason = await Send(http, HttpMethod.Post, "api/sefer/gerekce", token, new { tripId = 1, cylinderIds = new[] { 1 }, reason = "kayip", note = "x" }))
            {
                Check(noReason.StatusCode == HttpStatusCode.Forbidden, "Sefer teslim/dönüş/kapatma yetkisi olmayan gerekçe yazamamalı.");
            }

            string[] tokens = [.. (await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => MobileLogin(http, "sorumlu")))).Select(x => x.Token)];
            Check(tokens.Distinct().Count() == 5, "Aynı anda yapılan 5 mobil giriş ayrı anahtar almalı.");
            foreach (string each in tokens.Append(token))
            {
                using HttpResponseMessage ok = await Send(http, HttpMethod.Get, "api/ben", each);
                Check(ok.StatusCode == HttpStatusCode.OK, "Eşzamanlı girişlerden sonra tüm oturumlar geçerli kalmalı.");
            }

            MobileSession revoked = devices.Validate(token)!;
            Check(revoked.UserName == "saha1" && devices.All().Any(s => s.Id == revoked.Id), "Mobil oturum panel cihaz listesinde kullanıcısıyla görünmeli.");
            devices.Remove(revoked.Id);
            using (HttpResponseMessage closed = await Send(http, HttpMethod.Get, "api/ben", token))
            {
                Check(closed.StatusCode == HttpStatusCode.Unauthorized, "Yöneticinin kapattığı mobil oturum 401 almalı.");
            }

            Check(auth.CreateUser(admin, "127.0.0.1", "panelci", null, "Operatör", Password, false, [.. Perm.RoleDefaults(PanelRoles.Operator).Where(p => p != Perm.MobileLogin)]) is null,
                "Mobil girişi kapalı kullanıcı açılabilmeli.");
            using (HttpResponseMessage noMobile = await http.PostAsJsonAsync("api/giris", new { kullanici = "panelci", sifre = Password }))
            {
                Check(noMobile.StatusCode == HttpStatusCode.Unauthorized && (await Json(noMobile)).GetProperty("error").GetString()!.Contains("Mobil", StringComparison.Ordinal),
                    "Mobil giriş yetkisi olmayan kullanıcı telefonda giriş yapamamalı.");
            }

            string cookie = await PanelLogin(http, "saha1");
            using (HttpResponseMessage users = await Send(http, HttpMethod.Get, "panel/api/yonetim/kullanicilar", null, cookie: cookie))
            {
                Check(users.StatusCode == HttpStatusCode.Forbidden, "İzleyici kullanıcı listesini görememeli.");
            }

            using (HttpResponseMessage vehicle = await Send(http, HttpMethod.Post, "panel/api/arac", null, new { plate = "34 TST 01" }, cookie))
            {
                Check(vehicle.StatusCode == HttpStatusCode.Forbidden, "Araç ekleme yetkisi olmayan panelde araç ekleyememeli.");
            }

            using (HttpResponseMessage lossCheck = await Send(http, HttpMethod.Post, "panel/api/sefer/1/gerekce-kontrol", null, new { cylinderIds = new[] { 1 }, undo = false }, cookie))
            {
                Check(lossCheck.StatusCode == HttpStatusCode.Forbidden, "Kayıp kontrolü onayı yetkisi olmayan \"kontrol edildi\" işaretleyememeli.");
            }

            WebPanelUser viewer = WebPanelUserStore.FindByName("saha1")!;
            Check(auth.UpdateUser(admin, "127.0.0.1", viewer.Id, viewer.DisplayName, viewer.Role, true, [.. Perm.Effective(viewer), Perm.ManageUsers]) is null, "Yönetici tek yetki ekleyebilmeli.");
            using (HttpResponseMessage users = await Send(http, HttpMethod.Get, "panel/api/yonetim/kullanicilar", null, cookie: cookie))
            {
                Check(users.StatusCode == HttpStatusCode.Forbidden, "Yönetim bölümü yetki verilse bile yalnızca Yönetici'ye açık olmalı.");
            }

            Check(auth.UpdateUser(admin, "127.0.0.1", viewer.Id, viewer.DisplayName, viewer.Role, true,
                [.. Perm.Effective(WebPanelUserStore.Find(viewer.Id)!).Where(p => p != Perm.ManageUsers && p != Perm.CylinderView)]) is null, "Yönetici yetkiyi geri alabilmeli.");
            using (HttpResponseMessage cylinders = await Send(http, HttpMethod.Get, "panel/api/tupler", null, cookie: cookie))
            {
                Check(cylinders.StatusCode == HttpStatusCode.Forbidden, "Kaldırılan yetki hemen etkisini göstermeli.");
            }

            Check(auth.UpdateUser(admin, "127.0.0.1", viewer.Id, viewer.DisplayName, viewer.Role, true, [.. Perm.Effective(WebPanelUserStore.Find(viewer.Id)!), Perm.CylinderView]) is null,
                "Yönetici yetkiyi yeniden verebilmeli.");
            using (HttpResponseMessage cylinders = await Send(http, HttpMethod.Get, "panel/api/tupler", null, cookie: cookie))
            {
                Check(cylinders.StatusCode != HttpStatusCode.Forbidden, "Eklenen yetki yeniden giriş gerekmeden geçerli olmalı.");
            }

            using (HttpResponseMessage delete = await Send(http, HttpMethod.Post, $"panel/api/yonetim/kullanici/{admin.Id}/sil", null, new { onay = admin.UserName }, cookie))
            {
                Check(delete.StatusCode == HttpStatusCode.Forbidden && WebPanelUserStore.Find(admin.Id) is not null, "Kullanıcı yönetimi yetkisi olmayan kimseyi silememeli.");
            }

            using (HttpResponseMessage noCsrf = await Send(http, HttpMethod.Post, "panel/api/sifre", null, new { mevcut = "x" }, cookie, csrf: false))
            {
                Check(noCsrf.StatusCode == HttpStatusCode.Forbidden, "CSRF başlığı olmayan panel isteği reddedilmeli.");
            }

            await DriverHttpChecks(http, auth, admin);
            await SimulationHttpChecks(http);
            await FeatureHttpChecks(http, auth, admin);

            Check(auth.CreateUser(admin, "127.0.0.1", "yeniperson", null, "Operatör", Password, true) is null, "Geçici şifreli kullanıcı açılabilmeli.");
            (string fresh, JsonElement freshAccount) = await MobileLogin(http, "yeniperson");
            Check(freshAccount.GetProperty("mustChangePassword").GetBoolean(), "Geçici şifreli girişte şifre değişikliği istenmeli.");
            using (HttpResponseMessage blocked = await Send(http, HttpMethod.Get, "api/ozet", fresh))
            {
                Check(blocked.StatusCode == HttpStatusCode.Forbidden && (await Json(blocked)).GetProperty("mustChangePassword").GetBoolean(), "Şifre değişmeden işlem yapılamamalı.");
            }

            string key = Guid.NewGuid().ToString("N");
            object change = new { mevcut = Password, yeni = "Yeni-Sifre-2027", tekrar = "Yeni-Sifre-2027" };
            using (HttpResponseMessage changed = await Send(http, HttpMethod.Post, "api/sifre", fresh, change, key: key))
            {
                Check(changed.StatusCode == HttpStatusCode.OK, "Mobilde şifre değiştirilebilmeli.");
            }

            using (HttpResponseMessage replay = await Send(http, HttpMethod.Post, "api/sifre", fresh, change, key: key))
            {
                Check(replay.StatusCode == HttpStatusCode.OK && replay.Headers.Contains("Idempotent-Replay"), "Aynı istek anahtarıyla tekrar gönderilen istek yeniden işlenmeden ilk cevabı almalı.");
            }

            using (HttpResponseMessage me = await Send(http, HttpMethod.Get, "api/ben", fresh))
            {
                Check(me.StatusCode == HttpStatusCode.OK && !(await Json(me)).GetProperty("mustChangePassword").GetBoolean(), "Şifre değişince telefon oturumu sürmeli ve zorunluluk kalkmalı.");
            }

            Check(WebPanelUserStore.VerifyPassword(WebPanelUserStore.FindByName("yeniperson")!, "Yeni-Sifre-2027"), "Yeni şifre web panelde de geçerli olmalı.");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    // A Şoför account (like depo1..depo6): phone only, a fixed vehicle, and every screen outside its four jobs refused
    // by the server; the Yönetici turns single permissions on and off and the phone sees them on the next /api/ben.
    private static async Task DriverHttpChecks(HttpClient http, WebPanelAuth auth, WebPanelUser admin)
    {
        const string ip = "127.0.0.1";
        Check(auth.CreateUser(admin, ip, "sofor1", "Depo 1", "SOFOR", Password, false, null, "34abc123") is null, "Şoför hesabı sabit araçla açılabilmeli.");
        WebPanelUser driver = WebPanelUserStore.FindByName("sofor1")!;
        Check(driver.Role == PanelRoles.Driver && driver.FixedPlate == "34 ABC 123", "Şoför rolü ve sabit plaka (normalize) kaydedilmeli.");
        Check(auth.CreateUser(admin, ip, "sofor2", null, "Şoför", Password, false, null, "plaka-degil") is not null, "Geçersiz sabit plaka reddedilmeli.");

        using (HttpResponseMessage panel = await http.PostAsJsonAsync("panel/giris", new { kullanici = "sofor1", sifre = Password }))
        {
            Check(panel.StatusCode == HttpStatusCode.Unauthorized && (await Json(panel)).GetProperty("error").GetString()!.Contains("Web paneline", StringComparison.Ordinal),
                "Şoför web paneline giriş yapamamalı.");
        }

        (string token, JsonElement account) = await MobileLogin(http, "sofor1");
        Check(account.GetProperty("role").GetString() == PanelRoles.Driver && account.GetProperty("fixedPlate").GetString() == "34 ABC 123",
            "Mobil giriş şoför rolünü ve sabit aracı döndürmeli.");
        Check(account.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!).ToHashSet().SetEquals(Perm.RoleDefaults(PanelRoles.Driver)),
            "Mobil giriş şoförün varsayılan yetkilerini döndürmeli.");

        foreach ((HttpMethod method, string path, object? body) in new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Post, "api/tup/hareket", new { code = "X1", status = 3 }), (HttpMethod.Post, "api/tup/toplu-hareket", new { codes = new[] { "X1" }, status = 1 }),
            (HttpMethod.Post, "api/tup/yeni", new { serialNumber = "X" }), (HttpMethod.Post, "api/tup/sayim", new { customerId = 1 }),
            (HttpMethod.Get, "api/ozet", null), (HttpMethod.Get, "api/tup/durum", null), (HttpMethod.Get, "api/hareketler", null), (HttpMethod.Get, "api/tahmin", null),
            (HttpMethod.Get, "api/musterideki-tupler", null), (HttpMethod.Post, "api/musteri", new { name = "X" }),
            (HttpMethod.Post, "api/arac-kayit", new { plate = "34 TST 02" }), (HttpMethod.Get, "api/arac-kayit/1", null),
            (HttpMethod.Get, "api/depo-sayim/1/excel", null), (HttpMethod.Get, "api/depo-sayim/1/pdf", null),
            (HttpMethod.Post, "api/sefer/donus", new { tripId = 1, plate = "34 ABC 123", codes = new[] { "X1" } }), (HttpMethod.Post, "api/sefer/kapat", new { tripId = 1 }),
            (HttpMethod.Post, "api/sefer/gerekce", new { tripId = 1, cylinderIds = new[] { 1 }, reason = "kayip", note = "x" })
        })
        {
            using HttpResponseMessage denied = await Send(http, method, path, token, body);
            Check(denied.StatusCode == HttpStatusCode.Forbidden, $"Şoför {path} uç noktasına erişememeli ({(int)denied.StatusCode}).");
        }

        using (HttpResponseMessage reasons = await Send(http, HttpMethod.Get, "api/sefer/gerekceler", token))
        {
            Check(reasons.StatusCode == HttpStatusCode.OK, "Şoför sefer gerekçe listesini alabilmeli.");
        }

        using (HttpResponseMessage other = await Send(http, HttpMethod.Post, "api/sefer/baslat", token, new { plates = new[] { "06 XY 99" } }))
        {
            Check(other.StatusCode == HttpStatusCode.BadRequest && (await Json(other)).GetProperty("error").GetString()!.Contains("Sabit aracınız", StringComparison.Ordinal),
                "Sabit araçlı şoför sunucuda da başka araçla sefer başlatamamalı.");
        }

        using (HttpResponseMessage otherLoad = await Send(http, HttpMethod.Post, "api/sefer/yukle", token, new { tripId = 1, plate = "06 XY 99", codes = new[] { "X1" } }))
        {
            Check(otherLoad.StatusCode == HttpStatusCode.BadRequest, "Sabit araçlı şoför başka araca yükleme yapamamalı.");
        }

        List<string> granted = [.. Perm.Effective(driver), Perm.CylinderAudit];
        Check(auth.UpdateUser(admin, ip, driver.Id, driver.DisplayName, driver.Role, true, granted.Where(p => p != Perm.StockCount).ToList()) is null,
            "Yönetici şoföre müşteride sayım ekleyip depo sayımını kaldırabilmeli.");
        using (HttpResponseMessage me = await Send(http, HttpMethod.Get, "api/ben", token))
        {
            HashSet<string> now = [.. (await Json(me)).GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!)];
            Check(me.StatusCode == HttpStatusCode.OK && now.Contains(Perm.CylinderAudit) && !now.Contains(Perm.StockCount),
                "Telefon yeniden giriş yapmadan /api/ben ile değişen yetkileri görmeli.");
        }

        using (HttpResponseMessage count = await Send(http, HttpMethod.Get, "api/depo-sayim/aktif", token))
        {
            Check(count.StatusCode == HttpStatusCode.Forbidden, "Kaldırılan depo sayımı yetkisi telefonda hemen reddedilmeli.");
        }

        Check(auth.UpdateUser(admin, ip, driver.Id, driver.DisplayName, driver.Role, true, null, "") is null && WebPanelUserStore.Find(driver.Id)!.FixedPlate is null,
            "Yönetici sabit aracı kaldırabilmeli.");
        Check(auth.UpdateUser(admin, ip, driver.Id, driver.DisplayName, driver.Role, true, null, "34 ABC 123") is null && WebPanelUserStore.Find(driver.Id)!.FixedPlate == "34 ABC 123"
            && Perm.Has(WebPanelUserStore.Find(driver.Id)!, Perm.CylinderAudit), "Sabit araç değişince kişiye özel yetkiler korunmalı.");
    }

    // Optional features: switched off, their permissions vanish for everyone and the server refuses their endpoints with a
    // "feature off" message, while the stored per-user choices survive; customer accounts only ever reach the portal.
    private static async Task FeatureHttpChecks(HttpClient http, WebPanelAuth auth, WebPanelUser admin)
    {
        const string ip = "127.0.0.1";
        Dictionary<string, bool> allOn = Features.Catalog.ToDictionary(f => f.Key, _ => true);
        Check(auth.CreateCustomerAccount(admin, ip, "musteri1", "Firma Yetkilisi", 999, "Test Firma", Password, false) is null, "Müşteri hesabı açılabilmeli.");
        WebPanelUser customer = WebPanelUserStore.FindByName("musteri1")!;
        Check(customer.Role == PanelRoles.Customer && customer.CustomerId == 999 && Perm.Assigned(customer).Count == 0 && !PanelRoles.All.Contains(PanelRoles.Customer),
            "Müşteri hesabının personel yetkisi olmamalı ve rol listesinde görünmemeli.");
        Check(auth.CreateCustomerAccount(admin, ip, "musteri1", null, 999, "Test Firma", Password, false) is not null, "Aynı kullanıcı adıyla ikinci hesap açılamamalı.");
        Check(auth.UpdateUser(admin, ip, customer.Id, "x", PanelRoles.Operator, true) is not null, "Müşteri hesabı personel hesabına çevrilememeli.");

        using (HttpResponseMessage panel = await http.PostAsJsonAsync("panel/giris", new { kullanici = "musteri1", sifre = Password }))
        {
            Check(panel.StatusCode == HttpStatusCode.Unauthorized, "Müşteri hesabı web paneline giremez.");
        }

        using (HttpResponseMessage phone = await http.PostAsJsonAsync("api/giris", new { kullanici = "musteri1", sifre = Password, cihazAdi = "x" }))
        {
            Check(phone.StatusCode == HttpStatusCode.Unauthorized, "Müşteri hesabı mobil uygulamaya giremez.");
        }

        using (HttpResponseMessage staff = await http.PostAsJsonAsync("musteri/giris", new { kullanici = "sofor1", sifre = Password }))
        {
            Check(staff.StatusCode == HttpStatusCode.Unauthorized, "Personel hesabı müşteri portalına giremez.");
        }

        string portalCookie;
        using (HttpResponseMessage portal = await http.PostAsJsonAsync("musteri/giris", new { kullanici = "musteri1", sifre = Password }))
        {
            Check(portal.StatusCode == HttpStatusCode.OK, "Portal açıkken müşteri giriş yapabilmeli.");
            portalCookie = portal.Headers.GetValues("Set-Cookie").First(c => c.StartsWith(WebPanelAuth.CustomerCookieName + "=", StringComparison.Ordinal)).Split(';')[0];
        }

        using (HttpResponseMessage asPanel = await Send(http, HttpMethod.Get, "panel/api/ben", null, cookie: portalCookie.Replace(WebPanelAuth.CustomerCookieName + "=", WebPanelAuth.CookieName + "=")))
        {
            Check(asPanel.StatusCode == HttpStatusCode.Unauthorized, "Portal oturum anahtarı panel çerezi olarak kullanılamamalı.");
        }

        WebPanelUser driver = WebPanelUserStore.FindByName("sofor1")!;
        (string token, _) = await MobileLogin(http, "sofor1");
        Features.UseMemory(Features.Catalog.ToDictionary(f => f.Key, _ => false));
        try
        {
            using (HttpResponseMessage me = await Send(http, HttpMethod.Get, "api/ben", token))
            {
                HashSet<string> now = [.. (await Json(me)).GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!)];
                Check(!now.Contains(Perm.TripPickup) && !now.Contains(Perm.TripRoute) && now.Contains(Perm.TripDeliver) && now.Contains(Perm.TripStart),
                    "Kapalı özelliklerin yetkileri telefona gitmemeli; eski sefer yetkileri kalmalı.");
            }

            Check(Perm.Assigned(driver).Contains(Perm.TripPickup) && !Perm.Effective(driver).Contains(Perm.TripPickup),
                "Özellik kapanınca kişiye verilen yetki saklı kalmalı, yalnızca etkin olmamalı.");
            Check(Perm.Effective(WebPanelUserStore.FindByName("yonetici")!).All(p => Features.OwnerOf(p) is null), "Kapalı özellik yöneticide de görünmemeli.");

            foreach ((HttpMethod method, string path, object? body, string feature) in new (HttpMethod, string, object?, string)[]
            {
                (HttpMethod.Post, "api/sefer/bos-alim", new { tripId = 1, plate = "34 ABC 123", customerId = 1, codes = new[] { "X1" } }, Features.CustomerExchange),
                (HttpMethod.Get, "api/sefer/1/rota?plaka=34%20ABC%20123", null, Features.TripRoute),
                (HttpMethod.Post, "api/sefer/rota/ekle", new { tripId = 1, plate = "34 ABC 123", customerId = 1 }, Features.TripRoute),
                (HttpMethod.Post, "api/musteri/1/konum", new { latitude = 37.0, longitude = 35.3, accuracy = 10.0 }, Features.TripPlan),
                (HttpMethod.Post, "api/musteri/1/konum/onayla", new { }, Features.TripPlan),
                (HttpMethod.Get, "api/sefer-plani/bugun", null, Features.TripRoute)
            })
            {
                using HttpResponseMessage off = await Send(http, method, path, token, body);
                JsonElement json = await Json(off);
                Check(off.StatusCode == HttpStatusCode.Forbidden && json.GetProperty("featureOff").GetString() == feature
                    && json.GetProperty("error").GetString()!.Contains("özelliği şu an kapalı", StringComparison.Ordinal),
                    $"Kapalı özellikte {path} \"özellik kapalı\" mesajıyla reddedilmeli ({(int)off.StatusCode}).");
            }

            using (HttpResponseMessage portal = await http.PostAsJsonAsync("musteri/giris", new { kullanici = "musteri1", sifre = Password }))
            {
                Check(portal.StatusCode == HttpStatusCode.Unauthorized && (await Json(portal)).GetProperty("error").GetString()!.Contains("kapalı", StringComparison.Ordinal),
                    "Portal kapalıyken müşteri giriş yapamamalı.");
            }

            using (HttpResponseMessage open = await Send(http, HttpMethod.Get, "musteri/api/ben", null, cookie: portalCookie))
            {
                Check(open.StatusCode == HttpStatusCode.Unauthorized, "Portal kapanınca açık müşteri oturumu da geçersiz olmalı.");
            }
        }
        finally
        {
            Features.UseMemory(allOn);
        }

        // The trip plan needs the route feature: it counts as off without it and neither can be switched out of order.
        Dictionary<string, bool> planWithoutRoute = Features.Catalog.ToDictionary(f => f.Key, f => f.Key != Features.TripRoute);
        Features.UseMemory(planWithoutRoute);
        try
        {
            Check(!Features.IsOn(Features.TripPlan) && Features.DisabledPermissions().Contains(Perm.TripPlan) && Features.DisabledPermissions().Contains(Perm.CustomerLocation),
                "Sefer rotası kapalıyken sefer planı da kapalı sayılmalı.");
            planWithoutRoute[Features.TripPlan] = false;
            Check(Features.Set(Features.TripPlan, true, "test", "127.0.0.1")?.Contains("Sefer rotası", StringComparison.Ordinal) == true,
                "Sefer rotası açılmadan sefer planı açılamamalı.");
        }
        finally
        {
            Features.UseMemory(allOn);
        }

        Check(Features.Set(Features.TripRoute, false, "test", "127.0.0.1")?.Contains("Sefer planı", StringComparison.Ordinal) == true && Features.IsOn(Features.TripRoute),
            "Sefer planı açıkken sefer rotası kapatılamamalı.");

        using (HttpResponseMessage back = await Send(http, HttpMethod.Get, "api/ben", token))
        {
            HashSet<string> now = [.. (await Json(back)).GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!)];
            Check(now.Contains(Perm.TripPickup) && now.Contains(Perm.TripRoute), "Özellik yeniden açılınca saklı yetkiler geri gelmeli.");
        }
    }

    // Mobile "Kullanıcı gözünden": only a Yönetici lists users, sees one user's permissions and toggles single ones.
    private static async Task SimulationHttpChecks(HttpClient http)
    {
        WebPanelUser driver = WebPanelUserStore.FindByName("sofor1")!;
        (string adminToken, _) = await MobileLogin(http, "yonetici3");
        (string driverToken, _) = await MobileLogin(http, "sofor1");

        using (HttpResponseMessage denied = await Send(http, HttpMethod.Get, "api/simulasyon/kullanicilar", driverToken))
        {
            Check(denied.StatusCode == HttpStatusCode.Forbidden, "Yönetici olmayan kullanıcı listesini görememeli.");
        }

        using (HttpResponseMessage list = await Send(http, HttpMethod.Get, "api/simulasyon/kullanicilar", adminToken))
        {
            JsonElement users = await Json(list);
            Check(list.StatusCode == HttpStatusCode.OK && users.EnumerateArray().Any(u => u.GetProperty("userName").GetString() == "sofor1")
                && users.EnumerateArray().All(u => u.GetProperty("userName").GetString() != "yonetici3"), "Yönetici kendisi dışındaki kullanıcıları görmeli.");
        }

        using (HttpResponseMessage view = await Send(http, HttpMethod.Get, $"api/simulasyon/{driver.Id}", adminToken))
        {
            JsonElement v = await Json(view);
            Check(view.StatusCode == HttpStatusCode.OK && v.GetProperty("role").GetString() == PanelRoles.Driver && v.GetProperty("editable").GetBoolean()
                && v.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!).ToHashSet().SetEquals(Perm.Effective(driver)),
                "Simülasyon kullanıcının gerçek yetkilerini göstermeli.");
        }

        using (HttpResponseMessage grant = await Send(http, HttpMethod.Post, $"api/simulasyon/{driver.Id}/yetki", adminToken, new { key = Perm.CylinderEdit, granted = true }))
        {
            Check(grant.StatusCode == HttpStatusCode.OK && Perm.Has(WebPanelUserStore.Find(driver.Id)!, Perm.CylinderEdit),
                "Yönetici simülasyondan yetki ekleyebilmeli.");
        }

        using (HttpResponseMessage revoke = await Send(http, HttpMethod.Post, $"api/simulasyon/{driver.Id}/yetki", adminToken, new { key = Perm.CylinderView, granted = false }))
        {
            WebPanelUser now = WebPanelUserStore.Find(driver.Id)!;
            Check(revoke.StatusCode == HttpStatusCode.OK && !Perm.Has(now, Perm.CylinderView) && !Perm.Has(now, Perm.CylinderEdit),
                "Ön koşul çıkarılınca ona bağlı yetki de kalkmalı.");
        }

        using (HttpResponseMessage back = await Send(http, HttpMethod.Post, $"api/simulasyon/{driver.Id}/yetki", adminToken, new { key = Perm.CylinderView, granted = true }))
        {
            Check(back.StatusCode == HttpStatusCode.OK && Perm.Has(WebPanelUserStore.Find(driver.Id)!, Perm.CylinderView), "Kaldırılan yetki geri verilebilmeli.");
        }

        using (HttpResponseMessage driverToggle = await Send(http, HttpMethod.Post, $"api/simulasyon/{driver.Id}/yetki", driverToken, new { key = Perm.Summary, granted = true }))
        {
            Check(driverToggle.StatusCode == HttpStatusCode.Forbidden && !Perm.Has(WebPanelUserStore.Find(driver.Id)!, Perm.Summary),
                "Yönetici olmayan kendi yetkisini simülasyondan değiştirememeli.");
        }

        using (HttpResponseMessage unknown = await Send(http, HttpMethod.Post, $"api/simulasyon/{driver.Id}/yetki", adminToken, new { key = "yok-boyle", granted = true }))
        {
            Check(unknown.StatusCode == HttpStatusCode.BadRequest, "Bilinmeyen yetki reddedilmeli.");
        }
    }

    private static async Task DeletingUsers()
    {
        WebPanelAuth auth = new();
        DeviceStore devices = new();
        const string ip = "127.0.0.1";
        WebPanelUser admin = WebPanelUserStore.FindByName("yonetici")!;
        WebPanelUser manager = WebPanelUserStore.FindByName("sorumlu")!;
        Check(auth.CreateUser(admin, ip, "silinecek", "Silinecek Kişi", "Operatör", Password, false) is null, "Silinecek kullanıcı açılabilmeli.");
        WebPanelUser victim = WebPanelUserStore.FindByName("silinecek")!;
        PermissionOverrides.Replace(victim.Id, new Dictionary<string, bool> { [Perm.MoveScrap] = false }, "test");

        DefaultHttpContext panel = Context("10.4.0.1");
        Check(await auth.LoginAsync(panel, "silinecek", Password) is null, "Silinecek kullanıcı panele girebilmeli.");
        var phone = await auth.MobileLoginAsync(Context("10.4.0.2"), devices, "silinecek", Password, "Telefon");
        Check(phone.Error is null, "Silinecek kullanıcı telefona girebilmeli.");
        DefaultHttpContext bystander = Context("10.4.0.3");
        Check(await auth.LoginAsync(bystander, "sorumlu", Password) is null, "Başka kullanıcı panele girebilmeli.");

        Check(auth.DeleteUser(admin, ip, admin.Id, admin.UserName, devices, out _) is not null, "Kullanıcı kendini silememeli.");
        Check(auth.DeleteUser(manager, ip, admin.Id, admin.UserName, devices, out _) is not null, "Yönetici olmayan, Yönetici hesabını silememeli.");
        Check(auth.DeleteUser(admin, ip, victim.Id, "baska-ad", devices, out _) is not null, "Onay adı yanlışsa silinmemeli.");
        Check(auth.DeleteUser(manager, ip, victim.Id, "SILINECEK", devices, out string summary) is null, "Kullanıcı yöneticisi Yönetici olmayan hesabı silebilmeli.");
        Check(summary.Contains("1 panel", StringComparison.Ordinal) && summary.Contains("1 telefon", StringComparison.Ordinal), "Kapatılan oturumlar bildirilmeli: " + summary);
        Check(WebPanelUserStore.Find(victim.Id) is null && WebPanelUserStore.FindByName("silinecek") is null, "Silinen kullanıcı listede görünmemeli.");
        Check(auth.Current(WithCookie(panel)) is null, "Silinen kullanıcının panel oturumu hemen kapanmalı.");
        Check(devices.Validate(phone.Token!) is null && devices.All().All(s => s.UserId != victim.Id), "Silinen kullanıcının telefon oturumu hemen kapanmalı.");
        DefaultHttpContext bystanderRequest = Context("10.4.0.3");
        bystanderRequest.Request.Headers.Cookie = CookieValue(bystander);
        Check(auth.Current(bystanderRequest) is not null, "Silme başka kullanıcının oturumunu kapatmamalı.");
        Check((await auth.AuthenticateAsync("10.4.0.9", "silinecek", Password, "mobil")).User is null, "Silinen kullanıcı giriş yapamamalı.");
        Check(auth.CreateUser(admin, ip, "silinecek", null, "İzleyici", Password, false) is not null, "Silinen kullanıcının adı yeni hesaba verilmemeli.");
        Check(PermissionOverrides.For(victim.Id).Count == 0, "Silinen kullanıcının özel yetkileri temizlenmeli.");
        Check(auth.DeleteUser(admin, ip, victim.Id, "silinecek", devices, out _) is not null, "Silinen kullanıcı ikinci kez silinmemeli.");
        Check(WebPanelAudit.Latest(20).Any(e => e.Action == "Kullanıcı silindi" && e.Target == "silinecek" && e.Actor == "sorumlu"), "Silme denetim kaydına yazılmalı.");
        Check(auth.CreateUser(admin, ip, "silmesonrasi", null, "İzleyici", Password, false) is null && WebPanelUserStore.FindByName("silmesonrasi")!.Id > victim.Id,
            "Silinen kullanıcının numarası yeniden kullanılmamalı.");

        Check(auth.CreateUser(admin, ip, "yonetici4", null, PanelRoles.Admin, Password, false) is null, "Ek yönetici açılabilmeli.");
        Check(auth.DeleteUser(admin, ip, WebPanelUserStore.FindByName("yonetici4")!.Id, "yonetici4", devices, out _) is null, "Yönetici başka bir yöneticiyi silebilmeli.");
        foreach (WebPanelUser other in WebPanelUserStore.All().Where(u => u.Role == PanelRoles.Admin && u.IsActive && u.Id != admin.Id).ToList())
        {
            Check(auth.UpdateUser(admin, ip, other.Id, other.DisplayName, PanelRoles.Admin, false) is null, "Fazladan yönetici pasife alınabilmeli.");
        }

        WebPanelUser staleAdmin = WebPanelUserStore.FindByName("yonetici3")!;
        string? lastAdmin = auth.DeleteUser(staleAdmin, ip, admin.Id, admin.UserName, devices, out _);
        Check(lastAdmin is not null && lastAdmin.Contains("Son aktif yönetici", StringComparison.Ordinal), "Son aktif yönetici silinememeli.");
        Check(WebPanelUserStore.IsConfigured && WebPanelUserStore.Find(admin.Id)!.IsActive, "İlk yönetici aktif kalmalı.");
    }

    // Every /api and /panel/api endpoint must carry permission metadata; a forgotten one would be refused anyway.
    private static void CheckEveryEndpointHasPermission(WebApplication app)
    {
        List<RouteEndpoint> endpoints = [.. ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>()];
        List<string> missing = [];
        int checkedCount = 0;
        foreach (RouteEndpoint endpoint in endpoints)
        {
            string route = "/" + (endpoint.RoutePattern.RawText ?? string.Empty).TrimStart('/');
            bool mobile = route.StartsWith("/api/", StringComparison.Ordinal) && !DeviceAuthMiddleware.PublicPaths.Contains(route);
            if (!mobile && !route.StartsWith("/panel/api/", StringComparison.Ordinal))
            {
                continue;
            }

            checkedCount++;
            RequiresPermission? required = endpoint.Metadata.GetMetadata<RequiresPermission>();
            if (endpoint.Metadata.GetMetadata<SignedInOnly>() is null && (required is null || required.AnyOf.Length == 0 || !required.AnyOf.All(Perm.ByKey.ContainsKey)))
            {
                missing.Add(route);
            }
        }

        RouteEndpoint? info = endpoints.FirstOrDefault(e => "/" + (e.RoutePattern.RawText ?? string.Empty).TrimStart('/') == "/api/tup/bilgi");
        Check(info?.Metadata.GetMetadata<RequiresPermission>()?.AnyOf is [Perm.CylinderView],
            "Tüp Bilgisi (salt okunur) yalnızca tüp görüntüleme yetkisi istemeli.");
        Check(checkedCount > 50, $"Uç noktalar bulunamadı ({checkedCount}).");
        Check(missing.Count == 0, "Yetki tanımı olmayan uç noktalar: " + string.Join(", ", missing));
    }

    private static async Task<(string Token, JsonElement Account)> MobileLogin(HttpClient http, string user)
    {
        using HttpResponseMessage response = await http.PostAsJsonAsync("api/giris", new { kullanici = user, sifre = Password, cihazAdi = "Test" });
        Check(response.StatusCode == HttpStatusCode.OK, $"{user} mobil girişi başarılı olmalı ({(int)response.StatusCode}).");
        JsonElement json = await Json(response);
        return (json.GetProperty("token").GetString()!, json.GetProperty("account"));
    }

    private static async Task<string> PanelLogin(HttpClient http, string user)
    {
        using HttpResponseMessage response = await http.PostAsJsonAsync("panel/giris", new { kullanici = user, sifre = Password });
        Check(response.StatusCode == HttpStatusCode.OK, $"{user} panel girişi başarılı olmalı.");
        string setCookie = response.Headers.GetValues("Set-Cookie").First(c => c.StartsWith(WebPanelAuth.CookieName + "=", StringComparison.Ordinal));
        return setCookie.Split(';')[0];
    }

    private static async Task<HttpResponseMessage> Send(HttpClient http, HttpMethod method, string path, string? token, object? body = null, string? cookie = null, bool csrf = true, string? key = null)
    {
        HttpRequestMessage request = new(method, path);
        if (token is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
            if (csrf)
            {
                request.Headers.Add(WebPanelAuth.CsrfHeader, "1");
            }
        }

        if (key is not null)
        {
            request.Headers.Add(IdempotencyMiddleware.Header, key);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await http.SendAsync(request);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static DefaultHttpContext Context(string ip)
    {
        DefaultHttpContext context = new();
        context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        context.Request.Host = new HostString("127.0.0.1");
        return context;
    }

    private static string CookieValue(DefaultHttpContext login) =>
        login.Response.Headers.SetCookie.ToString().Split(';')[0];

    private static DefaultHttpContext WithCookie(DefaultHttpContext login)
    {
        DefaultHttpContext request = Context("10.1.0.1");
        request.Request.Headers.Cookie = CookieValue(login);
        return request;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
