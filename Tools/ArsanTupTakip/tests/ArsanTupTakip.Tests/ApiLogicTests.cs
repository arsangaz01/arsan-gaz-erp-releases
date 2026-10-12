using ArsanTupTakip.Api;
using ArsanTupTakip.Services;

namespace ArsanTupTakip.Tests;

internal static class ApiLogicTests
{
    internal static void Run()
    {
        RolesLimitWhatEachUserMayDo();
        PasswordAndUserNameRules();
        InitialAdminMustChangePasswordAndIsCreatedOnce();
        PairingCodeIsSingleUseAndLimited();
        RemovedDeviceLosesAccess();
        UpdateInfoNeedsApkAndMeta();
        CustomerNamesCompareIgnoringCaseAndSpaces();
        Console.WriteLine("PASS: panel yetkileri, şifre kuralları, ilk yönetici, cihaz eşleştirme, güncelleme bilgisi ve müşteri adı eşleştirme.");
    }

    private static void RolesLimitWhatEachUserMayDo()
    {
        Check(PanelRoles.Has(PanelRoles.Admin, PanelRoles.ManageUsers), "Yönetici kullanıcı ve cihaz yönetebilmeli.");
        Check(!PanelRoles.Has(PanelRoles.Operator, PanelRoles.ManageUsers), "Operatör kullanıcı/cihaz yönetememeli.");
        Check(PanelRoles.Has(PanelRoles.Operator, PanelRoles.Move), "Operatör tüp hareketi yapabilmeli.");
        Check(!PanelRoles.Has(PanelRoles.Viewer, PanelRoles.Move) && !PanelRoles.Has(PanelRoles.Viewer, PanelRoles.Register), "İzleyici kayıt değiştirememeli.");
        Check(PanelRoles.Has(PanelRoles.Viewer, PanelRoles.View) && PanelRoles.Has(PanelRoles.Viewer, PanelRoles.Export), "İzleyici görüntüleyip dışa aktarabilmeli.");
        Check(!PanelRoles.Has("bilinmeyen", PanelRoles.View), "Bilinmeyen rol hiçbir şey yapamamalı.");
        Check(PanelRoles.Normalize("yönetici") == PanelRoles.Admin && PanelRoles.Normalize("IZLEYICI") == PanelRoles.Viewer && PanelRoles.Normalize("x") is null,
            "Rol adları Türkçe karakterli/karaktersiz yazımla eşleşmeli.");
    }

    private static void PasswordAndUserNameRules()
    {
        Check(WebPanelUserStore.PasswordProblem("kisa1", "ali") is not null, "8 karakterden kısa şifre reddedilmeli.");
        Check(WebPanelUserStore.PasswordProblem("sadeceharf", "ali") is not null, "Rakamsız şifre reddedilmeli.");
        Check(WebPanelUserStore.PasswordProblem("yonetici2026x", "yonetici") is not null, "Kullanıcı adını içeren şifre reddedilmeli.");
        Check(WebPanelUserStore.PasswordProblem("Gu7venli-Sifre", "yonetici") is null, "Kurallara uyan şifre kabul edilmeli.");
        Check(WebPanelUserStore.UserNameProblem("yonetici") is null, "yonetici geçerli bir kullanıcı adı olmalı.");
        Check(WebPanelUserStore.UserNameProblem("yönetici") is not null, "Türkçe karakterli kullanıcı adı reddedilmeli.");
    }

    private static void InitialAdminMustChangePasswordAndIsCreatedOnce()
    {
        const string password = "Ilk-Sifre-2026";
        Check(WebPanelUserStore.CreateInitialAdmin("yonetici", password) is null, "İlk yönetici oluşturulabilmeli.");
        WebPanelUser? admin = WebPanelUserStore.FindByName("yonetici");
        Check(admin is not null && admin.Role == PanelRoles.Admin && admin.MustChangePassword && admin.IsActive, "İlk yönetici aktif, Yönetici ve şifre değiştirmeye zorunlu olmalı.");
        Check(WebPanelUserStore.VerifyPassword(admin!, password) && !WebPanelUserStore.VerifyPassword(admin!, password + "x"), "Şifre doğrulaması yalnızca doğru şifreyi kabul etmeli.");
        Check(!File.ReadAllText(WebPanelUserStore.FilePath).Contains(password, StringComparison.Ordinal), "Kullanıcı dosyasında düz şifre bulunmamalı.");
        Check(WebPanelUserStore.CreateInitialAdmin("ikinci", "Baska-Sifre-2026") is not null, "Aktif yönetici varken ilk yönetici yeniden oluşturulmamalı.");
        Check(WebPanelUserStore.IsConfigured, "Yönetici oluşturulduktan sonra panel yapılandırılmış sayılmalı.");
    }

