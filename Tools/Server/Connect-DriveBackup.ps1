<#
.SYNOPSIS
    Connects the Oracle server's off-site backup to Google Drive. Opens the browser for Google sign-in (rclone authorize,
    scope drive.file: rclone can only see the files it creates), pipes the resulting token straight to the server over SSH
    without printing it, then runs the first upload.
.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File Tools\Server\Connect-DriveBackup.ps1
#>
param(
    [string]$ServerHost = 'arsangaz@144.24.189.205',
    [string]$KeyPath = (Join-Path $env:USERPROFILE '.ssh\arsangaz_hetzner')
)

$ErrorActionPreference = 'Stop'
$ssh = @('-i', $KeyPath, '-o', 'BatchMode=yes', '-o', 'ConnectTimeout=15', $ServerHost)

$rclone = Get-Command rclone -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source
if (-not $rclone) {
    $rclone = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet') -Recurse -Filter rclone.exe -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty FullName
}
if (-not $rclone) { throw 'rclone bulunamadi. Kurulum: winget install Rclone.Rclone' }

Write-Host 'Tarayicida Google girisi acilacak. Yedeklerin gidecegi Google hesabini secip izin verin.' -ForegroundColor Cyan
$options = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('{"scope":"drive.file"}')).TrimEnd('=').Replace('+', '-').Replace('/', '_')
$output = & $rclone authorize drive $options
if ($LASTEXITCODE -ne 0) { throw "Google yetkilendirmesi tamamlanmadi (rclone cikis kodu $LASTEXITCODE)." }

$text = ($output -join "`n")
$match = [regex]::Match($text, '--->\s*(.+?)\s*<---', 'Singleline')
$token = if ($match.Success) { $match.Groups[1].Value.Trim() } else { $text.Trim() }
$text = $null
$output = $null
if (-not $token) { throw 'rclone yetki bilgisi dondurmedi.' }

$result = $token | & ssh @ssh 'sudo /usr/local/sbin/arsangaz-drive-setup' 2>&1
$code = $LASTEXITCODE
$token = $null
if ($code -ne 0) { throw "Sunucu Drive baglantisini kaydedemedi: $($result | Select-Object -Last 1)" }
Write-Output ($result | Select-Object -Last 1)

Write-Host 'Ilk yedek Drive''a gonderiliyor...' -ForegroundColor Cyan
& ssh @ssh 'sudo systemctl start arsangaz-pg-offsite; sudo journalctl -u arsangaz-pg-offsite --since "-2min" --no-pager -o cat | tail -n 3'
