using Godot;

// FireWall.cs — the Ring of Fire finisher. A ring of LIVE FLAMES: emissive flame tongues continuously spawn all around the
// perimeter, licking upward, flickering, and fading (not a smooth cylinder). It burns foes standing in the ring band
// (owner-authoritative); the incoming-projectile eating is host-side via Game.FireRings. Allies render a Remote visual copy
// (VFX kind 72) — the flames are spawned locally on every machine, so the ring looks alive for everyone.
public partial class FireWall : Node3D
{
    public Vector3 Center;
    public float Radius = 5f, Dur = 4f, Dps = 5f, BurnPer = 4f, BurnBomb = 30f;
    public int OwnerPeer = 0;
    public bool Remote = false;

    private float _t = 0f, _dmgTick = 0f, _flameT = 0f;
    private MeshInstance3D _base;
    private Color _col;

    public override void _Ready()
    {
        _col = DamageTypes.Col(DamageType.Ember);
        // a low, glowing molten ring at the base to ground the fire (a flat emissive torus)
        _base = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = Radius - 0.6f, OuterRadius = Radius + 0.6f, Rings = 24, RingSegments = 40 } };
        var bm = Game.Emissive(new Color(1f, 0.4f, 0.1f), 2.4f);
        bm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; bm.AlbedoColor = new Color(1f, 0.4f, 0.1f, 0.7f);
        _base.MaterialOverride = bm; _base.Position = new Vector3(0, 0.12f, 0); AddChild(_base);
        AddChild(new OmniLight3D { OmniRange = Radius * 1.8f, LightColor = _col, LightEnergy = 2.6f, Position = new Vector3(0, 2.2f, 0) });
    }

    // shared painterly flame-tongue material — a cone with hot base (-Y) → wispy tip (flame.gdshader). One instance for
    // every tongue on every wall (no per-flame allocation like the old Game.Emissive path).
    private static ShaderMaterial _tongueMat;
    private static ShaderMaterial TongueMat()
    {
        if (_tongueMat != null) return _tongueMat;
        var m = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/flame.gdshader") };
        m.SetShaderParameter("hot_color", new Color(1f, 0.9f, 0.5f));
        m.SetShaderParameter("mid_color", new Color(1f, 0.5f, 0.14f));
        m.SetShaderParameter("cool_color", new Color(0.7f, 0.12f, 0.03f));
        m.SetShaderParameter("flame_axis", new Vector3(0f, -1f, 0f));   // hot at the base, wispy licking tip up top
        m.SetShaderParameter("half_len", 1.0f);                          // cone Height = 2.0 → half-length 1.0
        _tongueMat = m; return m;
    }

    // one rising flame tongue at angle a on the ring — an authored CONE flame silhouette, not a round puff
    private void SpawnFlame(float a)
    {
        float rr = Radius + (GD.Randf() - 0.5f) * 1.0f;
        float s = 0.35f + GD.Randf() * 0.5f;
        var m = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.5f, Height = 2.0f, RadialSegments = 6 },   // unit cone (point up); node scale sizes it
            MaterialOverride = TongueMat(),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Scale = Vector3.One * s
        };
        AddChild(m);
        var start = new Vector3(Mathf.Cos(a) * rr, 0.1f + s, Mathf.Sin(a) * rr);   // cone base near the ground
        m.Position = start;
        float h = 2.5f + GD.Randf() * 6.0f;   // licks up toward the ~8u wall height
        var end = start + new Vector3((GD.Randf() - 0.5f) * 0.6f, h, (GD.Randf() - 0.5f) * 0.6f);
        var tw = m.CreateTween(); tw.SetParallel(true);
        tw.TweenProperty(m, "position", end, 0.55f).SetEase(Tween.EaseType.Out);
        tw.TweenProperty(m, "scale", Vector3.One * (s * 0.18f), 0.55f);   // taper as it rises (flame tip)
        tw.TweenProperty(m, "transparency", 1f, 0.55f);                    // additive flame fades out (node transparency)
        tw.Chain().TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(m)) m.QueueFree(); }));
    }

    public override void _Process(double delta)
    {
        var g = Game.I; if (g == null || !g.SimActive) return;
        float dt = (float)delta; _t += dt;
        float fade = Mathf.Clamp(Dur - _t, 0f, 1f);
        if (_base != null && _base.MaterialOverride is StandardMaterial3D bm) bm.AlbedoColor = new Color(1f, 0.4f, 0.1f, 0.7f * (0.4f + 0.6f * fade));

        // continuously spawn flame tongues around the ring so it reads as living fire
        _flameT -= dt;
        if (_flameT <= 0f && fade > 0.05f)
        {
            _flameT = 0.05f;
            int burst = Mathf.Max(3, (int)(6 * g.ParticleScale));
            for (int i = 0; i < burst; i++) SpawnFlame(GD.Randf() * Mathf.Tau);
        }

        if (!Remote)
        {
            _dmgTick += dt;
            if (_dmgTick >= 0.4f)
            {
                _dmgTick = 0f;
                foreach (var e in g.Enemies.ToArray())
                {
                    if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
                    float d = new Vector2(e.GlobalPosition.X - Center.X, e.GlobalPosition.Z - Center.Z).Length();
                    if (d > Radius - 1.5f && d < Radius + 1.5f)   // the burning ring band
                    {
                        e.Hurt(Dps * 0.4f, DamageType.Ember, false);
                        e.AddBurn(0.5f, BurnPer, BurnBomb, 0f, OwnerPeer);
                    }
                }
            }
        }
        if (_t >= Dur) QueueFree();
    }
}
