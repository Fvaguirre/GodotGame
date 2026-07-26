using Godot;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

// Top-level diagnostics for crashes / unhandled exceptions / main-thread FREEZES (the "not responding" hangs).
//
//   • Global handlers (AppDomain unhandled + unobserved Task) log the FULL stack to a file and the Output panel, so a crash
//     leaves a record even if the console scrolls away or the window dies.
//   • A background WATCHDOG thread watches a heartbeat the main loop bumps every frame. If the main thread goes silent past
//     FreezeMs, it writes a "[FREEZE]" line (with how long, and the last breadcrumb phase) — the one signal you can't get from
//     inside a hung main thread. It logs recovery too, so you see freeze duration.
//   • Call Mark("...") before a heavy/suspect section so a freeze report names WHERE it hung. Guard(...) wraps a block to log
//     any exception with its phase, then rethrows (behaviour unchanged, but recorded).
//
// Log file: user://crash.log  (path printed at Install). File writes are lock-guarded so the watchdog thread is safe.
public static class CrashLogger
{
    private const int FreezeMs = 3000;          // main thread silent this long ⇒ report a freeze
    private static readonly object _fileLock = new();
    private static string _logPath;
    private static long _lastBeatMs;            // TickCount64 of the last main-thread heartbeat
    private static volatile bool _frozen;
    private static volatile string _phase = "startup";
    private static bool _installed;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        _logPath = ProjectSettings.GlobalizePath("user://crash.log");

        LogBoth($"=== session start {DateTime.Now:yyyy-MM-dd HH:mm:ss} === (log: {_logPath})");

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            LogFile($"[UNHANDLED{(e.IsTerminating ? " · TERMINATING" : "")}] phase='{_phase}'\n{ex}");
            try { GD.PushError($"[CrashLogger] UNHANDLED in '{_phase}': {ex?.Message}"); } catch { }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogFile($"[UNOBSERVED TASK] {e.Exception}");
            e.SetObserved();
        };

        _lastBeatMs = System.Environment.TickCount64;
        var watchdog = new Thread(Watch) { IsBackground = true, Name = "CrashWatchdog" };
        watchdog.Start();
    }

    // Call once per frame from the main loop (heartbeat). Optionally names the current phase.
    public static void Beat(string phase = null)
    {
        _lastBeatMs = System.Environment.TickCount64;
        if (phase != null) _phase = phase;
        if (_frozen)
        {
            _frozen = false;
            LogBoth($"[RECOVERED] main thread resumed (had stalled in phase '{_phase}')");
        }
    }

    // Coarse breadcrumb: set before a heavy/suspect section so a freeze report can name it.
    public static void Mark(string phase) => _phase = phase;

    // Wrap a block so any exception is logged with its phase and rethrown (behaviour unchanged, but recorded).
    public static void Guard(string phase, Action body)
    {
        _phase = phase;
        try { body(); }
        catch (Exception ex)
        {
            LogFile($"[EXCEPTION in {phase}]\n{ex}");
            try { GD.PushError($"[CrashLogger] {phase}: {ex.Message}"); } catch { }
            throw;
        }
    }

    private static void Watch()
    {
        while (true)
        {
            Thread.Sleep(500);
            long silent = System.Environment.TickCount64 - _lastBeatMs;
            if (silent > FreezeMs && !_frozen)
            {
                _frozen = true;
                // file-only (GD.* isn't safe off the main thread, and the main thread is hung anyway)
                LogFile($"[FREEZE] main thread unresponsive for {silent} ms — last phase '{_phase}'");
            }
        }
    }

    public static void LogFile(string msg)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff} {msg}";
        lock (_fileLock)
        {
            try { if (_logPath != null) File.AppendAllText(_logPath, line + "\n"); } catch { }
        }
    }

    // File + Output panel (main thread only).
    public static void LogBoth(string msg)
    {
        LogFile(msg);
        try { GD.Print("[CrashLog] " + msg); } catch { }
    }
}
