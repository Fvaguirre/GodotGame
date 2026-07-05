using Godot;

// The non-zombie shoulder goblin scatters mines (seed-bomb style). Each sits armed with a red danger outline
// and detonates when a witch steps into its radius, hurting witches in the blast. They remain until triggered
// or the boss is killed. Host-authoritative detonation; client copies are visual (Remote) and cleared by a
// broadcast when the host detonates one (or when the boss dies).
public partial class BossMine : Node3D
{
    public float Radius = 3.4f, Dmg = 16f;
    public bool Remote = false;
    private float _arm = 0.7f, _age = 0f;
    private MeshInstance3D _ring, _body;

    public void Init(Vector3 pos, float radius, float dmg)
    {
        Radius = radius; Dmg = dmg;
        float gy = Game.I != null ? Game.I.SurfaceHeight(pos, 1e9f) : pos.Y;
        GlobalPosition = new Vector3(pos.X, gy + 0.35f, pos.Z);

        _body = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.5f, Height = 1f }, MaterialOverride = Game.ToonEmissive(new Color(0.55f, 0.62f, 0.24f), 1.3f, 0.05f) };
        AddChild(_body);
        _ring = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = radius * 0.9f, OuterRadius = radius }, MaterialOverride = Game.Emissive(new Color(1f, 0.16f, 0.1f), 2f) };
        _ring.Position = new Vector3(0, -0.3f, 0);
        AddChild(_ring);
        AddChild(new OmniLight3D { OmniRange = 3f, LightColor = new Color(0.65f, 0.85f, 0.3f), LightEnergy = 1f });
        Scale = new Vector3(0.2f, 0.2f, 0.2f);
        CreateTween().TweenProperty(this, "scale", Vector3.One, 0.3f).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Back);
    }

    public override void _Process(double delta)
    {
        if (Game.I == null || Game.I.State != GameState.Playing) return;
        float dt = (float)delta; _age += dt; if (_arm > 0f) _arm -= dt;

        bool bossAlive = false;
        foreach (var e in Game.I.Enemies) if (e != null && GodotObject.IsInstanceValid(e) && e.IsBoss && !e.Dead) { bossAlive = true; break; }
        if (!bossAlive && _age > 0.5f) { QueueFree(); return; }

        float p = 1f + 0.06f * Mathf.Sin(_age * 5f);
        if (_ring != null) _ring.Scale = new Vector3(p, 1f, p);
        if (_body != null) _body.Position = new Vector3(0, 0.05f * Mathf.Sin(_age * 3f), 0);

        if (Remote || _arm > 0f) return;   // clients: visual only; the host detonates
        bool trig = false;
        if (Game.I.Player != null && !Game.I.Player.Downed) { var d = Game.I.Player.GlobalPosition - GlobalPosition; d.Y = 0f; if (d.Length() < Radius) trig = true; }
        if (!trig && Game.I.NetMgr != null && Game.I.NetMgr.PlayerNear(GlobalPosition, Radius, out _) != 0) trig = true;
        if (trig) Detonate();
    }

    private void Detonate()
    {
        Game.I.NetMgr?.HurtPlayersIn(GlobalPosition, Radius, Dmg);
        Game.I.VfxRing(GlobalPosition, new Color(0.6f, 0.9f, 0.3f), Radius, 0.5f);
        Game.I.SpawnBrambleBurst(GlobalPosition, 1.6f, 9, net: true);
        Game.I.Sfx?.Thunder();
        Game.I.NetMgr?.BroadcastVfx(43, GlobalPosition, Vector3.Zero, Radius, 0f, Colors.White);   // clients: pop the matching ghost mine
        QueueFree();
    }
}
