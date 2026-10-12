<#
.SYNOPSIS
    End-to-end test of the server Cursor agent (arsangaz-cursoragent) through the real production queue, even while
    the PC is on: writes a "servertest-" request (never offered to the PC) to the server CopilotInbox, waits for the
    agent's result, prints it and removes the test files. The agent is asked to change nothing.
    -VideoPath attaches a local video (copied to the server's Telegram attachment folder and removed afterwards) to
    check that the agent receives the extracted frames (video-frames.js); the agent is asked to describe them.
.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File Tools\ServerAgent\Test-ServerCursorAgent.ps1
#>
param(
    [int]$TimeoutSeconds = 300,
    [string]$VideoPath,
    [string]$ServerHost = 'arsangaz@144.24.189.205',
    [string]$KeyPath = (Join-Path $env:USERPROFILE '.ssh\arsangaz_hetzner')
)

$ErrorActionPreference = 'Stop'
$ssh = @('-i', $KeyPath, '-o', 'BatchMode=yes', '-o', 'ConnectTimeout=15', $ServerHost)
$inbox = '/var/lib/arsangaz-data/prod/ArsanGazERP/AgentWatcherData/CopilotInbox'
$id = 'servertest-' + (Get-Date -Format 'yyyyMMddHHmmss')
$request = [ordered]@{
    Id = $id; TaskId = $id; CreatedUtc = (Get-Date).ToUniversalTime().ToString('o')
    Text = "Bu bir baglanti testidir. Hicbir dosyayi degistirme ve komut calistirma; yalnizca 'test tamam' yaz."
}
$remoteVideo = $null
if ($VideoPath) {
    $remoteVideo = "/var/lib/arsangaz-data/prod/ArsanGazERP/AgentWatcher/telegram-attachments/$id$([IO.Path]::GetExtension($VideoPath).ToLowerInvariant())"
    & scp -i $KeyPath -o BatchMode=yes -q $VideoPath "${ServerHost}:$remoteVideo"
    if ($LASTEXITCODE -ne 0) { throw 'Test videosu sunucuya kopyalanamadi.' }
    $request.Text = "Bu bir video eki testidir. Hicbir dosyayi degistirme ve komut calistirma. Ekteki videodan cikarilan kareleri " +
        "gorebiliyor musun? Gorebiliyorsan kac kare/gorsel aldigini, videonun suresini ve ekranda ne oldugunu 3-4 cumleyle anlat; " +
        "goremiyorsan bunu acikca yaz."
    $request.Attachments = @([ordered]@{ LocalPath = $remoteVideo; FileName = [IO.Path]::GetFileName($VideoPath); MimeType = 'video/mp4'; Size = (Get-Item $VideoPath).Length })
}
$request = $request | ConvertTo-Json -Compress -Depth 4
$encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($request))

Write-Output 'Sunucu Cursor ajani durumu:'
& ssh @ssh 'cat /var/lib/arsangaz-cursoragent/status.json 2>/dev/null || echo "durum dosyasi yok"; systemctl is-active arsangaz-cursoragent'
& ssh @ssh "printf '%s' '$encoded' | base64 -d > $inbox/$id.request.json.tmp && mv $inbox/$id.request.json.tmp $inbox/$id.request.json"
if ($LASTEXITCODE -ne 0) { throw 'Test istegi sunucu kuyruguna yazilamadi.' }
Write-Output "Test istegi kuyrukta: $id (en fazla $TimeoutSeconds sn beklenecek)"

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$result = $null
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 10
    $result = & ssh @ssh "if [ -f $inbox/$id.completion.json ]; then cat $inbox/$id.completion.json; elif [ -f $inbox/$id.ack.json ] && grep -q failed $inbox/$id.ack.json; then cat $inbox/$id.ack.json; fi"
    if ($result) { break }
}
& ssh @ssh "rm -f $inbox/$id.*$(if ($remoteVideo) { " $remoteVideo" })" | Out-Null

if (-not $result) {
    Write-Output 'SONUC: zaman asimi. Ajan gunlugu:'
    & ssh @ssh 'sudo journalctl -u arsangaz-cursoragent --since "-10min" --no-pager -o cat | tail -n 15'
    exit 1
}
$json = ($result -join "`n") | ConvertFrom-Json
if ($json.status -eq 'completed') { Write-Output "SONUC: BASARILI`n$($json.report)"; exit 0 }
Write-Output "SONUC: BASARISIZ ($($json.status)) $($json.error)$($json.report)"
exit 1
