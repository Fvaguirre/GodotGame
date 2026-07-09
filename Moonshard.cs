using Godot;

// Moonfall mutator hazard: a moon-fragment asteroid. It telegraphs a target ring on the ground, an ember-lit rock plummets
// from the sky (~1.1s — dodgeable), then IMPACTS for solid damage in a radius and leaves a molten CRATER that lingers,
// hurting anyone who stands in it. Size varies → bigger/smaller impact + crater. Host applies damage via HurtPlayersIn;
// client ghosts (Remote) are visual-only (spawned from VFX kind 62). See Game.SpawnMoonshard / MoonfallTick.
public partial class Moonshard : Node3D
{
    public bool Remote = false;
    public float Size = 1f;

    private float _fall = 1.6f;   // (BUFF) telegraph + fall time before impact — more time to read the danger disc and dodge (was 1.1)
    private float _linger, _age = 0f, _tick = 0f;
    private bool _impacted = false;
    private float _impactR, _directDmg, _craterR, _craterDmg;
    private MeshInstance3D _rock, _tele, _crater;
    private OmniLight3D _light;
    private Vector3 _ground;

    public void Init(Vector3 pos, float size)
    {
        Size = size;
        _impactR   = 2.8f * size;
        _craterR   = 3.3f * size;
        _directDmg = 14f * size + (Game.I?.Wave ?? 1) * 0.7f;   // (NERF) telegraphed hit — was 20*size + Wave*1.2, too punishing
        _craterDmg = 3.2f * size;                               // (NERF) lingering crater tick (was 5*size)
        _linger    = 2.4f + size * 1.7f;
        float gy = Game.I != null ? Game.I.SurfaceHeight(pos, 1e9f) : 0f;
        _ground = new Vector3(pos.X, gy, pos.Z);
        GlobalPosition = _ground;
        var ember = new Color(1f, 0.5f, 0.2f);

        _tele = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = _impactR * 0.88f, OuterRadius = _impactR } };
        var tm = Game.Emissive(new Color(1f, 0.2f, 0.1f), 2.4f);
        tm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; tm.AlbedoColor = new Color(1f, 0.2f, 0.1f, 0.9f);
        _tele.MaterialOverride = tm; _tele.Position = new Vector3(0, 0.08f, 0); AddChild(_tele);
        // (NEW) a FILLED red danger disc under the ring so the landing zone reads clearly on the ground before impact
        var disc = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = _impactR * 0.92f, BottomRadius = _impactR * 0.92f, Height = 0.05f } };
        var dm = Game.Emissive(new Color(1f, 0.25f, 0.12f), 1.4f);
        dm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; dm.AlbedoColor = new Color(1f, 0.25f, 0.12f, 0.34f); dm.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        disc.MaterialOverride = dm; disc.Position = new Vector3(0, -0.05f, 0); _tele.AddChild(disc);   // rides the ring's pulse

        float rs = 0.7f * size;
        _rock = new MeshInstance3D { Mesh = new SphereMesh { Radius = rs, Height = rs * 2f, RadialSegments = 7, Rings = 5 }, MaterialOverride = Game.ToonEmissive(new Color(0.55f, 0.46f, 0.55f), 0.9f, 0.09f) };
        _rock.Position = new Vector3(0, 36f, 0); AddChild(_rock);
        _rock.AddChild(new OmniLight3D { OmniRange = 7f * size, LightColor = ember, LightEnergy = 2.6f });
        _light = new OmniLight3D { OmniRange = _impactR * 2.2f, LightColor = new Color(1f, 0.3f, 0.14f), LightEnergy = 0.6f, Position = new Vector3(0, 1f, 0) };
        AddChild(_light);
    }

    public override void _Process(double delta)
    {
        if (Game.I == null || Game.I.State != GameState.Playing) return;
        float dt = (float)delta; _age += dt;
        if (!_impacted)
        {
            _fall -= dt;
            float t = Mathf.Clamp(1f - _fall / 1.6f, 0f, 1f);   // normalized against the 1.6s fall time
            if (_rock != null) { _rock.Position = new Vector3(0, Mathf.Lerp(36f, 0.6f * Size, t * t), 0); _rock.RotationDegrees += new Vector3(220f * dt, 150f * dt, 0f); }
            if (_tele != null) _tele.Scale = Vector3.One * (0.9f + 0.14f * Mathf.Sin(_age * 20f)) * (0.7f + 0.3f * t);   // pulse + grow the danger disc as it closes in
            if (_fall <= 0f) Impact();
        }
        else
        {
            _linger -= dt;
            if (!Remote) { _tick -= dt; if (_tick <= 0f) { _tick = 0.5f; Game.I.NetMgr?.HurtPlayersIn(_ground, _craterR, _craterDmg); } }   // linger tick (host)
            if (_linger <= 0f) QueueFree();
        }
    }

    private void Impact()
    {
        _impacted = true;
        if (_rock != null) { _rock.QueueFree(); _rock = null; }
        if (_tele != null) { _tele.QueueFree(); _tele = null; }
        var col = new Color(1f, 0.5f, 0.18f);
        if (!Remote) Game.I.NetMgr?.HurtPlayersIn(_ground, _impactR, _directDmg);   // the direct hit (host)
        Game.I.VfxRing(_ground, col, _impactR * 1.4f, 0.5f);
        Game.I.SpawnPollen(_ground + Vector3.Up * 0.5f, _impactR, new Color(0.6f, 0.5f, 0.45f), 14, 1.2f, net: false);
        Game.I.Sfx?.Thunder();

        _crater = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = _craterR, BottomRadius = _craterR, Height = 0.14f } };
        var cm = Game.ToonEmissive(new Color(1f, 0.35f, 0.1f), 1.6f, 0f);
        cm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; cm.AlbedoColor = new Color(1f, 0.35f, 0.1f, 0.55f);
        _crater.MaterialOverride = cm; _crater.Position = new Vector3(0, 0.07f, 0); AddChild(_crater);
        _crater.AddChild(new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = _craterR * 0.9f, OuterRadius = _craterR }, MaterialOverride = Game.Emissive(new Color(1f, 0.5f, 0.15f), 2.6f) });
        var fade = _crater.CreateTween(); fade.TweenInterval(Mathf.Max(0.1f, _linger - 0.6f));
        fade.TweenProperty(cm, "albedo_color", new Color(1f, 0.35f, 0.1f, 0f), 0.6f);
        if (_light != null) _light.LightEnergy = 1.5f;
    }
}
