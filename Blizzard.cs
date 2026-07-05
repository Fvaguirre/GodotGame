using Godot;

// Frost witch ult: a huge swirling storm that grinds every foe inside it, drops shattering icicles, and has a
// (upgradeable) chance to freeze foes caught in it. Host applies damage/freeze; a visual-only ghost runs on allies.
public partial class Blizzard : Node3D
{
    private Player _caster;
    private float _radius, _dur, _dps, _freezeChance, _life = 0f, _tickT = 0f, _iceT = 0f;
    private bool _remote;
    private Node3D _swirl;

    public void Init(Player caster, Vector3 pos, float radius, float dur, float dps, float freezeChance, bool remote)
    {
        _caster = caster; _radius = radius; _dur = dur; _dps = dps; _freezeChance = freezeChance; _remote = remote;
        GlobalPosition = pos;
        var col = new Color(0.72f, 0.9f, 1f);
        var ring = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = radius, BottomRadius = radius, Height = 0.1f, RadialSegments = 40 } };
        var rm = Game.ToonEmissive(col, 1.2f, 0f); rm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; rm.AlbedoColor = new Color(col.R, col.G, col.B, 0.16f);
        ring.MaterialOverride = rm; ring.Position = new Vector3(0, 0.06f, 0); AddChild(ring);
        _swirl = new Node3D(); AddChild(_swirl);
        for (int i = 0; i < 26; i++)
        {
            float a = i / 26f * Mathf.Tau, rr = radius * (0.25f + GD.Randf() * 0.75f);
            var flake = new MeshInstance3D { Mesh = new BoxMesh { Size = Vector3.One * 0.22f }, MaterialOverride = Game.Emissive(col, 1.6f) };
            flake.Position = new Vector3(Mathf.Cos(a) * rr, 1f + GD.Randf() * 4.5f, Mathf.Sin(a) * rr);
            _swirl.AddChild(flake);
        }
        AddChild(new OmniLight3D { OmniRange = radius * 1.2f, LightColor = col, LightEnergy = 1.6f, ShadowEnabled = false, Position = new Vector3(0, 3f, 0) });
    }

    public override void _Process(double delta)
    {
        var g = Game.I; if (g == null || g.State != GameState.Playing) return;
        float dt = (float)delta; _life += dt;
        if (_swirl != null) _swirl.RotationDegrees = new Vector3(0, _life * 45f, 0);
        _iceT -= dt; if (_iceT <= 0f) { _iceT = 0.12f; DropIcicle(); }
        if (!_remote)
        {
            _tickT -= dt;
            if (_tickT <= 0f)
            {
                _tickT = 0.25f;
                foreach (var e in g.Enemies.ToArray())
                {
                    if (e == null || e.Dead || !GodotObject.IsInstanceValid(e) || e.Remote) continue;
                    var d = e.GlobalPosition - GlobalPosition; d.Y = 0f;
                    if (d.Length() < _radius + e.Radius)
                    {
                        e.Hurt(_dps * 0.25f, DamageType.Frost);
                        if (!e.Frozen && GD.Randf() < _freezeChance * 0.25f) e.AddFreeze(e.FreezeThreshold, _caster != null ? _caster.FreezeThreshMul : 1f, _caster != null ? _caster.FrostDurBonus : 0f);
                    }
                }
            }
        }
        if (_life >= _dur) QueueFree();
    }

    private void DropIcicle()
    {
        var col = new Color(0.72f, 0.9f, 1f);
        float a = GD.Randf() * Mathf.Tau, rr = GD.Randf() * _radius;
        var pos = GlobalPosition + new Vector3(Mathf.Cos(a) * rr, 6.5f, Mathf.Sin(a) * rr);
        var ic = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.2f, Height = 1.2f, RadialSegments = 5 }, MaterialOverride = Game.Emissive(col, 2f) };
        ic.RotationDegrees = new Vector3(180, 0, 0); AddChild(ic); ic.GlobalPosition = pos;
        float gy = Game.I.SurfaceHeight(pos, pos.Y);
        var tw = ic.CreateTween();
        tw.TweenProperty(ic, "global_position", new Vector3(pos.X, gy + 0.3f, pos.Z), 0.38f).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
        tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(ic)) { Game.I.SpawnPollen(ic.GlobalPosition, 1f, col, 4, 0.4f, net: false); ic.QueueFree(); } }));
    }
}
