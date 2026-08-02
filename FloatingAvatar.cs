using Godot;

// FloatingAvatar.cs — AUTHORED "Far Far West / Rayman" floating witch. Hero pieces (dress/hat/hand) are Meshy GLBs (remeshed
// ~6-8k tris, untextured), re-skinned painterly + per-element glow; boots/hair/eyes/shadow procedural. Everything visual hangs
// under a `_rig` node that BOBS + LEANS with movement (secondary motion sells "walking" for a limbless body). Critic fixes:
// filled the head void (dark-but-LIT skull + a hair bun → not a headless mannequin), tamed the blown-out glow, hands read from
// behind, real motion, grounded. Pieces stay separate anchored nodes → FP can instance just the hand; TP shows the set.
public partial class FloatingAvatar : Node3D
{
    private Node3D _rig, _hat, _head, _handL, _handR, _hairPivot;
    private float _hairSwX, _hairSwZ, _hairVX, _hairVZ;   // lagged-spring state for the flowing hair (fake physics)
    private float _t, _fire, _charge;
    private Color _col;
    private Node3D _orb;
    private float _handBaseY = 0.82f, _hatBaseY = 1.72f, _headBaseY = 1.63f;
    private float _headX = -0.05f;   // the robe's baked centre sits left of X=0, so the head read shifted RIGHT — nudge the head/hat/neck back to the robe's visual centre

    public void SetCast(float fire, float charge) { _fire = fire; _charge = charge; }

