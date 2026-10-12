# Marks a claimed CopilotInbox request as started by the IDE chat so bridge.js will not take it over.
# Prints STARTED <id> when the IDE chat owns the request; otherwise prints TAKEN <id> <reason> and exits 2.
param([Parameter(Mandatory = $true)][string]$Id)

$ErrorActionPreference = 'Stop'
if ($Id -notmatch '^[A-Za-z0-9_-]{1,80}$') { throw "Gecersiz istek kimligi: $Id" }
$inbox = Join-Path $env:LOCALAPPDATA 'ArsanGazERP\AgentWatcherData\CopilotInbox'
$utf8 = New-Object System.Text.UTF8Encoding($false)

if (Test-Path (Join-Path $inbox "$Id.completion.json")) { Write-Output "TAKEN $Id zaten tamamlanmis"; exit 2 }
if (Test-Path (Join-Path $inbox "$Id.dispatch.json")) { Write-Output "TAKEN $Id yedek kopru (bridge.js) devraldi"; exit 2 }
if (-not (Test-Path (Join-Path $inbox "$Id.request.json"))) { Write-Output "TAKEN $Id istek dosyasi yok"; exit 2 }

$marker = @{ id = $Id; startedUtc = (Get-Date).ToUniversalTime().ToString('o'); pid = $PID } | ConvertTo-Json -Compress
[IO.File]::WriteAllText((Join-Path $inbox "$Id.ide-started.json"), $marker, $utf8)
# bridge.js writes the dispatch marker before releasing a claim; re-check to close the race.
if (Test-Path (Join-Path $inbox "$Id.dispatch.json")) {
    Remove-Item -LiteralPath (Join-Path $inbox "$Id.ide-started.json") -Force
    Write-Output "TAKEN $Id yedek kopru (bridge.js) devraldi"
    exit 2
}
Write-Output "STARTED $Id"

# Video attachments: the IDE chat cannot watch a video, so print timestamped frame PNGs (video-frames.js) to Read.
try {
    $request = [IO.File]::ReadAllText((Join-Path $inbox "$Id.request.json"), $utf8) | ConvertFrom-Json
    $videos = @(@($request.Attachments) | Where-Object { $_ } | Where-Object {
            ([string]$_.MimeType -like 'video/*') -or ([IO.Path]::GetExtension([string]$_.LocalPath) -match '^\.(mp4|mov|m4v|webm|mkv|3gp|avi)$')
        } | ForEach-Object { [string]$_.LocalPath } | Where-Object { $_ -and (Test-Path -LiteralPath $_) })
    if ($videos.Count -gt 0) {
        $node = (Get-Command node.exe -ErrorAction SilentlyContinue).Source
        if (-not $node) { $node = Join-Path $env:ProgramFiles 'nodejs\node.exe' }
        $script = Join-Path $PSScriptRoot '..\CursorTelegramBridge\video-frames.js'
        [Console]::OutputEncoding = $utf8
        $ErrorActionPreference = 'Continue'
        $json = (& $node $script --json @videos 2>$null) -join "`n"
        Write-Output "VIDEO_FRAMES: videoyu dogrudan okuyamazsin; asagidaki PNG karelerini Read araciyla oku."
        foreach ($video in @($json | ConvertFrom-Json)) {
            if ($video.error) { Write-Output "VIDEO $($video.source) kareler cikarilamadi: $($video.error)"; continue }
            $size = if ($video.width) { " $($video.width)x$($video.height)" } else { '' }
            Write-Output ("VIDEO {0} sure={1:0.0}s{2}" -f $video.source, [double]$video.durationSec, $size)
            foreach ($sheet in @($video.sheets)) {
                $times = (@($sheet.timestamps) | ForEach-Object { '{0:0.0}s' -f [double]$_ }) -join ' '
                Write-Output "  SHEET $($sheet.path) ($($video.columns)x$($video.rows), soldan saga/yukaridan asagi: $times)"
            }
            foreach ($frame in @($video.keyFrames)) {
                Write-Output ("  FRAME {0} (buyuk kare, {1:0.0}s)" -f $frame.path, [double]$frame.timeSec)
            }
        }
    }
} catch {
    Write-Output "VIDEO_FRAMES hata: $($_.Exception.Message)"
}
exit 0
