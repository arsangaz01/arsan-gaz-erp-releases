<#
.SYNOPSIS
    Keeps this PC in sync with origin: fast-forwards the working branch, rebuilds the desktop apps whose code
    changed (in a separate git worktree, so local work in progress is never built) and swaps the binaries in
    publish\current with .bak-<stamp> backups. The ERP is only restarted when the user is idle, or after a
    warning once the maximum deferral time has passed.

.PARAMETER DryRun
    Fetches and reports what would happen. No merge, build, swap, restart or Telegram message.
.PARAMETER BuildOnly
    Builds pending (and -ForceApps) apps into the staging folder and stops. Nothing is swapped or restarted,
    and the pending state is not changed.
.PARAMETER ForceApps
    Treat these apps as changed at the current HEAD (ERP, AgentWatcher, MobileApi).
.PARAMETER LibraryOnly
    Only define the functions (used by the tests).
.NOTES
    Disable: Disable-ScheduledTask -TaskName 'ArsanGazERP AutoSync', or set Enabled=false in
    %LOCALAPPDATA%\ArsanGazERP\AutoSync\config.json. Log: autosync.log in the same folder.
#>
[CmdletBinding()]
param(
    [switch]$DryRun,
    [switch]$BuildOnly,
    [string[]]$ForceApps = @(),
    [switch]$NoFetch,
    [switch]$LibraryOnly
)

$ErrorActionPreference = 'Stop'
# powershell -File passes "A,B" as one string.
$ForceApps = @($ForceApps | ForEach-Object { $_ -split '[,; ]+' } | Where-Object { $_ })
foreach ($name in $ForceApps) {
    if ($name -notin @('ERP', 'AgentWatcher', 'MobileApi')) { throw "Unknown app '$name' (ERP, AgentWatcher, MobileApi)." }
}
$script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$script:DataDir = Join-Path $env:LOCALAPPDATA 'ArsanGazERP\AutoSync'
$script:LogPath = Join-Path $script:DataDir 'autosync.log'
$script:StatePath = Join-Path $script:DataDir 'state.json'
$script:ConfigPath = Join-Path $script:DataDir 'config.json'
$script:WorktreeDir = Join-Path $script:DataDir 'wt'
$script:StagingRoot = Join-Path $script:DataDir 'staging'
$script:SendTelegramScript = Join-Path $script:RepoRoot 'Tools\CursorIdeBridge\Send-Telegram.ps1'
$script:PublishCurrent = Join-Path $script:RepoRoot 'publish\current'
$script:Utf8 = New-Object System.Text.UTF8Encoding($false)
$script:IsDryRun = [bool]$DryRun
$script:State = $null
$script:Config = $null
$script:DryRunApps = @()

# Files that are known to be dirty/untracked on this PC and must never block a sync.
$script:IgnoredDirtyPatterns = @(
    '^CloudAgentWorker/(package\.json|package-lock\.json|tsconfig\.json|wrangler\.jsonc)$',
    '^Tools/CursorTelegramBridge/erp-tools\.smoke\.js$',
    '^\.cursor/hooks\.json$',
    '(^|/)(bin|obj|bin-linux|obj-linux|node_modules)(/|$)'
)

$env:GIT_TERMINAL_PROMPT = '0'
$env:GCM_INTERACTIVE = 'never'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

# ---------------------------------------------------------------- infrastructure

