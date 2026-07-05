using Godot;

// A short-lived effect that grows and fades, then frees itself.
// Vfx.cs — a tiny one-shot mesh-burst helper (Init(mesh,color,life,energy) then it fades + frees). Used everywhere for quick pops.
public partial class Vfx : Node3D
{
    private float _life, _max, _grow, _baseEnergy;
    private MeshInstance3D _m;
    private StandardMaterial3D _mat;

    public void Init(Mesh mesh, Color col, float life, float grow)
    {
        _life = _max = life;
        _grow = grow;
        _m = new MeshInstance3D { Mesh = mesh };
        _mat = Game.Emissive(col, 1.6f);
        _mat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        _m.MaterialOverride = _mat;
        _baseEnergy = _mat.EmissionEnergyMultiplier;
        AddChild(_m);
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        _life -= dt;
        float t = Mathf.Clamp(_life / _max, 0, 1);
        float s = Mathf.Lerp(1f, _grow, 1f - t);
        _m.Scale = Vector3.One * s;
        var col = _mat.AlbedoColor; col.A = t * 0.85f; _mat.AlbedoColor = col;
        _mat.EmissionEnergyMultiplier = _baseEnergy * t;
        if (_life <= 0) QueueFree();
    }
}
