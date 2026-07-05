using Godot;
using System.Collections.Generic;

// Game.cs — the brain (root Node3D "Game"). Owns the GameState machine (below), the WAVE SPAWNER
// (the add(type, formula) block builds each wave's roster, then queues bosses/roulette/goblin/ritual
// events), enemy bookkeeping (Enemies list, SpawnEnemy/SpawnEnemyAt), witch & ult selection
// (ConfigureWitch, ChooseWitch, ChooseUlt, UltChoiceSet), the run economy (XP/levels, BossTokens),
// all input dispatch (branches on State), and shared VFX/util helpers (VfxRing, Emissive,
// ToonEmissive, SurfaceHeight).
//
// Game.I is the singleton; Game.IsAuthority / WardenCount gate host-only logic. Difficulty tuning
// (spawn formulas, elite/affix chances, co-op multiplier) lives in the wave block — see DEV_GUIDE.md
// §7.3. The HUD reads this object every frame; menu states pause the world (WorldRunning).
public enum GameState { Lobby, CharSelect, Playing, LevelUp, Swap, Stats, Element, Ult, UltMenu, Roulette, Mystic, Scroll, BindKey, Pause, Over }

// Shared witchy palette (mirrors the web build).
public static class Palette
{
    public static readonly Color Verdant = new Color(0.37f, 0.89f, 0.60f);
    public static readonly Color Lunar   = new Color(0.725f, 0.553f, 1.0f);
    public static readonly Color Ember   = new Color(1.0f, 0.808f, 0.42f);
    public static readonly Color Blood   = new Color(1.0f, 0.365f, 0.45f);
    public static readonly Color Moon    = new Color(0.91f, 0.894f, 1.0f);
    public static readonly Color Wind    = new Color(0.70f, 0.96f, 0.88f);   // Gale witch (NEW)
}

public struct Blocker { public Vector3 Pos; public float Radius; }
// Walkable flat surface (raised platform top, or ground patch).
public struct Deck { public Vector3 Center; public Vector2 Half; public float TopY; }
// Sloped walkway connecting two heights along one axis.
public struct Ramp { public Vector3 Center; public Vector2 Half; public float YLow; public float YHigh; public bool AlongX; }

public partial class Game : Node3D
{
    public static Game I;

    public const float Arena = 58f;
    public GameState State = GameState.Playing;
    public int Score = 0;
    public int Wave = 0;

    public Player Player;   // the local player (canonical reference used throughout)
    public bool ConsoleOpen = false;   // (NEW) dev console open → suspends local control so typing can't drive the game
    public readonly System.Collections.Generic.List<Player> Players = new();
    public Player LocalPlayer => Player;

    // ---- multiplayer-shaped helpers (currently operate over the single local player) ----
    public Player NearestPlayer(Vector3 pos)
    {
        Player best = null; float bd = float.MaxValue;
        foreach (var p in Players)
        {
            if (p == null || !GodotObject.IsInstanceValid(p) || p.Hp <= 0f) continue;
            float d = (p.GlobalPosition - pos).LengthSquared();
            if (d < bd) { bd = d; best = p; }
        }
        return best ?? Player;
    }
    public bool AnyPlayerInRange(Vector3 pos, float r)
    {
        float r2 = r * r;
        foreach (var p in Players)
            if (p != null && GodotObject.IsInstanceValid(p) && p.Hp > 0f && (p.GlobalPosition - pos).LengthSquared() <= r2) return true;
        return false;
    }
    public void ForEachPlayer(System.Action<Player> act)
    {
        foreach (var p in Players)
            if (p != null && GodotObject.IsInstanceValid(p)) act(p);
    }
    public int AlivePlayerCount()
    {
        int n = 0;
        foreach (var p in Players) if (p != null && GodotObject.IsInstanceValid(p) && p.Hp > 0f) n++;
        return n;
    }
    public Hud Hud;
    public FaithShield Shield;   // active Faith Shield dome (Divine ult), if any
    public readonly List<Enemy> Enemies = new();
    public readonly List<Blocker> Blockers = new();
    public readonly List<Pumpkin> Smashables = new();   // breakable props (pumpkins) the player can smash (NEW)
    public readonly List<Flower> Flowers = new();        // reactive blooms that glow when activity is near (NEW)
    public readonly List<Deck> Decks = new();
    public readonly List<Ramp> Ramps = new();
    public bool InExpedition = false;        // when true, the streamed open world is replaced by an authored Expedition leg
    public ExpoLayout Expo;
    private ExpoRun _expoRun;
    private Node3D _expoRoot;
    public bool InMaze = false;              // (NEW) the hedge-maze interlude (reuses InExpedition's world-swap)
    private MazeData _maze;
    private Node3D _mazeRoot;
    private Vector3 _preMazePos;
    private bool _mazeKeyWas = false;
    private int[,] _mazeDist;                 // cached corridor distance-to-portal field (fairy wisps + spawns)
    public readonly List<MazeWisp> MazeWisps = new();   // active breadcrumbs (drawn on the minimap)
    private Node3D _mazePortalNode;          // the exit portal (spawned on find-each-other, or immediately solo)
    private bool _mazeFound = false;         // MP: have the players met yet?
    private int _mazeStatueTarget = -1;      // solo: index of the elemental-statue chamber to reach before the portal opens
    private int[,] _mazeChaseDist;           // multi-source dist from all players → enemy corridor nav
    private float _mazeChaseT = 0f, _mazeSpawnT = 0f, _mazeElapsed = 0f;   // maze director timers
    private float _mazeGrace = 0f;           // (NEW) post-spawn window where zombies can't aggro (kills instant-aggro on entry)
    private float _specialSpawnT = 0f;       // (NEW) director cooldown for special enemies (Takers, future specials)
    public bool MazeGraceActive => _mazeGrace > 0f;
    public bool MazeAggroPhase => _mazeFound;   // (NEW) phase 2 (portal open) → zombies stay aggro'd, no de-aggro
    public Enemy EnemyByNetId(int id) { foreach (var e in Enemies) if (e != null && GodotObject.IsInstanceValid(e) && e.NetId == id) return e; return null; }   // (NEW) Taker grab lookup
    public class Blip { public Vector3 Pos; public Color Col; public float T; }   // firework triangulation ping
    public readonly List<Blip> Blips = new();
    public void AddMinimapBlip(Vector3 pos, Color col, bool net = true)
    {
        Blips.Add(new Blip { Pos = pos, Col = col, T = 6f });
        if (net) NetMgr?.BroadcastBlip(pos, col);
    }
    // player ping (middle-click): a beam + nameplate at the spot, a radar blip for everyone, synced to allies
    public void SpawnPing(Vector3 pos, string name, Color col, bool net = true)
    {
        var pm = new PingMarker(); AddChild(pm); pm.Init(pos, name, col);
        AddMinimapBlip(pos, col, net: false);           // blip our own radar like the firework does
        if (net) NetMgr?.BroadcastPing(pos, name, col); // …and everyone else's + their marker
    }
    public void DropFireworkWisp(Vector3 pos, Color col, bool net = true)   // phase-2 only: a guide wisp that pathfinds to the portal
    {
        if (_maze == null || !_mazeFound) return;   // only once the portal is open (the escape)
        var w = new FireworkWisp(); AddChild(w); w.Init(pos, col);
        if (net) NetMgr?.BroadcastVfx(39, pos, Vector3.Zero, 0f, 0f, col);
    }
    private readonly RandomNumberGenerator _mazeRng = new();
    public MazeData MazeInfo => _maze;       // later phases (fairy/wisps/heat) read the grid off this
    public Vector3 ExpoNavTarget(Vector3 from, Vector3 to, float lateral01) => Expo != null ? Expedition.NavTarget(Expo, from, to, lateral01) : to;
    private bool _expoKeyWas = false;

    // smash any breakable props (pumpkins) within range of a player attack. Prunes freed entries as it goes. (NEW)
    public void SmashNear(Vector3 center, float radius)
    {
        for (int i = Smashables.Count - 1; i >= 0; i--)
        {
            var pk = Smashables[i];
            if (pk == null || !GodotObject.IsInstanceValid(pk)) { Smashables.RemoveAt(i); continue; }
            var d = pk.GlobalPosition - center; d.Y = 0f;
            if (d.LengthSquared() <= (radius + 0.9f) * (radius + 0.9f)) pk.Smash();   // Smash() removes itself from the list
        }
    }

    // (NEW) A networked pumpkin smash arriving from another player. The world seed is shared, so props sit at the
    // same positions on every machine — match the nearest one and break it locally, with no loot (that went to the
    // smasher) and no re-broadcast. This is what makes pumpkins a shared object you can destroy for each other.
    public void SmashPumpkinAt(Vector3 pos)
    {
        Pumpkin best = null; float bd = 4f;
        for (int i = Smashables.Count - 1; i >= 0; i--)
        {
            var pk = Smashables[i];
            if (pk == null || !GodotObject.IsInstanceValid(pk)) { Smashables.RemoveAt(i); continue; }
            float d = (pk.GlobalPosition - pos).Length();
            if (d < bd) { bd = d; best = pk; }
        }
        best?.Smash(false, false);
    }

    // (NEW) Deal area DAMAGE to breakable world objects (pumpkins today; any future breakable prop). This is the
    // shared entry point so props break from ALL damage — bolts, melee, fields, AoE, DoT, the holy charge — not
    // just from a projectile passing through. Objects carry hidden HP and break when it's depleted.
    public void DamageWorld(Vector3 center, float radius, float dmg)
    {
        if (dmg <= 0f) return;
        float rr = (radius + 0.6f) * (radius + 0.6f);
        for (int i = Smashables.Count - 1; i >= 0; i--)
        {
            var pk = Smashables[i];
            if (pk == null || !GodotObject.IsInstanceValid(pk)) { Smashables.RemoveAt(i); continue; }
            var d = pk.GlobalPosition - center; d.Y = 0f;
            if (d.LengthSquared() <= rr) pk.TakeDamage(dmg);   // TakeDamage() removes itself when its HP hits 0
        }
    }

    // light up any flowers near some activity (footsteps, a jump, a spell). Prunes freed entries. (NEW)
    public void GlowFlowersNear(Vector3 center, float radius)
    {
        float r2 = radius * radius;
        for (int i = Flowers.Count - 1; i >= 0; i--)
        {
            var fl = Flowers[i];
            if (fl == null || !GodotObject.IsInstanceValid(fl)) { Flowers.RemoveAt(i); continue; }
            var d = fl.GlobalPosition - center; d.Y = 0f;
            if (d.LengthSquared() <= r2) fl.Pulse();
        }
    }

    // ---- water (NEW) ------------------------------------------------------
    // standing in pooled water: ground beneath is below the table and the feet are near the surface
    public bool InWater(Vector3 pos, float feetY)
    {
        if (_world == null || InExpedition) return false;
        return _world.Height(pos.X, pos.Z) < World.WaterLevel && feetY <= World.WaterLevel + 0.25f;
    }

    // is this point actually AT the water surface (over water AND low enough to touch it)? spells/projectiles use this to splash
    public void WaterTouch(Vector3 pos, float strength)
    {
        if (_world == null || InExpedition) return;
        if (_world.Height(pos.X, pos.Z) < World.WaterLevel && pos.Y < World.WaterLevel + 1.6f) WaterDisturb(pos, strength);   // height gate: don't ripple when it's flying high over the water
    }

    // splash every bit of water within an AoE's footprint, even if the centre is on dry land. One splash sound;
    // the rest are silent ripples so a big blast doesn't fire eight splash sounds at once. (NEW)
    public void WaterTouchArea(Vector3 center, float radius, float strength)
    {
        if (_world == null || InExpedition || radius <= 0f) return;
        if (center.Y > World.WaterLevel + 2f) return;   // blast is high above the water — no splash (height gate)
        bool sounded = false;
        if (_world.Height(center.X, center.Z) < World.WaterLevel) { WaterDisturb(center, strength, true); sounded = true; }
        int n = 8;
        for (int i = 0; i < n; i++)
        {
            float a = i / (float)n * Mathf.Tau;
            var p = center + new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * radius * 0.7f;
            if (_world.Height(p.X, p.Z) < World.WaterLevel)
            {
                bool playSound = !sounded;
                WaterDisturb(p, playSound ? strength : Mathf.Min(strength, 0.35f), playSound);
                sounded = true;
            }
        }
    }

    // occasional small ripple while something wades through (no sound — a movement trail)
    public void MaybeWaterTrail(Vector3 pos, float feetY, float dt)
    {
        if (!InWater(pos, feetY)) return;
        if (GD.Randf() < dt * 3f) WaterDisturb(pos, 0f);
    }

