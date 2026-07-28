using Godot;
using System.Collections.Generic;

// DEV collider-authoring editor (dev command `cedit`). Spawns a lineup of every authored Meshy structure, then lets you free-fly
// and place/scale/rotate/reposition colored colliders on them (red = solid no-top, blue = walkable, green = ramp), each in box or
// cylinder shape. Saves the placements RELATIVE to each model (unit-height local space) to res://data/colliders.json, which the
// spawn system (World.StructureModel/StairModel/ClimbableKeep) then reads to build real colliders. Inert until Enter().
public partial class ColliderEditor : Node3D
{
    // ---- the lineup of authored models (name, representative authoring height) ----
    private static readonly (string name, float h)[] Lineup =
    {
        ("cottage_a", 14f), ("cottage_b", 14f), ("fort", 13f), ("keep_climb", 18f), ("ruin", 11f),
        ("staircase", 6f), ("altar", 5f), ("well", 5f), ("gravestones", 3f), ("platform", 6f),
    };

    private class Slot { public string Name; public float Height; public Vector3 Pos; public float BaseY; }   // Pos = model origin XZ+Y(=BaseY)
    private class Placed { public int Model; public string Shape = "box"; public string Kind = "solid"; public Vector3 Pos; public Vector3 Size = new Vector3(0.6f, 0.6f, 0.6f); public float Yaw; }

    private readonly List<Slot> _slots = new();
    private readonly List<Placed> _placed = new();
    private Node3D _modelsRoot, _vizRoot;
    private int _sel = -1;
    private bool _dirty = true;

    public bool Active { get; private set; }
    public bool PaletteOpen { get; private set; }
    public int PalShape { get; private set; }    // 0 box, 1 cyl
    public int PalKind { get; private set; }     // 0 solid(red), 1 walk(blue), 2 ramp(green)
    public string Status = "";
    private float _statusT;

    public static readonly string[] ShapeNames = { "box", "cyl" };
    public static readonly string[] KindNames = { "solid", "walk", "ramp" };
    public static readonly string[] KindLabels = { "RED  solid (no top)", "BLUE  walkable", "GREEN  ramp" };

    public enum XMode { Move, Rotate, Scale }
    public XMode Mode = XMode.Move;   // persistent transform mode — G/R/T switch it, arrows apply it to the selected collider
    public string ModeName => Mode == XMode.Move ? "MOVE" : Mode == XMode.Rotate ? "ROTATE" : "SCALE";

    public override void _Ready() { Visible = false; SetProcessInput(false); }

