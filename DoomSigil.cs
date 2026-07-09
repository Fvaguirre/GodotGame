using Godot;

// DoomSigil.cs — the Doom Sigil finisher's deferred blast. A cursed pentacle brands the ground; it pulses/spins for a short
// fuse, then DETONATES for Curse damage across its radius (the caster pre-branded the foes, so the blast is scaled by count).
// Host/solo owns the damage (routes for clients); allies spawn a Remote ghost (visual-only) via VFX kind 65. Modeled on Moonshard.
public partial class DoomSigil : Node3D
{
    public bool Remote = false;
    private float _fuse = 1.35f, _linger = 0f, _age = 0f;
    private bool _blown = false;
    private float _radius, _dmg;
    private Color _col;
    private Player _src;
    private Node3D _rig;
    private OmniLight3D _light;

    public void Init(Vector3 pos, float radius, float dmg, Color col, Player src)
    { GlobalPosition = pos; _radius = radius; _dmg = dmg; _col = col; _src = src; Build(); }
    public void InitRemote(Vector3 pos, float radius, Color col)
    { Remote = true; GlobalPosition = pos; _radius = radius; _dmg = 0f; _col = col; Build(); }

    private void Build()
    {
        _rig = new Node3D(); AddChild(_rig);
        var m = Game.Emissive(_col, 2.6f);
        m.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; m.AlbedoColor = new Color(_col.R, _col.G, _col.B, 0.72f);
        // outer ring
        var ring = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = _radius * 0.86f, OuterRadius = _radius }, MaterialOverride = m };
        ring.RotationDegrees = new Vector3(90, 0, 0); ring.Position = new Vector3(0, 0.06f, 0); _rig.AddChild(ring);
        // inner ring
        var inner = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = _radius * 0.5f, OuterRadius = _radius * 0.58f }, MaterialOverride = m };
        inner.RotationDegrees = new Vector3(90, 0, 0); inner.Position = new Vector3(0, 0.05f, 0); _rig.AddChild(inner);
        // five rune spikes (a pentacle feel)
        for (int k = 0; k < 5; k++)
        {
            float a = k / 5f * Mathf.Tau;
            var spike = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.14f, Height = 0.8f, RadialSegments = 4 }, MaterialOverride = Game.Emissive(_col.Lerp(Colors.White, 0.3f), 3f) };
            spike.Position = new Vector3(Mathf.Sin(a) * _radius * 0.7f, 0.4f, Mathf.Cos(a) * _radius * 0.7f);
            _rig.AddChild(spike);
        }
        _light = new OmniLight3D { OmniRange = _radius * 2.2f, LightColor = _col, LightEnergy = 0.5f, Position = new Vector3(0, 1.2f, 0) };
        AddChild(_light);
    }

    public override void _Process(double delta)
    {
        if (Game.I == null || Game.I.State != GameState.Playing) return;
        float dt = (float)delta; _age += dt;
        if (!_blown)
        {
            _fuse -= dt;
            float p = 1f - Mathf.Clamp(_fuse / 1.35f, 0f, 1f);   // 0 → 1 over the fuse
            if (_rig != null) { _rig.Scale = Vector3.One * (0.55f + 0.5f * p + 0.05f * Mathf.Sin(_age * 26f)); _rig.RotateY(dt * (1.5f + 4f * p)); }
            if (_light != null) _light.LightEnergy = 0.5f + p * 2f;
            if (_fuse <= 0f) Detonate();
        }
        else { _linger -= dt; if (_linger <= 0f) QueueFree(); }
    }

    private void Detonate()
    {
        _blown = true; _linger = 0.55f;
        if (_rig != null) { _rig.QueueFree(); _rig = null; }
        if (!Remote && _src != null && GodotObject.IsInstanceValid(_src))
        {
            foreach (var e in Game.I.Enemies.ToArray())
                if (e != null && !e.Dead && GodotObject.IsInstanceValid(e) &&
                    new Vector2(e.GlobalPosition.X - GlobalPosition.X, e.GlobalPosition.Z - GlobalPosition.Z).Length() < _radius + e.Radius &&
                    !Game.I.SightBlocked(GlobalPosition, e.GlobalPosition))
                    e.Hurt(_dmg, DamageType.Curse, true);
            Game.I.DamageWorld(GlobalPosition, _radius, _dmg);
        }
        // boom: shockwave ring + rising doom pillars + light flash
        Game.I.VfxRing(GlobalPosition, _col, _radius * 1.5f, 0.5f);
        for (int k = 0; k < 8; k++)
        {
            float a = k / 8f * Mathf.Tau, r = _radius * (0.3f + GD.Randf() * 0.7f);
            var pil = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.05f, BottomRadius = 0.22f, Height = 2.2f }, MaterialOverride = Game.Emissive(_col, 2.4f) };
            AddChild(pil); pil.Position = new Vector3(Mathf.Sin(a) * r, 0.2f, Mathf.Cos(a) * r);
            var tw = pil.CreateTween(); tw.SetParallel(true);
            tw.TweenProperty(pil, "position:y", 2.6f, 0.5f).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
            tw.TweenProperty(pil, "scale", new Vector3(0.1f, 1.2f, 0.1f), 0.5f);
        }
        if (_light != null) { _light.LightEnergy = 3.5f; _light.OmniRange = _radius * 3f; }
        Game.I.Sfx?.CurseCrush(GlobalPosition);
    }
}
