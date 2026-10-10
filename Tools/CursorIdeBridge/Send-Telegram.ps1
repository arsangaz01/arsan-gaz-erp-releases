# Queues a message (and optional files) for the authorized Telegram chat; the running AgentWatcher sends it.
# Images up to 10 MB arrive as photos, other files up to 50 MB as documents.
param(
    [Parameter(Mandatory = $true)][string]$Path,
    [string[]]$Files = @()
)

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

$filePaths = @()
if ($Files.Count -gt 0) {
    $staging = Join-Path (Join-Path $data 'TelegramFiles') $id
    New-Item -ItemType Directory -Force -Path $staging | Out-Null
    foreach ($file in $Files) {
        $source = Get-Item -LiteralPath $file
        if ($source.Length -eq 0 -or $source.Length -gt 50MB) { throw "File is empty or larger than 50 MB: $file" }
        $target = Join-Path $staging $source.Name
        Copy-Item -LiteralPath $source.FullName -Destination $target
        $filePaths += $target
    }
}
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
    FilePaths = @($filePaths)
    DeliveredFileCount = 0
    LastError = $null
} | ConvertTo-Json
$destination = Join-Path $outbox "$id.json"
[IO.File]::WriteAllText("$destination.tmp", $item, $utf8)
Move-Item -LiteralPath "$destination.tmp" -Destination $destination

$journal = Join-Path $data 'cursor-journal.md'
[IO.File]::AppendAllText($journal, "`r`n## $((Get-Date).ToString('yyyy-MM-dd HH:mm')) - IDE sohbeti -> Telegram`r`n$text`r`n", $utf8)
Write-Output "QUEUED $id"