function Write-Log([string]$Message) {
    if (-not (Test-Path -LiteralPath $script:DataDir)) { New-Item -ItemType Directory -Force -Path $script:DataDir | Out-Null }
    $prefix = if ($script:IsDryRun) { '[DRY] ' } else { '' }
    $line = "[{0}] [{1}] {2}{3}" -f (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'), $PID, $prefix, $Message
    Write-Host $line
    for ($i = 0; $i -lt 5; $i++) {
        try { [IO.File]::AppendAllText($script:LogPath, $line + "`r`n", $script:Utf8); break } catch { Start-Sleep -Milliseconds 200 }
    }
}

function Invoke-LogRotation {
    if (-not (Test-Path -LiteralPath $script:LogPath)) { return }
    if ((Get-Item -LiteralPath $script:LogPath).Length -lt 1MB) { return }
    for ($i = 4; $i -ge 1; $i--) {
        $src = Join-Path $script:DataDir "autosync.$i.log"
        if (Test-Path -LiteralPath $src) { Move-Item -LiteralPath $src -Destination (Join-Path $script:DataDir "autosync.$($i + 1).log") -Force }
    }
    Move-Item -LiteralPath $script:LogPath -Destination (Join-Path $script:DataDir 'autosync.1.log') -Force
}

function ConvertTo-Hashtable($Value) {
    if ($null -eq $Value) { return $null }
    if ($Value -is [System.Management.Automation.PSCustomObject]) {
        $h = @{}
        foreach ($p in $Value.PSObject.Properties) { $h[$p.Name] = ConvertTo-Hashtable $p.Value }
        return $h
    }
    if ($Value -is [System.Collections.IEnumerable] -and $Value -isnot [string]) {
        return , @($Value | ForEach-Object { ConvertTo-Hashtable $_ })
    }
    return $Value
}

function Read-JsonFile([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return @{} }
    $text = [IO.File]::ReadAllText($Path, $script:Utf8)
    if ([string]::IsNullOrWhiteSpace($text)) { return @{} }
    $h = ConvertTo-Hashtable ($text | ConvertFrom-Json)
    if ($h -isnot [hashtable]) { return @{} }
    return $h
}

function Get-Config {
    $defaults = @{
        Enabled           = $true
        IdleMinutes       = 15     # ERP is restarted without waiting once the user has been idle this long
        MaxDeferHours     = 2      # after this, the ERP is restarted anyway (with a warning)
        WarningMinutes    = 5      # warning period before a forced ERP restart
        RestartErp        = $true  # false: the ERP is only swapped while it is not running
        NotifyTelegram    = $true
        StartupCheckSec   = 25     # how long a restarted app must stay alive
        BuildTimeoutMin   = 40
        KeepBackups       = 3
    }
    $user = Read-JsonFile $script:ConfigPath
    foreach ($k in @($user.Keys)) { $defaults[$k] = $user[$k] }
    return $defaults
}

function Get-State {
    $s = Read-JsonFile $script:StatePath
    foreach ($k in 'Notified', 'Staged', 'Backups') {
        if ($s[$k] -isnot [hashtable]) { $s[$k] = @{} }
    }
    if ($null -eq $s['PendingApps']) { $s['PendingApps'] = @() }
    $s['PendingApps'] = @($s['PendingApps'] | Where-Object { $_ })
    return $s
}

function Save-State {
    if ($script:IsDryRun) { return }
    $cutoff = (Get-Date).AddDays(-30)
    foreach ($k in @($script:State.Notified.Keys)) {
        $t = [datetime]::MinValue
        if (-not [datetime]::TryParse([string]$script:State.Notified[$k], [ref]$t) -or $t -lt $cutoff) { $script:State.Notified.Remove($k) }
    }
    $json = $script:State | ConvertTo-Json -Depth 6
    [IO.File]::WriteAllText("$($script:StatePath).tmp", $json, $script:Utf8)
    Move-Item -LiteralPath "$($script:StatePath).tmp" -Destination $script:StatePath -Force
}

function ConvertTo-CmdArg([string]$Arg) {
    if ($Arg -eq '') { return '""' }
    if ($Arg -notmatch '[\s"]') { return $Arg }
    return '"' + ($Arg -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1') + '"'
}

function Invoke-Native {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$ArgumentList = @(),
        [string]$WorkingDirectory = $script:RepoRoot,
        [int]$TimeoutSec = 600
    )
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $FilePath
    $psi.Arguments = (@($ArgumentList) | ForEach-Object { ConvertTo-CmdArg $_ }) -join ' '
    $psi.WorkingDirectory = $WorkingDirectory
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $psi.StandardOutputEncoding = $script:Utf8
    $psi.StandardErrorEncoding = $script:Utf8
    $p = [System.Diagnostics.Process]::Start($psi)
    $outTask = $p.StandardOutput.ReadToEndAsync()
    $errTask = $p.StandardError.ReadToEndAsync()
    $timedOut = $false
    if (-not $p.WaitForExit($TimeoutSec * 1000)) {
        $timedOut = $true
        try { & taskkill.exe /PID $p.Id /T /F 2>&1 | Out-Null } catch { }
        $p.WaitForExit(10000) | Out-Null
    }
    $code = if ($timedOut) { -1 } else { $p.ExitCode }
    return [pscustomobject]@{ ExitCode = $code; Out = [string]$outTask.Result; Err = [string]$errTask.Result; TimedOut = $timedOut }
}

function Invoke-Git {
    param([string[]]$GitArgs, [switch]$AllowFail, [string]$Dir = $script:RepoRoot, [int]$TimeoutSec = 300)
    $r = Invoke-Native -FilePath 'git' -ArgumentList (@('-C', $Dir, '-c', 'core.quotepath=false') + $GitArgs) -TimeoutSec $TimeoutSec
    if ($r.ExitCode -ne 0 -and -not $AllowFail) {
        throw "git $($GitArgs -join ' ') failed ($($r.ExitCode)): $(($r.Err + ' ' + $r.Out).Trim())"
    }
    return $r
}

function Get-GitLines([string[]]$GitArgs) {
    $r = Invoke-Git -GitArgs $GitArgs
    return @($r.Out -split "`r?`n" | Where-Object { $_ -ne '' })
}

function Test-GitAncestor([string]$Ancestor, [string]$Descendant) {
    return (Invoke-Git -GitArgs @('merge-base', '--is-ancestor', $Ancestor, $Descendant) -AllowFail).ExitCode -eq 0
}

function Send-Notice {
    param([Parameter(Mandatory)][string]$Text, [string]$OnceKey)
    if ($OnceKey -and $script:State.Notified.ContainsKey($OnceKey)) {
        Write-Log "Telegram (already sent, $OnceKey): $Text"
        return
    }
    if ($script:IsDryRun -or -not $script:Config.NotifyTelegram) {
        Write-Log "Telegram (not sent): $Text"
        return
    }
    $tmp = Join-Path $env:TEMP ("autosync-telegram-{0}.txt" -f [guid]::NewGuid().ToString('N'))
    try {
        [IO.File]::WriteAllText($tmp, "AutoSync (PC): $Text", $script:Utf8)
        $r = Invoke-Native -FilePath 'powershell.exe' -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $script:SendTelegramScript, '-Path', $tmp) -TimeoutSec 60
        if ($r.ExitCode -eq 0) { Write-Log "Telegram queued: $Text" }
        else { Write-Log "Telegram queue FAILED ($($r.ExitCode)): $(($r.Err + $r.Out).Trim()) | $Text" }
        if ($OnceKey) { $script:State.Notified[$OnceKey] = (Get-Date).ToString('o') }
    }
    finally { Remove-Item -LiteralPath $tmp -ErrorAction SilentlyContinue }
}

