using Godot;

// A detailed-ish witch figure built entirely from primitives, color-coded to her damage type.
// Used for the third-person body other players see (full), and optionally as a first-person
// body for the local player (firstPerson = legs/robe/torso only, so the FP camera hands still read).
// Procedural walk + jump animation driven by Animate(delta, speed01, airborne).
// WitchModel.cs — the procedural witch body (robe, torso, head, hat, arms, legs, glowing wings),
// built from primitives. WitchColor(idx) maps witch index 0-3 -> element color (Lunar/Holy/Blood/
// Nature) and is the single source of truth for witch tint (used here, by local first-person body,
// and by RemoteAvatar for allies). firstPerson mode draws only robe+legs. Animate(delta, speed01,
// airborne) drives walk/idle/air poses; ShowWings toggles the float/glide wings.
public partial class WitchModel : Node3D
{
    private Node3D _root, _skirt, _hat, _armL, _armR, _legL, _legR, _torso, _wingL, _wingR;
    private bool _wingsOn = false;
    private float _phase = 0f, _idleT = 0f;
    private string _armKind = ""; private float _armT = 0f, _armDur = 0f;   // (NEW) networked cast-pose overlay
    private bool _armHold = false;   // held pose: ramp in and STAY (for the ult-cast window's sustained casting stance)
    public void PlayArm(string kind, float dur) { _armKind = kind; _armT = 0f; _armDur = dur; _armHold = false; }
    // ramp INTO a cast pose over ~0.45s and hold it there (with a subtle channelling sway) — the ult-cast cinematic
    public void HoldPose(string kind) { _armKind = kind; _armT = 0f; _armDur = 0.45f; _armHold = true; }
    public void ClearPose() { _armHold = false; _armDur = 0f; }
    private bool _fp = false;
    private bool _authored = false;   // (PHASE 4B) this body is an imported authored mesh, not the procedural build
    private AnimationPlayer _authoredAp;   // its AnimationPlayer
    private string _idleKey, _walkKey, _runKey;   // library-quality locomotion clip keys (walk/run = fwd fallback)
    private string _wF, _wB, _wL, _wR, _rF, _rB, _rL, _rR;   // 8-way directional locomotion clips (authored mesh)
    private AnimationTree _locoTree;   // AnimationTree driving BlendSpace2D locomotion (+ future cast mask); null → AP fallback
    private Vector2 _blend;            // smoothed BlendSpace2D position
    private Node3D _authoredRoot;          // the imported mesh root
    // witch index → authored-mesh key (0=Lunar … 8=Arcane). Present a res://assets/models/<key>.glb to swap that witch in.
    private static readonly string[] WitchKeys = { "witch_lunar", "witch_divine", "witch_crimson", "witch_verdant", "witch_gale", "witch_frost", "witch_forsaken", "witch_ember", "witch_arcane" };

    public static Color WitchColor(int witchIdx) => witchIdx switch
    {
        1 => DamageTypes.Col(DamageType.Holy),    // Divine
        2 => DamageTypes.Col(DamageType.Blood),   // Crimson Blood
        3 => DamageTypes.Col(DamageType.Nature),  // Verdant
        4 => DamageTypes.Col(DamageType.Wind),    // Gale (NEW)
        5 => DamageTypes.Col(DamageType.Frost),   // Frost (NEW)
        6 => DamageTypes.Col(DamageType.Curse),   // Forsaken (NEW)
        7 => DamageTypes.Col(DamageType.Ember),   // Ember (NEW)
        8 => DamageTypes.Col(DamageType.Arcane),  // Arcane (NEW)
        _ => DamageTypes.Col(DamageType.Lunar),   // Lunar (default)
    };

