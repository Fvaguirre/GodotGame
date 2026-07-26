using Godot;
using System.Collections.Generic;

// Frost witch ult (REWORK) — GLACIAL SUNDER. The witch thrusts both palms to the sky and huge jagged icicles
// erupt from the ground beneath the foes in the target area. Each spear hits hard on emergence (can CRIT) and
// FLINGS foes upward, then stays rooted as a solid obstacle (foes collide with it, the party walks through) while
// it radiates cold — stacking frost + slight damage in an AoE that widens with tiers. Tiers raise the spear count,
// the cadence of fresh spears, the damage, the cold AoE and the freeze-stack rate. Legendary mod (Absolute Zero):
// the emergence hit INSTANTLY freezes, and the radiating cold SHATTERS frozen foes for extra tier-scaling damage.
// Host simulates all damage/freeze/fling/obstacles; allies get a visual-only ghost that erupts matching spears.
public partial class DeepFreeze : Node3D
{
    private Player _caster;
    private Vector3 _pos;
    private float _area, _dur, _thrustDmg, _coldDmg, _coldR, _life = 0f, _waveT = 0f, _waveCd;
    private bool _remote, _mod;
    private int _tier, _burstLeft;
    private static readonly Color IceCol = new(0.62f, 0.86f, 1f);

    public void Init(Player caster, Vector3 pos, float area, float dur, bool remote, bool mod = false,
                     int tier = 0, float thrustDmg = 0f, float coldDmg = 0f)
    {
        _caster = caster; _pos = pos; _area = area; _dur = dur; _remote = remote; _mod = mod;
        _tier = tier; _thrustDmg = thrustDmg; _coldDmg = coldDmg;
        _coldR = 3.2f + tier * 0.75f;                       // (REWORK) the cold aura each spear radiates — widens with tiers
        _waveCd = Mathf.Max(0.55f, 1.25f - tier * 0.14f);    // (REWORK) fresh spears erupt faster at higher tiers
        GlobalPosition = pos;

        // a rime-frost ground ring marks the sundered zone
        var disc = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = area, BottomRadius = area, Height = 0.12f, RadialSegments = 46 } };
        var m = Game.ToonEmissive(IceCol, 0.9f, 0f); m.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; m.AlbedoColor = new Color(IceCol.R, IceCol.G, IceCol.B, 0.28f);
        disc.MaterialOverride = m; disc.Position = new Vector3(0, 0.08f, 0); AddChild(disc);
        AddChild(new OmniLight3D { OmniRange = area * 1.2f, LightColor = IceCol, LightEnergy = 1.5f, ShadowEnabled = false, Position = new Vector3(0, 2.5f, 0) });
        Game.I.SpawnPollen(pos + Vector3.Up, area, IceCol, 26, 1f, net: false);
        Game.I.Sfx?.Freeze(pos, false);

        // opening burst: ~5 spears at base, more per tier — erupt on the foes standing in the zone
        int burst = 5 + tier * 2;
        var targets = PickTargets(burst);
        foreach (var p in targets) Erupt(p);
        // any remaining slots (thin crowd) still throw spears at random points so the salvo always reads
        for (int i = targets.Count; i < burst; i++) Erupt(RandomPoint());
        // then keep sundering fresh spears over the duration
        _burstLeft = 0;
    }

    // choose up to `n` distinct foe footpoints inside the zone (host only knows the real crowd; the ghost falls back to random)
    private List<Vector3> PickTargets(int n)
    {
        var outp = new List<Vector3>();
        if (_remote || Game.I == null) return outp;
        var pool = new List<Enemy>();
        foreach (var e in Game.I.Enemies.ToArray())
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e) || e.Remote) continue;
            var d = e.GlobalPosition - _pos; d.Y = 0f;
            if (d.Length() <= _area) pool.Add(e);
        }
        for (int i = 0; i < n && pool.Count > 0; i++)
        {
            int idx = (int)(GD.Randi() % (uint)pool.Count);
            outp.Add(new Vector3(pool[idx].GlobalPosition.X, 0f, pool[idx].GlobalPosition.Z));
            pool.RemoveAt(idx);
        }
        return outp;
    }

    private Vector3 RandomPoint()
    {
        float a = GD.Randf() * Mathf.Tau, r = _area * Mathf.Sqrt(GD.Randf());
        return new Vector3(_pos.X + Mathf.Cos(a) * r, 0f, _pos.Z + Mathf.Sin(a) * r);
    }

    private void Erupt(Vector3 p)
    {
        float gy = Game.I.SurfaceHeight(p, 0f);
        var spire = new IceSpire();
        Game.I.AddChild(spire);
        spire.Init(_caster, new Vector3(p.X, gy, p.Z), _remote, _mod, _tier, _thrustDmg, _coldDmg, _coldR);
    }

    public override void _Process(double delta)
    {
        var g = Game.I; if (g == null || !g.SimActive) return;
        float dt = (float)delta; _life += dt;
        // keep thrusting fresh spears through the active window
        if (_life < _dur)
        {
            _waveT -= dt;
            if (_waveT <= 0f)
            {
                _waveT = _waveCd;
                int per = 2 + _tier;                       // (REWORK) more spears per wave at higher tiers
                if (_remote) { for (int i = 0; i < per; i++) Erupt(RandomPoint()); }
                else
                {
                    var tg = PickTargets(per);
                    foreach (var p in tg) Erupt(p);
                }
            }
        }
        else if (_life >= _dur + 0.2f)
        {
            QueueFree();   // spears manage their own lifetime; the field controller can retire
        }
    }
}

