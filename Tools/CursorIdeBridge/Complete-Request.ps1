# Finishes a Telegram request claimed by the IDE chat: AgentWatcher forwards the
# completion report to Telegram exactly like a bridge.js completion.
param(
    [Parameter(Mandatory = $true)][string]$Id,
    [Parameter(Mandatory = $true)][string]$ReportPath
)

$ErrorActionPreference = 'Stop'
if ($Id -notmatch '^[A-Za-z0-9_-]{1,80}$') { throw "Invalid request id: $Id" }
$data = Join-Path $env:LOCALAPPDATA 'ArsanGazERP\AgentWatcherData'
$inbox = Join-Path $data 'CopilotInbox'
$utf8 = New-Object System.Text.UTF8Encoding($false)
$requestPath = Join-Path $inbox "$Id.request.json"
$completionPath = Join-Path $inbox "$Id.completion.json"
if (Test-Path $completionPath) { throw "Request already completed: $Id" }
$startedPath = Join-Path $inbox "$Id.ide-started.json"
if ((Test-Path (Join-Path $inbox "$Id.dispatch.json")) -and -not (Test-Path $startedPath)) {
    throw "Request was taken over by bridge.js; its report will reach Telegram from there: $Id"
}

$report = [IO.File]::ReadAllText((Resolve-Path $ReportPath), $utf8).Trim()
if ($report.Length -eq 0) { throw 'Report is empty.' }

$taskId = $Id
$requestText = ''
if (Test-Path $requestPath) {
    $request = [IO.File]::ReadAllText($requestPath, $utf8) | ConvertFrom-Json
    if ($request.TaskId) { $taskId = [string]$request.TaskId }
    $requestText = [string]$request.Text
}

$completion = [ordered]@{
    id = $Id
    taskId = $taskId
    status = 'completed'
    report = $report
    updatedUtc = (Get-Date).ToUniversalTime().ToString('o')
} | ConvertTo-Json
$temporary = "$completionPath.$PID.tmp"
[IO.File]::WriteAllText($temporary, $completion, $utf8)
Move-Item -LiteralPath $temporary -Destination $completionPath

Remove-Item -LiteralPath $requestPath -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $inbox "$Id.ide-claim.json") -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $startedPath -ErrorAction SilentlyContinue

$journal = Join-Path $data 'cursor-journal.md'
$entry = "`r`n## $((Get-Date).ToString('yyyy-MM-dd HH:mm')) - IDE sohbeti - $Id`r`n**Istek:** $requestText`r`n`r`n**Rapor:** $report`r`n"
[IO.File]::AppendAllText($journal, $entry, $utf8)
Write-Output "COMPLETED $Id -> Telegram"
