using Godot;
using System.Collections.Generic;

// Haunt.cs — a roaming "hot zone" the director lights up. Fighting INSIDE ramps harder + pays more; kill enough foes to
// BREAK it. VISUAL ONLY (Game.cs owns the logic). The look: an OMINOUS STORM. A dark thunderhead swirls in the sky and
// forks lightning down; the ground splits into glowing GREEN cracks; a cyclone of purple billows + fading phantoms turns
// over it, underlit sickly green; autumn leaves are torn in circles around the funnel; and the wind howls (foliage whips).
public partial class Haunt : Node3D
{
    public float Radius = 42f;
    public bool Remote = false;
    private float _t, _lightT = 2f;
    private float _boltAge = 99f;   // seconds since the last ambient fork — the geometry only SHOWS for a flash
    private MeshInstance3D _disc, _rim;
    private Node3D _vortex, _sky, _bolt;
    private OmniLight3D _flash, _greenCore;
    private readonly List<Ghost> _ghosts = new();

    private static readonly Color Purple = new Color(0.58f, 0.24f, 0.86f);   // cursed violet
    private static readonly Color Green  = new Color(0.36f, 1.0f, 0.42f);    // ominous green cracks
    private static readonly Color Cloud  = new Color(0.16f, 0.13f, 0.22f);   // dark thunderhead
    private static readonly Color[] Leaves = {
        new Color(0.95f, 0.48f, 0.13f), new Color(0.78f, 0.22f, 0.10f), new Color(0.86f, 0.62f, 0.16f), new Color(0.62f, 0.30f, 0.10f) };

    private const float FunnelH = 18f, SkyY = 46f;
    private const int GhostN = 8;

    private class Ghost { public Node3D Node; public MeshInstance3D[] Parts; public float[] BaseA; public float Ang, AngSpd, Life, MaxLife, Seed; }

    public void Init(Vector3 center, float radius)
    {
        Radius = radius;
        GlobalPosition = new Vector3(center.X, 0f, center.Z);
        World.EnsureHauntWindGlobals();

        BuildGround(radius);
        BuildSkyStorm(radius);
        BuildCyclone(radius);
        BuildLeaves(radius);

        _greenCore = new OmniLight3D { LightColor = Green, LightEnergy = 2.2f, OmniRange = radius * 0.9f, Position = new Vector3(0, 1.2f, 0) };
        AddChild(_greenCore);
        AddChild(new OmniLight3D { LightColor = Purple, LightEnergy = 2.0f, OmniRange = radius * 1.3f, Position = new Vector3(0, 8f, 0) });
    }

