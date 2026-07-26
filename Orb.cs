using Godot;

// Orb.cs — a floating magic pickup mote (XP). It reads as a witchy faceted arcane crystal that spins and glows in
// its element colour; the more XP it carries, the bigger and more ornate it gets (extra orbiting rune motes + a
// halo, and a rune ring on the richest drops). RemotePickup mirrors it for clients.
public partial class Orb : Node3D
{
    public float Xp = 10f;
    public float Life = 22f;
    public Color Tint = Palette.Ember;
    public int NetId = 0;
    public bool Remote = false;
    private float _bob;
    private Node3D _spin;          // the crystal + its motes; spins in _Process
    private OmniLight3D _light;    // (PERF) culled by Game.CullOrbLights — only the nearest few stay lit
    private bool _pulling = false; // (NEW) latched once magnetized — keeps streaking to the player even if they step away
    private float _pullSpeed = 0f; // ramps up from rest so you SEE the orb accelerate in, instead of it vanishing

    // richness tier from the XP the orb carries: 0 = a small speck, 1 = a gem + motes, 2 = an ornate rune-ringed crystal
    private int Tier => Xp >= 45f ? 2 : (Xp >= 16f ? 1 : 0);

    public override void _Ready()
    {
        _bob = (float)GD.RandRange(0, 6.28);
        int tier = Tier;
        float s = 0.22f + tier * 0.12f;                     // bigger crystal for more XP
        _spin = new Node3D(); AddChild(_spin);
        var gemCol = Tint.Lerp(Colors.White, 0.25f);
        var gem = Game.Emissive(gemCol, 2.2f + tier * 0.7f);

        // faceted arcane crystal — a diamond bipyramid (two 5-sided cones tip-to-tip)
        var top = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = s, Height = s * 1.7f, RadialSegments = 5 }, MaterialOverride = gem, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        top.Position = new Vector3(0, s * 0.85f, 0);
        var bot = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = s, Height = s * 1.7f, RadialSegments = 5 }, MaterialOverride = gem, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        bot.RotationDegrees = new Vector3(180, 0, 0); bot.Position = new Vector3(0, -s * 0.85f, 0);
        _spin.AddChild(top); _spin.AddChild(bot);

        if (tier >= 1)   // a soft translucent halo + orbiting rune motes (kept off tiny specks for fill-rate)
        {
            var halo = new MeshInstance3D { Mesh = new SphereMesh { Radius = s * 1.8f, Height = s * 3.6f }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            halo.MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(Tint.R, Tint.G, Tint.B, 0.16f), EmissionEnabled = true, Emission = Tint, EmissionEnergyMultiplier = 1.1f, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
            _spin.AddChild(halo);
            int motes = tier == 2 ? 3 : 2;
            for (int i = 0; i < motes; i++)
            {
                var pivot = new Node3D { RotationDegrees = new Vector3(0, i / (float)motes * 360f, 0) };
                float ms = 0.08f * (1f + tier * 0.35f);
                var mote = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(ms, ms, ms) }, MaterialOverride = gem, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
                mote.RotationDegrees = new Vector3(45, 0, 45);
                mote.Position = new Vector3(s * 2.2f, 0, 0);
                pivot.AddChild(mote); _spin.AddChild(pivot);
            }
        }
        if (tier == 2)   // a slow rune ring around the richest drops
        {
            var ring = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = s * 2.0f, OuterRadius = s * 2.3f, RingSegments = 6, Rings = 4 }, MaterialOverride = gem, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            ring.RotationDegrees = new Vector3(90, 0, 0); _spin.AddChild(ring);
        }

        _light = new OmniLight3D { OmniRange = 2.6f + tier * 1.2f, LightColor = Tint, LightEnergy = 0.7f + tier * 0.4f };
        AddChild(_light);
    }

    // (PERF) light budget: distant orbs still glow from their emissive mesh, they just stop casting a real-time OmniLight
    public void SetLit(bool on) { if (_light != null && GodotObject.IsInstanceValid(_light) && _light.Visible != on) _light.Visible = on; }

    public override void _Process(double delta)
    {
        if (Game.I == null || !Game.I.WorldRunning || !Game.I.SimActive) return;   // freeze while paused (no mid-menu pickups) (NEW)
        float dt = (float)delta;
        _bob += dt * 3f;
        if (Remote) { _spin.RotateY(dt * 2f); return; }   // client ghost: host drives it; position comes from the snapshot

        // orbs PERSIST (no despawn) and idle-bob until a warden comes within PickupRange — or a chest lodestone (magnet) is
        // active, which vacuums every orb on the map. Either way the orb then MAGNETIZES: it accelerates from rest and
        // visibly streaks to the nearest player, collecting (XP shared to everyone) only when it actually reaches them.
        Vector3 pp = Game.I.ResolveEnemyTarget(GlobalPosition, false, out long _, out bool _);
        float d = new Vector2(GlobalPosition.X - pp.X, GlobalPosition.Z - pp.Z).Length();
        bool magnet = Game.I.MagnetActive;
        if (magnet || d < Game.I.PickupRange) _pulling = true;   // in range (or lodestone) → start the pull; latched so it always completes

        if (_pulling)
        {
            Vector3 target = pp + Vector3.Up * 1.0f;              // fly to the player's chest (follows them onto ledges/air)
            Vector3 to = target - GlobalPosition;
            float dist = to.Length();
            if (dist < 1.3f) { Game.I.GrantSharedXp(Xp); QueueFree(); return; }   // reached the player → collect
            _pullSpeed = Mathf.MoveToward(_pullSpeed, magnet ? 42f : 30f, dt * (magnet ? 70f : 55f));   // ramp up → a visible streak, not a snap
            GlobalPosition += to / dist * Mathf.Min(_pullSpeed * dt, dist);
            _spin.RotateY(dt * 9f);                               // spin up as it zooms in
            return;
        }

        // idle: a low, small persistent crystal bobbing above the (hilly) ground until a warden comes near
        _spin.RotateY(dt * 2f);
        var here = GlobalPosition;
        here.Y = Game.I.SurfaceHeight(GlobalPosition, 1e9f) + 0.7f + Mathf.Sin(_bob) * 0.15f;
        GlobalPosition = here;
    }
}
