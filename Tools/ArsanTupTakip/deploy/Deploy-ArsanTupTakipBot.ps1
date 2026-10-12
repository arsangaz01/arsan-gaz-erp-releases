# @TuptakipArsanbot (arsantuptakip-bot), ArsanTüpTakip sunucu Cursor ajanı (arsantuptakip-agent) ve yayın betiğini
# (/usr/local/sbin/arsantuptakip-release) Oracle sunucusuna kurar. ArsanGazERP'nin Deploy-Server.ps1 düzenini kullanmaz.
#   powershell -NoProfile -ExecutionPolicy Bypass -File Tools\ArsanTupTakip\deploy\Deploy-ArsanTupTakipBot.ps1
param(
    [string]$Server = "arsangaz@144.24.189.205",
    [string]$KeyFile = "$env:USERPROFILE\.ssh\arsangaz_hetzner"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$stage = Join-Path $env:TEMP "arsantuptakip-bot-deploy"
Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$stage\app", "$stage\deploy" | Out-Null
$utf8 = New-Object System.Text.UTF8Encoding($false)

foreach ($name in "telegram-bot.js", "dev-agent.js", "commands.js", "queue.js", "agent-rules.js", "video-frames.js", "package.json", "package-lock.json") {
    $text = [IO.File]::ReadAllText("$root\bot\$name").Replace("`r`n", "`n")
    [IO.File]::WriteAllText("$stage\app\$name", $text, $utf8)
}
foreach ($name in "arsantuptakip-release", "test-release.sh", "install-bot.sh", "arsantuptakip-bot.service", "arsantuptakip-agent.service") {
    $text = [IO.File]::ReadAllText("$PSScriptRoot\$name").Replace("`r`n", "`n")
    [IO.File]::WriteAllText("$stage\deploy\$name", $text, $utf8)
}

$archive = Join-Path $env:TEMP "arsantuptakip-bot-deploy.tar.gz"
Remove-Item $archive -ErrorAction SilentlyContinue
tar -czf $archive -C $stage app deploy
if ($LASTEXITCODE -ne 0) { throw "Arşiv oluşturulamadı." }
scp -i $KeyFile -q $archive "${Server}:/tmp/arsantuptakip-bot-deploy.tar.gz"
if ($LASTEXITCODE -ne 0) { throw "scp başarısız." }
Remove-Item $archive

$new = "/tmp/arsantuptakip-bot-deploy"
ssh -i $KeyFile -o BatchMode=yes $Server (@(
    "set -eo pipefail",
    "rm -rf $new && mkdir -p $new && tar -xzf /tmp/arsantuptakip-bot-deploy.tar.gz -C $new && rm -f /tmp/arsantuptakip-bot-deploy.tar.gz",
    "bash $new/deploy/test-release.sh | tail -n 1",
    "cd $new/app && npm ci --omit=dev --no-audit --no-fund --loglevel=error",
    "sudo bash $new/deploy/install-bot.sh $new",
    "rm -rf $new"
) -join " && ")
if ($LASTEXITCODE -ne 0) { throw "Sunucu kurulumu başarısız." }
