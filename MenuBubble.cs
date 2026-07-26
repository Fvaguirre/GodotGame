using Godot;

// MenuBubble.cs — the witchy immunity bubble shown while a warden is in a menu (level-up / shop / equip-swap) and the
// fight rolls on around her (MP). A translucent element-tinted shell wrapped in two crossed rune bands, sat inside a
// slow-spinning summoning-circle sigil on the ground. Built once, tinted to the witch's damage colour, and spins +
// breathes in _Process. SHARED by the local Player (seen from inside) and every RemoteAvatar (so allies see it too).
public partial class MenuBubble : Node3D
{
    private float _t = 0f;
    private MeshInstance3D _shell;
    private Node3D _bandA, _bandB, _circle;
    private OmniLight3D _light;
    private StandardMaterial3D _shellMat, _runeMat;

    public void Build(Color c)
    {
        // --- translucent shell ---
        _shellMat = new StandardMaterial3D {
            AlbedoColor = new Color(c.R, c.G, c.B, 0.10f), EmissionEnabled = true, Emission = c, EmissionEnergyMultiplier = 1.1f,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha, BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
        _shell = new MeshInstance3D { Mesh = new SphereMesh { Radius = 1.5f, Height = 3.0f, RadialSegments = 28, Rings = 16 }, MaterialOverride = _shellMat, Position = new Vector3(0, 1.0f, 0) };
        AddChild(_shell);

        // shared bright rune/glyph material for every band tick, circle ring, spoke and glyph
        _runeMat = new StandardMaterial3D {
            AlbedoColor = c, EmissionEnabled = true, Emission = c, EmissionEnergyMultiplier = 3.4f,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, CullMode = BaseMaterial3D.CullModeEnum.Disabled };

        // --- two crossed rune bands orbiting her torso (armillary look) ---
        _bandA = BuildBand(1.5f, 10, 22f, 0f);  _bandA.Position = new Vector3(0, 1.0f, 0); AddChild(_bandA);
        _bandB = BuildBand(1.4f, 8, 0f, 74f);   _bandB.Position = new Vector3(0, 1.0f, 0); AddChild(_bandB);

        // --- ground summoning circle: two rings + radial spokes + glyph blocks, flat under her ---
        _circle = BuildCircle();
        _circle.Position = new Vector3(0, 0.06f, 0);
        AddChild(_circle);

        _light = new OmniLight3D { LightColor = c, LightEnergy = 1.6f, OmniRange = 6f, Position = new Vector3(0, 1.1f, 0) };
        AddChild(_light);
    }

    // update just the tints when the ally's witch (colour) changes, without rebuilding the geometry
    public void Retint(Color c)
    {
        if (_shellMat != null) { _shellMat.AlbedoColor = new Color(c.R, c.G, c.B, 0.10f); _shellMat.Emission = c; }
        if (_runeMat != null) { _runeMat.AlbedoColor = c; _runeMat.Emission = c; }
        if (_light != null) _light.LightColor = c;
    }

    // a horizontal rune ring studded with N glyph ticks, wrapped in a Y-spinner and tilted to its orbit plane
    private Node3D BuildBand(float radius, int runes, float tiltX, float tiltZ)
    {
        var spinner = new Node3D();
        var tilt = new Node3D { RotationDegrees = new Vector3(tiltX, 0, tiltZ) };
        spinner.AddChild(tilt);
        var ring = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = radius - 0.03f, OuterRadius = radius + 0.03f }, MaterialOverride = _runeMat, RotationDegrees = new Vector3(90, 0, 0) };
        tilt.AddChild(ring);
        for (int i = 0; i < runes; i++)
        {
            float a = i / (float)runes * Mathf.Tau;
            var tick = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.06f, 0.14f, 0.22f) }, MaterialOverride = _runeMat };
            tick.Position = new Vector3(Mathf.Cos(a) * radius, 0, Mathf.Sin(a) * radius);
            tick.Rotation = new Vector3(0, -a, 0);
            tilt.AddChild(tick);
        }
        return spinner;
    }

    private Node3D BuildCircle()
    {
        var circle = new Node3D();
        foreach (float r in new[] { 1.75f, 1.35f })
            circle.AddChild(new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = r - 0.04f, OuterRadius = r + 0.04f }, MaterialOverride = _runeMat, RotationDegrees = new Vector3(90, 0, 0) });
        const int n = 8;
        for (int i = 0; i < n; i++)
        {
            float a = i / (float)n * Mathf.Tau;
            var glyph = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.16f, 0.02f, 0.16f) }, MaterialOverride = _runeMat };
            glyph.Position = new Vector3(Mathf.Cos(a) * 1.55f, 0, Mathf.Sin(a) * 1.55f); glyph.Rotation = new Vector3(0, -a, 0);
            circle.AddChild(glyph);
            var spoke = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.40f, 0.015f, 0.03f) }, MaterialOverride = _runeMat };
            spoke.Position = new Vector3(Mathf.Cos(a) * 1.55f, 0, Mathf.Sin(a) * 1.55f); spoke.Rotation = new Vector3(0, -a, 0);
            circle.AddChild(spoke);
        }
        return circle;
    }

    public override void _Process(double delta)
    {
        _t += (float)delta;
        _bandA?.RotateY((float)delta * 0.9f);
        _bandB?.RotateY((float)delta * -0.7f);
        _circle?.RotateY((float)delta * 0.5f);
        float pulse = 1f + Mathf.Sin(_t * 3f) * 0.03f;
        if (_shell != null) _shell.Scale = Vector3.One * pulse;
        if (_light != null) _light.LightEnergy = 1.4f + Mathf.Sin(_t * 3f) * 0.4f;
    }
}
