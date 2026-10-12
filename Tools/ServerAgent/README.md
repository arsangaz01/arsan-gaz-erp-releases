# Oracle sunucusu: MobileApi, tünel ve ServerAgent

Sunucu: Ubuntu 24.04 arm64 (`arsangaz@144.24.189.205`, Tailscale 100.95.174.106). Veri: `/var/lib/arsangaz-data/prod`
(0700, sahibi `arsangaz`). Veritabanı: PC ile ortak PostgreSQL `arsangaz` (`/etc/arsangaz/postgres.env`).

| Servis | Görev |
|---|---|
| `arsangaz-mobileapi-prod` | Telefon API'si, web panel, `/indir` (127.0.0.1:5077) |
| `cloudflared` | `arsantuptakip.com` adlı tüneli (`/etc/cloudflared/config.yml`) |
| `arsangaz-serveragent` | Telegram giden kutusu, tahsilat hatırlatmaları, (token gelince) Telegram dinleme + 7/24 röle |
| `arsangaz-cursoragent` | Bilgisayar kapalıyken Cursor kuyruğunu işleyen sunucu Cursor ajanı (aşağıda) |

PC tarafı: `%LOCALAPPDATA%\ArsanGazERP\MobileApi\tunnel-disabled.txt` PC'deki tüneli kapalı tutar;
"ArsanGazERP ServerInboxSync" görevi (`Tools\CursorIdeBridge\Sync-ServerInbox.ps1`) sunucudaki Cursor kuyruğunu PC ile eşitler.

## Güncelleme
- Sunucu: `Tools\ServerAgent\deploy\Deploy-Server.ps1 -Component all|mobileapi|serveragent` (eski sürüm `/opt/arsangaz/<ad>.bak-<zaman>`).
- APK: `Tools\Deploy-MobileApk.ps1` APK'yı hem PC'ye hem sunucuya koyar.
- Sunucu dosyaları/servis tanımları: `deploy\install-server.sh` (idempotent).

## Gizli değerler (yalnızca PC'den, gizli giriş)
```powershell
.\Tools\ServerAgent\Set-ServerAgentValue.ps1 -Status
.\Tools\ServerAgent\Set-ServerAgentValue.ps1 -Key TELEGRAM_BOT_TOKEN
.\Tools\ServerAgent\Set-ServerAgentValue.ps1 -Key CLOUD_RELAY_TOKEN
.\Tools\ServerAgent\Switch-TelegramListener.ps1 -To Server   # iki token da girildikten sonra
```

## Sunucu Cursor ajanı (`arsangaz-cursoragent`)
`Tools/CursorTelegramBridge/server-bridge.js` (Cursor SDK yerel mod, bridge.js ile aynı kurallar) `/opt/arsangaz-cursoragent/app`
altında, `cursoragent` kullanıcısıyla çalışır. Bu kullanıcının sudo'su, prod veri klasörüne, `/etc/arsangaz/*.env` dosyalarına ve
veritabanına erişimi yoktur; kuyruğa yalnızca `cursor-inbox.sh` ile (`sudo -u arsangaz`, sudoers'ta tek komut) erişir.
- Karar: PC eşitleme sinyali (`pc-sync-status.json`) 90 sn'den eskiyse veya istek 10 dk sahipsiz kaldıysa sunucu isteği alır.
  `cursor-inbox.sh claim` ile `inbox-sync.sh mark` aynı kilidi (`CopilotInbox/.claim.lock`) kullanır; istek tek tarafa gider.
  Sunucunun aldığı istek `<id>.server-claim` ile işaretlenir; PC onu artık görmez.
- Depo: `/home/cursoragent/arsan-gaz-erp` (GitHub deploy key `~/.ssh/github_deploy`, ilk klonu servis kendisi yapar). Ajan
  yalnızca `cursor/server-*` dallarına push edebilir (pre-push kancası); main ve releases'e push yok.
- Anahtar: `Set-ServerAgentValue.ps1 -Key CURSOR_API_KEY -FromClipboard` → `/etc/arsangaz/cursoragent.env` (0600 cursoragent).
- Kurulum/güncelleme: `deploy\Deploy-Server.ps1 -Component cursoragent`. Test: `Test-ServerCursorAgent.ps1` (gerçek kuyrukta
  `servertest-` isteği; PC'ye verilmez). Kuyruk betik testi: `bash deploy/test-cursor-queue.sh` (geçici klasörde).
- Durum: `/var/lib/arsangaz-cursoragent/status.json` (Telegram `/durum`), günlük: `journalctl -u arsangaz-cursoragent`.
- Kapatma: `sudo systemctl disable --now arsangaz-cursoragent` (istekler yine kuyrukta PC'yi bekler).

## Geri alma
- Tünel PC'ye: PC'de `tunnel-disabled.txt` dosyasını silin (MobileApi 30 sn içinde tüneli açar), sonra sunucuda
  `sudo systemctl disable --now cloudflared`. İki bağlayıcı aynı anda çalışırsa istekler iki makineye bölünür; sunucudakini
  hemen kapatın. Sunucuda yapılan eşleşme/panel kullanıcısı değişiklikleri (`MobileApi\devices.json`, `web-panel-users.json`)
  PC'ye kendiliğinden geri gelmez; gerekirse `scp` ile geri kopyalayın.
- Telegram dinleme PC'ye: `Switch-TelegramListener.ps1 -To Pc`.
- MobileApi/ServerAgent eski sürüm: `sudo systemctl stop <servis>; sudo rm -rf /opt/arsangaz/<ad>; sudo mv /opt/arsangaz/<ad>.bak-<zaman> /opt/arsangaz/<ad>; sudo systemctl start <servis>`.
- Kuyruk eşitleme: `Tools\CursorIdeBridge\Register-ServerInboxSyncTask.ps1 -Unregister`.
- Sunucu servisleri tamamen: `sudo systemctl disable --now arsangaz-mobileapi-prod arsangaz-serveragent cloudflared`.
