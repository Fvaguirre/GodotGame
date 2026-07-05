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
        _ => DamageTypes.Col(DamageType.Lunar),   // Lunar (default)
    };

    public void Build(int witchIdx, bool firstPerson)
    {
        _fp = firstPerson;
        Color c = WitchColor(witchIdx);
        var robe = Game.ToonEmissive(new Color(c.R * 0.5f, c.G * 0.5f, c.B * 0.5f), 0.45f, 0.03f);
        var trim = Game.ToonEmissive(c, 1.5f, 0.02f);
        var skin = Game.ToonEmissive(new Color(0.86f, 0.78f, 0.72f), 0.35f, 0.02f);

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
        Add(_skirt, new CylinderMesh { TopRadius = 0.2f, BottomRadius = 0.56f, Height = 0.74f }, robe, Vector3.Zero);
        Add(_skirt, new TorusMesh { InnerRadius = 0.5f, OuterRadius = 0.6f }, trim, new Vector3(0, -0.36f, 0), new Vector3(90, 0, 0));   // glowing hem

        // torso
        _torso = new Node3D { Position = new Vector3(0, 1.18f, 0) };
        _root.AddChild(_torso);
        Add(_torso, new CylinderMesh { TopRadius = 0.18f, BottomRadius = 0.24f, Height = 0.5f }, robe, Vector3.Zero);
        Add(_torso, new CylinderMesh { TopRadius = 0.19f, BottomRadius = 0.19f, Height = 0.08f }, trim, new Vector3(0, 0.12f, 0));   // collar glow

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

        // head
        Add(_root, new SphereMesh { Radius = 0.17f, Height = 0.34f }, skin, new Vector3(0, 1.62f, 0));

        // witch hat (brim + cone), tilts a little while moving
        _hat = new Node3D { Position = new Vector3(0, 1.74f, 0) };
        _root.AddChild(_hat);
        Add(_hat, new CylinderMesh { TopRadius = 0.42f, BottomRadius = 0.46f, Height = 0.05f }, trim, new Vector3(0, 0f, 0.02f));
        Add(_hat, new CylinderMesh { TopRadius = 0.0f, BottomRadius = 0.3f, Height = 0.62f }, robe, new Vector3(0, 0.34f, 0.04f), new Vector3(-6, 0, 0));
        Add(_hat, new TorusMesh { InnerRadius = 0.16f, OuterRadius = 0.2f }, trim, new Vector3(0, 0.08f, 0.03f), new Vector3(90, 0, 0));

        // arms (third-person only; FP uses the camera hands). Pivot at the shoulder, mesh hangs down.
        _armL = new Node3D { Position = new Vector3(-0.27f, 1.32f, 0) }; _root.AddChild(_armL);
        Add(_armL, new CapsuleMesh { Radius = 0.07f, Height = 0.55f }, robe, new Vector3(0, -0.26f, 0));
        Add(_armL, new SphereMesh { Radius = 0.075f, Height = 0.15f }, skin, new Vector3(0, -0.52f, 0));   // hand
        _armR = new Node3D { Position = new Vector3(0.27f, 1.32f, 0) }; _root.AddChild(_armR);
        Add(_armR, new CapsuleMesh { Radius = 0.07f, Height = 0.55f }, robe, new Vector3(0, -0.26f, 0));
        Add(_armR, new SphereMesh { Radius = 0.075f, Height = 0.15f }, skin, new Vector3(0, -0.52f, 0));
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
