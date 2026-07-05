using Godot;
using System.Collections.Generic;

// WindPad.cs — the "Whirlwind" charged-modifier object: a stationary tornado parked where a full charge
// landed. It grinds enemies that wander in (host-authoritative via Net.StormForce, so a client caster's
// whirlwind still hurts the host's enemies), and it doubles as a JUMP PAD — any player standing in it gets
// launched high into the air (great for setting up air combos). The caster spawns the real one (damage);
// allies receive a visual-only copy via BroadcastVfx kind 12 that still launches THEIR local player, so the
// pad is usable by everyone. Self-despawns after its duration. (NEW)
public partial class WindPad : Node3D
{
    private Player _caster;
    private float _radius, _dur, _dps, _life = 0f, _dmgT = 0f, _launchCd = 0f;
    private bool _visualOnly;
    private Node3D _spin;
    private const float LaunchVel = 19f;   // the "huge boost"

    // funnel dims + spiraling debris for the vortex look (NEW)
    private float _topR, _baseR, _colH = 4.5f;
    private readonly List<MeshInstance3D> _debris = new();
    private readonly List<float> _dAng = new(), _dH = new(), _dRise = new(), _dSpin = new(), _dRadJit = new();

    public void Init(Player caster, Vector3 pos, float radius, float dur, float dps, bool visualOnly)
    {
        _caster = caster; _radius = radius; _dur = dur; _dps = dps; _visualOnly = visualOnly;
        GlobalPosition = new Vector3(pos.X, 0f, pos.Z);

        var col = DamageTypes.Col(DamageType.Wind);
        _topR = _radius * 0.95f; _baseR = _radius * 0.2f;

        _spin = new Node3D();
        AddChild(_spin);

        // translucent funnel body
        var bodyMat = MakeMat(col, 0.10f, false);
        var cone = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = _topR * 1.05f, BottomRadius = _baseR, Height = _colH, RadialSegments = 28 },
            MaterialOverride = bodyMat
        };
        cone.Position = new Vector3(0, 0.3f + _colH * 0.5f, 0);
        AddChild(cone);

        // helical palisade of thin vertical wind sheets — the spin makes it a vortex
        var sheetMat = MakeMat(col, 0.26f, true);
        int sheets = 22;
        for (int i = 0; i < sheets; i++)
        {
            float t = i / (float)(sheets - 1);
            float y = 0.3f + t * _colH;
            float r = Mathf.Lerp(_baseR, _topR, t);
            float ang = t * Mathf.Pi * 4f;
            var sheet = new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(Mathf.Lerp(0.6f, 2.2f, t), Mathf.Lerp(0.85f, 1.5f, t), 0.05f) },
                MaterialOverride = sheetMat
            };
            sheet.Position = new Vector3(Mathf.Cos(ang) * r, y, Mathf.Sin(ang) * r);
            sheet.Rotation = new Vector3(0, ang + Mathf.Pi * 0.5f + 0.35f, 0);
            _spin.AddChild(sheet);
        }

        // a bright, filled footprint disc so the standable jump-pad spot reads clearly
        var padMat = MakeMat(col, 0.42f, true);
        var pad = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = _radius, BottomRadius = _radius, Height = 0.08f, RadialSegments = 28 },
            MaterialOverride = padMat
        };
        pad.Position = new Vector3(0, 0.06f, 0);
        AddChild(pad);

        // spiraling debris specks
        var debrisMat = MakeMat(col, 0.75f, true);
        int deb = 14;
        for (int i = 0; i < deb; i++)
        {
            float sz = 0.12f + GD.Randf() * 0.1f;
            var d = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(sz, sz, sz) }, MaterialOverride = debrisMat };
            AddChild(d);
            _debris.Add(d);
            _dAng.Add(GD.Randf() * Mathf.Pi * 2f);
            _dH.Add(GD.Randf() * _colH);
            _dRise.Add(2.2f + GD.Randf() * 2.6f);
            _dSpin.Add(5f + GD.Randf() * 4f);
            _dRadJit.Add(0.85f + GD.Randf() * 0.28f);
        }

        AddChild(new OmniLight3D { Position = new Vector3(0, 2f, 0), OmniRange = _radius * 2f, LightColor = col, LightEnergy = 1.6f });

        Scale = new Vector3(0.2f, 0.2f, 0.2f);
        var tw = CreateTween();
        tw.TweenProperty(this, "scale", Vector3.One, 0.25f).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
    }

    // a translucent emissive wind material; `unshaded` for the glowing sheets/debris, lit for the soft body
    private static StandardMaterial3D MakeMat(Color col, float alpha, bool unshaded)
    {
        var m = new StandardMaterial3D
        {
            AlbedoColor = new Color(col.R, col.G, col.B, alpha),
            EmissionEnabled = true,
            Emission = col,
            EmissionEnergyMultiplier = unshaded ? 1.6f : 1.0f,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        if (unshaded) m.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        return m;
    }

    public override void _Process(double delta)
    {
        if (Game.I == null || Game.I.State != GameState.Playing) return;   // freeze while paused (NEW)
        float dt = (float)delta;
        _life += dt;
        if (_spin != null) _spin.RotateY(dt * 6f);
        if (_launchCd > 0f) _launchCd -= dt;

        // spiral debris up the funnel (visual; runs on every copy)
        for (int i = 0; i < _debris.Count; i++)
        {
            var d = _debris[i];
            if (d == null || !GodotObject.IsInstanceValid(d)) continue;
            float h = _dH[i] + _dRise[i] * dt; if (h > _colH) h -= _colH; _dH[i] = h;
            float a = _dAng[i] + _dSpin[i] * dt; _dAng[i] = a;
            float r = Mathf.Lerp(_baseR, _topR, h / _colH) * _dRadJit[i];
            d.Position = new Vector3(Mathf.Cos(a) * r, 0.3f + h, Mathf.Sin(a) * r);
        }

        // grind (only the caster's real pad deals damage; routed so client casters work too)
        if (!_visualOnly)
        {
            _dmgT -= dt;
            if (_dmgT <= 0f) { _dmgT = 0.3f; Game.I.NetMgr?.StormForce(GlobalPosition, _radius, 2, _dps * 0.3f); }
        }

        // jump pad: every machine launches ITS OWN local player when they're standing in the footprint, so
        // all players can use any whirlwind (real or the visual-only ally copy)
        var p = Game.I.Player;
        if (p != null && _launchCd <= 0f)
        {
            Vector3 flat = p.GlobalPosition - GlobalPosition; flat.Y = 0;
            float groundY = Game.I.SurfaceHeight(GlobalPosition, 0f);
            if (flat.Length() <= _radius && (p.GlobalPosition.Y - groundY) < 2.2f)
            {
                p.WindLaunch(LaunchVel);
                _launchCd = 0.5f;
                Game.I.Sfx?.Release(DamageType.Wind);
            }
        }

        if (_life >= _dur)
        {
            var tw = CreateTween();
            tw.TweenProperty(this, "scale", new Vector3(1.3f, 0.1f, 1.3f), 0.3f);
            tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(this)) QueueFree(); }));
            SetProcess(false);
        }
    }
}
