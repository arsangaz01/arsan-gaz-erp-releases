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

    The loop is also the PC-side watchdog: it restarts bridge.js and AgentWatcher (which sends every PC -> Telegram
    message from TelegramOutbox) when they are gone.
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
$BridgeScript = Join-Path (Split-Path $PSScriptRoot) 'CursorTelegramBridge\bridge.js'
$WatcherExe = Join-Path (Split-Path (Split-Path $PSScriptRoot)) 'publish\current\AgentWatcher\ArsanGazERP.AgentWatcher.exe'
$IdeWatchScript = Join-Path $PSScriptRoot 'ide-watch.ps1'
$script:LastBridgeStart = [DateTime]::MinValue
$script:WatcherMissingSince = $null
$script:LastWatcherStart = [DateTime]::MinValue
$script:LastIdeWatchStart = [DateTime]::MinValue
$script:BridgeAlerted = $false
$script:OpenIds = @()
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

function Test-BridgeRunning {
    try {
        $bridgePid = [int]([IO.File]::ReadAllText((Join-Path $LocalData 'AgentWatcherData\cursor-bridge.pid')).Trim())
        return (Get-Process -Id $bridgePid -ErrorAction Stop).ProcessName -eq 'node'
    }
    catch { return $false }
}

function Get-IdeWatchProcesses {
    # Any live CursorIdeBridge\ide-watch.ps1 counts — status.json alone races when Sync starts a second copy.
    try {
        return @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue |
            Where-Object { $_.CommandLine -and ($_.CommandLine -match 'CursorIdeBridge[\\/]+ide-watch\.ps1') })
    }
    catch { return @() }
}

function Test-IdeWatcherRunning {
    $procs = Get-IdeWatchProcesses
    if ($procs.Count -gt 0) {
        $status = Join-Path $Inbox 'ide-session-status.json'
        if (Test-Path -LiteralPath $status) {
            try {
                $json = [IO.File]::ReadAllText($status, $Utf8) | ConvertFrom-Json
                if ($json.owner -eq 'YouTubeAuto-Cursor') { return $false }
            }
            catch { }
        }
        return $true
    }
    $status = Join-Path $Inbox 'ide-session-status.json'
    if (-not (Test-Path -LiteralPath $status)) { return $false }
    if (((Get-Date).ToUniversalTime() - (Get-Item -LiteralPath $status).LastWriteTimeUtc).TotalSeconds -ge 90) { return $false }
    try {
        $json = [IO.File]::ReadAllText($status, $Utf8) | ConvertFrom-Json
        if ($json.owner -eq 'YouTubeAuto-Cursor') { return $false }
        if ($json.pid) {
            $proc = Get-Process -Id ([int]$json.pid) -ErrorAction Stop
            return $null -ne $proc
        }
    }
    catch { return $false }
    return $true
}

function Enforce-YouTubeArsanStub {
    # YouTubeAuto Cursor agents keep restoring the full hijack script; rewrite stub every sync pass.
    $path = 'C:\YouTubeAuto\TelegramBridge\Watch-ArsanTelegram.ps1'
    if (-not (Test-Path -LiteralPath (Split-Path $path))) { return }
    $marker = 'DISABLED permanently (ArsanGazERP owns arsanerpbot)'
    $needs = $true
    if (Test-Path -LiteralPath $path) {
        try {
            $text = [IO.File]::ReadAllText($path)
            if ($text -like "*$marker*") { $needs = $false }
        }
        catch { $needs = $true }
    }
    if (-not $needs) { return }
    $stub = @"
# $marker
# Do not restore the old CopilotInbox hijack. YouTube bot = watch.ps1 only.
Write-Output 'Watch-ArsanTelegram DISABLED: ERP Telegram owned by ArsanGazERP CursorIdeBridge.'
exit 2
"@
    try {
        if (Test-Path -LiteralPath $path) {
            attrib -R $path 2>$null
        }
        [IO.File]::WriteAllText($path, $stub, $Utf8)
        attrib +R $path 2>$null
        Write-Log 'Re-stubbed YouTubeAuto Watch-ArsanTelegram.ps1 (ERP inbox protection)'
        $notice = Join-Path $StateDir 'youtube-hijack-restub.txt'
        [IO.File]::WriteAllText($notice,
            "UYARI: YouTubeAuto Watch-ArsanTelegram.ps1 tekrar ERP kuyrugunu gaspetmeye calisti; stub yenilendi. YouTube bot icin yalnizca watch.ps1 kullanin.",
            $Utf8)
        try { & (Join-Path $PSScriptRoot 'Send-Telegram.ps1') -Path $notice | Out-Null } catch { }
    }
    catch { Write-Log "Watch-ArsanTelegram stub rewrite failed: $($_.Exception.Message)" }
}

