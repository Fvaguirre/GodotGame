using Godot;
using System.Collections.Generic;

// THE HOLLOW MOON, phase 2: he plants himself and spins up into a huge arcane vortex that DRAGS the coven in,
// grinds them for the duration, then finishes with a colossal stomp on whoever it reeled all the way to him.
// He is invulnerable for the whole spin (Enemy owns that flag).
//
// Networking mirrors the Cyclone pattern, inverted for players:
//   - Every machine spawns its own copy and PULLS ITS OWN LOCAL PLAYER each frame. Player position is
//     client-authoritative, so a per-frame pull RPC would both fight the owner and flood the wire.
//   - Only the HOST ticks damage and fires the finishing stomp (routed through Net, which reaches every warden).
// `hostSim == false` = the visual/pull-only copy an ally spawns.
public partial class BossVortex : Node3D
{
    public const float PullRange = 50f;     // outer edge of the drag
    public const float StompRange = 12f;    // "pulled all the way in" — who eats the finisher
    public const float StompMaxHpFrac = 0.45f;

    private float _dur, _life = 0f, _dps;
    private bool _hostSim, _done = false;
    private float _dmgT = 0f;
    private Node3D _spin;
    private float _topR, _baseR, _colH;
    private readonly List<MeshInstance3D> _debris = new();
    private readonly List<float> _dAng = new(), _dH = new(), _dRise = new(), _dSpin = new(), _dRadJit = new();

    private static readonly Color Arc = new Color(0.62f, 0.36f, 1f);

