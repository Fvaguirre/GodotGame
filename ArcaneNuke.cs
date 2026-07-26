using Godot;

// ArcaneNuke.cs — the arcane unicorn's detonation: a white-hot flash, a ground shockwave ring, and a towering rainbow-arcane
// "nuclear" mushroom cloud (~2× the boss's height) that billows for ~2.5s. Any add that enters the column is instantly wiped.
// Host drives the wipe; every machine shows its own bloom.
public partial class ArcaneNuke : Node3D
{
    public const float Life = 2.8f;
    public const float WipeRadius = 36f;
    private float _t = 0f, _wipeT = 0f, _tall = 12f;
    private Node3D _cloud;
    private MeshInstance3D _flash, _ring;
    private OmniLight3D _light;
    private ShaderMaterial _mat;
    private StandardMaterial3D _flashMat, _ringMat;

    // stylized-but-believable arcane fireball cloud: churning fbm billows with FAKE VOLUMETRIC SHADING (noise-gradient
    // normal) so the puffs have real form, a heat ramp (deep indigo smoke → arcane magenta → white-violet plasma cores)
    // that GLOWS in the dense/hot parts and goes smoky (non-emissive) at the wispy edges. No rainbow.
    private const string CloudShader = @"
shader_type spatial;
render_mode cull_disabled, unshaded, depth_prepass_alpha;
uniform float t = 0.0;
uniform vec3 col_core : source_color = vec3(1.5, 1.25, 1.75);   // white-violet plasma
uniform vec3 col_mid  : source_color = vec3(0.85, 0.34, 1.0);   // arcane magenta-violet
uniform vec3 col_edge : source_color = vec3(0.13, 0.08, 0.26);  // deep indigo smoke
float hash(vec3 p){ return fract(sin(dot(p, vec3(12.9,78.2,37.7)))*43758.5); }
float vnoise(vec3 p){ vec3 i=floor(p),f=fract(p); f=f*f*(3.0-2.0*f);
  return mix(mix(mix(hash(i),hash(i+vec3(1,0,0)),f.x),mix(hash(i+vec3(0,1,0)),hash(i+vec3(1,1,0)),f.x),f.y),
             mix(mix(hash(i+vec3(0,0,1)),hash(i+vec3(1,0,1)),f.x),mix(hash(i+vec3(0,1,1)),hash(i+vec3(1,1,1)),f.x),f.y),f.z); }
float fbm(vec3 p){ float v=0.0,a=0.55; for(int i=0;i<4;i++){ v+=a*vnoise(p); p=p*2.02+vec3(0.0,-t*0.5,0.0); a*=0.5; } return v; }
varying vec3 wp;
void vertex(){ wp = (MODEL_MATRIX*vec4(VERTEX,1.0)).xyz; }
void fragment(){
  vec3 sp = wp*0.16 + vec3(0.0,-t*0.7,0.0);
  float n = fbm(sp);
  // fake volumetric lighting from the noise gradient → the billows gain rounded form + shaded undersides
  float e = 0.35;
  float nx = fbm(sp+vec3(e,0,0)) - n;
  float ny = fbm(sp+vec3(0,e,0)) - n;
  float nz = fbm(sp+vec3(0,0,e)) - n;
  vec3 nrm = normalize(vec3(-nx, e*0.6 - ny, -nz));
  float lit = clamp(0.45 + 0.55*dot(nrm, normalize(vec3(0.35,0.85,0.3))), 0.0, 1.0);
  float dens = smoothstep(0.28, 0.72, n);
  float hot  = smoothstep(0.55, 0.95, n);                       // hottest inner cores
  vec3 col = mix(col_edge, col_mid, dens);
  col = mix(col, col_core, hot);
  col *= (0.45 + 0.95*lit);                                     // volumetric form (shaded undersides)
  ALBEDO = col*0.5;
  EMISSION = col * (dens*0.7 + hot*2.0);                        // plasma glows; smoky edges don't
  float fres = pow(1.0 - abs(dot(normalize(VIEW), normalize(NORMAL))), 1.2);
  ALPHA = clamp((0.34 + 0.6*n) * (0.4 + 0.6*fres), 0.0, 0.94);
}";

