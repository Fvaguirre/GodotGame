using Godot;

// Spawned where the Lunar Witch lands a killing blow during Lunar Eclipse: a small dark moon
// erupts with a blazing corona, dealing AoE lunar damage. Damage is applied on the first frame
// (decoupled from the kill's call stack) so chains cascade safely across frames rather than recursing.
// EclipseBurst.cs — the detonation burst of the Lunar 'Eclipse' ultimate. One-shot expanding VFX + AoE.
public partial class EclipseBurst : Node3D
{
    public float Radius = 5f;
    public float Dmg = 12f;
    public bool Remote = false;   // client visual copy: erupts visually, no damage

    private bool _hit = false;
    private float _t = 0f;
    private Node3D _rig;
    private StandardMaterial3D _coronaMat, _discMat;

    public override void _Ready()
    {
        var lun = DamageTypes.Col(DamageType.Lunar);
        _rig = new Node3D(); AddChild(_rig);
        var disc = new MeshInstance3D { Mesh = new SphereMesh { Radius = Radius * 0.45f, Height = Radius * 0.9f } };
        _discMat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.02f, 0.02f, 0.05f),
            EmissionEnabled = true, Emission = new Color(0.06f, 0.05f, 0.14f), EmissionEnergyMultiplier = 0.6f,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
        };
        disc.MaterialOverride = _discMat; _rig.AddChild(disc);
        var corona = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = Radius * 0.45f, OuterRadius = Radius * 0.62f } };
        _coronaMat = new StandardMaterial3D
        {
            AlbedoColor = new Color(lun.R, lun.G, lun.B, 0.9f),
            EmissionEnabled = true, Emission = lun, EmissionEnergyMultiplier = 3.4f,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
        };
        corona.RotationDegrees = new Vector3(90, 0, 0);
        corona.MaterialOverride = _coronaMat; _rig.AddChild(corona);
        _rig.AddChild(new OmniLight3D { OmniRange = Radius * 2.2f, LightColor = lun, LightEnergy = 2.6f });
        _rig.Scale = Vector3.One * 0.3f;
    }

    public override void _Process(double delta)
    {
        var g = Game.I;
        if (g == null || !g.SimActive) return;
        float dt = (float)delta;
        _t += dt;

        if (!_hit && !Remote)   // apply the blast once, on the first frame (not nested in the kill that spawned us)
        {
            _hit = true;
            foreach (var e in g.Enemies.ToArray())
            {
                if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
                var off = new Vector2(e.GlobalPosition.X - GlobalPosition.X, e.GlobalPosition.Z - GlobalPosition.Z);
                if (off.Length() < Radius + e.Radius) e.Hurt(Dmg, DamageType.Lunar, true);
            }
        }

        float k = Mathf.Clamp(_t / 0.45f, 0f, 1f);
        if (_rig != null) _rig.Scale = Vector3.One * (0.3f + 1.0f * k);
        float fade = 1f - k;
        if (_coronaMat != null) _coronaMat.EmissionEnergyMultiplier = 3.4f * fade;
        if (_discMat != null) _discMat.EmissionEnergyMultiplier = 0.6f * fade;
        if (_t >= 0.45f) QueueFree();
    }
}
