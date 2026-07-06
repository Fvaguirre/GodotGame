using Godot;

// CharSelect.cs — the character-select screen. A scrollable witch roster on the left; a rich detail card on the right
// (element badge, role, flavor, passives, and Power/Resilience/Mobility bars). Laid out with auto-sizing containers so it
// fits any window size. Confirm locks your witch; in multiplayer it then waits for every other warden before the run begins.
public partial class CharSelect : Control
{
    private struct W
    {
        public string Name; public DamageType Elem; public string Role, Desc; public string[] Passives; public int Power, Resil, Mobi;
    }

    // roster order matches ConfigureWitch indices 0..6
    private static readonly W[] Witches =
    {
        new W { Name = "The Lunar Witch", Elem = DamageType.Lunar, Role = "Versatile",
                Desc = "The moon's chosen. Balanced lunar bolts and orbiting crescent blades — and she waxes ever stronger beneath the night sky.",
                Passives = new[]{ "Nightfall — grows stronger at night", "Orbiting crescent blades", "Well-rounded offense and defense" },
                Power = 3, Resil = 4, Mobi = 3 },
        new W { Name = "The Divine Witch", Elem = DamageType.Holy, Role = "Support",
                Desc = "A radiant healer-warrior. She sears foes with a sweeping ray of holy light and drags her allies back from the brink of death.",
                Passives = new[]{ "Divine Intervention — cheat death once", "Sweeping holy ray", "Blesses and mends nearby allies" },
                Power = 2, Resil = 3, Mobi = 3 },
        new W { Name = "The Crimson Witch", Elem = DamageType.Blood, Role = "Glass Cannon",
                Desc = "A ravenous glass cannon. She trades all her armor for raw, savage power, sustained only by draining the blood of the slain.",
                Passives = new[]{ "Bloodthirst — lifesteal aura", "Highest raw damage of the coven", "Perilously fragile — minimal armor" },
                Power = 5, Resil = 1, Mobi = 3 },
        new W { Name = "The Verdant Witch", Elem = DamageType.Nature, Role = "Summoner",
                Desc = "Warden of the Grove. Her true power is her forest — towering tree-ents and creeping poison do the fighting for her.",
                Passives = new[]{ "The Grove — summons tree-ents", "Creeping poison damage-over-time", "Durable, but low personal damage" },
                Power = 2, Resil = 4, Mobi = 3 },
        new W { Name = "The Gale Witch", Elem = DamageType.Wind, Role = "Skirmisher",
                Desc = "A tempest on foot. Swiftest of the coven, she hurls foes aside with gusts and spinning cyclones — and grows deadlier the moment her feet leave the ground.",
                Passives = new[]{ "Tailwind — faster, with an extra dash", "Jetstream — bonus damage while airborne", "Knockback gusts and cyclones" },
                Power = 3, Resil = 2, Mobi = 5 },
        new W { Name = "The Frost Witch", Elem = DamageType.Frost, Role = "Sniper",
                Desc = "A patient long-range sniper. Her beam locks foes in solid ice; a charged icicle spear then shatters them for a devastating burst.",
                Passives = new[]{ "Freeze — the beam encases foes in ice", "Shatter — a charged spear detonates them", "Fragile and slow — keep your distance" },
                Power = 3, Resil = 2, Mobi = 2 },
        new W { Name = "The Forsaken Witch", Elem = DamageType.Curse, Role = "Controller",
                Desc = "A cursed puppeteer. Her lock-on beam tethers foes into groups that share every wound — melt one and you melt the whole pack.",
                Passives = new[]{ "Curse tethers — foes share damage", "Voodoo Crush — detonate stacked curses", "Low direct damage, immense control" },
                Power = 2, Resil = 3, Mobi = 3 },
    };

    private static readonly Color Ink = new Color(0.93f, 0.88f, 0.72f);
    private static readonly Color Dim = new Color(0.68f, 0.66f, 0.78f);

    private int _sel = 0;
    private bool _confirmed = false;
    private VBoxContainer _detail;
    private Button[] _rows;
    private Button _confirm;
    private Label _waiting;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        var bg = new ColorRect { Color = new Color(0.03f, 0.028f, 0.055f, 1f) };
        bg.SetAnchorsPreset(LayoutPreset.FullRect); AddChild(bg);

        var outer = new MarginContainer();
        outer.SetAnchorsPreset(LayoutPreset.FullRect);
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

        // bottom: confirm + waiting
        var confirmCenter = new CenterContainer();
        confirmCenter.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        root.AddChild(confirmCenter);
        _confirm = new Button { Text = "Confirm", CustomMinimumSize = new Vector2(320, 52) };
        _confirm.AddThemeFontSizeOverride("font_size", 22);
        _confirm.Pressed += OnConfirm;
        confirmCenter.AddChild(_confirm);

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

        _detail.AddChild(StatBar("POWER", w.Power, col));
        _detail.AddChild(StatBar("RESILIENCE", w.Resil, col));
        _detail.AddChild(StatBar("MOBILITY", w.Mobi, col));
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

    public override void _Process(double delta)
    {
        if (!Visible || !_confirmed || _waiting == null) return;
        var net = Game.I?.NetMgr;
        if (net != null && net.Active)
            _waiting.Text = $"waiting for wardens…  {Game.I.ReadyCount} / {net.PlayerCount()} ready";
    }
}
