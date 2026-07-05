using Godot;

// Flower.cs — a little bloom that flares brighter when you brush past it, jump near it, or hit it with fire/AoE.
// Game.GlowFlowersNear() pulses every bloom in range. Purely cosmetic + local, so it needs no networking. (NEW)
public partial class Flower : Node3D
{
    private MeshInstance3D _bloom;
    private StandardMaterial3D _mat;
    private float _baseEnergy = 1.8f;
    private Vector3 _baseScale = Vector3.One;
    private bool _glowing = false;

    public void Init(Color bloomCol, float stemH, ulong seed)
    {
        var stem = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.02f, BottomRadius = 0.03f, Height = stemH } };
        stem.MaterialOverride = Game.Toon(new Color(0.10f, 0.18f, 0.10f), 0.95f, 0.22f, 0f);
        stem.Position = new Vector3(0, stemH / 2f, 0);
        AddChild(stem);

        _mat = Game.ToonEmissive(bloomCol, 0.5f);
        _baseEnergy = _mat.EmissionEnergyMultiplier;
        _bloom = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.12f, Height = 0.18f }, MaterialOverride = _mat };
        _bloom.Position = new Vector3(0, stemH, 0);
        AddChild(_bloom);
    }

    // flare up then settle back; guarded so continuous proximity doesn't restack the tween every frame
    public void Pulse()
    {
        if (_mat == null || _glowing) return;
        _glowing = true;
        float peak = _baseEnergy * 2.6f;
        var t = CreateTween();
        t.TweenMethod(Callable.From<float>(v => { if (GodotObject.IsInstanceValid(_mat)) _mat.EmissionEnergyMultiplier = v; }), _baseEnergy, peak, 0.12f);
        t.TweenMethod(Callable.From<float>(v => { if (GodotObject.IsInstanceValid(_mat)) _mat.EmissionEnergyMultiplier = v; }), peak, _baseEnergy, 0.5f);
        t.TweenCallback(Callable.From(() => _glowing = false));

        if (GodotObject.IsInstanceValid(_bloom))
        {
            var st = _bloom.CreateTween();
            st.TweenProperty(_bloom, "scale", _baseScale * 1.35f, 0.12f).SetEase(Tween.EaseType.Out);
            st.TweenProperty(_bloom, "scale", _baseScale, 0.5f);
        }
    }
}
