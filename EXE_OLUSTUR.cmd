@echo off
chcp 65001 >nul
setlocal EnableExtensions
cd /d "%~dp0"

set "PUBLISH_ROOT=%CD%\publish"
set "CURRENT_DIR=%PUBLISH_ROOT%\current"
set "STAGE_DIR=%PUBLISH_ROOT%\staging-%RANDOM%-%RANDOM%"
set "BACKUP_DIR=%PUBLISH_ROOT%\backup-%RANDOM%-%RANDOM%"

mkdir "%STAGE_DIR%"
if errorlevel 1 goto hata

dotnet restore .\ArsanGazERP.csproj
if errorlevel 1 goto hata
dotnet build .\ArsanGazERP.csproj -c Release --no-restore
if errorlevel 1 goto hata
dotnet publish .\ArsanGazERP.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --no-restore -o "%STAGE_DIR%"
if errorlevel 1 goto hata
dotnet publish .\Tools\AgentWatcher\ArsanGazERP.AgentWatcher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o "%STAGE_DIR%\AgentWatcher"
if errorlevel 1 goto hata

if not exist "%STAGE_DIR%\ArsanGazERP.exe" goto yayin_hatasi
if not exist "%STAGE_DIR%\AgentWatcher\ArsanGazERP.AgentWatcher.exe" goto yayin_hatasi

powershell.exe -NoProfile -Command "$p = Get-Process | Where-Object { $_.Path -like '%CURRENT_DIR%\*' }; $p | ForEach-Object { $null = $_.CloseMainWindow() }; if ($p) { Start-Sleep -Seconds 8 }; Get-Process | Where-Object { $_.Path -like '%CURRENT_DIR%\*' } | Stop-Process -Force; Start-Sleep -Seconds 2"

if exist "%CURRENT_DIR%" (
    move "%CURRENT_DIR%" "%BACKUP_DIR%" >nul
    if errorlevel 1 goto yayin_hatasi
)

move "%STAGE_DIR%" "%CURRENT_DIR%" >nul
if errorlevel 1 (
    if exist "%BACKUP_DIR%" move "%BACKUP_DIR%" "%CURRENT_DIR%" >nul
    goto yayin_hatasi
)

copy /y "%CURRENT_DIR%\ArsanGazERP.exe" "%PUBLISH_ROOT%\ArsanGazERP.exe" >nul
if errorlevel 1 echo UYARI: Uyumluluk kopyasi guncellenemedi.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\Tools\Set-DesktopShortcut.ps1" -TargetPath "%CURRENT_DIR%\ArsanGazERP.exe" -AgentWatcherTargetPath "%CURRENT_DIR%\AgentWatcher\ArsanGazERP.AgentWatcher.exe"
if errorlevel 1 goto kisayol_hatasi
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\Tools\Update-AgentWatcherStartup.ps1" -ErpExecutablePath "%CURRENT_DIR%\ArsanGazERP.exe" -WatcherExecutablePath "%CURRENT_DIR%\AgentWatcher\ArsanGazERP.AgentWatcher.exe"
if errorlevel 1 goto kisayol_hatasi

start "" /D "%CURRENT_DIR%\AgentWatcher" "%CURRENT_DIR%\AgentWatcher\ArsanGazERP.AgentWatcher.exe" --background

echo.
echo ERP ve AgentWatcher yayinlandi: %CURRENT_DIR%
echo Masaustu kisayollari guncellendi: Arsan Gaz ERP ve Arsan Gaz AgentWatcher
explorer "%CURRENT_DIR%"
pause
exit /b 0

:kisayol_hatasi
echo UYARI: Uygulama yayinlandi ancak masaustu kisayolu guncellenemedi.
echo Hata ayrintisi icin yukaridaki PowerShell mesajini kontrol edin.
pause
exit /b 1

:yayin_hatasi
echo Yayin tamamlanamadi; onceki publish\current korunmaya calisildi.
echo Gecici yayin dizini: %STAGE_DIR%
pause
exit /b 1

:hata
echo Derleme veya yayin basarisiz oldu; mevcut uygulama dizini degistirilmedi.
echo Gecici yayin dizini: %STAGE_DIR%
pause
exit /b 1
