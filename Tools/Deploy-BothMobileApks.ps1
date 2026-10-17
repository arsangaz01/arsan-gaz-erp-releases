# Yalniz Android: ERP + ArsanTupTakip APK'larini ayni turda yayinlar.
# iOS bu betige dahil degil; ayri hat (Tools\Deploy-IosMobile.ps1 / Mac + Apple Developer).
#   powershell -NoProfile -ExecutionPolicy Bypass -File Tools\Deploy-BothMobileApks.ps1
param(
    [switch]$SkipBuild,
    [switch]$NoServer,
    [switch]$AllowPartial  # acil durum; varsayilan: biri fail ederse dur
)

$ErrorActionPreference = "Stop"
$here = $PSScriptRoot

$erpArgs = @()
$tupArgs = @()
if ($SkipBuild) { $erpArgs += "-SkipBuild"; $tupArgs += "-SkipBuild" }
if ($NoServer) { $erpArgs += "-NoServer"; $tupArgs += "-NoServer" }

Write-Host "=== Android ortak yayin (iOS ayri) ==="

Write-Host "=== 1/2 Arsan Gaz ERP Android APK ==="
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $here "Deploy-MobileApk.ps1") @erpArgs
if ($LASTEXITCODE -ne 0) {
    if ($AllowPartial) {
        Write-Warning "ERP APK basarisiz (exit $LASTEXITCODE); AllowPartial ile devam."
    }
    else {
        throw "ERP APK yayini basarisiz (exit $LASTEXITCODE). TupTakip yayini iptal."
    }
}

Write-Host "=== 2/2 ArsanTupTakip Android APK ==="
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $here "Deploy-TupTakipMobileApk.ps1") @tupArgs
if ($LASTEXITCODE -ne 0) {
    throw "TupTakip APK yayini basarisiz (exit $LASTEXITCODE). ERP yuklendiyse kanallar gecici farkli olabilir; hemen duzeltin."
}

Write-Host "=== Android ortak yayin tamam ==="
Write-Host "ERP:      https://arsantuptakip.com/indir"
Write-Host "TupTakip: https://arsantuptakip.com/tuptakip/indir"
Write-Host "iOS:      bu betikte yok (ayri hat)"
