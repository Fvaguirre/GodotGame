using Godot;

// A spectral scroll-keeper who appears every so often (one at a time, lingers until found).
// He offers spell-combos and charge-modifiers you don't yet own, so you can pick one up or swap.
// ScrollVendor.cs — the scroll vendor event: buy a chosen modifier/finisher scroll between waves.
public partial class ScrollVendor : Node3D
{
    public bool Triggered = false;
    public int NetId = 0;
    public bool Remote = false;
    private float _bob;
    private Node3D _rig;

    public override void _Ready()
    {
        var col = DamageTypes.Col(DamageType.Nature);
        var gold = new Color(1f, 0.82f, 0.34f);
        _rig = new Node3D(); AddChild(_rig);
        var stand = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.5f, BottomRadius = 0.7f, Height = 1.5f }, MaterialOverride = Game.Toon(new Color(0.10f, 0.22f, 0.14f), 0.9f, 0.2f, 0.03f) };
        stand.Position = new Vector3(0, 0.75f, 0); _rig.AddChild(stand);
        // open scroll (two angled panels)
        var pL = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.9f, 1.1f, 0.06f) }, MaterialOverride = Game.ToonEmissive(new Color(0.95f, 0.92f, 0.78f), 0.6f, 0f) };
        pL.Position = new Vector3(-0.45f, 1.9f, 0); pL.RotationDegrees = new Vector3(0, 18, 0); _rig.AddChild(pL);
        var pR = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.9f, 1.1f, 0.06f) }, MaterialOverride = Game.ToonEmissive(new Color(0.95f, 0.92f, 0.78f), 0.6f, 0f) };
        pR.Position = new Vector3(0.45f, 1.9f, 0); pR.RotationDegrees = new Vector3(0, -18, 0); _rig.AddChild(pR);
        AddChild(new OmniLight3D { OmniRange = 9f, LightColor = col, LightEnergy = 1.8f, Position = new Vector3(0, 1.9f, 0) });
        Game.AddBeacon(this, col);
    }

    public override void _Process(double delta)
    {
        if (Game.I == null) return;
        float dt = (float)delta;
        _bob += dt;
        if (_rig != null) _rig.RotationDegrees = new Vector3(0, Mathf.Sin(_bob * 0.8f) * 10f, 0);
    }
}
