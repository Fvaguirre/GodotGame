using Godot;

// NerfShrine.cs — a hidden GROVE shrine you find by exploring. Each weakens the coming boss fight in its own way (see Game
// for the logic). Kinds: Summoner (ward-defend → an arcane unicorn that nukes the boss on spawn), Sacrifice (pay 40% HP +
// slay a miniboss per player → a crimson drain sigil under the boss), Sanctuary (pay souls → a 2 HP/s party regen in the fight).
// State: 0 dormant · 1 in-progress · 2 armed/complete. Not marked on the minimap until you're near it; once armed it lights up.
public enum NerfKind { Summoner, Sacrifice, Sanctuary }

public partial class NerfShrine : Node3D
{
    public NerfKind Kind;
    public int NetId = 0;
    public bool Remote = false;
    public int State = 0;             // 0 dormant · 1 in-progress · 2 armed
    public const float Radius = 4.0f; // hold-E interaction reach

    private float _t;
    private OmniLight3D _light;
    private MeshInstance3D _core;
    private StandardMaterial3D _coreMat;
    private Node3D _ward;                 // (NEW) the big ground summoning-circle + sky-beam shown while State==1 (the defend phase)
    public const float WardRadius = 11f;  // the visible "hold this ground" zone

    public static Color KindColor(NerfKind k) => k switch
    {
        NerfKind.Summoner  => new Color(0.72f, 0.5f, 1f),    // arcane violet
        NerfKind.Sacrifice => new Color(0.95f, 0.2f, 0.28f), // sacrificial crimson
        _                  => new Color(1f, 0.86f, 0.5f),    // sanctuary gold/holy
    };
    public static string KindName(NerfKind k) => k switch { NerfKind.Summoner => "Summoning", NerfKind.Sacrifice => "Sacrifice", _ => "Sanctuary" };
    public Color IconColor => State == 2 ? KindColor(Kind).Lerp(Colors.White, 0.45f) : KindColor(Kind);

