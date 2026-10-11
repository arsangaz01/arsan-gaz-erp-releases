# Publishes MobileApi + ServerAgent for linux-arm64 and installs them on the Oracle server, keeping the previous
# build as /opt/arsangaz/<name>.bak-<time>. Restarts only services that are already enabled.
param(
    [ValidateSet('all', 'mobileapi', 'serveragent')][string]$Component = 'all',
    [string]$ServerHost = 'arsangaz@144.24.189.205',
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$key = Join-Path $env:USERPROFILE '.ssh\arsangaz_hetzner'
$out = Join-Path $repo 'publish\linux'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$components = if ($Component -eq 'all') { @('mobileapi', 'serveragent') } else { @($Component) }

function Invoke-Remote([string]$Command) {
    ssh -i $key -o BatchMode=yes $ServerHost $Command
    if ($LASTEXITCODE -ne 0) { throw "Sunucu komutu basarisiz ($LASTEXITCODE): $Command" }
}

foreach ($name in $components) {
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