    // ---- ground: cursed disc + radiating green cracks ---------------------------------------------------------
    private void BuildGround(float radius)
    {
        float cy = Game.I != null ? Game.I.SurfaceHeight(GlobalPosition, 0f) : 0f;   // conform the zone to the ground height under its heart
        _disc = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = radius, BottomRadius = radius, Height = 0.1f, RadialSegments = 44 } };
        var dm = Glow(Purple, 0.16f, 0.5f); dm.BlendMode = BaseMaterial3D.BlendModeEnum.Mix;
        _disc.MaterialOverride = dm; _disc.Position = new Vector3(0, cy + 0.06f, 0);
        AddChild(_disc);

        _rim = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = radius - 1.2f, OuterRadius = radius + 0.5f, Rings = 52, RingSegments = 10 } };
        _rim.MaterialOverride = Game.Emissive(new Color(1f, 0.32f, 0.34f), 2.6f);
        _rim.Position = new Vector3(0, cy + 0.18f, 0);
        AddChild(_rim);

        // jagged green cracks radiating from the heart like FORKED LIGHTNING. Each crack is a CONTINUOUS ribbon that
        // traces a gently-meandering polyline out from the centre — a dark sunken fissure with a molten-green core
        // running down it — with occasional branches. Continuous geometry → they read as connected cracks, not the old
        // loose straight strips.
        var rng = new RandomNumberGenerator { Seed = (ulong)Mathf.RoundToInt(radius * 97f) + 5 };
        var wallMat = new StandardMaterial3D { AlbedoColor = new Color(0.015f, 0.03f, 0.015f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
        var coreMat = new StandardMaterial3D { AlbedoColor = Green.Lerp(Colors.White, 0.35f), EmissionEnabled = true, Emission = Green, EmissionEnergyMultiplier = 3.4f, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
        int cracks = 11;
        for (int c = 0; c < cracks; c++)
        {
            float a = c * Mathf.Tau / cracks + rng.RandfRange(-0.18f, 0.18f);
            var pts = new System.Collections.Generic.List<Vector3>();
            var wds = new System.Collections.Generic.List<float>();
            int segs = 10 + rng.RandiRange(0, 4);
            float len = radius * rng.RandfRange(0.62f, 0.95f);
            Vector3 pos = Vector3.Zero; float dir = a;
            pts.Add(pos); wds.Add(1.7f);
            for (int s = 1; s <= segs; s++)
            {
                dir += rng.RandfRange(-0.17f, 0.17f);   // gentle meander → lightning, not zigzag
                pos += new Vector3(Mathf.Cos(dir), 0, Mathf.Sin(dir)) * (len / segs);
                pts.Add(pos); wds.Add(Mathf.Lerp(1.7f, 0.14f, s / (float)segs));
                if (s >= 3 && s <= segs - 2 && rng.Randf() < 0.22f)   // a fork — the classic branching-lightning look
                {
                    var bp = new System.Collections.Generic.List<Vector3> { pos };
                    var bw = new System.Collections.Generic.List<float> { wds[wds.Count - 1] * 0.8f };
                    float bd = dir + rng.RandfRange(0.5f, 1.0f) * (rng.Randf() < 0.5f ? 1f : -1f);
                    Vector3 bpos = pos; int bsegs = 3 + rng.RandiRange(0, 3);
                    for (int k = 1; k <= bsegs; k++)
                    {
                        bd += rng.RandfRange(-0.2f, 0.2f);
                        bpos += new Vector3(Mathf.Cos(bd), 0, Mathf.Sin(bd)) * (len / segs);
                        bp.Add(bpos); bw.Add(Mathf.Lerp(bw[0], 0.1f, k / (float)bsegs));
                    }
                    AddCrackRibbon(bp, bw, wallMat, 1.5f, 0.14f);
                    AddCrackRibbon(bp, bw, coreMat, 0.5f, 0.17f);
                }
            }
            AddCrackRibbon(pts, wds, wallMat, 1.5f, 0.14f);   // dark sunken fissure
            AddCrackRibbon(pts, wds, coreMat, 0.5f, 0.17f);   // molten-green core down its length
            var lp = pts[pts.Count / 2];
            float lsy = Game.I != null ? Game.I.SurfaceHeight(new Vector3(GlobalPosition.X + lp.X, 0f, GlobalPosition.Z + lp.Z), 0f) : 0f;
            AddChild(new OmniLight3D { LightColor = Green, LightEnergy = 2.2f, OmniRange = 12f, ShadowEnabled = false, Position = new Vector3(lp.X, lsy + 0.7f, lp.Z) });
        }
        // a bright molten heart where every crack converges
        {
            float hy = Game.I != null ? Game.I.SurfaceHeight(GlobalPosition, 0f) : 0f;
            var heart = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 2.6f, BottomRadius = 2.6f, Height = 0.05f, RadialSegments = 22 }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, MaterialOverride = coreMat };
            heart.Position = new Vector3(0, hy + 0.15f, 0); AddChild(heart);
            AddChild(new OmniLight3D { LightColor = Green, LightEnergy = 3.2f, OmniRange = 16f, ShadowEnabled = false, Position = new Vector3(0, hy + 1.2f, 0) });
        }
    }

    // build a continuous flat ribbon on the ground tracing `pts` (per-point widths), conformed to terrain height + yoff
    private void AddCrackRibbon(System.Collections.Generic.List<Vector3> pts, System.Collections.Generic.List<float> wds, Material mat, float wmul, float yoff)
    {
        if (pts.Count < 2) return;
        float GY(Vector3 p) => (Game.I != null ? Game.I.SurfaceHeight(new Vector3(GlobalPosition.X + p.X, 0f, GlobalPosition.Z + p.Z), 0f) : 0f) + yoff;
        var im = new ImmediateMesh();
        im.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            Vector3 a = pts[i], b = pts[i + 1];
            Vector3 d = b - a; d.Y = 0f; if (d.LengthSquared() < 1e-5f) continue; d = d.Normalized();
            Vector3 perp = new Vector3(-d.Z, 0, d.X);
            float wa = wds[i] * wmul * 0.5f, wb = wds[i + 1] * wmul * 0.5f;
            float ya = GY(a), yb = GY(b);
            Vector3 a0 = new Vector3(a.X, ya, a.Z) + perp * wa, a1 = new Vector3(a.X, ya, a.Z) - perp * wa;
            Vector3 b0 = new Vector3(b.X, yb, b.Z) + perp * wb, b1 = new Vector3(b.X, yb, b.Z) - perp * wb;
            im.SurfaceAddVertex(a0); im.SurfaceAddVertex(a1); im.SurfaceAddVertex(b0);
            im.SurfaceAddVertex(a1); im.SurfaceAddVertex(b1); im.SurfaceAddVertex(b0);
        }
        im.SurfaceEnd();
        AddChild(new MeshInstance3D { Mesh = im, MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
    }

    // ---- sky: a swirling thunderhead + forking lightning ------------------------------------------------------
    private void BuildSkyStorm(float radius)
    {
        _sky = new Node3D { Position = new Vector3(0, SkyY, 0) };
        AddChild(_sky);
        var rng = new RandomNumberGenerator { Seed = (ulong)Mathf.RoundToInt(radius * 53f) + 11 };
        // a dark churning cloud mass — flattened puffs across a wide disc, some greenish, low emissive
        int puffs = 46;
        for (int i = 0; i < puffs; i++)
        {
            float pr = radius * Mathf.Sqrt(rng.Randf()) * 1.35f;
            float pa = rng.RandfRange(0, Mathf.Tau);
            float sz = rng.RandfRange(5f, 11f);
            var tint = Cloud.Lerp(Green * 0.5f, rng.Randf() * 0.25f).Lerp(Purple * 0.6f, rng.Randf() * 0.25f);
            // (FIX) an 8-segment sphere squashed to half height is a faceted SLAB seen from underneath — that's the
            // "rectangles flying across the screen" in a Haunt: 46 of them, drifting as the sky deck rotates. Rounder
            // now, and the material dissolves the silhouette at grazing angles so a puff has no hard edge at all.
            var puff = new MeshInstance3D {
                Mesh = new SphereMesh { Radius = sz, Height = sz * 1.1f, RadialSegments = 16, Rings = 10 },
                MaterialOverride = CloudMat(tint),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Position = new Vector3(Mathf.Cos(pa) * pr, rng.RandfRange(-4f, 5f), Mathf.Sin(pa) * pr),
                Scale = new Vector3(1f, 0.62f, 1f),
            };
            _sky.AddChild(puff);
        }
        // the lightning bolt node (rebuilt each strike) + a flash light at the cloud base
        _bolt = new Node3D(); _sky.AddChild(_bolt);
        _flash = new OmniLight3D { LightColor = new Color(0.8f, 0.95f, 1f), LightEnergy = 0f, OmniRange = radius * 2.2f, Position = new Vector3(0, -6f, 0) };
        _sky.AddChild(_flash);
    }

    private void Strike()
    {
        foreach (var c in _bolt.GetChildren()) c.QueueFree();
        var rng = new RandomNumberGenerator { Seed = (ulong)Mathf.RoundToInt(_t * 1000f) + 3 };
        var boltMat = Glow(new Color(0.85f, 0.95f, 1f), 1f, 6f);
        // a jagged fork from the cloud base down toward the ground heart (segments are Z-length boxes aimed with LookAt)
        Vector3 p = new Vector3(rng.RandfRange(-Radius * 0.3f, Radius * 0.3f), -2f, rng.RandfRange(-Radius * 0.3f, Radius * 0.3f));
        int segs = 9;
        // (FIX) LookAtFromPosition is a GLOBAL-space operation. These a/b are offsets under _sky, so they have to be
        // converted — passing them raw teleported every segment to those coordinates near the world origin (i.e. buried
        // under the middle of the map), which is why the storm's ambient forks were never actually visible over a Haunt.
        Vector3 skyOrigin = _bolt.GlobalPosition;
        void BoltSeg(Vector3 a, Vector3 b, float thick)
        {
            Vector3 ga = skyOrigin + a, gb = skyOrigin + b;
            var mid = (ga + gb) * 0.5f; float len = (gb - ga).Length();
            if (len < 0.05f) return;
            var seg = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(thick, thick, len) }, MaterialOverride = boltMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            _bolt.AddChild(seg);
            var up = Mathf.Abs((gb - ga).Normalized().Dot(Vector3.Up)) > 0.98f ? Vector3.Forward : Vector3.Up;
            seg.LookAtFromPosition(mid, gb, up);   // -Z spans a→b, so the box's Z length lies along the segment
        }
        for (int s = 0; s < segs; s++)
        {
            var next = p + new Vector3(rng.RandfRange(-3f, 3f), -(SkyY - 8f) / segs, rng.RandfRange(-3f, 3f));
            BoltSeg(p, next, 0.35f);
            if (s > 2 && rng.Randf() < 0.35f)   // a small fork
            {
                var f = ((p + next) * 0.5f) + new Vector3(rng.RandfRange(-5f, 5f), -3f, rng.RandfRange(-5f, 5f));
                BoltSeg((p + next) * 0.5f, f, 0.2f);
            }
            p = next;
        }
        _flash.LightEnergy = 9f;
        _boltAge = 0f;
        if (!Remote) Game.I?.Sfx?.StormThunder(GlobalPosition + Vector3.Up * 6f, 0.85f + GD.Randf() * 0.4f);
    }

    // ---- the cyclone: funnel of billows + phantoms ------------------------------------------------------------
    private void BuildCyclone(float radius)
    {
        _vortex = new Node3D(); AddChild(_vortex);
        // (OVERHAUL) the funnel is now 3 nested SHADER CONES — procedural swirling noise that reads as a real churning
        // tornado (wispy vertical streaks whipping around), instead of a lame ring of rotating spheres. Wide at the top,
        // pinched at the base; underlit green low, cursed purple high; each layer scrolls at its own speed for depth.
        float topR = radius * 0.82f, baseR = radius * 0.06f;
        var layers = new (float rMul, float speed, float dens, float alpha)[] {
            (1.15f, 0.7f, 0.42f, 0.55f),   // outer haze, slow
            (0.85f, 1.15f, 0.5f, 0.8f),    // main body
            (0.5f,  1.9f, 0.62f, 1.0f),    // inner fast dark-cored spin
        };
        foreach (var (rMul, speed, dens, alpha) in layers)
        {
            var cone = new MeshInstance3D {
                Mesh = new CylinderMesh { TopRadius = topR * rMul, BottomRadius = baseR * rMul + 0.4f, Height = FunnelH, RadialSegments = 28, Rings = 12, CapTop = false, CapBottom = false },
                MaterialOverride = TornadoMat(speed, dens, alpha),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Position = new Vector3(0, FunnelH * 0.5f + 0.5f, 0),
            };
            _vortex.AddChild(cone);
        }
        // a dark churning debris core at the very throat so the base doesn't read as empty
        var core = new MeshInstance3D {
            Mesh = new CylinderMesh { TopRadius = baseR + 1.6f, BottomRadius = baseR + 0.6f, Height = FunnelH * 0.55f, RadialSegments = 20, CapTop = false, CapBottom = false },
            MaterialOverride = TornadoMat(2.6f, 0.72f, 0.9f, dark: true),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Position = new Vector3(0, FunnelH * 0.28f, 0),
        };
        _vortex.AddChild(core);
        // rising spectral wisps sucked into the swirl
        var p = new GpuParticles3D { Amount = 100, Lifetime = 3.4, Position = new Vector3(0, 0.4f, 0) };
        p.ProcessMaterial = new ParticleProcessMaterial {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Ring, EmissionRingRadius = radius * 0.7f, EmissionRingInnerRadius = radius * 0.12f, EmissionRingHeight = 0.6f, EmissionRingAxis = new Vector3(0, 1, 0),
            Direction = new Vector3(0, 1, 0), Spread = 6f, InitialVelocityMin = 2.2f, InitialVelocityMax = 5f,
            Gravity = new Vector3(0, 1.6f, 0), TangentialAccelMin = 7f, TangentialAccelMax = 13f, RadialAccelMin = -3f, RadialAccelMax = -7f,
            ScaleMin = 0.4f, ScaleMax = 1.4f, Color = new Color(0.7f, 0.9f, 0.8f, 0.6f) };
        // (FIX) these were 1.5m additive quads with a HARD edge — up close they read as big pale CARDS plastered over the
        // screen, and since they emit from a ring at radius*0.7 they spawn right on top of a witch fighting in the zone.
        // A soft radial falloff removes the silhouette entirely (they become actual wisps), and they're smaller now.
        p.DrawPass1 = new QuadMesh { Size = new Vector2(0.9f, 0.9f), Material = WispMat() };
        _vortex.AddChild(p);

        for (int i = 0; i < GhostN; i++) _ghosts.Add(MakeGhost(i));
    }

    // ---- autumn leaves whirling around the funnel -------------------------------------------------------------
    // These are the SAME authored Meshy leaves the Grove scatters on the ground (PropGlb "leaf_a/b/c", the models behind
    // PropField.Kind.GlbLeaf*), not flat coloured cards. Each band torn up by the storm is one leaf model, so the debris
    // in the funnel matches the leaf litter you walk over. The GLB's own baked albedo carries the detail; the per-band
    // autumn colour rides on top as a vertex-colour tint.
    private static readonly string[] LeafModels = { "leaf_a", "leaf_b", "leaf_c" };

    private void BuildLeaves(float radius)
    {
        for (int band = 0; band < Leaves.Length; band++)
        {
            var col = Leaves[band];
            string model = LeafModels[band % LeafModels.Length];
            var mesh = PropGlb.GetMesh(model);
            if (mesh == null) continue;   // missing GLB — skip the band rather than fall back to a card

            var lp = new GpuParticles3D { Amount = 34, Lifetime = 4.5, Position = new Vector3(0, 0.5f, 0) };
            lp.ProcessMaterial = new ParticleProcessMaterial {
                EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Ring, EmissionRingRadius = radius * 0.85f, EmissionRingInnerRadius = radius * 0.35f, EmissionRingHeight = 1.2f, EmissionRingAxis = new Vector3(0, 1, 0),
                Direction = new Vector3(0, 0.2f, 0), Spread = 30f, InitialVelocityMin = 1f, InitialVelocityMax = 3f,
                Gravity = new Vector3(0, 0.4f, 0), TangentialAccelMin = 9f, TangentialAccelMax = 15f, RadialAccelMin = -1f, RadialAccelMax = -3f,   // orbit the funnel
                AngularVelocityMin = -260f, AngularVelocityMax = 260f,   // tumble end over end
                // the leaf mesh is baked to 1.0 unit across its LARGEST axis, so scale here IS the leaf's width in metres
                ScaleMin = 0.20f, ScaleMax = 0.42f, Color = col };
            lp.DrawPass1 = mesh;
            lp.MaterialOverride = LeafMat(model, col);
            AddChild(lp);
        }
    }

    // the authored leaf's own baked texture, lit normally, tinted per-particle by the band colour (VertexColorUseAsAlbedo
    // is what lets ParticleProcessMaterial.Color reach it). Double-sided: a tumbling leaf shows both faces.
    private static readonly Dictionary<string, StandardMaterial3D> _leafMats = new();
    private static StandardMaterial3D LeafMat(string model, Color tint)
    {
        if (_leafMats.TryGetValue(model, out var cached)) return cached;
        var m = new StandardMaterial3D {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            VertexColorUseAsAlbedo = true,
            Roughness = 0.9f,
            EmissionEnabled = true, Emission = tint, EmissionEnergyMultiplier = 0.35f,   // faint underlight so they read in the gloom
        };
        // reuse the albedo the PropGlb loader already baked out of the GLB rather than re-reading the file
        if (PropGlb.Mat(model)?.GetShaderParameter("albedo_tex").As<Texture2D>() is Texture2D tex) m.AlbedoTexture = tex;
        _leafMats[model] = m;
        return m;
    }

    // procedural tornado material — fbm noise scrolled UP + swirled AROUND the funnel, wispy alpha, green→purple by height,
    // fresnel edge glow, an inner dark core. `speed` = how fast this layer whips; `dark` = the debris throat variant.
    private static Shader _tornadoShader;
    private ShaderMaterial TornadoMat(float speed, float dens, float alpha, bool dark = false)
    {
        _tornadoShader ??= new Shader { Code = TornadoCode };
        var m = new ShaderMaterial { Shader = _tornadoShader };
        m.SetShaderParameter("spin", speed);
        m.SetShaderParameter("dens", dens);
        m.SetShaderParameter("alpha_mul", alpha);
        m.SetShaderParameter("col_lo", dark ? new Color(0.10f, 0.20f, 0.10f) : Green);
        m.SetShaderParameter("col_hi", dark ? new Color(0.14f, 0.06f, 0.20f) : Purple);
        m.SetShaderParameter("darkcore", dark ? 1f : 0f);
        return m;
    }
    private const string TornadoCode = @"
