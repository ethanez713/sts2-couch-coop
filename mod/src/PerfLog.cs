using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace CouchCoop;

// Frame timing for each couch window, written to its game log so it sits among what the game was
// doing, with a wall-clock time so the two windows' logs line up. Every report splits a frame's
// time into game logic, render CPU and GPU; a frame much longer than all three was waiting on
// something outside the game (vsync, the compositor, the OS scheduler). Long frames are also
// logged one by one. grep "[CouchCoop] perf" in couch_host.log and couch_client.log.
static class PerfLog
{
    const double ReportSeconds = 10;
    const double HitchMs = 50;
    const double LoggedHitchMs = 100;
    const int MaxLoggedHitchesPerReport = 10;

    static readonly double[] FrameMs = new double[8192];
    static readonly double[] Sorted = new double[8192];
    static int _frames, _hitches, _loggedHitches;
    static long _lastFrame, _reportStart;
    static double _processMsSum, _processMsMax, _renderCpuMsSum, _gpuMsSum, _gpuMsMax;
    static int _fgSelf, _fgCouch, _fgOther;
    static IntPtr _hwnd;
    static Rid _viewport;
    static TimeSpan _cpuAtReport, _gcPauseAtReport, _gcPauseAtFrame;
    static (int Gen0, int Gen1, int Gen2) _gcAtReport, _gcAtFrame;
    static long _rawInputAtReport, _rawInputTicksAtReport, _rawInputAtFrame, _rawInputTicksAtFrame;

    // Every WM_INPUT this window handled, and the time spent in it, on the main thread.
    static long _rawInputs, _rawInputTicks;

    public static void RawInputHandled(long ticks)
    {
        _rawInputs++;
        _rawInputTicks += ticks;
    }

