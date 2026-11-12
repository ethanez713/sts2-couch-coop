using System.Collections.Generic;
using Godot;

namespace CouchCoop;

// Each window draws its own cursor at its own pointer, because the system cursor is parked and
// hidden while each window follows its own device. The game's cursor images and show/hide calls
// (NCursorManager, the targeting arrow, ...) are routed here instead of to the system cursor.
static class SoftwareCursor
{
    static CanvasLayer? _layer;
    static TextureRect? _sprite;
    static Input.MouseModeEnum _mode = Input.MouseModeEnum.Visible;
    static readonly Dictionary<Input.CursorShape, (Texture2D Texture, Vector2 HotSpot)> Shapes = new();
    static readonly Dictionary<Resource, Texture2D> Textures = new();

    public static void Install(SceneTree tree)
    {
        _layer = new CanvasLayer { Name = "CouchCoopCursor", Layer = 4096 };
        _sprite = new TextureRect { MouseFilter = Control.MouseFilterEnum.Ignore, TextureFilter = CanvasItem.TextureFilterEnum.Linear };
        _layer.AddChild(_sprite);
        _sprite.Ready += Refresh;
        tree.Root.CallDeferred(Node.MethodName.AddChild, _layer);
        Refresh();
    }

    // NCursorManager sets its images when it enters the tree, which can be before these calls are
    // routed here, and it skips re-setting an image it already set; take them from it directly.
    public static void AdoptGameCursor()
    {
        System.Type? type = HarmonyLib.AccessTools.TypeByName("MegaCrit.Sts2.Core.Nodes.CommonUi.NCursorManager");
        SceneTree tree = (SceneTree)Engine.GetMainLoop();
        Node? manager = type == null ? null : FindNode(tree.Root, type);
        if (manager == null)
        {
            return;
        }
        var fields = HarmonyLib.Traverse.Create(manager);
        if (fields.Field("_cursorInspect").GetValue() is Image inspect)
        {
            SetCustomMouseCursor(inspect, Input.CursorShape.Help, new Vector2(12, 12));
        }
        fields.Field("_lastSetCursor").SetValue(null);
        fields.Method("UpdateCursor").GetValue();
        fields.Method("RefreshCursorShown").GetValue();
    }

    static Node? FindNode(Node node, System.Type type)
    {
        if (type.IsInstanceOfType(node))
        {
            return node;
        }
        foreach (Node child in node.GetChildren())
        {
            if (FindNode(child, type) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    public static Input.MouseModeEnum GetMouseMode() => SeatPointer.Active ? _mode : Input.MouseMode;

    public static void SetMouseMode(Input.MouseModeEnum mode)
    {
        if (!SeatPointer.Active)
        {
            Input.MouseMode = mode;
            return;
        }
        _mode = mode;
        Refresh();
    }

    public static void SetCustomMouseCursor(Resource image, Input.CursorShape shape, Vector2? hotSpot)
    {
        if (!SeatPointer.Active)
        {
            Input.SetCustomMouseCursor(image, shape, hotSpot);
            return;
        }
        if (!Textures.TryGetValue(image, out Texture2D? texture))
        {
            texture = image as Texture2D ?? ImageTexture.CreateFromImage((Image)image);
            Textures[image] = texture;
        }
        Shapes[shape] = (texture, hotSpot ?? Vector2.Zero);
        Refresh();
    }

    // Shown at the image's own pixel size, as a hardware cursor would be.
    public static void Refresh()
    {
        if (_sprite == null || !_sprite.IsInsideTree())
        {
            return;
        }
        Viewport viewport = _sprite.GetViewport();
        Input.CursorShape shape = viewport.GuiGetHoveredControl() is { MouseDefaultCursorShape: Control.CursorShape.Help }
            ? Input.CursorShape.Help
            : Input.CursorShape.Arrow;
        if (_mode != Input.MouseModeEnum.Visible
            || !(Shapes.TryGetValue(shape, out var cursor) || Shapes.TryGetValue(Input.CursorShape.Arrow, out cursor)))
        {
            _sprite.Visible = false;
            return;
        }
        Transform2D toViewport = viewport.GetScreenTransform().AffineInverse();
        _sprite.Visible = true;
        _sprite.Texture = cursor.Texture;
        _sprite.Size = cursor.Texture.GetSize();
        _sprite.Scale = toViewport.Scale;
        _sprite.Position = toViewport * (WindowOwnPointer.Pointer - cursor.HotSpot);
    }
}
