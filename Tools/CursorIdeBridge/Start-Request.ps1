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