    // expanding ring on the water surface (+ splash sound/droplets for stronger disturbances)
    public void WaterDisturb(Vector3 pos, float strength, bool sound = true)
    {
        if (_world == null) return;
        float y = World.WaterLevel + 0.03f;
        var ring = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = 0.15f, OuterRadius = 0.32f } };
        var m = Emissive(new Color(0.55f, 0.82f, 0.95f), 0.5f);
        m.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        var mc = m.AlbedoColor; mc.A = 0.5f; m.AlbedoColor = mc;
        ring.MaterialOverride = m;
        // TorusMesh is already flat on the surface in this build — rotating it stood it upright (NEW)
        ring.Position = new Vector3(pos.X, y, pos.Z);
        _world.AddChild(ring);

        float maxR = 1.2f + strength * 2.5f, dur = 0.5f + strength * 0.3f;
        var tw = ring.CreateTween(); tw.SetParallel(true);
        tw.TweenProperty(ring, "scale", new Vector3(maxR, maxR, maxR), dur).SetEase(Tween.EaseType.Out);
        tw.TweenProperty(ring, "transparency", 1f, dur);
        var ft = ring.CreateTween(); ft.TweenInterval(dur + 0.05f);
        ft.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(ring)) ring.QueueFree(); }));

        if (strength >= 0.5f)
        {
            if (sound) Sfx?.SplashAt(new Vector3(pos.X, y, pos.Z));
            var dropMat = Emissive(new Color(0.6f, 0.85f, 0.97f), 0.6f);
            dropMat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
            for (int i = 0; i < 5; i++)
            {
                float a = i / 5f * Mathf.Tau + GD.Randf();
                var drop = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.08f, Height = 0.16f }, MaterialOverride = dropMat };
                _world.AddChild(drop);
                drop.Position = new Vector3(pos.X, y, pos.Z);
                var outv = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * (0.4f + strength * 0.5f);
                var dt2 = drop.CreateTween();
                dt2.TweenProperty(drop, "position", drop.Position + outv + new Vector3(0, 0.6f + strength * 0.5f, 0), 0.18f).SetEase(Tween.EaseType.Out);
                dt2.TweenProperty(drop, "position", drop.Position + outv * 1.7f, 0.28f).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
                var df = drop.CreateTween(); df.TweenInterval(0.5f);
                df.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(drop)) drop.QueueFree(); }));
            }
        }
    }

    // Highest walkable surface under an XZ position that's within stepping range of the feet.
    // Surfaces too far above the feet are ignored (you walk under raised platforms; ramps carry you up).
    public float SurfaceHeight(Vector3 pos, float feetY)
    {
        const float step = 1.6f;
        float best = (_world != null && !InExpedition) ? _world.Height(pos.X, pos.Z) : 0f;   // rolling terrain is the base surface; Expedition stays flat/authored (NEW)
        foreach (var d in Decks)
        {
            if (Mathf.Abs(pos.X - d.Center.X) <= d.Half.X && Mathf.Abs(pos.Z - d.Center.Z) <= d.Half.Y)
                if (d.TopY <= feetY + step && d.TopY > best) best = d.TopY;
        }
        foreach (var r in Ramps)
        {
            if (Mathf.Abs(pos.X - r.Center.X) <= r.Half.X && Mathf.Abs(pos.Z - r.Center.Z) <= r.Half.Y)
            {
                float t = r.AlongX ? (pos.X - (r.Center.X - r.Half.X)) / (2f * r.Half.X)
                                   : (pos.Z - (r.Center.Z - r.Half.Y)) / (2f * r.Half.Y);
                float y = Mathf.Lerp(r.YLow, r.YHigh, Mathf.Clamp(t, 0f, 1f));
                if (y <= feetY + step && y > best) best = y;
            }
        }
        return best;
    }

    private readonly Queue<string> _toSpawn = new();
    private float _spawnT = 0f;
    private float _waveGap = 30f;
    public const float WaveGapMax = 30f;
    private float _skipHold = 0f;
    private readonly System.Collections.Generic.HashSet<long> _skipVotes = new();   // (NEW) peers who've voted to skip the current rest/ritual (host tallies)
    private bool _prevSkippable = false;   // (NEW) rising-edge detector — clears votes when a fresh skippable window opens
    private bool _localVoted = false;      // (NEW) this machine already cast its vote for the current window
    private int _netSkipVotes = 0;         // (NEW) host-synced vote tally, shown on clients
    private float _waveSyncT = 0f;         // (NEW) throttle for host→client wave-state sync
    public int SkipVotes => IsAuthority ? _skipVotes.Count : _netSkipVotes;   // (NEW) HUD (clients read the synced tally)
    public int SkipNeeded => Mathf.Max(1, WardenCount);       // (NEW) all players must vote
    public void RegisterSkipVote(long peer) { if (IsAuthority) _skipVotes.Add(peer); }   // (NEW) host records a vote (own = peer 1; clients pass their sender id via RPC)
    public void ApplyWaveState(int wave, float gap, int votes) { if (IsAuthority) return; Wave = wave; _waveGap = gap; _netSkipVotes = votes; }   // (NEW) clients mirror the host's wave/intermission + tally so InIntermission + the vote UI work
    public float WaveGap => _waveGap;
    public float WaveGapFrac => Mathf.Clamp(_waveGap / WaveGapMax, 0f, 1f);
    public bool InIntermission => _toSpawn.Count == 0 && Enemies.Count == 0 && _waveGap > 0f && Wave >= 1;
    public float SkipHoldFrac => Mathf.Clamp(_skipHold / 2f, 0f, 1f);

    private Enemy _boss;
    private float _bossAddT = 0f;
    private System.Collections.Generic.List<string> _bossAddPool = new();   // (NEW) boss-wave add types; the director draws groups from this
    private int _bossAddGroup = 5;                                          // (NEW) current group size, adjusted by DPS trend
    private float _bossDmgAccum = 0f, _bossPrevDps = 0f;                    // (NEW) rolling DPS sample
    private bool _bossDpsInit = false;
    public float BossRecentDps = 0f;                                       // (NEW) last-window DPS → drives boss heat
    private float _poofSndT = 0f;                                          // (NEW) global throttle for the spawn-poof sound
    public Enemy Goblin;
    public float GoblinTime = 0f;
    private int _lootLeft = 0;
    private Rarity _lootMin = Rarity.Rare;
    public bool LootMode => _lootLeft > 0;
    public Rarity LootMin => _lootMin;

    public readonly List<RitualCircle> Rituals = new();
    public int Kills = 0;
    private int _rewardCat = -1;
    private int _rewardLeft = 0;
    public bool RewardMode => _rewardLeft > 0;
    public int RewardCat => _rewardCat;
    private int _eventBlock = -1;
    private int _eventsThisBlock = 0;
    private World _world;
    public long WorldSeed;   // shared map seed; host generates it, clients receive it so everyone gets the same world (NEW)

    public int Gold = 0;             // persists across runs
    public int LastWaveGold = 0;
    public float GoldFlash = 0f;
    private float _waveTimer = 0f;
    // ---- enemy director (host-side dynamic difficulty) ----
    public float Heat = 1f;                 // difficulty multiplier; rises when the party stomps, falls when it struggles
    private float _waveMinHpFrac = 1f;      // lowest party HP fraction seen during the wave
    private bool _downThisWave = false;     // did anyone go down this wave?
    public float DirectorStatMul => 1f + (Heat - 1f) * 0.5f;   // enemies take HALF the heat as raw HP/damage (density/elites carry the rest)
    private float _waveMaxComboMul = 1f;
    private int _waveComboAccrued = 0;
    public void AccrueCombo(int n) => _waveComboAccrued += n;
    public static float GameClock = 0f;   // advances only while Playing (so menus don't expire combos)
    public float BossTokens = 0f;          // boss = 1, mini-boss = 0.5; resets each game (stage 2 spends them)
    public void DropBossToken(Enemy e) { float amt = (e.Label != null && e.Label.Contains("MINI")) ? 0.5f : 1f; BossTokens += amt; NetMgr?.BroadcastBossToken(amt); }
    private float _tension = 0f;

    // ---- day / night cycle (full cycle = DayLength seconds) ----
    private Godot.Environment _env;
    private ShaderMaterial _skyMat;
    private float _skyTime = 0f;   // (NEW) sky animation clock; only advances while Playing so the sky freezes on pause
    private DirectionalLight3D _sun;
    public const float DayLength = 300f;
    public float DayTime = 0.0f;     // 0..1
    private static readonly string[] PhaseNames = { "Morning", "Noon", "Afternoon", "Evening", "Dusk", "Night", "Midnight", "Dawn" };
    // Moonlit-forest fairytale palette: cool misty days, golden-but-muted evenings, deep midnight-blue
    // nights lit by cool silver-blue moonlight. Phases: Morning,Noon,Afternoon,Evening,Dusk,Night,Midnight,Dawn (NEW)
    private static readonly Color[] KfTop = {
        new(0.24f,0.34f,0.46f), new(0.30f,0.46f,0.60f), new(0.26f,0.40f,0.54f), new(0.18f,0.22f,0.40f),
        new(0.10f,0.13f,0.30f), new(0.05f,0.07f,0.18f), new(0.03f,0.04f,0.12f), new(0.14f,0.18f,0.36f) };
    private static readonly Color[] KfHoriz = {
        new(0.74f,0.66f,0.60f), new(0.60f,0.72f,0.72f), new(0.78f,0.70f,0.60f), new(0.80f,0.54f,0.48f),
        new(0.50f,0.40f,0.52f), new(0.22f,0.30f,0.44f), new(0.14f,0.20f,0.34f), new(0.56f,0.50f,0.56f) };
    private static readonly Color[] KfSun = {
        new(1.00f,0.88f,0.74f), new(1.00f,0.96f,0.88f), new(1.00f,0.90f,0.78f), new(1.00f,0.78f,0.62f),
        new(0.78f,0.62f,0.70f), new(0.62f,0.74f,1.00f), new(0.56f,0.70f,1.00f), new(0.86f,0.76f,0.78f) };
    private static readonly float[] KfSunE = { 1.2f, 1.45f, 1.25f, 1.0f, 0.75f, 0.62f, 0.52f, 0.85f };
    private static readonly float[] KfAmb = { 0.72f, 0.9f, 0.8f, 0.6f, 0.46f, 0.4f, 0.34f, 0.55f };

    public int PhaseIndex => Mathf.Clamp((int)(DayTime * 8f) % 8, 0, 7);
    public string PhaseName => PhaseNames[PhaseIndex];
    public bool IsNight => PhaseIndex >= 4;   // dusk → dawn
    public float PhaseTimeLeft { get { float seg = DayLength / 8f; float into = (DayTime * 8f - PhaseIndex) * seg; return Mathf.Max(0f, seg - into); } }

    private void ApplyDayNight()
    {
        if (_skyMat == null) return;
        int a = PhaseIndex, b = (a + 1) % 8;
        float frac = DayTime * 8f - a;
        Color top = KfTop[a].Lerp(KfTop[b], frac);
        Color horiz = KfHoriz[a].Lerp(KfHoriz[b], frac);
        Color sunc = KfSun[a].Lerp(KfSun[b], frac);
        float sune = Mathf.Lerp(KfSunE[a], KfSunE[b], frac);
        float amb = Mathf.Lerp(KfAmb[a], KfAmb[b], frac);
        float night = Mathf.Clamp((1.1f - sune) / 0.7f, 0f, 1f);   // 0 in daylight → ~1 at deep night; drives stars/moon/aurora (NEW)
        float ma = DayTime * Mathf.Tau;
        var moonDir = new Vector3(Mathf.Cos(ma) * 0.7f, 0.52f, Mathf.Sin(ma) * 0.7f).Normalized();   // moon at mid elevation, slowly circling, easy to spot (NEW)
        _skyMat.SetShaderParameter("sky_top", V3lin(top));
        _skyMat.SetShaderParameter("sky_horizon", V3lin(horiz));
        _skyMat.SetShaderParameter("ground_horizon", V3lin(horiz.Darkened(0.6f)));
        _skyMat.SetShaderParameter("moon_dir", moonDir);
        _skyMat.SetShaderParameter("night", night);
        _skyMat.SetShaderParameter("star_amt", night);
        _skyMat.SetShaderParameter("aurora_amt", night * 0.6f);
        if (_sun != null) { _sun.LightColor = sunc; _sun.LightEnergy = sune; _sun.RotationDegrees = new Vector3(Mathf.Lerp(-20f, -78f, Mathf.Sin(DayTime * Mathf.Pi)), -38f, 0f); }
        if (_env != null) { _env.AmbientLightEnergy = amb; _env.FogLightColor = horiz.Darkened(0.3f); }
    }

    private float ComputeTension()
    {
        if (Player == null || State != GameState.Playing) return 0f;
        var pp = Player.GlobalPosition;
        int nearby = 0;
        foreach (var e in Enemies)
            if (e != null && !e.Dead && GodotObject.IsInstanceValid(e) && !e.IsGoblin && e.GlobalPosition.DistanceTo(pp) < 45f) nearby++;
        float t = Mathf.Min(0.5f, nearby / 8f * 0.5f);
        if (_boss != null && GodotObject.IsInstanceValid(_boss) && !_boss.Dead) t += 0.25f;
        t += Mathf.Min(0.25f, EnemyBolt.Live * 0.05f);
        t += Mathf.Min(0.40f, Player.HurtT * 0.40f);
        t += Mathf.Min(0.15f, Player.DashT * 0.15f);
        return Mathf.Clamp(t, 0f, 1f);
    }

    public System.Collections.Generic.List<UpgradeCard> Choices;
    public int BanCount = 0, LuckRerollCount = 0;               // (NEW) both cost-double each use this run
    public int Pick2Count = 0;                                 // (NEW) pick-2 cost doubles each use this run
    private int _pick2Extra = 0;                               // extra picks pending from a pick-2
    private bool _luckRerollNext = false;
    public int RerollCost => 12 + (Player != null ? Player.Level : 0) * 3;   // (NEW) scales with player level
    public int BanCost => 50 * (1 << Mathf.Min(BanCount, 8));                // (NEW) ×2 per ban, capped
    public int LuckRerollCost => 30 * (1 << Mathf.Min(LuckRerollCount, 8));  // (NEW) ×2 per use, capped
    public int Pick2Cost => 60 * (1 << Mathf.Min(Pick2Count, 8));            // (NEW) ×2 per use, capped
    public bool Pick2Armed => _pick2Extra > 0;
    private int _pendingLevels = 0;
    private readonly RandomNumberGenerator _rng = new();
    public Sfx Sfx;
    public DevConsole Dev;   // (NEW) dev/test console (~ to toggle)
    public int ChoiceGen = 0;
    public int PendingAttune = -1;
    public float SelectLock = 0f;   // brief input lockout when a choice screen opens

    // pending swap (when a slot is full)
    public bool SwapIsFin;
    private FinType _swFin; private int _swEvery; private float _swPow;
    private ModType _swMod; private float _swMag; private Rarity _swRar;
    public Rarity SwapRarity => _swRar;
    public FinType SwapFin => _swFin;
    public ModType SwapMod => _swMod;

    public override void _Ready()
    {
        I = this;
        _rng.Randomize();
        EnemyBolt.Live = 0;
        GameClock = 0f;
        LoadGold();
        SetupInput();
        BuildWorld();

        Player = new Player();
        AddChild(Player);
        Player.GlobalPosition = new Vector3(0, 0, 0);
        Players.Clear();
        Players.Add(Player);   // local player is the first (and currently only) entry

        var layer = new CanvasLayer();
        AddChild(layer);
        Hud = new Hud();
        layer.AddChild(Hud);
        Sfx = new Sfx();
        AddChild(Sfx);
        Dev = new DevConsole();
        AddChild(Dev);
        Sfx.MusicVol = _savedMusicVol;
        Player.MouseSens = _savedSens;

        NetMgr = new Net();
        NetMgr.Name = "Net";   // stable path (/root/Game/Net) so RPCs resolve identically on every peer
        AddChild(NetMgr);
        var lobbyLayer = new CanvasLayer { Layer = 50 };
        AddChild(lobbyLayer);
        LobbyUi = new Lobby();
        lobbyLayer.AddChild(LobbyUi);

        if (s_witch >= 0) { LobbyUi.Hide(); StartGame(); }   // restart kept the chosen witch — skip lobby
        else { State = GameState.Lobby; LobbyUi.Show(); Input.MouseMode = Input.MouseModeEnum.Visible; }
    }

    // ---- lobby callbacks ----
    public Net NetMgr;
    public Lobby LobbyUi;
    // solo or host = we own/drive the world; a connected client does not
    public void GrantSharedXp(float amt)
    {
        Player?.AddXp(amt);
        NetMgr?.BroadcastXp(amt);   // allies level on the same XP
    }

    // ---- hold-E to interact with world objects (first to finish the hold claims it) ----
    private const float HoldTime = 1.0f;
    private float _holdE = 0f;
    private System.Action _holdAction = null;
    private string _holdPrompt = "";
    private float _holdNeed = HoldTime;
    public float HoldEFrac => Mathf.Clamp(_holdE / Mathf.Max(0.0001f, _holdNeed), 0f, 1f);
    public bool HoldEActive => _holdAction != null;
    public string HoldEPrompt => _holdPrompt;

    public void HostOpenChest(int netId, long peer)
    {
        foreach (var c in Chests)
            if (c != null && GodotObject.IsInstanceValid(c) && !c.Opened && c.NetId == netId) { c.Open(peer); return; }
    }

    private void UpdateInteract(float dt)
    {
        _holdAction = null; _holdPrompt = ""; _holdNeed = HoldTime;
        if (Player == null || !CanControlLocal()) { _holdE = 0f; return; }
        Vector3 me = Player.GlobalPosition;
        float best = 3.5f * 3.5f;
        System.Action act = null; string prompt = "";
        bool winRevive = false, reviveInstant = false;

        if (IsAuthority)
        {
            foreach (var c in Chests)
            {
                if (c == null || !GodotObject.IsInstanceValid(c) || c.Opened) continue;
                float d = (c.GlobalPosition - me).LengthSquared();
                if (d < best) { best = d; var cc = c; act = () => cc.Open(0); prompt = "Hold E — open chest"; }
            }
        }
        else if (NetMgr != null && NetMgr.NearestPickupChest(me, 3.5f, out int cid, out float cd2))
        {
            if (cd2 < best) { best = cd2; int id = cid; act = () => NetMgr.RequestOpenChest(id); prompt = "Hold E — open chest"; }
        }

        if (_mystic != null && GodotObject.IsInstanceValid(_mystic) && !_mystic.Triggered)
        {
            float d = (_mystic.GlobalPosition - me).LengthSquared();
            if (d < best) { best = d; var m = _mystic; act = () => { if (!IsAuthority) NetMgr?.ClaimVendor(m.NetId); OpenMystic(m); }; prompt = "Hold E — the Mystic"; }
        }
        if (_scroll != null && GodotObject.IsInstanceValid(_scroll) && !_scroll.Triggered)
        {
            float d = (_scroll.GlobalPosition - me).LengthSquared();
            if (d < best) { best = d; var s = _scroll; act = () => { if (!IsAuthority) NetMgr?.ClaimVendor(s.NetId); OpenScroll(s); }; prompt = "Hold E — the Scrolls"; }
        }
        foreach (var r in _roulettes)
        {
            if (r == null || !GodotObject.IsInstanceValid(r) || r.Triggered) continue;
            float d = (r.GlobalPosition - me).LengthSquared();
            if (d < best) { best = d; var rr = r; act = () => { if (!IsAuthority) NetMgr?.ClaimRoulette(rr.NetId); OpenRoulette(rr); }; prompt = "Hold E — spin the wheel"; }
        }

        // Expedition objective: light the active beacon
        if (IsAuthority && InExpedition && _expoRun != null && _expoRun.BeaconReady)
        {
            float d = (_expoRun.ActivePos - me).LengthSquared();
            if (d < best) { best = d; act = () => _expoRun.LightBeacon(this); prompt = "Hold E — light the beacon"; }
        }
        else if (!IsAuthority && InExpedition && _expoRun != null && _expoRun.BeaconReady)   // client asks the host to light it
        {
            float d = (_expoRun.ActivePos - me).LengthSquared();
            if (d < best) { best = d; act = () => NetMgr?.RequestLightBeacon(); prompt = "Hold E — light the beacon"; }
        }

        // revive a downed ally (networked); a charged Divine witch revives instantly with a sky-beam
        if (NetMgr != null && NetMgr.Active && NetMgr.NearestDownedAlly(me, 3.5f, out long rpeer, out float rd2))
        {
            if (rd2 < best)
            {
                best = rd2; long peer = rpeer;
                bool divine = Player.DivineWitch && Player.Interventions > 0;
                winRevive = true; reviveInstant = divine;
                act = () =>
                {
                    if (Player.DivineWitch && Player.Interventions > 0) { Player.Interventions--; NetMgr.RevivePeer(peer, 1f, true); }
                    else NetMgr.RevivePeer(peer, 0.4f, false);
                };
                prompt = divine ? "Hold E — Divine Revival" : "Hold E — revive ally";
            }
        }

        _holdNeed = (winRevive && reviveInstant) ? 0.02f : HoldTime;
        _holdAction = act; _holdPrompt = prompt;
        if (act != null && Input.IsPhysicalKeyPressed(Key.E))
        {
            _holdE += dt;
            if (_holdE >= _holdNeed) { _holdE = 0f; var a = act; _holdAction = null; a(); }
        }
        else _holdE = 0f;
    }

    public bool IsAuthority => NetMgr == null || !NetMgr.Active || NetMgr.IsHost;
    public int WardenCount => (NetMgr != null && NetMgr.Active) ? NetMgr.PlayerCount() : 1;

    // (NEW) this machine's peer id (host/solo = 1). DoT/area sources stamp their caster with this.
    public int LocalPeer => (NetMgr != null && NetMgr.Active) ? NetMgr.LocalId : 1;
    // (NEW) credit a DoT/area effect's combo to its CASTER at the reduced drip rate, across the network.
    // Caster-owned nodes (fields/heals) run on the caster's machine, so owner == LocalPeer → local bump. Enemy
    // DoT ticks run on the host, so a client's DoT (owner != host) is delivered to that client via RPC. Per-owner
    // throttle bounds the RPC rate; the recipient's ComboFromDot also self-throttles to ~1/s.
    private readonly System.Collections.Generic.Dictionary<int, float> _dotCreditCd = new();
    public void AwardDotCombo(int owner)
    {
        if (owner == 0) owner = LocalPeer;
        if (_dotCreditCd.TryGetValue(owner, out var cd) && cd > 0f) return;
        _dotCreditCd[owner] = 0.5f;
        if (NetMgr == null || !NetMgr.Active || owner == LocalPeer) Player?.ComboFromDot();
        else NetMgr.SendDotCombo(owner);
    }
    private int _netEnemySeq = 1;

    // ---- shared world-run / pause model ----
    // Each player has a category: 0 = active (playing, or in a non-pausing menu like ult/stats),
    // 1 = soft pause (ESC — only pauses the world if EVERYONE pauses), 2 = level-up gate (pauses for all).
    public bool WorldRunning = true;
    public int LocalCat() => State switch
    {
        GameState.Pause => 1,
        GameState.LevelUp or GameState.Swap or GameState.Roulette or GameState.Element or GameState.BindKey => ChestPick ? 0 : 2,
        GameState.Playing or GameState.Stats or GameState.Ult or GameState.UltMenu or GameState.Mystic or GameState.Scroll => 0,
        _ => 0
    };
    // local player may act only while playing AND the shared world is running
    public bool CanControlLocal() => State == GameState.Playing && WorldRunning && !ConsoleOpen && !(Player != null && Player.Downed);

    private void ComputeWorldRunning()
    {
        if (!IsAuthority) return;   // clients receive WorldRunning from the host
        int my = LocalCat();
        bool anyGate = my == 2;
        bool allSoft = my == 1;
        if (NetMgr != null && NetMgr.Active && NetMgr.IsHost)
            NetMgr.ForEachPeerCat(c => { if (c == 2) anyGate = true; if (c != 1) allSoft = false; });
        bool run = _started && State != GameState.Over && !anyGate && !allSoft;
        WorldRunning = run;
        if (NetMgr != null && NetMgr.Active && NetMgr.IsHost) NetMgr.BroadcastWorldRunning(run);
    }

    // nearest player to an enemy: host's own player, or a connected ally avatar
    public Vector3 ResolveEnemyTarget(Vector3 from, bool canTargetMinions, out long peer, out bool isMinion)
    {
        peer = 0; isMinion = false;
        Vector3 best = Player != null ? Player.GlobalPosition : Vector3.Zero;
        float bd = Player != null ? (Player.GlobalPosition - from).LengthSquared() : float.MaxValue;
        if (NetMgr != null && NetMgr.Active && NetMgr.IsHost && NetMgr.NearestRemote(from, out long rp, out Vector3 rpos))
        {
            float d = (rpos - from).LengthSquared();
            if (d < bd) { best = rpos; bd = d; peer = rp; }
        }
        if (canTargetMinions)   // melee foes peel off to smash a nearby tree-ent — they're treated like an ally target
        {
            Vector3 mpos = Vector3.Zero; float md = float.MaxValue; bool mf = false;
            if (Player != null && Player.VerdantWitch)
                foreach (var t in Player.Ents)
                { if (t == null || !GodotObject.IsInstanceValid(t)) continue; float d = (t.GlobalPosition - from).LengthSquared(); if (d < md) { md = d; mpos = t.GlobalPosition; mf = true; } }
            if (NetMgr != null && NetMgr.IsHost) NetMgr.ConsiderGhostMinions(from, ref md, ref mpos, ref mf);
            if (mf && md < bd * 0.85f) { best = mpos; peer = 0; isMinion = true; }   // only divert for a clearly-closer ent
        }
        return best;
    }
    public void LobbySolo() { GoCharSelect(); }
    public void LobbyHost() { NetMgr.HostGame(Net.DefaultPort); GoCharSelect(); }
    public void LobbyJoin(string ip) { NetMgr.JoinGame(ip, Net.DefaultPort); GoCharSelect(); }
    private void GoCharSelect()
    {
        if (LobbyUi != null) LobbyUi.Hide();
        State = GameState.CharSelect; Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    // ---- transient on-screen popup ----
    public string Toast = "";
    public float ToastT = 0f;
    public void ShowToast(string msg, float secs = 3.5f) { Toast = msg; ToastT = secs; }

    private static int s_witch = -1;   // survives scene reload; -1 = show selection
    private bool _started = false;

    private void StartGame()
    {
        if (_started) return;
        _started = true;
        ConfigureWitch(s_witch < 0 ? 0 : s_witch);
        // in co-op, host and joiner spawn a few steps apart at the same area so they can see each other
        if (NetMgr != null && NetMgr.Active)
            Player.GlobalPosition = NetMgr.IsHost ? new Vector3(-2.5f, 0, 0) : new Vector3(2.5f, 0, 0);
        if (IsAuthority) NextWave();   // host/solo drive waves; clients receive enemies over the network
        State = GameState.Playing;
        Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    // Swap the procedural open world for an authored Expedition leg. (First slice: geometry + walk only —
    // objective beacon, stationary-Heat director, and surge come in following steps. Currently host/solo;
    // the layout is seed-deterministic so MP sync is just "send the seed" next.)
    public void BeginExpedition(ulong seed)
    {
        if (_world != null) { _world.QueueFree(); _world = null; }   // frees the streamed chunk meshes
        Smashables.Clear();   // pumpkins were children of the freed chunks (NEW)
        Flowers.Clear();      // and flowers (NEW)
        if (_expoRoot != null && GodotObject.IsInstanceValid(_expoRoot)) _expoRoot.QueueFree();
        Expo = Expedition.Build(seed);
        _expoRoot = Expedition.Realize(this, Expo);
        InExpedition = true;
        // clean slate: clear any endless-mode enemies/queue and reset the director for the leg
        foreach (var e in Enemies) if (GodotObject.IsInstanceValid(e)) e.QueueFree();
        Enemies.Clear();
        _toSpawn.Clear();
        Heat = 1f;
        _expoRun = new ExpoRun(Expo);
        bool client = NetMgr != null && NetMgr.Active && !NetMgr.IsHost;
        if (Player != null) Player.GlobalPosition = Expo.PlayerSpawn + new Vector3(client ? 1.5f : -1.5f, 0, 1.5f);
        Hud?.Banner("expedition: reach the beacon");
        if (NetMgr != null && NetMgr.Active && NetMgr.IsHost) { _expoStateSig = ""; NetMgr.BroadcastBeginExpedition((long)seed); }
    }

    // (NEW) Phase 1: enter the hedge maze (F6 test). Reuses the Expedition world-swap (InExpedition flattens
    // the surface + frees the streamed world + stops the wave loop); InMaze gates maze-specific logic.
    public void EnterMaze(ulong seed)
    {
        _preMazePos = Player != null ? Player.GlobalPosition : Vector3.Zero;
        if (_world != null) { _world.QueueFree(); _world = null; }
        Smashables.Clear(); Flowers.Clear();
        if (_expoRoot != null && GodotObject.IsInstanceValid(_expoRoot)) { _expoRoot.QueueFree(); _expoRoot = null; }
        if (_mazeRoot != null && GodotObject.IsInstanceValid(_mazeRoot)) { _mazeRoot.QueueFree(); _mazeRoot = null; }
        if (_mazePortalNode != null && GodotObject.IsInstanceValid(_mazePortalNode)) { _mazePortalNode.QueueFree(); _mazePortalNode = null; }
        MazeWisps.Clear();
        _maze = Maze.Build(seed, WardenCount);
        _mazeRoot = Maze.Realize(this, _maze);
        InExpedition = true; InMaze = true; _mazeFound = false;
        foreach (var e in Enemies) if (GodotObject.IsInstanceValid(e)) e.QueueFree();
        Enemies.Clear(); _toSpawn.Clear(); Heat = 1f; _specialSpawnT = 20f;
        UpgradePool.Banned.Clear(); BanCount = 0; LuckRerollCount = 0; _luckRerollNext = false;   // (NEW) card disables + costs reset each run
        Pick2Count = 0; _pick2Extra = 0;
        _mazeElapsed = 0f; _mazeChaseT = 0f; _mazeSpawnT = 3f; _mazeChaseDist = null; _mazeGrace = 2.5f; _specialSpawnT = 15f;   // brief grace + a delay before the first special enemy
        if (IsAuthority)   // seed an idle horde around the maze at load (scaled by players); they wake on sight/sound
        {
            int horde = 14 + WardenCount * 8;
            for (int h = 0; h < horde; h++)
            {
                Vector2I cell; int tries = 0;
                do { cell = new Vector2I(_mazeRng.RandiRange(0, _maze.W - 1), _mazeRng.RandiRange(0, _maze.H - 1)); tries++; }
                while (tries < 8 && _maze.Spawns.Exists(sp => Mathf.Abs(sp.X - cell.X) + Mathf.Abs(sp.Y - cell.Y) < 6));   // not right on a player
                SpawnMazeEnemy("swarmer", _maze.CellCenter(cell));
            }
        }
        bool mp = NetMgr != null && NetMgr.Active;
        int idx = mp ? NetMgr.LocalSpawnIndex() : 0;
        idx = Mathf.Clamp(idx, 0, _maze.Spawns.Count - 1);
        if (Player != null) Player.GlobalPosition = _maze.CellCenter(_maze.Spawns[idx]) + new Vector3(0f, 1f, 0f);
        if (!mp)   // solo: find a target elemental statue first (so you sneak past the idle horde), THEN the portal opens
        {
            _mazeStatueTarget = _maze.Chambers.Count > 0 ? _mazeRng.RandiRange(0, _maze.Chambers.Count - 1) : -1;
            string elem = _mazeStatueTarget >= 0 ? ((DamageType)_maze.ChamberElem[_mazeStatueTarget]).ToString() : "";
            Hud?.Banner(_mazeStatueTarget >= 0 ? $"find the {elem} statue" : "find the exit portal");
        }
        else       // MP: everyone spawns apart and must find each other; host mirrors entry to clients
        {
            if (IsAuthority) NetMgr.BroadcastEnterMaze((long)seed);
            Hud?.Banner("find each other — hold T to flare");
        }
    }

    // (NEW) build/relocate the exit portal at a cell + refresh the wisp distance-field.
    public void SpawnPortal(Vector2I cell, bool net = true)
    {
        if (_maze == null) return;
        _maze.Portal = cell;
        _mazeDist = Maze.DistField(_maze, cell);
        if (_mazePortalNode != null && GodotObject.IsInstanceValid(_mazePortalNode)) _mazePortalNode.QueueFree();
        var ppos = _maze.CellCenter(cell);
        var root = new Node3D { Name = "MazePortal" }; AddChild(root); _mazePortalNode = root;
        var portalCol = new Color(0.62f, 0.5f, 1f);
        var pil = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.9f, BottomRadius = 1.1f, Height = 5f }, MaterialOverride = Emissive(portalCol, 2.4f) };
        pil.Position = ppos + new Vector3(0, 2.5f, 0); root.AddChild(pil);
        var pOrb = new MeshInstance3D { Mesh = new SphereMesh { Radius = 1.3f, Height = 2.6f }, MaterialOverride = Emissive(portalCol, 3.2f) };
        pOrb.Position = ppos + new Vector3(0, 5.5f, 0); root.AddChild(pOrb);
        var beamMat = Emissive(portalCol, 2.4f);
        beamMat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; beamMat.AlbedoColor = new Color(portalCol.R, portalCol.G, portalCol.B, 0.16f); beamMat.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        var beam = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.7f, BottomRadius = 1.0f, Height = 50f }, MaterialOverride = beamMat };
        beam.Position = ppos + new Vector3(0, 25f, 0); root.AddChild(beam);   // tall beam so the exit is visible over the hedges
        root.AddChild(new OmniLight3D { Position = ppos + new Vector3(0, 4f, 0), OmniRange = _maze.Cell * 5f, LightColor = portalCol, LightEnergy = 3.2f });
        var hum = new AudioStreamPlayer3D { Stream = Sfx.PortalHumStream(), Autoplay = true, VolumeDb = -6f, MaxDistance = 46f, UnitSize = 10f };
        hum.Position = ppos + new Vector3(0, 2f, 0); root.AddChild(hum);
        if (net) NetMgr?.BroadcastVfx(38, ppos, Vector3.Zero, cell.X, cell.Y, portalCol);
    }

    // (NEW) leave the maze — rebuild the open world and drop the player back where they left.
    public void ExitMaze()
    {
        if (_mazeRoot != null && GodotObject.IsInstanceValid(_mazeRoot)) { _mazeRoot.QueueFree(); _mazeRoot = null; }
        if (_mazePortalNode != null && GodotObject.IsInstanceValid(_mazePortalNode)) { _mazePortalNode.QueueFree(); _mazePortalNode = null; }
        Blockers.Clear(); Decks.Clear(); Ramps.Clear();
        InExpedition = false; InMaze = false; _maze = null; _mazeFound = false; _mazeStatueTarget = -1;
        _mazeChaseDist = null;
        MazeWisps.Clear();
        _world = new World();
        _world.SetSeed((ulong)WorldSeed);
        AddChild(_world);
        _world.Update(_preMazePos);
        if (Player != null) Player.GlobalPosition = _preMazePos;
        Hud?.Banner("back to the grove");
    }

    // (NEW) Phase 3: spawn the guide fairy heading to the portal (drops navigable wisps as she goes).
    public void SpawnFairy(Vector3 from, bool net = true)
    {
        if (_maze == null) return;
        _mazeDist = Maze.DistField(_maze, _maze.Portal);   // cache the corridor distance field for wisp directions
        var f = new Fairy { Portal = _maze.PortalPos };
        AddChild(f);
        f.GlobalPosition = new Vector3(from.X, 0f, from.Z);
        if (IsAuthority) foreach (var e in Enemies) if (GodotObject.IsInstanceValid(e) && e.IsSwarmer) e.Alert();   // phase 2: the horde wakes and hunts
        if (IsAuthority) _specialSpawnT = Mathf.Max(_specialSpawnT, 12f);   // first Taker ~12s into the escape
        Sfx?.HordeScream();   // (NEW) blood-curdling server-wide scream — they're all coming
        if (net) NetMgr?.BroadcastVfx(37, from, Vector3.Zero, 0f, 0f, Colors.White);
    }

    // navigable direction from a maze cell toward the portal (used by the fairy's wisps + wrong-direction spawns)
    public Vector3 MazePathDir(Vector2I cell) => (_maze != null && _mazeDist != null) ? Maze.PathDir(_maze, _mazeDist, cell) : Vector3.Zero;
    public Vector3 MazePortalDir(Vector3 pos) => (_maze != null && _mazeDist != null) ? Maze.PathDir(_maze, _mazeDist, Maze.CellOf(_maze, pos)) : Vector3.Zero;   // corridor direction toward the portal
    public Vector3 MazePortalWorld => (_mazePortalNode != null && GodotObject.IsInstanceValid(_mazePortalNode)) ? _mazePortalNode.GlobalPosition : (_maze != null ? _maze.CellCenter(_maze.Portal) : Vector3.Zero);

    // solo target statue (for the minimap marker) — null once found
    public Vector3? MazeStatueTargetPos => (InMaze && !_mazeFound && _maze != null && _mazeStatueTarget >= 0 && _mazeStatueTarget < _maze.Chambers.Count) ? _maze.CellCenter(_maze.Chambers[_mazeStatueTarget]) : (Vector3?)null;
    public Color MazeStatueColor => (_maze != null && _mazeStatueTarget >= 0 && _mazeStatueTarget < _maze.ChamberElem.Count) ? DamageTypes.Col((DamageType)_maze.ChamberElem[_mazeStatueTarget]) : Colors.White;

    // find-each-other: every pair of players within ~6u AND with clear line of sight (no hedge between)
    private bool MazeAllMet(List<Vector3> pos)
    {
        for (int i = 0; i < pos.Count; i++)
            for (int j = i + 1; j < pos.Count; j++)
            {
                var d = pos[j] - pos[i]; d.Y = 0f;
                if (d.Length() > 6f) return false;
                if (!Maze.HasLoS(_maze, Maze.CellOf(_maze, pos[i]), Maze.CellOf(_maze, pos[j]))) return false;
            }
        return true;
    }

    // enemy corridor nav: the next cell centre toward the players (gradient down the chase field), or the
    // real target when basically on top of a player. Retargets maze mobs so they route through corridors.
    public Vector3 MazeWaypoint(Vector3 enemyPos, Vector3 fallback)
    {
        if (_maze == null || _mazeChaseDist == null) return fallback;
        var cell = Maze.CellOf(_maze, enemyPos);
        int here = _mazeChaseDist[cell.X, cell.Y];
        if (here <= 1) return fallback;
        Vector2I best = cell; int bd = here;
        foreach (var d in new[] { new Vector2I(0, 1), new Vector2I(0, -1), new Vector2I(1, 0), new Vector2I(-1, 0) })
        {
            var n = cell + d;
            if (_maze.In(n) && !_maze.Blocked(cell, n) && _mazeChaseDist[n.X, n.Y] >= 0 && _mazeChaseDist[n.X, n.Y] < bd) { bd = _mazeChaseDist[n.X, n.Y]; best = n; }
        }
        if (best == cell) return fallback;
        var wp = _maze.CellCenter(best);
        return new Vector3(wp.X, fallback.Y, wp.Z);
    }

    // is the segment from→to blocked by a tall wall (Deck TopY>1.8)? Used to occlude HUD health bars behind hedges.
    // TODO: gate the remaining radius-AoE loops with `&& !Game.I.SightBlocked(center, e.GlobalPosition)` the same way
    // the ember/blood/bleed/curse/nature/holy AoEs are gated (Player.cs), so no area damage clips through walls.
    public bool SightBlocked(Vector3 from, Vector3 to)
    {
        if (Decks.Count == 0) return false;
        int steps = Mathf.Clamp(Mathf.CeilToInt((to - from).Length() / 0.7f), 4, 120);   // ≤0.7u spacing → can't skip a 1.2-thick hedge
        for (int i = 1; i < steps; i++)
        {
            var p = from.Lerp(to, i / (float)steps);
            foreach (var d in Decks)
            {
                if (d.TopY < 1.8f) continue;   // low pad, not a wall
                if (p.Y < d.TopY && Mathf.Abs(p.X - d.Center.X) < d.Half.X && Mathf.Abs(p.Z - d.Center.Z) < d.Half.Y) return true;
            }
        }
        return false;
    }

    public bool MazeHasLoS(Vector3 a, Vector3 b) => _maze == null || Maze.HasLoS(_maze, Maze.CellOf(_maze, a), Maze.CellOf(_maze, b));

    // is a world position inside a solid blocker (tree / cover pillar)? Used so a Taker charge stuns on trees, not just walls.
    public bool BlockerAt(Vector3 pos, float extra = 0f)
    {
        foreach (var b in Blockers)
        {
            float dx = pos.X - b.Pos.X, dz = pos.Z - b.Pos.Z, rr = b.Radius + extra;
            if (dx * dx + dz * dz < rr * rr) return true;
        }
        return false;
    }

    // greedy corridor step from `from` toward `to` (investigating zombies shamble toward a heard sound)
    public Vector3 MazeStepToward(Vector3 from, Vector3 to)
    {
        if (_maze == null) return Vector3.Zero;
        var cell = Maze.CellOf(_maze, from);
        var tcell = Maze.CellOf(_maze, to);
        if (cell == tcell) { var dd = to - from; dd.Y = 0f; return dd.LengthSquared() > 0.01f ? dd.Normalized() : Vector3.Zero; }
        var tc = _maze.CellCenter(tcell);
        Vector2I best = cell; float bd = float.MaxValue;
        foreach (var d in new[] { new Vector2I(0, 1), new Vector2I(0, -1), new Vector2I(1, 0), new Vector2I(-1, 0) })
        {
            var n = cell + d;
            if (_maze.In(n) && !_maze.Blocked(cell, n)) { float dist = _maze.CellCenter(n).DistanceSquaredTo(tc); if (dist < bd) { bd = dist; best = n; } }
        }
        if (best == cell) return Vector3.Zero;
        var dir = _maze.CellCenter(best) - from; dir.Y = 0f;
        return dir.LengthSquared() > 0.01f ? dir.Normalized() : Vector3.Zero;
    }

    // a player-made noise (loudness tiers) — idle swarmers within range hear it (additive across players)
    public void EmitSound(Vector3 pos, float loud)
    {
        if (!IsAuthority || !InMaze) return;
        float range = loud >= 3.5f ? 80f : loud * 6f;   // firework carries across the maze; every other noise stays local
        foreach (var e in Enemies)
            if (GodotObject.IsInstanceValid(e) && e.IsSwarmer)
            {
                float d = e.GlobalPosition.DistanceTo(pos);
                if (d < range) e.Hear(pos, loud * (1f - d / range));   // closer + louder = more
            }
    }

    public void PlayerSound(Vector3 pos, float loud)   // host applies directly; clients route to the host
    {
        if (!InMaze) return;
        if (IsAuthority) EmitSound(pos, loud);
        else NetMgr?.ReportSound(pos, loud);
    }

    private string _expoStateSig = "";
    // host: push the objective state to clients only when it actually changes (event-driven, cheap)
    private void BroadcastExpoStateIfHost()
    {
        if (_expoRun == null || NetMgr == null || !NetMgr.Active || !NetMgr.IsHost) return;
        string sig = _expoRun.ActiveBeacon + "|" + (int)_expoRun.Cur + "|" + _expoRun.LitMask() + "|" + _expoRun.ObjectiveText;
        if (sig == _expoStateSig) return;
        _expoStateSig = sig;
        NetMgr.BroadcastExpoState(_expoRun.ActiveBeacon, (int)_expoRun.Cur, _expoRun.LitMask(), _expoRun.ObjectiveText);
    }

    // client: apply synced objective state (recolors newly-lit beacons + banners objective changes)
    public void ApplyExpoState(int activeBeacon, int phase, int litMask, string objective)
    {
        if (_expoRun == null) return;
        string prev = _expoRun.ObjectiveText;
        _expoRun.ApplyNetState(activeBeacon, phase, litMask, objective);
        if (objective != prev) Hud?.Banner(objective);
    }

    // host: a client requested to light the active beacon (LightBeacon self-guards on BeaconReady)
    public void HostLightBeacon() { if (InExpedition && _expoRun != null) _expoRun.LightBeacon(this); }

    private void ConfigureWitch(int i)
    {
        switch (i)        {
            case 3:    // The Verdant Witch
                Player.PrimaryType = DamageType.Nature;
                Player.SecondaryType = DamageType.Nature;
                Player.NightAffinity = false;
                Player.VerdantWitch = true;
                Player.DamageMul = 0.9f;        // lower personal DPS — her power is the Grove
                Player.S.DmgResist = 0.18f;     // medium durability
                break;
            case 5:    // The Frost Witch (NEW) — long-range sniper: freezing beam + charged icicle spear + shatter burst
                Player.PrimaryType = DamageType.Frost;
                Player.SecondaryType = DamageType.Frost;
                Player.NightAffinity = false;
                Player.FrostWitch = true;
                Player.DamageMul = 0.95f;       // steady personal DPS — her burst comes from freezing + shattering
                Player.S.DmgResist = 0.12f;     // fragile sniper — keep your distance
                Player.S.Speed = 9.0f * 0.9f;   // (NEW) a little slower on her feet — a sniper's downside; move-speed cards still lift her
                break;
            case 6:    // The Forsaken Witch (Curse) (NEW) — lock-on curse-suck beam that tethers foes into shared-damage groups
                Player.PrimaryType = DamageType.Curse;
                Player.SecondaryType = DamageType.Curse;
                Player.NightAffinity = false;
                Player.ForsakenWitch = true;
                Player.DamageMul = 0.85f;       // low direct damage — her power is the curse groups + shared damage
                Player.S.DmgResist = 0.15f;     // a controller, fragile but not paper
                break;
            case 4:    // The Gale Witch (Wind) (NEW)
                Player.PrimaryType = DamageType.Wind;
                Player.SecondaryType = DamageType.Wind;
                Player.NightAffinity = false;
                Player.GaleWitch = true;
                Player.DamageMul = 0.92f;       // modest personal DPS — her edge is mobility + control (knockback/cyclones)
                Player.S.DmgResist = 0.12f;     // lightly armored — she survives by evasion (Tailwind), not toughness
                Player.S.Speed = Mathf.Min(16.5f, Player.S.Speed * 1.12f);   // Tailwind: quicker on foot
                if (Player.S.DashCharges < 3) Player.S.DashCharges++;        // Tailwind: an extra dash charge
                Player.DashStock = Player.S.DashCharges;
                break;
            case 2:    // The Crimson Blood Witch
                Player.PrimaryType = DamageType.Blood;
                Player.SecondaryType = DamageType.Blood;
                Player.NightAffinity = false;
                Player.CrimsonWitch = true;
                Player.DamageMul = 1.15f;       // glass cannon — sustained by lifesteal aura + blood stacks
                Player.S.DmgResist = 0.08f;     // low base resistance (glass cannon)
                break;
            case 1:    // The Divine Witch
                Player.PrimaryType = DamageType.Holy;
                Player.SecondaryType = DamageType.Holy;
                Player.NightAffinity = false;
                Player.DivineWitch = true;
                Player.DamageMul = 0.815f;      // midpoint between the pre-buff 0.78 and the post-buff 0.85
                Player.Interventions = 1;       // first Divine Intervention ready
                Player.S.DmgResist = 0.15f;     // low-med base resistance
                break;
            default:   // 0 = The Lunar Witch
                Player.PrimaryType = DamageType.Lunar;
                Player.SecondaryType = DamageType.Lunar;
                Player.NightAffinity = true;   // waxes stronger at night
                Player.DamageMul = 1f;
                Player.S.DmgResist = 0.22f;    // medium base resistance
                break;
        }
        Player.RetintHands();
    }

    // (NEW) dev console: cleanly swap the local player's witch. Wipes the loadout (spell combos / modifiers /
    // minors) and resets stats to that witch's base, so it can be called repeatedly without old flags lingering
    // or Gale's stat buffs compounding.
    public void ChangeWitch(int i)
    {
        if (Player == null) return;
        Player.Fin.Clear(); Player.Mods.Clear(); Player.Minors.Clear();
        Player.S = new Stats();
        Player.DamageMul = 1f; Player.NightAffinity = false; Player.Interventions = 0;
        Player.DivineWitch = Player.CrimsonWitch = Player.VerdantWitch = Player.GaleWitch = Player.FrostWitch = Player.ForsakenWitch = false;
        ConfigureWitch(i);   // sets the new flag + primary/secondary + witch stats, and RetintHands rebuilds the body model
        Player.Hp = Player.S.MaxHp; Player.Mana = Player.S.ManaMax; Player.DashStock = Player.S.DashCharges;
    }

    private void ChooseWitch(int i)
    {
        s_witch = i;
        StartGame();
    }

    // Build the input map in code so there's no fragile project.godot section.
    private void SetupInput()
    {
        void Action(string name, params InputEvent[] evs)
        {
            if (InputMap.HasAction(name)) InputMap.EraseAction(name);
            InputMap.AddAction(name);
            foreach (var e in evs) InputMap.ActionAddEvent(name, e);
        }
        Action("move_forward", new InputEventKey { PhysicalKeycode = Key.W }, new InputEventKey { PhysicalKeycode = Key.Up });
        Action("move_back",    new InputEventKey { PhysicalKeycode = Key.S }, new InputEventKey { PhysicalKeycode = Key.Down });
        Action("move_left",    new InputEventKey { PhysicalKeycode = Key.A }, new InputEventKey { PhysicalKeycode = Key.Left });
        Action("move_right",   new InputEventKey { PhysicalKeycode = Key.D }, new InputEventKey { PhysicalKeycode = Key.Right });
        Action("cast",   new InputEventMouseButton { ButtonIndex = MouseButton.Left });
        Action("charge", new InputEventMouseButton { ButtonIndex = MouseButton.Right });
        Action("dash",   new InputEventKey { PhysicalKeycode = Key.Shift });
        Action("jump",   new InputEventKey { PhysicalKeycode = Key.Space });
        Action("fin1",   new InputEventKey { PhysicalKeycode = Key.Key1 });
        Action("fin2",   new InputEventKey { PhysicalKeycode = Key.Key2 });
        Action("fin3",   new InputEventKey { PhysicalKeycode = Key.Key3 });
        Action("fin4",   new InputEventKey { PhysicalKeycode = Key.Key4 });
        Action("fin5",   new InputEventKey { PhysicalKeycode = Key.Key5 });
        Action("pick1",  new InputEventKey { PhysicalKeycode = Key.Key1 });
        Action("pick2",  new InputEventKey { PhysicalKeycode = Key.Key2 });
        Action("pick3",  new InputEventKey { PhysicalKeycode = Key.Key3 });
        Action("pick4",  new InputEventKey { PhysicalKeycode = Key.Key4 });
        Action("pick5",  new InputEventKey { PhysicalKeycode = Key.Key5 });
        Action("pick6",  new InputEventKey { PhysicalKeycode = Key.Key6 });
        Action("pick7",  new InputEventKey { PhysicalKeycode = Key.Key7 });
        Action("pick7",  new InputEventKey { PhysicalKeycode = Key.Key7 });
        Action("pick8",  new InputEventKey { PhysicalKeycode = Key.Key8 });
        Action("pick0",  new InputEventKey { PhysicalKeycode = Key.Key0 });
        Action("stats",  new InputEventKey { PhysicalKeycode = Key.Tab });
        Action("ult",    new InputEventKey { PhysicalKeycode = Key.Q });
        Action("ultmenu", new InputEventKey { PhysicalKeycode = Key.U });
        Action("restart", new InputEventKey { PhysicalKeycode = Key.Enter });
        Action("changewitch", new InputEventKey { PhysicalKeycode = Key.C });
        Action("release_mouse", new InputEventKey { PhysicalKeycode = Key.Escape });
    }

    private static Vector3 V3lin(Color c) { var l = c.SrgbToLinear(); return new Vector3(l.R, l.G, l.B); }   // sRGB Color → linear vec3 for the sky shader's uniforms (NEW)

    // (NEW) Per-element projectile material. One parametric spatial shader (see ElementBoltCode) driven by
    // base_color (linear) + elem (= (int)DamageType). Gives each school a distinct animated surface —
    // Ember flickers, Frost facets, Blood pulses veins, Wind swirls, Curse churns, Arcane sparkles, etc.
    // Swap in wherever a bolt/orb wants elemental identity instead of a flat ToonEmissive.
    private static Mesh _cometMesh;   // (NEW) shared tiny billboard-ish sphere + additive material for all comet trails
    private static Mesh CometMesh()
    {
        if (_cometMesh == null)
        {
            var s = new SphereMesh { Radius = 0.14f, Height = 0.28f, RadialSegments = 6, Rings = 3 };
            s.Material = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                BlendMode = BaseMaterial3D.BlendModeEnum.Add,
                VertexColorUseAsAlbedo = true,   // per-particle colour (from the ColorRamp) drives the glow
                AlbedoColor = Colors.White
            };
            _cometMesh = s;
        }
        return _cometMesh;
    }
    // (NEW) a trailing comet tail for a projectile: world-space particles that linger where the bolt was and fade in its element colour.
    public static CpuParticles3D MakeCometTrail(Color tint)
    {
        var cp = new CpuParticles3D
        {
            Amount = System.Math.Max(4, (int)(24 * (Game.I != null ? Game.I.ParticleScale : 1f))),   // (NEW) denser tail; scaled down on lower presets
            Lifetime = 0.28,               // (NEW) shorter → a tight comet near the head, not a long sparse streak
            LocalCoords = false,           // stay in the world as the bolt flies → a trail, not a clump
            Emitting = true,
            Direction = Vector3.Zero,
            Spread = 8f,
            InitialVelocityMin = 0f,
            InitialVelocityMax = 0.6f,
            Gravity = Vector3.Zero,
            ScaleAmountMin = 0.7f,
            ScaleAmountMax = 1.2f,
            Mesh = CometMesh(),
            Color = Colors.White
        };
        var shrink = new Curve();          // (NEW) taper the tail to a fine point → comet shape
        shrink.AddPoint(new Vector2(0f, 1f));
        shrink.AddPoint(new Vector2(1f, 0.08f));
        cp.ScaleAmountCurve = shrink;
        var g = new Gradient();
        g.SetColor(0, new Color(tint.R, tint.G, tint.B, 1.0f));               // head: bright element colour
        g.SetColor(1, new Color(tint.R, tint.G, tint.B, 0.0f));               // tail: fades out
        g.AddPoint(0.5f, new Color(tint.R, tint.G, tint.B, 0.5f));            // (NEW) fuller mid-tail
        cp.ColorRamp = g;
        return cp;
    }

    // (NEW) cool silver-blue additive bloom for the moonlight-blade crescent's outer glow layer. Cached per colour.
    private static readonly System.Collections.Generic.Dictionary<uint, StandardMaterial3D> _crescentGlowCache = new();
    public static StandardMaterial3D CrescentGlowMat(Color tint)
    {
        uint k = tint.ToRgba32();
        if (_crescentGlowCache.TryGetValue(k, out var cached)) return cached;
        var cool = tint.Lerp(new Color(0.55f, 0.68f, 1.0f), 0.5f);   // shift toward cool silver-blue
        var m = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            AlbedoColor = new Color(cool.R, cool.G, cool.B, 0.28f),
            EmissionEnabled = true,
            Emission = cool,
            EmissionEnergyMultiplier = 1.6f
        };
        _crescentGlowCache[k] = m;
        return m;
    }

    // (NEW) a razor-thin flat crescent blade — a curved sliver in the local XY plane that tapers to sharp points at both
    // tips. Built once, shared. Scaled/oriented by the bolt. cull_disabled material so winding doesn't matter.
    private static Mesh _crescentBlade;
    public static Mesh CrescentBladeMesh()
    {
        if (_crescentBlade != null) return _crescentBlade;
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        int N = 48;
        float span = Mathf.DegToRad(200f);
        float R = 1.0f;
        float maxW = 0.09f;   // razor-thin half-width at the belly; tapers to 0 at the tips (sharp)
        for (int i = 0; i < N; i++)
        {
            float ta = i / (float)N, tb = (i + 1) / (float)N;
            float aa = -span / 2f + span * ta; var da = new Vector2(Mathf.Cos(aa), Mathf.Sin(aa));
            var spa = da * R; float wa = maxW * Mathf.Sin(Mathf.Pi * ta);
            var ia = new Vector3(spa.X - da.X * wa, spa.Y - da.Y * wa, 0f);
            var oa = new Vector3(spa.X + da.X * wa, spa.Y + da.Y * wa, 0f);
            float ab = -span / 2f + span * tb; var db = new Vector2(Mathf.Cos(ab), Mathf.Sin(ab));
            var spb = db * R; float wb = maxW * Mathf.Sin(Mathf.Pi * tb);
            var ib = new Vector3(spb.X - db.X * wb, spb.Y - db.Y * wb, 0f);
            var ob = new Vector3(spb.X + db.X * wb, spb.Y + db.Y * wb, 0f);
            st.AddVertex(ia); st.AddVertex(oa); st.AddVertex(ob);
            st.AddVertex(ia); st.AddVertex(ob); st.AddVertex(ib);
        }
        _crescentBlade = st.Commit();
        return _crescentBlade;
    }

    // (NEW) ghostly-white additive glow for the crescent blade. Cached per colour.
    private static readonly System.Collections.Generic.Dictionary<uint, StandardMaterial3D> _crescentBladeCache = new();
    public static StandardMaterial3D CrescentBladeMat(Color tint)
    {
        uint k = tint.ToRgba32();
        if (_crescentBladeCache.TryGetValue(k, out var cached)) return cached;
        var white = tint.Lerp(new Color(1f, 1f, 1f), 0.7f);   // bright moon-white (was a dim bluish additive ghost)
        var m = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,   // Mix blend, not additive → reads as solid bright white against the sky
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            AlbedoColor = new Color(white.R, white.G, white.B, 0.96f),
            EmissionEnabled = true,
            Emission = white,
            EmissionEnergyMultiplier = 4.2f
        };
        _crescentBladeCache[k] = m;
        return m;
    }

    // (NEW) Holy descending-ray dressing (cosmetic). A warm translucent light beam + a flat ground-scorch that
    // flickers like a candle. Beam material + scorch shader/mesh are all shared/cached.
    private static StandardMaterial3D _holyRayMat;
    public static StandardMaterial3D HolyRayMat()
    {
        if (_holyRayMat != null) return _holyRayMat;
        _holyRayMat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            AlbedoColor = new Color(1f, 0.93f, 0.68f, 0.42f),
            EmissionEnabled = true,
            Emission = new Color(1f, 0.9f, 0.6f),
            EmissionEnergyMultiplier = 3.6f
        };
        return _holyRayMat;
    }
    private static Mesh _holyScorchMesh;
    public static Mesh HolyScorchMesh() { _holyScorchMesh ??= new QuadMesh { Size = new Vector2(1.4f, 1.4f) }; return _holyScorchMesh; }
    private static ShaderMaterial _holyScorchMat;
    public static ShaderMaterial HolyScorchMat()
    {
        _holyScorchMat ??= new ShaderMaterial { Shader = new Shader { Code = HolyScorchCode } };
        return _holyScorchMat;
    }

    // (NEW) radial glow texture + a Decal-based scorch mark. A Decal PROJECTS onto the terrain surface, so it conforms
    // perfectly and never clips through hills (unlike a flat quad sitting on the ground).
    private static Texture2D _scorchTex;
    public static Texture2D ScorchTex()
    {
        if (_scorchTex != null) return _scorchTex;
        var grad = new Gradient();
        grad.SetColor(0, new Color(1f, 1f, 1f, 1f));   // bright centre
        grad.SetColor(1, new Color(0f, 0f, 0f, 0f));   // (FIX) RGB→black at the transparent edge so decal EMISSION doesn't paint the whole square
        _scorchTex = new GradientTexture2D
        {
            Gradient = grad, Width = 64, Height = 64,
            Fill = GradientTexture2D.FillEnum.Radial,
            FillFrom = new Vector2(0.5f, 0.5f), FillTo = new Vector2(0.5f, 0f)
        };
        return _scorchTex;
    }
    // (NEW) a filled-disc-with-rim radial texture for ground fields/AoEs, projected via a Decal so they conform
    // to hilly terrain instead of clipping through it (the same approach as the Holy right-click's ground strip).
    private static Texture2D _fieldTex;
    public static Texture2D FieldTex()
    {
        if (_fieldTex != null) return _fieldTex;
        var grad = new Gradient();
        grad.SetColor(0, new Color(0.5f, 0.5f, 0.5f, 0.5f));    // filled centre (RGB premultiplied by alpha)
        grad.SetColor(1, new Color(0f, 0f, 0f, 0f));            // (FIX) RGB→black at the transparent outer edge → no glowing square
        grad.AddPoint(0.82f, new Color(0.55f, 0.55f, 0.55f, 0.55f));
        grad.AddPoint(0.93f, new Color(1f, 1f, 1f, 1f));        // bright rim
        _fieldTex = new GradientTexture2D
        {
            Gradient = grad, Width = 96, Height = 96,
            Fill = GradientTexture2D.FillEnum.Radial,
            FillFrom = new Vector2(0.5f, 0.5f), FillTo = new Vector2(0.5f, 0f)
        };
        return _fieldTex;
    }

    // (NEW) a procedural crescent shape (big disc minus an offset disc) for Lunar impact marks — white moonlight.
    private static Texture2D _crescentImpactTex;
    private static Texture2D CrescentImpactTex()
    {
        if (_crescentImpactTex != null) return _crescentImpactTex;
        int N = 64;
        var img = Image.CreateEmpty(N, N, false, Image.Format.Rgba8);
        Vector2 cA = new Vector2(N * 0.5f, N * 0.5f); float rA = N * 0.42f;   // main disc
        Vector2 cB = new Vector2(N * 0.66f, N * 0.5f); float rB = N * 0.40f;  // subtracted disc (offset) → crescent
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f);
                float inA = Mathf.Clamp((rA - p.DistanceTo(cA)) / 2f + 0.5f, 0f, 1f);   // soft: inside disc A
                float outB = Mathf.Clamp((p.DistanceTo(cB) - rB) / 2f + 0.5f, 0f, 1f);  // soft: outside disc B
                img.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp(inA * outB, 0f, 1f)));
            }
        _crescentImpactTex = ImageTexture.CreateFromImage(img);
        return _crescentImpactTex;
    }

    // (NEW) impact marks for bolts/primaries: a decal (projects onto terrain, never clips) sized to the projectile
    // and coloured by its damage type — Lunar leaves a white crescent, everything else a type-tinted glow. They
    // fade gently and are hard-capped so rapid fire can't bog the machine down.
    private readonly System.Collections.Generic.List<Decal> _impactDecals = new();
    public void SpawnImpactDecal(Vector3 pos, DamageType dt, float projRadius, bool crescent)
    {
        if (State != GameState.Playing) return;
        _impactDecals.RemoveAll(x => x == null || !GodotObject.IsInstanceValid(x));
        const int MAX = 28;   // hard ceiling
        int cap = System.Math.Min(MAX, ImpactDecalCap);   // (NEW) lower presets keep fewer marks
        while (_impactDecals.Count >= cap) { var old = _impactDecals[0]; _impactDecals.RemoveAt(0); if (GodotObject.IsInstanceValid(old)) old.QueueFree(); }

        var col = crescent ? DamageTypes.Col(DamageType.Lunar).Lerp(Colors.White, 0.6f) : DamageTypes.Col(dt);
        var tex = crescent ? CrescentImpactTex() : ScorchTex();
        float sz = Mathf.Clamp(0.9f + projRadius * 3.5f, 0.8f, 6f);   // proportional to the projectile
        float gy = SurfaceHeight(pos, 1e9f);
        var d = new Decal
        {
            TextureAlbedo = tex, TextureEmission = tex,
            EmissionEnergy = 2.8f,
            Modulate = new Color(col.R, col.G, col.B, 0.95f),
            Size = new Vector3(sz, Mathf.Max(3f, sz), sz)
        };
        AddChild(d);
        d.GlobalPosition = new Vector3(pos.X, gy + sz * 0.4f, pos.Z);   // straddles ground → projects down onto the terrain
        d.RotationDegrees = new Vector3(0, (float)GD.RandRange(0, 360), 0);
        _impactDecals.Add(d);
        var tw = d.CreateTween();
        tw.TweenInterval(0.3f);            // hold briefly
        tw.SetParallel(true);
        tw.TweenProperty(d, "modulate:a", 0f, 0.55f);       // then fade gently
        tw.TweenProperty(d, "emission_energy", 0f, 0.55f);
        tw.SetParallel(false);
        tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(d)) d.QueueFree(); }));
    }
    // ---- (NEW) SURFACE IMPACT MARKS ------------------------------------------------------------------
    // Bolts/primaries leave a mark ON the surface they actually strike — parented to enemies (so it rides them),
    // or placed on structures/trees/ground — oriented to that surface's normal. The mark is an oriented QUAD
    // (not a downward decal), so it sits on walls/enemies too, and its SHAPE comes from a grayscale-premultiplied
    // mask (RGB fades to black with alpha) so the glow is the shape itself — never a glowing box.
    private static float SegDist(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a; float t = ab.LengthSquared() > 1e-6f ? Mathf.Clamp((p - a).Dot(ab) / ab.LengthSquared(), 0f, 1f) : 0f;
        return (p - (a + ab * t)).Length();
    }
    private static Texture2D _mDisc, _mCres, _mNeedle, _mPunch, _mSlash;
    private static Texture2D MaskTex(int shape)
    {
        switch (shape) { case 1: if (_mCres != null) return _mCres; break; case 2: if (_mNeedle != null) return _mNeedle; break; case 3: if (_mPunch != null) return _mPunch; break; case 4: if (_mSlash != null) return _mSlash; break; default: if (_mDisc != null) return _mDisc; break; }
        int N = 96;
        var img = Image.CreateEmpty(N, N, false, Image.Format.Rgba8);
        Vector2[] ndots = { new(0.45f, 0.15f), new(-0.4f, 0.3f), new(0.2f, -0.5f), new(-0.25f, -0.4f), new(0.55f, -0.2f), new(-0.55f, -0.05f), new(0.05f, 0.55f) };
        float[] ndr = { 0.16f, 0.13f, 0.15f, 0.12f, 0.10f, 0.12f, 0.11f };
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                float nx = (x + 0.5f) / N * 2f - 1f, ny = (y + 0.5f) / N * 2f - 1f;
                var p = new Vector2(nx, ny);
                float r = p.Length(), v = 0f;
                switch (shape)
                {
                    case 1: // crescent (Lunar)
                        {
                            float dA = (p - new Vector2(-0.06f, 0f)).Length(), dB = (p - new Vector2(0.30f, 0f)).Length();
                            v = Mathf.Clamp((0.82f - dA) / 0.10f, 0f, 1f) * Mathf.Clamp((dB - 0.72f) / 0.10f, 0f, 1f);
                            break;
                        }
                    case 2: // Nature: poison splatter + needle punctures
                        {
                            v = Mathf.Clamp((0.34f - r) / 0.18f, 0f, 1f);
                            for (int i = 0; i < ndots.Length; i++) v = Mathf.Max(v, Mathf.Clamp((ndr[i] - (p - ndots[i]).Length()) / 0.06f, 0f, 1f));
                            for (int k = 0; k < 3; k++) { float a = k * 2.09f; var tip = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * 0.62f; v = Mathf.Max(v, Mathf.Clamp((0.05f - SegDist(p, Vector2.Zero, tip)) / 0.03f, 0f, 1f) * 0.9f); }
                            break;
                        }
                    case 3: // Wind: punch impact — bright contact core + short UNEVEN radial blow-streaks (no ring → not a ship's wheel)
                        {
                            v = Mathf.Clamp((0.24f - r) / 0.15f, 0f, 1f);   // bright fist-contact core
                            float[] sa = { 0.35f, 1.25f, 2.15f, 3.05f, 4.0f, 5.05f };
                            float[] sl = { 0.74f, 0.55f, 0.82f, 0.6f, 0.78f, 0.5f };
                            for (int i = 0; i < sa.Length; i++)
                            {
                                var tip = new Vector2(Mathf.Cos(sa[i]), Mathf.Sin(sa[i])) * sl[i];
                                float taper = Mathf.Clamp(1f - r / sl[i], 0f, 1f);   // brightest near the core, fading to the tip
                                v = Mathf.Max(v, Mathf.Clamp((0.055f - SegDist(p, Vector2.Zero, tip)) / 0.045f, 0f, 1f) * (0.45f + 0.55f * taper));
                            }
                            break;
                        }
                    case 4: // Blood: crimson claw slash — 3 diagonal streaks
                        {
                            for (int k = -1; k <= 1; k++)
                            {
                                var off = new Vector2(0.16f, -0.16f) * k;
                                float d = SegDist(p, new Vector2(-0.7f, 0.7f) + off, new Vector2(0.7f, -0.7f) + off);
                                v = Mathf.Max(v, Mathf.Clamp((0.06f - d) / 0.05f, 0f, 1f) * Mathf.Clamp((0.85f - r) / 0.3f, 0f, 1f));
                            }
                            break;
                        }
                    default: // disc (round bolts / everything else)
                        v = Mathf.Clamp((0.9f - r) / 0.5f, 0f, 1f); v *= v;
                        break;
                }
                v = Mathf.Clamp(v, 0f, 1f);
                img.SetPixel(x, y, new Color(v, v, v, v));   // grayscale PREMULTIPLIED → tinted by material, shape = glow
            }
        var tex = ImageTexture.CreateFromImage(img);
        switch (shape) { case 1: _mCres = tex; break; case 2: _mNeedle = tex; break; case 3: _mPunch = tex; break; case 4: _mSlash = tex; break; default: _mDisc = tex; break; }
        return tex;
    }

    private readonly System.Collections.Generic.List<Node3D> _impactMarks = new();
    public void SpawnImpactMark(Vector3 hitPos, Vector3 normal, Node3D attachTo, DamageType dt, float projRadius, float roll = float.NaN)
    {
        if (State != GameState.Playing) return;
        _impactMarks.RemoveAll(x => x == null || !GodotObject.IsInstanceValid(x));
        int cap = GfxQuality == 0 ? 18 : GfxQuality == 1 ? 30 : 44;   // quads are cheap; hold a full area-attack spread
        while (_impactMarks.Count >= cap) { var oldm = _impactMarks[0]; _impactMarks.RemoveAt(0); if (GodotObject.IsInstanceValid(oldm)) oldm.QueueFree(); }

        int shape; Color tint;
        switch (dt)
        {
            case DamageType.Lunar: shape = 1; tint = DamageTypes.Col(DamageType.Lunar).Lerp(Colors.White, 0.7f); break;   // white moonlight crescent
            case DamageType.Nature: shape = 2; tint = DamageTypes.Col(DamageType.Nature); break;                          // green poison splatter
            case DamageType.Wind: shape = 3; tint = DamageTypes.Col(DamageType.Wind); break;                              // wind punch
            case DamageType.Blood: shape = 4; tint = new Color(0.86f, 0.05f, 0.12f); break;                               // crimson slash
            default: shape = 0; tint = DamageTypes.Col(dt); break;                                                        // round type-tinted glow
        }
        var n = normal.LengthSquared() > 1e-6f ? normal.Normalized() : Vector3.Up;
        float sz = Mathf.Clamp(0.55f + projRadius * 2.6f, 0.5f, 4f);
        var tex = MaskTex(shape);
        var mat = new StandardMaterial3D
        {
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            AlbedoTexture = tex, AlbedoColor = tint,
            EmissionEnabled = true, EmissionTexture = tex, Emission = tint, EmissionEnergyMultiplier = 3.5f
        };
        var mi = new MeshInstance3D { Mesh = new QuadMesh { Size = new Vector2(sz, sz) }, MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        (attachTo ?? this).AddChild(mi);
        mi.GlobalPosition = hitPos + n * 0.03f;   // lift slightly off the surface (anti z-fight)
        var up = Mathf.Abs(n.Y) > 0.9f ? Vector3.Forward : Vector3.Up;
        mi.LookAt(mi.GlobalPosition - n, up);      // quad face aligns to the surface normal (works on walls, enemies, ground)
        mi.RotateObjectLocal(Vector3.Back, float.IsNaN(roll) ? (float)GD.RandRange(0.0, Mathf.Tau) : roll);   // in-plane orientation (matched to the cast VFX when a roll is passed)
        _impactMarks.Add(mi);

        var tw = mi.CreateTween();
        tw.TweenInterval(0.35f);
        tw.SetParallel(true);
        tw.TweenProperty(mat, "albedo_color", new Color(tint.R, tint.G, tint.B, 0f), 0.6f);
        tw.TweenProperty(mat, "emission_energy_multiplier", 0f, 0.6f);
        tw.SetParallel(false);
        tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(mi)) mi.QueueFree(); }));
    }

    public static Decal MakeHolyScorch()
    {
        var t = ScorchTex();
        return new Decal
        {
            TextureAlbedo = t,
            TextureEmission = t,
            EmissionEnergy = 3.2f,
            Modulate = new Color(1f, 0.9f, 0.6f, 1f),
            Size = new Vector3(1.4f, 4f, 1.4f)
        };
    }
    private const string HolyScorchCode = @"
