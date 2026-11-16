param([switch]$SyncWhenClosed)
$ErrorActionPreference = "Stop"

# Couch windows run with Steam off and save under user://default/<net id>/modded/. The host
# window (net id 1) is the owner's: its progress is copied into the Steam profile when the
# game closes, so unlocks, epochs, discoveries and stats carry into normal play.
# Player 2's save is a throwaway copy of the owner's progress.

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
    }
    [IO.File]::WriteAllText($seedMarker, (ConvertTo-Json $marker))
}

# Copies only when the Steam save is exactly what the couch save was seeded from: if the owner
# played normally in between, the couch save is stale and would erase that progress. It is then
# kept under the work folder instead, for manual recovery.
#
# The copy keeps the Steam file's timestamp. On launch the Steam game replaces any local save
# whose timestamp differs from the cloud copy's, and the local timestamp is the cloud's after
# every game write; a matching timestamp keeps the couch progress, which the game uploads on
# its next progress write. Run history cannot cross over: the same sync deletes local run files
# the cloud lacks, and only the game can upload one.
function Sync-HostProgressToSteam {
    if (-not (Test-Path $seedMarker)) { return }
    $marker = Get-Content $seedMarker -Raw | ConvertFrom-Json
    function Save-Marker([string]$steamHash, [string]$hostHash) {
        [IO.File]::WriteAllText($seedMarker, (ConvertTo-Json @{ steamHash = $steamHash; hostHash = $hostHash }))
    }
    $hostProgress = Join-Path (Get-CouchSaves $hostId) "progress.save"
    $steamProgress = Join-Path $steamSaves "progress.save"
    if (-not (Test-Path $hostProgress)) { return }
    $hostHash = Get-FileSha $hostProgress
    if ($hostHash -eq $marker.hostHash) { return }
    if ((Get-FileSha $steamProgress) -ne $marker.steamHash) {
        $kept = Join-Path $workDir "unsynced\progress-$(Get-Date -Format yyyyMMdd-HHmmss).save"
        New-Item -ItemType Directory -Force -Path (Split-Path $kept) | Out-Null
        Copy-Item $hostProgress $kept
        Save-Marker $marker.steamHash $hostHash
        Write-SyncLog "Steam progress changed since the couch session was seeded; couch progress was NOT copied to Steam. Kept at $kept"
        return
    }
    try { [void](ConvertFrom-Json ([IO.File]::ReadAllText($hostProgress))) }
    catch { Write-SyncLog "Couch host progress.save does not parse; not copied to Steam."; return }
    $cloudTime = (Get-Item $steamProgress).LastWriteTimeUtc
    $staged = "$steamProgress.couch.tmp"
    Copy-Item $hostProgress $staged -Force
    [IO.File]::SetLastWriteTimeUtc($staged, $cloudTime)
    Move-Item $staged $steamProgress -Force
    Save-Marker $hostHash $hostHash
    Write-SyncLog "Copied couch host progress to the Steam profile."
}

if ($SyncWhenClosed) {
    while (Get-Process -Name SlayTheSpire2 -ErrorAction SilentlyContinue) { Start-Sleep -Seconds 5 }
    Sync-HostProgressToSteam
}
