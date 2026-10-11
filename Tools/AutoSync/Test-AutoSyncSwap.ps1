# Exercises AutoSync's swap / restart / rollback / backup-pruning path with a throwaway window app in %TEMP%.
# Does not touch publish\current, the real AutoSync state or any running ERP process.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'AutoSync.ps1') -LibraryOnly

$root = Join-Path $env:TEMP ("autosync-swaptest-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
$script:DataDir = Join-Path $root 'data'
$script:LogPath = Join-Path $script:DataDir 'autosync.log'
$script:StatePath = Join-Path $script:DataDir 'state.json'
$script:IsDryRun = $false
$script:Config = Get-Config
$script:Config.StartupCheckSec = 4
$script:Config.KeepBackups = 3
$script:State = @{ Notified = @{}; Staged = @{}; Backups = @{}; PendingApps = @() }

function New-DummyExe([string]$Dir, [string]$Version) {
    New-Item -ItemType Directory -Force -Path $Dir | Out-Null
    $source = @"
using System;
using System.Windows.Forms;
public static class AutoSyncDummy {
    [STAThread]
    public static void Main() {
        string version = "$Version";
        if (version.Equals("broken")) return;
        Form f = new Form();
        f.Text = "AutoSync dummy $Version";
        f.Opacity = 0;
        Application.Run(f);
    }
}
"@
    Add-Type -TypeDefinition $source -ReferencedAssemblies System.Windows.Forms -OutputType WindowsApplication -OutputAssembly (Join-Path $Dir 'AutoSyncDummy.exe')
    New-Item -ItemType Directory -Force -Path (Join-Path $Dir 'runtimes') | Out-Null
    [IO.File]::WriteAllText((Join-Path $Dir 'runtimes\native.txt'), $Version)
}

function Get-Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }

$failures = 0
function Assert([bool]$Condition, [string]$Message) {
    if ($Condition) { Write-Host "PASS  $Message" } else { Write-Host "FAIL  $Message"; $script:failures++ }
}

$install = Join-Path $root 'install'
$app = [pscustomobject]@{
    Name = 'Dummy'; InstallDir = $install; Exe = 'AutoSyncDummy.exe'; Files = @('AutoSyncDummy.exe')
    AllTopFiles = $false; Dirs = @('runtimes'); Arguments = ''; Interactive = $true
}
try {
    foreach ($v in 'v1', 'v2', 'broken') { New-DummyExe (Join-Path $root "stage-$v") $v }
    New-Item -ItemType Directory -Force -Path $install | Out-Null
    Copy-Item (Join-Path $root 'stage-v1\*') $install -Recurse
    $null = Start-App $app
    Start-Sleep -Seconds 2
    Assert ((Get-AppProcesses $app).Count -eq 1) 'v1 is running from the install dir'

    $r = Install-AppUpdate $app (Join-Path $root 'stage-v2')
    Assert $r.Ok "update to v2 succeeded: $($r.Message)"
    Assert ((Get-Hash (Join-Path $install $app.Exe)) -eq (Get-Hash (Join-Path $root 'stage-v2\AutoSyncDummy.exe'))) 'installed exe is v2'
    Assert ((Get-Content (Join-Path $install 'runtimes\native.txt')) -eq 'v2') 'runtimes folder was updated'
    Assert ((Get-AppProcesses $app).Count -eq 1) 'v2 was restarted'
    Assert (@(Get-ChildItem $install -Filter '*.bak-*').Count -eq 1) 'one .bak-<stamp> backup exists'

    $r = Install-AppUpdate $app (Join-Path $root 'stage-broken')
    Assert (-not $r.Ok) "broken build was rejected: $($r.Message)"
    Assert ((Get-Hash (Join-Path $install $app.Exe)) -eq (Get-Hash (Join-Path $root 'stage-v2\AutoSyncDummy.exe'))) 'v2 was restored after the failed start'
    Assert ((Get-AppProcesses $app).Count -eq 1) 'v2 is running again after rollback'

    foreach ($v in 'v1', 'v2', 'v1', 'v2') {
        Start-Sleep -Seconds 1
        $r = Install-AppUpdate $app (Join-Path $root "stage-$v")
        Assert $r.Ok "update to $v succeeded"
    }
    $baks = @(Get-ChildItem $install -Filter '*.bak-*')
    Assert ($baks.Count -eq 3) "only the last 3 backups are kept (found $($baks.Count))"

    $null = Stop-AppProcesses $app
    $r = Install-AppUpdate $app (Join-Path $root 'stage-v1')
    Assert ($r.Ok -and (Get-AppProcesses $app).Count -eq 0) 'an app that was not running is swapped but not started'
}
finally {
    foreach ($p in (Get-AppProcesses $app)) { try { $p.Kill() } catch { } }
    Start-Sleep -Seconds 1
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
if ($failures -gt 0) { Write-Host "$failures check(s) failed"; exit 1 }
Write-Host 'All swap checks passed.'
