using Godot;
using System.Collections.Generic;

// Creature.cs — procedural enemy bodies. CreatureKind (below) is the silhouette family; Enemy._Ready
// maps each enemy type string to a kind (e.g. brute/boss -> Orc, flyer/diver -> Mosquito). Add a new
// kind here only if a new enemy needs a distinct body; otherwise reuse an existing one. Handles the
// mesh build + walk/attack animation for the enemy.
public enum CreatureKind { Goblin, Orc, Spider, Mosquito, Bomber, Zapper, Zombie, HollowBoss, Crocodile, Troll, Pigmy, Pterodactyl, Bat, Snake }   // Crocodile + jungle set (NEW)

// Procedurally-built, procedurally-animated enemy models (primitive-based, but layered limbs with a
// walk/flap cycle and knee-bend so they read as creatures). Each instance is randomly varied so no
// two goblins/orcs look identical.
public partial class Creature : Node3D
{
    private CreatureKind _kind;
    private float _scale = 1f;
    private float _phase;
    private float _wing;
    private Node3D _body;
    private float _bodyBaseY;
    private readonly List<Node3D> _hips = new();
    private Creature _gobZombie, _gobNormal;   // (NEW) HollowBoss shoulder riders (real Zombie + Goblin creatures)
    private float _gobFireZ = 0f, _gobFireN = 0f;   // brief cast-lunge timers when each goblin fires
    public float StompWind = 0f;               // (NEW) 0..1 leg-lift during the stomp wind-up
    public void FireShoulder(bool zombie) { if (zombie) { _gobFireZ = 1f; _gobZombie?.Strike(); } else { _gobFireN = 1f; _gobNormal?.Strike(); } }
    public Vector3 ShoulderPos(bool zombie) { var g = zombie ? _gobZombie : _gobNormal; return (g != null && GodotObject.IsInstanceValid(g)) ? g.GlobalPosition : GlobalPosition; }
    public void PopGoblins() { if (_gobZombie != null && GodotObject.IsInstanceValid(_gobZombie)) _gobZombie.QueueFree(); if (_gobNormal != null && GodotObject.IsInstanceValid(_gobNormal)) _gobNormal.QueueFree(); }
    public void TopplePose(float pitch) { if (_body != null) _body.RotationDegrees = new Vector3(pitch, _body.RotationDegrees.Y, _body.RotationDegrees.Z); }
    private readonly List<Node3D> _knees = new();
    private readonly List<Vector3> _hipBase = new();
    private readonly List<Vector3> _kneeBase = new();
    private readonly List<Node3D> _arms = new();
    private readonly List<Node3D> _wings = new();
    private Node3D _keg;   // bomber
    private MeshInstance3D _orb;   // zapper focus
    private float _cast, _castTarget;
    private float _swing, _swingTarget, _strike;   // melee wind-up amount (0..1) + brief forward strike impulse
    private float _scream;                          // (NEW) zombie shriek-to-sky overlay (arms up, lean back)
    public void Scream() { _scream = 1f; }
    public int IdlePose = 0;                        // (NEW) 0 stand, 1 lie on floor, 2 slump, 3 snicker (idle swarmers)
    public bool AnimSuspended = false;              // (PERF) set by Enemy when far + off-camera → skip the skeletal pose writes entirely

    private void ZombieIdlePose()   // lie / slump / snicker (idle, non-alerted swarmers)
    {
        if (_body == null) return;
        switch (IdlePose)
        {
            case 1:   // sprawled on the floor
                _body.RotationDegrees = new Vector3(-82f, 90f, Mathf.Sin(_phase * 0.5f) * 2f);
                _body.Position = new Vector3(0, _bodyBaseY * 0.22f, _bodyBaseY * 0.45f);
                for (int i = 0; i < _hips.Count; i++) { _hips[i].RotationDegrees = new Vector3(-78f, 0, 0); if (_knees[i] != null) _knees[i].RotationDegrees = new Vector3(22f, 0, 0); }
                for (int i = 0; i < _arms.Count; i++) _arms[i].RotationDegrees = new Vector3(8f, 0, (i == 0 ? 62f : -62f));
                break;
            case 2:   // slumped against a wall, head hung
                _body.RotationDegrees = new Vector3(-36f, 0, Mathf.Sin(_phase * 0.4f) * 2f);
                _body.Position = new Vector3(0, _bodyBaseY * 0.55f, 0);
                for (int i = 0; i < _hips.Count; i++) { _hips[i].RotationDegrees = new Vector3(-68f, 0, (i == 0 ? 12f : -12f)); if (_knees[i] != null) _knees[i].RotationDegrees = new Vector3(82f, 0, 0); }
                for (int i = 0; i < _arms.Count; i++) _arms[i].RotationDegrees = new Vector3(14f + Mathf.Sin(_phase * 0.5f + i) * 3f, 0, (i == 0 ? 8f : -8f));
                break;
            default:  // 3: standing snicker — shoulders shuddering, head tilted
                float sh = Mathf.Sin(_phase * 14f) * 3f;
                _body.RotationDegrees = new Vector3(22f, 8f, sh);
                _body.Position = new Vector3(0, _bodyBaseY + Mathf.Abs(Mathf.Sin(_phase * 7f)) * _scale * 0.02f, 0);
                for (int i = 0; i < _hips.Count; i++) { _hips[i].RotationDegrees = Vector3.Zero; if (_knees[i] != null) _knees[i].RotationDegrees = new Vector3(4f, 0, 0); }
                for (int i = 0; i < _arms.Count; i++) _arms[i].RotationDegrees = new Vector3(-18f + sh, 0, (i == 0 ? 12f : -12f));
                break;
        }
    }
    public void SetCast(float t) { _castTarget = Mathf.Clamp(t, 0f, 1f); }
    public void SetSwing(float t) { _swingTarget = Mathf.Clamp(t, 0f, 1f); }
    public void Strike() { _strike = 1f; }

    private static Mesh Cone(float r, float h) => new CylinderMesh { TopRadius = 0.001f, BottomRadius = r, Height = h };
    private static Mesh Cyl(float r, float h) => new CylinderMesh { TopRadius = r, BottomRadius = r, Height = h };
    private static Mesh Box(float x, float y, float z) => new BoxMesh { Size = new Vector3(x, y, z) };
    private static Mesh Sph(float r) => new SphereMesh { Radius = r, Height = r * 2f };
    private static float R(float a, float b) => (float)GD.RandRange(a, b);

    private readonly List<MeshInstance3D> _detail = new();   // (PERF) the small trim parts — hidden as a group on far enemies (LOD)
    private readonly List<MeshInstance3D> _shadowParts = new();   // (PERF) the big shadow-casting parts (torso/head/limbs) — stop casting on far enemies
    private Material _limbMat;   // (FIX) limb material — limb parts are the silhouette (legs/arms) and must NEVER be culled as "trim"
    private bool _lodFar = false;
    private MeshInstance3D Part(Node3D parent, Mesh m, Material mat, Vector3 pos, Vector3 rotDeg, Vector3 scl)
    {
        var mi = new MeshInstance3D { Mesh = m, MaterialOverride = mat };
        // (PERF) small detail parts (teeth, warts, eyes, fingers…) don't cast shadows — the torso/head/limbs still do,
        // so the silhouette reads the same, but we stop redrawing ~15 tiny meshes per enemy into all 4 shadow cascades.
        var s = m.GetAabb().Size;
        bool small = Mathf.Max(s.X * Mathf.Abs(scl.X), Mathf.Max(s.Y * Mathf.Abs(scl.Y), s.Z * Mathf.Abs(scl.Z))) < 0.62f;
        if (small && mat != _limbMat)   // (FIX) never treat LIMBS as cullable trim — small creatures' legs/arms were vanishing at range
        {
            mi.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            _detail.Add(mi);   // …and drop these tiny trim parts once the foe is far enough that they're sub-pixel (LOD)
        }
        else _shadowParts.Add(mi);   // (PERF) limbs + big parts: always draw; far-LOD only stops their shadow-casting
        parent.AddChild(mi);
        mi.Position = pos; mi.RotationDegrees = rotDeg; mi.Scale = scl;
        return mi;
    }
    // (PERF) LOD: on distant enemies hide the dozens of tiny trim meshes (teeth/warts/eyes/fingers/spikes). The torso,
    // head and limbs keep drawing so the silhouette is unchanged; we just shed ~half the draw calls per far-off foe.
    public void SetLodFar(bool far)
    {
        if (far == _lodFar) return;
        _lodFar = far;
        for (int i = 0; i < _detail.Count; i++)
            if (_detail[i] != null && GodotObject.IsInstanceValid(_detail[i])) _detail[i].Visible = !far;
        var sh = far ? GeometryInstance3D.ShadowCastingSetting.Off : GeometryInstance3D.ShadowCastingSetting.On;   // (PERF) far foes stop feeding the shadow cascades
        for (int i = 0; i < _shadowParts.Count; i++)
            if (_shadowParts[i] != null && GodotObject.IsInstanceValid(_shadowParts[i])) _shadowParts[i].CastShadow = sh;
    }
    private Node3D Pivot(Node3D parent, Vector3 pos, Vector3 rotDeg = default)
    {
        var n = new Node3D();
        parent.AddChild(n);
        n.Position = pos; n.RotationDegrees = rotDeg;
        return n;
    }

