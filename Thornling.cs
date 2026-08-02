using Godot;

// Thornling.cs — the Verdant Witch's tree-ent minion (her main damage source). OWNER-SIMULATED AI:
// PickTarget prefers poisoned foes, else nearest, else returns to the witch's side; melee deals
// MinionDamage() Nature + a brief entangle (Root) + a little Poison, and Detonate() (triggered by her
// full-charge thorn passing through) bursts for MinionBurst(). MULTIPLAYER: each Verdant player
// broadcasts its ents via Net.MinionSnapshot; everyone else renders Ghost=true copies (no AI/damage)
// that follow the synced transform and play the attack lunge. AnimateBody() is shared by both.
public partial class Thornling : Node3D
{
    public Player Caster;
    public int Slot = 0;
    public float BodyYaw => _body != null ? _body.Rotation.Y : 0f;
    private float _atkCd = 0f;
    private float _phase = 0f;
    private float _retarget = 0f;
    private Enemy _tgt;
    private Node3D _body;
    private MeshInstance3D _thorns;   // Barkskin thorn shell — shown while the owning witch has Barkskin up
    private float _vy = 0f;
    public bool Ghost = false;        // network copy on an ally's screen: follows synced transform, no AI/damage
    public float AtkPulse = 0f;       // briefly >0 after an attack (sent over the wire)
    public float Fuse = 0f;           // Wild Swarm: >0 = auto-detonate when it reaches 0
    private float _atkAnim = 0f;      // drives the lunge
    private Vector3 _gpos; private float _gyaw = 0f; private bool _gInit = false;
    public void SetGhost(Vector3 pos, float yaw, bool atk)
    {
        _gpos = pos; _gyaw = yaw;
        if (!_gInit) { GlobalPosition = pos; _gInit = true; }
        if (atk) _atkAnim = 0.32f;
    }

    // --- ally-unit stats: ents are now real units that take damage, heal, and can die ---
    public float Hp = 0f, MaxHp = 0f;        // owner-sim; scaled to a fraction of the witch's HP on spawn
    public float GhostHpFrac = 1f;           // ghosts render their bar from this synced fraction
    public float HpFrac => MaxHp > 0f ? Mathf.Clamp(Hp / MaxHp, 0f, 1f) : 1f;
    private bool _dead = false;
    private float _slowT = 0f;               // negative status (enemy contact slows the ent)
    private float _blessT = 0f;              // (NEW) blessed: a friendly holy blessing that slowly mends the ent
    private float _windBoonT = 0f;           // Eyewall: move/attack-speed buff while in an ally's hurricane (NEW)
    public void GrantWindBoon(float dur) { if (!Ghost) _windBoonT = Mathf.Max(_windBoonT, dur); }   // (NEW)
    private float _contactCd = 0f;           // throttles incoming enemy contact damage
    private float _hurtPunch = 0f;           // brief squash on taking a hit
    private Node3D _hpBar; private MeshInstance3D _hpFill; private const float HpBarW = 0.95f;

    private const float Speed = 7.5f;
    private const float Reach = 2.2f;
    private const float SightR = 30f;

