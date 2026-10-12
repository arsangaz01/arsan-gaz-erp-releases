# ArsanTüpTakip API'sini derleyip Oracle sunucusuna kurar (install-server.sh). ArsanGazERP yayın düzenini kullanmaz.
#   powershell -NoProfile -ExecutionPolicy Bypass -File Tools\ArsanTupTakip\deploy\Deploy-ArsanTupTakip.ps1
param(
    [string]$Server = "arsangaz@144.24.189.205",
    [string]$KeyFile = "$env:USERPROFILE\.ssh\arsangaz_hetzner"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$stage = Join-Path $env:TEMP "arsantuptakip-deploy"
Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$stage\deploy" | Out-Null

dotnet publish "$root\src\ArsanTupTakip.Api\ArsanTupTakip.Api.csproj" -c Release -r linux-arm64 --self-contained false `
    -p:PublishSingleFile=false -o "$stage\api" -nologo -clp:ErrorsOnly
if ($LASTEXITCODE -ne 0) { throw "dotnet publish başarısız." }

# Shell scripts must reach the server with LF line endings.
foreach ($name in "install-server.sh", "verify-server.sh", "arsantuptakip-backup", "arsantuptakip-api.service", "arsantuptakip-backup.service", "arsantuptakip-backup.timer") {
    $text = [IO.File]::ReadAllText("$PSScriptRoot\$name").Replace("`r`n", "`n")
    [IO.File]::WriteAllText("$stage\deploy\$name", $text, (New-Object System.Text.UTF8Encoding($false)))
}

$archive = Join-Path $env:TEMP "arsantuptakip-deploy.tar.gz"
Remove-Item $archive -ErrorAction SilentlyContinue
tar -czf $archive -C $stage api deploy
if ($LASTEXITCODE -ne 0) { throw "Arşiv oluşturulamadı." }

scp -i $KeyFile $archive "${Server}:/tmp/arsantuptakip-deploy.tar.gz"
if ($LASTEXITCODE -ne 0) { throw "scp başarısız." }
ssh -i $KeyFile $Server "set -e; rm -rf /tmp/arsantuptakip-deploy; mkdir -p /tmp/arsantuptakip-deploy; tar -xzf /tmp/arsantuptakip-deploy.tar.gz -C /tmp/arsantuptakip-deploy; sudo bash /tmp/arsantuptakip-deploy/deploy/install-server.sh /tmp/arsantuptakip-deploy; rm -rf /tmp/arsantuptakip-deploy /tmp/arsantuptakip-deploy.tar.gz"
if ($LASTEXITCODE -ne 0) { throw "Sunucu kurulumu başarısız." }
