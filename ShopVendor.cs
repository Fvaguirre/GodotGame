using Godot;

// ShopVendor.cs — the wandering peddler. Unlike the Mystic/Scroll (one-shot, consumed on open), the shop LINGERS a
// couple of waves and is NOT consumed: both players can open it, and each shops their OWN instanced inventory (rolled
// per-machine from their witch). Host-spawned + synced to clients via VendorSnapshot (kind 2). See Game.SpawnShop.
public partial class ShopVendor : Node3D
{
    public int NetId = 0;
    public bool Remote = false;
    public int SpawnedWave = 0;   // host bookkeeping: the wave it appeared, for the ~2-wave lifetime + "leaving soon" warning
    private float _bob;
    private Node3D _rig;

    // Persisted inventory — this peddler rolls its stock ONCE (the first time it's opened) and keeps it, including which
    // slots you've already bought, so leaving and re-opening the shop shows the SAME wares instead of a fresh reroll.
    public bool OfferBuilt = false;
    public readonly System.Collections.Generic.List<UpgradeCard> Cards = new();
    public readonly System.Collections.Generic.List<int> Prices = new();
    public readonly System.Collections.Generic.List<bool> Sold = new();
    public readonly System.Collections.Generic.List<int> Section = new();

    public override void _Ready()
    {
        var gold = new Color(1f, 0.82f, 0.34f);
        var wood = Game.Toon(new Color(0.34f, 0.22f, 0.13f), 0.9f, 0.2f, 0.03f);
        _rig = new Node3D(); AddChild(_rig);
        // a little market cart: counter + canopy + wheels, with wares glinting on top
        var counter = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(1.9f, 0.9f, 1.0f) }, MaterialOverride = wood };
        counter.Position = new Vector3(0, 0.75f, 0); _rig.AddChild(counter);
        var canopy = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(2.2f, 0.14f, 1.3f) }, MaterialOverride = Game.ToonEmissive(new Color(0.62f, 0.2f, 0.28f), 0.5f, 0f) };
        canopy.Position = new Vector3(0, 2.1f, 0); canopy.RotationDegrees = new Vector3(8, 0, 0); _rig.AddChild(canopy);
        for (int sx = -1; sx <= 1; sx += 2)
        {
            var post = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.05f, BottomRadius = 0.05f, Height = 1.3f }, MaterialOverride = wood };
            post.Position = new Vector3(sx * 0.95f, 1.45f, -0.5f); _rig.AddChild(post);
            var wheel = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = 0.24f, OuterRadius = 0.36f }, MaterialOverride = wood };
            wheel.Position = new Vector3(sx * 0.95f, 0.36f, 0.55f); wheel.RotationDegrees = new Vector3(0, 90, 0); _rig.AddChild(wheel);
        }
        // glinting wares on the counter
        var rng = new System.Random(unchecked(NetId * 2654435761u).GetHashCode());
        for (int i = 0; i < 5; i++)
        {
            var col = Rarities.Col((Rarity)(rng.Next(0, 5)));
            var gem = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.11f, Height = 0.22f }, MaterialOverride = Game.ToonEmissive(col, 2.4f, 0f) };
            gem.Position = new Vector3(-0.7f + i * 0.35f, 1.32f, 0.15f); _rig.AddChild(gem);
        }
        AddChild(new OmniLight3D { OmniRange = 10f, LightColor = gold, LightEnergy = 2.0f, Position = new Vector3(0, 1.9f, 0) });
        Game.AddBeacon(this, gold);
    }

    public override void _Process(double delta)
    {
        if (Game.I == null) return;
        _bob += (float)delta;
        if (_rig != null) _rig.Position = new Vector3(0, Mathf.Sin(_bob * 1.4f) * 0.05f, 0);
    }
}
