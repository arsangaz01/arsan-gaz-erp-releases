# Queues a message for the authorized Telegram chat; the running AgentWatcher sends it.
param([Parameter(Mandatory = $true)][string]$Path)

$ErrorActionPreference = 'Stop'
$data = Join-Path $env:LOCALAPPDATA 'ArsanGazERP\AgentWatcherData'
$utf8 = New-Object System.Text.UTF8Encoding($false)
$settings = [IO.File]::ReadAllText((Join-Path $data 'settings.json'), $utf8) | ConvertFrom-Json
[long]$chatId = 0
if (-not [long]::TryParse([string]$settings.TelegramChatId, [ref]$chatId) -or $chatId -eq 0) {
    throw 'TelegramChatId is not configured in AgentWatcher settings.'
}

$text = [IO.File]::ReadAllText((Resolve-Path $Path), $utf8).Trim()
if ($text.Length -eq 0) { throw 'Message is empty.' }

$outbox = Join-Path $data 'TelegramOutbox'
New-Item -ItemType Directory -Force -Path $outbox | Out-Null
$id = 'cursor-ide-' + [guid]::NewGuid().ToString('N')
$now = (Get-Date).ToUniversalTime().ToString('o')
$item = [ordered]@{
    Id = $id
    Kind = 'telegram-copilot-direct-response'
    TaskId = $id
    ChatId = $chatId
    Report = $text
    CreatedUtc = $now
    NextAttemptUtc = $now
    Attempts = 0
    DeliveredChunkCount = 0
    Receipts = @()
    LastError = $null
} | ConvertTo-Json
$destination = Join-Path $outbox "$id.json"
[IO.File]::WriteAllText("$destination.tmp", $item, $utf8)
Move-Item -LiteralPath "$destination.tmp" -Destination $destination

$journal = Join-Path $data 'cursor-journal.md'
[IO.File]::AppendAllText($journal, "`r`n## $((Get-Date).ToString('yyyy-MM-dd HH:mm')) - IDE sohbeti -> Telegram`r`n$text`r`n", $utf8)
Write-Output "QUEUED $id"
