$ErrorActionPreference='Stop'
$Manifest='https://github.com/arsangaz01/arsan-gaz-erp-releases/releases/latest/download/update.json'
$Temp=Join-Path $env:TEMP 'ArsanGazERP_Update'
New-Item -ItemType Directory -Force -Path $Temp|Out-Null
$m=Invoke-RestMethod $Manifest -TimeoutSec 60
$current=[Version]'7.9.3';$remote=[Version]$m.version
if($remote -le $current){exit 0}
$setup=Join-Path $Temp 'ArsanGazERP_Setup.exe'
Invoke-WebRequest $m.url -OutFile $setup -TimeoutSec 600
$hash=(Get-FileHash $setup -Algorithm SHA256).Hash
if($hash -ne $m.sha256){throw 'Güncelleme SHA-256 doğrulaması başarısız.'}
Start-Process $setup -ArgumentList '/SILENT','/CLOSEAPPLICATIONS','/RESTARTAPPLICATIONS' -Verb RunAs