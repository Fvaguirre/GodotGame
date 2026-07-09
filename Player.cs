using Godot;
using System.Collections.Generic;

// Player.cs — THE WITCH. Movement, the whole casting pipeline, all four witches' primaries/
// secondaries, finishers, charged-cast modifiers, ultimates, combos, and the mana/blood/shield/Grove
// economies. Witch identity is a set of flags (DivineWitch/CrimsonWitch/VerdantWitch; Lunar = none)
// set by Game.ConfigureWitch; WitchIndex (0-3) derives from them.
//
// DAMAGE SPINE: Base() = 10 * S.Atk * UltDmgMul * DamageMul. Every ability multiplies Base() by its
// own coefficient and ComboMul(). CAST FLOW: Combat(dt) -> FireBolt/FireHolyRay/FireCrimsonTide ->
// SpawnBolt (rolls crit, broadcasts ghost) -> Bolt hits -> Enemy.Hurt -> OnHit/OnHitCore (combo,
// mana, lifesteal, ult charge). To add an ability/effect/witch, see DEV_GUIDE.md §6. This file is
// large; jump by method name (FireBolt, ExecuteFinisher, ApplyChargedMods, ActivateUlt, AddCombo).
public partial class Player : Node3D
{
    public Stats S = new Stats();
    public float Hp;
    public float Shield, MaxShield;
    private float _shieldT = 0f;
    private float _combatT = 999f;   // seconds since the last enemy damage; large = out of combat (NEW)
    public float ShieldSuppress = 0f;   // wardbane dispel: while >0, armor can't be gained and shield won't regen
    public DamageType PrimaryType = DamageType.Lunar;     // left-click
    public DamageType SecondaryType = DamageType.Lunar;   // charged right-click
    public bool NightAffinity = true;                     // Lunar Witch passive: waxes stronger at night
    public float LunarNightMul(DamageType t) => (NightAffinity && t == DamageType.Lunar && Game.I != null && Game.I.IsNight) ? 1.3f : 1f;

    private Camera3D _cam;
    private float _camKick = 0f;
    private const float BaseFov = 78f;
    private void CamKick(float a) { _camKick = Mathf.Min(1.2f, Mathf.Max(_camKick, a)); }
    public void CamKickExternal(float a) => CamKick(a);   // summons (Guardian) can shake the camera
    private float _pitch = 0f;
    private float _fireCd = 0f;
    private float _iframe = 0f;
    // how much combo survives a hit that reaches HP: 0 = full reset, 0.4 = keep 40%
    private const float ComboBreakKeep = 0.4f;
    private float _charge = 0f;
    private bool _chargedRefund = false;   // set on a non-blood charged release; refunds 1 mana on first enemy hit
    private bool _charging = false;
    public float MouseSens = 0.0022f;

    public bool Charging = false;
    public float ChargeAmt = 0f;
    public float ProcFlash = 0f;

    public int Combo = 0;
    public int BestCombo = 0;
    public float ComboT = -9f;
    public enum ComboAct { None, Light, Charged, Finisher }
    private ComboAct _lastAct = ComboAct.None;
    public bool FreshHit = false;     // true briefly when you chained a different action
    public float FreshT = 0f;
    public float FireHeat = 0f;       // rises while firing fast → drives music tempo
    public float HurtT = 0f;          // recent-damage spike for musical tension
    public float DashT = 0f;          // recent-dodge spike for musical tension

    // ---- ultimate ----
    public enum UltKind { None, Eclipse, LunarLight, Crescent, FaithShield, Judgement, Divinity, BloodTsunami, Exsanguinate, BloodRot, GroveGuardian, WildSwarm, Barkskin, Cyclone, Hurricane, Stormform, Blizzard, FrostElemental, DeepFreeze, HexCircle, LifeDrain, LifeCurse, MeteorDescent, WildfireRush, PhoenixAscend }   // …Forsaken = HexCircle/LifeDrain/LifeCurse (NEW)
    public UltKind Ult = UltKind.None;
    public float UltCharge = 0f;       // 0..1
    public float DmgWindow = 0f;        // damage dealt since last team-damage broadcast (ult-share)
    public int UltTier = 0;            // rarity tier 0..4 (boss-token upgrades)
    public bool UltActive = false;
    public float UltActiveT = 0f;
    public float UltDmgMul = 1f;
    public bool ModEclipse = false, ModLight = false, ModCrescent = false;   // legendary ult-mods
    public float EclipseCrit => (Ult == UltKind.Eclipse && UltActive) ? 0.25f : 0f;   // +crit while the eclipse is up
    private bool RollCrit() => GD.Randf() < Mathf.Min(0.95f, S.CritChance + EclipseCrit + (EmberFervorT > 0f ? _emberFervorCrit : 0f));
    private float _eclipseMax = 1f;
    public float EclipseFrac => _eclipseMax > 0f ? Mathf.Clamp(UltActiveT / _eclipseMax, 0f, 1f) : 0f;
    public float EclipseTime => UltActiveT;
    public float EclipseActive01 => (Ult == UltKind.Eclipse && UltActive) ? 1f : 0f;   // synced to allies for the blood-moon tell
    public bool ModShield = false, ModJudge = false, ModDivinity = false;     // divine legendary ult-mods
    public bool ModTsunami = false, ModExsang = false, ModRot = false;         // crimson legendary ult-mods
    public bool ModGuardian = false, ModSwarm = false, ModBark = false;        // verdant legendary ult-mods
    public bool ModCyclone = false, ModHurricane = false, ModStorm = false;   // gale legendary ult-mods (NEW)
    public bool ModBlizzard = false, ModFrostElem = false, ModDeepFreeze = false;   // frost legendary ult-mods (NEW)
    public bool ModPlague = false, ModRapture = false, ModRite = false;   // forsaken legendary ult-mods (NEW)
    // ---- Ember ult runtime state (NEW) ----
    private bool _meteorAscend = false; private float _meteorAscendT = 0f, _meteorBaseY = 0f;   // Meteor Descent: rise + top-down aim window
    private int _flameDashCharges = 0; private float _flameDashWindowT = 0f, _flameDashT = 0f, _flameDashDur = 0f, _flameDashDist = 0f; private Vector3 _flameDashDir;   // Wildfire Rush: dash stock + window + motion
    public float BurnLifestealT = 0f;   // Wildfire Rush: while >0, this player's burn ticks heal her 100%
    private bool _phoenix = false, _phoenixRebirth = false; private float _phoenixAuraT = 0f;   // Phoenix Ascendant: transform + one-shot cheat-death
    public bool PhoenixActive => Ult == UltKind.PhoenixAscend && UltActive;
    private Node3D _phoenixVfx;
    // ---- Ember Fervor finisher buff (crit + move speed; witch-agnostic) ----
    public float EmberFervorT = 0f; private float _emberFervorCrit = 0f, _emberFervorSpeed = 0f, _fervorNetT = 0f;
    private readonly System.Collections.Generic.List<Node3D> _fervorFlames = new();
    public bool EmberFervorActive => EmberFervorT > 0f;
    // ---- Forsaken ult runtime state (NEW) ----
    private int _hexGroup = 0; private Node3D _hexVfx; private float _hexTickT = 0f, _hexNetT = 0f;   // Hex Circle: the mega-group id + ground field + tick throttles
    private float _drainBank = 0f, _drainBaseY = 0f, _drainTickT = 0f, _drainNetT = 0f; private Node3D _drainVfx;   // Life Drain: banked lifesteal + hover base + aura
    private readonly System.Collections.Generic.List<Node3D> _drainLinks = new();
    public bool LifeDrainActive => Ult == UltKind.LifeDrain && UltActive;
    public bool HurricaneActive => Ult == UltKind.Hurricane && UltActive;      // piloting the hurricane (aloft) (NEW)
    private float _hurriBaseY = 0f;      // ground height she leapt from, to hover above and fall back to (NEW)
    private float _hurriFlingCd = 0f;    // throttles how often the storm re-flings each enemy batch (NEW)
    private float _hurriGrindT = 0f;     // throttles the storm's grind-damage tick (NEW)
    private float _mineDropT = 0f;       // Stormform legendary: cadence of air-mine drops while moving (NEW)
    private float _windZoneT = 0f;       // Eyewall: cadence of hurricane-zone buff broadcasts (NEW)
    private float _windBoonT = 0f;       // Eyewall: time left on the move/cast/charge buff from standing in a hurricane (NEW)
    public void GrantWindBoon(float dur) { _windBoonT = Mathf.Max(_windBoonT, dur); }   // Eyewall buff (applied by Net to allies in-zone) (NEW)
    private float WindBoonSpeedMul  => _windBoonT > 0f ? 1.3f  : 1f;   // (NEW)
    private float WindBoonFireMul   => _windBoonT > 0f ? 0.75f : 1f;   // faster casts (NEW)
    private float WindBoonChargeMul => _windBoonT > 0f ? 1.35f : 1f;   // faster charge fill (NEW)
    private Node3D _hurriVfx;            // the funnel visual that tracks beneath her (NEW)
    private float _hurriNetT = 0f;       // (NEW) throttle for broadcasting the funnel position to allies
    private float _stormMax = 1f;                                              // Stormform: HUD meter denominator (NEW)
    public bool StormActive => Ult == UltKind.Stormform && UltActive;          // Stormform self-buff query (NEW)
    public float StormFrac => _stormMax > 0f ? Mathf.Clamp(UltActiveT / _stormMax, 0f, 1f) : 0f;   // Stormform meter 0..1 (NEW)
    public float StormTime => UltActiveT;                                      // Stormform seconds left (NEW)
    private float StormSpeedMul => StormActive ? 1.5f : 1f;                    // Stormform: +50% move speed (NEW)
    public float MoveSpeedFactor => Mathf.Max(1f, StormSpeedMul * WindBoonSpeedMul * (EmberFervorT > 0f ? 1f + _emberFervorSpeed : 1f));   // (NEW) swarmers scale up to keep pace with a fast player; Ember Fervor also speeds her
    private float StormFireMul => StormActive ? 0.6f : 1f;                     // Stormform: 40% faster casts (NEW)
    private float _galeGuard = 0f;                                             // Tailwind: brief damage-reduction window after a dash (Gale) (NEW)
    public float GustPower = 1f;                                               // Gale card "Crosswind": scales charged-gust knockback + reach (NEW)
    public bool TempestHeart = false;                                         // Gale legendary "Tempest Heart": full-charge gusts drop a lingering mini-cyclone (NEW)
    public bool Cloudfeather = false;   // Gale legendary: passive HP regen while airborne (NEW)
    public bool Downburst = false;      // Gale legendary: landing from a height slams a Wind shockwave (NEW)
    public bool Jetstream = false;      // Gale legendary: +25% damage while airborne (NEW)
    private float JetstreamMul() => (Jetstream && Airborne) ? 1.25f : 1f;   // (NEW)
    private bool _galeHover = false;     // Gale: holding a charged punch in the air → hover + aim the dive (NEW)
    private bool _galeDiving = false;    // Gale: released an air charge → rocketing down to slam (NEW)
    private Vector3 _galeDiveTarget;     // Gale: ground point we're diving at (NEW)
    private float _galeDiveCharge = 0f;  // Gale: charge captured at release, used for the dive's slam power (NEW)
    private MeshInstance3D _galeAimRing; // Gale: ground target indicator shown while hovering (NEW)
    private float _barkT = 0f, _barkDmg = 0f, _barkMax = 1f;                   // Barkskin: invuln window + expiry-burst damage + bar max
    public bool BarkActive => _barkT > 0f;                                     // during Barkskin you take no damage and can't detonate your ents
    public float BarkFrac => _barkMax > 0f ? Mathf.Clamp(_barkT / _barkMax, 0f, 1f) : 0f;
    public float BarkTime => _barkT;
    public Guardian ActiveGuardian;                                           // current Ancient Guardian (synced to allies via Net)

    // ---- Crimson Blood Witch ----
    public bool CrimsonWitch = false;
    public bool VerdantWitch = false;   // Nature summoner/controller
    public bool GaleWitch = false;      // Wind mobility/control witch — knockback gusts + cyclones (NEW)
    public bool FrostWitch = false;     // (NEW) Frost sniper — freezing beam + charged icicle spear + shatter
    public bool ForsakenWitch = false;  // (NEW) Curse controller — a lock-on curse-suck beam that tethers foes into hexed groups
    public bool EmberWitch = false;     // (NEW) Ember pyro — flamethrower cone + aimed meteor; stacks burn → Living Bomb
    public int WitchIndex => EmberWitch ? 7 : ForsakenWitch ? 6 : FrostWitch ? 5 : GaleWitch ? 4 : (VerdantWitch ? 3 : (CrimsonWitch ? 2 : (DivineWitch ? 1 : 0)));   // 0 Lunar,1 Divine,2 Crimson,3 Verdant,4 Gale,5 Frost,6 Forsaken,7 Ember
    // ---- Forsaken (Curse) witch tuning ----
    public int MaxLinks = 6;              // (NEW) how many foes she can tether across all curse groups at once
    public float CurseRate = 2.5f;        // (NEW) curse stacks/sec the suck-beam builds — faster so groups form quickly
    public float CurseShareFrac = 0.5f;   // (NEW) fraction of any damage instance shared to a cursed group-mate
    public float CurseSpreadRange = 18f;  // (NEW) how close a foe must be to a beamed foe to get pulled into its curse group
    public DamageType CurseBonusType = DamageType.Curse;   // (NEW) cursed foes take bonus damage from this type (legendary can change it)
    public int CurseBonusType2 = -1;      // (NEW) legendary "Cursebrand": a SECOND damage type that also gets the bonus vs cursed foes (-1 = none, else a DamageType cast to int)
    public float CurseBonusMul = 1.5f;    // (NEW) how much extra the bonus type deals to cursed foes
    public float CurseStackCap = 5f;      // (NEW) crush detonation tapers to this many EFFECTIVE stacks (diminishing returns past it); affinity cards raise it
    public float CurseBeamLifesteal = 0.13f;   // (NEW) fraction of the suck-beam's DoT healed back to her (base-kit sustain)
    public bool SoulTether = false, WitheringPresence = false;   // (NEW) forsaken affinity legendaries
    private float _witherT = 0f;          // Withering Presence throttle
    // ---- Grove (Verdant minions) ----
    public System.Collections.Generic.List<Thornling> Ents = new();
    private int _entCombo = 0;
    public bool MinionChain = false;                              // legendary: ent explosions set off nearby ents
    // ---- witch-affinity card effects ----
    public int CrescentPierceBonus = 0;     // Lunar: Waxing Crescent
    public float CrescentSizeMul = 1f;       // Lunar: Waxing Crescent
    public float LunarBonus = 0f;            // Lunar: Nightfall's Gift (+Lunar dmg, doubled at night)
    public float UltChargeMul = 1f;          // Lunar: Lunar Eclipse
    public float BlessBonus = 0f;            // Divine: Benediction (longer bless + self-mend)
    public int MoteFork = 0;                 // Divine: Twin Light (mote forks to N nearby foes)
    public bool MartyrGrace = false;         // Divine: Martyr's Grace
    public bool SanguineFrenzy = false;      // Crimson: Sanguine Frenzy (more dmg the lower your HP)
    public float AuraBonusR = 0f;            // Crimson: Crimson Communion (+aura radius)
    public float AuraHealMul = 1f;           // Crimson: Crimson Communion (+aura-kill heal)
    public bool Hemoclast = false;           // Crimson: Hemoclast (blood nova when spending stacks)
    // ---- new witch legendaries (NEW) ----
    public bool RadiantMote = false;         // Divine legendary "Radiant Ascension": while AIRBORNE, motes mend allies they pass (+combo) and pierce on to a foe; primary can lock allies
    public bool GravityWell = false;         // Lunar legendary "Gravity Well": a slain foe collapses, dragging nearby enemies inward
    public bool Bloodbath = false;           // Crimson legendary "Bloodbath": each kill bursts blood — heals you + damages nearby foes
    public bool GuardianAegis = false;       // Divine legendary "Guardian's Aegis" (gate)
    public bool CrimsonFrenzy = false;       // Crimson legendary "Crimson Frenzy" (gate)
    public bool AncientGrove = false;        // Verdant legendary "Ancient Grove" (gate)
    public bool VerdantVitality = false;     // Verdant legendary "Verdant Vitality" (gate)
    private float _killProcCd = 0f;          // throttle for the on-kill legendary procs (Gravity Well / Bloodbath)
    // ---- Verdant finisher state + legendary mods (equippable by ANY witch) ----
    public bool ModPoisonField = false;      // Creeping Blight legendary: also slows + thicker poison
    public bool ModSeedMine = false;         // Seed Mines legendary: chain-detonate
    public bool ModThornSkin = false;        // Thorn Skin legendary: bigger burst + root/poison
    public int GroveEvery = 14;                                   // combo per summoned ent (lower = faster; upgradeable)
    public int GroveBonusEnts = 0;                               // extra max ents from upgrades
    public int MaxEnts => 3 + GroveBonusEnts;                    // base 3; grows ONLY via Deepening Grove cards (cap +4 → 7)
    public float MinionDamage() => Base() * 0.6f;
    public float MinionBurst() => Base() * 3.0f;                   // full-charge detonation — her big burst
    public float PoisonDps() => Base() * 0.15f;                   // (NERF 0.22→0.15) per application — additive, stacks while you keep hitting; her primary poison was ~2.6× overtuned
    // her tree-ents are part of her kit: their direct hits inherit her crit + lifesteal (but NOT her full
    // HP or flat damage, which stay fractional). Rolls a crit, heals her for the leech, returns final damage.
    public float MinionStrike(float baseDmg, out bool crit)
    {
        crit = RollCrit();
        float dmg = crit ? baseDmg * CritMult() : baseDmg;
        if (S.Lifesteal > 0f) Heal(dmg * S.Lifesteal);
        return dmg;
    }
    public int CountEnts() { Ents.RemoveAll(e => e == null || !GodotObject.IsInstanceValid(e)); return Ents.Count; }
    public float EntProgress => Mathf.Clamp(_entCombo / (float)GroveEvery, 0f, 1f);   // 0..1 toward the next ent

    // Wild Swarm ult: a forward-charging stampede of critters that trample everything in their path,
    // then vanish. They can't be damaged, detonated, or targeted — pure sweeping offense. ModSwarm
    // (Teeming Grove) makes the wave wider, deeper, and more numerous.
    private float LaunchStampede(int t)
    {
        Vector3 fwd = AimDir(); fwd.Y = 0;
        if (fwd.LengthSquared() < 0.01f) fwd = -GlobalTransform.Basis.Z;
        fwd = fwd.Normalized();
        float width = 9f + (ModSwarm ? 4f : 0f);
        float dur = 3f + t * 0.5f + (ModSwarm ? 1.5f : 0f);
        float dmg = MinionBurst() * (0.5f + 0.1f * t);            // per-hit; enemies in the lane get hit repeatedly as the stream passes
        var st = new Stampede();
        Game.I.AddChild(st);
        st.Init(this, GlobalPosition, fwd, width, dmg, dur, false);
        Game.I.NetMgr?.BroadcastVfx(10, GlobalPosition, fwd, width, dur, new Color(0.4f, 0.85f, 0.4f));
        return dur;
    }

    private void SummonEnt()    {
        if (Game.I == null) return;
        var t = new Thornling { Caster = this, Slot = Ents.Count };
        Game.I.AddChild(t);
        t.GlobalPosition = GlobalPosition + new Vector3((float)GD.RandRange(-2.0, 2.0), 0, (float)GD.RandRange(-2.0, 2.0));
        Ents.Add(t);
        Game.I.VfxRing(t.GlobalPosition, new Color(0.4f, 0.85f, 0.4f), 2f, 0.4f);
    }
    // crit damage multiplier with diminishing returns past +150% (keeps it strong but not runaway)
    public float CritMult()
    {
        float cd = S.CritDamage;
        float eff = cd <= 1.5f ? cd : 1.5f + (cd - 1.5f) * 0.45f;
        return 1f + eff;
    }
    public int BloodStacks = 0;
    // ---- unified ARMOR: one shared pool of damage-negating charges (blood = red, thorn = green).
    // a shared cap means stacking shield sources can't make you untouchable; +1 cards raise it toward the ceiling.
    public struct ArmorCharge { public bool Thorn; public float Dmg; }   // Dmg = thorn burst damage banked at grant time
    public System.Collections.Generic.List<ArmorCharge> Armor = new();
    public int MaxArmor = 3;
    public const int ArmorCeil = 5;
    public int ArmorCount => Armor.Count;
    public int ThornCount { get { int n = 0; foreach (var a in Armor) if (a.Thorn) n++; return n; } }
    public int ArmorPacked => (Armor.Count & 0xF) | ((ThornCount & 0xF) << 4);   // NetVitals: low nibble total, next nibble thorn
    public int StunStateNet => GrabbedBy != 0 ? 2 : (StunT > 0.1f ? 1 : 0);   // (NEW) synced via ArmorPacked bits 8-9 for the ally roster
    public bool HasShieldSource => Ult == UltKind.BloodTsunami || Fin.Exists(f => f.Type == FinType.ThornSkin);
    public void GrantRandomArmor()   // chest drop: ONE random armor charge (blood or thorn), respects the cap
    {
        bool thorn = GD.Randf() < 0.5f;
        AddArmor(thorn, thorn ? Base() * 1.6f : 0f);
    }
    public void AddArmor(bool thorn, float dmg = 0f)
    {
        if (ShieldSuppress > 0f) return;       // wardbane suppression: no new wards until it wears off
        if (Armor.Count >= MaxArmor) return;   // shared cap — no stacking past it
        Armor.Add(new ArmorCharge { Thorn = thorn, Dmg = dmg });
        ProcFlash = 0.3f;
    }
    public const int MaxBloodStacks = 12;
    private float _bloodPartial = 0f;
    public float FinHpCost = 0.18f;     // Crimson finisher cost as a fraction of max HP (mana upgrades reduce this)
    private float _rushT = 0f, _rushDur = 0.28f, _rushDist = 0f;
    private Vector3 _rushDir = Vector3.Forward;
    private bool _rushWind = false;       // Wind Rush emits gust puffs while gliding (NEW)
    private float _windPuffCd = 0f;       // throttle for wind-rush / air-dive gust puffs (NEW)

    public float AuraRadius => 5f + Mathf.Min(Level, 30) * 0.35f + AuraBonusR;   // grows with level (+ Communion)
    public void AddBloodStack(float n)
    {
        _bloodPartial += n;
        while (_bloodPartial >= 1f && BloodStacks < MaxBloodStacks) { _bloodPartial -= 1f; BloodStacks++; }
        if (BloodStacks >= MaxBloodStacks) _bloodPartial = 0f;
    }
    // Equippable blood-flavored abilities (finishers/mods/minors/ults usable by any witch) call THIS,
    // not AddBloodStack directly. Crimson banks a Blood Stack (her signature); every other witch — who
    // has no stack economy or HUD — instead mends a little HP. Keeps stacks exclusive to Crimson.
    public void BloodReward(float n)
    {
        if (CrimsonWitch) AddBloodStack(n);
        else if (n > 0f) Heal(S.MaxHp * 0.02f * n);
    }
    // a charged right-click release spends banked stacks to heal (universal — any witch with stacks).
    // a tap spends 1; a fuller charge spends more, up to a cap.
    private void ConsumeBloodStacks(float charge)
    {
        if (BloodStacks <= 0) return;
        int maxSpend = 1 + Mathf.FloorToInt(Mathf.Clamp(charge, 0f, 1f) * 5f);   // 1..6
        int spend = Mathf.Min(BloodStacks, maxSpend);
        BloodStacks -= spend;
        Heal(S.MaxHp * 0.045f * spend);
        if (spend > 0) { Ring(GlobalPosition, DamageTypes.Col(DamageType.Blood), 3.5f, 0.4f); ProcFlash = 0.25f; }
        if (Hemoclast && spend > 0)   // the heal-dump also erupts a blood nova scaling with stacks spent
        {
            float nr = 5f + spend * 0.8f, nd = Base() * (0.5f + 0.35f * spend);
            foreach (var en in Game.I.Enemies.ToArray())
                if (en != null && !en.Dead && GodotObject.IsInstanceValid(en) && Flat(en, GlobalPosition) < nr + en.Radius)
                    en.Hurt(nd, DamageType.Blood, true);
            Game.I.DamageWorld(GlobalPosition, nr, nd);   // (FIX) blood nova breaks props too
            Ring(GlobalPosition, DamageTypes.Col(DamageType.Blood), nr, 0.45f);
            Game.I.NetMgr?.BroadcastVfx(2, GlobalPosition, Vector3.Zero, nr, 0f, DamageTypes.Col(DamageType.Blood));
        }
    }
    // Crimson aura: a kill inside it gives a minor heal + banks a Blood Stack
    public void OnBloodAuraKill(Vector3 at)
    {
        if (!CrimsonWitch) return;
        if (new Vector2(at.X - GlobalPosition.X, at.Z - GlobalPosition.Z).Length() > AuraRadius) return;
        Heal(S.MaxHp * (0.02f + 0.0015f * Level) * (0.7f + 0.3f * ComboMul()) * AuraHealMul);   // minor, scales mostly with level (+Communion)
        AddBloodStack(1f);
    }

    // ---- Divine Witch ----
    public bool DivineWitch = false;
    public int Interventions = 0;       // Divine Intervention charges (revive on lethal)
    public bool Downed = false;         // incapacitated — an ally can revive; game-over only when ALL are down
    public float ReviveProg = 0f;       // 0..1 reviver progress (for HUD), set by Game while an ally holds E
    public float BlessedT = 0f;         // Blessed: amplifies all healing received
    public bool Divinity = false;       // Divinity ult active (ascended, invulnerable)
    private float _divT = 0f, _divBaseY = 0f, _noFall = 0f;
    private bool _divFalling = false;   // stays invulnerable through the descent until her feet touch ground
    private HolyGround _holyArea = null;   // the one consecrated strip the holy ray leaves (only one at a time)

    public float HealMul() => BlessedT > 0f ? 1.6f : 1f;
    private float _healAccum = 0f, _healPopT = 0f;
    public void Heal(float amt)
    {
        if (amt <= 0f || Hp <= 0f) return;
        float before = Hp;
        Hp = Mathf.Min(S.MaxHp, Hp + amt * HealMul());
        _healAccum += Hp - before;   // count only effective healing (not overheal)
        if (Game.I != null) Game.I.MyStats.Healing += Hp - before;   // (NEW) end-of-run tally: total healing done
    }
    // friendly healing also mends the witch's tree-ents (positive support effect)
    public bool HealOwnMinions(float amt)    {
        if (amt <= 0f) return false;   // (NEW) minions are allies — any friendly heal mends them
        bool did = false;
        foreach (var t in Ents.ToArray())
            if (t != null && GodotObject.IsInstanceValid(t) && t.Hp < t.MaxHp) { t.Heal(amt); did = true; }   // (FIX) minion's own bless amplifies (Thornling.Heal)
        return did;
    }
    public void BlessOwnMinions(float dur)    {   // (NEW) minions are allies — the holy bless reaches them too
        if (dur <= 0f) return;
        foreach (var t in Ents.ToArray())
            if (t != null && GodotObject.IsInstanceValid(t)) t.Bless(dur);
    }
    // a fully-charged right-click detonation that scored a kill gives one ent back, spawned immediately (max one per detonation)
    public void RefundEnt()
    {
        if (!VerdantWitch) return;
        if (CountEnts() >= MaxEnts) return;   // don't exceed the grove cap
        SummonEnt();
    }
    public float HealFlash = 0f;        // brief green cue when an ally heals you
    public void MarkHealed() { HealFlash = 0.5f; }
    private void FlushHealPopup(float dt)
    {
        if (_healPopT > 0f) _healPopT -= dt;
        if (_healAccum >= 1f && _healPopT <= 0f && Game.I != null && Game.I.DmgNumbers)
        {
            var pop = new DamagePopup();
            Game.I.AddChild(pop);
            pop.Init(_healAccum, new Color(0.40f, 0.95f, 0.55f), GlobalPosition, false, true);   // green "+N"
            _healAccum = 0f; _healPopT = 0.4f;
        }
        else if (_healAccum >= 1f && _healPopT <= 0f) { _healAccum = 0f; }   // numbers off — clear silently
    }
    public void OnShieldEnded() { }     // hook for Faith Shield teardown

    // a kill during Lunar Eclipse erupts in a dark-moon blast (moderate lunar AoE; chains across frames)
    public void OnEclipseKill(Vector3 at)
    {
        var b = new EclipseBurst { Radius = (5f + UltTier * 0.6f) * S.SpellArea, Dmg = Base() * 1.0f };
        Game.I.AddChild(b);
        b.GlobalPosition = new Vector3(at.X, 1.0f, at.Z);
        Game.I.NetMgr?.BroadcastVfx(7, b.GlobalPosition, Vector3.Zero, 5f + UltTier * 0.6f, 0f, DamageTypes.Col(DamageType.Lunar));
    }
    private readonly List<CrescentOrb> _crescents = new();
    public List<CrescentOrb> CrescentOrbs => _crescents;   // (NEW) synced to allies via CrescentSnapshot
    public float Mana;
    public bool GodMode = false;   // (NEW) dev console: invincible + infinite mana
    public int DashStock;
    public float DashCdT = 0f;
    private float _dashT = 0f;
    private Vector3 _dashDir = Vector3.Forward;
    private const float DashDur = 0.16f;
    public int Level = 1;
    public float Xp = 0f;
    public float XpNext = 28f;
    public float ManaFlash = 0f;

    public List<FinisherSlot> Fin = new();
    public List<Modifier> Mods = new();
    public List<MinorSlot> Minors = new();   // passive auto-finishers (stack infinitely)

    public void AddMinor(MinorType t)
    {
        var ex = Minors.Find(m => m.Type == t);
        if (ex != null) ex.Stacks++;
        else Minors.Add(new MinorSlot { Type = t });
    }
    // advance every minor's combo charge; fire any that reach their threshold
    private void TickMinors(int gain)
    {
        foreach (var m in Minors)
        {
            m.Charge += Mathf.Max(1, gain);
            if (m.Charge >= m.Every) { m.Charge = 0; FireMinor(m.Type, m.Stacks); }
        }
    }

    private SegBeam _beamSeg; private OmniLight3D _beamLight; private const int SpellLanceSegs = 7;
    private float _beamT = 0f, _beamPow = 0f, _beamWidth = 2.2f;
    private float _beamBurnT = 0f, _beamPlasmaT = 0f;   // (NEW) throttle scorch decals + plasma drips
    private Vector3 _beamDir = Vector3.Forward;   // locked at activation
    // ===== FROST WITCH: freezing beam (primary) =====
    private SegBeam _frostSeg; private const int FrostBeamSegs = 7; private float _frostBeamNetT = 0f, _frostBeamSndT = 0f, _beamHitT = 0f, _frostMarkT = 0f;
    private Node3D _frostNock;   // (NEW) nocked ice arrow shown while charging the icicle spear
    public float FreezeRate = 1.6f;   // stacks/sec the beam builds (card-scalable later)
    public float FrostDurBonus = 0f;     // (NEW) Lingering Frost: +frozen seconds
    public float FreezeThreshMul = 1f;   // (NEW) Brittle: lower freeze threshold
    public float ShatterPowerMul = 1f;   // (NEW) Shatterpoint: stronger shatter
    public float ShatterFreezeStacks = 1f;   // (NEW) shatter seeds this many flat freeze stacks into each hit foe (card-scalable)
    public bool ShatterCascade = false;  // (NEW legendary) shatters chain to nearby frozen foes
    public bool DeepWinter = false;      // (NEW legendary) frozen foes chill neighbours into freezing
    public bool GlacialImpaler = false;  // (NEW legendary) spear pierces everything + shatters frozen at any charge
    private const float BeamLen = 42f;
    public bool Channeling => _beamT > 0;

    private Node3D _armL, _armR;
    private MeshInstance3D _chargeOrb;
    private Node3D _thornCharge;   // Verdant charge-up spike
    private Vector3 _baseLPos, _baseRPos, _baseLRot, _baseRRot;
    private float _kickL, _kickR, _ht;
    private int _fireHand;
    private string _animKind = "";
    private float _animT, _animDur;

    private static float Now => Game.GameClock;
    private static int Tier(Rarity r) => (int)r;
    private float FrenzyMul() => SanguineFrenzy ? (1f + 0.25f * (1f - Mathf.Clamp(Hp / Mathf.Max(1f, S.MaxHp), 0f, 1f))) : 1f;   // up to +25% near death
    private float Base() => 10f * S.Atk * UltDmgMul * DamageMul * FrenzyMul() * JetstreamMul();   // JetstreamMul = Gale airborne bonus (NEW)
    public float ShatterBurstDmg() => Base() * 7.0f * ComboMul();   // (NEW) player-scaled flat shatter burst — her signature single-target snipe, tuned to edge out the Forsaken's crush (~68 → shatter ~73+ at full HP)
    public float DamageMul = 1f;   // per-witch base-damage scalar (Divine trades damage for sustain)
    public float ComboMul() => 1f + Mathf.Min(Mathf.Max(Combo - 1, 0), S.ComboCap) * S.ComboPow;
    public float ComboFrac() => Mathf.Clamp((S.ComboWindow - (Now - ComboT)) / S.ComboWindow, 0, 1);
    public bool ComboLive => Combo > 1 && (Now - ComboT) <= S.ComboWindow;
    public Vector3 AimDir() => (-_cam.GlobalTransform.Basis.Z).Normalized();
    public Vector3 EyePos => _cam.GlobalPosition;
    public Camera3D Cam => _cam;

    public override void _Ready()
    {
        Hp = S.MaxHp; Mana = S.ManaMax; DashStock = S.DashCharges;
        MaxShield = S.MaxHp * S.ShieldPct; Shield = MaxShield;
        _cam = new Camera3D { Position = new Vector3(0, 2.6f, 0), Fov = 78, Current = true };
        AddChild(_cam);
        AddChild(new OmniLight3D { Position = new Vector3(0, 2.3f, 0), OmniRange = 10f, LightColor = Palette.Lunar, LightEnergy = 0.6f });
        BuildHands();
        BuildBodyModel();
    }

    private void BuildBodyModel()
    {
        if (_bodyModel != null && GodotObject.IsInstanceValid(_bodyModel)) _bodyModel.QueueFree();
        _bodyModel = new WitchModel();
        _bodyModel.Build(WitchIndex, true);   // first-person: robe + legs only (no head/hat/arms — camera hands cover those)
        AddChild(_bodyModel);
        _prevBodyPos = GlobalPosition;
    }

    private MeshInstance3D _handMeshL, _handMeshR;
    private WitchModel _bodyModel;       // local first-person body (robe/legs); FP hands stay on the camera
    private Vector3 _prevBodyPos;

    private void BuildHands()
    {
        _armL = BuildArm(Palette.Verdant, out _handMeshL);
        _baseLPos = new Vector3(-0.40f, -0.32f, -0.25f); _baseLRot = new Vector3(-0.10f, 0.25f, 0);
        _armL.Position = _baseLPos; _armL.Rotation = _baseLRot; _cam.AddChild(_armL);
        _armR = BuildArm(Palette.Lunar, out _handMeshR);
        _baseRPos = new Vector3(0.40f, -0.32f, -0.25f); _baseRRot = new Vector3(-0.10f, -0.25f, 0);
        _armR.Position = _baseRPos; _armR.Rotation = _baseRRot; _cam.AddChild(_armR);
        _chargeOrb = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.18f, Height = 0.36f } };
        _chargeOrb.Position = new Vector3(0, -0.18f, -1.0f);
        _chargeOrb.MaterialOverride = Game.ToonEmissive(Palette.Lunar, 2.0f, 0f);
        _chargeOrb.Visible = false;
        _cam.AddChild(_chargeOrb);
        // Verdant: a knotted-wood conical spike that forms while charging the thorn, point facing forward.
        // Centered in view but kept semi-transparent so it doesn't block sight; the FIRED spike fades to solid.
        _thornCharge = new Node3D { Position = new Vector3(0, -0.16f, -1.1f), Visible = false };
        var twood = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.5f, 0.37f, 0.2f, 0.42f),
            EmissionEnabled = true, Emission = new Color(0.4f, 0.82f, 0.42f), EmissionEnergyMultiplier = 0.7f,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
        };
        var ccone = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.2f, Height = 0.8f }, MaterialOverride = twood };
        ccone.RotationDegrees = new Vector3(-90, 0, 0);   // point faces forward (-Z)
        _thornCharge.AddChild(ccone);
        for (int i = 0; i < 3; i++)
        {
            var knot = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.07f, Height = 0.14f }, MaterialOverride = twood };
            float a = i * 2.1f;
            knot.Position = new Vector3(Mathf.Cos(a) * 0.12f, Mathf.Sin(a) * 0.12f, -0.1f - i * 0.12f);
            _thornCharge.AddChild(knot);
        }
        _cam.AddChild(_thornCharge);
        RetintHands();

        // Crimson Blood Witch aura ring (grows with level; hidden for other witches)
        _auraRing = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = 0.92f, OuterRadius = 1.0f } };
        _auraRing.MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.78f, 0.07f, 0.12f, 0.5f),
            EmissionEnabled = true, Emission = DamageTypes.Col(DamageType.Blood), EmissionEnergyMultiplier = 1.4f,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
        };
        // flat ground ring under the witch — TorusMesh is already flat in this build (NEW: removed upright rotation)
        _auraRing.Position = new Vector3(0, 0.06f, 0);
        _auraRing.Visible = false;
        AddChild(_auraRing);
    }

    private MeshInstance3D _auraRing;
    private void UpdateAura()
    {
        if (_auraRing == null) return;
        _auraRing.Visible = CrimsonWitch;
        if (CrimsonWitch) { float r = AuraRadius; _auraRing.Scale = new Vector3(r, r, r); }
    }

    // recolor the hands/charge-orb to match the equipped elements (call after the witch is configured)
    public void RetintHands()
    {
        var pc = DamageTypes.Col(PrimaryType);
        var sc = DamageTypes.Col(SecondaryType);
        if (_handMeshL != null) _handMeshL.MaterialOverride = Game.ToonEmissive(pc, 0.8f, 0.02f);
        if (_handMeshR != null) _handMeshR.MaterialOverride = Game.ToonEmissive(sc, 0.8f, 0.02f);
        if (_bodyModel != null) BuildBodyModel();   // recolor the body to the witch's damage type
        if (_chargeOrb != null) _chargeOrb.MaterialOverride = Game.ToonEmissive(sc, 2.0f, 0f);
    }

    private Node3D BuildArm(Color skin, out MeshInstance3D handMesh)
    {
        var n = new Node3D();
        var fore = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.07f, BottomRadius = 0.10f, Height = 0.6f } };
        fore.RotationDegrees = new Vector3(90, 0, 0);
        fore.Position = new Vector3(0, 0, -0.30f);
        fore.MaterialOverride = Game.Toon(new Color(0.12f, 0.10f, 0.16f), 0.85f, 0.2f, 0.02f);
        n.AddChild(fore);
        var hand = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.13f, Height = 0.26f } };
        hand.Position = new Vector3(0, 0, -0.62f);
        hand.MaterialOverride = Game.ToonEmissive(skin, 0.5f, 0.02f);
        n.AddChild(hand);
        handMesh = hand;
        return n;
    }

    // middle-click ping: name whatever the crosshair is on (enemy / chest / vendor / pumpkin), else ping the spot
    private void DoPing()
    {
        if (Game.I == null) return;
        var origin = EyePos; var dir = AimDir();
        float bestAlong = 150f; Enemy be = null;
        foreach (var en in Game.I.Enemies)
        {
            if (en == null || en.Dead || en.Remote || !GodotObject.IsInstanceValid(en)) continue;
            var to = en.GlobalPosition + Vector3.Up * en.Radius - origin;
            float along = to.Dot(dir); if (along < 2f || along > 150f) continue;
            if ((to - dir * along).Length() < en.Radius + 3f && along < bestAlong) { bestAlong = along; be = en; }
        }
        Node3D bo = null; string bn = ""; Color bc = Colors.White; float boAlong = 150f;
        void consider(Node3D n, string nm, Color c)
        {
            if (n == null || !GodotObject.IsInstanceValid(n)) return;
            var to = n.GlobalPosition + Vector3.Up - origin;
            float along = to.Dot(dir); if (along < 2f || along > 150f) return;
            if ((to - dir * along).Length() < 3.5f && along < boAlong) { boAlong = along; bo = n; bn = nm; bc = c; }
        }
        foreach (var c in Game.I.Chests) consider(c, "Chest", new Color(1f, 0.85f, 0.3f));
        foreach (var pk in Game.I.Smashables) consider(pk, "Pumpkin", new Color(1f, 0.55f, 0.15f));
        consider(Game.I.VendorMystic, "The Mystic", new Color(0.72f, 0.42f, 1f));
        consider(Game.I.VendorScroll, "The Scrolls", new Color(0.42f, 0.8f, 1f));

        Vector3 pingPos; string name; Color col;
        if (be != null && bestAlong <= boAlong) { pingPos = be.GlobalPosition + Vector3.Up * (be.Radius + 1f); name = be.PingName; col = new Color(1f, 0.32f, 0.3f); }
        else if (bo != null) { pingPos = bo.GlobalPosition + Vector3.Up * 1.8f; name = bn; col = bc; }
        else
        {
            var hit = origin + dir * (dir.Y < -0.02f ? Mathf.Clamp(-origin.Y / dir.Y, 3f, 90f) : 45f);
            pingPos = new Vector3(hit.X, Game.I.SurfaceHeight(hit, 1e9f) + 0.15f, hit.Z);
            name = ""; col = new Color(0.5f, 0.82f, 1f);
        }
        Game.I.SpawnPing(pingPos, name, col);
        Game.I.Sfx?.Clink();   // local ping blip
    }

    public override void _Input(InputEvent e)
    {
        if (e is InputEventMouseButton pmb && pmb.Pressed && pmb.ButtonIndex == MouseButton.Middle && Game.I != null && Game.I.CanControlLocal()) { DoPing(); return; }   // (NEW) middle-click ping
        if (e is InputEventMouseButton mb && mb.Pressed && Input.MouseMode != Input.MouseModeEnum.Captured && Game.I.State == GameState.Playing && !Game.I.ConsoleOpen)
        { Input.MouseMode = Input.MouseModeEnum.Captured; return; }
        if (e is InputEventMouseMotion mm && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            RotateY(-mm.Relative.X * MouseSens);
            _pitch = Mathf.Clamp(_pitch - mm.Relative.Y * MouseSens, -1.4f, 1.4f);
            _cam.Rotation = new Vector3(_pitch, 0, 0);
        }
        // fire finishers by their bound key
        if (e is InputEventKey k && k.Pressed && !k.Echo && Game.I != null && Game.I.CanControlLocal())
        {
            for (int i = 0; i < Fin.Count; i++)
                if (Fin[i].Bind != Key.None && k.PhysicalKeycode == Fin[i].Bind) { FireFinisher(i); break; }
        }
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        if (_iframe > 0) _iframe -= dt;   // (FIX) always decay iframes — opening the console or being grabbed must NOT freeze immunity
        if (Game.I != null && !Game.I.CanControlLocal()) { if (_frostSeg != null) EndFrostBeam(); if (_curseSeg != null) EndCurseBeam(); return; }
        if (GodMode) { Hp = Mathf.Min(S.MaxHp, Hp + S.MaxHp * 3f * dt); Mana = S.ManaMax; }   // (NEW) dev god mode: takes hits (numbers show) but fast-regens + never dies
        if (GrabbedBy != 0)   // (NEW) held by a Taker: stunned + carried in its grasp
        {
            var t = Game.I.EnemyByNetId(GrabbedBy);
            if (t == null || t.Dead) GrabbedBy = 0;
            else
            {
                StunT = Mathf.Max(StunT, 0.25f);
                if (Game.I.IsAuthority) GlobalPosition = t.GraspPos;   // host/solo owns the Taker → snap here; clients get pos from the host (ReceiveGrabPos)
                _vy = 0f;
                return;   // no other movement/action while held
            }
        }
        if (_fireCd > 0) _fireCd -= dt;
        if (ManaFlash > 0) ManaFlash -= dt;
        if (ProcFlash > 0) ProcFlash -= dt;
        if (HealFlash > 0) HealFlash -= dt;
        UpdateAura();
        FlushHealPopup(dt);
        if (Input.IsActionJustPressed("release_mouse")) Input.MouseMode = Input.MouseModeEnum.Visible;

        AnimateHands(dt);
        if (Game.I != null && Game.I.CanControlLocal()) DrawCurseTethers();   // (NEW) tethers persist + show on every machine (synced group)
        if (_bodyModel != null)
        {
            var mv = GlobalPosition - _prevBodyPos; mv.Y = 0f; _prevBodyPos = GlobalPosition;
            float sp = Mathf.Clamp(mv.Length() / Mathf.Max(dt, 1e-4f) / Mathf.Max(1f, S.Speed), 0f, 1f);
            _bodyModel.Animate(dt, sp, !_grounded);
        }

        if (_camKick > 0f) _camKick = Mathf.Max(0f, _camKick - dt * 4.5f);
        _cam.Fov = BaseFov + _camKick * 9f - (FrostWitch && Charging ? ChargeAmt * 34f : 0f);   // (NEW) Frost sniper: zoom in on the cursor as she draws
        _cam.Position = new Vector3(0, 2.6f, _camKick * 0.18f);

        if (Game.I == null || !Game.I.CanControlLocal())
        {
            if (_beamSeg != null) { _beamSeg.Free(); _beamSeg = null; _beamLight = null; _beamT = 0; }
            return;
        }

        // shields & resonance
        MaxShield = S.MaxHp * S.ShieldPct;
        if (ShieldSuppress > 0f) ShieldSuppress = Mathf.Max(0f, ShieldSuppress - dt);
        if (_combatT < 1e6f) _combatT += dt;
        bool outOfCombat = _combatT >= 5f || (Game.I != null && Game.I.Enemies.Count == 0);   // no recent damage, or no enemies left (NEW)
        if (Shield < MaxShield && ShieldSuppress <= 0f)
        {
            if (outOfCombat)
                Shield = Mathf.Min(MaxShield, Shield + (MaxShield * 0.5f) * dt);   // out of combat: rushes back (~2s to full), ignoring the post-hit delay (NEW)
            else if (_shieldT > 0f) _shieldT -= dt;
            else Shield = Mathf.Min(MaxShield, Shield + S.ShieldRegen * dt);       // in combat: the slow trickle once the delay elapses
        }
        else if (_shieldT > 0f) _shieldT -= dt;


        if (Combo > 0 && Now - ComboT > S.ComboWindow) { Combo = 0; _lastAct = ComboAct.None; }
        if (FreshT > 0f) { FreshT -= dt; if (FreshT <= 0f) FreshHit = false; }
        FireHeat = Mathf.Max(0f, FireHeat - dt * 1.2f);
        if (HurtT > 0f) HurtT -= dt;
        if (BlessedT > 0f) BlessedT -= dt;
        if (_noFall > 0f) _noFall -= dt;
        if (DashT > 0f) DashT -= dt;
        if (DmgDirT > 0f) DmgDirT -= dt;
        UpdateUlt(dt);
        if (_srcComboCd > 0f) _srcComboCd -= dt;
        if (_dotComboCd > 0f) _dotComboCd -= dt;   // (NEW) DoT-driven combo throttle
        foreach (var f in Fin) { if (f.NotReadyFlash > 0f) f.NotReadyFlash -= dt; if (f.Armed && f.Type != FinType.CrimsonRush) { f.Window -= dt; if (f.Window <= 0) { f.Armed = false; f.Charge = 0; } } }

        if (_beamT > 0) UpdateBeam(dt);

        if (StunT > 0f)
        {
            StunT -= dt;        // stunned: no movement, dashing, casting, or finishers
            return;
        }

        if (Divinity)
        {
            _divT -= dt;
            Floating = false; _bodyModel?.ShowWings(false);
            _iframe = Mathf.Max(_iframe, 0.2f);                         // unkillable while ascended
            float targetY = _divBaseY + 12f;
            GlobalPosition = new Vector3(GlobalPosition.X, Mathf.MoveToward(GlobalPosition.Y, targetY, 16f * dt), GlobalPosition.Z);
            if (Input.IsActionPressed("cast") && _fireCd <= 0f) { FireDivinityMote(); _fireCd = Mathf.Max(0.2f, S.FireCd * 1.25f); }
            if (_divT <= 0f) { Divinity = false; UltActive = false; _iframe = 0.3f; _noFall = 3.0f; _divFalling = true; }   // immortal + no fall damage until she lands
            return;
        }

        if (HurricaneActive) { UpdateHurricane(dt); return; }   // aloft, steering the storm (NEW)
        if (LifeDrainActive) { UpdateLifeDrain(dt); return; }   // aloft, draining — free flight, then the release burst (NEW)
        if (_galeDiving) { UpdateGaleDive(dt); return; }   // Gale air-slam: rocket to the aimed spot, then slam (NEW)
        if (_meteorAscend) { UpdateMeteorAscend(dt); return; }   // (NEW) Ember ult: suspended, aiming the landing zone
        if (PhoenixActive) { UpdatePhoenix(dt); return; }        // (NEW) Ember ult: free phoenix flight + immolation aura

        if (_rushT > 0f)
        {
            _rushT -= dt;
            float step = _rushDist * (Mathf.Min(dt, _rushDur) / _rushDur);
            GlobalPosition = ClampPos(GlobalPosition + _rushDir * step);
            if (_rushWind) { _windPuffCd -= dt; if (_windPuffCd <= 0f) { SpawnWindPuff(GlobalPosition, _rushDir); _windPuffCd = 0.05f; } }   // wind trail (NEW)
            if (_rushT <= 0f) _rushWind = false;
        }
        else if (_flameDashT > 0f)   // (NEW) Wildfire Rush: a long flame dash, laying its trail as it goes
        {
            _flameDashT -= dt;
            float step = _flameDashDist * (Mathf.Min(dt, _flameDashDur) / _flameDashDur);
            GlobalPosition = ClampPos(GlobalPosition + _flameDashDir * step);
            Game.I.SpawnFlameCone(GlobalPosition, _flameDashDir, 3f, DamageTypes.Col(DamageType.Ember));
        }
        else if (_dashT > 0f)
        {
            _dashT -= dt;
            float step = S.DashDist * (Mathf.Min(dt, DashDur) / DashDur);
            GlobalPosition = ClampPos(GlobalPosition + _dashDir * step);
        }
        else
        {
            if (Input.IsActionJustPressed("dash") && DashStock > 0 && _snareT <= 0f && !_inWaterBody) StartDash();   // dash allowed mid-air, but not while rooted or wading/swimming — jump out of the water first (NEW)
            Move(dt);
        }
        if (_snareT > 0f) _snareT -= dt;
        if (_slowT > 0f) _slowT -= dt;
        if (Input.IsActionJustPressed("jump") && _jumps > 0) { _vy = JumpVel * S.JumpMul * (GaleWitch ? 1.1f : 1f); _jumps--; _grounded = false; if (Game.I.InWater(GlobalPosition, GlobalPosition.Y)) Game.I.WaterDisturb(GlobalPosition, 0.8f); Game.I.GlowFlowersNear(GlobalPosition, 2.4f); Game.I.PlayerSound(GlobalPosition, 0.5f); }   // Gale: +10% jump; splash off water + stir flowers on takeoff; quiet noise (NEW)
        Floating = !_grounded && _vy < 0f && Input.IsActionPressed("jump");   // hold Space while falling → glide (Move() already gives air steering)
        UpdateVertical(dt, Floating);
        _bodyModel?.ShowWings(Floating);
        if (DashStock < S.DashCharges)
        {
            if (DashCdT <= 0) DashCdT = S.DashCd;
            DashCdT -= dt;
            if (DashCdT <= 0) { DashStock++; DashCdT = DashStock < S.DashCharges ? S.DashCd : 0; }
        }

        if (_flareCd > 0f) _flareCd -= dt;
        if (_killProcCd > 0f) _killProcCd -= dt;   // (NEW) age the on-kill legendary throttle
        if (GaleWitch && Airborne && Game.I.State == GameState.Playing) Game.I.MyStats.Highlight += dt;   // (NEW) Gale highlight = seconds aloft
        if (!Downed && Game.I.CanControlLocal() && Input.IsPhysicalKeyPressed(Key.T) && _flareCd <= 0f) { FireFlare(); _flareCd = 2f; }   // (NEW) hold T → firework flare

        Combat(dt);
    }

    private float _flareCd = 0f;
    private void FireFlare()
    {
        var col = WitchModel.WitchColor(WitchIndex);
        var flat = AimDir(); flat.Y = 0; flat = flat.LengthSquared() > 0.001f ? flat.Normalized() : Vector3.Forward;
        var from = GlobalPosition + new Vector3(0, 1.35f, 0) + flat * 0.6f;   // roughly from the outstretched hand
        SetArm("flare", 0.8f);
        var fw = new Firework(); Game.I.AddChild(fw); fw.Init(from, col);
        Game.I.PlayerSound(from, 4f);                                        // loudest — a firework wakes the maze
        Game.I.NetMgr?.BroadcastVfx(36, from, Vector3.Up, 0f, 0f, col);       // allies see the firework
        Game.I.DropFireworkWisp(GlobalPosition, DamageTypes.Col(WitchDamage));   // (NEW) phase-2 guide wisp toward the portal
        Game.I.AddMinimapBlip(GlobalPosition, DamageTypes.Col(WitchDamage)); // (NEW) minimap ping so allies triangulate you
    }

    public DamageType WitchDamage => WitchIndex switch { 1 => DamageType.Holy, 2 => DamageType.Blood, 3 => DamageType.Nature, 4 => DamageType.Wind, 5 => DamageType.Frost, 6 => DamageType.Curse, 7 => DamageType.Ember, _ => DamageType.Lunar };

    private Vector3 InputDir()
    {
        Vector2 iv = Input.GetVector("move_left", "move_right", "move_forward", "move_back");
        Vector3 fwd = -GlobalTransform.Basis.Z; fwd.Y = 0;
        Vector3 right = GlobalTransform.Basis.X; right.Y = 0;
        if (fwd.LengthSquared() > 0) fwd = fwd.Normalized();
        if (right.LengthSquared() > 0) right = right.Normalized();
        Vector3 dir = right * iv.X - fwd * iv.Y;
        return dir.LengthSquared() > 0.001f ? dir.Normalized() : Vector3.Zero;
    }

    private void Move(float dt)
    {
        if (_snareT > 0f) return;   // rooted by a hexer
        Vector3 dir = InputDir();
        float spd = S.Speed * (_beamT > 0 ? 0.5f : 1f) * StormSpeedMul * WindBoonSpeedMul * SlowMul;   // StormSpeedMul = Stormform; WindBoonSpeedMul = Eyewall; SlowMul = swarmer hits (NEW)
        // water state: how far the bottom sits below the still surface right here (NEW)
        float wterrain = Game.I.SurfaceHeight(GlobalPosition, GlobalPosition.Y);
        float wdepth = World.WaterLevel - wterrain;
        bool deepWater = wdepth > WaterFloatDepth;
        _inWaterBody = wdepth > WaterWadeMin && GlobalPosition.Y <= World.WaterLevel + 0.1f;   // feet actually in the water, not on a bridge above it (NEW)
        bool wading = _inWaterBody;
        if (wading) spd *= deepWater ? 0.45f : 0.6f;   // swimming drags harder than wading (NEW)
        if (dir != Vector3.Zero)
        {
            GlobalPosition = ClampPos(GlobalPosition + dir * spd * dt);
            if (wading)
            {
                Game.I.MaybeWaterTrail(GlobalPosition, GlobalPosition.Y, dt);   // silent ripple trail as you wade (NEW)
                _wadeSndCd -= dt;
                if (_wadeSndCd <= 0f) { Game.I.Sfx?.WadeAt(GlobalPosition); _wadeSndCd = 0.6f + GD.Randf() * 0.25f; }   // soft, infrequent, jittered wade swish — NOT a splash (NEW)
            }
            _flowerGlowCd -= dt;
            if (_flowerGlowCd <= 0f) { Game.I.GlowFlowersNear(GlobalPosition, 2.2f); _flowerGlowCd = 0.12f; }   // flowers light up as you brush past (NEW)
        }
    }

    private void StartDash()
    {
        Vector3 dir = InputDir();
        if (dir == Vector3.Zero) { dir = -GlobalTransform.Basis.Z; dir.Y = 0; dir = dir.Normalized(); }
        _dashDir = dir; _dashT = DashDur; DashStock--;
        DashT = 1f;
        if (DashCdT <= 0) DashCdT = S.DashCd;
        _iframe = Mathf.Max(_iframe, 0.26f);
        if (GaleWitch) _galeGuard = 0.8f;   // Tailwind: brief damage reduction right after dashing (NEW)
    }

    private Vector3 ClampPos(Vector3 p)
    {
        foreach (var b in Game.I.Blockers)
        {
            var off = new Vector2(p.X - b.Pos.X, p.Z - b.Pos.Z);
            float dd = off.Length();
            if (dd < b.Radius + 1.0f) { float k = (b.Radius + 1.0f) / Mathf.Max(dd, 0.001f); p.X = b.Pos.X + off.X * k; p.Z = b.Pos.Z + off.Y * k; }
        }
        // raised platforms are solid from the sides (but walkable on top): push out if we're below the top
        foreach (var d in Game.I.Decks)
        {
            if (GlobalPosition.Y >= d.TopY - 0.6f) continue;   // standing on/near the top — let us walk to the edge
            float ex = d.Half.X + 0.9f, ez = d.Half.Y + 0.9f;
            float dx = p.X - d.Center.X, dz = p.Z - d.Center.Z;
            if (Mathf.Abs(dx) < ex && Mathf.Abs(dz) < ez)
            {
                if (ex - Mathf.Abs(dx) < ez - Mathf.Abs(dz)) p.X = d.Center.X + Mathf.Sign(dx) * ex;
                else p.Z = d.Center.Z + Mathf.Sign(dz) * ez;
            }
        }
        return p;   // Y preserved — vertical handled by UpdateVertical
    }

    // ---- jumping & gravity (floaty) ----
    public const int MaxJumps = 2;
    public int JumpsMax => MaxJumps + (GaleWitch ? 1 : 0);   // Tailwind: the Gale witch gets a 3rd jump (NEW)
    private const float Gravity = -18f, JumpVel = 8.5f, FallHurtSpeed = 16f;
    private const float GroundSnap = 0.6f;        // stick to slopes/steps so walking a hill never reads as "airborne" (NEW)
    private const float WaterWadeMin = 0.35f;     // water this deep+ counts as being in the water (slow, no dash, not airborne) (NEW)
    private const float WaterFloatDepth = 1.5f;   // deeper than this and we can't stand → float at the surface (NEW)
    private const float WaterNeck = 1.3f;         // when floating, feet ride this far below the surface (waterline ~chest/neck) (NEW)
    private bool _inWaterBody = false;            // currently wading or floating in water (NEW)
    private float _vy = 0f;
    public bool Floating = false;       // gliding (hold Space while falling) — drives wings + no fall damage
    public float _snareT = 0f;          // hexer root: can't move while > 0
    public void SnareMe(float dur) { if (!Downed) _snareT = Mathf.Max(_snareT, dur); }
    private float _slowT = 0f, _slowMul = 1f;
    private float SlowMul => _slowT > 0f ? _slowMul : 1f;
    public void SlowMe(float dur, float mul) { if (!Downed) { _slowMul = mul; _slowT = Mathf.Max(_slowT, dur); } }   // swarmer hits (NEW)
    private int _jumps = MaxJumps;
    private bool _grounded = true;
    private float _flowerGlowCd = 0f;   // throttle for lighting flowers while walking (NEW)
    private float _wadeSndCd = 0f;       // throttle for the soft wade swish in water (NEW)
    public bool Airborne => !_grounded;

    // Gale "Downburst" legendary: a Wind shockwave on landing — damage + a back-fling, scaling with how
    // hard she hit the ground. Routes through StormForce so it's host-authoritative / client-safe. (NEW)
    private void DownburstSlam(Vector3 pos, float impact)
    {
        float scale = Mathf.Clamp(impact / 16f, 0.4f, 2.0f);
        float radius = (6f + scale * 3f) * S.SpellArea;
        float dmg = Base() * (0.8f * scale);
        Game.I.NetMgr?.StormForce(pos, radius, 2, dmg);                 // shockwave damage
        Game.I.NetMgr?.StormForce(pos, radius, 3, 6f + scale * 4f);     // fling foes back (mass-scaled)
        Game.I.MyStats.Flings += Game.I.CountFlungNear(pos, radius);   // (NEW) tally enemies flung
        Game.I.NetMgr?.BroadcastVfx(6, pos, Vector3.Up, radius, 0f, DamageTypes.Col(DamageType.Wind));
        Ring(pos, DamageTypes.Col(DamageType.Wind), radius, 0.4f);
        Ring(pos, DamageTypes.Col(DamageType.Wind).Lerp(Colors.White, 0.4f), radius * 0.6f, 0.3f);
        CamKick(0.4f);
        Game.I.Sfx?.Impact(DamageType.Wind);
    }

    // launched by a Whirlwind jump-pad (WindPad): a big upward boost, jumps refilled for air combos, and a
    // grace window so the boosted descent doesn't self-inflict fall damage. (NEW)
    public void WindLaunch(float vy)
    {
        if (Downed || StunT > 0f) return;
        _vy = vy; _grounded = false; _jumps = JumpsMax;
        _noFall = Mathf.Max(_noFall, 2.5f);
    }

    private void UpdateVertical(float dt, bool floating = false)
    {
        if (_galeHover)   // Gale: charging a punch in mid-air holds her height (super-slow sink) so she can aim the dive (NEW)
        {
            _vy = -0.3f;
            _noFall = Mathf.Max(_noFall, 0.2f);
        }
        else
        {
            _vy += Gravity * dt;
            if (floating) { _vy = Mathf.Max(_vy, -2.0f); _noFall = Mathf.Max(_noFall, 0.2f); }   // slow glide, no fall damage
        }
        float ny = GlobalPosition.Y + _vy * dt;
        float terrain = Game.I.SurfaceHeight(GlobalPosition, GlobalPosition.Y);
        float wdepth = World.WaterLevel - terrain;                            // >0 = standing water over the bottom here (NEW)
        bool floatHere = wdepth > WaterFloatDepth;                            // too deep to stand → buoyancy holds us at the surface (NEW)
        float ground = floatHere ? (World.WaterLevel - WaterNeck) : terrain;  // rest at the neckline in deep water, else on the ground (NEW)
        bool snap = _grounded && _vy <= 0.05f && ny <= ground + GroundSnap;   // already grounded + walking → stick to the slope (NEW: was a hard ny<=ground, which flickered airborne on hills)
        if (ny <= ground || snap)
        {
            float impactDesc = -_vy;   // descent speed at the instant of landing (NEW: used by Downburst)
            bool wasAir = !_grounded;   // only splash on the actual touchdown, not every resting frame (NEW)
            if (_vy < -FallHurtSpeed && !_grounded && _noFall <= 0f && wdepth <= WaterWadeMin)   // water cushions the fall — no fall damage when landing in it (NEW)
            {
                float impact = -_vy - FallHurtSpeed;          // only the excess past the threshold hurts
                Hurt(Mathf.Min(35f, impact * 2.2f), null);    // gentle fall damage, capped
            }
            ny = ground; _vy = 0f; _jumps = JumpsMax; _grounded = true;   // refill jumps (3 for Gale); grounded even when floating so air-mods (Cloudfeather/Jetstream) don't fire in water (NEW)
            if (wasAir && impactDesc > 3f && Game.I.InWater(new Vector3(GlobalPosition.X, ny, GlobalPosition.Z), ny))
                Game.I.WaterDisturb(new Vector3(GlobalPosition.X, ny, GlobalPosition.Z), Mathf.Clamp(impactDesc * 0.06f, 0.6f, 1.6f));   // splash only on a real fall/jump landing, not stepping downhill underwater (NEW)
            _divFalling = false;   // landed — Divinity protection ends here
            if (Downburst && impactDesc >= 10f && wdepth <= WaterWadeMin)   // Gale legendary: a real landing slams a Wind shockwave (not when splashing into water) (NEW)
                DownburstSlam(new Vector3(GlobalPosition.X, ground, GlobalPosition.Z), impactDesc);
        }
        else _grounded = false;
        GlobalPosition = new Vector3(GlobalPosition.X, ny, GlobalPosition.Z);
    }

    private void Combat(float dt)
    {
        if (_beamT > 0) { Charging = false; ChargeAmt = 0; return; }
        if (HurricaneActive) { Charging = false; ChargeAmt = 0; return; }   // piloting the storm — no casting (NEW)

        if (GaleWitch) { UpdateGaleCharge(dt); }   // Gale: chargeable ground/air slam punch (NEW)
        else if (EmberWitch) { UpdateEmberCharge(dt); }   // (NEW) Ember: chargeable aimed meteor (ground aim ring under the reticle)
        else if (Input.IsActionPressed("charge") && !(UltActive && Ult == UltKind.Crescent))
        {
            if (!_charging) { _charging = true; _charge = 0f; Game.I.Sfx?.ChargeUp(SecondaryType); }
            _charge = Mathf.Min(1f, _charge + Mathf.Min(S.ChargeSpeed, 2.5f) * dt * WindBoonChargeMul);   // rate-capped; Eyewall speeds the fill (NEW)
        }
        else if (_charging)
        {
            _charging = false;
            if (_charge > 0.12f || (ForsakenWitch && _charge > 0.02f))
            {
                bool canFire = true;
                if (CrimsonWitch)
                {
                    float hc = S.MaxHp * 0.04f;                      // blood pays a slice of HP
                    if (Hp <= hc + 1f) { canFire = false; ResFail(); }
                    else { Hp -= hc; HurtT = 0.2f; }
                }
                else
                {
                    if (Mana < 0.5f) { canFire = false; ResFail(); }   // half a mana to release
                    else { Mana -= 0.5f; _chargedRefund = true; }        // refunds a full 1 when it connects
                }
                if (canFire)
                {
                    if (CrimsonWitch)
                    {
                        ConsumeBloodStacks(_charge);                 // banked Blood Stacks heal on release
                        if (_charge >= 0.95f) AddBloodStack(1f);     // a full-charge hold banks a stack for next time
                    }
                    if (SecondaryType == DamageType.Holy) FireHolyRay(_charge);
                    else if (SecondaryType == DamageType.Blood) FireCrimsonTide(_charge);
                    else if (SecondaryType == DamageType.Frost) FireIcicleSpear(_charge);
                    else if (SecondaryType == DamageType.Curse) FireVoodooCrush(_charge);
                    else FireBolt(_charge);
                    _fireCd = Mathf.Max(0.12f, S.FireCd) * 0.5f * StormFireMul * WindBoonFireMul;   // Stormform + Eyewall cast speed (NEW)
                    _kickL = 1; _kickR = 1;
                    FireHeat = Mathf.Min(1f, FireHeat + 0.14f);
                    Game.I.Sfx?.Release(SecondaryType);
                    CamKick(_charge >= 0.95f ? 0.75f : 0.2f + 0.4f * _charge);
                }
            }
            _charge = 0f;
        }
        Charging = _charging; ChargeAmt = _charge;

        if (FrostWitch)   // (NEW) primary = a channeled freezing beam (hold left-click)
        {
            if (!_charging && Input.IsActionPressed("cast") && !(UltActive && Ult == UltKind.Crescent)) UpdateFrostBeam(dt);
            else EndFrostBeam();
        }
        else if (ForsakenWitch)   // (NEW) primary = a lock-on curse-suck beam (hold left-click)
        {
            if (!_charging && Input.IsActionPressed("cast") && !(UltActive && Ult == UltKind.Crescent)) UpdateCurseBeam(dt);
            else EndCurseBeam();
        }
        else if (EmberWitch)   // (NEW) primary = a channeled flame cone (hold left-click) — ticks faster with cast speed
        {
            if (!_charging && Input.IsActionPressed("cast") && !(UltActive && Ult == UltKind.Crescent)) UpdateFlameCone(dt);
            else EndFlameCone();
        }
        else if (!_charging && Input.IsActionPressed("cast") && _fireCd <= 0f && !(UltActive && Ult == UltKind.Crescent))
        {
            FireBolt(0f);
            _fireCd = Mathf.Max(0.12f, S.FireCd) * StormFireMul * WindBoonFireMul;   // Stormform + Eyewall cast speed (NEW)
            FireHeat = Mathf.Min(1f, FireHeat + 0.09f);
            Game.I.Sfx?.Cast(PrimaryType);
            if ((_fireHand = 1 - _fireHand) == 0) _kickL = 1; else _kickR = 1;
        }
    }

    private void FireBolt(float charge)
    {
        Game.I.PlayerSound(GlobalPosition, 0.7f);   // primary fire noise
        bool isNormal = charge < 0.12f;
        bool full = charge >= 0.95f;
        Vector3 camFwd = -_cam.GlobalTransform.Basis.Z;
        var dtype = isNormal ? PrimaryType : SecondaryType;
        var baseCol = DamageTypes.Col(dtype);

        float dmg, radius; int pierce;
        if (isNormal)
        {
            dmg = Base() * 0.5f;                 // light, fast harass that builds the combo
            radius = 0.4f; pierce = 0;
        }
        else
        {
            float chargeMul = 1f + charge * (S.MaxCharge * 1.6f - 1f);   // ramps to ~4.8x at full charge
            dmg = Base() * chargeMul * ComboMul();                       // the combo you built is unleashed here
            radius = 0.5f;                                               // focused bolt — no splash unless a modifier adds it
            pierce = S.Pierce;                                           // pierce comes only from upgrades/modifiers, not from charging
        }
        var tint = full ? baseCol.Lerp(Colors.White, 0.4f) : baseCol;
        if (dtype == DamageType.Lunar && LunarBonus > 0f)   // Nightfall's Gift — doubled while it's night
            dmg *= 1f + LunarBonus * (Game.I != null && Game.I.IsNight ? 2f : 1f);

        // Blood primary = a short-range whip/arc lash (melee), not a projectile
        if (dtype == DamageType.Blood && isNormal)
        {
            FireBloodLash(dmg, tint);
            return;
        }

        // Nature primary = a burst of thin PURPLE NEEDLES (poison), fast & weak; rate-limited by cast speed
        if (dtype == DamageType.Nature && isNormal)
        {
            var pur = new Color(0.62f, 0.26f, 0.85f);   // purple denotes poison
            var fwd = camFwd.Normalized();
            var right = new Vector3(fwd.Z, 0, -fwd.X).Normalized();
            var up = right.Cross(fwd).Normalized();
            const int needles = 3;
            float per = dmg * 0.30f;                     // (NERF 0.42→0.30) scaled down — the Grove/ents should carry her DPS, not the primary
            for (int i = 0; i < needles; i++)
            {
                float sx = (GD.Randf() - 0.5f) * 0.085f, sy = (GD.Randf() - 0.5f) * 0.07f;
                var d = (fwd + right * sx + up * sy).Normalized() * 56f;
                SpawnBolt(_cam.GlobalPosition + camFwd * 1.2f, d, per, pierce, 0.16f, pur, dtype,
                    normal: true, charged: false, combo: i == 0, full: false, poisonDps: PoisonDps() * 0.5f, style: 1);
            }
            return;
        }
        if (dtype == DamageType.Nature && !isNormal) { FireThorn(charge); return; }

        // Wind (Gale witch): the primary is a frontal-arc PUNCH (mirrors the blood lash's shape) with a
        // wind-fist visual + thrust pose. The charged release is handled by UpdateGaleCharge → FireSlam,
        // so a charged Wind FireBolt shouldn't occur; guard it just in case. (NEW)
        if (dtype == DamageType.Wind && isNormal) { FireWindPunch(dmg); return; }
        if (dtype == DamageType.Wind && !isNormal) { FireSlam(charge, GlobalPosition); return; }

        // Holy primary = a light mote. Locks onto whatever the cursor was over; otherwise flies straight.
        if (dtype == DamageType.Holy && isNormal)
        {
            bool radiant = RadiantMote && Airborne;   // the airborne heal-through only comes online while she's aloft
            var tgt = AimTarget();
            Vector3 origin = _cam.GlobalPosition + camFwd * 1.2f;
            Vector3 dir = camFwd.Normalized();
            if (tgt == null && radiant && AimAllyPos(out var allyPos))   // no foe aimed → lock onto an ally to mend them (and hit whatever's behind)
                dir = (allyPos - origin).Normalized();
            Bolt mote;
            if (tgt != null)
            {
                mote = SpawnBolt(origin, dir * 44f, dmg, pierce, 0.45f, tint, dtype, normal: true, charged: false, combo: true, full: false, homing: true);
                mote.Target = tgt; mote.SeekLockedOnly = true; mote.HomeSpeed = 44f; mote.Turn = 7f; mote.HomeDelay = 0.05f;
            }
            else
            {
                mote = SpawnBolt(origin, dir * 48f, dmg, pierce, 0.45f, tint, dtype, normal: true, charged: false, combo: true, full: false, homing: false);
            }
            if (radiant) { mote.RadiantHeal = true; mote.HealAmt = 0.4f + 0.2f * Mathf.Clamp(Combo, 0, 30); }   // mends allies it passes; base 0.4 HP, more with combo
            return;
        }

        // Lunar full-charge: a wide horizontal crescent that grows as it flies, cleaving multiple foes
        if (dtype == DamageType.Lunar && full)
        {
            SpawnBolt(_cam.GlobalPosition + camFwd * 1.2f, camFwd.Normalized() * 30f, dmg * 1.15f,
                Mathf.Max(S.Pierce, 6) + CrescentPierceBonus, 1.1f * CrescentSizeMul, tint, dtype,
                normal: false, charged: true, combo: true, full: true,
                homing: false, life: 1.9f, fromCombo: false, horizontal: true, grow: 2.6f * CrescentSizeMul);
            Game.I.SpawnGroundSigil(GlobalPosition, 4.5f * S.SpellArea, baseCol);   // (NEW) lunar sigil flares under her — full charge only
            return;
        }

        SpawnBolt(_cam.GlobalPosition + camFwd * 1.2f, camFwd.Normalized() * 50f, dmg,
            pierce, radius, tint, dtype,
            normal: isNormal, charged: !isNormal, combo: true, full: full && !isNormal);

        // Crescendo split (driven by the equipped passive Crescendo finisher)
        int splitEvery = CrescendoEvery();
        if (isNormal && splitEvery > 0 && Combo > 0 && Combo % splitEvery == 0)
        {
            var right = new Vector3(-camFwd.Z, 0, camFwd.X).Normalized();
            foreach (float off in new[] { -0.14f, 0.14f })
            {
                var d = (camFwd + right * off).Normalized() * 50f;
                SpawnBolt(_cam.GlobalPosition + camFwd * 1.2f, d, dmg * 0.8f, 0, 0.4f, baseCol, PrimaryType, true, false, false, false);
            }
        }
    }

    private Bolt SpawnBolt(Vector3 pos, Vector3 vel, float dmg, int pierce, float radius, Color tint, DamageType dtype,
        bool normal, bool charged, bool combo, bool full, bool homing = false, float life = 1.6f, bool fromCombo = false,
        bool horizontal = false, float grow = 0f, float poisonDps = 0f,
        int style = 0, float rootOnHit = 0f, bool detonatesEnts = false)
    {
        bool crit = RollCrit();
        if (crit) { dmg *= CritMult(); tint = tint.Lerp(Colors.White, 0.5f); }   // crit: direct projectiles
        life *= S.SpellRange;   // spell range extends projectile travel
        radius *= S.SpellArea;  // spell area grows the splash/blast radius (applies to every witch's bolts uniformly)
        vel *= S.ProjSpeed;     // projectile-speed stat scales every non-hitscan bolt uniformly
        var b = new Bolt { Src = this, Vel = vel, Dmg = dmg, Crit = crit, Pierce = pierce, Radius = radius, Tint = tint, DType = dtype,
            Normal = normal, Charged = charged, ComboShot = combo, Full = full, Homing = homing, Life = life, FromCombo = fromCombo,
            Horizontal = horizontal, Grow = grow, Poison = poisonDps, Style = style, RootOnHit = rootOnHit, DetonatesEnts = detonatesEnts, SpeedMul = S.ProjSpeed };
        Game.I.AddChild(b); b.GlobalPosition = pos;
        Game.I.NetMgr?.BroadcastPBolt(pos, vel, radius, tint, (int)dtype, life, horizontal, grow, style);   // allies see the shot
        return b;
    }

    // the enemy most aligned with the crosshair (for the Holy seeking mote / lock-on)
    private Enemy AimTarget()
    {
        Vector3 o = _cam.GlobalPosition, f = (-_cam.GlobalTransform.Basis.Z).Normalized();
        Enemy best = null; float bestDot = 0.992f;   // ~7-degree cone (tighter than the old 15-degree)
        foreach (var e in Game.I.Enemies.ToArray())
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
            var to = e.GlobalPosition - o; float d = to.Length();
            if (d < 0.5f || d > 70f) continue;
            float dot = f.Dot(to / d);
            if (dot > bestDot) { bestDot = dot; best = e; }   // most-centered enemy within the cone
        }
        return best;
    }

    // (NEW) Radiant Ascension: find the ally most centered in the aim cone, so an airborne Divine can lock her healing mote onto them.
    private bool AimAllyPos(out Vector3 pos)
    {
        pos = Vector3.Zero;
        var net = Game.I.NetMgr;
        if (net == null || !net.Active) return false;
        Vector3 o = _cam.GlobalPosition, f = (-_cam.GlobalTransform.Basis.Z).Normalized();
        float bestDot = 0.985f; bool found = false;
        foreach (var ap in net.AllyPositions())
        {
            var aim = ap + Vector3.Up * 0.9f;
            var to = aim - o; float d = to.Length();
            if (d < 0.5f || d > 70f) continue;
            float dot = f.Dot(to / d);
            if (dot > bestDot) { bestDot = dot; pos = aim; found = true; }
        }
        return found;
    }

    // Blood Lash: a fast short-range arc swipe in front of the witch.
    // (NEW) Where a melee AIM RAY meets an enemy — a ray-sphere ENTRY point, so the mark lands where the cursor is
    // pointing on the target (head/body/etc.), not always at its centre/feet. Falls back to the nearest surface point
    // for enemies caught in the arc but off the exact aim line. This is the hit-registration groundwork for future
    // always-crit hit-spots (head/core).
    // Where along an enemy's body the CROSSHAIR is pointing when the melee lands — used so slash/punch marks
    // sit at the height you actually hit (aim at the head → mark high; at the legs → low), not pinned to centre.
    // Enemies are represented by a sphere at GlobalPosition, but they're taller than that, so we take the aim
    // ray's HEIGHT at the enemy's distance (clamped to a plausible body band) rather than a sphere hit. This is
    // the groundwork for future crit-spots (head/core) — the vertical hit position is a real aim result.
    private void AimHitOnEnemy(Vector3 o, Vector3 fwd, Enemy e, out Vector3 hitPos, out Vector3 nrm)
    {
        float along = Mathf.Max(0.3f, (e.GlobalPosition - o).Dot(fwd));
        Vector3 rayPt = o + fwd * along;                       // the aim ray at the enemy's distance
        float hy = Mathf.Clamp(rayPt.Y, e.GlobalPosition.Y - e.Radius, e.GlobalPosition.Y + e.Radius * 2.4f);   // stay on the body
        Vector3 flat = new Vector3(rayPt.X - e.GlobalPosition.X, 0f, rayPt.Z - e.GlobalPosition.Z);             // which side you struck
        flat = flat.LengthSquared() > 0.0001f ? flat.Normalized() : new Vector3(-fwd.X, 0f, -fwd.Z).Normalized();
        hitPos = new Vector3(e.GlobalPosition.X + flat.X * e.Radius, hy, e.GlobalPosition.Z + flat.Z * e.Radius);
        nrm = new Vector3(flat.X, 0.15f, flat.Z);
        nrm = nrm.LengthSquared() > 0.0001f ? nrm.Normalized() : Vector3.Up;   // faces outward toward the attacker
    }

    // (NEW) Where a melee AIM RAY meets the terrain within reach — so ground marks appear only where the swing
    // actually meets the ground. Aiming up or level into the air returns false (nothing drops on the ground).
    private bool AimGroundHit(Vector3 o, Vector3 fwd, float reach, out Vector3 hit)
    {
        hit = Vector3.Zero;
        for (float t = 0.5f; t <= reach; t += 0.6f)
        {
            Vector3 p = o + fwd * t;
            float gy = Game.I.SurfaceHeight(p, p.Y);
            if (p.Y <= gy + 0.05f) { hit = new Vector3(p.X, gy + 0.02f, p.Z); return true; }
        }
        return false;
    }

    private void FireBloodLash(float dmg, Color tint)
    {
        Vector3 o = _cam.GlobalPosition;
        Vector3 fwd = (-_cam.GlobalTransform.Basis.Z).Normalized();
        float reach = 8.6f * S.SpellArea, cosArc = 0.55f;   // ~57-degree half-arc (extended reach); area scales reach
        bool crit = RollCrit();              // the lash is direct damage → can crit
        if (crit) dmg *= CritMult();
        Game.I.NetMgr?.BroadcastVfx(3, GlobalPosition, fwd, 0f, 0f, tint);   // allies see the blade flurry
        foreach (var e in Game.I.Enemies.ToArray())
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
            var to = e.GlobalPosition - o; float d = to.Length();
            if (d > reach + e.Radius) continue;
            if (fwd.Dot(to / Mathf.Max(d, 0.001f)) < cosArc) continue;
            e.Hurt(dmg, DamageType.Blood, true, crit);
            e.Knockback(GlobalPosition, 0.6f);
            OnHitDirect(e, e.Dead, dmg, DamageType.Blood);
            AimHitOnEnemy(o, fwd, e, out var hp, out var hn);   // (NEW) slash lands where the cursor hit him, at that height
            Game.I.SpawnImpactMark(hp, hn, e, DamageType.Blood, 0.7f);
        }
        Game.I.SmashNear(o + fwd * (reach * 0.5f), reach * 0.5f);   // (FIX) the lash shatters pumpkins in its arc — this was dropped in the melee-decal rewrite, breaking pumpkin smashing for the Blood witch
        // visual: a flurry of blade slashes, each at a different spot/angle in front of her
        var right = new Vector3(fwd.Z, 0, -fwd.X).Normalized();
        float baseYaw = Mathf.Atan2(fwd.X, fwd.Z);
        int slashes = 3;
        var bladeMesh = new BoxMesh { Size = new Vector3(reach * 1.25f, 0.16f, 0.42f) };   // (NEW) shared mesh + material for all slashes in this lash
        var bladeMat = Game.ElementBoltMat(tint, DamageType.Blood);
        for (int s = 0; s < slashes; s++)
        {
            var blade = new MeshInstance3D { Mesh = bladeMesh };
            blade.MaterialOverride = bladeMat;
            Game.I.AddChild(blade);
            float lat = (GD.Randf() - 0.5f) * 3.2f;
            float vert = 0.6f + GD.Randf() * 1.6f;
            float depth = reach * (0.35f + GD.Randf() * 0.45f);
            blade.GlobalPosition = GlobalPosition + fwd * depth + right * lat + new Vector3(0, vert, 0);
            float roll = (GD.Randf() - 0.5f) * Mathf.Pi;     // varied slash angle
            float pitch = (GD.Randf() - 0.5f) * 0.7f;
            blade.Rotation = new Vector3(pitch, baseYaw, roll);
            blade.Scale = new Vector3(0.15f, 1f, 1f);         // starts as a thin sliver
            var tw = blade.CreateTween();
            tw.SetParallel(true);
            tw.TweenProperty(blade, "scale", new Vector3(1.1f, 1f, 1f), 0.10f).SetDelay(s * 0.02f);
            tw.TweenProperty(blade, "transparency", 1f, 0.16f).SetDelay(s * 0.02f + 0.04f);
            tw.SetParallel(false);
            tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(blade)) blade.QueueFree(); }));
        }
        if (AimGroundHit(o, fwd, reach, out var gslash))   // (NEW) ground slashes only where the swing meets the ground (none if aiming up/into air)
            for (int i = 0; i < 3; i++)
            {
                var gp = gslash + right * ((GD.Randf() - 0.5f) * 2.4f) + fwd * ((GD.Randf() - 0.5f) * 1.6f);
                float gy = Game.I.SurfaceHeight(gp, gp.Y);
                Game.I.SpawnImpactMark(new Vector3(gp.X, gy + 0.02f, gp.Z), Vector3.Up, null, DamageType.Blood, 0.85f, baseYaw + (GD.Randf() - 0.5f) * Mathf.Pi);
            }
    }

    // Gale PRIMARY — a frontal-arc wind punch: area damage + light knockback in front of her, with a
    // thrust pose, alternating hand recoil, and wind-fist crescents. Mirrors the blood lash's arc. (NEW)
    private void FireWindPunch(float dmgBase)
    {
        Vector3 o = _cam.GlobalPosition;
        Vector3 fwd = (-_cam.GlobalTransform.Basis.Z).Normalized();
        float reach = 7.5f * S.SpellArea, cosArc = 0.55f;   // ~57-degree half-arc; SpellArea (area cards) scales reach
        bool crit = RollCrit();
        float dmg = dmgBase * 1.3f * (crit ? CritMult() : 1f);   // (BUFF) Gale punches hit ~30% harder — she felt weak
        var col = DamageTypes.Col(DamageType.Wind);
        foreach (var e in Game.I.Enemies.ToArray())
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
            var to = e.GlobalPosition - o; float d = to.Length();
            if (d > reach + e.Radius) continue;
            if (fwd.Dot(to / Mathf.Max(d, 0.001f)) < cosArc) continue;
            float fdmg = e.Thrown ? dmg * 1.45f : dmg;   // (BUFF) extra lethal to airborne foes — rewards her fling→punch combo
            e.Hurt(fdmg, DamageType.Wind, true, crit);
            e.Knockback(GlobalPosition, 1.0f);                              // light shove on the basic punch
            OnHitDirectNormal(e, e.Dead, fdmg, DamageType.Wind);
            AimHitOnEnemy(o, fwd, e, out var hp, out var hn);   // (NEW) blow-mark lands where the cursor hit him, at that height
            Game.I.SpawnImpactMark(hp, hn, e, DamageType.Wind, 0.55f);
        }
        if (AimGroundHit(o, fwd, reach, out var gblow))   // (NEW) ground blow-marks only where the punch meets the ground (none if aiming up/into air)
        {
            var right = new Vector3(fwd.Z, 0, -fwd.X).Normalized();
            for (int i = 0; i < 4; i++)
            {
                var gp = gblow + right * ((GD.Randf() - 0.5f) * 2.6f) + fwd * ((GD.Randf() - 0.5f) * 1.8f);
                float gy = Game.I.SurfaceHeight(gp, gp.Y);
                Game.I.SpawnImpactMark(new Vector3(gp.X, gy + 0.02f, gp.Z), Vector3.Up, null, DamageType.Wind, 0.5f);
            }
        }
        SetArm("barrage", 0.2f);                                           // rapid alternating jab animation
        if ((_fireHand = 1 - _fireHand) == 0) _kickL = 1; else _kickR = 1; // alternate the hand recoil
        // anime-style punch barrage: a flurry of wind-fist impacts popped in the AIR in front of the eye
        WindPunchBarrage(o, fwd, col);
        Game.I.SmashNear(o + fwd * 3.5f, 4f);   // the punch shatters pumpkins in front of you (NEW)
        Game.I.WaterTouchArea(o + fwd * 3.5f, 4f, 0.7f);   // ...splashes water across the arc, not just dead-centre (NEW)
        Game.I.GlowFlowersNear(o + fwd * 3.5f, 4f);   // ...and lights flowers (NEW)
        Game.I.NetMgr?.BroadcastVfx(14, o, fwd, 0f, 0f, col);              // allies see the barrage in front of the caster
        Game.I.Sfx?.Cast(DamageType.Wind);
    }

    // a flurry of wind-fist impacts punched into the air in front of `eye`, along `fwd`. Anime-barrage feel —
    // staggered pops at chest/head level out front (NOT on the ground). Called locally + from ReceiveVfx so
    // every player sees it. (NEW)
    public void WindPunchBarrage(Vector3 eye, Vector3 fwd, Color col)
    {
        var right = new Vector3(fwd.Z, 0, -fwd.X).Normalized();
        var fistMesh = new SphereMesh { Radius = 0.34f, Height = 0.68f };   // (NEW) shared mesh + material for the whole barrage (all fists identical)
        var fistMat = Game.ElementBoltMat(col, DamageType.Wind);
        for (int i = 0; i < 6; i++)
        {
            var fist = new MeshInstance3D { Mesh = fistMesh };
            fist.MaterialOverride = fistMat;
            fist.Transparency = 0.4f;                                        // translucent wisp; the tween below fades it fully out
            Game.I.AddChild(fist);
            float sx = (GD.Randf() - 0.5f) * 1.7f;
            float sy = (GD.Randf() - 0.5f) * 1.2f;
            float depth = 1.8f + GD.Randf() * 2.4f;
            Vector3 p = eye + fwd * depth + right * sx + Vector3.Up * sy;
            fist.GlobalPosition = p;
            fist.Scale = new Vector3(0.15f, 0.15f, 0.15f);
            float delay = i * 0.035f;   // staggered → rapid barrage
            var tw = fist.CreateTween(); tw.SetParallel(true);
            tw.TweenProperty(fist, "scale", new Vector3(1.15f, 1.15f, 1.15f), 0.08f).SetDelay(delay).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
            tw.TweenProperty(fist, "global_position", p + fwd * 1.1f, 0.13f).SetDelay(delay);
            tw.TweenProperty(fist, "transparency", 1f, 0.14f).SetDelay(delay + 0.03f);
            tw.SetParallel(false);
            tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(fist)) fist.QueueFree(); }));
        }
    }

    // Gale CHARGED release — a ground-slam punch: radial Wind AoE + knockback that scale with charge.
    // A full charge also fires equipped charged-cast mods and (with Tempest Heart) drops a whirlwind. (NEW)
    private void FireSlam(float charge, Vector3 at)
    {
        float c = Mathf.Clamp(charge, 0f, 1f);
        bool full = c >= 0.95f;
        float radius = (5f + c * 6f) * S.SpellArea * Mathf.Lerp(1f, GustPower, 0.5f);   // Crosswind nudges reach
        float knock = (3f + c * 6f) * GustPower;
        bool crit = RollCrit();
        float dmg = Base() * (0.5f + c * 1.5f) * ComboMul() * (crit ? CritMult() : 1f);
        var col = DamageTypes.Col(DamageType.Wind);
        Vector3 center = new Vector3(at.X, at.Y, at.Z);
        int hits = 0;
        foreach (var e in Game.I.Enemies.ToArray())
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
            if (Flat(e, center) > radius + e.Radius) continue;
            e.Hurt(dmg, DamageType.Wind, true, crit);
            e.Knockback(center, knock);
            OnHitDirect(e, e.Dead, dmg, DamageType.Wind);   // (FIX) build combo + charge finishers PER enemy hit (was ComboFromSource — 0.15s-throttled & never charged finishers; also handles kill/Stormform)
            hits++;
        }
        if (hits > 0) AddMana(1f);   // right-click pays back a mana when it connects — matches every other witch's charged release (NEW)
        Game.I.SmashNear(center, radius);   // the slam shatters pumpkins caught in it (NEW)
        Game.I.GlowFlowersNear(center, radius);   // ...and lights flowers (NEW; the slam's Ring splashes any water it covers)
        if (full) ApplyChargedMods(new Vector3(center.X, 0.04f, center.Z));   // modifiers fire only at full charge
        if (full && TempestHeart)   // Tempest Heart legendary: a lingering whirlwind at the slam point
        {
            var cy = new Cyclone(); Game.I.AddChild(cy);
            cy.Init(this, new Vector3(center.X, 0f, center.Z), 3.5f, 3f, Base() * 0.5f, false, false);
            Game.I.NetMgr?.BroadcastVfx(11, new Vector3(center.X, 0f, center.Z), Vector3.Up, 3.5f, 3f, col);
        }
        Game.I.NetMgr?.BroadcastVfx(6, center, Vector3.Up, radius, 0f, col);   // allies see the slam burst
        // (NEW visual — cosmetic) DOWNDRAFT PRESSURE-BURST (always) + WIND SIGIL (full charge only). A funnel of
        // compressed air drives down, detonating outward gust rings + ground streaks; on a FULL charge a teal air
        // rune-circle also flares open across the slam radius (matches the brujería sigil theming). Mechanics untouched.
        if (full) Game.I.SpawnGroundSigilLinger(center, radius, col, 5f);   // (NEW) leaves a lingering wind-coloured magic circle where the slam landed — full charge only

        // a funnel of compressed air driving straight down into the ground (the downdraft)
        var down = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 1.2f, BottomRadius = 0.2f, Height = 5f } };
        var dm = Game.ToonEmissive(col, 2.2f, 0f);
        if (dm is StandardMaterial3D dsm) { dsm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; dsm.AlbedoColor = new Color(col.R, col.G, col.B, 0.55f); }
        down.MaterialOverride = dm;
        Game.I.AddChild(down);
        down.GlobalPosition = new Vector3(center.X, center.Y + 4.8f, center.Z);
        var dwt = down.CreateTween(); dwt.SetParallel(true);
        dwt.TweenProperty(down, "global_position", new Vector3(center.X, center.Y + 0.4f, center.Z), 0.12f).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
        dwt.TweenProperty(down, "scale", new Vector3(2f, 0.3f, 2f), 0.16f).SetDelay(0.1f);
        dwt.TweenProperty(down, "transparency", 1f, 0.18f).SetDelay(0.08f);
        dwt.SetParallel(false);
        dwt.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(down)) down.QueueFree(); }));

        // outward pressure rings + gust streaks racing across the ground
        Ring(center, col, radius, 0.35f);
        Ring(center, col.Lerp(Colors.White, 0.4f), radius * 0.6f, 0.26f);
        int gusts = Mathf.Max(4, (int)(8f * Game.I.ParticleScale));   // fewer streaks on lower graphics presets
        var gustMat = Game.ToonEmissive(col.Lerp(Colors.White, 0.3f), 1.8f, 0f);
        if (gustMat is StandardMaterial3D gsm) { gsm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; gsm.AlbedoColor = new Color(col.R, col.G, col.B, 0.7f); }
        for (int i = 0; i < gusts; i++)
        {
            float ga = i / (float)gusts * Mathf.Tau + GD.Randf() * 0.3f;
            var gdir = new Vector3(Mathf.Cos(ga), 0, Mathf.Sin(ga));
            var streak = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.15f, 0.06f, 1.5f) }, MaterialOverride = gustMat };
            Game.I.AddChild(streak);
            float sgy = Game.I.SurfaceHeight(center, center.Y);
            streak.GlobalPosition = new Vector3(center.X + gdir.X * 1.2f, sgy + 0.12f, center.Z + gdir.Z * 1.2f);
            streak.LookAt(streak.GlobalPosition + gdir, Vector3.Up);   // long axis points outward
            var send = new Vector3(center.X + gdir.X * radius * 1.05f, streak.GlobalPosition.Y, center.Z + gdir.Z * radius * 1.05f);
            var gtw = streak.CreateTween(); gtw.SetParallel(true);
            gtw.TweenProperty(streak, "global_position", send, 0.22f).SetEase(Tween.EaseType.Out);
            gtw.TweenProperty(streak, "transparency", 1f, 0.26f);
            gtw.SetParallel(false);
            gtw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(streak)) streak.QueueFree(); }));
        }
        Game.I.NetMgr?.BroadcastVfx(29, center, Vector3.Zero, radius, 0f, col);   // (NEW) allies see the downdraft funnel + gusts
        SetArm("grdpunch", 0.34f);   // wind up, then punch the ground (NEW)
        CamKick(0.3f + 0.5f * c);
        Game.I.Sfx?.Release(DamageType.Wind);
    }

    // Gale charged-punch handler. On the ground: hold to charge, release to slam at her feet. In the air:
    // holding makes her hover and aim a ground target (reach grows with height); releasing rockets her down
    // to slam there. Releasing early still slams (weaker, and no full-charge modifier proc). (NEW)
    private void UpdateGaleCharge(float dt)
    {
        if (_galeDiving) return;   // mid-dive: ignore input until the slam lands
        bool holding = Input.IsActionPressed("charge") && !(UltActive && Ult == UltKind.Crescent);
        if (holding)
        {
            if (!_charging) { _charging = true; _charge = 0f; Game.I.Sfx?.ChargeUp(DamageType.Wind); }
            _charge = Mathf.Min(1f, _charge + Mathf.Min(S.ChargeSpeed, 2.5f) * dt * WindBoonChargeMul);   // Eyewall speeds the fill (NEW)
            _galeHover = Airborne;                     // hover (hold height) only while off the ground
            if (_galeHover) UpdateGaleAimRing(); else HideGaleAimRing();
        }
        else if (_charging)
        {
            _charging = false;
            bool fromAir = _galeHover; _galeHover = false; HideGaleAimRing();
            if (_charge > 0.08f)
            {
                if (Mana < 0.5f) { ResFail(); }
                else
                {
                    Mana -= 0.5f;
                    if (fromAir && Airborne) { _galeDiving = true; _galeDiveCharge = _charge; _galeDiveTarget = GaleAimPoint(); }
                    else { FireSlam(_charge, GlobalPosition); _fireCd = Mathf.Max(0.12f, S.FireCd) * 0.5f; }
                }
            }
        }
        Charging = _charging; ChargeAmt = _charge;   // drive the hand/charge-orb visuals
    }

    // where an air slam will land: the camera's ground-aim, clamped so reach grows with altitude (NEW)
    private Vector3 GaleAimPoint()
    {
        Vector3 aim = GroundAim();
        float height = GlobalPosition.Y - Game.I.SurfaceHeight(GlobalPosition, GlobalPosition.Y);
        float maxReach = 6f + Mathf.Max(0f, height) * 1.6f;
        Vector3 flat = aim - GlobalPosition; flat.Y = 0;
        if (flat.Length() > maxReach) aim = GlobalPosition + flat.Normalized() * maxReach;
        return new Vector3(aim.X, Game.I.SurfaceHeight(aim, aim.Y), aim.Z);
    }

    private void UpdateGaleAimRing()
    {
        var p = GaleAimPoint();
        if (_galeAimRing == null)
        {
            _galeAimRing = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = 1.6f, OuterRadius = 2.0f } };
            var m = Game.ToonEmissive(DamageTypes.Col(DamageType.Wind), 1.6f, 0.02f);
            if (m is StandardMaterial3D sm) { sm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; sm.AlbedoColor = new Color(sm.AlbedoColor.R, sm.AlbedoColor.G, sm.AlbedoColor.B, 0.55f); }
            _galeAimRing.MaterialOverride = m;
            // TorusMesh is already flat (XZ) in this build — no rotation needed, or it stands upright (NEW)
            Game.I.AddChild(_galeAimRing);
        }
        _galeAimRing.Visible = true;
        _galeAimRing.GlobalPosition = new Vector3(p.X, p.Y + 0.06f, p.Z);
    }
    private void HideGaleAimRing() { if (_galeAimRing != null) _galeAimRing.Visible = false; }

    // ===================== EMBER WITCH — flamethrower primary + aimed meteor secondary (NEW) =====================
    private MeshInstance3D _emberAimRing;
    private float _flameTickT = 0f, _flameSndT = 0f;

    // held primary: a flame cone that TICKS at the cast-speed rate (so FireCd cards speed it up) and coats foes in burn.
    private void UpdateFlameCone(float dt)
    {
        var basis = _cam.GlobalTransform.Basis;
        Vector3 o = (_handMeshL != null && GodotObject.IsInstanceValid(_handMeshL))   // (NEW) flame pours from the LEFT HAND, not the eye
            ? _handMeshL.GlobalPosition
            : EyePos - basis.X * 0.3f - basis.Y * 0.42f - basis.Z * 0.5f;
        Vector3 dir = AimDir().Normalized();
        float reach = 12f * S.SpellArea * (PhoenixActive ? 1.7f : 1f);   // (NEW) reaches further; Phoenix Ascendant makes it huge
        Game.I.SpawnFlameCone(o, dir, reach, DamageTypes.Col(DamageType.Ember));   // continuous flame VFX (local)
        _flameSndT -= dt; if (_flameSndT <= 0f) { _flameSndT = 0.25f; Game.I.Sfx?.Cast(DamageType.Ember); }
        _emberNetT -= dt; if (_emberNetT <= 0f) { _emberNetT = 0.12f; Game.I.NetMgr?.BroadcastVfx(66, o, dir, reach, 0f, DamageTypes.Col(DamageType.Ember)); }   // allies see the flame
        _flameTickT -= dt;
        if (_flameTickT <= 0f) { _flameTickT = Mathf.Max(0.08f, S.FireCd * 0.6f); FlameConeTick(o, dir, reach); }   // faster cast speed → faster ticks
        FireHeat = Mathf.Min(1f, FireHeat + 0.03f);
    }
    private float _emberNetT = 0f;
    private void EndFlameCone() { }   // nothing to tear down — the flame VFX is fire-and-forget puffs

    private void FlameConeTick(Vector3 o, Vector3 dir, float reach)
    {
        float pmul = PhoenixActive ? 1.6f : 1f;                // Phoenix Ascendant: harder-hitting flame
        float cosArc = PhoenixActive ? 0.78f : 0.85f;         // ...and a wider cone while ascended
        float dmg = Base() * 0.26f * pmul * ComboMul();        // small per-tick direct
        float burnPer = Base() * 0.085f * pmul;                // burn dps PER stack (scales with base damage)
        float bombFlat = Base() * 3.2f;                        // Living Bomb blast on reaching the threshold
        bool credited = false;
        foreach (var e in Game.I.Enemies.ToArray())
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
            var to = e.GlobalPosition - o; float d = to.Length();
            if (d > reach + e.Radius) continue;
            if (dir.Dot(to / Mathf.Max(d, 0.001f)) < cosArc) continue;
            e.Hurt(dmg, DamageType.Ember, true);
            e.AddBurn(1f, burnPer, bombFlat);                  // +1 burn stack toward Living Bomb
            if (!credited) { OnHitDirectNormal(e, e.Dead, dmg, DamageType.Ember); credited = true; }   // build combo/finisher/mana ONCE per tick (like the beams)
        }
        Game.I.DamageWorld(o + dir * (reach * 0.5f), reach * 0.5f, dmg);
    }

    // charged secondary: hold to aim a big ground ring under the reticle, release to call a meteor there.
    private void UpdateEmberCharge(float dt)
    {
        bool holding = Input.IsActionPressed("charge") && !(UltActive && Ult == UltKind.Crescent);
        if (holding)
        {
            if (!_charging) { _charging = true; _charge = 0f; Game.I.Sfx?.ChargeUp(DamageType.Ember); }
            _charge = Mathf.Min(1f, _charge + Mathf.Min(S.ChargeSpeed, 2.5f) * dt);
            UpdateEmberAimRing();
        }
        else if (_charging)
        {
            _charging = false; HideEmberAimRing();
            if (_charge > 0.08f)
            {
                if (Mana < 0.5f) ResFail();
                else { Mana -= 0.5f; var aim = EmberAimPoint(_charge); FireMeteor(_charge, aim); if (_charge >= 0.95f) ApplyChargedMods(aim); Game.I.Sfx?.Release(DamageType.Ember); CamKick(0.4f); }   // (FIX) her full-charge now fires charged-mods too (Meteor mod → two meteors)
            }
            _charge = 0f;
        }
        Charging = _charging; ChargeAmt = _charge;
    }
    private float EmberMeteorRadius(float charge) => (4f + charge * 4.5f) * S.SpellArea;   // grows with hold (4→8.5) AND with area-spell cards (× SpellArea)
    private Vector3 EmberAimPoint(float charge)
    {
        Vector3 aim = GroundAim();
        float maxReach = 26f * Mathf.Clamp(S.SpellRange, 1f, 2.5f);
        Vector3 flat = aim - GlobalPosition; flat.Y = 0;
        if (flat.Length() > maxReach) aim = GlobalPosition + flat.Normalized() * maxReach;
        return new Vector3(aim.X, Game.I.SurfaceHeight(aim, aim.Y), aim.Z);
    }
    private void UpdateEmberAimRing() => ShowEmberAimRing(EmberAimPoint(_charge), EmberMeteorRadius(_charge));
    private void ShowEmberAimRing(Vector3 p, float rr)   // the ground landing reticle (torus scaled to the blast radius) — shared by the meteor secondary AND the Meteor Descent ult
    {
        if (_emberAimRing == null)
        {
            _emberAimRing = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = 0.9f, OuterRadius = 1.0f } };
            var m = Game.ToonEmissive(DamageTypes.Col(DamageType.Ember), 1.8f, 0.02f);
            if (m is StandardMaterial3D sm) { sm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; sm.AlbedoColor = new Color(1f, 0.5f, 0.18f, 0.6f); }
            _emberAimRing.MaterialOverride = m; Game.I.AddChild(_emberAimRing);
        }
        _emberAimRing.Visible = true;
        _emberAimRing.Scale = Vector3.One * rr;   // torus base radius ~1 → scale to the blast radius
        _emberAimRing.GlobalPosition = new Vector3(p.X, p.Y + 0.07f, p.Z);
    }
    private void HideEmberAimRing() { if (_emberAimRing != null) _emberAimRing.Visible = false; }

    // ===================== EMBER ULTIMATES (NEW) =====================
    private float MeteorUltRadius() => (10f + UltTier * 1.5f) * S.SpellArea;
    private Vector3 MeteorAimPoint() { var a = GroundAim(); return new Vector3(a.X, Game.I.SurfaceHeight(a, a.Y), a.Z); }

    // ULT 1 — Meteor Descent: rise invulnerable, aim a landing zone (5s or confirm), then SLAM.
    private void UpdateMeteorAscend(float dt)
    {
        if (Downed || Game.I.State != GameState.Playing) { _meteorAscend = false; UltActive = false; HideEmberAimRing(); _iframe = 0.3f; _noFall = 2f; return; }
        _iframe = Mathf.Max(_iframe, 0.3f); Floating = false;
        float targetY = _meteorBaseY + 18f;
        GlobalPosition = new Vector3(GlobalPosition.X, Mathf.MoveToward(GlobalPosition.Y, targetY, 26f * dt), GlobalPosition.Z);
        _grounded = false; _vy = 0f;
        ShowEmberAimRing(MeteorAimPoint(), MeteorUltRadius());
        _meteorAscendT -= dt;
        bool canConfirm = _meteorAscendT < 4.7f;   // ignore the activation-frame [Q] press so she doesn't drop instantly
        if (_meteorAscendT <= 0f || (canConfirm && (Input.IsActionJustPressed("cast") || Input.IsActionJustPressed("ult")))) MeteorLand(MeteorAimPoint());
    }
    private void MeteorLand(Vector3 target)
    {
        _meteorAscend = false; HideEmberAimRing();
        float gy = Game.I.SurfaceHeight(target, target.Y);
        var land = new Vector3(target.X, gy, target.Z);
        GlobalPosition = ClampPos(land);
        _grounded = true; _vy = 0f; _jumps = JumpsMax; _iframe = 0.4f; _noFall = 0.5f;
        UltActive = false;

        int t = UltTier; float radius = MeteorUltRadius();
        float centerDmg = Base() * (10f + t * 3f) * ComboMul();   // huge at the core
        float edgeDmg = Base() * (2f + t * 0.6f) * ComboMul();    // still okay at the rim
        float burnPer = Base() * 0.1f, bombFlat = Base() * 3.5f;
        var col = DamageTypes.Col(DamageType.Ember);
        foreach (var e in Game.I.Enemies.ToArray())
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
            float d = Flat(e, land); if (d > radius + e.Radius) continue;
            float tt = Mathf.Clamp(d / radius, 0f, 1f);
            float dmg = Mathf.Lerp(centerDmg, edgeDmg, tt * tt);   // taper: sharp huge core → okay edge
            e.Hurt(dmg, DamageType.Ember, true, RollCrit());
            e.AddBurn(e.LivingBombThreshold, burnPer, bombFlat, 0f, Game.I.LocalPeer);   // brand EVERYTHING with 1 Living Bomb
            OnHitDirect(e, e.Dead, dmg, DamageType.Ember);
        }
        Game.I.DamageWorld(land, radius, centerDmg);
        var field = new GroundField { Type = FieldType.Hex, DType = DamageType.Ember, Radius = radius, Dur = 6f, Power = Base() * 0.6f,
            TintColor = col, BurnAdd = 1f, BurnPer = burnPer, BurnBomb = bombFlat, BurnOwner = Game.I.LocalPeer, Src = this };
        Game.I.AddChild(field); field.GlobalPosition = new Vector3(land.X, 0.05f, land.Z);   // the 6s inferno keeps stacking burn

        Game.I.SpawnEmberBurst(land + Vector3.Up * 0.5f, radius * 1.2f);
        for (int i = 0; i < 3; i++) Game.I.SpawnEmberBurst(land + new Vector3((GD.Randf() - 0.5f) * radius, 0.5f, (GD.Randf() - 0.5f) * radius), radius * 0.5f);
        Ring(land, col, radius * 1.3f, 0.6f); CamKick(1f);
        Game.I.Sfx?.ModEmber(land); Game.I.Sfx?.Thunder();
        Game.I.NetMgr?.BroadcastVfx(68, land, Vector3.Down, radius, 1f, col);
    }

    // ULT 2 — Wildfire Rush: a flame dash that lays a burning trail.
    private void FlameDash()
    {
        if (_flameDashCharges <= 0) return;
        _flameDashCharges--;
        Vector3 dir = InputDir();
        if (dir == Vector3.Zero) { dir = -GlobalTransform.Basis.Z; dir.Y = 0; dir = dir.Normalized(); }
        float area = Mathf.Clamp(S.SpellArea, 1f, 2.5f);
        float len = (13f + UltTier) * area, halfW = 4f * area;   // ~8u wide × 12-15u long, area cards enlarge it
        _flameDashDir = dir; _flameDashDist = len; _flameDashDur = 0.24f; _flameDashT = _flameDashDur;
        _iframe = Mathf.Max(_iframe, 0.3f);
        Vector3 origin = new Vector3(GlobalPosition.X, Game.I.SurfaceHeight(GlobalPosition, GlobalPosition.Y), GlobalPosition.Z);
        var trail = new EmberTrail { Origin = origin, Dir = dir, Length = len, HalfW = halfW, Dur = 10f,
            BurnAdd = 1.2f, BurnPer = Base() * 0.11f, BurnBomb = Base() * 3.5f, HealPerSec = S.MaxHp * 0.02f, Caster = this, OwnerPeer = Game.I.LocalPeer };
        Game.I.AddChild(trail);
        Game.I.NetMgr?.BroadcastEmberTrail(origin, dir, len, halfW, 10f);
        CamKick(0.3f); Game.I.Sfx?.Cast(DamageType.Ember);
    }

    // ULT 3 — Phoenix Ascendant: free flight + immolation aura; flamethrower fires here (Combat is skipped while flying).
    private void UpdatePhoenix(float dt)
    {
        if (Downed || Game.I.State != GameState.Playing) { EndPhoenix(); return; }
        Floating = false;
        Vector3 dir = InputDir();
        Vector3 np = (dir != Vector3.Zero) ? GlobalPosition + dir * (S.Speed * 1.15f) * dt : GlobalPosition;
        float vy = 0f;
        if (Input.IsActionPressed("jump")) vy += 11f;
        if (Input.IsActionPressed("descend")) vy -= 11f;
        float ny = GlobalPosition.Y + vy * dt;
        float floor = Game.I.SurfaceHeight(np, GlobalPosition.Y) + 1.2f;
        if (ny < floor) ny = floor;
        GlobalPosition = ClampPos(new Vector3(np.X, ny, np.Z));
        _grounded = false; _vy = 0f; _noFall = 1f;

        if (Input.IsActionPressed("cast")) UpdateFlameCone(dt);   // free & huge flamethrower (bonus applied in UpdateFlameCone)
        else EndFlameCone();

        _phoenixAuraT -= dt;
        if (_phoenixAuraT <= 0f)
        {
            _phoenixAuraT = 0.4f;
            float ar = (7f + UltTier) * S.SpellArea, ad = Base() * (0.8f + UltTier * 0.2f);
            foreach (var e in Game.I.Enemies.ToArray())
            {
                if (e == null || e.Dead || !GodotObject.IsInstanceValid(e) || Flat(e, GlobalPosition) >= ar + e.Radius) continue;
                e.Hurt(ad, DamageType.Ember, false);
                e.AddBurn(1f, Base() * 0.09f, Base() * 3.2f, 0f, Game.I.LocalPeer);
            }
            Game.I.NetMgr?.BroadcastVfx(70, GlobalPosition, Vector3.Zero, ar, 0.4f, DamageTypes.Col(DamageType.Ember));
        }
        UltActiveT -= dt;
        if (UltActiveT <= 0f) EndPhoenix();
    }
    private void EndPhoenix()
    {
        _phoenix = false; UltActive = false; _noFall = 3f; _iframe = Mathf.Max(_iframe, 0.3f);
        if (_phoenixVfx != null && GodotObject.IsInstanceValid(_phoenixVfx)) { _phoenixVfx.QueueFree(); _phoenixVfx = null; }
    }
    private void PhoenixRebirth()   // cheat-death, once per Phoenix
    {
        _phoenixRebirth = false;
        Hp = S.MaxHp * 0.6f; Shield = 0; _iframe = Mathf.Max(_iframe, 1.2f);
        float r = 12f * S.SpellArea;
        foreach (var e in Game.I.Enemies.ToArray())
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e) || Flat(e, GlobalPosition) >= r + e.Radius) continue;
            e.Hurt(Base() * 6f, DamageType.Ember, true); e.Knockback(GlobalPosition, 8f);
            e.AddBurn(3f, Base() * 0.1f, Base() * 3.5f, 0f, Game.I.LocalPeer);
        }
        Game.I.DamageWorld(GlobalPosition, r, Base() * 6f);
        Game.I.SpawnEmberBurst(GlobalPosition + Vector3.Up * 0.5f, r * 1.2f);
        Ring(GlobalPosition, DamageTypes.Col(DamageType.Ember), r * 1.3f, 0.7f); CamKick(1f);
        Game.I.Sfx?.ModEmber(GlobalPosition); Game.I.Sfx?.Thunder();
        Game.I.NetMgr?.BroadcastVfx(70, GlobalPosition, Vector3.Up, r, 1f, DamageTypes.Col(DamageType.Ember));
        Game.I.Hud?.Banner("REBORN IN FLAME");
    }
    private Node3D BuildPhoenixAura()
    {
        var root = new Node3D(); var col = DamageTypes.Col(DamageType.Ember);
        var core = new MeshInstance3D { Mesh = new SphereMesh { Radius = 1.3f, Height = 2.6f }, MaterialOverride = Game.Emissive(col, 2.5f) };
        if (core.MaterialOverride is StandardMaterial3D cm) { cm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; cm.AlbedoColor = new Color(1f, 0.5f, 0.15f, 0.26f); }
        core.Position = new Vector3(0, 1f, 0); root.AddChild(core);
        for (int s = -1; s <= 1; s += 2)
        {
            var wing = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(2.6f, 1.7f, 0.1f) } };
            var wm = Game.Emissive(col, 2.2f); wm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; wm.AlbedoColor = new Color(1f, 0.45f, 0.12f, 0.5f); wm.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
            wing.MaterialOverride = wm; wing.Position = new Vector3(s * 1.7f, 1.5f, -0.2f); wing.RotationDegrees = new Vector3(0, 0, s * 35f);
            root.AddChild(wing);
        }
        root.AddChild(new OmniLight3D { OmniRange = 9f, LightColor = col, LightEnergy = 2.5f, Position = new Vector3(0, 1.5f, 0) });
        return root;
    }

    private void FireMeteor(float charge, Vector3 at)
    {
        float radius = EmberMeteorRadius(charge);
        float dmg = Base() * (1.8f + charge * 1.6f) * ComboMul();   // scales with hold
        float burnPer = Base() * 0.085f, bombFlat = Base() * 3.2f;
        int burnStacks = 3 + Mathf.RoundToInt(charge * 3f);          // instant burn toward Living Bomb
        Game.I.SpawnEmberMeteor(at, radius, dmg, burnStacks, burnPer, bombFlat, this);
    }

    // rocket toward the aimed ground point; slam on arrival (NEW)
    private void UpdateGaleDive(float dt)
    {
        Vector3 cur = GlobalPosition;
        Vector3 flatTo = new Vector3(_galeDiveTarget.X - cur.X, 0, _galeDiveTarget.Z - cur.Z);
        float diveSpeed = 28f;   // travel time instead of a near-instant teleport down (NEW)
        Vector3 step = flatTo.Length() > 0.01f ? flatTo.Normalized() * Mathf.Min(diveSpeed * dt, flatTo.Length()) : Vector3.Zero;
        float ny = cur.Y - diveSpeed * dt;
        float ground = Game.I.SurfaceHeight(new Vector3(cur.X + step.X, cur.Y, cur.Z + step.Z), cur.Y);
        if (ny <= ground + 0.15f || (flatTo.Length() < 0.4f && ny <= ground + 0.8f))
        {
            var land = new Vector3(_galeDiveTarget.X, ground, _galeDiveTarget.Z);
            GlobalPosition = ClampPos(land);
            _galeDiving = false; _vy = 0f; _grounded = true; _jumps = JumpsMax;
            FireSlam(_galeDiveCharge, new Vector3(land.X, 0.04f, land.Z));
            _fireCd = Mathf.Max(0.12f, S.FireCd) * 0.5f;
        }
        else
        {
            GlobalPosition = ClampPos(new Vector3(cur.X + step.X, ny, cur.Z + step.Z)); _grounded = false;
            _windPuffCd -= dt; if (_windPuffCd <= 0f) { SpawnWindPuff(GlobalPosition, step.LengthSquared() > 0.001f ? step : -GlobalTransform.Basis.Z); _windPuffCd = 0.05f; }   // gusty dive trail (NEW)
        }
    }

    // a kill while Stormform is active extends its duration (capped so it can't run forever) (NEW)
    private void StormformOnKill()
    {
        if (Ult == UltKind.Stormform && UltActive)
        {
            UltActiveT = Mathf.Min(20f, UltActiveT + 0.5f);
            _stormMax = Mathf.Max(_stormMax, UltActiveT);
        }
    }

    // Hurricane piloting (per-frame while the ult is up): rise to a hover height, steer horizontally with the
    // movement keys, keep the funnel tracking the ground beneath her, and grind + periodically fling enemies in
    // its radius. Flinging is host-authoritative (Enemy.Fling no-ops on client proxies — the synced arc shows
    // the result); a client→host fling RPC is the planned follow-up so client casters fling too. (NEW)
    private void UpdateHurricane(float dt)
    {
        float targetY = _hurriBaseY + 12f;                               // hover height above her launch point
        float ny = Mathf.MoveToward(GlobalPosition.Y, targetY, 22f * dt);
        Vector3 dir = InputDir();                                        // camera-relative steering
        Vector3 np = (dir != Vector3.Zero) ? ClampPos(GlobalPosition + dir * (S.Speed * 0.8f) * dt) : GlobalPosition;
        GlobalPosition = new Vector3(np.X, ny, np.Z);
        _grounded = false; _vy = 0f; _noFall = Mathf.Max(_noFall, 0.3f);

        Vector3 center = new Vector3(GlobalPosition.X, Game.I.SurfaceHeight(GlobalPosition, _hurriBaseY), GlobalPosition.Z);
        if (_hurriVfx != null && GodotObject.IsInstanceValid(_hurriVfx)) _hurriVfx.GlobalPosition = center;
        _hurriNetT -= dt; if (_hurriNetT <= 0f) { _hurriNetT = 0.08f; Game.I.NetMgr?.BroadcastHurriMove(center); }   // (NEW) track for allies

        // grind + fling go through Net.StormForce so a CLIENT caster's hurricane still grinds/flings the host's
        // enemies (host/solo apply immediately). Ticked to keep traffic light; the fling arcs sync via snapshot. (NEW)
        float radius = (10f + UltTier * 1.5f) * S.SpellArea;
        _hurriGrindT -= dt;
        if (_hurriGrindT <= 0f) { _hurriGrindT = 0.3f; Game.I.NetMgr?.StormForce(center, radius, 2, Base() * 0.5f * ComboMul() * 0.3f); }   // grind tick
        _hurriFlingCd -= dt;
        if (_hurriFlingCd <= 0f) { _hurriFlingCd = 0.45f; Game.I.NetMgr?.StormForce(center, radius, 1, 22f + (ModHurricane ? 4f : 0f)); }   // fling-up
        if (ModHurricane)   // Eyewall: pulse the buff zone so allies + their minions standing in the storm gain cast/charge/move speed (NEW)
        {
            _windZoneT -= dt;
            if (_windZoneT <= 0f) { _windZoneT = 0.1f; Game.I.NetMgr?.BroadcastWindZone(center, radius); }
        }
    }

    // Thorn: a piercing spike that hits everything in a forward line and instantly ROOTS them.
    // On a FULL charge, any of HER OWN ents in the line detonate for a strong Nature burst.
    private void FireThorn(float charge)
    {
        float c = Mathf.Clamp(charge, 0f, 1f);
        bool full = c >= 0.95f;
        Vector3 camFwd = (-_cam.GlobalTransform.Basis.Z).Normalized();
        var col = DamageTypes.Col(DamageType.Nature);
        float dmg = Base() * (0.5f + c * 1.2f) * ComboMul();   // scales with how long you held it (trimmed so the Grove/ents carry her DPS, not the thorn itself)
        float radius = 0.5f + c * 0.3f;                       // a bigger spike at higher charge (kept modest so it won't fill the view)
        int pierce = full ? 99 : 0;                            // pierces all ONLY at full charge
        var tint = full ? col.Lerp(Colors.White, 0.3f) : col;
        // a knotted-wood spike that flies forward with real travel time (slower than the primary needles).
        // NOTE: the thorn itself no longer roots — rooting now comes from the ENT EXPLOSIONS it sets off.
        SpawnBolt(_cam.GlobalPosition + camFwd * 1.2f, camFwd * 32f, dmg, pierce, radius, tint, DamageType.Nature,
            normal: false, charged: true, combo: true, full: full, life: 1.9f,
            style: 2, rootOnHit: 0f, detonatesEnts: full);   // detonates her ents ONLY at full charge
        CamKick(full ? 0.45f : 0.2f + 0.2f * c);
        if (full) Game.I.SpawnGroundSigil(GlobalPosition, 4.5f * S.SpellArea, col);   // (NEW) nature sigil flares under her — full charge only
        if (full) Game.I.SpawnBramblePatch(GlobalPosition, 4.5f * S.SpellArea, 5f);   // (NEW) and leaves a lingering bramble patch around her
        Game.I.Sfx?.Release(DamageType.Nature);
    }

    // Crimson Tide: a charged blood spin centered on the witch — damage + knockback scale with charge.
    private void FireCrimsonTide(float charge)
    {
        float c = Mathf.Clamp(charge, 0f, 1f);
        float radius = (6.5f + c * 6f) * S.SpellArea;   // spell area
        bool full = c >= 0.95f;   // (NEW) the ritual SIGILS only manifest on a full charge
        bool crit = RollCrit();          // the burst's initial damage is direct → can crit
        float dmg = Base() * (0.4f + c * 0.95f) * ComboMul() * (crit ? CritMult() : 1f);
        float knock = 1.5f + c * 4f;
        var col = DamageTypes.Col(DamageType.Blood);
        bool killed = false;
        foreach (var e in Game.I.Enemies.ToArray())
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
            if (Flat(e, GlobalPosition) > radius + e.Radius) continue;
            e.Hurt(dmg, DamageType.Blood, true, crit);
            e.Knockback(GlobalPosition, knock);
            e.Slow(1.4f, 0.6f);
            ComboFromSource();
            if (e.Dead) killed = true;
        }
        Game.I.DamageWorld(GlobalPosition, radius, dmg);   // (FIX) the Crimson right-click nova breaks props in its radius
        if (killed) BloodReward(1f);   // a kill banks a stack (Crimson) or mends a little (others)
        if (c >= 0.95f) ApplyChargedMods(new Vector3(GlobalPosition.X, 0.04f, GlobalPosition.Z));   // modifiers fire on a full-charge spin
        Game.I.NetMgr?.BroadcastVfx(2, GlobalPosition, Vector3.Zero, radius, 0f, col);   // allies see the blood orb burst
        // visual (NEW — cosmetic): a dark blood orb bound in glowing ritual SIGILS. It swells at her chest with the
        // rune-rings orbiting it, then bursts as a ground magic-circle flares open across the AoE. No functionality change.
        var orb = new Node3D();
        Game.I.AddChild(orb);
        orb.GlobalPosition = GlobalPosition + new Vector3(0, 1.0f, 0);
        var core = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.5f, Height = 1.0f }, MaterialOverride = Game.BloodOrbMat() };
        orb.AddChild(core);
        if (full)   // (NEW) ritual sigil rings only manifest on a FULL charge
        {
            var sigilMat = Game.SigilMat(col);
            for (int b = 0; b < 3; b++)   // ritual sigil rings bound around the orb at different tilts
            {
                var s = new MeshInstance3D { Mesh = new QuadMesh { Size = new Vector2(2.0f, 2.0f) }, MaterialOverride = sigilMat };
                s.RotationDegrees = new Vector3(70f + b * 34f, b * 55f, b * 40f);
                orb.AddChild(s);
            }
        }
        orb.AddChild(new OmniLight3D { OmniRange = radius * 1.5f, LightColor = col, LightEnergy = 2.4f });
        orb.Scale = new Vector3(0.3f, 0.3f, 0.3f);
        float target = radius * 0.5f;
        var ot = orb.CreateTween();
        ot.SetParallel(true);
        ot.TweenProperty(orb, "scale", new Vector3(target, target, target), 0.16f).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
        ot.TweenProperty(orb, "rotation", new Vector3(0, Mathf.Tau * 2.5f, 0), 0.36f);   // ritual spin
        ot.SetParallel(false);
        ot.TweenProperty(orb, "scale", new Vector3(0.01f, 0.01f, 0.01f), 0.16f);
        ot.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(orb)) orb.QueueFree(); }));

        // (NEW) the crimson magic circle flares open across the AoE and LINGERS where the nova landed — FULL charge only
        if (full) Game.I.SpawnGroundSigilLinger(GlobalPosition, radius, col, 5f);

        // outward shock rings marking the AoE
        Ring(GlobalPosition, col, radius, 0.4f);
        Ring(GlobalPosition, col.Lerp(Colors.White, 0.4f), radius * 0.6f, 0.3f);
        CamKick(0.3f + 0.4f * c);
    }

    // Holy secondary: a forward ground beam of light. Longer hold -> farther, wider, harder.
    // Allies the beam directly hits are healed + get Blessed (0s..2s by charge). A FULL charge
    // also leaves a lingering strip matching the beam that slowly sears foes / mends allies.
    // Holy RIGHT-CLICK (the charged secondary). On release, a warm ray of light descends from the sky and
    // SWEEPS straight forward along the aim, searing foes it passes and BLESSING the caster + allies. It then
    // leaves a lingering consecrated strip on the ground (projected, so it conforms to hills) that keeps searing
    // enemies and mending allies/minions. Range = hold + range cards; width = area cards; strip lasts base 1s,
    // longer the more it was charged. (NEW approach — replaces the old ground-lying beam.)
    // ===== FROST WITCH primary: a long-range freezing beam. Holds on a target build 1 freeze stack/sec + frost DPS + slow. =====
    private void UpdateFrostBeam(float dt)
    {
        Vector3 eye = EyePos, dir = AimDir();
        float len = 46f * S.SpellRange;
        Enemy hit = null; float bestT = len;
        foreach (var e in Game.I.Enemies.ToArray())
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
            var rel = e.GlobalPosition + Vector3.Up * e.Radius * 0.5f - eye;
            float proj = rel.Dot(dir); if (proj < 1f || proj > len) continue;
            if ((rel - dir * proj).Length() < e.Radius + 1.1f && proj < bestT) { bestT = proj; hit = e; }
        }
        float beamLen = hit != null ? bestT : len;
        // when not locked on a foe, terminate the beam on the first surface (pumpkin/tree/wall/ground) so it stops there AND marks it
        Vector3 fSurf = Vector3.Zero, fNorm = Vector3.Up; bool onSurface = false;
        if (hit == null && BeamSurfaceHit(eye, dir, len, out fSurf, out fNorm)) { beamLen = (fSurf - eye).Length(); onSurface = true; }
        if (hit != null)
        {
            hit.Hurt(Base() * 1.4f * dt * ComboMul(), DamageType.Frost, true);
            if (!hit.Frozen) hit.AddFreeze(dt * FreezeRate, FreezeThreshMul, FrostDurBonus);   // thread this witch's frost profile (best-of on the enemy)
            _beamHitT -= dt;
            if (_beamHitT <= 0f) { _beamHitT = Mathf.Max(0.12f, S.FireCd); OnHitDirectNormal(hit, hit.Dead, Base() * 1.4f * _beamHitT, DamageType.Frost); }   // combo + finisher charge + mana, ticked at the normal fire rate
        }
        // (NEW) leave a frost mark where the beam lands — a rime patch on the foe it's freezing, or a frost scorch on the ground/wall/pumpkin it's pointed at
        _frostMarkT -= dt;
        if (_frostMarkT <= 0f)
        {
            _frostMarkT = 0.14f;
            if (hit != null)
            {
                var mn = hit.GlobalPosition - eye; mn.Y *= 0.35f; mn = mn.LengthSquared() > 1e-4f ? -mn.Normalized() : Vector3.Up;
                Game.I.SpawnImpactMark(hit.GlobalPosition + mn * (hit.Radius * 0.9f), mn, hit, DamageType.Frost, 0.5f);
            }
            else if (onSurface)
            { Game.I.SpawnImpactMark(fSurf, fNorm, null, DamageType.Frost, 0.6f); Game.I.SmashNear(fSurf, 1.1f); }
        }
        EnsureFrostBeam(); PlaceFrostBeam(eye, dir, beamLen, dt);
        _frostBeamNetT -= dt;
        if (_frostBeamNetT <= 0f) { _frostBeamNetT = 0.1f; Game.I.NetMgr?.BroadcastVfx(50, eye + new Vector3(0, -0.2f, 0), dir, beamLen, 0f, DamageTypes.Col(DamageType.Frost)); }
        _frostBeamSndT -= dt; if (_frostBeamSndT <= 0f) { _frostBeamSndT = 0.4f; Game.I.Sfx?.Cast(DamageType.Frost); }
        FireHeat = Mathf.Min(1f, FireHeat + 0.02f);
    }
    private void EnsureFrostBeam()
    {
        if (_frostSeg != null) return;
        _frostSeg = new SegBeam(FrostBeamSegs);
        _frostSeg.Build(Game.I, seg =>
        {
            var core = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.12f, BottomRadius = 0.12f, Height = 1f, RadialSegments = 6 }, MaterialOverride = Game.Emissive(new Color(0.65f, 0.88f, 1f), 3f) };
            core.RotationDegrees = new Vector3(90, 0, 0); seg.AddChild(core);
            var glow = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.3f, BottomRadius = 0.3f, Height = 1f, RadialSegments = 6 } };
            var gm = Game.ToonEmissive(new Color(0.6f, 0.85f, 1f), 1.4f, 0f); gm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; gm.AlbedoColor = new Color(0.7f, 0.9f, 1f, 0.3f);
            glow.MaterialOverride = gm; glow.RotationDegrees = new Vector3(90, 0, 0); seg.AddChild(glow);
        });
    }
    // Frost primary bows/whips as she swings her aim or strafes (SegBeam). Emanates from her hand (down-right of the crosshair).
    private void PlaceFrostBeam(Vector3 eye, Vector3 dir, float len, float dt)
    {
        if (_frostSeg == null) return;
        var b = _cam.GlobalTransform.Basis;
        Vector3 origin = eye + b.X * 0.32f - b.Y * 0.42f + dir * 0.5f;
        Vector3 target = eye + dir * len;
        float pulse = 1f + 0.15f * Mathf.Sin(Now * 30f);
        _frostSeg.Place(origin, target, dt, 8f, 24f, pulse);
    }
    public void EndFrostBeam() { _frostSeg?.Free(); _frostSeg = null; }

    // ===== FORSAKEN WITCH primary: a Moira-style curse-suck beam. Locks the nearest foe to the reticle (med range),
    // builds curse, and ~once a second spreads the curse to a nearby foe — tethering them into a shared-damage group
    // (up to MaxLinks). Low direct damage; the payoff is the group + the right-click crush. =====
    private Enemy _curseTarget;
    private const float CurseBeamDmg = 0.78f;   // primary suck-beam DoT coefficient (× Base()); the group-share is where her damage really lives
    private const int CurseBeamSegs = 7;         // the beam is drawn as this many segments so it can BOW/whip (Moira-style) instead of being a rigid line
    private SegBeam _curseSeg; private OmniLight3D _curseLight;   // segmented lagging beam + the impact-end light
    private int _curseGroupSeq = 100;
    private float _curseTickT = 0f, _curseSpreadT = 0f, _curseBeamNetT = 0f, _curseMarkT = 0f;
    private readonly System.Collections.Generic.List<Node3D> _tetherVis = new();

    private bool CurseLockValid(Enemy e)
    {
        if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) return false;
        var aimPt = e.GlobalPosition + Vector3.Up * e.Radius * 0.5f;
        var to = aimPt - EyePos; float d = to.Length();
        if (d <= 0.5f || d >= 30f * S.SpellRange || AimDir().Dot(to / d) <= 0.78f) return false;   // hold a wider cone than acquisition (~39°) so the lock doesn't drop
        return !Game.I.SightBlocked(EyePos, aimPt);   // (NEW) a wall coming between us breaks the lock
    }
    private Enemy CurseAimTarget()
    {
        Vector3 o = EyePos, f = AimDir();
        Enemy best = null; float bestScore = 0.90f;   // within ~25° of the reticle
        foreach (var e in Game.I.Enemies.ToArray())
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
            var aimPt = e.GlobalPosition + Vector3.Up * e.Radius * 0.5f;
            var to = aimPt - o; float d = to.Length();
            if (d < 0.5f || d > 26f * S.SpellRange) continue;
            float dot = f.Dot(to / d);
            if (dot > bestScore && !Game.I.SightBlocked(o, aimPt)) { bestScore = dot; best = e; }   // (NEW) only lock foes in clear sight
        }
        return best;
    }
    private int TotalTethered() { int n = 0; foreach (var e in Game.I.Enemies) if (e != null && !e.Dead && e.CurseGroup != 0) n++; return n; }
    private int GroupSize(int g) { int n = 0; foreach (var e in Game.I.Enemies) if (e != null && !e.Dead && e.CurseGroup == g) n++; return n; }
    private void RefreshGroup(int g, float dur) { foreach (var e in Game.I.Enemies) if (e != null && !e.Dead && e.CurseGroup == g) e.CurseT = Mathf.Max(e.CurseT, dur); }
    private Enemy NearestSpreadTarget(Vector3 at, int group, float r)
    {
        Enemy best = null; float bd = r * r;
        foreach (var e in Game.I.Enemies.ToArray())
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e) || e.CurseGroup != 0) continue;   // only uncursed, ungrouped foes
            float d = (e.GlobalPosition - at).LengthSquared();
            if (d < bd) { bd = d; best = e; }
        }
        return best;
    }

    // (NEW) march the suck-beam along the reticle and return where it first meets the world — a tree/pillar (Blocker),
    // a tall wall (Deck), or the ground (SurfaceHeight). Lets the un-locked beam terminate on that surface and scorch it.
    // Returns false if it reaches maxDist through open air.
    private bool BeamSurfaceHit(Vector3 eye, Vector3 dir, float maxDist, out Vector3 hit, out Vector3 normal)
    {
        hit = eye + dir * maxDist; normal = -dir;
        const float stepLen = 0.8f;
        int steps = Mathf.CeilToInt(maxDist / stepLen);
        for (int i = 1; i <= steps; i++)
        {
            Vector3 p = eye + dir * Mathf.Min(i * stepLen, maxDist);
            foreach (var pk in Game.I.Smashables)   // pumpkins — the beam hits (and, at the call site, smashes) them
            {
                if (pk == null || !GodotObject.IsInstanceValid(pk)) continue;
                float fx = p.X - pk.GlobalPosition.X, fz = p.Z - pk.GlobalPosition.Z;
                if (fx * fx + fz * fz < 0.85f && p.Y > pk.GlobalPosition.Y - 0.3f && p.Y < pk.GlobalPosition.Y + 1.4f)
                {
                    var n = new Vector3(fx, 0f, fz); n = n.LengthSquared() > 1e-4f ? n.Normalized() : -dir;
                    hit = new Vector3(pk.GlobalPosition.X + n.X * 0.7f, p.Y, pk.GlobalPosition.Z + n.Z * 0.7f); normal = n; return true;
                }
            }
            foreach (var bl in Game.I.Blockers)   // trees / cover pillars (treated as cylinders, matching bolt collision)
            {
                float fx = p.X - bl.Pos.X, fz = p.Z - bl.Pos.Z;
                if (fx * fx + fz * fz < bl.Radius * bl.Radius)
                {
                    var n = new Vector3(fx, 0f, fz); n = n.LengthSquared() > 1e-4f ? n.Normalized() : -dir;
                    hit = new Vector3(bl.Pos.X + n.X * bl.Radius, p.Y, bl.Pos.Z + n.Z * bl.Radius); normal = n; return true;
                }
            }
            foreach (var wl in Game.I.Decks)   // maze / structure walls (tall decks) — hit their side face
            {
                if (wl.TopY < 1.8f) continue;
                if (p.Y < wl.TopY && Mathf.Abs(p.X - wl.Center.X) < wl.Half.X && Mathf.Abs(p.Z - wl.Center.Z) < wl.Half.Y)
                {
                    float dx = (p.X - wl.Center.X) / wl.Half.X, dz = (p.Z - wl.Center.Z) / wl.Half.Y;
                    normal = Mathf.Abs(dx) > Mathf.Abs(dz) ? new Vector3(Mathf.Sign(dx), 0f, 0f) : new Vector3(0f, 0f, Mathf.Sign(dz));
                    hit = p; return true;
                }
            }
            float gy = Game.I.SurfaceHeight(p, p.Y);   // ground / standable decks / ramps
            if (p.Y <= gy + 0.1f) { hit = new Vector3(p.X, gy + 0.02f, p.Z); normal = Vector3.Up; return true; }
        }
        return false;
    }

    private void UpdateCurseBeam(float dt)
    {
        Enemy tgt = CurseLockValid(_curseTarget) ? _curseTarget : CurseAimTarget();   // sticky: keep the locked foe while it's alive, in sight + roughly aimed at
        _curseTarget = tgt;
        Vector3 beamEnd;
        if (tgt != null)
        {
            beamEnd = tgt.GlobalPosition + Vector3.Up * tgt.Radius * 0.5f;
            bool _wasCursed = tgt.CurseStacks > 0.01f;
            tgt.AddCurse(dt * CurseRate, tgt.CurseGroup, CurseBonusType, CurseBonusMul, CurseShareFrac, CurseBonusType2);   // build stacks on the beamed foe
            if (!_wasCursed && tgt.CurseStacks > 0.01f) Game.I.MyStats.Highlight++;   // (NEW) Forsaken highlight = foes newly cursed
            float beamDmg = Base() * CurseBeamDmg * dt * ComboMul();
            tgt.Hurt(beamDmg, DamageType.Curse, true);   // primary DoT — the beam isn't her damage, the group is
            if (CurseBeamLifesteal > 0f && Hp < S.MaxHp) Heal(beamDmg * CurseBeamLifesteal);   // (NEW) small sustain: siphon while beaming a live foe
            int anchorCap = Mathf.FloorToInt(tgt.CurseStacks);   // 2 stacks → group of up to 2; 1 stack does nothing (no tether)
            if (anchorCap >= 2)
            {
                if (tgt.CurseGroup == 0 && TotalTethered() < MaxLinks) tgt.CurseGroup = ++_curseGroupSeq;   // anchor a group
                if (tgt.CurseGroup != 0)
                {
                    _curseSpreadT -= dt;
                    if (_curseSpreadT <= 0f)
                    {
                        _curseSpreadT = 0.5f;
                        if (GroupSize(tgt.CurseGroup) < anchorCap && TotalTethered() < MaxLinks)
                        {
                            var near = NearestSpreadTarget(tgt.GlobalPosition, tgt.CurseGroup, CurseSpreadRange);
                            if (near != null) { near.AddCurse(1f, tgt.CurseGroup, CurseBonusType, CurseBonusMul, CurseShareFrac, CurseBonusType2); Game.I.Sfx?.Poof(near.GlobalPosition); }
                        }
                    }
                    RefreshGroup(tgt.CurseGroup, Mathf.Max(2f, anchorCap));   // keep the WHOLE group linked for the group duration (refreshed while beaming any member)
                }
            }
            _curseTickT -= dt;
            if (_curseTickT <= 0f) { _curseTickT = Mathf.Max(0.12f, S.FireCd); OnHitDirectNormal(tgt, tgt.Dead, Base() * CurseBeamDmg * _curseTickT, DamageType.Curse); }
            _curseMarkT -= dt;
            if (_curseMarkT <= 0f)   // (NEW) leave a fading curse scorch on the foe the beam is eating (parented → rides with them)
            {
                _curseMarkT = 0.14f;
                var mn = beamEnd - EyePos; mn.Y *= 0.35f; mn = mn.LengthSquared() > 1e-4f ? -mn.Normalized() : Vector3.Up;   // face back toward the caster
                Game.I.SpawnImpactMark(tgt.GlobalPosition + mn * (tgt.Radius * 0.9f), mn, tgt, DamageType.Curse, 0.5f);
            }
        }
        else
        {
            // (NEW) no lock — the beam still pours out along the reticle. Terminate it on the first surface (ground/wall/tree)
            // and scorch that spot, so the primary leaves a mark on non-enemy surfaces too; otherwise pour to max range in open air.
            Vector3 dir = AimDir(); float reach = 30f * S.SpellRange;
            if (BeamSurfaceHit(EyePos, dir, reach, out var sHit, out var sNorm))
            {
                beamEnd = sHit;
                _curseMarkT -= dt;
                if (_curseMarkT <= 0f) { _curseMarkT = 0.14f; Game.I.SpawnImpactMark(sHit, sNorm, null, DamageType.Curse, 0.6f); Game.I.SmashNear(sHit, 1.1f); }
            }
            else beamEnd = EyePos + dir * reach;
        }
        EnsureCurseBeam(); PlaceCurseBeam(beamEnd, dt);
        _curseBeamNetT -= dt;
        if (_curseBeamNetT <= 0f)
        {
            _curseBeamNetT = 0.1f;
            Game.I.NetMgr?.BroadcastVfx(57, EyePos + new Vector3(0, -0.2f, 0), (beamEnd - EyePos), (beamEnd - EyePos).Length(), 0f, DamageTypes.Col(DamageType.Curse));
        }
        FireHeat = Mathf.Min(1f, FireHeat + 0.02f);
    }
    // (NEW) Shared "living beam" renderer. A beam is drawn as N segments whose interior control points LAG toward the
    // straight origin→target line — most in the middle, least at the ends — so the beam BOWS and whips when either end
    // moves (Moira-style), then springs back to true when things settle. Used by the curse beam, the frost primary, and
    // the arcane Spelllance. Layer meshes MUST be built length-along-local-Y (Height / Size.Y = 1, rotated 90° about X);
    // Place() scales each layer's Y to the segment length and X/Z by the girth pulse.
    private sealed class SegBeam
    {
        private readonly int _n;
        private readonly Vector3[] _pts;
        private readonly Node3D[] _segs;
        private bool _init;
        public Node3D Root;
        public SegBeam(int segs) { _n = segs; _pts = new Vector3[segs + 1]; _segs = new Node3D[segs]; }
        public Vector3 End => _pts[_n];
        public void Build(Node3D parent, System.Action<Node3D> buildLayers)
        {
            Root = new Node3D(); parent.AddChild(Root);
            for (int s = 0; s < _n; s++) { var seg = new Node3D(); Root.AddChild(seg); buildLayers(seg); _segs[s] = seg; }
            _init = false;
        }
        public void Place(Vector3 origin, Vector3 target, float dt, float lagMid, float lagEnd, float pulse)
        {
            if (Root == null) return;
            if (!_init) { for (int i = 0; i <= _n; i++) _pts[i] = origin.Lerp(target, i / (float)_n); _init = true; }   // spawn straight — no first-frame whip
            _pts[0] = origin; _pts[_n] = target;
            for (int i = 1; i < _n; i++)
            {
                float f = i / (float)_n;
                Vector3 ideal = origin.Lerp(target, f);
                float chase = Mathf.Lerp(lagMid, lagEnd, Mathf.Abs(f - 0.5f) * 2f);   // mid = lagMid (springy) → ends = lagEnd (anchored)
                _pts[i] = _pts[i].Lerp(ideal, Mathf.Clamp(chase * dt, 0f, 1f));
            }
            for (int s = 0; s < _n; s++)
            {
                Vector3 a = _pts[s], b = _pts[s + 1], d = b - a;
                float len = d.Length(); if (len < 0.03f) len = 0.03f;
                Vector3 dn = d / len;
                _segs[s].GlobalPosition = (a + b) * 0.5f;
                _segs[s].LookAt(_segs[s].GlobalPosition + dn, Mathf.Abs(dn.Y) > 0.98f ? Vector3.Forward : Vector3.Up);
                foreach (var lc in _segs[s].GetChildren()) if (lc is MeshInstance3D mi) mi.Scale = new Vector3(pulse, len, pulse);
            }
        }
        public void Free() { if (Root != null && GodotObject.IsInstanceValid(Root)) Root.QueueFree(); Root = null; }
    }

    private void EnsureCurseBeam()
    {
        if (_curseSeg != null) return;
        var col = DamageTypes.Col(DamageType.Curse);
        _curseSeg = new SegBeam(CurseBeamSegs);
        _curseSeg.Build(Game.I, seg =>
        {
            void Layer(float r, Color c, float energy, float alpha)
            {
                var mi = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = r, BottomRadius = r, Height = 1f, RadialSegments = 8 } };
                var m = Game.ToonEmissive(c, energy, 0f);
                if (alpha < 1f && m is StandardMaterial3D sm) { sm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; sm.AlbedoColor = new Color(c.R, c.G, c.B, alpha); }
                mi.MaterialOverride = m; mi.RotationDegrees = new Vector3(90, 0, 0); seg.AddChild(mi);
            }
            Layer(0.5f, col, 1.4f, 0.18f);                       // soft outer halo
            Layer(0.28f, col, 2.4f, 0.45f);                      // purple body
            Layer(0.12f, col.Lerp(Colors.White, 0.55f), 4f, 1f); // bright inner core
        });
        _curseLight = new OmniLight3D { OmniRange = 6f, LightColor = col, LightEnergy = 2.2f };
        _curseSeg.Root.AddChild(_curseLight);
    }
    // The beam pours from the LEFT hand (the doll rides the right) to the target, bowing/whipping via SegBeam as she strafes.
    private void PlaceCurseBeam(Vector3 target, float dt)
    {
        if (_curseSeg == null) return;
        var basis = _cam.GlobalTransform.Basis;
        Vector3 origin = (_handMeshL != null && GodotObject.IsInstanceValid(_handMeshL))
            ? _handMeshL.GlobalPosition
            : EyePos - basis.X * 0.3f - basis.Y * 0.42f - basis.Z * 0.5f;   // fallback: a left-hand-ish offset from the eye
        float pulse = 1f + 0.18f * Mathf.Sin(Now * 22f) + (GD.Randf() - 0.5f) * 0.08f;   // living, flowing wobble
        _curseSeg.Place(origin, target, dt, 8f, 24f, pulse);
        if (_curseLight != null) _curseLight.GlobalPosition = _curseSeg.End;   // omni light rides the impact end
    }
    public void EndCurseBeam() { _curseSeg?.Free(); _curseSeg = null; _curseLight = null; }

    // draw a faint curse link between each pair of tethered group members (local visual; refreshed each frame)
    private void DrawCurseTethers()
    {
        ClearTetherVis();
        var groups = new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<Enemy>>();
        foreach (var e in Game.I.Enemies.ToArray())
            if (e != null && !e.Dead && e.CurseGroup != 0 && GodotObject.IsInstanceValid(e))
            { if (!groups.TryGetValue(e.CurseGroup, out var l)) { l = new(); groups[e.CurseGroup] = l; } l.Add(e); }
        var col = DamageTypes.Col(DamageType.Curse);
        foreach (var g in groups.Values)
            for (int i = 0; i + 1 < g.Count; i++)   // chain links between consecutive members
                _tetherVis.Add(Game.I.SpawnCurseLink(g[i].GlobalPosition + Vector3.Up, g[i + 1].GlobalPosition + Vector3.Up, col));
    }
    private void ClearTetherVis() { foreach (var t in _tetherVis) if (t != null && GodotObject.IsInstanceValid(t)) t.QueueFree(); _tetherVis.Clear(); }

    // ===== FORSAKEN WITCH secondary: the voodoo-doll crush. A hitscan pull — no projectile — that consumes the
    // cursed foe's stacks (by charge: tap = 1, full = all) and detonates them. Breaks that foe's tether group. =====
    private Node3D _voodoo;   // right-hand doll shown while charging the crush
    private OmniLight3D _voodooLight;   // (NEW) doll glow that brightens when the reticle is over a cursed foe
    private void FireVoodooCrush(float charge)
    {
        float c = Mathf.Clamp(charge, 0f, 1f);
        SetArm("crush", 0.45f);                         // always do the clasp-and-yank motion
        CamKick(0.3f + 0.35f * c);
        var tgt = CurseAimTarget();
        var col = DamageTypes.Col(DamageType.Curse);
        if (tgt == null) { _chargedRefund = false; Game.I.Sfx?.Fizzle(); return; }   // nothing under the reticle → the 0.5 mana is spent, no refund
        float perStack = Base() * 1.4f * ComboMul();   // detonation damage per stack crushed (~90 at 5 stacks after the 1.5x cursed amp)
        if (tgt.Cursed && tgt.CurseStacks > 0f)
            tgt.ConsumeCurse(c < 0.12f ? 0.001f : c, perStack, CurseStackCap);   // consume + detonate stacks (damage tapers to CurseStackCap effective stacks; shared to the group before it breaks)
        else
            tgt.Hurt(Base() * 1.4f * ComboMul(), DamageType.Curse, true);   // no stacks → still a solid base curse hit
        if (_chargedRefund) { if (c >= 0.95f) AddMana(1f); _chargedRefund = false; }   // consume the flag FIRST so OnHitDirect's generic any-charge refund is a no-op — hers is full-charge-only (net +0.5); a tap/partial just spends the 0.5
        OnHitDirect(tgt, tgt.Dead, Base() * 1.4f * ComboMul(), DamageType.Curse);   // (NEW) register the crush as a charged hit: builds spell-combo count + charges spell-combo finishers (+ ult charge), like every other right-click
        if (c >= 0.95f) ApplyChargedMods(tgt.GlobalPosition);                        // (NEW) full charge fires her equipped right-click modifiers on the struck foe (was only wired through the projectile path)
        // detonation effects at the foe — bigger for a fuller charge
        float fx = 0.7f + 0.9f * c;
        Vector3 at = tgt.GlobalPosition + Vector3.Up * tgt.Radius * 0.6f;
        Game.I.SpawnGroundSigil(tgt.GlobalPosition, 3.4f * fx, col);
        Ring(at, col, 4.0f * fx, 0.4f + 0.2f * c);
        Ring(at, col.Lerp(Colors.White, 0.4f), 2.0f * fx, 0.3f);
        Game.I.SpawnPollen(at, 3.2f * fx, col, 12 + Mathf.RoundToInt(c * 10f), 0.6f, net: false);
        CurseImplosion(at, col, fx);                                            // shards of curse yanked inward, then a pop
        if (_voodoo != null) DollBurst(_voodoo.GlobalPosition, col, 0.6f + 0.5f * c);   // the doll bursts in her grip (compact outward puff — it's right in front of the camera)
        Game.I.NetMgr?.BroadcastVfx(58, tgt.GlobalPosition, Vector3.Up, 3.8f, 0f, col);
        Game.I.Sfx?.CurseCrush(tgt.GlobalPosition);
        Game.I.PlayerSound(GlobalPosition, 1.6f);
    }

    // the crush look: jagged curse shards converge on the point, then a bright pop — reads as "yanked and crushed". (NEW)
    private void CurseImplosion(Vector3 at, Color col, float scale = 1f)
    {
        var mat = Game.Emissive(col.Lerp(Colors.White, 0.25f), 3f);
        int shards = Mathf.Clamp(Mathf.RoundToInt(8 * scale), 5, 16);
        for (int i = 0; i < shards; i++)
        {
            float a = i / (float)shards * Mathf.Tau;
            var shard = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.14f * scale, Height = 0.9f * scale, RadialSegments = 4 }, MaterialOverride = mat };
            Game.I.AddChild(shard);
            var start = at + new Vector3(Mathf.Cos(a), GD.Randf() * 0.6f - 0.3f, Mathf.Sin(a)) * 3.2f * scale;
            shard.GlobalPosition = start;
            shard.LookAt(at, Vector3.Up);
            var tw = shard.CreateTween(); tw.SetParallel(true);
            tw.TweenProperty(shard, "global_position", at, 0.14f).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.In);
            tw.TweenProperty(shard, "scale", new Vector3(0.3f, 0.3f, 0.3f), 0.14f);
            tw.SetParallel(false);
            tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(shard)) shard.QueueFree(); }));
        }
        var pop = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.4f, Height = 0.8f }, MaterialOverride = Game.ToonEmissive(col, 3f, 0f) };
        Game.I.AddChild(pop); pop.GlobalPosition = at; pop.Scale = Vector3.One * 0.2f;
        var pt = pop.CreateTween();
        pt.TweenInterval(0.13f);
        pt.TweenProperty(pop, "scale", Vector3.One * (2.4f * scale), 0.18f + 0.1f * scale).SetEase(Tween.EaseType.Out);
        pt.Parallel().TweenProperty(pop, "transparency", 1f, 0.18f + 0.1f * scale);
        pt.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(pop)) pop.QueueFree(); }));
    }

    // (NEW) the doll popping in her grip on release — a compact OUTWARD burst (central flash + a spray of curse motes).
    // Deliberately small and outward-throwing: it happens right in front of the camera, where CurseImplosion's big inward
    // shards read as spikes flying at your face. Uses ToonEmissive (instance-transparency fade, same as the pop above).
    private void DollBurst(Vector3 at, Color col, float scale)
    {
        var pop = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.12f, Height = 0.24f }, MaterialOverride = Game.ToonEmissive(col.Lerp(Colors.White, 0.5f), 4f, 0f) };
        Game.I.AddChild(pop); pop.GlobalPosition = at; pop.Scale = Vector3.One * 0.35f;
        var pt = pop.CreateTween(); pt.SetParallel(true);
        pt.TweenProperty(pop, "scale", Vector3.One * (1.5f * scale), 0.18f).SetEase(Tween.EaseType.Out);
        pt.TweenProperty(pop, "transparency", 1f, 0.18f);
        pt.SetParallel(false);
        pt.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(pop)) pop.QueueFree(); }));

        int motes = Mathf.Clamp(Mathf.RoundToInt(8 * scale), 5, 12);
        var mat = Game.ToonEmissive(col, 3f, 0f);
        for (int i = 0; i < motes; i++)
        {
            var m = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.03f, Height = 0.06f }, MaterialOverride = mat };
            Game.I.AddChild(m); m.GlobalPosition = at;
            var dir = new Vector3(GD.Randf() - 0.5f, GD.Randf() * 0.7f + 0.05f, GD.Randf() - 0.5f).Normalized();
            var tw = m.CreateTween(); tw.SetParallel(true);
            tw.TweenProperty(m, "global_position", at + dir * (0.45f + GD.Randf() * 0.5f) * scale, 0.3f).SetEase(Tween.EaseType.Out);
            tw.TweenProperty(m, "scale", Vector3.One * 0.15f, 0.3f);
            tw.TweenProperty(m, "transparency", 1f, 0.3f);
            tw.SetParallel(false);
            tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(m)) m.QueueFree(); }));
        }
    }

    private void BuildVoodooDoll()
    {
        _voodoo = new Node3D(); _armR.AddChild(_voodoo);
        var burlap = Game.Toon(new Color(0.58f, 0.44f, 0.29f), 0.95f, 0.25f, 0.01f);
        void Add(Mesh m, Vector3 pos, Vector3 rot = default, Material mat = null)
        { var mi = new MeshInstance3D { Mesh = m, MaterialOverride = mat ?? burlap }; mi.Position = pos; mi.RotationDegrees = rot; _voodoo.AddChild(mi); }
        Add(new BoxMesh { Size = new Vector3(0.085f, 0.13f, 0.05f) }, new Vector3(0, 0, 0));                                     // body
        Add(new SphereMesh { Radius = 0.048f, Height = 0.096f }, new Vector3(0, 0.095f, 0));                                     // head
        Add(new BoxMesh { Size = new Vector3(0.018f, 0.075f, 0.018f) }, new Vector3(-0.055f, 0.02f, 0), new Vector3(0, 0, 25));  // left arm
        Add(new BoxMesh { Size = new Vector3(0.018f, 0.075f, 0.018f) }, new Vector3(0.055f, 0.02f, 0), new Vector3(0, 0, -25));  // right arm
        Add(new BoxMesh { Size = new Vector3(0.02f, 0.08f, 0.02f) }, new Vector3(-0.025f, -0.1f, 0));                            // left leg
        Add(new BoxMesh { Size = new Vector3(0.02f, 0.08f, 0.02f) }, new Vector3(0.025f, -0.1f, 0));                             // right leg
        var pin = Game.Emissive(DamageTypes.Col(DamageType.Curse), 3.2f);
        Add(new CylinderMesh { TopRadius = 0f, BottomRadius = 0.007f, Height = 0.13f, RadialSegments = 4 }, new Vector3(0.01f, 0.01f, 0.05f), new Vector3(75, 0, 15), pin);   // curse pin through the chest
        var stitch = Game.Toon(new Color(0.1f, 0.05f, 0.05f), 1f, 0f, 0f);   // stitched X eyes
        Add(new BoxMesh { Size = new Vector3(0.022f, 0.006f, 0.006f) }, new Vector3(-0.018f, 0.1f, 0.045f), new Vector3(0, 0, 45), stitch);
        Add(new BoxMesh { Size = new Vector3(0.022f, 0.006f, 0.006f) }, new Vector3(-0.018f, 0.1f, 0.045f), new Vector3(0, 0, -45), stitch);
        Add(new BoxMesh { Size = new Vector3(0.022f, 0.006f, 0.006f) }, new Vector3(0.018f, 0.1f, 0.045f), new Vector3(0, 0, 45), stitch);
        Add(new BoxMesh { Size = new Vector3(0.022f, 0.006f, 0.006f) }, new Vector3(0.018f, 0.1f, 0.045f), new Vector3(0, 0, -45), stitch);
        _voodooLight = new OmniLight3D { OmniRange = 0.9f, LightColor = DamageTypes.Col(DamageType.Curse), LightEnergy = 0.25f, Position = new Vector3(0, 0.02f, 0.06f) };
        _voodoo.AddChild(_voodooLight);
        _voodoo.Visible = false;
    }

    // ===== FROST WITCH secondary: a charged icicle spear — pierces 3; full charge instant-crits, or SHATTERS a frozen target. =====
    private void FireIcicleSpear(float charge)
    {
        float c = Mathf.Clamp(charge, 0f, 1f);
        bool full = c >= 0.95f;
        Vector3 camFwd = (-_cam.GlobalTransform.Basis.Z).Normalized();
        float dmg = Base() * (0.4f + c * 1.1f) * ComboMul();   // base — SpawnBolt rolls its own crit
        int pierce = GlacialImpaler ? 20 : 3;
        bool shatterAny = GlacialImpaler || full;   // Glacial Impaler shatters frozen foes at ANY charge
        var b = SpawnBolt(_cam.GlobalPosition + camFwd * 1.2f, camFwd * (46f + c * 20f), dmg,
            pierce, 0.35f + c * 0.55f, DamageTypes.Col(DamageType.Frost), DamageType.Frost,
            normal: false, charged: true, combo: true, full: full, homing: false, style: 3);
        if (b != null)
        {
            if (full && !b.Crit && RollCrit()) { b.Dmg *= CritMult(); b.Crit = true; }   // full charge: a SECOND crit roll → ~2× base crit chance (not a guaranteed crit)
            b.FrostSpear = true; b.FrostSpearFull = shatterAny;   // shatters frozen foes (full charge, or any charge with Glacial Impaler)
        }
        Game.I.Sfx?.Release(DamageType.Frost);
    }

    private void FireHolyRay(float charge)
    {
        float c = Mathf.Clamp(charge, 0f, 1f);
        Vector3 fwd = -_cam.GlobalTransform.Basis.Z; fwd.Y = 0;
        if (fwd.LengthSquared() < 0.001f) { fwd = -GlobalTransform.Basis.Z; fwd.Y = 0; }
        fwd = fwd.Normalized();
        Vector3 o = new Vector3(GlobalPosition.X, 0f, GlobalPosition.Z) + fwd * 1.0f;
        float len = (18f + c * 28f) * S.SpellRange;      // reach: hold + RANGE cards (up to ~46 base)
        float half = (0.55f + c * 0.95f) * S.SpellArea;  // half-width: hold + AREA cards
        Game.I.NetMgr?.BroadcastVfx(33, new Vector3(o.X, 0f, o.Z), fwd, len, half, DamageTypes.Col(DamageType.Holy));   // allies see the descending holy ray sweep

        bool rayCrit = RollCrit();
        float dmg = Base() * (0.3f + c * 0.7f) * ComboMul() * (rayCrit ? CritMult() : 1f);   // the per-foe sear the sweep's leading edge deals
        // NOTE: the mana refund is NOT predicted here — it fires only when the sweep's edge actually hits a foe
        // (HolyGround -> OnHitDirect -> _chargedRefund). No hit → no refund.

        // (NEW) Bless is a FULL-CHARGE reward, delivered by the SWEEP — not at cast. Only the caster is blessed
        // here (she always is, on a full charge). Allies/minions get blessed by HolyGround as the ray's leading
        // edge actually passes over them; standing in the lingering strip afterwards heals but does NOT bless.
        bool fullBless = c >= 0.95f;
        float blessDur = 2f * (S.MaxCharge / 3f) + BlessBonus;   // 2s at the default Overcharge stat; scales with Overcharge + Benediction
        if (fullBless) BlessedT = Mathf.Max(BlessedT, blessDur);   // the Divine caster always gets it on a full charge (no stacking)
        if (BlessBonus > 0f && c > 0.05f) Heal(S.MaxHp * 0.03f);    // Benediction mends you a little each cast

        // lingering consecrated strip. The ray SWEEPS forward (~1.2s): its leading edge deals the real sear hit
        // to each foe it reaches (carrying combo/weave), and full-charge MODIFIERS erupt AT the first foe it hits
        // (not an arbitrary fixed spot). Then the strip lingers, lightly searing foes and mending allies/minions.
        float sweep = 1.2f;
        float dur = 1f + c * 2f;
        if (_holyArea != null && GodotObject.IsInstanceValid(_holyArea)) _holyArea.QueueFree();
        var area = new HolyGround
        {
            Origin = new Vector3(o.X, 0.06f, o.Z), Dir = fwd,
            Length = len, HalfW = half,
            SweepDur = sweep, Dur = dur, MaxDur = dur,
            EnemyDmg = Base() * 0.35f,        // light lingering sear/sec
            HealPerSec = S.MaxHp * 0.02f,     // light heal/sec to allies/minions/self
            Caster = this, SweepDmg = dmg, SweepCrit = rayCrit,   // leading-edge hit: damage + combo/weave + mana refund
            FullCharge = c >= 0.95f,           // (NEW) full-charge modifiers erupt at the first foe the sweep reaches
            BlessDur = fullBless ? blessDur : 0f   // (NEW) full-charge only: the sweep blesses allies/minions it passes over
        };
        Game.I.AddChild(area);
        _holyArea = area;
        Game.I.NetMgr?.BroadcastVfx(35, new Vector3(o.X, 0.06f, o.Z), fwd, len, half, DamageTypes.Col(DamageType.Holy));   // allies see the consecrated strip decal

        SpawnHolySweepBeam(o, fwd, len, half, sweep);   // the descending ray, sweeping slowly forward
        if (fullBless) Game.I.SpawnGroundSigil(GlobalPosition, 4.5f * S.SpellArea, DamageTypes.Col(DamageType.Holy));   // (NEW) holy sigil flares under her — full charge only
        Game.I.Sfx?.Release(DamageType.Holy);
    }

    // The visual for the right-click: a bright warm column of light descending from the sky that sweeps forward
    // from the caster to the end of the ray, then fades. Purely cosmetic — the strip above does the mechanics. (NEW)
    private void SpawnHolySweepBeam(Vector3 o, Vector3 fwd, float len, float half, float sweepDur)
    {
        var beam = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = Mathf.Max(0.35f, half * 0.8f), BottomRadius = Mathf.Max(0.5f, half * 1.15f), Height = 34f }
        };
        beam.MaterialOverride = Game.HolyRayMat();   // warm additive shaft
        beam.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        Game.I.AddChild(beam);
        Vector3 start = new Vector3(o.X, 16f, o.Z);            // column centre ~16 high → base just below the ground, top in the sky
        Vector3 end = new Vector3(o.X, 16f, o.Z) + fwd * len;
        beam.GlobalPosition = start;
        beam.Transparency = 0.2f;
        var tw = beam.CreateTween();
        tw.SetParallel(true);
        tw.TweenProperty(beam, "global_position", end, sweepDur).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);   // slow forward sweep
        tw.TweenProperty(beam, "transparency", 1f, sweepDur * 0.45f).SetDelay(sweepDur * 0.75f);   // fade out as it finishes
        tw.SetParallel(false);
        tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(beam)) beam.QueueFree(); }));
    }

    // Build a glowing "divine construct" spear: bladed tip, fluted shaft, crossguard, pommel + inner core.
    private Node3D BuildLanceModel(float scale)
    {
        var col = DamageTypes.Col(DamageType.Holy);
        var outer = Game.ToonEmissive(col, 1.7f, 0.04f);
        var core = Game.ToonEmissive(col.Lerp(Colors.White, 0.7f), 2.6f, 0f);
        var n = new Node3D();
        void Add(Mesh m, Material mat, Vector3 pos, Vector3 rotDeg = default)
        { var mi = new MeshInstance3D { Mesh = m, MaterialOverride = mat }; mi.Position = pos; mi.RotationDegrees = rotDeg; n.AddChild(mi); }

        float s = scale;
        // bladed tip (point at y=0, widening up)
        Add(new CylinderMesh { TopRadius = 0.22f * s, BottomRadius = 0f, Height = 1.5f * s }, outer, new Vector3(0, 0.75f * s, 0));
        // fluted shaft
        Add(new CylinderMesh { TopRadius = 0.07f * s, BottomRadius = 0.12f * s, Height = 4.6f * s }, outer, new Vector3(0, 3.6f * s, 0));
        // glowing inner core down the shaft
        Add(new CylinderMesh { TopRadius = 0.04f * s, BottomRadius = 0.04f * s, Height = 6.2f * s }, core, new Vector3(0, 3.1f * s, 0));
        // crossguard
        Add(new BoxMesh { Size = new Vector3(1.1f * s, 0.16f * s, 0.22f * s) }, outer, new Vector3(0, 1.7f * s, 0));
        Add(new BoxMesh { Size = new Vector3(0.22f * s, 0.16f * s, 1.1f * s) }, outer, new Vector3(0, 1.7f * s, 0));
        // pommel
        Add(new SphereMesh { Radius = 0.2f * s, Height = 0.4f * s }, core, new Vector3(0, 6.0f * s, 0));
        return n;
    }

    // A divine lance plunging from the sky, planting at `at`, lingering for `dur`, then vanishing.
    private void Lance(Vector3 at, float dur = 0.5f, float scale = 1f)
    {
        var col = DamageTypes.Col(DamageType.Holy);
        var lance = BuildLanceModel(scale);
        Game.I.AddChild(lance);
        lance.Position = new Vector3(at.X, at.Y + 13f, at.Z);
        var tw = lance.CreateTween();
        tw.TweenProperty(lance, "position", new Vector3(at.X, at.Y, at.Z), 0.16f).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);   // plunge
        tw.TweenInterval(dur);
        tw.TweenProperty(lance, "scale", new Vector3(0.01f, 0.01f, 0.01f), 0.35f);   // dissolve away
        tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(lance)) lance.QueueFree(); }));
        Ring(at, col, 3f * scale, 0.4f);
        Game.I.NetMgr?.BroadcastLance(at, scale, dur);
    }

    private void FireDivinityMote()
    {
        var at = GroundAim();
        float dmg = Base() * 3.2f * ComboMul() * (1f + UltTier * 0.25f) * (ModDivinity ? 1.35f : 1f);
        float rad = 7f + UltTier * 0.8f;
        foreach (var e in Game.I.Enemies.ToArray())
            if (!e.Dead && Flat(e, at) < rad) { e.Hurt(dmg, DamageType.Holy, true); ComboFromSource(); }
        Game.I.DamageWorld(at, rad, dmg);   // (FIX) AoE breaks props too
        Ring(at, DamageTypes.Col(DamageType.Holy), rad, 0.45f);
        var v = new Vfx(); Game.I.AddChild(v);
        v.GlobalPosition = new Vector3(at.X, 0.5f, at.Z);
        v.Init(new SphereMesh { Radius = rad * 0.5f, Height = rad }, DamageTypes.Col(DamageType.Holy), 0.45f, 6f);
        Game.I.NetMgr?.BroadcastVfx(6, at, Vector3.Zero, rad, 0f, DamageTypes.Col(DamageType.Holy));   // (NEW) allies see the holy mote burst
        if (ModDivinity)   // lingering consecrated ground
        {
            var f = new GroundField { Type = FieldType.Heal, HealAllies = true, EnemyDmg = dmg * 0.18f, Radius = rad * 0.6f, Dur = 3.5f, Power = S.MaxHp * 0.02f, DType = DamageType.Holy, TintColor = DamageTypes.Col(DamageType.Holy), FromCombo = true };
            Game.I.AddChild(f); f.GlobalPosition = new Vector3(at.X, 0.04f, at.Z);
        }
        Game.I.Sfx?.Release(DamageType.Holy);
    }

    // Radiant Halo: a holy nova around the witch — damages foes, heals her.
    private void FinHalo(float pow, int t)
    {
        float rad = 8f + t * 1.2f, dmg = Base() * (1.4f + 0.3f * t) * pow;
        var col = DamageTypes.Col(DamageType.Holy);
        foreach (var e in Game.I.Enemies.ToArray())
            if (!e.Dead && Flat(e, GlobalPosition) < rad) { e.Hurt(dmg, DamageType.Holy, true); ComboFromSource(); }
        Game.I.DamageWorld(GlobalPosition, rad, dmg);   // (FIX) AoE breaks props too
        Heal(S.MaxHp * (0.06f + 0.02f * t));
        BlessedT = Mathf.Max(BlessedT, 4f);
        Ring(GlobalPosition, col, rad, 0.5f);
    }

    // Heaven's Lances: a fan of plunging holy lances ahead of the aim, each a small holy burst.
    private void FinLance(float pow, int t)
    {
        var at = GroundAim();
        int n = 3 + t;
        float dmg = Base() * (1.2f + 0.25f * t) * pow;
        var right = new Vector3(-(_cam.GlobalTransform.Basis.Z).Z, 0, (_cam.GlobalTransform.Basis.Z).X).Normalized();
        for (int i = 0; i < n; i++)
        {
            float off = (i - (n - 1) / 2f) * 2.6f;
            var spot = new Vector3(at.X + right.X * off, 0, at.Z + right.Z * off);
            foreach (var e in Game.I.Enemies.ToArray())
                if (!e.Dead && Flat(e, spot) < 3f) { e.Hurt(dmg, DamageType.Holy, true); ComboFromSource(); }
            Game.I.DamageWorld(spot, 3f, dmg);   // (FIX) AoE breaks props too
            Lance(spot);
        }
        Game.I.Sfx?.HolyLances(at);              // (NEW) sharp descending holy strike
    }

    // Blood Nova: a ring detonation around the witch — strong AoE + knockback. Scales with rarity (t) and pow.
    private void FinBloodNova(float pow, int t)
    {
        float rad = 9f + t * 1.2f;
        float dmg = Base() * (2.4f + 0.4f * t) * pow;
        var col = DamageTypes.Col(DamageType.Blood);
        bool killed = false;
        foreach (var e in Game.I.Enemies.ToArray())
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
            if (Flat(e, GlobalPosition) > rad + e.Radius) continue;
            e.Hurt(dmg, DamageType.Blood, true);
            e.Knockback(GlobalPosition, 5f + t);
            ComboFromSource();
            if (e.Dead) killed = true;
        }
        if (killed) BloodReward(1f);
        Game.I.DamageWorld(GlobalPosition, rad, dmg);   // (FIX) the blood nova breaks props too
        Ring(GlobalPosition, col, rad, 0.5f);
        // blood detonation from the center — an expanding orb that pops, plus splatter (like the Crimson right-click, minus the sigils) (NEW)
        Game.I.NetMgr?.BroadcastVfx(2, GlobalPosition, Vector3.Zero, rad, 0f, col);   // (NEW) allies see the blood detonation orb + ring
        var orb = new MeshInstance3D { Mesh = new SphereMesh { Radius = 1f, Height = 2f }, MaterialOverride = Game.BloodOrbMat() };
        Game.I.AddChild(orb);
        orb.GlobalPosition = GlobalPosition + new Vector3(0, 1.2f, 0);
        orb.Scale = new Vector3(0.2f, 0.2f, 0.2f);
        var ot = orb.CreateTween();
        ot.TweenProperty(orb, "scale", Vector3.One * (rad * 0.55f), 0.26f).SetEase(Tween.EaseType.Out);
        ot.TweenProperty(orb, "scale", new Vector3(0.05f, 0.05f, 0.05f), 0.22f).SetEase(Tween.EaseType.In);
        ot.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(orb)) orb.QueueFree(); }));
        Game.I.SpawnBloodMist(GlobalPosition, rad * 0.9f);
        CamKick(0.6f);
    }

    // Crimson Rush: surge forward riding a blood wave (gradual fast rush, not a teleport).
    // Higher rarity = farther dash, more damage/knockback.
    // --- Wind finishers (witch-agnostic, like all finishers) (NEW) ---

    // Updraft: launch the caster straight up and carry nearby small/medium foes aloft (mass-scaled, so brutes
    // barely rise) — a setup for air follow-ups (primary/charged/other combos mid-air).
    private void FinUpdraft(float pow, int t, Color col)
    {
        _vy = 23f + t * 1.5f; _grounded = false; _jumps = JumpsMax; _noFall = Mathf.Max(_noFall, 3.5f);   // (BUFF) launches the caster notably higher — more air time for follow-ups
        float rad = (6f + t) * S.SpellArea;
        float up = 16f + t * 2f + pow * 2f;
        Game.I.NetMgr?.StormForce(GlobalPosition, rad, 4, up);                 // lift small/medium foes straight up
        Game.I.MyStats.Flings += Game.I.CountFlungNear(GlobalPosition, rad);   // (NEW) tally enemies flung
        Game.I.NetMgr?.BroadcastVfx(0, GlobalPosition, Vector3.Up, rad, 0.5f, col);
        Ring(GlobalPosition, col, rad, 0.45f);
        Ring(GlobalPosition, col.Lerp(Colors.White, 0.4f), rad * 0.6f, 0.35f);
        Game.I.SpawnAirColumn(GlobalPosition, rad * 0.4f, 9f, 0.7f);   // (NEW) column of air swirling upward
        CamKick(0.5f);
        Game.I.Sfx?.Release(DamageType.Wind);
    }

    // a quick translucent gust streak that stretches + fades — trails a Wind Rush / air-dive so it reads as wind (NEW)
    private void SpawnWindPuff(Vector3 at, Vector3 dir)
    {
        dir.Y = 0f; dir = dir.LengthSquared() > 0.0001f ? dir.Normalized() : Vector3.Forward;
        var col = DamageTypes.Col(DamageType.Wind);
        var puff = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.4f, 0.4f, 2.2f) } };
        var m = Game.ToonEmissive(col, 0.8f, 0f);
        m.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        m.AlbedoColor = new Color(col.R, col.G, col.B, 0.4f);
        puff.MaterialOverride = m;
        puff.Rotation = new Vector3(0, Mathf.Atan2(dir.X, dir.Z), 0);
        Game.I.AddChild(puff);
        puff.GlobalPosition = new Vector3(at.X, at.Y + 1.0f, at.Z);
        var tw = puff.CreateTween(); tw.SetParallel(true);
        tw.TweenProperty(puff, "scale", new Vector3(1.6f, 0.2f, 2.6f), 0.35f);
        tw.TweenProperty(puff, "transparency", 1f, 0.35f);
        var f = puff.CreateTween(); f.TweenInterval(0.4f);
        f.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(puff)) puff.QueueFree(); }));
    }

    // Wind Rush: dash forward on a gust, lightly damaging + flinging foes aside (big ones resist, per Fling's
    // mass scaling). ~50% chance to refund all dashes once per cast if it connects. Higher rarity flings
    // harder, hits a bit harder, and rushes farther. Damage/fling route via StormForce (client-safe).
    private void FinWindRush(float pow, int t, Color col)
    {
        Vector3 fwd = -_cam.GlobalTransform.Basis.Z; fwd.Y = 0; fwd = fwd.Normalized();
        float dist = 13f + t * 3f + pow * 2f;   // further base reach now (NEW)
        float dmg = Base() * (0.35f + 0.1f * t) * pow;
        float flingPow = 8f + t * 2.5f + pow * 2f;       // higher rarity → harder fling
        float rad = (dist * 0.7f) * Mathf.Max(1f, S.SpellArea * 0.6f + 0.4f);
        _rushDur = 0.36f; _rushDist = dist; _rushDir = fwd; _rushT = _rushDur; _rushWind = true; _windPuffCd = 0f;   // longer, visibly-gusty glide (NEW)
        if (_inWaterBody) { _rushT = 0f; _rushDist = 0f; _rushWind = false; }   // no movement-dash combos while wading/swimming (NEW)
        _iframe = Mathf.Max(_iframe, _rushDur + 0.15f);
        Game.I.NetMgr?.StormForce(GlobalPosition, rad, 2, dmg);               // light damage in the lane
        Game.I.NetMgr?.StormForce(GlobalPosition, rad, 3, flingPow);          // fling foes aside/back (mass-scaled)
        Game.I.MyStats.Flings += Game.I.CountFlungNear(GlobalPosition, rad);   // (NEW) tally enemies flung
        // ~50% dash refund if an enemy is in the path (local check — works regardless of authority)
        bool hit = false;
        foreach (var e in Game.I.Enemies.ToArray())
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
            if (Flat(e, GlobalPosition) <= rad + e.Radius) { hit = true; break; }
        }
        if (hit && GD.Randf() < 0.5f) DashStock = S.DashCharges;             // reset dashes to max
        Game.I.NetMgr?.BroadcastVfx(6, GlobalPosition, fwd, rad, 0f, col);
        Game.I.NetMgr?.BroadcastVfx(32, GlobalPosition, fwd, dist, _rushDur, col);   // (NEW) allies see the wind bullet streak past
        Ring(GlobalPosition, col, rad * 0.7f, 0.4f);
        // a forward wind-bullet streaking along the dash (same as allies see — not an aura around her) (NEW)
        Game.I.SpawnWindBullet(GlobalPosition, fwd, dist, _rushDur);
        CamKick(0.5f);
        Game.I.Sfx?.WindRushBy(GlobalPosition);            // (NEW) loud gust as she rushes past
    }

    // Wind Slice: hurl a travelling X of wind forward that pierces and damages every foe in its path.
    private void FinWindSlice(float pow, int t, Color col)
    {
        Vector3 fwd = (-_cam.GlobalTransform.Basis.Z).Normalized();   // (NEW) full aim incl. pitch → flies through the crosshair
        var ws = new WindSlice
        {
            Dir = fwd,
            Dmg = Base() * (0.7f + 0.18f * t) * pow,
            Width = (4.5f + t * 0.8f) * S.SpellArea,
            Range = 30f + t * 6f,
            Speed = 34f,
        };
        Game.I.AddChild(ws);
        ws.GlobalPosition = EyePos + fwd * 1.4f;                      // (NEW) start at the eye so it tracks the reticle
        Ring(GlobalPosition, col, 3f, 0.35f);
        CamKick(0.4f);
        Game.I.Sfx?.WindSlash(EyePos + fwd * 2f);                     // (NEW) sharp wind woosh
    }

    // ---- Frost finishers (NEW) ----

    // Ice Spikes: a cone of ice erupts ahead (~12u), damaging foes and flinging the small/medium ones up.
    private void FinIceSpike(float pow, int t, Color col)
    {
        Vector3 o = GlobalPosition, fwd = AimDir(); fwd.Y = 0; fwd = fwd.LengthSquared() > 0.001f ? fwd.Normalized() : Vector3.Forward;
        float reach = 12f * S.SpellArea, cosArc = 0.5f;   // ~60° half-cone
        float dmg = Base() * (1.6f + 0.4f * t) * pow;
        foreach (var e in Game.I.Enemies.ToArray())
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
            var to = e.GlobalPosition - o; float d = to.Length(); to.Y = 0f;
            if (d > reach + e.Radius || to.LengthSquared() < 0.001f) continue;
            if (fwd.Dot(to.Normalized()) < cosArc) continue;
            e.Hurt(dmg, DamageType.Frost, true);
            ComboFromSource();
        }
        Game.I.NetMgr?.StormForce(o + fwd * (reach * 0.5f), reach * 0.6f, 4, 13f + t * 2f);   // fling small/medium foes up (host-authoritative / client-safe)
        Game.I.MyStats.Flings += Game.I.CountFlungNear(o + fwd * (reach * 0.5f), reach * 0.6f);   // (NEW) tally enemies flung
        Game.I.DamageWorld(o + fwd * (reach * 0.5f), reach * 0.6f, dmg);
        SpawnIceSpikeCone(o, fwd, reach, col);
        Game.I.NetMgr?.BroadcastVfx(54, o, fwd, reach, 0f, col);   // allies see the cone
        CamKick(0.5f);
        Game.I.Sfx?.IceShatter(o);
    }

    // the erupting ice-spike cone visual — called locally and (via ReceiveVfx) on allies. Cosmetic. (NEW)
    public void SpawnIceSpikeCone(Vector3 o, Vector3 fwd, float reach, Color col)
    {
        fwd.Y = 0f; fwd = fwd.LengthSquared() > 0.001f ? fwd.Normalized() : Vector3.Forward;
        var right = new Vector3(fwd.Z, 0, -fwd.X).Normalized();
        var ice = Game.Emissive(col, 2.2f);
        int rows = 4;
        for (int r = 0; r < rows; r++)
        {
            float depth = reach * (0.25f + r * 0.22f);
            int n = 2 + r;               // widening cone
            float spread = depth * 0.6f;
            for (int i = 0; i < n; i++)
            {
                float lat = (n == 1) ? 0f : (i / (float)(n - 1) - 0.5f) * spread;
                var gp = o + fwd * depth + right * lat;
                float gy = Game.I.SurfaceHeight(gp, gp.Y);
                float h = 1.6f + GD.Randf() * 1.6f + r * 0.3f;
                var spike = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.35f, Height = h, RadialSegments = 5 }, MaterialOverride = ice };
                spike.RotationDegrees = new Vector3((GD.Randf() - 0.5f) * 16f, GD.Randf() * 360f, (GD.Randf() - 0.5f) * 16f);
                Game.I.AddChild(spike);
                spike.GlobalPosition = new Vector3(gp.X, gy - h * 0.5f, gp.Z);   // start buried
                var tw = spike.CreateTween();
                tw.TweenProperty(spike, "global_position", new Vector3(gp.X, gy + h * 0.3f, gp.Z), 0.10f).SetDelay(r * 0.05f).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
                tw.TweenInterval(0.5f);
                tw.TweenProperty(spike, "global_position", new Vector3(gp.X, gy - h, gp.Z), 0.3f);
                tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(spike)) spike.QueueFree(); }));
            }
        }
    }

    // Frost Vault: kick off an icicle to launch UP + BACK to safety; the icicle stays and bursts to slow the foes left behind.
    private void FinFrostVault(float pow, int t, Color col)
    {
        Vector3 launchPos = GlobalPosition;
        Vector3 fwd = AimDir(); fwd.Y = 0; fwd = fwd.LengthSquared() > 0.001f ? fwd.Normalized() : Vector3.Forward;
        float up = 13f + t * 1.5f + pow * 2f;
        float back = 8f + t * 2f + pow;
        _vy = up; _grounded = false; _jumps = JumpsMax; _noFall = Mathf.Max(_noFall, 2.6f);
        _rushDir = -fwd; _rushDist = back; _rushDur = 0.32f; _rushT = _rushDur; _rushWind = false;   // glide up-and-back
        float rad = (6f + t) * S.SpellArea;
        float dmg = Base() * (0.7f + 0.2f * t) * pow;
        SpawnVaultIcicle(launchPos, rad, dmg, col);
        Game.I.NetMgr?.BroadcastVfx(55, launchPos, Vector3.Up, rad, 0f, col);   // allies see the icicle + burst ring
        CamKick(0.5f);
        Game.I.Sfx?.Freeze(launchPos);
    }

    // the vault icicle: erupts where she kicked off, holds ~0.7s, then bursts (host applies the slow + light frost). (NEW)
    public void SpawnVaultIcicle(Vector3 at, float rad, float dmg, Color col, bool remote = false)
    {
        float gy = Game.I.SurfaceHeight(at, at.Y);
        var ice = Game.Emissive(col, 2.4f);
        var spike = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.6f, Height = 4.2f, RadialSegments = 6 }, MaterialOverride = ice };
        Game.I.AddChild(spike);
        spike.GlobalPosition = new Vector3(at.X, gy - 2f, at.Z);
        var tw = spike.CreateTween();
        tw.TweenProperty(spike, "global_position", new Vector3(at.X, gy + 1.8f, at.Z), 0.12f).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
        tw.TweenInterval(0.6f);
        tw.TweenCallback(Callable.From(() =>
        {
            if (!remote && Game.I != null)
                foreach (var e in Game.I.Enemies.ToArray())
                    if (e != null && !e.Dead && GodotObject.IsInstanceValid(e) && Flat(e, at) < rad + e.Radius)
                    { e.Hurt(dmg, DamageType.Frost, true); e.Slow(2.5f, 0.45f); }
            if (!remote) Game.I?.DamageWorld(at, rad, dmg);
            Game.I?.SpawnFrostShatter(new Vector3(at.X, gy + 0.5f, at.Z), rad);
            Game.I?.VfxRing(new Vector3(at.X, gy + 0.06f, at.Z), col, rad, 0.4f);
        }));
        tw.TweenProperty(spike, "scale", new Vector3(1.6f, 0.1f, 1.6f), 0.25f);
        tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(spike)) spike.QueueFree(); }));
    }

    // Glacial Vise: clap two ice walls together over a 10×10 square ahead, crushing foes between them for % of their max HP.
    private void FinFrostWalls(float pow, int t, Color col)
    {
        Vector3 fwd = AimDir(); fwd.Y = 0; fwd = fwd.LengthSquared() > 0.001f ? fwd.Normalized() : Vector3.Forward;
        var right = new Vector3(fwd.Z, 0, -fwd.X).Normalized();
        float half = 5f * S.SpellArea;                  // a 10×10 square (scaled by area cards)
        Vector3 center = GlobalPosition + fwd * (half + 1.5f);
        float pct = Mathf.Min(0.07f, 0.02f + t * 0.012f);   // 2% (common) → ~7% (legendary) of max HP
        foreach (var e in Game.I.Enemies.ToArray())
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
            var rel = e.GlobalPosition - center;
            float af = Mathf.Abs(rel.Dot(fwd)), ar = Mathf.Abs(rel.Dot(right));
            if (af < half + e.Radius && ar < half + e.Radius)
            {
                e.Hurt(e.MaxHp * pct + Base() * 0.5f * pow, DamageType.Frost, true);   // % max HP + a small flat floor
                e.Slow(2f, 0.5f);
                ComboFromSource();
            }
        }
        Game.I.DamageWorld(center, half, Base() * 0.5f * pow);
        SpawnFrostWalls(center, fwd, right, half, col);
        Game.I.NetMgr?.BroadcastVfx(56, center, fwd, half, 0f, col);   // allies see the walls slam
        CamKick(0.6f);
        Game.I.Sfx?.IceShatter(center);
    }

    // two ice-sheet walls that slide in from the sides of the square and clap together. Cosmetic; called on all machines. (NEW)
    public void SpawnFrostWalls(Vector3 center, Vector3 fwd, Vector3 right, float half, Color col)
    {
        fwd.Y = 0f; right.Y = 0f;
        var mat = Game.ToonEmissive(col, 1.5f, 0f);
        if (mat is StandardMaterial3D sm) { sm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; sm.AlbedoColor = new Color(col.R, col.G, col.B, 0.7f); }
        float gy = Game.I.SurfaceHeight(center, center.Y);
        foreach (float side in new[] { -1f, 1f })
        {
            var wall = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(half * 2f, 5f, 0.6f) }, MaterialOverride = mat };
            Game.I.AddChild(wall);
            var start = center + right * side * (half + 1.2f);
            wall.GlobalPosition = new Vector3(start.X, gy + 2.5f, start.Z);
            wall.LookAt(wall.GlobalPosition + right, Vector3.Up);   // face inward (long axis along fwd)
            var inTarget = center + right * side * (half * 0.25f);
            var tw = wall.CreateTween();
            tw.TweenProperty(wall, "global_position", new Vector3(inTarget.X, gy + 2.5f, inTarget.Z), 0.14f).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);   // slam inward
            tw.TweenInterval(0.35f);
            tw.TweenProperty(wall, "transparency", 1f, 0.4f);
            tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(wall)) wall.QueueFree(); }));
        }
        Game.I.SpawnFrostShatter(new Vector3(center.X, gy + 1f, center.Z), half);
    }

    private void FinCrimsonRush(float pow, int t)
    {
        Vector3 fwd = -_cam.GlobalTransform.Basis.Z; fwd.Y = 0; fwd = fwd.Normalized();
        float dist = 9f + t * 2.5f + pow * 2f;
        float dmg = Base() * (0.6f + 0.15f * t) * pow;
        _rushDur = 0.30f; _rushDist = dist; _rushDir = fwd; _rushT = _rushDur; _rushWind = false;   // glide forward over the duration (no wind gusts — this is blood) (NEW)
        if (_inWaterBody) { _rushT = 0f; _rushDist = 0f; }   // no movement-dash combos while wading/swimming (NEW)
        _iframe = Mathf.Max(_iframe, _rushDur + 0.15f);                          // immune while riding the wave
        // the wave itself carries the damage + knockback, travelling with her
        var wave = new BloodWave
        {
            Dir = fwd, Dmg = dmg, Knock = 2.5f + 0.6f * t,
            Width = (5f + t) * S.SpellArea, Speed = dist / _rushDur, Range = dist + 1f, SlowDur = 1.2f, BanksStack = true,
            ShieldChance = Mathf.Min(0.75f, 0.20f + 0.15f * t),   // (NEW) per foe struck: 20% common → 35% uncommon → 50% rare (returns a blood shield)
            Gush = true                                            // (NEW) blood gush on each hit + splatter at the end of the ride
        };
        Game.I.AddChild(wave);
        wave.GlobalPosition = new Vector3(GlobalPosition.X, 0.5f, GlobalPosition.Z) + fwd * 1.5f;
        Ring(GlobalPosition, DamageTypes.Col(DamageType.Blood), 4f, 0.4f);
        CamKick(0.5f);
    }

    // Blood Curse: a cone of misty blood applies Hex. No bounce at common; higher rarity adds bounces.
    // Each hex applied banks a Blood Stack.
    private void FinBloodCurse(float pow, int t)
    {
        Vector3 o = _cam.GlobalPosition;
        Vector3 fwd = (-_cam.GlobalTransform.Basis.Z).Normalized();
        float reach = 12f, cosArc = 0.6f;
        int jumps = t;   // common (t=0) = no bounce
        int hexed = 0;
        bool curseKill = false;
        foreach (var e in Game.I.Enemies.ToArray())
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
            var to = e.GlobalPosition - o; float d = to.Length();
            if (d > reach + e.Radius) continue;
            if (fwd.Dot(to / Mathf.Max(d, 0.001f)) < cosArc) continue;
            e.Hurt(Base() * (0.8f + 0.15f * t) * pow, DamageType.Blood, true);
            e.Mark(3f, S.MarkAmp, jumps);
            ComboFromSource();
            hexed++;
            if (e.Dead) curseKill = true;
        }
        if (hexed > 0) BloodReward(hexed);   // Crimson: +1 stack/hex; others: a little heal/hex
        if (curseKill) BloodReward(1f);       // +1 more if it secured a kill
        // misty blood cone VFX
        Game.I.NetMgr?.BroadcastVfx(3, new Vector3(o.X, o.Y, o.Z), fwd, 0f, 0f, DamageTypes.Col(DamageType.Blood));
        var mist = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = reach * 0.7f, BottomRadius = 0.4f, Height = reach } };
        mist.MaterialOverride = Game.ToonEmissive(DamageTypes.Col(DamageType.Blood), 1.2f, 0.05f);
        Game.I.AddChild(mist);
        mist.GlobalPosition = o + fwd * (reach * 0.5f);
        mist.LookAt(mist.GlobalPosition + fwd, Vector3.Up);
        mist.RotateObjectLocal(Vector3.Right, Mathf.Pi / 2f);
        var tw = mist.CreateTween();
        tw.TweenProperty(mist, "transparency", 1f, 0.35f);
        tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(mist)) mist.QueueFree(); }));
        Game.I.SpawnBloodMist(o + fwd * (reach * 0.30f), reach * 0.35f);   // (NEW) more effects: drifting mist + droplets along the cone
        Game.I.SpawnBloodMist(o + fwd * (reach * 0.60f), reach * 0.45f);
        CamKick(0.4f);
    }

    // Combo grows faster when you *chain different actions* (light → charged → finisher),
    // and slower if you just spam one. Switching also flags a brief "fresh" flourish.
    private void AddCombo(int baseGain, ComboAct act)
    {
        int gain = baseGain;
        bool fresh = _lastAct != ComboAct.None && act != _lastAct;
        if (fresh) gain += 2;
        else if (act == _lastAct) gain = Mathf.Max(1, baseGain - 1);
        if (Now - ComboT <= S.ComboWindow) Combo += gain; else Combo = gain;
        ComboT = Now;
        _lastAct = act;
        if (Combo > BestCombo) BestCombo = Combo;
        Game.I?.AccrueCombo(gain);
        TickMinors(gain);
        if (VerdantWitch)
        {
            if (CountEnts() >= MaxEnts) _entCombo = 0;   // at the cap: don't bank progress toward the next ent
            else
            {
                _entCombo += gain;
                while (_entCombo >= GroveEvery && CountEnts() < MaxEnts) { _entCombo -= GroveEvery; SummonEnt(); }
            }
        }
        if (fresh)
        {
            FreshHit = true; FreshT = 0.5f;
            Game.I.Sfx?.Chord(Combo);
            Game.I.Hud?.ComboFlourish(act);
        }
    }

    public float StunT = 0f;
    public int GrabbedBy = 0;   // (NEW) Taker NetId holding this player (0 = free); locks input + snaps to the grasp
    public void Stun(float dur, Vector3? src = null)
    {
        StunT = Mathf.Max(StunT, dur);
        HurtT = 0.7f;
        if (src.HasValue) { DmgDirWorld = src.Value - GlobalPosition; DmgDirWorld.Y = 0; DmgDirT = 1.2f; }
        Game.I.Sfx?.Thunder();
    }

    public void DrainShield(float frac, Vector3? src = null)
    {
        if (Shield <= 0.01f) return;
        _combatT = 0f;   // being hit/dispelled = in combat (NEW)
        Shield = Mathf.Max(0f, Shield - MaxShield * frac);
        _shieldT = (Shield <= 0.01f) ? S.ShieldDelay * 2.4f : S.ShieldDelay;
        HurtT = 0.7f;
        if (src.HasValue) { DmgDirWorld = src.Value - GlobalPosition; DmgDirWorld.Y = 0; DmgDirT = 1.2f; }
        Game.I.Sfx?.Thunder();
    }

    public void OnHit(Enemy e, bool killed, Bolt b)
    {
        if (b == null) return;
        OnHitCore(e, killed, b.Dmg, b.DType, b.Normal, b.Charged, b.ComboShot);
        // Divine "Twin Light": a Holy mote forks to nearby foes on hit (forked motes don't re-fork)
        if (MoteFork > 0 && DivineWitch && b.Normal && b.DType == DamageType.Holy && !b.Forked && Game.I != null)
        {
            var near = new System.Collections.Generic.List<Enemy>();
            foreach (var en in Game.I.Enemies.ToArray())
                if (en != null && !en.Dead && en != e && GodotObject.IsInstanceValid(en) && en.GlobalPosition.DistanceTo(e.GlobalPosition) < 12f) near.Add(en);
            near.Sort((a, c2) => e.GlobalPosition.DistanceTo(a.GlobalPosition).CompareTo(e.GlobalPosition.DistanceTo(c2.GlobalPosition)));
            for (int i = 0; i < MoteFork && i < near.Count; i++)
            {
                var tgt = near[i];
                var dir = (tgt.GlobalPosition - e.GlobalPosition).Normalized();
                var fb = SpawnBolt(e.GlobalPosition + dir * 0.5f + Vector3.Up * 0.5f, dir * 44f, b.Dmg * 0.6f, 0, 0.4f,
                    DamageTypes.Col(DamageType.Holy), DamageType.Holy, normal: true, charged: false, combo: false, full: false, homing: true);
                fb.Target = tgt; fb.SeekLockedOnly = true; fb.HomeSpeed = 44f; fb.Turn = 7f; fb.Forked = true;
            }
        }
    }

    // bolt-free hit (used by the holy ray and other non-projectile attacks)
    public void OnHitDirect(Enemy e, bool killed, float dmg, DamageType dt)
        => OnHitCore(e, killed, dmg, dt, normal: false, charged: true, combo: true);

    // bolt-free NORMAL hit — for melee/instant primaries (e.g. the Gale punch). Registers as a normal attack so it
    // restores mana on hit (S.ManaGain) just like every other witch's primary, instead of being treated as charged. (NEW)
    public void OnHitDirectNormal(Enemy e, bool killed, float dmg, DamageType dt)
        => OnHitCore(e, killed, dmg, dt, normal: true, charged: false, combo: true);

    private void OnHitCore(Enemy e, bool killed, float dmg, DamageType dt, bool normal, bool charged, bool combo)
    {
        DmgWindow += dmg;   // shared with allies as ult charge
        var _st = Game.I.MyStats;   // (NEW) end-of-run tally (kills are host-authoritative — tracked in Enemy.Die, not here)
        _st.DamageDealt += dmg;
        if (dmg > _st.BiggestHit) _st.BiggestHit = dmg;
        if (e != null && e.IsBoss) _st.BossDamage += dmg;
        if (Ult != UltKind.None && !UltActive)
        {
            float k = Mathf.Min(0.022f, dmg * 0.0004f);   // capped per hit — no longer runs away with damage scaling
            if (dt == DamageType.Lunar && Game.I.IsNight && NightAffinity) k *= 1.6f;   // her lunar ult also charges faster at night
            if (killed) k += 0.012f;                       // kills feed the meter so melee/AoE isn't starved vs ranged
            UltCharge = Mathf.Min(1f, UltCharge + k * UltChargeMul);
        }
        if (Ult == UltKind.Eclipse && UltActive && ModEclipse)
        {
            Heal(dmg * 0.08f);
            e.Slow(1.2f, 0.5f);
        }
        if (Ult == UltKind.Eclipse && UltActive && killed)   // each kill stretches the eclipse (capped so it can't run forever)
        {
            UltActiveT = Mathf.Min(30f, UltActiveT + 0.5f);
            _eclipseMax = Mathf.Max(_eclipseMax, UltActiveT);
        }
        if (killed) StormformOnKill();   // a kill while Stormform is up extends it (NEW)
        // (NEW) on-kill legendary bursts — throttled so an AoE-clear frame can't spam StormForce
        if (killed && _killProcCd <= 0f && e != null && GodotObject.IsInstanceValid(e) && (GravityWell || Bloodbath))
        {
            _killProcCd = 0.25f;
            if (GravityWell && WitchIndex == 0)   // Lunar: the slain foe collapses, dragging nearby enemies inward
            {
                float rr = 6.5f * S.SpellArea;
                Game.I.NetMgr?.StormForce(e.GlobalPosition, rr, 0, 10f);   // mode 0 = pull-in
                Ring(e.GlobalPosition, DamageTypes.Col(DamageType.Lunar), rr, 0.35f);
                Game.I.Sfx?.Release(DamageType.Lunar);
            }
            if (Bloodbath && CrimsonWitch)        // Crimson: a kill bursts blood — heal yourself + damage nearby foes
            {
                float rr = 4.5f * S.SpellArea;
                Heal(S.MaxHp * 0.06f);
                Game.I.NetMgr?.StormForce(e.GlobalPosition, rr, 2, Base() * 0.7f * ComboMul());   // mode 2 = area damage
                Ring(e.GlobalPosition, DamageTypes.Col(DamageType.Blood), rr, 0.35f);
                Game.I.Sfx?.Release(DamageType.Blood);
            }
        }
        if (normal) AddMana(S.ManaGain);
        if (killed && charged) AddMana(0.5f);
        if (_chargedRefund && charged) { AddMana(1f); _chargedRefund = false; }   // right-click pays back a mana when it connects
        if (S.Lifesteal > 0) { Heal(dmg * S.Lifesteal); if (CrimsonWitch) _st.Highlight += dmg * S.Lifesteal; }   // Crimson highlight = health leeched
        if (CrimsonWitch && Game.I != null && dmg > 0f)   // (NEW) lone-target sustain: if only ONE enemy is in her aura, damaging it heals 10%→(scales w/ level) of the damage
        {
            int inAura = 0;
            foreach (var ae in Game.I.Enemies)
            {
                if (ae == null || !GodotObject.IsInstanceValid(ae) || ae.Dead || ae.Remote || ae.IsGoblin) continue;
                var d = ae.GlobalPosition - GlobalPosition; d.Y = 0f;
                if (d.Length() < AuraRadius) { inAura++; if (inAura > 1) break; }
            }
            if (inAura == 1) Heal(dmg * (0.10f + Mathf.Min(Level, 40) * 0.005f));   // 10% + 0.5%/level, capped at 30%
        }
        if (combo)
        {
            AddCombo(charged ? 2 : 1, charged ? ComboAct.Charged : ComboAct.Light);
            foreach (var f in Fin)
            {
                if (FinMeta.Passive(f.Type) || f.Armed) continue;
                if (f.Type == FinType.EmberFervor && EmberFervorActive) { f.Charge = 0; continue; }   // (NEW) can't rebuild Ember Fervor while its buff is up
                f.Charge++;
                if (f.Charge >= f.Every) { f.Armed = true; f.Window = 3.2f; ProcFlash = 0.3f; }
            }
        }
    }

    // ---- charge modifiers (full-charge AoE at impact) ----
    public bool OwnsModifier(ModType t) => Mods.Exists(m => m.Type == t);
    public bool ModifierFull => Mods.Count >= S.ModSlots;
    public void EquipModifier(ModType t, float mag, Rarity r)
    {
        var ex = Mods.Find(m => m.Type == t);
        if (ex != null) { if ((int)r >= (int)ex.Rarity) { ex.Rarity = r; ex.Mag = mag; } }
        else Mods.Add(new Modifier { Type = t, Mag = mag, Rarity = r });
        SetModStats(t, mag, r);
    }
    public void ReplaceModifier(int idx, ModType t, float mag, Rarity r)
    {
        if (idx < 0 || idx >= Mods.Count) return;
        Mods[idx] = new Modifier { Type = t, Mag = mag, Rarity = r };
        SetModStats(t, mag, r);
    }
    private void SetModStats(ModType t, float mag, Rarity r)
    {
        if (t == ModType.HexMark) { S.MarkJumps = (int)r + 1; S.MarkAmp = 1.25f + 0.03f * mag; }
    }

    public void ApplyChargedMods(Vector3 pos)
    {
        Game.I.PlayerSound(pos, 2.4f);   // charged right-click noise (2nd loudest)
        foreach (var m in Mods)
        {
            float mag = m.Mag;
            switch (m.Type)
            {
                case ModType.Frost:
                    foreach (var e in Game.I.Enemies.ToArray()) if (!e.Dead && Flat(e, pos) < 8f) e.Slow(3f, Mathf.Clamp(0.6f - 0.05f * mag, 0.3f, 0.6f));
                    Ring(pos, DamageTypes.Col(DamageType.Frost), 7f, 0.4f);
                    Game.I.SpawnGroundSpikes(pos, 7f, 12, DamageTypes.Col(DamageType.Frost), 1.4f); Game.I.Sfx?.ModFrost(pos); break;
                case ModType.Bramble:
                    foreach (var e in Game.I.Enemies.ToArray()) if (!e.Dead && Flat(e, pos) < 7f) e.Root(1.6f + 0.3f * mag);
                    Ring(pos, DamageTypes.Col(DamageType.Nature), 6f, 0.45f);
                    Game.I.SpawnBramblePatch(pos, 6.5f, 3f); Game.I.Sfx?.ModBramble(pos); break;
                case ModType.Sunder:
                    float d = Base() * 0.9f * (0.8f + 0.1f * mag), rad = 7f + mag;
                    foreach (var e in Game.I.Enemies.ToArray()) if (!e.Dead && Flat(e, pos) < rad && !Game.I.SightBlocked(pos, e.GlobalPosition)) e.Hurt(d, DamageType.Ember, false);
                    Game.I.DamageWorld(pos, rad, d);   // (NEW) AoE breaks props too
                    Ring(pos, DamageTypes.Col(DamageType.Ember), rad, 0.4f);
                    Game.I.SpawnEmberBurst(pos, rad); Game.I.Sfx?.ModEmber(pos); break;
                case ModType.Moonbeam:
                    var f = new GroundField { Type = FieldType.Moon, Radius = 3.2f, Dur = 6f, Power = Base() * 0.8f, DType = DamageType.Lunar };
                    Game.I.AddChild(f); f.GlobalPosition = new Vector3(pos.X, 0.04f, pos.Z);
                    Game.I.SpawnLightPillar(pos, DamageTypes.Col(DamageType.Lunar).Lerp(Colors.White, 0.3f), 3.2f, 16f, 0.6f); Game.I.Sfx?.ModChime(pos); break;
                case ModType.HexMark:
                    Enemy best = null; float bd = 1e9f;
                    foreach (var e in Game.I.Enemies.ToArray()) { if (e.Dead) continue; float dd = Flat(e, pos); if (dd < 8f && dd < bd) { bd = dd; best = e; } }
                    best?.Mark(3f, S.MarkAmp, S.MarkJumps);
                    Game.I.SpawnGroundSigil(pos, 4f, DamageTypes.Col(DamageType.Curse)); Ring(pos, DamageTypes.Col(DamageType.Curse), 4f, 0.4f); Game.I.Sfx?.ModCurse(pos); break;
                case ModType.Consecrate:
                    var cf = new GroundField { Type = FieldType.Heal, HealAllies = true, EnemyDmg = Base() * (0.4f + 0.08f * mag), Radius = 3.4f, Dur = 5f, Power = S.MaxHp * 0.02f, DType = DamageType.Holy, TintColor = DamageTypes.Col(DamageType.Holy), FromCombo = true };
                    Game.I.AddChild(cf); cf.GlobalPosition = new Vector3(pos.X, 0.04f, pos.Z);
                    Game.I.SpawnGroundSigil(pos, 3.4f, DamageTypes.Col(DamageType.Holy)); Game.I.SpawnLightPillar(pos, DamageTypes.Col(DamageType.Holy), 3.4f, 13f, 0.55f); Game.I.Sfx?.ModHoly(pos); break;
                case ModType.Smite:
                    Enemy sm = null; float sd = 1e9f;
                    foreach (var e in Game.I.Enemies.ToArray()) { if (e.Dead) continue; float dd = Flat(e, pos); if (dd < 10f && dd < sd) { sd = dd; sm = e; } }
                    if (sm != null)
                    {
                        sm.Hurt(Base() * (1.4f + 0.2f * mag), DamageType.Holy, false);
                        sm.Slow(2f, 0.5f);
                        Lance(sm.GlobalPosition);
                        Game.I.Sfx?.ModSmite(sm.GlobalPosition);
                        Heal(S.MaxHp * 0.015f);
                    }
                    break;
                case ModType.Hemorrhage:
                    foreach (var e in Game.I.Enemies.ToArray())
                        if (!e.Dead && Flat(e, pos) < 6.5f && !Game.I.SightBlocked(pos, e.GlobalPosition)) e.Bleed(Base() * (0.25f + 0.05f * mag), 4f, false);
                    Ring(pos, DamageTypes.Col(DamageType.Blood), 6f, 0.4f);
                    Game.I.SpawnBloodMist(pos, 6.5f); Game.I.Sfx?.ModBlood(pos); break;
                case ModType.CrimsonPool:
                    var cp = new GroundField { Type = FieldType.Hex, Radius = 4f + 0.3f * mag, Dur = 5f, Power = Base() * (0.4f + 0.06f * mag), DType = DamageType.Blood, TintColor = DamageTypes.Col(DamageType.Blood), FromCombo = true, SlowMul = 0.6f, GrantsBlood = true };
                    Game.I.AddChild(cp); cp.GlobalPosition = new Vector3(pos.X, 0.04f, pos.Z);
                    Game.I.SpawnBloodMist(pos, 4f); Game.I.Sfx?.ModPour(pos); break;
                case ModType.SanguineSpikes:
                    float sr = 6f + mag, sdmg = Base() * (0.7f + 0.1f * mag);
                    int sgHits = 0;
                    foreach (var e in Game.I.Enemies.ToArray())
                        if (!e.Dead && Flat(e, pos) < sr && !Game.I.SightBlocked(pos, e.GlobalPosition)) { e.Hurt(sdmg, DamageType.Blood, false); BloodReward(0.34f); sgHits++; }
                    Game.I.DamageWorld(pos, sr, sdmg);   // (FIX) AoE breaks props too
                    if (sgHits > 0) Game.I.NetMgr?.BloodAlliesNear(pos, 9999f, 0.34f * sgHits);   // blood AoE shares stacks with all wardens
                    Ring(pos, DamageTypes.Col(DamageType.Blood), sr, 0.4f);
                    Game.I.SpawnGroundSpikes(pos, sr, 14, DamageTypes.Col(DamageType.Blood), 1.1f); Game.I.Sfx?.ModSpike(pos); break;
                case ModType.Implosion:   // a lingering vortex that drags survivors in over a few seconds — less burst, much stronger pull (NEW)
                {
                    float irad = 8f + mag;
                    float idmg = Base() * (0.35f + 0.06f * mag);   // reduced initial hit (was 0.8 + 0.1*mag)
                    var icol = DamageTypes.Col(DamageType.Wind);
                    Game.I.NetMgr?.StormForce(pos, irad, 2, idmg);                 // a light opening hit (host-authoritative; routes for clients)
                    // the implosion lingers as a vortex: continuous, extra-strong drag + light grind for its duration
                    float idur = 2.6f + mag * 0.15f, idps = Base() * (0.45f + 0.05f * mag);
                    var cyI = new Cyclone(); Game.I.AddChild(cyI);
                    cyI.Init(this, new Vector3(pos.X, 0f, pos.Z), irad, idur, idps, true, false, 2.2f, suppressVisual: true);   // mechanics only — WindOrb is the look now (NEW LOOK)
                    var orb = new WindOrb(); Game.I.AddChild(orb);
                    orb.Init(new Vector3(pos.X, 0f, pos.Z), irad, idur);   // rasengan sphere at center + inward-spiraling gusts across the AoE
                    Game.I.NetMgr?.BroadcastVfx(15, new Vector3(pos.X, 0f, pos.Z), Vector3.Up, irad, idur, icol);   // allies see the wind orb
                    Ring(pos, icol, irad, 0.4f);
                    Game.I.Sfx?.ModWind(pos);
                    break;
                }
                case ModType.Whirlwind:   // spawn a stationary tornado: grinds foes + jump-pad for all players (NEW)
                {
                    float wrad = 3.2f + mag * 0.25f, wdur = 6f + mag * 0.5f, wdps = Base() * (0.5f + 0.08f * mag);
                    var pad = new WindPad(); Game.I.AddChild(pad);
                    pad.Init(this, new Vector3(pos.X, 0f, pos.Z), wrad, wdur, wdps, false);
                    Game.I.NetMgr?.BroadcastVfx(12, new Vector3(pos.X, 0f, pos.Z), Vector3.Up, wrad, wdur, DamageTypes.Col(DamageType.Wind));   // allies get a visual + jump-pad copy
                    Ring(pos, DamageTypes.Col(DamageType.Wind), wrad, 0.5f);
                    Game.I.Sfx?.ModWind(pos);
                    break;
                }
                case ModType.Meteor:   // (NEW Ember) call down a meteor where the charge lands (Ember witch → two meteors)
                {
                    float mrad = 6f + mag, mdmg = Base() * (2.2f + 0.3f * mag) * ComboMul();
                    var at = new Vector3(pos.X, Game.I.SurfaceHeight(pos, pos.Y), pos.Z);
                    Game.I.SpawnEmberMeteor(at, mrad, mdmg, 3 + (int)mag, Base() * 0.09f, Base() * 3.2f, this);   // host-authoritative + broadcasts a ghost (kind 67)
                    break;
                }
                case ModType.Eruption:   // (NEW Ember) molten upheaval + flame ring; knocks back, higher rarity flings the small ones skyward
                {
                    float erad = 7f + mag, edmg = Base() * (1.2f + 0.15f * mag), power = 8f + mag * 3f;
                    foreach (var e in Game.I.Enemies.ToArray())
                        if (!e.Dead && GodotObject.IsInstanceValid(e) && Flat(e, pos) < erad && !Game.I.SightBlocked(pos, e.GlobalPosition))
                        { e.Hurt(edmg, DamageType.Ember, false); e.AddBurn(1f, Base() * 0.08f, Base() * 3.2f, 0f, Game.I.LocalPeer); }
                    Game.I.NetMgr?.StormForce(pos, erad, 1, power);   // host-authoritative outward+up fling (mass-scaled: small foes fly, big resist)
                    Game.I.DamageWorld(pos, erad, edmg);
                    Game.I.SpawnGroundSpikes(pos, erad, 16, new Color(0.32f, 0.13f, 0.06f), 1.6f);   // molten rock heaving up
                    Game.I.SpawnEmberBurst(pos, erad);   // flame ring bursting outward
                    Ring(pos, DamageTypes.Col(DamageType.Ember), erad * 1.2f, 0.4f);
                    Game.I.Sfx?.ModEmber(pos);
                    break;
                }
            }
        }
    }

    private static float Flat(Enemy e, Vector3 p) => new Vector2(e.GlobalPosition.X - p.X, e.GlobalPosition.Z - p.Z).Length();

    // ---- finishers ----
    public bool OwnsFinisher(FinType t) => Fin.Exists(f => f.Type == t);
    public int FinisherRank(FinType t) { var f = Fin.Find(x => x.Type == t); return f != null ? (int)f.Rarity : -1; }   // (NEW) -1 = unowned, else the owned rarity as int (for the shop's "unowned-or-upgrade" filter)
    public int ModifierRank(ModType t) { var m = Mods.Find(x => x.Type == t); return m != null ? (int)m.Rarity : -1; }
    private int CrescendoEvery() { var f = Fin.Find(s => s.Type == FinType.Crescendo); return f != null ? f.Every : 0; }

    // every witch damage source (AoE ticks, crescents, etc.) feeds the combo — throttled so ticks don't spam it
    private float _srcComboCd = 0f;
    // AoE / field / secondary hits funnel through here (throttled so one AoE cast = one tick, not one per foe).
    // (NEW) It now takes part in the WEAVE system: switching source (e.g. Light primary -> Charged slam) flags a
    // fresh weave for +2 and a flourish, exactly like the light<->charged weave the direct casts already get.
    // Before, this did a flat +1 and never touched _lastAct, so witches whose secondary routes through here
    // (Gale slam, crescent orb, ground fields, holy AoE) never rewarded a weave. Default act = Charged.
    public void ComboFromSource(ComboAct act = ComboAct.Charged)
    {
        if (_srcComboCd > 0f) return;
        _srcComboCd = 0.15f;
        int gain = 1;
        bool fresh = _lastAct != ComboAct.None && act != _lastAct;   // switched source since the last hit
        if (fresh) gain += 2;                                        // weave bonus (matches the light<->charged +2)
        if (Now - ComboT <= S.ComboWindow) Combo += gain; else Combo = gain;
        ComboT = Now;
        _lastAct = act;                                              // (NEW) so the NEXT hit can see this source
        if (Combo > BestCombo) BestCombo = Combo;
        if (Ult != UltKind.None && !UltActive) UltCharge = Mathf.Min(1f, UltCharge + 0.004f * UltChargeMul);   // combo also charges the ult
        Game.I?.AccrueCombo(gain);
        if (fresh) { FreshHit = true; FreshT = 0.5f; Game.I.Sfx?.Chord(Combo); Game.I.Hud?.ComboFlourish(act); }
    }

    // (NEW) Poison / bleed DoT ticks trickle the combo up slowly and keep it alive while they burn. Throttled
    // hard (~1s) so a DoT ticking across many foes is a slow drip, not a combo fountain — roughly "1 per 2-3
    // ticks" (poison ticks 0.4s, bleed 0.3s). Deliberately does NOT touch _lastAct, so a DoT tick landing
    // between your primary and secondary can't accidentally break your weave detection.
    private float _dotComboCd = 0f;
    public void ComboFromDot()
    {
        if (_dotComboCd > 0f) return;
        _dotComboCd = 1.0f;
        if (Now - ComboT <= S.ComboWindow) Combo += 1; else Combo = 1;
        ComboT = Now;   // your own DoT keeps the combo window open while it ticks
        if (Combo > BestCombo) BestCombo = Combo;
        Game.I?.AccrueCombo(1);
    }
    public bool FinisherFull => Fin.Count >= S.FinSlots;
    private static readonly Key[] DefaultFinKeys = { Key.Key1, Key.Key2, Key.Key3, Key.Key4, Key.Key5 };
    public void EquipFinisher(FinType t, int every, float pow, Rarity r)
    {
        var ex = Fin.Find(s => s.Type == t);
        if (ex != null) { if ((int)r >= (int)ex.Rarity) { ex.Rarity = r; ex.Every = every; ex.Pow = pow; } return; }
        var slot = new FinisherSlot { Type = t, Every = every, Pow = pow, Rarity = r, Bind = Key.None };
        foreach (var dk in DefaultFinKeys)
            if (!Fin.Exists(s => s.Bind == dk)) { slot.Bind = dk; break; }   // first free 1-5 key (no collision even if binds were shuffled)
        Fin.Add(slot);
    }
    public void ReplaceFinisher(int idx, FinType t, int every, float pow, Rarity r)
    {
        if (idx < 0 || idx >= Fin.Count) return;
        var keep = Fin[idx].Bind;
        Fin[idx] = new FinisherSlot { Type = t, Every = every, Pow = pow, Rarity = r, Bind = keep };
    }

    // ---- minor passive auto-finishers ----
    private Enemy NearestEnemy(float maxR = 40f)
    {
        Enemy best = null; float bd = maxR * maxR;
        foreach (var e in Game.I.Enemies.ToArray())
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
            float d = (e.GlobalPosition - GlobalPosition).LengthSquared();
            if (d < bd) { bd = d; best = e; }
        }
        return best;
    }
    private void MinorBolt(DamageType ty, float coef, int stacks)
    {
        var tgt = NearestEnemy();
        Vector3 from = _cam.GlobalPosition;
        Vector3 dir = tgt != null ? (tgt.GlobalPosition - from).Normalized() : (-_cam.GlobalTransform.Basis.Z).Normalized();
        float dmg = Base() * coef * (1f + 0.12f * (stacks - 1));
        SpawnBolt(from + dir * 1.2f, dir * 36f, dmg, 0, 0.35f, DamageTypes.Col(ty), ty, false, false, true, false, homing: true, life: 2.2f, fromCombo: true);
        if (ty == DamageType.Blood) Heal(S.MaxHp * 0.005f * stacks);   // Bloodlet leeches a little
    }
    // (NEW) Shared AoE application — the go-forward way to build area attacks. Hits every live enemy within
    // `radius` of `center` AND breaks world props (pumpkins) in the same radius, so anything built on this can
    // never "forget" to break props. `onHit` runs once per hit enemy for extras (knockback/slow/root/etc.).
    // Returns the number of enemies hit. NOTE: the existing bespoke AoE loops are intentionally left untouched —
    // they already call DamageWorld — so this changes no current behavior; it's for new abilities/combos.
    // TODO (optional cleanup, no functional need): migrate the ~16 bespoke AoE loops in this file onto AoeHit,
    // ONE site per turn so each stays individually testable, to centralise prop-breaking and per-enemy handling.
    private int AoeHit(Vector3 center, float radius, float dmg, DamageType type, System.Action<Enemy> onHit = null,
                       bool combo = true, bool crit = false, bool includeEnemyRadius = true, ComboAct comboAct = ComboAct.Charged)
    {
        int hits = 0;
        foreach (var e in Game.I.Enemies.ToArray())
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
            float rr = includeEnemyRadius ? radius + e.Radius : radius;
            if (Flat(e, center) > rr) continue;
            e.Hurt(dmg, type, true, crit);
            onHit?.Invoke(e);
            if (combo) ComboFromSource(comboAct);
            hits++;
        }
        Game.I.DamageWorld(center, radius, dmg);   // props break from the same AoE, automatically
        return hits;
    }

    private void MinorAoE(DamageType ty, float coef, float radius, int stacks)
    {
        float dmg = Base() * coef * (1f + 0.12f * (stacks - 1));
        float r = radius + 0.4f * (stacks - 1);
        foreach (var e in Game.I.Enemies.ToArray())
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
            if (Flat(e, GlobalPosition) < r + e.Radius) { e.Hurt(dmg, ty, true); ComboFromSource(); }
        }
        Game.I.DamageWorld(GlobalPosition, r, dmg);   // (FIX) AoE breaks props too
        Ring(GlobalPosition, DamageTypes.Col(ty), r, 0.3f);
    }
    private void FireMinor(MinorType t, int stacks)
    {
        switch (t)
        {
            case MinorType.MoonMote: MinorBolt(DamageType.Lunar, 0.4f, stacks); break;
            case MinorType.LunarFlare: MinorAoE(DamageType.Lunar, 0.35f, 4f, stacks); break;
            case MinorType.ArcaneDart: MinorBolt(DamageType.Arcane, 0.45f, stacks); break;
            case MinorType.ManaSpark: MinorAoE(DamageType.Arcane, 0.35f, 4f, stacks); break;
            case MinorType.ThornSnap: { var e = NearestEnemy(18f); if (e != null) { e.Root(0.8f + 0.1f * stacks); e.Hurt(Base() * 0.3f, DamageType.Nature, true); } break; }
            case MinorType.Sporeling: MinorAoE(DamageType.Nature, 0.35f, 4f, stacks); break;
            case MinorType.FrostNip: { var e = NearestEnemy(22f); if (e != null) { e.Slow(2f, 0.55f); e.Hurt(Base() * 0.3f, DamageType.Frost, true); } break; }
            case MinorType.IcePrick: MinorAoE(DamageType.Frost, 0.3f, 4f, stacks); break;
            case MinorType.HexWisp: { var e = NearestEnemy(22f); if (e != null) e.Mark(3f, S.MarkAmp, 0); break; }
            case MinorType.RotTick: MinorAoE(DamageType.Curse, 0.35f, 4f, stacks); break;
            case MinorType.Glimmer: Heal(S.MaxHp * (0.015f + 0.004f * (stacks - 1))); Ring(GlobalPosition, DamageTypes.Col(DamageType.Holy), 2.5f, 0.3f); break;
            case MinorType.RadiantMote: MinorBolt(DamageType.Holy, 0.4f, stacks); break;
            case MinorType.Cinder: MinorBolt(DamageType.Ember, 0.4f, stacks); break;
            case MinorType.Ashflare: MinorAoE(DamageType.Ember, 0.35f, 4f, stacks); break;
            case MinorType.Bloodlet: MinorBolt(DamageType.Blood, 0.4f, stacks); break;
            case MinorType.Clot: MinorAoE(DamageType.Blood, 0.3f, 3.5f, stacks); BloodReward(0.34f * stacks); Game.I.NetMgr?.BloodAlliesNear(GlobalPosition, 9999f, 0.34f * stacks); break;
            case MinorType.Gust: { MinorBolt(DamageType.Wind, 0.4f, stacks); var e = NearestEnemy(20f); if (e != null) e.Knockback(GlobalPosition, 1.2f + 0.2f * stacks); break; }   // (NEW)
            case MinorType.Zephyr: MinorAoE(DamageType.Wind, 0.35f, 4f, stacks); break;   // (NEW)
        }
    }

    private void FireFinisher(int idx)
    {
        if (idx >= Fin.Count) return;
        var f = Fin[idx];
        if (FinMeta.Passive(f.Type)) return;   // Crescendo is passive — occupies a slot, never key-fired
        Game.I.PlayerSound(GlobalPosition, 1.6f);   // spell-combo noise
        if (!f.Armed) { FinNotReady(f); return; }
        if (CrimsonWitch)
        {
            float cost = S.MaxHp * FinHpCost;
            if (Hp <= cost + 1f) { ResFail(); return; }   // can't pay the blood price
            Hp -= cost; HurtT = 0.3f;
        }
        else if (!SpendMana(1f)) return;   // finishers cost 1 mana (was 2 → silently failed when you had 1–1.9) (NEW)
        f.Armed = false; f.Charge = 0; f.Window = 0;
        Execute(f);
        AddCombo(3, ComboAct.Finisher);
        CamKick(0.6f);
    }

    private void SetArm(string kind, float dur) { _animKind = kind; _animT = 0; _animDur = dur; Game.I.NetMgr?.BroadcastArm(kind, dur); }   // (NEW) broadcast → all cast poses sync in MP

    private void Execute(FinisherSlot f)
    {
        int t = Tier(f.Rarity); float pow = f.Pow;
        var col = FinMeta.Col(f.Type);
        switch (f.Type)
        {
            case FinType.Wave: FinWave(pow, t, col); SetArm("raise", 0.45f); break;
            case FinType.Beam: StartBeam(pow, t); SetArm("thrust", 0.3f); break;
            case FinType.Volley: FinVolley(pow, t, col); SetArm("thrust", 0.5f); break;
            case FinType.Fullmod: FinFull(pow, col); SetArm("together", 0.42f); break;
            case FinType.Heal: FinHeal(pow, t); SetArm("palmsup", 0.6f); break;
            case FinType.Root: FinRoot(pow, t, col); SetArm("slam", 0.45f); break;
            case FinType.Swarm: FinSwarm(pow, t, col); SetArm("thrust", 0.5f); break;
            case FinType.HexField: FinHexField(pow, t); SetArm("palmsup", 0.6f); break;
            case FinType.Halo: FinHalo(pow, t); SetArm("palmsup", 0.5f); break;
            case FinType.Lance: FinLance(pow, t); SetArm("thrust", 0.5f); break;
            case FinType.BloodNova: FinBloodNova(pow, t); SetArm("slam", 0.45f); break;
            case FinType.CrimsonRush: FinCrimsonRush(pow, t); SetArm("thrust", 0.4f); break;
            case FinType.BloodCurse: FinBloodCurse(pow, t); SetArm("together", 0.42f); break;
            case FinType.PoisonField: FinPoisonField(pow, t); SetArm("palmsup", 0.55f); break;
            case FinType.SeedMine: FinSeedMine(pow, t); SetArm("thrust", 0.45f); break;
            case FinType.ThornSkin: FinThornSkin(pow, t); SetArm("together", 0.45f); break;
            case FinType.Updraft: FinUpdraft(pow, t, col); SetArm("palmsup", 0.5f); break;       // (NEW)
            case FinType.WindRush: FinWindRush(pow, t, col); SetArm("thrust", 0.4f); break;       // (NEW)
            case FinType.WindSlice: FinWindSlice(pow, t, col); SetArm("thrust", 0.45f); break;    // (NEW)
            case FinType.IceSpike: FinIceSpike(pow, t, col); SetArm("thrust", 0.4f); break;        // (NEW)
            case FinType.FrostVault: FinFrostVault(pow, t, col); SetArm("slam", 0.4f); break;      // (NEW)
            case FinType.FrostWalls: FinFrostWalls(pow, t, col); SetArm("together", 0.4f); break;  // (NEW)
            case FinType.SoulReap: FinSoulReap(pow, t, col); SetArm("draw", 0.5f); break;           // (NEW Curse)
            case FinType.HexChains: FinHexChains(pow, t, col); SetArm("together", 0.45f); break;    // (NEW Curse)
            case FinType.DoomSigil: FinDoomSigil(pow, t, col); SetArm("palmsup", 0.5f); break;      // (NEW Curse)
            case FinType.FireWall: FinFireWall(pow, t, col); SetArm("raise", 0.45f); break;          // (NEW Ember)
            case FinType.Fireball: FinFireball(pow, t, col); SetArm("thrust", 0.4f); break;          // (NEW Ember)
            case FinType.EmberFervor: FinEmberFervor(pow, t); SetArm("together", 0.45f); break;      // (NEW Ember)
        }
    }

    // ===== Curse finishers (NEW) — witch-agnostic, but they lean into the curse fantasy =====
    // Soul Reap: a cursed reaping nova that bites harder the more wounded each foe is, and siphons souls to mend you.
    private void FinSoulReap(float pow, int t, Color col)
    {
        float radius = 8f + t * 0.8f, baseDmg = Base() * 1.2f * pow, healTotal = 0f;
        foreach (var e in Game.I.Enemies.ToArray())
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
            if (Flat(e, GlobalPosition) >= radius || Game.I.SightBlocked(GlobalPosition, e.GlobalPosition)) continue;
            float missing = e.MaxHp > 0f ? Mathf.Clamp(1f - e.Hp / e.MaxHp, 0f, 1f) : 0f;
            float dmg = baseDmg * (1f + 1.6f * missing);   // reaps the wounded — up to 2.6× on the nearly-dead
            e.Hurt(dmg, DamageType.Curse, true);
            healTotal += dmg * 0.05f;
            SpawnSoulWisp(e.GlobalPosition + Vector3.Up * 0.9f, col);
        }
        Game.I.DamageWorld(GlobalPosition, radius, baseDmg);
        if (healTotal > 0f) Heal(Mathf.Min(healTotal, S.MaxHp * 0.18f));   // soul harvest, capped
        Game.I.SpawnScytheVfx(GlobalPosition, AimDir(), radius, col);
        Ring(GlobalPosition, col, radius, 0.5f);
        Game.I.NetMgr?.BroadcastVfx(63, GlobalPosition, new Vector3(AimDir().X, 0f, AimDir().Z), radius, 0f, col);
        Game.I.Sfx?.CurseCrush(GlobalPosition);
    }

    // Hex Chains: bind the nearest foes into a temporary shared-pain web — a share of ALL damage any of them takes bleeds to the rest.
    private void FinHexChains(float pow, int t, Color col)
    {
        float radius = 10f + t * 1f; int maxLinks = 4 + t;
        var links = new System.Collections.Generic.List<Enemy>();
        foreach (var e in Game.I.Enemies.ToArray())
            if (e != null && !e.Dead && GodotObject.IsInstanceValid(e) && Flat(e, GlobalPosition) < radius && !Game.I.SightBlocked(GlobalPosition, e.GlobalPosition)) links.Add(e);
        links.Sort((a, b) => Flat(a, GlobalPosition).CompareTo(Flat(b, GlobalPosition)));
        if (links.Count > maxLinks) links.RemoveRange(maxLinks, links.Count - maxLinks);
        int group = ++_curseGroupSeq;
        float burst = Base() * 1.4f * pow;
        foreach (var e in links)
        {
            e.AddCurse(2f, group, DamageType.Curse, 1.35f, 0.4f);   // tether into a shared-pain group (40% of damage bleeds across)
            e.Hurt(burst, DamageType.Curse, true);
            SpawnCurseChain(GlobalPosition + Vector3.Up * 1.1f, e.GlobalPosition + Vector3.Up * 0.9f, col);
        }
        Game.I.SpawnGroundSigil(GlobalPosition, radius * 0.8f, col);
        Ring(GlobalPosition, col, radius, 0.5f);
        Game.I.NetMgr?.BroadcastVfx(64, GlobalPosition, Vector3.Zero, radius, 0f, col);
        Game.I.Sfx?.WitchCackle(GlobalPosition);
    }

    // Doom Sigil: brand nearby foes, then a delayed cursed detonation (bigger the more branded). Deferred blast = DoomSigil node.
    private void FinDoomSigil(float pow, int t, Color col)
    {
        var flat = new Vector3(AimDir().X, 0f, AimDir().Z);
        flat = flat.LengthSquared() > 0.001f ? flat.Normalized() : Vector3.Forward;
        var at = GlobalPosition + flat * 6f;
        at = new Vector3(at.X, Game.I.SurfaceHeight(at, 1e9f) + 0.05f, at.Z);
        float radius = 6f + t * 0.7f, dmg = Base() * 2.4f * pow;
        int branded = 0;
        foreach (var e in Game.I.Enemies.ToArray())
            if (e != null && !e.Dead && GodotObject.IsInstanceValid(e) && new Vector2(e.GlobalPosition.X - at.X, e.GlobalPosition.Z - at.Z).Length() < radius + e.Radius)
            { e.AddCurse(1.5f, 0, DamageType.Curse, 1.35f, 0f); branded++; }
        float mul = 1f + 0.12f * Mathf.Max(0, branded - 1);
        var sig = new DoomSigil(); Game.I.AddChild(sig); sig.Init(at, radius, dmg * mul, col, this);
        Game.I.NetMgr?.BroadcastVfx(65, at, Vector3.Zero, radius, 0f, col);   // allies spawn a Remote ghost sigil (visual only)
        Game.I.Sfx?.WitchCackle(GlobalPosition);
    }

    private void SpawnSoulWisp(Vector3 at, Color col)   // a soul mote drawn back to the caster (Soul Reap heal visual)
    {
        var w = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.12f, Height = 0.24f }, MaterialOverride = Game.ToonEmissive(col.Lerp(new Color(0.45f, 1f, 0.6f), 0.4f), 3f, 0f) };
        Game.I.AddChild(w); w.GlobalPosition = at;
        var tw = w.CreateTween(); tw.SetParallel(true);
        tw.TweenProperty(w, "global_position", EyePos, 0.45f).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.In);
        tw.TweenProperty(w, "scale", Vector3.One * 0.1f, 0.45f);
        tw.Chain().TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(w)) w.QueueFree(); }));
    }

    private void SpawnCurseChain(Vector3 a, Vector3 b, Color col)   // a fading cursed chain between the caster and a bound foe
    {
        float len = (b - a).Length(); if (len < 0.2f) return;
        var chain = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.05f, BottomRadius = 0.05f, Height = len, RadialSegments = 6 } };
        var mm = Game.Emissive(col, 2.6f); mm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; chain.MaterialOverride = mm;
        Game.I.AddChild(chain);
        chain.LookAtFromPosition((a + b) * 0.5f, b, Vector3.Up);
        chain.RotateObjectLocal(Vector3.Right, Mathf.Pi / 2f);   // cylinder length runs along local Y → aim it down the link
        var tw = chain.CreateTween();
        tw.TweenInterval(0.4f);
        tw.TweenProperty(mm, "albedo_color", new Color(col.R, col.G, col.B, 0f), 0.5f);
        tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(chain)) chain.QueueFree(); }));
    }

    // ===== Ember finishers (NEW) — witch-agnostic, the fire fantasy =====
    // Ring of Fire: a planted ring of flame that EATS incoming enemy projectiles and burns foes standing in it.
    private void FinFireWall(float pow, int t, Color col)
    {
        float radius = 5f + t * 0.5f, dur = 4f + t * 0.6f, dps = Base() * 0.5f * pow;
        var center = new Vector3(GlobalPosition.X, Game.I.SurfaceHeight(GlobalPosition, GlobalPosition.Y) + 0.05f, GlobalPosition.Z);
        var w = new FireWall { Center = center, Radius = radius, Dur = dur, Dps = dps, BurnPer = Base() * 0.08f, BurnBomb = Base() * 3.2f, OwnerPeer = Game.I.LocalPeer };
        Game.I.AddChild(w); w.GlobalPosition = center;
        Game.I.RegisterFireRing(center, radius, dur);   // host-side zone that eats enemy projectiles (a client routes it to the host)
        Game.I.NetMgr?.BroadcastVfx(72, center, Vector3.Zero, radius, dur, col);   // allies render the ring
        Ring(center, col, radius, 0.5f);
        Game.I.Sfx?.Release(DamageType.Ember);
    }

    // Fireball: hurl a med-speed fireball at the cursor — heavy direct hit + a medium blast on impact.
    private void FinFireball(float pow, int t, Color col)
    {
        Vector3 dir = AimDir().Normalized();
        Vector3 origin = EyePos + dir * 0.6f;
        float directDmg = Base() * 3.2f * pow * ComboMul(), blastDmg = Base() * 1.6f * pow * ComboMul(), blastR = 4.5f + t * 0.5f;
        var fb = new Fireball { Dir = dir, Speed = 22f, DirectDmg = directDmg, BlastDmg = blastDmg, BlastRadius = blastR, BurnPer = Base() * 0.09f, BurnBomb = Base() * 3.2f, OwnerPeer = Game.I.LocalPeer, Src = this };
        Game.I.AddChild(fb); fb.GlobalPosition = origin;
        Game.I.NetMgr?.BroadcastVfx(73, origin, dir, 22f, blastR, col);   // allies render a visual ghost fireball
        Game.I.Sfx?.Cast(DamageType.Ember);
    }

    // Ember Fervor: self-buff — crit + move speed for a few seconds; fists/feet blaze; can't recharge until it fades.
    private void FinEmberFervor(float pow, int t)
    {
        float dur = 5f + t * 0.8f;
        _emberFervorCrit = (0.1f + t * 0.03f) * pow;    // ~+10% → +22% crit
        _emberFervorSpeed = (0.15f + t * 0.03f) * pow;  // ~+15% → +27% move
        EmberFervorT = dur; _fervorNetT = 0f;
        ShowFervorFlames(true);
        Ring(GlobalPosition, DamageTypes.Col(DamageType.Ember), 3f, 0.5f);
        Game.I.SpawnEmberBurst(GlobalPosition + Vector3.Up * 1f, 3f);
        Game.I.NetMgr?.BroadcastVfx(70, GlobalPosition, Vector3.Zero, 3f, 0f, DamageTypes.Col(DamageType.Ember));
        Game.I.Sfx?.Release(DamageType.Ember); Game.I.Hud?.Banner("EMBER FERVOR");
    }

    private void ShowFervorFlames(bool on)
    {
        if (!on)
        {
            foreach (var f in _fervorFlames) if (f != null && GodotObject.IsInstanceValid(f)) f.QueueFree();
            _fervorFlames.Clear();
            return;
        }
        if (_fervorFlames.Count > 0) return;
        var col = DamageTypes.Col(DamageType.Ember);
        var mounts = new System.Collections.Generic.List<(Node3D parent, Vector3 pos, float r)>();
        if (_handMeshL != null) mounts.Add((_handMeshL, Vector3.Zero, 0.14f));   // view-model fists (local view)
        if (_handMeshR != null) mounts.Add((_handMeshR, Vector3.Zero, 0.14f));
        if (_bodyModel != null) { mounts.Add((_bodyModel, new Vector3(-0.18f, 0.12f, 0f), 0.16f)); mounts.Add((_bodyModel, new Vector3(0.18f, 0.12f, 0f), 0.16f)); }   // feet on the body model
        foreach (var (parent, pos, r) in mounts)
        {
            if (parent == null || !GodotObject.IsInstanceValid(parent)) continue;
            var fl = new MeshInstance3D { Mesh = new SphereMesh { Radius = r, Height = r * 2f, RadialSegments = 6, Rings = 4 } };
            var mm = Game.Emissive(col, 3.2f); mm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; mm.AlbedoColor = new Color(1f, 0.5f, 0.15f, 0.75f); fl.MaterialOverride = mm;
            parent.AddChild(fl); fl.Position = pos;
            fl.AddChild(new OmniLight3D { OmniRange = 2f, LightColor = col, LightEnergy = 1.4f });
            _fervorFlames.Add(fl);
        }
    }

    private void Ring(Vector3 at, Color col, float grow, float life)
    {
        Game.I.VfxRing(at, col, grow, life);
        Game.I.NetMgr?.BroadcastVfx(0, at, Vector3.Zero, grow, life, col);
    }

    private void FinWave(float pow, int t, Color col)
    {
        float dmg = Base() * 2.4f * pow, radius = 10f + t * 1.5f;
        foreach (var e in Game.I.Enemies.ToArray()) if (!e.Dead && Flat(e, GlobalPosition) < radius && !Game.I.SightBlocked(GlobalPosition, e.GlobalPosition)) e.Hurt(dmg, DamageType.Curse, true);
        Game.I.DamageWorld(GlobalPosition, radius, dmg);   // (FIX) AoE breaks props too
        Ring(GlobalPosition, col, radius * 0.95f, 0.5f);
    }

    private void StartBeam(float pow, int t)
    {
        _beamPow = Base() * 7f * pow;
        _beamWidth = 2.2f + t * 0.3f;
        _beamT = 0.9f + t * 0.25f;
        // lock direction now: toward the enemy under the cursor, otherwise straight ahead (flattened)
        var flat = AimDir(); flat.Y = 0; flat = flat.LengthSquared() > 0.001f ? flat.Normalized() : Vector3.Forward;
        var tgt = AimTarget();
        if (tgt != null)
        {
            var d = tgt.GlobalPosition - EyePos; d.Y = 0;
            _beamDir = d.LengthSquared() > 0.01f ? d.Normalized() : flat;
        }
        else _beamDir = flat;
        if (_beamSeg == null)
        {
            var arc = DamageTypes.Col(DamageType.Arcane);
            _beamSeg = new SegBeam(SpellLanceSegs);
            _beamSeg.Build(Game.I, seg =>
            {
                // each layer is a square-section box built length-along-local-Y (rotated 90° X) so SegBeam can stretch/bend it
                void Layer(float g, Color c, float energy, float alpha)
                {
                    var mi = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(g, 1f, g) } };
                    var m = Game.Emissive(c, energy);
                    if (alpha < 1f) { m.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; var ac = m.AlbedoColor; ac.A = alpha; m.AlbedoColor = ac; }
                    mi.MaterialOverride = m; mi.RotationDegrees = new Vector3(90, 0, 0); seg.AddChild(mi);
                }
                Layer(0.3f, Colors.White, 6f, 1f);                     // white-hot filament
                Layer(0.75f, arc.Lerp(Colors.White, 0.45f), 4.5f, 1f); // arcane plasma core
                Layer(1.6f, arc, 2.6f, 0.45f);                         // translucent plasma sheath
                Layer(2.6f, arc, 1.8f, 0.22f);                         // soft outer glow
            });
            _beamLight = new OmniLight3D { OmniRange = 10f, LightColor = arc, LightEnergy = 3.5f };
            _beamSeg.Root.AddChild(_beamLight);
        }
        Game.I.NetMgr?.BroadcastVfx(1, EyePos, _beamDir, BeamLen, _beamWidth, DamageTypes.Col(DamageType.Arcane));
    }

    private void UpdateBeam(float dt)
    {
        _beamT -= dt;
        var dir = _beamDir; var eye = EyePos;
        foreach (var e in Game.I.Enemies.ToArray())
        {
            if (e.Dead) continue;
            var rel = e.GlobalPosition - eye;
            float proj = rel.X * dir.X + rel.Z * dir.Z;
            if (proj < 0 || proj > BeamLen) continue;
            float px = eye.X + dir.X * proj, pz = eye.Z + dir.Z * proj;
            if (new Vector2(e.GlobalPosition.X - px, e.GlobalPosition.Z - pz).Length() < _beamWidth + e.Radius) e.Hurt(_beamPow * dt, DamageType.Arcane, true);
        }
        if (_beamSeg != null)
        {
            Vector3 origin = eye + new Vector3(0, -0.25f, 0);
            Vector3 target = eye + dir * BeamLen + new Vector3(0, -0.25f, 0);
            float pl = 1f + 0.18f * Mathf.Sin(Now * 40f) + (GD.Randf() - 0.5f) * 0.12f;   // pulse + plasma flicker
            _beamSeg.Place(origin, target, dt, 8f, 24f, pl);
            if (_beamLight != null) _beamLight.GlobalPosition = _beamSeg.End;
        }

        // plasma bits drip off along the beam and fall to the ground (NEW)
        _beamPlasmaT -= dt;
        if (_beamPlasmaT <= 0f && _beamT > 0f)
        {
            _beamPlasmaT = 0.05f;
            var arc = DamageTypes.Col(DamageType.Arcane);
            int drips = 1 + (GD.Randf() < 0.5f ? 1 : 0);
            for (int i = 0; i < drips; i++)
            {
                float ss = GD.Randf() * BeamLen;
                var p = eye + dir * ss + new Vector3((GD.Randf() - 0.5f) * 0.6f, -0.25f, (GD.Randf() - 0.5f) * 0.6f);
                var drop = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.12f, Height = 0.24f }, MaterialOverride = Game.Emissive(arc.Lerp(Colors.White, 0.3f), 3.5f) };
                Game.I.AddChild(drop);
                drop.GlobalPosition = p;
                float gy = Game.I.SurfaceHeight(p, p.Y);
                var land = new Vector3(p.X, gy + 0.05f, p.Z);
                var dt2 = drop.CreateTween();
                dt2.TweenProperty(drop, "global_position", land, 0.35f + GD.Randf() * 0.2f).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
                dt2.TweenProperty(drop, "scale", new Vector3(0.1f, 0.1f, 0.1f), 0.15f);
                dt2.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(drop)) drop.QueueFree(); }));
            }
        }

        // glowing arcane scorch marks where the beam lands — end point + under enemies it's burning (throttled) (NEW)
        _beamBurnT -= dt;
        if (_beamBurnT <= 0f && _beamT > 0f)
        {
            _beamBurnT = 0.12f;
            var arc = DamageTypes.Col(DamageType.Arcane);
            Game.I.SpawnBurnMark(eye + dir * BeamLen, arc, _beamWidth * 1.6f, 2.5f);
            int stamped = 0;
            foreach (var e in Game.I.Enemies.ToArray())
            {
                if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
                var rel = e.GlobalPosition - eye;
                float proj = rel.X * dir.X + rel.Z * dir.Z;
                if (proj < 0 || proj > BeamLen) continue;
                float px = eye.X + dir.X * proj, pz = eye.Z + dir.Z * proj;
                if (new Vector2(e.GlobalPosition.X - px, e.GlobalPosition.Z - pz).Length() < _beamWidth + e.Radius)
                {
                    Game.I.SpawnBurnMark(e.GlobalPosition, arc, _beamWidth * 1.2f, 2.5f);
                    if (++stamped >= 2) break;
                }
            }
        }
        if (_beamT <= 0 && _beamSeg != null) { _beamSeg.Free(); _beamSeg = null; _beamLight = null; }
    }

    private void FinVolley(float pow, int t, Color col)
    {
        int count = 5 + t; var dir = AimDir(); var eye = EyePos; var right = new Vector3(-dir.Z, 0, dir.X).Normalized();
        for (int i = 0; i < count; i++)
        {
            float off = (i - (count - 1) / 2f) * 0.1f;
            var d = (dir + right * off).Normalized() * 48f;
            SpawnBolt(eye + dir * 1.2f, d, Base() * 1.4f * pow, 0, 0.5f, col, DamageType.Arcane, false, false, false, false, fromCombo: true);
        }
        Ring(eye + dir * 1.2f, col, 2.2f, 0.3f);          // (NEW) arcane muzzle flash
        Game.I.Sfx?.ArcaneBlast(eye + dir * 1.5f);         // (NEW) thunderous arcane blast on fire
    }

    private void FinSwarm(float pow, int t, Color col)
    {
        int count = 7 + t;
        var aim = AimDir();

        var alive = new System.Collections.Generic.List<Enemy>();
        foreach (var e in Game.I.Enemies.ToArray())
            if (e != null && !e.Dead && GodotObject.IsInstanceValid(e)) alive.Add(e);

        for (int i = 0; i < count; i++)
        {
            float a = (i / (float)count) * Mathf.Pi * 2f + 0.4f;
            var horiz = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
            var start = GlobalPosition + new Vector3(0, 2.4f, 0) + horiz * 0.7f;
            var launch = (Vector3.Up * 2.3f + horiz * 1.3f).Normalized() * 17f;   // pop up into an arch
            var b = SpawnBolt(start, launch, Base() * 1.2f * pow, 0, 0.45f, col, DamageType.Arcane, false, false, false, false, life: 3.8f, fromCombo: true);
            b.Homing = true;
            b.HomeSpeed = 33f;
            b.Turn = 7.5f;
            b.HomeDelay = 0.22f + (i % 3) * 0.04f;                 // arch first, then seek
            b.Gravity = 26f;                                       // gives the arch its hang
            b.Target = alive.Count > 0 ? alive[i % alive.Count] : null;   // spread across distinct foes
            b.AimFallback = aim;                                   // no foes -> streak toward the cursor
        }
    }

    private void FinFull(float pow, Color col)
    {
        // dynamic: unleash a FULLY-charged right-click of whatever element is equipped in the secondary
        if (SecondaryType == DamageType.Holy) { FireHolyRay(1f); return; }
        var dir = AimDir(); var eye = EyePos;
        float dmg = Base() * S.MaxCharge * pow * ComboMul();
        SpawnBolt(eye + dir * 1.2f, dir * 34f, dmg, S.Pierce + 2, 1.4f, DamageTypes.Col(SecondaryType), SecondaryType,
            normal: false, charged: true, combo: false, full: true, fromCombo: true);
    }

    private void FinHeal(float pow, int t)
    {
        var f = new GroundField {
            Type = FieldType.Heal, HealAllies = true,
            Radius = 5f + t * 0.8f,
            Dur = 4f + t,
            Power = S.MaxHp * 0.028f * pow,
            EnemyDmg = Mathf.Min(5, t + 1),   // common 1 → legendary 5 dmg/sec
            FromCombo = true,
            Cap = Mathf.Clamp(t, 1, 4),       // 1 / 1 / 2 / 3 / 4 on screen by rarity
            DType = DamageType.Holy
        };
        Game.I.AddChild(f); f.GlobalPosition = new Vector3(GlobalPosition.X, 0.04f, GlobalPosition.Z);
        Game.I.RegisterComboField(f);
        Game.I.NetMgr?.BroadcastField((int)FieldType.Heal, new Vector3(GlobalPosition.X, 0.04f, GlobalPosition.Z), f.Radius, f.Dur, false, DamageTypes.Col(DamageType.Holy), (int)DamageType.Holy);   // (NEW) allies see the field
        Game.I.SpawnMeadow(GlobalPosition, f.Radius, DamageTypes.Col(DamageType.Holy), f.Dur);       // (NEW) holy meadow pops up in the AoE
        Game.I.SpawnPollen(GlobalPosition, f.Radius, DamageTypes.Col(DamageType.Holy), 14, f.Dur);   // (NEW) holy pollen drifting around
        Game.I.Sfx?.HolyRush(GlobalPosition);                                                        // (NEW) calm rushing holy sound
    }

    private void FinHexField(float pow, int t)
    {
        var flat = new Vector3(AimDir().X, 0, AimDir().Z).Normalized();
        var at = GlobalPosition + flat * 7f;
        var f = new GroundField { Type = FieldType.Hex, Radius = 5f + t * 0.6f, Dur = 4f + t * 0.5f, Power = Base() * 1.2f * pow, FromCombo = true, Cap = Mathf.Clamp(t, 1, 4), DType = DamageType.Curse };
        Game.I.AddChild(f); f.GlobalPosition = new Vector3(at.X, 0.04f, at.Z);
        Game.I.RegisterComboField(f);
        Game.I.NetMgr?.BroadcastField((int)FieldType.Hex, new Vector3(at.X, 0.04f, at.Z), f.Radius, f.Dur, false, DamageTypes.Col(DamageType.Curse), (int)DamageType.Curse);   // (NEW) allies see the field
        Game.I.SpawnGroundSigil(GlobalPosition, f.Radius * 1.2f, DamageTypes.Col(PrimaryType));   // (NEW) sigil flares around the caster in her element
        Game.I.Sfx?.WitchCackle(GlobalPosition);                                                  // (NEW) witch cackle on cast
    }

    private void FinRoot(float pow, int t, Color col)
    {
        float radius = 12f + t * 2f, dur = 2.4f + t * 0.5f, dmg = Base() * 1.0f * pow;
        foreach (var e in Game.I.Enemies.ToArray()) if (!e.Dead && Flat(e, GlobalPosition) < radius && !Game.I.SightBlocked(GlobalPosition, e.GlobalPosition)) { e.Root(dur); e.Hurt(dmg, DamageType.Nature, true); }
        Game.I.DamageWorld(GlobalPosition, radius, dmg);   // (FIX) AoE breaks props too
        Ring(GlobalPosition, col, radius, 0.6f);
    }

    // Creeping Blight: a poison field at your feet that keeps stacking additive poison on whoever stands in it.
    private void FinPoisonField(float pow, int t)
    {
        var f = new GroundField
        {
            Type = FieldType.Hex,
            TintColor = DamageTypes.Col(DamageType.Nature),
            Radius = 5.5f + t * 0.8f,
            Dur = 5f + t,
            Power = Base() * 0.12f * pow,               // small direct dmg/sec
            PoisonAdd = (1.5f + t * 1.2f) * pow,         // additive poison each 0.4s — ramps the longer they stand
            SlowMul = ModPoisonField ? 0.55f : 0f,       // legendary: also slows
            FromCombo = true,
            DType = DamageType.Nature,
            Src = this
        };
        Game.I.AddChild(f); f.GlobalPosition = new Vector3(GlobalPosition.X, 0.04f, GlobalPosition.Z);
        Game.I.RegisterComboField(f);
        Game.I.NetMgr?.BroadcastField((int)FieldType.Hex, new Vector3(GlobalPosition.X, 0.04f, GlobalPosition.Z), f.Radius, f.Dur, false, DamageTypes.Col(DamageType.Nature), (int)DamageType.Nature);   // (NEW) allies see the field
        Game.I.SpawnBlightFlower(GlobalPosition, DamageTypes.Col(DamageType.Nature), f.Dur);          // (NEW) grotesque flower at the center
        Game.I.SpawnPollen(GlobalPosition, f.Radius, DamageTypes.Col(DamageType.Nature), 16, f.Dur);  // (NEW) noxious pollen in the AoE
        Game.I.Sfx?.GasHiss(GlobalPosition);                                                          // (NEW) gas releasing hiss
    }

    // Seed Mines: scatter proximity mines that detonate when a foe steps near. Count + damage scale with rarity.
    private void FinSeedMine(float pow, int t)
    {
        int count = 4 + t * 2;                          // common 6 → legendary 14
        float dmg = Base() * (1.4f + 0.3f * t) * pow;
        float spread = 2f + 4.5f + t;
        for (int i = 0; i < count; i++)
        {
            float a = GD.Randf() * Mathf.Tau, r = 2f + GD.Randf() * spread;
            var pos = GlobalPosition + new Vector3(Mathf.Cos(a) * r, 0, Mathf.Sin(a) * r);
            var m = new SeedMine { Caster = this, Damage = dmg, Chain = ModSeedMine, Poison = Base() * 0.15f };
            Game.I.AddChild(m);
            float surf = Game.I.SurfaceHeight(pos, GlobalPosition.Y);        // (NEW) anchor to terrain so the husk sits ON the ground, not through it
            m.GlobalPosition = new Vector3(pos.X, surf + 0.12f, pos.Z);
            Game.I.SpawnBrambleBurst(m.GlobalPosition, 0.7f, 4);            // (NEW) bramble decals around each mine
            Game.I.NetMgr?.BroadcastVfx(34, m.GlobalPosition, Vector3.Zero, m.Trigger, m.Life, Colors.White);   // (NEW) allies see the mine
        }
        Ring(GlobalPosition, DamageTypes.Col(DamageType.Nature), 4f, 0.4f);
    }

    // Thorn Skin: bank a bark-shield charge (cap 3, +1 with the legendary). Each charge eats one full hit and
    // BURSTS for Nature damage around you when it pops (see Hurt). Mirrors the Crimson blood-shield pattern.
    private void FinThornSkin(float pow, int t)
    {
        AddArmor(true, Base() * (1.6f + 0.4f * t) * pow);   // a green (thorn) armor charge — bursts Nature on break
        Ring(GlobalPosition, DamageTypes.Col(DamageType.Nature), 2.8f, 0.4f);
    }

    private void AnimateHands(float dt)
    {
        if (_armL == null) return;
        _ht += dt;
        _kickL = Mathf.Max(0, _kickL - dt * 6f);
        _kickR = Mathf.Max(0, _kickR - dt * 6f);

        Vector3 lp = _baseLPos, rp = _baseRPos, lr = _baseLRot, rr = _baseRRot;
        float bob = Mathf.Sin(_ht * 2f) * 0.008f;
        lp.Y += bob; rp.Y += bob;
        lp.Z -= _kickL * 0.22f; lp.Y += _kickL * 0.05f; lr.X -= _kickL * 0.5f;
        rp.Z -= _kickR * 0.22f; rp.Y += _kickR * 0.05f; rr.X -= _kickR * 0.5f;

        if (Charging)
        {
            float cc = ChargeAmt;
            if (FrostWitch)   // (NEW) draw the ballista: right palm lifts UP + OUT to the side, pulled back; left steadies forward
            {
                rp.X = Mathf.Lerp(_baseRPos.X, 0.3f, cc); rp.Y += cc * 0.24f; rp.Z += cc * 0.14f;
                rr.X += cc * 0.5f; rr.Z -= cc * 0.8f;                                  // palm tilts up + turns to face away
                lp.X = Mathf.Lerp(_baseLPos.X, -0.1f, cc); lp.Z -= cc * 0.2f; lp.Y += cc * 0.03f;
            }
            else if (ForsakenWitch)   // (NEW) draw the doll hand in toward the chest as she squeezes
            {
                rp.X = Mathf.Lerp(_baseRPos.X, 0.12f, cc); rp.Z += cc * 0.14f; rp.Y += cc * 0.05f;
                rr.X += cc * 0.35f;
            }
            else
            {
                lp.X = Mathf.Lerp(_baseLPos.X, -0.16f, cc); rp.X = Mathf.Lerp(_baseRPos.X, 0.16f, cc);
                lp.Z -= cc * 0.12f; rp.Z -= cc * 0.12f; lp.Y += cc * 0.05f; rp.Y += cc * 0.05f;
            }
        }

        if (_animDur > 0)
        {
            _animT += dt; float k = Mathf.Clamp(_animT / _animDur, 0, 1); float e = Mathf.Sin(k * Mathf.Pi);
            switch (_animKind)
            {
                case "together": lp.X = Mathf.Lerp(_baseLPos.X, -0.08f, e); rp.X = Mathf.Lerp(_baseRPos.X, 0.08f, e); lp.Z -= 0.25f * e; rp.Z -= 0.25f * e; break;
                case "raise": lp.Y += 0.4f * e; lp.Z += 0.05f * e; lr.X -= 1.0f * e; break;
                case "thrust": rp.Z -= 0.5f * e; rr.X -= 0.7f * e; break;
                case "palmsup": float up = Mathf.Max(0, (k - 0.3f) / 0.7f); lp.Y += 0.34f * up; rp.Y += 0.34f * up; lp.Z -= 0.28f * e; rp.Z -= 0.28f * e; lr.X += 0.5f * up; rr.X += 0.5f * up; break;
                case "slam": lp.Y -= 0.18f * e; rp.Y -= 0.18f * e; lp.Z -= 0.28f * e; rp.Z -= 0.28f * e; lr.X += 0.5f * e; rr.X += 0.5f * e; break;
                case "barrage": { float f2 = Mathf.Abs(Mathf.Sin(k * Mathf.Pi * 3f)); rp.Z -= 0.5f * f2; lp.Z -= 0.5f * (1f - f2); rr.X -= 0.7f * f2; lr.X -= 0.7f * (1f - f2); break; }   // Gale primary: rapid alternating jabs (NEW)
                case "grdpunch":   // Gale charged: wind up, then drive a fist down into the ground (NEW)
                {
                    float wind = Mathf.Clamp(k / 0.35f, 0f, 1f);
                    float drive = Mathf.Clamp((k - 0.35f) / 0.65f, 0f, 1f);
                    float upw = wind * (1f - drive);
                    lp.Y += 0.45f * upw; rp.Y += 0.45f * upw; lr.X -= 0.9f * upw; rr.X -= 0.9f * upw;       // windup: fists up/back
                    lp.Y -= 0.34f * drive; rp.Y -= 0.34f * drive; lp.Z -= 0.35f * drive; rp.Z -= 0.35f * drive;  // drive down + forward
                    lr.X += 0.8f * drive; rr.X += 0.8f * drive;
                    break;
                }
                case "flare": rp.Z -= 0.55f * e; rp.Y += 0.12f * e; rr.X += 0.4f * e; rr.Z -= 0.35f * e; break;   // firework: right hand out front, palm up (NEW)
                case "crush": { float reach = Mathf.Clamp(k / 0.45f, 0f, 1f); float yank = Mathf.Clamp((k - 0.4f) / 0.6f, 0f, 1f); rp.Z -= 0.42f * reach; rp.Z += 0.55f * yank; rp.Y += 0.1f * reach - 0.12f * yank; rr.X += 0.7f * yank; break; }   // Forsaken: reach out, clasp, and YANK the curse in (NEW)
                case "draw":
                {
                    float reach = Mathf.Clamp(k / 0.4f, 0f, 1f);          // hands spread out wide & forward
                    float pull = Mathf.Clamp((k - 0.4f) / 0.6f, 0f, 1f);  // then drawn back to center
                    lp.X = Mathf.Lerp(_baseLPos.X, -0.6f, reach); lp.X = Mathf.Lerp(lp.X, -0.04f, pull);
                    rp.X = Mathf.Lerp(_baseRPos.X, 0.6f, reach);  rp.X = Mathf.Lerp(rp.X, 0.04f, pull);
                    lp.Z -= 0.22f * reach; lp.Z += 0.18f * pull;
                    rp.Z -= 0.22f * reach; rp.Z += 0.18f * pull;
                    lp.Y += 0.05f * e; rp.Y += 0.05f * e;
                    lr.Z += 0.6f * reach; rr.Z -= 0.6f * reach;          // palms turned outward to pull
                    break;
                }
            }
            if (k >= 1) _animDur = 0;
        }

        if (_beamT > 0) { lp.Z -= 0.32f; rp.Z -= 0.32f; lp.X *= 0.5f; rp.X *= 0.5f; lp.Y += 0.04f; rp.Y += 0.04f; }

        _armL.Position = lp; _armR.Position = rp; _armL.Rotation = lr; _armR.Rotation = rr;

        _chargeOrb.Visible = Charging && !VerdantWitch && !FrostWitch && !ForsakenWitch;
        if (FrostWitch && _armR != null)   // (NEW) nocked ice arrow: sits in front of the right palm, drawn forward + grows as she charges
        {
            if (_frostNock == null)
            {
                _frostNock = new Node3D(); _armR.AddChild(_frostNock);
                var ice = Game.Emissive(new Color(0.72f, 0.9f, 1f), 2.6f);
                var shaft = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.012f, BottomRadius = 0.014f, Height = 0.5f, RadialSegments = 6 }, MaterialOverride = ice };
                shaft.RotationDegrees = new Vector3(90, 0, 0); shaft.Position = new Vector3(0, 0, -0.25f); _frostNock.AddChild(shaft);
                var head = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.045f, Height = 0.14f, RadialSegments = 4 }, MaterialOverride = ice };
                head.RotationDegrees = new Vector3(90, 0, 0); head.Position = new Vector3(0, 0, -0.52f); _frostNock.AddChild(head);
            }
            _frostNock.Visible = Charging;
            if (Charging) { float cc = ChargeAmt; _frostNock.Position = new Vector3(0.05f, 0.02f, -0.1f - cc * 0.16f); _frostNock.Scale = Vector3.One * (0.55f + cc * 0.85f); }
        }
        if (ForsakenWitch && _armR != null)   // (NEW) voodoo doll: always held; clenched harder as she charges; glows over a cursed foe; pops on release
        {
            if (_voodoo == null) BuildVoodooDoll();
            _voodoo.Visible = true;
            _voodoo.Position = new Vector3(0.0f, 0.14f, -0.6f);   // sits in the fist (hand sphere is at Z -0.62): doll rises out of the grip instead of clipping the upper arm
            float clench = Charging ? ChargeAmt : 0f;                                   // squeeze harder the longer she charges
            float pop = (_animDur > 0 && _animKind == "crush") ? Mathf.Sin(Mathf.Clamp(_animT / _animDur, 0f, 1f) * Mathf.Pi) : 0f;   // full crush on release
            float sq = Mathf.Max(clench * 0.6f, pop);
            float s = 0.95f * (1f - sq * 0.4f);
            _voodoo.Scale = new Vector3(s * (1f - sq * 0.35f), s * (1f + sq * 0.4f), s * (1f - sq * 0.35f));   // squeezed narrow, stretched tall
            _voodoo.RotationDegrees = new Vector3(clench * 10f, 0, clench * 22f + pop * 45f);                  // wrenched as it's crushed
            var aim = CurseAimTarget();                                                 // glow when the reticle is over a cursed foe
            if (_voodooLight != null) _voodooLight.LightEnergy = Mathf.Lerp(_voodooLight.LightEnergy, (aim != null && aim.Cursed) ? 2.6f : 0.25f, dt * 10f);
        }
        if (_thornCharge != null)
        {
            bool show = Charging && VerdantWitch;
            _thornCharge.Visible = show;
            if (show) _thornCharge.Scale = new Vector3(0.4f + ChargeAmt * 0.35f, 0.4f + ChargeAmt * 0.35f, 0.5f + ChargeAmt * 0.85f);
        }
        if (Charging && !VerdantWitch)
        {
            _chargeOrb.Scale = Vector3.One * (0.12f + ChargeAmt * 0.5f);
            if (_chargeOrb.MaterialOverride is StandardMaterial3D m)
            {
                m.EmissionEnergyMultiplier = 2f + ChargeAmt * 5f;
                m.Emission = ChargeAmt >= 0.95f ? DamageTypes.Col(DamageType.Lunar) : Palette.Lunar;
            }
        }
    }

    // ---- ultimate ----
    public void TryUlt()
    {
        if (Ult == UltKind.None) return;
        if (Ult == UltKind.WildfireRush && UltActive && _flameDashCharges > 0) { FlameDash(); return; }   // (NEW) Q during the Wildfire window = a flame dash
        if (UltActive || UltCharge < 1f) return;
        ActivateUlt();
    }

    private void ActivateUlt()
    {
        UltCharge = 0f;
        Game.I.Sfx?.Release(DamageType.Lunar);
        switch (Ult)
        {
            case UltKind.Eclipse:
                UltActive = true; UltActiveT = 6f + UltTier * 1.6f; UltDmgMul = 2f; _eclipseMax = UltActiveT;
                {
                    var ev = new EclipseVfx { Dur = UltActiveT, MaxDur = UltActiveT };
                    Game.I.AddChild(ev);
                    ev.GlobalPosition = new Vector3(GlobalPosition.X, GlobalPosition.Y + 26f, GlobalPosition.Z);
                    Ring(GlobalPosition, DamageTypes.Col(DamageType.Lunar), 8f, 0.7f);
                    Ring(GlobalPosition, Colors.White, 4f, 0.5f);
                    CamKick(0.6f);
                }
                Game.I.NetMgr?.BroadcastVfx(4, GlobalPosition, Vector3.Zero, UltActiveT, 0f, DamageTypes.Col(DamageType.Lunar));
                Game.I.Hud?.Banner("LUNAR ECLIPSE");
                break;
            case UltKind.LunarLight: DeployLunarLight(); break;
            case UltKind.Crescent: SpawnCrescents(); break;
            case UltKind.FaithShield:
            {
                if (Game.I.Shield != null && GodotObject.IsInstanceValid(Game.I.Shield)) Game.I.Shield.QueueFree();
                int t = UltTier;
                var sh = new FaithShield
                {
                    MaxHp = 240f + t * 130f, Hp = 240f + t * 130f,
                    Radius = 10f + t * 1.2f, Dur = 8f + t * 2f,        // ~3.5x the old area, rooted where cast
                    MeleeDmg = 6f + t * 2f, Reflect = ModShield,
                    HealPerSec = S.MaxHp * (0.05f + t * 0.008f),       // medium heal to allies inside
                    BurstDmg = Base() * (4f + t * 1.5f), BurstRadius = 13f + t * 1.5f   // shatter blast on break/expire
                };
                if (ModShield) { sh.MaxHp *= 1.4f; sh.Hp = sh.MaxHp; }
                Game.I.AddChild(sh);
                sh.GlobalPosition = new Vector3(GlobalPosition.X, 0.1f, GlobalPosition.Z);
                Game.I.Shield = sh;
                Game.I.NetMgr?.BroadcastVfx(5, new Vector3(GlobalPosition.X, 0.1f, GlobalPosition.Z), Vector3.Zero, sh.Radius, sh.Dur, DamageTypes.Col(DamageType.Holy));
                UltActive = true; UltActiveT = sh.Dur;
                Game.I.Hud?.Banner("FAITH SHIELD");
                break;
            }
            case UltKind.Judgement:
            {
                int t = UltTier;
                if (ModJudge)
                {
                    // ONE colossal lance: devastating at the core, tapering to "okay" at the rim, then a pulsing field.
                    var at = GroundAim();
                    float rad = 9f + t * 1.2f;
                    float core = Base() * (7f + t * 2.5f);
                    foreach (var e in Game.I.Enemies.ToArray())
                    {
                        if (e.Dead) continue;
                        float dd = Flat(e, at);
                        if (dd < rad)
                        {
                            float falloff = Mathf.Lerp(1f, 0.3f, Mathf.Clamp(dd / rad, 0f, 1f));   // insane center -> okay edge
                            e.Hurt(core * falloff, DamageType.Holy, true); ComboFromSource();
                        }
                    }
                    Game.I.DamageWorld(at, rad, core);   // (FIX) AoE breaks props too
                    Lance(at, 5f, 3.2f);                 // a giant construct lance, planted for the duration
                    Ring(at, DamageTypes.Col(DamageType.Holy), rad, 0.8f);
                    Ring(at, Colors.White, rad * 0.5f, 0.6f);
                    var v = new Vfx(); Game.I.AddChild(v); v.GlobalPosition = new Vector3(at.X, 0.5f, at.Z);
                    v.Init(new SphereMesh { Radius = rad * 0.6f, Height = rad * 1.2f }, DamageTypes.Col(DamageType.Holy), 0.5f, 8f);
                    var pulse = new HolyPulse
                    {
                        Radius = rad, Dur = 5f, MaxDur = 5f,
                        PulseDmg = Base() * (0.5f + t * 0.12f),   // low-med pulse damage (heals allies by the same)
                        PulseHeal = S.MaxHp * 0.05f, Interval = 0.8f
                    };
                    Game.I.AddChild(pulse); pulse.GlobalPosition = new Vector3(at.X, 0.06f, at.Z);
                    Game.I.NetMgr?.BroadcastField((int)FieldType.Heal, new Vector3(at.X, 0.04f, at.Z), rad, 5f, false, DamageTypes.Col(DamageType.Holy), (int)DamageType.Holy);
                    CamKick(1.0f);
                    Game.I.Sfx?.Release(DamageType.Holy);
                    Game.I.Hud?.Banner("JUDGEMENT");
                }
                else
                {
                    var all = Game.I.Enemies.FindAll(e => e != null && !e.Dead && GodotObject.IsInstanceValid(e));
                    all.Sort((a, b) => Flat(a, GlobalPosition).CompareTo(Flat(b, GlobalPosition)));
                    int count = Mathf.Max(1, Mathf.CeilToInt(all.Count * 0.25f));
                    float dmg = Base() * (3.0f + t * 1.0f);
                    float aoe = 3.2f + t * 0.3f;
                    for (int i = 0; i < count && i < all.Count; i++)
                    {
                        var at = all[i].GlobalPosition;
                        foreach (var e in Game.I.Enemies.ToArray())   // small splash at each impact
                            if (!e.Dead && Flat(e, at) < aoe && !Game.I.SightBlocked(at, e.GlobalPosition)) { e.Hurt(dmg, DamageType.Holy, true); ComboFromSource(); }
                        Game.I.DamageWorld(at, aoe, dmg);   // (FIX) AoE breaks props too
                        Lance(at, 4.5f);                  // lances stay planted for the effect
                        var f = new GroundField { Type = FieldType.Heal, HealAllies = true, EnemyDmg = Base() * 0.5f, Radius = aoe, Dur = 4.5f, Power = S.MaxHp * (0.02f + t * 0.004f), DType = DamageType.Holy, TintColor = DamageTypes.Col(DamageType.Holy), FromCombo = true };
                        Game.I.AddChild(f); f.GlobalPosition = new Vector3(at.X, 0.04f, at.Z);
                    }
                    Game.I.Hud?.Banner("JUDGEMENT");
                }
                break;
            }
            case UltKind.Divinity:
            {
                Divinity = true;
                _divBaseY = GlobalPosition.Y;
                _divT = 6f + UltTier * 1.5f + (ModDivinity ? 3f : 0f);
                _iframe = 999f;
                UltActive = true;
                Game.I.NetMgr?.BroadcastVfx(6, GlobalPosition, Vector3.Zero, 4f, 0f, DamageTypes.Col(DamageType.Holy));
                Game.I.Hud?.Banner("DIVINITY");
                break;
            }
            case UltKind.BloodTsunami:
            {
                int t = UltTier;
                Vector3 fwd = -_cam.GlobalTransform.Basis.Z; fwd.Y = 0; fwd = fwd.Normalized();
                var wave = new BloodWave
                {
                    Dir = fwd,
                    Dmg = Base() * (3.8f + t * 0.9f) * (ModTsunami ? 1.35f : 1f),
                    Knock = 6f + t * 0.6f,
                    Width = 14f + t * 2.5f + (ModTsunami ? 5f : 0f),
                    Speed = 24f,
                    Range = 56f + t * 7f,
                    SlowDur = 3f
                };
                Game.I.AddChild(wave);
                wave.GlobalPosition = new Vector3(GlobalPosition.X, 0.5f, GlobalPosition.Z) + fwd * 2f;
                CamKick(0.8f);
                Game.I.Sfx?.Release(DamageType.Blood);
                Game.I.Hud?.Banner("BLOOD TSUNAMI");
                break;
            }
            case UltKind.Exsanguinate:
            {
                int t = UltTier;
                float rad = (12f + t * 2f) * S.SpellArea;
                float pct = 0.12f + t * 0.035f;                 // % of max HP as damage
                float exec = 0.18f + t * 0.035f + (ModExsang ? 0.12f : 0f);   // execute under this fraction of max HP
                bool killed = false;
                SetArm("draw", 0.75f);                          // hands reach out and draw the blood inward
                var tcol = DamageTypes.Col(DamageType.Blood);
                foreach (var e in Game.I.Enemies.ToArray())
                {
                    if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
                    if (Flat(e, GlobalPosition) > rad + e.Radius) continue;
                    if (!e.IsBoss && e.Hp <= e.MaxHp * exec) { e.Hurt(e.Hp + 9999f, DamageType.Blood, true); killed = true; }
                    else { e.Hurt(e.MaxHp * pct, DamageType.Blood, true); if (e.Dead) killed = true; }
                    e.Knockback(GlobalPosition, -2f);            // pull inward (negative = toward center)
                    BloodReward(1f);
                    Vector3 td = GlobalPosition - e.GlobalPosition; td.Y = 0f; float dist = td.Length();
                    if (dist > 0.5f)
                    {
                        var ndir = td.Normalized();
                        Game.I.VfxBloodTether(e.GlobalPosition, ndir, dist, tcol);          // a strand of blood drawn to her
                        Game.I.NetMgr?.BroadcastVfx(9, e.GlobalPosition, ndir, dist, 0f, tcol);   // allies see the tethers too
                    }
                }
                Game.I.DamageWorld(GlobalPosition, rad, Base());   // (FIX) the execute nova breaks props too
                if (killed) Heal(S.MaxHp);                       // a kill drains her back to full
                {
                    var bcol = DamageTypes.Col(DamageType.Blood);
                    Ring(GlobalPosition, bcol, rad, 0.7f);
                    Ring(GlobalPosition, bcol.Lerp(Colors.White, 0.35f), rad * 0.55f, 0.5f);
                    var v = new Vfx(); Game.I.AddChild(v); v.GlobalPosition = GlobalPosition + new Vector3(0, 1f, 0);
                    v.Init(new SphereMesh { Radius = rad * 0.5f, Height = rad }, bcol, 0.5f, 7f);
                    // a column of drawn blood erupting up from her
                    var col3 = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.3f, BottomRadius = 1.6f, Height = 9f }, MaterialOverride = Game.ToonEmissive(bcol, 2.2f, 0.03f) };
                    Game.I.AddChild(col3); col3.GlobalPosition = GlobalPosition + new Vector3(0, 4.5f, 0);
                    col3.Scale = new Vector3(0.2f, 1f, 0.2f);
                    var ct = col3.CreateTween();
                    ct.SetParallel(true);
                    ct.TweenProperty(col3, "scale", new Vector3(1.3f, 1f, 1.3f), 0.18f);
                    ct.TweenProperty(col3, "transparency", 1f, 0.5f);
                    ct.SetParallel(false);
                    ct.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(col3)) col3.QueueFree(); }));
                    Game.I.NetMgr?.BroadcastVfx(30, GlobalPosition, Vector3.Zero, 0f, 0f, bcol);   // (NEW) allies see the blood column
                }
                CamKick(0.9f);
                Game.I.NetMgr?.BroadcastVfx(6, GlobalPosition, Vector3.Zero, 12f, 0f, DamageTypes.Col(DamageType.Blood));
                Game.I.Sfx?.Release(DamageType.Blood);
                Game.I.Hud?.Banner("EXSANGUINATE");
                break;
            }
            case UltKind.BloodRot:
            {
                int t = UltTier;
                float rad = (11f + t * 1.5f + (ModRot ? 4f : 0f)) * S.SpellArea;
                float dps = Base() * (0.7f + t * 0.15f) * (ModRot ? 1.2f : 1f);
                foreach (var e in Game.I.Enemies.ToArray())
                {
                    if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
                    if (Flat(e, GlobalPosition) > rad + e.Radius) continue;
                    e.Bleed(dps, 6f + t, true);                  // rot: spreads when the victim dies
                }
                var f = new GroundField { Type = FieldType.Hex, Radius = rad, Dur = 6f + t, Power = dps * 0.4f, FromCombo = true, DType = DamageType.Blood, TintColor = DamageTypes.Col(DamageType.Blood), RotDps = dps };
                Game.I.AddChild(f); f.GlobalPosition = new Vector3(GlobalPosition.X, 0.04f, GlobalPosition.Z);
                Game.I.NetMgr?.BroadcastField((int)FieldType.Hex, new Vector3(GlobalPosition.X, 0.04f, GlobalPosition.Z), rad, 6f + t, false, DamageTypes.Col(DamageType.Blood), (int)DamageType.Blood);   // (NEW) allies see the rot field
                {
                    var rcol = DamageTypes.Col(DamageType.Blood);
                    Ring(GlobalPosition, rcol, rad, 0.7f);
                    // bubbling rot orbs welling up across the field
                    for (int b = 0; b < 7; b++)
                    {
                        float a = GD.Randf() * Mathf.Tau, rr = GD.Randf() * rad;
                        var bub = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.5f + GD.Randf() * 0.7f, Height = 1f }, MaterialOverride = Game.ToonEmissive(rcol, 1.8f, 0.04f) };
                        Game.I.AddChild(bub);
                        bub.GlobalPosition = GlobalPosition + new Vector3(Mathf.Cos(a) * rr, 0.2f, Mathf.Sin(a) * rr);
                        var bt = bub.CreateTween();
                        bt.SetParallel(true);
                        bt.TweenProperty(bub, "position", bub.GlobalPosition + new Vector3(0, 1.4f, 0), 0.6f).SetDelay(GD.Randf() * 0.4f);
                        bt.TweenProperty(bub, "transparency", 1f, 0.7f).SetDelay(GD.Randf() * 0.4f);
                        bt.SetParallel(false);
                        bt.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(bub)) bub.QueueFree(); }));
                    }
                }
                Game.I.NetMgr?.BroadcastVfx(31, GlobalPosition, Vector3.Zero, rad, 0f, DamageTypes.Col(DamageType.Blood));   // (NEW) allies see the rot-bubbles
                CamKick(0.6f);
                Game.I.Sfx?.Release(DamageType.Blood);
                Game.I.Hud?.Banner("BLOOD ROT");
                break;
            }
            case UltKind.GroveGuardian:
            {
                int t = UltTier;
                var at = GroundAim();
                var g = new Guardian
                {
                    Caster = this,
                    Slams = 4 + t + (ModGuardian ? 2 : 0),
                    SlamRadius = (7f + t * 0.8f + (ModGuardian ? 2f : 0f)) * S.SpellArea,
                    SlamDamage = Base() * (2.6f + t * 0.6f),     // center value; tapers to ~40% at the edge
                    Poison = ModGuardian ? Base() * 0.2f : 0f,
                    RootOnSlam = ModGuardian
                };
                Game.I.AddChild(g); g.GlobalPosition = new Vector3(at.X, 0f, at.Z);
                ActiveGuardian = g;
                UltActive = true; UltActiveT = g.Slams * 0.85f + 1.2f;
                CamKick(0.7f);
                Game.I.Sfx?.Release(DamageType.Nature);
                Game.I.Hud?.Banner("ANCIENT GUARDIAN");
                break;
            }
            case UltKind.WildSwarm:
            {
                int t = UltTier;
                float sdur = LaunchStampede(t);
                UltActive = true; UltActiveT = sdur + 0.4f;   // active flag spans the stampede
                CamKick(0.6f);
                Game.I.Hud?.Banner("WILD SWARM — STAMPEDE!");
                break;
            }
            case UltKind.Barkskin:
            {
                int t = UltTier;
                float dur = 7f + t * 1.0f + (ModBark ? 2.5f : 0f);
                GrantBark(dur);                                       // self + your own ents
                Game.I.NetMgr?.BroadcastBarkskin(dur);               // allies bark over too (each bursts on their own machine)
                Game.I.NetMgr?.HealAlliesNear(GlobalPosition, 18f, S.MaxHp * 0.15f);
                Game.I.NetMgr?.BroadcastVfx(6, GlobalPosition, Vector3.Zero, 6f, 0f, DamageTypes.Col(DamageType.Nature));
                UltActive = true; UltActiveT = dur;
                CamKick(0.4f);
                Game.I.Sfx?.Creak();                                 // the great tree groans as bark sheaths everyone
                Game.I.Hud?.Banner("BARKSKIN");
                break;
            }

            // ---- Gale witch ults (NEW) ----
            case UltKind.Hurricane:
            {
                // She LEAPS aloft and a steerable hurricane forms beneath her. While it's up she hovers and
                // drifts (normal air steering) and the funnel tracks under her, grinding + flinging enemies.
                // The fling/fall damage runs through Enemy.Fling (host-authoritative; arcs sync to clients).
                // When the duration ends she simply falls (handled in UpdateUlt / normal gravity). The funnel
                // and per-frame fling live in UpdateHurricane. Legendary (ModHurricane) speeds the caster up. (NEW)
                int t = UltTier;
                _hurriBaseY = Game.I.SurfaceHeight(GlobalPosition, GlobalPosition.Y - 0.1f);
                UltActive = true; UltActiveT = 6f + t * 1.0f + (ModHurricane ? 2f : 0f);
                _grounded = false; _vy = 6f;            // the leap kick-off; UpdateHurricane lifts her the rest of the way
                _hurriFlingCd = 0f;
                var col = DamageTypes.Col(DamageType.Wind);
                // a tracking funnel for the caster, and a (static) tell broadcast to allies — the flung enemies
                // themselves are Y-synced now, so allies still see the chaos even if the funnel doesn't track.
                float radius = (10f + t * 1.5f) * S.SpellArea;
                var cy = new Cyclone(); Game.I.AddChild(cy);
                cy.Init(this, new Vector3(GlobalPosition.X, _hurriBaseY, GlobalPosition.Z), radius, UltActiveT + 0.5f, 0f, true, true);  // visualOnly funnel we reposition each frame
                _hurriVfx = cy;
                Game.I.NetMgr?.BroadcastVfx(46, new Vector3(GlobalPosition.X, _hurriBaseY, GlobalPosition.Z), Vector3.Up, radius, UltActiveT, col);   // trackable funnel for allies
                CamKick(0.7f);
                Game.I.Sfx?.Release(DamageType.Wind);
                Game.I.Hud?.Banner("HURRICANE");
                break;
            }
            case UltKind.Cyclone:
            {
                // drop a persistent tornado at the aim point that drags in and grinds enemies, then bursts.
                // Maelstrom mod (ModCyclone) makes it bigger, longer, and pull harder.
                int t = UltTier;
                Vector3 pos = GroundAim();
                float radius = 11f + t * 1.4f + (ModCyclone ? 4f : 0f);   // way bigger
                float dur = 5.5f + t * 0.7f + (ModCyclone ? 2f : 0f);
                float dps = Base() * (1.8f + t * 0.4f);                    // roughly doubled — grinds bosses/single targets too
                var cy = new Cyclone();
                Game.I.AddChild(cy);
                cy.Init(this, pos, radius, dur, dps, ModCyclone, false);
                Game.I.NetMgr?.BroadcastVfx(11, pos, Vector3.Up, radius, dur, DamageTypes.Col(DamageType.Wind));  // allies see a visual-only twister
                UltActive = true; UltActiveT = 1.0f;   // brief flag; the Cyclone node self-manages its lifetime
                CamKick(0.5f);
                Game.I.Sfx?.Release(DamageType.Wind);
                Game.I.Hud?.Banner("CYCLONE");
                break;
            }
            case UltKind.Stormform:
            {
                // self-buff: a windborne frenzy — big move speed + much faster casts for the duration.
                // Eye of the Storm mod (ModStorm) makes it last longer.
                int t = UltTier;
                UltActive = true; UltActiveT = 7f + t * 1.2f; _stormMax = UltActiveT;   // ModStorm legendary is air-mines now, not duration (NEW)
                _mineDropT = 0.8f;
                Ring(GlobalPosition, DamageTypes.Col(DamageType.Wind), 6f, 0.6f);
                Ring(GlobalPosition, DamageTypes.Col(DamageType.Wind).Lerp(Colors.White, 0.4f), 3.5f, 0.45f);
                CamKick(0.5f);
                Game.I.Sfx?.Release(DamageType.Wind);
                Game.I.Hud?.Banner("STORMFORM");
                break;
            }
            // ---- Frost witch ults (NEW) ----
            case UltKind.Blizzard:
            {
                int t = UltTier;
                Vector3 pos = GroundAim();
                float radius = (12f + t * 2f) * (ModBlizzard ? 1.4f : 1f) * S.SpellArea;
                float dur = 6f + t * 0.8f;
                float dps = Base() * (0.9f + t * 0.25f) * (ModBlizzard ? 1.35f : 1f);
                float freezeChance = ModBlizzard ? 1f : Mathf.Min(0.5f, 0.10f + t * 0.10f);   // Whiteout: icicles always freeze
                var bz = new Blizzard(); Game.I.AddChild(bz); bz.Init(this, pos, radius, dur, dps, freezeChance, false);
                Game.I.NetMgr?.BroadcastVfx(51, pos, Vector3.Up, radius, dur, DamageTypes.Col(DamageType.Frost));
                UltActive = true; UltActiveT = 1f;
                CamKick(0.6f); Game.I.Sfx?.Release(DamageType.Frost); Game.I.Hud?.Banner("BLIZZARD");
                break;
            }
            case UltKind.FrostElemental:
            {
                int t = UltTier;
                float dur = 8f + t * 1.5f;
                float size = (2.6f + t * 0.4f) * (ModFrostElem ? 1.4f : 1f) * S.SpellArea;
                float dmg = Base() * (1.1f + t * 0.3f);
                var fe = new FrostElemental(); Game.I.AddChild(fe); fe.Init(this, GlobalPosition, size, dur, dmg, false, ModFrostElem);   // Avalanche: splits on melt
                Game.I.NetMgr?.BroadcastVfx(53, GlobalPosition, Vector3.Zero, size, dur, DamageTypes.Col(DamageType.Frost));
                UltActive = true; UltActiveT = 1f;
                CamKick(0.7f); Game.I.Sfx?.Release(DamageType.Frost); Game.I.Hud?.Banner("FROST ELEMENTAL");
                break;
            }
            case UltKind.DeepFreeze:
            {
                int t = UltTier;
                Vector3 pos = GroundAim();
                float radius = (10f + t * 1.5f) * S.SpellArea;
                float dur = (3f + t * 0.6f) * (ModDeepFreeze ? 1.6f : 1f);
                var df = new DeepFreeze(); Game.I.AddChild(df); df.Init(this, pos, radius, dur, false, ModDeepFreeze);   // Absolute Zero: longer + shatters foes inside on end
                Game.I.NetMgr?.BroadcastVfx(52, pos, Vector3.Up, radius, dur, DamageTypes.Col(DamageType.Frost));
                UltActive = true; UltActiveT = 1f;
                CamKick(0.5f); Game.I.Sfx?.Freeze(pos); Game.I.Hud?.Banner("DEEP FREEZE");
                break;
            }
            // ---- Forsaken witch ults (NEW) ----
            case UltKind.HexCircle:
            {
                int t = UltTier;
                float radius = (12f + t * 0.8f) * (ModPlague ? 1.4f : 1f) * S.SpellArea;
                _hexGroup = ++_curseGroupSeq;                 // one shared mega-group so damage cascades across everyone inside
                UltActive = true; UltActiveT = 10f + t * 1.5f;
                _hexTickT = 0f; _hexNetT = 0f;
                if (_hexVfx != null && GodotObject.IsInstanceValid(_hexVfx)) _hexVfx.QueueFree();
                _hexVfx = BuildHexField(radius); Game.I.AddChild(_hexVfx);
                _hexVfx.GlobalPosition = new Vector3(GlobalPosition.X, 0.05f, GlobalPosition.Z);
                Ring(GlobalPosition, DamageTypes.Col(DamageType.Curse), radius, 0.7f);
                Game.I.NetMgr?.BroadcastVfx(59, GlobalPosition, Vector3.Zero, radius, UltActiveT, DamageTypes.Col(DamageType.Curse));
                CamKick(0.5f); Game.I.Sfx?.CurseCrush(GlobalPosition); Game.I.Hud?.Banner("HEX CIRCLE");
                break;
            }
            case UltKind.LifeDrain:
            {
                int t = UltTier;
                float radius = (11f + t * 1.5f) * S.SpellArea;
                UltActive = true; UltActiveT = 7f + t * 1.0f;   // a proper flight-channel length, in line with Hurricane/Stormform
                _drainBank = 0f; _drainBaseY = GlobalPosition.Y; _drainTickT = 0f; _drainNetT = 0f;
                _grounded = false; _vy = 0f; _noFall = Mathf.Max(_noFall, 1f);   // she takes to the air for the channel
                if (_drainVfx != null && GodotObject.IsInstanceValid(_drainVfx)) _drainVfx.QueueFree();
                _drainVfx = BuildDrainAura(radius); AddChild(_drainVfx);   // parented to her → the aura rides along as she flies
                Ring(GlobalPosition, DamageTypes.Col(DamageType.Curse), radius, 0.6f);
                Game.I.NetMgr?.BroadcastVfx(60, GlobalPosition, Vector3.Zero, radius, UltActiveT, DamageTypes.Col(DamageType.Curse));
                CamKick(0.6f); Game.I.Sfx?.CurseCrush(GlobalPosition); Game.I.Hud?.Banner("LIFE DRAIN — Space/Ctrl to fly, drain then release");
                break;
            }
            case UltKind.LifeCurse:
                FireLifeCurse(UltTier);   // instant missing-HP rune nuke (no channel)
                break;
            case UltKind.MeteorDescent:
            {
                UltActive = true;
                _meteorAscend = true; _meteorAscendT = 5f; _meteorBaseY = GlobalPosition.Y;
                _grounded = false; _vy = 0f; _noFall = 999f; _iframe = 999f;   // rise, invulnerable, no fall damage until the slam
                EndFlameCone();
                Ring(GlobalPosition, DamageTypes.Col(DamageType.Ember), 6f, 0.6f);
                Game.I.NetMgr?.BroadcastVfx(68, GlobalPosition, Vector3.Up, 0f, 5f, DamageTypes.Col(DamageType.Ember));   // allies see her launch skyward
                CamKick(0.5f); Game.I.Sfx?.ChargeUp(DamageType.Ember); Game.I.Hud?.Banner("METEOR DESCENT — aim, then drop");
                break;
            }
            case UltKind.WildfireRush:
            {
                int t = UltTier;
                UltActive = true; UltActiveT = 10f;
                _flameDashCharges = 3 + (t + 1) / 2;             // 3 → 5 dashes across tiers
                _flameDashWindowT = 10f;
                BurnLifestealT = 16f;                            // her burn ticks heal her while the trails burn
                Ring(GlobalPosition, DamageTypes.Col(DamageType.Ember), 5f, 0.5f);
                Game.I.NetMgr?.BroadcastVfx(69, GlobalPosition, Vector3.Zero, 5f, 0f, DamageTypes.Col(DamageType.Ember));
                CamKick(0.4f); Game.I.Sfx?.Release(DamageType.Ember); Game.I.Hud?.Banner($"WILDFIRE RUSH — {_flameDashCharges} flame dashes [Q]");
                break;
            }
            case UltKind.PhoenixAscend:
            {
                int t = UltTier;
                UltActive = true; UltActiveT = 10f + t * 1.5f;
                _phoenix = true; _phoenixRebirth = true; _phoenixAuraT = 0f;
                _grounded = false; _vy = 2f; _noFall = 999f;
                if (_phoenixVfx != null && GodotObject.IsInstanceValid(_phoenixVfx)) _phoenixVfx.QueueFree();
                _phoenixVfx = BuildPhoenixAura(); AddChild(_phoenixVfx);
                Ring(GlobalPosition, DamageTypes.Col(DamageType.Ember), 7f, 0.7f);
                Game.I.NetMgr?.BroadcastVfx(70, GlobalPosition, Vector3.Zero, UltActiveT, 0f, DamageTypes.Col(DamageType.Ember));
                CamKick(0.6f); Game.I.Sfx?.ModEmber(GlobalPosition); Game.I.Hud?.Banner("PHOENIX ASCENDANT");
                break;
            }
        }
    }

    // ===== FORSAKEN ULT HELPERS (NEW) =====
    private Node3D BuildHexField(float radius)   // a curse-stain circle PROJECTED down onto the terrain — a Decal, so it conforms to hills instead of clipping like a flat disc
    {
        var col = DamageTypes.Col(DamageType.Curse);
        var root = new Node3D();
        var decal = new Decal
        {
            TextureAlbedo = Game.FieldTex(),
            TextureEmission = Game.FieldTex(),
            EmissionEnergy = 2.2f,
            Modulate = new Color(col.R, col.G, col.B, 0.85f),
            Size = new Vector3(radius * 2f, Mathf.Max(10f, radius * 1.6f), radius * 2f)   // Y = projection depth: spans hilly ground
        };
        root.AddChild(decal);
        root.AddChild(new OmniLight3D { OmniRange = radius, LightColor = col, LightEnergy = 1.6f, Position = new Vector3(0, 1.4f, 0) });
        return root;
    }
    private Node3D BuildDrainAura(float radius)   // a faint curse dome around her while she drains (parented to her)
    {
        var col = DamageTypes.Col(DamageType.Curse);
        var root = new Node3D();
        var dome = new MeshInstance3D { Mesh = new SphereMesh { Radius = radius, Height = radius * 2f } };
        var dm = Game.ToonEmissive(col, 1.1f, 0f); dm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; dm.AlbedoColor = new Color(col.R, col.G, col.B, 0.11f);
        dome.MaterialOverride = dm; root.AddChild(dome);
        root.AddChild(new OmniLight3D { OmniRange = radius * 1.2f, LightColor = col, LightEnergy = 2.2f });
        return root;
    }
    private void ClearDrainLinks() { foreach (var l in _drainLinks) if (l != null && GodotObject.IsInstanceValid(l)) l.QueueFree(); _drainLinks.Clear(); }

    // Hex Circle tick: keep the field beneath her, and every 0.25s curse everyone inside into the one mega-group (+stacks).
    private void UpdateHexCircle(float dt)
    {
        UltActiveT -= dt;
        int t = UltTier;
        float radius = (12f + t * 0.8f) * (ModPlague ? 1.4f : 1f) * S.SpellArea;
        if (_hexVfx != null && GodotObject.IsInstanceValid(_hexVfx))
            _hexVfx.GlobalPosition = new Vector3(GlobalPosition.X, Game.I.SurfaceHeight(GlobalPosition, GlobalPosition.Y) + 0.05f, GlobalPosition.Z);   // ride the ground; the decal projects down onto hills
        _hexTickT -= dt;
        if (_hexTickT <= 0f)
        {
            _hexTickT = 0.25f;
            foreach (var e in Game.I.Enemies.ToArray())
            {
                if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
                if (Flat(e, GlobalPosition) > radius + e.Radius) continue;
                e.AddCurse(0.5f, _hexGroup, CurseBonusType, CurseBonusMul, CurseShareFrac, CurseBonusType2);   // ~2 stacks/s and fold into the mega-group so shared damage cascades
                if (ModPlague) e.Hurt(Base() * 0.3f, DamageType.Curse, true);                 // Plaguebearer: the ring also festers
            }
        }
        _hexNetT -= dt;
        if (_hexNetT <= 0f) { _hexNetT = 0.5f; Game.I.NetMgr?.BroadcastVfx(59, GlobalPosition, Vector3.Zero, radius, 0.6f, DamageTypes.Col(DamageType.Curse)); }
        if (UltActiveT <= 0f)
        {
            UltActive = false;
            if (_hexVfx != null && GodotObject.IsInstanceValid(_hexVfx)) { _hexVfx.QueueFree(); _hexVfx = null; }
        }
    }

    // Life Drain: free flight + drain-link everything in range, banking the stolen life; on end, detonate for the bank.
    private void UpdateLifeDrain(float dt)
    {
        UltActiveT -= dt;
        int t = UltTier;
        float radius = (11f + t * 1.5f) * S.SpellArea;
        // ---- free flight (Rammatra): rise to a hover, Space climbs, Ctrl descends, movement keys drift ----
        float minY = Game.I.SurfaceHeight(GlobalPosition, GlobalPosition.Y) + 4f;
        float maxY = _drainBaseY + 22f;
        float lift = (Input.IsActionPressed("jump") ? 1f : 0f) - (Input.IsActionPressed("descend") ? 1f : 0f);
        float wantY = Mathf.Clamp((lift != 0f ? GlobalPosition.Y + lift * 11f * dt : GlobalPosition.Y), minY, maxY);
        wantY = Mathf.Max(wantY, minY);
        float ny = Mathf.MoveToward(GlobalPosition.Y, wantY, 16f * dt);
        Vector3 hdir = InputDir();
        Vector3 np = (hdir != Vector3.Zero) ? ClampPos(GlobalPosition + hdir * (S.Speed * 0.85f) * dt) : GlobalPosition;
        GlobalPosition = new Vector3(np.X, ny, np.Z);
        _grounded = false; _vy = 0f; _noFall = Mathf.Max(_noFall, 0.4f);
        _bodyModel?.ShowWings(true);
        // ---- drain everything in range; refresh the tether links every frame ----
        _drainTickT -= dt; bool tick = _drainTickT <= 0f; if (tick) _drainTickT = 0.1f;
        ClearDrainLinks();
        var col = DamageTypes.Col(DamageType.Curse);
        float bankCap = Base() * (6f + t * 1.5f);
        foreach (var e in Game.I.Enemies.ToArray())
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
            if (Flat(e, GlobalPosition) > radius + e.Radius) continue;
            if (tick)
            {
                float drain = Base() * 0.7f * 0.1f * ComboMul();   // damage for this 0.1s tick
                e.Hurt(drain, DamageType.Curse, true);
                float heal = drain * 0.5f;                          // lifesteal: heal half of what she drains…
                Heal(heal);
                _drainBank = Mathf.Min(_drainBank + heal, bankCap);  // …and bank it as the release payload (capped)
            }
            _drainLinks.Add(Game.I.SpawnCurseLink(GlobalPosition, e.GlobalPosition + Vector3.Up * e.Radius * 0.5f, col));
        }
        if (ModRapture && tick) Game.I.NetMgr?.StormForce(GlobalPosition, radius, 0, 8f);   // Rapture: drag the crowd in toward her
        _drainNetT -= dt;
        if (_drainNetT <= 0f) { _drainNetT = 0.2f; Game.I.NetMgr?.BroadcastVfx(60, GlobalPosition, Vector3.Zero, radius, 0.3f, col); }
        if (UltActiveT <= 0f) { UltActive = false; EndLifeDrainBurst(); }
    }

    private void EndLifeDrainBurst()   // the Wanda release: cross arms + detonate for the banked lifesteal
    {
        var col = DamageTypes.Col(DamageType.Curse);
        float radius = (11f + UltTier * 1.5f) * S.SpellArea;
        float burst = _drainBank;
        SetArm("crush", 0.5f);
        foreach (var e in Game.I.Enemies.ToArray())
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
            if (Flat(e, GlobalPosition) > radius + e.Radius) continue;
            e.Hurt(burst, DamageType.Curse, true);
        }
        Game.I.DamageWorld(GlobalPosition, radius, burst);
        Game.I.SpawnGroundSigil(GlobalPosition, radius, col);
        Ring(GlobalPosition, col, radius, 0.8f);
        Ring(GlobalPosition, col.Lerp(Colors.White, 0.5f), radius * 0.6f, 0.5f);
        CurseImplosion(GlobalPosition + Vector3.Up * 1.2f, col, 1.8f);
        Game.I.NetMgr?.BroadcastVfx(61, GlobalPosition, Vector3.Zero, radius, 0f, col);
        CamKick(1.0f); Game.I.Sfx?.CurseCrush(GlobalPosition); Game.I.PlayerSound(GlobalPosition, 2.2f);
        ClearDrainLinks();
        if (_drainVfx != null && GodotObject.IsInstanceValid(_drainVfx)) { _drainVfx.QueueFree(); _drainVfx = null; }
        _noFall = 3f;   // safe landing from the hover
    }

    // Life Curse: an instant rune-nuke. Damage to each foe = a fraction of its MAX HP, and that fraction grows the LOWER
    // her current HP is (floor 10% → ceiling 50%), with a lower cap for bosses/minibosses so it can't delete their phases.
    private void FireLifeCurse(int t)
    {
        var col = DamageTypes.Col(DamageType.Curse);
        float radius = (13f + t) * S.SpellArea;
        float missing = 1f - Mathf.Clamp(Hp / Mathf.Max(1f, S.MaxHp), 0f, 1f);
        float frac = Mathf.Lerp(0.10f, 0.50f, Mathf.Pow(missing, 1.3f));
        SetArm("crush", 0.5f);
        foreach (var e in Game.I.Enemies.ToArray())
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
            if (Flat(e, GlobalPosition) > radius + e.Radius) continue;
            if (Game.I.SightBlocked(GlobalPosition, e.GlobalPosition)) continue;
            float useFrac = e.IsBoss ? Mathf.Min(frac, 0.22f) : frac;   // IsBoss covers boss + miniboss
            float dmg = useFrac * e.MaxHp;
            e.Hurt(dmg, DamageType.Curse, true);                        // cursed foes still eat the curse-bonus amp on top
            if (!e.Dead) e.AddCurse(2f, 0, CurseBonusType, CurseBonusMul, CurseShareFrac, CurseBonusType2);   // curse the survivors
            if (ModRite && !e.Dead) Heal(dmg * 0.05f);                  // Blood Rite: siphon a sliver of the damage back as health
        }
        Game.I.DamageWorld(GlobalPosition, radius, frac * 60f);
        Game.I.SpawnGroundSigil(GlobalPosition, radius, col);
        Ring(GlobalPosition, col, radius, 0.85f);
        Ring(GlobalPosition, col.Lerp(Colors.White, 0.4f), radius * 0.6f, 0.5f);
        CurseImplosion(GlobalPosition + Vector3.Up * 1.2f, col, 1.9f);
        Game.I.NetMgr?.BroadcastVfx(61, GlobalPosition, Vector3.Zero, radius, 0f, col);
        CamKick(1.1f); Game.I.Sfx?.CurseCrush(GlobalPosition); Game.I.PlayerSound(GlobalPosition, 2.2f);
        Game.I.Hud?.Banner("LIFE CURSE");
    }

    private void UpdateUlt(float dt)
    {
        if (_galeGuard > 0f) _galeGuard -= dt;   // Tailwind post-dash window decays (NEW)
        if (WitheringPresence)   // (NEW) legendary: her very presence lightly curses AND rots every foe near her (small tick)
        {
            _witherT -= dt;
            if (_witherT <= 0f)
            {
                _witherT = 0.4f;
                float wr = 10f * S.SpellArea;
                foreach (var e in Game.I.Enemies.ToArray())
                {
                    if (e == null || e.Dead || !GodotObject.IsInstanceValid(e) || Flat(e, GlobalPosition) >= wr + e.Radius) continue;
                    e.AddCurse(0.15f, 0, CurseBonusType, CurseBonusMul, CurseShareFrac, CurseBonusType2);   // presence curses (light; group 0 → no auto-tether)
                    e.Hurt(Base() * 0.15f + e.MaxHp * 0.0015f, DamageType.Curse, false);                   // small rot: flat + a hair of max HP so even big foes visibly wither
                }
            }
        }
        if (_windBoonT > 0f) _windBoonT -= dt;   // Eyewall buff decays (NEW)
        if (Cloudfeather && Airborne && !Downed && Hp < S.MaxHp) Heal(S.MaxHp * 0.025f * dt);   // Cloudfeather: mend while aloft (NEW)
        if (_barkT > 0f) { _barkT -= dt; if (_barkT <= 0f) BarkBurst(); }   // Barkskin expiry burst
        if (UltActive && (Ult == UltKind.GroveGuardian || Ult == UltKind.WildSwarm || Ult == UltKind.Barkskin
            || Ult == UltKind.Cyclone || Ult == UltKind.Blizzard || Ult == UltKind.FrostElemental || Ult == UltKind.DeepFreeze))   // simple-countdown ults (NEW)
        {
            UltActiveT -= dt;
            if (UltActiveT <= 0f) UltActive = false;
        }
        if (UltActive && Ult == UltKind.Stormform)   // Stormform: countdown + (legendary) drop air-mines while moving (NEW)
        {
            UltActiveT -= dt;
            if (UltActiveT <= 0f) UltActive = false;
            else if (ModStorm)
            {
                _mineDropT -= dt;
                if (_mineDropT <= 0f && InputDir() != Vector3.Zero && !Airborne)
                {
                    _mineDropT = 0.8f;   // leave a mine in her wake roughly every 0.8s of walking
                    float gy = Game.I.SurfaceHeight(GlobalPosition, GlobalPosition.Y);
                    var mine = new AirMine(); Game.I.AddChild(mine);
                    mine.Init(this, new Vector3(GlobalPosition.X, gy, GlobalPosition.Z), Base() * 0.6f);
                }
            }
        }
        if (UltActive && Ult == UltKind.Hurricane)   // Hurricane: when it ends she falls; clear the funnel + suppress fall damage (NEW)
        {
            UltActiveT -= dt;
            if (UltActiveT <= 0f)
            {
                UltActive = false;
                _noFall = 3f;   // safe landing from the hover height
                if (_hurriVfx != null && GodotObject.IsInstanceValid(_hurriVfx)) _hurriVfx.QueueFree();
                _hurriVfx = null;
            }
        }
        if (UltActive && Ult == UltKind.FaithShield && (Game.I.Shield == null || !GodotObject.IsInstanceValid(Game.I.Shield)))
            UltActive = false;
        if (UltActive && (Ult == UltKind.Eclipse || Ult == UltKind.LunarLight))
        {
            UltActiveT -= dt;
            if (UltActiveT <= 0f) { UltActive = false; UltDmgMul = 1f; }
        }
        if (BurnLifestealT > 0f) BurnLifestealT -= dt;   // (NEW) Wildfire Rush lifesteal window
        if (EmberFervorT > 0f)   // (NEW) Ember Fervor buff: decay + a periodic ember pulse so allies see the flames
        {
            EmberFervorT -= dt; _fervorNetT -= dt;
            if (_fervorNetT <= 0f) { _fervorNetT = 0.5f; Game.I.NetMgr?.BroadcastVfx(70, GlobalPosition + Vector3.Up * 0.5f, Vector3.Zero, 1.6f, 0f, DamageTypes.Col(DamageType.Ember)); }
            if (EmberFervorT <= 0f) ShowFervorFlames(false);
        }
        if (UltActive && Ult == UltKind.WildfireRush)     // (NEW) Wildfire Rush: dash window ends after 10s or once all charges are spent
        {
            _flameDashWindowT -= dt;
            if (_flameDashWindowT <= 0f || _flameDashCharges <= 0) UltActive = false;
        }
        if (UltActive && Ult == UltKind.HexCircle) UpdateHexCircle(dt);   // (NEW) Forsaken curse field
        if (UltActive && Ult == UltKind.Crescent)
        {
            UpdateCrescentControl(dt);
            UltActiveT -= dt;
            if (UltActiveT <= 0f)   // duration over — blades vanish
            {
                foreach (var o in _crescents.ToArray()) if (GodotObject.IsInstanceValid(o)) o.QueueFree();
                _crescents.Clear();
                UltActive = false;
            }
        }
    }

    private Vector3 GroundAim()
    {
        var o = _cam.GlobalPosition; var d = -_cam.GlobalTransform.Basis.Z;
        Vector3 p;
        if (Mathf.Abs(d.Y) < 0.001f) p = GlobalPosition + new Vector3(d.X, 0, d.Z).Normalized() * 16f;
        else { float t = -o.Y / d.Y; if (t < 0) t = 16f; p = o + d * t; }
        var flat = new Vector3(p.X - GlobalPosition.X, 0, p.Z - GlobalPosition.Z);
        if (flat.Length() > 40f) p = GlobalPosition + flat.Normalized() * 40f;
        return p;
    }

    private void DeployLunarLight()
    {
        var at = GroundAim();
        float t = UltTier;
        var f = new GroundField
        {
            Type = FieldType.Heal, HealAllies = true, Beam = true, TintColor = DamageTypes.Col(DamageType.Lunar),
            Radius = 9f + t + (ModLight ? 3f : 0f), Dur = 8f + t,
            Power = S.MaxHp * (0.04f + (ModLight ? 0.02f : 0f)),
            EnemyDmg = Base() * (0.7f + t * 0.12f), FromCombo = true, DType = DamageType.Lunar   // moderate, scales with Atk
        };
        Game.I.AddChild(f);
        f.GlobalPosition = new Vector3(at.X, 0.04f, at.Z);
        Game.I.NetMgr?.BroadcastVfx(6, new Vector3(at.X, 0.5f, at.Z), Vector3.Zero, 9f, 0f, DamageTypes.Col(DamageType.Lunar));
        var lun = DamageTypes.Col(DamageType.Lunar);
        var pillar = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = f.Radius * 0.5f, BottomRadius = f.Radius * 0.85f, Height = 30f } };
        pillar.MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = new Color(lun.R, lun.G, lun.B, 0.20f),
            EmissionEnabled = true, Emission = lun, EmissionEnergyMultiplier = 1.4f,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
        };
        pillar.Position = new Vector3(0, 15f, 0);
        f.AddChild(pillar);
        Ring(new Vector3(at.X, 0.04f, at.Z), lun, f.Radius, 0.7f);
        Ring(new Vector3(at.X, 0.04f, at.Z), Colors.White, f.Radius * 0.5f, 0.5f);

        UltActive = true; UltActiveT = f.Dur;
        Game.I.Hud?.Banner("LUNAR LIGHT");
    }

    // bark sheaths you (and visually your ents): full damage immunity for the window; called on self and on allies via RPC
    // Wardbane dispel: strips all current wards, zeroes the lunar shield, and suppresses regaining either
    // for a few seconds — the counter to camping behind blood/thorn shield generators.
    public void Dispel(float suppressDur)
    {
        if (Downed) return;
        Armor.Clear();
        Shield = 0f;
        _shieldT = 0.6f;
        ShieldSuppress = Mathf.Max(ShieldSuppress, suppressDur);
        Game.I.Hud?.Banner("shields sundered!");
        Game.I.Sfx?.Fizzle();
        Ring(GlobalPosition, DamageTypes.Col(DamageType.Curse), 3f, 0.5f);
    }

    public void GrantBark(float dur)
    {
        if (dur <= 0f || Downed) return;
        _barkT = dur; _barkMax = dur;
        _barkDmg = Base() * (2.0f + UltTier * 0.5f);
        Shield = MaxShield;
        Game.I.Hud?.Banner("Barkskin — thorns up!");
        Ring(GlobalPosition, DamageTypes.Col(DamageType.Nature), 4f, 0.5f);
        foreach (var t in Ents.ToArray()) if (t != null && GodotObject.IsInstanceValid(t)) { t.Heal(t.MaxHp); Game.I.VfxRing(t.GlobalPosition, DamageTypes.Col(DamageType.Nature), 2f, 0.5f); }
    }

    private void BarkBurst()
    {
        var col = DamageTypes.Col(DamageType.Nature);
        float r = (ModBark ? 9f : 7f) * S.SpellArea;
        foreach (var e in Game.I.Enemies.ToArray())
            if (e != null && !e.Dead && GodotObject.IsInstanceValid(e) && Flat(e, GlobalPosition) < r + e.Radius)
            { e.Hurt(_barkDmg, DamageType.Nature, true); e.Root(1.0f); }
        // spikes erupt at random spots around you, each impaling a foe near it and applying poison
        int spikes = 6 + UltTier * 2 + (ModBark ? 4 : 0);
        for (int i = 0; i < spikes; i++)
        {
            float a = GD.Randf() * Mathf.Tau, rr = GD.Randf() * r * 1.3f;
            var sp = GlobalPosition + new Vector3(Mathf.Cos(a) * rr, 0, Mathf.Sin(a) * rr);
            foreach (var e in Game.I.Enemies.ToArray())
                if (e != null && !e.Dead && GodotObject.IsInstanceValid(e) && new Vector2(e.GlobalPosition.X - sp.X, e.GlobalPosition.Z - sp.Z).Length() < 2.0f + e.Radius)
                { e.Hurt(_barkDmg * 0.4f, DamageType.Nature, true); e.Poison(Base() * 0.18f, 3.5f); break; }
            var spike = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.28f, Height = 1.8f }, MaterialOverride = Game.ToonEmissive(col, 1.0f, 0.03f) };
            Game.I.AddChild(spike); spike.GlobalPosition = new Vector3(sp.X, 0.2f, sp.Z);
            var tw = spike.CreateTween(); tw.TweenProperty(spike, "position", new Vector3(sp.X, 1.1f, sp.Z), 0.12f);
            tw.TweenInterval(0.35f); tw.TweenProperty(spike, "transparency", 1f, 0.3f);
            tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(spike)) spike.QueueFree(); }));
        }
        if (ModBark)   // legendary: leave a creeping poison field behind
        {
            var f = new GroundField { Type = FieldType.Hex, TintColor = col, Radius = r, Dur = 5f, Power = Base() * 0.1f, PoisonAdd = 3f, SlowMul = 0.5f, FromCombo = true, DType = DamageType.Nature, Src = this };
            Game.I.AddChild(f); f.GlobalPosition = new Vector3(GlobalPosition.X, 0.04f, GlobalPosition.Z);
            Game.I.RegisterComboField(f);
        }
        Game.I.DamageWorld(GlobalPosition, r * 1.3f, _barkDmg);   // (FIX) burst + spikes break props too
        Ring(GlobalPosition, col, r, 0.5f);
        Game.I.NetMgr?.BroadcastVfx(6, GlobalPosition, Vector3.Zero, r, 0f, col);
        // the burst also erupts around each of your tree-ents (your ally-minions burst on their owner's machine)
        foreach (var ent in Ents.ToArray())
        {
            if (ent == null || !GodotObject.IsInstanceValid(ent)) continue;
            var ep = ent.GlobalPosition; float er = r * 0.7f;
            foreach (var e in Game.I.Enemies.ToArray())
                if (e != null && !e.Dead && GodotObject.IsInstanceValid(e) && new Vector2(e.GlobalPosition.X - ep.X, e.GlobalPosition.Z - ep.Z).Length() < er + e.Radius)
                { e.Hurt(_barkDmg * 0.6f, DamageType.Nature, true); e.Root(0.8f); e.Poison(Base() * 0.15f, 3f); }
            Game.I.VfxRing(ep, col, er, 0.45f);
        }
        Game.I.Sfx?.Impact(DamageType.Nature);
        CamKick(0.6f);
    }

    private void SpawnCrescents()
    {
        int count = 4 + UltTier + (ModCrescent ? 2 : 0);
        UltActive = true;
        UltActiveT = 9f + UltTier * 1.5f;     // they orbit for this long; fling & re-fling freely within it
        for (int i = 0; i < count; i++)
        {
            var orb = new CrescentOrb { Angle = i / (float)count * Mathf.Tau, OrbitR = 4.5f, Dmg = Base() * (3.5f + UltTier * 0.6f) };   // scales with Atk now (was flat 40+22t)
            Game.I.AddChild(orb);
            _crescents.Add(orb);
        }
        Ring(GlobalPosition, DamageTypes.Col(DamageType.Lunar), 5f, 0.6f);
        // allies see the orbs via the per-frame CrescentSnapshot (positions), covering orbit + fling + rotate

        CamKick(0.5f);
        Game.I.Hud?.Banner("CRESCENT MOON");
    }

    // Crescent control while the ult is up: hold LMB to drive the blades forward toward the reticle (the
    // longer you hold, the farther they reach, and they keep tracking your aim); hold RMB to lock them
    // orbiting the spot they're at, chasing each other in a wide horizontal ring; release either and they
    // glide back to orbit you. The smooth lerp in CrescentOrb makes the transitions read naturally.
    private float _cresHold = 0f;
    private bool _cresInPlace = false;
    private Vector3 _cresCenter;
    private void UpdateCrescentControl(float dt)
    {
        bool lmb = Input.IsActionPressed("cast");
        bool rmb = Input.IsActionPressed("charge");
        Vector3 aim = AimDir(); aim.Y = 0;
        if (aim.LengthSquared() < 0.001f) aim = -GlobalTransform.Basis.Z;
        aim = aim.Normalized();

        int mode; Vector3 center; float radius, spin;
        if (lmb)
        {
            _cresHold = Mathf.Min(1.6f, _cresHold + dt);
            _cresInPlace = false;
            float reach = 3f + _cresHold * 14f;              // hold longer → push farther (up to ~25m)
            mode = 1; center = GlobalPosition + aim * reach; radius = 2.6f; spin = 7f;
        }
        else if (rmb)
        {
            if (!_cresInPlace)                                // capture where they are right now and lock the ring there
            {
                _cresInPlace = true;
                Vector3 sum = Vector3.Zero; int c = 0;
                foreach (var o in _crescents) if (GodotObject.IsInstanceValid(o)) { sum += o.GlobalPosition; c++; }
                _cresCenter = c > 0 ? sum / c : GlobalPosition + aim * 6f;
            }
            _cresHold = 0f;
            mode = 2; center = _cresCenter; radius = 6f; spin = 8f;   // wide + fast → they chase each other
        }
        else { _cresHold = 0f; _cresInPlace = false; mode = 0; center = GlobalPosition; radius = 4.5f; spin = 2.2f; }

        foreach (var o in _crescents)
            if (GodotObject.IsInstanceValid(o)) o.SetControl(mode, center, radius, spin);
    }

    public void RemoveCrescent(CrescentOrb o)
    {
        _crescents.Remove(o);
    }

    public void AddMana(float amt) { Mana = Mathf.Clamp(Mana + amt, 0, S.ManaMax); }
    public void TryBurnLifesteal(float dmg) { if (BurnLifestealT > 0f && !Downed && Hp < S.MaxHp) Heal(dmg); }   // (NEW) Wildfire Rush: 100% of burn-tick damage heals her while the window is live
    public bool SpendMana(float n = 1f) { if (Mana >= n) { Mana -= n; return true; } ResFail(); return false; }
    // a cast couldn't be paid for (mana for most witches, HP for Crimson): flash the bar + sputter.
    // rising-edge sound so holding the button with an empty bar doesn't machine-gun the sparks.
    public void ResFail()
    {
        bool wasQuiet = ManaFlash <= 0f;
        ManaFlash = 0.4f;
        if (wasQuiet) Game.I?.Sfx?.Fizzle();
    }
    // pressed a real spell-combo key but it's still charging: sputter + flash THAT pip (not the resource bar)
    private void FinNotReady(FinisherSlot f)
    {
        bool wasQuiet = f.NotReadyFlash <= 0f;
        f.NotReadyFlash = 0.4f;
        if (wasQuiet) Game.I?.Sfx?.Fizzle();
    }

    public void AddXp(float amt)
    {
        Xp += amt;
        while (Xp >= XpNext) { Xp -= XpNext; Level++; ApplyLevelGain(); XpNext = 28f + (Level - 1) * 22f; if (DivineWitch && Level % 10 == 0) Interventions = Mathf.Min(2, Interventions + 1); Game.I.OpenLevelUp(); }
    }

    // Each level is rarer now, so each one grants a small permanent power bump to keep the curve climbing.
    private void ApplyLevelGain()
    {
        const float g = 0.0075f;                 // +0.75% base damage & max HP per level
        S.Atk *= 1f + g;
        float oldMax = S.MaxHp;
        S.MaxHp *= 1f + g;
        Hp = Mathf.Min(S.MaxHp, Hp + (S.MaxHp - oldMax));   // keep the new headroom filled
        S.ShieldPct *= 1f + g * 0.5f;            // shield capacity grows at half rate
    }

    public float DmgDirT = 0f;
    public Vector3 DmgDirWorld = Vector3.Forward;

    public void Hurt(float dmg, Vector3? src = null)
    {
        if (_iframe > 0 || _divFalling || Divinity || BarkActive || Downed || Game.I == null || !Game.I.WorldRunning) return;
        _combatT = 0f;   // taking fire = in combat; gates fast out-of-combat shield regen (NEW)
        if (Armor.Count > 0)   // one shared armor charge eats this whole hit, then pops (thorn charges also burst)
        {
            var ch = Armor[Armor.Count - 1]; Armor.RemoveAt(Armor.Count - 1);
            _iframe = 0.5f; ProcFlash = 0.3f;
            if (ch.Thorn)
            {
                float r = ModThornSkin ? 7f : 5f;
                foreach (var e in Game.I.Enemies.ToArray())
                    if (e != null && !e.Dead && GodotObject.IsInstanceValid(e) && Flat(e, GlobalPosition) < r + e.Radius)
                    { e.Hurt(ch.Dmg, DamageType.Nature, true); if (ModThornSkin) { e.Root(1.2f); e.Poison(Base() * 0.15f, 3f); } }
                Game.I.DamageWorld(GlobalPosition, r, ch.Dmg);   // (FIX) AoE breaks props too
                Ring(GlobalPosition, DamageTypes.Col(DamageType.Nature), r, 0.45f);
                Game.I.NetMgr?.BroadcastVfx(6, GlobalPosition, Vector3.Zero, r, 0f, DamageTypes.Col(DamageType.Nature));
                Game.I.Sfx?.Impact(DamageType.Nature);
            }
            else
            {
                Ring(GlobalPosition, DamageTypes.Col(DamageType.Blood), 2.6f, 0.35f);
                Game.I.Sfx?.Impact(DamageType.Blood);
            }
            return;
        }
        _iframe = 0.7f;
        HurtT = 1f;
        if (src.HasValue) { DmgDirWorld = src.Value - GlobalPosition; DmgDirWorld.Y = 0; DmgDirT = 1.4f; }
        dmg *= Mathf.Clamp(1f - S.DmgResist, 0.1f, 1f);   // damage resistance
        if (_galeGuard > 0f) dmg *= 0.55f;   // Tailwind: ~45% less damage in the window after a dash (Gale) (NEW)
        bool hadShield = Shield > 0f;
        if (Shield > 0) { if (dmg <= Shield) { Shield -= dmg; dmg = 0; } else { dmg -= Shield; Shield = 0; } }
        // emptying the shield (or being hit with none left) means a much longer wait before it rebuilds
        _shieldT = (Shield <= 0.01f) ? S.ShieldDelay * 2.4f : S.ShieldDelay;
        if (dmg > 0)
        {
            Hp -= dmg;
            Game.I.MyStats.DamageTaken += dmg;   // (NEW) end-of-run tally
            if (Combo > 1)   // a hit that reaches HP breaks your combo (shield protects it)
            {
                Combo = (int)(Combo * ComboBreakKeep);
                _lastAct = ComboAct.None;
                Game.I.Sfx?.Discord();
                Game.I.Hud?.ComboBreak();
            }
            if (Hp <= 0)
            {
                if (GodMode) { Hp = 1f; }   // (NEW) god mode takes the hit + shows numbers, but never dies
                else if (PhoenixActive && _phoenixRebirth) { PhoenixRebirth(); return; }   // (NEW) Phoenix Ascendant: reborn in flame instead of dying (once)
                else if (Interventions > 0) { DivineRez(GlobalPosition, true); return; }
                else { Hp = 0; GoDown(); }
            }
        }
    }

    // Incapacitated rather than dead: frozen until an ally revives, or game-over if everyone is down.
    public void GoDown()
    {
        if (Downed) return;
        Downed = true; Hp = 0f; ReviveProg = 0f;
        _meteorAscend = false; _phoenix = false; _flameDashT = 0f;   // (NEW) cancel any Ember flight ult cleanly
        if (_phoenixVfx != null && GodotObject.IsInstanceValid(_phoenixVfx)) { _phoenixVfx.QueueFree(); _phoenixVfx = null; }
        HideEmberAimRing();
        EmberFervorT = 0f; ShowFervorFlames(false);   // (NEW) drop the Ember Fervor buff/flames
        Game.I.MyStats.TimesDowned++;   // (NEW) end-of-run tally
        Charging = false; ChargeAmt = 0f;
        if (_beamSeg != null) { _beamSeg.Free(); _beamSeg = null; _beamLight = null; _beamT = 0; }
        Game.I.Hud?.Banner("DOWNED — hold on for an ally");
        Game.I.Sfx?.Discord();
        if (Game.I.NetMgr != null && Game.I.NetMgr.Active) Game.I.NetMgr.LocalDowned(true);
        else Game.I.GameOver();   // solo: no one can revive
    }

    // An ally finished reviving us (or a network revive arrived).
    public void ReviveMe(float frac, bool beam)
    {
        Downed = false; ReviveProg = 0f;
        Hp = Mathf.Max(1f, S.MaxHp * frac);
        _iframe = 2f; BlessedT = Mathf.Max(BlessedT, 2f);
        Game.I.Hud?.Banner("REVIVED");
        Game.I.Sfx?.Release(DamageType.Holy);
        if (beam) Game.I.RezBeam(GlobalPosition, true);
        if (Game.I.NetMgr != null && Game.I.NetMgr.Active) Game.I.NetMgr.LocalDowned(false);
    }

    // Divine passive: spend a banked charge to rez instantly with a sky-beam + medium-heal AoE.
    public void DivineRez(Vector3 at, bool self)
    {
        if (Interventions > 0) Interventions--;
        Downed = false; ReviveProg = 0f;
        Hp = S.MaxHp; _iframe = 1.6f; BlessedT = Mathf.Max(BlessedT, 4f);
        Game.I.Hud?.Banner(self ? "DIVINE INTERVENTION" : "DIVINE REVIVAL");
        Game.I.Sfx?.Release(DamageType.Holy);
        Game.I.RezBeam(at, true);
        if (MartyrGrace)   // erupt with holy light: full shield, mend nearby allies, blast foes back
        {
            Shield = MaxShield;
            Game.I.NetMgr?.HealAlliesNear(GlobalPosition, 16f, S.MaxHp * 0.25f);
            HealOwnMinions(S.MaxHp * 0.25f);
            foreach (var en in Game.I.Enemies.ToArray())
                if (en != null && !en.Dead && GodotObject.IsInstanceValid(en) && Flat(en, GlobalPosition) < 9f + en.Radius)
                { en.Knockback(GlobalPosition, 9f); en.Hurt(Base() * 1.2f, DamageType.Holy, true); }
            Game.I.DamageWorld(GlobalPosition, 9f, Base() * 1.2f);   // (FIX) AoE breaks props too
            Ring(GlobalPosition, DamageTypes.Col(DamageType.Holy), 9f, 0.5f);
            Game.I.NetMgr?.BroadcastVfx(6, GlobalPosition, Vector3.Zero, 9f, 0f, DamageTypes.Col(DamageType.Holy));
        }
        if (self && Game.I.NetMgr != null && Game.I.NetMgr.Active) Game.I.NetMgr.LocalDowned(false);
    }
}
