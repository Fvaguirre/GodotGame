using Godot;

// CrocBomb.cs — the crocodile-person's lobbed grenade. Arcs from the croc to the target's feet (~0.8s), LANDS with a telegraph
// ring, then blasts after a ~2s fuse (AoE damage to players). Host/solo owns the damage; allies spawn a Remote visual-only
// ghost (VFX kind 76). Modeled on Moonshard/EmberMeteor but it's an ENEMY attack (hurts players).
public partial class CrocBomb : Node3D
{
    public bool Remote = false;
    private Vector3 _from, _to;
    private float _dmg, _radius, _t = 0f, _fuse = 2f;
    private const float FlightT = 0.8f;
    private bool _landed = false, _blown = false;
    private MeshInstance3D _bomb, _tele;

    public void Init(Vector3 from, Vector3 to, float dmg, float radius, bool remote)
    {
        _from = from; _to = to; _dmg = dmg; _radius = Mathf.Max(2f, radius); Remote = remote;
        GlobalPosition = from;
        _bomb = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.42f, Height = 0.84f }, MaterialOverride = Game.ToonEmissive(new Color(0.32f, 0.8f, 0.28f), 0.9f) };
        AddChild(_bomb);
        AddChild(new OmniLight3D { OmniRange = 4.5f, LightColor = new Color(0.45f, 1f, 0.35f), LightEnergy = 1.4f });
    }

    public override void _Process(double delta)
    {
        if (Game.I == null || !Game.I.SimActive) return;
        float dt = (float)delta; _t += dt;
        if (!_landed)
        {
            float f = Mathf.Clamp(_t / FlightT, 0f, 1f);
            var pos = _from.Lerp(_to, f); pos.Y += Mathf.Sin(f * Mathf.Pi) * 6f;   // arc
            GlobalPosition = pos;
            if (_bomb != null) _bomb.RotationDegrees += new Vector3(300f * dt, 200f * dt, 0f);
            if (!Remote && DirectHit()) { Blow(); return; }   // (NEW) a direct hit mid-air detonates it immediately
            if (f >= 1f) Land();
        }
        else if (!_blown)
        {
            _fuse -= dt;
            float blink = _fuse < 0.6f ? 24f : 12f;   // blinks faster as it's about to go
            if (_tele != null) _tele.Scale = Vector3.One * (0.9f + 0.16f * Mathf.Sin(_t * blink));
            if (_bomb != null && _bomb.MaterialOverride is StandardMaterial3D bm) bm.EmissionEnergyMultiplier = 0.5f + (Mathf.Sin(_t * blink) > 0 ? 1.5f : 0f);
            if (_fuse <= 0f) Blow();
        }
        else if (_t > FlightT + 2.8f) QueueFree();
    }

    private bool DirectHit()
    {
        var pl = Game.I.Player;
        if (pl != null && !pl.Downed && GlobalPosition.DistanceTo(pl.GlobalPosition + Vector3.Up * 1.2f) < 1.7f) return true;
        if (Game.I.NetMgr != null && Game.I.NetMgr.BoltHitRemote(GlobalPosition, 1.7f, out long _)) return true;
        return false;
    }

    private void Land()
    {
        _landed = true; GlobalPosition = _to;
        var col = new Color(0.4f, 0.95f, 0.3f);   // green telegraph
        _tele = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = _radius * 0.85f, OuterRadius = _radius } };
        var tm = Game.Emissive(col, 2f); tm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; tm.AlbedoColor = new Color(col.R, col.G, col.B, 0.8f);
        _tele.MaterialOverride = tm; _tele.Position = new Vector3(0, 0.1f, 0); AddChild(_tele);
    }

    private void Blow()
    {
        if (_blown) return;
        _blown = true; _landed = true;
        if (!Remote) Game.I.NetMgr?.HurtPlayersIn(GlobalPosition, _radius, _dmg);   // enemy attack — damages players (host-authoritative)
        var green = new Color(0.4f, 0.98f, 0.32f);
        Game.I.VfxRing(GlobalPosition, green, _radius * 1.4f, 0.45f);
        var flash = new MeshInstance3D { Mesh = new SphereMesh { Radius = _radius * 0.5f, Height = _radius }, MaterialOverride = Game.Emissive(green, 3.2f) };
        Game.I.AddChild(flash); flash.GlobalPosition = GlobalPosition + Vector3.Up * 0.4f;
        if (flash.MaterialOverride is StandardMaterial3D fm)
        {
            fm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
            var tw = flash.CreateTween(); tw.SetParallel(true);
            tw.TweenProperty(flash, "scale", Vector3.One * 2.5f, 0.35f).SetEase(Tween.EaseType.Out);
            tw.TweenProperty(fm, "albedo_color", new Color(green.R, green.G, green.B, 0f), 0.35f);
            tw.Chain().TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(flash)) flash.QueueFree(); }));
        }
        Game.I.Sfx?.Thunder();
        if (_bomb != null) { _bomb.QueueFree(); _bomb = null; }
        if (_tele != null) { _tele.QueueFree(); _tele = null; }
    }
}
