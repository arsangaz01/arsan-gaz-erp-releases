# Publishes MobileApi + ServerAgent for linux-arm64 and installs them on the Oracle server, keeping the previous
# build as /opt/arsangaz/<name>.bak-<time>. Restarts only services that are already enabled.
# -Component cursoragent installs the server Cursor agent (Tools/CursorTelegramBridge/server-bridge.js) to
# /opt/arsangaz-cursoragent/app, the cursoragent user/sudoers/service (install-cursoragent.sh) and the queue scripts.
param(
    [ValidateSet('all', 'mobileapi', 'serveragent', 'cursoragent')][string]$Component = 'all',
    [string]$ServerHost = 'arsangaz@144.24.189.205',
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$key = Join-Path $env:USERPROFILE '.ssh\arsangaz_hetzner'
$out = Join-Path $repo 'publish\linux'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$components = if ($Component -eq 'all') { @('mobileapi', 'serveragent', 'cursoragent') } else { @($Component) }

function Invoke-Remote([string]$Command) {
    ssh -i $key -o BatchMode=yes $ServerHost $Command
    if ($LASTEXITCODE -ne 0) { throw "Sunucu komutu basarisiz ($LASTEXITCODE): $Command" }
}

function Install-CursorAgent {
    $stage = Join-Path $out 'cursoragent'
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Force -Path (Join-Path $stage 'app'), (Join-Path $stage 'deploy') | Out-Null
    $bridge = Join-Path $repo 'Tools\CursorTelegramBridge'
    foreach ($file in 'server-bridge.js', 'server-inbox.js', 'bridge.js', 'erp-tools.js', 'jarvis-memory.js', 'pii-mask.js', 'package.json', 'package-lock.json') {
        Copy-Item (Join-Path $bridge $file) (Join-Path $stage 'app')
    }
    foreach ($file in 'cursor-inbox.sh', 'install-cursoragent.sh', 'arsangaz-cursoragent.service', 'inbox-sync.sh', 'set-agent-value.sh') {
        Copy-Item (Join-Path $PSScriptRoot $file) (Join-Path $stage 'deploy')
    }

    $archive = Join-Path $env:TEMP "arsangaz-cursoragent-$stamp.tar.gz"
    tar -czf $archive -C $stage .
    if ($LASTEXITCODE -ne 0) { throw 'cursoragent arsivlenemedi.' }
    scp -i $key -q $archive "${ServerHost}:/tmp/arsangaz-cursoragent.tar.gz"
    if ($LASTEXITCODE -ne 0) { throw 'cursoragent yuklenemedi.' }
    Remove-Item $archive

    $new = '/tmp/arsangaz-cursoragent-new'
    Invoke-Remote (@(
        "set -e",
        "rm -rf $new && mkdir -p $new && tar -xzf /tmp/arsangaz-cursoragent.tar.gz -C $new && rm -f /tmp/arsangaz-cursoragent.tar.gz",
        "sed -i 's/\r$//' $new/deploy/*",
        "cd $new/app && npm ci --omit=dev --no-audit --no-fund --loglevel=error",
        "sudo bash $new/deploy/install-cursoragent.sh",
        "sudo install -m 0755 -o root -g root $new/deploy/inbox-sync.sh /opt/arsangaz/bin/inbox-sync.sh",
        "sudo install -m 0700 -o root -g root $new/deploy/set-agent-value.sh /opt/arsangaz/bin/set-agent-value.sh",
        "if [ -d /opt/arsangaz-cursoragent/app ]; then sudo mv /opt/arsangaz-cursoragent/app /opt/arsangaz-cursoragent/app.bak-$stamp; fi",
        "sudo mv $new/app /opt/arsangaz-cursoragent/app && rm -rf $new",
        "sudo chown -R root:root /opt/arsangaz-cursoragent/app && sudo chmod -R u=rwX,go=rX /opt/arsangaz-cursoragent/app",
        "sudo systemctl enable arsangaz-cursoragent >/dev/null 2>&1 && sudo systemctl restart arsangaz-cursoragent",
        "ls -dt /opt/arsangaz-cursoragent/app.bak-* 2>/dev/null | tail -n +4 | xargs -r sudo rm -rf"
    ) -join ' && ')
    Write-Host 'cursoragent kuruldu (servis: arsangaz-cursoragent).'
}

foreach ($name in $components) {
    if ($name -eq 'cursoragent') { Install-CursorAgent; continue }
    $target = Join-Path $out $name
    if (-not $SkipPublish) {
        if (Test-Path $target) { Remove-Item $target -Recurse -Force }
        if ($name -eq 'mobileapi') {
            dotnet publish (Join-Path $repo 'Tools\MobileApi\ArsanGazERP.MobileApi.csproj') -p:ArsanLinux=true -c Release -o $target -nologo -v q
        }
        else {
            dotnet publish (Join-Path $repo 'Tools\ServerAgent\ArsanGazERP.ServerAgent.csproj') -c Release -r linux-arm64 --self-contained false -o $target -nologo -v q
        }
        if ($LASTEXITCODE -ne 0) { throw "$name derlenemedi." }
    }

    $archive = Join-Path $env:TEMP "arsangaz-$name-$stamp.tar.gz"
    tar -czf $archive -C $target .
    if ($LASTEXITCODE -ne 0) { throw "$name arsivlenemedi." }
    scp -i $key -q $archive "${ServerHost}:/tmp/arsangaz-$name.tar.gz"
    if ($LASTEXITCODE -ne 0) { throw "$name yuklenemedi." }
    Remove-Item $archive

    $unit = if ($name -eq 'mobileapi') { 'arsangaz-mobileapi-prod' } else { 'arsangaz-serveragent' }
    Invoke-Remote (@(
        "set -e",
        "sudo rm -rf /tmp/arsangaz-$name-new && sudo mkdir -p /tmp/arsangaz-$name-new",
        "sudo tar -xzf /tmp/arsangaz-$name.tar.gz -C /tmp/arsangaz-$name-new && rm -f /tmp/arsangaz-$name.tar.gz",
        "if [ -d /opt/arsangaz/$name ]; then sudo mv /opt/arsangaz/$name /opt/arsangaz/$name.bak-$stamp; fi",
        "sudo mv /tmp/arsangaz-$name-new /opt/arsangaz/$name",
        "sudo chown -R root:root /opt/arsangaz/$name && sudo chmod -R u=rwX,go=rX /opt/arsangaz/$name",
        "if systemctl is-enabled --quiet $unit 2>/dev/null; then sudo systemctl restart $unit; fi",
        "ls -dt /opt/arsangaz/$name.bak-* 2>/dev/null | tail -n +4 | xargs -r sudo rm -rf"
    ) -join ' && ')
    Write-Host "$name kuruldu (onceki surum: /opt/arsangaz/$name.bak-$stamp)."
}
