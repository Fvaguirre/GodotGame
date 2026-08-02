using Godot;

// RiteSigil.cs — one node of the Sacrifice nerfer's CRIMSON RITE. A small blood-circle on the ground that appears when the
// world boss is summoned (one per warden, ringed around the arena). ANY warden standing inside it charges it; 3s fills it and
// it LIGHTS (a standing pillar of blood-light) and drops off the minimap. Light every sigil in the set and the rite fires —
// see Game.UpdateCrimsonRite. The host owns the charging; Charge/Lit are streamed so the fill reads on every machine.
public partial class RiteSigil : Node3D
{
    public int NetId = 0;
    public bool Remote = false;
    public bool Lit = false;
    public float Charge = 0f;             // 0..1
    public const float Radius = 3.6f;     // stand-in reach
    public const float FillTime = 3f;
    public static readonly Color Col = new Color(0.95f, 0.12f, 0.18f);

    private const int Teeth = 14;         // segmented rim — each tooth lights as the fill climbs, so progress reads at a glance
    private readonly MeshInstance3D[] _teeth = new MeshInstance3D[Teeth];
    private StandardMaterial3D _toothOn, _toothOff, _floorMat;
    private MeshInstance3D _floor, _beam;
    private OmniLight3D _light;
    private float _t;

    public override void _Ready()
    {
        _t = (float)GD.RandRange(0, 6.28);
        _toothOn = new StandardMaterial3D { AlbedoColor = Col, EmissionEnabled = true, Emission = Col, EmissionEnergyMultiplier = 4.2f, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, BlendMode = BaseMaterial3D.BlendModeEnum.Add, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
        _toothOff = new StandardMaterial3D { AlbedoColor = new Color(Col.R * 0.5f, Col.G * 0.5f, Col.B * 0.5f, 0.5f), EmissionEnabled = true, Emission = Col, EmissionEnergyMultiplier = 0.5f, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, BlendMode = BaseMaterial3D.BlendModeEnum.Add, CullMode = BaseMaterial3D.CullModeEnum.Disabled };

        _floorMat = new StandardMaterial3D { AlbedoColor = new Color(Col.R, Col.G, Col.B, 0.16f), EmissionEnabled = true, Emission = Col, EmissionEnergyMultiplier = 1.1f, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, BlendMode = BaseMaterial3D.BlendModeEnum.Add, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
        _floor = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = Radius, BottomRadius = Radius, Height = 0.03f }, MaterialOverride = _floorMat, Position = new Vector3(0, 0.05f, 0), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_floor);

        // inner sigil: a small ring + a cross of runes, so it reads as an occult mark and not just a decal
        AddChild(new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = Radius * 0.42f, OuterRadius = Radius * 0.5f }, MaterialOverride = _toothOn, RotationDegrees = new Vector3(90, 0, 0), Position = new Vector3(0, 0.07f, 0), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        for (int i = 0; i < 4; i++)
        {
            float a = i / 4f * Mathf.Tau;
            AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(Radius * 0.7f, 0.02f, 0.09f) }, MaterialOverride = _toothOn, Position = new Vector3(Mathf.Cos(a) * Radius * 0.22f, 0.07f, Mathf.Sin(a) * Radius * 0.22f), Rotation = new Vector3(0, -a, 0), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        }

        for (int i = 0; i < Teeth; i++)   // the fill ring
        {
            float a = i / (float)Teeth * Mathf.Tau;
            var tooth = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.34f, 0.02f, 0.62f) }, MaterialOverride = _toothOff, Position = new Vector3(Mathf.Cos(a) * Radius * 0.86f, 0.07f, Mathf.Sin(a) * Radius * 0.86f), Rotation = new Vector3(0, -a, 0), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            AddChild(tooth); _teeth[i] = tooth;
        }

        // the "find me" beacon — a soft column that's thin/dim while dormant and a hard pillar once lit
        _beam = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.5f, BottomRadius = 1.1f, Height = 26f }, MaterialOverride = _floorMat, Position = new Vector3(0, 13f, 0), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_beam);
        _light = new OmniLight3D { OmniRange = Radius * 3.4f, LightColor = Col, LightEnergy = 1.3f, Position = new Vector3(0, 1.4f, 0) };
        AddChild(_light);
    }

    public void SetLit(bool lit) { Lit = lit; if (lit) Charge = 1f; }

    public override void _Process(double delta)
    {
        _t += (float)delta;
        int on = Lit ? Teeth : Mathf.FloorToInt(Mathf.Clamp(Charge, 0f, 1f) * Teeth);
        for (int i = 0; i < Teeth; i++)
        {
            if (_teeth[i] == null) continue;
            _teeth[i].MaterialOverride = i < on ? _toothOn : _toothOff;
            _teeth[i].Scale = new Vector3(1f, 1f, i < on ? 1.35f : 1f);   // filled teeth also grow outward
        }
        // lit → a hard bright pillar + a fast confident pulse; charging → brightens with the fill; dormant → a slow low ember
        float target = Lit ? 3.1f : 0.9f + Charge * 1.6f;
        float pulse = Lit ? 0.5f * Mathf.Sin(_t * 5f) : 0.22f * Mathf.Sin(_t * 2f);
        if (_floorMat != null)
        {
            _floorMat.EmissionEnergyMultiplier = target + pulse;
            _floorMat.AlbedoColor = new Color(Col.R, Col.G, Col.B, Lit ? 0.34f : 0.13f + Charge * 0.16f);
        }
        if (_beam != null) _beam.Scale = new Vector3(Lit ? 1.5f : 0.5f + Charge * 0.6f, 1f, Lit ? 1.5f : 0.5f + Charge * 0.6f);
        if (_light != null) _light.LightEnergy = (Lit ? 3.2f : 1.1f + Charge * 1.4f) + pulse;
        if (_floor != null) _floor.Rotation = new Vector3(0, _t * (Lit ? 0.9f : 0.3f), 0);
    }
}
