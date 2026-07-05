using Godot;
using System.Collections.Generic;

// Stampede.cs — the Wild Swarm ultimate. A CONTINUOUS stream of little tree-ent critters (the same body
// as the Verdant Thornlings) pours forward for a few seconds, trampling everything in their lane and
// chanting a steady stream of silly battle-cries, kicking up cartoon dust as they go, then petering out.
// The critters can't be damaged, detonated, or targeted by enemies — they're a pure forward sweep.
// Damage is owner-only (routes to the host like any other hit); allies get a visual-only copy + the chant.
public partial class Stampede : Node3D
{
    private Player _caster;
    private Vector3 _origin, _fwd, _right;
    private float _width, _dmg, _dur, _travel, _speed = 16f;
    private bool _visualOnly;
    private float _elapsed = 0f, _spawnAcc = 0f, _chantAcc = 0.3f, _chantEvery = 0.5f, _dustT = 0f;

    private class Crit { public Node3D Node; public float Lane, Dist, Speed, Phase; }
    private readonly List<Crit> _crits = new();
    private readonly Dictionary<ulong, float> _hitCd = new();

    private static readonly string[] Lines = {
        "WEEEE!", "I get the eyeballs!", "CHAAARGE!", "for the grove!", "outta my way!",
        "yeehaw!", "snack time!", "last one's a toad!", "bonk!", "wheeee!", "I'm helping!", "LEEEROY!",
        "trample trample!", "Motherrr watches!", "squish 'em!", "no brakes!", "rooty tooty!"
    };

    public void Init(Player caster, Vector3 origin, Vector3 fwd, float width, float dmg, float durationSec, bool visualOnly)
    {
        _caster = caster; _origin = origin; _fwd = fwd.Normalized(); _width = width; _dmg = dmg; _dur = durationSec; _visualOnly = visualOnly;
        _right = new Vector3(_fwd.Z, 0, -_fwd.X);
        _travel = _dur * _speed + 6f;            // how far a critter runs before it peels off and fades
        Game.I.Sfx?.Rustle();
    }

    private void SpawnCritter()
    {
        var bark = Game.ToonEmissive(new Color(0.42f, 0.30f, 0.18f), 0.4f, 0.03f);
        var leaf = Game.ToonEmissive(new Color(0.30f, 0.72f, 0.34f), 0.7f, 0.04f);
        var glow = Game.ToonEmissive(new Color(0.55f, 1f, 0.5f), 1.6f, 0.02f);
        float s = (float)GD.RandRange(0.5, 0.8);

        var node = new Node3D();
        AddChild(node);
        void Add(Mesh m, Material mat, Vector3 pos, Vector3 rotDeg = default)
        { var mi = new MeshInstance3D { Mesh = m, MaterialOverride = mat }; mi.Position = pos; mi.RotationDegrees = rotDeg; node.AddChild(mi); }
        Add(new CylinderMesh { TopRadius = 0.22f, BottomRadius = 0.3f, Height = 1.0f }, bark, new Vector3(0, 0.5f, 0));   // trunk
        Add(new SphereMesh { Radius = 0.55f, Height = 1.1f }, leaf, new Vector3(0, 1.25f, 0));                            // canopy
        Add(new SphereMesh { Radius = 0.36f, Height = 0.72f }, leaf, new Vector3(0.32f, 1.5f, 0.1f));
        Add(new SphereMesh { Radius = 0.32f, Height = 0.64f }, leaf, new Vector3(-0.3f, 1.45f, -0.1f));
        Add(new SphereMesh { Radius = 0.06f, Height = 0.12f }, glow, new Vector3(0.12f, 0.95f, 0.28f));                   // eyes
        Add(new SphereMesh { Radius = 0.06f, Height = 0.12f }, glow, new Vector3(-0.12f, 0.95f, 0.28f));
        node.Scale = Vector3.One * s;
        node.Rotation = new Vector3(0, Mathf.Atan2(_fwd.X, _fwd.Z), 0);

        float lane = (float)GD.RandRange(-_width * 0.5, _width * 0.5);
        float back = (float)GD.RandRange(0.0, 3.0);
        var p = _origin + _right * lane - _fwd * back;
        node.GlobalPosition = new Vector3(p.X, 0, p.Z);
        _crits.Add(new Crit { Node = node, Lane = lane, Dist = -back, Speed = _speed * (float)GD.RandRange(0.92, 1.12), Phase = (float)GD.RandRange(0.0, 6.28) });
    }

