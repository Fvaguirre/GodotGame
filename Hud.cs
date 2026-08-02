using Godot;
using System.Collections.Generic;

// Hud.cs — ALL on-screen drawing (immediate-mode via _Draw). Branches on Game.State to render the
// right screen: in-run HUD (health/mana/combo, crosshair, enemy bars, witch-specific widgets like
// blood stacks and the Verdant Grove meter), plus the menus (DrawCharSelect, level-up cards, ult
// menu, banners, intermission). Clickable regions are exposed as Rect2[] (e.g. RWitch, RUlt) that
// Game hit-tests against the mouse. Pure presentation — no game state lives here. T()/DrawRect()/
// Frame() are the text/box helpers; sizes scale by a UI unit `u`.
public partial class Hud : Control
{
    private string _banner = "";
    private float _bannerT = 0f;
    private static readonly string[] KL = { "1", "2", "3", "4", "5" };
    private const float Tau = Mathf.Pi * 2f;

    public Rect2 RPauseResume, RPauseOptions, RPauseQuit, RPauseRestart, ROver, RChangeWitch;   // pause = Options / Quit Run / Restart Run (+ Resume via Esc)
    public Rect2 ROverRetry, ROverCharSelect, ROverEnd;   // (NEW) MP game-over host options
    public Rect2[] RPauseBind = new Rect2[5];
    public int PauseBindAt(Vector2 pos)
    {
        for (int i = 0; i < RPauseBind.Length; i++) if (RPauseBind[i].Size.X > 0 && RPauseBind[i].HasPoint(pos)) return i;
        return -1;
    }
    public static string KeyName(Key k)
    {
        string s = k.ToString();
        if (s.StartsWith("Key")) s = s.Substring(3);   // "Key1" -> "1"
        return k == Key.None ? "\u2014" : s;
    }
    public Rect2[] RUlt = { new Rect2(), new Rect2(), new Rect2() };
    public Rect2[] RUltMenu = { new Rect2(), new Rect2() };
    public Rect2[] RRoulette = { new Rect2(), new Rect2() };
    public Rect2[] RMystic = { new Rect2(), new Rect2(), new Rect2() };
    public Rect2[] RScroll = new Rect2[8];
    public Rect2 RScrollClose = new Rect2();
    public int ScrollAt(Vector2 pos)
    {
        if (RScrollClose.HasPoint(pos)) return -1;
        for (int i = 0; i < RScroll.Length; i++) if (RScroll[i].Size.X > 0 && RScroll[i].HasPoint(pos)) return i;
        return -2;
    }
    public Rect2[] RShop = new Rect2[12];
    public Rect2 RShopClose = new Rect2();
    public int ShopAt(Vector2 pos)
    {
        if (RShopClose.Size.X > 0 && RShopClose.HasPoint(pos)) return -1;
        for (int i = 0; i < RShop.Length; i++) if (RShop[i].Size.X > 0 && RShop[i].HasPoint(pos)) return i;
        return -2;
    }
    public Rect2[] RWitch = { new Rect2(), new Rect2(), new Rect2(), new Rect2(), new Rect2(), new Rect2(), new Rect2() };   // 5th = Gale, 6th = Frost, 7th = Forsaken (NEW)

    private Font _head, _body, _impact;
    private bool _fontsLoaded = false;

    private int _gen = -1;
    private float _panelT = 0f;

    // ===== level-up SLOT ROLL state (NEW) — cards spin, tick past, then slam to a stop left→right =====
    private const float RollSpinBase = 0.55f;    // when the FIRST card locks
    private const float RollStagger = 0.42f;     // gap between successive card locks
    private const float RollScramble = 0.055f;   // how fast the fake rarity flickers while spinning
    private bool _rollActive = false;
    private readonly bool[] _rollLocked = new bool[3];
    private float _rollTickT = 0f;                // throttle for the spin tick sound
    private int _rollScrambleTier = 0;           // the fake rarity currently shown on spinning cards
    private float _rollScrambleT = 0f;
    // the moment card i comes to rest
    private static float RollLockAt(int i) => RollSpinBase + i * RollStagger;
    private float RollTotal => RollLockAt(2);
    public bool RollBusy => _rollActive && _panelT < RollTotal;
    // click/keypress during the spin slams everything to a stop instead of selecting
    public void FinishRoll()
    {
        if (!_rollActive) return;
        var g = Game.I;
        for (int i = 0; i < 3; i++)
            if (!_rollLocked[i]) { _rollLocked[i] = true; if (g?.Choices != null && i < g.Choices.Count) g.Sfx?.RollLock((int)g.Choices[i].Rarity); }
        _panelT = Mathf.Max(_panelT, RollTotal);
    }

    private struct Pop { public Vector3 W; public string Txt; public Color Col; public float T; }
    private readonly List<Pop> _pops = new();
    private const float PopMax = 1.15f;
    private readonly RandomNumberGenerator _orng = new();

    private static readonly Dictionary<DamageType, string[]> Ono = new()
    {
        { DamageType.Lunar,    new[]{ "GLEAM!", "LUMEN!", "SHINE!" } },
        { DamageType.Arcane,   new[]{ "ZWAK!", "ZORP!", "WHRRM!" } },
        { DamageType.Nature,   new[]{ "SNAP!", "KRAK!", "THORN!" } },
        { DamageType.Frost,    new[]{ "FWISH!", "KRSSH!", "CHILL!" } },
        { DamageType.Curse,    new[]{ "HSSSK!", "DOOM!", "WRETCH!" } },
        { DamageType.Holy,     new[]{ "DING!", "HALO!", "PURGE!" } },
        { DamageType.Ember,    new[]{ "KABOOM!", "FWOOMP!", "BURST!" } },
        { DamageType.Blood,    new[]{ "SPLRT!", "RUPTURE!", "GUSH!", "VISCERA!" } },
        { DamageType.Physical, new[]{ "WHAK!", "BAM!", "POW!" } },
        { DamageType.Wind,     new[]{ "WHOOSH!", "GUST!", "FWOOSH!", "SWISH!" } },
    };

    private static readonly Color Gold = new Color(1.0f, 0.83f, 0.36f);
    private static readonly Color GoldDim = new Color(0.78f, 0.63f, 0.32f);
    private static readonly Color Ink = new Color(0.04f, 0.02f, 0.07f, 0.96f);
    private static readonly Color Panel = new Color(0.06f, 0.04f, 0.10f, 0.92f);
    private static readonly Color Faint = new Color(1f, 0.83f, 0.36f, 0.18f);
    private static readonly Color ValCol = new Color(0.93f, 0.9f, 1f);

    public override void _Ready()
    {
        SetAnchorsPreset(Control.LayoutPreset.FullRect);
        MouseFilter = Control.MouseFilterEnum.Ignore;
        _orng.Randomize();
    }

    private void EnsureFonts()
    {
        if (_fontsLoaded) return;
        _fontsLoaded = true;
        var def = GetThemeDefaultFont();
        _head = Load("res://fonts/CinzelDecorative-Black.ttf", def);
        _body = Load("res://fonts/CinzelDecorative-Bold.ttf", def);
        _impact = Load("res://fonts/PirataOne-Regular.ttf", def);
    }
    private Font Load(string path, Font fb) => ResourceLoader.Exists(path) ? ResourceLoader.Load<Font>(path) : fb;

    public void Banner(string txt) { _banner = txt; _bannerT = 2.2f; }

    private string _flourTxt = "";
    private float _flourT = 0f;
    private Color _flourCol = Colors.White;
    private float _breakT = 0f;
    private int _comboSeen = 0;       // (NEW) last combo value drawn — detects growth for a build-up pop
    private float _comboPopT = 0f;    // (NEW) brief pop each time the combo grows
    public void ComboBreak() { _breakT = 0.6f; }
    public void ComboFlourish(Player.ComboAct act)
    {
        switch (act)
        {
            case Player.ComboAct.Finisher: _flourTxt = "UNLEASH"; _flourCol = new Color(0.82f, 0.40f, 1f); break;
            case Player.ComboAct.Charged:  _flourTxt = "SURGE";   _flourCol = new Color(0.55f, 0.80f, 1f); break;
            default:                       _flourTxt = "WEAVE";   _flourCol = new Color(1f, 0.50f, 0.86f); break;
        }
        _flourT = 0.7f;
    }

    public void AddKill(Vector3 w, DamageType ty)
    {
        var arr = Ono.TryGetValue(ty, out var a) ? a : new[] { "POW!" };
        _pops.Add(new Pop { W = w, Txt = arr[_orng.RandiRange(0, arr.Length - 1)], Col = DamageTypes.Col(ty), T = 0 });
        if (_pops.Count > 40) _pops.RemoveAt(0);
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        if (_bannerT > 0) _bannerT -= dt;
        var g = Game.I;
        if (g != null && g.ChoiceGen != _gen)
        {
            _gen = g.ChoiceGen; _panelT = 0f;
            // (NEW) arm the slot roll whenever a fresh pick-3 (or reroll) appears
            _rollActive = g.State == GameState.LevelUp && g.Choices != null && g.Choices.Count > 0;
            for (int i = 0; i < 3; i++) _rollLocked[i] = false;
            _rollTickT = 0f; _rollScrambleT = 0f;
        }
        _panelT += dt;
        UpdateRoll(g, dt);
        for (int i = _pops.Count - 1; i >= 0; i--) { var p = _pops[i]; p.T += dt; _pops[i] = p; if (p.T >= PopMax) _pops.RemoveAt(i); }
        if (_flourT > 0f) _flourT -= dt;
        if (_comboPopT > 0f) _comboPopT -= dt;   // (NEW)
        if (_breakT > 0f) _breakT -= dt;
        QueueRedraw();
    }

    // drive the slot roll's sounds + scramble in _Process (not _Draw) so the ticks fire steadily regardless of frame timing
    private void UpdateRoll(Game g, float dt)
    {
        if (!_rollActive) return;
        if (g == null || g.State != GameState.LevelUp || g.Choices == null) { _rollActive = false; return; }

        // the flickering fake rarity shown on every still-spinning card
        _rollScrambleT -= dt;
        if (_rollScrambleT <= 0f)
        {
            _rollScrambleT = RollScramble;
            _rollScrambleTier = (_rollScrambleTier + 1 + _orng.RandiRange(0, 3)) % 5;   // jump around the rarity ladder
        }

        bool anySpinning = false;
        for (int i = 0; i < g.Choices.Count && i < 3; i++)
        {
            if (_rollLocked[i]) continue;
            if (_panelT >= RollLockAt(i))
            {
                _rollLocked[i] = true;
                g.Sfx?.RollLock((int)g.Choices[i].Rarity);   // this card slams home — sound scales with its rarity
            }
            else anySpinning = true;
        }

        // steady witchy ticking while anything is still spinning; speeds up slightly as it's about to stop
        if (anySpinning)
        {
            _rollTickT -= dt;
            if (_rollTickT <= 0f) { _rollTickT = 0.052f; g.Sfx?.RollTick(); }
        }
        else _rollActive = false;   // all three settled
    }

    private float U => Mathf.Clamp(GetViewportRect().Size.Y / 900f, 0.62f, 2.4f);

    private void T(Font f, Vector2 p, string s, float size, Color col, HorizontalAlignment a = HorizontalAlignment.Left, float w = -1, int outline = 0)
    {
        int fs = Mathf.Max(1, Mathf.RoundToInt(size));
        if (outline > 0) DrawStringOutline(f, p, s, a, w, fs, outline, Ink);
        DrawString(f, p, s, a, w, fs, col);
    }

    // a little flame glyph (drawn, so it always renders) centered at c, height ~2s.
    // Built ONLY from circles — DrawColoredPolygon triangulation is unreliable in this Godot build (even for triangles),
    // so a stack of shrinking circles gives a flame silhouette with zero triangulation risk.
    private void DrawFlameIcon(Vector2 c, float s, Color col)
    {
        if (s < 1f) s = 1f;
        DrawCircle(c + new Vector2(0, 0.45f * s), 0.5f * s, col);    // base
        DrawCircle(c + new Vector2(0, 0.0f * s), 0.36f * s, col);    // body
        DrawCircle(c + new Vector2(0, -0.42f * s), 0.22f * s, col);  // upper
        DrawCircle(c + new Vector2(0, -0.78f * s), 0.11f * s, col);  // tip
        var y = new Color(1f, 0.92f, 0.45f, col.A);
        DrawCircle(c + new Vector2(0, 0.42f * s), 0.26f * s, y);     // inner glow
        DrawCircle(c + new Vector2(0, -0.05f * s), 0.15f * s, y);
    }

    // a little bomb glyph with a sparking fuse, centered at c
    private void DrawBombIcon(Vector2 c, float s, float spark)
    {
        DrawCircle(c + new Vector2(0, 0.2f * s), 0.54f * s, new Color(0.1f, 0.1f, 0.12f));
        DrawCircle(c + new Vector2(-0.18f * s, 0.02f * s), 0.15f * s, new Color(0.4f, 0.4f, 0.46f));   // highlight
        DrawLine(c + new Vector2(0.08f * s, -0.32f * s), c + new Vector2(0.42f * s, -0.74f * s), new Color(0.5f, 0.42f, 0.28f), Mathf.Max(1f, 0.14f * s));
        DrawCircle(c + new Vector2(0.42f * s, -0.74f * s), 0.18f * s * (0.7f + 0.5f * spark), new Color(1f, 0.72f, 0.22f));
    }

    // draw an icon + short label as ONE group centered horizontally on cx (fits regardless of count width)
    private void IconLabel(Font f, float cx, float baselineY, bool bomb, string txt, float fs, Color txtCol, float spark = 0f)
    {
        float iconS = fs, gap = 3f;
        var tw = f.GetStringSize(txt, HorizontalAlignment.Left, -1, Mathf.RoundToInt(fs));
        float total = iconS + gap + tw.X, sx = cx - total / 2f;
        var ic = new Vector2(sx + iconS * 0.5f, baselineY - fs * 0.34f);
        if (bomb) DrawBombIcon(ic, iconS, spark); else DrawFlameIcon(ic, iconS * 0.9f, new Color(1f, 0.55f, 0.2f));
        T(f, new Vector2(sx + iconS + gap, baselineY), txt, fs, txtCol, HorizontalAlignment.Left, -1, Mathf.RoundToInt(1f * (fs / 10f)));
    }

    // a top-right radar: player-relative (up = facing), dots for nearby threats & points of interest
    private void DrawMinimap(Game g, Player p, Vector2 vp, float u)
    {
        float radius = 90 * u, range = 57.5f;   // (TUNE) +25% bigger radar, +25% more world shown around the player
        float cx = vp.X - radius - 18 * u, cy = radius + 18 * u;
        var ctr = new Vector2(cx, cy);
        DrawCircle(ctr, radius + 3 * u, new Color(0, 0, 0, 0.45f));
        DrawCircle(ctr, radius, g.InOverworld ? new Color(0.02f, 0.025f, 0.04f, 0.9f) : new Color(0.05f, 0.06f, 0.10f, 0.72f));   // (NEW) heavy fog base in the overworld — cleared cells drawn lighter over it
        DrawArc(ctr, radius, 0, Tau, 44, new Color(0.6f, 0.7f, 0.9f, 0.5f), 1.5f * u);

        float yaw = p.Rotation.Y;
        float cosY = Mathf.Cos(yaw), sinY = Mathf.Sin(yaw);   // player-locked radar: forward = up (corrected handedness)
        float sc = radius / range;
        Vector2 plotCtr = ctr;
        Vector2 Plot(Vector3 world, out bool inRange)
        {
            float dx = world.X - p.GlobalPosition.X, dz = world.Z - p.GlobalPosition.Z;
            float rxr = dx * cosY - dz * sinY;
            float rzr = dx * sinY + dz * cosY;
            inRange = (dx * dx + dz * dz) <= range * range;
            return new Vector2(plotCtr.X + rxr * sc, plotCtr.Y + rzr * sc);
        }

        // ---- fog of war (overworld only): dark until your forward vision cone sweeps a cell; cleared cells persist ----
        Vector2 Clamp(Vector2 sp) { var dv = sp - ctr; return dv.Length() > radius ? ctr + dv.Normalized() * radius : sp; }
        if (g.InOverworld)
        {
            var exploredCol = new Color(0.17f, 0.19f, 0.25f, 0.6f);
            float cellPx = Game.DiscCell * sc;
            int cr = Mathf.CeilToInt(range / Game.DiscCell) + 1;
            int pcx = Mathf.FloorToInt(p.GlobalPosition.X / Game.DiscCell), pcz = Mathf.FloorToInt(p.GlobalPosition.Z / Game.DiscCell);
            for (int cxi = pcx - cr; cxi <= pcx + cr; cxi++)
                for (int czi = pcz - cr; czi <= pcz + cr; czi++)
                {
                    if (!g.DiscoveredCell(cxi, czi)) continue;
                    var sp = Plot(new Vector3((cxi + 0.5f) * Game.DiscCell, 0, (czi + 0.5f) * Game.DiscCell), out _);
                    if (sp.DistanceTo(ctr) > radius) continue;   // clip fog cells to the radar disc
                    DrawCircle(sp, cellPx * 0.72f, exploredCol);
                }
        }

        // ---- discoverables: shown only once the fog reveals their cell; each with its own icon (edge-clamped off-radar) ----
        foreach (var r in g.Rituals)   // light circle in the rite's colour
        {
            if (r == null || !GodotObject.IsInstanceValid(r) || !g.Discovered(r.GlobalPosition)) continue;
            var sp = Clamp(Plot(r.GlobalPosition, out _));
            var rc = (r.Type == RiteType.Ward ? DamageTypes.Col(DamageType.Lunar) : r.Type == RiteType.Summon ? DamageTypes.Col(DamageType.Curse) : DamageTypes.Col(DamageType.Holy)).Lerp(Colors.White, 0.35f);
            DrawCircle(sp, 5.5f * u, new Color(rc.R, rc.G, rc.B, 0.3f));
            DrawCircle(sp, 3.2f * u, rc);
        }
        foreach (var ch in g.Chests)   // gold rectangle — greyed once opened
        {
            if (ch == null || !GodotObject.IsInstanceValid(ch) || ch.Hidden || !g.Discovered(ch.GlobalPosition)) continue;
            var raw = Plot(ch.GlobalPosition, out bool chIn);
            if (ch.Opened && !chIn) continue;                 // (FIX) a LOOTED chest is only a memory marker — don't edge-clamp it to the rim; just show it when it's actually on the radar
            var sp = ch.Opened ? raw : Clamp(raw);            // unopened chests still clamp to the edge so they point you toward loot
            var chc = ch.Opened ? new Color(0.5f, 0.5f, 0.55f, 0.75f) : new Color(1f, 0.82f, 0.3f, 0.98f);   // (NEW) opened → greyed out
            var clc = ch.Opened ? new Color(0.32f, 0.32f, 0.34f, 0.75f) : new Color(0.45f, 0.33f, 0.1f, 0.95f);
            DrawRect(new Rect2(sp.X - 3.6f * u, sp.Y - 3.6f * u, 7.2f * u, 7.2f * u), chc);
            DrawRect(new Rect2(sp.X - 3.6f * u, sp.Y - 0.7f * u, 7.2f * u, 1.4f * u), clc);   // clasp line
        }
        foreach (var ef in g.Effigies)   // diamond in the effigy's theme colour
        {
            if (ef == null || !GodotObject.IsInstanceValid(ef) || ef.Claimed || !g.Discovered(ef.GlobalPosition)) continue;
            var sp = Clamp(Plot(ef.GlobalPosition, out _));
            var col = EffigyCol(ef.Kind);
            DrawCircle(sp, 4.8f * u, new Color(col.R, col.G, col.B, 0.28f));
            Diamond(sp, 3.6f * u, col);
        }
        // (GALE NET) wind pad: a dot with a tick pointing the way it launches you (dir rotated into the player-locked frame).
        // NOT edge-clamped — with 20 pads, clamping pinned a permanent ring of them to the rim; they only show when actually on the radar.
        foreach (var gp in g.GalePads)
        {
            if (gp == null || !GodotObject.IsInstanceValid(gp) || !g.Discovered(gp.GlobalPosition)) continue;
            var sp = Plot(gp.GlobalPosition, out bool gpIn);
            if (!gpIn || sp.DistanceTo(ctr) > radius - 4f * u) continue;
            var wc = DamageTypes.Col(DamageType.Wind).Lerp(Colors.White, 0.2f);
            var ld = gp.LaunchDir;
            float tx = ld.X * cosY - ld.Z * sinY, tz = ld.X * sinY + ld.Z * cosY;
            var tip = new Vector2(sp.X + tx * 8f * u, sp.Y + tz * 8f * u);
            DrawCircle(sp, 3.4f * u, new Color(wc.R, wc.G, wc.B, 0.3f));
            DrawLine(sp, tip, wc, 1.6f * u);
            DrawCircle(tip, 1.4f * u, wc);
            DrawCircle(sp, 2.1f * u, wc);
        }
        foreach (var mg in g.Magnets)   // (MAGNET DROP) a dropped lodestone — ALWAYS shown (valuable + transient), edge-clamped; little violet horseshoe
        {
            if (mg == null || !GodotObject.IsInstanceValid(mg)) continue;
            var sp = Clamp(Plot(mg.GlobalPosition, out _));
            var mc = new Color(0.82f, 0.55f, 1f);
            DrawCircle(sp, 5f * u, new Color(mc.R, mc.G, mc.B, 0.3f));                        // pull glow
            DrawRect(new Rect2(sp.X - 3f * u, sp.Y - 3.2f * u, 1.7f * u, 5f * u), mc);        // left prong
            DrawRect(new Rect2(sp.X + 1.3f * u, sp.Y - 3.2f * u, 1.7f * u, 5f * u), mc);      // right prong
            DrawRect(new Rect2(sp.X - 3f * u, sp.Y + 1.4f * u, 6f * u, 1.7f * u), mc);        // base
        }
        // (NERFER) the standing boss-weakening shrine — ALWAYS revealed (no fog gate) and edge-CLAMPED to the radar rim, so it
        // reads as an objective you can navigate to from anywhere. Once it's ARMED (State 2) it's spent — drop the pin entirely
        // so a finished shrine stops competing with live objectives for your attention.
        foreach (var s in g.Nerfers)
        {
            if (s == null || !GodotObject.IsInstanceValid(s) || s.State == 2) continue;
            var sp = Clamp(Plot(s.GlobalPosition, out var ir));
            var nc = s.IconColor;
            if (!ir) DrawCircle(sp, 6.5f * u, new Color(nc.R, nc.G, nc.B, 0.25f));     // off-radar: a soft halo so the clamped pin reads as "over there"
            DrawArc(sp, 4.8f * u, 0, Tau, 18, nc, (s.State == 2 ? 2.4f : 1.6f) * u);   // ring
            DrawCircle(sp, 2f * u, nc);                                                // core
        }
        // (CRIMSON RITE) the blood sigils — an active objective, so always revealed and edge-clamped like the nerfer shrine.
        // A LIT sigil is done: it drops off the radar entirely (per design), leaving only the ones still needing a warden.
        foreach (var rs in g.RiteSigils)
        {
            if (rs == null || !GodotObject.IsInstanceValid(rs) || rs.Lit) continue;
            var sp = Clamp(Plot(rs.GlobalPosition, out var rsIn));
            var rc2 = RiteSigil.Col;
            if (!rsIn) DrawCircle(sp, 6.5f * u, new Color(rc2.R, rc2.G, rc2.B, 0.25f));      // off-radar halo → "over there"
            DrawArc(sp, 4.6f * u, 0, Tau, 18, new Color(rc2.R, rc2.G, rc2.B, 0.55f), 1.5f * u);
            if (rs.Charge > 0.001f)                                                           // partial fill reads on the pin too
                DrawArc(sp, 4.6f * u, -Mathf.Pi / 2f, -Mathf.Pi / 2f + Tau * Mathf.Clamp(rs.Charge, 0f, 1f), 20, rc2, 2.6f * u);
            DrawCircle(sp, 1.9f * u, rc2);
        }
        // vendors — each its own icon (fog-gated like the other discoverables)
        if (g.VendorMystic != null && GodotObject.IsInstanceValid(g.VendorMystic) && g.Discovered(g.VendorMystic.GlobalPosition))
        {
            var sp = Clamp(Plot(g.VendorMystic.GlobalPosition, out _)); var mc = new Color(0.4f, 0.95f, 0.9f);
            DrawCircle(sp, 4f * u, new Color(mc.R, mc.G, mc.B, 0.3f)); DrawCircle(sp, 3.2f * u, mc); DrawCircle(sp, 1.3f * u, new Color(0.03f, 0.06f, 0.08f));   // teal ringed eye
        }
        if (g.VendorScroll != null && GodotObject.IsInstanceValid(g.VendorScroll) && g.Discovered(g.VendorScroll.GlobalPosition))
        {
            var sp = Clamp(Plot(g.VendorScroll.GlobalPosition, out _)); var scol = new Color(0.88f, 0.8f, 0.52f);
            DrawRect(new Rect2(sp.X - 3f * u, sp.Y - 4f * u, 6f * u, 8f * u), scol);   // parchment scroll
            DrawRect(new Rect2(sp.X - 3f * u, sp.Y - 1.3f * u, 6f * u, 0.9f * u), new Color(0.38f, 0.3f, 0.14f, 0.85f));
            DrawRect(new Rect2(sp.X - 3f * u, sp.Y + 0.8f * u, 6f * u, 0.9f * u), new Color(0.38f, 0.3f, 0.14f, 0.85f));
        }
        if (g.VendorShop != null && GodotObject.IsInstanceValid(g.VendorShop) && g.Discovered(g.VendorShop.GlobalPosition))
        {
            var sp = Clamp(Plot(g.VendorShop.GlobalPosition, out _));
            SafePoly(new[] { new Vector2(sp.X, sp.Y - 4.6f * u), new Vector2(sp.X + 4.6f * u, sp.Y + 3f * u), new Vector2(sp.X - 4.6f * u, sp.Y + 3f * u) }, new Color(1f, 0.66f, 0.28f));   // peddler stall (tent)
        }
        foreach (var rl in g.RouletteList)   // wheel of fortune
        {
            if (rl == null || !GodotObject.IsInstanceValid(rl) || !g.Discovered(rl.GlobalPosition)) continue;
            var sp = Clamp(Plot(rl.GlobalPosition, out _)); var wc2 = new Color(1f, 0.85f, 0.35f);
            DrawArc(sp, 3.8f * u, 0, Tau, 18, wc2, 1.6f * u);
            DrawLine(new Vector2(sp.X - 3.8f * u, sp.Y), new Vector2(sp.X + 3.8f * u, sp.Y), wc2, 1.1f * u);
            DrawLine(new Vector2(sp.X, sp.Y - 3.8f * u), new Vector2(sp.X, sp.Y + 3.8f * u), wc2, 1.1f * u);
        }
        // garden travel portals + gate — navigation aids, ALWAYS shown (not fog-gated). NOT edge-clamped (user pref):
        // they only appear once actually on the radar, vanishing off-rim instead of pinning to the edge.
        foreach (var pt in g.GardenPortals)
        {
            if (pt == null || !GodotObject.IsInstanceValid(pt) || !pt.IsEntrance) continue;
            var sp = Plot(pt.GlobalPosition, out var ir); if (!ir) continue;
            DrawArc(sp, 4.4f * u, 0, Tau, 18, new Color(pt.Tint.R, pt.Tint.G, pt.Tint.B, 0.9f), 1.8f * u);   // portal ring
            DrawCircle(sp, 1.5f * u, pt.Tint);
        }
        if (g.GardenGateActive)
        {
            var sp = Plot(g.GardenGatePos, out var ir);
            if (ir)
            {
                DrawArc(sp, 5f * u, 0, Tau, 20, new Color(0.6f, 1f, 0.7f, 0.85f), 2f * u);
                DrawCircle(sp, 2.4f * u, new Color(0.6f, 1f, 0.7f));
            }
        }
        // (BOSS-LAIR) the world objective — ALWAYS shown, edge-clamped; colour = state (sealed red / active amber / conquered grey)
        if (g.Lair != null && GodotObject.IsInstanceValid(g.Lair))
        {
            var sp = Clamp(Plot(g.Lair.GlobalPosition, out _));
            var lc = g.Lair.IconColor;
            DrawCircle(sp, 7f * u, new Color(lc.R, lc.G, lc.B, 0.32f));
            SafePoly(new[] { new Vector2(sp.X - 4.6f * u, sp.Y - 4f * u), new Vector2(sp.X + 4.6f * u, sp.Y - 4f * u), new Vector2(sp.X, sp.Y + 4.8f * u) }, lc);   // a fanged maw pointing down
            DrawArc(sp, 7f * u, 0, Tau, 22, new Color(lc.R, lc.G, lc.B, 0.95f), 1.8f * u);
        }
        // (HAUNT) the roaming hot-zone — ALWAYS shown, edge-clamped, a pulsing magenta ring with the fill arc inside
        if (g.HauntActive)
        {
            var hc = new Color(0.9f, 0.34f, 0.72f);
            var sp = Clamp(Plot(g.HauntCenter, out var ir));
            float pr = 0.5f + 0.5f * Mathf.Sin((float)Time.GetTicksMsec() * 0.005f);
            if (ir)
            {
                float rr = Mathf.Max(6f * u, g.HauntRadius * sc);   // draw the zone at true scale when on-radar
                DrawCircle(sp, rr, new Color(hc.R, hc.G, hc.B, 0.12f + 0.06f * pr));
                DrawArc(sp, rr, 0, Tau, 28, new Color(hc.R, hc.G, hc.B, 0.85f), 1.8f * u);
                if (g.HauntFrac > 0.001f) DrawArc(sp, rr - 2.5f * u, -Mathf.Pi / 2f, -Mathf.Pi / 2f + Tau * g.HauntFrac, 26, new Color(1f, 0.85f, 0.4f), 2.4f * u);   // fill ring
            }
            else
            {
                DrawCircle(sp, 6.5f * u, new Color(hc.R, hc.G, hc.B, 0.28f));
                DrawArc(sp, 5f * u, 0, Tau, 18, new Color(hc.R, hc.G, hc.B, 0.7f + 0.3f * pr), 2f * u);
            }
        }
        var orbCol = new Color(0.62f, 0.86f, 1f, 0.8f);   // (NEW) tiny XP-orb specks (persistent on the map)
        foreach (var o in g.Orbs)
        { if (o == null || !GodotObject.IsInstanceValid(o)) continue; var sp = Plot(o.GlobalPosition, out var ir); if (ir) DrawCircle(sp, 1.1f * u, orbCol); }
        if (g.NetMgr != null && g.NetMgr.Active)
            foreach (var op in g.NetMgr.RemoteOrbPositions())
            { var sp = Plot(op, out var ir); if (ir) DrawCircle(sp, 1.1f * u, orbCol); }
        foreach (var e in g.Enemies)
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
            var sp = Plot(e.GlobalPosition, out var ir); if (!ir) continue;
            float rr; Color ec;
            if (e.IsBoss) { rr = 5.5f * u; ec = new Color(1f, 0.25f, 0.3f, 1f); }
            else if (e.Elite) { rr = 3.5f * u; ec = new Color(1f, 0.5f, 0.25f, 1f); }
            else if (e.Affix > 0) { rr = 3f * u; ec = new Color(1f, 0.7f, 0.3f, 0.95f); }
            else { rr = 2f * u; ec = new Color(1f, 0.45f, 0.45f, 0.9f); }
            DrawCircle(sp, rr, ec);
        }
        var entCol = new Color(0.4f, 0.92f, 0.45f, 0.95f);   // tree-ents (yours + allies') as small green dots
        foreach (var t in p.Ents)
        { if (t == null || !GodotObject.IsInstanceValid(t)) continue; var sp = Plot(t.GlobalPosition, out var ir); if (ir) DrawCircle(sp, 2.2f * u, entCol); }
        if (g.NetMgr != null && g.NetMgr.Active)
        {
            foreach (var gm in g.NetMgr.GhostMinionPositions())
            { var sp = Plot(gm, out var ir); if (ir) DrawCircle(sp, 2.2f * u, entCol); }
            foreach (var av in g.NetMgr.AllyAvatars())   // ally witches in their own witch color
            {
                if (av == null || !GodotObject.IsInstanceValid(av)) continue;
                var sp = Plot(av.GlobalPosition, out var ir); if (!ir) continue;
                var ac = av.WitchCol;
                if (av.Downed) ac = new Color(ac.R, ac.G, ac.B, 0.4f);
                DrawCircle(sp, 3.5f * u, new Color(ac.R, ac.G, ac.B, 0.95f));
                DrawArc(sp, 4.5f * u, 0, Tau, 16, new Color(1f, 1f, 1f, 0.5f), 1f * u);   // white ring marks an ally
                if (av.Blessed > 0f) DrawArc(sp, 6.5f * u, 0, Tau, 20, DamageTypes.Col(DamageType.Holy), 1.8f * u);   // (NEW) holy ring = Blessed
            }
        }