shader_type spatial;
render_mode blend_add, unshaded, cull_disabled, depth_draw_never;
varying float vseed;
void vertex(){ vseed = MODEL_MATRIX[3].x * 1.7 + MODEL_MATRIX[3].z * 2.3; }   // per-instance phase from world position
void fragment(){
    vec2 p = UV - vec2(0.5);
    float r = length(p) * 2.0;
    float glow = 1.0 - smoothstep(0.0, 1.0, r);                                              // bright centre → transparent edge
    float flick = 0.72 + 0.28 * (sin(TIME * 11.0 + vseed) * 0.5 + sin(TIME * 6.7 + vseed * 1.7) * 0.5);   // candle flicker
    vec3 warm = vec3(1.0, 0.92, 0.62);
    ALBEDO = warm;
    EMISSION = warm * glow * (1.0 + 2.2 * flick);
    ALPHA = glow;
}
";

    private static Shader _elementShader;   // (NEW) compiled ONCE and shared — recompiling per call tanked FPS (esp. the 6-fist wind primary)
    private static readonly System.Collections.Generic.Dictionary<(int, uint), ShaderMaterial> _elementMatCache = new();   // (NEW) share one material per (element, colour) across ALL callers — bolts/needles/holy/wood/lunar/wind/blood. Safe because uniforms are set once and never mutated; per-instance look (scale/transparency) lives on the MeshInstance, not the material.
    public static ShaderMaterial ElementBoltMat(Color tint, DamageType dt)
    {
        _elementShader ??= new Shader { Code = ElementBoltCode };
        var key = ((int)dt, tint.ToRgba32());
        if (_elementMatCache.TryGetValue(key, out var cached)) return cached;
        var m = new ShaderMaterial { Shader = _elementShader };
        m.SetShaderParameter("base_color", V3lin(tint));
        m.SetShaderParameter("elem", (int)dt);
        _elementMatCache[key] = m;
        return m;
    }

    // (NEW) Crimson charged right-click visuals — a dark blood-orb core with a hot fresnel rim, and glowing
    // ritual SIGIL rings (procedural magic-circle: rings, rotating ticks, rune glyphs). Cached like ElementBoltMat.
    private static Shader _bloodOrbShader; private static ShaderMaterial _bloodOrbMat;
    public static ShaderMaterial BloodOrbMat()
    {
        _bloodOrbShader ??= new Shader { Code = BloodOrbCode };
        if (_bloodOrbMat == null) { _bloodOrbMat = new ShaderMaterial { Shader = _bloodOrbShader }; _bloodOrbMat.SetShaderParameter("col", V3lin(DamageTypes.Col(DamageType.Blood))); }
        return _bloodOrbMat;
    }
    private static Shader _sigilShader;
    private static readonly System.Collections.Generic.Dictionary<uint, ShaderMaterial> _sigilCache = new();
    public static ShaderMaterial SigilMat(Color tint)
    {
        _sigilShader ??= new Shader { Code = SigilCode };
        uint k = tint.ToRgba32();
        if (_sigilCache.TryGetValue(k, out var m)) return m;
        m = new ShaderMaterial { Shader = _sigilShader };
        m.SetShaderParameter("col", V3lin(tint));
        _sigilCache[k] = m;
        return m;
    }
    // (NEW) Full-charge ritual flourish shared by EVERY witch's charged right-click: a flat ground rune-circle in
    // the witch's element colour that flares open across `radius` then fades. Centralised so the theming stays uniform.
    public void SpawnGroundSigil(Vector3 center, float radius, Color tint, bool net = true)
    {
        if (net) NetMgr?.BroadcastVfx(16, center, Vector3.Zero, radius, 0f, tint);
        var sig = new MeshInstance3D { Mesh = new QuadMesh { Size = new Vector2(1f, 1f) }, MaterialOverride = SigilMat(tint) };
        AddChild(sig);
        float gy = SurfaceHeight(center, center.Y);
        sig.GlobalPosition = new Vector3(center.X, gy + 0.05f, center.Z);
        sig.RotationDegrees = new Vector3(-90f, (float)GD.RandRange(0, 360), 0);   // lie flat on the ground
        sig.Scale = new Vector3(0.2f, 0.2f, 0.2f);
        var t = sig.CreateTween(); t.SetParallel(true);
        t.TweenProperty(sig, "scale", new Vector3(radius * 2f, radius * 2f, radius * 2f), 0.26f).SetEase(Tween.EaseType.Out);
        t.TweenProperty(sig, "transparency", 1f, 0.52f).SetDelay(0.08f);
        t.SetParallel(false);
        t.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(sig)) sig.QueueFree(); }));
    }

    // (NEW) Verdant thorn impact: a cluster of brambles/roots erupting from the ground at `at`, rising with a
    // little overshoot then withering back down. Count scales down on lower graphics presets.
    public void SpawnBrambleBurst(Vector3 at, float scale, int count, bool net = true)
    {
        if (net) NetMgr?.BroadcastVfx(19, at, Vector3.Zero, scale, count, Colors.White);
        count = Mathf.Max(2, (int)(count * ParticleScale));
        var mat = ToonEmissive(new Color(0.34f, 0.5f, 0.2f), 0.7f, 0.03f);   // mossy living-wood with a faint glow
        for (int i = 0; i < count; i++)
        {
            float a = i / (float)count * Mathf.Tau + GD.Randf() * 0.6f;
            var dir = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
            float off = (0.2f + GD.Randf() * 0.5f) * scale;
            float h = (0.8f + GD.Randf() * 0.7f) * scale;
            var br = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.015f * scale, BottomRadius = 0.07f * scale, Height = h }, MaterialOverride = mat };
            AddChild(br);
            float gy = SurfaceHeight(at + dir * off, at.Y);
            var groundPos = new Vector3(at.X + dir.X * off, gy + h * 0.35f, at.Z + dir.Z * off);
            br.RotationDegrees = new Vector3((GD.Randf() - 0.5f) * 46f, GD.Randf() * 360f, (GD.Randf() - 0.5f) * 46f);   // wild bramble tilt
            br.GlobalPosition = groundPos - new Vector3(0, h * 0.8f, 0);   // start mostly buried, then erupt
            br.Scale = new Vector3(1f, 0.4f, 1f);
            var tw = br.CreateTween(); tw.SetParallel(true);
            tw.TweenProperty(br, "global_position", groundPos, 0.16f).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Back);
            tw.TweenProperty(br, "scale", new Vector3(1f, 1f, 1f), 0.16f).SetEase(Tween.EaseType.Out);
            tw.SetParallel(false);
            tw.TweenInterval(0.55f);
            tw.TweenProperty(br, "scale", new Vector3(0.02f, 0.02f, 0.02f), 0.28f).SetEase(Tween.EaseType.In);   // wither
            tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(br)) br.QueueFree(); }));
        }
    }

    // (NEW) A LINGERING ground sigil: flares open, then stays as a magic circle for `life` seconds before slowly
    // fading. The AoE right-clicks (Crimson, Gale) leave one of these where the blast landed.
    public void SpawnGroundSigilLinger(Vector3 center, float radius, Color tint, float life, bool net = true)
    {
        if (net) NetMgr?.BroadcastVfx(17, center, Vector3.Zero, radius, life, tint);
        var sig = new MeshInstance3D { Mesh = new QuadMesh { Size = new Vector2(1f, 1f) }, MaterialOverride = SigilMat(tint) };
        AddChild(sig);
        float gy = SurfaceHeight(center, center.Y);
        sig.GlobalPosition = new Vector3(center.X, gy + 0.04f, center.Z);
        sig.RotationDegrees = new Vector3(-90f, (float)GD.RandRange(0, 360), 0);   // lie flat on the ground
        sig.Scale = new Vector3(0.2f, 0.2f, 0.2f);
        var t = sig.CreateTween();
        t.TweenProperty(sig, "scale", new Vector3(radius * 2f, radius * 2f, radius * 2f), 0.26f).SetEase(Tween.EaseType.Out);   // flare open
        t.TweenInterval(life);                                                                                                 // linger
        t.TweenProperty(sig, "transparency", 1f, 1.2f);                                                                        // slow fade
        t.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(sig)) sig.QueueFree(); }));
    }

    // (NEW) A lingering PATCH of brambles scattered over a disc — the Verdant thorn's full-charge right-click leaves
    // this around the caster. They rise in (staggered), hold for `life`, then wither. Count scales with the preset.
    public void SpawnBramblePatch(Vector3 center, float radius, float life, bool net = true)
    {
        if (net) NetMgr?.BroadcastVfx(18, center, Vector3.Zero, radius, life, Colors.White);
        int count = Mathf.Max(6, (int)(14 * ParticleScale));
        var mat = ToonEmissive(new Color(0.34f, 0.5f, 0.2f), 0.7f, 0.03f);   // mossy living-wood, faint glow
        for (int i = 0; i < count; i++)
        {
            float a = GD.Randf() * Mathf.Tau;
            float rr = Mathf.Sqrt(GD.Randf()) * radius;   // roughly uniform coverage over the disc
            var pos = new Vector3(center.X + Mathf.Cos(a) * rr, 0f, center.Z + Mathf.Sin(a) * rr);
            float h = 0.5f + GD.Randf() * 0.8f;
            var br = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.015f, BottomRadius = 0.06f, Height = h }, MaterialOverride = mat };
            AddChild(br);
            float gy = SurfaceHeight(pos, center.Y);
            var groundPos = new Vector3(pos.X, gy + h * 0.35f, pos.Z);
            br.RotationDegrees = new Vector3((GD.Randf() - 0.5f) * 46f, GD.Randf() * 360f, (GD.Randf() - 0.5f) * 46f);
            br.GlobalPosition = groundPos - new Vector3(0, h * 0.8f, 0);   // start buried, then erupt
            br.Scale = new Vector3(1f, 0.4f, 1f);
            float delay = GD.Randf() * 0.18f;
            var tw = br.CreateTween(); tw.SetParallel(true);
            tw.TweenProperty(br, "global_position", groundPos, 0.2f).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Back).SetDelay(delay);
            tw.TweenProperty(br, "scale", new Vector3(1f, 1f, 1f), 0.2f).SetEase(Tween.EaseType.Out).SetDelay(delay);
            tw.SetParallel(false);
            tw.TweenInterval(life);
            tw.TweenProperty(br, "scale", new Vector3(0.02f, 0.02f, 0.02f), 0.5f).SetEase(Tween.EaseType.In);   // wither
            tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(br)) br.QueueFree(); }));
        }
    }

    // (NEW) A ring of sharp spikes/shards erupting from the ground over a disc, holding `life`, then withering.
    // Shared by Frost Veil (pale ice shards) and Sanguine Spikes (crimson blood spikes).
    public void SpawnGroundSpikes(Vector3 center, float radius, int count, Color col, float life, bool net = true)
    {
        if (net) NetMgr?.BroadcastVfx(20, center, new Vector3(count, 0f, 0f), radius, life, col);
        count = Mathf.Max(4, (int)(count * ParticleScale));
        var mat = ToonEmissive(col.Lerp(Colors.White, 0.35f), 1.5f, 0.02f);
        for (int i = 0; i < count; i++)
        {
            float a = GD.Randf() * Mathf.Tau;
            float rr = Mathf.Sqrt(GD.Randf()) * radius;
            var pos = new Vector3(center.X + Mathf.Cos(a) * rr, 0f, center.Z + Mathf.Sin(a) * rr);
            float h = 0.6f + GD.Randf() * 0.9f;
            var sp = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.13f, Height = h, RadialSegments = 5 }, MaterialOverride = mat };
            AddChild(sp);
            float gy = SurfaceHeight(pos, center.Y);
            var groundPos = new Vector3(pos.X, gy + h * 0.4f, pos.Z);
            sp.RotationDegrees = new Vector3((GD.Randf() - 0.5f) * 34f, GD.Randf() * 360f, (GD.Randf() - 0.5f) * 34f);
            sp.GlobalPosition = groundPos - new Vector3(0, h * 0.85f, 0);   // start buried, then erupt
            sp.Scale = new Vector3(0.5f, 0.3f, 0.5f);
            float delay = GD.Randf() * 0.1f;
            var tw = sp.CreateTween(); tw.SetParallel(true);
            tw.TweenProperty(sp, "global_position", groundPos, 0.12f).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Back).SetDelay(delay);
            tw.TweenProperty(sp, "scale", new Vector3(1f, 1f, 1f), 0.12f).SetEase(Tween.EaseType.Out).SetDelay(delay);
            tw.SetParallel(false);
            tw.TweenInterval(life);
            tw.TweenProperty(sp, "scale", new Vector3(0.02f, 0.02f, 0.02f), 0.35f).SetEase(Tween.EaseType.In);   // wither
            tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(sp)) sp.QueueFree(); }));
        }
    }

    // (NEW) Sunder Burst: a fiery ember explosion — an animated fire-shader ball that swells and collapses, with
    // embers flung outward that arc and fall. (uses the cached Ember bolt shader so it churns like fire)
    public void SpawnEmberBurst(Vector3 center, float radius, bool net = true)
    {
        if (net) NetMgr?.BroadcastVfx(21, center, Vector3.Zero, radius, 0f, Colors.White);
        var col = DamageTypes.Col(DamageType.Ember);
        float gy = SurfaceHeight(center, center.Y);
        var ball = new MeshInstance3D { Mesh = new SphereMesh { Radius = 1f, Height = 2f }, MaterialOverride = ElementBoltMat(col, DamageType.Ember) };
        AddChild(ball);
        ball.GlobalPosition = new Vector3(center.X, gy + 0.6f, center.Z);
        ball.Scale = new Vector3(0.3f, 0.3f, 0.3f);
        ball.AddChild(new OmniLight3D { OmniRange = radius * 1.4f, LightColor = col, LightEnergy = 3f });
        var bt = ball.CreateTween();
        bt.TweenProperty(ball, "scale", Vector3.One * (radius * 0.45f), 0.16f).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Back);
        bt.TweenProperty(ball, "scale", new Vector3(0.02f, 0.02f, 0.02f), 0.24f).SetEase(Tween.EaseType.In);
        bt.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(ball)) ball.QueueFree(); }));
        int emb = Mathf.Max(6, (int)(14 * ParticleScale));
        var emat = ToonEmissive(col.Lerp(Colors.White, 0.3f), 3f, 0f);
        for (int i = 0; i < emb; i++)
        {
            float a = GD.Randf() * Mathf.Tau;
            var dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
            var em = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.08f, Height = 0.16f }, MaterialOverride = emat };
            AddChild(em);
            var start = new Vector3(center.X, gy + 0.5f, center.Z);
            em.GlobalPosition = start;
            float dist = (0.4f + GD.Randf()) * radius;
            var apex = start + dir * dist * 0.6f + new Vector3(0f, 1.5f + GD.Randf() * 1.5f, 0f);
            var land = new Vector3(start.X + dir.X * dist, SurfaceHeight(start + dir * dist, gy) + 0.05f, start.Z + dir.Z * dist);
            var et = em.CreateTween();
            et.TweenProperty(em, "global_position", apex, 0.18f).SetEase(Tween.EaseType.Out);
            et.TweenProperty(em, "global_position", land, 0.34f).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
            et.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(em)) em.QueueFree(); }));
        }
    }

    // (NEW) Hemorrhage: a burst of translucent blood mist puffs that swell and rise, plus droplets flung outward.
    public void SpawnBloodMist(Vector3 center, float radius, bool net = true)
    {
        if (net) NetMgr?.BroadcastVfx(22, center, Vector3.Zero, radius, 0f, Colors.White);
        var col = DamageTypes.Col(DamageType.Blood);
        float gy = SurfaceHeight(center, center.Y);
        int puffs = Mathf.Max(4, (int)(8 * ParticleScale));
        var pmat = ToonEmissive(col, 1.1f, 0f);
        if (pmat is StandardMaterial3D psm) { psm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; psm.AlbedoColor = new Color(col.R, col.G, col.B, 0.5f); }
        for (int i = 0; i < puffs; i++)
        {
            float a = GD.Randf() * Mathf.Tau;
            float rr = GD.Randf() * radius * 0.6f;
            var puff = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.6f, Height = 1.2f }, MaterialOverride = pmat };
            AddChild(puff);
            var p0 = new Vector3(center.X + Mathf.Cos(a) * rr, gy + 0.5f, center.Z + Mathf.Sin(a) * rr);
            puff.GlobalPosition = p0;
            puff.Scale = new Vector3(0.3f, 0.3f, 0.3f);
            var pt = puff.CreateTween(); pt.SetParallel(true);
            pt.TweenProperty(puff, "scale", Vector3.One * (0.8f + GD.Randf() * 0.6f), 0.5f).SetEase(Tween.EaseType.Out);
            pt.TweenProperty(puff, "global_position", p0 + new Vector3((GD.Randf() - 0.5f) * 1.2f, 0.9f, (GD.Randf() - 0.5f) * 1.2f), 0.6f);
            pt.TweenProperty(puff, "transparency", 1f, 0.6f);
            pt.SetParallel(false);
            pt.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(puff)) puff.QueueFree(); }));
        }
        int drops = Mathf.Max(6, (int)(12 * ParticleScale));
        var dmat = ToonEmissive(col.Darkened(0.1f), 1f, 0f);
        for (int i = 0; i < drops; i++)
        {
            float a = GD.Randf() * Mathf.Tau;
            var dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
            var dp = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.06f, Height = 0.12f }, MaterialOverride = dmat };
            AddChild(dp);
            var start = new Vector3(center.X, gy + 0.5f, center.Z);
            dp.GlobalPosition = start;
            float dist = (0.4f + GD.Randf()) * radius;
            var apex = start + dir * dist * 0.6f + new Vector3(0f, 1f + GD.Randf() * 1.2f, 0f);
            var land = new Vector3(start.X + dir.X * dist, SurfaceHeight(start + dir * dist, gy) + 0.05f, start.Z + dir.Z * dist);
            var dt2 = dp.CreateTween();
            dt2.TweenProperty(dp, "global_position", apex, 0.16f).SetEase(Tween.EaseType.Out);
            dt2.TweenProperty(dp, "global_position", land, 0.3f).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
            dt2.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(dp)) dp.QueueFree(); }));
        }
    }

    // (NEW) A shaft/pillar of light that snaps into being at `at`, holds `life`, then fades. Used by Moonwell Beam
    // (moonlight), Consecrated Ground, and Smite (holy strike).
    public void SpawnLightPillar(Vector3 at, Color col, float radius, float height, float life, bool net = true)
    {
        if (net) NetMgr?.BroadcastVfx(23, at, new Vector3(life, 0f, 0f), radius, height, col);
        float gy = SurfaceHeight(at, at.Y);
        var mat = ToonEmissive(col, 2.6f, 0f);
        if (mat is StandardMaterial3D psm) { psm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; psm.AlbedoColor = new Color(col.R, col.G, col.B, 0.4f); }
        var pillar = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = radius * 0.55f, BottomRadius = radius, Height = height }, MaterialOverride = mat };
        AddChild(pillar);
        pillar.GlobalPosition = new Vector3(at.X, gy + height * 0.5f, at.Z);
        pillar.Scale = new Vector3(0.2f, 1f, 0.2f);
        pillar.AddChild(new OmniLight3D { OmniRange = radius * 3f, LightColor = col, LightEnergy = 3f });
        var t = pillar.CreateTween(); t.SetParallel(true);
        t.TweenProperty(pillar, "scale", new Vector3(1f, 1f, 1f), 0.12f).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Back);
        t.SetParallel(false);
        t.TweenInterval(life);
        t.TweenProperty(pillar, "transparency", 1f, 0.35f);
        t.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(pillar)) pillar.QueueFree(); }));
    }

    // (NEW) A column of air that swirls upward — the Updraft finisher's look. A translucent wind cylinder
    // with helical sheets spinning around it as they rise, then it collapses away. Purely cosmetic.
    public void SpawnAirColumn(Vector3 center, float radius, float height, float life, bool net = true)
    {
        if (net) NetMgr?.BroadcastVfx(24, center, new Vector3(life, 0f, 0f), radius, height, Colors.White);
        float gy = SurfaceHeight(center, center.Y);
        var col = DamageTypes.Col(DamageType.Wind);
        var root = new Node3D(); AddChild(root);
        root.GlobalPosition = new Vector3(center.X, gy, center.Z);

        var colMat = ToonEmissive(col, 0.8f, 0f);
        if (colMat is StandardMaterial3D csm) { csm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; csm.AlbedoColor = new Color(col.R, col.G, col.B, 0.12f); csm.CullMode = BaseMaterial3D.CullModeEnum.Disabled; }
        var core = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = radius * 1.1f, BottomRadius = radius * 0.5f, Height = height, RadialSegments = 24 }, MaterialOverride = colMat };
        core.Position = new Vector3(0, height * 0.5f, 0);
        root.AddChild(core);

        var spin = new Node3D(); root.AddChild(spin);
        var sheetMat = ToonEmissive(col, 1.6f, 0f);
        if (sheetMat is StandardMaterial3D ssm) { ssm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; ssm.AlbedoColor = new Color(col.R, col.G, col.B, 0.3f); ssm.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded; ssm.CullMode = BaseMaterial3D.CullModeEnum.Disabled; }
        int sheets = 16;
        for (int i = 0; i < sheets; i++)
        {
            float ft = i / (float)(sheets - 1);
            float y = ft * height;
            float r = Mathf.Lerp(radius * 0.5f, radius * 1.05f, ft);
            float ang = ft * Mathf.Pi * 4f;
            var sheet = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(Mathf.Lerp(0.5f, 1.4f, ft), 1.1f, 0.05f) }, MaterialOverride = sheetMat };
            sheet.Position = new Vector3(Mathf.Cos(ang) * r, y, Mathf.Sin(ang) * r);
            sheet.Rotation = new Vector3(0, ang + Mathf.Pi * 0.5f, 0);
            spin.AddChild(sheet);
        }
        root.AddChild(new OmniLight3D { Position = new Vector3(0, height * 0.4f, 0), OmniRange = radius * 3f, LightColor = col, LightEnergy = 1.6f });

        var spinTw = spin.CreateTween();
        spinTw.TweenProperty(spin, "rotation", new Vector3(0, Mathf.Tau * 1.5f, 0), life + 0.6f);

        root.Scale = new Vector3(0.6f, 0.2f, 0.6f);
        var g = root.CreateTween();
        g.TweenProperty(root, "scale", Vector3.One, 0.25f).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Back);
        g.TweenInterval(life);
        g.TweenProperty(root, "scale", new Vector3(1.2f, 0.05f, 1.2f), 0.35f);
        g.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(root)) root.QueueFree(); }));
    }

    // (NEW) A flat glowing scorch / plasma burn mark stamped on the ground where a beam or blast lands.
    // Snaps in, holds for `life`, then fades. Purely cosmetic. Used by Spelllance's hits.
    public void SpawnBurnMark(Vector3 at, Color col, float size, float life, bool net = true)
    {
        if (net) NetMgr?.BroadcastVfx(25, at, Vector3.Zero, size, life, col);
        float gy = SurfaceHeight(at, at.Y);
        var mat = ToonEmissive(col, 2.2f, 0f);
        if (mat is StandardMaterial3D psm) { psm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; psm.AlbedoColor = new Color(col.R, col.G, col.B, 0.55f); psm.CullMode = BaseMaterial3D.CullModeEnum.Disabled; }
        var mark = new MeshInstance3D { Mesh = new QuadMesh { Size = new Vector2(size, size) }, MaterialOverride = mat };
        AddChild(mark);
        mark.GlobalPosition = new Vector3(at.X, gy + 0.05f, at.Z);
        mark.RotationDegrees = new Vector3(-90f, (float)GD.RandRange(0, 360), 0f);   // lie flat, random yaw
        mark.Scale = new Vector3(0.3f, 0.3f, 0.3f);
        var tw = mark.CreateTween();
        tw.TweenProperty(mark, "scale", Vector3.One, 0.12f).SetEase(Tween.EaseType.Out);
        tw.TweenInterval(life);
        tw.TweenProperty(mark, "transparency", 1f, 0.6f);
        tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(mark)) mark.QueueFree(); }));
    }

    // (NEW) Glowing pollen motes that drift and float within an area over `life` (staggered so they keep
    // appearing across the field's lifetime). Used by Mending Grove (holy) and Creeping Blight (nature).
    public void SpawnPollen(Vector3 center, float radius, Color col, int count, float life, bool net = true)
    {
        if (net) NetMgr?.BroadcastVfx(26, center, new Vector3(count, 0f, 0f), radius, life, col);
        float gy = SurfaceHeight(center, center.Y);
        int n = Mathf.Max(4, (int)(count * ParticleScale));
        var mat = ToonEmissive(col, 2.4f, 0f);
        if (mat is StandardMaterial3D psm) { psm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; psm.AlbedoColor = new Color(col.R, col.G, col.B, 0.85f); psm.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded; }
        for (int i = 0; i < n; i++)
        {
            var mote = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.06f + GD.Randf() * 0.05f, Height = 0.14f }, MaterialOverride = mat };
            AddChild(mote);
            float a = GD.Randf() * Mathf.Tau, rr = GD.Randf() * radius;
            var start = new Vector3(center.X + Mathf.Cos(a) * rr, gy + 0.3f, center.Z + Mathf.Sin(a) * rr);
            mote.GlobalPosition = start;
            mote.Scale = Vector3.Zero;
            float delay = GD.Randf() * life * 0.7f;
            float dur = 1.4f + GD.Randf() * 1.6f;
            var drift = start + new Vector3((GD.Randf() - 0.5f) * radius * 0.8f, 1.2f + GD.Randf() * 1.6f, (GD.Randf() - 0.5f) * radius * 0.8f);
            var tw = mote.CreateTween();
            tw.TweenInterval(delay);
            tw.TweenProperty(mote, "scale", Vector3.One, 0.3f);
            tw.SetParallel(true);
            tw.TweenProperty(mote, "global_position", drift, dur);
            tw.TweenProperty(mote, "transparency", 1f, dur);
            tw.SetParallel(false);
            tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(mote)) mote.QueueFree(); }));
        }
    }

    // (NEW) A little meadow of glowing flower tufts that pops up across a disc, holds `life`, then withers.
    // Used by Mending Grove (blooms tinted `col`).
    public void SpawnMeadow(Vector3 center, float radius, Color col, float life, bool net = true)
    {
        if (net) NetMgr?.BroadcastVfx(27, center, Vector3.Zero, radius, life, col);
        float baseY = SurfaceHeight(center, center.Y);
        int n = Mathf.Max(6, (int)(16 * ParticleScale));
        var stemMat = ToonEmissive(new Color(0.35f, 0.7f, 0.3f), 0.5f, 0.02f);
        var bloomMat = ToonEmissive(col, 2.2f, 0.02f);
        for (int i = 0; i < n; i++)
        {
            float a = GD.Randf() * Mathf.Tau, rr = GD.Randf() * radius;
            var pos = new Vector3(center.X + Mathf.Cos(a) * rr, 0, center.Z + Mathf.Sin(a) * rr);
            float gy = SurfaceHeight(pos, baseY);
            var tuft = new Node3D(); AddChild(tuft);
            tuft.GlobalPosition = new Vector3(pos.X, gy, pos.Z);
            float h = 0.3f + GD.Randf() * 0.25f;
            var stem = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.02f, BottomRadius = 0.04f, Height = h }, MaterialOverride = stemMat };
            stem.Position = new Vector3(0, h * 0.5f, 0);
            tuft.AddChild(stem);
            var bloom = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.12f + GD.Randf() * 0.06f, Height = 0.14f }, MaterialOverride = bloomMat };
            bloom.Position = new Vector3(0, h, 0);
            tuft.AddChild(bloom);
            tuft.Scale = Vector3.Zero;
            var tw = tuft.CreateTween();
            tw.TweenInterval(GD.Randf() * 0.4f);
            tw.TweenProperty(tuft, "scale", Vector3.One, 0.35f).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
            tw.TweenInterval(life);
            tw.TweenProperty(tuft, "scale", new Vector3(1f, 0.05f, 1f), 0.4f);   // wither down
            tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(tuft)) tuft.QueueFree(); }));
        }
    }

    // (NEW) A single grotesque flower that blooms at a spot, holds `life`, then wilts. Used by Creeping Blight.
    public void SpawnBlightFlower(Vector3 center, Color col, float life, bool net = true)
    {
        if (net) NetMgr?.BroadcastVfx(28, center, Vector3.Zero, 0f, life, col);
        float gy = SurfaceHeight(center, center.Y);
        var flower = new Node3D(); AddChild(flower);
        flower.GlobalPosition = new Vector3(center.X, gy, center.Z);
        var stemMat = ToonEmissive(new Color(0.28f, 0.35f, 0.16f), 0.4f, 0.03f);   // sickly stem
        var headMat = ToonEmissive(col.Darkened(0.15f), 1.4f, 0.03f);
        var petalMat = ToonEmissive(col, 1.8f, 0.03f);
        float h = 1.3f;
        var stem = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.09f, BottomRadius = 0.16f, Height = h }, MaterialOverride = stemMat };
        stem.Position = new Vector3(0, h * 0.5f, 0);
        stem.RotationDegrees = new Vector3(6f, 0, -4f);   // gnarled lean
        flower.AddChild(stem);
        var head = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.35f, Height = 0.6f }, MaterialOverride = headMat };
        head.Position = new Vector3(0, h, 0);
        flower.AddChild(head);
        int petals = 6;
        for (int i = 0; i < petals; i++)
        {
            float a = i / (float)petals * Mathf.Tau;
            var petal = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.14f, 0.05f, 0.5f) }, MaterialOverride = petalMat };
            petal.Position = new Vector3(Mathf.Cos(a) * 0.28f, h + 0.05f, Mathf.Sin(a) * 0.28f);
            petal.RotationDegrees = new Vector3(35f, Mathf.RadToDeg(a), 0);   // droop outward + down
            flower.AddChild(petal);
        }
        flower.AddChild(new OmniLight3D { Position = new Vector3(0, h, 0), OmniRange = 3f, LightColor = col, LightEnergy = 0.8f });
        flower.Scale = Vector3.Zero;
        var tw = flower.CreateTween();
        tw.TweenProperty(flower, "scale", Vector3.One, 0.5f).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
        tw.TweenInterval(life);
        tw.TweenProperty(flower, "scale", new Vector3(0.6f, 0.05f, 0.6f), 0.5f);   // wilt
        tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(flower)) flower.QueueFree(); }));
    }

    // (NEW) The Gale slam's downdraft — a funnel of compressed air driving into the ground + gust streaks
    // racing outward. Built on allies via ReceiveVfx kind 29 so the slam's downdraft replicates.
    public void SpawnDowndraft(Vector3 center, float radius, Color col)
    {
        var down = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 1.2f, BottomRadius = 0.2f, Height = 5f } };
        var dm = ToonEmissive(col, 2.2f, 0f);
        if (dm is StandardMaterial3D dsm) { dsm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; dsm.AlbedoColor = new Color(col.R, col.G, col.B, 0.55f); }
        down.MaterialOverride = dm;
        AddChild(down);
        down.GlobalPosition = new Vector3(center.X, center.Y + 4.8f, center.Z);
        var dwt = down.CreateTween(); dwt.SetParallel(true);
        dwt.TweenProperty(down, "global_position", new Vector3(center.X, center.Y + 0.4f, center.Z), 0.12f).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
        dwt.TweenProperty(down, "scale", new Vector3(2f, 0.3f, 2f), 0.16f).SetDelay(0.1f);
        dwt.TweenProperty(down, "transparency", 1f, 0.18f).SetDelay(0.08f);
        dwt.SetParallel(false);
        dwt.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(down)) down.QueueFree(); }));

        int gusts = Mathf.Max(4, (int)(8f * ParticleScale));
        var gustMat = ToonEmissive(col.Lerp(Colors.White, 0.3f), 1.8f, 0f);
        if (gustMat is StandardMaterial3D gsm) { gsm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; gsm.AlbedoColor = new Color(col.R, col.G, col.B, 0.7f); }
        for (int i = 0; i < gusts; i++)
        {
            float ga = i / (float)gusts * Mathf.Tau + GD.Randf() * 0.3f;
            var gdir = new Vector3(Mathf.Cos(ga), 0, Mathf.Sin(ga));
            var streak = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.15f, 0.06f, 1.5f) }, MaterialOverride = gustMat };
            AddChild(streak);
            float sgy = SurfaceHeight(center, center.Y);
            streak.GlobalPosition = new Vector3(center.X + gdir.X * 1.2f, sgy + 0.12f, center.Z + gdir.Z * 1.2f);
            streak.LookAt(streak.GlobalPosition + gdir, Vector3.Up);
            var send = new Vector3(center.X + gdir.X * radius * 1.05f, streak.GlobalPosition.Y, center.Z + gdir.Z * radius * 1.05f);
            var gtw = streak.CreateTween(); gtw.SetParallel(true);
            gtw.TweenProperty(streak, "global_position", send, 0.22f).SetEase(Tween.EaseType.Out);
            gtw.TweenProperty(streak, "transparency", 1f, 0.26f);
            gtw.SetParallel(false);
            gtw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(streak)) streak.QueueFree(); }));
        }
    }

    // (NEW) Exsanguinate's erupting blood column — built on allies via ReceiveVfx kind 30.
    public void SpawnBloodColumn(Vector3 at, Color col)
    {
        var col3 = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.3f, BottomRadius = 1.6f, Height = 9f }, MaterialOverride = ToonEmissive(col, 2.2f, 0.03f) };
        AddChild(col3); col3.GlobalPosition = at + new Vector3(0, 4.5f, 0);
        col3.Scale = new Vector3(0.2f, 1f, 0.2f);
        var ct = col3.CreateTween(); ct.SetParallel(true);
        ct.TweenProperty(col3, "scale", new Vector3(1.3f, 1f, 1.3f), 0.18f);
        ct.TweenProperty(col3, "transparency", 1f, 0.5f);
        ct.SetParallel(false);
        ct.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(col3)) col3.QueueFree(); }));
    }

    // (NEW) BloodRot's welling rot-bubbles — built on allies via ReceiveVfx kind 31.
    public void SpawnRotBubbles(Vector3 center, float radius, Color col)
    {
        for (int b = 0; b < 7; b++)
        {
            float a = GD.Randf() * Mathf.Tau, rr = GD.Randf() * radius;
            var bub = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.5f + GD.Randf() * 0.7f, Height = 1f }, MaterialOverride = ToonEmissive(col, 1.8f, 0.04f) };
            AddChild(bub);
            bub.GlobalPosition = center + new Vector3(Mathf.Cos(a) * rr, 0.2f, Mathf.Sin(a) * rr);
            var bt = bub.CreateTween(); bt.SetParallel(true);
            bt.TweenProperty(bub, "position", bub.GlobalPosition + new Vector3(0, 1.4f, 0), 0.6f).SetDelay(GD.Randf() * 0.4f);
            bt.TweenProperty(bub, "transparency", 1f, 0.7f).SetDelay(GD.Randf() * 0.4f);
            bt.SetParallel(false);
            bt.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(bub)) bub.QueueFree(); }));
        }
    }

    // (NEW) Wind Rush's wind-bullet, as a world-space travelling shell for allies (the caster's own copy is
    // parented to her avatar). Built via ReceiveVfx kind 32.
    // a burst of dust puffs kicked off a surface (Taker charge slam) — puffs expand, drift, and fade
    // frost witch beam segment shown to allies (short-lived; the caster broadcasts one every ~0.1s → looks continuous)
    public void SpawnFrostBeamSeg(Vector3 eye, Vector3 dir, float len)
    {
        var seg = new Node3D(); AddChild(seg);
        var core = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.12f, BottomRadius = 0.12f, Height = 1f, RadialSegments = 6 }, MaterialOverride = Emissive(new Color(0.65f, 0.88f, 1f), 3f) };
        core.RotationDegrees = new Vector3(90, 0, 0); core.Scale = new Vector3(1f, len, 1f);
        seg.AddChild(core);
        seg.GlobalPosition = eye + dir * (len / 2f);
        seg.LookAt(seg.GlobalPosition + dir, Vector3.Up);
        GetTree().CreateTimer(0.16f).Timeout += () => { if (GodotObject.IsInstanceValid(seg)) seg.QueueFree(); };
    }

    // Forsaken: the curse-suck beam segment (ally-visible ghost — kind 57)
    public void SpawnCurseBeamSeg(Vector3 from, Vector3 dir, float len)
    {
        var seg = new Node3D(); AddChild(seg);
        var col = DamageTypes.Col(DamageType.Curse);
        var core = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.1f, BottomRadius = 0.1f, Height = 1f, RadialSegments = 6 }, MaterialOverride = Emissive(col.Lerp(Colors.White, 0.3f), 3f) };
        core.RotationDegrees = new Vector3(90, 0, 0); core.Scale = new Vector3(1f, len, 1f);
        seg.AddChild(core);
        seg.GlobalPosition = from + dir * (len / 2f);
        seg.LookAt(seg.GlobalPosition + dir, Vector3.Up);
        GetTree().CreateTimer(0.16f).Timeout += () => { if (GodotObject.IsInstanceValid(seg)) seg.QueueFree(); };
    }

    // Forsaken: a persistent curse link (tether) between two foes. Caller owns it and frees it. (NEW)
    public Node3D SpawnCurseLink(Vector3 a, Vector3 b, Color col)
    {
        var link = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.1f, 0.1f, 1f) } };
        var m = ToonEmissive(col, 2f, 0f);
        if (m is StandardMaterial3D sm) { sm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; sm.AlbedoColor = new Color(col.R, col.G, col.B, 0.55f); }
        link.MaterialOverride = m;
        AddChild(link);
        var d = b - a; float len = d.Length(); if (len < 0.15f) len = 0.15f;
        link.GlobalPosition = (a + b) * 0.5f;
        if (d.LengthSquared() > 0.001f) link.LookAt(b, Vector3.Up);   // -Z spans toward b
        link.Scale = new Vector3(1f, 1f, len);
        return link;
    }

    // frost witch: ice-block shatter burst (shards + frost ring + mist)
    public void SpawnFrostShatter(Vector3 pos, float radius)
    {
        var col = new Color(0.62f, 0.86f, 1f);
        VfxRing(pos + Vector3.Up * radius * 0.5f, col, radius * 1.6f, 0.4f);
        SpawnPollen(pos + Vector3.Up * radius, radius * 2f, col, 16, 0.7f, net: false);
        for (int i = 0; i < 9; i++)
        {
            var sh = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.22f, 0.9f, 0.22f) }, MaterialOverride = Emissive(col, 2.2f) };
            AddChild(sh); sh.GlobalPosition = pos + Vector3.Up * radius * 0.6f;
            sh.RotationDegrees = new Vector3((float)GD.RandRange(0, 360), (float)GD.RandRange(0, 360), (float)GD.RandRange(0, 360));
            var v = new Vector3((float)GD.RandRange(-1.0, 1.0), (float)GD.RandRange(0.4, 1.3), (float)GD.RandRange(-1.0, 1.0)).Normalized() * (float)GD.RandRange(5.0, 9.0);
            var tw = sh.CreateTween(); tw.SetParallel(true);
            tw.TweenProperty(sh, "global_position", sh.GlobalPosition + v, 0.5);
            tw.TweenProperty(sh, "scale", Vector3.Zero, 0.5);
            tw.Chain().TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(sh)) sh.QueueFree(); }));
        }
    }
    public void SpawnFrostForm(Vector3 pos, float radius)
    {
        VfxRing(pos, new Color(0.62f, 0.86f, 1f), radius * 1.4f, 0.5f);
        SpawnPollen(pos + Vector3.Up * radius, radius * 1.6f, new Color(0.72f, 0.9f, 1f), 8, 0.6f, net: false);
    }

    // a purple magical poof when an enemy materializes: puffs bloom + drift + fade, a violet flash, and rising motes.
    // The sound is quiet + globally throttled so a group-spawn is a single soft "poof", not a wall of noise.
    public void SpawnPoof(Vector3 pos, bool net = true)
    {
        var col = new Color(0.6f, 0.3f, 0.92f);
        for (int i = 0; i < 7; i++)
        {
            var puff = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.5f, Height = 1f } };
            var m = ToonEmissive(col, 1.6f, 0f);
            m.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; m.AlbedoColor = new Color(col.R, col.G, col.B, 0.62f);
            puff.MaterialOverride = m; AddChild(puff);
            puff.GlobalPosition = pos + new Vector3(0f, 0.8f, 0f);
            var vel = new Vector3((float)GD.RandRange(-1.0, 1.0), (float)GD.RandRange(0.5, 1.6), (float)GD.RandRange(-1.0, 1.0)).Normalized() * (float)GD.RandRange(1.6, 3.2);
            var tw = CreateTween(); tw.SetParallel(true);
            tw.TweenProperty(puff, "global_position", puff.GlobalPosition + vel, 0.55);
            tw.TweenProperty(puff, "scale", Vector3.One * 2.3f, 0.55);
            tw.TweenProperty(m, "albedo_color", new Color(col.R, col.G, col.B, 0f), 0.55);
            tw.Chain().TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(puff)) puff.QueueFree(); }));
        }
        var light = new OmniLight3D { OmniRange = 5.5f, LightColor = col, LightEnergy = 3.2f, ShadowEnabled = false };
        AddChild(light); light.GlobalPosition = pos + new Vector3(0f, 1f, 0f);
        var lt = light.CreateTween(); lt.TweenProperty(light, "light_energy", 0f, 0.45f);
        lt.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(light)) light.QueueFree(); }));
        SpawnPollen(pos + new Vector3(0f, 1f, 0f), 1.6f, new Color(0.82f, 0.62f, 1f), 8, 0.8f, net: false);
        if (_poofSndT <= 0f) { _poofSndT = 0.12f; Sfx?.Poof(pos); }   // quiet, throttled: a group-spawn = ~one poof sound
        if (net) NetMgr?.BroadcastVfx(45, pos, Vector3.Zero, 0f, 0f, col);
    }

    public void SpawnDust(Vector3 pos, Vector3 dir)
    {
        var col = new Color(0.72f, 0.66f, 0.55f);
        dir.Y = 0f; dir = dir.LengthSquared() > 0.001f ? dir.Normalized() : Vector3.Forward;
        for (int i = 0; i < 8; i++)
        {
            var puff = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.35f, Height = 0.7f } };
            var m = ToonEmissive(col, 0.25f, 0f);
            m.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; m.AlbedoColor = new Color(col.R, col.G, col.B, 0.55f);
            puff.MaterialOverride = m;
            AddChild(puff);
            puff.GlobalPosition = pos + new Vector3(0, 0.3f, 0);
            var vel = (-dir + new Vector3((float)GD.RandRange(-1.0, 1.0), (float)GD.RandRange(0.4, 1.4), (float)GD.RandRange(-1.0, 1.0))).Normalized() * (float)GD.RandRange(1.8, 3.6);
            var tw = CreateTween(); tw.SetParallel(true);
            tw.TweenProperty(puff, "global_position", puff.GlobalPosition + vel, 0.6);
            tw.TweenProperty(puff, "scale", Vector3.One * 2.4f, 0.6);
            tw.TweenProperty(m, "albedo_color", new Color(col.R, col.G, col.B, 0f), 0.6);
            tw.Chain().TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(puff)) puff.QueueFree(); }));
        }
    }

    // boss goblin pestilence: a lingering Nature pool that hurts witches until the boss dies (host damages; clients get a ghost)
    public void SpawnPestilence(Vector3 pos, float radius, float dmg, bool remote, bool net = true)
    {
        var p = new PestilencePool { Remote = remote }; AddChild(p); p.Init(pos, radius, dmg);
        if (net) NetMgr?.BroadcastVfx(41, pos, Vector3.Zero, radius, dmg, Colors.White);
    }

    // orc boss rock throw (arcs at a witch, stuns) — deterministic flight so host+clients match; host applies the hit
    public void SpawnBossRock(Vector3 from, Vector3 target, float dmg, bool remote, bool net = true)
    {
        var r = new BossRock { Remote = remote }; AddChild(r); r.Init(from, target, dmg);
        if (net) NetMgr?.BroadcastVfx(44, from, target, dmg, 0f, Colors.White);
    }
    // non-zombie goblin mines: scatter armed mines around a spot (host); each is broadcast as a ghost to clients
    public void SpawnBossMines(Vector3 center, int count, float dmg)
    {
        for (int i = 0; i < count; i++)
        {
            float a = i / (float)count * Mathf.Tau + (float)GD.RandRange(-0.4, 0.4);
            float rr = 4f + (float)GD.RandRange(0.0, 6.0);
            var pos = new Vector3(center.X + Mathf.Cos(a) * rr, 0f, center.Z + Mathf.Sin(a) * rr);
            var m = new BossMine(); AddChild(m); m.Init(pos, 3.4f, dmg);
            NetMgr?.BroadcastVfx(42, pos, Vector3.Zero, 3.4f, dmg, Colors.White);
        }
    }
    public void SpawnBossMineGhost(Vector3 pos, float radius, float dmg) { var m = new BossMine { Remote = true }; AddChild(m); m.Init(pos, radius, dmg); }
    public void DetonateBossMineGhost(Vector3 pos)
    {
        BossMine best = null; float bd = 5f;
        foreach (var c in GetChildren()) if (c is BossMine bm && GodotObject.IsInstanceValid(bm)) { float d = bm.GlobalPosition.DistanceTo(pos); if (d < bd) { bd = d; best = bm; } }
        VfxRing(pos, new Color(0.6f, 0.9f, 0.3f), best != null ? best.Radius : 3.4f, 0.5f);
        SpawnBrambleBurst(pos, 1.6f, 9, net: false);
        Sfx?.Thunder();
        if (best != null) best.QueueFree();
    }

    public void SpawnWindBullet(Vector3 start, Vector3 dir, float dist, float dur)
    {
        dir.Y = 0f; dir = dir.LengthSquared() > 0.001f ? dir.Normalized() : Vector3.Forward;
        var col = DamageTypes.Col(DamageType.Wind);
        var rig = new Node3D(); AddChild(rig);
        rig.GlobalPosition = new Vector3(start.X, start.Y + 1.0f, start.Z) + dir * 1.5f;   // lead the dash so it reads as a forward bullet
        rig.LookAt(rig.GlobalPosition + dir, Vector3.Up);   // -Z runs along the dash line

        // elongated wind-bullet core (long axis = travel)
        var shell = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.8f, Height = 1.6f } };
        var shm = ToonEmissive(col, 1.5f, 0f);
        shm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; shm.AlbedoColor = new Color(col.R, col.G, col.B, 0.28f); shm.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        shell.MaterialOverride = shm;
        shell.Scale = new Vector3(0.7f, 0.7f, 3.4f);
        rig.AddChild(shell);

        // forward gust streaks ringing the core, running along the travel line → sells the direction
        var streakMat = ToonEmissive(col.Lerp(Colors.White, 0.3f), 2f, 0f);
        streakMat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; streakMat.AlbedoColor = new Color(col.R, col.G, col.B, 0.55f); streakMat.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded; streakMat.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        for (int i = 0; i < 5; i++)
        {
            float a = i / 5f * Mathf.Tau;
            var streak = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.12f, 0.12f, 2.8f) }, MaterialOverride = streakMat };
            streak.Position = new Vector3(Mathf.Cos(a) * 0.7f, Mathf.Sin(a) * 0.7f, 0f);
            rig.AddChild(streak);
        }
        rig.AddChild(new OmniLight3D { OmniRange = 5f, LightColor = col, LightEnergy = 1.5f });

        var end = rig.GlobalPosition + dir * dist;
        var st = rig.CreateTween(); st.SetParallel(true);
        st.TweenProperty(rig, "global_position", end, dur).SetEase(Tween.EaseType.Out);
        st.TweenProperty(shell, "scale", new Vector3(0.5f, 0.5f, 4.2f), dur);
        st.TweenProperty(shell, "transparency", 1f, dur);
        st.SetParallel(false);
        st.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(rig)) rig.QueueFree(); }));
    }

    // (NEW) The Divine right-click's descending holy ray, sweeping forward — built on allies via kind 33.
    public void SpawnHolySweep(Vector3 o, Vector3 fwd, float len, float half)
    {
        float sweepDur = 1.2f;
        var beam = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = Mathf.Max(0.35f, half * 0.8f), BottomRadius = Mathf.Max(0.5f, half * 1.15f), Height = 34f } };
        beam.MaterialOverride = HolyRayMat();
        beam.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        AddChild(beam);
        Vector3 start = new Vector3(o.X, 16f, o.Z);
        Vector3 end = new Vector3(o.X, 16f, o.Z) + fwd * len;
        beam.GlobalPosition = start;
        beam.Transparency = 0.2f;
        var tw = beam.CreateTween(); tw.SetParallel(true);
        tw.TweenProperty(beam, "global_position", end, sweepDur).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
        tw.TweenProperty(beam, "transparency", 1f, sweepDur * 0.45f).SetDelay(sweepDur * 0.75f);
        tw.SetParallel(false);
        tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(beam)) beam.QueueFree(); }));
    }

    private const string BloodOrbCode = @"
