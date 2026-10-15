# Builds the Android 4.4 handheld APK with the plain SDK tools (no Gradle):
#   aapt2 (resources + manifest) -> javac -> d8 -> zipalign -> apksigner (v1 + v2; Android 4.4 needs v1).
# Output: bin\ArsanTupTakip-ElTerminali.apk and its .meta.json for the server's /indir/el-terminali.
# The signing key lives outside the repo; it is created on the first build. Keep a backup: losing it means every
# handheld must uninstall the app before it can install a newer build.
param(
    [string]$KeyFolder = (Join-Path $env:LOCALAPPDATA 'ArsanGazERP\Secrets\elterminali-signing')
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$sdk = Join-Path $env:LOCALAPPDATA 'Android\sdk'
$buildTools = (Get-ChildItem (Join-Path $sdk 'build-tools') | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1).FullName
$platform = Join-Path ((Get-ChildItem (Join-Path $sdk 'platforms') | Sort-Object Name -Descending | Select-Object -First 1).FullName) 'android.jar'
$env:JAVA_HOME = Join-Path $env:LOCALAPPDATA 'Android\jdk17'
$env:PATH = (Join-Path $env:JAVA_HOME 'bin') + ';' + $env:PATH

function Run([string]$exe, [string[]]$arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$exe failed ($LASTEXITCODE)"
    }
}

$out = Join-Path $here 'bin'
if (Test-Path $out) {
    Remove-Item $out -Recurse -Force
}

$classes = Join-Path $out 'classes'
$dex = Join-Path $out 'dex'
New-Item -ItemType Directory $classes, $dex | Out-Null

[xml]$manifest = Get-Content (Join-Path $here 'AndroidManifest.xml') -Raw -Encoding UTF8
$ns = 'http://schemas.android.com/apk/res/android'
$versionName = $manifest.manifest.GetAttribute('versionName', $ns)
$versionCode = [int]$manifest.manifest.GetAttribute('versionCode', $ns)

Run (Join-Path $buildTools 'aapt2.exe') @('compile', '--dir', (Join-Path $here 'res'), '-o', (Join-Path $out 'res.zip'))
$unsigned = Join-Path $out 'unsigned.apk'
Run (Join-Path $buildTools 'aapt2.exe') @('link', '-o', $unsigned, '-I', $platform, '--manifest', (Join-Path $here 'AndroidManifest.xml'),
    '--min-sdk-version', '19', '--target-sdk-version', '23', '-A', (Join-Path $here 'assets'), (Join-Path $out 'res.zip'))

$sources = Get-ChildItem (Join-Path $here 'src') -Recurse -Filter *.java | ForEach-Object FullName
# android.jar's java.lang.invoke stubs cannot compile lambdas; Java types come from the JDK 8 API, d8 checks them against android.jar.
Run 'javac' (@('-encoding', 'UTF-8', '--release', '8', '-classpath', $platform, '-d', $classes) + $sources)
$classJar = Join-Path $out 'classes.jar'
Run 'jar' @('cf', $classJar, '-C', $classes, '.')
Run (Join-Path $buildTools 'd8.bat') @('--release', '--min-api', '19', '--lib', $platform, '--output', $dex, $classJar)

Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::Open($unsigned, 'Update')
try {
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $dex 'classes.dex'), 'classes.dex') | Out-Null
} finally {
    $zip.Dispose()
}

$aligned = Join-Path $out 'aligned.apk'
Run (Join-Path $buildTools 'zipalign.exe') @('-f', '-p', '4', $unsigned, $aligned)

$keyStore = Join-Path $KeyFolder 'elterminali.jks'
$passFile = Join-Path $KeyFolder 'password.txt'
if (-not (Test-Path $keyStore)) {
    New-Item -ItemType Directory $KeyFolder -Force | Out-Null
    $bytes = New-Object byte[] 24
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    [IO.File]::WriteAllText($passFile, [Convert]::ToBase64String($bytes).Replace('+', 'x').Replace('/', 'y'))
    $password = Get-Content $passFile -Raw
    Run 'keytool' @('-genkeypair', '-keystore', $keyStore, '-storetype', 'PKCS12', '-alias', 'elterminali', '-keyalg', 'RSA', '-keysize', '2048',
        '-validity', '10000', '-dname', 'CN=ArsanTupTakip El Terminali, O=Arsan Gaz, C=TR', '-storepass', $password, '-keypass', $password)
    Write-Host "Yeni imza anahtarı oluşturuldu: $KeyFolder (yedekleyin)"
}

$apk = Join-Path $out 'ArsanTupTakip-ElTerminali.apk'
Run (Join-Path $buildTools 'apksigner.bat') @('sign', '--ks', $keyStore, '--ks-key-alias', 'elterminali', '--ks-pass', "file:$passFile",
    '--min-sdk-version', '19', '--v1-signing-enabled', 'true', '--v2-signing-enabled', 'true', '--out', $apk, $aligned)
Run (Join-Path $buildTools 'apksigner.bat') @('verify', '--min-sdk-version', '19', '--verbose', $apk)
Remove-Item $unsigned, $aligned, (Join-Path $out 'res.zip')
[IO.File]::WriteAllText("$apk.meta.json", (@{ versionName = $versionName; versionCode = $versionCode } | ConvertTo-Json -Compress), (New-Object Text.UTF8Encoding $false))
Write-Host "APK: $apk ($versionName / $versionCode, $((Get-Item $apk).Length) bayt)"
