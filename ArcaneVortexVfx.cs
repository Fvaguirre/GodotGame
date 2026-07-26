using Godot;

// ArcaneVortexVfx.cs — the cosmetic swirl for the Arcane Vortex modifier: a ring of arcane shards spinning around the rim
// (Ravenous-Vortex-ish) with raw lightning arcing from the center to the edge. The GAMEPLAY (slow + DoT) is the GroundField
// spawned alongside it; this is caster-local visuals only, sized to match the field's real radius.
public partial class ArcaneVortexVfx : Node3D
{
    private float _life, _radius, _arcT;
    private Node3D _ring;

    public void Init(Vector3 pos, float radius, float dur)
    {
        _radius = radius; _life = dur;
        GlobalPosition = new Vector3(pos.X, Game.I.SurfaceHeight(pos, pos.Y) + 0.1f, pos.Z);
        var col = DamageTypes.Col(DamageType.Arcane);
        _ring = new Node3D(); AddChild(_ring);
        const int n = 8;
        for (int i = 0; i < n; i++)
        {
            float a = i / (float)n * Mathf.Tau;
            var sp = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = radius * 0.06f, Height = radius * 0.55f, RadialSegments = 4 }, MaterialOverride = Game.Emissive(col.Lerp(Colors.White, 0.25f), 2.2f), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            sp.Position = new Vector3(Mathf.Cos(a) * radius * 0.85f, 0.3f, Mathf.Sin(a) * radius * 0.85f);
            sp.RotationDegrees = new Vector3(60f, Mathf.RadToDeg(a), 0f);
            _ring.AddChild(sp);
        }
        AddChild(new OmniLight3D { OmniRange = radius * 1.6f, LightColor = col, LightEnergy = 1.6f, ShadowEnabled = false });
    }

    public override void _Process(double delta)
    {
        if (Game.I == null || !Game.I.SimActive) return;
        float dt = (float)delta; _life -= dt;
        if (_life <= 0f) { QueueFree(); return; }
        if (_ring != null) _ring.RotationDegrees += new Vector3(0f, dt * 140f, 0f);   // swirl
        _arcT -= dt;
        if (_arcT <= 0f)
        {
            _arcT = 0.12f;
            float a = GD.Randf() * Mathf.Tau;
            var edge = GlobalPosition + new Vector3(Mathf.Cos(a) * _radius * (0.6f + GD.Randf() * 0.4f), 0.3f + GD.Randf() * 0.5f, Mathf.Sin(a) * _radius * (0.6f + GD.Randf() * 0.4f));
            Game.I.SpawnArcaneSpark(GlobalPosition + Vector3.Up * 0.4f, edge);   // raw lightning arcing out to the rim
        }
    }
}
