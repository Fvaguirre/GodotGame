using Godot;
using System;

// DevConsole.cs — a developer console for testing. Toggle with the ~ (backtick) key. It grants/removes loadout
// (spell combos = finishers, minors, right-click modifiers, ultimates), bumps slot capacities, and toggles god
// mode. UIDs are the enum names lower-cased, enumerated live via reflection, so the listall* commands and the
// parser stay in sync automatically as we add content. Mutations target the LOCAL player (player1); on a 2-PC
// LAN test, open the console on each machine to drive its own player. Routed through Game.ConsoleOpen so typing
// never leaks into gameplay.
public partial class DevConsole : CanvasLayer
{
    private PanelContainer _panel;
    private RichTextLabel _log;
    private LineEdit _input;
    public bool IsOpen { get; private set; }

    // command history — Up/Down cycle through previously-entered lines
    private readonly System.Collections.Generic.List<string> _history = new();
    private int _histIdx = 0;   // index into _history; == Count means the live (blank) line

    // witch uid -> index (index MUST match Game.ConfigureWitch's cases). Update alongside a new witch.
    private static readonly (string uid, string name)[] Witches =
    {
        ("lunar",   "The Lunar Witch"),
        ("divine",  "The Divine Witch"),
        ("crimson", "The Crimson Blood Witch"),
        ("verdant", "The Verdant Witch"),
        ("gale",    "The Gale Witch"),
        ("frost",   "The Frost Witch"),
        ("forsaken","The Forsaken Witch"),
        ("ember",   "The Ember Witch"),
        ("arcane",  "The Arcane Witch"),
    };

    public override void _Ready()
    {
        Layer = 128;
        _panel = new PanelContainer();
        AddChild(_panel);
        _panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopWide);
        _panel.OffsetBottom = 340;
        _panel.Visible = false;

        var vb = new VBoxContainer();
        _panel.AddChild(vb);