    public void Init(Vector3 pos, float dur, float dps, bool hostSim)
    {
        _dur = dur; _dps = dps; _hostSim = hostSim;
        GlobalPosition = new Vector3(pos.X, 0f, pos.Z);

        // Reuse the proven Cyclone funnel shader (fbm scrolled up + swirled around, fresnel rim) so this reads as the
        // same family of effect, just arcane instead of wind and wide enough to swallow the arena.
        _topR = PullRange * 0.42f; _baseR = 2.2f; _colH = 30f;
        _spin = new Node3D();
        AddChild(_spin);

        var cone = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = _topR * 1.05f, BottomRadius = _baseR + 0.6f, Height = _colH, RadialSegments = 32, Rings = 14, CapTop = false, CapBottom = false },
            MaterialOverride = Cyclone.TornadoMat(Arc, 1.9f),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        cone.Position = new Vector3(0, 0.3f + _colH * 0.5f, 0);
        AddChild(cone);
        var core = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = _topR * 0.45f, BottomRadius = _baseR * 0.5f, Height = _colH * 0.9f, RadialSegments = 20, Rings = 10, CapTop = false, CapBottom = false },
            MaterialOverride = Cyclone.TornadoMat(Arc.Lerp(new Color(0.14f, 0.06f, 0.22f), 0.55f), 3.1f),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        core.Position = new Vector3(0, 0.3f + _colH * 0.45f, 0);
        AddChild(core);

        // A DENSE shroud right where his body is. He has no authored spin clip — he's whipped around fast instead — and
        // this makes the silhouette genuinely hard to read rather than relying on speed alone.
        var shroudSh = ResourceLoader.Load<Shader>("res://shaders/arcane_aura.gdshader");
        var shroud = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 5.5f, BottomRadius = 8.5f, Height = 17f, RadialSegments = 24, Rings = 12, CapTop = false, CapBottom = false },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        if (shroudSh != null)
        {
            var sm = new ShaderMaterial { Shader = shroudSh };
            sm.SetShaderParameter("tint", new Vector3(Arc.R, Arc.G, Arc.B));
            sm.SetShaderParameter("hot", new Vector3(0.9f, 0.84f, 1f));
            sm.SetShaderParameter("amount", 1f);
            sm.SetShaderParameter("speed", 4.2f);
            sm.SetShaderParameter("density", 0.55f);   // broad tongues → a solid churning wall, not lace
            sm.SetShaderParameter("wisp", 0.12f);
            shroud.MaterialOverride = sm;
        }
        else shroud.MaterialOverride = Cyclone.TornadoMat(Arc, 3.4f);
        shroud.Position = new Vector3(0, 8f, 0);
        _spin.AddChild(shroud);

        // the PULL BOUNDARY, drawn on the ground: you need to be able to see where the drag starts, or escaping is guesswork
        var edge = new MeshInstance3D
        {
            Mesh = new TorusMesh { InnerRadius = PullRange * 0.965f, OuterRadius = PullRange, Rings = 48, RingSegments = 8 },
            MaterialOverride = Ghost(Arc, 0.30f),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        edge.Position = new Vector3(0, 0.18f, 0);
        AddChild(edge);
        // …and the inner ring: cross this and the finishing stomp reaches you
        var inner = new MeshInstance3D
        {
            Mesh = new TorusMesh { InnerRadius = StompRange * 0.92f, OuterRadius = StompRange, Rings = 36, RingSegments = 8 },
            MaterialOverride = Ghost(new Color(1f, 0.35f, 0.30f), 0.42f),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        inner.Position = new Vector3(0, 0.2f, 0);
        _spin.AddChild(inner);

        var debrisMat = Game.ToonEmissive(Arc, 2.4f, 0f);
        debrisMat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        debrisMat.AlbedoColor = new Color(Arc.R, Arc.G, Arc.B, 0.8f);
        debrisMat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        for (int i = 0; i < 40; i++)
        {
            float sz = 0.2f + GD.Randf() * 0.35f;
            var d = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(sz, sz, sz) }, MaterialOverride = debrisMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            AddChild(d);
            _debris.Add(d);
            _dAng.Add(GD.Randf() * Mathf.Tau);
            _dH.Add(GD.Randf() * _colH);
            _dRise.Add(4f + GD.Randf() * 5f);
            _dSpin.Add(6f + GD.Randf() * 5f);
            _dRadJit.Add(0.8f + GD.Randf() * 0.35f);
        }

        AddChild(new OmniLight3D { Position = new Vector3(0, 3f, 0), OmniRange = 30f, LightColor = Arc, LightEnergy = 3.2f });
        AddChild(new OmniLight3D { Position = new Vector3(0, _colH * 0.75f, 0), OmniRange = 24f, LightColor = Arc, LightEnergy = 2.2f });

        Scale = new Vector3(0.15f, 0.15f, 0.15f);
        CreateTween().TweenProperty(this, "scale", Vector3.One, 0.5f).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
        Game.I?.Sfx?.StormThunder(GlobalPosition, 0.7f);
    }

    // The finishing blast: a wide column of arcane energy erupting out of the ground, on the same shader as his phase-2
    // corona. Deliberately NOT VfxLance — that's the Divine witch's holy lance and would read as the wrong element.
    private void Eruption(Vector3 at)
    {
        var sh = ResourceLoader.Load<Shader>("res://shaders/arcane_aura.gdshader");
        var rig = new Node3D();
        Game.I.AddChild(rig);
        rig.GlobalPosition = new Vector3(at.X, Game.I.SurfaceHeight(at, 1e9f), at.Z);
        for (int i = 0; i < 3; i++)
        {
            float rb = StompRange * (0.85f - i * 0.22f), h = 26f - i * 5f;
            var mi = new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = rb * 0.12f, BottomRadius = rb, Height = h, RadialSegments = 26, Rings = 12, CapTop = false, CapBottom = false },
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            if (sh != null)
            {
                var m = new ShaderMaterial { Shader = sh };
                m.SetShaderParameter("tint", new Vector3(Arc.R, Arc.G, Arc.B));
                m.SetShaderParameter("hot", new Vector3(0.92f, 0.86f, 1f));
                m.SetShaderParameter("amount", 1f);
                m.SetShaderParameter("speed", 3.4f + i * 1.1f);
                m.SetShaderParameter("density", 0.7f + i * 0.5f);
                m.SetShaderParameter("wisp", 0.6f);
                mi.MaterialOverride = m;
                var ft = rig.CreateTween();
                ft.TweenInterval(0.28f);
                ft.TweenMethod(Callable.From((float v) => { if (GodotObject.IsInstanceValid(mi)) m.SetShaderParameter("amount", v); }), 1f, 0f, 0.55f);
            }
            else mi.MaterialOverride = Game.Emissive(Arc, 3f);
            mi.Position = new Vector3(0, h * 0.5f, 0);
            mi.Scale = new Vector3(0.35f, 0.2f, 0.35f);
            rig.AddChild(mi);
            var tw = mi.CreateTween();
            tw.TweenProperty(mi, "scale", Vector3.One, 0.3f + i * 0.06f).SetTrans(Tween.TransitionType.Expo).SetEase(Tween.EaseType.Out);
        }
        rig.AddChild(new OmniLight3D { Position = new Vector3(0, 6f, 0), OmniRange = 34f, LightColor = Arc, LightEnergy = 5f });
        var fin = rig.CreateTween();
        fin.TweenInterval(0.95f);
        fin.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(rig)) rig.QueueFree(); }));
    }

    private static StandardMaterial3D Ghost(Color c, float a)
    {
        var m = Game.ToonEmissive(c, 2.2f, 0f);
        m.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        m.AlbedoColor = new Color(c.R, c.G, c.B, a);
        m.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        m.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        return m;
    }

    // Keep the funnel riding the boss (he's planted, but terrain/nudges can shift him a little).
    public void Follow(Vector3 pos) => GlobalPosition = new Vector3(pos.X, 0f, pos.Z);

    public override void _Process(double delta)
    {
        if (Game.I == null || !Game.I.SimActive || _done) return;
        float dt = (float)delta;
        _life += dt;
        _spin?.RotateY(dt * 9f);

        for (int i = 0; i < _debris.Count; i++)
        {
            var d = _debris[i];
            if (d == null || !GodotObject.IsInstanceValid(d)) continue;
            float h = _dH[i] + _dRise[i] * dt; if (h > _colH) h -= _colH; _dH[i] = h;
            float a = _dAng[i] + _dSpin[i] * dt; _dAng[i] = a;
            float r = Mathf.Lerp(_baseR, _topR, h / _colH) * _dRadJit[i];
            d.Position = new Vector3(Mathf.Cos(a) * r, 0.3f + h, Mathf.Sin(a) * r);
        }

        // ---- the drag: applied LOCALLY to this machine's own witch, every frame, so her own movement competes with
        //      it directly. Strength ramps hard as she nears the eye, so walking loses ground but a dash breaks out.
        Game.I.Player?.VortexPull(GlobalPosition, PullRange, dt);

        // ---- grind damage (host only; Net reaches every warden) ----
        if (_hostSim)
        {
            // Tick at 0.8s, NOT a fine 0.25s: Player.Hurt stamps a 0.7s i-frame on every hit that lands, so anything
            // faster than that is silently swallowed and the grind does literally nothing. One meaty tick per 0.8s
            // delivers the intended per-second rate and actually chews through her shield.
            _dmgT -= dt;
            if (_dmgT <= 0f) { _dmgT = 0.8f; Game.I.NetMgr?.HurtPlayersIn(GlobalPosition, PullRange, _dps * 0.8f); }
        }

        if (_life >= _dur) Finish();
    }

    // The payoff: everything reeled inside StompRange eats a flat % of its MAX health. Deliberately huge and
    // deliberately telegraphed by the inner ring — being dragged to the eye is supposed to be the failure state.
    private void Finish()
    {
        if (_done) return;
        _done = true;
        var at = GlobalPosition;

        if (_hostSim)
        {
            var pl = Game.I.Player;
            if (pl != null && !pl.Downed)
            {
                var d = pl.GlobalPosition - at; d.Y = 0f;
                // ignoreIFrame: the grind tick that fires moments earlier would otherwise eat the finisher outright
                if (d.Length() < StompRange) { pl.Hurt(pl.S.MaxHp * StompMaxHpFrac, at, ignoreIFrame: true); pl.Knockback(at, 26f); }
            }
            Game.I.NetMgr?.VortexStomp(at, StompRange, StompMaxHpFrac);
        }

        // ---- the stomp itself: nested arcane shocks + a rising column + shards, far heavier than his normal stomp ----
        var hot = new Color(0.92f, 0.84f, 1f);
        Game.I.VfxRing(at, hot, StompRange * 0.5f, 0.22f);
        Game.I.VfxRing(at, Arc, StompRange, 0.45f);
        Game.I.VfxRing(at, Arc.Lerp(hot, 0.5f), StompRange * 1.8f, 0.7f);
        Game.I.VfxRing(at, Arc, StompRange * 2.6f, 0.95f);
        Game.I.SpawnGroundSpikes(at, StompRange, 26, Arc, 0.6f);
        Game.I.SpawnGroundSpikes(at, StompRange * 1.7f, 18, hot, 0.5f);
        Eruption(at);                                          // an ARCANE column punched up out of the impact
        Game.I.Sfx?.Thunder();
        Game.I.Sfx?.StormThunder(at, 0.55f);
        Game.I.Player?.CamKickExternal(1.6f);

        var tw = CreateTween();
        tw.TweenProperty(this, "scale", new Vector3(1.7f, 0.08f, 1.7f), 0.35f);   // funnel slams flat and disperses
        tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(this)) QueueFree(); }));
    }
}
