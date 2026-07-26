using Godot;

// Frost witch ult: a huge swirling storm that grinds every foe inside it, drops shattering icicles, and has a
// (upgradeable) chance to freeze foes caught in it. Host applies damage/freeze; a visual-only ghost runs on allies.
public partial class Blizzard : Node3D
{
    private Player _caster;
    private float _radius, _dur, _dps, _freezeChance, _life = 0f, _tickT = 0f, _iceT = 0f;
    private bool _remote;
    private Node3D _swirl;
    private ShaderMaterial _vortexMat;
    private static readonly Color IceCol = new(0.72f, 0.9f, 1f);

    // a driving whiteout: an animated snow-vortex shell (fbm streaks spiralling round the funnel) + a rime ground decal
    private const string VortexShader = @"
shader_type spatial;
render_mode cull_disabled, unshaded, depth_draw_never, blend_add;
uniform float t = 0.0;
float hash(vec2 p){ return fract(sin(dot(p, vec2(41.3,289.1)))*43758.5); }
float noise(vec2 p){ vec2 i=floor(p),f=fract(p); f=f*f*(3.0-2.0*f);
  return mix(mix(hash(i),hash(i+vec2(1,0)),f.x),mix(hash(i+vec2(0,1)),hash(i+vec2(1,1)),f.x),f.y); }
void fragment(){
    // spiral the UV around the funnel + drift downward → snow streaming past
    float ang = atan(UV.x-0.5, UV.y-0.5);
    vec2 sp = vec2(ang*2.2 + UV.y*5.0 - t*3.0, UV.y*7.0 - t*2.2);
    float n = noise(sp)*0.6 + noise(sp*2.3)*0.4;
    float streak = smoothstep(0.45, 0.9, n);
    float fres = pow(1.0 - abs(dot(normalize(VIEW), normalize(NORMAL))), 1.4);
    float body = 0.10 + fres*0.35 + streak*0.5;
    ALBEDO = mix(vec3(0.6,0.8,1.0), vec3(1.0), streak);
    EMISSION = mix(vec3(0.55,0.78,1.0), vec3(1.0), streak) * body * 2.2;
    ALPHA = clamp(body, 0.0, 0.7);
}";

    public void Init(Player caster, Vector3 pos, float radius, float dur, float dps, float freezeChance, bool remote)
    {
        _caster = caster; _radius = radius; _dur = dur; _dps = dps; _freezeChance = freezeChance; _remote = remote;
        GlobalPosition = pos;
        var col = IceCol;
        // rime ground decal
        var ring = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = radius, BottomRadius = radius, Height = 0.1f, RadialSegments = 44 } };
        var rm = Game.ToonEmissive(col, 1.2f, 0f); rm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; rm.AlbedoColor = new Color(col.R, col.G, col.B, 0.16f);
        ring.MaterialOverride = rm; ring.Position = new Vector3(0, 0.06f, 0); AddChild(ring);

        // the whiteout vortex shell — a wide cone of animated driving snow
        _vortexMat = new ShaderMaterial { Shader = new Shader { Code = VortexShader } };
        var shell = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = radius * 1.15f, BottomRadius = radius * 0.55f, Height = 9.5f, RadialSegments = 40, CapTop = false, CapBottom = false }, MaterialOverride = _vortexMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        shell.Position = new Vector3(0, 4.7f, 0); AddChild(shell);
        var shell2 = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = radius * 0.7f, BottomRadius = radius * 0.3f, Height = 8.5f, RadialSegments = 30, CapTop = false, CapBottom = false }, MaterialOverride = _vortexMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        shell2.Position = new Vector3(0, 4.2f, 0); AddChild(shell2);

        _swirl = new Node3D(); AddChild(_swirl);
        for (int i = 0; i < 40; i++)
        {
            float a = i / 40f * Mathf.Tau, rr = radius * (0.2f + GD.Randf() * 0.8f);
            float fs = 0.16f + GD.Randf() * 0.16f;
            // (POLISH) flat tumbling ice flakes that glint, instead of solid cubes
            var flake = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(fs, fs, 0.03f) }, MaterialOverride = Game.Emissive(col, 1.4f), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            flake.Position = new Vector3(Mathf.Cos(a) * rr, 1f + GD.Randf() * 5.5f, Mathf.Sin(a) * rr);
            flake.RotationDegrees = new Vector3(GD.Randf() * 360f, GD.Randf() * 360f, GD.Randf() * 360f);
            _swirl.AddChild(flake);
        }
        AddChild(new OmniLight3D { OmniRange = radius * 1.3f, LightColor = col, LightEnergy = 1.9f, ShadowEnabled = false, Position = new Vector3(0, 3f, 0) });
        Game.I.FallingMotes(pos, radius, col.Lerp(Colors.White, 0.4f), 30, 14f);
    }

    public override void _Process(double delta)
    {
        var g = Game.I; if (g == null || !g.SimActive) return;
        float dt = (float)delta; _life += dt;
        if (_swirl != null) _swirl.RotationDegrees = new Vector3(0, _life * 90f, 0);
        _vortexMat?.SetShaderParameter("t", _life);
        _iceT -= dt; if (_iceT <= 0f) { _iceT = 0.1f; DropIcicle(); }
        if (!_remote)
        {
            _tickT -= dt;
            if (_tickT <= 0f)
            {
                _tickT = 0.25f;
                foreach (var e in g.Enemies.ToArray())
                {
                    if (e == null || e.Dead || !GodotObject.IsInstanceValid(e) || e.Remote) continue;
                    var d = e.GlobalPosition - GlobalPosition; d.Y = 0f;
                    if (d.Length() < _radius + e.Radius)
                    {
                        e.Hurt(_dps * 0.25f, DamageType.Frost);
                        if (!e.Frozen && GD.Randf() < _freezeChance * 0.25f) e.AddFreeze(e.FreezeThreshold, _caster != null ? _caster.FreezeThreshMul : 1f, _caster != null ? _caster.FrostDurBonus : 0f);
                    }
                }
            }
        }
        if (_life >= _dur) QueueFree();
    }

    private void DropIcicle()
    {
        var col = new Color(0.72f, 0.9f, 1f);
        float a = GD.Randf() * Mathf.Tau, rr = GD.Randf() * _radius;
        var pos = GlobalPosition + new Vector3(Mathf.Cos(a) * rr, 6.5f, Mathf.Sin(a) * rr);
        var ic = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.2f, Height = 1.2f, RadialSegments = 5 }, MaterialOverride = Game.Emissive(col, 2f) };
        ic.RotationDegrees = new Vector3(180, 0, 0); AddChild(ic); ic.GlobalPosition = pos;
        float gy = Game.I.SurfaceHeight(pos, pos.Y);
        var tw = ic.CreateTween();
        tw.TweenProperty(ic, "global_position", new Vector3(pos.X, gy + 0.3f, pos.Z), 0.38f).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
        tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(ic)) { Game.I.SpawnPollen(ic.GlobalPosition, 1f, col, 4, 0.4f, net: false); ic.QueueFree(); } }));
    }
}
