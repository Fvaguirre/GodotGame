using Godot;

// ArcaneAura.cs — a transformation aura the Arcane witch's ults wear: energy shards orbiting her, a pulsing plasma glow,
// crackling sparks, and a light. Parented to the caster (rides her); the ult frees it when it ends. Local-view VFX.
public partial class ArcaneAura : Node3D
{
    private Node3D _ring; private StandardMaterial3D _glow; private float _sparkT, _seed, _radius, _spark;

    private Color _col;
    public void Init(float radius, float sparkRate) => Init(radius, sparkRate, DamageTypes.Col(DamageType.Arcane));
    public void Init(float radius, float sparkRate, Color col)
    {
        _radius = radius; _spark = sparkRate; _seed = GD.Randf() * 9f; _col = col;
        var glowMi = new MeshInstance3D { Mesh = new SphereMesh { Radius = radius * 0.7f, Height = radius * 1.4f }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        _glow = new StandardMaterial3D { AlbedoColor = new Color(col.R, col.G, col.B, 0.14f), EmissionEnabled = true, Emission = col, EmissionEnergyMultiplier = 1.4f, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
        glowMi.MaterialOverride = _glow; glowMi.Position = new Vector3(0, 1.0f, 0); AddChild(glowMi);
        _ring = new Node3D(); _ring.Position = new Vector3(0, 1.0f, 0); AddChild(_ring);
        for (int i = 0; i < 9; i++)
        {
            float a = i / 9f * Mathf.Tau;
            var sp = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.09f, Height = 0.55f, RadialSegments = 4 }, MaterialOverride = Game.ElementEnergyMat(col), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            sp.Position = new Vector3(Mathf.Cos(a) * radius * 0.7f, Mathf.Sin(a * 2f) * 0.25f, Mathf.Sin(a) * radius * 0.7f);
            sp.RotationDegrees = new Vector3(GD.Randf() * 360f, Mathf.RadToDeg(a), GD.Randf() * 360f);
            _ring.AddChild(sp);
        }
        AddChild(new OmniLight3D { OmniRange = radius * 2f, LightColor = col, LightEnergy = 2.2f, ShadowEnabled = false, Position = new Vector3(0, 1f, 0) });
    }

    public override void _Process(double delta)
    {
        if (Game.I == null) return;
        float dt = (float)delta; float t = Time.GetTicksMsec() / 1000f;
        if (_ring != null) _ring.RotationDegrees += new Vector3(dt * 45f, dt * 135f, dt * 30f);
        if (_glow != null) _glow.EmissionEnergyMultiplier = 1.2f + 0.7f * Mathf.Abs(Mathf.Sin(t * 6f + _seed));
        _sparkT -= dt;
        if (_sparkT <= 0f && _spark > 0f)
        {
            _sparkT = _spark;
            var c = GlobalPosition + Vector3.Up * (0.8f + GD.Randf() * 1.2f);
            float a = GD.Randf() * Mathf.Tau;
            var edge = c + new Vector3(Mathf.Cos(a), GD.Randf() - 0.5f, Mathf.Sin(a)) * _radius * (0.7f + GD.Randf() * 0.5f);
            Game.I.SpawnArcaneSpark(c, edge);
        }
    }
}
