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

    public Rect2 RPauseMusic, RPauseSens, RPauseResume, RPauseDmg, ROver, RChangeWitch;
    public Rect2 ROverRetry, ROverCharSelect, ROverEnd;   // (NEW) MP game-over host options
    public Rect2 RPauseBloom, RPauseSsao, RPauseSsil;   // (NEW) post-processing toggles
    public Rect2[] RPauseGfx = new Rect2[3];             // (NEW) LOW / MED / HIGH preset buttons
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
        if (g != null && g.ChoiceGen != _gen) { _gen = g.ChoiceGen; _panelT = 0f; }
        _panelT += dt;
        for (int i = _pops.Count - 1; i >= 0; i--) { var p = _pops[i]; p.T += dt; _pops[i] = p; if (p.T >= PopMax) _pops.RemoveAt(i); }
        if (_flourT > 0f) _flourT -= dt;
        if (_comboPopT > 0f) _comboPopT -= dt;   // (NEW)
        if (_breakT > 0f) _breakT -= dt;
        QueueRedraw();
    }

    private float U => Mathf.Clamp(GetViewportRect().Size.Y / 900f, 0.62f, 2.4f);

    private void T(Font f, Vector2 p, string s, float size, Color col, HorizontalAlignment a = HorizontalAlignment.Left, float w = -1, int outline = 0)
    {
        int fs = Mathf.Max(1, Mathf.RoundToInt(size));
        if (outline > 0) DrawStringOutline(f, p, s, a, w, fs, outline, Ink);
        DrawString(f, p, s, a, w, fs, col);
    }

    // a top-right radar: player-relative (up = facing), dots for nearby threats & points of interest
    private void DrawMinimap(Game g, Player p, Vector2 vp, float u)
    {
        float radius = 72 * u, range = 46f;
        float cx = vp.X - radius - 18 * u, cy = radius + 18 * u;
        var ctr = new Vector2(cx, cy);
        DrawCircle(ctr, radius + 3 * u, new Color(0, 0, 0, 0.45f));
        DrawCircle(ctr, radius, new Color(0.05f, 0.06f, 0.10f, 0.72f));
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

        foreach (var r in g.Rituals)
        { if (r == null || !GodotObject.IsInstanceValid(r)) continue; var sp = Plot(r.GlobalPosition, out var ir); if (ir) DrawCircle(sp, 4 * u, new Color(0.8f, 0.5f, 1f, 0.95f)); }
        foreach (var ch in g.Chests)
        { if (ch == null || !GodotObject.IsInstanceValid(ch)) continue; var sp = Plot(ch.GlobalPosition, out var ir); if (ir) DrawRect(new Rect2(sp.X - 3 * u, sp.Y - 3 * u, 6 * u, 6 * u), new Color(1f, 0.82f, 0.3f, 0.95f)); }
        if (g.VendorMystic != null && GodotObject.IsInstanceValid(g.VendorMystic))
        { var sp = Plot(g.VendorMystic.GlobalPosition, out var ir); if (ir) DrawCircle(sp, 3.5f * u, new Color(0.4f, 0.95f, 0.9f, 0.95f)); }
        if (g.VendorScroll != null && GodotObject.IsInstanceValid(g.VendorScroll))
        { var sp = Plot(g.VendorScroll.GlobalPosition, out var ir); if (ir) DrawCircle(sp, 3.5f * u, new Color(0.6f, 0.9f, 0.5f, 0.95f)); }
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
        float w = 252 * u, pad = 10 * u, lineH = 15 * u;
        var words = body.Split(' ');
        var lines = new System.Collections.Generic.List<string>();
        string cur = "";
        foreach (var wd in words)
        {
            string next = cur.Length == 0 ? wd : cur + " " + wd;
            if (next.Length > 38) { if (cur.Length > 0) lines.Add(cur); cur = wd; }
            else cur = next;
        }
        if (cur.Length > 0) lines.Add(cur);
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

    // DrawColoredPolygon throws "Invalid polygon data, triangulation failed" on degenerate input — a zero-size
    // marker (s≈0), NaN/Inf coords, or collinear points. Every dynamically-sized polygon goes through here. (NEW)
    private void SafePoly(Vector2[] p, Color col)
    {
        if (p == null || p.Length < 3) return;
        foreach (var v in p) if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || Mathf.Abs(v.X) > 1e6f || Mathf.Abs(v.Y) > 1e6f) return;   // off-screen extremes lose precision
        for (int i = 0; i < p.Length; i++)
            for (int j = i + 1; j < p.Length; j++)
                if (p[i].DistanceSquaredTo(p[j]) < 4.0f) return;   // points within ~2px → triangulation yields no indices
        float area = 0f;
        for (int i = 0; i < p.Length; i++) { var a = p[i]; var b = p[(i + 1) % p.Length]; area += a.X * b.Y - b.X * a.Y; }
        if (Mathf.Abs(area) < 8.0f) return;   // ~zero area (thin/collinear) → triangulation would fail
        DrawColoredPolygon(p, col);
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

        if (g.State == GameState.Lobby) return;
        if (g.State == GameState.CharSelect) { DrawToast(g, vp, u); return; }   // the CharSelect Control node draws the roster now

        T(_head, new Vector2(m, m + 24 * u), $"Wave {g.Wave}", 26 * u, Gold, HorizontalAlignment.Left, -1, Mathf.RoundToInt(3 * u));
        T(_body, new Vector2(m, m + 50 * u), $"{g.Score} banished", 15 * u, GoldDim, HorizontalAlignment.Left, -1, Mathf.RoundToInt(2 * u));

        if (g.Goblin != null && GodotObject.IsInstanceValid(g.Goblin))
        {
            float pulse = 0.6f + 0.4f * Mathf.Sin(Time.GetTicksMsec() * 0.012f);
            var gc = new Color(1f, 0.84f, 0.3f, pulse);
            string gtxt = g.GoblinTime < 0f ? "\u2726 LOOT GOBLIN  —  strike it to start the chase!" : $"\u2726 LOOT GOBLIN  {Mathf.Max(0f, g.GoblinTime):0.0}s";
            T(_head, new Vector2(0f, m + 24 * u), gtxt, 18 * u, gc, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(3 * u));
        }

        // Gold (persists across runs)
        var goldCol = new Color(1f, 0.82f, 0.32f);
        T(_head, new Vector2(vp.X - 230 * u, m), $"\u29c9 {g.Gold}", 22 * u, goldCol, HorizontalAlignment.Right, 210 * u, Mathf.RoundToInt(3 * u));
        if (g.GoldFlash > 0f)
            T(_body, new Vector2(vp.X - 230 * u, m + 26 * u), $"+{g.LastWaveGold}", 15 * u, new Color(1f, 0.82f, 0.32f, Mathf.Clamp(g.GoldFlash, 0f, 1f)), HorizontalAlignment.Right, 210 * u, Mathf.RoundToInt(2 * u));

        // Day/night phase + countdown (top center)
        var phaseCol = g.IsNight ? new Color(0.6f, 0.65f, 1f) : new Color(1f, 0.85f, 0.6f);
        T(_head, new Vector2(0f, m), $"{g.PhaseName}  ·  {Mathf.CeilToInt(g.PhaseTimeLeft)}s", 16 * u, phaseCol, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(2 * u));
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
        if (p != null && g.State == GameState.Playing) DrawMinimap(g, p, vp, u);
        if (p != null) DrawRituals(u);
        if (p != null && g.InIntermission) DrawIntermission(g, vp, u);
        if (p != null && p.Downed) DrawDowned(g, vp, u);
        if (p != null && g.State == GameState.Playing && !g.WorldRunning) DrawWaiting(g, vp, u);
        if (p != null && g.HoldEActive) DrawHoldE(g, vp, u);

        DrawPops(u);
        DrawFlourish(u);

        if (_bannerT > 0)
        {
            float a = Mathf.Clamp(_bannerT, 0, 1);
            T(_head, new Vector2(0f, vp.Y * 0.26f), _banner, 46 * u, new Color(Gold.R, Gold.G, Gold.B, a), HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(4 * u));
        }

        if (g.State == GameState.LevelUp && g.Choices != null) DrawLevelUp(g, c, vp, u);
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
        if (g.State == GameState.Pause) DrawPause(g, vp, u);

        if (g.State == GameState.Over)
        {
            DrawRect(new Rect2(0, 0, vp.X, vp.Y), new Color(0, 0, 0, 0.62f));
            T(_head, new Vector2(0f, c.Y - 6 * u), "YOU FELL", 52 * u, new Color(0.95f, 0.4f, 0.45f), HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(4 * u));
            T(_body, new Vector2(0f, c.Y + 36 * u), $"Wave {g.Wave}  ·  {g.Score} banished  ·  best combo x{p?.BestCombo}", 18 * u, Gold, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(2 * u));
            ROver = RChangeWitch = ROverRetry = ROverCharSelect = ROverEnd = new Rect2();
            bool mp = g.NetMgr != null && g.NetMgr.Active;
            var viol = DamageTypes.Col(DamageType.Lunar);
            if (!mp)   // solo — scene reload is fine
            {
                ROver = new Rect2(vp.X / 2f - 130 * u, c.Y + 64 * u, 260 * u, 32 * u);
                Frame(ROver, new Color(0.95f, 0.4f, 0.45f, 0.6f), 1.5f * u);
                T(_body, new Vector2(0f, c.Y + 70 * u), "Rise again   [Enter]", 16 * u, GoldDim, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(2 * u));
                RChangeWitch = new Rect2(vp.X / 2f - 130 * u, c.Y + 104 * u, 260 * u, 30 * u);
                Frame(RChangeWitch, new Color(viol.R, viol.G, viol.B, 0.6f), 1.5f * u);
                T(_body, new Vector2(0f, c.Y + 109 * u), "Change witch   [C]", 15 * u, GoldDim, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(1 * u));
            }
            else if (g.NetMgr.IsHost)   // host decides for the whole group (keeps everyone connected)
            {
                float bw = 300 * u, bh = 34 * u, bx = vp.X / 2f - bw / 2f, by = c.Y + 60 * u;
                void Opt(ref Rect2 r, string label, Color col)
                {
                    r = new Rect2(bx, by, bw, bh); Frame(r, new Color(col.R, col.G, col.B, 0.7f), 1.5f * u);
                    T(_body, new Vector2(0f, by + 8 * u), label, 16 * u, GoldDim, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(1 * u));
                    by += 44 * u;
                }
                Opt(ref ROverRetry, "Retry — same witches", new Color(0.5f, 0.9f, 0.55f));
                Opt(ref ROverCharSelect, "Character Select", viol);
                Opt(ref ROverEnd, "End Game", new Color(0.95f, 0.4f, 0.45f));
            }
            else   // client waits for the host's call
                T(_body, new Vector2(0f, c.Y + 74 * u), "waiting for the host to decide…", 17 * u, GoldDim, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(1 * u));
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

    private static string UltName(Player.UltKind k) => k switch
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
        _ => ""
    };

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
        if (Game.I.BossTokens > 0f)
            T(_body, new Vector2(x, y + 14 * u), $"\u25d0 {Game.I.BossTokens:0.#} tokens  ·  [U] altar", 12 * u, GoldDim, HorizontalAlignment.Center, w, Mathf.RoundToInt(1 * u));

        // Lunar Eclipse timer — refills on each kill (kills extend it); red so it reads as the blood moon
        if (p.Ult == Player.UltKind.Eclipse && p.UltActive)
        {
            float bw = 200 * u, bh = 9 * u, bx = (vp.X - bw) / 2f, by = y - 60 * u;
            var rcol = new Color(0.85f, 0.12f, 0.14f);
            DrawRect(new Rect2(bx - 1 * u, by - 1 * u, bw + 2 * u, bh + 2 * u), new Color(0, 0, 0, 0.5f));
            DrawRect(new Rect2(bx, by, bw * p.EclipseFrac, bh), rcol);
            Frame(new Rect2(bx, by, bw, bh), new Color(rcol.R, rcol.G, rcol.B, 0.85f), 1.5f * u);
            T(_body, new Vector2(bx, by - 15 * u), $"ECLIPSE  {Mathf.CeilToInt(p.EclipseTime)}s  ·  +crit", 12 * u, rcol, HorizontalAlignment.Center, bw, Mathf.RoundToInt(1 * u));
        }

        // Stormform timer (Gale) — wind-tinted countdown while the self-buff is up (NEW)
        if (p.StormActive)
        {
            float bw = 200 * u, bh = 9 * u, bx = (vp.X - bw) / 2f, by = y - 60 * u;
            var wcol = DamageTypes.Col(DamageType.Wind);
            DrawRect(new Rect2(bx - 1 * u, by - 1 * u, bw + 2 * u, bh + 2 * u), new Color(0, 0, 0, 0.5f));
            DrawRect(new Rect2(bx, by, bw * p.StormFrac, bh), wcol);
            Frame(new Rect2(bx, by, bw, bh), new Color(wcol.R, wcol.G, wcol.B, 0.85f), 1.5f * u);
            T(_body, new Vector2(bx, by - 15 * u), $"STORMFORM  {Mathf.CeilToInt(p.StormTime)}s  ·  swift", 12 * u, wcol, HorizontalAlignment.Center, bw, Mathf.RoundToInt(1 * u));
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

        // Faith Shield HP bar
        var sh = Game.I.Shield;
        if (sh != null && GodotObject.IsInstanceValid(sh))
        {
            float sw = 200 * u, sh2 = 9 * u, sxp = (vp.X - sw) / 2f, syp = y - 44 * u;
            DrawRect(new Rect2(sxp - 1 * u, syp - 1 * u, sw + 2 * u, sh2 + 2 * u), new Color(0, 0, 0, 0.5f));
            DrawRect(new Rect2(sxp, syp, sw * Mathf.Clamp(sh.Hp / sh.MaxHp, 0f, 1f), sh2), col);
            Frame(new Rect2(sxp, syp, sw, sh2), new Color(col.R, col.G, col.B, 0.8f), 1.5f * u);
            T(_body, new Vector2(sxp, syp - 15 * u), $"FAITH SHIELD  {Mathf.CeilToInt(sh.Hp)}", 12 * u, new Color(col.R, col.G, col.B), HorizontalAlignment.Center, sw, Mathf.RoundToInt(1 * u));
        }

        // Divine passive readout — Intervention pips
        if (p.DivineWitch && p.Interventions > 0)
        {
            string pips = "";
            for (int i = 0; i < p.Interventions; i++) pips += "\u271d ";
            T(_body, new Vector2(x, y + (Game.I.BossTokens > 0f ? 28 * u : 14 * u)), $"Intervention {pips}", 12 * u, DamageTypes.Col(DamageType.Holy), HorizontalAlignment.Center, w, Mathf.RoundToInt(1 * u));
        }
    }

    // (NEW) Self status-effect chips — ALWAYS drawn for the local player (unlike DrawUlt, which bails when no ult
    // is chosen). Bless shows here as a proper status effect for whatever witch is blessed; more buffs can join.
    private void DrawBuffs(Player p, Vector2 vp, float u)
    {
        var chips = new System.Collections.Generic.List<(string label, float t, Color col)>();
        if (p.BlessedT > 0f) chips.Add(("\u271d BLESSED", p.BlessedT, DamageTypes.Col(DamageType.Holy)));
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
        // enemy-director threat readout
        float heat = g.Heat;
        string tier = heat >= 1.45f ? "RAVENOUS" : heat >= 1.25f ? "RESTLESS" : heat >= 1.08f ? "STIRRING" : heat <= 0.92f ? "DROWSY" : "CALM";
        var tcol = heat >= 1.25f ? new Color(1f, 0.45f, 0.4f) : heat >= 1.08f ? new Color(1f, 0.78f, 0.4f) : new Color(0.5f, 0.85f, 0.6f);
        T(_body, new Vector2(0, y + 60 * u), $"the grove · {tier}  ({heat:0.00}x)", 13 * u, tcol, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(1 * u));
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
        T(_head, new Vector2(0, vp.Y * 0.28f), "OPTIONS", 20 * u, Gold, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(3 * u));

        float bw = Mathf.Min(320 * u, vp.X * 0.5f), bh = 16 * u, bx = (vp.X - bw) / 2f;

        float vol = g.Sfx != null ? g.Sfx.MusicVol : 0.8f;
        float my = vp.Y * 0.36f;
        T(_body, new Vector2(0, my - 22 * u), "Music Volume", 16 * u, GoldDim, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(2 * u));
        RPauseMusic = new Rect2(bx, my, bw, bh);
        DrawRect(new Rect2(bx - 1 * u, my - 1 * u, bw + 2 * u, bh + 2 * u), new Color(0, 0, 0, 0.5f));
        DrawRect(new Rect2(bx, my, bw * vol, bh), col);
        Frame(RPauseMusic, new Color(col.R, col.G, col.B, 0.8f), 1.5f * u);
        T(_body, new Vector2(0, my + bh + 4 * u), $"{Mathf.RoundToInt(vol * 100)}%", 13 * u, GoldDim, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(1 * u));

        float sv = g.SensSlider;
        float sy = vp.Y * 0.50f;
        T(_body, new Vector2(0, sy - 22 * u), "Look Sensitivity", 16 * u, GoldDim, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(2 * u));
        RPauseSens = new Rect2(bx, sy, bw, bh);
        DrawRect(new Rect2(bx - 1 * u, sy - 1 * u, bw + 2 * u, bh + 2 * u), new Color(0, 0, 0, 0.5f));
        DrawRect(new Rect2(bx, sy, bw * sv, bh), col);
        Frame(RPauseSens, new Color(col.R, col.G, col.B, 0.8f), 1.5f * u);
        T(_body, new Vector2(0, sy + bh + 4 * u), $"{Mathf.RoundToInt(sv * 100)}%", 13 * u, GoldDim, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(1 * u));

        // (NEW) Graphics quality preset — per-machine, for multiplayer performance
        float qy = vp.Y * 0.56f;
        T(_body, new Vector2(0, qy - 20 * u), "Graphics Quality", 16 * u, GoldDim, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(2 * u));
        string[] qlab = { "LOW", "MED", "HIGH" };
        float qbw = 88 * u, qbh = 28 * u, qgap = 10 * u;
        float qtot = 3 * qbw + 2 * qgap, qsx = vp.X / 2f - qtot / 2f;
        for (int i = 0; i < 3; i++)
        {
            var qr = new Rect2(qsx + i * (qbw + qgap), qy, qbw, qbh);
            RPauseGfx[i] = qr;
            bool sel = g.GfxQuality == i;
            DrawRect(qr, sel ? new Color(col.R, col.G, col.B, 0.35f) : new Color(0, 0, 0, 0.4f));
            Frame(qr, new Color(col.R, col.G, col.B, sel ? 0.9f : 0.5f), 1.5f * u);
            T(_body, new Vector2(qr.Position.X, qy + 7 * u), qlab[i], 15 * u, sel ? new Color(col.R, col.G, col.B) : GoldDim, HorizontalAlignment.Center, qbw, Mathf.RoundToInt(2 * u));
        }

        // (NEW) per-effect toggles — damage numbers + post-processing (each independently overridable)
        float ty = vp.Y * 0.63f;
        float tw2 = 96 * u, th = 28 * u, tgap = 10 * u;
        float ttot = 4 * tw2 + 3 * tgap, tsx = vp.X / 2f - ttot / 2f;
        RPauseDmg = new Rect2(tsx, ty, tw2, th);
        RPauseBloom = new Rect2(tsx + (tw2 + tgap), ty, tw2, th);
        RPauseSsao = new Rect2(tsx + 2 * (tw2 + tgap), ty, tw2, th);
        RPauseSsil = new Rect2(tsx + 3 * (tw2 + tgap), ty, tw2, th);
        void Tog(Rect2 r, string label, bool onv)
        {
            DrawRect(r, onv ? new Color(col.R, col.G, col.B, 0.35f) : new Color(0, 0, 0, 0.4f));
            Frame(r, new Color(col.R, col.G, col.B, 0.8f), 1.5f * u);
            T(_body, new Vector2(r.Position.X, r.Position.Y + 5 * u), label, 12 * u, GoldDim, HorizontalAlignment.Center, r.Size.X, Mathf.RoundToInt(1 * u));
            T(_body, new Vector2(r.Position.X, r.Position.Y + 16 * u), onv ? "ON" : "OFF", 12 * u, onv ? new Color(col.R, col.G, col.B) : GoldDim, HorizontalAlignment.Center, r.Size.X, Mathf.RoundToInt(1 * u));
        }
        Tog(RPauseDmg, "Dmg #", g.DmgNumbers);
        Tog(RPauseBloom, "Bloom", g.GfxBloom);
        Tog(RPauseSsao, "SSAO", g.GfxSsao);
        Tog(RPauseSsil, "SSIL", g.GfxSsil);

        float fy = vp.Y * 0.70f;
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
                bool hov = fr.HasPoint(GetGlobalMousePosition());
                DrawRect(fr, new Color(fc.R, fc.G, fc.B, hov ? 0.32f : 0.16f));
                Frame(fr, hov ? Gold : fc, 1.5f * u);
                string nm = FinMeta.Name(pp.Fin[i].Type);
                if (nm.Length > 11) nm = nm.Substring(0, 11);
                T(_body, new Vector2(fr.Position.X + 6 * u, fr.Position.Y + 9 * u), nm, 11 * u, Gold, HorizontalAlignment.Left, bwid - 40 * u, Mathf.RoundToInt(1 * u));
                T(_body, new Vector2(fr.Position.X, fr.Position.Y + 8 * u), $"[{KeyName(pp.Fin[i].Bind)}]", 13 * u, fc, HorizontalAlignment.Right, bwid - 8 * u, Mathf.RoundToInt(1 * u));
            }
        }

        float ry = vp.Y * 0.80f;
        RPauseResume = new Rect2(vp.X / 2f - 110 * u, ry - 4 * u, 220 * u, 34 * u);
        Frame(RPauseResume, new Color(col.R, col.G, col.B, 0.6f), 1.5f * u);
        T(_body, new Vector2(0, ry), "Resume   [Esc]", 18 * u, Gold, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(2 * u));
        T(_body, new Vector2(0, ry + 40 * u), "drag sliders, or hold A / D for music", 13 * u, GoldDim, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(1 * u));
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

    private void DrawRituals(float u)
    {
        var cam = Game.I.Player?.Cam;
        if (cam == null) return;
        foreach (var r in Game.I.Rituals)
        {
            if (r == null || r.Done || !IsInstanceValid(r)) continue;
            var top = r.GlobalPosition + new Vector3(0, 6.5f, 0);
            if (cam.IsPositionBehind(top)) continue;
            var sp = cam.UnprojectPosition(top);
            Color col = r.Type == RiteType.Ward ? DamageTypes.Col(DamageType.Lunar)
                      : r.Type == RiteType.Summon ? DamageTypes.Col(DamageType.Curse)
                      : DamageTypes.Col(DamageType.Holy);
            float w = 158 * u, h = 44 * u;
            var box = new Rect2(sp.X - w / 2f, sp.Y - h, w, h);
            DrawRect(box, new Color(0.05f, 0.03f, 0.09f, 0.5f));     // see-through panel
            Frame(box, new Color(col.R, col.G, col.B, 0.85f), Mathf.Max(1.5f, 2f * u));

            string title = r.Type == RiteType.Ward ? "WARDING RITE" : r.Type == RiteType.Summon ? "RITE OF SUMMONING" : "CLEANSING RITE";
            T(_body, new Vector2(box.Position.X, box.Position.Y + 13 * u), title, 11 * u, new Color(col.R, col.G, col.B), HorizontalAlignment.Center, w, Mathf.RoundToInt(1 * u));

            string info;
            float pf;
            if (!r.Active) { info = $"enter to begin  \u00b7  {Mathf.CeilToInt(r.SecondsLeft)}s"; pf = 0f; }
            else if (r.Type == RiteType.Ward) { info = $"charging  {Mathf.RoundToInt(r.Status * 100)}%"; pf = r.Status; }
            else if (r.Type == RiteType.Summon) { info = $"slay it  \u00b7  {Mathf.CeilToInt(r.SecondsLeft)}s"; pf = r.Status; }
            else { info = $"{r.Killed}/{r.KillTarget}  \u00b7  {Mathf.CeilToInt(r.SecondsLeft)}s"; pf = Mathf.Clamp((float)r.Killed / Mathf.Max(1, r.KillTarget), 0f, 1f); }
            T(_body, new Vector2(box.Position.X, box.Position.Y + 29 * u), info, 11 * u, Gold, HorizontalAlignment.Center, w, Mathf.RoundToInt(1 * u));

            if (r.Active)
                DrawRect(new Rect2(box.Position.X, box.Position.Y + h - 4 * u, w * Mathf.Clamp(pf, 0f, 1f), 4 * u), col);
        }
    }

    private void DrawEnemyBars(float u)
    {
        var cam = Game.I.Player?.Cam;
        if (cam == null) return;
        foreach (var e in Game.I.Enemies)
        {
            if (e == null || e.Dead || !IsInstanceValid(e)) continue;
            var head = e.GlobalPosition + new Vector3(0, e.Radius + 0.8f, 0);
            if (cam.IsPositionBehind(head)) continue;
            if (Game.I.SightBlocked(cam.GlobalPosition, head)) continue;   // (NEW) don't draw bars through walls
            var sp = cam.UnprojectPosition(head);
            float frac = e.MaxHp > 0 ? Mathf.Clamp(e.Hp / e.MaxHp, 0, 1) : 0;
            float w = Mathf.Clamp(e.Radius * 26f, 30f, 130f) * u;
            float h = (e.IsBoss ? 8f : 5f) * u;
            float x = sp.X - w / 2f, y = sp.Y;
            DrawRect(new Rect2(x - 1 * u, y - 1 * u, w + 2 * u, h + 2 * u), new Color(0, 0, 0, 0.6f));
            var fill = e.IsGoblin ? new Color(1f, 0.84f, 0.3f) : new Color(0.95f, 0.3f, 0.32f).Lerp(new Color(0.45f, 0.9f, 0.4f), frac);
            DrawRect(new Rect2(x, y, w * frac, h), fill);
            Frame(new Rect2(x, y, w, h), e.Elite ? new Color(1f, 0.86f, 0.25f) : new Color(0, 0, 0, 0.7f), Mathf.Max(1f, 1.4f * u));
            // (REMOVED the frozen blue "bank" bar — no banking now; the ice-block model already shows a foe is frozen/shatter-able)
            if (!e.Frozen && e.FreezeStacks > 0.5f)   // (NEW) freeze-stack indicator ❄ N/threshold
            {
                T(_body, new Vector2(x, y - 14f * u), $"\u2744 {Mathf.CeilToInt(e.FreezeStacks)}/{Mathf.CeilToInt(e.FreezeThreshold)}", 10f * u, new Color(0.62f, 0.86f, 1f), HorizontalAlignment.Center, w, Mathf.RoundToInt(1 * u));
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
            string plate = e.PlateText();
            if (plate != "")
                T(_body, new Vector2(x, y - (e.Label != "" ? 15f : 4f) * u), plate, 8.5f * u, e.PlateColor(), HorizontalAlignment.Center, w, Mathf.RoundToInt(1 * u));
            float px = x, py = y + h + 3 * u, ps = 6 * u;
            void Pip(bool on, Color col) { if (on) { DrawRect(new Rect2(px, py, ps, ps), col); px += ps + 2 * u; } }
            Pip(e.SlowT > 0, DamageTypes.Col(DamageType.Frost));
            Pip(e.RootT > 0, DamageTypes.Col(DamageType.Nature));
            Pip(e.MarkT > 0, DamageTypes.Col(DamageType.Curse));
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

    private void DrawAllyRoster(Game g, Vector2 vp, float u)
    {
        var allies = g.NetMgr.AllyAvatars();
        if (allies.Count == 0) return;
        float w = 190 * u, x = vp.X - w - 12 * u, y = 92 * u;
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
        int armed = 0; foreach (var f in p.Fin) if (f.Armed) armed++;
        if (armed > 0)
        {
            int k = 0;
            for (int i = 0; i < p.Fin.Count; i++)
            {
                var f = p.Fin[i]; if (!f.Armed) continue;
                var fc = FinCol(p, f.Type);
                var sc = new Vector2(c.X + (k - (armed - 1) / 2f) * 36 * u, c.Y);
                DrawCircle(sc, 15 * u, new Color(fc.R, fc.G, fc.B, 0.16f));
                Arc(sc, 17 * u, f.Window / 3.2f, fc, 2.5f * u);
                k++;
            }
        }
    }

    // Witching Hour fires the equipped right-click element, so its indicator follows that color
    private static Color FinCol(Player p, FinType t)
        => (t == FinType.Fullmod && p != null) ? DamageTypes.Col(p.SecondaryType) : FinMeta.Col(t);

    // ===== card panel (typed + rarity-loud) =====
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
        TM(_body, new Vector2(bx, by + 110 * u), desc, 10.5f * u, new Color(0.88f, 0.86f, 0.94f), r.Size.X - 28 * u);
    }

    public Rect2 CardRect(int i)
    {
        var vp = GetViewportRect().Size; float u = U;
        int n = 3; float cw = 250 * u, chh = 172 * u, gap = 26 * u;
        float total = n * cw + (n - 1) * gap, sx0 = vp.X / 2f - total / 2f, cy = vp.Y * 0.34f;
        return new Rect2(sx0 + i * (cw + gap), cy, cw, chh);
    }
    public int CardAt(Vector2 pos)
    {
        var g = Game.I; if (g?.Choices == null) return -1;
        for (int i = 0; i < g.Choices.Count; i++) if (CardRect(i).HasPoint(pos)) return i;
        return -1;
    }

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
            var (typeLine, typeCol) = CategoryOf(card);
            DrawCardPanel(r, card.Rarity, card.Title, typeLine, typeCol, card.Desc, (i + 1).ToString(), r.HasPoint(mouse), u);
            // DISABLE button under each card (bans the whole rarity family for this run; not for uniques)
            var br = BanBtnRect(i); bool bcan = !card.Unique && g.Gold >= g.BanCost; bool bhov = br.HasPoint(mouse);
            var bcol = card.Unique ? new Color(0.25f, 0.25f, 0.28f, 0.7f) : (bhov && bcan ? new Color(0.72f, 0.2f, 0.2f, 0.96f) : (bcan ? new Color(0.5f, 0.16f, 0.16f, 0.85f) : new Color(0.32f, 0.14f, 0.14f, 0.7f)));
            DrawRect(br, bcol);
            T(_body, new Vector2(br.Position.X, br.Position.Y + 4 * u), card.Unique ? "UNIQUE" : $"DISABLE  {g.BanCost}g", 11 * u, Colors.White, HorizontalAlignment.Center, br.Size.X, Mathf.RoundToInt(1 * u));
        }
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
        T(_head, new Vector2(0f, vp.Y * 0.22f), brand ? "CURSEBRAND — 2ND CURSE TYPE" : (prim ? "ATTUNE — PRIMARY" : "ATTUNE — SECONDARY"), 34 * u, Gold, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(4 * u));
        T(_body, new Vector2(0f, vp.Y * 0.22f + 30 * u), brand ? "cursed foes will take your bonus damage from this type too" : (prim ? "choose a new element for your left-click" : "choose a new element for your charged right-click"), 15 * u, GoldDim, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(2 * u));
        var current = brand ? (p.CurseBonusType2 >= 0 ? (DamageType)p.CurseBonusType2 : DamageType.Curse) : (prim ? p.PrimaryType : p.SecondaryType);

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
        yR = St(rx, yR, "Banished", $"{g.Score}");

        if (ttTitle != null) DrawTooltip(mouse, vp, ttTitle, ttBody, ttCol, u);
        T(_body, new Vector2(0f, vp.Y - 36 * u), "Tab to close", 15 * u, GoldDim, HorizontalAlignment.Center, vp.X, Mathf.RoundToInt(2 * u));
    }
}
