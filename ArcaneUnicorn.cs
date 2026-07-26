using Godot;
using System.Collections.Generic;

// ArcaneUnicorn.cs — the Summoner nerfer's payoff: an unkillable arcane-spectre unicorn. It FOLLOWS the closest warden until
// the world boss awakens, then CHARGES straight for it at 10 u/s (ignoring every add) and DETONATES for 20% of the boss's
// CURRENT max HP — wiping all adds within 32u and blooming an ArcaneNuke mushroom cloud. No health bar; mobs can't touch it.
public partial class ArcaneUnicorn : Node3D
{
    public bool Remote = false;
    public bool Charging => _phase >= 1;         // committed (teleported) → drives the glow + the synced client state
    private int _phase = 0;                       // 0 follow · 1 arcane-teleported + telegraphing the sprint · 2 galloping in
    private float _windupT = 0f;
    private bool _done = false, _rCommitted = false;
    private const float WindupDur = 3f, ChargeSpeed = 22f;
    private long _target = 1;                  // peer it follows (default host); updated by proximity claim or T-recall
    private Vector3 _rtarget; private bool _rhave;   // client ghost: host-synced position
    private float _t, _gait, _chargeGlow;
    private Node3D _rig;                       // yaw-facing rig holding the whole body
    private readonly List<MeshInstance3D> _legs = new();
    private readonly List<Node3D> _mane = new();
    private readonly List<Node3D> _tail = new();
    private ShaderMaterial _mat;
    private OmniLight3D _light;
    private GpuParticles3D _wake;
    private static readonly Color Col = new Color(0.72f, 0.5f, 1f);

    private const string SpectreShader = @"
shader_type spatial;
render_mode cull_disabled, depth_prepass_alpha, diffuse_lambert;
uniform vec3 col : source_color = vec3(0.72,0.5,1.0);
uniform float intensity = 1.0;
varying vec3 vp;
void vertex(){ vp = VERTEX; }
void fragment(){
    float fres = pow(1.0 - abs(dot(normalize(VIEW), normalize(NORMAL))), 2.2);
    float flow = 0.5 + 0.5*sin(vp.y*5.0 - TIME*5.0);                       // energy rising through the body
    float iris = 0.5 + 0.5*sin(vp.y*2.0 + TIME*1.5);
    vec3 irisC = 0.5 + 0.5*cos(6.2831*(iris + vec3(0.0,0.33,0.66)));       // faint iridescence
    vec3 c = mix(col, irisC, 0.22) * (0.6 + 1.1*fres + 0.35*flow) * intensity;
    ALBEDO = c;
    EMISSION = c * (1.2 + 1.6*fres);
    ALPHA = clamp(0.30 + 0.75*fres + 0.18*flow, 0.0, 0.92);
}";

