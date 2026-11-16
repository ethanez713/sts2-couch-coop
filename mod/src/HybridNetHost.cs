using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Multiplayer.Transport.ENet;
using MegaCrit.Sts2.Core.Multiplayer.Transport.Steam;
using MegaCrit.Sts2.Core.Platform;
using Steamworks;

namespace CouchCoop;

// FastMpJoin dials 127.0.0.1:33771, the port the game's own ENet host uses.
sealed class HybridNetHost : NetHost
{
    internal const ushort Port = 33771;

    internal static HybridNetHost? Current { get; private set; }

    readonly SteamHost _steam;
    readonly ENetHost _enet;
    readonly Dictionary<ulong, NetHost> _owners = new();
    bool _enetRunning;
    bool _stopNow;

    internal HybridNetHost(INetHostHandler handler) : base(handler)
    {
        var steam = new TransportHandler(this, steam: true);
        var enet = new TransportHandler(this, steam: false);
        _steam = new SteamHost(steam);
        _enet = new ENetHost(enet);
        steam.Bind(_steam);
        enet.Bind(_enet);
    }

    internal CSteamID? LobbyId => _steam.LobbyId;

    public override bool IsConnected => _steam.IsConnected;

    public override ulong NetId => _steam.NetId;

    public override IEnumerable<ulong> ConnectedPeerIds
    {
        get
        {
            foreach (ulong id in _steam.ConnectedPeerIds)
                yield return id;
            if (_enetRunning)
            {
                foreach (ulong id in _enet.ConnectedPeerIds)
                    yield return id;
            }
        }
    }

    internal void Installed() => Current = this;

    internal NetErrorInfo? StartEnet(int maxClients)
    {
        NetErrorInfo? error = _enet.StartHost(Port, maxClients);
        if (!error.HasValue)
            _enetRunning = true;
        return error;
    }

    internal async Task<NetErrorInfo?> StartSteam(int maxPlayers)
    {
        NetErrorInfo? result;
        try
        {
            result = await _steam.StartHost(maxPlayers);
        }
        catch
        {
            StopEnet(NetError.InternalError, now: true);
            throw;
        }
        if (result.HasValue)
        {
            StopEnet(NetError.FailedToHost, now: true);
            return result;
        }
        CouchNames.BroadcastFromHost();
        return null;
    }

    public override void Update()
    {
        _steam.Update();
        if (_enetRunning)
            _enet.Update();
    }

    public override void SetHostIsClosed(bool isClosed)
    {
        _steam.SetHostIsClosed(isClosed);
        _enet.SetHostIsClosed(isClosed);
    }

    public override void SendMessageToClient(ulong peerId, byte[] bytes, int length, NetTransferMode mode, int channel = 0)
    {
        if (!_owners.TryGetValue(peerId, out NetHost? host))
            throw new InvalidOperationException($"Could not find connection for peer {peerId}!");
        host.SendMessageToClient(peerId, bytes, length, mode, channel);
    }

    public override void SendMessageToAll(byte[] bytes, int length, NetTransferMode mode, int channel = 0)
    {
        _steam.SendMessageToAll(bytes, length, mode, channel);
        if (_enetRunning)
            _enet.SendMessageToAll(bytes, length, mode, channel);
    }

    public override void DisconnectClient(ulong peerId, NetError reason, bool now = false, string? debugReason = null)
    {
        if (_owners.TryGetValue(peerId, out NetHost? host))
            host.DisconnectClient(peerId, reason, now, debugReason);
    }

    public override void StopHost(NetError reason, bool now = false)
    {
        _stopNow = now;
        _steam.StopHost(reason, now);
        StopEnet(reason, now);
    }

    public override string? GetRawLobbyIdentifier() => _steam.GetRawLobbyIdentifier();

    internal bool IsEnet(ulong peerId) => _owners.TryGetValue(peerId, out NetHost? host) && host == _enet;

    internal ulong[] SteamPeers() => Peers(_steam);

    internal ulong[] EnetPeers() => Peers(_enet);

    ulong[] Peers(NetHost transport)
    {
        var ids = new List<ulong>();
        foreach (KeyValuePair<ulong, NetHost> pair in _owners)
        {
            if (pair.Value == transport)
                ids.Add(pair.Key);
        }
        return ids.ToArray();
    }

    internal void Own(ulong peerId, NetHost transport) => _owners[peerId] = transport;

    internal void Release(ulong peerId) => _owners.Remove(peerId);

    internal void ForwardConnected(ulong peerId) => _handler.OnPeerConnected(peerId);

    internal void ForwardDisconnected(ulong peerId, NetErrorInfo info) => _handler.OnPeerDisconnected(peerId, info);

    internal void ForwardPacket(ulong senderId, byte[] packetBytes, NetTransferMode mode, int channel) =>
        _handler.OnPacketReceived(senderId, packetBytes, mode, channel);

