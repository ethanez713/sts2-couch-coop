using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace CouchCoop;

// One pointing device per window, as if each window were on its own machine. Windows merges
// every mouse and touchpad into one system cursor, so each window instead reads raw input
// from its own device (even in the background), keeps its own pointer and buttons, and feeds
// them to Godot as mouse events. The system cursor is parked inside the host while a couch
// window is in front, so neither device can click anything else.
static class SeatPointer
{
    const int RIM_TYPEMOUSE = 0;
    const uint RID_INPUT = 0x10000003;
    const uint RIDI_DEVICENAME = 0x20000007;
    const uint RIDEV_INPUTSINK = 0x00000100;
    const ushort MOUSE_MOVE_ABSOLUTE = 0x01;
    const ushort MOUSE_VIRTUAL_DESKTOP = 0x02;
    const uint WM_MOUSEMOVE = 0x0200;

    // Touchpad HID vendors; a precision touchpad's synthesized mouse input has no device handle.
    static readonly string[] TouchpadIds = { "ELAN", "SYNA", "MSFT0001", "ALPS", "ATML", "CYAP", "SYN1", "FTE" };

    public static bool Active { get; private set; }
    static string _selector = "";
    static float _speed = 1;
    static IntPtr _hwnd;
    static readonly Dictionary<IntPtr, bool> MineByDevice = new();
    static MouseButtonMask _buttons;
    static ulong _lastMoveUsec;
    static (MouseButton Button, ulong Usec, Vector2 At) _lastPress;

