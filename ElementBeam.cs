using Godot;

// A column of colored light that descends around the witch when she re-attunes an element.
// ElementBeam.cs — the channeled aimable beam used by the Beam finisher and Holy ray. Tinted by DamageType; ticks while held.
public partial class ElementBeam : Node3D
{
    private float _life = 1.6f, _max = 1.6f;
    private StandardMaterial3D _col, _ring;
    private MeshInstance3D _ringNode;
    private OmniLight3D _light;
    private float _lightBase;

    public void Init(Color c)
    {
        // tall translucent column
        var beam = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 2.4f, BottomRadius = 2.9f, Height = 26f } };
        _col = Game.Emissive(c, 2.2f);
        _col.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        _col.AlbedoColor = new Color(c.R, c.G, c.B, 0.42f);
        beam.MaterialOverride = _col;
        beam.Position = new Vector3(0, 13f, 0);
        AddChild(beam);

        // bright ground ring
        var ring = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = 2.6f, OuterRadius = 3.2f } };
        _ring = Game.Emissive(c, 3f);
        _ring.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        ring.MaterialOverride = _ring;
        ring.Position = new Vector3(0, 0.1f, 0);
        _ringNode = ring;
        AddChild(ring);

        _light = new OmniLight3D { OmniRange = 14f, LightColor = c, LightEnergy = 4f };
        _light.Position = new Vector3(0, 4f, 0);
        _lightBase = _light.LightEnergy;
        AddChild(_light);
    }

    public override void _Process(double delta)
    {
        if (Game.I == null || !Game.I.SimActive) return;   // freeze while paused (NEW)
        float dt = (float)delta;
        _life -= dt;
        float t = Mathf.Clamp(_life / _max, 0, 1);
        var a = _col.AlbedoColor; a.A = t * 0.42f; _col.AlbedoColor = a;
        _col.EmissionEnergyMultiplier = 2.2f * t;
        var ra = _ring.AlbedoColor; ra.A = t; _ring.AlbedoColor = ra;
        _ringNode.Scale = Vector3.One * Mathf.Lerp(1.4f, 1f, t);
        _light.LightEnergy = _lightBase * t;
        if (_life <= 0) QueueFree();
    }
}
