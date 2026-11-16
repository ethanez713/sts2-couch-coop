using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Transport.ENet;

namespace CouchCoop;

// ENet hardcodes the host as peer 1. The handshake and SendMessage(msg, hostId) are keyed on the
// Steam id, while packets from the host still arrive with sender 1.
[HarmonyPatch(typeof(ENetClient), nameof(ENetClient.HostNetId), MethodType.Getter)]
static class CouchHostNetId
{
    static void Postfix(ref ulong __result)
    {
        if (CouchMod.CouchHostId is ulong id)
            __result = id;
    }
}

[HarmonyPatch(typeof(NetClientGameService), nameof(NetClientGameService.OnPacketReceived))]
static class CouchHostSender
{
    const ulong ENetHostSender = 1;

    static void Prefix(ref ulong senderId)
    {
        if (senderId == ENetHostSender && CouchMod.CouchHostId is ulong id)
            senderId = id;
    }
}
