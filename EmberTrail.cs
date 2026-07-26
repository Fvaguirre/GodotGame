using Godot;

// EmberTrail.cs — the burning strip left by a Wildfire Rush flame dash. A rectangular ground zone (~8u wide × 12-15u long,
// scaled by AoE cards) that lingers Dur seconds. Each tick it STACKS burn on foes inside (the burn DoT does the damage, and
// its ticks lifesteal to the caster via _burnOwner). ALLIES who run the strip gain move speed + a light heal — never the
// caster. Host/solo owns the damage/burn; allies render a Remote visual-only ghost (Net.BroadcastEmberTrail).
public partial class EmberTrail : Node3D
{
    public Vector3 Origin, Dir;
    public float Length = 14f, HalfW = 4f, Dur = 10f;
    public float BurnAdd = 1.2f, BurnPer = 4f, BurnBomb = 30f, HealPerSec = 2f;
    public Player Caster;
    public int OwnerPeer = 0;
    public bool Remote = false;

    private Decal _decal;
    private OmniLight3D _light;
    private float _t = 0f, _burnTick = 0f, _allyTick = 0f, _flick = 0f;
    private Color _col;

    public override void _Ready()
    {
        _col = DamageTypes.Col(DamageType.Ember);
        var mid = Origin + Dir * (Length * 0.5f);
        float my = Game.I != null ? Game.I.SurfaceHeight(mid, mid.Y) : Origin.Y;
        _decal = new Decal
        {
            TextureAlbedo = Game.ScorchTex(), TextureEmission = Game.ScorchTex(), EmissionEnergy = 2.4f,
            Modulate = new Color(_col.R, _col.G, _col.B, 0.85f),
            Size = new Vector3(HalfW * 2f, 24f, Length)   // Y = projection depth over hilly ground
        };
        AddChild(_decal);
        _decal.GlobalPosition = new Vector3(mid.X, my, mid.Z);
        _decal.RotationDegrees = new Vector3(0, Mathf.RadToDeg(Mathf.Atan2(Dir.X, Dir.Z)), 0);
        _light = new OmniLight3D { OmniRange = Mathf.Max(HalfW, Length * 0.35f), LightColor = _col, LightEnergy = 1.4f, Position = new Vector3(mid.X, my + 1.5f, mid.Z) };
        AddChild(_light);
    }

    public override void _Process(double delta)
    {
        var g = Game.I; if (g == null || !g.SimActive) return;
        float dt = (float)delta; _t += dt;
        float fade = Mathf.Clamp(Dur - _t, 0f, 1f);
        if (_decal != null) { _decal.Modulate = new Color(_col.R, _col.G, _col.B, 0.85f * fade); _decal.EmissionEnergy = 0.8f + 1.8f * fade; }
        if (_light != null) _light.LightEnergy = 0.6f + 1.2f * fade;

        _flick -= dt;   // wild flames licking off the strip (both real + ghost, purely visual)
        if (_flick <= 0f)
        {
            _flick = 0.1f;
            var fp = Origin + Dir * (Length * GD.Randf()) + new Vector3(0, 0, 0);
            float fy = g.SurfaceHeight(fp, fp.Y);
            g.SpawnFlameCone(new Vector3(fp.X, fy + 0.15f, fp.Z), Vector3.Up, HalfW * 0.7f, _col);
        }
        if (Remote) { if (_t >= Dur) QueueFree(); return; }

        _burnTick += dt;
        if (_burnTick >= 0.5f)
        {
            _burnTick = 0f;
            foreach (var e in g.Enemies.ToArray())
                if (e != null && !e.Dead && GodotObject.IsInstanceValid(e) && Inside(e.GlobalPosition, e.Radius))
                    e.AddBurn(BurnAdd, BurnPer, BurnBomb, 0f, OwnerPeer);   // stacks burn; its ticks lifesteal to the owner
        }

        _allyTick += dt;
        if (_allyTick >= 0.3f)
        {
            _allyTick = 0f;
            g.NetMgr?.BuffAlliesInStrip(Origin, Dir, HalfW, Length, HealPerSec * 0.3f, 0.5f);   // allies (not caster) get heal + speed
        }

        if (_t >= Dur) QueueFree();
    }

    private bool Inside(Vector3 pos, float radius)
    {
        var rel = pos - Origin; rel.Y = 0;
        float along = rel.Dot(Dir);
        if (along < -radius || along > Length + radius) return false;
        float perp = (rel - Dir * along).Length();
        return perp < HalfW + radius;
    }
}
