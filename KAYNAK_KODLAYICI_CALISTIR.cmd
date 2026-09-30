@echo off
chcp 65001 >nul
set "ROOT=C:\ArsanGazERP\ArsanGazERP_V2_Duzeltilmis"
set "TASK=%ROOT%\SourceCoderTask.txt"
start "" notepad.exe "%TASK%"
echo Gorevi yazip Not Defteri dosyasini kaydedin ve kapatin.
pause
dotnet run --project "%ROOT%\Tools\SourceCoder\ArsanGazERP.SourceCoder.csproj" -c Release -- "%TASK%"
pause
