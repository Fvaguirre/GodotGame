using Godot;

// Faith Shield ult: a dome of holy light at the player's location.
// The player (and allies) pass freely and shoot out; enemies are pushed out and must
// break it to enter. Enemies in contact chip its HP and take minor damage from it.
// FaithShield.cs — the protective dome of the Divine 'FaithShield' ultimate (and the template for timed bubble shields).
public partial class FaithShield : Node3D
{
    public float Hp = 300f, MaxHp = 300f;
    public float Radius = 6f;
    public float Dur = 8f;
    public float MeleeDmg = 6f;       // per second to enemies in contact
    public bool Reflect = false;      // ModShield: stronger contact damage + heals occupant
    public float HealPerSec = 6f;     // medium heal to allies standing inside
    public float BurstDmg = 60f;      // shatter blast when broken or expired
    public float BurstRadius = 13f;

    private MeshInstance3D _dome;
    private float _dmgCd = 0f;
    private float _flash = 0f;

    public override void _Ready()
    {
        _dome = new MeshInstance3D { Mesh = new SphereMesh { Radius = Radius, Height = Radius * 2f } };
        var col = DamageTypes.Col(DamageType.Holy);
        var mat = new StandardMaterial3D
        {
            AlbedoColor = new Color(col.R, col.G, col.B, 0.22f),
            EmissionEnabled = true, Emission = col, EmissionEnergyMultiplier = 0.6f,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
        };
        _dome.MaterialOverride = mat;
        AddChild(_dome);
        AddChild(new OmniLight3D { OmniRange = Radius * 2.2f, LightColor = col, LightEnergy = 1.6f });
    }

    public void Hit(float dmg) { Hp -= dmg; _flash = 0.25f; }

    public override void _Process(double delta)
    {
        var g = Game.I;
        if (g == null || g.State != GameState.Playing) return;
        float dt = (float)delta;

        // rooted where it was cast — does NOT follow the witch

        Dur -= dt;
        if (_flash > 0f) _flash -= dt;
        _dmgCd -= dt;
        bool tick = _dmgCd <= 0f;
        if (tick) _dmgCd = 0.25f;

        // keep enemies out + chip the shield when they press against it; the shield burns them
        foreach (var e in g.Enemies.ToArray())
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
            var off = new Vector2(e.GlobalPosition.X - GlobalPosition.X, e.GlobalPosition.Z - GlobalPosition.Z);
            float d = off.Length();
            float rim = Radius + e.Radius;
            if (d < rim)
            {
                float k = rim / Mathf.Max(d, 0.001f);
                e.GlobalPosition = new Vector3(GlobalPosition.X + off.X * k, e.GlobalPosition.Y, GlobalPosition.Z + off.Y * k);
                Hp -= (Reflect ? 24f : 14f) * dt;          // pressing foes wear it down
                _flash = 0.2f;
                if (tick) e.Hurt((Reflect ? MeleeDmg * 1.8f : MeleeDmg) * 0.25f, DamageType.Holy, false);
            }
        }

        // heal allies (and the caster) standing inside
        if (tick && g.Player != null)
        {
            var po = new Vector2(g.Player.GlobalPosition.X - GlobalPosition.X, g.Player.GlobalPosition.Z - GlobalPosition.Z);
            if (po.Length() < Radius) g.Player.Heal(HealPerSec * 0.25f * (Reflect ? 1.3f : 1f));
        }

        // pulse
        if (_dome?.MaterialOverride is StandardMaterial3D m)
        {
            float frac = Mathf.Clamp(Hp / MaxHp, 0f, 1f);
            float e = 0.4f + 0.4f * frac + _flash * 2f;
            m.EmissionEnergyMultiplier = e;
            m.AlbedoColor = new Color(m.AlbedoColor.R, m.AlbedoColor.G, m.AlbedoColor.B, 0.12f + 0.16f * frac + _flash);
        }

        if (Hp <= 0f || Dur <= 0f)
        {
            Detonate();
            if (g.Player != null) g.Player.OnShieldEnded();
            g.Shield = null;
            QueueFree();
        }
    }

    // shattering crescendo: damages everything in a medium radius when broken or expired
    private void Detonate()
    {
        var g = Game.I;
        if (g == null) return;
        var col = DamageTypes.Col(DamageType.Holy);
        foreach (var e in g.Enemies.ToArray())
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
            var off = new Vector2(e.GlobalPosition.X - GlobalPosition.X, e.GlobalPosition.Z - GlobalPosition.Z);
            if (off.Length() < BurstRadius + e.Radius) e.Hurt(BurstDmg, DamageType.Holy, true);
        }
        var v = new Vfx(); g.AddChild(v); v.GlobalPosition = new Vector3(GlobalPosition.X, 1f, GlobalPosition.Z);
        v.Init(new SphereMesh { Radius = BurstRadius * 0.5f, Height = BurstRadius }, col, 0.5f, 7f);
        g.Sfx?.Release(DamageType.Holy);
    }
}