    public void Build(CreatureKind kind, float radius, Material body, Material limb, Material accent)
    {
        _limbMat = limb;   // (FIX) so Part() can exempt limbs from the size-based trim cull
        _kind = kind; _scale = radius;
        _phase = R(0f, 6.28f);
        switch (kind)
        {
            case CreatureKind.Goblin: Goblin(radius, body, limb, accent, false); break;
            case CreatureKind.Bomber: Goblin(radius, body, limb, accent, true); break;
            case CreatureKind.Zombie: Goblin(radius, body, limb, accent, false); break;   // humanoid skeleton, zombie shamble in Animate
            case CreatureKind.Orc: Orc(radius, body, limb, accent); break;
            case CreatureKind.HollowBoss: HollowBoss(radius, body, limb, accent); break;
            case CreatureKind.Spider: Spider(radius, body, limb, accent); break;
            case CreatureKind.Mosquito: Mosquito(radius, body, limb, accent); break;
            case CreatureKind.Zapper: Zapper(radius, body, limb, accent); break;
            case CreatureKind.Crocodile: Crocodile(radius, body, limb, accent); break;
            case CreatureKind.Troll: Troll(radius, body, limb, accent); break;
            case CreatureKind.Pigmy: Pigmy(radius, body, limb, accent); break;
            case CreatureKind.Pterodactyl: Pterodactyl(radius, body, limb, accent); break;
            case CreatureKind.Bat: Bat(radius, body, limb, accent); break;
            case CreatureKind.Snake: Snake(radius, body, limb, accent); break;
        }
    }

    // ---- jungle troll: hulking, hunched, HUGE dragging arms, underbite tusks, warty; a rushing bruiser ----
    private void Troll(float s, Material body, Material limb, Material accent)
    {
        float v = R(0.95f, 1.2f);
        float gh = s * 1.5f * v;
        _body = Pivot(this, new Vector3(0, gh, 0));
        _bodyBaseY = gh;
        float bw = s * 2.0f * v;
        Part(_body, Box(bw, gh, bw * 0.9f), body, Vector3.Zero, new Vector3(R(10, 18), 0, 0), Vector3.One);                       // massive hunched torso
        Part(_body, Sph(s * 0.9f), body, new Vector3(0, gh * 0.35f, -bw * 0.35f), Vector3.Zero, new Vector3(1.2f, 1f, 1f));       // hunchback
        Part(_body, Box(bw * 1.2f, gh * 0.4f, bw * 0.6f), limb, new Vector3(0, gh * 0.42f, 0), Vector3.Zero, Vector3.One);        // shoulders
        for (int i = 0; i < 5; i++) Part(_body, Sph(s * R(0.14f, 0.26f)), limb, new Vector3(R(-0.5f, 0.5f) * bw, R(-0.2f, 0.4f) * gh, R(-0.5f, -0.2f) * bw), Vector3.Zero, Vector3.One);   // warty lumps
        var head = Part(_body, Sph(s * 0.55f), body, new Vector3(0, gh * 0.5f, s * 0.25f), Vector3.Zero, new Vector3(1.1f, 0.9f, 1f));
        Part(head, Box(s * 0.8f, s * 0.2f, s * 0.3f), limb, new Vector3(0, s * 0.2f, s * 0.3f), new Vector3(-12, 0, 0), Vector3.One);   // heavy brow
        Part(head, Sph(s * 0.08f), accent, new Vector3(s * 0.18f, s * 0.05f, s * 0.42f), Vector3.Zero, Vector3.One);
        Part(head, Sph(s * 0.08f), accent, new Vector3(-s * 0.18f, s * 0.05f, s * 0.42f), Vector3.Zero, Vector3.One);
        Part(head, Box(s * 0.7f, s * 0.28f, s * 0.32f), body, new Vector3(0, -s * 0.28f, s * 0.34f), new Vector3(8, 0, 0), Vector3.One);   // jutting jaw
        Part(head, Cone(s * 0.11f, s * R(0.4f, 0.55f)), accent, new Vector3(s * 0.24f, -s * 0.2f, s * 0.42f), new Vector3(-20, 0, 0), Vector3.One);   // underbite tusks
        Part(head, Cone(s * 0.11f, s * R(0.4f, 0.55f)), accent, new Vector3(-s * 0.24f, -s * 0.2f, s * 0.42f), new Vector3(-20, 0, 0), Vector3.One);
        float legLen = gh * 0.4f, armLen = gh * 0.8f;   // stubby legs, huge dragging arms
        for (int i = 0; i < 2; i++)
        {
            float sx = i == 0 ? 1 : -1;
            var hip = Pivot(this, new Vector3(sx * bw * 0.32f, gh * 0.32f, 0));
            Part(hip, Cyl(s * 0.4f, legLen), limb, new Vector3(0, -legLen / 2f, 0), Vector3.Zero, Vector3.One);
            var knee = Pivot(hip, new Vector3(0, -legLen, 0));
            Part(knee, Cyl(s * 0.36f, legLen), limb, new Vector3(0, -legLen / 2f, 0), Vector3.Zero, Vector3.One);
            Part(knee, Box(s * 0.6f, s * 0.2f, s * 0.85f), limb, new Vector3(0, -legLen, s * 0.14f), Vector3.Zero, Vector3.One);
            _hips.Add(hip); _knees.Add(knee); _hipBase.Add(Vector3.Zero); _kneeBase.Add(Vector3.Zero);
            var arm = Pivot(_body, new Vector3(sx * bw * 0.6f, gh * 0.42f, 0));
            Part(arm, Cyl(s * 0.36f, armLen), limb, new Vector3(0, -armLen / 2f, 0), Vector3.Zero, Vector3.One);
            Part(arm, Sph(s * 0.5f), limb, new Vector3(0, -armLen, 0), Vector3.Zero, Vector3.One);   // huge fists
            _arms.Add(arm);
        }
    }

    // ---- pigmy: little tribal humanoid, big head with warpaint + a feather topknot, carries a spear/blowpipe ----
    private void Pigmy(float s, Material body, Material limb, Material accent)
    {
        float v = R(0.85f, 1.1f);
        float gh = s * 0.85f * v;
        _body = Pivot(this, new Vector3(0, gh, 0));
        _bodyBaseY = gh;
        float bw = s * 0.5f * v;
        Part(_body, Box(bw, gh * 0.8f, bw * 0.7f), body, Vector3.Zero, new Vector3(R(4, 10), 0, 0), Vector3.One);   // little torso
        var head = Part(_body, Sph(s * 0.4f), body, new Vector3(0, gh * 0.55f, s * 0.05f), Vector3.Zero, Vector3.One);
        Part(head, Box(s * 0.5f, s * 0.1f, s * 0.1f), accent, new Vector3(0, s * 0.02f, s * 0.3f), Vector3.Zero, Vector3.One);   // warpaint stripe
        Part(head, Sph(s * 0.06f), accent, new Vector3(s * 0.12f, s * 0.08f, s * 0.32f), Vector3.Zero, Vector3.One);
        Part(head, Sph(s * 0.06f), accent, new Vector3(-s * 0.12f, s * 0.08f, s * 0.32f), Vector3.Zero, Vector3.One);
        Part(head, Cone(s * 0.06f, s * 0.5f), accent, new Vector3(0, s * 0.4f, -s * 0.1f), new Vector3(-20, 0, 0), Vector3.One);   // feather topknot
        float legLen = gh * 0.4f, armLen = gh * 0.45f;
        for (int i = 0; i < 2; i++)
        {
            float sx = i == 0 ? 1 : -1;
            var hip = Pivot(this, new Vector3(sx * bw * 0.32f, gh * 0.3f, 0));
            Part(hip, Cyl(s * 0.12f, legLen), limb, new Vector3(0, -legLen / 2f, 0), Vector3.Zero, Vector3.One);
            var knee = Pivot(hip, new Vector3(0, -legLen, 0));
            Part(knee, Cyl(s * 0.1f, legLen), limb, new Vector3(0, -legLen / 2f, 0), Vector3.Zero, Vector3.One);
            Part(knee, Box(s * 0.24f, s * 0.1f, s * 0.34f), limb, new Vector3(0, -legLen, s * 0.1f), Vector3.Zero, Vector3.One);
            _hips.Add(hip); _knees.Add(knee); _hipBase.Add(Vector3.Zero); _kneeBase.Add(Vector3.Zero);
            var arm = Pivot(_body, new Vector3(sx * bw * 0.55f, gh * 0.32f, 0));
            Part(arm, Cyl(s * 0.09f, armLen), limb, new Vector3(0, -armLen / 2f, 0), Vector3.Zero, Vector3.One);
            Part(arm, Sph(s * 0.12f), limb, new Vector3(0, -armLen, 0), Vector3.Zero, Vector3.One);
            _arms.Add(arm);
        }
        // spear/blowpipe in the right hand — parented to the arm so it thrusts on the jab
        var weapon = Part(_arms[0], Cyl(s * 0.05f, s * 1.9f), accent, new Vector3(0, -armLen, s * 0.35f), new Vector3(82, 0, 0), Vector3.One);
        Part(weapon, Cone(s * 0.08f, s * 0.3f), limb, new Vector3(0, s * 0.95f, 0), Vector3.Zero, Vector3.One);   // tip
    }

