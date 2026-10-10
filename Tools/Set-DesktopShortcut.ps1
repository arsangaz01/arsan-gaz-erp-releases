param(
    [Parameter(Mandatory = $true)]
    [string]$TargetPath,
    [Parameter(Mandatory = $true)]
    [string]$AgentWatcherTargetPath
)

$targets = @(
    @{
        Path = [System.IO.Path]::GetFullPath($TargetPath)
        Name = "Arsan Gaz ERP"
        Arguments = ""
    },
    @{
        Path = [System.IO.Path]::GetFullPath($AgentWatcherTargetPath)
        Name = "Arsan Gaz AgentWatcher"
        Arguments = "--background"
    }
)

foreach ($target in $targets) {
    if (-not (Test-Path -LiteralPath $target.Path -PathType Leaf)) {
        throw "Application executable was not found: $($target.Path)"
    }
}

$desktopPath = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
if ([string]::IsNullOrWhiteSpace($desktopPath)) {
    throw "The current user's desktop directory could not be resolved."
}

if (-not (Test-Path -LiteralPath $desktopPath -PathType Container)) {
    New-Item -ItemType Directory -Path $desktopPath -Force | Out-Null
}

$shell = New-Object -ComObject WScript.Shell
try {
    foreach ($target in $targets) {
        $shortcutPath = Join-Path $desktopPath "$($target.Name).lnk"
        $shortcut = $shell.CreateShortcut($shortcutPath)
        try {
            $shortcut.TargetPath = $target.Path
            $shortcut.Arguments = $target.Arguments
            $shortcut.WorkingDirectory = Split-Path -Parent $target.Path
            $shortcut.IconLocation = "$($target.Path),0"
            $shortcut.Description = $target.Name
            $shortcut.Save()
        }
        finally {
            [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) | Out-Null
        }
    }
}
finally {
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null
}
