# ArsanTüpTakip

ArsanGaz ERP'den tamamen bağımsız tüp takip sistemi. Ortak veritabanı, servis, güncelleme kanalı veya
Telegram botu yoktur. Kod ERP'den kopyalanıp ayrıştırılmıştır; CRM, sohbet, kampanya ve ERP köprüleri çıkarılmıştır.

Şu an yalnızca **altyapı** kuruludur: sistem boştur, dışarıya açık değildir ve hiç kullanıcı hesabı yoktur.

## Klasörler

| Yol | İçerik |
| --- | --- |
| `src/ArsanTupTakip.Api` | .NET 10 API + web paneli (`/tuptakip/panel`) |
| `tests/ArsanTupTakip.Tests` | Konsol test çalıştırıcısı (`dotnet run --project tests/ArsanTupTakip.Tests`) |
| `mobile/ArsanTupTakip.Mobile` | MAUI Android uygulaması (`com.arsantuptakip.mobil`, sunucu adresi henüz boş) |
| `deploy/` | systemd servisi, yedek betiği/zamanlayıcısı, kurulum ve doğrulama betikleri |

CI: `.github/workflows/arsantuptakip.yml` (API derleme + testler, mobil Debug derleme; APK yayınlanmaz).

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

## Mobil cihaz eşleştirme

Yönetici panelde **Cihazlar → Eşleştirme kodu oluştur** ile 6 haneli kod alır (10 dakika, tek kullanımlık,
5 deneme). Telefonda Bağlantı sekmesine sunucu adresi ve kod girilir. Cihazlar panelden kaldırılabilir.

## Sonraki adımlar

1. Dışa açma: cloudflared'e ayrı bir yol (`/tuptakip` → `http://127.0.0.1:5080`) veya alt alan adı eklemek.
   Bu, ERP'nin tünel yapılandırmasını etkilediği için ayrıca onayla yapılmalı.
2. `/etc/arsantuptakip/api.env` içine `TUPTAKIP_PUBLIC_URL=...` ekleyip servisi yeniden başlatmak;
   mobilde `ServerUrlCoordinator.DefaultServerUrl` değerini aynı adresle doldurmak.
3. İlk yöneticiyi yukarıdaki komutla oluşturmak.
4. APK imzalama anahtarı oluşturup Release APK'yı `/var/lib/arsantuptakip/ArsanTupTakip-Mobil.apk`
   (+ `.meta.json`) olarak yayınlamak.
5. Ortağın eski sanal sunucusundaki verileri geçiş planına göre `arsantuptakip` veritabanına aktarmak.
6. İstenirse ayrı bir Telegram botu ve Honeywell el terminali desteği.