shader_type spatial;
render_mode cull_disabled, unshaded, blend_add, depth_draw_never, shadows_disabled;
uniform float spin = 1.0;
uniform float dens = 0.5;
uniform float alpha_mul = 1.0;
uniform float darkcore = 0.0;
uniform vec3 col_lo : source_color = vec3(0.36,1.0,0.42);
uniform vec3 col_hi : source_color = vec3(0.58,0.24,0.86);
float h21(vec2 p){ return fract(sin(dot(p,vec2(41.3,289.1)))*43758.5453); }
float vn(vec2 p){ vec2 i=floor(p),f=fract(p); f=f*f*(3.0-2.0*f);
    return mix(mix(h21(i),h21(i+vec2(1,0)),f.x),mix(h21(i+vec2(0,1)),h21(i+vec2(1,1)),f.x),f.y); }
float fbm(vec2 p){ float v=0.0,a=0.5; for(int i=0;i<4;i++){ v+=a*vn(p); p=p*2.03+1.7; a*=0.5; } return v; }
varying vec2 uv;
varying vec3 vn3;
varying vec3 vv;
void vertex(){ uv = UV; vn3 = NORMAL; vv = (MODEL_MATRIX*vec4(VERTEX,1.0)).xyz; }
void fragment(){
    float t = TIME;
    // swirl: scroll horizontally (around) fast + a twist that increases toward the base (uv.y=0), and rise upward
    float twist = (1.0 - uv.y) * 2.2;
    float ang = uv.x*7.0 + t*spin*(1.4 + twist) + uv.y*3.5;
    float rise = uv.y*5.0 - t*spin*1.6;
    float band = fbm(vec2(ang, rise));
    float fine = fbm(vec2(ang*2.3 + t*spin, rise*2.2 - t*spin*2.0));
    float d = band*0.6 + fine*0.4;
    // vertical profile: wispy/thin at the very top, densest in the churning lower third, tapering to the throat
    float prof = smoothstep(0.0,0.12,uv.y) * (1.0 - smoothstep(0.62,1.0,uv.y));
    prof = mix(prof, prof*1.3, 1.0-uv.y);
    float a = smoothstep(0.62 - dens*0.35, 0.95, d) * prof;
    // fresnel: brighten the silhouette edges so the funnel has volume
    float fres = pow(1.0 - abs(dot(normalize(vn3), normalize(vv - CAMERA_POSITION_WORLD))), 2.5);
    vec3 col = mix(col_lo, col_hi, uv.y);
    col += fres * mix(col_hi, vec3(1.0), 0.3) * 0.5;
    if (darkcore > 0.5) col *= 0.5;   // debris throat is murkier
    ALBEDO = col * (0.5 + d);
    ALPHA = clamp(a * alpha_mul, 0.0, 1.0);
}
";

    // A billboarded puff with a SOFT radial edge — alpha falls to zero well before the quad's border, so there is no
    // visible square no matter how close it gets to the camera. Also fades as it nears the near plane, so a wisp drifting
    // through your face dims instead of whiting out the frame.
    private static ShaderMaterial _wispMat;
    private static ShaderMaterial WispMat()
    {
        if (_wispMat != null) return _wispMat;
        _wispMat = new ShaderMaterial { Shader = new Shader { Code = WispCode } };
        return _wispMat;
    }
    // A thunderhead puff whose EDGES dissolve: alpha is driven by how face-on the surface is, so the silhouette fades
    // into the sky instead of ending at a hard polygon boundary. Without this the cloud deck reads as a pile of slabs.
    private static Shader _cloudShader;
    private static ShaderMaterial CloudMat(Color tint)
    {
        _cloudShader ??= new Shader { Code = CloudCode };
        var m = new ShaderMaterial { Shader = _cloudShader };
        m.SetShaderParameter("tint", tint);
        return m;
    }
    private const string CloudCode = @"
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_never, shadows_disabled, blend_mix;
uniform vec3 tint : source_color = vec3(0.16, 0.13, 0.22);
varying vec3 wn;
varying vec3 wp;
void vertex(){ wn = NORMAL; wp = (MODEL_MATRIX * vec4(VERTEX,1.0)).xyz; }
void fragment(){
    // face-on = solid, edge-on = gone. Squares the falloff so the rim is a long soft gradient, not a rolled edge.
    float facing = abs(dot(normalize(wn), normalize(wp - CAMERA_POSITION_WORLD)));
    float a = pow(smoothstep(0.0, 0.75, facing), 1.6);
    ALBEDO = tint;
    ALPHA = a * 0.72;
}
";

    private const string WispCode = @"
