using Godot;

// A lingering pool of pestilence spat by the boss's shoulder goblin. It sits on the ground dealing Nature
// damage to any witch standing in it, and remains until the boss is killed (host + clients both track the boss
// in Game.Enemies, so it clears everywhere at once). Ghost copies on clients are visual-only.
public partial class PestilencePool : Node3D
{
    public float Radius = 6f, Dmg = 8f;
    public bool Remote = false;
    private float _tick = 0f, _age = 0f;
    private MeshInstance3D _ring;

    public void Init(Vector3 pos, float radius, float dmg)
    {
        Radius = radius; Dmg = dmg;
        GlobalPosition = new Vector3(pos.X, 0.06f, pos.Z);
        var poolCol = new Color(0.5f, 0.85f, 0.22f);   // sickly green

        var disc = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = radius, BottomRadius = radius, Height = 0.14f } };
        var dm = Game.ToonEmissive(poolCol, 1.1f, 0f);
        dm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; dm.AlbedoColor = new Color(poolCol.R, poolCol.G, poolCol.B, 0.5f);
        disc.MaterialOverride = dm; AddChild(disc);

        // (POLISH) bubbling blisters break up the flat pool surface — each swells and shrinks on its own loop
        for (int i = 0; i < 6; i++)
        {
            float a = GD.Randf() * Mathf.Tau, rr = radius * (0.15f + GD.Randf() * 0.7f), bs = 0.3f + GD.Randf() * 0.4f;
            var b = new MeshInstance3D { Mesh = new SphereMesh { Radius = bs, Height = bs * 1.3f }, MaterialOverride = Game.ToonEmissive(poolCol.Lerp(new Color(0.32f, 0.5f, 0.12f), 0.5f), 0.9f, 0f), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            b.Position = new Vector3(Mathf.Cos(a) * rr, 0.04f, Mathf.Sin(a) * rr);
            AddChild(b);
            var bt = b.CreateTween().SetLoops();
            bt.TweenProperty(b, "scale", Vector3.One * 1.3f, 0.5f + GD.Randf() * 0.4f);
            bt.TweenProperty(b, "scale", Vector3.One * 0.7f, 0.5f + GD.Randf() * 0.4f);
        }

        var ring = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = radius * 0.93f, OuterRadius = radius } };
        ring.MaterialOverride = Game.Emissive(new Color(1f, 0.16f, 0.1f), 2.6f);   // red danger ring while in play
        AddChild(ring); _ring = ring;

        AddChild(new OmniLight3D { OmniRange = radius * 1.4f, LightColor = poolCol, LightEnergy = 1.2f });
        Game.I?.SpawnPollen(new Vector3(pos.X, 0.4f, pos.Z), radius, poolCol, 12, 8f, net: false);   // drifting spores
        Scale = new Vector3(0.2f, 1f, 0.2f);
        var tw = CreateTween(); tw.TweenProperty(this, "scale", Vector3.One, 0.35f).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Back);
    }

    public override void _Process(double delta)
    {
        if (Game.I == null || !Game.I.SimActive) return;
        float dt = (float)delta;
        _age += dt;
        bool bossAlive = false;
        foreach (var e in Game.I.Enemies) if (e != null && GodotObject.IsInstanceValid(e) && e.IsBoss && !e.Dead) { bossAlive = true; break; }
        if (!bossAlive && _age > 0.5f) { QueueFree(); return; }   // boss down → the plague clears everywhere
        if (_ring != null) _ring.RotationDegrees = new Vector3(0, _age * 40f, 0);
        if (Remote) return;   // clients: visual only; the host applies damage
        _tick -= dt;
        if (_tick <= 0f) { _tick = 0.5f; Game.I.NetMgr?.HurtPlayersIn(GlobalPosition, Radius, Dmg); }
    }
}
