using Godot;

// GalePad.cs — a wind travel-pad. Walk onto one (Game checks proximity) and it launches you on a 45° arc ~100u in its
// aimed direction. Pads are scattered + AIMED at load to form a cohesive hop-network across the map (never toward the edge).
// Purely a visual + a carried direction; the launch itself is Player.GaleLaunch. Host-placed, synced to clients.
public partial class GalePad : Node3D
{
    public int NetId = 0;
    public float DirYaw = 0f;       // aimed launch direction, atan2(dz,dx); LaunchDir = (cos,0,sin)
    public bool Remote = false;
    public const float Radius = 2.6f;   // step within this (on the ground) → launch
    public Vector3 LaunchDir => new Vector3(Mathf.Cos(DirYaw), 0f, Mathf.Sin(DirYaw));

    private float _t = 0f;
    private Node3D _chevrons;
    private GpuParticles3D _updraft;

    public override void _Ready()
    {
        var c = DamageTypes.Col(DamageType.Wind);
        var bright = new StandardMaterial3D {
            AlbedoColor = c, EmissionEnabled = true, Emission = c, EmissionEnergyMultiplier = 3f,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha, BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
        var faint = new StandardMaterial3D {
            AlbedoColor = new Color(c.R, c.G, c.B, 0.16f), EmissionEnabled = true, Emission = c, EmissionEnergyMultiplier = 1.2f,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha, BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, CullMode = BaseMaterial3D.CullModeEnum.Disabled };

        // glowing ground ring + a faint filled disc (the pad footprint)
        AddChild(new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = Radius - 0.28f, OuterRadius = Radius }, MaterialOverride = bright, RotationDegrees = new Vector3(90, 0, 0), Position = new Vector3(0, 0.07f, 0), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = Radius - 0.3f, BottomRadius = Radius - 0.3f, Height = 0.04f }, MaterialOverride = faint, Position = new Vector3(0, 0.05f, 0), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });

        // directional chevrons — cone arrows marching along LaunchDir so you can read where it flings you
        Vector3 d = LaunchDir;
        Vector3 axis = Vector3.Up.Cross(d);
        Basis pointing = axis.LengthSquared() > 1e-5f ? new Basis(axis.Normalized(), Mathf.Pi / 2f) : Basis.Identity;   // cone's +Y → d
        _chevrons = new Node3D(); AddChild(_chevrons);
        for (int i = 0; i < 3; i++)
        {
            var cone = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.34f, Height = 0.72f }, MaterialOverride = bright, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            cone.Basis = pointing;
            cone.Position = d * (i * 0.75f - 0.4f) + new Vector3(0, 0.28f, 0);
            _chevrons.AddChild(cone);
        }

        // updraft: motes swirling up out of the pad
        _updraft = new GpuParticles3D { Amount = 34, Lifetime = 1.3, Position = new Vector3(0, 0.1f, 0) };
        _updraft.ProcessMaterial = new ParticleProcessMaterial {
            Direction = new Vector3(0, 1, 0), Spread = 22f, InitialVelocityMin = 3.5f, InitialVelocityMax = 6.5f,
            Gravity = new Vector3(0, 1.5f, 0), ScaleMin = 0.12f, ScaleMax = 0.32f,
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Ring, EmissionRingRadius = Radius - 0.4f, EmissionRingInnerRadius = 0f, EmissionRingHeight = 0.1f, EmissionRingAxis = new Vector3(0, 1, 0),
            Color = new Color(c.R, c.G, c.B, 0.8f) };
        _updraft.DrawPass1 = new QuadMesh { Size = new Vector2(0.3f, 0.3f), Material = bright };
        AddChild(_updraft);

        AddChild(new OmniLight3D { LightColor = c, LightEnergy = 1.6f, OmniRange = 6f, Position = new Vector3(0, 1.0f, 0) });
    }

    public override void _Process(double delta)
    {
        _t += (float)delta;
        if (_chevrons != null)   // pulse the chevrons brightening toward the launch direction
            _chevrons.Scale = Vector3.One * (1f + Mathf.Sin(_t * 4f) * 0.06f);
    }
}
