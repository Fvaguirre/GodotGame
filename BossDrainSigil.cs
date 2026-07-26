using Godot;

// BossDrainSigil.cs — the Sacrifice nerfer's payoff: a crimson ritual circle that drops under the boss when he spawns and
// PERSISTS on the floor. While the boss stands anywhere inside it, it siphons his health — up to a total of 10% of his max HP,
// then it's spent and fades. Encourages herding/holding the boss in the circle. Host-authoritative (drives the drain).
public partial class BossDrainSigil : Node3D
{
    public const float Radius = 14f;      // a real zone to keep the boss in
    private float _drained = 0f;          // total HP siphoned so far
    private float _cap = 0f;              // 10% of the boss's max HP (locked at spawn)
    private float _t;
    private bool _spent = false;
    private Decal _decal;
    private OmniLight3D _light;
    private static readonly Color Col = new Color(0.95f, 0.12f, 0.18f);

    public override void _Ready()
    {
        _decal = new Decal
        {
            TextureAlbedo = Game.FieldTex(), TextureEmission = Game.FieldTex(), EmissionEnergy = 2.2f,
            Modulate = new Color(Col.R, Col.G, Col.B, 0.9f), Size = new Vector3(Radius * 2f, 10f, Radius * 2f)
        };
        AddChild(_decal);
        _light = new OmniLight3D { OmniRange = Radius * 1.6f, LightColor = Col, LightEnergy = 1.8f, Position = new Vector3(0, 1.5f, 0) };
        AddChild(_light);
    }

    public override void _Process(double delta)
    {
        _t += (float)delta;
        if (_decal != null) _decal.EmissionEnergy = 1.6f + 0.9f * Mathf.Sin(_t * 3f);
        if (_spent || Game.I == null || !Game.I.IsAuthority) return;
        var boss = Game.I.WorldBoss;
        if (boss == null || !GodotObject.IsInstanceValid(boss) || boss.Dead) return;
        if (_cap <= 0f) _cap = boss.MaxHp * 0.10f;   // lock to CURRENT max on first tick the boss exists (phase-aware for a future stage 2)
        float flat = new Vector2(boss.GlobalPosition.X - GlobalPosition.X, boss.GlobalPosition.Z - GlobalPosition.Z).Length();
        if (flat <= Radius + boss.Radius)
        {
            float amt = Mathf.Min(boss.MaxHp * 0.03f * (float)delta, _cap - _drained);   // ~10% over ~3.3s of standing in it
            if (amt > 0f) { boss.Hurt(amt, DamageType.Blood, false); _drained += amt; }
            if (_drained >= _cap - 0.5f) { _spent = true; FadeOut(); }
        }
    }

    private void FadeOut()
    {
        var tw = CreateTween();
        if (_decal != null) tw.TweenProperty(_decal, "modulate:a", 0f, 1.2f);
        tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(this)) QueueFree(); }));
    }
}
