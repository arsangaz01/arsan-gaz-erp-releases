# Exercises AutoSync's git rules on a throwaway repository pair in %TEMP% ("server" pushes, "pc" syncs):
# conflicting local changes, fast-forward + change classification, and one-time divergence notices.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'AutoSync.ps1') -LibraryOnly

$root = Join-Path $env:TEMP ("autosync-gittest-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
$script:DataDir = Join-Path $root 'data'
$script:LogPath = Join-Path $script:DataDir 'autosync.log'
$script:StatePath = Join-Path $script:DataDir 'state.json'
$script:IsDryRun = $false
$script:Config = Get-Config
$script:State = @{ Notified = @{}; Staged = @{}; Backups = @{}; PendingApps = @() }
$script:Sent = New-Object System.Collections.Generic.List[string]

function Send-Notice {
    param([string]$Text, [string]$OnceKey)
    if ($OnceKey -and $script:State.Notified.ContainsKey($OnceKey)) { return }
    $script:Sent.Add($Text)
    if ($OnceKey) { $script:State.Notified[$OnceKey] = (Get-Date).ToString('o') }
}

$failures = 0
function Assert([bool]$Condition, [string]$Message) {
    if ($Condition) { Write-Host "PASS  $Message" } else { Write-Host "FAIL  $Message"; $script:failures++ }
}
function G([string]$Dir) { $r = Invoke-Git -Dir $Dir -GitArgs $args; return $r.Out.Trim() }
function Write-RepoFile([string]$Repo, [string]$Path, [string]$Text) {
    $full = Join-Path $Repo $Path
    New-Item -ItemType Directory -Force -Path (Split-Path $full) | Out-Null
    [IO.File]::WriteAllText($full, $Text)
}

try {
    $bare = Join-Path $root 'origin.git'; $server = Join-Path $root 'server'; $pc = Join-Path $root 'pc'
    New-Item -ItemType Directory -Force -Path $root | Out-Null
    $null = G $root init -q --bare -b main $bare
    $null = G $root clone -q $bare $server
    foreach ($kv in @{
            'Tools/MobileApi/ArsanGazERP.MobileApi.csproj' = '<Project><ItemGroup><Compile Include="..\..\Services\Shared.cs" /></ItemGroup></Project>'
            'Tools/MobileApi/Api.cs' = '// api'; 'Services/Shared.cs' = '// shared'; 'Services/ErpOnly.cs' = '// erp'; 'README.md' = 'readme'
        }.GetEnumerator()) { Write-RepoFile $server $kv.Key $kv.Value }
    $null = G $server -c user.name=t -c user.email=t@t add -A
    $null = G $server -c user.name=t -c user.email=t@t commit -q -m init
    $null = G $server push -q origin HEAD:main
    $null = G $root clone -q $bare $pc
    $script:RepoRoot = $pc

    # 1) Server changes a file that is modified locally -> merge skipped, notice sent once.
    Write-RepoFile $server 'Tools/MobileApi/Api.cs' '// api v2'
    $null = G $server -c user.name=t -c user.email=t@t commit -q -am 'api v2'
    $null = G $server push -q origin HEAD:main
    Write-RepoFile $pc 'Tools/MobileApi/Api.cs' '// local work in progress'
    $headBefore = G $pc rev-parse HEAD
    Invoke-GitSync
    Assert ((G $pc rev-parse HEAD) -eq $headBefore) 'conflicting local change: no merge'
    Assert ((Get-Content (Join-Path $pc 'Tools/MobileApi/Api.cs')) -eq '// local work in progress') 'local work is untouched'
    Assert ($script:Sent.Count -eq 1 -and $script:Sent[0] -match 'kaydedilmemi') 'conflict notice sent'
    Invoke-GitSync
    Assert ($script:Sent.Count -eq 1) 'conflict notice is not repeated on the next run'

    # 2) Local change is committed away (here: reverted) -> fast-forward, MobileApi pending.
    $null = G $pc checkout -- Tools/MobileApi/Api.cs
    Write-RepoFile $pc 'Services/Untracked.cs' '// unrelated untracked file'
    Invoke-GitSync
    Assert ((G $pc rev-parse HEAD) -eq (G $server rev-parse HEAD)) 'fast-forwarded to the server commit'
    Assert ((@($script:State.PendingApps) -join ',') -eq 'MobileApi') "only MobileApi pending (got: $(@($script:State.PendingApps) -join ','))"
    Assert (Test-Path (Join-Path $pc 'Services/Untracked.cs')) 'unrelated untracked file kept'

    # 3) A shared file linked into MobileApi changes -> MobileApi + ERP.
    $script:State.PendingApps = @()
    Write-RepoFile $server 'Services/Shared.cs' '// shared v2'
    $null = G $server -c user.name=t -c user.email=t@t commit -q -am 'shared v2'
    $null = G $server push -q origin HEAD:main
    Invoke-GitSync
    Assert ((@($script:State.PendingApps | Sort-Object) -join ',') -eq 'ERP,MobileApi') "shared change -> ERP + MobileApi (got: $(@($script:State.PendingApps) -join ','))"

    # 4) Local commit + server commit -> diverged, nothing merged, notice once.
    $script:State.PendingApps = @()
    Write-RepoFile $pc 'README.md' 'local commit'
    $null = G $pc -c user.name=t -c user.email=t@t commit -q -am 'local only'
    Write-RepoFile $server 'Services/ErpOnly.cs' '// erp v2'
    $null = G $server -c user.name=t -c user.email=t@t commit -q -am 'erp v2'
    $null = G $server push -q origin HEAD:main
    $headBefore = G $pc rev-parse HEAD
    $sentBefore = $script:Sent.Count
    Invoke-GitSync
    Assert ((G $pc rev-parse HEAD) -eq $headBefore) 'diverged: local branch unchanged'
    Assert ($script:Sent.Count -eq $sentBefore + 1 -and $script:Sent[$script:Sent.Count - 1] -match 'ayrıştı') 'divergence notice sent'
    Invoke-GitSync
    Invoke-GitSync
    Assert ($script:Sent.Count -eq $sentBefore + 1) 'divergence notice not repeated'
    Assert (@($script:State.PendingApps).Count -eq 0) 'nothing pending while diverged'

    # 5) Local commits that are already on top of origin (ahead only) -> nothing to pull, no notice.
    $null = G $pc reset -q --hard origin/main
    Write-RepoFile $pc 'README.md' 'ahead'
    $null = G $pc -c user.name=t -c user.email=t@t commit -q -am 'ahead only'
    $sentBefore = $script:Sent.Count
    Invoke-GitSync
    Assert ($script:Sent.Count -eq $sentBefore) 'ahead-only: no notice'
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
if ($failures -gt 0) { Write-Host "$failures check(s) failed"; exit 1 }
Write-Host 'All git checks passed.'
