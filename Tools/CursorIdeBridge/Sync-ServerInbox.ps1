<#
.SYNOPSIS
    PC half of the server <-> PC Cursor queue (server half: /opt/arsangaz/bin/inbox-sync.sh).
    Phone requests (/api/cursor) and, once the server owns Telegram, Telegram requests are written to the server's
    CopilotInbox. This loop copies new requests (and their attachments) into the local CopilotInbox, where the IDE
    chat or bridge.js handles them as usual, and uploads progress/claim/completion files back so the phone and the
    server agent see the result.

    Telegram notification for phone requests is sent by exactly one side: the server when its bot token is
    configured (TELEGRAM 1), otherwise the PC MobileApi. Telegram-direct replies are always forwarded by the server
    (the PC never gets the .delivery.json tracking file).
.PARAMETER Once
    Runs one sync pass and exits (for tests).
.NOTES
    Scheduled task: Register-ServerInboxSyncTask.ps1. Log: %LOCALAPPDATA%\ArsanGazERP\ServerInboxSync\sync.log
#>
[CmdletBinding()]
param(
    [switch]$Once,
    [int]$IntervalSeconds = 15,
    [string]$ServerHost = 'arsangaz@144.24.189.205',
    [string]$KeyPath = (Join-Path $env:USERPROFILE '.ssh\arsangaz_hetzner')
)

$ErrorActionPreference = 'Stop'
$ServerData = '/var/lib/arsangaz-data/prod/ArsanGazERP/'
$LocalData = Join-Path $env:LOCALAPPDATA 'ArsanGazERP'
$Inbox = Join-Path $LocalData 'AgentWatcherData\CopilotInbox'
$StateDir = Join-Path $LocalData 'ServerInboxSync'
$LogPath = Join-Path $StateDir 'sync.log'
$Utf8 = New-Object System.Text.UTF8Encoding($false)
New-Item -ItemType Directory -Force -Path $Inbox, $StateDir | Out-Null

function Write-Log([string]$Message) {
    $line = '{0:yyyy-MM-dd HH:mm:ss} {1}' -f (Get-Date), $Message
    [IO.File]::AppendAllText($LogPath, $line + "`r`n", $Utf8)
    if ($Once) { Write-Output $line }
}

function Invoke-Server([string]$Arguments, [string]$InputText = $null) {
    $sshArgs = @('-i', $KeyPath, '-o', 'BatchMode=yes', '-o', 'ConnectTimeout=10', $ServerHost, "/opt/arsangaz/bin/inbox-sync.sh $Arguments")
    if ($null -ne $InputText) { $output = $InputText | & ssh @sshArgs 2>&1 }
    else { $output = & ssh @sshArgs 2>&1 }
    if ($LASTEXITCODE -ne 0) { throw "inbox-sync.sh $(($Arguments -split ' ')[0]) failed ($LASTEXITCODE): $($output | Select-Object -Last 1)" }
    return @($output | ForEach-Object { "$_" })
}

