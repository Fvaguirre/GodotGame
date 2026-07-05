using Godot;

// Frost witch ult: a large circle of ground that instantly ices over — every foe inside is frozen on cast, and
// any foe that walks in during its active window is frozen too. Host applies the freezes; allies see the ice disc.
public partial class DeepFreeze : Node3D
{
    private Player _caster;
    private float _radius, _dur, _life = 0f, _tickT = 0f;
    private bool _remote, _shatterEnd;
    private readonly System.Collections.Generic.HashSet<Enemy> _frozenByMe = new();   // (NEW) enemies THIS cast already froze — freeze each at most once

    public void Init(Player caster, Vector3 pos, float radius, float dur, bool remote, bool shatterOnEnd = false)
    {
        _caster = caster; _radius = radius; _dur = dur; _remote = remote; _shatterEnd = shatterOnEnd;
        GlobalPosition = pos;
        var col = new Color(0.6f, 0.85f, 1f);
        var disc = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = radius, BottomRadius = radius, Height = 0.16f, RadialSegments = 44 } };
        var m = Game.ToonEmissive(col, 1.4f, 0f); m.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; m.AlbedoColor = new Color(col.R, col.G, col.B, 0.4f);
        disc.MaterialOverride = m; disc.Position = new Vector3(0, 0.09f, 0); AddChild(disc);
        // jagged ice spikes around the rim
        for (int i = 0; i < 18; i++)
        {
            float a = i / 18f * Mathf.Tau;
            var spike = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.4f, Height = 1.4f + GD.Randf(), RadialSegments = 5 }, MaterialOverride = Game.Emissive(col, 2f) };
            spike.Position = new Vector3(Mathf.Cos(a) * radius * 0.95f, 0.6f, Mathf.Sin(a) * radius * 0.95f);
            spike.RotationDegrees = new Vector3(GD.Randf() * 20 - 10, 0, GD.Randf() * 20 - 10);
            AddChild(spike);
        }
        AddChild(new OmniLight3D { OmniRange = radius * 1.3f, LightColor = col, LightEnergy = 1.8f, ShadowEnabled = false, Position = new Vector3(0, 2f, 0) });
        Game.I.SpawnPollen(pos + Vector3.Up, radius, col, 22, 1f, net: false);
        Game.I.Sfx?.Freeze(pos, false);
        if (!_remote) FreezeInside();
    }

    private void FreezeInside()
    {
        if (Game.I == null) return;
        foreach (var e in Game.I.Enemies.ToArray())
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e) || e.Remote || e.Frozen) continue;
            if (_frozenByMe.Contains(e)) continue;   // this ult already froze it once — don't re-lock a survivor
            var d = e.GlobalPosition - GlobalPosition; d.Y = 0f;
            if (d.Length() < _radius + e.Radius)
            {
                e.AddFreeze(e.FreezeThreshold, _caster != null ? _caster.FreezeThreshMul : 1f, _caster != null ? _caster.FrostDurBonus : 0f);
                _frozenByMe.Add(e);   // freeze can still come from the beam/other witches/other shatters — just not from THIS ult again
            }
        }
    }

    public override void _Process(double delta)
    {
        var g = Game.I; if (g == null || g.State != GameState.Playing) return;
        float dt = (float)delta; _life += dt;
        if (!_remote) { _tickT -= dt; if (_tickT <= 0f) { _tickT = 0.3f; FreezeInside(); } }
        if (_life >= _dur)
        {
            if (!_remote && _shatterEnd)   // Absolute Zero: mass-shatter everything frozen inside (chains via ShatterCascade if owned)
                foreach (var e in g.Enemies.ToArray())
                {
                    if (e == null || e.Dead || !GodotObject.IsInstanceValid(e) || e.Remote || !e.Frozen) continue;
                    var d = e.GlobalPosition - GlobalPosition; d.Y = 0f;
                    if (d.Length() < _radius + e.Radius) e.ShatterInstant();
                }
            var tw = CreateTween(); tw.TweenProperty(this, "scale", new Vector3(1f, 0.01f, 1f), 0.5f);
            tw.TweenCallback(Callable.From(QueueFree));
        }
    }
}
