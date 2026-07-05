using Godot;

// A reward chest scattered through the world (more common near landmarks). Opens on approach.
// Chest.cs — a lootable chest pickup (reward container). Spawned as a run reward; opening grants cards/tokens.
public partial class Chest : Node3D
{
    public bool Opened = false;
    public int NetId = 0;
    public bool Remote = false;   // client-side ghost: never opens locally, host owns it
    private float _bob;
    private MeshInstance3D _lid;
    private OmniLight3D _light;
    private Node3D _beacon;

    public override void _Ready()
    {
        var gold = new Color(1f, 0.82f, 0.34f);
        var body = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(1.4f, 0.9f, 1.0f) } };
        body.MaterialOverride = Game.Toon(new Color(0.20f, 0.12f, 0.06f), 0.9f, 0.25f, 0.03f);
        body.Position = new Vector3(0, 0.45f, 0);
        AddChild(body);
        _lid = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(1.45f, 0.35f, 1.05f) } };
        _lid.MaterialOverride = Game.Toon(new Color(0.26f, 0.16f, 0.08f), 0.9f, 0.25f, 0.03f);
        _lid.Position = new Vector3(0, 1.0f, 0);
        AddChild(_lid);
        var seam = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(1.5f, 0.12f, 1.1f) } };
        seam.MaterialOverride = Game.ToonEmissive(gold, 1.2f, 0f);
        seam.Position = new Vector3(0, 0.82f, 0);
        AddChild(seam);
        _light = new OmniLight3D { OmniRange = 7f, LightColor = gold, LightEnergy = 1.4f, Position = new Vector3(0, 1.4f, 0) };
        AddChild(_light);
        _beacon = Game.AddBeacon(this, gold);
    }

    public override void _Process(double delta)
    {
        if (Opened || Remote || Game.I == null || !Game.I.WorldRunning) return;
        float dt = (float)delta;
        _bob += dt;
        _lid.Position = new Vector3(0, 1.0f + Mathf.Sin(_bob * 2f) * 0.04f, 0);
        if (_light != null) _light.LightEnergy = 1.2f + Mathf.Sin(_bob * 3f) * 0.4f;
        // opening is now driven by the hold-E interaction system (whoever holds E first gets it)
    }

    public void Open(long openerPeer = 0)
    {
        if (Opened) return;
        Opened = true;
        _lid.RotationDegrees = new Vector3(-70, 0, 0);
        _lid.Position = new Vector3(0, 1.1f, -0.5f);
        if (_light != null) _light.LightEnergy = 0.4f;
        if (_beacon != null && GodotObject.IsInstanceValid(_beacon)) { _beacon.QueueFree(); _beacon = null; }
        Game.I.OpenChestReward(GlobalPosition, openerPeer);
    }
}
