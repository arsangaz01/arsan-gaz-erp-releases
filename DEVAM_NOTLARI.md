# Devam Notları — 11 Ekim 2026, 01:20

## Bekleyen kararlar (kullanıcıdan)
1. **Commit + push onayı**: kampanya, Gmail göndericisi, mobil 1.6.1 ve hata düzeltmeleri. Henüz commit yok.
   Worker yapılandırma dosyaları commit'e girmeyecek.
2. Eski açık sorular: mobil öneriler (1–8), `publish\staging-*` klasörlerinin silinmesi (silmek yerine taşımak tercih),
   "Geciken faturalar" sekmesinin kaldırılması (sistemde fatura olmayacak).

## Jarvis'in beyni Cursor (11 Ekim)
- `Services/CursorJarvisBrain.cs`: Jarvis mesajı → `CopilotInbox/jarvischat-*.request.json` (+ dispatch marker, IDE sohbeti almaz)
  → bridge.js (Cursor SDK, ayrı "Arsan Gaz Jarvis" konuşması) → canlı adımlar progress.json `steps` → completion.
  160 sn içinde bitmezse sonuç Telegram'a gider. Köprü hazır değilse eski yerel modele düşer. İzinli PC komutları anında çalışır.
- Ayar: `AgentWatcherData\jarvis-brain.json` {"Brain":"cursor"|"local","Mode":"cursor-only"|"cursor-first"}; dosya yoksa Cursor.
  Şu an cursor-only (köprü ekledi, kullanıcı "Her şeyi Cursor yapsın" dedi): `/` ile başlamayan tüm ajan mesajları ve PC komutları Cursor'a gider.
  Köprü kapalıysa yerel model yerine hata mesajı döner.
- "Cursor'a aktar / öneriyi uygula" işleri `jarvis-` önekiyle Telegram raporlu kalır (köprü `jarvischat-` yapmıştı, düzeltildi).
- Durum: bridge.js yeniden başlatıldı. MobileApi yayında (yedek `.bak-pre-cursorjarvis`).
  Masaüstü ERP 10:05'te güncellendi (yedek `publish\current\ArsanGazERP.exe.bak-20261011`). Commit henüz yok.
- Doğrulanan: Köprü uçtan uca (10–11 sn, canlı adımlar, konuşma hafızası), node testleri 9/9.
  Doğrulanmayan: Telefondan /api/sohbet ile gerçek deneme.
- Sonraki aşama: Diğer ajanları ve Gözcü araştırmalarını Cursor'un dağıtması.

## GitHub Jarvis raporundan uygulananlar (11 Ekim, rapor: Reports\jarvis-github-inceleme-2026-10-11.md)
- ERP araçları: `.cursor/mcp.json` SDK ajanında çalışmıyordu (autoReview açıkken MCP çağrıları engelleniyor). Çözüm: `Tools/CursorTelegramBridge/erp-tools.js`
  ErpMcpServer'ı köprü içinde başlatıp 10 salt-okuma aracını SDK `customTools` olarak verir (her iki kanal).
- Kişisel veri maskeleme: `pii-mask.js` (telefon 0/+90 önekli veya "tel:" etiketli, e-posta, etiketli TC/VKN; barkodlara dokunmaz). Araç çıktıları ve Jarvis bağlamı maskeli gider.
- Jarvis kalıcı belleği: `jarvis-memory.js`, `AgentWatcherData\jarvis-memory\*.md` (bilgi başına dosya, unutulan `_unutulan`'a taşınır). Araçlar: jarvis_hafiza_kaydet / jarvis_hafiza_unut.
- Acil durdurma: `<id>.cancel.json` → köprü `run.cancel()`; Ajan Kontrol Merkezi'nde "Cursor işleri" sekmesi (`Services/CursorJobMonitor.cs`, `Views/AgentControlCenterWindow.CursorJobs.cs`).
- Doğrulanan: gerçek istekte erp_ozet/crm_ozet + bellek kaydet/unut çalıştı; durdurma ~1 sn; node 15/15, C# test paketi geçti, ERP 0 hata.
  Doğrulanmayan: "Cursor işleri" sekmesi ekranda (ERP yeniden yayınlanmadı, onay bekliyor).

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
