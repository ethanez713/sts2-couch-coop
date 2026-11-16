using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace CouchCoop;

// The game re-reads --fastmp every time a main menu is built, so Save & Quit would send the host
// straight into a new run that overwrites the save. Only the launch's first menu should act on it.
// --couch-host is the same kind of launch command, so it shares this gate.
[HarmonyPatch(typeof(NMainMenu), "CheckCommandLineArgs")]
static class FastMpOnlyAtLaunch
{
    static bool _launched;

    static bool Prefix(NMainMenu __instance)
    {
        if (_launched) return false;
        _launched = true;
        if (CouchMod.CouchHostMode is not string mode) return true;
        // --fastmp would open a second, ENet-only host beside the Steam lobby.
        StartCouchHost(__instance, mode);
        return false;
    }

    static void StartCouchHost(NMainMenu menu, string mode)
    {
        NMultiplayerSubmenu submenu = menu.OpenMultiplayerSubmenu();
        if (mode == "load")
        {
            ulong localId = PlatformUtil.GetLocalPlayerId(PlatformType.Steam);
            ReadSaveResult<SerializableRun> save = SaveManager.Instance.LoadAndCanonicalizeMultiplayerRunSave(localId);
            if (save.SaveData != null)
                submenu.StartHost(save.SaveData);
            else
                Log.Error("Failed to load multiplayer save");
            return;
        }
        if (mode == "new")
        {
            submenu.FastHost(GameMode.Standard, false);
            return;
        }
        Log.Error($"[CouchCoop] --couch-host {mode} is not new or load");
    }
}
