using Godot;
using System.Collections.Generic;

// Stampede.cs — the Wild Swarm ultimate. A CONTINUOUS stream of little tree-ent critters pours forward for a few
// seconds, trampling everything in their lane, chanting silly battle-cries and kicking up dust, then peters out.
// The critters can't be damaged, detonated, or targeted — a pure forward sweep. Damage is owner-only (routes to the
// host like any other hit); allies get a visual-only copy + the chant.
//
// (PERF REWORK) The old version spawned a fresh multi-mesh ent Node3D ~19×/sec, each living the whole ~12s window →
// 200+ full-bodied ents on screen at once, a per-critter SurfaceHeight every frame, and an O(enemies×critters) trample
// scan. That tanked the framerate. Now ALL critters are ONE GPU-instanced MultiMesh (a single draw call), capped at a
// fixed instance count and recycled; ground height is sampled on a cheap per-critter stagger; and the trample is an
// analytic lane-band test (O(enemies), not O(enemies×critters)).
public partial class Stampede : Node3D
{
    private Player _caster;
    private Vector3 _origin, _fwd, _right;
    private float _width, _dmg, _dur, _travel, _speed = 16f;
    private bool _visualOnly;
    private float _elapsed = 0f, _spawnAcc = 0f, _chantAcc = 0.8f, _chantEvery = 2.5f, _dustT = 0f;

    private const int Cap = 80;                 // hard ceiling on live critters (one MultiMesh, one draw call)
    private MultiMeshInstance3D _mmi;
    private MultiMesh _mm;
    private static Mesh _critMesh;