    // ---- pterodactyl: leathery flyer, long beaked head + back crest, big membrane wings, tail ----
    private void Pterodactyl(float s, Material body, Material limb, Material accent)
    {
        float v = R(0.9f, 1.15f); s *= v;
        _body = Pivot(this, Vector3.Zero);
        _bodyBaseY = 0f;
        Part(_body, Sph(s * 0.5f), body, Vector3.Zero, Vector3.Zero, new Vector3(1, 0.9f, 1.4f));   // body
        Part(_body, Cyl(s * 0.14f, s * 0.8f), body, new Vector3(0, s * 0.25f, s * 0.4f), new Vector3(50, 0, 0), Vector3.One);   // neck
        var head = Part(_body, Sph(s * 0.24f), body, new Vector3(0, s * 0.5f, s * 0.75f), Vector3.Zero, Vector3.One);
        Part(head, Cone(s * 0.1f, s * 0.9f), limb, new Vector3(0, -s * 0.02f, s * 0.5f), new Vector3(90, 0, 0), Vector3.One);   // long beak
        Part(head, Cone(s * 0.12f, s * 0.5f), accent, new Vector3(0, s * 0.1f, -s * 0.2f), new Vector3(-60, 0, 0), Vector3.One);   // head crest
        Part(head, Sph(s * 0.06f), accent, new Vector3(s * 0.12f, s * 0.05f, s * 0.1f), Vector3.Zero, Vector3.One);
        Part(head, Sph(s * 0.06f), accent, new Vector3(-s * 0.12f, s * 0.05f, s * 0.1f), Vector3.Zero, Vector3.One);
        Part(_body, Cone(s * 0.1f, s * 1.0f), body, new Vector3(0, 0, -s * 0.85f), new Vector3(-90, 0, 0), Vector3.One);   // tail
        for (int i = 0; i < 2; i++)
        {
            float sx = i == 0 ? 1 : -1;
            var w = Pivot(_body, new Vector3(sx * s * 0.35f, s * 0.15f, 0));
            Part(w, Box(s * 2.2f, s * 0.05f, s * 1.0f), limb, new Vector3(sx * s * 1.1f, 0, 0), Vector3.Zero, Vector3.One);   // membrane
            Part(w, Cyl(s * 0.06f, s * 2.0f), body, new Vector3(sx * s * 1.0f, 0, s * 0.3f), new Vector3(0, 0, sx * 90f), Vector3.One);   // leading-edge bone
            _wings.Add(w);
        }
        for (int i = 0; i < 2; i++) { float sx = i == 0 ? 1 : -1; Part(_body, Cyl(s * 0.05f, s * 0.4f), limb, new Vector3(sx * s * 0.2f, -s * 0.4f, -s * 0.2f), Vector3.Zero, Vector3.One); }
    }

    // ---- bat: round furry body, big ears + fangs, membrane wings ----
    private void Bat(float s, Material body, Material limb, Material accent)
    {
        float v = R(0.85f, 1.15f); s *= v;
        _body = Pivot(this, Vector3.Zero);
        _bodyBaseY = 0f;
        Part(_body, Sph(s * 0.45f), body, Vector3.Zero, Vector3.Zero, new Vector3(1, 1.1f, 1));   // furry body
        var head = Part(_body, Sph(s * 0.3f), body, new Vector3(0, s * 0.35f, s * 0.1f), Vector3.Zero, Vector3.One);
        Part(head, Cone(s * 0.12f, s * 0.5f), body, new Vector3(s * 0.15f, s * 0.3f, 0), new Vector3(-10, 0, 10), Vector3.One);   // ears
        Part(head, Cone(s * 0.12f, s * 0.5f), body, new Vector3(-s * 0.15f, s * 0.3f, 0), new Vector3(-10, 0, -10), Vector3.One);
        Part(head, Sph(s * 0.07f), accent, new Vector3(s * 0.12f, s * 0.02f, s * 0.24f), Vector3.Zero, Vector3.One);
        Part(head, Sph(s * 0.07f), accent, new Vector3(-s * 0.12f, s * 0.02f, s * 0.24f), Vector3.Zero, Vector3.One);
        Part(head, Cone(s * 0.03f, s * 0.1f), accent, new Vector3(s * 0.06f, -s * 0.16f, s * 0.22f), new Vector3(180, 0, 0), Vector3.One);   // fangs
        Part(head, Cone(s * 0.03f, s * 0.1f), accent, new Vector3(-s * 0.06f, -s * 0.16f, s * 0.22f), new Vector3(180, 0, 0), Vector3.One);
        for (int i = 0; i < 2; i++)
        {
            float sx = i == 0 ? 1 : -1;
            var w = Pivot(_body, new Vector3(sx * s * 0.3f, s * 0.1f, 0));
            Part(w, Box(s * 1.6f, s * 0.04f, s * 0.9f), limb, new Vector3(sx * s * 0.8f, 0, -s * 0.1f), Vector3.Zero, Vector3.One);
            _wings.Add(w);
        }
        for (int i = 0; i < 2; i++) { float sx = i == 0 ? 1 : -1; Part(_body, Cyl(s * 0.04f, s * 0.25f), limb, new Vector3(sx * s * 0.12f, -s * 0.4f, 0), Vector3.Zero, Vector3.One); }
    }

    // ---- snake: a chain of tapering segments low to the ground; forked tongue; SLITHERS via a travelling yaw wave ----
    private void Snake(float s, Material body, Material limb, Material accent)
    {
        float v = R(0.9f, 1.2f); s *= v;
        _bodyBaseY = s * 0.3f;
        Node3D prev = this;
        int segs = 7; float segLen = s * 0.55f, r = s * 0.32f;
        for (int i = 0; i < segs; i++)
        {
            var seg = Pivot(prev, i == 0 ? new Vector3(0, s * 0.3f, s * 0.2f) : new Vector3(0, 0, -segLen));
            float rr = r * (1f - i * 0.09f);
            // a smooth capsule per segment (overlapping caps fill the joints) → a continuous body, not caterpillar beads
            Part(seg, new CapsuleMesh { Radius = rr, Height = segLen + rr * 2.1f, RadialSegments = 12, Rings = 5 }, body, new Vector3(0, 0, -segLen * 0.5f), new Vector3(90, 0, 0), Vector3.One);
            _hips.Add(seg); _knees.Add(null); _hipBase.Add(Vector3.Zero); _kneeBase.Add(Vector3.Zero);
            if (i == 0) _body = seg;
            prev = seg;
        }
        var head = _body;
        Part(head, Sph(s * 0.36f), body, new Vector3(0, 0, s * 0.15f), Vector3.Zero, new Vector3(1.2f, 0.85f, 1.2f));
        Part(head, Sph(s * 0.07f), accent, new Vector3(s * 0.14f, s * 0.12f, s * 0.28f), Vector3.Zero, Vector3.One);
        Part(head, Sph(s * 0.07f), accent, new Vector3(-s * 0.14f, s * 0.12f, s * 0.28f), Vector3.Zero, Vector3.One);
        Part(head, Box(s * 0.03f, s * 0.03f, s * 0.4f), accent, new Vector3(0, -s * 0.02f, s * 0.5f), Vector3.Zero, Vector3.One);   // forked tongue
        Part(head, Box(s * 0.03f, s * 0.03f, s * 0.15f), accent, new Vector3(s * 0.05f, -s * 0.02f, s * 0.68f), new Vector3(0, 20, 0), Vector3.One);
        Part(head, Box(s * 0.03f, s * 0.03f, s * 0.15f), accent, new Vector3(-s * 0.05f, -s * 0.02f, s * 0.68f), new Vector3(0, -20, 0), Vector3.One);
    }

