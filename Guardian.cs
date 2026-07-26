using Godot;

// Guardian.cs — the "Ancient Guardian" ultimate: a colossal, ancient tree-ent guardian summoned at the cursor.
// It lurches toward the nearest foe and performs several telegraphed ground stomps. Each stomp plays a FULL
// animation BEFORE it lands — a slow overhead wind-up (arms raised, torso leaning back, a great root-foot lifting),
// then a crashing SLAM on the down-beat that deals radial-falloff damage (devastating at the core, ~40% at the rim),
// flings light foes into the air, cracks the ground and kicks the camera. Caster-simulated; damage routes through
// Enemy.Hurt so it forwards to the host on a client, and the fling arc syncs through the enemy snapshot. MULTIPLAYER:
// the owner broadcasts its transform + the exact slam PHASE (Net.GuardianState) every tick, so ghost copies replay
// the whole wind-up-and-slam in lockstep — not just the impact. Legendary "Heartwood" adds slams, radius, poison and
// a root per stomp. Withers and frees after its last slam.
public partial class Guardian : Node3D
{
    public Player Caster;
    public int Slams = 4;
    public float SlamRadius = 7f;
    public float SlamDamage = 80f;     // value at the impact point; tapers to 40% at the rim
    public float Poison = 0f;
    public bool RootOnSlam = false;

    public bool Ghost = false;         // network copy on an ally's screen: follows synced transform, no AI/damage
    public float BodyYaw => _body != null ? _body.Rotation.Y : 0f;

    // --- slam timeline: one stomp runs 0→SlamDur; the hit lands on the down-beat at ImpactK ---
    private const float SlamDur = 0.95f, ImpactK = 0.60f, RestDur = 0.4f;
    private float _slamT = -1f;        // -1 = resting between slams; else time into the current slam
    private bool _impactDone = false;
    private float _rest = 0.65f;       // wind-up before the very first slam
    private int _done = 0;
    private Vector3 _stepTarget; private bool _stepping = false;
    public float SlamPhase01() => _slamT < 0f ? -1f : Mathf.Clamp(_slamT / SlamDur, 0f, 1f);   // sent to allies each tick

    // --- ghost sync ---
    private Vector3 _gpos; private float _gyaw = 0f; private bool _gInit = false; private float _idle = 0f;
    private float _ghostSlam = -1f, _prevGhostSlam = -1f;
    public void SetGhost(Vector3 pos, float yaw, float slamPhase) { _gpos = pos; _gyaw = yaw; _gInit = true; _idle = 0f; _ghostSlam = slamPhase; }

    private Node3D _body, _armL, _armR, _footL, _footR, _eyeL, _eyeR;
    private Vector3 _footLBase, _footRBase;
    private float _phase = 0f;

    private void Add(Node3D parent, Mesh m, Material mat, Vector3 pos, Vector3 roteul = default)
    {
        var mi = new MeshInstance3D { Mesh = m, MaterialOverride = mat };
        mi.Position = pos; mi.RotationDegrees = roteul; parent.AddChild(mi);
    }
    private MeshInstance3D M(Node3D parent, Mesh m, Material mat, Vector3 pos)
    { var mi = new MeshInstance3D { Mesh = m, MaterialOverride = mat }; mi.Position = pos; parent.AddChild(mi); return mi; }

