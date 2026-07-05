using Godot;

// The orc boss picks up and hurls a rock at the nearest witch. It arcs in, and whoever it lands on/near is
// damaged and STUNNED. Deterministic flight (from + target) so host and client copies match; the host applies
// the damage/stun, client copies are visual (Remote). Self-destructs on impact or after a short life.
public partial class BossRock : Node3D
{
    public Vector3 Vel;
    public float Dmg = 22f, StunDur = 1.6f, HitR = 2.6f;
    public bool Remote = false;
    private float _life = 4f;
    private bool _done = false;

    public void Init(Vector3 from, Vector3 targetPos, float dmg)
    {
        Dmg = dmg;
        GlobalPosition = from;
        var flat = new Vector3(targetPos.X - from.X, 0f, targetPos.Z - from.Z);
        float dist = flat.Length();
        float speed = 24f;
        float travel = Mathf.Max(0.2f, dist / speed);
        Vel = flat / travel + new Vector3(0f, 22f * travel * 0.5f + (targetPos.Y - from.Y) / travel, 0f);   // ballistic arc that lands on target

        var rock = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.85f, Height = 1.7f, RadialSegments = 6, Rings = 4 }, MaterialOverride = Game.Toon(new Color(0.42f, 0.38f, 0.34f), 0.95f, 0.35f, 0.05f) };
        AddChild(rock);
        AddChild(new OmniLight3D { OmniRange = 4f, LightColor = new Color(1f, 0.55f, 0.3f), LightEnergy = 0.7f });
    }

    public override void _Process(double delta)
    {
        if (Game.I == null || Game.I.State != GameState.Playing) return;
        float dt = (float)delta;
        _life -= dt; if (_life <= 0f) { QueueFree(); return; }
        Vel += new Vector3(0f, -22f, 0f) * dt;
        GlobalPosition += Vel * dt;
        RotateX(dt * 6f); RotateZ(dt * 4.5f);
        float gy = Game.I.SurfaceHeight(GlobalPosition, 1e9f);

        if (!Remote && !_done)   // host: check for a witch hit
        {
            bool hit = false;
            if (Game.I.Player != null && !Game.I.Player.Downed)
            { var d = Game.I.Player.GlobalPosition - GlobalPosition; d.Y = 0f; if (d.Length() < HitR && Mathf.Abs(GlobalPosition.Y - Game.I.Player.GlobalPosition.Y) < 3f) hit = true; }
            if (!hit && Game.I.NetMgr != null && Game.I.NetMgr.PlayerNear(GlobalPosition, HitR, out _) != 0) hit = true;
            if (hit) { Game.I.NetMgr?.HurtStunPlayersIn(GlobalPosition, HitR + 0.6f, Dmg, StunDur); Land(); return; }
        }
        if (GlobalPosition.Y <= gy + 0.5f) Land();
    }

    private void Land()
    {
        if (_done) return; _done = true;
        Game.I.VfxRing(GlobalPosition, new Color(0.55f, 0.45f, 0.35f), 2.4f, 0.4f);
        Game.I.SpawnDust(GlobalPosition, Vector3.Up);
        Game.I.Sfx?.Thud(GlobalPosition, net: false);
        QueueFree();
    }
}