    // ---- crocodile humanoid: bipedal, LONG SNOUT with teeth, back scutes, thick tapering tail ----
    private void Crocodile(float s, Material body, Material limb, Material accent)
    {
        float v = R(0.9f, 1.12f);
        float gh = s * 1.15f * v;
        _body = Pivot(this, new Vector3(0, gh, 0));
        _bodyBaseY = gh;
        float bw = s * 1.1f * v;
        Part(_body, Box(bw, gh, bw * 0.8f), body, Vector3.Zero, new Vector3(R(4, 10), 0, 0), Vector3.One);                     // torso
        Part(_body, Box(bw * 1.15f, gh * 0.35f, bw * 0.55f), limb, new Vector3(0, gh * 0.42f, -s * 0.05f), Vector3.Zero, Vector3.One);   // shoulders
        Part(_body, Box(bw * 0.7f, gh * 0.5f, bw * 0.45f), accent, new Vector3(0, gh * 0.05f, s * 0.42f), Vector3.Zero, Vector3.One);    // pale belly plate
        for (int sp = 0; sp < 5; sp++)   // ridged back scutes
            Part(_body, Cone(s * 0.14f, s * 0.28f), accent, new Vector3(0, gh * (0.0f + sp * 0.14f), -bw * 0.42f), new Vector3(-110, 0, 0), Vector3.One);

        // flat wide head + a LONG SNOUT jutting forward, upper + lower jaw with teeth, bulging eyes on top
        var head = Part(_body, Box(s * 0.5f, s * 0.32f, s * 0.5f), body, new Vector3(0, gh * 0.5f, s * 0.2f), Vector3.Zero, Vector3.One);
        Part(head, Box(s * 0.4f, s * 0.2f, s * 1.15f), body, new Vector3(0, s * 0.06f, s * 0.72f), Vector3.Zero, Vector3.One);          // upper snout
        Part(head, Box(s * 0.36f, s * 0.14f, s * 1.05f), limb, new Vector3(0, -s * 0.14f, s * 0.68f), Vector3.Zero, Vector3.One);       // lower jaw
        Part(head, Sph(s * 0.13f), accent, new Vector3(s * 0.18f, s * 0.24f, s * 0.1f), Vector3.Zero, Vector3.One);                     // eye
        Part(head, Sph(s * 0.13f), accent, new Vector3(-s * 0.18f, s * 0.24f, s * 0.1f), Vector3.Zero, Vector3.One);
        Part(head, Sph(s * 0.06f), limb, new Vector3(s * 0.18f, s * 0.28f, s * 0.12f), Vector3.Zero, Vector3.One);                      // pupils
        Part(head, Sph(s * 0.06f), limb, new Vector3(-s * 0.18f, s * 0.28f, s * 0.12f), Vector3.Zero, Vector3.One);
        for (int t = 0; t < 5; t++)   // teeth along the snout
        {
            float tz = s * (0.3f + t * 0.17f);
            Part(head, Cone(s * 0.05f, s * 0.15f), accent, new Vector3(s * 0.17f, -s * 0.02f, tz), new Vector3(180, 0, 0), Vector3.One);
            Part(head, Cone(s * 0.05f, s * 0.15f), accent, new Vector3(-s * 0.17f, -s * 0.02f, tz), new Vector3(180, 0, 0), Vector3.One);
        }
        Part(head, Sph(s * 0.06f), limb, new Vector3(s * 0.09f, s * 0.14f, s * 1.24f), Vector3.Zero, Vector3.One);                      // nostrils
        Part(head, Sph(s * 0.06f), limb, new Vector3(-s * 0.09f, s * 0.14f, s * 1.24f), Vector3.Zero, Vector3.One);

        // thick tapering tail sweeping back and down
        var tail = Pivot(_body, new Vector3(0, -gh * 0.15f, -bw * 0.5f));
        float seg = s * 0.42f;
        for (int t = 0; t < 4; t++)
            Part(tail, Box(s * (0.42f - t * 0.08f), s * (0.36f - t * 0.07f), seg), body, new Vector3(0, -t * s * 0.1f, -seg * 0.5f - t * seg * 0.55f), new Vector3(t * 7f, 0, 0), Vector3.One);

        // bipedal legs + short clawed arms
        float legLen = gh * 0.4f, armLen = gh * 0.45f;
        for (int i = 0; i < 2; i++)
        {
            float sx = i == 0 ? 1 : -1;
            var hip = Pivot(this, new Vector3(sx * bw * 0.3f, gh * 0.3f, 0));
            Part(hip, Cyl(s * 0.24f, legLen), limb, new Vector3(0, -legLen / 2f, 0), Vector3.Zero, Vector3.One);
            var knee = Pivot(hip, new Vector3(0, -legLen, 0));
            Part(knee, Cyl(s * 0.2f, legLen), limb, new Vector3(0, -legLen / 2f, 0), Vector3.Zero, Vector3.One);
            Part(knee, Box(s * 0.42f, s * 0.16f, s * 0.65f), limb, new Vector3(0, -legLen, s * 0.16f), Vector3.Zero, Vector3.One);
            _hips.Add(hip); _knees.Add(knee); _hipBase.Add(Vector3.Zero); _kneeBase.Add(Vector3.Zero);

            var arm = Pivot(_body, new Vector3(sx * bw * 0.55f, gh * 0.38f, 0));
            Part(arm, Cyl(s * 0.18f, armLen), limb, new Vector3(0, -armLen / 2f, 0), Vector3.Zero, Vector3.One);
            Part(arm, Sph(s * 0.22f), limb, new Vector3(0, -armLen, 0), Vector3.Zero, Vector3.One);
            _arms.Add(arm);
        }
    }

    // ---- goblin: spindly, hunched, big head, crooked nose, asymmetric ears ----
    private void Goblin(float s, Material body, Material limb, Material accent, bool bomber)
    {
        float v = bomber ? 0.8f : R(0.85f, 1.15f);    // per-instance size jitter
        float gh = s * 0.95f * v;
        _body = Pivot(this, new Vector3(0, gh, 0));
        _bodyBaseY = gh;
        float bw = s * 0.62f * v;
        Part(_body, Box(bw, gh * 0.9f, bw * 0.7f), body, Vector3.Zero, new Vector3(R(16, 26), 0, 0), Vector3.One);   // hunched torso
        Part(_body, Sph(s * 0.3f), body, new Vector3(R(-0.06f, 0.06f) * s, gh * 0.2f, -bw * 0.4f), Vector3.Zero, new Vector3(1.1f, 0.7f, 1f));   // hunchback lump

        // oversized head, jutting forward
        var head = Part(_body, Sph(s * 0.52f * v), body, new Vector3(0, gh * 0.55f, s * 0.22f), Vector3.Zero, new Vector3(1f, 0.92f, 1.05f));
        Part(head, Cone(s * 0.13f, s * 0.7f), body, new Vector3(R(-0.04f, 0.04f) * s, -s * 0.05f, s * 0.45f), new Vector3(R(78, 104), 0, 0), Vector3.One);   // long crooked nose
        float earL = R(0.45f, 0.8f), earR = R(0.45f, 0.8f);   // asymmetric ears
        Part(head, Cone(s * 0.16f, s * earL), limb, new Vector3(s * 0.45f, s * 0.12f, 0), new Vector3(0, 0, R(-95, -60)), Vector3.One);
        Part(head, Cone(s * 0.16f, s * earR), limb, new Vector3(-s * 0.45f, s * 0.12f, 0), new Vector3(0, 0, R(60, 95)), Vector3.One);
        Part(head, Sph(s * 0.09f), accent, new Vector3(s * 0.18f, s * 0.08f, s * 0.4f), Vector3.Zero, Vector3.One);     // beady eyes
        Part(head, Sph(s * 0.09f), accent, new Vector3(-s * 0.18f, s * 0.08f, s * 0.4f), Vector3.Zero, Vector3.One);
        Part(head, Box(s * 0.3f, s * 0.06f, s * 0.12f), limb, new Vector3(0, -s * 0.22f, s * 0.34f), new Vector3(R(-8, 8), 0, 0), Vector3.One);   // jagged mouth
        for (int w = 0; w < 3; w++)   // warts
            Part(head, Sph(s * R(0.04f, 0.07f)), limb, new Vector3(R(-0.4f, 0.4f) * s, R(-0.2f, 0.4f) * s, R(0.2f, 0.45f) * s), Vector3.Zero, Vector3.One);

        float legLen = gh * 0.5f, armLen = gh * 0.62f;   // long gangly arms
        for (int i = 0; i < 2; i++)
        {
            float sx = i == 0 ? 1 : -1;
            var hip = Pivot(this, new Vector3(sx * bw * 0.28f, gh * 0.4f, 0));
            Part(hip, Cyl(s * 0.11f, legLen), limb, new Vector3(0, -legLen / 2f, 0), Vector3.Zero, Vector3.One);
            var knee = Pivot(hip, new Vector3(0, -legLen, 0));
            Part(knee, Cyl(s * 0.09f, legLen), limb, new Vector3(0, -legLen / 2f, 0), Vector3.Zero, Vector3.One);
            Part(knee, Box(s * 0.26f, s * 0.1f, s * 0.5f), limb, new Vector3(0, -legLen, s * 0.12f), Vector3.Zero, Vector3.One);   // big splayed foot
            _hips.Add(hip); _knees.Add(knee); _hipBase.Add(Vector3.Zero); _kneeBase.Add(Vector3.Zero);

            var arm = Pivot(_body, new Vector3(sx * bw * 0.55f, gh * 0.32f, 0));
            Part(arm, Cyl(s * 0.09f, armLen), limb, new Vector3(0, -armLen / 2f, 0), Vector3.Zero, Vector3.One);
            Part(arm, Sph(s * 0.15f), limb, new Vector3(0, -armLen, 0), Vector3.Zero, Vector3.One);   // big knobbly hand
            _arms.Add(arm);
        }

        if (bomber)
        {
            // clutches an explosive keg to its chest, lit fuse sparking
            _keg = Pivot(_body, new Vector3(0, gh * 0.05f, s * 0.5f));
            Part(_keg, Cyl(s * 0.42f, s * 0.7f), limb, Vector3.Zero, new Vector3(90, 0, 0), Vector3.One);
            Part(_keg, Cyl(s * 0.44f, s * 0.1f), accent, new Vector3(0, 0, s * 0.18f), new Vector3(90, 0, 0), Vector3.One);   // iron band
            Part(_keg, Cyl(s * 0.03f, s * 0.4f), limb, new Vector3(0, s * 0.3f, 0), Vector3.Zero, Vector3.One);   // fuse
            Part(_keg, Sph(s * 0.12f), accent, new Vector3(0, s * 0.52f, 0), Vector3.Zero, Vector3.One);          // spark
        }
    }

