@echo off
chcp 65001 >nul
cd /d "%~dp0"
rem .NET 10 SDK validation: build and publish the net10.0-windows project for win-x64.
if exist publish_staging rmdir /s /q publish_staging
dotnet restore
if errorlevel 1 goto hata
dotnet build -c Release --no-restore
if errorlevel 1 goto hata
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --no-restore -o publish_staging
if errorlevel 1 goto hata
if exist publish_backup goto yedek_var
if exist publish move publish publish_backup
if errorlevel 1 goto hata
move publish_staging publish
if errorlevel 1 goto geri_al
if exist publish_backup rmdir /s /q publish_backup
echo.
echo Uygulama hazir: %CD%\publish\ArsanGazERP.exe
explorer "%CD%\publish"
pause
exit /b 0
:geri_al
if exist publish_backup move publish_backup publish
goto hata
:yedek_var
echo Onceki yayin yedegi var; guvenlik icin otomatik degistirme yapilmadi.
goto hata
:hata
echo Derleme veya yayin basarisiz oldu.
pause
exit /b 1
