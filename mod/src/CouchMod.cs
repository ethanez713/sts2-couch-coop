using System.Linq;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace CouchCoop;

[ModInitializer(nameof(Init))]
public static class CouchMod
{
    // The couch launcher always starts the game in the developer loopback mode; outside it
    // (normal Steam play with mods enabled) the mod stays inert.
    public static bool IsCouchSession => OS.GetCmdlineArgs().Contains("--fastmp");

    public static void Init()
    {
        if (!IsCouchSession)
        {
            Log.Info("[CouchCoop] not a --fastmp session; patches not applied");
            return;
        }
        new Harmony("sts2-couch.CouchCoop").PatchAll(typeof(CouchMod).Assembly);
        EndTurnForAll.StartListening();
        Callable.From(TouchWithoutCursor.Install).CallDeferred();
        Callable.From(WindowOwnPointer.Install).CallDeferred();
        Log.Info("[CouchCoop] patches applied");
    }
}