// A single erupting icicle spear: a big jagged natural-ice spike that heaves up out of the ground, hits + flings on
// emergence, blocks foes (party walks through), then radiates cold for a few seconds before sinking away.
public partial class IceSpire : Node3D
{
    private Player _caster;
    private bool _remote, _mod, _erupted = false;
    private int _tier;
    private float _thrustDmg, _coldDmg, _coldR, _life = 0f, _maxLife, _tickT = 0.35f;
    private Blocker _blocker; private bool _blockerAdded = false;
    private static readonly Color IceCol = new(0.66f, 0.88f, 1f);

    public void Init(Player caster, Vector3 basePos, bool remote, bool mod, int tier, float thrustDmg, float coldDmg, float coldR)
    {
        _caster = caster; _remote = remote; _mod = mod; _tier = tier;
        _thrustDmg = thrustDmg; _coldDmg = coldDmg; _coldR = coldR;
        _maxLife = 5.5f + tier * 0.6f;                      // spears linger a little longer at higher tiers
        GlobalPosition = basePos;

        float h = 3.6f + tier * 0.5f + GD.Randf() * 1.4f;   // big, taller with tiers
        BuildSpear(h);

        // heave up out of the ground, aggressive overshoot
        var riser = GetChild<Node3D>(0);
        riser.Position = new Vector3(0, -h - 0.4f, 0);
        var tw = riser.CreateTween();
        tw.TweenProperty(riser, "position", Vector3.Zero, 0.16f).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
        Game.I.SpawnFrostShatter(basePos + Vector3.Up * 0.3f, _coldR * 0.5f);
        Game.I.SpawnPollen(basePos + Vector3.Up * 0.5f, _coldR, IceCol, 12, 0.9f, net: false);

        // solid obstacle: foes collide + steer around it; the party passes straight through (players don't test WallBlockers)
        if (!_remote)
        {
            _blocker = new Blocker { Pos = basePos, Radius = 1.15f };
            Game.I.WallBlockers.Add(_blocker); _blockerAdded = true;
            ThrustHit(basePos);
        }
    }

