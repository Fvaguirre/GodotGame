using Godot;

// GardenPortal.cs — a mossy stone archway on a pedestal with a shimmering portal in the middle. Two kinds:
// a TWO-WAY travel portal (walk up, hold E → whisked to its linked partner elsewhere on the map, and back), or
// the maze GATE (EntersMaze = true → hold E enters the cottage-garden maze). The portal can ONLY be used BETWEEN
// waves: while a wave is underway the ring stops spinning and the membrane dims to a faint glow, and hold-E is
// disabled (Game gates the interaction on InIntermission). Host owns destination effects; every machine renders
// its own arch and teleports its own local player. Marked on the minimap. (see the garden-portal region of Game.cs)
public partial class GardenPortal : Node3D
{
    public int NetId;                 // stable id shared across peers
    public int Pair;                  // both ends of a set share this
    public Vector3 Link;              // where stepping through sends you (the partner's position)
    public int Kind = 0;              // 0 = plain return end, 1 = maze gate side, 2 = gold-chest side, 3 = ambush side
    public bool IsEntrance = false;   // the scattered, minimap-marked A-side
    public bool EntersMaze = false;   // the maze gate arch (hold E enters the maze instead of teleporting)
    public Color Tint = new Color(1f, 0.4f, 0.85f);
    public bool Remote = false;       // client ghost (visual only; still teleports the local player)
    public float Cooldown = 0f;       // arrival grace so you don't instantly bounce back through

    private MeshInstance3D _disc, _spinRing;
    private StandardMaterial3D _discMat, _spinMat;
    private OmniLight3D _light;
    private float _spin = 0f, _pulse = 0f, _lit = 0f;

    public override void _Ready()
    {
        var col = Tint;
        var stone = Game.ToonEmissive(new Color(0.44f, 0.44f, 0.42f), 0.05f, 0.03f);
        var stoneDk = Game.ToonEmissive(new Color(0.30f, 0.30f, 0.29f), 0.04f, 0.03f);
        var moss = Game.ToonEmissive(new Color(0.20f, 0.42f, 0.22f), 0.15f, 0.04f);

        // stone pedestal
        var ped = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 2.3f, BottomRadius = 2.7f, Height = 0.7f, RadialSegments = 10 }, MaterialOverride = stoneDk };
        ped.Position = new Vector3(0, 0.35f, 0); AddChild(ped);
        var ped2 = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 2.0f, BottomRadius = 2.3f, Height = 0.35f, RadialSegments = 10 }, MaterialOverride = stone };
        ped2.Position = new Vector3(0, 0.85f, 0); AddChild(ped2);

        // two weathered side pillars, moss-crept
        for (int s = -1; s <= 1; s += 2)
        {
            var pil = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.75f, 3.4f, 0.75f) }, MaterialOverride = stone };
            pil.Position = new Vector3(s * 2.0f, 2.6f, 0); pil.RotationDegrees = new Vector3(0, s * 4f, s * 2f); AddChild(pil);
            var mo = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.45f, Height = 0.9f }, MaterialOverride = moss };
            mo.Position = new Vector3(s * 2.0f, 1.5f, 0.38f); mo.Scale = new Vector3(1.1f, 1.5f, 0.4f); AddChild(mo);
            var mo2 = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.3f, Height = 0.6f }, MaterialOverride = moss };
            mo2.Position = new Vector3(s * 2.0f, 3.6f, -0.35f); mo2.Scale = new Vector3(1f, 0.8f, 0.4f); AddChild(mo2);
        }

        // the stone arch ring (vertical doorway) resting on the pillars
        var arch = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = 1.7f, OuterRadius = 2.3f, RingSegments = 12, Rings = 8 }, MaterialOverride = stone };
        arch.Position = new Vector3(0, 3.9f, 0); arch.RotationDegrees = new Vector3(90, 0, 0); AddChild(arch);   // stand it up → doorway faces ±Z
        var mtop = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.55f, Height = 1.1f }, MaterialOverride = moss };
        mtop.Position = new Vector3(0.5f, 6.0f, 0.1f); mtop.Scale = new Vector3(1.5f, 0.55f, 0.55f); AddChild(mtop);

        // the shimmering portal membrane inside the ring
        _disc = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 1.7f, BottomRadius = 1.7f, Height = 0.12f } };
        _discMat = new StandardMaterial3D {
            AlbedoColor = new Color(col.R, col.G, col.B, 0.5f),
            EmissionEnabled = true, Emission = col, EmissionEnergyMultiplier = 2.2f,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        };
        _disc.MaterialOverride = _discMat; _disc.Position = new Vector3(0, 3.9f, 0); _disc.RotationDegrees = new Vector3(90, 0, 0); AddChild(_disc);

        // a bright inner ring that spins ONLY between waves
        _spinRing = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = 1.45f, OuterRadius = 1.68f } };
        _spinMat = new StandardMaterial3D { AlbedoColor = col, EmissionEnabled = true, Emission = col, EmissionEnergyMultiplier = 3.4f, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
        _spinRing.MaterialOverride = _spinMat; _spinRing.Position = new Vector3(0, 3.9f, 0); _spinRing.RotationDegrees = new Vector3(90, 0, 0); AddChild(_spinRing);

        _light = new OmniLight3D { OmniRange = 11f, LightColor = col, LightEnergy = 2f, Position = new Vector3(0, 3.9f, 0) };
        AddChild(_light);
    }

    public override void _Process(double delta)
    {
        if (Game.I == null) return;
        float dt = (float)delta;
        if (Cooldown > 0f) Cooldown -= dt;
        _pulse += dt;

        bool active = Game.I.InIntermission;   // usable ONLY between waves — dormant + faint while a wave is on
        _lit = Mathf.MoveToward(_lit, active ? 1f : 0f, dt * 2.5f);
        if (active) { _spin += dt * 48f; if (_spinRing != null) _spinRing.RotationDegrees = new Vector3(90, 0, _spin); }   // spins only when live

        float glow = 0.15f + 0.85f * _lit;
        float shimmer = 0.85f + 0.15f * Mathf.Sin(_pulse * 3f) * _lit;
        if (_discMat != null) { _discMat.AlbedoColor = new Color(Tint.R, Tint.G, Tint.B, (0.12f + 0.42f * _lit) * shimmer); _discMat.EmissionEnergyMultiplier = 0.5f + 2.2f * _lit; }
        if (_spinMat != null) _spinMat.EmissionEnergyMultiplier = 0.6f + 3.0f * _lit;
        if (_light != null) _light.LightEnergy = 0.45f + 2f * _lit * shimmer;
    }
}
