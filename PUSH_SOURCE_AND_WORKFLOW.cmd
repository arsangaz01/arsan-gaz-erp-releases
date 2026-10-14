@echo off
chcp 65001 >nul
cd /d "%~dp0"
where git >nul 2>nul
if errorlevel 1 (
  echo Git is not installed or not available in PATH.
  pause
  exit /b 1
)

if not exist ".git" git init

git remote get-url origin >nul 2>nul
if errorlevel 1 git remote add origin https://github.com/arsangaz01/arsan-gaz-erp-kaynak.git

rem Only tracked files are staged; new files must be added deliberately so local-only files are never pushed.
git add -u
git commit -m "Arsan Gaz ERP V6 CI and Setup pipeline"
if errorlevel 1 echo No new commit was created, continuing.

git push origin HEAD:main
if errorlevel 1 (
  echo Push failed. Complete GitHub authentication, then run this file again.
  pause
  exit /b 1
)

echo.
echo Source and workflow were pushed successfully.
echo To create a release, run CREATE_RELEASE_TAG.cmd.
pause