    public void Init(float bossRadius)
    {
        _tall = Mathf.Max(34f, bossRadius * 10f);   // (BIGGER) a towering column — was max(11, r·6)
        _mat = new ShaderMaterial { Shader = new Shader { Code = CloudShader } };
        _mat.SetShaderParameter("t", 0f);

        // the mushroom: a mass of MANY overlapping, size-varied, jittered billows so the noisy shader blends them into one
        // continuous cauliflower cloud (not a readable handful of spheres). Stem column → wide domed cap w/ under-roll → ground skirt.
        _cloud = new Node3D { Scale = new Vector3(0.15f, 0.15f, 0.15f) }; AddChild(_cloud);
        var rng = new RandomNumberGenerator(); rng.Randomize();
        float T = _tall;
        void Puff(float r, Vector3 pos) { var m = new MeshInstance3D { Mesh = new SphereMesh { Radius = r, Height = r * 2f, RadialSegments = 12, Rings = 6 }, MaterialOverride = _mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Position = pos }; _cloud.AddChild(m); }

        // STEM — a churning column, fat at the base (the dust foot), tapering into the neck; jittered puffs per level
        int stemLv = 7;
        for (int i = 0; i < stemLv; i++)
        {
            float u = i / (float)(stemLv - 1);                                        // 0 base → 1 neck
            float y = Mathf.Lerp(0.05f, 0.66f, u) * T;
            float rad = Mathf.Lerp(0.21f, 0.12f, u) * T;                              // (FATTER) beefier column
            for (int k = 0; k < 3; k++)
            {
                float a = rng.Randf() * Mathf.Tau, off = rng.RandfRange(0f, 0.06f) * T;
                Puff(rad * rng.RandfRange(0.8f, 1.12f), new Vector3(Mathf.Cos(a) * off, y + rng.RandfRange(-0.03f, 0.03f) * T, Mathf.Sin(a) * off));
            }
        }

        // CAP — the WIDE mushroom head (much broader now). Two rings of big rim billows + a domed centre + overhead caps.
        float capY = 0.82f * T, capR = 0.56f * T;   // (WIDER) was 0.36
        int rim = 16;
        for (int i = 0; i < rim; i++)
        {
            float a = i / (float)rim * Mathf.Tau + rng.RandfRange(-0.12f, 0.12f);
            float rr = capR * rng.RandfRange(0.82f, 1.06f);
            Puff(0.24f * T * rng.RandfRange(0.85f, 1.18f), new Vector3(Mathf.Cos(a) * rr, capY + rng.RandfRange(-0.05f, 0.07f) * T, Mathf.Sin(a) * rr));
        }
        int rim2 = 10;   // an inner ring so the broad cap fills in solid
        for (int i = 0; i < rim2; i++)
        {
            float a = i / (float)rim2 * Mathf.Tau + rng.RandfRange(-0.15f, 0.15f);
            float rr = capR * rng.RandfRange(0.45f, 0.68f);
            Puff(0.26f * T * rng.RandfRange(0.85f, 1.15f), new Vector3(Mathf.Cos(a) * rr, capY + rng.RandfRange(0f, 0.1f) * T, Mathf.Sin(a) * rr));
        }
        Puff(0.34f * T, new Vector3(0, capY + 0.06f * T, 0));                          // dome centre
        Puff(0.26f * T, new Vector3(0, capY + 0.18f * T, 0));                          // crown

        // UNDER-ROLL — the vortex curl tucked below & inside the rim, so the wide cap reads as rolling under
        int roll = 13;
        for (int i = 0; i < roll; i++)
        {
            float a = i / (float)roll * Mathf.Tau + rng.RandfRange(-0.12f, 0.12f);
            float rr = capR * rng.RandfRange(0.80f, 0.95f);
            Puff(0.16f * T * rng.RandfRange(0.85f, 1.18f), new Vector3(Mathf.Cos(a) * rr, capY - 0.16f * T, Mathf.Sin(a) * rr));
        }

        // CAULIFLOWER DETAIL — small puffs sprinkled over the broad cap, denser toward centre, bulging upward
        int detail = 34;
        for (int i = 0; i < detail; i++)
        {
            float a = rng.Randf() * Mathf.Tau, rr = capR * Mathf.Sqrt(rng.Randf());
            float yy = capY + rng.RandfRange(-0.02f, 0.16f) * T + (1f - rr / capR) * 0.07f * T;
            Puff(0.11f * T * rng.RandfRange(0.7f, 1.3f), new Vector3(Mathf.Cos(a) * rr, yy, Mathf.Sin(a) * rr));
        }

        // GROUND SKIRT — a wide dust wall rolling out along the ground at the base
        int skirt = 14;
        for (int i = 0; i < skirt; i++)
        {
            float a = i / (float)skirt * Mathf.Tau + rng.RandfRange(-0.15f, 0.15f);
            float rr = rng.RandfRange(0.22f, 0.5f) * T;
            Puff(0.14f * T * rng.RandfRange(0.8f, 1.25f), new Vector3(Mathf.Cos(a) * rr, 0.05f * T, Mathf.Sin(a) * rr));
        }

        // white-hot flash core
        _flashMat = new StandardMaterial3D { AlbedoColor = new Color(1f, 0.95f, 1f, 0.95f), EmissionEnabled = true, Emission = new Color(1f, 0.9f, 1f), EmissionEnergyMultiplier = 8f, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, BlendMode = BaseMaterial3D.BlendModeEnum.Add, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded };
        _flash = new MeshInstance3D { Mesh = new SphereMesh { Radius = _tall * 0.2f, Height = _tall * 0.4f }, MaterialOverride = _flashMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Position = new Vector3(0, _tall * 0.2f, 0) };
        AddChild(_flash);

        // ground shockwave ring
        _ringMat = new StandardMaterial3D { AlbedoColor = new Color(0.8f, 0.6f, 1f, 0.8f), EmissionEnabled = true, Emission = new Color(0.8f, 0.6f, 1f), EmissionEnergyMultiplier = 4f, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, BlendMode = BaseMaterial3D.BlendModeEnum.Add, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
        _ring = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = 0.9f, OuterRadius = 1.0f }, MaterialOverride = _ringMat, Position = new Vector3(0, 0.3f, 0), RotationDegrees = new Vector3(90, 0, 0) };
        AddChild(_ring);

