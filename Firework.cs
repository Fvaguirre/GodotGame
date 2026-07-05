using Godot;

// Firework.cs — the "flare" the witches fire straight up (hold T) to show each other where they are in the
// maze. Rises ABOVE the hedge walls, then bursts into a shower of sparks + several subexplosions, all in the
// witch's own colour. Self-contained + self-freeing, so it replicates trivially: the caster spawns one and
// broadcasts VFX kind 36; each ally spawns their own copy (and it plays its own launch/burst sound locally).
public partial class Firework : Node3D
{
    private Color _col;
    private float _t = 0f;
    private bool _burst = false;
    private const float RiseDur = 1.05f;
    private const float RiseH = 34f;      // clears the 28-unit hedges so it's visible over the walls
    private Vector3 _from;
    private MeshInstance3D _head;
    private OmniLight3D _headLight;

    public void Init(Vector3 from, Color col)
    {
        _from = from; _col = col;
        GlobalPosition = from;
        _head = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.28f, Height = 0.56f }, MaterialOverride = Game.Emissive(col.Lerp(Colors.White, 0.45f), 5.5f) };
        AddChild(_head);
        _headLight = new OmniLight3D { OmniRange = 7f, LightColor = col, LightEnergy = 2.4f };
        AddChild(_headLight);
        Game.I?.Sfx?.FireworkLaunch(from);
    }

    public override void _Process(double delta)
    {
        if (Game.I == null) return;
        float dt = (float)delta; _t += dt;
        if (!_burst)
        {
            float k = Mathf.Clamp(_t / RiseDur, 0f, 1f);
            float ease = 1f - (1f - k) * (1f - k);          // ease-out rise
            GlobalPosition = _from + new Vector3(0, ease * RiseH, 0);
            if (GD.Randf() < 0.7f) Trail(GlobalPosition);
            if (_t >= RiseDur) Burst();
        }
        else if (_t >= RiseDur + 2.4f) QueueFree();
    }

    private void Trail(Vector3 at)
    {
        var mote = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.1f, Height = 0.2f }, MaterialOverride = Game.Emissive(_col.Lerp(Colors.White, 0.3f), 3f) };
        Game.I.AddChild(mote);
        mote.GlobalPosition = at + new Vector3((GD.Randf() - 0.5f) * 0.2f, 0, (GD.Randf() - 0.5f) * 0.2f);
        var tw = mote.CreateTween(); tw.SetParallel(true);
        tw.TweenProperty(mote, "global_position", mote.GlobalPosition - new Vector3(0, 1.2f, 0), 0.5f);
        tw.TweenProperty(mote, "transparency", 1f, 0.5f);
        tw.SetParallel(false);
        tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(mote)) mote.QueueFree(); }));
    }

    private void Burst()
    {
        _burst = true;
        if (_head != null) _head.Visible = false;
        if (_headLight != null) _headLight.LightEnergy = 0f;
        var c = GlobalPosition;
        Game.I?.Sfx?.FireworkBurst(c);

        // bright flash
        var flash = new OmniLight3D { OmniRange = 22f, LightColor = _col, LightEnergy = 6f };
        Game.I.AddChild(flash); flash.GlobalPosition = c;
        var ft = flash.CreateTween();
        ft.TweenProperty(flash, "light_energy", 0f, 0.6f);
        ft.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(flash)) flash.QueueFree(); }));

        float ps = Mathf.Clamp(Game.I.ParticleScale, 0.4f, 1f);
        int sparks = (int)(70 * ps);
        for (int i = 0; i < sparks; i++) Spark(c, _col.Lerp(Colors.White, GD.Randf() * 0.5f), 4.5f + GD.Randf() * 4f, 0.06f + GD.Randf() * 0.05f, 1.0f + GD.Randf() * 0.5f);

        // subexplosions offset from the main burst
        int subs = Mathf.Max(3, (int)(5 * ps));
        for (int s = 0; s < subs; s++)
        {
            var off = c + new Vector3((GD.Randf() - 0.5f) * 6f, (GD.Randf() - 0.5f) * 4f, (GD.Randf() - 0.5f) * 6f);
            int n = (int)(16 * ps);
            for (int i = 0; i < n; i++) Spark(off, _col, 2.5f + GD.Randf() * 2f, 0.05f, 0.75f + GD.Randf() * 0.4f);
        }
    }

    private void Spark(Vector3 c, Color col, float reach, float rad, float dur)
    {
        var dir = new Vector3(GD.Randf() - 0.5f, GD.Randf() - 0.5f, GD.Randf() - 0.5f).Normalized();
        var spark = new MeshInstance3D { Mesh = new SphereMesh { Radius = rad, Height = rad * 2f }, MaterialOverride = Game.Emissive(col, 4f) };
        Game.I.AddChild(spark); spark.GlobalPosition = c;
        var end = c + dir * reach + new Vector3(0, -reach * 0.4f, 0);   // arc out, gravity droop
        var tw = spark.CreateTween(); tw.SetParallel(true);
        tw.TweenProperty(spark, "global_position", end, dur).SetEase(Tween.EaseType.Out);
        tw.TweenProperty(spark, "transparency", 1f, dur + 0.2f);
        tw.SetParallel(false);
        tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(spark)) spark.QueueFree(); }));
    }
}
