using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace CouchCoop;

// Lets one player use touch while another uses the mouse. Godot reads touch through WM_TOUCH,
// and Windows turns every touch into mouse input too, which jumps the shared system cursor
// away from the mouse player. Touch pointer messages are handled here and never reach
// DefWindowProc, the step that promotes them to mouse input
// (https://devblogs.microsoft.com/oldnewthing/20210728-00/?p=105487). Godot then gets the
// touches as screen-touch events, and its own touch-to-mouse emulation drives the game.
static class TouchWithoutCursor
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

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

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
        Log.Info(_godotProc == IntPtr.Zero
            ? $"[CouchCoop] could not hook window {hwnd} for touch: error {Marshal.GetLastPInvokeError()}"
            : "[CouchCoop] touch no longer moves the mouse cursor");
    }

    static IntPtr Hook(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
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
        // Godot rewrites the window style whenever the window moves or resizes.
        if (msg == WM_STYLECHANGING && (int)wParam == GWL_EXSTYLE)
        {
            // STYLESTRUCT { DWORD styleOld; DWORD styleNew; }
            int styleNew = Marshal.ReadInt32(lParam, 4);
            Marshal.WriteInt32(lParam, 4, styleNew | (int)WS_EX_NOACTIVATE);
        }
        IntPtr result = CallWindowProc(_godotProc, hwnd, msg, wParam, lParam);
        // Touch never reaches here as mouse input, so this is a real mouse click, which should
        // still focus its window despite the no-activate style.
        if (msg == WM_MOUSEACTIVATE && GetForegroundWindow() != hwnd)
        {
            SetForegroundWindow(hwnd);
        }
        return result;
    }

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

    static void SendTouch(int index, Vector2 position, bool pressed, bool canceled) =>
        Input.ParseInputEvent(new InputEventScreenTouch
        {
            Index = index,
            Position = position,
            Pressed = pressed,
            Canceled = canceled,
        });

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
