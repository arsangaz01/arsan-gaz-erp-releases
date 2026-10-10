@echo off
setlocal EnableExtensions
set "ROOT=C:\ArsanGazERP\ArsanGazERP_V2_Duzeltilmis"
set "EXE=%ROOT%\publish\current\ArsanGazERP.exe"
if exist "%EXE%" (
    start "" "%EXE%"
    exit /b 0
)
echo Arsan Gaz ERP executable was not found: %EXE%
exit /b 1
