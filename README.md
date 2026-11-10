# Slay the Spire 2 couch co-op

Two players on one Windows PC, each with their own game window (one per monitor), linked
over the game's built-in developer loopback mode (`--fastmp`, ENet on `127.0.0.1:33771`).
No game files are modified and no mod DLL is loaded, so saves stay in the vanilla profile.

## Play

Double-click `couch-coop.bat` (or run it with `-Player1 Ethan -Player2 Mom`).

- **Host window** (player 1): opens on the multiplayer menu. Click **Host** for a new run,
  or **Load** to resume a couch run.
- **Client window** (player 2): joins the host automatically about 20 s after launch. If it
  times out, use its multiplayer menu's join button.
- **Second monitor:** in the client window, set Settings → Display → monitor and fullscreen
  once. Each window remembers its own display settings.
- **Mouse:** whoever holds it plays; clicking a window focuses it. Only the focused window
  plays sound (Settings → Mute in background).

## How it fits together

- The host runs with Steam: your real profile, cloud saves, unlocks and achievements.
- The client runs with `--force-steam off`, so it saves to
  `%APPDATA%\SlayTheSpire2\default\1000\` and never writes your Steam profile. The launcher
  copies your `progress.save` into it on every launch so player 2 has your unlocks and
  ascension.
- Player names come from `%LOCALAPPDATA%\sts2-couch\mp_names.json`, written by the
  launcher and read from the game's working directory.
- Logs: `%APPDATA%\SlayTheSpire2\logs\couch_host.log` and `couch_client.log`.

## Known limits

- A couch save and an online Steam co-op save share one slot. Loading a save made the
  other way fails validation, and the game renames it as broken (recoverable, but it
  disappears from the menu). Finish or abandon one kind of run before starting the other.
- Abandoning a couch run from the menu deletes it without a run-history entry.
- Two players only. The game's dev mode allows client ids 2000 and 3000 for players 3–4.
