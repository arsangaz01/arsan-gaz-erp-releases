param(
    [string]$ExecutablePath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'publish\ArsanGazERP.exe')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
$ExecutablePath = (Resolve-Path $ExecutablePath).Path
$app = Get-Process ArsanGazERP -ErrorAction SilentlyContinue |
    Where-Object Path -eq $ExecutablePath |
    Select-Object -First 1

if (-not $app) {
    $app = Start-Process -FilePath $ExecutablePath -PassThru
}

for ($attempt = 0; $attempt -lt 15 -and -not $app.MainWindowHandle; $attempt++) {
    try {
        [void]$app.WaitForInputIdle(1000)
    }
    catch {
    }
    $app.Refresh()
}

if ($app.HasExited -or -not $app.MainWindowHandle) {
    throw 'ERP main window did not start.'
}

$smallU = [string][char]0x00FC
$smallC = [string][char]0x00E7
$smallS = [string][char]0x015F
$dotlessI = [string][char]0x0131
$capitalO = [string][char]0x00D6
$smallO = [string][char]0x00F6
$minimizeName = 'K' + $smallU + $smallC + $smallU + 'lt'
$maximizeName = 'Ekran' + $dotlessI + ' kapla'
$restoreName = $capitalO + 'nceki boyuta d' + $smallO + 'n'
$logoName = 'Arsan Gaz S' + $dotlessI + 'nai ve T' + $dotlessI + 'bbi Gazlar logosu'
$readyName = 'Sistem haz' + $dotlessI + 'r'
$lastRunPrefix = 'Son ' + $smallC + 'al' + $dotlessI + $smallS + 'ma'

$window = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$app.MainWindowHandle)
$windowHandle = [IntPtr]$window.Current.NativeWindowHandle
if ($windowHandle -eq [IntPtr]::Zero) {
    throw 'UI Automation did not return a native window handle.'
}
$windowPattern = $window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
if (-not $windowPattern.Current.CanMaximize -or -not $windowPattern.Current.CanMinimize) {
    throw 'The window does not expose minimize and maximize behavior.'
}
if ($windowPattern.Current.WindowVisualState -ne [System.Windows.Automation.WindowVisualState]::Normal) {
    $windowPattern.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Normal)
}

function Find-UiElement($Root, [string]$Name) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        $Name)
    $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Wait-UiElement($Root, [string]$Name) {
    for ($attempt = 0; $attempt -lt 12; $attempt++) {
        $element = Find-UiElement $Root $Name
        if ($element) {
            return $element
        }

        [void]$app.WaitForInputIdle(250)
    }

    return $null
}

foreach ($name in @(
    $minimizeName,
    $maximizeName,
    'Kapat',
    'Arsan Gaz amblemi',
    $logoName,
    $readyName)) {
    if (-not (Find-UiElement $window $name)) {
        throw "Visible control or logo is missing: $name"
    }
}

$analyze = Find-UiElement $window 'Ajan Analizi'
if (-not $analyze) {
    throw 'The agent analysis action is missing.'
}
$analyze.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
$latestRunSummary = $null

for ($attempt = 0; $attempt -lt 30 -and -not $latestRunSummary; $attempt++) {
    [void]$app.WaitForInputIdle(500)
    $elements = $window.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($element in $elements) {
        if ($element.Current.Name.StartsWith($lastRunPrefix, [StringComparison]::Ordinal)) {
            $latestRunSummary = $element
            break
        }
    }
}

if (-not $latestRunSummary) {
    throw 'The agent run was not persisted to the dashboard history.'
}

$maximize = Find-UiElement $window $maximizeName
$maximize.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
$restore = Wait-UiElement $window $restoreName
if (-not $restore) {
    throw 'Maximize button did not expose the restore control.'
}

$restore.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
$maximize = Wait-UiElement $window $maximizeName
if (-not $maximize) {
    throw 'Restore button did not return the maximize control.'
}

$minimize = Find-UiElement $window $minimizeName
$minimize.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
if ($windowPattern.Current.WindowVisualState -ne [System.Windows.Automation.WindowVisualState]::Minimized) {
    throw 'Minimize button did not minimize the window.'
}
$windowPattern.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Normal)

$close = Find-UiElement $window 'Kapat'
$close.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
if (-not $app.WaitForExit(10000)) {
    throw 'Close button did not exit the application.'
}

Write-Output 'UI smoke test passed: persisted agent run, logo, accessible controls, maximize, restore, minimize, and close.'