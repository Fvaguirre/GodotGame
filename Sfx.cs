using Godot;

// Procedurally synthesized UI sounds (no audio assets needed).
// Sfx.cs — procedural audio (cast/hit/release/UI cues). Game.I.Sfx is the singleton; call Release(type)/etc. No external assets.
public partial class Sfx : Node
{
    private AudioStreamPlayer _clink, _thunder, _elem, _chord, _music, _drums, _crit, _wardHum, _hauntMus;
    public float HauntBlend = 0f;            // (HAUNT) 0..1, set by Game: are you in the storm? drives the haunting music layer
    private float _hauntBlendCur = 0f;
    private const float Tau = 6.2831853f;

    public override void _Ready()
    {
        _clink = new AudioStreamPlayer { VolumeDb = -4f, Stream = BuildClink() };
        AddChild(_clink);
        _crit = new AudioStreamPlayer { VolumeDb = -5f, Stream = BuildCrit() };   // (NEW) local crit "clink"
        AddChild(_crit);
        _thunder = new AudioStreamPlayer { VolumeDb = -2f, Stream = BuildThunder() };
        AddChild(_thunder);
        _elem = new AudioStreamPlayer { VolumeDb = -3f };
        AddChild(_elem);
        _chord = new AudioStreamPlayer { VolumeDb = -6f };
        AddChild(_chord);
        _music = new AudioStreamPlayer { VolumeDb = -15f, Stream = BuildArp() };
        AddChild(_music);
        _music.Play();
        _drums = new AudioStreamPlayer { VolumeDb = -42f, Stream = BuildDrums() };  // starts inaudible, fades in with tension
        AddChild(_drums);
        _drums.Play();
        _wardHum = new AudioStreamPlayer { VolumeDb = -26f, Stream = BuildWardHum() };   // (NEW) warding-ritual charge drone; idle until WardCharge() starts it
        AddChild(_wardHum);
        _hauntMus = new AudioStreamPlayer { VolumeDb = -80f, Stream = BuildHauntMusic() };   // (HAUNT) eerie layer — always looping, silent until you enter a storm; phase-locked to the arp so it mixes in cleanly
        AddChild(_hauntMus); _hauntMus.Play();
        InitSpellSounds();
    }

