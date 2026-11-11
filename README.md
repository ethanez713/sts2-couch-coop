# Slay the Spire 2 couch co-op

Two players on one Windows PC, each with their own game window, linked over the game's
built-in developer loopback mode (`--fastmp`, ENet on `127.0.0.1:33771`). A small mod
(`mod/`) tunes co-op for players sitting in the same room.

## Setup

1. Build the couch mod once, and again after changing it: in WSL,
   `cd ~/sts2_multi/mod && dotnet build -c Release` (.NET 9 SDK).
2. For touch controls, subscribe to [Touch-STS2](https://steamcommunity.com/sharedfiles/filedetails/?id=3804990566)
   on the Steam Workshop, then turn on **Touchscreen mode** in the game's input settings.

## Play

Double-click `couch-coop.bat`, or run it with names: `couch-coop.bat -Player1 Ethan -Player2 Mom`.

- **Layout** is set on every launch: with two monitors, each window is fullscreen on its
  own monitor (host on the primary). With one, the windows sit side by side.
- **Host window** (player 1) opens character select for a new run, or the resume lobby if a
  couch run is saved. To start over instead, abandon from the pause menu inside the run.
- **Client window** (player 2) joins the host automatically about 20 s after launch.
- **Mouse or touch:** clicking or tapping a window focuses it. Only the focused window plays
  sound.

## Couch mod

The launcher copies the couch mod, and Touch-STS2 when it is downloaded, into the game's
`mods` folder and enables mods for the couch windows only. Normal Steam play leaves them
off, and the couch mod does nothing outside a `--fastmp` session.

- **No remote cursors:** everyone can see every screen, so the other players' cursors are hidden.
- **Single-pick map:** one player's pick decides the next room, with no vote. The party
  travels once every player has the map open; until then the latest pick wins.
- **Touch and mouse at once:** touch doesn't move the mouse cursor, so one player can tap
  while another uses the mouse.
- **End turn for All:** after you end your turn, a button beside Ping ends everyone else's turn too.

## Saves

Couch play is isolated from your Steam profile: both windows run with Steam off and save to
`%APPDATA%\SlayTheSpire2\default\1\modded\` (host) and `default\1000\modded\` (client).
So couch runs and online co-op runs can never overwrite each other.

Every launch copies your Steam profile's progress, prefs and settings into both folders, so
both players start with your unlocks and audio/keybind settings. Nothing flows back to
Steam, and couch runs earn no achievements. Card, relic and other unlocks earned in a couch
run are replaced by your Steam profile's on the next launch.

Co-op ascension is the exception: a couch win at your max co-op ascension unlocks the next
level, and it is kept. Each launch gives both windows the highest
`max_multiplayer_ascension` among your Steam profile and the two couch saves, because the
lobby caps ascension at the lowest max among its players. The lobby's chosen level is kept
from the host's couch save.

Player names live in `%LOCALAPPDATA%\sts2-couch\mp_names.json`, which the launcher writes.
Logs: `%APPDATA%\SlayTheSpire2\logs\couch_host.log` and `couch_client.log`.

## Limits

- Two players. The game's dev mode reserves client ids 2000 and 3000 for players 3–4.
- A controller follows whichever window has focus.
