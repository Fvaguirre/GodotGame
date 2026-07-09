using Godot;

// EmberDeathBurst.cs — a Living Bomb's death eruption. When an Ember-marked foe dies with Z Living Bomb stacks, it detonates
// Z times, ~0.2s apart, at the spot it died — each blast damaging nearby foes for a slice of the dead foe's MAX hp (chains
// through the horde). Host/solo owns the damage; each blast broadcasts its ember-burst VFX/SFX so allies see the whole string.
public partial class EmberDeathBurst : Node3D
{
    private int _left;
    private float _radius, _perBlast, _t = 0f;

    public void Init(Vector3 pos, int stacks, float radius, float perBlast)
    {
        GlobalPosition = pos; _left = stacks; _radius = radius; _perBlast = perBlast;
        Detonate();   // first blast fires immediately at the origin
    }

    public override void _Process(double delta)
    {
        if (Game.I == null || Game.I.State != GameState.Playing) return;
        _t -= (float)delta;
        if (_left > 0) { if (_t <= 0f) Detonate(); }
        else if (_t <= -0.4f) QueueFree();
    }

    private void Detonate()
    {
        _left--; _t = 0.2f;   // next blast in ~0.2s
        Vector3 pos = GlobalPosition;
        foreach (var o in Game.I.Enemies.ToArray())
            if (o != null && !o.Dead && GodotObject.IsInstanceValid(o) &&
                new Vector2(o.GlobalPosition.X - pos.X, o.GlobalPosition.Z - pos.Z).Length() < _radius + o.Radius)
                o.Hurt(_perBlast, DamageType.Ember, true);
        Game.I.DamageWorld(pos, _radius, _perBlast);
        Game.I.SpawnEmberBurst(pos + Vector3.Up * 0.4f, _radius * (0.85f + 0.1f * _left));   // broadcasts kind 21 → allies see each blast
        Game.I.VfxRing(pos, DamageTypes.Col(DamageType.Ember), _radius * 1.2f, 0.4f);
        Game.I.Sfx?.ModEmber(pos);   // networked ember boom
    }
}
