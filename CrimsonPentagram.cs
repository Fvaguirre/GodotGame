using Godot;

// CrimsonPentagram.cs — the Crimson Rite's payoff, centred on the world boss. It DRAWS ITSELF in real time: the enclosing
// circle sweeps around, then the five chords of the star are struck one by one, each stroke racing out from point to point.
// When the last stroke lands the whole figure flares white-hot and Burst() dispels it into an expanding shockwave.
// Pure visual + self-timed: the host runs the same clock in Game.UpdateCrimsonRite and does the actual killing at DrawDur.
// Clients spawn their own copy from one RPC and it bursts locally, so a dropped packet can't leave a pentagram hanging.
public partial class CrimsonPentagram : Node3D
{
    public const float DrawDur = 3.4f;      // circle sweep + five strokes; the detonation lands exactly here
    private const float CircleDur = 1.25f;  // the enclosing circle traces first
    private const float StrokeDur = 0.40f;  // then each of the five chords
    public float Radius = 26f;

    private static readonly Color Col = new Color(0.95f, 0.10f, 0.16f);
    private float _t;
    private bool _burst = false, _autoBurst = false;
    private readonly Node3D[] _strokes = new Node3D[5];          // each: a bright ground line + a tall light curtain above it
    private readonly MeshInstance3D[] _pillars = new MeshInstance3D[5];
    private readonly Vector3[] _pts = new Vector3[5];
    private const int ArcSegs = 60;
    private readonly Node3D[] _arc = new Node3D[ArcSegs];
    private StandardMaterial3D _mat, _glowMat, _curtainMat;
    private OmniLight3D _light;
    private const float ChordWallH = 11f, RingWallH = 4.5f, PillarH = 20f;

    // autoBurst: clients self-detonate the visual; the host lets Game call Burst() so the flash lands with the kills
    public void Init(float radius, bool autoBurst)
    {
        Radius = Mathf.Max(8f, radius);
        _autoBurst = autoBurst;
    }

    public override void _Ready()
    {
        _mat = new StandardMaterial3D { AlbedoColor = Col, EmissionEnabled = true, Emission = Col, EmissionEnergyMultiplier = 4.5f, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, BlendMode = BaseMaterial3D.BlendModeEnum.Add, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
        _glowMat = new StandardMaterial3D { AlbedoColor = new Color(Col.R, Col.G, Col.B, 0.10f), EmissionEnabled = true, Emission = Col, EmissionEnergyMultiplier = 1.6f, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, BlendMode = BaseMaterial3D.BlendModeEnum.Add, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
        // A 52u figure lying flat on the ground is nearly edge-on from a 1.6u eye height — it reads as stray red streaks, not a
        // pentagram. So every line also throws a translucent CURTAIN of blood-light upward: from the ground you see the figure
        // as walls closing around the boss, and from the air / minimap you still get the clean drawn shape.
        _curtainMat = new StandardMaterial3D { AlbedoColor = new Color(Col.R, Col.G, Col.B, 0.13f), EmissionEnabled = true, Emission = Col, EmissionEnergyMultiplier = 1.5f, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, BlendMode = BaseMaterial3D.BlendModeEnum.Add, CullMode = BaseMaterial3D.CullModeEnum.Disabled };

        // a faint blood pool fills the whole figure so it reads from the air as well as from the ground
        AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = Radius, BottomRadius = Radius, Height = 0.02f }, MaterialOverride = _glowMat, Position = new Vector3(0, 0.04f, 0), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });

