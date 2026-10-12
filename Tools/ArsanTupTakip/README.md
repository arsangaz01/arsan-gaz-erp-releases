# ArsanTüpTakip

ArsanGaz ERP'den tamamen bağımsız tüp takip sistemi. Ortak veritabanı, servis, güncelleme kanalı veya
Telegram botu yoktur (kendi botu: @TuptakipArsanbot, aşağıda). Kod ERP'den kopyalanıp ayrıştırılmıştır; CRM, sohbet, kampanya ve ERP köprüleri çıkarılmıştır.

Şu an yalnızca **altyapı** kuruludur: sistem boştur, dışarıya açık değildir ve hiç kullanıcı hesabı yoktur.

## Klasörler

| Yol | İçerik |
| --- | --- |
| `src/ArsanTupTakip.Api` | .NET 10 API + web paneli (`/tuptakip/panel`) |
| `tests/ArsanTupTakip.Tests` | Konsol test çalıştırıcısı (`dotnet run --project tests/ArsanTupTakip.Tests`) |
| `mobile/ArsanTupTakip.Mobile` | MAUI Android uygulaması (`com.arsantuptakip.mobil`, sunucu adresi henüz boş) |
| `bot/` | @TuptakipArsanbot (`telegram-bot.js`) ve sunucu Cursor ajanı (`dev-agent.js`), Node.js |
| `deploy/` | systemd servisleri, yedek betiği/zamanlayıcısı, yayın betiği, kurulum ve doğrulama betikleri |

CI: `.github/workflows/arsantuptakip.yml` (API derleme + testler, bot testleri, yayın betiği benzetim testi).
Dallarda mobil iş imzasız Debug derler; main'de `mobil-apk` işi imzalı Release APK üretir (aşağıda).

## Mobil APK (imzalı, mevcut uygulamayla yan yana)

- Paket `com.arsantuptakip.mobil` (ArsanGaz uygulaması `com.arsangaz.erpmobile`); iki uygulama aynı telefonda yan yana durur.
- İmza: mevcut uygulamanın anahtarı (GitHub `android-signing` ortamındaki sırlar, yalnızca main). Sertifika SHA-256
  `80ae16de…0153`; CI ve sunucu betiği bunu denetler. Mevcut iş akışlarına ve sırlara dokunulmadı.
- main'e her push'ta `mobil-apk` işi APK'yı `arsantuptakip-mobile-apk` artefaktına koyar (`ArsanTupTakip-Mobil.apk` +
  `apk-info.json`; 14 gün). Aynı işte sunucu betiğinin APK okuyucusu (`arsantuptakip-release apkinfo`) aapt ile karşılaştırılır.
- Yayın: Telegram'da `/apk` → `/apk_onay <kod>` (veya sunucuda `sudo arsantuptakip-release apk <sha>`). Betik main'deki
  mobil kodla aynı koddan derlenmiş en yeni başarılı çalışmanın artefaktını indirir; zip bütünlüğü, `apksigner` ile imza
  sertifikası, ikili manifestten paket adı + versionCode/versionName (csproj ve `apk-info.json` ile aynı olmalı) ve
  build numarasının yayındakinden büyük olması denetlenir. Sonra `/var/lib/arsantuptakip/ArsanTupTakip-Mobil.apk`
  (+ `.meta.json`, önceki 3 sürüm `.bak-*`) yazılır; API bunları okur:
  `https://arsantuptakip.com/tuptakip/indir` (APK) ve `/tuptakip/api/guncelleme` (`publishedMobileVersion/Build`, `apkReady`).
  Uygulama açılışta güncellemeyi önerir.
- Yeni APK için `mobile/ArsanTupTakip.Mobile/ArsanTupTakip.Mobile.csproj` içinde `ApplicationVersion` (build) artırılmalı;
  aynı build yeniden yayınlanmaz.
- GitHub okuma anahtarı: `/etc/arsantuptakip/github.env` (`GITHUB_ACTIONS_TOKEN`, root:root 600); `install-bot.sh`
  bir kez `/etc/arsangaz/release.env`'den yazdırmadan kopyalar. Otomatik (zamanlı) APK yayını yoktur.
- Kurulum notu: telefonda tarayıcıdan indirilen APK için "bilinmeyen kaynaklardan yükleme" izni gerekebilir.

## Sunucu düzeni (Oracle sunucusu)

- Servis: `arsantuptakip-api` → yalnızca `127.0.0.1:5080`, yol öneki `/tuptakip`
  (ör. `curl http://127.0.0.1:5080/tuptakip/api/merhaba`).
