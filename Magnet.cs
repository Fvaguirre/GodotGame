using Godot;

// Magnet.cs — a "lodestone" a witch drops from a slain foe. Walk near it and it vacuums EVERY XP shard on the map to you
// (same pull as the chest lodestone). Persists where it fell until grabbed. Host spawns + drives pickup; synced to clients.
public partial class Magnet : Node3D
{
    public int NetId = 0;
    public bool Remote = false;
    public const float Radius = 2.4f;   // walk within this → grab it

    private float _t;
    private Node3D _spin;

    public override void _Ready()
    {
        _spin = new Node3D { Position = new Vector3(0, 1.0f, 0) }; AddChild(_spin);
        var iron = Game.Toon(new Color(0.14f, 0.13f, 0.16f), 0.5f, 0.7f, 0.04f);   // dark witch-iron
        float w = 0.34f, h = 1.05f, gap = 0.52f;

        for (int s = -1; s <= 1; s += 2)   // the two horseshoe prongs + glowing pole tips
        {
            var prong = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(w, h, w) }, MaterialOverride = iron };
            prong.Position = new Vector3(s * gap, h * 0.5f + w, 0); _spin.AddChild(prong);
            var poleCol = s < 0 ? new Color(0.72f, 0.28f, 1f) : new Color(1f, 0.26f, 0.36f);   // arcane violet + crimson poles
            var tip = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(w + 0.07f, 0.3f, w + 0.07f) }, MaterialOverride = Game.ToonEmissive(poleCol, 3.6f, 0f), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            tip.Position = new Vector3(s * gap, h + w + 0.06f, 0); _spin.AddChild(tip);
        }
        var baseBar = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(gap * 2f + w, w, w) }, MaterialOverride = iron };
        baseBar.Position = new Vector3(0, w * 0.5f, 0); _spin.AddChild(baseBar);

        // a runic band + a floating sigil under the poles — the witchy tell
        var runeCol = new Color(0.82f, 0.55f, 1f);
        var rune = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = 0.5f, OuterRadius = 0.62f }, MaterialOverride = Game.ToonEmissive(runeCol, 2.8f, 0f), RotationDegrees = new Vector3(90, 0, 0), Position = new Vector3(0, 0.12f, 0), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        _spin.AddChild(rune);

        // motes swirling INWARD — it's pulling everything toward itself
        var p = new GpuParticles3D { Amount = 28, Lifetime = 1.2, Position = new Vector3(0, 1.0f, 0) };
        p.ProcessMaterial = new ParticleProcessMaterial {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Ring, EmissionRingRadius = 2.4f, EmissionRingInnerRadius = 1.7f, EmissionRingHeight = 1.4f, EmissionRingAxis = new Vector3(0, 1, 0),
            Direction = new Vector3(0, 1, 0), Spread = 0f, InitialVelocityMin = 0f, InitialVelocityMax = 0f,
            RadialAccelMin = -7f, RadialAccelMax = -10f, TangentialAccelMin = 3f, TangentialAccelMax = 6f,   // suck inward + swirl
            ScaleMin = 0.1f, ScaleMax = 0.26f, Color = new Color(runeCol.R, runeCol.G, runeCol.B, 0.9f) };
        p.DrawPass1 = new QuadMesh { Size = new Vector2(0.3f, 0.3f), Material = new StandardMaterial3D { AlbedoColor = runeCol, EmissionEnabled = true, Emission = runeCol, EmissionEnergyMultiplier = 3f, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, BlendMode = BaseMaterial3D.BlendModeEnum.Add, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled } };
        AddChild(p);

        AddChild(new OmniLight3D { LightColor = runeCol, LightEnergy = 1.9f, OmniRange = 7f, Position = new Vector3(0, 1.1f, 0) });
        Game.AddBeacon(this, runeCol);   // a light shaft so you can spot it from a distance
    }

    public override void _Process(double delta)
    {
        _t += (float)delta;
        if (_spin != null) { _spin.Rotation = new Vector3(0, _t * 1.3f, 0); _spin.Position = new Vector3(0, 1.0f + Mathf.Sin(_t * 2f) * 0.12f, 0); }
    }
}
