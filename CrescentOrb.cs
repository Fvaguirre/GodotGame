using Godot;
using System.Collections.Generic;

// Crescent Moon ultimate blade. Orbits the witch for the ult's duration; can be flung at the cursor
// and boomerangs back to re-orbit, repeatable. Persists through enemies (no vanish on impact).
// CrescentOrb.cs — the orbiting crescent blades of the Lunar 'Crescent' ultimate. Owner-simulated; synced to allies as ghost orbs.
public partial class CrescentOrb : Node3D
{
    public float Angle = 0f;
    public float OrbitR = 4.5f;
    public float Dmg = 45f;
    public Node3D OrbitCenter = null;   // remote ghost: orbit this ally avatar instead of the local player
    public bool Remote = false;         // remote ghost: visual only, no damage
    public float Life = 0f;             // remote ghost: auto-expire after the ult duration
    public Vector3 GhostTarget;         // remote ghost: the owner's real orb position (from the snapshot)
    public void SetGhostTarget(Vector3 t) => GhostTarget = t;

    public int Mode = 0;             // 0 = orbit player, 1 = forward cluster, 2 = orbit-in-place (owner-controlled)
    public Vector3 Center;           // owner-provided orbit center for modes 1 & 2
    public float OrbitRadius = 4.5f;
    public float SpinRate = 2.2f;
    private readonly Dictionary<ulong, float> _hitCd = new();

    public void SetControl(int mode, Vector3 center, float radius, float spin)
    { Mode = mode; Center = center; OrbitRadius = radius; SpinRate = spin; }

    public override void _Ready()
    {
        // build an actual crescent: a tapered arc of glowing segments forming a C (not a full ring)
        var mat = Game.ToonEmissive(DamageTypes.Col(DamageType.Lunar), 1.5f, 0.02f);
        int seg = 9;
        float span = Mathf.DegToRad(210f);
        for (int i = 0; i < seg; i++)
        {
            float t = i / (float)(seg - 1);          // 0..1 along the arc
            float a = -span / 2f + span * t;
            float taper = 0.18f + 0.42f * Mathf.Sin(t * Mathf.Pi);   // fat belly, thin cusps
            var s = new MeshInstance3D { Mesh = new SphereMesh { Radius = taper, Height = taper * 2f } };
            s.MaterialOverride = mat;
            s.Position = new Vector3(Mathf.Cos(a) * 0.8f, Mathf.Sin(a) * 0.8f, 0);
            AddChild(s);
        }
        AddChild(new OmniLight3D { OmniRange = 5f, LightColor = DamageTypes.Col(DamageType.Lunar), LightEnergy = 1.2f });
    }

    public void Fire(Vector3 dir) { }   // legacy no-op — the Q-fling was replaced by LMB/RMB control

    public override void _Process(double delta)
    {
        if (Game.I == null || Game.I.State != GameState.Playing) return;
        float dt = (float)delta;
        if (Remote)   // ghost: follow the owner's real positions (works for orbit, fling, and rotate-in-place alike)
        {
            if (Life > 0f) { Life -= dt; if (Life <= 0f) { QueueFree(); return; } }
            GlobalPosition = GlobalPosition.Lerp(GhostTarget, Mathf.Clamp(dt * 14f, 0f, 1f));
            RotateY(dt * 5f); RotateZ(dt * 2.5f);
            return;
        }

        Vector3 ctr;
        float r = OrbitRadius, spin = SpinRate;
        if (Mode == 0) { var pp = Game.I.Player; if (pp == null) return; ctr = pp.GlobalPosition; }
        else ctr = Center;                                         // forward cluster / in-place: owner supplies the center

        Angle += dt * spin;
        var target = new Vector3(ctr.X + Mathf.Cos(Angle) * r, ctr.Y + 1.4f, ctr.Z + Mathf.Sin(Angle) * r);
        GlobalPosition = GlobalPosition.Lerp(target, Mathf.Clamp(dt * 8f, 0f, 1f));   // smooth follow → forward push & return-to-player glide naturally
        RotateY(dt * 5f);
        RotateZ(dt * 2.5f);

        if (!Remote)
        {
            foreach (var e in Game.I.Enemies.ToArray())   // (FIX) snapshot — hits can mutate the live list
            {
                if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
                if (GlobalPosition.DistanceTo(e.GlobalPosition) < e.Radius + 1.1f)
                {
                    ulong id = e.GetInstanceId();
                    float now = (float)Time.GetTicksMsec() / 1000f;
                    if (!_hitCd.TryGetValue(id, out float t) || now >= t)
                    {
                        _hitCd[id] = now + 0.4f;
                        e.Hurt(Dmg, DamageType.Lunar, false); e.HitFrom(GlobalPosition);
                        Game.I.Player?.ComboFromSource();
                        Game.I.Sfx?.Impact(DamageType.Lunar);
                    }
                }
            }
            Game.I.DamageWorld(GlobalPosition, 1.4f, Dmg);   // (NEW) the orb smashes props it sweeps over
        }
    }
}
