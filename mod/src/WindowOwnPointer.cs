using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;

namespace CouchCoop;

// Gives each couch window its own pointer. Godot's root Viewport.GetMousePosition reads the
// one system cursor, and WarpMouse moves it, so with touch kept off that cursor the touch
// player's dragged card followed the mouse player, and Touch-STS2 parking its pointer yanked
// the mouse player's cursor. The call sites in the game and Touch-STS2 that read or move the
// pointer are rewritten to use the window's own last input position instead.
static class WindowOwnPointer
{
    // From a scan of sts2.dll and TouchSts2.dll for the calls below; nested lambda types are
    // scanned too. A name that no longer resolves is logged, not fatal.
    static readonly string[] CallerTypes =
    {
        "MegaCrit.Sts2.Core.Nodes.Combat.NMouseCardPlay",
        "MegaCrit.Sts2.Core.Nodes.Combat.NTargetingArrow",
        "MegaCrit.Sts2.Core.Nodes.Potions.NPotionPopup",
        "MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic.NHandImageCollection",
        "MegaCrit.Sts2.Core.Nodes.Reaction.NReactionWheel",
        "MegaCrit.Sts2.Core.Nodes.Screens.Map.NMouseHeldMapDrawingInput",
        "MegaCrit.Sts2.Core.Nodes.Vfx.NFollowCursor",
        "MegaCrit.Sts2.Core.Nodes.CommonUi.NControllerManager",
        "TouchSts2.TouchRuntime",
    };

    static readonly Dictionary<MethodInfo, MethodInfo> Replacements = new()
    {
        [AccessTools.Method(typeof(Viewport), nameof(Viewport.GetMousePosition))] =
            AccessTools.Method(typeof(WindowOwnPointer), nameof(MousePosition)),
        [AccessTools.Method(typeof(CanvasItem), nameof(CanvasItem.GetGlobalMousePosition))] =
            AccessTools.Method(typeof(WindowOwnPointer), nameof(GlobalMousePosition)),
        [AccessTools.Method(typeof(Viewport), nameof(Viewport.WarpMouse))] =
            AccessTools.Method(typeof(WindowOwnPointer), nameof(WarpViewportMouse)),
        [AccessTools.Method(typeof(Input), nameof(Input.WarpMouse))] =
            AccessTools.Method(typeof(WindowOwnPointer), nameof(WarpMouse)),
    };

    // In window pixels: the last real mouse or touch position this window received, or where
    // the game last warped it. Set by TouchWithoutCursor's window hook.
    public static Vector2 Pointer;

    // Runs after every mod has loaded, so Touch-STS2's types resolve.
    public static void Install()
    {
        var harmony = new Harmony("sts2-couch.CouchCoop.pointer");
        var transpiler = new HarmonyMethod(AccessTools.Method(typeof(WindowOwnPointer), nameof(Rewrite)));
        int patched = 0;
        foreach (string name in CallerTypes)
        {
            Type? type = AccessTools.TypeByName(name);
            if (type == null)
            {
                Log.Warn($"[CouchCoop] pointer: type {name} not found");
                continue;
            }
            foreach (MethodBase method in MethodsOf(type).Where(CallsPointerApi))
            {
                harmony.Patch(method, transpiler: transpiler);
                patched++;
            }
        }
        Log.Info($"[CouchCoop] each window keeps its own pointer ({patched} call sites rewritten)");
    }

    static IEnumerable<MethodBase> MethodsOf(Type type)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                                 | BindingFlags.Static | BindingFlags.DeclaredOnly;
        IEnumerable<MethodBase> own = type.GetMethods(all).Where(m => !m.IsAbstract && !m.ContainsGenericParameters)
            .Concat<MethodBase>(type.GetConstructors(all));
        return own.Concat(type.GetNestedTypes(all).Where(t => !t.ContainsGenericParameters).SelectMany(MethodsOf));
    }

    static bool CallsPointerApi(MethodBase method)
    {
        try
        {
            return PatchProcessor.GetOriginalInstructions(method)
                .Any(i => i.operand is MethodInfo called && Replacements.ContainsKey(called));
        }
        catch (Exception)
        {
            return false;
        }
    }

    static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.operand is MethodInfo called && Replacements.TryGetValue(called, out MethodInfo? replacement))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = replacement;
            }
            yield return instruction;
        }
    }

    static bool IsRoot(Viewport viewport) => viewport.GetParent() == null;

    // Godot's own fallback when no system mouse exists.
    public static Vector2 MousePosition(Viewport viewport) => IsRoot(viewport)
        ? viewport.GetScreenTransform().AffineInverse() * Pointer
        : viewport.GetMousePosition();

    public static Vector2 GlobalMousePosition(CanvasItem item) =>
        item.GetCanvasTransform().AffineInverse() * MousePosition(item.GetViewport());

    public static void WarpViewportMouse(Viewport viewport, Vector2 position)
    {
        if (!IsRoot(viewport))
        {
            viewport.WarpMouse(position);
            return;
        }
        WarpMouse(viewport.GetScreenTransform() * position);
    }

    public static void WarpMouse(Vector2 windowPosition) => Pointer = windowPosition;
}
