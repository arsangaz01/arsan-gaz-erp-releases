# Oracle sunucusu: MobileApi, tünel ve ServerAgent

Sunucu: Ubuntu 24.04 arm64 (`arsangaz@144.24.189.205`, Tailscale 100.95.174.106). Veri: `/var/lib/arsangaz-data/prod`
(0700, sahibi `arsangaz`). Veritabanı: PC ile ortak PostgreSQL `arsangaz` (`/etc/arsangaz/postgres.env`).

| Servis | Görev |
|---|---|
| `arsangaz-mobileapi-prod` | Telefon API'si, web panel, `/indir` (127.0.0.1:5077) |
| `cloudflared` | `arsantuptakip.com` adlı tüneli (`/etc/cloudflared/config.yml`) |
| `arsangaz-serveragent` | Telegram giden kutusu, tahsilat hatırlatmaları, (token gelince) Telegram dinleme + 7/24 röle |

PC tarafı: `%LOCALAPPDATA%\ArsanGazERP\MobileApi\tunnel-disabled.txt` PC'deki tüneli kapalı tutar;
"ArsanGazERP ServerInboxSync" görevi (`Tools\CursorIdeBridge\Sync-ServerInbox.ps1`) sunucudaki Cursor kuyruğunu PC ile eşitler.

## Güncelleme
- Sunucu: `Tools\ServerAgent\deploy\Deploy-Server.ps1 -Component all|mobileapi|serveragent` (eski sürüm `/opt/arsangaz/<ad>.bak-<zaman>`).
- APK: `Tools\Deploy-MobileApk.ps1` APK'yı hem PC'ye hem sunucuya koyar.
- Sunucu dosyaları/servis tanımları: `deploy\install-server.sh` (idempotent).

## Gizli değerler (yalnızca PC'den, gizli giriş)
```powershell
.\Tools\ServerAgent\Set-ServerSecret.ps1 -Status
.\Tools\ServerAgent\Set-ServerSecret.ps1 -Key TELEGRAM_BOT_TOKEN
.\Tools\ServerAgent\Set-ServerSecret.ps1 -Key CLOUD_RELAY_TOKEN
.\Tools\ServerAgent\Switch-TelegramListener.ps1 -To Server   # iki token da girildikten sonra
```

## Geri alma
- Tünel PC'ye: PC'de `tunnel-disabled.txt` dosyasını silin (MobileApi 30 sn içinde tüneli açar), sonra sunucuda
  `sudo systemctl disable --now cloudflared`. İki bağlayıcı aynı anda çalışırsa istekler iki makineye bölünür; sunucudakini
  hemen kapatın. Sunucuda yapılan eşleşme/panel kullanıcısı değişiklikleri (`MobileApi\devices.json`, `web-panel-users.json`)
  PC'ye kendiliğinden geri gelmez; gerekirse `scp` ile geri kopyalayın.
- Telegram dinleme PC'ye: `Switch-TelegramListener.ps1 -To Pc`.
- MobileApi/ServerAgent eski sürüm: `sudo systemctl stop <servis>; sudo rm -rf /opt/arsangaz/<ad>; sudo mv /opt/arsangaz/<ad>.bak-<zaman> /opt/arsangaz/<ad>; sudo systemctl start <servis>`.
- Kuyruk eşitleme: `Tools\CursorIdeBridge\Register-ServerInboxSyncTask.ps1 -Unregister`.
- Sunucu servisleri tamamen: `sudo systemctl disable --now arsangaz-mobileapi-prod arsangaz-serveragent cloudflared`.
