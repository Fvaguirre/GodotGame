using Godot;

// Field.cs — a lingering ground AoE that ticks while units stand in it (FieldType: Heal, Hex, Moon).
// The owner simulates the real effect; its radius scales by the caster's S.SpellArea (set owner-side
// in _Ready; ghost copies use the broadcast radius so allies see the right size). Copy this file as
// the template for any new "drop a zone that does X over time" effect. See DEV_GUIDE.md §6.4.
public enum FieldType { Heal, Hex, Moon }

// A persistent ground circle that ticks an effect for a duration, then fades and frees.
public partial class GroundField : Node3D
{
    public FieldType Type = FieldType.Hex;
    public float Radius = 6f;
    public float Dur = 5f;
    public float Power = 1f;     // heal/sec or dmg/sec
    public float EnemyDmg = 0f;  // dmg/sec to enemies (used by Heal field too)
    public Player Src;
    public bool FromCombo = false;
    public int Cap = 0;          // 0 = uncapped; combo fields set this by rarity
    public Color? TintColor = null;   // override the type color (e.g. lunar light)
    public bool Beam = false;         // force the moonbeam shaft
    public DamageType DType = DamageType.Curse;
    public bool GrantsBlood = false;   // Crimson Pool: banks Blood Stacks for whoever stands in it
    public float SlowMul = 0f;         // >0 = slow enemies inside
    public float RotDps = 0f;          // >0 = apply spreading rot-bleed to enemies inside (Blood Rot)
    public float PoisonAdd = 0f;       // >0 = stack additive Nature poison on foes standing inside (Creeping Blight)
    public bool Remote = false;        // client visual copy
    public bool HealAllies = false;    // also heal nearby ally avatars over the network (rez beam)
    private bool _announced = false;
    private float _bloodTick = 0f;
    private float _poiTick = 0f;
    private float _t = 0f;
    private Decal _decal;              // (NEW) projected ground visual — conforms to terrain, never clips
    private Color _baseCol;

    private Color Tint => TintColor ?? (Type switch {
        FieldType.Heal => DamageTypes.Col(DamageType.Holy),
        FieldType.Moon => DamageTypes.Col(DamageType.Lunar),
        _ => DamageTypes.Col(DamageType.Curse) });

    public override void _Ready()
    {
        if (!Remote) Radius *= Game.I?.Player?.S.SpellArea ?? 1f;   // owner scales; ghosts already receive the scaled radius
        var col = Tint;
        _baseCol = col;
        // (NEW) a decal projected straight down onto the terrain — conforms to hills, so it can't clip like the old flat disc.
        _decal = new Decal
        {
            TextureAlbedo = Game.FieldTex(),
            TextureEmission = Game.FieldTex(),
            EmissionEnergy = 1.6f,
            Modulate = new Color(col.R, col.G, col.B, 0.9f),
            Size = new Vector3(Radius * 2f, Mathf.Max(8f, Radius * 1.5f), Radius * 2f)   // Y = projection depth (covers hilly ground)
        };
        AddChild(_decal);

        if (Type == FieldType.Moon || Beam)
        {
            // an actual shaft of moonlight beaming straight down into the circle
            var shaft = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = Radius * 0.55f, BottomRadius = Radius * 0.95f, Height = 16f } };
            var sm = Game.Emissive(col, 0.9f);
            sm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
            var scol = sm.AlbedoColor; scol.A = 0.12f; sm.AlbedoColor = scol;
            sm.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
            shaft.MaterialOverride = sm;
            shaft.Position = new Vector3(0, 8f, 0);
            AddChild(shaft);

