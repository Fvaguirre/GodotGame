using Godot;

// A projectile fired by enemies. Travels straight (so it can be dashed/dodged).
// Host: hits whichever player it reaches (host player or an ally, routed over the net) + the Faith Shield.
// Remote (client): visual-only copy spawned from the host's fire event; pops on the local player but deals no damage.
// EnemyBolt.cs — enemy projectiles (casters/zappers/hexers). Host-authoritative; deals player damage via the Net DamagePlayer path.
public partial class EnemyBolt : Node3D
{
    public Vector3 Vel;
    public float Life = 5f;
    public float Dmg = 8f;
    public float Radius = 0.5f;
    public Color Tint = new Color(1f, 0.5f, 0.3f);
    public bool Remote = false;
    public static int Live = 0;

    public override void _Ready()
    {
        Live++;
        var mi = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.3f + Radius * 0.3f, Height = 0.6f + Radius * 0.6f } };
        mi.MaterialOverride = Game.ToonEmissive(Tint, 1.2f, 0.03f);
        AddChild(mi);
        AddChild(new OmniLight3D { OmniRange = 5f, LightColor = Tint, LightEnergy = 1.2f });
    }

    public override void _ExitTree() { Live--; }

    public override void _Process(double delta)
    {
        if (Game.I == null || Game.I.State != GameState.Playing) return;
        float dt = (float)delta;
        GlobalPosition += Vel * dt;
        Life -= dt;

        var p = Game.I.Player;

        if (Remote)
        {
            // client visual: pop when it reaches my own avatar (the host applies the real damage)
            if (p != null && GlobalPosition.DistanceTo(p.GlobalPosition + new Vector3(0, 1.4f, 0)) < Radius + 1.1f) { QueueFree(); return; }
            foreach (var bl in Game.I.Blockers)
                if (new Vector2(GlobalPosition.X - bl.Pos.X, GlobalPosition.Z - bl.Pos.Z).Length() < bl.Radius) { QueueFree(); return; }
            if (Life <= 0f) QueueFree();
            return;
        }

        // Faith Shield: enemy fire is absorbed by the dome and chips its HP
        var sh = Game.I.Shield;
        if (sh != null && GodotObject.IsInstanceValid(sh) && p != null)
        {
            float ds = new Vector2(GlobalPosition.X - sh.GlobalPosition.X, GlobalPosition.Z - sh.GlobalPosition.Z).Length();
            float pd = new Vector2(p.GlobalPosition.X - sh.GlobalPosition.X, p.GlobalPosition.Z - sh.GlobalPosition.Z).Length();
            if (ds < sh.Radius + Radius && pd < sh.Radius) { sh.Hit(Dmg); QueueFree(); return; }
        }
        // host player
        if (p != null && GlobalPosition.DistanceTo(p.GlobalPosition + new Vector3(0, 1.4f, 0)) < Radius + 1.1f)
        {
            p.Hurt(Dmg, GlobalPosition);
            QueueFree();
            return;
        }
        // allies (clients): route the damage to whoever it hits
        if (Game.I.NetMgr != null && Game.I.NetMgr.BoltHitRemote(GlobalPosition, Radius + 1.1f, out long peer))
        {
            Game.I.NetMgr.DamagePlayer(peer, Dmg);
            QueueFree();
            return;
        }
        foreach (var bl in Game.I.Blockers)
            if (new Vector2(GlobalPosition.X - bl.Pos.X, GlobalPosition.Z - bl.Pos.Z).Length() < bl.Radius) { QueueFree(); return; }

        float far = p != null ? GlobalPosition.DistanceTo(p.GlobalPosition) : 0f;
        if (Life <= 0 || far > 95f) QueueFree();
    }
}
