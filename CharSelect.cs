using Godot;

// CharSelect.cs — the character-select screen. A scrollable witch roster on the left; a rich detail card on the right
// (element badge, role, flavor, passives, and Power/Resilience/Mobility bars). Laid out with auto-sizing containers so it
// fits any window size. Confirm locks your witch; in multiplayer it then waits for every other warden before the run begins.
public partial class CharSelect : Control
{
    private struct W
    {
        public string Name; public DamageType Elem; public string Role, Desc;
        public string[] Passives;
        public int Dmg, Move, Resil, Sustain;      // the four honest axes (1-5)
        public int Hp; public float Speed, Resist, DmgMul;   // real numbers, shown as chips
    }

    // roster order matches ConfigureWitch indices 0..8; numbers match the per-witch stat spread
    private static readonly W[] Witches =
    {
        new W { Name = "The Lunar Witch", Elem = DamageType.Lunar, Role = "Moon-tank Caster",
                Desc = "The moon's chosen — a near-unkillable moonlit duelist. She brands the field with lunar power day and night, out-lasting every foe behind the coven's toughest hide.",
                Passives = new[]{ "Moonlight — innate +Lunar damage, day AND night (doubled at night)", "Moon-tank — the coven's highest resistance & second-highest HP", "Crescent blades & a night-charged ultimate" },
                Dmg = 3, Move = 2, Resil = 5, Sustain = 2, Hp = 125, Speed = 8.4f, Resist = 0.24f, DmgMul = 1.00f },
        new W { Name = "The Divine Witch", Elem = DamageType.Holy, Role = "Support Battlemage",
                Desc = "A radiant healer-warrior. Her sweeping holy ray blesses allies and Radiantly Smites the nearest foes; when a warden falls, she drags them back from death.",
                Passives = new[]{ "Divine Intervention — cheat death, again and again", "Radiant Smite — a full charge chains a pillar to two foes & mends you", "Blesses & heals nearby allies" },
                Dmg = 2, Move = 2, Resil = 4, Sustain = 5, Hp = 120, Speed = 9.0f, Resist = 0.13f, DmgMul = 0.90f },
        new W { Name = "The Crimson Witch", Elem = DamageType.Blood, Role = "Glass Cannon",
                Desc = "A ravenous glass cannon — the coven's highest raw damage, paid for in armor. She lives by draining the blood of the slain, and hits harder the closer she is to death.",
                Passives = new[]{ "Sanguine Thirst — a real lifesteal aura, plus heals on every kill", "Highest raw damage of the coven", "Perilously fragile — the lowest armor of the nine" },
                Dmg = 5, Move = 4, Resil = 1, Sustain = 3, Hp = 95, Speed = 9.6f, Resist = 0.08f, DmgMul = 1.18f },
        new W { Name = "The Verdant Witch", Elem = DamageType.Nature, Role = "Durable Summoner",
                Desc = "Warden of the Grove and the coven's stoutest wall. Her forest fights for her — tree-ents trickle up on their own and creeping poison melts the horde.",
                Passives = new[]{ "Living Grove — auto-summons tree-ents (a slow trickle even without combo)", "Walking fortress — the coven's highest HP", "Creeping poison damage-over-time" },
                Dmg = 2, Move = 2, Resil = 5, Sustain = 3, Hp = 135, Speed = 8.3f, Resist = 0.20f, DmgMul = 0.90f },
        new W { Name = "The Gale Witch", Elem = DamageType.Wind, Role = "Mobility Skirmisher",
                Desc = "A tempest on foot — swiftest of the coven, with an extra jump and dash. She hurls foes aside with gusts and grows deadlier the moment her feet leave the ground.",
                Passives = new[]{ "Tailwind — fastest, +1 dash, +1 jump, a guard window after each dash", "Airborne bonus damage", "Knockback gusts & cyclones" },
                Dmg = 3, Move = 5, Resil = 2, Sustain = 2, Hp = 95, Speed = 10.1f, Resist = 0.12f, DmgMul = 0.98f },
        new W { Name = "The Frost Witch", Elem = DamageType.Frost, Role = "Immovable Sniper",
                Desc = "A patient siege-sniper. Her beam locks foes in ice, a charged spear shatters them — and anything that reaches her leaves chilled to the bone.",
                Passives = new[]{ "Frost Armor — attackers who close on her are chilled & slowed", "Freeze → Shatter — a charged spear detonates the frozen", "Slowest of the coven, but hard to reach" },
                Dmg = 4, Move = 1, Resil = 3, Sustain = 1, Hp = 105, Speed = 8.0f, Resist = 0.14f, DmgMul = 0.95f },
        new W { Name = "The Forsaken Witch", Elem = DamageType.Curse, Role = "Wraith Controller",
                Desc = "A cursed puppeteer. Her lock-on beam tethers foes into groups that share every wound — and cursed foes bleed their life straight back into her.",
                Passives = new[]{ "Soul Siphon — cursed foes passively bleed life back to her", "Curse tethers — bound foes share all damage", "A creeping wraith — control over raw power" },
                Dmg = 3, Move = 2, Resil = 3, Sustain = 4, Hp = 105, Speed = 8.8f, Resist = 0.15f, DmgMul = 0.88f },
        new W { Name = "The Ember Witch", Elem = DamageType.Ember, Role = "Mobile Arsonist",
                Desc = "A gleeful arsonist wreathed in her own fire. Her flames stack into Living Bombs that erupt and chain through the horde — and the blaze mends her as it burns.",
                Passives = new[]{ "Cinder Skin — retaliatory heat singes attackers; her burns mend her", "Living Bomb — foes detonate at max burn & on death, chaining fire", "Innate burn power — no longer reliant on cards" },
                Dmg = 4, Move = 3, Resil = 2, Sustain = 2, Hp = 100, Speed = 9.2f, Resist = 0.12f, DmgMul = 0.92f },
        new W { Name = "The Arcane Witch", Elem = DamageType.Arcane, Role = "Precision Battlemage",
                Desc = "She channels raw arcane — a homing-missile marksman who brands foes into Conduits, then chains plasma through them all at once, healing off every crit.",
                Passives = new[]{ "Arcane Feedback — crits heal her; her missiles shoot down incoming bolts", "Conduits & Chain Lightning — marks that a charged bolt arcs through", "Crits on Conduits land twice as often" },
                Dmg = 3, Move = 3, Resil = 3, Sustain = 4, Hp = 105, Speed = 9.0f, Resist = 0.14f, DmgMul = 0.95f },
    };

