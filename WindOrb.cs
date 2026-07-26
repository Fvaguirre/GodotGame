using Godot;
using System.Collections.Generic;

// WindOrb.cs — the "Implosion" charged-modifier visual (NEW LOOK). A rasengan-style sphere of rotating
// wind hovers at the center while outer gusts spiral INWARD across the whole area of effect, selling the
// "yank survivors inward" mechanic. Purely cosmetic: the pull / grind / damage is driven by a
// visual-suppressed Cyclone spawned alongside it (see ModType.Implosion). Spawned locally for the caster
// and, for allies, via Net BroadcastVfx kind 15 (identical look; damage stays the caster's).
public partial class WindOrb : Node3D
{
    private float _life = 0f, _dur = 2.6f, _radius = 8f;
    private bool _done = false;
    private Node3D _core;                       // spinning rasengan shells
    private readonly List<MeshInstance3D> _bands = new();
    private readonly List<MeshInstance3D> _gusts = new();
    private readonly List<float> _gAng = new(), _gR = new(), _gSpin = new(), _gDraw = new();

    public void Init(Vector3 pos, float radius, float dur)
    {
        _radius = radius; _dur = dur;
        GlobalPosition = new Vector3(pos.X, 0f, pos.Z);
        var col = DamageTypes.Col(DamageType.Wind);
        float orbY = 1.6f;

        // ---- central rasengan sphere -----------------------------------------------------------------
        _core = new Node3D { Position = new Vector3(0, orbY, 0) };
        AddChild(_core);

        // bright dense inner core
        var coreMat = Game.ToonEmissive(col.Lerp(Colors.White, 0.35f), 3.4f, 0f);
        coreMat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        var core = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.5f, Height = 1.0f }, MaterialOverride = coreMat };
        _core.AddChild(core);

        // translucent outer shell for volume
        var shellMat = Game.ToonEmissive(col, 1.4f, 0f);
        shellMat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        shellMat.AlbedoColor = new Color(col.R, col.G, col.B, 0.22f);
        shellMat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        shellMat.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        var shell = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.95f, Height = 1.9f }, MaterialOverride = shellMat };
        _core.AddChild(shell);

        // three flattened swirl bands on tilted axes — the fast spin gives the rasengan cross-swirl
        var bandMat = Game.ToonEmissive(col, 2.2f, 0f);
        bandMat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        bandMat.AlbedoColor = new Color(col.R, col.G, col.B, 0.6f);
        bandMat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        bandMat.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        for (int i = 0; i < 3; i++)
        {
            var band = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.85f, Height = 0.28f }, MaterialOverride = bandMat };
            band.RotationDegrees = new Vector3(i * 55f, i * 40f, i * 30f);
            _bands.Add(band);
            _core.AddChild(band);
        }

        _core.AddChild(new OmniLight3D { OmniRange = _radius * 1.4f, LightColor = col, LightEnergy = 2.2f });

        // ---- faint AoE ground disc so the affected area reads -----------------------------------------
        var discMat = Game.ToonEmissive(col, 0.5f, 0f);
        discMat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        discMat.AlbedoColor = new Color(col.R, col.G, col.B, 0.08f);
        discMat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        discMat.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        var disc = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = _radius, BottomRadius = _radius, Height = 0.04f, RadialSegments = 32 }, MaterialOverride = discMat };
        disc.Position = new Vector3(0, 0.05f, 0);
        AddChild(disc);

        // ---- outer gusts that spiral inward across the AoE --------------------------------------------
        var gustMat = Game.ToonEmissive(col, 1.6f, 0f);
        gustMat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        gustMat.AlbedoColor = new Color(col.R, col.G, col.B, 0.4f);
        gustMat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        gustMat.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        int n = 14;
        for (int i = 0; i < n; i++)
        {
            var g = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.35f, 0.28f, 2.6f) }, MaterialOverride = gustMat };
            AddChild(g);
            _gusts.Add(g);
            _gAng.Add(GD.Randf() * Mathf.Tau);
            _gR.Add(_radius * (0.55f + GD.Randf() * 0.45f));
            _gSpin.Add(2.2f + GD.Randf() * 1.8f);
            _gDraw.Add(1.2f + GD.Randf() * 1.4f);          // inward pull speed
        }

        Scale = new Vector3(0.2f, 0.2f, 0.2f);
        var tw = CreateTween();
        tw.TweenProperty(this, "scale", Vector3.One, 0.25f).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
    }

    public override void _Process(double delta)
    {
        if (Game.I == null || !Game.I.SimActive) return;   // freeze while paused
        float dt = (float)delta;
        _life += dt;

        // rasengan swirl: bands spin fast on their own tilted axes; whole core slow-spins + bobs
        if (_core != null)
        {
            _core.RotateY(dt * 3.5f);
            _core.Position = new Vector3(0, 1.6f + Mathf.Sin(_life * 3f) * 0.12f, 0);
        }
        for (int i = 0; i < _bands.Count; i++)
        {
            var b = _bands[i];
            if (b == null || !GodotObject.IsInstanceValid(b)) continue;
            b.RotateX(dt * (6f + i * 1.5f));
            b.RotateZ(dt * (5f + i * 1.2f));
        }

        // outer gusts orbit the center AND creep inward (the implosion), respawning at the rim
        for (int i = 0; i < _gusts.Count; i++)
        {
            var g = _gusts[i];
            if (g == null || !GodotObject.IsInstanceValid(g)) continue;
            _gAng[i] += _gSpin[i] * dt;
            _gR[i] -= _gDraw[i] * dt;
            if (_gR[i] < 0.8f) { _gR[i] = _radius * (0.85f + GD.Randf() * 0.15f); _gAng[i] = GD.Randf() * Mathf.Tau; }
            float a = _gAng[i], r = _gR[i];
            g.Position = new Vector3(Mathf.Cos(a) * r, 0.5f, Mathf.Sin(a) * r);
            g.Rotation = new Vector3(0, -a + Mathf.Pi * 0.5f, 0);     // length runs tangent → reads as a swirling gust
        }

        if (_life >= _dur) Collapse();
    }

    private void Collapse()
    {
        if (_done) return;
        _done = true;
        var tw = CreateTween();
        tw.TweenProperty(this, "scale", new Vector3(0.05f, 0.05f, 0.05f), 0.3f).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.In);  // suck inward — implode
        tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(this)) QueueFree(); }));
    }
}
