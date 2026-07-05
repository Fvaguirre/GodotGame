using Godot;

// A wandering Mystic who appears every so often (one at a time, lingers until found).
// For 100 gold each he'll re-attune your left-click and/or right-click element.
// Mystic.cs — the Mystic vendor event: spend score/tokens for a curated upgrade between waves.
public partial class Mystic : Node3D
{
    public bool Triggered = false;
    public int NetId = 0;
    public bool Remote = false;
    private float _bob;
    private Node3D _rig;

    public override void _Ready()
    {
        var arc = DamageTypes.Col(DamageType.Arcane);
        var gold = new Color(1f, 0.82f, 0.34f);
        _rig = new Node3D(); AddChild(_rig);
        // robed body (tapered) + hooded head + floating orb
        var robe = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.25f, BottomRadius = 0.9f, Height = 2.0f }, MaterialOverride = Game.Toon(new Color(0.18f, 0.10f, 0.30f), 0.9f, 0.2f, 0.03f) };
        robe.Position = new Vector3(0, 1.0f, 0); _rig.AddChild(robe);
        var hood = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.35f, Height = 0.7f }, MaterialOverride = Game.Toon(new Color(0.12f, 0.07f, 0.22f), 0.9f, 0.2f, 0.03f) };
        hood.Position = new Vector3(0, 2.1f, 0); _rig.AddChild(hood);
        var orb = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.32f, Height = 0.64f }, MaterialOverride = Game.ToonEmissive(arc, 2.2f, 0f) };
        orb.Position = new Vector3(0, 1.4f, 0.7f); _rig.AddChild(orb);
        AddChild(new OmniLight3D { OmniRange = 9f, LightColor = arc, LightEnergy = 1.8f, Position = new Vector3(0, 1.8f, 0) });
        Game.AddBeacon(this, arc);
    }

    public override void _Process(double delta)
    {
        if (Game.I == null) return;
        float dt = (float)delta;
        _bob += dt;
        if (_rig != null) _rig.Position = new Vector3(0, Mathf.Sin(_bob * 1.6f) * 0.12f, 0);
    }
}