# ---------------------------------------------------------------- apps and change classification

function Get-AppDefinitions {
    return @(
        [pscustomobject]@{
            Name = 'MobileApi'; Project = 'Tools/MobileApi/ArsanGazERP.MobileApi.csproj'; Prefix = '^Tools/MobileApi/(?!bin-linux/|obj-linux/)'
            InstallDir = (Join-Path $script:PublishCurrent 'MobileApi'); Exe = 'ArsanGazERP.MobileApi.exe'
            Files = @('ArsanGazERP.MobileApi.exe', 'ArsanGazERP.MobileApi.pdb'); AllTopFiles = $false; Dirs = @(); Arguments = ''; Interactive = $false
        },
        [pscustomobject]@{
            Name = 'AgentWatcher'; Project = 'Tools/AgentWatcher/ArsanGazERP.AgentWatcher.csproj'; Prefix = '^Tools/AgentWatcher/'
            InstallDir = (Join-Path $script:PublishCurrent 'AgentWatcher'); Exe = 'ArsanGazERP.AgentWatcher.exe'
            Files = @(); AllTopFiles = $true; Dirs = @('runtimes'); Arguments = '--background'; Interactive = $false
            SelfExtractNative = $false
        },
        [pscustomobject]@{
            Name = 'ERP'; Project = 'ArsanGazERP.csproj'; Prefix = $null
            InstallDir = $script:PublishCurrent; Exe = 'ArsanGazERP.exe'
            Files = @('ArsanGazERP.exe', 'ArsanGazERP.pdb'); AllTopFiles = $false; Dirs = @('runtimes'); Arguments = ''; Interactive = $true
        }
    )
}

function Resolve-RepoRelative([string]$ProjectDir, [string]$Include) {
    $parts = New-Object System.Collections.Generic.List[string]
    foreach ($seg in (($ProjectDir + '/' + ($Include -replace '\\', '/')) -split '/')) {
        if ($seg -eq '' -or $seg -eq '.') { continue }
        if ($seg -eq '..') { if ($parts.Count -gt 0) { $parts.RemoveAt($parts.Count - 1) } ; continue }
        $parts.Add($seg)
    }
    return ($parts -join '/')
}

function Get-LinkedFilePatterns([string]$Sha, [string]$Project) {
    $r = Invoke-Git -GitArgs @('show', "$($Sha):$Project") -AllowFail
    if ($r.ExitCode -ne 0) { return @() }
    $projectDir = ($Project -replace '/[^/]+$', '')
    if ($projectDir -eq $Project) { $projectDir = '' }
    $patterns = @()
    foreach ($m in [regex]::Matches($r.Out, 'Include="(\.\.[^"]+)"')) {
        $rel = Resolve-RepoRelative $projectDir $m.Groups[1].Value
        $rx = '^' + ([regex]::Escape($rel) -replace '\\\*\\\*', '.*' -replace '\\\*', '[^/]*') + '$'
        $patterns += $rx
    }
    return $patterns
}

function Get-AffectedApps([string[]]$Files, [string]$Sha) {
    $affected = New-Object System.Collections.Generic.List[string]
    $globalRx = '^(global\.json|Directory\.Build\.(props|targets)|Directory\.Packages\.props|NuGet\.config)$'
    $erpExcludedRx = '^(Tools|AgentHost|CloudAgentWorker|installer|Scripts|publish[^/]*|_Backups|bin|obj|\.github|\.cursor|\.vscode|docs?)/'
    $erpExtRx = '\.(cs|xaml|csproj|props|targets|resx|png|ico|jpe?g|svg|json|config|manifest|ttf)$'
    foreach ($app in Get-AppDefinitions) {
        $patterns = @()
        if ($app.Prefix) { $patterns += $app.Prefix; $patterns += Get-LinkedFilePatterns $Sha $app.Project }
        foreach ($f in $Files) {
            $hit = $f -match $globalRx
            if (-not $hit) {
                if ($app.Name -eq 'ERP') { $hit = ($f -notmatch $erpExcludedRx) -and ($f -match $erpExtRx) }
                else { foreach ($p in $patterns) { if ($f -match $p) { $hit = $true; break } } }
            }
            if ($hit) { $affected.Add($app.Name); break }
        }
    }
    return @($affected)
}

function Get-DirtyPaths {
    $paths = @()
    foreach ($line in (Get-GitLines @('status', '--porcelain=v1', '--untracked-files=normal'))) {
        if ($line.Length -lt 4) { continue }
        $p = $line.Substring(3)
        if ($p -match ' -> ') { $p = ($p -split ' -> ')[-1] }
        $p = $p.Trim('"')
        $ignored = $false
        foreach ($rx in $script:IgnoredDirtyPatterns) { if ($p -match $rx) { $ignored = $true; break } }
        if (-not $ignored) { $paths += $p }
    }
    return $paths
}

# ---------------------------------------------------------------- user activity

function Initialize-Native {
    if ('AutoSyncNative' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class AutoSyncNative {
    [StructLayout(LayoutKind.Sequential)] private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    public static double IdleSeconds() {
        LASTINPUTINFO info = new LASTINPUTINFO();
        info.cbSize = (uint)Marshal.SizeOf(info);
        if (!GetLastInputInfo(ref info)) return -1;
        return unchecked((uint)Environment.TickCount - info.dwTime) / 1000.0;
    }
    public static uint ForegroundProcessId() {
        uint pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid); return pid;
    }
}
'@
}

