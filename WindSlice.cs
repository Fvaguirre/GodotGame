using Godot;
using System.Collections.Generic;

// WindSlice.cs — the "Wind Slice" finisher projectile: a travelling X of wind that flies forward and cuts
// each enemy in its path once (Wind damage; e.Hurt routes to the host for client casters). Modeled on
// BloodWave. The caster spawns the damaging one and broadcasts a Remote=true visual copy to allies
// (vfx kind 13), which surges + fades without dealing damage. Width/Range/Dmg scale with rarity + area. (NEW)
public partial class WindSlice : Node3D
{
    public Vector3 Dir = Vector3.Forward;
    public float Dmg = 20f;
    public float Width = 5f;
    public float Speed = 34f;
    public float Range = 40f;
    public bool Remote = false;        // client visual copy: travels + fades, no damage
    private bool _announced = false;
    private float _travelled = 0f;
    private Node3D _x;
    private readonly HashSet<Enemy> _hit = new();

    public override void _Ready()
    {
        var col = DamageTypes.Col(DamageType.Wind);
        var mat = new StandardMaterial3D {
            AlbedoColor = new Color(col.R, col.G, col.B, 0.75f),
            EmissionEnabled = true, Emission = col, EmissionEnergyMultiplier = 2.6f,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        var wispMat = new StandardMaterial3D {
            AlbedoColor = new Color(col.R, col.G, col.B, 0.3f),
            EmissionEnabled = true, Emission = col, EmissionEnergyMultiplier = 1.8f,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        _x = new Node3D();
        for (int i = 0; i < 2; i++)   // two crossed wind blades → an X (across the travel direction)
        {
            float roll = i == 0 ? 45f : -45f;
            var blade = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(Width * 1.25f, 0.5f, 0.1f) }, MaterialOverride = mat };
            blade.RotationDegrees = new Vector3(0, 0, roll);
            _x.AddChild(blade);
            for (int w = 1; w <= 2; w++)   // feathered wind echoes fore + aft → reads as a blade of wind, not a solid bar
            {
                float span = Width * (1.25f - w * 0.28f);
                var e1 = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(span, 0.28f, 0.06f) }, MaterialOverride = wispMat };
                e1.RotationDegrees = new Vector3(0, 0, roll); e1.Position = new Vector3(0, 0, w * 0.16f);
                _x.AddChild(e1);
                var e2 = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(span, 0.28f, 0.06f) }, MaterialOverride = wispMat };
                e2.RotationDegrees = new Vector3(0, 0, roll); e2.Position = new Vector3(0, 0, -w * 0.16f);
                _x.AddChild(e2);
            }
        }
        var core = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.5f, Height = 1f }, MaterialOverride = mat };
        _x.AddChild(core);   // bright hub where the blades cross
        AddChild(_x);
        AddChild(new OmniLight3D { OmniRange = Width * 1.4f, LightColor = col, LightEnergy = 2f });
        LookAt(GlobalPosition + Dir, Vector3.Up);   // face travel so the X stands up across the path
    }

    public override void _Process(double delta)
    {
        var g = Game.I;
        if (g == null || g.State != GameState.Playing) return;
        float dt = (float)delta;
        float step = Speed * dt;
        GlobalPosition += Dir * step;
        _travelled += step;
        if (_x != null) _x.RotateZ(dt * 8f);   // spin the X as it flies

        if (!Remote && !_announced)
        {
            _announced = true;
            g.NetMgr?.BroadcastVfx(13, GlobalPosition, Dir, Width, Range, DamageTypes.Col(DamageType.Wind));
        }

        var right = Dir.Cross(Vector3.Up).Normalized();
        if (!Remote)
        foreach (var e in g.Enemies.ToArray())
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e) || _hit.Contains(e)) continue;
            var to = e.GlobalPosition - GlobalPosition; to.Y = 0;
            float along = to.Dot(Dir);
            float side = Mathf.Abs(to.Dot(right));
            if (along > -1.4f && along < 1.8f && side < Width / 2f + e.Radius)
            {
                _hit.Add(e);
                e.Hurt(Dmg, DamageType.Wind, true);
            }
        }

        if (_travelled >= Range)
        {
            var tw = CreateTween();
            tw.TweenProperty(this, "scale", new Vector3(1.2f, 0.05f, 1.2f), 0.18f);
            tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(this)) QueueFree(); }));
            SetProcess(false);
        }
    }
}