    private struct Crit { public bool Active; public float Lane, Dist, Speed, Phase, GY, GYT, Scale; }
    private readonly Crit[] _crits = new Crit[Cap];
    private readonly Stack<int> _free = new();
    private int _liveCount = 0;
    private float _minDist = 1e9f, _maxDist = -1e9f;   // the occupied lane band (for the analytic trample)

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
        _travel = _dur * _speed + 6f;
        BuildMultiMesh();
        for (int i = Cap - 1; i >= 0; i--) { _free.Push(i); HideInstance(i); }
        Game.I.Sfx?.Rustle();
    }

    // one shared low-poly brown ent body baked to a single Mesh so every critter is one MultiMesh instance
    private static Mesh CritMesh()
    {
        if (_critMesh != null) return _critMesh;
        _critMesh = new CapsuleMesh { Radius = 0.42f, Height = 1.25f, RadialSegments = 6, Rings = 3 };
        return _critMesh;
    }

    private void BuildMultiMesh()
    {
        _mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = CritMesh(), InstanceCount = Cap };
        _mmi = new MultiMeshInstance3D { Multimesh = _mm, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        var mat = Game.ToonEmissive(new Color(0.42f, 0.30f, 0.16f), 0.35f, 0.02f);   // brown-bark, faintly lit
        _mmi.MaterialOverride = mat;
        AddChild(_mmi);
    }

    private void HideInstance(int i) => _mm.SetInstanceTransform(i, new Transform3D(Basis.Identity.Scaled(Vector3.Zero), new Vector3(0, -9999f, 0)));

    private void SpawnCritter()
    {
        if (_free.Count == 0) return;
        int i = _free.Pop();
        float back = (float)GD.RandRange(0.0, 3.0);
        _crits[i] = new Crit {
            Active = true,
            Lane = (float)GD.RandRange(-_width * 0.5, _width * 0.5),
            Dist = -back,
            Speed = _speed * (float)GD.RandRange(0.92, 1.12),
            Phase = (float)GD.RandRange(0.0, 6.28),
            GY = 0f, GYT = 0f, Scale = (float)GD.RandRange(0.6, 0.95),
        };
        _liveCount++;
    }

    public override void _Process(double delta)
    {
        if (Game.I == null || !Game.I.SimActive) return;   // freeze while paused
        float dt = (float)delta;
        _elapsed += dt;

        // continuous spawning at the back while the window is open (capped by the free pool → density without a node blowup)
        if (_elapsed < _dur)
        {
            _spawnAcc += dt;
            const float every = 0.08f;
            while (_spawnAcc >= every) { _spawnAcc -= every; SpawnCritter(); if (GD.Randf() < 0.5f) SpawnCritter(); }
        }

        // advance + write instance transforms in one pass; also track the occupied lane band for the trample
        _minDist = 1e9f; _maxDist = -1e9f;
        float yaw = Mathf.Atan2(_fwd.X, _fwd.Z);
        for (int i = 0; i < Cap; i++)
        {
            if (!_crits[i].Active) continue;
            ref var c = ref _crits[i];
            c.Dist += c.Speed * dt;
            c.Phase += dt * 16f;
            if (c.Dist > _travel) { c.Active = false; _liveCount--; HideInstance(i); _free.Push(i); continue; }

            // ground: sampled on a cheap per-critter stagger (~5×/s), not every frame for every critter
            c.GYT -= dt;
            var bp = _origin + _fwd * c.Dist + _right * c.Lane;
            if (c.GYT <= 0f) { c.GY = Game.I.SurfaceHeight(new Vector3(bp.X, 0f, bp.Z), 1e9f); c.GYT = 0.18f + GD.Randf() * 0.1f; }
            float hop = Mathf.Abs(Mathf.Sin(c.Phase)) * 0.3f;
            float lean = Mathf.Sin(c.Phase) * 0.18f;
            var basis = (new Basis(Vector3.Up, yaw) * new Basis(Vector3.Right, lean)).Scaled(Vector3.One * c.Scale);
            _mm.SetInstanceTransform(i, new Transform3D(basis, new Vector3(bp.X, c.GY + hop + 0.62f * c.Scale, bp.Z)));

            if (c.Dist < _minDist) _minDist = c.Dist;
            if (c.Dist > _maxDist) _maxDist = c.Dist;
        }

        // dust puffs across the front (throttled)
        _dustT -= dt;
        if (_dustT <= 0f && _liveCount > 0)
        {
            _dustT = 0.12f;
            float d = Mathf.Lerp(_minDist, _maxDist, 0.85f + GD.Randf() * 0.15f);
            float lane = (float)GD.RandRange(-_width * 0.5, _width * 0.5);
            var dp = _origin + _fwd * d + _right * lane;
            SpawnDust(new Vector3(dp.X, 0.1f, dp.Z));
        }

        // continuous chant
        _chantAcc -= dt;
        if (_chantAcc <= 0f && !_visualOnly && _liveCount > 0)
        {
            _chantAcc = _chantEvery = (float)GD.RandRange(1.8, 3.5);   // (TUNE) much sparser chirps — the longer ult made the old 0.35–0.7s spam grating
            float d = Mathf.Lerp(_minDist, _maxDist, GD.Randf());
            var cp = _origin + _fwd * d + _right * (float)GD.RandRange(-_width * 0.4, _width * 0.4);
            cp = new Vector3(cp.X, Game.I.SurfaceHeight(new Vector3(cp.X, 0f, cp.Z), 1e9f) + 1.2f, cp.Z);
            string line = Lines[(int)(GD.Randi() % (uint)Lines.Length)];
            var col = new Color(0.55f, 1f, 0.5f);
            Thornling.SpeakAt(cp, line, 3, col);
            Game.I.NetMgr?.BroadcastMinionSay(cp, line, 3, col);
        }

        // (PERF) trample = an analytic lane-band test: project each enemy onto the stampede axis and hit those inside the
        // occupied band + lane width. O(enemies), not O(enemies × critters). Per-enemy cooldown as before.
        if (!_visualOnly && _liveCount > 0)
        {
            float now = (float)Time.GetTicksMsec() / 1000f;
            float halfW = _width * 0.5f + 1.5f, lo = _minDist - 1.5f, hi = _maxDist + 1.5f;
            foreach (var e in Game.I.Enemies.ToArray())
            {
                if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
                if (_hitCd.TryGetValue(e.GetInstanceId(), out float t) && now < t) continue;
                Vector3 rel = e.GlobalPosition - _origin; rel.Y = 0f;
                float along = rel.Dot(_fwd);
                if (along < lo || along > hi) continue;
                if (Mathf.Abs(rel.Dot(_right)) > halfW + e.Radius) continue;
                _hitCd[e.GetInstanceId()] = now + 0.45f;
                e.Hurt(_dmg, DamageType.Nature, true, false);
                e.Knockback(_origin, 1.1f);
            }
        }

        if (_elapsed >= _dur && _liveCount == 0) QueueFree();
    }

    private void SpawnDust(Vector3 pos)
    {
        var dust = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 0.4f, Height = 0.5f, RadialSegments = 6, Rings = 4 },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
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
