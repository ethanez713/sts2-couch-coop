using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.PeerInput;
using MegaCrit.Sts2.Core.Runs;

namespace CouchCoop;

// Map travel by a single pick instead of a unanimous vote: the most recent pick wins, and the
// party moves once every player has the map screen open. Only the host decides travel, so
// these patches act on the host alone; clients keep the game's vote display as is.
[HarmonyPatch(typeof(MapSelectionSynchronizer))]
static class SinglePickMap
{
    static readonly AccessTools.FieldRef<MapSelectionSynchronizer, INetGameService> NetService =
        AccessTools.FieldRefAccess<MapSelectionSynchronizer, INetGameService>("_netService");
    static readonly AccessTools.FieldRef<MapSelectionSynchronizer, IRunState> RunStateOf =
        AccessTools.FieldRefAccess<MapSelectionSynchronizer, IRunState>("_runState");
    static readonly AccessTools.FieldRef<MapSelectionSynchronizer, MapLocation> Source =
        AccessTools.FieldRefAccess<MapSelectionSynchronizer, MapLocation>("_acceptingVotesFromSource");
    static readonly AccessTools.FieldRef<MapSelectionSynchronizer, List<MapVote?>> Votes =
        AccessTools.FieldRefAccess<MapSelectionSynchronizer, List<MapVote?>>("_votes");

    static MapVote? _latestPick;
    static PeerInputSynchronizer? _watchedInput;
    static string? _lastWaitLog;

    static bool IsHost(MapSelectionSynchronizer sync) => NetService(sync).Type != NetGameType.Client;

    [HarmonyPostfix, HarmonyPatch(nameof(MapSelectionSynchronizer.PlayerVotedForMapCoord))]
    static void AfterVote(MapSelectionSynchronizer __instance, Player player, MapVote? destination)
    {
        if (!IsHost(__instance) || !Equals(__instance.GetVote(player), destination))
        {
            return;
        }
        _latestPick = destination ?? Votes(__instance).LastOrDefault(v => v.HasValue);
        TryTravel(__instance);
    }

    // The game picks randomly among differing votes; make it take the latest pick instead.
    [HarmonyPrefix, HarmonyPatch("MoveToMapCoord")]
    static void BeforeMove(MapSelectionSynchronizer __instance)
    {
        List<MapVote?> votes = Votes(__instance);
        if (_latestPick is { } pick && votes.Contains(pick))
        {
            FillVotes(votes, pick);
        }
    }

    [HarmonyPostfix, HarmonyPatch(nameof(MapSelectionSynchronizer.OnLocationChanged))]
    static void AfterLocationChanged() => _latestPick = null;

    static void TryTravel(MapSelectionSynchronizer sync)
    {
        if (_latestPick is not { } pick || pick.mapGenerationCount != sync.MapGenerationCount)
        {
            return;
        }
        List<MapVote?> votes = Votes(sync);
        if (!votes.Contains(pick) || votes.All(v => v.HasValue))
        {
            // All votes in means the game's own path has already moved the party.
            return;
        }
        PeerInputSynchronizer? input = RunManager.Instance.InputSynchronizer;
        if (input == null)
        {
            return;
        }
        WatchScreens(input);
        IRunState run = RunStateOf(sync);
        List<Player> notOnMap = run.Players.Where(p => input.GetScreenType(p.NetId) != NetScreenType.Map).ToList();
        if (notOnMap.Count > 0)
        {
            string waiting = $"map pick {pick} waits for players {string.Join(",", notOnMap.Select(p => p.NetId))} to open the map";
            if (waiting != _lastWaitLog)
            {
                Log.Info($"[CouchCoop] {waiting}");
                _lastWaitLog = waiting;
            }
            return;
        }
        Log.Info($"[CouchCoop] everyone is on the map; travelling to {pick}");
        FillVotes(votes, pick);
        _latestPick = null;
        AccessTools.Method(typeof(MapSelectionSynchronizer), "MoveToMapCoord").Invoke(sync, null);
    }

    static void FillVotes(List<MapVote?> votes, MapVote pick)
    {
        for (int i = 0; i < votes.Count; i++)
        {
            votes[i] = pick;
        }
    }

    // A pick made while someone is still elsewhere (rewards, shop) travels when they open the map.
    static void WatchScreens(PeerInputSynchronizer input)
    {
        if (_watchedInput == input)
        {
            return;
        }
        _watchedInput = input;
        // StateChanged, not ScreenChanged: only StateChanged also fires for the host's own screen.
        input.StateChanged += _ =>
        {
            MapSelectionSynchronizer? sync = RunManager.Instance.MapSelectionSynchronizer;
            if (sync != null && _watchedInput == input && IsHost(sync))
            {
                TryTravel(sync);
            }
        };
    }
}