    // ---- orc: massive, brutish, tiny sunken head, big tusks, back spikes ----
    private void Orc(float s, Material body, Material limb, Material accent)
    {
        float v = R(0.9f, 1.2f);
        float gh = s * 1.35f * v;
        _body = Pivot(this, new Vector3(0, gh, 0));
        _bodyBaseY = gh;
        float bw = s * 1.7f * v;
        Part(_body, Box(bw, gh, bw * 0.85f), body, Vector3.Zero, new Vector3(R(6, 14), 0, 0), Vector3.One);                  // huge torso
        Part(_body, Box(bw * 1.25f, gh * 0.4f, bw * 0.6f), limb, new Vector3(0, gh * 0.45f, -s * 0.1f), Vector3.Zero, Vector3.One);   // broad shoulders
        // back spikes
        for (int sp = 0; sp < 4; sp++)
            Part(_body, Cone(s * R(0.12f, 0.2f), s * R(0.4f, 0.7f)), limb, new Vector3(R(-0.3f, 0.3f) * s, gh * (0.1f + sp * 0.12f), -bw * 0.45f), new Vector3(-120, 0, 0), Vector3.One);

        // small head sunk between shoulders
        var head = Part(_body, Sph(s * 0.46f), body, new Vector3(0, gh * 0.5f, s * 0.18f), Vector3.Zero, new Vector3(1.15f, 0.85f, 1f));
        Part(head, Box(s * 0.7f, s * 0.18f, s * 0.3f), limb, new Vector3(0, s * 0.18f, s * 0.28f), new Vector3(-12, 0, 0), Vector3.One);   // heavy brow
        Part(head, Sph(s * 0.08f), accent, new Vector3(s * 0.16f, s * 0.04f, s * 0.36f), Vector3.Zero, Vector3.One);
        Part(head, Sph(s * 0.08f), accent, new Vector3(-s * 0.16f, s * 0.04f, s * 0.36f), Vector3.Zero, Vector3.One);
        Part(head, Box(s * 0.6f, s * 0.22f, s * 0.28f), limb, new Vector3(0, -s * 0.22f, s * 0.3f), new Vector3(8, 0, 0), Vector3.One);   // jutting jaw
        Part(head, Cone(s * 0.13f, s * R(0.45f, 0.65f)), accent, new Vector3(s * 0.22f, -s * 0.25f, s * 0.4f), new Vector3(R(-30, -10), 0, 0), Vector3.One);   // tusks up
        Part(head, Cone(s * 0.13f, s * R(0.45f, 0.65f)), accent, new Vector3(-s * 0.22f, -s * 0.25f, s * 0.4f), new Vector3(R(-30, -10), 0, 0), Vector3.One);

        float legLen = gh * 0.42f, armLen = gh * 0.6f;   // thick stubby legs, long dragging arms
        for (int i = 0; i < 2; i++)
        {
            float sx = i == 0 ? 1 : -1;
            var hip = Pivot(this, new Vector3(sx * bw * 0.34f, gh * 0.34f, 0));
            Part(hip, Cyl(s * 0.3f, legLen), limb, new Vector3(0, -legLen / 2f, 0), Vector3.Zero, Vector3.One);
            var knee = Pivot(hip, new Vector3(0, -legLen, 0));
            Part(knee, Cyl(s * 0.26f, legLen), limb, new Vector3(0, -legLen / 2f, 0), Vector3.Zero, Vector3.One);
            Part(knee, Box(s * 0.5f, s * 0.18f, s * 0.7f), limb, new Vector3(0, -legLen, s * 0.12f), Vector3.Zero, Vector3.One);   // big feet
            _hips.Add(hip); _knees.Add(knee); _hipBase.Add(Vector3.Zero); _kneeBase.Add(Vector3.Zero);

            var arm = Pivot(_body, new Vector3(sx * bw * 0.62f, gh * 0.4f, 0));
            Part(arm, Cyl(s * 0.24f, armLen), limb, new Vector3(0, -armLen / 2f, 0), Vector3.Zero, Vector3.One);
            Part(arm, Sph(s * 0.32f), limb, new Vector3(0, -armLen, 0), Vector3.Zero, Vector3.One);   // huge fists
            _arms.Add(arm);
        }
    }

