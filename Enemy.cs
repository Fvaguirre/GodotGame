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
public enum EBehav { Melee, Ranged, Charged, Flyer, Healer, Goblin, Boss, Zapper, Bomber, Diver, Hexer, Totem, Sapper, Lobber, Phalanx, Archer }   // Lobber = croc bomb-thrower; Phalanx/Archer = the warded formation (NEW)

public partial class Enemy : Node3D, Grove.Dev.Ai.IAiObservable
{
    // --- DEV visual-test harness (res://dev/ai). Read-only + a deterministic slash trigger; no gameplay coupling. ---
    public bool IsAuthoredGoblin => _creature?.IsAuthoredGoblin ?? false;
    public void DebugSlash(bool left) => _creature?.DebugSlash(left);
    public void DebugWalk(float move) => _creature?.DebugWalkSpeed(move);
    public Godot.Collections.Dictionary GetAiDebugState() => new()
    {
        { "type", _type },
        { "radius", Radius },
        { "hp", Hp },
        { "affix", Affix },
        { "elite", _eliteRing != null },
        { "authored_goblin", IsAuthoredGoblin },
        { "name", PingName },
    };

    public float Hp, MaxHp, Speed, Dmg, Radius;
    public int Score;
    public Color Col;
    public bool Dead = false;
    public bool Elite = false;
    public bool IsBoss = false;
    public bool IsGoblin = false;
    public string Label = "";
    public bool PlateOccluded = false;   // (PERF) cached HUD nameplate line-of-sight result — the raycast is throttled to ~15Hz, not run every draw
    public ulong PlateLosMs = 0;         // next tick (ms) at which DrawEnemyBars re-runs the occlusion raycast for this foe
    public int NetId = 0;      // host-assigned id for multiplayer sync
    public int TypeIdx = 0;    // index into EnemyKinds table for client-side rendering
    public float SizeMul = 1f; // (NEW) power/variety size multiplier — host computes it, applied to Radius in _Ready, synced to clients so co-op renders matching sizes + hitboxes

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

    // Nameplate tag shown by the HUD. Two kinds of info:
    //  • ROLLED elite AFFIX (rare modifier) → a small ICON, because it's a modifier on top of the base enemy.
    //  • the enemy's BASE archetype ability → a WORD ("Stunner", "Rooter", "Empowerer"…), because that's just what it IS.
    // Both can show together (e.g. a frenzied ptero → "💢 Stunner"). Bosses/goblins/the Taker keep their name label instead.
    public string PlateTag()
    {
        if (IsBoss || IsGoblin || _type == "taker") return "";
        string icon = Affix switch
        {
            1 => "\U0001F537",   // shielded → blue diamond (energy barrier)
            2 => "\U0001F4A2",   // frenzied → rage
            3 => "\U0001FA78",   // vampiric → blood drop (lifesteal)
            4 => "\U0001F4A5",   // volatile → explosion (blows up on death)
            5 => "\U0001F6E1",   // armored → shield
            _ => ""
        };
        string word = _type switch
        {
            "sentinel" => "Armored",     // heavy-plated tank (crits punch through)
            "jtroll"   => "Charger",     // charges + knocks you back
            "ptero"    => "Stunner",     // ranged stun bolt
            "zapper"   => "Stunner",     // ranged stun bolt
            "snake"    => "Rooter",      // roots you on touch
            "croc"     => "Bomber",      // lobs timed bombs
            "bomber"   => "Bomber",      // rushes + self-detonates
            "healer"   => "Healer",      // heals its allies
            "hexer"    => "Hexer",       // curse + snare
            "wardbane" => "Dispeller",   // strips your shield/wards
            "splitter" => "Splitter",    // splits into two on death
            "totem"    => "Empowerer",   // hastes nearby allies
            "diver"    => "Diver",       // dive-bombs from above
            "bat"      => "Diver",       // dive-bombs from above
            "caster"   => "Caster",      // arcane bolt-thrower
            _ => ""
        };
        if (icon.Length > 0 && word.Length > 0) return icon + " " + word;
        return icon.Length > 0 ? icon : word;
    }

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
        if (_tgtIsEnemy)   // (PUPPET) its own slash, landing on its own ally — attributed to whoever turned it so the kill still pays out
        {
            if (Puppeted)
            {
                PuppetTgt.PuppetHurt(_puppetOwner, dmg);
                // (DANSE MACABRE) the brawl FEEDS the mechanic — but at the deepest generation, so infighting can never
                // seed a fresh detonation chain on top of the splash one. That's the whole reason this is capped here.
                if (_puppetFeed > 0f) PuppetTgt.AddDoom(_puppetFeed, _puppetOwner, DoomMaxGen);
            }
            return;
        }
        if (_tgtIsMinion) return;   // the ent takes contact damage on its owner's machine; no player here
        if (_tgtPeer == 0) { var pl = Game.I.Player; if (pl != null) pl.Hurt(dmg, GlobalPosition); }
        else Game.I.NetMgr?.DamagePlayer(_tgtPeer, dmg);
        if (_type == "swarmer") { if (_tgtPeer == 0) Game.I.Player?.SlowMe(1.2f, 0.6f); else Game.I.NetMgr?.SlowPlayer(_tgtPeer, 1.2f, 0.6f); Game.I.Sfx?.ZombieAttack(GlobalPosition); }   // (NEW) swarmer hits slow you + attack snarl   // route to the ally who's being hit
        if (_type == "jtroll") { if (_tgtPeer == 0) Game.I.Player?.Knockback(GlobalPosition, 18f); else Game.I.NetMgr?.KnockbackPlayer(_tgtPeer, GlobalPosition, 18f); }   // (CHANGED) troll charge KNOCKS YOU BACK instead of stunning — the jungle already has plenty of stuns
        if (_type == "snake") Game.I.TrySnakeRoot(_tgtPeer, NetId);   // (NEW) snake touch roots you — ground-only, throttled per player, ends on the snake's death
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

    // ---- Ember: burn stacks → Living Bomb (NEW) ----
    private float _burnStacks = 0f, _burnPerStack = 0f, _burnT = 0f, _burnTick = 0f, _bombFlat = 0f;
    private int _livingBombStacks = 0, _remoteLivingBomb = 0, _burnOwner = 1;   // _burnOwner = caster peer (for Wildfire Rush burn-tick lifesteal)
    private float _remoteBurn = 0f;
    public float BurnStacks => Remote ? _remoteBurn : _burnStacks;
    public void SetRemoteBurn(float b) { _remoteBurn = b; }   // (NEW) client burn stacks (synced via the snapshot's burn array)
    public int LivingBombStacks => Remote ? _remoteLivingBomb : _livingBombStacks;
    public float LivingBombThreshold => Mathf.Clamp(3f + MaxHp / 45f, 3f, 60f);   // HP-scaled, like the freeze threshold
    public void AddBurn(float amt, float perStack, float bombFlat, float durBonus = 0f, int owner = 0)
    {
        if (Remote) { Game.I.NetMgr?.ReportStatus(NetId, 6, amt, perStack, bombFlat); return; }   // client → host (status kind 6; host uses sender as owner)
        if (Dead) return;
        _burnStacks += amt;
        _burnPerStack = Mathf.Max(_burnPerStack, perStack);   // best-of the caster's burn power
        _bombFlat = Mathf.Max(_bombFlat, bombFlat);
        _burnT = Mathf.Max(_burnT, 3.5f + durBonus);
        _burnOwner = owner != 0 ? owner : (Game.I != null ? Game.I.LocalPeer : 1);
        while (_burnStacks >= LivingBombThreshold) { _burnStacks -= LivingBombThreshold; _livingBombStacks++; TriggerLivingBomb(); }   // each threshold crossing = a Living Bomb stack + a blast on THIS foe
    }
    private void TriggerLivingBomb()   // reaching Living Bomb status → an immediate blast on THIS foe ONLY (flat, base-scaled). Repeats as stacks pile.
    {
        Hurt(_bombFlat, DamageType.Ember, false);
        Game.I.SpawnEmberBurst(GlobalPosition + Vector3.Up * Radius * 0.5f, Radius * 1.5f);   // broadcasts kind 21 → allies see it
        Game.I.Sfx?.ModEmber(GlobalPosition);
        if (Game.I.Player != null && Game.I.Player.EmberWitch) Game.I.MyStats.Highlight++;   // Ember highlight = bombs detonated
    }
    private bool _bleedRot = false;
    private float _bleedBurstMul = 1f;   // (OVERHAUL) Hemorrhage Rupture: scales the on-death blood burst
    private float _rotBubT = 0f;
    private bool _rotShow = false;     // client mirror of the rot state (status bit 32)

    // (CRIMSON RITE) the pentagram's shockwave cuts this foe down — a heavier burst of the same crimson gashes
    public void RiteSlash() { for (int i = 0; i < 3; i++) SpawnBleedSlash(); }

