using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;

namespace CouchCoop;

// Everyone sees every screen in the same room, so the other players' cursors are only noise.
[HarmonyPatch(typeof(NRemoteMouseCursorContainer), "UpdateCursorVisibility")]
static class HideRemoteCursors
{
    static void Postfix(NRemoteMouseCursorContainer __instance) => __instance.Visible = false;
}

[HarmonyPatch(typeof(NRemoteMouseCursorContainer), nameof(NRemoteMouseCursorContainer._Ready))]
static class HideRemoteCursorsOnReady
{
    static void Postfix(NRemoteMouseCursorContainer __instance) => __instance.Visible = false;
}