    public override void _Ready()
    {
        _t = (float)GD.RandRange(0, 6.28);
        _mat = new ShaderMaterial { Shader = new Shader { Code = SpectreShader } };
        _mat.SetShaderParameter("col", new Vector3(Col.R, Col.G, Col.B));
        _mat.SetShaderParameter("intensity", 1f);

        _rig = new Node3D(); AddChild(_rig);
        MeshInstance3D M(Mesh mesh, Vector3 pos, Vector3 rotDeg, Vector3 scl, Node3D parent = null)
        {
            var p = new MeshInstance3D { Mesh = mesh, MaterialOverride = _mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Position = pos, RotationDegrees = rotDeg, Scale = scl };
            (parent ?? _rig).AddChild(p); return p;
        }
        // sleeker barrel body, tapered toward the hindquarters
        M(new SphereMesh { Radius = 0.62f, Height = 1.24f }, new Vector3(0, 1.45f, -0.1f), Vector3.Zero, new Vector3(0.92f, 0.86f, 1.9f));
        M(new SphereMesh { Radius = 0.42f, Height = 0.84f }, new Vector3(0, 1.5f, -0.95f), Vector3.Zero, new Vector3(0.8f, 0.8f, 1f));   // haunch
        // arched neck (two segments curving up-forward) + head
        var neck1 = M(new CylinderMesh { TopRadius = 0.26f, BottomRadius = 0.4f, Height = 0.7f }, new Vector3(0, 1.9f, 0.55f), new Vector3(-52, 0, 0), Vector3.One);
        M(new CylinderMesh { TopRadius = 0.2f, BottomRadius = 0.26f, Height = 0.55f }, new Vector3(0, 0.5f, 0.35f), new Vector3(28, 0, 0), Vector3.One, neck1);
        var head = M(new SphereMesh { Radius = 0.34f, Height = 0.68f }, new Vector3(0, 2.5f, 1.25f), new Vector3(20, 0, 0), new Vector3(0.78f, 0.78f, 1.25f));
        M(new CylinderMesh { TopRadius = 0.03f, BottomRadius = 0.16f, Height = 0.85f, RadialSegments = 6 }, new Vector3(0, 0.3f, 0.4f), new Vector3(-8, 0, 0), Vector3.One, head);   // spiral horn (approx)
        for (int r = 0; r < 3; r++) { var ring = M(new TorusMesh { InnerRadius = 0.05f - r * 0.012f, OuterRadius = 0.11f - r * 0.02f }, new Vector3(0, 0.35f + r * 0.22f, 0.42f + r * 0.03f), new Vector3(80, r * 40f, 0), Vector3.One, head); }
        M(new CylinderMesh { TopRadius = 0f, BottomRadius = 0.08f, Height = 0.24f, RadialSegments = 5 }, new Vector3(0.14f, 0.28f, 0.05f), new Vector3(-20, 0, 20), Vector3.One, head);   // ears
        M(new CylinderMesh { TopRadius = 0f, BottomRadius = 0.08f, Height = 0.24f, RadialSegments = 5 }, new Vector3(-0.14f, 0.28f, 0.05f), new Vector3(-20, 0, -20), Vector3.One, head);
        // legs
        foreach (var (lx, lz) in new[] { (-0.34f, 0.62f), (0.34f, 0.62f), (-0.34f, -0.72f), (0.34f, -0.72f) })
            _legs.Add(M(new CylinderMesh { TopRadius = 0.13f, BottomRadius = 0.07f, Height = 1.45f }, new Vector3(lx, 0.72f, lz), Vector3.Zero, Vector3.One));
        // flowing mane (down the neck) + tail (streaming back) — wisps that sway
        for (int i = 0; i < 6; i++) { var w = new Node3D(); _rig.AddChild(w); w.Position = new Vector3(0, 2.35f - i * 0.16f, 0.55f - i * 0.05f); M(new BoxMesh { Size = new Vector3(0.06f, 0.5f, 0.22f) }, new Vector3(0, -0.2f, 0), Vector3.Zero, Vector3.One, w); _mane.Add(w); }
        for (int i = 0; i < 6; i++) { var w = new Node3D(); _rig.AddChild(w); w.Position = new Vector3(0, 1.55f, -1.2f); M(new BoxMesh { Size = new Vector3(0.08f, 0.16f, 0.7f + i * 0.12f) }, new Vector3(0, -i * 0.12f, -0.35f), Vector3.Zero, Vector3.One, w); _tail.Add(w); }

        _light = new OmniLight3D { OmniRange = 11f, LightColor = Col, LightEnergy = 2.6f, Position = new Vector3(0, 1.9f, 0) };
        AddChild(_light);

        // sparkle wake — soft glowing motes trailing in world space
        _wake = new GpuParticles3D { Amount = 46, Lifetime = 1.0, LocalCoords = false, Position = new Vector3(0, 1.4f, -0.6f) };
        var pm = new ParticleProcessMaterial
        {
            Direction = new Vector3(0, 1, 0), Spread = 55f,
            InitialVelocityMin = 0.2f, InitialVelocityMax = 1.1f, Gravity = new Vector3(0, 0.6f, 0),
            ScaleMin = 0.04f, ScaleMax = 0.16f, Color = new Color(Col.R, Col.G, Col.B, 0.9f)
        };
        _wake.ProcessMaterial = pm;
        var puffMat = new StandardMaterial3D { AlbedoColor = new Color(Col.R, Col.G, Col.B, 0.85f), EmissionEnabled = true, Emission = Col, EmissionEnergyMultiplier = 3f, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, BlendMode = BaseMaterial3D.BlendModeEnum.Add, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled };
        _wake.DrawPass1 = new QuadMesh { Size = new Vector2(0.24f, 0.24f), Material = puffMat };
        AddChild(_wake);

        Game.I?.Sfx?.UnicornCall(GlobalPosition);
    }