    public void Build(int witchIdx, bool firstPerson)
    {
        _fp = firstPerson;
        // (PHASE 4B) authored-mesh swap: for full-body (non-FP) renders — how allies/avatars are drawn — use the imported
        // witch model when one exists. The local FIRST-PERSON body stays procedural robe/legs (the camera covers the torso
        // up anyway, and a full authored body would clip the FP camera). Procedural pose/anim methods already null-check
        // _root/etc, so they safely no-op here; the mesh is static until its AnimationTree is wired (next step).
        if (!firstPerson && witchIdx >= 0 && witchIdx < WitchKeys.Length && ModelAssets.Has(WitchKeys[witchIdx]))
        {
            var authored = ModelAssets.TryLoad(WitchKeys[witchIdx]);
            if (authored != null)
            {
                _authored = true;
                AddChild(authored);
                ModelAssets.Painterlify(authored);      // opaque + matte
                ModelAssets.ApplyFallbackAlbedo(authored, WitchKeys[witchIdx]);   // re-apply texture if the FBX import lost it
                ModelAssets.FitHeight(authored, 4.8f);  // calibrated game witch height
                authored.Position -= new Vector3(0f, 0.2f, 0f);   // tiny constant drop → counter the walk/run stride float
                // library-quality idle/walk/run (meshy_animate presets) — start on idle
                var loco = ModelAssets.SetupDirectionalLocomotion(authored);
                _authoredAp = loco.Ap; _idleKey = loco.Idle; _authoredRoot = authored;
                _wF = loco.WF; _wB = loco.WB; _wL = loco.WL; _wR = loco.WR;
                _rF = loco.RF; _rB = loco.RB; _rL = loco.RL; _rR = loco.RR;
                _walkKey = loco.WF; _runKey = loco.RF;   // forward fallbacks
                _locoTree = ModelAssets.BuildLocomotionTree(authored, loco,
                    "witches/anims/magic/Standing 1H Magic Attack 01.fbx",   // LEFT fire (mirrored → left hand): thrust + hold while held
                    "witches/anims/magic/Standing 2H Cast Spell 01.fbx",     // charge gather / ready pose source
                    "witches/anims/magic/Standing 2H Magic Attack 01.fbx",   // charge RELEASE thrust
                    "witches/anims/locomotion/Standing Jump.fbx",            // jump (still) → frozen falling pose
                    "witches/anims/locomotion/Standing Jump Running.fbx");   // jump (running) → frozen falling pose
                if (_authoredAp != null && _authoredAp.HasAnimationLibrary("jmpr"))   // capture the run-jump length for seek scrubbing
                {
                    var jl = _authoredAp.GetAnimationLibrary("jmpr");
                    foreach (StringName a in jl.GetAnimationList()) { _jumpLen = (float)jl.GetAnimation(a).Length; break; }
                }
                if (_locoTree == null && _authoredAp != null && _idleKey != null) _authoredAp.Play(_idleKey);   // fallback path
                return;
            }
        }
        Color c = WitchColor(witchIdx);
        var robe = Game.ToonEmissive(new Color(c.R * 0.5f, c.G * 0.5f, c.B * 0.5f), 0.45f, 0.03f);
        var trim = Game.ToonEmissive(c, 1.5f, 0.02f);
        var skin = Game.ToonEmissive(new Color(0.86f, 0.78f, 0.72f), 0.35f, 0.02f);
        Material gem = witchIdx == 8 ? Game.ArcaneEnergyMat() : Game.ToonEmissive(c, 3.2f, 0f);   // (GLAM) accents; the Arcane witch's shimmer with flowing raw-plasma
        Material Sheer(float a)                                          // (GLAM) translucent element-tint for capes / overskirts / ribbons
        {
            var m = Game.ToonEmissive(c, 0.9f, 0f);
            m.AlbedoColor = new Color(c.R, c.G, c.B, a);
            m.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
            m.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
            return m;
        }

        _root = new Node3D();
        AddChild(_root);

        MeshInstance3D Add(Node3D parent, Mesh m, Material mat, Vector3 pos, Vector3 rotDeg = default)
        {
            var mi = new MeshInstance3D { Mesh = m, MaterialOverride = mat };
            mi.Position = pos; mi.RotationDegrees = rotDeg; parent.AddChild(mi); return mi;
        }

        // robe / skirt (wide at the hem) — pivots a touch for a sway
        _skirt = new Node3D { Position = new Vector3(0, 0.78f, 0) };
        _root.AddChild(_skirt);
        Add(_skirt, new CylinderMesh { TopRadius = 0.16f, BottomRadius = 0.5f, Height = 0.74f }, robe, Vector3.Zero);         // slimmer waist → hourglass
        Add(_skirt, new CylinderMesh { TopRadius = 0.22f, BottomRadius = 0.86f, Height = 0.66f }, Sheer(0.5f), new Vector3(0, -0.06f, 0));   // (GLAM) dramatic flared overskirt, translucent
        Add(_skirt, new TorusMesh { InnerRadius = 0.5f, OuterRadius = 0.6f }, trim, new Vector3(0, -0.36f, 0), new Vector3(90, 0, 0));   // glowing hem

        // torso
        _torso = new Node3D { Position = new Vector3(0, 1.18f, 0) };
        _root.AddChild(_torso);
        Add(_torso, new CylinderMesh { TopRadius = 0.18f, BottomRadius = 0.20f, Height = 0.5f }, robe, Vector3.Zero);
        Add(_torso, new CylinderMesh { TopRadius = 0.19f, BottomRadius = 0.19f, Height = 0.08f }, trim, new Vector3(0, 0.12f, 0));   // collar glow
        Add(_torso, new TorusMesh { InnerRadius = 0.15f, OuterRadius = 0.19f }, trim, new Vector3(0, -0.2f, 0), new Vector3(90, 0, 0));   // (GLAM) cinched waist
        Add(_torso, new SphereMesh { Radius = 0.06f, Height = 0.12f }, gem, new Vector3(0, -0.2f, 0.18f));                               // (GLAM) belt gem

        // legs / feet (peek below the hem so steps read)
        _legL = new Node3D { Position = new Vector3(-0.15f, 0.5f, 0) }; _root.AddChild(_legL);
        Add(_legL, new CylinderMesh { TopRadius = 0.08f, BottomRadius = 0.07f, Height = 0.5f }, robe, new Vector3(0, -0.25f, 0));
        Add(_legL, new BoxMesh { Size = new Vector3(0.16f, 0.1f, 0.28f) }, trim, new Vector3(0, -0.5f, 0.06f));
        _legR = new Node3D { Position = new Vector3(0.15f, 0.5f, 0) }; _root.AddChild(_legR);
        Add(_legR, new CylinderMesh { TopRadius = 0.08f, BottomRadius = 0.07f, Height = 0.5f }, robe, new Vector3(0, -0.25f, 0));
        Add(_legR, new BoxMesh { Size = new Vector3(0.16f, 0.1f, 0.28f) }, trim, new Vector3(0, -0.5f, 0.06f));

        // glowing wings (witch's base color) — hidden until she floats; built for both FP and remote bodies
        var wingMat = Game.ToonEmissive(c, 1.9f, 0.03f);
        wingMat.AlbedoColor = new Color(c.R, c.G, c.B, 0.62f);
        wingMat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        wingMat.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        _wingL = new Node3D { Position = new Vector3(-0.12f, 1.32f, 0.14f) };
        _root.AddChild(_wingL);
        var wl = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.05f, 0.85f, 0.55f) }, MaterialOverride = wingMat };
        wl.Position = new Vector3(-0.4f, 0.12f, -0.05f); wl.RotationDegrees = new Vector3(0, 18, 22);
        _wingL.AddChild(wl);
        _wingL.Visible = false;
        _wingR = new Node3D { Position = new Vector3(0.12f, 1.32f, 0.14f) };
        _root.AddChild(_wingR);
        var wr = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.05f, 0.85f, 0.55f) }, MaterialOverride = wingMat };
        wr.Position = new Vector3(0.4f, 0.12f, -0.05f); wr.RotationDegrees = new Vector3(0, -18, -22);
        _wingR.AddChild(wr);
        _wingR.Visible = false;

        if (firstPerson) return;   // local body: skip head/hat/arms — the FP camera hands cover those

        // (GLAM, third-person) sharp shoulder pauldrons — a fierce, high-fashion silhouette
        Add(_torso, new CylinderMesh { TopRadius = 0.02f, BottomRadius = 0.15f, Height = 0.16f }, trim, new Vector3(-0.23f, 0.19f, 0), new Vector3(0, 0, 62));
        Add(_torso, new CylinderMesh { TopRadius = 0.02f, BottomRadius = 0.15f, Height = 0.16f }, trim, new Vector3(0.23f, 0.19f, 0), new Vector3(0, 0, -62));
        // (GLAM) a flowing cape/train from the upper back (translucent element tint)
        Add(_torso, new CylinderMesh { TopRadius = 0.14f, BottomRadius = 0.42f, Height = 1.05f }, Sheer(0.8f), new Vector3(0, -0.42f, -0.16f), new Vector3(-9, 0, 0));

        // head
        Add(_root, new SphereMesh { Radius = 0.17f, Height = 0.34f }, skin, new Vector3(0, 1.62f, 0));

        // witch hat (brim + cone), tilts a little while moving
        _hat = new Node3D { Position = new Vector3(0, 1.74f, 0) };
        _root.AddChild(_hat);
        Add(_hat, new CylinderMesh { TopRadius = 0.44f, BottomRadius = 0.5f, Height = 0.05f }, trim, new Vector3(0, 0f, 0.02f));       // wider, sharper brim
        Add(_hat, new CylinderMesh { TopRadius = 0.0f, BottomRadius = 0.3f, Height = 0.82f }, robe, new Vector3(0, 0.44f, 0.06f), new Vector3(-8, 0, 0));   // taller cone, jauntier tilt
        Add(_hat, new TorusMesh { InnerRadius = 0.16f, OuterRadius = 0.2f }, trim, new Vector3(0, 0.08f, 0.03f), new Vector3(90, 0, 0));
        Add(_hat, new SphereMesh { Radius = 0.055f, Height = 0.11f }, gem, new Vector3(0, 0.09f, 0.22f));                              // (GLAM) hatband gem

        // arms (third-person only; FP uses the camera hands). Pivot at the shoulder, mesh hangs down.
        _armL = new Node3D { Position = new Vector3(-0.27f, 1.32f, 0) }; _root.AddChild(_armL);
        Add(_armL, new CapsuleMesh { Radius = 0.07f, Height = 0.55f }, robe, new Vector3(0, -0.26f, 0));
        Add(_armL, new SphereMesh { Radius = 0.075f, Height = 0.15f }, skin, new Vector3(0, -0.52f, 0));   // hand
        _armR = new Node3D { Position = new Vector3(0.27f, 1.32f, 0) }; _root.AddChild(_armR);
        Add(_armR, new CapsuleMesh { Radius = 0.07f, Height = 0.55f }, robe, new Vector3(0, -0.26f, 0));
        Add(_armR, new SphereMesh { Radius = 0.075f, Height = 0.15f }, skin, new Vector3(0, -0.52f, 0));

        // ---- per-witch signature flare (third-person; each coven member reads at a glance) ----
        switch (witchIdx)
        {
            case 1:   // Divine — a floating halo above the head
                Add(_root, new TorusMesh { InnerRadius = 0.17f, OuterRadius = 0.21f }, gem, new Vector3(0, 2.0f, 0), new Vector3(90, 0, 0));
                break;
            case 2:   // Crimson — devil horns curling off the hat + a barbed collar
                Add(_hat, new CylinderMesh { TopRadius = 0f, BottomRadius = 0.06f, Height = 0.34f }, gem, new Vector3(-0.17f, 0.16f, 0.04f), new Vector3(0, 0, 34));
                Add(_hat, new CylinderMesh { TopRadius = 0f, BottomRadius = 0.06f, Height = 0.34f }, gem, new Vector3(0.17f, 0.16f, 0.04f), new Vector3(0, 0, -34));
                Add(_torso, new TorusMesh { InnerRadius = 0.14f, OuterRadius = 0.19f }, gem, new Vector3(0, 0.24f, 0), new Vector3(78, 0, 0));
                break;
            case 3:   // Verdant — antlers crowning the head
                Add(_root, new CylinderMesh { TopRadius = 0f, BottomRadius = 0.035f, Height = 0.4f }, gem, new Vector3(-0.13f, 1.74f, 0), new Vector3(0, 0, 46));
                Add(_root, new CylinderMesh { TopRadius = 0f, BottomRadius = 0.035f, Height = 0.4f }, gem, new Vector3(0.13f, 1.74f, 0), new Vector3(0, 0, -46));
                Add(_root, new CylinderMesh { TopRadius = 0f, BottomRadius = 0.025f, Height = 0.22f }, gem, new Vector3(-0.22f, 1.9f, 0), new Vector3(0, 0, 60));
                Add(_root, new CylinderMesh { TopRadius = 0f, BottomRadius = 0.025f, Height = 0.22f }, gem, new Vector3(0.22f, 1.9f, 0), new Vector3(0, 0, -60));
                break;
            case 4:   // Gale — trailing shoulder ribbons swept back
                Add(_torso, new BoxMesh { Size = new Vector3(0.08f, 0.9f, 0.02f) }, Sheer(0.75f), new Vector3(-0.24f, -0.2f, -0.12f), new Vector3(-16, 0, 10));
                Add(_torso, new BoxMesh { Size = new Vector3(0.08f, 0.9f, 0.02f) }, Sheer(0.75f), new Vector3(0.24f, -0.2f, -0.12f), new Vector3(-16, 0, -10));
                break;
            case 5:   // Frost — a crystalline crown of ice shards
                for (int k = -2; k <= 2; k++)
                    Add(_hat, new CylinderMesh { TopRadius = 0f, BottomRadius = 0.045f, Height = 0.24f + (2 - Mathf.Abs(k)) * 0.06f }, gem, new Vector3(k * 0.11f, 0.06f, 0.2f), new Vector3(-14, 0, k * -8));
                break;
            case 6:   // Forsaken — a jagged crown of curse-runes ringing the head
                for (int k = 0; k < 5; k++)
                {
                    float a = k / 5f * Mathf.Tau;
                    Add(_root, new CylinderMesh { TopRadius = 0f, BottomRadius = 0.035f, Height = 0.22f }, gem, new Vector3(Mathf.Sin(a) * 0.24f, 1.82f, Mathf.Cos(a) * 0.24f), new Vector3(24, Mathf.RadToDeg(a), 0));
                }
                break;
            case 7:   // Ember — a crown of upward flame-spikes on the hat
                for (int k = -2; k <= 2; k++)
                    Add(_hat, new CylinderMesh { TopRadius = 0f, BottomRadius = 0.05f, Height = 0.26f + (2 - Mathf.Abs(k)) * 0.08f }, gem, new Vector3(k * 0.1f, 0.5f, 0.14f), new Vector3(-10, 0, k * 6));
                break;
            case 8:   // Arcane — a jagged crystal crown, a floating focus orb, and a slow-spinning rune-halo behind her head
            {
                for (int k = 0; k < 7; k++)   // crystalline arcane crown ringing the hat cone (alternating tall/short shards)
                {
                    float a = k / 7f * Mathf.Tau, h = 0.26f + (k % 2 == 0 ? 0.18f : 0f);
                    Add(_hat, new PrismMesh { Size = new Vector3(0.07f, h, 0.05f) }, gem, new Vector3(Mathf.Sin(a) * 0.25f, 0.14f + h * 0.45f, Mathf.Cos(a) * 0.25f + 0.04f), new Vector3(Mathf.Cos(a) * 12f, Mathf.RadToDeg(a), Mathf.Sin(a) * 12f));
                }
                var orb = new Node3D { Position = new Vector3(0, 0.98f, 0.02f) }; _hat.AddChild(orb);   // floating focus orb wreathed in a rune-ring + spikes
                Add(orb, new SphereMesh { Radius = 0.09f, Height = 0.18f }, gem, Vector3.Zero);
                Add(orb, new TorusMesh { InnerRadius = 0.13f, OuterRadius = 0.16f }, gem, Vector3.Zero, new Vector3(78, 0, 0));
                for (int k = 0; k < 4; k++) { float a = k / 4f * Mathf.Tau; Add(orb, new PrismMesh { Size = new Vector3(0.03f, 0.13f, 0.03f) }, gem, new Vector3(Mathf.Cos(a) * 0.15f, 0, Mathf.Sin(a) * 0.15f), new Vector3(0, Mathf.RadToDeg(a), 90)); }
                var halo = new Node3D { Position = new Vector3(0, 1.64f, -0.14f) }; _root.AddChild(halo);   // a slow rune-halo behind her head
                Add(halo, new TorusMesh { InnerRadius = 0.30f, OuterRadius = 0.35f }, gem, Vector3.Zero, new Vector3(90, 0, 0));
                for (int k = 0; k < 12; k++) { float a = k / 12f * Mathf.Tau; Add(halo, new BoxMesh { Size = new Vector3(0.028f, 0.09f, 0.028f) }, gem, new Vector3(Mathf.Cos(a) * 0.35f, Mathf.Sin(a) * 0.35f, 0), new Vector3(0, 0, Mathf.RadToDeg(a))); }
                var htw = halo.CreateTween().SetLoops();
                htw.TweenProperty(halo, "rotation_degrees:z", 360f, 14.0);   // eternal slow spin
                break;
            }
            default:  // Lunar — a crescent moon crowning the hat + tiny orbiting moons
                Add(_hat, new TorusMesh { InnerRadius = 0.1f, OuterRadius = 0.14f }, gem, new Vector3(0, 0.66f, 0.02f), new Vector3(78, 0, 18));
                Add(_hat, new SphereMesh { Radius = 0.035f, Height = 0.07f }, gem, new Vector3(-0.22f, 0.5f, 0));
                Add(_hat, new SphereMesh { Radius = 0.03f, Height = 0.06f }, gem, new Vector3(0.24f, 0.42f, 0.03f));
                break;
        }

        if (witchIdx == 8)   // (GLAM) extra arcane finery: a ring of energy shards slowly orbiting her, + a brighter collar focus
        {
            var motes = new Node3D { Position = new Vector3(0, 1.0f, 0) }; _root.AddChild(motes);
            for (int k = 0; k < 6; k++)
            {
                float a = k / 6f * Mathf.Tau;
                Add(motes, new PrismMesh { Size = new Vector3(0.04f, 0.15f, 0.04f) }, gem, new Vector3(Mathf.Cos(a) * 0.56f, Mathf.Sin(a * 3f) * 0.12f, Mathf.Sin(a) * 0.56f), new Vector3(18, Mathf.RadToDeg(a), 20));
            }
            var mtw = motes.CreateTween().SetLoops();
            mtw.TweenProperty(motes, "rotation_degrees:y", 360f, 9.0);   // slow orbit around her
            Add(_torso, new SphereMesh { Radius = 0.05f, Height = 0.1f }, gem, new Vector3(0, 0.12f, 0.19f));   // brighter arcane collar focus
        }
    }

    public void ShowWings(bool on)
    {
        _wingsOn = on;
        if (_wingL != null) _wingL.Visible = on;
        if (_wingR != null) _wingR.Visible = on;
    }

    // (ECLIPSE) invert the witch to a black silhouette with a white outline glow for the ult's duration, then restore.
    private readonly System.Collections.Generic.Dictionary<MeshInstance3D, Material> _origMats = new();
    private static ShaderMaterial _eclipseMat;
    private const string EclipseModelShader = @"
