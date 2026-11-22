param(
    [string]$Player1 = "Player 1",
    [string]$Player2 = "Player 2",
    # mice: each window follows its own pointing device (Pointer1 for the host, Pointer2 for the
    # client): "touchpad", "mouse" (any non-touchpad), or text from the device's path.
    # touch: one shared mouse plus the touchscreen, with the Touch-STS2 mod.
    [ValidateSet("mice", "touch")] [string]$InputMode = "mice",
    [string]$Pointer1 = "touchpad",
    [string]$Pointer2 = "mouse",
    # Raw touchpad movement lacks the acceleration Windows gives its cursor, so it starts faster.
    [double]$Speed1 = 2.0,
    [double]$Speed2 = 1.0,
    # The host opens a Steam lobby friends can join whenever Steam is running; -Offline keeps
    # the session on this PC.
    [switch]$Offline,
    # Empty keeps the game's default (Direct3D 12 here).
    [ValidateSet("", "d3d12", "vulkan", "opengl3")] [string]$Renderer = "",
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2"
)
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "couch-saves.ps1")

if (Get-Process -Name SlayTheSpire2 -ErrorAction SilentlyContinue) {
    throw "Slay the Spire 2 is already running. Close it first; the host needs port 33771 free."
}

# Screen geometry must be in physical pixels, which is what the game's window settings use.
Add-Type -Name User32 -Namespace Couch -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();'
[void][Couch.User32]::SetProcessDPIAware()
Add-Type -AssemblyName System.Windows.Forms

$exe = Join-Path $GameDir "SlayTheSpire2.exe"
$steamApps = Split-Path (Split-Path $GameDir)

# The game loads mods from a folder next to its exe. Couch windows run with Steam off, so
# Workshop items are not read from Steam; they are copied in like local mods. Normal Steam
# play does not load these: mods stay off there until enabled in its own settings.
$modsDir = Join-Path $GameDir "mods"
$couchMod = Join-Path $PSScriptRoot "mod\bin\CouchCoop"
if (-not (Test-Path (Join-Path $couchMod "CouchCoop.dll"))) {
    throw "Couch mod not built. In WSL run: cd ~/sts2_multi/mod && dotnet build -c Release"
}
$workshopMods = @{ "3804990566" = "TouchSts2" }

function Copy-Mod([string]$from, [string]$name) {
    $to = Join-Path $modsDir $name
    if (Test-Path $to) { Remove-Item -Recurse -Force $to }
    New-Item -ItemType Directory -Force -Path $to | Out-Null
    Copy-Item -Recurse -Force (Join-Path $from "*") $to
}
Copy-Mod $couchMod "CouchCoop"
foreach ($item in $workshopMods.Keys) {
    $from = Join-Path $steamApps "workshop\content\2868840\$item"
    $to = Join-Path $modsDir $workshopMods[$item]
    if ($InputMode -ne "touch") { if (Test-Path $to) { Remove-Item -Recurse -Force $to } }
    elseif (Test-Path $from) { Copy-Mod $from $workshopMods[$item] }
    else { Write-Warning "Workshop item $item ($($workshopMods[$item])) is not downloaded; subscribe to it in Steam." }
}

# Net ids are fixed by the game's developer mode: host is 1, clients are 1000, 2000, 3000.
# With Steam off, each id gets its own save folder, user://default/<id>; with mods loaded the
# game keeps profiles under modded/.
$players = @(
    @{ Id = 1;    Name = $Player1; Log = "couch_host.log";   Pointer = $Pointer1; Speed = $Speed1 },
    @{ Id = 1000; Name = $Player2; Log = "couch_client.log"; Pointer = $Pointer2; Speed = $Speed2 }
)

# The game reads display names from ./mp_names.json in its working directory.
# WriteAllText, not Set-Content: Windows PowerShell 5 prepends a BOM that the game's JSON
# parser rejects.
$namesJson = ConvertTo-Json @($players | ForEach-Object { @{ net_id = $_.Id; name = $_.Name } })
[IO.File]::WriteAllText((Join-Path $workDir "mp_names.json"), $namesJson)

$screens = [System.Windows.Forms.Screen]::AllScreens
$primary = [array]::IndexOf($screens, [System.Windows.Forms.Screen]::PrimaryScreen)

