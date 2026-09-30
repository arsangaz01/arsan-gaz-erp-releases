param([string]$Version='7.9.3')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
$art=Join-Path $root 'artifacts'
Remove-Item $art -Recurse -Force -ErrorAction SilentlyContinue
dotnet restore (Join-Path $root 'ArsanGazERP.csproj') --force --no-cache
dotnet build (Join-Path $root 'ArsanGazERP.csproj') -c Release --no-restore
dotnet publish (Join-Path $root 'ArsanGazERP.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o (Join-Path $art 'app')
dotnet publish (Join-Path $root 'Tools\AgentWatcher\ArsanGazERP.AgentWatcher.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o (Join-Path $art 'app\AgentWatcher')
$candidates=@((Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),(Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),(Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'))
$iscc=$candidates|Where-Object{Test-Path $_}|Select-Object -First 1
if([string]::IsNullOrWhiteSpace($iscc)){winget install --id JRSoftware.InnoSetup -e --silent --accept-package-agreements --accept-source-agreements;for($i=0;$i-lt 30-and [string]::IsNullOrWhiteSpace($iscc);$i++){Start-Sleep 1;$iscc=$candidates|Where-Object{Test-Path $_}|Select-Object -First 1}}
if([string]::IsNullOrWhiteSpace($iscc)){throw 'ISCC.exe bulunamadı.'}
& $iscc (Join-Path $root 'Installer\ArsanGazERP.iss')
if($LASTEXITCODE-ne 0){throw 'Setup.exe oluşturulamadı.'}