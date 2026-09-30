@echo off
setlocal EnableExtensions
set "EXE=C:\ArsanGazERP\ArsanGazERP_V2_Duzeltilmis\publish\current\ArsanGazERP.exe"
if exist "%EXE%" start "" "%EXE%" & exit /b 0
for /f "delims=" %%F in ('powershell -NoProfile -Command "Get-ChildItem -Path ''C:\ArsanGazERP\ArsanGazERP_V2_Duzeltilmis'' -Filter ArsanGazERP.exe -Recurse -File ^| Where-Object { $_.FullName -notmatch ''\\(bin^|obj^|Tools)\\'' } ^| Sort-Object LastWriteTime -Descending ^| Select-Object -First 1 -ExpandProperty FullName"') do set "EXE=%%F"
if defined EXE start "" "%EXE%"
