@echo off
chcp 65001 >nul
cd /d "%~dp0"
where node >nul 2>nul || (echo Node.js bulunamadi. https://nodejs.org adresinden LTS surumunu kurun. & pause & exit /b 1)
if not exist "Tools\CursorTelegramBridge\node_modules\@cursor\sdk" (
  echo Cursor SDK kuruluyor...
  call npm install --prefix "Tools\CursorTelegramBridge" || (pause & exit /b 1)
)
node "Tools\CursorTelegramBridge\bridge.js" --login
echo.
echo Giris tamamlandiysa AgentWatcher kopruyu otomatik baslatir. Bu pencereyi kapatabilirsiniz.
pause
