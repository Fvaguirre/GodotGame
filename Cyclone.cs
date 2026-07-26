using Godot;
using System.Collections.Generic;

// Cyclone.cs — the Gale witch's "Cyclone" ultimate (and the lingering whirlwind from the "Tempest Heart"
// legendary). A persistent tornado parked in the world: it drags nearby enemies toward its eye and grinds
// them with Wind damage on a short per-enemy cooldown, then bursts outward when it expires.
//
// Networking: damage uses Enemy.Hurt, which already routes a client's hit to the host, so damage is correct
// for any caster. PULL (moving enemies) is position authority, so it's only applied to non-proxy enemies
// (e.Remote == false) — on the host for host-cast cyclones, or locally in solo. On a client the host owns
// enemy positions, so the client cyclone still damages but doesn't fight the host over where foes are.
// Allies spawn a visual-only copy (visualOnly == true, dps 0) via BroadcastVfx kind 11.
public partial class Cyclone : Node3D
{
    private Player _caster;
    private float _radius, _dur, _dps, _life = 0f;
    private bool _maelstrom, _visualOnly, _burst = false;
    private float _pullMul = 1f;   // Implosion cranks this up for a much harder drag-in (NEW)
    private float _dmgT = 0f, _pullT = 0f;   // tick timers for grind damage + drag-in (NEW)
    private Node3D _spin;          // the rotating funnel visual

    // (SHARED) a realistic-but-stylized tornado material — fbm noise scrolled UP + swirled AROUND the funnel, wispy alpha,
    // fresnel edge glow. Reused for any tornado-adjacent effect. `speed` = how fast this layer whips.
    private static Shader _tornadoShader;
    public static ShaderMaterial TornadoMat(Color tint, float speed)
    {
        _tornadoShader ??= new Shader { Code = TornadoCode };
        var m = new ShaderMaterial { Shader = _tornadoShader };
        m.SetShaderParameter("spin", speed);
        m.SetShaderParameter("tint", new Vector3(tint.R, tint.G, tint.B));
        return m;
    }
    private const string TornadoCode = @"
shader_type spatial;
render_mode cull_disabled, unshaded, blend_add, depth_draw_never, shadows_disabled;
uniform float spin = 1.0;
uniform vec3 tint : source_color = vec3(0.6,0.85,1.0);
float h(vec2 p){ return fract(sin(dot(p,vec2(41.3,289.1)))*43758.5453); }
float vn(vec2 p){ vec2 i=floor(p),f=fract(p); f=f*f*(3.0-2.0*f);
  return mix(mix(h(i),h(i+vec2(1,0)),f.x),mix(h(i+vec2(0,1)),h(i+vec2(1,1)),f.x),f.y); }
float fbm(vec2 p){ float v=0.0,a=0.5; for(int i=0;i<4;i++){ v+=a*vn(p); p=p*2.03+1.7; a*=0.5; } return v; }
varying vec3 vn3; varying vec3 vv; varying vec2 uv;
void vertex(){ uv=UV; vn3=NORMAL; vv=(MODEL_MATRIX*vec4(VERTEX,1.0)).xyz; }
void fragment(){
    float t=TIME;
    float twist=(1.0-uv.y)*2.4;                                  // spins faster toward the throat
    float ang=uv.x*7.0 + t*spin*(1.5+twist) + uv.y*3.5;
    float rise=uv.y*5.0 - t*spin*1.7;
    float d = fbm(vec2(ang,rise))*0.6 + fbm(vec2(ang*2.2+t*spin, rise*2.1 - t*spin*2.0))*0.4;
    float prof = smoothstep(0.0,0.12,uv.y) * (1.0-smoothstep(0.7,1.0,uv.y));   // wispy at the very top
    float a = smoothstep(0.42,0.95,d) * prof;
    float fres = pow(1.0-abs(dot(normalize(vn3), normalize(vv-CAMERA_POSITION_WORLD))), 2.5);
    vec3 col = tint*(0.5+d) + fres*mix(tint,vec3(1.0),0.4)*0.5;
    ALBEDO = col; ALPHA = clamp(a,0.0,0.9);
}
";

    // funnel dimensions + spiraling debris, animated in _Process for a real vortex look (NEW)
    private float _topR, _baseR, _colH = 6.0f;
    private readonly List<MeshInstance3D> _debris = new();
    private readonly List<float> _dAng = new(), _dH = new(), _dRise = new(), _dSpin = new(), _dRadJit = new();