shader_type spatial;
render_mode cull_back, diffuse_toon;
uniform vec3 col = vec3(0.55, 0.02, 0.05);
void fragment(){
    float fres = pow(1.0 - clamp(dot(normalize(NORMAL), normalize(VIEW)), 0.0, 1.0), 2.5);
    ALBEDO = col * 0.2;
    EMISSION = col * (0.4 + 3.5 * fres);   // dark viscous body, hot glowing rim (no veins)
    ROUGHNESS = 0.35;
    METALLIC = 0.0;
}
";
    private const string SigilCode = @"
shader_type spatial;
render_mode blend_mix, unshaded, cull_disabled, depth_draw_never;
uniform vec3 col = vec3(0.8, 0.05, 0.1);
void fragment(){
    vec2 p = UV - vec2(0.5);
    float r = length(p) * 2.0;
    float a = atan(p.y, p.x);
    float t = TIME;
    float ring1 = smoothstep(0.035, 0.0, abs(r - 0.92));
    float ring2 = smoothstep(0.025, 0.0, abs(r - 0.70));
    float ring3 = smoothstep(0.02, 0.0, abs(r - 0.60));
    float ticks = 0.0;
    if (r > 0.70 && r < 0.92) { float seg = fract((a + t * 0.3) * 12.0 / 6.2831853); ticks = smoothstep(0.10, 0.0, min(seg, 1.0 - seg)); }
    float glyph = 0.0;
    if (r > 0.575 && r < 0.66) { float seg2 = fract((a - t * 0.5) * 8.0 / 6.2831853); glyph = smoothstep(0.07, 0.0, min(seg2, 1.0 - seg2)); }
    float mask = clamp(ring1 + ring2 + ring3 * 0.6 + ticks * 0.85 + glyph, 0.0, 1.0);
    if (r > 0.98) mask = 0.0;
    EMISSION = col * mask * 3.2;
    ALBEDO = col * mask;
    ALPHA = mask;
}
";

    // (NEW) elem indices MATCH the DamageType enum order: Lunar0 Arcane1 Nature2 Frost3 Curse4 Holy5 Ember6 Physical7 Blood8 Wind9.
    private const string ElementBoltCode = @"
