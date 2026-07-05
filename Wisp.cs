using Godot;

// Wisp.cs — a will-o'-wisp: a small floating mote that glows, gently bobs, drifts, and breathes. Purely
// decorative + local (built per-machine from the chunk seed), so it needs no networking. Scattered through
// foliage biomes to give the moonlit forest pockets of magical light — and to feed SSIL coloured light to
// bounce onto nearby surfaces. Short-range, shadowless light to stay cheap under Forward+ clustering. (NEW)
public partial class Wisp : Node3D
{
    private MeshInstance3D _mote;
    private OmniLight3D _light;
    private float _t, _seed, _energy;

    public void Init(Color col, float energy, float range, float seed)
    {
        _energy = energy; _seed = seed;
        _mote = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.15f, Height = 0.30f } };
        _mote.MaterialOverride = Game.ToonEmissive(col, 2.4f, 0f);
        AddChild(_mote);
        _light = new OmniLight3D { OmniRange = range, LightColor = col, LightEnergy = energy, ShadowEnabled = false };
        AddChild(_light);
        // start the children at the float offset so frame 0 isn't at the anchor
        var off = new Vector3(0, 0.9f, 0);
        _mote.Position = off; _light.Position = off;
    }

    public override void _Process(double delta)
    {
        if (Game.I == null || Game.I.State != GameState.Playing) return;
        _t += (float)delta;
        // gentle bob + slow lateral drift around the anchor + a soft breathing pulse
        float bob = Mathf.Sin(_t * 1.3f + _seed) * 0.35f;
        float driftX = Mathf.Sin(_t * 0.6f + _seed * 1.7f) * 0.45f;
        float driftZ = Mathf.Cos(_t * 0.5f + _seed * 2.3f) * 0.45f;
        var off = new Vector3(driftX, 0.9f + bob, driftZ);   // floats ~0.9m off its anchor
        if (_mote != null) _mote.Position = off;
        if (_light != null)
        {
            _light.Position = off;
            _light.LightEnergy = _energy * (0.7f + 0.3f * Mathf.Sin(_t * 2.0f + _seed * 3.1f));   // breathe
        }
    }
}
