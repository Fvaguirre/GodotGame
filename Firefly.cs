using Godot;

// Firefly.cs — the Rainforest's ambient light mote (the jungle's answer to the Grove's wisp). Warm yellow-green, it drifts
// through the canopy and BLINKS on and off like a real firefly (unlike the wisp's smooth breathe). Purely decorative + local
// (built per-machine from the chunk seed), feeds SSIL bounce light. Scattered by the jungle chunk builder.
public partial class Firefly : Node3D
{
    private MeshInstance3D _mote;
    private OmniLight3D _light;
    private float _t, _seed, _energy, _blink = 1f;

    public void Init(Color col, float energy, float range, float seed)
    {
        _energy = energy; _seed = seed;
        _mote = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.1f, Height = 0.2f } };
        _mote.MaterialOverride = Game.ToonEmissive(col, 3.2f, 0f);
        AddChild(_mote);
        _light = new OmniLight3D { OmniRange = range, LightColor = col, LightEnergy = energy, ShadowEnabled = false };
        AddChild(_light);
        var off = new Vector3(0, 1.1f, 0);
        _mote.Position = off; _light.Position = off;
    }

    public override void _Process(double delta)
    {
        if (Game.I == null || !Game.I.SimActive) return;
        _t += (float)delta;
        float bob = Mathf.Sin(_t * 1.6f + _seed) * 0.5f;
        float driftX = Mathf.Sin(_t * 0.5f + _seed * 1.7f) * 0.7f;
        float driftZ = Mathf.Cos(_t * 0.42f + _seed * 2.3f) * 0.7f;
        var off = new Vector3(driftX, 1.1f + bob, driftZ);
        // firefly blink: mostly on, with sharp brief flickers to dark
        float phase = Mathf.Sin(_t * 3.3f + _seed * 4.1f);
        _blink = Mathf.Lerp(_blink, phase > 0.2f ? 1f : 0.05f, Mathf.Clamp((float)delta * 12f, 0f, 1f));
        if (_mote != null) { _mote.Position = off; if (_mote.MaterialOverride is StandardMaterial3D mm) mm.EmissionEnergyMultiplier = 3.2f * _blink; }
        if (_light != null) { _light.Position = off; _light.LightEnergy = _energy * _blink; }
    }
}
