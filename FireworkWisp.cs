using Godot;
using System.Collections.Generic;

// A firework flare's guide wisp (phase-2 only): rises to ~12u, pathfinds along the maze corridors toward the
// portal for 5 seconds leaving a fading trail behind it, then it and its trail vanish. Flies high so allies can
// spot the route over the hedges. Trail dots are TopLevel children (world-space) so they stay put, and are freed
// with the wisp when it self-destructs.
public partial class FireworkWisp : Node3D
{
    private const float Life = 5f, Height = 12f, Speed = 9f, TrailEvery = 0.07f;
    private float _t = 0f, _trailT = 0f;
    private Color _col = Colors.White;
    private MeshInstance3D _orb;
    private StandardMaterial3D _orbMat;
    private readonly List<StandardMaterial3D> _trailMats = new();

    public void Init(Vector3 pos, Color col)
    {
        _col = col;
        GlobalPosition = new Vector3(pos.X, Height, pos.Z);

        _orb = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.6f, Height = 1.2f } };
        _orbMat = Game.ToonEmissive(col, 3f, 0f);
        _orbMat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        _orbMat.AlbedoColor = new Color(col.R, col.G, col.B, 0.95f);
        _orb.MaterialOverride = _orbMat;
        AddChild(_orb);

        var light = new OmniLight3D { LightColor = col, OmniRange = 16f, LightEnergy = 2.2f, ShadowEnabled = false };
        AddChild(light);
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        _t += dt;
        if (_t >= Life || Game.I == null || !Game.I.InMaze) { QueueFree(); return; }

        // pathfind toward the portal along the corridor gradient (fall back to straight-line if none)
        var dir = Game.I.MazePortalDir(GlobalPosition);
        if (dir.LengthSquared() < 0.01f) { var pp = Game.I.MazePortalWorld; dir = new Vector3(pp.X - GlobalPosition.X, 0f, pp.Z - GlobalPosition.Z); }
        if (dir.LengthSquared() > 0.01f) GlobalPosition += dir.Normalized() * Speed * dt;
        GlobalPosition = new Vector3(GlobalPosition.X, Height, GlobalPosition.Z);   // hold altitude

        // fade near the end
        float a = Mathf.Clamp((Life - _t) / 0.6f, 0f, 1f);
        if (_orbMat != null) _orbMat.AlbedoColor = new Color(_col.R, _col.G, _col.B, 0.95f * a);

        // drop trail breadcrumbs (world-space, so they stay put and gently fade)
        _trailT -= dt;
        if (_trailT <= 0f)
        {
            _trailT = TrailEvery;
            var dot = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.28f, Height = 0.56f } };
            var dm = Game.ToonEmissive(_col, 2f, 0f);
            dm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; dm.AlbedoColor = new Color(_col.R, _col.G, _col.B, 0.7f);
            dot.MaterialOverride = dm;
            AddChild(dot);
            dot.TopLevel = true;                 // ignore my transform → stays in world space
            dot.GlobalPosition = GlobalPosition;
            _trailMats.Add(dm);
        }
        foreach (var dm in _trailMats)   // trail fades with the wisp's overall fade
            dm.AlbedoColor = new Color(_col.R, _col.G, _col.B, dm.AlbedoColor.A * (0.985f) * (0.4f + 0.6f * a));
    }
}