    public override void _Ready()
    {
        _t = (float)GD.RandRange(0, 6.28);
        var col = KindColor(Kind);
        var stone = Game.Toon(new Color(0.13f, 0.12f, 0.16f), 0.92f, 0.22f, 0.03f);

        // a low ringed plinth + a cluster of leaning rune-shards around a floating core
        var plinth = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 1.7f, BottomRadius = 2.1f, Height = 0.5f, RadialSegments = 8 }, MaterialOverride = stone };
        plinth.Position = new Vector3(0, 0.25f, 0); AddChild(plinth);
        for (int i = 0; i < 4; i++)
        {
            float a = i / 4f * Mathf.Tau;
            var shard = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.4f, 2.6f, 0.4f) }, MaterialOverride = stone };
            shard.Position = new Vector3(Mathf.Cos(a) * 1.35f, 1.5f, Mathf.Sin(a) * 1.35f);
            shard.RotationDegrees = new Vector3(Mathf.Cos(a) * 12f, a * 57f, Mathf.Sin(a) * 12f);   // lean outward
            AddChild(shard);
        }

        _coreMat = new StandardMaterial3D { AlbedoColor = col, EmissionEnabled = true, Emission = col, EmissionEnergyMultiplier = 2.6f, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
        _core = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.7f, Height = 1.4f }, MaterialOverride = _coreMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        _core.Position = new Vector3(0, 2.2f, 0); AddChild(_core);
        _light = new OmniLight3D { OmniRange = 13f, LightColor = col, LightEnergy = 1.6f, Position = new Vector3(0, 2.2f, 0) };
        AddChild(_light);
    }

    public void SetState(int s)
    {
        State = s;
        // the Summoner's defend phase (State 1) raises a big ground circle + sky-beam so it's obvious WHERE + THAT it's happening
        if (s == 1 && Kind == NerfKind.Summoner && _ward == null) BuildWard();
        else if (s != 1 && _ward != null) { _ward.QueueFree(); _ward = null; }
    }

    private void BuildWard()
    {
        var col = KindColor(Kind);
        _ward = new Node3D(); AddChild(_ward);
        var bright = new StandardMaterial3D { AlbedoColor = col, EmissionEnabled = true, Emission = col, EmissionEnergyMultiplier = 3.2f, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, BlendMode = BaseMaterial3D.BlendModeEnum.Add, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
        var faint = new StandardMaterial3D { AlbedoColor = new Color(col.R, col.G, col.B, 0.12f), EmissionEnabled = true, Emission = col, EmissionEnergyMultiplier = 1.4f, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, BlendMode = BaseMaterial3D.BlendModeEnum.Add, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
        float R = WardRadius;
        _ward.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = R, BottomRadius = R, Height = 0.03f }, MaterialOverride = faint, Position = new Vector3(0, 0.05f, 0), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });   // faint filled zone
        foreach (float rr in new[] { R, R * 0.66f, R * 0.34f })                                                                                                                                                                                     // concentric rings
            _ward.AddChild(new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = rr - 0.12f, OuterRadius = rr + 0.12f }, MaterialOverride = bright, RotationDegrees = new Vector3(90, 0, 0), Position = new Vector3(0, 0.07f, 0), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        int n = 10;
        for (int i = 0; i < n; i++)
        {
            float a = i / (float)n * Mathf.Tau;
            _ward.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(R * 0.9f, 0.02f, 0.1f) }, MaterialOverride = bright, Position = new Vector3(Mathf.Cos(a) * R * 0.5f, 0.06f, Mathf.Sin(a) * R * 0.5f), Rotation = new Vector3(0, -a, 0), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });   // spoke
            _ward.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.42f, 0.02f, 0.42f) }, MaterialOverride = bright, Position = new Vector3(Mathf.Cos(a) * R * 0.9f, 0.06f, Mathf.Sin(a) * R * 0.9f), Rotation = new Vector3(0, -a, 0), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });                     // rim glyph
        }
        _ward.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 1.3f, BottomRadius = 2.4f, Height = 42f }, MaterialOverride = faint, Position = new Vector3(0, 21f, 0), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });   // sky-beam — visible from across the map
        var up = new GpuParticles3D { Amount = 44, Lifetime = 2.0, Position = new Vector3(0, 0.2f, 0) };
        up.ProcessMaterial = new ParticleProcessMaterial {
            Direction = new Vector3(0, 1, 0), Spread = 8f, InitialVelocityMin = 5f, InitialVelocityMax = 12f, Gravity = new Vector3(0, 0.5f, 0), ScaleMin = 0.2f, ScaleMax = 0.5f,
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Ring, EmissionRingRadius = R * 0.92f, EmissionRingInnerRadius = R * 0.2f, EmissionRingHeight = 0.2f, EmissionRingAxis = new Vector3(0, 1, 0),
            Color = new Color(col.R, col.G, col.B, 0.85f) };
        up.DrawPass1 = new QuadMesh { Size = new Vector2(0.4f, 0.4f), Material = bright };
        _ward.AddChild(up);
    }

    public override void _Process(double delta)
    {
        _t += (float)delta;
        if (_ward != null) { _ward.Rotation = new Vector3(0, _t * 0.35f, 0); _ward.Scale = Vector3.One * (1f + Mathf.Sin(_t * 3f) * 0.02f); }   // the circle turns + breathes
        if (_core != null)
        {
            _core.Rotation = new Vector3(0, _t * 0.8f, 0);
            float e = State == 2 ? 3.8f + 1.4f * Mathf.Sin(_t * 4f) : 2.2f + 0.8f * Mathf.Sin(_t * 2f);   // armed → brighter, livelier pulse
            _coreMat.EmissionEnergyMultiplier = e;
            if (_light != null) _light.LightEnergy = State == 2 ? 2.6f : 1.6f;
            float bob = 2.2f + 0.12f * Mathf.Sin(_t * 1.6f);
            _core.Position = new Vector3(0, bob, 0);
        }
    }
}
