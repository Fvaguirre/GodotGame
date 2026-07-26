using Godot;

// RitualCircle.cs — between-wave ritual events that grant spell-combo finishers. RiteType lists the rite flavors; Game spawns them.
public enum RiteType { Ward, Summon, Cleanse }

// A random map event. Dormant until the player steps inside, then runs to success or failure.
public partial class RitualCircle : Node3D
{
    public RiteType Type;
    public float Radius = 12f;   // (NEW) doubled — each skybeam rite needs a bigger arena to fight/ward in
    public bool Active = false;
    public bool Done = false;
    public int NetId = 0;
    public bool Remote = false;   // client ghost: visual only, host drives charge/activation
    public void SetRemoteState(bool active, float status) { Active = active; Status = status; }

    public float Charge = 0f;          // ward: 0..1
    public const float WardTime = 22f;   // (NEW) base fill time when the party is present (was 40 — solo crawled)

    private Enemy _summon;              // summon: the mini-boss to slay
    public const float SummonTime = 32f;

    private int _killStart = 0;        // cleanse: kills banked at start
    public int KillTarget = 12;
    public const float CleanseTime = 26f;

    public float TimeLeft = 0f;        // summon / cleanse countdown
    public float Status = 0f;          // generic 0..1 progress for the HUD

    private MeshInstance3D _pillar;
    private StandardMaterial3D _pm;
    private Decal _decal;              // (NEW) projected ground circle — conforms to terrain, never clips
    private Color _col;
    private float _spawnT = 0f;
    private float _age = 0f;
    public const float Lifespan = 60f;   // an ignored circle fades after this

    private Color TypeCol => Type switch
    {
        RiteType.Ward => DamageTypes.Col(DamageType.Lunar),
        RiteType.Summon => DamageTypes.Col(DamageType.Curse),
        _ => DamageTypes.Col(DamageType.Holy)
    };

    public int Killed => Game.I != null ? Game.I.Kills - _killStart : 0;

    // seconds until this event expires (0 = no active countdown, e.g. a Ward you're holding)
    public float SecondsLeft => !Active ? Mathf.Max(0f, Lifespan - _age)
                              : (Type == RiteType.Ward ? 0f : Mathf.Max(0f, TimeLeft));