function Get-UserIdleSeconds { Initialize-Native; return [AutoSyncNative]::IdleSeconds() }
function Get-ForegroundProcessId { Initialize-Native; return [AutoSyncNative]::ForegroundProcessId() }

function Show-DesktopWarning([string]$Text, [int]$Seconds) {
    if ($script:IsDryRun) { return }
    $escaped = $Text -replace "'", "''"
    $cmd = "(New-Object -ComObject WScript.Shell).Popup('$escaped', $Seconds, 'Arsan Gaz ERP güncellemesi', 48) | Out-Null"
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($cmd))
    Start-Process -FilePath 'powershell.exe' -ArgumentList "-NoProfile -WindowStyle Hidden -EncodedCommand $encoded" -WindowStyle Hidden | Out-Null
}

# ---------------------------------------------------------------- build

function Update-BuildWorktree([string]$Sha) {
    if (Test-Path -LiteralPath (Join-Path $script:WorktreeDir '.git')) {
        Invoke-Git -Dir $script:WorktreeDir -GitArgs @('checkout', '--detach', '--force', $Sha) | Out-Null
        Invoke-Git -Dir $script:WorktreeDir -GitArgs @('clean', '-ffdq') | Out-Null
    }
    else {
        if (Test-Path -LiteralPath $script:WorktreeDir) { Remove-Item -LiteralPath $script:WorktreeDir -Recurse -Force }
        Invoke-Git -GitArgs @('worktree', 'prune') -AllowFail | Out-Null
        Invoke-Git -GitArgs @('worktree', 'add', '--detach', $script:WorktreeDir, $Sha) -TimeoutSec 600 | Out-Null
    }
    $head = (Invoke-Git -Dir $script:WorktreeDir -GitArgs @('rev-parse', 'HEAD')).Out.Trim()
    if ($head -ne $Sha) { throw "Build worktree is at $head, expected $Sha" }
}

function Get-BuildErrorSummary([string]$Output) {
    $lines = @($Output -split "`r?`n" | Where-Object { $_ -match ': error |error [A-Z]+\d+|Hata|failed' } | ForEach-Object { $_.Trim() } | Select-Object -Unique)
    if ($lines.Count -eq 0) { $lines = @($Output -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -Last 15) }
    $text = (($lines | Select-Object -First 8) -join "`n") -replace [regex]::Escape($script:WorktreeDir + '\'), ''
    if ($text.Length -gt 1500) { $text = $text.Substring(0, 1500) + '…' }
    return $text
}

function Invoke-AppBuild($App, [string]$Sha) {
    $stage = Join-Path $script:StagingRoot $App.Name
    $tmp = "$stage.new"
    if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Recurse -Force }
    $project = Join-Path $script:WorktreeDir ($App.Project -replace '/', '\')
    # Same switches as the production publishes (AgentWatcher keeps its native DLLs next to the exe).
    $publishArgs = @('publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true')
    if ($App.PSObject.Properties['SelfExtractNative'] -eq $null -or $App.SelfExtractNative) { $publishArgs += '-p:IncludeNativeLibrariesForSelfExtract=true' }
    $publishArgs += @('--disable-build-servers', '-nologo', '-v', 'q', '-o', $tmp)
    Write-Log "Building $($App.Name) at $($Sha.Substring(0, 7))..."
    $started = Get-Date
    $r = Invoke-Native -FilePath 'dotnet' -ArgumentList $publishArgs -WorkingDirectory $script:WorktreeDir -TimeoutSec ([int]$script:Config.BuildTimeoutMin * 60)
    $elapsed = [int]((Get-Date) - $started).TotalSeconds
    if ($r.ExitCode -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $tmp $App.Exe))) {
        $summary = if ($r.TimedOut) { "Derleme $($script:Config.BuildTimeoutMin) dakikada bitmedi." } else { Get-BuildErrorSummary ($r.Out + "`n" + $r.Err) }
        Write-Log "Build of $($App.Name) FAILED after $elapsed s (exit $($r.ExitCode)): $summary"
        return [pscustomobject]@{ Ok = $false; Error = $summary }
    }
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
    Move-Item -LiteralPath $tmp -Destination $stage
    [IO.File]::WriteAllText((Join-Path $stage 'autosync-build.json'), (@{ Sha = $Sha; BuiltAt = (Get-Date).ToString('o') } | ConvertTo-Json), $script:Utf8)
    $size = [math]::Round((Get-Item -LiteralPath (Join-Path $stage $App.Exe)).Length / 1MB, 1)
    Write-Log "Build of $($App.Name) OK in $elapsed s ($($App.Exe) $size MB) -> $stage"
    return [pscustomobject]@{ Ok = $true; Error = $null }
}

# ---------------------------------------------------------------- install / restart

function Get-AppProcesses($App) {
    $exePath = Join-Path $App.InstallDir $App.Exe
    $name = [IO.Path]::GetFileNameWithoutExtension($App.Exe)
    return @(Get-Process -Name $name -ErrorAction SilentlyContinue | Where-Object {
            try { $_.Path -and ([string]::Equals($_.Path, $exePath, [StringComparison]::OrdinalIgnoreCase)) } catch { $false }
        })
}

