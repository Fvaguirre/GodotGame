using Godot;
using System.Collections.Generic;

// Fx.cs — Phase 3 VFX INFRA. A POOLED GPU-particle spark system so bulk sparks (impacts, deaths, crits) are cheap and
// don't allocate a fresh node per burst. GpuParticles3D nodes live in a small reused pool parented under Game.I; each
// burst rents a finished one, reconfigures it, and re-fires it. One-shot particles auto-clear their Emitting flag when
// done, which is how the pool knows a node is free again. Graceful: if the pool is exhausted, the burst is simply
// skipped (never a crash, never unbounded growth).
public static class Fx
{
    private static Mesh _sparkMesh;
    private static readonly List<GpuParticles3D> _pool = new();
    private const int PoolCap = 24;

    // a soft round glow texture (bright centre → transparent edge) so a spark reads as a defined glowing DOT, not a
    // hard flat square. Modulated by the per-particle COLOR via vertex-colour-as-albedo.
    private static Texture2D _dotTex;
    private static Texture2D DotTex()
    {
        if (_dotTex != null) return _dotTex;
        var grad = new Gradient();
        grad.SetColor(0, new Color(1f, 1f, 1f, 1f));
        grad.SetColor(1, new Color(1f, 1f, 1f, 0f));
        grad.AddPoint(0.55f, new Color(1f, 1f, 1f, 0.55f));   // bright hot core, quick soft falloff
        _dotTex = new GradientTexture2D
        {
            Gradient = grad,
            Fill = GradientTexture2D.FillEnum.Radial,
            FillFrom = new Vector2(0.5f, 0.5f),
            FillTo = new Vector2(0.5f, 1.0f),
            Width = 48,
            Height = 48,
        };
        return _dotTex;
    }

    // a camera-facing additive quad whose colour comes from the per-particle COLOR (vertex-colour-as-albedo)
    private static Mesh SparkMesh()
    {
        if (_sparkMesh != null) return _sparkMesh;
        var q = new QuadMesh { Size = new Vector2(1f, 1f) };
        q.Material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            AlbedoTexture = DotTex(),                 // soft round glow → no square edges (emission omitted: it would ignore the texture mask and paint the whole square)
            VertexColorUseAsAlbedo = true,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
            BillboardKeepScale = true,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        _sparkMesh = q;
        return _sparkMesh;
    }

    // ---- GPU SHARD BURST (experiment, toggled by Game.GpuSparks) -----------------------------------------------------
    // The "proper" GPU take on the impact spark shards: the SAME cone mesh as the mesh version, a solid emissive material,
    // and the AlignYToVelocity particle flag so each cone points down its own fling direction. Pooled + tinted per-particle.
    private static Mesh _shardMesh;
    private static ShaderMaterial _shardMat;
    private static GradientTexture1D _fadeRamp;
    private static readonly List<GpuParticles3D> _shardPool = new();