function Get-Layout([int]$slot) {
    if ($screens.Count -ge 2) {
        $display = if ($slot -eq 0) { $primary } else { @(0..($screens.Count - 1) | Where-Object { $_ -ne $primary })[0] }
        return @{ fullscreen = $true; target_display = $display; window_position = @{ X = -1; Y = -1 }; window_size = @{ X = 1920; Y = 1080 } }
    }
    # One screen: two windows side by side. Position is relative to the display and excludes
    # the title bar, so leave room for it above the window.
    $work = $screens[0].WorkingArea
    $titleBar = [System.Windows.Forms.SystemInformation]::CaptionHeight + 2 * [System.Windows.Forms.SystemInformation]::FrameBorderSize.Height
    $width = [int]($work.Width / 2)
    return @{
        fullscreen = $false; target_display = 0
        window_position = @{ X = $work.X + $slot * $width; Y = $work.Y + $titleBar }
        window_size = @{ X = $width; Y = $work.Height - $titleBar }
    }
}

# Each window starts from the owner's progress and settings, copied fresh on every launch.
# The host's progress flows back to the Steam profile when the game closes
# (couch-saves.ps1), and any session that ended unsynced is flushed before seeding. Player 2's
# earnings are discarded, except co-op ascension (Merge-CoopAscension).
function Get-ProgressInt([string]$path, [string]$field) {
    if (-not (Test-Path $path)) { return $null }
    $m = [regex]::Match([IO.File]::ReadAllText($path), "`"$field`"\s*:\s*(\d+)")
    if ($m.Success) { return [int]$m.Groups[1].Value } else { return $null }
}

# Patched as text rather than round-tripped through ConvertTo-Json, which in PowerShell 5
# rewrites the rest of this 200 KB file (escaping, number formats) for no reason.
function Set-ProgressInt([string]$path, [string]$field, [int]$value) {
    $text = [IO.File]::ReadAllText($path)
    $text = [regex]::Replace($text, "(`"$field`"\s*:\s*)\d+", "`${1}$value")
    [IO.File]::WriteAllText($path, $text)
}

# The lobby caps ascension at the lowest max among its players, so both windows get the
# same value: the highest the Steam profile or either couch save has reached.
function Merge-CoopAscension([string[]]$couchProgress, [string]$steamProgress) {
    $sources = @($steamProgress) + $couchProgress
    $max = ($sources | ForEach-Object { Get-ProgressInt $_ "max_multiplayer_ascension" } | Measure-Object -Maximum).Maximum
    $preferred = Get-ProgressInt $couchProgress[0] "preferred_multiplayer_ascension"
    if ($null -eq $preferred) { $preferred = Get-ProgressInt $steamProgress "preferred_multiplayer_ascension" }
    if ($null -eq $max) { return }
    return @{ max = [int]$max; preferred = [Math]::Min([int]$preferred, [int]$max) }
}
Sync-HostProgressToSteam
Restore-SteamSettings

function Test-RunSave([string]$saves) {
    $save = Join-Path $saves "current_run_mp.save"
    # The game resumes from the .backup alone when a crash cut its save off mid-rename.
    (Test-Path $save) -or (Test-Path "$save.backup")
}

# A saved run records the host's net id (1 offline, the Steam id when hosted through Steam), so
# it resumes only in the mode that started it.
$offlineHostSaves = Get-CouchSaves 1
$steamHostSaves = Get-SteamHostSaves
$steamRunning = [bool](Get-Process -Name steam -ErrorAction SilentlyContinue)
$finishOfflineRun = (Test-RunSave $offlineHostSaves) -and -not (Test-RunSave $steamHostSaves)
$useSteam = $steamRunning -and -not $Offline -and -not $finishOfflineRun
if ($useSteam) {
    Write-Host "Hosting through Steam: friends can join from the game's Multiplayer menu or your invite."
} elseif ($steamRunning -and $finishOfflineRun -and -not $Offline) {
    Write-Host "Resuming the couch run saved offline; friends can join from the next run."
} elseif (-not $steamRunning -and (Test-RunSave $steamHostSaves)) {
    Write-Warning "A Steam-hosted couch run is saved; start Steam and relaunch to resume it. Playing offline instead."
}
$hostSaves = if ($useSteam) { $steamHostSaves } else { $offlineHostSaves }

# The offline windows load mods only from the mods folder; a Steam-on window would also load
# every subscribed Workshop mod, so those are switched off to keep both modes identical.
function Get-WorkshopModsOff {
    $workshop = Join-Path $steamApps "workshop\content\2868840"
    if (-not (Test-Path $workshop)) { return }
    Get-ChildItem $workshop -Directory | ForEach-Object { Get-ChildItem $_.FullName -Filter *.json -File } |
        ForEach-Object { try { (Get-Content $_.FullName -Raw | ConvertFrom-Json).id } catch { $null } } |
        Where-Object { $_ } | Sort-Object -Unique |
        ForEach-Object { @{ id = $_; source = "steam_workshop"; is_enabled = $false } }
}

$seatSaves = @(for ($slot = 0; $slot -lt $players.Count; $slot++) {
    if ($slot -eq 0) { $hostSaves } else { Get-CouchSaves $players[$slot].Id }
})
$couchProgress = @($seatSaves | ForEach-Object { Join-Path $_ "progress.save" })
$coopAscension = Merge-CoopAscension $couchProgress (Join-Path $steamSaves "progress.save")

if ($useSteam) {
    Backup-SteamSettings
    # The modded profile picks its profile slot from its own profile.save.
    New-Item -ItemType Directory -Force -Path (Join-Path $steamAccount.FullName "modded") | Out-Null
    Copy-Item (Join-Path $steamAccount.FullName "profile.save") (Join-Path $steamAccount.FullName "modded\profile.save") -Force
}

for ($slot = 0; $slot -lt $players.Count; $slot++) {
    $saves = $seatSaves[$slot]
    New-Item -ItemType Directory -Force -Path $saves | Out-Null
    foreach ($file in "progress.save", "prefs.save") {
        $source = Join-Path $steamSaves $file
        if (Test-Path $source) { Copy-Item $source (Join-Path $saves $file) -Force }
    }
    if ($coopAscension) {
        Set-ProgressInt $couchProgress[$slot] "max_multiplayer_ascension" $coopAscension.max
        Set-ProgressInt $couchProgress[$slot] "preferred_multiplayer_ascension" $coopAscension.preferred
    }
    $steamHosted = $useSteam -and $slot -eq 0
    $settings = Get-Content $(if ($steamHosted) { $steamSettingsBackup } else { $steamSettings }) -Raw | ConvertFrom-Json
    $layout = Get-Layout $slot
    # The game caps an unfocused window at 30 FPS unless it is in a multiplayer session, but it
    # decides when focus is lost: the window that loses focus on the main menu stays capped.
    $layout.limit_fps_in_background = $false
    $modList = @(if ($steamHosted) { Get-WorkshopModsOff })
    $layout.mod_settings = @{ mods_enabled = $true; mod_list = $modList }
    foreach ($key in $layout.Keys) { $settings | Add-Member -Force -NotePropertyName $key -NotePropertyValue $layout[$key] }
    $target = if ($steamHosted) { $steamSettings } else { Join-Path $userData "default\$($players[$slot].Id)\settings.save" }
    [IO.File]::WriteAllText($target, (ConvertTo-Json $settings -Depth 10))
}

Save-SeedMarker $hostSaves

# Offline, --fastmp is the game's own loopback mode: ENet on 127.0.0.1:33771 instead of Steam
# networking, every window with Steam off (see above). Hosted through Steam, the couch mod opens
# a Steam lobby and the same loopback port for the other couch windows (--couch-host). Either
# way the host resumes the couch run if one is saved, otherwise opens character select for a
# new standard run.
$resume = Test-RunSave $hostSaves

foreach ($slot in 0..($players.Count - 1)) {
    $p = $players[$slot]
    $gameArgs = @("--log-file", (Join-Path $logDir $p.Log), "--couch-seat", "$slot")
    if ($slot -eq 0 -and $useSteam) {
        $gameArgs += @("--couch-host", $(if ($resume) { "load" } else { "new" }), "--couch-seats", "$($players.Count)")
    } else {
        $gameArgs += @("--force-steam", "off", "--clientId", "$($p.Id)",
                       "--fastmp", $(if ($slot -gt 0) { "join" } elseif ($resume) { "load" } else { "host_standard" }))
        if ($useSteam) { $gameArgs += @("--couch-host-id", $steamId) }
    }
    if ($Renderer) { $gameArgs += @("--rendering-driver", $Renderer) }
    if ($InputMode -eq "mice") {
        $gameArgs += @("--couch-pointer", "`"$($p.Pointer)`"",
                       "--couch-pointer-speed", $p.Speed.ToString([Globalization.CultureInfo]::InvariantCulture))
    }
    # Launching the exe directly (not through Steam) needs the app id in the environment, or
    # SteamAPI init fails and the game quits.
    if ($slot -eq 0 -and $useSteam) { $env:SteamAppId = "2868840" }
    Start-Process -FilePath $exe -WorkingDirectory $workDir -ArgumentList $gameArgs
    Remove-Item Env:SteamAppId -ErrorAction SilentlyContinue
    # A client's join times out after 10 s, so the host must be listening first.
    if ($slot -eq 0) { Start-Sleep -Seconds 12 }
}

# The launcher exits now; a hidden copy of couch-saves.ps1 waits for the windows to close.
Start-Process powershell.exe -WindowStyle Hidden -ArgumentList @(
    "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$PSScriptRoot\couch-saves.ps1`"", "-SyncWhenClosed")
