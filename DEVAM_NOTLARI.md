# Devam Notları — 11 Ekim 2026, 01:20

## GEÇİŞ — eski sistemden tüp takip sistemine taşıma (beklemede, kullanıcı "sonra yapacağız" dedi, 12 Ekim 2026)
Hedef: firmadaki eski programın tüm verisi (tüpler, stok, müşteriler, hareketler, bakiyeler) bir kez bizim sunucuya
taşınacak; bundan sonra iş yalnızca bizim sistemden (ERP + mobil + web paneli) yürüyecek. Eski sistem bir süre salt-okunur yedek kalır.

### 0. Ortak için ayrı ve bağımsız tüp takip sistemi (kullanıcı isteği, 12 Ekim 19:24)
Tüp takip (mobil + site + el terminali + geçiş verisi) ortağın kullanımı için bizim sistemden tamamen ayrılacak; bizdeki sistem aynen kalır.
Altyapı/kod aynı, veri ve erişim ayrı; geliştirme yine Cursor + Telegram + sunucu üzerinden.
- Ayrı PostgreSQL veritabanı + yalnız ona yetkili ayrı DB kullanıcısı (bizim erp/crm şemalarına erişemez)
- Ayrı MobileApi örneği (ayrı systemd servisi, ayrı port, ayrı veri klasörü, ayrı panel şifresi), ayrı alan adı/alt alan (cloudflared)
- Ayrı APK (farklı paket adı/uygulama adı, öntanımlı sunucu ortağın adresi) → aynı telefonda ikisi birlikte kurulabilir
- Ayrı Telegram botu + ayrı sunucu ajanı örneği (ortağın telefonu/sohbeti); ERP masaüstü ve bizim müşteri verisi bu sisteme bağlanmaz
- Ayrı gece yedeği (Drive'da ayrı klasör)
- Karar (19:28): ayrı alan adı YOK; bizim site (arsantuptakip.com) üzerinden, farklı kullanıcı adı/şifre ile giriş →
  yalnız kendi verisini görür. Uygulama: aynı giriş ekranı, kullanıcı hangi sisteme aitse oraya yönlendirilir; arkada ayrı
  MobileApi örneği + ayrı DB (ör. cloudflared yol yönlendirmesi arsantuptakip.com/<ortak>/ → ayrı port). Panelde zaten
  kullanıcı/rol sistemi var (WebPanelEndpoints.cs, WebPanelAuth).
- Karar (19:28): Telegram aynı telefon/aynı hesap (kullanıcının kendisi), sadece AYRI BOT açılacak; ayrım bot bazında
  (ortak botu yalnız ortak verisine dokunur, bizim bot yalnız bizimkine).
- Bekleyen kararlar: sistem/uygulama adı, kod ortak mı (güncellemeler ikisine de) yoksa ayrı kopya mı, başlangıç verisi boş mu

#### ArsanTüpTakip — durum (12 Ekim 19:59, kullanıcı: "bu şekilde not al, sonra devam edeceğiz")
Kararlar: ad ArsanTüpTakip; kod ve güncellemeler tamamen ayrı (ortak hiçbir şey yok); iki sistemin de en yetkili
kullanıcısı bizim kullanıcı; boş başlar, veri GEÇİŞ ile eski sanal sunucudan gelir; girişler/bağlantılar sonra.
Yapıldı (commit a4c6cb5, CI "ArsanTupTakip" api + mobil-derleme yeşil):
- Kod `Tools/ArsanTupTakip/` (src, tests, mobile, deploy, README.md), CI `.github/workflows/arsantuptakip.yml`
- Sunucu: `arsantuptakip-api` yalnız 127.0.0.1:5080/tuptakip; /opt/arsantuptakip, /var/lib/arsantuptakip, /etc/arsantuptakip/api.env
- DB `arsantuptakip` + rol `arsantuptakip_app` (arsangaz'a bağlanamaz), 8 boş tablo; gece 04:20 yedek → Drive `ArsanTupTakip-Yedek`
- Mevcut sistem değişmedi (servisler, cloudflared ayarı, arsantuptakip.com yanıtları aynı)
- 12 Ekim 20:35: ayrı bot @TuptakipArsanbot + sunucu Cursor ajanı kuruldu (`Tools/ArsanTupTakip/bot`, servisler
  `arsantuptakip-bot`/`arsantuptakip-agent`, dal `cursor/arsantuptakip-agent`, yayın `/yayin` → `/yayin_onay <kod>`,
  `/geri_al`; ayrıntı README). Henüz hiç `/yayin_onay` yapılmadı: `/opt/arsantuptakip/api` hâlâ elle kurulan sürüm.
Sıradaki (kullanıcı "devam" deyince):
- [ ] Dışa açma: cloudflared'e /tuptakip yolu veya alt alan adı (mevcut tüneli taşıdığı için dikkatli; önce/sonra kontrol)
- [ ] TUPTAKIP_PUBLIC_URL + mobil sunucu adresi; ilk yönetici hesabı (komut README.md'de, şifre ekrana yazılmaz)
- [ ] APK imza anahtarı + yayın (paket ayrı, mevcut uygulamayla yan yana)
- [x] Ayrı Telegram botu (kullanıcının kendi hesabı)
- [ ] Eski sanal sunucudan veri geçişi, Honeywell el terminali

### 1. Veri taşıma (tek seferlik)
- Eski sistem: firmadaki bir cihazda çalışan sanal sunucu. Program adı, sürümü ve veritabanı türü bilinmiyor.
- Erişim yolu (kullanıcı seçecek): Tailscale (önerilen; geçiş bitince kaldırılır), AnyDesk/TeamViewer ya da USB ile yedek.
- Gerekenler: cihazda yönetici yetkili Windows hesabı, program adı/sürümü, veritabanı kullanıcı/şifresi (PC'de girilir, Telegram'a yazılmaz).
- Lisans sözleşmesi verinin dışarı alınmasını kısıtlıyor mu, kontrol edilecek.
- [ ] Tam yedek al (eski sisteme yazma yok)
- [ ] Yedeği sunucuda aç, tablo/alan eşleştirmesi çıkar
- [ ] Canlı veri kopyasında deneme aktarımı (barkod/seri no/vergi no ile eşleştirme, çift kayıt önleme)
- [ ] Özet ve sayımları kullanıcıya sun, onaydan sonra canlıya al

### 2. El terminali (Honeywell, Android varsayılıyor)
- Model ve Android sürümü kullanıcıdan öğrenilecek. Kurulum: terminal tarayıcısından arsantuptakip.com/indir.
- Yarım çalışma `gecis/el-terminali` dalında (7d26dcc, derlenmedi, yayınlanmadı): `HardwareScanner.cs` (Otomatik/Kamera/El terminali modu,
  Data Intent ayarları), `ScanInput.cs` (barkod alanına odak, klavye gizleme).
- [ ] MainActivity: hızlı tuş dizisi (wedge + Enter) dinleyicisi ve Data Intent alıcısı (eylem `com.arsangaz.erpmobile.BARCODE`, veri `data`)
- [ ] Tüp, Sefer, Müşteride sayım ve kamera ekranlarına bağla
- [ ] "El terminali" ayar/test ekranı (Bağlantı sekmesi)
- [ ] Mobil 1.8.0 / build 28, CI yeşil, APK yayını
- Terminal Windows CE çıkarsa web paneli seçeneği değerlendirilecek.

## Sunucuya taşıma — yapılacaklar listesi (12 Ekim 2026)
Durum: site, veritabanı (PostgreSQL), Telegram dinleme Oracle sunucusunda; PC'de AutoSync çalışıyor.
- [x] Cursor ajanını sunucuya kurmak — tamamlandı (12 Ekim 04:25). `arsangaz-cursoragent` servisi (kullanıcı
  `cursoragent`, sudo/DB/gizli dosya erişimi yok) Cursor anahtarıyla çalışıyor, depo klonlandı, `cursor/server-agent`
  dalına push izni var (dry-run). Bilgisayar kapalıyken (eşitleme sinyali > 90 sn) veya istek 10 dk sahipsiz kalınca
  kuyruktaki isteği kilitle sahiplenir. `Test-ServerCursorAgent.ps1` uçtan uca testi BAŞARILI.
- [ ] Gmail anahtarını sunucuya güvenli girmek (kampanya e-postaları sunucudan gitsin) — kullanıcı sonra yapacak.
- [ ] WhatsApp/Meta anahtarını sunucuya güvenli girmek (kampanya WhatsApp'ları sunucudan gitsin) — kullanıcı sonra yapacak.
- [ ] Sunucu ajanında eksik komutlar: prospect tarama, /gorev, /ekip, /ara, satır içi butonlar, LLM sohbeti.
- [x] Sunucu dışı yedek kopyası — tamamlandı (12 Ekim 04:42). `arsangaz-pg-offsite.timer` her gün ~03:45'te gece
  yedeğini rclone ile Google Drive `ArsanGazERP-Yedek/nightly` klasörüne gönderir (erişim drive.file ile sınırlı,
  Drive'da 90 gün saklanır, hata olursa Telegram'a bildirim). Bağlantı: `Tools\Server\Connect-DriveBackup.ps1`.
- [x] Elektrik kesikken telefondan geliştirme + yayın (12 Ekim 05:30). Akış: Telegram'a istek → sunucu Cursor ajanı
  derler/test eder (MobileApi, ServerAgent, masaüstü ERP/AgentWatcher derleme kontrolü, köprü testleri) → `cursor/server-agent`
  dalına push → GitHub CI (`.github/workflows/ci.yml`) → `/yayin` (bekleyenler + CI) → `/yayin_onay <kod>` → main'e
  birleştirme + push, MobileApi önce test örneğinde (5078, prod'un kopyası `arsangaz_staging` DB) sonra prod'da, sağlık
  kontrolü başarısızsa otomatik geri dönüş; masaüstü/Watcher PC açılınca AutoSync ile; mobil APK CI'da imzalanır (`/apk`).
  Ayrıntı: `Tools/ServerAgent/README.md` → "Telefondan geliştirme ve yayın".
- [ ] Kullanıcı: GitHub'a yayın anahtarını (arsangaz-release, yazma izinli) eklemek ve `GITHUB_ACTIONS_TOKEN`'ı
  (`Set-ServerAgentValue.ps1 -Key GITHUB_ACTIONS_TOKEN -FromClipboard`) girmek. Bunlar olmadan `/yayin` GitHub'a bağlanamaz.
- [ ] Öneri: `RELEASES_REPO_TOKEN` depo düzeyinde secret; sunucu ajanı kendi dalında iş akışını değiştirip okuyabilir.
  Yalnızca `v*` etiketlerine izinli bir ortama (environment) taşınmalı (APK anahtarı zaten `android-signing` ortamında, yalnızca main).

## Sefer takibi + yayın düzeltmeleri (12 Ekim 2026, akşam)
- Sunucu ajanı Sefer özelliğini ekledi (0311907, mobil 1.7.6/24). Yayında 3 sorun vardı: (1) `arsangaz-release apk`
  GitHub yanıtını python'a argüman olarak veriyordu → "Argument list too long", APK /indir'e konmadı; artık stdin (`py_json`).
  (2) Sefer tabloları PostgreSQL'de hiç oluşmadı: EF tabloları yalnızca boş şemada oluşur → `Data/PostgresSchema.cs` `Erp`
  ek SQL'i (CREATE ... IF NOT EXISTS). Yeni EF tablosu/kolonu eklerken buraya da eklenmeli. (3) Yükleme/teslim/dönüşte
  sefer satırı tüp hareketinden önce yazılıyordu; hareket reddedilirse yanlış "yüklendi/teslim" kalıyordu → sıra düzeltildi.
  Ayrıca `CylinderTripTracking` dönüşte okutulanları eksik sayıyordu (kendi testi kırmızıydı; CI AgentWatcher.Tests'i çalıştırmıyor).
- Web panelinde "Seferler" sekmesi: seferdeki araçlar, yüklenen/teslim/dönen/araçta, eksikler kırmızı + gaz türüne göre,
  yüklenmediği hâlde dönüşte okutulanlar (`/panel/api/seferler`, `/panel/api/sefer/{id}`).
- `arsangaz-release` her işlemden sonra gizli bilgi içermeyen `/var/lib/arsangaz-release/public/status.json` yazar;
  sunucu Cursor ajanı bunu her isteğe "canlı yayın durumu" olarak ekler (yanlış "yayınlanmadı" cevaplarını önlemek için).

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