function Stop-AppProcesses($App) {
    $procs = Get-AppProcesses $App
    if ($procs.Count -eq 0) { return $true }
    if ($App.Interactive) {
        $closeSent = $false
        foreach ($p in $procs) { try { if ($p.CloseMainWindow()) { $closeSent = $true } } catch { } }
        if ($closeSent) {
            for ($i = 0; $i -lt 45 -and (Get-AppProcesses $App).Count -gt 0; $i++) { Start-Sleep -Seconds 1 }
        }
        # The main window may be hidden in the tray; taskkill without /F asks every top-level window to close.
        foreach ($p in (Get-AppProcesses $App)) { & taskkill.exe /PID $p.Id 2>&1 | Out-Null }
        for ($i = 0; $i -lt 15 -and (Get-AppProcesses $App).Count -gt 0; $i++) { Start-Sleep -Seconds 1 }
    }
    foreach ($p in (Get-AppProcesses $App)) {
        Write-Log "Force-stopping $($App.Name) (pid $($p.Id))"
        try { $p.Kill() } catch { }
    }
    for ($i = 0; $i -lt 15 -and (Get-AppProcesses $App).Count -gt 0; $i++) { Start-Sleep -Seconds 1 }
    Start-Sleep -Seconds 2
    return (Get-AppProcesses $App).Count -eq 0
}

function Start-App($App) {
    $exePath = Join-Path $App.InstallDir $App.Exe
    $params = @{ FilePath = $exePath; WorkingDirectory = $App.InstallDir; PassThru = $true }
    if ($App.Arguments) { $params.ArgumentList = $App.Arguments }
    return Start-Process @params
}

function Test-AppStarted($App) {
    $p = Start-App $App
    Start-Sleep -Seconds ([int]$script:Config.StartupCheckSec)
    $p.Refresh()
    if (-not $p.HasExited) { return $true }
    # Some apps hand over to a second process; accept any instance running from the install dir.
    return (Get-AppProcesses $App).Count -gt 0
}

function Get-InstallItems($App, [string]$Stage) {
    if ($App.AllTopFiles) {
        return @(Get-ChildItem -LiteralPath $Stage -File | Where-Object { $_.Name -ne 'autosync-build.json' } | Select-Object -ExpandProperty Name)
    }
    return @($App.Files | Where-Object { Test-Path -LiteralPath (Join-Path $Stage $_) })
}

function Restore-Backups($App, $Backups) {
    foreach ($b in $Backups) {
        try {
            if (Test-Path -LiteralPath $b.Dest) { Remove-Item -LiteralPath $b.Dest -Force }
            if ($b.Bak) { Move-Item -LiteralPath $b.Bak -Destination $b.Dest -Force }
        }
        catch { Write-Log "Rollback of $($b.Dest) failed: $($_.Exception.Message)" }
    }
}

function Remove-OldBackups($App, [string]$Stamp) {
    $list = @()
    if ($script:State.Backups.ContainsKey($App.Name)) { $list = @($script:State.Backups[$App.Name]) }
    $list += $Stamp
    $keep = [int]$script:Config.KeepBackups
    while ($list.Count -gt $keep) {
        $old = $list[0]
        $list = @($list | Select-Object -Skip 1)
        foreach ($f in (Get-ChildItem -LiteralPath $App.InstallDir -File -Filter "*.bak-$old" -ErrorAction SilentlyContinue)) {
            Write-Log "Removing old AutoSync backup $($f.FullName)"
            Remove-Item -LiteralPath $f.FullName -Force -ErrorAction SilentlyContinue
        }
    }
    $script:State.Backups[$App.Name] = $list
}

# Swaps the staged build into the install dir. Returns @{ Ok; Message }.
function Install-AppUpdate($App, [string]$Stage) {
    $stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
    $items = Get-InstallItems $App $Stage
    if ($items.Count -eq 0) { return [pscustomobject]@{ Ok = $false; Message = "$($App.Name): staging klasöründe kurulacak dosya yok." } }
    if (-not (Test-Path -LiteralPath $App.InstallDir)) { return [pscustomobject]@{ Ok = $false; Message = "$($App.Name): kurulum klasörü yok ($($App.InstallDir))." } }

    $wasRunning = (Get-AppProcesses $App).Count -gt 0
    if ($wasRunning -and -not (Stop-AppProcesses $App)) {
        return [pscustomobject]@{ Ok = $false; Message = "$($App.Name) kapatılamadı; dosyalar değiştirilmedi." }
    }

    $backups = @()
    try {
        foreach ($name in $items) {
            $dest = Join-Path $App.InstallDir $name
            $bak = $null
            if (Test-Path -LiteralPath $dest) {
                $bak = "$dest.bak-$stamp"
                Move-Item -LiteralPath $dest -Destination $bak
            }
            $backups += [pscustomobject]@{ Dest = $dest; Bak = $bak }
            Copy-Item -LiteralPath (Join-Path $Stage $name) -Destination $dest
        }
        foreach ($dir in $App.Dirs) {
            $src = Join-Path $Stage $dir
            if (-not (Test-Path -LiteralPath $src)) { continue }
            & robocopy.exe $src (Join-Path $App.InstallDir $dir) /E /R:2 /W:2 /NFL /NDL /NJH /NJS /NP | Out-Null
            if ($LASTEXITCODE -ge 8) { throw "robocopy $dir failed ($LASTEXITCODE)" }
        }
    }
    catch {
        $err = $_.Exception.Message
        Write-Log "Swap of $($App.Name) failed, rolling back: $err"
        Restore-Backups $App $backups
        if ($wasRunning) { $null = Start-App $App }
        return [pscustomobject]@{ Ok = $false; Message = "$($App.Name) dosyaları değiştirilemedi ($err); eski sürüm geri yüklendi." }
    }
    Write-Log "Swapped $($App.Name): $($items -join ', ') (backup suffix .bak-$stamp)"

    if (-not $wasRunning) {
        Remove-OldBackups $App $stamp
        return [pscustomobject]@{ Ok = $true; Message = "$($App.Name) güncellendi (çalışmıyordu, başlatılmadı)." }
    }
    if (Test-AppStarted $App) {
        Remove-OldBackups $App $stamp
        return [pscustomobject]@{ Ok = $true; Message = "$($App.Name) güncellendi ve yeniden başlatıldı." }
    }

    Write-Log "$($App.Name) did not stay running after the update; rolling back"
    $null = Stop-AppProcesses $App
    Restore-Backups $App $backups
    $restarted = Test-AppStarted $App
    $tail = if ($restarted) { 'eski sürüm yeniden başlatıldı.' } else { 'eski sürüm de başlatılamadı, elle kontrol edin!' }
    return [pscustomobject]@{ Ok = $false; Message = "$($App.Name) yeni sürümle açılmadı; $tail" }
}

