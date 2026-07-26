using Godot;

// ArcaneRift.cs — a ~1-second arcane telegraph (Arcane Blink finisher) that hovers at a spot crackling like a charge orb,
// then DETONATES for area damage. Caster-owned; damage is host-authoritative via Player.ArcaneRiftHit → Enemy.Hurt. The
// ground telegraph + boom are networked (lingering sigil + kind-79 rupture) so allies see both.
public partial class ArcaneRift : Node3D
{
    private Player _src; private float _fuse, _life, _radius, _dmg, _seed; private bool _done;
    public float Pull = 0f;   // (OVERHAUL) Arcane Blink Implode: drag foes toward the rift before it detonates
    private Node3D _shards; private StandardMaterial3D _halo;

    public void Init(Player src, Vector3 pos, float radius, float dmg, float fuse)
    {
        _src = src; _radius = radius; _dmg = dmg; _fuse = fuse; _life = fuse; _seed = GD.Randf() * 9f;
        var col = DamageTypes.Col(DamageType.Arcane);
        GlobalPosition = new Vector3(pos.X, Game.I.SurfaceHeight(pos, pos.Y) + radius * 0.45f, pos.Z);
        AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = radius * 0.32f, Height = radius * 0.64f }, MaterialOverride = Game.ArcaneEnergyMat(), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        var halo = new MeshInstance3D { Mesh = new SphereMesh { Radius = radius * 0.5f, Height = radius }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        _halo = new StandardMaterial3D { AlbedoColor = new Color(col.R, col.G, col.B, 0.2f), EmissionEnabled = true, Emission = col, EmissionEnergyMultiplier = 1.6f, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
        halo.MaterialOverride = _halo; AddChild(halo);
        _shards = new Node3D(); AddChild(_shards);
        var smat = Game.Emissive(col.Lerp(Colors.White, 0.2f), 2.2f);
        for (int i = 0; i < 5; i++) { var sp = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = radius * 0.08f, Height = radius * (0.6f + GD.Randf() * 0.5f), RadialSegments = 4 }, MaterialOverride = smat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off }; sp.RotationDegrees = new Vector3(GD.Randf() * 360f, GD.Randf() * 360f, GD.Randf() * 360f); _shards.AddChild(sp); }
        AddChild(new OmniLight3D { OmniRange = radius * 1.6f, LightColor = col, LightEnergy = 2f, ShadowEnabled = false });
        Game.I.SpawnGroundSigilLinger(new Vector3(pos.X, 0.05f, pos.Z), radius, col, fuse);   // networked ground telegraph for the fuse
    }

    public override void _Process(double delta)
    {
        if (Game.I == null || !Game.I.SimActive) return;
        float dt = (float)delta; _life -= dt;
        float k = 1f - Mathf.Clamp(_life / _fuse, 0f, 1f);
        if (_shards != null) { _shards.RotationDegrees += new Vector3(dt * 180f, dt * 240f, dt * 140f); _shards.Scale = Vector3.One * (1f + k * 0.8f); }
        if (_halo != null) _halo.EmissionEnergyMultiplier = 1.4f + k * 1.6f + 0.4f * Mathf.Abs(Mathf.Sin(_seed + k * 30f));
        if (Pull > 0f && !_done)   // (OVERHAUL) Implode: pull foes toward the rift as it charges
            foreach (var e in Game.I.Enemies.ToArray())
                if (e != null && !e.Dead && GodotObject.IsInstanceValid(e))
                { var f = e.GlobalPosition - GlobalPosition; f.Y = 0f; if (f.Length() < _radius * 2.5f) e.PullToward(new Vector3(GlobalPosition.X, e.GlobalPosition.Y, GlobalPosition.Z), Pull * dt); }
        if (_life <= 0f && !_done) { _done = true; Detonate(); }
    }

    private void Detonate()
    {
        Vector3 c = new Vector3(GlobalPosition.X, Game.I.SurfaceHeight(GlobalPosition, GlobalPosition.Y), GlobalPosition.Z);
        if (_src != null && GodotObject.IsInstanceValid(_src) && !_src.Downed)
            foreach (var e in Game.I.Enemies.ToArray())
            {
                if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
                var f = e.GlobalPosition - c; f.Y = 0f; if (f.Length() > _radius + e.Radius) continue;
                _src.ArcaneRiftHit(e, _dmg);
            }
        Game.I.DamageWorld(c, _radius, _dmg);
        Game.I.SpawnArcaneRupture(c + Vector3.Up * 0.5f, _radius);
        Game.I.NetMgr?.BroadcastVfx(79, c + Vector3.Up * 0.5f, Vector3.Zero, _radius, 0f, DamageTypes.Col(DamageType.Arcane));
        QueueFree();
    }
}
