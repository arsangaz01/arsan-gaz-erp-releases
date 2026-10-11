@echo off
chcp 65001 >nul
cd /d "%~dp0"
for /f "tokens=2 delims=<>" %%V in ('findstr /i "<Version>" ArsanGazERP.csproj') do set VERSION=%%V
if "%VERSION%"=="" set VERSION=5.3.0
set TAG=v%VERSION%

rem The tag goes to the private source repo (origin); its workflow publishes the release to the public
rem arsan-gaz-erp-releases repo, which desktop auto-update reads. A version is released only once.
git ls-remote --exit-code --tags origin %TAG% >nul 2>nul
if not errorlevel 1 (
  echo Tag %TAG% already exists on origin. Increase the version in ArsanGazERP.csproj first.
  pause
  exit /b 1
)

echo Creating release tag: %TAG%
git tag %TAG%
if errorlevel 1 (
  echo Tag %TAG% already exists locally. Increase the version in ArsanGazERP.csproj first.
  pause
  exit /b 1
)
git push origin %TAG%
if errorlevel 1 (
  echo Tag push failed.
  pause
  exit /b 1
)

echo GitHub Actions release build started for %TAG% (published to arsan-gaz-erp-releases).
pause