# Release APK'yı MobileApi indirme klasörüne kopyalar; sürüm meta dosyasını csproj'dan yazar.
param(
    [string]$ApkPath = "",
    [switch]$SkipBuild,
    [switch]$NoServer,
    [string]$ServerHost = "arsangaz@144.24.189.205",
    [string]$ServerRoot = "/var/lib/arsangaz-data/prod/ArsanGazERP/MobileApi"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$csproj = Join-Path $repo "Tools\MobileApp\ArsanGazMobile.csproj"
$destRoot = Join-Path $env:LOCALAPPDATA "ArsanGazERP\MobileApi"
$destApk = Join-Path $destRoot "ArsanGazERP-Mobil.apk"
$destMeta = "$destApk.meta.json"

function Get-CsprojVersion {
    [xml]$xml = Get-Content $csproj
    $display = $xml.Project.PropertyGroup.ApplicationDisplayVersion | Select-Object -First 1
    $build = $xml.Project.PropertyGroup.ApplicationVersion | Select-Object -First 1
    if (-not $display -or -not $build) { throw "csproj sürüm alanları okunamadı." }
    return @{ Name = [string]$display; Code = [int]$build }
}

$jdk = Join-Path $env:LOCALAPPDATA "Android\jdk17"
$sdk = Join-Path $env:LOCALAPPDATA "Android\sdk"
if (-not $SkipBuild) {
    if (-not (Test-Path (Join-Path $jdk "bin\jar.exe"))) { throw "JDK bulunamadi: $jdk" }
    $env:JAVA_HOME = $jdk
    dotnet publish (Join-Path $repo "Tools\MobileApp\ArsanGazMobile.csproj") -c Release -f net10.0-android `
        -p:AndroidPackageFormat=apk -p:RunAOTCompilation=false -r android-arm64 `
        -p:AndroidSdkDirectory=$sdk -p:JavaSdkDirectory=$jdk
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish basarisiz (exit $LASTEXITCODE)." }
}

if (-not $ApkPath) {
    $apk = Get-ChildItem -Path (Join-Path $repo "Tools\MobileApp\bin\Release\net10.0-android\android-arm64") -Filter "*Signed.apk" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $apk) {
        $apk = Get-ChildItem -Path (Join-Path $repo "Tools\MobileApp\bin\Release\net10.0-android") -Recurse -Filter "*Signed.apk" -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
    }
    if (-not $apk) {
        $apk = Get-ChildItem -Path (Join-Path $repo "Tools\MobileApp\bin\Release\net10.0-android") -Recurse -Filter "*.apk" -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -notmatch "-Signed\.apk$" } |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
    }
    if (-not $apk) { throw "APK bulunamadı; önce publish çalıştırın." }
    $ApkPath = $apk.FullName
}

if (-not (Test-Path $ApkPath)) { throw "APK yok: $ApkPath" }

New-Item -ItemType Directory -Force -Path $destRoot | Out-Null
Copy-Item -Path $ApkPath -Destination $destApk -Force

$ver = Get-CsprojVersion
@{ versionName = $ver.Name; versionCode = $ver.Code } | ConvertTo-Json | Set-Content -Path $destMeta -Encoding UTF8

Write-Host "APK kopyalandi: $destApk"
Write-Host "Surum: $($ver.Name) build $($ver.Code)"

# arsantuptakip.com/indir sunucudaki MobileApi'den yayinlanir; APK oraya da yuklenir.
if (-not $NoServer) {
    $key = Join-Path $env:USERPROFILE ".ssh\arsangaz_hetzner"
    $remote = "$ServerHost`:$ServerRoot"
    scp -i $key -q $destApk "$remote/ArsanGazERP-Mobil.apk.upload"
    if ($LASTEXITCODE -ne 0) { throw "APK sunucuya yuklenemedi." }
    scp -i $key -q $destMeta "$remote/ArsanGazERP-Mobil.apk.meta.json.upload"
    if ($LASTEXITCODE -ne 0) { throw "APK meta sunucuya yuklenemedi." }
    ssh -i $key $ServerHost "cd '$ServerRoot' && chmod 600 *.upload && mv -f ArsanGazERP-Mobil.apk.upload ArsanGazERP-Mobil.apk && mv -f ArsanGazERP-Mobil.apk.meta.json.upload ArsanGazERP-Mobil.apk.meta.json"
    if ($LASTEXITCODE -ne 0) { throw "Sunucuda APK yerine konamadi." }
    Write-Host "APK sunucuya yuklendi: $ServerHost ($ServerRoot)"
}
