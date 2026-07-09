using Godot;

// Orb.cs — a generic floating pickup orb (XP / mana / pickups). RemotePickup mirrors it for clients.
public partial class Orb : Node3D
{
    public float Xp = 10f;
    public float Life = 22f;
    public Color Tint = Palette.Ember;
    public int NetId = 0;
    public bool Remote = false;
    private float _bob;
    private MeshInstance3D _mesh;

    public override void _Ready()
    {
        _bob = (float)GD.RandRange(0, 6.28);
        _mesh = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.32f, 0.32f, 0.32f) } };   // small persistent speck
        _mesh.MaterialOverride = Game.Emissive(Tint, 1.6f);
        _mesh.RotationDegrees = new Vector3(45, 0, 45);
        AddChild(_mesh);
        AddChild(new OmniLight3D { OmniRange = 2.6f, LightColor = Tint, LightEnergy = 0.7f });
    }

    public override void _Process(double delta)
    {
        if (Game.I == null || !Game.I.WorldRunning || Game.I.State != GameState.Playing) return;   // freeze while paused (no mid-menu pickups) (NEW)
        float dt = (float)delta;
        _bob += dt * 3f;
        _mesh.RotateY(dt * 2f);
        if (Remote) return;   // client ghost: host handles collection; position comes from the snapshot

        // orbs now PERSIST (no despawn) and only collect within the small PickupRange — unless a chest lodestone (magnet) is
        // active, which vacuums every orb to the party. Homing to the nearest player; collection grants XP to everyone.
        Vector3 pp = Game.I.ResolveEnemyTarget(GlobalPosition, false, out long _, out bool _);
        float d = new Vector2(GlobalPosition.X - pp.X, GlobalPosition.Z - pp.Z).Length();
        var here = GlobalPosition;
        float gy = Game.I.SurfaceHeight(GlobalPosition, 1e9f);   // sit above the actual (hilly) ground
        here.Y = gy + 0.7f + Mathf.Sin(_bob) * 0.15f;            // a low, small persistent speck
        bool magnet = Game.I.MagnetActive;
        float range = Game.I.PickupRange;
        if (magnet)   // lodestone: fly in from anywhere, collect when it reaches the player
        {
            here = here.Lerp(new Vector3(pp.X, gy + 1.2f, pp.Z), Mathf.Clamp(dt * 15f, 0f, 1f));
            if (new Vector2(here.X - pp.X, here.Z - pp.Z).Length() < 1.4f) { Game.I.GrantSharedXp(Xp); QueueFree(); return; }
            GlobalPosition = here; return;
        }
        if (d < range) { Game.I.GrantSharedXp(Xp); QueueFree(); return; }   // within the pickup radius → collect reliably (no homing-gate to graze past)
        GlobalPosition = here;   // otherwise idle-bob in place until a warden comes near
    }
}