    public void Init(Player caster, Vector3 pos, float radius, float dur, float dps, bool maelstrom, bool visualOnly, float pullMul = 1f, bool suppressVisual = false, bool eatsProjectiles = false)
    {
        _caster = caster; _radius = radius; _dur = dur; _dps = dps; _maelstrom = maelstrom; _visualOnly = visualOnly; _pullMul = pullMul;
        GlobalPosition = new Vector3(pos.X, 0f, pos.Z);
        if (eatsProjectiles && !visualOnly) Game.I.RegisterWindRing(new Vector3(pos.X, 0f, pos.Z), radius, dur);   // (NEW) its swirling wall swallows enemy projectiles

        if (suppressVisual) return;   // (NEW) mechanics-only funnel (Implosion supplies its own WindOrb look) — no tornado meshes

        var col = DamageTypes.Col(DamageType.Wind);
        _topR = _radius * 0.95f; _baseR = _radius * 0.14f;
        _colH = Mathf.Max(11f, _radius * 1.25f);   // (NEW) a towering funnel — scales tall with the (now much bigger) radius

        _spin = new Node3D();
        AddChild(_spin);

        // --- funnel body: a tall cone with a PROCEDURAL TORNADO shader — swirling fbm noise scrolled up + around, wispy
        //     alpha, fresnel-rimmed, so it reads as churning realistic-but-stylized wind (shared TornadoMat helper) -----
        var cone = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = _topR * 1.05f, BottomRadius = _baseR + 0.4f, Height = _colH, RadialSegments = 32, Rings = 14, CapTop = false, CapBottom = false },
            MaterialOverride = TornadoMat(col, 1.4f),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        cone.Position = new Vector3(0, 0.3f + _colH * 0.5f, 0);
        AddChild(cone);
        // a faint inner darker core cone so the throat reads dense, not hollow
        var core = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = _topR * 0.5f, BottomRadius = _baseR + 0.2f, Height = _colH * 0.9f, RadialSegments = 20, Rings = 10, CapTop = false, CapBottom = false },
            MaterialOverride = TornadoMat(col.Lerp(new Color(0.10f, 0.12f, 0.16f), 0.6f), 2.4f),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        core.Position = new Vector3(0, 0.3f + _colH * 0.45f, 0);
        AddChild(core);

        // --- helical palisade of thin vertical "wind sheets" wrapping the funnel; the spin sells the swirl ---
        var sheetMat = Game.ToonEmissive(col, 1.6f, 0.0f);
        sheetMat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        sheetMat.AlbedoColor = new Color(col.R, col.G, col.B, 0.26f);
        sheetMat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        sheetMat.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        int sheets = 34;
        for (int i = 0; i < sheets; i++)
        {
            float t = i / (float)(sheets - 1);
            float y = 0.3f + t * _colH;
            float r = Mathf.Lerp(_baseR, _topR, t);
            float ang = t * Mathf.Pi * 4.5f;                                   // ~2¼ turns of helix up the column
            var sheet = new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(Mathf.Lerp(0.5f, 2.3f, t), Mathf.Lerp(0.9f, 1.7f, t), 0.05f) },
                MaterialOverride = sheetMat
            };
            sheet.Position = new Vector3(Mathf.Cos(ang) * r, y, Mathf.Sin(ang) * r);
            sheet.Rotation = new Vector3(0, ang + Mathf.Pi * 0.5f + 0.35f, 0);  // width runs tangent to the funnel, leaned into the spin
            _spin.AddChild(sheet);
        }

        // --- dust skirt kicked up at the base ----------------------------------------------------------------
        var dustMat = Game.ToonEmissive(col, 0.9f, 0.0f);
        dustMat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        dustMat.AlbedoColor = new Color(col.R, col.G, col.B, 0.16f);
        dustMat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        dustMat.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        var dust = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = _topR * 0.85f, BottomRadius = _topR * 1.25f, Height = 0.5f, RadialSegments = 24 },
            MaterialOverride = dustMat
        };
        dust.Position = new Vector3(0, 0.28f, 0);
        _spin.AddChild(dust);
        // a wide flat ring of kicked-up dust sweeping around the base (rides the spin)
        var groundRing = new MeshInstance3D
        {
            Mesh = new TorusMesh { InnerRadius = _topR * 0.9f, OuterRadius = _topR * 1.5f, Rings = 24, RingSegments = 10 },
            MaterialOverride = dustMat
        };
        groundRing.Position = new Vector3(0, 0.14f, 0);
        _spin.AddChild(groundRing);

        // --- debris specks that spiral upward (animated by hand in _Process; the iconic tornado motion) ------
        var debrisMat = Game.ToonEmissive(col, 2.2f, 0.0f);
        debrisMat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        debrisMat.AlbedoColor = new Color(col.R, col.G, col.B, 0.75f);
        debrisMat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        int deb = 28;
        for (int i = 0; i < deb; i++)
        {
            float sz = 0.12f + GD.Randf() * 0.12f;
            var d = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(sz, sz, sz) }, MaterialOverride = debrisMat };
            AddChild(d);
            _debris.Add(d);
            _dAng.Add(GD.Randf() * Mathf.Pi * 2f);
            _dH.Add(GD.Randf() * _colH);
            _dRise.Add(2.4f + GD.Randf() * 2.8f);
            _dSpin.Add(5f + GD.Randf() * 4.5f);
            _dRadJit.Add(0.82f + GD.Randf() * 0.3f);
        }

        AddChild(new OmniLight3D { Position = new Vector3(0, 2.5f, 0), OmniRange = _radius * 1.8f, LightColor = col, LightEnergy = 2.4f });
        AddChild(new OmniLight3D { Position = new Vector3(0, _colH * 0.8f, 0), OmniRange = _radius * 1.4f, LightColor = col, LightEnergy = 1.8f });   // (NEW) a second light high up so the tall funnel glows top-to-bottom

        Scale = new Vector3(0.2f, 0.2f, 0.2f);
        var tw = CreateTween();
        tw.TweenProperty(this, "scale", Vector3.One, 0.25f).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
    }

    public override void _Process(double delta)
    {
        if (Game.I == null || !Game.I.SimActive) return;   // freeze while paused (NEW)
        float dt = (float)delta;
        _life += dt;
        if (_spin != null) _spin.RotateY(dt * 7f);   // fast swirl

        // spiral the debris up the funnel (visual only — runs on every copy, including the ally's) (NEW)
        for (int i = 0; i < _debris.Count; i++)
        {
            var d = _debris[i];
            if (d == null || !GodotObject.IsInstanceValid(d)) continue;
            float h = _dH[i] + _dRise[i] * dt; if (h > _colH) h -= _colH; _dH[i] = h;
            float a = _dAng[i] + _dSpin[i] * dt; _dAng[i] = a;
            float r = Mathf.Lerp(_baseR, _topR, h / _colH) * _dRadJit[i];
            d.Position = new Vector3(Mathf.Cos(a) * r, 0.3f + h, Mathf.Sin(a) * r);
        }

        // grind + drag-in (skipped on the visual-only ally copy). Both go through Net.StormForce so a CLIENT
        // caster's cyclone still affects the host's enemies; host/solo apply immediately. Ticked (not per-frame
        // per-enemy) to keep network traffic light. (NEW)
        if (!_visualOnly && !_burst)
        {
            _dmgT -= dt; _pullT -= dt;
            if (_dmgT <= 0f)
            {
                _dmgT = 0.3f; Game.I.NetMgr?.StormForce(GlobalPosition, _radius, 2, _dps * 0.3f);   // grind tick
                bool hit = false;
                foreach (var e in Game.I.Enemies) if (e != null && GodotObject.IsInstanceValid(e) && !e.Dead) { var d = e.GlobalPosition - GlobalPosition; d.Y = 0f; if (d.Length() < _radius + e.Radius) { hit = true; break; } }
                if (hit) Game.I.AwardDotCombo(Game.I.LocalPeer);   // (NEW) lingering wind builds her spell combo
            }
            if (_pullT <= 0f) { _pullT = 0.06f; Game.I.NetMgr?.StormForce(GlobalPosition, _radius, 0, (_maelstrom ? 6f : 4f) * 0.06f * _pullMul); }  // drag-in step
        }

        if (_life >= _dur && !_burst) Burst();
    }

    // final outward fling + a damage tick as the funnel collapses (also host-authoritative via StormForce)
    private void Burst()
    {
        _burst = true;
        if (!_visualOnly)
        {
            Game.I.NetMgr?.StormForce(GlobalPosition, _radius, 2, _dps * 0.8f);   // parting damage
            Game.I.NetMgr?.StormForce(GlobalPosition, _radius, 1, 6f);            // light outward toss
        }
        var tw = CreateTween();
        tw.TweenProperty(this, "scale", new Vector3(1.4f, 0.2f, 1.4f), 0.3f);   // flatten/spread as it dissipates
        tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(this)) QueueFree(); }));
    }
}