    // ---- THE HOLLOW MOON: tall half-orc / half-zombie. LEFT body alive, RIGHT body rotting; a giant HOLE bored
    // clean through his midsection (he's hollow); a ZOMBIE goblin perched on the left shoulder + a NON-zombie
    // goblin on the right. Legs index 0 = left(alive), 1 = right(zombie, drags in the walk). ----
    private void HollowBoss(float s, Material body, Material limb, Material accent)
    {
        float gh = s * 1.55f;
        _body = Pivot(this, new Vector3(0, gh, 0));
        _bodyBaseY = gh;
        float bw = s * 1.7f;

        var aBody = Game.Toon(new Color(0.46f, 0.56f, 0.36f), 0.95f, 0.25f);   // living orc flesh (left)
        var aLimb = Game.Toon(new Color(0.34f, 0.42f, 0.26f), 0.95f, 0.2f);
        var zBody = Game.Toon(new Color(0.40f, 0.44f, 0.34f), 0.95f, 0.2f);    // rotting green-gray (right)
        var zLimb = Game.Toon(new Color(0.28f, 0.32f, 0.24f), 0.95f, 0.2f);
        var deadEye = Game.Emissive(new Color(1f, 0.18f, 0.12f), 2.2f);        // dead red (zombie side)

        // UPPER CHEST — split alive / zombie
        Part(_body, Box(bw * 0.52f, gh * 0.42f, bw * 0.62f), aBody, new Vector3(-bw * 0.26f, gh * 0.3f, 0), new Vector3(8, 0, 0), Vector3.One);
        Part(_body, Box(bw * 0.52f, gh * 0.42f, bw * 0.62f), zBody, new Vector3(bw * 0.26f, gh * 0.3f, 0), new Vector3(8, 0, 0), Vector3.One);
        // broad shoulders (split)
        Part(_body, Box(bw * 0.72f, gh * 0.28f, bw * 0.62f), aLimb, new Vector3(-bw * 0.36f, gh * 0.56f, -s * 0.05f), Vector3.Zero, Vector3.One);
        Part(_body, Box(bw * 0.72f, gh * 0.28f, bw * 0.62f), zLimb, new Vector3(bw * 0.36f, gh * 0.56f, -s * 0.05f), Vector3.Zero, Vector3.One);

        // THE HOLLOW HOLE — a big vertical ring you see straight through, with ragged flesh strands across it
        float holeR = bw * 0.44f;
        Part(_body, new TorusMesh { InnerRadius = holeR * 0.7f, OuterRadius = holeR, Rings = 20, RingSegments = 20 }, aLimb, new Vector3(0, -gh * 0.02f, 0), new Vector3(90, 0, 0), Vector3.One);
        Part(_body, Cyl(s * 0.06f, holeR * 1.4f), zLimb, new Vector3(bw * 0.06f, -gh * 0.02f, 0), new Vector3(0, 0, 74), Vector3.One);   // torn strand
        Part(_body, Cyl(s * 0.05f, holeR * 1.15f), aLimb, new Vector3(-bw * 0.05f, gh * 0.05f, 0), new Vector3(0, 0, -54), Vector3.One);

        // LOWER BODY / pelvis (split) + a back spine behind the hole so he's one piece
        Part(_body, Box(bw * 0.5f, gh * 0.32f, bw * 0.6f), aBody, new Vector3(-bw * 0.26f, -gh * 0.44f, 0), Vector3.Zero, Vector3.One);
        Part(_body, Box(bw * 0.5f, gh * 0.32f, bw * 0.6f), zBody, new Vector3(bw * 0.26f, -gh * 0.44f, 0), Vector3.Zero, Vector3.One);
        Part(_body, Box(bw * 0.34f, gh * 0.72f, bw * 0.2f), zLimb, new Vector3(0, -gh * 0.05f, -bw * 0.36f), Vector3.Zero, Vector3.One);   // spine

        // HEAD — split alive / zombie, sunk between the shoulders
        var head = Pivot(_body, new Vector3(0, gh * 0.66f, s * 0.12f));
        Part(head, Sph(s * 0.5f), aBody, new Vector3(-s * 0.24f, 0, 0), Vector3.Zero, new Vector3(1.1f, 0.92f, 1f));
        Part(head, Sph(s * 0.5f), zBody, new Vector3(s * 0.24f, 0, 0), Vector3.Zero, new Vector3(1.1f, 0.92f, 1f));
        Part(head, Box(s * 0.95f, s * 0.2f, s * 0.3f), aLimb, new Vector3(0, s * 0.24f, s * 0.28f), new Vector3(-12, 0, 0), Vector3.One);   // heavy brow
        Part(head, Sph(s * 0.1f), accent, new Vector3(-s * 0.2f, s * 0.02f, s * 0.42f), Vector3.Zero, Vector3.One);     // left eye: bright moonlight
        Part(head, Sph(s * 0.1f), deadEye, new Vector3(s * 0.2f, s * 0.02f, s * 0.42f), Vector3.Zero, Vector3.One);     // right eye: dead red
        Part(head, Box(s * 0.7f, s * 0.22f, s * 0.28f), aLimb, new Vector3(0, -s * 0.24f, s * 0.32f), new Vector3(8, 0, 0), Vector3.One);   // jutting jaw
        Part(head, Cone(s * 0.14f, s * 0.62f), accent, new Vector3(s * 0.24f, -s * 0.26f, s * 0.44f), new Vector3(-20, 0, 0), Vector3.One);   // tusks
        Part(head, Cone(s * 0.14f, s * 0.62f), accent, new Vector3(-s * 0.24f, -s * 0.26f, s * 0.44f), new Vector3(-20, 0, 0), Vector3.One);

        // SHOULDER RIDERS — left = a real ZOMBIE (pestilence caster), right = a real GOBLIN (mine-thrower)
        float gs = s * 0.42f;
        _gobZombie = new Creature();
        _gobZombie.Build(CreatureKind.Zombie, gs, Game.Toon(new Color(0.40f, 0.47f, 0.30f), 0.95f, 0.2f), Game.Toon(new Color(0.26f, 0.30f, 0.20f), 0.95f, 0.2f), Game.Emissive(new Color(1f, 0.2f, 0.15f), 2f));
        var lp = Pivot(_body, new Vector3(-bw * 0.44f, gh * 0.6f, -s * 0.02f));
        lp.AddChild(_gobZombie);
        _gobNormal = new Creature();
        _gobNormal.Build(CreatureKind.Goblin, gs, Game.Toon(new Color(0.42f, 0.6f, 0.25f), 0.95f, 0.2f), Game.Toon(new Color(0.28f, 0.42f, 0.18f), 0.95f, 0.2f), Game.Emissive(new Color(1f, 0.85f, 0.2f), 2f));
        var rp = Pivot(_body, new Vector3(bw * 0.44f, gh * 0.6f, -s * 0.02f));
        rp.AddChild(_gobNormal);

        // LEGS + ARMS — index 0 left(alive), 1 right(zombie, drags)
        float legLen = gh * 0.52f, armLen = gh * 0.68f;
        for (int i = 0; i < 2; i++)
        {
            float sx = i == 0 ? -1 : 1;
            var lm = i == 0 ? aLimb : zLimb;
            var hip = Pivot(this, new Vector3(sx * bw * 0.3f, gh * 0.26f, 0));
            Part(hip, Cyl(s * 0.33f, legLen), lm, new Vector3(0, -legLen / 2f, 0), Vector3.Zero, Vector3.One);
            var knee = Pivot(hip, new Vector3(0, -legLen, 0));
            Part(knee, Cyl(s * 0.29f, legLen), lm, new Vector3(0, -legLen / 2f, 0), Vector3.Zero, Vector3.One);
            Part(knee, Box(s * 0.55f, s * 0.2f, s * 0.78f), lm, new Vector3(0, -legLen, s * 0.12f), Vector3.Zero, Vector3.One);
            _hips.Add(hip); _knees.Add(knee); _hipBase.Add(Vector3.Zero); _kneeBase.Add(Vector3.Zero);

            var arm = Pivot(_body, new Vector3(sx * bw * 0.56f, gh * 0.42f, 0));
            Part(arm, Cyl(s * 0.27f, armLen), lm, new Vector3(0, -armLen / 2f, 0), Vector3.Zero, Vector3.One);
            Part(arm, Sph(s * 0.36f), lm, new Vector3(0, -armLen, 0), Vector3.Zero, Vector3.One);   // big fists
            _arms.Add(arm);
        }
    }

    private void ShoulderGoblin(float x, float y, Material gbody, Material glimb, Color eyeCol, float s)
    {
        var g = Pivot(_body, new Vector3(x, y, -s * 0.04f));
        g.Scale = Vector3.One * 0.52f;   // perched mini-goblin
        Part(g, Sph(s * 0.55f), gbody, Vector3.Zero, new Vector3(14, 0, 0), new Vector3(1f, 0.9f, 1f));   // hunched body
        var gheadN = Part(g, Sph(s * 0.5f), gbody, new Vector3(0, s * 0.62f, s * 0.05f), Vector3.Zero, Vector3.One);
        Part(gheadN, Cone(s * 0.16f, s * 0.62f), glimb, new Vector3(s * 0.42f, s * 0.16f, 0), new Vector3(0, 0, -68), Vector3.One);   // ears
        Part(gheadN, Cone(s * 0.16f, s * 0.62f), glimb, new Vector3(-s * 0.42f, s * 0.16f, 0), new Vector3(0, 0, 68), Vector3.One);
        var eye = Game.Emissive(eyeCol, 2.4f);
        Part(gheadN, Sph(s * 0.11f), eye, new Vector3(s * 0.17f, 0, s * 0.36f), Vector3.Zero, Vector3.One);
        Part(gheadN, Sph(s * 0.11f), eye, new Vector3(-s * 0.17f, 0, s * 0.36f), Vector3.Zero, Vector3.One);
        Part(gheadN, Box(s * 0.42f, s * 0.1f, s * 0.2f), glimb, new Vector3(0, -s * 0.28f, s * 0.3f), Vector3.Zero, Vector3.One);   // fanged grin
    }

    // ---- spider: bent legs (femur out/up, tibia down), animated tetrapod stepping ----
    private void Spider(float s, Material body, Material limb, Material accent)
    {
        float v = R(0.85f, 1.2f);
        s *= v;
        _body = Pivot(this, new Vector3(0, s * 0.85f, 0));
        _bodyBaseY = s * 0.85f;
        Part(_body, Sph(s * 0.75f), body, new Vector3(0, 0, -s * 0.55f), Vector3.Zero, new Vector3(1, 0.78f, 1.25f));   // abdomen
        var ceph = Part(_body, Sph(s * 0.5f), body, new Vector3(0, 0, s * 0.4f), Vector3.Zero, Vector3.One);            // cephalothorax
        for (int e = 0; e < 6; e++)
            Part(ceph, Sph(s * R(0.05f, 0.09f)), accent, new Vector3((e % 2 == 0 ? 1 : -1) * s * R(0.12f, 0.24f), s * 0.18f, s * (0.35f - (e / 2) * 0.1f)), Vector3.Zero, Vector3.One);   // eye cluster
        Part(ceph, Cone(s * 0.1f, s * 0.45f), limb, new Vector3(s * 0.16f, -s * 0.12f, s * 0.46f), new Vector3(125, 0, 0), Vector3.One);   // fangs
        Part(ceph, Cone(s * 0.1f, s * 0.45f), limb, new Vector3(-s * 0.16f, -s * 0.12f, s * 0.46f), new Vector3(125, 0, 0), Vector3.One);

        float femur = s * 0.7f, tibia = s * 0.95f;
        for (int i = 0; i < 8; i++)
        {
            int side = i < 4 ? 1 : -1;
            int idx = i % 4;
            float along = (idx - 1.5f) * s * 0.34f;
            float yaw = side * (50f + idx * 16f);
            // hip: femur juts outward and slightly up
            var hip = Pivot(this, new Vector3(side * s * 0.42f, s * 0.78f, along + s * 0.15f), new Vector3(-28f, yaw, side * -8f));
            Part(hip, Cyl(s * 0.06f, femur), limb, new Vector3(0, -femur / 2f, 0), Vector3.Zero, Vector3.One);
            // knee bends the tibia back down toward the ground
            var knee = Pivot(hip, new Vector3(0, -femur, 0), new Vector3(95f, 0, 0));
            Part(knee, Cyl(s * 0.05f, tibia), limb, new Vector3(0, -tibia / 2f, 0), Vector3.Zero, Vector3.One);
            _hips.Add(hip); _knees.Add(knee);
            _hipBase.Add(new Vector3(-28f, yaw, side * -8f));
            _kneeBase.Add(new Vector3(95f, 0, 0));
        }
    }