    // (NEW) a couple of short bright-crimson gashes flick across the body — reads as "bleeding" (distinct from rot's rising bubbles)
    private void SpawnBleedSlash()
    {
        if (Game.I == null) return;
        var c = DamageTypes.Col(DamageType.Blood).Lerp(new Color(1f, 0.2f, 0.2f), 0.35f);
        var mat = Game.ToonEmissive(c, 3.4f, 0f);
        for (int i = 0; i < 2; i++)
        {
            float len = Radius * (0.55f + GD.Randf() * 0.5f);
            var slash = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(len, 0.09f, 0.03f) }, MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            Game.I.AddChild(slash);
            float a = GD.Randf() * Mathf.Tau, rr = Radius * (0.72f + GD.Randf() * 0.32f);
            float y = 0.4f + GD.Randf() * (Radius * 1.5f);
            var pos = GlobalPosition + new Vector3(Mathf.Cos(a) * rr, y, Mathf.Sin(a) * rr);
            slash.GlobalPosition = pos;
            slash.LookAt(pos + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)), Vector3.Up);   // face outward off the silhouette
            slash.RotateObjectLocal(Vector3.Forward, GD.Randf() * Mathf.Pi);               // random diagonal — a slash, not a bar
            var tw = slash.CreateTween(); tw.SetParallel(true);
            tw.TweenProperty(slash, "position", pos + new Vector3(0f, -0.35f, 0f), 0.35f);   // slight downward drip
            tw.TweenProperty(slash, "transparency", 1f, 0.35f);
            tw.SetParallel(false);
            tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(slash)) slash.QueueFree(); }));
        }
    }

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
    // (NEW) HUD threat telegraph: this ranged foe is winding up a shot (sieger charge / zapper / hexer / sapper tell)
    public bool Telegraphing => _chargeT > 0f || _zapTele > 0f || _hexTele > 0f;
    public float TeleFrac => _chargeT > 0f && _chargeDur > 0.01f ? Mathf.Clamp(1f - _chargeT / _chargeDur, 0f, 1f)
                           : _zapTele > 0f ? Mathf.Clamp(1f - _zapTele / 1.05f, 0f, 1f)
                           : _hexTele > 0f ? Mathf.Clamp(1f - _hexTele / 1.1f, 0f, 1f) : 0f;
    // (NEW) a diver/bat committed to its swoop — a body threat the HUD warns about like a projectile
    public bool Diving => _diving;
    // (NEW) special-infected charge state — Taker rearing back (4) or dashing to grab (1). Locator uses the existing IsSpecial.
    public bool SpecialCharging => (IsTaker && (_takerState == 4 || _takerState == 1)) || (IsPhalanx && (_chargeWind > 0f || _chargeLunge > 0f));
    public string SpecialTag => IsPhalanx ? "PHALANX" : "TAKER";
    public string SpecialWarn => IsPhalanx ? "CHARGE!" : "GRAB!";   // (NEW) the loud on-screen callout while this special commits to its big move
    private const float ThrowGravity = -26f, ThrowHurtSpeed = 9f, ThrowDmgPer = 2.4f;
    private float _fallDmgMul = 1f;   // (NEW) Updraft Tempest: multiplies the fall damage taken on this throw's landing
    private float _popAccum = 0f, _popT = 0f;
    private Color _popCol = Colors.White;
    private bool _popAmp = false;
    private bool _popCrit = false;

    // ===== WARDED PHALANX (NEW) — a compound miniboss: one ward-bearer at the front, up to 8 archers behind it =====
    // While the ward stands, NOTHING in the formation can be hurt: all damage pours into the ward pool, and the archers
    // are untouchable. They answer with a massed volley that paints a circle of falling arrows on whoever they're
    // targeting, which forces the party to keep moving. Break the ward and the unit inverts: the bearer drops its guard
    // and charges you (knockback + stun), while the now-defenceless archers scatter and cower behind other foes — until
    // another phalanx shows up, at which point they'll run to enlist in ITS ranks, making that ward tougher and its
    // volley deadlier. Every scaling number keys off the live archer count, so a merged super-formation is a real threat.
    public const int MaxArchers = 8;
    private float _wardBase = 0f;                        // pre-archer ward pool (set in Configure, scales with depth)
    private float _wardHp = 0f, _wardMax = 0f;
    private float _volleyT = 5f;                         // countdown to the next massed volley
    private float _rWard = -1f;                          // CLIENT mirror of the ward fraction (-1 = no ward / unknown)
    private bool _rGuarded = false;                      // CLIENT mirror of "protected by a leader's ward"
    private Enemy _leader = null;                        // archer -> its ward-bearer
    private readonly System.Collections.Generic.List<Enemy> _squad = new();   // bearer -> its archers
    private MeshInstance3D _wardDome, _wardRunes;
    private float _wardPulse = 0f;
    private float _joinScanT = 0f;
    public float WardFrac => Remote ? Mathf.Max(0f, _rWard) : (_wardMax > 0.01f ? Mathf.Clamp(_wardHp / _wardMax, 0f, 1f) : 0f);
    public bool WardUp => Remote ? _rWard > 0.001f : _wardHp > 0.01f;
    public int ArcherCount => _squad.Count;
    // an archer is untouchable exactly while its bearer's ward stands — that's the whole puzzle of the fight
    public bool WardGuarded => Remote ? _rGuarded : (_leader != null && GodotObject.IsInstanceValid(_leader) && !_leader.Dead && _leader.WardUp);
    public bool IsPhalanx => _type == "phalanx";
    public bool IsArcher => _type == "archer";
    public void SetRemoteWard(float frac) { _rWard = frac; }
    public void SetRemoteGuarded(bool on) { _rGuarded = on; }

    // (NEW REFLOW) trailing-chase bookkeeping — see Game.RunReflowDirector
    private float _chaseFarT = 0f;
    public float ChaseFarT => _chaseFarT;
    public void ResetChaseFar() { _chaseFarT = 0f; _avoidSign = 0f; _sepPush = Vector3.Zero; }
    // safe to pick up and re-insert somewhere else? Bosses/specials/goblins are set-pieces you're meant to leave behind
    // or chase down; a grabbed Taker, an airborne fling, a downed foe and un-woken ambushers all own their own position.
    public bool Relocatable => !Dead && !Remote && !IsBoss && !IsGoblin && !IsSpecial && !IsArcher
                               && !_thrown && _getUpT <= 0f && _grabPeer == 0 && _behav != EBehav.Totem
                               && RootT <= 0f && FrozenT <= 0f                     // (NEW) don't blink a HARD-CC'd foe away — it's held in a placed field (Blizzard/DeepFreeze/GroveGuardian root), let the field keep it
                               && !PhoenixHeld                                     // (PHOENIX) never blink a foe the phoenix is carrying
                               && !(_type == "swarmer" && !_alerted);

    // (PHOENIX) carried by a phoenix dive: locked to PhoenixHoldPos, all AI/attacks skipped
    public bool PhoenixHeld = false; public Vector3 PhoenixHoldPos;
    public void PhoenixGrab(Vector3 pos) { if (Remote || Dead) return; if (IsTaker && _grabPeer != 0) ReleaseGrab(); _thrown = false; _climbing = false; PhoenixHeld = true; PhoenixHoldPos = pos; }
    public void PhoenixRelease() { PhoenixHeld = false; }

    private bool _climbing = false;               // (NEW) hauling itself up a vertical face: half speed, and a crit/knock/fling peels it off
    private Vector3 _climbDir = Vector3.Forward;  // horizontal direction INTO the wall we're scaling
    public bool Climbing => _climbing;
    private bool _flungFromClimb = false;         // (NEW) the current fling started as a wall-peel → play the climb-slip fall lead-in
    private bool AuthBiped => _creature != null && _creature.IsAuthoredGoblin;   // (NEW) authored biped (goblin/zombie/ogre/taker) → drive real fall/get-up CLIPS instead of the procedural pitch-topple

    // push the enemy away from `from` (negative force pulls toward it)
    public void Knockback(Vector3 from, float force)
    {
        if (IsArcher && WardGuarded) return;   // (NEW) a warded archer can't be shoved out of formation — the ward shelters it from displacement too, so CC ults don't break the "unbreakable" ward phase
        var d = GlobalPosition - from; d.Y = 0;
        if (d.LengthSquared() < 0.01f) d = Vector3.Forward;
        d = d.Normalized();
        if (_climbing && force > 0f) { PeelOffWall(d * Mathf.Max(3.5f, force * 3f)); return; }   // (NEW) knocked off the wall mid-climb → it falls
        _knock += d * force * 6f;
    }

    // (NEW) shaken off a wall mid-climb (crit / knockback / fling). Hands the foe to the existing throw arc, so
    // UpdateThrown → EndThrow gives us impact-scaled fall damage, the topple/get-up stagger and the MP sync for free —
    // a foe peeled off near the top of a keep hits the dirt far harder than one shrugged off two metres up.
    private void PeelOffWall(Vector3 push)
    {
        if (Remote || Dead || _thrown || !_climbing) return;
        _climbing = false;
        _flungFromClimb = true;   // (NEW) the resulting fall opens on the climb-slip clip
        push.Y = 0f;
        if (push.LengthSquared() < 0.01f) push = -_climbDir * 4f;
        Fling(push + Vector3.Up * 2.5f);   // a little pop so it clears the face before gravity takes over
    }

    // ===== PHALANX: formation bookkeeping (host only) =====

    // stand up a fresh formation: this bearer takes command of `archers` and raises its ward
    public void FormPhalanx(System.Collections.Generic.List<Enemy> archers)
    {
        if (!IsPhalanx) return;
        foreach (var a in archers) if (a != null && a.IsArcher) { a._leader = this; _squad.Add(a); }
        RecomputeWard(true);
    }

    // a loose archer joins this bearer's ranks — the ward grows AND heals by the delta, and the volley gets meaner.
    // This is the escalation the player has to respect: ignore a broken unit's stragglers and the next one is worse.
    public bool EnlistArcher(Enemy a)
    {
        if (!IsPhalanx || a == null || !a.IsArcher || a.Dead || Dead || !WardUp) return false;
        if (_squad.Count >= MaxArchers) return false;
        if (a._leader == this) return false;
        a._leader?._squad.Remove(a);
        a._leader = this; _squad.Add(a);
        float before = _wardMax;
        RecomputeWard(false);
        _wardHp = Mathf.Min(_wardMax, _wardHp + (_wardMax - before));   // the new body reinforces the barrier
        Game.I?.NetMgr?.BroadcastWardGuard(a.NetId, true);
        Game.I?.Sfx?.Impact(DamageType.Arcane);
        Game.I?.Hud?.Banner("an archer falls in — the ward thickens");
        return true;
    }

    private void RecomputeWard(bool full)
    {
        PruneSquad();
        _wardMax = _wardBase * (1f + 0.35f * _squad.Count);   // 3 archers ≈ 2.05×, 8 ≈ 3.8× the bare pool
        if (full) _wardHp = _wardMax;
        _wardHp = Mathf.Min(_wardHp, _wardMax);
    }

    private void PruneSquad()
    {
        for (int i = _squad.Count - 1; i >= 0; i--)
        { var a = _squad[i]; if (a == null || !GodotObject.IsInstanceValid(a) || a.Dead) _squad.RemoveAt(i); }
    }

    // the ward falls: the bearer drops its guard and charges, the archers are suddenly killable and bolt for cover
    private void BreakWard()
    {
        _wardHp = 0f;
        PruneSquad();
        foreach (var a in _squad) { a._leader = null; a._fleeing = true; a._hasFlee = false; Game.I?.NetMgr?.BroadcastWardGuard(a.NetId, false); }
        _squad.Clear();
        Game.I?.NetMgr?.BroadcastWard(NetId, 0f);
        WardShatter();
        Game.I?.Hud?.Banner("the ward shatters — the bearer charges!");
    }

    private bool _shattered = false;
    private void WardShatter()
    {
        _shattered = true;
        var c = new Color(0.62f, 0.45f, 1f);
        Game.I?.Sfx?.Impact(DamageType.Arcane);
        Game.I?.SpawnPoof(GlobalPosition);
        if (_wardDome != null)
        {
            var tw = _wardDome.CreateTween();
            tw.TweenProperty(_wardDome, "scale", Vector3.One * 1.5f, 0.32f).SetTrans(Tween.TransitionType.Back);
            tw.Parallel().TweenProperty(_wardDome, "transparency", 1f, 0.32f);
            tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(_wardDome)) _wardDome.QueueFree(); _wardDome = null; }));
        }
        if (_wardRunes != null) { _wardRunes.QueueFree(); _wardRunes = null; }
        // knock nearby witches back a step — the barrier bursting outward reads as a real event
        var pl = Game.I?.Player;
        if (pl != null && pl.GlobalPosition.DistanceTo(GlobalPosition) < WardRadius + 2f) pl.Knockback(GlobalPosition, 6f);
        _ = c;
    }

    public float WardRadius => Radius * 3.4f;   // the dome the archers shelter inside

    // a shot that hits a warded archer: no damage, no number — just a spark so you can SEE it being turned away
    private float _deflectT = 0f;
    private void WardDeflect()
    {
        _flash = 0.10f;
        if (_deflectT > 0f) return;
        _deflectT = 0.16f;
        Game.I?.Sfx?.DamageTick(GlobalPosition + Vector3.Up * Radius, false);
        Game.I?.SpawnPollen(GlobalPosition + Vector3.Up * Radius, Radius * 1.4f, new Color(0.62f, 0.45f, 1f), 4, 3f, net: false);
    }

    // ===== PHALANX: visuals =====
    private void UpdateWardVisual(float dt)
    {
        bool up = WardUp;
        if (up && _wardDome == null && !_shattered) BuildWardDome();
        if (!up && _wardDome != null && !_shattered) { _shattered = true; WardShatter(); }   // clients reach this off the synced fraction; the host from BreakWard
        if (_wardDome == null) return;
        _wardPulse += dt;
        float f = WardFrac;
        // as the ward is worn down it dims, tightens and flickers faster — a readable "almost there" tell
        float flick = 0.5f + 0.5f * Mathf.Sin(_wardPulse * Mathf.Lerp(2.2f, 11f, 1f - f));
        var mat = _wardDome.MaterialOverride as StandardMaterial3D;
        if (mat != null)
        {
            var bc = new Color(0.55f, 0.42f, 0.98f).Lerp(new Color(1f, 0.35f, 0.45f), 1f - f);   // violet → angry red as it fails
            mat.AlbedoColor = new Color(bc.R, bc.G, bc.B, 0.10f + 0.20f * f * (0.6f + 0.4f * flick));
            mat.EmissionEnabled = true; mat.Emission = bc; mat.EmissionEnergyMultiplier = 0.8f + 2.4f * f * flick;
        }
        _wardDome.Scale = Vector3.One * (0.92f + 0.08f * f);
        if (_wardRunes != null) _wardRunes.RotationDegrees = new Vector3(0, _wardPulse * 26f, 0);
    }

    private void BuildWardDome()
    {
        float r = WardRadius;
        _wardDome = new MeshInstance3D { Mesh = new SphereMesh { Radius = r, Height = r * 2f } };
        _wardDome.MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.55f, 0.42f, 0.98f, 0.22f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            EmissionEnabled = true,
            Emission = new Color(0.55f, 0.42f, 0.98f),
            EmissionEnergyMultiplier = 2.2f,
        };
        _wardDome.Position = new Vector3(0, Radius * 0.4f, 0);
        AddChild(_wardDome);

        // a sigil ring scribed on the ground at the dome's foot, so the safe/blocked area reads from the outside too
        _wardRunes = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = r * 0.94f, OuterRadius = r } };
        _wardRunes.MaterialOverride = Game.Emissive(new Color(0.72f, 0.58f, 1f), 2.6f);
        _wardRunes.Position = new Vector3(0, -Radius * 0.85f, 0);
        AddChild(_wardRunes);
    }

    private float _wardNetT = 0f;
    private void UpdatePhalanxState(float dt)
    {
        if (IsPhalanx)
        {
            UpdateWardVisual(dt);
            if (!Remote && _wardHp > 0f)
            {
                PruneSquad();
                if (_squad.Count == 0) BreakWard();   // last archer down → nothing left to shelter, the barrier collapses
                else
                {
                    _wardNetT -= dt;
                    if (_wardNetT <= 0f) { _wardNetT = 0.2f; Game.I?.NetMgr?.BroadcastWard(NetId, WardFrac); }
                }
            }
        }
        else if (IsArcher && !Remote && _leader != null && (!GodotObject.IsInstanceValid(_leader) || _leader.Dead || !_leader.WardUp))
        { _leader = null; _fleeing = true; Game.I?.NetMgr?.BroadcastWardGuard(NetId, false); }
    }

    // ===== PHALANX: the massed volley =====
    // the bearer barks the order; the rank raises their bows to the sky and holds the draw. Nothing is committed yet —
    // the circle is only painted when they LOOSE, so the aiming pose is the earliest tell that a volley is coming.
    private void BeginVolley()
    {
        PruneSquad();
        if (_squad.Count == 0) return;
        _volleyWind = VolleyDraw;
        foreach (var a in _squad) { a.AimSky(true); a.Quip(); Game.I?.NetMgr?.BroadcastArcherPose(a.NetId, 1); }
        Game.I?.Sfx?.EnemyGrowl(GlobalPosition);
    }

    private void FireVolley()
    {
        PruneSquad();
        int n = _squad.Count;
        if (n <= 0 || Game.I == null) return;
        Vector3 aim = _tgt; aim.Y = 0f;
        float dps = Dmg * 0.18f * n;                                // 3 archers ≈ 14 dps at base, 8 ≈ 37 — scales with depth via Dmg
        float venom = Mathf.Min(5f, 0.625f * n);                    // capped at the promised 5/sec with a full 8-strong rank
        var v = new ArrowVolley();
        Game.I.AddChild(v);
        v.Init(aim, 6f + 0.25f * n, dps, venom);                    // a bigger rank paints a bigger circle
        Game.I.NetMgr?.BroadcastVolley(aim, 6f + 0.25f * n, dps, venom);
        foreach (var a in _squad) { a.Loose(); Game.I.NetMgr?.BroadcastArcherPose(a.NetId, 0); }   // every archer releases on the same frame
        _creature?.SetSwing(0f);
    }

    private const float VolleyDraw = 0.85f;   // seconds the rank spends visibly aiming before it looses
    private float _volleyWind = 0f;

    // ---- archer bow rig + quips ----
    private Node3D _bow; private float _aim = 0f, _aimT = 0f;
    private Label3D _quip; private float _quipT = 0f;
    private static readonly string[] Quips = {
        "loose!", "i hope this hits", "my arms are killing me",
        "mind the trees this time", "aim UP, gerald", "i'm gonna be so sore",
        "left a bit... no, my left", "that's the last of my good arrows",
        "don't watch, it's embarrassing", "i counted three of them?",
        "this is why i drew the short straw", "nocked! ...i think",
    };
    public void AimSky(bool on) { _aimT = on ? 1f : 0f; }
    public void Loose()
    {
        _aimT = 0f;
        _creature?.Strike();
        if (_bow != null && GodotObject.IsInstanceValid(_bow))
        {
            var tw = _bow.CreateTween();   // snap the limbs forward on release
            tw.TweenProperty(_bow, "scale", new Vector3(1f, 0.82f, 1f), 0.06f);
            tw.TweenProperty(_bow, "scale", Vector3.One, 0.22f).SetTrans(Tween.TransitionType.Elastic);
        }
        Game.I?.SpawnPollen(GlobalPosition + Vector3.Up * Radius * 2.2f, 0.8f, new Color(0.72f, 0.58f, 1f), 6, 6f, net: false);
        Game.I?.Sfx?.EnemyShoot(GlobalPosition);
    }
    public void Quip()
    {
        if (GD.Randf() > 0.55f) return;   // not every archer pipes up — a chorus of them would be noise
        _quip ??= MakeQuipLabel();
        if (_quip == null) return;
        _quip.Text = Quips[GD.RandRange(0, Quips.Length - 1)];
        _quip.Visible = true;
        _quipT = 2.2f;
    }
    private Label3D MakeQuipLabel()
    {
        var l = new Label3D {
            FontSize = 34, OutlineSize = 10, PixelSize = 0.0055f,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, NoDepthTest = true,
            Modulate = new Color(0.93f, 0.88f, 1f), OutlineModulate = new Color(0.04f, 0.02f, 0.07f, 0.95f),
            Position = new Vector3(0, Radius * 3.1f, 0), Visible = false,
        };
        AddChild(l);
        return l;
    }
    // build the bow once, parented to the creature so it rides the archer's facing
    private void BuildBow()
    {
        _bow = new Node3D { Position = new Vector3(Radius * 0.52f, Radius * 1.15f, Radius * 0.35f) };
        _creature.AddChild(_bow);
        var wood = Game.Toon(new Color(0.26f, 0.17f, 0.09f), 0.8f, 0.3f, 0.03f);
        var stringMat = Game.ToonEmissive(new Color(0.80f, 0.70f, 1f), 1.5f, 0f);
        float lr = Radius * 0.78f;
        // two swept limbs + a taut string: reads as a bow in silhouette without a real curve mesh
        for (int s = -1; s <= 1; s += 2)
        {
            var limb = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.07f, lr, 0.07f) }, MaterialOverride = wood };
            limb.Position = new Vector3(0, s * lr * 0.45f, 0);
            limb.RotationDegrees = new Vector3(0, 0, s * 16f);
            _bow.AddChild(limb);
        }
        var str = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.02f, lr * 1.75f, 0.02f) }, MaterialOverride = stringMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        str.Position = new Vector3(0, 0, -0.13f);
        _bow.AddChild(str);
        var shaft = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.045f, 0.045f, lr * 1.25f) }, MaterialOverride = Game.ToonEmissive(new Color(0.66f, 0.5f, 1f), 2.2f, 0f), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        shaft.Position = new Vector3(0, 0, 0.18f);
        _bow.AddChild(shaft);
    }
    // hold the bow low at rest, swing it up to a high loft while drawing — the sky-aim IS the telegraph
    private void UpdateBowPose(float dt)
    {
        if (_quipT > 0f) { _quipT -= dt; if (_quipT <= 0f && _quip != null) _quip.Visible = false; }
        if (_bow == null || !GodotObject.IsInstanceValid(_bow)) return;
        _aim = Mathf.MoveToward(_aim, _aimT, dt * 3.4f);
        _bow.RotationDegrees = new Vector3(Mathf.Lerp(6f, -62f, _aim), 0f, Mathf.Lerp(-8f, 4f, _aim));   // muzzle-down → lofted at the sky
        _bow.Position = new Vector3(Radius * 0.52f, Radius * (1.15f + 0.32f * _aim), Radius * (0.35f + 0.12f * _aim));
    }

    // ===== PHALANX: the ward-bearer's AI =====
    private void MovePhalanx(Player p, float dt, float spdMul)
    {
        PruneSquad();
        Vector3 to = _tgt - GlobalPosition; to.Y = 0f;
        float d = to.Length();
        Vector3 dir = d > 0.01f ? to / d : Vector3.Forward;

        if (WardUp)
        {
            // GUARDED: an advancing siege line. It closes to volley range and then holds, keeping its archers in
            // range of you without ever letting them get flanked. Standing still is fatal — the volley is coming.
            float hold = 24f;
            Vector3 want = Vector3.Zero;
            if (d > hold + 3f) want = dir;
            else if (d < hold - 8f) want = -dir;   // you rushed the line: it backs off so the archers keep their spacing
            if (want.LengthSquared() > 0.001f && spdMul > 0f)
                GlobalPosition = ClampArena(GlobalPosition + AvoidBlockers(want) * Speed * spdMul * dt);
            FaceTarget(dt);
            if (_volleyWind > 0f)   // the rank is drawing — hold, then loose
            {
                _volleyWind -= dt;
                if (_volleyWind <= 0f) FireVolley();
                return;
            }
            _volleyT -= dt;
            if (_volleyT <= 0f && _squad.Count > 0 && d < 60f)
            {
                _volleyT = VolleyEvery * Pace;
                BeginVolley();
            }
            return;
        }
        // BROKEN: the guard is gone and so is the patience — it barrels in, shield first, to knock you flat.
        if (_chargeLunge > 0f)
        {
            _chargeLunge -= dt;
            GlobalPosition = ClampArena(GlobalPosition + _slamDir * (Speed * 4.2f) * dt);
            if (d < Radius + 2.2f) { ShieldSlam(); _chargeLunge = 0f; _slamCd = 4.5f; }
            else if (_chargeLunge <= 0f) _slamCd = 3.2f;
            return;
        }
        if (_chargeWind > 0f)
        {
            _chargeWind -= dt;
            _creature?.SetSwing(Mathf.Clamp(1f - _chargeWind / 0.7f, 0f, 1f));
            FaceTarget(dt);
            if (_chargeWind <= 0f) { _creature?.SetSwing(0f); _slamDir = dir; _chargeLunge = 0.85f; Game.I?.Sfx?.EnemyGrowl(GlobalPosition); }
            return;
        }
        _slamCd -= dt;
        if (_slamCd <= 0f && d < 26f && d > 3.5f) { _chargeWind = 0.7f; return; }
        MoveMelee(p, dt, spdMul);   // otherwise it just closes like any other heavy
    }

    private const float VolleyEvery = 7f;
    private float _slamCd = 2f, _chargeWind = 0f, _chargeLunge = 0f;
    private Vector3 _slamDir = Vector3.Forward;

    private void ShieldSlam()
    {
        _creature?.Strike();
        Game.I?.Sfx?.Impact(DamageType.Physical);
        Game.I?.NetMgr?.HurtStunPlayersIn(GlobalPosition, Radius + 5f, Dmg, 1.3f);
        var pl = Game.I?.Player;
        if (pl != null && pl.GlobalPosition.DistanceTo(GlobalPosition) < Radius + 6f) pl.Knockback(GlobalPosition, 16f);
        Game.I?.NetMgr?.StormForce(GlobalPosition, Radius + 6f, 3, 16f);   // shove allies too (no damage — HurtStun already paid it)
    }

    private void FaceTarget(float dt)
    {
        if (_creature == null) return;
        Vector3 to = _tgt - GlobalPosition; to.Y = 0f;
        if (to.LengthSquared() < 0.01f) return;
        float yaw = Mathf.Atan2(to.X, to.Z);
        _creature.Rotation = new Vector3(0, Mathf.LerpAngle(_creature.Rotation.Y, yaw, dt * 6f), 0);
    }

    // ===== PHALANX: the archers' AI =====
    private bool _fleeing = false;
    private void MoveArcher(Player p, float dt, float spdMul)
    {
        if (WardGuarded)
        {
            // IN RANKS: hold a slot inside the dome, on the far side of the bearer from you, so the only way through
            // to them is through the ward. They never attack on their own — the bearer calls the volley.
            var L = _leader;
            int idx = Mathf.Max(0, L._squad.IndexOf(this));
            Vector3 back = L.GlobalPosition - _tgt; back.Y = 0f;
            back = back.LengthSquared() > 0.01f ? back.Normalized() : Vector3.Back;
            float ang = (idx - (L._squad.Count - 1) * 0.5f) * 0.55f;   // fanned out in a shallow rank behind the bearer
            Vector3 slot = L.GlobalPosition + back.Rotated(Vector3.Up, ang) * (L.WardRadius * 0.62f);
            Vector3 to = slot - GlobalPosition; to.Y = 0f;
            if (to.Length() > 1.2f && spdMul > 0f)
                GlobalPosition = ClampArena(GlobalPosition + to.Normalized() * Speed * spdMul * dt);
            FaceTarget(dt);
            return;
        }
        // BROKEN: defenceless. It looks for another bearer to enlist under; failing that it cowers behind whatever
        // body it can find — the loot goblin's cover logic, but the cover is other enemies.
        _joinScanT -= dt;
        if (_joinScanT <= 0f)
        {
            _joinScanT = 0.7f;
            var host = FindRecruitingBearer();
            if (host != null)
            {
                _fleeTarget = host.GlobalPosition; _hasFlee = true;
                if (GlobalPosition.DistanceTo(host.GlobalPosition) < host.WardRadius * 0.9f && host.EnlistArcher(this)) { _fleeing = false; return; }
            }
            else { _fleeTarget = PickCoverBehindEnemies(_tgt, GlobalPosition); _hasFlee = true; }
        }
        if (_hasFlee && spdMul > 0f)
        {
            Vector3 to = _fleeTarget - GlobalPosition; to.Y = 0f;
            Vector3 away = GlobalPosition - _tgt; away.Y = 0f;
            Vector3 want = to.Length() > 1.5f ? to.Normalized() : (away.LengthSquared() > 0.01f ? away.Normalized() : Vector3.Forward);
            if (away.Length() < 14f && away.LengthSquared() > 0.01f) want = (want + away.Normalized() * 0.8f).Normalized();   // you're close → always retreat too
            GlobalPosition = ClampArena(GlobalPosition + AvoidBlockers(want) * Speed * spdMul * dt);
        }
    }

    // the nearest bearer whose ward still stands and whose rank isn't full
    private Enemy FindRecruitingBearer()
    {
        Enemy best = null; float bd = 220f * 220f;
        var list = Game.I.Enemies;
        for (int i = 0; i < list.Count; i++)
        {
            var e = list[i];
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e) || !e.IsPhalanx || e.Remote) continue;
            if (!e.WardUp || e._squad.Count >= MaxArchers) continue;
            float d = GlobalPosition.DistanceSquaredTo(e.GlobalPosition);
            if (d < bd) { bd = d; best = e; }
        }
        return best;
    }

    // a spot on the far side of another (living, non-archer) enemy from the player — a meat shield, not a tree
    private Vector3 PickCoverBehindEnemies(Vector3 player, Vector3 self)
    {
        Vector3 best = self + (self - player).Normalized() * 16f; best.Y = 0f;
        float bestScore = -1e9f;
        var list = Game.I.Enemies;
        for (int i = 0; i < list.Count; i++)
        {
            var e = list[i];
            if (e == null || e == this || e.Dead || !GodotObject.IsInstanceValid(e) || e.IsArcher) continue;
            Vector3 bp = new Vector3(e.GlobalPosition.X, 0f, e.GlobalPosition.Z);
            float dPlayer = new Vector2(bp.X - player.X, bp.Z - player.Z).Length();
            float dSelf = new Vector2(bp.X - self.X, bp.Z - self.Z).Length();
            if (dPlayer < 5f || dSelf > 70f) continue;
            float score = -dSelf * 0.5f - Mathf.Abs(dPlayer - 15f) + e.Radius * 2.5f;   // prefer close, mid-range, BIG bodies
            if (score > bestScore)
            {
                bestScore = score;
                Vector3 away = bp - player; away.Y = 0f;
                if (away.LengthSquared() < 0.01f) away = Vector3.Forward;
                best = bp + away.Normalized() * (e.Radius + 1.8f); best.Y = 0f;
            }
        }
        return best;
    }

    // Hurricane fling — launch this enemy with a 3D velocity. Heavier (bigger) enemies are scaled down so
    // they're harder to pick up; bosses can't be flung (they just shrug + get a small nudge). The throw is
    // host-authoritative; the airborne arc reaches clients through the (now Y-bearing) enemy snapshot, and
    // fall damage is applied on landing in EndThrow, scaling with impact speed (i.e. fall height/force). (NEW)
    // (NEW) foes a fling can't meaningfully launch — bosses (no-op) and heavy bodies that barely leave the ground. Callers
    // that want a guaranteed reaction (e.g. Arcane Eruption) knock these back horizontally instead.
    public bool Flingable => _behav != EBehav.Boss && Radius < 1.9f;
    public void Fling(Vector3 velocity, float fallMul = 1f)
    {
        if (Remote || Dead) return;
        if (IsArcher && WardGuarded) return;   // (NEW) warded archers are immune to being flung too — the ward keeps the formation intact until you break it
        if (IsTaker && _grabPeer != 0) ReleaseGrab();   // (NEW) a flung Taker drops whoever it's carrying
        if (_behav == EBehav.Boss) { Knockback(GlobalPosition - velocity, 1.5f); return; }
        _climbing = false;   // (NEW) flung off whatever it was scaling
        _fallDmgMul = fallMul;   // (NEW) Updraft Tempest amplifies the landing damage
        float mass = 0.85f + Radius * 0.4f;   // weight: heavies launch lower than light foes, but everyone gets real air now (was 0.6 + Radius, which barely budged big enemies) (NEW)
        _throwVel = velocity / mass;
        _knock = Vector3.Zero;             // a throw overrides any pending horizontal knockback
        _thrown = true; _thrownT = 0f;
        if (AuthBiped) _creature.BipedAirborne(_flungFromClimb);   // (NEW) real airborne fall clip (climb-slip lead-in if peeled off a wall)
        _flungFromClimb = false;
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
        if (AuthBiped) _creature.Animate(dt, 0f);   // (NEW) advance the airborne fall clip; NO ragdoll spin — the clip carries the motion
        else if (_creature != null) { _creature.RotateX(_tumbleX * dt); _creature.RotateZ(_tumbleZ * dt); }   // procedural foes: chaotic ragdoll tumble
    }

    private void EndThrow(float impactSpeed)
    {
        _thrown = false; _throwVel = Vector3.Zero; _thrownT = 0f;
        if (impactSpeed > ThrowHurtSpeed && !Dead)
            Hurt((impactSpeed - ThrowHurtSpeed) * ThrowDmgPer * _fallDmgMul, DamageType.Wind, false);   // harder/higher landing = more damage (× Tempest)
        _fallDmgMul = 1f;   // consumed — reset for the next throw
        // real landings crash them onto the ground and leave them scrambling back up — a punish window. Trivial
        // tosses (and bosses/dead) just stand straight back up. (NEW)
        float gud = 0f;
        if (!Dead && _behav != EBehav.Boss && impactSpeed > 4f)
        {
            gud = Mathf.Clamp(0.4f + impactSpeed * 0.045f, 0.5f, 1.1f);
            if (AuthBiped)   // (NEW) authored biped: a real ground-to-standing clip (random stand-up 2/4) drives the get-up — no pitch-topple
            {
                _getUpDur = _getUpT = 1.6f;   // hold the AI open long enough for the stand-up clip; UpdateGetUp ends early once it finishes
                _creature.Rotation = new Vector3(0, _creature.Rotation.Y, 0);
                _creature.BipedGetUp();
            }
            else   // procedural foe: slam into a toppled pose (on its back/side); UpdateGetUp rights it
            {
                _getUpDur = gud; _getUpT = gud;
                if (_creature != null) _creature.Rotation = new Vector3(1.45f, _creature.Rotation.Y, GD.Randf() * 0.9f - 0.45f);
            }
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
        if (AuthBiped) _creature.BipedAirborne(false);   // (NEW) proxy plays the airborne fall clip (host doesn't sync climb-origin → plain free-fall)
    }

    // CLIENT proxy: host says it landed — topple + rise (dur>0) or just stand back up (dur==0). (NEW)
    public void RemoteLand(float getUpDur)
    {
        if (!Remote) return;
        _rThrown = false;
        if (getUpDur > 0f)
        {
            if (AuthBiped) { _getUpDur = _getUpT = 1.6f; _creature.Rotation = new Vector3(0, _creature.Rotation.Y, 0); _creature.BipedGetUp(); }   // (NEW) real stand-up clip
            else { _getUpDur = getUpDur; _getUpT = getUpDur; if (_creature != null) _creature.Rotation = new Vector3(1.45f, _creature.Rotation.Y, GD.Randf() * 0.9f - 0.45f); }
            Game.I.Sfx?.Impact(DamageType.Physical);
        }
        else { _getUpT = 0f; if (_creature != null) { _creature.Rotation = new Vector3(0, _creature.Rotation.Y, 0); if (AuthBiped) _creature.BipedLoco(false); } }
    }

    // downed-and-rising: authored bipeds play the ground→standing CLIP (end as soon as it finishes); procedural foes lerp the
    // toppled creature back upright over the stagger window. AI stays suppressed either way. (NEW)
    private void UpdateGetUp(float dt)
    {
        _getUpT -= dt;
        if (AuthBiped)
        {
            _creature.Animate(dt, 0f);   // drive the stand-up clip
            if (_creature.BipedOneShotDone) _getUpT = 0f;   // clip finished → resume AI now (don't wait out the padding window)
            if (_getUpT <= 0f) { _getUpT = 0f; _creature.BipedLoco(false); }
            return;
        }
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
    private bool _bleedPersist = false;   // (BLOOD ROT mod) the DoT never times out — bleeds until the foe dies
    public void Bleed(float dps, float dur, bool rot = false, int owner = 0, float burstMul = 1f, bool persist = false)
    {
        if (Remote) { Game.I.NetMgr?.ReportStatus(NetId, 0, dps, dur, (rot ? 1f : 0f) + (persist ? 2f : 0f)); return; }   // pack persist into the flag slot for MP
        _bleedDps = Mathf.Max(_bleedDps, dps);
        _bleedT = Mathf.Max(_bleedT, dur);
        _bleedBurstMul = Mathf.Max(_bleedBurstMul, burstMul);   // (OVERHAUL) Rupture: keep the strongest burst multiplier
        if (rot) _bleedRot = true;
        if (persist) _bleedPersist = true;
        _bleedOwner = owner != 0 ? owner : (Game.I != null ? Game.I.LocalPeer : 1);   // (NEW) caster peer
    }
    public float SlowMul = 0.45f;
    public float RootT = 0f;
    public float MarkT = 0f;
    // (HAUNT STORM) a generic ELECTRIC stun — held in place AND locked out of every ability, but with none of the
    // frost machinery (no stacks, no ice block, no shatter). Kept separate from FrozenT so a Haunt bolt can't be
    // mistaken for a Frost-witch freeze by anything that reads Frozen (Deep Winter, shatter, Taker grab-drop).
    public float ShockT = 0f;
    public void Shock(float dur) { if (!Dead) ShockT = Mathf.Max(ShockT, dur); }
    // ===== FROST WITCH: freeze stacks → frozen (ice block) → shatter =====
    public float FreezeStacks = 0f;
    public float FrozenT = 0f;                       // >0 = encased in ice (a stun)
    // ---- Forsaken curse / tether ----
    public float CurseStacks = 0f;                   // (NEW) unique curse stacks the suck-beam built on THIS foe
    public float CurseT = 0f;                        // (NEW) remaining tether/curse time
    public int CurseGroup = 0;                       // (NEW) tether group id (0 = ungrouped)
    private bool _remoteCursed = false;              // (NEW) client-proxy cursed mirror
    public bool Cursed => Remote ? _remoteCursed : CurseT > 0f;
    public bool Burning => !Remote && _burnT > 0f;   // (NEW) Ember Cinder Skin reads this to heal her near burning foes
    private DamageType _curseBonusType = DamageType.Curse;
    private int _curseBonusType2 = -1;   // (NEW) optional 2nd bonus type from the Cursebrand legendary (-1 = none)
    private float _curseBonusMul = 1.35f, _curseShareFrac = 0.35f;
    private bool _curseShareGuard = false;           // guards the shared-damage broadcast against recursion
    // (NEW) per-frame ceilings on the two runaway fan-outs (curse-group share + Shatter Cascade chaining). A big shatter
    // into a large curse group is O(hits × groupSize) Hurt calls, each snapshotting the enemy list — that combinatorial
    // burst is what froze the game in MP. These caps bound the work per frame (self-reset via the process-frame counter).
    private static ulong _shareFrame; private static int _shareBudget; private static bool _shareWarned;
    private static ulong _cascFrame; private static int _cascBudget; private static bool _cascWarned;

    // ===== (DOOM) the Forsaken's mechanic. ONE accumulating bank of banked damage per foe with a fuse: every application
    // adds to that same bank and refreshes the fuse (there is never a second stack), the fuse detonates it in a single
    // burst, and the instant the bank covers the distance to this foe's next FLOOR it fires early instead of waiting.
    // "Floor" is 0 for anything ordinary — so an execute is simply a kill — and the Hollow Moon's next authored phase
    // gate for him, so a boss execute punches him to his next stage rather than skipping content you built.
    // GENERATION is what bounds the chain: a splash applies Doom one generation deeper, and a gen-DoomMaxGen bank still
    // detonates for damage but no longer splashes. That plus the per-frame budget is what keeps this off the MP-freeze
    // path the curse-group cascade found (see the _share/_casc budgets above — same pattern, deliberately).
    public const float DoomFuse = 5f;
    public const int DoomMaxGen = 2;
    public const float DoomSplashFrac = 0.25f;
    public const float DoomSplashRadius = 5f;
    public float DoomBank = 0f;
    public float DoomT = 0f;
    private int _doomGen = 0;
    private long _doomOwner = 0;                     // peer credited with the detonation's damage, kills and souls
    private bool _doomGuard = false;                 // a detonation's own damage must never re-enter the detonation
    private float _doomSpreadMul = 1f;               // (FRAY) how hard this bank's blast carries when it goes off
    private float _doomSpreadR = DoomSplashRadius;   // …and how far, from the caster's DoomSpreadRadius
    // (MP) StatusMask is out of bits — stacks hold 22-27, the tether group 28-30, arcane-marked 31 — so Doom rides its
    // own packed int in the enemy snapshot, alongside the burn-stack array that set the precedent. The host owns the
    // bank, the fuse and every detonation; a client only ever renders what it's told.
    private float _remoteDoomBank = 0f, _remoteDoomT = 0f;
    private bool _remoteDoomLethal = false, _remotePuppeted = false;
    public bool Doomed => Remote ? _remoteDoomBank > 0.01f : DoomBank > 0.01f;
    public float DoomShownBank => Remote ? _remoteDoomBank : DoomBank;
    public float DoomShownT => Remote ? _remoteDoomT : DoomT;
    public bool DoomShownLethal => Remote ? _remoteDoomLethal : DoomBank >= Hp - DoomFloorHp();
    public bool PuppetShown => Remote ? _remotePuppeted : Puppeted;
    public int PackDoom()
    {
        if (DoomBank <= 0.01f && !Puppeted) return 0;
        int bank = Mathf.Clamp(Mathf.RoundToInt(DoomBank), 0, 0xFFFF);
        int fuse = Mathf.Clamp(Mathf.RoundToInt(DoomT * 10f), 0, 127);
        int v = bank | (fuse << 16);
        if (Puppeted) v |= 1 << 23;
        if (DoomBank > 0.01f && DoomBank >= Hp - DoomFloorHp()) v |= 1 << 24;   // the host resolves the floor; the client just paints it red
        return v;
    }
    public void SetRemoteDoom(int v)
    {
        _remoteDoomBank = v & 0xFFFF;
        _remoteDoomT = ((v >> 16) & 0x7F) / 10f;
        _remotePuppeted = (v & (1 << 23)) != 0;
        _remoteDoomLethal = (v & (1 << 24)) != 0;
    }
    public float DoomFrac => MaxHp > 0f ? Mathf.Clamp(DoomBank / MaxHp, 0f, 1f) : 0f;
    private static ulong _doomFrame; private static int _doomBudget; private static bool _doomWarned;

    // ===== (PUPPET) she never moves a body — she makes a body move itself. A puppeted foe keeps its own AI, its own
    // walk cycle and its own attack; the ONLY thing that changes is what it's pointed at. That's why this needs no new
    // animation and no new AI: _tgt is a plain Vector3 and HitTarget routes damage by descriptor, exactly the way melee
    // foes already peel off onto Verdant's tree-ents. This adds the third routing branch that path implies. =====
    public Enemy PuppetTgt;
    public float PuppetT = 0f;
    private long _puppetOwner = 0;
    private float _puppetFeed = 0f;     // Doom each landed puppet blow banks on its victim (Danse Macabre)
    private bool _puppetFinale = false; // Leg Grand Finale: when this leash ends, set its own bank off
    private bool _tgtIsEnemy = false;   // this frame's target is another enemy → HitTarget must not touch a player
    public bool Puppeted => PuppetT > 0f && PuppetTgt != null && !PuppetTgt.Dead && GodotObject.IsInstanceValid(PuppetTgt);

    // (ROUT) blind panic. Implemented as a TARGET override rather than new movement: point the foe at a spot behind
    // itself and its own walk/pathing carries it away, so nothing here has to know how any of the 16 behaviours move.
    public float RoutT = 0f;
    private Vector3 _routDir = Vector3.Forward;

    // (DOOM WALKER) a foe killed by its own detonation doesn't fall — it gets up and carries what's left of the blast
    // into the nearest crowd before letting go. Still puppetry: the body walks on its own legs, nothing is thrown.
    // Capped hard, because a walker is the most expensive thing here — it moves, animates and paths while already dead.
    public const int DoomWalkerCap = 4;
    private static int _doomWalkersLive = 0;
    private bool _doomWalking = false;
    private float _doomWalkT = 0f;
    private float _doomWalkPayload = 0f;
    public void Flee(Vector3 from, float dur, bool total = false)
    {
        if (Remote) { Game.I.NetMgr?.ReportStatus(NetId, 12, dur, total ? 1f : 0f, 0f); return; }   // a client's Rout — the host owns the panic
        if (Dead || IsBoss) return;
        Vector3 away = GlobalPosition - from; away.Y = 0f;
        _routDir = away.LengthSquared() > 0.01f ? away.Normalized() : Vector3.Forward;
        RoutT = Mathf.Max(RoutT, dur);
        if (total) { _alerted = false; _heard = 0f; }   // Leg Scattered: they lose you completely, not just their nerve
    }
    public float FreezeThreshold => (IsBoss
        ? Mathf.Clamp(1f + MaxHp / 300f, 1f, 18f)     // (NEW) bosses/minibosses: gentler HP→stacks so a tanky target freezes in a few seconds, not ~8
        : Mathf.Clamp(1f + MaxHp / 120f, 1f, 240f)) * _freezeThreshMul;   // (BUFF) dropped the flat ×1.25 tax → every freeze builds ~20% faster; Brittle (best-of) still lowers it
    private float _freezeExpT = 0f;                  // stacks all expire together 2s after the last one
    private bool _radiatesCold = false;              // (NEW) only beam/shatter freezes radiate Deep Winter; ambient-frozen foes don't (no chain)
    private float _freezeThreshMul = 1f, _freezeDurBonus = 0f;   // (NEW) best-of frost profile accumulated from contributing witches
    private float _deepWinterT = 0f;                 // (NEW) Deep Winter spread throttle
    private float _frozenBlue = 0f, _frozenBlueMax = 0f, _frozenBlueDmg = 0f;   // temp blue bar while frozen
    private float _frozenDur = 1f;              // (NEW) the freeze's full duration, so the ice block can melt (shrink+fade) across its life
    private MeshInstance3D _iceBlock;
    private float _remoteBlueFrac = 1f;
    public float FrozenBlueFrac => Remote ? _remoteBlueFrac : (_frozenBlueMax > 0f ? Mathf.Clamp(_frozenBlue / _frozenBlueMax, 0f, 1f) : 0f);
    public bool Frozen => FrozenT > 0f;
    private void EnsureIceBlock(bool show)
    {
        if (show)
        {
            if (_iceBlock == null)
            {
                _iceBlock = new MeshInstance3D { Mesh = new BoxMesh { Size = Vector3.One * Radius * 2.6f } };
                var m = Game.ToonEmissive(new Color(0.6f, 0.85f, 1f), 1.4f, 0f);
                m.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; m.AlbedoColor = new Color(0.7f, 0.9f, 1f, 0.42f); m.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
                _iceBlock.MaterialOverride = m;
                AddChild(_iceBlock);
            }
            else   // reuse a block from a previous freeze: restore it to full size + opacity (it may have been shrunk mid-melt)
            {
                _iceBlock.Scale = Vector3.One;
                if (_iceBlock.MaterialOverride is BaseMaterial3D im) { var ac = im.AlbedoColor; ac.A = 0.42f; im.AlbedoColor = ac; }
            }
            _iceBlock.Visible = true;
        }
        else if (_iceBlock != null) { _iceBlock.QueueFree(); _iceBlock = null; }   // (FIX) actually remove it — a hidden-but-kept block was the stale-ice bug
    }
    public float MarkAmp = 1f;
    public int MarkJumps = 0;
    private float _markDoom = 0f;   // (OVERHAUL) Hex Mark Doombrand: on-death curse detonation
    // ---- Arcane witch: Arcane Mark — a PERSISTENT paint (max 4 tracked on the caster, FIFO, cleared on death/eviction) that
    // her charged chain-lightning bounces through. No timer here; the caster's Player owns the mark set & calls SetArcaneMark. ----
    private bool _arcaneMarked = false;              // host truth (Arcane witch's own primary/charge marks, managed by her Player)
    private bool _markShow = false;                  // client-proxy mirror
    public float ConduitT = 0f;                      // (NEW) SELF-EXPIRING conduit state — lets ANY witch's swappable mod/finisher brand a conduit
    // a foe is "conduit-marked" if the Arcane witch painted it OR a swappable conduit-producer branded it (self-timed, cross-witch)
    public bool ArcaneMarked => Remote ? _markShow : (_arcaneMarked || ConduitT > 0f);   // HUD pip + chain/torrent targeting
    public void SetArcaneMark(bool on) { if (Remote) { Game.I.NetMgr?.ReportStatus(NetId, 8, on ? 1f : 0f, 0f, 0f); return; } _arcaneMarked = on; }
    public void MarkConduit(float dur) { if (Remote) { Game.I.NetMgr?.ReportStatus(NetId, 9, dur, 0f, 0f); return; } ConduitT = Mathf.Max(ConduitT, dur); }   // (NEW) any conduit producer

    private string _type = "shade";
    private EBehav _behav = EBehav.Melee;
    private float _touchCd = 0f;
    private float Pace => Game.I?.AtkPace ?? 1f;   // (NEW) global attack-cadence multiplier (slower early, ramps up with tier)
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
    private bool _losCache = false; private float _losCacheT = 0f;   // (PERF) cached line-of-sight to the target — SightBlocked is expensive in the maze; recompute ~5Hz staggered, not every frame
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
    // (NEW) special enemies: director-spawned, hard-capped, cooldown-gated, and excluded from the normal concurrent-horde
    // cap so they're always an EVENT rather than part of the stream. The phalanx BEARER is the special; its archers are
    // ordinary bodies (counting them here would blow the cap instantly).
    public bool IsSpecial => _type == "taker" || _type == "phalanx";
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
    // ANY damage (beams, ground fields, DoTs — which never call HitFrom) rouses an idle zombie: it investigates
    // toward the nearest warden (the source), shambling over to find them instead of standing there getting chipped.
    private void DamageInvestigate()
    {
        if (_type != "swarmer" || Remote || _alerted || Game.I == null) return;
        var wp = Game.I.NearestWardenPos(GlobalPosition);
        _heard = Mathf.Max(_heard, 8f); _soundPos = wp;
        var d = wp - GlobalPosition; d.Y = 0f;
        if (d.LengthSquared() > 0.01f) _faceYaw = Mathf.Atan2(d.X, d.Z);
    }
    private float _boltSpeed = 16f, _boltDmg = 8f, _boltRadius = 0.5f;
    private float _chargeDur = 0f, _chargeT = 0f;       // sieger telegraph
    private float _healEvery = 1.4f, _healCd = 0f, _healAmt = 6f;
    private float _strafe = 1f, _strafeT = 0f;

    private Creature _creature;
    private float _catchMul = 1f;   // distant enemies speed up to re-engage
    private StandardMaterial3D _mat;
    private float _flash = 0f, _baseEnergy;
    private Color _lastEmit = Colors.Black; private float _lastEmitEn = -999f; private bool _emitDirty = true;   // (PERF) skip redundant material emission writes
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
            case "swarmer": MaxHp = 24 * hs; Speed = 6.8f; Dmg = 5f; Score = 10; Radius = 0.95f; Col = new Color(0.42f, 0.5f, 0.32f); _behav = EBehav.Melee; _faceYaw = GD.Randf() * Mathf.Tau; break;   // (FIX) was 5*ds → double-scaled by the ds pass at line ~1038; plain base now scales once like every other mob
            case "taker": MaxHp = 260 * hs; Speed = 2.6f; Dmg = 6f; Score = 90; Radius = 1.9f; Col = new Color(0.30f, 0.34f, 0.26f); _behav = EBehav.Melee; break;   // (FIX) was 6*ds → same double-scale; scales once now
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
            // ---- Rainforest jungle enemies (NEW) ----
            case "jtroll": MaxHp = 90 * hs; Speed = 5.6f; Dmg = 26; Score = 42; Radius = 2.2f; Col = new Color(0.28f, 0.42f, 0.24f); _behav = EBehav.Melee; break;            // rushing bruiser — staggers you on hit
            case "pigmy":  MaxHp = 12 * hs; Speed = 8.5f; Dmg = 8;  Score = 8;  Radius = 0.8f; Col = new Color(0.75f, 0.6f, 0.35f); _behav = EBehav.Melee; _faceYaw = GD.Randf() * Mathf.Tau; break;  // fast fodder spear
            case "pigmydart": MaxHp = 11 * hs; Speed = 6.5f; Dmg = 0; Score = 12; Radius = 0.85f; Col = new Color(0.7f, 0.55f, 0.3f); _behav = EBehav.Ranged; _range = 24; _preferDist = 15; _fireEvery = 1.7f; _boltSpeed = 20; _boltDmg = 7; _boltRadius = 0.35f; break;  // fast fodder blowdart
            case "ptero":  MaxHp = 18 * hs; Speed = 6.0f; Dmg = 0;  Score = 30; Radius = 1.0f; Col = new Color(0.55f, 0.75f, 0.85f); _behav = EBehav.Zapper; _range = 32; _preferDist = 20; _fireEvery = 4.0f + GD.Randf() * 1.0f; _flyY = 6.5f; break;  // flying electric stunner — 4-5s between stun casts (was 3s) so you can't get chain-stunned
            case "bat":    MaxHp = 12 * hs; Speed = 8.0f; Dmg = 15; Score = 24; Radius = 0.7f; Col = new Color(0.3f, 0.24f, 0.3f); _behav = EBehav.Diver; _range = 22; _preferDist = 11; _flyY = 6.0f; _diveCd = 2.0f; break;  // diver
            case "croc":   MaxHp = 55 * hs; Speed = 3.0f; Dmg = 12; Score = 44; Radius = 1.6f; Col = new Color(0.4f, 0.55f, 0.3f); _behav = EBehav.Lobber; _range = 30; _preferDist = 20; _fireEvery = 3.4f; _boltDmg = 26; _boltRadius = 4.5f; break;  // lobs timed bombs
            case "snake":  MaxHp = 1;       Speed = 9.6f; Dmg = 6;  Score = 6;  Radius = 0.7f; Col = new Color(0.5f, 0.8f, 0.35f); _behav = EBehav.Melee; break;             // 1-hit glass cannon — roots you on touch
            case "goblin": MaxHp = 95 * bhs; Speed = 11.5f; Dmg = 0; Score = 0;  Radius = 1.0f; Col = new Color(1f, 0.84f, 0.3f); _behav = EBehav.Goblin; IsGoblin = true; Label = "LOOT GOBLIN"; break;
            // ---- WARDED PHALANX (a compound miniboss: one ward-bearer + up to 8 archers) ----
            case "phalanx": MaxHp = 320 * bhs; Speed = 2.4f; Dmg = 30; Score = 180; Radius = 2.8f; Col = new Color(0.55f, 0.42f, 0.95f); _behav = EBehav.Phalanx; Label = "WARD BEARER";
                           _wardBase = 900f * bhs; break;   // the ward, not the HP, is the real health bar
            case "archer":  MaxHp = 34 * hs;  Speed = 5.2f; Dmg = 6;  Score = 45; Radius = 1.05f; Col = new Color(0.70f, 0.58f, 1.0f); _behav = EBehav.Archer; Label = "PHALANX ARCHER"; break;
            case "miniboss": MaxHp = 680 * bhs; Speed = 3.0f; Dmg = 28; Score = 220; Radius = 3.0f; Col = new Color(0.62f, 0.30f, 0.85f); _behav = EBehav.Boss; IsBoss = true; Label = "MINI-BOSS";
                           _range = 30; _fireEvery = 2.4f; _boltSpeed = 15; _boltDmg = 16; _boltRadius = 0.7f; break;
            case "boss":   MaxHp = 4200 * bhs; Speed = 5.2f; Dmg = 40; Score = 800; Radius = 4.0f; Col = new Color(0.85f, 0.25f, 0.45f); _behav = EBehav.Boss; IsBoss = true; Label = "THE HOLLOW MOON";   // (REWORK) 2.6 -> 5.2: he read as a slow siege piece; now he closes
                           _range = 36; _fireEvery = 2.0f; _boltSpeed = 16; _boltDmg = 22; _boltRadius = 0.9f; break;
            default:       MaxHp = 14 * hs; Speed = 4.0f; Dmg = 10; Score = 10; Radius = 1.3f; Col = new Color(0.54f, 0.47f, 0.84f); _behav = EBehav.Melee; break;
        }
        Dmg *= ds; _boltDmg *= ds;   // contact + projectile damage scale with depth (host-authoritative, so MP-consistent)
        // (NEW) POST-WAVE-10 HARD RAMP — from wave 11 on, difficulty climbs STEEPLY: HP, damage, and move speed all compound.
        // Applies to ALL foes AND bosses/minibosses. This is what gives levels 2+ their teeth, and it carries across levels
        // because Wave keeps climbing through portals (the previous level's ending difficulty = the new level's start).
        if (wave > 10)
        {
            float hardHp = Mathf.Pow(1.062f, wave - 10);   // ~1.8× @ w20, 3.3× @ w30, 6× @ w40 (on top of the base depth scale)
            float hardDmg = Mathf.Pow(1.05f, wave - 10);   // damage ramps a touch gentler so it stays survivable
            MaxHp *= hardHp; Dmg *= hardDmg; _boltDmg *= hardDmg;
            Speed *= 1f + Mathf.Min(0.5f, (wave - 10) * 0.02f);   // up to +50% move speed
        }
        // (NEW) named wave mutators: Blood Moon / Surge foes move faster (bosses excepted, they have their own pacing)
        if (!IsBoss && Game.I != null)
        {
            if (Game.I.ActiveMutator == WaveMutator.BloodMoon) Speed *= 1.2f;   // (NERF 1.3→1.2) still "fast", but fewer hits land — Blood Moon foes were hitting too hard (director already ups their damage at that Heat)
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
        if (_type == "snake") MaxHp = 1f;   // (NEW) the snake ALWAYS dies in one hit, at any depth
        // (NEW) model + HITBOX size scale gently with power for visual variety — tougher (deeper-wave) trash looks bigger, plus a
        // small per-enemy jitter so a pack isn't uniform. Capped. Bosses/goblins keep their set size. Stored as SizeMul and APPLIED
        // to Radius in _Ready (before the mesh builds) — and SYNCED to clients so co-op renders identical sizes + hitboxes.
        if (!IsBoss && !IsGoblin)
            SizeMul = Mathf.Clamp(1f + (hs - 1f) * 0.16f, 1f, 1.28f) * (0.9f + GD.Randf() * 0.2f);
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

    // (BOSS-LAIR) weaken a summoned world boss — used by the future 3 hidden "nerfer" pickups. Scales HP + damage.
    public void ScaleBossPower(float mul)
    {
        if (mul >= 0.999f) return;
        MaxHp *= mul; Hp = MaxHp; Dmg *= mul; _boltDmg *= mul;
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
                if (Game.I.SightBlocked(GlobalPosition + Vector3.Up, next + Vector3.Up) || Game.I.BlockerAt(next, Radius * 0.6f))   // wall or tree/pillar → stunned
                {
                    _takerState = 2; _takerT = AuthBiped ? 3.0f : 2f; _chargeCd = 7f;   // authored: longer window so falling_down → stand-up-4 both fit
                    if (AuthBiped) { _creature.BipedWallSlam(); _wallSlamPhase = 0; }   // slam to the dirt, then get up on stand-up 4
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
        if (_creature != null)
        {
            if (AuthBiped) DriveTakerAnim(dt, moveAmt);
            else { _creature.IdlePose = _takerState == 2 ? 1 : 0; _creature.Animate(dt, _takerState == 2 ? 0f : moveAmt); }
        }
    }

    // (HARNESS) force a specific biped clip/state for deterministic capture, bypassing the AI. null = normal AI.
    private string _dbgBiped = null;
    public void DebugBiped(string canon) { _dbgBiped = canon; }
    public int DebugClipCount => _creature != null ? _creature.BipedClipCount : -1;   // (HARNESS) how many action clips this biped resolved (10 = full set)
    public void DebugWince(int variant) { _dbgBiped = "walk"; _creature?.Wince(variant); }   // (HARNESS) force a hurt flinch of the given variant
    // (HARNESS) THE HOLLOW MOON: drive one specific attack through the REAL wind-up→telegraph→fire path, bypassing only the
    // cooldown/range gates. Everything else (clip, gesture, hand glow, lanes, shout, the actual damage) runs as it would in a fight.
    public void DebugBossPattern(int pat, float dur = 1.2f)
    {
        var to = _tgt - GlobalPosition; to.Y = 0f;
        if (to.LengthSquared() < 0.01f) { to = -GlobalTransform.Basis.Z; to.Y = 0f; }
        var flat = to.LengthSquared() > 0.01f ? to.Normalized() : Vector3.Forward;
        _bossAoeReach = pat == 8 ? DashDist : (pat == 5 || pat == 7 ? 0f : 14f);
        if (pat == 8) _dashDir = flat;
        BeginBossCharge(pat, dur, flat, flat, Mathf.Max(_bossAoeReach, 12f), false);
    }
    public bool DebugBossWinding => _bossCharging;
    public string DebugBossClipState => _creature == null ? "no creature"
        : $"{_creature.DebugPlayingClip}@{_creature.DebugPlaySpeed:0.00}x playing={_creature.DebugApPlaying}";
    public bool DebugCasting => _creature != null && _creature.Casting;      // (HARNESS) a one-shot cast clip owns the body right now
    public float DebugFootGap => _creature != null ? _creature.DebugFootGap / Mathf.Max(0.01f, Radius) : 0f;   // foot offset in RADII, so it compares across body sizes
    public string DebugCastState => $"hold={_castHoldT:0.00} wind={_castWindT:0.00} pend={_castPend} healCd={_healCd:0.00}";
    public bool DebugHasClip(string canon) => _creature != null && _creature.HasBipedClip(canon);
    public bool DebugBossDashing => _dashT > 0f;
    public float DebugDashPushed => _dashMoved;   // distance the dash itself applied (net displacement can be less if terrain blocked him)
    public int DebugTripleLeft => _tripleLeft;    // charges remaining in the current phase-2 three-charge set
    public int DebugP2Stage => _p2Stage;          // 0 none, 1 prone, 2 rising, 3 laughing advance
    public bool DebugVortexUp => _vortex != null && GodotObject.IsInstanceValid(_vortex);
    public bool DebugSpinPending => _spinPending;
    public float DebugBossChargeFrac => ChargeFrac;
    public bool DebugThrown => _thrown;              // (HARNESS) mid-fling arc?
    public bool DebugGettingUp => _getUpT > 0f;      // (HARNESS) in the downed→rising window?
    public void DebugFling(Vector3 vel) { _dbgBiped = null; Fling(vel); }   // (HARNESS) REAL fling through the full arc/land/get-up path
    public void DebugClimbPeel(Vector3 push)         // (HARNESS) REAL crit/knock-off-a-wall: mark climbing, then peel → Fling(fromClimb) → climb-slip fall
    {
        _dbgBiped = null; _thrown = false;
        _climbing = true; _climbDir = Vector3.Forward;
        PeelOffWall(push);
    }
    private void DebugBipedTick(float dt)
    {
        float move = 0f;
        switch (_dbgBiped)
        {
            case "walk": _creature.BipedReach(0f); _creature.BipedLoco(false); move = 0.5f; break;
            case "run": _creature.BipedReach(0f); _creature.BipedLoco(true); break;
            case "reach": _creature.BipedReach(1f); _creature.BipedLoco(false); move = 0.4f; break;   // grab-arms telegraph while walking
            case "climb": _creature.BipedReach(0f); _creature.BipedClimb(); break;
            // "fall"/"walldown"/"standup": the state was TRIGGERED once by DebugBipedStart — just advance + hold here
        }
        _creature.Animate(dt, move);
    }
    // one-shot / transient states must be TRIGGERED once (not re-set every frame). Call this at the checkpoint, then hold with DebugBiped.
    public void DebugBipedStart(string canon)
    {
        _dbgBiped = canon;
        if (!AuthBiped) return;
        switch (canon)
        {
            case "fall": _creature.BipedReach(0f); _creature.BipedAirborne(false); break;
            case "climbfall": _dbgBiped = "fall"; _creature.BipedReach(0f); _creature.BipedAirborne(true); break;
            case "walldown": _creature.BipedReach(0f); _creature.BipedWallSlam(); break;
            case "standup": _creature.BipedReach(0f); _creature.BipedGetUp(4); break;
        }
    }
    private int _wallSlamPhase = 0;   // taker wall-stun: 0 = slammed down (falling_down), 1 = getting up (stand-up 4)
    // Authored taker: raise the grab-arms while winding up / dashing / carrying, RUN during the dash, and let the wall-slam play
    // its falling_down → stand-up-4 recovery. The melee punch (state 0) is the ordinary one-arm slash fired by MeleeAttack→Strike.
    private void DriveTakerAnim(float dt, float moveAmt)
    {
        _creature.BipedReach(_takerState == 4 || _takerState == 1 || _takerState == 3 ? 1f : 0f);   // arms forward: wind-up, dash, carry; down otherwise
        if (_climbing && _takerState == 0) _creature.BipedClimb();
        else switch (_takerState)
        {
            case 1: _creature.BipedLoco(true); break;    // dash → run clip
            case 2:                                       // wall-stun: falling_down (phase 0) → stand-up-4 (phase 1)
                if (_wallSlamPhase == 0 && _creature.BipedOneShotDone) { _creature.BipedGetUp(4); _wallSlamPhase = 1; }
                break;
            default: _creature.BipedLoco(false); break;   // approach / wind-up / carry → walk (arms up via reach when applicable)
        }
        _creature.Animate(dt, moveAmt);
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
        AddToGroup(Grove.Dev.Ai.AiObservable.Group);   // DEV harness observability (inert unless a scenario is running)
        if (!IsBoss && !IsGoblin && SizeMul != 1f) Radius *= SizeMul;   // (NEW) apply the (synced) power/variety size BEFORE the mesh + hitbox are built off Radius
        CreatureKind kind;
        if (_type == "boss") kind = CreatureKind.HollowBoss;   // THE HOLLOW MOON — bespoke half-orc/half-zombie w/ a hollow midsection
        else if (IsBoss || _type == "miniboss" || _type == "brute" || _type == "sieger") kind = CreatureKind.Orc;
        else if (_type == "sentinel" || _type == "phalanx") kind = CreatureKind.Orc;   // (NEW) the ward-bearer is a heavy
        else if (_type == "archer") kind = CreatureKind.Goblin;                        // (NEW) wiry archers behind the line
        else if (_type == "flyer" || _type == "diver") kind = CreatureKind.Mosquito;
        else if (_type == "bomber") kind = CreatureKind.Bomber;
        // (NEW) THE WITHERED KING body carries the grove's whole spellcaster family: the arcane caster, the stunner, the
        // healer, the empowering totem, the hexer and the dispeller.
        else if (_type == "caster" || _type == "zapper" || _type == "healer" || _type == "totem"
              || _type == "hexer" || _type == "wardbane") kind = CreatureKind.Withered;
        else if (_type == "swarmer") kind = CreatureKind.Zombie;   // (NEW) shambling zombie
        else if (_type == "taker") kind = CreatureKind.Taker;      // (NEW) big kidnapper — authored GLB with the full action set (run/fall/climb/stand-up)
        else if (_type == "jtroll") kind = CreatureKind.Troll;          // (NEW jungle) hulking troll bruiser
        else if (_type == "ptero") kind = CreatureKind.Pterodactyl;     // (NEW jungle) flying stunner
        else if (_type == "bat") kind = CreatureKind.Bat;               // (NEW jungle) diver
        else if (_type == "croc") kind = CreatureKind.Crocodile;        // (NEW jungle) crocodile-humanoid bomber
        else if (_type == "snake") kind = CreatureKind.Snake;           // (NEW jungle) slithering serpent
        else if (_type == "pigmy" || _type == "pigmydart") kind = CreatureKind.Pigmy;   // (NEW jungle) pigmy fodder
        else kind = CreatureKind.Goblin;   // shade / wisp / goblin-loot

        // two-tone palettes: orcs green->brown, goblins green->yellow, the rest neon for the synth look
        Color bodyC, limbC, accentC;
        if (IsBoss) { bodyC = Col; limbC = Col.Darkened(0.4f); accentC = Col.Lerp(Colors.White, 0.4f); }
        else if (_type == "swarmer") { bodyC = new Color(0.40f, 0.47f, 0.30f); limbC = new Color(0.26f, 0.30f, 0.20f); accentC = new Color(0.60f, 0.66f, 0.40f); }   // sickly zombie flesh (NEW)
        else if (_type == "phalanx") { bodyC = new Color(0.32f, 0.25f, 0.52f); limbC = new Color(0.19f, 0.15f, 0.33f); accentC = new Color(0.80f, 0.64f, 1f); }      // (NEW) warded bearer: deep violet plate, glowing sigils
        else if (_type == "archer") { bodyC = new Color(0.45f, 0.36f, 0.68f); limbC = new Color(0.26f, 0.20f, 0.42f); accentC = new Color(0.88f, 0.75f, 1f); }       // (NEW) its archers, in the bearer's colours
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
            case CreatureKind.Crocodile:
                bodyC = new Color(0.24f, 0.42f, 0.22f); limbC = new Color(0.17f, 0.30f, 0.15f); accentC = new Color(0.90f, 0.92f, 0.72f); break;   // scaly green, cream teeth/belly
            case CreatureKind.Troll:
                bodyC = new Color(0.26f, 0.40f, 0.24f); limbC = new Color(0.30f, 0.34f, 0.22f); accentC = new Color(0.88f, 0.86f, 0.62f); break;   // mossy troll, bone tusks
            case CreatureKind.Pigmy:
                bodyC = new Color(0.62f, 0.44f, 0.28f); limbC = new Color(0.45f, 0.30f, 0.18f); accentC = new Color(0.95f, 0.5f, 0.3f); break;      // tan skin, warpaint/feather
            case CreatureKind.Pterodactyl:
                bodyC = new Color(0.5f, 0.62f, 0.72f); limbC = new Color(0.3f, 0.4f, 0.5f); accentC = new Color(0.95f, 0.75f, 0.35f); break;        // leathery slate, amber crest
            case CreatureKind.Bat:
                bodyC = new Color(0.26f, 0.20f, 0.28f); limbC = new Color(0.15f, 0.11f, 0.17f); accentC = new Color(0.95f, 0.32f, 0.35f); break;    // dark, red eyes/fangs
            case CreatureKind.Snake:
                bodyC = new Color(0.28f, 0.62f, 0.26f); limbC = new Color(0.18f, 0.42f, 0.18f); accentC = new Color(0.95f, 0.85f, 0.2f); break;     // green scales, yellow eyes/tongue
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
        if (_type == "archer" && _creature != null) BuildBow();   // (NEW) every phalanx archer carries a nocked bow
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

    // perf: the light budget (Game.CullEnemyLights) keeps only the nearest N enemy lights lit in a big fight —
    // distant foes still glow from their emissive material, they just stop casting a real-time OmniLight.
    public void SetLightOn(bool on) { if (_light != null && GodotObject.IsInstanceValid(_light) && _light.Visible != on) _light.Visible = on; }

    public override void _Process(double delta)
    {
        if (Dead) return;
        if (Game.I == null || !Game.I.WorldRunning) return;
        if (!Game.I.SimActive) return;   // freeze AI/animation/firing/DoTs while a menu or card screen is open (NEW)
        float dt = (float)delta;
        if (_hitSndT > 0f) _hitSndT -= dt;   // (NEW) damage-tick throttle (ticks for remote proxies too — this runs before the Remote return below)
        if (_creature != null)   // (PERF) distance/visibility-driven LOD + animation cull, computed once per frame
        {
            var acam = Game.I.Player?.Cam;
            if (acam != null)
            {
                float camD2 = GlobalPosition.DistanceSquaredTo(acam.GlobalPosition);
                bool near = camD2 < 30f * 30f;
                // freeze skeletal animation for foes that are both far AND off-camera — their pose can't be seen
                _creature.AnimSuspended = !near && !acam.IsPositionInFrustum(GlobalPosition + Vector3.Up * Radius);
                // (PERF) count-adaptive LOD: the bigger the swarm, the CLOSER we drop detail + shadow-casting. At 180+ enemies a
                // tight swarm sits mostly within 18m, so a fixed 18m barely helped — pull it to ~8m so almost the whole horde
                // sheds its trim meshes + shadow-cascade passes (draws were the wall). Small fights keep full detail at 18m.
                int ec = Game.I.Enemies.Count;
                float lodD = ec > 120 ? 8f : ec > 60 ? 12f : 18f;
                _creature.SetLodFar(camD2 > lodD * lodD);
            }
            else { _creature.AnimSuspended = false; _creature.SetLodFar(false); }
        }
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

        // (CHANGE) bleeding now flicks little crimson SLASH marks (not the old rot bubbles). `_bleedT` is mirrored on clients
        // via status bit 1, so this reads on every machine. Blood Rot stays distinct via its pulsing crimson body glow (see tint).
        _rotBubT -= dt;
        if (_bleedT > 0f && _rotBubT <= 0f) { _rotBubT = 0.3f; SpawnBleedSlash(); }

        if (_behav == EBehav.Healer) UpdateHealerTether();   // (NEW) a green beam to whoever it's mending — runs on every machine so you can SEE who to kill first

        if (Remote)
        {
            if (_bossCharging) { _bossChargeT -= dt; if (_bossChargeT <= 0f) { _bossCharging = false; FireBossAnim(_bossPatPending); } }   // run the attack-timer bar on the client proxy (NEW)
            UpdateBossAnim(dt);   // (HOLLOW MOON) same gesture/glow telegraph on the proxy — clients must read the wind-up too
            UpdateCastAnim(dt);   // (NEW) …and run out the cast clip the host told us about, so the proxy returns to its walk
            UpdateRemoteSwing(dt);   // (MP FIX) …and the melee wind-up + strike, so the little ones visibly ATTACK on a client
            if (IsBoss) _bossHeat = Mathf.MoveToward(_bossHeat, Mathf.Clamp(0.12f + 0.66f * (1f - Hp / MaxHp), 0f, 1f), dt * 0.5f);   // (NEW) HP-based heat estimate for the HUD
            // client-side ghost: follow the host's reported position; animate from that motion
            var prev = GlobalPosition;
            // (NEW REFLOW) a big jump is a host-side RELOCATION, not lag — snap and poof instead of sliding the proxy
            // across half the map at lerp speed (which is what the smoothing would otherwise do).
            if (GlobalPosition.DistanceSquaredTo(_remoteTarget) > 400f)
            { GlobalPosition = _remoteTarget; Game.I.SpawnPoof(GlobalPosition); }
            else GlobalPosition = GlobalPosition.Lerp(_remoteTarget, Mathf.Clamp(dt * 16f, 0f, 1f));
            var mv = GlobalPosition - prev; mv.Y = 0;
            float moved = mv.Length();
            if (moved > 0.001f) Game.I.MaybeWaterTrail(GlobalPosition, GlobalPosition.Y - Radius, dt);   // proxy enemies ripple water on clients too (NEW)
            if (_creature != null)
            {
                if (_rThrown)   // networked ragdoll tumble while airborne — position follows the host arc above (NEW)
                {
                    if (AuthBiped) _creature.Animate(dt, 0f);   // (NEW) airborne fall CLIP instead of the ragdoll spin
                    else { _creature.RotateX(_tumbleX * dt); _creature.RotateZ(_tumbleZ * dt); }
                }
                else if (_getUpT > 0f)   // networked topple → rise after landing (NEW)
                {
                    _getUpT -= dt;
                    if (AuthBiped)   // (NEW) stand-up CLIP; end as soon as it finishes
                    {
                        _creature.Animate(dt, 0f);
                        if (_creature.BipedOneShotDone) _getUpT = 0f;
                        if (_getUpT <= 0f) _creature.BipedLoco(false);
                    }
                    else
                    {
                        float px = Mathf.LerpAngle(_creature.Rotation.X, 0f, dt * 9f);
                        float pz = Mathf.LerpAngle(_creature.Rotation.Z, 0f, dt * 9f);
                        _creature.Rotation = new Vector3(px, _creature.Rotation.Y, pz);
                        if (_getUpT <= 0f) { _getUpT = 0f; _creature.Rotation = new Vector3(0, _creature.Rotation.Y, 0); }
                    }
                }
                else
                {
                    if (moved > 0.001f)
                    {
                        float yaw = Mathf.Atan2(mv.X, mv.Z);
                        _creature.Rotation = new Vector3(0, Mathf.LerpAngle(_creature.Rotation.Y, yaw, dt * 8f), 0);
                    }
                    if (AuthBiped) _creature.BipedLoco(false);   // (NEW) proxies just walk/shamble (host drives the special states via throw/land events)
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
            if (_deflectT > 0f) _deflectT -= dt;
            if (IsPhalanx) UpdateWardVisual(dt);   // (NEW) the ward dome renders on clients too, driven by the synced fraction
            if (IsArcher) UpdateBowPose(dt);       // (NEW) …and so does the bow (aim/loose come over the wire, below)
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
        if (ShockT > 0f) ShockT -= dt;   // (HAUNT STORM) electric stun just runs out — no thaw/shatter tail
        if (FrozenT > 0f)   // (NEW) frozen countdown → the block melts across its life, then a light crack on expiry
        {
            FrozenT -= dt;
            if (_iceBlock != null)
            {
                _iceBlock.RotationDegrees = new Vector3(0, _iceBlock.RotationDegrees.Y + dt * 20f, 0);
                float melt = 1f - Mathf.Clamp(FrozenT / Mathf.Max(0.001f, _frozenDur), 0f, 1f);   // 0 fresh → 1 fully thawed
                _iceBlock.Scale = Vector3.One * (1f - melt * 0.5f);                                 // shrinks toward half as it thaws
                if (_iceBlock.MaterialOverride is BaseMaterial3D im) { var ac = im.AlbedoColor; ac.A = 0.42f * (1f - melt * 0.7f); im.AlbedoColor = ac; }   // and fades
            }
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
            if (FrozenT <= 0f) MeltFreeze();   // (FIX) timer ran out → thaw the ice with a light crack (no damage). Was ShatterFreeze(), which early-returned because FrozenT was already 0, leaving the ice block stuck on the enemy.
        }
        else if (FreezeStacks > 0f)   // (NEW) stacks all expire together 2s after the last one
        {
            _freezeExpT -= dt;
            if (_freezeExpT <= 0f) { FreezeStacks = 0f; _freezeThreshMul = 1f; _freezeDurBonus = 0f; }
        }
        if (MarkT > 0) MarkT -= dt; else MarkAmp = 1f;
        if (ConduitT > 0f) ConduitT -= dt;   // (NEW) conduit brand self-expires (so cross-witch producers don't need the Arcane-only mark manager)
        if (CurseT > 0f) { CurseT -= dt; if (CurseT <= 0f) { CurseGroup = 0; CurseStacks = 0f; } }   // (NEW) curse fades → drop the tether + stacks
        if (DoomT > 0f) { DoomT -= dt; if (DoomT <= 0f) DetonateDoom(); }   // (DOOM) the fuse. Every application refreshes it, so a foe she's actively feeding never goes off on its own
        if (PuppetT > 0f)   // (PUPPET) the leash — it also drops the moment its victim dies, so it never swings at a corpse
        {
            PuppetT -= dt;
            if (PuppetT <= 0f || PuppetTgt == null || PuppetTgt.Dead || !GodotObject.IsInstanceValid(PuppetTgt))
            {
                PuppetT = 0f; PuppetTgt = null; _puppetFeed = 0f;
                if (_puppetFinale) { _puppetFinale = false; DetonateDoom(1f, true); }   // Leg Grand Finale: the music stops and every dancer goes off at once
            }
        }
        if (RoutT > 0f) RoutT -= dt;
        if (_doomWalking)   // (DOOM WALKER) it has ~2s to reach someone, then it lets go wherever it stands
        {
            _doomWalkT -= dt;
            bool arrived = Puppeted && GlobalPosition.DistanceTo(PuppetTgt.GlobalPosition) < Radius + PuppetTgt.Radius + 1.2f;
            if (_doomWalkT <= 0f || arrived || !Puppeted) ReleaseDoomWalk();
        }
        if (_bleedT > 0f)
        {
            if (!_bleedPersist) _bleedT -= dt;   // (BLOOD ROT mod) persistent rot never runs out — it bleeds until death
            _bleedTick -= dt;
            if (_bleedTick <= 0f) { _bleedTick = 0.3f; if (!Dead) { HurtFrom(_bleedOwner, _bleedDps * 0.3f, DamageType.Blood); Game.I.AwardDotCombo(_bleedOwner); } }   // (NEW) DoT trickles combo + soul credit to its caster
        }
        if (_poiT > 0f)
        {
            _poiT -= dt; _poiTick -= dt;
            SlowT = Mathf.Max(SlowT, 0.2f);   // poison ivy slows as long as it's ticking on them
            if (_poiTick <= 0f) { _poiTick = 0.4f; if (!Dead) { HurtFrom(_poiOwner, _poiDps * 0.4f, DamageType.Nature); Game.I.AwardDotCombo(_poiOwner); } }   // (NEW) DoT trickles combo + soul credit to its caster
            if (_poiT <= 0f) _poiDps = 0f;
        }
        if (_burnT > 0f)   // (NEW) Ember burn DoT — ticks stacks × per-stack dps; stacks reset when it burns out
        {
            _burnT -= dt; _burnTick -= dt;
            if (_burnTick <= 0f) { _burnTick = 0.4f; if (!Dead) { float bd = _burnStacks * _burnPerStack * 0.4f; HurtFrom(_burnOwner, bd, DamageType.Ember); Game.I?.AwardBurnLifesteal(_burnOwner, bd); } }   // (NEW) burn ticks lifesteal + soul credit to the owner (Wildfire Rush)
            if (_burnT <= 0f) _burnStacks = 0f;
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
        if (_deflectT > 0f) _deflectT -= dt;
        if (IsPhalanx || IsArcher) UpdatePhalanxState(dt);   // (NEW) ward dome + the host's 5Hz ward sync
        if (IsArcher) UpdateBowPose(dt);

        if (PhoenixHeld) { GlobalPosition = PhoenixHoldPos; RootT = Mathf.Max(RootT, 0.2f); return; }   // (PHOENIX) carried by the phoenix dive — locked in place; skips all AI/attack/shoot below so it can't act
        if (_thrown) { UpdateThrown(dt); return; }   // airborne fling owns movement; skip AI + ground-follow (NEW)
        if (_getUpT > 0f) { UpdateGetUp(dt); return; }   // downed → rising; stay staggered + open (NEW)
        if (_dbgBiped != null && AuthBiped) { DebugBipedTick(dt); return; }   // (HARNESS) hold a forced biped clip for inspection; skip AI

        var p = Game.I.Player;
        if (p == null) return;
        _tgt = Game.I.ResolveEnemyTarget(GlobalPosition, _behav == EBehav.Melee, out _tgtPeer, out _tgtIsMinion);   // melee foes can peel onto ents
        _tgtIsEnemy = false;
        if (Puppeted)   // (PUPPET) point it at its own ally and let every other system — flank, reach, wind-up, aim — run untouched
        { _tgt = PuppetTgt.GlobalPosition; _tgtPeer = 0; _tgtIsMinion = false; _tgtIsEnemy = true; }
        if (RoutT > 0f)   // (ROUT) panic outranks everything: chase a point behind yourself and your own legs do the fleeing
        { _tgt = GlobalPosition + _routDir * 40f; _tgtPeer = 0; _tgtIsMinion = false; _tgtIsEnemy = false; }
        Vector3 realTarget = _tgt;   // (NEW) the actual player/ent, before corridor retargeting (for vision + hunt speed)

        // (#3) self-track the target's velocity so fast foes can LEAD it. Works for any target (player/ally/minion); the
        // length guard rejects target-switch position jumps so a re-target never reads as one giant velocity spike.
        if (_tgtVelInit)
        {
            Vector3 dTgt = realTarget - _lastTgtPos;
            _tgtVel = (dt > 0.0001f && dTgt.LengthSquared() < 100f) ? _tgtVel.Lerp(dTgt / dt, 0.25f) : Vector3.Zero;
        }
        _lastTgtPos = realTarget; _tgtVelInit = true;
        Vector3 flatToTgt = realTarget - GlobalPosition; flatToTgt.Y = 0f;
        float distToTgt = flatToTgt.Length();   // GROUND-PLANE distance — a flyer/diver's hover altitude must never inflate the flank/lead math

        if (_behav == EBehav.Melee && _type != "taker")   // (#2) FLANK: each foe owns a persistent side (golden-angle) and approaches from it —
        {                                                   // wide offset when closing (the horde fans into a NET around you) → tightens to a body-hit when adjacent.
            float ang = NetId * 2.3999632f;
            float minOff = Radius * 2f + 1.2f;   // big foes (boss/miniboss/power-scaled) have minOff > 7 — Max-then-Min instead of Clamp so min never exceeds max (was a ThrowMinMaxException crash)
            float off = Mathf.Max(minOff, Mathf.Min(distToTgt * 0.4f, 7f));
            _tgt = realTarget + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * off;
        }
        // (#3) INTERCEPTION: fast CLOSERS (divers, bombers, fleet-footed melee ≥8 spd) path to where you'll BE — cutting you off from the front instead of trailing.
        // NOT flyers: they're ranged kiters, not closers, and leading their orbit both misfits the intent and (via _tgt.Y) broke their hover.
        bool leadsTarget = _behav == EBehav.Diver || _behav == EBehav.Bomber || (_behav == EBehav.Melee && _type != "taker" && Speed >= 8f);
        if (leadsTarget && _tgtVel.LengthSquared() > 1f)
        {
            Vector3 hv = _tgtVel; hv.Y = 0f;   // lead on the ground plane ONLY — adding vertical velocity to _tgt is what perturbed flyer/diver hover height
            float lead = Mathf.Clamp(distToTgt / Mathf.Max(Speed, 4f), 0f, 1.3f);   // ≈ time-to-reach, capped so they don't over-lead a juking target
            _tgt += hv * lead;
        }
        if (Game.I.InExpedition) _tgt = Game.I.ExpoNavTarget(GlobalPosition, _tgt, ((NetId * 2654435761u) % 1000u) / 1000f);   // route through doorways, fanned across the gap
        if (Game.I.InMaze) _tgt = Game.I.MazeWaypoint(GlobalPosition, _tgt);   // follow corridors instead of b-lining into hedges (NEW)
        // (REMOVED the keep-STAIRS detour: routing every ground foe to one ramp base was unreliable — half of them milled
        // around a spot nowhere near the stairs. Foes now scale the wall directly instead, slowly and at their own peril.)
        if (_hasteT > 0f) _hasteT -= dt;
        float spdMul = ((RootT > 0 || FrozenT > 0f || ShockT > 0f) ? 0f : (SlowT > 0 ? SlowMul : 1f)) * (_hasteT > 0f ? 1.4f : 1f) * (RoutT > 0f ? 1.3f : 1f) * (Game.I.InWater(GlobalPosition, GlobalPosition.Y - Radius) ? 0.7f : 1f) * (_climbing ? 0.5f : 1f);   // frozen/rooted → held in place; totem haste; hip-deep water wades them down; scaling a wall is half speed (NEW)
        if (Affix == 3) { _affixTick -= dt; if (_affixTick <= 0f) { _affixTick = 0.8f; VampHeal(); } }   // vampiric
        float pdist = (_tgt - GlobalPosition).Length();
        // catch-up speed ONLY for enemies that close distance — never boost kiters/fleers (they'd outrun you forever)
        _catchMul = ((_behav == EBehav.Melee || _behav == EBehav.Bomber || _behav == EBehav.Boss) && pdist > 40f)
            ? Mathf.Min(3.0f, 1f + (pdist - 40f) * 0.07f) : 1f;   // (TUNE) gentler + later — was min(4.5, +0.11/u past 34), which read as "ultra fast" when a foe chased a far target in MP
        // (NEW REFLOW) how long this foe has been stuck trailing the party. Catch-up speed alone can't close the gap on a
        // witch who keeps running, so once this stews long enough the director picks the foe up and re-inserts it AHEAD of
        // her instead (see Game.RunReflowDirector). Closing the distance drains it fast so a brief gap never counts.
        if (pdist > 40f) _chaseFarT += dt; else _chaseFarT = Mathf.Max(0f, _chaseFarT - dt * 2.5f);

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
                _losCacheT -= dt;
                if (_losCacheT <= 0f)   // (PERF) SightBlocked steps the whole Decks list — recompute ~5Hz, staggered across enemies, and reuse between
                {
                    _losCacheT = 0.2f + (GetInstanceId() % 7) * 0.02f;
                    Vector3 eye2 = GlobalPosition + new Vector3(0, Radius + 0.4f, 0);
                    _losCache = !Game.I.SightBlocked(eye2, new Vector3(realTarget.X, 1.2f, realTarget.Z));
                }
                bool los = _losCache;
                if (los) _losLostT = 0f; else _losLostT += dt;
                if (Game.I.InExpedition && !Game.I.MazeAggroPhase && _losLostT > 1.8f) { _alerted = false; _heard = 0f; _losLostT = 0f; }   // de-aggro is a maze-only mechanic; in endless waves zombies never go back to idle (esp. important in the jungle where trees keep breaking line-of-sight)
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
            if (swarmerIdle) { _creature.Rotation = new Vector3(0, Mathf.LerpAngle(_creature.Rotation.Y, _faceYaw, dt * 2f), 0); AnimStep(dt, 0f); }
            else
            {
                var fd = (_behav == EBehav.Melee && _type != "taker" ? realTarget : _tgt) - GlobalPosition; fd.Y = 0;   // face the PLAYER, not the surround slot (so hits land, not air)
                if (fd.LengthSquared() > 0.02f)
                {
                    float yaw = Mathf.Atan2(fd.X, fd.Z);
                    _creature.Rotation = new Vector3(0, Mathf.LerpAngle(_creature.Rotation.Y, yaw, dt * 8f), 0);
                }
                AnimStep(dt, spdMul);
            }
        }
        UpdateCastAnim(dt);   // (NEW) advance any authored cast clip + fire what its wind-up owes

        if (!Remote && _type == "swarmer")
        {
            if (_screamT > 0f) _screamT -= dt;
            ulong znow = Time.GetTicksMsec();
            if (znow - _lastZombieMs > 800 && GD.Randf() < 0.3f)   // (QUIETER) global groan cadence roughly halved so a horde isn't a constant moan
            {
                _lastZombieMs = znow;
                if (!_alerted && _idlePose == 3) Game.I.Sfx?.ZombieSnicker(GlobalPosition);   // snickering idlers chuckle
                else Game.I.Sfx?.ZombieGroan(GlobalPosition);
            }
        }

        if (!swarmerIdle && !takerActive && FrozenT <= 0f && ShockT <= 0f)   // (NEW) FROZEN = a total lockout: no moving, firing, casting, diving, or abilities — encased in ice. SHOCKED (Haunt bolt) locks out the same way.
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
            case EBehav.Lobber: MoveLobber(p, dt, spdMul); break;   // (NEW) croc: kite to range, lob a timed bomb
            case EBehav.Totem: MoveTotem(p, dt, spdMul); break;
            case EBehav.Phalanx: MovePhalanx(p, dt, spdMul); break;   // (NEW) warded formation: siege line while warded, charging bruiser once broken
            case EBehav.Archer: MoveArcher(p, dt, spdMul); break;     // (NEW) volleys from inside the ward; cowers/re-enlists once it breaks
            case EBehav.Boss:
                if (_p2Stage == 1 || _p2Stage == 2 || _spinT > 0f) { }                            // (PHASE 2) down / rising / planted and spinning — no locomotion
                else if (_p2Stage == 3) BossLaughAdvance(dt);                                     // (PHASE 2) the unsteady laughing walk-in after he stands up
                else if (_dashT > 0f) BossDashRun(dt);                                            // (NEW) the head-down dash owns his movement
                else if (!_bossCharging) MoveMelee(p, dt, spdMul * Mathf.Lerp(1f, 1.5f, _bossHeat));   // freeze while telegraphing; hotter → faster (NEW)
                BossFire(p, dt);
                break;
        }

        // vertical: ground enemies follow the surface and climb ramps/walls toward an elevated player
        if (_behav != EBehav.Flyer && _behav != EBehav.Diver)
        {
            float feet = GlobalPosition.Y - Radius;
            float support = Game.I.SurfaceHeight(GlobalPosition, feet);
            float targetFeet = support;
            bool climbing = false;
            if (_tgt.Y > feet + 1.2f)   // player is up high — scale toward them
            {
                var hdir = _tgt - GlobalPosition; hdir.Y = 0;
                if (hdir.LengthSquared() > 0.01f) hdir = hdir.Normalized(); else hdir = Vector3.Forward;
                var ahead = GlobalPosition + hdir * (Radius + 1.6f);
                // (CHANGE) full-height surface query — foes CAN scale a vertical face again (the step-limited version left
                // them stuck at the wall, and the stairs detour that was meant to fix it clumped them up). Anything beyond
                // a normal step up counts as a real CLIMB: half the rise rate, half their ground speed (spdMul), and they
                // hang there exposed — a crit, knockback or fling peels them off for the fall (PeelOffWall).
                float deckAhead = Game.I.SurfaceHeight(ahead, 1e9f);
                if (deckAhead > feet + 0.3f)
                {
                    targetFeet = Mathf.Clamp(deckAhead, support, Mathf.Max(support, _tgt.Y));   // ceiling is at least the support height, so min can never exceed max (fixes ThrowMinMaxException on tall decks) (NEW)
                    climbing = !Game.I.InSky && deckAhead > feet + 1.7f;   // sky islands keep the free, full-speed climb
                    if (climbing) _climbDir = hdir;
                }
            }
            _climbing = climbing;
            float newFeet = Mathf.MoveToward(feet, targetFeet, (climbing ? 4.5f : 9f) * dt);
            GlobalPosition = new Vector3(GlobalPosition.X, newFeet + Radius, GlobalPosition.Z);
            Game.I.MaybeWaterTrail(GlobalPosition, GlobalPosition.Y - Radius, dt);   // enemy ripples while wading (NEW)
        }
        if (!(_type == "taker" && (_takerState == 1 || _takerState == 3)))   // Taker plows through while charging/carrying
        {
            SeparateFromPlayers();
            SeparateFromEnemies(dt);   // (NEW) spread the horde so bodies don't stack
        }
        // hard-collide with trees + structure walls EVERY frame, all modes (was Expedition-only, so foes ghosted
        // through fort/ruin walls in the world). A charging Taker plows through (its charge resolves walls via stun).
        if (!(_type == "taker" && _takerState == 1))
        {
            GlobalPosition = ClampArena(GlobalPosition);
            _stuckChk += dt;
            if (_stuckChk > 0.5f)   // wedged against geometry while trying to move → reverse our swerve side to escape
            {
                if (_avoidSign != 0f && GlobalPosition.DistanceSquaredTo(_stuckRef) < (Radius * 0.5f) * (Radius * 0.5f)) _avoidSign = -_avoidSign;
                _stuckRef = GlobalPosition; _stuckChk = 0f;
            }
        }
    }

    private float _rushCd = 3f, _rushWind = 0f, _rushLunge = 0f; private Vector3 _rushDir;   // (NEW) jungle-troll charge

    // (NEW) jungle troll: periodic RUSH — rear back (telegraph), then dash forward fast; the rush hit deals bonus damage and
    // staggers you (jtroll HitTarget applies the stun). Returns true while it's busy so MoveMelee yields.
    private bool TrollRush(float dt, float reach)
    {
        Vector3 tt = _tgt - GlobalPosition; tt.Y = 0; float td = tt.Length();
        if (_rushLunge > 0f)
        {
            _rushLunge -= dt;
            GlobalPosition += _rushDir * (Speed * 3.8f) * dt;
            if (td < reach + 0.6f) { _creature?.Strike(); HitTarget(Dmg * 1.4f); _rushLunge = 0f; _rushCd = 4.5f * Pace; }
            else if (_rushLunge <= 0f) _rushCd = 3.5f * Pace;
            return true;
        }
        if (_rushWind > 0f)
        {
            _rushWind -= dt;
            _creature?.SetSwing(Mathf.Clamp(1f - _rushWind / 0.55f, 0f, 1f));   // rear back
            if (_rushWind <= 0f) { _creature?.SetSwing(0f); _rushDir = td > 0.1f ? tt.Normalized() : Vector3.Forward; _rushLunge = 0.55f; Game.I.Sfx?.EnemyGrowl(GlobalPosition); }
            return true;
        }
        _rushCd -= dt;
        if (_rushCd <= 0f && td < 18f && td > 4f) { _rushWind = 0.55f; return true; }
        return false;
    }

    private void MoveMelee(Player p, float dt, float spdMul)
    {
        float reach = Radius + 1.4f;
        if (_type == "jtroll" && TrollRush(dt, reach)) return;   // (NEW) charging rush
        if (MeleeAttack(dt, reach)) return;   // winding up — hold position so the swing reads as a telegraph
        Vector3 to = _tgt - GlobalPosition; to.Y = 0;
        float dist = to.Length();
        if (dist > reach && spdMul > 0f)
        {
            float sp = Speed * _catchMul * spdMul;
            if (_type == "swarmer" && Game.I.Player != null) sp *= Game.I.Player.MoveSpeedFactor;   // (NEW) keep pace with a fast player
            Vector3 mv = AvoidBlockers(to);   // (NEW) route around trees/pillars instead of jamming into them
            GlobalPosition = ClampArena(GlobalPosition + mv * sp * dt);
        }
    }

    // (NEW) vertical reach gate. Reach/touch checks used XZ distance only, so an enemy could hit a player who had
    // jumped or flown well above (or below) it. The player origin sits at their feet; this enemy's body spans about
    // [-Radius, +2.4*Radius] around its origin. Require the player to be within that band (plus a little jump/arm slack)
    // before a swing or touch can land — a normal hop still gets hit, but flying up / standing on a high ledge doesn't.
    private bool VertReach()
    {
        float dy = _tgt.Y - GlobalPosition.Y;
        return dy <= Radius * 2.4f + 1.5f && dy >= -(Radius + 1.5f);
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
                if (dist < reach + 0.7f && VertReach()) HitTarget(Dmg);   // (NEW) whiff if the target jumped/flew out of vertical reach
                _atkCd = 1.0f * Pace;
            }
            return true;
        }
        if (dist < reach && _atkCd <= 0f && VertReach())   // (NEW) don't wind up a swing at a player who's out of vertical reach
        {
            _swinging = true; _atkWind = WindUpDur;
            ulong now = Time.GetTicksMsec();
            if (now - _lastGrowlMs > 200) { _lastGrowlMs = now; Game.I.Sfx?.EnemyGrowl(GlobalPosition); }   // audible tell
            // (MP FIX) tell the clients to swing too. The snapshot carries position/HP/status but NOTHING about
            // attacking, and StatusMask has no free bits left — so melee foes slid silently into a client and the
            // damage arrived with no wind-up, no tell and no animation. This is the same channel the casters use.
            if (!Remote) Game.I.NetMgr?.BroadcastEnemySwing(NetId, WindUpDur);
            return true;
        }
        return false;
    }

    // client proxy: replay a melee wind-up + strike the host just started, so the swing READS on this machine.
    // Purely cosmetic — the damage is the host's and arrives over DamagePlayer.
    public void RemoteSwing(float wind)
    {
        if (!Remote || _creature == null) return;
        _swinging = true; _atkWind = Mathf.Max(0.05f, wind);
        ulong now = Time.GetTicksMsec();
        if (now - _lastGrowlMs > 200) { _lastGrowlMs = now; Game.I.Sfx?.EnemyGrowl(GlobalPosition); }
    }
    // drives the proxy's wind-up each frame and lands the visual strike at the end (no damage — host-authoritative)
    private void UpdateRemoteSwing(float dt)
    {
        if (!_swinging) return;
        _atkWind -= dt;
        _creature?.SetSwing(Mathf.Clamp(1f - _atkWind / WindUpDur, 0f, 1f));
        if (_atkWind <= 0f) { _swinging = false; _creature?.SetSwing(0f); _creature?.Strike(); }
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

    // (NEW) soft mutual repulsion so the horde spreads around the target instead of stacking/clipping into one body.
    // (PERF) the O(n) scan over all enemies is the O(n²) director cost — recompute the push only ~15Hz (staggered so
    // not every enemy scans the same frame), but APPLY the cached push every frame so movement stays smooth.
    private float _sepT = 0f; private Vector3 _sepPush = Vector3.Zero;
    private Vector3 _lastTgtPos = Vector3.Zero, _tgtVel = Vector3.Zero; private bool _tgtVelInit = false;   // (#3) self-tracked target velocity → fast-foe interception
    // (PERF) procedural animation writes ~15-30 part transforms per enemy per frame (real CPU across the C#→engine boundary).
    // In a big swarm, throttle it to ~30Hz staggered — imperceptible on a churning horde. Small fights animate at full rate
    // (best look for a lone foe). Accumulated dt is passed so the walk cycle keeps real-time speed despite skipped frames.
    private float _animAcc = 0f;
    private void AnimStep(float dt, float amt)
    {
        if (_creature == null) return;
        if (AuthBiped) { if (_climbing) _creature.BipedClimb(); else _creature.BipedLoco(false); }   // (NEW) scaling a wall → climb clip; else walk/shamble
        _animAcc += dt;
        float gate = (Game.I != null && Game.I.Enemies.Count > 40) ? 0.03f + (GetInstanceId() % 3) * 0.004f : 0f;
        if (_animAcc < gate) return;
        _creature.Animate(_animAcc, amt);
        _animAcc = 0f;
    }

    private void SeparateFromEnemies(float dt)
    {
        if (_behav == EBehav.Flyer || _behav == EBehav.Diver || Game.I == null) return;
        _sepT -= dt;
        if (_sepT <= 0f)
        {
            _sepT = 0.07f + (GetInstanceId() % 4) * 0.008f;
            float px = 0f, pz = 0f;
            var list = Game.I.QueryEnemies(GlobalPosition.X, GlobalPosition.Z, Radius + 4f);   // (PERF) only neighbours in the nearby cells, not the whole horde (was O(N²))
            for (int i = 0; i < list.Count; i++)
            {
                var o = list[i];
                if (o == null || o == this || o.Dead || !GodotObject.IsInstanceValid(o)) continue;
                float md = (Radius + o.Radius) * 1.35f + 0.5f;   // (#1) personal space LARGER than the bodies → they push apart before touching, fanning the horde into a wide crescent instead of a stacked blob
                float ox = GlobalPosition.X - o.GlobalPosition.X, oz = GlobalPosition.Z - o.GlobalPosition.Z;
                float dd = ox * ox + oz * oz;
                if (dd < md * md && dd > 0.0001f) { float d = Mathf.Sqrt(dd); float f = (md - d) / md; px += ox / d * f; pz += oz / d * f; }
            }
            _sepPush = new Vector3(px, 0f, pz);
        }
        // cap the push as a fraction of THIS unit's own speed so seek always wins — a flat cap (was 3.2) exceeded slow heavies' move speed (sentinel 2.0, sieger 2.2, brute 2.6) and froze them in place.
        if (_sepPush.LengthSquared() > 0.0001f) GlobalPosition += _sepPush.LimitLength(Mathf.Min(3.2f, Speed * 0.55f) * dt);
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
            var np = GlobalPosition + AvoidBlockers(want) * Speed * _catchMul * spdMul * dt;   // (NEW) steer around trunks so ranged foes don't wedge on trees
            GlobalPosition = ClampArena(np);
        }
        if (Dmg > 0 && dist < Radius + 1.4f && _touchCd <= 0f && VertReach()) { HitTarget(Dmg); _touchCd = 0.7f * Pace; }

        // fire — the mage cast animation (cast4) covers the wind-up on any body that has it; see BeginCast
        if (charged)
        {
            if (_chargeT > 0f) { _chargeT -= dt; if (_chargeT <= 0f) FireAt(p, _boltSpeed, _boltDmg, _boltRadius); }
            else if (_fireCd <= 0f && dist < _range) { _chargeT = _chargeDur; _fireCd = _fireEvery * Pace; BeginCastAnim("cast4", _chargeDur, _fireCd); }   // (NEW) sieger: its existing charge IS the wind-up
        }
        else if (_castWindT <= 0f && _fireCd <= 0f && dist < _range)
        {
            _fireCd = _fireEvery * Pace;
            BeginCast("cast4", Mathf.Min(CastWind, _fireCd * 0.5f), 1, _fireCd);
        }
    }

    // (NEW) croc: kite to range, then LOB a timed bomb that arcs onto the target's feet and blasts ~2s after landing
    private void MoveLobber(Player p, float dt, float spdMul)
    {
        Vector3 to = _tgt - GlobalPosition; to.Y = 0;
        float dist = to.Length();
        Vector3 dir = dist > 0.01f ? to.Normalized() : Vector3.Forward;
        Vector3 want = Vector3.Zero;
        if (dist > _preferDist + 2f) want += dir;
        else if (dist < _preferDist - 2f) want -= dir;
        _strafeT -= dt; if (_strafeT <= 0) { _strafe = -_strafe; _strafeT = (float)GD.RandRange(1.5, 3.0); }
        want += new Vector3(-dir.Z, 0, dir.X) * _strafe * 0.4f;
        if (want.LengthSquared() > 0.001f && spdMul > 0f) GlobalPosition = ClampArena(GlobalPosition + AvoidBlockers(want) * Speed * _catchMul * spdMul * dt);   // (NEW) steer around trunks
        if (Dmg > 0 && dist < Radius + 1.4f && _touchCd <= 0f && VertReach()) { HitTarget(Dmg); _touchCd = 0.7f * Pace; }
        if (_fireCd <= 0f && dist < _range)
        {
            _fireCd = _fireEvery * Pace;
            var at = new Vector3(_tgt.X, Game.I.SurfaceHeight(_tgt, _tgt.Y), _tgt.Z);
            Game.I.SpawnCrocBomb(GlobalPosition + Vector3.Up * 1.5f, at, _boltDmg, _boltRadius);
        }
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
        if (_fireCd <= 0f && dist < _range) { FireAt(p, _boltSpeed, _boltDmg, _boltRadius); _fireCd = _fireEvery * Pace; }
    }

    private MeshInstance3D _healTether;
    // a glowing green beam from the healer to the ally it's currently mending, so the player can tell WHO to focus.
    // Purely visual → runs on host + clients (ally is picked locally from synced positions). Freed with the healer.
    private void UpdateHealerTether()
    {
        // match the heal logic: the nearest HURT, non-goblin ally within heal range is who it's actually mending
        Enemy ally = null; float best = 12f;
        var list = Game.I.Enemies;
        for (int i = 0; i < list.Count; i++)
        {
            var e = list[i];
            if (e == this || e == null || e.Dead || !GodotObject.IsInstanceValid(e) || e.IsGoblin) continue;
            if (e.Hp >= e.MaxHp) continue;   // only foes that actually need topping up
            float d = GlobalPosition.DistanceTo(e.GlobalPosition);
            if (d < best) { best = d; ally = e; }
        }
        if (ally == null) { if (_healTether != null) _healTether.Visible = false; return; }
        if (_healTether == null)
        {
            var gc = DamageTypes.Col(DamageType.Holy);
            var m = Game.ToonEmissive(gc, 2.6f, 0f); m.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; m.AlbedoColor = new Color(gc.R, gc.G, gc.B, 0.6f);
            _healTether = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.07f, BottomRadius = 0.07f, Height = 1f, RadialSegments = 5 }, MaterialOverride = m, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, TopLevel = true };
            AddChild(_healTether);   // child of the healer → auto-freed on death; TopLevel → world-space, ignores the healer's facing
        }
        _healTether.Visible = true;
        Vector3 a = GlobalPosition + Vector3.Up * (Radius * 0.8f);
        Vector3 b = ally.GlobalPosition + Vector3.Up * (ally.Radius * 0.8f);
        Vector3 dir = b - a; float len = dir.Length();
        if (len < 0.05f) { _healTether.Visible = false; return; }
        Vector3 yb = dir / len;
        Vector3 xb = yb.Cross(Vector3.Forward); if (xb.LengthSquared() < 1e-4f) xb = yb.Cross(Vector3.Right); xb = xb.Normalized();
        Vector3 zb = xb.Cross(yb).Normalized();
        _healTether.GlobalTransform = new Transform3D(new Basis(xb, yb * len, zb), (a + b) * 0.5f);   // Y-scaled to span a→b
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
        if (spdMul > 0f) { var np = GlobalPosition + AvoidBlockers(want) * Speed * _catchMul * spdMul * dt; GlobalPosition = ClampArena(np); }   // (NEW) steer around trunks

        _healCd -= dt;
        if (_healCd <= 0f && _castWindT <= 0f && ally != null && ally.GlobalPosition.DistanceTo(GlobalPosition) < 12f)
        {
            _healCd = _healEvery * Pace;
            BeginCast("cast", Mathf.Min(CastWind, _healCd * 0.5f), 2, _healCd);   // (NEW) the mage cast plays the mend; the heal lands on its release frame
        }
    }

    // the mend itself — re-resolves its target at the release frame, so a mid-cast death just wastes the cast
    private void HealPulse()
    {
        Enemy ally = NearestAlly();
        if (ally == null || ally.GlobalPosition.DistanceTo(GlobalPosition) > 14f) return;
        ally.Heal(_healAmt);
        var v = new Vfx(); Game.I.AddChild(v);
        v.GlobalPosition = ally.GlobalPosition + new Vector3(0, ally.Radius, 0);
        v.Init(new SphereMesh { Radius = ally.Radius * 0.6f, Height = ally.Radius * 1.2f }, DamageTypes.Col(DamageType.Holy), 0.4f, 2f);
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
    public string BossAttackName => _bossPatPending switch { 1 => "RADIAL BURST", 3 => "NOVA", 4 => "PESTILENCE", 5 => "STOMP", 6 => "ROCK THROW", 7 => "MINES", 8 => "CHARGE", _ => "VOLLEY" };   // (NEW) attack meter label
    // (NEW) hitting high — the head or a shoulder goblin — always crits THE HOLLOW MOON
    public bool IsCritZone(Vector3 hitPos)
    {
        if (IsBoss && _type == "boss") return (hitPos.Y - GlobalPosition.Y) > Radius * 1.9f;   // THE HOLLOW MOON's head/shoulders/upper chest (~2R+ above origin) — the waist/pelvis (~0.9R) no longer counts
        if (_type == "sentinel" && _sentinelCore != null && GodotObject.IsInstanceValid(_sentinelCore))
            return hitPos.DistanceTo(_sentinelCore.GlobalPosition) < Radius * 0.9f;   // (NEW) strike the exposed core → auto-crit through the armor
        return false;
    }

    // (NEW) capsule hit test — the visual model builds UP from ~the feet (origin sits low, head is ~Radius*1.9 above it),
    // so a sphere at the origin only covered the legs of tall foes ("can only hit their feet"). This tests distance to the
    // whole body spine (feet → head) so any point on the model registers. Radial girth stays ~Radius (matches the mesh width).
    public bool HitBy(Vector3 point, float pad)
    {
        // The model builds UP from ~the feet: the origin sits ~Radius above the feet and the model's head reaches
        // ~2.4*Radius above the origin (matching the melee aim band in AimHitOnEnemy). A fixed 1.9R spine left the upper
        // body/head un-hittable — worst on the boss, whose bespoke model is taller still (head ~2.6R above origin), so
        // shots only landed below the waist. Scale the spine to the real model, extending it further for the boss.
        float hi = _type == "boss" ? 3.0f : 2.4f;
        float lo = _type == "boss" ? 1.0f : 0.7f;
        Vector3 a = GlobalPosition + Vector3.Down * Radius * lo;   // near the feet
        Vector3 b = GlobalPosition + Vector3.Up * Radius * hi;     // top of the head
        Vector3 ab = b - a;
        float t = Mathf.Clamp((point - a).Dot(ab) / ab.LengthSquared(), 0f, 1f);
        return point.DistanceTo(a + ab * t) < Radius + pad;
    }

    // (NEW) ray-vs-body — for BEAMS and travelling projectiles, so the WHOLE model (feet→head) blocks/collides with the ray,
    // not just a sphere at the low origin (which let shots sail over a tall boss's head). Returns the entry distance `t`.
    public bool RayHitsBody(Vector3 o, Vector3 dir, float maxLen, float pad, out float t)
    {
        t = maxLen;
        float hi = _type == "boss" ? 3.0f : 2.4f, lo = _type == "boss" ? 1.0f : 0.7f;
        Vector3 a = GlobalPosition + Vector3.Down * Radius * lo;
        Vector3 b = GlobalPosition + Vector3.Up * Radius * hi;
        float r = Radius + pad; bool any = false;
        const int N = 6;
        for (int i = 0; i <= N; i++)
        {
            Vector3 p = a.Lerp(b, i / (float)N);
            float proj = (p - o).Dot(dir);
            if (proj < 0.5f || proj > maxLen) continue;
            if ((p - o - dir * proj).Length() < r && proj < t) { t = proj; any = true; }
        }
        return any;
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
    // ---- head-down charge: every 20% of his max HP the coven strips off him, he answers with 30u of shoulder ----
    // ======================= THE HOLLOW MOON — PHASE 2 =======================
    // Killing him the first time doesn't kill him: he plays the fall-forward clip, lies there laughing, gets back up on
    // half a health bar and fights harder. Everything below is host-authoritative; clients mirror via Net.BroadcastBossPhase2.
    public const float P2HpFrac = 0.5f;        // his phase-2 pool = half his phase-1 max. Every threshold below is a % of THAT.
    public const float P2DamageMul = 1.5f;     // the ONLY damage multiplier phase 2 adds — `enraged` gives projectiles, never damage
    public const float P2SpeedMul = 1.35f;
    private const float P2GroundDur = 5f;      // prone on the death clip's final frame…
    private const float P2LaughAt = 3f;        // …starting to laugh 3s in
    private const float P2WalkDur = 3f;        // then the unsteady laughing advance after standing up
    public const float SpinDur = 10f;
    private const float SpinDpsFrac = 0.035f;  // ~3.5% of a witch's max HP per second while caught — enough that 10s in
                                               // the funnel strips her shield AND bites HP, so the finisher lands raw

    public int BossPhase = 1;
    private float _p2Dmg = 1f;                          // 1 in phase 1, P2DamageMul in phase 2
    public bool Invuln = false;                         // revival sequence + the whole spin; drives the HUD read-out
    public bool BossInvuln => Invuln;
    private int _p2Stage = 0;                           // revival: 0 none, 1 prone, 2 rising, 3 laughing advance
    private float _p2T = 0f;
    private bool _p2Laughed = false;
    private int _tripleLeft = 0;                        // charges left in the current 3x set
    private int _step25 = 0, _step33 = 0;               // how many 25%/33% thresholds have fired
    private long _lastDashPeer = long.MinValue;         // who the previous charge in this set went at (so the next picks someone else)
    private bool _spinPending = false;                  // threshold crossed — he finishes the charges, THEN spins (owner's call)
    private float _spinT = 0f;
    private BossVortex _vortex;
    public bool BossSpinning => _spinT > 0f;
    public bool BossReviving => _p2Stage != 0;

    // (named _dash* rather than _charge* so it never reads as the existing _bossChargeT/_bossChargeDur WIND-UP timers)
    public const float DashDist = 30f;           // how far he travels
    private const float DashDur = 0.72f;         // …and how fast he covers it
    private int _hpStep = 0;                     // how many 20% thresholds have already fired (0-4)
    private bool _dashArmed = false;             // a threshold was crossed; the next BossFire launches it
    private float _dashT = 0f;                   // >0 while the dash is actually running
    private Vector3 _dashDir = Vector3.Forward;
    private float _dashMoved = 0f;               // how far the dash itself has pushed him (vs. where he ended up)
    private readonly System.Collections.Generic.HashSet<long> _dashHit = new();   // one hit per warden per charge
    public bool BossDashing => _dashT > 0f;

    // Called from Hurt(). PHASE 1: arm a single charge each time he crosses another 20% boundary.
    // PHASE 2: that single charge is retired — instead every 25% arms a THREE-charge set, and every 33% arms the spin.
    private void NoteChargeThreshold()
    {
        if (_type != "boss" || MaxHp <= 0f || Dead) return;
        if (BossPhase == 1)
        {
            int step = Mathf.Clamp(Mathf.FloorToInt((1f - Hp / MaxHp) / 0.2f + 0.0001f), 0, 4);
            if (step <= _hpStep) return;
            _hpStep = step;
            _dashArmed = true;
            return;
        }
        int s25 = Mathf.Clamp(Mathf.FloorToInt((1f - Hp / MaxHp) / 0.25f + 0.0001f), 0, 4);
        if (s25 > _step25) { _step25 = s25; _tripleLeft = 3; _lastDashPeer = long.MinValue; }
        int s33 = Mathf.Clamp(Mathf.FloorToInt((1f - Hp / MaxHp) / (1f / 3f) + 0.0001f), 0, 3);
        if (s33 > _step33)
        {
            _step33 = s33;
            // (OWNER'S CALL) finish-then-spin: he goes untouchable the INSTANT the threshold is crossed, rides out any
            // charges he's mid-way through, then spins — and only becomes vulnerable again once the spin ends.
            _spinPending = true;
            Invuln = true;
            Game.I?.NetMgr?.BroadcastBossPhase2(NetId, BossPhase, 1);
        }
    }

    // ---- phase 1 "death": he doesn't die, he falls, laughs, and gets back up on half a bar ----
    private void EnterPhase2()
    {
        BossPhase = 2;
        Dead = false;
        MaxHp *= P2HpFrac;                 // the phase-2 pool; every threshold above is a % of this
        Hp = MaxHp;
        _p2Dmg = P2DamageMul;
        Dmg *= P2DamageMul; _boltDmg *= P2DamageMul;   // contact + projectile; the explicit AoE numbers multiply at their sites
        Speed *= P2SpeedMul;
        _hpStep = 4; _dashArmed = false; _dashT = 0f;  // the single 20% charge is retired in phase 2
        _step25 = 0; _step33 = 0; _tripleLeft = 0; _spinPending = false;
        _bossCharging = false; _bossChargeT = 0f;
        Invuln = true;                                  // …for the whole fall → prone → rise → laughing-advance window
        _p2Stage = 1; _p2T = P2GroundDur; _p2Laughed = false;
        _creature?.BossDie();                           // the fall-forward clip; it holds its final frame when it ends
        // NOTE: the aura is NOT lit here. He's still a corpse as far as the coven knows — it ignites when he stands up,
        // which is the reveal. See the stage 2 -> 3 transition in UpdatePhase2.
        Game.I?.Sfx?.BossRoar(GlobalPosition);
        Game.I?.Hud?.Banner("THE HOLLOW MOON RISES AGAIN");
        Game.I?.NetMgr?.BroadcastBossPhase2(NetId, 2, 1);
        SayBossVox("YOU THOUGHT THAT WAS ALL?", new Color(0.75f, 0.5f, 1f), 3f);
    }

    // the revival sequence + the spin, ticked every frame from BossFire (host) and mirrored on proxies
    private void UpdatePhase2(float dt)
    {
        if (_p2Stage != 0)
        {
            _p2T -= dt;
            if (_p2Stage == 1)                                  // prone on the clip's last frame
            {
                if (!_p2Laughed && _p2T <= P2GroundDur - P2LaughAt)
                { _p2Laughed = true; Game.I?.Sfx?.TakerLaugh(GlobalPosition); SayBossVox("HEH... HEH HEH...", new Color(0.75f, 0.5f, 1f), 2.2f); }
                if (_p2T <= 0f) { _p2Stage = 2; _creature?.BossPlay("standup", 1f); }
            }
            else if (_p2Stage == 2)                             // Stand_Up8 — end as soon as the clip finishes
            {
                if (_creature != null && _creature.BipedOneShotDone)
                { _p2Stage = 3; _p2T = P2WalkDur; _creature.BossEndClip(); _creature.SetPhase2(); Game.I?.Sfx?.TakerLaugh(GlobalPosition); }
                else if (_p2T < -4f) { _p2Stage = 3; _p2T = P2WalkDur; _creature?.BossEndClip(); _creature?.SetPhase2(); }   // safety net if the clip never reports done
            }
            else                                                 // laughing advance on the unsteady walk
            {
                if (_p2T <= 0f)
                {
                    _p2Stage = 0; Invuln = false;
                    _tripleLeft = 3; _lastDashPeer = long.MinValue;   // …straight into the first three-charge set
                    Game.I?.NetMgr?.BroadcastBossPhase2(NetId, 2, 0);
                }
            }
            return;
        }
        if (_spinT > 0f)
        {
            _spinT -= dt;
            if (_vortex != null && GodotObject.IsInstanceValid(_vortex)) _vortex.Follow(GlobalPosition);
            _creature?.SetHandGlow(1f);
            // He has no authored spin clip, so WHIP the whole model — fast enough that the pose can't be read — and let
            // the tightened aura + funnel core swallow most of his silhouette. Ramps in so it doesn't snap.
            if (_creature != null)
            {
                float ramp = Mathf.Clamp((SpinDur - _spinT) / 0.7f, 0f, 1f);
                _creature.RotateY(dt * 26f * ramp);
            }
            if (_spinT <= 0f)
            {
                _vortex = null;
                Invuln = false;
                _creature?.SetHandGlow(0f);
                _creature?.SetSpinning(false);
                Game.I?.NetMgr?.BroadcastBossPhase2(NetId, 2, 0);
            }
        }
    }

    // he plants and spins up; the vortex node owns the drag, the grind and the finishing stomp
    private void HostStartSpin()
    {
        _spinPending = false;
        _spinT = SpinDur;
        Invuln = true;
        _bossCharging = false; _dashT = 0f;
        var pl = Game.I?.Player;
        float dps = (pl != null ? pl.S.MaxHp : 120f) * SpinDpsFrac;
        _vortex = new BossVortex();
        Game.I.AddChild(_vortex);
        _vortex.Init(GlobalPosition, SpinDur, dps, hostSim: true);
        _creature?.SetSpinning(true);
        Game.I.NetMgr?.BroadcastBossVortex(GlobalPosition, SpinDur, dps);
        Game.I.NetMgr?.BroadcastBossPhase2(NetId, 2, 1);
        SayBossVox("COME TO ME!", new Color(0.75f, 0.5f, 1f), 2f);
        Game.I.Sfx?.BossRoar(GlobalPosition);
        Game.I.Hud?.Banner("THE VORTEX PULLS — GET OUT");
    }

    // Stage 3 of the revival: he lurches toward the coven on the unsteady walk, still laughing, still untouchable.
    // Deliberately slow — this is the "oh no, he's getting up" beat, not an attack.
    private void BossLaughAdvance(float dt)
    {
        var to = _tgt - GlobalPosition; to.Y = 0f;
        if (to.LengthSquared() < 0.01f) return;
        var dir = to.Normalized();
        GlobalPosition += dir * (Speed * 0.45f) * dt;
        if (_creature != null)
            _creature.Rotation = new Vector3(0, Mathf.LerpAngle(_creature.Rotation.Y, Mathf.Atan2(dir.X, dir.Z), dt * 5f), 0);
        if (_p2Laughed && GD.Randf() < dt * 0.7f) Game.I?.Sfx?.TakerLaugh(GlobalPosition);   // ~0.7 cackles/sec while he closes
    }

    // next charge target: the nearest LIVING witch that isn't the one the previous charge in this set went at
    private Vector3 NextTripleTarget()
    {
        Vector3 best = _tgt; float bestD = float.MaxValue; long bestPeer = long.MinValue; bool found = false;
        void Consider(long peer, Vector3 pos)
        {
            if (peer == _lastDashPeer) return;
            float d = new Vector2(pos.X - GlobalPosition.X, pos.Z - GlobalPosition.Z).LengthSquared();
            if (d < bestD) { bestD = d; best = pos; bestPeer = peer; found = true; }
        }
        var pl = Game.I?.Player;
        if (pl != null && !pl.Downed) Consider(Game.I.LocalPeer, pl.GlobalPosition);
        if (Game.I?.NetMgr != null && Game.I.NetMgr.Active)
            foreach (var (peer, pos) in Game.I.NetMgr.AliveAllyPositions()) Consider(peer, pos);
        if (!found) { _lastDashPeer = long.MinValue; return _tgt; }   // solo (or everyone else down) → same witch again
        _lastDashPeer = bestPeer;
        return best;
    }

    // the dash: he plows forward on his own vector, shoving aside any warden he clips. Host-authoritative; clients see the
    // proxy slide because the host streams his position as usual.
    private void BossDashRun(float dt)
    {
        float move = Mathf.Min(dt, _dashT);   // don't overshoot the clock on the last frame — he must cover exactly DashDist
        _dashT -= dt;
        var prev = GlobalPosition;
        float step = (DashDist / DashDur) * move;
        GlobalPosition += _dashDir * step;
        _dashMoved += step;   // what the dash PUSHED, vs. where he ended up (terrain/structure resolve can shorten the latter)
        if (!Remote)
            Game.I.NetMgr?.ChargeSweep(prev, GlobalPosition, Radius + 2.6f, (30f + Game.I.Wave * 1.4f) * _p2Dmg, 26f, _dashHit);
        if (GD.Randf() < 0.55f) Game.I.SpawnDust(new Vector3(GlobalPosition.X, GlobalPosition.Y - Radius, GlobalPosition.Z), Vector3.Up);
        if (_dashT <= 0f)
        {
            Game.I.VfxRing(GlobalPosition, new Color(0.85f, 0.35f, 0.25f), 6f, 0.45f);
            Game.I.SpawnGroundSpikes(GlobalPosition, 6f, 9, new Color(0.7f, 0.4f, 0.2f), 0.4f);
            Game.I.Sfx?.Thud(GlobalPosition, net: false);
        }
    }

    private void BossFire(Player p, float dt)
    {
        // real-time heat: biggest driver is missing HP; recent player DPS nudges it up. Smoothly tracked so it ramps.
        float dpsF = MaxHp > 0f ? Mathf.Clamp(Game.I.BossRecentDps / (MaxHp * 0.03f), 0f, 1f) : 0f;
        float target = Mathf.Clamp(0.12f + 0.66f * (1f - Hp / MaxHp) + 0.22f * dpsF, 0f, 1f);
        _bossHeat = Mathf.MoveToward(_bossHeat, target, dt * 0.5f);
        _bossNovaCd -= dt; _bossPestCd -= dt; _bossStompCd -= dt;
        _bossRockCd -= dt; _bossMineCd -= dt; if (_goblinBufferCd > 0f) _goblinBufferCd -= dt;
        if (_critVoxCd > 0f) _critVoxCd -= dt;
        UpdateBossAnim(dt);   // (HOLLOW MOON) authored clip + procedural gesture + hand-glow telegraph
        if (BossPhase == 2)
        {
            UpdatePhase2(dt);
            if (_p2Stage != 0 || _spinT > 0f) return;   // reviving or spinning: he does nothing else
            // finish-then-spin: the pending spin only fires once the current charge set is spent (and he's untouchable meanwhile)
            if (_spinPending && _tripleLeft <= 0 && _dashT <= 0f && !_bossCharging) { HostStartSpin(); return; }
        }
        if (_creature != null && !_hollowAnim) _creature.StompWind = (_bossCharging && _bossPatPending == 5 && _bossChargeDur > 0.01f) ? Mathf.Clamp(1f - _bossChargeT / _bossChargeDur, 0f, 1f) : 0f;   // (NEW) raise the good leg through the stomp wind-up (procedural body only — the authored boss has a real stomp clip)
        if (_dashT > 0f) return;   // (CHARGE) mid-dash: no new attacks, no re-aim — he's committed
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
            FireBossAnim(_bossPatPending);
            if (_bossPatPending == 8) { _fireCd = Mathf.Max(_fireCd, 1.2f); return; }   // (CHARGE) the dash itself is the recovery
            if (_bossPatPending == 3) { _bossNovaCd = Mathf.Lerp(4.5f, 2.5f, _bossHeat); _fireCd = Mathf.Max(_fireCd, 0.6f); }   // hotter → recasts sooner
            else if (_bossPatPending == 4) { _bossPestCd = Mathf.Lerp(20f, 12f, _bossHeat); _goblinBufferCd = Mathf.Lerp(15f, 10f, _bossHeat); _fireCd = Mathf.Max(_fireCd, 0.8f); }   // pestilence 20→12s (goblin)
            else if (_bossPatPending == 5) { _bossStompCd = Mathf.Lerp(10f, 6f, _bossHeat); _fireCd = Mathf.Max(_fireCd, 0.8f); }   // stomp 10→6s
            else if (_bossPatPending == 6) { _bossRockCd = Mathf.Lerp(12f, 8f, _bossHeat); _fireCd = Mathf.Max(_fireCd, 0.8f); }    // rock throw 12→8s (orc)
            else if (_bossPatPending == 7) { _bossMineCd = Mathf.Lerp(20f, 12f, _bossHeat); _goblinBufferCd = Mathf.Lerp(15f, 10f, _bossHeat); _fireCd = Mathf.Max(_fireCd, 0.8f); }   // mines 20→12s (goblin)
            else _fireCd = _fireEvery * Mathf.Lerp(1f, 0.55f, _bossHeat) * Pace;
            return;
        }

        bool hollow = _type == "boss";   // the new abilities belong to THE HOLLOW MOON only (not the mini-boss)
        // (PHASE 2) TRIPLE CHARGE — every 25% of his pool he runs three back-to-back charges, each at the nearest witch
        // he did NOT just hit. He stays vulnerable throughout; this is his pressure tool, not a safe window.
        if (hollow && BossPhase == 2 && _tripleLeft > 0 && _dashT <= 0f)
        {
            _tripleLeft--;
            var tgt = NextTripleTarget();
            var cd = tgt - GlobalPosition; cd.Y = 0f;
            cd = cd.LengthSquared() > 0.01f ? cd.Normalized() : _bossFlatDir;
            _dashDir = cd;
            BeginBossCharge(8, 0.85f, cd, cd, DashDist, enraged);   // shorter tell than phase 1 — he's faster now
            return;
        }
        // HEAD-DOWN CHARGE — armed every time the coven takes another 20% of his pool off him (see NoteChargeThreshold).
        // Outranks everything else: it's the punish for burning him down. 30u of shoulder, dodgeable if you read the lane.
        if (hollow && BossPhase == 1 && _dashArmed && _dashT <= 0f)
        {
            _dashArmed = false;
            var cd = to; cd.Y = 0f;
            cd = cd.LengthSquared() > 0.01f ? cd.Normalized() : _bossFlatDir;
            _dashDir = cd;
            BeginBossCharge(8, 1.15f, cd, cd, DashDist, enraged);
            return;
        }
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

    // ================= THE HOLLOW MOON: authored attack animation + procedural gestures + hand-glow telegraph =================
    // Runs identically on the host and on client proxies (RemoteBossTell feeds it the same pattern + duration), so co-op
    // players see the same wind-up. The hand glow lights the moment a wind-up starts and holds until the attack resolves —
    // it IS part of the telegraph, on top of the existing danger lanes/rings.
    private const float AnimFirePoint = 0.72f;   // the volley lands ~72% into its clip; the remainder plays as follow-through
    private float _atkTail = 0.3f;               // how long that follow-through lasts for the clip currently playing
    private float _atkHoldT = 0f;                // >0 while the follow-through runs (boss has fired, glow still lit)
    private float _relT = 0f;                    // 0→1 release ramp for the PROCEDURAL gestures (rock hurl / mine signal)
    private bool _hollowAnim => _type == "boss" && _creature != null && _creature.IsAuthoredHollow;

    // Which authored clip covers which pattern. Only 7 (mines) is absent — that one is the procedural arm-raise/signal
    // gesture in BossGestureMod. The rock throw shares pestilence's grip-and-throw clip: a procedural overhead lift read
    // badly next to the authored animation, so the boulder now just materialises in front of him and the clip hurls it.
    private static string BossClipFor(int pat) => pat switch
    {
        0 => "cast6", 2 => "cast6",   // aimed volley + heavy burst
        1 => "cast1", 3 => "cast1",   // radial burst + close nova
        4 => "gripthrow",             // pestilence
        6 => "gripthrow",             // rock throw — same grab-and-hurl motion, different payload
        5 => "stomp",
        8 => "charge",
        _ => null,
    };

    private void BeginBossAnim(int pat, float dur)
    {
        // (RETROFIT) the MINI-BOSS rides the ogre body, which has the withered king's mage cast grafted onto it — every
        // pattern it can pick (0-3) is a bolt volley, so the whole wind-up plays as a cast instead of a walk cycle.
        // net:false — BroadcastBossTell already replays this exact call on every proxy, so a second RPC is waste.
        if (!_hollowAnim) { BeginCastAnim("cast4", dur, net: false); return; }
        _creature.SetHandGlow(1f);
        _atkHoldT = 0f; _relT = 0f;
        _creature.SetGesture(0f, 0f);
        _creature.ShowHeldRock(false);
        string clip = BossClipFor(pat);
        if (clip == null)
        {
            // Procedural gesture (mines): the arms are posed on top of LOCOMOTION. Drop any attack clip still holding its
            // last frame first, or the gesture layers onto a frozen cast pose and he looks broken.
            _creature.BossEndClip();
            _atkTail = 0.28f;
            return;
        }
        float len = _creature.BossClipLength(clip);
        float sp = len > 0.05f ? Mathf.Clamp(len * AnimFirePoint / Mathf.Max(0.05f, dur), 0.3f, 3.5f) : 1f;
        _creature.BossPlay(clip, sp);
        _atkTail = len > 0.05f ? Mathf.Min(0.9f, len * (1f - AnimFirePoint) / sp) : 0.3f;
    }

    // the wind-up finished and the attack went off — start the release ramp + the follow-through window
    private void FireBossAnim(int pat)
    {
        if (!_hollowAnim) return;
        _atkHoldT = Mathf.Max(0.18f, _atkTail);
        _relT = 0f;
        if (pat == 6) _creature.ShowHeldRock(false);   // the boulder is now a real BossRock in flight
    }

    private void EndBossAnim()
    {
        if (!_hollowAnim) return;
        _atkHoldT = 0f; _relT = 0f;
        _creature.SetHandGlow(0f);
        _creature.SetGesture(0f, 0f);
        _creature.ShowHeldRock(false);
        _creature.BossEndClip();
    }

    // per-frame gesture/glow drive — called from BossFire (host) and from the Remote proxy branch (clients)
    private void UpdateBossAnim(float dt)
    {
        if (!_hollowAnim) return;
        if (_bossCharging)
        {
            float p = _bossChargeDur > 0.01f ? Mathf.Clamp(1f - _bossChargeT / _bossChargeDur, 0f, 1f) : 0f;
            if (_bossPatPending == 6)        // ROCK THROW: the boulder tears itself up in front of him as the clip grips
                _creature.ShowHeldRock(p > 0.10f, Mathf.Clamp((p - 0.10f) / 0.55f, 0.05f, 1f));
            else if (_bossPatPending == 7)   // MINES: one arm goes straight up and waits there
                _creature.SetGesture(Mathf.Clamp(p / 0.55f, 0f, 1f), 0f);
            return;
        }
        if (_atkHoldT <= 0f) return;
        _atkHoldT -= dt;
        _relT = Mathf.MoveToward(_relT, 1f, dt * 5.5f);          // snap the release through in ~0.18s
        if (_bossPatPending == 7) _creature.SetGesture(1f, _relT);   // chop the arm flat to the front: GO
        if (_atkHoldT <= 0f) EndBossAnim();
    }

    // ================= WITHERED CASTERS: authored cast animations =================
    // The caster family (caster / stunner / healer / empowerer) rides the withered-king GLB and owns cast + cast4 +
    // castcharge; the ogre-bodied bolt throwers (sieger, mini-boss) get cast4 GRAFTED onto their bigger rig, retargeted
    // to their proportions. All of them drive the same one-shot clip channel the boss uses, so the per-frame BipedLoco
    // call can't stomp the clip halfway through.
    //
    // Every cast is a WIND-UP: the clip starts, and the bolt/heal/pulse lands at the clip's release frame. The cooldown
    // is started when the wind-up begins, so this costs no DPS — it just gives the attack a readable tell.
    // The mage casts are ~2.2s clips. Squeezing one into a short wind-up by speed alone reads as a twitch, so the stretch
    // is clamped: when the wind-up is shorter than the clip comfortably allows, the clip simply plays SLOWER than
    // "release at 72%" and the bolt leaves while the arms are still rising. That still reads as a cast; 4x does not.
    private const float CastWind = 0.9f;        // default wind-up for foes that used to fire instantly
    private const float CastSpeedMax = 1.8f;    // never crush a cast faster than this
    private const float CastTail = 0.6f;        // follow-through held after the release before he walks again
    private float _castHoldT = 0f;   // >0 while a cast clip owns the body
    private float _castWindT = 0f;   // >0 while a wind-up is running; the effect fires when it reaches 0
    private int _castPend = 0;       // what the wind-up owes: 1 = bolt, 2 = heal, 3 = empower pulse

    // Start `clip` stretched so its release frame lands `dur` seconds from now (dur <= 0 → play it at 1x). Returns false
    // when this body has no such clip (procedural spiders, flyers, the jungle set) — the caller then keeps firing instantly.
    // `cadence` = how long until this foe casts again. The follow-through is capped well inside it, or the clip eats the
    // whole cycle and the body never visibly returns to its walk — a healer would just slide around in a mend pose.
    private bool BeginCastAnim(string clip, float dur, float cadence = 0f, bool net = true)
    {
        if (_creature == null || !AuthBiped || !_creature.HasBipedClip(clip)) return false;
        float len = _creature.CastLength(clip);
        if (len < 0.05f) return false;
        float sp = dur > 0.05f ? Mathf.Clamp(len * AnimFirePoint / dur, 0.35f, CastSpeedMax) : 1f;
        _creature.CastPlay(clip, sp);
        float hold = dur > 0.05f ? Mathf.Min(len / sp, dur + CastTail) : len / sp;
        if (cadence > 0.05f) hold = Mathf.Min(hold, cadence * 0.7f);
        _castHoldT = hold;
        // (MP) every cast in the game funnels through here, so this is the one place the proxies need told. Cosmetic
        // only — the bolt/heal/curse itself is host-authoritative and already synced by its own path.
        if (net && !Remote) Game.I?.NetMgr?.BroadcastEnemyCast(NetId, CastIdx(clip), dur, cadence);
        return true;
    }

    // Clip identity on the wire. Kept as a tiny int so a horde of casters doesn't stream strings every 2 seconds.
    private static int CastIdx(string clip) => clip == "cast4" ? 1 : clip == "castcharge" ? 2 : 0;
    private static string CastClipOf(int idx) => idx == 1 ? "cast4" : idx == 2 ? "castcharge" : "cast";

    // client proxy: pose to the cast the host just started. `_castPend` deliberately stays 0 — a proxy animates,
    // it never fires anything.
    public void RemoteCast(int idx, float dur, float cadence)
    {
        if (!Remote) return;
        BeginCastAnim(CastClipOf(idx), dur, cadence, net: false);
    }

    // Begin a telegraphed cast: play the clip and owe `what` when the release frame arrives. Falls back to firing now.
    private void BeginCast(string clip, float wind, int what, float cadence)
    {
        if (BeginCastAnim(clip, wind, cadence)) { _castWindT = wind; _castPend = what; }
        else ReleaseCast(what);
    }

    private void ReleaseCast(int what)
    {
        switch (what)
        {
            case 1: FireAt(Game.I?.Player, _boltSpeed, _boltDmg, _boltRadius); break;
            case 2: HealPulse(); break;
            case 3: TotemPulse(); break;
        }
    }

    private void UpdateCastAnim(float dt)
    {
        // FROZEN is a total lockout, so a cast caught mid-wind-up is LOST rather than fired out of a block of ice.
        if (FrozenT > 0f && (_castWindT > 0f || _castHoldT > 0f))
        {
            _castWindT = 0f; _castPend = 0; _castHoldT = 0f;
            _creature?.CastEnd();
            return;
        }
        if (_castWindT > 0f)
        {
            _castWindT -= dt;
            if (_castWindT <= 0f) { int w = _castPend; _castPend = 0; ReleaseCast(w); }
        }
        if (_castHoldT > 0f) { _castHoldT -= dt; if (_castHoldT <= 0f) _creature?.CastEnd(); }
    }

    private void BeginBossCharge(int pat, float dur, Vector3 aim, Vector3 flatDir, float reach, bool enraged)
    {
        _bossCharging = true; _bossChargeT = dur; _bossChargeDur = dur; _bossPatPending = pat;
        _bossAim = aim; _bossFlatDir = flatDir; _bossEnraged = enraged;
        int idx = (int)(GD.Randf() * 997f);
        BeginBossAnim(pat, dur);
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
            Game.I.SpawnPestilence(center, 6.5f, (6f + Game.I.Wave * 0.5f) * _p2Dmg, remote: false, net: true);
            if (!_hollowAnim) _creature?.FireShoulder(true);   // (legacy procedural body only) left zombie goblin casts
            Game.I.Sfx?.BossRoar(GlobalPosition);
            return;
        }
        if (pat == 5)        // AoE stomp: shockwave around the boss, stuns/hurts witches in range
        {
            float r = 8f, dmg = (14f + Game.I.Wave * 0.9f) * _p2Dmg;
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
            Game.I.SpawnBossRock(from, target, (20f + Game.I.Wave * 1.1f) * _p2Dmg, remote: false, net: true);
            Game.I.Sfx?.BossRoar(GlobalPosition);
            return;
        }
        if (pat == 7)        // non-zombie goblin scatters mines around the boss
        {
            Game.I.SpawnBossMines(GlobalPosition, 4 + Game.I.WardenCount, (14f + Game.I.Wave * 0.8f) * _p2Dmg);
            if (!_hollowAnim) _creature?.FireShoulder(false);   // (legacy procedural body only) right goblin throws
            Game.I.Sfx?.BossRoar(GlobalPosition);
            return;
        }
        if (pat == 8)        // head-down charge — the wind-up is over, he goes
        {
            _dashT = DashDur; _dashHit.Clear(); _dashMoved = 0f;
            var cd = new Vector3(flatDir.X, 0f, flatDir.Z);
            _dashDir = cd.LengthSquared() > 0.01f ? cd.Normalized() : Vector3.Forward;
            Game.I.Sfx?.BossRoar(GlobalPosition);
            Game.I.VfxRing(GlobalPosition, new Color(0.95f, 0.4f, 0.2f), 5f, 0.35f);
            Game.I.Player?.CamKickExternal(0.7f);
            return;
        }
        if (pat == 3)        // close NOVA — an arcane shockwave that throws off a ring of bolts, not just the bolts
        {
            // THREE nested ground shocks racing outward at different speeds, plus arcane shards kicked up out of the
            // ground. Reads as a blast expanding THROUGH you rather than the old bare ring of bolts. Deliberately NO dome:
            // a translucent hemisphere over a boss reads as a SHIELD, which is the opposite of what a nova should say.
            float nr = Radius + 7f;
            var arc = new Color(0.62f, 0.36f, 1f);
            var hot = new Color(0.92f, 0.84f, 1f);
            Game.I.VfxRing(GlobalPosition, hot, nr * 0.45f, 0.22f);                 // hot core, snaps out first
            Game.I.VfxRing(GlobalPosition, arc, nr, 0.42f);                         // the shock itself, at the damage radius
            Game.I.VfxRing(GlobalPosition, arc.Lerp(hot, 0.4f), nr * 1.35f, 0.6f);  // the overrun, trailing past it
            Game.I.SpawnGroundSpikes(GlobalPosition, nr, 18, arc, 0.4f);            // shards torn out of the ground under him
            Game.I.NetMgr?.BroadcastVfx(0, GlobalPosition, Vector3.Zero, nr, 0.42f, arc);   // allies see the same shock
            Game.I.Player?.CamKickExternal(0.55f);
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

        if (pat == 8)   // CHARGE: one WIDE lane down his whole run — you dodge by leaving the corridor, so it must read as a corridor
        {
            var lane = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(7.2f, 0.05f, reach) }, MaterialOverride = laneMat };
            lane.Rotation = new Vector3(0, Mathf.Atan2(flatDir.X, flatDir.Z), 0);
            lane.Position = new Vector3(flatDir.X * reach * 0.5f, groundY, flatDir.Z * reach * 0.5f);
            lane.Transparency = 0.8f;
            AddChild(lane);
            lane.CreateTween().TweenProperty(lane, "transparency", 0.06f, dur).SetEase(Tween.EaseType.In);
            var cf = lane.CreateTween(); cf.TweenInterval(dur + 0.12f);
            cf.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(lane)) lane.QueueFree(); }));
            return;
        }
        if (pat == 4) { Ring(new Vector3(flatDir.X * reach, 0f, flatDir.Z * reach), 6.5f); return; }   // pestilence landing circle
        if (pat == 5) { Ring(Vector3.Zero, 8f); return; }                                              // stomp ring around the boss
        if (pat == 6)   // rock throw: red landing circle + a boulder forming above the boss's hands (wind-up anim)
        {
            Ring(new Vector3(flatDir.X * reach, 0f, flatDir.Z * reach), 3f);
            if (_hollowAnim) return;   // the authored boss cradles a REAL boulder between his raised hands — no stand-in needed
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
            if (_hollowAnim) return;   // his own arcane hand glow is the caster tell now — the goblin's green charge is gone
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
    private static readonly string[] _bossLinesCharge = { "ENOUGH!", "OUT OF MY WAY!", "I WILL BURY YOU!", "STAND STILL!" };
    private string[] BossLines(int pat) => pat switch { 1 => _bossLinesRing, 3 => _bossLinesNova, 4 => _bossLinesPest, 5 => _bossLinesStomp, 6 => _bossLinesRock, 7 => _bossLinesMine, 8 => _bossLinesCharge, _ => _bossLinesAimed };

    private void SayBossLine(int pat, int idx)
    {
        var lines = BossLines(pat);
        SayBossVox(lines[((idx % lines.Length) + lines.Length) % lines.Length], new Color(1f, 0.82f, 0.2f), 1.0f);
    }

    private float _critVoxCd = 0f;
    // (REWORK) the shoulder goblins are gone with the old procedural body — HE takes every hit and HE does all the talking.
    // Head and both shoulders are still crit zones; they just no longer have their own little voices.
    private static readonly string[] _critHeadLines = { "NOT MY SKULL!", "MY HEAD!", "ARGH — MY MOONS!", "MY FACE, WITCH!" };
    private static readonly string[] _critShoulderLines = { "MY SHOULDER!", "AAARGH!", "YOU'LL PAY FOR THAT!", "CURSE YOU, WITCH!" };
    private static readonly string[] _bossDeathLines = { "THE OTHER MOONS WILL TAKE YOU...", "THE OTHER MOONS... WILL AVENGE ME...", "YOU CANNOT KILL... ALL OF US...", "THE MOONS... ARE MANY..." };
    private static readonly string[] _bossTaunts = { "I WANT THE WITCHES' HEADS ON A STAKE!", "BURN THE WITCHES!", "BRING ME THEIR BONES!", "SWARM THEM, MY CHILDREN!", "TEAR THEM APART!", "NO MERCY FOR THE COVEN!", "DROWN THEM IN NUMBERS!" };
    public void Taunt() { SayBossVox(_bossTaunts[GD.RandRange(0, _bossTaunts.Length - 1)], new Color(1f, 0.6f, 0.2f), 1.5f); }

    // which high zone got hit: 0 none, 1 head, 2 left shoulder, 3 right shoulder
    public int CritZone(Vector3 hitPos)
    {
        if (!IsBoss || _type != "boss") return 0;
        if (hitPos.Y - GlobalPosition.Y < Radius * 0.7f) return 0;
        var lp = _creature != null ? _creature.ToLocal(hitPos) : ToLocal(hitPos);
        if (lp.X < -Radius * 0.4f) return 2;
        if (lp.X > Radius * 0.4f) return 3;
        return 1;
    }
    // a crit landed high — the boss yelps (throttled)
    public void CritHitReact(Vector3 hitPos)
    {
        if (_critVoxCd > 0f) return;
        int z = CritZone(hitPos);
        if (z == 0) return;
        _critVoxCd = 2.2f;
        var lines = z == 1 ? _critHeadLines : _critShoulderLines;
        SayBossVox(lines[GD.RandRange(0, lines.Length - 1)], new Color(1f, 0.4f, 0.35f), 1.0f);
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
        _bossPatPending = pat; _bossFlatDir = flat;   // (FIX) the proxy needs the pattern for the HUD label AND the anim/gesture
        BeginBossAnim(pat, dur);                      // …so clients see the same clip, gesture and hand glow as the host
    }

    // client proxy: mirror the host's phase/untouchable state so the aura, the unsteady walk and the HUD bar all match
    public void RemoteBossPhase2(int phase, bool invuln)
    {
        bool wasInvuln = Invuln;
        Invuln = invuln;
        if (phase >= 2 && BossPhase < 2) { BossPhase = 2; _creature?.BossDie(); }
        // the aura ignites the moment he becomes touchable again — i.e. once he's actually back on his feet, matching
        // the host's stage 2 -> 3 transition (the host has no way to stream that sub-state, and doesn't need to)
        if (BossPhase == 2 && wasInvuln && !invuln) { _creature?.BossEndClip(); _creature?.SetPhase2(); }
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
            var np = GlobalPosition + AvoidBlockers(want) * Speed * _catchMul * spdMul * dt;   // (NEW) steer around trunks
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
            _zapTele = 1.05f; _fireCd = _fireEvery * Pace;
            BeginCastAnim("castcharge", _zapTele, _fireCd);   // (NEW) the withered stunner's charged-spell clip IS the telegraph; the bolt lands on its release frame
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
            if (dist < Radius + 1.8f && _touchCd <= 0f && VertReach()) { HitTarget(Dmg); _touchCd = 0.7f * Pace; }
            if (_diveT <= 0f || GlobalPosition.Y <= _tgt.Y + 0.7f) { _diving = false; _diveCd = 2.6f * Pace; }   // climb back out
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
                Game.I.Sfx?.Incoming(GlobalPosition);                            // (NEW) audible swoop warning (HUD shows an arrow/bracket too)
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
            var np = GlobalPosition + AvoidBlockers(want) * Speed * _catchMul * spdMul * dt;   // (NEW) steer around trunks
            GlobalPosition = ClampArena(np);
        }
        if (_hexTele > 0f) { _hexTele -= dt; if (_hexTele <= 0f) HexStrike(); }
        else if (_hexCd <= 0f && dist < _range)
        {
            _hexTele = 1.0f; _hexCd = _fireEvery * Pace;
            BeginCastAnim("cast", _hexTele, _hexCd);   // (NEW) same mage cast the healer/empowerer use — the curse lands on its release frame
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
            GlobalPosition = ClampArena(GlobalPosition + AvoidBlockers(want) * Speed * _catchMul * spdMul * dt);   // (NEW) steer around trunks
        if (_hexTele > 0f) { _hexTele -= dt; if (_hexTele <= 0f) SapStrike(); }
        else if (_hexCd <= 0f && dist < _range)
        {
            _hexTele = 1.1f; _hexCd = _fireEvery * Pace;
            BeginCastAnim("cast", _hexTele, _hexCd);   // (NEW) the dispel pulse rides the same mage cast
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
        if (_totemTick <= 0f && _castWindT <= 0f)
        {
            _totemTick = 1.6f;   // (SLOWER) was 0.9 — the pulse now rides a real cast animation, so give the cast room to read
            BeginCast("cast", 0.9f, 3, _totemTick);   // (NEW) the mage cast throws the empower pulse out
        }
    }

    private void TotemPulse()
    {
        var gc = new Color(1f, 0.8f, 0.35f);
        Game.I.VfxRing(GlobalPosition, gc, 14f, 0.5f);                            // visible empower pulse (shows its radius)
        Game.I.NetMgr?.BroadcastVfx(0, GlobalPosition, Vector3.Up, 14f, 0.5f, gc);
        foreach (var e in Game.I.Enemies.ToArray())
        {
            if (e == null || e == this || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
            if (e._behav == EBehav.Totem) continue;
            if (GlobalPosition.DistanceTo(e.GlobalPosition) < 14f) e.ApplyHaste(1.9f);   // (was 1.1) outlasts the slower pulse cadence, so the aura stays unbroken
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
            var np = GlobalPosition + AvoidBlockers(to) * Speed * _catchMul * spdMul * dt;   // (NEW) steer around trunks
            GlobalPosition = ClampArena(np);
        }
        if (dist < Radius + 2.4f && VertReach()) Explode(p);   // (NEW) don't self-detonate on a player who's flown out of reach above
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
        // (DOOM WALKER) a walker freed mid-errand — despawn, relocate, run end — must give its slot back, or the static
        // counter leaks and walkers stop spawning for the rest of the session.
        if (_doomWalking) { _doomWalking = false; _doomWalkersLive = Mathf.Max(0, _doomWalkersLive - 1); }
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
        // (PUPPET) a turned caster/archer fires its OWN projectile at its OWN ally — the shot just changes sides, and the
        // curse tint is the tell that it isn't coming for you. This is the half of puppetry that isn't free: EnemyBolt
        // had no owner and no enemy collision, so both live on the bolt itself.
        var tint = _tgtIsEnemy ? DamageTypes.Col(DamageType.Curse).Lerp(Colors.White, 0.2f) : Col.Lerp(new Color(1, 1, 1), 0.25f);
        var origin = GlobalPosition + new Vector3(0, Radius * 0.6f, 0) + vel.Normalized() * (Radius + 0.5f);
        var b = new EnemyBolt { Vel = vel, Dmg = dmg, Radius = radius, Tint = tint, HitsEnemies = _tgtIsEnemy, OwnerPeer = _puppetOwner, Shooter = this };
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

    // (NEW) obstacle-avoidance steering: bend a desired horizontal move direction around nearby tree/pillar blockers so
    // enemies slip AROUND trunks instead of grinding straight into them (the ClampArena push-out alone just cancels their
    // forward progress and they stick). Cheap — only blockers within a short look-ahead of the travel direction matter.
    // Especially important in the dense jungle for ranged foes that would otherwise wedge on a tree and never reach you.
    private float _avoidSign = 0f;   // committed swerve side while routing around an obstacle (0 = path is clear)
    private Vector3 _stuckRef;       // position checkpoint for the anti-stuck flip
    private float _stuckChk = 0f;

    // Smarter local steering: instead of summing EVERY nearby trunk (which jitters and jams in dense jungle), find the
    // SINGLE most-threatening obstacle directly in our path — a tree/pillar (Blocker) or a structure wall (Deck) — and
    // commit to going around ONE side of it. The committed side (_avoidSign) persists so we don't oscillate, and the
    // per-frame anti-stuck flip (in the mover) reverses it if we end up wedged, letting foes escape local minima.
    private Vector3 AvoidBlockers(Vector3 dir)
    {
        if (Game.I == null) return dir;
        dir.Y = 0f;
        if (dir.LengthSquared() < 0.0001f) { _avoidSign = 0f; return dir; }
        dir = dir.Normalized();
        var gp = GlobalPosition;
        float feel = Radius + 4.0f;                              // how far ahead we look

        float bestThreat = 0.12f; Vector3 bestAway = Vector3.Zero; float bestGap = 0f; bool found = false;
        void Consider(float bx, float bz, float brad)
        {
            float ox = gp.X - bx, oz = gp.Z - bz;
            if (ox > feel + 2f || ox < -(feel + 2f) || oz > feel + 2f || oz < -(feel + 2f)) return;   // cheap reject before sqrt
            float d = Mathf.Sqrt(ox * ox + oz * oz);
            float gap = d - (brad + Radius * 0.9f);
            if (gap > feel) return;                             // too far to matter
            Vector3 away = d > 0.001f ? new Vector3(ox / d, 0f, oz / d) : -dir;   // obstacle → us
            float ahead = dir.Dot(-away);                       // 1 = obstacle dead ahead of our travel
            if (ahead <= 0.1f) return;                           // beside/behind us — no need to swerve
            float threat = ahead * Mathf.Clamp(1f - gap / feel, 0f, 1f);
            if (threat > bestThreat) { bestThreat = threat; bestAway = away; bestGap = gap; found = true; }
        }
        var bl = Game.I.Blockers;
        var nb = Game.I.QueryBlockers(gp.X, gp.Z, feel + 2f);        // (PERF) only nearby trees/rocks, not the whole jungle list
        for (int i = 0; i < nb.Count; i++) { var b = bl[nb[i]]; Consider(b.Pos.X, b.Pos.Z, b.Radius); }
        var wb = Game.I.WallBlockers;                                // (NEW) route around frost walls too (small list — stays linear)
        for (int i = 0; i < wb.Count; i++) Consider(wb[i].Pos.X, wb[i].Pos.Z, wb[i].Radius);
        // are we ON a ramp/staircase? then DON'T steer around the wall it climbs — let us ride the stairs up onto the deck
        bool onRamp = false;
        var rmps = Game.I.Ramps;
        for (int i = 0; i < rmps.Count; i++)
        {
            var r = rmps[i];
            if (Mathf.Abs(gp.X - r.Center.X) <= r.Half.X + Radius + 0.5f && Mathf.Abs(gp.Z - r.Center.Z) <= r.Half.Y + Radius + 0.5f) { onRamp = true; break; }
        }
        // (NEW) target is up on a structure? then the keep isn't an obstacle, it's the DESTINATION — orbiting it was what
        // left half the horde circling a keep in a clump instead of going up. Only still-taller decks get avoided.
        bool seekHigh = _tgt.Y > gp.Y - Radius + 1.2f;
        var dk = Game.I.Decks;
        var nd = Game.I.QueryDecks(gp.X, gp.Z, feel + 2f);          // (PERF) only nearby structure walls
        for (int i = 0; i < nd.Count; i++)                     // steer around structure walls too (as their bounding circle)
        {
            var d = dk[nd[i]];
            if (d.TopY < 1.8f || d.LowPad || gp.Y >= d.TopY - 0.6f || onRamp) continue;   // low pads (pedestals) = step up; already climbing/on a ramp = don't get shoved off
            if (seekHigh && d.TopY <= _tgt.Y + 2f) continue;   // (NEW) this is the thing we're trying to climb — walk into it
            Consider(d.Center.X, d.Center.Z, Mathf.Max(d.Half.X, d.Half.Y));
        }

        if (!found) { _avoidSign = 0f; return dir; }

        Vector3 tangent = new Vector3(-dir.Z, 0f, dir.X);
        if (_avoidSign == 0f) _avoidSign = tangent.Dot(bestAway) < 0f ? 1f : -1f;   // commit to the side we're already offset toward
        if (_avoidSign < 0f) tangent = -tangent;
        float w = Mathf.Clamp(1f - bestGap / feel, 0f, 1f);
        Vector3 steer = tangent * (0.6f + 1.7f * w) + bestAway * (0.45f * w);   // mostly around, a little push-off
        return (dir + steer).Normalized();
    }

    // Authoritative SOLID collision + arena bound, applied in EVERY mode (not just Expedition): keeps foes inside the
    // play area and physically OUT of trees/pillars (Blockers) and structure walls (Decks) — no more ghosting through.
    // Low walkable pads (TopY < 1.8) and foes already on top of a platform are exempt so ramps still work.
    private Vector3 ClampArena(Vector3 p)
    {
        float keepY = p.Y;
        if (Game.I == null) return p;
        var pl = Game.I.Player;
        Vector3 ctr = pl != null ? pl.GlobalPosition : Vector3.Zero;
        var off = new Vector2(p.X - ctr.X, p.Z - ctr.Z);
        if (off.Length() > 85f) { off = off.Normalized() * 85f; p.X = ctr.X + off.X; p.Z = ctr.Z + off.Y; }
        // (FIX) also keep foes inside the bounded overworld disc — the 85u leash is player-relative, so near the edge a foe
        // could otherwise drift out past the cliff wall. No-op outside the overworld (maze/sky/expedition are their own arenas).
        var cw = Game.I.ClampToWorld(new Vector3(p.X, keepY, p.Z), 8f); p.X = cw.X; p.Z = cw.Z;
        // trees / cover pillars — FULL-radius push-out (was 0.6×, which let them sink deep into trunks)
        var bl = Game.I.Blockers;
        var nb = Game.I.QueryBlockers(p.X, p.Z, 5.5f);   // (PERF) only nearby trees/rocks
        for (int i = 0; i < nb.Count; i++)
        {
            var b = bl[nb[i]];
            float ox = p.X - b.Pos.X, oz = p.Z - b.Pos.Z;
            if (ox > 5f || ox < -5f || oz > 5f || oz < -5f) continue;   // cheap AABB reject before the sqrt (most trees are far)
            float dd = Mathf.Sqrt(ox * ox + oz * oz);
            float minD = b.Radius + Radius * 0.9f;
            if (dd < minD) { float k = minD / Mathf.Max(dd, 0.001f); p.X = b.Pos.X + ox * k; p.Z = b.Pos.Z + oz * k; }
        }
        // frost walls — SOLID: same full-radius push-out so foes can't ghost through, only route around the ends (NEW)
        var wb = Game.I.WallBlockers;
        for (int i = 0; i < wb.Count; i++)
        {
            var b = wb[i];
            float ox = p.X - b.Pos.X, oz = p.Z - b.Pos.Z;
            if (ox > 5f || ox < -5f || oz > 5f || oz < -5f) continue;
            float dd = Mathf.Sqrt(ox * ox + oz * oz);
            float minD = b.Radius + Radius * 0.9f;
            if (dd < minD) { float k = minD / Mathf.Max(dd, 0.001f); p.X = b.Pos.X + ox * k; p.Z = b.Pos.Z + oz * k; }
        }
        // structure walls (forts / ruins / maze) — box push-out along the nearest face
        var dk = Game.I.Decks;
        var nd = Game.I.QueryDecks(p.X, p.Z, 5.5f);   // (PERF) only nearby structure walls
        for (int i = 0; i < nd.Count; i++)
        {
            var d = dk[nd[i]];
            if (d.TopY < 1.8f || d.LowPad) continue;      // low walkable pad (incl. pedestals on raised terrain) → step up any side, never a wall push-out
            if (keepY >= d.TopY - 0.6f) continue;         // already on/above the top (climbed a ramp / flyer)
            if (d.Floating && keepY < d.TopY - 4.0f) continue;   // (NEW) sky island: only a thin solid rim — don't trap flyers in an invisible column below it
            float ex = d.Half.X + Radius, ez = d.Half.Y + Radius;
            float dx = p.X - d.Center.X, dz = p.Z - d.Center.Z;
            if (Mathf.Abs(dx) < ex && Mathf.Abs(dz) < ez)
            {
                if (ex - Mathf.Abs(dx) < ez - Mathf.Abs(dz)) p.X = d.Center.X + Mathf.Sign(dx) * ex;
                else p.Z = d.Center.Z + Mathf.Sign(dz) * ez;
            }
        }
        p.Y = keepY;
        return p;
    }

    private void UpdateStatusVisual(float dt)
    {
        if (_flash > 0) { _mat.EmissionEnergyMultiplier = 6f; _emitDirty = true; }
        else
        {
            Color sc = Col; float en = _baseEnergy;
            bool threat = Diving || SpecialCharging || Telegraphing;   // (NEW) an imminent threat — swoop / grab-charge / winding up a shot
            bool rotv = Remote ? _rotShow : (_bleedT > 0f && _bleedRot);
            if (threat) { sc = new Color(1f, 0.05f, 0.05f); en = 4.8f + Mathf.Sin(Time.GetTicksMsec() * 0.02f) * 2.4f; }   // (NEW) BRIGHT pulsing red body highlight — hard to miss; wins over every other tint
            else if (rotv) { sc = sc.Lerp(DamageTypes.Col(DamageType.Blood), 0.78f); en = 3.0f + Mathf.Sin(Time.GetTicksMsec() * 0.012f) * 1.4f; }   // pulsing crimson rot
            else if (_chargeT > 0f || _hexTele > 0f) { sc = sc.Lerp(new Color(1f, 1f, 0.8f), 0.6f); en = 2.5f; }   // sieger/hexer wind-up glow
            else if (RootT > 0) { sc = sc.Lerp(DamageTypes.Col(DamageType.Nature), 0.6f); }
            else if (SlowT > 0) { sc = sc.Lerp(DamageTypes.Col(DamageType.Frost), 0.65f); en = _baseEnergy * 0.85f; }
            if (Cursed && !threat) { sc = sc.Lerp(DamageTypes.Col(DamageType.Curse), 0.72f); en = 2.4f + Mathf.Sin(Time.GetTicksMsec() * 0.009f) * 1.3f; }   // (NEW) pulsing curse glow (overrides other tints while cursed — but not the red threat highlight)
            // (PERF) only WRITE the material when it actually changed — an idle enemy (no status) has constant sc/en,
            // so this stops dirtying ~50 materials/frame for nothing. Animated statuses still update every frame.
            if (_emitDirty || !Mathf.IsEqualApprox(en, _lastEmitEn) || !sc.IsEqualApprox(_lastEmit))
            {
                _mat.Emission = sc;
                _mat.EmissionEnergyMultiplier = en;
                if (_light != null) _light.LightColor = sc;
                _lastEmit = sc; _lastEmitEn = en; _emitDirty = false;
            }
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

        // (NEW) cursed: a spinning curse ring at the feet + an overhead counter
        // (DOOM) a foe carrying a bank shows the same furniture — the ring reads "she has hold of this one" and the label
        // carries the READ the mechanic lives or dies on: how big the bomb is, and how long until it goes. Without a
        // legible countdown the execute lands as a random death instead of a payoff you watched coming.
        bool cursed = Cursed || Doomed;
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
            // (DOOM) no overhead number — the bank is a portion of the HEALTH BAR and is drawn there (Hud.DrawEnemyBars).
            // The foot ring stays, because "she has hold of this one" is worth reading from any angle.
            if (Doomed) { if (_curseLabel != null) { _curseLabel.QueueFree(); _curseLabel = null; } }
            else _curseLabel.Text = "☠" + Mathf.Max(1, Mathf.RoundToInt(CurseStacks));   // legacy curse-stack counter, until the old curse abilities are retired
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

    private ulong _winceNextMs = 0;   // (WINCE) per-enemy rate-limit so a horde doesn't flinch in lockstep (timestamp — no per-frame timer needed)
    public void Hurt(float dmg, DamageType type = DamageType.Lunar, bool fromCombo = false, bool crit = false, bool direct = false)
    {
        if (Dead) return;
        if (_doomWalking) return;   // (DOOM WALKER) already a corpse — it can't be killed twice, credited twice, or knocked off its errand
        // (NEW PHALANX) archers are untouchable for exactly as long as their bearer's ward stands — the ward IS the
        // fight. Bounce the shot with a spark instead of a number so it reads as "blocked", not "missed".
        if (IsArcher && WardGuarded) { WardDeflect(); return; }
        // (HOLLOW MOON PHASE 2) untouchable while he's getting back up and for the whole vortex spin. Bounce the hit with
        // a spark instead of a number so it reads as BLOCKED, not missed — the HUD bar says the same thing.
        if (Invuln) { WardDeflect(); return; }
        // (WINCE) a DIRECT hit (a landed bolt/melee — NOT an AoE field or a DoT tick) makes an authored biped flinch. Purely
        // cosmetic (no movement/AI effect), a random variant each time, and rate-limited per enemy so hordes don't sync up.
        if (direct && dmg > 0f && AuthBiped)
        {
            ulong nowMs = Time.GetTicksMsec();
            if (nowMs >= _winceNextMs) { _creature.Wince((int)(GD.Randi() % 4)); _winceNextMs = nowMs + (ulong)(450 + GD.Randi() % 500); }
        }
        // (ECLIPSE) EVERY lunar hit the local eclipsed Lunar witch lands detonates a shadow-nova. Hooked HERE (not OnHitCore)
        // so it catches ALL her lunar damage — bolts, charged, finishers, mods, fields, projectiles — and runs on HER machine
        // (host or client: e.Hurt is called on the attacker's side), so it's MP-correct. The busy flag stops recursion.
        {
            var lp = Game.I?.Player;
            if (lp != null && lp.EclipseOn && !lp.EclipseNovaBusy && type == DamageType.Lunar)
                lp.EclipseNovaAt(GlobalPosition);
        }
        if (Remote)
        {
            // a client landed a hit: the host owns this enemy, so route the damage there (but give this machine local feedback)
            Game.I.NetMgr?.ReportHit(NetId, dmg, (int)type, crit);
            if (Game.I.DmgNumbers) { _popAccum += dmg; _popCol = DamageTypes.Col(type); if (crit) _popCrit = true; _flash = 0.12f; }
            HitFeedback(crit);
            return;
        }
        if (IsGoblin && Game.I.GoblinTime < 0f) Game.I.GoblinTime = 12f;   // chase clock starts on first strike
        _lastAttackerPeer = Game.I.AttackerPeer;   // (NEW) credit this damage's dealer (host's own = LocalPeer; a client's routed hit = the reporter)
        var pl = Game.I.Player;
        if (pl != null) dmg *= pl.LunarNightMul(type);   // Lunar Witch: ALL lunar damage waxes stronger at night
        _lastType = type; _lastCombo = fromCombo;
        float dealt = dmg * MarkAmp;
        if ((CurseT > 0f || Doomed) && (type == _curseBonusType || (int)type == _curseBonusType2)) dealt *= _curseBonusMul;   // (NEW) cursed foes take extra from the curse-bonus type(s) — Curse by default; Cursebrand adds a 2nd. (DOOM) a DOOMED foe counts too, which is what keeps Virulence/Torment/Doombrand live now that strings are gone
        if (_armorDR > 0f && !crit) dealt *= (1f - _armorDR);                 // armored: crits punch through
        // (NEW PHALANX) every point you land on the bearer feeds the ward pool first — its own HP is untouchable until
        // the barrier falls. This is the tanky "break the shield" phase; crits are still your best tool against it.
        if (IsPhalanx && _wardHp > 0f)
        {
            _wardHp -= dealt;
            if (Game.I.DmgNumbers) { _popAccum += dealt; _popCol = new Color(0.62f, 0.45f, 1f); if (crit) _popCrit = true; }
            _flash = 0.12f;
            HitFeedback(crit);
            if (_lastAttackerPeer != 0) _damagers.Add(_lastAttackerPeer);   // chipping the ward still earns you a share of the kill
            Game.I.NoteEnemyDamage(dealt);
            if (_wardHp <= 0f) BreakWard();
            return;
        }
        if (_shield > 0f) { float s = Mathf.Min(_shield, dealt); _shield -= s; dealt -= s; }   // shielded soak
        // (REMOVED the frozen "blue bank" — frozen foes now take NORMAL damage; a charged-RMB spear SHATTERS them for a flat burst + execute, no banking step)
        Hp -= dealt;
        NoteChargeThreshold();   // (HOLLOW MOON) crossing another 20% of his pool arms the head-down charge
        if (dealt > 0f && _lastAttackerPeer != 0) _damagers.Add(_lastAttackerPeer);   // (NEW) record every damage contributor → all earn a soul when this foe dies
        DamageInvestigate();   // (NEW) ANY damage (beam / AoE / DoT, not just projectiles) makes an idle zombie investigate the source
        Game.I.NoteEnemyDamage(dealt);   // (NEW) feeds the boss-wave DPS director + heat
        // (EFFIGY) a share of everything she does to anything ELSE is banked on the doll's host. Gated to damage this
        // player actually dealt, and tagged at the deepest generation so the feed can never seed a detonation chain.
        // NOTE: host-side and single-player-attributed for now — per-peer effigies land with the Doom MP pass.
        if (dealt > 0.5f && !_doomGuard && Game.I.Player is Player efp && efp.EffigyT > 0f && efp.EffigyTgt != null
            && efp.EffigyTgt != this && !efp.EffigyTgt.Dead && GodotObject.IsInstanceValid(efp.EffigyTgt)
            && _lastAttackerPeer == Game.I.LocalPeer)
            efp.EffigyTgt.AddDoom(dealt * efp.EffigyShare, 0, DoomMaxGen);
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
        if (crit && _climbing && Hp > 0) PeelOffWall(-_climbDir * 5f);   // (NEW) a crit shakes a wall-scaling foe loose — it falls and takes the drop
        if (Hp <= 0)
        {
            if (pl != null && pl.Ult == Player.UltKind.Eclipse && pl.UltActive) pl.OnEclipseKill(GlobalPosition);
            Die();
        }
    }

    // (NEW) owner-attributed damage — DoT ticks run on the HOST but may belong to a client's spell, so stamp the source
    // peer around the Hurt call. This keeps both the kill-contribution set AND the kill credit pointed at the DoT's caster.
    private void HurtFrom(long owner, float dmg, DamageType type, bool fromCombo = false, bool crit = false)
    {
        if (Game.I == null) { Hurt(dmg, type, fromCombo, crit); return; }
        long prev = Game.I.AttackerPeer;
        Game.I.AttackerPeer = owner != 0 ? owner : Game.I.LocalPeer;
        Hurt(dmg, type, fromCombo, crit);
        Game.I.AttackerPeer = prev;
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
        m |= (Mathf.Min(_livingBombStacks, 15) & 0xF) << 11;   // (REPURPOSED bits 11-14, was the dead blue ice-bar) Ember Living Bomb stacks (0-15) — for the HUD indicator on every client
        m |= (Mathf.Min((int)FreezeStacks, 63) & 0x3F) << 15;        // (NEW) freeze stacks (for the indicator)
        if (CurseT > 0f) m |= 1 << 21;                               // (NEW) cursed
        m |= (Mathf.Min((int)CurseStacks, 63) & 0x3F) << 22;         // (NEW) curse stacks (overhead counter)
        m |= (CurseGroup & 0x7) << 28;                               // (NEW) low 3 bits of the tether group (for drawing links on all machines)
        if (ArcaneMarked) m |= unchecked((int)0x80000000);           // (NEW) bit 31 (the last free bit) — arcane-marked, for the client pip + turret targeting
        return m;
    }

    public void Heal(float amt) { if (!Dead) Hp = Mathf.Min(MaxHp, Hp + amt); }

    public void Slow(float dur, float mul) { if (Remote) { Game.I.NetMgr?.ReportStatus(NetId, 1, dur, mul, 0f); return; } SlowT = Mathf.Max(SlowT, dur); SlowMul = mul; }
    public void Root(float dur) { if (Remote) { Game.I.NetMgr?.ReportStatus(NetId, 2, dur, 0f, 0f); return; } RootT = Mathf.Max(RootT, dur); }
    public void Mark(float dur, float amp, int jumps, float doom = 0f) { if (Remote) { Game.I.NetMgr?.ReportStatus(NetId, 3, dur, amp, jumps); return; } MarkT = Mathf.Max(MarkT, dur); MarkAmp = amp; MarkJumps = jumps; if (doom > 0f) _markDoom = doom; }


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

    // ---- DOOM ----------------------------------------------------------------------------------------------------
    // The HP this foe cannot be executed past in one go. 0 for everything ordinary (so an execute just kills it), and
    // the boss's next authored gate for him: phase 1 arms a head-down charge every 20% (NoteChargeThreshold), phase 2
    // arms the untouchable vortex-spin every 1/3. Punching him to the next gate hands the fight back to authored
    // content instead of deleting it — and it means the execute is never switched off, only re-aimed.
    public float DoomFloorHp()
    {
        if (_type != "boss" || Dead || MaxHp <= 0f) return 0f;
        float band = BossPhase == 1 ? 0.2f : (1f / 3f);
        int idx = Mathf.CeilToInt(Hp / MaxHp / band - 0.0001f) - 1;   // the next boundary STRICTLY below where he is now
        return Mathf.Max(0f, idx * band * MaxHp);
    }

    // Bank Doom. `gen` is the chain depth (0 = a player applied it); `owner` is the peer credited when it goes off.
    public void AddDoom(float amt, long owner = 0, int gen = 0)
    {
        if (amt <= 0f) return;
        if (Remote) { Game.I.NetMgr?.ReportStatus(NetId, 10, amt, 0f, 0f); return; }   // clients route to the host, which owns the bank
        if (Dead || Invuln) return;                                                    // untouchable windows swallow it, like every other damage source
        DoomBank += amt;
        DoomT = DoomFuse;                                    // every application refreshes the fuse — she is the one who decides when it ends
        // carry the caster's curse-bonus profile the way AddCurse does, so her "+% damage to doomed" perks actually land
        if (Game.I != null && Game.I.Player is Player dp)
        {
            _curseBonusType = dp.CurseBonusType; _curseBonusMul = dp.CurseBonusMul; _curseBonusType2 = dp.CurseBonusType2;
            _doomSpreadMul = dp.DoomSpreadMul();   // (FRAY) stamped at application so a fuse pop spreads by it too
            _doomSpreadR = dp.DoomSpreadRadius;    // …and her B column's blast reach travels with the bank the same way
        }
        _doomGen = Mathf.Max(_doomGen, gen);                 // a bank fed by a splash carries the deeper generation, so it can't restart the chain
        if (owner != 0) _doomOwner = owner;
        else if (_doomOwner == 0 && Game.I != null) _doomOwner = Game.I.AttackerPeer != 0 ? Game.I.AttackerPeer : Game.I.LocalPeer;
        if (DoomBank >= Hp - DoomFloorHp()) DetonateDoom();   // the execute: it's covered the distance, so don't make the player wait out the fuse
    }

    // (PUPPET) turn this foe on one of its own for `dur`. Bosses are never turned — they're far too authored to hand the
    // wheel to — but they can still be doomed, which is what keeps her mechanic alive in a fight with nothing to puppet.
    public void Puppet(Enemy at, float dur, long owner = 0, float doomFeed = 0f, bool finale = false)
    {
        if (at == null || at == this || at.Dead || !GodotObject.IsInstanceValid(at)) return;
        // a client's Danse Macabre / Turncoat must still turn foes — route it to the host, which owns every enemy's AI.
        // The victim travels as its NetId in a float, which is exact well past any id this game will ever mint.
        if (Remote) { Game.I.NetMgr?.ReportStatus(NetId, 11, at.NetId, dur, doomFeed); return; }
        if (Dead || IsBoss) return;
        PuppetTgt = at;
        PuppetT = Mathf.Max(PuppetT, dur);
        _puppetFeed = Mathf.Max(_puppetFeed, doomFeed);
        _puppetFinale |= finale;
        _puppetOwner = owner != 0 ? owner : (Game.I != null ? (Game.I.AttackerPeer != 0 ? Game.I.AttackerPeer : Game.I.LocalPeer) : 0);
    }

    // Let the carried blast go where it now stands, then finish dying for real.
    private void ReleaseDoomWalk()
    {
        _doomWalking = false;
        _doomWalkersLive = Mathf.Max(0, _doomWalkersLive - 1);
        float payload = _doomWalkPayload; _doomWalkPayload = 0f;
        long owner = _doomOwner;
        Vector3 at = GlobalPosition;
        PuppetT = 0f; PuppetTgt = null;
        if (payload > 0.01f)
            foreach (var o in Game.I.Enemies.ToArray())
            {
                if (o == null || o == this || o.Dead || o.Remote || !GodotObject.IsInstanceValid(o)) continue;
                if (new Vector2(o.GlobalPosition.X - at.X, o.GlobalPosition.Z - at.Z).Length() > _doomSpreadR * _doomSpreadMul + o.Radius) continue;
                o.AddDoom(payload, owner, DoomMaxGen);   // delivered at the deepest generation — arriving must not restart a chain
            }
        Game.I.SpawnGroundSigil(at, 3.4f, DamageTypes.Col(DamageType.Curse));
        Game.I.NetMgr?.BroadcastVfx(58, at, Vector3.Up, 3.6f, 0f, DamageTypes.Col(DamageType.Curse));
        Game.I.Sfx?.CurseCrush(at);
        Hp = 0f;
        Die();
    }

    // Damage one foe deals to another under her control. Public so a turned archer's BOLT routes through the same
    // attribution path as a turned brute's slash. Typed Curse deliberately: it's her doing, and it feeds her own mechanic.
    public void PuppetHurt(long owner, float dmg) => HurtFrom(owner, dmg, DamageType.Curse, false);

    // Set it off. Deals the bank (clamped so it can't punch past the floor in one go), splashes a share around, and
    // clears. `frac` is how much of the bank goes — her charged release picks 10%→100%; the fuse always passes 1.
    public void DetonateDoom(float frac = 1f, bool crit = false, float spreadMul = 1f)
    {
        if (Remote || Dead || _doomGuard || DoomBank <= 0.01f) return;
        ulong fr = Engine.GetProcessFrames();
        if (fr != _doomFrame) { _doomFrame = fr; _doomBudget = 24; _doomWarned = false; }   // per-frame ceiling on detonations…
        if (_doomBudget <= 0)
        {
            if (!_doomWarned) { _doomWarned = true; GD.PushWarning($"[perf] doom detonation budget exhausted this frame ({Game.I.Enemies.Count} enemies) — deferring the rest to the next frame"); }
            DoomT = Mathf.Max(DoomT, 0.02f);   // …and the overflow is DEFERRED, not dropped — it still all goes off, just spread over frames
            return;
        }
        _doomBudget--;
        float take = Mathf.Clamp(frac, 0f, 1f) * DoomBank;
        DoomBank -= take;
        if (DoomBank <= 0.01f) { DoomBank = 0f; DoomT = 0f; } else DoomT = DoomFuse;   // a partial release re-arms the fuse on the remainder
        int gen = _doomGen;
        long owner = _doomOwner;
        var col = DamageTypes.Col(DamageType.Curse);
        Vector3 at = GlobalPosition;
        // The splash is staged BEFORE the killing blow, because if this detonation kills, Die() hands the payload to a
        // walker instead of dropping it here — the corpse carries it into the crowd rather than wasting it on empty ground.
        float spread = Mathf.Clamp(spreadMul * _doomSpreadMul, 0.2f, 4f);
        float splash = gen < DoomMaxGen ? take * Mathf.Min(0.6f, DoomSplashFrac * spread) : 0f;
        float splashR = _doomSpreadR * spread;
        int seedCap = Game.I != null && Game.I.Player != null ? Mathf.Max(2, Game.I.Player.MaxLinks) : 6;   // her B column = how many a blast seeds
        _doomWalkPayload = splash;
        _doomGuard = true;
        HurtFrom(owner, Mathf.Min(take, Mathf.Max(0f, Hp - DoomFloorHp())), DamageType.Curse, true, crit);   // clamped: never overshoot the next gate. The whole lump crits as one, which is the premium that makes banking worth it
        _doomGuard = false;
        if (!_doomWalking && splash > 0.01f)   // it survived (or no walker slot was free) → the splash lands right here
        {
            _doomWalkPayload = 0f;
            int seeded = 0;
            foreach (var o in Game.I.Enemies.ToArray())
            {
                if (seeded >= seedCap) break;
                if (o == null || o == this || o.Dead || o.Remote || !GodotObject.IsInstanceValid(o)) continue;
                if (new Vector2(o.GlobalPosition.X - at.X, o.GlobalPosition.Z - at.Z).Length() > splashR + o.Radius) continue;
                o.AddDoom(splash, owner, gen + 1);
                seeded++;
            }
        }
        Game.I.SpawnGroundSigil(at, Mathf.Max(2f, splashR), col);   // the sigil IS the tell for how far this blast carried
        Game.I.NetMgr?.BroadcastVfx(58, at, Vector3.Up, Mathf.Max(2f, splashR), 0f, col);
        Game.I.Sfx?.CurseCrush(at);
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
        FrozenT = 5f + _freezeDurBonus; _frozenDur = FrozenT; FreezeStacks = 0f; _freezeExpT = 0f;
        _frozenBlueMax = 0f; _frozenBlue = 0f; _frozenBlueDmg = 0f;   // no blue bank anymore (FrozenBlueFrac stays 0 → the bar isn't drawn)
        if (IsTaker && _grabPeer != 0) ReleaseGrab();   // (NEW) freezing a Taker (a hard stun, not slow/root) makes it drop its captive
        RootT = Mathf.Max(RootT, FrozenT);   // held in place
        EnsureIceBlock(true);
        Game.I.Sfx?.Freeze(GlobalPosition);
        Game.I.NetMgr?.BroadcastVfx(48, GlobalPosition, Vector3.Zero, Radius, FrozenT, DamageTypes.Col(DamageType.Frost));   // ice VFX for allies
    }

    public void ShatterInstant() { if (FrozenT <= 0f) return; ShatterFreeze(true); }   // full-charge spear / Glacial Impaler DETONATES the accrued blue bar (explosion + AoE)
    public void ShatterFreeze() => MeltFreeze();   // freeze timer ran out → quiet melt (no explosion, no damage)

    // the freeze wore off without anyone shattering it: thaw the ice away with a light crack — NO damage, AoE, or spread.
    // Idempotent + safe to call after FrozenT has already hit 0 (that's exactly the natural-expiry case).
    public void MeltFreeze()
    {
        bool wasIced = FrozenT > 0f || _iceBlock != null;
        FrozenT = 0f; _radiatesCold = false;
        _freezeThreshMul = 1f; _freezeDurBonus = 0f;   // next freeze accumulates its profile fresh
        EnsureIceBlock(false);                          // remove the ice block
        if (wasIced && Game.I != null) { Game.I.SpawnFrostShatter(GlobalPosition, Radius * 0.5f); Game.I.Sfx?.IceShatter(GlobalPosition); }   // quiet crack
    }

    // Break the ice by DETONATION — a Frost witch's spear set it off: a flat, player-scaled burst + %-max-HP execute,
    // an AoE splash, and frost spread. (The melt/thaw case lives in MeltFreeze.)
    public void ShatterFreeze(bool detonate)
    {
        if (!detonate) { MeltFreeze(); return; }
        if (FrozenT <= 0f) return;
        var pw = Game.I.Player;
        // (REDESIGN) No blue-bank at all. A frozen foe takes normal damage; a DETONATE (full-charge spear) SHATTERS it for a
        // flat, player-scaled burst + a %-max-HP execute — an immediate payoff the moment you see the ice, no pre-banking.
        float missing = MaxHp > 0f ? Mathf.Clamp(1f - Hp / MaxHp, 0f, 1f) : 0f;
        float powMul = pw != null ? pw.ShatterPowerMul : 1f;
        float hpFrac = 0.05f + 0.15f * missing;
        if (IsBoss) hpFrac = Mathf.Min(hpFrac, 0.12f);   // (NEW) cap the %-max-HP execute vs bosses/minibosses — the shatter is reliably landable now, so this keeps it from nuking MP-inflated boss pools (mirrors Life Curse's boss cap)
        float real = ((pw != null ? pw.ShatterBurstDmg() : 24f) + MaxHp * hpFrac) * powMul;
        FrozenT = 0f; _radiatesCold = false; EnsureIceBlock(false);   // (FIX) remove the ice block, don't just hide it
        _freezeThreshMul = 1f; _freezeDurBonus = 0f;   // next freeze accumulates its profile fresh
        Game.I.SpawnFrostShatter(GlobalPosition, Radius);
        Game.I.Sfx?.IceShatter(GlobalPosition);
        Game.I.NetMgr?.BroadcastVfx(49, GlobalPosition, Vector3.Zero, Radius, 0f, DamageTypes.Col(DamageType.Frost));
        float area = 7.5f * (pw != null ? pw.S.SpellArea : 1f);   // bigger shatter burst radius; still scales with AoE cards
        float shard = real * 0.3f;                               // modest AoE splash — Frost's strength is the single-target snipe (Forsaken keeps the AoE crown)
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
        Hp -= real;
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
        _remoteLivingBomb = (mask >> 11) & 0xF;   // (REPURPOSED bits 11-14) Ember Living Bomb stacks on the client
        FreezeStacks = (mask >> 15) & 0x3F;
        _remoteCursed = (mask & (1 << 21)) != 0;   // (NEW) mirror cursed glow + overhead counter + tether group
        CurseStacks = (mask >> 22) & 0x3F;
        CurseGroup = (mask >> 28) & 0x7;
        _markShow = (mask & unchecked((int)0x80000000)) != 0;   // (NEW) arcane-mark pip mirror on the client
        if ((_type == "swarmer" || _type == "taker") && _creature != null)   // (NEW) mirror idle/wall-stun pose + scream on the client proxy
        {
            _creature.IdlePose = (mask >> 7) & 3;
            bool scr = (mask & 512) != 0;
            if (scr && !_screamWas) _creature.Scream();
            _screamWas = scr;
        }
    }

    private long _lastAttackerPeer = 1;   // (NEW) who dealt the most recent damage — for host-authoritative kill credit
    private readonly System.Collections.Generic.HashSet<long> _damagers = new();   // (NEW) EVERY peer that dealt any damage to this foe — for contribution-based soul credit on death
    private void Die()
    {
        // (HOLLOW MOON) his first "death" is a fake-out — no orbs, no rewards, no lair payout. Intercepted HERE rather than
        // in Hurt so every kill path (bolts, shatter, execute, Explode) funnels through it.
        if (_type == "boss" && BossPhase == 1 && !Remote) { Hp = 0f; EnterPhase2(); return; }
        // (DOOM WALKER) …and for the same reason, a foe that died holding an undelivered blast gets one last walk. It
        // keeps its own gait and its own animations — it just has somewhere to be.
        if (!Remote && !_doomWalking && _doomWalkPayload > 0.01f && _doomWalkersLive < DoomWalkerCap && !IsBoss)
        {
            var carry = NearestAlly();
            if (carry != null)
            {
                _doomWalking = true; _doomWalkT = 2f; _doomWalkersLive++;
                Hp = 1f;                                   // stay upright long enough to deliver; Hurt ignores it from here
                Puppet(carry, 2f, _doomOwner);             // its own legs carry it — the same target override the living use
                return;
            }
        }
        Dead = true;
        Fx.SparkBurst(GlobalPosition + Vector3.Up * Radius * 0.5f, Vector3.Up, Col.Lerp(Colors.White, 0.3f), Radius * 0.5f, 8);   // (PHASE 3) GPU shard death-pop
        Game.I?.CreditKill(_lastAttackerPeer, Game.I != null && Game.I.IsNight);   // (NEW) exact MP kill attribution (host/solo only reaches Die)
        if (_type == "snake") Game.I?.NotifySnakeDied(NetId);   // (NEW) free anyone this snake had rooted
        if (_livingBombStacks > 0 && Game.I != null)   // (NEW) Ember Living Bomb: on death, erupt Z times ~0.2s apart at the death spot — each blast % of MAX hp, chaining through the crowd
        {
            var burst = new EmberDeathBurst(); Game.I.AddChild(burst);
            burst.Init(GlobalPosition + Vector3.Up * Radius * 0.5f, _livingBombStacks, 5.5f + Radius, MaxHp * 0.16f);
        }
        if (_type == "swarmer") Game.I.Sfx?.ZombieDeath(GlobalPosition);   // (NEW)
        if (_type == "taker") { ReleaseGrab(); Game.I.Sfx?.TakerDeath(GlobalPosition); }   // (NEW) free the captive
        if (IsPhalanx && _wardHp > 0f) BreakWard();          // (NEW) executed/deleted while warded → still release the rank
        if (IsArcher && _leader != null) { _leader._squad.Remove(this); _leader.RecomputeWard(false); _leader = null; }   // (NEW) a fallen archer weakens the ward it was feeding
        if (Affix == 4 || (Game.I != null && Game.I.ActiveMutator == WaveMutator.Volatile && !IsBoss && !IsGoblin)) Explode();   // volatile affix OR the Volatile mutator: blast on death (players only, never other enemies)
        if (_splitter) { for (int i = 0; i < 2; i++) Game.I.SpawnEnemyAt("spawnling", GlobalPosition); }   // splitter: spawn two (host → synced)
        Game.I.Player?.OnBloodAuraKill(GlobalPosition);        // local blood witch: ANY death in her aura banks a stack
        Game.I.NetMgr?.BroadcastEnemyDeath(GlobalPosition);   // ally blood witches check their own aura too
        if (!Remote) Game.I.NetMgr?.NoteEnemyDeath(NetId);    // (MP FIX) the id goes out in the next snapshot; clients reap ONLY on this, since absence now just means "capped out of the packet"
        // a bleeding victim ruptures; a ROT victim also spreads the bleed to nearby foes (Blood Rot chains)
        if (_bleedT > 0f)
        {
            float burst = _bleedDps * 1.2f * _bleedBurstMul;   // (OVERHAUL) Hemorrhage Rupture amplifies this
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
        if (_damagers.Count == 0) _damagers.Add(_lastAttackerPeer);   // (NEW) untracked kill (execute/environmental) → at least credit the last dealer
        // (HAUNT ECONOMY) souls now come ONLY from kills inside a Haunt — NoteHauntKill credits the contributors AND feeds
        // the break meter. Kills out in the world no longer pay souls; the hot-zone is the sole faucet.
        if (!IsBoss && !IsSpecial) Game.I.NoteHauntKill(GlobalPosition, _damagers);

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
        if (MarkT > 0 && _markDoom > 0f)   // (OVERHAUL) Doombrand: a marked foe detonates on death
        {
            foreach (var e in Game.I.Enemies.ToArray())
                if (e != null && e != this && !e.Dead && GodotObject.IsInstanceValid(e) && GlobalPosition.DistanceTo(e.GlobalPosition) < 5f) e.Hurt(_markDoom, DamageType.Curse, false);
            var dv = new Vfx(); Game.I.AddChild(dv); dv.GlobalPosition = new Vector3(GlobalPosition.X, 0.6f, GlobalPosition.Z);
            dv.Init(new SphereMesh { Radius = 2.5f, Height = 5f }, DamageTypes.Col(DamageType.Curse), 0.3f, 6f);
        }

        int orbs = IsBoss ? 8 : (Elite ? 3 : 1);
        for (int i = 0; i < orbs; i++)
        {
            var orb = new Orb { Xp = (Score * 0.5f + 2.5f) * Game.I.XpKillMul * Game.I.HauntXpMul(GlobalPosition) / orbs, Tint = Col, NetId = Game.I.NextPickupId() };   // (NEW) XpKillMul folds in the global frenzied→lvl-25 trim + the party-density damp; HauntXpMul = 2× inside the hot-zone
            Game.I.AddChild(orb);
            Game.I.AddXpOrb(orb);   // capped add — persistent orbs can't pile up unbounded
            var off = new Vector3((float)GD.RandRange(-1.5, 1.5), 1.2f, (float)GD.RandRange(-1.5, 1.5));
            orb.GlobalPosition = new Vector3(GlobalPosition.X, 1.2f, GlobalPosition.Z) + off;
        }

        // (MAGNET DROP) a witchy lodestone that vacuums every XP shard on the map when grabbed — base boss/miniboss 5%, elite 4%, normal 1.5%,
        // scaled by the BEST Luck among everyone who damaged this foe (×(1+luck), capped 25%). Only rolls when the lobby-wide cooldown is ready.
        if (Game.I.IsAuthority && Game.I.MagnetDropReady && _type != "spawnling" && _type != "goblin")
        {
            float mBase = IsBoss ? 0.0125f : (Elite ? 0.01f : 0.00375f);   // (HALVED AGAIN ×2 — lodestones were still landing too often)
            float mChance = Mathf.Min(0.25f, mBase * (1f + Mathf.Max(0f, Game.I.BestContributorLuck(_damagers))));
            if (GD.Randf() < mChance) Game.I.SpawnMagnet(new Vector3(GlobalPosition.X, 1.1f, GlobalPosition.Z));
        }
        // (NEW) WARD PLATING — same odds shape as the lodestone, but the cooldown is PER WARDEN (60s) rather than one
        // shared lobby timer, so a bigger party genuinely sees more of them. Credited to whoever landed the kill.
        if (Game.I.IsAuthority && Game.I.WardDropReady(_lastAttackerPeer) && _type != "spawnling" && _type != "goblin")
        {
            float wBase = IsBoss ? 0.0125f : (Elite ? 0.01f : 0.00375f);   // (HALVED AGAIN ×2 — same as lodestone)
            float wChance = Mathf.Min(0.25f, wBase * (1f + Mathf.Max(0f, Game.I.BestContributorLuck(_damagers))));
            if (GD.Randf() < wChance) Game.I.SpawnWardArmor(new Vector3(GlobalPosition.X, 1.1f, GlobalPosition.Z), _lastAttackerPeer);
        }

        if (_type == "boss") { BossDeathSequence(); return; }   // THE HOLLOW MOON gets a dramatic drawn-out death; frees itself after
        QueueFree();
    }

    // THE HOLLOW MOON dies: a death cry + a final line, then he pitches forward and goes down (the authored
    // Shot_and_Fall_Forward clip), his hollow body rupturing in rot & blood mid-fall. Frees itself when the sequence ends.
    private void BossDeathSequence()
    {
        Game.I.Sfx?.BossRoar(GlobalPosition);   // opening death cry
        SayBossVox(_bossDeathLines[GD.RandRange(0, _bossDeathLines.Length - 1)], new Color(1f, 0.35f, 0.35f), 3f);

        if (_hollowAnim)
        {
            _dashT = 0f;
            _creature.BossDie();                // the fall-forward clip drives the whole topple; no tween needed
            Game.I.SpawnBloodMist(GlobalPosition + new Vector3(0f, Radius, 0f), Radius * 1.2f);
        }
        else if (_creature != null)
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