        if (g.InMaze)   // (NEW) maze breadcrumbs: dot + a line showing which way each wisp points
        {
            var wcol = new Color(0.72f, 0.9f, 1f, 0.95f);
            foreach (var w in g.MazeWisps)
            {
                if (w == null || !GodotObject.IsInstanceValid(w)) continue;
                var sp = Plot(w.GlobalPosition, out var ir); if (!ir) continue;
                DrawCircle(sp, 2.2f * u, wcol);
                var dd = w.Dir;
                if (dd.LengthSquared() > 0.01f)
                {
                    float rdx = dd.X * cosY - dd.Z * sinY, rdz = dd.X * sinY + dd.Z * cosY;
                    var ad = new Vector2(rdx, rdz);
                    if (ad.LengthSquared() > 0.0001f) DrawLine(sp, sp + ad.Normalized() * 8f * u, wcol, 2f * u);
                }
            }
        }

        var stp = g.MazeStatueTargetPos;   // (NEW) solo objective marker, edge-clamped so it points the way
        if (stp.HasValue)
        {
            var sp2 = Plot(stp.Value, out var ir2);
            var mc = g.MazeStatueColor;
            if (!ir2) { var dv = sp2 - ctr; if (dv.Length() > radius) sp2 = ctr + dv.Normalized() * radius; }
            DrawCircle(sp2, 4f * u, mc);
            DrawArc(sp2, 6f * u, 0, Tau, 18, new Color(mc.R, mc.G, mc.B, 0.8f), 1.6f * u);
        }

        // (NEW) revealed cauldron — a cauldron icon, edge-clamped so it always points the way (pinned from the skybeam reveal on)
        var cpz = g.MazeCauldronRevealedPos;
        if (cpz.HasValue)
        {
            var sp = Plot(cpz.Value, out var ir);
            if (!ir) { var dv = sp - ctr; if (dv.Length() > radius) sp = ctr + dv.Normalized() * radius; }
            var cauCol = new Color(0.78f, 0.5f, 1f);   // witchy cauldron glow
            DrawCircle(sp, 5.5f * u, new Color(cauCol.R, cauCol.G, cauCol.B, 0.4f));                 // halo
            DrawCircle(sp, 3.6f * u, new Color(0.12f, 0.10f, 0.16f));                                // dark iron body
            DrawArc(sp, 3.6f * u, 0, Tau, 16, cauCol, 1.6f * u);                                     // glowing rim
            DrawRect(new Rect2(sp.X - 4.4f * u, sp.Y - 4.8f * u, 8.8f * u, 1.4f * u), cauCol);        // handle bar across the top
        }
        // (NEW) exit portal — a portal ring icon once the way out is open (pinned until you leave)
        if (g.InMaze && g.MazeFound)
        {
            var sp = Plot(g.MazePortalWorld, out var ir);
            if (!ir) { var dv = sp - ctr; if (dv.Length() > radius) sp = ctr + dv.Normalized() * radius; }
            var exCol = new Color(0.55f, 1f, 0.72f);   // mint escape portal
            DrawArc(sp, 5f * u, 0, Tau, 20, exCol, 2f * u);                                          // portal ring
            DrawArc(sp, 3f * u, 0, Tau, 16, new Color(exCol.R, exCol.G, exCol.B, 0.7f), 1.4f * u);
            DrawCircle(sp, 1.4f * u, new Color(exCol.R, exCol.G, exCol.B, 0.95f));                    // swirling core
        }

        foreach (var bl in g.Blips)   // (NEW) firework pings — triangulate allies (edge-clamped so they point)
        {
            var bp = Plot(bl.Pos, out var bir);
            float ba = Mathf.Clamp(bl.T / 6f, 0f, 1f);
            var bc = new Color(bl.Col.R, bl.Col.G, bl.Col.B, ba);
            if (!bir) { var dv = bp - ctr; if (dv.Length() > radius) bp = ctr + dv.Normalized() * radius; }
            DrawCircle(bp, 3.5f * u, bc);
            DrawArc(bp, 5.5f * u, 0, Tau, 16, new Color(bc.R, bc.G, bc.B, ba * 0.8f), 1.4f * u);
        }