shader_type spatial;
render_mode cull_back;
void fragment(){
    float fres = pow(1.0 - abs(dot(normalize(VIEW), normalize(NORMAL))), 2.6);
    ALBEDO = vec3(0.02, 0.02, 0.04);
    EMISSION = vec3(1.0) * fres * 1.6;   // white rim outline
    ROUGHNESS = 0.85;
}";
    public void SetEclipse(bool on)
    {
        _eclipseMat ??= new ShaderMaterial { Shader = new Shader { Code = EclipseModelShader } };
        void Walk(Node n)
        {
            foreach (var ch in n.GetChildren())
            {
                if (ch is MeshInstance3D mi)
                {
                    if (on) { if (!_origMats.ContainsKey(mi)) _origMats[mi] = mi.MaterialOverride; mi.MaterialOverride = _eclipseMat; }
                    else if (_origMats.TryGetValue(mi, out var om)) mi.MaterialOverride = om;
                }
                Walk(ch);
            }
        }
        Walk(this);
        if (!on) _origMats.Clear();
    }

    private static ShaderMaterial _spectralMat;
    private readonly System.Collections.Generic.Dictionary<MeshInstance3D, Material> _spectralOrig = new();
    private const string SpectralModelShader = @"
shader_type spatial;
render_mode cull_disabled, unshaded, depth_draw_never;
void fragment(){
    float fres = pow(1.0 - abs(dot(normalize(VIEW), normalize(NORMAL))), 1.6);
    ALBEDO = vec3(0.55, 0.35, 0.85);
    EMISSION = vec3(0.6, 0.4, 1.0) * (0.5 + fres * 1.8);   // ghostly violet glow, bright rim
    ALPHA = 0.28 + fres * 0.45;                            // translucent, edges catch the light
}";
    // (REWORK) LifeCurse Specter: turn the witch into a translucent violet projection
    public void SetSpectral(bool on)
    {
        _spectralMat ??= new ShaderMaterial { Shader = new Shader { Code = SpectralModelShader } };
        void Walk(Node n)
        {
            foreach (var ch in n.GetChildren())
            {
                if (ch is MeshInstance3D mi)
                {
                    if (on) { if (!_spectralOrig.ContainsKey(mi)) _spectralOrig[mi] = mi.MaterialOverride; mi.MaterialOverride = _spectralMat; }
                    else if (_spectralOrig.TryGetValue(mi, out var om)) mi.MaterialOverride = om;
                }
                Walk(ch);
            }
        }
        Walk(this);
        if (!on) _spectralOrig.Clear();
    }

    public void Collapse(bool down)
    {
        if (_root != null) _root.RotationDegrees = down ? new Vector3(82, 0, 0) : Vector3.Zero;
    }

    private bool _meditate = false;
    // (MP menu bubble) fold her into a cross-legged, floating meditation while she's sealed in the bubble — so allies see WHY she's untouchable
    public void Meditate(bool on) { _meditate = on; }
    private void MeditatePose(float dt)
    {
        _idleT += dt;
        float fbob = Mathf.Sin(_idleT * 1.4f) * 0.05f;
        _root.Rotation = new Vector3(0, _root.Rotation.Y, 0);
        _root.Position = new Vector3(0, 0.6f + fbob, 0);                       // lifted clear of the ground → floating
        if (_legL != null) _legL.Rotation = new Vector3(-1.5f, 0.55f, 0.85f);  // fold + cross the shins in front of her
        if (_legR != null) _legR.Rotation = new Vector3(-1.5f, -0.55f, -0.85f);
        if (_armL != null) _armL.Rotation = new Vector3(0.5f, 0, 0.5f);        // hands rested loosely toward the knees
        if (_armR != null) _armR.Rotation = new Vector3(0.5f, 0, -0.5f);
        if (_skirt != null) _skirt.Rotation = Vector3.Zero;
        if (_hat != null) _hat.Rotation = new Vector3(0, 0, Mathf.Sin(_idleT * 0.8f) * 0.03f);
    }

    // Plant an authored mesh's feet on the ground. Call AFTER this WitchModel is in-tree and positioned at the character's
    // feet — it reads real world bone transforms. No-op for the procedural build.
    public void GroundAuthored()
    {
        if (_authored && _authoredRoot != null && GodotObject.IsInstanceValid(_authoredRoot))
            ModelAssets.GroundToFeet(_authoredRoot);
    }

    // Fire the upper-body cast overlay (OneShot) — plays a cast clip on arms/torso while locomotion keeps the legs moving.
    // No-op for the procedural build or if the tree/cast clip is absent.
    private bool TreeOk => _locoTree != null && GodotObject.IsInstanceValid(_locoTree);
    // left-click fire: 0→1 thrusts the left arm forward, held at 1 while firing, →0 recovers. Driven by the player each frame.
    public void SetLeftFire(float amt) { if (TreeOk) _locoTree.Set("parameters/leftfire/blend_amount", Mathf.Clamp(amt, 0f, 1f)); }
    public void CastLeft() { }   // legacy net alias — left fire is now a continuous blend (SetLeftFire), not a trigger
    // right-click release: fire the 2H thrust OneShot
    public void Release() { if (TreeOk) _locoTree.Set("parameters/release/request", (int)AnimationNodeOneShot.OneShotRequest.Fire); }
    // right-click charge: drive the both-hands gather pose in by amount (0→1); holds while held
    public void SetCharge(float amt) { if (TreeOk) _locoTree.Set("parameters/charge/blend_amount", Mathf.Clamp(amt, 0f, 1f)); }
    public void Cast() => CastLeft();   // back-compat (net cast broadcast → left cast)
    // airborne: blend in the whole-body frozen falling pose (0=grounded, 1=mid-jump); running = which takeoff variant
    private float _jumpLen = 1f;   // run-jump clip length (the player scrubs it: launch → hold fall → land)
    public float JumpClipLen => _jumpLen;
    public void SetJump(float blend, bool running, bool mirror, float seekTime)
    {
        if (!TreeOk) return;
        _locoTree.Set("parameters/jump/blend_amount", Mathf.Clamp(blend, 0f, 1f));
        _locoTree.Set("parameters/jumpsel/blend_amount", running ? 1f : 0f);
        _locoTree.Set("parameters/runjmpsel/blend_amount", mirror ? 1f : 0f);
        _locoTree.Set("parameters/jumpseek/seek_request", seekTime);
    }

    public bool IsAuthored => _authored;   // true when this body is an imported authored mesh (vs the procedural build)
    public AnimationPlayer Ap => _authoredAp;   // (DEV) the authored AnimationPlayer (for the anim viewer)
    public void EnableTree(bool on) { if (_locoTree != null && GodotObject.IsInstanceValid(_locoTree)) _locoTree.Active = on; }

    // (DEV / EXPERIMENT) procedural IK on the LEFT arm: point/reach the hand at a world target, blended over the animation
    // (0 = anim, 1 = full IK). SkeletonIK3D runs after the AnimationTree, so it overrides the arm pose.
    private Node _ikL, _ikR;   // SkeletonIK3D per arm, stored as base Node so the serializer doesn't warn on the deprecated type
    private Node3D _ikTargetL, _ikTargetR;
    public void SetupCastIK()
    {
        if (_ikL != null && GodotObject.IsInstanceValid(_ikL)) return;
        var skel = _authoredRoot != null ? ModelAssets.FindSkeleton(_authoredRoot) : null;
        if (skel == null) return;
        _ikL = MakeArmIK(skel, "LeftArm", "LeftHand", out _ikTargetL);
        _ikR = MakeArmIK(skel, "RightArm", "RightHand", out _ikTargetR);
    }
    private static Node MakeArmIK(Skeleton3D skel, string armSuffix, string handSuffix, out Node3D target)
    {
        target = null; string arm = null, hand = null;
        for (int i = 0; i < skel.GetBoneCount(); i++)
        {
            string n = (string)skel.GetBoneName(i);
            if (n.EndsWith(armSuffix)) arm = n; else if (n.EndsWith(handSuffix)) hand = n;
        }
        if (arm == null || hand == null) return null;
        var t = new Node3D(); skel.AddChild(t); target = t;
#pragma warning disable CS0618
        var ik = new SkeletonIK3D { RootBone = arm, TipBone = hand, UseMagnet = true, OverrideTipBasis = true, MaxIterations = 16, Interpolation = 0f };
        skel.AddChild(ik); ik.TargetNode = ik.GetPathTo(t); ik.Start();
#pragma warning restore CS0618
        return ik;
    }
    public void DriveLeftIK(Vector3 target, Vector3 magnet, float blend) => DriveIK(_ikL, _ikTargetL, target, magnet, blend);
    public void DriveRightIK(Vector3 target, Vector3 magnet, float blend) => DriveIK(_ikR, _ikTargetR, target, magnet, blend);
    private static void DriveIK(Node ikNode, Node3D target, Vector3 tgt, Vector3 magnetWorld, float blend)
    {
        if (ikNode == null || !GodotObject.IsInstanceValid(ikNode)) return;
        if (target != null) target.GlobalPosition = tgt;
        // Magnet (elbow pole hint) is in the SKELETON's LOCAL space — convert the world hint or the elbow bends toward chest.
        Vector3 magnet = magnetWorld;
        if (target?.GetParent() is Skeleton3D sk) magnet = sk.ToLocal(magnetWorld);
#pragma warning disable CS0618
        var ik = (SkeletonIK3D)ikNode; ik.Magnet = magnet; ik.Interpolation = Mathf.Clamp(blend, 0f, 1f);
#pragma warning restore CS0618
    }

    public void Animate(double delta, float speed01, bool airborne, Vector3 moveDirWorld = default)
    {
        // AnimationTree path (preferred): drive the BlendSpace2D position (true 2D directional blend) + playback speed.
        if (_locoTree != null && GodotObject.IsInstanceValid(_locoTree))
        {
            bool moving = speed01 >= 0.06f;
            Vector2 target;
            if (!moving) target = Vector2.Zero;                 // origin = idle
            else
            {
                // move dir in local frame; mesh faces +Z so forward=+local.Z, right=-local.X
                Vector3 local = moveDirWorld.LengthSquared() > 1e-6f
                    ? GlobalTransform.Basis.Inverse() * moveDirWorld.Normalized()
                    : new Vector3(0f, 0f, 1f);
                Vector2 dir2 = new Vector2(-local.X, local.Z);
                if (dir2.LengthSquared() > 1e-6f) dir2 = dir2.Normalized();
                float radius = Mathf.Clamp((speed01 - 0.06f) / 0.94f * 2f, 0f, 2f);   // 0=idle,1=walk ring,2=run ring
                target = dir2 * radius;
            }
            // airborne → SNAP the locomotion to idle (no run-cycle bleed showing under the jump); grounded → smooth
            _blend = _blend.Lerp(target, airborne ? 1f : Mathf.Clamp((float)delta * 8f, 0f, 1f));
            _locoTree.Set("parameters/loco/blend_position", _blend);
            _locoTree.Set("parameters/speed/scale", moving ? 0.9f : 1f);              // slower-than-native playback
            return;
        }
        if (_authoredAp != null && GodotObject.IsInstanceValid(_authoredAp))
        {
            // directional locomotion: idle when still, else pick fwd/back/left/right (dominant axis) × walk/run by speed.
            bool moving = speed01 >= 0.06f;
            bool running = speed01 > 0.62f;
            string want;
            if (!moving) want = _idleKey;
            else
            {
                // move dir in the character's LOCAL frame. The authored Mixamo mesh faces +Z (its "right" is -X), so
                // forward = +local.Z and right = -local.X (this flip is a constant mesh-facing correction — correct for both
                // the tp puppet and co-op avatars).
                Vector3 local = moveDirWorld.LengthSquared() > 1e-6f
                    ? GlobalTransform.Basis.Inverse() * moveDirWorld.Normalized()
                    : new Vector3(0f, 0f, 1f);                  // no dir given → treat as forward (+Z)
                float fwd = local.Z, right = -local.X;
                string f = running ? _rF : _wF, b = running ? _rB : _wB, l = running ? _rL : _wL, rr = running ? _rR : _wR;
                want = Mathf.Abs(fwd) >= Mathf.Abs(right) ? (fwd >= 0f ? f : b) : (right >= 0f ? rr : l);
            }
            want ??= _idleKey ?? _wF ?? _walkKey;               // graceful fallback if a clip is missing
            if (want != null)
            {
                if ((string)_authoredAp.CurrentAnimation != want) _authoredAp.Play(want, 0.18);   // crossfade → no pop on dir change
                _authoredAp.SpeedScale = !moving ? 1f : Mathf.Clamp(speed01 * 0.9f, 0.45f, 0.95f);   // slower than native (was too fast)
            }
            return;
        }
        if (_root == null) return;
        float dt = (float)delta;
        if (_meditate) { MeditatePose(dt); return; }
        speed01 = Mathf.Clamp(speed01, 0f, 1f);
        _phase += dt * (3.2f + 9f * speed01);
        _idleT += dt;

        float bob = airborne ? 0f : Mathf.Abs(Mathf.Sin(_phase)) * 0.07f * speed01;
        float idleBob = Mathf.Sin(_idleT * 1.8f) * 0.015f * (1f - speed01);
        float lean = 0.16f * speed01;
        // root: bob/jump-rise + forward lean + slight roll
        if (!IsCollapsed())
            _root.Rotation = new Vector3(airborne ? -0.16f : lean * 0.6f, _root.Rotation.Y, Mathf.Sin(_phase) * 0.05f * speed01);
        _root.Position = new Vector3(0, bob + idleBob + (airborne ? 0.12f : 0f), 0);

        float step = Mathf.Sin(_phase);
        if (_skirt != null) _skirt.Rotation = new Vector3(0, 0, step * 0.10f * speed01);
        if (_hat != null) _hat.Rotation = new Vector3(step * 0.05f * speed01, 0, Mathf.Sin(_phase * 0.7f) * 0.03f);

        if (_legL != null) _legL.Rotation = new Vector3(airborne ? 0.7f : step * 0.6f * speed01, 0, 0);
        if (_legR != null) _legR.Rotation = new Vector3(airborne ? 0.5f : -step * 0.6f * speed01, 0, 0);

        float armSwing = airborne ? -1.0f : (step * (0.35f + 0.45f * speed01));
        if (_armL != null) _armL.Rotation = new Vector3(armSwing, 0, 0.14f);
        if (_armR != null) _armR.Rotation = new Vector3(-armSwing, 0, -0.14f);

        // cast-pose overlay — networked via Player.SetArm -> so allies see every cast animation (NEW)
        if ((_armDur > 0f || _armHold) && _armL != null && _armR != null)
        {
            _armT += dt; float k = Mathf.Clamp(_armT / _armDur, 0f, 1f);
            // one-shot poses ease in-and-out (sin); a HELD pose ramps to full and stays there
            float e = _armHold ? k : Mathf.Sin(k * Mathf.Pi);
            float sway = _armHold ? Mathf.Sin(_idleT * 3f) * 0.06f * k : 0f;   // subtle channelling energy while held
            Vector3 lr = _armL.Rotation, rr = _armR.Rotation;
            switch (_armKind)
            {
                case "flare":   rr.X = Mathf.Lerp(rr.X, 1.55f, e); rr.Z = Mathf.Lerp(rr.Z, -0.25f, e); break;   // right arm out horizontal, palm up
                case "raise":   lr.X = Mathf.Lerp(lr.X, 2.1f, e); break;                                        // one arm up
                case "palmsup": lr.X = Mathf.Lerp(lr.X, 1.1f, e); rr.X = Mathf.Lerp(rr.X, 1.1f, e); break;      // both forward-up, palms up
                case "thrust":  rr.X = Mathf.Lerp(rr.X, 1.7f, e); break;                                        // arm thrust forward
                case "together":lr.X = Mathf.Lerp(lr.X, 1.2f, e); rr.X = Mathf.Lerp(rr.X, 1.2f, e); lr.Z = Mathf.Lerp(lr.Z, -0.2f, e); rr.Z = Mathf.Lerp(rr.Z, 0.2f, e); break;
                case "slam":    lr.X = Mathf.Lerp(lr.X, -0.7f, e); rr.X = Mathf.Lerp(rr.X, -0.7f, e); break;     // arms driven down
                case "draw":    lr.Z = Mathf.Lerp(lr.Z, 0.8f, e); rr.Z = Mathf.Lerp(rr.Z, -0.8f, e); break;     // spread wide
                case "channel": lr.X = Mathf.Lerp(lr.X, 1.35f, e); rr.X = Mathf.Lerp(rr.X, 1.35f, e); lr.Z = Mathf.Lerp(lr.Z, -0.35f, e); rr.Z = Mathf.Lerp(rr.Z, 0.35f, e); break;   // (NEW) both hands raised forward, framing a sigil
                case "conjure": lr.X = Mathf.Lerp(lr.X, 2.35f, e) + sway; rr.X = Mathf.Lerp(rr.X, 2.35f, e) - sway; lr.Z = Mathf.Lerp(lr.Z, -0.55f, e); rr.Z = Mathf.Lerp(rr.Z, 0.55f, e); break;   // (NEW) arms flung high + out, conjuring
                case "barrage": { float f = Mathf.Abs(Mathf.Sin(k * Mathf.Pi * 3f)); rr.X = Mathf.Lerp(rr.X, 1.6f, f); lr.X = Mathf.Lerp(lr.X, 1.6f, 1f - f); break; }
                case "grdpunch": { float w = Mathf.Clamp(k / 0.35f, 0, 1), dr = Mathf.Clamp((k - 0.35f) / 0.65f, 0, 1); float up = w * (1 - dr); lr.X = Mathf.Lerp(lr.X, 2.0f, up); rr.X = Mathf.Lerp(rr.X, 2.0f, up); lr.X = Mathf.Lerp(lr.X, -0.7f, dr); rr.X = Mathf.Lerp(rr.X, -0.7f, dr); break; }
            }
            _armL.Rotation = lr; _armR.Rotation = rr;
            if (!_armHold && k >= 1f) _armDur = 0f;
        }

        if (_wingsOn)
        {
            float flap = Mathf.Sin(_idleT * 9f) * 0.45f;
            if (_wingL != null) _wingL.Rotation = new Vector3(0, 0.5f, 0.35f + flap);
            if (_wingR != null) _wingR.Rotation = new Vector3(0, -0.5f, -0.35f - flap);
        }
    }

    private bool IsCollapsed() => _root != null && Mathf.Abs(_root.RotationDegrees.X) > 60f;
}