    // ---------- lifecycle ----------
    public void Enter()
    {
        if (Active) return;
        Active = true; Visible = true; SetProcessInput(true);
        var g = Game.I;
        g.NoSpawn = true; g.ClearEnemies();
        g.State = GameState.ColliderEdit;
        Input.MouseMode = Input.MouseModeEnum.Captured;

        _modelsRoot = new Node3D(); AddChild(_modelsRoot);
        _vizRoot = new Node3D(); AddChild(_vizRoot);

        // ISOLATED authoring stage: hide the live world (terrain/structures/water) and build a clean flat platform HIGH in the air,
        // far from everything else, so the lineup stands alone with nothing intersecting it.
        g.SetWorldVisible(false);
        const float floorY = 600f;
        var p = g.Player;
        Vector3 stageC = new Vector3(p != null ? p.GlobalPosition.X : 0f, floorY, p != null ? p.GlobalPosition.Z : 0f);

        // grid spacing = large enough that even the biggest footprint can't touch its neighbour
        float maxHalf = 0f;
        foreach (var (n, h) in Lineup) { var ex = PropGlb.NormExtents(n); maxHalf = Mathf.Max(maxHalf, h * Mathf.Max(ex.X, ex.Y)); }
        float sp = 2f * maxHalf + 12f;
        int cols = 5;
        float gridW = (cols - 1) * sp, gridD = ((Lineup.Length - 1) / cols) * sp;

        var floor = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(gridW + sp + 20f, 1f, gridD + sp + 20f) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.16f, 0.17f, 0.2f), Roughness = 1f },
            Position = new Vector3(stageC.X, floorY - 0.5f, stageC.Z + gridD * 0.5f),
        };
        _modelsRoot.AddChild(floor);

        for (int i = 0; i < Lineup.Length; i++)
        {
            int cx = i % cols, cz = i / cols;
            // sink the model into the platform by the SAME embed the real spawn uses, so authored colliders match in-game exactly
            float embed = Lineup[i].name == "keep_climb" ? Mathf.Max(0.6f, Lineup[i].h * 0.04f) : Mathf.Max(0.4f, Lineup[i].h * 0.06f);
            float baseY = floorY - embed;
            var pos = new Vector3(stageC.X + (cx - (cols - 1) * 0.5f) * sp, baseY, stageC.Z + cz * sp);
            var slot = new Slot { Name = Lineup[i].name, Height = Lineup[i].h, Pos = pos, BaseY = baseY };   // BaseY = model feet (embedded), matching the spawn-time gy
            _slots.Add(slot);
            var mdl = PropGlb.Instance(slot.Name, slot.Height, seed: 100 + i);
            mdl.Position = slot.Pos;
            _modelsRoot.AddChild(mdl);
        }

        LoadTemplatesIntoPlaced();
        if (p != null) { p.GlobalPosition = stageC + new Vector3(0, 12f, -sp * 0.9f); p.Rotation = new Vector3(0, Mathf.Pi, 0); p.EditorLookPitch(-0.4f); }   // stand back + up on the -Z side, FACE +Z toward the lineup, look down at it
        _dirty = true;
        SetStatus($"COLLIDER EDITOR — {_slots.Count} models, {_placed.Count} colliders loaded");
    }

    public void Exit()
    {
        if (!Active) return;
        Active = false; Visible = false; SetProcessInput(false);
        _modelsRoot?.QueueFree(); _vizRoot?.QueueFree(); _modelsRoot = null; _vizRoot = null;
        _slots.Clear(); _placed.Clear(); _sel = -1; PaletteOpen = false;
        var g = Game.I;
        g.SetWorldVisible(true);   // restore the streamed world
        g.NoSpawn = false;
        g.State = GameState.Playing;
        Input.MouseMode = Input.MouseModeEnum.Captured;
        if (g.Player != null) { var pp = g.Player.GlobalPosition; pp.Y = g.SurfaceHeight(pp, 1e9f) + 1.5f; g.Player.GlobalPosition = pp; g.Player.EditorLookPitch(0f); }   // drop back onto the ground at the same XZ
    }

    private void SetStatus(string s) { Status = s; _statusT = 4f; }

    // ---------- template <-> placed conversion ----------
    private void LoadTemplatesIntoPlaced()
    {
        ColliderTemplates.EnsureLoaded();
        _placed.Clear();
        for (int m = 0; m < _slots.Count; m++)
        {
            if (!ColliderTemplates.Templates.TryGetValue(_slots[m].Name, out var list)) continue;
            var s = _slots[m];
            foreach (var e in list)
                _placed.Add(new Placed
                {
                    Model = m, Shape = e.Shape, Kind = e.Kind, Yaw = e.Yaw,
                    Pos = new Vector3(s.Pos.X + e.P.X * s.Height, s.BaseY + e.P.Y * s.Height, s.Pos.Z + e.P.Z * s.Height),
                    Size = e.S * s.Height,
                });
        }
    }

    private void SaveTemplates()
    {
        var data = new Dictionary<string, List<EditCol>>();
        foreach (var s in _slots) data[s.Name] = new List<EditCol>();
        foreach (var pl in _placed)
        {
            var s = _slots[pl.Model];
            data[s.Name].Add(new EditCol
            {
                Shape = pl.Shape, Kind = pl.Kind, Yaw = pl.Yaw,
                P = new Vector3((pl.Pos.X - s.Pos.X) / s.Height, (pl.Pos.Y - s.BaseY) / s.Height, (pl.Pos.Z - s.Pos.Z) / s.Height),
                S = pl.Size / s.Height,
            });
        }
        ColliderTemplates.Save(data);
        SetStatus($"SAVED {_placed.Count} colliders → res://data/colliders.json");
    }

    // ---------- helpers ----------
    private int NearestModel()
    {
        var p = Game.I.Player; if (p == null || _slots.Count == 0) return 0;
        int best = 0; float bd = float.MaxValue;
        for (int i = 0; i < _slots.Count; i++)
        {
            float dx = _slots[i].Pos.X - p.GlobalPosition.X, dz = _slots[i].Pos.Z - p.GlobalPosition.Z;
            float d = dx * dx + dz * dz; if (d < bd) { bd = d; best = i; }
        }
        return best;
    }
    public string NearestModelName() => _slots.Count == 0 ? "-" : _slots[NearestModel()].Name;
    public int SelectedCount => _placed.Count;
    public int SelectedIndex => _sel;
    public string SelInfo()
    {
        if (_sel < 0 || _sel >= _placed.Count) return "(none selected)";
        var pl = _placed[_sel]; var s = _slots[pl.Model];
        Vector3 lp = new Vector3((pl.Pos.X - s.Pos.X) / s.Height, (pl.Pos.Y - s.BaseY) / s.Height, (pl.Pos.Z - s.Pos.Z) / s.Height);
        return $"#{_sel + 1}/{_placed.Count}  {s.Name}  {pl.Shape}/{pl.Kind}\n  size({pl.Size.X:0.00} {pl.Size.Y:0.00} {pl.Size.Z:0.00})  yaw {Mathf.RadToDeg(pl.Yaw):0}°\n  local({lp.X:0.00} {lp.Y:0.00} {lp.Z:0.00})";
    }

    // ---------- input ----------
    public override void _Input(InputEvent e)
    {
        if (!Active || e is not InputEventKey k || !k.Pressed || k.Echo) return;
        var key = k.PhysicalKeycode;
        if (PaletteOpen) { PaletteKey(key); return; }
        bool fine = Input.IsPhysicalKeyPressed(Key.Shift);
        switch (key)
        {
            case Key.Escape: Exit(); return;
            case Key.M: PaletteOpen = true; return;
            case Key.Tab: case Key.Bracketright: CycleSel(1); return;
            case Key.Bracketleft: CycleSel(-1); return;
            case Key.Delete: case Key.X: DeleteSel(); return;
            case Key.Key0: case Key.Kp0: case Key.Enter: case Key.KpEnter: DuplicateSel(); return;
            case Key.G: Mode = XMode.Move; SetStatus("mode: MOVE  (arrows = X/Z, Q/E = Y)"); return;
            case Key.R: Mode = XMode.Rotate; SetStatus("mode: ROTATE  (←/→ = yaw)"); return;
            case Key.T: Mode = XMode.Scale; SetStatus("mode: SCALE  (arrows = X/Z, Q/E = Y)"); return;
            case Key.C: CycleKind(); return;
            case Key.V: CycleShape(); return;
            case Key.K: SaveTemplates(); return;
            default: TransformKey(key, fine); return;
        }
    }

    private void PaletteKey(Key key)
    {
        switch (key)
        {
            case Key.Up: PalShape = (PalShape + 1) % 2; break;
            case Key.Down: PalShape = (PalShape + 1) % 2; break;
            case Key.Left: PalKind = (PalKind + 2) % 3; break;
            case Key.Right: PalKind = (PalKind + 1) % 3; break;
            case Key.Enter: case Key.KpEnter: SpawnFromPalette(); PaletteOpen = false; break;
            case Key.M: case Key.Escape: PaletteOpen = false; break;
        }
    }

    // Apply the current MODE to the selected collider. Arrows = X/Z, Q/E (or PgUp/PgDn) = Y. Shift = fine step. Rotate = ←/→ yaw.
    private void TransformKey(Key key, bool fine)
    {
        if (_sel < 0 || _sel >= _placed.Count) { if (_placed.Count > 0) _sel = _placed.Count - 1; else { SetStatus("nothing selected — press M to add a collider"); return; } }
        var pl = _placed[_sel];
        bool up = key == Key.E || key == Key.Pageup, down = key == Key.Q || key == Key.Pagedown;
        if (Mode == XMode.Rotate)
        {
            float step = Mathf.DegToRad(fine ? 5f : 15f);
            if (key == Key.Left) pl.Yaw -= step; else if (key == Key.Right) pl.Yaw += step; else return;
        }
        else if (Mode == XMode.Scale)
        {
            float step = fine ? 0.05f : 0.25f;
            var sz = pl.Size;
            if (key == Key.Left) sz.X -= step; else if (key == Key.Right) sz.X += step;
            else if (key == Key.Up) sz.Z -= step; else if (key == Key.Down) sz.Z += step;
            else if (up) sz.Y += step; else if (down) sz.Y -= step;
            else return;
            pl.Size = new Vector3(Mathf.Max(0.05f, sz.X), Mathf.Max(0.05f, sz.Y), Mathf.Max(0.05f, sz.Z));
        }
        else   // Move
        {
            float step = fine ? 0.1f : 0.5f;
            var ps = pl.Pos;
            if (key == Key.Left) ps.X -= step; else if (key == Key.Right) ps.X += step;
            else if (key == Key.Up) ps.Z -= step; else if (key == Key.Down) ps.Z += step;
            else if (up) ps.Y += step; else if (down) ps.Y -= step;
            else return;
            pl.Pos = ps;
        }
        _dirty = true;
    }

    private void SpawnFromPalette()
    {
        int m = NearestModel();
        var s = _slots[m];
        var pl = new Placed
        {
            Model = m, Shape = ShapeNames[PalShape], Kind = KindNames[PalKind],
            Pos = new Vector3(s.Pos.X, s.BaseY + s.Height * 0.5f, s.Pos.Z),
            Size = Vector3.One * (s.Height * 0.18f),
        };
        _placed.Add(pl); _sel = _placed.Count - 1; _dirty = true;
        SetStatus($"spawned {pl.Shape}/{pl.Kind} on {s.Name}");
    }

    private void DuplicateSel()
    {
        if (_sel < 0) return;
        var o = _placed[_sel];
        _placed.Add(new Placed { Model = o.Model, Shape = o.Shape, Kind = o.Kind, Pos = o.Pos + new Vector3(1f, 0, 0), Size = o.Size, Yaw = o.Yaw });
        _sel = _placed.Count - 1; _dirty = true;
    }
    private void DeleteSel() { if (_sel < 0 || _sel >= _placed.Count) return; _placed.RemoveAt(_sel); _sel = Mathf.Min(_sel, _placed.Count - 1); _dirty = true; }
    private void CycleSel(int d) { if (_placed.Count == 0) { _sel = -1; return; } _sel = ((_sel + d) % _placed.Count + _placed.Count) % _placed.Count; _dirty = true; }
    private void CycleKind() { if (_sel < 0) return; int i = System.Array.IndexOf(KindNames, _placed[_sel].Kind); _placed[_sel].Kind = KindNames[(i + 1) % 3]; _dirty = true; }
    private void CycleShape() { if (_sel < 0) return; _placed[_sel].Shape = _placed[_sel].Shape == "box" ? "cyl" : "box"; _dirty = true; }

    // ---------- viz ----------
    public override void _Process(double delta)
    {
        if (!Active) return;
        if (_statusT > 0f) _statusT -= (float)delta;
        if (_dirty) { RebuildViz(); _dirty = false; }
    }

    private void RebuildViz()
    {
        foreach (var c in _vizRoot.GetChildren()) c.QueueFree();
        for (int i = 0; i < _placed.Count; i++)
        {
            var pl = _placed[i];
            bool sel = i == _sel;
            Color col = pl.Kind == "solid" ? new Color(1f, 0.2f, 0.2f) : pl.Kind == "walk" ? new Color(0.25f, 0.6f, 1f) : new Color(0.3f, 1f, 0.4f);
            col.A = sel ? 0.66f : 0.34f;
            var mat = new StandardMaterial3D
            {
                AlbedoColor = col,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                EmissionEnabled = sel, Emission = new Color(col.R, col.G, col.B), EmissionEnergyMultiplier = sel ? 0.5f : 0f,
            };
            Mesh mesh = pl.Shape == "cyl"
                ? new CylinderMesh { TopRadius = pl.Size.X, BottomRadius = pl.Size.X, Height = pl.Size.Y * 2f, RadialSegments = 16 }
                : new BoxMesh { Size = pl.Size * 2f };
            var mi = new MeshInstance3D { Mesh = mesh, MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Position = pl.Pos };
            // ramp kind: tilt the slab along local +X so its slope direction is visible
            if (pl.Kind == "ramp")
            {
                float ang = Mathf.Atan2(2f * pl.Size.Y, Mathf.Max(0.05f, 2f * pl.Size.X));
                mi.Basis = new Basis(Quaternion.FromEuler(new Vector3(0, pl.Yaw, 0)) * Quaternion.FromEuler(new Vector3(0, 0, ang)));
            }
            else mi.Basis = new Basis(Quaternion.FromEuler(new Vector3(0, pl.Yaw, 0)));
            _vizRoot.AddChild(mi);
        }
    }
}
