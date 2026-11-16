using System.Linq;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace CouchCoop;

[ModInitializer(nameof(Init))]
public static class CouchMod
{
    // The launcher passes --couch-seat <n> to every couch window: 0 hosts, 1-3 join. Without it
    // (normal Steam play with mods enabled) the mod stays inert.
    public static int? Seat { get; } = ReadSeat();

    public static bool IsCouchSession => Seat.HasValue;

    // Seat 0 only. "new" starts a standard lobby; "load" resumes the multiplayer save.
    public static string? CouchHostMode { get; } = ArgValue("--couch-host");

    public static bool IsCouchHost => CouchHostMode != null;

    public static int? CouchSeats { get; } = ReadInt("--couch-seats");

    // Seats 1–3. ENet reports that host as peer 1; the handshake is keyed on this SteamID64.
    public static ulong? CouchHostId { get; } = ReadUlong("--couch-host-id");

    static int? ReadSeat()
    {
        string? value = ArgValue("--couch-seat");
        return int.TryParse(value, out int seat) && seat >= 0 ? seat : null;
    }

    static int? ReadInt(string name) => int.TryParse(ArgValue(name), out int parsed) ? parsed : null;

    static ulong? ReadUlong(string name) => ulong.TryParse(ArgValue(name), out ulong parsed) ? parsed : null;

    public static string? ArgValue(string name)
    {
        string[] args = OS.GetCmdlineArgs();
        int i = System.Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    public static void Init()
    {
        if (!IsCouchSession)
        {
            Log.Info("[CouchCoop] no --couch-seat; patches not applied");
            return;
        }
        SeatPointer.Configure();
        new Harmony("sts2-couch.CouchCoop").PatchAll(typeof(CouchMod).Assembly);
        EndTurnForAll.StartListening();
        Callable.From(CouchWindow.Install).CallDeferred();
        Callable.From(WindowOwnPointer.Install).CallDeferred();
        Callable.From(PerfLog.Install).CallDeferred();
        if (SeatPointer.Active)
        {
            Callable.From(SoftwareCursor.AdoptGameCursor).CallDeferred();
        }
        Log.Info("[CouchCoop] patches applied");
    }
}
