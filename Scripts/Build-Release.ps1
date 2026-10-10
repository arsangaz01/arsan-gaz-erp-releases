param([string]$Version='7.9.3')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
$art=Join-Path $root 'artifacts'
$publishRoot=Join-Path $root 'publish'
$current=Join-Path $publishRoot 'current'
$releaseId=[Guid]::NewGuid().ToString('N')
$stage=Join-Path $publishRoot "release-staging-$releaseId"
$currentBackup=Join-Path $publishRoot "release-previous-$releaseId"
$artApp=Join-Path $art 'app'
$artAppStage=Join-Path $art "app-staging-$releaseId"
$artAppBackup=Join-Path $art "app-previous-$releaseId"

function Copy-DirectoryContents {
    param([string]$Source,[string]$Destination)

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Get-ChildItem -LiteralPath $Source -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $Destination -Recurse -Force
    }
}

function Promote-Directory {
    param([string]$Stage,[string]$Destination,[string]$Backup)

    if (Test-Path -LiteralPath $Destination) {
        Move-Item -LiteralPath $Destination -Destination $Backup
    }

    try {
        Move-Item -LiteralPath $Stage -Destination $Destination
    }
    catch {
        if ((Test-Path -LiteralPath $Backup) -and -not (Test-Path -LiteralPath $Destination)) {
            Move-Item -LiteralPath $Backup -Destination $Destination
        }
        throw
    }
}

New-Item -ItemType Directory -Path $publishRoot,$art -Force | Out-Null
New-Item -ItemType Directory -Path $stage -Force | Out-Null

dotnet restore (Join-Path $root 'ArsanGazERP.csproj') --force --no-cache
if ($LASTEXITCODE -ne 0) { throw 'ERP bağımlılıkları geri yüklenemedi.' }
dotnet build (Join-Path $root 'ArsanGazERP.csproj') -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'ERP Release derlemesi başarısız oldu.' }
dotnet publish (Join-Path $root 'ArsanGazERP.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --no-restore -o $stage
if ($LASTEXITCODE -ne 0) { throw 'ERP yayımlanamadı.' }
dotnet publish (Join-Path $root 'Tools\AgentWatcher\ArsanGazERP.AgentWatcher.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true --no-restore -o (Join-Path $stage 'AgentWatcher')
if ($LASTEXITCODE -ne 0) { throw 'AgentWatcher yayımlanamadı.' }

if (-not (Test-Path -LiteralPath (Join-Path $stage 'ArsanGazERP.exe'))) { throw 'ERP çalıştırılabilir dosyası yayımlanmadı.' }
if (-not (Test-Path -LiteralPath (Join-Path $stage 'AgentWatcher\ArsanGazERP.AgentWatcher.exe'))) { throw 'AgentWatcher çalıştırılabilir dosyası yayımlanmadı.' }

Copy-DirectoryContents -Source $stage -Destination $artAppStage
Promote-Directory -Stage $stage -Destination $current -Backup $currentBackup
Promote-Directory -Stage $artAppStage -Destination $artApp -Backup $artAppBackup

& (Join-Path $root 'Tools\Set-DesktopShortcut.ps1') `
    -TargetPath (Join-Path $current 'ArsanGazERP.exe') `
    -AgentWatcherTargetPath (Join-Path $current 'AgentWatcher\ArsanGazERP.AgentWatcher.exe')
if ($LASTEXITCODE -ne 0) { throw 'Masaüstü kısayolları güncellenemedi.' }
& (Join-Path $root 'Tools\Update-AgentWatcherStartup.ps1') `
    -ErpExecutablePath (Join-Path $current 'ArsanGazERP.exe') `
    -WatcherExecutablePath (Join-Path $current 'AgentWatcher\ArsanGazERP.AgentWatcher.exe')
if ($LASTEXITCODE -ne 0) { throw 'Windows başlangıç yolları güncellenemedi.' }
$candidates=@((Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),(Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),(Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'))
$iscc=$candidates|Where-Object{Test-Path $_}|Select-Object -First 1
if([string]::IsNullOrWhiteSpace($iscc)){winget install --id JRSoftware.InnoSetup -e --silent --accept-package-agreements --accept-source-agreements;for($i=0;$i-lt 30-and [string]::IsNullOrWhiteSpace($iscc);$i++){Start-Sleep 1;$iscc=$candidates|Where-Object{Test-Path $_}|Select-Object -First 1}}
if([string]::IsNullOrWhiteSpace($iscc)){throw 'ISCC.exe bulunamadı.'}
& $iscc (Join-Path $root 'Installer\ArsanGazERP.iss')
if($LASTEXITCODE-ne 0){throw 'Setup.exe oluşturulamadı.'}