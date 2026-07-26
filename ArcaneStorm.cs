using Godot;
using System.Collections.Generic;

// Arcane ult (REWORK of Overcharge) — ARCANE STORM: a large field that hangs over the battlefield and rains arcane
// bolts onto any foe caught inside it. Bolts hit HARDER against higher-max-HP foes (capped for bosses so it can't
// delete a phase), can crit with the witch's own crit passive, and each foe can be struck repeatedly — but only once
// per second. Lingers 13s base (+tier). Host simulates the strikes; every machine renders the raining field. Legendary
// mod (Singularity): a bigger storm that also drags foes toward its heart and strikes each of them twice as often.
public partial class ArcaneStorm : Node3D
{
    private Player _caster;
    private Vector3 _pos;
    private float _radius, _dur, _baseDmg, _hpScale, _bossCapMul, _critChance, _critMul;
    private bool _remote, _mod;
    private int _tier;
    private float _age = 0f, _tickT = 0f, _boltVisT = 0f, _pullT = 0f;
    private readonly Dictionary<Enemy, float> _nextHit = new();   // per-enemy 1s strike cooldown (keyed to _age)
    private static readonly Color ArcCol = new(0.72f, 0.45f, 1f);

    public void Init(Player caster, Vector3 pos, float radius, float dur, bool remote, bool mod, int tier,
                     float baseDmg, float hpScale, float bossCapMul, float critChance, float critMul)
    {
        _caster = caster; _pos = pos; _radius = radius; _dur = dur; _remote = remote; _mod = mod; _tier = tier;
        _baseDmg = baseDmg; _hpScale = hpScale; _bossCapMul = bossCapMul; _critChance = critChance; _critMul = critMul;
        GlobalPosition = pos;

        // storm decal + a roiling arcane ceiling of cloud overhead
        var disc = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = radius, BottomRadius = radius, Height = 0.12f, RadialSegments = 48 } };
        var m = Game.ToonEmissive(ArcCol, 1.1f, 0f); m.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; m.AlbedoColor = new Color(ArcCol.R, ArcCol.G, ArcCol.B, 0.24f);
        disc.MaterialOverride = m; disc.Position = new Vector3(0, 0.08f, 0); AddChild(disc);
        // (PHASE 3) a roiling arcane storm-cloud CANOPY (squashed dome + cloud shader) instead of a flat translucent cylinder
        var ceil = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = radius * 1.15f, Height = radius * 1.15f, RadialSegments = 32, Rings = 14 },
            Scale = new Vector3(1f, 0.34f, 1f),   // flatten into a low canopy dome
            MaterialOverride = ArcaneCloudMat(),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };
        ceil.Position = new Vector3(0, 13f, 0); AddChild(ceil);
        AddChild(new OmniLight3D { OmniRange = radius * 1.3f, LightColor = ArcCol, LightEnergy = 1.8f, ShadowEnabled = false, Position = new Vector3(0, 4f, 0) });
        Game.I.SpawnGroundSigil(pos, radius, ArcCol, net: false);
        Game.I.Sfx?.ArcaneBlast(pos);
    }

    // shared roiling-cloud material for the storm canopy (one instance across all storms)
    private static ShaderMaterial _cloudMat;
    private static ShaderMaterial ArcaneCloudMat()
    {
        if (_cloudMat != null) return _cloudMat;
        var m = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/arcane_cloud.gdshader") };
        m.SetShaderParameter("cloud_dark", new Color(0.16f, 0.05f, 0.32f));
        m.SetShaderParameter("cloud_bright", new Color(0.78f, 0.52f, 1f));
        _cloudMat = m; return m;
    }

    private Vector3 RandomPoint()
    {
        float a = GD.Randf() * Mathf.Tau, r = _radius * Mathf.Sqrt(GD.Randf());
        return new Vector3(_pos.X + Mathf.Cos(a) * r, 0f, _pos.Z + Mathf.Sin(a) * r);
    }

    public override void _Process(double delta)
    {
        var g = Game.I; if (g == null || !g.SimActive) return;
        float dt = (float)delta; _age += dt;

        // ambient rain of bolts — visual on every machine (not synced to real hits; keeps it cheap)
        _boltVisT -= dt;
        if (_boltVisT <= 0f)
        {
            _boltVisT = 0.16f;
            var bp = RandomPoint(); bp.Y = g.SurfaceHeight(bp, 0f);
            BoltVfx(bp, 0.32f, false);   // ambient flavor bolt — no ground rupture (cheap)
        }

        if (!_remote)
        {
            _tickT -= dt;
            if (_tickT <= 0f)
            {
                _tickT = 0.25f;
                foreach (var e in g.Enemies.ToArray())
                {
                    if (e == null || e.Dead || !GodotObject.IsInstanceValid(e) || e.Remote) continue;
                    var d = e.GlobalPosition - _pos; d.Y = 0f;
                    if (d.Length() > _radius + e.Radius) continue;
                    if (_nextHit.TryGetValue(e, out float nh) && _age < nh) continue;   // still on its 1s per-enemy cooldown
                    _nextHit[e] = _age + (_mod ? 0.5f : 1.0f);   // Singularity: strikes each foe twice as often
                    Strike(e);
                }
            }
            if (_mod)   // Singularity: drag the crowd toward the heart of the storm so they keep eating bolts
            {
                _pullT -= dt;
                if (_pullT <= 0f) { _pullT = 0.2f; g.NetMgr?.StormForce(_pos, _radius, 0, 3.5f); }
            }
        }

        if (_age >= _dur)
        {
            var tw = CreateTween(); tw.TweenProperty(this, "scale", new Vector3(1f, 0.01f, 1f), 0.5f);
            tw.TweenCallback(Callable.From(QueueFree));
            SetProcess(false);
        }
    }

    // a single arcane bolt onto `e` — more damage the tougher the foe, capped for bosses; crits with her passive
    private void Strike(Enemy e)
    {
        float hpBonus = _hpScale * e.MaxHp;
        if (e.IsBoss) hpBonus = Mathf.Min(hpBonus, _baseDmg * _bossCapMul);   // capped so it can't delete a boss phase
        float dmg = _baseDmg + hpBonus;
        bool crit = GD.Randf() < _critChance;
        if (crit) dmg *= _critMul;
        e.Hurt(dmg, DamageType.Arcane, true, crit);
        var at = new Vector3(e.GlobalPosition.X, Game.I.SurfaceHeight(e.GlobalPosition, e.GlobalPosition.Y), e.GlobalPosition.Z);
        BoltVfx(at, crit ? 0.65f : 0.45f, true);
    }

    // a bright bolt lancing down out of the storm ceiling to `at` (rupture flash only on real strikes)
    private void BoltVfx(Vector3 at, float scale, bool rupture)
    {
        float h = 15f;
        var bolt = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.06f + scale * 0.12f, BottomRadius = 0.02f, Height = h, RadialSegments = 6 }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, MaterialOverride = Game.Emissive(ArcCol.Lerp(Colors.White, 0.3f), 3.2f) };
        Game.I.AddChild(bolt); bolt.GlobalPosition = at + Vector3.Up * (h * 0.5f);
        var bt = bolt.CreateTween();
        bt.TweenProperty(bolt, "scale", new Vector3(1.4f, 1f, 1.4f), 0.06f).SetEase(Tween.EaseType.Out);
        bt.TweenProperty(bolt, "transparency", 1f, 0.22f);
        bt.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(bolt)) bolt.QueueFree(); }));
        if (rupture) Game.I.SpawnArcaneRupture(at + Vector3.Up * 0.4f, 1.4f + scale * 1.6f);
    }
}
