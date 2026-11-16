using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Platform.Null;
using MegaCrit.Sts2.Core.Platform.Steam;
using Steamworks;

namespace CouchCoop;

// Relayed on the end-turn socket, not in game packets: couch clients have Steam switched off,
// and a name inside a game message would be a new byte on the wire to vanilla friends.
static class CouchNames
{
    internal const string MessagePrefix = "names ";

    static readonly object Gate = new();
    static readonly Dictionary<ulong, string> Names = new();
    static readonly HashSet<ulong> CouchPeers = new();
    static int _seq;
    static bool _missingFileLogged;

    internal static bool HidesEveryRemoteCursor => !CouchMod.IsCouchHost && CouchMod.CouchHostId == null;

    internal static bool IsCouchPeer(ulong id)
    {
        if (CouchMod.IsCouchHost)
            return HybridNetHost.Current?.IsEnet(id) == true;
        if (CouchMod.CouchHostId is ulong hostId)
        {
            if (id == hostId) return true;
            lock (Gate)
                return CouchPeers.Contains(id);
        }
        return false;
    }

    internal static bool TryGet(ulong id, out string name)
    {
        lock (Gate)
        {
            if (Names.TryGetValue(id, out string? found) && !string.IsNullOrEmpty(found))
            {
                name = found;
                return true;
            }
        }
        name = "";
        return false;
    }

    internal static void BroadcastFromHost()
    {
        HybridNetHost? host = HybridNetHost.Current;
        if (!CouchMod.IsCouchHost || host == null) return;
        try
        {
            List<(ulong Id, string Name)> fromFile = ReadMpNames();
            var fromSteam = new List<(ulong Id, string Name)>();
            AddPersona(fromSteam, host.NetId);
            foreach (ulong id in host.SteamPeers())
                AddPersona(fromSteam, id);
            ulong[] couch = host.EnetPeers();
            string payload;
            lock (Gate)
            {
                foreach ((ulong id, string persona) in fromFile)
                    Names[id] = persona;
                foreach ((ulong id, string persona) in fromSteam)
                    Names[id] = persona;
                _seq++;
                payload = Write(_seq, Names, couch);
            }
            EndTurnForAll.SendToOthers(MessagePrefix + payload);
        }
        catch (Exception e)
        {
            Log.Error($"[CouchCoop] name roster: {e.Message}");
        }
    }

    internal static void Merge(string json)
    {
        if (CouchMod.IsCouchHost) return;
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        int seq = root.GetProperty("seq").GetInt32();
        lock (Gate)
        {
            if (seq <= _seq) return;
            _seq = seq;
            foreach (JsonProperty property in root.GetProperty("names").EnumerateObject())
            {
                if (!ulong.TryParse(property.Name, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id))
                    continue;
                string? persona = property.Value.GetString();
                if (!string.IsNullOrEmpty(persona))
                    Names[id] = persona;
            }
            CouchPeers.Clear();
            foreach (JsonElement id in root.GetProperty("couch").EnumerateArray())
                CouchPeers.Add(id.GetUInt64());
        }
    }

    static void AddPersona(List<(ulong Id, string Name)> into, ulong id)
    {
        ulong self = SteamUser.GetSteamID().m_SteamID;
        string persona = id == self
            ? SteamFriends.GetPersonaName()
            : SteamFriends.GetFriendPersonaName(new CSteamID(id));
        if (!string.IsNullOrEmpty(persona))
            into.Add((id, persona));
    }

    static List<(ulong Id, string Name)> ReadMpNames()
    {
        var found = new List<(ulong, string)>();
        string path = Path.Combine(Directory.GetCurrentDirectory(), "mp_names.json");
        if (!File.Exists(path))
        {
            if (!_missingFileLogged)
            {
                _missingFileLogged = true;
                Log.Warn($"[CouchCoop] no mp_names.json in {Directory.GetCurrentDirectory()}");
            }
            return found;
        }
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (JsonElement entry in doc.RootElement.EnumerateArray())
        {
            if (!entry.TryGetProperty("net_id", out JsonElement idElement) || !TryId(idElement, out ulong id))
                continue;
            if (!entry.TryGetProperty("name", out JsonElement nameElement))
                continue;
            string? persona = nameElement.GetString();
            if (!string.IsNullOrEmpty(persona))
                found.Add((id, persona));
        }
        return found;
    }

    static bool TryId(JsonElement element, out ulong id)
    {
        if (element.ValueKind == JsonValueKind.Number)
            return element.TryGetUInt64(out id);
        if (element.ValueKind == JsonValueKind.String &&
            ulong.TryParse(element.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out id))
            return true;
        id = 0;
        return false;
    }

    static string Write(int seq, Dictionary<ulong, string> names, ulong[] couch)
    {
        var text = new StringBuilder();
        text.Append("{\"seq\":").Append(seq.ToString(CultureInfo.InvariantCulture)).Append(",\"names\":{");
        bool first = true;
        foreach (KeyValuePair<ulong, string> pair in names)
        {
            if (!first) text.Append(',');
            first = false;
            text.Append('"').Append(pair.Key.ToString(CultureInfo.InvariantCulture)).Append("\":");
            text.Append(JsonSerializer.Serialize(pair.Value));
        }
        text.Append("},\"couch\":[");
        for (int i = 0; i < couch.Length; i++)
        {
            if (i > 0) text.Append(',');
            text.Append(couch[i].ToString(CultureInfo.InvariantCulture));
        }
        text.Append("]}");
        return text.ToString();
    }
}

[HarmonyPatch(typeof(SteamPlatformUtilStrategy), nameof(SteamPlatformUtilStrategy.GetPlayerName))]
static class SteamCouchNames
{
    static bool Prefix(ulong playerId, ref string __result)
    {
        if (!CouchNames.TryGet(playerId, out string name)) return true;
        __result = name;
        return false;
    }
}

[HarmonyPatch(typeof(NullPlatformUtilStrategy), nameof(NullPlatformUtilStrategy.GetPlayerName))]
static class NullCouchNames
{
    static bool Prefix(ulong playerId, ref string __result)
    {
        if (!CouchNames.TryGet(playerId, out string name)) return true;
        __result = name;
        return false;
    }
}

[HarmonyPatch(typeof(NetHostGameService), nameof(NetHostGameService.OnPeerConnected))]
static class CouchNamesOnConnect
{
    static void Postfix() => CouchNames.BroadcastFromHost();
}

[HarmonyPatch(typeof(NetHostGameService), nameof(NetHostGameService.OnPeerDisconnected))]
static class CouchNamesOnDisconnect
{
    static void Postfix() => CouchNames.BroadcastFromHost();
}