    public static void Install()
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        _viewport = tree.Root.GetViewportRid();
        RenderingServer.ViewportSetMeasureRenderTime(_viewport, true);
        if (OperatingSystem.IsWindows())
        {
            _hwnd = (IntPtr)DisplayServer.WindowGetNativeHandle(DisplayServer.HandleType.WindowHandle);
        }
        using (Process self = Process.GetCurrentProcess())
        {
            _cpuAtReport = self.TotalProcessorTime;
            string screens = string.Join(", ", Enumerable.Range(0, DisplayServer.GetScreenCount()).Select(i =>
                $"{i}: {DisplayServer.ScreenGetSize(i).X}x{DisplayServer.ScreenGetSize(i).Y} at {DisplayServer.ScreenGetPosition(i)} {DisplayServer.ScreenGetRefreshRate(i):0.#} Hz"));
            Log.Info($"[CouchCoop] perf seat {EndTurnForAll.OwnSeat()} pid {self.Id} priority {self.PriorityClass}, "
                     + $"{System.Environment.ProcessorCount} logical CPUs; GPU {RenderingServer.GetVideoAdapterName()} "
                     + $"(driver {string.Join(" ", OS.GetVideoAdapterDriverInfo())}, {RenderingServer.GetVideoAdapterApiVersion()}); "
                     + $"screens {screens}; report every {ReportSeconds} s, hitch = frame over {HitchMs} ms");
        }
        _gcPauseAtReport = _gcPauseAtFrame = GC.GetTotalPauseDuration();
        _gcAtReport = GcCounts();
        _gcAtFrame = GcCounts();
        _reportStart = _lastFrame = Stopwatch.GetTimestamp();
        tree.ProcessFrame += OnFrame;
    }

    // Called at the start of each frame, so the interval since the last call is the previous frame,
    // and the engine's process and render timings describe that same frame.
    static void OnFrame()
    {
        long now = Stopwatch.GetTimestamp();
        double frameMs = Stopwatch.GetElapsedTime(_lastFrame, now).TotalMilliseconds;
        _lastFrame = now;
        double processMs = Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000;
        double renderCpuMs = RenderingServer.ViewportGetMeasuredRenderTimeCpu(_viewport) + RenderingServer.GetFrameSetupTimeCpu();
        double gpuMs = RenderingServer.ViewportGetMeasuredRenderTimeGpu(_viewport);
        if (_frames < FrameMs.Length)
        {
            FrameMs[_frames++] = frameMs;
        }
        _processMsSum += processMs;
        _processMsMax = Math.Max(_processMsMax, processMs);
        _renderCpuMsSum += renderCpuMs;
        _gpuMsSum += gpuMs;
        _gpuMsMax = Math.Max(_gpuMsMax, gpuMs);
        string foreground = Foreground();
        switch (foreground)
        {
            case "self": _fgSelf++; break;
            case "other couch window": _fgCouch++; break;
            default: _fgOther++; break;
        }

        var gc = GcCounts();
        TimeSpan gcPause = GC.GetTotalPauseDuration();
        if (frameMs > HitchMs)
        {
            _hitches++;
            if (frameMs > LoggedHitchMs && _loggedHitches < MaxLoggedHitchesPerReport)
            {
                _loggedHitches++;
                Log.Info($"[CouchCoop] perf {Clock()} hitch {frameMs:0} ms: process {processMs:0.0} ms, render cpu {renderCpuMs:0.0} ms, "
                         + $"gpu {gpuMs:0.0} ms, gc {GcDelta(gc, _gcAtFrame)} pause {(gcPause - _gcPauseAtFrame).TotalMilliseconds:0.0} ms, "
                         + $"raw input {_rawInputs - _rawInputAtFrame} msgs {Ms(_rawInputTicks - _rawInputTicksAtFrame):0.0} ms, foreground {foreground}");
            }
        }
        _gcAtFrame = gc;
        _gcPauseAtFrame = gcPause;
        _rawInputAtFrame = _rawInputs;
        _rawInputTicksAtFrame = _rawInputTicks;

        if (Stopwatch.GetElapsedTime(_reportStart, now).TotalSeconds >= ReportSeconds)
        {
            Report(now, gc, gcPause);
        }
    }

    static void Report(long now, (int Gen0, int Gen1, int Gen2) gc, TimeSpan gcPause)
    {
        double seconds = Stopwatch.GetElapsedTime(_reportStart, now).TotalSeconds;
        int n = _frames;
        Array.Copy(FrameMs, Sorted, n);
        Array.Sort(Sorted, 0, n);
        double Percentile(double p) => n == 0 ? 0 : Sorted[Math.Min(n - 1, (int)(p * n))];
        TimeSpan cpu;
        using (Process self = Process.GetCurrentProcess())
        {
            cpu = self.TotalProcessorTime;
        }
        int screen = DisplayServer.WindowGetCurrentScreen();
        int frames = Math.Max(1, n);
        Log.Info($"[CouchCoop] perf {Clock()} {n / seconds:0.0} fps; frame ms avg {(n == 0 ? 0 : FrameMs.Take(n).Average()):0.0} "
                 + $"p50 {Percentile(0.5):0.0} p95 {Percentile(0.95):0.0} p99 {Percentile(0.99):0.0} max {(n == 0 ? 0 : Sorted[n - 1]):0} hitches {_hitches}; "
                 + $"process avg {_processMsSum / frames:0.0} max {_processMsMax:0.0}, render cpu avg {_renderCpuMsSum / frames:0.0}, "
                 + $"gpu avg {_gpuMsSum / frames:0.0} max {_gpuMsMax:0.0}; "
                 + $"process cpu {(cpu - _cpuAtReport).TotalSeconds / seconds * 100:0}% of a core; "
                 + $"gc {GcDelta(gc, _gcAtReport)} pause {(gcPause - _gcPauseAtReport).TotalMilliseconds:0.0} ms heap {GC.GetTotalMemory(false) >> 20} MB; "
                 + $"raw input {(_rawInputs - _rawInputAtReport) / seconds:0}/s {Ms(_rawInputTicks - _rawInputTicksAtReport) / seconds:0.00} ms/s; "
                 + $"foreground self {_fgSelf * 100 / frames}% other couch window {_fgCouch * 100 / frames}% other app {_fgOther * 100 / frames}%; "
                 + $"screen {screen} {DisplayServer.ScreenGetRefreshRate(screen):0.#} Hz {DisplayServer.WindowGetMode()} vsync {DisplayServer.WindowGetVsyncMode()} max fps {Engine.MaxFps}; "
                 + $"draw calls {Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame):0} "
                 + $"vram {Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / (1 << 20):0} MB nodes {Performance.GetMonitor(Performance.Monitor.ObjectNodeCount):0}");
        _reportStart = now;
        _frames = _hitches = _loggedHitches = _fgSelf = _fgCouch = _fgOther = 0;
        _processMsSum = _processMsMax = _renderCpuMsSum = _gpuMsSum = _gpuMsMax = 0;
        _cpuAtReport = cpu;
        _gcAtReport = gc;
        _gcPauseAtReport = gcPause;
        _rawInputAtReport = _rawInputs;
        _rawInputTicksAtReport = _rawInputTicks;
    }

    static string Foreground()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return "unknown";
        }
        IntPtr front = GetForegroundWindow();
        return front == _hwnd ? "self" : SeatPointer.IsCouchWindow(front) ? "other couch window" : "other app";
    }

    static (int Gen0, int Gen1, int Gen2) GcCounts() => (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));

    static string GcDelta((int Gen0, int Gen1, int Gen2) now, (int Gen0, int Gen1, int Gen2) before) =>
        $"gen0 +{now.Gen0 - before.Gen0} gen1 +{now.Gen1 - before.Gen1} gen2 +{now.Gen2 - before.Gen2}";

    static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    static string Clock() => DateTime.Now.ToString("HH:mm:ss.fff");

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();
}
