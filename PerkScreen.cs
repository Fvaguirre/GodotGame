using Godot;

// PerkScreen.cs — the coven perk-tree screen (opened from the home menu). Pick a witch, then spend gold to BUY perks and
// EQUIP them (left-click buys a buyable node, then left-click again equips an owned one; right-click unequips + cascades).
// The tree (3 lanes × 3 tiers) is custom-drawn with connecting support lines; witch tabs + Back are real buttons.
public partial class PerkScreen : Control
{
    private int _view = 0;                 // which witch's tree we're viewing
    private readonly Rect2[] _rects = new Rect2[9];
    private Button[] _tabs;
    private Label _info;

    private static readonly Color Ink = new Color(0.93f, 0.88f, 0.72f);
    private static readonly Color Dim = new Color(0.6f, 0.58f, 0.68f);

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        // NOTE: the background is drawn INSIDE _Draw — a child ColorRect would render on TOP of _Draw and hide the whole tree.

        // witch tabs
        var tabRow = new HBoxContainer();
        tabRow.SetAnchorsPreset(LayoutPreset.CenterTop); tabRow.Position = new Vector2(-490, 54); tabRow.AddThemeConstantOverride("separation", 6);
        AddChild(tabRow);
        _tabs = new Button[Perks.WitchCount];
        for (int w = 0; w < Perks.WitchCount; w++)
        {
            int ww = w;
            var b = new Button { Text = RunStats.WitchName(w), CustomMinimumSize = new Vector2(118, 34) };
            b.AddThemeFontSizeOverride("font_size", 14);
            b.Pressed += () => { _view = ww; RefreshTabs(); QueueRedraw(); UpdateInfo(); };
            tabRow.AddChild(b); _tabs[w] = b;
        }

        _info = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _info.AddThemeFontSizeOverride("font_size", 16); _info.AddThemeColorOverride("font_color", Ink);
        _info.SetAnchorsPreset(LayoutPreset.CenterTop); _info.Position = new Vector2(-360, 100); _info.CustomMinimumSize = new Vector2(720, 0);
        AddChild(_info);

