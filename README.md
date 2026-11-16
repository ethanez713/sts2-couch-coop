# Slay the Spire 2 couch co-op

**Play Slay the Spire 2 co-op with someone on the same couch, on one PC.** The game's
co-op is online only. This launcher opens two game windows on one machine, links them
over the game's own loopback mode, and adds a small mod so two people in one room are
not fighting over one cursor.

- Each player has their own window and their own pointer: two mice, or a mouse and a
  touchpad or touchscreen.
- One copy of the game. No second Steam account and no network.
- The host's progress counts toward your normal Steam profile.

Unofficial. Slay the Spire 2 is by Mega Crit, and this project is not affiliated with
them. It relies on a developer launch flag (`--fastmp`, ENet on `127.0.0.1:33771`) that
any game update could change.

## Setup

Windows, with Slay the Spire 2 installed through Steam and the .NET 9 SDK.

1. Build the mod once, and again after changing it:
   `cd mod && dotnet build -c Release`. The project expects the game at the default
   Steam path as seen from WSL; anywhere else, add
   `-p:GameData="<game folder>\data_sts2_windows_x86_64"`.
2. For touch mode only: subscribe to [Touch-STS2](https://steamcommunity.com/sharedfiles/filedetails/?id=3804990566)
   on the Steam Workshop, then turn on **Touchscreen mode** in the game's input settings.

## Play

Double-click `couch-coop.bat`, or run it with names: `couch-coop.bat -Player1 Ethan -Player2 Mom`.

- **Layout** is set on every launch: with two monitors, each window is fullscreen on its
  own monitor (host on the primary). With one, the windows sit side by side.
- **Host window** (player 1) opens character select for a new run, or the resume lobby if a
  couch run is saved. To start over instead, abandon from the pause menu inside the run.
- **Client window** (player 2) joins the host automatically about 20 s after launch.
- **Save & Quit** returns both windows to the main menu. Resume by relaunching, or in place:
  the host picks Multiplayer → Load, then the client picks Multiplayer → Join.
- **Input mode** (`-InputMode`):
  - `mice` (default): each window follows only its own pointing device, as if on its own PC. By
    default the host takes the touchpad and the client any other mouse; choose with
    `-Pointer1`/`-Pointer2` (`touchpad`, `mouse`, or text from the device path, which the
    logs list) and tune speed with `-Speed1`/`-Speed2`. While a couch window is in front the
    system cursor is parked out of sight; Alt-Tab away to use other apps. On a touchscreen,
    a finger works as the mouse of the window it touches (tap to click, drag to drag).
  - `touch`: one mouse and the touchscreen, with Touch-STS2. Clicking a window
    focuses it; tapping doesn't.
- Only the focused window plays sound.

## Couch mod

The launcher copies the couch mod, and in touch mode Touch-STS2, into the game's
`mods` folder and enables mods for the couch windows only. Normal Steam play leaves them
off, and the couch mod does nothing outside a `--fastmp` session.

- **No remote cursors:** everyone can see every screen, so the other players' cursors are hidden.
- **Single-pick map:** one player's pick decides the next room, with no vote. The party
  travels once every player has the map open; until then the latest pick wins.
- **Own pointer per window:** each window's game reads and moves only its own pointer, never
  the shared system cursor, so two players' drags and selections never interfere. In touch
  mode, touch never moves the mouse cursor or takes focus.
- **End Turn for All:** after you end your turn, a button beside Ping ends everyone else's turn too.

## Saves

Both windows run with Steam off and save to `%APPDATA%\SlayTheSpire2\default\1\modded\` (host)
and `default\1000\modded\` (client), so an in-progress couch run and an online co-op run can
never overwrite each other.

**The host's progress is your Steam profile's progress.** Every launch copies your Steam
profile's progress, prefs and settings into both folders. When both windows close, a hidden
background process (`couch-saves.ps1`) copies the host's `progress.save` (unlocks, timeline,
discoveries, stats, ascension) into the Steam profile, keeping the file's cloud timestamp so
Steam Cloud keeps it; the next normal launch uploads it. Settings are not copied back. The
next couch launch finishes any copy that was missed.

- Couch runs do not appear in the Steam profile's run history: Steam Cloud deletes local run
  files it has no copy of, and only the game itself can upload one.
- The copy is skipped if the Steam save changed after the couch session was seeded (you
  played normally in between); the couch progress is then kept in
  `%LOCALAPPDATA%\sts2-couch\unsynced\`, and the reason is in `logs\couch_sync.log`.
- The client's earnings are discarded, except co-op ascension. Each launch gives both windows
  the highest `max_multiplayer_ascension` among your Steam profile and the two couch saves,
  because the lobby caps ascension at the lowest max among its players. The lobby's chosen
  level is kept from the host's couch save.
- Couch runs earn no Steam achievements.

Player names live in `%LOCALAPPDATA%\sts2-couch\mp_names.json`, which the launcher writes.
Logs: `%APPDATA%\SlayTheSpire2\logs\couch_host.log` and `couch_client.log`.

## Limits

- Two players. The game's dev mode reserves client ids 2000 and 3000 for players 3–4.
- A controller follows whichever window has focus.

## License

[MIT](LICENSE). Copyright (c) 2026 Ethan Zhou.
