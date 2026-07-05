using Godot;

// Frost witch ult: a giant rolling snowball elemental that charts a semi-random path favouring dense enemy
// clusters, grinding foes for frost damage and flinging the small/medium ones as it rolls through them.
// Host applies damage/fling; allies see a ghost that the host repositions each frame.
public partial class FrostElemental : Node3D
{
    private Player _caster;
    private float _size, _dur, _dmg, _life = 0f, _retargetT = 0f, _netT = 0f;
    private Vector3 _vel;
    private bool _remote, _split;
    private MeshInstance3D _ball;

    public void Init(Player caster, Vector3 pos, float size, float dur, float dmg, bool remote, bool split = false)
    {
        _caster = caster; _size = size; _dur = dur; _dmg = dmg; _remote = remote; _split = split;
        GlobalPosition = new Vector3(pos.X, size, pos.Z);
        var col = new Color(0.82f, 0.93f, 1f);
        _ball = new MeshInstance3D { Mesh = new SphereMesh { Radius = size, Height = size * 2f }, MaterialOverride = Game.ToonEmissive(col, 1.3f, 0f) };
        AddChild(_ball);
        // a few chunky ice shards embedded so the roll reads
        for (int i = 0; i < 6; i++)
        {
            var sh = new MeshInstance3D { Mesh = new BoxMesh { Size = Vector3.One * size * 0.5f }, MaterialOverride = Game.Emissive(col, 1.8f) };
            sh.Position = new Vector3(GD.Randf() * 2 - 1, GD.Randf() * 2 - 1, GD.Randf() * 2 - 1).Normalized() * size * 0.8f;
            sh.RotationDegrees = new Vector3(GD.Randf() * 90, GD.Randf() * 90, GD.Randf() * 90);
            _ball.AddChild(sh);
        }
        AddChild(new OmniLight3D { OmniRange = size * 3f, LightColor = col, LightEnergy = 2f, ShadowEnabled = false });
        PickTarget();
    }

    private void PickTarget()
    {
        Vector3 best = GlobalPosition; int bestCount = -1;
        if (Game.I != null)
            foreach (var e in Game.I.Enemies.ToArray())
            {
                if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
                int c = 0;
                foreach (var o in Game.I.Enemies) if (o != null && !o.Dead && o.GlobalPosition.DistanceTo(e.GlobalPosition) < 12f) c++;
                if (c > bestCount) { bestCount = c; best = e.GlobalPosition; }
            }
        var dir = best - GlobalPosition; dir.Y = 0f;
        if (dir.LengthSquared() < 1f) dir = new Vector3(GD.Randf() * 2 - 1, 0, GD.Randf() * 2 - 1);
        // a little randomness so it wanders rather than beelines
        dir = dir.Normalized() + new Vector3(GD.Randf() * 0.6f - 0.3f, 0, GD.Randf() * 0.6f - 0.3f);
        _vel = dir.Normalized() * 15f;
    }

    public override void _Process(double delta)
    {
        var g = Game.I; if (g == null || g.State != GameState.Playing) return;
        float dt = (float)delta; _life += dt;
        if (_ball != null) _ball.RotationDegrees += new Vector3(dt * 220f, 0, 0);   // roll forward (both host + ghost)
        if (!_remote)
        {
            GlobalPosition += _vel * dt;
            float gy = g.SurfaceHeight(GlobalPosition, GlobalPosition.Y) + _size;
            GlobalPosition = new Vector3(GlobalPosition.X, gy, GlobalPosition.Z);
            _retargetT -= dt; if (_retargetT <= 0f) { _retargetT = 1.6f; PickTarget(); }
            foreach (var e in g.Enemies.ToArray())
            {
                if (e == null || e.Dead || !GodotObject.IsInstanceValid(e) || e.Remote) continue;
                if (e.GlobalPosition.DistanceTo(GlobalPosition) < _size + e.Radius + 0.6f)
                {
                    e.Hurt(_dmg * dt * 3f, DamageType.Frost);
                    if (!e.IsBoss) { var f = e.GlobalPosition - GlobalPosition; f.Y = 0f; e.Fling(f.Normalized() * 13f + Vector3.Up * 8f); }
                }
            }
            _netT -= dt; if (_netT <= 0f) { _netT = 0.08f; g.NetMgr?.BroadcastFrostElemMove(GlobalPosition); }
        }
        if (_life >= _dur)
        {
            if (!_remote && _split && _size > 1.2f)   // Avalanche: split into two smaller ones that keep rolling
            {
                for (int i = 0; i < 2; i++)
                {
                    var child = new FrostElemental(); g.AddChild(child);
                    var off = new Vector3(i == 0 ? 2f : -2f, 0, 0);
                    child.Init(_caster, GlobalPosition + off, _size * 0.6f, _dur * 0.6f, _dmg * 0.7f, false, false);
                    g.NetMgr?.BroadcastVfx(53, GlobalPosition + off, Vector3.Zero, _size * 0.6f, _dur * 0.6f, DamageTypes.Col(DamageType.Frost));
                }
            }
            if (_ball != null) { var tw = _ball.CreateTween(); tw.TweenProperty(_ball, "scale", Vector3.Zero, 0.4f); tw.TweenCallback(Callable.From(QueueFree)); }
            else QueueFree();
        }
    }
}
