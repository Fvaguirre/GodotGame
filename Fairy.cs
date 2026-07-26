using Godot;

// Fairy.cs — the maze guide. She spawns where the players are and drifts at a walking pace in a straight
// b-line toward the exit portal, passing THROUGH the hedges. Every ~12 units she drops a Wisp at the nearest
// walkable cell, and that wisp points down the actual corridor (BFS) toward the portal — so players lure
// toward the fairy but follow the wisps to navigate. Deterministic: every machine runs its own from the same
// spawn (the grid is identical by seed), so no per-wisp networking is needed.
public partial class Fairy : Node3D
{
    public Vector3 Portal;                 // world portal position (the straight-line target)
    private const float Speed = 3.2f;      // walking pace
    private const float WispEvery = 12f;   // drop a breadcrumb every ~12 units travelled
    private float _sinceWisp = 0f, _phase = 0f;
    private Node3D _body;
    private Node3D _wingL, _wingR;

    public override void _Ready()
    {
        var col = new Color(0.82f, 0.96f, 0.72f);
        _body = new Node3D { Position = new Vector3(0, 1.5f, 0) };
        AddChild(_body);
        var orb = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.22f, Height = 0.44f }, MaterialOverride = Game.Emissive(col.Lerp(Colors.White, 0.4f), 4.5f) };
        _body.AddChild(orb);
        var wingMat = Game.Emissive(col, 2f);
        wingMat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; wingMat.AlbedoColor = new Color(col.R, col.G, col.B, 0.4f); wingMat.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        _wingL = new Node3D { Position = new Vector3(-0.12f, 0.05f, -0.05f) }; _body.AddChild(_wingL);
        _wingL.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.35f, 0.5f, 0.02f) }, MaterialOverride = wingMat, Position = new Vector3(-0.17f, 0, 0) });
        _wingR = new Node3D { Position = new Vector3(0.12f, 0.05f, -0.05f) }; _body.AddChild(_wingR);
        _wingR.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.35f, 0.5f, 0.02f) }, MaterialOverride = wingMat, Position = new Vector3(0.17f, 0, 0) });
        _body.AddChild(new OmniLight3D { OmniRange = 6f, LightColor = col, LightEnergy = 2.4f });

        // a tall ray of light rising from the fairy, visible over the hedges across the maze (NEW)
        var rayMat = Game.Emissive(col, 2.2f);
        rayMat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; rayMat.AlbedoColor = new Color(col.R, col.G, col.B, 0.18f); rayMat.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        var ray = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.35f, BottomRadius = 0.6f, Height = 46f }, MaterialOverride = rayMat };
        ray.Position = new Vector3(0, 23f, 0);
        AddChild(ray);   // child of the fairy → tracks her X/Z, spans y≈0..46 (clears the 28-tall walls)
        _body.AddChild(new AudioStreamPlayer3D { Stream = Sfx.FairyDustStream(), Autoplay = true, VolumeDb = -9f, MaxDistance = 22f, UnitSize = 4f });   // hear her nearby

        _sinceWisp = WispEvery;   // drop one immediately so the way is marked from the start
    }

    private Vector3 _wp; private bool _haveWp = false;

    public override void _Process(double delta)
    {
        if (Game.I == null || !Game.I.InMaze) { QueueFree(); return; }
        float dt = (float)delta; _phase += dt * 8f;

        var toPortal = Portal - GlobalPosition; toPortal.Y = 0f;
        if (toPortal.Length() < 1.8f) { HighlightPortal(); QueueFree(); return; }   // reached the exit

        var m = Game.I.MazeInfo;
        if (m != null)   // follow the CORRIDORS toward the exit, cell to cell — never cut through a hedge
        {
            var cell = Maze.CellOf(m, GlobalPosition);
            if (!_haveWp || new Vector2(GlobalPosition.X - _wp.X, GlobalPosition.Z - _wp.Z).Length() < 0.5f)
            {
                var dir = Game.I.MazePathDir(cell);   // axis-aligned step down the path
                if (dir.LengthSquared() > 0.001f)
                {
                    var next = cell + new Vector2I(Mathf.RoundToInt(dir.X), Mathf.RoundToInt(dir.Z));
                    _wp = m.CellCenter(m.In(next) && !m.Blocked(cell, next) ? next : cell);   // stay centred in the corridor
                }
                else _wp = m.CellCenter(cell);
                _haveWp = true;
            }
            var to = _wp - GlobalPosition; to.Y = 0f;
            if (to.LengthSquared() > 1e-5f) GlobalPosition += to.Normalized() * Speed * dt;
        }
        else GlobalPosition += toPortal.Normalized() * Speed * dt;

        _body.Position = new Vector3(0, 1.5f + Mathf.Sin(_phase) * 0.18f, 0);
        float flap = Mathf.Sin(_phase * 2.2f) * 0.5f;
        if (_wingL != null) _wingL.Rotation = new Vector3(0, 0.5f + flap, 0);
        if (_wingR != null) _wingR.Rotation = new Vector3(0, -0.5f - flap, 0);

        _sinceWisp += Speed * dt;
        if (_sinceWisp >= WispEvery) { _sinceWisp = 0f; DropWisp(); }
    }

    private void DropWisp()
    {
        var m = Game.I.MazeInfo;
        if (m == null) return;
        var cell = Maze.CellOf(m, GlobalPosition);           // the walkable cell she's currently over
        int open = 0;                                        // count open passages out of this cell
        foreach (var d in new[] { new Vector2I(0, 1), new Vector2I(0, -1), new Vector2I(1, 0), new Vector2I(-1, 0) })
        { var n = cell + d; if (m.In(n) && !m.Blocked(cell, n)) open++; }
        if (open > 2) return;                                // 3-4 open sides = open area / big middle → no wisp here, only corridors & doorways
        var wpos = m.CellCenter(cell);                       // wisp sits at the corridor centre (never in a hedge)
        var dir = Game.I.MazePathDir(cell);                  // navigable direction toward the portal
        var w = new MazeWisp(); Game.I.AddChild(w); w.Init(wpos, dir);
    }

    private void HighlightPortal()
    {
        var col = new Color(0.7f, 0.6f, 1f);
        var flash = new OmniLight3D { OmniRange = 40f, LightColor = col, LightEnergy = 7f };
        Game.I.AddChild(flash); flash.GlobalPosition = Portal + new Vector3(0, 6f, 0);
        var ft = flash.CreateTween();
        ft.TweenProperty(flash, "light_energy", 2.5f, 1.2f);   // flare, then settle bright to keep marking the exit
        var burstMat = Game.Emissive(col.Lerp(Colors.White, 0.4f), 4f);
        burstMat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; burstMat.AlbedoColor = new Color(col.R, col.G, col.B, 0.6f); burstMat.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        var burst = new MeshInstance3D { Mesh = new SphereMesh { Radius = 2f, Height = 4f }, MaterialOverride = burstMat };
        Game.I.AddChild(burst); burst.GlobalPosition = Portal + new Vector3(0, 4f, 0); burst.Scale = Vector3.One * 0.2f;
        var bt = burst.CreateTween(); bt.SetParallel(true);
        bt.TweenProperty(burst, "scale", Vector3.One * 4f, 0.9f).SetEase(Tween.EaseType.Out);
        bt.TweenProperty(burst, "transparency", 1f, 0.9f);
        bt.SetParallel(false);
        bt.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(burst)) burst.QueueFree(); if (GodotObject.IsInstanceValid(flash)) flash.QueueFree(); }));
    }
}
