using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace CouchCoop;

// The couch window's message hook. With one device per window (SeatPointer), it feeds this
// window only its own device's raw input. Otherwise it lets one player use touch while another
// uses the mouse. Godot reads touch through WM_TOUCH,
// and Windows turns every touch into mouse input too, which jumps the shared system cursor
// away from the mouse player. Touch pointer messages are handled here and never reach
// DefWindowProc, the step that promotes them to mouse input
// (https://devblogs.microsoft.com/oldnewthing/20210728-00/?p=105487). Godot then gets the
// touches as screen-touch events, and its own touch-to-mouse emulation drives the game.
static class CouchWindow
{
    const uint WM_POINTERUPDATE = 0x0245;
    const uint WM_POINTERDOWN = 0x0246;
    const uint WM_POINTERUP = 0x0247;
    const uint WM_POINTERENTER = 0x0249;
    const uint WM_POINTERLEAVE = 0x024A;
    const uint WM_POINTERACTIVATE = 0x024B;
    const uint WM_POINTERCAPTURECHANGED = 0x024C;
    const int PA_NOACTIVATE = 3;
    const int PT_TOUCH = 2;
    const uint POINTER_FLAG_CANCELED = 0x8000;
    const int GWLP_WNDPROC = -4;
    const int GWL_EXSTYLE = -20;
    const long WS_EX_NOACTIVATE = 0x08000000;
    const long WS_EX_APPWINDOW = 0x00040000;
    const uint WM_MOUSEACTIVATE = 0x0021;
    const uint WM_STYLECHANGING = 0x007C;
    const uint WM_INPUT = 0x00FF;
    const uint WM_ACTIVATE = 0x0006;
    const uint WM_TIMER = 0x0113;
    const uint WM_EXITSIZEMOVE = 0x0232;
    const int MA_NOACTIVATEANDEAT = 4;
    const uint WM_MOUSEHOVER = 0x02A1;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    static extern IntPtr CallWindowProc(IntPtr previous, IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool GetPointerType(uint pointerId, out int pointerType);

    [DllImport("user32.dll")]
    static extern bool ScreenToClient(IntPtr hwnd, ref Point point);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hwnd);

    [StructLayout(LayoutKind.Sequential)]
    struct Point { public int X, Y; }

    // Held for the life of the process: the window calls back into it.
    static WndProc? _hook;
    static IntPtr _godotProc;
    // Windows pointer ids grow without bound; Godot expects small finger indices.
    static readonly Dictionary<uint, (int Index, Vector2 Position)> Fingers = new();

