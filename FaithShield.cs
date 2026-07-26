using Godot;

// FaithShield.cs — the Divine 'Faith Shield' ultimate: a dome of holy light rooted where cast. Allies pass through and
// shoot out freely; enemies are kept OUT and can NOT break it — it simply lasts its full duration, then SHATTERS like glass:
// a burst of holy shards + flat damage + a hard knockback to everything nearby. Duration is fixed (10s base); the radius
// scales with spell-area/range cards. MP: the authoritative copy (!Remote, on the host) blocks enemies and deals the shatter;
// Remote copies on other machines are visual-only, so everyone sees the dome and the shatter.
public partial class FaithShield : Node3D
{
    public float Radius = 6f;
    public float Dur = 10f, DurMax = 10f;
    public bool Remote = false;                 // visual-only copy on a non-authoritative machine
    public float MeleeDmg = 6f;                 // gentle sear on foes pressed against it
    public float HealPerSec = 6f;               // heal to the local warden standing inside
    public float BurstDmg = 60f;                // flat shatter damage
    public float BurstRadius = 13f;
    public float Knock = 16f;                   // shatter knockback power
    public bool Reflect = false;                // Aegis Sanctum ult-mod: bigger heal + harder shatter

    private MeshInstance3D _dome;
    private ShaderMaterial _mat;
    private Node3D _ring;
    private float _dmgCd = 0f, _pulse = 0f, _flash = 0f;
    private bool _shattered = false;

    // A holy force-field shader: a drifting hexagonal energy lattice, a bright fresnel rim (the dome glows at grazing
    // angles), and a radiant pulse that sweeps up the surface. `energy` is driven from _Process for shimmer/flash/fade.
    private const string ShieldShader = @"
shader_type spatial;
render_mode cull_disabled, unshaded, depth_draw_never;
uniform vec3 tint : source_color = vec3(1.0, 0.94, 0.7);
uniform float energy = 1.0;
float hexEdge(vec2 p) { p = abs(p); return max(dot(p, normalize(vec2(1.0, 1.732))), p.x); }
void fragment() {
    float fres = pow(1.0 - abs(dot(normalize(NORMAL), normalize(VIEW))), 2.2);
    vec2 uv = UV * 26.0; uv.y += TIME * 0.2;
    vec2 r = vec2(1.0, 1.732), h = r * 0.5;
    vec2 a = mod(uv, r) - h;
    vec2 b = mod(uv - h, r) - h;
    vec2 gv = dot(a, a) < dot(b, b) ? a : b;
    float cell = smoothstep(0.40, 0.50, hexEdge(gv));      // bright hex-cell borders
    float sweep = 0.5 + 0.5 * sin(UV.y * 10.0 - TIME * 2.5);
    float glow = fres * 1.6 + cell * 0.8 + sweep * 0.15;
    ALBEDO = tint;
    EMISSION = tint * (0.3 + glow) * energy;
    ALPHA = clamp((0.10 + fres * 0.6 + cell * 0.30 + sweep * 0.04) * energy, 0.0, 0.92);
}";

