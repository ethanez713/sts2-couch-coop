param([switch]$SyncWhenClosed)
$ErrorActionPreference = "Stop"

# Couch windows run with Steam off and save under user://default/<net id>/modded/. The host
# window (net id 1) is the owner's: its progress and run history are copied into the Steam
# profile when the game closes, so unlocks, epochs, discoveries, stats and history carry
# into normal play. Player 2's save is a throwaway copy of the owner's progress.

$userData = Join-Path $env:APPDATA "SlayTheSpire2"
$logDir = Join-Path $userData "logs"
$workDir = Join-Path $env:LOCALAPPDATA "sts2-couch"
$couchProfile = "modded\profile1"
$hostId = 1
$seedMarker = Join-Path $workDir "seed.json"
New-Item -ItemType Directory -Force -Path $logDir, $workDir | Out-Null

$steamAccount = Get-ChildItem (Join-Path $userData "steam") -Directory -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $steamAccount) { throw "No Steam save folder under $userData\steam. Run the game through Steam once first." }
$profileId = (Get-Content (Join-Path $steamAccount.FullName "profile.save") -Raw | ConvertFrom-Json).last_profile_id
$steamSaves = Join-Path $steamAccount.FullName "profile$profileId\saves"

function Get-CouchSaves([int]$netId) { Join-Path $userData "default\$netId\$couchProfile\saves" }

function Write-SyncLog([string]$message) {
    Write-Host $message
    Add-Content (Join-Path $logDir "couch_sync.log") "$(Get-Date -Format s) $message"
}

function Get-FileSha([string]$path) { (Get-FileHash -Algorithm SHA256 $path).Hash }

# Records the files as seeded, so a later sync can tell the host save moved and the Steam save did not.
function Save-SeedMarker {
    $marker = @{
        steamHash = Get-FileSha (Join-Path $steamSaves "progress.save")
        hostHash = Get-FileSha (Join-Path (Get-CouchSaves $hostId) "progress.save")
        seededTicks = [DateTime]::UtcNow.Ticks
    }
    [IO.File]::WriteAllText($seedMarker, (ConvertTo-Json $marker))
}

# Copies only when the Steam save is exactly what the couch save was seeded from: if the owner
# played normally in between, the couch save is stale and would erase that progress.
function Sync-HostProgressToSteam {
    if (-not (Test-Path $seedMarker)) { return }
    $marker = Get-Content $seedMarker -Raw | ConvertFrom-Json
    $hostSaves = Get-CouchSaves $hostId
    $hostProgress = Join-Path $hostSaves "progress.save"
    $steamProgress = Join-Path $steamSaves "progress.save"
    if (-not (Test-Path $hostProgress)) { return }
    if ((Get-FileSha $hostProgress) -eq $marker.hostHash) { return }
    if ((Get-FileSha $steamProgress) -ne $marker.steamHash) {
        Write-SyncLog "Steam progress changed since the couch session was seeded; couch progress was NOT copied to Steam."
        return
    }
    Copy-Item $hostProgress $steamProgress -Force
    $steamHistory = Join-Path $steamSaves "history"
    New-Item -ItemType Directory -Force -Path $steamHistory | Out-Null
    $newRuns = Get-ChildItem (Join-Path $hostSaves "history") -File -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTimeUtc.Ticks -ge $marker.seededTicks }
    foreach ($run in $newRuns) { Copy-Item $run.FullName (Join-Path $steamHistory $run.Name) -Force }
    $copiedHash = Get-FileSha $steamProgress
    [IO.File]::WriteAllText($seedMarker, (ConvertTo-Json @{
        steamHash = $copiedHash; hostHash = $copiedHash; seededTicks = $marker.seededTicks }))
    Write-SyncLog "Copied couch host progress and $(@($newRuns).Count) run file(s) to the Steam profile."
}

if ($SyncWhenClosed) {
    while (Get-Process -Name SlayTheSpire2 -ErrorAction SilentlyContinue) { Start-Sleep -Seconds 5 }
    Sync-HostProgressToSteam
}