shader_type spatial;
render_mode cull_back, diffuse_toon, specular_toon;

uniform vec3 base_color = vec3(0.8, 0.8, 0.9);   // LINEAR (set from V3lin)
uniform int elem = 0;

varying vec3 v_obj;

float hash13(vec3 p){ p = fract(p * 0.1031); p += dot(p, p.zyx + 31.32); return fract((p.x + p.y) * p.z); }
float vnoise(vec3 p){
    vec3 i = floor(p); vec3 f = fract(p); f = f * f * (3.0 - 2.0 * f);
    float n000 = hash13(i + vec3(0.0, 0.0, 0.0)); float n100 = hash13(i + vec3(1.0, 0.0, 0.0));
    float n010 = hash13(i + vec3(0.0, 1.0, 0.0)); float n110 = hash13(i + vec3(1.0, 1.0, 0.0));
    float n001 = hash13(i + vec3(0.0, 0.0, 1.0)); float n101 = hash13(i + vec3(1.0, 0.0, 1.0));
    float n011 = hash13(i + vec3(0.0, 1.0, 1.0)); float n111 = hash13(i + vec3(1.0, 1.0, 1.0));
    float x00 = mix(n000, n100, f.x); float x10 = mix(n010, n110, f.x);
    float x01 = mix(n001, n101, f.x); float x11 = mix(n011, n111, f.x);
    return mix(mix(x00, x10, f.y), mix(x01, x11, f.y), f.z);
}
float fbm(vec3 p){ float v = 0.0; float a = 0.5; for (int i = 0; i < 4; i++) { v += a * vnoise(p); p *= 2.02; a *= 0.5; } return v; }

void vertex(){ v_obj = VERTEX; }   // object-space position so the pattern rides the mesh, not the world

void fragment(){
    vec3 p = normalize(v_obj) * 2.2;
    float t = TIME;
    vec3 col = base_color;
    vec3 emis = base_color;
    float energy = 1.2;
    float rough = 0.5;
    float metal = 0.0;

    if (elem == 6) {                                   // Ember — flickering fire
        float f = fbm(p * 1.6 + vec3(0.0, -t * 2.2, 0.0));
        float hot = smoothstep(0.45, 0.9, f + 0.15 * sin(t * 12.0));
        col = mix(vec3(0.45, 0.06, 0.02), base_color, f);
        emis = mix(col, vec3(1.0, 0.9, 0.55), hot);
        energy = 1.6 + hot * 2.2;
        rough = 0.7;
    } else if (elem == 3) {                            // Frost — crystalline facets
        float f = fbm(p * 3.0);
        float facet = step(0.5, fract(f * 4.0));
        col = mix(base_color * 0.7, vec3(0.85, 0.95, 1.0), facet);
        emis = col;
        energy = 0.9 + 0.3 * facet;
        rough = 0.15;
    } else if (elem == 8) {                            // Blood — viscous, pulsing veins
        float f = fbm(p * 2.2 + vec3(0.0, t * 0.4, 0.0));
        float vein = smoothstep(0.55, 0.62, f);
        col = mix(vec3(0.28, 0.02, 0.03), base_color, vein);
        float pulse = 0.5 + 0.5 * sin(t * 3.0);
        emis = mix(col, vec3(0.9, 0.1, 0.12), vein * pulse);
        energy = 0.8 + vein * pulse * 1.2;
        rough = 0.25;
    } else if (elem == 9) {                            // Wind — wispy swirl
        float f = fbm(p * 1.4 + vec3(t * 1.6, t * 0.6, 0.0));
        col = mix(base_color * 0.8, vec3(0.95, 1.0, 0.98), smoothstep(0.4, 0.8, f));
        emis = col;
        energy = 0.9 + f * 0.8;
        rough = 0.6;
    } else if (elem == 4) {                            // Curse — oily churn
        float f = fbm(p * 2.0 + vec3(0.0, 0.0, t * 0.6));
        float irid = 0.5 + 0.5 * sin(f * 10.0 + t * 2.0);
        col = mix(vec3(0.16, 0.02, 0.20), base_color, f);
        emis = mix(col, vec3(0.9, 0.4, 1.0), irid * 0.4);
        energy = 1.0 + irid * 0.6;
        rough = 0.3;
    } else if (elem == 1) {                            // Arcane — shimmering sparkle
        float f = fbm(p * 4.0 + vec3(t * 0.8));
        float spark = step(0.86, hash13(floor(p * 20.0) + floor(vec3(t * 6.0))));
        emis = mix(base_color, vec3(1.0), spark);
        energy = 1.3 + spark * 2.0 + f * 0.4;
        rough = 0.4;
    } else if (elem == 2) {                            // Nature — mottled
        float f = fbm(p * 2.6);
        col = mix(base_color * 0.6, base_color, f);
        emis = col;
        energy = 0.8 + f * 0.4;
        rough = 0.7;
    } else if (elem == 7) {                            // Physical — plain metal
        emis = base_color * 0.25;
        energy = 0.3;
        rough = 0.35;
        metal = 0.6;
    } else if (elem == 5) {                            // Holy — radiant pulse
        float pulse = 0.6 + 0.4 * sin(t * 2.5);
        col = mix(base_color, vec3(1.0), 0.4);
        emis = mix(base_color, vec3(1.0, 0.97, 0.85), 0.5);
        energy = 1.6 + pulse * 1.0;
        rough = 0.4;
    } else {                                           // Lunar (0) + fallback — soft silver glow
        float sh = 0.5 + 0.5 * sin(fbm(p * 2.0) * 6.0 + t * 1.5);
        col = mix(base_color * 0.8, vec3(1.0), 0.3);
        energy = 1.1 + sh * 0.5;
        rough = 0.3;
    }

    ALBEDO = col;
    EMISSION = emis * energy;
    ROUGHNESS = rough;
    METALLIC = metal;
    float rim = pow(1.0 - clamp(dot(normalize(NORMAL), normalize(VIEW)), 0.0, 1.0), 2.5);   // fresnel rim for readability
    EMISSION += base_color * rim * 0.8;
}
";

    // Custom moonlit-forest sky: gradient + big pale moon + sparse twinkling stars + drifting clouds + faint aurora.
    // Driven per-phase from ApplyDayNight (sky_top/horizon, moon_dir, night/star/aurora amounts). (NEW)
    private const string SkyCode = @"
shader_type sky;


uniform vec3 sky_top = vec3(0.07, 0.10, 0.24);
uniform vec3 sky_horizon = vec3(0.34, 0.42, 0.50);
uniform vec3 ground_horizon = vec3(0.10, 0.13, 0.13);
uniform vec3 ground_bottom = vec3(0.02, 0.03, 0.04);
uniform vec3 moon_dir = vec3(0.0, 0.85, 0.45);
uniform vec3 moon_color = vec3(0.95, 0.96, 1.0);
uniform float moon_size = 0.012;
uniform vec3 cloud_color = vec3(0.55, 0.60, 0.72);
uniform float cloud_amt = 0.5;
uniform float night = 1.0;
uniform float star_amt = 1.0;
uniform float aurora_amt = 0.5;
uniform float sky_time = 0.0;   // (NEW) game clock that stops on pause — used for all sky animation instead of TIME

float hash13(vec3 p){ p = fract(p * 0.1031); p += dot(p, p.zyx + 31.32); return fract((p.x + p.y) * p.z); }
float hash21(vec2 p){ p = fract(p * vec2(123.34, 456.21)); p += dot(p, p + 45.32); return fract(p.x * p.y); }
float vnoise(vec2 p){
    vec2 i = floor(p), f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = hash21(i), b = hash21(i + vec2(1.0, 0.0));
    float c = hash21(i + vec2(0.0, 1.0)), d = hash21(i + vec2(1.0, 1.0));
    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}
float fbm(vec2 p){ float v = 0.0, a = 0.5; for (int i = 0; i < 5; i++){ v += a * vnoise(p); p *= 2.0; a *= 0.5; } return v; }

