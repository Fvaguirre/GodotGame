using Godot;

// Fireball.cs — the Fireball finisher projectile. Travels at a medium speed toward the cast direction, trailing fire. On a
// DIRECT enemy hit it deals heavy damage to that foe, plus a medium blast to everything in BlastRadius; on ground/timeout it
// just blasts. Host/caster owns damage; allies spawn a Remote visual-only ghost (VFX kind 73) that bursts cosmetically.
public partial class Fireball : Node3D
{
    public Vector3 Dir;
    public float Speed = 22f, DirectDmg = 30f, BlastDmg = 15f, BlastRadius = 4.5f, BurnPer = 4f, BurnBomb = 30f;
    public int OwnerPeer = 0;
    public int Cataclysm = 0;   // (OVERHAUL) Fireball evolution: lingering ember field on impact, scales with stacks
    public Player Src;
    public bool Remote = false;

    private float _life = 3f;
    private bool _done = false;
    private OmniLight3D _light;
    private float _flick = 0f;

    // shared painterly flame material (one instance for every fireball — no per-cast allocation)
    private static ShaderMaterial _flameMat;
    private static ShaderMaterial FlameMat()
    {
        if (_flameMat != null) return _flameMat;
        var m = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/flame.gdshader") };
        m.SetShaderParameter("hot_color", new Color(1f, 0.95f, 0.6f));
        m.SetShaderParameter("mid_color", new Color(1f, 0.45f, 0.1f));
        m.SetShaderParameter("cool_color", new Color(0.6f, 0.08f, 0.02f));
        m.SetShaderParameter("half_len", 0.42f);
        _flameMat = m; return m;
    }

    public override void _Ready()
    {
        var col = DamageTypes.Col(DamageType.Ember);
        // (PHASE 3) authored COMET silhouette: an elongated teardrop of painterly flame (not a glowing sphere) + a dense hot core.
        var flame = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 0.42f, Height = 0.84f, RadialSegments = 12, Rings = 9 },
            Scale = new Vector3(0.66f, 0.66f, 1.9f),   // stretch into a teardrop along local Z (the travel axis)
            MaterialOverride = FlameMat()
        };
        AddChild(flame);
        AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.2f, Height = 0.4f, RadialSegments = 8, Rings = 6 }, MaterialOverride = Game.Emissive(new Color(1f, 0.92f, 0.7f), 3.5f) });
        _light = new OmniLight3D { OmniRange = 6f, LightColor = col, LightEnergy = 2.5f };
        AddChild(_light);
        AddChild(Fx.Trail(new Color(1f, 0.55f, 0.2f), 0.3f, 22, 0.5f, 1.2f));   // (PHASE 3) soft round ember trail (right for fire, unlike sharp sparks)
        Dir = Dir.LengthSquared() > 0.001f ? Dir.Normalized() : Vector3.Forward;
    }

    public override void _Process(double delta)
    {
        var g = Game.I; if (g == null || !g.SimActive) return;
        float dt = (float)delta;
        GlobalPosition += Dir * Speed * dt;
        // orient the comet so its hot head (-Z) leads the travel direction, tail flaming behind
        Vector3 up = Mathf.Abs(Dir.Y) > 0.95f ? Vector3.Forward : Vector3.Up;
        LookAt(GlobalPosition + Dir, up);
        _flick += dt * 22f;   // flickering firelight
        if (_light != null) _light.LightEnergy = 2.2f + Mathf.Sin(_flick) * 0.5f;
        _life -= dt;
        g.SpawnFlameCone(GlobalPosition, -Dir, 1f, DamageTypes.Col(DamageType.Ember));   // fiery trail

        Enemy hit = null;
        foreach (var e in g.Enemies.ToArray())
            if (e != null && !e.Dead && GodotObject.IsInstanceValid(e) && e.HitBy(GlobalPosition, 0.7f)) { hit = e; break; }   // (FIX) full-body capsule — was a sphere at mid-body, so it flew over a tall boss's head
        float gy = g.SurfaceHeight(GlobalPosition, GlobalPosition.Y);
        if (hit != null || GlobalPosition.Y <= gy + 0.2f || _life <= 0f) Explode(hit);
    }

    private void Explode(Enemy direct)
    {
        if (_done) return; _done = true;
        var g = Game.I; var col = DamageTypes.Col(DamageType.Ember);
        Vector3 at = GlobalPosition;
        if (!Remote)   // host/caster owns the damage
        {
            if (direct != null && GodotObject.IsInstanceValid(direct) && !direct.Dead)
            {
                direct.Hurt(DirectDmg, DamageType.Ember, true);
                direct.AddBurn(2f, BurnPer, BurnBomb, 0f, OwnerPeer);
                if (Src != null && GodotObject.IsInstanceValid(Src)) Src.OnHitDirect(direct, direct.Dead, DirectDmg, DamageType.Ember);
            }
            foreach (var e in g.Enemies.ToArray())
            {
                if (e == null || e.Dead || e == direct || !GodotObject.IsInstanceValid(e)) continue;
                if (new Vector2(e.GlobalPosition.X - at.X, e.GlobalPosition.Z - at.Z).Length() < BlastRadius + e.Radius)
                { e.Hurt(BlastDmg, DamageType.Ember, true); e.AddBurn(1f, BurnPer, BurnBomb, 0f, OwnerPeer); }
            }
            g.DamageWorld(at, BlastRadius, BlastDmg);
            if (Cataclysm > 0)   // Evo B Cataclysm: leave a lingering re-igniting ember field
            {
                var field = new GroundField { Type = FieldType.Hex, DType = DamageType.Ember, Radius = BlastRadius * 0.7f,
                    Dur = 3f + Cataclysm * 1.2f, Power = BurnPer * 4f * (1f + 0.3f * Cataclysm), TintColor = DamageTypes.Col(DamageType.Ember),
                    BurnAdd = 1f, BurnPer = BurnPer, BurnBomb = BurnBomb, BurnOwner = OwnerPeer, Src = Src };
                g.AddChild(field); field.GlobalPosition = new Vector3(at.X, 0.05f, at.Z);
            }
        }
        g.SpawnEmberBurst(at, BlastRadius, false);   // each machine (real or ghost) shows its own blast
        g.VfxRing(at, col, BlastRadius * 1.2f, 0.4f);
        g.Sfx?.ModEmber(at, false); g.Sfx?.Thunder();
        QueueFree();
    }
}
