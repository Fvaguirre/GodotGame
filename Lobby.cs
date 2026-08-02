using Godot;

// Lobby.cs — the home screen. Three stacked panels toggled by visibility: MAIN (Play Solo / Multiplayer / Options / Quit),
// MULTIPLAYER (Host / Join by IP / Back), and OPTIONS (a TabContainer: Graphics / Sound / Screen). Laid out entirely with
// auto-sizing containers (CenterContainer) so it fits ANY window size — no hardcoded pixel positions. Solo/Host/Join hand
// off to Game.LobbySolo/Host/Join which drop into character select.
public partial class Lobby : Control
{
    private VBoxContainer _main, _mp, _opt;
    private LineEdit _ip;
    private ColorRect _bg;                 // full-screen background — opaque for the main menu, dimmed for the in-game options overlay
    private bool _inGameMode = false;      // true when shown as an in-game overlay (transparent bg, Back returns to the pause menu)
    private static readonly Color BgSolid = new Color(0.035f, 0.03f, 0.06f, 1f);
    private static readonly Color BgOverlay = new Color(0f, 0f, 0f, 0.55f);   // dim, not opaque — the paused game shows through

    private static readonly Color Ink = new Color(0.93f, 0.88f, 0.72f);
    private static readonly Color Accent = new Color(0.72f, 0.55f, 1.0f);      // witchy violet

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);   // (FIX) offsets too, so it fills the viewport WITHOUT needing a resize event (broke on scene reload)
        MouseFilter = MouseFilterEnum.Stop;

        _bg = new ColorRect { Color = BgSolid };
        _bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_bg);

        var center = new CenterContainer();
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(center);

        _main = Column(); center.AddChild(_main);
        _mp = Column(); center.AddChild(_mp);
        _opt = Column(); center.AddChild(_opt);

        BuildMain();
        BuildMultiplayer();
        BuildOptions();
        ShowPanel(0);
    }

    public override void _Process(double delta)
    {
        if (Visible)   // (FIX) re-assert full-viewport size — guards against layout-timing edges (scene reload had no resize to trigger it)
        {
            var vp = GetViewportRect().Size;
            if ((Size - vp).LengthSquared() > 1f) SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        }
    }

    private static VBoxContainer Column()
    {
        var v = new VBoxContainer { CustomMinimumSize = new Vector2(380, 0) };
        v.AddThemeConstantOverride("separation", 12);
        v.Alignment = BoxContainer.AlignmentMode.Center;
        return v;
    }

    // ---- styled building blocks ----------------------------------------
    private static StyleBoxFlat Box(Color bg, Color border, int bw = 2, int radius = 10)
    {
        var s = new StyleBoxFlat { BgColor = bg };
        s.BorderColor = border; s.SetBorderWidthAll(bw); s.SetCornerRadiusAll(radius);
        s.SetContentMarginAll(14);
        return s;
    }

    private Button MenuButton(string text, Color accent, System.Action onPressed, int fontSize = 22)
    {
        var b = new Button { Text = text, CustomMinimumSize = new Vector2(360, 52) };
        b.AddThemeFontSizeOverride("font_size", fontSize);
        b.AddThemeColorOverride("font_color", Ink);
        b.AddThemeColorOverride("font_hover_color", Colors.White);
        b.AddThemeStyleboxOverride("normal", Box(new Color(0.12f, 0.10f, 0.19f, 0.95f), new Color(accent.R, accent.G, accent.B, 0.55f)));
        b.AddThemeStyleboxOverride("hover", Box(new Color(0.20f, 0.15f, 0.30f, 0.98f), accent));
        b.AddThemeStyleboxOverride("pressed", Box(new Color(0.16f, 0.12f, 0.24f, 1f), accent));
        b.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        b.Pressed += onPressed;
        return b;
    }

    private static Label Header(string text, int size, Color col)
    {
        var l = new Label { Text = text, HorizontalAlignment = HorizontalAlignment.Center };
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", col);
        l.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        return l;
    }

    private static Control Spacer(int h) => new Control { CustomMinimumSize = new Vector2(0, h) };

    // ---- MAIN --------------------------------------------------------------
    private void BuildMain()
    {
        _main.AddChild(Header("WARDENS OF THE", 26, Ink));
        _main.AddChild(Header("MOONLIT GROVE", 34, Accent));
        _main.AddChild(Header("a co-op spellcaster roguelite", 13, new Color(0.7f, 0.68f, 0.8f)));
        _main.AddChild(Spacer(14));
        _main.AddChild(MenuButton("Play Solo", Accent, () => { Hide(); Game.I.LobbySolo(); }));
        _main.AddChild(MenuButton("Play Multiplayer", new Color(0.55f, 0.85f, 1f), () => ShowPanel(1)));
        _main.AddChild(MenuButton("Coven Perks", new Color(0.8f, 0.6f, 1f), () => { Hide(); Game.I.OpenPerks(); }));
        _main.AddChild(MenuButton("Options", new Color(0.6f, 0.9f, 0.6f), () => ShowPanel(2)));
        _main.AddChild(MenuButton("Quit", new Color(0.9f, 0.5f, 0.55f), () => GetTree().Quit(), 18));
    }

    // ---- MULTIPLAYER -------------------------------------------------------
    private void BuildMultiplayer()
    {
        _mp.AddChild(Header("MULTIPLAYER", 30, new Color(0.55f, 0.85f, 1f)));
        _mp.AddChild(Header("LAN co-op — one player hosts, the rest join", 13, new Color(0.7f, 0.7f, 0.82f)));
        _mp.AddChild(Spacer(10));
        _mp.AddChild(MenuButton("Host Game", new Color(0.55f, 0.85f, 1f), () => { Hide(); Game.I.LobbyHost(); }));
        _mp.AddChild(Header("— or join a host on your network —", 12, new Color(0.66f, 0.66f, 0.78f)));
        _ip = new LineEdit { PlaceholderText = "host IP  (e.g. 192.168.1.42)", CustomMinimumSize = new Vector2(360, 40) };
        _ip.AddThemeFontSizeOverride("font_size", 16);
        _mp.AddChild(_ip);
        _mp.AddChild(MenuButton("Join Game", new Color(0.6f, 0.9f, 0.6f), () => { Hide(); Game.I.LobbyJoin(_ip.Text); }, 20));
        _mp.AddChild(Spacer(8));
        _mp.AddChild(MenuButton("Back", new Color(0.7f, 0.7f, 0.8f), () => ShowPanel(0), 16));
    }

    // ---- OPTIONS -----------------------------------------------------------
    private void BuildOptions()
    {
        _opt.CustomMinimumSize = new Vector2(620, 0);
        _opt.AddChild(Header("OPTIONS", 30, new Color(0.6f, 0.9f, 0.6f)));

        var tabs = new TabContainer { CustomMinimumSize = new Vector2(600, 340) };
        tabs.AddThemeFontSizeOverride("font_size", 16);
        tabs.AddThemeStyleboxOverride("panel", Box(new Color(0.08f, 0.07f, 0.13f, 0.98f), new Color(0.3f, 0.28f, 0.4f, 0.7f)));
        tabs.AddChild(BuildGraphicsTab());
        tabs.AddChild(BuildSoundTab());
        tabs.AddChild(BuildScreenTab());
        _opt.AddChild(tabs);
        _opt.AddChild(MenuButton("Back", new Color(0.7f, 0.7f, 0.8f), () =>
        {
            Game.I?.SaveGold();
            if (_inGameMode) Game.I?.CloseInGameOptions();   // overlay: back to the pause menu
            else ShowPanel(0);                               // main menu: back to the main panel
        }, 16));
    }

    // ---- in-game options overlay -------------------------------------------
    // Show the EXACT same OPTIONS page over a paused run, with a transparent (dimmed) background so the game shows behind it.
    // Rebuilt each time so every control reflects the run's current settings.
    public void ShowOptionsOverlay()
    {
        _inGameMode = true;
        if (_bg != null) _bg.Color = BgOverlay;
        RebuildOptions();
        Show();
        ShowPanel(2);
    }

    public void HideOptionsOverlay()
    {
        _inGameMode = false;
        if (_bg != null) _bg.Color = BgSolid;   // restore the opaque menu background for next main-menu use
        Hide();
        ShowPanel(0);
    }

    private void RebuildOptions()
    {
        if (_opt == null) return;
        foreach (var c in _opt.GetChildren()) { _opt.RemoveChild(c); c.QueueFree(); }
        _bloom = _ssao = _ssil = null; _shadowOpt = null; _res = null;   // drop stale refs before the rebuild repopulates them
        BuildOptions();
    }

    private static VBoxContainer TabBody(string name)
    {
        var m = new MarginContainer { Name = name };
        m.AddThemeConstantOverride("margin_left", 24); m.AddThemeConstantOverride("margin_top", 20);
        m.AddThemeConstantOverride("margin_right", 24); m.AddThemeConstantOverride("margin_bottom", 20);
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 16);
        m.AddChild(v);
        return v;   // caller adds rows here; v.GetParent() is the tab (the named MarginContainer)
    }

    private static HBoxContainer Row(string label, Control control)
    {
        var h = new HBoxContainer();
        h.AddThemeConstantOverride("separation", 16);
        var l = new Label { Text = label, CustomMinimumSize = new Vector2(220, 0), VerticalAlignment = VerticalAlignment.Center };
        l.AddThemeFontSizeOverride("font_size", 16);
        l.AddThemeColorOverride("font_color", Ink);
        h.AddChild(l);
        control.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        h.AddChild(control);
        return h;
    }

    private static CheckBox Check(bool on, System.Action<bool> onToggle)
    {
        var c = new CheckBox { ButtonPressed = on };
        c.AddThemeFontSizeOverride("font_size", 15);
        c.Toggled += v => { onToggle(v); Game.I?.SaveGold(); };
        return c;
    }

    private Control BuildGraphicsTab()
    {
        var v = TabBody("Graphics");
        var g = Game.I;
        var quality = new OptionButton();
        quality.AddItem("Low"); quality.AddItem("Medium"); quality.AddItem("High"); quality.AddItem("Ultra");   // Ultra adds SSIL (fake GI) — the priciest effect
        quality.Selected = g != null ? g.GfxQuality : 2;
        quality.ItemSelected += idx => { Game.I?.SetGfxQuality((int)idx); Game.I?.SaveGold(); SyncGraphicsChecks(); };
        v.AddChild(Row("Quality Preset", quality));
        var shadows = new OptionButton();
        shadows.AddItem("Low"); shadows.AddItem("Medium"); shadows.AddItem("High");
        shadows.Selected = g != null ? g.ShadowQuality : 1;
        shadows.ItemSelected += idx => { Game.I?.SetShadowQuality((int)idx); Game.I?.SaveGold(); };
        _shadowOpt = shadows;
        v.AddChild(Row("Shadows", shadows));
        _bloom = Check(g == null || g.GfxBloom, on => { if (Game.I != null) { Game.I.GfxBloom = on; Game.I.ApplyGraphics(); } });
        v.AddChild(Row("Bloom / Glow", _bloom));
        _ssao = Check(g == null || g.GfxSsao, on => { if (Game.I != null) { Game.I.GfxSsao = on; Game.I.ApplyGraphics(); } });
        v.AddChild(Row("Ambient Occlusion", _ssao));
        _ssil = Check(g == null || g.GfxSsil, on => { if (Game.I != null) { Game.I.GfxSsil = on; Game.I.ApplyGraphics(); } });
        v.AddChild(Row("Indirect Light", _ssil));
        // (NEW) Texture Quality — caps the resolution the big ground/rock textures load at (High = full 2k, Medium = 1k, Low = 512).
        // Lower tiers downscale in-engine at load, saving VRAM on weaker GPUs (no extra files shipped).
        var texq = new OptionButton();
        texq.AddItem("Low"); texq.AddItem("Medium"); texq.AddItem("High");
        texq.Selected = g != null ? g.TextureQuality : 2;
        texq.ItemSelected += idx => { Game.I?.SetTextureQuality((int)idx); Game.I?.SaveGold(); };
        v.AddChild(Row("Texture Quality", texq));
        v.AddChild(Row("Damage Numbers", Check(g != null && g.DmgNumbers, on => { if (Game.I != null) Game.I.DmgNumbers = on; })));
        return v.GetParent<Control>();
    }
    private CheckBox _bloom, _ssao, _ssil;
    private OptionButton _shadowOpt;
    private void SyncGraphicsChecks()
    {
        var g = Game.I; if (g == null) return;
        if (_bloom != null) _bloom.SetPressedNoSignal(g.GfxBloom);
        if (_ssao != null) _ssao.SetPressedNoSignal(g.GfxSsao);
        if (_ssil != null) _ssil.SetPressedNoSignal(g.GfxSsil);
        if (_shadowOpt != null) _shadowOpt.Selected = g.ShadowQuality;   // preset also sets shadows; keep the dropdown in sync
    }

    private Control BuildSoundTab()
    {
        var v = TabBody("Sound");
        var g = Game.I;
        var music = new HSlider { MinValue = 0, MaxValue = 1, Step = 0.02, Value = g != null && g.Sfx != null ? g.Sfx.MusicVol : 0.8, CustomMinimumSize = new Vector2(0, 24) };
        music.ValueChanged += val => { Game.I?.SetMusicVol((float)val); Game.I?.SaveGold(); };
        v.AddChild(Row("Music Volume", music));
        var sens = new HSlider { MinValue = 0, MaxValue = 1, Step = 0.02, Value = g != null ? g.SensSlider : 0.4, CustomMinimumSize = new Vector2(0, 24) };
        sens.ValueChanged += val => { Game.I?.SetSensitivity((float)val); Game.I?.SaveGold(); };
        v.AddChild(Row("Look Sensitivity", sens));
        var padSens = new HSlider { MinValue = 0, MaxValue = 1, Step = 0.02, Value = g != null ? g.PadSensSlider : 0.4, CustomMinimumSize = new Vector2(0, 24) };
        padSens.ValueChanged += val => { Game.I?.SetPadSensitivity((float)val); Game.I?.SaveGold(); };
        v.AddChild(Row("Gamepad Look", padSens));
        return v.GetParent<Control>();
    }

    private Control BuildScreenTab()
    {
        var v = TabBody("Screen");
        var g = Game.I;
        var mode = new OptionButton();
        mode.AddItem("Windowed"); mode.AddItem("Fullscreen");
        mode.Selected = g != null ? g.WindowMode : 0;
        mode.ItemSelected += idx => { if (Game.I != null) { Game.I.WindowMode = (int)idx; Game.I.ApplyWindow(); Game.I.SaveGold(); if (_res != null) _res.Disabled = idx == 1; } };
        v.AddChild(Row("Window Mode", mode));
        _res = new OptionButton();
        // ApplyWindow clamps any pick to the usable rect (taskbar excluded), so an entry bigger than this monitor would
        // claim one size and quietly hand back another. Label those instead of lying about them.
        var usable = DisplayServer.ScreenGetUsableRect(DisplayServer.WindowGetCurrentScreen()).Size;
        for (int i = 0; i < Game.ResChoices.Length; i++)
        {
            var r = Game.ResChoices[i];
            bool fits = r.X <= usable.X && r.Y <= usable.Y;
            _res.AddItem(fits ? $"{r.X} × {r.Y}" : $"{r.X} × {r.Y}  (larger than this screen)");
            if (!fits) _res.SetItemDisabled(i, true);
        }
        _res.Selected = g != null ? g.ResIndex : 2;
        _res.Disabled = g != null && g.WindowMode == 1;
        _res.ItemSelected += idx => { if (Game.I != null) { Game.I.ResIndex = (int)idx; Game.I.ApplyWindow(); Game.I.SaveGold(); } };
        v.AddChild(Row("Resolution", _res));
        v.AddChild(Row("V-Sync", Check(g == null || g.VSync, on => { if (Game.I != null) { Game.I.VSync = on; Game.I.ApplyWindow(); } })));
        var fps = new OptionButton();
        int fpsSel = 1;
        for (int i = 0; i < Game.FpsChoices.Length; i++) { fps.AddItem($"{Game.FpsChoices[i]} FPS"); if (g != null && g.MaxFps == Game.FpsChoices[i]) fpsSel = i; }
        fps.Selected = fpsSel;
        fps.ItemSelected += idx => { Game.I?.SetMaxFps(Game.FpsChoices[(int)idx]); Game.I?.SaveGold(); };
        v.AddChild(Row("Max FPS", fps));
        // (PERF) 3D Render Scale — the biggest lever at high resolutions; renders the world lower and upscales, UI stays sharp.
        var rscale = new OptionButton();
        int rsSel = Game.RenderScaleChoices.Length - 1;
        for (int i = 0; i < Game.RenderScaleChoices.Length; i++) { rscale.AddItem($"{Mathf.RoundToInt(Game.RenderScaleChoices[i] * 100)}%"); if (g != null && Mathf.Abs(g.RenderScale - Game.RenderScaleChoices[i]) < 0.01f) rsSel = i; }
        rscale.Selected = rsSel;
        rscale.ItemSelected += idx => { Game.I?.SetRenderScale(Game.RenderScaleChoices[(int)idx]); Game.I?.SaveGold(); };
        v.AddChild(Row("Render Scale (3D)", rscale));
        // (UPSCALER) how the reduced 3D image is scaled up — FSR 2.2 is the DLSS-equivalent (temporal, near-native) Godot ships.
        var up = new OptionButton();
        up.AddItem("Bilinear"); up.AddItem("FSR 1.0"); up.AddItem("FSR 2.2");
        up.Selected = g != null ? g.UpscaleMode : 0;
        up.ItemSelected += idx => { Game.I?.SetUpscaleMode((int)idx); Game.I?.SaveGold(); };
        v.AddChild(Row("Upscaler", up));
        var view = new OptionButton();
        view.AddItem("Low"); view.AddItem("Medium"); view.AddItem("High");
        view.Selected = g != null ? g.ViewDist : 1;
        view.ItemSelected += idx => { Game.I?.SetViewDist((int)idx); Game.I?.SaveGold(); };
        v.AddChild(Row("Render Distance", view));
        return v.GetParent<Control>();
    }
    private OptionButton _res;

    // reset to the MAIN panel — used when returning here from character-select
    public void ShowMain() => ShowPanel(0);

    // ---- panel switching ---------------------------------------------------
    private void ShowPanel(int which)
    {
        if (_main != null) _main.Visible = which == 0;
        if (_mp != null) _mp.Visible = which == 1;
        if (_opt != null) _opt.Visible = which == 2;
    }
}
