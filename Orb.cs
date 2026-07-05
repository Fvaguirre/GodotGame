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
        _mesh = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.5f, 0.5f, 0.5f) } };
        _mesh.MaterialOverride = Game.Emissive(Tint, 1.4f);
        _mesh.RotationDegrees = new Vector3(45, 0, 45);
        AddChild(_mesh);
        AddChild(new OmniLight3D { OmniRange = 4f, LightColor = Tint, LightEnergy = 0.9f });
    }

    public override void _Process(double delta)
    {
        if (Game.I == null || !Game.I.WorldRunning || Game.I.State != GameState.Playing) return;   // freeze while paused (no mid-menu pickups) (NEW)
        float dt = (float)delta;
        _bob += dt * 3f;
        Life -= dt;
        _mesh.RotateY(dt * 2f);
        if (Remote) return;   // client ghost: host handles collection; position comes from the snapshot

        // home toward the nearest player (host or ally); collection grants XP to everyone
        Vector3 pp = Game.I.ResolveEnemyTarget(GlobalPosition, false, out long _, out bool _);
        {
            float d = new Vector2(GlobalPosition.X - pp.X, GlobalPosition.Z - pp.Z).Length();
            var here = GlobalPosition;
            float gy = Game.I.SurfaceHeight(GlobalPosition, 1e9f);   // (NEW) sit above the actual (hilly) ground, not a fixed world Y
            here.Y = gy + 1.2f + Mathf.Sin(_bob) * 0.2f;
            if (d < 10f)
            {
                var target = new Vector3(pp.X, gy + 1.4f, pp.Z);
                here = here.Lerp(target, Mathf.Clamp(dt * (3f + (10f - d)), 0, 1));
            }
            GlobalPosition = here;
            if (d < 2.4f) { Game.I.GrantSharedXp(Xp); QueueFree(); return; }
        }

        if (Life <= 0) QueueFree();
    }
}