shader_type spatial;
// NOTE: no 'billboard_keep_scale' render mode — that is a BaseMaterial3D BillboardMode, NOT a shader render_mode, and
// naming it here fails the whole compile. Godot then falls back to an untextured white material, i.e. exactly the hard
// white CARDS this shader exists to remove. vertex() below does the billboarding by hand, so no render mode is needed.
render_mode blend_add, unshaded, cull_disabled, depth_draw_never, shadows_disabled;
uniform vec3 tint : source_color = vec3(0.66, 0.92, 0.78);
varying float cam_fade;
void vertex(){
    // billboard toward the camera, preserving scale
    MODELVIEW_MATRIX = VIEW_MATRIX * mat4(INV_VIEW_MATRIX[0], INV_VIEW_MATRIX[1], INV_VIEW_MATRIX[2], MODEL_MATRIX[3]);
    MODELVIEW_MATRIX = MODELVIEW_MATRIX * mat4(vec4(length(MODEL_MATRIX[0].xyz),0,0,0), vec4(0,length(MODEL_MATRIX[1].xyz),0,0), vec4(0,0,length(MODEL_MATRIX[2].xyz),0), vec4(0,0,0,1));
    cam_fade = clamp((-(MODELVIEW_MATRIX * vec4(VERTEX,1.0)).z - 0.6) / 2.4, 0.0, 1.0);   // dim right in front of the lens
}
void fragment(){
    float d = length(UV - vec2(0.5));
    float a = smoothstep(0.5, 0.06, d);        // soft round falloff — kills the quad silhouette
    a *= a;                                     // tighter core, wispier skirt
    ALBEDO = tint * COLOR.rgb;
    ALPHA = a * COLOR.a * cam_fade * 0.55;
}
";

    private StandardMaterial3D Glow(Color c, float alpha, float energy, bool billboard = false, bool mix = false)
    {
        var m = new StandardMaterial3D {
            AlbedoColor = new Color(c.R, c.G, c.B, alpha),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = mix ? BaseMaterial3D.BlendModeEnum.Mix : BaseMaterial3D.BlendModeEnum.Add,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            EmissionEnabled = true, Emission = c, EmissionEnergyMultiplier = energy };
        if (billboard) m.BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled;
        return m;
    }

    // ---- the phantoms ------------------------------------------------------------------------------------------
    // One LATHED mesh, not a snowman of spheres: a rounded crown pinches into a neck, billows out at the shoulders,
    // then falls away into a robe that FRAYS — each vertical strip of cloth tears off at its own height, so the hem is
    // ragged instead of a closed hemisphere. Two trailing sleeve-tendrils drift behind it. The radius carries a
    // low-frequency fold pattern that twists as it descends, so the silhouette is cloth, never a capsule.
    // Built once and shared by all 8 phantoms (they differ by transform + fade, so one mesh is plenty).
    private static ArrayMesh _wraithMesh;
    private const int WSeg = 20, WRings = 16;
    private static readonly Vector3 WTop = new Vector3(0f, 1.05f, 0f);

    // Profile tuned so the ROBE is the silhouette. The first attempt flared hard at the shoulders and then tapered all
    // the way to a point, which — once the strips frayed — read as a pointed hat on stilts rather than a figure. The
    // body now stays nearly full-width down to 78% of its drop and only narrows at the very hem.
    private static float WraithRadius(float t, float ang)
    {
        float r;
        if (t < 0.14f)      r = Mathf.Sin(t / 0.14f * Mathf.Pi * 0.5f) * 0.46f;         // crown of the cowl
        else if (t < 0.26f) r = Mathf.Lerp(0.46f, 0.38f, (t - 0.14f) / 0.12f);          // neck
        else if (t < 0.44f) r = Mathf.Lerp(0.38f, 0.80f, (t - 0.26f) / 0.18f);          // shoulders
        else if (t < 0.78f) r = Mathf.Lerp(0.80f, 0.66f, (t - 0.44f) / 0.34f);          // the robe — held full, this IS the shape
        else                r = Mathf.Lerp(0.66f, 0.20f, (t - 0.78f) / 0.22f);          // hem draws in as it frays
        return r * (1f + 0.13f * Mathf.Sin(ang * 3f + t * 6.5f) + 0.05f * Mathf.Sin(ang * 5f - t * 3f));
    }
    private static float WraithY(float t) => Mathf.Lerp(WTop.Y, -2.45f, t);
    private static Vector3 WraithP(int ring, int seg)
    {
        float t = ring / (float)WRings, ang = seg * Mathf.Tau / WSeg;
        float r = WraithRadius(t, ang);
        return new Vector3(Mathf.Cos(ang) * r, WraithY(t), Mathf.Sin(ang) * r);
    }

    private static ArrayMesh WraithMesh()
    {
        if (_wraithMesh != null) return _wraithMesh;
        var rng = new RandomNumberGenerator { Seed = 90210 };
        var tear = new int[WSeg];   // how far down each strip of cloth survives before it frays away
        // only the LAST fifth of each strip is allowed to tear away — enough for a ragged hem, not enough to eat the robe
        for (int s = 0; s < WSeg; s++) tear[s] = Mathf.RoundToInt((0.80f + rng.Randf() * 0.20f) * WRings);

        var st = new SurfaceTool(); st.Begin(Mesh.PrimitiveType.Triangles);
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        { st.AddVertex(a); st.AddVertex(b); st.AddVertex(c); st.AddVertex(a); st.AddVertex(c); st.AddVertex(d); }

        for (int s = 0; s < WSeg; s++)
        {
            int s2 = (s + 1) % WSeg;
            int lim = Mathf.Min(tear[s], tear[s2]);   // a quad only exists where BOTH its strips are still intact
            for (int ring = 0; ring < lim; ring++)
                Quad(WraithP(ring, s), WraithP(ring, s2), WraithP(ring + 1, s2), WraithP(ring + 1, s));
        }

        // NO arms. Two attempts at sleeve/tendril tubes both read as stuck-on primitives — long ones looked like splayed
        // legs, short ones like triangular spikes. The robe silhouette alone is stronger, and a wraith with no visible
        // limbs is the more unsettling read anyway. Don't add them back.

        st.GenerateNormals();
        _wraithMesh = st.Commit();
        return _wraithMesh;
    }

    private Ghost MakeGhost(int i)
    {
        var node = new Node3D(); _vortex.AddChild(node);
        var parts = new List<MeshInstance3D>();
        // per-phantom material instances: _Process fades each part by writing its own AlbedoColor.A
        var body = new MeshInstance3D {
            Mesh = WraithMesh(),
            MaterialOverride = Glow(new Color(0.72f, 0.88f, 1f), 0.5f, 1.5f),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        node.AddChild(body); parts.Add(body);

        // narrowed SLITS angled into a hostile brow, in the Haunt's own sickly green — a pair of glowing eyeballs read
        // as a cartoon face; slits read as something looking at you
        var eyeMat = Glow(new Color(0.38f, 1.0f, 0.46f), 0.95f, 3.4f);
        foreach (float ex in new[] { -0.17f, 0.17f })
        {
            var eye = new MeshInstance3D {
                Mesh = new BoxMesh { Size = new Vector3(0.26f, 0.075f, 0.04f) },
                MaterialOverride = eyeMat,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Position = new Vector3(ex, 0.44f, 0.46f),   // just proud of the cowl (body radius there is ~0.43)
                Rotation = new Vector3(0f, 0f, ex < 0f ? -0.30f : 0.30f) };
            node.AddChild(eye); parts.Add(eye);
        }

        var g = new Ghost { Node = node, Parts = parts.ToArray(), BaseA = new float[parts.Count], Seed = i * 1.7f, MaxLife = 3.2f + i * 0.3f, Life = i * (3.2f / GhostN), Ang = i * Mathf.Tau / GhostN, AngSpd = 0.8f + (i % 3) * 0.22f };
        for (int k = 0; k < parts.Count; k++) g.BaseA[k] = ((StandardMaterial3D)parts[k].MaterialOverride).AlbedoColor.A;
        return g;
    }

    // (HARNESS) stop the phantoms orbiting up the funnel and line them up at a local anchor at full opacity, so a
    // scenario can frame them close enough to actually judge the silhouette. Inert in the shipping game.
    public bool DebugStageGhosts = false;
    public Vector3 DebugGhostAnchor = Vector3.Zero;
    public int GhostCount => _ghosts.Count;

    public void SetFill(float f)
    {
        f = Mathf.Clamp(f, 0f, 1f);
        if (_rim?.MaterialOverride is StandardMaterial3D rm) rm.EmissionEnergyMultiplier = 2.2f + 2.4f * f;
        if (_greenCore != null) _greenCore.LightEnergy = 1.8f + 1.6f * f;
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta; _t += dt;
        if (_rim != null) _rim.Rotation = new Vector3(0, _t * 0.6f, 0);
        if (_disc != null) { float s = 1f + 0.02f * Mathf.Sin(_t * 2f); _disc.Scale = new Vector3(s, 1f, s); }
        if (_vortex != null) _vortex.Rotation = new Vector3(0, _t * 0.95f, 0);
        if (_sky != null) _sky.Rotation = new Vector3(0, _t * 0.28f, 0);
        if (_greenCore != null) _greenCore.LightEnergy = Mathf.Max(_greenCore.LightEnergy, 0f) * 0.9f + (2.0f + 0.6f * Mathf.Sin(_t * 3f)) * 0.1f;

        // drive the local wind globals so foliage in the zone howls (bend² weighting = upper branches whip most)
        if (!Remote)
        {
            RenderingServer.GlobalShaderParameterSet("haunt_pos", GlobalPosition);
            RenderingServer.GlobalShaderParameterSet("haunt_rad", Radius);
            RenderingServer.GlobalShaderParameterSet("haunt_gust", 0.9f + 0.5f * Mathf.Sin(_t * 1.3f) + 0.2f * Mathf.Sin(_t * 4.7f));
        }

        // lightning: fade the flash, and fork a new bolt every few seconds
        if (_flash != null && _flash.LightEnergy > 0.05f) _flash.LightEnergy *= Mathf.Pow(0.02f, dt);   // sharp decay
        // The fork is a FLASH, not scenery. The geometry used to be left standing until the next Strike() rebuilt it,
        // i.e. a bolt frozen in the sky for 2.4-6.4s — which nobody saw only because the fork was being drawn at the
        // world origin (see the LookAtFromPosition fix). Now that it's in the right place it has to behave like lightning.
        _boltAge += dt;
        if (_bolt != null) _bolt.Visible = _boltAge < 0.20f && !(_boltAge > 0.085f && _boltAge < 0.115f);
        _lightT -= dt;
        if (_lightT <= 0f) { _lightT = 2.4f + GD.Randf() * 4f; Strike(); }

        if (DebugStageGhosts)   // (HARNESS) a static, fully-lit lineup instead of the orbit
        {
            // the phantoms hang under _vortex, which SPINS — a local anchor set under it gets swung around the zone
            // (and a far-off haunt centre turns that into a huge arc). Freeze the spin while staging.
            _vortex.Rotation = Vector3.Zero;
            // turn each one to actually LOOK at the witch — the haunt's yaw is arbitrary relative to the camera, so a
            // fixed rotation just pointed the faces (and the eye slits) somewhere off-frame
            Vector3 camLocal = (Game.I?.Player != null ? Game.I.Player.GlobalPosition : GlobalPosition) - GlobalPosition;
            for (int i = 0; i < _ghosts.Count; i++)
            {
                var dg = _ghosts[i];
                dg.Node.Position = DebugGhostAnchor + new Vector3((i - (_ghosts.Count - 1) * 0.5f) * 3.2f, 0f, 0f);
                Vector3 look = camLocal - dg.Node.Position; look.Y = 0f;
                dg.Node.Rotation = new Vector3(0f, Mathf.Atan2(look.X, look.Z), 0f);   // +Z (the face) toward the camera
                dg.Node.Scale = Vector3.One;
                for (int k = 0; k < dg.Parts.Length; k++)
                    if (dg.Parts[k].MaterialOverride is StandardMaterial3D dm)
                    { var dc = dm.AlbedoColor; dm.AlbedoColor = new Color(dc.R, dc.G, dc.B, dg.BaseA[k]); }
            }
            return;
        }

        // phantoms swirl up the funnel, fading in then out
        foreach (var g in _ghosts)
        {
            g.Life += dt;
            if (g.Life >= g.MaxLife) { g.Life = 0f; g.Ang = (g.Ang + 2.3999632f) % Mathf.Tau; }
            float u = g.Life / g.MaxLife;
            float h = u * FunnelH;
            float fr = Mathf.Lerp(Radius * 0.12f, Radius * 0.68f, Mathf.Pow(u, 0.85f));
            g.Ang += g.AngSpd * dt;
            var pos = new Vector3(Mathf.Cos(g.Ang) * fr, 1.0f + h + Mathf.Sin(_t * 1.7f + g.Seed) * 0.4f, Mathf.Sin(g.Ang) * fr);
            g.Node.Position = pos;
            g.Node.Rotation = new Vector3(0, -g.Ang + Mathf.Pi * 0.5f, Mathf.Sin(_t + g.Seed) * 0.18f);
            float grow = Mathf.Lerp(0.5f, 1.25f, u);
            g.Node.Scale = new Vector3(grow, grow, grow);
            float fade = Mathf.Clamp(Mathf.Sin(u * Mathf.Pi) * (0.6f + 0.4f * Mathf.Sin(_t * 6f + g.Seed)), 0f, 1f);
            for (int k = 0; k < g.Parts.Length; k++)
                if (g.Parts[k].MaterialOverride is StandardMaterial3D pm)
                { var c = pm.AlbedoColor; pm.AlbedoColor = new Color(c.R, c.G, c.B, g.BaseA[k] * fade); }
        }
    }

    public override void _ExitTree()
    {
        if (!Remote)
        {
            RenderingServer.GlobalShaderParameterSet("haunt_rad", 0f);   // stop the wind when the zone is gone
        }
    }
}
