using Godot;
using System.Collections.Generic;

// Ember ult (REWORK) — PHOENIX ASCENDANT: a giant flaming phoenix hurled at the cursor. It pierces every foe in a
// line (damage + burn stacks), GRABBING every non-boss it touches. After it has travelled its horizontal reach it
// banks skyward, carrying the grabbed foes up ~45u, then detonates in a phoenix-shaped blast: grabbed foes take heavy
// damage and are flung; bosses it merely grazed detonate in place for a (capped) percent of their max HP. Grabbed
// foes are held/stunned the whole flight and cannot be relocated near a player. Host simulates all enemy work; every
// machine flies the same deterministic visual bird (grabbed-foe positions come from the host snapshot).
public partial class PhoenixDive : Node3D
{
    private Player _caster;
    private Vector3 _dir, _pos;
    private int _tier, _grabCap;
    private bool _mod, _simulate;
    private float _touchDmg, _grabDmg, _bossFrac, _baseUnit;
    private float _horiz = 0f, _ascend = 0f, _life = 0f, _emberT = 0f;
    private int _phase = 0;   // 0 = flying out, 1 = rising, 2 = detonated
    private readonly List<Enemy> _grabbed = new();
    private readonly HashSet<Enemy> _touched = new();
    private readonly List<Enemy> _bossHit = new();
    private Node3D _bird;

    private const float HorizSpeed = 34f, MaxHoriz = 45f, MaxPierce = 75f, AscendSpeed = 42f, MaxAscend = 45f, HitR = 3.6f;
    private static readonly Color FireCol = new(1f, 0.5f, 0.15f);

    public void Init(Player caster, Vector3 origin, Vector3 dir, int tier, bool mod, bool simulate,
                     float touchDmg, float grabDmg, float bossFrac, float baseUnit)
    {
        _caster = caster; _tier = tier; _mod = mod; _simulate = simulate;
        _touchDmg = touchDmg; _grabDmg = grabDmg; _bossFrac = bossFrac; _baseUnit = baseUnit;
        _grabCap = 8 + tier * 2 + (mod ? 6 : 0);
        dir.Y = 0f; _dir = dir.LengthSquared() > 0.001f ? dir.Normalized() : Vector3.Forward;
        _pos = origin + Vector3.Up * 1.4f;
        BuildBird();
        GlobalPosition = _pos;
        Game.I.Sfx?.ModEmber(origin);
    }

    // shared painterly flame material for the phoenix body (hot head -Z leads its flight)
    private static ShaderMaterial _flameMat;
    private static ShaderMaterial FlameMat()
    {
        if (_flameMat != null) return _flameMat;
        var m = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/flame.gdshader") };
        m.SetShaderParameter("hot_color", new Color(1f, 0.92f, 0.55f));
        m.SetShaderParameter("mid_color", new Color(1f, 0.5f, 0.15f));
        m.SetShaderParameter("cool_color", new Color(0.7f, 0.14f, 0.03f));
        m.SetShaderParameter("half_len", 1.4f);
        _flameMat = m; return m;
    }

