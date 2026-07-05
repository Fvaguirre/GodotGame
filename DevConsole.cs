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

    // witch uid -> index (index MUST match Game.ConfigureWitch's cases). Update alongside a new witch.
    private static readonly (string uid, string name)[] Witches =
    {
        ("lunar",   "The Lunar Witch"),
        ("divine",  "The Divine Witch"),
        ("crimson", "The Crimson Blood Witch"),
        ("verdant", "The Verdant Witch"),
        ("gale",    "The Gale Witch"),
    };

    public override void _Ready()
    {
        Layer = 128;
        _panel = new PanelContainer();
        AddChild(_panel);
        _panel.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        _panel.OffsetBottom = 340;
        _panel.Visible = false;

        var vb = new VBoxContainer();
        _panel.AddChild(vb);

        _log = new RichTextLabel { ScrollFollowing = true, SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 300) };
        vb.AddChild(_log);

        _input = new LineEdit { PlaceholderText = "type a command — try 'help'" };
        vb.AddChild(_input);
        _input.TextSubmitted += OnSubmit;

        Print("Dev console ready — toggle with ~ . Type 'help'.");
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
        if (IsOpen) { _input.Clear(); _input.GrabFocus(); Input.MouseMode = Input.MouseModeEnum.Visible; }
        else _input.ReleaseFocus();
    }

    private void OnSubmit(string text)
    {
        Exec(text);
        _input.Clear();
        _input.GrabFocus();
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
            case "listplayers": ListPlayers(); return;
            case "listallspellcombos": ListCombos(); return;
            case "listallspellcombominors": ListMinors(); return;
            case "listallspellmodifiers": ListMods(); return;
            case "listallultimates": ListUlts(); return;
            case "listallwitches": ListWitches(); return;
            case "listallzombies": case "listfoes": case "listallfoes": ListFoes(); return;
            case "spawnfoe": case "spawnfoes": case "spawn": SpawnFoe(parts); return;
            case "freeze":
            {
                var g = Game.I; if (g == null) return;
                foreach (var e in g.Enemies.ToArray())
                    if (e != null && !e.Dead && !e.Remote && GodotObject.IsInstanceValid(e) && g.Player != null && e.GlobalPosition.DistanceTo(g.Player.GlobalPosition) < 40f)
                        e.AddFreeze(e.FreezeThreshold, g.Player != null ? g.Player.FreezeThreshMul : 1f, g.Player != null ? g.Player.FrostDurBonus : 0f);   // instantly freeze nearby foes to test the ice/shatter loop
                Print("froze nearby enemies.");
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
            case "togglegodmode":
                pl.GodMode = !pl.GodMode; Print($"god mode {(pl.GodMode ? "ON" : "OFF")} for player{pn}");
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
        Print("listplayers");
        Print("listallzombies                             (enemy types + uids)   [alias: listfoes]");
        Print("spawnfoe <uid> <count>                     (spawn foes near you)   [alias: spawnfoes]");
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
        Print("playerX.addcomboslot | playerX.addmodifierslot   (increase capacity)");
        Print("playerX.togglegodmode                      (infinite hp + mana)");
    }
}
