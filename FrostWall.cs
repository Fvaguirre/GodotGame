using Godot;
using System.Collections.Generic;

// FrostWall.cs — a persistent barrier of ice raised by the Frost Wall charged modifier. It BLOCKS enemies
// (registers a row of obstacle circles in Game.WallBlockers, which Enemy.AvoidBlockers steers around),
// stands for a few seconds (longer at higher rarity), then SHATTERS — bursting for area frost damage and a
// slow. Casting past your live-wall limit shatters your oldest wall early (see Player.SpawnFrostWallMod).
//
// Authoritative copy (Remote=false) lives on the caster: it owns the timeout, deals the shatter damage
// (routed to the host via Enemy.Hurt), and broadcasts spawn/shatter so allies get a remote copy. Remote
// copies are visual + obstacle only (so the HOST's enemy simulation avoids client-cast walls) and self-expire.
public partial class FrostWall : Node3D
{
    public bool Remote = false;
    public Vector3 Center;
    public Vector3 Along = Vector3.Right;   // the wall's long axis
    public float HalfLen = 3.6f;
    public float Dur = 5f;
    public float ShatterDmg = 0f;
    public float ShatterRad = 6f;
    public int Id = 0;
    public int Chill = 0;   // (OVERHAUL) Evo A Frostbite Wall: periodically freezes foes hugging the wall
    public int Pulse = 0;   // (OVERHAUL) Evo B Glacier: emits periodic frost damage pulses while it stands
    private Player _owner;

    private float _t;
    private float _chillT = 0f, _pulseT = 0f;
    private bool _shattered = false;
    private readonly List<Blocker> _circles = new();
    private Node3D _riser;

    // remote copies register here so a networked shatter can find the right one by position
    internal static readonly List<FrostWall> Remotes = new();

    public void Init(Player owner, Vector3 center, Vector3 along, float halfLen, float dur, float shatterDmg, float shatterRad, int id, bool remote)
    {
        _owner = owner; Center = center; HalfLen = halfLen; Dur = dur; ShatterDmg = shatterDmg; ShatterRad = shatterRad; Id = id; Remote = remote;
        along.Y = 0f; Along = along.LengthSquared() > 0.001f ? along.Normalized() : Vector3.Right;
        _t = dur;
        GlobalPosition = center;   // identity rotation — children placed with world-space Along offsets
        Build();
        RegisterObstacle();
        if (Remote) Remotes.Add(this);
    }

    private static readonly Color IceCol = new Color(0.60f, 0.85f, 1.0f);