    public override void _Process(double delta)
    {
        if (Game.I == null || Game.I.State != GameState.Playing) return;   // freeze while paused (NEW)
        float dt = (float)delta;
        _elapsed += dt;

        // continuous spawning at the back while the window is open → a flowing line, not a single row
        if (_elapsed < _dur)
        {
            _spawnAcc += dt;
            float every = 0.08f;
            while (_spawnAcc >= every) { _spawnAcc -= every; SpawnCritter(); if (GD.Randf() < 0.5f) SpawnCritter(); }
        }

        // advance critters; retire those that have run their distance
        for (int i = _crits.Count - 1; i >= 0; i--)
        {
            var c = _crits[i];
            if (!GodotObject.IsInstanceValid(c.Node)) { _crits.RemoveAt(i); continue; }
            c.Dist += c.Speed * dt;
            c.Phase += dt * 16f;
            var bp = _origin + _fwd * c.Dist + _right * c.Lane;
            float hop = Mathf.Abs(Mathf.Sin(c.Phase)) * 0.3f;
            float gy = Game.I != null ? Game.I.SurfaceHeight(new Vector3(bp.X, 0f, bp.Z), 1e9f) : 0f;   // (NEW) run ON the ground, not through hills
            c.Node.GlobalPosition = new Vector3(bp.X, gy + hop, bp.Z);
            c.Node.Rotation = new Vector3(Mathf.Sin(c.Phase) * 0.18f, Mathf.Atan2(_fwd.X, _fwd.Z), 0);   // lean/bob as they run
            if (c.Dist > _travel)
            {
                var node = c.Node; var tw = node.CreateTween(); tw.TweenProperty(node, "scale", Vector3.Zero, 0.18f);
                tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(node)) node.QueueFree(); }));
                _crits.RemoveAt(i);
            }
        }

        // dust puffs across the leading edge
        _dustT -= dt;
        if (_dustT <= 0f && _crits.Count > 0)
        {
            _dustT = 0.05f;
            for (int k = 0; k < 2; k++)
            {
                var c = _crits[(int)(GD.Randi() % (uint)_crits.Count)];
                if (GodotObject.IsInstanceValid(c.Node)) SpawnDust(new Vector3(c.Node.GlobalPosition.X, 0.1f, c.Node.GlobalPosition.Z));
            }
        }

        // continuous chant — a fresh silly line every half-second or so (owner broadcasts so everyone hears the chorus)
        _chantAcc -= dt;
        if (_chantAcc <= 0f && !_visualOnly && _crits.Count > 0)
        {
            _chantAcc = _chantEvery = (float)GD.RandRange(0.35, 0.7);
            var c = _crits[(int)(GD.Randi() % (uint)_crits.Count)];
            if (GodotObject.IsInstanceValid(c.Node))
            {
                string line = Lines[(int)(GD.Randi() % (uint)Lines.Length)];
                var col = new Color(0.55f, 1f, 0.5f);
                Thornling.SpeakAt(c.Node.GlobalPosition, line, 3, col);
                Game.I.NetMgr?.BroadcastMinionSay(c.Node.GlobalPosition, line, 3, col);
            }
        }

        // trample damage: any enemy near a critter takes a hit on a short per-enemy cooldown
        if (!_visualOnly && Game.I != null)
        {
            float now = (float)Time.GetTicksMsec() / 1000f;
            foreach (var e in Game.I.Enemies.ToArray())
            {
                if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
                if (_hitCd.TryGetValue(e.GetInstanceId(), out float t) && now < t) continue;
                bool near = false;
                foreach (var c in _crits)
                    if (GodotObject.IsInstanceValid(c.Node) && c.Node.GlobalPosition.DistanceTo(e.GlobalPosition) < e.Radius + 1.5f) { near = true; break; }
                if (near)
                {
                    _hitCd[e.GetInstanceId()] = now + 0.45f;
                    e.Hurt(_dmg, DamageType.Nature, true, false);
                    e.Knockback(_origin, 1.1f);
                }
            }
        }

        if (_elapsed >= _dur && _crits.Count == 0) QueueFree();
    }

    private void SpawnDust(Vector3 pos)
    {
        var dust = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 0.4f, Height = 0.5f },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.75f, 0.70f, 0.58f, 0.6f),
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
            }
        };
        Game.I.AddChild(dust);
        dust.GlobalPosition = pos;
        dust.Scale = Vector3.One * 0.4f;
        var tw = dust.CreateTween(); tw.SetParallel(true);
        tw.TweenProperty(dust, "scale", Vector3.One * 1.4f, 0.4f);
        tw.TweenProperty(dust, "position", pos + new Vector3(0, 0.6f, 0), 0.4f);
        tw.TweenProperty(dust, "transparency", 1f, 0.4f);
        tw.SetParallel(false);
        tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(dust)) dust.QueueFree(); }));
    }
}
