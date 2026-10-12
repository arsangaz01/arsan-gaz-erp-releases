using ArsanTupTakip.Api;
using ArsanTupTakip.Services;

namespace ArsanTupTakip.Tests;

internal static class ApiLogicTests
{
    internal static void Run()
    {
        PasswordAndUserNameRules();
        InitialAdminMustChangePasswordAndIsCreatedOnce();
        UpdateInfoNeedsApkAndMeta();
        CustomerNamesCompareIgnoringCaseAndSpaces();
        Console.WriteLine("PASS: şifre kuralları, ilk yönetici, güncelleme bilgisi ve müşteri adı eşleştirme.");
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