    public static void Install()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var hwnd = (IntPtr)DisplayServer.WindowGetNativeHandle(DisplayServer.HandleType.WindowHandle);
        _hook = Hook;
        _godotProc = SetWindowLongPtr(hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_hook));
        // Telling Windows a touch shouldn't activate this window still takes the foreground from
        // the other window and leaves no window focused; the no-activate style, as the on-screen
        // keyboard uses, leaves the foreground alone. App-window keeps the taskbar button.
        long style = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)(style | WS_EX_NOACTIVATE | WS_EX_APPWINDOW));
        if (_godotProc == IntPtr.Zero)
        {
            Log.Error($"[CouchCoop] could not hook window {hwnd}: error {Marshal.GetLastPInvokeError()}");
            return;
        }
        if (SeatPointer.Active)
        {
            SeatPointer.Install(hwnd);
        }
        else
        {
            Log.Info("[CouchCoop] touch no longer moves the mouse cursor");
        }
    }

    public static IntPtr CallGodot(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam) =>
        CallWindowProc(_godotProc, hwnd, msg, wParam, lParam);

    static IntPtr Hook(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (SeatPointer.Active && OwnDeviceOnly(hwnd, msg, wParam, lParam) is { } handled)
        {
            return handled;
        }
        if (msg is >= WM_POINTERUPDATE and <= WM_POINTERCAPTURECHANGED
            && GetPointerType(PointerId(wParam), out int type) && type == PT_TOUCH)
        {
            try
            {
                OnTouch(hwnd, msg, wParam, lParam);
            }
            catch (Exception e)
            {
                Log.Error($"[CouchCoop] touch message {msg:X} failed: {e.Message}");
            }
            return msg == WM_POINTERACTIVATE ? PA_NOACTIVATE : IntPtr.Zero;
        }
        // The mouse player's cursor passing over this window mid-touch would take over the drag.
        if (Fingers.Count > 0 && IsMouseMessage(msg))
        {
            return IntPtr.Zero;
        }
        if (IsMouseMessage(msg) && msg != 0x02A3 && msg is not (0x020A or 0x020E))
        {
            WindowOwnPointer.Pointer = new Vector2((short)((long)lParam & 0xFFFF), (short)(((long)lParam >> 16) & 0xFFFF));
        }
        // Godot rewrites the window style whenever the window moves or resizes.
        if (msg == WM_STYLECHANGING && (int)wParam == GWL_EXSTYLE)
        {
            // STYLESTRUCT { DWORD styleOld; DWORD styleNew; }
            int styleNew = Marshal.ReadInt32(lParam, 4);
            Marshal.WriteInt32(lParam, 4, styleNew | (int)WS_EX_NOACTIVATE);
        }
        IntPtr result = CallWindowProc(_godotProc, hwnd, msg, wParam, lParam);
        // Godot re-registers its own focus-following raw mouse input on activation and after a
        // move or resize, which would cut this window off from its device in the background.
        if (SeatPointer.Active && msg is WM_ACTIVATE or WM_TIMER or WM_EXITSIZEMOVE)
        {
            SeatPointer.Register();
        }
        // Touch never reaches here as mouse input, so this is a real mouse click, which should
        // still focus its window despite the no-activate style.
        if (msg == WM_MOUSEACTIVATE && GetForegroundWindow() != hwnd)
        {
            SetForegroundWindow(hwnd);
        }
        return result;
    }

    // With one device per window, this window's pointer comes only from its device's raw input;
    // the system cursor's messages (driven by every device) and touches are dropped.
    static IntPtr? OwnDeviceOnly(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_INPUT)
        {
            try
            {
                SeatPointer.OnRawInput(wParam, lParam);
            }
            catch (Exception e)
            {
                Log.Error($"[CouchCoop] raw input failed: {e.Message}");
            }
            return DefWindowProc(hwnd, msg, wParam, lParam);
        }
        if (IsMouseMessage(msg) || msg == WM_MOUSEHOVER)
        {
            return IntPtr.Zero;
        }
        // A click here comes from the parked system cursor, not this window's device; it only
        // brings the game back when another app is in front.
        if (msg == WM_MOUSEACTIVATE && SeatPointer.CouchWindowInFront())
        {
            return MA_NOACTIVATEANDEAT;
        }
        if (msg is >= WM_POINTERUPDATE and <= WM_POINTERCAPTURECHANGED
            && GetPointerType(PointerId(wParam), out int type) && type == PT_TOUCH)
        {
            return msg == WM_POINTERACTIVATE ? PA_NOACTIVATE : IntPtr.Zero;
        }
        return null;
    }

    static bool IsMouseMessage(uint msg) => msg is >= 0x0200 and <= 0x020E or 0x02A3;

    static uint PointerId(IntPtr wParam) => (uint)((ulong)wParam & 0xFFFF);

    static void OnTouch(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        uint id = PointerId(wParam);
        bool canceled = (((ulong)wParam >> 16) & POINTER_FLAG_CANCELED) != 0;
        switch (msg)
        {
            case WM_POINTERDOWN:
                Vector2 down = ClientPosition(hwnd, lParam);
                Fingers[id] = (FreeIndex(), down);
                SendTouch(Fingers[id].Index, down, pressed: true, canceled: false);
                break;
            case WM_POINTERUPDATE when Fingers.TryGetValue(id, out var finger):
                Vector2 now = ClientPosition(hwnd, lParam);
                if (now != finger.Position)
                {
                    Input.ParseInputEvent(new InputEventScreenDrag
                    {
                        Index = finger.Index,
                        Position = now,
                        Relative = now - finger.Position,
                        ScreenRelative = now - finger.Position,
                    });
                    Fingers[id] = (finger.Index, now);
                    FollowFirstFinger(finger.Index, now);
                }
                break;
            case WM_POINTERUP when Fingers.Remove(id, out var lifted):
                SendTouch(lifted.Index, ClientPosition(hwnd, lParam), pressed: false, canceled);
                break;
            case WM_POINTERCAPTURECHANGED when Fingers.Remove(id, out var lost):
                SendTouch(lost.Index, lost.Position, pressed: false, canceled: true);
                break;
        }
    }

    static void SendTouch(int index, Vector2 position, bool pressed, bool canceled)
    {
        FollowFirstFinger(index, position);
        Input.ParseInputEvent(new InputEventScreenTouch
        {
            Index = index,
            Position = position,
            Pressed = pressed,
            Canceled = canceled,
        });
    }

    // Godot turns the first finger into this window's mouse.
    static void FollowFirstFinger(int index, Vector2 position)
    {
        if (index == 0)
        {
            WindowOwnPointer.Pointer = position;
        }
    }

    static int FreeIndex() =>
        Enumerable.Range(0, Fingers.Count + 1).First(i => Fingers.Values.All(f => f.Index != i));

    // Pointer messages carry screen coordinates in physical pixels, as Godot's window does.
    static Vector2 ClientPosition(IntPtr hwnd, IntPtr lParam)
    {
        long packed = (long)lParam;
        var point = new Point { X = (short)(packed & 0xFFFF), Y = (short)((packed >> 16) & 0xFFFF) };
        ScreenToClient(hwnd, ref point);
        return new Vector2(point.X, point.Y);
    }
}
