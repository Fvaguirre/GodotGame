using Godot;

// Enemy.cs — every enemy in the game. Configure(type, wave) is the STAT TABLE (a switch on the type
// string setting MaxHp/Speed/Dmg/Score/Radius/Col/_behav + specials) and where HP/damage scaling is
// applied. EBehav (below) selects the per-frame AI (each value has a Move* method). Status effects
// (Bleed/Poison/Slow/Root/Mark/Knockback) pack into a bitmask synced to clients. Elite affixes live
// in MakeAffix (rolled in Game.SpawnEnemy). Body silhouette is chosen in _Ready (-> CreatureKind).
//
// MULTIPLAYER: enemies are real only on the host. On a client this object is a proxy (Remote = true)
// driven by EnemySnapshot; its Hurt() routes damage to the host via ReportHit and it NEVER Die()s
// locally. To add an enemy type see DEV_GUIDE.md §6.1 (don't forget the EnemyKinds table!).
public enum EBehav { Melee, Ranged, Charged, Flyer, Healer, Goblin, Boss, Zapper, Bomber, Diver, Hexer, Totem, Sapper }

public partial class Enemy : Node3D
{
    public float Hp, MaxHp, Speed, Dmg, Radius;
    public int Score;
    public Color Col;
    public bool Dead = false;
    public bool Elite = false;
    public bool IsBoss = false;
    public bool IsGoblin = false;
    public string Label = "";
    public int NetId = 0;      // host-assigned id for multiplayer sync
    public int TypeIdx = 0;    // index into EnemyKinds table for client-side rendering

    // ---- elite affixes (0 none,1 shielded,2 frenzied,3 vampiric,4 volatile,5 armored) ----
    public int Affix = 0;
    private float _armorDR = 0f;          // flat damage reduction (armored affix / sentinel); CRITS bypass it
    private float _shield = 0f, _shieldMax = 0f;   // shielded affix soak pool (host)
    private bool _shieldUp = false;       // client mirror of shield-active (status bit 64)
    private float _affixTick = 0f;
    private MeshInstance3D _affixAura, _shieldBubble;
    // archetype state
    private bool _splitter = false;
    private float _diveCd = 0f, _diveT = 0f; private bool _diving = false;   // diver
    private float _hexCd = 0f, _hexTele = 0f; private Vector3 _hexTarget;    // hexer
    private float _totemTick = 0f;                                          // totem
    private float _hasteT = 0f;                                             // totem haste buff timer (on the buffed enemy)
    private static Color AffixCol(int a) => a switch {
        1 => new Color(0.4f, 0.8f, 1f), 2 => new Color(1f, 0.35f, 0.2f), 3 => new Color(0.3f, 1f, 0.5f),
        4 => new Color(1f, 0.6f, 0.1f), 5 => new Color(0.72f, 0.72f, 0.8f), _ => Colors.White };

    public void SetAffix(int a) { Affix = a; }   // client visual only — host owns the mechanics

    // small nameplate text shown by the HUD near the health bar (affix and/or special archetype)
    public string PlateText()
    {
        string a = Affix switch { 1 => "Shielded", 2 => "Frenzied", 3 => "Vampiric", 4 => "Volatile", 5 => "Armored", _ => "" };
        string t = _type switch { "sentinel" => "Sentinel", "diver" => "Diver", "hexer" => "Hexer", "splitter" => "Splitter", "totem" => "Empowerer", _ => "" };
        if (a.Length > 0 && t.Length > 0) return a + " " + t;
        return a.Length > 0 ? a : t;
    }
    public Color PlateColor() => Affix > 0 ? AffixCol(Affix) : new Color(0.86f, 0.86f, 0.96f);

    public void MakeAffix(int a)
    {
        if (IsBoss || IsGoblin || a <= 0) return;
        Affix = a;
        MaxHp *= 1.25f; Hp = MaxHp; Score = Mathf.RoundToInt(Score * 1.5f);
        switch (a)
        {
            case 1: _shieldMax = MaxHp * 0.5f; _shield = _shieldMax; break;          // shielded
            case 2: Speed *= 1.45f; Dmg *= 1.35f; _boltDmg *= 1.35f; break;          // frenzied
            case 5: _armorDR = Mathf.Max(_armorDR, 0.35f); break;                    // armored (crit bypasses)
        }
    }
    public bool Remote = false;          // true on a client: host drives position, damage reports to host
    private Vector3 _remoteTarget;
    private bool _haveRemote = false;
    private Vector3 _tgt;       // nearest player's position this frame (host or ally)
    private long _tgtPeer = 0;  // 0 = local host player; otherwise the ally's peer id
    private bool _tgtIsMinion = false;  // true = parked on a tree-ent; the ent's owner deals its damage, so don't hit a player
    public void SetRemoteTarget(Vector3 p) { _remoteTarget = p; if (!_haveRemote) { GlobalPosition = p; _haveRemote = true; } }
    private void HitTarget(float dmg)
    {
        if (_tgtIsMinion) return;   // the ent takes contact damage on its owner's machine; no player here
        if (_tgtPeer == 0) { var pl = Game.I.Player; if (pl != null) pl.Hurt(dmg, GlobalPosition); }
        else Game.I.NetMgr?.DamagePlayer(_tgtPeer, dmg);
        if (_type == "swarmer") { if (_tgtPeer == 0) Game.I.Player?.SlowMe(1.2f, 0.6f); else Game.I.NetMgr?.SlowPlayer(_tgtPeer, 1.2f, 0.6f); Game.I.Sfx?.ZombieAttack(GlobalPosition); }   // (NEW) swarmer hits slow you + attack snarl   // route to the ally who's being hit
    }

    // status effects
    public float SlowT = 0f;
    private float _bleedT = 0f, _bleedDps = 0f, _bleedTick = 0f;
    private float _poiT = 0f, _poiDps = 0f, _poiTick = 0f;        // Verdant poison ivy: additive DoT + slow
    private int _poiOwner = 1, _bleedOwner = 1;                   // (NEW) caster peer for DoT combo attribution
    public bool IsPoisoned => _poiT > 0f;
    public void Poison(float addDps, float dur, int owner = 0)
    {
        if (Remote) { Game.I.NetMgr?.ReportStatus(NetId, 4, addDps, dur, 0f); return; }   // (NEW) route a client's poison to the host (owner = sender there)
        _poiDps = Mathf.Min(_poiDps + addDps, 60f);   // additive stacks, capped so it can't run away
        _poiT = Mathf.Max(_poiT, dur);
        _poiOwner = owner != 0 ? owner : (Game.I != null ? Game.I.LocalPeer : 1);   // (NEW) caster peer (host-applied = host)
    }
    private bool _bleedRot = false;
    private float _rotBubT = 0f;
    private bool _rotShow = false;     // client mirror of the rot state (status bit 32)

