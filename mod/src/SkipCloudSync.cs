using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Saves;

namespace CouchCoop;

// The launcher seeds this profile. The cloud copy would overwrite it.
[HarmonyPatch(typeof(SaveManager), nameof(SaveManager.SyncCloudToLocal))]
static class SkipCloudSync
{
    static bool Prefix(ref Task __result)
    {
        if (!CouchMod.IsCouchHost) return true;
        __result = Task.CompletedTask;
        return false;
    }
}