    // Shared cute tree-ent visual — built by the live Thornling AND the Wild Swarm stampede critters so they always
    // match. Constructs all meshes under `body`; hands back the sub-nodes that get animated (feet, arms, tuft, eyes,
    // motes). Callers that don't animate can pass discards (out _). A round BARK body so it reads brown, not green.
    // `segs` > 0 builds the SAME body at a low tessellation — the stampede bakes 80 of these into one MultiMesh, so
    // its critters must be cheap; the live ent (segs = 0) keeps Godot's default smooth primitives.
    public static void BuildEntBody(Node3D body, out Node3D footL, out Node3D footR, out Node3D armL, out Node3D armR, out Node3D tuft, out Node3D eyeL, out Node3D eyeR, out Node3D motes, bool detailed = true, int segs = 0)
    {
        int rs = segs > 0 ? segs : 64, rg = segs > 0 ? Mathf.Max(2, segs / 2) : 32;   // SphereMesh defaults are 64/32
        SphereMesh Sp(float r, float h) => new SphereMesh { Radius = r, Height = h, RadialSegments = rs, Rings = rg };
        CylinderMesh Cy(float top, float bot, float h) => new CylinderMesh { TopRadius = top, BottomRadius = bot, Height = h, RadialSegments = rs };
        var bark = Game.ToonEmissive(new Color(0.44f, 0.30f, 0.17f), 0.28f, 0.03f);   // mid brown bark
        var barkDark = Game.ToonEmissive(new Color(0.30f, 0.20f, 0.11f), 0.22f, 0.03f);   // knots / feet
        var barkWarm = Game.ToonEmissive(new Color(0.57f, 0.41f, 0.24f), 0.30f, 0.03f);   // lighter belly / muzzle
        var leaf = Game.ToonEmissive(new Color(0.30f, 0.70f, 0.34f), 0.6f, 0.04f);
        var leafDk = Game.ToonEmissive(new Color(0.20f, 0.50f, 0.24f), 0.5f, 0.04f);
        var glow = Game.ToonEmissive(new Color(0.65f, 1f, 0.55f), 2.2f, 0.02f);   // eye pupils
        var white = Game.ToonEmissive(new Color(0.96f, 1f, 0.93f), 0.6f, 0f);     // sclera + eye highlight
        var cheek = Game.ToonEmissive(new Color(1f, 0.55f, 0.45f), 0.5f, 0f);     // rosy cheeks
        var flower = Game.ToonEmissive(new Color(1f, 0.82f, 0.35f), 1.4f, 0f);    // little bloom accent
        void Add(Node3D p, Mesh m, Material mat, Vector3 pos, Vector3 rotDeg = default)
        { var mi = new MeshInstance3D { Mesh = m, MaterialOverride = mat }; mi.Position = pos; mi.RotationDegrees = rotDeg; p.AddChild(mi); }
        MeshInstance3D M(Node3D p, Mesh m, Material mat, Vector3 pos)
        { var mi = new MeshInstance3D { Mesh = m, MaterialOverride = mat }; mi.Position = pos; p.AddChild(mi); return mi; }

        // two stubby root-feet — animated for a little waddle
        footL = new Node3D { Position = new Vector3(-0.2f, 0.14f, 0.02f) }; body.AddChild(footL);
        Add(footL, Sp(0.17f, 0.3f), barkDark, Vector3.Zero);
        footR = new Node3D { Position = new Vector3(0.2f, 0.14f, 0.02f) }; body.AddChild(footR);
        Add(footR, Sp(0.17f, 0.3f), barkDark, Vector3.Zero);

        // round bark body (torso + head in one lump) — this is the brown mass that fixes "too green"
        M(body, Sp(0.52f, 1.04f), bark, new Vector3(0, 0.74f, 0)).Scale = new Vector3(1f, 1.12f, 0.96f);
        Add(body, Sp(0.34f, 0.6f), barkWarm, new Vector3(0, 0.6f, 0.3f));      // lighter belly patch (two-tone)
        if (detailed)
        {
            Add(body, Sp(0.12f, 0.24f), barkDark, new Vector3(-0.35f, 0.98f, 0.12f));   // bark knots / grain
            Add(body, Sp(0.09f, 0.18f), barkDark, new Vector3(0.34f, 0.5f, 0.2f));
        }

        // cute face: big eyes (sclera + glowing pupil + highlight), rosy cheeks, a tiny mouth
        eyeL = new Node3D { Position = new Vector3(-0.19f, 0.93f, 0.36f) }; body.AddChild(eyeL);
        M(eyeL, Sp(0.13f, 0.26f), white, Vector3.Zero).Scale = new Vector3(1f, 1.15f, 0.7f);
        M(eyeL, Sp(0.08f, 0.16f), glow, new Vector3(0.01f, -0.01f, 0.08f));
        eyeR = new Node3D { Position = new Vector3(0.19f, 0.93f, 0.36f) }; body.AddChild(eyeR);
        M(eyeR, Sp(0.13f, 0.26f), white, Vector3.Zero).Scale = new Vector3(1f, 1.15f, 0.7f);
        M(eyeR, Sp(0.08f, 0.16f), glow, new Vector3(-0.01f, -0.01f, 0.08f));
        if (detailed)
        {
            M(eyeL, Sp(0.03f, 0.06f), white, new Vector3(0.045f, 0.05f, 0.13f));       // eye highlights
            M(eyeR, Sp(0.03f, 0.06f), white, new Vector3(-0.045f, 0.05f, 0.13f));
            M(body, Sp(0.09f, 0.18f), cheek, new Vector3(-0.34f, 0.8f, 0.32f)).Scale = new Vector3(1f, 0.7f, 0.4f);
            M(body, Sp(0.09f, 0.18f), cheek, new Vector3(0.34f, 0.8f, 0.32f)).Scale = new Vector3(1f, 0.7f, 0.4f);
            M(body, Sp(0.05f, 0.1f), barkDark, new Vector3(0, 0.75f, 0.44f)).Scale = new Vector3(1.5f, 0.7f, 0.6f);   // mouth
        }

        // leafy tuft "hair" — small so plenty of bark still shows; a sprig + bloom for cuteness
        tuft = new Node3D { Position = new Vector3(0, 1.2f, 0) }; body.AddChild(tuft);
        Add(tuft, Sp(0.26f, 0.5f), leaf, new Vector3(0, 0.06f, 0));
        Add(tuft, Sp(0.2f, 0.4f), leafDk, new Vector3(0.2f, 0f, 0.06f));
        if (detailed)
        {
            Add(tuft, Sp(0.18f, 0.36f), leaf, new Vector3(-0.19f, 0.02f, -0.05f));
            Add(tuft, Sp(0.15f, 0.3f), leafDk, new Vector3(0.02f, 0.14f, -0.14f));
            Add(tuft, Cy(0.015f, 0.03f, 0.28f), barkWarm, new Vector3(0.06f, 0.28f, 0.02f), new Vector3(0, 0, -12));
            Add(tuft, Sp(0.07f, 0.14f), flower, new Vector3(0.03f, 0.42f, 0.03f));
        }

        // stubby branch arms with little leaf hands
        armL = new Node3D { Position = new Vector3(-0.42f, 0.85f, 0) }; body.AddChild(armL);
        Add(armL, Cy(0.05f, 0.08f, 0.5f), bark, new Vector3(-0.1f, -0.18f, 0), new Vector3(0, 0, 32));
        Add(armL, Sp(0.13f, 0.26f), leaf, new Vector3(-0.22f, -0.36f, 0));
        armR = new Node3D { Position = new Vector3(0.42f, 0.85f, 0) }; body.AddChild(armR);
        Add(armR, Cy(0.05f, 0.08f, 0.5f), bark, new Vector3(0.1f, -0.18f, 0), new Vector3(0, 0, -32));
        Add(armR, Sp(0.13f, 0.26f), leaf, new Vector3(0.22f, -0.36f, 0));

        // a few drifting spore motes around its head (detail only)
        motes = new Node3D { Position = new Vector3(0, 1.3f, 0) }; body.AddChild(motes);
        if (detailed)
            for (int i = 0; i < 3; i++) M(motes, Sp(0.035f, 0.07f), glow, new Vector3(Mathf.Cos(i * 2.09f) * 0.4f, 0, Mathf.Sin(i * 2.09f) * 0.4f));
    }

