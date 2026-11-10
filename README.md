# Slay the Spire 2 couch co-op

Two players on one Windows PC, each with their own game window, linked over the game's
built-in developer loopback mode (`--fastmp`, ENet on `127.0.0.1:33771`). No game files are
modified and no mod DLL is loaded.

## Play

Double-click `couch-coop.bat`, or run it with names: `couch-coop.bat -Player1 Ethan -Player2 Mom`.

- **Layout** is set on every launch: with two monitors, each window is fullscreen on its
  own monitor (host on the primary). With one, the windows sit side by side.
- **Host window** (player 1) opens character select for a new run, or the resume lobby if a
  couch run is saved. To start over instead, abandon from the pause menu inside the run.
- **Client window** (player 2) joins the host automatically about 20 s after launch.
- **Mouse:** whoever holds it plays; clicking a window focuses it. Only the focused window
  plays sound.

## Saves

Couch play is isolated from your Steam profile: both windows run with Steam off and save to
`%APPDATA%\SlayTheSpire2\default\1\` (host) and `default\1000\` (client). So couch runs and
online co-op runs can never overwrite each other.

Every launch copies your Steam profile's progress, prefs and settings into both folders, so
both players start with your unlocks, ascension and audio/keybind settings. Nothing flows
back: couch runs earn no achievements, and unlocks earned in a couch run are reset to your
Steam profile's on the next launch.

Player names live in `%LOCALAPPDATA%\sts2-couch\mp_names.json`, which the launcher writes.
Logs: `%APPDATA%\SlayTheSpire2\logs\couch_host.log` and `couch_client.log`.

## Limits

- Two players. The game's dev mode reserves client ids 2000 and 3000 for players 3–4.
- One shared mouse; a controller follows whichever window has focus.