function Stop-YouTubeErpHijack {
    # YouTubeAuto agents periodically revive Watch-ArsanTelegram.ps1 which overwrites
    # CopilotInbox\ide-session-status.json with owner=YouTubeAuto-Cursor and steals ERP claims.
    Enforce-YouTubeArsanStub
    $hijackers = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -and ($_.CommandLine -match 'Watch-ArsanTelegram\.ps1') })
    foreach ($h in $hijackers) {
        try {
            Stop-Process -Id $h.ProcessId -Force -ErrorAction SilentlyContinue
            Write-Log "Stopped YouTubeAuto ERP inbox hijack pid=$($h.ProcessId)"
        }
        catch { }
    }
    $status = Join-Path $Inbox 'ide-session-status.json'
    if (Test-Path -LiteralPath $status) {
        try {
            $json = [IO.File]::ReadAllText($status, $Utf8) | ConvertFrom-Json
            if ($json.owner -eq 'YouTubeAuto-Cursor') {
                Remove-Item -LiteralPath $status -Force -ErrorAction SilentlyContinue
                Write-Log 'Cleared YouTubeAuto-Cursor ide-session-status.json'
            }
        }
        catch { }
    }
    # Junction TelegramInbox -> CopilotInbox lets YouTube tools claim ERP requests; never allow it.
    $telegramInbox = Join-Path $LocalData 'AgentWatcherData\TelegramInbox'
    if (Test-Path -LiteralPath $telegramInbox) {
        try {
            $item = Get-Item -LiteralPath $telegramInbox -Force
            $isLink = ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
            if ($isLink) {
                cmd /c "rmdir `"$telegramInbox`"" | Out-Null
                Write-Log 'Removed TelegramInbox junction (ERP CopilotInbox must stay exclusive)'
            }
        }
        catch { Write-Log "TelegramInbox junction cleanup failed: $($_.Exception.Message)" }
    }
}

# Keep ArsanGaz ide-watch alive so Telegram requests land in the ERP Cursor chat (not YouTubeAuto).
# Prefer a single process: duplicates race status.pid and used to make bridge.js steal IDE claims.
function Confirm-IdeWatch {
    Stop-YouTubeErpHijack
    $procs = Get-IdeWatchProcesses
    if ($procs.Count -gt 1) {
        # Prefer direct -File ide-watch (Cursor chat shell). Drop -NonInteractive Sync copies and -Command wrappers.
        $chat = @($procs | Where-Object {
            $_.CommandLine -notmatch '-NonInteractive' -and $_.CommandLine -match '-File\s+\S*ide-watch\.ps1'
        })
        $keep = if ($chat.Count -gt 0) { $chat | Sort-Object ProcessId -Descending | Select-Object -First 1 }
            else { $procs | Sort-Object ProcessId -Descending | Select-Object -First 1 }
        foreach ($extra in $procs) {
            if ($extra.ProcessId -eq $keep.ProcessId) { continue }
            try {
                Stop-Process -Id $extra.ProcessId -Force -ErrorAction SilentlyContinue
                Write-Log "Stopped duplicate ide-watch pid=$($extra.ProcessId) (kept $($keep.ProcessId))"
            }
            catch { }
        }
        return
    }
    if (Test-IdeWatcherRunning) { return }
    # Allow quick retry when every ide-watch process is gone (status.json can look fresh for ~90s).
    if ((Get-IdeWatchProcesses).Count -eq 0) {
        if (((Get-Date) - $script:LastIdeWatchStart).TotalSeconds -lt 20) { return }
    }
    elseif (((Get-Date) - $script:LastIdeWatchStart).TotalMinutes -lt 2) { return }
    if (-not (Test-Path -LiteralPath $IdeWatchScript)) { Write-Log "ide-watch.ps1 missing: $IdeWatchScript"; return }
    $script:LastIdeWatchStart = Get-Date
    try {
        # Drop stale YouTubeAuto ownership so bridge/IDE can claim for ArsanGaz.
        $status = Join-Path $Inbox 'ide-session-status.json'
        if (Test-Path -LiteralPath $status) {
            try {
                $json = [IO.File]::ReadAllText($status, $Utf8) | ConvertFrom-Json
                if ($json.owner -eq 'YouTubeAuto-Cursor') { Remove-Item -LiteralPath $status -Force -ErrorAction SilentlyContinue }
            }
            catch { }
        }
        Start-Process -FilePath 'powershell.exe' -ArgumentList @(
            '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
            '-WindowStyle', 'Hidden', '-File', $IdeWatchScript
        ) -WorkingDirectory $PSScriptRoot -WindowStyle Hidden | Out-Null
        Write-Log 'ide-watch.ps1 was not healthy; started ArsanGaz CursorIdeBridge watcher.'
    }
    catch { Write-Log "ide-watch.ps1 could not be started: $($_.Exception.Message)" }
}

# Something on the PC must be able to run a request before the sync takes it from the server: the open IDE chat
# (ide-watch.ps1) or bridge.js, which is also the IDE chat's fallback. A dead bridge is restarted (at most every
# 5 minutes); if nothing can run, the request stays on the server and its Cursor agent answers it.
function Confirm-PcWorker {
    if (Test-BridgeRunning) { $script:BridgeAlerted = $false; return $true }
    $ide = Test-IdeWatcherRunning
    if (((Get-Date) - $script:LastBridgeStart).TotalMinutes -lt 5) { return $ide }
    $script:LastBridgeStart = Get-Date
    try {
        $node = (Get-Command node.exe -ErrorAction SilentlyContinue).Source
        if (-not $node) { $node = Join-Path $env:ProgramFiles 'nodejs\node.exe' }
        if (-not (Test-Path -LiteralPath $node)) { throw 'node.exe bulunamadi' }
        if (-not (Test-Path -LiteralPath $BridgeScript)) { throw "bridge.js yok: $BridgeScript" }
        Start-Process -FilePath $node -ArgumentList "`"$BridgeScript`"" -WorkingDirectory (Split-Path $BridgeScript) -WindowStyle Hidden | Out-Null
        foreach ($i in 1..10) { Start-Sleep -Seconds 1; if (Test-BridgeRunning) { break } }
        if (-not (Test-BridgeRunning)) { throw 'bridge.js 10 sn icinde calismaya baslamadi' }
        Write-Log 'bridge.js was not running; started it.'
        $script:BridgeAlerted = $false
        return $true
    }
    catch {
        Write-Log "bridge.js could not be started: $($_.Exception.Message)"
        if (-not $script:BridgeAlerted) {
            $script:BridgeAlerted = $true
            $note = Join-Path $StateDir 'bridge-alert.txt'
            # ASCII only: Windows PowerShell 5.1 reads this BOM-less script in the ANSI code page.
            [IO.File]::WriteAllText($note, "UYARI: Bilgisayardaki Cursor koprusu (bridge.js) calismiyor ve yeniden baslatilamadi: $($_.Exception.Message)`n" +
                "Telegram istekleri bu arada sunucudaki Cursor ajanina birakiliyor. Kopruyu duzeltmek icin PC'de Cursor_Telegram_Giris.cmd calistirin.", $Utf8)
            try { & (Join-Path $PSScriptRoot 'Send-Telegram.ps1') -Path $note | Out-Null } catch { Write-Log "Telegram alert failed: $($_.Exception.Message)" }
        }
        return $ide
    }
}

# Only the logon startup entry launches AgentWatcher, so nothing brought it back after a crash or kill until the next
# logon. It is restarted after 3 minutes of absence (longer than the startup entry and its own safe restart take),
# at most every 5 minutes; not while AutoSync is swapping it and not when "Windows ile baslat" is turned off.
function Confirm-AgentWatcher {
    if (Get-Process -Name 'ArsanGazERP.AgentWatcher' -ErrorAction SilentlyContinue) { $script:WatcherMissingSince = $null; return }
    if ($null -eq $script:WatcherMissingSince) { $script:WatcherMissingSince = Get-Date; return }
    if (((Get-Date) - $script:WatcherMissingSince).TotalMinutes -lt 3) { return }
    if (((Get-Date) - $script:LastWatcherStart).TotalMinutes -lt 5) { return }
    $autoSync = $null
    if ([System.Threading.Mutex]::TryOpenExisting('Local\ArsanGazERP.AutoSync', [ref]$autoSync)) { $autoSync.Dispose(); return }
    try {
        $settings = [IO.File]::ReadAllText((Join-Path $LocalData 'AgentWatcherData\settings.json'), $Utf8) | ConvertFrom-Json
        if ($settings.StartWithWindows -eq $false) { return }
    }
    catch { }
    $script:LastWatcherStart = Get-Date
    if (-not (Test-Path -LiteralPath $WatcherExe)) { Write-Log "AgentWatcher not found: $WatcherExe"; return }
    try {
        Start-Process -FilePath $WatcherExe -ArgumentList '--background' -WorkingDirectory (Split-Path $WatcherExe) | Out-Null
        Write-Log 'AgentWatcher was not running for 3 minutes; started it.'
    }
    catch { Write-Log "AgentWatcher could not be started: $($_.Exception.Message)" }
}

# A request the server took back (server Cursor agent) must not also run on the PC later.
# Require several consecutive misses so a transient empty/partial poll cannot wipe local requests.
function Remove-ServerTakenCopies([string[]]$Listed) {
    if ($null -eq $script:MissingCounts) { $script:MissingCounts = @{} }
    if ($Listed.Count -eq 0 -and @($script:OpenIds).Count -gt 0) {
        Write-Log 'Poll listed zero open requests while local open ids exist; skip purge this pass.'
        return
    }
    $keepMissing = @{}
    foreach ($id in @($script:OpenIds)) {
        if ($Listed -contains $id) { continue }
        if (Test-Path -LiteralPath (Join-Path $Inbox "$id.completion.json")) { continue }
        if ((Test-Path -LiteralPath (Join-Path $Inbox "$id.progress.json")) -or (Test-Path -LiteralPath (Join-Path $Inbox "$id.ide-started.json"))) { continue }
        $n = 1
        if ($script:MissingCounts.ContainsKey($id)) { $n = [int]$script:MissingCounts[$id] + 1 }
        $keepMissing[$id] = $n
        if ($n -lt 3) {
            Write-Log "Request missing from server list ($n/3); local copy kept: $id"
            continue
        }
        Remove-Item -LiteralPath (Join-Path $Inbox "$id.request.json"), (Join-Path $Inbox "$id.ide-claim.json") -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath (Join-Path $StateDir "$id.owner"), (Join-Path $StateDir "$id.hashes") -ErrorAction SilentlyContinue
        Write-Log "Request is no longer open on the server (taken by the server Cursor agent); local copy removed: $id"
        $keepMissing.Remove($id)
    }
    $script:MissingCounts = $keepMissing
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

    foreach ($kind in 'ide-claim', 'ide-started', 'progress') {
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
    $worker = $null
    $listed = @()
    foreach ($line in $lines) {
        if ($line -notmatch '^(NEW|OPEN) ([A-Za-z0-9_-]{1,80})$') { continue }
        try {
            $kind = $Matches[1]
            $id = $Matches[2]
            $listed += $id
            if ($kind -eq 'NEW') {
                if ($null -eq $worker) { $worker = Confirm-PcWorker }
                if (-not $worker) { continue }
            }
            # An OPEN request without an owner file was marked but its copy failed; copy it again.
            if ($kind -eq 'NEW' -or -not (Test-Path -LiteralPath (Join-Path $StateDir "$id.owner"))) { Import-Request $id $serverNotifies }
            else { Sync-Open $id }
        }
        catch { Write-Log "Sync failed for ${id}: $($_.Exception.Message)" }
    }
    Remove-ServerTakenCopies $listed
    $script:OpenIds = @($listed | Where-Object { Test-Path -LiteralPath (Join-Path $StateDir "$_.owner") })
}

$mutex = New-Object System.Threading.Mutex($false, 'Local\ArsanGazERP.ServerInboxSync')
if (-not $mutex.WaitOne(0)) { Write-Output 'Another sync instance is running.'; return }
try {
    if (-not $Once) { Write-Log 'Server inbox sync started.' }
    $lastError = $null
    do {
        if (-not $Once) {
            try { Confirm-AgentWatcher } catch { Write-Log "AgentWatcher check failed: $($_.Exception.Message)" }
            try { Confirm-IdeWatch } catch { Write-Log "ide-watch check failed: $($_.Exception.Message)" }
        }
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
