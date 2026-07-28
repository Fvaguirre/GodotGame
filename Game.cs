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
public enum GameState { Lobby, CharSelect, Playing, LevelUp, Swap, Stats, Element, Ult, UltMenu, Roulette, Mystic, Scroll, Shop, BindKey, Pause, Attune, Over, ColliderEdit }
// (NEW) named wave mutators — a hot streak can turn the next wave into one of these. Blood Moon = faster foes + more loot;
// Eclipse = dense fog / short sight; Surge = a dense fast-trash rush. Lasts one wave; synced to clients via the wave state.
public enum WaveMutator { None, BloodMoon, Eclipse, Surge, Moonfall, Volatile }
// (NEW) biomes / levels. Level 1 = the moonlit Grove; each portal (after a boss) advances to the next. Grove → Rainforest for now.
public enum Biome { Grove, Rainforest }

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

public struct Blocker { public Vector3 Pos; public float Radius; public float Top; }   // Top = world-Y of the structure's top (0 = "unknown/infinite"). Lets the player fly OVER a tree/house instead of catching its invisible column.
public struct FireRing { public Vector3 Pos; public float Radius; public float T; }   // (NEW) Ring of Fire zone: eats enemy projectiles that enter it (host-authoritative)
public struct VineGrab { public Vector3 Pos; public float TopY; public bool Sky; }   // (NEW) jungle grapple vine: Pos = low handhold you interact with, TopY = height it carries you to. Sky = a floating-island vine (only grabbable while airborne, so you can't grab one from the island it hangs under)
// Walkable flat surface (raised platform top, or ground patch). (AUTHORED-COLLIDER extensions: Yaw = Y-rotation of the box
// footprint so it can sit at any angle; Solid = red collider — you're blocked from the sides but CANNOT stand on top (excluded
// from the walkable-surface height); Cyl = cylinder footprint instead of a box (Half.X used as the radius).
public struct Deck { public Vector3 Center; public Vector2 Half; public float TopY; public bool Floating; public bool LowPad; public float Yaw; public bool Solid; public bool Cyl; public bool Boxed; public float BotY; }   // Floating = a sky island (thin solid rim). LowPad = a short dais (pedestal) you STEP up from any side. Boxed = an AUTHORED finite box: BotY..TopY (side-block only within that Y range; below it you pass under). Legacy decks (Boxed=false) block from -inf up to TopY.
// Sloped walkway connecting two heights along one axis (Yaw = Y-rotation of the slope so a ramp can face any direction).
public struct Ramp { public Vector3 Center; public Vector2 Half; public float YLow; public float YHigh; public bool AlongX; public float Yaw; }

public partial class Game : Node3D
{
    public static Game I;

    public const float Arena = 58f;
    public GameState State = GameState.Playing;
    public int Score = 0;
    public int Wave = 0;
    public WaveMutator ActiveMutator = WaveMutator.None;   // (NEW) the current wave's named mutator (None most waves)
    public Biome CurBiome = Biome.Grove;   // (NEW) which biome/level we're in (synced to clients on advance)
    public int LevelNum = 1;               // (NEW) 1-based level; increments each time the party takes a portal
    public int BiomeStartWave = 1;         // (NEW) the Wave at which the CURRENT biome began — biome-relative gating (e.g. "5 waves into the jungle" = Wave - BiomeStartWave >= 5), future-proof for starting in any biome
    public int BiomeWaves => Wave - BiomeStartWave;   // waves elapsed within the current biome
    private WaveMutator _endedMutator = WaveMutator.None;  // (NEW) the just-cleared wave's mutator — kept for the reward/gold after ActiveMutator is reset at intermission

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
    // nearest warden WORLD position, counting remote allies (RemoteAvatars) too — used so a damaged zombie can
    // shamble toward whoever hit it, even in MP where allies aren't local Player objects.
    public Vector3 NearestWardenPos(Vector3 pos)
    {
        Vector3 best = Player != null ? Player.GlobalPosition : pos;
        float bd = Player != null ? (Player.GlobalPosition - pos).LengthSquared() : float.MaxValue;
        if (NetMgr != null && NetMgr.Active)
            foreach (var av in NetMgr.AllyAvatars())
                if (GodotObject.IsInstanceValid(av)) { float d = (av.GlobalPosition - pos).LengthSquared(); if (d < bd) { bd = d; best = av.GlobalPosition; } }
        return best;
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
    public UltCastOverlay UltOverlay;   // (NEW) picture-in-picture cutouts of allies casting their ults
    public FaithShield Shield;   // active Faith Shield dome (Divine ult), if any
    public readonly List<Enemy> Enemies = new();
    public readonly List<Blocker> Blockers = new();
    public readonly List<Blocker> PersistentBlockers = new();   // structures that stay solid regardless of chunk streaming (the maze well)
    public readonly List<Blocker> PedestalRimBlockers = new();  // (NEW) the raised rune-block rims around pedestal daises — jump-overable; managed with pedestals (separate from the maze well's clear/add)
    public readonly List<Blocker> WallBlockers = new();          // (NEW) frost-wall obstacle circles — enemies steer around these; NOT touched by chunk-stream RebuildBlockers
    public readonly List<Deck> PersistentDecks = new();         // (NEW) floating sky-island tops — walkable, survive chunk streaming (flushed into Decks in RebuildBlockers)
    public readonly List<Ramp> PersistentRamps = new();         // (NEW) pedestal staircases — walkable ramps that survive chunk streaming (so foes climb the STAIRS, not the wall)
    public readonly List<VineGrab> Vines = new();   // (NEW) hanging-vine grapple points (jungle) — hold-E to ride up + fling skyward; managed with chunks
    public readonly List<VineGrab> PersistentVines = new();     // (NEW) sky-island vines — survive chunk streaming
    public bool InSky = false;                                  // (NEW) the jungle Sky-Islands ritual overlay is active (islands float above the live jungle)
    // ---- Sky-Islands ritual runtime state (generation lives in SkyIslands.cs; the state machine is here, mirroring the maze) ----
    private SkyIslands.SkyData _sky;         // island layout
    private Node3D _skyRoot;                 // island geometry root
    private Node3D _skyWhirl;                // the ground whirlwind interactable node
    private Vector3 _skyWhirlPos;            // where the whirlwind sits on the jungle floor
    private bool _skyWhirlActive = false;    // whirlwind present & rideable
    private bool _skySpawned = false;        // whirlwind spawned this biome visit (one-time gate)
    private float _skyElapsed, _skySpawnT, _skyFallT;   // director/heat timer, spawn cadence, fall-check throttle
    private bool _skyDone = false, _skyWon = false;
    private readonly List<bool> _skyEffigyLit = new();
    private bool _skyCauldronArmed = false;  // 3 effigies lit → the cauldron activates
    private List<Node3D> _skyEffigyNodes = new();
    private Node3D _skyCauldronNode, _skyCauldronBeam;
    public bool SkyActive => InSky;          // HUD readout
    public int SkyEffigiesLit { get { int n = 0; foreach (var b in _skyEffigyLit) if (b) n++; return n; } }
    public bool SkyCauldronArmed => _skyCauldronArmed;
    public readonly List<FireRing> FireRings = new();   // (NEW) active Ring-of-Fire projectile-eating zones (host/solo)
    public void RegisterFireRing(Vector3 pos, float radius, float dur)
    {
        if (NetMgr != null && NetMgr.Active && !IsAuthority) { NetMgr.ReqFireRing(pos, radius, dur); return; }   // a client routes it to the host, where the bolts live
        FireRings.Add(new FireRing { Pos = pos, Radius = radius, T = dur });
    }
    private void AgeFireRings(float dt)
    {
        for (int i = FireRings.Count - 1; i >= 0; i--) { var fr = FireRings[i]; fr.T -= dt; if (fr.T <= 0f) FireRings.RemoveAt(i); else FireRings[i] = fr; }
    }

    public readonly List<FireRing> WindRings = new();   // (NEW) Cyclone ult zones — their swirling edge eats enemy projectiles (host/solo), same shape as FireRings
    public void RegisterWindRing(Vector3 pos, float radius, float dur)
    {
        if (NetMgr != null && NetMgr.Active && !IsAuthority) { NetMgr.ReqWindRing(pos, radius, dur); return; }   // a client routes it to the host, where the bolts live
        WindRings.Add(new FireRing { Pos = pos, Radius = radius, T = dur });
    }
    private void AgeWindRings(float dt)
    {
        for (int i = WindRings.Count - 1; i >= 0; i--) { var fr = WindRings[i]; fr.T -= dt; if (fr.T <= 0f) WindRings.RemoveAt(i); else WindRings[i] = fr; }
    }
    // (NEW) a quick wind burst where the cyclone swallows a projectile: a small gust ring + swirling motes flung up
    public void SpawnWindPuff(Vector3 pos)
    {
        var col = DamageTypes.Col(DamageType.Wind);
        VfxRing(pos, col, 1.7f, 0.28f);
        SwirlDebris(pos, 1.9f, col, 6, false, 2.4f);
    }
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

    // ---- cottage-garden ritual (the maze event; see the garden-portal region) ----
    private bool _ritualActive = false;        // the 3-minute find-the-statue ritual is running
    private bool _ritualDone = false;          // statue found OR timed out — no more spawns
    private bool _ritualWon = false;           // statue was interacted (gold paid, escape veil)
    private float _ritualTimer = 0f;           // seconds left to find the statue
    private bool _veilActive = false;          // the darkness veil is FLOODING the maze from the statue
    private Vector3 _veilCenter;
    private Node3D _veilNode;
    private MultiMeshInstance3D _veilMM;        // dark mist, one instance per flooded corridor cell
    private int[,] _veilDist;                   // corridor BFS distance from the statue cell (−1 = unreachable, walled off)
    private readonly System.Collections.Generic.List<Vector2I> _veilOrder = new();   // reachable cells, sorted nearest-first
    private float _veilFront = 0f;              // current flood distance in cells (grows over VeilFill seconds)
    private float _veilPhase = 0f;              // drives the fog shimmer
    private int _veilMaxDist = 1, _veilVis = 0;
    private readonly System.Collections.Generic.List<AudioStreamPlayer3D> _whispers = new();   // eerie voices from within the mist
    private float _whisperT = 0f;
    private float _ritualTickT = 0f, _veilDmgT = 0f;
    private const float RitualDur = 180f;   // ~3 min base hunt
    private float VeilFillTime => 128f + 15f * (WardenCount - 1);   // (NEW) MP: +15s escape-veil flood time per extra warden (128 → 143 / 158 / 173)
    private float _locatorT = 0f;              // (NEW) pre-reveal cauldron "sonar" pulse timer
    private bool _cauldronRevealed = false;    // (NEW) latched once the skybeam reveals the cauldron — then pinned on the minimap
    private float RitualDuration => RitualDur * (1f + 0.25f * (WardenCount - 1));   // (NEW) MP: +25% cauldron-hunt time per extra warden (3min → 3.75 / 4.5 / 5.25)
    public bool RitualActive => _ritualActive;
    public float RitualTimeLeft => _ritualTimer;
    public bool RitualVeil => _veilActive;
    public float RitualVeilFrac => _veilMaxDist > 0 ? Mathf.Clamp(_veilFront / _veilMaxDist, 0f, 1f) : 0f;
    public bool RitualWon => _ritualWon;
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
        pos = _maze.CellCenter(Maze.CellOf(_maze, pos));   // snap to the corridor centre so it never hugs/clips a hedge
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
            if (d.Solid) continue;   // (AUTHORED) red collider — solid from the sides but NOT a standing surface (can't get on top)
            bool inside;
            if (d.Cyl)   // cylinder footprint
            {
                float ox = pos.X - d.Center.X, oz = pos.Z - d.Center.Z;
                inside = ox * ox + oz * oz <= d.Half.X * d.Half.X;
            }
            else if (d.Yaw != 0f)   // yawed box → test in the box's local frame (world→local = Godot Y-rot transpose)
            {
                float dx = pos.X - d.Center.X, dz = pos.Z - d.Center.Z;
                float c = Mathf.Cos(d.Yaw), s = Mathf.Sin(d.Yaw);
                inside = Mathf.Abs(dx * c - dz * s) <= d.Half.X && Mathf.Abs(dx * s + dz * c) <= d.Half.Y;
            }
            else inside = Mathf.Abs(pos.X - d.Center.X) <= d.Half.X && Mathf.Abs(pos.Z - d.Center.Z) <= d.Half.Y;
            if (inside && d.TopY <= feetY + step && d.TopY > best) best = d.TopY;
        }
        foreach (var r in Ramps)
        {
            float lx = pos.X - r.Center.X, lz = pos.Z - r.Center.Z;
            if (r.Yaw != 0f) { float c = Mathf.Cos(r.Yaw), s = Mathf.Sin(r.Yaw); float nx = lx * c - lz * s, nz = lx * s + lz * c; lx = nx; lz = nz; }   // (AUTHORED) world→local (Godot Y-rot transpose)
            if (Mathf.Abs(lx) <= r.Half.X && Mathf.Abs(lz) <= r.Half.Y)
            {
                float t = r.AlongX ? (lx + r.Half.X) / (2f * r.Half.X)
                                   : (lz + r.Half.Y) / (2f * r.Half.Y);
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
    public const float IntermissionShort = 3f;   // (NEW) non-rest waves get only a brief "cleared!" beat; the full ~30s rest is every 5th wave
    private float GapAfter(int wave) => (wave % 5 == 0) ? WaveGapMax : IntermissionShort;   // (NEW) intermission only every 5 waves — more action, less pausing
    private float _skipHold = 0f;
    private readonly System.Collections.Generic.HashSet<long> _skipVotes = new();   // (NEW) peers who've voted to skip the current rest/ritual (host tallies)
    private bool _prevSkippable = false;   // (NEW) rising-edge detector — clears votes when a fresh skippable window opens
    private bool _localVoted = false;      // (NEW) this machine already cast its vote for the current window
    private int _netSkipVotes = 0;         // (NEW) host-synced vote tally, shown on clients
    private float _waveSyncT = 0f;         // (NEW) throttle for host→client wave-state sync
    public int SkipVotes => IsAuthority ? _skipVotes.Count : _netSkipVotes;   // (NEW) HUD (clients read the synced tally)
    public int SkipNeeded => Mathf.Max(1, WardenCount);       // (NEW) all players must vote
    public void RegisterSkipVote(long peer) { if (IsAuthority) _skipVotes.Add(peer); }   // (NEW) host records a vote (own = peer 1; clients pass their sender id via RPC)
    public void ApplyWaveState(int wave, float gap, int votes, int mutator = 0) { if (IsAuthority) return; Wave = wave; _tier = Mathf.Max(1f, gap); _netSkipVotes = votes; var m = (WaveMutator)mutator; if (m != ActiveMutator) { ActiveMutator = m; if (m != WaveMutator.None) MutatorBanner(); } }   // (CONTINUOUS) clients mirror the host's difficulty tier (in the `gap` slot) + tally + active mutator
    public float WaveGap => _waveGap;
    public float WaveGapFrac => Mathf.Clamp(_waveGap / WaveGapMax, 0f, 1f);
    public bool InIntermission => _toSpawn.Count == 0 && Enemies.Count == 0 && _waveGap > 0f && Wave >= 1;
    public float SkipHoldFrac => Mathf.Clamp(_skipHold / 2f, 0f, 1f);

    private Enemy _boss;
    private float _bossAddT = 0f;
    private float _bossWaveT = 22f;   // (BOSS-LAIR) while the lair boss is up, waves keep advancing on THIS cadence (never waiting for a clear)
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
    private World _world;
    public long WorldSeed;   // shared map seed; host generates it, clients receive it so everyone gets the same world (NEW)
    private long? _forcedWorldSeed;   // DEV: fixed seed for AI-scenario runs → deterministic map + spawn framing
    public CollisionDebug ColDebug;   // (DEV) collision-bounds visualiser (`colliders` command)
    public ColliderEditor ColEditor;  // (DEV) collider-authoring editor (`cedit` command)
    public void SetWorldVisible(bool v) { if (_world != null) _world.Visible = v; }   // (DEV) hide the streamed terrain/structures/water for the isolated collider-editor stage

    public int Gold = 0;             // persists across runs
    public int Souls = 0;            // (HAUNT ECONOMY) souls come ONLY from kills inside a Haunt (per-player, resets each run) — spent at effigies (+ Sanctuary shrine); rituals are free now
    public int LastWaveGold = 0;
    public bool PerfOverlay = false;   // dev perf/network overlay, toggled by the console 'perf' command (lobby-wide)
    public float GoldFlash = 0f;
    private float _waveTimer = 0f;
    // ---- enemy director (host-side dynamic difficulty) ----
    public float Heat = 1f;                 // difficulty multiplier; rises when the party stomps, falls when it struggles
    private float _waveMinHpFrac = 1f;      // lowest party HP fraction seen during the wave
    private bool _downThisWave = false;     // did anyone go down this wave?
    public float DirectorStatMul => 1f + (Heat - 1f) * 0.5f;   // enemies take HALF the heat as raw HP/damage (density/elites carry the rest)
    // (NEW) global ATTACK-CADENCE multiplier on every foe's swing/fire/dive/heal interval — a tad SLOWER across the board,
    // ramping back UP with difficulty. tier 1 → ×1.18 (18% slower); eases to ×0.9 (a touch faster than base) by tier ~46.
    // Crosses 1.0 around tier ~32 (CATACLYSMIC), so the whole early/mid game breathes more and only the deep end sharpens.
    public float AtkPace => Mathf.Lerp(1.18f, 0.9f, Mathf.Clamp((_tier - 1f) / 45f, 0f, 1f));
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
    // (AUTUMN) warm golden/amber haze through the day (drives fog + horizon), cool misty rose at night
    private static readonly Color[] KfHoriz = {
        new(0.82f,0.68f,0.50f), new(0.80f,0.72f,0.55f), new(0.85f,0.70f,0.52f), new(0.82f,0.52f,0.42f),
        new(0.52f,0.38f,0.44f), new(0.24f,0.28f,0.40f), new(0.16f,0.20f,0.32f), new(0.60f,0.48f,0.48f) };
    private static readonly Color[] KfSun = {
        new(1.00f,0.85f,0.64f), new(1.00f,0.89f,0.70f), new(1.00f,0.83f,0.60f), new(1.00f,0.74f,0.55f),   // (AUTUMN) golden daylight
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
        if (_env != null)
        {
            // (NEW) named-mutator visuals compose on top of day/night here, so they auto-restore when ActiveMutator → None
            bool jng = CurBiome == Biome.Rainforest;   // (NEW) Rainforest: humid green haze reframes the same Heat/mutator visuals
            float ambMul = ActiveMutator == WaveMutator.Eclipse ? 0.42f : 1f;   // eclipse darkens the whole grove
            _env.AmbientLightEnergy = amb * ambMul * (jng ? 1.08f : 1f);
            var fogCol = horiz.Darkened(0.3f);
            if (jng) fogCol = fogCol.Lerp(new Color(0.10f, 0.30f, 0.15f), 0.55f);   // thick emerald jungle mist
            if (ActiveMutator == WaveMutator.BloodMoon) fogCol = fogCol.Lerp(new Color(0.55f, 0.06f, 0.05f), 0.7f);   // crimson blood haze
            else if (ActiveMutator == WaveMutator.Eclipse) fogCol = fogCol.Darkened(0.65f);
            _env.FogLightColor = fogCol;
            _env.FogDensity = ActiveMutator == WaveMutator.Eclipse ? 0.05f : (ActiveMutator == WaveMutator.BloodMoon ? 0.013f : (jng ? 0.017f : 0.007f));   // jungle = thick humid haze / shorter sight
            if (_skyMat != null)
                _skyMat.SetShaderParameter("moon_color", V3lin(ActiveMutator == WaveMutator.BloodMoon ? new Color(0.95f, 0.18f, 0.12f) : new Color(0.95f, 0.96f, 1.0f)));   // blood moon glows red
        }
    }

    private float ComputeTension()
    {
        if (Player == null || !SimActive) return 0f;
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
        CrashLogger.Install();   // global crash + main-thread-freeze diagnostics → user://crash.log + Output
        I = this;
        _rng.Randomize();
        EnemyBolt.Live = 0;
        GameClock = 0f;
        LoadGold();
        SetupInput();
        World.SetTexQuality(TextureQuality);   // apply the persisted Texture Quality BEFORE the world builds (chunks load at that tier)
        var scenSeed = Grove.Dev.Ai.AiTestRunner.ScenarioWorldSeed();   // deterministic world for AI scenarios (stable framing)
        if (scenSeed.HasValue) _forcedWorldSeed = scenSeed.Value;
        BuildWorld();

        ColDebug = new CollisionDebug { Visible = false }; AddChild(ColDebug);   // (DEV) collision visualiser — toggle with the `colliders` command
        ColEditor = new ColliderEditor(); AddChild(ColEditor);   // (DEV) collider-authoring editor — enter with the `cedit` command
        Engine.MaxFps = Mathf.Max(0, MaxFps);   // apply the persisted frame cap (default 60) even on a fresh save

        Player = new Player();
        AddChild(Player);
        Player.GlobalPosition = new Vector3(0, 0, 0);
        Players.Clear();
        Players.Add(Player);   // local player is the first (and currently only) entry

        var layer = new CanvasLayer();
        AddChild(layer);
        Hud = new Hud();
        layer.AddChild(Hud);
        UltOverlay = new UltCastOverlay();   // (NEW) its own CanvasLayer for the ally ult-cast cutouts
        AddChild(UltOverlay);
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
        CharSelectUi = new CharSelect();
        lobbyLayer.AddChild(CharSelectUi);
        CharSelectUi.Hide();
        PerkScreenUi = new PerkScreen();
        lobbyLayer.AddChild(PerkScreenUi);
        PerkScreenUi.Hide();

        var padLayer = new CanvasLayer { Layer = 100 };   // gamepad menu reticle sits above everything (HUD + lobby screens)
        AddChild(padLayer);
        padLayer.AddChild(new PadCursor());

        if (Grove.Dev.Ai.AiTestRunner.TryBoot(this)) return;   // DEV: launched with `-- --scenario <name>` → deterministic AI-test boot (inert otherwise)
        if (s_witch >= 0) { LobbyUi.Hide(); StartGame(); }   // restart kept the chosen witch — skip lobby
        else { State = GameState.Lobby; LobbyUi.Show(); Input.MouseMode = Input.MouseModeEnum.Visible; }
    }

    // DEV visual-test entry: pick a witch and jump straight into a live run (skips lobby/char-select). Used only by AiTestRunner.
    public void StartScenarioRun(int witchIdx)
    {
        s_witch = Mathf.Clamp(witchIdx, 0, 8);
        if (LobbyUi != null) LobbyUi.Hide();
        StartGame();
    }

    // ---- lobby callbacks ----
    public Net NetMgr;
    public Lobby LobbyUi;
    public CharSelect CharSelectUi;
    public PerkScreen PerkScreenUi;
    public void OpenPerks() { if (LobbyUi != null) LobbyUi.Hide(); if (PerkScreenUi != null) PerkScreenUi.Show(s_witch < 0 ? 0 : s_witch); }
    public void ClosePerks() { if (PerkScreenUi != null) PerkScreenUi.Hide(); if (LobbyUi != null) LobbyUi.Show(); }

    // ---- pause-menu run controls (Options overlay / Quit Run / Restart Run) --------------------------------------------
    public bool InGameOptions = false;   // true while the full main-menu options panel is overlaid on the paused run
    // only the host (or a solo player) may restart the shared run — a MP client can't yank everyone back to the start
    public bool CanRestartRun() => NetMgr == null || !NetMgr.Active || NetMgr.IsHost;
    private void ResumeRun() { SaveGold(); State = GameState.Playing; Input.MouseMode = Input.MouseModeEnum.Captured; }
    // open the SAME options page as the main menu, but overlaid transparently so the paused game shows behind it
    public void OpenInGameOptions()
    {
        InGameOptions = true;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        LobbyUi?.ShowOptionsOverlay();
    }
    // Back / Esc from the options overlay → return to the pause menu (still paused)
    public void CloseInGameOptions()
    {
        InGameOptions = false;
        LobbyUi?.HideOptionsOverlay();
        SelectLock = 0.2f;   // so the same Esc that closed options doesn't also resume the run
    }
    // Quit Run → tear down the run and return to the home screen. Works in MP too: host ends the session for everyone; a client just leaves.
    private void QuitRun()
    {
        InGameOptions = false; LobbyUi?.HideOptionsOverlay();
        if (NetMgr != null && NetMgr.Active)
        {
            if (NetMgr.IsHost) { NetMgr.BroadcastGameOverChoice(2); return; }   // 2 = End: disconnects + reloads on every peer (incl. host)
            NetMgr.Disconnect();                                                // client leaves the host's session
        }
        s_witch = -1; GetTree().ReloadCurrentScene();                          // solo / departed client → back to the main menu
    }
    // Restart Run → fresh run, same witch. Solo reloads the scene; MP host restarts for everyone; MP clients are blocked.
    private void RestartRun()
    {
        if (NetMgr != null && NetMgr.Active)
        {
            if (!NetMgr.IsHost) return;                // clients can't restart
            InGameOptions = false; LobbyUi?.HideOptionsOverlay();
            NetMgr.BroadcastGameOverChoice(1);         // 1 = Retry: SoftResetRun + StartGame on every peer
            return;
        }
        GetTree().ReloadCurrentScene();                // solo: full clean restart (s_witch kept ≥ 0 → same witch)
    }
    public int ReadyCount = 0;   // (NEW) MP char-select: how many wardens have locked in (synced from host)
    public RunStats MyStats = new RunStats();                                                        // (NEW) this player's end-of-run tally
    public readonly System.Collections.Generic.Dictionary<long, RunStats> AllStats = new();          // (NEW) every warden's PERSONAL tally (kills come from the host-authoritative tally below)
    // (NEW) host/solo-authoritative kill attribution — the ONLY exact way in HOST-OWNS-WORLD (clients can't tell who landed the killing blow)
    public long AttackerPeer = 1;   // the peer currently dealing damage (set per-hit; host's own = LocalPeer, a client's routed hit = the reporter)
    public readonly System.Collections.Generic.Dictionary<long, int> KillTally = new();
    public readonly System.Collections.Generic.Dictionary<long, int> NightKillTally = new();
    public void CreditKill(long peer, bool night)
    {
        KillTally[peer] = KillTally.GetValueOrDefault(peer) + 1;
        if (night) NightKillTally[peer] = NightKillTally.GetValueOrDefault(peer) + 1;
    }
    // (NEW) souls are contribution-based, not last-hit: EVERY peer that dealt any damage to a slain enemy earns 1 soul
    // (bolt/beam/melee/AoE/DoT/field/summon all funnel through Enemy.Hurt, which records the source peer). Host/solo-
    // authoritative (only the host reaches Enemy.Die): the host credits its own souls locally and RPCs allies theirs.
    public void CreditSouls(System.Collections.Generic.HashSet<long> contributors)
    {
        if (contributors == null || contributors.Count == 0) { Souls++; return; }   // fallback (e.g. an untracked kill) — credit the local reaper
        long me = LocalPeer;
        foreach (long peer in contributors)
        {
            if (peer == 0 || peer == me) Souls++;
            else NetMgr?.GrantSoul(peer, 1);
        }
    }
    // (MAGNET LUCK) the highest Luck among every warden that dealt damage to a foe — used to bias its magnet drop. Solo = local luck.
    public float BestContributorLuck(System.Collections.Generic.HashSet<long> contributors)
    {
        float mine = Player != null ? Player.S.Luck : 0f;
        if (contributors == null || contributors.Count == 0) return mine;
        float best = 0f;
        foreach (long peer in contributors)
            best = Mathf.Max(best, (peer == 0 || peer == LocalPeer) ? mine : (NetMgr != null ? NetMgr.PeerLuck(peer) : 0f));
        return best;
    }
    // (NEW) XP tuning — applied once to each kill's orb value (Enemy.Die):
    //  · XpGainMul  : global trim so a FRENZIED run (Heat pinned near the 1.6 cap) lands ~level 25 by the end of wave 10.
    //  · party damp : divide by the spawn-density bump (1 + 0.55·extra wardens) so per-player XP ≈ solo at ANY player count
    //    (XP is shared — every warden banks the full orb — so without this, more bodies = faster leveling for everyone).
    public const float XpGainMul = 0.41f;
    public float XpKillMul => XpGainMul / (1f + 0.55f * (WardenCount - 1));
    // solo or host = we own/drive the world; a connected client does not
    public void GrantSharedXp(float amt)
    {
        Player?.AddXp(amt);
        NetMgr?.BroadcastXp(amt);   // allies level on the same XP
    }

    // ---- hold-E to interact with world objects (first to finish the hold claims it) ----
    private const float HoldTime = 0.5f;   // (TUNE) halved — the run is much faster-paced now, so hold-E interactions snap in twice as quick
    private const float InstantHold = 0.02f;   // (TUNE) effectively a single tap — used for chests/vendors/effigies/rituals/shrines (everything but travel + the boss lair)
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
        _holdAction = null; _holdPrompt = ""; _holdNeed = HoldTime; HoldEIsRitual = false;
        if (Player == null || !CanControlLocal()) { _holdE = 0f; return; }
        Vector3 me = Player.GlobalPosition;
        float best = 3.5f * 3.5f;
        System.Action act = null; string prompt = "";
        bool ritualTarget = false;
        float need = HoldTime;   // per-winner hold time — each block below sets it (instant for chests/vendors/effigies/rites/shrines, hold for travel/revive/boss lair)

        if (IsAuthority)
        {
            foreach (var c in Chests)
            {
                if (c == null || !GodotObject.IsInstanceValid(c) || c.Opened) continue;
                float d = (c.GlobalPosition - me).LengthSquared();
                if (d < best) { best = d; var cc = c; act = () => cc.Open(0); prompt = "E — open chest"; need = InstantHold; }
            }
        }
        else if (NetMgr != null && NetMgr.NearestPickupChest(me, 3.5f, out int cid, out float cd2))
        {
            if (cd2 < best) { best = cd2; int id = cid; act = () => NetMgr.RequestOpenChest(id); prompt = "E — open chest"; need = InstantHold; }
        }

        if (_mystic != null && GodotObject.IsInstanceValid(_mystic) && !_mystic.Triggered)
        {
            float d = (_mystic.GlobalPosition - me).LengthSquared();
            if (d < best) { best = d; var m = _mystic; act = () => { if (!IsAuthority) NetMgr?.ClaimVendor(m.NetId); OpenMystic(m); }; prompt = "E — the Mystic"; need = InstantHold; }
        }
        if (_scroll != null && GodotObject.IsInstanceValid(_scroll) && !_scroll.Triggered)
        {
            float d = (_scroll.GlobalPosition - me).LengthSquared();
            if (d < best) { best = d; var s = _scroll; act = () => { if (!IsAuthority) NetMgr?.ClaimVendor(s.NetId); OpenScroll(s); }; prompt = "E — the Scrolls"; need = InstantHold; }
        }
        if (_shop != null && GodotObject.IsInstanceValid(_shop))   // NOT claimed — the peddler lingers so both players can shop
        {
            float d = (_shop.GlobalPosition - me).LengthSquared();
            if (d < best) { best = d; var sh = _shop; act = () => OpenShop(sh); prompt = "E — the Peddler"; need = InstantHold; }
        }
        foreach (var r in _roulettes)
        {
            if (r == null || !GodotObject.IsInstanceValid(r) || r.Triggered) continue;
            float d = (r.GlobalPosition - me).LengthSquared();
            if (d < best) { best = d; var rr = r; act = () => { if (!IsAuthority) NetMgr?.ClaimRoulette(rr.NetId); OpenRoulette(rr); }; prompt = "E — spin the wheel"; need = InstantHold; }
        }

        // Expedition objective: light the active beacon
        if (IsAuthority && InExpedition && _expoRun != null && _expoRun.BeaconReady)
        {
            float d = (_expoRun.ActivePos - me).LengthSquared();
            if (d < best) { best = d; act = () => _expoRun.LightBeacon(this); prompt = "Hold E — light the beacon"; need = HoldTime; }
        }
        else if (!IsAuthority && InExpedition && _expoRun != null && _expoRun.BeaconReady)   // client asks the host to light it
        {
            float d = (_expoRun.ActivePos - me).LengthSquared();
            if (d < best) { best = d; act = () => NetMgr?.RequestLightBeacon(); prompt = "Hold E — light the beacon"; need = HoldTime; }
        }

        // revive a downed ally (networked); a charged Divine witch revives instantly with a sky-beam
        if (NetMgr != null && NetMgr.Active && NetMgr.NearestDownedAlly(me, 3.5f, out long rpeer, out float rd2))
        {
            if (rd2 < best)
            {
                best = rd2; long peer = rpeer;
                bool divine = Player.DivineWitch && Player.Interventions > 0;
                need = divine ? InstantHold : HoldTime;
                act = () =>
                {
                    if (Player.DivineWitch && Player.Interventions > 0) { Player.Interventions--; NetMgr.RevivePeer(peer, 1f, true); }
                    else NetMgr.RevivePeer(peer, 0.4f, false);
                    MyStats.Revives++;   // (NEW) end-of-run tally
                };
                prompt = divine ? "Hold E — Divine Revival" : "Hold E — revive ally";
            }
        }

        // (NEW) level portal — hold E to teleport the party to the next biome
        if (_levelPortalActive)
        {
            float dp = (_levelPortalPos - me).LengthSquared();
            if (dp < best) { best = dp; act = () => AdvanceLevel(); prompt = "Hold E — enter the portal"; need = HoldTime; }
        }
        // (NEW) leave the maze — hold E on the exit portal (3.5u reach clears any decor/blocker push-out)
        if (InMaze && _mazePortalNode != null && _maze != null)
        {
            var pp = _maze.PortalPos;
            float d = (new Vector3(pp.X, me.Y, pp.Z) - me).LengthSquared();
            if (d < best) { best = d; act = () => ExitMaze(); prompt = "Hold E — leave the maze"; need = HoldTime; }
        }
        // (NEW) garden travel portals + maze gate — hold E, and ONLY between waves (dormant while a wave is on)
        if (!InMaze)   // (CONTINUOUS) no more intermissions — garden portals + the maze well are usable anytime now
        {
            for (int i = 0; i < _gPortals.Count; i++)
            {
                var pt = _gPortals[i];
                if (pt == null || !GodotObject.IsInstanceValid(pt) || pt.Cooldown > 0f) continue;
                float d = (pt.GlobalPosition - me).LengthSquared();
                if (d < best) { best = d; var p = pt; act = () => TakeGardenPortal(p); prompt = "Hold E — step through the portal"; need = HoldTime; }
            }
            if (_gateActive)   // the old moss well — hold E to descend into the maze
            {
                float d = (new Vector3(_gatePos.X, me.Y, _gatePos.Z) - me).LengthSquared();
                if (d < best) { best = d; act = () => { if (IsAuthority) EnterGardenMaze(); else NetMgr?.RequestEnterMaze(); }; prompt = "Hold E — descend into the well"; need = HoldTime; }
            }
        }
        // (NEW) jungle vine — a quick E rides you up the vine and flings you skyward. Sky-island vines only grab while AIRBORNE
        for (int i = 0; i < Vines.Count; i++)
        {
            if (Vines[i].Sky && Player.Grounded) continue;   // can't grab an island's own vine while standing on that island — only while falling
            float d = (Vines[i].Pos - me).LengthSquared();
            if (d < best) { best = d; need = 0.15f; float ty = Vines[i].TopY; float fv = Vines[i].Sky ? 11f : 20f; act = () => Player.VineLaunch(ty, fv); prompt = "Hold E — grab the vine"; }
        }
        // (NEW) sky-islands whirlwind — hold E to ride it up into the ritual, or re-ride after falling out
        if (_skyWhirlActive)
        {
            float d = (new Vector3(_skyWhirlPos.X, me.Y, _skyWhirlPos.Z) - me).LengthSquared();
            if (d < best) { best = d; need = HoldTime;
                act = () => { if (InSky) { if (_sky != null) Player.TeleportReset(_sky.Entry); } else if (IsAuthority) EnterSky(); else NetMgr?.RequestEnterSky(); };
                prompt = InSky ? "Hold E — ride back up" : "Hold E — ride the updraft into the sky"; }
        }
        // (NEW) sky effigies — hold E to light each; all lit awakens the cauldron
        if (InSky && _sky != null)
        {
            for (int i = 0; i < _sky.Effigies.Count; i++)
            {
                if (i < _skyEffigyLit.Count && _skyEffigyLit[i]) continue;
                float d = (new Vector3(_sky.Effigies[i].X, me.Y, _sky.Effigies[i].Z) - me).LengthSquared();
                if (d < best) { best = d; int idx = i; need = InstantHold;
                    act = () => { if (IsAuthority) LightSkyEffigy(idx); else NetMgr?.RequestSkyEffigy(idx); };
                    prompt = "E — light the effigy"; }
            }
        }

        // (EFFIGY) scattered blessing shrines — hold E to rouse one (costs gold; a claimed shrine is spent)
        for (int i = 0; i < Effigies.Count; i++)
        {
            var ef = Effigies[i];
            if (ef == null || !GodotObject.IsInstanceValid(ef) || ef.Claimed) continue;
            float de = (ef.GlobalPosition - me).LengthSquared();
            if (de < best) { best = de; var e = ef; int cost = EffigyCost(e.Kind); need = InstantHold;
                act = () => TryActivateEffigy(e, cost); prompt = $"E — {Effigy.KindName(e.Kind)} effigy · ☠{cost}"; }
        }

        // (NEW) ritual circles — stand ANYWHERE inside the ring + hold E to begin (no dead-center reach). Inside = top priority.
        for (int i = 0; i < Rituals.Count; i++)
        {
            var rc = Rituals[i];
            if (rc == null || !GodotObject.IsInstanceValid(rc) || rc.Active || rc.Done) continue;
            float flat = new Vector2(rc.GlobalPosition.X - me.X, rc.GlobalPosition.Z - me.Z).Length();
            if (flat > rc.Radius) continue;                       // must be inside the circle
            float eff = 0.05f;                                    // inside → beats the 3.5u default reach (the rite you're standing in wins)
            if (eff < best) { best = eff; var r = rc; need = InstantHold; ritualTarget = true;
                string rn = r.Type == RiteType.Ward ? "warding" : r.Type == RiteType.Summon ? "summoning" : "cleansing";
                act = () => TryActivateRitual(r); prompt = $"E — begin the {rn} rite"; }
        }

        // (BOSS-LAIR) the world boss objective — stand at the gate + hold E to challenge (from wave 2 on; nothing once conquered)
        if (_bossLair != null && GodotObject.IsInstanceValid(_bossLair) && _bossLair.State == 0)
        {
            float flat = new Vector2(_bossLair.GlobalPosition.X - me.X, _bossLair.GlobalPosition.Z - me.Z).Length();
            if (flat <= BossLair.Radius && 0.04f < best)
            {
                best = 0.04f; need = HoldTime; ritualTarget = false;
                if (Wave >= 2) { act = () => TryChallengeBoss(); prompt = "Hold E — CHALLENGE the boss lair"; }
                else { HoldEDisabled = true; HoldEDisabledText = "the lair is sealed — survive a wave to break it"; }
            }
        }

        // (NERFER) the three Grove shrines — hold E to activate (each kind has its own flow/cost)
        for (int i = 0; i < _nerfers.Count; i++)
        {
            var s = _nerfers[i];
            if (s == null || !GodotObject.IsInstanceValid(s)) continue;
            bool can = s.Kind == NerfKind.Sanctuary ? (s.State < 2 && !_sanctuaryPaid.Contains(LocalPeer)) : (s.State == 0);
            if (!can) continue;
            float dn = (new Vector3(s.GlobalPosition.X, me.Y, s.GlobalPosition.Z) - me).LengthSquared();
            if (dn > NerfShrine.Radius * NerfShrine.Radius || dn >= best) continue;
            best = dn; var sh = s; need = InstantHold; ritualTarget = false;
            prompt = sh.Kind switch
            {
                NerfKind.Summoner  => "E — begin the Summoning (defend ~45s)",
                NerfKind.Sacrifice => "E — SACRIFICE: −40% HP + slay the guardians",
                _                  => $"E — offer {SanctuaryShare} souls to the Sanctuary ({_sanctuaryPaid.Count}/{Mathf.Max(1, WardenCount)})",
            };
            act = () => TryActivateNerfer(sh);
        }

        // during a WAVE the garden portals + well are dormant — if you walk up to one, show WHY (greyed, no action)
        HoldEDisabled = false; HoldEDisabledText = "";   // (CONTINUOUS) garden portals + the maze well are usable anytime now — no "dormant during wave" gating

        _holdNeed = need;
        _holdAction = act; _holdPrompt = prompt; HoldEIsRitual = ritualTarget;
        // interact = hold E, or hold gamepad X (but not while LB is held — that's a spell chord)
        bool holding = act != null && (Input.IsPhysicalKeyPressed(Key.E) || (PadActive && Input.IsJoyButtonPressed(0, JoyButton.X) && !PadSpellHeld()));
        if (holding)
        {
            // (FIX) ONE activation per press. Interactables re-arm the moment the action returns, and with the near-instant
            // hold time that meant a held E re-fired every frame or two — which on a refused action (not enough souls, etc.)
            // machine-gunned the failure sound. The latch clears only when E is actually released.
            if (!_holdConsumed)
            {
                _holdE += dt;
                if (_holdE >= _holdNeed) { _holdE = 0f; _holdConsumed = true; var a = act; _holdAction = null; a(); }
            }
        }
        else { _holdE = 0f; _holdConsumed = false; }
    }
    private bool _holdConsumed = false;   // (FIX) latched after an activation until E is released — no auto-repeat
    public bool HoldEDisabled { get; private set; } = false;
    public string HoldEDisabledText { get; private set; } = "";
    public bool HoldEIsRitual { get; private set; } = false;   // (NEW) the current hold target is a ritual circle → HUD draws its prompt on the world panel, not center-screen

    public bool IsAuthority => NetMgr == null || !NetMgr.Active || NetMgr.IsHost;
    public int WardenCount => (NetMgr != null && NetMgr.Active) ? NetMgr.PlayerCount() : 1;

    // (NEW) the open, bounded Grove/Jungle — where the World.WorldRadius cliff-wall boundary applies. The maze, expedition
    // rooms, and sky-islands are their own self-contained arenas centered elsewhere, so the disc boundary is off there.
    public bool InOverworld => !InMaze && !InExpedition && !InSky;
    // clamp an XZ position to stay inside the bounded overworld disc (no-op outside the overworld). `margin` keeps things off the wall.
    public Vector3 ClampToWorld(Vector3 p, float margin = 6f)
    {
        if (!InOverworld) return p;
        float rr = World.WorldRadius - margin;
        float d2 = p.X * p.X + p.Z * p.Z;
        if (d2 > rr * rr) { float k = rr / Mathf.Sqrt(d2); p.X *= k; p.Z *= k; }
        return p;
    }

    // ---- minimap fog of war (NEW) ----
    // The minimap starts fully fogged; a forward vision CONE (up to 40u, plus a small always-known radius right around you)
    // permanently clears cells as you sweep it around. Discoverables (chests/effigies/vendors/rituals/portals) only show on a
    // revealed cell. Persists per-world; cleared on map change / run reset. Cells are World.XZ / DiscCell, packed into a long.
    public const float DiscCell = 7f;
    private readonly System.Collections.Generic.HashSet<long> _discovered = new();
    private static long CellKeyAt(int cx, int cz) => ((long)cx << 32) ^ (uint)cz;
    public bool DiscoveredCell(int cx, int cz) => _discovered.Contains(CellKeyAt(cx, cz));
    public bool Discovered(Vector3 w)   // outside the overworld there's no fog (maze/sky/expedition show everything)
        => !InOverworld || _discovered.Contains(CellKeyAt(Mathf.FloorToInt(w.X / DiscCell), Mathf.FloorToInt(w.Z / DiscCell)));
    public void ClearDiscovered() => _discovered.Clear();
    public void RevealMinimap()
    {
        if (!InOverworld || Player == null) return;
        Vector3 pc = Player.GlobalPosition;
        Vector3 fwd = -Player.GlobalTransform.Basis.Z; fwd.Y = 0f;
        fwd = fwd.LengthSquared() < 0.0001f ? Vector3.Forward : fwd.Normalized();
        const float R = 40f, near = 12f, cosHalf = 0.5f;   // 40u forward cone (~120° wide) + a 12u all-around "you know where you stand"
        int pcx = Mathf.FloorToInt(pc.X / DiscCell), pcz = Mathf.FloorToInt(pc.Z / DiscCell);
        int cells = Mathf.CeilToInt(R / DiscCell) + 1;
        for (int cx = pcx - cells; cx <= pcx + cells; cx++)
            for (int cz = pcz - cells; cz <= pcz + cells; cz++)
            {
                float ox = (cx + 0.5f) * DiscCell - pc.X, oz = (cz + 0.5f) * DiscCell - pc.Z;
                float d = Mathf.Sqrt(ox * ox + oz * oz);
                if (d > R) continue;
                bool reveal = d <= near || (d > 0.01f && (ox * fwd.X + oz * fwd.Z) / d >= cosHalf);
                if (reveal) _discovered.Add(CellKeyAt(cx, cz));
            }
    }

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
    // (NEW) route an Ember burn tick's lifesteal to its caster (Wildfire Rush). The owner heals only if their lifesteal window is live.
    public void AwardBurnLifesteal(int owner, float dmg)
    {
        if (dmg <= 0f) return;
        if (owner == 0) owner = LocalPeer;
        if (NetMgr == null || !NetMgr.Active || owner == LocalPeer) Player?.TryBurnLifesteal(dmg);
        else NetMgr.SendBurnHeal(owner, dmg);
    }
    // (NEW) snake root: route to the touched player's machine (they enforce the ground-only + 5s throttle)
    public void TrySnakeRoot(long peer, int snakeId)
    {
        if (peer == 0) Player?.TrySnakeRoot(snakeId);
        else NetMgr?.SendSnakeRoot(peer, snakeId);
    }
    public void NotifySnakeDied(int snakeId)   // the snake died → free anyone it had rooted, everywhere
    {
        Player?.ClearSnakeRoot(snakeId);
        NetMgr?.BroadcastSnakeDied(snakeId);
    }
    private int _netEnemySeq = 1;

    // ---- shared world-run / pause model ----
    // Each player has a category: 0 = active (playing, or in a non-pausing menu like ult/stats),
    // 1 = soft pause (ESC — only pauses the world if EVERYONE pauses), 2 = level-up gate (pauses for all).
    public bool WorldRunning = true;
    public int LocalCat() => State switch
    {
        GameState.Pause => 1,
        // (MP CONTINUE-AROUND) in multiplayer, level-up + equip/swap no longer freeze the shared world — the menuing witch is bubbled
        // (immune) and the fight rolls on around her. SOLO still hard-pauses to pick. Roulette/Element/BindKey stay hard gates.
        GameState.LevelUp or GameState.Swap or GameState.Attune => (ChestPick || (NetMgr != null && NetMgr.Active)) ? 0 : 2,
        GameState.Roulette or GameState.Element or GameState.BindKey => ChestPick ? 0 : 2,
        GameState.Playing or GameState.Stats or GameState.Ult or GameState.UltMenu or GameState.Mystic or GameState.Scroll or GameState.Shop => 0,
        _ => 0
    };
    // local player may act only while playing AND the shared world is running
    public bool CanControlLocal() => State == GameState.Playing && WorldRunning && !ConsoleOpen && !(Player != null && Player.Downed);

    // (MP CONTINUE-AROUND) world entities keep simulating whenever the shared world runs AND we're either actually playing or in MP.
    // Solo: a menu leaves Playing → SimActive false → everything pauses (traditional). MP: menus (Shop/LevelUp/Swap/…) keep the fight live.
    public bool SimActive => WorldRunning && (State == GameState.Playing || (NetMgr != null && NetMgr.Active));
    // true exactly when THIS machine is in a menu while the world keeps running around it — drives the local witch's immunity bubble.
    public bool MenuImmune => State != GameState.Playing && SimActive;

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
        ReadyCount = 0; NetMgr?.ResetReady();
        if (CharSelectUi != null) { CharSelectUi.Show(); CharSelectUi.Refresh(); }
    }

    // (NEW) leave character-select and return to the main menu. Tears down any pending host/join session so a
    // player who backed out of an MP lobby isn't left half-connected; re-entering Solo/Host/Join sets it up fresh.
    public void BackToLobbyFromSelect()
    {
        if (State != GameState.CharSelect) return;
        if (CharSelectUi != null) CharSelectUi.Hide();
        if (NetMgr != null && NetMgr.Active) NetMgr.Disconnect();
        ReadyCount = 0; NetMgr?.ResetReady();
        State = GameState.Lobby; Input.MouseMode = Input.MouseModeEnum.Visible;
        if (LobbyUi != null) { LobbyUi.Show(); LobbyUi.ShowMain(); }
    }

    // (NEW) called by the CharSelect UI when a warden locks in. Solo starts at once; in co-op we report ready and WAIT
    // for every connected warden before the host begins the run (Net.ReportReady → all ready → Net broadcasts BeginRun).
    public void ConfirmWitch(int i)
    {
        s_witch = i;
        if (NetMgr != null && NetMgr.Active) NetMgr.ReportReady();
        else StartGame();
    }
    public void BeginRunFromSelect()
    {
        if (CharSelectUi != null) CharSelectUi.Hide();
        StartGame();
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
        InGameOptions = false; LobbyUi?.HideOptionsOverlay();   // (MP) if a client was in the pause options overlay when the host restarted, dismiss it
        s_dynLights = 0;   // reset the transient-light budget for a clean run
        if (CharSelectUi != null) CharSelectUi.Hide();
        ConfigureWitch(s_witch < 0 ? 0 : s_witch);
        Player.ResetPerks();   // (ATTUNE) graph perks: buy nodes with attribute points earned per level (14 cap); hidden routes fire free
        MetaUnlocks.Apply(Player);   // (NEW) permanent cross-witch gold-tree unlocks (+finisher / +mod / +mana slot)
        Souls = 0;                   // (NEW) souls reset each run
        MyStats = new RunStats { WitchIdx = s_witch < 0 ? 0 : s_witch, Slot = (NetMgr != null && NetMgr.Active && !NetMgr.IsHost) ? 1 : 0 };   // (NEW) fresh end-of-run tally
        AllStats.Clear(); KillTally.Clear(); NightKillTally.Clear(); ClearDiscovered();
        Player.Hp = Player.S.MaxHp; Player.Mana = Player.S.ManaMax; Player.DashStock = Player.S.DashCharges; Player.Downed = false;   // full vitals (also covers an MP retry after a soft-reset)
        // in co-op, host and joiner spawn a few steps apart at the same area so they can see each other
        Vector3 spawn = (NetMgr != null && NetMgr.Active) ? (NetMgr.IsHost ? new Vector3(-2.5f, 0, 0) : new Vector3(2.5f, 0, 0)) : Vector3.Zero;
        Player.GlobalPosition = SafeSpawn(spawn);   // (SPAWN SAFETY) never start inside water / a structure / ruins; land on clear ground
        _spawnSettleT = 2f;                          // structures stream in per-chunk — re-nudge out of any that load ON the spawn
        if (IsAuthority) ResetDifficulty();   // (CONTINUOUS DIRECTOR) start the difficulty clock; the stream spawns continuously
        if (IsAuthority) PopulateMap();        // (MAP FILL) scatter EVERYTHING across the whole bounded disc, once, at load
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
        if (Player != null) { Player.GlobalPosition = _maze.CellCenter(_maze.Spawns[idx]) + new Vector3(0f, 1f, 0f); MoveEntsTo(Player.GlobalPosition, _preMazePos, 30f); }   // ents that were with you enter too
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

    // (NEW) leave the maze — rebuild the open world and drop the player back OUTSIDE the well (which caves in).
    // escaped = you reached the exit alive (→ 300 gold if you'd lit the cauldron); false = you were spat out on death.
    public void ExitMaze(bool escaped = true)
    {
        bool ritualWon = _ritualWon;   // capture before ClearRitual wipes it
        if (_mazeRoot != null && GodotObject.IsInstanceValid(_mazeRoot)) { _mazeRoot.QueueFree(); _mazeRoot = null; }
        if (_mazePortalNode != null && GodotObject.IsInstanceValid(_mazePortalNode)) { _mazePortalNode.QueueFree(); _mazePortalNode = null; }
        Blockers.Clear(); Decks.Clear(); Ramps.Clear(); WallBlockers.Clear();
        InExpedition = false; InMaze = false; _maze = null; _mazeFound = false; _mazeStatueTarget = -1;
        _mazeChaseDist = null;
        ClearRitual();
        foreach (var e in Enemies.ToArray()) if (GodotObject.IsInstanceValid(e)) e.QueueFree(); Enemies.Clear(); _toSpawn.Clear();   // the maze horde stays in the maze — don't let it chase you back out
        foreach (var oc in Chests) if (GodotObject.IsInstanceValid(oc)) oc.QueueFree(); Chests.Clear();   // maze chests don't follow you back to the grove
        MazeWisps.Clear();
        _world = new World();
        _world.SetSeed((ulong)WorldSeed);
        AddChild(_world);
        // climb out just OUTSIDE the well (on the garden path), not back where you happened to be
        Vector3 exitSpot = _gateActive ? new Vector3(_gatePos.X, 0, _gatePos.Z - 4f) : _preMazePos;
        exitSpot = new Vector3(exitSpot.X, SurfaceHeight(exitSpot, 80f) + 1f, exitSpot.Z);
        _world.Update(exitSpot);
        var mazePos = Player != null ? Player.GlobalPosition : exitSpot;
        if (Player != null) Player.GlobalPosition = exitSpot;
        MoveEntsTo(exitSpot, mazePos, 30f);   // ents in the maze near you follow you back to the grove
        CaveInWell();   // the well collapses behind you — a one-time descent
        NetMgr?.ReportLeftMaze();   // I'm out — the host stops counting me for the maze death rules
        _waveGap = 30f;   // give everyone 30s to get their bearings before the next wave
        bool won = escaped && ritualWon;
        if (won) { AddGold(300); Hud?.Banner("ESCAPED — 300 gold!"); }   // the reward is for ESCAPING, not for finding the cauldron
        else Hud?.Banner(escaped ? "back to the grove — the well has caved in" : "spat out of the dark — the well has caved in");
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
    public Vector3? MazeStatueTargetPos => (InMaze && !_mazeFound && !_gardenRitual && _maze != null && _mazeStatueTarget >= 0 && _mazeStatueTarget < _maze.Chambers.Count) ? _maze.CellCenter(_maze.Chambers[_mazeStatueTarget]) : (Vector3?)null;   // garden ritual keeps the statue HIDDEN
    public Color MazeStatueColor => (_maze != null && _mazeStatueTarget >= 0 && _mazeStatueTarget < _maze.ChamberElem.Count) ? DamageTypes.Col((DamageType)_maze.ChamberElem[_mazeStatueTarget]) : Colors.White;
    public bool MazeFound => _mazeFound;   // (NEW) the exit portal is open
    public Vector3? MazeCauldronRevealedPos => (InMaze && _gardenRitual && _cauldronRevealed && RitualStatueValid) ? RitualStatueWorld() : (Vector3?)null;   // (NEW) pinned once the skybeam reveals it

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

    // ================= (PERF) XZ spatial grids =================
    // Enemies scanned the ENTIRE Blockers/Decks lists (hundreds in the jungle/maze) every frame in AvoidBlockers/ClampArena,
    // and each other in SeparateFromEnemies — O(N×blockers) + O(N²). These grids bucket obstacles/enemies into 5m XZ cells so
    // a query touches only the local neighbourhood. Self-rebuilding: obstacle grid rebuilds when the list count changes (chunk
    // stream / maze build); enemy grid rebuilds once per frame. No edits needed at the mutation sites. Single-threaded → no locks.
    public const float GridCell = 5f;
    private readonly Dictionary<long, List<int>> _blockerGrid = new();
    private readonly Dictionary<long, List<int>> _deckGrid = new();
    private readonly Dictionary<long, List<Enemy>> _enemyGrid = new();
    private readonly List<int> _blkScratch = new();
    private readonly List<int> _deckScratch = new();
    private readonly List<Enemy> _enemyScratch = new();
    private int[] _blkStamp = System.Array.Empty<int>(); private int _blkQuery = 0;
    private int[] _deckStamp = System.Array.Empty<int>(); private int _deckQuery = 0;
    private int _gridBlkCount = -1, _gridDeckCount = -1;
    private ulong _enemyGridFrame = ulong.MaxValue;

    private static long PackCell(int cx, int cz) => ((long)cx << 32) ^ (uint)cz;
    private static int CellCoord(float w) => Mathf.FloorToInt(w / GridCell);
    private static void GridAddIdx(Dictionary<long, List<int>> g, int cx, int cz, int idx)
    {
        long k = PackCell(cx, cz);
        if (!g.TryGetValue(k, out var l)) { l = new List<int>(4); g[k] = l; }
        l.Add(idx);
    }
    private void RebuildObstacleGrid()
    {
        foreach (var kv in _blockerGrid) kv.Value.Clear();
        foreach (var kv in _deckGrid) kv.Value.Clear();
        for (int i = 0; i < Blockers.Count; i++)
        {
            var b = Blockers[i];
            int x0 = CellCoord(b.Pos.X - b.Radius), x1 = CellCoord(b.Pos.X + b.Radius);
            int z0 = CellCoord(b.Pos.Z - b.Radius), z1 = CellCoord(b.Pos.Z + b.Radius);
            for (int cx = x0; cx <= x1; cx++) for (int cz = z0; cz <= z1; cz++) GridAddIdx(_blockerGrid, cx, cz, i);
        }
        for (int i = 0; i < Decks.Count; i++)
        {
            var d = Decks[i];
            int x0 = CellCoord(d.Center.X - d.Half.X), x1 = CellCoord(d.Center.X + d.Half.X);
            int z0 = CellCoord(d.Center.Z - d.Half.Y), z1 = CellCoord(d.Center.Z + d.Half.Y);
            for (int cx = x0; cx <= x1; cx++) for (int cz = z0; cz <= z1; cz++) GridAddIdx(_deckGrid, cx, cz, i);
        }
        if (_blkStamp.Length < Blockers.Count) _blkStamp = new int[Blockers.Count + 64];
        if (_deckStamp.Length < Decks.Count) _deckStamp = new int[Decks.Count + 64];
        _gridBlkCount = Blockers.Count; _gridDeckCount = Decks.Count;
    }
    private void EnsureObstacleGrid() { if (_gridBlkCount != Blockers.Count || _gridDeckCount != Decks.Count) RebuildObstacleGrid(); }

    // Shared buffer of Blocker indices near (x,z) within `reach`. Valid until the NEXT QueryBlockers call — consume it immediately.
    public List<int> QueryBlockers(float x, float z, float reach)
    {
        EnsureObstacleGrid();
        _blkScratch.Clear();
        if (Blockers.Count == 0) return _blkScratch;
        _blkQuery++;
        int x0 = CellCoord(x - reach), x1 = CellCoord(x + reach), z0 = CellCoord(z - reach), z1 = CellCoord(z + reach);
        for (int cx = x0; cx <= x1; cx++) for (int cz = z0; cz <= z1; cz++)
            if (_blockerGrid.TryGetValue(PackCell(cx, cz), out var l))
                for (int j = 0; j < l.Count; j++) { int idx = l[j]; if (idx >= _blkStamp.Length) { _blkScratch.Add(idx); continue; } if (_blkStamp[idx] == _blkQuery) continue; _blkStamp[idx] = _blkQuery; _blkScratch.Add(idx); }
        return _blkScratch;
    }
    // Shared buffer of Deck indices near (x,z). Valid until the NEXT QueryDecks call.
    public List<int> QueryDecks(float x, float z, float reach)
    {
        EnsureObstacleGrid();
        _deckScratch.Clear();
        if (Decks.Count == 0) return _deckScratch;
        _deckQuery++;
        int x0 = CellCoord(x - reach), x1 = CellCoord(x + reach), z0 = CellCoord(z - reach), z1 = CellCoord(z + reach);
        for (int cx = x0; cx <= x1; cx++) for (int cz = z0; cz <= z1; cz++)
            if (_deckGrid.TryGetValue(PackCell(cx, cz), out var l))
                for (int j = 0; j < l.Count; j++) { int idx = l[j]; if (idx >= _deckStamp.Length) { _deckScratch.Add(idx); continue; } if (_deckStamp[idx] == _deckQuery) continue; _deckStamp[idx] = _deckQuery; _deckScratch.Add(idx); }
        return _deckScratch;
    }
    private void RebuildEnemyGrid()
    {
        foreach (var kv in _enemyGrid) kv.Value.Clear();
        for (int i = 0; i < Enemies.Count; i++)
        {
            var e = Enemies[i];
            if (e == null || !GodotObject.IsInstanceValid(e)) continue;
            long k = PackCell(CellCoord(e.GlobalPosition.X), CellCoord(e.GlobalPosition.Z));
            if (!_enemyGrid.TryGetValue(k, out var l)) { l = new List<Enemy>(8); _enemyGrid[k] = l; }
            l.Add(e);
        }
    }
    // Shared buffer of enemies near (x,z). Grid rebuilds once per frame. Valid until the NEXT QueryEnemies call.
    public List<Enemy> QueryEnemies(float x, float z, float reach)
    {
        ulong f = Engine.GetProcessFrames();
        if (_enemyGridFrame != f) { _enemyGridFrame = f; RebuildEnemyGrid(); }
        _enemyScratch.Clear();
        int x0 = CellCoord(x - reach), x1 = CellCoord(x + reach), z0 = CellCoord(z - reach), z1 = CellCoord(z + reach);
        for (int cx = x0; cx <= x1; cx++) for (int cz = z0; cz <= z1; cz++)
            if (_enemyGrid.TryGetValue(PackCell(cx, cz), out var l))
                for (int j = 0; j < l.Count; j++) _enemyScratch.Add(l[j]);
        return _enemyScratch;
    }

    // is a world position inside a solid blocker (tree / cover pillar)? Used so a Taker charge stuns on trees, not just walls.
    public bool BlockerAt(Vector3 pos, float extra = 0f)
    {
        var near = QueryBlockers(pos.X, pos.Z, extra + 3f);
        for (int j = 0; j < near.Count; j++)
        {
            var b = Blockers[near[j]];
            float dx = pos.X - b.Pos.X, dz = pos.Z - b.Pos.Z, rr = b.Radius + extra;
            if (dx * dx + dz * dz < rr * rr) return true;
        }
        foreach (var b in WallBlockers)   // (NEW) frost walls count as solid too (charging foes stop/stun on them) — small list, stays linear
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
                Player.S.DmgResist = 0.20f;     // (SPREAD) sturdy grovekeeper
                Player.S.MaxHp = 135f;          // (SPREAD) highest HP in the roster — a walking fortress
                Player.S.Speed = 8.3f;          // (SPREAD) slow — she plants and the army fights
                break;
            case 5:    // The Frost Witch (NEW) — long-range sniper: freezing beam + charged icicle spear + shatter burst
                Player.PrimaryType = DamageType.Frost;
                Player.SecondaryType = DamageType.Frost;
                Player.NightAffinity = false;
                Player.FrostWitch = true;
                Player.DamageMul = 0.95f;       // steady personal DPS — her burst comes from freezing + shattering
                Player.S.DmgResist = 0.14f;     // (SPREAD) a touch more durable; her real defense is Frost Armor (retaliatory chill)
                Player.S.MaxHp = 105f;          // (SPREAD)
                Player.S.Speed = 8.0f;          // (SPREAD) slowest in the roster — the immovable siege sniper
                break;
            case 6:    // The Forsaken Witch (Curse) (NEW) — lock-on curse-suck beam that tethers foes into shared-damage groups
                Player.PrimaryType = DamageType.Curse;
                Player.SecondaryType = DamageType.Curse;
                Player.NightAffinity = false;
                Player.ForsakenWitch = true;
                Player.DamageMul = 0.88f;       // (SPREAD) lifted a touch — her power is still the curse groups + shared damage
                Player.S.DmgResist = 0.15f;     // a controller, fragile but not paper; sustain comes from Soul Siphon
                Player.S.MaxHp = 105f;          // (SPREAD)
                Player.S.Speed = 8.8f;          // (SPREAD) slow-ish, deliberate
                break;
            case 7:    // The Ember Witch (Ember) (NEW) — flamethrower cone + aimed meteor; stacks burn → Living Bomb detonations
                Player.PrimaryType = DamageType.Ember;
                Player.SecondaryType = DamageType.Ember;
                Player.NightAffinity = false;
                Player.EmberWitch = true;
                Player.DamageMul = 0.92f;       // (SPREAD) her damage is the burn DoT + Living Bomb explosions
                Player.S.DmgResist = 0.12f;     // fragile pyro — survival is Cinder Skin (burn-lifesteal + retaliatory heat)
                Player.S.MaxHp = 100f;          // (SPREAD)
                Player.S.Speed = 9.2f;          // (SPREAD) a shade quick — a kiting arsonist
                Player.EmberBurnMul = 1.2f;     // (CINDER SKIN) a real INNATE burn multiplier — her damage no longer relies purely on cards
                break;
            case 4:    // The Gale Witch (Wind) (NEW)
                Player.PrimaryType = DamageType.Wind;
                Player.SecondaryType = DamageType.Wind;
                Player.NightAffinity = false;
                Player.GaleWitch = true;
                Player.DamageMul = 0.98f;       // she felt weak — lift her whole kit, plus a harder punch + airborne-kill bonus below
                Player.S.DmgResist = 0.12f;     // lightly armored — she survives by evasion (Tailwind), not toughness
                Player.S.MaxHp = 95f;           // (SPREAD) glassy — offset by best-in-class mobility
                Player.S.Speed = 10.1f;         // (SPREAD) fastest on foot — Tailwind
                if (Player.S.DashCharges < 3) Player.S.DashCharges++;        // Tailwind: an extra dash charge
                Player.DashStock = Player.S.DashCharges;
                break;
            case 2:    // The Crimson Blood Witch
                Player.PrimaryType = DamageType.Blood;
                Player.SecondaryType = DamageType.Blood;
                Player.NightAffinity = false;
                Player.CrimsonWitch = true;
                Player.DamageMul = 1.18f;       // (SPREAD) highest damage in the roster — glass cannon, sustained by lifesteal + blood stacks
                Player.S.DmgResist = 0.08f;     // lowest base resistance (glass cannon)
                Player.S.MaxHp = 95f;           // (SPREAD) fragile — she lives by killing fast (Sanguine Thirst)
                Player.S.Speed = 9.6f;          // (SPREAD) fast — she has to close the gap
                Player.S.Lifesteal = 0.05f;     // (SANGUINE THIRST) a real base lifesteal — makes the "lifesteal aura" true, not a false blurb
                break;
            case 8:    // The Arcane Witch (NEW) — 3-round homing missile burst + a chargeable sustained beam; the beam arcane-marks foes for auto-lock + bonus arcane damage
                Player.PrimaryType = DamageType.Arcane;
                Player.SecondaryType = DamageType.Arcane;
                Player.NightAffinity = false;
                Player.ArcaneWitch = true;
                Player.DamageMul = 0.95f;       // steady precision caster — payoff is marking + the sustained beam
                Player.S.DmgResist = 0.14f;     // lightly armored — a ranged controller
                Player.S.MaxHp = 105f;          // (SPREAD)
                Player.S.Speed = 9.0f;          // (SPREAD) baseline footing
                Player.ArcaneCritHealBonus = 0.05f;  // (SPREAD/PASSIVE) crit-heal up 25→30% base — her innate sustain
                break;
            case 1:    // The Divine Witch
                Player.PrimaryType = DamageType.Holy;
                Player.SecondaryType = DamageType.Holy;
                Player.NightAffinity = false;
                Player.DivineWitch = true;
                Player.DamageMul = 0.90f;       // (SPREAD) floor lifted — with Radiant Smite scaling she has real threat, not just support
                Player.Interventions = 1;       // first Divine Intervention ready
                Player.S.DmgResist = 0.13f;     // (SPREAD) moderate — her survival is interventions/heals/shields, not raw resist
                Player.S.MaxHp = 120f;          // (SPREAD) a durable anchor
                Player.S.ManaMax = 3f;          // (SPREAD) more mana for her ray/heals
                break;
            default:   // 0 = The Lunar Witch
                Player.PrimaryType = DamageType.Lunar;
                Player.SecondaryType = DamageType.Lunar;
                Player.NightAffinity = true;   // waxes stronger at night (Moonlight Marks work day+night; night amplifies)
                Player.DamageMul = 1f;
                Player.S.DmgResist = 0.24f;    // (SPREAD) highest base resistance — the moon-tank
                Player.S.MaxHp = 125f;         // (SPREAD) second only to Verdant — a wall
                Player.S.Speed = 8.4f;         // (SPREAD) stately/slow; she doesn't kite, she out-lasts
                Player.LunarBonus = 0.15f;     // (MOONLIGHT) innate +15% Lunar damage — works DAY and night (doubled at night). Fixes the day-dead Nightfall passive.
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
        Player.DivineWitch = Player.CrimsonWitch = Player.VerdantWitch = Player.GaleWitch = Player.FrostWitch = Player.ForsakenWitch = Player.EmberWitch = Player.ArcaneWitch = false;
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
        // Gamepad (Xbox) is layered onto the SAME actions as keyboard/mouse, so both stay live at once (see UpdateGamepad).
        // Left stick → move, right stick → look (polled in Player), triggers → fire, face buttons → the actions below.
        Action("move_forward", new InputEventKey { PhysicalKeycode = Key.W }, new InputEventKey { PhysicalKeycode = Key.Up },   new InputEventJoypadMotion { Axis = JoyAxis.LeftY, AxisValue = -1f });
        Action("move_back",    new InputEventKey { PhysicalKeycode = Key.S }, new InputEventKey { PhysicalKeycode = Key.Down }, new InputEventJoypadMotion { Axis = JoyAxis.LeftY, AxisValue = 1f });
        Action("move_left",    new InputEventKey { PhysicalKeycode = Key.A }, new InputEventKey { PhysicalKeycode = Key.Left }, new InputEventJoypadMotion { Axis = JoyAxis.LeftX, AxisValue = -1f });
        Action("move_right",   new InputEventKey { PhysicalKeycode = Key.D }, new InputEventKey { PhysicalKeycode = Key.Right },new InputEventJoypadMotion { Axis = JoyAxis.LeftX, AxisValue = 1f });
        Action("cast",   new InputEventMouseButton { ButtonIndex = MouseButton.Left },  new InputEventJoypadMotion { Axis = JoyAxis.TriggerLeft, AxisValue = 1f });    // LT = primary fire
        Action("charge", new InputEventMouseButton { ButtonIndex = MouseButton.Right }, new InputEventJoypadMotion { Axis = JoyAxis.TriggerRight, AxisValue = 1f });   // RT = secondary/charge
        Action("dash",   new InputEventKey { PhysicalKeycode = Key.Shift }, new InputEventJoypadButton { ButtonIndex = JoyButton.B });   // B = dash (ground)
        Action("jump",   new InputEventKey { PhysicalKeycode = Key.Space }, new InputEventJoypadButton { ButtonIndex = JoyButton.A });   // A = jump / float / fly up
        Action("descend", new InputEventKey { PhysicalKeycode = Key.Ctrl }, new InputEventJoypadButton { ButtonIndex = JoyButton.B });  // flight ults: B = fly down (dash is locked out mid-flight, so no clash)
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
        Action("stats",  new InputEventKey { PhysicalKeycode = Key.Tab }, new InputEventJoypadButton { ButtonIndex = JoyButton.Back });   // Back/Select = stats
        Action("ult",    new InputEventKey { PhysicalKeycode = Key.Q }, new InputEventJoypadButton { ButtonIndex = JoyButton.Y });        // Y = ult
        Action("ultmenu", new InputEventKey { PhysicalKeycode = Key.U }, new InputEventJoypadButton { ButtonIndex = JoyButton.LeftStick });   // L3 = open the ult-upgrade menu
        Action("restart", new InputEventKey { PhysicalKeycode = Key.Enter });
        Action("changewitch", new InputEventKey { PhysicalKeycode = Key.C });
        Action("release_mouse", new InputEventKey { PhysicalKeycode = Key.Escape }, new InputEventJoypadButton { ButtonIndex = JoyButton.Start });   // Start = pause/close
        // triggers need a firmer pull than the default 0.2 so a light touch doesn't fire; sticks keep the gentler default
        InputMap.ActionSetDeadzone("cast", 0.5f);
        InputMap.ActionSetDeadzone("charge", 0.5f);
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
        if (!SimActive) return;
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
    // (PERF) impact marks fire on every bolt/pierce/ground hit — previously each allocated a fresh StandardMaterial3D +
    // QuadMesh + Tween, the biggest transient-material churn in combat. Now the material (per shape+tint) and the quad
    // (per size bucket) are cached & shared, and the fade is driven by the MeshInstance's Transparency (0→1) instead of
    // mutating the material, so every mark of a given element/size batches into one draw call.
    private readonly System.Collections.Generic.Dictionary<int, StandardMaterial3D> _markMats = new();
    private readonly System.Collections.Generic.Dictionary<int, QuadMesh> _markMeshes = new();
    private StandardMaterial3D MarkMat(int shape, DamageType dt, Color tint)
    {
        int key = shape * 100 + (int)dt;
        if (_markMats.TryGetValue(key, out var m)) return m;
        var tex = MaskTex(shape);
        m = new StandardMaterial3D
        {
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            AlbedoTexture = tex, AlbedoColor = tint,
            EmissionEnabled = true, EmissionTexture = tex, Emission = tint, EmissionEnergyMultiplier = 3.5f
        };
        _markMats[key] = m;
        return m;
    }
    private QuadMesh MarkMesh(float sz)
    {
        int key = Mathf.Clamp((int)Mathf.Round(sz * 4f), 2, 16);   // 0.25u buckets → a dozen shared quads cover the whole size range
        if (_markMeshes.TryGetValue(key, out var q)) return q;
        float s = key / 4f;
        q = new QuadMesh { Size = new Vector2(s, s) };
        _markMeshes[key] = q;
        return q;
    }

    public void SpawnImpactMark(Vector3 hitPos, Vector3 normal, Node3D attachTo, DamageType dt, float projRadius, float roll = float.NaN)
    {
        if (!SimActive) return;
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
        var mi = new MeshInstance3D { Mesh = MarkMesh(sz), MaterialOverride = MarkMat(shape, dt, tint), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        (attachTo ?? this).AddChild(mi);
        mi.GlobalPosition = hitPos + n * 0.03f;   // lift slightly off the surface (anti z-fight)
        var up = Mathf.Abs(n.Y) > 0.9f ? Vector3.Forward : Vector3.Up;
        mi.LookAt(mi.GlobalPosition - n, up);      // quad face aligns to the surface normal (works on walls, enemies, ground)
        mi.RotateObjectLocal(Vector3.Back, float.IsNaN(roll) ? (float)GD.RandRange(0.0, Mathf.Tau) : roll);   // in-plane orientation (matched to the cast VFX when a roll is passed)
        _impactMarks.Add(mi);

        // fade on the NODE (shared material stays untouched, so marks batch): Transparency 0→1 dissolves the whole quad, glow and all
        var tw = mi.CreateTween();
        tw.TweenInterval(0.35f);
        tw.TweenProperty(mi, "transparency", 1f, 0.6f);
        tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(mi)) mi.QueueFree(); }));

        SpawnImpactBurst(hitPos, n, tint, sz);   // (PHASE 3) punchy 3D burst on top of the flat mark
    }

    private int _impactBursts = 0;
    // (PHASE 3) a punchy 3D impact BURST layered on the flat mark: a quick FLASH + an expanding RING shockwave (authored
    // silhouette, lies in the surface plane) + a few radial SPARK shards, each phased grow→fade so hits land with weight.
    // Concurrent-capped + quality-gated so a dense swarm can't flood it.
    public void SpawnImpactBurst(Vector3 pos, Vector3 normal, Color tint, float size)
    {
        if (!SimActive) return;
        int cap = GfxQuality == 0 ? 4 : GfxQuality == 1 ? 9 : 14;
        if (_impactBursts >= cap) return;
        var n = normal.LengthSquared() > 1e-6f ? normal.Normalized() : Vector3.Up;
        Vector3 at = pos + n * 0.05f;
        var up = Mathf.Abs(n.Y) > 0.9f ? Vector3.Forward : Vector3.Up;

        // FLASH — a bright core that pops then vanishes fast (impact moment)
        var flash = new MeshInstance3D { Mesh = new SphereMesh { Radius = size * 0.32f, Height = size * 0.64f }, MaterialOverride = Emissive(tint.Lerp(Colors.White, 0.55f), 3f), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(flash); flash.GlobalPosition = at;
        var ft = flash.CreateTween();
        ft.TweenProperty(flash, "scale", Vector3.One * 1.7f, 0.12f).SetEase(Tween.EaseType.Out);
        ft.Parallel().TweenProperty(flash, "transparency", 1f, 0.14f);
        ft.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(flash)) flash.QueueFree(); }));

        // RING — an expanding shockwave lying flat in the surface plane (dissipation)
        _impactBursts++;
        var ring = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = size * 0.5f, OuterRadius = size * 0.6f }, MaterialOverride = Emissive(tint, 2.2f), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(ring); ring.GlobalPosition = at; ring.LookAt(at - n, up); ring.Scale = Vector3.One * 0.3f;
        var rt = ring.CreateTween();
        rt.TweenProperty(ring, "scale", Vector3.One * 1.9f, 0.32f).SetEase(Tween.EaseType.Out);
        rt.Parallel().TweenProperty(ring, "transparency", 1f, 0.34f);
        rt.TweenCallback(Callable.From(() => { _impactBursts--; if (GodotObject.IsInstanceValid(ring)) ring.QueueFree(); }));

        // SPARKS — pooled GPU shard-cone burst (Fx.SparkBurst): same cone silhouette as the old mesh version, aligned to
        // velocity + damped ease-out, but pooled for cheap bulk. Quality-gated.
        if (GfxQuality >= 1)
            Fx.SparkBurst(at, n, tint, size, GfxQuality >= 2 ? 6 : 4);
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

    // Arcane witch signature: raw plasma energy — flowing white-hot veins over violet, unstable crackle, additive glow.
    // Modeled on the proven ElementBoltCode structure (same noise fns) so it's safe; used on her lightning / torrent / orbs.
    private static Shader _arcaneEnergyShader;
    private static readonly System.Collections.Generic.Dictionary<uint, ShaderMaterial> _energyCache = new();
    public static ShaderMaterial ElementEnergyMat(Color col)   // flowing raw-energy plasma in ANY element colour (cached per colour) — used by every ult's flourish
    {
        _arcaneEnergyShader ??= new Shader { Code = ArcaneEnergyCode };
        uint k = col.ToRgba32();
        if (_energyCache.TryGetValue(k, out var m)) return m;
        m = new ShaderMaterial { Shader = _arcaneEnergyShader };
        m.SetShaderParameter("col", V3lin(col));
        _energyCache[k] = m;
        return m;
    }
    public static ShaderMaterial ArcaneEnergyMat() => ElementEnergyMat(DamageTypes.Col(DamageType.Arcane));
    private const string ArcaneEnergyCode = @"
shader_type spatial;
render_mode blend_add, unshaded, cull_disabled, depth_draw_never;
uniform vec3 col = vec3(0.5, 0.2, 1.0);
varying vec3 v_obj;
float h13(vec3 p){ p = fract(p * 0.1031); p += dot(p, p.zyx + 31.32); return fract((p.x + p.y) * p.z); }
float vn(vec3 p){ vec3 i = floor(p); vec3 f = fract(p); f = f * f * (3.0 - 2.0 * f);
  float n000 = h13(i); float n100 = h13(i + vec3(1.0,0.0,0.0)); float n010 = h13(i + vec3(0.0,1.0,0.0)); float n110 = h13(i + vec3(1.0,1.0,0.0));
  float n001 = h13(i + vec3(0.0,0.0,1.0)); float n101 = h13(i + vec3(1.0,0.0,1.0)); float n011 = h13(i + vec3(0.0,1.0,1.0)); float n111 = h13(i + vec3(1.0,1.0,1.0));
  float x00 = mix(n000,n100,f.x); float x10 = mix(n010,n110,f.x); float x01 = mix(n001,n101,f.x); float x11 = mix(n011,n111,f.x);
  return mix(mix(x00,x10,f.y), mix(x01,x11,f.y), f.z); }
float fbm(vec3 p){ float v = 0.0; float a = 0.5; for (int i = 0; i < 4; i++){ v += a * vn(p); p *= 2.03; a *= 0.5; } return v; }
void vertex(){ v_obj = VERTEX; }
void fragment(){
  float t = TIME;
  float flow = fbm(v_obj * 3.0 + vec3(0.0, t * 3.0, 0.0));                 // energy flowing along the mesh
  float veins = smoothstep(0.42, 0.72, flow);                             // bright white-hot filaments
  float flick = 0.65 + 0.45 * sin(t * 34.0 + flow * 22.0) + 0.3 * h13(floor(v_obj * 26.0) + floor(vec3(t * 22.0)));   // unstable crackle
  vec3 hot = mix(col, vec3(1.0), veins * 0.9);
  float rim = pow(1.0 - clamp(dot(normalize(NORMAL), normalize(VIEW)), 0.0, 1.0), 1.5);
  vec3 e = (hot * (1.1 + veins * 2.2) * flick + col * rim * 1.6) * 1.15;
  ALBEDO = e;
  EMISSION = e;   // written to both so it glows whether or not unshaded honors EMISSION under blend_add
}
";

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
        float gy = center.Y;   // (FIX) originate the mist at the passed height (chest/hands/airborne caster) — was snapped to the ground surface, so mist always puffed at your feet even mid-air. Droplets still LAND on the real surface below.
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

    // Arcane: the sustained beam segment (ally-visible ghost — kind 78)
    public void SpawnArcaneBeamSeg(Vector3 from, Vector3 dir, float len)
    {
        var seg = new Node3D(); AddChild(seg);
        var col = DamageTypes.Col(DamageType.Arcane);
        var glow = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.26f, BottomRadius = 0.26f, Height = 1f, RadialSegments = 8 } };
        var gm = ToonEmissive(col, 1.6f, 0f); gm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; gm.AlbedoColor = new Color(col.R, col.G, col.B, 0.3f);
        glow.MaterialOverride = gm; glow.RotationDegrees = new Vector3(90, 0, 0); glow.Scale = new Vector3(1f, len, 1f); seg.AddChild(glow);
        var core = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.11f, BottomRadius = 0.11f, Height = 1f, RadialSegments = 6 }, MaterialOverride = Emissive(col.Lerp(Colors.White, 0.4f), 3.4f) };
        core.RotationDegrees = new Vector3(90, 0, 0); core.Scale = new Vector3(1f, len, 1f); seg.AddChild(core);
        seg.GlobalPosition = from + dir * (len / 2f);
        seg.LookAt(seg.GlobalPosition + dir, Vector3.Up);
        GetTree().CreateTimer(0.16f).Timeout += () => { if (GodotObject.IsInstanceValid(seg)) seg.QueueFree(); };
    }

    // Arcane: a raw-arcane rupture — an imploding flash + expanding ring + chaotic arcs spitting outward (kind 79)
    public void SpawnArcaneRupture(Vector3 pos, float radius)
    {
        var col = DamageTypes.Col(DamageType.Arcane);
        VfxRing(pos, col, radius, 0.4f);
        var flash = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.5f, Height = 1.0f }, MaterialOverride = Emissive(col.Lerp(Colors.White, 0.6f), 5f), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(flash); flash.GlobalPosition = pos; flash.Scale = Vector3.One * 0.3f;
        var tw = flash.CreateTween();
        tw.TweenProperty(flash, "scale", Vector3.One * Mathf.Max(1f, radius * 0.8f), 0.22).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(flash)) flash.QueueFree(); }));
        for (int i = 0; i < 6; i++)   // untamed arcs spitting out in all directions
        {
            float a = i / 6f * Mathf.Tau + GD.Randf();
            var end = pos + new Vector3(Mathf.Cos(a), 0.15f, Mathf.Sin(a)) * radius * (0.55f + GD.Randf() * 0.5f);
            var d = end - pos; float l = d.Length(); if (l > 0.1f) SpawnArcaneBeamSeg(pos, d / l, l);
        }
        Sfx?.Cast(DamageType.Arcane);
    }

    // Arcane: jagged chain-lightning through a path of points (her → mark → mark …). Spawns a self-animating node that reveals
    // the chain jump-by-jump and writhes each frame, so it visibly TRAVELS + bounces instead of flashing all at once.
    public void SpawnArcaneLightning(System.Collections.Generic.List<Vector3> pts, float charge)
    {
        if (pts == null || pts.Count < 2) return;
        var n = new ArcaneLightning(); AddChild(n); n.Init(pts, charge);
    }

    // Universal ULT-ACTIVATION flourish — every witch's ult erupts this (element-coloured) so it FEELS like an ult: a ground
    // rune, two expanding shockwave rings, a bright rising energy column (the element-energy shader), a crown of rising shards,
    // and a bloom of light that all punch out then dissipate. Local juice on top of each ult's own effect.
    public void UltCast(Vector3 pos, Color col)
    {
        var p = new Vector3(pos.X, SurfaceHeight(pos, pos.Y), pos.Z);
        SpawnGroundSigil(p, 6.5f, col, false);
        VfxRing(p + Vector3.Up * 0.2f, col, 10f, 0.6f);
        VfxRing(p + Vector3.Up * 0.2f, col.Lerp(Colors.White, 0.5f), 5.5f, 0.5f);
        var root = new Node3D(); AddChild(root); root.GlobalPosition = p;
        var pillar = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.35f, BottomRadius = 1.3f, Height = 8f, RadialSegments = 12 }, MaterialOverride = ElementEnergyMat(col), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        pillar.Position = new Vector3(0, 4f, 0); root.AddChild(pillar);
        for (int i = 0; i < 8; i++)   // a crown of rising energy shards
        {
            float a = i / 8f * Mathf.Tau;
            var sh = new MeshInstance3D { Mesh = new PrismMesh { Size = new Vector3(0.12f, 0.55f, 0.12f) }, MaterialOverride = ElementEnergyMat(col), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            sh.Position = new Vector3(Mathf.Cos(a) * 2f, 0.5f, Mathf.Sin(a) * 2f); sh.RotationDegrees = new Vector3(0, Mathf.RadToDeg(a), 8); root.AddChild(sh);
        }
        var light = new OmniLight3D { OmniRange = 15f, LightColor = col, LightEnergy = 4.5f, ShadowEnabled = false }; root.AddChild(light); light.Position = new Vector3(0, 2f, 0);
        root.Scale = new Vector3(1f, 0.08f, 1f);
        var tw = root.CreateTween();
        tw.TweenProperty(root, "scale", new Vector3(1.15f, 1f, 1.15f), 0.16).SetTrans(Tween.TransitionType.Expo).SetEase(Tween.EaseType.Out);   // erupt up
        tw.TweenInterval(0.18);
        tw.TweenProperty(root, "scale", new Vector3(0.2f, 1.35f, 0.2f), 0.4).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);   // dissipate skyward
        tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(root)) root.QueueFree(); }));
        light.CreateTween().TweenProperty(light, "light_energy", 0f, 0.55);
    }

    // Glowing motes raining DOWN from the sky to the ground across a radius — moonlight / holy descent / etc.
    public void FallingMotes(Vector3 center, float radius, Color col, int count, float height = 9f)
    {
        for (int i = 0; i < count; i++)
        {
            float a = GD.Randf() * Mathf.Tau, r = Mathf.Sqrt(GD.Randf()) * radius;
            var start = center + new Vector3(Mathf.Cos(a) * r, height + GD.Randf() * 3f, Mathf.Sin(a) * r);
            var mote = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.07f + GD.Randf() * 0.06f, Height = 0.16f }, MaterialOverride = Emissive(col.Lerp(Colors.White, 0.4f), 3f), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            AddChild(mote); mote.GlobalPosition = start;
            var tw = mote.CreateTween();
            tw.TweenProperty(mote, "global_position", new Vector3(start.X, center.Y + 0.2f, start.Z), 0.9 + GD.Randf() * 0.6).SetEase(Tween.EaseType.In);
            tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(mote)) mote.QueueFree(); }));
        }
    }

    // Wind: leaves & grit caught in a swirl — flung UP-and-out (inward=false, e.g. a hurricane launch) or dragged
    // IN-and-up toward a funnel's throat (inward=true, e.g. a cyclone touchdown). Debris spirals as it travels.
    public void SwirlDebris(Vector3 center, float radius, Color col, int count, bool inward, float height = 7f)
    {
        var leafCol = col.Lerp(new Color(0.45f, 0.38f, 0.22f), 0.55f);   // wind-tinted dust/leaf
        for (int i = 0; i < count; i++)
        {
            float a = GD.Randf() * Mathf.Tau;
            float rStart = inward ? radius * (0.7f + GD.Randf() * 0.4f) : radius * (0.1f + GD.Randf() * 0.3f);
            float rEnd = inward ? radius * (0.05f + GD.Randf() * 0.15f) : radius * (0.6f + GD.Randf() * 0.5f);
            var start = center + new Vector3(Mathf.Cos(a) * rStart, 0.2f + GD.Randf() * 0.5f, Mathf.Sin(a) * rStart);
            float aEnd = a + (inward ? 2.2f : 1.4f) * (GD.Randf() < 0.5f ? 1f : -1f);   // spiral sweep
            var end = center + new Vector3(Mathf.Cos(aEnd) * rEnd, height * (0.5f + GD.Randf() * 0.8f), Mathf.Sin(aEnd) * rEnd);
            var bit = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.22f + GD.Randf() * 0.2f, 0.05f, 0.14f + GD.Randf() * 0.12f) }, MaterialOverride = ToonEmissive(leafCol, 0.5f, 0.06f), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            AddChild(bit); bit.GlobalPosition = start; bit.RotationDegrees = new Vector3(GD.Randf() * 360f, GD.Randf() * 360f, GD.Randf() * 360f);
            var tw = bit.CreateTween(); tw.SetParallel(true);
            tw.TweenProperty(bit, "global_position", end, 0.5 + GD.Randf() * 0.4).SetEase(Tween.EaseType.Out);
            tw.TweenProperty(bit, "rotation_degrees", bit.RotationDegrees + new Vector3(720f, 540f, 360f), 0.7);
            tw.TweenProperty(bit, "transparency", 1f, 0.8 + GD.Randf() * 0.3);
            tw.SetParallel(false);
            tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(bit)) bit.QueueFree(); }));
        }
    }

    // Curse/soul: faint wisps that well up FROM the ground and drift upward, dissipating — the inverse of FallingMotes.
    // Reads as souls/dread being pulled loose (hex circles, life drains).
    public void RisingWisps(Vector3 center, float radius, Color col, int count, float height = 6f)
    {
        for (int i = 0; i < count; i++)
        {
            float a = GD.Randf() * Mathf.Tau, r = Mathf.Sqrt(GD.Randf()) * radius;
            var start = center + new Vector3(Mathf.Cos(a) * r, 0.1f + GD.Randf() * 0.4f, Mathf.Sin(a) * r);
            var wisp = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.09f + GD.Randf() * 0.08f, Height = 0.2f }, MaterialOverride = Emissive(col.Lerp(Colors.White, 0.25f), 2.4f), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            AddChild(wisp); wisp.GlobalPosition = start;
            float drift = 0.6f + GD.Randf() * 1.2f;
            var end = new Vector3(start.X + Mathf.Cos(a) * drift, center.Y + height * (0.5f + GD.Randf() * 0.9f), start.Z + Mathf.Sin(a) * drift);
            var tw = wisp.CreateTween(); tw.SetParallel(true);
            tw.TweenProperty(wisp, "global_position", end, 0.9 + GD.Randf() * 0.7).SetEase(Tween.EaseType.Out);
            tw.TweenProperty(wisp, "transparency", 1f, 1.0 + GD.Randf() * 0.5);
            tw.SetParallel(false);
            tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(wisp)) wisp.QueueFree(); }));
        }
    }

    // Arcane: a fat "torrent" beam-burst (Arcane Torrent finisher) — a wide layered cylinder that flares then fades.
    public void SpawnArcaneKamehameha(Vector3 from, Vector3 dir, float len, float width, Color col)
    {
        var seg = new Node3D(); AddChild(seg);
        seg.GlobalPosition = from + dir * (len / 2f);
        seg.LookAt(seg.GlobalPosition + dir, Mathf.Abs(dir.Y) > 0.98f ? Vector3.Forward : Vector3.Up);
        void Layer(float r, Color c, float en, float a)
        {
            var mi = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = r, BottomRadius = r, Height = 1f, RadialSegments = 10 }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            var m = ToonEmissive(c, en, 0f); m.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; m.AlbedoColor = new Color(c.R, c.G, c.B, a);
            mi.MaterialOverride = m; mi.RotationDegrees = new Vector3(90, 0, 0); mi.Scale = new Vector3(1f, len, 1f); seg.AddChild(mi);
        }
        Layer(width, col, 1.5f, 0.18f);
        Layer(width * 0.6f, col, 2.6f, 0.45f);
        var coreMi = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = width * 0.32f, BottomRadius = width * 0.32f, Height = 1f, RadialSegments = 12 }, MaterialOverride = ArcaneEnergyMat(), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        coreMi.RotationDegrees = new Vector3(90, 0, 0); coreMi.Scale = new Vector3(1f, len, 1f); seg.AddChild(coreMi);   // shader-driven flowing plasma core
        // hold the torrent long enough to read, then collapse it away (was a 0.28s flash you could barely see)
        seg.Scale = new Vector3(0.5f, 1f, 0.5f);
        var tw = seg.CreateTween();
        tw.TweenProperty(seg, "scale", Vector3.One, 0.14).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);   // punch out to full width
        tw.TweenInterval(0.5);
        tw.TweenProperty(seg, "scale", Vector3.One * 0.12f, 0.22).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);   // collapse away
        tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(seg)) seg.QueueFree(); }));
    }

    // Arcane: a tiny thin jagged spark (~0.08s) between two points — the crackle arcing off her charge orb to her hands/air.
    public void SpawnArcaneSpark(Vector3 a, Vector3 b)
    {
        float len = (b - a).Length(); if (len < 0.02f || len > 6f) return;
        var col = DamageTypes.Col(DamageType.Arcane).Lerp(Colors.White, 0.35f);
        var dir = (b - a) / len;
        var perp = dir.Cross(Vector3.Up); if (perp.LengthSquared() < 1e-4f) perp = Vector3.Right; perp = perp.Normalized();
        var perp2 = dir.Cross(perp).Normalized();
        var root = new Node3D(); AddChild(root);
        int n = Mathf.Clamp(Mathf.RoundToInt(len / 0.25f), 2, 6);
        Vector3 prev = a;
        for (int s = 1; s <= n; s++)
        {
            Vector3 p = s == n ? b : a + dir * (len * s / n) + perp * ((GD.Randf() - 0.5f) * 0.12f) + perp2 * ((GD.Randf() - 0.5f) * 0.12f);
            var d = p - prev; float l = d.Length();
            if (l > 0.005f)
            {
                var node = new Node3D(); root.AddChild(node);
                node.GlobalPosition = (prev + p) * 0.5f;
                node.LookAt(node.GlobalPosition + d / l, Mathf.Abs((d / l).Y) > 0.98f ? Vector3.Forward : Vector3.Up);
                var mi = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.012f, BottomRadius = 0.012f, Height = 1f, RadialSegments = 4 }, MaterialOverride = Emissive(col, 2.4f), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
                mi.RotationDegrees = new Vector3(90, 0, 0); mi.Scale = new Vector3(1f, l, 1f); node.AddChild(mi);
            }
            prev = p;
        }
        var tw = root.CreateTween(); tw.TweenInterval(0.08); tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(root)) root.QueueFree(); }));
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
    // (NEW) cached frosted-ice shader for Frost Wall crystals: cold translucent body with a bright fresnel rim
    // (edges of the ice catch the light) and a faint internal glow. Single shared instance — all walls look the same.
    private ShaderMaterial _iceWallMat;
    private const string IceWallCode = @"
shader_type spatial;
render_mode cull_disabled, diffuse_burley;
uniform vec4 tint : source_color = vec4(0.60, 0.85, 1.0, 0.52);
void fragment() {
    float fres = pow(1.0 - clamp(dot(normalize(NORMAL), normalize(VIEW)), 0.0, 1.0), 3.0);
    ALBEDO = tint.rgb;
    ALPHA = clamp(tint.a + fres * 0.45, 0.0, 1.0);
    EMISSION = tint.rgb * (0.22 + fres * 0.75);
    ROUGHNESS = 0.12;
    METALLIC = 0.0;
}";
    public ShaderMaterial IceWallMat()
    {
        if (_iceWallMat == null)
        {
            var sh = new Shader { Code = IceWallCode };
            _iceWallMat = new ShaderMaterial { Shader = sh };
        }
        return _iceWallMat;
    }

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
    // ---- Moonfall mutator: rain varied moon-fragment asteroids over the field ----
    private float _moonfallT = 0f;
    private void MoonfallTick(float dt)
    {
        if (!IsAuthority || !WorldRunning) return;
        _moonfallT -= dt;
        if (_moonfallT > 0f) return;
        _moonfallT = (float)GD.RandRange(0.5, 1.1);   // cadence of impacts
        // anchor each impact on a RANDOM player (host + allies), then a modest offset — so shards rain around whoever's
        // chosen, threatening the whole party over the wave rather than only the host.
        var anchors = new List<Vector3>();
        if (Player != null && GodotObject.IsInstanceValid(Player) && !Player.Downed) anchors.Add(Player.GlobalPosition);
        if (NetMgr != null && NetMgr.Active) anchors.AddRange(NetMgr.AllyPositions());
        Vector3 pc = anchors.Count > 0 ? anchors[_rng.RandiRange(0, anchors.Count - 1)] : (Player != null ? Player.GlobalPosition : Vector3.Zero);
        float a = (float)GD.RandRange(0.0, Mathf.Tau), d = (float)GD.RandRange(0.0, 18.0);   // near the chosen player (dodgeable)
        var pos = new Vector3(pc.X + Mathf.Cos(a) * d, 0f, pc.Z + Mathf.Sin(a) * d);
        float size = (float)GD.RandRange(0.6, 1.7);   // small → large fragments
        SpawnMoonshard(pos, size, remote: false, net: true);
    }
    public void SpawnMoonshard(Vector3 pos, float size, bool remote, bool net = true)
    {
        var m = new Moonshard { Remote = remote }; AddChild(m); m.Init(pos, size);
        if (net) NetMgr?.BroadcastVfx(62, pos, Vector3.Zero, size, 0f, Colors.White);
    }

    // (NEW) Ember flamethrower flame — a short-lived scatter of glowing flame motes in the cone. Local; UpdateFlameCone broadcasts (kind 66).
    public void SpawnFlameCone(Vector3 o, Vector3 dir, float reach, Color col)
    {
        dir = dir.LengthSquared() > 0.001f ? dir.Normalized() : Vector3.Forward;   // (FIX) full 3D aim — flame follows the cursor up/down, no longer flattened
        var right = dir.Cross(Vector3.Up);
        right = right.LengthSquared() < 0.001f ? Vector3.Right : right.Normalized();   // dir nearly vertical → any perpendicular
        var up = right.Cross(dir).Normalized();
        int n = Mathf.Max(3, (int)(6 * ParticleScale));
        for (int i = 0; i < n; i++)
        {
            float d = reach * (0.12f + 0.85f * GD.Randf()), spread = d * 0.16f;   // (NEW) tighter jet — motes hug the axis (was 0.32) & reach further
            var p = o + dir * d + right * ((GD.Randf() - 0.5f) * spread) + up * ((GD.Randf() - 0.5f) * spread);
            float rr = 0.18f + d * 0.04f;   // (NEW) slimmer motes so it doesn't blanket the view (was 0.26 + 0.05d)
            var fl = new MeshInstance3D { Mesh = new SphereMesh { Radius = rr, Height = rr * 2f, RadialSegments = 6, Rings = 4 } };
            var mm = Emissive(col.Lerp(new Color(1f, 0.28f, 0.05f), GD.Randf() * 0.55f), 3.4f);
            mm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; fl.MaterialOverride = mm;
            AddChild(fl); fl.GlobalPosition = p;
            var tw = fl.CreateTween(); tw.SetParallel(true);
            tw.TweenProperty(fl, "scale", Vector3.One * 1.7f, 0.22f);
            tw.TweenProperty(fl, "global_position", p + dir * 0.5f + Vector3.Up * 0.25f, 0.22f);   // drift along the flame + a little rise
            tw.TweenProperty(mm, "albedo_color", new Color(mm.AlbedoColor.R, mm.AlbedoColor.G, mm.AlbedoColor.B, 0f), 0.22f);
            tw.Chain().TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(fl)) fl.QueueFree(); }));
        }
    }

    // (NEW) Ember meteor: host/caster owns the damage; allies spawn a visual ghost via VFX kind 67.
    public void SpawnEmberMeteor(Vector3 at, float radius, float dmg, int burnStacks, float burnPer, float bombFlat, Player src, float fallTime = 1.7f)
    {
        var m = new EmberMeteor(); AddChild(m); m.Init(at, radius, dmg, burnStacks, burnPer, bombFlat, src, fallTime);
        NetMgr?.BroadcastVfx(67, at, Vector3.Zero, radius, fallTime, DamageTypes.Col(DamageType.Ember));   // b = fall time so the ghost falls at the same rate
    }
    public void SpawnEmberMeteorGhost(Vector3 at, float radius, float fallTime) { var m = new EmberMeteor(); AddChild(m); m.InitRemote(at, radius, fallTime); }

    // (PHOENIX) spawn a phoenix-dive projectile. `simulate` = this machine owns the enemy grab/carry/damage (host or solo);
    // non-sim instances fly the same deterministic visual bird only.
    public void SpawnPhoenixDive(Player caster, Vector3 origin, Vector3 dir, int tier, bool mod, bool simulate, float touchDmg, float grabDmg, float bossFrac, float baseUnit)
    {
        var pd = new PhoenixDive(); AddChild(pd); pd.Init(caster, origin, dir, tier, mod, simulate, touchDmg, grabDmg, bossFrac, baseUnit);
    }
    // routes through the host in MP (NetMgr owns the sim); spawns a simulating instance directly when solo / no NetMgr
    public void FirePhoenixDive(Player caster, Vector3 origin, Vector3 dir, int tier, bool mod, float touchDmg, float grabDmg, float bossFrac, float baseUnit)
    {
        if (NetMgr != null) NetMgr.FirePhoenixDive(caster, origin, dir, tier, mod, touchDmg, grabDmg, bossFrac, baseUnit);
        else SpawnPhoenixDive(caster, origin, dir, tier, mod, true, touchDmg, grabDmg, bossFrac, baseUnit);
    }

    // (ARCANE STORM) host-authoritative rain field, same routing as the phoenix dive
    public void SpawnArcaneStorm(Player caster, Vector3 pos, float radius, float dur, bool remote, bool mod, int tier, float baseDmg, float hpScale, float bossCapMul, float critChance, float critMul)
    {
        var st = new ArcaneStorm(); AddChild(st); st.Init(caster, pos, radius, dur, remote, mod, tier, baseDmg, hpScale, bossCapMul, critChance, critMul);
    }
    public void FireArcaneStorm(Player caster, Vector3 pos, float radius, float dur, bool mod, int tier, float baseDmg, float hpScale, float bossCapMul, float critChance, float critMul)
    {
        if (NetMgr != null) NetMgr.FireArcaneStorm(caster, pos, radius, dur, mod, tier, baseDmg, hpScale, bossCapMul, critChance, critMul);
        else SpawnArcaneStorm(caster, pos, radius, dur, false, mod, tier, baseDmg, hpScale, bossCapMul, critChance, critMul);
    }

    // (NEW) a little visible GUST of wind — a scatter of leaves/motes that tumbles downwind past the player. Local ambience.
    private float _windGustT = 2f;
    public void SpawnWindGust(Vector3 near)
    {
        var dir = new Vector3(0.85f, 0, 0.5f).Normalized();   // matches the foliage shader's wind direction
        int n = Mathf.Max(4, (int)(9 * ParticleScale));
        var start = near - dir * 16f + new Vector3((float)GD.RandRange(-7, 7), 0, (float)GD.RandRange(-7, 7));
        for (int i = 0; i < n; i++)
        {
            var col = (CurBiome == Biome.Rainforest ? new Color(0.35f, 0.62f, 0.28f) : new Color(0.55f, 0.55f, 0.7f)).Lerp(new Color(0.72f, 0.7f, 0.42f), GD.Randf());
            var leaf = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.22f, 0.02f, 0.32f) } };
            var mm = Emissive(col, 0.35f); mm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; mm.AlbedoColor = new Color(col.R, col.G, col.B, 0.7f); mm.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
            leaf.MaterialOverride = mm; AddChild(leaf);
            var p0 = start + new Vector3((float)GD.RandRange(-3, 3), (float)GD.RandRange(0.6, 4.5), (float)GD.RandRange(-3, 3));
            leaf.GlobalPosition = p0; leaf.Rotation = new Vector3(GD.Randf() * 6f, GD.Randf() * 6f, GD.Randf() * 6f);
            float travel = 26f + GD.Randf() * 12f, dur = 2.4f + GD.Randf() * 1.3f;
            var end = p0 + dir * travel + new Vector3(0, (float)GD.RandRange(-1, 2), 0);
            var tw = leaf.CreateTween(); tw.SetParallel(true);
            tw.TweenProperty(leaf, "global_position", end, dur);
            tw.TweenProperty(leaf, "rotation", leaf.Rotation + new Vector3(GD.Randf() * 16f, GD.Randf() * 16f, GD.Randf() * 16f), dur);
            tw.TweenProperty(mm, "albedo_color", new Color(col.R, col.G, col.B, 0f), dur);
            tw.Chain().TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(leaf)) leaf.QueueFree(); }));
        }
    }

    // (NEW) croc bomber's lobbed grenade — host owns damage; allies spawn a ghost via VFX kind 76
    public void SpawnCrocBomb(Vector3 from, Vector3 to, float dmg, float radius)
    {
        var b = new CrocBomb(); AddChild(b); b.Init(from, to, dmg, radius, false);
        NetMgr?.BroadcastVfx(76, to, from, radius, dmg, Colors.White);
    }
    public void SpawnCrocBombGhost(Vector3 to, Vector3 from, float radius) { var b = new CrocBomb(); AddChild(b); b.Init(from, to, 0f, radius, true); }

    // (NEW) Eruption charged-mod VFX — dark ROCK CHUNKS heave up out of the ground (glowing molten seams) + molten lava pools + a flame ring.
    public void SpawnMoltenEruption(Vector3 center, float radius, bool net = true)
    {
        center = new Vector3(center.X, SurfaceHeight(center, center.Y), center.Z);
        var rockMat = Toon(new Color(0.13f, 0.11f, 0.10f), 0.92f, 0.35f, 0.02f);
        var moltenMat = Emissive(new Color(1f, 0.4f, 0.08f), 2.6f);
        int n = Mathf.Max(7, (int)(12 * ParticleScale));
        for (int i = 0; i < n; i++)   // chunks of rock upheaving, then crumbling back
        {
            float a = GD.Randf() * Mathf.Tau, d = GD.Randf() * radius * 0.85f;
            var gp = center + new Vector3(Mathf.Cos(a) * d, 0, Mathf.Sin(a) * d);
            float s = 0.4f + GD.Randf() * 1.0f;
            var rock = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(s, s * 0.85f, s * (0.7f + GD.Randf() * 0.6f)) }, MaterialOverride = rockMat };
            AddChild(rock);
            rock.GlobalPosition = new Vector3(gp.X, gp.Y - 0.4f, gp.Z);
            rock.Rotation = new Vector3(GD.Randf() * 3f, GD.Randf() * 6f, GD.Randf() * 3f);
            var seam = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(s * 0.92f, s * 0.22f, s * 0.62f) }, MaterialOverride = moltenMat };
            seam.Position = new Vector3(0, -s * 0.4f, 0); rock.AddChild(seam);   // glowing molten underside
            float up = 0.6f + GD.Randf() * 1.7f;
            var tw = rock.CreateTween();
            tw.TweenProperty(rock, "global_position", new Vector3(gp.X, gp.Y + up, gp.Z), 0.2f).SetEase(Tween.EaseType.Out);
            tw.TweenProperty(rock, "global_position", new Vector3(gp.X, gp.Y + 0.08f, gp.Z), 0.5f).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
            tw.TweenInterval(0.9f + GD.Randf());
            tw.TweenProperty(rock, "scale", Vector3.Zero, 0.35f);
            tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(rock)) rock.QueueFree(); }));
        }
        int pools = Mathf.Max(3, (int)(6 * ParticleScale));
        for (int i = 0; i < pools; i++)   // glowing lava pools left on the ground
        {
            float a = GD.Randf() * Mathf.Tau, d = GD.Randf() * radius * 0.8f;
            var gp = center + new Vector3(Mathf.Cos(a) * d, 0.04f, Mathf.Sin(a) * d);
            float pr = 0.5f + GD.Randf() * 1.1f;
            var pool = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = pr, BottomRadius = pr, Height = 0.05f, RadialSegments = 12 } };
            var pm = Emissive(new Color(1f, 0.38f, 0.06f), 2.8f); pm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; pm.AlbedoColor = new Color(1f, 0.38f, 0.06f, 0.95f);
            pool.MaterialOverride = pm; AddChild(pool); pool.GlobalPosition = gp; pool.Scale = new Vector3(0.2f, 1f, 0.2f);
            var tw = pool.CreateTween();
            tw.TweenProperty(pool, "scale", new Vector3(1.2f, 1f, 1.2f), 0.4f).SetEase(Tween.EaseType.Out);
            tw.TweenInterval(1.2f);
            tw.TweenProperty(pm, "albedo_color", new Color(0.3f, 0.06f, 0.02f, 0f), 0.7f);
            tw.TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(pool)) pool.QueueFree(); }));
        }
        SpawnEmberBurst(center + Vector3.Up * 0.3f, radius, false);   // the flame ring bursting out over the rubble
        VfxRing(center, DamageTypes.Col(DamageType.Ember), radius * 1.3f, 0.5f);
        Sfx?.ModEmber(center, false);
        if (net) NetMgr?.BroadcastVfx(77, center, Vector3.Zero, radius, 0f, DamageTypes.Col(DamageType.Ember));   // allies see it
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
        env.AdjustmentContrast = 1.05f;
        env.AdjustmentSaturation = 1.16f;   // (AUTUMN) richer so the reds/oranges/golds pop as vibrant fall
        // soft moonlit glow on emissives — eased back from the old synthwave neon blast (NEW)
        env.GlowEnabled = true;
        env.GlowIntensity = 0.45f;
        env.GlowBloom = 0.11f;      // (PAINTERLY) eased so bright emissives glow instead of blowing to flat white
        env.GlowStrength = 0.95f;
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
        ApplyShadowQuality();   // (PERF) set PSSM splits + distance from GfxQuality (default was 4-split @ 100m — very heavy with a big swarm + dense foliage)

        // Soft cool fill from the opposite side (no shadow) — a faint forest/moonlight bounce for the two-tone cel look (NEW)
        var fill = new DirectionalLight3D();
        fill.RotationDegrees = new Vector3(-28, 140, 0);
        fill.LightColor = new Color(0.44f, 0.36f, 0.28f);   // (AUTUMN) warm amber bounce in the shadows (was cool teal)
        fill.LightEnergy = 0.45f;
        fill.ShadowEnabled = false;
        AddChild(fill);

        // Procedural streaming world (chunks load around the player).
        _world = new World();
        if (_forcedWorldSeed.HasValue) { WorldSeed = _forcedWorldSeed.Value; GD.Print($"[SEED] forced WorldSeed={WorldSeed}"); }   // DEV scenario: deterministic map
        else if (IsAuthority) WorldSeed = (long)(((ulong)GD.Randi() << 32) ^ (ulong)GD.Randi() ^ 0x9E3779B97F4A7C15UL);   // host/solo pick the map; clients get it over the net (NEW)
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

    // ===== LEVEL PORTAL + advance to the next biome (NEW) =====
    private Node3D _levelPortal; private Vector3 _levelPortalPos; private bool _levelPortalActive = false; private int _portalWave = -1;

    // ===== BOSS LAIR: the world's boss is a discoverable structure you challenge when ready — not a wave-10 gate =====
    private BossLair _bossLair;
    private bool _bossFightActive = false;    // the world boss is summoned + alive
    public bool WorldBossDown = false;        // this world's boss is defeated → the portal is (or is about to be) open
    public int BossNerfStacks = 0;            // (FUTURE) 0-3 hidden nerfers found on the map → each weakens the summoned boss
    public BossLair Lair => _bossLair;        // for the minimap marker
    public void SetRemoteBossLair(Vector3 pos, int netId)   // client: build/refresh the lair ghost from the host
    {
        if (_bossLair != null && GodotObject.IsInstanceValid(_bossLair)) _bossLair.QueueFree();
        _bossLair = new BossLair { NetId = netId, Remote = true };
        AddChild(_bossLair); _bossLair.GlobalPosition = pos;
    }
    public void SetRemoteBossLairState(int state) { if (_bossLair != null && GodotObject.IsInstanceValid(_bossLair)) _bossLair.SetState(state); }

    public void SpawnBossLair()
    {
        if (!IsAuthority || !InOverworld) return;
        if (_bossLair != null && GodotObject.IsInstanceValid(_bossLair)) { _bossLair.QueueFree(); _bossLair = null; }
        _bossFightActive = false; WorldBossDown = false;
        var pos = GroundedDrySpawn(Vector3.Zero, 200f, World.WorldRadius - 60f);   // a real trek out from the spawn point
        _mapOccupied.Add(pos);   // (MAP FILL) reserve the lair's spot so nothing else piles on it
        _bossLair = new BossLair { NetId = NextPickupId() };
        AddChild(_bossLair); _bossLair.GlobalPosition = pos;
        NetMgr?.BroadcastBossLair(pos, _bossLair.NetId);
        Hud?.Banner("a boss lair looms out in this land — challenge it when you're ready");
    }

    // hold-E on the lair: challenge the boss (any warden, from wave 2 on). Client asks the host; host summons.
    private void TryChallengeBoss()
    {
        if (_bossLair == null || !GodotObject.IsInstanceValid(_bossLair) || _bossLair.State != 0) return;
        if (Wave < 2) { Hud?.Banner("the lair is still sealed — survive a wave first"); Sfx?.Denied(); return; }
        if (NetMgr != null && NetMgr.Active && !IsAuthority) { NetMgr.RequestChallengeBoss(); return; }
        HostSummonBoss();
    }
    public void HostSummonBoss()
    {
        if (!IsAuthority || _bossLair == null || !GodotObject.IsInstanceValid(_bossLair) || _bossFightActive || WorldBossDown) return;
        _bossFightActive = true;
        _bossLair.SetState(1); NetMgr?.BroadcastBossLairState(1);
        var e = SpawnBossAt("boss", _bossLair.GlobalPosition + new Vector3(0, 0, 5f));   // emerges just in front of the gate; scales to the CURRENT wave (SpawnBossAt configs with Wave)
        if (e != null && BossNerfStacks > 0) e.ScaleBossPower(Mathf.Clamp(1f - 0.12f * BossNerfStacks, 0.4f, 1f));   // (FUTURE) the 3 nerfers weaken it
        _boss = e; _bossAddT = 5f; _bossWaveT = 22f;
        if (_sacrificeArmed && e != null)   // (NERFER Sacrifice) drop the crimson drain sigil under the boss — drains up to 10% of his max while he stands in it
        {
            var sig = new BossDrainSigil(); AddChild(sig);
            sig.GlobalPosition = new Vector3(e.GlobalPosition.X, 0.05f, e.GlobalPosition.Z);
            NetMgr?.BroadcastDrainSigil(sig.GlobalPosition);
        }
        _bossAddPool = new System.Collections.Generic.List<string> { "shade", "swarmer", "caster", "flyer", "brute", "diver", "hexer" };
        _bossAddGroup = 4 + WardenCount * 2; _bossDpsInit = false; _bossDmgAccum = 0f; _bossPrevDps = 0f; BossRecentDps = 0f;
        Hud?.Banner("THE LAIR AWAKENS — the boss emerges! the waves press on…"); Sfx?.Thunder();
        NetMgr?.BroadcastBossSummon();
    }
    // the world boss died. THE single chokepoint — a FUTURE second-stage revive-enraged would intercept HERE instead of opening the portal.
    private void OnWorldBossDefeated()
    {
        _bossFightActive = false; WorldBossDown = true;
        if (_bossLair != null && GodotObject.IsInstanceValid(_bossLair)) { _bossLair.SetState(2); NetMgr?.BroadcastBossLairState(2); }
        _sanctuaryArmed = false;   // (NERFER) sanctuary regen ends when the boss is fully defeated
        if (Player != null) Player.Sanctuary = false;
        Hud?.Banner("THE LAIR FALLS SILENT — the way onward opens");
        SpawnLevelPortal();
    }

    // ===== NERFER SHRINES (Grove): three hidden shrines that each weaken the coming boss fight =====
    public Enemy WorldBoss => _boss;   // for the drain sigil + the arcane unicorn
    private readonly System.Collections.Generic.List<NerfShrine> _nerfers = new();
    public System.Collections.Generic.List<NerfShrine> Nerfers => _nerfers;
    public int ShrinesDone { get { int n = 0; foreach (var s in _nerfers) if (s != null && s.State == 2) n++; return n; } }
    public const int ShrinesTotal = 3;
    private bool _sacrificeArmed = false;      // → a crimson drain sigil drops under the boss when he spawns
    public bool SanctuaryArmed => _sanctuaryArmed;
    private bool _sanctuaryArmed = false;      // → 2 HP/s party regen while the boss is up
    private readonly System.Collections.Generic.List<Enemy> _sacMinibosses = new();      // slay them all to arm the drain
    private readonly System.Collections.Generic.HashSet<long> _sanctuaryPaid = new();    // peers who've paid their soul share
    private float _summonerT = 0f;             // the Summoner ward-defend countdown (State 1)
    private NerfShrine _summonerShrine;        // the shrine running the ward-defend
    public bool SummonerActive => _summonerShrine != null && GodotObject.IsInstanceValid(_summonerShrine) && _summonerT > 0f;   // for the HUD defend-timer
    public float SummonerTimeLeft => Mathf.Max(0f, _summonerT);
    public Vector3 SummonerPos => _summonerShrine != null && GodotObject.IsInstanceValid(_summonerShrine) ? _summonerShrine.GlobalPosition : Vector3.Zero;
    public const int SanctuaryShare = 40;      // souls each warden contributes (tunable)
    public int SanctuaryPaidCount => _sanctuaryPaid.Count;

    public void SpawnNerfers()
    {
        if (!IsAuthority) return;
        foreach (var s in _nerfers.ToArray()) if (GodotObject.IsInstanceValid(s)) s.QueueFree();
        _nerfers.Clear();
        _sacrificeArmed = false; _sanctuaryArmed = false; _sacMinibosses.Clear(); _sanctuaryPaid.Clear();
        _summonerT = 0f; _summonerShrine = null;
        if (!InOverworld || CurBiome != Biome.Grove) return;   // Grove-specific set for now — the Jungle gets its own flavours later
        for (int k = 0; k < 3; k++)
        {
            var pos = SpreadPointInWorld(_mapOccupied, 90f);
            var sh = new NerfShrine { Kind = (NerfKind)k, NetId = NextPickupId() };
            AddChild(sh); sh.GlobalPosition = pos; _nerfers.Add(sh); _mapOccupied.Add(pos);
        }
        NetMgr?.BroadcastNerfers(_nerfers);
    }
    public void SetRemoteNerfers(int[] kinds, int[] ids, float[] px, float[] py, float[] pz)
    {
        foreach (var s in _nerfers.ToArray()) if (GodotObject.IsInstanceValid(s)) s.QueueFree();
        _nerfers.Clear();
        _sanctuaryArmed = false; _sanctuaryBossSeen = false; _sanctuaryPaid.Clear(); if (Player != null) Player.Sanctuary = false;   // fresh world → reset client nerfer state
        for (int i = 0; i < ids.Length; i++)
        {
            var sh = new NerfShrine { Kind = (NerfKind)kinds[i], NetId = ids[i], Remote = true };
            AddChild(sh); sh.GlobalPosition = new Vector3(px[i], py[i], pz[i]); _nerfers.Add(sh);
        }
    }
    public void SetRemoteNerferState(int netId, int state)
    {
        foreach (var s in _nerfers) if (s != null && GodotObject.IsInstanceValid(s) && s.NetId == netId)
        {
            s.SetState(state);
            if (s.Kind == NerfKind.Summoner)   // (client HUD) mirror the defend-timer locally so the countdown shows for allies too
            {
                if (state == 1) { _summonerShrine = s; _summonerT = 45f; }
                else if (_summonerShrine == s) { _summonerShrine = null; _summonerT = 0f; }
            }
            break;
        }
    }

    // hold-E dispatch — each kind has its own flow
    private void TryActivateNerfer(NerfShrine s)
    {
        if (s == null || !GodotObject.IsInstanceValid(s) || s.State == 2) return;
        switch (s.Kind)
        {
            case NerfKind.Summoner:
                if (s.State != 0) return;
                if (NetMgr != null && NetMgr.Active && !IsAuthority) { NetMgr.RequestNerfer(s.NetId); return; }
                HostStartSummoner(s);
                break;
            case NerfKind.Sacrifice:
                if (s.State != 0) return;
                if (NetMgr != null && NetMgr.Active && !IsAuthority) { NetMgr.RequestNerfer(s.NetId); return; }
                HostBeginSacrifice(s);
                break;
            case NerfKind.Sanctuary:
                TrySanctuaryContribute(s);
                break;
        }
    }
    public void HostStartSummoner(NerfShrine s)
    {
        if (!IsAuthority || s == null || s.State != 0) return;
        s.SetState(1); NetMgr?.BroadcastNerferState(s.NetId, 1);
        _summonerShrine = s; _summonerT = 45f;
        Hud?.Banner("the Summoning has begun — DEFEND the glowing circle for 45s!"); Sfx?.RiteWin();
    }
    public void SetRemoteDrainSigil(Vector3 pos)   // client: visual-only ghost of the crimson sigil (host drives the actual drain)
    {
        var sig = new BossDrainSigil(); AddChild(sig); sig.GlobalPosition = pos;
    }
    public void HostBeginSacrifice(NerfShrine s)
    {
        if (!IsAuthority || s == null || s.State != 0) return;
        s.SetState(1); NetMgr?.BroadcastNerferState(s.NetId, 1);
        NetMgr?.BroadcastSacrificeCost();                 // every warden pays 40% of CURRENT HP
        if (Player != null) Player.Hp = Mathf.Max(1f, Player.Hp * 0.6f);
        int n = Mathf.Max(1, WardenCount);
        for (int i = 0; i < n; i++)
        {
            var mb = SpawnBossAt("miniboss", s.GlobalPosition + new Vector3(Mathf.Cos(i * 2.3f) * 8f, 0, Mathf.Sin(i * 2.3f) * 8f));
            if (mb != null) _sacMinibosses.Add(mb);
        }
        Hud?.Banner("A SACRIFICE — blood paid, guardians rise. slay them all to curse the boss!"); Sfx?.Thunder();
    }
    public void ClientSacrificeCost() { if (Player != null) Player.Hp = Mathf.Max(1f, Player.Hp * 0.6f); }
    private void OnSacMinibossDied(Enemy e)
    {
        _sacMinibosses.Remove(e);
        if (_sacMinibosses.Count == 0 && !_sacrificeArmed)
        {
            _sacrificeArmed = true;
            foreach (var s in _nerfers) if (s != null && s.Kind == NerfKind.Sacrifice) { s.SetState(2); NetMgr?.BroadcastNerferState(s.NetId, 2); }
            Hud?.Banner("the sacrifice is complete — a draining sigil will curse the boss's ground");
            if (_bossFightActive && _boss != null && GodotObject.IsInstanceValid(_boss))   // boss already up → drop the sigil right now
            {
                var sig = new BossDrainSigil(); AddChild(sig);
                sig.GlobalPosition = new Vector3(_boss.GlobalPosition.X, 0.05f, _boss.GlobalPosition.Z);
                NetMgr?.BroadcastDrainSigil(sig.GlobalPosition);
            }
        }
    }
    private void TrySanctuaryContribute(NerfShrine s)
    {
        if (s.State == 2) return;
        long me = LocalPeer;
        if (_sanctuaryPaid.Contains(me)) { Hud?.Banner("you've paid your share — await the others"); return; }
        if (Souls < SanctuaryShare) { Sfx?.Denied(); Hud?.Banner($"the sanctuary needs {SanctuaryShare} souls from you"); return; }
        Souls -= SanctuaryShare;
        if (NetMgr != null && NetMgr.Active && !IsAuthority) { NetMgr.RequestSanctuaryPay(s.NetId); _sanctuaryPaid.Add(me); return; }
        HostSanctuaryPaid(s.NetId, me);
    }
    public void HostSanctuaryPaid(int netId, long peer)
    {
        if (!IsAuthority) return;
        _sanctuaryPaid.Add(peer);
        NerfShrine s = null; foreach (var n in _nerfers) if (n != null && n.NetId == netId) { s = n; break; }
        if (s == null || s.State == 2) return;
        if (_sanctuaryPaid.Count >= Mathf.Max(1, WardenCount))
        {
            _sanctuaryArmed = true; s.SetState(2);
            NetMgr?.BroadcastNerferState(s.NetId, 2); NetMgr?.BroadcastSanctuaryArmed();
            if (Player != null) Player.Sanctuary = true;
            Hud?.Banner("the sanctuary awakens — a blessing of mending will guard you in the fight");
        }
    }
    public void ClientSanctuaryArmed() { _sanctuaryArmed = true; if (Player != null) Player.Sanctuary = true; }

    // per-frame nerfer upkeep: the Summoner ward countdown + the Sanctuary regen during the fight
    private void UpdateNerfers(float dt)
    {
        if (_summonerT > 0f)
        {
            _summonerT -= dt;   // count down on EVERY machine so the HUD defend-timer reads right for host + clients
            if (IsAuthority)
            {
                if (_summonerT <= 0f && _summonerShrine != null && GodotObject.IsInstanceValid(_summonerShrine))
                {
                    _summonerShrine.SetState(2); NetMgr?.BroadcastNerferState(_summonerShrine.NetId, 2);
                    SpawnArcaneUnicorn(_summonerShrine.GlobalPosition);
                    Hud?.Banner("an ARCANE SPECTRE answers — it will follow you until the boss awakens");
                    _summonerShrine = null;
                }
                else if (_summonerT > 0f) { _summonerSpawnT -= dt; if (_summonerSpawnT <= 0f) { _summonerSpawnT = 1.6f; for (int i = 0; i < WardenCount; i++) SpawnAdd(); } }   // waves of adds attack the circle
            }
            else if (_summonerT <= 0f) _summonerShrine = null;   // client cleanup (the State 2 sync also handles the shrine)
            if (_summonerT <= 0f) { Sfx?.WardComplete(); _summonerSounding = false; }   // (SFX) same witchy relief ding as a rite completing — every machine
        }
        UpdateSummonerSound();   // (SFX) the warding ritual's rising charge-drone while you defend the Summoning
        // (NERFER Summoner) stream the unicorn's position to clients so they see it follow/charge (~10Hz)
        if (IsAuthority && _unicorn != null && GodotObject.IsInstanceValid(_unicorn) && NetMgr != null && NetMgr.Active)
        {
            _unicornSyncT -= dt;
            if (_unicornSyncT <= 0f) { _unicornSyncT = 0.1f; NetMgr.BroadcastUnicorn(_unicorn.GlobalPosition, _unicorn.Charging); }
        }
        if (_sanctuaryArmed && Player != null)   // (NERFER) party regen while the boss rages — each machine heals its own; works on host AND client
        {
            bool bossUp = false;
            foreach (var e in Enemies) if (e != null && GodotObject.IsInstanceValid(e) && e.IsBoss && !e.Dead) { bossUp = true; break; }
            if (bossUp) { _sanctuaryBossSeen = true; if (!Player.Downed) Player.Heal(2f * dt); }
            else if (_sanctuaryBossSeen) { _sanctuaryArmed = false; _sanctuaryBossSeen = false; Player.Sanctuary = false; }   // the boss we buffed against is gone → sanctuary spent (covers clients, who don't run OnWorldBossDefeated)
        }
    }
    // (SFX) mirror the warding ritual's charge drone for the Summoning defend: rising hum while you're near + the fill climbs, relief ding at 100%
    private bool _summonerSounding = false;
    private void UpdateSummonerSound()
    {
        var pl = Player;
        bool near = SummonerActive && pl != null
                    && new Vector2(SummonerPos.X - pl.GlobalPosition.X, SummonerPos.Z - pl.GlobalPosition.Z).Length() <= NerfShrine.WardRadius * 1.25f;
        if (near) { Sfx?.WardCharge(Mathf.Clamp(1f - _summonerT / 45f, 0f, 1f)); _summonerSounding = true; }
        else if (_summonerSounding) { _summonerSounding = false; Sfx?.WardChargeStop(); }
    }
    private bool _sanctuaryBossSeen = false;
    private float _summonerSpawnT = 1.6f;
    private ArcaneUnicorn _unicorn;
    private float _unicornSyncT = 0f;
    public void SpawnArcaneUnicorn(Vector3 pos)   // (NERFER Summoner) the arcane spectre — host drives it; position streamed to clients
    {
        if (_unicorn != null && GodotObject.IsInstanceValid(_unicorn)) _unicorn.QueueFree();
        _unicorn = new ArcaneUnicorn();
        AddChild(_unicorn); _unicorn.GlobalPosition = new Vector3(pos.X, SurfaceHeight(pos, 60f), pos.Z);
    }
    // (NERFER Summoner) T-firework recall — the unicorn follows the warden who fired it. Client → host; host applies.
    public void RecallUnicorn(long peer)
    {
        if (_unicorn == null || !GodotObject.IsInstanceValid(_unicorn)) return;
        if (NetMgr != null && NetMgr.Active && !IsAuthority) { NetMgr.RequestUnicornRecall(); return; }
        _unicorn.RecallTo(peer);
    }
    public void SetRemoteUnicorn(Vector3 pos, bool charging)   // client: spawn/refresh the ghost from the host's stream
    {
        if (_unicorn == null || !GodotObject.IsInstanceValid(_unicorn)) { _unicorn = new ArcaneUnicorn { Remote = true }; AddChild(_unicorn); _unicorn.GlobalPosition = pos; }
        _unicorn.SetRemoteState(pos, charging);
    }
    public void SetRemoteUnicornGone(Vector3 pos, float bossRadius)   // client: the unicorn detonated — free ghost + bloom the cloud
    {
        if (_unicorn != null && GodotObject.IsInstanceValid(_unicorn)) { _unicorn.QueueFree(); _unicorn = null; }
        var nuke = new ArcaneNuke(); AddChild(nuke); nuke.GlobalPosition = pos; nuke.Init(bossRadius);
    }

    public void SpawnLevelPortal()
    {
        RemoveLevelPortal();
        _portalWave = Wave;
        var pp = Player != null ? Player.GlobalPosition : Vector3.Zero;
        var pos = pp + new Vector3(7f, 0, 7f);
        pos = new Vector3(pos.X, SurfaceHeight(pos, 60f), pos.Z);
        BuildPortalVisual(pos);
        _levelPortalActive = true; _levelPortalPos = pos;
        Hud?.Banner("a portal to the next land has opened — hold E to enter");
        NetMgr?.BroadcastPortal(pos);
    }
    public void ReceivePortal(Vector3 pos) { BuildPortalVisual(pos); _levelPortalActive = true; _levelPortalPos = pos; }
    private void BuildPortalVisual(Vector3 pos)
    {
        var col = new Color(0.45f, 1f, 0.62f);
        _levelPortal = new Node3D(); AddChild(_levelPortal); _levelPortal.GlobalPosition = pos;
        var ring = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = 1.7f, OuterRadius = 2.1f }, MaterialOverride = Emissive(col, 3f) };
        ring.Position = new Vector3(0, 2.4f, 0);
        _levelPortal.AddChild(ring);
        var disc = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 1.7f, BottomRadius = 1.7f, Height = 0.1f } };
        var dm = Emissive(col.Lerp(Colors.White, 0.3f), 2f); dm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; dm.AlbedoColor = new Color(col.R, col.G, col.B, 0.5f); dm.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        disc.MaterialOverride = dm; disc.Position = new Vector3(0, 2.4f, 0); disc.RotationDegrees = new Vector3(90, 0, 0);
        _levelPortal.AddChild(disc);
        var beam = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 1.7f, BottomRadius = 1.7f, Height = 14f } };
        var bm = Emissive(col, 1.2f); bm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; bm.AlbedoColor = new Color(col.R, col.G, col.B, 0.14f); bm.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        beam.MaterialOverride = bm; beam.Position = new Vector3(0, 7f, 0); _levelPortal.AddChild(beam);
        _levelPortal.AddChild(new OmniLight3D { OmniRange = 11f, LightColor = col, LightEnergy = 2.6f, Position = new Vector3(0, 3f, 0) });
    }
    public void RemoveLevelPortal()
    {
        if (_levelPortal != null && GodotObject.IsInstanceValid(_levelPortal)) _levelPortal.QueueFree();
        _levelPortal = null; _levelPortalActive = false;
    }
    public void AdvanceLevel()
    {
        if (NetMgr != null && NetMgr.Active && !IsAuthority) { NetMgr.RequestAdvanceLevel(); return; }   // a client asks the host
        int lvl = LevelNum + 1;
        var biome = lvl >= 2 ? Biome.Rainforest : Biome.Grove;   // only 2 biomes for now (2+ = Rainforest)
        long seed = (long)(((ulong)GD.Randi() << 32) ^ (ulong)GD.Randi() ^ 0x9E3779B97F4A7C15UL);
        ApplyLevelAdvance(lvl, biome, seed);
        NetMgr?.BroadcastLevelAdvance(lvl, (int)biome, seed);
    }
    public void ApplyLevelAdvance(int level, Biome biome, long seed)
    {
        Dbg.Log($"ApplyLevelAdvance level={level} biome={biome} start");
        if (biome != CurBiome) BiomeStartWave = Wave;   // (NEW) mark where this biome began for biome-relative ritual gating
        LevelNum = level; CurBiome = biome;
        RemoveLevelPortal();
        ClearGardenPortals(); _gardenSpawned = false;   // garden portals belong to the Grove only
        foreach (var e in Effigies) if (GodotObject.IsInstanceValid(e)) e.QueueFree(); Effigies.Clear(); _effigiesSpawned = false;   // (EFFIGY) new world → fresh effigies (cost tier persists across the run)
        foreach (var r in Rituals.ToArray()) if (GodotObject.IsInstanceValid(r)) r.QueueFree(); Rituals.Clear();   // (NEW) rituals persist per-world (no expiry) → wipe any unfinished one when you leave for a new map
        foreach (var g in GalePads.ToArray()) if (GodotObject.IsInstanceValid(g)) g.QueueFree(); GalePads.Clear();   // (GALE NET) old map's pads go away (clients too; host rebuilds via broadcast)
        ClearPedestals();   // (PLATFORMS) old map's daises + their walkable decks go away (host rebuilds via broadcast)
        foreach (var m in Magnets.ToArray()) if (GodotObject.IsInstanceValid(m)) m.QueueFree(); Magnets.Clear();   // (MAGNET DROP) uncollected lodestones don't follow you to the next map
        foreach (var w in WardArmors.ToArray()) if (GodotObject.IsInstanceValid(w)) w.QueueFree(); WardArmors.Clear(); _wardCd.Clear();   // (NEW) same for uncollected ward plating
        if (_haunt != null && GodotObject.IsInstanceValid(_haunt)) { _haunt.QueueFree(); _haunt = null; } HauntActive = false; _wasInHaunt = false; PlayerInHaunt = false;   // (HAUNT) old zone doesn't follow to the new map (PopulateMap spawns a fresh one)
        ClearDiscovered();   // (NEW) fresh fog of war on the new map
        if (InSky) ExitSky(false);                       // (NEW) advancing out of a level ends any active sky ritual
        if (_skyWhirl != null && GodotObject.IsInstanceValid(_skyWhirl)) { _skyWhirl.QueueFree(); _skyWhirl = null; }
        _skyWhirlActive = false; _skySpawned = false;    // (NEW) the whirlwind is biome-relative — re-arm it for the next jungle stretch
        foreach (var e in Enemies.ToArray()) if (GodotObject.IsInstanceValid(e)) e.QueueFree();
        Enemies.Clear(); _toSpawn.Clear();
        foreach (var o in Orbs.ToArray()) if (GodotObject.IsInstanceValid(o)) o.QueueFree();
        Orbs.Clear();   // (NEW) XP orbs from the old area don't follow you to the new one
        WorldSeed = seed;
        if (Player != null) { var o = new Vector3(0, SurfaceHeight(Vector3.Zero, 80f) + 1.5f, 0); Player.GlobalPosition = o; }
        _world?.Reseed((ulong)seed, Player != null ? Player.GlobalPosition : Vector3.Zero);
        if (IsAuthority) ResetVendorCadence();
        if (IsAuthority) PopulateMap();       // (MAP FILL) scatter EVERYTHING across the whole bounded disc, once, for the new map
        Hud?.Banner(biome == Biome.Rainforest ? "THE MAGICAL RAINFOREST" : "A NEW LAND");
        Sfx?.Thunder();
        Dbg.Log($"ApplyLevelAdvance level={level} END");
    }
    private void ResetVendorCadence()
    {
        if (_shop != null && GodotObject.IsInstanceValid(_shop)) _shop.QueueFree(); _shop = null;
        if (_scroll != null && GodotObject.IsInstanceValid(_scroll)) _scroll.QueueFree(); _scroll = null;
        foreach (var r in _roulettes) if (GodotObject.IsInstanceValid(r)) r.QueueFree(); _roulettes.Clear();
        foreach (var c in Chests) if (GodotObject.IsInstanceValid(c)) c.QueueFree(); Chests.Clear();
        _shopLastSpawnWave = Wave;
    }

    // ===== GARDEN PORTALS + cottage-garden maze entry (world 1 = the Grove) =====
    private readonly System.Collections.Generic.List<GardenPortal> _gPortals = new();
    private bool _gardenSpawned = false;
    private int _gardenPairSeq = 0;
    private Node3D _gateNode; private Node3D _wellNode; private Vector3 _gatePos; private bool _gateActive = false;   // maze entrance = an old moss well (_wellNode) in a garden (_gateNode)
    private bool _gardenRitual = false;
    private readonly System.Collections.Generic.HashSet<int> _gardenAmbushed = new();
    public bool GardenGateActive => _gateActive;
    public Vector3 GardenGatePos => _gatePos;
    public System.Collections.Generic.List<GardenPortal> GardenPortals => _gPortals;

    private static int PackCol(Color c) => (Mathf.RoundToInt(Mathf.Clamp(c.R, 0, 1) * 255) << 16) | (Mathf.RoundToInt(Mathf.Clamp(c.G, 0, 1) * 255) << 8) | Mathf.RoundToInt(Mathf.Clamp(c.B, 0, 1) * 255);
    private static Color UnpackCol(int v) => new Color(((v >> 16) & 0xFF) / 255f, ((v >> 8) & 0xFF) / 255f, (v & 0xFF) / 255f);

    // give the same gold to EVERY warden (portal chest 150, maze ritual 300) — routes through the host in MP
    public void RewardGoldAll(int amt)
    {
        if (NetMgr != null && NetMgr.Active) { if (IsAuthority) NetMgr.BroadcastGoldAll(amt); }
        else AddGold(amt);
    }

    // (EFFIGY) host/solo scatters the blessing shrines once, on the main explorable worlds, spaced far apart
    private void MaybeSpawnEffigies()
    {
        if (_effigiesSpawned || Player == null) return;
        if (NetMgr != null && NetMgr.Active && !IsAuthority) return;   // host-authoritative (client net-sync is a follow-up)
        if (InMaze || InExpedition || InSky) return;
        if (CurBiome != Biome.Grove && CurBiome != Biome.Rainforest) return;   // main explorable worlds only
        if (!SimActive || Wave < 2) return;
        SpawnEffigies();
    }
    private void SpawnEffigies()
    {
        if (!IsAuthority || !InOverworld) return;
        if (CurBiome != Biome.Grove && CurBiome != Biome.Rainforest) return;   // main explorable worlds only
        foreach (var e in Effigies.ToArray()) if (GodotObject.IsInstanceValid(e)) e.QueueFree();
        Effigies.Clear();
        _effigiesSpawned = true;
        int perType = 3 + (Mathf.Max(1, WardenCount) - 1);   // (TUNE) 3 of each of the 5 themes solo (=15) + 1 more per extra warden — was a bare 2×players that left solo sparse
        for (int kind = 0; kind < 5; kind++)
            for (int n = 0; n < perType; n++)
            {
                Vector3 pos;
                if (_pedestalTops.Count > 0 && _rng.Randf() < 0.6f)   // (PLATFORMS) prefer standing this effigy ON an empty pedestal (its Y syncs to clients)
                { int idx = _rng.RandiRange(0, _pedestalTops.Count - 1); pos = _pedestalTops[idx]; _pedestalTops.RemoveAt(idx); }
                else { pos = SpreadPointInWorld(_mapOccupied, 60f); _mapOccupied.Add(pos); }   // else disc-wide, spaced from everything else
                var ef = new Effigy { Kind = kind, NetId = NextPickupId() };
                AddChild(ef); ef.GlobalPosition = pos;
                Effigies.Add(ef);
            }
        Hud?.Banner("strange effigies stand scattered across the land — seek them out and rouse them");
        if (NetMgr != null && NetMgr.Active && IsAuthority) BroadcastEffigiesNet();   // clients build ghost copies
    }
    private void BroadcastEffigiesNet()
    {
        int n = Effigies.Count;
        var ids = new int[n]; var kinds = new int[n]; var px = new float[n]; var py = new float[n]; var pz = new float[n];
        for (int i = 0; i < n; i++) { var e = Effigies[i]; ids[i] = e.NetId; kinds[i] = e.Kind; px[i] = e.GlobalPosition.X; py[i] = e.GlobalPosition.Y; pz[i] = e.GlobalPosition.Z; }
        NetMgr.BroadcastEffigies(ids, kinds, px, py, pz);
    }
    // hold-E on an effigy: check your own gold, then activate locally (host/solo) or ask the host (client)
    private void TryActivateEffigy(Effigy ef, int cost)
    {
        if (ef == null || !GodotObject.IsInstanceValid(ef) || ef.Claimed) return;
        if (Souls < cost) { Sfx?.Denied(); Hud?.Banner($"need {cost} souls to rouse the {Effigy.KindName(ef.Kind)} effigy"); return; }
        if (NetMgr != null && NetMgr.Active && !IsAuthority) { NetMgr.RequestEffigy(ef.NetId); return; }   // client → host claims + grants
        HostActivateEffigy(ef.NetId, 0);   // solo / host activates its own
    }
    // host-authoritative: claim the shrine, bump the lobby-wide cost tier, then grant the themed pick to the activator
    public void HostActivateEffigy(int netId, long peer)
    {
        Effigy ef = null;
        foreach (var e in Effigies) if (e != null && GodotObject.IsInstanceValid(e) && e.NetId == netId && !e.Claimed) { ef = e; break; }
        if (ef == null) return;   // already spent / gone — deny (a client's optimistic gold check simply doesn't apply)
        int kind = Mathf.Clamp(ef.Kind, 0, 4), cost = EffigyCost(kind);
        _effigyActivations[kind]++;
        Effigies.Remove(ef); ef.Claim();
        if (NetMgr != null && NetMgr.Active)
        {
            NetMgr.BroadcastEffigyClaim(netId);
            NetMgr.BroadcastEffigyTiers((int[])_effigyActivations.Clone());
        }
        if (peer == 0) { Souls = Mathf.Max(0, Souls - cost); OpenEffigyPick(kind); }   // host / solo activator pays souls + rolls locally
        else NetMgr?.GrantEffigy(peer, kind, cost);                          // client activator: they pay + roll on their end
    }
    // client: the host granted my rousing — pay the quoted cost and take the themed pick locally (correct witch for Coven rolls)
    public void ClientEffigyGranted(int kind, int cost) { Souls = Mathf.Max(0, Souls - cost); OpenEffigyPick(kind); }

    // (HAUNT ECONOMY) rituals are now FREE — just hold-E to begin one (souls are spent only at effigies). Solo/host begins
    // it directly; a client asks the host.
    private void TryActivateRitual(RitualCircle r)
    {
        if (r == null || !GodotObject.IsInstanceValid(r) || r.Active || r.Done) return;
        if (NetMgr != null && NetMgr.Active && !IsAuthority) { NetMgr.RequestRitual(r.NetId); return; }   // client → host begins it
        r.BeginRite();   // solo / host begins
    }
    // host-authoritative: a client held E on this circle → begin the rite (no cost).
    public void HostBeginRitual(int netId, long peer)
    {
        RitualCircle r = null;
        foreach (var rc in Rituals) if (rc != null && GodotObject.IsInstanceValid(rc) && rc.NetId == netId && !rc.Active && !rc.Done) { r = rc; break; }
        if (r == null) return;   // already begun / gone
        r.BeginRite();
    }
    // client: the host began my rite (kept for RPC compatibility — no cost is charged now)
    public void ClientRitualCharged(int cost) { }
    // client: rebuild ghost effigies from the host's spawn broadcast
    public void ApplyEffigySync(int[] ids, int[] kinds, float[] px, float[] py, float[] pz)
    {
        foreach (var e in Effigies) if (GodotObject.IsInstanceValid(e)) e.QueueFree();
        Effigies.Clear();
        for (int i = 0; i < ids.Length; i++)
        {
            var ef = new Effigy { Kind = kinds[i], NetId = ids[i], Remote = true };
            AddChild(ef); ef.GlobalPosition = new Vector3(px[i], py[i], pz[i]); Effigies.Add(ef);
        }
        _effigiesSpawned = true;
    }
    public void ApplyEffigyClaim(int netId)
    {
        for (int i = Effigies.Count - 1; i >= 0; i--) { var e = Effigies[i]; if (e != null && e.NetId == netId) { Effigies.RemoveAt(i); if (GodotObject.IsInstanceValid(e)) e.Claim(); } }
    }
    public void ApplyEffigyTiers(int[] counts) { for (int i = 0; i < 5 && i < counts.Length; i++) _effigyActivations[i] = counts[i]; }
    private void OpenEffigyPick(int kind)
    {
        if (State != GameState.Playing) return;
        _effigyKind = kind; ChestPick = true; _pendingLevels++;
        Choices = RollChoices();          // reads _effigyKind for the themed roll
        // (FIX) do NOT clear _effigyKind here — it must persist so a reroll / luck-reroll of THIS pick stays scoped to the
        // effigy theme (it was clearing immediately, so rerolls fell back to the normal pool). Cleared on resolve in FinishStep.
        ChoiceGen++; RarityCue(Choices);
        State = GameState.LevelUp; Input.MouseMode = Input.MouseModeEnum.Visible; SelectLock = 0.3f;
        Hud?.Banner($"the {Effigy.KindName(kind)} effigy stirs — choose your boon");
    }

    // host places the portals once, when the Grove is up and running; clients receive ghosts via BroadcastGarden
    private void MaybeSpawnGardenPortals()
    {
        if (_gardenSpawned || !IsAuthority) return;
        if (CurBiome != Biome.Grove || InMaze || InExpedition || Player == null) return;
        if (!SimActive) return;
        if (Wave < 5) return;   // the maze + its portals only appear once you're a few waves deep
        SpawnGardenPortals();
    }

    private void SpawnGardenPortals()
    {
        _gardenSpawned = true;
        int sets = 2 + Mathf.Max(0, (WardenCount - 1) / 2);
        int mazeSet = _rng.RandiRange(0, sets - 1);
        _gatePos = SpreadPointInWorld(_mapOccupied, 90f); _mapOccupied.Add(_gatePos);   // (MAP FILL) the maze gate — out in the disc, spaced from everything; the minimap points the way
        // distinct hues — none are the mint-green of the next-world portal
        var palette = new Color[] {
            new Color(1f, 0.30f, 0.80f), new Color(1f, 0.55f, 0.15f), new Color(0.35f, 0.68f, 1f),
            new Color(0.78f, 0.40f, 1f), new Color(1f, 0.28f, 0.30f), new Color(0.20f, 0.85f, 0.95f),
        };
        // (MAP FILL) scattered disc-wide, spaced from the gate and everything else already on the map
        Vector3 ScatterEntrance()
        {
            var p = SpreadPointInWorld(_mapOccupied, 90f);
            _mapOccupied.Add(p);
            return p;
        }
        int id = 1;
        for (int s = 0; s < sets; s++)
        {
            var col = palette[s % palette.Length];
            var aPos = ScatterEntrance();
            int kind; Vector3 bPos;
            if (s == mazeSet) { kind = 1; float ga = _rng.RandfRange(0, Mathf.Tau); var gp = _gatePos + new Vector3(Mathf.Cos(ga) * 7f, 0, Mathf.Sin(ga) * 7f); bPos = new Vector3(gp.X, SurfaceHeight(gp, 60f), gp.Z); }
            else if (_rng.Randf() < 0.2f) { kind = 2; bPos = SpreadPointInWorld(_mapOccupied, 50f); _mapOccupied.Add(bPos); }
            else { kind = 3; bPos = SpreadPointInWorld(_mapOccupied, 50f); _mapOccupied.Add(bPos); }
            int pair = ++_gardenPairSeq;
            AddGardenPortal(id++, pair, aPos, bPos, col, kind, true);     // A: scattered entrance (marked)
            AddGardenPortal(id++, pair, bPos, aPos, col, kind, false);    // B: return end
            if (kind == 2) SpawnGoldChest(bPos);                          // hidden 150-gold chest on the far side
        }
        BuildGate(_gatePos);
        _gateActive = true;
        Hud?.Banner("shimmering portals have opened across the grove");
        if (NetMgr != null && NetMgr.Active && IsAuthority) BroadcastGarden();
    }

    private GardenPortal AddGardenPortal(int netId, int pair, Vector3 pos, Vector3 link, Color col, int kind, bool entrance, bool remote = false)
    {
        var p = new GardenPortal { NetId = netId, Pair = pair, Link = link, Tint = col, Kind = kind, IsEntrance = entrance, Remote = remote, Cooldown = 1.2f };
        AddChild(p);
        p.GlobalPosition = pos;
        _gPortals.Add(p);
        return p;
    }

    private void SpawnGoldChest(Vector3 at)
    {
        var c = new Chest { NetId = NextPickupId(), SpecialGold = 150, Hidden = true };
        AddChild(c);
        c.GlobalPosition = new Vector3(at.X, SurfaceHeight(at, 60f), at.Z);
        Chests.Add(c);
    }

    // the maze entrance: an OLD MOSS-COVERED WELL you hold E on to descend into the maze, standing in a lush garden
    // clearing (real wind-reactive trees, organic layered shrubs, flowerbeds, a stone stepping-path).
    private void BuildGate(Vector3 pos)
    {
        if (_gateNode != null && GodotObject.IsInstanceValid(_gateNode)) _gateNode.QueueFree();
        if (_wellNode != null && GodotObject.IsInstanceValid(_wellNode)) _wellNode.QueueFree();
        var root = new Node3D { Name = "MazeWellGarden" }; AddChild(root); root.GlobalPosition = pos; _gateNode = root;
        var jr = new RandomNumberGenerator { Seed = (ulong)(Mathf.RoundToInt(pos.X) * 92821 ^ Mathf.RoundToInt(pos.Z) * 68917) };   // deterministic layout

        _wellNode = new Node3D(); AddChild(_wellNode); _wellNode.GlobalPosition = pos;
        BuildWell(_wellNode, jr);
        var wellBlock = new Blocker { Pos = pos, Radius = 1.9f };   // solid well
        PersistentBlockers.Clear(); PersistentBlockers.Add(wellBlock);   // survives chunk streaming
        Blockers.Add(wellBlock);                                          // and takes effect right now

        // lush wind-reactive trees ringing the clearing (built the same way as the world's grove trees)
        for (int i = 0; i < 5; i++)
        {
            float a = (i + 0.35f) / 5f * Mathf.Tau, r = jr.RandfRange(8.5f, 11.5f);
            PlaceGardenTree(root, new Vector3(Mathf.Cos(a) * r, 0, Mathf.Sin(a) * r), jr);
        }
        // organic layered shrubs forming a loose border (with a gap toward the stone path)
        var shrub1 = ToonEmissive(new Color(0.15f, 0.34f, 0.17f), 0.14f, 0.04f);
        var shrub2 = ToonEmissive(new Color(0.20f, 0.44f, 0.22f), 0.18f, 0.04f);
        for (int i = 0; i < 22; i++)
        {
            float a = i / 22f * Mathf.Tau;
            if (Mathf.Abs(Mathf.Sin(a)) > 0.9f && Mathf.Cos(a) < 0f) continue;   // path opening
            var bp = new Vector3(Mathf.Cos(a) * (6.8f + jr.RandfRange(-0.7f, 0.7f)), 0, Mathf.Sin(a) * (6.8f + jr.RandfRange(-0.7f, 0.7f)));
            Shrub(root, bp, jr, shrub1, shrub2);
        }
        // flowerbeds around the well
        var flowerCols = new[] { new Color(1f, 0.5f, 0.7f), new Color(1f, 1f, 0.9f), new Color(1f, 0.85f, 0.35f), new Color(0.75f, 0.55f, 1f), new Color(1f, 0.4f, 0.4f) };
        var stemMat = ToonEmissive(new Color(0.16f, 0.34f, 0.18f), 0.15f, 0.04f);
        for (int i = 0; i < 30; i++)
        {
            float a = jr.RandfRange(0, Mathf.Tau), r = jr.RandfRange(2.6f, 6.2f);
            var fp = new Vector3(Mathf.Cos(a) * r, 0, Mathf.Sin(a) * r);
            var stem = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.02f, BottomRadius = 0.035f, Height = 0.5f }, MaterialOverride = stemMat };
            stem.Position = fp + new Vector3(0, 0.25f, 0); root.AddChild(stem);
            var bloom = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.14f, Height = 0.28f }, MaterialOverride = ToonEmissive(flowerCols[jr.RandiRange(0, flowerCols.Length - 1)], 0.9f, 0f) };
            bloom.Position = fp + new Vector3(0, 0.56f, 0); root.AddChild(bloom);
        }
        // a stone stepping-stone path leading in from the border opening
        var pathMat = ToonEmissive(new Color(0.4f, 0.4f, 0.38f), 0.05f, 0.03f);
        for (int i = 0; i < 5; i++)
        {
            var sp = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = jr.RandfRange(0.55f, 0.75f), BottomRadius = 0.55f, Height = 0.1f, RadialSegments = 8 }, MaterialOverride = pathMat };
            sp.Position = new Vector3(jr.RandfRange(-0.35f, 0.35f), 0.05f, -6.6f + i * 1.35f); sp.RotationDegrees = new Vector3(0, jr.RandfRange(0, 60), 0); root.AddChild(sp);
        }
    }

    // an old cobbled well: stone ring wall (mossy), a wooden frame + peaked roof, a bucket on a rope, and a faintly
    // glowing pool of water at the bottom that shimmers.
    private void BuildWell(Node3D root, RandomNumberGenerator rng)
    {
        var stone = ToonEmissive(new Color(0.42f, 0.42f, 0.40f), 0.05f, 0.03f);
        var stoneDk = ToonEmissive(new Color(0.26f, 0.26f, 0.25f), 0.04f, 0.03f);
        var moss = ToonEmissive(new Color(0.17f, 0.40f, 0.20f), 0.16f, 0.04f);
        var wood = ToonEmissive(new Color(0.27f, 0.17f, 0.09f), 0.06f, 0.03f);

        var wall = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 1.65f, BottomRadius = 1.75f, Height = 1.2f, RadialSegments = 16 }, MaterialOverride = stone };
        wall.Position = new Vector3(0, 0.6f, 0); root.AddChild(wall);
        var inner = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 1.32f, BottomRadius = 1.32f, Height = 1.3f, RadialSegments = 16 }, MaterialOverride = stoneDk };
        inner.Position = new Vector3(0, 0.62f, 0); root.AddChild(inner);
        var rim = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = 1.35f, OuterRadius = 1.78f, RingSegments = 16, Rings = 6 }, MaterialOverride = stone };
        rim.Position = new Vector3(0, 1.2f, 0); root.AddChild(rim);   // flat cap ring on top
        // faintly glowing water that shimmers (tween loop — no per-frame code)
        var waterMat = Emissive(new Color(0.35f, 0.8f, 0.62f), 1.4f);
        waterMat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; waterMat.AlbedoColor = new Color(0.05f, 0.16f, 0.14f, 0.9f); waterMat.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        var water = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 1.3f, BottomRadius = 1.3f, Height = 0.08f }, MaterialOverride = waterMat };
        water.Position = new Vector3(0, 0.7f, 0); root.AddChild(water);
        var tw = water.CreateTween().SetLoops();
        tw.TweenProperty(waterMat, "emission_energy_multiplier", 0.9f, 1.8f).SetTrans(Tween.TransitionType.Sine);
        tw.TweenProperty(waterMat, "emission_energy_multiplier", 2.6f, 1.8f).SetTrans(Tween.TransitionType.Sine);
        // moss patches crept over the stone
        for (int i = 0; i < 7; i++)
        {
            float a = rng.RandfRange(0, Mathf.Tau);
            var mo = new MeshInstance3D { Mesh = new SphereMesh { Radius = rng.RandfRange(0.3f, 0.55f), Height = 0.7f }, MaterialOverride = moss };
            mo.Position = new Vector3(Mathf.Cos(a) * 1.72f, rng.RandfRange(0.2f, 1.15f), Mathf.Sin(a) * 1.72f); mo.Scale = new Vector3(1f, 0.7f, 0.35f); root.AddChild(mo);
        }
        // wooden frame: two posts, a crossbeam, a peaked roof
        for (int s = -1; s <= 1; s += 2)
        {
            var post = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.1f, BottomRadius = 0.13f, Height = 2.6f, RadialSegments = 6 }, MaterialOverride = wood };
            post.Position = new Vector3(s * 1.5f, 1.3f, 0); post.RotationDegrees = new Vector3(0, 0, s * 2f); root.AddChild(post);
        }
        var beam = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.09f, BottomRadius = 0.09f, Height = 3.3f, RadialSegments = 6 }, MaterialOverride = wood };
        beam.Position = new Vector3(0, 2.6f, 0); beam.RotationDegrees = new Vector3(0, 0, 90); root.AddChild(beam);
        for (int s = -1; s <= 1; s += 2)
        {
            var plank = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(3.5f, 0.12f, 1.5f) }, MaterialOverride = wood };
            plank.Position = new Vector3(0, 3.15f, s * 0.68f); plank.RotationDegrees = new Vector3(s * 34f, 0, 0); root.AddChild(plank);
        }
        // rope + bucket
        var rope = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.02f, BottomRadius = 0.02f, Height = 1.3f, RadialSegments = 4 }, MaterialOverride = ToonEmissive(new Color(0.42f, 0.36f, 0.2f), 0.04f, 0f) };
        rope.Position = new Vector3(0.5f, 1.95f, 0); root.AddChild(rope);
        var bucket = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.28f, BottomRadius = 0.22f, Height = 0.42f, RadialSegments = 8 }, MaterialOverride = wood };
        bucket.Position = new Vector3(0.5f, 1.32f, 0); root.AddChild(bucket);
        root.AddChild(new OmniLight3D { OmniRange = 8f, LightColor = new Color(0.4f, 0.85f, 0.7f), LightEnergy = 1.4f, Position = new Vector3(0, 1.1f, 0) });
    }

    // one lush, wind-reactive tree built exactly like the world's grove trees (procedural mesh + wind shader materials)
    private void PlaceGardenTree(Node3D root, Vector3 localPos, RandomNumberGenerator rng)
    {
        var sp = ProcTree.Species.GroveOak;
        int variant = ProcTree.PickVariant(sp, rng, out _, out _, out _);
        var (bark, leaf, hasLeaves) = ProcTree.VariantMeshes(sp, variant);
        var (bmat, lmat) = ProcTree.SpeciesMats(sp);
        var holder = new Node3D { Position = localPos, Rotation = new Vector3(0, rng.Randf() * Mathf.Tau, 0) };
        root.AddChild(holder);
        holder.AddChild(new MeshInstance3D { Mesh = bark, MaterialOverride = bmat });
        if (hasLeaves && leaf != null) holder.AddChild(new MeshInstance3D { Mesh = leaf, MaterialOverride = lmat });
    }

    // an organic shrub: a clump of overlapping leafy lobes (no hard boxes)
    private void Shrub(Node3D root, Vector3 at, RandomNumberGenerator rng, Material m1, Material m2)
    {
        int lobes = rng.RandiRange(4, 6);
        float baseS = rng.RandfRange(0.75f, 1.15f);
        for (int i = 0; i < lobes; i++)
        {
            float s = baseS * rng.RandfRange(0.55f, 1f);
            var sph = new MeshInstance3D { Mesh = new SphereMesh { Radius = s, Height = s * 1.7f }, MaterialOverride = (i % 2 == 0 ? m1 : m2) };
            sph.Position = at + new Vector3(rng.RandfRange(-baseS, baseS) * 0.6f, s * 0.7f + rng.RandfRange(0f, 0.3f), rng.RandfRange(-baseS, baseS) * 0.6f);
            root.AddChild(sph);
        }
    }

    private void TakeGardenPortal(GardenPortal pt)
    {
        var oldPos = Player.GlobalPosition;
        var dest = new Vector3(pt.Link.X, SurfaceHeight(pt.Link, 60f) + 1.2f, pt.Link.Z);
        Player.GlobalPosition = dest;
        MoveEntsTo(dest, oldPos, 22f);   // nearby tree-ents come through with you
        foreach (var o in _gPortals) if (GodotObject.IsInstanceValid(o) && o.Pair == pt.Pair) o.Cooldown = 1.8f;
        VfxRing(dest, pt.Tint, 3f, 0.4f);
        if (pt.Kind == 3)   // ambush set — the FIRST use in EITHER direction springs elites where you arrive (host dedupes per pair)
        {
            if (IsAuthority) HostGardenAmbush(pt.Pair, dest);
            else NetMgr?.RequestGardenAmbush(pt.Pair, dest);
        }
    }

    // bring the local Verdant witch's tree-ents along on a teleport: only ents within `range` of `fromNear` follow
    // (pass a big range to gather them all), scattered around `to`. Owner-side; positions sync via MinionSnapshot.
    public void MoveEntsTo(Vector3 to, Vector3 fromNear, float range)
    {
        if (Player == null || Player.Ents == null) return;
        float r2 = range * range;
        foreach (var t in Player.Ents)
        {
            if (t == null || !GodotObject.IsInstanceValid(t)) continue;
            if ((t.GlobalPosition - fromNear).LengthSquared() > r2) continue;
            float a = GD.Randf() * Mathf.Tau, rr = GD.Randf() * 3f;
            t.GlobalPosition = new Vector3(to.X + Mathf.Cos(a) * rr, to.Y, to.Z + Mathf.Sin(a) * rr);
        }
    }

    public void HostGardenAmbush(int pair, Vector3 at)
    {
        if (!IsAuthority || !_gardenAmbushed.Add(pair)) return;   // once per set
        for (int i = 0; i < 3; i++)
        {
            string et = _rng.Randf() < 0.5f ? "sieger" : "caster";
            var e = new Enemy(); e.Configure(et, Wave); e.MakeElite();
            e.NetId = _netEnemySeq++; e.TypeIdx = EnemyKinds.Index(et);
            AddChild(e);
            float a = _rng.RandfRange(0, Mathf.Tau);
            e.GlobalPosition = new Vector3(at.X + Mathf.Cos(a) * 5f, e.Radius, at.Z + Mathf.Sin(a) * 5f);
            Enemies.Add(e);
        }
        Hud?.Banner("an ambush!");
    }

    // enter the cottage-garden maze (reuses the maze world-swap). MP: host drives it + mirrors to clients.
    public void EnterGardenMaze()
    {
        if (InMaze) return;
        // portals + well PERSIST (they only clear when you advance to the next world); the well caves in on exit
        _gardenRitual = true;
        NetMgr?.MazeEnterAll();   // the whole party descends together — track who's inside for the death/exit rules
        ulong seed = ((ulong)(uint)GD.Randi()) | ((ulong)(uint)GD.Randi() << 32);
        EnterMaze(seed);
        SetupRitual();
    }

    public void ClearGardenPortals(bool includeWell = true)
    {
        foreach (var p in _gPortals) if (GodotObject.IsInstanceValid(p)) p.QueueFree();
        _gPortals.Clear();
        if (includeWell)
        {
            if (_gateNode != null && GodotObject.IsInstanceValid(_gateNode)) { _gateNode.QueueFree(); _gateNode = null; }
            if (_wellNode != null && GodotObject.IsInstanceValid(_wellNode)) { _wellNode.QueueFree(); _wellNode = null; }
            PersistentBlockers.Clear();
            _gateActive = false;
        }
    }

    // the well collapses into rubble after you climb back out — a one-time entrance (keeps its solid collider)
    private void CaveInWell()
    {
        if (_wellNode != null && GodotObject.IsInstanceValid(_wellNode)) _wellNode.QueueFree();
        _wellNode = new Node3D(); AddChild(_wellNode); _wellNode.GlobalPosition = _gatePos;
        var stone = ToonEmissive(new Color(0.30f, 0.30f, 0.29f), 0.04f, 0.03f);
        var moss = ToonEmissive(new Color(0.17f, 0.40f, 0.20f), 0.16f, 0.04f);
        var wood = ToonEmissive(new Color(0.27f, 0.17f, 0.09f), 0.06f, 0.03f);
        var rng = new RandomNumberGenerator { Seed = (ulong)(Mathf.RoundToInt(_gatePos.X) * 40503 ^ Mathf.RoundToInt(_gatePos.Z) * 27271) };
        for (int i = 0; i < 15; i++)   // a low collapsed rubble mound where the well was
        {
            float a = rng.RandfRange(0, Mathf.Tau), r = rng.RandfRange(0f, 1.7f), s = rng.RandfRange(0.4f, 0.95f);
            var rock = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(s, s * 0.7f, s * rng.RandfRange(0.8f, 1.2f)) }, MaterialOverride = i % 4 == 0 ? moss : stone };
            rock.Position = new Vector3(Mathf.Cos(a) * r, s * 0.3f + rng.RandfRange(0f, 0.35f), Mathf.Sin(a) * r);
            rock.RotationDegrees = new Vector3(rng.RandfRange(-20, 20), rng.RandfRange(0, 360), rng.RandfRange(-20, 20));
            _wellNode.AddChild(rock);
        }
        var post = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.1f, BottomRadius = 0.13f, Height = 1.9f, RadialSegments = 6 }, MaterialOverride = wood };
        post.Position = new Vector3(1.2f, 0.6f, 0.2f); post.RotationDegrees = new Vector3(0, 0, 62); _wellNode.AddChild(post);   // a broken, leaning post
        _gateActive = false;   // the way down is gone
    }

    private void BroadcastGarden()
    {
        int n = _gPortals.Count;
        var ids = new int[n]; var pairs = new int[n]; var px = new float[n]; var py = new float[n]; var pz = new float[n];
        var lx = new float[n]; var ly = new float[n]; var lz = new float[n]; var kinds = new int[n]; var entr = new int[n]; var cols = new int[n];
        for (int i = 0; i < n; i++)
        {
            var p = _gPortals[i];
            ids[i] = p.NetId; pairs[i] = p.Pair;
            px[i] = p.GlobalPosition.X; py[i] = p.GlobalPosition.Y; pz[i] = p.GlobalPosition.Z;
            lx[i] = p.Link.X; ly[i] = p.Link.Y; lz[i] = p.Link.Z;
            kinds[i] = p.Kind; entr[i] = p.IsEntrance ? 1 : 0; cols[i] = PackCol(p.Tint);
        }
        NetMgr.BroadcastGarden(ids, pairs, px, py, pz, lx, ly, lz, kinds, entr, cols, _gatePos.X, _gatePos.Y, _gatePos.Z, 0);
    }

    public void ApplyGardenSync(int[] ids, int[] pairs, float[] px, float[] py, float[] pz, float[] lx, float[] ly, float[] lz, int[] kinds, int[] entr, int[] cols, float gx, float gy, float gz, int gcol)
    {
        ClearGardenPortals();
        _gardenSpawned = true;
        for (int i = 0; i < ids.Length; i++)
            AddGardenPortal(ids[i], pairs[i], new Vector3(px[i], py[i], pz[i]), new Vector3(lx[i], ly[i], lz[i]), UnpackCol(cols[i]), kinds[i], entr[i] == 1, remote: true);
        _gatePos = new Vector3(gx, gy, gz);
        BuildGate(_gatePos);
        _gateActive = true;
    }

    // ---- the maze RITUAL: find a hidden cauldron in 3 minutes for 300g each, then flee the flooding darkness veil ----
    private Node3D _ritualStatue;

    private Vector2I _ritualStatueCell = new Vector2I(-1, -1);   // the cauldron's cell (its own hidden nook, not a chamber)
    private MeshInstance3D _ritualBeam;   // the cauldron skybeam — only shown in the last minute of the search
    private bool RitualStatueValid => _maze != null && _ritualStatueCell.X >= 0 && _maze.In(_ritualStatueCell);
    private Vector3 RitualStatueWorld() => _maze != null ? _maze.CellCenter(_ritualStatueCell) : Vector3.Zero;

    // pick the cauldron's cell: FAR (corridor distance) from every player spawn, tucked into a dead-end/corner, and
    // never a chamber or a plaza. So it's a genuine hunt in a varied, hidden spot — never near where you're dropped in.
    private Vector2I PickRitualStatueCell()
    {
        var dist = Maze.DistField(_maze, _maze.Spawns);
        var dirs = new[] { new Vector2I(0, 1), new Vector2I(0, -1), new Vector2I(1, 0), new Vector2I(-1, 0) };
        Vector2I best = _maze.Spawns.Count > 0 ? _maze.Spawns[0] : new Vector2I(_maze.W / 2, _maze.H / 2);
        int bestScore = int.MinValue;
        for (int x = 0; x < _maze.W; x++)
            for (int y = 0; y < _maze.H; y++)
            {
                int d = dist[x, y];
                if (d < 0) continue;                                  // unreachable
                var c = new Vector2I(x, y);
                if (_maze.Chambers.Contains(c) || _maze.DecorCells.Contains(c)) continue;   // not on a chamber or a decor prop
                bool inPlaza = false;
                for (int p = 0; p < _maze.Plazas.Count; p++) { var pc = _maze.Plazas[p]; if (Mathf.Abs(pc.X - x) + Mathf.Abs(pc.Y - y) <= _maze.PlazaR[p] + 1) { inPlaza = true; break; } }
                if (inPlaza) continue;                                // not in a big open clearing (drab / too visible)
                int open = 0; foreach (var dd in dirs) { var n = c + dd; if (_maze.In(n) && !_maze.Blocked(c, n)) open++; }
                int score = d * 3 - open * 4;                         // far away, and prefer a tucked-away dead-end
                if (score > bestScore) { bestScore = score; best = c; }
            }
        return best;
    }

    // a distinct glowing cauldron idol in a hidden nook far from spawn — you have to explore to find it (not on the
    // minimap). Built on host + clients from the synced cell, so it looks the same to all.
    private void BuildRitualStatue()
    {
        if (_ritualStatue != null && GodotObject.IsInstanceValid(_ritualStatue)) { _ritualStatue.QueueFree(); _ritualStatue = null; }
        if (!RitualStatueValid) return;
        var pos = RitualStatueWorld();
        var root = new Node3D(); AddChild(root); root.GlobalPosition = pos; _ritualStatue = root;
        var iron = ToonEmissive(new Color(0.13f, 0.13f, 0.15f), 0.05f, 0.03f);   // dark cast-iron cauldron
        var stone = ToonEmissive(new Color(0.44f, 0.42f, 0.38f), 0.08f, 0.03f);  // carved stone plinth
        var brew = new Color(0.4f, 1f, 0.55f);                                    // glowing green brew
        // a carved stone plinth (this IS a statue — the cauldron sits enshrined on it)
        var plinth = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 1.25f, BottomRadius = 1.5f, Height = 1.1f, RadialSegments = 8 }, MaterialOverride = stone };
        plinth.Position = new Vector3(0, 0.55f, 0); root.AddChild(plinth);
        // three little iron legs
        for (int i = 0; i < 3; i++)
        {
            float a = i / 3f * Mathf.Tau + 0.5f;
            var leg = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.1f, BottomRadius = 0.15f, Height = 0.7f }, MaterialOverride = iron };
            leg.Position = new Vector3(Mathf.Cos(a) * 0.75f, 1.35f, Mathf.Sin(a) * 0.75f); root.AddChild(leg);
        }
        // the cauldron belly — a round pot (squashed sphere)
        var belly = new MeshInstance3D { Mesh = new SphereMesh { Radius = 1.35f, Height = 2.2f }, MaterialOverride = iron };
        belly.Position = new Vector3(0, 2.2f, 0); belly.Scale = new Vector3(1f, 0.82f, 1f); root.AddChild(belly);
        // the flared rim
        var rim = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = 1.05f, OuterRadius = 1.32f }, MaterialOverride = iron };
        rim.Position = new Vector3(0, 2.9f, 0); root.AddChild(rim);
        // the glowing brew surface + a rising bubble
        var surf = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 1.05f, BottomRadius = 1.05f, Height = 0.1f }, MaterialOverride = Emissive(brew, 3.4f) };
        surf.Position = new Vector3(0, 2.92f, 0); root.AddChild(surf);
        var bub = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.4f, Height = 0.8f }, MaterialOverride = Emissive(brew.Lerp(Colors.White, 0.35f), 3.8f) };
        bub.Position = new Vector3(0, 3.25f, 0); root.AddChild(bub);
        // a tall skybeam rising from the cauldron — hidden at first (you have to hunt), then revealed for the LAST MINUTE
        // as a mercy so nobody runs out the clock lost. Toggled in UpdateGardenRitual.
        var bm = Emissive(brew, 2.2f); bm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; bm.AlbedoColor = new Color(brew.R, brew.G, brew.B, 0.16f); bm.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        _ritualBeam = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.7f, BottomRadius = 1.1f, Height = 70f }, MaterialOverride = bm, Visible = false };
        _ritualBeam.Position = new Vector3(0, 35f, 0); root.AddChild(_ritualBeam);
        root.AddChild(new OmniLight3D { OmniRange = 9f, LightColor = brew, LightEnergy = 2.4f, Position = new Vector3(0, 3f, 0) });
    }
    public Vector3? RitualStatuePos => (_ritualActive && !_ritualDone && RitualStatueValid) ? RitualStatueWorld() : (Vector3?)null;

    // host/solo: arm the ritual just after EnterMaze (overrides the maze's default find-each-other / find-statue flow)
    private void SetupRitual()
    {
        if (_maze == null) return;
        _ritualActive = true; _ritualDone = false; _ritualWon = false; _ritualTimer = RitualDuration;
        _veilActive = false; _veilFront = 0f; _mazeFound = false; _mazeStatueTarget = -1;
        _ritualStatueCell = PickRitualStatueCell();   // a varied, hidden nook FAR from every spawn
        foreach (var oc in Chests) if (GodotObject.IsInstanceValid(oc)) oc.QueueFree(); Chests.Clear();   // grove chests don't belong in the maze
        int chests = 2 * WardenCount;   // normal reward chests hidden through the hedges
        for (int i = 0; i < chests; i++)
        {
            Vector2I cell; int tries = 0;
            do { cell = new Vector2I(_mazeRng.RandiRange(0, _maze.W - 1), _mazeRng.RandiRange(0, _maze.H - 1)); tries++; }
            while (tries < 12 && (_maze.Spawns.Exists(sp => Mathf.Abs(sp.X - cell.X) + Mathf.Abs(sp.Y - cell.Y) < 3) || cell == _ritualStatueCell));
            var c = new Chest { NetId = NextPickupId() };
            AddChild(c); c.GlobalPosition = _maze.CellCenter(cell); Chests.Add(c);
        }
        BuildRitualStatue();
        Hud?.Banner("THE RITUAL BEGINS — find the hidden cauldron!");
        if (NetMgr != null && NetMgr.Active && IsAuthority) NetMgr.BroadcastRitualStart(_ritualStatueCell.X, _ritualStatueCell.Y);
    }
    public void ApplyRitualStart(int cellX, int cellY)   // client mirror
    {
        _gardenRitual = true; _ritualActive = true; _ritualDone = false; _ritualWon = false; _ritualTimer = RitualDuration;
        _veilActive = false; _veilFront = 0f; _mazeFound = false; _mazeStatueTarget = -1; _ritualStatueCell = new Vector2I(cellX, cellY);
        BuildRitualStatue();
        Hud?.Banner("THE RITUAL BEGINS — find the hidden cauldron!");
    }

    // the statue was interacted (host resolves): pay everyone, then start the escape veil
    public void CompleteRitual()
    {
        if (!_ritualActive || _ritualDone) return;
        _ritualDone = true; _ritualWon = true;
        // NO gold yet — the 300 is paid only if you actually ESCAPE (reach the exit alive). See ExitMaze.
        StartVeilAndExit();
        if (NetMgr != null && NetMgr.Active && IsAuthority) NetMgr.BroadcastRitualEnd(1);
        Hud?.Banner("the cauldron is lit — FLEE the darkness to the exit!");
    }
    private void RitualTimeout()
    {
        if (_ritualDone) return;
        _ritualDone = true; _ritualWon = false;
        OpenExit();   // no reward, but open the way out
        if (NetMgr != null && NetMgr.Active && IsAuthority) NetMgr.BroadcastRitualEnd(0);
        Hud?.Banner("the ritual fades — no reward. find the exit.");
    }
    public void ApplyRitualEnd(int reason)   // client mirror (portal + fairy arrive via BroadcastMazeOpen)
    {
        if (_ritualDone) return;
        _ritualDone = true; _ritualWon = reason == 1;
        if (reason == 1)
        {
            _veilCenter = RitualStatueValid ? RitualStatueWorld() : Vector3.Zero;
            _veilActive = true; _veilFront = 0f; BuildVeil();
            Hud?.Banner("RITUAL COMPLETE — FLEE the darkness!");
        }
        else Hud?.Banner("the ritual fades — find the exit.");
    }

    private void StartVeilAndExit()
    {
        _veilCenter = RitualStatueValid ? RitualStatueWorld() : (Player != null ? Player.GlobalPosition : Vector3.Zero);
        _veilActive = true; _veilFront = 0f;
        BuildVeil();
        OpenExit();
    }
    private void OpenExit()   // host: open the exit portal + guide fairy, furthest from the veil origin
    {
        if (_maze == null) return;
        var statueCell = RitualStatueValid ? _ritualStatueCell : Maze.CellOf(_maze, Player != null ? Player.GlobalPosition : Vector3.Zero);
        var exitCell = Maze.PickPortal(_maze, new System.Collections.Generic.List<Vector2I> { statueCell });
        _mazeFound = true;
        SpawnPortal(exitCell, net: false);
        var fairyAt = Player != null ? new Vector3(Player.GlobalPosition.X, 0f, Player.GlobalPosition.Z) : _maze.CellCenter(statueCell);
        SpawnFairy(fairyAt, net: false);
        if (NetMgr != null && NetMgr.Active && IsAuthority) NetMgr.BroadcastMazeOpen(fairyAt, exitCell.X, exitCell.Y);
    }

    // an expanding glowing ring that washes out from a world point — used as the cauldron's triangulation "sonar"
    private void SpawnLocatorWave(Vector3 center, Color col, float maxR)
    {
        var mat = new StandardMaterial3D { AlbedoColor = new Color(col.R, col.G, col.B, 0.7f), EmissionEnabled = true, Emission = col, EmissionEnergyMultiplier = 2.2f, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
        var ring = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = 1.15f, OuterRadius = 1.5f, RingSegments = 8, Rings = 40 }, MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        ring.RotationDegrees = new Vector3(90, 0, 0);
        AddChild(ring); ring.GlobalPosition = new Vector3(center.X, 0.6f, center.Z); ring.Scale = Vector3.One * 0.08f;
        var tw = ring.CreateTween(); tw.SetParallel(true);
        tw.TweenProperty(ring, "scale", Vector3.One * (maxR / 1.5f), 2.0f).SetEase(Tween.EaseType.Out);
        tw.TweenProperty(mat, "albedo_color:a", 0f, 2.0f);
        tw.Chain().TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(ring)) ring.QueueFree(); }));
    }

    private void UpdateGardenRitual(float dt)
    {
        if (!_ritualActive) return;
        if (!_ritualDone)
        {
            _ritualTimer -= dt;
            if (IsAuthority && _ritualTimer <= 0f) RitualTimeout();
        }
        // reveal the cauldron skybeam in the final stretch (earlier per extra warden: 60 → 85 / 110 / 135s left)
        bool revealNow = !_ritualDone && _ritualTimer <= 60f + (WardenCount - 1) * 25f;
        if (revealNow && !_cauldronRevealed && RitualStatueValid)   // the reveal moment: one big circular wave + pin it on the minimap
        {
            _cauldronRevealed = true;
            SpawnLocatorWave(RitualStatueWorld(), DamageTypes.Col(DamageType.Curse).Lerp(Colors.White, 0.35f), 52f);
            Sfx?.Thunder();
            Hud?.Banner("the cauldron reveals itself — a skybeam marks the way!");
        }
        if (_ritualBeam != null && GodotObject.IsInstanceValid(_ritualBeam))
            _ritualBeam.Visible = _cauldronRevealed && !_ritualDone;
        if (_ritualActive && !_ritualDone && !_cauldronRevealed && RitualStatueValid)   // pre-reveal: a sonar pulse from its centre every 15s to triangulate by
        {
            _locatorT -= dt;
            if (_locatorT <= 0f) { _locatorT = 15f; SpawnLocatorWave(RitualStatueWorld(), DamageTypes.Col(DamageType.Curse), 34f); Sfx?.HexWeave(RitualStatueWorld()); }
        }
        if (_veilActive)
        {
            _veilFront = Mathf.Min(_veilMaxDist, _veilFront + (_veilMaxDist / VeilFillTime) * dt);   // flood advances one BFS ring at a time, deterministic on every machine
            _veilPhase += dt;
            UpdateVeilVisual();
            _whisperT -= dt;
            if (_whisperT <= 0f) { _whisperT = 1.5f; RepositionWhispers(); }   // the mist murmurs from wherever it's closest to you
            if (IsAuthority)   // host drives ALL veil damage (hits the host player + every ally in a flooded cell) — clients must not double-apply
            {
                _veilDmgT -= dt;
                if (_veilDmgT <= 0f) { _veilDmgT = 0.5f; VeilDamageFlooded(5f); }   // 10 dmg/s while standing in the dark
            }
        }
        if (IsAuthority && NetMgr != null && NetMgr.Active)
        {
            _ritualTickT -= dt;
            if (_ritualTickT <= 0f) { _ritualTickT = 0.4f; NetMgr.BroadcastRitualTick(_ritualTimer, _veilFront); }
        }
    }
    public void ApplyRitualTick(float timeLeft, float veilR)   // client HUD/veil sync
    {
        _ritualTimer = timeLeft;
        if (_veilActive) _veilFront = veilR;
    }

    // an animated fog material for the veil: soft round edges (alpha falls off toward each blob's silhouette so there
    // are no hard spheres) + a rolling, billowing vertex displacement so the mist churns like it's flowing. Per-instance
    // COLOR (set each frame) drives the flood fade-in. Shared static shader.
    private static Shader _veilShader;
    private const string VeilFogCode = @"
shader_type spatial;
render_mode blend_mix, cull_disabled, unshaded;
varying vec4 vc;
void vertex() {
    vc = INSTANCE_CUSTOM;
    vec3 wp = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    float t = TIME * 0.55;
    VERTEX.x += sin(wp.z * 0.25 + t) * 0.8 + cos(wp.y * 0.4 + t * 1.3) * 0.35;
    VERTEX.y += sin(wp.x * 0.3 + t * 0.8) * 0.55 + cos(wp.z * 0.35 + t) * 0.3;
    VERTEX.z += cos(wp.x * 0.22 + t * 1.1) * 0.8;
}
void fragment() {
    float e = clamp(dot(normalize(NORMAL), normalize(VIEW)), 0.0, 1.0);
    ALBEDO = vc.rgb;
    ALPHA = vc.a * pow(e, 1.4);
}";
    private static ShaderMaterial VeilFogMat() { _veilShader ??= new Shader { Code = VeilFogCode }; return new ShaderMaterial { Shader = _veilShader }; }

    // build the flood: BFS corridor distances from the statue cell, and a MultiMesh of dark mist boxes (one per
    // reachable cell) ordered nearest-first. The flood front reveals them in order → the dark spreads THROUGH the
    // corridors and is stopped by hedges (unreachable cells never flood). Deterministic → identical on every machine.
    private void BuildVeil()
    {
        if (_veilNode != null && GodotObject.IsInstanceValid(_veilNode)) _veilNode.QueueFree();
        _veilNode = new Node3D(); AddChild(_veilNode);
        _veilOrder.Clear(); _veilFront = 0f; _veilMaxDist = 1; _veilMM = null;
        if (_maze == null || !RitualStatueValid) return;
        _veilDist = Maze.DistField(_maze, _ritualStatueCell);
        for (int x = 0; x < _maze.W; x++)
            for (int y = 0; y < _maze.H; y++)
                if (_veilDist[x, y] >= 0) { _veilOrder.Add(new Vector2I(x, y)); if (_veilDist[x, y] > _veilMaxDist) _veilMaxDist = _veilDist[x, y]; }
        _veilOrder.Sort((a, b) => _veilDist[a.X, a.Y].CompareTo(_veilDist[b.X, b.Y]));   // nearest-to-statue first
        float cell = _maze.Cell;
        // big, jittered, OVERLAPPING soft blobs (radius ~0.75 cell → neighbours merge) instead of boxes → reads as fog,
        // not a grid. Per-instance alpha (set each frame) fades cells in at the front, so the flood creeps softly.
        var jr = new RandomNumberGenerator { Seed = 0xF0691234UL ^ (ulong)_veilOrder.Count };
        var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true, Mesh = new SphereMesh { Radius = cell * 0.92f, Height = cell * 1.84f }, InstanceCount = _veilOrder.Count };
        for (int i = 0; i < _veilOrder.Count; i++)
        {
            var c = _maze.CellCenter(_veilOrder[i]);
            float jx = jr.RandfRange(-cell * 0.32f, cell * 0.32f), jz = jr.RandfRange(-cell * 0.32f, cell * 0.32f), jy = jr.RandfRange(-0.4f, 1.6f), sc = jr.RandfRange(0.85f, 1.45f);
            var b = Basis.Identity.Scaled(new Vector3(sc, sc * 0.72f, sc));   // varied + heavily overlapping → billowing cloud, no grid
            mm.SetInstanceTransform(i, new Transform3D(b, new Vector3(c.X + jx, 2.8f + jy, c.Z + jz)));
            mm.SetInstanceCustomData(i, new Color(0.03f, 0.03f, 0.045f, 0f));   // rgb tint + alpha (fade), read by the fog shader
        }
        mm.VisibleInstanceCount = 0;
        _veilMM = new MultiMeshInstance3D { Multimesh = mm, MaterialOverride = VeilFogMat() };   // animated billowing fog shader (soft edges)
        _veilNode.AddChild(_veilMM);

        // whispers emanating from within the mist — a few detuned voices that reposition to nearby flooded cells
        _whispers.Clear();
        var wstream = Sfx.WhisperStream();   // one buffer shared by all voices; each starts at its own offset + pitch
        for (int i = 0; i < 3; i++)
        {
            var w = new AudioStreamPlayer3D { Stream = wstream, VolumeDb = -12f, MaxDistance = 34f, UnitSize = 6f, PitchScale = 0.82f + i * 0.13f, Autoplay = false };
            _veilNode.AddChild(w);
            w.GlobalPosition = _veilCenter + new Vector3(0, 2.2f, 0);
            w.Play(i * 1.3f);
            _whispers.Add(w);
        }
        _whisperT = 0f;
    }
    private void UpdateVeilVisual()
    {
        if (_veilMM == null || _veilMM.Multimesh == null) return;
        int vis = 0;
        for (int i = 0; i < _veilOrder.Count; i++) { if (_veilDist[_veilOrder[i].X, _veilOrder[i].Y] <= _veilFront) vis = i + 1; else break; }   // sorted → prefix
        _veilVis = vis;
        var mm = _veilMM.Multimesh;
        mm.VisibleInstanceCount = vis;
        // soft leading edge: a blob fades in over ~5 BFS rings behind the front (so there's no hard line), plus a gentle
        // per-cell shimmer so the fog looks alive rather than static.
        const float fadeWin = 5f;
        for (int i = 0; i < vis; i++)
        {
            var cv = _veilOrder[i];
            float fade = Mathf.Clamp((_veilFront - _veilDist[cv.X, cv.Y]) / fadeWin, 0f, 1f);
            float shimmer = 0.82f + 0.18f * Mathf.Sin(_veilPhase * 1.4f + cv.X * 1.7f + cv.Y * 2.3f);
            mm.SetInstanceCustomData(i, new Color(0.03f, 0.03f, 0.045f, Mathf.Clamp(fade * 0.95f * shimmer, 0f, 1f)));   // much denser now (the shader edge-softens it)
        }
    }
    // drift the whisper voices to flooded cells near the player so the dark around you murmurs
    private void RepositionWhispers()
    {
        if (_whispers.Count == 0 || _maze == null || Player == null || _veilVis <= 0) return;
        var pp = Player.GlobalPosition;
        foreach (var w in _whispers)
        {
            if (!GodotObject.IsInstanceValid(w)) continue;
            Vector3 spot = _veilCenter;
            for (int tryI = 0; tryI < 12; tryI++)
            {
                var c = _maze.CellCenter(_veilOrder[_rng.RandiRange(0, _veilVis - 1)]);
                if (new Vector2(c.X - pp.X, c.Z - pp.Z).Length() < 30f) { spot = new Vector3(c.X, 2.2f, c.Z); break; }
            }
            w.GlobalPosition = spot;
        }
    }
    private bool VeilFloodedAt(Vector3 pos)
    {
        if (_veilDist == null || _maze == null) return false;
        var c = Maze.CellOf(_maze, pos);
        int d = _veilDist[c.X, c.Y];
        return d >= 0 && d + 2f <= _veilFront;   // only where the fog has thickened (a couple rings behind the faint front)
    }
    private void VeilDamageFlooded(float dmg)   // host: hurt any warden standing in a flooded cell
    {
        if (Player != null && !Player.Downed && VeilFloodedAt(Player.GlobalPosition)) Player.Hurt(dmg);
        NetMgr?.DamageRemotesWhere(VeilFloodedAt, dmg);
    }
    // dying in the maze: you're revived and spat back OUT to the grove (the well then caves in). Only triggers when
    // you're ALONE in the maze, or when the WHOLE party is downed — otherwise you stay down for an ally to revive.
    public void MazeDeathExit()
    {
        if (!InMaze || Player == null) return;
        Player.ReviveMe(0.35f, false);   // revive so you're not stuck downed after the swap
        ExitMaze(escaped: false);         // no gold — you didn't make it out on your own
    }

    private void ClearRitual()
    {
        _ritualActive = false; _ritualDone = false; _ritualWon = false; _veilActive = false; _veilFront = 0f; _gardenRitual = false;
        if (_veilNode != null && GodotObject.IsInstanceValid(_veilNode)) { _veilNode.QueueFree(); _veilNode = null; }
        if (_ritualStatue != null && GodotObject.IsInstanceValid(_ritualStatue)) { _ritualStatue.QueueFree(); _ritualStatue = null; }
        _veilMM = null; _veilOrder.Clear(); _whispers.Clear(); _veilVis = 0; _ritualStatueCell = new Vector2I(-1, -1); _ritualBeam = null;
        _cauldronRevealed = false; _locatorT = 0f;   // (NEW) reset the reveal latch + sonar for the next maze
    }

    // ======================= SKY ISLANDS RITUAL (jungle) =======================
    // A ground whirlwind opens 5 waves into the jungle; ride it up into a floating-island platforming ritual: light
    // 3 effigies, reach the cauldron. Islands float ABOVE the live jungle (no world-swap) so falling drops you back.

    public void MaybeSpawnSkyWhirl()   // host: open the whirlwind once, 5 waves into the jungle
    {
        if (!IsAuthority || CurBiome != Biome.Rainforest || InMaze || InExpedition || InSky || _skySpawned || Player == null) return;
        if (BiomeWaves < 5) return;
        var pos = GroundedDrySpawn(Player.GlobalPosition, 26f, 60f);   // a clear spot near the party
        ShowSkyWhirl(pos);
        NetMgr?.BroadcastSkyWhirl(pos);
        Hud?.Banner("a roaring updraft tears open the canopy — ride it into the sky");
        Sfx?.Release(DamageType.Wind); Sfx?.Thunder();
    }

    public void ShowSkyWhirl(Vector3 pos)   // build the ground whirlwind interactable (host + clients)
    {
        _skySpawned = true; _skyWhirlActive = true;
        float gy = SurfaceHeight(pos, pos.Y);
        _skyWhirlPos = new Vector3(pos.X, gy, pos.Z);
        if (_skyWhirl != null && GodotObject.IsInstanceValid(_skyWhirl)) _skyWhirl.QueueFree();
        _skyWhirl = new Node3D { Position = _skyWhirlPos };
        AddChild(_skyWhirl);
        var col = DamageTypes.Col(DamageType.Wind);
        var spin = new Node3D(); _skyWhirl.AddChild(spin);
        var mat = ToonEmissive(col, 1.4f, 0f);
        if (mat is StandardMaterial3D sm) { sm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; sm.AlbedoColor = new Color(col.R, col.G, col.B, 0.26f); sm.CullMode = BaseMaterial3D.CullModeEnum.Disabled; }
        for (int i = 0; i < 16; i++)
        {
            float t = i / 15f, y = 0.3f + t * 7f, rr = Mathf.Lerp(0.5f, 2.6f, t), a = t * Mathf.Pi * 4f;
            var sheet = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(Mathf.Lerp(0.4f, 1.6f, t), 1.2f, 0.05f) }, MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            sheet.Position = new Vector3(Mathf.Cos(a) * rr, y, Mathf.Sin(a) * rr);
            sheet.Rotation = new Vector3(0, a + Mathf.Pi * 0.5f + 0.3f, 0);
            spin.AddChild(sheet);
        }
        _skyWhirl.AddChild(new OmniLight3D { Position = new Vector3(0, 2.5f, 0), OmniRange = 8f, LightColor = col, LightEnergy = 2f });
        var tw = spin.CreateTween().SetLoops();
        tw.TweenProperty(spin, "rotation_degrees:y", 360f, 1.1);
    }

    public void EnterSky()   // host entry (or re-entry routing)
    {
        if (InSky) { if (Player != null && _sky != null) Player.TeleportReset(_sky.Entry); return; }   // re-ride: hop back up
        if (!_skyWhirlActive) return;
        ulong seed = (ulong)GD.Randi() ^ ((ulong)GD.Randi() << 32);
        NetMgr?.BroadcastEnterSky(seed, _skyWhirlPos);
        EnterSkyRealm(seed, _skyWhirlPos);
    }

    public void EnterSkyRealm(ulong seed, Vector3 origin)   // build the island cluster + go up (all peers)
    {
        if (InSky) return;
        float gy = SurfaceHeight(origin, origin.Y);
        _sky = SkyIslands.Build(seed, WardenCount, new Vector3(origin.X, gy, origin.Z));
        var rz = SkyIslands.Realize(this, _sky, seed);
        _skyRoot = rz.Root; _skyEffigyNodes = rz.EffigyNodes; _skyCauldronNode = rz.CauldronNode; _skyCauldronBeam = rz.CauldronBeam;
        _world?.MarkBlockersDirty();
        _world?.Update(Player != null ? Player.GlobalPosition : Vector3.Zero);   // flush island decks/vines into the live lists NOW so teleport-landing + enemy grounding work this frame
        InSky = true; _skyDone = false; _skyWon = false; _skyCauldronArmed = false;
        _skyEffigyLit.Clear(); for (int i = 0; i < _sky.Effigies.Count; i++) _skyEffigyLit.Add(false);
        _skyElapsed = 0f; _skySpawnT = 3f; _skyFallT = 0f; Heat = 1f;
        Enemies.Clear(); _toSpawn.Clear();   // no leftover jungle mobs bleed into the sky
        Player?.TeleportReset(_sky.Entry);
        foreach (var cp in _sky.Chests) { var ch = new Chest { SpecialGold = 150 }; AddChild(ch); ch.GlobalPosition = cp; Chests.Add(ch); }
        if (IsAuthority)   // scatter zombies on the islands — already HUNTING so they close in + attack when you land
            foreach (var isle in _sky.Isles)
                if (isle.Role != 1 && GD.Randf() < 0.65f)
                {
                    int n = 1 + (isle.Radius > 5.5f ? 1 : 0);
                    for (int k = 0; k < n; k++)
                    {
                        var zp = new Vector3(isle.Center.X + (float)GD.RandRange(-isle.Radius * 0.5, isle.Radius * 0.5), isle.Center.Y + 0.6f, isle.Center.Z + (float)GD.RandRange(-isle.Radius * 0.5, isle.Radius * 0.5));
                        SpawnEnemyAtExact("swarmer", zp);
                        if (Enemies.Count > 0) Enemies[Enemies.Count - 1].WakeSilent();   // (FIX) they were idle & never attacked — wake them to hunt
                    }
                }
        Hud?.Banner("SKY RITUAL — light the 3 effigies, then reach the cauldron");
        Sfx?.Thunder();
    }

    private void TickSky(float dt)
    {
        if (!InSky || _sky == null) return;
        _skyElapsed += dt;
        Heat = Mathf.Clamp(1f + Mathf.Min(_skyElapsed, 120f) * 0.012f, 1f, 2.5f);   // heat ramps like the maze search
        if (!_skyCauldronArmed && SkyEffigiesLit >= _sky.Effigies.Count && _sky.Effigies.Count > 0) ArmSkyCauldron();

        if (IsAuthority)   // director: flyers/divers/bats swarm the islands
        {
            _skySpawnT -= dt;
            if (_skySpawnT <= 0f)
            {
                _skySpawnT = Mathf.Lerp(4.0f, 1.5f, Mathf.Clamp((Heat - 1f) / 1.5f, 0f, 1f));
                int cap = 6 * WardenCount + 4;
                if (Enemies.Count < cap)
                {
                    int n = 1 + (int)(WardenCount * (Heat - 1f));
                    var kinds = new[] { "flyer", "diver", "bat" };
                    for (int i = 0; i < n; i++)
                    {
                        var isle = _sky.Isles[GD.RandRange(0, _sky.Isles.Count - 1)];
                        var p = new Vector3(isle.Center.X + (float)GD.RandRange(-7.0, 7.0), isle.Center.Y + 6f, isle.Center.Z + (float)GD.RandRange(-7.0, 7.0));
                        SpawnEnemyAtExact(kinds[GD.RandRange(0, kinds.Length - 1)], p);
                    }
                }
            }
        }

        // cauldron reached (armed) → complete
        if (_skyCauldronArmed && !_skyDone && Player != null && new Vector2(Player.GlobalPosition.X - _sky.Cauldron.X, Player.GlobalPosition.Z - _sky.Cauldron.Z).Length() < 3.5f)
        {
            if (IsAuthority) CompleteSky(); else NetMgr?.RequestSkyComplete();
        }

        _skyFallT -= dt;
        if (_skyFallT <= 0f) { _skyFallT = 0.35f; CheckSkyFalls(); }
    }

    private void ArmSkyCauldron()
    {
        _skyCauldronArmed = true;
        if (_skyCauldronBeam != null && GodotObject.IsInstanceValid(_skyCauldronBeam)) _skyCauldronBeam.Visible = true;
        Hud?.Banner("the effigies blaze — the cauldron awakens! reach it");
        Sfx?.Thunder();
    }

    public void LightSkyEffigy(int idx)
    {
        if (idx < 0 || idx >= _skyEffigyLit.Count || _skyEffigyLit[idx]) return;
        _skyEffigyLit[idx] = true;
        if (idx < _skyEffigyNodes.Count && GodotObject.IsInstanceValid(_skyEffigyNodes[idx]))
        {
            foreach (var ch in _skyEffigyNodes[idx].GetChildren())
                if (ch is MeshInstance3D mi && mi.MaterialOverride is StandardMaterial3D m) { m.EmissionEnergyMultiplier = 3.5f; m.Emission = new Color(0.5f, 1f, 0.6f); }
            VfxRing(_skyEffigyNodes[idx].GlobalPosition + Vector3.Up, new Color(0.5f, 1f, 0.6f), 3f, 0.6f);
        }
        Sfx?.CurseCrush(_sky != null && idx < _sky.Effigies.Count ? _sky.Effigies[idx] : (Player?.GlobalPosition ?? Vector3.Zero));
        Hud?.Banner($"effigy lit  ·  {SkyEffigiesLit}/{_sky?.Effigies.Count ?? 3}");
        if (IsAuthority) NetMgr?.BroadcastSkyEffigy(idx);
    }

    public void CompleteSky()
    {
        if (_skyDone) return;
        _skyDone = true; _skyWon = true;
        NetMgr?.BroadcastExitSky(true);
        ExitSky(true);
    }

    // per-frame: has everyone fallen out (below the island tier) or gone down? → the ritual ends
    private void CheckSkyFalls()
    {
        if (_sky == null || Player == null) return;
        float floor = _sky.BaseY - 58f;   // below the hanging vines' reach — only THEN are you out (you had the whole vine-gap to catch one and ride back up)
        bool localOut = Player.GlobalPosition.Y < floor || Player.Downed;
        if (!(NetMgr != null && NetMgr.Active))   // solo: you ARE the team
        {
            if (localOut && InSky) { if (Player.Downed) Player.ReviveMe(0.4f, false); ExitSky(false); }
            return;
        }
        if (!IsAuthority) return;   // host evaluates the whole party
        bool allOut = localOut;
        foreach (var av in NetMgr.AllyAvatars()) if (GodotObject.IsInstanceValid(av)) allOut &= (av.GlobalPosition.Y < floor || av.Downed);
        if (allOut && InSky) { NetMgr?.BroadcastExitSky(false); ExitSky(false); }
    }

    public void ExitSky(bool won)   // teardown (all peers) — mirrors ExitMaze but keeps the live jungle underneath
    {
        if (!InSky) return;
        InSky = false;
        if (_skyRoot != null && GodotObject.IsInstanceValid(_skyRoot)) _skyRoot.QueueFree();
        _skyRoot = null; _skyEffigyNodes = new(); _skyCauldronNode = null; _skyCauldronBeam = null;
        PersistentDecks.RemoveAll(d => d.Floating); PersistentVines.Clear();   // clear only the SKY (floating) decks — overworld pedestal decks/ramps persist
        _world?.MarkBlockersDirty();
        _skyEffigyLit.Clear(); _skyCauldronArmed = false;
        foreach (var e in Enemies.ToArray()) if (GodotObject.IsInstanceValid(e)) e.QueueFree(); Enemies.Clear(); _toSpawn.Clear();
        foreach (var c in Chests.ToArray()) if (GodotObject.IsInstanceValid(c)) c.QueueFree(); Chests.Clear();
        if (_skyWhirl != null && GodotObject.IsInstanceValid(_skyWhirl)) _skyWhirl.QueueFree();
        _skyWhirl = null; _skyWhirlActive = false;
        if (Player != null)
        {
            var gy = SurfaceHeight(_skyWhirlPos, 80f);
            Player.TeleportReset(new Vector3(_skyWhirlPos.X, gy + 1.5f, _skyWhirlPos.Z));
            if (Player.Downed) Player.ReviveMe(0.4f, false);
        }
        _waveGap = 20f; Heat = 1f;
        if (won) { RewardGoldAll(300); Hud?.Banner("THE SKY RITUAL IS COMPLETE — 300 gold!"); }
        else Hud?.Banner("the islands crumble and the updraft dies");
        _sky = null;
    }

    public void SkyPlayerDown()   // dying in the sky = falling out (revived), never a game-over
    {
        if (!InSky || Player == null) return;
        Player.ReviveMe(0.4f, false);
        if (!(NetMgr != null && NetMgr.Active)) ExitSky(false);   // solo: you're the whole team → ends now (MP ends via CheckSkyFalls when all are out)
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
        m.EmissionEnergyMultiplier = 1.0f + energy * 1.2f;   // (PAINTERLY) controlled emission — eased down from 1.6 to stop the blown-out bloom
        m.Roughness = 0.85f;
        m.DiffuseMode = BaseMaterial3D.DiffuseModeEnum.Toon;
        m.SpecularMode = BaseMaterial3D.SpecularModeEnum.Toon;
        m.RimEnabled = true; m.Rim = 0.35f; m.RimTint = 0.4f;   // (PHASE 3) soft painterly edge glow instead of a dead-flat uniform emissive
        return m;
    }

    // (PHASE 3) radiate cone SPIKES out of a spherical body — turns a bare glowing orb into a menacing sea-mine / spiked
    // hazard casing. Spikes alternate into upper/lower bands around the equator and point straight out from the centre.
    public static void AddSpikes(Node3D parent, Material mat, float coreR, float spikeLen, int count)
    {
        for (int i = 0; i < count; i++)
        {
            float a = i / (float)count * Mathf.Tau;
            float el = (i % 2 == 0) ? 0.5f : -0.32f;   // alternate an upper and a lower ring of spikes
            var dir = new Vector3(Mathf.Cos(a) * Mathf.Cos(el), Mathf.Sin(el), Mathf.Sin(a) * Mathf.Cos(el)).Normalized();
            var pivot = new Node3D();
            Vector3 x = dir.Cross(Vector3.Forward); if (x.LengthSquared() < 0.001f) x = dir.Cross(Vector3.Right); x = x.Normalized();
            Vector3 z = x.Cross(dir).Normalized();
            pivot.Basis = new Basis(x, dir, z);   // local +Y now points along dir
            parent.AddChild(pivot);
            var spike = new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = coreR * 0.3f, Height = spikeLen, RadialSegments = 5 },
                Position = new Vector3(0, coreR + spikeLen * 0.5f, 0),   // base at the surface, point outward
                MaterialOverride = mat,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
            };
            pivot.AddChild(spike);
        }
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

    // (NEW) Soul Reap's cursed scythe: a glowing crescent arc that sweeps in front of the caster and fades. Position-based so
    // both the local cast and the networked ally copy (VFX kind 63) can spawn it.
    public void SpawnScytheVfx(Vector3 pos, Vector3 dir, float radius, Color col)
    {
        dir.Y = 0f; dir = dir.LengthSquared() > 0.001f ? dir.Normalized() : Vector3.Forward;
        var arc = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = radius * 0.6f, OuterRadius = radius * 0.82f } };
        var mm = Emissive(col, 3.2f); mm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; arc.MaterialOverride = mm;
        AddChild(arc);
        arc.GlobalPosition = new Vector3(pos.X, SurfaceHeight(pos, 1e9f) + 1.1f, pos.Z);
        arc.RotationDegrees = new Vector3(78f, Mathf.RadToDeg(Mathf.Atan2(dir.X, dir.Z)), 0f);
        var tw = arc.CreateTween(); tw.SetParallel(true);
        tw.TweenProperty(arc, "rotation:y", arc.Rotation.Y + Mathf.Pi * 1.1f, 0.4f);
        tw.TweenProperty(arc, "scale", Vector3.One * 1.35f, 0.4f);
        tw.TweenProperty(mm, "albedo_color", new Color(col.R, col.G, col.B, 0f), 0.42f);
        tw.Chain().TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(arc)) arc.QueueFree(); }));
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
    // a Faith Shield cast by ANOTHER player — the host makes it authoritative (blocks + shatters host-owned enemies), a
    // client makes it visual-only. Not stored in Game.Shield (that's the LOCAL player's own shield).
    public void SpawnRemoteFaithShield(Vector3 pos, float radius, float dur, float burstDmg, float knock, bool reflect)
    {
        var sh = new FaithShield
        {
            Radius = radius, Dur = dur, DurMax = dur, BurstDmg = burstDmg, BurstRadius = radius + 3f, Knock = knock,
            Reflect = reflect, MeleeDmg = 6f, HealPerSec = (Player != null ? Player.S.MaxHp : 100f) * 0.05f,
            Remote = !IsAuthority,
        };
        AddChild(sh); sh.GlobalPosition = new Vector3(pos.X, 0.1f, pos.Z);
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
        m.EmissionEnergyMultiplier = 1.0f + energy * 1.2f;   // (PAINTERLY) controlled emission — eased from 1.6
        return m;
    }

    // (REWORK) Model-SHAPED x-ray ghost so allies & friendly minions read through walls/crowds — NOT a fat capsule.
    // Clones each of the entity's meshes as a translucent, always-on-top emissive overlay parented to the real mesh, so it
    // rides the animation and traces the actual character/ent silhouette. Returns the shared material (recolor via it).
    // ---- dynamic-light budget (perf) ----------------------------------------------------------------
    // Every enemy carries a real-time OmniLight; in a big MP fight that alone can be 30-40 lights, before bolts
    // and effects pile on. GPU cost scales hard with concurrent lights. So: only the NEAREST few enemy lights stay
    // on (the rest still glow via emissive materials), and transient projectile lights draw from a shared cap.
    private float _lightCullT = 0f;
    private readonly System.Collections.Generic.List<(float d, Enemy e)> _lightSort = new();
    private void CullEnemyLights()
    {
        if (Player == null || !SimActive) return;
        int budget = GfxQuality == 0 ? 4 : GfxQuality == 1 ? 8 : 14;
        var pc = Player.GlobalPosition;
        _lightSort.Clear();
        foreach (var e in Enemies)
            if (e != null && !e.Dead && GodotObject.IsInstanceValid(e)) _lightSort.Add((e.GlobalPosition.DistanceSquaredTo(pc), e));
        if (_lightSort.Count > budget) _lightSort.Sort((a, b) => a.d.CompareTo(b.d));
        for (int i = 0; i < _lightSort.Count; i++) _lightSort[i].e.SetLightOn(i < budget);
    }

    private readonly System.Collections.Generic.List<(float d, Wisp w)> _wispSort = new();
    // perf: decorative will-o'-wisps scatter dozens of always-on OmniLights across the forest. Only a handful can
    // ever be near enough to matter, so keep the nearest N lit (by quality) and switch the rest's light off — the
    // glowing mote itself stays drawn, so the scene looks identical; we just stop feeding far lights to the clusterer.
    private void CullWispLights()
    {
        if (Player == null || !SimActive) return;
        int budget = GfxQuality == 0 ? 6 : GfxQuality == 1 ? 12 : 18;
        var pc = Player.GlobalPosition;
        _wispSort.Clear();
        foreach (var w in Wisp.All)
            if (w != null && GodotObject.IsInstanceValid(w)) _wispSort.Add((w.LightPos.DistanceSquaredTo(pc), w));
        if (_wispSort.Count > budget) _wispSort.Sort((a, b) => a.d.CompareTo(b.d));
        for (int i = 0; i < _wispSort.Count; i++) _wispSort[i].w.SetLit(i < budget);
    }

    private readonly System.Collections.Generic.List<(float d, Orb o)> _orbSort = new();
    // perf: a farmed swarm hoards up to 150 XP orbs, each an always-on OmniLight — the dominant fps drain. Keep only the
    // nearest N lit (by quality); the emissive box still glows so the scene looks identical. Mirrors CullWispLights.
    private void CullOrbLights()
    {
        if (Player == null || !SimActive) return;
        int budget = GfxQuality == 0 ? 6 : GfxQuality == 1 ? 12 : 18;
        var pc = Player.GlobalPosition;
        _orbSort.Clear();
        foreach (var o in Orbs)
            if (o != null && GodotObject.IsInstanceValid(o)) _orbSort.Add((o.GlobalPosition.DistanceSquaredTo(pc), o));
        if (_orbSort.Count > budget) _orbSort.Sort((a, b) => a.d.CompareTo(b.d));
        for (int i = 0; i < _orbSort.Count; i++) _orbSort[i].o.SetLit(i < budget);
    }

    // shared cap for TRANSIENT projectile/effect lights (player + enemy bolts). Callers add a light only when the
    // pool has room and decrement it in _ExitTree. Bounded so a bolt-storm can't spawn 60 lights at once.
    private static int s_dynLights = 0;
    public const int DynLightCap = 26;
    public static bool DynLightRoom => s_dynLights < DynLightCap;
    public static void DynLightAdd() => s_dynLights++;
    public static void DynLightRemove() { if (s_dynLights > 0) s_dynLights--; }

    public static StandardMaterial3D SilhouetteMat(Color col) => new StandardMaterial3D
    {
        AlbedoColor = new Color(col.R, col.G, col.B, 0.3f),
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        EmissionEnabled = true, Emission = col, EmissionEnergyMultiplier = 1.4f,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        NoDepthTest = true, RenderPriority = 8   // always drawn on top
    };
    public static void AddModelSilhouette(Node3D model, Material mat)
    {
        if (model == null) return;
        var meshes = new System.Collections.Generic.List<MeshInstance3D>();
        CollectMeshInstances(model, meshes);
        foreach (var mi in meshes)
            mi.AddChild(new MeshInstance3D { Mesh = mi.Mesh, MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
    }
    private static void CollectMeshInstances(Node node, System.Collections.Generic.List<MeshInstance3D> outList)
    {
        foreach (var c in node.GetChildren())
        {
            if (c is MeshInstance3D mi && mi.Mesh != null) outList.Add(mi);
            CollectMeshInstances(c, outList);
        }
    }
    // (NEW) count non-boss enemies in a radius — used to tally "enemies flung" for the end-of-run stats
    public int CountFlungNear(Vector3 center, float radius)
    {
        int n = 0;
        foreach (var e in Enemies)
            if (e != null && !e.Dead && GodotObject.IsInstanceValid(e) && !e.IsBoss && new Vector2(e.GlobalPosition.X - center.X, e.GlobalPosition.Z - center.Z).Length() < radius + e.Radius) n++;
        return n;
    }
    public static StandardMaterial3D AddFriendlySilhouette(Node3D parent, Color col, float radius = 0.45f, float height = 1.6f, float yOff = 0.9f)
    {
        var mat = SilhouetteMat(col);
        AddModelSilhouette(parent, mat);
        return mat;
    }

    // ---- waves ----
    // The enemy director: after each wave it judges how the party coped (clear speed, lowest health, downs,
    // over-leveling) and nudges Heat. Heat then drives spawn density, elite/affix odds, enemy HP/damage, and
    // composition in NextWave/SpawnEnemy. Bounded [0.85, 1.6] so it never trivializes or hard-walls the run.
    private void AssessDirector()
    {
        // (REWORK) PRIMARY signal = SURVIVAL MARGIN, not clear time. How low the party's HP dipped + downs directly measures
        // "did they have this under control", and it's naturally player-count / wave-size AGNOSTIC — a party that never dropped
        // below 78% crushed the wave whether it took 15s solo or 50s as a 4-stack. Absolute time (old metric) unfairly read
        // MP's bigger, slower waves as "struggling". Clear time is kept only as a MINOR nudge, normalized by party size.
        float minHp = _waveMinHpFrac;   // lowest party HP frac this wave (host + allies)
        bool downs = _downThisWave;
        float step;
        if (downs || minHp < 0.30f) step = -0.12f;            // someone fell / HP cratered — back off hard
        else if (minHp > 0.78f) step = 0.09f;                 // barely scratched — they're cruising, ramp up
        else if (minHp > 0.58f) step = 0.05f;                 // comfortable margin — drift up
        else if (minHp < 0.45f) step = -0.05f;                // took a real beating (no down) — ease a touch
        else step = 0f;                                       // about right — hold

        // secondary (minor): clear pace vs a par that scales with wave depth AND party size, so MP isn't penalized for its bigger waves
        float par = (13f + Wave * 2.2f) * (1f + 0.5f * (WardenCount - 1));   // solo par; ×1.5 (2p) … ×2.5 (4p), mirroring the body scaling
        float pace = par / Mathf.Max(_waveTimer, 1f);         // >1 = faster than expected for this wave's size/party
        if (!downs)
        {
            if (pace > 1.5f) step += 0.03f;                   // blew through it even for its size
            else if (pace < 0.55f) step -= 0.03f;             // genuinely dragging (slow relative to size)
        }
        if (Player != null && Player.Level > Wave + 4) step += 0.03f;   // over-leveled relative to depth

        float old = Heat;
        Heat = Mathf.Clamp(Heat + step, 0.85f, 1.6f);
        if (Heat - old > 0.07f) Hud?.Banner("the threat rises");    // (NEW) biome-agnostic
        else if (old - Heat > 0.07f) Hud?.Banner("the threat eases");
        _waveMinHpFrac = 1f; _downThisWave = false;           // reset for the next wave
    }

    public void DevForceWave(int w)   // (NEW) dev: jump the wave director to wave `w` and spawn its roster + miniboss/boss now
    {
        Wave = Mathf.Max(0, w - 1);
        if (BiomeStartWave > Wave) BiomeStartWave = Wave;
        NextWave();
    }

    private void NextWave()
    {
        Wave++;
        // named wave mutator: a hot streak (high Heat) can turn a normal, non-boss wave into a Blood Moon / Eclipse / Surge
        var clearedMutator = _endedMutator;   // the mutator on the wave that just ended (ActiveMutator was already cleared at intermission)
        _endedMutator = WaveMutator.None;
        ActiveMutator = WaveMutator.None;
        if (IsAuthority && clearedMutator != WaveMutator.None)   // survived a mutator wave → every warden gets a pick-3 with a guaranteed legendary
        {
            GrantMutatorRewardLocal();
            NetMgr?.BroadcastMutatorReward();
        }
        // (TUNED) lowered the bar so mutators actually show up: Heat > 1.05 (was 1.22 — rarely reached) and 55% roll (was 35%),
        // and eligible from wave 3 (was 4). Chance also scales up a touch with Heat so a real hot streak nearly guarantees one.
        if (IsAuthority && Wave >= 3 && Wave % 5 != 0 && Heat > 1.05f && _rng.Randf() < 0.55f + (Heat - 1.05f) * 0.5f)
            ActiveMutator = (WaveMutator)_rng.RandiRange(1, 5);
        ShopSpawnCheck();                      // peddler: maybe appear / warn it's leaving / pack up (wave-driven)
        if (Wave % 10 == 0) SpawnRoulette();   // ~1 wheel of fortune every 10 waves (capped at 3, spaced)
        if (Player != null && Player.DivineWitch && Wave > 1 && Wave % 10 == 1)
            Player.Interventions = Mathf.Min(2, Player.Interventions + 1);   // refreshes each 10-wave cycle
        var list = new List<string>();
        float cm = (1f + 0.55f * (WardenCount - 1)) * Heat;   // bodies per warden, amplified by the director's Heat
        if (ActiveMutator == WaveMutator.Surge) cm *= 1.7f;   // Surge: a dense rush of fast trash
        void add(string t, int n) { int c = Mathf.Max(n > 0 ? 1 : 0, Mathf.RoundToInt(n * cm)); for (int i = 0; i < c; i++) list.Add(t); }

        if (CurBiome == Biome.Rainforest)
        {
            // (NEW) Rainforest roster: pigmy fodder + darts, snakes, bats, trolls, ptero stunners, croc bombers, + zombies (taker still special-spawns)
            add("swarmer", 4 + Mathf.FloorToInt(Wave * 1.4f));   // zombies still shamble through the jungle
            add("pigmy", 6 + Mathf.FloorToInt(Wave * 2.0f));
            add("pigmydart", 3 + Mathf.FloorToInt(Wave * 1.0f));
            add("snake", Mathf.FloorToInt(Wave * 0.85f));
            if (Wave >= 2) add("bat", Mathf.FloorToInt((Wave - 1) * 0.7f));
            if (Wave >= 2) add("jtroll", Mathf.FloorToInt((Wave - 1) * 0.45f));
            if (Wave >= 3) add("ptero", Mathf.Min(5, Mathf.FloorToInt((Wave - 2) * 0.5f)));
            if (Wave >= 4) add("croc", Mathf.Min(5, Mathf.FloorToInt((Wave - 3) * 0.5f)));
            if (Heat > 1.12f && Wave >= 2) { add("pigmydart", 1); add("bat", 1); }
            if (Heat > 1.30f && Wave >= 4) add("ptero", 1);
            if (ActiveMutator == WaveMutator.Volatile) add("croc", 3 + Wave / 4);
        }
        else
        {
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
            if (ActiveMutator == WaveMutator.Volatile) add("bomber", 4 + Wave / 3);   // Volatile: extra powderkegs on top of the everyone-explodes rule
        }

        var rng = new RandomNumberGenerator(); rng.Randomize();
        for (int i = list.Count - 1; i > 0; i--) { int j = rng.RandiRange(0, i); (list[i], list[j]) = (list[j], list[i]); }
        foreach (var t in list) _toSpawn.Enqueue(t);   // (BOSS-LAIR) every wave enqueues normally now — the world boss is a LAIR you challenge when ready, not a wave-10 gate

        if (Wave % 5 == 0) SpawnEnemy("miniboss");   // mini-boss still spikes every 5th wave; the full boss is the lair

        // rare loot goblin — the Blood Moon draws them out (its "more loot": goblins drop for whoever downs them, so it's fair in MP)
        if (Goblin == null && rng.Randf() < (ActiveMutator == WaveMutator.BloodMoon ? 0.45f : 0.14f)) SpawnGoblin();

        // (rituals no longer spawn per-wave — they ALL spawn at map start now, 5 per warden, spread across the bounded world; see SpawnAllRituals)

        if (ActiveMutator != WaveMutator.None) MutatorBanner();
        else Hud?.Banner(Wave % 10 == 0 ? "THE HOLLOW MOON" : $"Wave {Wave}");
    }
    private void MutatorBanner()
    {
        switch (ActiveMutator)
        {
            case WaveMutator.BloodMoon: Hud?.Banner("BLOOD MOON — the horde runs red and fast"); break;
            case WaveMutator.Eclipse:   Hud?.Banner("ECLIPSE — darkness swallows the grove"); break;
            case WaveMutator.Surge:     Hud?.Banner("SURGE — they come in a flood"); break;
            case WaveMutator.Moonfall:  Hud?.Banner("MOONFALL — the sky is falling, keep moving!"); break;
            case WaveMutator.Volatile:  Hud?.Banner("VOLATILE — the dead burst; mind the blasts"); break;
        }
    }

    // spawn an enemy at a specific spot (splitter children) — host only; children sync via the normal snapshot
    public bool NoSpawn = false;   // (DEV) inspect mode — freeze the whole spawn stream
    // (DEV) clear the field of enemies now (skips Die/drops — purely to empty the map for inspection)
    public void ClearEnemies()
    {
        foreach (var e in Enemies.ToArray()) if (e != null && GodotObject.IsInstanceValid(e)) e.QueueFree();
        Enemies.Clear();
    }

    public void SpawnEnemyAt(string type, Vector3 pos)
    {
        if (NoSpawn) return;
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
        if (NoSpawn) return;
        var e = new Enemy();
        e.Configure(type, Wave);
        AddChild(e);
        e.NetId = _netEnemySeq++;
        e.TypeIdx = EnemyKinds.Index(type);
        e.GlobalPosition = new Vector3(pos.X, Mathf.Max(pos.Y, e.Radius), pos.Z);
        Enemies.Add(e);
        SpawnPoof(e.GlobalPosition);
    }

    // (DEV harness) spawn one enemy with an optional affix/elite, at an exact spot, returned. Sets the flags BEFORE AddChild so
    // the affix aura / elite ring build correctly in _Ready — same ordering as the maze spawn.
    public Enemy SpawnEnemyForTest(string type, Vector3 pos, int affix = 0, bool elite = false)
    {
        var e = new Enemy();
        e.Configure(type, Wave);
        if (elite) e.MakeElite(); else if (affix > 0) e.MakeAffix(affix);
        AddChild(e);
        e.NetId = _netEnemySeq++;
        e.TypeIdx = EnemyKinds.Index(type);
        e.GlobalPosition = new Vector3(pos.X, Mathf.Max(pos.Y, e.Radius), pos.Z);
        Enemies.Add(e);
        return e;
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

    // (MP) a random warden to spawn/anchor around — host player or any connected ally, chosen evenly so no one player
    // hogs the whole horde. `local` = we picked the host's own player (so we can bias the spawn by its tracked heading).
    private Vector3 SpawnAnchor(RandomNumberGenerator rng, out bool local)
    {
        local = true;
        Vector3 hostPos = Player != null ? Player.GlobalPosition : Vector3.Zero;
        if (NetMgr == null || !NetMgr.Active) return hostPos;
        var allies = NetMgr.AllyPositions();
        if (allies.Count == 0) return hostPos;
        int pick = rng.RandiRange(0, allies.Count);   // 0 = host, 1..N = an ally
        if (pick == 0) return hostPos;
        local = false;
        return allies[pick - 1];
    }
    // is ANY warden standing in the Haunt? (host-side director intensity + rim spawns — not just the local player)
    public bool AnyWardenInHaunt
    {
        get
        {
            if (!HauntActive) return false;
            if (Player != null && InsideHaunt(Player.GlobalPosition)) return true;
            if (NetMgr != null && NetMgr.Active)
                foreach (var ap in NetMgr.AllyPositions()) if (InsideHaunt(ap)) return true;
            return false;
        }
    }

    private void SpawnEnemy(string type)
    {
        if (NoSpawn) return;
        var e = new Enemy();
        e.Configure(type, Wave);

        bool boss = type == "boss" || type == "miniboss";
        if (!boss && type != "goblin")
        {
            float eliteChance = 0.08f + Wave * 0.004f + (Heat - 1f) * 0.12f + Mathf.Max(0, Wave - 10) * 0.012f;   // director pushes more elites when hot; post-wave-10 they get MUCH more common
            if (_rng.Randf() < Mathf.Min(0.6f, eliteChance)) e.MakeElite();
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
        // (#4) bias ~45% of the stream into a cone ahead of your movement — so running forward meets fresh foes instead of only ever trailing behind you
        // (MP FIX) the stream spawned the WHOLE horde around the HOST's player, so clients were ignored and the host got
        // swarmed. Pick a random warden to spawn around each time, so the horde distributes across everyone. Solo = the host.
        Vector3 sc = SpawnAnchor(rng, out bool anchorIsLocal);
        Vector3 mv = anchorIsLocal ? _playerVelSmooth : Vector3.Zero; mv.Y = 0f;   // heading-cone bias only for the local player (we only track their velocity)
        float a = (mv.Length() > 3.5f && rng.Randf() < 0.45f)
            ? Mathf.Atan2(mv.Z, mv.X) + rng.RandfRange(-0.75f, 0.75f)   // ~±43° cone around your heading
            : rng.RandfRange(0, Mathf.Tau);
        float r = 44f + rng.RandfRange(2, 12);
        // (HAUNT) while ANY warden fights in the zone, spawn foes on the zone's RIM aimed inward — the arena fills with the
        // horde rather than trailing you, so the hot-zone reads as a real battleground.
        if (AnyWardenInHaunt)
        {
            float ha = rng.RandfRange(0f, Mathf.Tau);
            float hr = HauntRadius * rng.RandfRange(0.82f, 1.0f);
            e.GlobalPosition = new Vector3(HauntCenter.X + Mathf.Cos(ha) * hr, e.Radius, HauntCenter.Z + Mathf.Sin(ha) * hr);
        }
        else
        e.GlobalPosition = new Vector3(sc.X + Mathf.Cos(a) * r, e.Radius, sc.Z + Mathf.Sin(a) * r);
        e.WakeSilent();   // (NEW) wave-spawned swarmers hunt immediately (idle only applies inside the maze)
        Enemies.Add(e);
        if (!boss) SpawnPoof(e.GlobalPosition);   // (NEW) purple materialization poof (boss gets a dramatic entrance, no poof)
        if (type == "boss") { _boss = e; _bossAddT = 5f; }
    }

    // (NEW) THE WARDED PHALANX — a compound miniboss that arrives as a formed unit: one ward-bearer plus a rank of
    // archers, dropped in together off to one side so you SEE the formation coming. Everything about the fight scales
    // off the archer count, so the unit is built as a whole here rather than trickled in through the normal stream.
    public void SpawnPhalanxUnit(int archers = 3, Vector3? at = null)
    {
        if (!IsAuthority) return;
        var pc = Player != null ? Player.GlobalPosition : Vector3.Zero;
        Vector3 center;
        if (at.HasValue) center = at.Value;
        else
        {
            float a = _rng.RandfRange(0f, Mathf.Tau);
            center = new Vector3(pc.X + Mathf.Cos(a) * 46f, 0f, pc.Z + Mathf.Sin(a) * 46f);
        }
        center = ClampToWorld(center, 25f);

        var lead = new Enemy();
        lead.Configure("phalanx", Wave);
        AddChild(lead); lead.NetId = _netEnemySeq++; lead.TypeIdx = EnemyKinds.Index("phalanx");
        lead.GlobalPosition = new Vector3(center.X, lead.Radius, center.Z);
        lead.WakeSilent();
        Enemies.Add(lead);
        SpawnPoof(lead.GlobalPosition);

        // the rank forms up behind the bearer, on the side away from the party
        Vector3 back = center - pc; back.Y = 0f;
        back = back.LengthSquared() > 0.01f ? back.Normalized() : Vector3.Back;
        var squad = new System.Collections.Generic.List<Enemy>();
        int n = Mathf.Clamp(archers, 1, Enemy.MaxArchers);
        for (int i = 0; i < n; i++)
        {
            var arc = new Enemy();
            arc.Configure("archer", Wave);
            AddChild(arc); arc.NetId = _netEnemySeq++; arc.TypeIdx = EnemyKinds.Index("archer");
            Vector3 slot = center + back.Rotated(Vector3.Up, (i - (n - 1) * 0.5f) * 0.55f) * 6f;
            arc.GlobalPosition = new Vector3(slot.X, arc.Radius, slot.Z);
            arc.WakeSilent();
            Enemies.Add(arc);
            SpawnPoof(arc.GlobalPosition);
            squad.Add(arc);
        }
        lead.FormPhalanx(squad);
        foreach (var arc in squad) NetMgr?.BroadcastWardGuard(arc.NetId, true);
        NetMgr?.BroadcastWard(lead.NetId, 1f);
        Hud?.Banner("a WARDED PHALANX takes the field — break the ward");
        Sfx?.Impact(DamageType.Arcane);
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
    public readonly System.Collections.Generic.List<Effigy> Effigies = new();   // (EFFIGY) scattered blessing shrines
    private bool _effigiesSpawned = false;
    private readonly int[] _effigyActivations = new int[5];   // per-type activation count, lobby-wide → raises the next cost
    private int _effigyKind = -1;                             // theme of the pick currently being rolled (-1 = normal)
    public int EffigyCost(int kind) => 30 + 20 * _effigyActivations[Mathf.Clamp(kind, 0, 4)];   // (NEW) in SOULS now; per-type tier rises per use, lobby-wide
    // (NEW) XP-orb pickup: tiny base radius (orbs persist on the map until you're near or a magnet pulls them)
    public float PickupRange => Player != null ? Player.S.PickupRange : 1.8f;
    private float _magnetT = 0f;
    public bool MagnetActive => _magnetT > 0f;
    public void ActivateMagnet(float dur = 4f) { _magnetT = Mathf.Max(_magnetT, dur); }

    // ===== HAUNT — the roaming hot-zone (MVP: give exploration a moving center of gravity) =====================
    // A marked zone the director lights up. Fighting INSIDE it spawns a denser fight and pays bonus souls per kill;
    // fill its meter (kills-inside) and it BREAKS — a payout + reward chest at its heart, then a fresh Haunt ignites
    // elsewhere so you go seek the next fight. Arriving in a Haunt gives a brief lull, then the local fight escalates.
    private Haunt _haunt;
    public Haunt TheHaunt => _haunt;
    public Vector3 HauntCenter { get; private set; }
    public float HauntRadius = 42f;
    public bool HauntActive { get; private set; }
    private int _hauntKills; private int _hauntGoal = 30;
    public float HauntFrac => _hauntGoal > 0 ? Mathf.Clamp(_hauntKills / (float)_hauntGoal, 0f, 1f) : 0f;
    public bool PlayerInHaunt { get; private set; }        // is the LOCAL player standing in it (drives HUD + local intensity)
    private bool _wasInHaunt = false;
    private float _hauntFillNetT = 0f;

    public void SpawnHaunt(Vector3? at)
    {
        if (!IsAuthority || !InOverworld) return;
        if (_haunt != null && GodotObject.IsInstanceValid(_haunt)) { _haunt.QueueFree(); _haunt = null; }
        Vector3 c;
        if (at.HasValue) c = at.Value;
        else
        {
            // place it a real trip away from the party so reaching it is the point
            var from = Player != null ? Player.GlobalPosition : Vector3.Zero;
            c = GroundedDrySpawn(from, 120f, 240f);
            if (new Vector2(c.X - from.X, c.Z - from.Z).Length() < 90f) c = GroundedDrySpawn(Vector3.Zero, 120f, World.WorldRadius - 60f);
        }
        c = ClampToWorld(c, HauntRadius + 12f);   // (FIX) keep the WHOLE haunt zone (its radius too) off the boundary cliffs
        c.Y = SurfaceHeight(c, 1e9f);
        HauntCenter = c;
        _hauntGoal = 26 + DiffStage() * 6 + (WardenCount - 1) * 8;   // scales with difficulty + party
        _hauntKills = 0; HauntActive = true;
        _haunt = new Haunt(); AddChild(_haunt); _haunt.Init(c, HauntRadius);
        NetMgr?.BroadcastHaunt(c, HauntRadius);
        Hud?.Banner("a HAUNT stirs — fight there to break it");
    }
    public void SetRemoteHaunt(Vector3 c, float radius)   // client ghost
    {
        HauntCenter = c; HauntRadius = radius; HauntActive = true;
        if (_haunt != null && GodotObject.IsInstanceValid(_haunt)) _haunt.QueueFree();
        _haunt = new Haunt { Remote = true }; AddChild(_haunt); _haunt.Init(c, radius);
    }
    public void SetRemoteHauntFill(float f) { if (_haunt != null && GodotObject.IsInstanceValid(_haunt)) _haunt.SetFill(f); }

    private bool InsideHaunt(Vector3 p) => HauntActive && new Vector2(p.X - HauntCenter.X, p.Z - HauntCenter.Z).LengthSquared() < HauntRadius * HauntRadius;
    public const float HauntXp = 2f;   // XP multiplier for kills inside a Haunt
    public float HauntXpMul(Vector3 pos) => InsideHaunt(pos) ? HauntXp : 1f;

    // Enemy.Die → a foe fell (host only). A kill INSIDE the zone is the ONLY soul faucet: it credits the contributors
    // (MP-fair, per-player) AND feeds the break meter. Kills out in the world pay nothing.
    public void NoteHauntKill(Vector3 pos, System.Collections.Generic.HashSet<long> contributors)
    {
        if (!IsAuthority || !HauntActive || !InsideHaunt(pos)) return;
        CreditSouls(contributors);   // the sole soul source now — grants each CONTRIBUTOR (host + any allies who damaged it)
        CreditSouls(contributors);   // (TUNE) +1 bonus → a Haunt kill pays 2 souls PER CONTRIBUTOR (works in MP, not just the host)
        _hauntKills++;
        if (_hauntKills >= _hauntGoal) BreakHaunt();
    }

    private void BreakHaunt()
    {
        if (!IsAuthority) return;
        Vector3 c = HauntCenter;
        int soulPay = 30 + DiffStage() * 8;   // (TUNE) a heftier break payout
        int goldPay = 40;
        GrantHauntBreakReward(soulPay, goldPay);           // the host's own payout
        NetMgr?.GrantAllHauntBreak(soulPay, goldPay);      // (FIX) every CLIENT gets the same souls + gold — the break rewards the whole coven
        var rc = new Chest { NetId = NextPickupId(), SpecialGold = 120 };   // a VISIBLE reward chest at the heart of the broken Haunt (beacon + minimap)
        AddChild(rc); rc.GlobalPosition = new Vector3(c.X, SurfaceHeight(c, 1e9f), c.Z); Chests.Add(rc);
        HauntBurst(c);
        Sfx?.Thunder(); Sfx?.RollLock(4);
        NetMgr?.BroadcastHauntBreak(c);
        HauntActive = false;
        _hauntCd = 3f;                                       // a short beat before the next one lights up
    }
    // apply the break payout to THIS machine's player (host calls directly; clients via the RPC below)
    public void GrantHauntBreakReward(int souls, int gold)
    {
        Souls += souls; Gold += gold; GoldFlash = 1.5f;
        Hud?.Banner($"the HAUNT breaks — +{souls} souls, a reward rises");
    }
    private float _hauntCd = 0f;
    public void ClientHauntBreak(Vector3 c) { HauntBurst(c); Sfx?.Thunder(); if (_haunt != null && GodotObject.IsInstanceValid(_haunt)) { _haunt.QueueFree(); _haunt = null; } HauntActive = false; }
    private void HauntBurst(Vector3 c)
    {
        var col = new Color(0.85f, 0.3f, 0.68f);
        VfxRing(c + Vector3.Up * 0.5f, col, HauntRadius * 1.1f, 0.8f);
        VfxRing(c + Vector3.Up * 0.5f, col.Lerp(Colors.White, 0.5f), HauntRadius * 0.6f, 0.6f);
        SpawnPoof(c);
    }

    private void UpdateHaunt(float dt)
    {
        if (!InOverworld) { PlayerInHaunt = false; return; }
        // local-player membership drives the HUD + the local intensity boost (each machine checks its own player)
        bool inside = Player != null && !Player.Downed && InsideHaunt(Player.GlobalPosition);
        PlayerInHaunt = inside;
        if (inside && !_wasInHaunt) OnEnterHaunt();
        _wasInHaunt = inside;

        if (!IsAuthority) return;
        if (!HauntActive) { _hauntCd -= dt; if (_hauntCd <= 0f) SpawnHaunt(null); return; }
        if (_haunt != null && GodotObject.IsInstanceValid(_haunt)) _haunt.SetFill(HauntFrac);
        _hauntFillNetT -= dt;
        if (_hauntFillNetT <= 0f) { _hauntFillNetT = 0.25f; NetMgr?.BroadcastHauntFill(HauntFrac); }
    }
    // arriving in a Haunt: a brief lull (reset the spawn ramp low) so the fresh fight builds up punchily instead of
    // dragging the same blob in with you — the "fresh area = fresh escalation" beat.
    private void OnEnterHaunt()
    {
        if (!IsAuthority) return;
        _spawnTarget = Mathf.Min(_spawnTarget, 6f);
    }

    // (MAGNET DROP) lodestones dropped by slain foes — persist until a warden walks over one, then vacuum every XP shard on the map.
    public readonly List<Magnet> Magnets = new();
    private float _magnetDropCd = 0f;                 // lobby-wide: at most one lodestone drops per ~2.5 min
    public bool MagnetDropReady => _magnetDropCd <= 0f;
    public void SpawnMagnet(Vector3 pos)
    {
        if (!IsAuthority || _magnetDropCd > 0f) return;
        _magnetDropCd = 150f;                          // ~2.5 min before the next one can drop
        pos = new Vector3(pos.X, SurfaceHeight(pos, 1e9f), pos.Z);
        var m = new Magnet { NetId = NextPickupId() }; AddChild(m); m.GlobalPosition = pos; Magnets.Add(m);
        NetMgr?.BroadcastMagnetSpawn(m.NetId, pos.X, pos.Z);
    }
    public void SetRemoteMagnetSpawn(int netId, float x, float z)
    {
        if (Magnets.Exists(mm => mm != null && mm.NetId == netId)) return;
        var m = new Magnet { NetId = netId, Remote = true }; AddChild(m);
        m.GlobalPosition = new Vector3(x, SurfaceHeight(new Vector3(x, 0, z), 1e9f), z); Magnets.Add(m);
    }
    public void RemoveMagnet(int netId)
    {
        for (int i = Magnets.Count - 1; i >= 0; i--) { var m = Magnets[i]; if (m != null && m.NetId == netId) { Magnets.RemoveAt(i); if (GodotObject.IsInstanceValid(m)) m.QueueFree(); } }
    }
    // grab a lodestone. HOST latches the pull (every orb streaks in); CLIENTS just see it vanish + the flourish (orbs are host-driven,
    // so their synced positions already stream toward the party).
    public void TriggerMagnet(int netId, Vector3 at) { RemoveMagnet(netId); ActivateMagnet(7f); MagnetEffects(at); }
    public void ClientMagnetTaken(int netId, Vector3 at) { RemoveMagnet(netId); MagnetEffects(at); }
    private void MagnetEffects(Vector3 at)
    {
        Sfx?.WindRushBy(at); Sfx?.Clink();
        VfxRing(at, new Color(0.8f, 0.5f, 1f), 6f, 0.6f);
        Hud?.Banner("a LODESTONE — every soul-shard is drawn to you!");
    }
    // (DE-OVERLAP) structures stream in per-chunk, so an interactable placed at load can end up buried in a tree/house/keep once
    // that chunk generates. Push a point out of any nearby solid structure (tree/house/pillar Blockers + tall keep Decks), re-ground it.
    private bool NudgeOutOfStructures(ref Vector3 pos, float clearance)
    {
        bool moved = false;
        for (int iter = 0; iter < 5; iter++)
        {
            bool hit = false;
            var nb = QueryBlockers(pos.X, pos.Z, 9f);
            for (int i = 0; i < nb.Count; i++)
            {
                var b = Blockers[nb[i]];
                float dx = pos.X - b.Pos.X, dz = pos.Z - b.Pos.Z;
                float dd = Mathf.Sqrt(dx * dx + dz * dz), minD = b.Radius + clearance;
                if (dd < minD) { if (dd < 0.01f) { dx = 1f; dz = 0f; dd = 1f; } float k = minD / dd; pos.X = b.Pos.X + dx * k; pos.Z = b.Pos.Z + dz * k; hit = true; moved = true; }
            }
            var ndk = QueryDecks(pos.X, pos.Z, 11f);
            for (int i = 0; i < ndk.Count; i++)
            {
                var d = Decks[ndk[i]];
                if (d.TopY < 1.8f || d.LowPad || d.Floating) continue;   // low pads/pedestals/sky-rims are fine to sit on/near
                float ex = d.Half.X + clearance, ez = d.Half.Y + clearance;
                float dx = pos.X - d.Center.X, dz = pos.Z - d.Center.Z;
                if (Mathf.Abs(dx) < ex && Mathf.Abs(dz) < ez)
                {
                    if (ex - Mathf.Abs(dx) < ez - Mathf.Abs(dz)) pos.X = d.Center.X + (dx >= 0f ? ex : -ex);
                    else pos.Z = d.Center.Z + (dz >= 0f ? ez : -ez);
                    hit = true; moved = true;
                }
            }
            if (!hit) break;
        }
        if (moved) { pos = ClampToWorld(pos, 20f); pos.Y = SurfaceHeight(pos, 1e9f); }
        return moved;
    }

    // (SPAWN SAFETY) A safe overworld spawn near `near`: on dry LAND (spiral out of water), shoved clear of any solid structure
    // (tree/house/keep via NudgeOutOfStructures), and grounded. Note: structures stream in per-chunk, so at the very first frame
    // the Decks/Blockers near spawn may not exist yet — the `_spawnSettleT` re-check in _Process finishes the job once they load.
    public Vector3 SafeSpawn(Vector3 near)
    {
        float minLand = World.WaterLevel + 0.5f;
        Vector3 best = new Vector3(near.X, 0f, near.Z);
        if (_world != null && _world.Height(best.X, best.Z) < minLand)   // in water → spiral out to the nearest dry land
        {
            bool found = false;
            for (float r = 6f; r <= 160f && !found; r += 6f)
                for (int a = 0; a < 16 && !found; a++)
                {
                    float ang = a * Mathf.Tau / 16f;
                    float cx = near.X + Mathf.Cos(ang) * r, cz = near.Z + Mathf.Sin(ang) * r;
                    if (_world.Height(cx, cz) >= minLand) { best.X = cx; best.Z = cz; found = true; }
                }
        }
        NudgeOutOfStructures(ref best, 2f);
        best.Y = SurfaceHeight(best, 1e9f);
        return best;
    }

    // Horizontal distance from (x,z) to the nearest SOLID structure (tree/pillar/house Blocker + tall keep Deck), capped at 10.
    // Bigger = more open. Deterministic given world state → identical on every MP client. Only meaningful once chunks are loaded.
    private float StructureClearance(float x, float z)
    {
        float min = 10f;
        var nb = QueryBlockers(x, z, 12f);
        for (int i = 0; i < nb.Count; i++)
        {
            var b = Blockers[nb[i]];
            float d = Mathf.Sqrt((x - b.Pos.X) * (x - b.Pos.X) + (z - b.Pos.Z) * (z - b.Pos.Z)) - b.Radius;
            if (d < min) min = d;
        }
        var nd = QueryDecks(x, z, 14f);
        for (int i = 0; i < nd.Count; i++)
        {
            var d = Decks[nd[i]];
            if (d.TopY < 1.8f || d.LowPad || d.Floating) continue;
            float ox = Mathf.Max(0f, Mathf.Abs(x - d.Center.X) - d.Half.X), oz = Mathf.Max(0f, Mathf.Abs(z - d.Center.Z) - d.Half.Y);
            float dd = Mathf.Sqrt(ox * ox + oz * oz);
            if (dd < min) min = dd;
        }
        return min;
    }

    // Best OPEN spawn near `near`: dry land, maximally clear of solid structures (not merely shoved against a wall). Prefers spots
    // close to `near`. Deterministic given world state (MP-safe). Only effective once spawn-chunk structures are loaded.
    public Vector3 BestOpenSpawn(Vector3 near)
    {
        float minLand = World.WaterLevel + 0.5f;
        Vector3 best = new Vector3(near.X, 0f, near.Z);
        float bestScore = float.NegativeInfinity; bool any = false;
        for (float r = 0f; r <= 70f; r += 5f)
        {
            int steps = r < 1f ? 1 : Mathf.Max(8, (int)r);
            for (int a = 0; a < steps; a++)
            {
                float ang = a * Mathf.Tau / steps;
                float cx = near.X + Mathf.Cos(ang) * r, cz = near.Z + Mathf.Sin(ang) * r;
                if (_world != null && _world.Height(cx, cz) < minLand) continue;   // in water → skip
                float score = StructureClearance(cx, cz) - r * 0.05f;              // open, but bias toward staying near `near`
                if (score > bestScore) { bestScore = score; best = new Vector3(cx, 0f, cz); any = true; }
            }
            if (bestScore >= 6f) break;   // comfortably open — good enough, stop searching outward
        }
        if (!any) best = new Vector3(near.X, 0f, near.Z);
        best.Y = SurfaceHeight(best, 1e9f);
        return best;
    }

    // Re-check the player's spawn for a short window after StartGame, as chunk structures stream in. Runs locally on each MP
    // client for ITS OWN player (the corrected position then syncs via the normal position broadcast).
    private float _spawnSettleT = 0f;
    private void SettleSpawn(float dt)
    {
        if (_spawnSettleT <= 0f || Player == null || !InOverworld) { _spawnSettleT = 0f; return; }
        _spawnSettleT -= dt;
        if (Decks.Count == 0 && Blockers.Count == 0) return;   // spawn-chunk structures not loaded yet — wait
        Vector3 p = Player.GlobalPosition;
        var test = p;
        bool buried = NudgeOutOfStructures(ref test, 2f);       // would the de-overlap have to move her? → she's in/against a structure
        if (buried || StructureClearance(p.X, p.Z) < 2f)
            Player.GlobalPosition = BestOpenSpawn(p);            // relocate to genuinely open ground nearby (not just a wall edge)
        _spawnSettleT = 0f;   // one clean correction once structures exist
    }
    // host: sweep the on-load interactables near the player (where structures are actually loaded) and shove any buried inside a
    // structure back out to clear ground. Chests/vendors/rituals/roulettes re-sync via their periodic snapshots; the one-shot sets re-broadcast.
    private float _deOverlapT = 0f;
    private void DeOverlapInteractables(float dt)
    {
        if (!IsAuthority || Player == null || !InOverworld) return;
        _deOverlapT -= dt; if (_deOverlapT > 0f) return; _deOverlapT = 0.4f;
        Vector3 pc = Player.GlobalPosition; float r2 = 135f * 135f;   // only within the loaded (collision) region
        bool Near(Vector3 p) => new Vector2(p.X - pc.X, p.Z - pc.Z).LengthSquared() < r2;

        foreach (var c in Chests) if (c != null && GodotObject.IsInstanceValid(c) && !c.Opened && Near(c.GlobalPosition)) { var p = c.GlobalPosition; if (NudgeOutOfStructures(ref p, 1.6f)) c.GlobalPosition = p; }
        foreach (var r in Rituals) if (r != null && GodotObject.IsInstanceValid(r) && Near(r.GlobalPosition)) { var p = r.GlobalPosition; if (NudgeOutOfStructures(ref p, r.Radius + 0.5f)) r.GlobalPosition = p; }
        foreach (var rl in _roulettes) if (rl != null && GodotObject.IsInstanceValid(rl) && Near(rl.GlobalPosition)) { var p = rl.GlobalPosition; if (NudgeOutOfStructures(ref p, 1.8f)) rl.GlobalPosition = p; }
        if (_shop != null && GodotObject.IsInstanceValid(_shop) && Near(_shop.GlobalPosition)) { var p = _shop.GlobalPosition; if (NudgeOutOfStructures(ref p, 2.2f)) _shop.GlobalPosition = p; }
        if (_scroll != null && GodotObject.IsInstanceValid(_scroll) && Near(_scroll.GlobalPosition)) { var p = _scroll.GlobalPosition; if (NudgeOutOfStructures(ref p, 2.2f)) _scroll.GlobalPosition = p; }

        bool effMoved = false;
        foreach (var e in Effigies) if (e != null && GodotObject.IsInstanceValid(e) && !e.Claimed && Near(e.GlobalPosition)) { var p = e.GlobalPosition; if (NudgeOutOfStructures(ref p, 1.8f)) { e.GlobalPosition = p; effMoved = true; } }
        if (effMoved && NetMgr != null && NetMgr.Active) BroadcastEffigiesNet();

        bool padMoved = false;
        foreach (var g in GalePads) if (g != null && GodotObject.IsInstanceValid(g) && Near(g.GlobalPosition)) { var p = g.GlobalPosition; if (NudgeOutOfStructures(ref p, GalePad.Radius + 0.5f)) { g.GlobalPosition = p; padMoved = true; } }
        if (padMoved && NetMgr != null && NetMgr.Active)
        {
            int n = GalePads.Count; var ids = new int[n]; var px = new float[n]; var pz = new float[n]; var yaw = new float[n];
            for (int i = 0; i < n; i++) { var g = GalePads[i]; ids[i] = g.NetId; px[i] = g.GlobalPosition.X; pz[i] = g.GlobalPosition.Z; yaw[i] = g.DirYaw; }
            NetMgr.BroadcastGalePads(ids, px, pz, yaw);
        }

        bool nerfMoved = false;
        foreach (var s in _nerfers) if (s != null && GodotObject.IsInstanceValid(s) && Near(s.GlobalPosition)) { var p = s.GlobalPosition; if (NudgeOutOfStructures(ref p, NerfShrine.Radius * 0.6f)) { s.GlobalPosition = p; nerfMoved = true; } }
        if (nerfMoved && NetMgr != null && NetMgr.Active) NetMgr.BroadcastNerfers(_nerfers);

        if (_bossLair != null && GodotObject.IsInstanceValid(_bossLair) && Near(_bossLair.GlobalPosition)) { var p = _bossLair.GlobalPosition; if (NudgeOutOfStructures(ref p, BossLair.Radius + 1f)) { _bossLair.GlobalPosition = p; NetMgr?.BroadcastBossLair(p, _bossLair.NetId); } }
    }
    private void UpdateMagnets(float dt)   // host-authoritative pickup: any warden stepping onto a lodestone grabs it
    {
        if (!IsAuthority) return;
        if (_magnetDropCd > 0f) _magnetDropCd -= dt;   // tick the lobby-wide drop cooldown
        if (Magnets.Count == 0) return;
        for (int i = Magnets.Count - 1; i >= 0; i--)
        {
            var m = Magnets[i]; if (m == null || !GodotObject.IsInstanceValid(m)) { Magnets.RemoveAt(i); continue; }
            bool got = Player != null && !Player.Downed && new Vector2(m.GlobalPosition.X - Player.GlobalPosition.X, m.GlobalPosition.Z - Player.GlobalPosition.Z).LengthSquared() < Magnet.Radius * Magnet.Radius;
            if (!got && NetMgr != null && NetMgr.Active)
                foreach (var ap in NetMgr.AllyPositions()) if (new Vector2(m.GlobalPosition.X - ap.X, m.GlobalPosition.Z - ap.Z).LengthSquared() < Magnet.Radius * Magnet.Radius) { got = true; break; }
            if (got) { int id = m.NetId; var at = m.GlobalPosition; NetMgr?.BroadcastMagnetTaken(id, at.X, at.Y, at.Z); TriggerMagnet(id, at); }
        }
    }
    // ===== WARD PLATING (NEW) — the lodestone's sibling: fills YOUR armor slots, on a PER-WARDEN cooldown =====
    public readonly List<WardArmor> WardArmors = new();
    private readonly Dictionary<long, float> _wardCd = new();   // peer → seconds until that warden can knock another one loose
    private const float WardDropGap = 60f;
    public bool WardDropReady(long peer) => !_wardCd.TryGetValue(peer, out float t) || t <= 0f;
    public void SpawnWardArmor(Vector3 pos, long peer)
    {
        if (!IsAuthority || !WardDropReady(peer)) return;
        _wardCd[peer] = WardDropGap;
        pos.Y = SurfaceHeight(pos, 1e9f);
        var w = new WardArmor { NetId = NextPickupId() }; AddChild(w); w.GlobalPosition = pos; WardArmors.Add(w);
        NetMgr?.BroadcastWardArmorSpawn(w.NetId, pos.X, pos.Z);
    }
    public void SetRemoteWardArmorSpawn(int netId, float x, float z)
    {
        if (WardArmors.Exists(ww => ww != null && ww.NetId == netId)) return;
        var w = new WardArmor { NetId = netId, Remote = true }; AddChild(w);
        w.GlobalPosition = new Vector3(x, SurfaceHeight(new Vector3(x, 0, z), 1e9f), z); WardArmors.Add(w);
    }
    public void RemoveWardArmor(int netId)
    {
        for (int i = WardArmors.Count - 1; i >= 0; i--) { var w = WardArmors[i]; if (w != null && w.NetId == netId) { WardArmors.RemoveAt(i); if (GodotObject.IsInstanceValid(w)) w.QueueFree(); } }
    }
    public void ClientWardArmorTaken(int netId, Vector3 at) { RemoveWardArmor(netId); WardArmorEffects(at); }
    private void WardArmorEffects(Vector3 at)
    {
        Sfx?.Impact(DamageType.Frost);
        SpawnPollen(at + Vector3.Up, 3.2f, new Color(0.45f, 0.78f, 1f), 16, 5f, net: false);
    }
    private void UpdateWardArmors(float dt)   // host-authoritative pickup: the warden who steps on it is the one who's plated
    {
        if (!IsAuthority) return;
        if (_wardCd.Count > 0)
        {
            _wardTickKeys.Clear(); foreach (var k in _wardCd.Keys) _wardTickKeys.Add(k);
            foreach (var k in _wardTickKeys) if (_wardCd[k] > 0f) _wardCd[k] -= dt;
        }
        if (WardArmors.Count == 0) return;
        for (int i = WardArmors.Count - 1; i >= 0; i--)
        {
            var w = WardArmors[i]; if (w == null || !GodotObject.IsInstanceValid(w)) { WardArmors.RemoveAt(i); continue; }
            long taker = 0; bool got = false;
            if (Player != null && !Player.Downed && new Vector2(w.GlobalPosition.X - Player.GlobalPosition.X, w.GlobalPosition.Z - Player.GlobalPosition.Z).LengthSquared() < WardArmor.Radius * WardArmor.Radius)
            { got = true; taker = LocalPeer; }
            if (!got && NetMgr != null && NetMgr.Active)
                foreach (var (p, ap) in NetMgr.AllyPeerPositions())
                    if (new Vector2(w.GlobalPosition.X - ap.X, w.GlobalPosition.Z - ap.Z).LengthSquared() < WardArmor.Radius * WardArmor.Radius) { got = true; taker = p; break; }
            if (!got) continue;
            int id = w.NetId; var at = w.GlobalPosition;
            NetMgr?.BroadcastWardArmorTaken(id, at.X, at.Y, at.Z);
            RemoveWardArmor(id); WardArmorEffects(at);
            if (taker == LocalPeer) { Player?.FillArmorRandom(); Hud?.Banner("ward plating — armor restored"); }
            else NetMgr?.GrantArmorFill(taker);   // only the warden who touched it gets plated, not the coven
        }
    }
    private readonly List<long> _wardTickKeys = new();

    public void AddXpOrb(Orb o)   // persistent orbs → soft-cap the count so a hoard can't tank perf
    {
        Orbs.RemoveAll(x => x == null || !GodotObject.IsInstanceValid(x));
        while (Orbs.Count >= 150) { var old = Orbs[0]; Orbs.RemoveAt(0); if (GodotObject.IsInstanceValid(old)) old.QueueFree(); }   // cap keeps the persistent hoard + its MP snapshot bounded
        Orbs.Add(o);
    }
    private int _netPickupSeq = 1;
    public int NextPickupId() => _netPickupSeq++;
    public bool ChestPick = false;   // a card pick that came from a chest — does NOT pause others
    private RouletteMachine _roulette;
    private readonly System.Collections.Generic.List<RouletteMachine> _roulettes = new();
    private bool _rouletteActive = false;
    public int RoulettePull => (_roulette != null && GodotObject.IsInstanceValid(_roulette)) ? _roulette.Pulls : 0;

    private int _ultOfferCount = 0;   // (ULT CARDS) how many equip offers seen — drives the by-level-10 pity
    // pick which ult card (if any) to inject this level-up. Legendary equip when you have none (guaranteed by ~L10);
    // once equipped: Epic tier-up / Legendary mod / Legendary swap-to-another, grace-gated so a level burst can't flood.
    private UpgradeCard RollUltCard()
    {
        var p = Player; if (p == null || p.Level < 3) return null;
        var set = UltChoiceSet();
        if (p.Ult == Player.UltKind.None)
        {
            bool pity = p.Level >= 10 && _ultOfferCount == 0;   // by level 10 you WILL have been offered your ultimate
            if (pity || _rng.Randf() < 0.34f) { _ultOfferCount++; return UpgradePool.UltEquipCard(p, set[_rng.RandiRange(0, set.Length - 1)]); }
            return null;
        }
        if (_ultModCd > 0) return null;   // brief grace after any ult card
        float r = _rng.Randf();
        // (TUNE) ult tier/mod/swap cards were surfacing on ~46% of level-ups and crowding out normal picks — roughly halved
        // to ~26% combined, with a longer grace after each so they don't cluster.
        if (r < 0.13f && p.UltTier < 4) { _ultModCd = 4; return UpgradePool.UltTierCard(p); }         // Epic empower
        if (r < 0.20f) { var m = UpgradePool.UltModCard(p); if (m != null) { _ultModCd = 6; return m; } }   // Legendary mod
        if (r < 0.26f)   // Legendary swap to a different ult of this witch (its saved tier persists)
        {
            var others = System.Array.FindAll(set, k => k != p.Ult);
            if (others.Length > 0) { _ultModCd = 5; return UpgradePool.UltEquipCard(p, others[_rng.RandiRange(0, others.Length - 1)]); }
        }
        return null;
    }

    private List<UpgradeCard> RollChoices()
    {
        float savedLuck = Player.S.Luck;
        if (_luckRerollNext) Player.S.Luck *= 2f;   // (NEW) luck-reroll: double luck for THIS roll only (stat itself untouched)
        List<UpgradeCard> list =
            _effigyKind >= 0 ? UpgradePool.RollEffigy(Player, _rng, _effigyKind, 3) :
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
        // (ULT CARDS) ults now flow through the level-up itself: after level 3, an equip card (Legendary) can surface; once
        // you own one, its tier-ups (Epic) + its legendary mod + the option to swap to another of your witch's ults surface.
        // Injected on the normal roll AND the purple (Coven, kind 4) effigy. Not on the other themed effigies / reward picks.
        bool ultContext = (_effigyKind < 0 && _rewardLeft <= 0 && _lootLeft <= 0) || _effigyKind == 4;
        if (ultContext && list.Count > 0)
        {
            var uc = RollUltCard();
            if (uc != null && !list.Exists(c => c.Title == uc.Title)) list[_rng.RandiRange(0, list.Count - 1)] = uc;
        }
        if (_ultModCd > 0) _ultModCd--;
        if (_guaranteeLegCount > 0)   // (NEW) mutator-clear reward: this pick is guaranteed to contain a legendary
        {
            _guaranteeLegCount--;
            if (!list.Exists(c => c.Rarity == Rarity.Legendary) && list.Count > 0)
            {
                var leg = UpgradePool.RollOneLegendary(Player, _rng);
                if (leg != null) list[_rng.RandiRange(0, list.Count - 1)] = leg;
            }
        }
        Player.S.Luck = savedLuck; _luckRerollNext = false;   // (NEW) restore luck; consume the luck-reroll
        return list;
    }
    private int _guaranteeLegCount = 0;   // upcoming picks that must include a legendary (mutator-clear rewards)

    // reward every warden a pick-3 with a guaranteed legendary — granted on clearing a named mutator wave (host calls +broadcasts)
    public void GrantMutatorRewardLocal()
    {
        _pendingLevels += 1;
        _guaranteeLegCount += 1;
        if (State == GameState.Playing)
        {
            Choices = RollChoices();
            ChoiceGen++; RarityCue(Choices);
            State = GameState.LevelUp;
            Input.MouseMode = Input.MouseModeEnum.Visible;
            SelectLock = 0.3f;
        }
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
    private ShopVendor _shop;
    public Mystic VendorMystic => _mystic;          // for the minimap
    public ScrollVendor VendorScroll => _scroll;
    public ShopVendor VendorShop => _shop;
    public Mystic CurMystic => _mystic;
    public ScrollVendor CurScroll => _scroll;
    public ShopVendor CurShop => _shop;
    public Mystic RemoteMystic { set { _mystic = value; } }
    public ScrollVendor RemoteScroll { set { _scroll = value; } }
    public ShopVendor RemoteShop { set { _shop = value; } }
    // ---- shop (peddler) per-machine state: the local player's instanced inventory ----
    public readonly System.Collections.Generic.List<UpgradeCard> ShopCards = new();
    public readonly System.Collections.Generic.List<int> ShopPrices = new();
    public readonly System.Collections.Generic.List<bool> ShopSold = new();
    public readonly System.Collections.Generic.List<int> ShopSection = new();   // 0 boons, 1 finishers, 2 modifiers (for the UI columns)
    private int _shopCleanouts = 0;      // per-run: each full clear boosts the NEXT roll's luck (×2, ×3, …)
    private ShopVendor _activeShop;      // the peddler currently being browsed — its stock is cached on the vendor itself
    private bool _returnToShop = false;  // a purchase opened a sub-screen (element chooser / swap) — come back here after
    private int _shopBuyIdx = -1, _shopBuyPrice = 0;   // the in-flight shop purchase (for refund if a full-slot swap is cancelled)
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
        // Mystic (re-attunement) vendor removed — retuning a witch's attacks muddied her identity. SpawnMystic() no longer called.
        if (lv % 10 == 5 && lv != _lastScrollLvl) { _lastScrollLvl = lv; SpawnScroll(); }
    }
    // pick a spot near `around` (ring minD..maxD) on DRY ground, anchored to the terrain surface. Replaces the old
    // hardcoded Y=0, which made vendors/mystics/chests clip hills or float over water on the new heightmap. (NEW)
    private Vector3 GroundedDrySpawn(Vector3 around, float minD, float maxD)
    {
        Vector3 best = around; float bestY = -9999f;
        for (int i = 0; i < 24; i++)   // more tries so gameplay objects reliably find real dry land, not the waterline
        {
            float a = _rng.RandfRange(0, Mathf.Tau), d = _rng.RandfRange(minD, maxD);
            var p = ClampToWorld(new Vector3(around.X + Mathf.Cos(a) * d, 0, around.Z + Mathf.Sin(a) * d), 14f);   // (NEW) keep spawns inside the bounded overworld (well off the cliff wall)
            float gy = SurfaceHeight(p, 1e9f);
            if (gy >= World.WaterLevel + 0.6f) return new Vector3(p.X, gy, p.Z);   // comfortably DRY ground — take it (raised from +0.2 so rituals/pedestals/vendors don't sit at the waterline)
            if (gy > bestY) { bestY = gy; best = new Vector3(p.X, Mathf.Max(gy, World.WaterLevel + 0.6f), p.Z); }
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

    // ---- the peddler (shop vendor) -----------------------------------------------------------------
    private int _shopLastSpawnWave = -99;
    private void SpawnShop()
    {
        if (_shop != null && GodotObject.IsInstanceValid(_shop)) return;
        var s = new ShopVendor { NetId = NextPickupId(), SpawnedWave = Wave }; AddChild(s); s.GlobalPosition = VendorSpawnPos(); _shop = s;
        Hud?.Banner("a peddler has set up shop…");
    }
    // tier-driven: never in the first few tiers; first eligible check spawns ONE peddler that then STAYS put for the whole
    // world (it no longer packs up / relocates — only a new map clears it). Host-driven (clients receive it via VendorSnapshot).
    public void ShopSpawnCheck()
    {
        if (!IsAuthority) return;
        if (_shop != null && GodotObject.IsInstanceValid(_shop)) return;   // already set up — the peddler lingers the whole run, no despawn
        if (Wave < 4) return;
        _shopLastSpawnWave = Wave; SpawnShop();   // first eligible tier → he arrives and stays
    }

    public void OpenShop(ShopVendor s)   // NOT consumed — the vendor lingers so both players can shop
    {
        _activeShop = s;
        if (s == null) BuildShopOffer();                        // fallback (shouldn't happen) — behave as before
        else if (!s.OfferBuilt) { BuildShopOffer(); StoreShopOffer(s); s.OfferBuilt = true; }   // first open: roll once, cache on the vendor
        else RestoreShopOffer(s);                               // re-open: show the SAME stock (incl. already-bought slots)
        State = GameState.Shop; Input.MouseMode = Input.MouseModeEnum.Visible; SelectLock = 0.3f;
    }
    // copy the freshly-built Game-side offer onto the vendor so it survives leaving the screen
    private void StoreShopOffer(ShopVendor s)
    {
        s.Cards.Clear(); s.Prices.Clear(); s.Sold.Clear(); s.Section.Clear();
        s.Cards.AddRange(ShopCards); s.Prices.AddRange(ShopPrices); s.Sold.AddRange(ShopSold); s.Section.AddRange(ShopSection);
    }
    // repopulate the Game-side offer from the vendor's cached stock
    private void RestoreShopOffer(ShopVendor s)
    {
        ShopCards.Clear(); ShopPrices.Clear(); ShopSold.Clear(); ShopSection.Clear();
        ShopCards.AddRange(s.Cards); ShopPrices.AddRange(s.Prices); ShopSold.AddRange(s.Sold); ShopSection.AddRange(s.Section);
    }
    private void BuildShopOffer()
    {
        ShopCards.Clear(); ShopPrices.Clear(); ShopSold.Clear(); ShopSection.Clear();
        float savedLuck = Player.S.Luck;
        Player.S.Luck = savedLuck * (1 + _shopCleanouts);   // clean-out escalation: ×1 the first time, then ×2, ×3…
        var boons = UpgradePool.RollShopBoons(Player, _rng, 4);
        var fins  = UpgradePool.RollShopFinishers(Player, _rng);
        var mods  = UpgradePool.RollShopModifiers(Player, _rng);
        Player.S.Luck = savedLuck;
        void add(System.Collections.Generic.List<UpgradeCard> src, int section, int count)
        {
            for (int i = 0; i < count; i++)
            {
                var c = (i < src.Count) ? src[i] : null;
                ShopCards.Add(c); ShopSection.Add(section);
                ShopPrices.Add(c != null ? UpgradePool.RarityCost(c.Rarity) : 0);
                ShopSold.Add(c == null);   // an empty slot (nothing eligible rolled) reads as "sold out"
            }
        }
        add(boons, 0, 4); add(fins, 1, 4); add(mods, 2, 4);
    }
    public void ShopBuy(int idx)
    {
        if (idx < 0 || idx >= ShopCards.Count || ShopSold[idx]) return;
        var card = ShopCards[idx];
        if (card == null) return;
        int price = ShopPrices[idx];
        if (Gold < price) { GoldFlash = 1.5f; Sfx?.Denied(); return; }
        Gold -= price; SaveGold();
        ShopSold[idx] = true;
        if (_activeShop != null && idx < _activeShop.Sold.Count) _activeShop.Sold[idx] = true;   // persist the purchase on the vendor
        Sfx?.Clink();
        _shopBuyIdx = idx; _shopBuyPrice = price;   // remember it in case ApplyShopCard opens a full-slot swap the player then cancels
        ApplyShopCard(card);
        bool anyLeft = false;
        for (int i = 0; i < ShopSold.Count; i++) if (!ShopSold[i]) { anyLeft = true; break; }
        if (!anyLeft) { _shopCleanouts++; Hud?.Banner("you cleaned out the peddler! (next visit rolls richer)"); }
    }
    private void ApplyShopCard(UpgradeCard card)
    {
        if (card.AttuneSlot >= 0)   // Cursebrand — open the element chooser, return to the shop after
        { _returnToShop = true; PendingAttune = card.AttuneSlot; State = GameState.Element; Input.MouseMode = Input.MouseModeEnum.Visible; ChoiceGen++; SelectLock = 0.3f; return; }
        if (card.FinKind.HasValue)
        {
            var t = card.FinKind.Value;
            if (Player.OwnsFinisher(t) || !Player.FinisherFull) Player.EquipFinisher(t, card.FinEvery, card.FinPow, card.Rarity);
            else { _returnToShop = true; SwapIsFin = true; _swFin = t; _swEvery = card.FinEvery; _swPow = card.FinPow; _swRar = card.Rarity; State = GameState.Swap; Input.MouseMode = Input.MouseModeEnum.Visible; ChoiceGen++; SelectLock = 0.3f; }
            return;
        }
        if (card.ModKind.HasValue)
        {
            var t = card.ModKind.Value;
            if (Player.OwnsModifier(t) || !Player.ModifierFull) Player.EquipModifier(t, card.ModMag, card.Rarity);
            else { _returnToShop = true; SwapIsFin = false; _swMod = t; _swMag = card.ModMag; _swRar = card.Rarity; State = GameState.Swap; Input.MouseMode = Input.MouseModeEnum.Visible; ChoiceGen++; SelectLock = 0.3f; }
            return;
        }
        card.Apply(Player); Player.Hp = Mathf.Min(Player.S.MaxHp, Player.Hp);   // a boon / blessing / witch upgrade / ult-mod
    }
    public void CloseShop() { State = GameState.Playing; Input.MouseMode = Input.MouseModeEnum.Captured; }
    private void ReturnToShop() { State = GameState.Shop; Input.MouseMode = Input.MouseModeEnum.Visible; SelectLock = 0.3f; }

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
        : (Player != null && Player.ForsakenWitch) ? new[] { Player.UltKind.HexCircle, Player.UltKind.LifeDrain, Player.UltKind.LifeCurse }   // (NEW)
        : (Player != null && Player.EmberWitch) ? new[] { Player.UltKind.MeteorDescent, Player.UltKind.WildfireRush, Player.UltKind.PhoenixAscend }   // (NEW)
        : (Player != null && Player.ArcaneWitch) ? new[] { Player.UltKind.ArcaneAscend, Player.UltKind.ArcaneEruption, Player.UltKind.ArcaneOvercharge }   // (NEW)
        : new[] { Player.UltKind.Eclipse, Player.UltKind.LunarLight, Player.UltKind.Crescent };
    public void OpenUltMenu() { State = GameState.UltMenu; Input.MouseMode = Input.MouseModeEnum.Visible; SelectLock = 0.3f; }
    private void CloseUltMenu() { State = GameState.Playing; Input.MouseMode = Input.MouseModeEnum.Captured; }

    // legacy dedicated-offer path (now only reachable via the token UltSwap); routes through EquipUlt so tiers PERSIST.
    private void ChooseUlt(Player.UltKind k)
    {
        Player.EquipUlt(k);   // restores this ult's saved tier + banner; mods are per-ult so they persist naturally
        _ultModCd = 4;        // grace before the tier/mod cards start surfacing
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
        // Black-cat LUCK biases the whole table: it kills the ambush and pumps the premium rewards (gold / armor / magnet).
        float l = Mathf.Clamp(Player != null ? Player.S.Luck : 0f, 0f, 1f);
        // (NEW) mercy drop: when the OPENER is HURT, a chest has a rising chance to just give a healing font instead of rolling
        // the table. ~1% at full health, climbing to ~33% at ≤20% HP; Luck pushes it higher so it lands more when you need it.
        // (MP) keys off the ACTUAL opener's synced HP — a client opening a chest is judged by THEIR health, not the host's.
        float hpFrac = (openerPeer != 0 && openerPeer != LocalPeer && NetMgr != null && NetMgr.Active)
            ? NetMgr.PeerHpFrac(openerPeer)
            : (Player != null ? Mathf.Clamp(Player.Hp / Mathf.Max(1f, Player.S.MaxHp), 0f, 1f) : 1f);
        if (hpFrac < 0.999f)
        {
            float need = hpFrac <= 0.2f ? 0.33f : 0.33f - 0.4f * (hpFrac - 0.2f);   // 33% at ≤20% HP → ~1% at full
            float healChance = Mathf.Clamp(need * (1f + l), 0f, 0.6f);              // Luck boosts it (capped so it's never guaranteed)
            if (_rng.Randf() < healChance)
            {
                var hf = new GroundField { Type = FieldType.Heal, Radius = 5f, Dur = 6f, Power = Player.S.MaxHp * 0.04f, EnemyDmg = 0f, DType = DamageType.Holy, HealAllies = true };
                AddChild(hf); hf.GlobalPosition = new Vector3(at.X, 0.04f, at.Z);
                Hud?.Banner("a healing font");
                return;   // this chest answered your wounds — skip the normal reward table
            }
        }
        float wGold = 0.33f + l * 0.10f, wUlt = 0.22f, wHeal = 0.17f, wCard = 0.08f;
        float wWard = 0.08f + l * 0.14f, wMagnet = 0.08f + l * 0.10f, wAmbush = 0.04f * Mathf.Max(0f, 1f - l * 2.5f);
        float x = _rng.Randf() * (wGold + wUlt + wHeal + wCard + wWard + wMagnet + wAmbush);
        if ((x -= wGold) < 0f) { GiveGold(openerPeer, Mathf.RoundToInt((10 + Wave * 4) * _rng.RandfRange(0.8f, 1.4f))); Sfx?.Coins(); }   // (NEW) a bright coin ch-ching
        else if ((x -= wUlt) < 0f) { GiveUltCharge(openerPeer, _rng.RandfRange(0.1f, 0.5f)); }
        else if ((x -= wHeal) < 0f)
        {
            var f = new GroundField { Type = FieldType.Heal, Radius = 5f, Dur = 6f, Power = Player.S.MaxHp * 0.04f, EnemyDmg = 0f, DType = DamageType.Holy, HealAllies = true };   // (FIX) also heal co-op allies (their Remote field copy is visual-only)
            AddChild(f); f.GlobalPosition = new Vector3(at.X, 0.04f, at.Z);
            Hud?.Banner("a healing font");
        }
        else if ((x -= wCard) < 0f) { GiveChestCard(openerPeer); }
        else if ((x -= wWard) < 0f) { GiveWard(openerPeer); }   // FILLS your armor with random charges (blood/thorn)
        else if ((x -= wMagnet) < 0f)   // a lodestone — vacuums every XP orb on the map to the party
        {
            ActivateMagnet(4.5f);
            VfxRing(at, new Color(0.7f, 0.85f, 1f), 6f, 0.6f);
            Sfx?.Whish(at);   // (NEW) a whooshing pull as the orbs rush in
            Hud?.Banner("a lodestone — the orbs rush to you!");
        }
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
        if (peer == 0) { Player.FillArmorRandom(); Hud?.Banner("armor charges — fully warded!"); }
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
        string[] adds = CurBiome == Biome.Rainforest
            ? new[] { "pigmy", "pigmydart", "snake", "bat", "swarmer" }   // (NEW) jungle adds pull from the jungle roster
            : new[] { "shade", "wisp", "caster", "flyer" };
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
        string[] pool = CurBiome == Biome.Rainforest
            ? new[] { "pigmy", "pigmydart", "snake", "bat", "jtroll", "swarmer" }   // (NEW) jungle Cleanse horde
            : new[] { "shade", "wisp", "caster", "flyer", "brute" };
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

    // (NEW) rituals now ALL spawn at once, spread across the whole bounded overworld — 5 per warden. Called at game start
    // and on each new overworld map. Host-authoritative; clients receive them via the ritual snapshot.
    // (MAP FILL) the shared "already-placed" list for a run — every discoverable (rituals, shrines, lair, chests, vendors,
    // effigies, portals) spreads against ALL the others so the whole bounded disc fills evenly with no big empty gaps.
    private readonly List<Vector3> _mapOccupied = new();
    // true if a map-wide feature (ritual/pedestal/effigy/vendor/chest/lair/gate…) sits within `r` of this XZ — so chunk-streamed
    // structures/trees can avoid dropping on top of them. (Map features are placed at load; chunks build lazily around them.)
    public bool NearMapFeature(Vector3 p, float r)
    {
        float r2 = r * r;
        foreach (var q in _mapOccupied)
            if ((q.X - p.X) * (q.X - p.X) + (q.Z - p.Z) * (q.Z - p.Z) < r2) return true;
        return false;
    }

    // Host/solo: populate the ENTIRE bounded map, once, at load — a fresh, different-every-run layout decided up front.
    // Called from both map-setup paths (StartGame for the first Grove, ApplyLevelAdvance for every later map).
    private void PopulateMap()
    {
        if (!IsAuthority) return;
        _mapOccupied.Clear();
        SpawnPedestals();    // raised stone platforms FIRST — some effigies get elevated onto them
        SpawnBossLair();     // the far-out objective first (claims its distant spot)
        SpawnNerfers();      // 3 hidden Grove shrines
        SpawnAllRituals();   // 5 rites per warden
        SpawnLoadVendors();  // the peddler + scroll vendor, out in the world
        SpawnAllChests();    // 12 chests per warden, scattered wide
        SpawnEffigies();     // 2 of each of the 5 effigy themes per warden
        SpawnGalePads();     // (GALE NET) ~10 launch pads forming a cohesive hop-network across the map
        SpawnHaunt(null);    // (HAUNT) the roaming hot-zone — the moving center of gravity for combat
    }

    // (GALE NET) scatter ~10 wind launch-pads and AIM each so a 45° ~100u launch lands in-bounds (never toward the edge), on dry
    // land, and near ANOTHER pad — so they chain into a travel web. Host places + aims; clients receive positions+yaws.
    public readonly List<GalePad> GalePads = new();
    private float _galePadCd = 0f;
    private const int GalePadCount = 20;
    private const float GaleLaunchDist = 100f;
    private void SpawnGalePads()
    {
        if (!IsAuthority || !InOverworld) return;
        foreach (var g in GalePads.ToArray()) if (GodotObject.IsInstanceValid(g)) g.QueueFree();
        GalePads.Clear();
        float edge = World.WorldRadius - 45f;
        var pts = new List<Vector3>();
        int outerRing = 7;   // guaranteed pads out near the rim → they get aimed back inward (edge coverage the user asked for)
        for (int i = 0; i < GalePadCount; i++)
        {
            var p = i < outerRing
                ? SpreadPointInWorld(_mapOccupied, 70f, World.WorldRadius * 0.66f, edge)   // outer band
                : SpreadPointInWorld(_mapOccupied, 85f);                                    // anywhere in the disc
            pts.Add(p); _mapOccupied.Add(p);
        }
        var yaws = new float[GalePadCount];
        var ids = new int[GalePadCount]; var px = new float[GalePadCount]; var pz = new float[GalePadCount];
        for (int i = 0; i < GalePadCount; i++)
        {
            Vector3 P = pts[i];
            float bestYaw = Mathf.Atan2(-P.Z, -P.X);   // fallback: toward origin — always lands in-bounds
            float bestScore = -1e9f;
            for (int j = -1; j < GalePadCount; j++)     // candidates: every other pad, plus origin (j == -1)
            {
                if (j == i) continue;
                Vector3 T = j < 0 ? Vector3.Zero : pts[j];
                Vector3 dv = T - P; dv.Y = 0f; if (dv.Length() < 8f) continue;
                Vector3 dir = dv.Normalized();
                Vector3 landing = P + dir * GaleLaunchDist;
                if (new Vector2(landing.X, landing.Z).Length() > edge) continue;   // would fling near/over the cliff wall → reject
                float gy = SurfaceHeight(landing, 1e9f);
                float score = (gy < World.WaterLevel + 0.2f ? -40f : 0f);          // avoid launching into water
                float nearPad = 1e9f; for (int k = 0; k < GalePadCount; k++) if (k != i) nearPad = Mathf.Min(nearPad, landing.DistanceTo(pts[k]));
                score += -Mathf.Abs(nearPad - 14f);                                 // aim to land ~14u BESIDE another pad, never ON it → a network you hop by choice, not a forced chain
                if (score > bestScore) { bestScore = score; bestYaw = Mathf.Atan2(dir.Z, dir.X); }
            }
            yaws[i] = bestYaw;
            var pad = new GalePad { NetId = NextPickupId(), DirYaw = bestYaw };
            AddChild(pad); pad.GlobalPosition = P; GalePads.Add(pad);
            ids[i] = pad.NetId; px[i] = P.X; pz[i] = P.Z;
        }
        NetMgr?.BroadcastGalePads(ids, px, pz, yaws);
    }
    // client: rebuild the pads from the host's one-shot broadcast
    public void SetRemoteGalePads(int[] ids, float[] px, float[] pz, float[] yaw)
    {
        foreach (var g in GalePads.ToArray()) if (GodotObject.IsInstanceValid(g)) g.QueueFree();
        GalePads.Clear();
        for (int i = 0; i < ids.Length; i++)
        {
            var pad = new GalePad { NetId = ids[i], DirYaw = yaw[i], Remote = true };
            AddChild(pad); pad.GlobalPosition = new Vector3(px[i], SurfaceHeight(new Vector3(px[i], 0, pz[i]), 1e9f), pz[i]); GalePads.Add(pad);
        }
    }
    // walk-on launch. You only fire a pad by STEPPING ONTO it from clear ground (the "armed" gate): the pad rearms only once you
    // stand grounded OFF every pad. So landing on/beside a pad never auto-relaunches — chaining is always your choice; walk off to stop.
    private bool _galePadArmed = true;
    private void UpdateGalePads(float dt)
    {
        if (_galePadCd > 0f) _galePadCd -= dt;
        if (Player == null || !CanControlLocal() || GalePads.Count == 0) return;
        var pp = Player.GlobalPosition;
        GalePad on = null;
        foreach (var g in GalePads)
        {
            if (g == null || !GodotObject.IsInstanceValid(g)) continue;
            if (new Vector2(g.GlobalPosition.X - pp.X, g.GlobalPosition.Z - pp.Z).LengthSquared() < GalePad.Radius * GalePad.Radius) { on = g; break; }
        }
        if (Player.Grounded && on == null) _galePadArmed = true;   // standing on clear ground → ready to launch again
        if (Player.Grounded && on != null && _galePadArmed && _galePadCd <= 0f)
        { Player.GaleLaunch(on.LaunchDir, GaleLaunchDist); _galePadArmed = false; _galePadCd = 0.5f; }   // disarm until you step back onto clear ground
    }

    // (PLATFORMS) persistent raised daises scattered at load; a few effigies get placed ON them so the world has verticality/landmarks.
    public readonly List<Pedestal> Pedestals = new();
    private readonly List<Vector3> _pedestalTops = new();   // unused pedestal-top world positions (an effigy claims one, then it's removed)
    private const int PedestalCount = 8;
    private void SpawnPedestals()
    {
        if (!IsAuthority || !InOverworld) return;
        ClearPedestals();
        var ids = new int[PedestalCount]; var px = new float[PedestalCount]; var pz = new float[PedestalCount];
        for (int i = 0; i < PedestalCount; i++)
        {
            var p = SpreadPointInWorld(_mapOccupied, 95f); _mapOccupied.Add(p);
            var ped = new Pedestal { NetId = NextPickupId() }; AddChild(ped); ped.GlobalPosition = p; Pedestals.Add(ped);
            float daisR = Pedestal.DaisR;
            PersistentDecks.Add(new Deck { Center = new Vector3(p.X, 0, p.Z), Half = new Vector2(daisR, daisR), TopY = p.Y + Pedestal.TopH, LowPad = true });   // (FIX) deck covers the WHOLE dais footprint; LowPad → step up from any side (no ramp needed)
            _pedestalTops.Add(new Vector3(p.X, p.Y + Pedestal.TopH - 0.15f, p.Z));   // effigy sits ON the dais surface
            AddPedestalRim(p, daisR);
            ids[i] = ped.NetId; px[i] = p.X; pz[i] = p.Z;
        }
        _world?.MarkBlockersDirty();   // flush the new pedestal decks into Decks so they're solid + walkable right away
        NetMgr?.BroadcastPedestals(ids, px, pz);
    }
    private void ClearPedestals()
    {
        foreach (var pd in Pedestals.ToArray()) if (GodotObject.IsInstanceValid(pd)) pd.QueueFree();
        Pedestals.Clear(); _pedestalTops.Clear();
        PersistentDecks.RemoveAll(d => !d.Floating);   // strip our overworld pedestal decks (sky-island decks are Floating=true → kept)
        PersistentRamps.Clear();                        // only pedestals add persistent ramps → safe to strip all
        PedestalRimBlockers.Clear();
        _world?.MarkBlockersDirty();
    }
    // red blockers on the raised rune-block "pony walls" out on the dais RIM — jump-over height, gaps between them, matching the
    // model's raised blocks (~6 around the perimeter). Sized/positioned to the real dais radius.
    private void AddPedestalRim(Vector3 p, float daisR)
    {
        int n = 6;
        for (int k = 0; k < n; k++)
        {
            float a = k / (float)n * Mathf.Tau + 0.5f, rr = daisR * 0.85f;
            PedestalRimBlockers.Add(new Blocker { Pos = new Vector3(p.X + Mathf.Cos(a) * rr, 0, p.Z + Mathf.Sin(a) * rr), Radius = daisR * 0.2f, Top = p.Y + Pedestal.TopH + 1.3f });
        }
    }
    // client: rebuild pedestals + their walkable decks from the host broadcast
    public void SetRemotePedestals(int[] ids, float[] px, float[] pz)
    {
        ClearPedestals();
        for (int i = 0; i < ids.Length; i++)
        {
            float y = SurfaceHeight(new Vector3(px[i], 0, pz[i]), 1e9f);
            var ped = new Pedestal { NetId = ids[i], Remote = true }; AddChild(ped); ped.GlobalPosition = new Vector3(px[i], y, pz[i]); Pedestals.Add(ped);
            float daisR = Pedestal.DaisR;
            PersistentDecks.Add(new Deck { Center = new Vector3(px[i], 0, pz[i]), Half = new Vector2(daisR, daisR), TopY = y + Pedestal.TopH, LowPad = true });
            AddPedestalRim(new Vector3(px[i], y, pz[i]), daisR);
        }
        _world?.MarkBlockersDirty();
    }

    // (MAP FILL) all the run's chests, scattered across the whole disc, decided at load (was a near-player trickle timer).
    private void SpawnAllChests()
    {
        if (!IsAuthority || !InOverworld) return;
        foreach (var c in Chests.ToArray()) if (GodotObject.IsInstanceValid(c)) c.QueueFree();
        Chests.Clear();
        int count = 18 + 7 * (Mathf.Max(1, WardenCount) - 1);   // (TUNE) solid solo base (18) + a bit per extra warden — not a bare ×players that left solo empty
        for (int i = 0; i < count; i++)
        {
            var pos = SpreadPointInWorld(_mapOccupied, 34f);
            var c = new Chest { NetId = NextPickupId() };
            AddChild(c); c.GlobalPosition = pos; Chests.Add(c); _mapOccupied.Add(pos);
        }
    }

    // (MAP FILL) the standing vendors (peddler + scroll seller), placed out in the world at load. Both persist the whole map.
    private void SpawnLoadVendors()
    {
        if (!IsAuthority || !InOverworld) return;
        if (_shop == null || !GodotObject.IsInstanceValid(_shop))
        {
            var pos = SpreadPointInWorld(_mapOccupied, 90f);
            _shop = new ShopVendor { NetId = NextPickupId(), SpawnedWave = Wave }; AddChild(_shop); _shop.GlobalPosition = pos; _mapOccupied.Add(pos);
        }
        if (_scroll == null || !GodotObject.IsInstanceValid(_scroll))
        {
            var pos = SpreadPointInWorld(_mapOccupied, 90f);
            _scroll = new ScrollVendor { NetId = NextPickupId() }; AddChild(_scroll); _scroll.GlobalPosition = pos; _mapOccupied.Add(pos);
        }
    }

    public void SpawnAllRituals()
    {
        if (!IsAuthority || !InOverworld) return;
        foreach (var r in Rituals.ToArray()) if (GodotObject.IsInstanceValid(r)) r.QueueFree();
        Rituals.Clear();
        int count = 5 * Mathf.Max(1, WardenCount);
        for (int i = 0; i < count; i++)
        {
            var pos = SpreadPointInWorld(_mapOccupied, 70f);
            var r = new RitualCircle { Type = (RiteType)_rng.RandiRange(0, 2), NetId = NextPickupId() };
            AddChild(r); r.GlobalPosition = pos; Rituals.Add(r); _mapOccupied.Add(pos);
        }
        Hud?.Banner("ritual circles stir across the land — seek them out");
        NetMgr?.BroadcastRite(0, 0);   // one stir banner for allies (the circles themselves arrive via snapshot)
    }
    // (REMOVED RampNavTarget/RampBase — the "send every ground foe to the staircase" detour. It read well on paper but
    // funnelled the horde onto one point and left most of them bunched short of it; foes now climb the face directly,
    // slowly, and can be knocked off. Ramps still exist as geometry and are still the fast (full-speed) way up.)

    // a grounded, dry point somewhere in the bounded disc (sampled around origin), spaced from the already-placed ones
    private Vector3 SpreadPointInWorld(List<Vector3> placed, float minSep) => SpreadPointInWorld(placed, minSep, 40f, World.WorldRadius - 45f);
    private Vector3 SpreadPointInWorld(List<Vector3> placed, float minSep, float minR, float maxR)
    {
        Vector3 best = GroundedDrySpawn(Vector3.Zero, minR, maxR); float bestSep = -1f;
        for (int t = 0; t < 20; t++)
        {
            var cand = GroundedDrySpawn(Vector3.Zero, minR, maxR);
            float sep = 1e9f; foreach (var q in placed) sep = Mathf.Min(sep, cand.DistanceTo(q));
            if (sep > bestSep) { bestSep = sep; best = cand; }
            if (bestSep > minSep) break;
        }
        return best;
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
            case 2: Hud?.Banner("RITUAL COMPLETE \u2014 a boon for all!"); Sfx?.WardComplete(); break;   // (NEW) witchy relief ding on EVERY rite completion (only the ward also charges up to it)
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
        if (e == _boss) { _boss = null; if (IsAuthority && _bossFightActive) OnWorldBossDefeated(); }   // (BOSS-LAIR) the world boss died → portal (future: second-stage revive intercepts in OnWorldBossDefeated)
        if (_sacMinibosses.Count > 0 && _sacMinibosses.Contains(e)) OnSacMinibossDied(e);   // (NERFER Sacrifice) all slain → arm the drain
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

        // Gamepad menu nav: A = select (click at the stick-driven cursor), B = back (Escape). Mirrors down+up; gameplay presses no-op inside.
        if (e is InputEventJoypadButton mjb && (mjb.ButtonIndex == JoyButton.A || mjb.ButtonIndex == JoyButton.B))
        { PadMenuButton(mjb.ButtonIndex, mjb.Pressed); if (State != GameState.Playing) return; }

        if (e is InputEventKey f3 && f3.Pressed && !f3.Echo && f3.PhysicalKeycode == Key.F3) { PadDebug = !PadDebug; return; }   // toggle the gamepad debug readout

        if (e is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
        {
            if (!mb.Pressed) { _dragSlider = -1; return; }
            var pos = mb.Position;
            if (SelectLock > 0f && State != GameState.Pause) return;
            switch (State)
            {
                case GameState.CharSelect:
                    break;   // the CharSelect Control node handles its own clicks
                case GameState.LevelUp:
                {
                    if (Hud.RollBusy) { Hud.FinishRoll(); break; }   // (NEW) clicking mid-spin slams the reels to a stop, doesn't pick
                    int btn = Hud.LevelUpBtn(pos);
                    if (btn == 1) { RerollChoices(); break; }
                    if (btn == 2) { LuckRerollChoices(); break; }
                    if (btn == 3) { BuyPick2(); break; }
                    if (btn == 4) { DeclineChoice(); break; }
                    if (btn >= 100) { BanChoice(btn - 100); break; }
                    int idx = Hud.CardAt(pos); if (idx >= 0) ApplyChoice(idx);
                    break;
                }
                case GameState.Attune:
                    if (Hud.AttuneDoneRect.HasPoint(pos)) { CloseAttune(); break; }
                    int an = Hud.AttuneNodeAt(pos);
                    if (an >= 0) { if (Player.PurchasePerk(an)) { Sfx?.Clink(); if (Player.AttunePoints <= 0) CloseAttune(); } else Sfx?.Fizzle(); }
                    break;
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
                case GameState.Shop:
                    { int idx = Hud.ShopAt(pos); if (idx == -1) CloseShop(); else if (idx >= 0) ShopBuy(idx); break; }
                case GameState.Pause:
                    if (InGameOptions) break;   // the options overlay's Control nodes own input while it's up
                    { int bi = Hud.PauseBindAt(pos); if (bi >= 0) { RebindFinisher(bi); break; } }
                    if (Hud.RPauseResume.HasPoint(pos)) ResumeRun();
                    else if (Hud.RPauseOptions.HasPoint(pos)) OpenInGameOptions();
                    else if (Hud.RPauseQuit.HasPoint(pos)) QuitRun();
                    else if (Hud.RPauseRestart.HasPoint(pos) && CanRestartRun()) RestartRun();
                    break;
                case GameState.Over:
                    if (NetMgr != null && NetMgr.Active)   // MP: only the host may choose, and it applies to everyone
                    {
                        if (NetMgr.IsHost)
                        {
                            if (Hud.ROverRetry.HasPoint(pos)) NetMgr.BroadcastGameOverChoice(1);
                            else if (Hud.ROverCharSelect.HasPoint(pos)) NetMgr.BroadcastGameOverChoice(0);
                            else if (Hud.ROverEnd.HasPoint(pos)) NetMgr.BroadcastGameOverChoice(2);
                        }
                    }
                    else if (Hud.ROver.HasPoint(pos)) GetTree().ReloadCurrentScene();
                    else if (Hud.RChangeWitch.HasPoint(pos)) { s_witch = -1; GetTree().ReloadCurrentScene(); }
                    break;
                case GameState.Stats:
                    State = GameState.Playing; Input.MouseMode = Input.MouseModeEnum.Captured;
                    break;
            }
        }
    }


    // (ULT CARDS) ults are now acquired through the level-up roll itself (Legendary equip cards from level 3, guaranteed by
    // ~level 10 via RollUltCard's pity) — NOT a dedicated pop-up offer. This is a no-op so the old call sites stay harmless.
    private bool TryOfferUlt() => false;

    public void OpenLevelUp()
    {
        _pendingLevels++;
        VendorSpawnChecks();
        if (State == GameState.Playing)
        {
            if (TryOfferUlt()) return;
            Choices = RollChoices();
            ChoiceGen++; RarityCue(Choices);
            State = GameState.LevelUp;
            Input.MouseMode = Input.MouseModeEnum.Visible;
            SelectLock = 0.3f;
        }
    }

    private void RarityCue(System.Collections.Generic.List<UpgradeCard> cards)
    {
        // (NEW) during a LevelUp the SLOT ROLL plays the rarity feedback per-card as each reel locks, so firing the
        // whole-hand cue here would just spoil the reveal. Other card screens (swap etc.) still get the instant cue.
        if (State == GameState.LevelUp) return;
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

    // (NEW) forgo this pick-3 for gold instead — scaled by the best rarity offered (~40% of its shop value)
    public int DeclineGold
    {
        get
        {
            if (Choices == null || Choices.Count == 0) return 0;
            int best = 0; foreach (var c in Choices) best = Mathf.Max(best, (int)c.Rarity);
            return Mathf.RoundToInt(UpgradePool.RarityCost((Rarity)best) * 0.4f);
        }
    }
    public void DeclineChoice()
    {
        if (State != GameState.LevelUp || Choices == null) return;
        int g = DeclineGold;
        AddGold(g);
        Hud?.Banner($"declined — +{g} gold");
        FinishStep();
    }

    // damage types offered when re-attuning an attack
    public static readonly DamageType[] Elements = { DamageType.Arcane, DamageType.Nature, DamageType.Frost, DamageType.Curse, DamageType.Holy, DamageType.Ember, DamageType.Lunar, DamageType.Wind };

    public void DoElement(int idx)
    {
        if (idx >= 0 && idx < Elements.Length)
        {
            var ty = Elements[idx];
            if (PendingAttune == 2)   // Cursebrand: pick a 2nd type that also gets the curse-bonus amp — no hand retint (her attacks are unchanged)
            {
                Player.CurseBonusType2 = (int)ty;
                Sfx?.Element(ty);
                var beam2 = new ElementBeam(); AddChild(beam2); beam2.GlobalPosition = new Vector3(Player.GlobalPosition.X, 0f, Player.GlobalPosition.Z); beam2.Init(DamageTypes.Col(ty));
            }
            else if (PendingAttune == 3)   // (NEW) Grafted Element: retype the tree-ent explosions + restyle every ent
            {
                Player.EntElement = ty; Player.EntElementChosen = true; Player.RefreshEntVisuals();
                Sfx?.Element(ty);
                var beam3 = new ElementBeam(); AddChild(beam3); beam3.GlobalPosition = new Vector3(Player.GlobalPosition.X, 0f, Player.GlobalPosition.Z); beam3.Init(DamageTypes.Col(ty));
            }
            else
            {
                if (PendingAttune == 0) Player.PrimaryType = ty; else Player.SecondaryType = ty;
                Player.RetintHands();
                Sfx?.Element(ty);
                var beam = new ElementBeam();
                AddChild(beam);
                beam.GlobalPosition = new Vector3(Player.GlobalPosition.X, 0f, Player.GlobalPosition.Z);
                beam.Init(DamageTypes.Col(ty));
            }
        }
        PendingAttune = -1;
        if (_mysticAttune) { _mysticAttune = false; State = GameState.Mystic; Input.MouseMode = Input.MouseModeEnum.Visible; SelectLock = 0.3f; return; }
        if (_returnToShop) { _returnToShop = false; ReturnToShop(); return; }   // Cursebrand bought from the shop → back to shopping
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
        if (_returnToShop)   // this swap came from a shop purchase
        {
            _returnToShop = false;
            if (idx < 0 && _shopBuyIdx >= 0)   // cancelled ("Keep current") → refund the gold + restock that shop slot
            { Gold += _shopBuyPrice; SaveGold(); if (_shopBuyIdx < ShopSold.Count) ShopSold[_shopBuyIdx] = false;
              if (_activeShop != null && _shopBuyIdx < _activeShop.Sold.Count) _activeShop.Sold[_shopBuyIdx] = false; }
            _shopBuyIdx = -1;
            ReturnToShop();
            return;
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
        _effigyKind = -1;   // (FIX) the current effigy pick is resolved — clear the theme so the NEXT pick rolls normally (rerolls kept it set until now)
        if (_rewardLeft > 0) { _rewardLeft--; if (_rewardLeft == 0) _rewardCat = -1; }
        else if (_lootLeft > 0) _lootLeft--;
        if (_pendingLevels > 0)
        {
            if (TryOfferUlt()) return;   // crossed the ult-unlock level while draining a burst of levels → offer the ult here
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
        else if (Player != null && Player.AttunePoints > 0 && Player.PerkAvailable().Count > 0)
        { OpenAttune(); ChestPick = false; _effigyKind = -1; }   // (ATTRIBUTE) a point to spend → the live perk-tree pop-up, after the card + any equip/swap/bind
        else { State = GameState.Playing; Input.MouseMode = Input.MouseModeEnum.Captured; ChestPick = false; _effigyKind = -1; }
    }

    public void OpenAttune() { State = GameState.Attune; Input.MouseMode = Input.MouseModeEnum.Visible; SelectLock = 0.25f; }
    public void CloseAttune() { State = GameState.Playing; Input.MouseMode = Input.MouseModeEnum.Captured; }

    // ================= Gamepad (Xbox) =================
    // Layered on top of keyboard/mouse — both input methods are always live. Movement + most buttons ride the InputMap
    // actions (registered in SetupInput). The bits that can't be expressed as a plain action live here: right-stick look
    // (Player.UpdatePadLook), the LB+button spell chords + R3 quick-turn (Player._Input), and menu cursor/select/back.
    public static bool PadActive => Input.GetConnectedJoypads().Count > 0;
    public static bool PadDebug = false;   // on-screen gamepad readout — toggle with F3 when diagnosing controller input
    // hold LB = spell-cast modifier: while held, the face buttons fire spell slots instead of jump/dash/ult/interact
    public static bool PadSpellHeld() => PadActive && Input.IsJoyButtonPressed(0, JoyButton.LeftShoulder);
    private const float PadCursorSpeed = 1400f;   // menu cursor travel (px/sec) at full left-stick deflection
    private Vector2 _padCursor = Vector2.Zero;    // OUR tracked menu cursor (never read back from the OS while steering — Parsec asserts its own cursor, which made a GetMousePosition-based loop drift off-screen)
    private bool _padCursorSeeded = false;
    public Vector2 PadCursor => _padCursor;       // Hud draws its own reticle here so there's feedback even if Parsec hides the real cursor
    public bool PadCursorShown => PadActive && State != GameState.Playing && _padCursorSeeded && Input.MouseMode == Input.MouseModeEnum.Visible;

    // In any menu, the left stick drives a cursor we own; we clamp it and warp the OS cursor to match so every existing
    // mouse-driven screen (rect hit-tests + Control buttons) still works. When the stick is idle we follow the real mouse,
    // so a physical mouse keeps working and the controller picks up from wherever it left off.
    private void UpdatePadCursor(float dt)
    {
        if (!PadActive || State == GameState.Playing || Input.MouseMode != Input.MouseModeEnum.Visible) { _padCursorSeeded = false; return; }
        var vp = GetViewport();
        if (vp == null) return;
        var sz = vp.GetVisibleRect().Size;
        if (!_padCursorSeeded) { _padCursor = vp.GetMousePosition(); _padCursorSeeded = true; }
        var stick = new Vector2(Input.GetJoyAxis(0, JoyAxis.LeftX), Input.GetJoyAxis(0, JoyAxis.LeftY));
        float mag = stick.Length();
        if (mag >= 0.18f)
        {
            float t = (mag - 0.18f) / 0.82f;                                       // rescale past the deadzone
            _padCursor += (stick / mag) * (t * t) * PadCursorSpeed * dt;           // squared curve = fine control near center
            _padCursor = new Vector2(Mathf.Clamp(_padCursor.X, 0f, sz.X), Mathf.Clamp(_padCursor.Y, 0f, sz.Y));
            Input.WarpMouse(_padCursor);
        }
        else
        {
            var m = vp.GetMousePosition();   // idle: track the real cursor so a physical mouse stays coherent — but clamp, in case Parsec reports an off-window position
            _padCursor = new Vector2(Mathf.Clamp(m.X, 0f, sz.X), Mathf.Clamp(m.Y, 0f, sz.Y));
        }
    }

    // A = select, B = back. We MIRROR the controller button's own down/up (not a synthetic press+release in one frame) so
    // polled handlers (IsActionJustPressed) get a clean edge: A → left mouse at the cursor (rect-tests + Control buttons),
    // B → Escape (release_mouse / CharSelect+Lobby back). Presses are ignored during gameplay; releases always pass through
    // so a click that transitions us INTO gameplay still delivers its matching mouse-up (no stuck "cast" button).
    private void PadMenuButton(JoyButton btn, bool pressed)
    {
        var vp = GetViewport();
        if (vp == null) return;
        if (pressed && State == GameState.Playing) return;
        if (btn == JoyButton.A)
        {
            var p = _padCursorSeeded ? _padCursor : vp.GetMousePosition();   // click where OUR reticle is, not the possibly-Parsec-pinned OS cursor
            Input.WarpMouse(p);                                              // make sure the real cursor is there before the click lands
            Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = pressed, Position = p, GlobalPosition = p });
        }
        else if (btn == JoyButton.B)
            Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Key.Escape, Keycode = Key.Escape, Pressed = pressed });
    }

    public override void _Process(double delta)
    {
        CrashLogger.Beat("Game._Process");   // heartbeat — the watchdog flags a freeze if this stops
        float dt = (float)delta;
        if (_spawnSettleT > 0f) SettleSpawn(dt);   // (SPAWN SAFETY) nudge the player off any structure that streamed in on spawn
        if (SelectLock > 0f) SelectLock -= dt;
        UpdatePadCursor(dt);
        if (State == GameState.Playing && _pendingLevels == 0) TryOfferUlt();   // (SAFETY NET) never leave an unlock-level+ warden without their ult offer, no matter how the levels were gained
        if (_dotCreditCd.Count > 0)   // (NEW) age the per-caster DoT-combo throttles
        {
            var __dk = new System.Collections.Generic.List<int>(_dotCreditCd.Keys);
            foreach (var k in __dk) { float v = _dotCreditCd[k] - dt; if (v <= 0f) _dotCreditCd.Remove(k); else _dotCreditCd[k] = v; }
        }
        if (ToastT > 0f) ToastT -= dt;
        _lightCullT -= dt;
        if (_lightCullT <= 0f) { _lightCullT = 0.2f; CullEnemyLights(); CullWispLights(); CullOrbLights(); }   // perf: cap real-time enemy + wisp + XP-orb lights to the nearest few
        ComputeWorldRunning();
        UpdateInteract(dt);
        UpdateGalePads(dt);   // (GALE NET) walk-on launch pads — each machine checks its own local player
        UpdateMagnets(dt);
        UpdateWardArmors(dt);   // (NEW) ward-plating pickups, per-warden drop cooldown
        UpdateHaunt(dt);        // (HAUNT) the roaming hot-zone: membership, fill meter, break + respawn    // (MAGNET DROP) host detects a warden stepping onto a dropped lodestone
        DeOverlapInteractables(dt);   // (DE-OVERLAP) shove any on-load interactable that streamed inside a structure back to clear ground
        if (State == GameState.Playing) { MaybeSpawnGardenPortals(); MaybeSpawnEffigies(); RevealMinimap(); UpdateNerfers(dt); }
        if (State == GameState.Playing) MaybeSpawnSkyWhirl();   // (NEW) jungle sky-islands whirlwind, 5 waves into the jungle
        if (InSky) TickSky(dt);                                 // (NEW) sky ritual director + heat + fall-out check
        if (GoldFlash > 0f) GoldFlash -= dt;
        if (!InExpedition && _world != null && Player != null) _world.Update(Player.GlobalPosition);
        if (State == GameState.Playing && Player != null && !InExpedition && !InMaze)   // (NEW) periodic visible wind gusts drifting past
        {
            _windGustT -= dt;
            if (_windGustT <= 0f) { _windGustT = (float)GD.RandRange(2.5, 5.0); SpawnWindGust(Player.GlobalPosition); }
        }
        // DEBUG (host/solo): F6 enters/exits the hedge-maze test; F7 loads the old Expedition test leg.
        bool f6 = Input.IsPhysicalKeyPressed(Key.F6);
        if (f6 && !_mazeKeyWas && IsAuthority && State == GameState.Playing) { if (InMaze) ExitMaze(); else EnterMaze((ulong)GD.Randi()); }
        _mazeKeyWas = f6;
        bool f7 = Input.IsPhysicalKeyPressed(Key.F7);
        if (f7 && !_expoKeyWas && IsAuthority && State == GameState.Playing && !InMaze) BeginExpedition((ulong)GD.Randi());
        _expoKeyWas = f7;
        // (leaving the maze is a hold-E on the exit portal — see UpdateInteract)
        if (InMaze && _ritualActive) UpdateGardenRitual(dt);
        if (InMaze && _gardenRitual && _ritualActive && !_ritualDone && _maze != null && Player != null && RitualStatueValid)
        {
            var spos = RitualStatueWorld();   // reaching the hidden cauldron completes the ritual
            if (new Vector2(Player.GlobalPosition.X - spos.X, Player.GlobalPosition.Z - spos.Z).Length() < 3.5f)
            {
                if (IsAuthority) CompleteRitual();
                else NetMgr?.RequestStatue();
            }
        }
        if (!_gardenRitual && InMaze && !_mazeFound && _maze != null && Player != null && !(NetMgr != null && NetMgr.Active) && _mazeStatueTarget >= 0)
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
        if (!_gardenRitual && InMaze && !_mazeFound && IsAuthority && NetMgr != null && NetMgr.Active && _maze != null && Player != null)
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
            bool ritualSearch = _gardenRitual && _ritualActive && !_ritualDone;   // the 3-min hunt for the statue
            bool hot = mazeOpened || ritualSearch;   // mobs keep spawning the WHOLE time in the maze — including the escape (they only truly stop when you leave)
            if (ritualSearch)   // ritual threat ramps with elapsed time, CAPPED near the 2-minute mark so it stays tense, not impossible
            {
                float searchT = Mathf.Min(RitualDur - _ritualTimer, 120f);
                Heat = Mathf.Clamp(1f + searchT * 0.012f, 1f, 2.5f);
            }
            else if (mazeOpened && _mazeDist != null)   // heat: from time, more from team distance-to-portal (steeper now)
            {
                float sumd = 0f; foreach (var c in pcells) { int dd = _mazeDist[c.X, c.Y]; if (dd > 0) sumd += dd; }
                Heat = Mathf.Clamp(1f + _mazeElapsed * 0.022f + (pcells.Count > 0 ? sumd / pcells.Count : 0f) * 0.045f, 1f, 2.8f);
            }

            _mazeSpawnT -= dt;
            if (_mazeSpawnT <= 0f)   // keep reinforcements pouring in the whole time — search AND escape
            {
                if (Enemies.Count < 13 * WardenCount + 8)   // dwindled cap so 2-player escapes aren't overwhelming
                {
                    int count = hot ? 1 + (int)(WardenCount * Mathf.Max(0f, Heat - 1f) * 1.3f) : 2;
                    int[,] portalDist = null;   // spawn out-of-LOS in ANY direction (incl. ahead) so the horde can cut you off
                    for (int i = 0; i < count; i++)
                        if (Maze.PickSpawnCell(_maze, portalDist, pcells, _mazeRng, out var scell))
                        {
                            var me = SpawnMazeEnemy("swarmer", _maze.CellCenter(scell));
                            if (hot) me?.Alert();   // ritual hunt + escape: reinforcements hunt immediately
                        }
                }
                _mazeSpawnT = hot ? Mathf.Lerp(2.2f, 0.6f, Mathf.Clamp((Heat - 1f) / 1.6f, 0f, 1f))
                                  : Mathf.Lerp(3.0f, 1.3f, Mathf.Clamp(_mazeElapsed / 45f, 0f, 1f));
            }
            // Special enemies (Takers; future specials): MP-only, capped at (players-1) total, on a director cooldown — checked every frame
            _specialSpawnT -= dt;
            if (hot && NetMgr != null && NetMgr.Active && WardenCount >= 2 && _mazeDist != null && _specialSpawnT <= 0f)
            {
                _specialSpawnT = Mathf.Lerp(20f, 10f, Mathf.Clamp((Heat - 1f) / 1.8f, 0f, 1f));   // (FIX) reset the cooldown even when capped / no valid cell — so Takers respect the delay
                int specials = 0; foreach (var e in Enemies) if (e != null && GodotObject.IsInstanceValid(e) && e.IsSpecial) specials++;
                if (specials < WardenCount - 1 && Maze.PickSpawnCell(_maze, _mazeDist, pcells, _mazeRng, out var tcell))
                    SpawnMazeEnemy("taker", _maze.CellCenter(tcell));
            }
        }
        if (State == GameState.Playing && WorldRunning) GameClock += dt;
        if (State == GameState.Playing && WorldRunning) { DayTime += dt / DayLength; if (DayTime >= 1f) DayTime -= 1f; _skyTime += dt; _skyMat?.SetShaderParameter("sky_time", _skyTime); ApplyDayNight(); }
        if (Sfx != null && Player != null)
        {
            // (FIX) while the world is PAUSED the percussion bed used to hold whatever combat level it was at — so sitting
            // in a pause menu left a dry shaker/tom pattern ticking away under the arp, which reads as a metronome click
            // rather than as music. Tension now falls to zero whenever the sim isn't running, so pausing leaves the melody
            // alone. (MP keeps playing: SimActive stays true there because the world runs on around the menu.)
            float target = SimActive ? ComputeTension() : 0f;
            _tension = Mathf.Lerp(_tension, target, (target > _tension ? 6f : 1.8f) * dt);
            float fireNudge = Mathf.Min(Player.FireHeat, 0.5f) * 0.16f;   // capped + gentle so holding fire can't run it away
            float tens = _tension * 0.22f;
            Sfx.SetTempo(0.97f + Mathf.Max(fireNudge, tens));
            Sfx.EventActive = Rituals.Count > 0;
            Sfx.HauntBlend = PlayerInHaunt ? 1f : 0f;   // (HAUNT) fade the eerie music layer in while you're in the storm
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
                if (_waveSyncT <= 0f) { _waveSyncT = 0.2f; NetMgr.BroadcastWaveState(Wave, _tier, _skipVotes.Count, (int)ActiveMutator); }   // (CONTINUOUS) send the fractional difficulty tier in the old gap slot
            }
        }

        if (State == GameState.CharSelect) return;   // the CharSelect Control node handles selection/confirm
        if (State == GameState.Over)
        {
            if (NetMgr != null && NetMgr.Active)   // MP: host uses the on-screen buttons; keys only shortcut the host's choices
            {
                if (NetMgr.IsHost)
                {
                    if (Input.IsActionJustPressed("restart")) NetMgr.BroadcastGameOverChoice(1);
                    else if (Input.IsActionJustPressed("changewitch")) NetMgr.BroadcastGameOverChoice(0);
                }
                return;
            }
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
            if (InGameOptions)   // Esc backs out of the options overlay to the pause menu (rather than resuming)
            {
                if (SelectLock <= 0f && Input.IsActionJustPressed("release_mouse")) CloseInGameOptions();
                return;
            }
            if (SelectLock <= 0f && (Input.IsActionJustPressed("release_mouse") || Input.IsActionJustPressed("pick1")))
                ResumeRun();
            return;
        }

        if (State == GameState.Attune)
        {
            if (SelectLock <= 0f && Input.IsActionJustPressed("release_mouse")) CloseAttune();   // (FIX) "interact" was never a defined InputMap action — Esc / the Done button close it
            return;
        }

        if (State == GameState.LevelUp)
        {
            if (SelectLock <= 0f)
            {
                bool anyPick = Input.IsActionJustPressed("pick1") || Input.IsActionJustPressed("pick2") || Input.IsActionJustPressed("pick3") || Input.IsActionJustPressed("pick0");
                if (Hud.RollBusy) { if (anyPick) Hud.FinishRoll(); }   // (NEW) a keypress mid-spin settles the reels instead of picking
                else if (Input.IsActionJustPressed("pick1")) ApplyChoice(0);
                else if (Input.IsActionJustPressed("pick2")) ApplyChoice(1);
                else if (Input.IsActionJustPressed("pick3")) ApplyChoice(2);
                else if (Input.IsActionJustPressed("pick0")) DeclineChoice();   // (NEW) 0 = forgo the pick for gold
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
        if (State == GameState.Shop)
        {
            if (SelectLock <= 0f && Input.IsActionJustPressed("release_mouse")) CloseShop();   // Esc to leave; buying is click-only (12 items)
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

        if (State == GameState.Playing && Input.IsActionJustPressed("ult") && !PadSpellHeld()) Player.TryUlt();   // Y = ult, but LB+Y is spell slot 2
        // (REMOVED) the [U] boss-token ult-upgrade/swap menu is deprecated — ults are card-based now (tier-ups + swaps roll in the level-up). U no longer opens it.

        // Playing — spawn pacing. (FIX) count ONLY active combat toward the clear time — NOT the between-wave rest. It used
        // to run through the whole ~30s intermission too, so `clear` was always ≥30s and the director's fast-clear up-ramps
        // (<16s / <26s) were unreachable unless you skipped the rest — which is why Heat never climbed past 1.0.
        if (_toSpawn.Count > 0 || Enemies.Count > 0) _waveTimer += dt;
        if (_magnetT > 0f) _magnetT -= dt;   // chest lodestone: orbs vacuum to the party while it's active
        if (IsAuthority && Player != null && State == GameState.Playing)   // feed the director
        {
            float hp = Player.S.MaxHp > 0 ? Player.Hp / Player.S.MaxHp : 1f;
            if (Player.Downed) hp = 0f;
            if (NetMgr != null && NetMgr.Active) hp = Mathf.Min(hp, NetMgr.MinAllyHpFrac());   // (NEW) party-wide lowest HP, not just the host's
            _waveMinHpFrac = Mathf.Min(_waveMinHpFrac, hp);
            if (Player.Downed) _downThisWave = true;
            if (NetMgr != null && NetMgr.Active && NetMgr.AnyDowned()) _downThisWave = true;
        }
        if (Player != null) _waveMaxComboMul = Mathf.Max(_waveMaxComboMul, Player.ComboMul());

        // Expedition mode drives spawns/objective from its own director instead of the endless waves.
        if (IsAuthority && WorldRunning && InExpedition && _expoRun != null) { _expoRun.Tick(this, dt); BroadcastExpoStateIfHost(); }

        // Only the authority (solo or host) drives chests, spawns, boss adds, and wave progression,
        // and only while the shared world is running (paused during level-up gates / all-pause / the sky ritual).
        if (IsAuthority && WorldRunning && !InExpedition && !InSky)
        {
        // (MAP FILL) chests no longer trickle in near the player over time — the whole map's chests are scattered once at load (SpawnAllChests)

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

        UpdateSpecialDirector(dt);   // (SMART DIRECTOR) situational Taker spawning — see below
        RunReflowDirector(dt);       // (NEW) recycle foes that have fallen behind into fresh ones ahead of / flanking the party

        if (FireRings.Count > 0) AgeFireRings(dt);   // (NEW) expire Ring-of-Fire zones
        if (WindRings.Count > 0) AgeWindRings(dt);   // (NEW) expire Cyclone projectile-eating zones
        if (ActiveMutator == WaveMutator.Moonfall) MoonfallTick(dt);   // rain moon-fragment asteroids across the field this wave

        // (CONTINUOUS DIRECTOR) no more waves or intermissions — a time-ramped difficulty coefficient drives a continuous,
        // escalating stream of enemies (RoR2-style). See UpdateDifficulty / RunStreamDirector below.
        UpdateDifficulty(dt);
        }
    }

    private void AwardWaveGold()
    {
        // (REWORK) tied to the wave's HEAT (its actual difficulty) instead of clear time — the metric we just retired as a bad
        // signal (esp. in MP). Harder waves pay more; combo rewards style; depth + a mutator-gauntlet bonus round it out.
        float heatF = 0.6f + Heat;                                // ~1.45 (Heat 0.85) → 2.2 (Heat 1.6): the wave's difficulty
        float comboF = Mathf.Clamp(_waveMaxComboMul, 1f, 2f);     // your peak combo this wave — a style reward
        float depthF = 1f + (Wave - 1) * 0.1f;                    // later waves are worth more
        float mutF = _endedMutator != WaveMutator.None ? 1.4f : 1f;   // named-mutator waves are a gauntlet → +40%
        int g = Mathf.Max(1, Mathf.RoundToInt(7f * heatF * comboF * depthF * mutF));
        int flat = Mathf.RoundToInt(_waveComboAccrued * 0.04f);   // small bonus for total combo activity
        g += flat;
        Gold += g;
        LastWaveGold = g;
        GoldFlash = 3f;
        SaveGold();
    }

    // ===== CONTINUOUS DIFFICULTY DIRECTOR (RoR2-style) — replaces waves/intermission =====
    private float _diffTime = 0f;      // elapsed "combat clock" (scaled by party size)
    private float _tier = 1f;          // the difficulty coefficient (hidden; drives scaling, unlocks, spawn pressure)
    private float _credits = 0f;       // spawn-director credit pool
    private int _lastTierInt = 1;
    private float _heatNudgeT = 12f;   // situational Heat re-evaluation cadence
    private float _mutatorT = 0f;      // active-mutator lifetime
    public float Difficulty => _tier;  // for the HUD difficulty meter
    // difficulty STAGE = which escalation band we're in (mirrors Hud.DiffBands thresholds): CALM,STIRRING,RESTLESS,MENACING,FRENZIED,RUINOUS,CATACLYSMIC,APOCALYPSE,OBLIVION
    private static readonly float[] StageTiers = { 0f, 3f, 6f, 10f, 15f, 22f, 30f, 45f, 70f };
    public int DiffStage() { int s = 0; for (int i = StageTiers.Length - 1; i >= 0; i--) if (_tier >= StageTiers[i]) { s = i; break; } return s; }
    private Vector3 _lastPlayerPos = Vector3.Zero, _playerVelSmooth = Vector3.Zero; private bool _ppInit = false;   // (#4) smoothed local-player movement → spawn-ahead bias

    private struct StreamMob { public string T; public float Cost; public int Unlock; public float W; public StreamMob(string t, float c, int u, float w) { T = t; Cost = c; Unlock = u; W = w; } }
    private static readonly StreamMob[] GroveMobs = {
        new("shade", 1f, 1, 10f), new("swarmer", 1f, 1, 10f), new("wisp", 1.5f, 1, 5f), new("brute", 5f, 1, 3f),
        new("caster", 3f, 2, 4f), new("flyer", 2.5f, 2, 4f),
        new("sieger", 6f, 3, 2f), new("healer", 4f, 3, 2f), new("diver", 3f, 3, 3f), new("splitter", 4f, 3, 2.5f),
        new("zapper", 3f, 4, 2.5f), new("sentinel", 8f, 4, 1.5f), new("hexer", 4f, 4, 2f), new("wardbane", 4f, 4, 2f),
        new("bomber", 3f, 5, 2f), new("totem", 5f, 5, 1.2f),
    };
    private static readonly StreamMob[] JungleMobs = {
        new("swarmer", 1f, 1, 8f), new("pigmy", 1f, 1, 10f), new("pigmydart", 1.5f, 1, 5f), new("snake", 1f, 1, 4f),
        new("bat", 2f, 2, 4f), new("jtroll", 5f, 2, 2.5f), new("ptero", 3f, 3, 3f), new("croc", 4f, 4, 3f),
    };

    private void UpdateDifficulty(float dt)
    {
        if (!IsAuthority) return;   // host/solo drives the clock + stream; clients receive Wave/tier via BroadcastWaveState
        if (!SimActive || InMaze || InExpedition || InSky) return;   // those modes run their own pacing/spawners; in MP the stream keeps flowing while someone's in a menu
        _diffTime += dt * (1f + 0.04f * (WardenCount - 1));   // (SLOWED) MP clock barely faster now — was ×1.1-1.3, which made co-op ramp to "ultra fast" enemies well before the intended timeline
        if (Player != null)   // (#4) track the local player's smoothed velocity so the stream can spawn foes ahead of your run
        {
            Vector3 pv = (_ppInit && dt > 0.0001f) ? (Player.GlobalPosition - _lastPlayerPos) / dt : Vector3.Zero; pv.Y = 0f;
            if (pv.LengthSquared() > 900f) pv = Vector3.Zero;   // ignore dash/teleport spikes (>30 u/s)
            _playerVelSmooth = _playerVelSmooth.Lerp(pv, 0.2f);
            _lastPlayerPos = Player.GlobalPosition; _ppInit = true;
        }
        float m = _diffTime / 60f;
        _tier = 1f + m * 0.33f + m * m * 0.0062f;            // (TUNE) nudged the ramp up a hair to offset the slightly-faster XP curve (was 0.30/0.0055) — MENACING ~19min, RUINOUS ~37min, CATACLYSMIC ~45min
        Wave = Mathf.Max(1, (int)_tier);                     // (COMPAT) hidden tier drives everything that used to read "Wave"
        if (Wave > _lastTierInt) { for (int w = _lastTierInt + 1; w <= Wave; w++) OnTierUp(w); _lastTierInt = Wave; }

        _heatNudgeT -= dt; if (_heatNudgeT <= 0f) { _heatNudgeT = 12f; NudgeHeat(); }
        if (ActiveMutator != WaveMutator.None) { _mutatorT -= dt; if (_mutatorT <= 0f) EndMutator(); }
        RunStreamDirector(dt);
    }

    // a difficulty tier ticked over → fire the periodic events that used to hang off wave numbers (no roster batch anymore)
    private void OnTierUp(int tier)
    {
        if (!IsAuthority) return;
        AwardWaveGold(); _endedMutator = WaveMutator.None; _waveMaxComboMul = 1f; _waveComboAccrued = 0;
        ShopSpawnCheck();
        if (tier % 10 == 0) SpawnRoulette();
        if (Player != null && Player.DivineWitch && tier > 1 && tier % 10 == 1) Player.Interventions = Mathf.Min(2, Player.Interventions + 1);
        if (tier % 5 == 0) SpawnEnemy("miniboss");
        // (the Warded Phalanx is NOT on a tier cadence — it's a SPECIAL, spawned by the desire director below)
        if (Goblin == null && _rng.Randf() < (ActiveMutator == WaveMutator.BloodMoon ? 0.25f : 0.06f)) SpawnGoblin();
        if (tier >= 3 && tier % 5 != 0 && ActiveMutator == WaveMutator.None && _rng.Randf() < 0.28f)   // a named mutator flares up for ~50s, then rewards on clear
        { ActiveMutator = (WaveMutator)_rng.RandiRange(1, 5); _mutatorT = 50f; MutatorBanner(); }   // synced to clients via BroadcastWaveState's mutator field
    }
    private void EndMutator()
    {
        var was = ActiveMutator; ActiveMutator = WaveMutator.None; _mutatorT = 0f;
        foreach (var ms in GetChildren()) if (ms is Moonshard mm && GodotObject.IsInstanceValid(mm)) mm.QueueFree();
        if (IsAuthority && was != WaveMutator.None) { _endedMutator = was; GrantMutatorRewardLocal(); NetMgr?.BroadcastMutatorReward(); }
    }
    private void NudgeHeat()   // (SITUATIONAL) the secondary, small modifier on top of the time-based ramp
    {
        if (!IsAuthority) return;
        float hp = Player != null ? Mathf.Clamp(Player.Hp / Mathf.Max(1f, Player.S.MaxHp), 0f, 1f) : 1f;
        if (NetMgr != null && NetMgr.Active) hp = Mathf.Min(hp, NetMgr.MinAllyHpFrac());
        bool downed = Player != null && Player.Downed;
        float step = downed ? -0.09f : (hp > 0.72f ? 0.05f : (hp < 0.35f ? -0.06f : 0f));
        Heat = Mathf.Clamp(Heat + step, 0.9f, 1.4f);
    }

    // the continuous stream: bank credits over time (faster as difficulty climbs), spend them on unlocked enemies, capped by a concurrent limit
    private void RunStreamDirector(float dt)
    {
        if (!IsAuthority) return;
        float playerFactor = 1f + 0.45f * (WardenCount - 1);
        float openGrace = Mathf.Clamp(_diffTime / 20f, 0.4f, 1f);   // brief ~20s ease-in on the spawn RATE only (so t=0 isn't a burst) — NOT the concurrent cap
        // (HAUNT) fighting inside the hot-zone spawns a DENSER fight — more credits banked, a higher concurrent cap.
        bool haunted = AnyWardenInHaunt;   // host director: dense if ANY warden is in the zone, not just the local player
        float hauntRate = haunted ? 1.6f : 1f;
        _credits += dt * (0.7f + _tier * 0.18f) * playerFactor * Heat * openGrace * hauntRate;
        // (STAGE-DRIVEN CAP) the max concurrent horde steps up with the difficulty STAGE (the HUD band), not a warmup: CALM≈25 → +7/stage.
        // Early stages stay light (perf-friendly); the big hordes are the late-game "threatening/unbearable" payoff at 20-25min+.
        int maxAlive = Mathf.Min(haunted ? 96 : 72, 25 + DiffStage() * 7 + (WardenCount - 1) * 6 + (haunted ? 16 : 0));
        int alive = 0; foreach (var e in Enemies) if (e != null && GodotObject.IsInstanceValid(e) && !e.Dead && !e.IsBoss && !e.IsSpecial) alive++;
        // (BREATHING ROOM) the concurrent cap RAMPS toward maxAlive instead of refilling instantly. A big WIPE (alive drops below 40%
        // of max while the ramp was high) restarts the ramp low → clearing a bunch buys real breathing room at ANY difficulty. A slow
        // drifting multiplier jitters the refill rate so the cadence pulses naturally (down-ramp when you kill fast, up-ramp to recover).
        float wipeThresh = maxAlive * 0.4f;
        if (alive < wipeThresh && _spawnTarget > wipeThresh) _spawnTarget = Mathf.Max(alive, maxAlive * 0.15f);   // wipe → restart the ramp from ~current
        _rampVaryT -= dt; if (_rampVaryT <= 0f) { _rampVaryT = 2.5f + _rng.Randf() * 3f; _rampVary = 0.7f + _rng.Randf() * 0.6f; }   // 0.7..1.3, re-rolled every ~2.5-5.5s
        _spawnTarget = Mathf.MoveToward(_spawnTarget, maxAlive, (maxAlive / 12f) * _rampVary * dt);   // ~12s to refill a full wipe, jittered
        int cap = Mathf.Min(maxAlive, Mathf.FloorToInt(_spawnTarget));
        var pool = CurBiome == Biome.Rainforest ? JungleMobs : GroveMobs;
        int guard = 0;
        while (_credits >= 1f && alive < cap && guard++ < 6)
        {
            float tot = 0f;
            foreach (var mb in pool) if (mb.Unlock <= _tier && mb.Cost <= _credits) tot += mb.W;
            if (tot <= 0f) break;
            float x = _rng.Randf() * tot; StreamMob pick = default; bool got = false;
            foreach (var mb in pool) if (mb.Unlock <= _tier && mb.Cost <= _credits) { x -= mb.W; if (x <= 0f) { pick = mb; got = true; break; } }
            if (!got) break;
            _credits -= pick.Cost; SpawnEnemy(pick.T); alive++;
        }
        if (_credits > 45f) _credits = 45f;   // don't bank forever
    }
    private float _spawnTarget = 0f;                 // (BREATHING ROOM) the ramping concurrent cap (climbs toward maxAlive; restarts low on a big wipe)
    private float _rampVary = 1f, _rampVaryT = 0f;   // drifting refill-rate multiplier for natural spawn-cadence variation
    public void ResetDifficulty() { _diffTime = 0f; _tier = 1f; _credits = 0f; _lastTierInt = 1; Wave = 1; Heat = 1f; _mutatorT = 0f; _takerDesire = 0f; _takerCd = 0f; _phalanxDesire = 0f; _phalanxCd = 0f; _spawnTarget = 0f; _rampVary = 1f; _rampVaryT = 0f; _magnetDropCd = 0f; }

    // ===== SMART SPECIAL-ENEMY DIRECTOR — a "desire score" builds from the situation, then spawns a special (Taker) =====
    // Hard rules unchanged: MP-only, capped at (players-1) alive, and a minimum cooldown so it can never chain. Future specials
    // plug into the same desire model.
    private float _takerDesire = 0f, _takerCd = 0f;
    private void UpdateSpecialDirector(float dt)
    {
        if (!IsAuthority || !SimActive || InMaze || InExpedition || InSky) return;
        RunPhalanxDirector(dt);   // (NEW) the Warded Phalanx is a special too — but unlike the Taker it works solo
        if (NetMgr == null || !NetMgr.Active || WardenCount < 2) return;   // the TAKER is a co-op pressure tool (0 in solo — it kidnaps you, so someone has to rescue)
        _takerCd -= dt;
        if (_tier < 3f) return;   // hold off until things heat up
        int specials = 0; foreach (var e in Enemies) if (e != null && GodotObject.IsInstanceValid(e) && e.IsSpecial) specials++;
        if (specials >= WardenCount - 1) { _takerDesire = Mathf.Min(_takerDesire, 0.5f); return; }   // at the cap → let desire simmer, don't spawn

        float desire = 0.02f;   // baseline creep so it eventually shows up regardless
        bool eventOn = _bossFightActive
                     || Rituals.Exists(r => r != null && GodotObject.IsInstanceValid(r) && r.Active && !r.Done)
                     || _nerfers.Exists(s => s != null && GodotObject.IsInstanceValid(s) && s.State == 1);
        if (eventOn) desire += 0.09f;                         // the party's committed to something → prime time to strike
        if (PartySpread() > 35f) desire += 0.11f;             // spread out → a clean pick-off
        float minHp = Player != null ? Player.Hp / Mathf.Max(1f, Player.S.MaxHp) : 1f;
        minHp = Mathf.Min(minHp, NetMgr.MinAllyHpFrac());
        if (minHp < 0.3f) desire += 0.07f;                    // someone's fragile
        if ((Player != null && Player.Downed) || NetMgr.AnyAllyDowned()) desire += 0.16f;   // someone's down → kick them while they're out
        desire += Mathf.Clamp((_tier - 3f) * 0.004f, 0f, 0.05f);   // harder overall = a touch more pressure

        _takerDesire += desire * dt;
        if (_takerDesire >= 1f && _takerCd <= 0f)
        {
            SpawnEnemy("taker");
            _takerDesire = 0f; _takerCd = 12f;   // minimum breathing room before the next one
        }
    }

    // ===== REFLOW DIRECTOR (NEW) =====
    // The problem: the concurrent cap means a horde that spawns behind you STAYS behind you. Run off to explore and the
    // whole roster is a conga line at your back — nothing ahead, no pressure in the direction you're actually going, and
    // the cap blocks fresh spawns from filling the gap. Catch-up speed can't fix it; a witch who keeps moving is simply
    // faster than the horde. So instead of spawning MORE bodies, we RE-USE the ones that have fallen out of the fight:
    // any foe that's been trailing far behind for a while, and that nobody can currently see, gets picked up and set back
    // down AHEAD of or FLANKING the party. It keeps its exact state — HP, shield, burn, curse, elite/affix, everything —
    // because it's the same node, just moved. Host-authoritative; clients follow via the enemy snapshot (their proxies
    // snap + poof on a big delta rather than sliding, see Enemy's remote branch).
    private float _reflowT = 0f;
    private readonly System.Collections.Generic.Dictionary<long, Vector3> _headLast = new();
    private readonly System.Collections.Generic.Dictionary<long, Vector3> _headDir = new();
    private void RunReflowDirector(float dt)
    {
        if (!IsAuthority || !SimActive || InMaze || InExpedition || InSky) return;
        _reflowT -= dt;
        if (_reflowT > 0f) return;
        const float Step = 0.5f;
        _reflowT = Step;
        SampleHeadings(Step);

        // (NEW) the horde re-forms FASTER the deeper the run gets. At CALM a straggler gets a long leash — you can still
        // outrun a fight and get a breather. By CATACLYSMIC they're on you again within a couple of seconds, from a shorter
        // leash, several at a time: at that point running away should stop being an escape and start being a relocation.
        float t = Mathf.Clamp(DiffStage() / 6f, 0f, 1f);   // 0 at CALM → 1 at CATACLYSMIC and beyond
        float patience = Mathf.Lerp(ReflowPatienceCalm, ReflowPatienceHot, t);
        float near = Mathf.Lerp(ReflowNearCalm, ReflowNearHot, t);
        int budget = Mathf.RoundToInt(Mathf.Lerp(2f, 6f, t));

        int moved = 0;
        for (int i = 0; i < Enemies.Count && moved < budget; i++)
        {
            var e = Enemies[i];
            if (e == null || !GodotObject.IsInstanceValid(e) || !e.Relocatable) continue;
            if (!NearestWarden(e.GlobalPosition, out long peer, out Vector3 wp)) continue;
            var flat = e.GlobalPosition - wp; flat.Y = 0f;
            float d = flat.Length();
            // two ways to fall out of the fight: a long stern chase, or being flat-out abandoned
            bool stale = (d > near && e.ChaseFarT > patience) || d > ReflowFar;
            if (!stale) continue;
            if (SeenLocally(e.GlobalPosition)) continue;   // never blink a foe out from under someone's crosshair
            if (Reflow(e, peer, wp)) moved++;
        }
    }
    private const float ReflowNearCalm = 58f, ReflowNearHot = 40f;         // "trailing" distance, by difficulty stage
    private const float ReflowPatienceCalm = 7f, ReflowPatienceHot = 1.8f; // seconds of trailing before we re-insert it
    private const float ReflowFar = 100f;       // this far out it's abandoned — re-insert immediately, at any difficulty

    // per-player smoothed heading, so re-inserted foes land in the direction each warden is actually travelling
    private void SampleHeadings(float step)
    {
        void Sample(long peer, Vector3 pos)
        {
            if (_headLast.TryGetValue(peer, out var last))
            {
                var v = (pos - last) / Mathf.Max(step, 0.001f); v.Y = 0f;
                if (v.LengthSquared() > 900f) v = Vector3.Zero;   // teleport/dash spike
                _headDir[peer] = _headDir.TryGetValue(peer, out var prev) ? prev.Lerp(v, 0.45f) : v;
            }
            _headLast[peer] = pos;
        }
        if (Player != null) Sample(LocalPeer, Player.GlobalPosition);
        if (NetMgr != null && NetMgr.Active)
            foreach (var (peer, pos) in NetMgr.AllyPeerPositions()) Sample(peer, pos);
    }

    private bool NearestWarden(Vector3 from, out long peer, out Vector3 pos)
    {
        peer = LocalPeer; pos = Vector3.Zero; bool found = false; float bd = float.MaxValue;
        if (Player != null && !Player.Downed) { pos = Player.GlobalPosition; bd = from.DistanceSquaredTo(pos); found = true; }
        if (NetMgr != null && NetMgr.Active)
            foreach (var (p, wp) in NetMgr.AllyPeerPositions())
            { float d = from.DistanceSquaredTo(wp); if (d < bd) { bd = d; pos = wp; peer = p; found = true; } }
        return found;
    }

    // only the HOST's own camera can be frustum-tested; allies are covered by the fact that we already require the foe to
    // be 58u+ from EVERY warden before it's eligible, which puts it at the far edge of readability anyway.
    private bool SeenLocally(Vector3 p)
    {
        var cam = Player?.Cam;
        if (cam == null) return false;
        return cam.IsPositionInFrustum(p) && p.DistanceSquaredTo(cam.GlobalPosition) < 140f * 140f;
    }

    // set the foe down in a fresh arc around its warden: mostly AHEAD of where she's running, the rest on her flanks,
    // never back where it came from. Returns false if no valid ground turned up (it just keeps chasing, no harm done).
    private bool Reflow(Enemy e, long peer, Vector3 wp)
    {
        Vector3 head = _headDir.TryGetValue(peer, out var h) ? h : Vector3.Zero;
        head.Y = 0f;
        float baseAng = head.Length() > 3.5f ? Mathf.Atan2(head.Z, head.X) : _rng.RandfRange(0f, Mathf.Tau);   // standing still → any side

        for (int t = 0; t < 8; t++)
        {
            // 60% cut you off out front (±35°), 40% swing wide onto a flank (±60..110°) — a net, not a wall
            float off = _rng.Randf() < 0.6f
                ? _rng.RandfRange(-0.61f, 0.61f)
                : (_rng.Randf() < 0.5f ? _rng.RandfRange(1.05f, 1.92f) : _rng.RandfRange(-1.92f, -1.05f));
            float ang = baseAng + off;
            float r = _rng.RandfRange(34f, 46f);
            Vector3 cand = new Vector3(wp.X + Mathf.Cos(ang) * r, 0f, wp.Z + Mathf.Sin(ang) * r);
            cand = ClampToWorld(cand, 22f);
            cand.Y = SurfaceHeight(cand, 1e9f);
            if (InWater(cand, cand.Y)) continue;                       // don't strand it in a pond
            if (NearAnyWarden(cand, 24f)) continue;                    // never materialise on top of somebody
            NudgeOutOfStructures(ref cand, e.Radius + 0.8f);           // and never inside a tree/house/keep
            if (NearAnyWarden(cand, 20f)) continue;                    // the nudge could have pushed it into someone

            e.GlobalPosition = new Vector3(cand.X, cand.Y + e.Radius, cand.Z);
            e.ResetChaseFar();
            SpawnPoof(e.GlobalPosition);   // it materialises rather than popping in — reads as intent, not a glitch
            return true;
        }
        return false;
    }

    private bool NearAnyWarden(Vector3 p, float minD)
    {
        float m2 = minD * minD;
        if (Player != null && new Vector2(p.X - Player.GlobalPosition.X, p.Z - Player.GlobalPosition.Z).LengthSquared() < m2) return true;
        if (NetMgr != null && NetMgr.Active)
            foreach (var ap in NetMgr.AllyPositions())
                if (new Vector2(p.X - ap.X, p.Z - ap.Z).LengthSquared() < m2) return true;
        return false;
    }

    // (NEW) THE WARDED PHALANX — the second special, on the same desire model as the Taker but with its own read of the
    // situation. Where the Taker punishes a SPREAD party, the phalanx punishes a PLANTED one: it's a zone-denial siege
    // unit, so it wants to arrive exactly when you're standing still (camping a spot, anchored to a ritual/boss/shrine)
    // or coasting. Unlike the Taker it works solo — nothing about it needs an ally to resolve.
    private float _phalanxDesire = 0f, _phalanxCd = 0f;
    private void RunPhalanxDirector(float dt)
    {
        _phalanxCd -= dt;
        if (_tier < 4f) return;                    // give the run a few minutes before the first formation
        int maxUnits = 1 + (WardenCount - 1) / 2;  // solo/duo 1, trio/quad 2 — they bring 3-8 bodies each, so this stays sane
        int units = 0;
        foreach (var e in Enemies) if (e != null && GodotObject.IsInstanceValid(e) && !e.Dead && e.IsPhalanx) units++;
        if (units >= maxUnits) { _phalanxDesire = Mathf.Min(_phalanxDesire, 0.5f); return; }

        float desire = 0.002f;   // baseline creep — one will show up eventually no matter how you play
        bool anchored = _bossFightActive
                      || Rituals.Exists(r => r != null && GodotObject.IsInstanceValid(r) && r.Active && !r.Done)
                      || _nerfers.Exists(s => s != null && GodotObject.IsInstanceValid(s) && s.State == 1);
        if (anchored) desire += 0.006f;                                        // you're tied to a spot → prime time to deny it
        if (_playerVelSmooth.Length() < 4f) desire += 0.005f;                  // camping / turtling → make them move
        if (Heat > 1.15f) desire += 0.004f;                                    // the party's stomping → escalate
        desire += Mathf.Clamp((_tier - 4f) * 0.0003f, 0f, 0.004f);             // and a slow bleed upward with depth

        _phalanxDesire += desire * dt;
        if (_phalanxDesire >= 1f && _phalanxCd <= 0f)
        {
            SpawnPhalanxUnit(Mathf.Clamp(3 + Mathf.FloorToInt(_tier / 12f), 3, Enemy.MaxArchers));   // deeper runs field a bigger rank
            _phalanxDesire = 0f; _phalanxCd = 60f;   // a hard minimum gap — this unit is an event, not a spawn
        }
    }
    private float PartySpread()
    {
        if (Player == null) return 0f;
        var pos = new System.Collections.Generic.List<Vector3> { Player.GlobalPosition };
        if (NetMgr != null && NetMgr.Active) pos.AddRange(NetMgr.AllyPositions());
        float max = 0f;
        for (int i = 0; i < pos.Count; i++) for (int j = i + 1; j < pos.Count; j++) { float d = pos[i].DistanceTo(pos[j]); if (d > max) max = d; }
        return max;
    }

    private float _savedMusicVol = 0.8f;
    private float _savedSens = 0.0022f;
    public bool DmgNumbers = false;   // floating damage numbers, colored by damage type

    // (NEW) per-machine graphics settings — each player in multiplayer tunes their own for performance.
    public int GfxQuality = 2;        // 0 Low, 1 Med, 2 High
    public int ShadowQuality = 1;     // (NEW) 0 Low / 1 Med / 2 High — INDEPENDENT shadow control (default Med = 2-split; was always 4-split)
    public bool GfxBloom = true;      // glow / bloom
    public bool GfxSsao = true;       // screen-space ambient occlusion
    public bool GfxSsil = true;       // screen-space indirect light (fake GI)
    public int ImpactDecalCap => GfxQuality == 0 ? 8 : GfxQuality == 1 ? 18 : 28;   // fewer ground marks on lower presets
    public float ParticleScale => GfxQuality == 0 ? 0.4f : GfxQuality == 1 ? 0.7f : 1f;   // thinner particle trails on lower presets

    // (NEW) screen / resolution settings (Options → Screen tab)
    public static readonly Vector2I[] ResChoices = { new Vector2I(1280, 720), new Vector2I(1600, 900), new Vector2I(1920, 1080), new Vector2I(2560, 1440) };
    public int WindowMode = 0;   // 0 windowed, 1 borderless fullscreen
    public int ResIndex = 2;     // index into ResChoices (default 1920×1080)
    public bool VSync = true;
    public int ViewDist = 1;     // (NEW) Render Distance: 0 Low, 1 Med, 2 High → World.FarRadius (LOD-ring reach)
    public int TextureQuality = 2;   // (NEW) 0 Low (512), 1 Medium (1k), 2 High (full 2k) — caps ground/rock texture resolution (VRAM). Persisted.
    public void SetTextureQuality(int q) { TextureQuality = Mathf.Clamp(q, 0, 2); World.SetTexQuality(TextureQuality); }
    public int MaxFps = 60;       // (NEW) explicit frame cap (30/60/90/120/144). Persisted. Independent of V-Sync + the Painterly quality.
    public static readonly int[] FpsChoices = { 30, 60, 90, 120, 144 };
    public void SetMaxFps(int fps) { MaxFps = fps; Engine.MaxFps = Mathf.Max(0, fps); }
    public int FarRing => ViewDist <= 0 ? 3 : ViewDist == 1 ? 4 : 5;
    public void SetViewDist(int v) { ViewDist = Mathf.Clamp(v, 0, 2); _world?.RefreshStreaming(); }
    public void DebugGrovePatch(Vector3 c) => _world?.DebugGrovePatch(c);   // (DEV) in-world prop/structure validation patch (grove_showcase scenario)
    public Vector3 DebugSpawnClimbableKeep(Vector3 c) => _world != null ? _world.DebugSpawnClimbableKeep(c) : new Vector3(c.Y, c.X, c.Z);   // (DEV) → (roofY, xStairWorld, stairFarZ)
    public Godot.Collections.Array<Vector3> DebugStructureAudit(Vector3 c) => _world != null ? _world.DebugStructureAudit(c) : new Godot.Collections.Array<Vector3>();
    public void ApplyWindow()
    {
        Engine.MaxFps = Mathf.Max(0, MaxFps);   // (NEW) explicit frame cap (applies whether V-Sync is on or off)
        DisplayServer.WindowSetVsyncMode(VSync ? DisplayServer.VSyncMode.Enabled : DisplayServer.VSyncMode.Disabled);
        if (WindowMode == 1) { DisplayServer.WindowSetMode(DisplayServer.WindowMode.Fullscreen); return; }
        DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
        int scr = DisplayServer.WindowGetCurrentScreen();
        var usable = DisplayServer.ScreenGetUsableRect(scr);   // excludes the taskbar
        var r = ResChoices[Mathf.Clamp(ResIndex, 0, ResChoices.Length - 1)];
        r = new Vector2I(Mathf.Min(r.X, usable.Size.X), Mathf.Min(r.Y, usable.Size.Y));   // never bigger than the screen
        DisplayServer.WindowSetSize(r);
        DisplayServer.WindowSetPosition(usable.Position + (usable.Size - r) / 2);          // centered in the work area
    }

    // Toggles are authoritative — ApplyGraphics uses the individual flags directly, so a user can override any
    // single effect regardless of preset. Picking a preset just sets sensible defaults for those flags.
    public void ApplyGraphics()
    {
        ApplyShadowQuality();
        if (_env == null) return;
        _env.GlowEnabled = GfxBloom;
        _env.SsaoEnabled = GfxSsao;
        _env.SsilEnabled = GfxSsil;
    }
    // (PERF) the directional-shadow pass is a top GPU cost (it redraws every shadow-caster per cascade). Scale it with quality:
    // High = 4-split @ 85m (crisp), Med = 2-split @ 55m (halves the cascade passes), Low = orthogonal 1-split @ 35m (cheapest).
    private void ApplyShadowQuality()
    {
        if (_sun == null) return;
        _sun.DirectionalShadowMode = ShadowQuality >= 2 ? DirectionalLight3D.ShadowMode.Parallel4Splits
                                    : ShadowQuality == 1 ? DirectionalLight3D.ShadowMode.Parallel2Splits
                                    : DirectionalLight3D.ShadowMode.Orthogonal;
        _sun.DirectionalShadowMaxDistance = ShadowQuality >= 2 ? 85f : ShadowQuality == 1 ? 55f : 35f;
    }
    public void SetGfxQuality(int q)
    {
        GfxQuality = Mathf.Clamp(q, 0, 2);
        GfxBloom = GfxQuality >= 1;   // preset defaults; each toggle can still be flipped individually
        GfxSsao = GfxQuality >= 2;
        GfxSsil = GfxQuality >= 2;
        ShadowQuality = GfxQuality;   // (NEW) the master preset sets a matching shadow level; the separate Shadows control below overrides it
        ApplyGraphics();
    }
    public void SetShadowQuality(int q) { ShadowQuality = Mathf.Clamp(q, 0, 2); ApplyShadowQuality(); }   // (NEW) independent shadow control

    public void SaveGold()
    {
        var cfg = new ConfigFile();
        cfg.SetValue("progress", "gold", Gold);
        cfg.SetValue("options", "musicvol", Sfx != null ? Sfx.MusicVol : _savedMusicVol);
        cfg.SetValue("options", "sens", Player != null ? Player.MouseSens : _savedSens);
        cfg.SetValue("options", "padsens", Player.PadSensMul);   // static — always current
        cfg.SetValue("options", "dmgnumbers", DmgNumbers);
        cfg.SetValue("options", "gfxquality", GfxQuality);
        cfg.SetValue("options", "gfxbloom", GfxBloom);
        cfg.SetValue("options", "gfxssao", GfxSsao);
        cfg.SetValue("options", "gfxssil", GfxSsil);
        cfg.SetValue("options", "windowmode", WindowMode);
        cfg.SetValue("options", "resindex", ResIndex);
        cfg.SetValue("options", "vsync", VSync);
        cfg.SetValue("options", "viewdist", ViewDist);
        cfg.SetValue("options", "shadowquality", ShadowQuality);
        cfg.SetValue("options", "maxfps", MaxFps);
        cfg.SetValue("options", "texquality", TextureQuality);
        Perks.Save(cfg);   // (NEW) persist the coven perk trees (owned + equipped per witch)
        MetaUnlocks.Save(cfg);   // (NEW) persist the general gold meta-tree (+fin / +mod / +mana)
        cfg.Save("user://grove_save.cfg");
    }

    public void SavePerks() => SaveGold();   // (NEW) perk buy/equip changes persist (gold + perk sets are both written by SaveGold)

    private void LoadGold()
    {
        var cfg = new ConfigFile();
        if (cfg.Load("user://grove_save.cfg") == Error.Ok)
        {
            Gold = cfg.GetValue("progress", "gold", 0).AsInt32();
            _savedMusicVol = (float)cfg.GetValue("options", "musicvol", 0.8f).AsDouble();
            _savedSens = (float)cfg.GetValue("options", "sens", 0.0022f).AsDouble();
            Player.PadSensMul = (float)cfg.GetValue("options", "padsens", 1f).AsDouble();   // static; applies immediately
            DmgNumbers = cfg.GetValue("options", "dmgnumbers", false).AsBool();
            GfxQuality = cfg.GetValue("options", "gfxquality", 2).AsInt32();
            GfxBloom = cfg.GetValue("options", "gfxbloom", true).AsBool();
            GfxSsao = cfg.GetValue("options", "gfxssao", true).AsBool();
            GfxSsil = cfg.GetValue("options", "gfxssil", true).AsBool();
            WindowMode = cfg.GetValue("options", "windowmode", 0).AsInt32();
            ResIndex = cfg.GetValue("options", "resindex", 2).AsInt32();
            VSync = cfg.GetValue("options", "vsync", true).AsBool();
            ViewDist = cfg.GetValue("options", "viewdist", 1).AsInt32();
            ShadowQuality = cfg.GetValue("options", "shadowquality", 1).AsInt32();
            MaxFps = cfg.GetValue("options", "maxfps", 60).AsInt32();          // default 60
            TextureQuality = cfg.GetValue("options", "texquality", 2).AsInt32();   // default High
            ApplyGraphics();   // no-op if the environment isn't built yet; BuildWorld re-applies
            ApplyWindow();
            Perks.Load(cfg);   // (NEW) restore the coven perk trees
            MetaUnlocks.Load(cfg);   // (NEW) restore the general gold meta-tree
        }
        else
        {
            Perks.Load(cfg);   // still load perks even on a fresh save (empty → no-op)
        }
    }

    public void SetMusicVol(float v) { if (Sfx != null) Sfx.MusicVol = Mathf.Clamp(v, 0f, 1f); }
    public float SensSlider => Player != null ? Mathf.InverseLerp(0.0006f, 0.005f, Player.MouseSens) : 0.4f;
    public void SetSensitivity(float v) { if (Player != null) Player.MouseSens = Mathf.Lerp(0.0006f, 0.005f, Mathf.Clamp(v, 0f, 1f)); }
    public float PadSensSlider => Mathf.InverseLerp(0.3f, 1.9f, Player.PadSensMul);   // PadSensMul is static → valid even before a Player exists
    public void SetPadSensitivity(float v) { Player.PadSensMul = Mathf.Lerp(0.3f, 1.9f, Mathf.Clamp(v, 0f, 1f)); }

    public void GameOver()
    {
        if (State == GameState.Over) return;
        State = GameState.Over;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        if (Player != null) MyStats.BestCombo = Player.BestCombo;   // (NEW) final combo record
        AllStats[LocalPeer] = MyStats;      // (NEW) publish this warden's PERSONAL tally to the scoreboard
        NetMgr?.BroadcastRunStats(MyStats);
        if (NetMgr != null && NetMgr.Active && NetMgr.IsHost) NetMgr.BroadcastKillTally();   // host is the ONE source of truth for kills
    }

    // (NEW) MP game-over: the host's choice, applied on every peer. 0 = char-select, 1 = retry same witches, 2 = end session.
    public void ApplyGameOverChoice(int choice)
    {
        if (choice == 2)   // End Game — tear down the session and return to the home screen (scene reload is fine, we're ending)
        {
            NetMgr?.Disconnect();
            s_witch = -1;
            GetTree().ReloadCurrentScene();
            return;
        }
        SoftResetRun();
        if (choice == 0) GoCharSelect();   // back to character select — the ready-gate runs again
        else StartGame();                  // retry with the witches everyone already has (each peer keeps its own s_witch)
    }

    // Reset the RUN in place — clears the world's entities + each warden's progress WITHOUT reloading the scene, so the
    // network session survives (a scene reload would drop everyone). Used only by the MP game-over flow.
    public void SoftResetRun()
    {
        foreach (var e in Enemies.ToArray()) if (GodotObject.IsInstanceValid(e)) e.QueueFree();
        Enemies.Clear(); _toSpawn.Clear();
        foreach (var o in Orbs.ToArray()) if (GodotObject.IsInstanceValid(o)) o.QueueFree(); Orbs.Clear();
        foreach (var e in Effigies.ToArray()) if (GodotObject.IsInstanceValid(e)) e.QueueFree(); Effigies.Clear(); _effigiesSpawned = false; System.Array.Clear(_effigyActivations, 0, _effigyActivations.Length);   // (EFFIGY) new run → reset shrines + cost tiers
        foreach (var r in Rituals.ToArray()) if (GodotObject.IsInstanceValid(r)) r.QueueFree(); Rituals.Clear();
        foreach (var c in Chests.ToArray()) if (GodotObject.IsInstanceValid(c)) c.QueueFree(); Chests.Clear();
        if (_bossLair != null && GodotObject.IsInstanceValid(_bossLair)) { _bossLair.QueueFree(); _bossLair = null; }   // (BOSS-LAIR) reset
        _bossFightActive = false; WorldBossDown = false;
        ClearDiscovered();   // (NEW) reset the fog of war
        Wave = 0; Heat = 1f; Score = 0; _waveTimer = 0f; _magnetT = 0f; ActiveMutator = WaveMutator.None; ResetDifficulty();
        _started = false; _ultOffered = false; ReadyCount = 0; NetMgr?.ResetReady();
        if (Player != null)
        {
            Player.Fin.Clear(); Player.Mods.Clear(); Player.Minors.Clear();
            Player.S = new Stats();
            Player.DamageMul = 1f; Player.NightAffinity = false; Player.Interventions = 0;
            Player.DivineWitch = Player.CrimsonWitch = Player.VerdantWitch = Player.GaleWitch = Player.FrostWitch = Player.ForsakenWitch = Player.EmberWitch = Player.ArcaneWitch = false;
            Player.Level = 1; Player.Xp = 0f; Player.XpNext = 26f; Player.Combo = 0; Player.BestCombo = 0;
            Player.Ult = Player.UltKind.None; Player.UltCharge = 0f; Player.UltActive = false;
            Player.UltTier = 0; Player.UltTiers.Clear(); _ultOfferCount = 0;   // (ULT CARDS) fresh witch/run → forget persisted ult tiers + pity
            // ult MODS are per-ult booleans that now persist across a swap, so clear them here on a fresh witch/run (this used to live in ChooseUlt)
            Player.ModEclipse = Player.ModLight = Player.ModCrescent = Player.ModShield = Player.ModJudge = Player.ModDivinity = false;
            Player.ModTsunami = Player.ModExsang = Player.ModRot = Player.ModGuardian = Player.ModSwarm = Player.ModBark = false;
            Player.ModPlague = Player.ModRapture = Player.ModRite = Player.ModArcStorm = Player.ModArcCataclysm = Player.ModArcUnbound = false;
            Player.Downed = false; Player.ReviveProg = 0f;
            NetMgr?.LocalDowned(false);   // clear my downed avatar on everyone else + the host's tally
        }
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
