# Devam Notları — 11 Ekim 2026, 01:20

## Bekleyen kararlar (kullanıcıdan)
1. **Commit + push onayı**: kampanya, Gmail göndericisi, mobil 1.6.1 ve hata düzeltmeleri. Henüz commit yok.
   Worker yapılandırma dosyaları commit'e girmeyecek.
2. Eski açık sorular: mobil öneriler (1–8), `publish\staging-*` klasörlerinin silinmesi (silmek yerine taşımak tercih),
   "Geciken faturalar" sekmesinin kaldırılması (sistemde fatura olmayacak).

## Gmail göndericisi (ozelgazlar@arsangaz.com, Google Workspace)
- Durum: Uygulama şifreleri sayfası "Aradığınız ayar, hesabınızda kullanılamıyor" diyor.
- Yapılacak (kullanıcı):
  1. https://myaccount.google.com/signinoptions/twosv → 2 adımlı doğrulamayı aç.
  2. Açılmıyorsa admin.google.com → Güvenlik → Kimlik doğrulama → 2 Adımlı Doğrulama →
     "Kullanıcıların açmasına izin ver".
  3. https://myaccount.google.com/apppasswords → "ArsanGazERP" adlı şifreyi oluştur.
  4. PC'de `Tools\Set-GmailSender.ps1` çalıştır. Şifre Telegram'a yazılmaz.
- Alternatif (yönetici izni yoksa): Workspace SMTP relay (IP izni) veya OAuth. İkisi de kod değişikliği ister.
- Doğrulanmadı: Gerçek mail gönderimi.

## Mobil uygulama (yayında 1.6.2 / build 10, 01:30)
- 1.6.2: "Yeni sohbet" düğmesi taşan seçenek satırından "Kime:" satırının sağına taşındı (ChatPage.cs).
  Telefonda görünümü doğrulanmadı. 1.6.1 APK yedeği `publish\mobile\ArsanGazERP-Mobil-1.6.1.apk`.

### Önceki (1.6.1 / build 9)
- Açılışta çökme: Muhtemel neden, eşzamanlı derlemelerin aynı `obj` klasörünü bozmasıydı.
  `%TEMP%\arsangaz-mobile-clean` içinde temiz derleme yapıldı.
- Yayında: `%LOCALAPPDATA%\ArsanGazERP\MobileApi\ArsanGazERP-Mobil.apk` (32.212.974 bayt). Kopyası `publish\mobile\`.
  `/api/guncelleme` → 1.6.1 / 9.
- İndirme: https://institute-keys-municipal-kijiji.trycloudflare.com/indir (tünel adresi değişebilir).
- Çökme kaydı: Çökerse sonraki açılışta hata ekranı çıkar → ekran görüntüsü istenecek.
- 01:02'de telefondan "Yeni sohbet tuşu ekrana sığmıyor" isteği geldi (bridge.js aldı). Uygulama artık açılıyor olabilir.
  Hangi build'in kurulu olduğu doğrulanmadı. Bridge'in sonucunu `cursor-journal.md` sonundan kontrol et.
- Ders: Mobil derlemeleri aynı anda iki ajanla aynı klasörde çalıştırma.

## Kampanya
- Hazır: gruplar, onay kaydı (ret kaydı ezilmez), e-posta (Gmail), yarı otomatik WhatsApp, mobil Kampanya sekmesi, zamanlayıcı (AgentWatcher).
- Eksik (kullanıcı): Gmail uygulama şifresi; otomatik WhatsApp için Meta şablonu. E-posta PDF ek sınırı 2,5 MB.
- Gerçek müşterilere gönderim onaysız yapılmaz.

## Çalışan sürümler / yedekler
- AgentWatcher (Gmail destekli) `publish\current\AgentWatcher`. Yedek: `*.exe.bak-pre-gmail`.
- MobileApi (bridge yayını, 1.6.1) `publish\current\MobileApi`. Yedek: `*.exe.bak-1.6pre`.
- `devices.json` düz dizi olmalı. PS5 ile `@(ConvertFrom-Json)` kullanarak yeniden yazma. Yedek: `devices.json.bak-kampanya`.
