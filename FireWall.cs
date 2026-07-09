using Godot;

// FireWall.cs — the Ring of Fire finisher. A continuous CURTAIN of flame (a cylindrical shell driven by a procedural fire
// shader: hot/opaque at the base, flickering ragged flame-tips up top, additive glow) that rings the caster for Dur seconds.
// It burns foes standing in the ring band (owner-authoritative); incoming-projectile eating is handled host-side via
// Game.FireRings (registered by FinFireWall). Allies render a Remote visual-only copy (VFX kind 72).
public partial class FireWall : Node3D
{
    public Vector3 Center;
    public float Radius = 5f, Dur = 4f, Dps = 5f, BurnPer = 4f, BurnBomb = 30f;
    public int OwnerPeer = 0;
    public bool Remote = false;

    private float _t = 0f, _dmgTick = 0f;
    private MeshInstance3D _outer, _inner;
    private Color _col;

    private static ShaderMaterial _fireMat;
    private static ShaderMaterial FireMat()
    {
        if (_fireMat != null) return _fireMat;
        var sh = new Shader { Code = @"
shader_type spatial;
render_mode unshaded, cull_disabled, blend_add, depth_draw_never;
uniform vec3 hot : source_color = vec3(1.0, 0.92, 0.5);
uniform vec3 mid : source_color = vec3(1.0, 0.45, 0.1);
uniform vec3 cool : source_color = vec3(0.8, 0.13, 0.04);
uniform float speed = 1.7;
float hash(vec2 p){ return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
float vnoise(vec2 p){
    vec2 i = floor(p); vec2 f = fract(p); f = f*f*(3.0-2.0*f);
    float a = hash(i); float b = hash(i+vec2(1.0,0.0));
    float c = hash(i+vec2(0.0,1.0)); float d = hash(i+vec2(1.0,1.0));
    return mix(mix(a,b,f.x), mix(c,d,f.x), f.y);
}
float fbm(vec2 p){ float v=0.0; float a=0.5; for(int i=0;i<4;i++){ v+=a*vnoise(p); p*=2.0; a*=0.5; } return v; }
void fragment(){
    float y = UV.y;
    float n = fbm(vec2(UV.x*13.0 + TIME*0.4, UV.y*9.0 - TIME*speed));   // more vertical detail so a tall wall isn't a stretched smear
    float a = smoothstep(0.05, 0.5, (1.0 - y*0.7) * (0.45 + 1.0*n));    // flames climb most of the height, ragged tips up top
    vec3 col = mix(hot, mid, clamp(y*3.0, 0.0, 1.0));
    col = mix(col, cool, clamp((y-0.35)*2.0, 0.0, 1.0));
    ALBEDO = col;
    EMISSION = col * (1.6 + 1.6*n);
    ALPHA = a;
}" };
        _fireMat = new ShaderMaterial { Shader = sh };
        return _fireMat;
    }

    private MeshInstance3D MakeCurtain(float r, float h)
    {
        // an OPEN cylinder shell (no caps) = a ring wall; the fire shader paints the flames onto it
        var mi = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = r, BottomRadius = r, Height = h, RadialSegments = 44, Rings = 1, CapTop = false, CapBottom = false }, MaterialOverride = FireMat() };
        mi.Position = new Vector3(0, h * 0.5f, 0);
        return mi;
    }

    public override void _Ready()
    {
        _col = DamageTypes.Col(DamageType.Ember);
        float h = 8f;   // tall curtain — walls the caster off and covers projectiles at any height
        _outer = MakeCurtain(Radius, h); AddChild(_outer);              // outer flame sheet
        _inner = MakeCurtain(Radius * 0.94f, h * 0.9f); AddChild(_inner);   // a second, inner sheet for depth
        AddChild(new OmniLight3D { OmniRange = Radius * 1.8f, LightColor = _col, LightEnergy = 2.4f, Position = new Vector3(0, 2.2f, 0) });
    }

    public override void _Process(double delta)
    {
        var g = Game.I; if (g == null || g.State != GameState.Playing) return;
        float dt = (float)delta; _t += dt;
        float fade = Mathf.Clamp(Dur - _t, 0f, 1f);   // over the last second the flames die DOWN into the ground (node is anchored at ground level)
        Scale = new Vector3(1f, 0.15f + 0.85f * fade, 1f);
        if (!Remote)
        {
            _dmgTick += dt;
            if (_dmgTick >= 0.4f)
            {
                _dmgTick = 0f;
                foreach (var e in g.Enemies.ToArray())
                {
                    if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
                    float d = new Vector2(e.GlobalPosition.X - Center.X, e.GlobalPosition.Z - Center.Z).Length();
                    if (d > Radius - 1.5f && d < Radius + 1.5f)   // the burning ring band
                    {
                        e.Hurt(Dps * 0.4f, DamageType.Ember, false);
                        e.AddBurn(0.5f, BurnPer, BurnBomb, 0f, OwnerPeer);
                    }
                }
            }
        }
        if (_t >= Dur) QueueFree();
    }
}
