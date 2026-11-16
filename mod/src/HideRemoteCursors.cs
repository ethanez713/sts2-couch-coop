using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;

namespace CouchCoop;

// Couch seats share a room, so their cursors are noise. Friends are elsewhere; theirs stay.
// Offline, every peer is a couch seat.
[HarmonyPatch(typeof(NRemoteMouseCursorContainer), "UpdateCursorVisibility")]
static class HideRemoteCursors
{
    static readonly AccessTools.FieldRef<NRemoteMouseCursorContainer, List<NRemoteMouseCursor>> Cursors =
        AccessTools.FieldRefAccess<NRemoteMouseCursorContainer, List<NRemoteMouseCursor>>("_cursors");

    static void Postfix(NRemoteMouseCursorContainer __instance)
    {
        if (CouchNames.HidesEveryRemoteCursor)
        {
            __instance.Visible = false;
            return;
        }
        foreach (NRemoteMouseCursor cursor in Cursors(__instance))
        {
            if (CouchNames.IsCouchPeer(cursor.PlayerId))
                cursor.Visible = false;
        }
    }
}

[HarmonyPatch(typeof(NRemoteMouseCursorContainer), nameof(NRemoteMouseCursorContainer._Ready))]
static class HideRemoteCursorsOnReady
{
    static void Postfix(NRemoteMouseCursorContainer __instance)
    {
        if (CouchNames.HidesEveryRemoteCursor)
            __instance.Visible = false;
    }
}
