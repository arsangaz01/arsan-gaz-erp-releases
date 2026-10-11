# Registers (or removes with -Unregister) the per-user "ArsanGazERP AutoSync" scheduled task:
# at logon (+3 min) and every 30 minutes, only while the user is logged on, hidden window.
param(
    [switch]$Unregister,
    [int]$IntervalMinutes = 30
)

$ErrorActionPreference = 'Stop'
$taskName = 'ArsanGazERP AutoSync'

if ($Unregister) {
    if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
        Write-Output "Removed scheduled task '$taskName'."
    }
    else { Write-Output "Scheduled task '$taskName' does not exist." }
    return
}

$launcher = Join-Path $PSScriptRoot 'AutoSync-Hidden.vbs'
if (-not (Test-Path -LiteralPath $launcher)) { throw "Launcher not found: $launcher" }
$user = "$env:USERDOMAIN\$env:USERNAME"

$action = New-ScheduledTaskAction -Execute 'wscript.exe' -Argument "//B //NoLogo `"$launcher`"" -WorkingDirectory $PSScriptRoot
$atLogon = New-ScheduledTaskTrigger -AtLogOn -User $user
$atLogon.Delay = 'PT3M'
$repeat = New-ScheduledTaskTrigger -Once -At (Get-Date).Date.AddMinutes([math]::Ceiling(((Get-Date) - (Get-Date).Date).TotalMinutes / $IntervalMinutes) * $IntervalMinutes) `
    -RepetitionInterval (New-TimeSpan -Minutes $IntervalMinutes)
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -StartWhenAvailable -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Hours 2)

Register-ScheduledTask -TaskName $taskName -Action $action -Trigger @($atLogon, $repeat) -Principal $principal -Settings $settings `
    -Description 'Arsan Gaz ERP: origin ile senkronize olur, değişen masaüstü uygulamalarını derleyip güvenli şekilde günceller (Tools\AutoSync\AutoSync.ps1).' -Force | Out-Null

$task = Get-ScheduledTask -TaskName $taskName
$info = $task | Get-ScheduledTaskInfo
Write-Output "Registered '$taskName' (state: $($task.State), next run: $($info.NextRunTime))."