        // the enclosing circle, cut into segments that appear one after another (the "sweep") — each with its own low wall
        float segLen = Mathf.Tau * Radius / ArcSegs * 1.12f;
        for (int i = 0; i < ArcSegs; i++)
        {
            float a = i / (float)ArcSegs * Mathf.Tau;
            var rig = new Node3D { Position = new Vector3(Mathf.Cos(a) * Radius, 0f, Mathf.Sin(a) * Radius), Rotation = new Vector3(0, -a, 0), Visible = false };
            AddChild(rig);
            rig.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.55f, 0.02f, segLen) }, MaterialOverride = _mat, Position = new Vector3(0, 0.08f, 0), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
            rig.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.04f, RingWallH, segLen) }, MaterialOverride = _curtainMat, Position = new Vector3(0, RingWallH * 0.5f, 0), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
            _arc[i] = rig;
        }

        // the five points of the star, then the chords in the classic 0→2→4→1→3→0 order
        for (int i = 0; i < 5; i++)
        {
            float a = -Mathf.Pi / 2f + i / 5f * Mathf.Tau;   // point-up
            _pts[i] = new Vector3(Mathf.Cos(a) * Radius, 0.1f, Mathf.Sin(a) * Radius);
            // a tall pillar marks each point of the star — the part you can see from anywhere in the arena
            var pil = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.35f, BottomRadius = 0.85f, Height = PillarH }, MaterialOverride = _curtainMat, Position = new Vector3(_pts[i].X, PillarH * 0.5f, _pts[i].Z), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = false };
            AddChild(pil); _pillars[i] = pil;
        }
        for (int s = 0; s < 5; s++)
        {
            Vector3 from = _pts[(s * 2) % 5], to = _pts[((s + 1) * 2) % 5];
            var d = to - from; float len = d.Length();
            var rig = new Node3D { Visible = false };
            AddChild(rig);
            rig.Position = (from + to) * 0.5f;
            rig.Rotation = new Vector3(0, Mathf.Atan2(d.X, d.Z), 0);
            rig.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.85f, 0.02f, len) }, MaterialOverride = _mat, Position = new Vector3(0, 0.1f, 0), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
            rig.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.05f, ChordWallH, len) }, MaterialOverride = _curtainMat, Position = new Vector3(0, ChordWallH * 0.5f, 0), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
            _strokes[s] = rig;
        }

        _light = new OmniLight3D { OmniRange = Radius * 1.5f, LightColor = Col, LightEnergy = 2.2f, Position = new Vector3(0, 3f, 0) };
        AddChild(_light);
        Game.I?.Sfx?.Thunder();
    }

    public override void _Process(double delta)
    {
        _t += (float)delta;

        // ---- the circle sweeps around ----
        int arcOn = Mathf.Clamp(Mathf.FloorToInt(_t / CircleDur * ArcSegs), 0, ArcSegs);
        for (int i = 0; i < ArcSegs; i++) if (_arc[i] != null && !_arc[i].Visible && i < arcOn) _arc[i].Visible = true;

        // ---- then each chord is struck, racing from one point to the next ----
        for (int s = 0; s < 5; s++)
        {
            var bar = _strokes[s]; if (bar == null) continue;
            float t0 = CircleDur + s * StrokeDur;
            float k = Mathf.Clamp((_t - t0) / StrokeDur, 0f, 1f);
            if (k <= 0f) continue;
            if (!bar.Visible) { bar.Visible = true; var pil = _pillars[(s * 2) % 5]; if (pil != null) pil.Visible = true; }   // the point it starts from lights up
            Vector3 from = _pts[(s * 2) % 5], to = _pts[((s + 1) * 2) % 5];
            bar.Scale = new Vector3(1f, 1f, Mathf.Max(0.001f, k));                 // stroke grows along its own length…
            bar.Position = from.Lerp((from + to) * 0.5f, k);                        // …anchored at the point it starts from
            if (k >= 1f) { var pil = _pillars[((s + 1) * 2) % 5]; if (pil != null) pil.Visible = true; }                      // …and the point it lands on
        }

        float prog = Mathf.Clamp(_t / DrawDur, 0f, 1f);
        if (_light != null) _light.LightEnergy = 2.2f + 3.5f * prog;
        if (_mat != null) _mat.EmissionEnergyMultiplier = 4.5f + 3f * prog;
        if (_curtainMat != null)   // the walls thicken and brighten as the figure closes — the "it's about to go off" tell
        {
            _curtainMat.EmissionEnergyMultiplier = 1.5f + 2.2f * prog;
            _curtainMat.AlbedoColor = new Color(Col.R, Col.G, Col.B, 0.13f + 0.14f * prog);
        }

        if (_autoBurst && !_burst && _t >= DrawDur) Burst();
    }

    // the figure is complete → flare white-hot and dispel outward as a shockwave
    public void Burst()
    {
        if (_burst) return;
        _burst = true;
        var g = Game.I;
        if (g != null)
        {
            var white = Col.Lerp(Colors.White, 0.75f);
            g.VfxRing(GlobalPosition, white, Radius * 0.5f, 0.35f);
            g.VfxRing(GlobalPosition, Col, Radius * 1.25f, 0.6f);
            g.VfxRing(GlobalPosition, Col, Radius * 2.1f, 0.9f);      // the wave rolling out past the figure
            g.Sfx?.Thunder();
            g.Player?.CamKickExternal(1f);
        }
        if (_mat != null) { _mat.Emission = Colors.White; _mat.EmissionEnergyMultiplier = 22f; }
        if (_glowMat != null) { _glowMat.EmissionEnergyMultiplier = 9f; _glowMat.AlbedoColor = new Color(1f, 0.6f, 0.6f, 0.5f); }
        if (_curtainMat != null) { _curtainMat.Emission = Colors.White; _curtainMat.EmissionEnergyMultiplier = 12f; _curtainMat.AlbedoColor = new Color(1f, 0.75f, 0.75f, 0.45f); }
        if (_light != null) _light.LightEnergy = 14f;

        var tw = CreateTween(); tw.SetParallel(true);
        tw.TweenProperty(this, "scale", new Vector3(1.7f, 1f, 1.7f), 0.85f);        // the whole figure blows outward as it fades
        if (_mat != null) tw.TweenProperty(_mat, "albedo_color", new Color(1f, 1f, 1f, 0f), 0.85f);
        if (_glowMat != null) tw.TweenProperty(_glowMat, "albedo_color", new Color(1f, 0.6f, 0.6f, 0f), 0.85f);
        if (_curtainMat != null) tw.TweenProperty(_curtainMat, "albedo_color", new Color(1f, 0.75f, 0.75f, 0f), 0.85f);
        if (_light != null) tw.TweenProperty(_light, "light_energy", 0f, 0.85f);
        tw.SetParallel(false);
        tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(this)) QueueFree(); }));
    }
}
