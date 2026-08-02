using Godot;

// HauntBolt.cs — a single lightning strike inside a Haunt. The storm overhead is no longer just scenery: it
// picks spots inside the zone and hurls bolts at them. A strike telegraphs as a PURPLE ground circle ringed in
// RED, then a forked bolt falls and everything caught in the circle is hurt AND stunned — foes and wardens
// alike. Fully avoidable, so the cost of standing still in a Haunt is real, and baiting the horde under a
// telegraph is a genuine play.
//
// Each bolt is independent and short-lived; Game.UpdateHauntBolts owns how many exist and how often they land.
// Host simulates the damage; client copies are visual-only (Remote), exactly like ArrowVolley / PestilencePool.
public partial class HauntBolt : Node3D
{
    public float Radius = 5.5f;
    public float Telegraph = 1.15f;   // warning window — long enough to walk out of, short enough to respect
    public float PlayerDmg = 14f;
    public float PlayerStun = 0.85f;
    public float EnemyDmg = 26f;
    public float EnemyStun = 1.6f;
    public bool Remote = false;

    // Where the bolt forks down from. Deliberately LOW: the Haunt's cyclone is an additive, depth-write-free cone ~18m
    // tall and ~34m wide at the top, centred on the zone. A bolt dropping from the cloud deck (46m) spends almost its
    // whole length inside that haze and washes out to nothing — which is exactly what the first two passes did. Starting
    // near the ground keeps the shaft in clear air for most strikes, and near the heart being half-swallowed by the
    // funnel is honest anyway: you are standing inside a tornado.
    private const float SkyY = 15f;

    public int DebugArcSegments => _bolt != null ? _bolt.GetChildCount() : 0;   // (HARNESS) proves the arc geometry exists
    public bool DebugStruck => _struck;

    private float _t = 0f;
    private bool _struck = false;
    private Decal _decal;
    private MeshInstance3D _ring, _closer;
    private Node3D _bolt;
    private OmniLight3D _light;

    private static readonly Color Purple = new Color(0.60f, 0.26f, 0.92f);   // the fill — cursed violet, matches the Haunt
    private static readonly Color Rim    = new Color(1f, 0.18f, 0.20f);      // the outline — danger red
    private static readonly Color Arc    = new Color(0.86f, 0.92f, 1f);      // the bolt itself

