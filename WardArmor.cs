using Godot;

// WardArmor.cs — "ward plating", a collectible a witch knocks loose from a slain foe. Walking over it FILLS every empty
// armor slot with random charges (the same reward a chest's ward pays out) — but only for the warden who touches it, not
// the whole coven. Persists where it fell until grabbed. Sibling of Magnet.cs: host spawns + drives pickup, synced to clients.
public partial class WardArmor : Node3D
{
    public int NetId = 0;
    public bool Remote = false;
    public const float Radius = 2.4f;

    private float _t;
    private Node3D _spin;
    private static readonly Color WardCol = new Color(0.45f, 0.78f, 1f);   // pale protective blue

    public override void _Ready()
    {
        _spin = new Node3D { Position = new Vector3(0, 1.05f, 0) }; AddChild(_spin);
        var plate = Game.Toon(new Color(0.20f, 0.24f, 0.30f), 0.55f, 0.65f, 0.04f);   // cold plate steel
        var glow = Game.ToonEmissive(WardCol, 3.4f, 0f);

        // a small kite shield: tapered body, a bright rim, and a rune boss at the centre
        var body = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.92f, 1.05f, 0.16f) }, MaterialOverride = plate };
        _spin.AddChild(body);
        var tip = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.62f, 0.5f, 0.16f) }, MaterialOverride = plate };
        tip.Position = new Vector3(0, -0.66f, 0); tip.RotationDegrees = new Vector3(0, 0, 45f); _spin.AddChild(tip);
        foreach (float sx in new[] { -0.46f, 0.46f })   // glowing edge trim down both sides
        {
            var edge = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.07f, 1.06f, 0.19f) }, MaterialOverride = glow, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            edge.Position = new Vector3(sx, 0, 0); _spin.AddChild(edge);
        }
        var boss = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.2f, Height = 0.28f }, MaterialOverride = glow, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        boss.Position = new Vector3(0, 0.08f, 0.12f); _spin.AddChild(boss);

        // a warding sigil ring orbiting the plate — the same visual language as the phalanx ward, at pickup scale
        var ring = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = 0.62f, OuterRadius = 0.72f }, MaterialOverride = Game.ToonEmissive(WardCol, 2.4f, 0f), RotationDegrees = new Vector3(90, 0, 0), Position = new Vector3(0, 0.05f, 0), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        _spin.AddChild(ring);

        // motes drifting UP and outward — it's shedding protection, the opposite of the lodestone's inward pull
        var p = new GpuParticles3D { Amount = 22, Lifetime = 1.4, Position = new Vector3(0, 0.4f, 0) };
        p.ProcessMaterial = new ParticleProcessMaterial {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Ring, EmissionRingRadius = 1.1f, EmissionRingInnerRadius = 0.5f, EmissionRingHeight = 0.2f, EmissionRingAxis = new Vector3(0, 1, 0),
            Direction = new Vector3(0, 1, 0), Spread = 6f, InitialVelocityMin = 0.5f, InitialVelocityMax = 1.3f,
            RadialAccelMin = 0.4f, RadialAccelMax = 1.1f, TangentialAccelMin = 1.4f, TangentialAccelMax = 2.6f,
            ScaleMin = 0.09f, ScaleMax = 0.22f, Color = new Color(WardCol.R, WardCol.G, WardCol.B, 0.85f) };
        p.DrawPass1 = new QuadMesh { Size = new Vector2(0.26f, 0.26f), Material = new StandardMaterial3D { AlbedoColor = WardCol, EmissionEnabled = true, Emission = WardCol, EmissionEnergyMultiplier = 3f, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, BlendMode = BaseMaterial3D.BlendModeEnum.Add, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled } };
        AddChild(p);

        AddChild(new OmniLight3D { LightColor = WardCol, LightEnergy = 1.7f, OmniRange = 6.5f, Position = new Vector3(0, 1.1f, 0) });
        Game.AddBeacon(this, WardCol);
    }

    public override void _Process(double delta)
    {
        _t += (float)delta;
        if (_spin != null) { _spin.Rotation = new Vector3(0, _t * 1.1f, 0); _spin.Position = new Vector3(0, 1.05f + Mathf.Sin(_t * 1.9f) * 0.12f, 0); }
    }
}
