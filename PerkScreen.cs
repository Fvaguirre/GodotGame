using Godot;
using System.Collections.Generic;

// PerkScreen.cs — the coven perk page. A PANNABLE reference view of a witch's perk GRAPH (circular nodes, name only,
// hover for effect) plus its 3 HIDDEN ROUTES. You don't buy here — nodes are bought in-run with attribute points on the
// level-up screen. Discovered hidden routes reveal their required node-set (hover a route to highlight its nodes in the graph).
public partial class PerkScreen : Control
{
    private int _view = 0;
    private Vector2 _pan = Vector2.Zero;
    private bool _dragging = false, _dragMoved = false;
    private Vector2 _dragStart, _panStart;
    private int _hoverRoute = -1;
    private readonly Rect2[] _routeRects = new Rect2[3];
    private readonly Rect2[] _metaRects = new Rect2[3];
    private Button[] _tabs;
    private Label _info;

    private static readonly Color Gold = new Color(1f, 0.84f, 0.34f);
    private static readonly Color Ink = new Color(0.93f, 0.88f, 0.72f);
    private static readonly Color Dim = new Color(0.6f, 0.58f, 0.68f);
    private const float ColStride = 96f, RowStride = 108f;
    private float _u = 1f;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        var tabRow = new HBoxContainer();
        tabRow.SetAnchorsPreset(LayoutPreset.CenterTop); tabRow.Position = new Vector2(-490, 12); tabRow.AddThemeConstantOverride("separation", 6);
        AddChild(tabRow);
        _tabs = new Button[Perks.WitchCount];
        for (int w = 0; w < Perks.WitchCount; w++)
        {
            int ww = w;
            var b = new Button { Text = RunStats.WitchName(w), CustomMinimumSize = new Vector2(118, 32) };
            b.AddThemeFontSizeOverride("font_size", 14);
            b.Pressed += () => { _view = ww; _pan = Vector2.Zero; RefreshTabs(); QueueRedraw(); UpdateInfo(); };
            tabRow.AddChild(b); _tabs[w] = b;
        }
        _info = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _info.AddThemeFontSizeOverride("font_size", 15); _info.AddThemeColorOverride("font_color", Ink);
        _info.SetAnchorsPreset(LayoutPreset.CenterTop); _info.Position = new Vector2(-430, 50); _info.CustomMinimumSize = new Vector2(860, 0);
        AddChild(_info);
        var back = new Button { Text = "Back", CustomMinimumSize = new Vector2(160, 40) };
        back.AddThemeFontSizeOverride("font_size", 18);
        back.SetAnchorsPreset(LayoutPreset.CenterBottom); back.Position = new Vector2(-80, -52);
        back.Pressed += () => Game.I.ClosePerks();
        AddChild(back);
    }

    public void Show(int witch) { _view = Mathf.Clamp(witch, 0, Perks.WitchCount - 1); _pan = Vector2.Zero; Visible = true; RefreshTabs(); QueueRedraw(); UpdateInfo(); }

    private void RefreshTabs()
    {
        for (int w = 0; w < Perks.WitchCount; w++)
        {
            var col = WitchModel.WitchColor(w); bool on = w == _view;
            var sb = new StyleBoxFlat { BgColor = on ? new Color(col.R * 0.3f, col.G * 0.3f, col.B * 0.3f, 0.95f) : new Color(0.11f, 0.10f, 0.17f, 0.9f) };
            sb.BorderColor = on ? col : new Color(col.R, col.G, col.B, 0.4f); sb.SetBorderWidthAll(on ? 2 : 1); sb.SetCornerRadiusAll(6);
            _tabs[w].AddThemeStyleboxOverride("normal", sb); _tabs[w].AddThemeStyleboxOverride("hover", sb); _tabs[w].AddThemeStyleboxOverride("pressed", sb);
            _tabs[w].AddThemeColorOverride("font_color", on ? Colors.White : Ink);
        }
    }

    private void UpdateInfo()
    {
        int disc = 0, owned = 0; for (int i = 0; i < 3; i++) if (Perks.RouteDiscovered(_view, i)) disc++;
        for (int i = 0; i < Perks.NodeCount; i++) if (Perks.Owned(_view, i)) owned++;
        _info.Text = $"{RunStats.WitchName(_view)}   ·   Gold: {Game.I.Gold}   ·   Unlocked: {owned}/{Perks.NodeCount}   ·   routes found {disc}/3   ·   click to unlock (gold) · activate in-run with attribute points";
    }

    private Vector2 NodePos(int id)
    {
        var (c, r) = Perks.PosOf(id);
        return new Vector2(Size.X / 2f + (c - 5.5f) * ColStride * _u + _pan.X, Size.Y / 2f + (r - 3f) * RowStride * _u + _pan.Y + 20f * _u);
    }

    public override void _Draw()
    {
        DrawRect(new Rect2(0, 0, Size.X, Size.Y), new Color(0.03f, 0.028f, 0.055f, 1f));
        var font = GetThemeDefaultFont();
        float u = _u = Mathf.Clamp(Mathf.Min(Size.X / 1320f, Size.Y / 760f), 0.6f, 1.6f);
        var nodes = Perks.Nodes(_view);
        var el = WitchModel.WitchColor(_view);
        var routes = Perks.Routes(_view);
        var mouse = GetLocalMousePosition();

        // which nodes a hovered route needs → highlight them
        var hl = new HashSet<int>();
        if (_hoverRoute >= 0 && _hoverRoute < routes.Length && Perks.RouteDiscovered(_view, _hoverRoute))
            foreach (int n in routes[_hoverRoute].Req) hl.Add(n);

        // edges (curved, bowed toward parent column)
        var bez = new Vector2[16];
        foreach (var p in nodes)
        {
            var c = NodePos(p.Id);
            foreach (int t in Perks.EdgesOf(p.Id))
            {
                if (t < p.Id) continue;   // draw each undirected pair once
                var tc = NodePos(t);
                bool lit = hl.Contains(p.Id) && hl.Contains(t);
                var lc = lit ? new Color(el.R, el.G, el.B, 0.9f) : new Color(0.3f, 0.28f, 0.38f, 0.6f);
                var ctrl = new Vector2(tc.X + (tc.X - c.X) * 0.14f, c.Y * 0.4f + tc.Y * 0.6f);
                for (int k = 0; k < bez.Length; k++) { float s = k / (float)(bez.Length - 1), it = 1f - s; bez[k] = it * it * c + 2f * it * s * ctrl + s * s * tc; }
                DrawPolyline(bez, lc, (lit ? 3f : 1.4f) * u, true);
            }
        }

        // nodes
        PerkNode hover = null; Vector2 hoverAt = Vector2.Zero;
        foreach (var p in nodes)
        {
            var c = NodePos(p.Id);
            float rad = (p.Keystone ? 26f : 19f) * u;
            bool highlighted = hl.Contains(p.Id);
            bool owned = Perks.Owned(_view, p.Id);
            bool canUnlock = Perks.CanUnlock(_view, p.Id, Game.I.Gold);
            bool reach = Perks.UnlockReachable(_view, p.Id);
            bool over = c.DistanceTo(mouse) <= rad + 3 * u;
            if (over) { hover = p; hoverAt = c; }
            Color ring = p.Keystone ? Gold : el;
            Color fill = owned ? new Color(ring.R * 0.32f, ring.G * 0.32f, ring.B * 0.32f, 1f)
                       : highlighted ? new Color(ring.R * 0.22f, ring.G * 0.22f, ring.B * 0.22f, 1f) : new Color(0.09f, 0.085f, 0.13f, 1f);
            Color rc = owned ? ring : canUnlock ? Gold : reach ? new Color(ring.R, ring.G, ring.B, 0.5f) : new Color(0.32f, 0.3f, 0.38f, 0.6f);
            if (highlighted && !owned) rc = ring;
            DrawCircle(c, rad + 2 * u, new Color(0, 0, 0, 0.5f));
            DrawCircle(c, rad, fill);
            DrawArc(c, rad, 0, Mathf.Tau, 34, rc, (owned || highlighted ? 3f : over ? 2.4f : 1.6f) * u, true);
            if (p.Keystone) DrawCircle(c, rad * 0.28f, new Color(Gold.R, Gold.G, Gold.B, owned ? 1f : 0.55f));
            DrawString(font, new Vector2(c.X - 66 * u, c.Y + rad + 12 * u), p.Name, HorizontalAlignment.Center, 132 * u, (int)(10.5f * u), owned ? Colors.White : Ink);
        }

        // ── hidden routes panel (bottom-left) ──
        {
            float pw = 300 * u, ph = 62 * u, px = 24 * u, py0 = Size.Y - 3 * (ph + 8 * u) - 24 * u;
            DrawString(font, new Vector2(px, py0 - 12 * u), "HIDDEN ROUTES  ·  own a node-set in a run to unlock", HorizontalAlignment.Left, pw, (int)(12 * u), Gold.Lerp(Ink, 0.3f));
            for (int i = 0; i < routes.Length; i++)
            {
                var r = new Rect2(px, py0 + i * (ph + 8 * u), pw, ph);
                _routeRects[i] = r;
                bool disc = Perks.RouteDiscovered(_view, i);
                bool over = r.HasPoint(mouse);
                DrawRect(r, new Color(0.08f, 0.075f, 0.12f, 0.95f));
                DrawRect(r, disc ? new Color(el.R, el.G, el.B, over ? 1f : 0.8f) : new Color(0.4f, 0.37f, 0.3f, 0.6f), false, 1.6f * u);
                if (disc)
                {
                    DrawString(font, new Vector2(r.Position.X + 10 * u, r.Position.Y + 17 * u), $"◆ {routes[i].Name}", HorizontalAlignment.Left, pw - 16 * u, (int)(14 * u), el.Lerp(Colors.White, 0.3f));
                    DrawString(font, new Vector2(r.Position.X + 10 * u, r.Position.Y + 34 * u), routes[i].Desc, HorizontalAlignment.Left, pw - 16 * u, (int)(10 * u), Ink);
                    DrawString(font, new Vector2(r.Position.X + 10 * u, r.Position.Y + ph - 7 * u), $"{routes[i].Req.Length} nodes · hover to see the path", HorizontalAlignment.Left, pw - 16 * u, (int)(9.5f * u), Dim);
                }
                else
                {
                    DrawString(font, new Vector2(r.Position.X + 10 * u, r.Position.Y + 22 * u), "◆ ??? — undiscovered", HorizontalAlignment.Left, pw - 16 * u, (int)(14 * u), Dim);
                    DrawString(font, new Vector2(r.Position.X + 10 * u, r.Position.Y + ph - 7 * u), $"a {routes[i].Req.Length}-node hidden route — find it in a run", HorizontalAlignment.Left, pw - 16 * u, (int)(9.5f * u), Dim);
                }
            }
        }

        // ── Coven Meta strip (top overlay) ──
        {
            float mW = 200 * u, mH = 44 * u, mGap = 36 * u, stripW = 3 * mW + 2 * mGap;
            float mLeft = (Size.X - stripW) / 2f, mTop = 82 * u;
            for (int i = 0; i < 3; i++)
            {
                var r = new Rect2(mLeft + i * (mW + mGap), mTop, mW, mH);
                _metaRects[i] = r;
                bool owned = MetaUnlocks.Owned(i), buyable = MetaUnlocks.CanBuy(i, Game.I.Gold);
                DrawRect(r, owned ? new Color(Gold.R * 0.28f, Gold.G * 0.28f, Gold.B * 0.16f, 0.98f) : new Color(0.09f, 0.085f, 0.12f, 0.95f));
                DrawRect(r, owned ? Gold : (buyable ? Gold : new Color(0.4f, 0.37f, 0.3f, 0.6f)), false, 1.6f * u);
                Color tx = owned ? Colors.White : (buyable ? Ink : Dim);
                DrawString(font, new Vector2(r.Position.X + 10 * u, r.Position.Y + 16 * u), MetaUnlocks.Name(i), HorizontalAlignment.Left, mW - 16 * u, (int)(13 * u), tx);
                DrawString(font, new Vector2(r.Position.X + 10 * u, r.Position.Y + 30 * u), MetaUnlocks.Desc(i), HorizontalAlignment.Left, mW - 16 * u, (int)(9.5f * u), tx.Lerp(new Color(tx.R, tx.G, tx.B, 0.7f), 0.5f));
                DrawString(font, new Vector2(r.Position.X + mW - 66 * u, r.Position.Y + mH - 6 * u), owned ? "OWNED" : $"{MetaUnlocks.Cost}g", HorizontalAlignment.Left, 62 * u, (int)(10 * u), owned ? Gold : (buyable ? Gold : Dim));
            }
        }

        // node hover tooltip — effect + gold status
        if (hover != null)
        {
            bool owned = Perks.Owned(_view, hover.Id);
            bool canUnlock = Perks.CanUnlock(_view, hover.Id, Game.I.Gold), reach = Perks.UnlockReachable(_view, hover.Id);
            float tw = 258 * u, th = 56 * u;
            float tx = Mathf.Clamp(hoverAt.X + 16 * u, 6 * u, Size.X - tw - 6 * u), ty = Mathf.Clamp(hoverAt.Y - th / 2f, 6 * u, Size.Y - th - 6 * u);
            var ring = hover.Keystone ? Gold : el;
            DrawRect(new Rect2(tx - 2 * u, ty - 2 * u, tw + 4 * u, th + 4 * u), new Color(0, 0, 0, 0.9f));
            DrawRect(new Rect2(tx, ty, tw, th), new Color(0.09f, 0.085f, 0.13f, 1f)); DrawRect(new Rect2(tx, ty, tw, th), ring, false, 1.5f * u);
            DrawString(font, new Vector2(tx + 9 * u, ty + 15 * u), (hover.Keystone ? "★ " : "") + hover.Name, HorizontalAlignment.Left, tw - 14 * u, (int)(13 * u), ring.Lerp(Colors.White, 0.3f));
            DrawString(font, new Vector2(tx + 9 * u, ty + 31 * u), hover.Desc, HorizontalAlignment.Left, tw - 14 * u, (int)(10.5f * u), Ink);
            string status = owned ? "unlocked · activate in-run with an attribute point" : reach ? $"{Perks.NodeCost(hover.Id)} gold to unlock" + (canUnlock ? " · click" : " (need more gold)") : "locked — unlock a connected node first";
            DrawString(font, new Vector2(tx + 9 * u, ty + th - 8 * u), status, HorizontalAlignment.Left, tw - 14 * u, (int)(9.5f * u), owned ? el.Lerp(Colors.White, 0.4f) : (canUnlock ? Gold : Dim));
        }
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed)
                {
                    for (int i = 0; i < 3; i++) if (_metaRects[i].HasPoint(mb.Position)) { if (MetaUnlocks.Buy(i)) Game.I.Sfx?.Clink(); else Game.I.Sfx?.Fizzle(); QueueRedraw(); UpdateInfo(); return; }
                    _dragging = true; _dragMoved = false; _dragStart = mb.Position; _panStart = _pan;
                }
                else if (_dragging) { _dragging = false; if (!_dragMoved) ClickNode(mb.Position); }
            }
            return;
        }
        if (e is InputEventMouseMotion mm)
        {
            if (_dragging) { if (mm.Position.DistanceTo(_dragStart) > 5f) _dragMoved = true; _pan = _panStart + (mm.Position - _dragStart); }
            _hoverRoute = -1;
            for (int i = 0; i < 3; i++) if (_routeRects[i].HasPoint(mm.Position)) { _hoverRoute = i; break; }
            QueueRedraw();
        }
    }

    private void ClickNode(Vector2 pos)   // unlock a node with gold (one-time, permanent)
    {
        foreach (var p in Perks.Nodes(_view))
        {
            var c = NodePos(p.Id);
            float rad = (p.Keystone ? 26f : 19f) * _u;
            if (c.DistanceTo(pos) > rad + 3 * _u) continue;
            if (!Perks.Owned(_view, p.Id)) { if (Perks.Unlock(_view, p.Id)) Game.I.Sfx?.Clink(); else Game.I.Sfx?.Fizzle(); }
            QueueRedraw(); UpdateInfo(); RefreshTabs();
            return;
        }
    }
}
