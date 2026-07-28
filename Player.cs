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
public partial class Player : Node3D, Grove.Dev.Ai.IAiObservable
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
    // ---- gamepad right-stick look ----
    public static float PadLookSens = 3.1f;     // base yaw/pitch rate (rad/s) at full stick deflection
    public static float PadSensMul = 1f;        // user "Gamepad Look" setting multiplier (persisted alongside look sens)
    private const float PadLookDead = 0.16f;    // radial deadzone
    private Vector2 _padLook = Vector2.Zero;    // smoothed stick vector
    private float _turn180 = 0f;                // remaining yaw for an R3 quick-turn

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
    public float HurtFlash = 0f;      // (NEW) full-screen red vignette intensity, scaled by hit severity
    public float ShieldBreakT = 0f;   // (NEW) spikes when a hit empties the shield → HUD flash + callout
    public float ArmorBreakT = 0f;    // (NEW) spikes when an armor charge pops → HUD flash + callout
    public bool LowHp => !Downed && Hp > 0f && Hp <= S.MaxHp * 0.20f;   // (NEW) low-health alarm state
    private bool _lowHpWarned = false;
    private float _heartT = 0f;
    public float DashT = 0f;          // recent-dodge spike for musical tension

    // ---- ultimate ----
    public enum UltKind { None, Eclipse, LunarLight, Crescent, FaithShield, Judgement, Divinity, BloodTsunami, Exsanguinate, BloodRot, GroveGuardian, WildSwarm, Barkskin, Cyclone, Hurricane, Stormform, Blizzard, FrostElemental, DeepFreeze, HexCircle, LifeDrain, LifeCurse, MeteorDescent, WildfireRush, PhoenixAscend, ArcaneAscend, ArcaneEruption, ArcaneOvercharge }   // …Arcane = ArcaneAscend/ArcaneEruption/ArcaneOvercharge (NEW)
    public UltKind Ult = UltKind.None;
    public float UltCharge = 0f;       // 0..1
    public float UltLingerT = 0f;      // (NEW) while >0, an ult's lingering field/summon/transformation is still active → NO ult recharge (anti-chain; DoTs/instant bursts are unaffected)
    public float DmgWindow = 0f;        // damage dealt since last team-damage broadcast (ult-share)
    public int UltTier = 0;            // rarity tier 0..4 (upgraded via Epic level-up cards)
    // (ULT CARDS) per-ult tier, so tiers PERSIST across a swap — drop an ult and re-pick it later and it comes back at
    // the tier you had it. Cleared only on witch config / new run.
    public readonly System.Collections.Generic.Dictionary<UltKind, int> UltTiers = new();
    public void EquipUlt(UltKind k)
    {
        Ult = k;
        UltTier = UltTiers.TryGetValue(k, out int tr) ? tr : 0;   // restore this ult's saved tier
        UltCharge = 0f;
        Game.I?.Hud?.Banner(UltTier > 0 ? "ultimate re-bound" : "ultimate bound");
    }
    public void UpgradeUltTier()
    {
        if (Ult == UltKind.None || UltTier >= 4) return;
        UltTier++;
        UltTiers[Ult] = UltTier;   // remember it for after a swap
        Game.I?.Hud?.Banner("ultimate empowered");
    }
    public bool UltActive = false;
    public float UltActiveT = 0f;
    public float UltDmgMul = 1f;
    // ---- Arcane witch: passive (crit-heal) + ult state ----
    private const float ArcaneCritHeal = 0.25f;   // heals 25% of a crit's damage (her passive)
    public float ArcanePowerMul = 1f;             // Arcane affinity: her spell damage (missiles / chain-lightning / ult lightning)
    public float ArcaneCritHealBonus = 0f;        // Arcane affinity: extra crit-heal fraction
    public bool ArcaneChainReaction = false;      // Arcane legendary: a foe slain by her arcane bursts in a nova
    public bool ArcanePersistMarks = false;       // Arcane legendary: the chain no longer burns off Conduit marks
    public bool ArcaneLiving = false;             // Arcane affinity capstone (Living Current) dedup gate
    private bool _inArcaneNova = false;           // reentrancy guard for the Chain Reaction nova
    private bool _arcaneAscend = false; private float _arcaneAscendFireT = 0f; private ArcaneAura _arcaneAura;
    private ArcaneAura _ultAura;   // (NEW) reusable element-coloured empowerment aura for other witches' ults (Eclipse / Divinity / …)
    private void GrantUltAura(Color col, float radius = 2.6f) { if (_ultAura != null && GodotObject.IsInstanceValid(_ultAura)) _ultAura.QueueFree(); _ultAura = new ArcaneAura(); AddChild(_ultAura); _ultAura.Init(radius, 0f, col); }
    private void ClearUltAura() { if (_ultAura != null && GodotObject.IsInstanceValid(_ultAura)) { _ultAura.QueueFree(); _ultAura = null; } }
    public bool ArcaneAscendActive => _arcaneAscend;
    public bool OverchargeActive => false;   // (REWORK) ArcaneOvercharge is no longer a stat steroid — it's the Arcane Storm rain field; the old steroid buffs are neutralized here
    private float OverchargeSpeedMul => OverchargeActive ? (1.4f + UltTier * 0.06f + (ModArcUnbound ? 0.35f : 0f)) : 1f;   // Unbound: greater buffs
    private float OverchargeJumpMul => OverchargeActive ? (1.7f + UltTier * 0.12f + (ModArcUnbound ? 0.5f : 0f)) : 1f;
    public float OverchargeCrit => OverchargeActive ? (0.30f + UltTier * 0.05f + (ModArcUnbound ? 0.2f : 0f)) : 0f;
    public float OverchargeCritDmg => OverchargeActive ? (0.6f + UltTier * 0.18f + (ModArcUnbound ? 0.5f : 0f)) : 0f;
    public bool ModEclipse = false, ModLight = false, ModCrescent = false;   // legendary ult-mods
    public float EclipseCrit => (Ult == UltKind.Eclipse && UltActive) ? (0.25f + UltTier * 0.05f) : 0f;   // (REWORK) +25% crit, +5%/tier while eclipsed
    public bool EclipseOn => Ult == UltKind.Eclipse && UltActive;
    public float UltMax = 1f;   // (ULT METERS) the full duration of the current active ult, for the HUD duration bar
    private float _eclipseBoomCd = 0f;
    private bool _eclipseWasOn = false;
    private bool _eclipseNovaBusy = false;
    public bool EclipseNovaBusy => _eclipseNovaBusy;   // (ECLIPSE) true while a nova is dealing its own lunar damage → don't re-detonate
    // (ECLIPSE) a shadow-nova on ANY lunar hit she lands — fired from Enemy.Hurt on HER machine (so it catches left/right
    // click, charged, finishers, mods, AND fields/projectiles, and works whether she's host or client). Throttled; the
    // busy flag stops its own lunar damage from re-triggering. VFX broadcast so allies see the black/white burst.
    public void EclipseNovaAt(Vector3 at)
    {
        if (_eclipseBoomCd > 0f || _eclipseNovaBusy) return;
        _eclipseBoomCd = 0.12f;
        _eclipseNovaBusy = true;
        float r = 4.5f * S.SpellArea;
        float dmg = Base() * (0.55f + UltTier * 0.15f);
        foreach (var e in Game.I.Enemies.ToArray())
            if (e != null && !e.Dead && GodotObject.IsInstanceValid(e) &&
                new Vector2(e.GlobalPosition.X - at.X, e.GlobalPosition.Z - at.Z).Length() < r + e.Radius)
                e.Hurt(dmg, DamageType.Lunar, true);
        _eclipseNovaBusy = false;
        var up = at + Vector3.Up * 0.6f;
        Game.I.VfxRing(up, Colors.Black, r, 0.32f);
        Game.I.VfxRing(up, Colors.White, r * 0.62f, 0.26f);
        Game.I.NetMgr?.BroadcastVfx(0, up, Vector3.Up, r, 0.3f, Colors.White);   // allies see the shadow-nova ring
    }
    private void EclipseBlinkVfx(Vector3 at)
    {
        Game.I.VfxRing(at + Vector3.Up * 1f, Colors.Black, 3.2f, 0.3f);
        Game.I.VfxRing(at + Vector3.Up * 1f, Colors.White, 1.8f, 0.25f);
    }
    // (LUNAR LIGHT) purge the player's negative statuses — stun, root, slow, venom, and a Taker grab
    public void CleanseNegative()
    {
        StunT = 0f; _snareT = 0f; _slowT = 0f; VenomT = 0f; VenomDps = 0f; RootingSnakeId = 0;
        if (GrabbedBy != 0) GrabbedBy = 0;   // slip a kidnapper's grasp
    }
    public float EclipseSpeedMul => EclipseOn ? 2f : 1f;    // (REWORK) ×2 move speed while eclipsed
    public float EclipseJumpMul => EclipseOn ? 3f : 1f;     // (REWORK) ×3 jump height while eclipsed
    private bool RollCrit() => GD.Randf() < Mathf.Min(0.95f, S.CritChance + EclipseCrit + OverchargeCrit + (EmberFervorT > 0f ? _emberFervorCrit : 0f));
    public bool RollCritPublic() => RollCrit();       // (REWORK) so Deep Freeze icicles / Phoenix / Arcane Storm can crit with the caster's crit stat
    public float CritMultPublic() => CritMult();
    public float CritChanceNow => Mathf.Min(0.95f, S.CritChance + EclipseCrit + OverchargeCrit + (EmberFervorT > 0f ? _emberFervorCrit : 0f));   // (REWORK) effective crit chance snapshot for host-simulated field ults (Arcane Storm bolts respect her crit passive)
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
    public bool ModMeteorDesc = false, ModWildfire = false, ModPhoenix = false;   // ember legendary ult-mods (NEW)
    public bool ModArcStorm = false, ModArcCataclysm = false, ModArcUnbound = false;   // arcane legendary ult-mods (NEW)
    private int _phoenixRebirths = 0;   // (NEW) how many cheat-deaths remain this Phoenix (Immortal Phoenix ult-mod → 2)
    // ---- Ember ult runtime state (NEW) ----
    private bool _meteorAscend = false; private float _meteorAscendT = 0f, _meteorBaseY = 0f;   // Meteor Descent: rise + top-down aim window
    private bool _meteorDiving = false; private Vector3 _meteorDiveTarget;   // (NEW) Meteor Descent: the plummet phase (travel time) between confirm and impact
    private int _meteorRainLeft = 0; private float _meteorRainT = 0f;   // (REWORK) Meteor Descent: meteors that rain at random while she's aloft aiming
    private int _flameDashCharges = 0; private float _flameDashWindowT = 0f, _flameDashT = 0f, _flameDashDur = 0f, _flameDashDist = 0f; private Vector3 _flameDashDir;   // Wildfire Rush: dash stock + window + motion
    private int _windCharges = 0; private float _windWindowT = 0f;   // (STORMFORM REWORK) Wind Rush dash charges + use window (mirrors Wildfire Rush)
    public int WindCharges => _windCharges;   // HUD: charges left this Stormform
    public int FlameCharges => _flameDashCharges;   // HUD: charges left this Wildfire Rush
    private float _rushDashLingerT = 0f, _rushDashLingerMax = 1f;   // (REWORK) HUD: how long the LAST dash's lingering field (flame trail / wind area) still burns
    public float RushDashLingerT => _rushDashLingerT;
    public float RushDashLingerFrac => _rushDashLingerMax > 0.01f ? Mathf.Clamp(_rushDashLingerT / _rushDashLingerMax, 0f, 1f) : 0f;
    // HUD: time left in the CHARGE-SPEND WINDOW (Wildfire Rush / Wind Rush must spend charges before it closes)
    public float RushWindowT => Ult == UltKind.WildfireRush ? _flameDashWindowT : (Ult == UltKind.Stormform ? _windWindowT : 0f);
    public float RushWindowFrac => Ult == UltKind.WildfireRush ? Mathf.Clamp(_flameDashWindowT / 10f, 0f, 1f) : (Ult == UltKind.Stormform ? Mathf.Clamp(_windWindowT / 12f, 0f, 1f) : 0f);
    public float BurnLifestealT = 0f;   // Wildfire Rush: while >0, this player's burn ticks heal her 100%
    private bool _phoenix = false, _phoenixRebirth = false; private float _phoenixAuraT = 0f;   // Phoenix Ascendant: transform + one-shot cheat-death
    public bool PhoenixActive => Ult == UltKind.PhoenixAscend && UltActive;
    private Node3D _phoenixVfx;
    // ---- Ember Fervor finisher buff (crit + move speed; witch-agnostic) ----
    public float EmberFervorT = 0f; private float _emberFervorCrit = 0f, _emberFervorSpeed = 0f, _fervorNetT = 0f;
    public int FervorWildfire = 0, FervorPhoenix = 0;   // (OVERHAUL) Fervor evolutions: hits-ignite (Wildfire) / heal-over-buff (Phoenix Heart)
    public float EmberBurnMul = 1f, FlameReachMul = 1f, LivingBombMul = 1f;   // (NEW) Ember-affinity blessings scale burn / flame reach / Living-Bomb blasts
    public bool EmberInferno = false;   // (NEW) Ember legendary blessing (once)
    public float FireWallT = 0f;   // (NEW) Ring of Fire can't recharge while an active wall is still burning
    public float SnakeRootCd = 0f; public int RootingSnakeId = 0;   // (NEW) snake root: once per 5s per player, ground-only, ends when that snake dies
    public void TrySnakeRoot(int snakeId)
    {
        if (Downed || Airborne || SnakeRootCd > 0f) return;   // ground only; throttled per player
        SnareMe(2f); SnakeRootCd = 5f; RootingSnakeId = snakeId;
    }
    public void ClearSnakeRoot(int snakeId) { if (RootingSnakeId == snakeId && snakeId != 0) { _snareT = 0f; RootingSnakeId = 0; } }   // the rooting snake died → free the player
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
    private float HurricaneSpeedMul => (Ult == UltKind.Hurricane && UltActive) ? 2.5f : 1f;   // (REWORK) ×2.5 caster move speed while piloting the hurricane
    public float WindZoneT = 0f; private float WindZoneMul => WindZoneT > 0f ? 3f : 1f;   // (WIND RUSH) ×3 move speed while standing in a wind area
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
    public bool ArcaneWitch = false;    // (NEW) Arcane — 3-round homing missile burst + a chargeable sustained beam that arcane-marks foes
    public int WitchIndex => ArcaneWitch ? 8 : EmberWitch ? 7 : ForsakenWitch ? 6 : FrostWitch ? 5 : GaleWitch ? 4 : (VerdantWitch ? 3 : (CrimsonWitch ? 2 : (DivineWitch ? 1 : 0)));   // 0 Lunar,1 Divine,2 Crimson,3 Verdant,4 Gale,5 Frost,6 Forsaken,7 Ember,8 Arcane

    // --- DEV visual-test harness read-outs (see res://dev/ai). Read-only exposure of internal state; no behavioural coupling.
    // (Grounded already exists elsewhere on Player.) ---
    public float VyDebug => _vy;

    // Semantic snapshot for the AI test runner (opted in via the "ai_observable" group in _Ready). NOT a full property dump.
    public Godot.Collections.Dictionary GetAiDebugState()
    {
        var d = new Godot.Collections.Dictionary
        {
            { "witch_index", WitchIndex },
            { "position", new Godot.Collections.Array { GlobalPosition.X, GlobalPosition.Y, GlobalPosition.Z } },
            { "grounded", _grounded },
            { "vy", _vy },
            { "hp", Hp },
            { "mana", Mana },
            { "charging", Charging },
            { "charge_amt", ChargeAmt },
            { "ult", Ult.ToString() },
            { "ult_active", UltActive },
            { "downed", Downed },
        };
        // authored-puppet animation read-out (the tp3 witch), when present
        if (_tp3Puppet != null && GodotObject.IsInstanceValid(_tp3Puppet))
            d["tp3_puppet"] = _tp3Puppet.GetAiDebugState();
        return d;
    }
    // (NEW) does this witch's PRIMARY fire launch piercing bolts? Lunar/Divine/Crimson/Verdant/Gale do; Frost/Forsaken/Ember use
    // beams/cones (which already hit everything in their path) and Arcane uses homing missiles — so S.Pierce does nothing for them.
    public bool FiresBolts => !FrostWitch && !ForsakenWitch && !EmberWitch && !ArcaneWitch;
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
    public int MaxEnts => 4 + GroveBonusEnts;                    // (REWORK) base 4 (was 3); grows via Grove perks/cards
    private float _groveTrickleT = 0f;                           // (REWORK) Living Grove: a slow passive summon even without combo
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

    // (NEW) "Grafted Element": the Verdant Witch can attune her tree-ents to a chosen damage type — their explosions deal it
    // (plus a fitting on-hit effect), and the ents visibly take on that element's look for the rest of the run.
    public DamageType EntElement = DamageType.Nature;
    public bool EntElementChosen = false;
    public void RefreshEntVisuals() { foreach (var e in Ents) if (e != null && GodotObject.IsInstanceValid(e)) e.SetElement(EntElement); }
    public void ApplyEntStatus(Enemy e, Vector3 center)   // element-specific rider on an ent blast (on top of the usual poison+root)
    {
        switch (EntElement)
        {
            case DamageType.Ember: e.AddBurn(1.5f, Base() * 0.08f, Base() * 3f, 0f, Game.I.LocalPeer); break;
            case DamageType.Frost: e.AddFreeze(1.5f, FreezeThreshMul, FrostDurBonus); break;
            case DamageType.Curse: e.Mark(3f, S.MarkAmp, 0); break;
            case DamageType.Lunar: e.Slow(1.4f, 0.65f); break;
            case DamageType.Wind:  e.Knockback(center, 7f); break;
            case DamageType.Holy:  Heal(S.MaxHp * 0.01f); break;
            case DamageType.Blood: BloodReward(0.25f); break;
            // Arcane / Nature: no extra rider (Nature already gets its poison + root)
        }
    }

    // Wild Swarm ult: a forward-charging stampede of critters that trample everything in their path,
    // then vanish. They can't be damaged, detonated, or targeted — pure sweeping offense. ModSwarm
    // (Teeming Grove) makes the wave wider, deeper, and more numerous.
    private float LaunchStampede(int t)
    {
        Vector3 fwd = AimDir(); fwd.Y = 0;
        if (fwd.LengthSquared() < 0.01f) fwd = -GlobalTransform.Basis.Z;
        fwd = fwd.Normalized();
        float width = (9f + (ModSwarm ? 4f : 0f)) * S.SpellArea;   // Stampede stores _width raw; scale the trample lane here
        float dur = 12f + t * 1.0f + (ModSwarm ? 1.5f : 0f);       // (REWORK) base 12s
        float dmg = MinionBurst() * (0.75f + 0.15f * t);          // (REWORK) buffed per-hit; enemies in the lane get hit repeatedly as the stream passes
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
        float cd = S.CritDamage + OverchargeCritDmg;
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
    public void GrantRandomArmor()   // ONE random armor charge (blood or thorn), respects the cap
    {
        bool thorn = GD.Randf() < 0.5f;
        AddArmor(thorn, thorn ? Base() * 1.6f : 0f);
    }
    public void FillArmorRandom()    // chest reward: FILL every empty armor slot with random charges (blood/thorn)
    {
        int guard = 0;
        while (Armor.Count < MaxArmor && ShieldSuppress <= 0f && guard++ < 32)
        {
            int before = Armor.Count;
            GrantRandomArmor();
            if (Armor.Count == before) break;   // cap/suppression hit — stop
        }
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
        Heal(S.MaxHp * (CrimsonWitch ? 0.035f : 0.045f) * spend);   // (TWEAK) Crimson now also gets BURST from the dump, so lean her heal down a touch; other witches (heal-only) keep the old value
        if (spend > 0) { Ring(GlobalPosition, DamageTypes.Col(DamageType.Blood), 3.5f, 0.4f); ProcFlash = 0.25f; }
        if ((CrimsonWitch || Hemoclast) && spend > 0)   // (NEW) the stack-dump ALWAYS erupts a blood nova for Crimson (was Hemoclast-only — her signature loop had ZERO burst); Hemoclast makes it bigger
        {
            float mag = Hemoclast ? (0.5f + 0.35f * spend) : (0.35f + 0.25f * spend);   // base ≈Base×1.85 @6 stacks; Hemoclast ≈Base×2.6 (unchanged)
            float nr = (5f + spend * 0.8f) * S.SpellArea, nd = Base() * mag;
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
    // (NEW) VENOM — the Warded Phalanx's arrow rain leaves poison in you. It's refreshed for as long as you stand in
    // the volley circle (VenomHold) but only starts BITING once you're clear of it, so the punish lands after the
    // dodge, not on top of the field damage. Blessed purges it outright — a Divine ally can cleanse the whole party.
    public float VenomT = 0f, VenomDps = 0f, VenomHold = 0f;
    private float _venomTick = 0f;
    public bool Sanctuary = false;      // (NERFER) Sanctuary shrine armed → soft aura + angelic hum + 2 HP/s regen during the boss fight
    public bool Divinity = false;       // Divinity ult active (ascended, invulnerable)
    private float _divT = 0f, _divBaseY = 0f, _noFall = 0f;
    private bool _divFalling = false;   // stays invulnerable through the descent until her feet touch ground
    private bool _divRisen = false;      // (REWORK) finished the initial ascent → free-flight control is live
    // (EXSANGUINATE REWORK) a channeled blood transform: a DoT aura that ticks foes; a kill pops + heals her full
    private bool _exsang = false; private float _exsangRad = 12f, _exsangDps = 10f, _exsangTickT = 0f;
    public bool ExsangActive => _exsang;
    private void UpdateExsanguinate(float dt)
    {
        if (!_exsang) return;
        _exsangTickT -= dt;
        if (_exsangTickT > 0f) return;
        _exsangTickT = 0.4f;
        float tick = _exsangDps * 0.4f;
        bool anyKill = false;
        foreach (var e in Game.I.Enemies.ToArray())
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
            if (Flat(e, GlobalPosition) > _exsangRad + e.Radius) continue;
            e.Hurt(tick, DamageType.Blood, true);   // Base-scaled DoT → works on bosses, not %HP
            if (e.Dead) { ExsangPop(e.GlobalPosition); anyKill = true; }
        }
        if (anyKill) { Heal(S.MaxHp); Ring(GlobalPosition, DamageTypes.Col(DamageType.Blood).Lerp(Colors.White, 0.4f), 3.5f, 0.4f); }
        // a soft pulsing aura ring each tick so the danger zone reads
        Ring(new Vector3(GlobalPosition.X, 0.06f, GlobalPosition.Z), DamageTypes.Col(DamageType.Blood), _exsangRad, 0.4f);
    }
    // a foe dies inside the aura → a small blood nova damages nearby others (chains the harvest)
    private void ExsangPop(Vector3 at)
    {
        float r = 4.5f * S.SpellArea, dmg = _exsangDps * 1.5f;
        foreach (var e in Game.I.Enemies.ToArray())
            if (e != null && !e.Dead && GodotObject.IsInstanceValid(e) && Flat(e, at) < r + e.Radius)
                e.Hurt(dmg, DamageType.Blood, true);
        var bc = DamageTypes.Col(DamageType.Blood);
        Game.I.VfxRing(at + Vector3.Up * 0.5f, bc, r, 0.3f);
        Game.I.SpawnBloodMist(at, r * 0.7f);
        Game.I.NetMgr?.BroadcastVfx(0, at + Vector3.Up * 0.5f, Vector3.Up, r, 0.3f, bc);
    }
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
    public float XpNext = 26f;
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
    public float FreezeRate = 2.0f;   // (BUFF 1.6→2.0) stacks/sec the beam builds — snappier so the freeze→shatter loop keeps up (card-scalable later)
    public float FrostDurBonus = 0f;     // (NEW) Lingering Frost: +frozen seconds
    public float FreezeThreshMul = 1f;   // (NEW) Brittle: lower freeze threshold
    public float ShatterPowerMul = 1f;   // (NEW) Shatterpoint: stronger shatter
    public float ShatterFreezeStacks = 1f;   // (NEW) shatter seeds this many flat freeze stacks into each hit foe (card-scalable)
    public bool ShatterCascade = false;  // (NEW legendary) shatters chain to nearby frozen foes
    public bool DeepWinter = false;      // (NEW legendary) frozen foes chill neighbours into freezing
    public bool GlacialImpaler = false;  // (NEW legendary) spear pierces everything + shatters frozen at any charge
    private const float BeamLen = 42f;
    private float _beamLen = BeamLen;   // BeamLen × SpellRange, captured at StartBeam (a const can't read instance stats)
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
    private float Base() => 10f * S.Atk * UltDmgMul * DamageMul * FrenzyMul() * JetstreamMul() * HolyEmpowerMul();   // JetstreamMul = Gale airborne bonus (NEW); HolyEmpowerMul = Divine Hallowed buff (OVERHAUL)
    public float ShatterBurstDmg() => Base() * 7.0f * ComboMul();   // (NEW) player-scaled flat shatter burst — her signature single-target snipe, tuned to edge out the Forsaken's crush (~68 → shatter ~73+ at full HP)
    public float DamageMul = 1f;   // per-witch base-damage scalar (Divine trades damage for sustain)
    public float HolyEmpowerT = 0f, HolyEmpowerAmt = 0f;   // (OVERHAUL) Consecrated Ground Hallowed: a timed Holy damage buff
    private float HolyEmpowerMul() => HolyEmpowerT > 0f ? 1f + HolyEmpowerAmt : 1f;
    private float _thornBurstRad = 5f, _thornRoot = 0f, _thornResistT = 0f, _thornResistAmt = 0f;   // (OVERHAUL) Thorn Skin stacks cached for the armor-break burst
    private int _beamOverload = 0; private float _beamHeld = 0f; private readonly System.Collections.Generic.List<SegBeam> _prismSegs = new();   // (OVERHAUL) Spelllance Overload crit-ramp + Prism extra beams
    public float ComboMul() => 1f + Mathf.Min(Mathf.Max(Combo - 1, 0), S.ComboCap) * S.ComboPow;
    public float ComboFrac() => Mathf.Clamp((S.ComboWindow - (Now - ComboT)) / S.ComboWindow, 0, 1);
    public bool ComboLive => Combo > 1 && (Now - ComboT) <= S.ComboWindow;
    public Vector3 AimDir() => (-_cam.GlobalTransform.Basis.Z).Normalized();
    public Vector3 EyePos => _cam.GlobalPosition;
    public Camera3D Cam => _cam;

    private Camera3D _tpCam;
    private WitchModel _tpPuppet;
    private bool _tp = false;
    private float _tpYaw, _tpPitch = 0.15f, _tpDist = 8f;
    private Vector3 _tpFocus, _prevTpPos;
    private bool _tpCastHeld;   // edge-detect for the C-key cast preview in third-person
    private bool _castWasHeld;  // co-op cast-broadcast edge detect
    private float _castRepulse; // co-op cast-broadcast re-pulse timer (keeps the mask alive while cast is held)
    private bool _chargeWasHeld; // edge detect for the charge-release animation trigger
    private bool _fpAuthored;   // (DEV prototype) first-person view uses the AUTHORED witch arms/body instead of primitives
    private WitchModel _fpPuppet;
    private float _fpEyeY = 3.1f;   // eye/camera height for the authored FP view (tunable via `fp <eye>`)
    private float _fpYaw = 0.54f;   // body twist (~31°) to bring the arms toward center (tunable via `fp <eye> <twistDeg>`)
    private float _fpNear = 0.8f;   // camera near-clip (m) — slices off the chest/head splat close to the lens, keeps the arms
    private float _fpNearSaved = 0.05f;   // original camera near, restored on exit
    private float _fpFwd = 0f;      // forward(+)/back(-) offset of the authored body relative to the camera
    public bool FirstPersonAuthored => _fpAuthored;
    // (DEV) playable THIRD-PERSON prototype: authored witch shown full-body, follow-cam behind, she faces your aim + strafes.
    private bool _tp3;
    private WitchModel _tp3Puppet;
    private float _tp3H = 4.0f, _tp3D = 2.8f, _tp3Lat = 1.2f;   // OVER-THE-SHOULDER cam: height, distance behind, lateral offset
    public bool ThirdPersonPlay => _tp3;
    private float _leftFire;    // 0→1 left-arm thrust blend (ramps up while LMB held, down on release)
    private bool _castIK;       // (DEV EXPERIMENT) drive the cast arms with IK instead of the clip poses
    private float _releaseIK;   // 0→1 decaying push-out blend fired on charge release
    private Vector3 _ikLeftTarget, _ikChargeTarget;   // where the IK hands reach — used as the fire muzzle when IK is on
    private float _jumpBlend, _jumpElapsed, _jumpSeek, _landT; private bool _jumpRun, _jumpMir, _wasAir;   // jump blend/scrub/land + takeoff variant
    private const float LandDur = 0.28f;   // landing (knee-bend absorb) duration
    public string ToggleCastIK()
    {
        if (!_tp3 || _tp3Puppet == null || !GodotObject.IsInstanceValid(_tp3Puppet)) return "enter tp3 first ('tp3').";
        _castIK = !_castIK;
        if (_castIK) _tp3Puppet.SetupCastIK();
        return _castIK ? "cast IK ON — left hand points at the crosshair on fire (clip pose off)." : "cast IK OFF — back to the clip pose.";
    }
    // (DEV) anim viewer: browse the casting clips on the witch model with [ / ]
    private bool _animView;
    private WitchModel _animPuppet;
    private System.Collections.Generic.List<(string key, string name)> _animList;
    private int _animIdx;
    private Label3D _animLabel;
    private bool _animPrevHeld, _animNextHeld;
    private static readonly string[] ViewerAnims = {
        "witches/anims/magic/standing 1H cast spell 01.fbx",
        "witches/anims/magic/Standing 1H Magic Attack 01.fbx",
        "witches/anims/magic/Standing 1H Magic Attack 02.fbx",
        "witches/anims/magic/Standing 1H Magic Attack 03.fbx",
        "witches/anims/magic/Standing 2H Cast Spell 01.fbx",
        "witches/anims/magic/Standing 2H Magic Attack 01.fbx",
        "witches/anims/magic/Standing 2H Magic Attack 02.fbx",
        "witches/anims/magic/Standing 2H Magic Attack 03.fbx",
        "witches/anims/magic/Standing 2H Magic Attack 04.fbx",
        "witches/anims/magic/Standing 2H Magic Attack 05.fbx",
        "witches/anims/magic/Standing 2H Magic Area Attack 01.fbx",
        "witches/anims/magic/Standing 2H Magic Area Attack 02.fbx",
    };
    public bool AnimViewer => _animView;
    public bool ThirdPerson => _tp;
    // (DEV) third-person inspect toggle: drop a full witch puppet (authored mesh if available) into the WORLD (fixed, so
    // it doesn't spin with you) + a mouse-ORBIT camera around it; hides the FP body/hands. Mouse = orbit, wheel = zoom.
    public bool ToggleThirdPerson()
    {
        _tp = !_tp;
        if (_tp)
        {
            Vector3 feet = GlobalPosition;
            _tpPuppet = new WitchModel();
            _tpPuppet.Build(WitchIndex, false);        // non-FP → uses the authored mesh when one exists
            Game.I.AddChild(_tpPuppet);                // parent to the WORLD, not the player, so she stays put
            _tpPuppet.GlobalPosition = feet;
            _tpPuppet.Rotation = new Vector3(0, Rotation.Y + Mathf.Pi, 0);   // face toward the camera's starting side
            _tpFocus = feet + Vector3.Up * 2.4f;       // orbit around her mid-body
            _tpYaw = Rotation.Y; _tpPitch = 0.15f; _tpDist = 8f; _prevTpPos = GlobalPosition;
            _tpCam = new Camera3D { Fov = 58, Current = true };
            Game.I.AddChild(_tpCam);
            UpdateTpCam();
            if (_bodyModel != null && GodotObject.IsInstanceValid(_bodyModel)) _bodyModel.Visible = false;
            if (_armL != null) _armL.Visible = false;
            if (_armR != null) _armR.Visible = false;
        }
        else
        {
            if (_tpCam != null && GodotObject.IsInstanceValid(_tpCam)) _tpCam.QueueFree();
            _tpCam = null;
            if (_tpPuppet != null && GodotObject.IsInstanceValid(_tpPuppet)) _tpPuppet.QueueFree();
            _tpPuppet = null;
            if (_cam != null) _cam.Current = true;
            if (_bodyModel != null && GodotObject.IsInstanceValid(_bodyModel)) _bodyModel.Visible = true;
            if (_armL != null) _armL.Visible = true;
            if (_armR != null) _armR.Visible = true;
        }
        return _tp;
    }

    private SkelViz _tpSkelViz;
    // (DEV) toggle the pulsing skeleton overlay on the third-person inspect puppet. Returns a status/bone summary.
    public string ToggleTpSkeleton()
    {
        if (!_tp || _tpPuppet == null || !GodotObject.IsInstanceValid(_tpPuppet)) return "enter third-person first (type 'tp').";
        if (_tpSkelViz != null && GodotObject.IsInstanceValid(_tpSkelViz)) { _tpSkelViz.QueueFree(); _tpSkelViz = null; return "skeleton overlay OFF."; }
        var (summary, viz) = ModelAssets.ShowSkeleton(_tpPuppet);
        _tpSkelViz = viz;
        return summary;
    }

    // (DEV prototype) Toggle a UNIFIED first-person view: the authored Mixamo witch parented to the player, camera at her
    // eyes, head+hat culled, primitive FP hands hidden. You see your REAL authored arms — same mesh/anims as co-op allies —
    // driven by your movement + cast mask. Lets us judge how the authored arms read while aiming/casting.
    public string ToggleFirstPersonAuthored(float eyeY = -1f, float twistDeg = -999f, float near = -1f, float fwd = -999f)
    {
        bool hasArgs = eyeY > 0f || twistDeg > -900f || near > 0f || fwd > -900f;
        if (_fpAuthored && !hasArgs)   // already on + no args → toggle OFF
        {
            if (_fpPuppet != null && GodotObject.IsInstanceValid(_fpPuppet)) _fpPuppet.QueueFree();
            _fpPuppet = null; _fpAuthored = false;
            SetPrimitiveFpVisible(true);
            _cam.Near = _fpNearSaved;                        // restore normal near-clip
            return "first-person AUTHORED off — primitive hands restored.";
        }
        if (!_fpAuthored)   // turn ON
        {
            var puppet = new WitchModel();
            puppet.Build(WitchIndex, false);                 // non-FP branch → authored full body + anim tree
            if (!puppet.IsAuthored) { puppet.QueueFree(); return "no authored mesh for this witch (drop witch_<key>.fbx first)."; }
            _fpPuppet = puppet;
            AddChild(_fpPuppet);                             // parent to the player → yaws with your look, moves with you
            ModelAssets.HideForFirstPerson(_fpPuppet);       // collapse head/neck/hair + legs (keep arms + torso)
            SetPrimitiveFpVisible(false);
            _fpNearSaved = _cam.Near;
            _fpAuthored = true;
        }
        if (eyeY > 0f) _fpEyeY = eyeY;                       // live tuning (works while it's already on too)
        if (twistDeg > -900f) _fpYaw = Mathf.DegToRad(twistDeg);
        if (near > 0f) _fpNear = near;
        if (fwd > -900f) _fpFwd = fwd;
        if (_fpPuppet != null)
        {
            _fpPuppet.Rotation = new Vector3(0f, Mathf.Pi + _fpYaw, 0f);   // mesh faces +Z → +Pi to camera, +twist to center the arms
            _fpPuppet.Position = new Vector3(0f, 0f, -_fpFwd);            // +fwd = push the body/arms forward (camera looks -Z)
        }
        _cam.Near = _fpNear;                                 // clip the chest/head splat near the lens
        return $"first-person AUTHORED — eye {_fpEyeY:0.0}m, twist {Mathf.RadToDeg(_fpYaw):0}°, near {_fpNear:0.00}m, fwd {_fpFwd:0.00}m. 'fp' exits; 'fp <eye> <twistDeg> <near> <fwd>' tunes live.";
    }

    // (DEV) Playable third-person: full authored witch behind a follow-cam, facing your aim, strafing with WASD, casting with
    // the mask. Spawns stay ON — this is meant to be actually played. `tp3 <dist> <height>` tunes the camera live.
    public string ToggleThirdPersonPlay(float dist = -1f, float height = -1f, float lat = -999f)
    {
        bool hasArgs = dist > 0f || height > 0f || lat > -900f;
        if (_tp3 && !hasArgs)
        {
            if (_tp3Puppet != null && GodotObject.IsInstanceValid(_tp3Puppet)) _tp3Puppet.QueueFree();
            _tp3Puppet = null; _tp3 = false;
            SetPrimitiveFpVisible(true);
            return "third-person PLAY off — first-person restored.";
        }
        if (!_tp3)
        {
            var p = new WitchModel();
            p.Build(WitchIndex, false);
            if (!p.IsAuthored) { p.QueueFree(); return "no authored mesh for this witch (drop witch_<key>.fbx first)."; }
            _tp3Puppet = p;
            AddChild(_tp3Puppet);
            _tp3Puppet.Position = Vector3.Zero;
            _tp3Puppet.Rotation = new Vector3(0f, Mathf.Pi, 0f);   // mesh faces +Z → +Pi so she faces your aim (-Z), back to the cam
            // Ground AFTER the AnimationTree ticks (the idle clip repositions her hips vs rest) — a few frames later, then plant.
            GetTree().CreateTimer(0.15).Timeout += () => { if (GodotObject.IsInstanceValid(_tp3Puppet)) _tp3Puppet.GroundAuthored(); };
            SetPrimitiveFpVisible(false);
            _tp3 = true;
        }
        if (dist > 0f) _tp3D = dist;
        if (height > 0f) _tp3H = height;
        if (lat > -900f) _tp3Lat = lat;
        return $"third-person PLAY on — over-shoulder cam dist {_tp3D:0.0} height {_tp3H:0.0} lateral {_tp3Lat:0.0}. Move + cast. 'tp3' exits; 'tp3 <dist> <height> <lateral>' tunes.";
    }

    // Projectile spawn origin. In 3rd-person play: LEFT hand for the primary mote, BOTH hands (midpoint) for a charge release;
    // in all other modes it's the usual camera muzzle — so normal first-person play is byte-for-byte unchanged.
    private bool _muzzleCharge;   // set true only around the charge-release fire so FireOrigin uses both hands
    private Vector3 FireOrigin(Vector3 camFwd)
    {
        if (_castIK && _tp3 && _tp3Puppet != null && GodotObject.IsInstanceValid(_tp3Puppet))
        {   // IK: fire from the hand reach point computed FRESH this instant (no stale-frame lag during a jump)
            Vector3 camF = camFwd.Normalized(), camR = _cam.GlobalTransform.Basis.X.Normalized();
            Vector3 pos = _tp3Puppet.GlobalPosition;
            if (_muzzleCharge) return pos + Vector3.Up * 3.8f + camF * 2.4f;   // both-hand push point
            return pos + Vector3.Up * 3.9f - camR * 0.6f + camF * 1.8f;        // left reach point
        }
        if (_tp3 && _tp3Puppet != null && GodotObject.IsInstanceValid(_tp3Puppet) && TryHands(out var lh, out var rh))
        {
            var f = camFwd.Normalized();
            return (_muzzleCharge ? (lh + rh) * 0.5f : lh) + f * 0.4f;   // clear the mesh
        }
        return _cam.GlobalPosition + camFwd * 1.2f;   // FP: usual camera muzzle
    }

    private bool TryHands(out Vector3 left, out Vector3 right)
    {
        left = default; right = default; bool gotL = false, gotR = false;
        var skel = ModelAssets.FindSkeleton(_tp3Puppet);
        if (skel == null) return false;
        for (int i = 0; i < skel.GetBoneCount(); i++)
        {
            string n = (string)skel.GetBoneName(i);   // the hand itself; finger bones are ...Hand{Index,Thumb,...}1
            if (!gotL && n.EndsWith("LeftHand")) { left = (skel.GlobalTransform * skel.GetBoneGlobalPose(i)).Origin; gotL = true; }
            else if (!gotR && n.EndsWith("RightHand")) { right = (skel.GlobalTransform * skel.GetBoneGlobalPose(i)).Origin; gotR = true; }
        }
        if (gotL && !gotR) right = left; else if (gotR && !gotL) left = right;
        return gotL || gotR;
    }

    // (DEV) Anim viewer: drop the witch in front of the camera, disable her loco/cast tree, and play the casting clips one at
    // a time (looped, in place). [ = previous, ] = next; the clip name floats above her.
    public string ToggleAnimViewer()
    {
        if (_animView)
        {
            if (_animPuppet != null && GodotObject.IsInstanceValid(_animPuppet)) _animPuppet.QueueFree();
            _animPuppet = null; _animList = null; _animLabel = null; _animView = false;
            SetPrimitiveFpVisible(true);
            return "anim viewer OFF.";
        }
        var p = new WitchModel();
        p.Build(WitchIndex, false);
        if (!p.IsAuthored) { p.QueueFree(); return "no authored mesh for this witch."; }
        _animPuppet = p;
        AddChild(_animPuppet);
        _animPuppet.Position = Vector3.Zero;
        _animPuppet.Rotation = new Vector3(0f, 0f, 0f);   // face +Z toward the front camera so you see the cast
        _animPuppet.EnableTree(false);                    // stop the loco/cast tree — we drive clips directly
        _animList = ModelAssets.LoadViewerAnims(_animPuppet.Ap, ViewerAnims);
        SetPrimitiveFpVisible(false);
        _animLabel = new Label3D
        {
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, NoDepthTest = true, FontSize = 64, PixelSize = 0.004f,
            Modulate = new Color(1f, 0.93f, 0.45f), OutlineModulate = new Color(0f, 0f, 0f), OutlineSize = 14,
            Position = new Vector3(0f, 5.6f, 0f),
        };
        _animPuppet.AddChild(_animLabel);
        _animIdx = 0; _animView = true;
        PlayViewerAnim();
        return $"anim viewer ON ({_animList?.Count ?? 0} casting clips). '[' prev · ']' next · 'animview' exits.";
    }

    private void PlayViewerAnim()
    {
        if (_animPuppet == null || !GodotObject.IsInstanceValid(_animPuppet) || _animList == null || _animList.Count == 0) return;
        _animIdx = ((_animIdx % _animList.Count) + _animList.Count) % _animList.Count;
        var (key, name) = _animList[_animIdx];
        _animPuppet.Ap?.Play(key);
        if (_animLabel != null && GodotObject.IsInstanceValid(_animLabel)) _animLabel.Text = $"{_animIdx + 1}/{_animList.Count}  {name}";
    }

    // Hide the first-person (camera-anchored) charge visuals — they read as floating at the reticle in 3rd-person modes.
    // The generic charge sphere is KEPT in tp3 (repositioned to her hands); the others are hidden for now.
    private void HideFpChargeVisuals()
    {
        if (_thornCharge != null) _thornCharge.Visible = false;
        if (_frostNock != null) _frostNock.Visible = false;
        if (_voodoo != null && GodotObject.IsInstanceValid(_voodoo)) _voodoo.Visible = false;
        if (_arcaneOrb != null && GodotObject.IsInstanceValid(_arcaneOrb)) _arcaneOrb.Visible = false;
        if (_chargeOrb != null && !_tp3) _chargeOrb.Visible = false;   // in tp3 we reposition it to her hands instead
    }

    private void SetPrimitiveFpVisible(bool v)
    {
        if (_bodyModel != null && GodotObject.IsInstanceValid(_bodyModel)) _bodyModel.Visible = v;
        if (_armL != null) _armL.Visible = v;
        if (_armR != null) _armR.Visible = v;
    }

    private void UpdateTpCam()
    {
        if (_tpCam == null || !GodotObject.IsInstanceValid(_tpCam)) return;
        float cp = Mathf.Cos(_tpPitch), sp = Mathf.Sin(_tpPitch);
        var dir = new Vector3(cp * Mathf.Sin(_tpYaw), sp, cp * Mathf.Cos(_tpYaw));
        _tpCam.GlobalPosition = _tpFocus + dir * _tpDist;
        _tpCam.LookAt(_tpFocus, Vector3.Up);
    }

    public override void _Ready()
    {
        AddToGroup(Grove.Dev.Ai.AiObservable.Group);   // opt in to the DEV visual-test harness (inert unless a scenario runs)
        Hp = S.MaxHp; Mana = S.ManaMax; DashStock = S.DashCharges;
        MaxShield = S.MaxHp * S.ShieldPct; Shield = MaxShield;
        _cam = new Camera3D { Position = new Vector3(0, 2.6f, 0), Fov = 78, Current = true };
        AddChild(_cam);
        _witchLight = new OmniLight3D { Position = new Vector3(0, 2.3f, 0), OmniRange = 10f, LightColor = Palette.Lunar, LightEnergy = 0.6f };
        AddChild(_witchLight);   // personal fill-glow — recolored to the witch's OWN element in RetintHands (was hardcoded Lunar violet → tinted every witch purple)
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
        // personal fill-glow matches the witch's element (Divine → warm gold, not the old hardcoded Lunar violet). Softer for
        // authored-mesh witches so a strong colored light doesn't muddy their real textures.
        if (_witchLight != null)
        {
            bool authored = ModelAssets.Has(WitchModel.KeyFor(WitchIndex));
            _witchLight.LightColor = pc;
            _witchLight.LightEnergy = authored ? 0.28f : 0.6f;
        }
    }
    private OmniLight3D _witchLight;

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
        hand.MaterialOverride = Game.ToonEmissive(skin, 0.3f, 0.02f);   // (PAINTERLY) calmer hand glow — stop the pure-white bloom crescents
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
        if (_tp && e is InputEventMouseButton tw && tw.Pressed)   // (DEV inspect) wheel = zoom the orbit
        {
            if (tw.ButtonIndex == MouseButton.WheelUp) { _tpDist = Mathf.Max(3f, _tpDist - 0.7f); UpdateTpCam(); return; }
            if (tw.ButtonIndex == MouseButton.WheelDown) { _tpDist = Mathf.Min(22f, _tpDist + 0.7f); UpdateTpCam(); return; }
        }
        if (e is InputEventMouseMotion mm && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            if (_tp)   // (DEV inspect) mouse orbits the puppet instead of turning the player
            {
                _tpYaw -= mm.Relative.X * MouseSens;
                _tpPitch = Mathf.Clamp(_tpPitch - mm.Relative.Y * MouseSens, -1.2f, 1.3f);
                UpdateTpCam();
                return;
            }
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
        // gamepad: R3 = quick-turn 180°; hold LB + a face button = fire spell slot 1-5
        if (e is InputEventJoypadButton jb && jb.Pressed && Game.I != null && Game.I.CanControlLocal() && Game.I.State == GameState.Playing)
        {
            if (jb.ButtonIndex == JoyButton.RightStick) { _turn180 = Mathf.Pi; return; }   // whip around to face behind
            if (Input.IsJoyButtonPressed(jb.Device, JoyButton.LeftShoulder))
            {
                int slot = jb.ButtonIndex switch { JoyButton.X => 0, JoyButton.Y => 1, JoyButton.B => 2, JoyButton.A => 3, JoyButton.RightShoulder => 4, _ => -1 };
                if (slot >= 0) { FireFinisher(slot); return; }
            }
        }
    }

    // right-stick look: radial deadzone + squared response curve + light exponential smoothing, all frame-rate independent.
    // Also drives the R3 quick-turn. Runs every frame the player is controllable (incl. during flight/ascend ults).
    private void UpdatePadLook(float dt)
    {
        if (_turn180 > 0f) { float step = Mathf.Min(_turn180, 22f * dt); RotateY(step); _turn180 -= step; }   // snap-turn at a fixed fast rate — reads instant, no nausea
        if (!Game.PadActive) return;
        var raw = new Vector2(Input.GetJoyAxis(0, JoyAxis.RightX), Input.GetJoyAxis(0, JoyAxis.RightY));
        float mag = raw.Length();
        Vector2 target = Vector2.Zero;
        if (mag > PadLookDead)
        {
            float t = Mathf.Clamp((mag - PadLookDead) / (1f - PadLookDead), 0f, 1f);
            target = (raw / mag) * (t * t);   // squared curve: precise near center, fast at the edge
        }
        _padLook = _padLook.Lerp(target, 1f - Mathf.Exp(-dt * 32f));   // fast, light smoothing — kills stick jitter with negligible latency
        if (_padLook.LengthSquared() < 1e-8f) return;
        float rate = PadLookSens * PadSensMul * dt;
        RotateY(-_padLook.X * rate);
        _pitch = Mathf.Clamp(_pitch - _padLook.Y * rate, -1.4f, 1.4f);
        _cam.Rotation = new Vector3(_pitch, 0, 0);
    }

    public override void _Process(double delta)
    {
        CrashLogger.Mark("Player._Process");   // breadcrumb for freeze localization
        float dt = (float)delta;
        if (Game.I != null && Game.I.State == GameState.ColliderEdit) { EditorFreeFly(dt); return; }   // (DEV) collider-editor free-fly cam
        // menus pause the world (WorldRunning=false) → freeze the authored anim trees to a still frame (this must run even
        // while paused, so it's before the CanControlLocal early-return below)
        bool worldRunning = Game.I == null || Game.I.WorldRunning;
        if (_tp3Puppet != null && GodotObject.IsInstanceValid(_tp3Puppet)) _tp3Puppet.EnableTree(worldRunning);
        if (_fpPuppet != null && GodotObject.IsInstanceValid(_fpPuppet)) _fpPuppet.EnableTree(worldRunning);
        if (_tp && _tpPuppet != null && GodotObject.IsInstanceValid(_tpPuppet))   // (DEV inspect) drive the puppet's walk/idle from your movement
        {
            Vector3 d = GlobalPosition - _prevTpPos; d.Y = 0f;
            // pass world move dir so the puppet (fixed facing) plays the matching strafe clip — walk any direction to preview
            _tpPuppet.Animate(dt, Mathf.Clamp(d.Length() / Mathf.Max(dt, 1e-4f) / 9f, 0f, 1f), false, d);
            _prevTpPos = GlobalPosition;
            bool castKey = Input.IsPhysicalKeyPressed(Key.C);                        // press C to preview the upper-body cast mask
            if (castKey && !_tpCastHeld) _tpPuppet.Cast();
            _tpCastHeld = castKey;
        }
        if (_iframe > 0) _iframe -= dt;   // (FIX) always decay iframes — opening the console or being grabbed must NOT freeze immunity
        UpdateMenuImmunity(dt);           // (MP) elemental bubble while she's in a menu; on close it bursts, shoving foes off her
        if (Downed && !_grounded && Game.I != null && Game.I.WorldRunning) UpdateVertical(dt, false);   // (NEW) a body downed mid-air keeps falling to the ground instead of freezing there (fall damage is a no-op while Downed)
        if (Game.I != null && !Game.I.CanControlLocal()) { if (_frostSeg != null) EndFrostBeam(); if (_curseSeg != null) EndCurseBeam(); return; }
        if (GodMode) { Hp = Mathf.Min(S.MaxHp, Hp + S.MaxHp * 3f * dt); Mana = S.ManaMax; if (Ult != UltKind.None && !UltActive) UltCharge = 1f; if (Game.I != null) Game.I.BossTokens = Mathf.Max(Game.I.BossTokens, 99f); }   // (NEW) dev god mode: fast-regens + never dies, infinite mana, ult stays charged, and infinite boss tokens (free ult upgrade/swap via [U])
        if (GrabbedBy != 0)   // (NEW) held by a Taker: stunned + carried in its grasp
        {
            var t = Game.I.EnemyByNetId(GrabbedBy);
            if (t == null || t.Dead || MenuShielded || FullyImmune) GrabbedBy = 0;   // (MP) the bubble / Divinity / Faith Shield lets her slip a Taker's grasp
            else
            {
                StunT = Mathf.Max(StunT, 0.25f);
                if (Game.I.IsAuthority) GlobalPosition = t.GraspPos;   // host/solo owns the Taker → snap here; clients get pos from the host (ReceiveGrabPos)
                _vy = 0f;
                return;   // no other movement/action while held
            }
        }
        if (_fireCd > 0) _fireCd -= dt;
        // co-op: broadcast a cast pulse so allies' avatars play the upper-body cast overlay — on press + re-pulse while held
        bool casting = Input.IsActionPressed("cast");
        _castRepulse -= dt;
        if (casting && (!_castWasHeld || _castRepulse <= 0f))
        {
            Game.I.NetMgr?.BroadcastCast(); _castRepulse = 0.6f;
            if (_fpAuthored && _fpPuppet != null && GodotObject.IsInstanceValid(_fpPuppet)) _fpPuppet.Cast();   // your own FP arms cast
        }
        _castWasHeld = casting;
        if (ManaFlash > 0) ManaFlash -= dt;
        if (ProcFlash > 0) ProcFlash -= dt;
        if (HealFlash > 0) HealFlash -= dt;
        UpdateAura();
        FlushHealPopup(dt);
        if (Input.IsActionJustPressed("release_mouse")) Input.MouseMode = Input.MouseModeEnum.Visible;

        AnimateHands(dt);
        if (_tp3 || _fpAuthored || _animView) HideFpChargeVisuals();   // FP charge orb/doll/nock are screen-anchored — hide them in 3rd-person
        if (Game.I != null && Game.I.CanControlLocal()) DrawCurseTethers();   // (NEW) tethers persist + show on every machine (synced group)
        if (_bodyModel != null)
        {
            var mv = GlobalPosition - _prevBodyPos; mv.Y = 0f; _prevBodyPos = GlobalPosition;
            float sp = Mathf.Clamp(mv.Length() / Mathf.Max(dt, 1e-4f) / Mathf.Max(1f, S.Speed), 0f, 1f);
            _bodyModel.Animate(dt, sp, !_grounded);
            if (_fpAuthored && _fpPuppet != null && GodotObject.IsInstanceValid(_fpPuppet))
                _fpPuppet.Animate(dt, 0f, false);   // FP: freeze at idle (legs hidden) so the arms stay stable on screen; cast still fires
            if (_tp3 && _tp3Puppet != null && GodotObject.IsInstanceValid(_tp3Puppet))
            {
                _tp3Puppet.Animate(dt, _grounded ? sp : 0f, !_grounded, mv);   // airborne → freeze locomotion to idle (no run-cycle bob under the jump)
                if (_castIK)   // IK drives the arms (grounded AND airborne — the jump is legs-only, so arms stay free to cast)
                {
                    Vector3 camF = (-_cam.GlobalTransform.Basis.Z).Normalized(), camR = _cam.GlobalTransform.Basis.X.Normalized();
                    Vector3 pos = _tp3Puppet.GlobalPosition;
                    Vector3 shL = pos + Vector3.Up * 3.9f - camR * 0.6f, shR = pos + Vector3.Up * 3.9f + camR * 0.6f;
                    Vector3 magL = shL + Vector3.Down * 1.2f - camR * 1.0f, magR = shR + Vector3.Down * 1.2f + camR * 1.0f;   // elbow hints
                    _ikLeftTarget = shL + camF * 1.8f;                          // primary reach point → also the fire muzzle
                    _ikChargeTarget = pos + Vector3.Up * 3.8f + camF * 2.4f;    // release push point → both-hand muzzle
                    if (!Charging && _chargeWasHeld) _releaseIK = 1f;                 // trigger a forward push on charge release
                    _releaseIK = Mathf.MoveToward(_releaseIK, 0f, dt * 4.5f);
                    if (Charging)   // both hands gather to a close point in front, rising with charge
                    {
                        Vector3 gather = pos + Vector3.Up * 3.6f + camF * 0.9f;
                        _tp3Puppet.DriveLeftIK(gather - camR * 0.35f, magL, ChargeAmt);
                        _tp3Puppet.DriveRightIK(gather + camR * 0.35f, magR, ChargeAmt);
                    }
                    else if (_releaseIK > 0.02f)   // both hands thrust forward toward the crosshair
                    {
                        _tp3Puppet.DriveLeftIK(_ikChargeTarget - camR * 0.3f, magL, _releaseIK);
                        _tp3Puppet.DriveRightIK(_ikChargeTarget + camR * 0.3f, magR, _releaseIK);
                    }
                    else   // primary: left hand points at the crosshair, right arm relaxed
                    {
                        _tp3Puppet.DriveLeftIK(_ikLeftTarget, magL, _leftFire);
                        _tp3Puppet.DriveRightIK(Vector3.Zero, magR, 0f);
                    }
                }
                // drive the charge sphere directly (AnimateHands leaves it hidden in 3rd-person): show + grow it at the
                // midpoint of her raised/gathering hands while charging
                if (_chargeOrb != null)
                {
                    bool show = Charging && ChargeAmt > 0.02f;   // charge orb shows in the air too (arms are free to gather)
                    _chargeOrb.Visible = show;
                    if (show)
                    {
                        Vector3 camF = (-_cam.GlobalTransform.Basis.Z).Normalized();
                        // VISUAL only (does not touch _charge / damage): halved max size (~2.0 vs 4.0), eased (pow 1.6) so it
                        // keeps growing across the whole charge; sits in FRONT of the hands by its own radius.
                        float scale = 0.3f + Mathf.Pow(ChargeAmt, 1.6f) * 1.7f;
                        float radius = scale * 0.18f;   // sphere base radius
                        Vector3 handsMid = _tp3Puppet.GlobalPosition + Vector3.Up * 3.6f + camF * 0.9f;
                        _chargeOrb.GlobalPosition = handsMid + camF * radius;
                        _chargeOrb.Scale = Vector3.One * scale;
                    }
                }
            }
            // two-button caster: drive the authored puppet's charge gather-pose + fire the release on let-go
            var authored = _tp3 ? _tp3Puppet : (_fpAuthored ? _fpPuppet : null);
            if (authored != null && GodotObject.IsInstanceValid(authored))
            {
                authored.SetCharge(_castIK ? 0f : (Charging ? ChargeAmt : 0f));   // IK owns the arms when enabled; charge works in the air (legs-only jump)
                if (!_castIK && !Charging && _chargeWasHeld) authored.Release();
                // left fire: thrust the arm forward FAST on LMB-press, hold while held (rapid fire), recover on release
                float lfTarget = (Input.IsActionPressed("cast") && !Charging) ? 1f : 0f;
                _leftFire = Mathf.MoveToward(_leftFire, lfTarget, (lfTarget > _leftFire ? 16f : 7f) * dt);
                authored.SetLeftFire(_castIK ? 0f : _leftFire);   // IK owns the arm when enabled
                // jump: freeze a whole-body falling pose while airborne — running vs still picked at takeoff
                bool air = !_grounded;
                if (air && !_wasAir)   // capture at takeoff: run vs still, and strafe-right → mirrored variant for variety
                {
                    _jumpRun = sp > 0.5f;
                    Vector3 local = _tp3Puppet != null ? _tp3Puppet.GlobalTransform.Basis.Inverse() * (mv.LengthSquared() > 1e-4f ? mv.Normalized() : Vector3.Zero) : Vector3.Zero;
                    _jumpMir = local.X > 0f;
                    _jumpElapsed = 0f;
                }
                if (!air && _wasAir) _landT = LandDur;   // touchdown → play the land phase (knee-bend absorb)
                float jlen = authored.JumpClipLen;
                if (air)   // LAUNCH → HOLD FALL: scrub fast (4×) to ~80% of the clip, then hold
                {
                    _jumpElapsed += dt;
                    _jumpSeek = Mathf.Min(_jumpElapsed * 4f, 0.80f * jlen);
                    _jumpBlend = Mathf.MoveToward(_jumpBlend, 0.8f, 4.5f * dt);
                }
                else if (_landT > 0f)   // LAND: play the clip's tail (80→100%) and ease the legs back to locomotion
                {
                    _landT -= dt;
                    float lp = Mathf.Clamp(1f - _landT / LandDur, 0f, 1f);
                    _jumpSeek = Mathf.Lerp(0.80f, 1.0f, lp) * jlen;
                    _jumpBlend = 0.8f * (1f - lp * lp);
                }
                else _jumpBlend = Mathf.MoveToward(_jumpBlend, 0f, 11f * dt);
                authored.SetJump(_jumpBlend, _jumpRun, _jumpMir, _jumpSeek);
                _wasAir = air;
            }
            _chargeWasHeld = Charging;
        }

        if (_camKick > 0f) _camKick = Mathf.Max(0f, _camKick - dt * 4.5f);
        _cam.Fov = BaseFov + _camKick * 9f - (FrostWitch && Charging ? ChargeAmt * 34f : 0f);   // (NEW) Frost sniper: zoom in on the cursor as she draws
        // camera placement: anim-viewer front cam · 3rd-person follow-cam · authored-FP eye height · else normal FP eye
        if (_animView) _cam.Position = new Vector3(0f, 4.2f, 6.5f + _camKick * 0.18f);   // front view to see the cast
        else if (_tp3) _cam.Position = new Vector3(_tp3Lat, _tp3H, _tp3D + _camKick * 0.18f);   // over-the-shoulder: lens near her shoulder so fire reads from her
        else _cam.Position = new Vector3(0, _fpAuthored ? _fpEyeY : 2.6f, _camKick * 0.18f);
        if (_animView)   // browse casting clips with [ / ]
        {
            bool pv = Input.IsPhysicalKeyPressed(Key.Bracketleft), nx = Input.IsPhysicalKeyPressed(Key.Bracketright);
            if (pv && !_animPrevHeld) { _animIdx--; PlayViewerAnim(); }
            if (nx && !_animNextHeld) { _animIdx++; PlayViewerAnim(); }
            _animPrevHeld = pv; _animNextHeld = nx;
        }

        if (Game.I == null || !Game.I.CanControlLocal())
        {
            if (_beamSeg != null) { _beamSeg.Free(); _beamSeg = null; _beamLight = null; _beamT = 0; }
        foreach (var ps in _prismSegs) ps?.Free(); _prismSegs.Clear();
            return;
        }

        // shields & resonance
        MaxShield = S.MaxHp * S.ShieldPct;
        if (ShieldSuppress > 0f) ShieldSuppress = Mathf.Max(0f, ShieldSuppress - dt);
        if (_combatT < 1e6f) _combatT += dt;
        bool inMist = Game.I != null && Game.I.RitualVeil;
        // the shield still regenerates normally (the slow trickle) — but while the maze veil is up we suppress the
        // fast out-of-combat RUSH-back (50%/s), which otherwise outpaced the mist's 10/s so it could never kill you.
        bool outOfCombat = (_combatT >= 5f || (Game.I != null && Game.I.Enemies.Count == 0)) && !inMist;
        if (Shield < MaxShield && ShieldSuppress <= 0f)
        {
            if (outOfCombat)
                Shield = Mathf.Min(MaxShield, Shield + (MaxShield * 0.5f) * dt);   // out of combat: rushes back (~2s to full), ignoring the post-hit delay (NEW)
            else if (_shieldT > 0f) _shieldT -= dt;
            else Shield = Mathf.Min(MaxShield, Shield + S.ShieldRegen * dt);       // in combat: the slow trickle once the delay elapses
        }
        else if (_shieldT > 0f) _shieldT -= dt;


        UpdateWitchPassives(dt);   // (SURVIVAL PASSIVES) Frost Armor / Soul Siphon / Cinder Skin

        if (Combo > 0 && Now - ComboT > S.ComboWindow) { Combo = 0; _lastAct = ComboAct.None; }
        if (FreshT > 0f) { FreshT -= dt; if (FreshT <= 0f) FreshHit = false; }
        FireHeat = Mathf.Max(0f, FireHeat - dt * 1.2f);
        if (HurtT > 0f) HurtT -= dt;
        if (HurtFlash > 0f) HurtFlash = Mathf.Max(0f, HurtFlash - dt * 2.2f);
        if (ShieldBreakT > 0f) ShieldBreakT -= dt;
        if (ArmorBreakT > 0f) ArmorBreakT -= dt;
        {   // (NEW) low-health alarm: one-shot warning on crossing 20%, then a heartbeat that quickens as you near death
            float hpf = Hp / Mathf.Max(1f, S.MaxHp);
            if (!Downed && Hp > 0f && hpf <= 0.20f)
            {
                if (!_lowHpWarned) { _lowHpWarned = true; Game.I?.Sfx?.LowHealth(); Game.I?.Hud?.Banner("LOW HEALTH"); }
                _heartT -= dt;
                if (_heartT <= 0f) { _heartT = Mathf.Lerp(0.42f, 0.95f, Mathf.Clamp(hpf / 0.20f, 0f, 1f)); Game.I?.Sfx?.Heartbeat(); }
            }
            else if (hpf > 0.28f) _lowHpWarned = false;   // hysteresis: re-arm only after healing well clear
        }
        if (BlessedT > 0f) BlessedT -= dt;
        UpdateVenom(dt);   // (NEW) phalanx arrow-venom
        if (HolyEmpowerT > 0f) HolyEmpowerT -= dt;   // (OVERHAUL) Hallowed buff decay
        if (UltLingerT > 0f) UltLingerT -= dt;       // (NEW) ult-linger recharge-lock decay
        if (_eclipseBoomCd > 0f) _eclipseBoomCd -= dt;   // (ECLIPSE) shadow-nova throttle
        if (WindZoneT > 0f) WindZoneT -= dt;             // (WIND RUSH) ×3-speed wind-area timer (refreshed by the field while inside)
        if (!EclipseOn && _eclipseWasOn) { _eclipseWasOn = false; _bodyModel?.SetEclipse(false); }   // eclipse ended → restore her colours
        else if (EclipseOn) _eclipseWasOn = true;
        if (_thornResistT > 0f) _thornResistT -= dt;   // (OVERHAUL) Ironbark thorn-resist decay
        if (_noFall > 0f) _noFall -= dt;
        if (DashT > 0f) DashT -= dt;
        if (DmgDirT > 0f) DmgDirT -= dt;
        UpdateUlt(dt);
        if (_srcComboCd > 0f) _srcComboCd -= dt;
        if (_dotComboCd > 0f) _dotComboCd -= dt;   // (NEW) DoT-driven combo throttle
        foreach (var f in Fin) { if (f.NotReadyFlash > 0f) f.NotReadyFlash -= dt; }   // (OVERHAUL) finishers no longer expire — once armed they stay armed until fired (was: Window countdown that wiped Charge; only Blood Rush was exempt). In a swarm game the timer only punished repositioning; cadence is tuned via each finisher's Every instead.

        if (_beamT > 0) UpdateBeam(dt);

        if (StunT > 0f)
        {
            StunT -= dt;        // stunned: no movement, dashing, casting, or finishers
            ApplyKnockback(dt); // …but a shove still pushes you
            return;
        }

        UpdatePadLook(dt);   // gamepad right-stick aim + R3 quick-turn (before the flight/ult early-returns so it works while aloft)

        if (Divinity)
        {
            _divT -= dt; UltActiveT = _divT;
            _iframe = Mathf.Max(_iframe, 0.2f);                         // unkillable while ascended (FullyImmune blocks CC too)
            _bodyModel?.ShowWings(true); Floating = true;
            // (REWORK) rise once, then FREE FLIGHT: steer horizontally at flight speed, jump = ascend, dash-key = descend,
            // gravity off. She hovers when you're not pressing up/down, so you can hold station and rain motes.
            float targetY = _divBaseY + 12f;
            if (!_divRisen)
            {
                GlobalPosition = new Vector3(GlobalPosition.X, Mathf.MoveToward(GlobalPosition.Y, targetY, 16f * dt), GlobalPosition.Z);
                if (GlobalPosition.Y >= targetY - 0.3f) _divRisen = true;
            }
            else
            {
                Vector3 mv = InputDir() * S.Speed * 1.15f;
                float vy = 0f;
                if (Input.IsActionPressed("jump")) vy += 10f;
                if (Input.IsActionPressed("descend")) vy -= 10f;   // (FIX) descend = the standard flight-down bind (Ctrl), same as every other flight ult
                Vector3 np = ClampPos(GlobalPosition + (mv + Vector3.Up * vy) * dt);
                float floorY = Game.I.SurfaceHeight(np, np.Y) + 3.5f;   // never sink into the ground while flying
                np.Y = Mathf.Max(np.Y, floorY);
                GlobalPosition = np;
            }
            if (Input.IsActionPressed("cast") && _fireCd <= 0f) { FireDivinityMote(); _fireCd = Mathf.Max(0.2f, S.FireCd * 1.25f); }
            if (_divT <= 0f) { Divinity = false; UltActive = false; _divRisen = false; _iframe = 0.3f; _noFall = 3.0f; _divFalling = true; ClearUltAura(); }   // immortal + no fall damage until she lands
            return;
        }

        if (HurricaneActive) { UpdateHurricane(dt); return; }   // aloft, steering the storm (NEW)
        if (LifeDrainActive) { UpdateLifeDrain(dt); return; }   // aloft, draining — free flight, then the release burst (NEW)
        if (_galeDiving) { UpdateGaleDive(dt); return; }   // Gale air-slam: rocket to the aimed spot, then slam (NEW)
        if (_meteorAscend) { UpdateMeteorAscend(dt); return; }   // (NEW) Ember ult: suspended, aiming the landing zone
        if (_meteorDiving) { UpdateMeteorDive(dt); return; }     // (NEW) …then plummeting toward it (travel time, not an instant slam)
        if (_phoenix) { UpdatePhoenix(dt); return; }             // (legacy flight — no longer used by the reworked Phoenix, kept guarded by _phoenix which stays false)
        if (ArcaneAscendActive) { UpdateArcaneAscend(dt); return; }   // (NEW) Arcane ult: ascend aloft, rain chain-lightning with LMB
        if (_vineRising) { UpdateVineRise(dt); return; }         // (NEW) grappling up a jungle vine

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
            if (Input.IsActionJustPressed("dash") && DashStock > 0 && _snareT <= 0f && !_inWaterBody && !Game.PadSpellHeld()) StartDash();   // dash allowed mid-air, but not while rooted or wading/swimming — jump out of the water first (NEW). LB+B is spell slot 3, not a dash.
            Move(dt);
        }
        if (_snareT > 0f) _snareT -= dt;
        if (_slowT > 0f) _slowT -= dt;
        bool padSpell = Game.PadSpellHeld();   // holding LB casts a spell slot with the face buttons — A/B don't jump/glide then
        if (Input.IsActionJustPressed("jump") && _jumps > 0 && !padSpell) { _vy = JumpVel * S.JumpMul * (GaleWitch ? 1.1f : 1f) * OverchargeJumpMul * EclipseJumpMul; _jumps--; _grounded = false; if (Game.I.InWater(GlobalPosition, GlobalPosition.Y)) Game.I.WaterDisturb(GlobalPosition, 0.8f); Game.I.GlowFlowersNear(GlobalPosition, 2.4f); Game.I.PlayerSound(GlobalPosition, 0.5f); }   // Gale: +10% jump; splash off water + stir flowers on takeoff; quiet noise (NEW)
        Floating = !_grounded && _vy < 0f && Input.IsActionPressed("jump") && !padSpell;   // hold Space while falling → glide (Move() already gives air steering)
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
        if (!Downed && Game.I.CanControlLocal() && Input.IsPhysicalKeyPressed(Key.T) && _flareCd <= 0f) { FireFlare(); Game.I.RecallUnicorn(Game.I.LocalPeer); _flareCd = 2f; }   // (NEW) hold T → firework flare AND recall the arcane unicorn to you

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

    public DamageType WitchDamage => WitchIndex switch { 1 => DamageType.Holy, 2 => DamageType.Blood, 3 => DamageType.Nature, 4 => DamageType.Wind, 5 => DamageType.Frost, 6 => DamageType.Curse, 7 => DamageType.Ember, 8 => DamageType.Arcane, _ => DamageType.Lunar };

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
        ApplyKnockback(dt);         // (NEW) external shove decays here too — applies even while rooted
        if (_launchVel.LengthSquared() > 0.0001f)   // (GALE PAD) ballistic horizontal momentum through the arc; cleared the frame we touch down
        {
            if (_grounded) _launchVel = Vector3.Zero;
            else
            {
                Vector3 wish = InputDir();   // (NEW) air-steer: WASD curves the launch direction somewhat (dashing pivots it harder — see StartDash)
                if (wish.LengthSquared() > 0.01f)
                {
                    float sp = _launchVel.Length();
                    _launchVel = _launchVel.Lerp(wish.Normalized() * sp, Mathf.Clamp(dt * 1.3f, 0f, 1f));
                    float sp2 = _launchVel.Length(); if (sp2 > 0.001f) _launchVel *= sp / sp2;   // keep the launch speed, only turn its heading
                }
                GlobalPosition = ClampPos(GlobalPosition + _launchVel * dt);
            }
        }
        if (_snareT > 0f) return;   // rooted by a hexer
        Vector3 dir = InputDir();
        float spd = S.Speed * (_beamT > 0 ? 0.5f : 1f) * StormSpeedMul * OverchargeSpeedMul * WindBoonSpeedMul * EclipseSpeedMul * HurricaneSpeedMul * WindZoneMul * SlowMul * (_specter ? 3f : 1f);   // (REWORK) Specter: ×3 move while immaterial   // StormSpeedMul = Stormform; OverchargeSpeedMul = Arcane Overcharge; WindBoonSpeedMul = Eyewall; EclipseSpeedMul = ×2 eclipse; HurricaneSpeedMul = ×2.5 hurricane; WindZoneMul = ×3 wind-rush area; SlowMul = swarmer hits (NEW)
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
        // (ECLIPSE) her shift becomes an ARCANE BLINK — an instant teleport in the eclipse's black/white theme, not a slide
        if (EclipseOn)
        {
            float blink = Mathf.Max(9f, S.DashDist * 2.2f);
            Vector3 dest = ClampPos(GlobalPosition + dir * blink);
            var from = GlobalPosition;
            GlobalPosition = new Vector3(dest.X, GlobalPosition.Y, dest.Z);
            DashStock--; DashT = 1f; if (DashCdT <= 0) DashCdT = S.DashCd; _iframe = Mathf.Max(_iframe, 0.3f);
            EclipseBlinkVfx(from); EclipseBlinkVfx(GlobalPosition);
            Game.I.Sfx?.Cast(DamageType.Lunar);
            return;
        }
        _dashDir = dir; _dashT = DashDur; DashStock--;
        DashT = 1f;
        if (_launchVel.LengthSquared() > 0.01f) _launchVel = dir * _launchVel.Length();   // (GALE) dashing mid-launch pivots the arc hard toward the dash direction
        if (DashCdT <= 0) DashCdT = S.DashCd;
        _iframe = Mathf.Max(_iframe, 0.26f);
        if (GaleWitch) _galeGuard = 0.8f;   // Tailwind: brief damage reduction right after dashing (NEW)
    }

    // (DEV) free-fly camera for the collider editor: WASD relative to look, Space/Ctrl up/down, Shift = fast. Mouse-look already
    // runs while the mouse is captured (see _Input). The Player is a plain Node3D, so we just drive GlobalPosition directly.
    public void EditorLookPitch(float pitch) { _pitch = Mathf.Clamp(pitch, -1.4f, 1.4f); if (_cam != null) _cam.Rotation = new Vector3(_pitch, 0, 0); }
    private void EditorFreeFly(float dt)
    {
        if (_cam == null) return;
        float sp = Input.IsPhysicalKeyPressed(Key.Shift) ? 70f : 26f;
        Vector3 fwd = -_cam.GlobalTransform.Basis.Z;
        Vector3 right = _cam.GlobalTransform.Basis.X;
        Vector3 mv = Vector3.Zero;
        // physical WASD ONLY (not the move ACTIONS — those also bind the arrow keys, which the editor reserves for transforms)
        if (Input.IsPhysicalKeyPressed(Key.W)) mv += fwd;
        if (Input.IsPhysicalKeyPressed(Key.S)) mv -= fwd;
        if (Input.IsPhysicalKeyPressed(Key.D)) mv += right;
        if (Input.IsPhysicalKeyPressed(Key.A)) mv -= right;
        if (Input.IsPhysicalKeyPressed(Key.Space)) mv += Vector3.Up;
        if (Input.IsPhysicalKeyPressed(Key.Ctrl)) mv -= Vector3.Up;   // fly down (Q/E are collider Y in the editor)
        if (mv.LengthSquared() > 1e-4f) GlobalPosition += mv.Normalized() * sp * dt;
    }

    private Vector3 ClampPos(Vector3 p)
    {
        // (NEW) structures aren't infinitely tall — once you're flying clear above the local ground (arcing off a gale pad, an ult, etc.)
        // you sail over their footprints instead of snagging on the invisible column. ~13u clears the tallest grove trees. Disc + decks still clamp.
        bool aboveStructures = (p.Y - Game.I.SurfaceHeight(p, p.Y)) > 16f;   // fallback for blockers without a known top (maze etc.)
        if (!aboveStructures)
            foreach (var b in Game.I.Blockers)
            {
                if (b.Top > 0.01f && p.Y > b.Top + 0.5f) continue;   // flying clear above THIS structure's actual top → sail over it (no invisible column)
                var off = new Vector2(p.X - b.Pos.X, p.Z - b.Pos.Z);
                float dd = off.Length();
                if (dd < b.Radius + 1.0f) { float k = (b.Radius + 1.0f) / Mathf.Max(dd, 0.001f); p.X = b.Pos.X + off.X * k; p.Z = b.Pos.Z + off.Y * k; }
            }
        // raised platforms are solid from the sides (but walkable on top): push out if we're below the top
        foreach (var d in Game.I.Decks)
        {
            if (d.LowPad) continue;   // (NEW) a short dais — step straight up any side, no wall push-out (matches enemies)
            if (GlobalPosition.Y >= d.TopY - 0.6f) continue;   // standing on/near the top — let us walk to the edge (red/blue both let you leave the top)
            if (d.Boxed && GlobalPosition.Y < d.BotY - 0.6f) continue;   // (AUTHORED) a finite box — below it you pass underneath, no invisible column
            if (d.Floating && GlobalPosition.Y < d.TopY - 4.0f) continue;   // (NEW) sky island: only a thin solid rim below the top — open air below, so you can fly under it to catch a vine (no invisible column)
            const float pad = 0.9f;
            if (d.Cyl)   // (AUTHORED) cylinder footprint → radial push-out, like a Blocker
            {
                float rr = d.Half.X + pad;
                float ox = p.X - d.Center.X, oz = p.Z - d.Center.Z;
                float dist = Mathf.Sqrt(ox * ox + oz * oz);
                if (dist < rr) { float k = rr / Mathf.Max(dist, 0.001f); p.X = d.Center.X + ox * k; p.Z = d.Center.Z + oz * k; }
                continue;
            }
            float ex = d.Half.X + pad, ez = d.Half.Y + pad;
            float dx = p.X - d.Center.X, dz = p.Z - d.Center.Z;
            float lx = dx, lz = dz;   // (AUTHORED) into the box's local frame (world→local = Godot Y-rot transpose) so a yawed collider pushes out squarely
            if (d.Yaw != 0f) { float c = Mathf.Cos(d.Yaw), s = Mathf.Sin(d.Yaw); lx = dx * c - dz * s; lz = dx * s + dz * c; }
            if (Mathf.Abs(lx) < ex && Mathf.Abs(lz) < ez)
            {
                if (ex - Mathf.Abs(lx) < ez - Mathf.Abs(lz)) lx = Mathf.Sign(lx) * ex;
                else lz = Mathf.Sign(lz) * ez;
                if (d.Yaw != 0f) { float c = Mathf.Cos(d.Yaw), s = Mathf.Sin(d.Yaw); p.X = d.Center.X + lx * c + lz * s; p.Z = d.Center.Z - lx * s + lz * c; }   // local→world (Godot Y-rot)
                else { p.X = d.Center.X + lx; p.Z = d.Center.Z + lz; }
            }
        }
        // (FIX) the overworld cliff-wall boundary. NEGATIVE margin pushes the stop OUT past WorldRadius so you can walk right up
        // to the cliff rock. The mountains are buried 40u and cone-shaped, so their rock face AT GROUND LEVEL sits ~440-465 from
        // origin (well beyond WorldRadius=425); a WorldRadius-3 clamp stopped you ~20-40u short of the visible wall — the "invisible
        // wall too far in front of the mountains" bug. WorldRadius+10 puts the stop just inside the nearest ground-level rock face.
        p = Game.I.ClampToWorld(p, World.PlayerEdgeMargin);   // no-op in maze/expedition/sky
        return p;   // Y preserved — vertical handled by UpdateVertical
    }

    // ---- jumping & gravity (floaty) ----
    public const int MaxJumps = 2;
    public int JumpsMax => MaxJumps + (GaleWitch ? 1 : 0);   // Tailwind: the Gale witch gets a 3rd jump (NEW)
    private const float Gravity = -18f, FallHurtSpeed = 16f;
    // jump scaled to the authored witch height (all FitHeight-normalized to ~4.8m). ~11.5 → ~3.7m peak, proportional to her
    // size. If future witches use a different height H, scale by sqrt(H/4.8) so taller witches clear proportionally.
    private const float AuthoredHeight = 4.8f;
    private static readonly float JumpVel = 8.5f * Mathf.Sqrt(AuthoredHeight / 2.5f);   // 2.5 = the old procedural character height
    private const float GroundSnap = 0.6f;        // stick to slopes/steps so walking a hill never reads as "airborne" (NEW)
    private const float WaterWadeMin = 0.35f;     // water this deep+ counts as being in the water (slow, no dash, not airborne) (NEW)
    private const float WaterFloatDepth = 1.5f;   // deeper than this and we can't stand → float at the surface (NEW)
    private const float WaterNeck = 1.3f;         // when floating, feet ride this far below the surface (waterline ~chest/neck) (NEW)
    private bool _inWaterBody = false;            // currently wading or floating in water (NEW)
    private float _vy = 0f;
    public bool Floating = false;       // gliding (hold Space while falling) — drives wings + no fall damage
    public float _snareT = 0f;          // hexer root: can't move while > 0
    // (MP) inside her menu bubble she shrugs off ALL crowd control — roots, snares, slows, stuns, grabs, knockbacks
    public bool MenuShielded => Game.I != null && Game.I.MenuImmune;
    // (DIVINE) fully immune to EVERYTHING — damage, stun, root, slow, venom, grab. Divinity (ascended), standing inside a
    // Faith Shield dome, or the menu bubble. Guards every incoming-status entry point so boss abilities / hex / phalanx
    // arrows / roots can't slip through the shield or through Divinity.
    public bool InsideFaithShield => Game.I?.Shield != null && GodotObject.IsInstanceValid(Game.I.Shield)
        && new Vector2(GlobalPosition.X - Game.I.Shield.GlobalPosition.X, GlobalPosition.Z - Game.I.Shield.GlobalPosition.Z).Length() < Game.I.Shield.Radius;
    public bool FullyImmune => Divinity || MenuShielded || InsideFaithShield || BarkActive || _specter;   // (REWORK) Barkskin + Specter = full immunity (CC too, not just damage)
    public bool SpecterActive => _specter;   // (REWORK) LifeCurse immaterial transform: cannot act, immune to everything, 3x speed, heals
    public float SpecterActive01 => _specter ? 1f : 0f;   // (MP) synced to allies so they see the violet projection
    private bool _specter = false; private float _specterT = 0f, _specterNetT = 0f, _specterDotT = 0f; private Node3D _specterVfx;
    public void SnareMe(float dur) { if (!Downed && !MenuShielded && !FullyImmune) _snareT = Mathf.Max(_snareT, dur); }

    private Vector3 _knockVel;          // (NEW) decaying external shove (troll charge, blasts) — applies even while snared/stunned
    public void Knockback(Vector3 from, float power)
    {
        if (MenuShielded) return;   // (MP) the bubble eats external shoves too
        Vector3 d = GlobalPosition - from; d.Y = 0f;
        d = d.LengthSquared() > 0.001f ? d.Normalized() : new Vector3(-GlobalTransform.Basis.Z.X, 0f, -GlobalTransform.Basis.Z.Z).Normalized();
        _knockVel = d * power;
        HurtT = 0.7f; DmgDirWorld = from - GlobalPosition; DmgDirWorld.Y = 0f; DmgDirT = 1.2f;   // red flash + hit-direction arrow, so it reads as a hit
    }
    private void ApplyKnockback(float dt)
    {
        if (_knockVel.LengthSquared() < 0.0001f) return;
        GlobalPosition = ClampPos(GlobalPosition + _knockVel * dt);   // ClampPos → the shove still respects trees/walls
        _knockVel = _knockVel.MoveToward(Vector3.Zero, 30f * dt);
    }
    private float _slowT = 0f, _slowMul = 1f;
    private float SlowMul => _slowT > 0f ? _slowMul : 1f;
    public void SlowMe(float dur, float mul) { if (!Downed && !MenuShielded && !FullyImmune) { _slowMul = mul; _slowT = Mathf.Max(_slowT, dur); } }   // swarmer hits (NEW)

    // ===== (MP CONTINUE-AROUND) menu-immunity bubble =====
    // While she's in a menu (level-up / shop / equip-swap) and the world keeps running around her (MP), she's sealed in a
    // bubble of her own element — immune to everything (see the CC guards + Hurt). On close the bubble bursts, flinging every
    // foe within 10u outward (no damage) so she never gets released into a pile-on. Bubble is local; the burst is networked.
    private MenuBubble _menuBubble;
    private bool _wasMenuImmune = false;
    private const float MenuNovaRadius = 10f;
    private void UpdateMenuImmunity(float dt)
    {
        bool mi = Game.I != null && Game.I.MenuImmune;
        if (mi) { EnsureMenuBubble(); if (_menuBubble != null) _menuBubble.Visible = true; }
        else if (_menuBubble != null && _menuBubble.Visible) _menuBubble.Visible = false;
        if (_wasMenuImmune && !mi) MenuBubbleBurst();   // falling edge: she confirmed her pick → the bubble pops outward
        _wasMenuImmune = mi;
    }
    private void EnsureMenuBubble()
    {
        if (_menuBubble != null && GodotObject.IsInstanceValid(_menuBubble)) return;
        _menuBubble = new MenuBubble(); AddChild(_menuBubble); _menuBubble.Build(DamageTypes.Col(WitchDamage));   // seen from inside, around the local witch
    }
    private void MenuBubbleBurst()
    {
        if (_menuBubble != null && GodotObject.IsInstanceValid(_menuBubble)) _menuBubble.Visible = false;
        Vector3 pos = GlobalPosition; Color c = DamageTypes.Col(WitchDamage);
        var net = Game.I.NetMgr;
        if (net != null && net.Active) net.StormForce(pos, MenuNovaRadius, 3, 16f);   // routes client→host; mode 3 = fling outward + slight lift, NO damage
        else
            foreach (var e in Game.I.Enemies)
            {
                if (e == null || !GodotObject.IsInstanceValid(e) || e.Dead) continue;
                Vector3 fl = e.GlobalPosition - pos; fl.Y = 0f;
                if (fl.Length() <= MenuNovaRadius + e.Radius) e.Knockback(pos, 2.4f);
            }
        Game.I.VfxRing(pos, c, MenuNovaRadius, 0.5f);
        Game.I.NetMgr?.BroadcastVfx(0, pos, Vector3.Up, MenuNovaRadius, 0.5f, c);   // allies see the shove ring too
        Game.I.Sfx?.WindRushBy(pos);   // a witchy whoomph (self-networked)
    }
    private int _jumps = MaxJumps;
    private bool _grounded = true;
    public bool Grounded => _grounded;   // (NEW) standing on solid ground/deck (used by sky-island vines: grabbable only while airborne)
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

    // (GALE PAD) launched by a wind-pad: a 45° ballistic arc `dist` units in `dir`. Sets equal up + horizontal speed
    // (v = sqrt(dist*g/2) each, g=18) so the arc covers `dist`; horizontal momentum persists via _launchVel until landing.
    private Vector3 _launchVel = Vector3.Zero;
    public void GaleLaunch(Vector3 dir, float dist)
    {
        if (Downed || StunT > 0f || GrabbedBy != 0) return;
        dir.Y = 0f; if (dir.LengthSquared() < 0.001f) return; dir = dir.Normalized();
        float comp = Mathf.Sqrt(dist * 18f * 0.5f);   // 45°: equal vertical + horizontal components
        _vy = comp; _grounded = false; _jumps = JumpsMax; _launchVel = dir * comp;
        _noFall = Mathf.Max(_noFall, 4f);              // no self-inflicted fall damage on the boosted descent
        Game.I?.Sfx?.WindRushBy(GlobalPosition);
        Game.I?.GlowFlowersNear(GlobalPosition, 3f);
    }

    private void UpdateVertical(float dt, bool floating = false)
    {
        CrashLogger.Mark("Player.UpdateVertical");
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
        CrashLogger.Mark("Player.Combat");
        if (_beamT > 0) { Charging = false; ChargeAmt = 0; return; }
        if (HurricaneActive) { Charging = false; ChargeAmt = 0; return; }   // piloting the storm — no casting (NEW)
        if (_specter) { Charging = false; ChargeAmt = 0; return; }          // (REWORK) immaterial Specter cannot attack or damage

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
            if (_charge > 0.12f || ((ForsakenWitch || ArcaneWitch) && _charge > 0.02f))
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
                    if (Mana < 1f) { canFire = false; ResFail(); }       // (EXPERIMENT) a full mana to release a right-click
                    else { Mana -= 1f; _chargedRefund = true; }          // …and it returns only 0.5 on a hit → net −0.5 (mana is a real resource, not a faucet)
                }
                if (canFire)
                {
                    if (CrimsonWitch)
                    {
                        ConsumeBloodStacks(_charge);                 // banked Blood Stacks heal on release
                        if (_charge >= 0.95f) AddBloodStack(1f);     // a full-charge hold banks a stack for next time
                    }
                    _muzzleCharge = true;   // charge release fires from BOTH hands (3rd-person muzzle)
                    if (SecondaryType == DamageType.Holy) FireHolyRay(_charge);
                    else if (SecondaryType == DamageType.Blood) FireCrimsonTide(_charge);
                    else if (SecondaryType == DamageType.Frost) FireIcicleSpear(_charge);
                    else if (SecondaryType == DamageType.Curse) FireVoodooCrush(_charge);
                    else if (SecondaryType == DamageType.Arcane) FireArcaneChain(_charge);   // (NEW) jagged arcane chain-lightning through her marked foes
                    else FireBolt(_charge);
                    _muzzleCharge = false;
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
        else if (ArcaneWitch)   // (NEW) primary = a 3-round homing arcane-missile burst from the LEFT hand
        {
            if (_arcaneBurst > 0)   // mid-volley: fire the remaining missiles at a fixed cadence, then recover
            {
                _arcaneBurstT -= dt;
                if (_arcaneBurstT <= 0f)
                {
                    FireArcaneMissile(_arcaneBurst == 3);   // only the first shot of the volley builds combo
                    _arcaneBurst--;
                    _arcaneBurstT = 0.085f;
                    if (_arcaneBurst == 0) _fireCd = Mathf.Max(0.12f, S.FireCd) * 1.7f * StormFireMul * WindBoonFireMul;   // recovery between volleys
                }
            }
            else if (!_charging && Input.IsActionPressed("cast") && _fireCd <= 0f && !(UltActive && Ult == UltKind.Crescent))
                { _arcaneBurst = 3; SetArm("flick", 0.22f); }   // begin a volley — first missile fires next tick; hand flicks the magic out
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
                SpawnBolt(FireOrigin(camFwd), d, per, pierce, 0.16f, pur, dtype,
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
            Vector3 origin = FireOrigin(camFwd);
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
            SpawnBolt(FireOrigin(camFwd), camFwd.Normalized() * 30f, dmg * 1.15f,
                Mathf.Max(S.Pierce, 6) + CrescentPierceBonus, 1.1f * CrescentSizeMul, tint, dtype,
                normal: false, charged: true, combo: true, full: true,
                homing: false, life: 1.9f, fromCombo: false, horizontal: true, grow: 2.6f * CrescentSizeMul);
            Game.I.SpawnGroundSigil(GlobalPosition, 4.5f * S.SpellArea, baseCol);   // (NEW) lunar sigil flares under her — full charge only
            return;
        }

        SpawnBolt(FireOrigin(camFwd), camFwd.Normalized() * 50f, dmg,
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
                SpawnBolt(FireOrigin(camFwd), d, dmg * 0.8f, 0, 0.4f, baseCol, PrimaryType, true, false, false, false);
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

    // (NEW) crosshair-aimed crit-zone test so MELEE/close attacks can crit hit-spots (boss head/upper body, sentinel core)
    // the same way ranged bolts do — the vertical aim from AimHitOnEnemy is a real aim result, so pointing high crits the head.
    private bool AimCritZone(Vector3 o, Vector3 fwd, Enemy e)
    {
        if (e == null) return false;
        AimHitOnEnemy(o, fwd, e, out var hp, out _);
        return e.IsCritZone(hp);
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
            if (fwd.Dot(to / Mathf.Max(d, 0.001f)) < cosArc && !e.RayHitsBody(o, fwd, reach + e.Radius, e.Radius, out _)) continue;   // (FIX) in the arc, OR the crosshair ray passes through the body (tall foes: aim at the head)
            bool zone = AimCritZone(o, fwd, e); bool ecrit = crit || zone; float edmg = (zone && !crit) ? dmg * CritMult() : dmg;   // (NEW) aim at the boss head/core → this hit crits, even in melee
            e.Hurt(edmg, DamageType.Blood, true, ecrit);
            e.Knockback(GlobalPosition, 0.6f);
            OnHitDirect(e, e.Dead, edmg, DamageType.Blood);
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
        float dmg = dmgBase * 1.3f * (Airborne ? 1.35f : 1f) * (crit ? CritMult() : 1f);   // (BUFF) +30% base; (NEW) +35% while SHE'S airborne — converts her mobility into damage & works vs knockback-immune bosses (target-airborne ×1.45 below still stacks)
        var col = DamageTypes.Col(DamageType.Wind);
        foreach (var e in Game.I.Enemies.ToArray())
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
            var to = e.GlobalPosition - o; float d = to.Length();
            if (d > reach + e.Radius) continue;
            if (fwd.Dot(to / Mathf.Max(d, 0.001f)) < cosArc && !e.RayHitsBody(o, fwd, reach + e.Radius, e.Radius, out _)) continue;   // (FIX) in the arc, OR the crosshair ray passes through the body (tall foes: aim at the head)
            bool zone = AimCritZone(o, fwd, e); bool ecrit = crit || zone;   // (NEW) melee can crit the boss crit-zone by aiming at it
            float fdmg = e.Thrown ? dmg * 1.45f : dmg;   // (BUFF) extra lethal to airborne foes — rewards her fling→punch combo
            if (zone && !crit) fdmg *= CritMult();
            e.Hurt(fdmg, DamageType.Wind, true, ecrit);
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
        float dmg = Base() * (0.5f + c * 1.9f) * ComboMul() * (Airborne ? 1.35f : 1f) * (crit ? CritMult() : 1f);   // (BUFF 1.5→1.9 + airborne self-bonus) her signature slam now converts mobility into damage — jump→slam is a boss-usable combo
        var col = DamageTypes.Col(DamageType.Wind);
        Vector3 center = new Vector3(at.X, at.Y, at.Z);
        Vector3 co = _cam.GlobalPosition, cfwd = (-_cam.GlobalTransform.Basis.Z).Normalized();   // (NEW) crosshair, for boss crit-zone
        int hits = 0;
        foreach (var e in Game.I.Enemies.ToArray())
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
            if (Flat(e, center) > radius + e.Radius) continue;
            bool zone = AimCritZone(co, cfwd, e); bool ecrit = crit || zone; float edmg = (zone && !crit) ? dmg * CritMult() : dmg;   // (NEW) aim at the boss head/core → crit it
            e.Hurt(edmg, DamageType.Wind, true, ecrit);
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
            float twr = 3.5f * S.SpellArea;   // Cyclone doesn't self-scale
            cy.Init(this, new Vector3(center.X, 0f, center.Z), twr, 3f, Base() * 0.5f, false, false);
            Game.I.NetMgr?.BroadcastVfx(11, new Vector3(center.X, 0f, center.Z), Vector3.Up, twr, 3f, col);
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
        float reach = 12f * S.SpellArea * FlameReachMul * (PhoenixActive ? 1.7f : 1f);   // (NEW) reaches further; Phoenix Ascendant makes it huge; Cinderreach blessing extends it
        Game.I.SpawnFlameCone(o, dir, reach, DamageTypes.Col(DamageType.Ember));   // continuous flame VFX (local)
        _flameSndT -= dt; if (_flameSndT <= 0f) { _flameSndT = 0.25f; Game.I.Sfx?.Cast(DamageType.Ember); }
        _emberNetT -= dt; if (_emberNetT <= 0f) { _emberNetT = 0.12f; Game.I.NetMgr?.BroadcastVfx(66, o, dir, reach, 0f, DamageTypes.Col(DamageType.Ember)); }   // allies see the flame
        _flameTickT -= dt;
        if (_flameTickT <= 0f) { _flameTickT = Mathf.Max(0.08f, S.FireCd * 0.6f); FlameConeTick(o, dir, reach); }   // faster cast speed → faster ticks
        _flameDecalT -= dt;   // (NEW) leave scorch marks on the ground the flame licks over (networked → kind 25)
        if (_flameDecalT <= 0f)
        {
            _flameDecalT = 0.28f;
            var gpt = o + dir * (reach * (0.35f + GD.Randf() * 0.55f));
            float gy = Game.I.SurfaceHeight(gpt, gpt.Y);
            Game.I.SpawnBurnMark(new Vector3(gpt.X, gy + 0.03f, gpt.Z), DamageTypes.Col(DamageType.Ember), (1.4f + GD.Randf() * 0.6f) * S.SpellArea, 4f);
        }
        FireHeat = Mathf.Min(1f, FireHeat + 0.03f);
    }
    private float _emberNetT = 0f, _flameDecalT = 0f;
    private void EndFlameCone() { }   // nothing to tear down — the flame VFX is fire-and-forget puffs

    private void FlameConeTick(Vector3 o, Vector3 dir, float reach)
    {
        float pmul = PhoenixActive ? 1.6f : 1f;                // Phoenix Ascendant: harder-hitting flame
        float cosArc = PhoenixActive ? 0.78f : 0.85f;         // ...and a wider cone while ascended
        float dmg = Base() * 0.26f * pmul * ComboMul();        // small per-tick direct
        float burnPer = Base() * 0.085f * pmul * EmberBurnMul; // burn dps PER stack (scales with base damage); Kindling boosts it
        float bombFlat = Base() * 3.2f * LivingBombMul;        // Living Bomb blast on reaching the threshold; Detonator boosts it
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
    private float MeteorUltRadius() => (13f + UltTier * 2f) * S.SpellArea;   // (REWORK) bigger base impact
    private Vector3 MeteorAimPoint() { var a = GroundAim(); return new Vector3(a.X, Game.I.SurfaceHeight(a, a.Y), a.Z); }

    // ULT 1 — Meteor Descent: rise invulnerable, aim a landing zone (5s or confirm), then SLAM.
    private void UpdateMeteorAscend(float dt)
    {
        if (Downed || !Game.I.SimActive) { _meteorAscend = false; UltActive = false; HideEmberAimRing(); _iframe = 0.3f; _noFall = 2f; return; }
        _iframe = Mathf.Max(_iframe, 0.3f); Floating = false;
        float targetY = _meteorBaseY + 18f;
        GlobalPosition = new Vector3(GlobalPosition.X, Mathf.MoveToward(GlobalPosition.Y, targetY, 26f * dt), GlobalPosition.Z);
        _grounded = false; _vy = 0f;
        ShowEmberAimRing(MeteorAimPoint(), MeteorUltRadius());
        _meteorAscendT -= dt;
        UltActiveT = _meteorAscendT;   // (REWORK) feed the aim-window duration meter
        // (REWORK) meteors rain at random around her while she hangs aloft aiming
        if (_meteorRainLeft > 0)
        {
            _meteorRainT -= dt;
            if (_meteorRainT <= 0f)
            {
                _meteorRainLeft--; _meteorRainT = 0.5f + GD.Randf() * 0.4f;
                int t = UltTier;
                float a = GD.Randf() * Mathf.Tau, rr = (10f + GD.Randf() * 26f) * S.SpellArea;
                var rp = new Vector3(GlobalPosition.X + Mathf.Cos(a) * rr, 0f, GlobalPosition.Z + Mathf.Sin(a) * rr);
                float rgy = Game.I.SurfaceHeight(rp, 0f);
                float rMeteorR = (5f + t * 0.8f) * S.SpellArea, rDmg = Base() * (3.5f + t * 1.2f) * ComboMul();
                Game.I.SpawnEmberMeteor(new Vector3(rp.X, rgy, rp.Z), rMeteorR, rDmg, 2, Base() * 0.1f, Base() * 3.5f, this, 1.5f);
            }
        }
        bool canConfirm = _meteorAscendT < 4.7f;   // ignore the activation-frame [Q] press so she doesn't drop instantly
        if (_meteorAscendT <= 0f || (canConfirm && (Input.IsActionJustPressed("cast") || Input.IsActionJustPressed("ult")))) MeteorLand(MeteorAimPoint());
    }
    private void MeteorLand(Vector3 target)   // confirm: BEGIN the plummet (travel time). Impact/damage is deferred to MeteorImpact once she lands.
    {
        _meteorAscend = false;
        float gy0 = Game.I.SurfaceHeight(target, target.Y);
        _meteorDiveTarget = new Vector3(target.X, gy0, target.Z);
        _meteorDiving = true; _iframe = Mathf.Max(_iframe, 0.5f);
        Game.I.Sfx?.Whish(GlobalPosition);   // whoosh as she rockets down
    }

    private void UpdateMeteorDive(float dt)
    {
        if (Downed || !Game.I.SimActive) { _meteorDiving = false; UltActive = false; HideEmberAimRing(); _iframe = 0.3f; _noFall = 2f; return; }
        _iframe = Mathf.Max(_iframe, 0.3f);
        ShowEmberAimRing(_meteorDiveTarget, MeteorUltRadius());   // keep the landing telegraph up while she falls — a real dodge window for foes now
        Vector3 land0 = _meteorDiveTarget, cur = GlobalPosition, to = land0 - cur; float dist = to.Length();
        float step = 42f * dt;   // fast plummet, but it visibly TRAVELS now (was an instant teleport-slam)
        if (dist <= step + 0.25f || cur.Y <= land0.Y + 0.25f) { _meteorDiving = false; MeteorImpact(land0); return; }
        GlobalPosition = cur + to / Mathf.Max(dist, 0.001f) * step;
        _grounded = false; _vy = 0f;
        if (GD.Randf() < 0.6f) Game.I.SpawnEmberBurst(GlobalPosition, 1.3f);   // trailing fire streak down
    }

    private void MeteorImpact(Vector3 target)
    {
        HideEmberAimRing();
        float gy = Game.I.SurfaceHeight(target, target.Y);
        var land = new Vector3(target.X, gy, target.Z);
        GlobalPosition = ClampPos(land);
        _grounded = true; _vy = 0f; _jumps = JumpsMax; _iframe = 0.4f; _noFall = 0.5f;
        UltActive = false;

        int t = UltTier;
        float radiusBase = (13f + t * 2f) * (ModMeteorDesc ? 1.35f : 1f);   // (REWORK) bigger base; raw (matches MeteorUltRadius pre-SpellArea); the GroundField auto-scales this
        float radius = radiusBase * S.SpellArea;   // Extinction Event: wider; direct blast + satellites scale here
        float centerDmg = Base() * (10f + t * 3f) * ComboMul() * (ModMeteorDesc ? 1.3f : 1f);   // …and harder at the core
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
        float infernoDur = 13f + t + (ModMeteorDesc ? 3f : 0f);   // (REWORK) the living inferno lasts 13s base, longer with tiers/Extinction Event
        var field = new GroundField { Type = FieldType.Hex, DType = DamageType.Ember, Radius = radiusBase, Dur = infernoDur, Power = Base() * 0.6f,
            TintColor = col, BurnAdd = 1f, BurnPer = burnPer, BurnBomb = bombFlat, BurnOwner = Game.I.LocalPeer, Src = this };
        Game.I.AddChild(field); field.GlobalPosition = new Vector3(land.X, 0.05f, land.Z);   // the inferno keeps stacking burn for its whole duration
        UltLingerT = infernoDur; UltMax = infernoDur;   // (REWORK) the living-inferno duration meter (also gates recharge until it burns out)
        if (ModMeteorDesc)   // Extinction Event: satellite meteors rain around the impact
            for (int i = 0; i < 3; i++)
            {
                float a = i / 3f * Mathf.Tau + GD.Randf();
                var sat = new Vector3(land.X + Mathf.Cos(a) * radius * 0.8f, gy, land.Z + Mathf.Sin(a) * radius * 0.8f);
                Game.I.SpawnEmberMeteor(sat, radius * 0.4f, centerDmg * 0.35f, 3, burnPer, bombFlat, this, 1.3f);
            }

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
        float len = (13f + UltTier) * area * (ModWildfire ? 1.3f : 1f), halfW = 4f * area * (ModWildfire ? 1.4f : 1f);   // Firestorm: longer + wider
        _flameDashDir = dir; _flameDashDist = len; _flameDashDur = 0.24f; _flameDashT = _flameDashDur;
        _iframe = Mathf.Max(_iframe, 0.3f);
        Vector3 origin = new Vector3(GlobalPosition.X, Game.I.SurfaceHeight(GlobalPosition, GlobalPosition.Y), GlobalPosition.Z);
        var trail = new EmberTrail { Origin = origin, Dir = dir, Length = len, HalfW = halfW, Dur = 10f,
            BurnAdd = 1.2f, BurnPer = Base() * 0.11f, BurnBomb = Base() * 3.5f, HealPerSec = S.MaxHp * (ModWildfire ? 0.032f : 0.02f), Caster = this, OwnerPeer = Game.I.LocalPeer };
        Game.I.AddChild(trail);
        Game.I.NetMgr?.BroadcastEmberTrail(origin, dir, len, halfW, 10f);
        _rushDashLingerT = 10f; _rushDashLingerMax = 10f;   // HUD: the flame trail burns 10s
        CamKick(0.3f); Game.I.Sfx?.Cast(DamageType.Ember);
    }

    // (STORMFORM REWORK) a WIND RUSH — a maxed wind-rush dash that leaves a ×3-speed wind area + air mines along its path.
    private void WindRush()
    {
        if (_windCharges <= 0) return;
        _windCharges--;
        Vector3 dir = InputDir();
        if (dir == Vector3.Zero) { dir = -GlobalTransform.Basis.Z; dir.Y = 0; dir = dir.Normalized(); }
        float area = Mathf.Clamp(S.SpellArea, 1f, 2.5f);
        float dist = (14f + UltTier * 1.5f) * area * (ModStorm ? 1.4f : 1f);   // (MOD) longer dash
        _rushDir = dir; _rushDist = dist; _rushDur = 0.28f; _rushT = _rushDur; _rushWind = true; _windPuffCd = 0f;
        if (_inWaterBody) { _rushT = 0f; _rushDist = 0f; _rushWind = false; }
        _iframe = Mathf.Max(_iframe, _rushDur + 0.15f);
        var wcol = DamageTypes.Col(DamageType.Wind);
        float rad = 5f * area, dmg = Base() * (0.9f + UltTier * 0.2f);
        Game.I.NetMgr?.StormForce(GlobalPosition, rad, 2, dmg);    // damage the lane
        Game.I.NetMgr?.StormForce(GlobalPosition, rad, 3, 14f);    // fling foes aside (mass-scaled)
        int drops = 4;
        for (int i = 0; i < drops; i++)
        {
            var tp = GlobalPosition + dir * (dist * (i + 0.5f) / drops);
            float gy = Game.I.SurfaceHeight(tp, tp.Y);
            var gpos = new Vector3(tp.X, gy, tp.Z);
            // a WIND AREA — ×3 move speed to any player inside (no damage, no heal)
            var wf = new GroundField { Type = FieldType.Hex, Radius = 4.5f, Dur = 6f, Power = 0f, SpeedBoost = true, DType = DamageType.Wind, TintColor = wcol };
            Game.I.AddChild(wf); wf.GlobalPosition = new Vector3(tp.X, 0.04f, tp.Z);
            Game.I.NetMgr?.BroadcastWindZone(gpos, 4.5f, 6f);   // allies get the boost + see it
            // and an AIR MINE that catches foes in range
            var mine = new AirMine(); Game.I.AddChild(mine); mine.Init(this, gpos, Base() * 0.7f);
        }
        Game.I.SpawnWindBullet(GlobalPosition, dir, dist, _rushDur);
        Game.I.NetMgr?.BroadcastVfx(32, GlobalPosition, dir, dist, _rushDur, wcol);   // allies see the wind streak
        _rushDashLingerT = 6f; _rushDashLingerMax = 6f;   // HUD: the wind areas last 6s
        CamKick(0.35f); Game.I.Sfx?.WindRushBy(GlobalPosition);
    }

    // ULT 3 — Phoenix Ascendant: free flight + immolation aura; flamethrower fires here (Combat is skipped while flying).
    private void UpdatePhoenix(float dt)
    {
        if (Downed || !Game.I.SimActive) { EndPhoenix(); return; }
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
            float ar = (7f + UltTier) * S.SpellArea * (ModPhoenix ? 1.4f : 1f), ad = Base() * (0.8f + UltTier * 0.2f) * (ModPhoenix ? 1.4f : 1f);   // Immortal Phoenix: fiercer aura
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
    private void PhoenixRebirth()   // cheat-death (once per Phoenix; twice with Immortal Phoenix)
    {
        _phoenixRebirths--;
        _phoenixRebirth = _phoenixRebirths > 0;   // another life still banked?
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
        float burnPer = Base() * 0.085f * EmberBurnMul, bombFlat = Base() * 3.2f * LivingBombMul;
        int burnStacks = 3 + Mathf.RoundToInt(charge * 3f);          // instant burn toward Living Bomb
        Game.I.SpawnEmberMeteor(at, radius, dmg, burnStacks, burnPer, bombFlat, this);
        if (charge >= 0.95f) Game.I.SpawnGroundSigil(at, radius, DamageTypes.Col(DamageType.Ember));   // full-charge ground rune flourish (shared by every witch's charged right-click)
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
        SpawnBolt(FireOrigin(camFwd), camFwd * 32f, dmg, pierce, radius, tint, DamageType.Nature,
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
        float dmg = Base() * (0.4f + c * 1.3f) * ComboMul() * (crit ? CritMult() : 1f);   // (BUFF 0.95→1.3) the squishiest witch should out-burst up close — full Tide now Base×1.7
        float knock = 1.5f + c * 4f;
        var col = DamageTypes.Col(DamageType.Blood);
        bool killed = false;
        Vector3 o = _cam.GlobalPosition, fwd = (-_cam.GlobalTransform.Basis.Z).Normalized();   // (NEW) crosshair, for boss crit-zone
        foreach (var e in Game.I.Enemies.ToArray())
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
            if (Flat(e, GlobalPosition) > radius + e.Radius) continue;
            bool zone = AimCritZone(o, fwd, e); bool ecrit = crit || zone; float edmg = (zone && !crit) ? dmg * CritMult() : dmg;   // (NEW) aim at the boss head/core → crit it
            e.Hurt(edmg, DamageType.Blood, true, ecrit);
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
            if (e.RayHitsBody(eye, dir, len, 1.1f, out float et) && et < bestT) { bestT = et; hit = e; }   // (FIX) whole-body ray test — was a sphere at mid-body, missing tall foes' heads
        }
        float beamLen = hit != null ? bestT : len;
        // when not locked on a foe, terminate the beam on the first surface (pumpkin/tree/wall/ground) so it stops there AND marks it
        Vector3 fSurf = Vector3.Zero, fNorm = Vector3.Up; bool onSurface = false;
        if (hit == null && BeamSurfaceHit(eye, dir, len, out fSurf, out fNorm)) { beamLen = (fSurf - eye).Length(); onSurface = true; }
        if (hit != null)
        {
            hit.Hurt(Base() * 1.4f * dt * ComboMul(), DamageType.Frost, true);
            if (!hit.Frozen) hit.AddFreeze(dt * FreezeRate, FreezeThreshMul, FrostDurBonus);   // thread this witch's frost profile (best-of on the enemy)
            // (NEW) the beam bleeds cold onto the pack around its target — a light splash-freeze (no extra damage) so SWEEPING it
            // chills a CROWD, not just the one locked foe. Entry-level version of Deep Winter (which cascades a COMPLETED freeze).
            float splashR = 2.5f * S.SpellArea;
            foreach (var o in Game.I.Enemies.ToArray())
            {
                if (o == null || o == hit || o.Dead || o.Frozen || !GodotObject.IsInstanceValid(o)) continue;
                if (o.GlobalPosition.DistanceTo(hit.GlobalPosition) < splashR + o.Radius)
                    o.AddFreeze(dt * FreezeRate * 0.35f, FreezeThreshMul, FrostDurBonus, canRadiate: false);   // splash chill can't itself spread (keeps Deep Winter meaningful)
            }
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

    // ===== ARCANE WITCH =====
    // Primary: a 3-round arcane bolt burst from the LEFT hand (tight, gentle aim-assist; restores mana on hit). Landing all 3 on
    // ONE foe (3 consecutive same-target hits) MARKS it. Marks are persistent, capped at 4 (FIFO — the oldest drops when a 5th is
    // made), and cleared on death. Secondary (charged RMB): jagged arcane chain-lightning that bounces her → through each marked
    // foe (piercing in-between foes), scaling with charge. No marks → a single hitscan at the reticle. −0.5 mana / +1 on hit.
    // (OVERHAUL) marks are now UNCAPPED and TIME-LIMITED: every arcane bolt that hits marks its target, the mark ticks
    // down over ArcaneMarkDur seconds (a blessing extends it), and the charged chain zaps EVERY live mark.
    public float ArcaneMarkDur = 3f;         // base mark lifetime; "Lingering Sigils" blessings add to it
    private int _arcaneBurst = 0; private float _arcaneBurstT = 0f;
    private readonly System.Collections.Generic.List<Enemy> _arcaneMarks = new();
    private readonly System.Collections.Generic.List<float> _markExpire = new();   // index-aligned countdown per mark
    public int ArcaneMarkCount { get { int n = 0; foreach (var e in _arcaneMarks) if (e != null && !e.Dead && GodotObject.IsInstanceValid(e)) n++; return n; } }

    private void FireArcaneMissile(bool combo)
    {
        Game.I.PlayerSound(GlobalPosition, 0.6f);
        var basis = _cam.GlobalTransform.Basis;
        Vector3 camFwd = (-basis.Z).Normalized();
        Vector3 origin = (_handMeshL != null && GodotObject.IsInstanceValid(_handMeshL))
            ? _handMeshL.GlobalPosition
            : EyePos - basis.X * 0.3f - basis.Y * 0.42f - basis.Z * 0.5f;   // fallback: a left-hand-ish offset
        var tint = DamageTypes.Col(DamageType.Arcane);
        float dmg = Base() * 0.5f * ComboMul() * ArcanePowerMul;
        var tgt = AimTarget();   // gentle assist toward whatever's under the crosshair — tight, still responsive (not auto-pilot)
        Bolt m;
        if (tgt != null)
        {
            Vector3 dir = (tgt.GlobalPosition + Vector3.Up * tgt.Radius * 0.5f - origin).Normalized();
            m = SpawnBolt(origin, dir * 54f, dmg, 0, 0.32f, tint, DamageType.Arcane, normal: true, charged: false, combo: combo, full: false, homing: true);
            m.Target = tgt; m.SeekLockedOnly = true; m.HomeSpeed = 54f; m.Turn = 6f; m.HomeDelay = 0.04f;
        }
        else
            m = SpawnBolt(origin, camFwd * 56f, dmg, 0, 0.32f, tint, DamageType.Arcane, normal: true, charged: false, combo: combo, full: false, homing: false);
        m.ArcaneBurst = true;   // tracked in OnHit → 3-on-one-target marks it
        _kickL = 1;
        FireHeat = Mathf.Min(1f, FireHeat + 0.05f);
        Game.I.Sfx?.Cast(DamageType.Arcane);
    }

    private void PruneArcaneMarks()
    {
        for (int i = _arcaneMarks.Count - 1; i >= 0; i--)
        {
            var e = _arcaneMarks[i];
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) { _arcaneMarks.RemoveAt(i); _markExpire.RemoveAt(i); }
        }
    }

    // tick every mark's lifetime; a mark that runs out is cleared (unless Unstable Mind keeps it until death). Called each frame.
    private void UpdateArcaneMarks(float dt)
    {
        for (int i = _arcaneMarks.Count - 1; i >= 0; i--)
        {
            var e = _arcaneMarks[i];
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) { _arcaneMarks.RemoveAt(i); _markExpire.RemoveAt(i); continue; }
            if (ArcanePersistMarks) continue;                       // legendary: marks never time out
            _markExpire[i] -= dt;
            if (_markExpire[i] <= 0f) { e.SetArcaneMark(false); _arcaneMarks.RemoveAt(i); _markExpire.RemoveAt(i); }
        }
    }

    // Mark a foe (or REFRESH an existing mark's timer). Uncapped — every hit marks. Lasts ArcaneMarkDur seconds.
    private void MarkArcane(Enemy e)
    {
        if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) return;
        int idx = _arcaneMarks.IndexOf(e);
        if (idx >= 0) { _markExpire[idx] = ArcaneMarkDur; return; }   // already marked → just refresh the timer (quietly)
        _arcaneMarks.Add(e); _markExpire.Add(ArcaneMarkDur);
        e.SetArcaneMark(true);
        Game.I.MyStats.Highlight++;   // "Foes Marked"
    }

    // greedy nearest-neighbor path through the live marks, starting from `from` — the zigzag order the bolt bounces in.
    private System.Collections.Generic.List<Enemy> OrderedMarkChain(Vector3 from)
    {
        var pool = new System.Collections.Generic.List<Enemy>();
        foreach (var e in _arcaneMarks) if (e != null && !e.Dead && GodotObject.IsInstanceValid(e)) pool.Add(e);
        var ordered = new System.Collections.Generic.List<Enemy>();
        Vector3 cur = from;
        while (pool.Count > 0)
        {
            int best = 0; float bd = float.MaxValue;
            for (int i = 0; i < pool.Count; i++) { float d = pool[i].GlobalPosition.DistanceSquaredTo(cur); if (d < bd) { bd = d; best = i; } }
            var e = pool[best]; pool.RemoveAt(best); ordered.Add(e); cur = e.GlobalPosition;
        }
        return ordered;
    }

    private static float DistPointToSeg(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a; float t = ab.LengthSquared() > 1e-6f ? Mathf.Clamp((p - a).Dot(ab) / ab.LengthSquared(), 0f, 1f) : 0f;
        return (p - (a + ab * t)).Length();
    }

    // charge-release: jagged raw-arcane chain-lightning. Bounces her → through each marked foe (piercing in-between foes for
    // normal dmg @ base crit; marked foes take normal dmg @ 2x crit CHANCE). No marks → a single hitscan at the reticle, no bounce.
    private void FireArcaneChain(float charge)
    {
        PruneArcaneMarks();
        float chargeMul = 1f + charge * (S.MaxCharge * 1.6f - 1f);   // power scales with charge (same ramp as a charged bolt)
        float dmg = Base() * 1.4f * chargeMul * ComboMul() * ArcanePowerMul;
        var col = DamageTypes.Col(DamageType.Arcane);
        if (charge >= 0.95f) Game.I.SpawnGroundSigil(GlobalPosition, 4.5f * S.SpellArea, col);   // full-charge ground rune flourish (shared by every witch's charged right-click)
        var basis = _cam.GlobalTransform.Basis;
        Vector3 start = (_handMeshR != null && GodotObject.IsInstanceValid(_handMeshR)) ? _handMeshR.GlobalPosition : EyePos + basis.X * 0.3f - basis.Y * 0.4f;
        var pts = new System.Collections.Generic.List<Vector3> { start };
        bool hitAny = false, modsFired = false;
        void Mods(Vector3 at) { if (!modsFired && charge >= 0.95f) { ApplyChargedMods(at); modsFired = true; } }   // charged-mod ability triggers on the FIRST foe hit only

        var chain = OrderedMarkChain(start);
        if (chain.Count > 0)
        {
            Vector3 prev = start;
            foreach (var target in chain)
            {
                var tp = target.GlobalPosition + Vector3.Up * target.Radius * 0.5f;
                foreach (var e in Game.I.Enemies.ToArray())   // pierce the foes the bolt zigzags THROUGH on the way to this mark
                {
                    if (e == null || e.Dead || e == target || _arcaneMarks.Contains(e) || !GodotObject.IsInstanceValid(e)) continue;
                    var ep = e.GlobalPosition + Vector3.Up * e.Radius * 0.5f;
                    if (DistPointToSeg(ep, prev, tp) > e.Radius + 0.8f) continue;
                    bool c1 = RollCrit(); float d1 = dmg; if (c1) d1 *= CritMult();
                    e.Hurt(d1, DamageType.Arcane, true, c1);   // in-between: normal dmg, base crit
                    OnHitDirect(e, e.Dead, d1, DamageType.Arcane, c1);   // every foe the chain touches builds combo + finisher charge + crit-heal
                    hitAny = true; Mods(ep);
                }
                bool crit = RollCrit(); if (!crit) crit = RollCrit();   // marked endpoint: 2x crit CHANCE (two rolls)
                float d = dmg; if (crit) d *= CritMult();
                target.Hurt(d, DamageType.Arcane, true, crit);
                OnHitDirect(target, target.Dead, d, DamageType.Arcane, crit);   // charged on-hit: combo, ult charge, the −0.5/+1 mana refund, crit-heal
                hitAny = true; Mods(tp);
                pts.Add(tp); prev = tp;
            }
            // the chain BURNS OFF the marks it bounced through — you have to re-mark for the next cast (unless Unstable Mind keeps them)
            if (!ArcanePersistMarks)
            {
                foreach (var e in _arcaneMarks) if (e != null && GodotObject.IsInstanceValid(e)) e.SetArcaneMark(false);
                _arcaneMarks.Clear(); _markExpire.Clear();
            }
        }
        else   // no marks → single hitscan at the reticle
        {
            var tgt = AimTarget();
            Vector3 endPt;
            if (tgt != null)
            {
                endPt = tgt.GlobalPosition + Vector3.Up * tgt.Radius * 0.5f;
                bool crit = RollCrit(); float d = dmg; if (crit) d *= CritMult();
                tgt.Hurt(d, DamageType.Arcane, true, crit);
                OnHitDirect(tgt, tgt.Dead, d, DamageType.Arcane, crit);
                hitAny = true; Mods(endPt);
            }
            else if (BeamSurfaceHit(EyePos, AimDir(), 40f * S.SpellRange, out var surf, out _)) endPt = surf;
            else endPt = EyePos + AimDir() * (40f * S.SpellRange);
            pts.Add(endPt);
        }
        if (!hitAny) _chargedRefund = false;   // whiffed: the 0.5 is spent, no refund (OnHitDirect refunds the +1 when it connects)

        Game.I.SpawnArcaneLightning(pts, charge);
        for (int i = 0; i + 1 < pts.Count; i++) { var d2 = pts[i + 1] - pts[i]; float l = d2.Length(); if (l > 0.1f) Game.I.NetMgr?.BroadcastVfx(78, pts[i], d2 / l, l, 0f, col); }
        Game.I.Sfx?.Release(DamageType.Arcane);
        _kickR = 1;
        FireHeat = Mathf.Min(1f, FireHeat + 0.12f);
    }

    // draw a faint curse link between each pair of tethered group members (local visual; refreshed each frame)
    private void DrawCurseTethers()
    {
        CrashLogger.Mark("Player.DrawCurseTethers");
        if (TotalTethered() < 2) { if (_tetherVis.Count > 0) ClearTetherVis(); return; }   // (PERF) nothing tethered → bail BEFORE allocating the ToArray + Dictionary + Lists every frame (the common case)
        ClearTetherVis();
        var groups = new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<Enemy>>();
        foreach (var e in Game.I.Enemies)   // (PERF) iterate directly — this loop only reads Enemies + spawns link nodes under Game.I, never mutates the list
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
        var b = SpawnBolt(FireOrigin(camFwd), camFwd * (46f + c * 20f), dmg,
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
        float dmg = Base() * (0.4f + c * 2.0f) * ComboMul() * (rayCrit ? CritMult() : 1f);   // (BUFF 0.3+0.7→0.4+2.0) the sweep was Base×1.0 at full — absurdly weak; now Base×2.4, still the lowest charged burst but solo-viable
        // NOTE: the mana refund is NOT predicted here — it fires only when the sweep's edge actually hits a foe
        // (HolyGround -> OnHitDirect -> _chargedRefund). No hit → no refund.

        // (NEW) Bless is a FULL-CHARGE reward, delivered by the SWEEP — not at cast. Only the caster is blessed
        // here (she always is, on a full charge). Allies/minions get blessed by HolyGround as the ray's leading
        // edge actually passes over them; standing in the lingering strip afterwards heals but does NOT bless.
        bool fullBless = c >= 0.95f;
        float blessDur = 2f * (S.MaxCharge / 3f) + BlessBonus;   // 2s at the default Overcharge stat; scales with Overcharge + Benediction
        if (fullBless) BlessedT = Mathf.Max(BlessedT, blessDur);   // the Divine caster always gets it on a full charge (no stacking)
        if (fullBless && DivineWitch)   // (REWORK) RADIANT SMITE — a real finisher that scales with her damage: a hard pillar on
        {                                //          the nearest foe that CHAINS to a second, each smite mending her.
            var order = new List<Enemy>();
            foreach (var e in Game.I.Enemies.ToArray())
            {
                if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
                if (Flat(e, GlobalPosition) < 40f * S.SpellRange) order.Add(e);
            }
            order.Sort((a, b) => Flat(a, GlobalPosition).CompareTo(Flat(b, GlobalPosition)));
            int hit = 0;
            for (int i = 0; i < order.Count && hit < 2; i++)
            {
                var tgt = order[i]; bool sc = RollCrit();
                float sd = Base() * (hit == 0 ? 2.2f : 1.2f) * ComboMul() * (sc ? CritMult() : 1f);   // scales with her damage; chain hits softer
                tgt.Hurt(sd, DamageType.Holy, true, sc); tgt.Slow(1.2f, 0.6f); Heal(S.MaxHp * 0.03f); hit++;
                Game.I.NetMgr?.BroadcastVfx(33, tgt.GlobalPosition, Vector3.Up, 6f, 0.5f, DamageTypes.Col(DamageType.Holy));
                Game.I.Sfx?.ModSmite(tgt.GlobalPosition);
            }
        }
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
        float radBase = 7f + UltTier * 0.8f;   // raw base — the ModDivinity GroundField auto-scales this by SpellArea
        float rad = radBase * S.SpellArea;     // the direct blast + VFX scale here
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
            var f = new GroundField { Type = FieldType.Heal, HealAllies = true, EnemyDmg = dmg * 0.18f, Radius = radBase * 0.6f, Dur = 3.5f, Power = S.MaxHp * 0.02f, DType = DamageType.Holy, TintColor = DamageTypes.Col(DamageType.Holy), FromCombo = true };
            Game.I.AddChild(f); f.GlobalPosition = new Vector3(at.X, 0.04f, at.Z);
        }
        Game.I.Sfx?.Release(DamageType.Holy);
    }

    // Radiant Halo: a holy nova around the witch — damages foes, heals her.
    private void FinHalo(float pow, int t)
    {
        int s0 = _curFin.Stat[0], s1 = _curFin.Stat[1], s2 = _curFin.Stat[2], e0 = _curFin.Evo[0], e1 = _curFin.Evo[1];
        float rad = (8f + 1.2f * s1) * S.SpellArea, dmg = Base() * 1.4f * (1f + 0.18f * s0);   // Stat① Radiance / Stat② Corona
        var col = DamageTypes.Col(DamageType.Holy);
        void Bloom(float r, float scl)
        {
            foreach (var e in Game.I.Enemies.ToArray())
                if (!e.Dead && Flat(e, GlobalPosition) < r) { e.Hurt(dmg * scl, DamageType.Holy, true); ComboFromSource(); }
            Game.I.DamageWorld(GlobalPosition, r, dmg * scl);   // (FIX) AoE breaks props too
            Ring(GlobalPosition, col, r, 0.5f);
        }
        Bloom(rad, 1f);
        for (int i = 1; i <= e0; i++) Bloom(rad * (1f + 0.35f * i), 0.6f);   // Epic Twin Halo: +1 concentric ring each stack
        Heal(S.MaxHp * (0.06f + 0.02f * s2));         // Stat③ Benediction: heal
        BlessedT = Mathf.Max(BlessedT, 4f + s2);       // Stat③ Benediction: Blessed duration
        if (e1 > 0)   // Leg Sanctuary: raise a protective shield (self; allies via net later)
        {
            float amt = S.MaxHp * (0.05f + 0.03f * e1);
            MaxShield = Mathf.Max(MaxShield, amt); Shield = Mathf.Max(Shield, amt);
        }
    }

    // Heaven's Lances: a fan of plunging holy lances ahead of the aim, each a small holy burst.
    private void FinLance(float pow, int t)
    {
        int s0 = _curFin.Stat[0], s1 = _curFin.Stat[1], s2 = _curFin.Stat[2], e0 = _curFin.Evo[0], e1 = _curFin.Evo[1];
        var at = GroundAim();
        int n = 3 + s1;                                    // Stat② Legion
        float dmg = Base() * 1.2f * (1f + 0.18f * s0);     // Stat① Judgement
        float lr = (3f + 0.5f * s2) * S.SpellArea;         // Stat③ Wide Aim
        var right = new Vector3(-(_cam.GlobalTransform.Basis.Z).Z, 0, (_cam.GlobalTransform.Basis.Z).X).Normalized();
        void Volley(float scl)
        {
            for (int i = 0; i < n; i++)
            {
                float off = (i - (n - 1) / 2f) * 2.6f;
                var spot = new Vector3(at.X + right.X * off, 0, at.Z + right.Z * off);
                foreach (var e in Game.I.Enemies.ToArray())
                    if (!e.Dead && Flat(e, spot) < lr) { e.Hurt(dmg * scl, DamageType.Holy, true); if (e0 > 0) e.Root(0.5f + 0.4f * e0); ComboFromSource(); }   // Epic Condemn: stun (root) struck foes
                Game.I.DamageWorld(spot, lr, dmg * scl);   // (FIX) AoE breaks props too
                Lance(spot);
            }
        }
        Volley(1f);
        for (int v = 1; v <= e1; v++)   // Leg Rain of Heaven: +1 delayed volley each stack
        {
            var tw = CreateTween(); tw.TweenInterval(0.28f * v);
            tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(this) && Game.I != null) Volley(0.7f); }));
        }
        Game.I.Sfx?.HolyLances(at);              // (NEW) sharp descending holy strike
    }

    // Blood Nova: a ring detonation around the witch — strong AoE + knockback. Scales with rarity (t) and pow.
    private void FinBloodNova(float pow, int t)
    {
        int s0 = _curFin.Stat[0], s1 = _curFin.Stat[1], s2 = _curFin.Stat[2], e0 = _curFin.Evo[0], e1 = _curFin.Evo[1];
        float rad = (9f + 1.2f * s1) * S.SpellArea;       // Stat② Spatter
        float dmg = Base() * 2.4f * (1f + 0.18f * s0);    // Stat① Rupture
        if (e1 > 0) { float missing = S.MaxHp > 0f ? Mathf.Clamp(1f - Hp / S.MaxHp, 0f, 1f) : 0f; dmg *= 1f + missing * 0.25f * e1; }   // Leg Sanguine Surge: scales with missing HP
        var col = DamageTypes.Col(DamageType.Blood);
        bool killed = false;
        foreach (var e in Game.I.Enemies.ToArray())
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
            if (Flat(e, GlobalPosition) > rad + e.Radius) continue;
            e.Hurt(dmg, DamageType.Blood, true);
            e.Knockback(GlobalPosition, 5f + 1.5f * s2);              // Stat③ Repel
            if (e0 > 0) e.Bleed(Base() * 0.1f * e0, 3f, false);       // Epic Hemoclast: the nova applies bleed
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
        int s0 = _curFin.Stat[0], s1 = _curFin.Stat[1], s2 = _curFin.Stat[2], e0 = _curFin.Evo[0], e1 = _curFin.Evo[1];
        _vy = 23f + 3f * s2; _grounded = false; _jumps = JumpsMax; _noFall = Mathf.Max(_noFall, 3.5f);   // Stat③ Ascend: self-launch height
        float rad = (6f + s1) * S.SpellArea;                    // Stat② Wide Gust
        float up = 16f + 3f * s0 + pow * 2f;                    // Stat① Squall: lift force
        Game.I.NetMgr?.StormForce(GlobalPosition, rad, 4, up, 1f, e1 > 0 ? 0.6f + 0.4f * e1 : 0f);   // lift foes up; base does NO fall damage — Leg Tempest UNLOCKS it (stack1 ≈ 1×, scaling to 2.2× at ×4)
        Game.I.MyStats.Flings += Game.I.CountFlungNear(GlobalPosition, rad);   // (NEW) tally enemies flung
        if (e0 > 0)   // Epic Cyclone Kick: the launch now deals damage
        {
            float kick = Base() * 0.3f * e0;
            foreach (var e in Game.I.Enemies.ToArray())
                if (!e.Dead && GodotObject.IsInstanceValid(e) && Flat(e, GlobalPosition) < rad + e.Radius) e.Hurt(kick, DamageType.Wind, true);
            Game.I.DamageWorld(GlobalPosition, rad, kick);
        }
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
        int s0 = _curFin.Stat[0], s1 = _curFin.Stat[1], s2 = _curFin.Stat[2], e0 = _curFin.Evo[0], e1 = _curFin.Evo[1];
        Vector3 fwd = -_cam.GlobalTransform.Basis.Z; fwd.Y = 0; fwd = fwd.Normalized();
        float dist = 13f + 3f * s1 + pow * 2f;                  // Stat② Slipstream
        float dmg = Base() * 0.35f * (1f + 0.18f * s0);         // Stat① Buffet
        float flingPow = 8f + 2.5f * s2 + pow * 2f;             // Stat③ Uplift
        float rad = (dist * 0.7f) * Mathf.Max(1f, S.SpellArea * 0.6f + 0.4f);
        _rushDur = 0.36f; _rushDist = dist; _rushDir = fwd; _rushT = _rushDur; _rushWind = true; _windPuffCd = 0f;   // longer, visibly-gusty glide (NEW)
        if (_inWaterBody) { _rushT = 0f; _rushDist = 0f; _rushWind = false; }   // no movement-dash combos while wading/swimming (NEW)
        _iframe = Mathf.Max(_iframe, _rushDur + 0.15f);
        Game.I.NetMgr?.StormForce(GlobalPosition, rad, 2, dmg);               // light damage in the lane
        Game.I.NetMgr?.StormForce(GlobalPosition, rad, 3, flingPow);          // fling foes aside/back (mass-scaled)
        Game.I.MyStats.Flings += Game.I.CountFlungNear(GlobalPosition, rad);   // (NEW) tally enemies flung
        // dash refund if an enemy is in the path (local check — works regardless of authority)
        bool hit = false;
        foreach (var e in Game.I.Enemies.ToArray())
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
            if (Flat(e, GlobalPosition) <= rad + e.Radius) { hit = true; break; }
        }
        if (hit && GD.Randf() < 0.5f + 0.15f * e0) DashStock = S.DashCharges;   // Epic Second Wind: refund chance grows → guaranteed
        if (e1 > 0)   // Leg Gale Force: leaves a damaging gust trail along the dash
        {
            int drops = 2 + e1;
            for (int i = 0; i < drops; i++)
            {
                var tp = GlobalPosition + fwd * (dist * (i + 0.5f) / drops);
                var gf = new GroundField { Type = FieldType.Hex, Radius = 2.5f, Dur = 1.6f + 0.2f * e1, Power = Base() * 0.1f * e1, DType = DamageType.Wind, TintColor = DamageTypes.Col(DamageType.Wind), FromCombo = true };
                Game.I.AddChild(gf); gf.GlobalPosition = new Vector3(tp.X, 0.04f, tp.Z);
            }
        }
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
        int s0 = _curFin.Stat[0], s1 = _curFin.Stat[1], s2 = _curFin.Stat[2], e0 = _curFin.Evo[0], e1 = _curFin.Evo[1];
        Vector3 fwd = (-_cam.GlobalTransform.Basis.Z).Normalized();   // (NEW) full aim incl. pitch → flies through the crosshair
        float dmg = Base() * 0.7f * (1f + 0.18f * s0);          // Stat① Edge
        float width = (4.5f + 0.8f * s1) * S.SpellArea;         // Stat② Broad Cut
        float range = (30f + 6f * s2) * S.SpellRange;           // Stat③ Far Throw: range
        float speed = 34f * (1f + 0.1f * s2) * S.ProjSpeed;     // Stat③ Far Throw: speed
        float pull = e1 > 0 ? 2.5f + 1.5f * e1 : 0f;           // Leg Vortex Edge: drags foes together in its wake
        int slices = 1 + e0;                                    // Epic Cross Cut: +1 slice / stack (fans into an X-cross)
        for (int i = 0; i < slices; i++)
        {
            float ang = i == 0 ? 0f : ((i + 1) / 2) * 0.7f * ((i % 2 == 1) ? -1f : 1f);
            var dir = fwd.Rotated(Vector3.Up, ang);
            var ws = new WindSlice { Dir = dir, Dmg = dmg, Width = width, Range = range, Speed = speed, Pull = pull };
            Game.I.AddChild(ws);
            ws.GlobalPosition = EyePos + dir * 1.4f;                  // (NEW) start at the eye so it tracks the reticle
        }
        Ring(GlobalPosition, col, 3f, 0.35f);
        CamKick(0.4f);
        Game.I.Sfx?.WindSlash(EyePos + fwd * 2f);                     // (NEW) sharp wind woosh
    }

    // ---- Frost finishers (NEW) ----

    // Ice Spikes: a cone of ice erupts ahead (~12u), damaging foes and flinging the small/medium ones up.
    private void FinIceSpike(float pow, int t, Color col)
    {
        int s0 = _curFin.Stat[0], s1 = _curFin.Stat[1], s2 = _curFin.Stat[2], e0 = _curFin.Evo[0], e1 = _curFin.Evo[1];
        Vector3 o = GlobalPosition, fwd = AimDir(); fwd.Y = 0; fwd = fwd.LengthSquared() > 0.001f ? fwd.Normalized() : Vector3.Forward;
        float reach = (12f + 1.5f * s1) * S.SpellArea, cosArc = Mathf.Max(0.35f, 0.5f - 0.035f * s1);   // Stat② Wide Cone: reach & angle
        float dmg = Base() * (1.6f * (1f + 0.18f * s0));   // Stat① Frostbite
        foreach (var e in Game.I.Enemies.ToArray())
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
            var to = e.GlobalPosition - o; float d = to.Length(); to.Y = 0f;
            if (d > reach + e.Radius || to.LengthSquared() < 0.001f) continue;
            if (fwd.Dot(to.Normalized()) < cosArc) continue;
            e.Hurt(dmg * (e.Frozen && e1 > 0 ? 1f + 0.25f * e1 : 1f), DamageType.Frost, true);   // Evo B Impaler: bonus vs frozen
            if (e0 > 0) e.AddFreeze(0.5f + 0.5f * e0, FreezeThreshMul, FrostDurBonus);            // Evo A Rime: freeze stacks
            if (e1 > 0 && e.Frozen) e.ShatterFreeze(true);                                        // Evo B Impaler: shatter frozen
            ComboFromSource();
        }
        Game.I.NetMgr?.StormForce(o + fwd * (reach * 0.5f), reach * 0.6f, 4, 13f + 3f * s2);   // Stat③ Upheaval: fling force
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
        int s0 = _curFin.Stat[0], s1 = _curFin.Stat[1], s2 = _curFin.Stat[2], e0 = _curFin.Evo[0], e1 = _curFin.Evo[1];
        Vector3 launchPos = GlobalPosition;
        Vector3 fwd = AimDir(); fwd.Y = 0; fwd = fwd.LengthSquared() > 0.001f ? fwd.Normalized() : Vector3.Forward;
        float up = 13f + pow * 2f, back = 8f + pow;
        _vy = up; _grounded = false; _jumps = JumpsMax; _noFall = Mathf.Max(_noFall, 2.6f);
        _rushDir = -fwd; _rushDist = back; _rushDur = 0.32f; _rushT = _rushDur; _rushWind = false;   // glide up-and-back
        float rad = (6f + 0.8f * s1) * S.SpellArea;                        // Stat② Fracture
        float dmg = Base() * (0.7f * (1f + 0.18f * s0));                   // Stat① Shard
        float slowF = Mathf.Max(0.25f, 0.45f - 0.05f * s2);               // Stat③ Numb: stronger slow
        float freeze = e0 > 0 ? 0.8f + 0.6f * e0 : 0f;                    // Evo A Flash Freeze
        SpawnVaultIcicle(launchPos, rad, dmg, col, false, slowF, freeze);
        for (int i = 1; i <= e1; i++)   // Evo B Avalanche: +1 icicle / stack
        {
            float a = i / (float)Mathf.Max(1, e1) * Mathf.Tau;
            SpawnVaultIcicle(launchPos + new Vector3(Mathf.Cos(a) * rad * 0.8f, 0, Mathf.Sin(a) * rad * 0.8f), rad * 0.7f, dmg * 0.7f, col, false, slowF, freeze);
        }
        Game.I.NetMgr?.BroadcastVfx(55, launchPos, Vector3.Up, rad, 0f, col);   // allies see the icicle + burst ring
        CamKick(0.5f);
        Game.I.Sfx?.Freeze(launchPos);
    }

    // ===== Frost Wall charged modifier (NEW) =====
    // Raise a persistent frost wall that BLOCKS enemies and shatters (for area damage) after a few seconds.
    // Live-wall limit grows with rarity (1 → 4); casting past the limit shatters the OLDEST wall early.
    private int _frostWallSeq = 0;
    private readonly System.Collections.Generic.List<FrostWall> _frostWalls = new();
    private void SpawnFrostWallMod(Vector3 pos, Modifier m)
    {
        int s0 = m.Stat[0], s1 = m.Stat[1], s2 = m.Stat[2], e0 = m.Evo[0], e1 = m.Evo[1];
        int limit = 1 + s2 / 2;                                      // Stat③ Permafrost: +live-wall count (0→1, 2→2, 4→3)
        float dur = 5f + s2;                                        // …and +duration
        // drop any freed refs, then evict the oldest if we're already at the limit (its shatter damages nearby foes)
        _frostWalls.RemoveAll(w => w == null || !GodotObject.IsInstanceValid(w));
        while (_frostWalls.Count >= limit && _frostWalls.Count > 0)
        {
            var oldest = _frostWalls[0];
            _frostWalls.RemoveAt(0);
            if (GodotObject.IsInstanceValid(oldest)) oldest.Shatter(true);
        }
        // orient the wall across the approach line (perpendicular to caster → cast point)
        Vector3 toWall = pos - GlobalPosition; toWall.Y = 0f;
        Vector3 facing = toWall.LengthSquared() > 0.01f ? toWall.Normalized() : -GlobalTransform.Basis.Z;
        Vector3 along = new Vector3(-facing.Z, 0f, facing.X);
        float halfLen = (3.6f + 0.5f * s1) * S.SpellArea;           // Stat② Rampart: +wall length
        float gy = Game.I.SurfaceHeight(pos, GlobalPosition.Y);
        var center = new Vector3(pos.X, gy, pos.Z);
        float shatterDmg = Base() * (1.3f * (1f + 0.18f * s0)) * ComboMul();   // Stat① Shatter
        float shatterRad = halfLen + 3f;
        int id = ++_frostWallSeq;
        var wall = new FrostWall(); Game.I.AddChild(wall);
        wall.Init(this, center, along, halfLen, dur, shatterDmg, shatterRad, id, false);
        wall.Chill = e0; wall.Pulse = e1;   // Evo A Frostbite Wall (chills nearby) / Evo B Glacier (pulses frost)
        _frostWalls.Add(wall);
        SetArm("ward", 0.4f);
        Game.I.Sfx?.Freeze(center); Game.I.Sfx?.ModFrost(center);
        Game.I.NetMgr?.BroadcastVfx(81, center, along, halfLen, dur, DamageTypes.Col(DamageType.Frost));   // allies get a remote copy (visual + obstacle)
    }
    public void OnFrostWallGone(FrostWall w) { _frostWalls.Remove(w); }

    // the vault icicle: erupts where she kicked off, holds ~0.7s, then bursts (host applies the slow + light frost). (NEW)
    public void SpawnVaultIcicle(Vector3 at, float rad, float dmg, Color col, bool remote = false, float slowFactor = 0.45f, float freezeAmt = 0f)
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
                    { e.Hurt(dmg, DamageType.Frost, true); e.Slow(2.5f, slowFactor); if (freezeAmt > 0f) e.AddFreeze(freezeAmt, FreezeThreshMul, FrostDurBonus); }
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
        int s0 = _curFin.Stat[0], s1 = _curFin.Stat[1], s2 = _curFin.Stat[2], e0 = _curFin.Evo[0], e1 = _curFin.Evo[1];
        Vector3 fwd = AimDir(); fwd.Y = 0; fwd = fwd.LengthSquared() > 0.001f ? fwd.Normalized() : Vector3.Forward;
        var right = new Vector3(fwd.Z, 0, -fwd.X).Normalized();
        float half = (5f + 0.6f * s1) * S.SpellArea;                          // Stat② Wide Vise
        Vector3 center = GlobalPosition + fwd * (half + 1.5f);
        float pct = Mathf.Min(0.12f, 0.02f + 0.008f * s0);                    // Stat① Crush: +0.8% max-HP / stack
        float slowF = Mathf.Max(0.25f, 0.5f - 0.05f * s2);                    // Stat③ Rimebite
        float clapBonus = e0 > 0 ? Base() * (0.6f + 0.6f * e0) : 0f;          // Evo A Shatter Clap
        foreach (var e in Game.I.Enemies.ToArray())
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
            var rel = e.GlobalPosition - center;
            float af = Mathf.Abs(rel.Dot(fwd)), ar = Mathf.Abs(rel.Dot(right));
            if (af < half + e.Radius && ar < half + e.Radius)
            {
                if (e1 > 0) e.AddFreeze(e.FreezeThreshold * (0.6f + 0.15f * e1), FreezeThreshMul, FrostDurBonus);   // Evo B Absolute Vise: freeze trapped foes
                e.Hurt(e.MaxHp * pct + Base() * 0.5f + clapBonus, DamageType.Frost, true);   // % max HP + flat floor (+ clap bonus)
                e.Slow(2f, slowF);
                ComboFromSource();
            }
        }
        Game.I.DamageWorld(center, half, Base() * 0.5f);
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
        int s0 = _curFin.Stat[0], s1 = _curFin.Stat[1], s2 = _curFin.Stat[2], e0 = _curFin.Evo[0], e1 = _curFin.Evo[1];
        Vector3 fwd = -_cam.GlobalTransform.Basis.Z; fwd.Y = 0; fwd = fwd.Normalized();
        float dist = 9f + 2.5f * s1 + pow * 2f;            // Stat② Momentum: distance
        float dmg = Base() * 0.6f * (1f + 0.18f * s0);     // Stat① Onslaught
        _rushDur = 0.30f; _rushDist = dist; _rushDir = fwd; _rushT = _rushDur; _rushWind = false;   // glide forward over the duration (no wind gusts — this is blood) (NEW)
        if (_inWaterBody) { _rushT = 0f; _rushDist = 0f; }   // no movement-dash combos while wading/swimming (NEW)
        _iframe = Mathf.Max(_iframe, _rushDur + 0.15f);                          // immune while riding the wave
        // the wave itself carries the damage + knockback, travelling with her
        void Wave(Vector3 at, float scl)
        {
            var wave = new BloodWave
            {
                Dir = fwd, Dmg = dmg * scl, Knock = 2.5f + 0.6f * s1,
                Width = (5f + s1) * S.SpellArea, Speed = dist / _rushDur, Range = dist + 1f, SlowDur = 1.2f, BanksStack = true,
                ShieldChance = Mathf.Min(0.75f, 0.20f + 0.11f * s2),   // Stat③ Bulwark: blood-shield return chance
                Gush = true                                            // blood gush on each hit + splatter at the end of the ride
            };
            Game.I.AddChild(wave);
            wave.GlobalPosition = new Vector3(at.X, 0.5f, at.Z) + fwd * 1.5f;
        }
        Wave(GlobalPosition, 1f);
        for (int i = 1; i <= e1; i++)   // Leg Tsunami: +1 chasing wave each stack
        {
            var origin = GlobalPosition;
            var tw = CreateTween(); tw.TweenInterval(0.22f * i);
            tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(this) && Game.I != null) Wave(origin, 0.7f); }));
        }
        if (e0 > 0)   // Epic Gore Trail: a bleeding trail laid along the dash line
        {
            int drops = 2 + e0;
            for (int i = 0; i < drops; i++)
            {
                var tp = GlobalPosition + fwd * (dist * (i + 0.5f) / drops);
                var gf = new GroundField { Type = FieldType.Hex, Radius = 1.8f, Dur = 2f + 0.3f * e0, Power = Base() * 0.12f * e0, DType = DamageType.Blood, TintColor = DamageTypes.Col(DamageType.Blood), FromCombo = true, RotDps = Base() * 0.06f * e0 };
                Game.I.AddChild(gf); gf.GlobalPosition = new Vector3(tp.X, 0.04f, tp.Z);
            }
        }
        Ring(GlobalPosition, DamageTypes.Col(DamageType.Blood), 4f, 0.4f);
        CamKick(0.5f);
    }

    // Blood Curse: a cone of misty blood applies Hex. No bounce at common; higher rarity adds bounces.
    // Each hex applied banks a Blood Stack.
    private void FinBloodCurse(float pow, int t)
    {
        int s0 = _curFin.Stat[0], s1 = _curFin.Stat[1], s2 = _curFin.Stat[2], e0 = _curFin.Evo[0], e1 = _curFin.Evo[1];
        Vector3 o = _cam.GlobalPosition;
        Vector3 fwd = (-_cam.GlobalTransform.Basis.Z).Normalized();
        float reach = 12f * S.SpellArea, cosArc = Mathf.Max(0.35f, 0.6f - 0.04f * s1);   // Stat② Wide Maw
        int jumps = s2 + (e0 > 0 ? e0 : 0);                            // Stat③ Contagion (+ Epic Plague spread range)
        float markAmp = S.MarkAmp * (e0 > 0 ? 1f + 0.15f * e0 : 1f);   // Epic Plague: hex potency
        float cdmg = Base() * 0.8f * (1f + 0.18f * s0);                // Stat① Miasma
        int hexed = 0;
        bool curseKill = false;
        foreach (var e in Game.I.Enemies.ToArray())
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
            var to = e.GlobalPosition - o; float d = to.Length();
            if (d > reach + e.Radius) continue;
            if (fwd.Dot(to / Mathf.Max(d, 0.001f)) < cosArc) continue;
            e.Hurt(cdmg, DamageType.Blood, true);
            e.Mark(3f, markAmp, jumps);
            if (e1 > 0) Heal(cdmg * 0.03f * e1);   // Leg Exsanguinate: cursed foes bleed HP into your pool
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
        if (MenuShielded || FullyImmune) return;   // (MP) no stun-lock while menuing in the bubble; Divinity/Faith Shield are total immunity
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
        if (Shield <= 0.01f) { ShieldBreakT = 0.6f; HurtFlash = Mathf.Max(HurtFlash, 0.45f); Game.I.Sfx?.ShieldBreak(); }   // (NEW) dispel emptied the shield
        if (src.HasValue) { DmgDirWorld = src.Value - GlobalPosition; DmgDirWorld.Y = 0; DmgDirT = 1.2f; }
        Game.I.Sfx?.Thunder();
    }

    public void OnHit(Enemy e, bool killed, Bolt b)
    {
        if (b == null) return;
        OnHitCore(e, killed, b.Dmg, b.DType, b.Normal, b.Charged, b.ComboShot, b.Crit);
        if (b.ArcaneBurst && ArcaneWitch && e != null) MarkArcane(e);   // (OVERHAUL) EVERY arcane bolt marks its target — up to 3 foes per left-click
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
    public void OnHitDirect(Enemy e, bool killed, float dmg, DamageType dt, bool crit = false)
        => OnHitCore(e, killed, dmg, dt, normal: false, charged: true, combo: true, crit: crit);

    // bolt-free NORMAL hit — for melee/instant primaries (e.g. the Gale punch). Registers as a normal attack so it
    // restores mana on hit (S.ManaGain) just like every other witch's primary, instead of being treated as charged. (NEW)
    public void OnHitDirectNormal(Enemy e, bool killed, float dmg, DamageType dt)
        => OnHitCore(e, killed, dmg, dt, normal: true, charged: false, combo: true);

    private void OnHitCore(Enemy e, bool killed, float dmg, DamageType dt, bool normal, bool charged, bool combo, bool crit = false)
    {
        DmgWindow += dmg;   // shared with allies as ult charge
        if (FervorWildfire > 0 && EmberFervorT > 0f && e != null && !e.Dead) e.AddBurn(1f, Base() * 0.05f * FervorWildfire, Base() * 3.2f, 0f, Game.I.LocalPeer);   // (OVERHAUL) Ember Fervor · Wildfire: your hits ignite
        var _st = Game.I.MyStats;   // (NEW) end-of-run tally (kills are host-authoritative — tracked in Enemy.Die, not here)
        _st.DamageDealt += dmg;
        if (dmg > _st.BiggestHit) _st.BiggestHit = dmg;
        if (e != null && e.IsBoss) _st.BossDamage += dmg;
        if (Ult != UltKind.None && !UltActive && UltLingerT <= 0f && _rushDashLingerT <= 0f)   // no recharge while a lingering ult effect is up (fields, transforms) OR a rush-dash field still burns — avoids the recharge-mid-ult feedback loop
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
        if (killed && OverchargeActive && ModArcUnbound) UltActiveT = Mathf.Min(22f, UltActiveT + 0.5f);   // Unbound: kills extend Overcharge
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
        if (_chargedRefund && charged) { AddMana(0.5f); _chargedRefund = false; }   // (EXPERIMENT) right-click returns 0.5 on connect (net −0.5 vs the 1 it cost)
        if (S.Lifesteal > 0) { Heal(dmg * S.Lifesteal); if (CrimsonWitch) _st.Highlight += dmg * S.Lifesteal; }   // Crimson highlight = health leeched
        if (crit && ArcaneWitch && dmg > 0f && !Downed) Heal(dmg * (ArcaneCritHeal + ArcaneCritHealBonus));   // (NEW) Arcane passive: her crits heal her a slice of the damage
        if (killed && ArcaneChainReaction && ArcaneWitch && dt == DamageType.Arcane && !_inArcaneNova && e != null && GodotObject.IsInstanceValid(e))   // legendary: an arcane kill bursts in a nova
        {
            _inArcaneNova = true;   // guard: a nova's own kills can't spawn nested novas (bounds the cascade)
            float nr = 5.5f * S.SpellArea, nd = Base() * 1.2f * ComboMul() * ArcanePowerMul; var np = e.GlobalPosition;
            foreach (var o in Game.I.Enemies.ToArray())
            {
                if (o == null || o.Dead || o == e || !GodotObject.IsInstanceValid(o) || Flat(o, np) > nr + o.Radius) continue;
                bool nc = RollCrit(); float d = nd; if (nc) d *= CritMult();
                o.Hurt(d, DamageType.Arcane, true, nc); OnHitDirect(o, o.Dead, d, DamageType.Arcane, nc);
            }
            Game.I.DamageWorld(np, nr, nd);
            Game.I.SpawnArcaneRupture(np + Vector3.Up * 0.5f, nr);
            Game.I.NetMgr?.BroadcastVfx(79, np + Vector3.Up * 0.5f, Vector3.Zero, nr, 0f, DamageTypes.Col(DamageType.Arcane));
            _inArcaneNova = false;
        }
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
                if (f.Type == FinType.FireWall && FireWallT > 0f) { f.Charge = 0; continue; }          // (NEW) can't rebuild Ring of Fire until the active wall expires
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

    // ===== (OVERHAUL) per-ability upgrade trees — path 0-2 = the 3 stat paths, 3-4 = the 2 evolutions; each stacks to UpgCap =====
    public const int UpgCap = 4;
    public void UpgradeMod(ModType t, int path)
    {
        var m = Mods.Find(x => x.Type == t); if (m == null) return;
        if (path >= 0 && path < 3) m.Stat[path] = Mathf.Min(UpgCap, m.Stat[path] + 1);
        else if (path >= 3 && path < 5) m.Evo[path - 3] = Mathf.Min(UpgCap, m.Evo[path - 3] + 1);
    }
    public void UpgradeFin(FinType t, int path)
    {
        var f = Fin.Find(x => x.Type == t); if (f == null) return;
        if (path >= 0 && path < 3) f.Stat[path] = Mathf.Min(UpgCap, f.Stat[path] + 1);
        else if (path >= 3 && path < 5) f.Evo[path - 3] = Mathf.Min(UpgCap, f.Evo[path - 3] + 1);
    }
    public int ModUpg(ModType t, int path) { var m = Mods.Find(x => x.Type == t); if (m == null) return 0; return path < 3 ? m.Stat[path] : m.Evo[path - 3]; }
    public int FinUpg(FinType t, int path) { var f = Fin.Find(x => x.Type == t); if (f == null) return 0; return path < 3 ? f.Stat[path] : f.Evo[path - 3]; }

    public void ApplyChargedMods(Vector3 pos)
    {
        Game.I.PlayerSound(pos, 2.4f);   // charged right-click noise (2nd loudest)
        foreach (var m in Mods)
        {
            float mag = m.Mag;
            switch (m.Type)
            {
                case ModType.FrostWall:
                    SpawnFrostWallMod(pos, m); break;
                case ModType.Bramble:
                {
                    int s0 = m.Stat[0], s1 = m.Stat[1], s2 = m.Stat[2], e0 = m.Evo[0], e1 = m.Evo[1];
                    float rootDur = 1.6f + 0.4f * s0;                  // Stat① Snare
                    float patchR = (6.5f + 0.8f * s1) * S.SpellArea;   // Stat② Thornfield
                    float patchDur = 3f + s2;                          // Stat③ Persistence
                    foreach (var e in Game.I.Enemies.ToArray())
                        if (!e.Dead && Flat(e, pos) < patchR) { e.Root(rootDur); if (e0 > 0) e.Hurt(Base() * (0.5f + 0.4f * e0), DamageType.Nature, false); }   // Epic Thorn Snap: burst on spawn
                    Ring(pos, DamageTypes.Col(DamageType.Nature), patchR, 0.45f);
                    Game.I.SpawnBramblePatch(pos, patchR, patchDur);
                    for (int i = 1; i <= e1; i++)   // Leg Overgrowth: the patch spreads over time
                    {
                        float gr = patchR * (1f + 0.35f * i);
                        var tw = CreateTween(); tw.TweenInterval(0.4f * i);
                        tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(this) && Game.I != null) { Game.I.SpawnBramblePatch(pos, gr, patchDur * 0.7f); foreach (var e in Game.I.Enemies.ToArray()) if (!e.Dead && Flat(e, pos) < gr) e.Root(rootDur * 0.7f); } }));
                    }
                    Game.I.Sfx?.ModBramble(pos);
                    break;
                }
                case ModType.Sunder:   // (OVERHAUL) stack-driven: cleave / shockwave / cinders + Shatter / Detonation
                {
                    int s0 = m.Stat[0], s1 = m.Stat[1], s2 = m.Stat[2], e0 = m.Evo[0], e1 = m.Evo[1];
                    float d = Base() * 0.9f * (0.8f * (1f + 0.2f * s0)), rad = (7f + 0.8f * s1) * S.SpellArea;   // Stat① Cleave / ② Shockwave
                    float sBurn = Base() * 0.06f * s2 + (e0 > 0 ? Base() * 0.08f * (1f + 0.4f * e0) : 0f);          // Stat③ Cinders + Evo A Shatter (adds burn)
                    foreach (var e in Game.I.Enemies.ToArray())
                    {
                        if (e.Dead || Flat(e, pos) >= rad || Game.I.SightBlocked(pos, e.GlobalPosition)) continue;
                        e.Hurt(d, DamageType.Ember, false);
                        if (sBurn > 0f) e.AddBurn(1f, sBurn, Base() * 3.2f, 0f, Game.I.LocalPeer);
                        if (e1 > 0) e.AddBurn(e.LivingBombThreshold, Mathf.Max(sBurn, Base() * 0.08f), Base() * 3.2f * (1f + 0.3f * e1), 0f, Game.I.LocalPeer);   // Evo B Detonation: instant Living Bomb
                    }
                    Game.I.DamageWorld(pos, rad, d);
                    Ring(pos, DamageTypes.Col(DamageType.Ember), rad, 0.4f);
                    Game.I.SpawnEmberBurst(pos, rad); Game.I.Sfx?.ModEmber(pos); break;
                }
                case ModType.Moonbeam:
                {
                    int s0 = m.Stat[0], s1 = m.Stat[1], s2 = m.Stat[2], e0 = m.Evo[0], e1 = m.Evo[1];
                    float mpow = Base() * 0.8f * (1f + 0.18f * s0);   // Stat① Radiance
                    float mrad = 3.2f + 0.6f * s1;                     // Stat② Wellspring (×SpellArea applied in field _Ready)
                    float mdur = 6f + 1.2f * s2;                       // Stat③ Waning
                    float pull = e1 > 0 ? 1.6f + 0.8f * e1 : 0f;      // Leg Lunar Tide: drag foes to centre
                    void Well(Vector3 wp)
                    {
                        var wf = new GroundField { Type = FieldType.Moon, Radius = mrad, Dur = mdur, Power = mpow, DType = DamageType.Lunar, Pull = pull };
                        Game.I.AddChild(wf); wf.GlobalPosition = new Vector3(wp.X, 0.04f, wp.Z);
                        Game.I.SpawnLightPillar(wp, DamageTypes.Col(DamageType.Lunar).Lerp(Colors.White, 0.3f), mrad, 16f, 0.6f);
                    }
                    Well(pos);
                    for (int i = 1; i <= e0; i++)   // Epic Twin Wells: +1 well each stack
                    {
                        float a = i / (float)Mathf.Max(1, e0) * Mathf.Tau;
                        Well(pos + new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * (mrad * 1.3f));
                    }
                    Game.I.Sfx?.ModChime(pos);
                    break;
                }
                case ModType.HexMark:
                {
                    int s0 = m.Stat[0], s1 = m.Stat[1], s2 = m.Stat[2], e0 = m.Evo[0], e1 = m.Evo[1];
                    float amp = 1.25f + 0.1f * s0;                 // Stat① Vulnerability
                    int jumps = 1 + s1;                            // Stat② Contagion
                    float mrange = (8f + 2f * s2) * S.SpellRange;  // Stat③ Far Curse: range
                    float mdur = 3f + s2;                          // Stat③ Far Curse: duration
                    float doom = e1 > 0 ? Base() * (0.8f + 0.5f * e1) : 0f;   // Leg Doombrand: on-death detonation
                    int marks = 1 + e0;                            // Epic Spreading Mark: also mark extra nearby foes
                    var pool = new System.Collections.Generic.List<Enemy>();
                    foreach (var e in Game.I.Enemies.ToArray()) if (e != null && !e.Dead && Flat(e, pos) < mrange) pool.Add(e);
                    pool.Sort((a, b) => Flat(a, pos).CompareTo(Flat(b, pos)));
                    for (int i = 0; i < marks && i < pool.Count; i++) pool[i].Mark(mdur, amp, jumps, doom);
                    Game.I.SpawnGroundSigil(pos, 4f, DamageTypes.Col(DamageType.Curse)); Ring(pos, DamageTypes.Col(DamageType.Curse), 4f, 0.4f); Game.I.Sfx?.ModCurse(pos);
                    break;
                }
                case ModType.Consecrate:
                {
                    int s0 = m.Stat[0], s1 = m.Stat[1], s2 = m.Stat[2], e0 = m.Evo[0], e1 = m.Evo[1];
                    float crad = 3.4f + 0.6f * s1;   // Stat② Sanctum (×SpellArea in field _Ready)
                    var cf = new GroundField { Type = FieldType.Heal, HealAllies = true,
                        EnemyDmg = Base() * 0.4f * (1f + 0.18f * s0),   // Stat① Wrath
                        Radius = crad, Dur = 5f,
                        Power = S.MaxHp * 0.02f * (1f + 0.15f * s2),    // Stat③ Grace: ally-heal power
                        DType = DamageType.Holy, TintColor = DamageTypes.Col(DamageType.Holy), FromCombo = true,
                        DeathBurst = e1 > 0 ? Base() * (0.8f + 0.5f * e1) : 0f };   // Leg Sanctified: foes dying on it burst
                    Game.I.AddChild(cf); cf.GlobalPosition = new Vector3(pos.X, 0.04f, pos.Z);
                    if (e0 > 0) { HolyEmpowerT = 5f; HolyEmpowerAmt = 0.07f * e0; }   // Epic Hallowed: consecration empowers your damage
                    Game.I.SpawnGroundSigil(pos, crad, DamageTypes.Col(DamageType.Holy)); Game.I.SpawnLightPillar(pos, DamageTypes.Col(DamageType.Holy), crad, 13f, 0.55f); Game.I.Sfx?.ModHoly(pos);
                    break;
                }
                case ModType.Smite:
                {
                    int s0 = m.Stat[0], s1 = m.Stat[1], s2 = m.Stat[2], e0 = m.Evo[0], e1 = m.Evo[1];
                    int targets = 1 + e1;                             // Leg Wrath of Heaven: +1 target each stack
                    float range = (10f + 1.5f * s1) * S.SpellRange;   // Stat② Far Reach
                    float smiteDmg = Base() * 1.4f * (1f + 0.18f * s0);   // Stat① Verdict
                    float slowF = Mathf.Max(0.3f, 0.5f - 0.05f * s2); // Stat③ Retribution: stronger slow
                    var pool = new System.Collections.Generic.List<Enemy>();
                    foreach (var e in Game.I.Enemies.ToArray()) if (e != null && !e.Dead && Flat(e, pos) < range) pool.Add(e);
                    pool.Sort((a, b) => Flat(a, pos).CompareTo(Flat(b, pos)));
                    for (int i = 0; i < targets && i < pool.Count; i++)
                    {
                        var e = pool[i];
                        e.Hurt(smiteDmg, DamageType.Holy, false);
                        e.Slow(2f, slowF);
                        if (e0 > 0) e.Mark(2f * e0, 1.25f, 0);   // Epic Condemn: vuln window (+2s/stack → 8s)
                        Lance(e.GlobalPosition);
                    }
                    if (pool.Count > 0) { Game.I.Sfx?.ModSmite(pool[0].GlobalPosition); Heal(S.MaxHp * (0.015f + 0.005f * s2)); }   // Stat③ Retribution: self-heal
                    break;
                }
                case ModType.Hemorrhage:
                {
                    int s0 = m.Stat[0], s1 = m.Stat[1], s2 = m.Stat[2], e0 = m.Evo[0], e1 = m.Evo[1];
                    float hDps = Base() * 0.25f * (1f + 0.18f * s0);   // Stat① Laceration
                    float hRad = (6.5f + 0.8f * s1) * S.SpellArea;      // Stat② Spray
                    float hDur = 4f + s2;                               // Stat③ Festering
                    float burstMul = 1f + 0.4f * e0;                    // Epic Rupture: death-bursts hit harder
                    foreach (var e in Game.I.Enemies.ToArray())
                        if (!e.Dead && Flat(e, pos) < hRad && !Game.I.SightBlocked(pos, e.GlobalPosition)) e.Bleed(hDps, hDur, e1 > 0, 0, burstMul);   // Leg Crimson Plague: rot bleed spreads on death
                    Ring(pos, DamageTypes.Col(DamageType.Blood), hRad, 0.4f);
                    Game.I.SpawnBloodMist(pos, hRad); Game.I.Sfx?.ModBlood(pos);
                    break;
                }
                case ModType.CrimsonPool:
                {
                    int s0 = m.Stat[0], s1 = m.Stat[1], s2 = m.Stat[2], e0 = m.Evo[0], e1 = m.Evo[1];
                    var cpField = new GroundField { Type = FieldType.Hex, Radius = 4f + 0.6f * s1, Dur = 5f,   // Stat② Flood
                        Power = Base() * 0.4f * (1f + 0.18f * s0),                  // Stat① Coagulate
                        DType = DamageType.Blood, TintColor = DamageTypes.Col(DamageType.Blood), FromCombo = true,
                        SlowMul = Mathf.Max(0.35f, 0.6f - 0.05f * s2),              // Stat③ Mire: stronger slow
                        GrantsBlood = true,
                        BloodBankMul = 1f + 0.3f * e0,                              // Epic Deep Well: banks/heals faster
                        RotDps = e1 > 0 ? Base() * 0.1f * e1 : 0f };                // Leg Bloodmire: foes in the pool take bleed
                    Game.I.AddChild(cpField); cpField.GlobalPosition = new Vector3(pos.X, 0.04f, pos.Z);
                    Game.I.SpawnBloodMist(pos, 4f); Game.I.Sfx?.ModPour(pos);
                    break;
                }
                case ModType.SanguineSpikes:
                {
                    int s0 = m.Stat[0], s1 = m.Stat[1], s2 = m.Stat[2], e0 = m.Evo[0], e1 = m.Evo[1];
                    float ssRad = (6f + 0.8f * s1) * S.SpellArea, ssDmg = Base() * 0.7f * (1f + 0.18f * s0);   // Stat②/Stat①
                    float bloodPer = 0.34f + 0.08f * s2;   // Stat③ Harvest
                    void Impale(float scl)
                    {
                        int hits = 0;
                        foreach (var e in Game.I.Enemies.ToArray())
                            if (!e.Dead && Flat(e, pos) < ssRad && !Game.I.SightBlocked(pos, e.GlobalPosition)) { e.Hurt(ssDmg * scl, DamageType.Blood, false); if (e0 > 0) e.Root(0.4f + 0.3f * e0); BloodReward(bloodPer); hits++; }   // Epic Barbs: spikes root
                        Game.I.DamageWorld(pos, ssRad, ssDmg * scl);
                        if (hits > 0) Game.I.NetMgr?.BloodAlliesNear(pos, 9999f, bloodPer * hits);
                        Game.I.SpawnGroundSpikes(pos, ssRad, 14, DamageTypes.Col(DamageType.Blood), 1.1f);
                    }
                    Impale(1f);
                    for (int i = 1; i <= e1; i++)   // Leg Crimson Garden: the spikes persist & re-trigger
                    {
                        var tw = CreateTween(); tw.TweenInterval(0.5f * i);
                        tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(this) && Game.I != null) Impale(0.7f); }));
                    }
                    Ring(pos, DamageTypes.Col(DamageType.Blood), ssRad, 0.4f); Game.I.Sfx?.ModSpike(pos);
                    break;
                }
                case ModType.Implosion:   // (OVERHAUL) stack-driven vortex: Rend/Event Horizon/Sustained + Crush / Singularity
                {
                    int s0 = m.Stat[0], s1 = m.Stat[1], s2 = m.Stat[2], e0 = m.Evo[0], e1 = m.Evo[1];
                    float irad = (8f + 0.8f * s1) * S.SpellArea;               // Stat② Event Horizon
                    float idmg = Base() * 0.35f * (1f + 0.3f * e0);           // opening hit (Epic Crush amplifies)
                    float idps = Base() * 0.45f * (1f + 0.18f * s0);         // Stat① Rend
                    float idur = 2.6f + 0.4f * s2;                           // Stat③ Sustained
                    float pullMul = 2.2f * (e1 > 0 ? 1f + 0.3f * e1 : 1f);   // Leg Singularity: pulls harder
                    var icol = DamageTypes.Col(DamageType.Wind);
                    Game.I.NetMgr?.StormForce(pos, irad, 2, idmg);
                    var cyI = new Cyclone(); Game.I.AddChild(cyI);
                    cyI.Init(this, new Vector3(pos.X, 0f, pos.Z), irad, idur, idps, true, false, pullMul, suppressVisual: true);
                    var orb = new WindOrb(); Game.I.AddChild(orb);
                    orb.Init(new Vector3(pos.X, 0f, pos.Z), irad, idur);
                    Game.I.NetMgr?.BroadcastVfx(15, new Vector3(pos.X, 0f, pos.Z), Vector3.Up, irad, idur, icol);
                    if (e1 > 0)   // Leg Singularity: the vortex collapses into a final burst
                    {
                        var here = pos; float burst = Base() * (0.8f + 0.6f * e1);
                        var tw = CreateTween(); tw.TweenInterval(idur);
                        tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(this) && Game.I != null) { foreach (var e in Game.I.Enemies.ToArray()) if (!e.Dead && GodotObject.IsInstanceValid(e) && Flat(e, here) < irad) e.Hurt(burst, DamageType.Wind, false); Game.I.DamageWorld(here, irad, burst); Game.I.VfxRing(here, icol, irad, 0.4f); } }));
                    }
                    Ring(pos, icol, irad, 0.4f);
                    Game.I.Sfx?.ModWind(pos);
                    break;
                }
                case ModType.Whirlwind:   // (OVERHAUL) stack-driven tornado: Shred/Funnel/Enduring + Launch Pad / Roaming Twister
                {
                    int s0 = m.Stat[0], s1 = m.Stat[1], s2 = m.Stat[2], e0 = m.Evo[0], e1 = m.Evo[1];
                    float wrad = (3.2f + 0.4f * s1) * S.SpellArea;                                   // Stat② Funnel
                    float wdur = 6f + s2 + (e1 > 0 ? e1 : 0);                                        // Stat③ Enduring (+ Roaming Twister duration)
                    float wdps = Base() * 0.5f * (1f + 0.18f * s0) * (e0 > 0 ? 1f + 0.15f * e0 : 1f);   // Stat① Shred (+ Launch Pad damage)
                    var pad = new WindPad(); Game.I.AddChild(pad);
                    pad.Init(this, new Vector3(pos.X, 0f, pos.Z), wrad, wdur, wdps, false);
                    pad.LaunchMul = e0 > 0 ? 1f + 0.25f * e0 : 1f;      // Epic Launch Pad: higher launch
                    pad.Roam = e1 > 0 ? 2.5f + 1.5f * e1 : 0f;         // Leg Roaming Twister: wanders toward foes
                    Game.I.NetMgr?.BroadcastVfx(12, new Vector3(pos.X, 0f, pos.Z), Vector3.Up, wrad, wdur, DamageTypes.Col(DamageType.Wind));
                    Ring(pos, DamageTypes.Col(DamageType.Wind), wrad, 0.5f);
                    Game.I.Sfx?.ModWind(pos);
                    break;
                }
                case ModType.Meteor:   // (OVERHAUL) always-Common base; the upgrade tree's stacks drive damage / blast / descent + the 2 evolutions
                {
                    int s0 = m.Stat[0], s1 = m.Stat[1], s2 = m.Stat[2], e0 = m.Evo[0], e1 = m.Evo[1];
                    float mrad = (6f + 0.8f * s1) * S.SpellArea;                       // Stat② Blast: +0.8 radius / stack
                    float mdmg = Base() * (2.2f * (1f + 0.18f * s0)) * ComboMul();      // Stat① Impact: +18% / stack
                    float fall = Mathf.Max(1.2f, 2.9f - 0.35f * s2);                    // Stat③ Descent: −0.35s fall / stack (snappier)
                    var at = new Vector3(pos.X, Game.I.SurfaceHeight(pos, pos.Y), pos.Z);
                    Game.I.SpawnEmberMeteor(at, mrad, mdmg, 3, Base() * 0.09f, Base() * 3.2f, this, fall);   // host-authoritative + broadcasts a ghost (kind 67)
                    for (int i = 0; i < e0; i++)   // Evo A — Meteor Shower: +1 satellite meteor / stack
                    {
                        float a = (i + 0.5f) / e0 * Mathf.Tau + GD.Randf();
                        var sat = new Vector3(at.X + Mathf.Cos(a) * mrad * 0.8f, at.Y, at.Z + Mathf.Sin(a) * mrad * 0.8f);
                        Game.I.SpawnEmberMeteor(sat, mrad * 0.5f, mdmg * 0.5f, 2, Base() * 0.09f, Base() * 3.2f, this, fall * 0.9f);
                    }
                    if (e1 > 0)   // Evo B — Cataclysm: a lingering re-igniting ember field; damage & duration scale with stacks
                    {
                        var field = new GroundField { Type = FieldType.Hex, DType = DamageType.Ember, Radius = mrad * 0.7f,
                            Dur = 3f + e1 * 1.5f, Power = Base() * (0.3f + 0.15f * e1), TintColor = DamageTypes.Col(DamageType.Ember),
                            BurnAdd = 1f, BurnPer = Base() * 0.08f, BurnBomb = Base() * 3.2f, BurnOwner = Game.I.LocalPeer, Src = this };
                        Game.I.AddChild(field); field.GlobalPosition = new Vector3(at.X, 0.05f, at.Z);
                    }
                    break;
                }
                case ModType.Eruption:   // (NEW Ember) molten upheaval + flame ring; knocks back, higher rarity flings the small ones skyward
                {
                    int s0 = m.Stat[0], s1 = m.Stat[1], s2 = m.Stat[2], e0 = m.Evo[0], e1 = m.Evo[1];
                    float erad = (7f + 0.8f * s1) * S.SpellArea;                    // Stat② Caldera: +0.8 r / stack
                    float edmg = Base() * (1.2f * (1f + 0.18f * s0));              // Stat① Magma: +18% / stack
                    float power = 8f + 2f * s2, flingChance = Mathf.Min(0.9f, 0.2f + 0.18f * s2);   // Stat③ Upthrust: +knockback & fling / stack
                    void Erupt(Vector3 p)
                    {
                        foreach (var e in Game.I.Enemies.ToArray())
                            if (!e.Dead && GodotObject.IsInstanceValid(e) && Flat(e, p) < erad && !Game.I.SightBlocked(p, e.GlobalPosition))
                            { e.Hurt(edmg, DamageType.Ember, false); e.AddBurn(1f, Base() * 0.08f, Base() * 3.2f, 0f, Game.I.LocalPeer); }
                        Game.I.NetMgr?.StormForce(p, erad, 1, power, flingChance);
                        Game.I.DamageWorld(p, erad, edmg);
                        Game.I.SpawnMoltenEruption(p, erad);
                    }
                    Erupt(pos);
                    if (e0 > 0)   // Evo A Fissure: a lingering burning crack
                    {
                        var fissure = new GroundField { Type = FieldType.Hex, DType = DamageType.Ember, Radius = erad * 0.6f,
                            Dur = 3f + e0 * 1.2f, Power = Base() * (0.25f + 0.12f * e0), TintColor = DamageTypes.Col(DamageType.Ember),
                            BurnAdd = 1f, BurnPer = Base() * 0.08f, BurnBomb = Base() * 3.2f, BurnOwner = Game.I.LocalPeer, Src = this };
                        Game.I.AddChild(fissure); fissure.GlobalPosition = new Vector3(pos.X, 0.05f, pos.Z);
                    }
                    for (int i = 1; i <= e1; i++)   // Evo B Volcano: +1 delayed re-eruption / stack
                    {
                        var tw = CreateTween(); tw.TweenInterval(0.5f * i);
                        tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(this) && Game.I != null) Erupt(pos); }));
                    }
                    break;
                }
                case ModType.FrostNova:   // (OVERHAUL) stack-driven: coldsnap / whiteout / deep freeze + Bitter Cold / Flash Freeze
                {
                    int s0 = m.Stat[0], s1 = m.Stat[1], s2 = m.Stat[2], e0 = m.Evo[0], e1 = m.Evo[1];
                    float nr = (6f + 0.8f * s1) * S.SpellArea;                     // Stat② Whiteout
                    float ndmg = Base() * (0.6f * (1f + 0.18f * s0));             // Stat① Coldsnap
                    float freeze = e1 > 0 ? 100f : (1f + 0.6f * s2);             // Stat③ Deep Freeze; Evo B Flash Freeze → instant freeze
                    void Nova(Vector3 p, float scl)
                    {
                        foreach (var e in Game.I.Enemies.ToArray())
                            if (!e.Dead && GodotObject.IsInstanceValid(e) && Flat(e, p) < nr && !Game.I.SightBlocked(p, e.GlobalPosition))
                            { e.Hurt(ndmg * scl, DamageType.Frost, false); e.AddFreeze(freeze, FreezeThreshMul, FrostDurBonus); e.Slow(2.5f, 0.5f); }
                        Game.I.DamageWorld(p, nr, ndmg * scl);
                        Ring(p, DamageTypes.Col(DamageType.Frost), nr, 0.45f);
                    }
                    Nova(pos, 1f);
                    for (int i = 1; i <= e0; i++)   // Evo A Bitter Cold: +1 delayed pulse / stack
                    {
                        var tw = CreateTween(); tw.TweenInterval(0.35f * i);
                        tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(this) && Game.I != null) Nova(pos, 0.6f); }));
                    }
                    Game.I.Sfx?.Cast(DamageType.Frost);
                    break;
                }
                case ModType.Spore:   // (OVERHAUL) stack-driven poison cloud + field: Toxin/Billow/Lingering + Bursting Spores/Fungal Bloom
                {
                    int s0 = m.Stat[0], s1 = m.Stat[1], s2 = m.Stat[2], e0 = m.Evo[0], e1 = m.Evo[1];
                    float pr = 6f + 0.8f * s1;               // Stat② Billow (raw; field _Ready ×SpellArea)
                    float phit = pr * S.SpellArea;
                    float pdps = Base() * 0.3f * (1f + 0.18f * s0);   // Stat① Toxin
                    foreach (var e in Game.I.Enemies.ToArray())
                        if (!e.Dead && GodotObject.IsInstanceValid(e) && Flat(e, pos) < phit && !Game.I.SightBlocked(pos, e.GlobalPosition))
                        { e.Hurt(Base() * 0.4f, DamageType.Nature, false); e.Poison(pdps, 4f, Game.I.LocalPeer); }
                    var pf = new GroundField { Type = FieldType.Hex, Radius = pr, Dur = 4f + s2, Power = pdps, DType = DamageType.Nature, TintColor = DamageTypes.Col(DamageType.Nature), FromCombo = true };   // Stat③ Lingering
                    Game.I.AddChild(pf); pf.GlobalPosition = new Vector3(pos.X, 0.04f, pos.Z);
                    if (e0 > 0)   // Epic Bursting Spores: the cloud detonates when it ends
                    {
                        float detR = phit, detDmg = Base() * (0.6f + 0.5f * e0);
                        var tw = CreateTween(); tw.TweenInterval(4f + s2);
                        tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(this) && Game.I != null) { foreach (var e in Game.I.Enemies.ToArray()) if (!e.Dead && Flat(e, pos) < detR) e.Hurt(detDmg, DamageType.Nature, false); Game.I.DamageWorld(pos, detR, detDmg); Game.I.VfxRing(pos, DamageTypes.Col(DamageType.Nature), detR, 0.4f); } }));
                    }
                    for (int i = 0; i < e1 && CountEnts() < MaxEnts; i++) SummonEnt();   // Leg Fungal Bloom: spawn sporelings that fight for you
                    Ring(pos, DamageTypes.Col(DamageType.Nature), phit, 0.4f); Game.I.Sfx?.Cast(DamageType.Nature);
                    break;
                }
                case ModType.Cursefield:   // (OVERHAUL) stack-driven cursed field: Blight/Pall/Enfeeble + Deep Curse / Withering Field
                {
                    int s0 = m.Stat[0], s1 = m.Stat[1], s2 = m.Stat[2], e0 = m.Evo[0], e1 = m.Evo[1];
                    float cr = 5.5f + 0.8f * s1;             // Stat② Pall (raw; field _Ready ×SpellArea)
                    float chit = cr * S.SpellArea;
                    float cdps = Base() * 0.4f * (1f + 0.18f * s0);   // Stat① Blight
                    var curf = new GroundField { Type = FieldType.Hex, Radius = cr, Dur = 5f, Power = cdps, DType = DamageType.Curse, TintColor = DamageTypes.Col(DamageType.Curse), FromCombo = true,
                        SlowMul = Mathf.Max(0.3f, 0.7f - 0.06f * s2),         // Stat③ Enfeeble: stronger slow
                        RotDps = e1 > 0 ? Base() * 0.1f * e1 : 0f };          // Leg Withering Field: foes inside rot
                    Game.I.AddChild(curf); curf.GlobalPosition = new Vector3(pos.X, 0.04f, pos.Z);
                    float markAmp = e0 > 0 ? 1.2f + 0.1f * e0 : 1f;   // Epic Deep Curse: mark for amplified damage
                    foreach (var e in Game.I.Enemies.ToArray())
                        if (!e.Dead && GodotObject.IsInstanceValid(e) && Flat(e, pos) < chit && !Game.I.SightBlocked(pos, e.GlobalPosition))
                        { e.Hurt(Base() * 0.5f, DamageType.Curse, false); if (e0 > 0) e.Mark(3f, markAmp, 0); }
                    Ring(pos, DamageTypes.Col(DamageType.Curse), chit, 0.4f); Game.I.Sfx?.Cast(DamageType.Curse);
                    break;
                }
                case ModType.Moonfall:   // (OVERHAUL) stack-driven Lunar nova: Crater/Impact/Moonlight + Afterglow/Nightfall
                {
                    int s0 = m.Stat[0], s1 = m.Stat[1], s2 = m.Stat[2], e0 = m.Evo[0], e1 = m.Evo[1];
                    float mrBase = 6.5f + 0.8f * s1, mr = mrBase * S.SpellArea;                 // Stat② Impact
                    float mdmg = Base() * 1.0f * (1f + 0.18f * s0) * (1f + LunarBonus);         // Stat① Crater
                    void Crater(Vector3 cp, float scl)
                    {
                        foreach (var e in Game.I.Enemies.ToArray())
                            if (!e.Dead && GodotObject.IsInstanceValid(e) && Flat(e, cp) < mr && !Game.I.SightBlocked(cp, e.GlobalPosition))
                            { bool crit = RollCrit() || GD.Randf() < 0.12f * s2; e.Hurt(mdmg * scl, DamageType.Lunar, true, crit); e.Slow(1.5f, 0.6f); }   // Stat③ Moonlight: extra crit chance
                        Game.I.DamageWorld(cp, mr, mdmg * scl);
                        Ring(cp, DamageTypes.Col(DamageType.Lunar), mr, 0.55f);
                    }
                    Crater(pos, 1f);
                    if (e0 > 0)   // Epic Afterglow: leaves a lingering lunar scorch
                    {
                        var sf = new GroundField { Type = FieldType.Moon, Radius = mrBase * 0.7f, Dur = 2f + 0.8f * e0, Power = mdmg * (0.1f + 0.05f * e0), DType = DamageType.Lunar, FromCombo = true };
                        Game.I.AddChild(sf); sf.GlobalPosition = new Vector3(pos.X, 0.04f, pos.Z);
                    }
                    for (int i = 1; i <= e1 && Game.I.IsNight; i++)   // Leg Nightfall: at night it craters again
                    {
                        var tw = CreateTween(); tw.TweenInterval(0.3f * i);
                        tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(this) && Game.I != null) Crater(pos, 0.7f); }));
                    }
                    Game.I.Sfx?.Cast(DamageType.Lunar);
                    break;
                }
                case ModType.ArcaneVortex:   // (OVERHAUL) stack-driven vortex: Maelstrom/Expanse/Drag + Singularity / Unstable Core
                {
                    int s0 = m.Stat[0], s1 = m.Stat[1], s2 = m.Stat[2], e0 = m.Evo[0], e1 = m.Evo[1];
                    float vr = 5f + 0.8f * s1;                         // Stat② Expanse (raw; field _Ready ×SpellArea)
                    float vdps = Base() * 0.5f * (1f + 0.18f * s0);   // Stat① Maelstrom
                    var vf = new GroundField { Type = FieldType.Hex, Radius = vr, Dur = 5f, Power = vdps, DType = DamageType.Arcane, TintColor = DamageTypes.Col(DamageType.Arcane), FromCombo = true,
                        SlowMul = Mathf.Max(0.3f, 0.5f - 0.04f * s2),          // Stat③ Drag
                        Pull = e0 > 0 ? 2f + 1f * e0 : 0f };                   // Epic Singularity: pulls foes inward
                    Game.I.AddChild(vf); vf.GlobalPosition = new Vector3(pos.X, 0.04f, pos.Z);
                    var vv = new ArcaneVortexVfx(); Game.I.AddChild(vv); vv.Init(new Vector3(pos.X, 0.04f, pos.Z), vr * S.SpellArea, 5f);   // the swirling lightning look, matched to the field's real radius
                    if (e1 > 0)   // Leg Unstable Core: collapses into a nova when it ends
                    {
                        var here = pos; float nova = Base() * (1.0f + 0.6f * e1), nr = vr * S.SpellArea;
                        var tw = CreateTween(); tw.TweenInterval(5f);
                        tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(this) && Game.I != null) { foreach (var e in Game.I.Enemies.ToArray()) if (!e.Dead && GodotObject.IsInstanceValid(e) && Flat(e, here) < nr) e.Hurt(nova, DamageType.Arcane, true); Game.I.DamageWorld(here, nr, nova); Game.I.VfxRing(here, DamageTypes.Col(DamageType.Arcane), nr, 0.4f); } }));
                    }
                    Ring(pos, DamageTypes.Col(DamageType.Arcane), vr, 0.4f); Game.I.Sfx?.Release(DamageType.Arcane);
                    break;
                }
                case ModType.ArcStorm:   // (OVERHAUL) stack-driven chain lightning: Voltage/Arc/Conductance + Overcharge / Chain Reaction
                {
                    int s0 = m.Stat[0], s1 = m.Stat[1], s2 = m.Stat[2], e0 = m.Evo[0], e1 = m.Evo[1];
                    int jumps = 2 + s1 + e0;                               // Stat② Arc (+ Epic Overcharge: +1 jump / stack)
                    float stormDmg = Base() * 1.4f * (1f + 0.18f * s0);   // Stat① Voltage
                    float forkRange = (14f + 2f * s2) * S.SpellRange;      // Stat③ Conductance: fork range
                    var vis = new System.Collections.Generic.List<Enemy>();
                    foreach (var e in Game.I.Enemies.ToArray())
                        if (e != null && !e.Dead && GodotObject.IsInstanceValid(e) && GlobalPosition.DistanceTo(e.GlobalPosition) < 45f * S.SpellRange && !Game.I.SightBlocked(EyePos, e.GlobalPosition + Vector3.Up)) vis.Add(e);
                    if (vis.Count == 0) break;
                    var cur = vis[Mathf.Clamp((int)(GD.Randf() * vis.Count), 0, vis.Count - 1)];
                    var visited = new System.Collections.Generic.HashSet<ulong>();
                    Vector3 from = (_handMeshR != null && GodotObject.IsInstanceValid(_handMeshR)) ? _handMeshR.GlobalPosition : EyePos;
                    var acol = DamageTypes.Col(DamageType.Arcane);
                    for (int j = 0; j < jumps && cur != null; j++)
                    {
                        visited.Add(cur.GetInstanceId());
                        var hitPt = cur.GlobalPosition + Vector3.Up * cur.Radius * 0.5f;
                        bool crit = RollCrit(); float d2 = stormDmg; if (crit) d2 *= CritMult();
                        cur.Hurt(d2, DamageType.Arcane, true, crit); OnHitDirect(cur, cur.Dead, d2, DamageType.Arcane, crit);
                        if (e1 > 0) cur.Mark(3f, 1.2f + 0.1f * e1, 0);   // Leg Chain Reaction: struck foes marked for extra chains
                        Game.I.SpawnArcaneLightning(new System.Collections.Generic.List<Vector3> { from, hitPt }, 0.8f);
                        Game.I.NetMgr?.BroadcastVfx(78, from, (hitPt - from).Normalized(), (hitPt - from).Length(), 0f, acol);
                        from = hitPt;
                        Enemy next = null; float nd = forkRange;
                        foreach (var e in Game.I.Enemies.ToArray())
                        {
                            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e) || visited.Contains(e.GetInstanceId())) continue;
                            float dd = e.GlobalPosition.DistanceTo(cur.GlobalPosition); if (dd < nd) { nd = dd; next = e; }
                        }
                        cur = next;
                    }
                    Game.I.Sfx?.Cast(DamageType.Arcane); Game.I.Sfx?.Thunder();
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
        if (Ult != UltKind.None && !UltActive && UltLingerT <= 0f && _rushDashLingerT <= 0f) UltCharge = Mathf.Min(1f, UltCharge + 0.004f * UltChargeMul);   // combo also charges the ult (not while a lingering ult effect / rush-dash field is up)
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

    private FinisherSlot _curFin;   // (OVERHAUL) slot currently executing — converted Fin* methods read its Stat/Evo stacks
    private void Execute(FinisherSlot f)
    {
        _curFin = f;
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
            case FinType.LunarNova: FinLunarNova(pow, t, col); SetArm("raise", 0.45f); break;          // (NEW Lunar)
            case FinType.CrescentStorm: FinCrescentStorm(pow, t, col); SetArm("thrust", 0.5f); break;  // (NEW Lunar)
            case FinType.ArcaneBlink: FinArcaneBlink(pow, t, col); SetArm("flick", 0.3f); break;        // (NEW Arcane)
            case FinType.ArcaneBlast: FinArcaneBlast(pow, t, col); SetArm("channel", 0.85f); break;      // (NEW Arcane)
        }
    }

    // ===== Arcane finishers (NEW) — universal =====
    // A blink: teleport in your move direction (reach 9u common → 23u legendary), leaving a rift at BOTH ends that erupts ~1s later.
    private void FinArcaneBlink(float pow, int t, Color col)
    {
        int s0 = _curFin.Stat[0], s1 = _curFin.Stat[1], s2 = _curFin.Stat[2], e0 = _curFin.Evo[0], e1 = _curFin.Evo[1];
        float maxDist = 9f + 3f * s2;   // Stat③ Phase: blink distance
        Vector3 startPos = GlobalPosition;
        Vector3 dir = InputDir();                                              // blink in the direction you're MOVING
        if (dir.LengthSquared() < 0.01f) dir = -GlobalTransform.Basis.Z;       // standing still → blink the way you're facing
        dir.Y = 0f; dir = dir.LengthSquared() > 0.001f ? dir.Normalized() : Vector3.Forward;
        float dist = maxDist;
        if (BeamSurfaceHit(GlobalPosition + Vector3.Up * 1.0f, dir, maxDist, out var surf, out _))   // stop just short of a wall in the way
            dist = Mathf.Max(0f, new Vector2(surf.X - GlobalPosition.X, surf.Z - GlobalPosition.Z).Length() - 0.6f);
        Vector3 dest = GlobalPosition + dir * dist;
        float gy = Game.I.SurfaceHeight(dest, GlobalPosition.Y);
        dest = ClampPos(new Vector3(dest.X, gy, dest.Z));
        GlobalPosition = dest; _iframe = Mathf.Max(_iframe, 0.3f); _grounded = true; _vy = 0f; _jumps = JumpsMax; _noFall = 0.5f;
        float rdmg = Base() * 2.2f * (1f + 0.18f * s0) * ComboMul(), rrad = (4f + 0.6f * s1) * S.SpellArea;   // Stat① Rift / Stat② Warp
        float pull = e1 > 0 ? 3f + 1.5f * e1 : 0f;   // Leg Implode: rifts pull foes in first
        SpawnArcaneRift(startPos, rrad, rdmg, pull);
        SpawnArcaneRift(dest, rrad, rdmg, pull);
        for (int i = 1; i <= e0; i++)   // Epic Triple Rift: extra rifts bloom mid-blink
        {
            var mid = startPos.Lerp(dest, i / (float)(e0 + 1));
            SpawnArcaneRift(mid, rrad * 0.85f, rdmg * 0.8f, pull);
        }
        Game.I.SpawnArcaneLightning(new System.Collections.Generic.List<Vector3> { startPos + Vector3.Up, dest + Vector3.Up }, 1f);
        CamKick(0.4f); Game.I.Sfx?.Cast(DamageType.Arcane);
    }
    private void SpawnArcaneRift(Vector3 pos, float radius, float dmg, float pull = 0f) { var r = new ArcaneRift(); Game.I.AddChild(r); r.Init(this, pos, radius, dmg, 1f); r.Pull = pull; }
    public void ArcaneRiftHit(Enemy e, float dmg)   // called by an ArcaneRift when it detonates
    {
        bool crit = RollCrit(); float d = dmg; if (crit) d *= CritMult();
        e.Hurt(d, DamageType.Arcane, true, crit); OnHitDirect(e, e.Dead, d, DamageType.Arcane, crit);
    }

    // Arcane Torrent: a wide raw-arcane beam-burst straight ahead — hits everything in a broad corridor + shoves them back.
    private void FinArcaneBlast(float pow, int t, Color col)
    {
        int s0 = _curFin.Stat[0], s1 = _curFin.Stat[1], s2 = _curFin.Stat[2], e0 = _curFin.Evo[0], e1 = _curFin.Evo[1];
        Vector3 eye = EyePos, dir = AimDir();
        float len = (26f + 4f * s2) * S.SpellRange, halfW = (2.0f + 0.3f * s1) * S.SpellArea;   // Stat③ Distance / Stat② Breadth
        float dmg = Base() * 3.0f * (1f + 0.18f * s0) * ComboMul(), push = 0.8f + 0.25f * s2;   // Stat① Surge
        float critBonus = 0.12f * e0, critMulBonus = e0 > 0 ? 1f + 0.1f * e0 : 1f;              // Epic Overcharge: crit ramps
        foreach (var e in Game.I.Enemies.ToArray())
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
            if (!e.RayHitsBody(eye, dir, len, halfW, out _)) continue;   // (FIX) whole-body ray test — was a sphere at mid-body, missing tall foes' heads
            bool crit = RollCrit() || (critBonus > 0f && GD.Randf() < critBonus); float d = dmg; if (crit) d *= CritMult() * critMulBonus;
            e.Hurt(d, DamageType.Arcane, true, crit); OnHitDirect(e, e.Dead, d, DamageType.Arcane, crit);
            e.Knockback(GlobalPosition, push);
            if (e1 > 0 && e.MarkT > 0f)   // Leg Cataclysm: chains through conduit-marked foes
            {
                int chained = 0;
                foreach (var o in Game.I.Enemies.ToArray())
                {
                    if (chained >= e1) break;
                    if (o == null || o == e || o.Dead || !GodotObject.IsInstanceValid(o)) continue;
                    if (e.GlobalPosition.DistanceTo(o.GlobalPosition) < 10f) { o.Hurt(dmg * 0.6f, DamageType.Arcane, true); chained++; }
                }
            }
        }
        Vector3 start = (_handMeshR != null && GodotObject.IsInstanceValid(_handMeshR)) ? _handMeshR.GlobalPosition : eye + dir * 0.5f;
        Game.I.SpawnArcaneKamehameha(start, dir, len, halfW, col);
        Game.I.SpawnArcaneRupture(start + dir * 1.5f, halfW * 1.6f);   // muzzle flare
        Game.I.NetMgr?.BroadcastVfx(80, start, dir, len, halfW, col);
        CamKick(0.9f); Game.I.Sfx?.ArcaneBlast(start);   // electric arcane thunder-zap
    }

    // ===== Lunar finishers (NEW) — Lunar only had Fullmod; these lean into the moon/crescent fantasy =====
    private void FinLunarNova(float pow, int t, Color col)
    {
        int s0 = _curFin.Stat[0], s1 = _curFin.Stat[1], s2 = _curFin.Stat[2], e0 = _curFin.Evo[0], e1 = _curFin.Evo[1];
        float dmg = Base() * 2.6f * (1f + 0.18f * s0) * (1f + LunarBonus);   // Stat① Waxing
        float radius = (9f + 1.5f * s1) * S.SpellArea;                        // Stat② Full Moon
        if (e1 > 0 && Game.I.IsNight) { radius *= 1f + 0.15f * e1; dmg *= 1f + 0.2f * e1; }   // Leg Blood Moon: swells at night
        float slowDur = 1.6f + 0.3f * s2, slowF = Mathf.Max(0.3f, 0.6f - 0.06f * s2);          // Stat③ Deep Chill
        void Pulse(float scl)
        {
            foreach (var e in Game.I.Enemies.ToArray())
                if (!e.Dead && GodotObject.IsInstanceValid(e) && Flat(e, GlobalPosition) < radius && !Game.I.SightBlocked(GlobalPosition, e.GlobalPosition))
                { e.Hurt(dmg * scl, DamageType.Lunar, true, RollCrit()); e.Slow(slowDur, slowF); }
            Game.I.DamageWorld(GlobalPosition, radius, dmg * scl);
            Ring(GlobalPosition, col, radius * 0.95f, 0.55f);
        }
        Pulse(1f);
        if (e0 > 0)   // Epic Eclipse Echo: a delayed second pulse, stronger per stack
        {
            float escl = 0.3f + 0.15f * e0;
            var tw = CreateTween(); tw.TweenInterval(0.3f);
            tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(this) && Game.I != null) Pulse(escl); }));
        }
        Game.I.Sfx?.Cast(DamageType.Lunar); CamKick(0.5f);
    }
    private void FinCrescentStorm(float pow, int t, Color col)
    {
        int s0 = _curFin.Stat[0], s1 = _curFin.Stat[1], s2 = _curFin.Stat[2], e0 = _curFin.Evo[0], e1 = _curFin.Evo[1];
        int baseCount = 6 + s1, count = baseCount + e1; var aim = AimDir();   // Stat② Gibbous + Leg Waxing Horde orbiters
        var alive = new System.Collections.Generic.List<Enemy>();
        foreach (var e in Game.I.Enemies.ToArray()) if (e != null && !e.Dead && GodotObject.IsInstanceValid(e)) alive.Add(e);
        float bladeDmg = Base() * 1.5f * (1f + 0.18f * s0) * (1f + LunarBonus);   // Stat① Keen Edge
        int pierce = CrescentPierceBonus + s2;                                    // Stat③ Sickle: pierce
        float size = 0.55f * CrescentSizeMul * (1f + 0.15f * s2);                 // Stat③ Sickle: blade size
        for (int i = 0; i < count; i++)
        {
            bool orbiter = i >= baseCount;   // the extra Waxing Horde blades orbit before loosing
            float a = (i / (float)count) * Mathf.Tau + 0.3f;
            var horiz = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
            var start = GlobalPosition + new Vector3(0, 2.2f, 0) + horiz * 0.6f;
            var launch = orbiter ? new Vector3(-horiz.Z, 0.4f, horiz.X).Normalized() * 16f : (Vector3.Up * 1.5f + horiz * 1.6f).Normalized() * 20f;
            var b = SpawnBolt(start, launch, bladeDmg, pierce, size, col, DamageType.Lunar, false, false, false, false, life: 3.6f, fromCombo: true);
            b.Homing = true; b.HomeSpeed = 34f; b.Turn = 8f; b.HomeDelay = orbiter ? 0.55f : 0.18f + (i % 3) * 0.03f;
            b.Target = alive.Count > 0 ? alive[i % alive.Count] : null; b.AimFallback = aim;
            if (e0 > 0) { b.Splinter = e0; b.SplinterDmg = bladeDmg * 0.5f; }   // Epic Splintering Moon: shards on first hit
        }
        Ring(GlobalPosition, col, 2.5f, 0.3f); Game.I.Sfx?.Cast(DamageType.Lunar);
    }

    // ===== Curse finishers (NEW) — witch-agnostic, but they lean into the curse fantasy =====
    // Soul Reap: a cursed reaping nova that bites harder the more wounded each foe is, and siphons souls to mend you.
    private void FinSoulReap(float pow, int t, Color col)
    {
        int s0 = _curFin.Stat[0], s1 = _curFin.Stat[1], s2 = _curFin.Stat[2], e0 = _curFin.Evo[0], e1 = _curFin.Evo[1];
        float radius = (8f + 0.8f * s1) * S.SpellArea, baseDmg = Base() * 1.2f * (1f + 0.18f * s0), healTotal = 0f;   // Stat②/①
        float exec = 1.6f + 0.5f * s2;                     // Stat③ Execution: missing-HP scaling
        float steal = 0.05f * (1f + 0.6f * e0);            // Epic Glut: stronger lifesteal
        bool killed = false;
        foreach (var e in Game.I.Enemies.ToArray())
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
            if (Flat(e, GlobalPosition) >= radius || Game.I.SightBlocked(GlobalPosition, e.GlobalPosition)) continue;
            float missing = e.MaxHp > 0f ? Mathf.Clamp(1f - e.Hp / e.MaxHp, 0f, 1f) : 0f;
            float dmg = baseDmg * (1f + exec * missing);   // reaps the wounded harder (Execution scales the bonus)
            e.Hurt(dmg, DamageType.Curse, true);
            healTotal += dmg * steal;
            if (e.Dead) killed = true;
            SpawnSoulWisp(e.GlobalPosition + Vector3.Up * 0.9f, col);
        }
        Game.I.DamageWorld(GlobalPosition, radius, baseDmg);
        if (healTotal > 0f) Heal(Mathf.Min(healTotal, S.MaxHp * (0.18f + 0.1f * e0)));   // soul harvest, capped (Glut raises the cap)
        if (e1 > 0 && killed && _curFin != null && GD.Randf() < 0.25f * e1) { _curFin.Charge = _curFin.Every; _curFin.Armed = true; }   // Leg Harvest: kills refund the finisher's charge
        Game.I.SpawnScytheVfx(GlobalPosition, AimDir(), radius, col);
        Ring(GlobalPosition, col, radius, 0.5f);
        Game.I.NetMgr?.BroadcastVfx(63, GlobalPosition, new Vector3(AimDir().X, 0f, AimDir().Z), radius, 0f, col);
        Game.I.Sfx?.CurseCrush(GlobalPosition);
    }

    // Hex Chains: bind the nearest foes into a temporary shared-pain web — a share of ALL damage any of them takes bleeds to the rest.
    private void FinHexChains(float pow, int t, Color col)
    {
        int s0 = _curFin.Stat[0], s1 = _curFin.Stat[1], s2 = _curFin.Stat[2], e0 = _curFin.Evo[0], e1 = _curFin.Evo[1];
        float radius = (10f + 1f * s1) * S.SpellArea; int maxLinks = 4 + s1;   // Stat② Weave: reach + link count
        float share = Mathf.Min(0.85f, 0.4f + 0.08f * s2 + (e1 > 0 ? 0.1f * e1 : 0f));   // Stat③ Sympathy (+ Leg Torment)
        float slowF = e0 > 0 ? Mathf.Max(0.3f, 0.7f - 0.08f * e0) : 0f;   // Epic Bind: chained foes slowed
        var links = new System.Collections.Generic.List<Enemy>();
        foreach (var e in Game.I.Enemies.ToArray())
            if (e != null && !e.Dead && GodotObject.IsInstanceValid(e) && Flat(e, GlobalPosition) < radius && !Game.I.SightBlocked(GlobalPosition, e.GlobalPosition)) links.Add(e);
        links.Sort((a, b) => Flat(a, GlobalPosition).CompareTo(Flat(b, GlobalPosition)));
        if (links.Count > maxLinks) links.RemoveRange(maxLinks, links.Count - maxLinks);
        int group = ++_curseGroupSeq;
        float burst = Base() * 1.4f * (1f + 0.18f * s0);   // Stat① Lash
        foreach (var e in links)
        {
            e.AddCurse(2f, group, DamageType.Curse, 1.35f, share);   // tether into a shared-pain group (share of damage bleeds across)
            e.Hurt(burst, DamageType.Curse, true);
            if (slowF > 0f) e.Slow(2f, slowF);
            SpawnCurseChain(GlobalPosition + Vector3.Up * 1.1f, e.GlobalPosition + Vector3.Up * 0.9f, col);
        }
        Game.I.SpawnGroundSigil(GlobalPosition, radius * 0.8f, col);
        Ring(GlobalPosition, col, radius, 0.5f);
        Game.I.NetMgr?.BroadcastVfx(64, GlobalPosition, Vector3.Zero, radius, 0f, col);
        Game.I.Sfx?.ArcaneBlast(GlobalPosition);   // (was the scratchy WitchCackle) — an electric arcane thunder-zap
    }

    // Doom Sigil: brand nearby foes, then a delayed cursed detonation (bigger the more branded). Deferred blast = DoomSigil node.
    private void FinDoomSigil(float pow, int t, Color col)
    {
        var flat = new Vector3(AimDir().X, 0f, AimDir().Z);
        flat = flat.LengthSquared() > 0.001f ? flat.Normalized() : Vector3.Forward;
        var at = GlobalPosition + flat * 6f;
        at = new Vector3(at.X, Game.I.SurfaceHeight(at, 1e9f) + 0.05f, at.Z);
        int s0 = _curFin.Stat[0], s1 = _curFin.Stat[1], s2 = _curFin.Stat[2], e0 = _curFin.Evo[0], e1 = _curFin.Evo[1];
        float radius = (6f + 0.7f * s1) * S.SpellArea, dmg = Base() * 2.4f * (1f + 0.18f * s0);   // Stat②/①
        int branded = 0;
        foreach (var e in Game.I.Enemies.ToArray())
            if (e != null && !e.Dead && GodotObject.IsInstanceValid(e) && new Vector2(e.GlobalPosition.X - at.X, e.GlobalPosition.Z - at.Z).Length() < radius + e.Radius)
            { e.AddCurse(1.5f, 0, DamageType.Curse, 1.35f, 0f); branded++; }
        float mul = 1f + (0.12f + 0.06f * s2) * Mathf.Max(0, branded - 1);   // Stat③ Compounding: per-brand bonus
        float fuse = Mathf.Max(0.5f, 1.35f - 0.2f * e0);                     // Epic Quickdoom: shorter detonation delay
        var sig = new DoomSigil(); Game.I.AddChild(sig); sig.Init(at, radius, dmg * mul, col, this, fuse, e1);   // Leg Cataclysm Sigil: chain generations
        Game.I.NetMgr?.BroadcastVfx(65, at, Vector3.Zero, radius, 0f, col);   // allies spawn a Remote ghost sigil (visual only)
        Game.I.Sfx?.ArcaneBlast(at);   // (was the scratchy WitchCackle) — an electric arcane thunder-zap
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
        int s0 = _curFin.Stat[0], s1 = _curFin.Stat[1], s2 = _curFin.Stat[2], e0 = _curFin.Evo[0], e1 = _curFin.Evo[1];
        float radius = (5f + 0.7f * s1) * S.SpellArea, dur = 2.5f + 0.4f * s2, dps = Base() * 0.5f * (1f + 0.18f * s0);   // Stat① Blaze / ② Wide Ring / ③ Everburn
        float burnPer = Base() * 0.08f * (1f + 0.4f * e0);   // Evo A Roaring Flames: +40% burn / stack
        FireWallT = dur;   // block re-arming until the wall burns out
        var center = new Vector3(GlobalPosition.X, Game.I.SurfaceHeight(GlobalPosition, GlobalPosition.Y) + 0.05f, GlobalPosition.Z);
        void SpawnRing(float r, float d)
        {
            var w = new FireWall { Center = center, Radius = r, Dur = dur, Dps = d, BurnPer = burnPer, BurnBomb = Base() * 3.2f, OwnerPeer = Game.I.LocalPeer };
            Game.I.AddChild(w); w.GlobalPosition = center;
            Game.I.RegisterFireRing(center, r, dur);
            Game.I.NetMgr?.BroadcastVfx(72, center, Vector3.Zero, r, dur, col);
            Ring(center, col, r, 0.5f);
        }
        SpawnRing(radius, dps);
        for (int i = 1; i <= e1; i++) SpawnRing(radius * (1f + 0.35f * i), dps * 0.7f);   // Evo B Expanding Inferno: +1 concentric outer ring / stack
        Game.I.Sfx?.Release(DamageType.Ember);
    }

    // Fireball: hurl a med-speed fireball at the cursor — heavy direct hit + a medium blast on impact.
    private void FinFireball(float pow, int t, Color col)
    {
        int s0 = _curFin.Stat[0], s1 = _curFin.Stat[1], s2 = _curFin.Stat[2], e0 = _curFin.Evo[0], e1 = _curFin.Evo[1];
        float dmgMul = 1f + 0.18f * s0;                                          // Stat① Scorch: +18% direct+blast / stack
        float directDmg = Base() * 3.2f * dmgMul * ComboMul(), blastDmg = Base() * 1.6f * dmgMul * ComboMul();
        float blastR = (4.5f + 0.6f * s1) * S.SpellArea;                         // Stat② Detonation: +0.6 radius / stack
        float burnPer = Base() * 0.09f * (1f + 0.25f * s2);                      // Stat③ Ignition: +25% burn / stack
        float fbSpeed = 22f * S.ProjSpeed;
        int shots = 1 + e0;                                                      // Evo A Split Shot: +1 fireball / stack
        Vector3 baseDir = AimDir().Normalized();
        for (int i = 0; i < shots; i++)
        {
            Vector3 dir = shots == 1 ? baseDir : baseDir.Rotated(Vector3.Up, (i - (shots - 1) * 0.5f) * 0.14f).Normalized();
            Vector3 origin = EyePos + dir * 0.6f;
            var fb = new Fireball { Dir = dir, Speed = fbSpeed, DirectDmg = directDmg, BlastDmg = blastDmg, BlastRadius = blastR, BurnPer = burnPer, BurnBomb = Base() * 3.2f, OwnerPeer = Game.I.LocalPeer, Src = this, Cataclysm = e1 };   // Evo B Cataclysm: lingering field on impact
            Game.I.AddChild(fb); fb.GlobalPosition = origin;
            Game.I.NetMgr?.BroadcastVfx(73, origin, dir, fbSpeed, blastR, col);
        }
        Game.I.Sfx?.Cast(DamageType.Ember);
    }

    // Ember Fervor: self-buff — crit + move speed for a few seconds; fists/feet blaze; can't recharge until it fades.
    private void FinEmberFervor(float pow, int t)
    {
        int s0 = _curFin.Stat[0], s1 = _curFin.Stat[1], s2 = _curFin.Stat[2], e0 = _curFin.Evo[0], e1 = _curFin.Evo[1];
        float dur = 5f + 0.9f * s0;                      // Stat① Frenzy: +0.9s / stack
        _emberFervorCrit = 0.10f + 0.03f * s1;          // Stat② Fervour: +3% crit / stack
        _emberFervorSpeed = 0.15f + 0.03f * s2;         // Stat③ Swiftness: +3% move / stack
        FervorWildfire = e0;                            // Evo A Wildfire: your hits ignite (potency scales with stacks)
        FervorPhoenix = e1;                             // Evo B Phoenix Heart: heal over the buff (rate scales)
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

    // Arcane witch: raw plasma crackling around her view-model hands at all times — little emissive shards that jitter, flicker,
    // and tumble, plus a soft arcane glow on each hand. Built lazily on first use; local-view only (first-person hands).
    private readonly System.Collections.Generic.List<MeshInstance3D> _arcaneHandFx = new();
    private readonly System.Collections.Generic.List<Vector3> _arcaneHandBase = new();
    private void UpdateArcaneHandFx(float dt)
    {
        if (_arcaneHandFx.Count == 0)
        {
            var col = DamageTypes.Col(DamageType.Arcane);
            foreach (var hand in new[] { _handMeshL, _handMeshR })
            {
                if (hand == null || !GodotObject.IsInstanceValid(hand)) continue;
                hand.AddChild(new OmniLight3D { OmniRange = 1.6f, LightColor = col, LightEnergy = 1.2f, ShadowEnabled = false });
                for (int i = 0; i < 4; i++)
                {
                    var b = new Vector3((GD.Randf() - 0.5f) * 0.16f, (GD.Randf() - 0.5f) * 0.16f, (GD.Randf() - 0.5f) * 0.16f);
                    var sp = new MeshInstance3D { Mesh = new BoxMesh { Size = Vector3.One * 0.03f }, MaterialOverride = Game.Emissive(col.Lerp(Colors.White, 0.2f), 2.4f), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
                    hand.AddChild(sp); sp.Position = b;
                    _arcaneHandFx.Add(sp); _arcaneHandBase.Add(b);
                }
            }
            if (_arcaneHandFx.Count == 0) return;
        }
        for (int i = 0; i < _arcaneHandFx.Count; i++)
        {
            var sp = _arcaneHandFx[i];
            if (sp == null || !GodotObject.IsInstanceValid(sp)) continue;
            sp.Position = _arcaneHandBase[i] + new Vector3((GD.Randf() - 0.5f) * 0.05f, (GD.Randf() - 0.5f) * 0.05f, (GD.Randf() - 0.5f) * 0.05f);   // crackle jitter
            sp.Scale = Vector3.One * (GD.Randf() < 0.22f ? 0f : 0.6f + GD.Randf() * 0.9f);   // flicker on/off + size
            sp.RotationDegrees = new Vector3(GD.Randf() * 360f, GD.Randf() * 360f, GD.Randf() * 360f);
        }
    }
    private void ClearArcaneHandFx()
    {
        foreach (var sp in _arcaneHandFx) if (sp != null && GodotObject.IsInstanceValid(sp)) sp.QueueFree();
        _arcaneHandFx.Clear(); _arcaneHandBase.Clear();
    }

    // Arcane charge: a growing, crackling orb of raw arcane held between her palms, spitting tiny sparks at her hands + the
    // air around it — bigger and more violent the longer she charges. Positioned at the midpoint of her two view-model hands.
    private Node3D _arcaneOrb, _arcaneOrbShards; private StandardMaterial3D _arcaneOrbHalo; private float _arcaneOrbArcT;
    private void UpdateArcaneChargeOrb(float dt, float amt)
    {
        if (amt < 0.02f) { ClearArcaneChargeOrb(); return; }
        var col = DamageTypes.Col(DamageType.Arcane);
        if (_arcaneOrb == null || !GodotObject.IsInstanceValid(_arcaneOrb))
        {
            _arcaneOrb = new Node3D(); Game.I.AddChild(_arcaneOrb);
            _arcaneOrb.AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = 1f, Height = 2f }, MaterialOverride = Game.ArcaneEnergyMat(), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });   // shader-driven plasma core
            var halo = new MeshInstance3D { Mesh = new SphereMesh { Radius = 1.6f, Height = 3.2f }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            _arcaneOrbHalo = new StandardMaterial3D { AlbedoColor = new Color(col.R, col.G, col.B, 0.25f), EmissionEnabled = true, Emission = col, EmissionEnergyMultiplier = 1.6f, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
            halo.MaterialOverride = _arcaneOrbHalo; _arcaneOrb.AddChild(halo);
            _arcaneOrbShards = new Node3D(); _arcaneOrb.AddChild(_arcaneOrbShards);
            var smat = Game.Emissive(col.Lerp(Colors.White, 0.2f), 2.2f);
            for (int i = 0; i < 6; i++) { var sp = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.12f, Height = 1.8f + GD.Randf(), RadialSegments = 4 }, MaterialOverride = smat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off }; sp.RotationDegrees = new Vector3(GD.Randf() * 360f, GD.Randf() * 360f, GD.Randf() * 360f); _arcaneOrbShards.AddChild(sp); }
            _arcaneOrb.AddChild(new OmniLight3D { OmniRange = 3f, LightColor = col, LightEnergy = 1.6f, ShadowEnabled = false });
        }
        Vector3 pos = (_handMeshL != null && _handMeshR != null && GodotObject.IsInstanceValid(_handMeshL) && GodotObject.IsInstanceValid(_handMeshR))
            ? (_handMeshL.GlobalPosition + _handMeshR.GlobalPosition) * 0.5f : EyePos + AimDir() * 0.6f;
        _arcaneOrb.GlobalPosition = pos;
        _arcaneOrb.Scale = Vector3.One * (0.04f + amt * 0.13f);   // grows with charge
        if (_arcaneOrbShards != null)
        {
            _arcaneOrbShards.RotationDegrees += new Vector3(dt * (120f + amt * 220f), dt * (160f + amt * 260f), dt * (90f + amt * 180f));
            float j = 0.85f + amt * 0.35f * Mathf.Sin(_ht * 40f) + (GD.Randf() - 0.5f) * (0.1f + amt * 0.3f);
            _arcaneOrbShards.Scale = Vector3.One * Mathf.Clamp(j, 0.5f, 1.5f);
        }
        if (_arcaneOrbHalo != null) _arcaneOrbHalo.EmissionEnergyMultiplier = 1.2f + amt * 1.0f + 0.5f * Mathf.Abs(Mathf.Sin(_ht * 22f));
        _arcaneOrbArcT -= dt;
        if (_arcaneOrbArcT <= 0f)
        {
            _arcaneOrbArcT = Mathf.Max(0.03f, 0.09f - amt * 0.05f);   // faster crackle as it grows
            float pick = GD.Randf();
            Vector3 tgt = (pick < 0.4f && _handMeshL != null) ? _handMeshL.GlobalPosition
                : (pick < 0.8f && _handMeshR != null) ? _handMeshR.GlobalPosition
                : pos + new Vector3(GD.Randf() - 0.5f, GD.Randf() - 0.5f, GD.Randf() - 0.5f) * (0.3f + amt * 0.55f);   // …or arc into the air around it
            Game.I.SpawnArcaneSpark(pos, tgt);
        }
    }
    private void ClearArcaneChargeOrb()
    {
        if (_arcaneOrb != null && GodotObject.IsInstanceValid(_arcaneOrb)) _arcaneOrb.QueueFree();
        _arcaneOrb = null; _arcaneOrbShards = null; _arcaneOrbHalo = null;
    }

    // Fade her view-model arms/hands (incl. their ink-outline next_pass) translucent as the globe-charge nears full, so they
    // stop blocking the crosshair; eased back to solid on release as the pose returns to normal. `fade`: 0 solid → 1 invisible.
    private float _armFade;
    private void SetArmFade(float fade)
    {
        foreach (var arm in new[] { _armL, _armR })
        {
            if (arm == null || !GodotObject.IsInstanceValid(arm)) continue;
            foreach (var c in arm.GetChildren())
            {
                if (c is MeshInstance3D mi && mi.MaterialOverride is StandardMaterial3D sm)
                {
                    FadeMat(sm, fade);
                    if (sm.NextPass is StandardMaterial3D np) FadeMat(np, fade);   // the ink outline too, else it lingers as a floating silhouette
                }
            }
        }
    }
    private static void FadeMat(StandardMaterial3D m, float fade)
    {
        if (fade <= 0.002f)
        {
            if (m.Transparency != BaseMaterial3D.TransparencyEnum.Disabled) { m.Transparency = BaseMaterial3D.TransparencyEnum.Disabled; var a0 = m.AlbedoColor; a0.A = 1f; m.AlbedoColor = a0; }
            return;
        }
        m.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        var a = m.AlbedoColor; a.A = 1f - fade; m.AlbedoColor = a;
    }

    private void Ring(Vector3 at, Color col, float grow, float life)
    {
        Game.I.VfxRing(at, col, grow, life);
        Game.I.NetMgr?.BroadcastVfx(0, at, Vector3.Zero, grow, life, col);
    }

    private void FinWave(float pow, int t, Color col)
    {
        int s0 = _curFin.Stat[0], s1 = _curFin.Stat[1], s2 = _curFin.Stat[2], e0 = _curFin.Evo[0], e1 = _curFin.Evo[1];
        float dmg = Base() * 2.4f * (1f + 0.18f * s0);      // Stat① Wither
        float radius = (10f + 1.5f * s1) * S.SpellArea;     // Stat② Expanse
        float markAmp = 1.15f + 0.12f * s2;                 // Stat③ Malediction: curse potency applied
        void Pulse(float r, float scl)
        {
            foreach (var e in Game.I.Enemies.ToArray())
                if (!e.Dead && Flat(e, GlobalPosition) < r && !Game.I.SightBlocked(GlobalPosition, e.GlobalPosition))
                {
                    bool wasCursed = e.MarkT > 0f;
                    e.Hurt(dmg * scl, DamageType.Curse, true);
                    e.Mark(3f, markAmp, 0);
                    if (e1 > 0 && wasCursed && !e.Dead) e.Hurt(Base() * (0.6f + 0.4f * e1), DamageType.Curse, false);   // Leg Doom Wave: cursed foes caught explode
                }
            Game.I.DamageWorld(GlobalPosition, r, dmg * scl);   // (FIX) AoE breaks props too
            Ring(GlobalPosition, col, r * 0.95f, 0.5f);
        }
        Pulse(radius, 1f);
        for (int i = 1; i <= e0; i++)   // Epic Echo Pulse: +1 ring each stack, rippling outward
        {
            float rr = radius * (1f + 0.4f * i);
            var tw = CreateTween(); tw.TweenInterval(0.2f * i);
            tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(this) && Game.I != null) Pulse(rr, 0.7f); }));
        }
    }

    private void StartBeam(float pow, int t)
    {
        int s0 = _curFin != null ? _curFin.Stat[0] : 0, s1 = _curFin != null ? _curFin.Stat[1] : 0, s2 = _curFin != null ? _curFin.Stat[2] : 0;
        _beamOverload = _curFin != null ? _curFin.Evo[0] : 0; int prism = _curFin != null ? _curFin.Evo[1] : 0;   // Epic Overload / Leg Prism
        _beamPow = Base() * 7f * (1f + 0.18f * s0);      // Stat① Bore
        _beamWidth = (2.2f + 0.3f * s1) * S.SpellArea;   // Stat② Beamwidth
        _beamLen = BeamLen * S.SpellRange;
        _beamT = 0.9f + 0.25f * s2; _beamHeld = 0f;      // Stat③ Channel
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
        while (_prismSegs.Count < prism)   // (OVERHAUL) Prism: build the extra fanned beams
        {
            var pcol = DamageTypes.Col(DamageType.Arcane);
            var pseg = new SegBeam(SpellLanceSegs);
            pseg.Build(Game.I, s =>
            {
                var mi = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.9f, 1f, 0.9f) } };
                var m = Game.Emissive(pcol.Lerp(Colors.White, 0.4f), 3.2f); m.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; var ac = m.AlbedoColor; ac.A = 0.65f; m.AlbedoColor = ac;
                mi.MaterialOverride = m; mi.RotationDegrees = new Vector3(90, 0, 0); s.AddChild(mi);
            });
            _prismSegs.Add(pseg);
        }
        Game.I.NetMgr?.BroadcastVfx(1, EyePos, _beamDir, _beamLen, _beamWidth, DamageTypes.Col(DamageType.Arcane));
    }

    private void UpdateBeam(float dt)
    {
        CrashLogger.Mark("Player.UpdateBeam");
        _beamT -= dt; _beamHeld += dt;
        float beamCrit = _beamOverload > 0 ? Mathf.Min(0.8f, 0.08f * _beamOverload + 0.12f * _beamOverload * _beamHeld) : 0f;   // Overload: crit chance climbs while held
        var dir = _beamDir; var eye = EyePos;
        foreach (var e in Game.I.Enemies.ToArray())
        {
            if (e.Dead) continue;
            var rel = e.GlobalPosition - eye;
            float proj = rel.X * dir.X + rel.Z * dir.Z;
            if (proj < 0 || proj > _beamLen) continue;
            float px = eye.X + dir.X * proj, pz = eye.Z + dir.Z * proj;
            if (new Vector2(e.GlobalPosition.X - px, e.GlobalPosition.Z - pz).Length() < _beamWidth + e.Radius) { bool bc = beamCrit > 0f && GD.Randf() < beamCrit; float bd = _beamPow * dt; if (bc) bd *= CritMult(); e.Hurt(bd, DamageType.Arcane, true, bc); }
        }
        for (int pi = 0; pi < _prismSegs.Count; pi++)   // (OVERHAUL) Prism: damage + place each fanned extra beam
        {
            float ang = (pi / 2 + 1) * 0.2f * ((pi % 2 == 0) ? 1f : -1f);
            var pdir = dir.Rotated(Vector3.Up, ang);
            foreach (var e in Game.I.Enemies.ToArray())
            {
                if (e.Dead) continue;
                var rel = e.GlobalPosition - eye; float proj = rel.X * pdir.X + rel.Z * pdir.Z;
                if (proj < 0 || proj > _beamLen) continue;
                float px = eye.X + pdir.X * proj, pz = eye.Z + pdir.Z * proj;
                if (new Vector2(e.GlobalPosition.X - px, e.GlobalPosition.Z - pz).Length() < _beamWidth + e.Radius) e.Hurt(_beamPow * dt, DamageType.Arcane, true);
            }
            var ps = _prismSegs[pi];
            if (ps != null) { Vector3 o = eye + new Vector3(0, -0.25f, 0), tg = eye + pdir * _beamLen + new Vector3(0, -0.25f, 0); ps.Place(o, tg, dt, 8f, 24f, 1f); }
        }
        if (_beamSeg != null)
        {
            Vector3 origin = eye + new Vector3(0, -0.25f, 0);
            Vector3 target = eye + dir * _beamLen + new Vector3(0, -0.25f, 0);
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
                float ss = GD.Randf() * _beamLen;
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
            Game.I.SpawnBurnMark(eye + dir * _beamLen, arc, _beamWidth * 1.6f, 2.5f);
            int stamped = 0;
            foreach (var e in Game.I.Enemies.ToArray())
            {
                if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
                var rel = e.GlobalPosition - eye;
                float proj = rel.X * dir.X + rel.Z * dir.Z;
                if (proj < 0 || proj > _beamLen) continue;
                float px = eye.X + dir.X * proj, pz = eye.Z + dir.Z * proj;
                if (new Vector2(e.GlobalPosition.X - px, e.GlobalPosition.Z - pz).Length() < _beamWidth + e.Radius)
                {
                    Game.I.SpawnBurnMark(e.GlobalPosition, arc, _beamWidth * 1.2f, 2.5f);
                    if (++stamped >= 2) break;
                }
            }
        }
        if (_beamT <= 0 && _beamSeg != null) { _beamSeg.Free(); _beamSeg = null; _beamLight = null; foreach (var ps in _prismSegs) ps?.Free(); _prismSegs.Clear(); }
    }

    private void FinVolley(float pow, int t, Color col)
    {
        int s0 = _curFin.Stat[0], s1 = _curFin.Stat[1], s2 = _curFin.Stat[2], e0 = _curFin.Evo[0], e1 = _curFin.Evo[1];
        int count = 5 + s1 + e0;                            // Stat② Salvo (+ Seeking adds a bolt)
        float bdmg = Base() * 1.4f * (1f + 0.18f * s0);     // Stat① Volley
        float speed = 48f * (1f + 0.15f * s2);              // Stat③ Velocity
        var dir = AimDir(); var eye = EyePos; var right = new Vector3(-dir.Z, 0, dir.X).Normalized();
        void Salvo()
        {
            for (int i = 0; i < count; i++)
            {
                float off = (i - (count - 1) / 2f) * 0.1f;
                var d = (dir + right * off).Normalized() * speed;
                var b = SpawnBolt(eye + dir * 1.2f, d, bdmg, 0, 0.5f, col, DamageType.Arcane, false, false, false, false, fromCombo: true);
                if (e0 > 0) { b.Homing = true; b.HomeSpeed = speed; b.Turn = 3f + 1.5f * e0; }   // Epic Seeking: bolts gain homing
            }
            Ring(eye + dir * 1.2f, col, 2.2f, 0.3f);
        }
        Salvo();
        for (int v = 1; v <= e1; v++)   // Leg Barrage: +1 delayed volley each stack
        {
            var tw = CreateTween(); tw.TweenInterval(0.18f * v);
            tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(this) && Game.I != null) Salvo(); }));
        }
        Game.I.Sfx?.ArcaneBlast(eye + dir * 1.5f);
    }

    private void FinSwarm(float pow, int t, Color col)
    {
        int s0 = _curFin.Stat[0], s1 = _curFin.Stat[1], s2 = _curFin.Stat[2], e0 = _curFin.Evo[0], e1 = _curFin.Evo[1];
        int count = 7 + s1;                                // Stat② Coven
        float bdmg = Base() * 1.2f * (1f + 0.18f * s0);    // Stat① Spellbite
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
            var b = SpawnBolt(start, launch, bdmg, 0, 0.45f, col, DamageType.Arcane, false, false, false, false, life: 3.8f, fromCombo: true);
            b.Homing = true;
            b.HomeSpeed = 33f;
            b.Turn = 7.5f + 1.5f * s2;                             // Stat③ Tracking: sharper homing
            b.HomeDelay = 0.22f + (i % 3) * 0.04f;                 // arch first, then seek
            b.Gravity = 26f;                                       // gives the arch its hang
            b.Target = alive.Count > 0 ? alive[i % alive.Count] : null;   // spread across distinct foes
            b.AimFallback = aim;                                   // no foes -> streak toward the cursor
            if (e0 > 0) b.MarkOnHit = 1.15f + 0.1f * e0;           // Epic Conduit Swarm: bolts leave conduit marks
            if (e1 > 0) { b.Arc = e1; b.ArcDmg = bdmg * 0.6f; }    // Leg Living Current: bolts arc to another foe on hit
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
        int s0 = _curFin.Stat[0], s1 = _curFin.Stat[1], s2 = _curFin.Stat[2], e0 = _curFin.Evo[0], e1 = _curFin.Evo[1];
        var f = new GroundField {
            Type = FieldType.Heal, HealAllies = true,
            Radius = 5f + 0.8f * s1,                                          // Stat② Grove
            Dur = 4f + s2,                                                    // Stat③ Evergreen
            Power = S.MaxHp * 0.028f * (1f + 0.18f * s0) * (e1 > 0 ? 1f + 0.2f * e1 : 1f),   // Stat① Blessing (+ Leg Wellspring heal power)
            EnemyDmg = 1f + (e0 > 0 ? Base() * (0.2f + 0.1f * e0) : 0f),      // Epic Consecrated: foes inside take searing damage
            FromCombo = true,
            Cap = 4,
            Follow = e1 > 0,                                                  // Leg Wellspring: the field follows you
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
        Game.I.Sfx?.HexWeave(GlobalPosition);                                                     // dark curse incantation (was the scratchy WitchCackle record-scratch)
    }

    private void FinRoot(float pow, int t, Color col)
    {
        int s0 = _curFin.Stat[0], s1 = _curFin.Stat[1], s2 = _curFin.Stat[2], e0 = _curFin.Evo[0], e1 = _curFin.Evo[1];
        float radius = (12f + 2f * s1) * S.SpellArea, dur = 2.4f + 0.5f * s2, dmg = Base() * 1.0f * (1f + 0.18f * s0);   // Stat②/③/①
        void Snare(float r, float scl)
        {
            foreach (var e in Game.I.Enemies.ToArray()) if (!e.Dead && Flat(e, GlobalPosition) < r && !Game.I.SightBlocked(GlobalPosition, e.GlobalPosition)) { e.Root(dur); e.Hurt(dmg * scl, DamageType.Nature, true); if (e0 > 0) e.Poison(Base() * 0.1f * e0, 3f, Game.I.LocalPeer); }   // Epic Thornburst: rooted foes take poison
            Game.I.DamageWorld(GlobalPosition, r, dmg * scl);   // (FIX) AoE breaks props too
            Ring(GlobalPosition, col, r, 0.6f);
        }
        Snare(radius, 1f);
        for (int i = 1; i <= e1; i++)   // Leg Ensnaring Grove: roots ripple outward
        {
            float rr = radius * (1f + 0.3f * i);
            var tw = CreateTween(); tw.TweenInterval(0.18f * i);
            tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(this) && Game.I != null) Snare(rr, 0.5f); }));
        }
    }

    // Creeping Blight: a poison field at your feet that keeps stacking additive poison on whoever stands in it.
    private void FinPoisonField(float pow, int t)
    {
        int s0 = _curFin.Stat[0], s1 = _curFin.Stat[1], s2 = _curFin.Stat[2], e0 = _curFin.Evo[0], e1 = _curFin.Evo[1];
        var f = new GroundField
        {
            Type = FieldType.Hex,
            TintColor = DamageTypes.Col(DamageType.Nature),
            Radius = 5.5f + 0.8f * s1,                                        // Stat② Overgrowth
            Dur = 5f + s2,                                                    // Stat③ Perennial
            Power = Base() * 0.12f,                                          // small direct dmg/sec
            PoisonAdd = 1.5f * (1f + 0.3f * s0),                             // Stat① Virulence: poison ramp
            SlowMul = e0 > 0 ? Mathf.Max(0.35f, 0.65f - 0.06f * e0) : 0f,    // Epic Toxic Bloom: also slows
            Creep = e1 > 0 ? 1.2f + 0.6f * e1 : 0f,                          // Leg Miasma: creeps toward enemies
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
        int s0 = _curFin.Stat[0], s1 = _curFin.Stat[1], s2 = _curFin.Stat[2], e0 = _curFin.Evo[0], e1 = _curFin.Evo[1];
        int count = 4 + 2 * s1;                          // Stat② Sowing
        float dmg = Base() * 1.4f * (1f + 0.18f * s0);   // Stat① Yield
        float spread = 2f + 4.5f + s1;
        float blast = (4.5f + 0.6f * s2) * S.SpellArea;  // Stat③ Blast Cap
        for (int i = 0; i < count; i++)
        {
            float a = GD.Randf() * Mathf.Tau, r = 2f + GD.Randf() * spread;
            var pos = GlobalPosition + new Vector3(Mathf.Cos(a) * r, 0, Mathf.Sin(a) * r);
            var m = new SeedMine { Caster = this, Damage = dmg, Chain = e0 > 0, Poison = Base() * 0.15f, Blast = blast,
                CloudPoison = e1 > 0 ? Base() * 0.12f * e1 : 0f, CloudRadius = e1 > 0 ? 3f + 0.6f * e1 : 0f };   // Epic Chain Bloom / Leg Spore Mines
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
        int s0 = _curFin.Stat[0], s1 = _curFin.Stat[1], s2 = _curFin.Stat[2], e0 = _curFin.Evo[0], e1 = _curFin.Evo[1];
        _thornBurstRad = 5f + 0.8f * s1;                        // Stat② Bramble: burst radius
        _thornRoot = e0 > 0 ? 0.8f + 0.4f * e0 : 0f;            // Epic Snare Bark: the burst roots
        if (e1 > 0) { _thornResistT = 6f; _thornResistAmt = 0.08f * e1; }   // Leg Ironbark: briefly gain damage resist
        int barkGrants = 1 + s2 / 2;                            // Stat③ Bark: bank 1 → 3 thorn charges per cast (still capped by the shared MaxArmor)
        float barbsDmg = Base() * 1.6f * (1f + 0.18f * s0);     // Stat① Barbs: burst damage
        for (int i = 0; i < barkGrants; i++) AddArmor(true, barbsDmg);   // AddArmor no-ops once the shared pool is full
        Ring(GlobalPosition, DamageTypes.Col(DamageType.Nature), 2.8f, 0.4f);
    }

    private void AnimateHands(float dt)
    {
        CrashLogger.Mark("Player.AnimateHands");
        if (_armL == null) return;
        _ht += dt;
        _kickL = Mathf.Max(0, _kickL - dt * 6f);
        _kickR = Mathf.Max(0, _kickR - dt * 6f);
        if (ArcaneWitch) { UpdateArcaneHandFx(dt); UpdateArcaneMarks(dt); } else if (_arcaneHandFx.Count > 0) ClearArcaneHandFx();   // raw plasma crackling on her hands + mark-timer decay
        if (ArcaneWitch && Charging) UpdateArcaneChargeOrb(dt, ChargeAmt); else ClearArcaneChargeOrb();   // the growing crackling charge orb between her palms
        if (ArcaneWitch)   // fade arms/hands out as the charge nears full so they don't block the crosshair; ease back on release
        {
            float target = Charging ? Mathf.Clamp((ChargeAmt - 0.5f) / 0.45f, 0f, 1f) * 0.9f : 0f;
            _armFade = Mathf.MoveToward(_armFade, target, dt * 5.5f);
            SetArmFade(_armFade);
        }

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
            else if (ArcaneWitch)   // (NEW) grasping a growing orb: LEFT cradles it from below (palm up), RIGHT hovers above (palm down)
            {
                lp.X = Mathf.Lerp(_baseLPos.X, -0.05f, cc); lp.Z -= cc * 0.30f; lp.Y = Mathf.Lerp(_baseLPos.Y, _baseLPos.Y - 0.05f, cc); lr.X += cc * 1.5f;
                rp.X = Mathf.Lerp(_baseRPos.X, 0.05f, cc); rp.Z -= cc * 0.30f; rp.Y = Mathf.Lerp(_baseRPos.Y, _baseRPos.Y + 0.24f, cc); rr.X -= cc * 1.5f;
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
                case "flick": { float f3 = Mathf.Sin(k * Mathf.Pi); lp.Z -= 0.34f * f3; lp.X -= 0.07f * f3; lp.Y += 0.06f * f3; lr.X -= 1.0f * f3; lr.Y += 0.5f * f3; break; }   // Arcane: LEFT hand snaps forward + outward, flicking the burst out (NEW)
                case "channel": { float fc = Mathf.Sin(k * Mathf.Pi); lp.X = Mathf.Lerp(_baseLPos.X, -0.07f, fc); rp.X = Mathf.Lerp(_baseRPos.X, 0.07f, fc); lp.Z -= 0.5f * fc; rp.Z -= 0.5f * fc; lp.Y += 0.05f * fc; rp.Y += 0.05f * fc; lr.X -= 0.7f * fc; rr.X -= 0.7f * fc; break; }   // Arcane: both palms drive a torrent forward (NEW)
                case "conjure": { float cu = Mathf.Sin(k * Mathf.Pi); lp.Y += 0.44f * cu; rp.Y += 0.44f * cu; lp.X -= 0.10f * cu; rp.X += 0.10f * cu; lp.Z -= 0.12f * cu; rp.Z -= 0.12f * cu; lr.X += 0.9f * cu; rr.X += 0.9f * cu; break; }   // Arcane: arms flung up, conjuring raw power (NEW)
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
                case "ward": { float wd = Mathf.Sin(k * Mathf.Pi); lp.Z -= 0.44f * wd; rp.Z -= 0.44f * wd; lp.Y += 0.20f * wd; rp.Y += 0.20f * wd; lp.X -= 0.10f * wd; rp.X += 0.10f * wd; lr.X += 0.6f * wd; rr.X += 0.6f * wd; break; }   // Frost Wall: both palms push forward + out, raising a barrier (NEW)
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
        if (Ult == UltKind.Stormform && UltActive && _windCharges > 0) { WindRush(); return; }            // (REWORK) Q during Stormform = a wind rush
        if (Ult == UltKind.LifeCurse && _specter) { EndSpecter(true); return; }                            // (REWORK) Q during Specter = detonate the burst early
        if (UltActive || UltCharge < 1f) return;
        ActivateUlt();
    }

    private void ActivateUlt()
    {
        UltCharge = 0f;
        UltMax = 0f;   // (ULT METERS) each ult that has a timed window sets this in its case → the generic HUD duration bar only shows for those
        // (NEW) anti-chain recharge lock. Reset here; the FIELD/SUMMON ult cases below set this to their EXACT lingering
        // duration (in-scope value → precise). Transform/aura ults keep UltActive true for their whole window, so the
        // existing !UltActive gate already covers them exactly. Instant bursts / DoTs leave nothing → recharge normally.
        UltLingerT = 0f;
        var _ecol = DamageTypes.Col(WitchDamage);        // (NEW) every witch's ult erupts a big element-coloured activation flourish + cinematic boom + shake
        Game.I.UltCast(GlobalPosition, _ecol);
        Game.I.Sfx?.UltCast(GlobalPosition);
        CamKick(1.0f);
        Game.I.NetMgr?.BroadcastUltCast((int)Ult);   // (NEW) allies get a picture-in-picture "ult cast" cutout of you casting this
        if (Game.I.UltOverlay != null && Game.I.UltOverlay.SoloTest) Game.I.UltOverlay.TriggerLocal(this, Ult);   // (dev) single-player self-preview
        switch (Ult)
        {
            case UltKind.Eclipse:
                // (REWORK) NO MORE ×2 damage. Instead: an inverted black/white transform — ×2 move, ×3 jump, +crit, an
                // arcane-blink dash, and every LUNAR hit detonates a shadow-nova. UltDmgMul stays 1.
                UltActive = true; UltActiveT = 8f + UltTier * 1.6f; UltDmgMul = 1f; _eclipseMax = UltActiveT; UltMax = UltActiveT;
                {
                    var ev = new EclipseVfx { Dur = UltActiveT, MaxDur = UltActiveT };
                    Game.I.AddChild(ev);
                    ev.GlobalPosition = new Vector3(GlobalPosition.X, GlobalPosition.Y + 26f, GlobalPosition.Z);
                    Ring(GlobalPosition, Colors.Black, 9f, 0.7f);
                    Ring(GlobalPosition, Colors.White, 4.5f, 0.5f);
                    CamKick(0.7f);
                    GrantUltAura(new Color(0.02f, 0.02f, 0.05f), 3.1f);   // an eclipse SHADOW wreathes her (white accents come from the aura's rim)
                    Game.I.FallingMotes(GlobalPosition, 7f, Colors.White, 20);   // pale motes rain down
                    _bodyModel?.SetEclipse(true);
                }
                Game.I.NetMgr?.BroadcastVfx(4, GlobalPosition, Vector3.Zero, UltActiveT, 0f, DamageTypes.Col(DamageType.Lunar));
                Game.I.Sfx?.Thunder();
                Game.I.Hud?.Banner("ECLIPSE");
                break;
            case UltKind.LunarLight: DeployLunarLight(); break;
            case UltKind.Crescent: SpawnCrescents(); break;
            case UltKind.FaithShield:
            {
                if (Game.I.Shield != null && GodotObject.IsInstanceValid(Game.I.Shield)) Game.I.Shield.QueueFree();
                int t = UltTier;
                float radius = (9f + t * 1.2f) * S.SpellArea;        // scales with spell-area / range cards
                float dur = 13f;                                     // (REWORK) base 13s; NULLIFIES all attacks while you're inside (Player.InsideFaithShield)
                float burst = Base() * (5f + t * 2f) * (ModShield ? 1.4f : 1f);   // flat shatter damage (base-scaled)
                float knock = 16f + t * 2f;
                var pos = new Vector3(GlobalPosition.X, 0.1f, GlobalPosition.Z);
                var sh = new FaithShield
                {
                    Radius = radius, Dur = dur, DurMax = dur, MeleeDmg = 6f + t * 2f, Reflect = ModShield,
                    HealPerSec = S.MaxHp * (0.05f + t * 0.008f),
                    BurstDmg = burst, BurstRadius = radius + 3f, Knock = knock,
                    Remote = !Game.I.IsAuthority,                    // host = authoritative (blocks + shatters); client = visual
                };
                Game.I.AddChild(sh); sh.GlobalPosition = pos;
                Game.I.Shield = sh;
                Game.I.NetMgr?.SpawnFaithShield(pos, radius, dur, burst, knock, ModShield);   // MP: host spawns an authoritative copy, others a visual one
                {
                    var hcol = DamageTypes.Col(DamageType.Holy);
                    Ring(pos, hcol, radius, 0.7f);
                    Ring(pos, Colors.White, radius * 0.6f, 0.55f);
                    Game.I.FallingMotes(pos, radius * 0.92f, hcol, 30, 13f);   // grace rains down as the aegis rises
                    Game.I.Sfx?.Thunder();
                }
                UltActive = true; UltActiveT = dur; UltMax = dur;
                Game.I.Hud?.Banner("FAITH SHIELD");
                break;
            }
            case UltKind.Judgement:
            {
                int t = UltTier;
                float fieldDur = 13f;              // (REWORK) the light/lance fields linger ≥13s base
                UltLingerT = fieldDur; UltMax = fieldDur;   // no recharge until they fade; drives the HUD duration meter
                if (ModJudge)
                {
                    // ONE colossal lance: devastating at the core, tapering to "okay" at the rim, then a pulsing field.
                    var at = GroundAim();
                    float rad = (9f + t * 1.2f) * S.SpellArea;   // HolyPulse/Lance don't auto-scale, so scale here
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
                    Game.I.FallingMotes(at, rad * 0.85f, DamageTypes.Col(DamageType.Holy), 26, 15f);   // the heavens open over the strike
                    var v = new Vfx(); Game.I.AddChild(v); v.GlobalPosition = new Vector3(at.X, 0.5f, at.Z);
                    v.Init(new SphereMesh { Radius = rad * 0.6f, Height = rad * 1.2f }, DamageTypes.Col(DamageType.Holy), 0.5f, 8f);
                    float pulseR = (12f + t * 1.6f) * S.SpellArea;   // (REWORK) bigger base, scales with area cards
                    var pulse = new HolyPulse
                    {
                        Radius = pulseR, Dur = fieldDur, MaxDur = fieldDur,
                        PulseDmg = Base() * (0.5f + t * 0.12f),   // low-med pulse damage (heals allies by the same)
                        PulseHeal = S.MaxHp * 0.05f, Interval = 0.8f
                    };
                    Game.I.AddChild(pulse); pulse.GlobalPosition = new Vector3(at.X, 0.06f, at.Z);
                    Game.I.NetMgr?.BroadcastField((int)FieldType.Heal, new Vector3(at.X, 0.04f, at.Z), pulseR, fieldDur, false, DamageTypes.Col(DamageType.Holy), (int)DamageType.Holy);
                    CamKick(1.0f);
                    Game.I.Sfx?.Release(DamageType.Holy);
                    Game.I.Hud?.Banner("JUDGEMENT");
                }
                else
                {
                    var all = Game.I.Enemies.FindAll(e => e != null && !e.Dead && GodotObject.IsInstanceValid(e));
                    all.Sort((a, b) => Flat(a, GlobalPosition).CompareTo(Flat(b, GlobalPosition)));
                    int count = Mathf.Min(12, Mathf.Max(1, Mathf.CeilToInt(all.Count * 0.25f)));   // (PERF) cap strike points — was up to 25% of the WHOLE swarm, each spawning a Lance + GroundField + motes + an inner per-enemy raycast splash
                    float dmg = Base() * (3.0f + t * 1.0f);
                    float aoe = 5.0f + t * 0.5f;              // (REWORK) bigger base; the GroundField auto-scales this by SpellArea
                    float aoeHit = aoe * S.SpellArea;         // splash + world damage scale to match the field
                    for (int i = 0; i < count && i < all.Count; i++)
                    {
                        var at = all[i].GlobalPosition;
                        foreach (var e in Game.I.Enemies.ToArray())   // small splash at each impact
                            if (!e.Dead && Flat(e, at) < aoeHit && !Game.I.SightBlocked(at, e.GlobalPosition)) { e.Hurt(dmg, DamageType.Holy, true); ComboFromSource(); }
                        Game.I.DamageWorld(at, aoeHit, dmg);   // (FIX) AoE breaks props too
                        Lance(at, 4.5f);                  // lances stay planted for the effect
                        Ring(at, DamageTypes.Col(DamageType.Holy), aoeHit, 0.55f);
                        Game.I.FallingMotes(at, aoeHit * 1.4f, DamageTypes.Col(DamageType.Holy), 10, 14f);   // a shaft of judgement from above
                        var f = new GroundField { Type = FieldType.Heal, HealAllies = true, EnemyDmg = Base() * 0.5f, Radius = aoe, Dur = fieldDur, Power = S.MaxHp * (0.02f + t * 0.004f), DType = DamageType.Holy, TintColor = DamageTypes.Col(DamageType.Holy), FromCombo = true };   // (REWORK) fields last ≥13s (GroundField scales Radius by SpellArea)
                        Game.I.AddChild(f); f.GlobalPosition = new Vector3(at.X, 0.04f, at.Z);
                    }
                    CamKick(0.8f);
                    Game.I.Sfx?.Release(DamageType.Holy);
                    Game.I.Hud?.Banner("JUDGEMENT");
                }
                break;
            }
            case UltKind.Divinity:
            {
                Divinity = true;
                _divBaseY = GlobalPosition.Y;
                _divT = 13f + UltTier * 1.5f + (ModDivinity ? 3f : 0f);   // (REWORK) base 13s
                _iframe = 999f;
                UltActive = true; UltActiveT = _divT; UltMax = _divT;      // drive the HUD duration meter
                _divRisen = false;
                GrantUltAura(DamageTypes.Col(DamageType.Holy), 3.1f);   // radiant grace wreathes her as she ascends
                Game.I.FallingMotes(GlobalPosition, 6f, DamageTypes.Col(DamageType.Holy), 26, 12f);
                Ring(GlobalPosition, DamageTypes.Col(DamageType.Holy), 6f, 0.7f);
                Ring(GlobalPosition, Colors.White, 3.5f, 0.55f);
                Game.I.Sfx?.Thunder();
                Game.I.NetMgr?.BroadcastVfx(6, GlobalPosition, Vector3.Zero, 4f, 0f, DamageTypes.Col(DamageType.Holy));
                Game.I.Hud?.Banner("DIVINITY");
                break;
            }
            case UltKind.BloodTsunami:
            {
                int t = UltTier;
                Vector3 fwd = -_cam.GlobalTransform.Basis.Z; fwd.Y = 0; fwd = fwd.Normalized();
                float dmg = Base() * (3.8f + t * 0.9f) * (ModTsunami ? 1.35f : 1f);   // mod keeps the ×1.35 (enhance only)
                float width = (20f + t * 2.5f + (ModTsunami ? 6f : 0f)) * S.SpellArea;
                float speed = 24f * S.ProjSpeed, range = (60f + t * 7f) * S.SpellRange;
                var bcol = DamageTypes.Col(DamageType.Blood);
                void Wave(Vector3 dir)
                {
                    var w = new BloodWave { Dir = dir, Dmg = dmg, Knock = 6f + t * 0.6f, Width = width, Speed = speed, Range = range, SlowDur = 3f };
                    Game.I.AddChild(w);
                    w.GlobalPosition = new Vector3(GlobalPosition.X, 0.5f, GlobalPosition.Z) + dir * 2f;
                }
                if (ModTsunami)   // (MOD REWORK) a RADIAL tsunami — waves erupt in all directions from her, not one wall
                {
                    int spokes = 8;
                    for (int i = 0; i < spokes; i++) Wave(fwd.Rotated(Vector3.Up, i * Mathf.Tau / spokes));
                    Ring(new Vector3(GlobalPosition.X, 0.05f, GlobalPosition.Z), bcol, 8f, 0.7f);
                    Ring(new Vector3(GlobalPosition.X, 0.05f, GlobalPosition.Z), bcol.Lerp(Colors.White, 0.4f), 4f, 0.5f);
                }
                else
                {
                    Wave(fwd);
                    var basePos = new Vector3(GlobalPosition.X, 0.4f, GlobalPosition.Z) + fwd * 2f;
                    for (int i = 0; i < 26; i++)   // a fan of gore flung ahead of the crest as it surges forward
                    {
                        float spread = (GD.Randf() - 0.5f) * 1.6f;
                        var dir = fwd.Rotated(Vector3.Up, spread);
                        var drop = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.18f + GD.Randf() * 0.28f, Height = 0.4f }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, MaterialOverride = Game.ToonEmissive(bcol, 1.6f, 0.03f) };
                        Game.I.AddChild(drop); drop.GlobalPosition = basePos + Vector3.Up * (GD.Randf() * 1.4f);
                        float reach = 6f + GD.Randf() * 14f;
                        var land = basePos + dir * reach; land.Y = 0.1f;
                        var dt2 = drop.CreateTween(); dt2.SetParallel(true);
                        dt2.TweenProperty(drop, "global_position", land, 0.35f + GD.Randf() * 0.25f).SetEase(Tween.EaseType.Out);
                        dt2.TweenProperty(drop, "transparency", 1f, 0.55f);
                        dt2.SetParallel(false);
                        dt2.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(drop)) drop.QueueFree(); }));
                    }
                    Ring(new Vector3(GlobalPosition.X, 0.05f, GlobalPosition.Z), bcol, 5f, 0.6f);
                }
                CamKick(0.8f);
                Game.I.Sfx?.Release(DamageType.Blood);
                Game.I.Sfx?.Thunder();
                Game.I.Hud?.Banner(ModTsunami ? "BLOOD TSUNAMI — RADIAL" : "BLOOD TSUNAMI");
                break;
            }
            case UltKind.Exsanguinate:
            {
                int t = UltTier;
                // (REWORK) now a CHANNELED TRANSFORM: for the duration she bleeds everyone in her aura with a rising DoT,
                // each kill POPS (nova) + heals her to FULL, and she keeps attacking normally. Works on bosses (Base-scaled).
                _exsang = true;
                _exsangRad = (12f + t * 2f) * (ModExsang ? 1.4f : 1f) * S.SpellArea;   // (MOD) Bloodthirst: bigger aura
                _exsangDps = Base() * (1.3f + t * 0.35f) * (ModExsang ? 1.25f : 1f);    // (MOD) …and a harder-hitting harvest
                _exsangTickT = 0f;
                UltActive = true; UltActiveT = 12f + t * 1.0f; UltMax = UltActiveT;
                GrantUltAura(DamageTypes.Col(DamageType.Blood), 3.0f);
                SetArm("draw", 0.6f);
                {
                    float rad = _exsangRad;
                    var bcol = DamageTypes.Col(DamageType.Blood);
                    Ring(GlobalPosition, bcol, rad, 0.7f);
                    Ring(GlobalPosition, bcol.Lerp(Colors.White, 0.35f), rad * 0.55f, 0.5f);
                    var v = new Vfx(); Game.I.AddChild(v); v.GlobalPosition = GlobalPosition + new Vector3(0, 1f, 0);
                    v.Init(new SphereMesh { Radius = rad * 0.6f, Height = rad * 1.2f }, bcol, 0.7f, 8f);
                    Game.I.SpawnGroundSigil(GlobalPosition, rad, bcol);          // a blood sigil lingers on the ground
                    Game.I.SpawnBloodMist(GlobalPosition, rad * 0.7f);           // a burst of gore

                    // a TOWERING, churning column of drawn blood erupts and writhes up out of her (~2.5s, not a half-second flash)
                    float ch = rad * 0.9f + 9f;
                    var col3 = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.7f, BottomRadius = 2.6f, Height = ch, RadialSegments = 16 }, MaterialOverride = Game.ElementBoltMat(bcol, DamageType.Blood), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
                    Game.I.AddChild(col3); col3.GlobalPosition = GlobalPosition + new Vector3(0, ch * 0.5f - 0.5f, 0);
                    col3.Scale = new Vector3(0.15f, 0.3f, 0.15f);
                    var ct = col3.CreateTween();
                    ct.TweenProperty(col3, "scale", new Vector3(1.25f, 1f, 1.25f), 0.3f).SetEase(Tween.EaseType.Out);   // erupt
                    ct.TweenProperty(col3, "scale", new Vector3(1.1f, 1f, 1.1f), 1.7f);                                 // writhe/settle
                    ct.TweenProperty(col3, "scale", new Vector3(0.04f, 1.25f, 0.04f), 0.7f).SetEase(Tween.EaseType.In); // thin out + draw up as it dissipates (additive shader → fade by scale, not alpha)
                    ct.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(col3)) col3.QueueFree(); }));
                    // a bright inner core so the column reads hot even from afar
                    var core = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.28f, BottomRadius = 1.0f, Height = ch, RadialSegments = 10 }, MaterialOverride = Game.ToonEmissive(bcol.Lerp(Colors.White, 0.35f), 2.8f, 0.03f), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
                    Game.I.AddChild(core); core.GlobalPosition = col3.GlobalPosition; core.Scale = new Vector3(0.15f, 0.3f, 0.15f);
                    var cot = core.CreateTween();
                    cot.TweenProperty(core, "scale", new Vector3(1f, 1f, 1f), 0.3f).SetEase(Tween.EaseType.Out);
                    cot.TweenInterval(1.7f);
                    cot.TweenProperty(core, "transparency", 1f, 0.7f);
                    cot.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(core)) core.QueueFree(); }));
                    // spiraling blood ribbons drawn up and around her — the siphoned life winding into the column
                    for (int rb = 0; rb < 12; rb++)
                    {
                        float a0 = rb / 12f * Mathf.Tau;
                        float rr = rad * 0.42f;
                        var start = GlobalPosition + new Vector3(Mathf.Cos(a0) * rr, ch * 0.32f, Mathf.Sin(a0) * rr);
                        var ribbon = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.34f, Height = ch * 0.7f, RadialSegments = 6 }, MaterialOverride = Game.ToonEmissive(bcol, 1.9f, 0.03f), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
                        Game.I.AddChild(ribbon); ribbon.GlobalPosition = start;
                        ribbon.RotationDegrees = new Vector3((GD.Randf() - 0.5f) * 40f, GD.Randf() * 360f, (GD.Randf() - 0.5f) * 40f);
                        var rt = ribbon.CreateTween(); rt.SetParallel(true);
                        rt.TweenProperty(ribbon, "global_position", start + new Vector3(0, ch * 0.5f, 0), 1.7f).SetEase(Tween.EaseType.Out).SetDelay(rb * 0.03f);
                        rt.TweenProperty(ribbon, "rotation_degrees", ribbon.RotationDegrees + new Vector3(0, 260f, 0), 1.7f).SetDelay(rb * 0.03f);
                        rt.TweenProperty(ribbon, "transparency", 1f, 1.3f).SetDelay(0.5f + rb * 0.03f);
                        rt.SetParallel(false);
                        rt.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(ribbon)) ribbon.QueueFree(); }));
                    }
                    Game.I.RisingWisps(GlobalPosition, rad * 0.8f, bcol, 32, ch * 0.6f);   // drained life ascending into the column
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
                float radBase = 11f + t * 1.5f + (ModRot ? 4f : 0f);   // raw — the GroundField auto-scales this by SpellArea
                float rad = radBase * S.SpellArea;                     // the direct bleed loop + ring + broadcast scale here
                float dps = Base() * (1.1f + t * 0.2f) * (ModRot ? 1.2f : 1f);   // (REWORK) more base damage (was 0.7+0.15t); mod ×1.2 on top
                float fieldDur = 14f + t;                              // (REWORK) initial field lasts 14+t s base
                UltLingerT = fieldDur; UltMax = fieldDur;             // no recharge until it fades; drives the HUD meter
                foreach (var e in Game.I.Enemies.ToArray())
                {
                    if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
                    if (Flat(e, GlobalPosition) > rad + e.Radius) continue;
                    e.Bleed(dps, fieldDur, true, 0, 1f, ModRot);   // (MOD) ModRot → the rot DoT PERSISTS on the foe until it dies (never auto-clears)
                }
                var f = new GroundField { Type = FieldType.Hex, Radius = radBase, Dur = fieldDur, Power = dps * 0.4f, FromCombo = true, DType = DamageType.Blood, TintColor = DamageTypes.Col(DamageType.Blood), RotDps = dps, RotPersist = ModRot };
                Game.I.AddChild(f); f.GlobalPosition = new Vector3(GlobalPosition.X, 0.04f, GlobalPosition.Z);
                Game.I.NetMgr?.BroadcastField((int)FieldType.Hex, new Vector3(GlobalPosition.X, 0.04f, GlobalPosition.Z), rad, fieldDur, false, DamageTypes.Col(DamageType.Blood), (int)DamageType.Blood);   // (NEW) allies see the rot field
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
                    Slams = 10 + t + (ModGuardian ? 2 : 0),      // (REWORK) more slams → the guardian stomps for ~13s+ base
                    SlamRadius = (7f + t * 0.8f + (ModGuardian ? 2f : 0f)) * S.SpellArea,
                    SlamDamage = Base() * (3.6f + t * 0.8f),     // (REWORK) buffed center value; tapers to ~40% at the edge
                    Poison = ModGuardian ? Base() * 0.2f : 0f,
                    RootOnSlam = ModGuardian
                };
                Game.I.AddChild(g); g.GlobalPosition = new Vector3(at.X, 0f, at.Z);
                ActiveGuardian = g;
                UltActive = true; UltActiveT = g.Slams * 1.35f; UltMax = UltActiveT;   // ~1.35s per stomp (SlamDur+RestDur); drives the HUD meter
                {
                    var ncol = DamageTypes.Col(DamageType.Nature);
                    Ring(new Vector3(at.X, 0.05f, at.Z), ncol, g.SlamRadius, 0.7f);
                    Ring(new Vector3(at.X, 0.05f, at.Z), ncol.Lerp(Colors.White, 0.3f), g.SlamRadius * 0.5f, 0.5f);
                    // roots heave up in a ring as the ancient wakes
                    for (int i = 0; i < 9; i++)
                    {
                        float a = i / 9f * Mathf.Tau, rr = 2.5f + GD.Randf() * 3f;
                        var rp = new Vector3(at.X + Mathf.Cos(a) * rr, 0f, at.Z + Mathf.Sin(a) * rr);
                        var root = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.35f, Height = 2.4f }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, MaterialOverride = Game.ToonEmissive(ncol, 0.8f, 0.05f) };
                        Game.I.AddChild(root); root.GlobalPosition = new Vector3(rp.X, -1f, rp.Z);
                        root.RotationDegrees = new Vector3((GD.Randf() - 0.5f) * 25f, GD.Randf() * 360f, (GD.Randf() - 0.5f) * 25f);
                        var rt = root.CreateTween();
                        rt.TweenProperty(root, "global_position", new Vector3(rp.X, 1.1f, rp.Z), 0.22f).SetEase(Tween.EaseType.Out).SetDelay(GD.Randf() * 0.2f);
                        rt.TweenInterval(0.5f); rt.TweenProperty(root, "transparency", 1f, 0.4f);
                        rt.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(root)) root.QueueFree(); }));
                    }
                    Game.I.FallingMotes(new Vector3(at.X, 0.05f, at.Z), g.SlamRadius, ncol, 20, 10f);   // leaves shaken loose drift down
                }
                CamKick(0.7f);
                Game.I.Sfx?.Release(DamageType.Nature);
                Game.I.Sfx?.Creak();
                Game.I.Hud?.Banner("ANCIENT GUARDIAN");
                break;
            }
            case UltKind.WildSwarm:
            {
                int t = UltTier;
                float sdur = LaunchStampede(t);
                UltActive = true; UltActiveT = sdur + 0.4f; UltMax = UltActiveT;   // active flag spans the stampede; drives the HUD meter
                {
                    var ncol = DamageTypes.Col(DamageType.Nature);
                    Vector3 fwd2 = -_cam.GlobalTransform.Basis.Z; fwd2.Y = 0; fwd2 = fwd2.Normalized();
                    // a churn of kicked-up earth & leaves as the herd breaks loose ahead of her
                    for (int i = 0; i < 22; i++)
                    {
                        float spread = (GD.Randf() - 0.5f) * 1.5f;
                        var dir = fwd2.Rotated(Vector3.Up, spread);
                        var puff = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.3f + GD.Randf() * 0.5f, Height = 0.7f }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, MaterialOverride = Game.ToonEmissive(ncol.Lerp(new Color(0.4f, 0.3f, 0.2f), 0.5f), 0.5f, 0.05f) };
                        Game.I.AddChild(puff); puff.GlobalPosition = new Vector3(GlobalPosition.X, 0.3f, GlobalPosition.Z) + dir * (2f + GD.Randf() * 2f);
                        var land = puff.GlobalPosition + dir * (5f + GD.Randf() * 8f) + Vector3.Up * (1f + GD.Randf());
                        var pt = puff.CreateTween(); pt.SetParallel(true);
                        pt.TweenProperty(puff, "global_position", land, 0.4f + GD.Randf() * 0.3f).SetEase(Tween.EaseType.Out);
                        pt.TweenProperty(puff, "transparency", 1f, 0.6f);
                        pt.SetParallel(false);
                        pt.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(puff)) puff.QueueFree(); }));
                    }
                    Ring(new Vector3(GlobalPosition.X, 0.05f, GlobalPosition.Z), ncol, 5f, 0.5f);
                }
                CamKick(0.6f);
                Game.I.Sfx?.Release(DamageType.Nature);
                Game.I.Hud?.Banner("WILD SWARM — STAMPEDE!");
                break;
            }
            case UltKind.Barkskin:
            {
                int t = UltTier;
                float dur = 15f + t * 1.0f + (ModBark ? 2.5f : 0f);   // (REWORK) base 15s
                GrantBark(dur);                                       // self + your own ents
                Game.I.NetMgr?.BroadcastBarkskin(dur);               // (REWORK) allies bark over too regardless of distance — broadcast, not radius; each barks their own ents locally
                Game.I.NetMgr?.HealAlliesNear(GlobalPosition, 99999f, S.MaxHp * 0.15f);   // (REWORK) heal ALL allies, however far
                Game.I.NetMgr?.BroadcastVfx(6, GlobalPosition, Vector3.Zero, 6f, 0f, DamageTypes.Col(DamageType.Nature));
                {
                    var ncol = DamageTypes.Col(DamageType.Nature);
                    Ring(GlobalPosition, ncol, 4.5f, 0.5f);
                    // slabs of bark heave up out of the earth and wrap over her in a tight shell
                    for (int i = 0; i < 8; i++)
                    {
                        float a = i / 8f * Mathf.Tau;
                        var plate = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.5f, 1.7f, 0.28f) }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, MaterialOverride = Game.ToonEmissive(ncol.Lerp(new Color(0.35f, 0.24f, 0.12f), 0.65f), 0.4f, 0.05f) };
                        Game.I.AddChild(plate);
                        var seat = GlobalPosition + new Vector3(Mathf.Cos(a) * 1.05f, 1.0f, Mathf.Sin(a) * 1.05f);
                        plate.GlobalPosition = GlobalPosition + new Vector3(Mathf.Cos(a) * 1.8f, -1.2f, Mathf.Sin(a) * 1.8f);
                        plate.LookAt(GlobalPosition + Vector3.Up * 1.0f, Vector3.Up);
                        var pt = plate.CreateTween();
                        pt.TweenProperty(plate, "global_position", seat, 0.22f).SetEase(Tween.EaseType.Out).SetDelay(GD.Randf() * 0.12f);
                        pt.TweenInterval(0.35f);
                        pt.TweenProperty(plate, "transparency", 1f, 0.45f);   // the shell "sets" then the tell fades; the shield visual carries the duration
                        pt.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(plate)) plate.QueueFree(); }));
                    }
                    Game.I.FallingMotes(GlobalPosition, 5f, ncol, 16, 8f);   // leaves shaken loose drift down
                }
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
                UltActive = true; UltActiveT = 10f + t * 1.0f + (ModHurricane ? 2f : 0f); UltMax = UltActiveT;   // (REWORK) base 10s
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
                {
                    var gp = new Vector3(GlobalPosition.X, _hurriBaseY + 0.05f, GlobalPosition.Z);
                    Ring(gp, col, radius * 0.6f, 0.5f);
                    Ring(gp, col.Lerp(Colors.White, 0.5f), 4f, 0.45f);
                    Game.I.SwirlDebris(gp, radius * 0.7f, col, 30, false, 9f);   // the downdraft blasts grit up-and-out as she launches
                }
                CamKick(0.7f);
                Game.I.Sfx?.Release(DamageType.Wind);
                Game.I.Sfx?.Thunder();
                Game.I.Hud?.Banner("HURRICANE");
                break;
            }
            case UltKind.Cyclone:
            {
                // drop a persistent tornado at the aim point that drags in and grinds enemies, then bursts.
                // Maelstrom mod (ModCyclone) makes it bigger, longer, and pull harder.
                int t = UltTier;
                Vector3 pos = GroundAim();
                float radius = (20f + t * 1.6f + (ModCyclone ? 8f : 0f)) * S.SpellArea;   // (BIG) a towering colossal tornado; Cyclone doesn't self-scale
                float dur = 12f + t * 1.0f + (ModCyclone ? 2f : 0f);   // (REWORK) base 12s
                UltLingerT = dur; UltMax = dur;   // the tornado grinds for `dur` s (UltActive is only a 1s flag); drives the HUD meter
                float dps = Base() * (2.6f + t * 0.5f);                    // (REWORK) more base damage; grinds bosses/single targets too
                var cy = new Cyclone();
                Game.I.AddChild(cy);
                cy.Init(this, pos, radius, dur, dps, ModCyclone, false, eatsProjectiles: true);   // (NEW) its wall eats enemy projectiles
                Game.I.NetMgr?.BroadcastVfx(11, pos, Vector3.Up, radius, dur, DamageTypes.Col(DamageType.Wind));  // allies see a visual-only twister
                {
                    var wcol = DamageTypes.Col(DamageType.Wind);
                    var gp = new Vector3(pos.X, 0.05f, pos.Z);
                    Game.I.SpawnGroundSigil(gp, radius, wcol);            // a wind sigil brands the ground under the funnel
                    Ring(gp, wcol, radius, 0.8f);
                    Ring(gp, wcol.Lerp(Colors.White, 0.4f), radius * 0.6f, 0.6f);
                    Ring(gp, wcol, radius * 1.3f, 1.0f);
                    Game.I.SwirlDebris(gp, radius, wcol, 52, true, 12f);   // everything loose is dragged into the throat and flung up
                }
                UltActive = true; UltActiveT = 1.0f;   // brief flag; the Cyclone node self-manages its lifetime
                CamKick(0.9f);
                Game.I.Sfx?.Release(DamageType.Wind);
                Game.I.Sfx?.Thunder();
                Game.I.Hud?.Banner("CYCLONE");
                break;
            }
            case UltKind.Stormform:
            {
                // (REWORK) mirrors Wildfire Rush: it grants a stock of WIND RUSH charges (fire with Q). Each is a maxed
                // wind-rush dash that leaves a ×3-speed WIND AREA + AIR MINES along the path. No heal, no flame trail.
                int t = UltTier;
                _windCharges = 2 + (t + 1) / 2 + (ModStorm ? 2 : 0);   // (NERF) base 2 (was 3); +2 charges with Eye of the Storm
                _windWindowT = 12f;                                     // window to spend them
                UltActive = true; UltActiveT = _windWindowT;            // HUD shows CHARGES (not a duration) for this ult
                GrantUltAura(DamageTypes.Col(DamageType.Wind), 2.4f);
                Ring(GlobalPosition, DamageTypes.Col(DamageType.Wind), 6f, 0.6f);
                Ring(GlobalPosition, DamageTypes.Col(DamageType.Wind).Lerp(Colors.White, 0.4f), 3.5f, 0.45f);
                CamKick(0.5f);
                Game.I.Sfx?.Release(DamageType.Wind);
                Game.I.Hud?.Banner($"STORMFORM — {_windCharges} wind rushes [Q]");
                break;
            }
            // ---- Frost witch ults (NEW) ----
            case UltKind.Blizzard:
            {
                int t = UltTier;
                Vector3 pos = GroundAim();
                float radius = (15f + t * 2.5f) * (ModBlizzard ? 1.4f : 1f) * S.SpellArea;   // (REWORK) larger base area
                float dur = 15f + t * 1.0f;                                                   // (REWORK) base 15s
                UltLingerT = dur; UltMax = dur;   // the blizzard field lingers `dur` s (UltActive is a 1s flag); drives the HUD meter
                float dps = Base() * (1.2f + t * 0.3f) * (ModBlizzard ? 1.35f : 1f);          // (REWORK) more base damage
                float freezeChance = ModBlizzard ? 1f : Mathf.Min(0.5f, 0.10f + t * 0.10f);   // Whiteout: icicles always freeze
                var bz = new Blizzard(); Game.I.AddChild(bz); bz.Init(this, pos, radius, dur, dps, freezeChance, false);
                Game.I.NetMgr?.BroadcastVfx(51, pos, Vector3.Up, radius, dur, DamageTypes.Col(DamageType.Frost));
                {
                    var fcol = DamageTypes.Col(DamageType.Frost);
                    Ring(new Vector3(pos.X, 0.05f, pos.Z), fcol, radius, 0.7f);
                    Ring(new Vector3(pos.X, 0.05f, pos.Z), fcol.Lerp(Colors.White, 0.5f), radius * 0.55f, 0.5f);
                    Game.I.FallingMotes(pos, radius, fcol.Lerp(Colors.White, 0.4f), 34, 15f);   // the sky whites out with driving snow
                }
                UltActive = true; UltActiveT = 1f;
                CamKick(0.6f); Game.I.Sfx?.Release(DamageType.Frost); Game.I.Hud?.Banner("BLIZZARD");
                break;
            }
            case UltKind.FrostElemental:
            {
                int t = UltTier;
                float dur = 11f + t * 1.0f;                                                    // (REWORK) base 11s
                UltLingerT = dur; UltMax = dur;   // the elemental fights for `dur` s (UltActive is a 1s flag); drives the HUD meter
                float size = (2.6f + t * 0.4f) * (ModFrostElem ? 1.4f : 1f) * S.SpellArea;
                float dmg = Base() * (1.5f + t * 0.35f);                                        // (REWORK) a bit more base damage
                var fe = new FrostElemental(); Game.I.AddChild(fe); fe.Init(this, GlobalPosition, size, dur, dmg, false, ModFrostElem);   // Avalanche: splits on melt
                Game.I.NetMgr?.BroadcastVfx(53, GlobalPosition, Vector3.Zero, size, dur, DamageTypes.Col(DamageType.Frost));
                {
                    // the golem coalesces out of the ground — shards of ice heave up and lock together where it forms
                    var fcol = DamageTypes.Col(DamageType.Frost);
                    var gp = new Vector3(GlobalPosition.X, 0.05f, GlobalPosition.Z);
                    Ring(gp, fcol.Lerp(Colors.White, 0.5f), size * 2.2f, 0.7f);
                    int shards = 10 + t;
                    for (int i = 0; i < shards; i++)
                    {
                        float a = i / (float)shards * Mathf.Tau, rr = size * (0.8f + GD.Randf() * 1.1f);
                        var sp = gp + new Vector3(Mathf.Cos(a) * rr, 0f, Mathf.Sin(a) * rr);
                        float h = 1.4f + GD.Randf() * (size * 0.9f);
                        var shard = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.28f + GD.Randf() * 0.22f, Height = h, RadialSegments = 5 }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, MaterialOverride = Game.ToonEmissive(fcol.Lerp(Colors.White, 0.35f), 1.1f, 0.06f) };
                        Game.I.AddChild(shard); shard.GlobalPosition = new Vector3(sp.X, -h, sp.Z);
                        shard.RotationDegrees = new Vector3((GD.Randf() - 0.5f) * 25f, GD.Randf() * 360f, (GD.Randf() - 0.5f) * 25f);
                        var st = shard.CreateTween();
                        st.TweenProperty(shard, "global_position", new Vector3(sp.X, h * 0.35f, sp.Z), 0.2f).SetEase(Tween.EaseType.Out).SetDelay(GD.Randf() * 0.18f);
                        st.TweenInterval(0.6f); st.TweenProperty(shard, "transparency", 1f, 0.5f);
                        st.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(shard)) shard.QueueFree(); }));
                    }
                    Game.I.FallingMotes(gp, size * 2f, fcol.Lerp(Colors.White, 0.4f), 20, 10f);
                }
                UltActive = true; UltActiveT = 1f;
                CamKick(0.7f); Game.I.Sfx?.Release(DamageType.Frost); Game.I.Sfx?.Freeze(GlobalPosition); Game.I.Hud?.Banner("FROST ELEMENTAL");
                break;
            }
            case UltKind.DeepFreeze:
            {
                // (REWORK) GLACIAL SUNDER — palms to the sky, huge jagged icicles erupt under the crowd: hard hit + fling-up,
                // then solid obstacles that radiate cold. Count/AoE/damage/freeze all scale with tiers; can crit; mod flash-freezes + shatters.
                int t = UltTier;
                Vector3 pos = GroundAim();
                float area = (12f + t * 2f) * S.SpellArea;
                float dur = 10f + t * 1.0f;
                UltLingerT = dur; UltMax = dur;   // the sunder keeps erupting spears for `dur` s; drives the HUD meter
                float thrust = Base() * (2.2f + t * 0.6f);     // emergence hit — hits hard, can crit
                float cold = Base() * (0.25f + t * 0.1f);      // per cold-tick, slight
                var df = new DeepFreeze(); Game.I.AddChild(df); df.Init(this, pos, area, dur, false, ModDeepFreeze, t, thrust, cold);
                Game.I.NetMgr?.BroadcastVfx(52, pos, Vector3.Up, area, dur, DamageTypes.Col(DamageType.Frost));
                {
                    var fcol = DamageTypes.Col(DamageType.Frost);
                    Ring(new Vector3(pos.X, 0.05f, pos.Z), fcol.Lerp(Colors.White, 0.5f), area, 0.8f);
                    Game.I.FallingMotes(pos, area, fcol.Lerp(Colors.White, 0.4f), 24, 13f);
                }
                SetArm("palmsup", 0.6f);
                UltActive = true; UltActiveT = 1f;
                CamKick(0.7f); Game.I.Sfx?.Freeze(pos); Game.I.Sfx?.Release(DamageType.Frost); Game.I.Hud?.Banner("GLACIAL SUNDER");
                break;
            }
            // ---- Forsaken witch ults (NEW) ----
            case UltKind.HexCircle:
            {
                int t = UltTier;
                float radius = (12f + t * 0.8f) * (ModPlague ? 1.4f : 1f) * S.SpellArea;
                _hexGroup = ++_curseGroupSeq;                 // one shared mega-group so damage cascades across everyone inside
                UltActive = true; UltActiveT = 15f + t * 1.0f; UltMax = UltActiveT;   // (REWORK) base 15s + HUD meter
                _hexTickT = 0f; _hexNetT = 0f;
                if (_hexVfx != null && GodotObject.IsInstanceValid(_hexVfx)) _hexVfx.QueueFree();
                _hexVfx = BuildHexField(radius); Game.I.AddChild(_hexVfx);
                _hexVfx.GlobalPosition = new Vector3(GlobalPosition.X, 0.05f, GlobalPosition.Z);
                {
                    var ccol = DamageTypes.Col(DamageType.Curse);
                    Game.I.SpawnGroundSigil(GlobalPosition, radius, ccol);   // a hex sigil brands the ground
                    Ring(GlobalPosition, ccol, radius, 0.7f);
                    Ring(GlobalPosition, ccol.Lerp(Colors.White, 0.35f), radius * 0.55f, 0.5f);
                    CurseImplosion(GlobalPosition + Vector3.Up * 1.2f, ccol, 2.2f);   // a dark heart collapses inward as the circle bites
                    Game.I.RisingWisps(new Vector3(GlobalPosition.X, 0.05f, GlobalPosition.Z), radius, ccol, 34, 7f);   // souls torn loose all across the ring
                }
                Game.I.NetMgr?.BroadcastVfx(59, GlobalPosition, Vector3.Zero, radius, UltActiveT, DamageTypes.Col(DamageType.Curse));
                CamKick(0.5f); Game.I.Sfx?.CurseCrush(GlobalPosition); Game.I.Hud?.Banner("HEX CIRCLE");
                break;
            }
            case UltKind.LifeDrain:
            {
                int t = UltTier;
                float radius = (11f + t * 1.5f) * S.SpellArea;
                UltActive = true; UltActiveT = 13f + t * 1.0f; UltMax = UltActiveT;   // (REWORK) base 13s flight-channel + HUD meter
                _drainBank = 0f; _drainBaseY = GlobalPosition.Y; _drainTickT = 0f; _drainNetT = 0f;
                _grounded = false; _vy = 0f; _noFall = Mathf.Max(_noFall, 1f);   // she takes to the air for the channel
                if (_drainVfx != null && GodotObject.IsInstanceValid(_drainVfx)) _drainVfx.QueueFree();
                _drainVfx = BuildDrainAura(radius); AddChild(_drainVfx);   // parented to her → the aura rides along as she flies
                {
                    var ccol = DamageTypes.Col(DamageType.Curse);
                    Game.I.SpawnGroundSigil(GlobalPosition, radius, ccol);
                    Ring(GlobalPosition, ccol, radius, 0.6f);
                    Ring(GlobalPosition, ccol.Lerp(Colors.White, 0.35f), radius * 0.5f, 0.5f);
                    Game.I.RisingWisps(new Vector3(GlobalPosition.X, 0.05f, GlobalPosition.Z), radius, ccol, 40, 9f);   // life itself is dragged up out of the field toward her
                }
                Game.I.NetMgr?.BroadcastVfx(60, GlobalPosition, Vector3.Zero, radius, UltActiveT, DamageTypes.Col(DamageType.Curse));
                CamKick(0.6f); Game.I.Sfx?.CurseCrush(GlobalPosition); Game.I.Hud?.Banner("LIFE DRAIN — Space/Ctrl to fly, drain then release");
                break;
            }
            case UltKind.LifeCurse:
                StartSpecter();   // (REWORK) SPECTER — immaterial projection: heal, ×3 speed, untouchable; release a %HP nova on retrigger/timeout
                break;
            case UltKind.MeteorDescent:
            {
                UltActive = true; UltActiveT = 5f; UltMax = 5f;   // (REWORK) aim window drives the HUD duration meter
                _meteorAscend = true; _meteorAscendT = 5f; _meteorBaseY = GlobalPosition.Y;
                _meteorRainLeft = 3 + UltTier + (ModMeteorDesc ? 3 : 0);   // (REWORK) meteors rain at random while she's aloft (+3 with Extinction Event); scales with tiers
                _meteorRainT = 0.4f;
                _grounded = false; _vy = 0f; _noFall = 999f; _iframe = 999f;   // rise, invulnerable, no fall damage until the slam
                EndFlameCone();
                {
                    var ecol = DamageTypes.Col(DamageType.Ember);
                    var feet = new Vector3(GlobalPosition.X, GlobalPosition.Y - 0.4f, GlobalPosition.Z);
                    Ring(feet, ecol, 6f, 0.6f);
                    Ring(feet, ecol.Lerp(Colors.White, 0.4f), 3.5f, 0.5f);
                    // a roaring column of fire hurls her aloft
                    var column = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 1.4f, BottomRadius = 2.4f, Height = 16f }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, MaterialOverride = Game.ToonEmissive(ecol, 2.4f, 0.03f) };
                    Game.I.AddChild(column); column.GlobalPosition = feet + Vector3.Up * 7f;
                    column.Scale = new Vector3(0.2f, 1f, 0.2f);
                    var colt = column.CreateTween(); colt.SetParallel(true);
                    colt.TweenProperty(column, "scale", new Vector3(1.3f, 1f, 1.3f), 0.2f).SetEase(Tween.EaseType.Out);
                    colt.TweenProperty(column, "transparency", 1f, 0.6f);
                    colt.SetParallel(false);
                    colt.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(column)) column.QueueFree(); }));
                    Game.I.RisingWisps(feet, 6f, ecol, 30, 14f);   // embers stream up in her wake
                    Game.I.SpawnEmberBurst(feet + Vector3.Up * 0.5f, 5f);
                }
                Game.I.NetMgr?.BroadcastVfx(68, GlobalPosition, Vector3.Up, 0f, 5f, DamageTypes.Col(DamageType.Ember));   // allies see her launch skyward
                CamKick(0.7f); Game.I.Sfx?.ChargeUp(DamageType.Ember); Game.I.Sfx?.Thunder(); Game.I.Hud?.Banner("METEOR DESCENT — aim, then drop");
                break;
            }
            case UltKind.WildfireRush:
            {
                int t = UltTier;
                UltActive = true; UltActiveT = 10f;
                _flameDashCharges = 3 + (t + 1) / 2 + (ModWildfire ? 2 : 0);   // 3 → 5 dashes across tiers (+2 with Firestorm)
                _flameDashWindowT = 10f;
                BurnLifestealT = 16f;                            // her burn ticks heal her while the trails burn
                {
                    var ecol = DamageTypes.Col(DamageType.Ember);
                    Ring(GlobalPosition, ecol, 5f, 0.5f);
                    Ring(GlobalPosition, ecol.Lerp(Colors.White, 0.4f), 3f, 0.4f);
                    // she ignites — jets of flame lick up around her feet as the rush kindles
                    for (int i = 0; i < 8; i++)
                    {
                        float a = i / 8f * Mathf.Tau, rr = 0.9f + GD.Randf() * 0.9f;
                        var jet = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.3f, Height = 1.6f + GD.Randf() * 1.2f }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, MaterialOverride = Game.ToonEmissive(ecol, 2.2f, 0.03f) };
                        Game.I.AddChild(jet);
                        jet.GlobalPosition = GlobalPosition + new Vector3(Mathf.Cos(a) * rr, 0.3f, Mathf.Sin(a) * rr);
                        jet.Scale = new Vector3(1f, 0.1f, 1f);
                        var jt = jet.CreateTween(); jt.SetParallel(true);
                        jt.TweenProperty(jet, "scale", new Vector3(1f, 1.2f, 1f), 0.16f).SetEase(Tween.EaseType.Out).SetDelay(GD.Randf() * 0.1f);
                        jt.TweenProperty(jet, "transparency", 1f, 0.5f);
                        jt.SetParallel(false);
                        jt.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(jet)) jet.QueueFree(); }));
                    }
                    Game.I.RisingWisps(GlobalPosition, 4f, ecol, 20, 6f);   // embers curl up off her
                }
                Game.I.NetMgr?.BroadcastVfx(69, GlobalPosition, Vector3.Zero, 5f, 0f, DamageTypes.Col(DamageType.Ember));
                CamKick(0.4f); Game.I.Sfx?.Release(DamageType.Ember); Game.I.Hud?.Banner($"WILDFIRE RUSH — {_flameDashCharges} flame dashes [Q]");
                break;
            }
            case UltKind.PhoenixAscend:
            {
                // (FULL REWORK) hurl a giant flaming phoenix at the cursor: it pierces + burns a line, GRABS every non-boss
                // it touches, banks skyward carrying them ~45u, then detonates — grabbed foes take heavy dmg + are flung,
                // grazed bosses detonate in place for a capped %HP. Host simulates; every machine flies the visual bird.
                int t = UltTier;
                Vector3 aim = GroundAim();
                Vector3 dir = new Vector3(aim.X - GlobalPosition.X, 0f, aim.Z - GlobalPosition.Z);
                if (dir.LengthSquared() < 0.01f) { dir = -GlobalTransform.Basis.Z; dir.Y = 0f; }
                dir = dir.Normalized();
                Vector3 origin = GlobalPosition + dir * 1.2f + Vector3.Up * 0.6f;
                float touchDmg = Base() * (1.2f + t * 0.3f) * ComboMul();       // pierce hit — scales with tiers
                float grabDmg = Base() * (6f + t * 2f) * ComboMul();            // skyburst on grabbed foes — hits HARD
                float bossFrac = 0.10f + t * 0.03f;                            // grazed bosses lose this % max HP (capped)
                Game.I.FirePhoenixDive(this, origin, dir, t, ModPhoenix, touchDmg, grabDmg, bossFrac, Base());
                SetArm("thrust", 0.5f);
                _phoenix = false; _phoenixRebirth = false;   // not a transform anymore — she stays grounded and casts normally
                UltActive = true; UltActiveT = 4.5f; UltMax = 0f;   // brief ACTIVE window (gates recharge while the bird flies); no duration bar
                Ring(GlobalPosition, DamageTypes.Col(DamageType.Ember), 6f, 0.6f);
                Game.I.FallingMotes(GlobalPosition, 6f, DamageTypes.Col(DamageType.Ember), 20, 9f);
                CamKick(0.7f); Game.I.Sfx?.Release(DamageType.Ember); Game.I.Sfx?.Thunder(); Game.I.Hud?.Banner("PHOENIX ASCENDANT");
                break;
            }
            // ---- Arcane witch ults (NEW) ----
            case UltKind.ArcaneAscend:
            {
                int t = UltTier;
                UltActive = true; UltActiveT = (12f + t * 1.5f) * (ModArcStorm ? 1.35f : 1f); UltDmgMul = 2f; UltMax = UltActiveT;   // a long transformation channel (Storm Incarnate: longer) + duration meter
                _arcaneAscend = true; _arcaneAscendFireT = 0f;
                var acol = DamageTypes.Col(DamageType.Arcane); Vector3 gp = GlobalPosition;
                _grounded = false; _vy = 22f; _noFall = 999f;   // an arcane bolt erupts her skyward
                float lr = (5f + t * 0.6f) * S.SpellArea, ldmg = Base() * (3f + t * 0.8f) * ComboMul();   // launch blast — scales with AoE
                foreach (var e in Game.I.Enemies.ToArray())
                {
                    if (e == null || e.Dead || !GodotObject.IsInstanceValid(e) || Flat(e, gp) > lr + e.Radius) continue;
                    bool crit = RollCrit(); float d = ldmg; if (crit) d *= CritMult();
                    e.Hurt(d, DamageType.Arcane, true, crit); OnHitDirect(e, e.Dead, d, DamageType.Arcane, crit);
                }
                Game.I.DamageWorld(gp, lr, ldmg);
                Game.I.SpawnArcaneRupture(gp + Vector3.Up * 0.5f, lr);
                Game.I.SpawnGroundSigil(gp, lr, acol);
                Game.I.SpawnArcaneKamehameha(gp, Vector3.Up, 16f, 1.8f, acol);   // a raw-arcane column erupts skyward at the launch
                Game.I.NetMgr?.BroadcastVfx(79, gp + Vector3.Up * 0.5f, Vector3.Zero, lr, 0f, acol);
                float sy = Game.I.SurfaceHeight(gp, gp.Y);
                GlobalPosition = new Vector3(gp.X, sy + 8f, gp.Z);   // …then she rides it up into the sky
                if (_arcaneAura != null && GodotObject.IsInstanceValid(_arcaneAura)) _arcaneAura.QueueFree();
                _arcaneAura = new ArcaneAura(); AddChild(_arcaneAura); _arcaneAura.Init(2.9f, 0.08f);   // transformation aura rides her
                SetArm("conjure", 0.7f); CamKick(1.4f); Game.I.Sfx?.ChargeUp(DamageType.Arcane); Game.I.Sfx?.ArcaneBlast(gp); Game.I.Sfx?.Thunder(); Game.I.Hud?.Banner("ARCANE ASCENSION — Space/Ctrl to fly, LMB lightning");
                break;
            }
            case UltKind.ArcaneEruption:
            {
                int t = UltTier;
                float radiusB = (13f + t * 2f) * (ModArcCataclysm ? 1.4f : 1f), radius = radiusB * S.SpellArea;   // (REWORK) bigger base; grows with AoE cards + tier (Cataclysm: bigger)
                float centerDmg = Base() * (7f + t * 2f) * ComboMul();      // (REWORK) stronger base core
                float edgeDmg = Base() * (2f + t * 0.5f) * ComboMul();      // (REWORK) stronger base rim
                var col = DamageTypes.Col(DamageType.Arcane); var here = GlobalPosition;
                foreach (var e in Game.I.Enemies.ToArray())
                {
                    if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
                    float d = Flat(e, here); if (d > radius + e.Radius) continue;
                    float tt = Mathf.Clamp(d / radius, 0f, 1f);
                    float dm = Mathf.Lerp(centerDmg, edgeDmg, tt);            // more damage the closer to her
                    bool crit = RollCrit(); if (crit) dm *= CritMult();
                    e.Hurt(dm, DamageType.Arcane, true, crit); OnHitDirect(e, e.Dead, dm, DamageType.Arcane, crit);
                    if (!e.Dead)   // survivors flung back + knocked up, harder near the center (host-authoritative)
                    {
                        float close = 1f - tt;
                        Vector3 outw = e.GlobalPosition - here; outw.Y = 0f; outw = outw.LengthSquared() > 0.01f ? outw.Normalized() : Vector3.Forward;
                        e.Fling(outw * ((9f + t) * (0.5f + close)) + Vector3.Up * ((8f + t) * (0.5f + close)));
                        // (FIX) bosses + heavy foes shrug off the fling (Enemy.Fling barely budges them / no-ops on bosses),
                        // so hit THEM with a hard horizontal KNOCKBACK instead — scales with the ult's tier + proximity.
                        if (!e.Flingable) e.Knockback(here, (10f + t * 2.5f) * (0.55f + 0.45f * close));
                    }
                }
                Game.I.DamageWorld(here, radius, centerDmg);
                Ring(here, col, radius, 0.7f); Ring(here, col.Lerp(Colors.White, 0.5f), radius * 0.5f, 0.5f); Ring(here, col, radius * 1.35f, 0.95f);
                Game.I.SpawnArcaneRupture(here + Vector3.Up * 0.6f, radius);
                Game.I.SpawnGroundSigil(here, radius, col);
                for (int i = 0; i < 10; i++)   // a crown of raw-arcane pillars heaves up around the blast
                {
                    float a = i / 10f * Mathf.Tau + GD.Randf();
                    var pp = here + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * radius * (0.3f + GD.Randf() * 0.6f);
                    Game.I.SpawnArcaneKamehameha(new Vector3(pp.X, Game.I.SurfaceHeight(pp, here.Y), pp.Z), Vector3.Up, 6f + GD.Randf() * 5f, 0.5f, col);
                }
                float efDur = (ModArcCataclysm ? 8f : 5f) + t;   // (REWORK) the field lingers a bit longer, scales with tiers
                var ef = new GroundField { Type = FieldType.Hex, Radius = radiusB, Dur = efDur, Power = Base() * (0.9f + t * 0.15f) * (ModArcCataclysm ? 1.5f : 1f), DType = DamageType.Arcane, TintColor = col, FromCombo = true, SlowMul = 0.6f };
                Game.I.AddChild(ef); ef.GlobalPosition = new Vector3(here.X, 0.05f, here.Z);   // lingering unstable field keeps grinding
                UltLingerT = efDur; UltMax = efDur;   // (REWORK) the unstable field grinds for its duration (UltActive is a 0.6s flag) — duration meter + no recharge until it fades
                if (ModArcCataclysm) SpawnArcaneRift(here, radius * 0.85f, centerDmg * 0.7f);   // Cataclysm: a second delayed shockwave (~1s later)
                UltActive = true; UltActiveT = 0.6f;
                SetArm("conjure", 0.6f); CamKick(1.5f); Game.I.Sfx?.ArcaneBlast(here); Game.I.Sfx?.Thunder(); Game.I.Hud?.Banner("ARCANE ERUPTION");
                break;
            }
            case UltKind.ArcaneOvercharge:
            {
                // (FULL REWORK) ARCANE STORM — a large field at the cursor that rains arcane bolts on any foe inside.
                // Bolts scale with the target's max HP (capped for bosses), can crit with her passive, and each foe can
                // be struck once per second. Lingers 13s base (+tier). Host simulates; every machine renders the storm.
                int t = UltTier;
                Vector3 pos = GroundAim(); pos = new Vector3(pos.X, Game.I.SurfaceHeight(pos, pos.Y), pos.Z);
                float radius = (16f + t * 2f) * (ModArcUnbound ? 1.4f : 1f) * S.SpellArea;   // Singularity: bigger storm
                float dur = 13f + t;
                float baseDmg = Base() * (1.0f + t * 0.25f);         // per bolt
                float hpScale = 0.03f + t * 0.008f;                  // + this fraction of the target's max HP per bolt
                float bossCapMul = 2.5f + t * 0.3f;                  // bosses: hp-bonus capped at base×this (strong but bounded)
                Game.I.FireArcaneStorm(this, pos, radius, dur, ModArcUnbound, t, baseDmg, hpScale, bossCapMul, CritChanceNow, CritMultPublic());
                UltActive = true; UltActiveT = 1f; UltLingerT = dur; UltMax = dur;   // brief cast flag + the field-linger duration meter (no recharge until it fades)
                var ocol = DamageTypes.Col(DamageType.Arcane);
                Ring(pos, ocol, radius, 0.7f);
                Ring(pos, ocol.Lerp(Colors.White, 0.4f), radius * 0.55f, 0.5f);
                Game.I.FallingMotes(pos, radius, ocol, 30, 15f);
                SetArm("conjure", 0.6f); CamKick(0.9f); Game.I.Sfx?.ChargeUp(DamageType.Arcane); Game.I.Sfx?.ArcaneBlast(pos); Game.I.Sfx?.Thunder(); Game.I.Hud?.Banner("ARCANE STORM");
                break;
            }
        }
    }

    // Arcane ULT 1 — Ascension: free flight (Space up / Ctrl down); LMB rains massive chain-lightning that hits several
    // foes at once and arcs to their neighbours; crits heal (passive) and kills lightly heal (this ult).
    private void UpdateArcaneAscend(float dt)
    {
        if (Downed || !Game.I.SimActive) { EndArcaneAscend(); return; }
        Floating = false;
        Vector3 dir = InputDir();
        Vector3 np = (dir != Vector3.Zero) ? GlobalPosition + dir * (S.Speed * 1.1f) * dt : GlobalPosition;
        float vy = 0f;
        if (Input.IsActionPressed("jump")) vy += 11f;
        if (Input.IsActionPressed("descend")) vy -= 11f;
        float ny = GlobalPosition.Y + vy * dt;
        float floor = Game.I.SurfaceHeight(np, GlobalPosition.Y) + 3f;   // stays aloft
        if (ny < floor) ny = floor;
        GlobalPosition = ClampPos(new Vector3(np.X, ny, np.Z));
        _grounded = false; _vy = 0f; _noFall = 1f;
        _arcaneAscendFireT -= dt;
        if (Input.IsActionPressed("cast") && _arcaneAscendFireT <= 0f) { _arcaneAscendFireT = Mathf.Max(0.14f, S.FireCd) * 1.15f; FireArcaneUltLightning(); }
        UltActiveT -= dt;
        if (UltActiveT <= 0f) EndArcaneAscend();
    }
    private void EndArcaneAscend() { _arcaneAscend = false; UltActive = false; UltDmgMul = 1f; _noFall = 3f; _iframe = Mathf.Max(_iframe, 0.3f); if (_arcaneAura != null && GodotObject.IsInstanceValid(_arcaneAura)) { _arcaneAura.QueueFree(); _arcaneAura = null; } }

    private void FireArcaneUltLightning()
    {
        Vector3 eye = EyePos, aim = AimDir();
        var col = DamageTypes.Col(DamageType.Arcane);
        float range = 40f * S.SpellRange, dmg = Base() * (1.6f + UltTier * 0.4f) * ComboMul() * ArcanePowerMul;   // UltDmgMul=2 already doubles Base while aloft
        float healKill = S.MaxHp * (0.02f + UltTier * 0.006f);
        int maxTargets = 3 + UltTier + (ModArcStorm ? 2 : 0);   // Storm Incarnate: strikes more foes at once
        Vector3 start = (_handMeshR != null && GodotObject.IsInstanceValid(_handMeshR)) ? _handMeshR.GlobalPosition : eye + aim * 0.5f;
        var cand = new System.Collections.Generic.List<(float a, Enemy e)>();
        foreach (var e in Game.I.Enemies.ToArray())
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
            var to = e.GlobalPosition + Vector3.Up * e.Radius * 0.5f - eye; float d = to.Length(); if (d > range) continue;
            if (aim.Dot(to / Mathf.Max(d, 0.01f)) < 0.55f) continue;   // ~57° forward cone
            cand.Add((aim.Dot(to / Mathf.Max(d, 0.01f)), e));
        }
        cand.Sort((x, y) => y.a.CompareTo(x.a));
        int hit = 0;
        void Zap(Vector3 from, Enemy e, float coeff)
        {
            var ep = e.GlobalPosition + Vector3.Up * e.Radius * 0.5f;
            bool crit = RollCrit(); float d = dmg * coeff; if (crit) d *= CritMult();
            bool wasAlive = !e.Dead;
            e.Hurt(d, DamageType.Arcane, true, crit); OnHitDirect(e, e.Dead, d, DamageType.Arcane, crit);
            if (wasAlive && e.Dead && !Downed) Heal(healKill);   // kills lightly heal her
            Game.I.SpawnArcaneLightning(new System.Collections.Generic.List<Vector3> { from, ep }, 1f);
            Game.I.NetMgr?.BroadcastVfx(78, from, (ep - from).Normalized(), (ep - from).Length(), 0f, col);
        }
        foreach (var (_, e) in cand)
        {
            if (hit >= maxTargets) break; hit++;
            Zap(start, e, 1f);
            int arcs = ModArcStorm ? 2 : 1; Enemy prev = e;   // arc to neighbour(s) — Storm Incarnate doubles the arcs
            for (int ai = 0; ai < arcs && prev != null; ai++)
            {
                Enemy near = null; float nd = 8f * S.SpellRange;
                foreach (var o in Game.I.Enemies.ToArray())
                {
                    if (o == null || o.Dead || o == e || o == prev || !GodotObject.IsInstanceValid(o)) continue;
                    float dd = o.GlobalPosition.DistanceTo(prev.GlobalPosition); if (dd < nd) { nd = dd; near = o; }
                }
                if (near == null) break;
                Zap(prev.GlobalPosition + Vector3.Up * prev.Radius * 0.5f, near, 0.7f - ai * 0.15f);
                prev = near;
            }
        }
        if (hit > 0) { _kickR = 1; FireHeat = Mathf.Min(1f, FireHeat + 0.08f); Game.I.Sfx?.Cast(DamageType.Arcane); }
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
                e.AddCurse(0.6f, _hexGroup, CurseBonusType, CurseBonusMul, CurseShareFrac, CurseBonusType2);   // ~2.4 stacks/s and fold into the mega-group so shared damage cascades
                e.Hurt(Base() * (0.22f + t * 0.06f), DamageType.Curse, true);                 // (REWORK) the ring itself gnaws — more base dps, scales with tiers
                if (ModPlague) e.Hurt(Base() * (0.32f + t * 0.08f), DamageType.Curse, true);  // Plaguebearer: the ring also festers harder
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
        _drainTickT -= dt; bool tick = _drainTickT <= 0f;
        var col = DamageTypes.Col(DamageType.Curse);
        if (tick)   // (PERF) ALL per-enemy work — damage AND the tether rebuild — now runs only on the 0.1s tick, not every frame; and the tether nodes are capped. Was: ~100 node create+free PER FRAME in a swarm for the whole channel.
        {
            _drainTickT = 0.1f;
            ClearDrainLinks();
            float bankCap = Base() * (6f + t * 1.5f);
            int links = 0;
            foreach (var e in Game.I.Enemies.ToArray())
            {
                if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
                if (Flat(e, GlobalPosition) > radius + e.Radius) continue;
                float drain = Base() * (1.1f + t * 0.2f) * 0.1f * ComboMul();   // (REWORK) more damage/sec, scales with tiers — damage for this 0.1s tick
                e.Hurt(drain, DamageType.Curse, true);
                float heal = drain * 0.5f;                          // lifesteal: heal half of what she drains…
                Heal(heal);
                _drainBank = Mathf.Min(_drainBank + heal, bankCap);  // …and bank it as the release payload (capped)
                if (links < 20) { links++; _drainLinks.Add(Game.I.SpawnCurseLink(GlobalPosition, e.GlobalPosition + Vector3.Up * e.Radius * 0.5f, col)); }   // (PERF) cap the visible tethers
            }
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

    // LifeCurse (REWORK) — SPECTER: the witch tears loose as an immaterial violet projection. For up to 10s she is
    // untouchable (immune to all damage + CC via FullyImmune), cannot attack, moves at ×3 speed and knits her wounds
    // shut (a full 9s heals to 100%). When she retriggers Q, or the 10s elapses, the projection collapses: a ~30u nova
    // at her final position rips a %-of-max-HP chunk out of every foe (capped for bosses but still brutal) and flings
    // them outward — both scaling with tiers. Legendary mod (Soul Harrow): drifting THROUGH a foe while immaterial
    // saddles it with a %-max-HP curse DoT. Host simulates the burst/DoT; allies see the recolour via vitals bit 11.
    private void StartSpecter()
    {
        int t = UltTier;
        _specter = true; _specterT = 10f; _specterNetT = 0f; _specterDotT = 0f;
        UltActive = true; UltActiveT = 10f; UltMax = 10f;
        CleanseNegative();                    // shed any root/slow/stun as she phases out of the world
        _bodyModel?.SetSpectral(true);
        var ccol = DamageTypes.Col(DamageType.Curse);
        if (_specterVfx != null && GodotObject.IsInstanceValid(_specterVfx)) _specterVfx.QueueFree();
        _specterVfx = BuildSpecterAura(); AddChild(_specterVfx);   // rides along with her as she drifts
        Ring(GlobalPosition, ccol, 5f, 0.6f);
        Ring(GlobalPosition, ccol.Lerp(Colors.White, 0.35f), 2.6f, 0.5f);
        CurseImplosion(GlobalPosition + Vector3.Up * 1.2f, ccol, 2.2f);
        Game.I.RisingWisps(new Vector3(GlobalPosition.X, 0.05f, GlobalPosition.Z), 5f, ccol, 26, 7f);
        SetArm("together", 0.5f);
        CamKick(0.6f); Game.I.Sfx?.CurseCrush(GlobalPosition);
        Game.I.Hud?.Banner("SPECTER — Q to release the nova");
    }

    private Node3D BuildSpecterAura()   // a faint violet projection-shell around her while immaterial (parented to her)
    {
        var col = DamageTypes.Col(DamageType.Curse);
        var root = new Node3D();
        var shell = new MeshInstance3D { Mesh = new SphereMesh { Radius = 1.4f, Height = 2.8f } };
        var sm = Game.ToonEmissive(col, 1.3f, 0f); sm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; sm.AlbedoColor = new Color(col.R, col.G, col.B, 0.14f);
        shell.MaterialOverride = sm; shell.Position = new Vector3(0, 1.0f, 0); root.AddChild(shell);
        root.AddChild(new OmniLight3D { OmniRange = 4.5f, LightColor = col, LightEnergy = 2.4f, Position = new Vector3(0, 1.2f, 0), ShadowEnabled = false });
        return root;
    }

    // immaterial drift: heal toward full, curse-DoT anything she passes through (mod), count down to the release nova
    private void UpdateSpecter(float dt)
    {
        _specterT -= dt;
        Heal(S.MaxHp * dt / 9f);          // knit her wounds — a full 9s of drift = back to 100%
        _bodyModel?.ShowWings(true);
        if (ModRite)                       // Soul Harrow: foes she drifts through take a %-max-HP curse DoT
        {
            _specterDotT -= dt;
            if (_specterDotT <= 0f)
            {
                _specterDotT = 0.25f;
                float pr = 2.6f;
                foreach (var e in Game.I.Enemies.ToArray())
                {
                    if (e == null || e.Dead || !GodotObject.IsInstanceValid(e) || e.Remote) continue;
                    if (Flat(e, GlobalPosition) > pr + e.Radius) continue;
                    float frac = e.IsBoss ? 0.006f : 0.02f;   // per 0.25s tick (capped for bosses)
                    e.Hurt(frac * e.MaxHp, DamageType.Curse, true);
                }
            }
        }
        _specterNetT -= dt;
        if (_specterNetT <= 0f) { _specterNetT = 0.25f; Game.I.NetMgr?.BroadcastVfx(60, GlobalPosition + Vector3.Up * 0.8f, Vector3.Zero, 2.5f, 0.2f, DamageTypes.Col(DamageType.Curse)); }
        if (_specterT <= 0f) EndSpecter(false);
    }

    // the projection collapses — a %-HP nova + outward fling at her final position (retrigger = early release)
    private void EndSpecter(bool retrig)
    {
        if (!_specter) return;
        _specter = false; UltActive = false; UltActiveT = 0f;
        int t = UltTier;
        _bodyModel?.SetSpectral(false); _bodyModel?.ShowWings(false);
        if (_specterVfx != null && GodotObject.IsInstanceValid(_specterVfx)) { _specterVfx.QueueFree(); _specterVfx = null; }
        var col = DamageTypes.Col(DamageType.Curse);
        float radius = (30f + t * 4f) * S.SpellArea;
        float frac = 0.18f + t * 0.05f;               // % max HP torn out — scales with tiers
        float bossFrac = 0.06f + t * 0.02f;           // capped but still strong vs bosses
        float fling = 16f + t * 3f;                   // outward launch — scales with tiers
        SetArm("crush", 0.5f);
        int bursts = 0;
        foreach (var e in Game.I.Enemies.ToArray())
        {
            if (e == null || e.Dead || !GodotObject.IsInstanceValid(e) || e.Remote) continue;
            var d = e.GlobalPosition - GlobalPosition; d.Y = 0f;
            if (d.Length() > radius + e.Radius) continue;
            float useFrac = e.IsBoss ? bossFrac : frac;
            e.Hurt(useFrac * e.MaxHp, DamageType.Curse, true);
            if (!e.Dead)
            {
                var dir = d.LengthSquared() > 0.01f ? d.Normalized() : Vector3.Forward;
                e.Fling(dir * fling + Vector3.Up * 6f);
            }
            if (bursts < 24) { bursts++; Game.I.VfxRing(e.GlobalPosition + Vector3.Up * 0.9f, col, 2.2f, 0.4f); }
        }
        Game.I.DamageWorld(GlobalPosition, radius, frac * 80f);
        Game.I.SpawnGroundSigil(GlobalPosition, radius, col);
        Ring(GlobalPosition, col, radius, 0.85f);
        Ring(GlobalPosition, col.Lerp(Colors.White, 0.4f), radius * 0.6f, 0.5f);
        Game.I.FallingMotes(GlobalPosition, radius, col, 32, 12f);
        CurseImplosion(GlobalPosition + Vector3.Up * 1.2f, col, 2.6f);
        Game.I.RisingWisps(GlobalPosition, radius * 0.85f, col, 36, 10f);
        Game.I.NetMgr?.BroadcastVfx(61, GlobalPosition, Vector3.Zero, radius, 0f, col);
        CamKick(1.3f); Game.I.Sfx?.CurseCrush(GlobalPosition); Game.I.Sfx?.Thunder(); Game.I.PlayerSound(GlobalPosition, 2.2f);
        Game.I.Hud?.Banner(retrig ? "SPECTER RELEASE" : "SPECTER — RELEASE");
    }

    // ===== Attunement live-spend (per-run perk activation) =====
    // Owned nodes (gold, persistent) are LIT during a run by spending Attunement Points earned on level-up. Activation
    // applies the node effect live to the fresh per-run Stats; it resets each run. Prereqs are within-branch (need the
    // node above ACTIVATED). Points: small node 1, keystone 2.
    // graph model: buy nodes with attribute points (14 cap, +1 every 4 levels); availability recomputed from the whole
    // owned set each time; hidden routes fire free when their node-set is fully owned.
    public int AttunePoints = 0;
    private int _attuneEarned = 0;
    private readonly HashSet<int> _perkLit = new();      // nodes ACTIVATED this run (with attribute points)
    private readonly HashSet<int> _routesDone = new();
    public bool PerkLit(int id) => _perkLit.Contains(id);
    public List<int> PerkAvailable()                     // gold-OWNED nodes that are graph-reachable from the lit set (or roots)
    {
        var outl = new List<int>();
        foreach (int n in Perks.Available(WitchIndex, _perkLit)) if (Perks.Owned(WitchIndex, n)) outl.Add(n);
        return outl;
    }
    public void ResetPerks() { _perkLit.Clear(); _routesDone.Clear(); AttunePoints = 1; _attuneEarned = 1; }   // start with 1 point
    public void GrantAttune() { if (Level % 4 != 0 || _attuneEarned >= Perks.AttuneCap) return; _attuneEarned++; AttunePoints++; }   // +1 every 4 levels, capped
    public bool PurchasePerk(int id)
    {
        if (_perkLit.Contains(id) || AttunePoints <= 0) return false;
        if (!Perks.Owned(WitchIndex, id)) return false;                            // must be gold-unlocked first
        if (!Perks.Available(WitchIndex, _perkLit).Contains(id)) return false;     // and graph-reachable this run
        AttunePoints--; _perkLit.Add(id);
        Perks.Node(WitchIndex, id).Apply?.Invoke(this);
        CheckHiddenRoutes();
        return true;
    }
    private void CheckHiddenRoutes()   // any hidden route whose whole node-set is now owned fires (free) + is catalogued
    {
        var routes = Perks.Routes(WitchIndex);
        for (int ri = 0; ri < routes.Length; ri++)
        {
            if (_routesDone.Contains(ri)) continue;
            var r = routes[ri];
            bool all = true; foreach (int n in r.Req) if (!_perkLit.Contains(n)) { all = false; break; }
            if (!all) continue;
            _routesDone.Add(ri); r.Apply?.Invoke(this);
            Perks.MarkDiscovered(WitchIndex, ri);
            Game.I?.Hud?.Banner($"HIDDEN KEYSTONE — {r.Name}");
            Game.I?.Sfx?.WardComplete();
        }
    }

    private float _witchPassiveT = 0f, _witchPassiveSfxT = 0f;
    // (SURVIVAL PASSIVES) the fragile witches' innate survival — element-flavored, NOT active tools:
    //   Frost → Frost Armor (anything close is chilled/slowed), Forsaken → Soul Siphon (cursed foes bleed life to her),
    //   Ember → Cinder Skin (retaliatory heat singes attackers + her burns knit her wounds). Throttled to 4 Hz; MP-safe
    //   (status calls route to the host for remote proxies; heals are local to the witch's own machine).
    private void UpdateWitchPassives(float dt)
    {
        if (Game.I == null || !Game.I.SimActive || Downed) return;
        _witchPassiveT -= dt; _witchPassiveSfxT -= dt;
        if (_witchPassiveT > 0f) return;
        _witchPassiveT = 0.25f;
        float area = Mathf.Sqrt(Mathf.Max(1f, S.SpellArea));

        if (VerdantWitch)
        {
            // Living Grove: trickle a fresh ent every ~9s even without combo, so she's never left army-less
            _groveTrickleT -= 0.25f;
            if (_groveTrickleT <= 0f) { _groveTrickleT = 9f; if (CountEnts() < MaxEnts) SummonEnt(); }
        }
        if (FrostWitch)
        {
            float r = 4.6f * area; int chilled = 0;
            foreach (var e in Game.I.Enemies.ToArray())   // (FIX) snapshot — Slow/AddFreeze can remove a foe mid-loop
            {
                if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
                if (Flat(e, GlobalPosition) > r + e.Radius) continue;
                e.Slow(0.6f, 0.55f);                                   // ~45% slow, refreshed while near (routes to host for proxies)
                e.AddFreeze(0.08f, FreezeThreshMul, FrostDurBonus);    // a whisper of freeze buildup — lingerers eventually lock
                chilled++;
            }
            if (chilled > 0)
            {
                Game.I.SpawnPollen(GlobalPosition + Vector3.Up * 0.6f, r, new Color(0.72f, 0.9f, 1f), 3, 0.8f, net: false);
                if (_witchPassiveSfxT <= 0f) { _witchPassiveSfxT = 1.8f; Game.I.Sfx?.Freeze(GlobalPosition, false); }
            }
        }
        else if (ForsakenWitch)
        {
            float r = 15f * area; float healed = 0f; Enemy wispFrom = null;
            foreach (var e in Game.I.Enemies.ToArray())   // (FIX) snapshot for consistency/safety
            {
                if (e == null || e.Dead || !GodotObject.IsInstanceValid(e) || !e.Cursed) continue;
                if (Flat(e, GlobalPosition) > r + e.Radius) continue;
                healed += S.MaxHp * 0.004f;                            // 0.4% max HP per cursed foe per tick
                if (wispFrom == null) wispFrom = e;
                if (healed >= S.MaxHp * 0.03f) break;                  // cap ~3%/tick (~12%/s)
            }
            if (healed > 0f)
            {
                Heal(healed);
                if (wispFrom != null)
                {
                    var l = Game.I.SpawnCurseLink(wispFrom.GlobalPosition + Vector3.Up * wispFrom.Radius * 0.5f, GlobalPosition + Vector3.Up, DamageTypes.Col(DamageType.Curse));
                    if (l != null) { var t = l.CreateTween(); t.TweenInterval(0.2f); t.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(l)) l.QueueFree(); })); }
                }
            }
        }
        else if (EmberWitch)
        {
            float r = 3.6f * area;
            foreach (var e in Game.I.Enemies.ToArray())   // (FIX) snapshot — AddBurn can kill a low-HP foe, mutating Enemies mid-loop (was the crash at Player.cs:6659)
            {
                if (e == null || e.Dead || !GodotObject.IsInstanceValid(e)) continue;
                if (Flat(e, GlobalPosition) > r + e.Radius) continue;
                e.AddBurn(0.6f, Base() * 0.06f, Base() * 2.5f, 0f, Game.I.LocalPeer);   // retaliatory heat singes anything crowding her
            }
            float healed = 0f, hr = 9f * area;
            foreach (var e in Game.I.Enemies.ToArray())   // (FIX) snapshot for consistency/safety
            {
                if (e == null || e.Dead || !GodotObject.IsInstanceValid(e) || !e.Burning) continue;
                if (Flat(e, GlobalPosition) > hr + e.Radius) continue;
                healed += S.MaxHp * 0.0035f;
                if (healed >= S.MaxHp * 0.025f) break;
            }
            if (healed > 0f)
            {
                Heal(healed);
                Game.I.SpawnPollen(GlobalPosition + Vector3.Up * 0.7f, r, new Color(1f, 0.6f, 0.25f), 2, 0.7f, net: false);
            }
        }
        else if (ArcaneWitch)
        {
            // Arcane Feedback: her homing missiles occasionally shoot down an incoming enemy bolt that's close by
            EnemyBolt best = null; float bd = 5.5f;
            foreach (var b in EnemyBolt.All)
            {
                if (b == null || b.Remote || !GodotObject.IsInstanceValid(b)) continue;
                float d = b.GlobalPosition.DistanceTo(GlobalPosition);
                if (d < bd) { bd = d; best = b; }
            }
            if (best != null)
            {
                var bp = best.GlobalPosition; best.QueueFree();
                Game.I.SpawnArcaneRupture(bp, 1.6f);
                Game.I.Sfx?.ArcaneBlast(bp, false);
            }
        }
    }

    private void UpdateUlt(float dt)
    {
        CrashLogger.Mark("Player.UpdateUlt");
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
        if (UltActive && Ult == UltKind.Stormform)   // (REWORK) Wind Rush charges: the window runs down; ends when it's spent or empty
        {
            _windWindowT -= dt; UltActiveT = _windWindowT;
            if (_windWindowT <= 0f || _windCharges <= 0) { UltActive = false; ClearUltAura(); }
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
            if (UltActiveT <= 0f) { UltActive = false; UltDmgMul = 1f; ClearUltAura(); }
        }
        if (_exsang)   // (REWORK) Exsanguinate channel: DoT aura ticks + kill-pop-heal, and the timer runs down
        {
            UpdateExsanguinate(dt);
            UltActiveT -= dt;
            if (UltActiveT <= 0f) { _exsang = false; UltActive = false; ClearUltAura(); }
        }
        if (UltActive && (Ult == UltKind.ArcaneEruption || Ult == UltKind.ArcaneOvercharge))   // (NEW) instant eruption flag + overcharge steroid countdown
        {
            UltActiveT -= dt;
            if (UltActiveT <= 0f) { UltActive = false; if (Ult == UltKind.ArcaneOvercharge && _arcaneAura != null && GodotObject.IsInstanceValid(_arcaneAura)) { _arcaneAura.QueueFree(); _arcaneAura = null; } }
        }
        if (BurnLifestealT > 0f) BurnLifestealT -= dt;   // (NEW) Wildfire Rush lifesteal window
        if (FireWallT > 0f) FireWallT -= dt;             // (NEW) Ring of Fire re-arm lock
        if (SnakeRootCd > 0f) SnakeRootCd -= dt;         // (NEW) per-player snake-root cooldown
        if (EmberFervorT > 0f)   // (NEW) Ember Fervor buff: decay + a periodic ember pulse so allies see the flames
        {
            EmberFervorT -= dt; _fervorNetT -= dt;
            if (FervorPhoenix > 0) Heal(S.MaxHp * 0.008f * FervorPhoenix * dt);   // (OVERHAUL) Phoenix Heart: heal over the buff, scales with stacks
            if (_fervorNetT <= 0f) { _fervorNetT = 0.5f; Game.I.NetMgr?.BroadcastVfx(70, GlobalPosition + Vector3.Up * 0.5f, Vector3.Zero, 1.6f, 0f, DamageTypes.Col(DamageType.Ember)); }
            if (EmberFervorT <= 0f) ShowFervorFlames(false);
        }
        if (UltActive && Ult == UltKind.WildfireRush)     // (NEW) Wildfire Rush: dash window ends after 10s or once all charges are spent
        {
            _flameDashWindowT -= dt;
            if (_flameDashWindowT <= 0f || _flameDashCharges <= 0) UltActive = false;
        }
        if (UltActive && Ult == UltKind.PhoenixAscend) { UltActiveT -= dt; if (UltActiveT <= 0f) UltActive = false; }   // (REWORK) brief ACTIVE window while the phoenix bird flies — gates recharge, then clears
        if (_rushDashLingerT > 0f) _rushDashLingerT -= dt;   // (REWORK) HUD: time left on the LAST dash's lingering field (flame trail / wind area)
        if (UltActive && Ult == UltKind.HexCircle) UpdateHexCircle(dt);   // (NEW) Forsaken curse field
        if (_specter) UpdateSpecter(dt);                                   // (REWORK) LifeCurse immaterial projection drift
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
        float groundY = Game.I.SurfaceHeight(GlobalPosition, GlobalPosition.Y);   // (FIX) aim onto the surface UNDER her (includes floating-island decks), not the y=0 world base — so reticles land on the island, not the jungle far below
        Vector3 p;
        if (Mathf.Abs(d.Y) < 0.001f) p = GlobalPosition + new Vector3(d.X, 0, d.Z).Normalized() * 16f;
        else { float t = (groundY - o.Y) / d.Y; if (t < 0) t = 16f; p = o + d * t; p.Y = groundY; }
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
            Type = FieldType.Heal, HealAllies = true, Cleanse = true, Beam = true, TintColor = DamageTypes.Col(DamageType.Lunar),
            Radius = 13.5f + t + (ModLight ? 4f : 0f), Dur = 9f + t,          // (REWORK) ~1.5× bigger base; cleanses; a touch longer
            Power = S.MaxHp * (0.055f + (ModLight ? 0.025f : 0f)),            // (REWORK) heals more
            EnemyDmg = Base() * (1.0f + t * 0.18f), FromCombo = true, DType = DamageType.Lunar   // (REWORK) hits harder, scales with Atk
        };
        Game.I.AddChild(f);
        f.GlobalPosition = new Vector3(at.X, 0.04f, at.Z);
        UltLingerT = 8f + t;   // (NEW) the moonwell field lingers 8+t s (no UltActive on this ult) — no recharge until it fades
        Game.I.NetMgr?.BroadcastVfx(6, new Vector3(at.X, 0.5f, at.Z), Vector3.Zero, 9f, 0f, DamageTypes.Col(DamageType.Lunar));
        var lun = DamageTypes.Col(DamageType.Lunar);
        // the shaft of moonlight is now living plasma — flowing veins of light pouring from the heavens
        var pillar = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = f.Radius * 0.5f, BottomRadius = f.Radius * 0.85f, Height = 30f }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        pillar.MaterialOverride = Game.ElementEnergyMat(lun);
        pillar.Position = new Vector3(0, 15f, 0);
        f.AddChild(pillar);
        // (REWORK) a FAINTER, moonlit-blue inner shaft — the old one was a near-white column that whited-out your view from
        // inside the well. Lower alpha + blue tint + gentler emission so you can actually SEE the fight while standing in it.
        var core = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = f.Radius * 0.16f, BottomRadius = f.Radius * 0.26f, Height = 30f }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(lun.R, lun.G, lun.B, 0.18f), EmissionEnabled = true, Emission = lun, EmissionEnergyMultiplier = 0.8f, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded } };
        core.Position = new Vector3(0, 15f, 0); f.AddChild(core);
        f.AddChild(new OmniLight3D { OmniRange = f.Radius * 2.2f, LightColor = lun, LightEnergy = 1.7f, ShadowEnabled = false, Position = new Vector3(0, 2f, 0) });
        Ring(new Vector3(at.X, 0.04f, at.Z), lun, f.Radius, 0.7f);
        Ring(new Vector3(at.X, 0.04f, at.Z), Colors.White, f.Radius * 0.5f, 0.5f);
        Game.I.FallingMotes(new Vector3(at.X, 0.04f, at.Z), f.Radius * 0.9f, lun, 30, 12f);   // moonlight rains into the well
        Game.I.Sfx?.Thunder();

        UltActive = true; UltActiveT = f.Dur; UltMax = f.Dur;
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
        _barkDmg = Base() * (4.5f + UltTier * 1.0f);   // (REWORK) a LOT more burst damage on expiry
        Shield = MaxShield;
        Game.I.Hud?.Banner("Barkskin — thorns up!");
        Ring(GlobalPosition, DamageTypes.Col(DamageType.Nature), 4f, 0.5f);
        foreach (var t in Ents.ToArray()) if (t != null && GodotObject.IsInstanceValid(t)) { t.Heal(t.MaxHp); Game.I.VfxRing(t.GlobalPosition, DamageTypes.Col(DamageType.Nature), 2f, 0.5f); }
    }

    private void BarkBurst()
    {
        var col = DamageTypes.Col(DamageType.Nature);
        float rBase = ModBark ? 9f : 7f;   // raw — the ModBark GroundField auto-scales this by SpellArea
        float r = rBase * S.SpellArea;     // the burst + spikes + world damage scale here
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
            var f = new GroundField { Type = FieldType.Hex, TintColor = col, Radius = rBase, Dur = 5f, Power = Base() * 0.1f, PoisonAdd = 3f, SlowMul = 0.5f, FromCombo = true, DType = DamageType.Nature, Src = this };
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
        var lun = DamageTypes.Col(DamageType.Lunar);
        for (int i = 0; i < count; i++)
        {
            float ang = i / (float)count * Mathf.Tau;
            var orb = new CrescentOrb { Angle = ang, OrbitR = 4.5f, Dmg = Base() * (3.5f + UltTier * 0.6f) };   // scales with Atk now (was flat 40+22t)
            Game.I.AddChild(orb);
            _crescents.Add(orb);
            // each blade is forged from a flash of moonlight at its orbit point
            var forge = GlobalPosition + new Vector3(Mathf.Cos(ang) * 4.5f, 1.1f, Mathf.Sin(ang) * 4.5f);
            var flash = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.5f, Height = 1f }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(1, 1, 1, 0.9f), EmissionEnabled = true, Emission = lun.Lerp(Colors.White, 0.6f), EmissionEnergyMultiplier = 4f, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded } };
            Game.I.AddChild(flash); flash.GlobalPosition = forge;
            var ft = flash.CreateTween(); ft.SetParallel();
            ft.TweenProperty(flash, "scale", Vector3.One * 2.2f, 0.35f).SetEase(Tween.EaseType.Out);
            ft.TweenProperty(flash, "transparency", 1f, 0.35f);
            ft.Chain().TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(flash)) flash.QueueFree(); }));
        }
        Ring(GlobalPosition, lun, 5f, 0.6f);
        Ring(GlobalPosition, Colors.White, 4.5f, 0.5f);
        Game.I.FallingMotes(GlobalPosition, 5f, lun, 16, 8f);
        Game.I.Sfx?.Thunder();
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
    // (NEW) hard teleport: place + zero vertical velocity + a brief no-fall grace + refill jumps (sky-ritual entry/exit, re-ride)
    public void TeleportReset(Vector3 pos) { GlobalPosition = pos; _vy = 0f; _grounded = false; _noFall = 1.5f; _jumps = JumpsMax; _vineRising = false; }
    private bool _vineRising = false; private float _vineTargetY = 0f; private float _vineFling = 20f;
    public void VineLaunch(float topY, float flingVel = 20f)   // (NEW) jungle vine: grapple UP the vine, then fling skyward at the top (hold jump to glide down). flingVel = the extra pop at the top (sky-island vines use a smaller one)
    {
        if (Downed || _vineRising) return;
        _vineRising = true; _vineTargetY = topY; _vineFling = flingVel;
        _grounded = false; _vy = 0f; _noFall = 6f;
        Game.I.PlayerSound(GlobalPosition, 0.6f);
        Game.I.GlowFlowersNear(GlobalPosition, 3f);
    }
    private void UpdateVineRise(float dt)
    {
        Floating = false;
        float ny = Mathf.MoveToward(GlobalPosition.Y, _vineTargetY, 42f * dt);   // whoosh up the vine
        GlobalPosition = new Vector3(GlobalPosition.X, ny, GlobalPosition.Z);
        _grounded = false; _vy = 0f;
        if (ny >= _vineTargetY - 0.3f)   // reached the top → fling for the fun extra
        {
            _vineRising = false; _vy = _vineFling; _noFall = 4f; _jumps = JumpsMax;
            Game.I.PlayerSound(GlobalPosition, 0.5f);
        }
    }
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
        while (Xp >= XpNext) { Xp -= XpNext; Level++; ApplyLevelGain(); XpNext = 26f + (Level - 1) * 19f; if (DivineWitch && Level % 10 == 0) Interventions = Mathf.Min(2, Interventions + 1); GrantAttune(); Game.I.OpenLevelUp(); }   // (TUNE) leveled a tad faster; (ATTUNE) +1 point/level, +1 milestone every 5th, capped per run
    }

    // (NEW) dev/testing: jump straight to a target level with the per-level stat gains applied, but NO upgrade prompts.
    public void DevJumpLevel(int target)
    {
        while (Level < target) { Level++; ApplyLevelGain(); XpNext = 26f + (Level - 1) * 19f; if (DivineWitch && Level % 10 == 0) Interventions = Mathf.Min(2, Interventions + 1); }   // (TUNE) leveled a tad faster
        Xp = 0f;
    }

    // Each level is rarer now, so each one grants a small permanent power bump to keep the curve climbing.
    private void ApplyLevelGain()
    {
        const float g = 0.0075f;                 // +0.75% base damage & max HP per level
        S.Atk *= 1f + g;
        float oldMax = S.MaxHp;
        S.MaxHp *= 1f + g;
        Hp = Mathf.Min(S.MaxHp, Hp + (S.MaxHp - oldMax));   // keep the new headroom filled
        Hp = Mathf.Min(S.MaxHp, Hp + S.MaxHp * 0.15f);      // (NEW) a little level-up heal — +15% of max HP each level
        S.ShieldPct *= 1f + g * 0.5f;            // shield capacity grows at half rate
    }

    public float DmgDirT = 0f;
    public Vector3 DmgDirWorld = Vector3.Forward;

    // (NEW) apply/refresh arrow-venom. Called every damage tick while you're inside a volley circle (locally on the
    // host, over the wire on clients) — the hold window is what keeps it from double-dipping with the field damage.
    public void ApplyVenom(float dur, float dps)
    {
        if (Downed || BlessedT > 0f || FullyImmune) return;   // Blessed clears it; Divinity/Faith Shield block it entirely
        VenomT = Mathf.Max(VenomT, dur);
        VenomDps = Mathf.Max(VenomDps, dps);   // a bigger formation's venom overrides a smaller one's
        VenomHold = 0.5f;                      // suppressed while you're still standing in the rain
    }

    private void UpdateVenom(float dt)
    {
        if (VenomT <= 0f) { VenomDps = 0f; return; }
        if (BlessedT > 0f) { VenomT = 0f; VenomDps = 0f; _venomTick = 0f; Game.I?.Hud?.Banner("the blessing burns the venom away"); return; }   // instant purge
        if (VenomHold > 0f) { VenomHold -= dt; return; }   // still in the circle — the field damage is doing the work
        VenomT -= dt;
        _venomTick -= dt;
        if (_venomTick <= 0f) { _venomTick = 0.5f; Hurt(VenomDps * 0.5f); }
        if (VenomT <= 0f) VenomDps = 0f;
    }

    public void Hurt(float dmg, Vector3? src = null)
    {
        if (_iframe > 0 || _divFalling || Divinity || BarkActive || _specter || Downed || Game.I == null || !Game.I.WorldRunning) return;   // _specter: immaterial Specter is untouchable
        if (Game.I.MenuImmune || InsideFaithShield) return;   // (MP) untouchable inside her elemental bubble; and ALL damage is nullified inside a Faith Shield dome
        _combatT = 0f;   // taking fire = in combat; gates fast out-of-combat shield regen (NEW)
        if (Armor.Count > 0)   // one shared armor charge eats this whole hit, then pops (thorn charges also burst)
        {
            var ch = Armor[Armor.Count - 1]; Armor.RemoveAt(Armor.Count - 1);
            _iframe = 0.5f; ProcFlash = 0.3f;
            ArmorBreakT = 0.6f; HurtFlash = Mathf.Max(HurtFlash, 0.4f);   // (NEW) armor pop reads LOUD — flash + callout + clang
            if (src.HasValue) { DmgDirWorld = src.Value - GlobalPosition; DmgDirWorld.Y = 0; DmgDirT = 1.2f; }
            Game.I.Sfx?.ArmorBreak();
            if (ch.Thorn)
            {
                float r = _thornBurstRad > 0f ? _thornBurstRad : 5f;   // (OVERHAUL) Bramble: burst radius
                foreach (var e in Game.I.Enemies.ToArray())
                    if (e != null && !e.Dead && GodotObject.IsInstanceValid(e) && Flat(e, GlobalPosition) < r + e.Radius)
                    { e.Hurt(ch.Dmg, DamageType.Nature, true); if (_thornRoot > 0f) e.Root(_thornRoot); }   // (OVERHAUL) Snare Bark: burst roots
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
        if (_thornResistT > 0f) dmg *= Mathf.Max(0.3f, 1f - _thornResistAmt);   // (OVERHAUL) Ironbark: brief thorn damage resist
        if (_galeGuard > 0f) dmg *= 0.55f;   // Tailwind: ~45% less damage in the window after a dash (Gale) (NEW)
        bool hadShield = Shield > 0f;
        if (Shield > 0) { if (dmg <= Shield) { Shield -= dmg; dmg = 0; } else { dmg -= Shield; Shield = 0; } }
        // emptying the shield (or being hit with none left) means a much longer wait before it rebuilds
        _shieldT = (Shield <= 0.01f) ? S.ShieldDelay * 2.4f : S.ShieldDelay;
        if (hadShield && Shield <= 0.01f) { ShieldBreakT = 0.6f; HurtFlash = Mathf.Max(HurtFlash, 0.5f); Game.I.Sfx?.ShieldBreak(); }   // (NEW) this hit just SHATTERED the shield
        if (dmg > 0)
        {
            Hp -= dmg;
            float sev = Mathf.Clamp(dmg / Mathf.Max(1f, S.MaxHp), 0f, 1f);   // (NEW) hit severity → flash + grunt scale
            HurtFlash = Mathf.Max(HurtFlash, Mathf.Min(1f, 0.4f + sev * 3f));
            Game.I.Sfx?.PlayerHurt(sev);
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
        _meteorAscend = false; _meteorDiving = false; _phoenix = false; _flameDashT = 0f;   // (NEW) cancel any Ember flight ult cleanly
        if (_phoenixVfx != null && GodotObject.IsInstanceValid(_phoenixVfx)) { _phoenixVfx.QueueFree(); _phoenixVfx = null; }
        ClearUltAura();   // free any empowerment aura (Eclipse/Divinity/Stormform) if she's downed mid-ult
        _exsang = false; UltActive = false; _bodyModel?.SetEclipse(false);   // (REWORK) end a channeled transform / eclipse recolour if downed mid-cast
        HideEmberAimRing();
        EmberFervorT = 0f; ShowFervorFlames(false);   // (NEW) drop the Ember Fervor buff/flames
        Game.I.MyStats.TimesDowned++;   // (NEW) end-of-run tally
        Charging = false; ChargeAmt = 0f;
        if (_beamSeg != null) { _beamSeg.Free(); _beamSeg = null; _beamLight = null; _beamT = 0; }
        foreach (var ps in _prismSegs) ps?.Free(); _prismSegs.Clear();
        Game.I.Hud?.Banner("DOWNED — hold on for an ally");
        Game.I.Sfx?.Discord();
        if (Game.I.NetMgr != null && Game.I.NetMgr.Active) Game.I.NetMgr.LocalDowned(true);   // MP: report; the host evaluates all-down (→ maze/sky spit-out or game over)
        else if (Game.I.InMaze) Game.I.MazeDeathExit();   // solo maze death: revived + spat back out (the well caves in), NOT game over
        else if (Game.I.InSky) Game.I.SkyPlayerDown();    // solo sky death: revived + the ritual ends (you fell), NOT game over
        else Game.I.GameOver();   // solo, open world: no one can revive
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
        Hp = S.MaxHp * 0.55f; _iframe = 1.6f; BlessedT = Mathf.Max(BlessedT, 4f);   // (NERF) revive at 55% HP, not a full reset — the cheat-death was making her near-unkillable at depth
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