    private void BuildBird()
    {
        _bird = new Node3D(); AddChild(_bird);
        // (PHASE 3) flaming COMET body (painterly flame shader) instead of a glowing sphere — elongated along the flight axis
        var core = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 1.4f, Height = 2.8f, RadialSegments = 12, Rings = 9 },
            Scale = new Vector3(0.82f, 0.82f, 2.0f),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = FlameMat()
        };
        _bird.AddChild(core);
        _bird.AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.7f, Height = 1.4f }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, MaterialOverride = Game.Emissive(new Color(1f, 0.95f, 0.72f), 3.4f), Position = new Vector3(0, 0, -1.2f) });   // white-hot heart at the head
        _bird.AddChild(Game.MakeCometTrail(FireCol));   // fiery wake streaming behind
        // swept flaming wings + a long tail so it reads as a bird streaking through the air
        for (int s = -1; s <= 1; s += 2)
        {
            var wing = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(3.4f, 2.1f, 0.12f) }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            var wm = Game.Emissive(FireCol, 2.6f); wm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; wm.AlbedoColor = new Color(1f, 0.42f, 0.1f, 0.65f); wm.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
            wing.MaterialOverride = wm; wing.Position = new Vector3(s * 2.1f, 0.4f, -0.3f); wing.RotationDegrees = new Vector3(0, 0, s * 32f);
            _bird.AddChild(wing);
        }
        var tail = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.7f, Height = 3.4f, RadialSegments = 6 }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, MaterialOverride = Game.Emissive(FireCol.Lerp(Colors.Yellow, 0.3f), 2.8f) };
        tail.Position = new Vector3(0, 0.1f, 2f); tail.RotationDegrees = new Vector3(90, 0, 0);
        _bird.AddChild(tail);
        _bird.AddChild(new OmniLight3D { OmniRange = 10f, LightColor = FireCol, LightEnergy = 3f, ShadowEnabled = false });
        FacePhase();
    }

    private void FacePhase()
    {
        if (_bird == null) return;
        Vector3 look = _phase == 1 ? Vector3.Up : _dir;
        var tgt = _pos + look;
        _bird.LookAt(tgt, _phase == 1 ? _dir : Vector3.Up);
    }

    public override void _Process(double delta)
    {
        var g = Game.I; if (g == null || !g.SimActive) return;
        if (_phase == 2) return;   // detonated — the death tween finishes on its own
        float dt = (float)delta; _life += dt;
        if (_phase == 0)
        {
            float step = HorizSpeed * dt; _horiz += step;
            _pos += _dir * step;
            if (_simulate) SweepTouch();
            CarryGrabbed(false);
            bool outArena = _pos.Length() > 720f;   // ~overworld disc bound
            if (_horiz >= MaxHoriz || outArena || _horiz >= MaxPierce) { _phase = 1; FacePhase(); }
        }
        else if (_phase == 1)
        {
            float step = AscendSpeed * dt; _ascend += step;
            _pos += Vector3.Up * step;
            CarryGrabbed(true);
            if (_ascend >= MaxAscend) { Detonate(); return; }
        }
        GlobalPosition = _pos;
        if (_bird != null) _bird.RotateObjectLocal(Vector3.Forward, dt * 6f);
        _emberT -= dt;
        if (_emberT <= 0f) { _emberT = 0.06f; g.SpawnEmberBurst(_pos, 1.6f); }
    }

    // pierce: damage + burn everything the bird passes; grab non-bosses, mark bosses for the floor detonation
    private void SweepTouch()
    {
        foreach (var e in Game.I.Enemies.ToArray())
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e) || e.Remote) continue;
            if (_touched.Contains(e)) continue;
            var d = e.GlobalPosition - _pos; d.Y = 0f;
            if (d.Length() > HitR + e.Radius) continue;
            _touched.Add(e);
            bool crit = _caster != null && _caster.RollCritPublic();
            e.Hurt(_touchDmg * (crit && _caster != null ? _caster.CritMultPublic() : 1f), DamageType.Ember, true, crit);
            e.AddBurn(2f, _baseUnit * 0.1f, _baseUnit * 3.5f, 0f, Game.I.LocalPeer);
            if (e.Dead) continue;
            if (!e.IsBoss && _grabbed.Count < _grabCap) { e.PhoenixGrab(e.GlobalPosition); _grabbed.Add(e); }
            else if (e.IsBoss) _bossHit.Add(e);
        }
    }

    // keep the grabbed foes clustered on/under the bird so they visibly ride along and rise
    private void CarryGrabbed(bool rising)
    {
        if (!_simulate) return;
        for (int i = _grabbed.Count - 1; i >= 0; i--)
        {
            var e = _grabbed[i];
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) { _grabbed.RemoveAt(i); continue; }
            float a = (i / (float)Mathf.Max(1, _grabbed.Count)) * Mathf.Tau;
            float rr = 1.5f + (i % 3) * 0.9f;
            var off = new Vector3(Mathf.Cos(a) * rr, rising ? -1.2f - (i % 4) * 0.5f : 0f, Mathf.Sin(a) * rr);
            e.PhoenixHoldPos = _pos + off;
            e.PhoenixHeld = true;   // refresh in case something cleared it
        }
    }

    private void Detonate()
    {
        _phase = 2;
        var g = Game.I;
        float blastR = (10f + _tier * 1.5f) * (_mod ? 1.4f : 1f);
        // skyburst — grabbed foes take the big hit + get flung out of the sky
        if (_simulate)
        {
            foreach (var e in _grabbed.ToArray())
            {
                if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
                e.PhoenixRelease();
                bool crit = _caster != null && _caster.RollCritPublic();
                e.Hurt(_grabDmg * (crit && _caster != null ? _caster.CritMultPublic() : 1f), DamageType.Ember, true, crit);
                if (!e.Dead)
                {
                    var outw = e.GlobalPosition - _pos; outw.Y = 0f;
                    outw = outw.LengthSquared() > 0.01f ? outw.Normalized() : new Vector3(GD.Randf() - 0.5f, 0, GD.Randf() - 0.5f).Normalized();
                    e.Fling(outw * (14f + _tier * 2f) + Vector3.Down * 2f, 1.3f);
                }
            }
            // bosses the bird only grazed detonate where they stand for a capped % of their max HP
            foreach (var e in _bossHit)
            {
                if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
                e.Hurt(_bossFrac * e.MaxHp, DamageType.Ember, true);
                e.AddBurn(3f, _baseUnit * 0.12f, _baseUnit * 3.5f, 0f, Game.I.LocalPeer);
                PhoenixShapeBurst(new Vector3(e.GlobalPosition.X, Game.I.SurfaceHeight(e.GlobalPosition, e.GlobalPosition.Y) + 1f, e.GlobalPosition.Z), 6f);
                Game.I.NetMgr?.BroadcastVfx(70, e.GlobalPosition + Vector3.Up * 1f, Vector3.Up, 6f, 1f, FireCol);
            }
            Game.I.DamageWorld(_pos, blastR, _grabDmg * 0.5f);
            if (_mod)   // Eternal Flame: the skyburst rains a burning field onto its floor projection
            {
                var floor = new Vector3(_pos.X, Game.I.SurfaceHeight(_pos, 0f), _pos.Z);
                var field = new GroundField { Type = FieldType.Hex, DType = DamageType.Ember, Radius = blastR * 0.9f, Dur = 8f, Power = _baseUnit * 0.7f,
                    TintColor = FireCol, BurnAdd = 1f, BurnPer = _baseUnit * 0.1f, BurnBomb = _baseUnit * 3.5f, BurnOwner = Game.I.LocalPeer, Src = _caster };
                Game.I.AddChild(field); field.GlobalPosition = new Vector3(floor.X, 0.05f, floor.Z);
            }
        }
        // the phoenix-shaped skyburst (visual on every machine)
        PhoenixShapeBurst(_pos, blastR * 1.2f);
        g.SpawnEmberBurst(_pos, blastR * 1.4f);
        g.Sfx?.Thunder(); g.Sfx?.ModEmber(_pos);
        var tw = _bird.CreateTween();
        tw.TweenProperty(_bird, "scale", Vector3.One * 3.5f, 0.2f).SetEase(Tween.EaseType.Out);
        tw.TweenProperty(_bird, "scale", Vector3.One * 0.01f, 0.2f).SetEase(Tween.EaseType.In);
        tw.TweenCallback(Callable.From(QueueFree));
    }

    // a spread pair of flaming wings + core bloom at `at`
    private void PhoenixShapeBurst(Vector3 at, float scale)
    {
        var core = new MeshInstance3D { Mesh = new SphereMesh { Radius = scale * 0.4f, Height = scale * 0.8f }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, MaterialOverride = Game.Emissive(FireCol, 3.4f) };
        Game.I.AddChild(core); core.GlobalPosition = at;
        var ct = core.CreateTween(); ct.SetParallel(true);
        ct.TweenProperty(core, "scale", Vector3.One * 1.8f, 0.4f).SetEase(Tween.EaseType.Out);
        ct.TweenProperty(core, "transparency", 1f, 0.5f);
        ct.Chain().TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(core)) core.QueueFree(); }));
        for (int s = -1; s <= 1; s += 2)
        {
            var wing = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(scale * 1.6f, scale, 0.15f) }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            var wm = Game.Emissive(FireCol, 2.8f); wm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; wm.AlbedoColor = new Color(1f, 0.4f, 0.1f, 0.7f); wm.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
            wing.MaterialOverride = wm; Game.I.AddChild(wing);
            wing.GlobalPosition = at + new Vector3(s * scale * 0.6f, scale * 0.3f, 0);
            wing.RotationDegrees = new Vector3(0, 0, s * 40f);
            wing.Scale = new Vector3(0.2f, 0.2f, 1f);
            var wt = wing.CreateTween(); wt.SetParallel(true);
            wt.TweenProperty(wing, "scale", new Vector3(1.2f, 1.2f, 1f), 0.28f).SetEase(Tween.EaseType.Out);
            wt.TweenProperty(wing, "transparency", 1f, 0.55f);
            wt.Chain().TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(wing)) wing.QueueFree(); }));
        }
        Game.I.VfxRing(at, FireCol, scale * 1.3f, 0.6f);
    }

    public override void _ExitTree()
    {
        foreach (var e in _grabbed) if (e != null && GodotObject.IsInstanceValid(e)) e.PhoenixRelease();   // never leave a foe stuck held if we die early
    }
}
