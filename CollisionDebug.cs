using Godot;

// DEV collision visualiser. The player uses a custom collision system (no physics mesh colliders), so the solid parts of the
// world are invisible Blockers (cylinders), walkable tops are Decks (flat boxes) and stairs are Ramps (sloped boxes). This node
// draws them as translucent coloured shapes so that collision can actually be SEEN and validated against the visual models:
//   RED   = solid Blocker (can't walk through)
//   BLUE  = walkable Deck top (stand on it)
//   GREEN = Ramp (walk up)
// Toggle with the `colliders` dev command. Off + zero-cost when hidden; refreshes periodically while on (collision streams with
// chunks). Dev-only.
public partial class CollisionDebug : Node3D
{
    private bool _on;
    private float _t;

    public void Toggle() { _on = !_on; Visible = _on; if (_on) Rebuild(); }
    public bool On => _on;

    public override void _Process(double delta)
    {
        if (!_on) return;
        _t -= (float)delta;
        if (_t <= 0f) { _t = 0.4f; Rebuild(); }
    }

    private void Rebuild()
    {
        foreach (var ch in GetChildren()) ch.QueueFree();
        var g = Game.I; if (g == null) return;
        var blockMat = Mat(new Color(1f, 0.15f, 0.15f, 0.32f));
        var deckMat = Mat(new Color(0.2f, 0.6f, 1f, 0.42f));
        var rampMat = Mat(new Color(0.25f, 1f, 0.35f, 0.42f));

        foreach (var b in g.Blockers)
        {
            float top = Mathf.Max(0.3f, b.Top);
            var m = new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = b.Radius, BottomRadius = b.Radius, Height = top, RadialSegments = 12 },
                MaterialOverride = blockMat,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Position = new Vector3(b.Pos.X, top * 0.5f, b.Pos.Z),
            };
            AddChild(m);
        }
        foreach (var d in g.Decks)
        {
            // (AUTHORED) red = Solid (can't stand on top), blue = walkable; Cyl = cylinder footprint; Yaw = angled box.
            // Boxed authored decks draw their FULL volume (BotY..TopY) so it matches what was authored; legacy decks draw a thin top slab.
            var mat = d.Solid ? blockMat : deckMat;
            float thick = d.Boxed ? Mathf.Max(0.14f, d.TopY - d.BotY) : 0.14f;
            float cyMid = d.Boxed ? (d.TopY + d.BotY) * 0.5f : d.TopY;
            var m = new MeshInstance3D
            {
                Mesh = d.Cyl
                    ? new CylinderMesh { TopRadius = d.Half.X, BottomRadius = d.Half.X, Height = thick, RadialSegments = 16 }
                    : new BoxMesh { Size = new Vector3(d.Half.X * 2f, thick, d.Half.Y * 2f) },
                MaterialOverride = mat,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Position = new Vector3(d.Center.X, cyMid, d.Center.Z),
                Rotation = new Vector3(0, d.Yaw, 0),
            };
            AddChild(m);
        }
        foreach (var r in g.Ramps)
        {
            float runLen = 2f * (r.AlongX ? r.Half.X : r.Half.Y);
            float wid = 2f * (r.AlongX ? r.Half.Y : r.Half.X);
            float dY = r.YHigh - r.YLow;
            float len = Mathf.Sqrt(runLen * runLen + dY * dY);
            float angle = Mathf.Atan2(dY, Mathf.Max(0.01f, runLen));
            var m = new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(r.AlongX ? len : wid, 0.14f, r.AlongX ? wid : len) },
                MaterialOverride = rampMat,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Position = new Vector3(r.Center.X, (r.YLow + r.YHigh) * 0.5f, r.Center.Z),
                // slope tilt in the ramp's local frame, then add the ramp's Y-yaw so it faces the authored direction
                Basis = new Basis(Quaternion.FromEuler(new Vector3(0, r.Yaw, 0)) * Quaternion.FromEuler(r.AlongX ? new Vector3(0, 0, angle) : new Vector3(-angle, 0, 0))),
            };
            AddChild(m);
        }
    }

    private static StandardMaterial3D Mat(Color c) => new StandardMaterial3D
    {
        AlbedoColor = c,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
    };
}
