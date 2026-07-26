using Godot;

// EmberMeteor.cs — the Ember witch's aimed meteor secondary. A telegraphed sky-strike: a danger ring/disc marks the ground,
// a burning rock plummets (~1.3s), then IMPACTS for AoE Ember damage to ENEMIES and slaps X burn stacks on each (instant
// progress toward Living Bomb). Host/solo (the caster's machine) owns the damage; allies spawn a Remote visual-only ghost via
// VFX kind 67. Modeled on Moonshard, but friendly (hits enemies, not players).
public partial class EmberMeteor : Node3D
{
    public bool Remote = false;
    private float _fall = 1.7f, _fallDur = 1.7f, _age = 0f;   // (NEW) fall duration is now configurable (mod meteors fall slower so you can tell them apart)
    private bool _impacted = false;
    private float _radius, _dmg, _burnPer, _bombFlat;
    private int _burnStacks;
    private Player _src;
    private Vector3 _ground;
    private MeshInstance3D _rock, _tele, _disc;
    private OmniLight3D _light;

    public void Init(Vector3 at, float radius, float dmg, int burnStacks, float burnPer, float bombFlat, Player src, float fallTime = 1.7f)
    { _radius = radius; _dmg = dmg; _burnStacks = burnStacks; _burnPer = burnPer; _bombFlat = bombFlat; _src = src; _fall = _fallDur = fallTime; Build(at); }
    public void InitRemote(Vector3 at, float radius, float fallTime = 1.7f) { Remote = true; _radius = radius; _fall = _fallDur = fallTime; Build(at); }

    private void Build(Vector3 at)
    {
        float gy = Game.I != null ? Game.I.SurfaceHeight(at, 1e9f) : 0f;
        _ground = new Vector3(at.X, gy, at.Z); GlobalPosition = _ground;
        var col = DamageTypes.Col(DamageType.Ember);
        _tele = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = _radius * 0.88f, OuterRadius = _radius } };
        var tm = Game.Emissive(new Color(1f, 0.5f, 0.15f), 2.4f);
        tm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; tm.AlbedoColor = new Color(1f, 0.5f, 0.15f, 0.9f);
        _tele.MaterialOverride = tm; _tele.Position = new Vector3(0, 0.08f, 0); AddChild(_tele);
        _disc = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = _radius * 0.92f, BottomRadius = _radius * 0.92f, Height = 0.05f } };
        var dm = Game.Emissive(new Color(1f, 0.45f, 0.12f), 1.4f);
        dm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; dm.AlbedoColor = new Color(1f, 0.45f, 0.12f, 0.3f); dm.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        _disc.MaterialOverride = dm; _disc.Position = new Vector3(0, 0.03f, 0); AddChild(_disc);
        float rs = 0.55f + _radius * 0.07f;
        _rock = new MeshInstance3D { Mesh = new SphereMesh { Radius = rs, Height = rs * 2f, RadialSegments = 6, Rings = 4 }, MaterialOverride = Game.ToonEmissive(new Color(0.2f, 0.09f, 0.05f), 0.5f, 0.09f) };   // (POLISH) charred low-poly crust
        _rock.Position = new Vector3(0, 38f, 0); AddChild(_rock);
        _rock.AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = rs * 0.86f, Height = rs * 1.72f }, MaterialOverride = Game.Emissive(new Color(1f, 0.6f, 0.2f), 3.2f), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Position = new Vector3(0, -rs * 0.4f, 0) });   // molten leading face glowing through the cracks
        _rock.AddChild(new OmniLight3D { OmniRange = 8f, LightColor = new Color(1f, 0.5f, 0.2f), LightEnergy = 3f });
        _light = new OmniLight3D { OmniRange = _radius * 2.2f, LightColor = new Color(1f, 0.4f, 0.15f), LightEnergy = 0.6f, Position = new Vector3(0, 1f, 0) };
        AddChild(_light);
    }

    public override void _Process(double delta)
    {
        if (Game.I == null || !Game.I.SimActive) return;
        float dt = (float)delta; _age += dt;
        if (!_impacted)
        {
            _fall -= dt;
            float t = Mathf.Clamp(1f - _fall / _fallDur, 0f, 1f);
            if (_rock != null) { _rock.Position = new Vector3(0, Mathf.Lerp(38f, 0.5f, t * t), 0); _rock.RotationDegrees += new Vector3(240f * dt, 160f * dt, 0f); }
            if (_tele != null) _tele.Scale = Vector3.One * (0.9f + 0.12f * Mathf.Sin(_age * 24f)) * (0.7f + 0.3f * t);
            if (_fall <= 0f) Impact();
        }
        else { _fall -= dt; if (_fall <= -0.7f) QueueFree(); }
    }

    private void Impact()
    {
        _impacted = true; _fall = 0f;
        if (_rock != null) { _rock.QueueFree(); _rock = null; }
        if (_tele != null) { _tele.QueueFree(); _tele = null; }
        if (_disc != null) { _disc.QueueFree(); _disc = null; }
        if (!Remote && _src != null && GodotObject.IsInstanceValid(_src))
        {
            bool credited = false;
            foreach (var e in Game.I.Enemies.ToArray())
                if (e != null && !e.Dead && GodotObject.IsInstanceValid(e) && new Vector2(e.GlobalPosition.X - _ground.X, e.GlobalPosition.Z - _ground.Z).Length() < _radius + e.Radius)
                {
                    e.Hurt(_dmg, DamageType.Ember, true);
                    e.AddBurn(_burnStacks, _burnPer, _bombFlat);   // instant burn stacks → Living Bomb progress
                    if (!credited) { _src.OnHitDirect(e, e.Dead, _dmg, DamageType.Ember); credited = true; }   // combo/finisher once
                }
            if (credited) _src.AddMana(1f);   // (FIX) the charged secondary refunds a mana when it connects — matches every other witch's release
            Game.I.DamageWorld(_ground, _radius, _dmg);
        }
        Game.I.SpawnEmberBurst(_ground + Vector3.Up * 0.4f, _radius * 1.3f, net: false);   // each machine (real or ghost) shows its own burst
        Game.I.VfxRing(_ground, DamageTypes.Col(DamageType.Ember), _radius * 1.4f, 0.5f);
        Game.I.Sfx?.ModEmber(_ground, false);   // positional ember boom is the impact; dropped the extra global Thunder() that stacked into a wall of blasts when meteors rain
        if (_light != null) _light.LightEnergy = 3f;
    }
}