    // --couch-pointer <touchpad|mouse|text in the device path> [--couch-pointer-speed <x>]
    public static void Configure()
    {
        string[] args = OS.GetCmdlineArgs();
        int i = Array.IndexOf(args, "--couch-pointer");
        if (i < 0 || i + 1 >= args.Length)
        {
            return;
        }
        _selector = args[i + 1];
        int s = Array.IndexOf(args, "--couch-pointer-speed");
        if (s >= 0 && s + 1 < args.Length && float.TryParse(args[s + 1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float speed))
        {
            _speed = speed;
        }
        Active = true;
    }

    public static void Install(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _systemSpeed = SystemSpeed();
        // Godot re-registers its own raw input whenever the mouse mode changes, so the real mode is
        // set once here (the game's own calls go to SoftwareCursor) and registration follows it.
        Input.MouseMode = Input.MouseModeEnum.Hidden;
        Register();
        SceneTree tree = (SceneTree)Engine.GetMainLoop();
        if (!tree.Root.GuiEmbedSubwindows)
        {
            // Godot reads the system cursor for hover unless the root embeds subwindows.
            tree.Root.GuiEmbedSubwindows = true;
        }
        GetClientRect(hwnd, out Rect client);
        WindowOwnPointer.Pointer = _sentPointer = new Vector2(client.Right / 2, client.Bottom / 2);
        TellGodotMouseEntered();
        SoftwareCursor.Install(tree);
        tree.ProcessFrame += OnFrame;
        if (EndTurnForAll.OwnSeat() == 0)
        {
            new Thread(ParkSystemCursor) { IsBackground = true, Name = "CouchCoop cursor park" }.Start();
        }
        Log.Info($"[CouchCoop] this window takes pointer input only from the '{_selector}' device (speed {_speed})");
    }

    // Godot replaces it with its own focus-following registration on activation and after a move.
    public static void EnsureRegistered()
    {
        uint count = 8;
        var registered = new RawInputDevice[count];
        uint found = GetRegisteredRawInputDevices(registered, ref count, (uint)Marshal.SizeOf<RawInputDevice>());
        bool ours = found != uint.MaxValue && registered.Take((int)found).Any(d =>
            d is { UsagePage: 0x01, Usage: 0x02 } && d.Target == _hwnd && (d.Flags & RIDEV_INPUTSINK) != 0);
        if (!ours)
        {
            Register();
        }
    }

    static void Register()
    {
        var device = new RawInputDevice { UsagePage = 0x01, Usage = 0x02, Flags = RIDEV_INPUTSINK, Target = _hwnd };
        if (!RegisterRawInputDevices(new[] { device }, 1, (uint)Marshal.SizeOf<RawInputDevice>()))
        {
            Log.Error($"[CouchCoop] raw mouse registration failed: {Marshal.GetLastPInvokeError()}");
        }
    }

    // Godot only marks the mouse as inside its window (needed for hover) on a real WM_MOUSEMOVE;
    // the WM_MOUSELEAVE that follows is dropped by the window hook.
    static void TellGodotMouseEntered()
    {
        Vector2 p = WindowOwnPointer.Pointer;
        CouchWindow.CallGodot(_hwnd, WM_MOUSEMOVE, IntPtr.Zero, (IntPtr)(((int)p.Y << 16) | ((int)p.X & 0xFFFF)));
    }

    public static void OnRawInput(IntPtr wParam, IntPtr lParam)
    {
        // RIM_INPUTSINK: delivered while another window is in front.
        if ((long)wParam == 1 && !_loggedBackground)
        {
            _loggedBackground = true;
            Log.Info("[CouchCoop] receiving pointer input while in the background");
        }
        if (!IsCouchWindow(GetForegroundWindow()))
        {
            ReleaseHeldButtons();
            return;
        }
        // Every report from every pointing device reaches every couch window, up to 1000 a second
        // per device, so this path allocates nothing.
        uint size = RawBufferSize;
        if (GetRawInputData(lParam, RID_INPUT, RawBuffer, ref size, HeaderSize) is 0 or uint.MaxValue
            || Marshal.ReadInt32(RawBuffer, HeaderTypeOffset) != RIM_TYPEMOUSE
            || !IsMine(Marshal.ReadIntPtr(RawBuffer, HeaderDeviceOffset)))
        {
            return;
        }
        Apply(new RawMouse
        {
            Flags = (ushort)Marshal.ReadInt16(RawBuffer, FlagsOffset),
            ButtonFlags = (ushort)Marshal.ReadInt16(RawBuffer, ButtonFlagsOffset),
            ButtonData = (ushort)Marshal.ReadInt16(RawBuffer, ButtonDataOffset),
            LastX = Marshal.ReadInt32(RawBuffer, LastXOffset),
            LastY = Marshal.ReadInt32(RawBuffer, LastYOffset),
        });
    }

    const uint RawBufferSize = 256;
    static readonly IntPtr RawBuffer = Marshal.AllocHGlobal((int)RawBufferSize);
    static readonly uint HeaderSize = (uint)Marshal.SizeOf<RawInputHeader>();
    static readonly int HeaderTypeOffset = (int)Marshal.OffsetOf<RawInputHeader>(nameof(RawInputHeader.Type));
    static readonly int HeaderDeviceOffset = (int)Marshal.OffsetOf<RawInputHeader>(nameof(RawInputHeader.Device));
    static readonly int FlagsOffset = MouseOffset(nameof(RawMouse.Flags));
    static readonly int ButtonFlagsOffset = MouseOffset(nameof(RawMouse.ButtonFlags));
    static readonly int ButtonDataOffset = MouseOffset(nameof(RawMouse.ButtonData));
    static readonly int LastXOffset = MouseOffset(nameof(RawMouse.LastX));
    static readonly int LastYOffset = MouseOffset(nameof(RawMouse.LastY));

    static int MouseOffset(string field) => (int)HeaderSize + (int)Marshal.OffsetOf<RawMouse>(field);

    // A release made after Alt-Tab away never arrives here.
    static void ReleaseHeldButtons()
    {
        foreach (MouseButton button in new[] { MouseButton.Left, MouseButton.Right, MouseButton.Middle, MouseButton.Xbutton1, MouseButton.Xbutton2 })
        {
            if ((_buttons & (MouseButtonMask)(1 << ((int)button - 1))) != 0)
            {
                SendButton(button, pressed: false);
            }
        }
    }

    static bool IsMine(IntPtr device)
    {
        if (!MineByDevice.TryGetValue(device, out bool mine))
        {
            string name = device == IntPtr.Zero ? "(no device handle: precision touchpad or injected)" : DeviceName(device);
            bool touchpad = device == IntPtr.Zero || TouchpadIds.Any(id => name.Contains(id, StringComparison.OrdinalIgnoreCase));
            mine = _selector switch
            {
                "touchpad" => touchpad,
                "mouse" => !touchpad,
                _ => name.Contains(_selector, StringComparison.OrdinalIgnoreCase),
            };
            MineByDevice[device] = mine;
            Log.Info($"[CouchCoop] pointer device {name}: {(mine ? "this window's" : "ignored")}");
        }
        return mine;
    }

    static void Apply(RawMouse mouse)
    {
        if ((mouse.Flags & MOUSE_MOVE_ABSOLUTE) != 0)
        {
            bool desktop = (mouse.Flags & MOUSE_VIRTUAL_DESKTOP) != 0;
            int left = desktop ? GetSystemMetrics(76) : 0, top = desktop ? GetSystemMetrics(77) : 0;
            int width = GetSystemMetrics(desktop ? 78 : 0), height = GetSystemMetrics(desktop ? 79 : 1);
            var screen = new Point { X = left + (int)(mouse.LastX / 65535.0 * width), Y = top + (int)(mouse.LastY / 65535.0 * height) };
            ScreenToClient(_hwnd, ref screen);
            MoveTo(new Vector2(screen.X, screen.Y));
        }
        else if ((mouse.LastX != 0 || mouse.LastY != 0) && _touch == null)
        {
            MoveTo(WindowOwnPointer.Pointer + new Vector2(mouse.LastX, mouse.LastY) * _speed * _systemSpeed);
        }
        ushort flags = mouse.ButtonFlags;
        Press(flags, 0x0001, 0x0002, MouseButton.Left);
        Press(flags, 0x0004, 0x0008, MouseButton.Right);
        Press(flags, 0x0010, 0x0020, MouseButton.Middle);
        Press(flags, 0x0040, 0x0080, MouseButton.Xbutton1);
        Press(flags, 0x0100, 0x0200, MouseButton.Xbutton2);
        if ((flags & 0x0400) != 0)
        {
            Wheel((short)mouse.ButtonData, MouseButton.WheelUp, MouseButton.WheelDown);
        }
        if ((flags & 0x0800) != 0)
        {
            Wheel((short)mouse.ButtonData, MouseButton.WheelRight, MouseButton.WheelLeft);
        }
    }

    // Windows' pointer-speed slider (1-20, default 10) scales cursor movement the same way.
    static float SystemSpeed()
    {
        int speed = 10;
        SystemParametersInfo(0x0070, 0, ref speed, 0);
        return speed / 10f;
    }

    static float _systemSpeed = 1;
    static bool _loggedBackground;

    // The first finger down acts as this window's mouse with its left button held; further
    // fingers are ignored, and the device can't move the pointer until the finger lifts.
    static uint? _touch;

    // A tap lands without any approach, but the game picks targets from hover, which it only
    // updates a frame after the pointer moves; the press waits until it has.
    const int TapSettleFrames = 2;
    static int _pressInFrames;
    static bool _releaseAfterPress;

    public static void TouchDown(uint id, Vector2 at)
    {
        if (_touch != null)
        {
            return;
        }
        _touch = id;
        MoveTo(at);
        SendPendingMotion();
        _pressInFrames = TapSettleFrames;
    }

    public static void TouchMove(uint id, Vector2 at)
    {
        if (_touch == id)
        {
            MoveTo(at);
        }
    }

    // A cancelled touch (the system took the gesture) still lifts the button so nothing stays held.
    public static void TouchUp(uint id, Vector2? at, bool canceled)
    {
        if (_touch != id)
        {
            return;
        }
        if (at is { } lifted && !canceled)
        {
            MoveTo(lifted);
        }
        _touch = null;
        if (_pressInFrames > 0)
        {
            _releaseAfterPress = true;
            return;
        }
        SendButton(MouseButton.Left, pressed: false);
    }

    static void OnFrame()
    {
        SendPendingMotion();
        if (_pressInFrames > 0 && --_pressInFrames == 0)
        {
            SendButton(MouseButton.Left, pressed: true);
            if (_releaseAfterPress)
            {
                _releaseAfterPress = false;
                SendButton(MouseButton.Left, pressed: false);
            }
        }
    }

    // The game moving the mouse (controller switch, reaction wheel) moves this window's pointer.
    public static void WarpTo(Vector2 target) => MoveTo(target);

    // The pointer moves at once (the game reads it any time); Godot gets one motion event per
    // frame, as with Windows' own coalesced cursor messages, sent before the frame's processing.
    static void MoveTo(Vector2 target)
    {
        GetClientRect(_hwnd, out Rect client);
        WindowOwnPointer.Pointer = target.Clamp(Vector2.Zero, new Vector2(Math.Max(0, client.Right - 1), Math.Max(0, client.Bottom - 1)));
    }

    static Vector2 _sentPointer;

    static void SendPendingMotion()
    {
        Vector2 at = WindowOwnPointer.Pointer;
        Vector2 relative = at - _sentPointer;
        if (relative == Vector2.Zero)
        {
            return;
        }
        ulong now = Time.GetTicksUsec();
        float seconds = (float)Math.Max(0.001, (now - _lastMoveUsec) / 1e6);
        _lastMoveUsec = now;
        _sentPointer = at;
        using var motion = new InputEventMouseMotion
        {
            Position = at,
            GlobalPosition = at,
            Relative = relative,
            ScreenRelative = relative,
            Velocity = relative / seconds,
            ScreenVelocity = relative / seconds,
            ButtonMask = _buttons,
        };
        Input.ParseInputEvent(motion);
        SoftwareCursor.Refresh();
    }

    static void Press(ushort flags, ushort down, ushort up, MouseButton button)
    {
        if ((flags & down) != 0)
        {
            SendButton(button, pressed: true);
        }
        if ((flags & up) != 0)
        {
            SendButton(button, pressed: false);
        }
    }

    static void SendButton(MouseButton button, bool pressed, float factor = 1)
    {
        SendPendingMotion();
        bool isWheel = button is >= MouseButton.WheelUp and <= MouseButton.WheelRight;
        var mask = isWheel ? 0 : (MouseButtonMask)(1 << ((int)button - 1));
        _buttons = pressed ? _buttons | mask : _buttons & ~mask;
        Vector2 at = WindowOwnPointer.Pointer;
        bool doubleClick = false;
        if (pressed && button <= MouseButton.Middle)
        {
            ulong now = Time.GetTicksUsec();
            doubleClick = _lastPress.Button == button && now - _lastPress.Usec <= GetDoubleClickTime() * 1000UL
                          && _lastPress.At.DistanceTo(at) <= GetSystemMetrics(36);
            _lastPress = doubleClick ? default : (button, now, at);
        }
        using var click = new InputEventMouseButton
        {
            ButtonIndex = button,
            Pressed = pressed,
            Position = at,
            GlobalPosition = at,
            ButtonMask = _buttons,
            DoubleClick = doubleClick,
            Factor = factor,
        };
        Input.ParseInputEvent(click);
        SoftwareCursor.Refresh();
    }

    static void Wheel(short delta, MouseButton positive, MouseButton negative)
    {
        MouseButton button = delta > 0 ? positive : negative;
        float factor = Math.Abs(delta) / 120f;
        SendButton(button, pressed: true, factor);
        SendButton(button, pressed: false, factor);
    }

    // Holds the system cursor on one hidden spot in the host while a couch window is in front,
    // so neither device's movement reaches other apps. Alt-Tab away releases it.
    static void ParkSystemCursor()
    {
        bool parked = false;
        while (true)
        {
            try
            {
                if (IsCouchWindow(GetForegroundWindow()) && GetClientRect(_hwnd, out Rect client))
                {
                    var center = new Point { X = client.Right / 2, Y = client.Bottom / 2 };
                    ClientToScreen(_hwnd, ref center);
                    var spot = new Rect { Left = center.X, Top = center.Y, Right = center.X + 1, Bottom = center.Y + 1 };
                    if (!GetClipCursor(out Rect current) || !current.Equals(spot))
                    {
                        ClipCursor(ref spot);
                    }
                    parked = true;
                }
                else if (parked)
                {
                    ClipCursor(IntPtr.Zero);
                    parked = false;
                }
            }
            catch (Exception e)
            {
                Log.Error($"[CouchCoop] cursor park: {e.Message}");
            }
            Thread.Sleep(100);
        }
    }

    public static bool CouchWindowInFront() => IsCouchWindow(GetForegroundWindow());

    static readonly ConcurrentDictionary<IntPtr, bool> CouchWindows = new();

    static bool IsCouchWindow(IntPtr hwnd) => hwnd != IntPtr.Zero && CouchWindows.GetOrAdd(hwnd, h =>
    {
        GetWindowThreadProcessId(h, out uint pid);
        try
        {
            return Process.GetProcessById((int)pid).ProcessName == Process.GetCurrentProcess().ProcessName;
        }
        catch (ArgumentException)
        {
            return false;
        }
    });

    static string DeviceName(IntPtr device)
    {
        uint length = 0;
        GetRawInputDeviceInfo(device, RIDI_DEVICENAME, null, ref length);
        var name = new StringBuilder((int)length + 1);
        GetRawInputDeviceInfo(device, RIDI_DEVICENAME, name, ref length);
        return name.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RawInputDevice { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }

    [StructLayout(LayoutKind.Sequential)]
    struct RawInputHeader { public uint Type, Size; public IntPtr Device, WParam; }

    [StructLayout(LayoutKind.Sequential)]
    struct RawMouse
    {
        public ushort Flags;
        ushort _padding;
        public ushort ButtonFlags, ButtonData;
        public uint RawButtons;
        public int LastX, LastY;
        public uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint count, uint size);

    [DllImport("user32.dll")]
    static extern uint GetRegisteredRawInputDevices([Out] RawInputDevice[] devices, ref uint count, uint size);

    [DllImport("user32.dll")]
    static extern uint GetRawInputData(IntPtr rawInput, uint command, IntPtr data, ref uint size, uint headerSize);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, StringBuilder? data, ref uint size);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    static extern bool GetClientRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    static extern bool ClientToScreen(IntPtr hwnd, ref Point point);

    [DllImport("user32.dll")]
    static extern bool ScreenToClient(IntPtr hwnd, ref Point point);

    [DllImport("user32.dll")]
    static extern bool ClipCursor(ref Rect rect);

    [DllImport("user32.dll")]
    static extern bool GetClipCursor(out Rect rect);

    [DllImport("user32.dll")]
    static extern bool ClipCursor(IntPtr rect);

    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    static extern bool SystemParametersInfo(uint action, uint param, ref int value, uint winIni);
}
