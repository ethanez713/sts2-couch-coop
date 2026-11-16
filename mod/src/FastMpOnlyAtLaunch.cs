using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

namespace CouchCoop;

// The game re-reads --fastmp every time a main menu is built, so Save & Quit would send the host
// straight into a new run that overwrites the save. Only the launch's first menu should act on it.
[HarmonyPatch(typeof(NMainMenu), "CheckCommandLineArgs")]
static class FastMpOnlyAtLaunch
{
    static bool _launched;

    static bool Prefix()
    {
        if (_launched) return false;
        _launched = true;
        return true;
    }
}
