using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace CouchCoop;

// An "End Turn for All" button beside Ping. Each couch window is its own game process and the
// game only lets a player end their own turn, so the button asks the other windows over
// localhost UDP to press their own End Turn.
static class EndTurnForAll
{
    const string Message = "end-turn";
    const int BasePort = 33790;
    // The launcher's net ids, in seat order: host, then clients (fixed by the game's dev mode).
    static readonly ulong[] SeatIds = { 1, 1000, 2000, 3000 };

    static int SeatPort(int seat) => BasePort + seat;

    static int OwnSeat()
    {
        string[] args = OS.GetCmdlineArgs();
        int i = Array.IndexOf(args, "--clientId");
        ulong id = i >= 0 && i + 1 < args.Length && ulong.TryParse(args[i + 1], out ulong parsed) ? parsed : 1;
        return Math.Max(0, Array.IndexOf(SeatIds, id));
    }

    public static void StartListening()
    {
        int port = SeatPort(OwnSeat());
        var thread = new Thread(() => Listen(port)) { IsBackground = true, Name = "CouchCoop end-turn listener" };
        thread.Start();
    }

    static void Listen(int port)
    {
        try
        {
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
            while (true)
            {
                IPEndPoint? from = null;
                if (Encoding.ASCII.GetString(udp.Receive(ref from)) == Message)
                {
                    Callable.From(EndOwnTurn).CallDeferred();
                }
            }
        }
        catch (Exception e)
        {
            Log.Error($"[CouchCoop] end-turn listener on port {port} stopped: {e.Message}");
        }
    }

    public static void AskOthersToEndTurn()
    {
        int own = OwnSeat();
        using var udp = new UdpClient();
        byte[] bytes = Encoding.ASCII.GetBytes(Message);
        foreach (int seat in Enumerable.Range(0, SeatIds.Length).Where(s => s != own))
        {
            udp.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Loopback, SeatPort(seat)));
        }
    }

    static readonly AccessTools.FieldRef<NEndTurnButton, CombatState?> ButtonCombatState =
        AccessTools.FieldRefAccess<NEndTurnButton, CombatState?>("_combatState");

    static void EndOwnTurn()
    {
        NEndTurnButton? button = NCombatRoom.Instance?.Ui?.EndTurnButton;
        if (button == null || !CombatManager.Instance.IsInProgress)
        {
            return;
        }
        Player? me = LocalContext.GetMe(ButtonCombatState(button));
        // CallReleaseLogic toggles, so only press it while this player's turn is still open.
        if (me != null && !CombatManager.Instance.IsPlayerReadyToEndTurn(me))
        {
            Log.Info("[CouchCoop] ending turn on request from another window");
            button.CallReleaseLogic();
        }
    }
}

[HarmonyPatch(typeof(NPingButton))]
static class EndTurnForAllButton
{
    const string Label = "End Turn for All";
    // Ping's art is 150 px wide; the longer label needs a wider banner beside it.
    const float Width = 240;
    const float Gap = 8;
    static readonly System.Collections.Generic.Dictionary<NPingButton, Control> Clones = new();

    // A script-less copy of the Ping button's scene keeps its art and font, and as its child
    // it rides the same slide-in and slide-out animations.
    [HarmonyPostfix, HarmonyPatch(nameof(NPingButton._Ready))]
    static void AfterReady(NPingButton __instance)
    {
        var clone = (Control)__instance.Duplicate((int)Node.DuplicateFlags.Groups);
        clone.Name = "EndTurnForAll";
        clone.UniqueNameInOwner = false;
        clone.MouseFilter = Control.MouseFilterEnum.Stop;
        foreach (Control child in clone.FindChildren("*", owned: false).OfType<Control>())
        {
            child.MouseFilter = Control.MouseFilterEnum.Ignore;
        }
        clone.GetNodeOrNull<Control>("Visuals/ControllerIcon")?.Hide();
        // The copy starts with the disabled tint Ping applies to itself in _Ready.
        ShaderMaterial? shader = null;
        if (clone.GetNodeOrNull<TextureRect>("Visuals/Image") is { } image)
        {
            image.StretchMode = TextureRect.StretchModeEnum.Scale;
            image.Modulate = Colors.White;
            if (image.Material is ShaderMaterial original)
            {
                image.Material = shader = (ShaderMaterial)original.Duplicate();
                shader.SetShaderParameter("v", 1f);
            }
        }
        if (clone.GetNodeOrNull<Label>("Visuals/Label") is { } label)
        {
            label.Text = Label;
            label.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            label.VerticalAlignment = VerticalAlignment.Center;
            label.Modulate = StsColors.cream;
        }
        clone.MouseEntered += () => shader?.SetShaderParameter("v", 1.5f);
        clone.MouseExited += () => shader?.SetShaderParameter("v", 1f);
        clone.GuiInput += e =>
        {
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false })
            {
                EndTurnForAll.AskOthersToEndTurn();
                clone.Visible = false;
            }
        };
        clone.Visible = false;
        __instance.AddChild(clone);
        // Ping's root is anchored to its parent's bottom-right; the copy is placed by its parent's top-left.
        clone.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        clone.Size = new Vector2(Width, __instance.Size.Y);
        clone.Position = new Vector2(-Width - Gap, 0);
        Clones[__instance] = clone;
        __instance.TreeExiting += () => Clones.Remove(__instance);
    }

    [HarmonyPostfix, HarmonyPatch("SetState")]
    static void AfterSetState(NPingButton __instance)
    {
        if (Clones.TryGetValue(__instance, out Control? clone))
        {
            clone.Visible = Traverse.Create(__instance).Field("_state").GetValue()?.ToString() == "Enabled";
        }
    }
}
