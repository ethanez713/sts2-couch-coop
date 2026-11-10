param(
    [string]$Player1 = "Player 1",
    [string]$Player2 = "Player 2",
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2"
)
$ErrorActionPreference = "Stop"

if (Get-Process -Name SlayTheSpire2 -ErrorAction SilentlyContinue) {
    throw "Slay the Spire 2 is already running. Close it first; the host needs port 33771 free."
}

$exe = Join-Path $GameDir "SlayTheSpire2.exe"
$userData = Join-Path $env:APPDATA "SlayTheSpire2"
$logDir = Join-Path $userData "logs"
$workDir = Join-Path $env:LOCALAPPDATA "sts2-couch"
New-Item -ItemType Directory -Force -Path $logDir, $workDir | Out-Null

# Net ids are fixed by the game's developer mode: host is 1, clients are 1000, 2000, 3000.
$clientId = 1000

# The game reads display names from ./mp_names.json in its working directory.
# WriteAllText, not Set-Content: Windows PowerShell 5 prepends a BOM that the game's JSON
# parser rejects.
$namesJson = @(
    @{ net_id = 1; name = $Player1 },
    @{ net_id = $clientId; name = $Player2 }
) | ConvertTo-Json
[IO.File]::WriteAllText((Join-Path $workDir "mp_names.json"), $namesJson)

# The client runs without Steam, so it starts from an empty profile: locked characters and
# a max ascension of 0, which also drags the lobby's ascension down. Mirror the host's
# progress into it on every launch so both players have the owner's unlocks.
$steamAccount = Get-ChildItem (Join-Path $userData "steam") -Directory |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($steamAccount) {
    $profileId = (Get-Content (Join-Path $steamAccount.FullName "profile.save") -Raw | ConvertFrom-Json).last_profile_id
    $hostProgress = Join-Path $steamAccount.FullName "profile$profileId\saves\progress.save"
    $clientSaves = Join-Path $userData "default\$clientId\profile1\saves"
    if (Test-Path $hostProgress) {
        New-Item -ItemType Directory -Force -Path $clientSaves | Out-Null
        Copy-Item $hostProgress (Join-Path $clientSaves "progress.save") -Force
    }
}

# --fastmp is the game's own loopback mode: ENet on 127.0.0.1:33771 instead of Steam
# networking. Only the host talks to Steam (saves, cloud, achievements); the client runs
# with Steam off so it writes to its own save folder instead of racing the host's files.
# A bare --fastmp opens the multiplayer menu (Host, or Load when a co-op save exists).
# Don't use "--fastmp load": it looks the host up by Steam id, but co-op saves record net
# id 1, so validation fails and the game renames the save as broken.
$hostArgs = @("--fastmp", "--log-file", (Join-Path $logDir "couch_host.log"))
$clientArgs = @("--force-steam", "off", "--fastmp", "join", "--clientId", "$clientId",
                "--log-file", (Join-Path $logDir "couch_client.log"))

# Launching the exe directly (not through Steam) needs the app id in the environment,
# or SteamAPI init fails and the game quits.
$env:SteamAppId = "2868840"
Start-Process -FilePath $exe -WorkingDirectory $workDir -ArgumentList $hostArgs
Remove-Item Env:SteamAppId

# The client's join times out after 10 s, so the host must be listening first.
Start-Sleep -Seconds 12
Start-Process -FilePath $exe -WorkingDirectory $workDir -ArgumentList $clientArgs