    public void RecallTo(long peer) { if (_phase == 0) _target = peer; }   // T-recall: follow the warden who fired the flare
    public void SetRemoteState(Vector3 pos, bool charging)
    {
        // (TELEPORT) a big host jump is the arcane blink, not lag → snap the ghost + flash, don't slide it across the map
        if (_rhave && Game.I != null && new Vector2(pos.X - GlobalPosition.X, pos.Z - GlobalPosition.Z).Length() > 12f)
        { GlobalPosition = pos; TeleportFlash(pos); }
        _rtarget = pos; _rhave = true; _rCommitted = charging;
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta; _t += dt;
        bool committed = Remote ? _rCommitted : _phase >= 1;
        _rearing = !Remote && _phase == 1;
        // glow ramps as it commits; PEAKS during the telegraph windup so the "getting ready" reads as charging power
        float glowTarget = committed ? (_rearing ? 1.3f : 1f) : 0f;
        _chargeGlow = Mathf.MoveToward(_chargeGlow, glowTarget, dt * 2f);
        _mat?.SetShaderParameter("intensity", 1f + _chargeGlow * 1.6f);
        if (_light != null) _light.LightEnergy = 2.6f + _chargeGlow * 4f;

        if (Remote)   // client ghost: glide toward the host-synced position + face it; host drives all logic
        {
            if (_rhave && Game.I != null)
            {
                var to = _rtarget - GlobalPosition; to.Y = 0f; float d = to.Length();
                if (d > 0.05f) { GlobalPosition += to.Normalized() * Mathf.Min(d, (_rCommitted ? 24f : 13f) * dt); _gait += dt * (_rCommitted ? 24f : 12f); _rig.Rotation = new Vector3(0, Mathf.LerpAngle(_rig.Rotation.Y, Mathf.Atan2(to.X, to.Z), dt * 9f), 0); }
                GlobalPosition = new Vector3(GlobalPosition.X, Game.I.SurfaceHeight(GlobalPosition, GlobalPosition.Y), GlobalPosition.Z);
            }
            Animate(dt); return;
        }
        if (Game.I == null || !Game.I.IsAuthority) { Animate(dt); return; }

        var boss = Game.I.WorldBoss;
        bool bossLive = boss != null && GodotObject.IsInstanceValid(boss) && !boss.Dead;

        // boss awakens → ARCANE TELEPORT to a runway 35u out, then TELEGRAPH the sprint before charging
        if (_phase == 0 && bossLive) { TeleportForBoss(boss); _phase = 1; _windupT = WindupDur; }

        Vector3 goal;
        if (_phase >= 1 && bossLive)
        {
            goal = boss.GlobalPosition;
            if (_phase == 1)   // TELEGRAPH: hold the runway, rear + glow builds, aimed dead at the boss (~3s)
            {
                _windupT -= dt;
                if (_windupT <= 0f) { _phase = 2; Game.I.Sfx?.UnicornCharge(GlobalPosition); }   // GO
            }
            else               // CHARGE: full gallop straight into the boss, then detonate
            {
                var to = goal - GlobalPosition; to.Y = 0f;
                if (to.Length() <= 3.2f + boss.Radius) { Detonate(boss); return; }
                GlobalPosition += to.Normalized() * ChargeSpeed * dt;
                _gait += dt * 26f;
            }
        }
        else if (_phase >= 1)   // boss died before impact → stand down, resume following
        { _phase = 0; goal = FollowTargetPos(); }
        else
        {
            Vector3 tp = FollowTargetPos();
            var to = tp - GlobalPosition; to.Y = 0f; float d = to.Length();
            if (d > 3f) { GlobalPosition += to.Normalized() * Mathf.Min(11f, d) * dt; _gait += dt * 12f; }
            goal = tp;
        }
        var fd = goal - GlobalPosition; fd.Y = 0f;
        // snap-face the boss while telegraphing/charging (no lazy lerp — it should stare it down); lerp otherwise
        if (fd.LengthSquared() > 0.01f) _rig.Rotation = new Vector3(0, Mathf.LerpAngle(_rig.Rotation.Y, Mathf.Atan2(fd.X, fd.Z), dt * (_phase >= 1 ? 16f : 9f)), 0);
        GlobalPosition = new Vector3(GlobalPosition.X, Game.I.SurfaceHeight(GlobalPosition, GlobalPosition.Y), GlobalPosition.Z);
        Animate(dt);
    }