# ---------------------------------------------------------------- main

function Get-SyncTarget {
    $head = (Invoke-Git -GitArgs @('rev-parse', 'HEAD')).Out.Trim()
    $upstream = (Invoke-Git -GitArgs @('rev-parse', '--abbrev-ref', '--symbolic-full-name', '@{u}') -AllowFail).Out.Trim()
    $candidates = @()
    if ($upstream -and $upstream -notmatch 'fatal') { $candidates += $upstream }
    if ($candidates -notcontains 'origin/main') { $candidates += 'origin/main' }
    $target = $head
    $diverged = @()
    foreach ($ref in $candidates) {
        $r = Invoke-Git -GitArgs @('rev-parse', '--verify', '-q', "$ref^{commit}") -AllowFail
        if ($r.ExitCode -ne 0) { continue }
        $sha = $r.Out.Trim()
        if ($sha -eq $head -or (Test-GitAncestor $sha $head)) { continue }
        if (Test-GitAncestor $head $sha) {
            if (Test-GitAncestor $target $sha) { $target = $sha }
            elseif (-not (Test-GitAncestor $sha $target)) { Write-Log "Remote refs $ref and $target are on different lines; keeping $target" }
        }
        else { $diverged += [pscustomobject]@{ Ref = $ref; Sha = $sha } }
    }
    return [pscustomobject]@{ Head = $head; Upstream = $upstream; Target = $target; Diverged = $diverged }
}

function Invoke-GitSync {
    $branch = (Invoke-Git -GitArgs @('symbolic-ref', '--short', '-q', 'HEAD') -AllowFail).Out.Trim()
    if (-not $branch) { Write-Log 'HEAD is detached; git sync skipped.'; return }

    if (-not $NoFetch) {
        $f = Invoke-Git -GitArgs @('fetch', 'origin', '--prune', '--quiet') -AllowFail -TimeoutSec 180
        if ($f.ExitCode -ne 0) { Write-Log "git fetch failed (offline?): $(($f.Err + $f.Out).Trim())"; return }
    }

    $t = Get-SyncTarget
    $localOnly = [int](Invoke-Git -GitArgs @('rev-list', '--count', 'HEAD', '--not', '--remotes=origin')).Out.Trim()
    Write-Log ("Branch {0} @ {1}, upstream {2}, local-only commits {3}" -f $branch, $t.Head.Substring(0, 7), $t.Upstream, $localOnly)

    foreach ($d in $t.Diverged) {
        Write-Log "Local branch and $($d.Ref) ($($d.Sha.Substring(0, 7))) have diverged; no merge."
        Send-Notice -OnceKey "diverged:$($t.Head):$($d.Sha)" -Text ("Yerel dal ($branch, $($t.Head.Substring(0, 7))) ile $($d.Ref) ($($d.Sha.Substring(0, 7))) ayrıştı. " +
            'Otomatik birleştirme yapılmadı, yerel iş korunuyor. Elle birleştirme (merge/rebase) gerekiyor.')
    }
    if ($t.Target -eq $t.Head) {
        if ($localOnly -gt 0 -and $t.Diverged.Count -eq 0) { Write-Log "$localOnly local commit(s) not on origin yet; nothing to pull." }
        else { Write-Log 'Up to date.' }
        return
    }

    $incoming = Get-GitLines @('diff', '--name-only', $t.Head, $t.Target)
    $commits = Get-GitLines @('log', '--format=%h %s', "$($t.Head)..$($t.Target)")
    Write-Log ("{0} incoming commit(s) to {1}: {2}" -f $commits.Count, $t.Target.Substring(0, 7), (($commits | Select-Object -First 5) -join ' | '))

    $dirty = Get-DirtyPaths
    $conflicts = @()
    foreach ($d in $dirty) {
        foreach ($f in $incoming) {
            if ($f -eq $d -or ($d.EndsWith('/') -and $f.StartsWith($d))) { $conflicts += $f }
        }
    }
    if ($conflicts.Count -gt 0) {
        $list = (($conflicts | Select-Object -Unique -First 5) -join ', ')
        Write-Log "Incoming commits touch locally modified files ($list); merge skipped."
        Send-Notice -OnceKey "dirty:$($t.Target)" -Text ("Origin'de $($commits.Count) yeni commit var ama yerelde kaydedilmemiş değişiklik olan dosyalara dokunuyor: $list. " +
            'Yerel iş korunuyor, birleştirme ertelendi. Değişiklikler commit edilince otomatik devam eder.')
        return
    }
    if ($dirty.Count -gt 0) { Write-Log "Working tree has $($dirty.Count) unrelated local change(s); they are kept as is." }

    $apps = Get-AffectedApps -Files $incoming -Sha $t.Target
    Write-Log ("Changed files: {0}; apps to rebuild: {1}" -f $incoming.Count, $(if ($apps.Count) { $apps -join ', ' } else { 'none' }))
    if ($script:IsDryRun) { Write-Log "Would fast-forward $branch to $($t.Target.Substring(0, 7))."; $script:DryRunApps = $apps; return }

    $m = Invoke-Git -GitArgs @('merge', '--ff-only', '--quiet', $t.Target) -AllowFail -TimeoutSec 300
    if ($m.ExitCode -ne 0) {
        $msg = ($m.Err + ' ' + $m.Out).Trim()
        Write-Log "Fast-forward failed: $msg"
        Send-Notice -OnceKey "ffail:$($t.Target)" -Text "Origin'deki yeni commit'ler alınamadı (fast-forward başarısız): $msg"
        return
    }
    Write-Log "Fast-forwarded $branch to $($t.Target.Substring(0, 7))."
    if ($apps.Count -gt 0) {
        $script:State.PendingApps = @(@($script:State.PendingApps) + $apps | Select-Object -Unique)
        $script:State.PendingSha = $t.Target
        $script:State.Remove('LastBuildFailedSha')
    }
    $script:State.LastSyncedSha = $t.Target
    $script:State.LastSyncedAt = (Get-Date).ToString('o')
}

