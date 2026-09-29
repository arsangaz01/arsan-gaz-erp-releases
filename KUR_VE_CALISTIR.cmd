@echo off
chcp 65001 >nul
cd /d "%~dp0"
dotnet clean
dotnet restore
if errorlevel 1 goto hata
dotnet run
if errorlevel 1 goto hata
exit /b 0
:hata
echo Islem basarisiz oldu.
pause
exit /b 1