    // arcane-blink to a runway 35u from the boss, on the side toward the warden it was following, so the charge crosses view
    private void TeleportForBoss(Enemy boss)
    {
        var g = Game.I;
        Vector3 bp = boss.GlobalPosition;
        Vector3 dir = FollowTargetPos() - bp; dir.Y = 0f;
        if (dir.LengthSquared() < 1f) { dir = GlobalPosition - bp; dir.Y = 0f; }
        dir = dir.LengthSquared() > 0.01f ? dir.Normalized() : Vector3.Forward;
        Vector3 dest = g.ClampToWorld(bp + dir * 35f, 12f);
        TeleportFlash(GlobalPosition);                                  // vanish here
        GlobalPosition = new Vector3(dest.X, g.SurfaceHeight(dest, 1e9f), dest.Z);
        TeleportFlash(GlobalPosition);                                  // appear there
        var fd = bp - GlobalPosition; fd.Y = 0f;                        // face the boss at once
        if (fd.LengthSquared() > 0.01f) _rig.Rotation = new Vector3(0, Mathf.Atan2(fd.X, fd.Z), 0);
        g.Sfx?.UnicornCall(GlobalPosition);                            // teleport shimmer
        g.Sfx?.UnicornCharge(GlobalPosition);                          // + the winding-up whinny
    }

    private void TeleportFlash(Vector3 at)
    {
        var g = Game.I; if (g == null) return;
        var up = at + Vector3.Up * 1.4f;
        g.VfxRing(up, Col, 4.5f, 0.55f);
        g.VfxRing(up, Col.Lerp(Colors.White, 0.6f), 2.2f, 0.4f);
        g.SpawnPoof(at + Vector3.Up * 0.8f, net: false);
    }