    private void Build()
    {
        _riser = new Node3D { Position = new Vector3(0, -3.6f, 0) };   // starts buried, heaves up
        AddChild(_riser);

        // a translucent ice slab body, long axis along the wall
        float len = HalfLen * 2f;
        var slab = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.5f, 2.4f, len) }, MaterialOverride = Game.I.IceWallMat(), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        slab.Position = new Vector3(0, 1.2f, 0);
        slab.RotationDegrees = new Vector3(0, Mathf.RadToDeg(Mathf.Atan2(Along.X, Along.Z)), 0);   // local +Z → Along
        _riser.AddChild(slab);

        // a jagged crest of ice crystals along the length
        int crystals = 7;
        for (int i = 0; i < crystals; i++)
        {
            float f = crystals == 1 ? 0f : (i / (float)(crystals - 1) * 2f - 1f);   // -1..1
            float h = 2.6f + (float)GD.RandRange(0.0, 1.8);
            var perp = new Vector3(-Along.Z, 0, Along.X) * (float)GD.RandRange(-0.18, 0.18);
            var sh = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.42f + (float)GD.RandRange(0.0, 0.25), Height = h, RadialSegments = 5 }, MaterialOverride = Game.I.IceWallMat(), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            sh.Position = Along * (f * HalfLen) + perp + new Vector3(0, h * 0.5f, 0);
            sh.RotationDegrees = new Vector3((float)GD.RandRange(-12.0, 12.0), (float)GD.RandRange(0.0, 360.0), (float)GD.RandRange(-12.0, 12.0));
            _riser.AddChild(sh);
        }
        // a couple of bright inner shards so it reads as glinting ice
        for (int i = 0; i < 3; i++)
        {
            float f = (float)GD.RandRange(-0.7, 0.7);
            var core = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.2f, Height = 2.2f, RadialSegments = 4 }, MaterialOverride = Game.ToonEmissive(IceCol.Lerp(Colors.White, 0.6f), 1.8f, 0.05f), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            core.Position = Along * (f * HalfLen) + new Vector3(0, 1.3f, 0);
            _riser.AddChild(core);
        }
        _riser.AddChild(new OmniLight3D { OmniRange = HalfLen * 2.2f, LightColor = IceCol, LightEnergy = 1.6f, ShadowEnabled = false, Position = new Vector3(0, 1.6f, 0) });

        // heave up out of the ground with a slight overshoot
        var tw = _riser.CreateTween();
        tw.TweenProperty(_riser, "position", Vector3.Zero, 0.28f).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
        Game.I.SpawnFrostShatter(Center + Vector3.Up * 0.3f, HalfLen * 0.5f);   // a puff of frost as it erupts
    }

    private void RegisterObstacle()
    {
        // approximate the wall with a row of overlapping circles so the circle-based enemy steering routes around it
        for (int i = -2; i <= 2; i++)
        {
            var c = new Blocker { Pos = Center + Along * (i * HalfLen * 0.5f), Radius = 1.3f };
            _circles.Add(c);
            Game.I.WallBlockers.Add(c);
        }
    }

    private void UnregisterObstacle()
    {
        if (Game.I != null) foreach (var c in _circles) Game.I.WallBlockers.Remove(c);
        _circles.Clear();
    }

    public override void _Process(double delta)
    {
        if (Game.I == null || _shattered) return;
        float dt = (float)delta;
        _t -= dt;
        if (!Remote && (Chill > 0 || Pulse > 0))   // authoritative copy runs the aura procs
        {
            if (Chill > 0)
            {
                _chillT -= dt;
                if (_chillT <= 0f)
                {
                    _chillT = 0.5f;
                    foreach (var e in Game.I.Enemies.ToArray())
                        if (e != null && !e.Dead && GodotObject.IsInstanceValid(e) && Flat(e, Center) < ShatterRad + e.Radius)
                            e.AddFreeze(0.3f * Chill, _owner != null ? _owner.FreezeThreshMul : 1f, _owner != null ? _owner.FrostDurBonus : 0f);
                }
            }
            if (Pulse > 0)
            {
                _pulseT -= dt;
                if (_pulseT <= 0f)
                {
                    _pulseT = Mathf.Max(0.4f, 1.2f - 0.15f * Pulse);
                    foreach (var e in Game.I.Enemies.ToArray())
                        if (e != null && !e.Dead && GodotObject.IsInstanceValid(e) && Flat(e, Center) < ShatterRad + e.Radius)
                            e.Hurt(ShatterDmg * (0.12f + 0.04f * Pulse), DamageType.Frost, true);
                    Game.I.SpawnFrostShatter(Center + Vector3.Up * 0.5f, ShatterRad * 0.6f);
                }
            }
        }
        if (_t <= 0f) Shatter(true);
    }

    // Break the wall: clear its obstacle, burst frost VFX/SFX, and (authoritative copy only) damage nearby foes.
    public void Shatter(bool dealDamage)
    {
        if (_shattered) return;
        _shattered = true;
        UnregisterObstacle();
        if (Game.I == null) { QueueFree(); return; }

        Game.I.SpawnFrostShatter(Center + Vector3.Up * 1.0f, HalfLen);
        Game.I.Sfx?.IceShatter(Center);

        if (!Remote && dealDamage)
        {
            foreach (var e in Game.I.Enemies.ToArray())
            {
                if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
                if (Flat(e, Center) > ShatterRad + e.Radius) continue;
                e.Hurt(ShatterDmg, DamageType.Frost, true);
                e.Slow(2.0f, 0.55f);
            }
            Game.I.DamageWorld(Center, ShatterRad, ShatterDmg);
            Game.I.NetMgr?.BroadcastVfx(82, Center, Vector3.Zero, ShatterRad, 0f, IceCol);   // allies shatter their remote copy
        }

        _owner?.OnFrostWallGone(this);
        QueueFree();
    }

    private static float Flat(Enemy e, Vector3 p) { var d = e.GlobalPosition - p; d.Y = 0f; return d.Length(); }

    // network: shatter the remote wall nearest a broadcast position (visual only)
    public static void ShatterNearestRemote(Vector3 pos)
    {
        FrostWall best = null; float bestD = 8f * 8f;
        foreach (var w in Remotes)
        {
            if (w == null || !GodotObject.IsInstanceValid(w) || w._shattered) continue;
            float d = w.Center.DistanceSquaredTo(pos);
            if (d < bestD) { bestD = d; best = w; }
        }
        best?.Shatter(false);
    }

    public override void _ExitTree()
    {
        if (!_shattered) UnregisterObstacle();   // safety: never leave a stale obstacle if freed without shattering
        Remotes.Remove(this);
    }
}
