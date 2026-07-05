using Godot;

// Guardian.cs — the "Ancient Guardian" ultimate: a gnarled, ancient tree-ent summoned at the cursor
// that performs several ground slams, lurching toward the nearest foe between beats. Each slam does
// radial-falloff damage (devastating at the impact point, ~40% at the rim). Caster-simulated; damage
// routes through Enemy.Hurt so it forwards to the host on a client. It is SYNCED to all players: the
// owner broadcasts its transform + slam pulses (Net.GuardianState) and everyone else renders a Ghost
// copy that follows along and plays the slam animation. Legendary "Heartwood" adds slams, radius,
// poison and a root per stomp. Withers and frees after its last slam.
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
    private bool _slamFlag = false;
    public bool TakeSlamFlag() { bool f = _slamFlag; _slamFlag = false; return f; }
    private Vector3 _gpos; private float _gyaw = 0f; private bool _gInit = false; private float _idle = 0f;
    public void SetGhost(Vector3 pos, float yaw, bool slam) { _gpos = pos; _gyaw = yaw; _gInit = true; _idle = 0f; if (slam) { _anim = 0.28f; Stomp(pos); } }

    private int _done = 0;
    private float _beat = 0.6f;        // wind-up before the first slam
    private Node3D _body;
    private Node3D _armL, _armR;
    private float _phase = 0f;
    private float _anim = 0f;          // slam animation timer

    private void Add(Node3D parent, Mesh m, StandardMaterial3D mat, Vector3 pos, Vector3 roteul)
    {
        var mi = new MeshInstance3D { Mesh = m, MaterialOverride = mat };
        mi.Position = pos; mi.RotationDegrees = roteul; parent.AddChild(mi);
    }

    public override void _Ready()
    {
        var bark = new Color(0.30f, 0.22f, 0.13f);
        var darkBark = new Color(0.22f, 0.16f, 0.10f);
        var leaf = new Color(0.30f, 0.62f, 0.30f);
        var eye = new Color(0.7f, 1f, 0.5f);
        var barkMat = Game.ToonEmissive(bark, 0.25f, 0.02f);
        var darkMat = Game.ToonEmissive(darkBark, 0.2f, 0.02f);
        var leafMat = Game.ToonEmissive(leaf, 0.6f, 0.03f);

        _body = new Node3D(); AddChild(_body);

        // splayed buttress roots
        for (int i = 0; i < 5; i++)
        {
            float a = i / 5f * Mathf.Tau;
            var root = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.25f, BottomRadius = 0.7f, Height = 2.2f }, MaterialOverride = darkMat };
            root.Position = new Vector3(Mathf.Cos(a) * 1.1f, 0.7f, Mathf.Sin(a) * 1.1f);
            root.RotationDegrees = new Vector3(Mathf.Cos(a) * 32f, 0, -Mathf.Sin(a) * 32f);
            _body.AddChild(root);
        }
        // gnarled trunk — three leaning, offset segments
        Add(_body, new CylinderMesh { TopRadius = 1.0f, BottomRadius = 1.4f, Height = 2.6f }, barkMat, new Vector3(0f, 1.9f, 0f), new Vector3(4, 0, 3));
        Add(_body, new CylinderMesh { TopRadius = 0.8f, BottomRadius = 1.05f, Height = 2.4f }, barkMat, new Vector3(-0.25f, 3.9f, 0.12f), new Vector3(-3, 20, -6));
        Add(_body, new CylinderMesh { TopRadius = 0.55f, BottomRadius = 0.85f, Height = 2.2f }, barkMat, new Vector3(0.18f, 5.8f, -0.1f), new Vector3(5, -10, 4));
        // burls / knots
        Add(_body, new SphereMesh { Radius = 0.5f, Height = 1.0f }, barkMat, new Vector3(0.5f, 3.0f, 0.3f), Vector3.Zero);
        Add(_body, new SphereMesh { Radius = 0.4f, Height = 0.8f }, barkMat, new Vector3(-0.45f, 4.6f, -0.2f), Vector3.Zero);
        // glowing eyes (a hollow, ancient face)
        var eyeMat = Game.Emissive(eye, 3.0f);
        Add(_body, new SphereMesh { Radius = 0.16f, Height = 0.32f }, eyeMat, new Vector3(-0.32f, 5.4f, 0.7f), Vector3.Zero);
        Add(_body, new SphereMesh { Radius = 0.16f, Height = 0.32f }, eyeMat, new Vector3(0.30f, 5.5f, 0.68f), Vector3.Zero);
        // asymmetric mossy crown
        Add(_body, new SphereMesh { Radius = 2.4f, Height = 3.8f }, leafMat, new Vector3(0.2f, 7.6f, 0f), Vector3.Zero);
        Add(_body, new SphereMesh { Radius = 1.7f, Height = 2.8f }, leafMat, new Vector3(-1.7f, 7.0f, 0.4f), Vector3.Zero);
        Add(_body, new SphereMesh { Radius = 1.5f, Height = 2.4f }, leafMat, new Vector3(1.6f, 6.6f, -0.6f), Vector3.Zero);
        // twisted arms with finger-twigs
        _armL = new Node3D { Position = new Vector3(-1.5f, 4.6f, 0) }; _body.AddChild(_armL);
        Add(_armL, new CylinderMesh { TopRadius = 0.28f, BottomRadius = 0.5f, Height = 3.0f }, barkMat, new Vector3(-0.6f, 0.4f, 0), new Vector3(0, 0, 58));
        Add(_armL, new CylinderMesh { TopRadius = 0.12f, BottomRadius = 0.2f, Height = 1.1f }, barkMat, new Vector3(-1.5f, 1.1f, 0.2f), new Vector3(10, 0, 92));
        Add(_armL, new CylinderMesh { TopRadius = 0.1f, BottomRadius = 0.18f, Height = 1.0f }, barkMat, new Vector3(-1.5f, 1.0f, -0.3f), new Vector3(-20, 0, 100));
        _armR = new Node3D { Position = new Vector3(1.5f, 4.6f, 0) }; _body.AddChild(_armR);
        Add(_armR, new CylinderMesh { TopRadius = 0.28f, BottomRadius = 0.5f, Height = 3.0f }, barkMat, new Vector3(0.6f, 0.4f, 0), new Vector3(0, 0, -58));
        Add(_armR, new CylinderMesh { TopRadius = 0.12f, BottomRadius = 0.2f, Height = 1.1f }, barkMat, new Vector3(1.5f, 1.1f, 0.2f), new Vector3(10, 0, -92));
        Add(_armR, new CylinderMesh { TopRadius = 0.1f, BottomRadius = 0.18f, Height = 1.0f }, barkMat, new Vector3(1.5f, 1.0f, -0.3f), new Vector3(-20, 0, -100));

        AddChild(new OmniLight3D { Position = new Vector3(0, 5.5f, 0), OmniRange = 9f, LightColor = leaf, LightEnergy = 1.0f });
        if (Game.I != null) Game.AddFriendlySilhouette(this, new Color(0.4f, 0.85f, 0.4f), 1.5f, 9f, 4.5f);
    }

    public override void _Process(double delta)
    {
        if (Game.I == null || Game.I.State != GameState.Playing) return;   // freeze while paused (NEW)
        float dt = (float)delta;
        _phase += dt;
        if (_anim > 0f) { _anim -= dt; float k = Mathf.Clamp(_anim / 0.28f, 0f, 1f); if (_armL != null) { _armL.RotationDegrees = new Vector3(-50 * (1 - k), 0, 0); _armR.RotationDegrees = new Vector3(-50 * (1 - k), 0, 0); } }
        if (_body != null) _body.Rotation = new Vector3(0, _body.Rotation.Y, Mathf.Sin(_phase * 1.5f) * 0.035f);

        if (Ghost)   // network copy: follow synced transform, animate slams; no AI/damage
        {
            if (_gInit) { GlobalPosition = GlobalPosition.Lerp(_gpos, Mathf.Min(1f, dt * 10f)); if (_body != null) _body.Rotation = new Vector3(0, _gyaw, _body.Rotation.Z); }
            _idle += dt; if (_idle > 1.3f) QueueFree();   // owner stopped broadcasting → the guardian is gone
            return;
        }
        if (Caster == null || !GodotObject.IsInstanceValid(Caster)) { QueueFree(); return; }
        if (!Game.I.WorldRunning) return;

        _beat -= dt;
        if (_beat <= 0f)
        {
            Slam();
            _done++;
            _beat = 0.85f;
            if (_done >= Slams) { Wither(); return; }
        }
    }

    private void Slam()
    {
        // lurch toward the nearest foe so it stomps through the crowd
        Enemy near = null; float bd = 1e9f;
        foreach (var e in Game.I.Enemies.ToArray())
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
            float d = GlobalPosition.DistanceTo(e.GlobalPosition);
            if (d < bd) { bd = d; near = e; }
        }
        if (near != null && bd > 3f)
        {
            var step = (near.GlobalPosition - GlobalPosition); step.Y = 0;
            GlobalPosition += step.Normalized() * Mathf.Min(5f, step.Length() * 0.5f);
            if (_body != null) _body.Rotation = new Vector3(0, Mathf.Atan2(step.X, step.Z), _body.Rotation.Z);
        }
        _anim = 0.28f; _slamFlag = true;
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
        Game.I.DamageWorld(GlobalPosition, SlamRadius, SlamDamage);   // (NEW) the slam breaks props too
        Caster?.CamKickExternal(0.5f);
        Game.I.Sfx?.Impact(DamageType.Nature);
    }

    // visual shockwave (runs on owner + ghosts)
    private void Stomp(Vector3 at)
    {
        var col = new Color(0.45f, 0.85f, 0.4f);
        Game.I.VfxRing(new Vector3(at.X, 0.05f, at.Z), col, SlamRadius * 2f, 0.45f);
    }

    private void Wither()
    {
        if (Caster != null && GodotObject.IsInstanceValid(Caster) && Caster.ActiveGuardian == this) Caster.ActiveGuardian = null;
        var tw = _body.CreateTween();
        tw.TweenProperty(_body, "scale", new Vector3(0.05f, 0.05f, 0.05f), 0.5f);
        tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(this)) QueueFree(); }));
    }
}