    public override void _Ready()
    {
        var bark = new Color(0.31f, 0.22f, 0.13f);
        var darkBark = new Color(0.21f, 0.15f, 0.09f);
        var warmBark = new Color(0.44f, 0.32f, 0.19f);
        var leaf = new Color(0.27f, 0.55f, 0.27f);
        var leafDk = new Color(0.19f, 0.42f, 0.21f);
        var moss = new Color(0.36f, 0.54f, 0.30f);
        var eye = new Color(0.75f, 1f, 0.5f);
        var barkMat = Game.ToonEmissive(bark, 0.22f, 0.03f);
        var darkMat = Game.ToonEmissive(darkBark, 0.18f, 0.03f);
        var warmMat = Game.ToonEmissive(warmBark, 0.24f, 0.03f);
        var leafMat = Game.ToonEmissive(leaf, 0.5f, 0.04f);
        var leafDkMat = Game.ToonEmissive(leafDk, 0.45f, 0.04f);
        var mossMat = Game.ToonEmissive(moss, 0.5f, 0.05f);
        var eyeMat = Game.Emissive(eye, 3.2f);
        var mushMat = Game.ToonEmissive(new Color(0.82f, 0.26f, 0.20f), 0.6f, 0.02f);
        var mushStem = Game.ToonEmissive(new Color(0.86f, 0.80f, 0.70f), 0.3f, 0.02f);

        _body = new Node3D(); AddChild(_body);

        // --- two great root-legs the guardian stands and stomps on ---
        _footL = new Node3D { Position = new Vector3(-0.78f, 0f, 0.05f) }; _body.AddChild(_footL); _footLBase = _footL.Position;
        Add(_footL, new CylinderMesh { TopRadius = 0.42f, BottomRadius = 0.62f, Height = 1.5f }, darkMat, new Vector3(0, 0.72f, 0), new Vector3(7, 0, 4));
        Add(_footL, new SphereMesh { Radius = 0.5f, Height = 0.7f }, darkMat, new Vector3(0.08f, 0.14f, 0.22f));   // gnarled toe-root
        Add(_footL, new CylinderMesh { TopRadius = 0.06f, BottomRadius = 0.14f, Height = 0.6f }, darkMat, new Vector3(-0.3f, 0.1f, 0.15f), new Vector3(60, 0, 20));
        _footR = new Node3D { Position = new Vector3(0.78f, 0f, 0.05f) }; _body.AddChild(_footR); _footRBase = _footR.Position;
        Add(_footR, new CylinderMesh { TopRadius = 0.42f, BottomRadius = 0.62f, Height = 1.5f }, darkMat, new Vector3(0, 0.72f, 0), new Vector3(7, 0, -4));
        Add(_footR, new SphereMesh { Radius = 0.5f, Height = 0.7f }, darkMat, new Vector3(-0.08f, 0.14f, 0.22f));
        Add(_footR, new CylinderMesh { TopRadius = 0.06f, BottomRadius = 0.14f, Height = 0.6f }, darkMat, new Vector3(0.3f, 0.1f, 0.15f), new Vector3(60, 0, -20));

        // splayed buttress roots flaring from the trunk base
        for (int i = 0; i < 6; i++)
        {
            float a = i / 6f * Mathf.Tau;
            var root = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.22f, BottomRadius = 0.75f, Height = 2.4f }, MaterialOverride = darkMat };
            root.Position = new Vector3(Mathf.Cos(a) * 1.2f, 0.8f, Mathf.Sin(a) * 1.2f);
            root.RotationDegrees = new Vector3(Mathf.Cos(a) * 34f, 0, -Mathf.Sin(a) * 34f);
            _body.AddChild(root);
        }
        // gnarled trunk — three leaning, offset segments
        Add(_body, new CylinderMesh { TopRadius = 1.05f, BottomRadius = 1.5f, Height = 2.7f }, barkMat, new Vector3(0f, 2.0f, 0f), new Vector3(4, 0, 3));
        Add(_body, new CylinderMesh { TopRadius = 0.82f, BottomRadius = 1.1f, Height = 2.5f }, barkMat, new Vector3(-0.26f, 4.05f, 0.12f), new Vector3(-3, 20, -6));
        Add(_body, new CylinderMesh { TopRadius = 0.58f, BottomRadius = 0.88f, Height = 2.3f }, barkMat, new Vector3(0.18f, 6.0f, -0.1f), new Vector3(5, -10, 4));
        // burls / knots / grain
        Add(_body, new SphereMesh { Radius = 0.55f, Height = 1.1f }, warmMat, new Vector3(0.55f, 3.1f, 0.35f));
        Add(_body, new SphereMesh { Radius = 0.42f, Height = 0.84f }, barkMat, new Vector3(-0.5f, 4.8f, -0.2f));
        Add(_body, new SphereMesh { Radius = 0.3f, Height = 0.6f }, warmMat, new Vector3(-0.6f, 2.4f, 0.5f));
        Add(_body, new SphereMesh { Radius = 0.26f, Height = 0.52f }, barkMat, new Vector3(0.7f, 5.0f, 0.2f));
        // mushrooms sprouting from the ancient bark
        void Shroom(Vector3 p, float s) { Add(_body, new CylinderMesh { TopRadius = 0.05f * s, BottomRadius = 0.07f * s, Height = 0.22f * s }, mushStem, p); Add(_body, new SphereMesh { Radius = 0.16f * s, Height = 0.16f * s }, mushMat, p + new Vector3(0, 0.14f * s, 0)); }
        Shroom(new Vector3(0.9f, 2.2f, 0.5f), 1.1f);
        Shroom(new Vector3(-0.75f, 3.6f, 0.55f), 0.8f);
        Shroom(new Vector3(0.5f, 1.6f, 0.7f), 0.9f);

        // hollow ancient face: a heavy brow ridge over deep glowing eyes
        M(_body, new SphereMesh { Radius = 0.62f, Height = 0.5f }, barkMat, new Vector3(0f, 5.78f, 0.66f)).Scale = new Vector3(1.5f, 0.5f, 0.7f);   // brow
        _eyeL = new Node3D { Position = new Vector3(-0.34f, 5.42f, 0.74f) }; _body.AddChild(_eyeL);
        M(_eyeL, new SphereMesh { Radius = 0.17f, Height = 0.34f }, eyeMat, Vector3.Zero);
        _eyeR = new Node3D { Position = new Vector3(0.32f, 5.46f, 0.72f) }; _body.AddChild(_eyeR);
        M(_eyeR, new SphereMesh { Radius = 0.17f, Height = 0.34f }, eyeMat, Vector3.Zero);
        // a hanging moss beard beneath the face
        for (int i = 0; i < 5; i++)
        {
            float x = -0.4f + i * 0.2f;
            Add(_body, new CylinderMesh { TopRadius = 0.05f, BottomRadius = 0.02f, Height = 0.5f + (i % 2) * 0.3f }, mossMat, new Vector3(x, 4.7f - (i % 2) * 0.15f, 0.62f), new Vector3(14, 0, 0));
        }

        // asymmetric mossy crown
        Add(_body, new SphereMesh { Radius = 2.6f, Height = 4.0f }, leafMat, new Vector3(0.2f, 7.9f, 0f));
        Add(_body, new SphereMesh { Radius = 1.9f, Height = 3.0f }, leafDkMat, new Vector3(-1.9f, 7.2f, 0.4f));
        Add(_body, new SphereMesh { Radius = 1.6f, Height = 2.6f }, leafMat, new Vector3(1.8f, 6.8f, -0.6f));
        Add(_body, new SphereMesh { Radius = 1.3f, Height = 2.1f }, leafDkMat, new Vector3(0.3f, 9.1f, -0.3f));

        // hanging vines drooping from the crown
        for (int i = 0; i < 4; i++)
        {
            float a = i / 4f * Mathf.Tau + 0.4f;
            Add(_body, new CylinderMesh { TopRadius = 0.03f, BottomRadius = 0.05f, Height = 1.6f + (i % 2) * 0.6f }, mossMat, new Vector3(Mathf.Cos(a) * 2.0f, 6.7f, Mathf.Sin(a) * 2.0f), new Vector3(6, 0, Mathf.Cos(a) * 8f));
        }

        // twisted arms with finger-twigs — pivots at the shoulders so they raise overhead for the wind-up
        _armL = new Node3D { Position = new Vector3(-1.55f, 4.7f, 0) }; _body.AddChild(_armL);
        Add(_armL, new CylinderMesh { TopRadius = 0.28f, BottomRadius = 0.52f, Height = 3.1f }, barkMat, new Vector3(-0.6f, 0.4f, 0), new Vector3(0, 0, 58));
        Add(_armL, new CylinderMesh { TopRadius = 0.12f, BottomRadius = 0.2f, Height = 1.2f }, barkMat, new Vector3(-1.55f, 1.15f, 0.2f), new Vector3(10, 0, 92));
        Add(_armL, new CylinderMesh { TopRadius = 0.1f, BottomRadius = 0.18f, Height = 1.1f }, barkMat, new Vector3(-1.55f, 1.05f, -0.3f), new Vector3(-20, 0, 100));
        Add(_armL, new SphereMesh { Radius = 0.34f, Height = 0.68f }, mossMat, new Vector3(-1.7f, 1.5f, -0.05f));   // mossy knuckle
        _armR = new Node3D { Position = new Vector3(1.55f, 4.7f, 0) }; _body.AddChild(_armR);
        Add(_armR, new CylinderMesh { TopRadius = 0.28f, BottomRadius = 0.52f, Height = 3.1f }, barkMat, new Vector3(0.6f, 0.4f, 0), new Vector3(0, 0, -58));
        Add(_armR, new CylinderMesh { TopRadius = 0.12f, BottomRadius = 0.2f, Height = 1.2f }, barkMat, new Vector3(1.55f, 1.15f, 0.2f), new Vector3(10, 0, -92));
        Add(_armR, new CylinderMesh { TopRadius = 0.1f, BottomRadius = 0.18f, Height = 1.1f }, barkMat, new Vector3(1.55f, 1.05f, -0.3f), new Vector3(-20, 0, -100));
        Add(_armR, new SphereMesh { Radius = 0.34f, Height = 0.68f }, mossMat, new Vector3(1.7f, 1.5f, -0.05f));

        AddChild(new OmniLight3D { Position = new Vector3(0, 5.5f, 0.5f), OmniRange = 10f, LightColor = eye, LightEnergy = 1.2f });
        // (no x-ray silhouette — its green translucent shell washed out the ancient wood-bark body; the guardian is
        //  a towering landmark anyway, so it needs no through-wall aid.)
        ApplySlamPose(-1f);
    }

    public override void _Process(double delta)
    {
        if (Game.I == null || !Game.I.SimActive) return;   // freeze while paused
        float dt = (float)delta;
        _phase += dt;

        if (Ghost)   // network copy: follow synced transform + replay the exact slam phase; no AI/damage
        {
            if (_gInit) { GlobalPosition = GlobalPosition.Lerp(_gpos, Mathf.Min(1f, dt * 10f)); if (_body != null) _body.Rotation = new Vector3(_body.Rotation.X, _gyaw, _body.Rotation.Z); }
            ApplySlamPose(_ghostSlam);
            if (_prevGhostSlam >= 0f && _prevGhostSlam < ImpactK && _ghostSlam >= ImpactK) Stomp(GlobalPosition);   // fire the shockwave as the synced phase crosses impact
            _prevGhostSlam = _ghostSlam;
            _idle += dt; if (_idle > 1.3f) QueueFree();   // owner stopped broadcasting → the guardian is gone
            return;
        }

        if (Caster == null || !GodotObject.IsInstanceValid(Caster)) { QueueFree(); return; }
        // stand on the ground beneath it
        float gy = Game.I.SurfaceHeight(GlobalPosition, GlobalPosition.Y);
        GlobalPosition = new Vector3(GlobalPosition.X, Mathf.MoveToward(GlobalPosition.Y, gy, 9f * dt), GlobalPosition.Z);
        if (!Game.I.WorldRunning) return;

        if (_slamT < 0f)   // resting between stomps
        {
            ApplySlamPose(-1f);
            _rest -= dt;
            if (_rest <= 0f) BeginSlam();
        }
        else               // a stomp is in progress: wind up, land, recover
        {
            _slamT += dt;
            float k = _slamT / SlamDur;
            if (_stepping && k < ImpactK)   // lurch toward the foe during the wind-up
            {
                var step = _stepTarget - GlobalPosition; step.Y = 0f;
                if (step.Length() > 0.1f) GlobalPosition += step.Normalized() * Mathf.Min(6.5f * dt, step.Length());
            }
            ApplySlamPose(k);
            if (!_impactDone && k >= ImpactK) { _impactDone = true; DoImpact(); }
            if (_slamT >= SlamDur)
            {
                _slamT = -1f; _done++; _rest = RestDur;
                if (_done >= Slams) { Wither(); return; }
            }
        }
    }

    // pose the whole body for a stomp. k<0 = idle sway; 0..0.60 = overhead wind-up; 0.60..0.72 = crashing slam;
    // 0.72..1 = recover. Shared by the owner (from _slamT) and ghost copies (from the synced _ghostSlam).
    private void ApplySlamPose(float k)
    {
        float armDeg, leanX, footLift, crouch, eyeFlare;
        if (k < 0f)
        {
            armDeg = Mathf.Sin(_phase * 1.1f) * 4f; leanX = 0f; footLift = 0f; crouch = 0f; eyeFlare = 1f + 0.08f * Mathf.Sin(_phase * 2f);
        }
        else if (k < 0.60f)          // WIND-UP: arms rise overhead, torso leans back, the lead root-foot lifts high
        {
            float w = Ease(k / 0.60f);
            armDeg = Mathf.Lerp(0f, -155f, w);
            leanX = Mathf.Lerp(0f, -0.30f, w);
            footLift = Mathf.Sin(k / 0.60f * Mathf.Pi * 0.5f) * 1.15f;
            crouch = w * 0.22f;
            eyeFlare = 1f + w * 0.7f;   // eyes blaze as it rears up
        }
        else if (k < 0.72f)          // SLAM: arms and foot crash down, torso pitches forward, body drops
        {
            float s = (k - 0.60f) / 0.12f;
            armDeg = Mathf.Lerp(-155f, 72f, s);
            leanX = Mathf.Lerp(-0.30f, 0.34f, s);
            footLift = Mathf.Lerp(1.15f, -0.18f, s);
            crouch = Mathf.Lerp(0.22f, 0.62f, s);
            eyeFlare = Mathf.Lerp(1.7f, 1f, s);
        }
        else                         // RECOVER: ease back to neutral
        {
            float r = (k - 0.72f) / 0.28f;
            armDeg = Mathf.Lerp(72f, 0f, r);
            leanX = Mathf.Lerp(0.34f, 0f, r);
            footLift = Mathf.Lerp(-0.18f, 0f, r);
            crouch = Mathf.Lerp(0.62f, 0f, r);
            eyeFlare = 1f;
        }
        if (_armL != null) _armL.RotationDegrees = new Vector3(armDeg, 0, 0);
        if (_armR != null) _armR.RotationDegrees = new Vector3(armDeg, 0, 0);
        if (_body != null) { _body.Rotation = new Vector3(leanX, _body.Rotation.Y, Mathf.Sin(_phase * 1.3f) * 0.03f); _body.Position = new Vector3(0, -crouch, 0); }
        if (_footR != null) _footR.Position = new Vector3(_footRBase.X, _footRBase.Y + footLift, _footRBase.Z);
        if (_footL != null) _footL.Position = new Vector3(_footLBase.X, _footLBase.Y + Mathf.Max(0f, footLift - 0.9f) * 0.4f, _footLBase.Z);   // trailing foot rocks slightly
        if (_eyeL != null) _eyeL.Scale = new Vector3(eyeFlare, eyeFlare, eyeFlare);
        if (_eyeR != null) _eyeR.Scale = new Vector3(eyeFlare, eyeFlare, eyeFlare);
    }
    private static float Ease(float t) => t * t * (3f - 2f * t);

    // choose a foe, face it, and set a lurch target for the wind-up
    private void BeginSlam()
    {
        _slamT = 0f; _impactDone = false; _stepping = false;
        Enemy near = null; float bd = 1e9f;
        foreach (var e in Game.I.Enemies.ToArray())
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
            float d = GlobalPosition.DistanceTo(e.GlobalPosition);
            if (d < bd) { bd = d; near = e; }
        }
        if (near != null)
        {
            var step = near.GlobalPosition - GlobalPosition; step.Y = 0f;
            if (step.LengthSquared() > 0.01f && _body != null) _body.Rotation = new Vector3(_body.Rotation.X, Mathf.Atan2(step.X, step.Z), _body.Rotation.Z);
            if (step.Length() > 3f) { _stepTarget = GlobalPosition + step.Normalized() * Mathf.Min(5f, step.Length() - 2.5f); _stepping = true; }
        }
    }

    // the down-beat: radial damage + a knock-up on light foes + shockwave + camera kick
    private void DoImpact()
    {
        Stomp(GlobalPosition);
        foreach (var e in Game.I.Enemies.ToArray())
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
            float d = new Vector2(e.GlobalPosition.X - GlobalPosition.X, e.GlobalPosition.Z - GlobalPosition.Z).Length();
            if (d < SlamRadius + e.Radius)
            {
                float fall = Mathf.Lerp(1f, 0.4f, Mathf.Clamp(d / SlamRadius, 0f, 1f));   // brutal core, medium rim
                e.Hurt(SlamDamage * fall, DamageType.Nature, true); e.HitFrom(GlobalPosition);
                if (Poison > 0f) e.Poison(Poison, 3f);
                if (RootOnSlam) e.Root(1.0f);
            }
        }
        // fling nearby foes up + out — routed through StormForce so it's host-authoritative (a client's guardian
        // asks the host) and mass-scaled: light foes go flying, brutes barely rock, bosses shrug. Syncs to all.
        Game.I.NetMgr?.StormForce(GlobalPosition, SlamRadius, 3, 15f);
        Game.I.DamageWorld(GlobalPosition, SlamRadius, SlamDamage);   // the slam breaks props too
        Caster?.CamKickExternal(0.9f);
        Game.I.Sfx?.Impact(DamageType.Nature);
    }

    // ground shockwave + dust + flung debris (runs on owner + ghosts)
    private void Stomp(Vector3 at)
    {
        var col = new Color(0.5f, 0.85f, 0.42f);
        var ground = new Vector3(at.X, 0.05f, at.Z);
        Game.I.VfxRing(ground, col, SlamRadius * 2f, 0.5f);
        Game.I.VfxRing(ground, new Color(0.55f, 0.44f, 0.28f), SlamRadius * 1.15f, 0.34f);   // brown dust ring

        int chunks = Mathf.Max(5, (int)(14 * Game.I.ParticleScale));
        var dirt = Game.ToonEmissive(new Color(0.30f, 0.22f, 0.13f), 0.08f, 0.02f);
        for (int i = 0; i < chunks; i++)
        {
            float a = i / (float)chunks * Mathf.Tau + GD.Randf() * 0.4f;
            var outw = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
            var chunk = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.25f + GD.Randf() * 0.4f, 0.25f + GD.Randf() * 0.35f, 0.25f + GD.Randf() * 0.4f) }, MaterialOverride = dirt };
            Game.I.AddChild(chunk);
            var start = ground + outw * (1.5f + GD.Randf() * 2.5f) + Vector3.Up * 0.2f;
            chunk.GlobalPosition = start;
            chunk.Rotation = new Vector3(GD.Randf() * 6.28f, GD.Randf() * 6.28f, GD.Randf() * 6.28f);
            var end = start + outw * (1.5f + GD.Randf() * 2f) + Vector3.Up * (2f + GD.Randf() * 2.5f);   // pop up + out...
            var tw = chunk.CreateTween(); tw.SetParallel(true);
            tw.TweenProperty(chunk, "global_position", end, 0.28f).SetEase(Tween.EaseType.Out);
            tw.TweenProperty(chunk, "rotation", chunk.Rotation + new Vector3(GD.Randf() * 8f, GD.Randf() * 8f, GD.Randf() * 8f), 0.85f);
            tw.SetParallel(false);
            tw.TweenProperty(chunk, "global_position", new Vector3(end.X + outw.X, 0.05f, end.Z + outw.Z), 0.55f).SetEase(Tween.EaseType.In);   // ...then fall
            tw.TweenProperty(chunk, "scale", new Vector3(0.01f, 0.01f, 0.01f), 0.2f);
            tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(chunk)) chunk.QueueFree(); }));
        }
        // a rising dust column
        var v = new Vfx(); Game.I.AddChild(v); v.GlobalPosition = new Vector3(ground.X, 0.6f, ground.Z);
        v.Init(new SphereMesh { Radius = SlamRadius * 0.35f, Height = SlamRadius * 0.7f }, new Color(0.5f, 0.42f, 0.28f), 0.4f, 3f);
    }

    private void Wither()
    {
        if (Caster != null && GodotObject.IsInstanceValid(Caster) && Caster.ActiveGuardian == this) Caster.ActiveGuardian = null;
        var tw = _body.CreateTween();
        tw.TweenProperty(_body, "scale", new Vector3(0.05f, 0.05f, 0.05f), 0.5f).SetEase(Tween.EaseType.In);
        tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(this)) QueueFree(); }));
    }
}