    public void Build(int witchIdx)
    {
        _col = WitchModel.WitchColor(witchIdx);
        // REFERENCE look: the robe is desaturated GREY (tattered) — ALL the element colour comes from the eyes + energy hair. Only a
        // faint per-witch tint so the three still differ subtly, but it reads grey, not "dyed cloth".
        Color grey = new Color(0.32f, 0.33f, 0.38f);   // cool desaturated SLATE (reference), not warm cream
        Color dressCol = grey.Lerp(_col, 0.12f);
        Color felt = new Color(0.13f, 0.14f, 0.17f);   // darker slate (tattered under-layer / lining)
        Color headCol = new Color(0.02f, 0.02f, 0.03f);   // GHOSTLY near-black featureless face — glowing eyes shine out of the dark
        var dressMat = Vis.Painterly(dressCol, rough: 0.96f, roughVar: 0.22f, macroValue: 0.2f, macroHue: 0.05f, macroScale: 0.7f, detailScale: 4.5f, detailValue: 0.18f);   // matte, cloth value-drift, low hue variation (grey)
        var feltMat = Vis.Painterly(felt, rough: 0.96f, roughVar: 0.14f, macroValue: 0.16f, macroHue: 0.03f, macroScale: 0.9f, detailScale: 5f, detailValue: 0.13f);
        var headMat = Vis.Painterly(headCol, rough: 0.7f, roughVar: 0.1f, macroValue: 0.06f, macroHue: 0.02f, macroScale: 1.0f, fresnel: 1.0f, fresnelCol: _col);   // faint element FRESNEL rim so the dark head still has a silhouette
        var glowMat = Vis.Painterly(_col, rough: 0.55f, macroValue: 0.1f, emission: _col, emissionEnergy: 1.3f, emissionThreshold: 0.45f);   // held HERO orb — element hue survives the bloom
        // eyes: bright ELEMENT colour shining out of the black face
        Color eyeCol = _col.Lerp(Colors.White, 0.16f);   // luminous but clearly the DAMAGE hue, not blown white
        var eyeMat = Vis.Painterly(eyeCol, rough: 0.45f, macroValue: 0.04f, emission: eyeCol, emissionEnergy: 0.6f, emissionThreshold: 0.4f);   // soft teal almond glow (critic: was pure-white starburst)

        // strong soft contact shadow — stays on the ground (NOT under _rig, so it doesn't bob)
        var shadow = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 0.6f, Height = 1.2f, RadialSegments = 18, Rings = 6 },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0, 0, 0, 0.42f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Position = new Vector3(0, 0.04f, 0), Scale = new Vector3(1.15f, 0.04f, 1.2f),
        };
        AddChild(shadow);

        _rig = new Node3D(); AddChild(_rig);   // bob/lean container — everything visual hangs here

        // ---- DRESS (authored) ----
        float bodyH = 1.62f;
        var dress = new MeshInstance3D { Mesh = PropGlb.GetMesh("robe"), MaterialOverride = dressMat, Scale = Vector3.One * bodyH };
        _rig.AddChild(dress);

        // ---- LINING: the dress GLB is open at the neckline AND the back — those openings showed the culled interior as a BLACK
        //      VOID (critic's #1 issue: "headless mannequin"). A slightly-smaller copy of the SAME mesh with culling DISABLED + a
        //      dark matte fabric hugs every opening exactly (no primitive-filler gaps), so you see lit interior cloth, not a hole. ----
        Color linCol = new Color(dressCol.R * 0.72f + 0.04f, dressCol.G * 0.72f + 0.04f, dressCol.B * 0.72f + 0.05f);   // near dress tone so open edges read as fabric, not a hole
        var lining = new MeshInstance3D { Mesh = PropGlb.GetMesh("robe"), Scale = Vector3.One * (bodyH * 0.985f) };
        lining.MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = linCol, Roughness = 1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            EmissionEnabled = true, Emission = linCol, EmissionEnergyMultiplier = 0.4f,   // self-light so interior/armhole edges never read as a black void
        };
        _rig.AddChild(lining);
        // SHOULDER CAPS: the sleeveless armholes read as dark sockets from 3/4 & side (critic). Two dress-toned caps close them and
        // read as little cap-sleeves / shoulders — capping the hole with garment mass rather than showing the interior edge.
        for (int i = 0; i < 2; i++)
        {
            float s = i == 0 ? 1 : -1;
            var cap = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.11f, Height = 0.22f }, MaterialOverride = dressMat, Position = new Vector3(s * 0.135f, 1.46f, 0.01f), Scale = new Vector3(1.05f, 0.85f, 1.05f) };
            _rig.AddChild(cap);
        }
        // NECK so the head reads as ATTACHED above the collar — taller + fatter at the base so there's no profile gap
        // NECK — a SHORT ghostly-black collar bridging the head to the bodice. Kept short + high so it does NOT drop into the open
        // robe-back (a long column there read as a dark stump through the back). Hidden behind the hair from behind.
        var neck = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.12f, BottomRadius = 0.15f, Height = 0.26f, RadialSegments = 12 }, MaterialOverride = headMat, Position = new Vector3(_headX, 1.50f, 0.02f) };
        _rig.AddChild(neck);

        // ---- HEAD: a GHOSTLY near-black spectral face with sharp glowing eyes shining out, framed by supernatural FLAME-HAIR ----
        _head = new Node3D { Position = new Vector3(_headX, _headBaseY, 0.02f) }; _rig.AddChild(_head);
        var skull = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.17f, Height = 0.34f }, MaterialOverride = headMat, Scale = new Vector3(1f, 1.05f, 0.95f) }; _head.AddChild(skull);
        BuildGhastlyHair(_head);   // element-coloured flame-hair aura framing the head (crown/back/sides, behind the hat)
        // NARROW SLANTED predator eyes (owner: not round prey eyes) — long thin element slivers, outer corners raised = fierce/sly.
        // Duplicated material with a HIGH render priority so the eyes always draw ON TOP of the flame-hair.
        var eyeTop = (ShaderMaterial)eyeMat.Duplicate(true); eyeTop.RenderPriority = 2;
        for (int i = 0; i < 2; i++)
        {
            float s = i == 0 ? 1 : -1;
            var eye = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.02f, Height = 0.04f }, MaterialOverride = eyeTop, Scale = new Vector3(2.2f, 0.46f, 1f) };
            eye.Position = new Vector3(s * 0.06f, 0.02f, 0.162f); eye.RotationDegrees = new Vector3(0, 0, s * 8f); _head.AddChild(eye);   // calmer near-horizontal glowing almonds (reference: eerie-blank, not scowling)
        }

        // ---- HAT (authored) — raised so its brim crowns the silhouette; NO primitive glow band (element reads via eyes + hue) ----
        _hat = new Node3D { Position = new Vector3(_headX, _hatBaseY, 0) }; _rig.AddChild(_hat);
        var hat = new MeshInstance3D { Mesh = PropGlb.GetMesh("hat"), MaterialOverride = feltMat, Scale = Vector3.One * 0.66f }; _hat.AddChild(hat);

        // ---- HANDS (authored gloves) grip the held ORB from the sides (kept as-is per owner) ----
        Vector3 orbPos = new Vector3(0, _handBaseY + 0.10f, 0.42f);
        Vector3 handAnchorR = new Vector3(0.29f, orbPos.Y - 0.02f, 0.45f);   // grip from the RIGHT, out past the orb edge; left mirrors
        _handL = BuildHand(-1); _handL.Position = new Vector3(-handAnchorR.X, handAnchorR.Y, handAnchorR.Z); _rig.AddChild(_handL);
        _handR = BuildHand(1); _handR.Position = handAnchorR; _rig.AddChild(_handR);

        // ---- held FOCUS ORB — the hero element beacon, gripped between the hands ----
        _orb = new Node3D { Position = orbPos }; _rig.AddChild(_orb);
        var orb = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.19f, Height = 0.38f }, MaterialOverride = glowMat }; _orb.AddChild(orb);
    }

    // A chunky gloved hand GRIPPING the orb from one side. `side` = -1 (left) / +1 (right) → mirror via node Scale.X.
    private Node3D BuildHand(float side)
    {
        var n = new Node3D { Scale = new Vector3(side, 1f, 1f) };
        var hand = PropGlb.Instance("hand", 0.28f);
        // GRIP pose: yaw -90 faces the palm INWARD at the orb, +pitch tilts fingers up-and-inward so the spread fingers WRAP the sphere.
        hand.RotationDegrees = new Vector3(38f, -90f, 0f);
        n.AddChild(hand);
        return n;
    }

    // ---- GHASTLY FLAME-HAIR — thin cone "strands" rooted on the crown/back/sides, carved into licking flames by ghastly_hair.gdshader
    private static Shader _hairShader;
    private static Texture2D _hairNoise;
    private static Texture2D HairNoise()
    {
        if (_hairNoise != null) return _hairNoise;
        var fnl = new FastNoiseLite { NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth, Frequency = 0.045f, FractalOctaves = 3 };
        _hairNoise = new NoiseTexture2D { Width = 128, Height = 128, Seamless = true, Noise = fnl };
        return _hairNoise;
    }

    private void BuildGhastlyHair(Node3D head)
    {
        _hairShader ??= GD.Load<Shader>("res://shaders/ghastly_hair.gdshader");
        // RenderPriority HIGH so the additive hair draws AFTER (over) the transparent water shader — otherwise the pond sorts on top
        // and obscures the hair (owner). Head/eyes are opaque so they're unaffected.
        var mat = new ShaderMaterial { Shader = _hairShader, RenderPriority = 8 };
        // SATURATED, vivid version of the damage colour is the hair BODY — not too bright a value, so additive overlap + bloom stays cyan (not white)
        Color hairCol = Color.FromHsv(_col.H, Mathf.Min(1f, _col.S * 1.5f + 0.25f), 0.8f);
        mat.SetShaderParameter("flame_color", hairCol);
        mat.SetShaderParameter("intensity", 0.72f);   // firmer, denser, luminous (critic: was thin/see-through)
        mat.SetShaderParameter("strand_count", 10.0f);// dense discrete strands
        mat.SetShaderParameter("noise_tex", HairNoise());
        mat.SetShaderParameter("pan_speed", 1.0f);
        mat.SetShaderParameter("displace_amt", 0.05f);
        mat.SetShaderParameter("flow_curve", 0.16f);   // animated drift (baked static_wave carries the resting waviness)
        mat.SetShaderParameter("static_wave", 0.3f);   // strong BAKED wave → clearly wavy hair, not a combed sheet
        mat.SetShaderParameter("scalp_hug", 0.06f);    // SLIGHT over-crown drape only (bigger read as a glowing skullcap)
        mat.SetShaderParameter("tip_taper", 0.45f);

        _hairPivot = new Node3D(); head.AddChild(_hairPivot);   // scalp pivot — swung by a lagged spring in Animate → hair "physics"

        // BACK CURTAIN (reference-shaped): roots along a horizontal ARC across the crown-back (under the hat), hanging DOWN — a
        // curtain, NOT a radial fan. Mass loaded on the CENTRE-back (centre locks straightest + LONGEST → to mid/lower robe); the
        // side locks only slightly shorter and only slightly splayed. This is "hair cascading down the back", not "moth wings".
        // MANY smooth narrow ribbon locks arranged on a 3D TEARDROP ARC that wraps the back HEMISPHERE of the head (so it has real
        // front-to-back depth from the side/3-quarter, not a flat billboard). Centre-back locks are furthest back + LONGEST; the
        // edge locks curl forward around the head. Mass stays BEHIND the shoulders (small forward reach) → doesn't veil the chest.
        const int back = 26;   // DENSE overlap → continuous sheet, not separated tentacles
        for (int i = 0; i < back; i++)
        {
            float u = (float)i / (back - 1);
            float a = Mathf.Lerp(-1f, 1f, u);                      // -1 (left) … 0 (centre-back) … +1 (right)
            float ang = a * 1.55f;                                 // ~±89° around the back hemisphere
            float centre = 1f - Mathf.Abs(a);                      // 1 at centre-back → 0 at the ears
            float len = 0.55f + centre * 1.55f + Frac(i * 0.383f) * 0.16f;   // centre-back LONGEST (to lower robe), ears shortest → teardrop taper
            float wid = 0.28f + Frac(i * 0.19f) * 0.08f;
            float rx = Mathf.Sin(ang) * 0.16f;
            float rz = -0.12f - Mathf.Cos(ang) * 0.12f;           // whole ring pushed BACK behind the shoulders (nothing crosses the chest/orb)
            Vector3 root = new Vector3(rx, 0.12f + Frac(i * 0.53f) * 0.04f, rz);
            Vector3 flow = new Vector3(Mathf.Sin(ang) * 0.6f, -1f, -0.25f).Normalized();   // DOWN + splay WIDE (to ~1.7× shoulder) + BACK
            Vector3 face = new Vector3(Mathf.Sin(ang) * 1.3f, 0.2f, Mathf.Cos(ang) * -1f).Normalized();   // faces radially outward → rounded volume from every angle
            AddHairLock(mat, root, flow, face, len, wid, Frac(i * 0.61803f));
        }
        // (front face-framing wisps removed — they draped over the chest/orb; the orb is the hero prop and must stay clear)
    }

    // one flowing ribbon "lock": a subdivided PlaneMesh oriented so local +Z = flow(down) and +Y = the face normal, with the root at `root`.
    private void AddHairLock(ShaderMaterial mat, Vector3 root, Vector3 flow, Vector3 face, float len, float wid, float phase)
    {
        var plane = new PlaneMesh { Size = new Vector2(wid, len), SubdivideDepth = 16, SubdivideWidth = 4 };
        var ribbon = new MeshInstance3D { Mesh = plane, MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        ribbon.Basis = BasisFromZY(flow, face);
        ribbon.Position = root + flow * (len * 0.5f);
        ribbon.SetInstanceShaderParameter("phase", phase);
        _hairPivot.AddChild(ribbon);
    }

    // basis whose local +Z = z and local +Y aligned to yHint (for orienting a PlaneMesh ribbon: length along z, face-normal along y)
    private static Basis BasisFromZY(Vector3 z, Vector3 yHint)
    {
        z = z.Normalized();
        Vector3 x = yHint.Cross(z).Normalized();
        Vector3 y = z.Cross(x).Normalized();
        return new Basis(x, y, z);
    }

    private static float Frac(float f) => f - Mathf.Floor(f);

    public void Animate(float dt, float move)
    {
        _t += dt * (1f + move * 2.5f);
        float bob = Mathf.Sin(_t * 2.2f) * (0.03f + move * 0.05f);
        float sway = Mathf.Sin(_t * 2.2f + 0.7f);
        float stride = Mathf.Sin(_t * 5.5f);
        float castFwd = _fire * 0.24f + _charge * 0.16f;

        // WHOLE-BODY secondary motion: hover bob + a lean into travel + a gentle idle sway → sells "moving/floating", not a statue
        if (_rig != null)
        {
            _rig.Position = new Vector3(0, 0.06f + bob, 0);   // hover baseline lifts the hem clear of the ground/water (shadow stays down → reads as floating)
            _rig.RotationDegrees = new Vector3(move * 10f, 0, sway * (0.6f + move * 2f));   // pitch forward into move + gentle roll sway (small, so the raised head doesn't visibly drift off-centre)
        }
        if (_hat != null) { _hat.Position = new Vector3(_headX, _hatBaseY, 0); _hat.RotationDegrees = new Vector3(Mathf.Sin(_t * 1.2f) * 3f, 0, Mathf.Cos(_t * 0.9f) * 3.5f); }
        if (_head != null) _head.RotationDegrees = new Vector3(0, 0, -sway * 2f);   // head counter-tilts the body sway (life)
        if (_hairPivot != null)   // LAGGED SPRING → the mane trails when moving, swings past, and settles (fake hair physics)
        {
            float tgtPitch = move * 8f + Mathf.Sin(_t * 1.6f) * 2f;   // GENTLE trail when travelling + slow idle drift (big values flung it into a jet)
            float tgtRoll = -sway * 3.5f;
            _hairVX += (tgtPitch - _hairSwX) * 55f * dt; _hairVX *= Mathf.Exp(-7f * dt); _hairSwX += _hairVX * dt;
            _hairVZ += (tgtRoll - _hairSwZ) * 55f * dt; _hairVZ *= Mathf.Exp(-7f * dt); _hairSwZ += _hairVZ * dt;
            _hairPivot.RotationDegrees = new Vector3(_hairSwX, 0, _hairSwZ);
        }
        if (_handL != null) _handL.Position = new Vector3(-0.29f, _handBaseY + 0.08f + bob * 0.5f + stride * move * 0.05f, 0.45f + castFwd);
        if (_handR != null) _handR.Position = new Vector3(0.29f, _handBaseY + 0.08f + bob * 0.5f - stride * move * 0.05f, 0.45f + castFwd);
        if (_orb != null) { _orb.Position = new Vector3(0, _handBaseY + 0.10f + bob * 0.5f + Mathf.Sin(_t * 3f) * 0.02f, 0.42f + castFwd); _orb.Scale = Vector3.One * (0.85f + _charge * 0.35f + Mathf.Sin(_t * 4f) * 0.04f); }
    }
}
