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
    private Node3D _barrel;
    private float _sprayT = 0f;
    private readonly HashSet<Enemy> _hit = new();

    public override void _Ready()
    {
        var col = DamageTypes.Col(DamageType.Blood);
        float W = Width;
        float h = Mathf.Clamp(W * 0.42f, 5f, 9f);   // taller crest for wider waves

        // the main rolling body — a rounded horizontal roll of blood (NOT a box), laid along the wave's width,
        // translucent so it reads as liquid rather than a solid wall
        _mesh = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = h * 0.42f, BottomRadius = h * 0.5f, Height = W, RadialSegments = 22 }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        _mesh.RotationDegrees = new Vector3(0, 0, 90);   // lay the cylinder along local X (the wave's width)
        _mesh.Position = new Vector3(0, h * 0.5f, 0.2f);
        _mat = new StandardMaterial3D
        {
            AlbedoColor = new Color(col.R, col.G, col.B, 0.72f),
            EmissionEnabled = true, Emission = col, EmissionEnergyMultiplier = 1.9f,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        _mesh.MaterialOverride = _mat;
        AddChild(_mesh);

        // the churning crest barrel — the blood element shader roils like fluid; sat up-and-forward as the breaking curl
        _barrel = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = h * 0.3f, BottomRadius = h * 0.34f, Height = W * 0.98f, RadialSegments = 18 }, MaterialOverride = Game.ElementBoltMat(col, DamageType.Blood), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        ((MeshInstance3D)_barrel).RotationDegrees = new Vector3(0, 0, 90);
        ((MeshInstance3D)_barrel).Position = new Vector3(0, h * 0.86f, -h * 0.28f);
        AddChild(_barrel);

        // the advancing front face — a sloped translucent sheet leaning forward, so it reads as an oncoming wall of liquid
        var face = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(W * 1.02f, h * 1.15f, 0.5f) }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        var fm = Game.ToonEmissive(col, 1.5f, 0f); fm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; fm.AlbedoColor = new Color(col.R, col.G, col.B, 0.34f); fm.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        face.MaterialOverride = fm;
        face.RotationDegrees = new Vector3(-28f, 0, 0);   // top leans forward over the base
        face.Position = new Vector3(0, h * 0.5f, -h * 0.36f);
        AddChild(face);

        // faint outer sheath for volume glow
        var sheath = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = h * 0.62f, BottomRadius = h * 0.72f, Height = W * 1.1f, RadialSegments = 16 }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        var sm = Game.ToonEmissive(col, 1.2f, 0f); sm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; sm.AlbedoColor = new Color(col.R, col.G, col.B, 0.16f); sm.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        sheath.MaterialOverride = sm; sheath.RotationDegrees = new Vector3(0, 0, 90); sheath.Position = new Vector3(0, h * 0.55f, 0.1f);
        AddChild(sheath);

        // a lighter foam line breaking along the very crest
        var foam = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = h * 0.12f, BottomRadius = h * 0.12f, Height = W * 0.96f, RadialSegments = 10 }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = Game.ToonEmissive(col.Lerp(new Color(1f, 0.5f, 0.5f), 0.6f), 2.4f, 0f) };
        foam.RotationDegrees = new Vector3(0, 0, 90); foam.Position = new Vector3(0, h * 1.02f, -h * 0.32f);
        AddChild(foam);

        LookAt(GlobalPosition + Dir, Vector3.Up);
        AddChild(new OmniLight3D { OmniRange = W * 0.95f, LightColor = col, LightEnergy = 3.2f, Position = new Vector3(0, h * 0.6f, 0) });
        if (!Remote) { Game.I?.Sfx?.Release(DamageType.Blood); Game.I?.SpawnBloodMist(GlobalPosition, W * 0.5f, net: false); }   // a wet surge + a burst of mist on release
    }

    public override void _Process(double delta)
    {
        var g = Game.I;
        if (g == null || !g.SimActive) return;
        float dt = (float)delta;
        float step = Speed * dt;
        GlobalPosition += Dir * step;
        _travelled += step;

        // fling droplets of blood off the breaking crest — the spray is what sells "liquid" over "solid block"
        _sprayT -= dt;
        if (_sprayT <= 0f && _travelled < Range)
        {
            _sprayT = 0.05f;
            var rt = Dir.Cross(Vector3.Up).Normalized();
            float hh = Mathf.Clamp(Width * 0.42f, 5f, 9f);
            int n = 2 + (int)(Width / 12f);
            for (int i = 0; i < n; i++)
            {
                float sx = (GD.Randf() - 0.5f) * Width;
                var start = GlobalPosition + Vector3.Up * (hh * 0.95f) + rt * sx - Dir * (hh * 0.3f);
                var drop = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.14f + GD.Randf() * 0.22f, Height = 0.4f }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, MaterialOverride = Game.ToonEmissive(DamageTypes.Col(DamageType.Blood), 1.6f, 0.03f) };
                g.AddChild(drop); drop.GlobalPosition = start;
                var end = start + Dir * (2f + GD.Randf() * 5f) + Vector3.Up * (0.6f + GD.Randf()) + Vector3.Down * (hh + GD.Randf() * 3f);   // arc up-and-forward, then fall
                var tw = drop.CreateTween(); tw.SetParallel(true);
                tw.TweenProperty(drop, "global_position", end, 0.55 + GD.Randf() * 0.3).SetEase(Tween.EaseType.In);
                tw.TweenProperty(drop, "transparency", 1f, 0.7);
                tw.SetParallel(false);
                tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(drop)) drop.QueueFree(); }));
            }
        }

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
            float f = Mathf.Clamp((_travelled - Range) / 4f, 0f, 1f);   // 0 → 1 as it dissipates
            foreach (var ch in GetChildren()) if (ch is GeometryInstance3D gi) gi.Transparency = f;   // fade the whole wave out (body + crest + face + foam + sheath)
            if (_travelled >= Range + 4f) QueueFree();
        }
    }
}
