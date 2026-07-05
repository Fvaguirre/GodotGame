using Godot;

// AirMine.cs — the Gale witch's Stormform legendary. While Stormform is up and she's walking she leaves these
// in her wake (see Player.UpdateUlt). A mine arms after a short delay, then detonates when an enemy wanders
// close: it launches everything nearby straight up (small impact damage on the spot, then the launch becomes
// fall damage when they land — handled by Enemy's thrown/fling physics). Detonation runs through
// Net.StormForce so it works for client casters too (host applies the fling/damage to the real enemies). (NEW)
public partial class AirMine : Node3D
{
    private Player _caster;
    private float _dmg;
    private float _arm = 0.4f;          // brief arming delay so it doesn't pop on the dropper
    private float _life = 10f;          // self-despawn if nothing trips it
    private bool _spent = false;
    private const float TriggerR = 2.6f, BlastR = 4.5f, PopUp = 16f;
    private MeshInstance3D _orb;
    private float _bob = 0f;

    public void Init(Player caster, Vector3 pos, float dmg)
    {
        _caster = caster; _dmg = dmg;
        GlobalPosition = new Vector3(pos.X, pos.Y + 0.7f, pos.Z);   // floats just off the ground

        var col = DamageTypes.Col(DamageType.Wind);
        var mat = new StandardMaterial3D {
            AlbedoColor = new Color(col.R, col.G, col.B, 0.55f),
            EmissionEnabled = true, Emission = col, EmissionEnergyMultiplier = 2.2f,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        };
        _orb = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.35f, Height = 0.7f }, MaterialOverride = mat };
        AddChild(_orb);
        var ring = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = 0.5f, OuterRadius = 0.62f }, MaterialOverride = mat };
        ring.RotationDegrees = new Vector3(90, 0, 0);
        _orb.AddChild(ring);
        AddChild(new OmniLight3D { OmniRange = 3f, LightColor = col, LightEnergy = 1.2f });
    }

    public override void _Process(double delta)
    {
        if (Game.I == null || _spent || Game.I.State != GameState.Playing) return;   // freeze while paused (NEW)
        float dt = (float)delta;
        if (_arm > 0f) _arm -= dt;
        _life -= dt;
        _bob += dt;
        if (_orb != null) { _orb.Position = new Vector3(0, Mathf.Sin(_bob * 3f) * 0.08f, 0); _orb.RotateY(dt * 2f); }
        if (_life <= 0f) { QueueFree(); return; }
        if (_arm > 0f) return;

        // trip when any enemy strays within the trigger radius (proxy positions are synced, so a client
        // caster's mines still detect correctly; the blast itself is applied host-side via StormForce)
        foreach (var e in Game.I.Enemies.ToArray())
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
            float d = new Vector2(e.GlobalPosition.X - GlobalPosition.X, e.GlobalPosition.Z - GlobalPosition.Z).Length();
            if (d <= TriggerR + e.Radius) { Explode(); return; }
        }
    }

    private void Explode()
    {
        _spent = true;
        Vector3 at = new Vector3(GlobalPosition.X, GlobalPosition.Y - 0.7f, GlobalPosition.Z);
        Game.I.NetMgr?.StormForce(at, BlastR, 2, _dmg);     // small impact damage on the spot
        Game.I.NetMgr?.StormForce(at, BlastR, 1, PopUp);    // launch them up → fall damage on landing
        var col = DamageTypes.Col(DamageType.Wind);
        Game.I.NetMgr?.BroadcastVfx(0, at, Vector3.Up, BlastR, 0f, col);   // allies see the pop ring (kind 0)
        // local burst: a quick expanding ring + flash
        var ring = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = BlastR * 0.7f, OuterRadius = BlastR * 0.8f } };
        var rm = new StandardMaterial3D {
            AlbedoColor = new Color(col.R, col.G, col.B, 0.6f), EmissionEnabled = true, Emission = col,
            EmissionEnergyMultiplier = 2.5f, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        };
        ring.MaterialOverride = rm;
        Game.I.AddChild(ring);   // flat ground shockwave ring (NEW: removed the upright rotation)
        ring.GlobalPosition = new Vector3(at.X, at.Y + 0.1f, at.Z);
        ring.Scale = new Vector3(0.3f, 0.3f, 0.3f);
        var tw = ring.CreateTween(); tw.SetParallel(true);
        tw.TweenProperty(ring, "scale", new Vector3(1.4f, 1.4f, 1.4f), 0.3f);
        tw.TweenProperty(ring, "transparency", 1f, 0.32f);
        tw.SetParallel(false);
        tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(ring)) ring.QueueFree(); }));
        Game.I.Sfx?.Impact(DamageType.Wind);
        QueueFree();
    }
}