    private bool _rearing = false;
    private void Animate(float dt)
    {
        if (_rig == null) return;
        if (_rearing)   // (TELEGRAPH) rear up on the hind legs, pawing the air, glowing — a menacing wind-up before the sprint
        {
            float rr = 0.5f + 0.5f * Mathf.Sin(_t * 5f);   // paw/bounce
            _rig.Position = new Vector3(0, 0.15f + 0.12f * rr, 0);
            _rig.RotationDegrees = new Vector3(28f + 8f * rr, _rig.RotationDegrees.Y, 0);   // pitched back
            _legs[0].RotationDegrees = new Vector3(-70f - 25f * rr, 0, 0);   // front legs up + pawing
            _legs[1].RotationDegrees = new Vector3(-58f - 30f * (1f - rr), 0, 0);
            _legs[2].RotationDegrees = new Vector3(10f, 0, 0);               // hind legs planted
            _legs[3].RotationDegrees = new Vector3(10f, 0, 0);
            for (int i = 0; i < _mane.Count; i++) _mane[i].RotationDegrees = new Vector3(30f + 22f * Mathf.Sin(_t * 8f - i * 0.6f), 0, 8f * Mathf.Sin(_t * 4f - i));
            for (int i = 0; i < _tail.Count; i++) _tail[i].RotationDegrees = new Vector3(14f * Mathf.Sin(_t * 5f - i * 0.5f), 10f * Mathf.Sin(_t * 4.3f - i * 0.4f), 0);
            return;
        }
        float bob = 0.14f * Mathf.Sin(_gait * 0.5f) + 0.04f * Mathf.Sin(_t * 3f);
        _rig.Position = new Vector3(0, bob, 0);
        float lean = _chargeGlow * -10f;   // lean into the charge
        _rig.RotationDegrees = new Vector3(lean, _rig.RotationDegrees.Y, 0);
        for (int i = 0; i < _legs.Count; i++)   // gallop: diagonal pairs swing opposite
        {
            float ph = _gait + (i % 2 == 0 ? 0f : Mathf.Pi) + (i < 2 ? 0f : 0.6f);
            _legs[i].RotationDegrees = new Vector3(28f * Mathf.Sin(ph), 0, 0);
        }
        for (int i = 0; i < _mane.Count; i++) _mane[i].RotationDegrees = new Vector3(18f + 12f * Mathf.Sin(_t * 5f - i * 0.6f) + _chargeGlow * 20f, 0, 6f * Mathf.Sin(_t * 3f - i));
        for (int i = 0; i < _tail.Count; i++) _tail[i].RotationDegrees = new Vector3(10f * Mathf.Sin(_t * 4f - i * 0.5f), 8f * Mathf.Sin(_t * 3.3f - i * 0.4f), 0);
    }

    private Vector3 FollowTargetPos()
    {
        var g = Game.I; var self = GlobalPosition;
        // proximity CLAIM: a warden who walks within 3.5u takes ownership (so "walk up to it" retargets, alongside T-recall)
        long claim = 0; float cd = 3.5f;
        if (g.Player != null) { float d = self.DistanceTo(g.Player.GlobalPosition); if (d < cd) { cd = d; claim = g.LocalPeer; } }
        if (g.NetMgr != null && g.NetMgr.Active) foreach (var (peer, pos) in g.NetMgr.AllyPeerPositions()) { float d = self.DistanceTo(pos); if (d < cd) { cd = d; claim = peer; } }
        if (claim != 0) _target = claim;
        // resolve the current target's position
        if (_target == g.LocalPeer && g.Player != null) return g.Player.GlobalPosition;
        if (g.NetMgr != null && g.NetMgr.Active) { var p = g.NetMgr.PeerPosition(_target); if (p != Vector3.Zero) return p; }
        return g.Player != null ? g.Player.GlobalPosition : self;
    }

    private void Detonate(Enemy boss)
    {
        if (_done) return; _done = true;
        var g = Game.I; var at = boss.GlobalPosition;
        boss.Hurt(boss.MaxHp * 0.20f, DamageType.Arcane, true);   // 20% of CURRENT max (phase-aware for a future stage 2)
        foreach (var e in g.Enemies.ToArray())
            if (e != null && e != boss && !e.Dead && GodotObject.IsInstanceValid(e) && !e.IsBoss && e.GlobalPosition.DistanceTo(at) < 32f)
                e.Hurt(e.MaxHp + 9999f, DamageType.Arcane, true);
        var nuke = new ArcaneNuke(); g.AddChild(nuke); nuke.GlobalPosition = at; nuke.Init(boss.Radius);
        g.NetMgr?.BroadcastUnicornGone(at, boss.Radius);   // clients: free the ghost + bloom their own cloud
        QueueFree();
    }
}