function Invoke-PendingDeploy {
    $head = (Invoke-Git -GitArgs @('rev-parse', 'HEAD')).Out.Trim()
    $apps = @($script:State.PendingApps)
    $sha = [string]$script:State.PendingSha
    if ($ForceApps.Count -gt 0) {
        $apps = @($apps + $ForceApps | Select-Object -Unique)
        $sha = $head
    }
    if ($script:IsDryRun -and $script:DryRunApps) { $apps = @($apps + $script:DryRunApps | Select-Object -Unique) }
    if ($apps.Count -eq 0) { return }
    if (-not $sha) { $sha = $head }
    $defs = @(Get-AppDefinitions | Where-Object { $apps -contains $_.Name })
    Write-Log ("Pending apps: {0} at {1}" -f ($apps -join ', '), $sha.Substring(0, 7))

    if ($script:IsDryRun) {
        foreach ($app in $defs) {
            $running = (Get-AppProcesses $app).Count -gt 0
            Write-Log ("Would build {0} and swap into {1} (running: {2})" -f $app.Name, $app.InstallDir, $running)
            if ($app.Interactive -and $running) {
                $idle = [int](Get-UserIdleSeconds)
                Write-Log ("ERP is running; user idle {0} s (threshold {1} min), ERP in foreground: {2}" -f $idle, $script:Config.IdleMinutes, ((Get-ForegroundProcessId) -in @((Get-AppProcesses $app).Id)))
            }
        }
        return
    }

    # 1) Build what is not staged yet for this commit.
    $toBuild = @($defs | Where-Object {
            $ForceApps -contains $_.Name -or [string]$script:State.Staged[$_.Name] -ne $sha -or
            -not (Test-Path -LiteralPath (Join-Path (Join-Path $script:StagingRoot $_.Name) $_.Exe))
        })
    if ($toBuild.Count -gt 0) {
        if (-not $BuildOnly -and $ForceApps.Count -eq 0 -and [string]$script:State.LastBuildFailedSha -eq $sha) {
            Write-Log "Build for $($sha.Substring(0, 7)) already failed; waiting for a new commit."
            return
        }
        Update-BuildWorktree $sha
        foreach ($app in $toBuild) {
            $b = Invoke-AppBuild $app $sha
            if (-not $b.Ok) {
                if (-not $BuildOnly) { $script:State.LastBuildFailedSha = $sha }
                Send-Notice -OnceKey $(if ($BuildOnly) { $null } else { "buildfail:$sha" }) -Text ("$($app.Name) derlemesi başarısız ($($sha.Substring(0, 7))). Eski sürüm çalışmaya devam ediyor.`n$($b.Error)")
                return
            }
            $script:State.Staged[$app.Name] = $sha
        }
    }
    if ($BuildOnly) { Write-Log 'BuildOnly: staged builds are ready; nothing swapped or restarted.'; return }
    if (-not $script:State.PendingSince) { $script:State.PendingSince = (Get-Date).ToString('o') }
    Save-State

    # 2) Background apps are swapped right away; the ERP waits for an idle user.
    $results = @()
    foreach ($app in $defs) {
        $stage = Join-Path $script:StagingRoot $app.Name
        if ($app.Interactive -and -not (Test-ErpCanRestart $app $sha)) { continue }
        if (-not $app.Interactive) {
            $agentWatcher = Get-AppDefinitions | Where-Object { $_.Name -eq 'AgentWatcher' }
            if ($app.Name -eq 'AgentWatcher' -or (Get-AppProcesses $agentWatcher).Count -gt 0) {
                Send-Notice -Text "$($app.Name) güncelleniyor ($($sha.Substring(0, 7)))… Birkaç saniye kesinti olabilir."
                if ($app.Name -eq 'AgentWatcher') { Start-Sleep -Seconds 15 }
            }
        }
        $r = Install-AppUpdate $app $stage
        Write-Log "Install result $($app.Name): ok=$($r.Ok) $($r.Message)"
        $results += $r
        $script:State.PendingApps = @($script:State.PendingApps | Where-Object { $_ -ne $app.Name })
        if (-not $r.Ok) { $script:State.LastApplyFailed = @{ App = $app.Name; Sha = $sha; At = (Get-Date).ToString('o') } }
        Save-State
    }
    if (@($script:State.PendingApps).Count -eq 0) {
        $script:State.Remove('PendingSince'); $script:State.Remove('PendingSha'); $script:State.Remove('ErpWarnedAt')
    }
    if ($results.Count -gt 0) {
        $ok = @($results | Where-Object { $_.Ok }).Count -eq $results.Count
        $title = if ($ok) { "Güncelleme tamamlandı ($($sha.Substring(0, 7)))." } else { "Güncellemede sorun var ($($sha.Substring(0, 7)))." }
        Send-Notice -Text ($title + "`n" + (($results | ForEach-Object { '- ' + $_.Message }) -join "`n"))
    }
}