    private void SpawnRotBubble()
    {
        if (Game.I == null) return;
        var c = DamageTypes.Col(DamageType.Blood);
        for (int i = 0; i < 2; i++)   // denser so it reads on big models
        {
            float sz = 0.22f + GD.Randf() * 0.34f;
            var b = new MeshInstance3D { Mesh = new SphereMesh { Radius = sz, Height = sz * 2f }, MaterialOverride = Game.ToonEmissive(c, 2.8f, 0.05f) };
            Game.I.AddChild(b);
            float a = GD.Randf() * Mathf.Tau, rr = Radius * (0.7f + GD.Randf() * 0.55f);   // hug the silhouette edge so the body doesn't hide it
            float y0 = 0.3f + GD.Randf() * (Radius * 1.7f);                                 // spread up the full height, not just the feet
            b.GlobalPosition = GlobalPosition + new Vector3(Mathf.Cos(a) * rr, y0, Mathf.Sin(a) * rr);
            float rise = Radius * 1.6f + 1.6f;
            var tw = b.CreateTween(); tw.SetParallel(true);
            tw.TweenProperty(b, "position", b.GlobalPosition + new Vector3(0, rise, 0), 0.85f);   // rise above the head
            tw.TweenProperty(b, "transparency", 1f, 0.85f);
            tw.SetParallel(false);
            tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(b)) b.QueueFree(); }));
        }
    }
    private Vector3 _knock = Vector3.Zero;
    // Hurricane fling physics (host-authoritative; the arc syncs to clients via the enemy snapshot's Y) (NEW)
    private bool _thrown = false;             // airborne, physics-driven until it lands
    private Vector3 _throwVel = Vector3.Zero; // current 3D throw velocity
    private float _thrownT = 0f;              // seconds airborne this throw (landing grace)
    private float _tumbleX = 0f, _tumbleZ = 0f;   // per-throw ragdoll spin speeds (rad/s) on two axes (NEW)
    private bool _rThrown = false;                 // client proxy: mid-air tumbling (driven by networked throw/land events) (NEW)
    private float _getUpT = 0f, _getUpDur = 0f;   // after a hard landing: downed → rising stagger window (NEW)
    public bool Thrown => _thrown;            // query for the Hurricane ult
    private const float ThrowGravity = -26f, ThrowHurtSpeed = 9f, ThrowDmgPer = 2.4f;
    private float _popAccum = 0f, _popT = 0f;
    private Color _popCol = Colors.White;
    private bool _popAmp = false;
    private bool _popCrit = false;

    // push the enemy away from `from` (negative force pulls toward it)
    public void Knockback(Vector3 from, float force)
    {
        var d = GlobalPosition - from; d.Y = 0;
        if (d.LengthSquared() < 0.01f) d = Vector3.Forward;
        _knock += d.Normalized() * force * 6f;
    }

    // Hurricane fling — launch this enemy with a 3D velocity. Heavier (bigger) enemies are scaled down so
    // they're harder to pick up; bosses can't be flung (they just shrug + get a small nudge). The throw is
    // host-authoritative; the airborne arc reaches clients through the (now Y-bearing) enemy snapshot, and
    // fall damage is applied on landing in EndThrow, scaling with impact speed (i.e. fall height/force). (NEW)
    public void Fling(Vector3 velocity)
    {
        if (Remote || Dead) return;
        if (_behav == EBehav.Boss) { Knockback(GlobalPosition - velocity, 1.5f); return; }
        float mass = 0.85f + Radius * 0.4f;   // weight: heavies launch lower than light foes, but everyone gets real air now (was 0.6 + Radius, which barely budged big enemies) (NEW)
        _throwVel = velocity / mass;
        _knock = Vector3.Zero;             // a throw overrides any pending horizontal knockback
        _thrown = true; _thrownT = 0f;
        // ragdoll tumble: random spin on two axes, faster the harder the launch (NEW)
        float spin = Mathf.Clamp(_throwVel.Length() * 0.5f, 4f, 14f);
        _tumbleX = (GD.Randf() * 2f - 1f) * spin;
        _tumbleZ = (GD.Randf() * 2f - 1f) * spin;
        Game.I.NetMgr?.BroadcastEnemyThrow(NetId, _tumbleX, _tumbleZ);   // clients tumble their proxy to match (NEW)
    }

    // gentle radial pull toward a point (Cyclone). Host-authoritative; ignored on proxies + while thrown. (NEW)
    public void PullToward(Vector3 center, float step)
    {
        if (Remote || Dead || _thrown) return;
        Vector3 to = center - GlobalPosition; to.Y = 0; float d = to.Length();
        if (d < 1.0f) return;
        GlobalPosition += to.Normalized() * Mathf.Min(step, d - 0.8f);
    }

    // integrate the throw arc; land + apply fall damage when feet meet the ground while descending (NEW)
    private void UpdateThrown(float dt)
    {
        _thrownT += dt;
        _throwVel.Y += ThrowGravity * dt;
        Vector3 np = GlobalPosition + _throwVel * dt;
        float feet = np.Y - Radius;
        float ground = Game.I.SurfaceHeight(np, feet);
        if (feet <= ground && _thrownT > 0.05f && _throwVel.Y <= 0f)
        {
            float impact = -_throwVel.Y;   // descent speed at the moment of impact
            GlobalPosition = ClampArena(new Vector3(np.X, ground + Radius, np.Z));
            EndThrow(impact);
            return;
        }
        GlobalPosition = ClampArena(np);
        if (_creature != null) { _creature.RotateX(_tumbleX * dt); _creature.RotateZ(_tumbleZ * dt); }   // chaotic ragdoll tumble (NEW)
    }

    private void EndThrow(float impactSpeed)
    {
        _thrown = false; _throwVel = Vector3.Zero; _thrownT = 0f;
        if (impactSpeed > ThrowHurtSpeed && !Dead)
            Hurt((impactSpeed - ThrowHurtSpeed) * ThrowDmgPer, DamageType.Wind, false);   // harder/higher landing = more damage
        // real landings crash them onto the ground and leave them scrambling back up — a punish window. Trivial
        // tosses (and bosses/dead) just stand straight back up. (NEW)
        float gud = 0f;
        if (!Dead && _behav != EBehav.Boss && impactSpeed > 4f)
        {
            gud = Mathf.Clamp(0.4f + impactSpeed * 0.045f, 0.5f, 1.1f);
            _getUpDur = gud; _getUpT = gud;
            if (_creature != null)   // slam into a toppled pose (on its back/side); UpdateGetUp rights it
                _creature.Rotation = new Vector3(1.45f, _creature.Rotation.Y, GD.Randf() * 0.9f - 0.45f);
            Game.I.Sfx?.Impact(DamageType.Physical);
        }
        else if (_creature != null) _creature.Rotation = new Vector3(0, _creature.Rotation.Y, 0);   // clear the tumble pitch
        Game.I.NetMgr?.BroadcastEnemyLand(NetId, gud);   // clients play the same topple→get-up (gud>0) or just stand (NEW)
    }

    // CLIENT proxy: host says this enemy was flung — start tumbling. Position still follows the host arc. (NEW)
    public void RemoteThrowBegin(float tumbleX, float tumbleZ)
    {
        if (!Remote) return;
        _rThrown = true; _tumbleX = tumbleX; _tumbleZ = tumbleZ; _getUpT = 0f;
    }

    // CLIENT proxy: host says it landed — topple + rise (dur>0) or just stand back up (dur==0). (NEW)
    public void RemoteLand(float getUpDur)
    {
        if (!Remote) return;
        _rThrown = false;
        if (getUpDur > 0f)
        {
            _getUpDur = getUpDur; _getUpT = getUpDur;
            if (_creature != null) _creature.Rotation = new Vector3(1.45f, _creature.Rotation.Y, GD.Randf() * 0.9f - 0.45f);
            Game.I.Sfx?.Impact(DamageType.Physical);
        }
        else { _getUpT = 0f; if (_creature != null) _creature.Rotation = new Vector3(0, _creature.Rotation.Y, 0); }
    }

    // downed-and-rising: lerp the toppled creature back upright over the stagger window; AI stays suppressed (NEW)
    private void UpdateGetUp(float dt)
    {
        _getUpT -= dt;
        if (_creature != null)
        {
            float px = Mathf.LerpAngle(_creature.Rotation.X, 0f, dt * 9f);
            float pz = Mathf.LerpAngle(_creature.Rotation.Z, 0f, dt * 9f);
            _creature.Rotation = new Vector3(px, _creature.Rotation.Y, pz);
        }
        if (_getUpT <= 0f)
        {
            _getUpT = 0f;
            if (_creature != null) _creature.Rotation = new Vector3(0, _creature.Rotation.Y, 0);
        }
    }
    // bleed DoT; rot=true spreads to nearby enemies when this one dies
    public void Bleed(float dps, float dur, bool rot = false, int owner = 0)
    {
        if (Remote) { Game.I.NetMgr?.ReportStatus(NetId, 0, dps, dur, rot ? 1f : 0f); return; }
        _bleedDps = Mathf.Max(_bleedDps, dps);
        _bleedT = Mathf.Max(_bleedT, dur);
        if (rot) _bleedRot = true;
        _bleedOwner = owner != 0 ? owner : (Game.I != null ? Game.I.LocalPeer : 1);   // (NEW) caster peer
    }
    public float SlowMul = 0.45f;
    public float RootT = 0f;
    public float MarkT = 0f;
    // ===== FROST WITCH: freeze stacks → frozen (ice block) → shatter =====
    public float FreezeStacks = 0f;
    public float FrozenT = 0f;                       // >0 = encased in ice (a stun)
    // ---- Forsaken curse / tether ----
    public float CurseStacks = 0f;                   // (NEW) unique curse stacks the suck-beam built on THIS foe
    public float CurseT = 0f;                        // (NEW) remaining tether/curse time
    public int CurseGroup = 0;                       // (NEW) tether group id (0 = ungrouped)
    private bool _remoteCursed = false;              // (NEW) client-proxy cursed mirror
    public bool Cursed => Remote ? _remoteCursed : CurseT > 0f;
    private DamageType _curseBonusType = DamageType.Curse;
    private int _curseBonusType2 = -1;   // (NEW) optional 2nd bonus type from the Cursebrand legendary (-1 = none)
    private float _curseBonusMul = 1.35f, _curseShareFrac = 0.35f;
    private bool _curseShareGuard = false;           // guards the shared-damage broadcast against recursion
    // (NEW) per-frame ceilings on the two runaway fan-outs (curse-group share + Shatter Cascade chaining). A big shatter
    // into a large curse group is O(hits × groupSize) Hurt calls, each snapshotting the enemy list — that combinatorial
    // burst is what froze the game in MP. These caps bound the work per frame (self-reset via the process-frame counter).
    private static ulong _shareFrame; private static int _shareBudget; private static bool _shareWarned;
    private static ulong _cascFrame; private static int _cascBudget; private static bool _cascWarned;
    public float FreezeThreshold => Mathf.Clamp(1f + MaxHp / 120f, 1f, 240f) * 1.25f * _freezeThreshMul;   // +25% stacks to freeze (across the board); Brittle (best-of) still lowers it
    private float _freezeExpT = 0f;                  // stacks all expire together 2s after the last one
    private bool _radiatesCold = false;              // (NEW) only beam/shatter freezes radiate Deep Winter; ambient-frozen foes don't (no chain)
    private float _freezeThreshMul = 1f, _freezeDurBonus = 0f;   // (NEW) best-of frost profile accumulated from contributing witches
    private float _deepWinterT = 0f;                 // (NEW) Deep Winter spread throttle
    private float _frozenBlue = 0f, _frozenBlueMax = 0f, _frozenBlueDmg = 0f;   // temp blue bar while frozen
    private MeshInstance3D _iceBlock;
    private float _remoteBlueFrac = 1f;
    public float FrozenBlueFrac => Remote ? _remoteBlueFrac : (_frozenBlueMax > 0f ? Mathf.Clamp(_frozenBlue / _frozenBlueMax, 0f, 1f) : 0f);
    public bool Frozen => FrozenT > 0f;
    private void EnsureIceBlock(bool show)
    {
        if (show && _iceBlock == null)
        {
            _iceBlock = new MeshInstance3D { Mesh = new BoxMesh { Size = Vector3.One * Radius * 2.6f } };
            var m = Game.ToonEmissive(new Color(0.6f, 0.85f, 1f), 1.4f, 0f);
            m.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; m.AlbedoColor = new Color(0.7f, 0.9f, 1f, 0.4f); m.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
            _iceBlock.MaterialOverride = m;
            AddChild(_iceBlock);
        }
        if (_iceBlock != null) _iceBlock.Visible = show;
    }
    public float MarkAmp = 1f;
    public int MarkJumps = 0;

    private string _type = "shade";
    private EBehav _behav = EBehav.Melee;
    private float _touchCd = 0f;
    private float _atkCd = 0f;        // melee: cooldown before the next swing can begin
    private float _atkWind = 0f;      // melee: remaining wind-up before the strike connects
    private bool _swinging = false;   // melee: mid wind-up (holds position, telegraphs)
    private const float WindUpDur = 0.5f;
    private float _spin;
    private float _flyY = 5f;

    // ranged / special params
    private float _fireCd = 0f, _fireEvery = 2f, _range = 24f, _preferDist = 16f;
    private float _spawnSndT = 0.12f;                 // small delay so GlobalPosition is set before the spawn growl
    private static ulong _lastGrowlMs = 0, _lastShootMs = 0;   // global throttles so a wave-spawn burst isn't a wall of noise
    private static ulong _lastZombieMs = 0;                    // (NEW) global swarmer-groan throttle
    private bool _hadLos = false;                              // (NEW) swarmer: had line-of-sight last frame (excited/scream on gain)
    private bool _alerted = false;                             // (NEW) swarmer: awake + hunting (vs idle until it sees/hears you)
    private float _faceYaw = 0f;                               // (NEW) swarmer idle facing (randomized in Configure)
    private float _heard = 0f;                                 // (NEW) accumulated nearby noise (decays); drives look/investigate/aggro
    private Vector3 _soundPos;                                 // (NEW) last heard sound position
    private int _idlePose = 0;                                 // (NEW) 0 stand,1 lie,2 slump,3 snicker (synced to clients)
    private float _screamT = 0f;                               // (NEW) scream pulse (synced so clients play the shriek pose)
    private bool _screamWas = false;                           // (NEW) client edge-detect for the scream pulse
    private int _takerState = 0;                               // (NEW) Taker: 0 approach,1 charge,2 wall-stun,3 carrying
    private float _takerT = 0f, _boneT = 0f, _tickT = 0f;
    private Vector3 _chargeDir;
    private long _grabPeer = 0;
    private bool _growled = false;
    private float _chargeCd = 0f;    // (NEW) 7s between dash attempts (starts when the dash STOPS)
    private float _chargeDist = 0f;  // (NEW) distance travelled this dash
    private Vector3 _wanderDir;
    private float _wanderT = 0f;
    public bool IsTaker => _type == "taker";
    public bool IsSpecial => _type == "taker";   // (NEW) special enemies are capped at (players-1) total; add future specials here
    public Vector3 GraspPos => GlobalPosition + (_creature != null ? new Vector3(Mathf.Sin(_creature.Rotation.Y), 0, Mathf.Cos(_creature.Rotation.Y)) : Vector3.Forward) * (Radius + 0.5f) + new Vector3(0, 0.4f, 0);
    public bool IsSwarmer => _type == "swarmer";
    public bool Alerted => _alerted;
    public void Hear(Vector3 pos, float amount) { if (_alerted || _type != "swarmer") return; _heard = Mathf.Min(_heard + amount, 12f); _soundPos = pos; }   // additive; louder+closer = more
    // a projectile hit wakes an idle zombie's curiosity: face the source + investigate toward it (aggros on sight there)
    public void HitFrom(Vector3 from)
    {
        if (_type != "swarmer") return;
        if (Remote) { Game.I.NetMgr?.ReportHitFrom(NetId, from); return; }   // client's projectile → tell the host (owns the AI)
        if (_alerted) return;
        _heard = 8f; _soundPos = from;
        var d = from - GlobalPosition; d.Y = 0f;
        if (d.LengthSquared() > 0.01f) _faceYaw = Mathf.Atan2(d.X, d.Z);   // snap to face where it came from
    }
    private float _boltSpeed = 16f, _boltDmg = 8f, _boltRadius = 0.5f;
    private float _chargeDur = 0f, _chargeT = 0f;       // sieger telegraph
    private float _healEvery = 1.4f, _healCd = 0f, _healAmt = 6f;
    private float _strafe = 1f, _strafeT = 0f;

    private Creature _creature;
    private float _catchMul = 1f;   // distant enemies speed up to re-engage
    private StandardMaterial3D _mat;
    private float _flash = 0f, _baseEnergy;
    private float _hitSndT = 0f;   // (NEW) per-enemy throttle for the universal damage-tick sound (keeps DoTs a gentle pulse, not a machine-gun)
    private MeshInstance3D _markRing, _statusRing, _eliteRing;
    private MeshInstance3D _curseRing;              // (NEW) cursed ground ring
    private Godot.Label3D _curseLabel;              // (NEW) overhead curse-stacks counter
    private OmniLight3D _light;
    private MeshInstance3D _sentinelCore;           // (NEW) the Sentinel's exposed glowing weakpoint — hitting it auto-crits (bypasses the armor)

    public void Configure(string type, int wave)
    {
        _type = type;
        int sw = Mathf.Min(wave, 30);                                            // cap the exponent so very deep runs don't explode
        float hs  = Mathf.Pow(1.075f, sw) * (1f + Mathf.Max(0, wave - 30) * 0.10f);   // trash HP: ~+7.5%/wave compounding
        float bhs = Mathf.Pow(1.10f,  sw) * (1f + Mathf.Max(0, wave - 30) * 0.12f);   // bosses scale harder with depth
        float ds  = 1f + Mathf.Min(wave, 30) * 0.035f;                           // enemy DAMAGE rises gently with depth (was flat)
        switch (type)
        {
            case "wisp":   MaxHp = 7 * hs;  Speed = 8.6f; Dmg = 7;  Score = 14; Radius = 0.9f; Col = new Color(0.50f, 0.82f, 1.0f); _behav = EBehav.Melee; break;
            case "swarmer": MaxHp = 24 * hs; Speed = 6.8f; Dmg = 5f * ds; Score = 10; Radius = 0.95f; Col = new Color(0.42f, 0.5f, 0.32f); _behav = EBehav.Melee; _faceYaw = GD.Randf() * Mathf.Tau; break;   // maze zombie: fast, low dmg, slows on hit, swarms (NEW)
            case "taker": MaxHp = 260 * hs; Speed = 2.6f; Dmg = 6f * ds; Score = 90; Radius = 1.9f; Col = new Color(0.30f, 0.34f, 0.26f); _behav = EBehav.Melee; break;   // (NEW) big kidnapper: charges, grabs, carries a player off (MP only)
            case "brute":  MaxHp = 60 * hs; Speed = 2.6f; Dmg = 20; Score = 30; Radius = 2.2f; Col = Palette.Blood; _behav = EBehav.Melee; break;
            case "caster": MaxHp = 12 * hs; Speed = 5.0f; Dmg = 0;  Score = 20; Radius = 1.0f; Col = DamageTypes.Col(DamageType.Arcane); _behav = EBehav.Ranged;
                           _range = 26; _preferDist = 16; _fireEvery = 1.8f; _boltSpeed = 17; _boltDmg = 9; _boltRadius = 0.5f; break;
            case "sieger": MaxHp = 95 * hs; Speed = 2.2f; Dmg = 12; Score = 50; Radius = 2.0f; Col = DamageTypes.Col(DamageType.Ember); _behav = EBehav.Charged;
                           _range = 32; _preferDist = 22; _fireEvery = 3.4f; _chargeDur = 1.0f; _boltSpeed = 11; _boltDmg = 32; _boltRadius = 1.2f; break;
            case "flyer":  MaxHp = 10 * hs; Speed = 7.0f; Dmg = 4;  Score = 22; Radius = 0.75f; Col = new Color(0.8f, 0.85f, 1f); _behav = EBehav.Flyer;
                           _range = 26; _preferDist = 14; _fireEvery = 1.4f; _boltSpeed = 19; _boltDmg = 5; _boltRadius = 0.4f; _flyY = 5.5f; break;
            case "healer": MaxHp = 24 * hs; Speed = 4.2f; Dmg = 0;  Score = 30; Radius = 1.1f; Col = DamageTypes.Col(DamageType.Holy); _behav = EBehav.Healer;
                           _healEvery = 1.4f; _healAmt = 6f * hs; break;
            case "zapper": MaxHp = 16 * hs; Speed = 4.6f; Dmg = 0;  Score = 34; Radius = 1.0f; Col = new Color(0.55f, 0.8f, 1f); _behav = EBehav.Zapper;
                           _range = 34; _preferDist = 22; _fireEvery = 3.2f; break;
            case "bomber": MaxHp = 9 * hs;  Speed = 9.5f; Dmg = 34; Score = 18; Radius = 0.85f; Col = new Color(1f, 0.45f, 0.18f); _behav = EBehav.Bomber; break;
            case "sentinel": MaxHp = 140 * hs; Speed = 2.0f; Dmg = 22; Score = 60; Radius = 2.3f; Col = new Color(0.6f, 0.62f, 0.7f); _behav = EBehav.Melee; _armorDR = 0.55f; break;   // armored except crits
            case "diver":    MaxHp = 13 * hs;  Speed = 7.5f; Dmg = 16; Score = 28; Radius = 0.9f; Col = new Color(0.9f, 0.6f, 1f); _behav = EBehav.Diver; _range = 24; _preferDist = 12; _flyY = 6.5f; _diveCd = 2.2f; break;
            case "hexer":    MaxHp = 22 * hs;  Speed = 4.8f; Dmg = 0;  Score = 40; Radius = 1.0f; Col = DamageTypes.Col(DamageType.Curse); _behav = EBehav.Hexer; _range = 30; _preferDist = 20; _fireEvery = 3.6f; break;
            case "wardbane": MaxHp = 30 * hs;  Speed = 4.4f; Dmg = 8;  Score = 44; Radius = 1.1f; Col = new Color(0.6f, 0.3f, 0.85f); _behav = EBehav.Sapper; _range = 26; _preferDist = 17; _fireEvery = 4.0f; break;
            case "splitter": MaxHp = 40 * hs;  Speed = 3.4f; Dmg = 16; Score = 28; Radius = 1.6f; Col = new Color(0.5f, 0.85f, 0.4f); _behav = EBehav.Melee; _splitter = true; break;
            case "totem":    MaxHp = 70 * hs;  Speed = 0f;   Dmg = 0;  Score = 55; Radius = 1.4f; Col = new Color(1f, 0.8f, 0.35f); _behav = EBehav.Totem; break;
            case "spawnling":MaxHp = 8 * hs;   Speed = 6.5f; Dmg = 8;  Score = 6;  Radius = 0.7f; Col = new Color(0.5f, 0.85f, 0.4f); _behav = EBehav.Melee; break;
            case "goblin": MaxHp = 95 * bhs; Speed = 11.5f; Dmg = 0; Score = 0;  Radius = 1.0f; Col = new Color(1f, 0.84f, 0.3f); _behav = EBehav.Goblin; IsGoblin = true; Label = "LOOT GOBLIN"; break;
            case "miniboss": MaxHp = 680 * bhs; Speed = 3.0f; Dmg = 28; Score = 220; Radius = 3.0f; Col = new Color(0.62f, 0.30f, 0.85f); _behav = EBehav.Boss; IsBoss = true; Label = "MINI-BOSS";
                           _range = 30; _fireEvery = 2.4f; _boltSpeed = 15; _boltDmg = 16; _boltRadius = 0.7f; break;
            case "boss":   MaxHp = 4200 * bhs; Speed = 2.6f; Dmg = 40; Score = 800; Radius = 4.0f; Col = new Color(0.85f, 0.25f, 0.45f); _behav = EBehav.Boss; IsBoss = true; Label = "THE HOLLOW MOON";
                           _range = 36; _fireEvery = 2.0f; _boltSpeed = 16; _boltDmg = 22; _boltRadius = 0.9f; break;
            default:       MaxHp = 14 * hs; Speed = 4.0f; Dmg = 10; Score = 10; Radius = 1.3f; Col = new Color(0.54f, 0.47f, 0.84f); _behav = EBehav.Melee; break;
        }
        Dmg *= ds; _boltDmg *= ds;   // contact + projectile damage scale with depth (host-authoritative, so MP-consistent)
        // (NEW) named wave mutators: Blood Moon / Surge foes move faster (bosses excepted, they have their own pacing)
        if (!IsBoss && Game.I != null)
        {
            if (Game.I.ActiveMutator == WaveMutator.BloodMoon) Speed *= 1.3f;
            else if (Game.I.ActiveMutator == WaveMutator.Surge) Speed *= 1.18f;
        }
        float dmul = Game.I?.DirectorStatMul ?? 1f;   // enemy director: extra HP/damage when the party is dominating
        MaxHp *= dmul; Dmg *= dmul; _boltDmg *= dmul;
        int players = (Game.I != null) ? Game.I.WardenCount : 1;
        if (players > 1)
        {
            // bosses/goblins are shared single targets, so they scale harder than trash
            float hpScale = (IsBoss || IsGoblin) ? (1f + 0.70f * (players - 1)) : (1f + 0.30f * (players - 1));
            MaxHp *= hpScale;
        }
        Hp = MaxHp;
        _spin = (float)GD.RandRange(-2.0, 2.0);
        _strafe = GD.Randf() < 0.5f ? -1f : 1f;
        _fireCd = (float)GD.RandRange(0.4, _fireEvery);
    }

    public void MakeElite()
    {
        Elite = true;
        MaxHp *= 3.2f; Hp = MaxHp;
        Dmg *= 1.7f; _boltDmg *= 1.7f; _healAmt *= 1.7f;
        Speed *= 1.12f; Radius *= 1.18f;
        Score = Mathf.RoundToInt(Score * 3.0f) + (IsGoblin ? 0 : 8);
        Col = Col.Lerp(new Color(1f, 0.86f, 0.25f), 0.5f);
        if (Label != "" && !IsGoblin) Label = "ELITE " + Label;
        if (IsGoblin) Label = "ELITE GOBLIN";
    }

    // aggro spreads through the horde: an idle zombie that sees an already-woken zombie wakes too (throttled)
    private float _aggroScanT = 0f;
    private float _spawnAge = 0f;   // (NEW) per-zombie: can't aggro for its first seconds of life (kills insta-aggro on any spawn)
    private float _losLostT = 0f;   // (NEW) alerted: seconds since it last had sight of the target (de-aggro in phase 1)
    private bool SeesAlertedZombie(float dt)
    {
        _aggroScanT -= dt;
        if (_aggroScanT > 0f) return false;
        _aggroScanT = 0.3f;
        var list = Game.I.Enemies;
        for (int i = 0; i < list.Count; i++)
        {
            var o = list[i];
            if (o == null || o == this || o.Dead || !GodotObject.IsInstanceValid(o) || !o.IsSwarmer || !o.Alerted) continue;
            if (SeesTarget(o.GlobalPosition)) return true;   // reuse cone + range + LOS
        }
        return false;
    }

    // swarmer vision: target within a ~60° forward cone, in range, with clear line of sight
    private bool SeesTarget(Vector3 target)
    {
        if (Game.I.MazeGraceActive || _spawnAge < 2f) return false;   // (NEW) no waking during the maze grace OR this zombie's first 2s
        var to = target - GlobalPosition; to.Y = 0f; float d = to.Length();
        Vector3 eye = GlobalPosition + new Vector3(0, Radius + 0.4f, 0);
        Vector3 tgt = new Vector3(target.X, 1.2f, target.Z);
        if (d < 3f) return !Game.I.SightBlocked(eye, tgt);   // very close: sense unless a wall/object is actually between
        if (d > 14f) return false;               // vision range
        float facing = _creature != null ? _creature.Rotation.Y : 0f;
        var fdir = new Vector3(Mathf.Sin(facing), 0f, Mathf.Cos(facing));
        if (fdir.Dot(to / d) < 0.5f) return false;   // outside the forward cone
        return !Game.I.SightBlocked(eye, tgt);       // real wall/collidable occlusion (not just the grid)
    }

    // wake an idle swarmer (called by vision here, by the sound system in Batch 2, and by phase-2 wake-all)
    public void Alert()
    {
        if (_alerted || _type != "swarmer") return;
        _alerted = true;
        Game.I.Sfx?.ZombieExcited(GlobalPosition);
        if (GD.Randf() < 0.3f) { Game.I.Sfx?.ZombieScream(GlobalPosition); _creature?.Scream(); _screamT = 1f; }
    }
    public void WakeSilent() { if (_type == "swarmer") _alerted = true; }   // (NEW) endless-mode swarmers spawn already hunting, no aggro shout

    // ---- Taker: charge → grab → carry a player away (MP kidnapper) --------------------------------
    private void FaceMove(Vector3 dir, float rate)
    {
        if (_creature == null || dir.LengthSquared() < 0.001f) return;
        float yaw = Mathf.Atan2(dir.X, dir.Z);
        _creature.Rotation = new Vector3(0, Mathf.LerpAngle(_creature.Rotation.Y, yaw, Mathf.Clamp(rate, 0f, 1f)), 0);
    }

    private void TakerLogic(float dt, Vector3 target)
    {
        if (_grabPeer != 0 && _takerState != 3) _takerState = 3;   // holding a player → carry only (no charge, no punch)
        float moveAmt = 0f;
        if (_chargeCd > 0f) _chargeCd -= dt;
        switch (_takerState)
        {
            case 0:   // approach; punch in melee; wind up to charge if off cooldown
            {
                var pto = target - GlobalPosition; pto.Y = 0f; float pd = pto.Length();
                _takerT -= dt; if (_takerT <= 0f) { _takerT = 2.5f; Game.I.Sfx?.TakerGrunt(GlobalPosition); }
                if (pd < Radius + 2f)                                   // in reach → punch (works even on charge cooldown)
                {
                    FaceMove(pto, dt * 6f); MeleeAttack(dt, Radius + 2f); moveAmt = 0.15f;
                }
                else if (_chargeCd <= 0f && pd > 10f && pd < 20f && Game.I.MazeHasLoS(GlobalPosition, target) && GD.Randf() < dt * 1.2f)
                {
                    _takerState = 4; _takerT = 1.1f; _chargeDir = pto.Normalized();   // WIND UP (only at a good distance; not spammed off cooldown)
                    Game.I.Sfx?.TakerLaugh(GlobalPosition);
                }
                else                                                    // shamble toward the player
                {
                    var wp = Game.I.InMaze ? Game.I.MazeWaypoint(GlobalPosition, target) : target;
                    var to = wp - GlobalPosition; to.Y = 0f;
                    if (to.Length() > 0.6f && RootT <= 0f && FrozenT <= 0f) { GlobalPosition += to.Normalized() * Speed * dt; FaceMove(to, dt * 5f); moveAmt = 0.5f; }
                }
                break;
            }
            case 4:   // windup: rear back + telegraph, then dash
            {
                FaceMove(_chargeDir, dt * 4f); moveAmt = 0.1f;
                _takerT -= dt;
                if (_takerT <= 0f)
                {
                    _takerState = 1; _chargeDist = 0f;   // dash begins (travels ~18u); cooldown starts when it STOPS
                    float dist = 18f, dur = dist / (Speed * 4f);   // match the wind streak to his charge speed so it stays with him (not racing ahead)
                    Game.I.SpawnWindBullet(GlobalPosition, _chargeDir, dist, dur);                                   // Wind Rush dash VFX (local)
                    Game.I.NetMgr?.BroadcastVfx(32, GlobalPosition, _chargeDir, dist, dur, DamageTypes.Col(DamageType.Wind));   // …and for allies
                    Game.I.Sfx?.WindRushBy(GlobalPosition); Game.I.Sfx?.TakerGrowl(GlobalPosition);
                }
                break;
            }
            case 1:   // charge: straight line, fast; grabs the FIRST player he touches, plows through zombies, or slams a wall
            {
                FaceMove(_chargeDir, dt * 6f); moveAmt = 1.3f;
                long peer;   // grab check FIRST — the first player he contacts is caught, not shoved
                if (Game.I.NetMgr != null && Game.I.NetMgr.Active) peer = Game.I.NetMgr.PlayerNear(GlobalPosition, Radius + 1.8f, out _);
                else peer = (Game.I.Player != null && new Vector2(Game.I.Player.GlobalPosition.X - GlobalPosition.X, Game.I.Player.GlobalPosition.Z - GlobalPosition.Z).Length() < Radius + 1.8f) ? Game.I.LocalPeer : 0;
                if (peer != 0) { GrabPlayer(peer); break; }

                float dd = Speed * 4f * dt;
                var next = GlobalPosition + _chargeDir * dd;
                if (Game.I.SightBlocked(GlobalPosition + Vector3.Up, next + Vector3.Up) || Game.I.BlockerAt(next, Radius * 0.6f))   // wall or tree/pillar → stunned 2s
                {
                    _takerState = 2; _takerT = 2f; _chargeCd = 7f;
                    var impact = GlobalPosition + _chargeDir * Radius;
                    Game.I.Sfx?.Thud(impact); Game.I.Sfx?.TakerGrunt(impact);   // thud + ouch grunt
                    Game.I.SpawnImpactMark(new Vector3(impact.X, 0.05f, impact.Z), Vector3.Up, null, DamageType.Physical, 1.5f);   // scuff on the ground
                    Game.I.SpawnDust(impact, _chargeDir);                                              // dust bursting out of the surface (local)
                    Game.I.NetMgr?.BroadcastVfx(40, impact, _chargeDir, 0f, 0f, Colors.White);          // …and for allies
                }
                else
                {
                    GlobalPosition = next; _chargeDist += dd;
                    ChargeShove();   // plow through — shove other zombies aside (players are grabbed, not shoved)
                    if (_chargeDist >= 18f) { _takerState = 0; _chargeCd = 7f; }   // ran the full dash, missed → cooldown starts
                }
                break;
            }
            case 2:   // wall-stun: fallen, helpless ~2s
                _takerT -= dt; if (_takerT <= 0f) _takerState = 0;
                break;
            case 3:   // carrying: flee from rescuers / wander, squeeze + tick
                CarryMove(dt); moveAmt = 0.35f; CarryTick(dt);
                break;
        }
        if (_creature != null) { _creature.IdlePose = _takerState == 2 ? 1 : 0; _creature.Animate(dt, _takerState == 2 ? 0f : moveAmt); }
    }

    // carry: move away from any OTHER player in line of sight (routing around corners); wander if none can see him
    private void CarryMove(float dt)
    {
        Vector3 away = Vector3.Zero; int seen = 0;
        var eye = GlobalPosition + new Vector3(0, Radius + 0.4f, 0);
        var grasp = GraspPos;
        void Consider(Vector3 ap)
        {
            if (ap.DistanceTo(grasp) < 1.6f) return;                              // that's the captive, not a rescuer
            if (Game.I.SightBlocked(eye, new Vector3(ap.X, 1.2f, ap.Z))) return;  // only flee from those he can see
            var d = GlobalPosition - ap; d.Y = 0f;
            if (d.LengthSquared() > 0.1f) { away += d.Normalized(); seen++; }
        }
        if (Game.I.Player != null) Consider(Game.I.Player.GlobalPosition);
        if (Game.I.NetMgr != null && Game.I.NetMgr.Active) foreach (var ap in Game.I.NetMgr.AllyPositions()) Consider(ap);

        Vector3 dir;
        if (seen > 0) dir = away.Normalized();
        else
        {
            _wanderT -= dt;
            if (_wanderT <= 0f || _wanderDir.LengthSquared() < 0.01f) { _wanderT = 2.5f + GD.Randf() * 2f; float a = GD.Randf() * Mathf.Tau; _wanderDir = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)); }
            dir = _wanderDir;
        }
        var goal = GlobalPosition + dir * 8f;
        var step = Game.I.InMaze ? Game.I.MazeStepToward(GlobalPosition, goal) : dir;   // corridor nav → naturally escapes corners/deadends
        if (step.LengthSquared() > 0.01f && RootT <= 0f && FrozenT <= 0f) { GlobalPosition += step * Speed * 0.55f * dt; FaceMove(step, dt * 4f); }
        else _wanderT = 0f;   // stuck → re-roll next frame
        if (Game.I.NetMgr != null && Game.I.NetMgr.Active && _grabPeer != Game.I.LocalPeer) Game.I.NetMgr.BroadcastGrabPos(_grabPeer, GraspPos);   // (NEW) host drives the captive's position
    }

    // during a charge the Taker plows through OTHER ZOMBIES; players are grabbed on contact, never shoved
    private void ChargeShove()
    {
        float r = Radius + 1.8f;
        foreach (var e in Game.I.Enemies.ToArray())
            if (e != null && e != this && !e.Dead && GodotObject.IsInstanceValid(e))
            {
                var d = e.GlobalPosition - GlobalPosition; d.Y = 0f;
                if (d.Length() < r + e.Radius && d.LengthSquared() > 0.01f) e.GlobalPosition += d.Normalized() * 0.5f;
            }
    }

    private void GrabPlayer(long peer)
    {
        _grabPeer = peer; _takerState = 3; _takerT = 0f; _boneT = 0f; _tickT = 0.6f; _chargeCd = 7f;   // capture ends the dash → cooldown starts
        Game.I.NetMgr?.BroadcastGrab(NetId, peer, true);
        if (peer == Game.I.LocalPeer && Game.I.Player != null) Game.I.Player.GrabbedBy = NetId;   // host's own player
        Game.I.Sfx?.TakerBone(GlobalPosition);
    }

    private void CarryTick(float dt)
    {
        _boneT -= dt; if (_boneT <= 0f) { _boneT = 0.9f + GD.Randf() * 0.6f; Game.I.Sfx?.TakerBone(GlobalPosition); }   // bone-break squeezes
        _tickT -= dt;
        if (_tickT <= 0f)
        {
            _tickT = 1f;
            float dmg = 5f + Game.I.Wave * 0.6f;   // scales with progression (≈ player level)
            if (_grabPeer == Game.I.LocalPeer) { Game.I.Player?.Hurt(dmg); if (Game.I.Player != null && Game.I.Player.Downed) ReleaseGrab(); }
            else Game.I.NetMgr?.DamagePlayer(_grabPeer, dmg);
        }
    }

    public void ReleaseGrab()
    {
        if (_grabPeer != 0)
        {
            Game.I.NetMgr?.BroadcastGrab(NetId, _grabPeer, false);
            if (_grabPeer == Game.I.LocalPeer && Game.I.Player != null) Game.I.Player.GrabbedBy = 0;
        }
        _grabPeer = 0; _takerState = 0;
    }

    public override void _Ready()
    {
        CreatureKind kind;
        if (_type == "boss") kind = CreatureKind.HollowBoss;   // THE HOLLOW MOON — bespoke half-orc/half-zombie w/ a hollow midsection
        else if (IsBoss || _type == "miniboss" || _type == "brute" || _type == "sieger") kind = CreatureKind.Orc;
        else if (_type == "sentinel") kind = CreatureKind.Orc;
        else if (_type == "flyer" || _type == "diver") kind = CreatureKind.Mosquito;
        else if (_type == "zapper") kind = CreatureKind.Zapper;
        else if (_type == "bomber") kind = CreatureKind.Bomber;
        else if (_type == "caster" || _type == "healer" || _type == "hexer" || _type == "totem" || _type == "wardbane") kind = CreatureKind.Spider;
        else if (_type == "swarmer" || _type == "taker") kind = CreatureKind.Zombie;   // (NEW) shambling zombie / big kidnapper
        else kind = CreatureKind.Goblin;   // shade / wisp / goblin-loot

        // two-tone palettes: orcs green->brown, goblins green->yellow, the rest neon for the synth look
        Color bodyC, limbC, accentC;
        if (IsBoss) { bodyC = Col; limbC = Col.Darkened(0.4f); accentC = Col.Lerp(Colors.White, 0.4f); }
        else if (_type == "swarmer") { bodyC = new Color(0.40f, 0.47f, 0.30f); limbC = new Color(0.26f, 0.30f, 0.20f); accentC = new Color(0.60f, 0.66f, 0.40f); }   // sickly zombie flesh (NEW)
        else switch (kind)
        {
            case CreatureKind.Orc:
                bodyC = new Color(0.32f, 0.52f, 0.24f); limbC = new Color(0.40f, 0.27f, 0.15f); accentC = new Color(0.90f, 0.86f, 0.62f); break;   // green torso, brown limbs, bone tusks
            case CreatureKind.Goblin:
                if (IsGoblin) { bodyC = Col; limbC = Col.Darkened(0.4f); accentC = Col.Lerp(Colors.White, 0.35f); }   // loot goblin keeps its gold
                else { bodyC = new Color(0.40f, 0.62f, 0.22f); limbC = new Color(0.66f, 0.66f, 0.16f); accentC = new Color(0.95f, 0.88f, 0.28f); }   // green -> yellow
                break;
            case CreatureKind.Spider:
                bodyC = new Color(0.80f, 0.18f, 0.72f); limbC = new Color(0.40f, 0.08f, 0.46f); accentC = new Color(1.0f, 0.45f, 0.95f); break;   // neon magenta
            case CreatureKind.Mosquito:
                bodyC = new Color(0.24f, 0.90f, 0.95f); limbC = new Color(0.10f, 0.42f, 0.52f); accentC = new Color(0.65f, 1.0f, 1.0f); break;   // neon cyan
            case CreatureKind.Bomber:
                bodyC = new Color(1.0f, 0.42f, 0.16f); limbC = new Color(0.48f, 0.16f, 0.07f); accentC = new Color(1.0f, 0.85f, 0.30f); break;   // hot orange
            case CreatureKind.Zapper:
                bodyC = new Color(0.42f, 0.70f, 1.0f); limbC = new Color(0.16f, 0.28f, 0.60f); accentC = new Color(0.72f, 0.96f, 1.0f); break;   // electric blue
            default:
                bodyC = Col; limbC = Col.Darkened(0.45f); accentC = Col.Lerp(Colors.White, 0.35f); break;
        }

        _mat = Game.ToonEmissive(bodyC, IsGoblin ? 0.7f : 0.35f, 0.045f);
        _baseEnergy = _mat.EmissionEnergyMultiplier;
        var limbMat = Game.Toon(limbC, 0.85f, 0.2f, 0.045f);
        var accentMat = Game.ToonEmissive(accentC, 1.6f, 0f);

        _creature = new Creature();
        AddChild(_creature);
        _creature.Build(kind, Radius, _mat, limbMat, accentMat);
        if (_type == "sentinel")   // (NEW) exposed glowing core on the chest — a bright, caged weak spot; hitting it auto-crits and melts the armor
        {
            float cs = Radius * 0.4f;
            var coreCol = new Color(1f, 0.5f, 0.12f);
            _sentinelCore = new MeshInstance3D { Mesh = new SphereMesh { Radius = cs, Height = cs * 2f }, MaterialOverride = Game.ToonEmissive(coreCol, 5.5f, 0f) };
            _sentinelCore.Position = new Vector3(0, Radius * 1.05f, Radius * 0.78f);   // front chest, protruding forward; child of the creature so it rides its facing
            _creature.AddChild(_sentinelCore);
            var cageMat = Game.Toon(new Color(0.10f, 0.10f, 0.12f), 0.9f, 0.35f, 0f);   // dark bars caging the core so it reads as an exposed weak point
            for (int b = 0; b < 3; b++)
            {
                var bar = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(cs * 2.4f, cs * 0.24f, cs * 0.24f) }, MaterialOverride = cageMat };
                bar.Position = new Vector3(0, (b - 1) * cs * 0.62f, cs * 0.55f);
                _sentinelCore.AddChild(bar);
            }
            _sentinelCore.AddChild(new OmniLight3D { OmniRange = Radius * 2.6f, LightColor = coreCol, LightEnergy = 1.8f });
            var cpulse = _sentinelCore.CreateTween(); cpulse.SetLoops();   // pulse so it draws the eye
            cpulse.TweenProperty(_sentinelCore, "scale", Vector3.One * 1.18f, 0.55f).SetTrans(Tween.TransitionType.Sine);
            cpulse.TweenProperty(_sentinelCore, "scale", Vector3.One * 0.9f, 0.55f).SetTrans(Tween.TransitionType.Sine);
        }
        if (_type == "swarmer" && _creature != null)
        {
            _creature.Rotation = new Vector3(0, _faceYaw, 0);   // (NEW) spread initial facing so a whole batch doesn't spot you at once
            float rp = GD.Randf(); _idlePose = rp < 0.45f ? 0 : (rp < 0.65f ? 1 : (rp < 0.82f ? 2 : 3));   // mostly standing; some lying/slumped/snickering
            _creature.IdlePose = _idlePose;
        }

        _light = new OmniLight3D { OmniRange = Radius * 4f, LightColor = Col, LightEnergy = IsGoblin ? 1.2f : 0.5f };
        AddChild(_light);

        if (Elite)
        {
            _eliteRing = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = Radius * 1.3f, OuterRadius = Radius * 1.5f } };
            _eliteRing.MaterialOverride = Game.Emissive(new Color(1f, 0.86f, 0.25f), 2.2f);
            _eliteRing.Position = new Vector3(0, -Radius * 0.8f, 0);   // flat ground ring at the feet (NEW: was upright + mid-body)
            AddChild(_eliteRing);
        }

        if (IsGoblin)
        {
            var snd = new AudioStreamPlayer3D { Stream = Sfx.ShimmerStream(), VolumeDb = -4f, MaxDistance = 60f, Autoplay = true };
            AddChild(snd);
        }
    }

    private bool _shimmerDone = false, _shimmerPrimed = false;   // (NEW) arrival shimmer: fire once when first seen
    private void ArrivalShimmer()
    {
        var col = new Color(0.78f, 0.56f, 1f);   // faint magical silver-violet
        Game.I.SpawnPollen(GlobalPosition + Vector3.Up * Radius, Radius * 1.4f, col, 6, 0.55f, net: false);
        var ring = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = Radius * 0.6f, OuterRadius = Radius * 0.9f } };
        var m = Game.ToonEmissive(col, 2f, 0f);
        m.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; m.AlbedoColor = new Color(col.R, col.G, col.B, 0.6f);
        ring.MaterialOverride = m; ring.Position = new Vector3(0, -Radius * 0.85f, 0); ring.Scale = new Vector3(0.4f, 1f, 0.4f);
        AddChild(ring);
        var tw = CreateTween(); tw.SetParallel(true);
        tw.TweenProperty(ring, "scale", new Vector3(1.5f, 1f, 1.5f), 0.4);
        tw.TweenProperty(m, "albedo_color", new Color(col.R, col.G, col.B, 0f), 0.4);
        tw.Chain().TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(ring)) ring.QueueFree(); }));
    }

    public override void _Process(double delta)
    {
        if (Dead) return;
        if (Game.I == null || !Game.I.WorldRunning) return;
        if (Game.I.State != GameState.Playing) return;   // freeze AI/animation/firing/DoTs while a menu or card screen is open (NEW)
        float dt = (float)delta;
        if (_hitSndT > 0f) _hitSndT -= dt;   // (NEW) damage-tick throttle (ticks for remote proxies too — this runs before the Remote return below)
        if (!_shimmerDone)   // (NEW) small "arrival" shimmer the first time the local camera catches this foe
        {
            var scam = Game.I.Player?.Cam;
            if (scam != null)
            {
                bool inView = scam.IsPositionInFrustum(GlobalPosition + Vector3.Up * Radius) && GlobalPosition.DistanceTo(scam.GlobalPosition) < 75f;
                if (!_shimmerPrimed) { _shimmerPrimed = true; _shimmerDone = inView; }   // spawned already in view → it poofed, no arrival shimmer
                else if (inView) { _shimmerDone = true; ArrivalShimmer(); }
            }
        }
        if (_spawnSndT > 0f)   // directional spawn alert: bosses roar; others growl (throttled + chance)
        {
            _spawnSndT -= dt;
            if (_spawnSndT <= 0f)
            {
                if (IsBoss) Game.I.Sfx?.BossRoar(GlobalPosition);
                else { ulong now = Time.GetTicksMsec(); if (now - _lastGrowlMs > 1600) { _lastGrowlMs = now; if (GD.Randf() < 0.55f) Game.I.Sfx?.EnemyGrowl(GlobalPosition); else Game.I.Sfx?.ZombieSnicker(GlobalPosition, false); } }   // ~one spawn growl/snarl per 1.6s across the whole horde
            }
        }

        // Blood Rot: bubbling crimson aura rises off affected enemies (host drives, client mirrors via status bit)
        _rotBubT -= dt;
        bool rotActive = Remote ? _rotShow : (_bleedT > 0f && _bleedRot);
        if (rotActive && _rotBubT <= 0f) { _rotBubT = 0.26f; SpawnRotBubble(); }

        if (Remote)
        {
            if (_bossCharging) { _bossChargeT -= dt; if (_bossChargeT <= 0f) _bossCharging = false; }   // run the attack-timer bar on the client proxy (NEW)
            if (IsBoss) _bossHeat = Mathf.MoveToward(_bossHeat, Mathf.Clamp(0.12f + 0.66f * (1f - Hp / MaxHp), 0f, 1f), dt * 0.5f);   // (NEW) HP-based heat estimate for the HUD
            // client-side ghost: follow the host's reported position; animate from that motion
            var prev = GlobalPosition;
            GlobalPosition = GlobalPosition.Lerp(_remoteTarget, Mathf.Clamp(dt * 16f, 0f, 1f));
            var mv = GlobalPosition - prev; mv.Y = 0;
            float moved = mv.Length();
            if (moved > 0.001f) Game.I.MaybeWaterTrail(GlobalPosition, GlobalPosition.Y - Radius, dt);   // proxy enemies ripple water on clients too (NEW)
            if (_creature != null)
            {
                if (_rThrown)   // networked ragdoll tumble while airborne — position follows the host arc above (NEW)
                {
                    _creature.RotateX(_tumbleX * dt); _creature.RotateZ(_tumbleZ * dt);
                }
                else if (_getUpT > 0f)   // networked topple → rise after landing (NEW)
                {
                    _getUpT -= dt;
                    float px = Mathf.LerpAngle(_creature.Rotation.X, 0f, dt * 9f);
                    float pz = Mathf.LerpAngle(_creature.Rotation.Z, 0f, dt * 9f);
                    _creature.Rotation = new Vector3(px, _creature.Rotation.Y, pz);
                    if (_getUpT <= 0f) { _getUpT = 0f; _creature.Rotation = new Vector3(0, _creature.Rotation.Y, 0); }
                }
                else
                {
                    if (moved > 0.001f)
                    {
                        float yaw = Mathf.Atan2(mv.X, mv.Z);
                        _creature.Rotation = new Vector3(0, Mathf.LerpAngle(_creature.Rotation.Y, yaw, dt * 8f), 0);
                    }
                    _creature.Animate(dt, Mathf.Clamp(moved / (dt * 6f + 1e-5f), 0f, 1.5f));
                }
            }
            if (_popT > 0f) _popT -= dt;
            if (_popAccum >= 1f && _popT <= 0f)
            {
                if (Game.I.DmgNumbers) { var pop = new DamagePopup(); Game.I.AddChild(pop); pop.Init(_popAccum, _popCol, PopupPos, _popCrit, false, _popAmp); }
                _popAccum = 0f; _popAmp = false; _popCrit = false; _popT = 0.28f;
            }
            if (_flash > 0) _flash -= dt;
            UpdateStatusVisual(dt);   // emission = Col + bleed/slow/root/mark tints & rings from synced status
            SeparateFromPlayers();    // keep client-side proxies out of this player's camera
            return;
        }

        if (SlowT > 0) SlowT -= dt;
        if (_popT > 0f) _popT -= dt;
        if (_popAccum >= 1f && _popT <= 0f)
        {
            if (Game.I.DmgNumbers && !Dead)
            {
                var pop = new DamagePopup();
                Game.I.AddChild(pop);
                pop.Init(_popAccum, _popCol, PopupPos, _popCrit, false, _popAmp);
            }
            Game.I.NetMgr?.BroadcastPopup(PopupPos, _popAccum, _popCol, _popCrit);
            _popAccum = 0f; _popAmp = false; _popCrit = false; _popT = 0.28f;
        }
        if (RootT > 0) RootT -= dt;
        if (FrozenT > 0f)   // (NEW) frozen countdown → shatter on expiry
        {
            FrozenT -= dt;
            if (_iceBlock != null) _iceBlock.RotationDegrees = new Vector3(0, _iceBlock.RotationDegrees.Y + dt * 20f, 0);
            if (!Remote && _radiatesCold && Game.I.Player != null && Game.I.Player.DeepWinter)   // (NEW legendary) chill neighbours toward freezing — only REAL freezes radiate (no cascade)
            {
                _deepWinterT -= dt;
                if (_deepWinterT <= 0f)
                {
                    _deepWinterT = 0.4f;
                    var dw = Game.I.Player;
                    foreach (var o in Game.I.Enemies.ToArray())
                        if (o != null && o != this && !o.Dead && !o.Frozen && !o.Remote && GodotObject.IsInstanceValid(o) && o.GlobalPosition.DistanceTo(GlobalPosition) < 7f)
                            o.AddFreeze(o.FreezeThreshold * 0.12f, dw != null ? dw.FreezeThreshMul : 1f, dw != null ? dw.FrostDurBonus : 0f, canRadiate: false);   // ambient chill can't itself spread further
                }
            }
            if (FrozenT <= 0f) ShatterFreeze();
        }
        else if (FreezeStacks > 0f)   // (NEW) stacks all expire together 2s after the last one
        {
            _freezeExpT -= dt;
            if (_freezeExpT <= 0f) { FreezeStacks = 0f; _freezeThreshMul = 1f; _freezeDurBonus = 0f; }
        }
        if (MarkT > 0) MarkT -= dt; else MarkAmp = 1f;
        if (CurseT > 0f) { CurseT -= dt; if (CurseT <= 0f) { CurseGroup = 0; CurseStacks = 0f; } }   // (NEW) curse fades → drop the tether + stacks
        if (_bleedT > 0f)
        {
            _bleedT -= dt; _bleedTick -= dt;
            if (_bleedTick <= 0f) { _bleedTick = 0.3f; if (!Dead) { Hurt(_bleedDps * 0.3f, DamageType.Blood, false); Game.I.AwardDotCombo(_bleedOwner); } }   // (NEW) DoT trickles combo to its caster
        }
        if (_poiT > 0f)
        {
            _poiT -= dt; _poiTick -= dt;
            SlowT = Mathf.Max(SlowT, 0.2f);   // poison ivy slows as long as it's ticking on them
            if (_poiTick <= 0f) { _poiTick = 0.4f; if (!Dead) { Hurt(_poiDps * 0.4f, DamageType.Nature, false); Game.I.AwardDotCombo(_poiOwner); } }   // (NEW) DoT trickles combo to its caster
            if (_poiT <= 0f) _poiDps = 0f;
        }
        if (_knock.LengthSquared() > 0.0001f)
        {
            GlobalPosition += _knock * dt;
            _knock = _knock.Lerp(Vector3.Zero, Mathf.Clamp(dt * 7f, 0f, 1f));
        }
        if (_flash > 0) _flash -= dt;
        if (_fireCd > 0) _fireCd -= dt;
        if (_touchCd > 0) _touchCd -= dt;
        if (_eliteRing != null) _eliteRing.RotateY(dt * 2f);   // spin flat-in-plane (NEW: was RotateZ → tumbled upright)

        UpdateStatusVisual(dt);

        if (_thrown) { UpdateThrown(dt); return; }   // airborne fling owns movement; skip AI + ground-follow (NEW)
        if (_getUpT > 0f) { UpdateGetUp(dt); return; }   // downed → rising; stay staggered + open (NEW)

        var p = Game.I.Player;
        if (p == null) return;
        _tgt = Game.I.ResolveEnemyTarget(GlobalPosition, _behav == EBehav.Melee, out _tgtPeer, out _tgtIsMinion);   // melee foes can peel onto ents
        Vector3 realTarget = _tgt;   // (NEW) the actual player/ent, before corridor retargeting (for vision + hunt speed)
        if (_behav == EBehav.Melee && _type != "taker")   // surround: melee foes aim at a distinct spot around the target (golden-angle) → attack from open sides
        {
            float ang = NetId * 2.3999632f;
            _tgt = realTarget + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * (Radius * 2f + 1.6f);
        }
        if (Game.I.InExpedition) _tgt = Game.I.ExpoNavTarget(GlobalPosition, _tgt, ((NetId * 2654435761u) % 1000u) / 1000f);   // route through doorways, fanned across the gap
        if (Game.I.InMaze) _tgt = Game.I.MazeWaypoint(GlobalPosition, _tgt);   // follow corridors instead of b-lining into hedges (NEW)
        if (_hasteT > 0f) _hasteT -= dt;
        float spdMul = ((RootT > 0 || FrozenT > 0f) ? 0f : (SlowT > 0 ? SlowMul : 1f)) * (_hasteT > 0f ? 1.4f : 1f) * (Game.I.InWater(GlobalPosition, GlobalPosition.Y - Radius) ? 0.7f : 1f);   // frozen/rooted → held in place; totem haste; hip-deep water wades them down (NEW)
        if (Affix == 3) { _affixTick -= dt; if (_affixTick <= 0f) { _affixTick = 0.8f; VampHeal(); } }   // vampiric
        float pdist = (_tgt - GlobalPosition).Length();
        // catch-up speed ONLY for enemies that close distance — never boost kiters/fleers (they'd outrun you forever)
        _catchMul = ((_behav == EBehav.Melee || _behav == EBehav.Bomber || _behav == EBehav.Boss) && pdist > 34f)
            ? Mathf.Min(4.5f, 1f + (pdist - 34f) * 0.11f) : 1f;

        // swarmer idle/alert (L4D-style): idle + facing a fixed direction until it SEES (or, Batch 2, HEARS) you
        bool swarmerIdle = false;
        if (_type == "swarmer" && !Remote)
        {
            _spawnAge += dt;
            _heard = Mathf.Max(0f, _heard - dt * 0.7f);   // noise fades
            if (!_alerted && (SeesTarget(realTarget) || SeesAlertedZombie(dt))) Alert();   // ONLY sight wakes them; sound just makes them investigate
            swarmerIdle = !_alerted;
            if (swarmerIdle)
            {
                if (_heard >= 3f)   // MEDIUM: shamble toward the sound, navigating corridors
                {
                    var step = Game.I.MazeStepToward(GlobalPosition, _soundPos);
                    if (step != Vector3.Zero)
                    {
                        _faceYaw = Mathf.Atan2(step.X, step.Z);
                        float spd = (RootT > 0 ? 0f : 1f);
                        GlobalPosition += step * Speed * 0.42f * spd * dt;   // slow shamble
                    }
                }
                else if (_heard >= 1f)   // LOW: look toward the sound
                {
                    var ld = _soundPos - GlobalPosition; ld.Y = 0f;
                    if (ld.LengthSquared() > 0.1f) _faceYaw = Mathf.Atan2(ld.X, ld.Z);
                }
            }
            if (_alerted)   // lost sight for a while → back to idle (phase 1); phase 2 they never let go
            {
                Vector3 eye2 = GlobalPosition + new Vector3(0, Radius + 0.4f, 0);
                bool los = !Game.I.SightBlocked(eye2, new Vector3(realTarget.X, 1.2f, realTarget.Z));
                if (los) _losLostT = 0f; else _losLostT += dt;
                if (!Game.I.MazeAggroPhase && _losLostT > 1.8f) { _alerted = false; _heard = 0f; _losLostT = 0f; }
                else
                {
                    float td = (realTarget - GlobalPosition).Length();
                    _catchMul = Mathf.Max(_catchMul, 1f + Mathf.Clamp(td - 10f, 0f, 30f) * 0.05f + (los ? 0f : 0.6f));   // hunt faster when far / can't see them
                }
            }
        }

        bool takerActive = false;
        if (_type == "taker" && !Remote)   // (NEW) the Taker runs its own charge/grab/carry state machine
        {
            if (!_growled) { _growled = true; Game.I.Sfx?.TakerGrowl(GlobalPosition); }
            TakerLogic(dt, realTarget);
            takerActive = true;
        }

        if (_creature != null && !takerActive)
        {
            if (swarmerIdle) { _creature.Rotation = new Vector3(0, Mathf.LerpAngle(_creature.Rotation.Y, _faceYaw, dt * 2f), 0); _creature.Animate(dt, 0f); }
            else
            {
                var fd = (_behav == EBehav.Melee && _type != "taker" ? realTarget : _tgt) - GlobalPosition; fd.Y = 0;   // face the PLAYER, not the surround slot (so hits land, not air)
                if (fd.LengthSquared() > 0.02f)
                {
                    float yaw = Mathf.Atan2(fd.X, fd.Z);
                    _creature.Rotation = new Vector3(0, Mathf.LerpAngle(_creature.Rotation.Y, yaw, dt * 8f), 0);
                }
                _creature.Animate(dt, spdMul);
            }
        }

        if (!Remote && _type == "swarmer")
        {
            if (_screamT > 0f) _screamT -= dt;
            ulong znow = Time.GetTicksMsec();
            if (znow - _lastZombieMs > 380 && GD.Randf() < 0.5f)
            {
                _lastZombieMs = znow;
                if (!_alerted && _idlePose == 3) Game.I.Sfx?.ZombieSnicker(GlobalPosition);   // snickering idlers chuckle
                else Game.I.Sfx?.ZombieGroan(GlobalPosition);
            }
        }

        if (!swarmerIdle && !takerActive)
        switch (_behav)
        {
            case EBehav.Melee: MoveMelee(p, dt, spdMul); break;
            case EBehav.Ranged: MoveRanged(p, dt, spdMul, false); break;
            case EBehav.Charged: MoveRanged(p, dt, spdMul, true); break;
            case EBehav.Flyer: MoveFlyer(p, dt, spdMul); break;
            case EBehav.Healer: MoveHealer(p, dt, spdMul); break;
            case EBehav.Goblin: MoveGoblin(p, dt, spdMul); break;
            case EBehav.Zapper: MoveZapper(p, dt, spdMul); break;
            case EBehav.Bomber: MoveBomber(p, dt, spdMul); break;
            case EBehav.Diver: MoveDiver(p, dt, spdMul); break;
            case EBehav.Hexer: MoveHexer(p, dt, spdMul); break;
            case EBehav.Sapper: MoveSapper(p, dt, spdMul); break;
            case EBehav.Totem: MoveTotem(p, dt, spdMul); break;
            case EBehav.Boss: if (!_bossCharging) MoveMelee(p, dt, spdMul * Mathf.Lerp(1f, 1.5f, _bossHeat)); BossFire(p, dt); break;   // freeze while telegraphing; hotter → faster (NEW)
        }

        // vertical: ground enemies follow the surface and climb ramps/walls toward an elevated player
        if (_behav != EBehav.Flyer && _behav != EBehav.Diver)
        {
            float feet = GlobalPosition.Y - Radius;
            float support = Game.I.SurfaceHeight(GlobalPosition, feet);
            float targetFeet = support;
            if (_tgt.Y > feet + 1.2f)   // player is up high — scale toward them
            {
                var hdir = _tgt - GlobalPosition; hdir.Y = 0;
                if (hdir.LengthSquared() > 0.01f) hdir = hdir.Normalized(); else hdir = Vector3.Forward;
                var ahead = GlobalPosition + hdir * (Radius + 1.6f);
                float deckAhead = Game.I.SurfaceHeight(ahead, 1e9f);   // tallest surface just ahead (ignores step limit)
                if (deckAhead > feet + 0.3f) targetFeet = Mathf.Clamp(deckAhead, support, Mathf.Max(support, _tgt.Y));   // ceiling is at least the support height, so min can never exceed max (fixes ThrowMinMaxException on tall decks) (NEW)
            }
            float newFeet = Mathf.MoveToward(feet, targetFeet, 9f * dt);
            GlobalPosition = new Vector3(GlobalPosition.X, newFeet + Radius, GlobalPosition.Z);
            Game.I.MaybeWaterTrail(GlobalPosition, GlobalPosition.Y - Radius, dt);   // enemy ripples while wading (NEW)
        }
        if (!(_type == "taker" && (_takerState == 1 || _takerState == 3)))   // Taker plows through while charging/carrying
        {
            SeparateFromPlayers();
            SeparateFromEnemies(dt);   // (NEW) spread the horde so bodies don't stack
        }
        if (Game.I.InExpedition && !(_type == "taker" && _takerState == 1)) ExpoWallClamp();   // charge handles walls itself (stun)
    }

    // Expedition-only solid collision: push out of cover pillars (Blockers) and walls (tall Decks),
    // mirroring the player's clamp so surge bodies route around obstacles instead of ghosting through
    // them. Low walkable pads (TopY < 1.8) and airborne foes (above a wall's top) are exempt.
    private void ExpoWallClamp()
    {
        var p = GlobalPosition;
        float m = Radius;
        foreach (var b in Game.I.Blockers)
        {
            float ox = p.X - b.Pos.X, oz = p.Z - b.Pos.Z;
            float dd = Mathf.Sqrt(ox * ox + oz * oz);
            float rr = b.Radius + m * 0.8f;
            if (dd < rr) { float k = rr / Mathf.Max(dd, 0.001f); p.X = b.Pos.X + ox * k; p.Z = b.Pos.Z + oz * k; }
        }
        foreach (var d in Game.I.Decks)
        {
            if (d.TopY < 1.8f) continue;                       // walkable pad, not a wall
            if (GlobalPosition.Y >= d.TopY - 0.6f) continue;   // on/above the top (e.g. a flyer) — don't body-block
            float ex = d.Half.X + m, ez = d.Half.Y + m;
            float dx = p.X - d.Center.X, dz = p.Z - d.Center.Z;
            if (Mathf.Abs(dx) < ex && Mathf.Abs(dz) < ez)
            {
                if (ex - Mathf.Abs(dx) < ez - Mathf.Abs(dz)) p.X = d.Center.X + Mathf.Sign(dx) * ex;
                else p.Z = d.Center.Z + Mathf.Sign(dz) * ez;
            }
        }
        GlobalPosition = new Vector3(p.X, GlobalPosition.Y, p.Z);
    }

    private void MoveMelee(Player p, float dt, float spdMul)
    {
        float reach = Radius + 1.4f;
        if (MeleeAttack(dt, reach)) return;   // winding up — hold position so the swing reads as a telegraph
        Vector3 to = _tgt - GlobalPosition; to.Y = 0;
        float dist = to.Length();
        if (dist > reach && spdMul > 0f)
        {
            float sp = Speed * _catchMul * spdMul;
            if (_type == "swarmer" && Game.I.Player != null) sp *= Game.I.Player.MoveSpeedFactor;   // (NEW) keep pace with a fast player
            GlobalPosition += to.Normalized() * sp * dt;
        }
    }

    // Telegraphed melee swing (melee + bosses): once in reach, rear back over a wind-up, then strike.
    // The hit only lands if the target is STILL in reach when the swing connects, giving a dodge window.
    // Returns true while winding up so the caller holds position. HitTarget self-guards minion targets.
    private bool MeleeAttack(float dt, float reach)
    {
        if (Dmg <= 0f) return false;
        if (_atkCd > 0f) _atkCd -= dt;
        float dist = new Vector2(_tgt.X - GlobalPosition.X, _tgt.Z - GlobalPosition.Z).Length();
        if (_swinging)
        {
            _atkWind -= dt;
            _creature?.SetSwing(Mathf.Clamp(1f - _atkWind / WindUpDur, 0f, 1f));
            if (_atkWind <= 0f)
            {
                _swinging = false;
                _creature?.SetSwing(0f);
                _creature?.Strike();
                if (dist < reach + 0.7f) HitTarget(Dmg);
                _atkCd = 1.0f;
            }
            return true;
        }
        if (dist < reach && _atkCd <= 0f)
        {
            _swinging = true; _atkWind = WindUpDur;
            ulong now = Time.GetTicksMsec();
            if (now - _lastGrowlMs > 200) { _lastGrowlMs = now; Game.I.Sfx?.EnemyGrowl(GlobalPosition); }   // audible tell
            return true;
        }
        return false;
    }

    // Keep enemies out of players' bodies so they can't walk through you or clip the camera. Runs on
    // every machine (real enemies host-side, proxies client-side) so each player's own camera is safe.
    private void SeparateFromPlayers()
    {
        if (_behav == EBehav.Flyer || _behav == EBehav.Diver) return;   // airborne — don't shove off the player's column
        var pl = Game.I.Player;
        if (pl != null) PushOutOfBody(pl.GlobalPosition);
        var net = Game.I.NetMgr;
        if (net != null && net.Active)
            foreach (var ap in net.AllyPositions()) PushOutOfBody(ap);
    }

    // (NEW) soft mutual repulsion so the horde spreads around the target instead of stacking/clipping into one body
    private void SeparateFromEnemies(float dt)
    {
        if (_behav == EBehav.Flyer || _behav == EBehav.Diver || Game.I == null) return;
        float px = 0f, pz = 0f;
        var list = Game.I.Enemies;
        for (int i = 0; i < list.Count; i++)
        {
            var o = list[i];
            if (o == null || o == this || o.Dead || !GodotObject.IsInstanceValid(o)) continue;
            float md = (Radius + o.Radius) * 0.9f;   // allow gentle overlap so they can pack in
            float ox = GlobalPosition.X - o.GlobalPosition.X, oz = GlobalPosition.Z - o.GlobalPosition.Z;
            float dd = ox * ox + oz * oz;
            if (dd < md * md && dd > 0.0001f) { float d = Mathf.Sqrt(dd); float f = (md - d) / md; px += ox / d * f; pz += oz / d * f; }
        }
        var push = new Vector3(px, 0f, pz);
        if (push.LengthSquared() > 0.0001f) GlobalPosition += push.LimitLength(2.5f * dt);   // < seek speed → seek wins, no gridlock
    }
    private void PushOutOfBody(Vector3 c)
    {
        float minD = Radius + 0.9f;
        float ox = GlobalPosition.X - c.X, oz = GlobalPosition.Z - c.Z;
        float dd = Mathf.Sqrt(ox * ox + oz * oz);
        if (dd > minD) return;
        if (dd < 0.0001f) { GlobalPosition = new Vector3(c.X + minD, GlobalPosition.Y, c.Z); return; }
        float k = minD / dd;
        GlobalPosition = new Vector3(c.X + ox * k, GlobalPosition.Y, c.Z + oz * k);
    }

    private void MoveRanged(Player p, float dt, float spdMul, bool charged)
    {
        Vector3 to = _tgt - GlobalPosition; to.Y = 0;
        float dist = to.Length();
        Vector3 dir = dist > 0.01f ? to.Normalized() : Vector3.Forward;
        Vector3 want = Vector3.Zero;
        if (dist > _preferDist + 2f) want += dir;                 // close in
        else if (dist < _preferDist - 2f) want -= dir;            // back off
        _strafeT -= dt; if (_strafeT <= 0) { _strafe = -_strafe; _strafeT = (float)GD.RandRange(1.2, 2.4); }
        want += new Vector3(-dir.Z, 0, dir.X) * _strafe * 0.6f;   // strafe
        if (want.LengthSquared() > 0.001f && spdMul > 0f)
        {
            var np = GlobalPosition + want.Normalized() * Speed * _catchMul * spdMul * dt;
           
            GlobalPosition = ClampArena(np);
        }
        if (Dmg > 0 && dist < Radius + 1.4f && _touchCd <= 0f) { HitTarget(Dmg); _touchCd = 0.7f; }

        // fire
        if (charged)
        {
            if (_chargeT > 0f) { _chargeT -= dt; if (_chargeT <= 0f) FireAt(p, _boltSpeed, _boltDmg, _boltRadius); }
            else if (_fireCd <= 0f && dist < _range) { _chargeT = _chargeDur; _fireCd = _fireEvery; }
        }
        else if (_fireCd <= 0f && dist < _range) { FireAt(p, _boltSpeed, _boltDmg, _boltRadius); _fireCd = _fireEvery; }
    }

    private void MoveFlyer(Player p, float dt, float spdMul)
    {
        Vector3 to = _tgt - GlobalPosition; to.Y = 0;
        float dist = to.Length();
        Vector3 dir = dist > 0.01f ? to.Normalized() : Vector3.Forward;
        Vector3 want = new Vector3(-dir.Z, 0, dir.X) * _strafe;   // orbit the player
        if (dist > _preferDist + 3f) want += dir * 0.8f; else if (dist < _preferDist - 3f) want -= dir * 0.8f;
        _strafeT -= dt; if (_strafeT <= 0) { _strafe = -_strafe; _strafeT = (float)GD.RandRange(0.8, 1.8); }
        if (want.LengthSquared() > 0.001f && spdMul > 0f)
        {
            var np = GlobalPosition + want.Normalized() * Speed * _catchMul * spdMul * dt;
            np.Y = _tgt.Y + 3.6f + Mathf.Sin(Time.GetTicksMsec() * 0.003f) * 0.6f;   // hover above the player (follows you onto platforms)
            GlobalPosition = ClampArena(np);
        }
        if (_fireCd <= 0f && dist < _range) { FireAt(p, _boltSpeed, _boltDmg, _boltRadius); _fireCd = _fireEvery; }
    }

    private void MoveHealer(Player p, float dt, float spdMul)
    {
        Enemy ally = NearestAlly();
        Vector3 pfrom = _tgt - GlobalPosition; pfrom.Y = 0;
        Vector3 want;
        if (ally != null)
        {
            Vector3 toa = ally.GlobalPosition - GlobalPosition; toa.Y = 0;
            want = toa.Length() > 5f ? toa.Normalized() : -pfrom.Normalized() * 0.5f;   // hover near ally, drift from player
        }
        else want = pfrom.LengthSquared() > 0.01f ? -pfrom.Normalized() : Vector3.Forward;   // flee player if alone
        if (spdMul > 0f) { var np = GlobalPosition + want.Normalized() * Speed * _catchMul * spdMul * dt; GlobalPosition = ClampArena(np); }

        _healCd -= dt;
        if (_healCd <= 0f && ally != null && ally.GlobalPosition.DistanceTo(GlobalPosition) < 12f)
        {
            ally.Heal(_healAmt);
            _healCd = _healEvery;
            var v = new Vfx(); Game.I.AddChild(v);
            v.GlobalPosition = ally.GlobalPosition + new Vector3(0, ally.Radius, 0);
            v.Init(new SphereMesh { Radius = ally.Radius * 0.6f, Height = ally.Radius * 1.2f }, DamageTypes.Col(DamageType.Holy), 0.4f, 2f);
        }
    }

    private Vector3 _fleeTarget;
    private float _recmT = 0f;
    private bool _hasFlee = false;

    private void MoveGoblin(Player p, float dt, float spdMul)
    {
        Vector3 gp = GlobalPosition;
        Vector3 from = gp - _tgt; from.Y = 0;
        float dist = from.Length();
        bool seen = LosClear(gp, _tgt);
        bool threatened = dist < 18f || seen;

        _recmT -= dt;
        if (threatened && (_recmT <= 0f || !_hasFlee))
        {
            _fleeTarget = PickCover(_tgt, gp);
            _hasFlee = true;
            _recmT = 1.0f;                       // commit briefly so it doesn't jitter in place
        }
        if (!threatened) _hasFlee = false;       // hidden and you're far → hold position

        Vector3 want = Vector3.Zero;
        if (_hasFlee)
        {
            Vector3 to = _fleeTarget - gp; to.Y = 0;
            if (to.Length() < 2f) want = dist > 0.01f ? from.Normalized() : Vector3.Forward;
            else want = to.Normalized();
            if (dist > 0.01f) want = (want * 0.7f + from.Normalized() * 0.5f);   // always biased to retreat
        }
        if (want.LengthSquared() > 0.001f && spdMul > 0f)
        {
            var np = gp + want.Normalized() * Speed * _catchMul * spdMul * dt;
           
            GlobalPosition = ClampArena(np);
        }
    }

    // a hide-spot behind a blocker that's away from the player and not the one we're already on
    private Vector3 PickCover(Vector3 player, Vector3 self)
    {
        Vector3 best = self + (self - player).Normalized() * 14f; best.Y = 0;   // fallback: straight away
        float bestScore = -1e9f;
        foreach (var b in Game.I.Blockers)
        {
            var bp = new Vector3(b.Pos.X, 0, b.Pos.Z);
            float dPlayer = new Vector2(bp.X - player.X, bp.Z - player.Z).Length();
            float dSelf = new Vector2(bp.X - self.X, bp.Z - self.Z).Length();
            if (dPlayer < 6f || dSelf < 2.5f) continue;      // skip cover on the player or one we're already at
            float score = -dSelf * 0.5f - Mathf.Abs(dPlayer - 16f);
            if (score > bestScore)
            {
                bestScore = score;
                var away = bp - player; away.Y = 0;
                best = bp + away.Normalized() * (b.Radius + 2.5f); best.Y = 0;
            }
        }
        return best;
    }

    private bool LosClear(Vector3 a, Vector3 b)
    {
        foreach (var bl in Game.I.Blockers)
            if (SegDist(new Vector2(bl.Pos.X, bl.Pos.Z), new Vector2(a.X, a.Z), new Vector2(b.X, b.Z)) < bl.Radius + 0.4f) return false;
        return true;
    }

    private static float SegDist(Vector2 pt, Vector2 a, Vector2 b)
    {
        var ab = b - a; float len2 = ab.LengthSquared();
        if (len2 < 0.0001f) return pt.DistanceTo(a);
        float t = Mathf.Clamp((pt - a).Dot(ab) / len2, 0f, 1f);
        return pt.DistanceTo(a + ab * t);
    }

    private int _bossPat = 0;
    private int _bossRing = 0;   // alternates the radial ring between a ground sweep and a jump-height sweep (NEW)
    private float _bossNovaCd = 0f;
    private float _bossPestCd = 10f, _bossStompCd = 6f, _bossAoeReach = 0f;   // (NEW) Stage-2 boss abilities: pestilence pool + AoE stomp
    private float _bossRockCd = 9f, _bossMineCd = 14f, _goblinBufferCd = 0f;   // (NEW) Stage-3: orc rock throw + goblin mines + 10-15s buffer between the two goblin abilities
    // boss attacks now telegraph: a wind-up holds the boss, shows a danger indicator + shout, then fires (NEW)
    private bool _bossCharging = false;
    private float _bossHeat = 0.2f;          // (NEW) real-time aggression 0..1 — mostly from missing HP, some from witch combos; scales cooldowns + speed
    public float BossHeat => _bossHeat;
    private float _bossChargeT = 0f;
    private float _bossChargeDur = 0f;       // full wind-up length, for the attack-timer bar (NEW)
    private bool _bossEnraged = false;       // captured at wind-up start so telegraph + volley always match (NEW)
    private int _bossPatPending = 0;
    private Vector3 _bossAim = Vector3.Forward, _bossFlatDir = Vector3.Forward;
    public bool IsCharging => _bossCharging;                                                                   // HUD: boss winding up an attack (NEW)
    public string BossAttackName => _bossPatPending switch { 1 => "RADIAL BURST", 3 => "NOVA", 4 => "PESTILENCE", 5 => "STOMP", 6 => "ROCK THROW", 7 => "MINES", _ => "VOLLEY" };   // (NEW) attack meter label
    // (NEW) hitting high — the head or a shoulder goblin — always crits THE HOLLOW MOON
    public bool IsCritZone(Vector3 hitPos)
    {
        if (IsBoss && _type == "boss") return (hitPos.Y - GlobalPosition.Y) > Radius * 0.7f;   // THE HOLLOW MOON's head/shoulders
        if (_type == "sentinel" && _sentinelCore != null && GodotObject.IsInstanceValid(_sentinelCore))
            return hitPos.DistanceTo(_sentinelCore.GlobalPosition) < Radius * 0.9f;   // (NEW) strike the exposed core → auto-crit through the armor
        return false;
    }

    // (NEW) capsule hit test — the visual model builds UP from ~the feet (origin sits low, head is ~Radius*1.9 above it),
    // so a sphere at the origin only covered the legs of tall foes ("can only hit their feet"). This tests distance to the
    // whole body spine (feet → head) so any point on the model registers. Radial girth stays ~Radius (matches the mesh width).
    public bool HitBy(Vector3 point, float pad)
    {
        Vector3 a = GlobalPosition + Vector3.Down * Radius * 0.7f;   // near the feet
        Vector3 b = GlobalPosition + Vector3.Up * Radius * 1.9f;     // near the top of the head
        Vector3 ab = b - a;
        float t = Mathf.Clamp((point - a).Dot(ab) / ab.LengthSquared(), 0f, 1f);
        return point.DistanceTo(a + ab * t) < Radius + pad;
    }
    public string PingName => !string.IsNullOrEmpty(Label) ? Label : _type switch   // (NEW) nice name for the ping nameplate
    {
        "shade" => "Shade", "swarmer" => "Zombie", "caster" => "Caster", "flyer" => "Flyer", "brute" => "Brute",
        "sieger" => "Sieger", "healer" => "Healer", "zapper" => "Zapper", "bomber" => "Bomber", "diver" => "Diver",
        "splitter" => "Splitter", "sentinel" => "Sentinel", "hexer" => "Hexer", "wardbane" => "Wardbane", "totem" => "Totem",
        "wisp" => "Wisp", "taker" => "The Taker", "miniboss" => "Champion", "boss" => "The Hollow Moon", _ => "Enemy"
    };
    private Vector3 PopupPos => IsBoss ? GlobalPosition + new Vector3(0f, Radius * 2.7f, 0f) : GlobalPosition;   // (NEW) float boss numbers above the huge model
    public float ChargeFrac => _bossChargeDur > 0.0001f ? Mathf.Clamp(1f - _bossChargeT / _bossChargeDur, 0f, 1f) : 0f;   // HUD: 0→1 attack-timer (NEW)
    private void BossFire(Player p, float dt)
    {
        // real-time heat: biggest driver is missing HP; recent player DPS nudges it up. Smoothly tracked so it ramps.
        float dpsF = MaxHp > 0f ? Mathf.Clamp(Game.I.BossRecentDps / (MaxHp * 0.03f), 0f, 1f) : 0f;
        float target = Mathf.Clamp(0.12f + 0.66f * (1f - Hp / MaxHp) + 0.22f * dpsF, 0f, 1f);
        _bossHeat = Mathf.MoveToward(_bossHeat, target, dt * 0.5f);
        _bossNovaCd -= dt; _bossPestCd -= dt; _bossStompCd -= dt;
        _bossRockCd -= dt; _bossMineCd -= dt; if (_goblinBufferCd > 0f) _goblinBufferCd -= dt;
        if (_critVoxCd > 0f) _critVoxCd -= dt;
        if (_creature != null) _creature.StompWind = (_bossCharging && _bossPatPending == 5 && _bossChargeDur > 0.01f) ? Mathf.Clamp(1f - _bossChargeT / _bossChargeDur, 0f, 1f) : 0f;   // (NEW) raise the good leg through the stomp wind-up
        Vector3 to = _tgt - GlobalPosition; to.Y = 0;
        float dist = to.Length();
        bool enraged = Hp < MaxHp * 0.5f;

        // true 3D aim at the player's chest, so shots descend to a hittable height instead of sailing overhead (NEW)
        Vector3 muzzle = GlobalPosition + new Vector3(0, Radius * 0.6f, 0);
        Vector3 aim = _tgt + new Vector3(0, 1.0f, 0) - muzzle;
        aim = aim.LengthSquared() < 0.01f ? Vector3.Forward : aim.Normalized();
        Vector3 flatDir = dist > 0.01f ? to / dist : Vector3.Forward;

        // ---- mid wind-up: when the telegraph finishes, the volley fires along the SAME direction it warned (NEW) ----
        if (_bossCharging)
        {
            _bossChargeT -= dt;
            if (_bossChargeT > 0f) return;        // still telegraphing — boss is held (see the behavior switch)
            _bossCharging = false;
            FireBossPattern(_bossPatPending, _bossEnraged, _bossAim, _bossFlatDir);
            if (_bossPatPending == 3) { _bossNovaCd = Mathf.Lerp(4.5f, 2.5f, _bossHeat); _fireCd = Mathf.Max(_fireCd, 0.6f); }   // hotter → recasts sooner
            else if (_bossPatPending == 4) { _bossPestCd = Mathf.Lerp(20f, 12f, _bossHeat); _goblinBufferCd = Mathf.Lerp(15f, 10f, _bossHeat); _fireCd = Mathf.Max(_fireCd, 0.8f); }   // pestilence 20→12s (goblin)
            else if (_bossPatPending == 5) { _bossStompCd = Mathf.Lerp(10f, 6f, _bossHeat); _fireCd = Mathf.Max(_fireCd, 0.8f); }   // stomp 10→6s
            else if (_bossPatPending == 6) { _bossRockCd = Mathf.Lerp(12f, 8f, _bossHeat); _fireCd = Mathf.Max(_fireCd, 0.8f); }    // rock throw 12→8s (orc)
            else if (_bossPatPending == 7) { _bossMineCd = Mathf.Lerp(20f, 12f, _bossHeat); _goblinBufferCd = Mathf.Lerp(15f, 10f, _bossHeat); _fireCd = Mathf.Max(_fireCd, 0.8f); }   // mines 20→12s (goblin)
            else _fireCd = _fireEvery * Mathf.Lerp(1f, 0.55f, _bossHeat);
            return;
        }

        bool hollow = _type == "boss";   // the new abilities belong to THE HOLLOW MOON only (not the mini-boss)
        // AoE STOMP — only if a witch is in close range; 3s telegraphed wind-up (red ring around the boss)
        if (hollow && _bossStompCd <= 0f && dist < Radius + 8f) { BeginBossAoe(5, GlobalPosition, 3f); return; }
        // ROCK THROW (orc) — hurl a rock at the nearest witch; 3s telegraphed wind-up (red landing circle) — stuns on hit
        if (hollow && _bossRockCd <= 0f && dist < _range * 1.5f) { BeginBossAoe(6, _tgt, 3f); return; }
        // PESTILENCE (goblin) — lingering pool; respects the buffer between the two goblins
        if (hollow && _bossPestCd <= 0f && _goblinBufferCd <= 0f && dist < _range * 1.3f) { BeginBossAoe(4, _tgt, 3f); return; }
        // MINES (non-zombie goblin) — scatter armed mines; also respects the goblin buffer
        if (hollow && _bossMineCd <= 0f && _goblinBufferCd <= 0f && dist < _range * 1.4f) { BeginBossAoe(7, GlobalPosition, 3f); return; }

        // close-range nova punishes hugging — short, sharp wind-up
        if (dist < Radius + 7f && _bossNovaCd <= 0f) { BeginBossCharge(3, enraged ? 0.4f : 0.55f, aim, flatDir, Radius + 7f, enraged); return; }

        if (_fireCd > 0f || dist > _range) return;
        int pat = _bossPat++ % 3;
        float reach = (pat == 1) ? _range * 0.6f : _range;
        BeginBossCharge(pat, enraged ? 0.55f : 0.8f, aim, flatDir, reach, enraged);
    }

    // lock in the next attack, freeze, and telegraph it (per-shot lanes + shout + grunt), locally and to clients (NEW)
    // (NEW) telegraphed AoE (pat 4 pestilence at a spot, pat 5 stomp around the boss): encode the target as
    // direction+distance so it reuses the existing MP-synced BeginBossCharge telegraph pipeline.
    private void BeginBossAoe(int pat, Vector3 center, float dur)
    {
        var to = center - GlobalPosition; to.Y = 0f;
        float reach = to.Length();
        Vector3 flatDir = reach > 0.01f ? to / reach : Vector3.Forward;
        _bossAoeReach = reach;
        BeginBossCharge(pat, dur, flatDir, flatDir, reach, false);
    }

    private void BeginBossCharge(int pat, float dur, Vector3 aim, Vector3 flatDir, float reach, bool enraged)
    {
        _bossCharging = true; _bossChargeT = dur; _bossChargeDur = dur; _bossPatPending = pat;
        _bossAim = aim; _bossFlatDir = flatDir; _bossEnraged = enraged;
        int idx = (int)(GD.Randf() * 997f);
        ShowBossTelegraph(pat, flatDir, reach, dur, enraged);
        SayBossLine(pat, idx);
        Game.I.Sfx?.BossTell(GlobalPosition);
        Game.I.NetMgr?.BroadcastBossTell(NetId, pat, flatDir.X, flatDir.Z, reach, dur, idx, enraged ? 1 : 0);
    }

    // the actual volley (factored out of the old BossFire); aim/flatDir are the values captured at wind-up start (NEW)
    private void FireBossPattern(int pat, bool enraged, Vector3 aim, Vector3 flatDir)
    {
        if (pat == 4)        // pestilence: lingering Nature pool at the telegraphed spot (stays until the boss dies)
        {
            var center = GlobalPosition + new Vector3(flatDir.X, 0f, flatDir.Z) * _bossAoeReach;
            Game.I.SpawnPestilence(center, 6.5f, 6f + Game.I.Wave * 0.5f, remote: false, net: true);
            _creature?.FireShoulder(true);   // left zombie goblin casts
            Game.I.Sfx?.BossRoar(GlobalPosition);
            return;
        }
        if (pat == 5)        // AoE stomp: shockwave around the boss, stuns/hurts witches in range
        {
            float r = 8f, dmg = 14f + Game.I.Wave * 0.9f;
            Game.I.NetMgr?.HurtPlayersIn(GlobalPosition, r, dmg);
            Game.I.VfxRing(GlobalPosition, new Color(1f, 0.5f, 0.15f), r, 0.5f);
            Game.I.NetMgr?.BroadcastVfx(0, GlobalPosition, Vector3.Zero, r, 0.5f, new Color(1f, 0.5f, 0.15f));   // ring for allies
            Game.I.SpawnGroundSpikes(GlobalPosition, r, 14, new Color(0.7f, 0.4f, 0.2f), 0.4f);
            Game.I.Sfx?.Thunder();
            return;
        }
        if (pat == 6)        // orc rock throw at the telegraphed spot — stuns on hit
        {
            var target = GlobalPosition + new Vector3(flatDir.X, 0f, flatDir.Z) * _bossAoeReach;
            var from = GlobalPosition + new Vector3(0f, Radius * 1.5f, 0f) + new Vector3(flatDir.X, 0f, flatDir.Z) * (Radius * 0.8f);
            Game.I.SpawnBossRock(from, target, 20f + Game.I.Wave * 1.1f, remote: false, net: true);
            Game.I.Sfx?.BossRoar(GlobalPosition);
            return;
        }
        if (pat == 7)        // non-zombie goblin scatters mines around the boss
        {
            Game.I.SpawnBossMines(GlobalPosition, 4 + Game.I.WardenCount, 14f + Game.I.Wave * 0.8f);
            _creature?.FireShoulder(false);   // right non-zombie goblin throws
            Game.I.Sfx?.BossRoar(GlobalPosition);
            return;
        }
        if (pat == 3)        // close nova
        {
            for (int i = 0; i < 16; i++)
            {
                Vector3 d = Vector3.Forward.Rotated(Vector3.Up, i * Mathf.Tau / 16f); d.Y = -0.18f;
                SpawnBolt(d.Normalized() * (_boltSpeed * 0.9f), _boltDmg * 0.8f, _boltRadius);
            }
            Game.I.Sfx?.Thunder();
        }
        else if (pat == 0)   // aimed fan
        {
            int n = enraged ? 3 : 2;
            for (int i = -n; i <= n; i++) SpawnBolt(aim.Rotated(Vector3.Up, i * 0.16f) * _boltSpeed, _boltDmg, _boltRadius);
        }
        else if (pat == 1)   // full radial ring — alternates a low ground sweep and a higher jump-height sweep
        {
            int n = enraged ? 18 : 12;
            float pitch = (_bossRing++ % 2 == 0) ? -0.22f : 0.10f;
            for (int i = 0; i < n; i++)
            {
                Vector3 d = flatDir.Rotated(Vector3.Up, i * Mathf.Tau / n); d.Y = pitch;
                SpawnBolt(d.Normalized() * _boltSpeed, _boltDmg * 0.85f, _boltRadius);
            }
        }
        else                 // fast heavy aimed burst
        {
            for (int i = -1; i <= 1; i++) SpawnBolt(aim.Rotated(Vector3.Up, i * 0.06f) * (_boltSpeed * 1.25f), _boltDmg * 1.15f, _boltRadius * 0.9f);
        }
    }

    // danger lanes that brighten over the wind-up: ONE thin line per projectile, along that shot's real heading,
    // so you can read exactly where each bolt goes and which gaps are safe. Children of the boss (follow + free
    // with it). Drawn identically on host and clients. (NEW)
    private void ShowBossTelegraph(int pat, Vector3 flatDir, float reach, float dur, bool enraged)
    {
        var danger = new Color(1f, 0.18f, 0.10f);
        float groundY = -Radius + 0.06f;   // boss origin is at feet+Radius, so feet are local -Radius
        var laneMat = Game.Emissive(danger, 0.9f);
        laneMat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        var lc = laneMat.AlbedoColor; lc.A = 0.8f; laneMat.AlbedoColor = lc;

        void Lane(Vector3 dir)
        {
            dir.Y = 0f;
            dir = dir.LengthSquared() < 0.0001f ? Vector3.Forward : dir.Normalized();
            var lane = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.28f, 0.05f, reach) }, MaterialOverride = laneMat };
            lane.Rotation = new Vector3(0, Mathf.Atan2(dir.X, dir.Z), 0);
            lane.Position = new Vector3(dir.X * reach * 0.5f, groundY, dir.Z * reach * 0.5f);
            lane.Transparency = 0.85f;   // faint, then brightens as the volley nears
            AddChild(lane);
            lane.CreateTween().TweenProperty(lane, "transparency", 0.12f, dur).SetEase(Tween.EaseType.In);
            var lf = lane.CreateTween(); lf.TweenInterval(dur + 0.12f);
            lf.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(lane)) lane.QueueFree(); }));
        }

        void Ring(Vector3 localCenter, float rad)   // red danger circle in the SHAPE of the AoE (pestilence landing / stomp)
        {
            var ring = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = rad * 0.9f, OuterRadius = rad }, MaterialOverride = laneMat };
            ring.Position = new Vector3(localCenter.X, groundY, localCenter.Z);
            ring.Transparency = 0.85f;
            AddChild(ring);
            ring.CreateTween().TweenProperty(ring, "transparency", 0.1f, dur).SetEase(Tween.EaseType.In);
            var rf = ring.CreateTween(); rf.TweenInterval(dur + 0.12f);
            rf.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(ring)) ring.QueueFree(); }));
        }

        if (pat == 4) { Ring(new Vector3(flatDir.X * reach, 0f, flatDir.Z * reach), 6.5f); return; }   // pestilence landing circle
        if (pat == 5) { Ring(Vector3.Zero, 8f); return; }                                              // stomp ring around the boss
        if (pat == 6)   // rock throw: red landing circle + a boulder forming above the boss's hands (wind-up anim)
        {
            Ring(new Vector3(flatDir.X * reach, 0f, flatDir.Z * reach), 3f);
            var rock = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.85f, Height = 1.7f, RadialSegments = 6, Rings = 4 }, MaterialOverride = Game.Toon(new Color(0.42f, 0.38f, 0.34f), 0.95f, 0.35f, 0.05f) };
            rock.Position = new Vector3(0f, Radius * 1.5f, 0f); rock.Scale = Vector3.Zero;
            AddChild(rock);
            var rt = rock.CreateTween(); rt.TweenProperty(rock, "scale", Vector3.One, dur * 0.8f).SetEase(Tween.EaseType.Out);
            rt.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(rock)) rock.QueueFree(); }));
            return;
        }
        if (pat == 7)   // mines: red scatter ring + a green goblin charge glow (wind-up anim)
        {
            Ring(Vector3.Zero, 10f);
            var glow = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.7f, Height = 1.4f } };
            var gm = Game.ToonEmissive(new Color(0.6f, 0.85f, 0.3f), 2.4f, 0f); gm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; gm.AlbedoColor = new Color(0.6f, 0.85f, 0.3f, 0.7f);
            glow.MaterialOverride = gm; glow.Position = new Vector3(Radius * 0.6f, Radius * 1.4f, 0f); glow.Scale = Vector3.Zero;
            AddChild(glow);
            var gt = glow.CreateTween(); gt.TweenProperty(glow, "scale", Vector3.One * 1.6f, dur).SetEase(Tween.EaseType.Out);
            gt.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(glow)) glow.QueueFree(); }));
            return;
        }

        if (pat == 0)        // aimed fan
        {
            int n = enraged ? 3 : 2;
            for (int i = -n; i <= n; i++) Lane(flatDir.Rotated(Vector3.Up, i * 0.16f));
        }
        else if (pat == 1)   // full radial ring
        {
            int n = enraged ? 18 : 12;
            for (int i = 0; i < n; i++) Lane(flatDir.Rotated(Vector3.Up, i * Mathf.Tau / n));
        }
        else if (pat == 3)   // close nova (16 spokes, radial)
        {
            for (int i = 0; i < 16; i++) Lane(Vector3.Forward.Rotated(Vector3.Up, i * Mathf.Tau / 16f));
        }
        else                 // heavy aimed burst
        {
            for (int i = -1; i <= 1; i++) Lane(flatDir.Rotated(Vector3.Up, i * 0.06f));
        }
    }

    // a shouted line floats over the boss as it winds up — pattern-flavored so you can read what's coming (NEW)
    private static readonly string[] _bossLinesAimed = { "DIE!", "FOUND YOU!", "PIERCE!", "NO ESCAPE!", "STRAIGHT THROUGH YOU!" };
    private static readonly string[] _bossLinesRing  = { "NOWHERE TO RUN!", "ALL AROUND YOU!", "SCATTER, VERMIN!", "THE GROVE CONSUMES!", "DROWN IN IT!" };
    private static readonly string[] _bossLinesNova  = { "GET BACK!", "BEGONE!", "TOO CLOSE!", "REPENT!" };
    private static readonly string[] _bossLinesPest  = { "ROT AND WITHER!", "BREATHE THE PLAGUE!", "FEED, MY PET!", "SICKEN, WITCH!" };
    private static readonly string[] _bossLinesStomp = { "KNEEL!", "SHATTER!", "THE EARTH ANSWERS!", "BE CRUSHED!" };
    private static readonly string[] _bossLinesRock  = { "CATCH!", "BE STILL!", "CRUSH THEM!", "TAKE THIS!" };
    private static readonly string[] _bossLinesMine  = { "TREAD CAREFULLY!", "A GIFT, LITTLE WITCH!", "STEP LIGHTLY!", "SCATTER THEM!" };
    private string[] BossLines(int pat) => pat switch { 1 => _bossLinesRing, 3 => _bossLinesNova, 4 => _bossLinesPest, 5 => _bossLinesStomp, 6 => _bossLinesRock, 7 => _bossLinesMine, _ => _bossLinesAimed };

    private void SayBossLine(int pat, int idx)
    {
        var lines = BossLines(pat);
        SayBossVox(lines[((idx % lines.Length) + lines.Length) % lines.Length], new Color(1f, 0.82f, 0.2f), 1.0f);
    }

    private float _critVoxCd = 0f;
    private static readonly string[] _critHeadLines = { "AHH, MY GOBLINS!", "NOT MY SKULL!", "MY HEAD!", "ARGH — MY MOONS!" };
    private static readonly string[] _critGobNormal = { "OW! YOU LITTLE—", "CURSE YOU, WITCH!", "MY EYE, MY EYE!", "RUDE!" };
    private static readonly string[] _critGobZombie = { "UUUGHHH...", "OOOUCHH...", "GRAAAHHH...", "hhhngghh..." };
    private static readonly string[] _bossDeathLines = { "THE OTHER MOONS WILL TAKE YOU...", "THE OTHER MOONS... WILL AVENGE ME...", "YOU CANNOT KILL... ALL OF US...", "THE MOONS... ARE MANY..." };
    private static readonly string[] _bossTaunts = { "I WANT THE WITCHES' HEADS ON A STAKE!", "BURN THE WITCHES!", "BRING ME THEIR BONES!", "SWARM THEM, MY CHILDREN!", "TEAR THEM APART!", "NO MERCY FOR THE COVEN!", "DROWN THEM IN NUMBERS!" };
    public void Taunt() { SayBossVox(_bossTaunts[GD.RandRange(0, _bossTaunts.Length - 1)], new Color(1f, 0.6f, 0.2f), 1.5f); }

    // which high zone got hit: 0 none, 1 head, 2 left(zombie goblin), 3 right(normal goblin)
    public int CritZone(Vector3 hitPos)
    {
        if (!IsBoss || _type != "boss") return 0;
        if (hitPos.Y - GlobalPosition.Y < Radius * 0.7f) return 0;
        var lp = _creature != null ? _creature.ToLocal(hitPos) : ToLocal(hitPos);
        if (lp.X < -Radius * 0.4f) return 2;
        if (lp.X > Radius * 0.4f) return 3;
        return 1;
    }
    // a crit landed high — the boss / the struck goblin yelps (throttled)
    public void CritHitReact(Vector3 hitPos)
    {
        if (_critVoxCd > 0f) return;
        int z = CritZone(hitPos);
        if (z == 0) return;
        _critVoxCd = 2.2f;
        switch (z)
        {
            case 2: SayBossVox(_critGobZombie[GD.RandRange(0, _critGobZombie.Length - 1)], new Color(0.55f, 0.9f, 0.3f), 1.0f); break;
            case 3: SayBossVox(_critGobNormal[GD.RandRange(0, _critGobNormal.Length - 1)], new Color(1f, 0.85f, 0.25f), 1.0f); break;
            default: SayBossVox(_critHeadLines[GD.RandRange(0, _critHeadLines.Length - 1)], new Color(1f, 0.4f, 0.35f), 1.0f); break;
        }
    }

    private void SayBossVox(string line, Color col, float hold)
    {
        var lbl = new Label3D
        {
            Text = line, FontSize = 120, OutlineSize = 28, PixelSize = 0.012f,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, NoDepthTest = true,
            Modulate = col, OutlineModulate = new Color(0, 0, 0, 1f),
            Position = new Vector3(0, Radius * 2.9f, 0)
        };
        AddChild(lbl);
        var t = lbl.CreateTween();
        t.TweenInterval(hold);
        t.TweenProperty(lbl, "modulate:a", 0f, 0.6f);
        var f = lbl.CreateTween(); f.TweenInterval(hold + 0.7f);
        f.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(lbl)) lbl.QueueFree(); }));
    }

    // client proxy mirror: show the same lanes + shout + grunt the host broadcast (no firing — bolts are synced),
    // and drive the wind-up bar locally so co-op players see the same attack timer. (NEW)
    public void RemoteBossTell(int pat, float fx, float fz, float reach, float dur, int idx, int enr)
    {
        var flat = new Vector3(fx, 0, fz);
        flat = flat.LengthSquared() < 0.0001f ? Vector3.Forward : flat.Normalized();
        ShowBossTelegraph(pat, flat, reach, dur, enr != 0);
        SayBossLine(pat, idx);
        Game.I.Sfx?.BossTell(GlobalPosition);
        _bossCharging = true; _bossChargeT = dur; _bossChargeDur = dur;   // fills the attack-timer bar on the client too
    }

    // ---- zapper: telegraphed lightning that drains half your shield ----
    private float _zapTele = 0f;
    private Vector3 _zapTarget;
    private Node3D _zapMark;
    private void MoveZapper(Player p, float dt, float spdMul)
    {
        if (_creature != null) _creature.SetCast(_zapTele > 0f ? 1f : 0f);   // rear-back cast pose while telegraphing
        Vector3 to = _tgt - GlobalPosition; to.Y = 0;
        float dist = to.Length();
        Vector3 dir = dist > 0.01f ? to.Normalized() : Vector3.Forward;
        Vector3 want = Vector3.Zero;
        if (dist > _preferDist + 2f) want += dir; else if (dist < _preferDist - 2f) want -= dir;
        _strafeT -= dt; if (_strafeT <= 0) { _strafe = -_strafe; _strafeT = (float)GD.RandRange(1.2, 2.4); }
        want += new Vector3(-dir.Z, 0, dir.X) * _strafe * 0.6f;
        if (want.LengthSquared() > 0.001f && spdMul > 0f && _zapTele <= 0f)   // freeze while telegraphing
        {
            var np = GlobalPosition + want.Normalized() * Speed * _catchMul * spdMul * dt;
            GlobalPosition = ClampArena(np);
        }

        if (_zapTele > 0f)
        {
            _zapTele -= dt;
            if (_zapMark != null && GodotObject.IsInstanceValid(_zapMark))
                _zapMark.Scale = Vector3.One * (1f + Mathf.Sin(_zapTele * 18f) * 0.06f);
            if (_zapTele <= 0f) ZapStrike(p);
        }
        else if (_fireCd <= 0f && dist < _range)
        {
            _zapTarget = _tgt; _zapTarget.Y = 0f;
            _zapTele = 1.05f; _fireCd = _fireEvery;
            _zapMark = MakeZapMark(_zapTarget);
            Game.I.NetMgr?.BroadcastZap(_zapTarget, false);   // allies see the telegraph
        }
    }

    // ---- new archetypes ----
    private void MoveDiver(Player p, float dt, float spdMul)
    {
        Vector3 to = _tgt - GlobalPosition; to.Y = 0;
        float dist = to.Length();
        Vector3 dir = dist > 0.01f ? to.Normalized() : Vector3.Forward;
        if (_diveCd > 0f) _diveCd -= dt;
        if (_diving)
        {
            var np = GlobalPosition + (_tgt - GlobalPosition).Normalized() * Speed * 2.6f * dt;   // fast swoop
            GlobalPosition = ClampArena(np);
            _diveT -= dt;
            if (dist < Radius + 1.8f && _touchCd <= 0f) { HitTarget(Dmg); _touchCd = 0.7f; }
            if (_diveT <= 0f || GlobalPosition.Y <= _tgt.Y + 0.7f) { _diving = false; _diveCd = 2.6f; }   // climb back out
        }
        else
        {
            Vector3 want = new Vector3(-dir.Z, 0, dir.X) * _strafe;
            if (dist > _preferDist + 3f) want += dir * 0.8f; else if (dist < _preferDist - 3f) want -= dir * 0.8f;
            _strafeT -= dt; if (_strafeT <= 0) { _strafe = -_strafe; _strafeT = (float)GD.RandRange(0.8, 1.8); }
            var np = GlobalPosition;
            if (want.LengthSquared() > 0.001f && spdMul > 0f) np += want.Normalized() * Speed * spdMul * dt;
            np.Y = Mathf.Lerp(np.Y, _tgt.Y + _flyY + 1.5f, dt * 3f);   // hover high (the wind-up before a dive)
            GlobalPosition = ClampArena(np);
            if (_diveCd <= 0f && dist < _range)
            {
                _diving = true; _diveT = 1.5f;
                var dc = new Color(0.9f, 0.6f, 1f);
                Game.I.VfxRing(_tgt, dc, 3f, 0.7f);                              // dive marker — glide or dash clear
                Game.I.NetMgr?.BroadcastVfx(0, _tgt, Vector3.Up, 3f, 0.7f, dc);
            }
        }
    }

    private void MoveHexer(Player p, float dt, float spdMul)
    {
        if (_creature != null) _creature.SetCast(_hexTele > 0f ? 1f : 0f);   // rear-back tell while charging the hex
        Vector3 to = _tgt - GlobalPosition; to.Y = 0;
        float dist = to.Length();
        Vector3 dir = dist > 0.01f ? to.Normalized() : Vector3.Forward;
        Vector3 want = Vector3.Zero;
        if (dist > _preferDist + 2f) want += dir; else if (dist < _preferDist - 2f) want -= dir;
        _strafeT -= dt; if (_strafeT <= 0) { _strafe = -_strafe; _strafeT = (float)GD.RandRange(1.2, 2.4); }
        want += new Vector3(-dir.Z, 0, dir.X) * _strafe * 0.6f;
        if (want.LengthSquared() > 0.001f && spdMul > 0f && _hexTele <= 0f)
        {
            var np = GlobalPosition + want.Normalized() * Speed * _catchMul * spdMul * dt;
            GlobalPosition = ClampArena(np);
        }
        if (_hexTele > 0f) { _hexTele -= dt; if (_hexTele <= 0f) HexStrike(); }
        else if (_hexCd <= 0f && dist < _range)
        {
            _hexTele = 1.0f; _hexCd = _fireEvery;
            var cc = DamageTypes.Col(DamageType.Curse);
            Game.I.VfxRing(_tgt, cc, 3.5f, 1.0f);                              // telegraph: ring at the target — dash out before it lands
            Game.I.NetMgr?.BroadcastVfx(0, _tgt, Vector3.Up, 3.5f, 1.0f, cc); // all peers see it
        }
    }
    private void HexStrike()
    {
        if (_tgtIsMinion) return;
        if (_tgtPeer == 0) Game.I.Player?.SnareMe(1.4f);          // host player
        else Game.I.NetMgr?.SnarePlayer(_tgtPeer, 1.4f);          // route to the targeted ally
    }

    // wardbane: a curse-caster that kites and periodically fires a telegraphed dispel pulse, stripping the
    // target's wards/shield and suppressing regain. Reuses the hexer's telegraph timers (_hexTele/_hexCd).
    private void MoveSapper(Player p, float dt, float spdMul)
    {
        if (_creature != null) _creature.SetCast(_hexTele > 0f ? 1f : 0f);
        Vector3 to = _tgt - GlobalPosition; to.Y = 0;
        float dist = to.Length();
        Vector3 dir = dist > 0.01f ? to.Normalized() : Vector3.Forward;
        Vector3 want = Vector3.Zero;
        if (dist > _preferDist + 2f) want += dir; else if (dist < _preferDist - 2f) want -= dir;
        _strafeT -= dt; if (_strafeT <= 0) { _strafe = -_strafe; _strafeT = (float)GD.RandRange(1.2, 2.4); }
        want += new Vector3(-dir.Z, 0, dir.X) * _strafe * 0.6f;
        if (want.LengthSquared() > 0.001f && spdMul > 0f && _hexTele <= 0f)
            GlobalPosition = ClampArena(GlobalPosition + want.Normalized() * Speed * _catchMul * spdMul * dt);
        if (_hexTele > 0f) { _hexTele -= dt; if (_hexTele <= 0f) SapStrike(); }
        else if (_hexCd <= 0f && dist < _range)
        {
            _hexTele = 1.1f; _hexCd = _fireEvery;
            var cc = new Color(0.6f, 0.3f, 0.85f);
            Game.I.VfxRing(_tgt, cc, 3.2f, 1.0f);                              // telegraph: dispel ring — break line of sight or eat the strip
            Game.I.NetMgr?.BroadcastVfx(0, _tgt, Vector3.Up, 3.2f, 1.0f, cc); // all peers see it
        }
    }

    private void SapStrike()
    {
        if (_tgtIsMinion) return;
        if (_tgtPeer == 0) { Game.I.Player?.Dispel(3.0f); Game.I.Player?.Hurt(Dmg, GlobalPosition); }
        else { Game.I.NetMgr?.DispelPlayer(_tgtPeer, 3.0f); Game.I.NetMgr?.DamagePlayer(_tgtPeer, Dmg); }
    }

    private void MoveTotem(Player p, float dt, float spdMul)
    {
        // stationary buffer — hastes nearby foes; you decide whether to kill it first
        _totemTick -= dt;
        if (_totemTick <= 0f)
        {
            _totemTick = 0.9f;
            var gc = new Color(1f, 0.8f, 0.35f);
            Game.I.VfxRing(GlobalPosition, gc, 14f, 0.5f);                            // visible empower pulse (shows its radius)
            Game.I.NetMgr?.BroadcastVfx(0, GlobalPosition, Vector3.Up, 14f, 0.5f, gc);
            foreach (var e in Game.I.Enemies.ToArray())
            {
                if (e == null || e == this || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
                if (e._behav == EBehav.Totem) continue;
                if (GlobalPosition.DistanceTo(e.GlobalPosition) < 14f) e.ApplyHaste(1.1f);
            }
        }
    }
    public void ApplyHaste(float dur) { _hasteT = Mathf.Max(_hasteT, dur); }

    private void VampHeal()
    {
        foreach (var e in Game.I.Enemies.ToArray())
        {
            if (e == null || e == this || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
            if (GlobalPosition.DistanceTo(e.GlobalPosition) < 9f) e.Heal(e.MaxHp * 0.03f);
        }
    }

    private void Explode()   // volatile affix: blast on death (host-authoritative; broadcast for visuals)
    {
        float r = 6.5f, edmg = 16f + Dmg * 1.2f;
        var col = new Color(1f, 0.55f, 0.12f);
        var pl = Game.I.Player;
        if (pl != null && pl.GlobalPosition.DistanceTo(GlobalPosition) < r) pl.Hurt(edmg, GlobalPosition);
        Game.I.NetMgr?.DamageAlliesNear(GlobalPosition, r, edmg);
        var v = new Vfx(); Game.I.AddChild(v); v.GlobalPosition = GlobalPosition + Vector3.Up * 0.6f;
        v.Init(new SphereMesh { Radius = r, Height = r * 2f }, col, 0.4f, 7f);
        Game.I.NetMgr?.BroadcastVfx(6, GlobalPosition, Vector3.Zero, r, 0f, col);
    }

    private Node3D MakeZapMark(Vector3 at) => Game.I.ZapMarkNode(at);

    private void ZapStrike(Player p)
    {
        if (_zapMark != null && GodotObject.IsInstanceValid(_zapMark))
        {
            var bolt = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.12f, BottomRadius = 0.45f, Height = 34f } };
            bolt.MaterialOverride = Game.Emissive(new Color(0.75f, 0.88f, 1f), 3.2f);
            bolt.Position = new Vector3(0, 17f, 0);
            _zapMark.AddChild(bolt);
        }
        Game.I.Sfx?.Thunder();
        Game.I.NetMgr?.BroadcastZap(_zapTarget, true);   // allies see the lightning

        // anyone standing on the mark gets caught — host player locally, allies over the net
        var hp = Game.I.Player;
        if (hp != null)
        {
            float dh = new Vector2(hp.GlobalPosition.X - _zapTarget.X, hp.GlobalPosition.Z - _zapTarget.Z).Length();
            if (dh < 3.3f)
            {
                if (hp.Shield > 0.01f) hp.DrainShield(0.5f, GlobalPosition);
                else hp.Stun(0.45f, GlobalPosition);
            }
        }
        Game.I.NetMgr?.StunAlliesNear(_zapTarget, 3.3f, 0.45f);

        var m = _zapMark; _zapMark = null;
        if (m != null && GodotObject.IsInstanceValid(m))
            m.GetTree().CreateTimer(0.2f).Timeout += () => { if (GodotObject.IsInstanceValid(m)) m.QueueFree(); };
    }

    // ---- bomber: rushes you and blows itself up ----
    private void MoveBomber(Player p, float dt, float spdMul)
    {
        Vector3 to = _tgt - GlobalPosition; to.Y = 0;
        float dist = to.Length();
        if (dist > 0.01f && spdMul > 0f)
        {
            var np = GlobalPosition + to.Normalized() * Speed * _catchMul * spdMul * dt;
            GlobalPosition = ClampArena(np);
        }
        if (dist < Radius + 2.4f) Explode(p);
    }

    private void Explode(Player p)
    {
        if (Dead) return;
        float blast = 5.5f;
        var hp = Game.I.Player;
        if (hp != null && (hp.GlobalPosition - GlobalPosition).Length() < blast) hp.Hurt(Dmg, GlobalPosition);
        Game.I.NetMgr?.DamageAlliesNear(GlobalPosition, blast, Dmg);   // catch every nearby warden
        Game.I.BlastVFX(GlobalPosition, blast, new Color(1f, 0.5f, 0.12f));
        Game.I.NetMgr?.BroadcastBlast(GlobalPosition, blast, new Color(1f, 0.5f, 0.12f));
        Game.I.Sfx?.Thunder();
        Die();
    }

    public override void _ExitTree()
    {
        if (_zapMark != null && GodotObject.IsInstanceValid(_zapMark)) _zapMark.QueueFree();
    }

    private void FireAt(Player p, float speed, float dmg, float radius)
    {
        Vector3 to = _tgt + new Vector3(0, 1.4f, 0) - (GlobalPosition + new Vector3(0, Radius, 0));
        if (to.LengthSquared() < 0.01f) return;
        { ulong now = Time.GetTicksMsec(); if (now - _lastShootMs > 110 && GD.Randf() < 0.5f) { _lastShootMs = now; Game.I.Sfx?.EnemyShoot(GlobalPosition); } }
        SpawnBolt(to.Normalized() * speed, dmg, radius);
    }

    private void SpawnBolt(Vector3 vel, float dmg, float radius)
    {
        var tint = Col.Lerp(new Color(1, 1, 1), 0.25f);
        var origin = GlobalPosition + new Vector3(0, Radius * 0.6f, 0) + vel.Normalized() * (Radius + 0.5f);
        var b = new EnemyBolt { Vel = vel, Dmg = dmg, Radius = radius, Tint = tint };
        Game.I.AddChild(b);
        b.GlobalPosition = origin;
        Game.I.NetMgr?.BroadcastBolt(origin, vel, radius, tint);   // allies see a visual copy
    }

    private Enemy NearestAlly()
    {
        Enemy best = null; float bd = 1e9f;
        foreach (var e in Game.I.Enemies)
        {
            if (e == null || e == this || e.Dead || !GodotObject.IsInstanceValid(e) || e.IsGoblin) continue;
            if (e.Hp >= e.MaxHp) continue;   // only those who need it
            float d = GlobalPosition.DistanceTo(e.GlobalPosition);
            if (d < bd) { bd = d; best = e; }
        }
        if (best != null) return best;
        // otherwise stick near any ally
        foreach (var e in Game.I.Enemies)
        {
            if (e == null || e == this || e.Dead || !GodotObject.IsInstanceValid(e) || e.IsGoblin) continue;
            float d = GlobalPosition.DistanceTo(e.GlobalPosition);
            if (d < bd) { bd = d; best = e; }
        }
        return best;
    }

    private Vector3 ClampArena(Vector3 p)
    {
        float keepY = p.Y;
        var pl = Game.I != null ? Game.I.Player : null;
        Vector3 ctr = pl != null ? pl.GlobalPosition : Vector3.Zero;
        var off = new Vector2(p.X - ctr.X, p.Z - ctr.Z);
        if (off.Length() > 85f) { off = off.Normalized() * 85f; p.X = ctr.X + off.X; p.Z = ctr.Z + off.Y; }
        // push out of environment blockers so they path around trees/pillars/walls instead of clipping through
        foreach (var b in Game.I.Blockers)
        {
            var bo = new Vector2(p.X - b.Pos.X, p.Z - b.Pos.Z);
            float dd = bo.Length();
            float minD = b.Radius + Radius * 0.6f;
            if (dd < minD) { float k = minD / Mathf.Max(dd, 0.001f); p.X = b.Pos.X + bo.X * k; p.Z = b.Pos.Z + bo.Y * k; }
        }
        p.Y = keepY;
        return p;
    }

    private void UpdateStatusVisual(float dt)
    {
        if (_flash > 0) { _mat.EmissionEnergyMultiplier = 6f; }
        else
        {
            Color sc = Col; float en = _baseEnergy;
            bool rotv = Remote ? _rotShow : (_bleedT > 0f && _bleedRot);
            if (rotv) { sc = sc.Lerp(DamageTypes.Col(DamageType.Blood), 0.78f); en = 3.0f + Mathf.Sin(Time.GetTicksMsec() * 0.012f) * 1.4f; }   // pulsing crimson rot
            else if (_chargeT > 0f || _hexTele > 0f) { sc = sc.Lerp(new Color(1f, 1f, 0.8f), 0.6f); en = 2.5f; }   // sieger/hexer wind-up glow
            else if (RootT > 0) { sc = sc.Lerp(DamageTypes.Col(DamageType.Nature), 0.6f); }
            else if (SlowT > 0) { sc = sc.Lerp(DamageTypes.Col(DamageType.Frost), 0.65f); en = _baseEnergy * 0.85f; }
            if (Cursed) { sc = sc.Lerp(DamageTypes.Col(DamageType.Curse), 0.72f); en = 2.4f + Mathf.Sin(Time.GetTicksMsec() * 0.009f) * 1.3f; }   // (NEW) pulsing curse glow (overrides other tints while cursed)
            _mat.Emission = sc;
            _mat.EmissionEnergyMultiplier = en;
            if (_light != null) _light.LightColor = sc;
        }

        bool marked = MarkT > 0;
        if (marked && _markRing == null)
        {
            _markRing = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = Radius * 1.25f, OuterRadius = Radius * 1.42f } };
            _markRing.MaterialOverride = Game.Emissive(DamageTypes.Col(DamageType.Curse), 1.8f);
            _markRing.Position = new Vector3(0, -Radius * 0.8f, 0);   // flat ground ring (NEW)
            AddChild(_markRing);
        }
        if (!marked && _markRing != null) { _markRing.QueueFree(); _markRing = null; }
        if (_markRing != null) _markRing.RotateY(dt * 2.5f);   // spin flat (NEW: was RotateZ)

        // (NEW) cursed: a spinning curse ring at the feet + an overhead stack counter
        bool cursed = Cursed;
        if (cursed && _curseRing == null)
        {
            _curseRing = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = Radius * 1.05f, OuterRadius = Radius * 1.28f } };
            _curseRing.MaterialOverride = Game.Emissive(DamageTypes.Col(DamageType.Curse), 2.2f);
            _curseRing.Position = new Vector3(0, -Radius * 0.8f, 0);
            AddChild(_curseRing);
        }
        if (!cursed && _curseRing != null) { _curseRing.QueueFree(); _curseRing = null; }
        if (_curseRing != null) _curseRing.RotateY(dt * -3.2f);
        if (cursed)
        {
            if (_curseLabel == null)
            {
                _curseLabel = new Godot.Label3D { FontSize = 40, OutlineSize = 12, PixelSize = 0.006f, Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, NoDepthTest = true, Modulate = DamageTypes.Col(DamageType.Curse).Lerp(Colors.White, 0.4f), OutlineModulate = new Color(0, 0, 0, 1f), Position = new Vector3(0, Radius * 2.0f, 0) };
                AddChild(_curseLabel);
            }
            _curseLabel.Text = "☠" + Mathf.Max(1, Mathf.RoundToInt(CurseStacks));   // just the stack count
        }
        else if (_curseLabel != null) { _curseLabel.QueueFree(); _curseLabel = null; }

        bool rooted = RootT > 0;
        if (rooted && _statusRing == null)
        {
            _statusRing = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = Radius * 0.9f, OuterRadius = Radius * 1.1f } };
            _statusRing.MaterialOverride = Game.Emissive(DamageTypes.Col(DamageType.Nature), 1.6f);
            _statusRing.Position = new Vector3(0, -Radius * 0.8f, 0);
            AddChild(_statusRing);
        }
        if (!rooted && _statusRing != null) { _statusRing.QueueFree(); _statusRing = null; }

        // affix aura ring (Affix is synced, so this shows on host AND clients)
        if (Affix > 0 && _affixAura == null)
        {
            _affixAura = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = Radius * 1.3f, OuterRadius = Radius * 1.5f } };
            _affixAura.MaterialOverride = Game.Emissive(AffixCol(Affix), 1.9f);
            _affixAura.Position = new Vector3(0, -Radius * 0.8f, 0);   // flat ground ring at the feet (NEW: was upright + mid-body)
            AddChild(_affixAura);
        }
        if (_affixAura != null) _affixAura.RotateY(dt * 1.6f);   // spin flat (NEW: was RotateZ)

        bool shieldShown = Affix == 1 && (Remote ? _shieldUp : _shield > 0f);
        if (shieldShown && _shieldBubble == null)
        {
            _shieldBubble = new MeshInstance3D { Mesh = new SphereMesh { Radius = Radius * 1.5f, Height = Radius * 3f } };
            _shieldBubble.MaterialOverride = new StandardMaterial3D {
                AlbedoColor = new Color(0.4f, 0.8f, 1f, 0.22f), EmissionEnabled = true, Emission = new Color(0.4f, 0.8f, 1f),
                EmissionEnergyMultiplier = 1.3f, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
            AddChild(_shieldBubble);
        }
        if (!shieldShown && _shieldBubble != null) { _shieldBubble.QueueFree(); _shieldBubble = null; }
    }

    private DamageType _lastType = DamageType.Lunar;
    private bool _lastCombo = false;

    // (NEW) universal damage-instance feedback: a soft "tick" on any hit, a bright "plink" on a crit. Spatial at the foe,
    // played on whichever machine resolves the hit (so the attacker always hears their own hits, host or client). Throttled
    // per-enemy so DoTs pulse instead of buzzing; a crit always plinks (and re-arms the throttle so a tick doesn't stack on it).
    private void HitFeedback(bool crit)
    {
        Vector3 at = GlobalPosition + Vector3.Up * Radius * 0.6f;
        if (crit) { Game.I.Sfx?.DamageTick(at, true); _hitSndT = 0.06f; return; }
        if (_hitSndT > 0f) return;
        _hitSndT = 0.08f;
        Game.I.Sfx?.DamageTick(at, false);
    }

    public void Hurt(float dmg, DamageType type = DamageType.Lunar, bool fromCombo = false, bool crit = false)
    {
        if (Dead) return;
        if (Remote)
        {
            // a client landed a hit: the host owns this enemy, so route the damage there (but give this machine local feedback)
            Game.I.NetMgr?.ReportHit(NetId, dmg, (int)type, crit);
            if (Game.I.DmgNumbers) { _popAccum += dmg; _popCol = DamageTypes.Col(type); if (crit) _popCrit = true; _flash = 0.12f; }
            HitFeedback(crit);
            return;
        }
        if (IsGoblin && Game.I.GoblinTime < 0f) Game.I.GoblinTime = 12f;   // chase clock starts on first strike
        var pl = Game.I.Player;
        if (pl != null) dmg *= pl.LunarNightMul(type);   // Lunar Witch: ALL lunar damage waxes stronger at night
        _lastType = type; _lastCombo = fromCombo;
        float dealt = dmg * MarkAmp;
        if (CurseT > 0f && (type == _curseBonusType || (int)type == _curseBonusType2)) dealt *= _curseBonusMul;   // (NEW) cursed foes take extra from the curse-bonus type(s) — Curse by default; Cursebrand adds a 2nd
        if (_armorDR > 0f && !crit) dealt *= (1f - _armorDR);                 // armored: crits punch through
        if (_shield > 0f) { float s = Mathf.Min(_shield, dealt); _shield -= s; dealt -= s; }   // shielded soak
        // (REMOVED the frozen "blue bank" — frozen foes now take NORMAL damage; a charged-RMB spear SHATTERS them for a flat burst + execute, no banking step)
        Hp -= dealt;
        Game.I.NoteEnemyDamage(dealt);   // (NEW) feeds the boss-wave DPS director + heat
        if (CurseGroup != 0 && !_curseShareGuard && _curseShareFrac > 0f && dealt > 0.5f)   // (NEW) tethered curse group shares this damage instance
        {
            ulong fr = Engine.GetProcessFrames();
            if (fr != _shareFrame) { _shareFrame = fr; _shareBudget = 1500; _shareWarned = false; }   // per-frame ceiling on shared-damage instances
            if (_shareBudget > 0)
            {
                float shared = dealt * _curseShareFrac;
                foreach (var o in Game.I.Enemies.ToArray())
                {
                    if (_shareBudget <= 0) break;
                    if (o == null || o == this || o.Dead || o.Remote || !GodotObject.IsInstanceValid(o) || o.CurseGroup != CurseGroup) continue;
                    _shareBudget--;
                    o._curseShareGuard = true; o.Hurt(shared, DamageType.Curse); o._curseShareGuard = false;
                }
            }
            else if (!_shareWarned) { _shareWarned = true; GD.PushWarning($"[perf] curse-share budget exhausted this frame ({Game.I.Enemies.Count} enemies, group {CurseGroup}) — capping the cascade to avoid a hang"); }
        }
        _flash = 0.12f;
        if (Game.I.DmgNumbers)
        {
            _popAccum += dealt;                       // batch rapid/DoT/AoE ticks into one number
            _popCol = DamageTypes.Col(type);
            if (MarkAmp > 1.01f) _popAmp = true;
            if (crit) _popCrit = true;
        }
        HitFeedback(crit);   // (NEW) universal hit tick / crit plink for ALL damage sources (melee, spells, AoE, bolts, DoTs)
        if (Hp <= 0)
        {
            if (pl != null && pl.Ult == Player.UltKind.Eclipse && pl.UltActive) pl.OnEclipseKill(GlobalPosition);
            Die();
        }
    }

    public int StatusMask()
    {
        int m = 0;
        if (_bleedT > 0f) m |= 1;
        if (SlowT > 0f) m |= 2;
        if (RootT > 0f) m |= 4;
        if (MarkT > 0f) m |= 8;
        if (_chargeT > 0f) m |= 16;
        if (_bleedT > 0f && _bleedRot) m |= 32;   // Blood Rot → bubbling crimson aura on clients
        if (_shield > 0f) m |= 64;                 // shielded affix bubble still up
        if (_type == "swarmer" && !_alerted) m |= (_idlePose & 3) << 7;   // (NEW) idle pose → 128/256
        else if (_type == "taker" && _takerState == 2) m |= (1 & 3) << 7;  // (NEW) wall-stun → lie pose on clients
        if (_screamT > 0f) m |= 512;               // (NEW) scream pulse
        if (FrozenT > 0f) m |= 1024;               // (NEW) frozen in ice
        m |= (Mathf.RoundToInt(FrozenBlueFrac * 15f) & 0xF) << 11;   // (NEW) blue ice-bar (coarse)
        m |= (Mathf.Min((int)FreezeStacks, 63) & 0x3F) << 15;        // (NEW) freeze stacks (for the indicator)
        if (CurseT > 0f) m |= 1 << 21;                               // (NEW) cursed
        m |= (Mathf.Min((int)CurseStacks, 63) & 0x3F) << 22;         // (NEW) curse stacks (overhead counter)
        m |= (CurseGroup & 0x7) << 28;                               // (NEW) low 3 bits of the tether group (for drawing links on all machines)
        return m;
    }

    public void Heal(float amt) { if (!Dead) Hp = Mathf.Min(MaxHp, Hp + amt); }

    public void Slow(float dur, float mul) { if (Remote) { Game.I.NetMgr?.ReportStatus(NetId, 1, dur, mul, 0f); return; } SlowT = Mathf.Max(SlowT, dur); SlowMul = mul; }
    public void Root(float dur) { if (Remote) { Game.I.NetMgr?.ReportStatus(NetId, 2, dur, 0f, 0f); return; } RootT = Mathf.Max(RootT, dur); }
    public void Mark(float dur, float amp, int jumps) { if (Remote) { Game.I.NetMgr?.ReportStatus(NetId, 3, dur, amp, jumps); return; } MarkT = Mathf.Max(MarkT, dur); MarkAmp = amp; MarkJumps = jumps; }

    // (NEW) The suck-beam builds curse; when it spreads, callers pass the same group id to tether foes together.
    // Tether/curse duration = the stack count in seconds (5 stacks → 5s), floored at 2s.
    public void AddCurse(float amt, int group, DamageType bonusType, float bonusMul, float shareFrac, int bonusType2 = -1)
    {
        if (Remote) { Game.I.NetMgr?.ReportCurse(NetId, amt, group, (int)bonusType, bonusMul, shareFrac, bonusType2); return; }
        if (Dead) return;
        CurseStacks += amt;
        CurseT = Mathf.Max(CurseT, Mathf.Max(2f, CurseStacks));
        if (group != 0) CurseGroup = group;
        _curseBonusType = bonusType; _curseBonusMul = bonusMul; _curseShareFrac = shareFrac; _curseBonusType2 = bonusType2;
    }

    // (NEW) The voodoo-doll crush (right-click): consume `frac` of the stacks and detonate them for curse damage.
    // Untethers this foe's group; foes that still have their own stacks can re-form a new one on the next beam tick.
    public void ConsumeCurse(float frac, float perStack, float stackCap = 5f)
    {
        if (Remote) { Game.I.NetMgr?.ReportStatus(NetId, 7, frac, perStack, stackCap); return; }
        if (Dead || CurseStacks <= 0f) return;
        float consumed = Mathf.Clamp(frac, 0f, 1f) * CurseStacks;
        if (frac < 0.999f) consumed = Mathf.Max(1f, Mathf.Floor(consumed));   // a tap always eats at least 1 stack
        consumed = Mathf.Min(consumed, CurseStacks);
        CurseStacks -= consumed;
        int grp = CurseGroup;
        // detonation damage tapers with diminishing returns toward a ceiling of `stackCap` EFFECTIVE stacks. tanh keeps the
        // first few stacks counting ~fully (1→~1, 2→~1.9), then flattens toward the ceiling (cap 5: 8 stacks→~4.6, ∞→5).
        // Keeps a fully-fed target from scaling to absurd numbers at base kit; affinity cards raise the ceiling.
        float effStacks = stackCap > 0.01f ? stackCap * (float)System.Math.Tanh(consumed / stackCap) : consumed;
        Hurt(perStack * effStacks, DamageType.Curse, true);   // (stacks REMOVED are still `consumed`; only the damage is tapered)
        if (grp != 0)   // break this group's tether (members keep their OWN stacks and can re-link later)
            foreach (var o in Game.I.Enemies.ToArray())
                if (o != null && !o.Dead && GodotObject.IsInstanceValid(o) && o.CurseGroup == grp) { o.CurseGroup = 0; }
        if (CurseStacks <= 0f) CurseT = 0f;
        Game.I.SpawnGroundSigil(GlobalPosition, 2.5f, DamageTypes.Col(DamageType.Curse));
    }

    // add freeze stacks (routes to host if a client applies it). Reaching the HP-scaled threshold encases the enemy in ice.
    // canRadiate: whether a freeze CAUSED by this application may itself spread Deep Winter's chill. The beam/shatter pass
    // true; Deep Winter's own aura passes false — so ambient-frozen foes DON'T re-radiate, capping the spread to one ring
    // around a genuine freeze instead of chain-freezing the whole map.
    public void AddFreeze(float amt, float threshMul = 1f, float durBonus = 0f, bool canRadiate = true)
    {
        if (Remote) { Game.I.NetMgr?.ReportStatus(NetId, 5, amt, threshMul, durBonus); return; }   // route to host WITH the caster's frost profile
        if (Dead || FrozenT > 0f) return;   // can't stack a frozen enemy
        _freezeThreshMul = Mathf.Min(_freezeThreshMul, threshMul);   // best-of: the coldest (Brittle-est) contributor sets the pace
        _freezeDurBonus = Mathf.Max(_freezeDurBonus, durBonus);      // best-of: the longest-freeze contributor wins
        FreezeStacks += amt;
        _freezeExpT = 2f;   // a new stack refreshes the whole stack's timeout
        Slow(0.5f, Mathf.Clamp(1f - (FreezeStacks / Mathf.Max(1f, FreezeThreshold)) * 0.6f, 0.4f, 0.95f));   // the longer the beam holds, the more it slows
        if (FreezeStacks >= FreezeThreshold) { _radiatesCold = canRadiate; Freeze(); }
    }

    private void Freeze()
    {
        FrozenT = 5f + _freezeDurBonus; FreezeStacks = 0f; _freezeExpT = 0f;
        _frozenBlueMax = 0f; _frozenBlue = 0f; _frozenBlueDmg = 0f;   // no blue bank anymore (FrozenBlueFrac stays 0 → the bar isn't drawn)
        RootT = Mathf.Max(RootT, FrozenT);   // held in place
        EnsureIceBlock(true);
        Game.I.Sfx?.Freeze(GlobalPosition);
        Game.I.NetMgr?.BroadcastVfx(48, GlobalPosition, Vector3.Zero, Radius, FrozenT, DamageTypes.Col(DamageType.Frost));   // ice VFX for allies
    }

    public void ShatterInstant() { if (FrozenT <= 0f) return; ShatterFreeze(true); }   // full-charge spear / Glacial Impaler DETONATES the accrued blue bar (explosion + AoE)
    public void ShatterFreeze() => ShatterFreeze(false);   // freeze timer ran out → melt (banked damage, no explosion)

    // Break the ice. detonate=true → a Frost witch's spear set it off: convert banked blue damage, explode it as an AoE, spread frost.
    // detonate=false → the freeze just melted: the enemy takes the banked damage but there's NO explosion, AoE, or spread.
    public void ShatterFreeze(bool detonate)
    {
        if (FrozenT <= 0f) return;
        var pw = Game.I.Player;
        // (REDESIGN) No blue-bank at all. A frozen foe takes normal damage; a DETONATE (full-charge spear) SHATTERS it for a
        // flat, player-scaled burst + a %-max-HP execute — an immediate payoff the moment you see the ice, no pre-banking.
        float burst = 0f;
        if (detonate)
        {
            float missing = MaxHp > 0f ? Mathf.Clamp(1f - Hp / MaxHp, 0f, 1f) : 0f;
            float powMul = pw != null ? pw.ShatterPowerMul : 1f;
            burst = ((pw != null ? pw.ShatterBurstDmg() : 24f) + MaxHp * (0.05f + 0.15f * missing)) * powMul;
        }
        float real = burst;   // detonate = the burst; melt = 0 (the foe already took its damage normally while frozen)
        FrozenT = 0f; _radiatesCold = false; if (_iceBlock != null) _iceBlock.Visible = false;
        _freezeThreshMul = 1f; _freezeDurBonus = 0f;   // next freeze accumulates its profile fresh
        if (detonate)
        {
            Game.I.SpawnFrostShatter(GlobalPosition, Radius);
            Game.I.Sfx?.IceShatter(GlobalPosition);
            Game.I.NetMgr?.BroadcastVfx(49, GlobalPosition, Vector3.Zero, Radius, 0f, DamageTypes.Col(DamageType.Frost));
            float area = 7.5f * (pw != null ? pw.S.SpellArea : 1f);   // bigger shatter burst radius; still scales with AoE cards
            float shard = burst * 0.3f;                               // modest AoE splash — Frost's strength is the single-target snipe (Forsaken keeps the AoE crown)
            bool cascade = pw != null && pw.ShatterCascade;
            ulong cfr = Engine.GetProcessFrames();
            if (cfr != _cascFrame) { _cascFrame = cfr; _cascBudget = 24; _cascWarned = false; }   // per-frame ceiling on chained shatters (a huge cluster can't blow up the frame)
            foreach (var o in Game.I.Enemies.ToArray())
            {
                if (o == null || o == this || o.Dead || !GodotObject.IsInstanceValid(o)) continue;
                if (o.GlobalPosition.DistanceTo(GlobalPosition) < area + o.Radius)
                {
                    if (cascade && o.Frozen && _cascBudget <= 0 && !_cascWarned) { _cascWarned = true; GD.PushWarning($"[perf] shatter-cascade budget exhausted this frame ({Game.I.Enemies.Count} enemies) — capping the chain to avoid a hang"); }
                    if (cascade && o.Frozen && _cascBudget > 0) { _cascBudget--; o.ShatterInstant(); }   // Shatter Cascade legendary chains the detonation (budget-capped)
                    else { o.Hurt(Mathf.Min(shard, o.MaxHp * 0.5f), DamageType.Frost); o.AddFreeze(pw != null ? pw.ShatterFreezeStacks : 1f, pw != null ? pw.FreezeThreshMul : 1f, pw != null ? pw.FrostDurBonus : 0f); }
                }
            }
            bool willDie = Hp - real <= 0f;
            if (pw != null && real > 0f) pw.OnHitDirect(this, willDie, real, DamageType.Frost);   // detonation builds combo + charges finishers
        }
        else
        {
            Game.I.SpawnFrostShatter(GlobalPosition, Radius * 0.5f); Game.I.Sfx?.IceShatter(GlobalPosition);   // freeze wore off — a quiet crack as the ice melts (no damage/AoE)
        }
        Hp -= real;   // detonated → the shatter burst; melted → 0 (the foe took its damage normally while frozen)
        if (Hp <= 0f) { var pl = Game.I.Player; if (pl != null && pl.Ult == Player.UltKind.Eclipse && pl.UltActive) pl.OnEclipseKill(GlobalPosition); Die(); }
    }

    // client proxy: reflect the host's active statuses so the tints/rings show (bitmask: 1 bleed,2 slow,4 root,8 mark,16 charge)
    public void SetRemoteStatus(int mask)
    {
        _bleedT = (mask & 1) != 0 ? 1f : 0f;
        SlowT = (mask & 2) != 0 ? 1f : 0f;
        RootT = (mask & 4) != 0 ? 1f : 0f;
        MarkT = (mask & 8) != 0 ? 1f : 0f;
        _chargeT = (mask & 16) != 0 ? 1f : 0f;
        _rotShow = (mask & 32) != 0;
        _shieldUp = (mask & 64) != 0;
        bool frozen = (mask & 1024) != 0;   // (NEW) mirror the ice block + blue bar + stacks
        FrozenT = frozen ? 1f : 0f;
        EnsureIceBlock(frozen);
        _remoteBlueFrac = ((mask >> 11) & 0xF) / 15f;
        FreezeStacks = (mask >> 15) & 0x3F;
        _remoteCursed = (mask & (1 << 21)) != 0;   // (NEW) mirror cursed glow + overhead counter + tether group
        CurseStacks = (mask >> 22) & 0x3F;
        CurseGroup = (mask >> 28) & 0x7;
        if ((_type == "swarmer" || _type == "taker") && _creature != null)   // (NEW) mirror idle/wall-stun pose + scream on the client proxy
        {
            _creature.IdlePose = (mask >> 7) & 3;
            bool scr = (mask & 512) != 0;
            if (scr && !_screamWas) _creature.Scream();
            _screamWas = scr;
        }
    }

    private void Die()
    {
        Dead = true;
        if (_type == "swarmer") Game.I.Sfx?.ZombieDeath(GlobalPosition);   // (NEW)
        if (_type == "taker") { ReleaseGrab(); Game.I.Sfx?.TakerDeath(GlobalPosition); }   // (NEW) free the captive
        if (Affix == 4 || (Game.I != null && Game.I.ActiveMutator == WaveMutator.Volatile && !IsBoss && !IsGoblin)) Explode();   // volatile affix OR the Volatile mutator: blast on death (players only, never other enemies)
        if (_splitter) { for (int i = 0; i < 2; i++) Game.I.SpawnEnemyAt("spawnling", GlobalPosition); }   // splitter: spawn two (host → synced)
        Game.I.Player?.OnBloodAuraKill(GlobalPosition);        // local blood witch: ANY death in her aura banks a stack
        Game.I.NetMgr?.BroadcastEnemyDeath(GlobalPosition);   // ally blood witches check their own aura too
        // a bleeding victim ruptures; a ROT victim also spreads the bleed to nearby foes (Blood Rot chains)
        if (_bleedT > 0f)
        {
            float burst = _bleedDps * 1.2f;
            foreach (var e in Game.I.Enemies.ToArray())
            {
                if (e == null || e == this || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
                if (GlobalPosition.DistanceTo(e.GlobalPosition) < 5f)
                {
                    e.Hurt(burst, DamageType.Blood, false);
                    if (_bleedRot) e.Bleed(_bleedDps, 4f, true);
                }
            }
            var v = new Vfx(); Game.I.AddChild(v); v.GlobalPosition = new Vector3(GlobalPosition.X, 0.6f, GlobalPosition.Z);
            v.Init(new SphereMesh { Radius = 2.5f, Height = 5f }, DamageTypes.Col(DamageType.Blood), 0.3f, 6f);
        }
        Game.I.Score += Score;
        Game.I.NetMgr?.BroadcastKill(Score);   // allies share the kill credit
        Game.I.RemoveEnemy(this);

        if (_lastCombo) Game.I.Hud?.AddKill(new Vector3(GlobalPosition.X, GlobalPosition.Y + Radius + 0.6f, GlobalPosition.Z), _lastType);

        if (IsGoblin) { Game.I.GoblinLoot(Elite); Game.I.NetMgr?.BroadcastGoblinLoot(Elite); QueueFree(); return; }

        Game.I.Sfx?.Death();
        if (IsBoss) Game.I.DropBossToken(this);

        Game.I.Kills++;

        if (MarkT > 0 && MarkJumps > 0)
        {
            Enemy best = null; float bd = 1e9f;
            foreach (var e in Game.I.Enemies.ToArray())
            {
                if (e == null || e.Dead) continue;
                float d = GlobalPosition.DistanceTo(e.GlobalPosition);
                if (d < 9f && d < bd) { bd = d; best = e; }
            }
            if (best != null) { best.Mark(2.5f, MarkAmp, MarkJumps - 1); best.Hurt(MaxHp * 0.18f, DamageType.Curse, false); }
        }

        int orbs = IsBoss ? 8 : (Elite ? 3 : 1);
        for (int i = 0; i < orbs; i++)
        {
            var orb = new Orb { Xp = (Score * 0.5f + 2.5f) / orbs, Tint = Col, NetId = Game.I.NextPickupId() };   // XP per kill trimmed (was Score*0.6+4) — slows early leveling; the flat term dominated trash-heavy waves (NEW)
            Game.I.AddChild(orb);
            Game.I.AddXpOrb(orb);   // capped add — persistent orbs can't pile up unbounded
            var off = new Vector3((float)GD.RandRange(-1.5, 1.5), 1.2f, (float)GD.RandRange(-1.5, 1.5));
            orb.GlobalPosition = new Vector3(GlobalPosition.X, 1.2f, GlobalPosition.Z) + off;
        }

        if (_type == "boss") { BossDeathSequence(); return; }   // THE HOLLOW MOON gets a dramatic drawn-out death; frees itself after
        QueueFree();
    }

    // THE HOLLOW MOON dies: a death cry + a final line, his shoulder goblins burst (red + green), he topples onto
    // his back, then his hollow body ruptures in rot & blood as he screams. Frees itself when the sequence ends.
    private void BossDeathSequence()
    {
        Game.I.Sfx?.BossRoar(GlobalPosition);   // opening death cry
        SayBossVox(_bossDeathLines[GD.RandRange(0, _bossDeathLines.Length - 1)], new Color(1f, 0.35f, 0.35f), 3f);

        if (_creature != null)
        {
            var zp = _creature.ShoulderPos(true);   // left = zombie goblin → GREEN blood mist
            var np = _creature.ShoulderPos(false);  // right = non-zombie goblin → RED blood
            Game.I.SpawnBloodMist(np, 3f);
            Game.I.SpawnPollen(zp, 3f, new Color(0.5f, 0.9f, 0.25f), 18, 1.5f, net: false);
            Game.I.VfxRing(zp, new Color(0.5f, 0.9f, 0.25f), 2.4f, 0.5f);
            Game.I.VfxRing(np, new Color(0.85f, 0.1f, 0.12f), 2.4f, 0.5f);
            _creature.PopGoblins();

            // topple backward + settle to the ground (minimal clipping)
            var tw = _creature.CreateTween();
            tw.TweenInterval(0.6f);
            tw.TweenProperty(_creature, "rotation", new Vector3(-1.35f, _creature.Rotation.Y, 0f), 1.1f).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.In);
            tw.Parallel().TweenProperty(_creature, "position", _creature.Position - new Vector3(0f, Radius * 0.55f, 0f), 1.1f);
        }

        // mid-fall: the hollow body ruptures in rot + blood, one more scream
        GetTree().CreateTimer(1.9f).Timeout += () =>
        {
            if (!GodotObject.IsInstanceValid(this) || Game.I == null) return;
            var at = GlobalPosition + new Vector3(0f, Radius, 0f);
            Game.I.SpawnBloodMist(at, Radius * 1.6f);
            Game.I.SpawnPollen(at, Radius * 1.7f, new Color(0.5f, 0.7f, 0.3f), 26, 1.6f, net: false);
            Game.I.VfxRing(GlobalPosition, new Color(0.7f, 0.3f, 0.3f), Radius * 2f, 0.6f);
            Game.I.Sfx?.BossRoar(GlobalPosition);
        };
        GetTree().CreateTimer(2.7f).Timeout += () => { if (GodotObject.IsInstanceValid(this)) QueueFree(); };
    }
}