        var pcol = new Color(1f, 1f, 1f, 0.95f);   // you, at center, with a forward tick
        DrawCircle(ctr, 3 * u, pcol);
        DrawLine(ctr, ctr + new Vector2(0, -9 * u), pcol, 2f * u);
    }

    // a hover popup: a small panel near the cursor with a title + word-wrapped description
    private void DrawTooltip(Vector2 mouse, Vector2 vp, string title, string body, Color col, float u)
    {
        if (string.IsNullOrEmpty(body)) return;
        float w = 268 * u, pad = 10 * u, lineH = 15 * u;
        var lines = WrapText(_body, body, 12 * u, w - pad * 2);   // (NEW) pixel-accurate wrap — never overflows the panel width
        float h = pad * 2 + 20 * u + lines.Count * lineH;
        float x = mouse.X + 16 * u, y = mouse.Y + 10 * u;
        if (x + w > vp.X - 4 * u) x = mouse.X - w - 16 * u;
        if (y + h > vp.Y - 4 * u) y = vp.Y - h - 4 * u;
        if (x < 4 * u) x = 4 * u;
        if (y < 4 * u) y = 4 * u;
        var panel = new Rect2(x, y, w, h);
        DrawRect(panel, new Color(0.04f, 0.03f, 0.07f, 0.97f));
        Frame(panel, new Color(col.R, col.G, col.B, 0.9f), 1.5f * u);
        T(_body, new Vector2(x + pad, y + pad + 12 * u), title, 14 * u, new Color(col.R, col.G, col.B), HorizontalAlignment.Left, w - pad * 2, Mathf.RoundToInt(2 * u));
        float ly = y + pad + 30 * u;
        foreach (var ln in lines) { T(_body, new Vector2(x + pad, ly), ln, 12 * u, GoldDim, HorizontalAlignment.Left, w - pad * 2, 0); ly += lineH; }
    }

    // wrapping multi-line text (DrawString does not wrap) — used for card descriptions
    private void TM(Font f, Vector2 p, string s, float size, Color col, float w)
    {
        int fs = Mathf.Max(1, Mathf.RoundToInt(size));
        DrawMultilineString(f, p, s, HorizontalAlignment.Left, w, fs, -1, col);
    }

    // (NEW) auto-fit multi-line text into a box: shrink the font until the wrapped text fits within maxH so descriptions
    // NEVER clip, no matter how long — dynamic for any future spell/mod text.
    private void TMFit(Font f, Vector2 p, string s, float size, Color col, float w, float maxH)
    {
        int fs = Mathf.Max(1, Mathf.RoundToInt(size));
        while (fs > 7 && f.GetMultilineStringSize(s, HorizontalAlignment.Left, w, fs).Y > maxH) fs--;
        DrawMultilineString(f, p, s, HorizontalAlignment.Left, w, fs, -1, col);
    }

    // (NEW) pixel-accurate word wrap (measures each word, unlike the old char-count heuristic that could overflow the box)
    private System.Collections.Generic.List<string> WrapText(Font f, string s, float size, float maxW)
    {
        int fs = Mathf.Max(1, Mathf.RoundToInt(size));
        var lines = new System.Collections.Generic.List<string>();
        string cur = "";
        foreach (var wd in s.Split(' '))
        {
            string next = cur.Length == 0 ? wd : cur + " " + wd;
            if (cur.Length > 0 && f.GetStringSize(next, HorizontalAlignment.Left, -1, fs).X > maxW) { lines.Add(cur); cur = wd; }
            else cur = next;
        }
        if (cur.Length > 0) lines.Add(cur);
        return lines;
    }
    private void Frame(Rect2 r, Color col, float wd) => DrawRect(r, col, false, wd);
    private void Bar(float x, float y, float w, float h, float frac, Color fill)
    {
        DrawRect(new Rect2(x, y, w, h), new Color(0, 0, 0, 0.55f));
        DrawRect(new Rect2(x, y, w * Mathf.Clamp(frac, 0, 1), h), fill);
        Frame(new Rect2(x, y, w, h), new Color(Gold.R, Gold.G, Gold.B, 0.55f), Mathf.Max(1f, U));
    }
    private void Arc(Vector2 c, float r, float frac, Color col, float width)
        => DrawArc(c, r, -Mathf.Pi / 2f, -Mathf.Pi / 2f + Tau * Mathf.Clamp(frac, 0, 1), 32, col, width, true);
    private void Diamond(Vector2 c, float s, Color col)
        => SafePoly(new[] { new Vector2(c.X, c.Y - s), new Vector2(c.X + s, c.Y), new Vector2(c.X, c.Y + s), new Vector2(c.X - s, c.Y) }, col);

    // (CONTINUOUS) difficulty-meter escalation bands: threshold tier → name + colour (green → white-hot). Never caps.
    private static readonly (float t, string name, Color col)[] DiffBands = {
        (0f,  "CALM",        new Color(0.42f, 0.85f, 0.5f)),
        (3f,  "STIRRING",    new Color(0.62f, 0.9f, 0.35f)),
        (6f,  "RESTLESS",    new Color(0.95f, 0.85f, 0.3f)),
        (10f, "MENACING",    new Color(1f, 0.62f, 0.2f)),
        (15f, "FRENZIED",    new Color(1f, 0.36f, 0.15f)),
        (22f, "RUINOUS",     new Color(0.95f, 0.2f, 0.2f)),
        (30f, "CATACLYSMIC", new Color(0.85f, 0.28f, 0.9f)),
        (45f, "APOCALYPSE",  new Color(1f, 0.92f, 1f)),
        (70f, "OBLIVION",    new Color(1f, 1f, 1f)),
    };

    // (NEW) minimap effigy-diamond colour by theme (0 survival / 1 power / 2 fortune / 3 swiftness / 4 coven)
    private static Color EffigyCol(int kind) => kind switch
    {
        0 => new Color(0.45f, 0.9f, 0.5f),    // survival — green
        1 => new Color(1f, 0.42f, 0.36f),     // power — red
        2 => new Color(1f, 0.83f, 0.32f),     // fortune — gold
        3 => new Color(0.45f, 0.85f, 1f),     // swiftness — cyan
        _ => new Color(0.72f, 0.5f, 1f),      // coven — purple
    };

    // DrawColoredPolygon throws "Invalid polygon data, triangulation failed" even on perfectly valid small
    // triangles in this Godot build — the triangulator is simply unreliable. So we NEVER call it: instead we
    // scanline-fill the polygon with horizontal DrawLine spans. Works for any convex shape (all our uses are
    // triangles or quads: popup tails, diamonds, minimap arrows, sheen quads). 100% reliable, no triangulator.
    private void SafePoly(Vector2[] p, Color col)
    {
        if (p == null || p.Length < 3 || col.A <= 0f) return;
        float minY = float.MaxValue, maxY = float.MinValue;
        foreach (var v in p)
        {
            if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || Mathf.Abs(v.X) > 1e6f || Mathf.Abs(v.Y) > 1e6f) return;   // off-screen extremes lose precision
            if (v.Y < minY) minY = v.Y;
            if (v.Y > maxY) maxY = v.Y;
        }
        int y0 = Mathf.FloorToInt(minY), y1 = Mathf.CeilToInt(maxY);
        if (y1 - y0 > 2048 || y1 < y0) return;   // absurd extent guard
        for (int y = y0; y <= y1; y++)
        {
            float sy = y + 0.5f;
            float lx = float.MaxValue, rx = float.MinValue;
            for (int i = 0; i < p.Length; i++)   // find where scanline crosses each edge
            {
                var a = p[i]; var b = p[(i + 1) % p.Length];
                if ((a.Y <= sy && b.Y > sy) || (b.Y <= sy && a.Y > sy))
                {
                    float x = a.X + (sy - a.Y) / (b.Y - a.Y) * (b.X - a.X);
                    if (x < lx) lx = x;
                    if (x > rx) rx = x;
                }
            }
            if (rx > lx) DrawLine(new Vector2(lx, sy), new Vector2(rx, sy), col, 1.0f);
        }
    }

    // dev perf/network overlay (top-left), toggled lobby-wide via the console 'perf' command. Shows frame-time,
    // draw stats, live entity counts, and — on the host — the per-tick snapshot packet sizes vs the ~1392B MTU,
    // so we can see at a glance whether a fight is CPU-bound, GPU-bound, or just saturating the network.
    private void DrawPerf(Font f, float u)
    {
        var g = Game.I; if (g == null) return;
        var white  = new Color(0.82f, 0.88f, 1f);
        var green  = new Color(0.50f, 1f, 0.60f);
        var yellow = new Color(1f, 0.85f, 0.35f);
        var red    = new Color(1f, 0.45f, 0.40f);

        double fps = Engine.GetFramesPerSecond();
        float procMs = (float)Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000f;
        float physMs = (float)Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000f;
        int draws = (int)Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame);
        int nodes = (int)Performance.GetMonitor(Performance.Monitor.ObjectNodeCount);
        float vram = (float)Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / 1048576f;

        var lines = new System.Collections.Generic.List<(string, Color)>();
        lines.Add(($"FPS {fps:0}   frame {procMs:0.0}ms", fps >= 55 ? green : fps >= 30 ? yellow : red));
        lines.Add(($"phys {physMs:0.0}ms   draws {draws}   nodes {nodes}", white));
        lines.Add(($"vram {vram:0}MB", white));
        lines.Add(($"enemies {g.Enemies.Count}   orbs {g.Orbs.Count}", white));

        var net = g.NetMgr;
        if (net != null && net.Active)
        {
            lines.Add(($"net {(g.IsAuthority ? "HOST" : "CLIENT")} @ {Net.NetHz:0}Hz   MTU 1392B", white));
            if (g.IsAuthority)   // only the host builds/sends the snapshots
            {
                lines.Add(($"  enemy pkt {net.NetEnemyBytes}B ({net.NetEnemiesSynced} synced)", net.NetEnemyBytes > 1392 ? red : net.NetEnemyBytes > 1100 ? yellow : green));
                lines.Add(($"  pickup pkt {net.NetPickupBytes}B ({net.NetOrbsSynced} orbs)", net.NetPickupBytes > 1392 ? red : net.NetPickupBytes > 1100 ? yellow : green));
            }
        }
        else lines.Add(("net: solo", white));

        float fs = 15 * u, lh = fs * 1.35f, pad = 8 * u;
        float w = 0f;
        foreach (var (s, _) in lines) w = Mathf.Max(w, f.GetStringSize(s, HorizontalAlignment.Left, -1, Mathf.RoundToInt(fs)).X);
        var org = new Vector2(10 * u, 10 * u);
        DrawRect(new Rect2(org, new Vector2(w + pad * 2, lines.Count * lh + pad * 2)), new Color(0.03f, 0.02f, 0.06f, 0.72f));
        Frame(new Rect2(org, new Vector2(w + pad * 2, lines.Count * lh + pad * 2)), new Color(0.5f, 0.7f, 1f, 0.35f), 1.5f * u);
        float y = org.Y + pad + fs;
        foreach (var (s, col) in lines) { T(f, new Vector2(org.X + pad, y), s, fs, col, HorizontalAlignment.Left, -1, Mathf.RoundToInt(2 * u)); y += lh; }
    }

    // Arcane witch "Conduit" mark glyph — a node with four conduit prongs + a bright core (built from circles/lines; the
    // build's DrawColoredPolygon triangulation is unreliable, so no polygons).
    private void DrawConduit(Vector2 c, float r, Color col)
    {
        DrawCircle(c, r + 2f, new Color(0, 0, 0, 0.5f));                       // dark backing for contrast
        for (int i = 0; i < 4; i++)
        {
            float a = i * Mathf.Pi / 2f + Mathf.Pi / 4f;
            var d = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            DrawLine(c + d * r * 0.7f, c + d * r * 1.9f, new Color(col.R, col.G, col.B, 0.9f), Mathf.Max(1.5f, r * 0.26f));   // conduit prongs
        }
        DrawCircle(c, r, col);
        DrawCircle(c, r * 0.42f, new Color(1f, 0.98f, 1f, 0.95f));            // bright core
    }

    // Divine Intervention glyph — a radiant halo ring with a cross inside + a bright core (cheat-death charge).
    private void DrawHalo(Vector2 c, float r, Color col)
    {
        DrawCircle(c, r + 2.5f, new Color(0, 0, 0, 0.45f));                                  // dark backing for contrast
        float pulse = 0.75f + 0.25f * Mathf.Sin(Time.GetTicksMsec() * 0.005f);
        DrawArc(c, r * 1.35f, 0f, Mathf.Tau, 28, new Color(col.R, col.G, col.B, 0.5f * pulse), Mathf.Max(1f, r * 0.14f));   // outer glow ring
        DrawArc(c, r, 0f, Mathf.Tau, 28, new Color(col.R, col.G, col.B, 0.95f), Mathf.Max(1.5f, r * 0.2f));                // the halo
        var cr = new Color(1f, 0.98f, 0.85f, 0.95f); float a = r * 0.62f, cw = Mathf.Max(1.5f, r * 0.2f);
        DrawLine(c + new Vector2(0, -a), c + new Vector2(0, a * 0.9f), cr, cw);              // cross — vertical
        DrawLine(c + new Vector2(-a * 0.62f, -a * 0.12f), c + new Vector2(a * 0.62f, -a * 0.12f), cr, cw);   // cross — horizontal (high bar)
        DrawCircle(c, r * 0.17f, new Color(1f, 1f, 0.92f, 0.98f));                           // bright core
    }

    // Divine Intervention tracker for the local Divine witch — a row of halo glyphs (cheat-death charges) near the reticle.
    private void DrawInterventionTracker(Vector2 vp, float u, Player p)
    {
        int n = p.Interventions;
        var col = DamageTypes.Col(DamageType.Holy);
        float r = 7f * u, gap = 24f * u;
        float cx = vp.X * 0.5f - (n - 1) * gap * 0.5f, cy = vp.Y * 0.5f - 74f * u;   // (FIX) moved ABOVE the crosshair — was +68u, overlapping the spell-combo/finisher pip row below
        T(_body, new Vector2(0, cy - 24f * u), n == 1 ? "INTERVENTION" : $"INTERVENTIONS  {n}", 11f * u, new Color(col.R, col.G, col.B, 0.8f), HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(1 * u));
        for (int i = 0; i < n; i++) DrawHalo(new Vector2(cx + i * gap, cy), r, col);
    }

    // Conduit tracker for the local Arcane witch — filled/empty node slots below the crosshair (max 4).
    private void DrawConduitTracker(Vector2 vp, float u, Player p)
    {
        int have = p.ArcaneMarkCount;
        if (have <= 0) return;   // (OVERHAUL) marks are uncapped now — nothing to show when none are live
        var col = DamageTypes.Col(DamageType.Arcane);
        float r = 6f * u, gap = 22f * u;
        int shown = Mathf.Min(have, 12);   // cap the ICONS (not the marks) so a huge chain doesn't span the screen
        float cx = vp.X * 0.5f - (shown - 1) * gap * 0.5f, cy = vp.Y * 0.5f + 48f * u;
        T(_body, new Vector2(0, cy - 22f * u), $"CONDUITS  {have}", 11f * u, new Color(col.R, col.G, col.B, 0.8f), HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(1 * u));
        for (int i = 0; i < shown; i++) DrawConduit(new Vector2(cx + i * gap, cy), r, col);
    }

    // TEMP gamepad diagnostic — shows what Godot actually receives from the controller. Toggle with F3 (Game.PadDebug).
    private void DrawPadDebug(Vector2 vp, float u)
    {
        float x = vp.X - 340 * u, y = 96 * u, lh = 19 * u;
        DrawRect(new Rect2(x - 12 * u, y - 22 * u, 344 * u, 232 * u), new Color(0, 0, 0, 0.74f));
        var pads = Input.GetConnectedJoypads();
        void L(string s, Color col) { T(_body, new Vector2(x, y), s, 13 * u, col, HorizontalAlignment.Left, -1, Mathf.RoundToInt(1 * u)); y += lh; }
        L("GAMEPAD DEBUG  (F3 to hide)", Gold);
        L($"Connected joypads: {pads.Count}", pads.Count > 0 ? new Color(0.5f, 1f, 0.5f) : new Color(1f, 0.5f, 0.5f));
        foreach (var id in pads) L($"  #{id}: {Input.GetJoyName(id)}", new Color(0.8f, 0.85f, 1f));
        if (pads.Count == 0) L("  nothing detected (Parsec? focus?)", new Color(1f, 0.72f, 0.4f));
        else
        {
            int d = pads[0];
            L($"L stick: {Input.GetJoyAxis(d, JoyAxis.LeftX):0.00}, {Input.GetJoyAxis(d, JoyAxis.LeftY):0.00}", Colors.White);
            L($"R stick: {Input.GetJoyAxis(d, JoyAxis.RightX):0.00}, {Input.GetJoyAxis(d, JoyAxis.RightY):0.00}", Colors.White);
            L($"LT / RT: {Input.GetJoyAxis(d, JoyAxis.TriggerLeft):0.00} / {Input.GetJoyAxis(d, JoyAxis.TriggerRight):0.00}", Colors.White);
            var btns = new (JoyButton b, string n)[] { (JoyButton.A, "A"), (JoyButton.B, "B"), (JoyButton.X, "X"), (JoyButton.Y, "Y"), (JoyButton.LeftShoulder, "LB"), (JoyButton.RightShoulder, "RB"), (JoyButton.LeftStick, "L3"), (JoyButton.RightStick, "R3"), (JoyButton.Back, "Back"), (JoyButton.Start, "Start"), (JoyButton.DpadUp, "U"), (JoyButton.DpadDown, "D"), (JoyButton.DpadLeft, "L"), (JoyButton.DpadRight, "R") };
            string pressed = "";
            foreach (var (b, n) in btns) if (Input.IsJoyButtonPressed(d, b)) pressed += n + " ";
            L($"Buttons: {(pressed == "" ? "(none)" : pressed)}", new Color(1f, 0.9f, 0.5f));
        }
        L($"PadActive={Game.PadActive}  SpellHeld={Game.PadSpellHeld()}", new Color(0.7f, 1f, 0.9f));
    }

    public override void _Draw()
    {
        EnsureFonts();
        var g = Game.I;
        if (g == null) return;
        float u = U;
        var vp = GetViewportRect().Size;
        Vector2 c = vp / 2f;
        float m = 22 * u;
        var p = g.Player;

        if (Game.PadDebug) DrawPadDebug(vp, u);   // gamepad diagnostic (F3)

        if (g.State == GameState.Lobby) return;
        if (g.State == GameState.CharSelect) { DrawToast(g, vp, u); return; }   // the CharSelect Control node draws the roster now
        if (g.State == GameState.ColliderEdit) { if (g.ColEditor != null) DrawColliderEdit(g, vp, u); return; }   // (DEV) clean authoring stage — no gameplay HUD

        // (NEW) full-screen damage feedback — red vignette on hits, a pulsing alarm while low, a cyan flash when the shield breaks
        if (p != null)
        {
            if (p.HurtFlash > 0.001f) Vignette(vp, new Color(0.92f, 0.06f, 0.09f), p.HurtFlash);
            if (p.LowHp) { float lp = 0.30f + 0.28f * Mathf.Sin((float)Time.GetTicksMsec() * 0.006f); Vignette(vp, new Color(0.85f, 0.04f, 0.06f), lp); }
            if (p.ShieldBreakT > 0.001f) Vignette(vp, new Color(0.42f, 0.78f, 1f), Mathf.Clamp(p.ShieldBreakT / 0.6f, 0f, 1f) * 0.7f);
        }

        // (CONTINUOUS) top-left: the standing nerfer shrine + its escalating soul toll (replaces the old x/3 counter) + the run score
        var curNerf = g.CurrentNerfer;
        var shrCol = curNerf != null && curNerf.State == 0 ? NerfShrine.KindColor(curNerf.Kind).Lerp(Colors.White, 0.35f) : Gold;
        T(_head, new Vector2(m, m + 24 * u), g.NerferHudLine(), 22 * u, shrCol, HorizontalAlignment.Left, -1, Mathf.RoundToInt(3 * u));
        T(_body, new Vector2(m, m + 50 * u), $"{g.Score} score", 15 * u, GoldDim, HorizontalAlignment.Left, -1, Mathf.RoundToInt(2 * u));

        // (CONTINUOUS) DIFFICULTY meter — a climbing tier + escalating name + intensifying colour + a bar filling within the band
        {
            float d = g.Difficulty;
            int bi = 0; for (int i = DiffBands.Length - 1; i >= 0; i--) if (d >= DiffBands[i].t) { bi = i; break; }
            var band = DiffBands[bi];
            float lo = band.t, hi = bi < DiffBands.Length - 1 ? DiffBands[bi + 1].t : lo + 25f;
            float frac = Mathf.Clamp((d - lo) / Mathf.Max(0.01f, hi - lo), 0f, 1f);
            var hc = band.col;
            if (bi >= DiffBands.Length - 3) { float pz = 0.72f + 0.28f * Mathf.Sin((float)Time.GetTicksMsec() * 0.008f); hc = new Color(hc.R * pz + (1 - pz), hc.G * pz, hc.B * pz + (1 - pz) * 0.5f, 1f); }   // top bands seethe
            var ssz = _head.GetStringSize(g.NerferHudLine(), HorizontalAlignment.Left, -1, Mathf.RoundToInt(22 * u));
            float hx = m + ssz.X + 26 * u, hw = 150 * u, hh = 13 * u;
            T(_body, new Vector2(hx, m + 10 * u), $"DIFFICULTY  ·  tier {Mathf.FloorToInt(d)}", 12 * u, GoldDim, HorizontalAlignment.Left, -1, Mathf.RoundToInt(1 * u));
            DrawRect(new Rect2(hx, m + 12 * u, hw, hh), new Color(0, 0, 0, 0.5f));
            DrawRect(new Rect2(hx, m + 12 * u, hw * frac, hh), hc);
            Frame(new Rect2(hx, m + 12 * u, hw, hh), new Color(hc.R, hc.G, hc.B, 0.75f), Mathf.Max(1f, u));
            T(_body, new Vector2(hx + hw + 8 * u, m + 23 * u), band.name, 14 * u, hc, HorizontalAlignment.Left, -1, Mathf.RoundToInt(2 * u));
        }

        if (g.Goblin != null && GodotObject.IsInstanceValid(g.Goblin))
        {
            float pulse = 0.6f + 0.4f * Mathf.Sin(Time.GetTicksMsec() * 0.012f);
            var gc = new Color(1f, 0.84f, 0.3f, pulse);
            string gtxt = g.GoblinTime < 0f ? "\u2726 LOOT GOBLIN  —  strike it to start the chase!" : $"\u2726 LOOT GOBLIN  {Mathf.Max(0f, g.GoblinTime):0.0}s";
            T(_head, new Vector2(0f, m + 24 * u), gtxt, 18 * u, gc, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(3 * u));
        }

        // Gold (persists across runs) + Souls (per-run) \u2014 one row, LEFT of the minimap (souls sits to the RIGHT of gold)
        var goldCol = new Color(1f, 0.82f, 0.32f);
        var soulCol = new Color(0.72f, 0.56f, 1f);
        float curRight = vp.X - 210 * u;                       // right edge of the pair \u2014 clears the top-right minimap (its left edge \u2248 vp.X - 198u after the +25% size bump)
        float soulsW = 100 * u, goldW = 150 * u, curGap = 12 * u;
        float soulsX = curRight - soulsW;
        float goldX = soulsX - curGap - goldW;
        T(_head, new Vector2(goldX, m), $"\u29c9 {g.Gold}", 22 * u, goldCol, HorizontalAlignment.Right, goldW, Mathf.RoundToInt(3 * u));
        T(_head, new Vector2(soulsX, m + 2 * u), $"\u2620 {g.Souls}", 20 * u, soulCol, HorizontalAlignment.Right, soulsW, Mathf.RoundToInt(2 * u));   // (NEW) souls now ride the gold row (was tucked under gold, behind the minimap)
        if (g.GoldFlash > 0f)
            T(_body, new Vector2(goldX, m + 26 * u), $"+{g.LastWaveGold}", 15 * u, new Color(1f, 0.82f, 0.32f, Mathf.Clamp(g.GoldFlash, 0f, 1f)), HorizontalAlignment.Right, goldW, Mathf.RoundToInt(2 * u));

        // Day/night phase + countdown (top center) — hidden inside the maze (the wave clock is paused there)
        if (!g.InMaze)
        {
            var phaseCol = g.IsNight ? new Color(0.6f, 0.65f, 1f) : new Color(1f, 0.85f, 0.6f);
            T(_head, new Vector2(0f, m), $"{g.PhaseName}  ·  {Mathf.CeilToInt(g.PhaseTimeLeft)}s", 16 * u, phaseCol, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(2 * u));
        }
        if (p != null && g.RitualActive) DrawRitual(g, vp, u);
        if (p != null && p.Minors.Count > 0) DrawMinors(p, vp, u, m);

        if (p != null) DrawVitals(p, vp, u, m);
        if (p != null) DrawBuffs(p, vp, u);   // (NEW) always-visible self status chips (Blessed, ...)
        if (p != null && p._snareT > 0f)
        {
            float aa = 0.6f + 0.4f * Mathf.Sin(Time.GetTicksMsec() * 0.012f);
            var nc = DamageTypes.Col(DamageType.Nature);
            T(_head, new Vector2(0, vp.Y * 0.60f), "⚠ ROOTED", 24f * u, new Color(nc.R, nc.G, nc.B, aa), HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(4 * u));
        }
        if (p != null && g.NetMgr != null && g.NetMgr.Active) DrawAllyRoster(g, vp, u);
        if (p != null) DrawDamageDir(p, c, vp, u);
        if (p != null && g.WorldRunning) DrawThreats(p, c, vp, u);   // (NEW) incoming-projectile warnings
        if (p != null)   // (NEW) big legible callouts the moment a layer of protection drops
        {
            if (p.ShieldBreakT > 0.001f)
                T(_head, new Vector2(0, vp.Y * 0.34f), "SHIELD DOWN", 22 * u, new Color(0.62f, 0.86f, 1f, Mathf.Clamp(p.ShieldBreakT / 0.6f, 0f, 1f)), HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(2 * u));
            if (p.ArmorBreakT > 0.001f)
                T(_head, new Vector2(0, vp.Y * 0.38f), "ARMOR BROKEN", 20 * u, new Color(1f, 0.62f, 0.45f, Mathf.Clamp(p.ArmorBreakT / 0.6f, 0f, 1f)), HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(2 * u));
        }
        if (p != null && p.HealFlash > 0f)
        {
            float ha = Mathf.Clamp(p.HealFlash / 0.5f, 0f, 1f) * 0.22f;
            float t = 14 * u; var gc = new Color(0.4f, 1f, 0.55f, ha);
            DrawRect(new Rect2(0, 0, vp.X, t), gc);
            DrawRect(new Rect2(0, vp.Y - t, vp.X, t), gc);
            DrawRect(new Rect2(0, 0, t, vp.Y), gc);
            DrawRect(new Rect2(vp.X - t, 0, t, vp.Y), gc);
        }
        if (p != null) DrawUlt(p, vp, u);
        if (p != null && p.StunT > 0f)
            T(_head, new Vector2(0, vp.Y * 0.4f), "\u26a1 STUNNED", 30 * u, new Color(0.7f, 0.85f, 1f), HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(3 * u));

        var ch = new Color(1f, 0.95f, 0.78f, 0.85f);
        DrawRect(new Rect2(c.X - 1 * u, c.Y - 9 * u, 2 * u, 18 * u), ch);
        DrawRect(new Rect2(c.X - 9 * u, c.Y - 1 * u, 18 * u, 2 * u), ch);

        if (p != null) DrawCombat(p, c, u);
        if (p != null && p.CrimsonWitch) DrawBloodStacks(p, c, vp, u);
        if (p != null && p.VerdantWitch) DrawEntStatus(p, c, vp, u);
        if (p != null) DrawEnemyBars(u);
        if (p != null && p.ArcaneWitch && g.State == GameState.Playing) DrawConduitTracker(vp, u, p);   // (NEW) Arcane conduit-mark tracker under the crosshair
        if (p != null && p.DivineWitch && p.Interventions > 0 && g.State == GameState.Playing) DrawInterventionTracker(vp, u, p);   // (NEW) Divine Intervention charges as halo glyphs near the reticle
        if (p != null && g.State == GameState.Playing) DrawMinimap(g, p, vp, u);
        if (p != null && g.PlayerInHaunt && g.State == GameState.Playing) DrawHaunt(g, vp, u);   // (HAUNT) "in the zone" banner + break meter
        if (p != null) DrawRituals(u);
        if (p != null && g.SummonerActive) DrawSummonerTimer(g, vp, u);   // (NERFER) the Summoning defend-timer
        // (NERFER Sacrifice) sigils → pentagram → silence. Gated on Playing: purging 30 foes almost always pops the level-up
        // pick-3 a beat later, and the tracker was drawing straight through its header.
        if (p != null && g.State == GameState.Playing && (g.RiteOpen || g.RiteDrawing || g.SpawnStalled)) DrawCrimsonRite(g, vp, u);
        if (p != null && g.InIntermission) DrawIntermission(g, vp, u);
        if (p != null && p.Downed) DrawDowned(g, vp, u);
        if (p != null && g.State == GameState.Playing && !g.WorldRunning) DrawWaiting(g, vp, u);
        if (p != null && g.HoldEActive && !g.HoldEIsRitual) DrawHoldE(g, vp, u);   // (NEW) rituals show their hold-E on the world panel (DrawRituals), not center-screen
        else if (p != null && g.HoldEDisabled)   // greyed "can't use yet" note, no progress ring
            T(_body, new Vector2(0, vp.Y * 0.62f), g.HoldEDisabledText, 16 * u, new Color(0.72f, 0.72f, 0.74f, 0.9f), HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(2 * u));

        DrawPops(u);
        DrawFlourish(u);

        if (g.PerfOverlay) DrawPerf(_impact, u);

        if (_bannerT > 0)
        {
            float a = Mathf.Clamp(_bannerT, 0, 1);
            T(_head, new Vector2(0f, vp.Y * 0.26f), _banner, 46 * u, new Color(Gold.R, Gold.G, Gold.B, a), HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(4 * u));
        }

        if (g.State == GameState.LevelUp && g.Choices != null) DrawLevelUp(g, c, vp, u);
        if (g.State == GameState.Attune && p != null) DrawAttune(g, p, vp, u);
        if (g.State == GameState.Swap && p != null) DrawSwap(g, p, c, vp, u);
        if (g.State == GameState.Stats && p != null) DrawStats(g, p, c, vp, u);
        if (g.State == GameState.Element && p != null) DrawElement(g, p, c, vp, u);
        if (g.State == GameState.Ult) DrawUltChoice(g, c, vp, u);
        if (g.State == GameState.UltMenu && p != null) DrawUltMenu(g, p, c, vp, u);
        if (g.State == GameState.Roulette && p != null) DrawRoulette(g, p, c, vp, u);
        if (g.State == GameState.Mystic) DrawMystic(g, vp, u);
        if (g.State == GameState.Scroll && p != null) DrawScroll(g, p, vp, u);
        if (g.State == GameState.Shop && p != null) DrawShop(g, p, vp, u);
        if (g.State == GameState.BindKey && p != null) DrawBindKey(g, p, vp, u);
        if (g.State == GameState.Pause && !g.InGameOptions) DrawPause(g, vp, u);   // options overlay (the full main-menu options panel) hides the pause buttons

        if (g.State == GameState.Over)
        {
            DrawRect(new Rect2(0, 0, vp.X, vp.Y), new Color(0, 0, 0, 0.72f));
            float top = vp.Y * 0.08f;
            T(_head, new Vector2(0f, top), "YOU FELL", 46 * u, new Color(0.95f, 0.4f, 0.45f), HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(4 * u));
            T(_body, new Vector2(0f, top + 52 * u), $"tier {g.Wave}  ·  {g.Score} score  ·  best combo x{p?.BestCombo}", 17 * u, Gold, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(2 * u));

            // ---- scoreboard: one row per warden (solo = one). Kills come from the host's authoritative tally, not the personal block ----
            var pr = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<long, RunStats>>(g.AllStats);
            if (pr.Count == 0) pr.Add(new System.Collections.Generic.KeyValuePair<long, RunStats>(g.LocalPeer, g.MyStats));
            pr.Sort((a, b) => a.Value.Slot.CompareTo(b.Value.Slot));
            float tw = Mathf.Min(vp.X * 0.96f, 1300 * u), tx = (vp.X - tw) / 2f, ty = top + 92 * u;
            string[] heads = { "WARDEN", "KILLS", "DMG", "BOSS", "HEAL", "TAKEN", "FLUNG", "DOWNS", "REVIVES", "COMBO", "BIG HIT", "SIGNATURE" };
            float[] fr = { 0.11f, 0.06f, 0.09f, 0.08f, 0.08f, 0.07f, 0.06f, 0.06f, 0.07f, 0.06f, 0.07f, 0.19f };
            float[] cx = new float[heads.Length]; float acc = 0f;
            for (int i = 0; i < heads.Length; i++) { cx[i] = tx + acc * tw; acc += fr[i]; }
            for (int i = 0; i < heads.Length; i++) T(_body, new Vector2(cx[i], ty), heads[i], 12 * u, GoldDim, HorizontalAlignment.Left, tw * fr[i], 1);
            ty += 22 * u;
            foreach (var kv in pr)
            {
                var s = kv.Value; var wc = WitchModel.WitchColor(s.WitchIdx);
                int kills = g.KillTally.GetValueOrDefault(kv.Key);
                string sig = s.WitchIdx == 0 ? $"Night Kills: {g.NightKillTally.GetValueOrDefault(kv.Key)}" : $"{RunStats.HighlightLabel(s.WitchIdx)}: {s.HighlightValue()}";
                DrawRect(new Rect2(tx - 6 * u, ty - 2 * u, tw + 12 * u, 22 * u), new Color(wc.R, wc.G, wc.B, 0.12f));
                void Cell(int i, string txt, Color col) => T(_body, new Vector2(cx[i], ty), txt, 14 * u, col, HorizontalAlignment.Left, tw * fr[i], 1);
                T(_head, new Vector2(cx[0], ty), RunStats.WitchName(s.WitchIdx), 15 * u, wc, HorizontalAlignment.Left, tw * fr[0], 1);
                Cell(1, $"{kills}", Gold);
                Cell(2, $"{s.DamageDealt:0}", Gold);
                Cell(3, $"{s.BossDamage:0}", Gold);
                Cell(4, $"{s.Healing:0}", new Color(0.6f, 0.95f, 0.7f));
                Cell(5, $"{s.DamageTaken:0}", new Color(0.95f, 0.55f, 0.5f));
                Cell(6, $"{s.Flings}", Gold);
                Cell(7, $"{s.TimesDowned}", GoldDim);
                Cell(8, $"{s.Revives}", new Color(0.6f, 0.95f, 0.7f));
                Cell(9, $"x{s.BestCombo}", Gold);
                Cell(10, $"{s.BiggestHit:0}", Gold);
                T(_body, new Vector2(cx[11], ty), sig, 12 * u, wc.Lerp(Colors.White, 0.25f), HorizontalAlignment.Left, tw * fr[11], 1);
                ty += 24 * u;
            }

            // ---- retry / continue options, below the scoreboard ----
            ROver = RChangeWitch = ROverRetry = ROverCharSelect = ROverEnd = new Rect2();
            bool mp = g.NetMgr != null && g.NetMgr.Active;
            var viol = DamageTypes.Col(DamageType.Lunar);
            float oy = ty + 22 * u, bw = 300 * u, bx = vp.X / 2f - bw / 2f;
            void Opt(ref Rect2 r, string label, Color col, float h)
            {
                r = new Rect2(bx, oy, bw, h); Frame(r, new Color(col.R, col.G, col.B, 0.7f), 1.5f * u);
                T(_body, new Vector2(0f, oy + (h - 16 * u) * 0.5f), label, 15 * u, GoldDim, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(1 * u));
                oy += h + 10 * u;
            }
            if (!mp)
            {
                Opt(ref ROver, "Rise again   [Enter]", new Color(0.95f, 0.4f, 0.45f), 32 * u);
                Opt(ref RChangeWitch, "Change witch   [C]", viol, 30 * u);
            }
            else if (g.NetMgr.IsHost)
            {
                Opt(ref ROverRetry, "Retry — same witches", new Color(0.5f, 0.9f, 0.55f), 34 * u);
                Opt(ref ROverCharSelect, "Character Select", viol, 34 * u);
                Opt(ref ROverEnd, "End Game", new Color(0.95f, 0.4f, 0.45f), 34 * u);
            }
            else
                T(_body, new Vector2(0f, oy + 4 * u), "waiting for the host to decide…", 17 * u, GoldDim, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(1 * u));
        }

        DrawToast(g, vp, u);
    }

    private void DrawToast(Game g, Vector2 vp, float u)
    {
        if (g.ToastT <= 0f || string.IsNullOrEmpty(g.Toast)) return;
        float a = Mathf.Clamp(g.ToastT, 0f, 1f);   // fade out over the last second
        float w = 360 * u, h = 40 * u, x = vp.X / 2f - w / 2f, y = vp.Y * 0.12f;
        DrawRect(new Rect2(x, y, w, h), new Color(0.05f, 0.08f, 0.14f, 0.85f * a));
        Frame(new Rect2(x, y, w, h), new Color(0.55f, 0.8f, 1f, 0.9f * a), 1.5f * u);
        T(_head, new Vector2(0f, y + 11 * u), g.Toast, 16 * u, new Color(0.8f, 0.92f, 1f, a), HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(2 * u));
    }

    public static string UltName(Player.UltKind k) => k switch
    {
        Player.UltKind.Eclipse => "Lunar Eclipse",
        Player.UltKind.LunarLight => "Lunar Light",
        Player.UltKind.Crescent => "Crescent Moon",
        Player.UltKind.FaithShield => "Faith Shield",
        Player.UltKind.Judgement => "Judgement",
        Player.UltKind.Divinity => "Divinity",
        Player.UltKind.BloodTsunami => "Blood Tsunami",
        Player.UltKind.Exsanguinate => "Exsanguinate",
        Player.UltKind.BloodRot => "Blood Rot",
        Player.UltKind.GroveGuardian => "Ancient Guardian",
        Player.UltKind.WildSwarm => "Wild Swarm",
        Player.UltKind.Barkskin => "Barkskin",
        Player.UltKind.Cyclone => "Cyclone",          // (NEW)
        Player.UltKind.Hurricane => "Hurricane",      // (NEW)
        Player.UltKind.Stormform => "Stormform",      // (NEW)
        Player.UltKind.Blizzard => "Blizzard",             // (NEW)
        Player.UltKind.FrostElemental => "Frost Elemental",// (NEW)
        Player.UltKind.DeepFreeze => "Deep Freeze",        // (NEW)
        Player.UltKind.HexCircle => "Hex Circle",          // (NEW)
        Player.UltKind.LifeDrain => "Life Drain",          // (NEW)
        Player.UltKind.LifeCurse => "Life Curse",          // (NEW)
        Player.UltKind.MeteorDescent => "Meteor Descent",  // (NEW)
        Player.UltKind.WildfireRush => "Wildfire Rush",    // (NEW)
        Player.UltKind.PhoenixAscend => "Phoenix Ascendant",// (NEW)
        Player.UltKind.ArcaneAscend => "Arcane Ascension",   // (NEW)
        Player.UltKind.ArcaneEruption => "Arcane Eruption",  // (NEW)
        Player.UltKind.ArcaneOvercharge => "Arcane Storm",     // (REWORK)
        _ => "—"
    };

    private static string UltDesc(Player.UltKind k) => k switch
    {
        Player.UltKind.Eclipse => "Deal double damage for several seconds.",
        Player.UltKind.LunarLight => "Aim a great moonbeam — heals you, scorches foes caught inside.",
        Player.UltKind.Crescent => "Blades orbit and shred on contact. While active: hold [LMB] to drive them forward at your reticle (hold longer to reach farther), hold [RMB] to lock them spinning in place; release to return to orbit.",
        Player.UltKind.FaithShield => "Raise a dome of light. Foes can't enter and must break it; you shoot out freely.",
        Player.UltKind.Judgement => "Lances impale a swath of the field, leaving healing ground where they fall.",
        Player.UltKind.Divinity => "Ascend — invulnerable, raining huge exploding motes with primary fire. Combos persist.",
        Player.UltKind.BloodTsunami => "Surge a wide wave of blood forward — strong damage, knockback, and a slow. Travels far.",
        Player.UltKind.Exsanguinate => "Drain all around you for % damage and execute the weak. A single kill heals you to full.",
        Player.UltKind.BloodRot => "Rot a wide area with bleed. Anything that dies bursts, spreading the rot to others.",
        Player.UltKind.GroveGuardian => "Summon a towering tree-ent that ground-slams several times — devastating at the core, strong at the rim.",
        Player.UltKind.WildSwarm => "Unleash a stampede of tree-ents that charges forward, trampling everything in its path. They can't be hurt or detonated — they just run, chant, and vanish.",
        Player.UltKind.Barkskin => "Bark over: take no damage for a few seconds (you can't detonate ents). When it ends, erupt with thorns + poison spikes.",
        Player.UltKind.Cyclone => "Conjure a tornado at your reticle that drags in and grinds foes for several seconds, then bursts. (Maelstrom: bigger, longer, pulls harder.)",          // (NEW)
        Player.UltKind.Hurricane => "Leap aloft and pilot a hurricane beneath you — steer it across the field to grind enemies and fling them tumbling (big ones resist; they take fall damage on landing). (Eyewall: lasts longer, and allies + their minions caught in it gain cast/charge/move speed.)",   // (NEW)
        Player.UltKind.Stormform => "Become the storm: big move speed and much faster casts for the duration; each kill extends it. (Eye of the Storm: while moving you leave air-mines that launch foes skyward for impact + fall damage.)",                                       // (NEW)
        Player.UltKind.Blizzard => "Call a huge storm at your reticle — swirling snow and falling icicles grind every foe inside, with a chance to freeze them solid. Bigger + stronger + likelier to freeze as you upgrade it (10% → 50%).",   // (NEW)
        Player.UltKind.FrostElemental => "Summon a giant rolling snowball elemental that wanders the field toward the thickest crowds, grinding foes for frost damage and flinging the smaller ones as it rolls through them. Lasts longer at higher tiers; sized by AoE cards.",   // (NEW)
        Player.UltKind.DeepFreeze => "Ice over a large circle at your reticle — every foe inside is frozen solid on cast, and any that wander in during its brief active window freeze too.",   // (NEW)
        Player.UltKind.HexCircle => "Curse the ground around you for ~10s: every foe inside is dragged into one huge shared tether-group and piles on curse stacks, so damage to any of them cascades across the whole crowd. (Plaguebearer: bigger, and the ground festers with a curse DoT.)",   // (NEW)
        Player.UltKind.LifeDrain => "Rise into the air and fly freely (Space up / Ctrl down) while draining every foe in a wide radius — damaging them, healing you, and banking the stolen life. When it ends, detonate for the full banked amount. Fewer foes = you focus more on each. (Rapture: also drags them all toward you.)",   // (NEW)
        Player.UltKind.LifeCurse => "Erupt a curse rune beneath you. Each foe takes a share of its MAX health — the LOWER your current HP, the bigger the share (up to half; bosses capped). A desperation nuke. (Blood Rite: siphons some of the damage back as health.)",   // (NEW)
        Player.UltKind.MeteorDescent => "Rise into the sky invulnerable and aim a landing zone with your reticle (5s, or auto-drop). SLAM down for massive damage — devastating at the core, fading to the rim — brand every foe there with a Living Bomb, and leave a 6s inferno that keeps stacking burn. Radius scales with AoE cards.",   // (NEW)
        Player.UltKind.WildfireRush => "Gain 3 flame dashes (press [Q]) for ~10s. Each dash blazes a long burning trail that stacks burn on foes for 10s — and every point of BURN damage heals you. Allies who run the trail gain move speed + a light heal (not you). Trails sized by AoE cards.",   // (NEW)
        Player.UltKind.PhoenixAscend => "Become a phoenix for ~10s: fly freely (Space up / Ctrl down), an immolation aura torches nearby foes, and your flamethrower turns free & huge. If you'd die during it, you're reborn in a fiery burst instead — once.",   // (NEW)
        Player.UltKind.ArcaneAscend => "A bolt of raw arcane erupts you into the sky (flat-damaging everything ~5m around your launch) — then fly freely for ~10s (Space up / Ctrl down) and rain massive chain-lightning with [LMB] that strikes several foes at once and arcs to their neighbours (can crit). Kills heal you. Upgrades: more damage, more heal per kill, longer.",   // (NEW)
        Player.UltKind.ArcaneEruption => "Release a huge burst of raw arcane around you — heavy damage that's strongest at the center and tapers to the rim (can crit). Survivors are flung back and knocked skyward, harder the closer they were. Bigger with AoE cards + tier.",   // (NEW)
        Player.UltKind.ArcaneOvercharge => "Call down a large arcane storm at your cursor that rains bolts on every foe caught inside it for 13s. Bolts hit tougher foes harder (capped on bosses), can crit, and strike each foe once a second. Everything scales up as you upgrade it.",   // (REWORK)
        _ => ""
    };

    // (REWORK) a row of pips = charges remaining on a Q-charge ult (Wind Rush / Flame Dash), auto-updating as you spend them
    // a slim centered labelled progress bar (used for the rush spend-window + last-dash linger meters)
    private void DrawMiniMeter(Vector2 vp, float u, float by, Color col, string label, float frac)
    {
        float bw = 170 * u, bh = 7 * u, bx = (vp.X - bw) / 2f;
        DrawRect(new Rect2(bx - 1 * u, by - 1 * u, bw + 2 * u, bh + 2 * u), new Color(0, 0, 0, 0.5f));
        DrawRect(new Rect2(bx, by, bw * Mathf.Clamp(frac, 0f, 1f), bh), col);
        Frame(new Rect2(bx, by, bw, bh), new Color(col.R, col.G, col.B, 0.8f), 1.2f * u);
        T(_body, new Vector2(bx, by - 13 * u), label, 10.5f * u, col, HorizontalAlignment.Center, bw, Mathf.RoundToInt(1 * u));
    }

    private void DrawUltCharges(Vector2 vp, float u, float y, string label, int charges, Color col)
    {
        float by = y - 62 * u, r = 6.5f * u, gap = 20 * u;
        int shown = Mathf.Clamp(charges, 0, 12);
        float cx = vp.X * 0.5f - (Mathf.Max(1, shown) - 1) * gap * 0.5f;
        T(_body, new Vector2(0, by - 15 * u), $"{label}  ×{charges}  ·  [Q]", 12 * u, col, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(1 * u));
        for (int i = 0; i < shown; i++)
        {
            var c = new Vector2(cx + i * gap, by);
            DrawCircle(c, r + 1.5f * u, new Color(0, 0, 0, 0.5f));
            DrawCircle(c, r, col);
            DrawCircle(c, r * 0.42f, new Color(1f, 1f, 1f, 0.85f));
        }
    }

    private void DrawUlt(Player p, Vector2 vp, float u)
    {
        if (p.Ult == Player.UltKind.None) return;
        float w = 220 * u, h = 10 * u;
        float x = (vp.X - w) / 2f, y = vp.Y - 30 * u;
        var col = DamageTypes.Col(p.DivineWitch ? DamageType.Holy : DamageType.Lunar);
        DrawRect(new Rect2(x - 1 * u, y - 1 * u, w + 2 * u, h + 2 * u), new Color(0, 0, 0, 0.5f));
        DrawRect(new Rect2(x, y, w * Mathf.Clamp(p.UltCharge, 0f, 1f), h), col);
        Frame(new Rect2(x, y, w, h), new Color(col.R, col.G, col.B, 0.8f), 1.5f * u);
        string tag = p.UltActive ? "  · ACTIVE" : (p.UltCharge >= 1f ? "  · READY [Q]" : "");
        T(_body, new Vector2(x, y - 16 * u), $"{UltName(p.Ult)}  (T{p.UltTier + 1}){tag}", 13 * u, new Color(col.R, col.G, col.B), HorizontalAlignment.Center, w, Mathf.RoundToInt(2 * u));
        // (REMOVED) boss-token readout + [U] altar hint — ults are card-based now; tokens are deprecated

        // (ULT METERS) generic ACTIVE-DURATION bar — shows for any timed ult that doesn't have a bespoke bar below
        // (Stormform / Barkskin / Faith Shield draw their own). Covers Eclipse, Lunar Light, Divinity, Hurricane, etc.
        bool hasBespoke = p.StormActive || p.BarkActive || (Game.I.Shield != null && GodotObject.IsInstanceValid(Game.I.Shield));
        float durNow = Mathf.Max(p.UltActive ? p.UltActiveT : 0f, p.UltLingerT);   // active window OR the lingering fields (Blizzard/Judgement) — whichever is running (field ults keep a 1s UltActive flag alongside a long UltLingerT)
        if (durNow > 0.05f && p.UltMax > 0.1f && !hasBespoke)
        {
            bool ecl = p.Ult == Player.UltKind.Eclipse;
            var dc = ecl ? new Color(0.92f, 0.92f, 1f) : DamageTypes.Col(p.WitchDamage);
            float frac = Mathf.Clamp(durNow / Mathf.Max(0.01f, p.UltMax), 0f, 1f);
            float bw = 200 * u, bh = 9 * u, bx = (vp.X - bw) / 2f, by = y - 60 * u;
            DrawRect(new Rect2(bx - 1 * u, by - 1 * u, bw + 2 * u, bh + 2 * u), new Color(0, 0, 0, ecl ? 0.85f : 0.5f));   // eclipse: darker trough
            DrawRect(new Rect2(bx, by, bw * frac, bh), dc);
            Frame(new Rect2(bx, by, bw, bh), new Color(dc.R, dc.G, dc.B, 0.85f), 1.5f * u);
            string extra = ecl ? "  ·  blink · ×2 spd · +crit" : "";
            T(_body, new Vector2(bx, by - 15 * u), $"{UltName(p.Ult).ToUpper()}  {Mathf.CeilToInt(durNow)}s{extra}", 12 * u, dc, HorizontalAlignment.Center, bw, Mathf.RoundToInt(1 * u));
        }

        // (REWORK) charge readouts — Stormform (Wind Rush) & Wildfire Rush show CHARGES LEFT, plus a spend-window meter
        // and a "last dash still burning" meter (the flame trail / wind area lingers after each dash).
        bool rushWind = p.StormActive;
        bool rushFire = p.Ult == Player.UltKind.WildfireRush && p.UltActive;
        if (rushWind || rushFire)
        {
            var rcol = DamageTypes.Col(rushWind ? DamageType.Wind : DamageType.Ember);
            DrawUltCharges(vp, u, y, rushWind ? "WIND RUSH" : "FLAME DASH", rushWind ? p.WindCharges : p.FlameCharges, rcol);
            // spend-window meter — you must use your charges before this closes
            if (p.RushWindowT > 0.05f)
                DrawMiniMeter(vp, u, y - 46 * u, rcol, $"WINDOW  {Mathf.CeilToInt(p.RushWindowT)}s", p.RushWindowFrac);
            // last-dash lingering field meter — how long the trail/area you just laid still burns
            if (p.RushDashLingerT > 0.05f)
                DrawMiniMeter(vp, u, y - 33 * u, rcol.Lerp(Colors.White, 0.3f), $"LAST DASH  {Mathf.CeilToInt(p.RushDashLingerT)}s", p.RushDashLingerFrac);
        }

        // Barkskin timer (Verdant) — green countdown so the player can read the thorns window; shows on every player's HUD since the ult barks the whole team
        if (p.BarkActive)
        {
            float bw = 200 * u, bh = 9 * u, bx = (vp.X - bw) / 2f, by = y - 60 * u;
            var gcol = DamageTypes.Col(DamageType.Nature);
            DrawRect(new Rect2(bx - 1 * u, by - 1 * u, bw + 2 * u, bh + 2 * u), new Color(0, 0, 0, 0.5f));
            DrawRect(new Rect2(bx, by, bw * p.BarkFrac, bh), gcol);
            Frame(new Rect2(bx, by, bw, bh), new Color(gcol.R, gcol.G, gcol.B, 0.85f), 1.5f * u);
            T(_body, new Vector2(bx, by - 15 * u), $"BARKSKIN  {Mathf.CeilToInt(p.BarkTime)}s", 12 * u, gcol, HorizontalAlignment.Center, bw, Mathf.RoundToInt(1 * u));
        }

        // Faith Shield duration bar (it can't be broken — it just counts down, then shatters)
        var sh = Game.I.Shield;
        if (sh != null && GodotObject.IsInstanceValid(sh))
        {
            float sw = 200 * u, sh2 = 9 * u, sxp = (vp.X - sw) / 2f, syp = y - 44 * u;
            DrawRect(new Rect2(sxp - 1 * u, syp - 1 * u, sw + 2 * u, sh2 + 2 * u), new Color(0, 0, 0, 0.5f));
            DrawRect(new Rect2(sxp, syp, sw * Mathf.Clamp(sh.Dur / Mathf.Max(0.01f, sh.DurMax), 0f, 1f), sh2), col);
            Frame(new Rect2(sxp, syp, sw, sh2), new Color(col.R, col.G, col.B, 0.8f), 1.5f * u);
            T(_body, new Vector2(sxp, syp - 15 * u), $"FAITH SHIELD  {Mathf.CeilToInt(sh.Dur)}s", 12 * u, new Color(col.R, col.G, col.B), HorizontalAlignment.Center, sw, Mathf.RoundToInt(1 * u));
        }

        // Divine passive readout — Intervention pips
        // (Divine Intervention charges now render as drawn halo glyphs near the reticle \u2014 see DrawInterventionTracker.)
    }

    // (NEW) Self status-effect chips — ALWAYS drawn for the local player (unlike DrawUlt, which bails when no ult
    // is chosen). Bless shows here as a proper status effect for whatever witch is blessed; more buffs can join.
    private void DrawBuffs(Player p, Vector2 vp, float u)
    {
        var chips = new System.Collections.Generic.List<(string label, float t, Color col)>();
        if (p.BlessedT > 0f) chips.Add(("\u271d BLESSED", p.BlessedT, DamageTypes.Col(DamageType.Holy)));
        if (p.VenomT > 0f) chips.Add(("\u2620 VENOM", p.VenomT, new Color(0.55f, 0.85f, 0.30f)));   // (NEW) phalanx arrow-poison \u2014 Blessed purges it
        if (p.EmberFervorActive) chips.Add(("EMBER FERVOR", p.EmberFervorT, DamageTypes.Col(DamageType.Ember)));   // (NEW) active fervor buff
        if (chips.Count == 0) return;
        float cw = 132 * u, chh = 26 * u, gap = 8 * u;
        float total = chips.Count * cw + (chips.Count - 1) * gap;
        float sx = vp.X / 2f - total / 2f, y = vp.Y * 0.73f;
        float pulse = 0.7f + 0.3f * Mathf.Sin(Time.GetTicksMsec() * 0.006f);
        for (int i = 0; i < chips.Count; i++)
        {
            var ci = chips[i];
            var r = new Rect2(sx + i * (cw + gap), y, cw, chh);
            DrawRect(r, new Color(ci.col.R, ci.col.G, ci.col.B, 0.22f));
            Frame(r, new Color(ci.col.R, ci.col.G, ci.col.B, 0.85f * pulse), 1.5f * u);
            T(_body, new Vector2(r.Position.X, y + 6 * u), $"{ci.label}  {ci.t:0.0}s", 12 * u, new Color(ci.col.R, ci.col.G, ci.col.B), HorizontalAlignment.Center, cw, Mathf.RoundToInt(1 * u));
        }
    }

    private void DrawUltChoice(Game g, Vector2 c, Vector2 vp, float u)
    {
        DrawRect(new Rect2(0, 0, vp.X, vp.Y), new Color(0, 0, 0, 0.72f));
        var p = g.Player;
        bool divine = p != null && p.DivineWitch;
        var col = DamageTypes.Col(divine ? DamageType.Holy : DamageType.Lunar);
        T(_head, new Vector2(0, vp.Y * 0.15f), "CHOOSE YOUR ULTIMATE", 38 * u, col, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(4 * u));
        T(_body, new Vector2(0, vp.Y * 0.15f + 30 * u),
            divine ? "builds from damaging foes and mending allies" : "builds from damage — lunar damage builds twice as fast at night",
            14 * u, GoldDim, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(2 * u));
        var set = g.UltChoiceSet();
        float cw = Mathf.Min(330 * u, vp.X / 3.4f), gap = 16 * u;
        float total = cw * 3 + gap * 2, sx = (vp.X - total) / 2f, y = vp.Y * 0.34f, ch = 210 * u;
        for (int i = 0; i < 3; i++)
        {
            var r = new Rect2(sx + i * (cw + gap), y, cw, ch);
            RUlt[i] = r;
            DrawRect(r, new Color(0.08f, 0.06f, 0.14f, 0.96f));
            Frame(r, col, 2.5f * u);
            T(_head, new Vector2(r.Position.X, r.Position.Y + 32 * u), $"{i + 1} · {UltName(set[i])}", 18 * u, Gold, HorizontalAlignment.Center, cw, Mathf.RoundToInt(2 * u));
            TM(_body, new Vector2(r.Position.X + 14 * u, r.Position.Y + 66 * u), UltDesc(set[i]), 12.5f * u, GoldDim, cw - 28 * u);
        }
    }

    private void DrawCharSelect(Game g, Vector2 vp, float u)
    {
        DrawRect(new Rect2(0, 0, vp.X, vp.Y), new Color(0.02f, 0.02f, 0.05f, 0.96f));
        var lun = DamageTypes.Col(DamageType.Lunar);
        var hol = DamageTypes.Col(DamageType.Holy);
        var blo = DamageTypes.Col(DamageType.Blood);
        var nat = DamageTypes.Col(DamageType.Nature);
        var wnd = DamageTypes.Col(DamageType.Wind);   // (NEW)
        var frt = DamageTypes.Col(DamageType.Frost);  // (NEW)
        var fsk = DamageTypes.Col(DamageType.Curse);   // (NEW) Forsaken
        T(_head, new Vector2(0, vp.Y * 0.10f), "WARDENS OF THE MOONLIT GROVE", 38 * u, lun, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(4 * u));
        T(_body, new Vector2(0, vp.Y * 0.10f + 42 * u), "choose your witch", 18 * u, GoldDim, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(2 * u));

        // seven cards now — narrower so the Forsaken witch fits on the row (NEW)
        float cw = Mathf.Min(230 * u, vp.X * 0.132f), ch = 300 * u, gap = 12 * u;
        float totalW = cw * 7 + gap * 6;
        float x0 = (vp.X - totalW) / 2f, y0 = vp.Y * 0.30f;

        WitchCard(new Rect2(x0, y0, cw, ch), 0, "The Lunar Witch", lun,
            "Lunar primary & secondary. A blank slate — no combos, no modifiers.\n\nPassive — Nightfall: her Lunar magic hits harder and her ultimate charges faster after dusk.",
            "[1]", u);
        WitchCard(new Rect2(x0 + cw + gap, y0, cw, ch), 1, "The Divine Witch", hol,
            "Holy primary seeks its mark; her charged ray blesses. Holy sears foes & mends allies.\n\nPassive — Divine Intervention: cheats death once every 10 waves, and is gifted a Legendary boon every 10 levels.",
            "[2]", u);
        WitchCard(new Rect2(x0 + (cw + gap) * 2, y0, cw, ch), 2, "The Crimson Blood Witch", blo,
            "Blood lash & a charged spin that flings foes back. A glass cannon — hits hard, dies fast.\n\nPassive — Blood Pact: spells cost HP, not mana. Kills in her Blood Aura heal her and bank Blood Stacks.",
            "[3]", u);
        WitchCard(new Rect2(x0 + (cw + gap) * 3, y0, cw, ch), 3, "The Verdant Witch", nat,
            "Poison-ivy needles that stack venom and slow; a charged thorn spike that pierces everything at full charge and detonates her own tree-ents — and it's those ent blasts that root and poison the foes around them.\n\nPassive — Grove: as her combo climbs she grows tree-ent minions (up to 3+) that hunt poisoned foes.",
            "[4]", u);
        WitchCard(new Rect2(x0 + (cw + gap) * 4, y0, cw, ch), 4, "The Gale Witch", wnd,   // (NEW)
            "Wind primary & charged. Fast twin slashes and a charged gust-cone that hurls foes back. A hit-and-run controller built on knockback and speed.\n\nPassive — Tailwind: faster, with an extra dash and a brief evasive window right after dashing.",
            "[5]", u);
        WitchCard(new Rect2(x0 + (cw + gap) * 5, y0, cw, ch), 5, "The Frost Witch", frt,   // (NEW)
            "A sniper. A long-range freezing beam that stacks frost & slows, and a charged icicle spear that pierces 3 (full charge: double crit chance, or SHATTERS a frozen foe).\n\nPassive — Deep Freeze: stacking frost from the beam encases foes in ice; shatter the block to finish them — small chunks on the healthy, an execute on the weak.",
            "[6]", u);
        WitchCard(new Rect2(x0 + (cw + gap) * 6, y0, cw, ch), 6, "The Forsaken Witch", fsk,   // (NEW)
            "A curse controller. A lock-on suck-beam that latches the foe nearest your reticle, building curse and spreading it to nearby enemies — tethering them into a group that SHARES a cut of every hit any of them takes (from anyone). Cursed foes take extra Curse damage.\n\nSecondary — Voodoo Crush: clasp your hand on a cursed foe to consume its curse stacks (by charge) and detonate them.",
            "[7]", u);

        T(_body, new Vector2(0, vp.Y * 0.88f), "press a number or click a card to begin", 16 * u, Gold, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(2 * u));
    }

    private void WitchCard(Rect2 r, int idx, string name, Color col, string desc, string key, float u)
    {
        RWitch[idx] = r;
        DrawRect(r, new Color(0.08f, 0.06f, 0.14f, 0.98f));
        Frame(r, col, 2.5f * u);
        T(_head, new Vector2(r.Position.X, r.Position.Y + 28 * u), name, 21 * u, Gold, HorizontalAlignment.Center, r.Size.X, Mathf.RoundToInt(3 * u));
        // emblem
        if (idx == 0)
            for (int i = 0; i < 9; i++)
            {
                float t = i / 8f, a = Mathf.DegToRad(-105f + 210f * t);
                float rad = (5f + 12f * Mathf.Sin(t * Mathf.Pi)) * u;
                DrawCircle(new Vector2(r.Position.X + r.Size.X / 2f + Mathf.Cos(a) * 34 * u, r.Position.Y + 92 * u + Mathf.Sin(a) * 34 * u), rad, col);
            }
        else
        {
            var ctr = new Vector2(r.Position.X + r.Size.X / 2f, r.Position.Y + 92 * u);
            for (int i = 0; i < 8; i++)
            {
                float a = Mathf.DegToRad(i * 45f);
                DrawLine(ctr, ctr + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * 30 * u, col, 2.5f * u);
            }
            DrawCircle(ctr, 9 * u, col);
        }
        TM(_body, new Vector2(r.Position.X + 16 * u, r.Position.Y + 128 * u), desc, 12.5f * u, GoldDim, r.Size.X - 32 * u);
        T(_head, new Vector2(r.Position.X, r.Position.Y + r.Size.Y - 30 * u), key, 18 * u, col, HorizontalAlignment.Center, r.Size.X, Mathf.RoundToInt(2 * u));
    }

    private void DrawEntStatus(Player p, Vector2 c, Vector2 vp, float u)
    {
        var col = DamageTypes.Col(DamageType.Nature);
        int n = p.CountEnts(), max = p.MaxEnts;
        float pip = 11 * u, gap = 5 * u;
        float totalW = max * pip + (max - 1) * gap;
        float x0 = c.X - totalW / 2f, y = c.Y + 122 * u;
        for (int i = 0; i < max; i++)   // one leaf pip per ent slot
        {
            var r = new Rect2(x0 + i * (pip + gap), y, pip, pip * 1.4f);
            bool on = i < n;
            DrawRect(r, new Color(col.R, col.G, col.B, on ? 0.9f : 0.16f));
            Frame(r, new Color(col.R, col.G, col.B, on ? 1f : 0.4f), 1.5f * u);
        }
        // progress meter toward the next ent (combo-gated). Full bar = MAX reached.
        bool atMax = n >= max;
        float barW = totalW, barH = 5 * u, by = y + pip * 1.4f + 4 * u;
        var track = new Rect2(c.X - barW / 2f, by, barW, barH);
        DrawRect(track, new Color(col.R, col.G, col.B, 0.16f));
        float fill = atMax ? 1f : p.EntProgress;
        DrawRect(new Rect2(track.Position.X, by, barW * fill, barH), new Color(col.R, col.G, col.B, 0.85f));
        Frame(track, new Color(col.R, col.G, col.B, 0.5f), 1f * u);
        string label = atMax ? $"GROVE {n}/{max}  \u2022  MAX" : $"GROVE {n}/{max}  \u2022  next";
        T(_body, new Vector2(0f, by + barH + 15 * u), label, 11 * u, new Color(col.R, col.G, col.B, 0.9f), HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(1 * u));
    }

    private void DrawBloodStacks(Player p, Vector2 c, Vector2 vp, float u)
    {
        var col = DamageTypes.Col(DamageType.Blood);
        int max = Player.MaxBloodStacks, n = p.BloodStacks;
        float pip = 9 * u, gap = 4 * u;
        float totalW = max * pip + (max - 1) * gap;
        float x0 = c.X - totalW / 2f, y = c.Y + 122 * u;
        for (int i = 0; i < max; i++)
        {
            var r = new Rect2(x0 + i * (pip + gap), y, pip, pip * 1.6f);
            bool on = i < n;
            DrawRect(r, new Color(col.R, col.G, col.B, on ? 0.9f : 0.18f));
            Frame(r, new Color(col.R, col.G, col.B, on ? 1f : 0.4f), 1.5f * u);
        }
        T(_body, new Vector2(0f, y + pip * 1.6f + 3 * u), $"BLOOD \u00d7{n}", 11 * u, new Color(col.R, col.G, col.B, 0.9f), HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(1 * u));
    }

    private void DrawMystic(Game g, Vector2 vp, float u)
    {
        DrawRect(new Rect2(0, 0, vp.X, vp.Y), new Color(0.03f, 0.02f, 0.06f, 0.88f));
        var arc = DamageTypes.Col(DamageType.Arcane);
        T(_head, new Vector2(0, vp.Y * 0.22f), "THE MYSTIC", 34 * u, arc, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(4 * u));
        T(_body, new Vector2(0, vp.Y * 0.22f + 34 * u), "\u201cI can re-weave your attunements\u2026 for a price.\u201d", 15 * u, GoldDim, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(2 * u));
        var goldCol = new Color(1f, 0.82f, 0.34f);
        bool afford = g.Gold >= Game.MysticCost;
        T(_head, new Vector2(0, vp.Y * 0.22f + 60 * u), $"\u29c9 {g.Gold} gold", 18 * u, goldCol, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(2 * u));

        float bw = 360 * u, bh = 56 * u, x0 = vp.X / 2f - bw / 2f, y = vp.Y * 0.40f;
        var p = g.Player;
        string[] labels = {
            $"[1]  Re-attune LEFT-click  (now: {(p != null ? DamageTypes.Name(p.PrimaryType) : "?")})   \u2014  {Game.MysticCost}g",
            $"[2]  Re-attune RIGHT-click  (now: {(p != null ? DamageTypes.Name(p.SecondaryType) : "?")})   \u2014  {Game.MysticCost}g",
            "[3]  Leave"
        };
        for (int i = 0; i < 3; i++)
        {
            var r = new Rect2(x0, y + i * (bh + 12 * u), bw, bh);
            RMystic[i] = r;
            bool ok = i == 2 || afford;
            var cc = i == 2 ? GoldDim : (ok ? arc : new Color(0.5f, 0.4f, 0.4f));
            DrawRect(r, new Color(cc.R, cc.G, cc.B, 0.16f));
            Frame(r, cc, 2f * u);
            T(_body, new Vector2(r.Position.X, r.Position.Y + bh / 2f - 2 * u), labels[i], 15 * u, ok ? Gold : new Color(0.7f, 0.6f, 0.6f), HorizontalAlignment.Center, bw, Mathf.RoundToInt(2 * u));
        }
        if (!afford) T(_body, new Vector2(0, y + 3 * (bh + 12 * u)), "not enough gold", 13 * u, new Color(1f, 0.5f, 0.5f), HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(1 * u));
    }

    private void DrawScroll(Game g, Player p, Vector2 vp, float u)
    {
        DrawRect(new Rect2(0, 0, vp.X, vp.Y), new Color(0.02f, 0.04f, 0.03f, 0.90f));
        var nat = DamageTypes.Col(DamageType.Nature);
        T(_head, new Vector2(0, vp.Y * 0.12f), "THE SCROLL-KEEPER", 32 * u, nat, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(4 * u));
        T(_body, new Vector2(0, vp.Y * 0.12f + 32 * u), "take a spell you don't yet carry (full slots will prompt a swap)", 14 * u, GoldDim, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(2 * u));

        int nf = g.ScrollFins.Count, total = nf + g.ScrollMods.Count;
        for (int i = 0; i < RScroll.Length; i++) RScroll[i] = new Rect2(-1, -1, 0, 0);
        string ttTitle = null, ttBody = null; Color ttCol = Gold;
        if (total == 0)
            T(_body, new Vector2(0, vp.Y * 0.4f), "you already carry everything he has", 16 * u, Gold, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(2 * u));

        float cw = 210 * u, ch = 92 * u, gx = 18 * u, gy = 14 * u;
        int cols = Mathf.Min(4, Mathf.Max(1, total));
        int rows = (total + cols - 1) / Mathf.Max(1, cols);
        float totalW = cols * cw + (cols - 1) * gx, x0 = vp.X / 2f - totalW / 2f, y0 = vp.Y * 0.26f;
        for (int i = 0; i < total && i < RScroll.Length; i++)
        {
            int col = i % cols, row = i / cols;
            var r = new Rect2(x0 + col * (cw + gx), y0 + row * (ch + gy), cw, ch);
            RScroll[i] = r;
            bool isFin = i < nf;
            Color tc = isFin ? FinMeta.Col(g.ScrollFins[i]) : ModMeta.Col(g.ScrollMods[i - nf]);
            string name = isFin ? FinMeta.Name(g.ScrollFins[i]) : ModMeta.Name(g.ScrollMods[i - nf]);
            string tag = isFin ? ("SPELL \u00b7 " + DamageTypes.Name(FinMeta.DType(g.ScrollFins[i]))) : ("MOD \u00b7 " + DamageTypes.Name(ModMeta.DType(g.ScrollMods[i - nf])));
            bool hover = r.HasPoint(GetGlobalMousePosition());
            DrawRect(r, new Color(Panel.R, Panel.G, Panel.B, 0.96f));
            DrawRect(r, new Color(tc.R, tc.G, tc.B, hover ? 0.28f : 0.15f));
            Frame(r, hover ? Gold : tc, (hover ? 3.5f : 2f) * u);
            T(_body, new Vector2(r.Position.X + 8 * u, r.Position.Y + 14 * u), $"[{i + 1}]", 12 * u, GoldDim, HorizontalAlignment.Left, -1, Mathf.RoundToInt(1 * u));
            T(_head, new Vector2(r.Position.X, r.Position.Y + 40 * u), name, 16 * u, Gold, HorizontalAlignment.Center, cw, Mathf.RoundToInt(2 * u));
            T(_body, new Vector2(r.Position.X, r.Position.Y + 66 * u), tag, 11 * u, tc, HorizontalAlignment.Center, cw, Mathf.RoundToInt(1 * u));
            if (hover) { ttTitle = name; ttBody = isFin ? FinMeta.Desc(g.ScrollFins[i]) : ModMeta.Desc(g.ScrollMods[i - nf]); ttCol = tc; }   // (NEW) hover description
        }
        RScrollClose = new Rect2(vp.X / 2f - 90 * u, vp.Y * 0.82f, 180 * u, 30 * u);
        Frame(RScrollClose, new Color(Gold.R, Gold.G, Gold.B, 0.5f), 1.5f * u);
        T(_body, new Vector2(RScrollClose.Position.X, RScrollClose.Position.Y + 8 * u), "leave  [Esc]", 14 * u, GoldDim, HorizontalAlignment.Center, RScrollClose.Size.X, Mathf.RoundToInt(1 * u));
        if (ttTitle != null) DrawTooltip(GetGlobalMousePosition(), vp, ttTitle, ttBody, ttCol, u);   // (NEW) hover description popup
    }

    // the peddler shop — 3 columns (boons / finishers / modifiers), 4 cards each, priced in gold; click to buy.
    private void DrawShop(Game g, Player p, Vector2 vp, float u)
    {
        DrawRect(new Rect2(0, 0, vp.X, vp.Y), new Color(0.03f, 0.02f, 0.01f, 0.92f));
        var gold = new Color(1f, 0.82f, 0.34f);
        T(_head, new Vector2(0, vp.Y * 0.055f), "THE PEDDLER", 32 * u, gold, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(4 * u));
        T(_body, new Vector2(0, vp.Y * 0.055f + 34 * u), $"gold: {g.Gold}", 16 * u, gold, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(2 * u));

        string[] heads = { "BOONS", "FINISHERS", "MODIFIERS" };
        for (int i = 0; i < RShop.Length; i++) RShop[i] = new Rect2(-1, -1, 0, 0);
        string ttTitle = null, ttBody = null; Color ttCol = gold;

        float cw = 230 * u, chh = 74 * u, gx = 26 * u, gyy = 12 * u;
        float totalW = 3 * cw + 2 * gx, x0 = vp.X / 2f - totalW / 2f, y0 = vp.Y * 0.22f;
        for (int sec = 0; sec < 3; sec++)
        {
            float cx = x0 + sec * (cw + gx);
            T(_head, new Vector2(cx, y0 - 28 * u), heads[sec], 15 * u, gold, HorizontalAlignment.Center, cw, Mathf.RoundToInt(2 * u));
            for (int row = 0; row < 4; row++)
            {
                int idx = sec * 4 + row;
                if (idx >= g.ShopCards.Count) continue;
                var card = g.ShopCards[idx];
                var r = new Rect2(cx, y0 + row * (chh + gyy), cw, chh);
                RShop[idx] = r;
                bool sold = g.ShopSold[idx];
                bool empty = card == null;
                Color rc = empty ? new Color(0.4f, 0.4f, 0.4f) : Rarities.Col(card.Rarity);
                bool hover = !sold && !empty && r.HasPoint(GetGlobalMousePosition());
                DrawRect(r, new Color(Panel.R, Panel.G, Panel.B, 0.96f));
                DrawRect(r, new Color(rc.R, rc.G, rc.B, hover ? 0.28f : 0.13f));
                Frame(r, hover ? gold : rc, (hover ? 3.5f : 2f) * u);
                if (empty || sold)
                {
                    DrawRect(r, new Color(0f, 0f, 0f, 0.55f));
                    T(_head, new Vector2(cx, r.Position.Y + chh / 2f - 9 * u), empty ? "—" : "SOLD", 15 * u, GoldDim, HorizontalAlignment.Center, cw, Mathf.RoundToInt(1 * u));
                    continue;
                }
                int price = g.ShopPrices[idx];
                bool afford = g.Gold >= price;
                T(_head, new Vector2(cx, r.Position.Y + 12 * u), card.Title, 15 * u, Gold, HorizontalAlignment.Center, cw, Mathf.RoundToInt(2 * u));
                T(_body, new Vector2(cx, r.Position.Y + 36 * u), Rarities.Name(card.Rarity), 11 * u, rc, HorizontalAlignment.Center, cw, Mathf.RoundToInt(1 * u));
                T(_body, new Vector2(cx, r.Position.Y + 52 * u), $"{price} g", 14 * u, afford ? gold : new Color(0.82f, 0.35f, 0.30f), HorizontalAlignment.Center, cw, Mathf.RoundToInt(1 * u));
                if (hover) { ttTitle = card.Title; ttBody = card.Desc; ttCol = rc; }
            }
        }
        RShopClose = new Rect2(vp.X / 2f - 90 * u, vp.Y * 0.90f, 180 * u, 30 * u);
        Frame(RShopClose, new Color(gold.R, gold.G, gold.B, 0.5f), 1.5f * u);
        T(_body, new Vector2(RShopClose.Position.X, RShopClose.Position.Y + 8 * u), "leave  [Esc]", 14 * u, GoldDim, HorizontalAlignment.Center, RShopClose.Size.X, Mathf.RoundToInt(1 * u));
        if (ttTitle != null) DrawTooltip(GetGlobalMousePosition(), vp, ttTitle, ttBody, ttCol, u);
    }

    private void DrawMinors(Player p, Vector2 vp, float u, float m)
    {
        int n = p.Minors.Count;
        float cw = 58 * u, chh = 22 * u, gap = 6 * u;
        int perRow = Mathf.Max(1, Mathf.FloorToInt((vp.X * 0.7f) / (cw + gap)));
        float y0 = m + 26 * u;
        for (int i = 0; i < n; i++)
        {
            int col = i % perRow, row = i / perRow;
            float rowCount = Mathf.Min(perRow, n - row * perRow);
            float rowW = rowCount * cw + (rowCount - 1) * gap;
            float x0 = vp.X / 2f - rowW / 2f;
            var ms = p.Minors[i];
            var c = MinorMeta.Col(ms.Type);
            var r = new Rect2(x0 + col * (cw + gap), y0 + row * (chh + 4 * u), cw, chh);
            DrawRect(r, new Color(0.05f, 0.04f, 0.08f, 0.7f));
            float frac = Mathf.Clamp(ms.Charge / (float)ms.Every, 0f, 1f);
            DrawRect(new Rect2(r.Position.X, r.Position.Y, cw * frac, chh), new Color(c.R, c.G, c.B, 0.5f));
            Frame(r, new Color(c.R, c.G, c.B, 0.85f), 1f * u);
            string ab = MinorMeta.Name(ms.Type);
            if (ab.Length > 7) ab = ab.Substring(0, 7);
            T(_body, new Vector2(r.Position.X + 3 * u, r.Position.Y + 5 * u), ab, 9 * u, new Color(0.92f, 0.9f, 0.96f), HorizontalAlignment.Left, cw - 6 * u, 0);
            if (ms.Stacks > 1) T(_body, new Vector2(r.Position.X, r.Position.Y + 5 * u), $"x{ms.Stacks}", 9 * u, c, HorizontalAlignment.Right, cw - 4 * u, 0);
        }
    }

    private void DrawHoldE(Game g, Vector2 vp, float u)
    {
        float frac = g.HoldEFrac;
        float y = vp.Y * 0.62f;
        T(_body, new Vector2(0, y), g.HoldEPrompt, 17 * u, new Color(0.95f, 0.92f, 0.8f), HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(2 * u));
        float bw = 180 * u, bh = 9 * u, bx = vp.X / 2f - bw / 2f, by = y + 26 * u;
        DrawRect(new Rect2(bx - 1 * u, by - 1 * u, bw + 2 * u, bh + 2 * u), new Color(0, 0, 0, 0.55f));
        if (frac > 0f) DrawRect(new Rect2(bx, by, bw * frac, bh), Gold);
        Frame(new Rect2(bx, by, bw, bh), new Color(Gold.R, Gold.G, Gold.B, 0.7f), 1f * u);
    }

    private void DrawDowned(Game g, Vector2 vp, float u)
    {
        DrawRect(new Rect2(0, 0, vp.X, vp.Y), new Color(0.35f, 0.02f, 0.04f, 0.28f));
        float w = 520 * u, h = 70 * u, x = vp.X / 2f - w / 2f, y = vp.Y * 0.40f;
        DrawRect(new Rect2(x, y, w, h), new Color(0.10f, 0.02f, 0.03f, 0.82f));
        Frame(new Rect2(x, y, w, h), new Color(1f, 0.45f, 0.45f, 0.9f), 1.5f * u);
        T(_head, new Vector2(0, y + 12 * u), "YOU ARE DOWNED", 22 * u, new Color(1f, 0.6f, 0.6f), HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(2 * u));
        T(_body, new Vector2(0, y + 42 * u), "hold on — a Warden can revive you", 13 * u, GoldDim, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(1 * u));
    }

    private void DrawWaiting(Game g, Vector2 vp, float u)
    {
        float w = 460 * u, h = 64 * u, x = vp.X / 2f - w / 2f, y = vp.Y * 0.42f;
        DrawRect(new Rect2(x, y, w, h), new Color(0.03f, 0.04f, 0.08f, 0.78f));
        Frame(new Rect2(x, y, w, h), new Color(0.7f, 0.85f, 1f, 0.85f), 1.5f * u);
        T(_head, new Vector2(0, y + 12 * u), "WAITING FOR OTHER WARDENS", 20 * u, new Color(0.85f, 0.92f, 1f), HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(2 * u));
        T(_body, new Vector2(0, y + 38 * u), "they're still choosing an upgrade — your combo is safe", 13 * u, GoldDim, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(1 * u));
    }

    private void DrawIntermission(Game g, Vector2 vp, float u)
    {
        var col = DamageTypes.Col(DamageType.Lunar);
        float y = vp.Y * 0.16f;
        T(_head, new Vector2(0, y), $"NEXT WAVE IN  {Mathf.CeilToInt(g.WaveGap)}s", 26 * u, col, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(3 * u));
        // (the threat readout now lives permanently in the top-left THREAT meter — no centered "the grove · …" line here)
        float sh = g.SkipHoldFrac;
        if (sh > 0f)
        {
            float bw = 240 * u, bh = 12 * u, bx = vp.X / 2f - bw / 2f, by = y + 36 * u;
            DrawRect(new Rect2(bx - 1 * u, by - 1 * u, bw + 2 * u, bh + 2 * u), new Color(0, 0, 0, 0.5f));
            DrawRect(new Rect2(bx, by, bw * sh, bh), new Color(1f, 0.4f, 0.45f));
            T(_body, new Vector2(0, by + bh + 4 * u), g.SkipNeeded > 1 ? $"voted  ({g.SkipVotes}/{g.SkipNeeded})" : "skipping\u2026", 13 * u, new Color(1f, 0.6f, 0.6f), HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(1 * u));
        }
        else
        {
            T(_body, new Vector2(0, y + 30 * u), g.SkipNeeded > 1 ? $"hold  [Backspace]  to vote skip   ({g.SkipVotes}/{g.SkipNeeded})" : "hold  [Backspace]  to skip", 14 * u, GoldDim, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(2 * u));
        }
    }

    private void DrawBindKey(Game g, Player p, Vector2 vp, float u)
    {
        DrawRect(new Rect2(0, 0, vp.X, vp.Y), new Color(0.02f, 0.02f, 0.05f, 0.86f));
        int idx = g.BindIdx;
        string nm = (idx >= 0 && idx < p.Fin.Count) ? FinMeta.Name(p.Fin[idx].Type) : "spell combo";
        var fc = (idx >= 0 && idx < p.Fin.Count) ? FinMeta.Col(p.Fin[idx].Type) : Gold;
        T(_head, new Vector2(0, vp.Y * 0.40f), "BIND A KEY", 34 * u, fc, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(4 * u));
        T(_body, new Vector2(0, vp.Y * 0.40f + 38 * u), $"press any key to fire  {nm}", 16 * u, Gold, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(2 * u));
        T(_body, new Vector2(0, vp.Y * 0.40f + 64 * u), "(Esc keeps the current key)", 13 * u, GoldDim, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(1 * u));
        T(_body, new Vector2(0, vp.Y * 0.40f + 84 * u), "movement, dash, jump, interact & menu keys are reserved", 11 * u, new Color(GoldDim.R, GoldDim.G, GoldDim.B, 0.7f), HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(1 * u));
    }

    private void DrawPause(Game g, Vector2 vp, float u)
    {
        DrawRect(new Rect2(0, 0, vp.X, vp.Y), new Color(0, 0, 0, 0.78f));
        var col = DamageTypes.Col(DamageType.Lunar);
        T(_head, new Vector2(0, vp.Y * 0.16f), "PAUSED", 46 * u, col, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(4 * u));

        // Three run options — Options / Quit Run / Restart Run. Restart is host-or-solo only (a MP client can't restart the shared run).
        bool canRestart = g.CanRestartRun();
        float bw = Mathf.Min(300 * u, vp.X * 0.6f), bh = 46 * u, bx = (vp.X - bw) / 2f;
        float gap = 14 * u, y0 = vp.Y * 0.34f;
        Vector2 mouse = GetGlobalMousePosition();
        Rect2 Btn(float y, string label, Color accent, bool enabled)
        {
            var r = new Rect2(bx, y, bw, bh);
            bool hov = enabled && r.HasPoint(mouse);
            DrawRect(r, enabled ? new Color(accent.R, accent.G, accent.B, hov ? 0.32f : 0.16f) : new Color(0, 0, 0, 0.35f));
            Frame(r, enabled ? (hov ? Gold : accent) : new Color(accent.R, accent.G, accent.B, 0.25f), 1.5f * u);
            T(_head, new Vector2(0, y + bh * 0.5f - 13 * u), label, 20 * u, enabled ? (hov ? Colors.White : Gold) : new Color(0.5f, 0.5f, 0.55f), HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(2 * u));
            return r;
        }
        RPauseOptions = Btn(y0, "Options", col, true);
        RPauseQuit = Btn(y0 + (bh + gap), "Quit Run", new Color(0.9f, 0.5f, 0.55f), true);
        RPauseRestart = Btn(y0 + 2 * (bh + gap), "Restart Run", new Color(0.6f, 0.9f, 0.6f), canRestart);
        if (!canRestart)
            T(_body, new Vector2(0, y0 + 2 * (bh + gap) + bh + 3 * u), "only the host can restart", 12 * u, GoldDim, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(1 * u));

        // Spell Combo Keys rebinder — preserved from the old pause menu (click a combo to rebind its key).
        float fy = vp.Y * 0.72f;
        for (int i = 0; i < RPauseBind.Length; i++) RPauseBind[i] = new Rect2(-1, -1, 0, 0);
        var pp = g.Player;
        int fn = pp != null ? pp.Fin.Count : 0;
        if (fn > 0)
        {
            T(_body, new Vector2(0, fy - 20 * u), "Spell Combo Keys  (click to rebind)", 15 * u, GoldDim, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(2 * u));
            float bwid = 132 * u, bhi = 30 * u, bgap = 10 * u;
            float rowW = fn * bwid + (fn - 1) * bgap, sx = vp.X / 2f - rowW / 2f;
            for (int i = 0; i < fn && i < RPauseBind.Length; i++)
            {
                var fr = new Rect2(sx + i * (bwid + bgap), fy, bwid, bhi);
                RPauseBind[i] = fr;
                var fc = FinMeta.Col(pp.Fin[i].Type);
                bool hov = fr.HasPoint(mouse);
                DrawRect(fr, new Color(fc.R, fc.G, fc.B, hov ? 0.32f : 0.16f));
                Frame(fr, hov ? Gold : fc, 1.5f * u);
                string nm = FinMeta.Name(pp.Fin[i].Type);
                if (nm.Length > 11) nm = nm.Substring(0, 11);
                T(_body, new Vector2(fr.Position.X + 6 * u, fr.Position.Y + 9 * u), nm, 11 * u, Gold, HorizontalAlignment.Left, bwid - 40 * u, Mathf.RoundToInt(1 * u));
                T(_body, new Vector2(fr.Position.X, fr.Position.Y + 8 * u), $"[{KeyName(pp.Fin[i].Bind)}]", 13 * u, fc, HorizontalAlignment.Right, bwid - 8 * u, Mathf.RoundToInt(1 * u));
            }
        }

        float ry = vp.Y * 0.84f;
        RPauseResume = new Rect2(vp.X / 2f - 110 * u, ry - 4 * u, 220 * u, 34 * u);
        Frame(RPauseResume, new Color(col.R, col.G, col.B, 0.6f), 1.5f * u);
        T(_body, new Vector2(0, ry), "Resume   [Esc]", 18 * u, Gold, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(2 * u));
    }

    private void DrawColliderEdit(Game g, Vector2 vp, float u)
    {
        var ed = g.ColEditor;
        // left info panel (kept off-center so the 3D authoring view stays clear)
        float px = 16 * u, pw = 340 * u, py = 70 * u;
        DrawRect(new Rect2(px - 8 * u, py - 34 * u, pw, 250 * u), new Color(0, 0, 0, 0.55f));
        var accent = DamageTypes.Col(DamageType.Lunar);
        T(_head, new Vector2(px, py - 30 * u), "COLLIDER EDITOR", 22 * u, accent, HorizontalAlignment.Left, pw, Mathf.RoundToInt(2 * u));
        T(_body, new Vector2(px, py), $"near: {ed.NearestModelName()}     colliders: {ed.SelectedCount}", 14 * u, Gold, HorizontalAlignment.Left, pw, 0);
        var modeCol = ed.Mode == ColliderEditor.XMode.Move ? new Color(0.5f, 0.85f, 1f) : ed.Mode == ColliderEditor.XMode.Rotate ? new Color(1f, 0.75f, 0.4f) : new Color(0.6f, 1f, 0.6f);
        T(_head, new Vector2(px + 200 * u, py - 30 * u), ed.ModeName, 16 * u, modeCol, HorizontalAlignment.Left, 140 * u, Mathf.RoundToInt(1 * u));
        // selected collider readout (3 lines)
        string[] lines = ed.SelInfo().Split('\n');
        for (int i = 0; i < lines.Length; i++)
            T(_body, new Vector2(px, py + (24 + i * 18) * u), lines[i], 13 * u, GoldDim, HorizontalAlignment.Left, pw, 0);
        // controls legend
        string[] help = {
            "WASD + mouse = fly  ·  Space/Ctrl = up/down  ·  Shift = fast",
            "M = new  ·  Tab / [ ] = select  ·  X = delete  ·  Enter = dup",
            "G / R / T  =  MOVE / ROTATE / SCALE mode",
            "arrows = X/Z  ·  Q/E = Y  ·  (rotate: ←/→)  ·  Shift = fine",
            "C = color  ·  V = shape  ·  K = SAVE  ·  Esc = exit",
        };
        for (int i = 0; i < help.Length; i++)
            T(_body, new Vector2(px, py + (92 + i * 16) * u), help[i], 12 * u, new Color(0.7f, 0.7f, 0.8f), HorizontalAlignment.Left, pw, 0);
        if (!string.IsNullOrEmpty(ed.Status))
            T(_body, new Vector2(px, py + 180 * u), ed.Status, 13 * u, new Color(0.6f, 0.95f, 0.6f), HorizontalAlignment.Left, pw, 0);

        // center crosshair
        T(_body, new Vector2(0, vp.Y * 0.5f - 8 * u), "+", 20 * u, new Color(1, 1, 1, 0.6f), HorizontalAlignment.Center, vp.X, 0);

        // palette popup
        if (ed.PaletteOpen)
        {
            float mw = 300 * u, mh = 150 * u, mx = vp.X / 2f - mw / 2f, my = vp.Y / 2f - mh / 2f;
            DrawRect(new Rect2(mx, my, mw, mh), new Color(0.03f, 0.03f, 0.06f, 0.92f));
            Frame(new Rect2(mx, my, mw, mh), accent, 2 * u);
            T(_head, new Vector2(mx, my + 8 * u), "NEW COLLIDER", 18 * u, accent, HorizontalAlignment.Center, mw, Mathf.RoundToInt(2 * u));
            T(_body, new Vector2(mx, my + 40 * u), "Shape  (↑/↓)", 13 * u, GoldDim, HorizontalAlignment.Center, mw, 0);
            for (int i = 0; i < ColliderEditor.ShapeNames.Length; i++)
                T(_body, new Vector2(mx + (60 + i * 120) * u, my + 58 * u), ColliderEditor.ShapeNames[i].ToUpper(), 15 * u, i == ed.PalShape ? Colors.White : GoldDim, HorizontalAlignment.Left, 120 * u, 0);
            T(_body, new Vector2(mx, my + 86 * u), "Color / behavior  (←/→)", 13 * u, GoldDim, HorizontalAlignment.Center, mw, 0);
            var kc = new[] { new Color(1f, 0.35f, 0.35f), new Color(0.4f, 0.7f, 1f), new Color(0.4f, 1f, 0.5f) };
            T(_body, new Vector2(mx, my + 104 * u), ColliderEditor.KindLabels[ed.PalKind], 15 * u, kc[ed.PalKind], HorizontalAlignment.Center, mw, 0);
            T(_body, new Vector2(mx, my + 128 * u), "Enter = spawn   ·   Esc = cancel", 12 * u, new Color(0.7f, 0.7f, 0.8f), HorizontalAlignment.Center, mw, 0);
        }
    }

    private void DrawRoulette(Game g, Player p, Vector2 c, Vector2 vp, float u)
    {
        DrawRect(new Rect2(0, 0, vp.X, vp.Y), new Color(0, 0, 0, 0.72f));
        var col = DamageTypes.Col(DamageType.Curse);
        T(_head, new Vector2(0, vp.Y * 0.2f), "WHEEL OF FORTUNE", 40 * u, new Color(1f, 0.82f, 0.34f), HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(4 * u));
        int pull = g.RoulettePull;
        if (pull >= 3)
        {
            T(_body, new Vector2(0, vp.Y * 0.2f + 40 * u), "the wheel is spent", 18 * u, GoldDim, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(2 * u));
            RRoulette[0] = new Rect2(-1, -1, 0, 0);
            RRoulette[1] = new Rect2(vp.X / 2f - 110 * u, vp.Y * 0.2f + 64 * u, 220 * u, 30 * u);
            Frame(RRoulette[1], new Color(Gold.R, Gold.G, Gold.B, 0.5f), 1.5f * u);
            T(_body, new Vector2(0, vp.Y * 0.2f + 70 * u), "Leave  [Esc]", 16 * u, GoldDim, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(2 * u));
            return;
        }
        int cost = Mathf.Max(1, Mathf.FloorToInt(g.Gold * (pull + 1) * 0.10f));
        int pct = (pull + 1) * 10;
        int leg = pull == 0 ? 5 : pull == 1 ? 10 : 15;
        bool canAfford = g.Gold >= cost;
        T(_body, new Vector2(0, vp.Y * 0.2f + 38 * u), $"spin {pull + 1} of 3", 18 * u, Gold, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(2 * u));
        T(_body, new Vector2(0, vp.Y * 0.40f), $"cost: {pct}% of gold  =  \u29c9 {cost}", 20 * u, canAfford ? Gold : new Color(0.95f, 0.4f, 0.45f), HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(3 * u));
        T(_body, new Vector2(0, vp.Y * 0.40f + 30 * u), $"legendary chance: {leg}%   ·   you have \u29c9 {g.Gold}", 15 * u, GoldDim, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(2 * u));
        RRoulette[0] = new Rect2(vp.X / 2f - 150 * u, vp.Y * 0.40f + 56 * u, 300 * u, 30 * u);
        RRoulette[1] = new Rect2(vp.X / 2f - 80 * u, vp.Y * 0.40f + 92 * u, 160 * u, 26 * u);
        if (canAfford) Frame(RRoulette[0], new Color(1f, 0.82f, 0.34f, 0.6f), 1.5f * u);
        Frame(RRoulette[1], new Color(Gold.R, Gold.G, Gold.B, 0.4f), 1.2f * u);
        T(_body, new Vector2(0, vp.Y * 0.40f + 64 * u), canAfford ? "[1] / [Space]  spin" : "not enough gold", 16 * u, canAfford ? new Color(1f, 0.82f, 0.34f) : GoldDim, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(2 * u));
        T(_body, new Vector2(0, vp.Y * 0.40f + 98 * u), "Leave  [Esc]", 14 * u, GoldDim, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(1 * u));
    }

    private void DrawUltMenu(Game g, Player p, Vector2 c, Vector2 vp, float u)
    {
        DrawRect(new Rect2(0, 0, vp.X, vp.Y), new Color(0, 0, 0, 0.72f));
        var col = DamageTypes.Col(DamageType.Lunar);
        T(_head, new Vector2(0, vp.Y * 0.22f), "MOON ALTAR", 40 * u, col, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(4 * u));
        T(_body, new Vector2(0, vp.Y * 0.22f + 34 * u), $"{UltName(p.Ult)}  ·  Tier {p.UltTier + 1}/5  ·  {g.BossTokens:0.#} tokens", 16 * u, Gold, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(2 * u));
        bool canUp = p.UltTier < 4 && g.BossTokens >= g.UltUpgradeCost;
        string up = p.UltTier >= 4 ? "[1] Upgrade — MAXED" : $"[1] Upgrade  (cost {g.UltUpgradeCost} tokens)";
        RUltMenu[0] = new Rect2(vp.X / 2f - 200 * u, vp.Y * 0.42f - 4 * u, 400 * u, 28 * u);
        RUltMenu[1] = new Rect2(vp.X / 2f - 200 * u, vp.Y * 0.42f + 26 * u, 400 * u, 28 * u);
        if (canUp) Frame(RUltMenu[0], new Color(col.R, col.G, col.B, 0.5f), 1.2f * u);
        if (g.BossTokens >= 1f) Frame(RUltMenu[1], new Color(col.R, col.G, col.B, 0.5f), 1.2f * u);
        T(_body, new Vector2(0, vp.Y * 0.42f), up, 18 * u, canUp ? Gold : GoldDim, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(2 * u));
        T(_body, new Vector2(0, vp.Y * 0.42f + 30 * u), "[2] Swap ultimate  (cost 1 token)", 18 * u, g.BossTokens >= 1f ? Gold : GoldDim, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(2 * u));
        T(_body, new Vector2(0, vp.Y * 0.42f + 66 * u), "[U] or Esc to close", 14 * u, GoldDim, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(1 * u));
    }

    // (NEW) soft edge-hugging vignette faked with nested fading frames — used for hit / low-health / shield feedback
    private void Vignette(Vector2 vp, Color col, float intensity)
    {
        intensity = Mathf.Clamp(intensity, 0f, 1f);
        if (intensity <= 0.001f) return;
        int bands = 7;
        float maxW = Mathf.Min(vp.X, vp.Y) * 0.17f;
        float bandW = maxW / bands;
        for (int i = 0; i < bands; i++)
        {
            float t = i / (float)(bands - 1);                    // 0 outer → 1 inner
            float inset = t * (maxW - bandW);
            float a = intensity * (1f - t) * (1f - t) * 0.55f;   // strongest at the very edge, fading inward
            if (a <= 0.002f) continue;
            Frame(new Rect2(inset, inset, vp.X - inset * 2f, vp.Y - inset * 2f), new Color(col.R, col.G, col.B, a), bandW + 1f);
        }
    }

    // (NEW) a bold chevron pinned to the screen edge, pointing toward a world direction relative to the player's camera.
    // Drawn with a dark glow backing + black outline so it stays hard-to-miss against any scene.
    private void EdgeArrow(Vector3 camFwd, Vector3 camRight, Vector3 worldDir, Vector2 c, float edgeR, float u, Color col, float sizeScale)
    {
        worldDir.Y = 0; if (worldDir.LengthSquared() < 0.001f) return; worldDir = worldDir.Normalized();
        float ang = Mathf.Atan2(worldDir.Dot(camRight), worldDir.Dot(camFwd));
        var pos = c + new Vector2(Mathf.Sin(ang), -Mathf.Cos(ang)) * edgeR;
        float s = 27 * u * sizeScale;   // (BUFF) much bigger than before (was 18)
        var dir = (pos - c).Normalized(); var perp = new Vector2(-dir.Y, dir.X);
        DrawCircle(pos, s * 1.2f, new Color(0f, 0f, 0f, 0.35f * col.A));   // soft dark halo so it reads on bright terrain
        SafePoly(new[] { pos + dir * s * 1.18f, pos - dir * s * 0.5f + perp * s * 1.18f, pos - dir * s * 0.5f - perp * s * 1.18f }, new Color(0f, 0f, 0f, 0.85f * col.A));   // black outline
        var bright = new Color(col.R, col.G, col.B, Mathf.Min(1f, col.A + 0.3f));
        SafePoly(new[] { pos + dir * s, pos - dir * s * 0.4f + perp * s, pos - dir * s * 0.4f - perp * s }, bright);   // bright fill
    }

    // (NEW) incoming-projectile warnings: pre-fire telegraphs on charging foes, plus predictive arrows / brackets /
    // directional glow for any straight-line enemy bolt on a collision course with the local player. Everything is gated
    // on the exact threat test (EnemyBolt.ThreatTo) so harmless fire whizzing past never clutters the screen.
    private void DrawThreats(Player p, Vector2 c, Vector2 vp, float u)
    {
        var cam = p.Cam;
        if (cam == null) return;
        var basis = cam.GlobalTransform.Basis;
        Vector3 camFwd = -basis.Z; camFwd.Y = 0; if (camFwd.LengthSquared() < 0.001f) return; camFwd = camFwd.Normalized();
        Vector3 camRight = basis.X; camRight.Y = 0; camRight = camRight.Normalized();
        Vector3 pp = p.GlobalPosition;
        float edgeR = Mathf.Min(vp.X, vp.Y) * 0.40f;

        // ---- ① pre-fire telegraph: mark ranged foes winding up a shot ----
        int teleShown = 0;
        foreach (var e in Game.I.Enemies)
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e) || !e.Telegraphing) continue;
            if (teleShown++ >= 6) break;
            float tf = e.TeleFrac;
            var tcol = new Color(1f, 0.75f, 0.2f, 0.5f + 0.45f * tf);
            Vector3 wp = e.GlobalPosition + new Vector3(0, e.Radius + 1.2f, 0);
            bool behind = cam.IsPositionBehind(wp);
            Vector2 sp = behind ? Vector2.Zero : cam.UnprojectPosition(wp);
            bool onScreen = !behind && sp.X >= 0 && sp.X <= vp.X && sp.Y >= 0 && sp.Y <= vp.Y;
            if (onScreen)
            {
                float rr = Mathf.Lerp(22f, 9f, tf) * u;   // ring tightens as the shot charges
                DrawArc(sp, rr, 0, Mathf.Tau, 20, tcol, 2f * u);
                T(_head, new Vector2(sp.X - 40 * u, sp.Y - 13 * u), "!", 22 * u, tcol, HorizontalAlignment.Center, 80 * u, Mathf.RoundToInt(2 * u));
            }
            else EdgeArrow(camFwd, camRight, e.GlobalPosition - pp, c, edgeR, u, tcol, 0.8f);
        }

        // ---- diver / bat swoops: airborne BODY threats (no bolt) — warn like an incoming shot ----
        foreach (var e in Game.I.Enemies)
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e) || !e.Diving) continue;
            var dcol = new Color(0.85f, 0.5f, 1f, 0.92f);   // diver purple
            Vector3 wp = e.GlobalPosition;
            bool behind = cam.IsPositionBehind(wp);
            Vector2 sp = behind ? Vector2.Zero : cam.UnprojectPosition(wp);
            bool onScreen = !behind && sp.X >= 0 && sp.X <= vp.X && sp.Y >= 0 && sp.Y <= vp.Y;
            if (onScreen) { DrawArc(sp, 17 * u, 0, Mathf.Tau, 22, dcol, 2.5f * u); DrawArc(sp, 9 * u, 0, Mathf.Tau, 16, dcol, 1.5f * u); }
            else EdgeArrow(camFwd, camRight, wp - pp, c, edgeR, u, dcol, 1.0f);
        }

        // ---- special infected (Taker): always-on locator, escalating to a loud GRAB warning while it charges ----
        foreach (var e in Game.I.Enemies)
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e) || !e.IsSpecial) continue;
            bool charging = e.SpecialCharging;
            var scol = charging ? new Color(1f, 0.28f, 0.32f, 0.95f) : new Color(0.78f, 0.35f, 0.92f, 0.7f);
            float pulse = charging ? 0.6f + 0.4f * Mathf.Sin((float)Time.GetTicksMsec() * 0.02f) : 1f;
            Vector3 wp = e.GlobalPosition + new Vector3(0, e.Radius + 1.6f, 0);
            bool behind = cam.IsPositionBehind(wp);
            Vector2 sp = behind ? Vector2.Zero : cam.UnprojectPosition(wp);
            bool onScreen = !behind && sp.X >= 0 && sp.X <= vp.X && sp.Y >= 0 && sp.Y <= vp.Y;
            if (onScreen)
            {
                float rr = (charging ? 26f : 15f) * u * pulse;
                DrawArc(sp, rr, 0, Mathf.Tau, 26, scol, (charging ? 3f : 2f) * u);
                T(_body, new Vector2(sp.X - 70 * u, sp.Y - rr - 15 * u), charging ? e.SpecialWarn : e.SpecialTag, (charging ? 18f : 12f) * u, scol, HorizontalAlignment.Center, 140 * u, Mathf.RoundToInt(2 * u));
            }
            else EdgeArrow(camFwd, camRight, wp - pp, c, edgeR, u, scol, charging ? 1.4f : 1.0f);
        }

        // ---- ②③④ in-flight bolts on a collision course ----
        float urgentTti = 99f; Vector2 urgentEdge = c; bool anyUrgent = false;
        int shown = 0;
        foreach (var b in EnemyBolt.All)
        {
            if (b == null || !GodotObject.IsInstanceValid(b)) continue;
            if (!b.ThreatTo(pp, out float tti, out float miss)) continue;
            if (shown++ >= 10) break;
            float a = Mathf.Clamp(1f - tti / 1.6f, 0.25f, 1f);   // closer = more opaque
            var col = new Color(1f, 0.24f, 0.28f, a);
            Vector3 bw = b.GlobalPosition;
            bool behind = cam.IsPositionBehind(bw);
            Vector2 sp = behind ? Vector2.Zero : cam.UnprojectPosition(bw);
            bool onScreen = !behind && sp.X >= 0 && sp.X <= vp.X && sp.Y >= 0 && sp.Y <= vp.Y;
            if (onScreen)
            {
                DrawArc(sp, 15 * u, 0, Mathf.Tau, 22, col, 2f * u);                         // ③ target bracket ring
                float ir = 15f * u * Mathf.Clamp(tti / 1.2f, 0.12f, 1f);
                DrawArc(sp, ir, 0, Mathf.Tau, 22, new Color(1f, 0.55f, 0.3f, a), 2f * u);   // closing ring = time to impact
            }
            else EdgeArrow(camFwd, camRight, bw - pp, c, edgeR, u, col, 0.85f + (1f - Mathf.Clamp(tti / 1.2f, 0f, 1f)) * 0.6f);   // ②

            if (tti < urgentTti)
            {
                urgentTti = tti; anyUrgent = true;
                Vector3 dd = bw - pp; dd.Y = 0;
                if (dd.LengthSquared() > 0.001f) { dd = dd.Normalized(); float ang = Mathf.Atan2(dd.Dot(camRight), dd.Dot(camFwd)); urgentEdge = c + new Vector2(Mathf.Sin(ang), -Mathf.Cos(ang)) * edgeR; }
            }
        }

        // ④ directional danger glow at the most urgent threat's screen edge, swelling in the last ~0.7s
        if (anyUrgent && urgentTti < 0.7f)
        {
            float gi = 1f - urgentTti / 0.7f;
            for (int k = 0; k < 4; k++)
                DrawCircle(urgentEdge, (34 + k * 20) * u, new Color(1f, 0.12f, 0.14f, 0.10f * gi));
        }
    }

    private void DrawDamageDir(Player p, Vector2 c, Vector2 vp, float u)
    {
        if (p.DmgDirT <= 0f) return;
        var cam = p.Cam;
        if (cam == null) return;
        var basis = cam.GlobalTransform.Basis;
        Vector3 fwd = -basis.Z; fwd.Y = 0; if (fwd.LengthSquared() < 0.001f) return; fwd = fwd.Normalized();
        Vector3 right = basis.X; right.Y = 0; right = right.Normalized();
        Vector3 d = p.DmgDirWorld; d.Y = 0; if (d.LengthSquared() < 0.001f) return; d = d.Normalized();
        float ang = Mathf.Atan2(d.Dot(right), d.Dot(fwd));   // 0 = ahead
        float a = Mathf.Clamp(p.DmgDirT, 0f, 1f);
        float radius = Mathf.Min(vp.X, vp.Y) * 0.34f;
        var pos = c + new Vector2(Mathf.Sin(ang), -Mathf.Cos(ang)) * radius;
        // a red chevron pointing inward (toward where the hit came from)
        float s = 22 * u;
        var dir = (pos - c).Normalized();
        var perp = new Vector2(-dir.Y, dir.X);
        var col = new Color(1f, 0.25f, 0.3f, a);
        SafePoly(new[] { pos + dir * s, pos - dir * s * 0.4f + perp * s, pos - dir * s * 0.4f - perp * s }, col);
    }

    // (NERFER Summoner) a top-centre defend objective: "DEFEND THE SUMMONING" + seconds + a draining bar, so the 45s isn't invisible
    // (HAUNT) shown while the local player fights inside the hot-zone: a title + the break-meter you fill by killing here
    private void DrawHaunt(Game g, Vector2 vp, float u)
    {
        var hc = new Color(0.95f, 0.42f, 0.78f);
        float pulse = 0.6f + 0.4f * Mathf.Sin((float)Time.GetTicksMsec() * 0.006f);
        float y = vp.Y * 0.165f;
        T(_body, new Vector2(0, y), "☠  IN THE HAUNT  ☠", 16 * u, new Color(hc.R, hc.G, hc.B, 0.85f + 0.15f * pulse), HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(2 * u));
        float bw = 300 * u, bh = 9 * u, bx = vp.X / 2f - bw / 2f, by = y + 24 * u;
        DrawRect(new Rect2(bx - 1.5f * u, by - 1.5f * u, bw + 3 * u, bh + 3 * u), new Color(0, 0, 0, 0.55f));
        DrawRect(new Rect2(bx, by, bw * g.HauntFrac, bh), new Color(1f, 0.78f, 0.32f).Lerp(hc, 0.3f));
        Frame(new Rect2(bx, by, bw, bh), new Color(hc.R, hc.G, hc.B, 0.8f), 1.3f * u);
        T(_body, new Vector2(0, by + 12 * u), "break the haunt — kills here feed the meter", 11.5f * u, GoldDim, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(1 * u));
    }

    private void DrawSummonerTimer(Game g, Vector2 vp, float u)
    {
        float tl = g.SummonerTimeLeft;
        bool held = g.SummonerHeld;
        var col = NerfShrine.KindColor(NerfKind.Summoner);
        float y = vp.Y * 0.11f;
        // (FIX) the clock only runs while someone stands in the circle — say so loudly when it's stalled, or the frozen number reads as a bug
        var warn = new Color(1f, 0.42f, 0.32f);
        float pulse = 0.65f + 0.35f * Mathf.Sin((float)Time.GetTicksMsec() * 0.008f);
        T(_body, new Vector2(0, y), held ? "HOLD THE SUMMONING" : "STAND IN THE CIRCLE!", 20 * u,
          held ? col.Lerp(Colors.White, 0.3f) : new Color(warn.R, warn.G, warn.B, pulse), HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(2 * u));
        T(_body, new Vector2(0, y + 26 * u), $"{Mathf.CeilToInt(tl)}s", 26 * u,
          held ? Colors.White : new Color(0.72f, 0.6f, 0.6f), HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(2 * u));
        float bw = 260 * u, bh = 8 * u, bx = vp.X / 2f - bw / 2f, by = y + 62 * u;
        DrawRect(new Rect2(bx - 1.5f * u, by - 1.5f * u, bw + 3 * u, bh + 3 * u), new Color(0, 0, 0, 0.55f));
        DrawRect(new Rect2(bx, by, bw * Mathf.Clamp(tl / Game.SummonerDur, 0f, 1f), bh), held ? col : col.Darkened(0.5f));
    }

    // (NERFER Sacrifice) the Crimson Rite tracker. Three states, one slot: how many sigils are still unlit → the pentagram
    // drawing itself → the silence counting down. Sits just under the Summoning band so the two can never collide.
    private void DrawCrimsonRite(Game g, Vector2 vp, float u)
    {
        var col = RiteSigil.Col.Lerp(Colors.White, 0.25f);
        float y = vp.Y * 0.11f + (g.SummonerActive ? 84 * u : 0f);
        string top; float frac; Color bar;
        if (g.SpawnStalled)
        {
            top = "THE HORDE IS BROKEN";
            frac = Mathf.Clamp(g.SpawnStallT / Game.RiteStallCap, 0f, 1f);
            bar = new Color(0.55f, 0.95f, 0.6f);   // green: this one is RELIEF, not a threat — read it apart from the red states
            T(_body, new Vector2(0, y), top, 20 * u, bar, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(2 * u));
            T(_body, new Vector2(0, y + 26 * u), $"{Mathf.CeilToInt(g.SpawnStallT)}s of silence", 22 * u, Colors.White, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(2 * u));
        }
        else if (g.RiteDrawing)
        {
            float pulse = 0.6f + 0.4f * Mathf.Sin((float)Time.GetTicksMsec() * 0.012f);
            T(_body, new Vector2(0, y), "THE RITE IS COMPLETE", 20 * u, new Color(col.R, col.G, col.B, pulse), HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(2 * u));
            T(_body, new Vector2(0, y + 26 * u), "blood answers…", 18 * u, Colors.White, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(2 * u));
            frac = g.RiteDrawProgress; bar = col;   // the bar tracks the figure actually drawing itself

        }
        else
        {
            int lit = g.RiteLit, tot = g.RiteTotal;
            T(_body, new Vector2(0, y), "THE CRIMSON RITE", 20 * u, col, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(2 * u));
            T(_body, new Vector2(0, y + 26 * u), tot > 1 ? $"{lit}/{tot} sigils lit — stand in one" : "stand in the sigil", 18 * u, Colors.White, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(2 * u));
            // sum the PARTIAL fills, not just the lit count — solo (1 sigil) the bar would otherwise sit dead at 0 for the whole
            // 3s and then snap to full, which reads as a broken meter rather than progress
            float sum = 0f;
            foreach (var rs in g.RiteSigils) if (rs != null && GodotObject.IsInstanceValid(rs)) sum += rs.Lit ? 1f : Mathf.Clamp(rs.Charge, 0f, 1f);
            frac = tot > 0 ? sum / tot : 0f; bar = col;
        }
        float bw = 260 * u, bh = 8 * u, bx = vp.X / 2f - bw / 2f, by = y + 56 * u;
        DrawRect(new Rect2(bx - 1.5f * u, by - 1.5f * u, bw + 3 * u, bh + 3 * u), new Color(0, 0, 0, 0.55f));
        DrawRect(new Rect2(bx, by, bw * frac, bh), bar);
        // per-sigil pips under the bar, so "which one still needs somebody" is countable at a glance
        if (!g.SpawnStalled && !g.RiteDrawing && g.RiteTotal > 1)
        {
            float pw = 22 * u, gap = 6 * u, tot2 = g.RiteTotal * pw + (g.RiteTotal - 1) * gap;
            float px0 = vp.X / 2f - tot2 / 2f;
            int i = 0;
            foreach (var rs in g.RiteSigils)
            {
                if (rs == null || !GodotObject.IsInstanceValid(rs)) { i++; continue; }
                float f = rs.Lit ? 1f : Mathf.Clamp(rs.Charge, 0f, 1f);
                var r = new Rect2(px0 + i * (pw + gap), by + 14 * u, pw, 5 * u);
                DrawRect(r, new Color(0, 0, 0, 0.5f));
                DrawRect(new Rect2(r.Position.X, r.Position.Y, pw * f, r.Size.Y), rs.Lit ? col : col.Darkened(0.35f));
                i++;
            }
        }
    }

    private void DrawRituals(float u)
    {
        var cam = Game.I.Player?.Cam;
        if (cam == null) return;
        var pp = Game.I.Player.GlobalPosition;
        foreach (var r in Game.I.Rituals)
        {
            if (r == null || r.Done || !IsInstanceValid(r)) continue;
            // (NEW) only show a circle's panel when you're within ~10u of its OUTSIDE edge \u2014 with 5\u00d7players rituals spread across
            // the map, showing every one you glance toward would clog the HUD. Its minimap pin still guides you to it from afar.
            if (new Vector2(r.GlobalPosition.X - pp.X, r.GlobalPosition.Z - pp.Z).Length() > r.Radius + 10f) continue;
            var top = r.GlobalPosition + new Vector3(0, 3.2f, 0);   // (NEW) lowered (was 6.5) \u2014 sits near eye level so you don't crane up at the center pillar
            if (cam.IsPositionBehind(top)) continue;
            var sp = cam.UnprojectPosition(top);
            Color col = r.Type == RiteType.Ward ? DamageTypes.Col(DamageType.Lunar)
                      : r.Type == RiteType.Summon ? DamageTypes.Col(DamageType.Curse)
                      : DamageTypes.Col(DamageType.Holy);
            bool inside = !r.Active && new Vector2(r.GlobalPosition.X - pp.X, r.GlobalPosition.Z - pp.Z).Length() <= r.Radius;   // (NEW) inside the ring \u2192 show the hold-E affordance
            float w = 158 * u, h = (inside ? 62 : 44) * u;
            var box = new Rect2(sp.X - w / 2f, sp.Y - h, w, h);
            DrawRect(box, new Color(0.05f, 0.03f, 0.09f, 0.5f));     // see-through panel
            Frame(box, new Color(col.R, col.G, col.B, 0.85f), Mathf.Max(1.5f, 2f * u));

            string title = r.Type == RiteType.Ward ? "WARDING RITE" : r.Type == RiteType.Summon ? "RITE OF SUMMONING" : "CLEANSING RITE";
            T(_body, new Vector2(box.Position.X, box.Position.Y + 13 * u), title, 11 * u, new Color(col.R, col.G, col.B), HorizontalAlignment.Center, w, Mathf.RoundToInt(1 * u));

            if (!r.Active)
            {
                // rituals are FREE now (souls come from Haunts) \u2014 show what the rite does; the hold-E prompt + fill appear BELOW once you're inside
                string rsub = r.Type == RiteType.Ward ? "wards the grove" : r.Type == RiteType.Summon ? "summons a challenge" : "cleanse the horde";
                T(_body, new Vector2(box.Position.X, box.Position.Y + 29 * u), rsub, 11 * u, new Color(col.R, col.G, col.B, 0.85f), HorizontalAlignment.Center, w, Mathf.RoundToInt(1 * u));
                if (inside)
                {
                    T(_body, new Vector2(box.Position.X, box.Position.Y + 44 * u), "hold E to begin", 11 * u, new Color(0.95f, 0.92f, 0.8f), HorizontalAlignment.Center, w, Mathf.RoundToInt(1 * u));
                    float pf = Game.I.HoldEIsRitual ? Game.I.HoldEFrac : 0f;
                    float bw = w - 24 * u, bx = box.Position.X + 12 * u, by = box.Position.Y + h - 8 * u, bh = 5 * u;
                    DrawRect(new Rect2(bx - 1 * u, by - 1 * u, bw + 2 * u, bh + 2 * u), new Color(0, 0, 0, 0.55f));
                    if (pf > 0f) DrawRect(new Rect2(bx, by, bw * pf, bh), col);
                    Frame(new Rect2(bx, by, bw, bh), new Color(col.R, col.G, col.B, 0.7f), 1f * u);
                }
            }
            else
            {
                string info; float pf;
                if (r.Type == RiteType.Ward) { info = $"charging  {Mathf.RoundToInt(r.Status * 100)}%"; pf = r.Status; }
                else if (r.Type == RiteType.Summon) { info = $"slay it  \u00b7  {Mathf.CeilToInt(r.SecondsLeft)}s"; pf = r.Status; }
                else { info = $"{r.Killed}/{r.KillTarget}  \u00b7  {Mathf.CeilToInt(r.SecondsLeft)}s"; pf = Mathf.Clamp((float)r.Killed / Mathf.Max(1, r.KillTarget), 0f, 1f); }
                T(_body, new Vector2(box.Position.X, box.Position.Y + 29 * u), info, 11 * u, Gold, HorizontalAlignment.Center, w, Mathf.RoundToInt(1 * u));
                DrawRect(new Rect2(box.Position.X, box.Position.Y + h - 4 * u, w * Mathf.Clamp(pf, 0f, 1f), 4 * u), col);
            }
        }
    }

    private void DrawEnemyBars(float u)
    {
        var cam = Game.I.Player?.Cam;
        if (cam == null) return;
        ulong now = Time.GetTicksMsec();
        foreach (var e in Game.I.Enemies)
        {
            if (e == null || e.Dead || !IsInstanceValid(e)) continue;
            var head = e.GlobalPosition + new Vector3(0, e.Radius + 0.8f, 0);
            if (cam.IsPositionBehind(head)) continue;
            // (PERF) the wall-occlusion test is a physics raycast — throttle it to ~15Hz per foe and reuse the cached result each draw
            if (now >= e.PlateLosMs) { e.PlateLosMs = now + 66; e.PlateOccluded = Game.I.SightBlocked(cam.GlobalPosition, head); }
            if (e.PlateOccluded) continue;   // (NEW) don't draw bars through walls
            var sp = cam.UnprojectPosition(head);
            float frac = e.MaxHp > 0 ? Mathf.Clamp(e.Hp / e.MaxHp, 0, 1) : 0;
            float w = Mathf.Clamp(e.Radius * 26f, 30f, 130f) * u;
            float h = (e.IsBoss ? 8f : 5f) * u;
            float x = sp.X - w / 2f, y = sp.Y;
            DrawRect(new Rect2(x - 1 * u, y - 1 * u, w + 2 * u, h + 2 * u), new Color(0, 0, 0, 0.6f));
            var fill = e.IsGoblin ? new Color(1f, 0.84f, 0.3f) : new Color(0.95f, 0.3f, 0.32f).Lerp(new Color(0.45f, 0.9f, 0.4f), frac);
            // (HOLLOW MOON PHASE 2) UNTOUCHABLE: while he's getting back up or spinning, the bar drains to a pulsing arcane
            // slab with a padlock. Players MUST be able to tell "my damage is doing nothing" apart from "he's just tanky".
            if (e.IsBoss && e.BossInvuln)
            {
                float pulse = 0.55f + 0.45f * Mathf.Sin((Time.GetTicksMsec() % 700) / 700f * Tau);
                fill = new Color(0.52f, 0.34f, 0.95f).Lerp(new Color(0.92f, 0.86f, 1f), pulse);
            }
            DrawRect(new Rect2(x, y, w * frac, h), fill);
            Frame(new Rect2(x, y, w, h), e.Elite ? new Color(1f, 0.86f, 0.25f) : new Color(0, 0, 0, 0.7f), Mathf.Max(1f, 1.4f * u));
            if (e.IsBoss && e.BossInvuln)
            {
                float pulse = 0.55f + 0.45f * Mathf.Sin((Time.GetTicksMsec() % 700) / 700f * Tau);
                Frame(new Rect2(x - 2f * u, y - 2f * u, w + 4f * u, h + 4f * u), new Color(0.85f, 0.78f, 1f, 0.35f + 0.5f * pulse), Mathf.Max(1f, 1.6f * u));
                T(_head, new Vector2(x, y + h + 1.5f * u), "\U0001F512 IMMUNE", 10f * u, new Color(0.85f, 0.78f, 1f, 0.6f + 0.4f * pulse), HorizontalAlignment.Center, w, Mathf.RoundToInt(1 * u));
            }
            // (DOOM) banked damage IS a slice of this health bar, so draw it as one: the claimed portion sits at the
            // leading edge of the fill, showing exactly how much of what's left is already spoken for. A number floating
            // overhead couldn't be read in a crowd; this can. The hairline above is the fuse draining toward detonation,
            // and the whole thing flips to a fast red pulse the moment the bank covers the rest of the bar — the tell
            // that says "it's dead already, hit it now or watch it go".
            if (e.Doomed && frac > 0f)
            {
                float dfrac = e.MaxHp > 0f ? Mathf.Clamp(e.DoomShownBank / e.MaxHp, 0f, 1f) : 0f;
                float claimed = Mathf.Min(dfrac, frac);
                bool lethal = e.DoomShownLethal;
                float period = lethal ? 320f : 950f;
                float dpulse = 0.5f + 0.5f * Mathf.Sin((Time.GetTicksMsec() % (ulong)period) / period * Tau);
                var dcol = lethal
                    ? new Color(1f, 0.30f, 0.42f).Lerp(new Color(1f, 0.88f, 0.94f), dpulse)
                    : new Color(0.58f, 0.26f, 0.82f).Lerp(new Color(0.84f, 0.60f, 1f), 0.35f + 0.3f * dpulse);
                DrawRect(new Rect2(x + w * (frac - claimed), y, w * claimed, h), dcol);
                float fz = Mathf.Clamp(e.DoomShownT / Enemy.DoomFuse, 0f, 1f);
                if (fz > 0f) DrawRect(new Rect2(x, y - 2.8f * u, w * fz, 1.6f * u), new Color(0.78f, 0.55f, 1f, 0.9f));
            }
            // (REMOVED the frozen blue "bank" bar — no banking now; the ice-block model already shows a foe is frozen/shatter-able)
            if (!e.Frozen && e.FreezeStacks > 0.5f)   // (NEW) freeze-stack indicator ❄ N/threshold
            {
                T(_body, new Vector2(x, y - 14f * u), $"\u2744 {Mathf.CeilToInt(e.FreezeStacks)}/{Mathf.CeilToInt(e.FreezeThreshold)}", 10f * u, new Color(0.62f, 0.86f, 1f), HorizontalAlignment.Center, w, Mathf.RoundToInt(1 * u));
            }
            if (e.BurnStacks > 0.5f && !e.Frozen)   // (NEW) Ember burn: 🔥 current/needed toward Living Bomb
                IconLabel(_body, sp.X, y - 14f * u, false, $"{Mathf.CeilToInt(e.BurnStacks)}/{Mathf.CeilToInt(e.LivingBombThreshold)}", 10f * u, new Color(1f, 0.68f, 0.32f));
            if (e.LivingBombStacks > 0)   // (NEW) Ember Living Bomb: 💣 xN running count (synced → all players), pulses
            {
                float pulse = 0.5f + 0.5f * Mathf.Sin((Time.GetTicksMsec() % 900) / 900f * Mathf.Tau);
                IconLabel(_head, sp.X, y - 26f * u, true, $"x{e.LivingBombStacks}", 11f * u, new Color(1f, 0.5f + 0.4f * pulse, 0.15f), pulse);
            }
            if (e.WardUp)   // (NEW) WARDED PHALANX: the ward bar sits under the health bar and IS the real fight
            {
                float wy = y + h + 1f * u, wh = 3.2f * u;
                DrawRect(new Rect2(x - 1 * u, wy - 1 * u, w + 2 * u, wh + 2 * u), new Color(0, 0, 0, 0.6f));
                float wf = e.WardFrac;
                DrawRect(new Rect2(x, wy, w * wf, wh), new Color(0.62f, 0.48f, 1f).Lerp(new Color(1f, 0.35f, 0.42f), 1f - wf));
                Frame(new Rect2(x, wy, w, wh), new Color(0.8f, 0.7f, 1f, 0.9f), Mathf.Max(1f, 1.1f * u));
            }
            if (e.IsBoss)   // (NEW) aggression / heat meter — thin bar under the health bar
            {
                float hy = y + h + 1f * u, hh = 2.2f * u;
                DrawRect(new Rect2(x - 1 * u, hy - 1 * u, w + 2 * u, hh + 2 * u), new Color(0, 0, 0, 0.55f));
                DrawRect(new Rect2(x, hy, w * e.BossHeat, hh), new Color(1f, 0.78f, 0.2f).Lerp(new Color(1f, 0.14f, 0.1f), e.BossHeat));
            }
            if (e.IsBoss && e.IsCharging)   // attack wind-up timer + name (the attack meter — tells you what's coming) (NEW)
            {
                float ay = y + h + 4.5f * u, ah = 3f * u;
                T(_body, new Vector2(x, ay - 14f * u), "\u26a0 " + e.BossAttackName, 11f * u, new Color(1f, 0.55f, 0.22f), HorizontalAlignment.Center, w, Mathf.RoundToInt(1 * u));
                DrawRect(new Rect2(x - 1 * u, ay - 1 * u, w + 2 * u, ah + 2 * u), new Color(0, 0, 0, 0.6f));
                DrawRect(new Rect2(x, ay, w * e.ChargeFrac, ah), new Color(1f, 0.55f, 0.12f));
                Frame(new Rect2(x, ay, w, ah), new Color(1f, 0.3f, 0.12f, 0.9f), Mathf.Max(1f, 1.1f * u));
            }
            if (e.Label != "")
                T(_body, new Vector2(x, y - 4 * u), e.Label, (e.IsBoss ? 12f : 10f) * u, e.Elite ? new Color(1f, 0.86f, 0.3f) : Gold, HorizontalAlignment.Center, w, Mathf.RoundToInt(1 * u));
            string tag = e.PlateTag();   // affix ICON + archetype WORD (e.g. "💢 Stunner")
            if (tag != "")
            {
                float tfs = 10.5f * u, ty = y - (e.Label != "" ? 17f : 6f) * u;
                var tw = _head.GetStringSize(tag, HorizontalAlignment.Left, -1, Mathf.RoundToInt(tfs));
                T(_head, new Vector2(sp.X - tw.X / 2f, ty), tag, tfs, new Color(1f, 0.97f, 0.86f), HorizontalAlignment.Left, -1, Mathf.RoundToInt(1.5f * u));   // centered on the foe, NO width clip → never cut off
            }
            float px = x, py = y + h + 3 * u, ps = 6 * u;
            void Pip(bool on, Color col) { if (on) { DrawRect(new Rect2(px, py, ps, ps), col); px += ps + 2 * u; } }
            Pip(e.SlowT > 0, DamageTypes.Col(DamageType.Frost));
            Pip(e.RootT > 0, DamageTypes.Col(DamageType.Nature));
            Pip(e.MarkT > 0, DamageTypes.Col(DamageType.Curse));
            if (e.ArcaneMarked) DrawConduit(new Vector2(x - 11 * u, y + h * 0.5f), 6f * u, DamageTypes.Col(DamageType.Arcane));   // (NEW) CONDUIT — the chain-lightning arcs through it
        }
    }

    private void DrawFlourish(float u)
    {
        if (_flourT <= 0f) return;
        var vp = GetViewportRect().Size;
        float k = 1f - (_flourT / 0.7f);                 // 0 → 1 over its life
        float a = Mathf.Clamp(1f - k * 0.9f, 0f, 1f);
        float fs = 46 * u * Mathf.Lerp(1.35f, 1f, Mathf.Min(1f, k * 3f));
        float y = vp.Y * 0.40f - k * 42 * u;
        DrawStringOutline(_head, new Vector2(0, y), _flourTxt, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(fs), Mathf.RoundToInt(6 * u), new Color(0.04f, 0.02f, 0.07f, a));
        DrawString(_head, new Vector2(0, y), _flourTxt, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(fs), new Color(_flourCol.R, _flourCol.G, _flourCol.B, a));
    }

    private void DrawPops(float u)
    {
        if (_pops.Count == 0) return;
        var cam = Game.I.Player?.Cam;
        if (cam == null) return;
        foreach (var pop in _pops)
        {
            if (cam.IsPositionBehind(pop.W)) continue;
            var sp = cam.UnprojectPosition(pop.W);
            float k = pop.T / PopMax;
            float a = k < 0.12f ? 1f : Mathf.Clamp(1f - (k - 0.12f) / 0.88f, 0, 1);
            float punch = k < 0.12f ? Mathf.Lerp(1.35f, 1f, k / 0.12f) : 1f;
            float fs = 24 * u * punch;
            var pos = sp - new Vector2(0, k * 46 * u);
            var size = _impact.GetStringSize(pop.Txt, HorizontalAlignment.Left, -1, Mathf.RoundToInt(fs));
            float padX = 12 * u, padY = 6 * u;
            var rect = new Rect2(pos.X - size.X / 2 - padX, pos.Y - size.Y - padY, size.X + padX * 2, size.Y + padY * 2);
            DrawRect(rect, new Color(0.06f, 0.04f, 0.10f, 0.92f * a));
            SafePoly(new[] { new Vector2(pos.X - 7 * u, rect.Position.Y + rect.Size.Y - 1), new Vector2(pos.X + 7 * u, rect.Position.Y + rect.Size.Y - 1), new Vector2(pos.X, rect.Position.Y + rect.Size.Y + 10 * u) }, new Color(0.06f, 0.04f, 0.10f, 0.92f * a));
            Frame(rect, new Color(pop.Col.R, pop.Col.G, pop.Col.B, a), 2.5f * u);
            var tp = new Vector2(rect.Position.X + padX, rect.Position.Y + padY + size.Y * 0.8f);
            var fill = new Color(Mathf.Lerp(pop.Col.R, 1f, 0.55f), Mathf.Lerp(pop.Col.G, 1f, 0.55f), Mathf.Lerp(pop.Col.B, 1f, 0.55f), a);
            DrawStringOutline(_impact, tp, pop.Txt, HorizontalAlignment.Left, -1, Mathf.RoundToInt(fs), Mathf.RoundToInt(3 * u), new Color(0.04f, 0.02f, 0.07f, a));
            DrawString(_impact, tp, pop.Txt, HorizontalAlignment.Left, -1, Mathf.RoundToInt(fs), fill);
        }
    }

    // maze-ritual banner: a 3:00 countdown while hunting the hidden statue, then a FLEE warning + veil-fill bar
    private void DrawRitual(Game g, Vector2 vp, float u)
    {
        if (g.RitualVeil)
        {
            float a = 0.6f + 0.4f * Mathf.Sin(Time.GetTicksMsec() * 0.01f);
            T(_head, new Vector2(0, vp.Y * 0.14f), "FLEE THE DARKNESS", 40 * u, new Color(0.85f, 0.35f, 1f, a), HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(3 * u));
            float bw = 300 * u, bx = vp.X * 0.5f - bw * 0.5f, by = vp.Y * 0.205f;
            DrawRect(new Rect2(bx - 2 * u, by - 2 * u, bw + 4 * u, 10 * u + 4 * u), new Color(0, 0, 0, 0.5f));
            Bar(bx, by, bw, 10 * u, g.RitualVeilFrac, new Color(0.55f, 0.12f, 0.65f));
            return;
        }
        float t = Mathf.Max(0f, g.RitualTimeLeft);
        int mm = (int)t / 60, ss = (int)t % 60;
        var tc = t < 30f ? new Color(1f, 0.4f, 0.4f) : new Color(0.92f, 0.86f, 1f);
        T(_head, new Vector2(0, vp.Y * 0.12f), $"RITUAL   {mm}:{ss:00}", 30 * u, tc, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(3 * u));
        T(_body, new Vector2(0, vp.Y * 0.12f + 34 * u), "find the hidden statue", 15 * u, new Color(0.82f, 0.80f, 0.92f), HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(2 * u));
    }

    private void DrawAllyRoster(Game g, Vector2 vp, float u)
    {
        var allies = g.NetMgr.AllyAvatars();
        if (allies.Count == 0) return;
        // start BELOW the top-right minimap (centered at y=108u, radius 90u → bottom ≈198u) so the two don't overlap
        float w = 190 * u, x = vp.X - w - 12 * u, y = 212 * u;
        float rowH = 42 * u, pad = 6 * u, bw = w - 2 * pad;
        float pulse = 0.5f + 0.5f * Mathf.Sin(Time.GetTicksMsec() * 0.012f);
        bool anyCrit = false;
        int idx = 2;
        foreach (var a in allies)
        {
            bool crit = !a.Downed && a.HpFrac < 0.20f;
            bool stunned = !a.Downed && a.StunState > 0;   // (NEW)
            anyCrit |= crit;
            var bg = a.Downed ? new Color(0.14f, 0.02f, 0.03f, 0.82f)
                   : crit ? new Color(0.10f + 0.18f * pulse, 0.02f, 0.03f, 0.84f)
                   : stunned ? new Color(0.10f + 0.16f * pulse, 0.09f, 0.02f, 0.84f)
                   : new Color(0.03f, 0.04f, 0.08f, 0.72f);
            DrawRect(new Rect2(x, y, w, rowH), bg);
            Frame(new Rect2(x, y, w, rowH), (crit || a.Downed) ? new Color(1f, 0.3f, 0.3f, 0.55f + 0.45f * pulse) : (stunned ? new Color(1f, 0.85f, 0.2f, 0.55f + 0.45f * pulse) : new Color(0.7f, 0.85f, 1f, 0.5f)), 1.2f * u);

            string name = a.Downed ? $"Warden {idx} \u2014 DOWNED" : stunned ? $"Warden {idx}  \u26a1 {(a.StunState == 2 ? "GRABBED!" : "STUNNED")}" : (crit ? $"Warden {idx}  \u26a0 LOW" : $"Warden {idx}");
            var nameCol = a.Downed ? new Color(1f, 0.55f, 0.55f) : stunned ? new Color(1f, 0.88f, 0.35f) : (crit ? new Color(1f, 0.55f, 0.55f) : new Color(0.82f, 0.9f, 1f));
            T(_body, new Vector2(x + pad, y + 13 * u), name, 12 * u, nameCol, HorizontalAlignment.Left, bw, Mathf.RoundToInt(2 * u));

            Bar(x + pad, y + 19 * u, bw, 6 * u, a.HpFrac, a.HpFrac > 0.35f ? Palette.Verdant : Palette.Blood);
            Bar(x + pad, y + 28 * u, bw, 4 * u, a.ManaFrac, new Color(0.55f, 0.88f, 1f));

            string tags = "";
            if (a.Blessed > 0f) tags += "Blessed  ";
            if (a.BloodStacks > 0) tags += $"\u25c6{a.BloodStacks}  ";
            if (a.ArmorCount > 0) tags += $"\u2748{a.ArmorCount}";
            if (tags.Length > 0) T(_body, new Vector2(x + pad, y + 39 * u), tags, 9 * u, new Color(1f, 0.85f, 0.7f), HorizontalAlignment.Left, bw, Mathf.RoundToInt(1 * u));

            y += rowH + pad;
            idx++;
        }
        if (anyCrit)
            T(_head, new Vector2(0, vp.Y * 0.30f), "\u26a0 ALLY CRITICAL", 24 * u, new Color(1f, 0.4f, 0.4f, 0.6f + 0.4f * pulse), HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(3 * u));
    }

    private void DrawVitals(Player p, Vector2 vp, float u, float m)
    {
        float bw = 264 * u, bh = 18 * u, bx = m;
        float hpY = vp.Y - m - 64 * u;
        float shieldY = hpY - 9 * u;
        float manaY = hpY + bh + 5 * u;
        float dashY = manaY + 16 * u;

        if (p.MaxShield > 0.5f)
        {
            float sw = bw * Mathf.Clamp(p.MaxShield / p.S.MaxHp, 0, 1);
            Bar(bx, shieldY, sw, 6 * u, p.MaxShield > 0 ? p.Shield / p.MaxShield : 0, DamageTypes.Col(DamageType.Lunar));
        }
        // ARMOR: shared damage-negating charges, shown for EVERY witch at all times.
        // red = blood (blood wave), green = thorn (Thorn Skin). Empty slots show the current cap.
        {
            var blood = DamageTypes.Col(DamageType.Blood);
            float ay = shieldY - 13 * u;
            for (int i = 0; i < p.MaxArmor; i++)
            {
                var pr = new Rect2(bx + i * 16 * u, ay, 12 * u, 9 * u);
                bool on = i < p.Armor.Count;
                if (on)
                {
                    var cc = p.Armor[i].Thorn ? Palette.Verdant : blood;
                    DrawRect(pr, new Color(cc.R, cc.G, cc.B, 0.92f));
                    Frame(pr, new Color(1f, 1f, 1f, 0.85f), Mathf.Max(1f, u));
                }
                else
                {
                    DrawRect(pr, new Color(0.5f, 0.52f, 0.58f, 0.16f));
                    Frame(pr, new Color(0.7f, 0.75f, 0.82f, 0.35f), Mathf.Max(1f, u));
                }
            }
            T(_body, new Vector2(bx + p.MaxArmor * 16 * u + 6 * u, ay + 9 * u), "ARMOR", 9 * u, GoldDim, HorizontalAlignment.Left, -1, Mathf.RoundToInt(1 * u));
        }
        float rf = Mathf.Clamp(p.ManaFlash / 0.4f, 0f, 1f);     // resource-fail flash (a cast you couldn't pay for)
        float rfShake = rf > 0f ? Mathf.Sin((float)Time.GetTicksMsec() * 0.045f) * 5f * u * rf : 0f;
        var rfBorder = new Color(1f, 0.25f, 0.22f, rf);
        float hf = Mathf.Clamp(p.Hp / p.S.MaxHp, 0, 1);
        float hShake = p.CrimsonWitch ? rfShake : 0f;           // Crimson pays in HP — her health bar IS the resource bar
        Bar(bx + hShake, hpY, bw, bh, hf, hf > 0.35f ? Palette.Verdant : Palette.Blood);
        if (p.CrimsonWitch && rf > 0f) Frame(new Rect2(bx + hShake - 2 * u, hpY - 2 * u, bw + 4 * u, bh + 4 * u), rfBorder, Mathf.Max(2f, 2.5f * u));
        T(_body, new Vector2(bx + hShake + bw + 8 * u, hpY + bh * 0.78f), $"{Mathf.CeilToInt(p.Hp)}/{Mathf.RoundToInt(p.S.MaxHp)}", 13 * u, Gold, HorizontalAlignment.Left, -1, Mathf.RoundToInt(2 * u));
        if (hf <= 0.20f && !p.Downed)   // (NEW) low-health: pulsing alarm frame around the HP bar
        {
            float lp = 0.45f + 0.45f * Mathf.Sin((float)Time.GetTicksMsec() * 0.011f);
            Frame(new Rect2(bx + hShake - 3 * u, hpY - 3 * u, bw + 6 * u, bh + 6 * u), new Color(1f, 0.2f, 0.2f, 0.35f + 0.5f * lp), Mathf.Max(2f, 3f * u));
        }

        if (!p.CrimsonWitch)   // Crimson casts on HP, not mana — no mana bar for her
        {
            int mm = Mathf.RoundToInt(p.S.ManaMax);
            float seg = (bw - (mm - 1) * 4 * u) / mm, mh = 9 * u;
            var manaCol = p.ManaFlash > 0 ? Palette.Blood : new Color(0.55f, 0.88f, 1f);
            for (int i = 0; i < mm; i++)
            {
                float sx = bx + rfShake + i * (seg + 4 * u);
                DrawRect(new Rect2(sx, manaY, seg, mh), new Color(0, 0, 0, 0.55f));
                float fv = Mathf.Clamp(p.Mana - i, 0, 1);
                if (fv > 0) DrawRect(new Rect2(sx, manaY, seg * fv, mh), manaCol);
                Frame(new Rect2(sx, manaY, seg, mh), new Color(Gold.R, Gold.G, Gold.B, 0.4f), Mathf.Max(1f, u));
            }
            if (rf > 0f) Frame(new Rect2(bx + rfShake - 2 * u, manaY - 2 * u, bw + 4 * u, mh + 4 * u), rfBorder, Mathf.Max(2f, 2.5f * u));
        }

        for (int i = 0; i < p.S.DashCharges; i++)
            DrawRect(new Rect2(bx + i * 16 * u, dashY, 11 * u, 6 * u), i < p.DashStock ? Palette.Lunar : new Color(0.3f, 0.3f, 0.42f));
        T(_body, new Vector2(bx + p.S.DashCharges * 16 * u + 8 * u, dashY + 7 * u), "DASH", 10 * u, GoldDim);

        float mox = bx + 110 * u;
        for (int i = 0; i < p.S.ModSlots; i++)
        {
            float mx = mox + i * 30 * u;
            var box = new Rect2(mx, dashY - 4 * u, 26 * u, 15 * u);
            if (i < p.Mods.Count)
            {
                var mo = p.Mods[i]; var mc = ModMeta.Col(mo.Type);
                DrawRect(box, new Color(mc.R, mc.G, mc.B, 0.3f));
                Frame(box, mc, Mathf.Max(1.4f, 1.4f * u));
                T(_body, new Vector2(mx + 4 * u, dashY + 8 * u), ModMeta.Tag(mo.Type), 9 * u, mc);
            }
            else { DrawRect(box, new Color(1, 1, 1, 0.05f)); Frame(box, Faint, Mathf.Max(1f, u)); }
        }
        T(_body, new Vector2(mox, dashY + 22 * u), "MODS", 10 * u, GoldDim);

        float xpf = Mathf.Clamp(p.Xp / p.XpNext, 0, 1);
        DrawRect(new Rect2(0, vp.Y - 7 * u, vp.X, 7 * u), new Color(0, 0, 0, 0.5f));
        DrawRect(new Rect2(0, vp.Y - 7 * u, vp.X * xpf, 7 * u), Palette.Ember);
        T(_impact, new Vector2(0, vp.Y - 34 * u), $"Lv {p.Level}", 20 * u, Gold, HorizontalAlignment.Right, vp.X - m, Mathf.RoundToInt(2 * u));
        T(_body, new Vector2(0, m + 16 * u), "Tab — Grimoire", 12 * u, GoldDim, HorizontalAlignment.Right, vp.X - m, Mathf.RoundToInt(2 * u));
    }

    private void DrawCombat(Player p, Vector2 c, float u)
    {
        if (_breakT > 0f)
        {
            // combo just got cut by a hit — flash a red, jittering count
            float k = _breakT / 0.6f;
            var cc = new Vector2(c.X + (float)(_orng.RandfRange(-1, 1) * 5 * u * k), c.Y - 56 * u + (float)(_orng.RandfRange(-1, 1) * 5 * u * k));
            var rc = new Color(1f, 0.25f, 0.3f, Mathf.Clamp(k, 0, 1));
            T(_impact, new Vector2(cc.X - 100 * u, cc.Y + 8 * u), $"x{p.Combo}", 38 * u * (1f + k * 0.4f), rc, HorizontalAlignment.Center, 200 * u, Mathf.RoundToInt(4 * u));
        }
        else if (p.ComboLive)
        {
            float frac = p.ComboFrac();                     // 1 fresh → 0 about to expire
            float urg = 1f - frac;                          // rises as the window runs out
            if (p.Combo > _comboSeen) _comboPopT = 0.28f;   // it just grew → pop
            _comboSeen = p.Combo;
            float pop = Mathf.Clamp(_comboPopT / 0.28f, 0f, 1f);
            float weave = Mathf.Clamp(p.FreshT / 0.5f, 0f, 1f);   // a weave just landed

            // tremble intensifies hard as it nears expiry (plus a jolt on a fresh weave)
            float tremble = (1.5f + urg * urg * 9f + weave * 3f) * u;
            var jit = new Vector2((float)_orng.RandfRange(-1, 1), (float)_orng.RandfRange(-1, 1)) * tremble;
            var cc = new Vector2(c.X, c.Y - 56 * u) + jit;

            // ring + colour shift from gold toward warm-red as it's about to drop
            var arcCol = Gold.Lerp(new Color(1f, 0.30f, 0.22f), urg * 0.85f);
            Arc(cc, 22 * u, frac, arcCol, (3.5f + weave * 2.5f) * u);

            // build-up + weave bump the size and flash toward the weave colour
            float sc = 1f + Mathf.Min(p.Combo, 12) * 0.04f + pop * 0.5f + weave * 0.6f;
            var txtCol = arcCol.Lerp(_flourCol, weave);
            float glow = Mathf.Max(weave, pop);
            if (glow > 0.01f)
                DrawArc(cc, (24f + glow * 14f) * u, 0, Tau, 26, new Color(txtCol.R, txtCol.G, txtCol.B, 0.35f * glow), (2.5f + glow * 2f) * u);
            T(_impact, new Vector2(cc.X - 100 * u, cc.Y + 8 * u), $"x{p.Combo}", 30 * u * sc, txtCol, HorizontalAlignment.Center, 200 * u, Mathf.RoundToInt(3 * u));
        }
        else { _comboSeen = 0; }   // combo gone — next build-up pops fresh
        if (p.Charging)
        {
            var col = p.ChargeAmt >= 0.95f ? DamageTypes.Col(DamageType.Lunar) : Palette.Lunar;
            DrawArc(c, 26 * u, 0, Tau, 28, new Color(1, 1, 1, 0.14f), 2f * u);
            Arc(c, 26 * u, p.ChargeAmt, col, 3.5f * u);
            if (p.ChargeAmt >= 0.95f) T(_body, new Vector2(c.X - 60 * u, c.Y + 48 * u), "FULL", 13 * u, DamageTypes.Col(DamageType.Lunar), HorizontalAlignment.Center, 120 * u, Mathf.RoundToInt(2 * u));
        }
        int caps = p.S.FinSlots;
        float gap = 42 * u, x0 = c.X - (caps - 1) * gap / 2f, py = c.Y + 74 * u;
        float pulse = 0.55f + 0.45f * Mathf.Sin(Time.GetTicksMsec() * 0.011f);
        for (int i = 0; i < caps; i++)
        {
            var pc = new Vector2(x0 + i * gap, py);
            if (i < p.Fin.Count)
            {
                var f = p.Fin[i]; var fc = FinCol(p, f.Type);
                float nrf = Mathf.Clamp(f.NotReadyFlash / 0.4f, 0f, 1f);   // not-ready sputter flash
                if (nrf > 0f) pc.X += Mathf.Sin((float)Time.GetTicksMsec() * 0.05f) * 4f * u * nrf;   // shake the pip
                DrawArc(pc, 13 * u, 0, Tau, 24, Faint, 3f * u);
                if (f.Armed) { DrawCircle(pc, 13 * u, new Color(fc.R, fc.G, fc.B, 0.28f * pulse)); Arc(pc, 13 * u, 1f, fc, 3.5f * u); }
                else Arc(pc, 13 * u, (float)f.Charge / f.Every, fc, 3.5f * u);
                if (nrf > 0f) DrawArc(pc, 16 * u, 0, Tau, 24, new Color(1f, 0.25f, 0.22f, nrf), 2.5f * u);   // red "not ready" ring
                T(_body, new Vector2(pc.X - 15 * u, pc.Y + 28 * u), KeyName(f.Bind), 12 * u, nrf > 0f ? new Color(1f, 0.45f, 0.45f) : (f.Armed ? Gold : GoldDim), HorizontalAlignment.Center, 30 * u, Mathf.RoundToInt(2 * u));
            }
            else { DrawArc(pc, 13 * u, 0, Tau, 24, Faint, 2f * u); T(_body, new Vector2(pc.X - 15 * u, pc.Y + 28 * u), i < KL.Length ? KL[i] : "?", 11 * u, new Color(1, 1, 1, 0.25f), HorizontalAlignment.Center, 30 * u); }
        }
        // (REMOVED) a SECOND armed-finisher indicator used to draw a ring per armed finisher right at the reticle.
        // The pip row above already shows readiness AND the keybind, so this was duplicate clutter over the crosshair.
        // Note: the ring it drew was the only display of `f.Window` (how long the armed finisher stays up) — say the
        // word if you want that countdown folded into the pip ring instead of the solid "armed" ring it draws now.
    }

    // Witching Hour fires the equipped right-click element, so its indicator follows that color
    private static Color FinCol(Player p, FinType t)
        => (t == FinType.Fullmod && p != null) ? DamageTypes.Col(p.SecondaryType) : FinMeta.Col(t);

    // ===== card panel (typed + rarity-loud) =====
    // a card mid-spin: no real face, a flickering rarity colour, streaking motion blur and a scrambling glyph. Reads as
    // a slot reel whirling before it stops.
    // deterministic per-cell rarity so the reel reads as a fixed strip of symbols scrolling past (not random noise)
    private static int ReelTier(long m) { ulong h = (ulong)(m * 2654435761L); h ^= h >> 13; return (int)(h % 5UL); }

    // A real slot reel: a vertical strip of witchy sigils scrolls UP through the card window, motion-blurred and
    // decelerating with an easeOutBack detent bounce, a glowing payline across the middle, and reel-shading top/bottom.
    private void DrawSpinningCard(Rect2 r, int idx, int finalTier, float u)
    {
        float t = _panelT, lockAt = RollLockAt(idx);
        float frac = Mathf.Clamp(t / lockAt, 0f, 1f);
        // easeOutBack — races, then overshoots the detent and settles back into it (the slot "ka-chunk")
        float c1 = 1.9f, c3 = c1 + 1f, f1 = frac - 1f;
        float eased = 1f + c3 * f1 * f1 * f1 + c1 * f1 * f1;
        float speed = Mathf.Clamp((1f - frac) * (1f - frac), 0f, 1f);   // blur/stretch proxy: fast early, still at the end

        float midY = r.Position.Y + r.Size.Y * 0.5f;
        float spacing = 46f * u;
        int cellsPassed = 30 + idx * 5;                 // total symbols that fly past before it stops
        float scroll = cellsPassed * spacing * eased;   // final scroll lands exactly on a detent → a symbol dead-centre
        long mCenter = (long)Mathf.Round(scroll / spacing);

        // background window + a faint rarity wash that resolves toward the real colour as it slows
        var rc = Rarities.Col((Rarity)Mathf.Clamp(ReelTier(mCenter), 0, 4)).Lerp(Rarities.Col((Rarity)Mathf.Clamp(finalTier, 0, 4)), frac);
        DrawRect(r, new Color(0.05f, 0.045f, 0.08f, 0.97f));
        DrawRect(r, new Color(rc.R, rc.G, rc.B, 0.10f));

        float margin = 18f * u;
        for (long o = -3; o <= 3; o++)
        {
            long m = mCenter + o;
            float y = midY + (m * spacing - scroll);
            if (y < r.Position.Y - spacing || y > r.Position.Y + r.Size.Y + spacing) continue;
            // the symbol that ends up centred at the stop IS the real rarity; the strip resolves to it as it slows
            int tier = (m == (long)cellsPassed) ? finalTier : ReelTier(m);
            if (frac > 0.55f && o == 0) tier = finalTier;
            float edgeFade = Mathf.Clamp((y - r.Position.Y) / margin, 0f, 1f) * Mathf.Clamp((r.Position.Y + r.Size.Y - y) / margin, 0f, 1f);
            float vstretch = 1f + speed * 2.2f;   // smear along travel while fast
            // motion-blur ghosts trailing DOWNward (symbols move up)
            int ghosts = Mathf.RoundToInt(speed * 3f);
            for (int gI = ghosts; gI >= 1; gI--)
                DrawReelSymbol(r.Position.X + r.Size.X * 0.5f, y + gI * spacing * 0.42f * speed, 15f * u, vstretch, tier, m, 0.16f * edgeFade, t);
            DrawReelSymbol(r.Position.X + r.Size.X * 0.5f, y, 15f * u, vstretch, tier, m, 0.95f * edgeFade, t);
        }

        // reel shading — dark gradient bands top & bottom fake the curve of a physical reel
        int shN = 5;
        for (int b = 0; b < shN; b++)
        {
            float a = 0.5f * (1f - b / (float)shN);
            DrawRect(new Rect2(r.Position.X, r.Position.Y + b * 5f * u, r.Size.X, 5f * u), new Color(0.03f, 0.02f, 0.05f, a));
            DrawRect(new Rect2(r.Position.X, r.Position.Y + r.Size.Y - (b + 1) * 5f * u, r.Size.X, 5f * u), new Color(0.03f, 0.02f, 0.05f, a));
        }
        // the PAYLINE — a glowing horizontal bar across the middle where the symbol locks in
        float payGlow = 0.5f + 0.5f * Mathf.Sin(t * 12f);
        var pc = Rarities.Col((Rarity)Mathf.Clamp(finalTier, 0, 4));
        DrawRect(new Rect2(r.Position.X, midY - 2f * u, r.Size.X, 4f * u), new Color(pc.R, pc.G, pc.B, 0.28f + 0.22f * payGlow));
        DrawLine(new Vector2(r.Position.X + 3 * u, midY), new Vector2(r.Position.X + r.Size.X - 3 * u, midY), new Color(1f, 0.95f, 0.8f, 0.35f + 0.3f * payGlow), 1.4f * u);
        // little payline arrows pointing in from both edges
        float ax = 8f * u;
        SafePoly(new[] { new Vector2(r.Position.X, midY - ax), new Vector2(r.Position.X + ax, midY), new Vector2(r.Position.X, midY + ax) }, new Color(pc.R, pc.G, pc.B, 0.9f));
        SafePoly(new[] { new Vector2(r.Position.X + r.Size.X, midY - ax), new Vector2(r.Position.X + r.Size.X - ax, midY), new Vector2(r.Position.X + r.Size.X, midY + ax) }, new Color(pc.R, pc.G, pc.B, 0.9f));

        // anticipation: the frame brightens + the top/bottom bars flash as it's about to stop
        float antic = Mathf.SmoothStep(0.55f, 1f, frac);
        Frame(r, rc.Lerp(new Color(1f, 0.95f, 0.75f), antic * (0.4f + 0.6f * payGlow)), (3f + antic * 1.5f) * u);
    }

    // one witchy reel sigil — an outer bloom, a rarity gem/ring/crescent/star by seed, tinted to its rarity
    private void DrawReelSymbol(float cx, float cy, float s, float vstretch, int tier, long seed, float alpha, float t)
    {
        if (alpha <= 0.01f) return;
        var rc = Rarities.Col((Rarity)Mathf.Clamp(tier, 0, 4));
        var c = new Vector2(cx, cy);
        float sy = s * vstretch;
        // soft bloom
        DrawCircle(c, s * 1.35f, new Color(rc.R, rc.G, rc.B, 0.10f * alpha));
        int kind = (int)(((ulong)(seed * 6364136223846793005L) >> 33) % 4UL);
        var bright = rc.Lerp(Colors.White, 0.45f);
        switch (kind)
        {
            case 0:   // gem — a tall diamond
                SafePoly(new[] { new Vector2(cx, cy - sy), new Vector2(cx + s * 0.7f, cy), new Vector2(cx, cy + sy), new Vector2(cx - s * 0.7f, cy) }, new Color(rc.R, rc.G, rc.B, alpha));
                SafePoly(new[] { new Vector2(cx, cy - sy * 0.5f), new Vector2(cx + s * 0.34f, cy), new Vector2(cx, cy + sy * 0.5f), new Vector2(cx - s * 0.34f, cy) }, new Color(bright.R, bright.G, bright.B, alpha));
                break;
            case 1:   // ring / arcane sigil
                DrawArc(c, s * 0.85f, 0f, Mathf.Tau, 20, new Color(rc.R, rc.G, rc.B, alpha), 2.6f * (s / 15f), true);
                DrawCircle(c, s * 0.28f, new Color(bright.R, bright.G, bright.B, alpha));
                break;
            case 2:   // crescent moon — bright disc with a dark bite offset
                DrawCircle(c, s * 0.8f, new Color(rc.R, rc.G, rc.B, alpha));
                DrawCircle(new Vector2(cx + s * 0.34f, cy - s * 0.12f), s * 0.72f, new Color(0.05f, 0.045f, 0.08f, alpha));
                break;
            default:  // 4-point star (two crossed diamonds)
                float rot = t * 2f;
                for (int q = 0; q < 2; q++)
                {
                    float a0 = rot + q * Mathf.Pi * 0.5f;
                    var p0 = c + new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * sy;
                    var p1 = c + new Vector2(Mathf.Cos(a0 + Mathf.Pi * 0.5f), Mathf.Sin(a0 + Mathf.Pi * 0.5f)) * (s * 0.4f);
                    var p2 = c - new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * sy;
                    var p3 = c - new Vector2(Mathf.Cos(a0 + Mathf.Pi * 0.5f), Mathf.Sin(a0 + Mathf.Pi * 0.5f)) * (s * 0.4f);
                    SafePoly(new[] { p0, p1, p2, p3 }, new Color(rc.R, rc.G, rc.B, alpha));
                }
                DrawCircle(c, s * 0.22f, new Color(bright.R, bright.G, bright.B, alpha));
                break;
        }
    }

    private void DrawCardPanel(Rect2 r, Rarity rar, string title, string typeLine, Color typeCol, string desc, string badge, bool hover, float u)
    {
        int tier = (int)rar;
        var rc = Rarities.Col(rar);
        float rt = r.Position.X + r.Size.X, rb = r.Position.Y + r.Size.Y;

        // base panel + rarity wash (rarity owns the background; type stays on the ribbon/pill only)
        DrawRect(r, new Color(Panel.R, Panel.G, Panel.B, hover ? 0.99f : 0.94f));
        DrawRect(r, new Color(rc.R, rc.G, rc.B, 0.13f));
        DrawRect(new Rect2(r.Position.X, r.Position.Y, 7 * u, r.Size.Y), typeCol);   // type ribbon (only typed accent on the body)

        // rarity top bar (taller, pulses for epic+) + matching bottom strip
        float barPulse = tier >= 3 ? 0.66f + 0.34f * Mathf.Abs(Mathf.Sin(_panelT * 4.5f)) : 1f;
        DrawRect(new Rect2(r.Position.X, r.Position.Y, r.Size.X, 10 * u), new Color(rc.R, rc.G, rc.B, barPulse));
        DrawRect(new Rect2(r.Position.X, rb - 5 * u, r.Size.X, 5 * u), new Color(rc.R, rc.G, rc.B, 0.85f));

        // epic+ : soft outer glow halo
        if (tier >= 3) Frame(new Rect2(r.Position.X - 3 * u, r.Position.Y - 3 * u, r.Size.X + 6 * u, r.Size.Y + 6 * u), new Color(rc.R, rc.G, rc.B, 0.28f), 3f * u);

        // rare+ : a bright ray of light sweeping across the card and over the title font (clipped to the card)
        if (tier >= 2)
        {
            float sp = _panelT % 2.0f, dur = 0.62f;
            if (sp < dur)
            {
                float prog = sp / dur, bwid = 30 * u, skew = 22 * u, aa = Mathf.Sin(prog * Mathf.Pi);
                float sx = r.Position.X - bwid + prog * (r.Size.X + 2 * bwid);
                float L = r.Position.X, R = rt;
                float tL = Mathf.Clamp(sx, L, R), tR = Mathf.Clamp(sx + bwid, L, R);
                float bL = Mathf.Clamp(sx - skew, L, R), bR = Mathf.Clamp(sx + bwid - skew, L, R);
                if (tR - tL > 0.5f && bR - bL > 0.5f)   // both edges need width, else the clamped quad collapses to a line → "invalid polygon, triangulation failed" (NEW)
                    SafePoly(new[] { new Vector2(tL, r.Position.Y), new Vector2(tR, r.Position.Y), new Vector2(bR, rb), new Vector2(bL, rb) }, new Color(1f, 0.98f, 0.88f, 0.22f * aa));
            }
        }
        // uncommon+ : a ray of light briefly hitting the tippy top-right corner
        if (tier >= 1)
        {
            float gp = _panelT % 1.7f, dur = 0.45f;
            if (gp < dur)
            {
                float a = Mathf.Sin(gp / dur * Mathf.Pi), s = 26 * u;
                var gl = new Color(1f, 1f, 0.93f, 0.72f * a);
                SafePoly(new[] { new Vector2(rt - s, r.Position.Y), new Vector2(rt, r.Position.Y), new Vector2(rt, r.Position.Y + s) }, new Color(gl.R, gl.G, gl.B, 0.5f * a));
                DrawLine(new Vector2(rt - s - 6 * u, r.Position.Y - 4 * u), new Vector2(rt + 4 * u, r.Position.Y + s + 6 * u), gl, 2.5f * u);
                DrawLine(new Vector2(rt - 8 * u, r.Position.Y - 6 * u), new Vector2(rt + 6 * u, r.Position.Y + 10 * u), gl, 1.5f * u);
            }
        }

        // border (legendary flickers electric blue-white)
        Color border = tier >= 4 ? rc.Lerp(new Color(0.75f, 0.88f, 1f), Mathf.Abs(Mathf.Sin(_panelT * 16f) * Mathf.Sin(_panelT * 5.3f))) : rc;
        Frame(r, hover ? Gold : border, (hover ? 4f : 3f) * u);

        float bx = r.Position.X + 16 * u, by = r.Position.Y;
        if (badge != "") T(_impact, new Vector2(bx, by + 38 * u), badge, 22 * u, rc, HorizontalAlignment.Left, -1, Mathf.RoundToInt(2 * u));
        T(_body, new Vector2(bx + (badge != "" ? 26 * u : 0), by + 35 * u), Rarities.Name(rar).ToUpper(), 13 * u, rc, HorizontalAlignment.Left, -1, Mathf.RoundToInt(2 * u));
        // rarity gems (tier+1), outlined so they read at a glance
        for (int i = 0; i <= tier; i++)
        {
            var gc = new Vector2(rt - 15 * u - i * 15 * u, by + 28 * u);
            Diamond(gc, 6.5f * u, Ink);
            Diamond(gc, 5 * u, rc);
        }
        T(_head, new Vector2(bx, by + 66 * u), title, 15 * u, rc, HorizontalAlignment.Left, r.Size.X - 28 * u, Mathf.RoundToInt(1 * u));
        if (typeLine != "")
        {
            var ts = _body.GetStringSize(typeLine, HorizontalAlignment.Left, -1, Mathf.RoundToInt(11 * u));
            DrawRect(new Rect2(bx - 3 * u, by + 78 * u, ts.X + 10 * u, 16 * u), new Color(typeCol.R, typeCol.G, typeCol.B, 0.9f));
            T(_body, new Vector2(bx + 2 * u, by + 90 * u), typeLine, 11 * u, new Color(0.06f, 0.04f, 0.09f), HorizontalAlignment.Left, -1, 0);
        }
        float descY = by + 110 * u;
        TMFit(_body, new Vector2(bx, descY), desc, 10.5f * u, new Color(0.88f, 0.86f, 0.94f), r.Size.X - 28 * u, rb - descY - 8 * u);   // (NEW) auto-fit so long descriptions never clip off the card
    }

    public Rect2 CardRect(int i)
    {
        var vp = GetViewportRect().Size; float u = U;
        int n = 3; float cw = 250 * u, chh = 188 * u, gap = 26 * u;   // (NEW) taller so descriptions fit (TMFit shrinks only if still needed)
        float total = n * cw + (n - 1) * gap, sx0 = vp.X / 2f - total / 2f, cy = vp.Y * 0.34f;
        return new Rect2(sx0 + i * (cw + gap), cy, cw, chh);
    }
    public int CardAt(Vector2 pos)
    {
        var g = Game.I; if (g?.Choices == null) return -1;
        for (int i = 0; i < g.Choices.Count; i++) if (CardRect(i).HasPoint(pos)) return i;
        return -1;
    }

    // (ATTRIBUTE pop-up) clickable node circles in the live perk-tree pop-up; returns the perk id clicked, or -1
    private readonly System.Collections.Generic.List<(Vector2 c, float r, int id)> _attuneNodes = new();
    public Rect2 AttuneDoneRect;
    public int AttuneNodeAt(Vector2 pos) { foreach (var f in _attuneNodes) if (f.c.DistanceTo(pos) <= f.r + 3f) return f.id; return -1; }

    // level-up action buttons: a DISABLE button under each card, and REROLL / LUCKY REROLL below the row
    public Rect2 BanBtnRect(int i) { var r = CardRect(i); float u = U; return new Rect2(r.Position.X + r.Size.X * 0.12f, r.Position.Y + r.Size.Y + 5 * u, r.Size.X * 0.76f, 24 * u); }
    public Rect2 RerollBtnRect() { var r = CardRect(0); float u = U; return new Rect2(r.Position.X, r.Position.Y + r.Size.Y + 38 * u, r.Size.X, 34 * u); }
    public Rect2 LuckBtnRect() { var r = CardRect(1); float u = U; return new Rect2(r.Position.X, r.Position.Y + r.Size.Y + 38 * u, r.Size.X, 34 * u); }
    public Rect2 Pick2BtnRect() { var r = CardRect(2); float u = U; return new Rect2(r.Position.X, r.Position.Y + r.Size.Y + 38 * u, r.Size.X, 34 * u); }
    public Rect2 DeclineBtnRect() { var r0 = CardRect(0); var r2 = CardRect(2); float u = U; float x0 = r0.Position.X, right = r2.Position.X + r2.Size.X, w = (right - x0) * 0.44f; return new Rect2((x0 + right) / 2f - w / 2f, r0.Position.Y + r0.Size.Y + 78 * u, w, 30 * u); }
    public int LevelUpBtn(Vector2 pos)   // 1 = reroll, 2 = lucky reroll, 3 = pick two, 4 = decline-for-gold, 100+i = disable card i, 0 = none
    {
        if (RerollBtnRect().HasPoint(pos)) return 1;
        if (LuckBtnRect().HasPoint(pos)) return 2;
        if (Pick2BtnRect().HasPoint(pos)) return 3;
        if (DeclineBtnRect().HasPoint(pos)) return 4;
        var g = Game.I; int n = g?.Choices?.Count ?? 0;
        for (int i = 0; i < n; i++) if (BanBtnRect(i).HasPoint(pos)) return 100 + i;
        return 0;
    }

    private (string, Color) CategoryOf(UpgradeCard card)
    {
        if (card.FinKind.HasValue) return ("SPELL · " + DamageTypes.Name(FinMeta.DType(card.FinKind.Value)), FinMeta.Col(card.FinKind.Value));
        if (card.ModKind.HasValue) return ("CHARGE MOD · " + DamageTypes.Name(ModMeta.DType(card.ModKind.Value)), ModMeta.Col(card.ModKind.Value));
        return ("BLESSING", GoldDim);
    }

    // (ATTRIBUTE pop-up) the live perk-tree: light owned nodes with attribute points; nodes glow as you buy them.
    private void DrawAttune(Game g, Player p, Vector2 vp, float u)
    {
        DrawRect(new Rect2(0, 0, vp.X, vp.Y), new Color(0.02f, 0.02f, 0.04f, 0.82f));
        var wcol = WitchModel.WitchColor(p.WitchIndex);
        T(_head, new Vector2(0f, vp.Y * 0.045f), "ATTRIBUTE PERKS", 32 * u, wcol.Lerp(Gold, 0.3f), HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(3 * u));
        T(_body, new Vector2(0f, vp.Y * 0.045f + 32 * u), $"{p.AttunePoints} point{(p.AttunePoints == 1 ? "" : "s")} to spend  —  light a glowing node (hover for detail)", 15 * u, GoldDim, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(1 * u));

        float gx0 = vp.X * 0.5f, gy0 = vp.Y * 0.52f;
        float colStride = vp.X * 0.72f / 11f, rowStride = vp.Y * 0.62f / 6f;
        Vector2 NP(int id) { var (cc, rr) = Perks.PosOf(id); return new Vector2(gx0 + (cc - 5.5f) * colStride, gy0 + (rr - 3f) * rowStride); }
        var nodes = Perks.Nodes(p.WitchIndex);
        var avail = new System.Collections.Generic.HashSet<int>(p.PerkAvailable());
        var mouse = GetGlobalMousePosition();
        float pulse = 0.6f + 0.4f * Mathf.Sin(Time.GetTicksMsec() * 0.006f);

        // edges (curved), lit when both ends are activated
        var bez = new Vector2[14];
        foreach (var nd in nodes)
        {
            var a = NP(nd.Id);
            foreach (int t in Perks.EdgesOf(nd.Id))
            {
                if (t < nd.Id) continue;
                var b = NP(t);
                bool live = p.PerkLit(nd.Id) && p.PerkLit(t);
                var lc = live ? new Color(wcol.R, wcol.G, wcol.B, 0.85f) : new Color(0.28f, 0.26f, 0.34f, 0.5f);
                var ctrl = new Vector2(b.X + (b.X - a.X) * 0.14f, a.Y * 0.4f + b.Y * 0.6f);
                for (int k = 0; k < bez.Length; k++) { float s = k / (float)(bez.Length - 1), it = 1f - s; bez[k] = it * it * a + 2f * it * s * ctrl + s * s * b; }
                DrawPolyline(bez, lc, (live ? 3f : 1.3f) * u, true);
            }
        }

        // nodes
        _attuneNodes.Clear();
        PerkNode hover = null; Vector2 hoverAt = Vector2.Zero;
        foreach (var nd in nodes)
        {
            var ctr = NP(nd.Id); float rad = (nd.Keystone ? 22f : 16f) * u;
            bool lit = p.PerkLit(nd.Id), can = avail.Contains(nd.Id), owned = Perks.Owned(p.WitchIndex, nd.Id);
            bool over = ctr.DistanceTo(mouse) <= rad + 3 * u;
            if (over) { hover = nd; hoverAt = ctr; }
            var ring = nd.Keystone ? Gold : wcol;
            DrawCircle(ctr, rad + 2 * u, new Color(0, 0, 0, 0.5f));
            if (lit) { DrawCircle(ctr, rad, new Color(ring.R * 0.4f, ring.G * 0.4f, ring.B * 0.4f, 1f)); DrawArc(ctr, rad, 0, Mathf.Tau, 30, ring, 3f * u, true); }
            else if (can) { DrawCircle(ctr, rad, new Color(0.12f, 0.11f, 0.17f, 1f)); DrawArc(ctr, rad, 0, Mathf.Tau, 30, new Color(ring.R, ring.G, ring.B, over ? 1f : pulse), (over ? 3f : 2.4f) * u, true); DrawArc(ctr, rad + 4 * u, 0, Mathf.Tau, 30, new Color(ring.R, ring.G, ring.B, 0.35f * pulse), 1.4f * u, true); _attuneNodes.Add((ctr, rad, nd.Id)); }
            else { DrawCircle(ctr, rad, new Color(0.07f, 0.065f, 0.1f, owned ? 1f : 0.75f)); DrawArc(ctr, rad, 0, Mathf.Tau, 24, new Color(ring.R, ring.G, ring.B, owned ? 0.4f : 0.16f), 1.4f * u, true); }
            if (nd.Keystone) DrawCircle(ctr, rad * 0.28f, new Color(Gold.R, Gold.G, Gold.B, lit ? 1f : (can ? 0.7f : 0.35f)));
            T(_body, new Vector2(ctr.X - 60 * u, ctr.Y + rad + 10 * u), nd.Name, 9.5f * u, lit ? Colors.White : (can ? new Color(0.88f, 0.84f, 0.7f) : new Color(0.5f, 0.48f, 0.56f)), HorizontalAlignment.Center, 120 * u, Mathf.RoundToInt(1 * u));
        }

        if (hover != null)
        {
            bool owned2 = Perks.Owned(p.WitchIndex, hover.Id);
            float tw = 262 * u, th = 44 * u, tx = Mathf.Clamp(hoverAt.X + 16 * u, 6 * u, vp.X - tw - 6 * u), ty = Mathf.Clamp(hoverAt.Y - th / 2f, 6 * u, vp.Y - th - 6 * u);
            var ring = hover.Keystone ? Gold : wcol;
            DrawRect(new Rect2(tx - 2 * u, ty - 2 * u, tw + 4 * u, th + 4 * u), new Color(0, 0, 0, 0.9f));
            DrawRect(new Rect2(tx, ty, tw, th), new Color(0.09f, 0.085f, 0.13f, 1f)); Frame(new Rect2(tx, ty, tw, th), ring, 1.5f * u);
            T(_body, new Vector2(tx + 9 * u, ty + 8 * u), (hover.Keystone ? "★ " : "") + hover.Name, 12.5f * u, ring.Lerp(Colors.White, 0.3f), HorizontalAlignment.Left, tw - 14 * u, Mathf.RoundToInt(1 * u));
            T(_body, new Vector2(tx + 9 * u, ty + 24 * u), owned2 ? hover.Desc : "not unlocked — buy it with gold on the coven page", 10.5f * u, owned2 ? new Color(0.88f, 0.84f, 0.7f) : GoldDim, HorizontalAlignment.Left, tw - 14 * u, Mathf.RoundToInt(1 * u));
        }

        // Done button
        float bw = 220 * u, bh = 40 * u;
        AttuneDoneRect = new Rect2(vp.X / 2f - bw / 2f, vp.Y - 62 * u, bw, bh);
        bool dOver = AttuneDoneRect.HasPoint(mouse);
        DrawRect(AttuneDoneRect, new Color(wcol.R * 0.25f, wcol.G * 0.25f, wcol.B * 0.25f, 0.95f));
        Frame(AttuneDoneRect, new Color(wcol.R, wcol.G, wcol.B, dOver ? 1f : 0.8f), 1.8f * u);
        T(_body, new Vector2(AttuneDoneRect.Position.X, AttuneDoneRect.Position.Y + 11 * u), "DONE  (Esc)", 16 * u, Colors.White, HorizontalAlignment.Center, bw, Mathf.RoundToInt(1 * u));
    }

    private void DrawLevelUp(Game g, Vector2 c, Vector2 vp, float u)
    {
        DrawRect(new Rect2(0, 0, vp.X, vp.Y), new Color(0, 0, 0, 0.6f));
        bool loot = g.LootMode;
        bool reward = g.RewardMode;
        string title = "LEVEL UP";
        string sub = "choose a gift";
        var titleCol = Gold;
        if (reward)
        {
            titleCol = g.RewardCat == 0 ? DamageTypes.Col(DamageType.Lunar) : g.RewardCat == 1 ? DamageTypes.Col(DamageType.Curse) : DamageTypes.Col(DamageType.Holy);
            title = g.RewardCat == 0 ? "WARD COMPLETE" : g.RewardCat == 1 ? "SUMMONING BROKEN" : "CLEANSED";
            sub = g.RewardCat == 0 ? "a blessing \u2014 attribute only" : g.RewardCat == 1 ? "a spell combo of your choosing" : "a charged-cast modifier";
        }
        else if (loot)
        {
            titleCol = new Color(1f, 0.84f, 0.3f);
            title = "GOBLIN HOARD";
            sub = $"plunder two \u2014 {Rarities.Name(g.LootMin).ToLower()} and above";
        }
        T(_head, new Vector2(0f, vp.Y * 0.2f), title, 40 * u, titleCol, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(4 * u));
        T(_body, new Vector2(0f, vp.Y * 0.2f + 30 * u), sub, 16 * u, GoldDim, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(2 * u));

        var mouse = GetGlobalMousePosition();
        for (int i = 0; i < g.Choices.Count; i++)
        {
            var card = g.Choices[i]; var r = CardRect(i);
            bool spinning = _rollActive && _panelT < RollLockAt(i);
            if (spinning) { DrawSpinningCard(r, i, (int)card.Rarity, u); continue; }   // still whirling — no face, no buttons yet
            // (NEW) lock PUNCH: a brief scale overshoot the instant this card slams to rest
            float sinceLock = _panelT - RollLockAt(i);
            if (_rollActive && sinceLock < 0.24f)
            {
                float k = Mathf.Sin(sinceLock / 0.24f * Mathf.Pi) * 0.07f;   // grow-then-settle
                float ex = r.Size.X * k, ey = r.Size.Y * k;
                r = new Rect2(r.Position.X - ex * 0.5f, r.Position.Y - ey * 0.5f, r.Size.X + ex, r.Size.Y + ey);
            }
            var (typeLine, typeCol) = CategoryOf(card);
            DrawCardPanel(r, card.Rarity, card.Title, typeLine, typeCol, card.Desc, (i + 1).ToString(), r.HasPoint(mouse), u);
            // (NEW) lock FLASH — a bright ring + wash bursts out the instant the reel stops, scaled by rarity
            if (_rollActive && sinceLock < 0.30f)
            {
                float fk = sinceLock / 0.30f;
                var fc = Rarities.Col(card.Rarity);
                var ctr = new Vector2(r.Position.X + r.Size.X * 0.5f, r.Position.Y + r.Size.Y * 0.5f);
                float ringR = Mathf.Lerp(r.Size.X * 0.2f, r.Size.X * (0.62f + 0.1f * (int)card.Rarity), fk);
                DrawArc(ctr, ringR, 0f, Mathf.Tau, 28, new Color(fc.R, fc.G, fc.B, (1f - fk) * 0.8f), (3.5f - 3f * fk) * u, true);
                DrawRect(r, new Color(1f, 0.97f, 0.85f, (1f - fk) * (0.10f + 0.06f * (int)card.Rarity)));   // white pop, brighter for rarer
            }
            if (RollBusy) continue;   // don't show the DISABLE button until the whole roll has settled
            // DISABLE button under each card (bans the whole rarity family for this run; not for uniques)
            var br = BanBtnRect(i); bool bcan = !card.Unique && g.Gold >= g.BanCost; bool bhov = br.HasPoint(mouse);
            var bcol = card.Unique ? new Color(0.25f, 0.25f, 0.28f, 0.7f) : (bhov && bcan ? new Color(0.72f, 0.2f, 0.2f, 0.96f) : (bcan ? new Color(0.5f, 0.16f, 0.16f, 0.85f) : new Color(0.32f, 0.14f, 0.14f, 0.7f)));
            DrawRect(br, bcol);
            T(_body, new Vector2(br.Position.X, br.Position.Y + 4 * u), card.Unique ? "UNIQUE" : $"DISABLE  {g.BanCost}g", 11 * u, Colors.White, HorizontalAlignment.Center, br.Size.X, Mathf.RoundToInt(1 * u));
        }
        if (!RollBusy)
        {
            var rr = RerollBtnRect(); bool rcan = g.Gold >= g.RerollCost; bool rhov = rr.HasPoint(mouse);
            DrawRect(rr, rhov && rcan ? new Color(0.22f, 0.42f, 0.26f, 0.96f) : (rcan ? new Color(0.16f, 0.32f, 0.2f, 0.85f) : new Color(0.22f, 0.22f, 0.24f, 0.7f)));
            T(_body, new Vector2(rr.Position.X, rr.Position.Y + 8 * u), $"REROLL  {g.RerollCost}g", 13 * u, Colors.White, HorizontalAlignment.Center, rr.Size.X, Mathf.RoundToInt(1 * u));
            var lr = LuckBtnRect(); bool lcan = g.Gold >= g.LuckRerollCost; bool lhov = lr.HasPoint(mouse);
            DrawRect(lr, lhov && lcan ? new Color(0.4f, 0.34f, 0.1f, 0.96f) : (lcan ? new Color(0.3f, 0.26f, 0.08f, 0.85f) : new Color(0.22f, 0.22f, 0.24f, 0.7f)));
            T(_body, new Vector2(lr.Position.X, lr.Position.Y + 8 * u), $"LUCKY REROLL  {g.LuckRerollCost}g", 12 * u, new Color(1f, 0.9f, 0.42f), HorizontalAlignment.Center, lr.Size.X, Mathf.RoundToInt(1 * u));
            var pr = Pick2BtnRect(); bool pcan = !g.Pick2Armed && g.Gold >= g.Pick2Cost; bool phov = pr.HasPoint(mouse);
            DrawRect(pr, g.Pick2Armed ? new Color(0.25f, 0.4f, 0.5f, 0.9f) : (phov && pcan ? new Color(0.2f, 0.4f, 0.5f, 0.96f) : (pcan ? new Color(0.14f, 0.3f, 0.38f, 0.85f) : new Color(0.22f, 0.22f, 0.24f, 0.7f))));
            T(_body, new Vector2(pr.Position.X, pr.Position.Y + 8 * u), g.Pick2Armed ? "PICK TWO — active" : $"PICK TWO  {g.Pick2Cost}g", 12 * u, new Color(0.6f, 0.85f, 1f), HorizontalAlignment.Center, pr.Size.X, Mathf.RoundToInt(1 * u));
            // DECLINE for gold — forgo this pick entirely
            var dr = DeclineBtnRect(); bool dhov = dr.HasPoint(mouse);
            DrawRect(dr, dhov ? new Color(0.42f, 0.35f, 0.12f, 0.96f) : new Color(0.3f, 0.26f, 0.1f, 0.85f));
            Frame(dr, dhov ? Gold : GoldDim, (dhov ? 2.5f : 1.5f) * u);
            T(_body, new Vector2(dr.Position.X, dr.Position.Y + 7 * u), $"DECLINE — take {g.DeclineGold}g  (0)", 12 * u, new Color(1f, 0.9f, 0.42f), HorizontalAlignment.Center, dr.Size.X, Mathf.RoundToInt(1 * u));
        }
        T(_body, new Vector2(0f, vp.Y * 0.34f + 290 * u), "click a card   ·   press  1 / 2 / 3   ·   0 to decline for gold", 14 * u, GoldDim, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(2 * u));
    }

    // ===== swap =====
    public Rect2 SwapRect(int i)
    {
        var g = Game.I; var p = g?.Player; if (p == null) return new Rect2();
        int n = g.SwapIsFin ? p.Fin.Count : p.Mods.Count;
        var vp = GetViewportRect().Size; float u = U;
        float cw = 214 * u, chh = 150 * u, gap = 20 * u;
        float total = n * cw + (n - 1) * gap, sx0 = vp.X / 2f - total / 2f, cy = vp.Y * 0.46f;
        return new Rect2(sx0 + i * (cw + gap), cy, cw, chh);
    }
    public Rect2 SwapSkipRect()
    {
        var vp = GetViewportRect().Size; float u = U;
        return new Rect2(vp.X / 2f - 110 * u, vp.Y * 0.46f + 168 * u, 220 * u, 34 * u);
    }
    public int SwapAt(Vector2 pos)
    {
        var g = Game.I; var p = g?.Player; if (p == null) return -2;
        int n = g.SwapIsFin ? p.Fin.Count : p.Mods.Count;
        for (int i = 0; i < n; i++) if (SwapRect(i).HasPoint(pos)) return i;
        if (SwapSkipRect().HasPoint(pos)) return -1;
        return -2;
    }

    private void DrawSwap(Game g, Player p, Vector2 c, Vector2 vp, float u)
    {
        DrawRect(new Rect2(0, 0, vp.X, vp.Y), new Color(0, 0, 0, 0.74f));
        bool fin = g.SwapIsFin;
        T(_head, new Vector2(0f, vp.Y * 0.12f), fin ? "SPELL SLOTS FULL" : "MODIFIER SLOTS FULL", 32 * u, Gold, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(3 * u));
        T(_body, new Vector2(0f, vp.Y * 0.12f + 28 * u), "choose one to replace — or keep what you have", 15 * u, GoldDim, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(2 * u));

        var inRect = new Rect2(c.X - 130 * u, vp.Y * 0.21f, 260 * u, 132 * u);
        if (fin) { var t = g.SwapFin; DrawCardPanel(inRect, g.SwapRarity, "NEW · " + FinMeta.Name(t), "SPELL · " + DamageTypes.Name(FinMeta.DType(t)), FinMeta.Col(t), "incoming combo spell", "", true, u); }
        else { var t = g.SwapMod; DrawCardPanel(inRect, g.SwapRarity, "NEW · " + ModMeta.Name(t), "CHARGE MOD · " + DamageTypes.Name(ModMeta.DType(t)), ModMeta.Col(t), "incoming charge modifier", "", true, u); }

        var mouse = GetGlobalMousePosition();
        string ttTitle = null, ttBody = null; Color ttCol = Gold;
        if (inRect.HasPoint(mouse))
        {
            if (fin) { ttTitle = FinMeta.Name(g.SwapFin); ttBody = FinMeta.Desc(g.SwapFin); ttCol = FinMeta.Col(g.SwapFin); }
            else { ttTitle = ModMeta.Name(g.SwapMod); ttBody = ModMeta.Desc(g.SwapMod); ttCol = ModMeta.Col(g.SwapMod); }
        }
        int n = fin ? p.Fin.Count : p.Mods.Count;
        for (int i = 0; i < n; i++)
        {
            var r = SwapRect(i);
            bool hov = r.HasPoint(mouse);
            if (fin) { var f = p.Fin[i]; DrawCardPanel(r, f.Rarity, FinMeta.Name(f.Type), "SPELL · " + DamageTypes.Name(FinMeta.DType(f.Type)), FinMeta.Col(f.Type), $"every {f.Every} combo", (i + 1).ToString(), hov, u); if (hov) { ttTitle = FinMeta.Name(f.Type); ttBody = FinMeta.Desc(f.Type); ttCol = FinMeta.Col(f.Type); } }
            else { var mo = p.Mods[i]; DrawCardPanel(r, mo.Rarity, ModMeta.Name(mo.Type), "CHARGE MOD · " + DamageTypes.Name(ModMeta.DType(mo.Type)), ModMeta.Col(mo.Type), "equipped", (i + 1).ToString(), hov, u); if (hov) { ttTitle = ModMeta.Name(mo.Type); ttBody = ModMeta.Desc(mo.Type); ttCol = ModMeta.Col(mo.Type); } }
        }

        var skip = SwapSkipRect();
        bool sh = skip.HasPoint(mouse);
        DrawRect(skip, new Color(Panel.R, Panel.G, Panel.B, 0.95f));
        Frame(skip, sh ? Gold : GoldDim, (sh ? 2.5f : 1.5f) * u);
        T(_body, new Vector2(skip.Position.X + skip.Size.X / 2f, skip.Position.Y + 23 * u), "Keep current  (0)", 14 * u, Gold, HorizontalAlignment.Center, skip.Size.X, Mathf.RoundToInt(2 * u));
        if (ttTitle != null) DrawTooltip(mouse, vp, ttTitle, ttBody, ttCol, u);
    }

    // ===== element attunement chooser =====
    public Rect2 ElementRect(int i)
    {
        var vp = GetViewportRect().Size; float u = U;
        float tw = 168 * u, th = 100 * u, gx = 20 * u, gy = 18 * u;
        int col = i % 3, row = i / 3;
        float totalW = 3 * tw + 2 * gx, x0 = vp.X / 2f - totalW / 2f, y0 = vp.Y * 0.40f;
        return new Rect2(x0 + col * (tw + gx), y0 + row * (th + gy), tw, th);
    }
    public int ElementAt(Vector2 pos)
    {
        for (int i = 0; i < Game.Elements.Length; i++) if (ElementRect(i).HasPoint(pos)) return i;
        return -1;
    }

    private void DrawElement(Game g, Player p, Vector2 c, Vector2 vp, float u)
    {
        DrawRect(new Rect2(0, 0, vp.X, vp.Y), new Color(0.02f, 0.01f, 0.05f, 0.86f));
        bool prim = g.PendingAttune == 0;
        bool brand = g.PendingAttune == 2;
        bool graft = g.PendingAttune == 3;
        string head = graft ? "GRAFTED ELEMENT — TREE-ENTS" : brand ? "CURSEBRAND — 2ND CURSE TYPE" : (prim ? "ATTUNE — PRIMARY" : "ATTUNE — SECONDARY");
        string sub  = graft ? "your tree-ents' explosions will deal this element (and take on its look)" : brand ? "cursed foes will take your bonus damage from this type too" : (prim ? "choose a new element for your left-click" : "choose a new element for your charged right-click");
        T(_head, new Vector2(0f, vp.Y * 0.22f), head, 34 * u, Gold, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(4 * u));
        T(_body, new Vector2(0f, vp.Y * 0.22f + 30 * u), sub, 15 * u, GoldDim, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(2 * u));
        var current = graft ? p.EntElement : brand ? (p.CurseBonusType2 >= 0 ? (DamageType)p.CurseBonusType2 : DamageType.Curse) : (prim ? p.PrimaryType : p.SecondaryType);

        var mouse = GetGlobalMousePosition();
        for (int i = 0; i < Game.Elements.Length; i++)
        {
            var ty = Game.Elements[i];
            var col = DamageTypes.Col(ty);
            var r = ElementRect(i);
            bool hover = r.HasPoint(mouse);
            DrawRect(r, new Color(Panel.R, Panel.G, Panel.B, 0.95f));
            DrawRect(r, new Color(col.R, col.G, col.B, hover ? 0.34f : 0.2f));
            Frame(r, hover ? Gold : col, (hover ? 4f : 2.5f) * u);
            Diamond(new Vector2(r.Position.X + r.Size.X / 2f, r.Position.Y + 34 * u), 13 * u, col);
            T(_head, new Vector2(r.Position.X, r.Position.Y + 70 * u), DamageTypes.Name(ty).ToUpper(), 16 * u, Gold, HorizontalAlignment.Center, r.Size.X, Mathf.RoundToInt(2 * u));
            T(_body, new Vector2(r.Position.X + 8 * u, r.Position.Y + 18 * u), $"{i + 1}", 13 * u, GoldDim, HorizontalAlignment.Left, -1, Mathf.RoundToInt(1 * u));
            if (ty == current) T(_body, new Vector2(r.Position.X, r.Position.Y + 88 * u), "current", 10 * u, col, HorizontalAlignment.Center, r.Size.X, Mathf.RoundToInt(1 * u));
        }
        T(_body, new Vector2(0f, vp.Y * 0.40f + 250 * u), "click an element   ·   or press  1 – 8", 14 * u, GoldDim, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(2 * u));
    }

    // ===== Tab grimoire =====
    private void DrawStats(Game g, Player p, Vector2 c, Vector2 vp, float u)
    {
        DrawRect(new Rect2(0, 0, vp.X, vp.Y), new Color(0.02f, 0.01f, 0.05f, 0.92f));
        T(_head, new Vector2(0f, 50 * u), "THE MOON WITCH", 36 * u, Gold, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(4 * u));
        T(_body, new Vector2(0f, 80 * u), "Grimoire  ·  primary fire: Lunar", 14 * u, GoldDim, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(2 * u));

        var s = p.S;
        var mouse = GetGlobalMousePosition();
        string ttTitle = null, ttBody = null; Color ttCol = Gold;
        float lh = 22 * u, top = 122 * u, colW = 300 * u;
        float lx = c.X - colW - 34 * u, rx = c.X + 34 * u;
        int ob = Mathf.RoundToInt(2 * u), o1 = Mathf.RoundToInt(1 * u);

        float Sec(float x, float y, string t) { T(_head, new Vector2(x, y + 4 * u), t, 16 * u, Gold, HorizontalAlignment.Left, -1, ob); DrawRect(new Rect2(x, y + 11 * u, colW, Mathf.Max(1f, 1.5f * u)), new Color(Gold.R, Gold.G, Gold.B, 0.35f)); return y + lh * 1.7f; }
        float St(float x, float y, string l, string v) { T(_body, new Vector2(x, y), l, 13 * u, GoldDim, HorizontalAlignment.Left, -1, o1); T(_body, new Vector2(x + colW, y), v, 13 * u, ValCol, HorizontalAlignment.Right, -1, o1); return y + lh; }

        float yL = top;
        yL = Sec(lx, yL, "POWER");
        yL = St(lx, yL, "Primary / Secondary", $"{DamageTypes.Name(p.PrimaryType)} / {DamageTypes.Name(p.SecondaryType)}");
        yL = St(lx, yL, "Spell Power", $"{Mathf.RoundToInt(s.Atk * 100)}%");
        yL = St(lx, yL, "Cast Rate", $"{(1f / s.FireCd):0.0}/s");
        yL = St(lx, yL, "Charge Speed", $"{s.ChargeSpeed:0.0}x");
        yL = St(lx, yL, "Max Charged", $"{s.MaxCharge:0.0}x");
        yL = St(lx, yL, "Pierce", $"{s.Pierce}");
        yL = St(lx, yL, "Lifesteal", $"{Mathf.RoundToInt(s.Lifesteal * 100)}%");
        yL = St(lx, yL, "Crit", $"{Mathf.RoundToInt(s.CritChance * 100)}% · +{Mathf.RoundToInt(s.CritDamage * 100)}% dmg");
        yL = St(lx, yL, "Spell Range / Area", $"{s.SpellRange:0.00}x / {s.SpellArea:0.00}x");
        yL = St(lx, yL, "Projectile Speed", $"{s.ProjSpeed:0.00}x");
        yL = St(lx, yL, "Luck", $"{s.Luck:0.0}");
        yL += lh * 1.1f;
        yL = Sec(lx, yL, "VITALS & COMBO");
        yL = St(lx, yL, "Health", $"{Mathf.CeilToInt(p.Hp)}/{Mathf.RoundToInt(s.MaxHp)}");
        yL = St(lx, yL, "Shield", $"{Mathf.CeilToInt(p.Shield)}/{Mathf.RoundToInt(p.MaxShield)} ({s.ShieldRegen:0.0}/s)");
        yL = St(lx, yL, "Mana", $"{Mathf.FloorToInt(p.Mana)}/{Mathf.RoundToInt(s.ManaMax)} (+{s.ManaGain:0.00})");
        yL = St(lx, yL, "Move Speed", $"{s.Speed:0.0}");
        yL = St(lx, yL, "Dash", $"{s.DashCharges}x · {s.DashDist:0}u · {s.DashCd:0.0}s");
        yL = St(lx, yL, "Combo Power", $"+{(s.ComboPow * 100):0.0}%/stk");
        yL = St(lx, yL, "Combo Cap/Win", $"{s.ComboCap} · {s.ComboWindow:0.00}s");
        yL = St(lx, yL, "Longest Combo", $"x{p.BestCombo}");

        float yR = top;
        yR = Sec(rx, yR, $"SPELL COMBOS   {p.Fin.Count}/{s.FinSlots}");
        for (int i = 0; i < s.FinSlots; i++)
        {
            string key = i < KL.Length ? KL[i] : "?";
            if (i < p.Fin.Count)
            {
                var f = p.Fin[i];
                T(_body, new Vector2(rx, yR), $"[{KeyName(f.Bind)}]  {FinMeta.Name(f.Type)}", 13 * u, Gold, HorizontalAlignment.Left, -1, ob);
                T(_body, new Vector2(rx + colW, yR), $"{Rarities.Name(f.Rarity)} · {DamageTypes.Name(FinMeta.DType(f.Type))}", 11 * u, FinMeta.Col(f.Type), HorizontalAlignment.Right, -1, o1);
            }
            else T(_body, new Vector2(rx, yR), $"[{key}]  — empty —", 12 * u, new Color(1, 1, 1, 0.3f));
            if (i < p.Fin.Count && new Rect2(rx, yR - 14 * u, colW, lh).HasPoint(mouse)) { var ff = p.Fin[i]; ttTitle = FinMeta.Name(ff.Type); ttBody = FinMeta.Desc(ff.Type); ttCol = FinMeta.Col(ff.Type); }
            yR += lh;
        }
        yR += lh * 1.1f;
        yR = Sec(rx, yR, $"CHARGE MODIFIERS   {p.Mods.Count}/{s.ModSlots}");
        for (int i = 0; i < s.ModSlots; i++)
        {
            if (i < p.Mods.Count)
            {
                var mo = p.Mods[i];
                T(_body, new Vector2(rx, yR), ModMeta.Name(mo.Type), 13 * u, Gold, HorizontalAlignment.Left, -1, ob);
                T(_body, new Vector2(rx + colW, yR), $"{Rarities.Name(mo.Rarity)} · {DamageTypes.Name(ModMeta.DType(mo.Type))}", 11 * u, ModMeta.Col(mo.Type), HorizontalAlignment.Right, -1, o1);
            }
            else T(_body, new Vector2(rx, yR), "— empty —", 12 * u, new Color(1, 1, 1, 0.3f));
            if (i < p.Mods.Count && new Rect2(rx, yR - 14 * u, colW, lh).HasPoint(mouse)) { var mm = p.Mods[i]; ttTitle = ModMeta.Name(mm.Type); ttBody = ModMeta.Desc(mm.Type); ttCol = ModMeta.Col(mm.Type); }
            yR += lh;
        }
        yR += lh * 1.1f;
        yR = Sec(rx, yR, "JOURNEY");
        yR = St(rx, yR, "Level", $"{p.Level}");
        yR = St(rx, yR, "Wave", $"{g.Wave}");
        yR = St(rx, yR, "Score", $"{g.Score}");

        if (ttTitle != null) DrawTooltip(mouse, vp, ttTitle, ttBody, ttCol, u);
        T(_body, new Vector2(0f, vp.Y - 36 * u), "Tab to close", 15 * u, GoldDim, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(2 * u));
    }
}
