using Godot;

// A player ping: a beam of light + a ground ring at the pinged spot, plus (if it landed on something named)
// a floating nameplate that scales up with distance so far-off players can still read it. Lives ~2.5s.
public partial class PingMarker : Node3D
{
    private float _life = 2.5f, _age = 0f;
    private Label3D _lbl;
    private MeshInstance3D _ring, _beam;

    public void Init(Vector3 pos, string name, Color col)
    {
        GlobalPosition = pos;

        _beam = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.16f, BottomRadius = 0.16f, Height = 7f, RadialSegments = 8 }, MaterialOverride = Game.Emissive(col, 2.6f) };
        _beam.Position = new Vector3(0, 3.5f, 0);
        AddChild(_beam);

        _ring = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = 0.8f, OuterRadius = 1.1f }, MaterialOverride = Game.Emissive(col, 2.2f) };
        _ring.Position = new Vector3(0, 0.1f, 0);
        AddChild(_ring);

        AddChild(new OmniLight3D { OmniRange = 6f, LightColor = col, LightEnergy = 2.2f, ShadowEnabled = false });

        if (!string.IsNullOrEmpty(name))
        {
            _lbl = new Label3D
            {
                Text = name, FontSize = 110, OutlineSize = 26, PixelSize = 0.01f,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, NoDepthTest = true,
                Modulate = col, OutlineModulate = new Color(0, 0, 0, 1f),
                Position = new Vector3(0, 7.4f, 0)
            };
            AddChild(_lbl);
        }
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        _age += dt; _life -= dt;
        if (_life <= 0f || Game.I == null) { QueueFree(); return; }

        float pop = _age < 0.2f ? _age / 0.2f : 1f;                     // quick pop-in
        if (_ring != null) { _ring.Scale = new Vector3(pop * (1f + 0.12f * Mathf.Sin(_age * 6f)), 1f, pop * (1f + 0.12f * Mathf.Sin(_age * 6f))); _ring.RotationDegrees = new Vector3(0, _age * 60f, 0); }
        if (_beam != null) _beam.Scale = new Vector3(1f, pop, 1f);

        // nameplate grows with distance to the LOCAL camera so it stays readable far away
        if (_lbl != null && Game.I.Player != null && Game.I.Player.Cam != null)
        {
            float d = Game.I.Player.Cam.GlobalPosition.DistanceTo(GlobalPosition);
            _lbl.PixelSize = Mathf.Clamp(0.009f + d * 0.0007f, 0.009f, 0.06f);
        }

        // gentle fade in the last 0.5s
        float a = Mathf.Clamp(_life / 0.5f, 0f, 1f);
        if (_lbl != null) _lbl.Modulate = new Color(_lbl.Modulate.R, _lbl.Modulate.G, _lbl.Modulate.B, a);
    }
}
