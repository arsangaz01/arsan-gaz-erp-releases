<#
.SYNOPSIS
    Stores a server agent secret on the Oracle server (/etc/arsangaz/serveragent.env, 0600 root) without it ever
    appearing on screen, in the command line, in shell history or in a log. The value is typed into a hidden prompt
    and piped to /opt/arsangaz/bin/set-agent-value.sh over SSH.
.EXAMPLE
    .\Tools\ServerAgent\Set-ServerAgentValue.ps1 -Status
    .\Tools\ServerAgent\Set-ServerAgentValue.ps1 -Key TELEGRAM_BOT_TOKEN
    .\Tools\ServerAgent\Set-ServerAgentValue.ps1 -Key CLOUD_RELAY_TOKEN
.NOTES
    TELEGRAM_BOT_TOKEN: the bot token from BotFather (same bot as the PC AgentWatcher).
    CLOUD_RELAY_TOKEN: the Cloudflare Worker AGENT_API_TOKEN. If it is unknown, set a new one with
    "npx wrangler secret put AGENT_API_TOKEN" in CloudAgentWorker, then enter the same value here and in the PC
    AgentWatcher settings.
#>
[CmdletBinding(DefaultParameterSetName = 'Set')]
param(
    [Parameter(ParameterSetName = 'Set', Mandatory = $true)]
    [ValidateSet('TELEGRAM_BOT_TOKEN', 'CLOUD_RELAY_TOKEN', 'TELEGRAM_CHAT_ID', 'CLOUD_RELAY_URL')]
    [string]$Key,
    [Parameter(ParameterSetName = 'Status', Mandatory = $true)]
    [switch]$Status,
    [Parameter(ParameterSetName = 'Set')]
    [switch]$FromClipboard,
    [switch]$NoRestart,
    [string]$ServerHost = 'arsangaz@144.24.189.205',
    [string]$KeyPath = (Join-Path $env:USERPROFILE '.ssh\arsangaz_hetzner')
)

$ErrorActionPreference = 'Stop'
$ssh = @('-i', $KeyPath, '-o', 'BatchMode=yes', '-o', 'ConnectTimeout=15', $ServerHost)

if ($Status) {
    & ssh @ssh 'sudo /opt/arsangaz/bin/set-agent-value.sh --status'
    exit $LASTEXITCODE
}

if ($FromClipboard) {
    $secure = New-Object System.Security.SecureString
    foreach ($ch in [string](Get-Clipboard -Raw)) { $secure.AppendChar($ch) }
    Set-Clipboard -Value ' '
} else {
    $secure = Read-Host -Prompt "$Key (yazdiginiz gorunmez)" -AsSecureString
}
$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
try {
    $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr).Trim()
    if (-not $plain) { throw 'Bos deger girildi; hicbir sey degistirilmedi.' }
    if ($Key -eq 'TELEGRAM_BOT_TOKEN' -and $plain -notmatch '^\d{8,12}:[A-Za-z0-9_-]{35}$') {
        throw "Bot token bicimi hatali ($($plain.Length) karakter; 46 olmali). Hicbir sey degistirilmedi."
    }
    $output = $plain | & ssh @ssh "sudo /opt/arsangaz/bin/set-agent-value.sh $Key" 2>&1
    $code = $LASTEXITCODE
}
finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    $plain = $null
}

if ($code -ne 0) { throw "Sunucu degeri kabul etmedi: $($output | Select-Object -Last 1)" }
Write-Output ($output | Select-Object -Last 1)

if (-not $NoRestart) {
    & ssh @ssh 'sudo systemctl restart arsangaz-serveragent && sleep 8 && systemctl is-active arsangaz-serveragent && sudo journalctl -u arsangaz-serveragent --since "-20s" --no-pager -o cat | tail -n 6'
}
