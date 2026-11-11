param(
    [string]$Player1 = "Player 1",
    [string]$Player2 = "Player 2",
    # mice: each window follows its own pointing device (Pointer1 for the host, Pointer2 for the
    # client): "touchpad", "mouse" (any non-touchpad), or text from the device's path.
    # touch: one shared mouse plus the touchscreen, with the Touch-STS2 mod.
    [ValidateSet("mice", "touch")] [string]$InputMode = "mice",
    [string]$Pointer1 = "touchpad",
    [string]$Pointer2 = "mouse",
    [double]$Speed1 = 1.0,
    [double]$Speed2 = 1.0,
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2"
)
$ErrorActionPreference = "Stop"

if (Get-Process -Name SlayTheSpire2 -ErrorAction SilentlyContinue) {
    throw "Slay the Spire 2 is already running. Close it first; the host needs port 33771 free."
}

# Screen geometry must be in physical pixels, which is what the game's window settings use.
Add-Type -Name User32 -Namespace Couch -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();'
[void][Couch.User32]::SetProcessDPIAware()
Add-Type -AssemblyName System.Windows.Forms

$exe = Join-Path $GameDir "SlayTheSpire2.exe"
$steamApps = Split-Path (Split-Path $GameDir)
$userData = Join-Path $env:APPDATA "SlayTheSpire2"
$logDir = Join-Path $userData "logs"
$workDir = Join-Path $env:LOCALAPPDATA "sts2-couch"
New-Item -ItemType Directory -Force -Path $logDir, $workDir | Out-Null

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

# Couch runs never touch the Steam profile, so couch and online co-op saves cannot collide.
# Each window starts from the owner's progress and settings, copied fresh on every launch;
# nothing earned in a couch run flows back. The exception is co-op ascension, which couch
# runs earn and keep (Merge-CoopAscension).
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
$steamAccount = Get-ChildItem (Join-Path $userData "steam") -Directory -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $steamAccount) { throw "No Steam save folder under $userData\steam. Run the game through Steam once first." }
$profileId = (Get-Content (Join-Path $steamAccount.FullName "profile.save") -Raw | ConvertFrom-Json).last_profile_id
$steamSaves = Join-Path $steamAccount.FullName "profile$profileId\saves"

$couchProfile = "modded\profile1"
$couchProgress = @($players | ForEach-Object { Join-Path $userData "default\$($_.Id)\$couchProfile\saves\progress.save" })
$coopAscension = Merge-CoopAscension $couchProgress (Join-Path $steamSaves "progress.save")

for ($slot = 0; $slot -lt $players.Count; $slot++) {
    $folder = Join-Path $userData "default\$($players[$slot].Id)"
    $saves = Join-Path $folder "$couchProfile\saves"
    New-Item -ItemType Directory -Force -Path $saves | Out-Null
    foreach ($file in "progress.save", "prefs.save") {
        $source = Join-Path $steamSaves $file
        if (Test-Path $source) { Copy-Item $source (Join-Path $saves $file) -Force }
    }
    if ($coopAscension) {
        Set-ProgressInt $couchProgress[$slot] "max_multiplayer_ascension" $coopAscension.max
        Set-ProgressInt $couchProgress[$slot] "preferred_multiplayer_ascension" $coopAscension.preferred
    }
    $settings = Get-Content (Join-Path $steamAccount.FullName "settings.save") -Raw | ConvertFrom-Json
    $layout = Get-Layout $slot
    # The game caps an unfocused window at 30 FPS unless it is in a multiplayer session, but it
    # decides when focus is lost: the window that loses focus on the main menu stays capped.
    $layout.limit_fps_in_background = $false
    $layout.mod_settings = @{ mods_enabled = $true; mod_list = @() }
    foreach ($key in $layout.Keys) { $settings | Add-Member -Force -NotePropertyName $key -NotePropertyValue $layout[$key] }
    [IO.File]::WriteAllText((Join-Path $folder "settings.save"), (ConvertTo-Json $settings -Depth 10))
}

# --fastmp is the game's own loopback mode: ENet on 127.0.0.1:33771 instead of Steam
# networking. Both windows run with Steam off (see above). The host resumes the couch run
# if one is saved, otherwise opens character select for a new standard run.
$hostSave = Join-Path $userData "default\1\$couchProfile\saves\current_run_mp.save"
$hostMode = if (Test-Path $hostSave) { "load" } else { "host_standard" }

foreach ($slot in 0..($players.Count - 1)) {
    $p = $players[$slot]
    $gameArgs = @("--force-steam", "off", "--clientId", "$($p.Id)", "--log-file", (Join-Path $logDir $p.Log),
                  "--fastmp", $(if ($slot -eq 0) { $hostMode } else { "join" }))
    if ($InputMode -eq "mice") {
        $gameArgs += @("--couch-pointer", "`"$($p.Pointer)`"",
                       "--couch-pointer-speed", $p.Speed.ToString([Globalization.CultureInfo]::InvariantCulture))
    }
    Start-Process -FilePath $exe -WorkingDirectory $workDir -ArgumentList $gameArgs
    # A client's join times out after 10 s, so the host must be listening first.
    if ($slot -eq 0) { Start-Sleep -Seconds 12 }
}