void sky(){
    vec3 dir = normalize(EYEDIR);
    float h = dir.y;

    vec3 col;
    if (h >= 0.0) { col = mix(sky_horizon, sky_top, pow(clamp(h, 0.0, 1.0), 0.55)); }
    else { col = mix(ground_horizon, ground_bottom, pow(clamp(-h, 0.0, 1.0), 0.5)); }

    float skyMask = smoothstep(-0.02, 0.08, h);

    // sparse twinkling point-stars (one per occupied cell, centred)
    if (star_amt > 0.001 && skyMask > 0.0) {
        vec3 sp = dir * 200.0;
        vec3 cell = floor(sp);
        vec3 f = fract(sp) - 0.5;
        float hh = hash13(cell);
        float star = step(0.986, hh) * (1.0 - smoothstep(0.0, 0.32, length(f)));
        star *= 0.65 + 0.35 * sin(sky_time * 2.5 + hh * 90.0);
        col += vec3(0.95, 0.97, 1.0) * star * 2.2 * star_amt * skyMask;
    }

    // slow drifting clouds — kept faint so they read as haze, not banded streaks
    if (cloud_amt > 0.001 && h > 0.03) {
        vec2 cuv = dir.xz / (0.55 + h * 0.8);
        cuv = cuv * 0.7 + vec2(sky_time * 0.006, sky_time * 0.003);
        float c = smoothstep(0.60, 1.05, fbm(cuv * 1.3)) * cloud_amt;
        c *= smoothstep(0.03, 0.32, h);
        col = mix(col, mix(cloud_color, sky_top + vec3(0.05), night * 0.4), c * 0.45);
    }

    // faint aurora ribbon high in the sky
    if (aurora_amt > 0.001 && h > 0.10) {
        float ab = (h - 0.45) * 3.2;
        float band = exp(-ab * ab);
        float wav = vnoise(vec2(dir.x * 2.5 + sky_time * 0.05, dir.z * 2.5));
        float a = band * smoothstep(0.4, 0.8, wav) * aurora_amt;
        col += mix(vec3(0.15, 0.7, 0.5), vec3(0.35, 0.4, 0.8), wav) * a * 0.5;
    }

    // sun disc + soft daytime glow, from the key directional light
    if (LIGHT0_ENABLED) {
        float sd = dot(dir, -LIGHT0_DIRECTION);
        float sun = smoothstep(0.9990, 0.9996, sd);      // (NEW) crisp disc edge (was soft/blurry)
        col = mix(col, LIGHT0_COLOR * (2.0 + LIGHT0_ENERGY), sun);
        col += LIGHT0_COLOR * smoothstep(0.95, 0.9990, sd) * 0.10 * (1.0 - night);
    }

    // big pale moon — crisp disc, finer surface, soft halo
    float md = dot(dir, normalize(moon_dir));
    float disc = smoothstep(1.0 - moon_size, 1.0 - moon_size * 0.88, md);          // (NEW) crisp edge (was *0.4 → very soft/blurry)
    float limb = smoothstep(1.0 - moon_size, 1.0 - moon_size * 0.15, md);          // brighter centre, gently darker rim
    float craters = fbm(dir.xy * 55.0 + 5.0) * 0.6 + fbm(dir.xy * 22.0) * 0.4;     // (NEW) finer, multi-octave — no blocky low-res speckle
    vec3 moonSurf = moon_color * (0.85 + 0.5 * craters) * (0.6 + 0.4 * limb);
    col = mix(col, moonSurf, disc);
    float halo = smoothstep(1.0 - moon_size * 6.0, 1.0 - moon_size, md) * (1.0 - disc);
    col += moon_color * halo * 0.18 * (0.5 + 0.5 * night);

    col += (hash21(SCREEN_UV * 973.0) - 0.5) * (1.6 / 255.0);   // (NEW) screen dither — breaks up the smooth-gradient banding that showed as faint arc streaks
    COLOR = col;
}
";

    private void BuildWorld()
    {
        // Environment — stylized dusk. Flatter, painterly lighting that reads as cel-shaded.
        var env = new Godot.Environment();
        env.BackgroundMode = Godot.Environment.BGMode.Sky;
        var sky = new Sky();
        var skyMat = new ShaderMaterial { Shader = new Shader { Code = SkyCode } };   // custom moonlit sky: moon, stars, clouds, aurora (NEW)
        skyMat.SetShaderParameter("sky_top", V3lin(new Color(0.07f, 0.10f, 0.24f)));
        skyMat.SetShaderParameter("sky_horizon", V3lin(new Color(0.34f, 0.42f, 0.50f)));
        skyMat.SetShaderParameter("ground_horizon", V3lin(new Color(0.10f, 0.13f, 0.13f)));
        skyMat.SetShaderParameter("ground_bottom", V3lin(new Color(0.02f, 0.03f, 0.04f)));
        skyMat.SetShaderParameter("moon_color", V3lin(new Color(0.95f, 0.96f, 1.0f)));
        skyMat.SetShaderParameter("moon_size", 0.022f);
        skyMat.SetShaderParameter("cloud_color", V3lin(new Color(0.55f, 0.60f, 0.72f)));
        skyMat.SetShaderParameter("cloud_amt", 0.45f);
        skyMat.SetShaderParameter("moon_dir", new Vector3(0.55f, 0.52f, 0.0f).Normalized());
        skyMat.SetShaderParameter("night", 1.0f);
        skyMat.SetShaderParameter("star_amt", 1.0f);
        skyMat.SetShaderParameter("aurora_amt", 0.5f);
        sky.SkyMaterial = skyMat;
        env.Sky = sky;
        // soft sky ambient so toon bands read in shadow too
        env.AmbientLightSource = Godot.Environment.AmbientSource.Sky;
        env.AmbientLightSkyContribution = 0.5f;
        env.AmbientLightEnergy = 0.6f;
        // painterly naturalistic rolloff + a gentle grade for the fairytale look (NEW)
        env.TonemapMode = Godot.Environment.ToneMapper.Filmic;
        env.TonemapWhite = 1.3f;
        env.AdjustmentEnabled = true;
        env.AdjustmentContrast = 1.04f;
        env.AdjustmentSaturation = 1.06f;
        // soft moonlit glow on emissives — eased back from the old synthwave neon blast (NEW)
        env.GlowEnabled = true;
        env.GlowIntensity = 0.5f;
        env.GlowBloom = 0.16f;
        env.GlowStrength = 1.05f;
        env.SsaoEnabled = true;
        env.SsaoRadius = 1.2f;
        env.SsaoIntensity = 1.6f;
        // SSIL — screen-space indirect light: cheap fake-GI that bounces colour between nearby surfaces (moss-green
        // off foliage, cool off the moonlit ground), so the forest reads as lit by its surroundings. Forward+ only. (NEW)
        env.SsilEnabled = true;
        env.SsilRadius = 6f;
        env.SsilIntensity = 1.25f;
        env.SsilSharpness = 0.98f;
        env.SsilNormalRejection = 1.0f;
        env.FogEnabled = true;
        env.FogLightColor = new Color(0.34f, 0.42f, 0.44f);   // cool forest mist (NEW: was magenta haze; ApplyDayNight tints it per phase)
        env.FogDensity = 0.007f;
        env.FogSkyAffect = 0.1f;   // (NEW) fog barely touches the sky — was defaulting to 1.0, which washed the whole sky (moon/stars/sun) to flat fog colour. THIS is why the sky looked blank.

        var we = new WorldEnvironment { Environment = env };
        AddChild(we);
        _env = env; _skyMat = skyMat;
        ApplyGraphics();   // (NEW) honour the loaded per-machine graphics settings

        // Key light (cool moonlight) — brighter for crisp toon banding.
        var sun = new DirectionalLight3D();
        sun.RotationDegrees = new Vector3(-52, -38, 0);
        sun.LightColor = new Color(0.80f, 0.82f, 1.0f);
        sun.LightEnergy = 1.4f;
        sun.ShadowEnabled = true;
        AddChild(sun);
        _sun = sun;

        // Soft cool fill from the opposite side (no shadow) — a faint forest/moonlight bounce for the two-tone cel look (NEW)
        var fill = new DirectionalLight3D();
        fill.RotationDegrees = new Vector3(-28, 140, 0);
        fill.LightColor = new Color(0.34f, 0.42f, 0.40f);
        fill.LightEnergy = 0.45f;
        fill.ShadowEnabled = false;
        AddChild(fill);

        // Procedural streaming world (chunks load around the player).
        _world = new World();
        if (IsAuthority) WorldSeed = (long)(((ulong)GD.Randi() << 32) ^ (ulong)GD.Randi() ^ 0x9E3779B97F4A7C15UL);   // host/solo pick the map; clients get it over the net (NEW)
        _world.SetSeed((ulong)WorldSeed);
        AddChild(_world);
        _world.Update(Vector3.Zero);
    }

    // a client received the host's map seed — rebuild the world to match (NEW)
    public void ReseedWorld(long seed)
    {
        WorldSeed = seed;
        if (_world != null) _world.Reseed((ulong)seed, Player != null ? Player.GlobalPosition : Vector3.Zero);
    }

    private void AddTree(Vector3 pos, float scale = 1f, Color? tip = null)
    {
        var trunk = new MeshInstance3D();
        trunk.Mesh = new CylinderMesh { TopRadius = 0.5f * scale, BottomRadius = 0.9f * scale, Height = 7f * scale };
        var tmat = new StandardMaterial3D { AlbedoColor = new Color(0.10f, 0.08f, 0.14f), Roughness = 0.9f };
        trunk.MaterialOverride = tmat;
        trunk.Position = pos + new Vector3(0, 3.5f * scale, 0);
        AddChild(trunk);

        var crown = new MeshInstance3D();
        crown.Mesh = new SphereMesh { Radius = 2.2f * scale, Height = 4.4f * scale };
        crown.MaterialOverride = Emissive(tip ?? Palette.Verdant, 0.35f);
        crown.Position = pos + new Vector3(0, 7.5f * scale, 0);
        AddChild(crown);
    }

    public static StandardMaterial3D Emissive(Color c, float energy)
    {
        var m = new StandardMaterial3D();
        m.AlbedoColor = new Color(c.R * 0.35f, c.G * 0.35f, c.B * 0.35f);
        m.EmissionEnabled = true;
        m.Emission = c;
        m.EmissionEnergyMultiplier = 1.0f + energy * 1.6f;   // gentler than before — less blinding bloom
        m.Roughness = 0.85f;
        m.DiffuseMode = BaseMaterial3D.DiffuseModeEnum.Toon;
        m.SpecularMode = BaseMaterial3D.SpecularModeEnum.Toon;
        return m;
    }

    // A glowing vertical ray that marks lootable points (chests, roulette) from a distance.
    // Returns the container so callers can free it (e.g. a chest hides its beam once opened).
    public Node3D ZapMarkNode(Vector3 at)
    {
        var n = new Node3D(); AddChild(n);
        n.GlobalPosition = new Vector3(at.X, 0.06f, at.Z);
        var ring = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = 2.9f, OuterRadius = 3.4f } };
        ring.MaterialOverride = Emissive(new Color(1f, 0.92f, 0.3f), 2.4f);
        n.AddChild(ring);   // flat by default in this build (NEW: removed upright rotation)
        return n;
    }
    public void ZapTelegraphVFX(Vector3 at)   // client-side telegraph (auto-clears around strike time)
    {
        var n = ZapMarkNode(at);
        n.GetTree().CreateTimer(1.15f).Timeout += () => { if (GodotObject.IsInstanceValid(n)) n.QueueFree(); };
    }
    public void ZapStrikeVFX(Vector3 at)       // client-side lightning flash
    {
        var n = ZapMarkNode(at);
        var bolt = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.12f, BottomRadius = 0.45f, Height = 34f } };
        bolt.MaterialOverride = Emissive(new Color(0.75f, 0.88f, 1f), 3.2f);
        bolt.Position = new Vector3(0, 17f, 0);
        n.AddChild(bolt);
        Sfx?.Thunder();
        n.GetTree().CreateTimer(0.3f).Timeout += () => { if (GodotObject.IsInstanceValid(n)) n.QueueFree(); };
    }

    public void BlastVFX(Vector3 at, float radius, Color col)
    {
        var n = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = radius * 0.55f, OuterRadius = radius } };
        n.MaterialOverride = Emissive(col, 2.6f);
        AddChild(n);   // flat by default in this build (NEW: removed upright rotation)
        n.GlobalPosition = at + new Vector3(0, 0.1f, 0);
        n.GetTree().CreateTimer(0.25f).Timeout += () => { if (GodotObject.IsInstanceValid(n)) n.QueueFree(); };
    }

    public void VfxRing(Vector3 at, Color col, float grow, float life)
    {
        var v = new Vfx(); AddChild(v);
        v.GlobalPosition = new Vector3(at.X, SurfaceHeight(at, 1e9f) + 0.3f, at.Z);   // ride the terrain so AoE rings don't sink into hills (NEW)
        v.Init(new TorusMesh { InnerRadius = 0.9f, OuterRadius = 1.15f }, col, life, grow);
        WaterTouchArea(at, grow, Mathf.Clamp(grow * 0.12f, 0.25f, 1.3f));   // any AoE ring splashes water within its radius (NEW)
    }
    public void VfxBeam(Vector3 o, Vector3 fwd, float len, float half, Color col)
    {
        if (fwd.LengthSquared() < 0.001f) fwd = Vector3.Forward;
        var rig = new Node3D(); AddChild(rig);
        rig.GlobalPosition = new Vector3(o.X, 0.3f, o.Z) + fwd * (len / 2f);
        rig.Rotation = new Vector3(0, Mathf.Atan2(fwd.X, fwd.Z), 0);
        var fil = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.3f, 0.3f, len) }, MaterialOverride = Emissive(Colors.White, 6f) };
        rig.AddChild(fil);                                                                                   // white-hot filament
        var core = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(half * 0.7f, half * 0.7f, len) }, MaterialOverride = Emissive(col.Lerp(Colors.White, 0.45f), 4.5f) };
        rig.AddChild(core);                                                                                  // plasma core
        var sheath = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(half * 1.4f, half * 1.4f, len) } };
        var sm = Emissive(col, 2.6f); sm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; var scc = sm.AlbedoColor; scc.A = 0.45f; sm.AlbedoColor = scc; sheath.MaterialOverride = sm;
        rig.AddChild(sheath);                                                                                // translucent plasma sheath
        var halo = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(half * 2.2f, half * 2.2f, len) } };
        var hm = Emissive(col, 1.8f); hm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; var hc = hm.AlbedoColor; hc.A = 0.22f; hm.AlbedoColor = hc; halo.MaterialOverride = hm;
        rig.AddChild(halo);                                                                                  // soft outer glow
        rig.AddChild(new OmniLight3D { OmniRange = 9f, LightColor = col, LightEnergy = 3f });
        rig.Scale = new Vector3(1f, 1f, 0.1f);
        var tw = rig.CreateTween();
        tw.TweenProperty(rig, "scale", new Vector3(1f, 1f, 1f), 0.10f);
        tw.TweenInterval(0.55f);
        tw.TweenProperty(rig, "scale", new Vector3(1f, 1f, 0.05f), 0.20f);
        tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(rig)) rig.QueueFree(); }));
    }
    public void VfxCrimsonOrb(Vector3 at, float radius, Color col)
    {
        var orb = new MeshInstance3D { Mesh = new SphereMesh { Radius = 1.0f, Height = 2.0f } };
        orb.MaterialOverride = BloodOrbMat();   // (NEW) authentic dark-body/hot-rim blood orb for allies
        AddChild(orb);
        orb.GlobalPosition = at + new Vector3(0, 1.0f, 0);
        orb.Scale = Vector3.One * 0.3f;
        var tw = orb.CreateTween();
        tw.TweenProperty(orb, "scale", Vector3.One * Mathf.Max(0.4f, radius * 0.4f), 0.12f);
        tw.TweenProperty(orb, "scale", Vector3.One * 0.05f, 0.18f);
        tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(orb)) orb.QueueFree(); }));
        VfxRing(at, col, radius, 0.4f);
    }
    // a strand of blood drawn from an enemy toward the witch (Exsanguinate). Streams inward then fades.
    public void VfxBloodTether(Vector3 from, Vector3 dir, float dist, Color col)
    {
        if (dir.LengthSquared() < 0.001f || dist < 0.5f) return;
        dir = dir.Normalized();
        Vector3 to = from + dir * dist;
        var n = new Node3D();
        AddChild(n);
        n.GlobalPosition = (from + to) * 0.5f + new Vector3(0, 1.0f, 0);
        n.LookAt(n.GlobalPosition + dir, Vector3.Up);   // local -Z faces the witch
        var mat = ToonEmissive(col, 3.0f, 0.04f);
        mat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        var mi = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.26f, 0.26f, dist) }, MaterialOverride = mat };
        n.AddChild(mi);
        // a bright bead of blood that travels along the strand toward the witch
        var bead = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.42f, Height = 0.84f }, MaterialOverride = ToonEmissive(col, 3.6f, 0.05f) };
        bead.Position = new Vector3(0, 0, dist * 0.5f);   // start at the enemy end (+Z), stream to -Z (witch)
        n.AddChild(bead);
        var tw = n.CreateTween();
        tw.TweenInterval(0.12f);                          // appear & hold so the strand is readable
        tw.SetParallel(true);
        tw.TweenProperty(bead, "position", new Vector3(0, 0, -dist * 0.5f), 0.6f);   // bead pulled to the witch
        tw.TweenProperty(mi, "scale", new Vector3(1f, 1f, 0.06f), 0.6f);             // strand draws inward
        tw.TweenProperty(mi, "transparency", 1f, 0.62f);
        tw.SetParallel(false);
        tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(n)) n.QueueFree(); }));
    }

    public void VfxLash(Vector3 origin, Vector3 fwd, Color tint)
    {
        if (fwd.LengthSquared() < 0.001f) fwd = Vector3.Forward;
        var right = new Vector3(fwd.Z, 0, -fwd.X).Normalized();
        float reach = 7f;
        for (int s = 0; s < 3; s++)
        {
            var blade = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(reach * 1.25f, 0.16f, 0.42f) } };
            blade.MaterialOverride = ToonEmissive(tint.Lerp(Colors.White, 0.15f), 2.4f, 0.03f);
            AddChild(blade);
            float lat = (GD.Randf() - 0.5f) * 3.2f, vert = 0.6f + GD.Randf() * 1.6f, depth = reach * (0.35f + GD.Randf() * 0.45f);
            blade.GlobalPosition = origin + fwd * depth + right * lat + new Vector3(0, vert, 0);
            float roll = (GD.Randf() - 0.5f) * Mathf.Pi, pitch = (GD.Randf() - 0.5f) * 0.7f;
            blade.Rotation = new Vector3(pitch, Mathf.Atan2(fwd.X, fwd.Z), roll);
            var tw = blade.CreateTween();
            tw.TweenProperty(blade, "scale", new Vector3(0.05f, 1f, 1f), 0.18f);
            tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(blade)) blade.QueueFree(); }));
        }
    }

    public void VfxEclipseAura(Vector3 at, float dur)
    {
        var ev = new EclipseVfx { Dur = dur, MaxDur = dur };
        AddChild(ev);
        ev.GlobalPosition = new Vector3(at.X, at.Y + 26f, at.Z);
    }
    public void VfxEclipseBurst(Vector3 at, float radius)
    {
        var b = new EclipseBurst { Radius = radius, Dmg = 0f, Remote = true };
        AddChild(b);
        b.GlobalPosition = at;
    }
    public void VfxBloodWaveGhost(Vector3 at, Vector3 dir, float width, float range)
    {
        var w = new BloodWave { Remote = true, Dir = dir, Width = width, Speed = 22f, Range = range, Dmg = 0f };
        AddChild(w);
        w.GlobalPosition = at;
    }
    public void VfxDome(Vector3 at, float radius, float dur, Color col)
    {
        var dome = new MeshInstance3D { Mesh = new SphereMesh { Radius = radius, Height = radius * 2f } };
        dome.MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = new Color(col.R, col.G, col.B, 0.16f),
            EmissionEnabled = true, Emission = col, EmissionEnergyMultiplier = 0.8f,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled
        };
        AddChild(dome);
        dome.GlobalPosition = new Vector3(at.X, 0.1f, at.Z);
        dome.GetTree().CreateTimer(Mathf.Max(0.2f, dur)).Timeout += () => { if (GodotObject.IsInstanceValid(dome)) dome.QueueFree(); };
    }
    public void VfxUltBurst(Vector3 at, float radius, Color col)
    {
        var v = new Vfx(); AddChild(v);
        v.GlobalPosition = at + new Vector3(0, 1f, 0);
        v.Init(new SphereMesh { Radius = Mathf.Max(1f, radius * 0.5f), Height = Mathf.Max(2f, radius) }, col, 0.5f, 6f);
        VfxRing(at, col, Mathf.Max(3f, radius), 0.6f);
    }

    public void VfxLance(Vector3 at, float dur, float scale)
    {
        var col = DamageTypes.Col(DamageType.Holy);
        var outer = ToonEmissive(col, 1.7f, 0.04f);
        var core = ToonEmissive(col.Lerp(Colors.White, 0.7f), 2.6f, 0f);
        var n = new Node3D();
        void Add(Mesh m, Material mat, Vector3 pos)
        { var mi = new MeshInstance3D { Mesh = m, MaterialOverride = mat }; mi.Position = pos; n.AddChild(mi); }
        float s = scale;
        Add(new CylinderMesh { TopRadius = 0.22f * s, BottomRadius = 0f, Height = 1.5f * s }, outer, new Vector3(0, 0.75f * s, 0));
        Add(new CylinderMesh { TopRadius = 0.07f * s, BottomRadius = 0.12f * s, Height = 4.6f * s }, outer, new Vector3(0, 3.6f * s, 0));
        Add(new CylinderMesh { TopRadius = 0.04f * s, BottomRadius = 0.04f * s, Height = 6.2f * s }, core, new Vector3(0, 3.1f * s, 0));
        Add(new BoxMesh { Size = new Vector3(1.1f * s, 0.16f * s, 0.22f * s) }, outer, new Vector3(0, 1.7f * s, 0));
        Add(new BoxMesh { Size = new Vector3(0.22f * s, 0.16f * s, 1.1f * s) }, outer, new Vector3(0, 1.7f * s, 0));
        Add(new SphereMesh { Radius = 0.2f * s, Height = 0.4f * s }, core, new Vector3(0, 6.0f * s, 0));
        AddChild(n);
        n.Position = new Vector3(at.X, at.Y + 13f, at.Z);
        var tw = n.CreateTween();
        tw.TweenProperty(n, "position", new Vector3(at.X, at.Y, at.Z), 0.16f).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
        tw.TweenInterval(dur);
        tw.TweenProperty(n, "scale", new Vector3(0.01f, 0.01f, 0.01f), 0.35f);
        tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(n)) n.QueueFree(); }));
    }

    public static Node3D AddBeacon(Node3D parent, Color col)
    {
        var holder = new Node3D();
        parent.AddChild(holder);
        float h = 42f;
        var beam = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.12f, BottomRadius = 0.55f, Height = h } };
        beam.MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = new Color(col.R, col.G, col.B, 0.20f),
            EmissionEnabled = true,
            Emission = col,
            EmissionEnergyMultiplier = 2.4f,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled
        };
        beam.Position = new Vector3(0, h / 2f + 0.5f, 0);
        holder.AddChild(beam);
        holder.AddChild(new OmniLight3D { OmniRange = 16f, LightColor = col, LightEnergy = 2.0f, Position = new Vector3(0, 3f, 0) });
        return holder;
    }

    // Inverted-hull ink outline (used as a next_pass for the cel-shaded look).
    public static StandardMaterial3D Outline(float amt = 0.03f, Color? col = null)
    {
        return new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = col ?? new Color(0.03f, 0.025f, 0.04f),
            CullMode = BaseMaterial3D.CullModeEnum.Front,
            Grow = true,
            GrowAmount = amt
        };
    }

    // Cel-shaded matte surface: banded toon lighting + rim + ink outline.
    public static StandardMaterial3D Toon(Color albedo, float rough = 0.95f, float rim = 0.3f, float outline = 0.03f)
    {
        var m = new StandardMaterial3D
        {
            AlbedoColor = albedo,
            Roughness = rough,
            Metallic = 0f,
            DiffuseMode = BaseMaterial3D.DiffuseModeEnum.Toon,
            SpecularMode = BaseMaterial3D.SpecularModeEnum.Toon
        };
        if (rim > 0f) { m.RimEnabled = true; m.Rim = rim; m.RimTint = 0.35f; }
        if (outline > 0f) m.NextPass = Outline(outline);
        return m;
    }

    // Cel-shaded glowing surface (magic, characters): toon base + emission + outline.
    public static StandardMaterial3D ToonEmissive(Color c, float energy, float outline = 0.03f)
    {
        var m = Toon(new Color(c.R * 0.5f, c.G * 0.5f, c.B * 0.5f), 0.85f, 0.25f, outline);
        m.EmissionEnabled = true;
        m.Emission = c;
        m.EmissionEnergyMultiplier = 1.0f + energy * 1.6f;
        return m;
    }

    // Attach an x-ray silhouette to any friendly entity (minions now, future friendlies later) so it
    // reads through walls and crowds — same treatment ally avatars get. Returns the overlay mesh.
    public static MeshInstance3D AddFriendlySilhouette(Node3D parent, Color col, float radius = 0.45f, float height = 1.6f, float yOff = 0.9f)
    {
        var sil = new MeshInstance3D { Mesh = new CapsuleMesh { Radius = radius, Height = height } };
        sil.Position = new Vector3(0, yOff, 0);
        sil.MaterialOverride = new StandardMaterial3D
        {
            AlbedoColor = new Color(col.R, col.G, col.B, 0.4f),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            EmissionEnabled = true, Emission = col, EmissionEnergyMultiplier = 1.3f,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            NoDepthTest = true, RenderPriority = 8   // always drawn on top
        };
        parent.AddChild(sil);
        return sil;
    }

    // ---- waves ----
    // The enemy director: after each wave it judges how the party coped (clear speed, lowest health, downs,
    // over-leveling) and nudges Heat. Heat then drives spawn density, elite/affix odds, enemy HP/damage, and
    // composition in NextWave/SpawnEnemy. Bounded [0.85, 1.6] so it never trivializes or hard-walls the run.
    private void AssessDirector()
    {
        float clear = _waveTimer;
        bool healthy = _waveMinHpFrac > 0.55f && !_downThisWave;
        bool struggled = _downThisWave || _waveMinHpFrac < 0.25f;
        float step;
        if (struggled) step = -0.12f;                         // back off — they're hurting
        else if (clear < 16f && healthy) step = 0.10f;        // stomped it untouched — ramp up
        else if (clear > 42f) step = -0.06f;                  // dragging — ease a touch
        else if (clear < 26f && healthy) step = 0.04f;        // comfortable — drift up
        else step = 0f;                                       // about right — hold
        if (Player != null && Player.Level > Wave + 4) step += 0.03f;   // over-leveled relative to depth

        float old = Heat;
        Heat = Mathf.Clamp(Heat + step, 0.85f, 1.6f);
        if (Heat - old > 0.07f) Hud?.Banner("the grove grows restless");
        else if (old - Heat > 0.07f) Hud?.Banner("the grove settles");
        _waveMinHpFrac = 1f; _downThisWave = false;           // reset for the next wave
    }

    private void NextWave()
    {
        Wave++;
        if (Wave % 10 == 0) SpawnRoulette();   // ~1 wheel of fortune every 10 waves (capped at 3, spaced)
        if (Player != null && Player.DivineWitch && Wave > 1 && Wave % 10 == 1)
            Player.Interventions = Mathf.Min(2, Player.Interventions + 1);   // refreshes each 10-wave cycle
        var list = new List<string>();
        float cm = (1f + 0.55f * (WardenCount - 1)) * Heat;   // bodies per warden, amplified by the director's Heat
        void add(string t, int n) { int c = Mathf.Max(n > 0 ? 1 : 0, Mathf.RoundToInt(n * cm)); for (int i = 0; i < c; i++) list.Add(t); }

        add("shade", 5 + Mathf.FloorToInt(Wave * 1.8f));
        add("swarmer", 6 + Mathf.FloorToInt(Wave * 2.2f));   // (NEW) big shambling zombie horde in endless mode
        add("wisp", Mathf.Max(0, Mathf.FloorToInt(Wave * 1.1f)));
        add("brute", Mathf.FloorToInt(Wave * 0.55f));
        if (Wave >= 2) add("caster", Mathf.FloorToInt((Wave - 1) * 0.8f));
        if (Wave >= 2) add("flyer", Mathf.FloorToInt((Wave - 1) * 0.7f));
        if (Wave >= 3) add("sieger", Mathf.FloorToInt((Wave - 2) * 0.4f));
        if (Wave >= 3) add("healer", Mathf.Min(4, Mathf.FloorToInt((Wave - 2) * 0.4f)));
        if (Wave >= 4) add("zapper", Mathf.Min(5, Mathf.FloorToInt((Wave - 3) * 0.5f)));
        if (Wave >= 5) add("bomber", Mathf.Min(6, Mathf.FloorToInt((Wave - 4) * 0.65f)));
        if (Wave >= 3) add("diver", Mathf.FloorToInt((Wave - 2) * 0.5f));
        if (Wave >= 3) add("splitter", Mathf.FloorToInt((Wave - 2) * 0.45f));
        if (Wave >= 4) add("sentinel", Mathf.Min(3, Mathf.FloorToInt((Wave - 3) * 0.4f)));
        if (Wave >= 4) add("hexer", Mathf.Min(3, Mathf.FloorToInt((Wave - 3) * 0.35f)));
        if (Wave >= 4) add("wardbane", Mathf.Min(3, Mathf.FloorToInt((Wave - 3) * 0.4f)));
        if (Wave >= 5) add("totem", Mathf.Min(2, Mathf.FloorToInt((Wave - 4) * 0.34f)));

        // director "smart" composition: a hot run gets extra pressure units (ranged/divers/hexers), not just more trash
        if (Heat > 1.12f && Wave >= 2) { add("caster", 1); add("diver", 1); }
        if (Heat > 1.30f && Wave >= 4) add("hexer", 1);

        var rng = new RandomNumberGenerator(); rng.Randomize();
        for (int i = list.Count - 1; i > 0; i--) { int j = rng.RandiRange(0, i); (list[i], list[j]) = (list[j], list[i]); }
        if (Wave % 10 == 0)   // boss wave: the DPS director trickles these in groups (not a chaotic all-at-once dump)
        {
            _bossAddPool = new System.Collections.Generic.List<string>(list);
            _bossAddGroup = 4 + WardenCount * 2; _bossDpsInit = false; _bossDmgAccum = 0f; _bossPrevDps = 0f; BossRecentDps = 0f;
        }
        else foreach (var t in list) _toSpawn.Enqueue(t);

        // mini-boss every 5th wave, full boss every 10th (boss spawns adds while alive)
        if (Wave % 10 == 0) SpawnEnemy("boss");
        else if (Wave % 5 == 0) SpawnEnemy("miniboss");

        // rare loot goblin
        if (Goblin == null && rng.Randf() < 0.14f) SpawnGoblin();

        // ritual events (grant spell-combo finishers). Steady cadence so endless always has a mana sink / boon source.
        if (Wave >= 2 && Rituals.Count == 0)
        {
            int block = Wave / 4;                            // shorter blocks → more chances
            if (block != _eventBlock) { _eventBlock = block; _eventsThisBlock = 0; }
            float chance = Wave <= 8 ? 0.8f : 0.65f;         // generous early, still frequent in endless
            int perBlock = 2;                                // up to 2 per 4-wave block at every stage
            if (_eventsThisBlock < perBlock && rng.Randf() < chance) SpawnRitual();
        }

        Hud?.Banner(Wave % 10 == 0 ? "THE HOLLOW MOON" : $"Wave {Wave}");
    }

    // spawn an enemy at a specific spot (splitter children) — host only; children sync via the normal snapshot
    public void SpawnEnemyAt(string type, Vector3 pos)
    {
        var e = new Enemy();
        e.Configure(type, Wave);
        AddChild(e);
        e.NetId = _netEnemySeq++;
        e.TypeIdx = EnemyKinds.Index(type);
        var off = new Vector3((float)GD.RandRange(-1.2, 1.2), 0, (float)GD.RandRange(-1.2, 1.2));
        e.GlobalPosition = new Vector3(pos.X + off.X, e.Radius, pos.Z + off.Z);
        Enemies.Add(e);
        SpawnPoof(e.GlobalPosition);
    }

    // exact-position spawn (respects Y so a high spawn drops to the ground via the normal ground-follow) — dev spawnfoe
    public void SpawnEnemyAtExact(string type, Vector3 pos)
    {
        var e = new Enemy();
        e.Configure(type, Wave);
        AddChild(e);
        e.NetId = _netEnemySeq++;
        e.TypeIdx = EnemyKinds.Index(type);
        e.GlobalPosition = new Vector3(pos.X, Mathf.Max(pos.Y, e.Radius), pos.Z);
        Enemies.Add(e);
        SpawnPoof(e.GlobalPosition);
    }

    // maze spawn: rolls elite/affix BEFORE AddChild so the ring + affix visuals build in _Ready
    private Enemy SpawnMazeEnemy(string type, Vector3 pos)
    {
        var e = new Enemy();
        e.Configure(type, Wave);
        if (_mazeRng.Randf() < 0.05f + (Heat - 1f) * 0.12f) e.MakeElite();
        else if (_mazeRng.Randf() < 0.10f + (Heat - 1f) * 0.15f) e.MakeAffix(_mazeRng.RandiRange(1, 5));
        AddChild(e);
        e.NetId = _netEnemySeq++;
        e.TypeIdx = EnemyKinds.Index(type);
        e.GlobalPosition = new Vector3(pos.X, e.Radius, pos.Z);
        Enemies.Add(e);
        SpawnPoof(e.GlobalPosition);
        return e;
    }

    // boss-wave adds arrive as a clustered SECTION off to one side (not scattered), drawn from the wave's roster
    private void SpawnBossAddGroup(int count)
    {
        if (_bossAddPool.Count == 0) _bossAddPool = new System.Collections.Generic.List<string> { "shade", "swarmer", "caster", "flyer", "brute" };
        var sc = Player != null ? Player.GlobalPosition : Vector3.Zero;
        float a = _rng.RandfRange(0, Mathf.Tau);
        var center = new Vector3(sc.X + Mathf.Cos(a) * 40f, 0f, sc.Z + Mathf.Sin(a) * 40f);
        for (int i = 0; i < count; i++)
        {
            string t = _bossAddPool[_rng.RandiRange(0, _bossAddPool.Count - 1)];
            var e = new Enemy(); e.Configure(t, Wave);
            if (t != "totem" && t != "spawnling" && _rng.Randf() < 0.10f) e.MakeElite();   // occasional elite for spice
            AddChild(e); e.NetId = _netEnemySeq++; e.TypeIdx = EnemyKinds.Index(t);
            var off = new Vector3(_rng.RandfRange(-6f, 6f), 0f, _rng.RandfRange(-6f, 6f));
            var pos = center + off;
            e.GlobalPosition = new Vector3(pos.X, e.Radius, pos.Z);
            e.WakeSilent();
            Enemies.Add(e);
            SpawnPoof(e.GlobalPosition);
        }
    }

    // damage dealt to any enemy while the boss lives → feeds the DPS director + boss heat
    public void NoteEnemyDamage(float dmg) { if (_boss != null && GodotObject.IsInstanceValid(_boss) && !_boss.Dead) _bossDmgAccum += dmg; }

    private void SpawnEnemy(string type)
    {
        var e = new Enemy();
        e.Configure(type, Wave);

        bool boss = type == "boss" || type == "miniboss";
        if (!boss && type != "goblin")
        {
            float eliteChance = 0.08f + Wave * 0.004f + (Heat - 1f) * 0.12f;   // director pushes more elites when hot
            if (_rng.Randf() < Mathf.Min(0.32f, eliteChance)) e.MakeElite();
        }
        if (!boss && type != "goblin" && type != "totem" && type != "spawnling")
        {
            float affChance = Mathf.Min(0.4f, 0.08f + Wave * 0.012f + (Heat - 1f) * 0.14f);   // and more affixes
            if (_rng.Randf() < affChance) e.MakeAffix(_rng.RandiRange(1, 5));
        }

        AddChild(e);
        e.NetId = _netEnemySeq++;
        e.TypeIdx = EnemyKinds.Index(type);
        var rng = new RandomNumberGenerator(); rng.Randomize();
        float a = rng.RandfRange(0, Mathf.Tau);
        float r = 44f + rng.RandfRange(2, 12);
        var sc = Player != null ? Player.GlobalPosition : Vector3.Zero;
        e.GlobalPosition = new Vector3(sc.X + Mathf.Cos(a) * r, e.Radius, sc.Z + Mathf.Sin(a) * r);
        e.WakeSilent();   // (NEW) wave-spawned swarmers hunt immediately (idle only applies inside the maze)
        Enemies.Add(e);
        if (!boss) SpawnPoof(e.GlobalPosition);   // (NEW) purple materialization poof (boss gets a dramatic entrance, no poof)
        if (type == "boss") { _boss = e; _bossAddT = 5f; }
    }

    private void SpawnGoblin()
    {
        var g = new Enemy();
        g.Configure("goblin", Wave);
        if (_rng.Randf() < 0.22f) g.MakeElite();   // rarer elite goblin → epic+ loot
        AddChild(g);
        g.NetId = _netEnemySeq++;
        g.TypeIdx = EnemyKinds.Index("goblin");
        var rng = new RandomNumberGenerator(); rng.Randomize();
        float a = rng.RandfRange(0, Mathf.Tau);
        var gc = Player != null ? Player.GlobalPosition : Vector3.Zero;
        float gr = 30f + rng.RandfRange(0, 8);
        g.GlobalPosition = new Vector3(gc.X + Mathf.Cos(a) * gr, g.Radius, gc.Z + Mathf.Sin(a) * gr);
        Enemies.Add(g);
        Goblin = g;
        GoblinTime = -1f;   // paused — the chase clock only starts once you land a hit
    }

    public void GoblinLoot(bool elite)
    {
        _lootLeft = 2;
        _lootMin = elite ? Rarity.Epic : Rarity.Rare;
        _pendingLevels += 2;
        Goblin = null;
        if (State == GameState.Playing)
        {
            Choices = RollChoices();
            ChoiceGen++; RarityCue(Choices);
            State = GameState.LevelUp;
            Input.MouseMode = Input.MouseModeEnum.Visible;
            SelectLock = 0.3f;
        }
    }

    private bool _ultOffered = false;
    public readonly System.Collections.Generic.List<Chest> Chests = new();
    public readonly System.Collections.Generic.List<Orb> Orbs = new();
    private int _netPickupSeq = 1;
    public int NextPickupId() => _netPickupSeq++;
    public bool ChestPick = false;   // a card pick that came from a chest — does NOT pause others
    private float _chestT = 7f;
    private RouletteMachine _roulette;
    private readonly System.Collections.Generic.List<RouletteMachine> _roulettes = new();
    private bool _rouletteActive = false;
    public int RoulettePull => (_roulette != null && GodotObject.IsInstanceValid(_roulette)) ? _roulette.Pulls : 0;

    private List<UpgradeCard> RollChoices()
    {
        float savedLuck = Player.S.Luck;
        if (_luckRerollNext) Player.S.Luck *= 2f;   // (NEW) luck-reroll: double luck for THIS roll only (stat itself untouched)
        List<UpgradeCard> list =
            _rewardLeft > 0 ? UpgradePool.RollCategory(Player, _rng, _rewardCat, 3) :
            _lootLeft > 0 ? UpgradePool.RollFiltered(Player, _rng, _lootMin, 3) :
            UpgradePool.RollThree(Player, _rng);
        // Divine Witch: guaranteed legendary upgrade every 10 levels
        if (Player.DivineWitch && Player.Level > 0 && Player.Level % 10 == 0
            && !list.Exists(c => c.Rarity == Rarity.Legendary) && list.Count > 0)
        {
            var leg = UpgradePool.RollOneLegendary(Player, _rng);
            if (leg != null) list[_rng.RandiRange(0, list.Count - 1)] = leg;
        }
        // chance to inject an ultimate-modification (only once an ult is equipped), with a cooldown so a
        // burst of level-ups (right after binding an ult, or a boss XP dump) can't keep flooding it
        if (Player.Ult != Player.UltKind.None && list.Count > 0 && _ultModCd <= 0 && _rng.Randf() < 0.12f)
        {
            var mod = UpgradePool.UltModCard(Player);
            if (mod != null) { list[_rng.RandiRange(0, list.Count - 1)] = mod; _ultModCd = 6; }
        }
        if (_ultModCd > 0) _ultModCd--;
        Player.S.Luck = savedLuck; _luckRerollNext = false;   // (NEW) restore luck; consume the luck-reroll
        return list;
    }

    // ===== level-up card actions: reroll / luck-reroll / disable (ban) =====
    public void RerollChoices()
    {
        if (State != GameState.LevelUp || Choices == null) return;
        if (Gold < RerollCost) { Hud?.Banner("not enough gold"); return; }
        Gold -= RerollCost; SaveGold();
        Choices = RollChoices(); ChoiceGen++; RarityCue(Choices); Sfx?.Clink();
    }
    public void LuckRerollChoices()
    {
        if (State != GameState.LevelUp || Choices == null) return;
        if (Gold < LuckRerollCost) { Hud?.Banner("not enough gold"); return; }
        Gold -= LuckRerollCost; SaveGold(); LuckRerollCount = Mathf.Min(LuckRerollCount + 1, 12);
        _luckRerollNext = true;
        Choices = RollChoices(); ChoiceGen++; RarityCue(Choices); Sfx?.Clink();
    }
    public void BuyPick2()
    {
        if (State != GameState.LevelUp || Choices == null || _pick2Extra > 0) return;
        if (Choices.Count < 2) return;
        if (Gold < Pick2Cost) { Hud?.Banner("not enough gold"); return; }
        Gold -= Pick2Cost; SaveGold(); Pick2Count = Mathf.Min(Pick2Count + 1, 12);
        _pick2Extra = 1;   // this roll now grants ONE extra pick (2 total)
        Sfx?.Clink(); Hud?.Banner("PICK TWO");
    }
    public void BanChoice(int idx)
    {
        if (State != GameState.LevelUp || Choices == null || idx < 0 || idx >= Choices.Count) return;
        var card = Choices[idx];
        if (card.Unique) { Hud?.Banner("can't disable a unique card"); return; }
        if (Gold < BanCost) { Hud?.Banner("not enough gold"); return; }
        Gold -= BanCost; SaveGold(); BanCount = Mathf.Min(BanCount + 1, 12);
        UpgradePool.Banned.Add(card.Title);   // whole rarity hierarchy (banned by Title) — resets each new game
        Choices = RollChoices(); ChoiceGen++; RarityCue(Choices); Sfx?.Clink();
    }

    public void AddGold(int amt)
    {
        if (amt < 1) amt = 1;
        Gold += amt; LastWaveGold = amt; GoldFlash = 3f; SaveGold();
    }

    // ---- roulette ----
    public void OpenRoulette(RouletteMachine m)
    {
        _roulette = m; _rouletteActive = true;
        State = GameState.Roulette;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        SelectLock = 0.3f;
    }

    private void DoRoulettePull()
    {
        if (_roulette == null || !GodotObject.IsInstanceValid(_roulette)) { EndRoulette(); return; }
        int pull = _roulette.Pulls;
        if (pull >= 3) { EndRoulette(); return; }
        int cost = Mathf.Max(1, Mathf.FloorToInt(Gold * (pull + 1) * 0.10f));
        if (Gold < cost) { Hud?.Banner("not enough gold"); return; }
        Gold -= cost; SaveGold();
        float legChance = pull == 0 ? 0.05f : pull == 1 ? 0.10f : 0.15f;
        _roulette.Pulls++;
        Choices = UpgradePool.RollRoulette(Player, _rng, legChance);
        _pendingLevels++;
        ChoiceGen++; RarityCue(Choices);
        State = GameState.LevelUp;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        SelectLock = 0.3f;
    }

    private void EndRoulette()
    {
        _rouletteActive = false;
        if (_roulette != null && GodotObject.IsInstanceValid(_roulette)) { _roulettes.Remove(_roulette); _roulette.QueueFree(); }
        _roulette = null;
        State = GameState.Playing;
        Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    // ===== wandering vendors (Mystic = re-attune for gold; Scroll = pick up finishers/mods you lack) =====
    private Mystic _mystic;
    private ScrollVendor _scroll;
    public Mystic VendorMystic => _mystic;          // for the minimap
    public ScrollVendor VendorScroll => _scroll;
    public Mystic CurMystic => _mystic;
    public ScrollVendor CurScroll => _scroll;
    public Mystic RemoteMystic { set { _mystic = value; } }
    public ScrollVendor RemoteScroll { set { _scroll = value; } }
    public void HostClaimVendor(int netId)   // a client used a vendor; consume it for everyone
    {
        if (_mystic != null && GodotObject.IsInstanceValid(_mystic) && _mystic.NetId == netId) { _mystic.QueueFree(); _mystic = null; return; }
        if (_scroll != null && GodotObject.IsInstanceValid(_scroll) && _scroll.NetId == netId) { _scroll.QueueFree(); _scroll = null; return; }
    }
    public System.Collections.Generic.List<RouletteMachine> RouletteList => _roulettes;
    public void AddRemoteRoulette(RouletteMachine m) => _roulettes.Add(m);
    public void RemoveRemoteRoulette(RouletteMachine m) => _roulettes.Remove(m);
    public void HostClaimRoulette(int netId)   // a client took a wheel; consume it for everyone
    {
        for (int i = 0; i < _roulettes.Count; i++)
        {
            var r = _roulettes[i];
            if (r != null && GodotObject.IsInstanceValid(r) && r.NetId == netId) { _roulettes.RemoveAt(i); r.QueueFree(); return; }
        }
    }
    private int _lastMysticLvl = -1, _lastScrollLvl = -1;
    private bool _mysticAttune = false;
    public System.Collections.Generic.List<FinType> ScrollFins = new();
    public System.Collections.Generic.List<ModType> ScrollMods = new();
    public const int MysticCost = 100;

    // ---- finisher key binding ----
    private int _bindIdx = -1;
    private int _ultModCd = 0;   // level-ups before the ult-mod card may be offered again (prevents post-ult / level-burst flooding)
    private bool _bindFromOptions = false;
    private bool _bindLevelFlow = false;
    public int BindIdx => _bindIdx;

    private void OpenBindPrompt(int idx, bool fromOptions, bool levelFlow)
    {
        _bindIdx = idx; _bindFromOptions = fromOptions; _bindLevelFlow = levelFlow;
        State = GameState.BindKey; Input.MouseMode = Input.MouseModeEnum.Visible; SelectLock = 0.25f;
    }
    private void FinishBind()
    {
        if (_bindFromOptions) { _bindFromOptions = false; _bindIdx = -1; State = GameState.Pause; SelectLock = 0.2f; return; }
        bool lf = _bindLevelFlow; _bindIdx = -1;
        if (lf) FinishStep();
        else { State = GameState.Playing; Input.MouseMode = Input.MouseModeEnum.Captured; }
    }
    public void RebindFinisher(int idx) => OpenBindPrompt(idx, true, false);   // from the pause/options menu

    // core controls a spell-combo may NOT be bound to (would clash with movement/dash/jump/interact/menus)
    private static bool IsReservedKey(Key k) => k is Key.W or Key.A or Key.S or Key.D
        or Key.Up or Key.Down or Key.Left or Key.Right or Key.Shift or Key.Space or Key.Tab or Key.E or Key.Escape;

    // equip a finisher, prompting for a key bind if it went into a fresh slot
    private void EquipFinisherPrompt(FinType t, int every, float pow, Rarity r, bool levelFlow)
    {
        int before = Player.Fin.Count;
        Player.EquipFinisher(t, every, pow, r);
        if (Player.Fin.Count > before) OpenBindPrompt(Player.Fin.Count - 1, false, levelFlow);
        else if (levelFlow) FinishStep();
        else { State = GameState.Playing; Input.MouseMode = Input.MouseModeEnum.Captured; }
    }

    private void VendorSpawnChecks()
    {
        if (!IsAuthority) return;   // vendors are host-driven; clients don't spawn their own
        int lv = Player != null ? Player.Level : 1;
        if (lv % 10 == 0 && lv != _lastMysticLvl) { _lastMysticLvl = lv; SpawnMystic(); }
        if (lv % 10 == 5 && lv != _lastScrollLvl) { _lastScrollLvl = lv; SpawnScroll(); }
    }
    // pick a spot near `around` (ring minD..maxD) on DRY ground, anchored to the terrain surface. Replaces the old
    // hardcoded Y=0, which made vendors/mystics/chests clip hills or float over water on the new heightmap. (NEW)
    private Vector3 GroundedDrySpawn(Vector3 around, float minD, float maxD)
    {
        Vector3 best = around; float bestY = -9999f;
        for (int i = 0; i < 14; i++)
        {
            float a = _rng.RandfRange(0, Mathf.Tau), d = _rng.RandfRange(minD, maxD);
            var p = new Vector3(around.X + Mathf.Cos(a) * d, 0, around.Z + Mathf.Sin(a) * d);
            float gy = SurfaceHeight(p, 1e9f);
            if (gy >= World.WaterLevel + 0.2f) return new Vector3(p.X, gy, p.Z);   // dry ground — take it
            if (gy > bestY) { bestY = gy; best = new Vector3(p.X, Mathf.Max(gy, World.WaterLevel + 0.2f), p.Z); }
        }
        return best;   // every try was over water → driest spot found, lifted to the shoreline so it never floats on open water
    }
    private Vector3 VendorSpawnPos()
    {
        var pc = Player != null ? Player.GlobalPosition : Vector3.Zero;
        return GroundedDrySpawn(pc, 55f, 120f);   // terrain-anchored + kept off the water (NEW)
    }
    private void SpawnMystic()
    {
        if (_mystic != null && GodotObject.IsInstanceValid(_mystic)) return;   // only one at a time
        var m = new Mystic { NetId = NextPickupId() }; AddChild(m); m.GlobalPosition = VendorSpawnPos(); _mystic = m;
        Hud?.Banner("a mystic wanders the grove\u2026");
    }
    private void SpawnScroll()
    {
        if (_scroll != null && GodotObject.IsInstanceValid(_scroll)) return;
        var s = new ScrollVendor { NetId = NextPickupId() }; AddChild(s); s.GlobalPosition = VendorSpawnPos(); _scroll = s;
        Hud?.Banner("a scroll-keeper appears\u2026");
    }

    public void OpenMystic(Mystic m)
    {
        if (m != null && GodotObject.IsInstanceValid(m)) m.QueueFree();
        _mystic = null;
        State = GameState.Mystic; Input.MouseMode = Input.MouseModeEnum.Visible; SelectLock = 0.3f;
    }
    public void MysticBuy(int slot)   // 0 = left, 1 = right
    {
        if (Gold < MysticCost) { GoldFlash = 1.5f; return; }
        Gold -= MysticCost; SaveGold();
        PendingAttune = slot; _mysticAttune = true;
        State = GameState.Element; Input.MouseMode = Input.MouseModeEnum.Visible; ChoiceGen++; SelectLock = 0.3f;
    }
    public void CloseMystic() { State = GameState.Playing; Input.MouseMode = Input.MouseModeEnum.Captured; }

    public void OpenScroll(ScrollVendor s)
    {
        if (s != null && GodotObject.IsInstanceValid(s)) s.QueueFree();
        _scroll = null;
        BuildScrollOffer();
        State = GameState.Scroll; Input.MouseMode = Input.MouseModeEnum.Visible; SelectLock = 0.3f;
    }
    private void BuildScrollOffer()
    {
        ScrollFins.Clear(); ScrollMods.Clear();
        foreach (FinType t in System.Enum.GetValues(typeof(FinType)))
            if (t != FinType.Crescendo && t != FinType.Fullmod && !Player.OwnsFinisher(t)) ScrollFins.Add(t);
        foreach (ModType t in System.Enum.GetValues(typeof(ModType)))
            if (!Player.OwnsModifier(t)) ScrollMods.Add(t);
        while (ScrollFins.Count > 4) ScrollFins.RemoveAt(_rng.RandiRange(0, ScrollFins.Count - 1));
        while (ScrollMods.Count > 4) ScrollMods.RemoveAt(_rng.RandiRange(0, ScrollMods.Count - 1));
    }
    public void ScrollPick(int idx)
    {
        int nf = ScrollFins.Count;
        if (idx < nf)
        {
            var t = ScrollFins[idx];
            if (Player.OwnsFinisher(t) || !Player.FinisherFull) { EquipFinisherPrompt(t, 6, 1.1f, Rarity.Rare, false); }
            else { SwapIsFin = true; _swFin = t; _swEvery = 6; _swPow = 1.1f; _swRar = Rarity.Rare; State = GameState.Swap; Input.MouseMode = Input.MouseModeEnum.Visible; ChoiceGen++; SelectLock = 0.3f; }
        }
        else
        {
            int mi = idx - nf;
            if (mi < 0 || mi >= ScrollMods.Count) return;
            var t = ScrollMods[mi];
            if (Player.OwnsModifier(t) || !Player.ModifierFull) { Player.EquipModifier(t, 2f, Rarity.Rare); CloseScroll(); }
            else { SwapIsFin = false; _swMod = t; _swMag = 2f; _swRar = Rarity.Rare; State = GameState.Swap; Input.MouseMode = Input.MouseModeEnum.Visible; ChoiceGen++; SelectLock = 0.3f; }
        }
    }
    public void CloseScroll() { State = GameState.Playing; Input.MouseMode = Input.MouseModeEnum.Captured; }

    private void SpawnRoulette()
    {
        if (!IsAuthority) return;   // clients receive roulettes via snapshot
        _roulettes.RemoveAll(r => r == null || !GodotObject.IsInstanceValid(r));
        if (_roulettes.Count >= 3) return;
        var pc = Player != null ? Player.GlobalPosition : Vector3.Zero;
        Vector3 pos = Vector3.Zero; bool ok = false;
        for (int attempt = 0; attempt < 10 && !ok; attempt++)
        {
            float a = _rng.RandfRange(0, Mathf.Tau); float d = _rng.RandfRange(80f, 170f);
            pos = new Vector3(pc.X + Mathf.Cos(a) * d, 0, pc.Z + Mathf.Sin(a) * d);
            ok = true;
            foreach (var r in _roulettes)
                if (r != null && GodotObject.IsInstanceValid(r) && r.GlobalPosition.DistanceTo(pos) < 60f) { ok = false; break; }
        }
        var m = new RouletteMachine { NetId = NextPickupId() };
        AddChild(m);
        m.GlobalPosition = pos;
        _roulettes.Add(m);
        Hud?.Banner("a wheel of fortune stirs somewhere\u2026");
    }

    // ---- ultimate menus ----
    public int UltUpgradeCost => Player.UltTier + 1;   // 1,2,3,4 tokens per tier
    public void OpenUltChoice() { State = GameState.Ult; Input.MouseMode = Input.MouseModeEnum.Visible; SelectLock = 0.3f; }

    // the three ultimates offered depend on the chosen witch
    public Player.UltKind[] UltChoiceSet() =>
        (Player != null && Player.CrimsonWitch) ? new[] { Player.UltKind.BloodTsunami, Player.UltKind.Exsanguinate, Player.UltKind.BloodRot }
        : (Player != null && Player.DivineWitch) ? new[] { Player.UltKind.FaithShield, Player.UltKind.Judgement, Player.UltKind.Divinity }
        : (Player != null && Player.VerdantWitch) ? new[] { Player.UltKind.GroveGuardian, Player.UltKind.WildSwarm, Player.UltKind.Barkskin }
        : (Player != null && Player.GaleWitch) ? new[] { Player.UltKind.Cyclone, Player.UltKind.Hurricane, Player.UltKind.Stormform }   // (NEW)
        : (Player != null && Player.FrostWitch) ? new[] { Player.UltKind.Blizzard, Player.UltKind.FrostElemental, Player.UltKind.DeepFreeze }   // (NEW)
        : new[] { Player.UltKind.Eclipse, Player.UltKind.LunarLight, Player.UltKind.Crescent };
    public void OpenUltMenu() { State = GameState.UltMenu; Input.MouseMode = Input.MouseModeEnum.Visible; SelectLock = 0.3f; }
    private void CloseUltMenu() { State = GameState.Playing; Input.MouseMode = Input.MouseModeEnum.Captured; }

    private void ChooseUlt(Player.UltKind k)
    {
        Player.Ult = k; Player.UltTier = 0; Player.UltCharge = 0f;
        Player.ModEclipse = Player.ModLight = Player.ModCrescent = false;
        Player.ModShield = Player.ModJudge = Player.ModDivinity = false;
        Player.ModTsunami = Player.ModExsang = Player.ModRot = false;
        Player.ModGuardian = Player.ModSwarm = Player.ModBark = false;
        Hud?.Banner("ultimate bound");
        _ultModCd = 4;   // grace: don't dangle the ult-mod in the very next few level-ups after binding it
        if (_pendingLevels > 0) { Choices = RollChoices(); ChoiceGen++; RarityCue(Choices); State = GameState.LevelUp; SelectLock = 0.3f; }
        else { State = GameState.Playing; Input.MouseMode = Input.MouseModeEnum.Captured; }
    }

    private void UltUpgrade()
    {
        if (Player.Ult == Player.UltKind.None || Player.UltTier >= 4 || BossTokens < UltUpgradeCost) return;
        BossTokens -= UltUpgradeCost; Player.UltTier++;
        Hud?.Banner("ultimate empowered");
    }

    private void UltSwap()
    {
        if (BossTokens < 1f) return;
        BossTokens -= 1f;
        OpenUltChoice();
    }

    // ---- chests ----
    private void SpawnChest()
    {
        var c = new Chest();
        c.NetId = NextPickupId();
        AddChild(c);
        Vector3 pos;
        if (Blockers.Count > 0 && _rng.Randf() < 0.7f)
        {
            var b = Blockers[_rng.RandiRange(0, Blockers.Count - 1)];
            pos = GroundedDrySpawn(b.Pos, b.Radius + 3f, b.Radius + 6f);   // near a landmark, on dry terrain (NEW)
        }
        else
        {
            var pc = Player != null ? Player.GlobalPosition : Vector3.Zero;
            pos = GroundedDrySpawn(pc, 22f, 60f);   // terrain-anchored + off the water (NEW)
        }
        c.GlobalPosition = pos;
        Chests.Add(c);
    }

    public void OpenChestReward(Vector3 at, long openerPeer = 0)
    {
        Sfx?.Clink();
        float r = _rng.Randf();
        if (r < 0.33f) { GiveGold(openerPeer, Mathf.RoundToInt((10 + Wave * 4) * _rng.RandfRange(0.8f, 1.4f))); }
        else if (r < 0.55f) { GiveUltCharge(openerPeer, _rng.RandfRange(0.1f, 0.5f)); }
        else if (r < 0.72f)
        {
            var f = new GroundField { Type = FieldType.Heal, Radius = 5f, Dur = 6f, Power = Player.S.MaxHp * 0.04f, EnemyDmg = 0f, DType = DamageType.Holy };
            AddChild(f); f.GlobalPosition = new Vector3(at.X, 0.04f, at.Z);
            Hud?.Banner("a healing font");
        }
        else if (r < 0.82f) { GiveChestCard(openerPeer); }
        else if (r < 0.92f) { GiveWard(openerPeer); }   // one random armor charge (blood or thorn)
        else
        {
            Hud?.Banner("an ambush!");
            for (int i = 0; i < 3; i++)
            {
                string et = _rng.Randf() < 0.5f ? "sieger" : "caster";
                var e = new Enemy();
                e.Configure(et, Wave);
                e.MakeElite();
                e.NetId = _netEnemySeq++; e.TypeIdx = EnemyKinds.Index(et);
                AddChild(e);
                float a = _rng.RandfRange(0, Mathf.Tau);
                e.GlobalPosition = new Vector3(at.X + Mathf.Cos(a) * 4f, e.Radius, at.Z + Mathf.Sin(a) * 4f);
                Enemies.Add(e);
            }
        }
    }

    // ---- chest rewards routed to the opening player (host applies its own; allies via RPC) ----
    private void GiveGold(long peer, int amt)
    {
        if (peer == 0) { AddGold(amt); Hud?.Banner("gold!"); }
        else NetMgr?.GiveReward(peer, 0, amt);
    }
    private void GiveUltCharge(long peer, float amt)
    {
        if (peer == 0)
        {
            if (Player.Ult != Player.UltKind.None && !Player.UltActive) { Player.UltCharge = Mathf.Min(1f, Player.UltCharge + amt); Hud?.Banner("ultimate charge!"); }
            else { AddGold(Mathf.RoundToInt((10 + Wave * 4) * 0.7f)); Hud?.Banner("gold!"); }
        }
        else NetMgr?.GiveReward(peer, 1, amt);
    }
    private void GiveChestCard(long peer)
    {
        if (peer == 0) OpenChestCard();
        else NetMgr?.GiveReward(peer, 2, 0);
    }
    private void GiveWard(long peer)
    {
        if (peer == 0) { Player.GrantRandomArmor(); Hud?.Banner("an armor charge"); }
        else NetMgr?.GiveReward(peer, 3, 0);
    }
    // a single non-pausing card pick (does not gate the world for others)
    public void OpenChestCard()
    {
        ChestPick = true;
        Hud?.Banner("a gift!");
        OpenLevelUp();
    }

    public void SpawnAdd()
    {
        string[] adds = { "shade", "wisp", "caster", "flyer" };
        SpawnEnemy(adds[_rng.RandiRange(0, adds.Length - 1)]);
    }

    public Enemy SpawnBossAt(string type, Vector3 pos)
    {
        var e = new Enemy();
        e.Configure(type, Wave);
        AddChild(e);
        e.NetId = _netEnemySeq++;
        e.TypeIdx = EnemyKinds.Index(type);
        e.GlobalPosition = new Vector3(pos.X, e.Radius, pos.Z);
        Enemies.Add(e);
        return e;
    }

    // dedicated enemies for the Cleanse rite, ringed around the circle (so the wave running dry can't strand it)
    public void SpawnCleanseHorde(Vector3 center, int n)
    {
        string[] pool = { "shade", "wisp", "caster", "flyer", "brute" };
        var rng = new RandomNumberGenerator(); rng.Randomize();
        for (int i = 0; i < n; i++)
        {
            var e = new Enemy();
            string ct = pool[rng.RandiRange(0, pool.Length - 1)];
            e.Configure(ct, Wave);
            AddChild(e);
            e.NetId = _netEnemySeq++;
            e.TypeIdx = EnemyKinds.Index(ct);
            float a = (i / (float)Mathf.Max(1, n)) * Mathf.Tau + rng.RandfRange(-0.2f, 0.2f);
            float r = 6.5f + rng.RandfRange(0f, 5f) + (i % 2) * 2.5f;   // two loose rings around the circle
            e.GlobalPosition = new Vector3(center.X + Mathf.Cos(a) * r, e.Radius, center.Z + Mathf.Sin(a) * r);
            Enemies.Add(e);
        }
    }

    private void SpawnRitual()
    {
        if (!IsAuthority) return;          // clients receive rituals via snapshot
        if (Rituals.Count > 0) return;   // only one at a time
        var r = new RitualCircle { Type = (RiteType)_rng.RandiRange(0, 2), NetId = NextPickupId() };
        AddChild(r);
        var rng = new RandomNumberGenerator(); rng.Randomize();
        float a = rng.RandfRange(0, Mathf.Tau);
        float dist = rng.RandfRange(26f, 46f);
        var rc = Player != null ? Player.GlobalPosition : Vector3.Zero;
        r.GlobalPosition = new Vector3(rc.X + Mathf.Cos(a) * dist, 0.03f, rc.Z + Mathf.Sin(a) * dist);
        Rituals.Add(r);
        _eventsThisBlock++;
        AnnounceRite(0, (int)r.Type);
    }

    public void RemoveRitual(RitualCircle r) => Rituals.Remove(r);

    private readonly List<GroundField> _comboFields = new();
    public void RegisterComboField(GroundField f)
    {
        _comboFields.RemoveAll(g => g == null || !GodotObject.IsInstanceValid(g));
        _comboFields.Add(f);
        if (f.Cap <= 0) return;
        var same = _comboFields.FindAll(g => g.Type == f.Type);   // oldest-first (insertion order)
        while (same.Count > f.Cap)
        {
            var oldest = same[0];
            same.RemoveAt(0);
            _comboFields.Remove(oldest);
            if (GodotObject.IsInstanceValid(oldest)) oldest.QueueFree();
        }
    }

    public void RitualReward(RiteType t)
    {
        int cat = t == RiteType.Ward ? 0 : (t == RiteType.Summon ? 1 : 2);
        OpenReward(cat);
        NetMgr?.BroadcastReward(cat);   // every warden gets the ritual card; world gates until all choose
    }

    // local banner + sound for a ritual lifecycle event (0 spawned, 1 started, 2 success, 3 fail)
    public void RiteBannerSound(int kind, int type)
    {
        switch (kind)
        {
            case 0: Hud?.Banner("A RITUAL CIRCLE STIRS\u2026"); break;
            case 1: Hud?.Banner(type == 0 ? "WARDING \u2014 hold the circle!" : type == 1 ? "RITE OF SUMMONING \u2014 slay it!" : "CLEANSING \u2014 purge them!"); break;
            case 2: Hud?.Banner("RITUAL COMPLETE \u2014 a boon for all!"); Sfx?.RiteWin(); break;
            case 3: Hud?.Banner("RITUAL FAILED"); Sfx?.RiteFail(); break;
        }
    }
    // host fires the event locally AND tells every client to show the same banner/sound
    public void AnnounceRite(int kind, int type)
    {
        RiteBannerSound(kind, type);
        NetMgr?.BroadcastRite(kind, type);
    }
    public void GrantRewardLocal(int cat) => OpenReward(cat);

    public int PlayersInRange(Vector3 pos, float r)
    {
        int n = 0;
        if (Player != null && GodotObject.IsInstanceValid(Player))
        {
            var d = Player.GlobalPosition - pos; d.Y = 0;
            if (d.Length() < r) n++;
        }
        if (NetMgr != null && NetMgr.Active) n += NetMgr.RemoteAvatarsInRange(pos, r);
        return n;
    }

    private void OpenReward(int cat)
    {
        _rewardCat = cat;
        _rewardLeft += 1;
        _pendingLevels += 1;
        if (State == GameState.Playing)
        {
            Choices = RollChoices();
            ChoiceGen++; RarityCue(Choices);
            State = GameState.LevelUp;
            Input.MouseMode = Input.MouseModeEnum.Visible;
            SelectLock = 0.3f;
        }
    }

    public void RemoveEnemy(Enemy e)
    {
        Enemies.Remove(e);
        if (e == _boss) _boss = null;
        if (e == Goblin) Goblin = null;
    }

    private int _dragSlider = -1;
    public override void _Input(InputEvent e)
    {
        if (Hud == null) return;

        // capturing a key to bind a finisher
        if (State == GameState.BindKey && e is InputEventKey bk && bk.Pressed && !bk.Echo && SelectLock <= 0f)
        {
            var k = bk.PhysicalKeycode;
            if (k == Key.Escape) { FinishBind(); return; }      // cancel — keep whatever default it had
            if (IsReservedKey(k)) return;                       // can't steal a core control; ignore and keep the prompt open
            if (_bindIdx >= 0 && _bindIdx < Player.Fin.Count)
            {
                var oldX = Player.Fin[_bindIdx].Bind;
                for (int i = 0; i < Player.Fin.Count; i++)       // swap so two combos never share a key (the old bug: the later one got shadowed)
                    if (i != _bindIdx && Player.Fin[i].Bind == k) { Player.Fin[i].Bind = oldX; oldX = Key.None; }
                Player.Fin[_bindIdx].Bind = k;
            }
            FinishBind();
            return;
        }

        if (e is InputEventMouseMotion mm && _dragSlider >= 0 && State == GameState.Pause) { ApplySlider(mm.Position); return; }

        if (e is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
        {
            if (!mb.Pressed) { _dragSlider = -1; return; }
            var pos = mb.Position;
            if (SelectLock > 0f && State != GameState.Pause) return;
            switch (State)
            {
                case GameState.CharSelect:
                    if (Hud.RWitch[0].HasPoint(pos)) ChooseWitch(0);
                    else if (Hud.RWitch.Length > 1 && Hud.RWitch[1].HasPoint(pos)) ChooseWitch(1);
                    else if (Hud.RWitch.Length > 2 && Hud.RWitch[2].HasPoint(pos)) ChooseWitch(2);
                    else if (Hud.RWitch.Length > 3 && Hud.RWitch[3].HasPoint(pos)) ChooseWitch(3);
                    else if (Hud.RWitch.Length > 4 && Hud.RWitch[4].HasPoint(pos)) ChooseWitch(4);   // Gale witch (NEW)
                    else if (Hud.RWitch.Length > 5 && Hud.RWitch[5].HasPoint(pos)) ChooseWitch(5);   // Frost witch (NEW)
                    else if (Hud.RWitch.Length > 6 && Hud.RWitch[6].HasPoint(pos)) ChooseWitch(6);   // Forsaken witch (NEW)
                    break;
                case GameState.LevelUp:
                {
                    int btn = Hud.LevelUpBtn(pos);
                    if (btn == 1) { RerollChoices(); break; }
                    if (btn == 2) { LuckRerollChoices(); break; }
                    if (btn == 3) { BuyPick2(); break; }
                    if (btn >= 100) { BanChoice(btn - 100); break; }
                    int idx = Hud.CardAt(pos); if (idx >= 0) ApplyChoice(idx);
                    break;
                }
                case GameState.Swap:    { int idx = Hud.SwapAt(pos); if (idx != -2) DoSwap(idx); break; }
                case GameState.Element: { int idx = Hud.ElementAt(pos); if (idx >= 0) DoElement(idx); break; }
                case GameState.Ult:
                    for (int i = 0; i < 3; i++)
                        if (Hud.RUlt[i].HasPoint(pos)) { ChooseUlt(UltChoiceSet()[i]); break; }
                    break;
                case GameState.UltMenu:
                    if (Hud.RUltMenu[0].HasPoint(pos)) UltUpgrade();
                    else if (Hud.RUltMenu[1].HasPoint(pos)) UltSwap();
                    break;
                case GameState.Roulette:
                    if (Hud.RRoulette[0].HasPoint(pos)) DoRoulettePull();
                    else if (Hud.RRoulette[1].HasPoint(pos)) EndRoulette();
                    break;
                case GameState.Mystic:
                    if (Hud.RMystic[0].HasPoint(pos)) MysticBuy(0);
                    else if (Hud.RMystic[1].HasPoint(pos)) MysticBuy(1);
                    else if (Hud.RMystic[2].HasPoint(pos)) CloseMystic();
                    break;
                case GameState.Scroll:
                    { int idx = Hud.ScrollAt(pos); if (idx == -1) CloseScroll(); else if (idx >= 0) ScrollPick(idx); break; }
                case GameState.Pause:
                    { int bi = Hud.PauseBindAt(pos); if (bi >= 0) { RebindFinisher(bi); break; } }
                    if (Hud.RPauseResume.HasPoint(pos)) { SaveGold(); State = GameState.Playing; Input.MouseMode = Input.MouseModeEnum.Captured; }
                    else if (Hud.RPauseDmg.HasPoint(pos)) { DmgNumbers = !DmgNumbers; SaveGold(); }
                    else if (Hud.RPauseMusic.HasPoint(pos)) { _dragSlider = 0; ApplySlider(pos); }
                    else if (Hud.RPauseSens.HasPoint(pos)) { _dragSlider = 1; ApplySlider(pos); }
                    else if (Hud.RPauseBloom.HasPoint(pos)) { GfxBloom = !GfxBloom; ApplyGraphics(); SaveGold(); }
                    else if (Hud.RPauseSsao.HasPoint(pos)) { GfxSsao = !GfxSsao; ApplyGraphics(); SaveGold(); }
                    else if (Hud.RPauseSsil.HasPoint(pos)) { GfxSsil = !GfxSsil; ApplyGraphics(); SaveGold(); }
                    else { for (int gi = 0; gi < 3; gi++) if (Hud.RPauseGfx[gi].HasPoint(pos)) { SetGfxQuality(gi); SaveGold(); break; } }
                    break;
                case GameState.Over:
                    if (Hud.ROver.HasPoint(pos)) GetTree().ReloadCurrentScene();
                    else if (Hud.RChangeWitch.HasPoint(pos)) { s_witch = -1; GetTree().ReloadCurrentScene(); }
                    break;
                case GameState.Stats:
                    State = GameState.Playing; Input.MouseMode = Input.MouseModeEnum.Captured;
                    break;
            }
        }
    }

    private void ApplySlider(Vector2 pos)
    {
        if (_dragSlider == 0) { var r = Hud.RPauseMusic; SetMusicVol((pos.X - r.Position.X) / Mathf.Max(1f, r.Size.X)); }
        else if (_dragSlider == 1) { var r = Hud.RPauseSens; SetSensitivity((pos.X - r.Position.X) / Mathf.Max(1f, r.Size.X)); }
    }

    public void OpenLevelUp()
    {
        _pendingLevels++;
        VendorSpawnChecks();
        if (State == GameState.Playing)
        {
            if (Player.Level >= 10 && Player.Ult == Player.UltKind.None && !_ultOffered) { _ultOffered = true; OpenUltChoice(); return; }
            Choices = RollChoices();
            ChoiceGen++; RarityCue(Choices);
            State = GameState.LevelUp;
            Input.MouseMode = Input.MouseModeEnum.Visible;
            SelectLock = 0.3f;
        }
    }

    private void RarityCue(System.Collections.Generic.List<UpgradeCard> cards)
    {
        Rarity hi = Rarity.Common;
        foreach (var c in cards) if ((int)c.Rarity > (int)hi) hi = c.Rarity;
        PlayRarity(hi);
    }
    private void PlayRarity(Rarity r)
    {
        if (r == Rarity.Legendary) { Sfx?.Thunder(); Sfx?.Clink(); }
        else if (r == Rarity.Epic) Sfx?.Clink();
    }

    private void ApplyChoice(int i)
    {
        if (Choices == null || i < 0 || i >= Choices.Count) return;
        var card = Choices[i];
        if (_pick2Extra > 0) Choices.RemoveAt(i);   // (NEW) pick-2: drop it so the second pick shows the remaining cards
        if (card.AttuneSlot >= 0) { PendingAttune = card.AttuneSlot; State = GameState.Element; Input.MouseMode = Input.MouseModeEnum.Visible; ChoiceGen++; SelectLock = 0.3f; }
        else if (card.FinKind.HasValue) RouteFinisher(card);
        else if (card.ModKind.HasValue) RouteModifier(card);
        else { card.Apply(Player); Player.Hp = Mathf.Min(Player.S.MaxHp, Player.Hp); FinishStep(); }
    }

    // damage types offered when re-attuning an attack
    public static readonly DamageType[] Elements = { DamageType.Arcane, DamageType.Nature, DamageType.Frost, DamageType.Curse, DamageType.Holy, DamageType.Ember, DamageType.Lunar, DamageType.Wind };

    public void DoElement(int idx)
    {
        if (idx >= 0 && idx < Elements.Length)
        {
            var ty = Elements[idx];
            if (PendingAttune == 0) Player.PrimaryType = ty; else Player.SecondaryType = ty;
            Player.RetintHands();
            Sfx?.Element(ty);
            var beam = new ElementBeam();
            AddChild(beam);
            beam.GlobalPosition = new Vector3(Player.GlobalPosition.X, 0f, Player.GlobalPosition.Z);
            beam.Init(DamageTypes.Col(ty));
        }
        PendingAttune = -1;
        if (_mysticAttune) { _mysticAttune = false; State = GameState.Mystic; Input.MouseMode = Input.MouseModeEnum.Visible; SelectLock = 0.3f; return; }
        FinishStep();
    }

    private void RouteFinisher(UpgradeCard c)
    {
        var t = c.FinKind.Value;
        if (Player.OwnsFinisher(t) || !Player.FinisherFull) { EquipFinisherPrompt(t, c.FinEvery, c.FinPow, c.Rarity, true); }
        else { SwapIsFin = true; _swFin = t; _swEvery = c.FinEvery; _swPow = c.FinPow; _swRar = c.Rarity; State = GameState.Swap; Input.MouseMode = Input.MouseModeEnum.Visible; ChoiceGen++; PlayRarity(_swRar); SelectLock = 0.3f; }
    }

    private void RouteModifier(UpgradeCard c)
    {
        var t = c.ModKind.Value;
        if (Player.OwnsModifier(t) || !Player.ModifierFull) { Player.EquipModifier(t, c.ModMag, c.Rarity); FinishStep(); }
        else { SwapIsFin = false; _swMod = t; _swMag = c.ModMag; _swRar = c.Rarity; State = GameState.Swap; Input.MouseMode = Input.MouseModeEnum.Visible; ChoiceGen++; PlayRarity(_swRar); SelectLock = 0.3f; }
    }

    private void DoSwap(int idx)
    {
        if (idx >= 0)
        {
            if (SwapIsFin) Player.ReplaceFinisher(idx, _swFin, _swEvery, _swPow, _swRar);
            else Player.ReplaceModifier(idx, _swMod, _swMag, _swRar);
        }
        FinishStep();
    }

    private void FinishStep()
    {
        if (_pick2Extra > 0)   // (NEW) pick-2: take a second card from the SAME roll instead of advancing
        {
            _pick2Extra--;
            State = GameState.LevelUp; Input.MouseMode = Input.MouseModeEnum.Visible; SelectLock = 0.3f; ChoiceGen++;
            return;
        }
        _pendingLevels--;
        if (_rewardLeft > 0) { _rewardLeft--; if (_rewardLeft == 0) _rewardCat = -1; }
        else if (_lootLeft > 0) _lootLeft--;
        if (_pendingLevels > 0)
        {
            Choices = RollChoices();
            ChoiceGen++; RarityCue(Choices);
            State = GameState.LevelUp;
            Input.MouseMode = Input.MouseModeEnum.Visible;
            SelectLock = 0.3f;
        }
        else if (_rouletteActive)
        {
            if (_roulette != null && GodotObject.IsInstanceValid(_roulette) && _roulette.Pulls < 3)
            { State = GameState.Roulette; Input.MouseMode = Input.MouseModeEnum.Visible; SelectLock = 0.3f; }
            else EndRoulette();
        }
        else { State = GameState.Playing; Input.MouseMode = Input.MouseModeEnum.Captured; ChestPick = false; }
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        if (SelectLock > 0f) SelectLock -= dt;
        if (_dotCreditCd.Count > 0)   // (NEW) age the per-caster DoT-combo throttles
        {
            var __dk = new System.Collections.Generic.List<int>(_dotCreditCd.Keys);
            foreach (var k in __dk) { float v = _dotCreditCd[k] - dt; if (v <= 0f) _dotCreditCd.Remove(k); else _dotCreditCd[k] = v; }
        }
        if (ToastT > 0f) ToastT -= dt;
        ComputeWorldRunning();
        UpdateInteract(dt);
        if (GoldFlash > 0f) GoldFlash -= dt;
        if (!InExpedition && _world != null && Player != null) _world.Update(Player.GlobalPosition);
        // DEBUG (host/solo): F6 enters/exits the hedge-maze test; F7 loads the old Expedition test leg.
        bool f6 = Input.IsPhysicalKeyPressed(Key.F6);
        if (f6 && !_mazeKeyWas && IsAuthority && State == GameState.Playing) { if (InMaze) ExitMaze(); else EnterMaze((ulong)GD.Randi()); }
        _mazeKeyWas = f6;
        bool f7 = Input.IsPhysicalKeyPressed(Key.F7);
        if (f7 && !_expoKeyWas && IsAuthority && State == GameState.Playing && !InMaze) BeginExpedition((ulong)GD.Randi());
        _expoKeyWas = f7;
        if (InMaze && _mazePortalNode != null && _maze != null && Player != null)   // reaching the (spawned) portal returns to the open world
        {
            var pp = _maze.PortalPos;
            if (new Vector2(Player.GlobalPosition.X - pp.X, Player.GlobalPosition.Z - pp.Z).Length() < 2.6f) ExitMaze();
        }
        if (InMaze && !_mazeFound && _maze != null && Player != null && !(NetMgr != null && NetMgr.Active) && _mazeStatueTarget >= 0)
        {
            var spos = _maze.CellCenter(_maze.Chambers[_mazeStatueTarget]);
            if (new Vector2(Player.GlobalPosition.X - spos.X, Player.GlobalPosition.Z - spos.Z).Length() < 4.5f)   // reached the statue
            {
                _mazeFound = true;
                SpawnPortal(Maze.PickPortal(_maze, new List<Vector2I> { Maze.CellOf(_maze, Player.GlobalPosition) }), net: false);
                SpawnFairy(new Vector3(Player.GlobalPosition.X, 0f, Player.GlobalPosition.Z), net: false);
                Hud?.Banner("the way opens — follow the fairy");
            }
        }
        if (InMaze && !_mazeFound && IsAuthority && NetMgr != null && NetMgr.Active && _maze != null && Player != null)
        {
            var positions = new List<Vector3> { Player.GlobalPosition };
            foreach (var av in NetMgr.AllyAvatars()) if (GodotObject.IsInstanceValid(av)) positions.Add(av.GlobalPosition);
            if (positions.Count >= 2 && MazeAllMet(positions))
            {
                _mazeFound = true;
                var cells = new List<Vector2I>();
                foreach (var pp2 in positions) cells.Add(Maze.CellOf(_maze, pp2));
                var portalCell = Maze.PickPortal(_maze, cells);            // furthest + out-of-sight from everyone
                Vector3 c = Vector3.Zero; foreach (var pp2 in positions) c += pp2; c /= positions.Count;
                var fairyAt = new Vector3(c.X, 0f, c.Z);
                SpawnPortal(portalCell, net: false);
                SpawnFairy(fairyAt, net: false);
                NetMgr.BroadcastMazeOpen(fairyAt, portalCell.X, portalCell.Y);   // one reliable message → clients spawn both, in order
                Hud?.Banner("the way opens — follow the fairy");
            }
        }
        for (int i = Blips.Count - 1; i >= 0; i--) { Blips[i].T -= dt; if (Blips[i].T <= 0f) Blips.RemoveAt(i); }   // (NEW) fade firework pings
        if (InMaze && IsAuthority && _maze != null && Player != null)   // maze AI director (host)
        {
            _mazeElapsed += dt;
            _mazeGrace = Mathf.Max(0f, _mazeGrace - dt);
            var pcells = new List<Vector2I> { Maze.CellOf(_maze, Player.GlobalPosition) };
            if (NetMgr != null && NetMgr.Active) foreach (var av in NetMgr.AllyAvatars()) if (GodotObject.IsInstanceValid(av)) pcells.Add(Maze.CellOf(_maze, av.GlobalPosition));

            _mazeChaseT -= dt;
            if (_mazeChaseT <= 0f) { _mazeChaseT = 0.3f; _mazeChaseDist = Maze.DistField(_maze, pcells); }   // enemy corridor nav

            bool mazeOpened = _mazeFound;   // (FIX) phase 2 = portal actually open (solo now has a find-statue phase 1; was always-true in solo → pre-aggro'd spawns)
            if (mazeOpened && _mazeDist != null)   // heat: from time, more from team distance-to-portal (steeper now)
            {
                float sumd = 0f; foreach (var c in pcells) { int dd = _mazeDist[c.X, c.Y]; if (dd > 0) sumd += dd; }
                Heat = Mathf.Clamp(1f + _mazeElapsed * 0.022f + (pcells.Count > 0 ? sumd / pcells.Count : 0f) * 0.045f, 1f, 2.8f);
            }

            _mazeSpawnT -= dt;
            if (_mazeSpawnT <= 0f)
            {
                if (Enemies.Count < 13 * WardenCount + 8)   // dwindled cap so 2-player escapes aren't overwhelming
                {
                    int count = mazeOpened ? 1 + (int)(WardenCount * Mathf.Max(0f, Heat - 1f) * 1.3f) : 2;   // fewer reinforcements per tick
                    int[,] portalDist = null;   // spawn out-of-LOS in ANY direction (incl. ahead) so the horde can cut you off
                    for (int i = 0; i < count; i++)
                        if (Maze.PickSpawnCell(_maze, portalDist, pcells, _mazeRng, out var scell))
                        {
                            var me = SpawnMazeEnemy("swarmer", _maze.CellCenter(scell));
                            if (mazeOpened) me?.Alert();   // phase 2: reinforcements hunt immediately
                        }
                }
                // find phase: trickle idle mobs faster the longer you search; phase 2: fast + heat-scaled
                _mazeSpawnT = mazeOpened ? Mathf.Lerp(2.2f, 0.6f, Mathf.Clamp((Heat - 1f) / 1.6f, 0f, 1f))
                                         : Mathf.Lerp(3.0f, 1.3f, Mathf.Clamp(_mazeElapsed / 45f, 0f, 1f));
            }
            // Special enemies (Takers; future specials): MP-only, capped at (players-1) total, on a director cooldown — checked every frame
            _specialSpawnT -= dt;
            if (mazeOpened && NetMgr != null && NetMgr.Active && WardenCount >= 2 && _mazeDist != null && _specialSpawnT <= 0f)
            {
                int specials = 0; foreach (var e in Enemies) if (e != null && GodotObject.IsInstanceValid(e) && e.IsSpecial) specials++;
                if (specials < WardenCount - 1 && Maze.PickSpawnCell(_maze, _mazeDist, pcells, _mazeRng, out var tcell))
                {
                    SpawnMazeEnemy("taker", _maze.CellCenter(tcell));
                    _specialSpawnT = Mathf.Lerp(20f, 10f, Mathf.Clamp((Heat - 1f) / 1.8f, 0f, 1f));   // 20s base, down to 10s when the director is hot
                }
            }
        }
        if (State == GameState.Playing && WorldRunning) GameClock += dt;
        if (State == GameState.Playing && WorldRunning) { DayTime += dt / DayLength; if (DayTime >= 1f) DayTime -= 1f; _skyTime += dt; _skyMat?.SetShaderParameter("sky_time", _skyTime); ApplyDayNight(); }
        if (Sfx != null && Player != null)
        {
            float target = ComputeTension();
            _tension = Mathf.Lerp(_tension, target, (target > _tension ? 6f : 1.2f) * dt);
            float fireNudge = Mathf.Min(Player.FireHeat, 0.5f) * 0.16f;   // capped + gentle so holding fire can't run it away
            float tens = _tension * 0.22f;
            Sfx.SetTempo(0.97f + Mathf.Max(fireNudge, tens));
            Sfx.EventActive = Rituals.Count > 0;
            Sfx.SetIntensity(_tension);
        }

        // Vote-to-skip: ANY player holds Backspace 2s to vote; the host skips only when EVERY player has voted. (NEW)
        // Covers both the between-wave rest and an in-progress ritual. Host tallies; clients send their vote over the net.
        if (State == GameState.Playing && WorldRunning)
        {
            bool riteActive = Rituals.Exists(r => r != null && r.Active && !r.Done);
            bool skippable = InIntermission || riteActive;
            if (skippable != _prevSkippable)
            {
                if (skippable && IsAuthority) _skipVotes.Clear();   // fresh window → drop stale votes (host owns the tally)
                _prevSkippable = skippable; _skipHold = 0f; _localVoted = false;
            }
            if (skippable && Input.IsPhysicalKeyPressed(Key.Backspace))
            {
                _skipHold += dt;
                if (_skipHold >= 2f && !_localVoted)
                {
                    _localVoted = true;
                    if (IsAuthority) RegisterSkipVote(1);   // host votes as peer 1 (solo: one vote, and SkipNeeded is 1)
                    else NetMgr?.VoteSkip();                // client → host
                }
            }
            else if (!Input.IsPhysicalKeyPressed(Key.Backspace)) _skipHold = 0f;

            // host: everyone voted while a ritual is running → skip the rite
            if (IsAuthority && riteActive && _skipVotes.Count >= SkipNeeded)
            {
                Rituals.Find(r => r != null && r.Active && !r.Done)?.ForceSkip();
                _skipVotes.Clear(); _localVoted = false;
            }

            if (IsAuthority && NetMgr != null && NetMgr.Active)   // (NEW) sync wave/intermission + vote tally so clients see the prompt and can vote
            {
                _waveSyncT -= dt;
                if (_waveSyncT <= 0f) { _waveSyncT = 0.2f; NetMgr.BroadcastWaveState(Wave, _waveGap, _skipVotes.Count); }
            }
        }

        if (State == GameState.CharSelect)
        {
            if (Input.IsActionJustPressed("pick1")) ChooseWitch(0);
            else if (Input.IsActionJustPressed("pick2")) ChooseWitch(1);
            else if (Input.IsActionJustPressed("pick3")) ChooseWitch(2);
            else if (Input.IsActionJustPressed("pick4")) ChooseWitch(3);
            else if (Input.IsActionJustPressed("pick5")) ChooseWitch(4);   // Gale witch (NEW)
            else if (Input.IsActionJustPressed("pick6")) ChooseWitch(5);   // Frost witch (NEW)
            else if (Input.IsActionJustPressed("pick7")) ChooseWitch(6);   // Forsaken witch (NEW)
            return;
        }
        if (State == GameState.Over)
        {
            if (Input.IsActionJustPressed("restart")) GetTree().ReloadCurrentScene();
            else if (Input.IsActionJustPressed("changewitch")) { s_witch = -1; GetTree().ReloadCurrentScene(); }
            return;
        }

        if (Input.IsActionJustPressed("stats") && (State == GameState.Playing || State == GameState.Stats))
        {
            State = State == GameState.Stats ? GameState.Playing : GameState.Stats;
            Input.MouseMode = State == GameState.Stats ? Input.MouseModeEnum.Visible : Input.MouseModeEnum.Captured;
            return;
        }
        if (State == GameState.Stats) return;

        if (Input.IsActionJustPressed("release_mouse") && State == GameState.Playing)
        {
            State = GameState.Pause;
            Input.MouseMode = Input.MouseModeEnum.Visible;
            SelectLock = 0.2f;
            return;
        }
        if (State == GameState.Pause)
        {
            if (Sfx != null)
            {
                if (Input.IsActionPressed("move_left")) { Sfx.MusicVol = Mathf.Max(0f, Sfx.MusicVol - dt * 0.6f); }
                if (Input.IsActionPressed("move_right")) { Sfx.MusicVol = Mathf.Min(1f, Sfx.MusicVol + dt * 0.6f); }
            }
            if (SelectLock <= 0f && (Input.IsActionJustPressed("release_mouse") || Input.IsActionJustPressed("pick1")))
            {
                SaveGold();   // persist music volume
                State = GameState.Playing;
                Input.MouseMode = Input.MouseModeEnum.Captured;
            }
            return;
        }

        if (State == GameState.LevelUp)
        {
            if (SelectLock <= 0f)
            {
                if (Input.IsActionJustPressed("pick1")) ApplyChoice(0);
                else if (Input.IsActionJustPressed("pick2")) ApplyChoice(1);
                else if (Input.IsActionJustPressed("pick3")) ApplyChoice(2);
            }
            return;
        }

        if (State == GameState.Mystic)
        {
            if (SelectLock <= 0f)
            {
                if (Input.IsActionJustPressed("release_mouse") || Input.IsActionJustPressed("pick3")) CloseMystic();
                else if (Input.IsActionJustPressed("pick1")) MysticBuy(0);
                else if (Input.IsActionJustPressed("pick2")) MysticBuy(1);
            }
            return;
        }
        if (State == GameState.Scroll)
        {
            if (SelectLock <= 0f)
            {
                if (Input.IsActionJustPressed("release_mouse")) CloseScroll();
                else if (Input.IsActionJustPressed("pick1")) ScrollPick(0);
                else if (Input.IsActionJustPressed("pick2")) ScrollPick(1);
                else if (Input.IsActionJustPressed("pick3")) ScrollPick(2);
                else if (Input.IsActionJustPressed("pick4")) ScrollPick(3);
                else if (Input.IsActionJustPressed("pick5")) ScrollPick(4);
                else if (Input.IsActionJustPressed("pick6")) ScrollPick(5);
                else if (Input.IsActionJustPressed("pick7")) ScrollPick(6);
            }
            return;
        }
        if (State == GameState.Swap)
        {
            int n = SwapIsFin ? Player.Fin.Count : Player.Mods.Count;
            if (SelectLock <= 0f)
            {
                if (Input.IsActionJustPressed("pick1") && n > 0) DoSwap(0);
                else if (Input.IsActionJustPressed("pick2") && n > 1) DoSwap(1);
                else if (Input.IsActionJustPressed("pick3") && n > 2) DoSwap(2);
                else if (Input.IsActionJustPressed("pick4") && n > 3) DoSwap(3);
                else if (Input.IsActionJustPressed("pick5") && n > 4) DoSwap(4);
                else if (Input.IsActionJustPressed("pick0")) DoSwap(-1);
            }
            return;
        }
        if (State == GameState.Element)
        {
            if (SelectLock <= 0f)
            {
                if (Input.IsActionJustPressed("pick1")) DoElement(0);
                else if (Input.IsActionJustPressed("pick2")) DoElement(1);
                else if (Input.IsActionJustPressed("pick3")) DoElement(2);
                else if (Input.IsActionJustPressed("pick4")) DoElement(3);
                else if (Input.IsActionJustPressed("pick5")) DoElement(4);
                else if (Input.IsActionJustPressed("pick6")) DoElement(5);
                else if (Input.IsActionJustPressed("pick7")) DoElement(6);
                else if (Input.IsActionJustPressed("pick8")) DoElement(7);
            }
            return;
        }
        if (State == GameState.Ult)
        {
            if (SelectLock <= 0f)
            {
                if (Input.IsActionJustPressed("pick1")) ChooseUlt(UltChoiceSet()[0]);
                else if (Input.IsActionJustPressed("pick2")) ChooseUlt(UltChoiceSet()[1]);
                else if (Input.IsActionJustPressed("pick3")) ChooseUlt(UltChoiceSet()[2]);
            }
            return;
        }
        if (State == GameState.UltMenu)
        {
            if (SelectLock <= 0f)
            {
                if (Input.IsActionJustPressed("pick1")) UltUpgrade();
                else if (Input.IsActionJustPressed("pick2")) UltSwap();
                else if (Input.IsActionJustPressed("ultmenu") || Input.IsActionJustPressed("release_mouse")) CloseUltMenu();
            }
            return;
        }
        if (State == GameState.Roulette)
        {
            if (SelectLock <= 0f)
            {
                if (Input.IsActionJustPressed("pick1") || Input.IsActionJustPressed("ult")) DoRoulettePull();
                else if (Input.IsActionJustPressed("release_mouse") || Input.IsActionJustPressed("ultmenu")) EndRoulette();
            }
            return;
        }

        if (State == GameState.Playing && Input.IsActionJustPressed("ult")) Player.TryUlt();
        if (State == GameState.Playing && Input.IsActionJustPressed("ultmenu") && Player.Ult != Player.UltKind.None) { OpenUltMenu(); return; }

        // Playing — spawn pacing
        _waveTimer += dt;
        if (IsAuthority && Player != null && State == GameState.Playing)   // feed the director
        {
            float hp = Player.S.MaxHp > 0 ? Player.Hp / Player.S.MaxHp : 1f;
            _waveMinHpFrac = Mathf.Min(_waveMinHpFrac, hp);
            if (Player.Downed) _downThisWave = true;
            if (NetMgr != null && NetMgr.Active && NetMgr.AnyDowned()) _downThisWave = true;
        }
        if (Player != null) _waveMaxComboMul = Mathf.Max(_waveMaxComboMul, Player.ComboMul());

        // Expedition mode drives spawns/objective from its own director instead of the endless waves.
        if (IsAuthority && WorldRunning && InExpedition && _expoRun != null) { _expoRun.Tick(this, dt); BroadcastExpoStateIfHost(); }

        // Only the authority (solo or host) drives chests, spawns, boss adds, and wave progression,
        // and only while the shared world is running (paused during level-up gates / all-pause).
        if (IsAuthority && WorldRunning && !InExpedition)
        {
        _chestT -= dt;
        if (_chestT <= 0f)
        {
            _chestT = (float)GD.RandRange(7.0, 12.0);
            int cap = 1 + Wave / 3;
            if (Chests.Count < cap) SpawnChest();
        }

        if (Goblin != null)
        {
            if (GoblinTime > 0f)
            {
                GoblinTime -= dt;
                if (GoblinTime <= 0f) { if (GodotObject.IsInstanceValid(Goblin)) { Enemies.Remove(Goblin); Goblin.QueueFree(); } Goblin = null; }
            }
        }

        if (_poofSndT > 0f) _poofSndT -= dt;
        if (_boss != null && GodotObject.IsInstanceValid(_boss) && !_boss.Dead)
        {
            _bossAddT -= dt;
            if (_bossAddT <= 0f)
            {
                _bossAddT = 10f;   // a new section of adds every 10s
                float curDps = _bossDmgAccum / 10f; _bossDmgAccum = 0f;
                if (_bossDpsInit)   // adapt group size to how hard the coven is pushing
                {
                    if (curDps > _bossPrevDps * 1.1f) _bossAddGroup += 2;        // DPS trending UP → send more next time
                    else if (curDps < _bossPrevDps * 0.9f) _bossAddGroup -= 2;   // DPS trending DOWN → ease off
                    _bossAddGroup = Mathf.Clamp(_bossAddGroup, 3, 8 + WardenCount * 3);
                }
                _bossPrevDps = curDps; BossRecentDps = curDps; _bossDpsInit = true;
                SpawnBossAddGroup(_bossAddGroup);
                _boss.Taunt();
            }
        }

        // Special enemies in endless mode too — MP-only, capped at (players-1) total, on the shared cooldown
        if (IsAuthority && State == GameState.Playing && !InMaze && !InExpedition && NetMgr != null && NetMgr.Active && WardenCount >= 2)
        {
            _specialSpawnT -= dt;
            if (_specialSpawnT <= 0f && Wave >= 3 && Enemies.Count > 0)   // only mid-wave — never during the between-wave rest (was blocking skips)
            {
                int specials = 0; foreach (var e in Enemies) if (e != null && GodotObject.IsInstanceValid(e) && e.IsSpecial) specials++;
                if (specials < WardenCount - 1)
                {
                    SpawnEnemy("taker");
                    _specialSpawnT = Mathf.Lerp(20f, 10f, Mathf.Clamp((Wave - 3) / 20f, 0f, 1f));   // 20s → 10s as the run escalates
                }
            }
        }

        if (_toSpawn.Count > 0)
        {
            _spawnT -= dt;
            if (_spawnT <= 0f) { SpawnEnemy(_toSpawn.Dequeue()); _spawnT = (float)GD.RandRange(0.25, 0.55); }
        }
        else if (Enemies.Count == 0)
        {
            // all players must vote (hold Backspace) to skip the between-wave rest — detection is global in _Process (NEW)
            if (Wave >= 1 && _skipVotes.Count >= SkipNeeded) _waveGap = 0f;

            _waveGap -= dt;
            if (_waveGap <= 0f)
            {
                if (Wave >= 1) AwardWaveGold();
                if (Wave >= 1) AssessDirector();   // read how the party handled this wave, set next wave's Heat
                Chests.RemoveAll(c => {
                    if (c == null || !GodotObject.IsInstanceValid(c)) return true;
                    if (c.Opened) { c.QueueFree(); return true; }
                    return false;
                });
                NextWave();
                _waveTimer = 0f; _waveMaxComboMul = 1f; _waveComboAccrued = 0;
                _waveGap = WaveGapMax; _skipHold = 0f; _skipVotes.Clear(); _localVoted = false;   // (NEW) new wave → reset votes
            }
        }
        }
    }

    private void AwardWaveGold()
    {
        float comboF = Mathf.Max(1f, _waveMaxComboMul);            // your peak combo multiplier this wave
        float par = 16f + Wave * 2.5f;                            // expected clear time
        float timeF = Mathf.Clamp(par / Mathf.Max(_waveTimer, 1f), 0.4f, 2.5f);   // faster clear → more
        float diffF = 1f + (Wave - 1) * 0.12f;                    // later waves are worth more
        int g = Mathf.Max(1, Mathf.RoundToInt(8f * comboF * timeF * diffF));
        int flat = Mathf.RoundToInt(_waveComboAccrued * 0.05f);   // small bonus for total combo activity
        g += flat;
        Gold += g;
        LastWaveGold = g;
        GoldFlash = 3f;
        SaveGold();
    }

    private float _savedMusicVol = 0.8f;
    private float _savedSens = 0.0022f;
    public bool DmgNumbers = false;   // floating damage numbers, colored by damage type

    // (NEW) per-machine graphics settings — each player in multiplayer tunes their own for performance.
    public int GfxQuality = 2;        // 0 Low, 1 Med, 2 High
    public bool GfxBloom = true;      // glow / bloom
    public bool GfxSsao = true;       // screen-space ambient occlusion
    public bool GfxSsil = true;       // screen-space indirect light (fake GI)
    public int ImpactDecalCap => GfxQuality == 0 ? 8 : GfxQuality == 1 ? 18 : 28;   // fewer ground marks on lower presets
    public float ParticleScale => GfxQuality == 0 ? 0.4f : GfxQuality == 1 ? 0.7f : 1f;   // thinner particle trails on lower presets

    // Toggles are authoritative — ApplyGraphics uses the individual flags directly, so a user can override any
    // single effect regardless of preset. Picking a preset just sets sensible defaults for those flags.
    public void ApplyGraphics()
    {
        if (_env == null) return;
        _env.GlowEnabled = GfxBloom;
        _env.SsaoEnabled = GfxSsao;
        _env.SsilEnabled = GfxSsil;
    }
    public void SetGfxQuality(int q)
    {
        GfxQuality = Mathf.Clamp(q, 0, 2);
        GfxBloom = GfxQuality >= 1;   // preset defaults; each toggle can still be flipped individually
        GfxSsao = GfxQuality >= 2;
        GfxSsil = GfxQuality >= 2;
        ApplyGraphics();
    }

    public void SaveGold()
    {
        var cfg = new ConfigFile();
        cfg.SetValue("progress", "gold", Gold);
        cfg.SetValue("options", "musicvol", Sfx != null ? Sfx.MusicVol : _savedMusicVol);
        cfg.SetValue("options", "sens", Player != null ? Player.MouseSens : _savedSens);
        cfg.SetValue("options", "dmgnumbers", DmgNumbers);
        cfg.SetValue("options", "gfxquality", GfxQuality);
        cfg.SetValue("options", "gfxbloom", GfxBloom);
        cfg.SetValue("options", "gfxssao", GfxSsao);
        cfg.SetValue("options", "gfxssil", GfxSsil);
        cfg.Save("user://grove_save.cfg");
    }

    private void LoadGold()
    {
        var cfg = new ConfigFile();
        if (cfg.Load("user://grove_save.cfg") == Error.Ok)
        {
            Gold = cfg.GetValue("progress", "gold", 0).AsInt32();
            _savedMusicVol = (float)cfg.GetValue("options", "musicvol", 0.8f).AsDouble();
            _savedSens = (float)cfg.GetValue("options", "sens", 0.0022f).AsDouble();
            DmgNumbers = cfg.GetValue("options", "dmgnumbers", false).AsBool();
            GfxQuality = cfg.GetValue("options", "gfxquality", 2).AsInt32();
            GfxBloom = cfg.GetValue("options", "gfxbloom", true).AsBool();
            GfxSsao = cfg.GetValue("options", "gfxssao", true).AsBool();
            GfxSsil = cfg.GetValue("options", "gfxssil", true).AsBool();
            ApplyGraphics();   // no-op if the environment isn't built yet; BuildWorld re-applies
        }
    }

    public void SetMusicVol(float v) { if (Sfx != null) Sfx.MusicVol = Mathf.Clamp(v, 0f, 1f); }
    public float SensSlider => Player != null ? Mathf.InverseLerp(0.0006f, 0.005f, Player.MouseSens) : 0.4f;
    public void SetSensitivity(float v) { if (Player != null) Player.MouseSens = Mathf.Lerp(0.0006f, 0.005f, Mathf.Clamp(v, 0f, 1f)); }

    public void GameOver()
    {
        if (State == GameState.Over) return;
        State = GameState.Over;
        Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    // just the holy pillar of light (used by the network receiver)
    public void RezBeamColumn(Vector3 at)
    {
        var beam = new ElementBeam();
        AddChild(beam);
        beam.GlobalPosition = new Vector3(at.X, 0f, at.Z);
        beam.Init(DamageTypes.Col(DamageType.Holy));
    }

    // Holy revival VFX: a column of light from the sky in the holy color + a lingering medium-heal field.
    // The field auto-syncs to allies; broadcast==true also sends the beam column so allies see the pillar.
    public void RezBeam(Vector3 at, bool broadcast)
    {
        var col = DamageTypes.Col(DamageType.Holy);
        RezBeamColumn(at);

        var heal = new GroundField {
            Type = FieldType.Heal, Radius = 6f, Dur = 6f,
            Power = (Player != null ? Player.S.MaxHp : 100f) * 0.04f,   // ~4%/s medium heal
            EnemyDmg = 0f, DType = DamageType.Holy, TintColor = col, HealAllies = true
        };
        AddChild(heal);
        heal.GlobalPosition = new Vector3(at.X, 0.04f, at.Z);   // GroundField self-announces to allies

        if (broadcast) NetMgr?.BroadcastRezBeam(new Vector3(at.X, 0f, at.Z));
    }
}