    public override void _Ready()
    {
        _body = new Node3D();
        AddChild(_body);
        BuildEntBody(_body, out _footL, out _footR, out _armL, out _armR, out _tuft, out _eyeL, out _eyeR, out _motes);
        AddChild(new OmniLight3D { Position = new Vector3(0, 1.0f, 0), OmniRange = 3.4f, LightColor = new Color(0.4f, 0.9f, 0.45f), LightEnergy = 0.5f });

        // a persistent floating nameplate so allies (and you) can spot ents in a crowd
        var plate = new Label3D {
            Text = "Ent",
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            Modulate = new Color(0.62f, 1f, 0.62f),
            OutlineModulate = new Color(0, 0, 0, 0.92f), OutlineSize = 8, FontSize = 30, PixelSize = 0.0058f,
            NoDepthTest = true, RenderPriority = 9, Position = new Vector3(0, 2.75f, 0)
        };
        AddChild(plate);

        // Barkskin thorn shell (matches the player/avatar version, scaled to the ent). Hidden until the owner barks.
        _thorns = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.85f, Height = 1.7f } };
        _thorns.Position = new Vector3(0, 0.95f, 0);
        _thorns.MaterialOverride = new StandardMaterial3D {
            AlbedoColor = new Color(0.30f, 0.85f, 0.40f, 0.18f),
            EmissionEnabled = true, Emission = new Color(0.35f, 0.95f, 0.45f), EmissionEnergyMultiplier = 1.3f,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled
        };
        var spikeMat = new StandardMaterial3D {
            AlbedoColor = new Color(0.22f, 0.55f, 0.26f),
            EmissionEnabled = true, Emission = new Color(0.30f, 0.80f, 0.35f), EmissionEnergyMultiplier = 0.8f
        };
        int spikes = 11;
        for (int i = 0; i < spikes; i++)
        {
            float u = (i + 0.5f) / spikes;
            float theta = u * Mathf.Tau * 3f;
            float yy = 1f - 2f * u;
            float ring = Mathf.Sqrt(Mathf.Max(0f, 1f - yy * yy));
            var dir = new Vector3(Mathf.Cos(theta) * ring, yy, Mathf.Sin(theta) * ring);
            var spike = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.08f, Height = 0.34f }, MaterialOverride = spikeMat };
            spike.Position = dir * 0.85f;
            var axis = Vector3.Up.Cross(dir);
            float ang = Mathf.Acos(Mathf.Clamp(Vector3.Up.Dot(dir), -1f, 1f));
            spike.Basis = axis.LengthSquared() > 1e-5f ? new Basis(axis.Normalized(), ang) : Basis.Identity;
            _thorns.AddChild(spike);
        }
        _thorns.Visible = false;
        AddChild(_thorns);
        // (was an x-ray silhouette here — removed: its green translucent shell washed out the new brown-bark body.
        //  the floating "Ent" nameplate above now handles find-in-a-crowd instead.)
        if (!Ghost && Caster != null && GodotObject.IsInstanceValid(Caster))
        { MaxHp = Caster.S.MaxHp * 0.28f; Hp = MaxHp; }   // a fraction of the witch's HP — re-summoned ents scale as she levels
        BuildHpBar();
        if (!Ghost && Caster != null && GodotObject.IsInstanceValid(Caster) && Caster.EntElementChosen) SetElement(Caster.EntElement);   // (NEW) inherit the witch's Grafted Element look
        if (!Ghost && Game.I != null && GD.Randf() < 0.12f) Say("LEEEEROOOOY JENKINS!", 3, new Color(1f, 0.85f, 0.4f));   // rare spawn warcry
    }

    // a small billboarded health bar above the canopy (shown once damaged; ghosts use the synced fraction)
    private void BuildHpBar()
    {
        _hpBar = new Node3D { Position = new Vector3(0, 2.4f, 0), Visible = false };
        AddChild(_hpBar);
        Material Bar(Color c) => new StandardMaterial3D
        {
            AlbedoColor = c, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled, NoDepthTest = true,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha, RenderPriority = 9
        };
        var bg = new MeshInstance3D { Mesh = new QuadMesh { Size = new Vector2(HpBarW, 0.13f) }, MaterialOverride = Bar(new Color(0, 0, 0, 0.7f)) };
        _hpBar.AddChild(bg);
        _hpFill = new MeshInstance3D { Mesh = new QuadMesh { Size = new Vector2(HpBarW, 0.11f) }, MaterialOverride = Bar(new Color(0.45f, 0.9f, 0.4f, 0.95f)) };
        _hpFill.Position = new Vector3(0, 0, 0.001f);
        _hpBar.AddChild(_hpFill);
    }

    // left-anchored shrink + green→red tint; hides at full health
    private void UpdateHpBar(float frac)
    {
        if (_hpBar == null) return;
        bool show = frac < 0.999f && frac > 0f;
        _hpBar.Visible = show;
        if (!show) return;
        _hpFill.Scale = new Vector3(Mathf.Max(0.0001f, frac), 1f, 1f);
        _hpFill.Position = new Vector3(-HpBarW * 0.5f * (1f - frac), 0, 0.001f);
        var mat = (StandardMaterial3D)_hpFill.MaterialOverride;
        mat.AlbedoColor = new Color(Mathf.Lerp(0.95f, 0.45f, frac), Mathf.Lerp(0.25f, 0.9f, frac), 0.35f, 0.95f);
    }
    private Node3D _armL, _armR, _footL, _footR, _tuft, _eyeL, _eyeR, _motes;
    private float _moveAmt = 0f, _targetMove = 0f, _motePhase = 0f, _blink = 2f, _blinkPhase = 0f, _leafT = 3f;

    public void SetThorns(bool on) { if (_thorns != null && _thorns.Visible != on) _thorns.Visible = on; }

    // (NEW) Grafted Element: give the ent a themed adornment on its head for the chosen damage type. Nature = the default
    // look (no adornment). Rebuilt each call; rides the body so it turns with the ent.
    private DamageType _element = DamageType.Nature; private bool _hasElement = false;
    private Node3D _elemNode; private float _elemPhase = 0f;
    public void SetElement(DamageType e)
    {
        _element = e; _hasElement = e != DamageType.Nature;
        if (_elemNode != null && GodotObject.IsInstanceValid(_elemNode)) { _elemNode.QueueFree(); _elemNode = null; }
        if (!_hasElement || _body == null) return;
        var col = DamageTypes.Col(e);
        var mat = Game.ToonEmissive(col, 2.6f, 0f);
        _elemNode = new Node3D { Position = new Vector3(0, 1.75f, 0.05f) };
        _body.AddChild(_elemNode);
        if (e == DamageType.Ember)   // a flickering flame crest
        {
            for (int i = 0; i < 3; i++)
            {
                float s = 0.22f - i * 0.05f;
                var f = new MeshInstance3D { Mesh = new SphereMesh { Radius = s, Height = s * 2f }, MaterialOverride = mat };
                f.Position = new Vector3(0, 0.18f + i * 0.16f, 0);
                _elemNode.AddChild(f);
            }
        }
        else if (e == DamageType.Frost)   // a crown of ice shards
        {
            for (int i = 0; i < 4; i++)
            {
                float a = i / 4f * Mathf.Tau;
                var c = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.08f, Height = 0.34f }, MaterialOverride = mat };
                c.Position = new Vector3(Mathf.Cos(a) * 0.18f, 0.14f, Mathf.Sin(a) * 0.18f);
                c.RotationDegrees = new Vector3(0, 0, Mathf.Cos(a) * 18f);
                _elemNode.AddChild(c);
            }
        }
        else   // a glowing orb + two orbiting motes (arcane / curse / holy / lunar / wind / blood)
        {
            var core = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.15f, Height = 0.3f }, MaterialOverride = mat };
            core.Position = new Vector3(0, 0.2f, 0); _elemNode.AddChild(core);
            for (int i = 0; i < 2; i++)
            {
                var b = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.07f, Height = 0.14f }, MaterialOverride = mat };
                b.Position = new Vector3(i == 0 ? 0.22f : -0.22f, 0.24f, 0); _elemNode.AddChild(b);
            }
        }
        _elemNode.AddChild(new OmniLight3D { OmniRange = 2.5f, LightColor = col, LightEnergy = 1.2f, Position = new Vector3(0, 0.2f, 0) });
    }

    public override void _Process(double delta)
    {
        if (Game.I == null || !Game.I.SimActive) return;   // freeze while paused (NEW)
        float dt = (float)delta;
        if (!Ghost && Fuse > 0f)   // Wild Swarm: count down and blow up on its own (unless detonated sooner)
        {
            Fuse -= dt;
            if (Fuse <= 0f) { Detonate(); return; }
        }
        if (_atkAnim > 0f) _atkAnim -= dt;
        if (AtkPulse > 0f) AtkPulse -= dt;

        if (Ghost)   // network copy: follow synced transform, play lunge from synced attacks
        {
            _targetMove = GlobalPosition.DistanceTo(_gpos) > 0.06f ? 1f : 0f;
            GlobalPosition = GlobalPosition.Lerp(_gpos, Mathf.Clamp(dt * 12f, 0f, 1f));
            _phase += dt * 6f;
            _body.Rotation = new Vector3(0, Mathf.LerpAngle(_body.Rotation.Y, _gyaw, dt * 10f), 0);
            AnimateBody(dt);
            UpdateHpBar(GhostHpFrac);
            return;
        }

        if (Caster == null || !GodotObject.IsInstanceValid(Caster)) return;
        SetThorns(Caster.BarkActive);   // live ent: mirror the owner's Barkskin (ghosts are driven from MinionSnapshot)
        if (!Game.I.WorldRunning) return;
        if (_atkCd > 0f) _atkCd -= dt;
        _retarget -= dt;
        var prevTgt = _tgt;
        if (_retarget <= 0f || _tgt == null || !GodotObject.IsInstanceValid(_tgt) || _tgt.Dead) { _retarget = 0.35f; _tgt = PickTarget(); }
        if (!Ghost && _tgt != null && _tgt != prevTgt && GodotObject.IsInstanceValid(_tgt) && !_tgt.Dead && GD.Randf() < 0.18f)
            Say(GD.Randf() < 0.5f ? "For Motherrr!" : "Kill kill!", 1, new Color(0.6f, 0.95f, 0.5f));

        Vector3 goal;
        bool combat = _tgt != null && GodotObject.IsInstanceValid(_tgt) && !_tgt.Dead && Game.I.MazeHasLoS(GlobalPosition, _tgt.GlobalPosition);   // drop the chase if it loses sight (maze hedges)
        if (combat) goal = _tgt.GlobalPosition;
        else
        {
            // no foes — drift back to the witch's side
            float a = Slot * 2.3f;
            goal = Caster.GlobalPosition + new Vector3(Mathf.Cos(a) * 2.6f, 0, Mathf.Sin(a) * 2.6f);
        }

        if (_slowT > 0f) _slowT -= dt;
        if (_blessT > 0f) _blessT -= dt;   // (FIX) bless no longer direct-heals — it only amplifies healing RECEIVED (see Heal below)
        if (_windBoonT > 0f) _windBoonT -= dt;   // Eyewall buff decays (NEW)
        if (_hurtPunch > 0f) _hurtPunch -= dt;
        Vector3 to = goal - GlobalPosition; to.Y = 0f;
        float dist = to.Length();
        float stop = combat ? Reach : 0.4f;
        float spd = Speed * (_slowT > 0f ? 0.5f : 1f) * (_windBoonT > 0f ? 1.35f : 1f);   // Eyewall move-speed buff (NEW)
        if (dist > stop)
        {
            var dir = to.Normalized();
            GlobalPosition += dir * spd * dt;
            float yaw = Mathf.Atan2(dir.X, dir.Z);
            _body.Rotation = new Vector3(0, Mathf.LerpAngle(_body.Rotation.Y, yaw, dt * 9f), 0);
            _phase += dt * 9f;
        }
        else _phase += dt * 2f;

        GlobalPosition = PushOutSolids(GlobalPosition);   // (NEW) minions collide with trees + structure walls too
        // follow the ground
        float gy = Game.I.SurfaceHeight(GlobalPosition, GlobalPosition.Y);
        GlobalPosition = new Vector3(GlobalPosition.X, Mathf.MoveToward(GlobalPosition.Y, gy, 12f * dt), GlobalPosition.Z);

        _targetMove = dist > stop ? 1f : 0f;
        AnimateBody(dt);

        if (combat && dist <= stop + _tgt.Radius && _atkCd <= 0f)
        {
            _atkCd = Mathf.Clamp(0.8f * (Caster.S.FireCd / 0.28f), 0.4f, 1.1f) * (_windBoonT > 0f ? 0.7f : 1f);   // attack speed tracks the witch's cast rate; Eyewall hastens it (NEW)
            _atkAnim = 0.32f; AtkPulse = 0.2f;                 // lunge (synced to allies)
            float dmg = Caster.MinionStrike(Caster.MinionDamage(), out bool crit);
            _tgt.Hurt(dmg, DamageType.Nature, true, crit); _tgt.HitFrom(GlobalPosition);
            _tgt.Root(0.5f);                                   // entangle on hit
            _tgt.Poison(Caster.PoisonDps() * 0.5f, 3f);          // a touch of poison too
            Caster.ComboFromSource();
            Game.I.VfxRing(_tgt.GlobalPosition, new Color(0.4f, 0.85f, 0.4f), 1.6f, 0.25f);
        }

        // ENEMIES ATTACK ENTS: any foe in melee range chips the ent on a cooldown (owner-authoritative,
        // so it works for the host's ents and every client's ents alike — each owner sims its own pack)
        if (_contactCd > 0f) _contactCd -= dt;
        if (!_dead && MaxHp > 0f && _contactCd <= 0f)
        {
            Enemy hitter = null; float best = float.MaxValue;
            foreach (var e in Game.I.Enemies.ToArray())
            {
                if (e == null || e.Dead || !GodotObject.IsInstanceValid(e) || e.Dmg <= 0f) continue;
                float reach = Reach + e.Radius + 0.5f;
                float d = (e.GlobalPosition - GlobalPosition).LengthSquared();
                if (d < reach * reach && d < best) { best = d; hitter = e; }
            }
            if (hitter != null)
            {
                _contactCd = 0.7f;
                Hurt(hitter.Dmg * 0.6f);
                if (hitter.Elite || hitter.Affix > 0) Slow(1.0f);   // tougher foes also briefly hobble the ent (negative affix effect)
            }
        }
        UpdateHpBar(HpFrac);
    }

    // shared cute animation set (used by owner and ghost copies): waddle walk, breathing, attack lunge, hurt squash,
    // eye blinks, a swaying leaf tuft, drifting spore motes, and the occasional falling leaf.
    private void AnimateBody(float dt)
    {
        if (_body == null) return;
        _moveAmt = Mathf.MoveToward(_moveAmt, _targetMove, dt * 6f);
        float walk = _moveAmt;
        float bob = Mathf.Abs(Mathf.Sin(_phase)) * (0.05f + 0.08f * walk);
        float lunge = _atkAnim > 0f ? Mathf.Sin((1f - _atkAnim / 0.32f) * Mathf.Pi) : 0f;   // 0→1→0 thrust
        float hurt = _hurtPunch > 0f ? _hurtPunch / 0.15f : 0f;                              // 1→0 squash on a hit

        // body: bob + forward lunge + gentle breathing + hurt squash + a little waddle roll
        float breathe = 1f + 0.03f * Mathf.Sin(_phase * 0.9f);
        float sqX = 1f + hurt * 0.22f, sqY = (1f - hurt * 0.28f) * breathe;
        _body.Position = new Vector3(0, bob, -lunge * 0.5f);
        _body.Scale = new Vector3(sqX, sqY, sqX);
        _body.Rotation = new Vector3(lunge * 0.35f, _body.Rotation.Y, Mathf.Sin(_phase) * 0.06f * walk);

        // feet: alternate step-lift while walking
        if (_footL != null) _footL.Position = new Vector3(-0.2f, 0.14f + Mathf.Max(0f, Mathf.Sin(_phase)) * 0.12f * walk, 0.02f + Mathf.Cos(_phase) * 0.05f * walk);
        if (_footR != null) _footR.Position = new Vector3(0.2f, 0.14f + Mathf.Max(0f, Mathf.Sin(_phase + Mathf.Pi)) * 0.12f * walk, 0.02f + Mathf.Cos(_phase + Mathf.Pi) * 0.05f * walk);

        // arms swing while moving, then thrust on the attack lunge
        float swing = Mathf.Sin(_phase) * 0.5f * (0.35f + walk);
        if (_armL != null) _armL.Rotation = new Vector3(swing - lunge * 1.4f, 0, 0.3f);
        if (_armR != null) _armR.Rotation = new Vector3(-swing - lunge * 1.4f, 0, -0.3f);

        // leafy tuft sway
        if (_tuft != null) _tuft.Rotation = new Vector3(Mathf.Sin(_phase * 0.8f) * 0.12f, 0, Mathf.Cos(_phase * 0.7f) * 0.12f);

        // eye blink
        _blink -= dt;
        if (_blink <= 0f) { _blink = 2.5f + GD.Randf() * 3f; _blinkPhase = 0.16f; }
        float eyeY = 1f;
        if (_blinkPhase > 0f) { _blinkPhase -= dt; eyeY = Mathf.Lerp(1f, 0.12f, Mathf.Sin(Mathf.Clamp(_blinkPhase / 0.16f, 0f, 1f) * Mathf.Pi)); }
        if (_eyeL != null) _eyeL.Scale = new Vector3(1f, eyeY, 1f);
        if (_eyeR != null) _eyeR.Scale = new Vector3(1f, eyeY, 1f);

        // drifting spore motes around the head
        _motePhase += dt;
        if (_motes != null) { int i = 0; foreach (var c in _motes.GetChildren()) if (c is MeshInstance3D mi) { float a = _motePhase * 1.3f + i * 2.1f; mi.Position = new Vector3(Mathf.Cos(a) * 0.42f, 0.05f + Mathf.Sin(a * 1.7f) * 0.08f, Mathf.Sin(a) * 0.42f); i++; } }

        // an occasional leaf flutters off it
        _leafT -= dt;
        if (_leafT <= 0f) { _leafT = 2.5f + GD.Randf() * 3.5f; DropLeaf(); }

        if (_elemNode != null && _hasElement)   // animate the Grafted-Element crest
        {
            _elemPhase += dt * 3.6f;
            if (_element == DamageType.Ember)   // flicker the flame
            {
                int i = 0;
                foreach (var c in _elemNode.GetChildren())
                    if (c is MeshInstance3D mi) { float f = 0.85f + 0.28f * Mathf.Sin(_elemPhase * 9f + i * 1.3f); mi.Scale = new Vector3(f, 1.15f + 0.3f * Mathf.Sin(_elemPhase * 13f + i), f); i++; }
            }
            else _elemNode.Rotation = new Vector3(0, _elemPhase * 1.6f, 0);   // slow orbit for the others
        }
    }

    // a small leaf flutters off and settles — purely cosmetic, runs locally on owner + ghosts
    private void DropLeaf()
    {
        if (Game.I == null) return;
        var mat = Game.ToonEmissive(GD.Randf() < 0.5f ? new Color(0.30f, 0.70f, 0.34f) : new Color(0.20f, 0.50f, 0.24f), 0.5f, 0.04f);
        var leaf = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.06f, Height = 0.12f }, MaterialOverride = mat };
        leaf.Scale = new Vector3(1.6f, 0.25f, 1f);
        Game.I.AddChild(leaf);
        var start = GlobalPosition + new Vector3((GD.Randf() - 0.5f) * 0.5f, 1.5f, (GD.Randf() - 0.5f) * 0.5f);
        leaf.GlobalPosition = start;
        leaf.Rotation = new Vector3(GD.Randf() * 3f, GD.Randf() * 6f, GD.Randf() * 3f);
        var end = start + new Vector3((GD.Randf() - 0.5f) * 1.3f, -1.5f, (GD.Randf() - 0.5f) * 1.3f);   // flutter down + drift
        var tw = leaf.CreateTween(); tw.SetParallel(true);
        tw.TweenProperty(leaf, "global_position", end, 1.7f).SetEase(Tween.EaseType.InOut);
        tw.TweenProperty(leaf, "rotation", leaf.Rotation + new Vector3(2f, 4f, 2f), 1.7f);
        tw.TweenProperty(leaf, "scale", new Vector3(0.01f, 0.01f, 0.01f), 0.35f).SetDelay(1.35f);
        tw.SetParallel(false);
        tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(leaf)) leaf.QueueFree(); }));
    }

    // (NEW) push the ent out of trees/pillars (Blockers) and structure walls (Decks) so minions can't ghost through
    // them. Mirrors the enemy solid-collision; ~0.5 body radius. Ghost copies follow the synced (already-collided)
    // position, so only the owner runs this.
    private Vector3 PushOutSolids(Vector3 p)
    {
        var g = Game.I; if (g == null) return p;
        const float r = 0.5f;
        var bl = g.Blockers;
        for (int i = 0; i < bl.Count; i++)
        {
            var b = bl[i];
            float ox = p.X - b.Pos.X, oz = p.Z - b.Pos.Z;
            float dd = Mathf.Sqrt(ox * ox + oz * oz);
            float minD = b.Radius + r;
            if (dd < minD) { float k = minD / Mathf.Max(dd, 0.001f); p.X = b.Pos.X + ox * k; p.Z = b.Pos.Z + oz * k; }
        }
        var dk = g.Decks;
        for (int i = 0; i < dk.Count; i++)
        {
            var d = dk[i];
            if (d.TopY < 1.8f || p.Y >= d.TopY - 0.6f) continue;
            float ex = d.Half.X + r, ez = d.Half.Y + r;
            float dx = p.X - d.Center.X, dz = p.Z - d.Center.Z;
            if (Mathf.Abs(dx) < ex && Mathf.Abs(dz) < ez)
            {
                if (ex - Mathf.Abs(dx) < ez - Mathf.Abs(dz)) p.X = d.Center.X + Mathf.Sign(dx) * ex;
                else p.Z = d.Center.Z + Mathf.Sign(dz) * ez;
            }
        }
        return p;
    }

    private Enemy PickTarget()
    {
        Enemy poisoned = null, nearest = null; float pd = SightR * SightR, nd = SightR * SightR;
        foreach (var e in Game.I.Enemies.ToArray())
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
            if (!Game.I.MazeHasLoS(GlobalPosition, e.GlobalPosition)) continue;   // don't hunt what it can't see (grid LOS; no-ops outside the maze) — stops ents jamming into hedges
            float d = (e.GlobalPosition - GlobalPosition).LengthSquared();
            if (e.IsPoisoned && d < pd) { pd = d; poisoned = e; }
            if (d < nd) { nd = d; nearest = e; }
        }
        return poisoned ?? nearest;
    }

    // her full-charge thorn passing through this ent blows it up for a strong Nature burst
    public int Detonate()
    {
        if (_detonated) return 0;          // guard: chain reactions can call this more than once
        if (Caster != null && GodotObject.IsInstanceValid(Caster) && Caster.BarkActive) return 0;   // can't detonate ents during Barkskin
        _detonated = true;
        if (Game.I != null && Game.I.Player != null && Game.I.Player.VerdantWitch) Game.I.MyStats.Highlight++;   // (NEW) Verdant highlight = ents detonated
        Caster?.Ents.Remove(this);         // leave the grove now so a recount/refund this frame is accurate
        if (!Ghost && Game.I != null && GD.Randf() < 0.4f) Say(GD.Randf() < 0.5f ? "Booomm!" : "Oooowww!", 2, new Color(0.5f, 0.9f, 0.5f));
        float dmg = Caster != null ? Caster.MinionBurst() : 60f;
        float rad = 5.5f * (Caster != null ? Caster.S.SpellArea : 1f);   // minion blast grows with spell area
        var dtype = Caster != null ? Caster.EntElement : DamageType.Nature;   // (NEW) Grafted Element — the blast's damage type
        var col = DamageTypes.Col(dtype);
        int kills = 0;
        foreach (var e in Game.I.Enemies.ToArray())
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
            if (new Vector2(e.GlobalPosition.X - GlobalPosition.X, e.GlobalPosition.Z - GlobalPosition.Z).Length() < rad + e.Radius)
            {
                float hit = dmg; bool dcrit = false;
                if (Caster != null && GodotObject.IsInstanceValid(Caster)) hit = Caster.MinionStrike(dmg, out dcrit);   // crit + lifesteal per foe caught
                e.Hurt(hit, dtype, true, dcrit); e.HitFrom(GlobalPosition); e.Poison(Caster != null ? Caster.PoisonDps() : 4f, 4f); e.Root(1.6f); Caster?.ComboFromSource();
                if (Caster != null && GodotObject.IsInstanceValid(Caster) && Caster.EntElementChosen) Caster.ApplyEntStatus(e, GlobalPosition);   // (NEW) element rider (burn/freeze/etc.)
                if (e.Remote ? e.Hp <= hit : e.Dead) kills++;   // host: real death; client: estimate from the synced HP (so a client-Verdant's charged detonation also refunds)
            }   // the blast roots + feeds combo
        }
        // legendary "Wildfire Bloom": each blast sets off nearby ents (the guard keeps it finite); chain kills count too
        if (Caster != null && GodotObject.IsInstanceValid(Caster) && Caster.MinionChain)
            foreach (var t in Caster.Ents.ToArray())
                if (t != null && t != this && GodotObject.IsInstanceValid(t) && GlobalPosition.DistanceTo(t.GlobalPosition) < rad + 0.5f) kills += t.Detonate();
        Game.I.DamageWorld(GlobalPosition, rad, dmg);   // (FIX) the ent blast breaks props in its radius too
        Game.I.VfxRing(GlobalPosition, col, rad + 0.5f, 0.4f);
        var v = new Vfx(); Game.I.AddChild(v); v.GlobalPosition = GlobalPosition + Vector3.Up * 0.6f;
        v.Init(new SphereMesh { Radius = 2.8f, Height = 5.6f }, col, 0.4f, 6f);
        QueueFree();
        return kills;
    }

    // --- ally-unit damage/heal API (owner-side; ghosts ignore — their HP is driven by the synced fraction) ---
    public void Hurt(float dmg)
    {
        if (Ghost || _dead || MaxHp <= 0f || dmg <= 0f) return;
        if (Caster != null && GodotObject.IsInstanceValid(Caster) && Caster.BarkActive) return;   // Barkskin shields the whole grove
        Hp -= dmg; _hurtPunch = 0.15f;
        if (Hp <= 0f) Die();
    }
    public void Heal(float amt)
    {
        if (Ghost || _dead || MaxHp <= 0f || amt <= 0f) return;
        Hp = Mathf.Min(MaxHp, Hp + amt * (_blessT > 0f ? 1.6f : 1f));   // (NEW) bless amplifies healing received
    }
    public void Slow(float dur) { if (!Ghost) _slowT = Mathf.Max(_slowT, dur); }
    public void Bless(float dur) { if (!Ghost) _blessT = Mathf.Max(_blessT, dur); }   // (NEW) friendly holy blessing
    private void Die()
    {
        if (_dead) return; _dead = true;
        var dtype = Caster != null ? Caster.EntElement : DamageType.Nature;   // (NEW) death pop also takes the Grafted Element
        var col = DamageTypes.Col(dtype);
        float drad = 3.0f * (Caster != null ? Caster.S.SpellArea : 1f);
        // death throes: a WEAK pop on its own, deliberately a fraction of a charged detonation and with
        // no root/chain/crit/lifesteal — so actively detonating ents (full-charge right-click) stays the
        // real payoff (~5x the damage + utility), not something you get for free by letting them die.
        if (!Ghost && !_detonated && Game.I != null)
        {
            float dpop = (Caster != null && GodotObject.IsInstanceValid(Caster)) ? Caster.MinionBurst() * 0.22f : 10f;
            foreach (var e in Game.I.Enemies.ToArray())
            {
                if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
                if (new Vector2(e.GlobalPosition.X - GlobalPosition.X, e.GlobalPosition.Z - GlobalPosition.Z).Length() < drad + e.Radius)
                { e.Hurt(dpop, dtype, true); e.HitFrom(GlobalPosition); e.Poison(Caster != null ? Caster.PoisonDps() * 0.5f : 2f, 3f); if (Caster != null && Caster.EntElementChosen) Caster.ApplyEntStatus(e, GlobalPosition); }
            }
        }
        Game.I?.VfxRing(GlobalPosition, col, drad - 0.6f, 0.3f);            // smaller, dimmer than a detonation nova
        var v = new Vfx(); Game.I?.AddChild(v); v.GlobalPosition = GlobalPosition + Vector3.Up * 0.6f;
        v.Init(new SphereMesh { Radius = 1.1f, Height = 2.2f }, col, 0.3f, 5f);
        if (!Ghost && Game.I != null && GD.Randf() < 0.5f) Say(GD.Randf() < 0.5f ? "Auuugh!" : "For... the grove...", 2, col);
        QueueFree();   // the witch's CountEnts prunes the freed ent next snapshot
    }
    private bool _detonated = false;

    private static ulong _lastSayMs = 0;
    // owner-side: throttle, speak locally, and broadcast so allies see/hear it on the synced ghost
    private void Say(string text, int voiceKind, Color col)
    {
        if (Ghost || Game.I == null) return;
        ulong now = Time.GetTicksMsec();
        if (now - _lastSayMs < 750) return;
        _lastSayMs = now;
        SpeakAt(GlobalPosition, text, voiceKind, col);
        Game.I.NetMgr?.BroadcastMinionSay(GlobalPosition, text, voiceKind, col);
    }

    // renders the floating speech line + plays the squeaky voice at a world position (called locally AND from the say RPC)
    public static void SpeakAt(Vector3 pos, string text, int voiceKind, Color col)
    {
        if (Game.I == null) return;
        Game.I.Sfx?.Minion(voiceKind);
        var lbl = new Label3D
        {
            Text = text, FontSize = 64, PixelSize = 0.0045f,
            Modulate = col, OutlineSize = 14, OutlineModulate = new Color(0, 0, 0, 0.95f),
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, NoDepthTest = true, RenderPriority = 10
        };
        Game.I.AddChild(lbl);
        lbl.GlobalPosition = pos + new Vector3(0, 2.1f, 0);
        var tw = lbl.CreateTween(); tw.SetParallel(true);
        tw.TweenProperty(lbl, "global_position", lbl.GlobalPosition + new Vector3(0, 1.0f, 0), 1.2f);
        tw.TweenProperty(lbl, "modulate:a", 0f, 1.2f).SetDelay(0.45f);
        tw.SetParallel(false);
        tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(lbl)) lbl.QueueFree(); }));
    }
}