    internal void OnSteamDisconnected(NetErrorInfo info)
    {
        StopEnet(info.GetReason(), _stopNow);
        _handler.OnDisconnected(info);
    }

    void StopEnet(NetError reason, bool now)
    {
        if (!_enetRunning) return;
        _enetRunning = false;
        _enet.StopHost(reason, now);
    }

    // ENet's OnDisconnected fires because Steam already stopped. Forwarding it would tear the session down twice.
    sealed class TransportHandler : INetHostHandler
    {
        readonly HybridNetHost _hybrid;
        readonly bool _steam;
        NetHost? _transport;

        public TransportHandler(HybridNetHost hybrid, bool steam)
        {
            _hybrid = hybrid;
            _steam = steam;
        }

        public void Bind(NetHost transport) => _transport = transport;

        public void OnPeerConnected(ulong peerId)
        {
            if (_transport != null)
                _hybrid.Own(peerId, _transport);
            _hybrid.ForwardConnected(peerId);
        }

        public void OnPeerDisconnected(ulong peerId, NetErrorInfo info)
        {
            _hybrid.Release(peerId);
            _hybrid.ForwardDisconnected(peerId, info);
        }

        public void OnDisconnected(NetErrorInfo info)
        {
            if (_steam)
                _hybrid.OnSteamDisconnected(info);
        }

        public void OnPacketReceived(ulong senderId, byte[] packetBytes, NetTransferMode mode, int channel) =>
            _hybrid.ForwardPacket(senderId, packetBytes, mode, channel);
    }
}

[HarmonyPatch(typeof(NetHostGameService), nameof(NetHostGameService.StartSteamHost))]
static class HybridSteamHost
{
    static bool Prefix(NetHostGameService __instance, int maxClients, ref Task<NetErrorInfo?> __result)
    {
        if (!CouchMod.IsCouchHost) return true;
        if (CouchMod.CouchSeats is not int seats || seats < 2)
        {
            Log.Error($"[CouchCoop] --couch-host needs --couch-seats of at least 2, got {CouchMod.ArgValue("--couch-seats")}");
            __result = Task.FromResult<NetErrorInfo?>(new NetErrorInfo(NetError.FailedToHost, selfInitiated: true));
            return false;
        }
        int couchClients = seats - 1;
        // Couch clients are not Steam lobby members. The game lobby counts them and rejects the
        // rest with LobbyFull, so the Steam lobby is smaller by that many. CreateLobby rejects 0.
        int steamLobby = maxClients - couchClients;
        if (steamLobby < 1)
        {
            Log.Error($"[CouchCoop] steam lobby size {steamLobby} for {seats} couch seats; using 1");
            steamLobby = 1;
        }
        var hybrid = new HybridNetHost(__instance);
        NetErrorInfo? enetError = hybrid.StartEnet(couchClients);
        if (enetError.HasValue)
        {
            __result = Task.FromResult(enetError);
            return false;
        }
        // NetHostGameService.Update runs only once Steam reports connected, so a couch client
        // that dials during CreateLobby waits until the lobby exists.
        hybrid.Installed();
        AccessTools.Field(typeof(NetHostGameService), "_netHost").SetValue(__instance, hybrid);
        PropertyInfo platform = AccessTools.Property(typeof(NetHostGameService), nameof(NetHostGameService.Platform));
        MethodInfo setPlatform = platform.GetSetMethod(true)
            ?? throw new MissingMethodException(nameof(NetHostGameService), "set_Platform");
        setPlatform.Invoke(__instance, new object[] { PlatformType.Steam });
        Log.Info($"[CouchCoop] hybrid host, steam lobby {steamLobby}, enet {couchClients} on {HybridNetHost.Port}");
        __result = hybrid.StartSteam(steamLobby);
        return false;
    }
}

// TryOpenInviteDialog only accepts a SteamHost. The lobby friends are invited to is the inner one.
[HarmonyPatch(typeof(MegaCrit.Sts2.Core.Platform.Steam.SteamPlatformUtilStrategy), nameof(MegaCrit.Sts2.Core.Platform.Steam.SteamPlatformUtilStrategy.TryOpenInviteDialog))]
static class HybridInviteDialog
{
    static bool Prefix(INetGameService netService, ref bool __result)
    {
        if (netService is not INetHostGameService host || host.NetHost is not HybridNetHost hybrid)
            return true;
        if (!SteamUtils.IsOverlayEnabled())
        {
            Log.Warn("Tried to open invite dialog, but the player has disabled the steam overlay");
            __result = false;
            return false;
        }
        CSteamID? lobby = hybrid.LobbyId;
        if (!lobby.HasValue)
        {
            Log.Warn("Tried to open invite dialog but steam host is not yet in a lobby");
            __result = false;
            return false;
        }
        SteamFriends.ActivateGameOverlayInviteDialog(lobby.Value);
        __result = true;
        return false;
    }
}
