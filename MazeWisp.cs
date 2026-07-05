using Godot;

// MazeWisp.cs — a breadcrumb the fairy drops at a corridor centre. It glows and points (an arrow) down the
// actual navigable direction toward the exit portal, so players follow the trail through the maze. Persists
// until the maze ends. (Named MazeWisp to avoid the decorative forest Wisp.)
public partial class MazeWisp : Node3D
{
    private float _phase = 0f;
    private Node3D _arrow;
    public Vector3 Dir;   // navigable direction toward the portal (for the minimap arrow)

    public void Init(Vector3 pos, Vector3 dir)
    {
        Dir = dir;
        Game.I?.MazeWisps.Add(this);
        GlobalPosition = pos + new Vector3(0, 1.2f, 0);
        var col = new Color(0.72f, 0.9f, 1f);
        AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.18f, Height = 0.36f }, MaterialOverride = Game.Emissive(col, 4f) });
        AddChild(new OmniLight3D { OmniRange = 4.5f, LightColor = col, LightEnergy = 1.6f });

        if (dir.LengthSquared() > 0.01f)
        {
            var aMat = Game.Emissive(col.Lerp(Colors.White, 0.35f), 5f);
            _arrow = new Node3D { Rotation = new Vector3(0, Mathf.Atan2(dir.X, dir.Z), 0) };   // local +Z faces the corridor
            AddChild(_arrow);
            _arrow.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.1f, 0.1f, 0.7f) }, MaterialOverride = aMat, Position = new Vector3(0, 0, 0.45f) });
            _arrow.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.26f, 0.1f, 0.26f) }, MaterialOverride = aMat, Position = new Vector3(0, 0, 0.9f), RotationDegrees = new Vector3(0, 45, 0) });
        }
    }

    public override void _Process(double delta)
    {
        if (Game.I == null || !Game.I.InMaze) { QueueFree(); return; }   // cleared when the maze ends
        float dt = (float)delta; _phase += dt * 2.2f;
        Position = new Vector3(Position.X, Position.Y + Mathf.Sin(_phase) * 0.004f, Position.Z);   // gentle bob
        if (_arrow != null) _arrow.RotationDegrees = new Vector3(0, _arrow.RotationDegrees.Y, Mathf.Sin(_phase) * 4f);
    }
}