# Decides whether the ERP may be restarted now. Sends the heads-up / warnings.
function Test-ErpCanRestart($App, [string]$Sha) {
    $procs = Get-AppProcesses $App
    if ($procs.Count -eq 0) { Write-Log 'ERP is not running; swapping now.'; return $true }
    if (-not $script:Config.RestartErp) {
        Write-Log 'ERP is running and RestartErp=false; waiting until it is closed.'
        Send-Notice -OnceKey "erpwait:$Sha" -Text "ERP güncellemesi hazır ($($Sha.Substring(0, 7))). ERP kapatıldığında otomatik kurulacak (otomatik yeniden başlatma kapalı)."
        return $false
    }
    $idle = Get-UserIdleSeconds
    $foreground = (Get-ForegroundProcessId) -in @($procs.Id)
    $since = [datetime]::Parse([string]$script:State.PendingSince)
    $deadline = $since.AddHours([double]$script:Config.MaxDeferHours)
    Write-Log ("ERP running; user idle {0} s, ERP foreground {1}, deadline {2:HH:mm}" -f [int]$idle, $foreground, $deadline)

    if ($idle -ge [double]$script:Config.IdleMinutes * 60) {
        Send-Notice -Text "ERP güncelleniyor ($($Sha.Substring(0, 7)))… Bilgisayar $([int]($idle / 60)) dakikadır boşta; ERP kapatılıp yeni sürümle açılacak."
        Start-Sleep -Seconds 20
        if ((Get-UserIdleSeconds) -lt 20) { Write-Log 'User became active during the heads-up; postponing.'; return $false }
        return $true
    }
    if ((Get-Date) -lt $deadline) {
        Send-Notice -OnceKey "erpdefer:$Sha" -Text ("ERP güncellemesi hazır ($($Sha.Substring(0, 7))) ama ERP şu an kullanımda. " +
            "Bilgisayar $($script:Config.IdleMinutes) dakika boşta kalınca kurulacak; en geç $($deadline.ToString('HH:mm')) civarında uyarıyla yeniden başlatılacak.")
        return $false
    }
    $minutes = [int]$script:Config.WarningMinutes
    $warning = "ERP $minutes dakika içinde güncelleme için kapatılıp yeniden açılacak. Lütfen açık işinizi kaydedin."
    Send-Notice -Text "ERP güncelleniyor ($($Sha.Substring(0, 7)))… $warning"
    Show-DesktopWarning $warning ($minutes * 60)
    $script:State.ErpWarnedAt = (Get-Date).ToString('o')
    Save-State
    Start-Sleep -Seconds ($minutes * 60)
    return $true
}

function Invoke-AutoSyncMain {
    if (-not (Test-Path -LiteralPath $script:DataDir)) { New-Item -ItemType Directory -Force -Path $script:DataDir | Out-Null }
    try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch { }
    $mutex = New-Object System.Threading.Mutex($false, 'Local\ArsanGazERP.AutoSync')
    $owned = $false
    try {
        try { $owned = $mutex.WaitOne(0) } catch [System.Threading.AbandonedMutexException] { $owned = $true }
        if (-not $owned) { Write-Log 'Another AutoSync run is active; exiting.'; return }
        Invoke-LogRotation
        $script:Config = Get-Config
        $script:State = Get-State
        $mode = @(); if ($script:IsDryRun) { $mode += 'dry-run' }; if ($BuildOnly) { $mode += 'build-only' }; if ($ForceApps.Count) { $mode += "force=$($ForceApps -join ',')" }
        Write-Log ("=== AutoSync start {0}" -f ($mode -join ' '))
        if (-not $script:Config.Enabled) { Write-Log 'Disabled in config.json; exiting.'; return }
        try {
            if (-not $BuildOnly) { Invoke-GitSync }
            Save-State
            Invoke-PendingDeploy
            Save-State
        }
        catch {
            $msg = $_.Exception.Message
            Write-Log "ERROR: $msg`n$($_.ScriptStackTrace)"
            $key = 'error:' + (Get-Date).ToString('yyyyMMdd') + ':' + ($msg.GetHashCode())
            Send-Notice -OnceKey $key -Text "AutoSync hatası: $msg"
            Save-State
        }
        Write-Log '=== AutoSync end'
    }
    finally {
        if ($owned) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
    }
}

if (-not $LibraryOnly) { Invoke-AutoSyncMain }
