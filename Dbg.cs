using Godot;

// Dbg.cs — a tiny trace log for diagnosing HARD FREEZES. Every call flushes to disk immediately, so when the game locks up
// the LAST LINE in the log is whatever step it froze on (an infinite loop / runaway alloc never gets to write its "done").
//   • Log file:  user://trace.log   (on Windows: %APPDATA%\Godot\app_userdata\<project>\trace.log — path is printed on first write)
//   • Toggle at runtime with the dev-console command:  trace
// Leave it on while reproducing the freeze, then read the tail of trace.log.
public static class Dbg
{
    public static bool On = true;
    private static FileAccess _f;
    private static bool _tried;

    public static void Log(string s)
    {
        if (!On) return;
        if (!_tried)
        {
            _tried = true;
            try { _f = FileAccess.Open("user://trace.log", FileAccess.ModeFlags.Write); GD.Print("[TRACE] logging to ", OS.GetUserDataDir(), "/trace.log"); }
            catch (System.Exception e) { GD.PushWarning("[TRACE] open failed: " + e.Message); }
        }
        GD.Print("[TRACE] " + s);
        if (_f != null) { _f.StoreLine(Time.GetTicksMsec() + "  " + s); _f.Flush(); }   // flush() → survives a freeze
    }
}