    public void Clink() { if (_clink != null) _clink.Play(); }
    public void Coins() { PlayOne(BuildCoins(), -3f); }   // (NEW) bright coin "ch-ching" when a chest pays out gold
    public void CritHit() { if (_crit != null) _crit.Play(); }   // (NEW) local-only crit ding (only the hitting player hears it)
    public void Thunder() { if (_thunder != null) _thunder.Play(); }
    // (HAUNT) a positional thunderclap over the storm — a deep rolling crack, pitch-varied so repeats don't sound canned
    private AudioStreamWav _stormThunder;
    public void StormThunder(Vector3 pos, float pitch = 1f)
    {
        _stormThunder ??= BuildStormThunder();
        At(_stormThunder, pos, 2f, 140f, pitch);
    }
    private static AudioStreamWav BuildStormThunder()
    {
        int rate = 22050, n = (int)(rate * 1.5f);   // longer, rolling
        var s = new float[n];
        var rng = new System.Random(19);
        float lp = 0f;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate;
            // a sharp crack that decays into a long low rolling rumble with a couple of secondary peaks
            float crackEnv = Mathf.Min(1f, t * 120f) * Mathf.Exp(-t * 6.5f);
            float rollEnv = Mathf.Exp(-t * 1.4f) * (0.6f + 0.4f * Mathf.Sin(t * Tau * 1.7f) + 0.3f * Mathf.Sin(t * Tau * 0.7f + 1f));
            float noise = (float)(rng.NextDouble() * 2 - 1);
            lp += (noise - lp) * 0.18f;   // low-passed rumble noise
            float rumble = Mathf.Sin(t * Tau * 52f) * 0.5f + Mathf.Sin(t * Tau * 33f) * 0.4f + Mathf.Sin(t * Tau * 21f) * 0.3f;
            float crack = noise * crackEnv;
            s[i] = (crack * 0.5f + (rumble + lp * 1.2f) * rollEnv * 0.55f);
        }
        for (int i = 0; i < n; i++) s[i] = (float)System.Math.Tanh(s[i] * 1.1) * 0.9f;
        return Wav(s, rate);
    }

    // (NEW) warding-ritual charge loop — call every frame a warden stands in the circle; pitch + volume swell with the fill (0..1)
    public void WardCharge(float frac)
    {
        if (_wardHum == null) return;
        frac = Mathf.Clamp(frac, 0f, 1f);
        if (!_wardHum.Playing) _wardHum.Play();
        _wardHum.PitchScale = 0.72f + frac * 0.95f;                    // rises ~0.72 → 1.67 as it fills — the drone "gets more filled"
        _wardHum.VolumeDb = Mathf.Lerp(-24f, -6f, Mathf.Sqrt(frac));   // swells in (sqrt so it's audible early, blooms toward 100%)
    }
    public void WardChargeStop() { if (_wardHum != null && _wardHum.Playing) _wardHum.Stop(); }
    public void WardComplete() { PlayOne(BuildWardComplete(), -2f); WardChargeStop(); }   // (NEW) witchy relief ding at 100%

    public void UnicornCall(Vector3 pos) { At(BuildUnicornCall(), pos, -4f, 60f); }   // (NEW) ethereal spectre shimmer/whinny on spawn
    public void UnicornCharge(Vector3 pos) { At(BuildUnicornCharge(), pos, -3f, 70f); }   // (NEW) rising magical gallop as it charges the boss
    public void ArcaneBoom(Vector3 pos) { At(BuildArcaneBoom(), pos, 4f, 120f); }   // (NEW) the mushroom-cloud detonation — deep boom + rumble + shimmer

    // ---- spell / combat one-shots (round-robin pool so they overlap) ----
    private AudioStreamPlayer[] _pool;
    private int _pi = 0;
    private AudioStreamPlayer3D[] _pool3d;     // spatial one-shots for enemy sounds (directional alerts)
    private int _pi3 = 0;
    private AudioStreamWav _enemyGrowl, _enemyShoot, _bossRoar, _zAttack, _zGroan, _zDeath, _zExcited;   // (PERF) swarm sounds cached once, not synth'd per play
    private System.Collections.Generic.Dictionary<DamageType, AudioStreamWav> _cast, _impact, _charge, _release;
    private AudioStreamWav _death, _discord, _riteWin, _riteFail;
    private AudioStreamWav _damageTick, _critPlink;              // (NEW) universal per-hit confirm; crit → satisfying plink
    private AudioStreamPlayer3D[] _hitPool; private int _hpi = 0; // dedicated spatial pool so hit ticks don't starve enemy ambience
    public bool EventActive = false;     // a ritual is live → push the mix harder

    private void InitSpellSounds()
    {
        _pool = new AudioStreamPlayer[8];
        for (int i = 0; i < _pool.Length; i++) { _pool[i] = new AudioStreamPlayer(); AddChild(_pool[i]); }
        _pool3d = new AudioStreamPlayer3D[10];
        for (int i = 0; i < _pool3d.Length; i++) { _pool3d[i] = new AudioStreamPlayer3D { MaxDistance = 55f, UnitSize = 8f }; AddChild(_pool3d[i]); }
        _hitPool = new AudioStreamPlayer3D[6];
        for (int i = 0; i < _hitPool.Length; i++) { _hitPool[i] = new AudioStreamPlayer3D { MaxDistance = 55f, UnitSize = 7f }; AddChild(_hitPool[i]); }
        _damageTick = BuildDamageTick(); _critPlink = BuildCritPlink();
        _enemyGrowl = BuildEnemyGrowl(); _enemyShoot = BuildEnemyShoot(); _bossRoar = BuildBossRoar();
        _zAttack = BuildZombieAttack(); _zGroan = BuildZombieGroan(); _zDeath = BuildZombieDeath(); _zExcited = BuildZombieExcited();   // (PERF) build the swarm WAVs ONCE (was rebuilt every play → GC storm in a horde)
        _cast = new(); _impact = new(); _charge = new(); _release = new();
        foreach (DamageType t in System.Enum.GetValues(typeof(DamageType)))
        {
            _cast[t] = BuildCast(t); _impact[t] = BuildImpact(t); _charge[t] = BuildCharge(t); _release[t] = BuildRelease(t);
        }
        _death = BuildDeath(); _discord = BuildDiscord();
        _riteWin = BuildRiteWin(); _riteFail = BuildRiteFail();
    }

    private void PlayOne(AudioStreamWav w, float db)
    {
        if (_pool == null || w == null) return;
        var p = _pool[_pi]; _pi = (_pi + 1) % _pool.Length;
        p.Stream = w; p.VolumeDb = db; p.Play();
    }

    public void Cast(DamageType t)    { if (_cast != null) PlayOne(_cast[t], -10f); }
    public void ChargeUp(DamageType t){ if (_charge != null) PlayOne(_charge[t], -10f); }
    public void Release(DamageType t) { if (_release != null) PlayOne(_release[t], -5f); }
    public void Impact(DamageType t)  { if (_impact != null) PlayOne(_impact[t], -11f); }
    public void Death()               { PlayOne(_death, -8f); }
    public void Rustle()              { PlayOne(BuildRustle(), -11f); }   // Wild Swarm spawn — leaves shuffling
    public void Creak()               { PlayOne(BuildCreak(), -5f); }     // Barkskin activation — great tree groaning
    public void Minion(int kind)      { PlayOne(BuildMinion(kind), -13f); } // 0 spawn,1 attack,2 detonate,3 rally
    public void Fizzle()              { PlayOne(BuildFizzle(), -9f); }      // failed cast — not enough mana/blood
    public void PlayerHurt(float sev) { PlayOne(BuildPlayerHurt(Mathf.Clamp(sev, 0f, 1f)), Mathf.Lerp(-11f, -2f, Mathf.Clamp(sev, 0f, 1f))); }   // (NEW) local pained thud when a hit reaches your HP — scales with severity
    public void ShieldBreak()         { PlayOne(BuildShieldBreak(), -3f); }   // (NEW) crystalline shield-down shatter
    public void ArmorBreak()          { PlayOne(BuildArmorBreak(), -3f); }    // (NEW) metallic armor-charge clang
    public void Heartbeat()           { PlayOne(BuildHeartbeat(), -6f); }     // (NEW) low-health pulse thump (quickens near death)
    public void LowHealth()           { PlayOne(BuildLowHealth(), -3f); }     // (NEW) one-shot alarm when you cross into low health

    // ---- spatial enemy sounds (directional alerts; played at the enemy's world position) ----
    public void EnemyGrowl(Vector3 pos) { At(_enemyGrowl, pos, -7f, 50f); }
    public void Poof(Vector3 pos) { At(BuildPoof(), pos, -13f, 45f); }   // (NEW) quiet magical spawn poof
    public void EnemyShoot(Vector3 pos) { At(_enemyShoot, pos, -9f, 55f); }
    public void Incoming(Vector3 pos)   { At(BuildIncoming(), pos, -6f, 60f); }   // (NEW) spatial rising whistle — a shot on a collision course with you
    public void BossRoar(Vector3 pos)   { At(_bossRoar, pos, 0f, 95f); }
    public void BossTell(Vector3 pos)   { At(BuildBossTell(), pos, -4f, 80f); }   // short rising guttural wind-up grunt (NEW)
    public void CrunchAt(Vector3 pos)   { At(BuildCrunch(), pos, -5f, 55f); }   // smashed pumpkin: wet caving-in splat + rind cracks (NEW)
    public void SplashAt(Vector3 pos)   { At(BuildSplash(), pos, -12f, 42f, 0.9f + GD.Randf() * 0.3f); }   // water displacement — softer + slight pitch variation so jumps don't grate (NEW)
    public void WadeAt(Vector3 pos)     { At(BuildWade(), pos, -21f, 22f, 0.8f + GD.Randf() * 0.45f); }    // quiet muffled wade swish; randomized pitch so repeated steps don't sound identical (NEW)
    // (NEW) charged-modifier one-shots (spatial)
    public void ModFrost(Vector3 pos, bool net = true)   { At(BuildFrostShatter(), pos, -6f, 55f); if (net) Game.I?.NetMgr?.BroadcastSfx((int)Snd.ModFrost, pos); }
    public void ModBramble(Vector3 pos, bool net = true) { At(BuildBrambleSnap(), pos, -6f, 55f); if (net) Game.I?.NetMgr?.BroadcastSfx((int)Snd.ModBramble, pos); }
    public void ModEmber(Vector3 pos, bool net = true)   { At(BuildEmberBoom(), pos, -4f, 60f); if (net) Game.I?.NetMgr?.BroadcastSfx((int)Snd.ModEmber, pos); }
    public void ModBlood(Vector3 pos, bool net = true)   { At(BuildBloodSpray(), pos, -6f, 55f); if (net) Game.I?.NetMgr?.BroadcastSfx((int)Snd.ModBlood, pos); }
    public void ModSpike(Vector3 pos, bool net = true)   { At(BuildSpikeStab(), pos, -6f, 55f); if (net) Game.I?.NetMgr?.BroadcastSfx((int)Snd.ModSpike, pos); }
    public void ModCurse(Vector3 pos, bool net = true)   { At(BuildCurseWhoosh(), pos, -6f, 55f); if (net) Game.I?.NetMgr?.BroadcastSfx((int)Snd.ModCurse, pos); }
    public void CurseCrush(Vector3 pos)                  { At(BuildCurseCrush(), pos, -3f, 60f); }   // (NEW) voodoo crush — squelchy dark implosion (no net: the kind-58 VFX plays it on allies)
    public void HexWeave(Vector3 pos)                    { At(BuildHexWeave(), pos, -4f, 60f); }      // (NEW) dark curse incantation for the Hex combos (replaced the scratchy WitchCackle)
    private float _whishLast = -1f;
    public void Whish(Vector3 pos)                       // (NEW) soft airy whoosh — the Cyclone swallowing an enemy projectile
    {
        float now = Time.GetTicksMsec() / 1000f;
        if (now - _whishLast < 0.05f) return;            // throttle so a volley of eaten bolts isn't a wall of noise
        _whishLast = now;
        At(BuildWhish(), pos, -8f, 45f);
    }
    public void ModChime(Vector3 pos, bool net = true)   { At(BuildLunarChime(), pos, -7f, 55f); if (net) Game.I?.NetMgr?.BroadcastSfx((int)Snd.ModChime, pos); }
    public void ModHoly(Vector3 pos, bool net = true)    { At(BuildHolyChord(), pos, -6f, 55f); if (net) Game.I?.NetMgr?.BroadcastSfx((int)Snd.ModHoly, pos); }
    public void ModSmite(Vector3 pos, bool net = true)   { At(BuildSmiteStrike(), pos, -4f, 60f); if (net) Game.I?.NetMgr?.BroadcastSfx((int)Snd.ModSmite, pos); }
    public void ModPour(Vector3 pos, bool net = true)    { At(BuildBloodPour(), pos, -6f, 55f); if (net) Game.I?.NetMgr?.BroadcastSfx((int)Snd.ModPour, pos); }
    public void ModWind(Vector3 pos, bool net = true)    { At(BuildWindWhoosh(), pos, -5f, 60f); if (net) Game.I?.NetMgr?.BroadcastSfx((int)Snd.ModWind, pos); }
    public void ArcaneBlast(Vector3 pos, bool net = true){ At(BuildArcaneBlast(), pos, -3f, 75f); if (net) Game.I?.NetMgr?.BroadcastSfx((int)Snd.ArcaneBlast, pos); }
    public void UltCast(Vector3 pos) { At(BuildUltCast(), pos, -1f, 95f); }   // (NEW) universal cinematic ult-activation whoomph (local — every ult already broadcasts its own element sound)
    public void HolyLances(Vector3 pos, bool net = true) { At(BuildLanceFall(), pos, -4f, 65f); if (net) Game.I?.NetMgr?.BroadcastSfx((int)Snd.HolyLances, pos); }
    public void WindRushBy(Vector3 pos, bool net = true) { At(BuildWindWhoosh(), pos, -2f, 75f); if (net) Game.I?.NetMgr?.BroadcastSfx((int)Snd.WindRushBy, pos); }
    public void WindSlash(Vector3 pos, bool net = true)  { At(BuildWindSlash(), pos, -4f, 65f); if (net) Game.I?.NetMgr?.BroadcastSfx((int)Snd.WindSlash, pos); }
    public void RootRush(Vector3 pos, bool net = true)   { At(BuildRootRush(), pos, -5f, 55f); if (net) Game.I?.NetMgr?.BroadcastSfx((int)Snd.RootRush, pos); }
    public void HolyRush(Vector3 pos, bool net = true)   { At(BuildHolyRush(), pos, -8f, 55f); if (net) Game.I?.NetMgr?.BroadcastSfx((int)Snd.HolyRush, pos); }
    public void WitchCackle(Vector3 pos, bool net = true){ At(BuildWitchCackle(), pos, -5f, 55f); if (net) Game.I?.NetMgr?.BroadcastSfx((int)Snd.WitchCackle, pos); }
    public void GasHiss(Vector3 pos, bool net = true)    { At(BuildGasRelease(), pos, -6f, 50f); if (net) Game.I?.NetMgr?.BroadcastSfx((int)Snd.GasHiss, pos); }
    public void FireworkLaunch(Vector3 pos) { At(BuildFireworkLaunch(), pos, -5f, 90f); }   // rising whistle (Firework plays locally on each machine)
    public void FireworkBurst(Vector3 pos)  { At(BuildFireworkBurst(), pos, -3f, 100f); }    // crackle boom
    public void ZombieGroan(Vector3 pos, bool net = true) { At(_zGroan, pos, -7f, 45f, 0.82f + GD.Randf() * 0.32f); if (net) Game.I?.NetMgr?.BroadcastSfx((int)Snd.ZombieGroan, pos); }   // (PERF) cached WAV + random pitch for variation
    public void ZombieDeath(Vector3 pos, bool net = true) { At(_zDeath, pos, -5f, 45f, 0.88f + GD.Randf() * 0.26f); if (net) Game.I?.NetMgr?.BroadcastSfx((int)Snd.ZombieDeath, pos); }   // (PERF) cached
    private float _zAtkLast = -1f;
    public void ZombieAttack(Vector3 pos, bool net = true)   // bite/lunge
    {
        float now = Time.GetTicksMsec() / 1000f;
        if (now - _zAtkLast < 0.05f) return;   // (PERF) 150 swarmers biting a stationary player was ~150 fresh-WAV builds/sec (GC storm → 100ms spikes) — cache the WAV + throttle to ~20/sec
        _zAtkLast = now;
        At(_zAttack, pos, -6f, 45f, 0.82f + GD.Randf() * 0.32f);
        if (net) Game.I?.NetMgr?.BroadcastSfx((int)Snd.ZombieAttack, pos);
    }
    public void ZombieExcited(Vector3 pos, bool net = true){ At(_zExcited, pos, -6f, 50f, 0.88f + GD.Randf() * 0.26f); if (net) Game.I?.NetMgr?.BroadcastSfx((int)Snd.ZombieExcited, pos); }   // (PERF) cached
    public void ZombieScream(Vector3 pos, bool net = true) { At(BuildZombieScream(), pos, -4f, 60f); if (net) Game.I?.NetMgr?.BroadcastSfx((int)Snd.ZombieScream, pos); }   // shriek on spotting
    public void ZombieSnicker(Vector3 pos, bool net = true) { At(BuildZombieSnicker(), pos, -8f, 35f); if (net) Game.I?.NetMgr?.BroadcastSfx((int)Snd.ZombieSnicker, pos); }   // idle chuckle
    public void TakerGrowl(Vector3 pos, bool net = true) { At(BuildTakerGrowl(), pos, -2f, 90f); if (net) Game.I?.NetMgr?.BroadcastSfx((int)Snd.TakerGrowl, pos); }   // deep spawn announce
    public void TakerGrunt(Vector3 pos, bool net = true) { At(BuildTakerGrunt(), pos, -5f, 60f); if (net) Game.I?.NetMgr?.BroadcastSfx((int)Snd.TakerGrunt, pos); }   // navigating grunt
    public void TakerBone(Vector3 pos, bool net = true) { At(BuildTakerBone(), pos, -4f, 55f); if (net) Game.I?.NetMgr?.BroadcastSfx((int)Snd.TakerBone, pos); }    // bone-break while squeezing
    public void TakerDeath(Vector3 pos, bool net = true) { At(BuildTakerDeath(), pos, -2f, 80f); if (net) Game.I?.NetMgr?.BroadcastSfx((int)Snd.TakerDeath, pos); }   // deep death "ughh"
    public void TakerLaugh(Vector3 pos, bool net = true) { At(BuildTakerLaugh(), pos, -2f, 75f); if (net) Game.I?.NetMgr?.BroadcastSfx((int)Snd.TakerLaugh, pos); }   // snarled zombie cackle before the dash
    public void Thud(Vector3 pos, bool net = true) { At(BuildThud(), pos, -1f, 70f); if (net) Game.I?.NetMgr?.BroadcastSfx((int)Snd.Thud, pos); }   // heavy body slamming a wall
    public void Freeze(Vector3 pos, bool net = true) { At(BuildFreeze(), pos, -5f, 55f); }        // (NEW) enemy encased in ice (crackle)
    public void IceShatter(Vector3 pos, bool net = true) { At(BuildIceShatter(), pos, -3f, 65f); }  // (NEW) ice block shatters
    public void HordeScream()   // (NEW) phase-2 server-wide blood-curdling scream — non-spatial so everyone hears it
    {
        if (_pool == null || _pool.Length == 0) return;
        var p = _pool[_pi]; _pi = (_pi + 1) % _pool.Length;
        p.Stream = BuildHordeScream(); p.VolumeDb = -2f; p.PitchScale = 1f; p.Play();
    }

    public enum Snd { ModFrost, ModBramble, ModEmber, ModBlood, ModSpike, ModCurse, ModChime, ModHoly, ModSmite, ModPour, ModWind, ArcaneBlast, HolyLances, WindRushBy, WindSlash, RootRush, HolyRush, WitchCackle, GasHiss, ZombieGroan, ZombieDeath, ZombieAttack, ZombieExcited, ZombieScream, ZombieSnicker, TakerGrowl, TakerGrunt, TakerBone, TakerDeath, TakerLaugh, Thud }

    // remote replay of an ally's ability sound (net:false → no re-broadcast)
    public void PlayNet(int id, Vector3 pos)
    {
        switch ((Snd)id)
        {
            case Snd.ModFrost:    ModFrost(pos, false); break;
            case Snd.ModBramble:  ModBramble(pos, false); break;
            case Snd.ModEmber:    ModEmber(pos, false); break;
            case Snd.ModBlood:    ModBlood(pos, false); break;
            case Snd.ModSpike:    ModSpike(pos, false); break;
            case Snd.ModCurse:    ModCurse(pos, false); break;
            case Snd.ModChime:    ModChime(pos, false); break;
            case Snd.ModHoly:     ModHoly(pos, false); break;
            case Snd.ModSmite:    ModSmite(pos, false); break;
            case Snd.ModPour:     ModPour(pos, false); break;
            case Snd.ModWind:     ModWind(pos, false); break;
            case Snd.ArcaneBlast: ArcaneBlast(pos, false); break;
            case Snd.HolyLances:  HolyLances(pos, false); break;
            case Snd.WindRushBy:  WindRushBy(pos, false); break;
            case Snd.WindSlash:   WindSlash(pos, false); break;
            case Snd.RootRush:    RootRush(pos, false); break;
            case Snd.HolyRush:    HolyRush(pos, false); break;
            case Snd.WitchCackle: WitchCackle(pos, false); break;
            case Snd.GasHiss:     GasHiss(pos, false); break;
            case Snd.ZombieGroan: ZombieGroan(pos, false); break;
            case Snd.ZombieDeath: ZombieDeath(pos, false); break;
            case Snd.ZombieAttack: ZombieAttack(pos, false); break;
            case Snd.ZombieExcited: ZombieExcited(pos, false); break;
            case Snd.ZombieScream: ZombieScream(pos, false); break;
            case Snd.ZombieSnicker: ZombieSnicker(pos, false); break;
            case Snd.TakerGrowl: TakerGrowl(pos, false); break;
            case Snd.TakerGrunt: TakerGrunt(pos, false); break;
            case Snd.TakerBone: TakerBone(pos, false); break;
            case Snd.TakerDeath: TakerDeath(pos, false); break;
            case Snd.TakerLaugh: TakerLaugh(pos, false); break;
            case Snd.Thud: Thud(pos, false); break;
        }
    }
    private void At(AudioStreamWav w, Vector3 pos, float db, float maxDist, float pitch = 1f)
    {
        if (_pool3d == null || w == null) return;
        var p = _pool3d[_pi3]; _pi3 = (_pi3 + 1) % _pool3d.Length;
        p.Stream = w; p.VolumeDb = db; p.MaxDistance = maxDist; p.PitchScale = pitch; p.GlobalPosition = pos; p.Play();   // reset pitch each play (was leaking a prior caller's pitch)
    }

    // (NEW) universal damage-instance feedback — a soft "tick" on every hit, a bright "plink" on a crit.
    // Spatial at the struck foe; plays on whichever machine resolves the hit (attacker hears their own). Its own pool.
    public void DamageTick(Vector3 pos, bool crit)
    {
        if (_hitPool == null) return;
        var w = crit ? _critPlink : _damageTick;
        if (w == null) return;
        var p = _hitPool[_hpi]; _hpi = (_hpi + 1) % _hitPool.Length;
        p.Stream = w; p.VolumeDb = crit ? -6f : -15f; p.MaxDistance = crit ? 62f : 46f; p.GlobalPosition = pos; p.Play();
    }

    private static AudioStreamWav BuildWade()
    {
        int rate = 22050, n = (int)(rate * 0.22f); var s = new float[n]; var rng = new System.Random((int)GD.Randi());
        float lp = 0f, lp2 = 0f;
        for (int i = 0; i < n; i++)
        {
            float tt = i / (float)rate;
            float env = Mathf.Sin(Mathf.Pi * Mathf.Clamp(tt / 0.22f, 0f, 1f));   // gentle swell in/out — no attack click
            float white = (float)(rng.NextDouble() * 2 - 1);
            lp += (white - lp) * 0.10f;                                          // two one-pole low passes → muffled, no harsh highs
            lp2 += (lp - lp2) * 0.10f;
            s[i] = lp2 * env * 0.5f;
        }
        return Wav(s, rate);
    }

    private static AudioStreamWav BuildSplash()
    {
        int rate = 22050, n = (int)(rate * 0.30f); var s = new float[n]; var rng = new System.Random((int)GD.Randi());
        float prev = 0f;
        for (int i = 0; i < n; i++)
        {
            float tt = i / (float)rate, k = tt / 0.30f;
            float env = Mathf.Min(1f, tt / 0.004f) * Mathf.Exp(-tt * 9f);
            float plunk = Mathf.Sin(tt * Tau * Mathf.Lerp(420f, 140f, k)) * 0.4f;        // pitch drops as the water closes back over
            float white = (float)(rng.NextDouble() * 2 - 1);
            float hp = white - prev; prev = white;                                       // high-passed → splashy hiss
            s[i] = (plunk + hp * 0.5f * Mathf.Exp(-tt * 6f)) * env * 0.7f;
        }
        return Wav(s, rate);
    }

    private static AudioStreamWav BuildCrunch()
    {
        int rate = 22050, n = (int)(rate * 0.24f); var s = new float[n]; var rng = new System.Random((int)GD.Randi());
        float prev = 0f;
        for (int i = 0; i < n; i++)
        {
            float tt = i / (float)rate, k = tt / 0.24f;
            float env = Mathf.Min(1f, tt / 0.005f) * Mathf.Exp(-tt * 14f);            // sharp attack, quick wet decay
            float thud = Mathf.Sin(tt * Tau * Mathf.Lerp(150f, 60f, k)) * 0.5f;       // the body caving in (pitch drops)
            float white = (float)(rng.NextDouble() * 2 - 1);
            float hp = white - prev; prev = white;                                    // high-passed noise → juicy splat hiss
            float crackle = (rng.NextDouble() < 0.12) ? (float)(rng.NextDouble() * 2 - 1) * 0.7f * Mathf.Exp(-tt * 8f) : 0f;   // rind cracks
            s[i] = (thud + hp * 0.45f + crackle) * env * 0.8f;
        }
        return Wav(s, rate);
    }

    private static AudioStreamWav BuildEnemyGrowl()
    {
        int rate = 22050, n = (int)(rate * 0.32f); var s = new float[n]; var rng = new System.Random((int)GD.Randi());
        for (int i = 0; i < n; i++)
        {
            float tt = i / (float)rate; float env = Mathf.Min(1f, tt / 0.04f) * Mathf.Exp(-tt * 4f);
            float f = 95f * (1f + 0.04f * Mathf.Sin(tt * Tau * 18f));                 // low growl with a snarl wobble
            float body = Mathf.Sin(tt * Tau * f) * 0.5f + Mathf.Sin(tt * Tau * f * 1.5f) * 0.25f;
            float grit = ((float)rng.NextDouble() * 2 - 1) * 0.3f;
            s[i] = (body + grit * 0.4f) * env * 0.7f;
        }
        return Wav(s, rate);
    }
    private static AudioStreamWav BuildEnemyShoot()
    {
        int rate = 22050, n = (int)(rate * 0.16f); var s = new float[n]; var rng = new System.Random((int)GD.Randi());
        for (int i = 0; i < n; i++)
        {
            float tt = i / (float)rate, k = tt / 0.16f;
            float f = Mathf.Lerp(700f, 300f, k); float env = Mathf.Exp(-tt * 16f);
            float tone = Mathf.Sin(tt * Tau * f); float noise = ((float)rng.NextDouble() * 2 - 1) * 0.3f * Mathf.Exp(-tt * 30f);
            s[i] = (tone * 0.5f + noise) * env * 0.6f;
        }
        return Wav(s, rate);
    }
    private static AudioStreamWav BuildBossTell()
    {
        int rate = 22050, n = (int)(rate * 0.38f); var s = new float[n]; var rng = new System.Random((int)GD.Randi());
        for (int i = 0; i < n; i++)
        {
            float tt = i / (float)rate, k = tt / 0.38f;
            float env = Mathf.Min(1f, tt / 0.03f) * Mathf.Exp(-tt * 2.6f);
            float f = Mathf.Lerp(70f, 110f, k) * (1f + 0.05f * Mathf.Sin(tt * Tau * 7f));        // rising guttural — a wind-up
            float body = Mathf.Sin(tt * Tau * f) * 0.5f + Mathf.Sin(tt * Tau * f * 1.5f) * 0.28f + Mathf.Sin(tt * Tau * f * 2.01f) * 0.14f;
            float grit = ((float)rng.NextDouble() * 2 - 1) * 0.28f * (0.5f + 0.5f * Mathf.Sin(tt * Tau * 5f));
            s[i] = (body + grit * 0.4f) * env * 0.8f;
        }
        return Wav(s, rate);
    }

    private static AudioStreamWav BuildBossRoar()
    {
        int rate = 22050, n = (int)(rate * 0.8f); var s = new float[n]; var rng = new System.Random((int)GD.Randi());
        for (int i = 0; i < n; i++)
        {
            float tt = i / (float)rate, k = tt / 0.8f;
            float env = Mathf.Min(1f, tt / 0.08f) * Mathf.Exp(-tt * 1.6f);
            float f = Mathf.Lerp(70f, 50f, k) * (1f + 0.05f * Mathf.Sin(tt * Tau * 9f));
            float body = Mathf.Sin(tt * Tau * f) * 0.5f + Mathf.Sin(tt * Tau * f * 1.5f) * 0.3f + Mathf.Sin(tt * Tau * f * 2.02f) * 0.15f;
            float grit = ((float)rng.NextDouble() * 2 - 1) * 0.25f * (0.5f + 0.5f * Mathf.Sin(tt * Tau * 5f));
            s[i] = (body + grit * 0.4f) * env * 0.85f;
        }
        return Wav(s, rate);
    }

    private static AudioStreamWav BuildRustle()
    {
        int rate = 22050, n = (int)(rate * 0.45f); var s = new float[n]; var rng = new System.Random((int)GD.Randi());
        float prev = 0f;
        for (int i = 0; i < n; i++)
        {
            float tt = i / (float)rate; float env = Mathf.Sin(Mathf.Pi * Mathf.Clamp(tt / 0.45f, 0f, 1f));   // soft swell in/out
            float white = (float)(rng.NextDouble() * 2 - 1);
            float hp = white - prev; prev = white;                                                          // crude high-pass → leafy hiss
            float crackle = (rng.NextDouble() < 0.04) ? (float)(rng.NextDouble() * 2 - 1) * 0.5f : 0f;        // tiny twig crackles
            s[i] = (hp * 0.5f + crackle) * env * 0.5f;
        }
        return Wav(s, rate);
    }

    private static AudioStreamWav BuildCreak()
    {
        int rate = 22050, n = (int)(rate * 0.9f); var s = new float[n]; var rng = new System.Random((int)GD.Randi());
        for (int i = 0; i < n; i++)
        {
            float tt = i / (float)rate, k = tt / 0.9f;
            float f = Mathf.Lerp(150f, 70f, k) * (1f + 0.06f * Mathf.Sin(tt * Tau * 7f));                     // bending groan with a creak wobble
            float body = Mathf.Sin(tt * Tau * f);
            float grain = ((float)rng.NextDouble() * 2 - 1) * 0.25f * (0.4f + 0.6f * Mathf.Abs(Mathf.Sin(tt * Tau * 9f)));   // stick-slip friction
            float env = Mathf.Min(1f, tt / 0.05f) * Mathf.Exp(-tt * 1.5f);
            s[i] = (body * 0.5f + grain * 0.5f) * env * 0.7f;
        }
        return Wav(s, rate);
    }

    // procedural "minion" chatter (no voice assets) — squeaky gibberish syllables. kind 3 = a long rising rally cry.
    private static AudioStreamWav BuildMinion(int kind)
    {
        int rate = 22050; var rng = new System.Random((int)GD.Randi());
        int sylls = kind == 3 ? 5 + rng.Next(3) : kind == 1 ? 1 : 2 + rng.Next(kind == 0 ? 2 : 1);
        float sylLen = kind == 1 ? 0.10f : 0.12f;
        int n = (int)(rate * sylLen * sylls); var s = new float[n];
        float basePitch = (230f + (float)rng.NextDouble() * 120f) * (kind == 3 ? 0.9f : 1f);
        for (int sy = 0; sy < sylls; sy++)
        {
            float p0 = basePitch * (0.85f + (float)rng.NextDouble() * 0.5f);
            float p1 = kind == 0 ? p0 * 1.3f : kind == 2 ? p0 * 0.6f : kind == 3 ? (sy == sylls - 1 ? p0 * 1.6f : p0 * 1.1f) : p0 * 1.15f;
            int s0 = (int)(sy * sylLen * rate), s1 = (int)((sy + 1) * sylLen * rate); if (s1 > n) s1 = n;
            for (int i = s0; i < s1; i++)
            {
                float lt = (i - s0) / (float)Mathf.Max(1, s1 - s0); float tt = i / (float)rate;
                float f = Mathf.Lerp(p0, p1, lt);
                float vow = Mathf.Sin(tt * Tau * f) + 0.4f * Mathf.Sin(tt * Tau * f * 2f) + 0.2f * Mathf.Sin(tt * Tau * f * 3.1f);
                float buzz = Mathf.Sign(Mathf.Sin(tt * Tau * f * 0.5f)) * 0.15f;
                float env = Mathf.Sin(Mathf.Pi * lt);
                s[i] += vow * 0.3f + buzz * env;
            }
        }
        for (int i = 0; i < n; i++) s[i] = Mathf.Clamp(s[i] * 0.5f, -1f, 1f);
        return Wav(s, rate);
    }
    public void Discord()             { PlayOne(_discord, -4f); }
    public void RiteWin()             { PlayOne(_riteWin, -4f); }
    public void RiteFail()            { PlayOne(_riteFail, -5f); }

    private static float TypeFreq(DamageType t) => t switch
    {
        DamageType.Lunar => 523f, DamageType.Arcane => 698f, DamageType.Nature => 440f,
        DamageType.Frost => 784f, DamageType.Curse => 311f, DamageType.Holy => 880f,
        DamageType.Ember => 392f, DamageType.Wind => 622f, _ => 466f
    };

    private static AudioStreamWav BuildCast(DamageType t)
    {
        float f0 = TypeFreq(t); int rate = 22050, n = (int)(rate * 0.14f);
        var s = new float[n]; var rng = new System.Random((int)t * 7 + 1);
        for (int i = 0; i < n; i++)
        {
            float tt = i / (float)rate;
            float env = Mathf.Exp(-tt * 16f);
            float bend = 1f + 0.5f * Mathf.Exp(-tt * 30f);                 // quick downward zap
            float v = Mathf.Sin(tt * Tau * f0 * bend) * 0.5f + Mathf.Sin(tt * Tau * f0 * 1.5f) * 0.25f;
            float air = ((float)rng.NextDouble() * 2 - 1) * 0.12f * Mathf.Exp(-tt * 40f);
            s[i] = (v + air) * env * 0.7f;
        }
        return Wav(s, rate);
    }

    private static AudioStreamWav BuildCharge(DamageType t)
    {
        float f0 = TypeFreq(t); int rate = 22050, n = (int)(rate * 0.55f);
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float tt = i / (float)rate; float k = tt / 0.55f;
            float f = Mathf.Lerp(f0 * 0.5f, f0 * 1.05f, k * k);            // rising swell
            float env = Mathf.Min(1f, k * 4f) * (1f - k * 0.2f);
            float trem = 0.8f + 0.2f * Mathf.Sin(tt * Tau * 9f);          // witchy wobble
            s[i] = Mathf.Sin(tt * Tau * f) * 0.45f * env * trem;
        }
        return Wav(s, rate);
    }

    private static AudioStreamWav BuildRelease(DamageType t)
    {
        float f0 = TypeFreq(t); int rate = 22050, n = (int)(rate * 0.4f);
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float tt = i / (float)rate; float env = Mathf.Exp(-tt * 7f);
            float boom = Mathf.Sin(tt * Tau * f0 * 0.5f) * 0.5f;
            float chord = Mathf.Sin(tt * Tau * f0) * 0.3f + Mathf.Sin(tt * Tau * f0 * 1.4983f) * 0.22f;
            s[i] = (boom + chord) * env * 0.8f;
        }
        return Wav(s, rate);
    }

    private static AudioStreamWav BuildImpact(DamageType t)
    {
        float f0 = TypeFreq(t); int rate = 22050, n = (int)(rate * 0.16f);
        var s = new float[n]; var rng = new System.Random((int)t * 11 + 3);
        for (int i = 0; i < n; i++)
        {
            float tt = i / (float)rate; float env = Mathf.Exp(-tt * 20f);
            float thud = Mathf.Sin(tt * Tau * f0 * 0.5f) * 0.5f;
            float spark = Mathf.Sin(tt * Tau * f0 * 2.2f) * 0.2f * Mathf.Exp(-tt * 30f);
            float noise = ((float)rng.NextDouble() * 2 - 1) * 0.18f * Mathf.Exp(-tt * 50f);
            s[i] = (thud + spark + noise) * env * 0.7f;
        }
        return Wav(s, rate);
    }

    private static AudioStreamWav BuildDeath()
    {
        int rate = 22050, n = (int)(rate * 0.32f);
        var s = new float[n]; var rng = new System.Random(99);
        for (int i = 0; i < n; i++)
        {
            float tt = i / (float)rate; float k = tt / 0.32f;
            float f = Mathf.Lerp(360f, 90f, k);                          // dissolving downward
            float env = Mathf.Exp(-tt * 6f);
            float noise = ((float)rng.NextDouble() * 2 - 1) * (0.4f * (1f - k));
            s[i] = (Mathf.Sin(tt * Tau * f) * 0.5f + noise) * env * 0.6f;
        }
        return Wav(s, rate);
    }

    // (NEW) local pained thud when a hit reaches your HP. Bigger hits = lower, meatier, longer.
    private static AudioStreamWav BuildPlayerHurt(float sev)
    {
        int rate = 22050, n = (int)(rate * (0.15f + 0.12f * sev)); var s = new float[n]; var rng = new System.Random((int)GD.Randi());
        float dur = n / (float)rate, baseF = Mathf.Lerp(215f, 120f, sev); float prev = 0f;
        for (int i = 0; i < n; i++)
        {
            float tt = i / (float)rate, k = tt / dur;
            float env = Mathf.Min(1f, tt / 0.004f) * Mathf.Exp(-tt * (17f - 7f * sev));
            float f = Mathf.Lerp(baseF, baseF * 0.55f, k);                        // downward "oof"
            float body = Mathf.Sin(tt * Tau * f) * 0.55f + Mathf.Sin(tt * Tau * f * 1.5f) * 0.22f;
            float white = (float)(rng.NextDouble() * 2 - 1);
            float hp = white - prev; prev = white;                               // fleshy impact hiss
            s[i] = (body + hp * (0.22f + 0.22f * sev)) * env * 0.85f;
        }
        return Wav(s, rate);
    }

    // (NEW) shield emptied — a bright glassy cluster that cracks apart and powers down.
    private static AudioStreamWav BuildShieldBreak()
    {
        int rate = 22050, n = (int)(rate * 0.34f); var s = new float[n]; var rng = new System.Random((int)GD.Randi());
        for (int i = 0; i < n; i++)
        {
            float tt = i / (float)rate;
            float env = Mathf.Min(1f, tt / 0.003f) * Mathf.Exp(-tt * 9f);
            float glass = Mathf.Sin(tt * Tau * 1400f) * 0.3f + Mathf.Sin(tt * Tau * 1870f) * 0.22f + Mathf.Sin(tt * Tau * 2540f) * 0.15f;
            float shard = (rng.NextDouble() < 0.25) ? (float)(rng.NextDouble() * 2 - 1) * 0.6f * Mathf.Exp(-tt * 11f) : 0f;
            float sweep = Mathf.Sin(tt * Tau * Mathf.Lerp(900f, 300f, tt / 0.34f)) * 0.2f * Mathf.Exp(-tt * 6f);   // falling power-down
            s[i] = (glass + shard + sweep) * env * 0.7f;
        }
        return Wav(s, rate);
    }

    // (NEW) armor charge popped — an inharmonic metallic clang.
    private static AudioStreamWav BuildArmorBreak()
    {
        int rate = 22050, n = (int)(rate * 0.26f); var s = new float[n]; var rng = new System.Random((int)GD.Randi());
        for (int i = 0; i < n; i++)
        {
            float tt = i / (float)rate;
            float env = Mathf.Min(1f, tt / 0.002f) * Mathf.Exp(-tt * 13f);
            float clang = Mathf.Sin(tt * Tau * 520f) * 0.35f + Mathf.Sin(tt * Tau * 1190f) * 0.25f + Mathf.Sin(tt * Tau * 1730f) * 0.17f;
            float ring = Mathf.Sin(tt * Tau * 2600f) * 0.12f * Mathf.Exp(-tt * 5f);
            float grit = ((float)rng.NextDouble() * 2 - 1) * 0.25f * Mathf.Exp(-tt * 20f);
            s[i] = (clang + ring + grit) * env * 0.75f;
        }
        return Wav(s, rate);
    }

    // (NEW) low-health heartbeat — a lub-dub of low sine thumps.
    private static AudioStreamWav BuildHeartbeat()
    {
        int rate = 22050, n = (int)(rate * 0.30f); var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float tt = i / (float)rate;
            float lub = Mathf.Sin(tt * Tau * 62f) * Mathf.Min(1f, tt / 0.004f) * Mathf.Exp(-tt * 30f);
            float t2 = tt - 0.13f;
            float dub = t2 > 0f ? Mathf.Sin(t2 * Tau * 54f) * Mathf.Min(1f, t2 / 0.004f) * Mathf.Exp(-t2 * 34f) * 0.8f : 0f;
            s[i] = (lub + dub) * 0.9f;
        }
        return Wav(s, rate);
    }

    // (NEW) spatial "incoming" whistle — a rising airy tone that reads as a projectile bearing down on you.
    private static AudioStreamWav BuildIncoming()
    {
        int rate = 22050, n = (int)(rate * 0.42f); var s = new float[n]; var rng = new System.Random((int)GD.Randi());
        for (int i = 0; i < n; i++)
        {
            float tt = i / (float)rate, k = tt / 0.42f;
            float env = Mathf.Min(1f, tt / 0.02f) * Mathf.Min(1f, (1f - k) / 0.25f);   // swell in, cut near the end
            float f = Mathf.Lerp(520f, 1150f, k * k);                                   // rising whistle = approaching
            float tone = Mathf.Sin(tt * Tau * f) * 0.5f + Mathf.Sin(tt * Tau * f * 2.01f) * 0.12f;
            float air = ((float)rng.NextDouble() * 2 - 1) * 0.12f;                       // faint wind hiss
            s[i] = (tone + air) * env * 0.5f;
        }
        return Wav(s, rate);
    }

    // (NEW) one-shot low-health alarm — a tense descending double-sting.
    private static AudioStreamWav BuildLowHealth()
    {
        int rate = 22050, n = (int)(rate * 0.5f); var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float tt = i / (float)rate, k = tt / 0.5f;
            float env = Mathf.Min(1f, tt / 0.006f) * Mathf.Exp(-tt * 3.4f);
            float wob = 1f + 0.02f * Mathf.Sin(tt * Tau * 11f);
            float f = Mathf.Lerp(500f, 330f, k) * wob;                            // descending, uneasy
            float body = Mathf.Sin(tt * Tau * f) * 0.4f + Mathf.Sin(tt * Tau * f * 0.5f) * 0.3f + Mathf.Sin(tt * Tau * f * 1.5f) * 0.12f;
            s[i] = body * env * 0.6f;
        }
        return Wav(s, rate);
    }

    private static AudioStreamWav BuildRiteWin()
    {
        int rate = 22050, n = (int)(rate * 0.75f);
        var s = new float[n];
        float root = 523.25f;                                   // C5
        float[] steps = { 1f, 1.2599f, 1.4983f, 2f };           // major triad + octave, arpeggiated
        for (int i = 0; i < n; i++)
        {
            float tt = i / (float)rate; float k = tt / 0.75f;
            int stage = Mathf.Clamp((int)(k * 4f), 0, 3);
            float local = k * 4f - stage;                       // 0..1 within the current note
            float f = root * steps[stage];
            float env = Mathf.Min(1f, local * 8f) * Mathf.Exp(-local * 2.2f);
            float tone = Mathf.Sin(tt * Tau * f) * 0.45f + Mathf.Sin(tt * Tau * f * 2f) * 0.16f;
            float bells = Mathf.Sin(tt * Tau * root * 4f) * 0.05f * Mathf.Exp(-tt * 2.5f);
            s[i] = (tone + bells) * env * 0.7f;
        }
        return Wav(s, rate);
    }

    private static AudioStreamWav BuildRiteFail()
    {
        int rate = 22050, n = (int)(rate * 0.6f);
        var s = new float[n]; var rng = new System.Random(57);
        for (int i = 0; i < n; i++)
        {
            float tt = i / (float)rate; float k = tt / 0.6f;
            float f = (k < 0.5f) ? 392f : 311.13f;              // G4 -> Eb4, a sinking minor third
            float local = (k < 0.5f) ? k : (k - 0.5f);
            float env = Mathf.Exp(-local * 2f * 3.4f);
            float detune = Mathf.Sin(tt * Tau * f * 1.006f) * 0.18f;
            float buzz = ((float)rng.NextDouble() * 2 - 1) * 0.06f * Mathf.Exp(-tt * 7f);
            s[i] = (Mathf.Sin(tt * Tau * f) * 0.4f + detune + buzz) * env * 0.6f;
        }
        return Wav(s, rate);
    }

    // (NEW) witchy warding drone — an open-fifth lunar pad that breathes, with a faint high shimmer. 2s loop; every partial
    // and modulator completes whole cycles over the loop, so it repeats seamlessly. Pitch is scaled live by WardCharge().
    public static AudioStreamWav BuildWardHum()
    {
        int rate = 22050, n = (int)(rate * 2f); var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate;
            float breathe = 0.62f + 0.38f * Mathf.Sin(t * Tau * 1.5f);                                              // slow swell (3 cycles / 2s)
            float drone = Mathf.Sin(t * Tau * 196f) * 0.34f + Mathf.Sin(t * Tau * 294f) * 0.22f + Mathf.Sin(t * Tau * 392f) * 0.12f;  // G3 + D4 + G4 open fifth — mystical
            float shimmer = (Mathf.Sin(t * Tau * 1176f) * 0.06f + Mathf.Sin(t * Tau * 1568f) * 0.04f) * (0.5f + 0.5f * Mathf.Sin(t * Tau * 7f)); // high sparkle
            s[i] = (drone * breathe + shimmer) * 0.32f;
        }
        var w = Wav(s, rate); w.LoopMode = AudioStreamWav.LoopModeEnum.Forward; w.LoopBegin = 0; w.LoopEnd = n; return w;
    }

    // (NEW) warding-complete relief ding — a bright major chord struck together (resolve), inharmonic bell partials on top,
    // and a shimmering magical tail that rings out. Witchy + triumphant.
    private static AudioStreamWav BuildWardComplete()
    {
        int rate = 22050, n = (int)(rate * 1.15f); var s = new float[n];
        float root = 523.25f;                                   // C5
        float[] chord = { 1f, 1.25f, 1.5f, 2f };                // C E G C — struck all at once
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate;
            float env = Mathf.Min(1f, t / 0.006f) * Mathf.Exp(-t * 2.2f);
            float ch = 0f; foreach (var m in chord) ch += Mathf.Sin(t * Tau * root * m);
            ch *= 0.16f;
            float bell = (Mathf.Sin(t * Tau * root * 4f) * 0.12f + Mathf.Sin(t * Tau * root * 6.2f) * 0.06f) * Mathf.Exp(-t * 3.4f);         // inharmonic bell
            float sparkle = Mathf.Sin(t * Tau * (1900f + 420f * Mathf.Sin(t * Tau * 5f))) * 0.05f * Mathf.Exp(-t * 1.9f);                    // shimmer tail
            s[i] = (ch + bell + sparkle) * env * 0.8f;
        }
        return Wav(s, rate);
    }

    // (NEW) arcane-spectre unicorn spawn — a bright rising four-note shimmer over an airy breath. Ethereal, otherworldly.
    private static AudioStreamWav BuildUnicornCall()
    {
        int rate = 22050, n = (int)(rate * 0.95f); var s = new float[n]; var rng = new System.Random(7);
        float root = 587.33f; float[] steps = { 1f, 1.5f, 2f, 2.667f };
        float lp = 0f;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate, k = t / 0.95f;
            int stage = Mathf.Clamp((int)(k * 4f), 0, 3); float local = k * 4f - stage;
            float f = root * steps[stage];
            float env = Mathf.Min(1f, local * 9f) * Mathf.Exp(-local * 1.7f);
            float tone = Mathf.Sin(t * Tau * f) * 0.42f + Mathf.Sin(t * Tau * f * 2.01f) * 0.16f + Mathf.Sin(t * Tau * f * 3f) * 0.07f;
            float noise = (float)(rng.NextDouble() * 2 - 1); lp += (noise - lp) * 0.05f;
            float breath = lp * 0.1f * Mathf.Exp(-t * 1.2f);
            s[i] = (tone * env + breath) * 0.6f;
        }
        return Wav(s, rate);
    }
    // (NEW) the unicorn's charge — a swelling upward magical whoosh (rising pitch + gallop pulse)
    private static AudioStreamWav BuildUnicornCharge()
    {
        int rate = 22050, n = (int)(rate * 0.7f); var s = new float[n]; var rng = new System.Random(21);
        float lp = 0f;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate, k = t / 0.7f;
            float f = Mathf.Lerp(300f, 1100f, k * k);
            float env = Mathf.Min(1f, t / 0.05f) * (1f - k * 0.3f);
            float tone = (Mathf.Sin(t * Tau * f) + Mathf.Sin(t * Tau * f * 1.5f) * 0.5f) * 0.3f;
            float noise = (float)(rng.NextDouble() * 2 - 1); lp += (noise - lp) * 0.2f;
            float whoosh = lp * 0.25f * k;
            float gallop = 0.6f + 0.4f * Mathf.Sin(t * Tau * 8f);
            s[i] = (tone + whoosh) * env * gallop * 0.7f;
        }
        return Wav(s, rate);
    }
    // (NEW) the arcane nuke — a sub-bass thump, a sharp crack, a long low-passed rumble, and a magical high shimmer tail
    private static AudioStreamWav BuildArcaneBoom()
    {
        int rate = 22050, n = (int)(rate * 2.2f); var s = new float[n]; var rng = new System.Random(99);
        float lp = 0f, lp2 = 0f;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate;
            float subF = Mathf.Lerp(95f, 36f, Mathf.Clamp(t / 0.6f, 0f, 1f));
            float sub = Mathf.Sin(t * Tau * subF) * Mathf.Exp(-t * 1.5f) * 0.9f;
            float noise = (float)(rng.NextDouble() * 2 - 1);
            lp += (noise - lp) * 0.08f; lp2 += (lp - lp2) * 0.08f;
            float crack = noise * Mathf.Exp(-t * 26f) * 0.5f;
            float rumble = lp2 * Mathf.Exp(-t * 1.05f) * 0.7f;
            float shim = (Mathf.Sin(t * Tau * 1400f) + Mathf.Sin(t * Tau * 2100f)) * 0.04f * Mathf.Exp(-t * 2.3f) * (0.5f + 0.5f * Mathf.Sin(t * Tau * 9f));
            s[i] = Mathf.Clamp(sub + crack + rumble + shim, -1f, 1f);
        }
        return Wav(s, rate);
    }

    private static AudioStreamWav BuildDiscord()
    {
        int rate = 22050, n = (int)(rate * 0.5f);
        var s = new float[n];
        float r = 233f;
        for (int i = 0; i < n; i++)
        {
            float tt = i / (float)rate; float env = Mathf.Exp(-tt * 4.5f) * Mathf.Min(1f, tt * 50f);
            // clustered dissonance: root, minor 2nd, tritone
            float v = Mathf.Sin(tt * Tau * r) * 0.4f + Mathf.Sin(tt * Tau * r * 1.0595f) * 0.34f + Mathf.Sin(tt * Tau * r * 1.4142f) * 0.3f;
            s[i] = v * env * 0.55f;
        }
        return Wav(s, rate);
    }

    // witchy synth chord played when a combo is advanced by a *different* action
    public void Chord(int combo)
    {
        if (_chord == null) return;
        _chord.Stream = BuildChord(combo);
        _chord.Play();
    }

    // music tempo follows your fire rate / combat energy (drums stay locked to the arp)
    public void SetTempo(float pitch)
    {
        pitch = Mathf.Clamp(pitch, 0.85f, 1.6f);
        if (_music != null) _music.PitchScale = pitch;
        if (_drums != null) _drums.PitchScale = pitch;
        if (_hauntMus != null) _hauntMus.PitchScale = pitch;   // (HAUNT) the eerie layer speeds up with the combat tempo too
    }

    // drum presence rises with tension: silent when calm, driving when things get hairy
    public float MusicVol = 0.8f;        // 0..1 base music volume (player-adjustable)
    private float MusicGainDb => Mathf.Lerp(-40f, 0f, Mathf.Clamp(MusicVol, 0f, 1f));

    // (DIAGNOSTIC) hard mutes so a periodic click can be isolated to a single looping stream — see the `audio` dev command
    public bool MuteDrums = false, MuteMusic = false;
    public void SetIntensity(float t)
    {
        if (_drums == null) return;
        if (MuteDrums) { _drums.VolumeDb = -80f; if (_music != null) _music.VolumeDb = MuteMusic ? -80f : (EventActive ? -11f : -15f) + MusicGainDb; return; }
        if (MuteMusic && _music != null) _music.VolumeDb = -80f;
        float eff = Mathf.Clamp(Mathf.Max(t, EventActive ? 0.5f : 0f), 0f, 1f);
        // (FIX) the old -42dB "silent" floor wasn't silent — in a quiet moment you could hear the dry shaker ticking under
        // the arp. Drop the floor far enough that a calm scene really is just the melody.
        _drums.VolumeDb = (eff < 0.02f ? -80f : Mathf.Lerp(-54f, -7f, eff)) + MusicGainDb;
        if (_music != null) _music.VolumeDb = MuteMusic ? -80f : (EventActive ? -11f : -15f) + MusicGainDb;
        // (HAUNT) fade the eerie layer in/out with HauntBlend; it swells with combat intensity while you're in the storm
        _hauntBlendCur = Mathf.Lerp(_hauntBlendCur, HauntBlend, 0.05f);   // ~0.35s dynamic crossfade
        if (_hauntMus != null)
        {
            float target = Mathf.Lerp(-19f, -6f, eff);                    // louder as the fight heats up
            _hauntMus.VolumeDb = (MuteMusic || _hauntBlendCur < 0.01f) ? -80f : Mathf.Lerp(-80f, target, _hauntBlendCur) + MusicGainDb;
        }
    }
    public void Element(DamageType t)
    {
        if (_elem == null) return;
        _elem.Stream = BuildElement(t);
        _elem.Play();
    }

    // gentle looping shimmer that radiates from the loot goblin
    public static AudioStreamWav ShimmerStream()
    {
        int rate = 22050, n = (int)(rate * 0.5f);
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate;
            float wob = 0.55f + 0.45f * Mathf.Sin(t * Tau * 6.5f);
            float v = (Mathf.Sin(t * Tau * 1568f) * 0.4f + Mathf.Sin(t * Tau * 2093f) * 0.3f + Mathf.Sin(t * Tau * 3136f) * 0.18f) * wob;
            s[i] = v * 0.3f;
        }
        var w = Wav(s, rate);
        w.LoopMode = AudioStreamWav.LoopModeEnum.Forward;
        w.LoopBegin = 0;
        w.LoopEnd = n;
        return w;
    }

    public static AudioStreamWav PortalHumStream()   // low magical drone at the exit portal
    {
        int rate = 22050, n = (int)(rate * 1.2f); var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate;
            float wob = 0.6f + 0.4f * Mathf.Sin(t * Tau * 1.3f);
            float v = (Mathf.Sin(t * Tau * 110f) * 0.4f + Mathf.Sin(t * Tau * 165f) * 0.25f + Mathf.Sin(t * Tau * 220f) * 0.12f) * wob;
            s[i] = v * 0.3f;
        }
        var w = Wav(s, rate); w.LoopMode = AudioStreamWav.LoopModeEnum.Forward; w.LoopBegin = 0; w.LoopEnd = n; return w;
    }

    // eerie overlapping whispers for the maze darkness veil — breathy shaped-noise "syllables" from a few
    // detuned voices, with sibilant onsets and a faint low murmur underneath. Loops seamlessly. (NEW)
    public static AudioStreamWav WhisperStream()
    {
        int rate = 22050, n = (int)(rate * 4f); var s = new float[n]; var rng = new System.Random(31337);
        float lp = 0f;
        float[] vPhase = { 0f, 1.7f, 3.4f }, vRate = { 3.1f, 2.3f, 4.4f }, vf = { 900f, 1300f, 700f }, vp = { 1f, 0.72f, 1.35f };
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate;
            float noise = (float)(rng.NextDouble() * 2 - 1);
            lp += (noise - lp) * 0.14f;         // heavy low-pass → soft breath
            float breath = lp, hiss = noise - lp;   // residue = sibilance (s / sh)
            float v = 0f;
            for (int k = 0; k < 3; k++)
            {
                float ph = t * vRate[k] + vPhase[k];
                float frac = ph - Mathf.Floor(ph);
                float vowel = frac < 0.5f ? 0.5f - 0.5f * Mathf.Cos(frac / 0.5f * Tau) : 0f;   // whispered "word", then a gap
                float con = frac < 0.1f ? (1f - frac / 0.1f) : 0f;                              // sibilant consonant onset
                float form = 0.6f + 0.4f * Mathf.Sin(t * Tau * vf[k] * vp[k]);                  // formant colour (ring-mod tint)
                v += (breath * vowel * form + hiss * con * 0.5f) * 0.55f;
            }
            v += Mathf.Sin(t * Tau * 64f) * 0.03f * (0.5f + 0.5f * Mathf.Sin(t * Tau * 0.6f));   // low murmur bed
            s[i] = Mathf.Clamp(v * 0.5f, -1f, 1f);
        }
        var w = Wav(s, rate); w.LoopMode = AudioStreamWav.LoopModeEnum.Forward; w.LoopBegin = 0; w.LoopEnd = n; return w;
    }

    public static AudioStreamWav FairyDustStream()   // tiny magical dust shimmer around the fairy
    {
        int rate = 22050, n = (int)(rate * 0.9f); var s = new float[n]; var rng = new System.Random(7);
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate;
            float tw = 0.5f + 0.5f * Mathf.Sin(t * Tau * 9f);
            float sparkle = Mathf.Sin(t * Tau * 2600f) * 0.15f * tw + Mathf.Sin(t * Tau * 3400f) * 0.1f * (0.5f + 0.5f * Mathf.Sin(t * Tau * 13f));
            float shimmer = (float)(rng.NextDouble() * 2 - 1) * 0.04f * tw;
            s[i] = (sparkle + shimmer) * 0.25f;
        }
        var w = Wav(s, rate); w.LoopMode = AudioStreamWav.LoopModeEnum.Forward; w.LoopBegin = 0; w.LoopEnd = n; return w;
    }

    private static AudioStreamWav BuildZombieAttack()
    {
        int rate = 22050, n = (int)(rate * 0.4f); var s = new float[n]; var rng = new System.Random((int)GD.Randi());
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate, k = t / 0.4f;
            float env = Mathf.Min(1f, t / 0.01f) * Mathf.Exp(-t * 5f);
            float growl = Mathf.Sin(t * Tau * Mathf.Lerp(150f, 90f, k)) * 0.4f;
            float rasp = (float)(rng.NextDouble() * 2 - 1) * 0.3f;
            s[i] = (growl + rasp * (0.4f + 0.4f * Mathf.Sin(t * Tau * 30f))) * env * 0.7f;   // guttural bite
        }
        return Wav(s, rate);
    }

    private static AudioStreamWav BuildZombieExcited()
    {
        int rate = 22050, n = (int)(rate * 0.5f); var s = new float[n]; var rng = new System.Random((int)GD.Randi());
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate, k = t / 0.5f;
            float env = Mathf.Min(1f, t / 0.02f) * Mathf.Min(1f, (1f - k) / 0.2f + 0.001f);
            float growl = Mathf.Sin(t * Tau * Mathf.Lerp(120f, 240f, k)) * 0.4f;   // rising excited growl
            float rasp = (float)(rng.NextDouble() * 2 - 1) * 0.25f;
            s[i] = (growl + rasp) * env * 0.7f;
        }
        return Wav(s, rate);
    }

    private static AudioStreamWav BuildZombieScream()
    {
        int rate = 22050, n = (int)(rate * 0.8f); var s = new float[n]; var rng = new System.Random((int)GD.Randi());
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate, k = t / 0.8f;
            float env = Mathf.Min(1f, t / 0.01f) * Mathf.Min(1f, (1f - k) / 0.25f + 0.001f);
            float pitch = Mathf.Lerp(280f, 700f, Mathf.Min(1f, k * 2f)) * (1f + 0.08f * Mathf.Sin(t * Tau * 18f));   // rising shriek + vibrato
            float voice = Mathf.Sin(t * Tau * pitch) * 0.4f + Mathf.Sin(t * Tau * pitch * 1.5f) * 0.2f;
            float rasp = (float)(rng.NextDouble() * 2 - 1) * 0.2f;
            s[i] = (voice + rasp) * env * 0.8f;
        }
        return Wav(s, rate);
    }

    private static AudioStreamWav BuildFreeze()   // rising icy crackle as the enemy encases in ice
    {
        int rate = 22050, n = (int)(rate * 0.5f); var s = new float[n]; var rng = new System.Random((int)GD.Randi());
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate;
            float env = Mathf.Min(1f, t * 8f) * Mathf.Exp(-t * 4f);
            float shimmer = Mathf.Sin(t * Tau * (900f + 700f * t)) * 0.3f;               // rising glassy tone
            float crackle = (float)(rng.NextDouble() * 2 - 1) * 0.35f * (0.4f + 0.6f * Mathf.Abs(Mathf.Sin(t * 60f)));   // frost crackle
            s[i] = (shimmer + crackle) * env;
        }
        return Wav(s, rate);
    }

    private static AudioStreamWav BuildCurseCrush()   // voodoo doll crush — a wet crunch, a low thud, and a dark downward implosion
    {
        int rate = 22050, n = (int)(rate * 0.45f); var s = new float[n]; var rng = new System.Random((int)GD.Randi());
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate;
            float env = Mathf.Exp(-t * 7f);
            float thud = Mathf.Sin(t * Tau * (115f - 65f * t)) * 0.7f * Mathf.Exp(-t * 10f);   // low body thud, pitch drops
            float squelch = (float)(rng.NextDouble() * 2 - 1) * 0.38f * Mathf.Exp(-t * 13f) * (0.5f + 0.5f * Mathf.Sin(t * Tau * 38f));   // wet squelch (wobbled noise)
            float dark = Mathf.Sin(t * Tau * (320f - 240f * t)) * 0.3f * Mathf.Exp(-t * 5f);    // dark downward magic sweep
            float crunch = (float)(rng.NextDouble() * 2 - 1) * 0.5f * Mathf.Exp(-t * 42f);       // sharp initial crunch
            s[i] = (thud + squelch + dark + crunch) * env;
        }
        return Wav(s, rate);
    }

    // A dark curse incantation for the Hex combos — an ominous rising drone, a deep sub, a detuned "curse chord"
    // shimmer with slow vibrato, and a soft bell "seal". Fully tonal/harmonic — no white-noise rasp (replaces the
    // scratchy WitchCackle that read like a record scratch).
    private static AudioStreamWav BuildHexWeave()
    {
        int rate = 22050, n = (int)(rate * 0.6f); var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate;
            float env = Mathf.Exp(-t * 3.0f);
            float drone = Mathf.Sin(t * Tau * (68f + 84f * t)) * 0.5f;                                   // ominous rising drone 68→152
            float sub = Mathf.Sin(t * Tau * 44f) * 0.28f * Mathf.Exp(-t * 2.5f);                         // deep sub weight
            float shimmer = (Mathf.Sin(t * Tau * 415f) + Mathf.Sin(t * Tau * 622f) * 0.6f + Mathf.Sin(t * Tau * 311f) * 0.5f)
                            * 0.14f * (0.55f + 0.45f * Mathf.Sin(t * Tau * 5.5f));                       // detuned curse chord + slow vibrato
            float seal = Mathf.Sin(t * Tau * 260f) * 0.35f * Mathf.Exp(-Mathf.Abs(t - 0.05f) * 36f);     // a soft 'seal' bell early on
            s[i] = (drone + sub + shimmer + seal) * env;
        }
        return Wav(s, rate);
    }

    // A soft airy whoosh (low-passed noise swelling up then down + a faint downward wind tone). No harsh rasp — the
    // noise is heavily smoothed so it reads as moving air, not static.
    private static AudioStreamWav BuildWhish()
    {
        int rate = 22050, n = (int)(rate * 0.3f); var s = new float[n]; var rng = new System.Random((int)GD.Randi());
        float lp = 0f;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate, u = t / 0.3f;
            float env = Mathf.Sin(u * Mathf.Pi);                                   // swells up and fades — the "whoosh" shape
            float white = (float)(rng.NextDouble() * 2 - 1);
            lp = lp * 0.84f + white * 0.16f;                                        // low-pass → airy, not scratchy
            float tone = Mathf.Sin(t * Tau * (620f - 320f * u)) * 0.12f;           // faint wind tone sweeping down
            s[i] = (lp * 0.7f + tone) * env * 0.5f;
        }
        return Wav(s, rate);
    }

    private static AudioStreamWav BuildIceShatter()   // bright glassy shatter + tinkling shards
    {
        int rate = 22050, n = (int)(rate * 0.5f); var s = new float[n]; var rng = new System.Random((int)GD.Randi());
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate;
            float env = Mathf.Exp(-t * 9f);
            float crack = (float)(rng.NextDouble() * 2 - 1) * 0.6f * Mathf.Exp(-t * 30f);   // initial break
            float tink = (Mathf.Sin(t * Tau * 3200f) + Mathf.Sin(t * Tau * 4700f) * 0.7f + Mathf.Sin(t * Tau * 6100f) * 0.5f) * 0.25f * Mathf.Exp(-t * 6f);   // shards
            s[i] = (crack + tink) * env;
        }
        return Wav(s, rate);
    }

    private static AudioStreamWav BuildPoof()   // soft airy magical "poof" — a quick descending whoosh + shimmer
    {
        int rate = 22050, n = (int)(rate * 0.28f); var s = new float[n]; var rng = new System.Random((int)GD.Randi());
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate;
            float env = Mathf.Exp(-t * 13f);
            float air = (float)(rng.NextDouble() * 2 - 1) * 0.5f;                 // breathy noise
            float tone = Mathf.Sin(t * Tau * (620f - 300f * t)) * 0.35f;          // downward whoosh
            float shimmer = Mathf.Sin(t * Tau * 1560f) * 0.12f * Mathf.Exp(-t * 22f);   // sparkle
            s[i] = (air * 0.5f + tone + shimmer) * env;
        }
        return Wav(s, rate);
    }

    private static AudioStreamWav BuildThud()   // heavy low-frequency body-slam boom
    {
        int rate = 22050, n = (int)(rate * 0.4f); var s = new float[n]; var rng = new System.Random((int)GD.Randi());
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate;
            float env = Mathf.Exp(-t * 12f);
            float boom = Mathf.Sin(t * Tau * (60f - 40f * t)) * 0.7f;   // pitch drops fast
            float thwack = (float)(rng.NextDouble() * 2 - 1) * 0.4f * Mathf.Exp(-t * 40f);   // initial crack
            s[i] = (boom + thwack) * env;
        }
        return Wav(s, rate);
    }

    private static AudioStreamWav BuildTakerLaugh()   // snarled guttural cackle ("hur-hur-hur" with a growl under it)
    {
        int rate = 22050, n = (int)(rate * 0.9f); var s = new float[n]; var rng = new System.Random((int)GD.Randi());
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate, k = t / 0.9f;
            float env = Mathf.Min(1f, t / 0.03f) * Mathf.Min(1f, (1f - k) / 0.2f + 0.001f);
            float gate = (0.5f + 0.5f * Mathf.Sin(t * Tau * 6f)) > 0.55f ? 1f : 0.3f;   // chuckle bursts
            float growl = Mathf.Sin(t * Tau * (70f + 25f * Mathf.Sin(t * Tau * 6f))) * 0.45f;
            float rasp = (float)(rng.NextDouble() * 2 - 1) * 0.3f;
            s[i] = (growl + rasp) * gate * env * 0.75f;
        }
        return Wav(s, rate);
    }

    private static AudioStreamWav BuildTakerGrowl()   // long, deep, menacing growl
    {
        int rate = 22050, n = (int)(rate * 1.3f); var s = new float[n]; var rng = new System.Random((int)GD.Randi());
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate, k = t / 1.3f;
            float env = Mathf.Min(1f, t / 0.08f) * Mathf.Min(1f, (1f - k) / 0.3f + 0.001f);
            float f = 55f + 12f * Mathf.Sin(t * Tau * 2.5f);
            float growl = Mathf.Sin(t * Tau * f) * 0.5f + Mathf.Sin(t * Tau * f * 1.5f) * 0.2f;
            float rasp = (float)(rng.NextDouble() * 2 - 1) * 0.3f * (0.5f + 0.5f * Mathf.Sin(t * Tau * 24f));
            s[i] = (growl + rasp) * env * 0.8f;
        }
        return Wav(s, rate);
    }

    private static AudioStreamWav BuildTakerGrunt()   // short deep grunt
    {
        int rate = 22050, n = (int)(rate * 0.5f); var s = new float[n]; var rng = new System.Random((int)GD.Randi());
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate;
            float env = Mathf.Min(1f, t / 0.02f) * Mathf.Exp(-t * 5f);
            float g = Mathf.Sin(t * Tau * (62f - 20f * t)) * 0.5f;
            float rasp = (float)(rng.NextDouble() * 2 - 1) * 0.28f;
            s[i] = (g + rasp) * env * 0.7f;
        }
        return Wav(s, rate);
    }

    private static AudioStreamWav BuildTakerBone()    // wet crack / bone break
    {
        int rate = 22050, n = (int)(rate * 0.35f); var s = new float[n]; var rng = new System.Random((int)GD.Randi());
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate;
            float crack = 0f;
            foreach (float ct in new[] { 0.02f, 0.09f, 0.16f, 0.24f })   // a few sharp snaps
                if (t > ct && t < ct + 0.015f) crack += (float)(rng.NextDouble() * 2 - 1);
            float low = Mathf.Sin(t * Tau * 90f) * 0.2f * Mathf.Exp(-t * 8f);
            s[i] = (crack * 0.7f + low) * Mathf.Min(1f, t / 0.005f);
        }
        return Wav(s, rate);
    }

    private static AudioStreamWav BuildTakerDeath()   // deep drawn-out "ughhh"
    {
        int rate = 22050, n = (int)(rate * 1.1f); var s = new float[n]; var rng = new System.Random((int)GD.Randi());
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate, k = t / 1.1f;
            float env = Mathf.Min(1f, t / 0.03f) * Mathf.Min(1f, (1f - k) / 0.4f + 0.001f);
            float f = Mathf.Lerp(75f, 42f, k);   // pitch falls as it dies
            float voice = Mathf.Sin(t * Tau * f) * 0.5f + Mathf.Sin(t * Tau * f * 1.5f) * 0.18f;
            float rasp = (float)(rng.NextDouble() * 2 - 1) * 0.25f;
            s[i] = (voice + rasp) * env * 0.8f;
        }
        return Wav(s, rate);
    }

    private static AudioStreamWav BuildHordeScream()
    {
        int rate = 22050, n = (int)(rate * 1.6f); var s = new float[n]; var rng = new System.Random((int)GD.Randi());
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate, k = t / 1.6f;
            float env = Mathf.Min(1f, t / 0.05f) * Mathf.Min(1f, (1f - k) / 0.3f + 0.001f);
            float p1 = Mathf.Lerp(220f, 520f, Mathf.Min(1f, k * 1.5f)) * (1f + 0.06f * Mathf.Sin(t * Tau * 14f));
            float voice = Mathf.Sin(t * Tau * p1) * 0.3f + Mathf.Sin(t * Tau * p1 * 1.34f) * 0.22f + Mathf.Sin(t * Tau * p1 * 0.5f) * 0.2f;   // a chorus of detuned screams
            float rasp = (float)(rng.NextDouble() * 2 - 1) * 0.25f;
            s[i] = (voice + rasp) * env * 0.85f;
        }
        return Wav(s, rate);
    }

    private static AudioStreamWav BuildZombieSnicker()   // low guttural "heh-heh-heh" chuckle
    {
        int rate = 22050, n = (int)(rate * 0.6f); var s = new float[n]; var rng = new System.Random((int)GD.Randi());
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate;
            float puff = 0.5f + 0.5f * Mathf.Sin(t * Tau * 8f);      // ~8 chuckle bursts
            float gate = puff > 0.6f ? 1f : 0f;
            float growl = Mathf.Sin(t * Tau * (95f + 10f * Mathf.Sin(t * Tau * 3f))) * 0.4f;
            float rasp = (float)(rng.NextDouble() * 2 - 1) * 0.25f;
            float env = Mathf.Min(1f, t / 0.02f) * Mathf.Min(1f, (1f - t / 0.6f) / 0.2f + 0.001f);
            s[i] = (growl + rasp) * gate * env * 0.6f;
        }
        return Wav(s, rate);
    }

    // a bell-like attune chime whose pitch/timbre keys off the element
    private static AudioStreamWav BuildElement(DamageType t)
    {
        float f0 = t switch
        {
            DamageType.Lunar => 659f,
            DamageType.Arcane => 880f,
            DamageType.Nature => 523f,
            DamageType.Frost => 988f,
            DamageType.Curse => 330f,
            DamageType.Holy => 1175f,
            DamageType.Ember => 440f,
            DamageType.Wind => 740f,
            _ => 392f,
        };
        int rate = 22050, n = (int)(rate * 0.55f);
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float tt = i / (float)rate;
            float env = Mathf.Exp(-tt * 6.5f);
            float bend = 1f + 0.02f * Mathf.Exp(-tt * 22f);          // tiny attack bend
            float v = Mathf.Sin(tt * Tau * f0 * bend) * 0.55f
                    + Mathf.Sin(tt * Tau * f0 * 2f) * 0.25f
                    + Mathf.Sin(tt * Tau * f0 * 3f) * 0.12f;
            float shimmer = Mathf.Sin(tt * Tau * f0 * 4.02f) * 0.08f * Mathf.Exp(-tt * 3f);
            s[i] = (v + shimmer) * env * 0.6f;
        }
        return Wav(s, rate);
    }

    // looping 80s drum-machine pattern, same length as the arp so they phase together
    // ritual / tribal drum bed (fades in with tension) — deep heartbeat toms + a rattling shaker, to sit under the witchy
    // arp. stepDur matches BuildArp so the two stay phase-locked when SetTempo scales them together.
    private static AudioStreamWav BuildDrums()
    {
        int rate = 22050;
        float stepDur = 0.15f;
        int steps = 16, stepN = (int)(rate * stepDur), n = stepN * steps;
        var s = new float[n];
        var rng = new System.Random(13);

        void Tom(int at, float f0, float f1, float amp)   // ritual tom / hand-drum: a pitched thump sliding down
        {
            int dur = (int)(rate * 0.24f);
            for (int j = 0; j < dur && at + j < n; j++)
            {
                float t = j / (float)rate;
                float f = Mathf.Lerp(f0, f1, Mathf.Min(1f, t * 9f));
                s[at + j] += Mathf.Sin(t * Tau * f) * Mathf.Exp(-t * 11f) * amp;
            }
        }
        void Shaker(int at, float amp)   // dry rattle/shaker — witchy hand percussion
        {
            int dur = (int)(rate * 0.06f);
            for (int j = 0; j < dur && at + j < n; j++)
            {
                float t = j / (float)rate;
                float noise = (float)(rng.NextDouble() * 2 - 1);
                // (FIX) ~1ms attack ramp. Jumping straight to full-amplitude white noise on the first sample is a hard
                // discontinuity — i.e. a CLICK — which is what made this read as static rather than as a shaker.
                s[at + j] += noise * Mathf.Min(1f, t * 900f) * Mathf.Exp(-t * 55f) * amp;
            }
        }

        for (int st = 0; st < steps; st++)
        {
            int at = st * stepN;
            if (st % 8 == 0) Tom(at, 150f, 52f, 0.95f);   // deep heartbeat on the 1
            if (st % 8 == 3) Tom(at, 120f, 60f, 0.55f);   // syncopated low answer
            if (st % 8 == 6) Tom(at, 195f, 92f, 0.65f);   // mid tom
            if (st % 2 == 1) Shaker(at, 0.26f);           // off-beat rattle
            if (st % 4 == 2) Shaker(at, 0.16f);
        }

        var w = Wav(s, rate);
        w.LoopMode = AudioStreamWav.LoopModeEnum.Forward;
        w.LoopBegin = 0;
        w.LoopEnd = n;
        return w;
    }

    // a minor synth chord with a little vibrato — root rises as the combo grows
    private static AudioStreamWav BuildChord(int combo)
    {
        int rate = 22050, n = (int)(rate * 0.6f);
        var s = new float[n];
        float[] scale = { 196f, 220f, 261.63f, 293.66f, 329.63f, 392f };
        float root = scale[Mathf.Clamp(combo / 5, 0, scale.Length - 1)];
        float m3 = root * 1.1892f, p5 = root * 1.4983f, oct = root * 2f;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate;
            float env = Mathf.Exp(-t * 3.0f) * Mathf.Min(1f, t * 60f);
            float vib = 1f + 0.006f * Mathf.Sin(t * Tau * 5.5f);
            float v = Mathf.Sin(t * Tau * root * vib) * 0.42f
                    + Mathf.Sin(t * Tau * m3 * vib) * 0.30f
                    + Mathf.Sin(t * Tau * p5) * 0.26f
                    + Mathf.Sin(t * Tau * oct) * 0.16f
                    + Mathf.Sin(t * Tau * root * 1.005f) * 0.12f;   // detune shimmer
            float air = Mathf.Sin(t * Tau * oct * 2f) * 0.06f * Mathf.Exp(-t * 5f);
            s[i] = (v + air) * env * 0.5f;
        }
        return Wav(s, rate);
    }

    // looping WITCHY bell arpeggio in A harmonic minor — the raised 7th (G#) and flat 6th (F) give the eerie, gothic
    // colour. Music-box / celeste-like notes ring with long tails that wrap the loop, layering into a haunting pad over a
    // low drone with a slow uneasy sway. Tempo + intensity are still applied EXTERNALLY (PitchScale + volume via
    // SetTempo/SetIntensity), so it speeds up and swells with the game state exactly like the old arp did.
    private static AudioStreamWav BuildArp()
    {
        int rate = 22050;
        float A3 = 220f, B3 = 246.94f, C4 = 261.63f, E4 = 329.63f, F4 = 349.23f, Gs4 = 415.30f, A4 = 440f;
        float[] seq = {
            A3, E4, C4, E4,   A3, F4, E4, C4,
            B3, E4, Gs4, E4,  A3, C4, E4, A4
        };
        float stepDur = 0.15f;
        int stepN = (int)(rate * stepDur);
        int n = stepN * seq.Length;
        float loopSec = n / (float)rate;
        var s = new float[n];
        // low drone (A1) + its fifth + octave, whole cycles → seamless; slow tremolo = an uneasy sway (2 per loop)
        float droneF = Mathf.Round(55f * loopSec) / loopSec;
        float fifthF = Mathf.Round(82.41f * loopSec) / loopSec;
        for (int i = 0; i < n; i++)
        {
            float gt = i / (float)rate;
            float trem = 0.85f + 0.15f * Mathf.Sin(gt * Tau * (2f / loopSec));
            float drone = (Mathf.Sin(gt * Tau * droneF) * 0.5f + Mathf.Sin(gt * Tau * fifthF) * 0.16f + Mathf.Sin(gt * Tau * droneF * 2f) * 0.10f) * trem;
            s[i] += drone * 0.15f;
        }
        // bell / celeste notes with long ringing tails that wrap the loop (so it's seamless AND lush)
        int tailN = (int)(rate * 0.5f);
        for (int step = 0; step < seq.Length; step++)
        {
            float f = seq[step];
            int start = step * stepN;
            for (int j = 0; j < tailN; j++)
            {
                int idx = (start + j) % n;
                float t = j / (float)rate;
                float env = Mathf.Exp(-t * 7f) * Mathf.Min(1f, t * 250f);   // sharp pluck, ~0.4s ring
                float vib = 1f + 0.004f * Mathf.Sin(t * Tau * 5.2f);
                float bell = Mathf.Sin(t * Tau * f * vib) * 0.5f
                           + Mathf.Sin(t * Tau * f * 2f) * 0.26f
                           + Mathf.Sin(t * Tau * f * 3f) * 0.13f
                           + Mathf.Sin(t * Tau * f * 4.2f) * 0.08f;   // inharmonic partial = glassy shimmer
                s[idx] += bell * env * 0.26f;
            }
        }
        for (int i = 0; i < n; i++) s[i] = (float)System.Math.Tanh(s[i] * 1.1) * 0.85f;   // gentle limiter for the overlapping tails
        var w = Wav(s, rate);
        w.LoopMode = AudioStreamWav.LoopModeEnum.Forward;
        w.LoopBegin = 0;
        w.LoopEnd = n;
        return w;
    }

    // ===== LEVEL-UP SLOT ROLL (NEW) — the cards spin, tick past, and slam to a stop, brighter the rarer they land =====
    private AudioStreamWav _rollTick;
    private AudioStreamWav[] _rollLock;   // indexed by rarity tier 0..4
    public void RollTick()
    {
        _rollTick ??= BuildRollTick();
        PlayOne(_rollTick, -13f);   // soft — it fires many times per spin
    }
    public void RollLock(int tier)
    {
        tier = Mathf.Clamp(tier, 0, 4);
        _rollLock ??= new AudioStreamWav[5];
        _rollLock[tier] ??= BuildRollLock(tier);
        PlayOne(_rollLock[tier], Mathf.Lerp(-7f, -1f, tier / 4f));   // rarer = louder
        if (tier >= 4) { Thunder(); }                                // legendary lands with a thunderclap
        else if (tier >= 3) Clink();                                 // epic gets a bright ping on top
    }
    // a dry witchy "tok" — a muted low blip with a faint bell partial, quick tick as a card face flicks past
    private static AudioStreamWav BuildRollTick()
    {
        int rate = 22050, n = (int)(rate * 0.055f);
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate;
            float env = Mathf.Min(1f, t / 0.001f) * Mathf.Exp(-t * 70f);
            float v = Mathf.Sin(t * Tau * 180f) * 0.7f + Mathf.Sin(t * Tau * 540f) * 0.14f;   // dull body + a whisper of bell
            s[i] = v * env * 0.5f;
        }
        return Wav(s, rate);
    }
    // the card slamming to a stop: a struck bell whose brightness, ring-length and sub-thump all grow with rarity.
    // common = a flat wooden knock; legendary = a bright, long, shimmering chime. Punchy attack on every tier.
    private static AudioStreamWav BuildRollLock(int tier)
    {
        int rate = 22050;
        float dur = Mathf.Lerp(0.16f, 0.62f, tier / 4f);
        int n = (int)(rate * dur);
        var s = new float[n];
        var rng = new System.Random(1000 + tier);
        // fundamental climbs a little with rarity so higher tiers ring brighter (A minor-ish steps)
        float f0 = new[] { 196f, 220f, 261.63f, 329.63f, 392f }[tier];
        float ring = Mathf.Lerp(9f, 3.4f, tier / 4f);     // higher tier = longer sustain
        float partialMix = tier / 4f;                     // more (glassy, inharmonic) upper partials as it gets rarer
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate;
            float atk = Mathf.Min(1f, t / 0.0015f);        // hard punchy transient
            float env = atk * Mathf.Exp(-t * ring);
            float bell = Mathf.Sin(t * Tau * f0) * 0.6f
                       + Mathf.Sin(t * Tau * f0 * 2.01f) * 0.26f * partialMix
                       + Mathf.Sin(t * Tau * f0 * 3.02f) * 0.16f * partialMix
                       + Mathf.Sin(t * Tau * f0 * 4.7f) * 0.10f * partialMix;   // inharmonic = magical shimmer
            float sub = Mathf.Sin(t * Tau * f0 * 0.5f) * (0.20f + 0.22f * partialMix) * Mathf.Exp(-t * 14f);   // punch under it
            float knock = (float)(rng.NextDouble() * 2 - 1) * Mathf.Exp(-t * 130f) * 0.22f;                     // woody attack click
            s[i] = (bell + sub + knock) * env * 0.55f;
        }
        // a soft high shimmer sweep tacked on for epic+ — the "ooo, rare" tail
        if (tier >= 3)
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)rate;
                float f = Mathf.Lerp(1400f, 3200f, Mathf.Min(1f, t / dur));
                s[i] += Mathf.Sin(t * Tau * f) * 0.10f * Mathf.Sin(Mathf.Min(1f, t / dur) * Mathf.Pi) * (tier == 4 ? 1.3f : 0.8f);
            }
        for (int i = 0; i < n; i++) s[i] = (float)System.Math.Tanh(s[i] * 1.15) * 0.9f;
        return Wav(s, rate);
    }

    // (NEW) REFUSED — "you can't afford that". Deliberately NOT the spell fizzle (which is a crackly sputter that grates
    // when it repeats): a soft, dull two-note fall, like a coin refused. Short, dry, and easy on the ear.
    private AudioStreamWav _denied;
    public void Denied()
    {
        _denied ??= BuildDenied();
        PlayOne(_denied, -7f);
    }
    private static AudioStreamWav BuildDenied()
    {
        int rate = 22050, n = (int)(rate * 0.30f);
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate;
            float k = t / 0.30f;
            // two soft muted tones a tone apart, the second lower — a downward "nuh-uh"
            float f = k < 0.42f ? 262f : 196f;
            float phase = k < 0.42f ? t : (t - 0.126f);
            float gate = k < 0.42f ? Mathf.Min(1f, phase / 0.008f) * Mathf.Exp(-phase * 9f)
                                   : Mathf.Min(1f, phase / 0.008f) * Mathf.Exp(-phase * 7f);
            float v = Mathf.Sin(phase * Tau * f) * 0.7f + Mathf.Sin(phase * Tau * f * 2f) * 0.12f;   // mostly fundamental = dull, not bright
            float body = Mathf.Sin(phase * Tau * f * 0.5f) * 0.22f;                                    // a little weight underneath
            s[i] = (v + body) * gate * 0.5f;
        }
        return Wav(s, rate);
    }

    // failed-cast sputter: a bright spark that crackles then fizzles out, pitch sliding down
    private static AudioStreamWav BuildFizzle()
    {
        int rate = 22050, n = (int)(rate * 0.34f);
        var s = new float[n];
        var rng = new System.Random((int)GD.Randi());
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)n;                          // 0..1 progress
            float tt = i / (float)rate;
            float env = Mathf.Pow(1f - t, 1.7f);             // quick decay
            float f = Mathf.Lerp(1250f, 280f, t);            // descending sparkle tone
            float tone = Mathf.Sin(tt * Tau * f) * 0.35f;
            float crackle = (float)(rng.NextDouble() * 2.0 - 1.0);
            crackle *= rng.NextDouble() < 0.5 * (1f - t) ? 1f : 0.12f;   // sparks thin out over time
            s[i] = (tone + crackle * 0.7f) * env;
        }
        for (int i = 0; i < n; i++) s[i] = Mathf.Clamp(s[i] * 0.6f, -1f, 1f);
        return Wav(s, rate);
    }

    private static AudioStreamWav BuildFrostShatter()
    {
        int rate = 22050, n = (int)(rate * 0.45f); var s = new float[n]; var rng = new System.Random((int)GD.Randi());
        float prev = 0f;
        for (int i = 0; i < n; i++)
        {
            float tt = i / (float)rate;
            float env = Mathf.Min(1f, tt / 0.003f) * Mathf.Exp(-tt * 7f);
            float chime = Mathf.Sin(tt * Tau * 1760f) * 0.4f + Mathf.Sin(tt * Tau * 2640f) * 0.25f + Mathf.Sin(tt * Tau * 3520f) * 0.15f;   // crystalline partials
            float white = (float)(rng.NextDouble() * 2 - 1);
            float hp = white - prev; prev = white;
            float tinkle = (rng.NextDouble() < 0.25) ? hp * 0.6f * Mathf.Exp(-tt * 10f) : hp * 0.12f;   // sparse ice tinkle
            s[i] = (chime * Mathf.Exp(-tt * 5f) + tinkle) * env * 0.7f;
        }
        return Wav(s, rate);
    }

    private static AudioStreamWav BuildBrambleSnap()
    {
        int rate = 22050, n = (int)(rate * 0.4f); var s = new float[n]; var rng = new System.Random((int)GD.Randi());
        for (int i = 0; i < n; i++)
        {
            float tt = i / (float)rate, k = tt / 0.4f;
            float env = Mathf.Min(1f, tt / 0.004f) * Mathf.Exp(-tt * 5f);
            float creak = Mathf.Sin(tt * Tau * Mathf.Lerp(180f, 90f, k) * (1f + 0.05f * Mathf.Sin(tt * Tau * 11f))) * 0.4f;   // bending groan
            float noise = (float)(rng.NextDouble() * 2 - 1);
            float snap = (tt < 0.05f) ? noise * 0.6f * Mathf.Exp(-tt * 40f) : 0f;   // sharp woody snap up front
            float grain = noise * 0.2f * Mathf.Abs(Mathf.Sin(tt * Tau * 8f));
            s[i] = (creak * 0.6f + snap + grain * 0.4f) * env * 0.75f;
        }
        return Wav(s, rate);
    }

    private static AudioStreamWav BuildEmberBoom()
    {
        int rate = 22050, n = (int)(rate * 0.5f); var s = new float[n]; var rng = new System.Random((int)GD.Randi());
        for (int i = 0; i < n; i++)
        {
            float tt = i / (float)rate, k = tt / 0.5f;
            float env = Mathf.Min(1f, tt / 0.004f) * Mathf.Exp(-tt * 6f);
            float boom = Mathf.Sin(tt * Tau * Mathf.Lerp(120f, 45f, k)) * 0.6f;   // low thump, pitch drops
            float noise = (float)(rng.NextDouble() * 2 - 1);
            float crackle = noise * (0.3f + 0.5f * Mathf.Abs(Mathf.Sin(tt * Tau * 30f))) * Mathf.Exp(-tt * 4f);   // fire crackle
            s[i] = (boom * 0.7f + crackle * 0.4f) * env * 0.85f;
        }
        return Wav(s, rate);
    }

    private static AudioStreamWav BuildBloodSpray()
    {
        int rate = 22050, n = (int)(rate * 0.35f); var s = new float[n]; var rng = new System.Random((int)GD.Randi());
        float prev = 0f;
        for (int i = 0; i < n; i++)
        {
            float tt = i / (float)rate, k = tt / 0.35f;
            float env = Mathf.Min(1f, tt / 0.004f) * Mathf.Exp(-tt * 10f);
            float wet = Mathf.Sin(tt * Tau * Mathf.Lerp(200f, 90f, k)) * 0.4f;   // wet body, pitch drops
            float white = (float)(rng.NextDouble() * 2 - 1);
            float hp = white - prev; prev = white;
            s[i] = (wet + hp * 0.55f * Mathf.Exp(-tt * 7f)) * env * 0.75f;   // high-passed spray hiss
        }
        return Wav(s, rate);
    }

    private static AudioStreamWav BuildSpikeStab()
    {
        int rate = 22050, n = (int)(rate * 0.3f); var s = new float[n]; var rng = new System.Random((int)GD.Randi());
        float prev = 0f;
        for (int i = 0; i < n; i++)
        {
            float tt = i / (float)rate, k = tt / 0.3f;
            float env = Mathf.Min(1f, tt / 0.002f) * Mathf.Exp(-tt * 13f);   // sharp attack, fast decay
            float shnk = Mathf.Sin(tt * Tau * Mathf.Lerp(600f, 180f, k)) * 0.45f;   // fast downward pitch = stab
            float white = (float)(rng.NextDouble() * 2 - 1);
            float hp = white - prev; prev = white;
            s[i] = (shnk + hp * 0.4f * Mathf.Exp(-tt * 9f)) * env * 0.8f;
        }
        return Wav(s, rate);
    }

    private static AudioStreamWav BuildCurseWhoosh()
    {
        int rate = 22050, n = (int)(rate * 0.55f); var s = new float[n]; var rng = new System.Random((int)GD.Randi());
        for (int i = 0; i < n; i++)
        {
            float tt = i / (float)rate, k = tt / 0.55f;
            float env = Mathf.Min(1f, tt / 0.02f) * Mathf.Exp(-tt * 3.5f);
            float f = Mathf.Lerp(320f, 90f, k);   // descending, ominous
            float tone = Mathf.Sin(tt * Tau * f) * 0.4f + Mathf.Sin(tt * Tau * f * 1.5f) * 0.2f;
            float wob = 1f + 0.1f * Mathf.Sin(tt * Tau * 5f);
            float noise = (float)(rng.NextDouble() * 2 - 1) * 0.25f * Mathf.Exp(-tt * 4f);
            s[i] = (tone * wob + noise) * env * 0.7f;
        }
        return Wav(s, rate);
    }

    private static AudioStreamWav BuildCoins()
    {
        int rate = 22050, n = (int)(rate * 0.45f); var s = new float[n];
        // two bright metallic dings, the second a touch later + higher → a "ch-ching"
        float[] onset = { 0f, 0.07f };
        float[] freq = { 1760f, 2637f };
        for (int i = 0; i < n; i++)
        {
            float tt = i / (float)rate; float v = 0f;
            for (int k = 0; k < 2; k++)
            {
                float lt = tt - onset[k]; if (lt < 0f) continue;
                float env = Mathf.Min(1f, lt / 0.002f) * Mathf.Exp(-lt * 22f);
                v += (Mathf.Sin(lt * Tau * freq[k]) * 0.5f + Mathf.Sin(lt * Tau * freq[k] * 2.01f) * 0.28f + Mathf.Sin(lt * Tau * freq[k] * 3.02f) * 0.14f) * env;
            }
            s[i] = v * 0.5f;
        }
        return Wav(s, rate);
    }

    private static AudioStreamWav BuildLunarChime()
    {
        int rate = 22050, n = (int)(rate * 0.6f); var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float tt = i / (float)rate;
            float env = Mathf.Min(1f, tt / 0.005f) * Mathf.Exp(-tt * 4f);
            float bell = Mathf.Sin(tt * Tau * 880f) * 0.4f + Mathf.Sin(tt * Tau * 1320f) * 0.25f * Mathf.Exp(-tt * 6f) + Mathf.Sin(tt * Tau * 1760f) * 0.12f * Mathf.Exp(-tt * 9f);
            float hum = Mathf.Sin(tt * Tau * 220f) * 0.15f;
            s[i] = (bell + hum) * env * 0.7f;
        }
        return Wav(s, rate);
    }

    private static AudioStreamWav BuildHolyChord()
    {
        int rate = 22050, n = (int)(rate * 0.6f); var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float tt = i / (float)rate;
            float env = Mathf.Min(1f, tt / 0.01f) * Mathf.Exp(-tt * 3f);
            float chord = Mathf.Sin(tt * Tau * 523f) * 0.3f + Mathf.Sin(tt * Tau * 659f) * 0.3f + Mathf.Sin(tt * Tau * 784f) * 0.3f;   // major triad
            float shimmer = Mathf.Sin(tt * Tau * 1568f) * 0.1f * (0.5f + 0.5f * Mathf.Sin(tt * Tau * 6f));
            s[i] = (chord + shimmer) * env * 0.6f;
        }
        return Wav(s, rate);
    }

    private static AudioStreamWav BuildSmiteStrike()
    {
        int rate = 22050, n = (int)(rate * 0.4f); var s = new float[n]; var rng = new System.Random((int)GD.Randi());
        for (int i = 0; i < n; i++)
        {
            float tt = i / (float)rate, k = tt / 0.4f;
            float env = Mathf.Min(1f, tt / 0.001f) * Mathf.Exp(-tt * 9f);   // sharp crack
            float crack = (float)(rng.NextDouble() * 2 - 1) * Mathf.Exp(-tt * 30f) * 0.6f;
            float tone = Mathf.Sin(tt * Tau * Mathf.Lerp(1200f, 500f, k)) * 0.4f;
            float chime = Mathf.Sin(tt * Tau * 1046f) * 0.2f * Mathf.Exp(-tt * 6f);
            s[i] = (crack + tone + chime) * env * 0.85f;
        }
        return Wav(s, rate);
    }

    private static AudioStreamWav BuildBloodPour()
    {
        int rate = 22050, n = (int)(rate * 0.5f); var s = new float[n]; var rng = new System.Random((int)GD.Randi());
        float prev = 0f;
        for (int i = 0; i < n; i++)
        {
            float tt = i / (float)rate, k = tt / 0.5f;
            float env = Mathf.Min(1f, tt / 0.01f) * Mathf.Exp(-tt * 4f);
            float glug = Mathf.Sin(tt * Tau * Mathf.Lerp(160f, 70f, k) * (1f + 0.3f * Mathf.Sin(tt * Tau * 9f))) * 0.4f;
            float white = (float)(rng.NextDouble() * 2 - 1);
            float hp = white - prev; prev = white;
            s[i] = (glug + hp * 0.35f * Mathf.Exp(-tt * 5f)) * env * 0.7f;
        }
        return Wav(s, rate);
    }

    private static AudioStreamWav BuildWindWhoosh()
    {
        int rate = 22050, n = (int)(rate * 0.6f); var s = new float[n]; var rng = new System.Random((int)GD.Randi());
        float prev = 0f;
        for (int i = 0; i < n; i++)
        {
            float tt = i / (float)rate, k = tt / 0.6f;
            float env = Mathf.Min(1f, tt / 0.05f) * Mathf.Min(1f, (1f - k) / 0.3f + 0.001f);   // swell then taper
            float white = (float)(rng.NextDouble() * 2 - 1);
            float lp = prev + (white - prev) * 0.2f; prev = lp;   // low-passed → airy whoosh
            float howl = Mathf.Sin(tt * Tau * Mathf.Lerp(300f, 600f, k)) * 0.15f;   // rising whistle
            s[i] = (lp * 0.8f + howl) * env * 0.7f;
        }
        return Wav(s, rate);
    }

    private static AudioStreamWav BuildUltCast()   // cinematic "power unleashed": rising sweep + deep sub-boom + bright shimmer + rumble tail
    {
        int rate = 22050, n = (int)(rate * 1.0f); var s = new float[n]; var rng = new System.Random((int)GD.Randi());
        float prev = 0f;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate, k = t / 1.0f;
            float env = Mathf.Min(1f, t / 0.005f) * Mathf.Exp(-t * 2.4f);
            float sweep = Mathf.Sin(t * Tau * Mathf.Lerp(160f, 950f, Mathf.Min(1f, t * 3.2f))) * 0.32f * Mathf.Exp(-t * 4.5f);   // rising whoosh
            float sub = Mathf.Sin(t * Tau * Mathf.Lerp(72f, 44f, k)) * 0.6f;                                                    // deep sub boom
            float shimmer = (Mathf.Sin(t * Tau * 1760f) + Mathf.Sin(t * Tau * 2350f)) * 0.09f * Mathf.Exp(-t * 6.5f);          // crystalline shimmer
            float noise = (float)(rng.NextDouble() * 2 - 1); float lp = prev + (noise - prev) * 0.28f; prev = lp;              // rumble body
            s[i] = (sub * 0.7f + sweep + shimmer + lp * 0.22f) * env * 0.85f;
        }
        return Wav(s, rate);
    }

    private static AudioStreamWav BuildArcaneBlast()
    {
        int rate = 22050, n = (int)(rate * 0.55f); var s = new float[n]; var rng = new System.Random((int)GD.Randi());
        float prev = 0f;
        for (int i = 0; i < n; i++)
        {
            float tt = i / (float)rate, k = tt / 0.55f;
            float env = Mathf.Min(1f, tt / 0.002f) * Mathf.Exp(-tt * 5.5f);
            float boom = Mathf.Sin(tt * Tau * Mathf.Lerp(90f, 38f, k)) * 0.6f;                        // low thunder body, pitch drops
            float zap = Mathf.Sin(tt * Tau * Mathf.Lerp(1400f, 300f, k)) * 0.25f * Mathf.Exp(-tt * 10f);   // electric arcane zap
            float noise = (float)(rng.NextDouble() * 2 - 1);
            float lp = prev + (noise - prev) * 0.35f; prev = lp;                                       // rumble
            s[i] = (boom * 0.7f + zap + lp * 0.35f) * env * 0.9f;
        }
        return Wav(s, rate);
    }

    private static AudioStreamWav BuildLanceFall()
    {
        int rate = 22050, n = (int)(rate * 0.45f); var s = new float[n]; var rng = new System.Random((int)GD.Randi());
        for (int i = 0; i < n; i++)
        {
            float tt = i / (float)rate, k = tt / 0.45f;
            float env = Mathf.Min(1f, tt / 0.001f) * Mathf.Exp(-tt * 7f);                              // sharp attack
            float whistle = Mathf.Sin(tt * Tau * Mathf.Lerp(1700f, 700f, k)) * 0.35f;                  // descending shriek (lances plunging)
            float crack = (float)(rng.NextDouble() * 2 - 1) * Mathf.Exp(-tt * 26f) * 0.4f;             // impact crack
            float chime = (Mathf.Sin(tt * Tau * 1046f) + Mathf.Sin(tt * Tau * 1568f)) * 0.15f * Mathf.Exp(-tt * 4f);   // holy shimmer
            s[i] = (whistle + crack + chime) * env * 0.85f;
        }
        return Wav(s, rate);
    }

    private static AudioStreamWav BuildWindSlash()
    {
        int rate = 22050, n = (int)(rate * 0.32f); var s = new float[n]; var rng = new System.Random((int)GD.Randi());
        float prev = 0f;
        for (int i = 0; i < n; i++)
        {
            float tt = i / (float)rate, k = tt / 0.32f;
            float env = Mathf.Min(1f, tt / 0.004f) * Mathf.Exp(-tt * 9f);              // fast, sharp
            float white = (float)(rng.NextDouble() * 2 - 1);
            float bp = white - prev; prev = white;                                     // crude high-pass → airy hiss
            float whistle = Mathf.Sin(tt * Tau * Mathf.Lerp(2600f, 900f, k)) * 0.25f;  // descending slash whistle
            s[i] = (bp * 0.5f + whistle) * env * 0.8f;
        }
        return Wav(s, rate);
    }

    private static AudioStreamWav BuildRootRush()
    {
        int rate = 22050, n = (int)(rate * 0.45f); var s = new float[n]; var rng = new System.Random((int)GD.Randi());
        float prev = 0f;
        for (int i = 0; i < n; i++)
        {
            float tt = i / (float)rate, k = tt / 0.45f;
            float env = Mathf.Min(1f, tt / 0.006f) * Mathf.Exp(-tt * 6f);
            float noise = (float)(rng.NextDouble() * 2 - 1);
            float lp = prev + (noise - prev) * 0.45f; prev = lp;                        // filtered rustle
            float creak = Mathf.Sin(tt * Tau * Mathf.Lerp(70f, 240f, k)) * 0.35f;       // rising woody creak (roots surging up)
            float whip = noise * Mathf.Exp(-tt * 18f) * 0.4f;                           // initial whip
            s[i] = (lp * 0.5f + creak + whip) * env * 0.85f;
        }
        return Wav(s, rate);
    }

    private static AudioStreamWav BuildHolyRush()
    {
        int rate = 22050, n = (int)(rate * 0.9f); var s = new float[n]; var rng = new System.Random((int)GD.Randi());
        float prev = 0f;
        for (int i = 0; i < n; i++)
        {
            float tt = i / (float)rate, k = tt / 0.9f;
            float env = Mathf.Min(1f, tt / 0.15f) * Mathf.Min(1f, (1f - k) / 0.3f + 0.001f);   // slow swell, gentle taper
            float white = (float)(rng.NextDouble() * 2 - 1);
            float lp = prev + (white - prev) * 0.08f; prev = lp;                                // very airy, soft rush
            float pad = (Mathf.Sin(tt * Tau * 392f) + Mathf.Sin(tt * Tau * 523f)) * 0.12f;      // soft holy chord undertone
            float shimmer = Mathf.Sin(tt * Tau * 1568f) * 0.05f * (0.5f + 0.5f * Mathf.Sin(tt * Tau * 3f));
            s[i] = (lp * 0.5f + pad + shimmer) * env * 0.5f;
        }
        return Wav(s, rate);
    }

    private static AudioStreamWav BuildWitchCackle()
    {
        int rate = 22050, n = (int)(rate * 0.85f); var s = new float[n]; var rng = new System.Random((int)GD.Randi());
        for (int i = 0; i < n; i++)
        {
            float tt = i / (float)rate, k = tt / 0.85f;
            float syl = Mathf.Pow(Mathf.Max(0f, Mathf.Sin(tt * Tau * 7f)), 3f);                 // ha-ha-ha amplitude gate (~7/s)
            float env = Mathf.Min(1f, tt / 0.02f) * Mathf.Min(1f, (1f - k) / 0.2f + 0.001f);
            float pitch = Mathf.Lerp(360f, 620f, k) * (1f + 0.06f * Mathf.Sin(tt * Tau * 12f));  // rising, warbling
            float voice = Mathf.Sin(tt * Tau * pitch) * 0.5f + Mathf.Sin(tt * Tau * pitch * 2f) * 0.2f;
            float rasp = (float)(rng.NextDouble() * 2 - 1) * 0.12f;                              // witchy rasp
            s[i] = (voice + rasp) * syl * env * 0.7f;
        }
        return Wav(s, rate);
    }

    private static AudioStreamWav BuildGasRelease()
    {
        int rate = 22050, n = (int)(rate * 0.8f); var s = new float[n]; var rng = new System.Random((int)GD.Randi());
        float prev = 0f;
        for (int i = 0; i < n; i++)
        {
            float tt = i / (float)rate, k = tt / 0.8f;
            float env = Mathf.Min(1f, tt / 0.02f) * Mathf.Exp(-tt * 1.6f);                       // psss then decay
            float white = (float)(rng.NextDouble() * 2 - 1);
            float hp = white - prev * 0.85f; prev = white;                                        // high-passed → hiss
            float bubble = Mathf.Sin(tt * Tau * (60f + 30f * Mathf.Sin(tt * Tau * 5f))) * 0.1f * (0.5f + 0.5f * Mathf.Sin(tt * Tau * 9f));   // low gurgle
            s[i] = (hp * 0.6f + bubble) * env * 0.6f;
        }
        return Wav(s, rate);
    }

    private static AudioStreamWav BuildFireworkLaunch()
    {
        int rate = 22050, n = (int)(rate * 0.9f); var s = new float[n]; var rng = new System.Random((int)GD.Randi());
        float prev = 0f;
        for (int i = 0; i < n; i++)
        {
            float tt = i / (float)rate, k = tt / 0.9f;
            float env = Mathf.Min(1f, tt / 0.02f) * Mathf.Min(1f, (1f - k) / 0.2f + 0.001f);
            float whistle = Mathf.Sin(tt * Tau * Mathf.Lerp(500f, 1500f, k)) * 0.3f;   // rising whistle
            float white = (float)(rng.NextDouble() * 2 - 1);
            float hp = white - prev * 0.8f; prev = white;                              // airy hiss
            s[i] = (whistle + hp * 0.3f) * env * 0.6f;
        }
        return Wav(s, rate);
    }

    private static AudioStreamWav BuildFireworkBurst()
    {
        int rate = 22050, n = (int)(rate * 0.9f); var s = new float[n]; var rng = new System.Random((int)GD.Randi());
        for (int i = 0; i < n; i++)
        {
            float tt = i / (float)rate, k = tt / 0.9f;
            float boom = Mathf.Sin(tt * Tau * Mathf.Lerp(160f, 60f, Mathf.Min(1f, k * 4f))) * 0.5f * Mathf.Exp(-tt * 7f);   // initial boom
            float c = (float)(rng.NextDouble() * 2 - 1);
            float crackle = c * Mathf.Exp(-Mathf.Abs(Mathf.Sin(tt * Tau * 7f)) * 3f) * 0.4f * Mathf.Min(1f, (1f - k) / 0.3f + 0.001f);   // scattered pops
            s[i] = (boom + crackle) * 0.7f;
        }
        return Wav(s, rate);
    }

    private static AudioStreamWav BuildZombieGroan()
    {
        int rate = 22050; var rng = new System.Random((int)GD.Randi());
        float dur = 0.5f + (float)rng.NextDouble() * 0.4f;
        int n = (int)(rate * dur); var s = new float[n];
        float f0 = 70f + (float)rng.NextDouble() * 50f;      // random low pitch → pool variety
        float bend = 0.7f + (float)rng.NextDouble() * 0.6f;
        float prev = 0f;
        for (int i = 0; i < n; i++)
        {
            float tt = i / (float)rate, k = tt / dur;
            float env = Mathf.Min(1f, tt / 0.05f) * Mathf.Min(1f, (1f - k) / 0.25f + 0.001f);
            float pitch = f0 * Mathf.Lerp(1f, bend, k);
            float voice = Mathf.Sin(tt * Tau * pitch) * 0.5f + Mathf.Sin(tt * Tau * pitch * 2f) * 0.25f + Mathf.Sin(tt * Tau * pitch * 3f) * 0.12f;
            float noise = (float)(rng.NextDouble() * 2 - 1);
            float lp = prev + (noise - prev) * 0.25f; prev = lp;
            float rasp = Mathf.Sign(voice) * Mathf.Pow(Mathf.Abs(voice), 0.7f);   // raspy distortion
            s[i] = (rasp * 0.6f + lp * 0.3f) * env * 0.7f;
        }
        return Wav(s, rate);
    }

    private static AudioStreamWav BuildZombieDeath()
    {
        int rate = 22050; var rng = new System.Random((int)GD.Randi());
        float dur = 0.7f; int n = (int)(rate * dur); var s = new float[n];
        float prev = 0f;
        for (int i = 0; i < n; i++)
        {
            float tt = i / (float)rate, k = tt / dur;
            float env = Mathf.Min(1f, tt / 0.02f) * Mathf.Exp(-tt * 2.5f);
            float pitch = Mathf.Lerp(140f, 55f, k);   // descending "ughh"
            float voice = Mathf.Sin(tt * Tau * pitch) * 0.5f + Mathf.Sin(tt * Tau * pitch * 2f) * 0.2f;
            float noise = (float)(rng.NextDouble() * 2 - 1);
            float lp = prev + (noise - prev) * 0.3f; prev = lp;
            float rasp = Mathf.Sign(voice) * Mathf.Pow(Mathf.Abs(voice), 0.6f);
            s[i] = (rasp * 0.6f + lp * 0.35f) * env * 0.8f;
        }
        return Wav(s, rate);
    }

    private static AudioStreamWav Wav(float[] s, int rate)
    {
        var w = new AudioStreamWav { Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = rate, Stereo = false };
        var b = new byte[s.Length * 2];
        for (int i = 0; i < s.Length; i++)
        {
            short v = (short)(Mathf.Clamp(s[i], -1f, 1f) * 32000);
            b[i * 2] = (byte)(v & 0xFF);
            b[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        w.Data = b;
        return w;
    }

    // bright metallic treasure shine/clink
    private static AudioStreamWav BuildCrit()   // sharp bright two-tone crit "clink" (Overwatch-style)
    {
        int rate = 22050, n = (int)(rate * 0.18f);
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate;
            float env = Mathf.Exp(-t * 26f);
            float v = Mathf.Sin(t * Tau * 3140f) * 0.55f + Mathf.Sin(t * Tau * 4700f) * 0.4f + Mathf.Sin(t * Tau * 6280f) * 0.22f;
            float env2 = t > 0.03f ? Mathf.Exp(-(t - 0.03f) * 24f) : 0f;
            v += Mathf.Sin(t * Tau * 5240f) * 0.45f * env2;   // quick second ping
            s[i] = v * env * 0.6f;
        }
        return Wav(s, rate);
    }

    private static AudioStreamWav BuildDamageTick()   // (NEW) soft universal hit-confirm — a short punchy un-pitched "tick"
    {
        int rate = 22050, n = (int)(rate * 0.05f);
        var s = new float[n];
        var rng = new System.Random(31);
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate;
            float env = Mathf.Exp(-t * 95f);                                          // very fast decay → a click, not a tone
            float noise = (float)(rng.NextDouble() * 2 - 1);
            float body = Mathf.Sin(t * Tau * 300f) * 0.6f + Mathf.Sin(t * Tau * 520f) * 0.28f;   // little low thump under the click
            s[i] = (body * 0.7f + noise * 0.45f) * env * 0.5f;
        }
        return Wav(s, rate);
    }

    private static AudioStreamWav BuildCritPlink()   // (NEW) satisfying bright bell "plink" for crits — clean high ping + sparkle tail
    {
        int rate = 22050, n = (int)(rate * 0.22f);
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate;
            float env = Mathf.Exp(-t * 19f);
            float v = Mathf.Sin(t * Tau * 1560f) * 0.6f + Mathf.Sin(t * Tau * 2340f) * 0.34f + Mathf.Sin(t * Tau * 3120f) * 0.2f;   // fundamental + fifth + octave
            float spark = t > 0.018f ? Mathf.Exp(-(t - 0.018f) * 32f) : 0f;
            v += Mathf.Sin(t * Tau * 4680f) * 0.3f * spark;   // bright leading sparkle
            s[i] = v * env * 0.55f;
        }
        return Wav(s, rate);
    }

    private static AudioStreamWav BuildClink()
    {
        int rate = 22050, n = (int)(rate * 0.26f);
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate;
            float env = Mathf.Exp(-t * 16f);
            float v = Mathf.Sin(t * Tau * 1880f) * 0.5f + Mathf.Sin(t * Tau * 2640f) * 0.35f + Mathf.Sin(t * Tau * 3960f) * 0.2f;
            float env2 = t > 0.07f ? Mathf.Exp(-(t - 0.07f) * 14f) : 0f;
            v += Mathf.Sin(t * Tau * 2280f) * 0.4f * env2;
            s[i] = v * env * 0.6f;
        }
        return Wav(s, rate);
    }

    // thunderous electric buzz
    // (HAUNT) the eerie layer that mixes over the witchy arp inside a storm. SAME 2.4s loop length as BuildArp so the two
    // stay phase-locked when SetTempo pitches them together. A low dread drone + a sparse, breathy, dissonant motif (with a
    // tritone for unease) on detuned voices with long ghostly tails that wrap the loop.
    private static AudioStreamWav BuildHauntMusic()
    {
        int rate = 22050;
        float stepDur = 0.15f; int steps = 16, stepN = (int)(rate * stepDur), n = stepN * steps;
        float loopSec = n / (float)rate;
        var s = new float[n];
        var rng = new System.Random(29);

        // low dread drone (A1) + a tense minor-second beating above it — whole cycles so it wraps seamlessly
        float d0 = Mathf.Round(55f * loopSec) / loopSec;
        float d1 = Mathf.Round(58.27f * loopSec) / loopSec;   // ~Bb1 → slow dissonant beat
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate;
            float breath = 0.7f + 0.3f * Mathf.Sin(t * Tau * (1f / loopSec));   // one slow swell per loop
            float drone = (Mathf.Sin(t * Tau * d0) * 0.5f + Mathf.Sin(t * Tau * d1) * 0.28f + Mathf.Sin(t * Tau * d0 * 0.5f) * 0.3f) * breath;
            s[i] += drone * 0.16f;
        }

        // sparse haunting motif — A minor with the raised 7th (G#) + a tritone (D#) for the "wrong" feel
        float A3 = 220f, C4 = 261.63f, Ds4 = 311.13f, E4 = 329.63f, Gs4 = 415.30f, A4 = 440f;
        var notes = new (int step, float f, float amp)[] {
            (0, A3, 0.9f), (3, E4, 0.6f), (5, Ds4, 0.7f), (8, C4, 0.8f), (10, Gs4, 0.55f), (12, A4, 0.5f), (14, E4, 0.45f),
        };
        int tailN = (int)(rate * 1.1f);   // long ghostly ring
        foreach (var (step, f, amp) in notes)
        {
            int start = step * stepN;
            for (int j = 0; j < tailN; j++)
            {
                int idx = (start + j) % n;
                float t = j / (float)rate;
                float env = Mathf.Exp(-t * 3.2f) * Mathf.Min(1f, t * 40f);        // soft breathy onset, long decay
                float vib = 1f + 0.006f * Mathf.Sin(t * Tau * 4.5f);              // wavering, unsettled pitch
                float breathNoise = 1f + 0.10f * (float)(rng.NextDouble() * 2 - 1);   // airy, voice-like texture
                float voice = Mathf.Sin(t * Tau * f * vib) * 0.5f
                            + Mathf.Sin(t * Tau * f * 1.003f * vib) * 0.34f          // detuned twin = chorus/haunt
                            + Mathf.Sin(t * Tau * f * 2f) * 0.14f
                            + Mathf.Sin(t * Tau * f * 2.98f) * 0.08f;               // slightly inharmonic = glassy dread
                s[idx] += voice * env * breathNoise * amp * 0.2f;
            }
        }
        // a faint high shimmer drifting through — like a distant wail
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate;
            float wail = Mathf.Sin(t * Tau * (784f + 12f * Mathf.Sin(t * Tau * 0.5f))) * 0.03f * (0.5f + 0.5f * Mathf.Sin(t * Tau * (1f / loopSec) - 1f));
            s[i] += wail;
        }
        for (int i = 0; i < n; i++) s[i] = (float)System.Math.Tanh(s[i] * 1.2) * 0.8f;
        var w = Wav(s, rate); w.LoopMode = AudioStreamWav.LoopModeEnum.Forward; w.LoopBegin = 0; w.LoopEnd = n; return w;
    }

    private static AudioStreamWav BuildThunder()
    {
        int rate = 22050, n = (int)(rate * 0.62f);
        var s = new float[n];
        var rng = new System.Random(7);
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate;
            float env = Mathf.Min(1f, t * 50f) * Mathf.Exp(-t * 4.5f);
            float noise = (float)(rng.NextDouble() * 2 - 1);
            float rumble = Mathf.Sin(t * Tau * 68f) * 0.6f + Mathf.Sin(t * Tau * 44f) * 0.4f;
            float buzz = Mathf.Sin(t * Tau * 140f) * (0.4f + 0.6f * noise);
            float crackle = noise * (0.4f + 0.6f * Mathf.Sin(t * Tau * 26f));
            s[i] = (rumble * 0.45f + buzz * 0.3f + crackle * 0.35f) * env * 0.85f;
        }
        return Wav(s, rate);
    }
}
