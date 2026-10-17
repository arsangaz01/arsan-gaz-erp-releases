# iOS (IPA) ayri yayin hatti — Android Both betiginden bagimsiz.
# Bu PC Windows; imzali IPA icin Mac + Xcode + Apple Developer sart.
#   powershell -NoProfile -ExecutionPolicy Bypass -File Tools\Deploy-IosMobile.ps1
#   powershell ... -File Tools\Deploy-IosMobile.ps1 -InstallWorkload
param(
    [switch]$SkipBuild,
    [switch]$NoServer,
    [switch]$InstallWorkload,
    [switch]$TryBuild
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$csproj = Join-Path $repo "Tools\MobileApp\ArsanGazMobile.csproj"

Write-Host "=== iOS ayri hat (Android APK'lardan bagimsiz) ==="
Write-Host "OS: $([Environment]::OSVersion.VersionString)"
Write-Host "FREE_GB: $([math]::Round((Get-PSDrive C).Free/1GB,2))"

$onMac = [bool]$IsMacOS
Write-Host "Mac host: $onMac"

Write-Host ""
Write-Host "--- Workload ---"
$wl = & dotnet workload list 2>&1 | Out-String
Write-Host $wl
$hasIos = $wl -match 'maui-ios'
Write-Host "maui-ios yuklu: $hasIos"

if ($InstallWorkload) {
    Write-Host "dotnet workload install maui-ios ..."
    & dotnet workload install maui-ios --skip-manifest-update
    if ($LASTEXITCODE -ne 0) { throw "maui-ios workload kurulamadi (exit $LASTEXITCODE)." }
    $hasIos = $true
}

Write-Host ""
Write-Host "--- Proje ---"
Write-Host "csproj: $csproj"
Write-Host "Platforms/iOS stub + Info.plist hazir."
Write-Host "Mac derleme ornegi:"
Write-Host "  dotnet publish `"$csproj`" -c Release -f net10.0-ios -p:BuildIos=true"

Write-Host ""
Write-Host "--- Engeller (IPA icin) ---"
$blockers = [System.Collections.Generic.List[string]]::new()
if (-not $onMac) {
    $blockers.Add("Bu PC Windows; Xcode yok. Mac (fiziksel veya bulut) gerekli.")
}
if (-not $hasIos) {
    $blockers.Add("dotnet maui-ios workload yok. -InstallWorkload deneyin; IPA icin yine Mac lazim.")
}
$blockers.Add("Apple Developer hesabi + sertifika + provisioning bu betikte yok.")
$blockers.Add("TestFlight veya ayri iOS indir linki sunucuda henuz yok.")
foreach ($b in $blockers) { Write-Host (" * " + $b) }

if ($TryBuild) {
    if (-not $onMac) {
        throw "TryBuild: Windows'ta net10.0-ios IPA derlenemez. Mac'te ayni betigi calistirin."
    }
    if (-not $SkipBuild) {
        & dotnet publish $csproj -c Release -f net10.0-ios -p:BuildIos=true
        if ($LASTEXITCODE -ne 0) { throw "iOS publish basarisiz (exit $LASTEXITCODE)." }
    }
    if (-not $NoServer) {
        throw "iOS sunucu yayini henuz yok (TestFlight / IPA host kurulunca eklenecek)."
    }
}

Write-Host ""
Write-Host "--- Mac'te IPA (onay sonrasi adimlar) ---"
Write-Host "1) Xcode + Apple ID (Developer) acik olsun"
Write-Host "2) Bundle ID: com.arsangaz.erpmobile (App Store Connect'te ayni)"
Write-Host "3) Repo'yu Mac'e cekin, sonra:"
Write-Host "   dotnet workload install maui-ios"
Write-Host "   dotnet publish Tools/MobileApp/ArsanGazMobile.csproj -c Release -f net10.0-ios -p:BuildIos=true"
Write-Host "4) Cikan .ipa -> TestFlight veya ayri indir linki (Android /indir'den bagimsiz)"
Write-Host "Windows'tan Pair to Mac: Visual Studio + Mac IP/kullanici; bu ajan SSH/Mac IP bekler."

Write-Host ""
Write-Host "=== Durum: hazirlik; IPA uretilmedi / yayinlanmadi ==="
Write-Host "Android: Tools\Deploy-BothMobileApks.ps1"
Write-Host "Sonraki sizden: Mac erisimi (IP/SSH) veya Mac'te yukaridaki publish."
exit 0
