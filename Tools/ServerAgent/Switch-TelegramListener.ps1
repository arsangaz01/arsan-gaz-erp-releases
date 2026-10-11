<#
.SYNOPSIS
    Moves the single Telegram getUpdates consumer (and the 7/24 Cloudflare relay heartbeat) between the PC
    AgentWatcher and the Oracle server agent. Exactly one side listens at any time:
      -To Server: stop AgentWatcher, set TelegramPollingDisabled=true (PC keeps sending in send-only mode), copy the
                  update offset to the server, enable SERVERAGENT_TELEGRAM_POLLING, start AgentWatcher again.
      -To Pc:     disable server polling first, copy the offset back, clear the flag and restart AgentWatcher.
    Refuses -To Server until the bot token and the relay token are stored on the server (Set-ServerAgentValue.ps1).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Server', 'Pc')]
    [string]$To,
    [string]$ServerHost = 'arsangaz@144.24.189.205',
    [string]$KeyPath = (Join-Path $env:USERPROFILE '.ssh\arsangaz_hetzner')
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$watcherDir = Join-Path $repo 'publish\current\AgentWatcher'
$watcherExe = Join-Path $watcherDir 'ArsanGazERP.AgentWatcher.exe'
$settingsPath = Join-Path $env:LOCALAPPDATA 'ArsanGazERP\AgentWatcherData\settings.json'
$localOffset = Join-Path $env:LOCALAPPDATA 'ArsanGazERP\AgentWatcher\telegram-offset.txt'
$serverOffset = '/var/lib/arsangaz-data/prod/ArsanGazERP/AgentWatcher/telegram-offset.txt'
$ssh = @('-i', $KeyPath, '-o', 'BatchMode=yes', '-o', 'ConnectTimeout=15', $ServerHost)
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Invoke-Server([string]$Command, [string]$InputText = $null) {
    if ($null -ne $InputText) { $out = $InputText | & ssh @ssh $Command 2>&1 } else { $out = & ssh @ssh $Command 2>&1 }
    if ($LASTEXITCODE -ne 0) { throw "Sunucu komutu basarisiz ($LASTEXITCODE): $($out | Select-Object -Last 1)" }
    return @($out | ForEach-Object { "$_" })
}

function Get-Watcher { @(Get-Process -Name 'ArsanGazERP.AgentWatcher' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $watcherExe }) }

function Stop-Watcher {
    foreach ($p in Get-Watcher) { try { $p.Kill() } catch { } }
    for ($i = 0; $i -lt 20 -and (Get-Watcher).Count -gt 0; $i++) { Start-Sleep -Seconds 1 }
    if ((Get-Watcher).Count -gt 0) { throw 'AgentWatcher durdurulamadi.' }
}

function Set-PollingDisabled([bool]$Disabled) {
    $settings = [IO.File]::ReadAllText($settingsPath, $utf8) | ConvertFrom-Json
    if ($settings.PSObject.Properties['TelegramPollingDisabled']) { $settings.TelegramPollingDisabled = $Disabled }
    else { $settings | Add-Member -NotePropertyName TelegramPollingDisabled -NotePropertyValue $Disabled }
    $temporary = "$settingsPath.switch.tmp"
    [IO.File]::WriteAllText($temporary, ($settings | ConvertTo-Json -Depth 20), $utf8)
    Copy-Item -LiteralPath $settingsPath -Destination "$settingsPath.bak-switch" -Force
    Move-Item -LiteralPath $temporary -Destination $settingsPath -Force
}

function Get-ServerStatus {
    $json = (Invoke-Server 'sudo cat /var/lib/arsangaz-data/prod/ArsanGazERP/ServerAgent/status.json') -join "`n"
    return $json | ConvertFrom-Json
}

if ($To -eq 'Server') {
    $secrets = Invoke-Server 'sudo /opt/arsangaz/bin/set-agent-value.sh --status'
    foreach ($required in 'TELEGRAM_BOT_TOKEN=set', 'TELEGRAM_CHAT_ID=set', 'CLOUD_RELAY_URL=set', 'CLOUD_RELAY_TOKEN=set') {
        if ($secrets -notcontains $required) {
            throw "Sunucuda eksik ayar: $($required -replace '=set$', ''). Once Set-ServerAgentValue.ps1 ile girin; gecis yapilmadi."
        }
    }

    Write-Output 'AgentWatcher durduruluyor (PC dinlemeyi birakir)...'
    Stop-Watcher
    try {
        Set-PollingDisabled $true
        if (Test-Path -LiteralPath $localOffset) {
            $offset = [IO.File]::ReadAllText($localOffset).Trim()
            if ($offset -match '^\d+$') {
                Invoke-Server "mkdir -p `$(dirname $serverOffset) && tr -d '\r\n' > $serverOffset.tmp && mv -f $serverOffset.tmp $serverOffset" $offset | Out-Null
            }
        }
        Invoke-Server 'sudo /opt/arsangaz/bin/set-agent-value.sh SERVERAGENT_TELEGRAM_POLLING' '1' | Out-Null
        Invoke-Server 'sudo systemctl restart arsangaz-serveragent' | Out-Null
    }
    finally {
        Start-Process -FilePath $watcherExe -ArgumentList '--background' -WorkingDirectory $watcherDir | Out-Null
    }

    Start-Sleep -Seconds 20
    $status = Get-ServerStatus
    Write-Output "Sunucu: dinleme=$($status.polling) bagli=$($status.pollingConnected) role=$($status.relay)"
    if (-not $status.pollingConnected) { Write-Warning 'Sunucu dinlemesi henuz bagli gorunmuyor; journalctl -u arsangaz-serveragent kontrol edin. Geri almak icin: -To Pc' }
}
else {
    Invoke-Server 'sudo /opt/arsangaz/bin/set-agent-value.sh SERVERAGENT_TELEGRAM_POLLING' '0' | Out-Null
    Invoke-Server 'sudo systemctl restart arsangaz-serveragent' | Out-Null
    $offset = (Invoke-Server "cat $serverOffset 2>/dev/null || true" | Select-Object -Last 1)
    Stop-Watcher
    try {
        if ($offset -match '^\d+$') { [IO.File]::WriteAllText($localOffset, $offset, $utf8) }
        Set-PollingDisabled $false
    }
    finally {
        Start-Process -FilePath $watcherExe -ArgumentList '--background' -WorkingDirectory $watcherDir | Out-Null
    }
    Write-Output 'Telegram dinleme PC AgentWatcher''a geri alindi.'
}
