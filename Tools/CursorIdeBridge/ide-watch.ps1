# Runs inside the open Cursor IDE chat as a monitored background shell.
# While it runs, Telegram requests in CopilotInbox are claimed for the IDE chat
# (bridge.js stays idle) and new ERP research reports wake the chat.
# The chat must run Start-Request.ps1 within bridge.js's grace period, otherwise bridge.js takes the request over.
param([int]$IntervalSeconds = 3)

$ErrorActionPreference = 'Continue'
$data = Join-Path $env:LOCALAPPDATA 'ArsanGazERP\AgentWatcherData'
$inbox = Join-Path $data 'CopilotInbox'
$reports = Join-Path $data 'Reports'
$statusPath = Join-Path $inbox 'ide-session-status.json'
$seenPath = Join-Path $data 'ide-seen-reports.txt'
$utf8 = New-Object System.Text.UTF8Encoding($false)
$reportPattern = 'erp-research|daily-review'

function Write-Atomic([string]$path, [string]$text) {
    $temporary = "$path.$PID.tmp"
    [IO.File]::WriteAllText($temporary, $text, $utf8)
    Move-Item -LiteralPath $temporary -Destination $path -Force
}

function Write-Wake([string]$kind, [hashtable]$payload) {
    $json = $payload | ConvertTo-Json -Compress
    Write-Output "AGENT_LOOP_WAKE_ide_$kind $json"
}

function Get-Preview([string]$requestPath) {
    try {
        $request = [IO.File]::ReadAllText($requestPath, $utf8) | ConvertFrom-Json
        $text = [string]$request.Text
        $text = ($text -replace '\s+', ' ').Trim()
        if ($text.Length -gt 300) { $text = $text.Substring(0, 300) + '...' }
        return $text
    } catch {
        return '(okunamadi)'
    }
}

New-Item -ItemType Directory -Force -Path $inbox, $reports | Out-Null
$seen = New-Object 'System.Collections.Generic.HashSet[string]'
if (Test-Path $seenPath) {
    foreach ($line in [IO.File]::ReadAllLines($seenPath, $utf8)) { [void]$seen.Add($line) }
} else {
    $existing = Get-ChildItem $reports -File -Filter *.txt | Where-Object Name -match $reportPattern | ForEach-Object Name
    [IO.File]::WriteAllLines($seenPath, [string[]]@($existing), $utf8)
    foreach ($name in $existing) { [void]$seen.Add($name) }
}

$firstPass = $true
Write-Output "IDE_WATCH_STARTED pid=$PID inbox=$inbox"
while ($true) {
    $status = @{ active = $true; pid = $PID; updatedUtc = (Get-Date).ToUniversalTime().ToString('o') } | ConvertTo-Json -Compress
    Write-Atomic $statusPath $status

    foreach ($request in Get-ChildItem $inbox -File -Filter *.request.json -ErrorAction SilentlyContinue | Sort-Object LastWriteTime) {
        $id = $request.Name -replace '\.request\.json$', ''
        if ($id -notmatch '^[A-Za-z0-9_-]{1,80}$') { continue }
        if (Test-Path (Join-Path $inbox "$id.completion.json")) { continue }
        if (Test-Path (Join-Path $inbox "$id.ack.json")) { continue }
        if (Test-Path (Join-Path $inbox "$id.dispatch.json")) { continue }
        $claimPath = Join-Path $inbox "$id.ide-claim.json"
        $claimed = Test-Path $claimPath
        if ($claimed -and -not $firstPass) { continue }
        if (-not $claimed) {
            Write-Atomic $claimPath (@{ id = $id; claimedUtc = (Get-Date).ToUniversalTime().ToString('o'); pid = $PID } | ConvertTo-Json -Compress)
        }
        Write-Wake 'request' @{ id = $id; path = $request.FullName; preview = (Get-Preview $request.FullName); resumed = $claimed }
    }

    foreach ($report in Get-ChildItem $reports -File -Filter *.txt -ErrorAction SilentlyContinue | Where-Object Name -match $reportPattern | Sort-Object LastWriteTime) {
        if (-not $seen.Add($report.Name)) { continue }
        [IO.File]::AppendAllText($seenPath, $report.Name + "`r`n", $utf8)
        Write-Wake 'report' @{ name = $report.Name; path = $report.FullName }
    }

    $firstPass = $false
    Start-Sleep -Seconds $IntervalSeconds
}
