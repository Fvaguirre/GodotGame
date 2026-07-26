using Godot;

// A circular holy area that PULSES on a beat: each pulse sears enemies in radius and mends allies.
// Used by the Judgement "one huge lance" modifier.
// HolyPulse.cs — a one-shot holy nova pulse (heal allies / smite foes). Used by Divine finishers/ults.
public partial class HolyPulse : Node3D
{
    public float Radius = 9f;
    public float Dur = 5f, MaxDur = 5f;
    public float PulseDmg = 12f;       // per pulse
    public float PulseHeal = 8f;       // per pulse to allies/self
    public float Interval = 0.8f;

    private MeshInstance3D _disc;
    private StandardMaterial3D _mat;
    private float _beat = 0f;

    public override void _Ready()
    {
        var col = DamageTypes.Col(DamageType.Holy);
        _disc = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = Radius, BottomRadius = Radius, Height = 0.1f } };
        _mat = new StandardMaterial3D
        {
            AlbedoColor = new Color(col.R, col.G, col.B, 0.22f),
            EmissionEnabled = true, Emission = col, EmissionEnergyMultiplier = 1.0f,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
        };
        _disc.MaterialOverride = _mat;
        _disc.Position = new Vector3(0, 0.06f, 0);
        AddChild(_disc);
        AddChild(new OmniLight3D { OmniRange = Radius * 1.4f, LightColor = col, LightEnergy = 1.6f });
        SpawnPulseRing();   // (PHASE 3) a ring on the opening cast
    }

    // (PHASE 3) an expanding holy shockwave ring — spawned on each damage/heal beat so the pulse is READABLE (authored
    // silhouette that grows to the full radius and fades), instead of an invisible beat under a flat glowing disc.
    private void SpawnPulseRing()
    {
        var col = DamageTypes.Col(DamageType.Holy).Lerp(Colors.White, 0.45f);
        var ring = new MeshInstance3D
        {
            Mesh = new TorusMesh { InnerRadius = 0.86f, OuterRadius = 1.0f },
            MaterialOverride = Game.Emissive(col, 2.2f),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Position = new Vector3(0, 0.09f, 0),
            Scale = Vector3.One * (Radius * 0.16f)
        };
        AddChild(ring);
        var t = ring.CreateTween();
        t.TweenProperty(ring, "scale", Vector3.One * Radius, 0.6f).SetEase(Tween.EaseType.Out);
        t.Parallel().TweenProperty(ring, "transparency", 1f, 0.62f);
        t.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(ring)) ring.QueueFree(); }));
    }

    public override void _Process(double delta)
    {
        var g = Game.I;
        if (g == null || !g.SimActive) return;
        float dt = (float)delta;
        Dur -= dt;
        _beat -= dt;

        if (_mat != null)
        {
            float f = Mathf.Clamp(Dur / Mathf.Max(0.01f, MaxDur), 0f, 1f);
            float throb = 0.7f + 0.5f * Mathf.Sin((MaxDur - Dur) * 6f);
            _mat.EmissionEnergyMultiplier = (0.6f + 0.8f * f) * throb;
            _mat.AlbedoColor = new Color(_mat.AlbedoColor.R, _mat.AlbedoColor.G, _mat.AlbedoColor.B, (0.10f + 0.18f * f));
        }

        if (_beat <= 0f)
        {
            _beat = Interval;
            SpawnPulseRing();   // (PHASE 3) visible shockwave on every beat
            foreach (var e in g.Enemies.ToArray())
            {
                if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
                var off = new Vector2(e.GlobalPosition.X - GlobalPosition.X, e.GlobalPosition.Z - GlobalPosition.Z);
                if (off.Length() < Radius + e.Radius) e.Hurt(PulseDmg, DamageType.Holy, true);
            }
            var p = g.Player;
            if (p != null)
            {
                var off = new Vector2(p.GlobalPosition.X - GlobalPosition.X, p.GlobalPosition.Z - GlobalPosition.Z);
                if (off.Length() < Radius) p.Heal(PulseHeal);
            }
            g.NetMgr?.HealAlliesNear(GlobalPosition, Radius, PulseHeal);
        }

        if (Dur <= 0f) QueueFree();
    }
}
