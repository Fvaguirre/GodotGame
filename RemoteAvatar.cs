using Godot;

// A visible stand-in for another player on the network. Net feeds it position+facing+vitals;
// it smoothly interpolates and animates a procedural witch body, color-coded to that ally's
// damage type. An x-ray silhouette keeps allies readable through walls and crowds.
// RemoteAvatar.cs — how an ALLY is drawn on your screen: their full WitchModel colored to their witch
// (from the witchIndex carried in NetVitals), an x-ray silhouette so you can see them through walls,
// a nameplate (distance-scaled), and blood/float cues. Driven entirely by Net snapshots (NetState for
// transform, NetVitals for HUD/color). One instance per other peer, created/destroyed in Net.
public partial class RemoteAvatar : Node3D
{
    private Vector3 _target;
    private float _targetYaw;
    private bool _have = false;
    public bool Downed = false;
    public int StunState = 0;   // (NEW) 0 none, 1 stunned by a mob, 2 grabbed by a Taker
    public float HpFrac = 1f, ManaFrac = 0f, ShieldFrac = 0f, Blessed = 0f;
    public int BloodStacks = 0, ArmorCount = 0, ArmorThorn = 0;
    public float Bark = 0f;

    private WitchModel _model;
    private MeshInstance3D _bubble, _thorns, _bloodMoon;
    private Node3D _gust;   // Stormform tell: wind rings swirling around this ally, shown to all (NEW)
    private StandardMaterial3D _bubbleMat;
    private StandardMaterial3D _silMat;
    private Label3D _tag;
    private int _slot = 0, _witch = -1;
    private Color _col = DamageTypes.Col(DamageType.Lunar);
    private Vector3 _prevPos;
    private float _speed01 = 0f;

    public void SetVitals(float hp, float mana, float shield, float blessed, int blood, int armorPacked, int witch, float bark, float eclipse, float storm)
    {
        HpFrac = hp; ManaFrac = mana; ShieldFrac = shield; Blessed = blessed; BloodStacks = blood; Bark = bark;
        ArmorCount = armorPacked & 0xF; ArmorThorn = (armorPacked >> 4) & 0xF;
        StunState = (armorPacked >> 8) & 0x3;   // (NEW) 0 none, 1 stunned, 2 grabbed
        if (_thorns != null) _thorns.Visible = bark > 0f;
        if (_bloodMoon != null) _bloodMoon.Visible = eclipse > 0f;
        if (_gust != null) _gust.Visible = storm > 0f;   // Stormform tell (NEW)
        if (_bubble != null)
        {
            _bubble.Visible = ArmorCount > 0;
            if (ArmorCount > 0 && _bubbleMat != null)
            {
                // green if mostly thorn, red if mostly blood — so allies can read the shield type at a glance
                var bc = ArmorThorn * 2 >= ArmorCount ? new Color(0.4f, 0.95f, 0.45f) : new Color(0.85f, 0.12f, 0.16f);
                _bubbleMat.AlbedoColor = new Color(bc.R, bc.G, bc.B, 0.24f);
                _bubbleMat.Emission = bc;
            }
        }
        if (witch != _witch) ApplyWitch(witch);
    }
    public Color WitchCol => _col;   // this ally's witch color (for the minimap dot)

