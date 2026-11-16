param([switch]$SyncWhenClosed)
$ErrorActionPreference = "Stop"

# The host window is the owner's: it saves under the Steam account's modded\ folder when it
# hosts through Steam, or under user://default/1/modded/ offline. Its progress is copied into
# the Steam profile when the game closes, so unlocks, epochs, discoveries and stats carry into
# normal play. Other seats run with Steam off under user://default/<net id>/modded/; their
# saves are throwaway copies of the owner's progress.

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
$steamId = $steamAccount.Name
$steamSettings = Join-Path $steamAccount.FullName "settings.save"
$steamSettingsBackup = Join-Path $workDir "steam-settings.save"

function Get-CouchSaves([int]$netId) { Join-Path $userData "default\$netId\$couchProfile\saves" }

# A Steam-hosted couch host is the Steam account running modded, which the game keeps apart
# from normal play under modded\.
function Get-SteamHostSaves { Join-Path $steamAccount.FullName "modded\profile$profileId\saves" }

function Write-SyncLog([string]$message) {
    Write-Host $message
    Add-Content (Join-Path $logDir "couch_sync.log") "$(Get-Date -Format s) $message"
}

function Get-FileSha([string]$path) { (Get-FileHash -Algorithm SHA256 $path).Hash }

# Records the files as seeded, so a later sync can tell the host save moved and the Steam save did not.
function Save-SeedMarker([string]$hostSaves) {
    $marker = @{
        steamHash = Get-FileSha (Join-Path $steamSaves "progress.save")
        hostHash = Get-FileSha (Join-Path $hostSaves "progress.save")
        hostSaves = $hostSaves
    }
    [IO.File]::WriteAllText($seedMarker, (ConvertTo-Json $marker))
}

# The Steam-hosted couch host runs on the account's real settings.save (mods on, couch window
# layout), so the launcher keeps the owner's copy here until the session closes.
function Backup-SteamSettings { Copy-Item $steamSettings $steamSettingsBackup -Force }

function Restore-SteamSettings {
    if (-not (Test-Path $steamSettingsBackup)) { return }
    # The game falls back to settings.save.backup when settings.save fails to parse, so both get the owner's copy.
    foreach ($target in $steamSettings, "$steamSettings.backup") { Copy-Item $steamSettingsBackup $target -Force }
    Remove-Item $steamSettingsBackup
    Write-SyncLog "Restored the Steam profile's settings after a Steam-hosted couch session."
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
    $hostSaves = if ($marker.hostSaves) { $marker.hostSaves } else { Get-CouchSaves $hostId }
    function Save-Marker([string]$steamHash, [string]$hostHash) {
        [IO.File]::WriteAllText($seedMarker, (ConvertTo-Json @{ steamHash = $steamHash; hostHash = $hostHash; hostSaves = $hostSaves }))
    }
    $hostProgress = Join-Path $hostSaves "progress.save"
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
    Restore-SteamSettings
}
