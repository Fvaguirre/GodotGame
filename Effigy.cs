using Godot;

// Effigy.cs — a scattered world shrine you find by exploring. Hold E (costs gold that rises per type, lobby-wide)
// to rouse it: it grants a guaranteed roll-3 of a THEMED universal blessing and is then spent. Themes by Kind:
// 0 survival · 1 power (damage) · 2 fortune (luck/crit-chance) · 3 swiftness (movement) · 4 coven (witch-specific).
// Colour-coded by theme; a skybeam beacon helps you spot one from afar, then venture to it.
public partial class Effigy : Node3D
{
    public int Kind = 0;
    public int NetId = 0;
    public bool Claimed = false;
    public bool Remote = false;
    private float _t;
    private Node3D _float;   // the hovering rune orb + ring
    private OmniLight3D _light;

    public static Color KindColor(int k) => k switch
    {
        0 => new Color(0.45f, 0.95f, 0.5f),   // survival — vital green
        1 => new Color(1f, 0.45f, 0.28f),     // power — ember red
        2 => new Color(1f, 0.82f, 0.34f),     // fortune — luck gold
        3 => new Color(0.42f, 0.85f, 0.95f),  // swiftness — gale cyan
        _ => new Color(0.72f, 0.42f, 1f),     // coven — arcane violet
    };
    public static string KindName(int k) => k switch { 0 => "Survival", 1 => "Power", 2 => "Fortune", 3 => "Swiftness", _ => "Coven" };

    public override void _Ready()
    {
        _t = (float)GD.RandRange(0, 6.28);
        var col = KindColor(Kind);
        var stone = Game.Toon(new Color(0.16f, 0.15f, 0.19f), 0.9f, 0.2f, 0.03f);
        var glow = Game.ToonEmissive(col, 2.4f, 0f);

        // a stacked-stone plinth + a leaning carved pillar
        var plinth = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.7f, BottomRadius = 0.95f, Height = 0.5f }, MaterialOverride = stone };
        plinth.Position = new Vector3(0, 0.25f, 0); AddChild(plinth);
        var pillar = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.68f, 1.8f, 0.68f) }, MaterialOverride = stone };
        pillar.Position = new Vector3(0, 1.4f, 0); pillar.RotationDegrees = new Vector3(0, 22f, 0); AddChild(pillar);
        for (int i = 0; i < 3; i++)   // glowing carved runes down the face
        {
            var rune = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.13f, 0.3f, 0.02f) }, MaterialOverride = glow, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            rune.Position = new Vector3(0, 0.95f + i * 0.45f, 0.36f); AddChild(rune);
        }

        // a floating rune orb + spinning ring above the shrine
        _float = new Node3D { Position = new Vector3(0, 2.75f, 0) }; AddChild(_float);
        _float.AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.32f, Height = 0.64f }, MaterialOverride = Game.Emissive(col.Lerp(Colors.White, 0.3f), 3f), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        var ring = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = 0.5f, OuterRadius = 0.58f, RingSegments = 6, Rings = 5 }, MaterialOverride = glow, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        ring.RotationDegrees = new Vector3(90, 0, 0); _float.AddChild(ring);

        _light = new OmniLight3D { OmniRange = 8f, LightColor = col, LightEnergy = 2f, Position = new Vector3(0, 2.75f, 0) };
        AddChild(_light);
        Game.AddBeacon(this, col);   // a skybeam so you can spot it across the land and venture over
    }

    public override void _Process(double delta)
    {
        if (Game.I == null) return;
        float dt = (float)delta; _t += dt;
        if (_float != null) { _float.RotateY(dt * 1.2f); _float.Position = new Vector3(0, 2.75f + Mathf.Sin(_t * 1.4f) * 0.12f, 0); }
        if (_light != null) _light.LightEnergy = 1.6f + 0.5f * Mathf.Abs(Mathf.Sin(_t * 1.6f));
    }

    // spent: shrink the whole shrine (beacon included, since it's a child) and free it
    public void Claim()
    {
        if (Claimed) return; Claimed = true;
        var tw = CreateTween(); tw.SetParallel(true);
        tw.TweenProperty(this, "scale", Vector3.One * 0.01f, 0.4f).SetEase(Tween.EaseType.In);
        if (_light != null) tw.TweenProperty(_light, "light_energy", 0f, 0.4f);
        tw.Chain().TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(this)) QueueFree(); }));
    }
}
