using Godot;
using System.Collections.Generic;

// A wide wall of blood that surges forward, striking each enemy once: heavy damage, knockback, and a slow.
// BloodWave.cs — the expanding blood-wave VFX/hit ring used by Crimson abilities (e.g. Blood Tsunami). Visual + optional AoE sweep.
public partial class BloodWave : Node3D
{
    public Vector3 Dir = Vector3.Forward;
    public float Dmg = 30f;
    public float Knock = 5f;
    public float Width = 12f;
    public float Speed = 22f;
    public float Range = 46f;
    public float SlowDur = 2.5f;
    public bool BanksStack = false;   // CrimsonRush wave: grant the caster a stack if it kills
    public float ShieldChance = 0.5f;  // (NEW) per-foe chance to return a blood shield charge; CrimsonRush sets this by rarity (0.20 common → up)
    public bool Gush = false;          // (NEW) CrimsonRush: blood gush at each struck foe + a splatter when the wave dies
    public bool Remote = false;        // client visual copy: surges + fades, no damage
    private bool _announced = false;

    private bool _banked = false;
    private bool _splattered = false;

    private float _travelled = 0f;
    private MeshInstance3D _mesh;
    private StandardMaterial3D _mat;
    private readonly HashSet<Enemy> _hit = new();

    public override void _Ready()
    {
        var col = DamageTypes.Col(DamageType.Blood);
        _mesh = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(Width, 6f, 1.8f) } };
        _mat = new StandardMaterial3D
        {
            AlbedoColor = new Color(col.R, col.G, col.B, 0.7f),
            EmissionEnabled = true, Emission = col, EmissionEnergyMultiplier = 2.2f,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
        };
        _mesh.MaterialOverride = _mat;
        AddChild(_mesh);
        // roiling crest along the top (blood element shader churns like real fluid)
        var crest = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(Width * 1.02f, 2.6f, 2.6f) }, MaterialOverride = Game.ElementBoltMat(col, DamageType.Blood) };
        crest.Position = new Vector3(0, 3.3f, 0.2f);
        AddChild(crest);
        // soft translucent outer sheath so it reads as a towering wall of blood
        var sheath = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(Width * 1.15f, 7.2f, 3.4f) } };
        var sm = Game.ToonEmissive(col, 1.4f, 0f);
        sm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; sm.AlbedoColor = new Color(col.R, col.G, col.B, 0.22f); sm.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        sheath.MaterialOverride = sm;
        AddChild(sheath);
        LookAt(GlobalPosition + Dir, Vector3.Up);
        AddChild(new OmniLight3D { OmniRange = Width * 1.2f, LightColor = col, LightEnergy = 3f });
        if (!Remote) { Game.I?.Sfx?.Thunder(); Game.I?.SpawnBloodMist(GlobalPosition, Width * 0.5f, net: false); }   // a crash + a burst of mist on release
    }

    public override void _Process(double delta)
    {
        var g = Game.I;
        if (g == null || g.State != GameState.Playing) return;
        float dt = (float)delta;
        float step = Speed * dt;
        GlobalPosition += Dir * step;
        _travelled += step;

        if (!Remote && !_announced)
        {
            _announced = true;
            g.NetMgr?.BroadcastVfx(8, GlobalPosition, Dir, Width, Range, DamageTypes.Col(DamageType.Blood));
        }

        var right = Dir.Cross(Vector3.Up).Normalized();
        if (!Remote)
        foreach (var e in g.Enemies.ToArray())
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e) || _hit.Contains(e)) continue;
            var to = e.GlobalPosition - GlobalPosition; to.Y = 0;
            float along = to.Dot(Dir);
            float side = Mathf.Abs(to.Dot(right));
            if (along > -1.2f && along < 1.6f && side < Width / 2f + e.Radius)
            {
                _hit.Add(e);
                e.Hurt(Dmg, DamageType.Blood, true);
                e.Knockback(GlobalPosition, Knock);
                e.Slow(SlowDur, 0.55f);
                if (Gush) g.SpawnBloodMist(e.GlobalPosition, 2.2f);   // (NEW) blood gush on each hit (CrimsonRush)
                if (BanksStack)
                {
                    if (!_banked && e.Dead) { _banked = true; g.Player?.BloodReward(1f); }
                    if (g.Player != null && GD.Randf() < ShieldChance) g.Player.AddArmor(false);   // (NEW) chance scales with rarity (set by CrimsonRush): 20% common → up. Returns a red (blood) armor charge
                }
            }
        }
        g.DamageWorld(GlobalPosition, Width * 0.5f + 1.2f, Dmg);   // (NEW) the wave breaks props it sweeps over

        if (_travelled >= Range)
        {
            if (Gush && !_splattered) { _splattered = true; g.SpawnBloodMist(GlobalPosition, Width * 0.5f); }   // (NEW) splatters away at the end (CrimsonRush)
            float f = Mathf.Clamp(1f - (_travelled - Range) / 4f, 0f, 1f);
            if (_mat != null) _mat.AlbedoColor = new Color(_mat.AlbedoColor.R, _mat.AlbedoColor.G, _mat.AlbedoColor.B, 0.6f * f);
            if (_travelled >= Range + 4f) QueueFree();
        }
    }
}