    private static void PairingCodeIsSingleUseAndLimited()
    {
        DeviceStore devices = new();
        PairingService pairing = new(devices);
        Check(pairing.TryPair("123456", "Telefon") is null, "Kod oluşturulmadan eşleşme olmamalı.");

        PairingCode code = pairing.CreateCode();
        Check(code.Code.Length == 6 && code.Code.All(char.IsDigit), "Eşleştirme kodu 6 haneli olmalı.");
        Check(pairing.ActiveCodeExpiresUtc() is not null, "Oluşturulan kod etkin görünmeli.");
        string wrong = code.Code == "000000" ? "111111" : "000000";
        Check(pairing.TryPair(wrong, "Telefon") is null, "Yanlış kod reddedilmeli.");
        string? token = pairing.TryPair(code.Code, "Saha telefonu");
        Check(token is not null && devices.Validate(token) is { Name: "Saha telefonu" }, "Doğru kod erişim anahtarı vermeli.");
        Check(pairing.TryPair(code.Code, "Başka telefon") is null, "Kod ikinci kez kullanılamamalı.");

        PairingCode limited = pairing.CreateCode();
        string other = limited.Code == "000000" ? "111111" : "000000";
        for (int i = 0; i < PairingService.MaximumAttempts; i++)
        {
            pairing.TryPair(other, "Saldırgan");
        }

        Check(pairing.TryPair(limited.Code, "Telefon") is null, "5 hatalı denemeden sonra kod iptal olmalı.");

        PairingCode cancelled = pairing.CreateCode();
        pairing.CancelCode();
        Check(pairing.TryPair(cancelled.Code, "Telefon") is null && pairing.ActiveCodeExpiresUtc() is null, "İptal edilen kod kullanılamamalı.");
        Check(!File.ReadAllText(DeviceStore.FilePath).Contains(token!, StringComparison.Ordinal), "Cihaz dosyasında düz erişim anahtarı bulunmamalı.");
    }

    private static void RemovedDeviceLosesAccess()
    {
        DeviceStore devices = new();
        string token = devices.Add("Kayıp telefon");
        PairedDevice? device = devices.Validate(token);
        Check(device is not null, "Eklenen cihaz doğrulanmalı.");
        Check(devices.Remove(device!.Id) is not null, "Cihaz kaldırılabilmeli.");
        Check(devices.Validate(token) is null, "Kaldırılan cihazın anahtarı geçersiz olmalı.");
        Check(devices.Remove("yok") is null, "Olmayan cihaz kaldırılamamalı.");
    }

    private static void UpdateInfoNeedsApkAndMeta()
    {
        Check(!AppUpdates.Get().ApkReady, "APK yokken güncelleme hazır görünmemeli.");
        File.WriteAllBytes(AppUpdates.ApkFile, [1, 2, 3]);
        Check(!AppUpdates.Get().ApkReady, "Sürüm bilgisi (meta) olmadan APK duyurulmamalı.");
        File.WriteAllText(AppUpdates.ApkFile + ".meta.json", """{"versionName":"1.0.0","versionCode":1}""");
        MobileUpdateInfo info = AppUpdates.Get();
        Check(info.ApkReady && info.PublishedMobileVersion == "1.0.0" && info.PublishedMobileBuild == 1 && info.ApkSizeBytes == 3, "APK ve meta varken sürüm duyurulmalı.");
        File.Delete(AppUpdates.ApkFile);
        File.Delete(AppUpdates.ApkFile + ".meta.json");
    }

    private static void CustomerNamesCompareIgnoringCaseAndSpaces()
    {
        Check(CrudService.CustomerNameKey("  arsan   gaz ") == CrudService.CustomerNameKey("ARSAN GAZ"), "Müşteri adları büyük/küçük harf ve boşluk farkı gözetmeden eşleşmeli.");
        Check(CrudService.CustomerNameKey("iğdır") == "İĞDIR", "Türkçe büyük harf dönüşümü kullanılmalı.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
