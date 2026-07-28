using Godot;

// Flower.cs — a little bloom that flares brighter when you brush past it, jump near it, or hit it with fire/AoE.
// Game.GlowFlowersNear() pulses every bloom in range. Purely cosmetic + local, so it needs no networking. (NEW)
public partial class Flower : Node3D
{
    private MeshInstance3D _bloom;   // the whole flower model (stem + bloom baked in)
    private Vector3 _baseScale = Vector3.One;
    private bool _glowing = false;

    public void Init(Color bloomCol, float stemH, ulong seed)
    {
        // (MESHY) real 3D flower model. It carries its own stem, so no separate stem node. A gentle per-flower colour push
        // toward the requested palette bloom colour (plus seeded jitter) keeps a bed of flowers varied but grove-cohesive.
        float height = Mathf.Max(0.4f, stemH * 1.9f);
        _bloom = PropGlb.Instance("flower", height, seed: (int)seed);
        // signed albedo offset: nudge toward the palette hue (small, so the baked texture still reads), plus the seed jitter
        var jit = Vis.VaryColorSeeded((int)seed, 0.05f, 0.09f);
        var push = new Color((bloomCol.R - 0.55f) * 0.28f, (bloomCol.G - 0.55f) * 0.28f, (bloomCol.B - 0.55f) * 0.28f, 0f);
        _bloom.SetInstanceShaderParameter("node_var", new Vector4(jit.R + push.R, jit.G + push.G, jit.B + push.B, 0f));
        AddChild(_bloom);
    }

    // react to being brushed past / jumped near / hit: a quick scale pop, then settle back.
    // guarded so continuous proximity doesn't restack the tween every frame.
    public void Pulse()
    {
        if (_glowing || !GodotObject.IsInstanceValid(_bloom)) return;
        _glowing = true;
        _baseScale = _bloom.Scale;
        var st = _bloom.CreateTween();
        st.TweenProperty(_bloom, "scale", _baseScale * 1.32f, 0.12f).SetEase(Tween.EaseType.Out);
        st.TweenProperty(_bloom, "scale", _baseScale, 0.5f).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
        st.TweenCallback(Callable.From(() => _glowing = false));
    }
}
