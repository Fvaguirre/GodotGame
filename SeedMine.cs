using Godot;

// SeedMine.cs — a proximity mine flung out by the "Seed Mines" finisher (equippable by any witch).
// Caster-simulated: it arms briefly, then detonates when an enemy steps near, dealing a med-strong
// Nature blast (+ a little poison). Damage routes through Enemy.Hurt so it forwards to the host on a
// client like every other hit. With the legendary "Sympathetic Seeds" it chains to nearby mines.
public partial class SeedMine : Node3D
{
    public Player Caster;
    public float Damage = 40f;
    public float Trigger = 1.9f;     // proximity that sets it off
    public float Blast = 4.5f;       // explosion radius
    public bool Chain = false;       // legendary: detonating sets off nearby mines
    public float Poison = 0f;        // poison dps applied to foes in the blast
    public float CloudPoison = 0f;   // (OVERHAUL) Spore Mines: poison cloud left where it detonates
    public float CloudRadius = 0f;   // (OVERHAUL) raw radius — GroundField._Ready scales by SpellArea
    public float Life = 16f;         // mines linger a while, then wither
    public bool Remote = false;      // (NEW) visual-only copy on allies: shows + pops visually, deals no damage
    private float _arm = 0.45f;
    private bool _done = false;
    private Node3D _body;
    private float _phase = 0f;

    public override void _Ready()
    {
        var col = new Color(0.45f, 0.85f, 0.4f);
        _body = new Node3D(); AddChild(_body);
        var husk = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.32f, Height = 0.7f }, MaterialOverride = Game.ToonEmissive(new Color(0.4f, 0.3f, 0.18f), 0.4f, 0.03f) };
        husk.Scale = new Vector3(1f, 1.3f, 1f);
        _body.AddChild(husk);
        var sprout = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.06f, Height = 0.35f }, MaterialOverride = Game.ToonEmissive(col, 1.2f, 0.02f) };
        sprout.Position = new Vector3(0, 0.45f, 0);
        _body.AddChild(sprout);
        // a faint ground telegraph so you can see where they sit
        var ring = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = Trigger - 0.25f, OuterRadius = Trigger } };
        var rm = Game.Emissive(col, 0.9f); rm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        var rc = rm.AlbedoColor; rc.A = 0.4f; rm.AlbedoColor = rc;
        ring.MaterialOverride = rm; ring.Position = new Vector3(0, 0.04f, 0);
        AddChild(ring);
        AddChild(new OmniLight3D { Position = new Vector3(0, 0.3f, 0), OmniRange = 2.5f, LightColor = col, LightEnergy = 0.6f });
    }

    public override void _Process(double delta)
    {
        if (_done || Game.I == null || !Game.I.SimActive) return;   // freeze while paused (NEW)
        if (!Remote && (Caster == null || !GodotObject.IsInstanceValid(Caster))) return;   // the real (damaging) mine needs its caster
        if (!Game.I.WorldRunning) return;
        float dt = (float)delta;
        if (_arm > 0f) _arm -= dt;
        Life -= dt;
        _phase += dt * 3f;
        if (_body != null) _body.Position = new Vector3(0, Mathf.Sin(_phase) * 0.06f, 0);   // gentle bob
        if (Life <= 0f) { QueueFree(); return; }
        if (_arm > 0f) return;
        foreach (var e in Game.I.Enemies.ToArray())
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
            if (new Vector2(e.GlobalPosition.X - GlobalPosition.X, e.GlobalPosition.Z - GlobalPosition.Z).Length() < Trigger + e.Radius) { if (Remote) DetonateVisual(); else Detonate(); return; }
        }
    }

    // (NEW) VFX-only pop for ally copies — mirrors the real burst's look, no damage/chain/sound.
    private void DetonateVisual()
    {
        if (_done) return;
        _done = true;
        var burst = new Color(1.0f, 0.55f, 0.12f);
        Game.I.VfxRing(GlobalPosition, burst, Blast, 0.4f);
        var v = new Vfx(); Game.I.AddChild(v); v.GlobalPosition = GlobalPosition + Vector3.Up * 0.4f;
        v.Init(new SphereMesh { Radius = Blast * 0.5f, Height = Blast }, burst, 0.4f, 6f);
        var flash = new OmniLight3D { OmniRange = Blast * 2.2f, LightColor = burst, LightEnergy = 4.5f };
        Game.I.AddChild(flash); flash.GlobalPosition = GlobalPosition + Vector3.Up * 0.5f;
        var ft = flash.CreateTween();
        ft.TweenProperty(flash, "light_energy", 0f, 0.4f);
        ft.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(flash)) flash.QueueFree(); }));
        QueueFree();
    }

    public void Detonate()
    {
        if (_done) return;
        _done = true;
        var burst = new Color(1.0f, 0.55f, 0.12f);   // (NEW) warm yellow/orange burst glow
        foreach (var e in Game.I.Enemies.ToArray())
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
            if (new Vector2(e.GlobalPosition.X - GlobalPosition.X, e.GlobalPosition.Z - GlobalPosition.Z).Length() < Blast + e.Radius)
            { e.Hurt(Damage, DamageType.Nature, true); e.HitFrom(GlobalPosition); if (Poison > 0f) e.Poison(Poison, 3f); }
        }
        Game.I.DamageWorld(GlobalPosition, Blast, Damage);   // (NEW) the blast breaks props too
        if (CloudPoison > 0f)   // (OVERHAUL) Spore Mines: leave a lingering poison cloud
        {
            var cloud = new GroundField { Type = FieldType.Hex, Radius = CloudRadius, Dur = 3.5f, Power = CloudPoison * 0.4f, PoisonAdd = CloudPoison, DType = DamageType.Nature, TintColor = DamageTypes.Col(DamageType.Nature), Src = Caster };
            Game.I.AddChild(cloud); cloud.GlobalPosition = new Vector3(GlobalPosition.X, 0.04f, GlobalPosition.Z);
        }
        if (Chain)   // legendary: set off nearby mines too
            foreach (var n in Game.I.GetChildren())
                if (n is SeedMine sm && sm != this && GodotObject.IsInstanceValid(sm) && !sm._done && GlobalPosition.DistanceTo(sm.GlobalPosition) < Blast + 2f) sm.Detonate();
        Game.I.VfxRing(GlobalPosition, burst, Blast, 0.4f);
        var v = new Vfx(); Game.I.AddChild(v); v.GlobalPosition = GlobalPosition + Vector3.Up * 0.4f;
        v.Init(new SphereMesh { Radius = Blast * 0.5f, Height = Blast }, burst, 0.4f, 6f);
        var flash = new OmniLight3D { OmniRange = Blast * 2.2f, LightColor = burst, LightEnergy = 4.5f };   // (NEW) bright warm burst glow
        Game.I.AddChild(flash); flash.GlobalPosition = GlobalPosition + Vector3.Up * 0.5f;
        var ft = flash.CreateTween();
        ft.TweenProperty(flash, "light_energy", 0f, 0.4f);
        ft.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(flash)) flash.QueueFree(); }));
        Game.I.Sfx?.Impact(DamageType.Nature);
        Game.I.Sfx?.RootRush(GlobalPosition);                                                                // (NEW) fast-moving roots
        QueueFree();
    }
}