    private static readonly Color Ink = new Color(0.93f, 0.88f, 0.72f);
    private static readonly Color Dim = new Color(0.68f, 0.66f, 0.78f);

    private int _sel = 0;
    private bool _confirmed = false;
    private VBoxContainer _detail;
    private Button[] _rows;
    private Button _confirm, _back;
    private Label _waiting;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        var bg = new ColorRect { Color = new Color(0.03f, 0.028f, 0.055f, 1f) };
        bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); AddChild(bg);

        var outer = new MarginContainer();
        outer.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        outer.AddThemeConstantOverride("margin_left", 48); outer.AddThemeConstantOverride("margin_right", 48);
        outer.AddThemeConstantOverride("margin_top", 30); outer.AddThemeConstantOverride("margin_bottom", 26);
        AddChild(outer);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 16);
        outer.AddChild(root);

        var title = new Label { Text = "CHOOSE YOUR WARDEN", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 30);
        title.AddThemeColorOverride("font_color", Ink);
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        root.AddChild(title);

        // middle: roster | detail
        var mid = new HBoxContainer();
        mid.AddThemeConstantOverride("separation", 22);
        mid.SizeFlagsVertical = SizeFlags.ExpandFill;
        root.AddChild(mid);

        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(340, 0) };
        scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        scroll.SizeFlagsVertical = SizeFlags.ExpandFill;
        mid.AddChild(scroll);
        var list = new VBoxContainer { CustomMinimumSize = new Vector2(320, 0) };
        list.AddThemeConstantOverride("separation", 8);
        list.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(list);
        _rows = new Button[Witches.Length];
        for (int i = 0; i < Witches.Length; i++)
        {
            int idx = i;
            var b = new Button { Text = "   " + Witches[i].Name, Alignment = HorizontalAlignment.Left, CustomMinimumSize = new Vector2(0, 56) };
            b.AddThemeFontSizeOverride("font_size", 20);
            b.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            b.Pressed += () => Select(idx);
            list.AddChild(b);
            _rows[i] = b;
        }

        var detailWrap = new PanelContainer();
        detailWrap.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        detailWrap.SizeFlagsVertical = SizeFlags.ExpandFill;
        detailWrap.AddThemeStyleboxOverride("panel", Box(new Color(0.075f, 0.065f, 0.12f, 0.98f), new Color(0.3f, 0.28f, 0.42f, 0.8f)));
        mid.AddChild(detailWrap);
        var dmargin = new MarginContainer();
        dmargin.AddThemeConstantOverride("margin_left", 28); dmargin.AddThemeConstantOverride("margin_top", 22);
        dmargin.AddThemeConstantOverride("margin_right", 28); dmargin.AddThemeConstantOverride("margin_bottom", 22);
        detailWrap.AddChild(dmargin);
        _detail = new VBoxContainer();
        _detail.AddThemeConstantOverride("separation", 13);
        dmargin.AddChild(_detail);

        // bottom: back + confirm + waiting
        var confirmCenter = new CenterContainer();
        confirmCenter.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        root.AddChild(confirmCenter);
        var btnRow = new HBoxContainer();
        btnRow.AddThemeConstantOverride("separation", 14);
        confirmCenter.AddChild(btnRow);
        _back = new Button { Text = "Back", CustomMinimumSize = new Vector2(150, 52) };
        _back.AddThemeFontSizeOverride("font_size", 20);
        _back.AddThemeColorOverride("font_color", Dim);
        _back.AddThemeStyleboxOverride("normal", Box(new Color(0.11f, 0.10f, 0.17f, 0.95f), new Color(0.5f, 0.5f, 0.6f, 0.6f), 2, 12));
        _back.AddThemeStyleboxOverride("hover", Box(new Color(0.18f, 0.16f, 0.24f, 1f), new Color(0.7f, 0.7f, 0.8f, 0.9f), 2, 12));
        _back.AddThemeStyleboxOverride("pressed", Box(new Color(0.16f, 0.14f, 0.22f, 1f), new Color(0.7f, 0.7f, 0.8f, 1f), 2, 12));
        _back.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        _back.Pressed += OnBack;
        btnRow.AddChild(_back);
        _confirm = new Button { Text = "Confirm", CustomMinimumSize = new Vector2(320, 52) };
        _confirm.AddThemeFontSizeOverride("font_size", 22);
        _confirm.Pressed += OnConfirm;
        btnRow.AddChild(_confirm);

        _waiting = new Label { Text = "", HorizontalAlignment = HorizontalAlignment.Center };
        _waiting.AddThemeFontSizeOverride("font_size", 16);
        _waiting.AddThemeColorOverride("font_color", new Color(0.8f, 0.9f, 1f));
        _waiting.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        root.AddChild(_waiting);

        Select(0);
    }

    private static StyleBoxFlat Box(Color bg, Color border, int bw = 2, int radius = 10)
    {
        var s = new StyleBoxFlat { BgColor = bg };
        s.BorderColor = border; s.SetBorderWidthAll(bw); s.SetCornerRadiusAll(radius); s.SetContentMarginAll(6);
        return s;
    }

    // reset when (re)entering char-select — clears the "waiting" lock so a returning group can re-pick
    public void Refresh()
    {
        _confirmed = false;
        if (_confirm != null) { _confirm.Disabled = false; _confirm.Text = "Confirm"; }
        if (_waiting != null) _waiting.Text = "";
        Select(_sel);
    }

    private void Select(int i)
    {
        if (_confirmed) return;
        _sel = i;
        var w = Witches[i];
        var col = DamageTypes.Col(w.Elem);
        for (int r = 0; r < _rows.Length; r++)
        {
            var rc = DamageTypes.Col(Witches[r].Elem);
            bool on = r == i;
            _rows[r].AddThemeStyleboxOverride("normal", Box(on ? new Color(rc.R * 0.28f, rc.G * 0.28f, rc.B * 0.28f, 0.95f) : new Color(0.11f, 0.10f, 0.17f, 0.9f), on ? rc : new Color(rc.R, rc.G, rc.B, 0.35f)));
            _rows[r].AddThemeStyleboxOverride("hover", Box(new Color(rc.R * 0.30f, rc.G * 0.30f, rc.B * 0.30f, 0.98f), rc));
            _rows[r].AddThemeStyleboxOverride("pressed", Box(new Color(rc.R * 0.30f, rc.G * 0.30f, rc.B * 0.30f, 1f), rc));
            _rows[r].AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
            _rows[r].AddThemeColorOverride("font_color", on ? Colors.White : Ink);
        }
        BuildDetail(w, col);
        if (_confirm != null)
        {
            _confirm.AddThemeColorOverride("font_color", Colors.White);
            _confirm.AddThemeStyleboxOverride("normal", Box(new Color(col.R * 0.30f, col.G * 0.30f, col.B * 0.30f, 0.95f), col, 2, 12));
            _confirm.AddThemeStyleboxOverride("hover", Box(new Color(col.R * 0.42f, col.G * 0.42f, col.B * 0.42f, 1f), col, 2, 12));
            _confirm.AddThemeStyleboxOverride("pressed", Box(new Color(col.R * 0.36f, col.G * 0.36f, col.B * 0.36f, 1f), col, 2, 12));
            _confirm.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        }
    }

    private void BuildDetail(W w, Color col)
    {
        foreach (var c in _detail.GetChildren()) c.QueueFree();

        var name = new Label { Text = w.Name };
        name.AddThemeFontSizeOverride("font_size", 30);
        name.AddThemeColorOverride("font_color", col);
        _detail.AddChild(name);

        var tags = new HBoxContainer();
        tags.AddThemeConstantOverride("separation", 10);
        var badge = new Label { Text = "  " + DamageTypes.Name(w.Elem).ToUpper() + "  ", VerticalAlignment = VerticalAlignment.Center };
        badge.AddThemeFontSizeOverride("font_size", 14);
        badge.AddThemeColorOverride("font_color", new Color(0.05f, 0.04f, 0.08f));
        badge.AddThemeStyleboxOverride("normal", Box(col, col, 0, 12));
        tags.AddChild(badge);
        var role = new Label { Text = "  " + w.Role.ToUpper() + "  ", VerticalAlignment = VerticalAlignment.Center };
        role.AddThemeFontSizeOverride("font_size", 14);
        role.AddThemeColorOverride("font_color", Dim);
        role.AddThemeStyleboxOverride("normal", Box(new Color(0.14f, 0.13f, 0.2f, 1f), new Color(col.R, col.G, col.B, 0.5f), 1, 12));
        tags.AddChild(role);
        _detail.AddChild(tags);

        var desc = new Label { Text = w.Desc, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        desc.AddThemeFontSizeOverride("font_size", 15);
        desc.AddThemeColorOverride("font_color", Ink);
        desc.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _detail.AddChild(desc);

        var pTitle = new Label { Text = "PASSIVES" };
        pTitle.AddThemeFontSizeOverride("font_size", 13);
        pTitle.AddThemeColorOverride("font_color", new Color(col.R, col.G, col.B, 0.9f));
        _detail.AddChild(pTitle);
        foreach (var p in w.Passives)
        {
            var prow = new Label { Text = "◆  " + p, AutowrapMode = TextServer.AutowrapMode.WordSmart };
            prow.AddThemeFontSizeOverride("font_size", 14);
            prow.AddThemeColorOverride("font_color", Dim);
            prow.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            _detail.AddChild(prow);
        }

        // real numbers, as chips
        var stats = new HBoxContainer(); stats.AddThemeConstantOverride("separation", 8);
        stats.AddChild(StatChip($"{w.Hp} HP", col));
        stats.AddChild(StatChip($"{w.Speed:0.0} spd", col));
        stats.AddChild(StatChip($"{Mathf.RoundToInt(w.Resist * 100)}% resist", col));
        stats.AddChild(StatChip($"×{w.DmgMul:0.00} dmg", col));
        _detail.AddChild(stats);

        _detail.AddChild(StatBar("DAMAGE", w.Dmg, col));
        _detail.AddChild(StatBar("MOVEMENT", w.Move, col));
        _detail.AddChild(StatBar("RESILIENCE", w.Resil, col));
        _detail.AddChild(StatBar("SUSTAIN", w.Sustain, col));
    }

    private Label StatChip(string text, Color col)
    {
        var l = new Label { Text = "  " + text + "  ", VerticalAlignment = VerticalAlignment.Center };
        l.AddThemeFontSizeOverride("font_size", 13);
        l.AddThemeColorOverride("font_color", Ink);
        l.AddThemeStyleboxOverride("normal", Box(new Color(0.13f, 0.12f, 0.19f, 1f), new Color(col.R, col.G, col.B, 0.55f), 1, 8));
        return l;
    }

    private HBoxContainer StatBar(string label, int filled, Color col)
    {
        var h = new HBoxContainer();
        h.AddThemeConstantOverride("separation", 8);
        var l = new Label { Text = label, VerticalAlignment = VerticalAlignment.Center, CustomMinimumSize = new Vector2(120, 0) };
        l.AddThemeFontSizeOverride("font_size", 13);
        l.AddThemeColorOverride("font_color", Dim);
        h.AddChild(l);
        for (int s = 0; s < 5; s++)
        {
            var seg = new ColorRect { CustomMinimumSize = new Vector2(46, 14), Color = s < filled ? col : new Color(0.2f, 0.19f, 0.26f, 1f) };
            h.AddChild(seg);
        }
        return h;
    }

    private void OnConfirm()
    {
        if (_confirmed) return;
        _confirmed = true;
        _confirm.Disabled = true;
        _confirm.Text = "Locked In";
        Game.I.ConfirmWitch(_sel);
    }

    // leave char-select → main menu (also cancels a pending host/join). Works even after "Locked In" in co-op.
    private void OnBack() => Game.I?.BackToLobbyFromSelect();

    public override void _Input(InputEvent e)
    {
        if (!Visible) return;
        if (e is InputEventKey k && k.Pressed && !k.Echo && k.Keycode == Key.Escape)
        {
            OnBack();
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _Process(double delta)
    {
        if (!Visible || !_confirmed || _waiting == null) return;
        var net = Game.I?.NetMgr;
        if (net != null && net.Active)
            _waiting.Text = $"waiting for wardens…  {Game.I.ReadyCount} / {net.PlayerCount()} ready";
    }
}