function ConvertTo-LocalPath([string]$ServerPath) {
    $relative = $ServerPath.Substring($ServerData.Length)
    if ($relative -match '(^|/)\.\.(/|$)') { throw "Unsafe attachment path: $ServerPath" }
    return Join-Path $LocalData ($relative -replace '/', '\')
}

function Import-Request([string]$Id, [bool]$ServerNotifies) {
    # Claim first (under the lock shared with the server Cursor agent) so a request is never processed on both sides.
    $mark = Invoke-Server "mark $Id"
    if ($mark -contains 'TAKEN') { Write-Log "Request is handled by the server Cursor agent: $Id"; return }

    $requestPath = Join-Path $Inbox "$Id.request.json"
    if (-not (Test-Path -LiteralPath $requestPath)) {
        $json = $Utf8.GetString([Convert]::FromBase64String((Invoke-Server "get $Id" | Select-Object -Last 1)))
        # Attachment paths are server paths; fetch each file and point the request at the local copy.
        $literals = [regex]::Matches($json, '"(/var/lib/arsangaz-data/prod/ArsanGazERP/(?:[^"\\]|\\.)+)"') |
            ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique
        foreach ($literal in $literals) {
            $serverPath = ConvertFrom-Json ('"' + $literal + '"')
            $localPath = ConvertTo-LocalPath $serverPath
            New-Item -ItemType Directory -Force -Path (Split-Path $localPath) | Out-Null
            $encodedPath = [Convert]::ToBase64String($Utf8.GetBytes($serverPath))
            $bytes = [Convert]::FromBase64String((Invoke-Server "file $encodedPath" | Select-Object -Last 1))
            [IO.File]::WriteAllBytes($localPath, $bytes)
            $json = $json.Replace('"' + $literal + '"', (ConvertTo-Json $localPath))
        }

        if ($ServerNotifies) {
            [IO.File]::WriteAllText((Join-Path $Inbox "$Id.mobile-notified"), (Get-Date).ToUniversalTime().ToString('o'), $Utf8)
        }

        $temporary = "$requestPath.sync.tmp"
        [IO.File]::WriteAllText($temporary, $json, $Utf8)
        Move-Item -LiteralPath $temporary -Destination $requestPath -Force
        Write-Log "Server request copied to the PC queue: $Id ($($literals.Count) attachment(s))"
    }

    [IO.File]::WriteAllText((Join-Path $StateDir "$Id.owner"), $(if ($ServerNotifies) { 'server' } else { 'pc' }), $Utf8)
}

function Send-File([string]$Id, [string]$Kind, [string]$Path) {
    $content = [Convert]::ToBase64String([IO.File]::ReadAllBytes($Path))
    Invoke-Server "put $Id $Kind" $content | Out-Null
}

function Sync-Open([string]$Id) {
    $hashFile = Join-Path $StateDir "$Id.hashes"
    $sent = @{}
    if (Test-Path -LiteralPath $hashFile) {
        foreach ($line in [IO.File]::ReadAllLines($hashFile)) { $parts = $line -split '=', 2; if ($parts.Count -eq 2) { $sent[$parts[0]] = $parts[1] } }
    }

    foreach ($kind in 'ide-claim', 'progress') {
        $path = Join-Path $Inbox "$Id.$kind.json"
        if (-not (Test-Path -LiteralPath $path)) { continue }
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        if ($sent[$kind] -ne $hash) { Send-File $Id $kind $path; $sent[$kind] = $hash }
    }

    $completion = Join-Path $Inbox "$Id.completion.json"
    $ack = Join-Path $Inbox "$Id.ack.json"
    $failed = (Test-Path -LiteralPath $ack) -and ([IO.File]::ReadAllText($ack) -match '"failed"')
    if ((Test-Path -LiteralPath $completion) -or $failed) {
        $ownerFile = Join-Path $StateDir "$Id.owner"
        if ((Test-Path -LiteralPath $ownerFile) -and [IO.File]::ReadAllText($ownerFile) -eq 'pc') {
            # The PC MobileApi notifies Telegram; keep the server from sending a second message.
            Invoke-Server "notified $Id" | Out-Null
        }
        if ($failed) { Send-File $Id 'ack' $ack }
        if (Test-Path -LiteralPath $completion) { Send-File $Id 'completion' $completion }
        Remove-Item -LiteralPath $hashFile, $ownerFile -ErrorAction SilentlyContinue
        Write-Log "Result sent back to the server: $Id"
        return
    }

    [IO.File]::WriteAllLines($hashFile, [string[]]@($sent.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }), $Utf8)
}

function Invoke-SyncPass {
    $lines = Invoke-Server "poll $env:COMPUTERNAME"
    $serverNotifies = $lines -contains 'TELEGRAM 1'
    foreach ($line in $lines) {
        if ($line -notmatch '^(NEW|OPEN) ([A-Za-z0-9_-]{1,80})$') { continue }
        try {
            $id = $Matches[2]
            # An OPEN request without an owner file was marked but its copy failed; copy it again.
            if ($Matches[1] -eq 'NEW' -or -not (Test-Path -LiteralPath (Join-Path $StateDir "$id.owner"))) { Import-Request $id $serverNotifies }
            else { Sync-Open $id }
        }
        catch { Write-Log "Sync failed for ${id}: $($_.Exception.Message)" }
    }
}

$mutex = New-Object System.Threading.Mutex($false, 'Local\ArsanGazERP.ServerInboxSync')
if (-not $mutex.WaitOne(0)) { Write-Output 'Another sync instance is running.'; return }
try {
    if (-not $Once) { Write-Log 'Server inbox sync started.' }
    $lastError = $null
    do {
        try {
            Invoke-SyncPass
            if ($lastError) { Write-Log 'Server reachable again.'; $lastError = $null }
        }
        catch {
            if ($_.Exception.Message -ne $lastError) { Write-Log "Server unreachable: $($_.Exception.Message)"; $lastError = $_.Exception.Message }
        }
        if (-not $Once) { Start-Sleep -Seconds $IntervalSeconds }
    } while (-not $Once)
}
finally { $mutex.ReleaseMutex() }