        var back = new Button { Text = "Back", CustomMinimumSize = new Vector2(160, 40) };
        back.AddThemeFontSizeOverride("font_size", 18);
        back.SetAnchorsPreset(LayoutPreset.CenterBottom); back.Position = new Vector2(-80, -60);
        back.Pressed += () => Game.I.ClosePerks();
        AddChild(back);
    }

    public void Show(int witch) { _view = Mathf.Clamp(witch, 0, 6); Visible = true; RefreshTabs(); QueueRedraw(); UpdateInfo(); }

    private void RefreshTabs()
    {
        for (int w = 0; w < Perks.WitchCount; w++)
        {
            var col = WitchModel.WitchColor(w);
            bool on = w == _view;
            var sb = new StyleBoxFlat { BgColor = on ? new Color(col.R * 0.3f, col.G * 0.3f, col.B * 0.3f, 0.95f) : new Color(0.11f, 0.10f, 0.17f, 0.9f) };
            sb.BorderColor = on ? col : new Color(col.R, col.G, col.B, 0.4f); sb.SetBorderWidthAll(on ? 2 : 1); sb.SetCornerRadiusAll(6);
            _tabs[w].AddThemeStyleboxOverride("normal", sb);
            _tabs[w].AddThemeStyleboxOverride("hover", sb);
            _tabs[w].AddThemeStyleboxOverride("pressed", sb);
            _tabs[w].AddThemeColorOverride("font_color", on ? Colors.White : Ink);
        }
    }

    private void UpdateInfo()
    {
        var names = Perks.LaneNames(_view);
        int eq = Perks.EquippedCount(_view), maj = Perks.MajorCount(_view);
        _info.Text = $"{RunStats.WitchName(_view)}  —  {names[0]} / {names[1]} / {names[2]}      Gold: {Game.I.Gold}      Equipped: {eq}/{Perks.Cap}   Majors: {maj}/{Perks.MaxMajors}";
    }

    public override void _Draw()
    {
        DrawRect(new Rect2(0, 0, Size.X, Size.Y), new Color(0.03f, 0.028f, 0.055f, 1f));   // background (must be drawn here, not a child)
        var font = GetThemeDefaultFont();
        float u = Mathf.Clamp(Mathf.Min(Size.X / 1320f, Size.Y / 760f), 0.6f, 1.6f);
        float nodeW = 210 * u, nodeH = 76 * u, colGap = 96 * u, rowGap = 66 * u;
        float colStride = nodeW + colGap, rowStride = nodeH + rowGap;
        float treeW = 3 * nodeW + 2 * colGap, treeH = 3 * nodeH + 2 * rowGap;
        float treeLeft = (Size.X - treeW) / 2f, treeTop = (Size.Y - treeH) / 2f + 40 * u;
        var tree = Perks.Tree(_view);

        Vector2 Center(int i)
        {
            int lane = i / 3, tier = i % 3 + 1;
            float cx = treeLeft + lane * colStride + nodeW / 2f;
            float cy = treeTop + (3 - tier) * rowStride + nodeH / 2f;   // tier 3 top, tier 1 bottom
            return new Vector2(cx, cy);
        }

        // connecting support lines (behind the nodes)
        for (int i = 0; i < 9; i++)
        {
            var c = Center(i);
            foreach (int s in tree[i].Supports)
            {
                var sc = Center(s);
                bool live = Perks.Equipped(_view, i) && Perks.Equipped(_view, s);
                var lc = live ? WitchModel.WitchColor(_view) : new Color(0.3f, 0.28f, 0.36f, 0.8f);
                DrawLine(new Vector2(c.X, c.Y + nodeH / 2f), new Vector2(sc.X, sc.Y - nodeH / 2f), lc, (live ? 3f : 1.5f) * u);
            }
        }

        // nodes
        var el = WitchModel.WitchColor(_view);
        for (int i = 0; i < 9; i++)
        {
            var p = tree[i];
            var c = Center(i);
            var r = new Rect2(c.X - nodeW / 2f, c.Y - nodeH / 2f, nodeW, nodeH);
            _rects[i] = r;

            bool owned = Perks.Owned(_view, i), eq = Perks.Equipped(_view, i), buyable = Perks.CanBuy(_view, i, Game.I.Gold), prereq = Perks.PrereqOwned(_view, i);
            Color fill, border; float bw;
            if (eq) { fill = new Color(el.R * 0.34f, el.G * 0.34f, el.B * 0.34f, 0.98f); border = el; bw = 3f * u; }
            else if (owned) { fill = new Color(0.14f, 0.13f, 0.2f, 0.98f); border = new Color(el.R, el.G, el.B, 0.6f); bw = 2f * u; }
            else if (prereq) { fill = new Color(0.10f, 0.09f, 0.15f, 0.95f); border = buyable ? new Color(1f, 0.84f, 0.34f, 0.9f) : new Color(0.5f, 0.45f, 0.3f, 0.6f); bw = 2f * u; }
            else { fill = new Color(0.07f, 0.065f, 0.1f, 0.9f); border = new Color(0.3f, 0.28f, 0.34f, 0.5f); bw = 1.5f * u; }

            DrawRect(r, fill);
            DrawRect(r, border, false, bw);
            if (p.Major) DrawRect(new Rect2(r.Position.X + 3 * u, r.Position.Y + 3 * u, r.Size.X - 6 * u, 4 * u), new Color(1f, 0.84f, 0.34f, eq ? 1f : 0.5f));   // gold major bar

            Color txt = eq ? Colors.White : (owned ? Ink : (prereq ? Ink : Dim));
            DrawString(font, new Vector2(r.Position.X + 10 * u, r.Position.Y + 22 * u), (p.Major ? "★ " : "") + p.Name, HorizontalAlignment.Left, nodeW - 16 * u, (int)(15 * u), txt);
            DrawMultilineString(font, new Vector2(r.Position.X + 10 * u, r.Position.Y + 38 * u), p.Desc, HorizontalAlignment.Left, nodeW - 16 * u, (int)(11 * u), 2, txt.Lerp(new Color(txt.R, txt.G, txt.B, 0.75f), 0.5f));
            string tag = eq ? "EQUIPPED" : owned ? "owned · click to equip" : buyable ? $"{p.Cost}g · click to buy" : prereq ? $"{p.Cost}g (need more gold)" : "locked";
            DrawString(font, new Vector2(r.Position.X + 10 * u, r.Position.Y + nodeH - 8 * u), tag, HorizontalAlignment.Left, nodeW - 16 * u, (int)(10 * u), eq ? el.Lerp(Colors.White, 0.4f) : (buyable ? new Color(1f, 0.84f, 0.34f) : Dim));
        }

        DrawString(font, new Vector2(Size.X / 2f - 300 * u, Size.Y - 84 * u), "left-click: buy, then equip   ·   right-click: unequip", HorizontalAlignment.Left, 600 * u, (int)(13 * u), Dim);
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is not InputEventMouseButton mb || !mb.Pressed) return;
        for (int i = 0; i < 9; i++)
        {
            if (!_rects[i].HasPoint(mb.Position)) continue;
            if (mb.ButtonIndex == MouseButton.Left)
            {
                if (!Perks.Owned(_view, i)) { if (Perks.Buy(_view, i)) Perks.Equip(_view, i); }   // buy, and equip right away if it fits
                else Perks.Equip(_view, i);
            }
            else if (mb.ButtonIndex == MouseButton.Right)
                Perks.Unequip(_view, i);
            QueueRedraw(); UpdateInfo(); RefreshTabs();
            return;
        }
    }
}
