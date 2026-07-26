using Godot;

// BossLair.cs — the world's boss objective. Spawns at map load somewhere in the bounded disc; ALWAYS marked on the minimap.
// Hold E (from wave 2 on) to CHALLENGE it: the boss emerges here and the escalating waves keep going around the fight. When
// the boss is beaten the portal to the next world opens. State: 0 sealed (dormant), 1 active (boss out), 2 conquered.
public partial class BossLair : Node3D
{
    public int NetId = 0;
    public bool Remote = false;   // client ghost: host owns activation/defeat, this just reflects synced state + draws the visual
    public int State = 0;         // 0 sealed · 1 active · 2 conquered
    public const float Radius = 4.5f;   // hold-E interaction reach to the gate

    private float _t;
    private OmniLight3D _light;
    private MeshInstance3D _core;      // the sealed "eye" — pulses red while dormant, flares on activation, goes dark when conquered
    private StandardMaterial3D _coreMat;

    private static readonly Color Sealed = new Color(0.95f, 0.18f, 0.22f);   // ominous blood-red seal
    private static readonly Color Active = new Color(1f, 0.55f, 0.15f);      // raging amber while the boss is out
    private static readonly Color Dead   = new Color(0.28f, 0.28f, 0.34f);   // spent grey once conquered

    public Color IconColor => State == 2 ? Dead : State == 1 ? Active : Sealed;

    public override void _Ready()
    {
        _t = (float)GD.RandRange(0, 6.28);
        var stone = Game.Toon(new Color(0.10f, 0.09f, 0.12f), 0.95f, 0.25f, 0.03f);   // near-black cursed basalt

        // a heavy trilithon gate — two leaning monoliths + a lintel, framing the sealed core
        foreach (float sx in new[] { -1.9f, 1.9f })
        {
            var pillar = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(1.3f, 6.2f, 1.3f) }, MaterialOverride = stone };
            pillar.Position = new Vector3(sx, 3.0f, 0f);
            pillar.RotationDegrees = new Vector3(0, 0, sx < 0 ? 4f : -4f);   // lean inward
            AddChild(pillar);
        }
        var lintel = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(5.6f, 1.3f, 1.5f) }, MaterialOverride = stone };
        lintel.Position = new Vector3(0, 6.4f, 0f); AddChild(lintel);
        // jagged altar step at the base
        var step = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 3.2f, BottomRadius = 3.8f, Height = 0.7f, RadialSegments = 6 }, MaterialOverride = stone };
        step.Position = new Vector3(0, 0.35f, 0f); AddChild(step);

        // the sealed core — a glowing orb caged between the pillars
        _coreMat = new StandardMaterial3D
        {
            AlbedoColor = Sealed, EmissionEnabled = true, Emission = Sealed, EmissionEnergyMultiplier = 3.2f,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
        };
        _core = new MeshInstance3D { Mesh = new SphereMesh { Radius = 1.1f, Height = 2.2f }, MaterialOverride = _coreMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        _core.Position = new Vector3(0, 3.2f, 0f); AddChild(_core);

        _light = new OmniLight3D { OmniRange = 22f, LightColor = Sealed, LightEnergy = 2.4f, Position = new Vector3(0, 3.2f, 0) };
        AddChild(_light);

        ApplyStateVisual();
    }

    public void SetState(int s) { if (State == s) return; State = s; ApplyStateVisual(); }

    private void ApplyStateVisual()
    {
        var c = IconColor;
        if (_coreMat != null) { _coreMat.AlbedoColor = c; _coreMat.Emission = c; _coreMat.EmissionEnergyMultiplier = State == 2 ? 0.6f : 3.2f; }
        if (_light != null) { _light.LightColor = c; _light.LightEnergy = State == 2 ? 0.5f : (State == 1 ? 3.4f : 2.4f); }
    }

    public override void _Process(double delta)
    {
        _t += (float)delta;
        if (_core != null)
        {
            _core.Rotation = new Vector3(0, _t * 0.6f, 0);
            float pulse = State == 2 ? 0.6f : (State == 1 ? 3.4f + 1.2f * Mathf.Sin(_t * 6f) : 2.6f + 1.0f * Mathf.Sin(_t * 2.2f));   // dormant breathes slow, active roils fast
            if (_coreMat != null) _coreMat.EmissionEnergyMultiplier = pulse;
            float s = 1f + (State == 1 ? 0.12f : 0.05f) * Mathf.Sin(_t * (State == 1 ? 7f : 2.2f));
            _core.Scale = new Vector3(s, s, s);
        }
    }
}
