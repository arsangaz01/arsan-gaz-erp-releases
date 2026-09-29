@echo off
chcp 65001 >nul
cd /d "%~dp0"
for /f "tokens=2 delims=<>" %%V in ('findstr /i "<Version>" ArsanGazERP.csproj') do set VERSION=%%V
if "%VERSION%"=="" set VERSION=5.0.2
set TAG=v%VERSION%

echo Creating release tag: %TAG%
git tag -f %TAG%
git push origin %TAG% --force
if errorlevel 1 (
  echo Tag push failed.
  pause
  exit /b 1
)

echo GitHub Actions release build started for %TAG%.
pause