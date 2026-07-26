using Godot;

// ArrowVolley.cs — the Warded Phalanx's massed volley. The archers loose a arcing salvo at whoever the
// formation is targeting; it lands as a telegraphed circle, then rains arcane arrows into that circle for a
// few seconds. Standing in it ticks damage, and carrying yourself out of it leaves a lingering VENOM dot
// (Blessed purges it instantly). Both the tick damage and the venom scale with how many archers are still
// in the formation. Host simulates the damage; client copies are visual-only (Remote), like PestilencePool.
public partial class ArrowVolley : Node3D
{
    public float Radius = 7f;
    public float Telegraph = 1.0f;    // RED warning window — your chance to walk out before anything lands
    public float Dur = 2.6f;          // seconds of arrow rain
    public float Dps = 12f;           // damage/sec to anyone standing inside
    public float VenomDps = 2f;       // lingering poison dealt AFTER you leave
    public float VenomDur = 5f;
    public bool Remote = false;

    // The shafts are spawned above the circle and fall under gravity, so if we started them the instant damage began the
    // visual would land ~a fifth of a second LATE and the circle would hurt before anything visibly hit it. Emission is
    // therefore lead in by exactly the drop time: shafts strike the ground on the same frame the damage starts.
    private const float DropHeight = 9f;
    private const float ArrowLead = 0.20f;

    private float _t = 0f, _tick = 0f;
    private bool _impacted = false;
    private Decal _decal;
    private MeshInstance3D _ring;
    private GpuParticles3D _rain;
    private OmniLight3D _light;
    private static readonly Color WarnCol = new Color(1f, 0.20f, 0.22f);        // DANGER red while it's only a threat
    private static readonly Color VolleyCol = new Color(0.62f, 0.42f, 0.95f);   // venom violet once it's actually raining

    public void Init(Vector3 pos, float radius, float dps, float venomDps)
    {
        Radius = radius; Dps = dps; VenomDps = venomDps;
        GlobalPosition = new Vector3(pos.X, 0f, pos.Z);

        // ground circle — a projected decal so it conforms to terrain instead of clipping through hills
        _decal = new Decal
        {
            TextureAlbedo = Game.FieldTex(),
            TextureEmission = Game.FieldTex(),
            EmissionEnergy = 2.2f,
            Modulate = new Color(WarnCol.R, WarnCol.G, WarnCol.B, 0.55f),   // starts RED — "something is going to land here"
            Size = new Vector3(radius * 2f, 14f, radius * 2f),
            AlbedoMix = 0.9f,
        };
        _decal.Position = new Vector3(0, 5f, 0);
        AddChild(_decal);

        // hard danger rim so the edge of the kill circle is unambiguous
        _ring = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = radius * 0.95f, OuterRadius = radius } };
        _ring.MaterialOverride = Game.Emissive(WarnCol, 3.2f);
        _ring.Position = new Vector3(0, 0.12f, 0);
        AddChild(_ring);

        _light = new OmniLight3D { OmniRange = radius * 2.2f, LightColor = WarnCol, LightEnergy = 1.4f };
        _light.Position = new Vector3(0, 3f, 0);
        AddChild(_light);

        BuildRain(radius);
        _rain.Emitting = false;   // held until the telegraph elapses
    }

    // falling arrows: thin fletched shafts spawned across the disc, dropped fast under gravity
    private void BuildRain(float radius)
    {
        var shaft = new BoxMesh { Size = new Vector3(0.09f, 1.5f, 0.09f) };
        var mat = Game.ToonEmissive(VolleyCol, 2.4f, 0f);
        mat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        shaft.Material = mat;

        var pm = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
            EmissionBoxExtents = new Vector3(radius * 0.92f, 0.4f, radius * 0.92f),
            Direction = Vector3.Down,
            Spread = 2f,
            Gravity = new Vector3(0, -60f, 0),
            InitialVelocityMin = 38f,
            InitialVelocityMax = 42f,
            ScaleMin = 0.8f,
            ScaleMax = 1.5f,
            Color = VolleyCol,
        };
        _rain = new GpuParticles3D
        {
            Amount = Mathf.Clamp(Mathf.RoundToInt(radius * 14f), 40, 160),
            Lifetime = 0.7f,
            DrawPass1 = shaft,
            ProcessMaterial = pm,
            Explosiveness = 0f,
            LocalCoords = false,
        };
        _rain.Position = new Vector3(0, DropHeight, 0);   // low enough that the shafts land within ArrowLead of release
        AddChild(_rain);
    }

    public override void _Process(double delta)
    {
        if (Game.I == null) return;
        float dt = (float)delta;
        if (!Game.I.SimActive) return;
        _t += dt;

        // release the shafts slightly EARLY so they're mid-flight and strike exactly as the circle goes live
        if (_rain != null && !_rain.Emitting && _t >= Telegraph - ArrowLead) _rain.Emitting = true;

        if (_t < Telegraph)
        {
            // WARNING PHASE — pure red, no fill: the circle is only a threat, nothing here hurts yet. It pulses faster
            // and faster as the shafts close, so the urgency is readable without reading a timer.
            float k = _t / Telegraph;
            float pulse = 0.35f + 0.65f * Mathf.Abs(Mathf.Sin(k * k * 26f));
            if (_decal != null) _decal.Modulate = new Color(WarnCol.R, WarnCol.G, WarnCol.B, 0.16f + 0.34f * pulse);
            if (_ring != null) { _ring.Scale = Vector3.One * (0.96f + 0.06f * pulse); _ring.MaterialOverride = Game.Emissive(WarnCol, 2.0f + 2.4f * pulse); }
            if (_light != null) { _light.LightColor = WarnCol; _light.LightEnergy = 0.6f + 2.2f * pulse; }
            return;
        }
        if (!_impacted)
        {
            // IMPACT — the circle floods venom-violet on the same frame the first shafts hit and the damage starts.
            // Red = "about to", violet = "now". No ambiguity about when the ground turns lethal.
            _impacted = true;
            Game.I.Sfx?.Impact(DamageType.Arcane);
            if (_decal != null) _decal.Modulate = new Color(VolleyCol.R, VolleyCol.G, VolleyCol.B, 0.92f);
            if (_ring != null) { _ring.MaterialOverride = Game.Emissive(VolleyCol.Lerp(Colors.White, 0.25f), 3.2f); _ring.Scale = Vector3.One; }
            if (_light != null) { _light.LightColor = VolleyCol; _light.LightEnergy = 2.2f; }
        }

        float life = _t - Telegraph;
        if (life >= Dur)
        {
            if (_rain != null) _rain.Emitting = false;
            if (_decal != null) _decal.Modulate = new Color(VolleyCol.R, VolleyCol.G, VolleyCol.B, Mathf.Max(0f, 0.9f - (life - Dur) * 1.6f));
            if (_light != null) _light.LightEnergy = Mathf.Max(0f, 2f - (life - Dur) * 4f);
            if (life > Dur + 0.8f) QueueFree();
            return;
        }
        if (_ring != null) _ring.RotationDegrees = new Vector3(0, life * 90f, 0);
        if (Remote) return;   // clients: visual only — the host owns the damage

        _tick -= dt;
        if (_tick > 0f) return;
        _tick = 0.35f;
        Game.I.NetMgr?.HurtPlayersIn(GlobalPosition, Radius, Dps * 0.35f);
        Game.I.NetMgr?.VenomPlayersIn(GlobalPosition, Radius, VenomDur, VenomDps);   // refreshed while inside; only bites once you leave
    }
}
