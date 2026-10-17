# ArsanTüpTakip imzalı APK'yı derleyip /tuptakip/indir veri klasörüne koyar.
# Prod yayınında tek başına çağırma; Tools\Deploy-BothMobileApks.ps1 kullan.
param(
    [string]$ApkPath = "",
    [switch]$SkipBuild,
    [switch]$NoServer,
    [string]$ServerHost = "arsangaz@144.24.189.205",
    [string]$ServerDataDir = "/var/lib/arsantuptakip"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$csproj = Join-Path $repo "Tools\ArsanTupTakip\mobile\ArsanTupTakip.Mobile\ArsanTupTakip.Mobile.csproj"
$destRoot = Join-Path $env:LOCALAPPDATA "ArsanGazERP\ArsanTupTakip"
$destApk = Join-Path $destRoot "ArsanTupTakip-Mobil.apk"
$destMeta = "$destApk.meta.json"
$apkName = "ArsanTupTakip-Mobil.apk"

function Get-CsprojVersion {
    [xml]$xml = Get-Content $csproj
    $display = $xml.Project.PropertyGroup.ApplicationDisplayVersion | Select-Object -First 1
    $build = $xml.Project.PropertyGroup.ApplicationVersion | Select-Object -First 1
    if (-not $display -or -not $build) { throw "TupTakip csproj sürüm alanları okunamadı." }
    return @{ Name = [string]$display; Code = [int]$build }
}

$jdk = Join-Path $env:LOCALAPPDATA "Android\jdk17"
$sdk = Join-Path $env:LOCALAPPDATA "Android\sdk"
if (-not $SkipBuild) {
    if (-not (Test-Path (Join-Path $jdk "bin\jar.exe"))) { throw "JDK bulunamadi: $jdk" }
    $env:JAVA_HOME = $jdk
    dotnet publish $csproj -c Release -f net10.0-android `
        -p:AndroidPackageFormat=apk -p:RunAOTCompilation=false -r android-arm64 `
        -p:AndroidSdkDirectory=$sdk -p:JavaSdkDirectory=$jdk
    if ($LASTEXITCODE -ne 0) { throw "TupTakip dotnet publish basarisiz (exit $LASTEXITCODE)." }
}

if (-not $ApkPath) {
    $binRoot = Join-Path $repo "Tools\ArsanTupTakip\mobile\ArsanTupTakip.Mobile\bin\Release\net10.0-android"
    $apk = Get-ChildItem -Path (Join-Path $binRoot "android-arm64") -Filter "*Signed.apk" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $apk) {
        $apk = Get-ChildItem -Path $binRoot -Recurse -Filter "*Signed.apk" -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
    }
    if (-not $apk) {
        $apk = Get-ChildItem -Path $binRoot -Recurse -Filter "*.apk" -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -notmatch "-Signed\.apk$" } |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
    }
    if (-not $apk) { throw "TupTakip APK bulunamadı; önce publish çalıştırın." }
    $ApkPath = $apk.FullName
}

if (-not (Test-Path $ApkPath)) { throw "APK yok: $ApkPath" }

New-Item -ItemType Directory -Force -Path $destRoot | Out-Null
Copy-Item -Path $ApkPath -Destination $destApk -Force

$ver = Get-CsprojVersion
@{ versionName = $ver.Name; versionCode = $ver.Code; package = "com.arsantuptakip.mobil" } |
    ConvertTo-Json | Set-Content -Path $destMeta -Encoding UTF8

Write-Host "TupTakip APK kopyalandi: $destApk"
Write-Host "Surum: $($ver.Name) build $($ver.Code)"

if (-not $NoServer) {
    $key = Join-Path $env:USERPROFILE ".ssh\arsangaz_hetzner"
    $remoteTmp = "/tmp/$apkName"
    scp -i $key -q $destApk "${ServerHost}:${remoteTmp}.upload"
    if ($LASTEXITCODE -ne 0) { throw "TupTakip APK sunucuya yuklenemedi." }
    scp -i $key -q $destMeta "${ServerHost}:${remoteTmp}.meta.json.upload"
    if ($LASTEXITCODE -ne 0) { throw "TupTakip APK meta sunucuya yuklenemedi." }
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    # SSH tek satır + LF: Windows CRLF bash'te "set: - : invalid option" üretir.
    $remote = @(
        "set -e",
        "sudo install -d -m 0750 -o arsantuptakip -g arsantuptakip '$ServerDataDir'",
        "if [ -f '$ServerDataDir/$apkName' ]; then sudo cp -p '$ServerDataDir/$apkName' '$ServerDataDir/$apkName.bak-$stamp'; fi",
        "sudo install -m 0640 -o arsantuptakip -g arsantuptakip '${remoteTmp}.upload' '$ServerDataDir/$apkName.upload'",
        "sudo install -m 0640 -o arsantuptakip -g arsantuptakip '${remoteTmp}.meta.json.upload' '$ServerDataDir/$apkName.meta.json.upload'",
        "sudo mv -f '$ServerDataDir/$apkName.upload' '$ServerDataDir/$apkName'",
        "sudo mv -f '$ServerDataDir/$apkName.meta.json.upload' '$ServerDataDir/$apkName.meta.json'",
        "sudo chown arsantuptakip:arsantuptakip '$ServerDataDir/$apkName' '$ServerDataDir/$apkName.meta.json'",
        "rm -f '${remoteTmp}.upload' '${remoteTmp}.meta.json.upload'"
    ) -join "`n"
    ssh -i $key $ServerHost $remote
    if ($LASTEXITCODE -ne 0) { throw "Sunucuda TupTakip APK yerine konamadi." }
    Write-Host "TupTakip APK sunucuya yuklendi: $ServerHost ($ServerDataDir) -> /tuptakip/indir"
}
