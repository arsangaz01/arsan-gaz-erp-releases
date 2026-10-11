# Registers (or removes with -Unregister) the per-user "ArsanGazERP ServerInboxSync" scheduled task: starts the
# long-running Sync-ServerInbox.ps1 loop at logon and re-checks every 15 minutes (a second instance exits at once).
param([switch]$Unregister)

$ErrorActionPreference = 'Stop'
$taskName = 'ArsanGazERP ServerInboxSync'

if ($Unregister) {
    if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
        Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
        Write-Output "Removed scheduled task '$taskName'."
    }
    else { Write-Output "Scheduled task '$taskName' does not exist." }
    return
}

$launcher = Join-Path $PSScriptRoot 'Sync-ServerInbox-Hidden.vbs'
if (-not (Test-Path -LiteralPath $launcher)) { throw "Launcher not found: $launcher" }
$user = "$env:USERDOMAIN\$env:USERNAME"

$action = New-ScheduledTaskAction -Execute 'wscript.exe' -Argument "//B //NoLogo `"$launcher`"" -WorkingDirectory $PSScriptRoot
$atLogon = New-ScheduledTaskTrigger -AtLogOn -User $user
$atLogon.Delay = 'PT1M'
$repeat = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 15)
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -StartWhenAvailable -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero)

Register-ScheduledTask -TaskName $taskName -Action $action -Trigger @($atLogon, $repeat) -Principal $principal -Settings $settings `
    -Description 'Arsan Gaz ERP: sunucudaki Cursor kuyrugunu (telefon/Telegram istekleri) PC ile esitler (Tools\CursorIdeBridge\Sync-ServerInbox.ps1).' -Force | Out-Null

$task = Get-ScheduledTask -TaskName $taskName
Write-Output "Registered '$taskName' (state: $($task.State))."