    public override void _Ready()
    {
        var c = TypeCol;
        _col = c;

        // (NEW) a decal projected onto the terrain — conforms to hills instead of clipping like the old flat disc.
        _decal = new Decal
        {
            TextureAlbedo = Game.FieldTex(),
            TextureEmission = Game.FieldTex(),
            EmissionEnergy = 1.4f,
            Modulate = new Color(c.R, c.G, c.B, 0.85f),
            Size = new Vector3(Radius * 2f, Mathf.Max(8f, Radius * 1.5f), Radius * 2f)
        };
        AddChild(_decal);

        _pillar = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = Radius * 0.92f, BottomRadius = Radius * 0.92f, Height = 18f } };
        _pm = Game.Emissive(c, 1.0f);
        _pm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        var pc = _pm.AlbedoColor; pc.A = 0.05f; _pm.AlbedoColor = pc;
        _pillar.MaterialOverride = _pm;
        _pillar.Position = new Vector3(0, 9, 0);
        AddChild(_pillar);

        AddChild(new OmniLight3D { OmniRange = Radius * 3f, LightColor = c, LightEnergy = 1.6f, Position = new Vector3(0, 3, 0) });
    }

    public override void _Process(double delta)
    {
        if (Done) return;
        if (Game.I == null || !Game.I.SimActive)
        {
            if (_wardSounding) { _wardSounding = false; Game.I?.Sfx?.WardChargeStop(); }   // (NEW) don't leave the drone humming through a pause / game-over
            return;
        }
        float dt = (float)delta;

        if (Remote)
        {
            // client ghost: host owns charge/activation; just reflect synced status visually
            if (Active) { if (_decal != null) _decal.EmissionEnergy = 1.4f + Status * 2f; }
            else { if (_decal != null) _decal.EmissionEnergy = 1.2f + 0.6f * Mathf.Sin(Time.GetTicksMsec() * 0.004f); }
            UpdateWardSound();   // (NEW) drone follows the synced fill for this client's player too
            return;
        }

        _age += dt;   // (NEW) NO expiry — rituals persist the whole time you're on a world; they clear only on complete/fail or when you leave for a new map

        int insideCount = Game.I.PlayersInRange(GlobalPosition, Radius);
        bool inside = insideCount > 0;

        if (!Active)
        {
            float pulse = 1.2f + 0.6f * Mathf.Sin(Time.GetTicksMsec() * 0.004f);
            if (_decal != null) _decal.EmissionEnergy = pulse;
            // (NEW) no longer auto-starts on walk-in — the party must walk up + hold E (Game.TryActivateRitual); the pulse just invites them
            return;
        }

        switch (Type)
        {
            case RiteType.Ward:
            {
                int wc = Mathf.Max(1, Game.I.WardenCount);                          // (NEW) party size
                if (inside) Charge += ((float)insideCount / wc) * dt / WardTime;    // (NEW) fills in ~WardTime whenever the party is present, regardless of size — solo now takes ~22s instead of 40
                else Charge -= dt * 0.12f / WardTime;                               // bleeds slowly if empty
                Charge = Mathf.Clamp(Charge, 0f, 1f);
                Status = Charge;
                if (_decal != null) _decal.EmissionEnergy = 1.4f + Charge * 2f;
                UpdateWardSound();   // (NEW) charge drone rises with the fill while you stand in it
                _spawnT -= dt;
                if (_spawnT <= 0f) { for (int i = 0; i < wc; i++) Game.I.SpawnAdd(); _spawnT = 1.3f; }   // (NEW) bombardment scales with party (solo = 1 add/tick)
                if (Charge >= 1f) Succeed();
                break;
            }

            case RiteType.Summon:
                TimeLeft -= dt;
                Status = Mathf.Clamp(TimeLeft / SummonTime, 0f, 1f);
                if (_summon == null || !GodotObject.IsInstanceValid(_summon) || _summon.Dead) { Succeed(); break; }
                if (TimeLeft <= 0f) Fail();
                break;

            case RiteType.Cleanse:
                TimeLeft -= dt;
                Status = Mathf.Clamp(TimeLeft / CleanseTime, 0f, 1f);
                if (Killed >= KillTarget) Succeed();
                else if (TimeLeft <= 0f) Fail();
                break;
        }
    }

    // (NEW) the warding drone — only the WARD rite is "stand in it to charge", so only it hums. Plays for the local player
    // whenever they're inside (with a little grace past the rim); pitch/volume swell with the fill. Stops on leave/end.
    private bool _wardSounding = false;
    private void UpdateWardSound()
    {
        var pl = Game.I?.Player;
        bool play = Type == RiteType.Ward && Active && !Done && pl != null
                    && new Vector2(GlobalPosition.X - pl.GlobalPosition.X, GlobalPosition.Z - pl.GlobalPosition.Z).Length() <= Radius * 1.25f;
        if (play) { Game.I.Sfx?.WardCharge(Status); _wardSounding = true; }
        else if (_wardSounding) { _wardSounding = false; Game.I?.Sfx?.WardChargeStop(); }
    }
    public override void _ExitTree() { if (_wardSounding) { _wardSounding = false; Game.I?.Sfx?.WardChargeStop(); } }

    // (NEW) souls to begin, scaling with the wave it appears at — shown in the hold-E prompt; the ACTIVATOR pays (souls are per-player)
    public int ActivationCost => 10 * Mathf.Max(1, Game.I != null ? Game.I.Wave : 1);
    // begin the rite — host/solo authoritative. Payment is handled by the caller (Game.TryActivateRitual), since souls are per-player.
    public void BeginRite()
    {
        if (Active || Done || Remote) return;
        Active = true;
        _age = 0f;   // (NEW) reset the expiry clock the moment the rite begins
        switch (Type)
        {
            case RiteType.Summon:
                TimeLeft = SummonTime + (Mathf.Max(1, Game.I.WardenCount) - 1) * 3f;   // (NEW) a little longer for bigger parties (tougher scaled boss)
                _summon = Game.I.SpawnBossAt("miniboss", GlobalPosition);
                break;
            case RiteType.Cleanse:
            {
                int wc = Mathf.Max(1, Game.I.WardenCount);                            // (NEW) scale kill goal + time with party size
                int per = Mathf.Min(9, 5 + Mathf.FloorToInt(Game.I.Wave * 0.35f));    // per-warden kills, grows slowly with wave
                KillTarget = Mathf.Clamp(per * wc, 4, 12 * wc);
                TimeLeft = CleanseTime + (wc - 1) * 4f;
                _killStart = Game.I.Kills;
                Game.I.SpawnCleanseHorde(GlobalPosition, KillTarget + 2);   // a couple spare so it's always completable
                break;
            }
            default:
                break;
        }
        Game.I.AnnounceRite(1, (int)Type);
    }

    private void Succeed()
    {
        if (Done) return;
        Done = true;
        Game.I.AnnounceRite(2, (int)Type);
        Game.I.RitualReward(Type);
        Game.I.RemoveRitual(this);
        QueueFree();
    }

    private void Fail()
    {
        if (Done) return;
        Done = true;
        Game.I.AnnounceRite(3, (int)Type);   // banner + failure sound for all wardens; your run continues
        Game.I.RemoveRitual(this);
        QueueFree();
    }

    public void ForceSkip()   // (NEW) all players voted to skip this rite — end it cleanly (no reward), run continues
    {
        if (Done) return;
        Done = true;
        Game.I.Hud?.Banner("ritual skipped");
        Game.I.RemoveRitual(this);
        QueueFree();
    }
}