- Uygulama: `/opt/arsantuptakip/api` (önceki sürüm `/opt/arsantuptakip/api.prev`).
- Veri (panel kullanıcıları, cihazlar, APK): `/var/lib/arsantuptakip`.
- Ayarlar: `/etc/arsantuptakip/api.env` (root, 600; veritabanı bağlantısı burada).
- Veritabanı: `arsantuptakip` (şema `tuptakip`), rol `arsantuptakip_app`. Rol süper kullanıcı değildir;
  `pg_hba.conf` içindeki `# ArsanTupTakip BEGIN/END` bloğu bu rolün yalnızca `127.0.0.1` üzerinden
  `arsantuptakip` veritabanına bağlanmasına izin verir (`arsangaz` dahil diğer tüm veritabanları reddedilir).
- Yedek: `arsantuptakip-backup.timer` her gece 04:20 → `/var/backups/arsantuptakip` (14 gün) ve
  Google Drive `ArsanTupTakip-Yedek/nightly` (90 gün). Durum: `/var/lib/arsantuptakip-backup/status.json`.
  ERP yedek zamanlayıcılarına dokunulmaz; `/etc/arsangaz/rclone.conf` yalnızca okunur.

## Kurulum / güncelleme

Windows'tan (SSH anahtarı `~/.ssh/arsangaz_hetzner`):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\ArsanTupTakip\deploy\Deploy-ArsanTupTakip.ps1
```

Betik linux-arm64 için yayınlar, sunucuya kopyalar ve `install-server.sh` çalıştırır. Tekrar çalıştırmak güvenlidir:
mevcut veritabanı şifresi korunur, şema yalnızca boş veritabanında oluşturulur, sağlık kontrolü başarısız olursa
önceki sürüme döner. Yönetici hesabı oluşturmaz, cloudflared'e dokunmaz.

Doğrulama (salt okunur; mevcut sistemin ve yalıtımın durumunu yazar):

```bash
sudo bash deploy/verify-server.sh
```

## Telegram botu ve geliştirme ajanı (@TuptakipArsanbot)

ArsanGaz ERP botundan ayrı ikinci bot. Yalnızca kullanıcının kendi sohbetini kabul eder; diğer sohbetler yok sayılır.
ERP'nin servislerine, botuna, `arsangaz-release` düzenine ve `/etc/arsangaz` dosyalarına dokunmaz.

| Komut | İş |
| --- | --- |
| `/durum` | API sağlığı, veritabanı + tablo kayıt sayıları, son yedek, yayındaki commit, ajan durumu |
| `/yayin` | Yayın bekleyen değişiklikleri gösterir, 15 dk geçerli 6 haneli onay kodu verir |
| `/yayin_onay <kod>` | Sunucuda derleme + testler → (ajan dalıysa) main'e birleştirme ve GitHub'a gönderme → `/opt/arsantuptakip/api` kurulumu → sağlık kontrolü; geçmezse önceki kuruluma otomatik döner |
| `/geri_al` → `/geri_al <kod>` | Bir önceki kuruluma döner (şimdiki kurulum yedek olur) |
| `/apk` → `/apk_onay <kod>` | main'deki mobil uygulamanın imzalı APK'sını GitHub'dan alıp doğrular ve `/tuptakip/indir`'e koyar |
| `/iptal`, `/yeni`, `/yardim` | Süren isteği durdurur / yeni Cursor konuşması / komut listesi |

Komut olmayan her mesaj (metin, fotoğraf, belge, video; video karelere çevrilir) bir geliştirme isteğidir:

1. `arsantuptakip-bot` (kullanıcı `tuptakipbot`) isteği ve ekleri `/var/lib/arsantuptakip-queue/requests/<id>/` altına yazar.
2. `arsantuptakip-agent` (kullanıcı `tuptakipagent`, sudo ve GitHub anahtarı yok) isteği kendi klonunda
   (`/home/tuptakipagent/arsan-gaz-erp`, dal `cursor/arsantuptakip-agent`, model composer-2.5) Cursor SDK ile işler.
   Talimatı: yalnızca `Tools/ArsanTupTakip/**` değiştirilir, derleme + testler geçmeden commit edilmez, push yapılmaz.
   İlerleme ve son rapor bot üzerinden Telegram'a gelir.
3. Ajanın commit'leri `outgoing/agent-branch.bundle` paketine konur. `/yayin` paketi root'taki yayın deposuna alır;
   `Tools/ArsanTupTakip` dışına dokunan commit'ler reddedilir. main'e birleştirme yalnızca `/yayin_onay` ile ve sunucudaki
   derleme + testler geçince yapılır.

Sunucu düzeni:

- Yayın betiği: `/usr/local/sbin/arsantuptakip-release status|sync|pending|publish <sha>|previous|rollback|apkcheck|apk <sha>` (root).
  Bot yalnızca bu betiği sudo ile çalıştırabilir (`/etc/sudoers.d/arsantuptakip-bot`); değiştiren eylemler ayrı bir
  geçici systemd birimi olarak çalışır. Durum/depo: `/var/lib/arsantuptakip-release` (ajanın okuduğu ayna: `mirror.git`).
  Derleme kullanıcısı `tuptakipbuild`. Kurulum `api` + `api.prev`; yayınlanan commit `api/.release-sha` dosyasında.
- GitHub: `/etc/arsantuptakip/release/github_release` (yalnızca root), depoya yazma izinli deploy key olarak eklidir.
- Ayarlar (root:root 600, yalnızca systemd okur): `/etc/arsantuptakip/telegram.env` (`TUPTAKIP_TELEGRAM_BOT_TOKEN`,
  `TUPTAKIP_TELEGRAM_CHAT_ID`), `/etc/arsantuptakip/agent.env` (`CURSOR_API_KEY`). Kurulum betiği sohbet kimliğini ve
  Cursor anahtarını mevcut sistemden yalnızca bir kez, yazdırmadan kopyalar.
- Bildirim kutusu: `/var/lib/arsantuptakip-queue/notices/<ad>.txt` (grup `tuptakipq`, 0660) dosyasını bot olduğu gibi
  Telegram'a gönderip siler (sunucu betikleri/yönetici duyuruları için).
- Bot/ajan kodu `/opt/arsantuptakip-bot/app`; `/yayin` bunları güncellemez. Güncellemek için Windows'tan:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\ArsanTupTakip\deploy\Deploy-ArsanTupTakipBot.ps1
```

Testler: `node --test bot/*.test.js` ve `bash deploy/test-release.sh` (yayın betiği benzetimi; root/systemd/GitHub gerekmez).
`/durum` Telegram'a göndermeden denenebilir:
`sudo -u tuptakipbot env TUPTAKIP_BOT_STATE_DIR=/tmp/sim node /opt/arsantuptakip-bot/app/telegram-bot.js --simulate /durum`.

## İlk yönetici hesabı (daha sonra)

Hesap henüz yok; panel girişi "henüz yapılandırılmadı" der. Oluşturmak için sunucuda:

```bash
sudo bash -c 'set -a; . /etc/arsantuptakip/api.env; set +a; read -rsp "Geçici şifre: " P; echo;
  printf "%s\n" "$P" | runuser -u arsantuptakip -- env TUPTAKIP_DATA_DIR=/var/lib/arsantuptakip \
  TUPTAKIP_PG_CONNECTION="$TUPTAKIP_PG_CONNECTION" /usr/bin/dotnet /opt/arsantuptakip/api/ArsanTupTakip.Api.dll --ilk-yonetici <kullanici>'
```

Şifre en az 8 karakter, harf ve rakam içermeli; ilk girişte değiştirilmesi zorunludur. Etkin bir yönetici varken
komut yeni yönetici oluşturmaz. Şifre unutulursa aynı biçimde `--sifre-sifirla <kullanici>` kullanılır.
Diğer kullanıcılar (Yönetici / Operatör / İzleyici) panelden eklenir.

## Kullanıcılar ve yetkiler

Yetki kataloğu `src/ArsanTupTakip.Api/Api/Permissions.cs` (`Perm.Catalog`) içindedir: giriş (panel / mobil), özet ve
raporlar, tüpler (görüntüleme, kayıt, düzenleme, pasife alma, müşteride sayım), her tüp durumu için ayrı hareket yetkisi
(teslim, iade, araç, dolum, servis, hurda) ile dolu/boş seçimi, toplu hareket, "başka müşterideki tüpü yine de kaydet",
hareket geçmişi ve Excel, müşteriler, seferler (görüntüleme, başlatma, yükleme, teslim, dönüş, kapatma), araçlar
(görüntüleme, ekleme, düzenleme, pasife alma) ve yönetim (kullanıcı, cihaz, denetim kaydı).

- Roller hazır şablondur: **Yönetici** her şeye sahiptir ve kısıtlanamaz; **Operatör** ve **İzleyici** varsayılanları
  kullanıcı bazında tek tek açılıp kapatılabilir. Yalnızca farklar `tuptakip.user_permission_overrides` tablosunda
  tutulur (açılışta `UpgradeSql` ile oluşturulur).
- Panelde **Yönetim → Kullanıcılar → Düzenle**: gruplu işaret listesi; rol varsayılanından eklenenler yeşil, kaldırılanlar
  kırmızı görünür, "Rolün varsayılanına dön" farkları siler. Bir yetki işaretlenince ön koşulu da işaretlenir.
- Yönetici olmayan bir kullanıcı yöneticisi kendinde olmayan yetkiyi veremez, Yönetici hesaplarına ve kendi yetkilerine
  dokunamaz. Son aktif yönetici pasife alınamaz veya düşürülemez.
- Her `/panel/api` ve `/api` uç noktası yetki bilgisi taşır; yetkisi tanımlanmamış uç nokta reddedilir. Panel ve mobil
  uygulama yetkisi olmayan sekme ve düğmeleri gizler; asıl denetim sunucudadır (403 ve Türkçe mesaj).
- Yalnızca yetki değişikliği açık oturumları kapatmaz (bir sonraki istekte geçerli olur); rol değişikliği, pasife alma ve
  şifre değişikliği oturumları kapatır.

## Mobil giriş ve oturum süreleri

Telefonda web paneldeki kullanıcı adı ve şifreyle giriş yapılır (`POST /api/giris`); hatalı deneme kilidi panelle
ortaktır (adres başına 5 deneme / 15 dk, saatte toplam 20). Eski 6 haneli eşleştirme kaldırıldı.

- Mobil oturum girişten itibaren **24 saat**, panel oturumu girişten itibaren **1 saat** geçerlidir (kesin süre,
  kullanım uzatmaz). Süre dolunca sunucu 401 döner; uygulama giriş ekranını açar, panel giriş sayfasına döner.
- Her giriş ayrı rastgele anahtar alır; aynı kullanıcı birden çok telefonda/tarayıcıda aynı anda açık kalabilir, yeni
  giriş diğerlerini kapatmaz. Yönetici **Yönetim → Mobil oturumlar** listesinden (kullanıcı, cihaz, bitiş) tek bir
  telefon oturumunu kapatabilir.
- İlk girişte şifre değiştirmesi gereken kullanıcı telefonda da önce şifresini değiştirir; şifre her iki tarafta ortaktır.

## Eşzamanlı kullanım

- Tüp, araç, müşteri ve sefer kayıtlarında PostgreSQL `xmin` satır sürümüyle iyimser eşzamanlılık: ekranda görülen
  sürüm kaydedilirken gönderilir; arada başka biri değiştirdiyse hiçbir şey yazılmaz ve 409 ile "Bu kayıt başka bir
  kullanıcı tarafından değiştirildi, yenileyip tekrar deneyin" döner (panel ve uygulama kaydı yeniler).
- Yazma istekleri `Idempotency-Key` başlığı taşır (`tuptakip.request_keys`): bağlantı kopup aynı okutma tekrar
  gönderilirse hareket ikinci kez işlenmez, ilk cevap döner. Bağlantısızken saklanan hareketler aynı anahtarla gönderilir.
- Testler: `dotnet run --project tests/ArsanTupTakip.Tests`; PostgreSQL eşzamanlılık testleri `TUPTAKIP_TEST_PG_CONNECTION`
  tanımlıysa çalışır (CI'da geçici postgres servisiyle).

## Sonraki adımlar

1. Dışa açma: cloudflared'e ayrı bir yol (`/tuptakip` → `http://127.0.0.1:5080`) veya alt alan adı eklemek.
   Bu, ERP'nin tünel yapılandırmasını etkilediği için ayrıca onayla yapılmalı.
2. `/etc/arsantuptakip/api.env` içine `TUPTAKIP_PUBLIC_URL=...` ekleyip servisi yeniden başlatmak;
   mobilde `ServerUrlCoordinator.DefaultServerUrl` değerini aynı adresle doldurmak.
3. İlk yöneticiyi yukarıdaki komutla oluşturmak.
4. ~~İmzalı APK yayını~~ — yapıldı (yukarıda "Mobil APK").
5. Ortağın eski sanal sunucusundaki verileri geçiş planına göre `arsantuptakip` veritabanına aktarmak.
6. İstenirse Honeywell el terminali desteği. (Ayrı Telegram botu kuruldu: yukarıdaki bölüm.)