    private void BuildSpear(float h)
    {
        var riser = new Node3D(); AddChild(riser);
        var mat = Game.ToonEmissive(IceCol.Lerp(Colors.White, 0.25f), 1.3f, 0.05f);
        mat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; mat.AlbedoColor = new Color(IceCol.R, IceCol.G, IceCol.B, 0.9f);
        // a jagged natural icicle: a tall core cone plus a few offset shards clustered at the base
        var core = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.55f, Height = h, RadialSegments = 6 }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, MaterialOverride = mat };
        core.Position = new Vector3(0, h * 0.5f, 0);
        core.RotationDegrees = new Vector3((GD.Randf() - 0.5f) * 12f, GD.Randf() * 360f, (GD.Randf() - 0.5f) * 12f);
        riser.AddChild(core);
        int shards = 3 + _tier / 2;
        for (int i = 0; i < shards; i++)
        {
            float a = GD.Randf() * Mathf.Tau, rr = 0.35f + GD.Randf() * 0.5f;
            float sh = h * (0.45f + GD.Randf() * 0.4f);
            var shard = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.28f + GD.Randf() * 0.18f, Height = sh, RadialSegments = 5 }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, MaterialOverride = mat };
            shard.Position = new Vector3(Mathf.Cos(a) * rr, sh * 0.5f, Mathf.Sin(a) * rr);
            shard.RotationDegrees = new Vector3((GD.Randf() - 0.5f) * 30f, GD.Randf() * 360f, (GD.Randf() - 0.5f) * 30f);
            riser.AddChild(shard);
        }
        riser.AddChild(new OmniLight3D { OmniRange = _coldR * 1.4f, LightColor = IceCol, LightEnergy = 1.4f, ShadowEnabled = false, Position = new Vector3(0, h * 0.5f, 0) });
    }

    // emergence: hard hit + fling UP everything close to the spear (host only)
    private void ThrustHit(Vector3 at)
    {
        if (Game.I == null) return;
        float hitR = Mathf.Max(2.4f, _coldR * 0.85f);
        foreach (var e in Game.I.Enemies.ToArray())
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e) || e.Remote) continue;
            var d = e.GlobalPosition - at; d.Y = 0f;
            if (d.Length() > hitR + e.Radius) continue;
            bool crit = _caster != null && _caster.RollCritPublic();
            float dmg = _thrustDmg * (crit && _caster != null ? _caster.CritMultPublic() : 1f);
            e.Hurt(dmg, DamageType.Frost, true, crit);
            if (!e.Dead) e.Fling(Vector3.Up * (11f + _tier * 1.2f) + d.Normalized() * 2.5f);   // punt them skyward
            if (_mod && !e.Dead)                              // Absolute Zero: the thrust flash-freezes
                e.AddFreeze(e.FreezeThreshold * 2f, _caster != null ? _caster.FreezeThreshMul : 1f, _caster != null ? _caster.FrostDurBonus : 0f);
        }
    }

    public override void _Process(double delta)
    {
        var g = Game.I; if (g == null || !g.SimActive) return;
        float dt = (float)delta; _life += dt;
        // radiate cold: stack frost + slight damage on foes in the aura (host only)
        if (!_remote)
        {
            _tickT -= dt;
            if (_tickT <= 0f)
            {
                _tickT = Mathf.Max(0.22f, 0.4f - _tier * 0.03f);   // (REWORK) faster freeze-stacking at higher tiers
                foreach (var e in g.Enemies.ToArray())
                {
                    if (e == null || e.Dead || !GodotObject.IsInstanceValid(e) || e.Remote) continue;
                    var d = e.GlobalPosition - GlobalPosition; d.Y = 0f;
                    if (d.Length() > _coldR + e.Radius) continue;
                    e.Hurt(_coldDmg, DamageType.Frost, true);
                    e.AddFreeze((0.28f + _tier * 0.06f) * (_caster != null ? _caster.FrostDurBonus + 1f : 1f), _caster != null ? _caster.FreezeThreshMul : 1f, _caster != null ? _caster.FrostDurBonus : 0f);
                    if (_mod && e.Frozen)                        // Absolute Zero: the aura shatters frozen foes for extra tier-scaling damage
                    {
                        e.Hurt(_coldDmg * (1.5f + _tier * 0.5f), DamageType.Frost, true);
                        e.ShatterInstant();
                    }
                }
            }
        }
        if (_life >= _maxLife)
        {
            if (_blockerAdded) { Game.I.WallBlockers.Remove(_blocker); _blockerAdded = false; }
            var riser = GetChildCount() > 0 ? GetChild<Node3D>(0) : null;
            if (riser != null)
            {
                Game.I.SpawnFrostShatter(GlobalPosition + Vector3.Up * 1.2f, _coldR * 0.4f);
                var tw = riser.CreateTween();
                tw.TweenProperty(riser, "position", new Vector3(0, -riser.Position.Y - 4f, 0), 0.4f).SetEase(Tween.EaseType.In);
                tw.TweenCallback(Callable.From(QueueFree));
            }
            else QueueFree();
            SetProcess(false);
        }
    }

    public override void _ExitTree()
    {
        if (_blockerAdded && Game.I != null) { Game.I.WallBlockers.Remove(_blocker); _blockerAdded = false; }
    }
}