    // ---- mosquito ----
    private void Mosquito(float s, Material body, Material limb, Material accent)
    {
        float v = R(0.85f, 1.15f); s *= v;
        _body = Pivot(this, Vector3.Zero);
        _bodyBaseY = 0f;
        Part(_body, Sph(s * 0.45f), body, new Vector3(0, 0, s * 0.5f), Vector3.Zero, Vector3.One);
        Part(_body, Sph(s * 0.55f), body, Vector3.Zero, Vector3.Zero, Vector3.One);
        Part(_body, Cyl(s * 0.28f, s * 1.4f), body, new Vector3(0, 0, -s * 0.9f), new Vector3(90, 0, 0), new Vector3(1, 1, 0.6f));
        Part(_body, Cone(s * 0.06f, s * 1.2f), accent, new Vector3(0, -s * 0.1f, s * 1.25f), new Vector3(90, 0, 0), Vector3.One);
        Part(_body, Sph(s * 0.22f), accent, new Vector3(s * 0.25f, s * 0.1f, s * 0.6f), Vector3.Zero, Vector3.One);
        Part(_body, Sph(s * 0.22f), accent, new Vector3(-s * 0.25f, s * 0.1f, s * 0.6f), Vector3.Zero, Vector3.One);
        for (int i = 0; i < 2; i++)
        {
            float sx = i == 0 ? 1 : -1;
            var w = Pivot(_body, new Vector3(sx * s * 0.2f, s * 0.35f, 0));
            Part(w, Box(s * 1.5f, s * 0.04f, s * 0.7f), limb, new Vector3(sx * s * 0.75f, 0, -s * 0.1f), Vector3.Zero, Vector3.One);
            _wings.Add(w);
        }
        for (int i = 0; i < 6; i++)
        {
            int side = i < 3 ? 1 : -1;
            float along = ((i % 3) - 1) * s * 0.4f;
            var hip = Pivot(_body, new Vector3(side * s * 0.4f, -s * 0.2f, along), new Vector3(0, side * 30f, side * 50f));
            Part(hip, Cyl(s * 0.04f, s * 1.3f), limb, new Vector3(0, -s * 0.65f, 0), Vector3.Zero, Vector3.One);
            _hips.Add(hip); _knees.Add(null);
            _hipBase.Add(new Vector3(0, side * 30f, side * 50f)); _kneeBase.Add(Vector3.Zero);
        }
    }

    // ---- zapper: a floating hooded wizard that rears back and casts lightning ----
    private void Zapper(float s, Material body, Material limb, Material accent)
    {
        float v = R(0.9f, 1.1f); s *= v;
        _body = Pivot(this, new Vector3(0, s * 1.35f, 0));   // hovers
        _bodyBaseY = s * 1.35f;
        Part(_body, new CylinderMesh { TopRadius = s * 0.25f, BottomRadius = s * 0.95f, Height = s * 1.6f }, body, new Vector3(0, -s * 0.5f, 0), Vector3.Zero, Vector3.One);   // robe
        Part(_body, new CylinderMesh { TopRadius = s * 0.3f, BottomRadius = s * 0.55f, Height = s * 0.2f }, limb, new Vector3(0, -s * 1.25f, 0), Vector3.Zero, Vector3.One);   // tattered hem
        Part(_body, Sph(s * 0.38f), body, new Vector3(0, s * 0.4f, 0), Vector3.Zero, new Vector3(1, 1.1f, 1));   // shoulders
        var head = Part(_body, Sph(s * 0.32f), body, new Vector3(0, s * 0.78f, 0.02f), Vector3.Zero, Vector3.One);
        Part(head, Cone(s * 0.5f, s * 0.85f), limb, new Vector3(0, s * 0.2f, -s * 0.04f), new Vector3(10, 0, 0), Vector3.One);   // pointed hood
        Part(head, Sph(s * 0.07f), accent, new Vector3(s * 0.12f, -s * 0.02f, s * 0.27f), Vector3.Zero, Vector3.One);            // eyes
        Part(head, Sph(s * 0.07f), accent, new Vector3(-s * 0.12f, -s * 0.02f, s * 0.27f), Vector3.Zero, Vector3.One);
        for (int i = 0; i < 2; i++)
        {
            float sx = i == 0 ? 1 : -1;
            var arm = Pivot(_body, new Vector3(sx * s * 0.42f, s * 0.5f, 0));
            Part(arm, Cyl(s * 0.1f, s * 0.7f), limb, new Vector3(0, -s * 0.35f, 0), Vector3.Zero, Vector3.One);
            Part(arm, Sph(s * 0.13f), accent, new Vector3(0, -s * 0.72f, 0), Vector3.Zero, Vector3.One);   // glowing hands
            _arms.Add(arm);
        }
        _orb = Part(_body, Sph(s * 0.26f), accent, new Vector3(0, s * 0.55f, s * 0.55f), Vector3.Zero, Vector3.One);   // focus orb
        for (int i = 0; i < 4; i++)
            Part(_orb, Cone(s * 0.05f, s * 0.32f), accent, Vector3.Zero, new Vector3(R(0, 360), R(0, 360), R(0, 360)), Vector3.One);   // crackle spikes
    }

