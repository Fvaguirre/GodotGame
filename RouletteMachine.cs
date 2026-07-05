using Godot;

// A wheel of fortune that randomly appears (one at a time). Spend escalating gold for up to 3 gambles.
// RouletteMachine.cs — the wheel-of-fortune event (every ~10 waves): spin for a randomized reward.
public partial class RouletteMachine : Node3D
{
    public int Pulls = 0;
    public bool Triggered = false;
    public int NetId = 0;
    public bool Remote = false;
    private float _spin;
    private MeshInstance3D _wheel;

    public override void _Ready()
    {
        var gold = new Color(1f, 0.82f, 0.34f);
        var purple = DamageTypes.Col(DamageType.Curse);
        var post = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.4f, 2.2f, 0.4f) } };
        post.MaterialOverride = Game.Toon(new Color(0.15f, 0.10f, 0.18f), 0.9f, 0.2f, 0.03f);
        post.Position = new Vector3(0, 1.1f, 0);
        AddChild(post);
        _wheel = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = 1.1f, OuterRadius = 1.5f } };
        _wheel.MaterialOverride = Game.ToonEmissive(purple, 1.2f, 0.03f);
        _wheel.Position = new Vector3(0, 2.4f, 0);
        AddChild(_wheel);
        var hub = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.5f, Height = 1f } };
        hub.MaterialOverride = Game.ToonEmissive(gold, 1.4f, 0.03f);
        hub.Position = new Vector3(0, 2.4f, 0);
        AddChild(hub);
        AddChild(new OmniLight3D { OmniRange = 9f, LightColor = purple, LightEnergy = 1.6f, Position = new Vector3(0, 2.4f, 0) });
        Game.AddBeacon(this, new Color(1f, 0.82f, 0.34f));
    }

    public override void _Process(double delta)
    {
        if (Game.I == null) return;
        float dt = (float)delta;
        _spin += dt;
        if (_wheel != null) _wheel.RotationDegrees = new Vector3(0, 0, _spin * 60f);
    }
}
