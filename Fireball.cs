using Godot;

// Fireball.cs — the Fireball finisher projectile. Travels at a medium speed toward the cast direction, trailing fire. On a
// DIRECT enemy hit it deals heavy damage to that foe, plus a medium blast to everything in BlastRadius; on ground/timeout it
// just blasts. Host/caster owns damage; allies spawn a Remote visual-only ghost (VFX kind 73) that bursts cosmetically.
public partial class Fireball : Node3D
{
    public Vector3 Dir;
    public float Speed = 22f, DirectDmg = 30f, BlastDmg = 15f, BlastRadius = 4.5f, BurnPer = 4f, BurnBomb = 30f;
    public int OwnerPeer = 0;
    public Player Src;
    public bool Remote = false;

    private float _life = 3f;
    private bool _done = false;

    public override void _Ready()
    {
        var col = DamageTypes.Col(DamageType.Ember);
        AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.4f, Height = 0.8f, RadialSegments = 8, Rings = 6 }, MaterialOverride = Game.Emissive(col, 3f) });
        AddChild(new OmniLight3D { OmniRange = 6f, LightColor = col, LightEnergy = 2.5f });
        Dir = Dir.LengthSquared() > 0.001f ? Dir.Normalized() : Vector3.Forward;
    }

    public override void _Process(double delta)
    {
        var g = Game.I; if (g == null || g.State != GameState.Playing) return;
        float dt = (float)delta;
        GlobalPosition += Dir * Speed * dt;
        _life -= dt;
        g.SpawnFlameCone(GlobalPosition, -Dir, 1f, DamageTypes.Col(DamageType.Ember));   // fiery trail

        Enemy hit = null;
        foreach (var e in g.Enemies.ToArray())
            if (e != null && !e.Dead && GodotObject.IsInstanceValid(e) && GlobalPosition.DistanceTo(e.GlobalPosition + Vector3.Up * e.Radius * 0.5f) < 0.7f + e.Radius) { hit = e; break; }
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
        }
        g.SpawnEmberBurst(at, BlastRadius, false);   // each machine (real or ghost) shows its own blast
        g.VfxRing(at, col, BlastRadius * 1.2f, 0.4f);
        g.Sfx?.ModEmber(at, false); g.Sfx?.Thunder();
        QueueFree();
    }
}