    public override void _Ready()
    {
        BuildModel();

        // x-ray silhouette: a model-SHAPED ghost (per-mesh overlay on the actual witch model), drawn on top so allies read
        // through walls/enemies — traces her real outline instead of a fat capsule. Recolored via the shared _silMat.
        _silMat = Game.SilhouetteMat(_col);
        Game.AddModelSilhouette(_model, _silMat);

        // blood shield bubble
        _bubble = new MeshInstance3D { Mesh = new SphereMesh { Radius = 1.2f, Height = 2.4f } };
        _bubble.Position = new Vector3(0, 1.0f, 0);
        _bubbleMat = new StandardMaterial3D {
            AlbedoColor = new Color(0.8f, 0.05f, 0.12f, 0.22f),
            EmissionEnabled = true, Emission = new Color(0.7f, 0.05f, 0.1f), EmissionEnergyMultiplier = 1.4f,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled
        };
        _bubble.MaterialOverride = _bubbleMat;
        _bubble.Visible = false;
        AddChild(_bubble);

        // Barkskin thorn shell — a green translucent shell studded with outward spikes, shown to ALL
        // players whenever this ally has Barkskin up (synced via vitals.bark).
        _thorns = new MeshInstance3D { Mesh = new SphereMesh { Radius = 1.35f, Height = 2.7f } };
        _thorns.Position = new Vector3(0, 1.0f, 0);
        _thorns.MaterialOverride = new StandardMaterial3D {
            AlbedoColor = new Color(0.30f, 0.85f, 0.40f, 0.18f),
            EmissionEnabled = true, Emission = new Color(0.35f, 0.95f, 0.45f), EmissionEnergyMultiplier = 1.3f,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled
        };
        var spikeMat = new StandardMaterial3D {
            AlbedoColor = new Color(0.22f, 0.55f, 0.26f),
            EmissionEnabled = true, Emission = new Color(0.30f, 0.80f, 0.35f), EmissionEnergyMultiplier = 0.8f
        };
        int spikes = 14;
        for (int i = 0; i < spikes; i++)
        {
            float u = (i + 0.5f) / spikes;
            float theta = u * Mathf.Tau * 3f;
            float y = 1f - 2f * u;
            float ring = Mathf.Sqrt(Mathf.Max(0f, 1f - y * y));
            var dir = new Vector3(Mathf.Cos(theta) * ring, y, Mathf.Sin(theta) * ring);
            var spike = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.12f, Height = 0.5f }, MaterialOverride = spikeMat };
            spike.Position = dir * 1.35f;
            var axis = Vector3.Up.Cross(dir);
            float ang = Mathf.Acos(Mathf.Clamp(Vector3.Up.Dot(dir), -1f, 1f));
            spike.Basis = axis.LengthSquared() > 1e-5f ? new Basis(axis.Normalized(), ang) : Basis.Identity;
            _thorns.AddChild(spike);
        }
        _thorns.Visible = false;
        AddChild(_thorns);

        // Blood moon — a red glowing orb that hovers above this ally while their Lunar Eclipse is active.
        // It's a child of the avatar, so it follows them automatically. Shown to all players (eclipse synced via vitals).
        _bloodMoon = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.9f, Height = 1.8f } };
        _bloodMoon.Position = new Vector3(0, 3.4f, 0);
        _bloodMoon.MaterialOverride = new StandardMaterial3D {
            AlbedoColor = new Color(0.5f, 0.04f, 0.06f),
            EmissionEnabled = true, Emission = new Color(0.85f, 0.10f, 0.12f), EmissionEnergyMultiplier = 2.2f,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel
        };
        var moonGlow = new OmniLight3D { OmniRange = 7f, LightColor = new Color(0.9f, 0.2f, 0.2f), LightEnergy = 1.4f };
        _bloodMoon.AddChild(moonGlow);
        _bloodMoon.Visible = false;
        AddChild(_bloodMoon);

        // Stormform gust — translucent wind rings that swirl around this ally while their Stormform is up
        // (synced via vitals.storm). Spun in the per-frame update. (NEW)
        _gust = new Node3D();
        var windCol = DamageTypes.Col(DamageType.Wind);
        for (int i = 0; i < 3; i++)
        {
            var ring = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = 0.95f + i * 0.12f, OuterRadius = 1.15f + i * 0.12f } };
            var gm = new StandardMaterial3D {
                AlbedoColor = new Color(windCol.R, windCol.G, windCol.B, 0.34f),
                EmissionEnabled = true, Emission = windCol, EmissionEnergyMultiplier = 1.4f,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            };
            ring.MaterialOverride = gm;
            ring.Position = new Vector3(0, 0.5f + i * 0.7f, 0);
            ring.RotationDegrees = new Vector3(90, 0, i * 22f);   // near-horizontal bands at staggered tilts
            _gust.AddChild(ring);
        }
        _gust.Visible = false;
        AddChild(_gust);

        // big, legible nameplate, fixed screen size, drawn on top
        _tag = new Label3D {
            Text = $"Warden {_slot + 2}",
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            Modulate = _col.Lerp(Colors.White, 0.35f),
            OutlineModulate = new Color(0, 0, 0, 0.92f), OutlineSize = 10, FontSize = 40, PixelSize = 0.008f,
            NoDepthTest = true, RenderPriority = 9
        };
        _tag.Position = new Vector3(0, 2.35f, 0);
        AddChild(_tag);

        ApplyColor();
    }

    private void BuildModel()
    {
        if (_model != null && GodotObject.IsInstanceValid(_model)) _model.QueueFree();
        _model = new WitchModel();
        _model.Build(Mathf.Max(0, _witch), false);
        AddChild(_model);
        if (_silMat != null) Game.AddModelSilhouette(_model, _silMat);   // re-trace the x-ray ghost on the new model
        if (Downed) _model.Collapse(true);
    }

    public void SetTeamColor(int slot) { _slot = slot; if (_tag != null) _tag.Text = $"Warden {_slot + 2}"; }

    public void SetFloating(bool f) { _model?.ShowWings(f); }
    public void PlayArm(string kind, float dur) { _model?.PlayArm(kind, dur); }   // (NEW) allies' cast poses

    private void ApplyWitch(int witch)
    {
        _witch = witch;
        _col = WitchModel.WitchColor(Mathf.Max(0, witch));
        if (IsInsideTree()) { BuildModel(); ApplyColor(); }
    }

    private void ApplyColor()
    {
        if (_silMat != null && !Downed) { _silMat.AlbedoColor = new Color(_col.R, _col.G, _col.B, 0.42f); _silMat.Emission = _col; }
        if (_tag != null && !Downed) { _tag.Text = $"Warden {_slot + 2}"; _tag.Modulate = _col.Lerp(Colors.White, 0.35f); }
    }

    public void SetDowned(bool d)
    {
        Downed = d;
        _model?.Collapse(d);
        if (_silMat != null)
        {
            _silMat.AlbedoColor = d ? new Color(1f, 0.2f, 0.2f, 0.6f) : new Color(_col.R, _col.G, _col.B, 0.42f);
            _silMat.Emission = d ? new Color(1f, 0.2f, 0.2f) : _col;
        }
        if (_tag != null)
        {
            _tag.Text = d ? $"Warden {_slot + 2} — DOWNED" : $"Warden {_slot + 2}";
            _tag.Modulate = d ? new Color(1f, 0.5f, 0.5f) : _col.Lerp(Colors.White, 0.35f);
        }
    }

    public void SetTarget(Vector3 pos, float yaw)
    {
        _target = pos; _targetYaw = yaw;
        if (!_have) { GlobalPosition = pos; Rotation = new Vector3(0, yaw, 0); _have = true; _prevPos = pos; }
    }

    public void Tick(float dt)
    {
        if (!_have) return;
        Vector3 prev = GlobalPosition;
        GlobalPosition = GlobalPosition.Lerp(_target, Mathf.Clamp(dt * 14f, 0f, 1f));
        float y = Mathf.LerpAngle(Rotation.Y, _targetYaw, Mathf.Clamp(dt * 12f, 0f, 1f));
        Rotation = new Vector3(0, y, 0);

        // procedural animation drive: horizontal speed (normalized) + airborne via surface height
        var mv = GlobalPosition - prev; mv.Y = 0f;
        float inst = mv.Length() / Mathf.Max(dt, 1e-4f);
        _speed01 = Mathf.Lerp(_speed01, Mathf.Clamp(inst / 9f, 0f, 1f), Mathf.Clamp(dt * 10f, 0f, 1f));
        bool airborne = false;
        if (Game.I != null)
        {
            float gy = Game.I.SurfaceHeight(GlobalPosition, GlobalPosition.Y);
            airborne = (GlobalPosition.Y - gy) > 0.35f;
        }
        if (_gust != null && _gust.Visible) _gust.RotateY(dt * 3f);   // Stormform: swirl the wind rings (NEW)
        if (!Downed) _model?.Animate(dt, _speed01, airborne);
    }
}