    private static Mesh ShardMesh()
    {
        if (_shardMesh != null) return _shardMesh;
        _shardMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/spark_particle.gdshader") };
        _shardMat.SetShaderParameter("energy", 2.6f);
        var c = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.25f, Height = 2.0f, RadialSegments = 4 };   // unit cone points +Y
        c.Material = _shardMat;
        _shardMesh = c;
        return _shardMesh;
    }

    private static GradientTexture1D FadeRamp()
    {
        if (_fadeRamp != null) return _fadeRamp;
        var g = new Gradient();
        g.SetColor(0, new Color(1f, 1f, 1f, 1f));
        g.SetColor(1, new Color(1f, 1f, 1f, 0f));   // fade alpha over lifetime
        _fadeRamp = new GradientTexture1D { Gradient = g };
        return _fadeRamp;
    }

    private static GpuParticles3D RentShard()
    {
        foreach (var p in _shardPool)
            if (GodotObject.IsInstanceValid(p) && !p.Emitting) return p;
        if (_shardPool.Count < PoolCap && Game.I != null)
        {
            var np = new GpuParticles3D
            {
                OneShot = true,
                Lifetime = 0.4,
                Explosiveness = 1.0f,
                DrawPass1 = ShardMesh(),
                Emitting = false,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            Game.I.AddChild(np);
            _shardPool.Add(np);
            return np;
        }
        return null;
    }

    // GPU equivalent of the mesh impact-spark shards. `normal` = surface normal (shards spray out of it).
    public static void SparkBurst(Vector3 pos, Vector3 normal, Color tint, float size, int amount)
    {
        if (Game.I == null) return;
        var p = RentShard();
        if (p == null) return;
        Vector3 n = normal.LengthSquared() > 1e-6f ? normal.Normalized() : Vector3.Up;
        p.Amount = Mathf.Max(1, amount);
        var mat = p.ProcessMaterial as ParticleProcessMaterial ?? new ParticleProcessMaterial();
        mat.Direction = new Vector3(0f, 1f, 0f);   // local +Y — the node is oriented so +Y = surface normal
        mat.Spread = 80f;
        mat.InitialVelocityMin = size * 4f;
        mat.InitialVelocityMax = size * 7f;
        mat.Gravity = new Vector3(0f, -4f, 0f);
        mat.DampingMin = 6f; mat.DampingMax = 9f;   // ease-out slowdown (approximates the mesh version's EaseOut tween)
        mat.ScaleMin = size * 0.18f; mat.ScaleMax = size * 0.24f;
        mat.Color = tint.Lerp(Colors.White, 0.3f);
        mat.ColorRamp = FadeRamp();
        mat.SetParticleFlag(ParticleProcessMaterial.ParticleFlags.AlignYToVelocity, true);   // cone points down its velocity
        p.ProcessMaterial = mat;
        // orient the emitter so local +Y points along the surface normal
        Vector3 x = n.Cross(Vector3.Forward); if (x.LengthSquared() < 0.001f) x = n.Cross(Vector3.Right); x = x.Normalized();
        Vector3 z = x.Cross(n).Normalized();
        p.Basis = new Basis(x, n, z);
        p.GlobalPosition = pos;
        p.Restart();
        p.Emitting = true;
    }

    private static GpuParticles3D Rent()
    {
        foreach (var p in _pool)
            if (GodotObject.IsInstanceValid(p) && !p.Emitting) return p;
        if (_pool.Count < PoolCap && Game.I != null)
        {
            var np = new GpuParticles3D
            {
                OneShot = true,
                Lifetime = 0.55,
                Explosiveness = 0.95f,
                DrawPass1 = SparkMesh(),
                Emitting = false,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            Game.I.AddChild(np);
            _pool.Add(np);
            return np;
        }
        return null;   // pool exhausted → skip this burst (graceful)
    }

    // a continuous GPU-particle TRAIL to attach as a child of a moving node (LocalCoords=false → emitted embers stay in
    // world space and stream out behind it). Returns the node; caller does node.AddChild(Fx.Trail(col)). Not pooled — it
    // lives and dies with its owner projectile.
    public static GpuParticles3D Trail(Color col, float size = 0.22f, int amount = 20, float lifetime = 0.5f, float rise = 0.6f)
    {
        var p = new GpuParticles3D
        {
            Amount = Mathf.Max(1, amount),
            Lifetime = lifetime,
            OneShot = false,
            Explosiveness = 0f,
            LocalCoords = false,
            DrawPass1 = SparkMesh(),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Emitting = true,
        };
        p.ProcessMaterial = new ParticleProcessMaterial
        {
            Direction = Vector3.Up,
            Spread = 180f,
            InitialVelocityMin = 0.1f,
            InitialVelocityMax = 0.7f,
            Gravity = new Vector3(0f, rise, 0f),
            ScaleMin = size * 0.4f,
            ScaleMax = size,
            Color = col,
        };
        return p;
    }

    // fire a one-shot spark burst at a world position. `size` ≈ spark quad size, `amount` particles, `speed` fling speed.
    public static void Sparks(Vector3 pos, Color col, float size, int amount, float speed = 6f)
    {
        if (Game.I == null) return;
        var p = Rent();
        if (p == null) return;
        p.Amount = Mathf.Max(1, amount);
        var mat = p.ProcessMaterial as ParticleProcessMaterial ?? new ParticleProcessMaterial();
        mat.Direction = Vector3.Up;
        mat.Spread = 78f;
        mat.InitialVelocityMin = speed * 0.4f;
        mat.InitialVelocityMax = speed;
        mat.Gravity = new Vector3(0f, -14f, 0f);
        mat.ScaleMin = size * 0.4f;
        mat.ScaleMax = size;
        mat.Color = col;
        p.ProcessMaterial = mat;
        p.GlobalPosition = pos;
        p.Restart();
        p.Emitting = true;
    }
}