    public void Animate(float dt, float move)
    {
        if (AnimSuspended) return;   // (PERF) invisible foe (far + outside the frustum) → freeze the pose, skip all the per-part transform writes
        move = Mathf.Clamp(move, 0f, 1f);
        switch (_kind)
        {
            case CreatureKind.Goblin:
            case CreatureKind.Bomber:
            case CreatureKind.Orc:
            case CreatureKind.Crocodile:
            case CreatureKind.Troll:
            case CreatureKind.Pigmy:
                {
                    float spd = (_kind == CreatureKind.Orc || _kind == CreatureKind.Troll) ? 3f : (_kind == CreatureKind.Bomber || _kind == CreatureKind.Pigmy ? 6f : 4.5f);
                    _phase += dt * (1.5f + move * spd);
                    float sw = (8f + move * ((_kind == CreatureKind.Orc || _kind == CreatureKind.Troll) ? 30f : 40f));
                    for (int i = 0; i < _hips.Count; i++)
                    {
                        float a = Mathf.Sin(_phase + i * Mathf.Pi);
                        _hips[i].RotationDegrees = new Vector3(a * sw, 0, 0);
                        if (_knees[i] != null) _knees[i].RotationDegrees = new Vector3(Mathf.Max(0f, a) * sw * 0.9f, 0, 0);
                    }
                    for (int i = 0; i < _arms.Count; i++)
                        _arms[i].RotationDegrees = new Vector3(-Mathf.Sin(_phase + i * Mathf.Pi) * sw * 0.6f, 0, 0);
                    // melee swing overlay: arms rear back through the wind-up, then slam forward on the strike
                    _swing = Mathf.MoveToward(_swing, _swingTarget, dt * 3.5f);
                    _strike = Mathf.MoveToward(_strike, 0f, dt * 7f);
                    if (_swing > 0.01f || _strike > 0.01f)
                    {
                        float raise = _swing * 80f - _strike * 150f;
                        for (int i = 0; i < _arms.Count; i++)
                            _arms[i].RotationDegrees = new Vector3(-raise, 0, _arms[i].RotationDegrees.Z);
                    }
                    if (_body != null) _body.Position = new Vector3(0, _bodyBaseY + Mathf.Abs(Mathf.Sin(_phase)) * _scale * 0.06f * (0.5f + move), 0);
                    if (_keg != null) _keg.RotationDegrees = new Vector3(Mathf.Sin(_phase * 2f) * 5f, 0, 0);
                }
                break;

            case CreatureKind.HollowBoss:
                {
                    _phase += dt * (1.3f + move * 3.0f);   // slow and heavy
                    float sw = 8f + move * 26f;
                    for (int i = 0; i < _hips.Count; i++)
                    {
                        float a = Mathf.Sin(_phase + i * Mathf.Pi);
                        float drag = i == 1 ? 0.42f : 1f;   // RIGHT (zombie) leg barely lifts → it drags/lurches
                        _hips[i].RotationDegrees = new Vector3(a * sw * drag - (i == 1 ? 8f : 0f), 0, 0);
                        if (_knees[i] != null) _knees[i].RotationDegrees = new Vector3(Mathf.Max(0f, a) * sw * (i == 1 ? 0.35f : 0.9f), 0, 0);
                    }
                    for (int i = 0; i < _arms.Count; i++)
                        _arms[i].RotationDegrees = new Vector3(-Mathf.Sin(_phase + i * Mathf.Pi) * sw * 0.5f + (i == 1 ? -16f : 0f), 0, (i == 1 ? 8f : 0f));   // right arm hangs limp
                    _swing = Mathf.MoveToward(_swing, _swingTarget, dt * 3.5f);
                    _strike = Mathf.MoveToward(_strike, 0f, dt * 7f);
                    if (_swing > 0.01f || _strike > 0.01f)
                    {
                        float raise = _swing * 80f - _strike * 150f;
                        for (int i = 0; i < _arms.Count; i++) _arms[i].RotationDegrees = new Vector3(-raise, 0, _arms[i].RotationDegrees.Z);
                    }
                    if (_body != null)
                    {
                        _body.RotationDegrees = new Vector3(4f + Mathf.Sin(_phase) * 2f, Mathf.Sin(_phase * 0.5f) * 3f, 5f + Mathf.Sin(_phase) * 3f);   // lurch/lean toward the dead side
                        _body.Position = new Vector3(0, _bodyBaseY + Mathf.Abs(Mathf.Sin(_phase)) * _scale * 0.05f * (0.5f + move), 0);
                    }
                    _gobFireZ = Mathf.MoveToward(_gobFireZ, 0f, dt * 1.5f);
                    _gobFireN = Mathf.MoveToward(_gobFireN, 0f, dt * 1.5f);
                    _gobZombie?.Animate(dt, _gobFireZ > 0.05f ? 0.6f : 0.04f);   // idle sway; lunge/cast when firing
                    _gobNormal?.Animate(dt, _gobFireN > 0.05f ? 0.6f : 0.04f);
                    if (StompWind > 0.01f && _hips.Count > 0) _hips[0].RotationDegrees = new Vector3(-StompWind * 70f, 0, 0);   // wind-up: raise the good leg; it slams down on release
                }
                break;

            case CreatureKind.Zombie:
                {
                    _phase += dt * (1.1f + move * 3.2f);   // slow, dragging shamble
                    bool idlePosing = move < 0.06f && IdlePose > 0 && _scream <= 0.01f;
                    float dropY = idlePosing ? (IdlePose == 1 ? -_scale * 0.55f : IdlePose == 2 ? -_scale * 0.28f : 0f) : 0f;
                    Position = new Vector3(Position.X, Mathf.MoveToward(Position.Y, dropY, dt * 5f), Position.Z);   // sit lie/slump/stun on the floor, not floating
                    if (idlePosing) ZombieIdlePose();
                    else
                    {
                        float swz = 6f + move * 20f;
                        for (int i = 0; i < _hips.Count; i++)
                        {
                            float a = Mathf.Sin(_phase + i * Mathf.Pi);
                            float drag = i == 0 ? 1f : 0.55f;   // one leg drags → limp
                            _hips[i].RotationDegrees = new Vector3(a * swz * drag, 0, 0);
                            if (_knees[i] != null) _knees[i].RotationDegrees = new Vector3(Mathf.Max(0f, a) * swz * 0.7f, 0, 0);
                        }
                        for (int i = 0; i < _arms.Count; i++)
                            _arms[i].RotationDegrees = new Vector3(-72f + Mathf.Sin(_phase * 0.8f + i) * 8f, 0, (i == 0 ? 10f : -10f) + Mathf.Sin(_phase) * 4f);   // arms dangle forward, reaching
                        _swing = Mathf.MoveToward(_swing, _swingTarget, dt * 3.5f);
                        _strike = Mathf.MoveToward(_strike, 0f, dt * 7f);
                        if (_strike > 0.01f)
                            for (int i = 0; i < _arms.Count; i++)
                                _arms[i].RotationDegrees = new Vector3(-72f - _strike * 70f, 0, _arms[i].RotationDegrees.Z);   // lunge on hit
                        if (_body != null)
                        {
                            _body.RotationDegrees = new Vector3(18f + Mathf.Sin(_phase * 0.6f) * 4f, Mathf.Sin(_phase * 0.5f) * 6f, Mathf.Sin(_phase) * 5f);   // hunched, swaying
                            _body.Position = new Vector3(Mathf.Sin(_phase * 0.5f) * _scale * 0.04f, _bodyBaseY + Mathf.Abs(Mathf.Sin(_phase)) * _scale * 0.03f, 0);
                        }
                    }
                    _scream = Mathf.MoveToward(_scream, 0f, dt * 0.8f);   // decays over ~1.2s
                    if (_scream > 0.01f)
                    {
                        for (int i = 0; i < _arms.Count; i++)
                            _arms[i].RotationDegrees = new Vector3(-150f, 0, (i == 0 ? 18f : -18f));   // arms up + out, palms to sky
                        if (_body != null) _body.RotationDegrees = new Vector3(-24f + Mathf.Sin(_phase * 9f) * 3f, 0, 0);   // head back, shrieking, shuddering
                    }
                }
                break;
            case CreatureKind.Spider:
                {
                    _phase += dt * (4f + move * 9f);
                    for (int i = 0; i < _hips.Count; i++)
                    {
                        int group = (i % 2) ^ ((i / 4) & 1);          // alternating tetrapod
                        float ph = _phase + group * Mathf.Pi;
                        float swing = Mathf.Sin(ph) * (4f + move * 16f);     // fore/aft sweep
                        float lift = Mathf.Max(0f, Mathf.Sin(ph)) * (move * 34f);   // raise foot on the forward half
                        var hb = _hipBase[i]; var kb = _kneeBase[i];
                        _hips[i].RotationDegrees = new Vector3(hb.X, hb.Y + swing, hb.Z);
                        if (_knees[i] != null) _knees[i].RotationDegrees = new Vector3(kb.X - lift, kb.Y, kb.Z);
                    }
                    if (_body != null) _body.Position = new Vector3(0, _bodyBaseY + Mathf.Abs(Mathf.Sin(_phase * 2f)) * _scale * 0.04f, 0);
                }
                break;

            case CreatureKind.Zapper:
                {
                    _cast = Mathf.MoveToward(_cast, _castTarget, dt * 4f);
                    _phase += dt * (1.6f + _cast * 4f);
                    if (_body != null) _body.Position = new Vector3(0, _bodyBaseY + Mathf.Sin(_phase) * _scale * 0.12f, 0);
                    for (int i = 0; i < _arms.Count; i++)
                        _arms[i].RotationDegrees = new Vector3(-25f - _cast * 120f, 0, (i == 0 ? 1 : -1) * (10f + _cast * 18f));   // arms rear back to cast
                    if (_orb != null)
                    {
                        float g = 1f + _cast * 1.2f + Mathf.Sin(_phase * 3f) * 0.06f;
                        _orb.Scale = Vector3.One * g;
                        _orb.Position = new Vector3(0, _scale * (0.55f + _cast * 0.3f), _scale * (0.55f + _cast * 0.1f));
                        _orb.RotateY(dt * 4f);
                    }
                }
                break;

            case CreatureKind.Mosquito:
                {
                    _wing += dt * 46f;
                    float flap = Mathf.Sin(_wing) * 55f;
                    for (int i = 0; i < _wings.Count; i++)
                        _wings[i].RotationDegrees = new Vector3(0, 0, (i == 0 ? 1 : -1) * flap);
                    _phase += dt * (2f + move * 3f);
                    for (int i = 0; i < _hips.Count; i++)
                    {
                        var b = _hipBase[i];
                        _hips[i].RotationDegrees = new Vector3(Mathf.Sin(_phase + i) * 6f, b.Y, b.Z + Mathf.Sin(_phase + i) * 4f);
                    }
                    if (_body != null) _body.Position = new Vector3(0, Mathf.Sin(_phase * 0.7f) * _scale * 0.12f, 0);
                }
                break;

            case CreatureKind.Pterodactyl:
            case CreatureKind.Bat:
                {
                    _cast = Mathf.MoveToward(_cast, _castTarget, dt * 3f);
                    bool charging = _kind == CreatureKind.Pterodactyl && _cast > 0.02f;   // ptero winding up its stun bolt
                    _wing += dt * (_kind == CreatureKind.Bat ? 22f : 13f) * (charging ? 2.4f : 1f);   // beat frantically while charging
                    float flap = Mathf.Sin(_wing) * (_kind == CreatureKind.Bat ? 46f : 40f);
                    for (int i = 0; i < _wings.Count; i++)
                        _wings[i].RotationDegrees = new Vector3(0, 0, (i == 0 ? 1 : -1) * (flap + (charging ? 35f * _cast : 0f)));   // wings sweep UP as it winds up
                    _phase += dt * 2f;
                    if (_body != null)
                    {
                        _body.RotationDegrees = new Vector3(charging ? -50f * _cast : 0f, 0f, 0f);                                   // rear back to cast — clear tell
                        _body.Position = new Vector3(0, Mathf.Sin(_phase) * _scale * 0.1f + (charging ? _cast * _scale * 0.35f : 0f), 0);   // rise as it charges
                        _body.Scale = Vector3.One * (charging ? 1f + Mathf.Sin(_phase * 11f) * 0.07f * _cast : 1f);                  // electric shudder
                    }
                }
                break;

            case CreatureKind.Snake:
                {
                    _phase += dt * (2f + move * 6f);
                    for (int i = 0; i < _hips.Count; i++)   // a travelling S-wave yaws each segment → slither
                        _hips[i].RotationDegrees = new Vector3(0, Mathf.Sin(_phase - i * 0.8f) * (7f + move * 15f), 0);
                }
                break;
        }
    }
}