    public void Init(Vector3 pos, float radius)
    {
        Radius = radius;
        float gy = Game.I != null ? Game.I.SurfaceHeight(pos, 1e9f) : 0f;
        GlobalPosition = new Vector3(pos.X, gy, pos.Z);

        // PURPLE fill — a projected decal so it conforms to the Haunt's uneven ground instead of clipping through it
        _decal = new Decal
        {
            TextureAlbedo = Game.FieldTex(),
            TextureEmission = Game.FieldTex(),
            EmissionEnergy = 2.4f,
            Modulate = new Color(Purple.R, Purple.G, Purple.B, 0.45f),
            Size = new Vector3(radius * 2f, 12f, radius * 2f),
            AlbedoMix = 0.9f,
        };
        _decal.Position = new Vector3(0, 5f, 0);
        AddChild(_decal);

        // RED outline — a hard rim so the edge of the strike is unambiguous at a glance. Emission is kept LOW on purpose:
        // Game.Emissive multiplies energy by 1.2 and anything past ~2 blooms this red straight to white, which is how the
        // first pass lost the red outline entirely.
        _ring = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = radius * 0.91f, OuterRadius = radius, Rings = 28, RingSegments = 6 } };
        _ring.MaterialOverride = Game.Emissive(Rim, 1.3f);
        _ring.Position = new Vector3(0, 0.14f, 0);
        _ring.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        AddChild(_ring);

        // CLOSING ring — collapses from well outside the circle onto the rim across the telegraph. The Haunt's own floor is
        // already violet with a red rim on the zone edge, so a static purple disc drawn on it barely separates; MOTION is
        // what actually reads against that background, and its radius doubles as the countdown.
        _closer = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = 0.90f, OuterRadius = 1f, Rings = 26, RingSegments = 6 } };
        _closer.MaterialOverride = Game.Emissive(Rim, 1.6f);
        _closer.Position = new Vector3(0, 0.5f, 0);
        _closer.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        AddChild(_closer);

        _light = new OmniLight3D { OmniRange = radius * 2.4f, LightColor = Purple, LightEnergy = 1.2f, ShadowEnabled = false, Position = new Vector3(0, 3f, 0) };
        AddChild(_light);
    }

    public override void _Process(double delta)
    {
        var g = Game.I; if (g == null || !g.SimActive) return;
        float dt = (float)delta; _t += dt;

        if (_t < Telegraph)
        {
            // WARNING — nothing here hurts yet. The pulse accelerates as the bolt closes, so the urgency reads
            // without a timer (same language as the Phalanx volley: quickening flash = it lands NOW).
            float k = _t / Telegraph;
            float pulse = 0.35f + 0.65f * Mathf.Abs(Mathf.Sin(k * k * 24f));
            if (_decal != null) _decal.Modulate = new Color(Purple.R, Purple.G, Purple.B, 0.34f + 0.50f * pulse);
            if (_ring != null) { _ring.Scale = Vector3.One * (0.97f + 0.05f * pulse); _ring.MaterialOverride = Game.Emissive(Rim, 0.9f + 1.1f * pulse); }
            if (_closer != null)
            {
                float cr = Mathf.Lerp(Radius * 1.75f, Radius, k * k);   // eases in fast, then settles precisely onto the rim
                _closer.Scale = new Vector3(cr, 1f, cr);
                _closer.Visible = true;
            }
            if (_light != null) _light.LightEnergy = 0.5f + 2.0f * pulse;
            return;
        }

        if (!_struck) { _struck = true; if (_closer != null) _closer.Visible = false; Strike(); }

        // afterglow: the bolt flickers out over ~0.45s, then the whole thing goes away
        float life = _t - Telegraph;
        // hold the arc long enough to actually be seen at 60fps (a 0.16s flash read as nothing), with one flicker gap
        if (_bolt != null) _bolt.Visible = life < 0.30f && !(life > 0.15f && life < 0.19f);
        if (_decal != null) _decal.Modulate = new Color(1f, 1f, 1f, Mathf.Max(0f, 0.85f - life * 2.0f));
        if (_light != null) _light.LightEnergy = Mathf.Max(0f, 10f - life * 26f);
        if (_ring != null) { float s = 1f + life * 1.6f; _ring.Scale = new Vector3(s, 1f, s); _ring.MaterialOverride = Game.Emissive(Rim, Mathf.Max(0f, 3f - life * 7f)); }
        if (life > 0.55f) QueueFree();
    }

    private void Strike()
    {
        BuildArc();
        if (_light != null) { _light.LightColor = Arc; _light.LightEnergy = 10f; }
        if (_decal != null) _decal.Modulate = new Color(1f, 1f, 1f, 0.85f);   // flashes white on contact
        var g = Game.I;
        g.Sfx?.StormThunder(GlobalPosition + Vector3.Up * 2f, 1.05f + GD.Randf() * 0.35f);
        g.SpawnArcaneRupture(GlobalPosition + Vector3.Up * 0.3f, Radius * 0.7f);
        g.VfxRing(GlobalPosition, Arc, Radius * 2.2f, 0.35f);            // hard white shock ring off the impact point
        g.VfxRing(GlobalPosition, Purple, Radius * 1.3f, 0.5f);

        if (Remote) return;   // clients render the strike; the host owns every point of damage

        // WARDENS — one instant hit + stun, as a single i-frame-gated decision (see Net.HauntBoltPlayersIn)
        g.NetMgr?.HauntBoltPlayersIn(GlobalPosition, Radius, PlayerDmg, PlayerStun);

        // FOES — same circle, harder hit, longer stun. Bosses take the damage but shrug off the stun; ambient
        // weather must never lock a boss out of its fight.
        foreach (var e in g.Enemies.ToArray())
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e) || e.Remote) continue;
            var d = e.GlobalPosition - GlobalPosition; d.Y = 0f;
            if (d.Length() > Radius + e.Radius) continue;
            e.Hurt(EnemyDmg, DamageType.Arcane, false, false);
            if (!e.IsBoss && !e.Dead) e.Shock(EnemyStun);
        }
    }

    // a jagged fork from the cloud deck down to the circle (same construction as Haunt.Strike: Z-length boxes aimed
    // with LookAt), plus a couple of short branches so it reads as real lightning rather than a straight pole
    // The bolt is drawn TWICE: a fat translucent violet sheath and a thin white-hot core inside it. A single thin white
    // trunk (the first pass) was invisible in play — 0.34u of white read as a couple of pixels against the Haunt's
    // blown-out sky, so the strike landed with damage numbers and no lightning. The sheath is what actually sells it.
    private void BuildArc()
    {
        _bolt = new Node3D(); AddChild(_bolt);
        var core = Game.Emissive(Arc, 7f);
        var sheath = new StandardMaterial3D {
            AlbedoColor = new Color(Purple.R, Purple.G, Purple.B, 0.5f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            EmissionEnabled = true, Emission = Purple.Lerp(Colors.White, 0.35f), EmissionEnergyMultiplier = 2.6f };
        var rng = new RandomNumberGenerator { Seed = (ulong)Mathf.RoundToInt(GlobalPosition.X * 31f + GlobalPosition.Z * 17f) + 7 };

        // a and b arrive as offsets from the strike point. LookAtFromPosition works in GLOBAL space, so they MUST be
        // converted first — feeding it local offsets silently relocates every segment to those coordinates near the world
        // origin, which is why the first passes produced 22 segments of geometry and no lightning anywhere near the circle.
        Vector3 origin = GlobalPosition;
        void Seg(Vector3 a, Vector3 b, float thick)
        {
            Vector3 ga = origin + a, gb = origin + b;
            var mid = (ga + gb) * 0.5f; float len = (gb - ga).Length();
            if (len < 0.05f) return;
            var up = Mathf.Abs((gb - ga).Normalized().Dot(Vector3.Up)) > 0.98f ? Vector3.Forward : Vector3.Up;
            foreach (var (t, m) in new[] { (thick * 3.2f, sheath), (thick, (Material)core) })
            {
                var s = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(t, t, len) }, MaterialOverride = m, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
                _bolt.AddChild(s);
                s.LookAtFromPosition(mid, gb, up);   // -Z spans a→b, so the box's Z length lies along the segment
            }
        }

        const int segs = 8;
        Vector3 p = new Vector3(rng.RandfRange(-2.5f, 2.5f), SkyY, rng.RandfRange(-2.5f, 2.5f));
        for (int i = 0; i < segs; i++)
        {
            float f = (i + 1) / (float)segs;
            // converge on the circle's centre as it descends, so the bolt visibly terminates where the damage is
            var next = new Vector3(Mathf.Lerp(p.X, 0f, f) + rng.RandfRange(-1.6f, 1.6f) * (1f - f),
                                   SkyY * (1f - f),
                                   Mathf.Lerp(p.Z, 0f, f) + rng.RandfRange(-1.6f, 1.6f) * (1f - f));
            if (i == segs - 1) next = Vector3.Zero;
            Seg(p, next, 1.15f);
            if (i > 1 && i < segs - 1 && rng.Randf() < 0.4f)   // a short dead-end fork
            {
                var mid = (p + next) * 0.5f;
                Seg(mid, mid + new Vector3(rng.RandfRange(-2.5f, 2.5f), -1.6f, rng.RandfRange(-2.5f, 2.5f)), 0.55f);
            }
            p = next;
        }
    }
}
