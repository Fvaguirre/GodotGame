using Godot;
using System.Collections.Generic;

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
    // (PUPPET) a shot fired by a turned foe. It ignores players entirely and looks for ENEMIES instead — the one piece of
    // ranged puppetry that isn't free, since this class only ever knew how to find a witch. Host-only, like all its damage.
    public bool HitsEnemies = false;
    public long OwnerPeer = 0;    // peer credited with the kill, so a puppet's kill still pays XP/souls/Highlight (NOT `Owner` — that's Node.Owner)
    public Node3D Shooter;        // never hit whoever fired it
    public static int Live = 0;
    private bool _litReg = false;
    private bool _warned = false;   // (NEW) fired the one-shot "incoming" whistle already

    // (NEW) every live bolt registers here so the HUD can scan for shots on a collision course with the local player
    internal static readonly List<EnemyBolt> All = new();

    // (NEW) exact straight-line threat test vs a point (the local player). Returns time-to-impact + closest miss distance.
    public bool ThreatTo(Vector3 pPos, out float tti, out float miss)
    {
        tti = 0f; miss = 999f;
        float sp2 = Vel.LengthSquared();
        if (sp2 < 0.01f) return false;
        Vector3 rel = (pPos + new Vector3(0, 1.2f, 0)) - GlobalPosition;
        float tc = rel.Dot(Vel) / sp2;                 // time of closest approach along the straight path
        if (tc < 0f) return false;                     // moving away / already passed the player
        float look = Mathf.Min(Life, 1.6f);
        if (tc > look) return false;                   // too far out to matter yet
        miss = (rel - Vel * tc).Length();
        tti = tc;
        return miss < Radius + 1.5f;                   // on a collision course with the player's capsule
    }

    // perf: enemy-bolt lights draw from the shared global cap so a caster-heavy jungle can't spawn dozens of lights
    private void AddBoltLight(Color col, float range, float energy)
    {
        if (!Game.DynLightRoom) return;
        Game.DynLightAdd(); _litReg = true;
        AddChild(new OmniLight3D { OmniRange = range, LightColor = col, LightEnergy = energy });
    }

    public override void _Ready()
    {
        Live++;
        All.Add(this);
        if (Radius <= 0.4f)   // (NEW) small projectiles (blowdarts / needles) render as an oriented DART instead of an orb
        {
            var shaft = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.03f, BottomRadius = 0.08f, Height = 0.85f }, MaterialOverride = Game.ToonEmissive(Tint, 1.4f, 0.02f) };
            shaft.RotationDegrees = new Vector3(90, 0, 0);   // lie along local Z so LookAt aligns it with the flight direction
            AddChild(shaft);
            var fl = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.2f, 0.02f, 0.14f) }, MaterialOverride = Game.Toon(new Color(0.25f, 0.5f, 0.28f), 0.9f, 0.2f, 0f) };
            fl.Position = new Vector3(0, 0, 0.42f); AddChild(fl);   // green fletching at the back
            if (Vel.LengthSquared() > 0.01f) LookAt(GlobalPosition + Vel, Vector3.Up);   // point it down its flight path
            AddBoltLight(Tint, 3f, 0.8f);
        }
        else
        {
            // (PHASE 3) larger caster bolts read as an oriented molten SHARD streak (not a round orb) — a clearer incoming threat
            float vr = 0.3f + Radius * 0.3f;
            var mi = new MeshInstance3D
            {
                Mesh = new SphereMesh { Radius = vr, Height = vr * 2f },
                Scale = new Vector3(0.85f, 0.85f, 1.8f),   // stretch along local Z (flight axis via LookAt)
                MaterialOverride = Game.ToonEmissive(Tint, 1.2f, 0.03f)
            };
            AddChild(mi);
            AddChild(new MeshInstance3D
            {
                Mesh = new SphereMesh { Radius = vr * 0.5f, Height = vr },
                Position = new Vector3(0, 0, -vr * 0.9f),   // bright leading head
                MaterialOverride = Game.Emissive(Tint.Lerp(Colors.White, 0.3f), 2.5f),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
            });
            if (Vel.LengthSquared() > 0.01f)
            {
                Vector3 up = Mathf.Abs(Vel.Normalized().Y) > 0.95f ? Vector3.Forward : Vector3.Up;
                LookAt(GlobalPosition + Vel, up);   // point the shard down its flight path (-Z leads)
            }
            AddBoltLight(Tint, 5f, 1.2f);
        }
    }

    public override void _ExitTree() { Live--; All.Remove(this); if (_litReg) { Game.DynLightRemove(); _litReg = false; } }

    public override void _Process(double delta)
    {
        if (Game.I == null || !Game.I.SimActive) return;
        float dt = (float)delta;
        GlobalPosition += Vel * dt;
        Life -= dt;

        var p = Game.I.Player;

        // (NEW) a spatial "incoming" whistle the moment a shot on a collision course gets close — panned from its direction
        if (!_warned && !HitsEnemies && p != null && ThreatTo(p.GlobalPosition, out float _tti, out float _miss) && _tti < 0.85f && _tti > 0.04f)   // (PUPPET) a turned foe's shot can't hurt you — don't cry incoming
        { _warned = true; Game.I.Sfx?.Incoming(GlobalPosition); }

        if (Remote)
        {
            // client visual: pop when it reaches my own avatar (the host applies the real damage)
            if (p != null && GlobalPosition.DistanceTo(p.GlobalPosition + new Vector3(0, 1.4f, 0)) < Radius + 1.1f) { QueueFree(); return; }
            foreach (var bl in Game.I.Blockers)
                if (new Vector2(GlobalPosition.X - bl.Pos.X, GlobalPosition.Z - bl.Pos.Z).Length() < bl.Radius) { QueueFree(); return; }
            if (HitsDeck()) { QueueFree(); return; }
            if (Life <= 0f) QueueFree();
            return;
        }

        // (PUPPET) a turned foe's shot: hunt enemies, never the witches. Runs before every player-facing check below so a
        // puppet's bolt can't clip a warden on its way across, and skips the wind/fire wards too — those guard the player.
        if (HitsEnemies)
        {
            foreach (var e in Game.I.Enemies.ToArray())
            {
                if (e == null || e == Shooter || e.Dead || e.Remote || !GodotObject.IsInstanceValid(e)) continue;
                if (GlobalPosition.DistanceTo(e.GlobalPosition + new Vector3(0, e.Radius * 0.6f, 0)) > Radius + e.Radius) continue;
                e.PuppetHurt(OwnerPeer, Dmg);
                QueueFree();
                return;
            }
            foreach (var bl in Game.I.Blockers)
                if (new Vector2(GlobalPosition.X - bl.Pos.X, GlobalPosition.Z - bl.Pos.Z).Length() < bl.Radius) { QueueFree(); return; }
            if (HitsDeck() || Life <= 0f) QueueFree();
            return;
        }

        // (NEW) Ring of Fire eats incoming enemy projectiles — puff + crackle, damage negated
        foreach (var fr in Game.I.FireRings)
            if (new Vector2(GlobalPosition.X - fr.Pos.X, GlobalPosition.Z - fr.Pos.Z).Length() < fr.Radius)
            { Game.I.SpawnEmberBurst(GlobalPosition, 1.3f); Game.I.Sfx?.Impact(DamageType.Ember); QueueFree(); return; }

        // (NEW) the Cyclone's swirling wall eats incoming enemy projectiles — swept up with a whish, damage negated
        foreach (var wr in Game.I.WindRings)
            if (new Vector2(GlobalPosition.X - wr.Pos.X, GlobalPosition.Z - wr.Pos.Z).Length() < wr.Radius)
            { Game.I.SpawnWindPuff(GlobalPosition); Game.I.Sfx?.Whish(GlobalPosition); QueueFree(); return; }

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
        if (HitsDeck()) { QueueFree(); return; }   // (NEW) splat on structure walls instead of passing through

        float far = p != null ? GlobalPosition.DistanceTo(p.GlobalPosition) : 0f;
        if (Life <= 0 || far > 95f) QueueFree();
    }

    // true if this bolt is inside a structure wall (Deck) — flies over low pads and over the wall top
    private bool HitsDeck()
    {
        foreach (var d in Game.I.Decks)
        {
            if (d.TopY < 1.8f || GlobalPosition.Y >= d.TopY) continue;
            if (Mathf.Abs(GlobalPosition.X - d.Center.X) < d.Half.X && Mathf.Abs(GlobalPosition.Z - d.Center.Z) < d.Half.Y) return true;
        }
        return false;
    }
}
