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
    public void PlayArm(string kind, float dur) { _armKind = kind; _armT = 0f; _armDur = dur; }
    private bool _fp = false;

    public static Color WitchColor(int witchIdx) => witchIdx switch
    {
        1 => DamageTypes.Col(DamageType.Holy),    // Divine
        2 => DamageTypes.Col(DamageType.Blood),   // Crimson Blood
        3 => DamageTypes.Col(DamageType.Nature),  // Verdant
        4 => DamageTypes.Col(DamageType.Wind),    // Gale (NEW)
        5 => DamageTypes.Col(DamageType.Frost),   // Frost (NEW)
        6 => DamageTypes.Col(DamageType.Curse),   // Forsaken (NEW)
        7 => DamageTypes.Col(DamageType.Ember),   // Ember (NEW)
        _ => DamageTypes.Col(DamageType.Lunar),   // Lunar (default)
    };

    public void Build(int witchIdx, bool firstPerson)
    {
        _fp = firstPerson;
        Color c = WitchColor(witchIdx);
        var robe = Game.ToonEmissive(new Color(c.R * 0.5f, c.G * 0.5f, c.B * 0.5f), 0.45f, 0.03f);
        var trim = Game.ToonEmissive(c, 1.5f, 0.02f);
        var skin = Game.ToonEmissive(new Color(0.86f, 0.78f, 0.72f), 0.35f, 0.02f);
        var gem = Game.ToonEmissive(c, 3.2f, 0f);                       // (GLAM) bright accent for gems / per-witch signatures
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
            default:  // Lunar — a crescent moon crowning the hat + tiny orbiting moons
                Add(_hat, new TorusMesh { InnerRadius = 0.1f, OuterRadius = 0.14f }, gem, new Vector3(0, 0.66f, 0.02f), new Vector3(78, 0, 18));
                Add(_hat, new SphereMesh { Radius = 0.035f, Height = 0.07f }, gem, new Vector3(-0.22f, 0.5f, 0));
                Add(_hat, new SphereMesh { Radius = 0.03f, Height = 0.06f }, gem, new Vector3(0.24f, 0.42f, 0.03f));
                break;
        }
    }

    public void ShowWings(bool on)
    {
        _wingsOn = on;
        if (_wingL != null) _wingL.Visible = on;
        if (_wingR != null) _wingR.Visible = on;
    }

    public void Collapse(bool down)
    {
        if (_root != null) _root.RotationDegrees = down ? new Vector3(82, 0, 0) : Vector3.Zero;
    }

    public void Animate(double delta, float speed01, bool airborne)
    {
        if (_root == null) return;
        float dt = (float)delta;
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
        if (_armDur > 0f && _armL != null && _armR != null)
        {
            _armT += dt; float k = Mathf.Clamp(_armT / _armDur, 0f, 1f); float e = Mathf.Sin(k * Mathf.Pi);
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
                case "barrage": { float f = Mathf.Abs(Mathf.Sin(k * Mathf.Pi * 3f)); rr.X = Mathf.Lerp(rr.X, 1.6f, f); lr.X = Mathf.Lerp(lr.X, 1.6f, 1f - f); break; }
                case "grdpunch": { float w = Mathf.Clamp(k / 0.35f, 0, 1), dr = Mathf.Clamp((k - 0.35f) / 0.65f, 0, 1); float up = w * (1 - dr); lr.X = Mathf.Lerp(lr.X, 2.0f, up); rr.X = Mathf.Lerp(rr.X, 2.0f, up); lr.X = Mathf.Lerp(lr.X, -0.7f, dr); rr.X = Mathf.Lerp(rr.X, -0.7f, dr); break; }
            }
            _armL.Rotation = lr; _armR.Rotation = rr;
            if (k >= 1f) _armDur = 0f;
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