        // debris motes flung up and out
        var debris = new GpuParticles3D { Amount = 90, Lifetime = 1.6, OneShot = true, Explosiveness = 0.85f, Position = new Vector3(0, 1f, 0) };
        var pm = new ParticleProcessMaterial { Direction = new Vector3(0, 1, 0), Spread = 80f, InitialVelocityMin = 8f, InitialVelocityMax = 22f, Gravity = new Vector3(0, -12f, 0), ScaleMin = 0.15f, ScaleMax = 0.5f, Color = new Color(0.85f, 0.7f, 1f, 0.9f) };
        debris.ProcessMaterial = pm;
        var dMat = new StandardMaterial3D { AlbedoColor = new Color(0.85f, 0.7f, 1f, 0.9f), EmissionEnabled = true, Emission = new Color(0.8f, 0.6f, 1f), EmissionEnergyMultiplier = 3f, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, BlendMode = BaseMaterial3D.BlendModeEnum.Add, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled };
        debris.DrawPass1 = new QuadMesh { Size = new Vector2(0.4f, 0.4f), Material = dMat };
        AddChild(debris);

        _light = new OmniLight3D { OmniRange = _tall * 2.4f, LightColor = new Color(0.85f, 0.7f, 1f), LightEnergy = 9f, Position = new Vector3(0, _tall * 0.5f, 0) };
        AddChild(_light);
        Game.I?.Sfx?.ArcaneBoom(GlobalPosition);
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta; _t += dt;
        _mat?.SetShaderParameter("t", _t);
        float grow = Mathf.Min(1f, _t / 0.5f);
        if (_cloud != null) _cloud.Scale = Vector3.One * Mathf.Lerp(0.12f, 1.2f, Mathf.Sqrt(grow));   // (BIGGER) blooms larger
        if (_flash != null) { float fa = Mathf.Clamp(1f - _t / 0.35f, 0f, 1f); _flashMat.AlbedoColor = new Color(1f, 0.95f, 1f, fa * 0.95f); _flash.Scale = Vector3.One * Mathf.Lerp(0.5f, 2.2f, _t / 0.35f); }
        if (_ring != null) { float rk = Mathf.Clamp(_t / 0.8f, 0f, 1f); float rs = Mathf.Lerp(1f, WipeRadius, rk); _ring.Scale = new Vector3(rs, rs, 1f + rk * 3f); _ringMat.AlbedoColor = new Color(0.8f, 0.6f, 1f, (1f - rk) * 0.8f); }
        if (_light != null) _light.LightEnergy = Mathf.Lerp(11f, 1.5f, _t / Life);

        if (Game.I != null && Game.I.IsAuthority)
        {
            _wipeT -= dt;
            if (_wipeT <= 0f)
            {
                _wipeT = 0.15f;
                foreach (var e in Game.I.Enemies.ToArray())
                    if (e != null && !e.Dead && GodotObject.IsInstanceValid(e) && !e.IsBoss &&
                        new Vector2(e.GlobalPosition.X - GlobalPosition.X, e.GlobalPosition.Z - GlobalPosition.Z).Length() < WipeRadius)
                        e.Hurt(e.MaxHp + 9999f, DamageType.Arcane, true);
            }
        }

        if (_t >= Life)
        {
            if (_cloud != null) { var tw = _cloud.CreateTween(); tw.TweenProperty(_cloud, "scale", Vector3.One * 1.5f, 0.7f); }
            var t2 = CreateTween(); t2.TweenInterval(0.7f); t2.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(this)) QueueFree(); }));
            SetProcess(false);
        }
    }
}
