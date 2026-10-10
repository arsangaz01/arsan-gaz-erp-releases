param(
    [Parameter(Mandatory = $true)]
    [string]$WatcherExecutablePath,
    [Parameter(Mandatory = $true)]
    [string]$ErpExecutablePath
)

$ErrorActionPreference = 'Stop'

$watcherExecutable = [System.IO.Path]::GetFullPath($WatcherExecutablePath)
$erpExecutable = [System.IO.Path]::GetFullPath($ErpExecutablePath)
foreach ($executable in @($watcherExecutable, $erpExecutable)) {
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "Application executable was not found: $executable"
    }
}

$startupDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::Startup)
if ([string]::IsNullOrWhiteSpace($startupDirectory)) {
    throw 'The current user startup directory could not be resolved.'
}

$startupScript = Join-Path $startupDirectory 'ArsanGazERP-AgentWatcher.cmd'
if (-not (Test-Path -LiteralPath $startupScript -PathType Leaf)) {
    Write-Output 'Automatic startup is not configured; no startup script was changed.'
    return
}

$lines = [System.IO.File]::ReadAllLines($startupScript)
$watcherLineCount = 0
$erpLineCount = 0
for ($index = 0; $index -lt $lines.Length; $index++) {
    if ($lines[$index] -match '^\s*set\s+"WATCHER_EXE=') {
        $lines[$index] = "set `"WATCHER_EXE=$watcherExecutable`""
        $watcherLineCount++
    }
    elseif ($lines[$index] -match '^\s*set\s+"ERP_EXE=') {
        $lines[$index] = "set `"ERP_EXE=$erpExecutable`""
        $erpLineCount++
    }
}

if ($watcherLineCount -ne 1) {
    throw "Expected one Watcher executable entry in $startupScript; found $watcherLineCount."
}
if ($erpLineCount -gt 1) {
    throw "Expected at most one ERP executable entry in $startupScript; found $erpLineCount."
}

[System.IO.File]::WriteAllLines($startupScript, $lines, [System.Text.Encoding]::ASCII)
Write-Output "Updated automatic startup executable paths in $startupScript."
