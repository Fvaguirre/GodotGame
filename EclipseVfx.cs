using Godot;

// The Lunar Eclipse overhead spectacle: a dark moon with a glowing corona ring that
// hangs above the witch for the duration of the ult, slowly turning and pulsing.
// EclipseVfx.cs — the lingering eclipse aura/sky effect for the Lunar 'Eclipse' ultimate (atmosphere, not damage).
public partial class EclipseVfx : Node3D
{
    public float Dur = 8f, MaxDur = 8f;
    public float Height = 26f;

    private Node3D _rig;
    private StandardMaterial3D _coronaMat;
    private float _spin = 0f;

    public override void _Ready()
    {
        var lun = DamageTypes.Col(DamageType.Lunar);
        _rig = new Node3D();
        AddChild(_rig);

        // dark moon disc
        var disc = new MeshInstance3D { Mesh = new SphereMesh { Radius = 5.5f, Height = 11f } };
        disc.MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.02f, 0.02f, 0.05f),
            EmissionEnabled = true, Emission = new Color(0.05f, 0.04f, 0.12f), EmissionEnergyMultiplier = 0.4f,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
        };
        _rig.AddChild(disc);

        // blazing corona ring
        var corona = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = 5.6f, OuterRadius = 7.4f } };
        _coronaMat = new StandardMaterial3D
        {
            AlbedoColor = new Color(lun.R, lun.G, lun.B, 0.85f),
            EmissionEnabled = true, Emission = lun, EmissionEnergyMultiplier = 3.0f,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
        };
        corona.MaterialOverride = _coronaMat;
        corona.RotationDegrees = new Vector3(90, 0, 0);   // face downward toward the witch
        _rig.AddChild(corona);
        _rig.AddChild(new OmniLight3D { OmniRange = 40f, LightColor = lun, LightEnergy = 2.4f });
    }

    public override void _Process(double delta)
    {
        var g = Game.I;
        if (g == null) return;
        float dt = (float)delta;
        Dur -= dt;
        _spin += dt * 0.5f;
        if (g.Player != null) GlobalPosition = new Vector3(g.Player.GlobalPosition.X, g.Player.GlobalPosition.Y + Height, g.Player.GlobalPosition.Z);
        if (_rig != null) _rig.Rotation = new Vector3(0, _spin, 0);
        if (_coronaMat != null)
        {
            float f = Mathf.Clamp(Dur / Mathf.Max(0.01f, MaxDur), 0f, 1f);
            _coronaMat.EmissionEnergyMultiplier = (2.2f + 1.2f * Mathf.Sin(_spin * 6f)) * Mathf.Clamp(f * 2f, 0f, 1f);
        }
        // not tied to the ult flag (Eclipse is a buff with no node), just its own timer
        if (Dur <= 0f) QueueFree();
    }
}