            var down = new SpotLight3D
            {
                SpotRange = 18f, SpotAngle = 32f, LightColor = col, LightEnergy = 3.0f,
                Position = new Vector3(0, 15f, 0), RotationDegrees = new Vector3(-90, 0, 0)
            };
            AddChild(down);
            AddChild(new OmniLight3D { OmniRange = Radius * 2.5f, LightColor = col, LightEnergy = 1.2f, Position = new Vector3(0, 1.5f, 0) });
        }
    }

    public override void _Process(double delta)
    {
        if (Game.I == null || Game.I.State != GameState.Playing) return;
        float dt = (float)delta;
        _t += dt;

        if (Remote)   // client copy: just the visual + lifetime
        {
            float rfade = Mathf.Clamp(Dur - _t, 0, 1);
            if (_decal != null) { _decal.Modulate = new Color(_baseCol.R, _baseCol.G, _baseCol.B, 0.9f * rfade); _decal.EmissionEnergy = 0.5f + 1.6f * rfade; }
            if (_t >= Dur) QueueFree();
            return;
        }

        if (!_announced)   // tell allies to render a visual copy of this field (position is set by now)
        {
            _announced = true;
            GlobalPosition = new Vector3(GlobalPosition.X, Game.I.SurfaceHeight(GlobalPosition, 1e9f) + 0.05f, GlobalPosition.Z);   // sit on the (now hilly) terrain instead of clipping through it (NEW)
            Game.I.NetMgr?.BroadcastField((int)Type, GlobalPosition, Radius, Dur, Beam, Tint, (int)DType);
        }

        if (Type == FieldType.Heal)
        {
            var p = Game.I.Player;
            if (p != null && Flat(p.GlobalPosition) < Radius)
                p.Heal(Power * dt);
            if (Src != null && GodotObject.IsInstanceValid(Src) && Src.VerdantWitch)   // friendly heal mends the witch's ents
                foreach (var t in Src.Ents)
                    if (t != null && GodotObject.IsInstanceValid(t) && Flat(t.GlobalPosition) < Radius) t.Heal(Power * dt);
            if (HealAllies) Game.I.NetMgr?.HealAlliesNear(GlobalPosition, Radius, Power * dt);
            if (EnemyDmg > 0f)
            {
                foreach (var e in Game.I.Enemies.ToArray())
                {
                    if (e == null || e.Dead) continue;
                    if (Flat(e.GlobalPosition) < Radius) { e.Hurt(EnemyDmg * dt, DType, FromCombo); if (FromCombo) Game.I.Player?.ComboFromDot(); }
                }
                Game.I.DamageWorld(GlobalPosition, Radius, EnemyDmg * dt);   // (NEW) fields break props too
            }
        }
        else
        {
            foreach (var e in Game.I.Enemies.ToArray())
            {
                if (e == null || e.Dead) continue;
                if (Flat(e.GlobalPosition) < Radius) { e.Hurt(Power * dt, DType, FromCombo); if (FromCombo) Game.I.Player?.ComboFromDot(); if (SlowMul > 0f) e.Slow(0.6f, SlowMul); if (RotDps > 0f) e.Bleed(RotDps, 2.5f, true); }
            }
            Game.I.DamageWorld(GlobalPosition, Radius, Power * dt);   // (NEW) damaging fields break props too
        }

        if (GrantsBlood)
        {
            _bloodTick += dt;
            if (_bloodTick >= 0.8f)
            {
                _bloodTick = 0f;
                var pl = Game.I.Player;
                if (pl != null && Flat(pl.GlobalPosition) < Radius) pl.BloodReward(1f);   // local player: Crimson banks a stack, others mend
                Game.I.NetMgr?.BloodAlliesNear(GlobalPosition, Radius, 1f);                // (NEW) allies inside get it too — each translated to THEIR witch on their end
            }
        }

        if (PoisonAdd > 0f)   // Creeping Blight: keep stacking additive poison the longer they stand in it
        {
            _poiTick += dt;
            if (_poiTick >= 0.4f)
            {
                _poiTick = 0f;
                foreach (var e in Game.I.Enemies.ToArray())
                    if (e != null && !e.Dead && Flat(e.GlobalPosition) < Radius) { e.Poison(PoisonAdd, 2.5f); if (SlowMul > 0f) e.Slow(0.5f, SlowMul); }
            }
        }

        float fade = Mathf.Clamp(Dur - _t, 0, 1);
        if (_decal != null) { _decal.Modulate = new Color(_baseCol.R, _baseCol.G, _baseCol.B, 0.9f * fade); _decal.EmissionEnergy = 0.5f + 1.6f * fade; }
        if (_t >= Dur) QueueFree();
    }

    private float Flat(Vector3 v) => new Vector2(v.X - GlobalPosition.X, v.Z - GlobalPosition.Z).Length();
}
