# Copies the production MobileApi data (paired phones, web panel users/settings, trusted IPs, audit log, APK) from
# this PC to the server data folder. Files are written 0600 as the arsangaz user; tunnel credentials are NOT copied.
param([string]$ServerHost = 'arsangaz@144.24.189.205')

$ErrorActionPreference = 'Stop'
$key = Join-Path $env:USERPROFILE '.ssh\arsangaz_hetzner'
$source = Join-Path $env:LOCALAPPDATA 'ArsanGazERP\MobileApi'
$remote = '/var/lib/arsangaz-data/prod/ArsanGazERP/MobileApi'
$names = @('devices.json', 'web-panel-users.json', 'web-panel.json', 'trusted-login-ips.txt', 'web-panel-audit.jsonl',
    'stable-public-url.txt', 'ArsanGazERP-Mobil.apk', 'ArsanGazERP-Mobil.apk.meta.json')

$files = foreach ($name in $names) {
    $path = Join-Path $source $name
    if (Test-Path $path) { $path } else { Write-Host "Yok, atlandi: $name" }
}
scp -i $key -q @files "${ServerHost}:$remote/"
if ($LASTEXITCODE -ne 0) { throw 'Mobil veri dosyalari kopyalanamadi.' }
ssh -i $key -o BatchMode=yes $ServerHost "chmod 600 $remote/* && sha256sum $remote/devices.json $remote/web-panel-users.json $remote/ArsanGazERP-Mobil.apk | awk '{print `$1}'"
if ($LASTEXITCODE -ne 0) { throw 'Sunucuda izinler ayarlanamadi.' }
Get-FileHash (Join-Path $source 'devices.json'), (Join-Path $source 'web-panel-users.json'), (Join-Path $source 'ArsanGazERP-Mobil.apk') -Algorithm SHA256 |
    ForEach-Object { $_.Hash.ToLowerInvariant() }