        _log = new RichTextLabel { ScrollFollowing = true, SelectionEnabled = true, SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 300) };
        vb.AddChild(_log);

        _input = new LineEdit { PlaceholderText = "type a command — try 'help'" };
        vb.AddChild(_input);
        _input.TextSubmitted += OnSubmit;
        _input.GuiInput += OnInputGui;   // intercept Up/Down for history

        Print("Dev console ready — toggle with ~ . Type 'help'.  (Up/Down = command history)");
    }

    public override void _Input(InputEvent e)
    {
        if (e is InputEventKey k && k.Pressed && !k.Echo && k.Keycode == Key.Quoteleft)
        {
            Toggle();
            GetViewport().SetInputAsHandled();
        }
    }

    private void Toggle()
    {
        IsOpen = !IsOpen;
        _panel.Visible = IsOpen;
        if (Game.I != null) Game.I.ConsoleOpen = IsOpen;
        if (IsOpen)
        {
            _input.Clear();
            _histIdx = _history.Count;
            Input.MouseMode = Input.MouseModeEnum.Visible;
            _input.CallDeferred(Control.MethodName.GrabFocus);   // deferred so focus reliably lands on the box the first time it's shown
        }
        else _input.ReleaseFocus();
    }

    private void OnSubmit(string text)
    {
        Exec(text);
        string t = (text ?? "").Trim();
        if (t.Length > 0 && (_history.Count == 0 || _history[_history.Count - 1] != t)) _history.Add(t);   // record (skip immediate repeats)
        _histIdx = _history.Count;   // reset browsing to the live line
        _input.Clear();
        _input.GrabFocus();
    }

    // Up/Down while typing scroll through previously-entered commands
    private void OnInputGui(InputEvent e)
    {
        if (e is InputEventKey k && k.Pressed && !k.Echo)
        {
            if (k.Keycode == Key.Up) { NavHistory(-1); _input.AcceptEvent(); }
            else if (k.Keycode == Key.Down) { NavHistory(1); _input.AcceptEvent(); }
        }
    }

    private void NavHistory(int dir)
    {
        if (_history.Count == 0) return;
        _histIdx = Mathf.Clamp(_histIdx + dir, 0, _history.Count);
        _input.Text = _histIdx >= _history.Count ? "" : _history[_histIdx];
        _input.CaretColumn = _input.Text.Length;   // caret to the end of the recalled line
    }

    private void Print(string s) { _log?.AppendText(s + "\n"); }

    // ---- enum helpers (uid = lower-cased enum name) ----
    private static bool TryEnum<T>(string uid, out T val) where T : struct, Enum
    {
        uid = (uid ?? "").Trim().ToLowerInvariant();
        foreach (T v in Enum.GetValues<T>())
            if (v.ToString().ToLowerInvariant() == uid) { val = v; return true; }
        val = default;
        return false;
    }

    private static Key ParseKey(string s)
    {
        s = (s ?? "").Trim();
        if (s.Length == 0) return Key.None;
        if (s.Length == 1 && char.IsDigit(s[0]) && Enum.TryParse<Key>("Key" + s, out var kd)) return kd;
        if (Enum.TryParse<Key>(s, true, out var k)) return k;                 // "Q", "F1", ...
        if (s.Length == 1 && Enum.TryParse<Key>(s.ToUpperInvariant(), out var kl)) return kl;
        return Key.None;
    }

    private static string KeyLabel(Key k)
    {
        string s = k.ToString();
        return s.StartsWith("Key") && s.Length == 4 ? s.Substring(3) : s;   // Key1 -> 1
    }

    // ---- command dispatch ----
    private void Exec(string line)
    {
        line = (line ?? "").Trim();
        if (line.Length == 0) return;
        Print("> " + line);
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string cmd = parts[0].ToLowerInvariant();

        switch (cmd)
        {
            case "help": Help(); return;
            case "trace":
                Dbg.On = !(parts.Length > 1 && parts[1].ToLowerInvariant() == "off");
                if (Dbg.On) Dbg.Log("trace enabled via console");
                Print($"trace {(Dbg.On ? "ON" : "OFF")} → {OS.GetUserDataDir()}/trace.log");
                return;
            case "perf": case "fps": case "netstats":
            {
                var g = Game.I; if (g == null) { Print("no game."); return; }
                bool on = !(parts.Length > 1 && parts[1].ToLowerInvariant() == "off");
                if (parts.Length <= 1) on = !g.PerfOverlay;   // bare 'perf' toggles
                g.NetMgr?.BroadcastPerfOverlay(on);           // fans out to the whole lobby
                if (g.NetMgr == null || !g.NetMgr.Active) g.PerfOverlay = on;   // solo: set locally
                Print($"perf overlay {(on ? "ON" : "OFF")} (whole lobby)");
                return;
            }
            case "routes": case "unlockroutes": case "hiddenroutes":
            {
                // bare 'routes' TOGGLES: if any of the 27 are still hidden, reveal them all; otherwise wipe the catalogue.
                bool on = Perks.DiscoveredCount < Perks.RouteTotal;
                if (parts.Length > 1)
                {
                    string a = parts[1].ToLowerInvariant();
                    on = a == "on" || a == "all" || a == "true" || a == "1";
                }
                Perks.SetAllDiscovered(on);
                Print(on
                    ? $"hidden routes: ALL {Perks.RouteTotal} catalogued (9 witches x 3) — open the Coven page to read their names + node paths."
                    : "hidden routes: catalogue CLEARED — all 27 are '??? undiscovered' again.");
                Print("  (this is the discovery log only; a route still fires in a run when you actually own its node-set)");
                return;
            }
            case "listplayers": ListPlayers(); return;
            case "listallspellcombos": ListCombos(); return;
            case "listallspellcombominors": ListMinors(); return;
            case "listallspellmodifiers": ListMods(); return;
            case "listallultimates": ListUlts(); return;
            case "listallwitches": ListWitches(); return;
            case "listallzombies": case "listfoes": case "listallfoes": ListFoes(); return;
            case "spawnfoe": case "spawnfoes": case "spawn": SpawnFoe(parts); return;
            case "audio":   // (DIAGNOSTIC) isolate a periodic click: mute the looping beds one at a time and listen
            {
                var sx = Game.I?.Sfx; if (sx == null) { Print("no audio."); return; }
                string which = parts.Length >= 2 ? parts[1].ToLowerInvariant() : "";
                if (which == "drums") sx.MuteDrums = !sx.MuteDrums;
                else if (which == "music") sx.MuteMusic = !sx.MuteMusic;
                else if (which == "on") { sx.MuteDrums = false; sx.MuteMusic = false; }
                else { Print("audio drums | audio music | audio on  — toggle each looping bed to find what's ticking."); return; }
                Print($"drums {(sx.MuteDrums ? "MUTED" : "on")}, music {(sx.MuteMusic ? "MUTED" : "on")}. If the tick survives BOTH muted, it isn't a music loop.");
                return;
            }
            case "haunt":   // (HAUNT) light a hot-zone right on top of you to test the loop
            {
                var g = Game.I; if (g == null || g.Player == null) { Print("no game."); return; }
                if (!g.IsAuthority) { Print("host only."); return; }
                g.SpawnHaunt(g.Player.GlobalPosition + new Vector3(20, 0, 0));   // just beside you so you can walk in
                Print("lit a HAUNT next to you. Walk in and kill to fill the break meter.");
                return;
            }
            case "phalanx": case "warded":   // (NEW) drop a Warded Phalanx on yourself — "phalanx 8" for a full 8-archer rank
            {
                var g = Game.I; if (g == null || g.Player == null) { Print("no game."); return; }
                if (!g.IsAuthority) { Print("host only."); return; }
                int n = 3; if (parts.Length >= 2) int.TryParse(parts[1], out n);
                g.SpawnPhalanxUnit(Mathf.Clamp(n, 1, Enemy.MaxArchers));
                Print($"spawned a WARDED PHALANX with {Mathf.Clamp(n, 1, Enemy.MaxArchers)} archers. Break the ward to expose them.");
                return;
            }
            case "freeze":
            {
                var g = Game.I; if (g == null) return;
                foreach (var e in g.Enemies.ToArray())
                    if (e != null && !e.Dead && !e.Remote && GodotObject.IsInstanceValid(e) && g.Player != null && e.GlobalPosition.DistanceTo(g.Player.GlobalPosition) < 40f)
                        e.AddFreeze(e.FreezeThreshold, g.Player != null ? g.Player.FreezeThreshMul : 1f, g.Player != null ? g.Player.FrostDurBonus : 0f);   // instantly freeze nearby foes to test the ice/shatter loop
                Print("froze nearby enemies.");
                return;
            }
            case "biome": case "jungle": case "rainforest": case "nextlevel": case "portal":
            {
                var g = Game.I; if (g == null) return;
                if (!g.IsAuthority) { Print("host only."); return; }
                g.AdvanceLevel();   // opens the next level immediately (level 2 = Magical Rainforest)
                Print($"advanced to level {g.LevelNum} ({g.CurBiome}).");
                return;
            }
            case "testperf":
            {
                var g = Game.I; if (g == null || g.Player == null) { Print("no game."); return; }
                if (!g.IsAuthority) { Print("host only."); return; }
                var pl = g.Player;
                pl.GodMode = true;
                if (pl.Ult == Player.UltKind.None) { pl.Ult = g.UltChoiceSet()[0]; pl.UltTier = 0; }
                pl.DevJumpLevel(30);
                if (g.CurBiome != Biome.Rainforest) g.AdvanceLevel();   // drop into the jungle
                g.DevForceWave(15);
                g.Heat = 1.6f;
                g.NetMgr?.BroadcastPerfOverlay(true);
                if (g.NetMgr == null || !g.NetMgr.Active) g.PerfOverlay = true;
                Print("TESTPERF: god mode, level 30, jungle, wave 15, max heat, perf overlay ON.");
                return;
            }
            case "loadmodel":
            {
                var g = Game.I; if (g == null || g.Player == null) { Print("no game."); return; }
                if (parts.Length < 2) { Print("usage: loadmodel <key> [height_m]   (default 2.6m = game character scale; tune against nearby enemies)"); return; }
                string key = parts[1];
                float tgt = 4.8f; if (parts.Length >= 3) float.TryParse(parts[2], out tgt);   // 4.8m = calibrated game witch scale (Lunar)
                if (!ModelAssets.Has(key)) { Print($"no asset at res://assets/models/{key}.glb — drop the imported .glb there first."); return; }
                var m = ModelAssets.TryLoad(key);
                if (m == null) { Print($"failed to instantiate {key}.glb."); return; }
                g.AddChild(m);
                ModelAssets.Painterlify(m);   // de-ghost: force opaque + matte
                float rawH = ModelAssets.FitHeight(m, tgt);
                var fwd = -g.Player.GlobalTransform.Basis.Z; fwd.Y = 0; fwd = fwd.Normalized();
                m.GlobalPosition = g.Player.GlobalPosition + fwd * 4f;
                string anim = ModelAssets.Animate(m, key);   // try to make her move
                Print($"spawned '{key}' at {tgt:0.##}m (native {rawH:0.###}m). ANIM: {anim}");
                return;
            }
            case "copy": case "copylog":
            {
                string sel = _log?.GetSelectedText() ?? "";
                string txt = sel.Length > 0 ? sel : (_log?.GetParsedText() ?? "");
                DisplayServer.ClipboardSet(txt);
                Print($"copied {txt.Length} chars to clipboard ({(sel.Length > 0 ? "selection" : "full log")}). Paste anywhere.");
                return;
            }
            case "skel": case "skeleton":
            {
                var g = Game.I; if (g == null || g.Player == null) { Print("no game."); return; }
                Print(g.Player.ToggleTpSkeleton());   // pulsing bone dots on the tp puppet (run 'tp' first); run 'skel' again to hide
                return;
            }
            case "anim": case "previewanim":
            {
                var g = Game.I; if (g == null || g.Player == null) { Print("no game."); return; }
                if (parts.Length < 2) { Print("usage: anim <animfile.glb>   (spawns witch_lunar in front playing that clip from assets/models/witches/ — audition anims before committing)"); return; }
                var m = ModelAssets.TryLoad("witch_lunar");
                if (m == null) { Print("witch_lunar.glb not found/imported."); return; }
                g.AddChild(m);
                ModelAssets.Painterlify(m);
                ModelAssets.FitHeight(m, 4.8f);
                var fwd = -g.Player.GlobalTransform.Basis.Z; fwd.Y = 0; fwd = fwd.Normalized();
                m.GlobalPosition = g.Player.GlobalPosition + fwd * 4f;
                Print(ModelAssets.PlayFrom(m, parts[1]));
                return;
            }
            case "tp": case "thirdperson": case "inspect":
            {
                var g = Game.I; if (g == null || g.Player == null) { Print("no game."); return; }
                bool on = g.Player.ToggleThirdPerson();
                if (on) { g.NoSpawn = true; g.ClearEnemies(); Print("THIRD-PERSON INSPECT ON — map cleared + spawns frozen. Turn (mouse/A-D) to orbit your witch. Run 'tp' again to exit."); }
                else { g.NoSpawn = false; Print("third-person OFF — spawns resumed."); }
                return;
            }
            case "castik": case "ik":
            {
                var g = Game.I; if (g == null || g.Player == null) { Print("no game."); return; }
                Print(g.Player.ToggleCastIK());
                return;
            }
            case "animview": case "animviewer": case "viewanims":
            {
                var g = Game.I; if (g == null || g.Player == null) { Print("no game."); return; }
                Print(g.Player.ToggleAnimViewer());
                if (g.Player.AnimViewer) { g.NoSpawn = true; g.ClearEnemies(); }   // freeze the world while browsing
                else g.NoSpawn = false;
                return;
            }
            case "tp3": case "play3": case "thirdplay":
            {
                var g = Game.I; if (g == null || g.Player == null) { Print("no game."); return; }
                float d = parts.Length > 1 && float.TryParse(parts[1], out var dd) ? dd : -1f;
                float ht = parts.Length > 2 && float.TryParse(parts[2], out var hh) ? hh : -1f;
                float lat = parts.Length > 3 && float.TryParse(parts[3], out var ll) ? ll : -999f;
                Print(g.Player.ToggleThirdPersonPlay(d, ht, lat));
                return;
            }
            case "fppose": case "handpose":
            {
                var g = Game.I; if (g == null || g.Player == null) { Print("no game."); return; }
                Print(g.Player.ToggleFpHandPose());
                return;
            }
            case "colliders": case "collision": case "col":
            {
                var g = Game.I; if (g == null || g.ColDebug == null) { Print("no game."); return; }
                g.ColDebug.Toggle();
                Print($"collision bounds {(g.ColDebug.On ? "ON (red=solid, blue=deck, green=ramp)" : "OFF")}");
                return;
            }
            case "nospawn": case "nomobs": case "peace":
            {
                var g = Game.I; if (g == null) { Print("no game."); return; }
                g.NoSpawn = !g.NoSpawn;
                if (g.NoSpawn) g.ClearEnemies();
                Print($"enemy spawns {(g.NoSpawn ? "OFF (mobs cleared)" : "ON")}");
                return;
            }
            case "cedit": case "collideredit": case "celab":
            {
                var g = Game.I; if (g == null || g.ColEditor == null) { Print("no game."); return; }
                if (g.ColEditor.Active) { g.ColEditor.Exit(); Print("collider editor: EXIT"); }
                else { Toggle(); g.ColEditor.Enter(); Print("collider editor: ENTER — WASD+mouse fly (Space/Ctrl up-down). G/R/T = move/rotate/scale mode, then arrows = X/Z & Q/E = Y. M=new, Tab=select, C=color, V=shape, X=delete, K=save, Esc=exit"); }
                return;
            }
            case "skindark": case "darkskin":
            {
                var g = Game.I; if (g == null || g.Player == null) { Print("no game."); return; }
                float factor = parts.Length > 1 && float.TryParse(parts[1], out var ff) ? Mathf.Clamp(ff, 0.1f, 1f) : 0.6f;
                string key = parts.Length > 2 ? parts[2] : WitchModel.KeyFor(g.Player.WitchIndex);
                if (key == null) { Print("unknown witch."); return; }
                Print(ModelAssets.BakeBodyDark(key, factor));
                return;
            }
            case "fp": case "firstperson":
            {
                var g = Game.I; if (g == null || g.Player == null) { Print("no game."); return; }
                float eye = parts.Length > 1 && float.TryParse(parts[1], out var h) ? h : -1f;
                float twist = parts.Length > 2 && float.TryParse(parts[2], out var t) ? t : -999f;
                float near = parts.Length > 3 && float.TryParse(parts[3], out var nr) ? nr : -1f;
                float fwd = parts.Length > 4 && float.TryParse(parts[4], out var fw) ? fw : -999f;
                Print(g.Player.ToggleFirstPersonAuthored(eye, twist, near, fwd));
                if (g.Player.FirstPersonAuthored) { g.NoSpawn = true; g.ClearEnemies(); }   // freeze the world while you tune the FP view
                else g.NoSpawn = false;
                return;
            }
            case "sky": case "skyritual": case "skyislands":
            {
                var g = Game.I; if (g == null || g.Player == null) { Print("no game."); return; }
                if (!g.IsAuthority) { Print("host only."); return; }
                if (g.InSky) { g.ExitSky(false); Print("exited the sky ritual."); return; }
                g.ShowSkyWhirl(g.Player.GlobalPosition);   // drop a whirlwind at your feet + ride it up now (skips the wave-5 gate)
                g.EnterSky();
                Print("entered the SKY ISLANDS ritual (dev). Light the 3 effigies, then reach the cauldron. Fall off / die to leave. Run 'sky' again to force-exit.");
                return;
            }
            case "singleplayerultwindow": case "soloultwindow":
            {
                if (Game.I?.UltOverlay == null) { Print("no overlay."); return; }
                bool on = !(parts.Length > 1 && (parts[1].ToLowerInvariant() is "false" or "0" or "off"));
                if (parts.Length <= 1) on = !Game.I.UltOverlay.SoloTest;   // bare form toggles
                Game.I.UltOverlay.EnableSolo(on);
                Print($"single-player ult windows {(on ? "ON" : "OFF")} — casting your OWN ult now pops a cutout of you (dev/testing).");
                return;
            }
            case "ultwindow": case "testultwindow":
            {
                // dev preview of the ally ult-cast cutout (normally it only appears when a REMOTE ally ults in co-op).
                // Each window is a self-contained staged cinematic, so a preview just needs a witch + an ult.
                if (Game.I?.UltOverlay == null) { Print("no overlay."); return; }
                int widx = 1;
                if (parts.Length >= 2) { int wi = Array.FindIndex(Witches, ww => ww.uid == parts[1].ToLowerInvariant()); if (wi >= 0) widx = wi; }
                var sample = new[] { Player.UltKind.Eclipse, Player.UltKind.FaithShield, Player.UltKind.BloodTsunami, Player.UltKind.GroveGuardian, Player.UltKind.Hurricane, Player.UltKind.Blizzard, Player.UltKind.HexCircle, Player.UltKind.MeteorDescent, Player.UltKind.ArcaneAscend };
                Game.I.UltOverlay.Preview(widx, sample[Mathf.Clamp(widx, 0, sample.Length - 1)]);
                Print($"ult-cast window preview: {Witches[widx].name}. (dev-only; the real cutout pops when an ALLY ults in co-op)");
                return;
            }
        }

        int dot = cmd.IndexOf('.');
        if (dot > 0)
        {
            string who = cmd.Substring(0, dot), sub = cmd.Substring(dot + 1);
            if (!who.StartsWith("player") || !int.TryParse(who.Substring(6), out int pn) || pn < 1)
            { Print("bad player id (use player1, player2, …). Type 'help'."); return; }
            var pl = ResolvePlayer(pn, out string err);
            if (pl == null) { Print(err); return; }
            ExecPlayer(pl, pn, sub, parts);
            return;
        }
        Print("unknown command: " + cmd + "  (type 'help')");
    }

    private void ListFoes()
    {
        Print("enemy types (uid: name):");
        for (int i = 0; i < EnemyKinds.Types.Length; i++) Print($"  {i}: {EnemyKinds.Types[i]}");
        Print("spawn with:  spawnfoe <uid> <count>");
    }

    private void SpawnFoe(string[] parts)
    {
        if (parts.Length < 2 || !int.TryParse(parts[1], out int uid) || uid < 0 || uid >= EnemyKinds.Types.Length)
        { Print("usage: spawnfoe <uid> <count>   (see 'listallzombies')"); return; }
        int count = 1;
        if (parts.Length >= 3) int.TryParse(parts[2], out count);
        count = Mathf.Clamp(count, 1, 50);
        var pl = Game.I?.Player;
        if (pl == null || Game.I == null) { Print("no local player."); return; }
        string type = EnemyKinds.Types[uid];

        // aim at the mouse cursor → world point (floor plane y=0). If the cursor isn't over ground, spawn in the air to drop.
        Vector3 basePos = pl.GlobalPosition; bool onGround = true;
        var cam = GetViewport()?.GetCamera3D();
        if (cam != null)
        {
            var mouse = GetViewport().GetMousePosition();
            var origin = cam.ProjectRayOrigin(mouse);
            var dir = cam.ProjectRayNormal(mouse);
            if (dir.Y < -0.001f)   // ray points down → hits the floor
            {
                float t = -origin.Y / dir.Y;
                if (t > 0f && t < 300f) { var hit = origin + dir * t; basePos = new Vector3(hit.X, 0f, hit.Z); onGround = true; }
                else { basePos = origin + dir * 30f; onGround = false; }
            }
            else { basePos = origin + dir * 30f; onGround = false; }   // aimed level/up → no floor: spawn in air
        }
        float spawnY = onGround ? 0f : 14f;   // not on ground → drop from the air

        for (int i = 0; i < count; i++)
        {
            float a = (float)GD.RandRange(0.0, Mathf.Tau), r = count > 1 ? (float)GD.RandRange(0.0, 3.0) : 0f;
            Game.I.SpawnEnemyAtExact(type, new Vector3(basePos.X + Mathf.Cos(a) * r, spawnY, basePos.Z + Mathf.Sin(a) * r));
        }
        Print($"spawned {count}x {type} at cursor{(onGround ? "" : " (dropping from air)")}.");
    }

    private Player ResolvePlayer(int n, out string err)
    {
        err = null;
        if (n == 1)
        {
            if (Game.I?.Player != null) return Game.I.Player;
            err = "no local player yet.";
            return null;
        }
        err = $"player{n} is a remote player — the console only mutates the local player (player1). Open the console on that machine to control it.";
        return null;
    }

    private void ExecPlayer(Player pl, int pn, string sub, string[] parts)
    {
        switch (sub)
        {
            case "free":
            {
                if (Game.I == null) { Print("no game."); return; }
                int killed = 0;
                foreach (var e in Game.I.Enemies.ToArray())
                    if (e != null && !e.Dead && GodotObject.IsInstanceValid(e) && e.GlobalPosition.DistanceTo(pl.GlobalPosition) < 30f) { e.Hurt(e.MaxHp * 3f, DamageType.Physical, true); killed++; }
                pl.GrabbedBy = 0;
                Print($"freed player{pn}: cleared {killed} nearby foes.");
                return;
            }
            case "addspellcombo":
            {
                if (parts.Length < 3) { Print("usage: playerX.addspellcombo <uid> <keybind>"); return; }
                if (!TryEnum<FinType>(parts[1], out var ft)) { Print("unknown spell combo uid: " + parts[1] + " (see listallspellcombos)"); return; }
                Key bind = ParseKey(parts[2]);
                if (bind == Key.None) { Print("bad keybind: " + parts[2] + " (try 1-5, or a letter)"); return; }
                var slot = new FinisherSlot { Type = ft, Every = 3, Pow = 1f, Rarity = Rarity.Legendary, Bind = bind };
                int at = pl.Fin.FindIndex(s => s.Bind == bind);
                if (at >= 0) { pl.Fin[at] = slot; Print($"replaced combo on [{KeyLabel(bind)}] with {FinMeta.Name(ft)}"); }
                else { pl.Fin.Add(slot); Print($"bound {FinMeta.Name(ft)} to [{KeyLabel(bind)}] (combos: {pl.Fin.Count}/{pl.S.FinSlots})"); }
                return;
            }
            case "removespellcombo":
            {
                if (parts.Length < 2 || !TryEnum<FinType>(parts[1], out var ft)) { Print("usage: playerX.removespellcombo <uid>"); return; }
                int idx = pl.Fin.FindIndex(s => s.Type == ft);
                if (idx >= 0) { pl.Fin.RemoveAt(idx); Print("removed combo " + FinMeta.Name(ft)); }
                else Print(FinMeta.Name(ft) + " isn't equipped.");
                return;
            }
            case "addmodifier":
            {
                if (parts.Length < 3 || !TryEnum<ModType>(parts[1], out var mt) || !int.TryParse(parts[2], out int slot) || slot < 1)
                { Print("usage: playerX.addmodifier <uid> <slot#>"); return; }
                if (slot - 1 < pl.Mods.Count) { pl.ReplaceModifier(slot - 1, mt, 5f, Rarity.Legendary); Print($"set modifier slot {slot} = {ModMeta.Name(mt)}"); }
                else { pl.EquipModifier(mt, 5f, Rarity.Legendary); Print($"added modifier {ModMeta.Name(mt)} (slot {pl.Mods.Count}/{pl.S.ModSlots})"); }
                return;
            }
            case "removespellmodifier":
            case "removemodifier":
            {
                if (parts.Length < 2 || !TryEnum<ModType>(parts[1], out var mt)) { Print("usage: playerX.removespellmodifier <uid>"); return; }
                int idx = pl.Mods.FindIndex(m => m.Type == mt);
                if (idx >= 0) { pl.Mods.RemoveAt(idx); Print("removed modifier " + ModMeta.Name(mt)); }
                else Print(ModMeta.Name(mt) + " isn't equipped.");
                return;
            }
            case "addspellcombominor":
            {
                if (parts.Length < 2 || !TryEnum<MinorType>(parts[1], out var mt)) { Print("usage: playerX.addspellcombominor <uid>"); return; }
                pl.AddMinor(mt); Print("added minor " + MinorMeta.Name(mt));
                return;
            }
            case "removespellcombominor":
            {
                if (parts.Length < 2 || !TryEnum<MinorType>(parts[1], out var mt)) { Print("usage: playerX.removespellcombominor <uid>"); return; }
                int idx = pl.Minors.FindIndex(m => m.Type == mt);
                if (idx >= 0) { pl.Minors.RemoveAt(idx); Print("removed minor " + MinorMeta.Name(mt)); }
                else Print(MinorMeta.Name(mt) + " isn't equipped.");
                return;
            }
            case "addultimate":
            {
                if (parts.Length < 2 || !TryEnum<Player.UltKind>(parts[1], out var uk)) { Print("usage: playerX.addultimate <uid> [level 0-4]"); return; }
                int lvl = 0;
                if (parts.Length >= 3) int.TryParse(parts[2], out lvl);
                lvl = Mathf.Clamp(lvl, 0, 4);
                pl.Ult = uk; pl.UltTier = lvl;
                Print($"set ultimate = {uk} (level {lvl})");
                return;
            }
            case "removeultimate":
                pl.Ult = Player.UltKind.None; pl.UltTier = 0; Print("ultimate cleared.");
                return;
            case "addmodifierslot":
            case "addspellmodifierslot":
                pl.S.ModSlots++; Print($"modifier slots now {pl.S.ModSlots}");
                return;
            case "addcomboslot":
            case "addspellcomboslot":
                pl.S.FinSlots++; Print($"spell-combo slots now {pl.S.FinSlots}");
                return;
            case "changewitch":
            {
                if (parts.Length < 2) { Print("usage: playerX.changewitch <uid>  (see listallwitches)"); return; }
                string wuid = parts[1].ToLowerInvariant();
                int widx = Array.FindIndex(Witches, w => w.uid == wuid);
                if (widx < 0) { Print("unknown witch uid: " + parts[1] + " (see listallwitches)"); return; }
                Game.I.ChangeWitch(widx);
                Print($"changed to {Witches[widx].name} — loadout reset (combos, modifiers, minors cleared; stats reset to base).");
                return;
            }
            case "addgold": case "gold":
            {
                if (parts.Length < 2 || !int.TryParse(parts[1], out int amt)) { Print("usage: playerX.addgold <amount>"); return; }
                if (Game.I == null) { Print("no game."); return; }
                Game.I.AddGold(amt);   // gold is run-global (local machine); floors at 1
                Print($"added {Mathf.Max(1, amt)} gold — total {Game.I.Gold}");
                return;
            }
            case "abup":   // (DEV/OVERHAUL) grant an ability-upgrade stack; auto-equips the ability at Common if unowned. playerX.abup <mod|fin> <Type> <path 0-4>
            {
                if (parts.Length < 4 || !int.TryParse(parts[3], out int path)) { Print("usage: abup <mod|fin> <Type> <path 0-4>  (0-2 = stat paths, 3-4 = evolutions)"); return; }
                string kind = parts[1].ToLowerInvariant();
                if (kind == "mod" && System.Enum.TryParse<ModType>(parts[2], true, out var mt))
                {
                    if (!pl.OwnsModifier(mt)) pl.EquipModifier(mt, 1f, Rarity.Common);
                    pl.UpgradeMod(mt, path); Print($"  {mt} path {path} → stack {pl.ModUpg(mt, path)}/{Player.UpgCap}");
                }
                else if (kind == "fin" && System.Enum.TryParse<FinType>(parts[2], true, out var ft))
                {
                    if (!pl.Fin.Exists(f => f.Type == ft)) pl.EquipFinisher(ft, 8, 1f, Rarity.Common);
                    pl.UpgradeFin(ft, path); Print($"  {ft} path {path} → stack {pl.FinUpg(ft, path)}/{Player.UpgCap}");
                }
                else Print("  bad kind/type — e.g. abup mod Meteor 0");
                return;
            }
            case "addlevels": case "addlevel":
            {
                if (parts.Length < 2 || !int.TryParse(parts[1], out int n) || n < 1) { Print("usage: playerX.addlevels <count>"); return; }
                if (Game.I == null) { Print("no game."); return; }
                n = Mathf.Clamp(n, 1, 100);
                // Feed exactly enough XP to gain n levels through the normal path (Player.AddXp → Game.OpenLevelUp),
                // so ult offers + one upgrade pick PER level still happen and it's shared to all players.
                // Mirrors the XP curve in Player.AddXp: XpNext = 28 + (Level-1)*22.
                float xp = pl.XpNext - pl.Xp;                                   // finish the current level
                for (int i = 1; i < n; i++) xp += 28f + (pl.Level + i - 1) * 22f;   // then each subsequent level's threshold
                Game.I.GrantSharedXp(xp);
                Print($"granted {n} level(s) — shared XP to all players. Close the console (~) to pick your {n} upgrade(s); ults still offer at their unlock levels.");
                return;
            }
            case "tgm":
            case "togglegodmode":
                pl.GodMode = !pl.GodMode;
                if (pl.GodMode && pl.Ult == Player.UltKind.None && Game.I != null)   // no ult equipped? grant the witch's default (first) ult so you can test ults
                { pl.Ult = Game.I.UltChoiceSet()[0]; pl.UltTier = 0; Print($"  granted default ult: {Hud.UltName(pl.Ult)}"); }
                Print($"god mode {(pl.GodMode ? "ON — invincible, infinite mana, ult always charged, infinite ult tokens ([U] to upgrade/swap free)" : "OFF")} for player{pn}");
                return;
            default:
                Print("unknown player command: " + sub + "  (type 'help')");
                return;
        }
    }

    // ---- listings (enum-driven, always current) ----
    private void ListPlayers()
    {
        Print("-- players --");
        Print("  player1  (you, local)");
        try
        {
            if (Multiplayer?.MultiplayerPeer != null)
            {
                int n = 2;
                foreach (var id in Multiplayer.GetPeers()) Print($"  player{n++}  (remote, peer {id})");
            }
        }
        catch { }
    }

    private void ListCombos()
    {
        Print("-- spell combos  (uid : name) --");
        foreach (FinType t in Enum.GetValues<FinType>()) Print($"  {t.ToString().ToLowerInvariant()} : {FinMeta.Name(t)}");
    }

    private void ListMinors()
    {
        Print("-- minor combos  (uid : name) --");
        foreach (MinorType t in Enum.GetValues<MinorType>()) Print($"  {t.ToString().ToLowerInvariant()} : {MinorMeta.Name(t)}");
    }

    private void ListMods()
    {
        Print("-- right-click modifiers  (uid : name) --");
        foreach (ModType t in Enum.GetValues<ModType>()) Print($"  {t.ToString().ToLowerInvariant()} : {ModMeta.Name(t)}");
    }

    private void ListUlts()
    {
        Print("-- ultimates  (uid) --");
        foreach (Player.UltKind t in Enum.GetValues<Player.UltKind>())
            if (t != Player.UltKind.None) Print("  " + t.ToString().ToLowerInvariant());
    }

    private void ListWitches()
    {
        Print("-- witches  (uid : name) --");
        foreach (var w in Witches) Print($"  {w.uid} : {w.name}");
    }

    private void Help()
    {
        Print("-- dev console --  (mutations target player1 = local)");
        Print("perf [on|off]                             (frame-time + network overlay, whole lobby)   [alias: fps, netstats]");
        Print("listplayers");
        Print("listallzombies                             (enemy types + uids)   [alias: listfoes]");
        Print("spawnfoe <uid> <count>                     (spawn foes near you)   [alias: spawnfoes]");
        Print("routes [on|off]                            (TOGGLE: catalogue all 27 hidden perk routes vs none)   [alias: unlockroutes, hiddenroutes]");
        Print("sky                                        (force-enter the jungle Sky-Islands ritual now; run again to exit)   [alias: skyritual]");
        Print("ultwindow [witch uid]                      (preview the ally ult-cast cutout window — co-op-only feature)");
        Print("singleplayerultwindow [true|false]         (toggle: your OWN ults pop a cutout in single player for testing)");
        Print("playerX.free                               (kill nearby foes / break a Taker grab)");
        Print("listallspellcombos | listallspellcombominors | listallspellmodifiers | listallultimates | listallwitches");
        Print("playerX.changewitch <uid>                 (swaps witch + wipes loadout)");
        Print("playerX.addspellcombo <uid> <keybind>      (replaces whatever's on that key)");
        Print("playerX.removespellcombo <uid>");
        Print("playerX.addmodifier <uid> <slot#>          (slot 1..modSlots)");
        Print("playerX.removespellmodifier <uid>");
        Print("playerX.addspellcombominor <uid>");
        Print("playerX.removespellcombominor <uid>");
        Print("playerX.addultimate <uid> [level 0-4]      (replaces current ultimate)");
        Print("playerX.removeultimate");
        Print("playerX.addlevels <count>                 (instantly level up N times — shared XP; prompts N upgrade picks + ult offers)");
        Print("playerX.addcomboslot | playerX.addmodifierslot   (increase capacity)");
        Print("playerX.tgm                                (toggle god mode: invincible + infinite mana + ult always charged + infinite ult tokens; grants a default ult if none)   [alias: togglegodmode]");
        Print("playerX.addgold <amount>                   (grants gold)   [alias: gold]");
    }
}