    public override void _Ready()
    {
        var col = DamageTypes.Col(DamageType.Holy);
        var tint = new Color(col.R, col.G, col.B).Lerp(new Color(1f, 0.96f, 0.8f), 0.45f);   // holy, warmed toward radiant white
        _mat = new ShaderMaterial { Shader = new Shader { Code = ShieldShader } };
        _mat.SetShaderParameter("tint", new Vector3(tint.R, tint.G, tint.B));
        _mat.SetShaderParameter("energy", 1.0f);

        _dome = new MeshInstance3D { Mesh = new SphereMesh { Radius = Radius, Height = Radius * 2f }, MaterialOverride = _mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_dome);
        // a faint outer shell a touch larger — parallax gives the barrier real depth instead of one thin skin
        var outer = new MeshInstance3D { Mesh = new SphereMesh { Radius = Radius * 1.05f, Height = Radius * 2.1f }, MaterialOverride = _mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(outer);
        // a radiant rune band around the base that slowly turns
        _ring = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = Radius * 0.93f, OuterRadius = Radius * 1.03f }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(tint.R, tint.G, tint.B, 0.5f), EmissionEnabled = true, Emission = tint, EmissionEnergyMultiplier = 2.4f, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, CullMode = BaseMaterial3D.CullModeEnum.Disabled } };
        ((Node3D)_ring).Position = new Vector3(0, 0.12f, 0);
        AddChild(_ring);
        AddChild(new OmniLight3D { OmniRange = Radius * 2.4f, LightColor = col, LightEnergy = 2.0f, Position = new Vector3(0, Radius * 0.4f, 0) });
    }

    // an enemy projectile splashed against the dome — a flash, but it can't chip it (unbreakable)
    public void Hit(float dmg) { _flash = 0.25f; }

    public override void _Process(double delta)
    {
        var g = Game.I;
        if (g == null || !g.SimActive) return;
        float dt = (float)delta;
        _pulse += dt; if (_flash > 0f) _flash -= dt;
        Dur -= dt;

        if (!Remote)   // authoritative (host): keep enemies OUT — they can't get in and can't break it
        {
            _dmgCd -= dt; bool tick = _dmgCd <= 0f; if (tick) _dmgCd = 0.25f;
            foreach (var e in g.Enemies.ToArray())
            {
                if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
                var off = new Vector2(e.GlobalPosition.X - GlobalPosition.X, e.GlobalPosition.Z - GlobalPosition.Z);
                float d = off.Length(); float rim = Radius + e.Radius;
                if (d < rim)
                {
                    float k = rim / Mathf.Max(d, 0.001f);
                    e.GlobalPosition = new Vector3(GlobalPosition.X + off.X * k, e.GlobalPosition.Y, GlobalPosition.Z + off.Y * k);
                    if (tick) e.Hurt((Reflect ? MeleeDmg * 1.8f : MeleeDmg) * 0.25f, DamageType.Holy, false);
                    _flash = 0.14f;
                }
            }
        }

        // heal the LOCAL warden standing inside (each machine mends its own player)
        if (g.Player != null)
        {
            var po = new Vector2(g.Player.GlobalPosition.X - GlobalPosition.X, g.Player.GlobalPosition.Z - GlobalPosition.Z);
            if (po.Length() < Radius) g.Player.Heal(HealPerSec * dt * (Reflect ? 1.3f : 1f));
        }

        if (_mat != null)   // shimmer + flash-on-hit + dim as it nears the end, all via the shader's energy uniform
        {
            float fade = Mathf.Clamp(Dur / 1.2f, 0.4f, 1f);
            float e = (0.9f + 0.12f * Mathf.Sin(_pulse * 4f) + _flash * 2.5f) * fade;
            _mat.SetShaderParameter("energy", e);
        }
        if (_ring != null && GodotObject.IsInstanceValid(_ring)) _ring.Rotation = new Vector3(0, _pulse * 0.6f, 0);   // the rune band turns

        if (Dur <= 0f && !_shattered)
        {
            _shattered = true;
            Shatter();
            bool wasMine = g.Shield == this;   // the LOCAL player's own shield (not another player's shadow copy)
            if (wasMine) { g.Shield = null; if (g.Player != null) g.Player.OnShieldEnded(); }
            QueueFree();
        }
    }

    // it lasts its whole life, then SHATTERS: holy glass shards fly out, and (authoritative only) foes nearby take flat
    // damage and get flung back.
    private void Shatter()
    {
        var g = Game.I; if (g == null) return;
        var col = DamageTypes.Col(DamageType.Holy);

        if (!Remote)   // damage + knockback only on the authoritative copy (host)
        {
            foreach (var e in g.Enemies.ToArray())
            {
                if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
                var off = new Vector2(e.GlobalPosition.X - GlobalPosition.X, e.GlobalPosition.Z - GlobalPosition.Z);
                if (off.Length() < BurstRadius + e.Radius) { e.Hurt(BurstDmg * (Reflect ? 1.4f : 1f), DamageType.Holy, true); e.Knockback(GlobalPosition, Knock); }
            }
            g.DamageWorld(GlobalPosition, BurstRadius, BurstDmg);
        }

        // --- glass shatter VFX (every machine sees this) ---
        var glass = new StandardMaterial3D { AlbedoColor = new Color(col.R, col.G, col.B, 0.72f), EmissionEnabled = true, Emission = col, EmissionEnergyMultiplier = 2.4f, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
        int shards = Mathf.Max(10, (int)(22 * (Game.I.ParticleScale)));
        for (int i = 0; i < shards; i++)
        {
            float a = i / (float)shards * Mathf.Tau + GD.Randf() * 0.35f;
            float el = GD.Randf() * 1.1f;
            var outw = new Vector3(Mathf.Cos(a) * Mathf.Cos(el), Mathf.Sin(el) * 0.7f, Mathf.Sin(a) * Mathf.Cos(el));
            var shard = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.35f + GD.Randf() * 0.5f, 0.55f + GD.Randf() * 0.7f, 0.05f) }, MaterialOverride = glass };
            g.AddChild(shard);
            var start = GlobalPosition + outw * (Radius * 0.9f) + Vector3.Up * (Radius * 0.3f);
            shard.GlobalPosition = start;
            shard.Rotation = new Vector3(GD.Randf() * 6.28f, GD.Randf() * 6.28f, GD.Randf() * 6.28f);
            var end = start + outw * (3f + GD.Randf() * 4.5f) + Vector3.Down * (2.5f + GD.Randf() * 3.5f);   // burst out, then fall
            var tw = shard.CreateTween(); tw.SetParallel(true);
            tw.TweenProperty(shard, "global_position", end, 0.95f).SetEase(Tween.EaseType.Out);
            tw.TweenProperty(shard, "rotation", shard.Rotation + new Vector3(GD.Randf() * 9f, GD.Randf() * 9f, GD.Randf() * 9f), 0.95f);
            tw.TweenProperty(shard, "transparency", 1f, 0.95f);
            tw.SetParallel(false);
            tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(shard)) shard.QueueFree(); }));
        }
        // a bright collapse flash
        var v = new Vfx(); g.AddChild(v); v.GlobalPosition = new Vector3(GlobalPosition.X, 1f, GlobalPosition.Z);
        v.Init(new SphereMesh { Radius = BurstRadius * 0.4f, Height = BurstRadius * 0.8f }, col, 0.45f, 8f);
        g.Sfx?.Release(DamageType.Holy);
    }
}
