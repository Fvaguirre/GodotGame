using Godot;
using System.Collections.Generic;

// Phase-2 networking foundation: LAN host/join (no password), and live position sync so
// players can see each other move. World state (enemies/waves/XP) is NOT yet shared — that's phase 3.
// Net.cs — ALL multiplayer. LAN ENet, up to 4 players, port 7777. The model is HOST-OWNS-WORLD /
// CLIENT-OWNS-AVATAR (see DEV_GUIDE.md §3). _Process runs a ~20 Hz (SendHz) broadcast loop: each peer
// sends its own NetState (pos/yaw/floating) + periodic NetVitals + (Verdant) MinionSnapshot; the host
// additionally sends EnemySnapshot. Receivers reconcile proxies: _remotes (RemoteAvatar per ally),
// _renemies (Enemy proxies), _ghostEnts (Thornling ghosts), remote bolts (ReceivePBolt), remote VFX
// (ReceiveVfx).
//
// DAMAGE ROUTING: clients send hits to the host via ReportHit; the host applies them and the result
// returns in the next EnemySnapshot. Enemy->player damage uses DamagePlayer/ReceivePlayerDamage.
// Anything new that damages, heals, or is visual must use these paths or it desyncs. Clean up proxies
// in OnPeerDisconnected when you add a new per-peer collection.
public partial class Net : Node
{
    public const int DefaultPort = 7777;

    public bool Active = false;
    public bool IsHost = false;
    public string Status = "offline";

    private readonly Dictionary<long, RemoteAvatar> _remotes = new();
    private readonly Dictionary<long, System.Collections.Generic.List<Thornling>> _ghostEnts = new();
    private readonly Dictionary<long, Guardian> _ghostGuardians = new();   // one Ancient Guardian per ally
    private int _minTick = 0;
    // (MP FIX) enemies that ACTUALLY died on the host since the last snapshot. Absence from a snapshot used to be the
    // only despawn signal, but the packet is capped at 30 foes — so every foe pushed out by the cap was destroyed and
    // rebuilt on each client, which is why the boss blinked out and why heavy fights churned proxies.
    private readonly List<int> _deadIds = new();
    public void NoteEnemyDeath(int netId) { if (Active && IsHost && netId != 0) _deadIds.Add(netId); }
    private readonly Dictionary<int, ulong> _enemySeen = new();   // client: last snapshot that mentioned each proxy
    private const ulong ProxyStaleMs = 12000;                     // safety net if a despawn ever goes missing
    private int _crescTick = 0; private bool _hadCrescents = false;   // (NEW) crescent orb position sync
    private readonly System.Collections.Generic.Dictionary<long, Cyclone> _hurriGhosts = new();   // (NEW) per-caster hurricane funnel ghosts
    private readonly System.Collections.Generic.Dictionary<long, FrostElemental> _frostElemGhosts = new();   // (NEW) per-caster frost elemental ghosts

    public void BroadcastFrostElemMove(Vector3 pos) { if (!Active) return; Rpc(nameof(ReceiveFrostElemMove), pos.X, pos.Y, pos.Z); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveFrostElemMove(float x, float y, float z)
    {
        long sp = Multiplayer.GetRemoteSenderId();
        if (_frostElemGhosts.TryGetValue(sp, out var fe) && GodotObject.IsInstanceValid(fe)) fe.GlobalPosition = new Vector3(x, y, z);
    }

    // player ping → everyone sees the marker + a radar blip
    public void BroadcastPing(Vector3 pos, string name, Color col) { if (!Active) return; Rpc(nameof(ReceivePing), pos.X, pos.Y, pos.Z, name ?? "", col.R, col.G, col.B); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceivePing(float x, float y, float z, string name, float r, float g, float b)
    {
        Game.I?.SpawnPing(new Vector3(x, y, z), name, new Color(r, g, b), net: false);
    }

    // a player cast — fire the sender's ally avatar's upper-body cast animation (arms cast while legs keep moving)
    public void BroadcastCast() { if (!Active) return; Rpc(nameof(ReceiveCast)); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    private void ReceiveCast()
    {
        if (_remotes.TryGetValue(Multiplayer.GetRemoteSenderId(), out var av)) av.Cast();
    }

    // move a caster's hurricane funnel each frame so allies see it track (not sit static at the cast spot)
    public void BroadcastHurriMove(Vector3 pos) { if (!Active) return; Rpc(nameof(ReceiveHurriMove), pos.X, pos.Y, pos.Z); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveHurriMove(float x, float y, float z)
    {
        long sp = Multiplayer.GetRemoteSenderId();
        if (_hurriGhosts.TryGetValue(sp, out var cy) && GodotObject.IsInstanceValid(cy)) cy.GlobalPosition = new Vector3(x, y, z);
    }
    private readonly Dictionary<int, Enemy> _renemies = new();
    private readonly Dictionary<int, RemotePickup> _rpickups = new();
    private readonly Dictionary<int, RitualCircle> _rituals = new();
    private readonly Dictionary<int, Node3D> _rvendors = new();
    private readonly HashSet<int> _claimedVendors = new();   // vendors this client used; don't respawn while host still reports them
    private readonly Dictionary<int, RouletteMachine> _rroulettes = new();
    private readonly HashSet<int> _claimedRoulettes = new();
    private readonly Dictionary<long, System.Collections.Generic.List<CrescentOrb>> _rcrescents = new();
    private readonly Dictionary<long, int> _peerCat = new();   // host: each ally's pause category
    private float _sendT = 0f;
    private int _vitalsTick = 0;
    private readonly Dictionary<long, float> _allyHealAccum = new();
    private float _allyHealFlush = 0f;
    private readonly System.Collections.Generic.List<Vector3> _deathQueue = new();
    private float _deathFlush = 0f;
    private float _teamDmgFlush = 0f;
    private float _enemyT = 0f;
    private float _stateT = 0f;
    private const float SendHz = 20f;
    private const float EnemyHz = 15f;
    public static float NetHz => EnemyHz;

    // --- perf overlay telemetry (last snapshot tick), read by the HUD's dev overlay ---
    public int NetEnemiesSynced, NetOrbsSynced, NetEnemyBytes, NetPickupBytes;

    // toggle the dev perf overlay for the WHOLE lobby — server relay fans this out to every peer
    public void BroadcastPerfOverlay(bool on)
    {
        if (Game.I != null) Game.I.PerfOverlay = on;
        bool connected = Multiplayer.MultiplayerPeer != null
            && Multiplayer.MultiplayerPeer.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connected
            && Multiplayer.GetPeers().Length > 0;
        if (connected) Rpc(nameof(ReceivePerfOverlay), on);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceivePerfOverlay(bool on) { if (Game.I != null) Game.I.PerfOverlay = on; }

    public void ForEachPeerCat(System.Action<int> act) { foreach (var c in _peerCat.Values) act(c); }

    public override void _Ready()
    {
        Multiplayer.PeerConnected += OnPeerConnected;
        Multiplayer.PeerDisconnected += OnPeerDisconnected;
        Multiplayer.ConnectedToServer += OnConnectedToServer;
        Multiplayer.ConnectionFailed += OnConnectionFailed;
        Multiplayer.ServerDisconnected += OnServerDisconnected;
    }

    public string HostGame(int port)
    {
        var peer = new ENetMultiplayerPeer();
        var err = peer.CreateServer(port, 4);
        if (err != Error.Ok) { Status = "host failed: " + err; return Status; }
        Multiplayer.MultiplayerPeer = peer;
        Active = true; IsHost = true;
        Status = $"hosting on port {port} \u2014 waiting for players";
        return Status;
    }

    public string JoinGame(string ip, int port)
    {
        var peer = new ENetMultiplayerPeer();
        var err = peer.CreateClient(string.IsNullOrEmpty(ip) ? "127.0.0.1" : ip, port);
        if (err != Error.Ok) { Status = "join failed: " + err; return Status; }
        Multiplayer.MultiplayerPeer = peer;
        Active = true; IsHost = false;
        Status = "connecting\u2026";
        return Status;
    }

    public void Disconnect()
    {
        foreach (var kv in _remotes) if (GodotObject.IsInstanceValid(kv.Value)) kv.Value.QueueFree();
        _remotes.Clear();
        foreach (var kv in _renemies) if (GodotObject.IsInstanceValid(kv.Value)) { Game.I?.Enemies.Remove(kv.Value); kv.Value.QueueFree(); }
        _renemies.Clear();
        foreach (var kv in _rpickups) if (GodotObject.IsInstanceValid(kv.Value)) kv.Value.QueueFree();
        _rpickups.Clear();
        foreach (var kv in _rituals) if (GodotObject.IsInstanceValid(kv.Value)) { Game.I?.Rituals.Remove(kv.Value); kv.Value.QueueFree(); }
        _rituals.Clear();
        foreach (var kv in _rvendors) if (GodotObject.IsInstanceValid(kv.Value)) kv.Value.QueueFree();
        _rvendors.Clear();
        _claimedVendors.Clear();
        foreach (var kv in _rroulettes) if (GodotObject.IsInstanceValid(kv.Value)) { Game.I?.RemoveRemoteRoulette(kv.Value); kv.Value.QueueFree(); }
        _rroulettes.Clear();
        _claimedRoulettes.Clear();
        foreach (var kv in _rcrescents) foreach (var o in kv.Value) if (GodotObject.IsInstanceValid(o)) o.QueueFree();
        _rcrescents.Clear();
        _downed.Clear();
        if (Multiplayer.MultiplayerPeer != null) { Multiplayer.MultiplayerPeer.Close(); Multiplayer.MultiplayerPeer = null; }
        Active = false; IsHost = false; Status = "offline";
    }

    // ---- shared pause model ----
    // client -> host: report my pause category each tick; host -> all: the resulting world-run flag
    public void ReportCat(int cat)
    {
        if (!Active || IsHost) return;
        RpcId(1, nameof(ReceiveCat), cat);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveCat(int cat)
    {
        if (!IsHost) return;
        _peerCat[Multiplayer.GetRemoteSenderId()] = cat;
    }
    public void BroadcastWorldRunning(bool run)
    {
        bool connected = Multiplayer.MultiplayerPeer != null
            && Multiplayer.MultiplayerPeer.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connected
            && Multiplayer.GetPeers().Length > 0;
        if (IsHost && connected) Rpc(nameof(ReceiveWorldRunning), run);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    private void ReceiveWorldRunning(bool run) { if (!IsHost && Game.I != null) Game.I.WorldRunning = run; }

    // host -> all: shared XP gain
    public void BroadcastXp(float amt)
    {
        if (!Active || !IsHost) return;
        Rpc(nameof(ReceiveXp), amt);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveXp(float amt) { if (!IsHost) Game.I?.LocalPlayer?.AddXp(amt); }

    // host -> all: award the same gold to EVERY warden (portal chest = 150, maze ritual = 300). Host applies locally too.
    public void BroadcastGoldAll(int amt)
    {
        if (Game.I != null) Game.I.AddGold(amt);
        if (Active && IsHost) Rpc(nameof(ReceiveGoldAll), amt);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveGoldAll(int amt) { if (!IsHost && Game.I != null) Game.I.AddGold(amt); }

    // host -> all: the Grove's garden portals + maze gate (one-shot, reliable). Clients build synced ghosts.
    public void BroadcastGarden(int[] ids, int[] pairs, float[] px, float[] py, float[] pz, float[] lx, float[] ly, float[] lz, int[] kinds, int[] entr, int[] cols, float gx, float gy, float gz, int gcol)
    {
        if (Active && IsHost) Rpc(nameof(ReceiveGarden), ids, pairs, px, py, pz, lx, ly, lz, kinds, entr, cols, gx, gy, gz, gcol);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveGarden(int[] ids, int[] pairs, float[] px, float[] py, float[] pz, float[] lx, float[] ly, float[] lz, int[] kinds, int[] entr, int[] cols, float gx, float gy, float gz, int gcol)
    {
        if (IsHost) return;
        Game.I?.ApplyGardenSync(ids, pairs, px, py, pz, lx, ly, lz, kinds, entr, cols, gx, gy, gz, gcol);
    }

    // client -> host: I stepped through an ambush portal / into the maze gate — host resolves it for everyone.
    public void RequestGardenAmbush(int pair, Vector3 at)
    {
        if (!Active || IsHost) return;
        RpcId(1, nameof(ReceiveGardenAmbush), pair, at.X, at.Y, at.Z);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveGardenAmbush(int pair, float x, float y, float z) { if (IsHost) Game.I?.HostGardenAmbush(pair, new Vector3(x, y, z)); }
    public void RequestEnterMaze()
    {
        if (!Active || IsHost) return;
        RpcId(1, nameof(ReceiveReqEnterMaze));
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveReqEnterMaze() { if (IsHost) Game.I?.EnterGardenMaze(); }

    // maze ritual: host -> all start / end / per-tick (timer + veil), and client -> host statue interaction
    public void BroadcastRitualStart(int cellX, int cellY) { if (Active && IsHost) Rpc(nameof(ReceiveRitualStart), cellX, cellY); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveRitualStart(int cellX, int cellY) { if (!IsHost) Game.I?.ApplyRitualStart(cellX, cellY); }
    public void BroadcastRitualEnd(int reason) { if (Active && IsHost) Rpc(nameof(ReceiveRitualEnd), reason); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveRitualEnd(int reason) { if (!IsHost) Game.I?.ApplyRitualEnd(reason); }
    public void BroadcastRitualTick(float timeLeft, float veilR) { if (Active && IsHost) Rpc(nameof(ReceiveRitualTick), timeLeft, veilR); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    private void ReceiveRitualTick(float timeLeft, float veilR) { if (!IsHost) Game.I?.ApplyRitualTick(timeLeft, veilR); }
    public void RequestStatue() { if (!Active || IsHost) return; RpcId(1, nameof(ReceiveStatue)); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveStatue() { if (IsHost) Game.I?.CompleteRitual(); }

    // host -> a specific ally: a chest reward they earned (0 gold, 1 ult charge, 2 a card)
    public void GiveReward(long peer, int kind, float amt)
    {
        if (!Active || !IsHost) return;
        RpcId(peer, nameof(ReceiveReward), kind, amt);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveReward(int kind, float amt)
    {
        if (IsHost || Game.I == null) return;
        var pl = Game.I.LocalPlayer;
        if (kind == 0) { Game.I.AddGold((int)amt); Game.I.Hud?.Banner("gold!"); }
        else if (kind == 1)
        {
            if (pl != null && pl.Ult != Player.UltKind.None && !pl.UltActive) { pl.UltCharge = Mathf.Min(1f, pl.UltCharge + amt); Game.I.Hud?.Banner("ultimate charge!"); }
            else { Game.I.AddGold((int)((10 + Game.I.Wave * 4) * 0.7f)); Game.I.Hud?.Banner("gold!"); }
        }
        else if (kind == 2) { Game.I.OpenChestCard(); }
        else if (kind == 3) { pl?.FillArmorRandom(); Game.I.Hud?.Banner("armor charges — fully warded!"); }
    }

    // host -> a specific ally: you dealt damage to a slain enemy, so you earn `count` soul(s). Souls are per-player
    // (each machine tracks its own), so the host awards its own locally and RPCs every OTHER contributor theirs.
    public void GrantSoul(long peer, int count)
    {
        if (!Active || !IsHost || count <= 0) return;
        RpcId(peer, nameof(ReceiveSoul), count);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveSoul(int count)
    {
        if (IsHost || Game.I == null) return;
        Game.I.Souls += count;
    }

    // client -> host: I held E on this ritual circle (I've checked my own souls). Host begins the rite + bills me.
    public void RequestRitual(int netId)
    {
        if (!Active || IsHost) return;
        RpcId(1, nameof(ReceiveRitualRequest), netId);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveRitualRequest(int netId)
    {
        if (!IsHost || Game.I == null) return;
        Game.I.HostBeginRitual(netId, Multiplayer.GetRemoteSenderId());
    }
    // host -> the activating ally: your rite has begun, pay the quoted souls
    public void GrantRitual(long peer, int cost)
    {
        if (!Active || !IsHost) return;
        RpcId(peer, nameof(ReceiveRitualCharged), cost);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveRitualCharged(int cost)
    {
        if (IsHost || Game.I == null) return;
        Game.I.ClientRitualCharged(cost);
    }

    // ---- (BOSS LAIR) sync: host → all for the lair's placement + state; client → host to challenge it ----
    public void BroadcastBossLair(Vector3 pos, int netId) { if (Active && IsHost) Rpc(nameof(ReceiveBossLair), pos.X, pos.Y, pos.Z, netId); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveBossLair(float x, float y, float z, int netId) { if (!IsHost) Game.I?.SetRemoteBossLair(new Vector3(x, y, z), netId); }

    public void BroadcastBossLairState(int state) { if (Active && IsHost) Rpc(nameof(ReceiveBossLairState), state); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveBossLairState(int state) { if (!IsHost) Game.I?.SetRemoteBossLairState(state); }

    public void RequestChallengeBoss() { if (!Active || IsHost) return; RpcId(1, nameof(ReceiveChallengeBoss)); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveChallengeBoss() { if (IsHost) Game.I?.HostSummonBoss(); }

    public void BroadcastBossSummon() { if (Active && IsHost) Rpc(nameof(ReceiveBossSummon)); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveBossSummon() { if (!IsHost) { Game.I?.Hud?.Banner("THE LAIR AWAKENS — the boss emerges!"); Game.I?.Sfx?.Thunder(); } }

    // ---- (NERFER SHRINES) sync ----
    public void BroadcastNerfers(System.Collections.Generic.List<NerfShrine> list, int uses)
    {
        if (!Active || !IsHost) return;
        var k = new System.Collections.Generic.List<int>(); var id = new System.Collections.Generic.List<int>();
        var px = new System.Collections.Generic.List<float>(); var py = new System.Collections.Generic.List<float>(); var pz = new System.Collections.Generic.List<float>();
        foreach (var s in list) if (s != null && GodotObject.IsInstanceValid(s)) { k.Add((int)s.Kind); id.Add(s.NetId); px.Add(s.GlobalPosition.X); py.Add(s.GlobalPosition.Y); pz.Add(s.GlobalPosition.Z); }
        Rpc(nameof(ReceiveNerfers), k.ToArray(), id.ToArray(), px.ToArray(), py.ToArray(), pz.ToArray(), uses);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveNerfers(int[] kinds, int[] ids, float[] px, float[] py, float[] pz, int uses) { if (!IsHost) Game.I?.SetRemoteNerfers(kinds, ids, px, py, pz, uses); }

    // ---- (GALE PADS) sync: host → all, positions + aimed yaws ----
    public void BroadcastGalePads(int[] ids, float[] px, float[] pz, float[] yaw) { if (Active && IsHost) Rpc(nameof(ReceiveGalePads), ids, px, pz, yaw); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveGalePads(int[] ids, float[] px, float[] pz, float[] yaw) { if (!IsHost) Game.I?.SetRemoteGalePads(ids, px, pz, yaw); }

    // ---- (PEDESTALS) sync: host → all, positions (Y is re-grounded client-side) ----
    public void BroadcastPedestals(int[] ids, float[] px, float[] pz) { if (Active && IsHost) Rpc(nameof(ReceivePedestals), ids, px, pz); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceivePedestals(int[] ids, float[] px, float[] pz) { if (!IsHost) Game.I?.SetRemotePedestals(ids, px, pz); }

    // ---- (MAGNET DROPS) sync: host → all on spawn + on pickup ----
    public void BroadcastMagnetSpawn(int netId, float x, float z) { if (Active && IsHost) Rpc(nameof(ReceiveMagnetSpawn), netId, x, z); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveMagnetSpawn(int netId, float x, float z) { if (!IsHost) Game.I?.SetRemoteMagnetSpawn(netId, x, z); }
    public void BroadcastMagnetTaken(int netId, float x, float y, float z) { if (Active && IsHost) Rpc(nameof(ReceiveMagnetTaken), netId, x, y, z); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveMagnetTaken(int netId, float x, float y, float z) { if (!IsHost) Game.I?.ClientMagnetTaken(netId, new Vector3(x, y, z)); }

    // (NEW) ward plating — same one-shot pattern as the lodestone, plus a targeted grant so ONLY the warden who
    // stepped on it gets their armor filled
    public void BroadcastWardArmorSpawn(int netId, float x, float z) { if (Active && IsHost) Rpc(nameof(ReceiveWardArmorSpawn), netId, x, z); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveWardArmorSpawn(int netId, float x, float z) { if (!IsHost) Game.I?.SetRemoteWardArmorSpawn(netId, x, z); }
    public void BroadcastWardArmorTaken(int netId, float x, float y, float z) { if (Active && IsHost) Rpc(nameof(ReceiveWardArmorTaken), netId, x, y, z); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveWardArmorTaken(int netId, float x, float y, float z) { if (!IsHost) Game.I?.ClientWardArmorTaken(netId, new Vector3(x, y, z)); }
    public void GrantArmorFill(long peer) { if (Active && IsHost && peer != 0) RpcId(peer, nameof(ReceiveArmorFill)); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveArmorFill() { Game.I?.LocalPlayer?.FillArmorRandom(); Game.I?.Hud?.Banner("ward plating — armor restored"); }

    // (HAUNT) the roaming hot-zone — host announces spawn / break / fill so clients render the zone, meter and payout
    public void BroadcastHaunt(Vector3 c, float radius) { if (Active && IsHost) Rpc(nameof(ReceiveHaunt), c, radius); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveHaunt(Vector3 c, float radius) { if (!IsHost) Game.I?.SetRemoteHaunt(c, radius); }
    public void BroadcastHauntBreak(Vector3 c) { if (Active && IsHost) Rpc(nameof(ReceiveHauntBreak), c); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveHauntBreak(Vector3 c) { if (!IsHost) Game.I?.ClientHauntBreak(c); }
    // (FIX) the break payout goes to EVERY player, not just the host — host grants each client the same souls + gold
    public void GrantAllHauntBreak(int souls, int gold) { if (Active && IsHost) Rpc(nameof(ReceiveHauntBreakReward), souls, gold); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveHauntBreakReward(int souls, int gold) { if (!IsHost) Game.I?.GrantHauntBreakReward(souls, gold); }
    // (HAUNT STORM) a lightning strike the host just placed — clients render the telegraph + bolt, host owns the damage.
    // Unreliable: at the top difficulty this is ~1/s, and a dropped strike costs a client one flash, never a desync
    // (nothing about the world state depends on the client having seen it).
    public void BroadcastHauntBolt(Vector3 at, float radius) { if (Active && IsHost) Rpc(nameof(ReceiveHauntBolt), at, radius); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
    private void ReceiveHauntBolt(Vector3 at, float radius) { if (!IsHost) Game.I?.SpawnRemoteHauntBolt(at, radius); }

    public void BroadcastHauntFill(float f) { if (Active && IsHost) Rpc(nameof(ReceiveHauntFill), f); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    private void ReceiveHauntFill(float f) { if (!IsHost) Game.I?.SetRemoteHauntFill(f); }

    public void BroadcastNerferState(int netId, int state) { if (Active && IsHost) Rpc(nameof(ReceiveNerferState), netId, state); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveNerferState(int netId, int state) { if (!IsHost) Game.I?.SetRemoteNerferState(netId, state); }

    // (NERFER TOLL) a client paid its soul share → host tallies it and fires the shrine once everyone has paid
    public void RequestNerferPay(int netId) { if (!Active || IsHost) return; RpcId(1, nameof(ReceiveNerferPay), netId); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveNerferPay(int netId) { if (IsHost) Game.I?.HostNerferPaid(netId, Multiplayer.GetRemoteSenderId()); }

    public void BroadcastNerferPaid(int paid, int uses) { if (Active && IsHost) Rpc(nameof(ReceiveNerferPaid), paid, uses); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveNerferPaid(int paid, int uses) { if (!IsHost) Game.I?.SetRemoteNerferPaid(paid, uses); }

    // (NERFER Summoner) the host owns the hold-the-circle clock — clients just render what it streams
    public void BroadcastSummonerTick(float timeLeft, bool held) { if (Active && IsHost) Rpc(nameof(ReceiveSummonerTick), timeLeft, held); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    private void ReceiveSummonerTick(float timeLeft, bool held) { if (!IsHost) Game.I?.SetRemoteSummonerTick(timeLeft, held); }

    public void BroadcastSacrificeCost() { if (Active && IsHost) Rpc(nameof(ReceiveSacrificeCost)); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveSacrificeCost() { if (!IsHost) Game.I?.ClientSacrificeCost(); }

    public void BroadcastSanctuaryArmed() { if (Active && IsHost) Rpc(nameof(ReceiveSanctuaryArmed)); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveSanctuaryArmed() { if (!IsHost) Game.I?.ClientSanctuaryArmed(); }

    // ---- (NERFER Sacrifice) THE CRIMSON RITE: sigil set → per-sigil fill → the pentagram fires → the detonation + stall ----
    // Charging is host-authoritative (it reads every warden's position), so clients only ever render what the host reports.
    public void BroadcastRiteSigils(int[] ids, float[] px, float[] pz) { if (Active && IsHost) Rpc(nameof(ReceiveRiteSigils), ids, px, pz); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveRiteSigils(int[] ids, float[] px, float[] pz) { if (!IsHost) Game.I?.SetRemoteRiteSigils(ids, px, pz); }

    public void BroadcastRiteCharge(int netId, float charge, bool lit) { if (Active && IsHost) Rpc(nameof(ReceiveRiteCharge), netId, charge, lit); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    private void ReceiveRiteCharge(int netId, float charge, bool lit) { if (!IsHost) Game.I?.SetRemoteRiteCharge(netId, charge, lit); }

    public void BroadcastRiteFire(Vector3 center) { if (Active && IsHost) Rpc(nameof(ReceiveRiteFire), center.X, center.Y, center.Z); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveRiteFire(float x, float y, float z) { if (!IsHost) Game.I?.SetRemoteRiteFire(new Vector3(x, y, z)); }

    public void BroadcastRiteDetonate(Vector3 center, int stallSeconds) { if (Active && IsHost) Rpc(nameof(ReceiveRiteDetonate), center.X, center.Y, center.Z, stallSeconds); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveRiteDetonate(float x, float y, float z, int stallSeconds) { if (!IsHost) Game.I?.ClientRiteDetonate(new Vector3(x, y, z), stallSeconds); }

    // ---- (NERFER Summoner) arcane-unicorn stream ----
    public void BroadcastUnicorn(Vector3 pos, bool charging) { if (Active && IsHost) Rpc(nameof(ReceiveUnicorn), pos.X, pos.Y, pos.Z, charging); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    private void ReceiveUnicorn(float x, float y, float z, bool charging) { if (!IsHost) Game.I?.SetRemoteUnicorn(new Vector3(x, y, z), charging); }

    public void BroadcastUnicornGone(Vector3 pos, float bossRadius) { if (Active && IsHost) Rpc(nameof(ReceiveUnicornGone), pos.X, pos.Y, pos.Z, bossRadius); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveUnicornGone(float x, float y, float z, float bossRadius) { if (!IsHost) Game.I?.SetRemoteUnicornGone(new Vector3(x, y, z), bossRadius); }

    public void RequestUnicornRecall() { if (!Active || IsHost) return; RpcId(1, nameof(ReceiveUnicornRecall)); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveUnicornRecall() { if (IsHost) Game.I?.RecallUnicorn(Multiplayer.GetRemoteSenderId()); }

    public bool NearestPickupChest(Vector3 pos, float range, out int netId, out float distSq)
    {
        netId = 0; distSq = range * range; bool found = false;
        foreach (var kv in _rpickups)
        {
            if (kv.Value == null || !GodotObject.IsInstanceValid(kv.Value) || kv.Value.Kind != 1) continue;
            float d = (kv.Value.GlobalPosition - pos).LengthSquared();
            if (d < distSq) { distSq = d; netId = kv.Key; found = true; }
        }
        return found;
    }
    // client -> host: I held E on this chest; host opens it and gives me the reward
    public void RequestOpenChest(int netId)
    {
        if (!Active || IsHost) return;
        RpcId(1, nameof(ReceiveOpenChest), netId);
    }    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveOpenChest(int netId)
    {
        if (!IsHost || Game.I == null) return;
        long sender = Multiplayer.GetRemoteSenderId();
        Game.I.HostOpenChest(netId, sender);
    }

    // ---- (EFFIGY) blessing-shrine sync ----
    // host -> all: the scattered effigies (one-shot, on spawn). Clients build ghost copies they can interact with.
    public void BroadcastEffigies(int[] ids, int[] kinds, float[] px, float[] py, float[] pz)
    {
        if (Active && IsHost) Rpc(nameof(ReceiveEffigies), ids, kinds, px, py, pz);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveEffigies(int[] ids, int[] kinds, float[] px, float[] py, float[] pz)
    {
        if (IsHost) return;
        Game.I?.ApplyEffigySync(ids, kinds, px, py, pz);
    }
    // client -> host: I held E on this effigy (I've checked my own gold). Host claims it + grants me the pick.
    public void RequestEffigy(int netId)
    {
        if (!Active || IsHost) return;
        RpcId(1, nameof(ReceiveEffigyRequest), netId);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveEffigyRequest(int netId)
    {
        if (!IsHost || Game.I == null) return;
        Game.I.HostActivateEffigy(netId, Multiplayer.GetRemoteSenderId());
    }
    // host -> the requesting ally: you may take a themed pick (kind), pay this cost
    public void GrantEffigy(long peer, int kind, int cost)
    {
        if (!Active || !IsHost) return;
        RpcId(peer, nameof(ReceiveGrantEffigy), kind, cost);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveGrantEffigy(int kind, int cost)
    {
        if (IsHost || Game.I == null) return;
        Game.I.ClientEffigyGranted(kind, cost);
    }
    // host -> all: this effigy is spent — remove it everywhere
    public void BroadcastEffigyClaim(int netId)
    {
        if (Active && IsHost) Rpc(nameof(ReceiveEffigyClaim), netId);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveEffigyClaim(int netId)
    {
        if (IsHost) return;
        Game.I?.ApplyEffigyClaim(netId);
    }
    // host -> all: updated per-type activation counts (drives the rising cost, lobby-wide)
    public void BroadcastEffigyTiers(int[] counts)
    {
        if (Active && IsHost) Rpc(nameof(ReceiveEffigyTiers), counts);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveEffigyTiers(int[] counts)
    {
        if (IsHost) return;
        Game.I?.ApplyEffigyTiers(counts);
    }

    // ---- Expedition mode sync ----
    // Geometry parity: the leg is seed-deterministic, so the host just sends the seed and every client
    // rebuilds the identical layout. Objective state (active beacon / phase / lit mask / banner text) is
    // host-authoritative and pushed on change. Clients light beacons by requesting it from the host.
    private bool NetConnected() => Multiplayer.MultiplayerPeer != null
        && Multiplayer.MultiplayerPeer.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connected
        && Multiplayer.GetPeers().Length > 0;

    public void BroadcastBeginExpedition(long seed)
    {
        if (IsHost && NetConnected()) Rpc(nameof(ReceiveBeginExpedition), seed);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveBeginExpedition(long seed)
    {
        if (IsHost || Game.I == null) return;
        Game.I.BeginExpedition((ulong)seed);
    }

    // maze interlude MP entry — host broadcasts the seed; every client builds the same maze + spawns to its slot
    public void BroadcastEnterMaze(long seed) { if (IsHost && NetConnected()) Rpc(nameof(ReceiveEnterMaze), seed); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveEnterMaze(long seed) { if (IsHost || Game.I == null) return; Game.I.EnterMaze((ulong)seed); }

    // stable per-player index (same on every machine): position of my id in the sorted full peer set
    public int LocalSpawnIndex()
    {
        if (!Active) return 0;
        var ids = new System.Collections.Generic.List<int> { Multiplayer.GetUniqueId() };
        foreach (var pid in Multiplayer.GetPeers()) ids.Add(pid);
        ids.Sort();
        int idx = ids.IndexOf(Multiplayer.GetUniqueId());
        return idx < 0 ? 0 : idx;
    }

    // players met → open the exit: reliable so the portal cell + fairy start always arrive together
    public void BroadcastMazeOpen(Vector3 fairy, int cx, int cy) { if (!Active) return; Rpc(nameof(ReceiveMazeOpen), fairy.X, fairy.Y, fairy.Z, cx, cy); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveMazeOpen(float fx, float fy, float fz, int cx, int cy)
    {
        if (Game.I == null) return;
        Game.I.SpawnPortal(new Vector2I(cx, cy), net: false);
        Game.I.SpawnFairy(new Vector3(fx, fy, fz), net: false);
    }

    // maze sound-aggro: a client tells the host where/how loud it just was, so host-owned zombies can hear it
    public void ReportSound(Vector3 pos, float loud) { if (!Active) return; RpcId(1, nameof(ReceiveSound), pos.X, pos.Y, pos.Z, loud); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveSound(float x, float y, float z, float loud) { if (IsHost) Game.I?.EmitSound(new Vector3(x, y, z), loud); }

    // client's projectile hit an idle zombie → tell the host to make it investigate the source
    public void ReportHitFrom(int enemyNetId, Vector3 from) { if (!Active) return; RpcId(1, nameof(ReceiveHitFrom), enemyNetId, from.X, from.Y, from.Z); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveHitFrom(int enemyNetId, float x, float y, float z) { if (IsHost) Game.I?.EnemyByNetId(enemyNetId)?.HitFrom(new Vector3(x, y, z)); }

    // firework minimap ping (triangulation) — everyone sees it in the firing witch's colour
    public void BroadcastBlip(Vector3 pos, Color col) { if (!Active) return; Rpc(nameof(ReceiveBlip), pos.X, pos.Y, pos.Z, col.R, col.G, col.B); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveBlip(float x, float y, float z, float r, float g, float b) { Game.I?.AddMinimapBlip(new Vector3(x, y, z), new Color(r, g, b), net: false); }

    // Taker charge: shove a client's player out of the charge path
    public void BroadcastShove(Vector3 from, float radius, float force) { if (!Active) return; Rpc(nameof(ReceiveShove), from.X, from.Y, from.Z, radius, force); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveShove(float x, float y, float z, float radius, float force)
    {
        var p = Game.I?.Player; if (p == null) return;
        var d = new Vector3(p.GlobalPosition.X - x, 0f, p.GlobalPosition.Z - z);
        if (d.Length() < radius && d.LengthSquared() > 0.01f) p.GlobalPosition += d.Normalized() * force;
    }

    // Taker grab: nearest player (host + allies) within range; returns their peer id (0 = none)
    public long PlayerNear(Vector3 pos, float range, out Vector3 hitPos)
    {
        hitPos = pos; long best = 0; float bd = range * range;
        if (Game.I != null && Game.I.Player != null)
        {
            var p = Game.I.Player.GlobalPosition; float d = new Vector2(p.X - pos.X, p.Z - pos.Z).LengthSquared();
            if (d < bd) { bd = d; best = Game.I.LocalPeer; hitPos = p; }
        }
        foreach (var kv in _remotes)
        {
            if (!GodotObject.IsInstanceValid(kv.Value)) continue;
            var p = kv.Value.GlobalPosition; float d = new Vector2(p.X - pos.X, p.Z - pos.Z).LengthSquared();
            if (d < bd) { bd = d; best = kv.Key; hitPos = p; }
        }
        return best;
    }

    // Taker grab/release → tell the grabbed player (host or client) to lock/unlock
    public void BroadcastGrab(int takerNetId, long peer, bool grabbed) { if (!Active) return; Rpc(nameof(ReceiveGrab), takerNetId, peer, grabbed); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveGrab(int takerNetId, long peer, bool grabbed)
    {
        if (Game.I?.Player == null) return;
        if (peer == Game.I.LocalPeer) Game.I.Player.GrabbedBy = grabbed ? takerNetId : 0;
    }

    // host drives the carried player's exact position each frame (host owns the Taker → no proxy lag/facing guesswork)
    public void BroadcastGrabPos(long peer, Vector3 pos) { if (!Active) return; Rpc(nameof(ReceiveGrabPos), peer, pos.X, pos.Y, pos.Z); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveGrabPos(long peer, float x, float y, float z)
    {
        var p = Game.I?.Player;
        if (p != null && peer == Game.I.LocalPeer && p.GrabbedBy != 0) { p.GlobalPosition = new Vector3(x, y, z); p.StunT = Mathf.Max(p.StunT, 0.3f); }
    }

    public void BroadcastExpoState(int activeBeacon, int phase, int litMask, string objective)
    {
        if (IsHost && NetConnected()) Rpc(nameof(ReceiveExpoState), activeBeacon, phase, litMask, objective);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveExpoState(int activeBeacon, int phase, int litMask, string objective)
    {
        if (IsHost || Game.I == null) return;
        Game.I.ApplyExpoState(activeBeacon, phase, litMask, objective);
    }

    public void RequestLightBeacon()
    {
        if (!Active || IsHost) return;
        RpcId(1, nameof(ReceiveLightBeacon));
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveLightBeacon()
    {
        if (!IsHost || Game.I == null) return;
        Game.I.HostLightBeacon();
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    private void RitualSnapshot(int[] ids, int[] types, float[] xs, float[] zs, int[] active, float[] status)
    {
        if (IsHost || Game.I == null) return;
        var seen = new HashSet<int>();
        for (int i = 0; i < ids.Length; i++)
        {
            int id = ids[i]; seen.Add(id);
            if (!_rituals.TryGetValue(id, out var rc) || !GodotObject.IsInstanceValid(rc))
            {
                rc = new RitualCircle { Type = (RiteType)types[i], Remote = true, NetId = id };
                Game.I.AddChild(rc);                       // _Ready builds the ring using Type
                rc.GlobalPosition = new Vector3(xs[i], 0f, zs[i]);
                Game.I.Rituals.Add(rc);                     // so the HUD ritual readout shows it
                _rituals[id] = rc;
            }
            rc.GlobalPosition = new Vector3(xs[i], 0f, zs[i]);
            rc.SetRemoteState(active[i] == 1, status[i]);
        }
        var gone = new List<int>();
        foreach (var kv in _rituals) if (!seen.Contains(kv.Key)) gone.Add(kv.Key);
        foreach (var id in gone)
        {
            if (_rituals.TryGetValue(id, out var rc) && GodotObject.IsInstanceValid(rc)) { Game.I.Rituals.Remove(rc); rc.QueueFree(); }
            _rituals.Remove(id);
        }
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    private void VendorSnapshot(int[] ids, int[] kinds, float[] xs, float[] zs)
    {
        if (IsHost || Game.I == null) return;
        var seen = new HashSet<int>();
        for (int i = 0; i < ids.Length; i++)
        {
            int id = ids[i]; seen.Add(id);
            if (_claimedVendors.Contains(id)) continue;   // I already used it; don't respawn while host still reports it
            if (!_rvendors.TryGetValue(id, out var v) || !GodotObject.IsInstanceValid(v))
            {
                if (kinds[i] == 0)
                {
                    var m = new Mystic { NetId = id, Remote = true };
                    Game.I.AddChild(m); m.GlobalPosition = new Vector3(xs[i], 0f, zs[i]);
                    Game.I.RemoteMystic = m; _rvendors[id] = m;
                }
                else if (kinds[i] == 2)
                {
                    var sh = new ShopVendor { NetId = id, Remote = true };
                    Game.I.AddChild(sh); sh.GlobalPosition = new Vector3(xs[i], 0f, zs[i]);
                    Game.I.RemoteShop = sh; _rvendors[id] = sh;
                }
                else
                {
                    var s = new ScrollVendor { NetId = id, Remote = true };
                    Game.I.AddChild(s); s.GlobalPosition = new Vector3(xs[i], 0f, zs[i]);
                    Game.I.RemoteScroll = s; _rvendors[id] = s;
                }
            }
        }
        var gone = new List<int>();
        foreach (var kv in _rvendors) if (!seen.Contains(kv.Key)) gone.Add(kv.Key);
        foreach (var id in gone)
        {
            if (_rvendors.TryGetValue(id, out var v) && GodotObject.IsInstanceValid(v))
            {
                if (Game.I.CurMystic == v) Game.I.RemoteMystic = null;
                if (Game.I.CurScroll == v) Game.I.RemoteScroll = null;
                if (Game.I.CurShop == v) Game.I.RemoteShop = null;
                v.QueueFree();
            }
            _rvendors.Remove(id);
        }
        var unclaim = new List<int>();
        foreach (var id in _claimedVendors) if (!seen.Contains(id)) unclaim.Add(id);
        foreach (var id in unclaim) _claimedVendors.Remove(id);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    private void RouletteSnapshot(int[] ids, float[] xs, float[] zs)
    {
        if (IsHost || Game.I == null) return;
        var seen = new HashSet<int>();
        for (int i = 0; i < ids.Length; i++)
        {
            int id = ids[i]; seen.Add(id);
            if (_claimedRoulettes.Contains(id)) continue;   // I'm spinning it; don't respawn
            if (!_rroulettes.TryGetValue(id, out var r) || !GodotObject.IsInstanceValid(r))
            {
                r = new RouletteMachine { NetId = id, Remote = true };
                Game.I.AddChild(r); r.GlobalPosition = new Vector3(xs[i], 0f, zs[i]);
                Game.I.AddRemoteRoulette(r); _rroulettes[id] = r;
            }
        }
        var gone = new List<int>();
        foreach (var kv in _rroulettes) if (!seen.Contains(kv.Key)) gone.Add(kv.Key);
        foreach (var id in gone)
        {
            if (_rroulettes.TryGetValue(id, out var r) && GodotObject.IsInstanceValid(r)) { Game.I.RemoveRemoteRoulette(r); r.QueueFree(); }
            _rroulettes.Remove(id);
        }
        var unclaim = new List<int>();
        foreach (var id in _claimedRoulettes) if (!seen.Contains(id)) unclaim.Add(id);
        foreach (var id in unclaim) _claimedRoulettes.Remove(id);
    }

    // client -> host: I'm taking this wheel; consume it for everyone (it stays mine until I finish spinning)
    public void ClaimRoulette(int netId)
    {
        if (!Active || IsHost) return;
        _claimedRoulettes.Add(netId);
        _rroulettes.Remove(netId);   // EndRoulette frees the local ghost when the session ends
        RpcId(1, nameof(ReceiveClaimRoulette), netId);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveClaimRoulette(int netId)
    {
        if (!IsHost || Game.I == null) return;
        Game.I.HostClaimRoulette(netId);
    }

    // client -> host: I used a vendor; consume it for everyone
    public void ClaimVendor(int netId)
    {
        if (!Active || IsHost) return;
        _claimedVendors.Add(netId);
        _rvendors.Remove(netId);   // OpenMystic/OpenScroll frees the local ghost itself
        RpcId(1, nameof(ReceiveClaimVendor), netId);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveClaimVendor(int netId)
    {
        if (!IsHost || Game.I == null) return;
        Game.I.HostClaimVendor(netId);
    }

    // host -> all clients: a ritual completed, everyone draws a reward card (gates the world until all choose)
    public void BroadcastReward(int cat)
    {
        if (!Active || !IsHost) return;
        Rpc(nameof(ReceiveRitualReward), cat);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveRitualReward(int cat)
    {
        if (IsHost || Game.I == null) return;
        Game.I.GrantRewardLocal(cat);
    }
    // (NEW) mutator-clear reward: every warden gets a pick-3 with a guaranteed legendary
    public void BroadcastMutatorReward()
    {
        if (!Active || !IsHost) return;
        Rpc(nameof(ReceiveMutatorReward));
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveMutatorReward()
    {
        if (IsHost || Game.I == null) return;
        Game.I.GrantMutatorRewardLocal();
    }

    // host -> clients: ritual lifecycle banners + sounds (kind: 0 spawned,1 started,2 success,3 fail)
    public void BroadcastRite(int kind, int type)
    {
        if (!Active || !IsHost) return;
        Rpc(nameof(ReceiveRite), kind, type);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveRite(int kind, int type) { Game.I?.RiteBannerSound(kind, type); }

    // host -> clients: spawn a visual-only copy of an enemy bolt so allies can see/dodge it
    public void BroadcastBolt(Vector3 origin, Vector3 vel, float radius, Color tint)
    {
        if (!Active || !IsHost) return;
        Rpc(nameof(ReceiveBolt), origin.X, origin.Y, origin.Z, vel.X, vel.Y, vel.Z, radius, PackColor(tint));
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveBolt(float ox, float oy, float oz, float vx, float vy, float vz, float radius, int tint)
    {
        if (IsHost || Game.I == null) return;
        var b = new EnemyBolt { Vel = new Vector3(vx, vy, vz), Dmg = 0f, Radius = radius, Tint = UnpackColor(tint), Remote = true };
        Game.I.AddChild(b);
        b.GlobalPosition = new Vector3(ox, oy, oz);
        ulong now = Time.GetTicksMsec();   // hear incoming shots directionally (throttled so spreads aren't a wall of noise)
        if (now - _lastEBoltSndMs > 110 && GD.Randf() < 0.5f) { _lastEBoltSndMs = now; Game.I.Sfx?.EnemyShoot(b.GlobalPosition); }
    }
    private ulong _lastEBoltSndMs = 0;

    // host -> clients: a boss is winding up an attack — mirror the lanes + shout + timer on the proxy (NEW)
    public void BroadcastBossTell(int netId, int pat, float fx, float fz, float reach, float dur, int idx, int enr)
    {
        if (!Active || !IsHost) return;
        Rpc(nameof(ReceiveBossTell), netId, pat, fx, fz, reach, dur, idx, enr);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveBossTell(int netId, int pat, float fx, float fz, float reach, float dur, int idx, int enr)
    {
        if (IsHost || Game.I == null) return;
        if (_renemies.TryGetValue(netId, out var e) && GodotObject.IsInstanceValid(e)) e.RemoteBossTell(pat, fx, fz, reach, dur, idx, enr);
    }

    // host -> clients: a foe started a cast animation (mage cast / charged spell) — pose the proxy the same way.
    // Purely cosmetic: the bolt/heal/curse it resolves into is host-authoritative and rides its own path, so this
    // stays UNRELIABLE like the other per-frame-ish enemy tells. A dropped one just means one missed wind-up pose.
    // (MP FIX) host → clients: a melee foe just started its wind-up. The enemy snapshot carries no attack state and
    // StatusMask is out of bits, so without this the little melee foes never visibly swing on a client — they just
    // walk into you and damage appears. Cosmetic only; the hit itself is host-authoritative via DamagePlayer.
    public void BroadcastEnemySwing(int netId, float wind)
    { if (!Active || !IsHost) return; Rpc(nameof(ReceiveEnemySwing), netId, wind); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveEnemySwing(int netId, float wind)
    {
        if (IsHost || Game.I == null) return;
        if (_renemies.TryGetValue(netId, out var e) && GodotObject.IsInstanceValid(e)) e.RemoteSwing(wind);
    }

    public void BroadcastEnemyCast(int netId, int clip, float dur, float cadence)
    {
        if (!Active || !IsHost) return;
        Rpc(nameof(ReceiveEnemyCast), netId, clip, dur, cadence);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveEnemyCast(int netId, int clip, float dur, float cadence)
    {
        if (IsHost || Game.I == null) return;
        if (_renemies.TryGetValue(netId, out var e) && GodotObject.IsInstanceValid(e)) e.RemoteCast(clip, dur, cadence);
    }

    // host -> clients: an enemy was flung — start the proxy tumbling to match (reliable: rare event) (NEW)
    public void BroadcastEnemyThrow(int netId, float tumbleX, float tumbleZ)
    {
        if (!Active || !IsHost) return;
        Rpc(nameof(ReceiveEnemyThrow), netId, tumbleX, tumbleZ);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveEnemyThrow(int netId, float tumbleX, float tumbleZ)
    {
        if (IsHost || Game.I == null) return;
        if (_renemies.TryGetValue(netId, out var e) && GodotObject.IsInstanceValid(e)) e.RemoteThrowBegin(tumbleX, tumbleZ);
    }

    // host -> clients: a flung enemy landed — play the same topple→get-up (dur>0) or just stand (dur==0) (NEW)
    public void BroadcastEnemyLand(int netId, float getUpDur)
    {
        if (!Active || !IsHost) return;
        Rpc(nameof(ReceiveEnemyLand), netId, getUpDur);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveEnemyLand(int netId, float getUpDur)
    {
        if (IsHost || Game.I == null) return;
        if (_renemies.TryGetValue(netId, out var e) && GodotObject.IsInstanceValid(e)) e.RemoteLand(getUpDur);
    }

    // any peer -> all others: render a tree-ent's voiceline (floating text + squeak) on their synced ghost
    public void BroadcastMinionSay(Vector3 pos, string text, int kind, Color col)
    {
        if (!Active) return;
        Rpc(nameof(ReceiveMinionSay), pos, text, kind, col);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveMinionSay(Vector3 pos, string text, int kind, Color col)
    {
        Thornling.SpeakAt(pos, text, kind, col);
    }

    // host-side: did a bolt/AoE reach an ally avatar? (returns that ally's peer so we can route the hit)
    public bool BoltHitRemote(Vector3 pos, float reach, out long peer)
    {
        peer = 0;
        if (!Active || !IsHost) return false;
        foreach (var kv in _remotes)
        {
            if (kv.Value == null || !GodotObject.IsInstanceValid(kv.Value)) continue;
            if (pos.DistanceTo(kv.Value.GlobalPosition + new Vector3(0, 1.4f, 0)) < reach) { peer = kv.Key; return true; }
        }
        return false;
    }

    // host -> ally: a zapper strike caught you (stun); host player is handled locally by the caller
    public void StunAlliesNear(Vector3 at, float r, float dur)
    {
        if (!Active || !IsHost) return;
        foreach (var kv in _remotes)
        {
            if (kv.Value == null || !GodotObject.IsInstanceValid(kv.Value)) continue;
            var d = kv.Value.GlobalPosition - at; d.Y = 0;
            if (d.Length() < r) RpcId(kv.Key, nameof(ReceiveStun), dur);
        }
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveStun(float dur)
    {
        if (Game.I?.Player == null) return;
        var pl = Game.I.Player;
        if (pl.Shield > 0.01f) pl.DrainShield(0.5f, pl.GlobalPosition);
        else pl.Stun(dur, pl.GlobalPosition);
    }

    // host -> clients: zapper telegraph + strike VFX so allies see the shock
    public void BroadcastZap(Vector3 at, bool strike)
    {
        if (!Active || !IsHost) return;
        Rpc(nameof(ReceiveZap), at.X, at.Z, strike);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveZap(float x, float z, bool strike)
    {
        if (IsHost || Game.I == null) return;
        if (strike) Game.I.ZapStrikeVFX(new Vector3(x, 0f, z));
        else Game.I.ZapTelegraphVFX(new Vector3(x, 0f, z));
    }

    // host: AoE damage to ally avatars in range (e.g. a bomber blowing up)
    public void DamageAlliesNear(Vector3 at, float r, float dmg)
    {
        if (!Active || !IsHost) return;
        foreach (var kv in _remotes)
        {
            if (kv.Value == null || !GodotObject.IsInstanceValid(kv.Value)) continue;
            var d = kv.Value.GlobalPosition - at; d.Y = 0;
            if (d.Length() < r) DamagePlayer(kv.Key, dmg);
        }
    }

    // any player's support AoE -> heal/bless/blood the OTHER players standing in it (their own avatar heals locally)
    public bool HealAlliesNear(Vector3 at, float r, float amt)   // returns true if it healed at least one ally (Radiant mote uses this to heal once per pass)
    {
        if (!Active) return false;
        bool healedAlly = false;
        foreach (var kv in _remotes)
        {
            if (kv.Value == null || !GodotObject.IsInstanceValid(kv.Value)) continue;
            var d = kv.Value.GlobalPosition - at; d.Y = 0;
            if (d.Length() < r)
            {
                RpcId(kv.Key, nameof(ReceiveHealAlly), amt);
                healedAlly = true;
                _allyHealAccum[kv.Key] = (_allyHealAccum.TryGetValue(kv.Key, out var acc) ? acc : 0f) + amt;
                if (Game.I != null) { Game.I.MyStats.Healing += amt; if (Game.I.Player != null && Game.I.Player.DivineWitch) Game.I.MyStats.Highlight += amt; }   // (NEW) tally ally-healing (+ Divine highlight)
            }
        }
        if (healedAlly) Game.I?.Player?.ComboFromDot();   // healing allies drips combo (reduced DoT rate)
        return healedAlly;
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveHealAlly(float amt) { var p = Game.I?.Player; if (p != null) { p.Heal(amt); p.HealOwnMinions(amt); p.MarkHealed(); } }

    // (NEW) Bless remote allies the Holy sweep's leading edge has reached (revealed length rl), once per peer.
    // Same strip geometry as HolyGround.Inside; `done` tracks who's already been blessed this sweep.
    public void BlessSweptAllies(Vector3 origin, Vector3 dir, float halfW, float rl, float dur, System.Collections.Generic.HashSet<long> done)
    {
        if (!Active || dur <= 0f) return;
        foreach (var kv in _remotes)
        {
            if (kv.Value == null || !GodotObject.IsInstanceValid(kv.Value) || done.Contains(kv.Key)) continue;
            var rel = kv.Value.GlobalPosition - origin; rel.Y = 0;
            float along = rel.Dot(dir);
            if (along < -0.6f || along > rl + 0.6f) continue;
            if ((rel - dir * along).Length() > halfW + 0.6f) continue;
            done.Add(kv.Key);
            RpcId(kv.Key, nameof(ReceiveBlessAlly), dur);
        }
    }

    // (NEW) Wildfire Rush: buff remote allies standing in the rectangular flame trail — light heal + move speed. NEVER the caster (only _remotes).
    public void BuffAlliesInStrip(Vector3 origin, Vector3 dir, float halfW, float len, float healAmt, float speedDur)
    {
        if (!Active) return;
        foreach (var kv in _remotes)
        {
            if (kv.Value == null || !GodotObject.IsInstanceValid(kv.Value)) continue;
            var rel = kv.Value.GlobalPosition - origin; rel.Y = 0;
            float along = rel.Dot(dir);
            if (along < -1f || along > len + 1f) continue;
            if ((rel - dir * along).Length() > halfW + 1f) continue;
            RpcId(kv.Key, nameof(ReceiveEmberTrailBuff), healAmt, speedDur);
            if (Game.I != null && healAmt > 0f) Game.I.MyStats.Healing += healAmt;
        }
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveEmberTrailBuff(float healAmt, float speedDur)
    {
        var p = Game.I?.Player; if (p == null) return;
        if (healAmt > 0f) { p.Heal(healAmt); p.MarkHealed(); }
        if (speedDur > 0f) p.GrantWindBoon(speedDur);   // reuse the +30% move boon
    }

    // (NEW) Ring of Fire: a client asks the host to register a projectile-eating zone (the host owns enemy bolts).
    public void ReqFireRing(Vector3 pos, float radius, float dur) { if (Active && !IsHost) RpcId(1, nameof(ReceiveFireRing), pos.X, pos.Y, pos.Z, radius, dur); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveFireRing(float x, float y, float z, float radius, float dur) { if (IsHost) Game.I.RegisterFireRing(new Vector3(x, y, z), radius, dur); }

    // (NEW) Cyclone: a client asks the host to register a wind projectile-eating zone
    public void ReqWindRing(Vector3 pos, float radius, float dur) { if (Active && !IsHost) RpcId(1, nameof(ReceiveWindRing), pos.X, pos.Y, pos.Z, radius, dur); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveWindRing(float x, float y, float z, float radius, float dur) { if (IsHost) Game.I.RegisterWindRing(new Vector3(x, y, z), radius, dur); }

    // (NEW) Sky Islands ritual (jungle)
    public void BroadcastSkyWhirl(Vector3 pos) { if (Active) Rpc(nameof(ReceiveSkyWhirl), pos.X, pos.Y, pos.Z); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveSkyWhirl(float x, float y, float z) { Game.I?.ShowSkyWhirl(new Vector3(x, y, z)); }

    public void RequestEnterSky() { if (Active && !IsHost) RpcId(1, nameof(ReceiveRequestEnterSky)); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveRequestEnterSky() { if (IsHost) Game.I?.EnterSky(); }

    public void BroadcastEnterSky(ulong seed, Vector3 origin) { if (Active) Rpc(nameof(ReceiveEnterSky), (long)seed, origin.X, origin.Y, origin.Z); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveEnterSky(long seed, float x, float y, float z) { Game.I?.EnterSkyRealm((ulong)seed, new Vector3(x, y, z)); }

    public void RequestSkyEffigy(int idx) { if (Active && !IsHost) RpcId(1, nameof(ReceiveRequestSkyEffigy), idx); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveRequestSkyEffigy(int idx) { if (IsHost) Game.I?.LightSkyEffigy(idx); }

    public void BroadcastSkyEffigy(int idx) { if (Active) Rpc(nameof(ReceiveSkyEffigy), idx); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveSkyEffigy(int idx) { Game.I?.LightSkyEffigy(idx); }

    public void RequestSkyComplete() { if (Active && !IsHost) RpcId(1, nameof(ReceiveRequestSkyComplete)); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveRequestSkyComplete() { if (IsHost) Game.I?.CompleteSky(); }

    public void BroadcastExitSky(bool won) { if (Active) Rpc(nameof(ReceiveExitSky), won); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveExitSky(bool won) { Game.I?.ExitSky(won); }

    // (NEW) snake root: host → the touched ally (they enforce ground-only + throttle); and snake-death → free everyone rooted
    public void SendSnakeRoot(long peer, int snakeId) { if (Active) RpcId(peer, nameof(ReceiveSnakeRoot), snakeId); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveSnakeRoot(int snakeId) { Game.I?.Player?.TrySnakeRoot(snakeId); }
    public void BroadcastSnakeDied(int snakeId) { if (Active) Rpc(nameof(ReceiveSnakeDied), snakeId); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveSnakeDied(int snakeId) { Game.I?.Player?.ClearSnakeRoot(snakeId); }

    // (NEW) Wildfire Rush: a burn tick lifesteals back to its remote caster.
    public void SendBurnHeal(int peer, float amt) { if (Active) RpcId(peer, nameof(ReceiveBurnHeal), amt); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveBurnHeal(float amt) { Game.I?.Player?.TryBurnLifesteal(amt); }

    // (NEW) Wildfire Rush: allies render a visual-only ghost of the flame trail.
    public void BroadcastEmberTrail(Vector3 origin, Vector3 dir, float len, float halfW, float dur)
    { if (Active) Rpc(nameof(ReceiveEmberTrail), origin.X, origin.Y, origin.Z, dir.X, dir.Z, len, halfW, dur); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveEmberTrail(float ox, float oy, float oz, float dx, float dz, float len, float halfW, float dur)
    {
        var d = new Vector3(dx, 0, dz); d = d.LengthSquared() > 0.001f ? d.Normalized() : Vector3.Forward;
        var t = new EmberTrail { Remote = true, Origin = new Vector3(ox, oy, oz), Dir = d, Length = len, HalfW = halfW, Dur = dur };
        Game.I.AddChild(t);
    }

    public void BlessAlliesNear(Vector3 at, float r, float dur)
    {
        if (!Active) return;
        foreach (var kv in _remotes)
        {
            if (kv.Value == null || !GodotObject.IsInstanceValid(kv.Value)) continue;
            var d = kv.Value.GlobalPosition - at; d.Y = 0;
            if (d.Length() < r) RpcId(kv.Key, nameof(ReceiveBlessAlly), dur);
        }
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveBlessAlly(float dur) { var p = Game.I?.Player; if (p != null) { p.BlessedT = Mathf.Max(p.BlessedT, dur); p.BlessOwnMinions(dur); } }

    // (NEW) this machine's peer id (host = 1; solo = 1). Used to attribute DoT/area combo to the true caster.
    public int LocalId => Active ? Multiplayer.GetUniqueId() : 1;
    // (NEW) the host credits a DoT's caster: it ticks the enemy DoT, then tells that caster's machine to bump their combo.
    public void SendDotCombo(int peer) { if (Active) RpcId(peer, nameof(ReceiveDotCombo)); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveDotCombo() { Game.I?.Player?.ComboFromDot(); }

    public void BroadcastBarkskin(float dur)   // Barkskin: every ally barks over on their own machine (and bursts there on expiry)
    {
        if (!Active) return;
        foreach (var kv in _remotes)
            if (kv.Value != null && GodotObject.IsInstanceValid(kv.Value)) RpcId(kv.Key, nameof(ReceiveBarkskin), dur);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveBarkskin(float dur) { Game.I?.Player?.GrantBark(dur); }

    public void BloodAlliesNear(Vector3 at, float r, float stacks)
    {
        if (!Active) return;
        foreach (var kv in _remotes)
        {
            if (kv.Value == null || !GodotObject.IsInstanceValid(kv.Value)) continue;
            var d = kv.Value.GlobalPosition - at; d.Y = 0;
            if (d.Length() < r) RpcId(kv.Key, nameof(ReceiveBloodAlly), stacks);
        }
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveBloodAlly(float stacks) { Game.I?.Player?.BloodReward(stacks); }
    // host -> clients: a one-shot blast ring VFX (so allies see explosions)
    public void BroadcastBlast(Vector3 at, float radius, Color col)
    {
        if (!Active || !IsHost) return;
        Rpc(nameof(ReceiveBlast), at.X, at.Y, at.Z, radius, PackColor(col));
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveBlast(float x, float y, float z, float radius, int col)
    {
        if (IsHost || Game.I == null) return;
        Game.I.BlastVFX(new Vector3(x, y, z), radius, UnpackColor(col));
    }

    // ---- player ability visuals: any peer broadcasts its own spawns so EVERYONE sees them ----
    public void BroadcastPBolt(Vector3 o, Vector3 vel, float radius, Color tint, int dtype, float life, bool horizontal = false, float grow = 0f, int style = 0)
    {
        if (!Active) return;
        Rpc(nameof(ReceivePBolt), o.X, o.Y, o.Z, vel.X, vel.Y, vel.Z, radius, PackColor(tint), dtype, life, horizontal, grow, style);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
    private void ReceivePBolt(float ox, float oy, float oz, float vx, float vy, float vz, float radius, int col, int dtype, float life, bool horizontal, float grow, int style)
    {
        if (Game.I == null) return;
        var b = new Bolt
        {
            Remote = true, Vel = new Vector3(vx, vy, vz), Radius = radius, Tint = UnpackColor(col),
            DType = (DamageType)dtype, Life = life, Normal = false, ComboShot = false, Homing = false,
            Horizontal = horizontal, Grow = grow, Style = style
        };
        Game.I.AddChild(b); b.GlobalPosition = new Vector3(ox, oy, oz);
    }

    public void BroadcastVfx(int kind, Vector3 o, Vector3 dir, float a, float b, Color col)
    {
        if (!Active) return;
        Rpc(nameof(ReceiveVfx), kind, o.X, o.Y, o.Z, dir.X, dir.Y, dir.Z, a, b, PackColor(col));
    }

    // ability sounds — allies hear each other's spells (id = Sfx.Snd)
    public void BroadcastSfx(int id, Vector3 pos)
    {
        if (!Active) return;
        Rpc(nameof(ReceiveSfx), id, pos.X, pos.Y, pos.Z);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
    private void ReceiveSfx(int id, float x, float y, float z) { Game.I?.Sfx?.PlayNet(id, new Vector3(x, y, z)); }

    // wave/intermission state + vote tally — so clients see the skip prompt and can vote
    public void BroadcastWaveState(int wave, float gap, int votes, int mutator)
    {
        if (!Active) return;
        Rpc(nameof(ReceiveWaveState), wave, gap, votes, mutator);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    private void ReceiveWaveState(int wave, float gap, int votes, int mutator) { Game.I?.ApplyWaveState(wave, gap, votes, mutator); }

    // cast-pose animation — allies see each other's arm animations (Player.SetArm broadcasts through here)
    public void BroadcastArm(string kind, float dur)
    {
        if (!Active) return;
        Rpc(nameof(ReceiveArm), kind, dur);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
    private void ReceiveArm(string kind, float dur)
    {
        long s = Multiplayer.GetRemoteSenderId();
        if (_remotes.TryGetValue(s, out var av) && GodotObject.IsInstanceValid(av)) av.PlayArm(kind, dur);
    }

    // (NEW) picture-in-picture "ult cast" cutout — an ally started ulting; pop a stylized third-person window of them casting it
    public void BroadcastUltCast(int ultKind)
    {
        if (!Active) return;
        Rpc(nameof(ReceiveUltCast), ultKind);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveUltCast(int ultKind)
    {
        long s = Multiplayer.GetRemoteSenderId();
        if (_remotes.TryGetValue(s, out var av) && GodotObject.IsInstanceValid(av))
            Game.I?.UltOverlay?.Trigger(av, (Player.UltKind)ultKind);
    }

    // (NEW) tell every peer to break the pumpkin at this position (shared props — destroy each other's)
    public void BroadcastSmashPumpkin(Vector3 pos)
    {
        if (!Active) return;
        Rpc(nameof(ReceiveSmashPumpkin), pos.X, pos.Y, pos.Z);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveSmashPumpkin(float x, float y, float z) { Game.I?.SmashPumpkinAt(new Vector3(x, y, z)); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
    private void ReceiveVfx(int kind, float ox, float oy, float oz, float dx, float dy, float dz, float a, float b, int col)
    {
        if (Game.I == null) return;
        var o = new Vector3(ox, oy, oz); var dir = new Vector3(dx, dy, dz); var c = UnpackColor(col);
        switch (kind)
        {
            case 0: Game.I.VfxRing(o, c, a, b); break;
            case 1: Game.I.VfxBeam(o, dir, a, b, c); break;
            case 2: Game.I.VfxCrimsonOrb(o, a, c); break;
            case 3: Game.I.VfxLash(o, dir, c); break;
            case 4: Game.I.VfxEclipseAura(o, a); break;             // a: duration
            case 5: Game.I.VfxDome(o, a, b, c); break;              // a: radius, b: duration
            case 6: Game.I.VfxUltBurst(o, a, c); break;             // a: radius
            case 7: Game.I.VfxEclipseBurst(o, a); break;            // a: radius
            case 8: Game.I.VfxBloodWaveGhost(o, dir, a, b); break;  // a: width, b: range
            case 9: Game.I.VfxBloodTether(o, dir, a, c); break;     // a: distance, dir: toward the witch
            case 10:                                                 // Wild Swarm stampede (visual only on allies; damage is the caster's)
            {
                var st = new Stampede(); Game.I.AddChild(st);
                st.Init(null, o, dir, a, 0f, b, true);                // a = width, b = duration
                break;
            }
            case 11:                                                 // Gale Cyclone (visual only on allies; damage is the caster's) (NEW)
            {
                var cy = new Cyclone(); Game.I.AddChild(cy);
                cy.Init(null, o, a, b, 0f, false, true);              // a = radius, b = duration, visualOnly
                break;
            }
            case 46:                                                 // Gale Hurricane tracking funnel (visual only; ReceiveHurriMove repositions it)
            {
                long sp46 = Multiplayer.GetRemoteSenderId();
                if (_hurriGhosts.TryGetValue(sp46, out var old46) && GodotObject.IsInstanceValid(old46)) old46.QueueFree();
                var cy46 = new Cyclone(); Game.I.AddChild(cy46); cy46.Init(null, o, a, b, 0f, false, true);
                _hurriGhosts[sp46] = cy46;
                break;
            }
            case 12:                                                 // Whirlwind jump-pad (visual + jump-pad for the local player; damage is the caster's) (NEW)
            {
                var pad = new WindPad(); Game.I.AddChild(pad);
                pad.Init(null, o, a, b, 0f, true);                   // a = radius, b = duration, visualOnly
                break;
            }
            case 13:                                                 // Wind Slice ghost (visual only on allies; damage is the caster's) (NEW)
            {
                var ws = new WindSlice { Dir = dir, Width = a, Range = b, Remote = true };
                Game.I.AddChild(ws);
                ws.GlobalPosition = o;
                break;
            }
            case 14:                                                 // Wind punch barrage in the air in front of the caster (NEW)
                Game.I.Player?.WindPunchBarrage(o, dir, c);
                break;
            case 15:                                                 // Implosion wind-orb (visual only on allies; damage is the caster's) (NEW LOOK)
            {
                var orb = new WindOrb(); Game.I.AddChild(orb);
                orb.Init(o, a, b);                                    // a = radius, b = duration
                break;
            }
            case 16: Game.I.SpawnGroundSigil(o, a, c, net: false); break;                    // full-charge ritual sigil (a=radius)
            case 17: Game.I.SpawnGroundSigilLinger(o, a, c, b, net: false); break;           // lingering sigil (a=radius, b=life)
            case 18: Game.I.SpawnBramblePatch(o, a, b, net: false); break;                   // bramble patch (a=radius, b=life)
            case 19: Game.I.SpawnBrambleBurst(o, a, (int)b, net: false); break;              // bramble burst (a=scale, b=count)
            case 20: Game.I.SpawnGroundSpikes(o, a, (int)dir.X, c, b, net: false); break;    // ground spikes (a=radius, dir.X=count, b=life)
            case 21: Game.I.SpawnEmberBurst(o, a, net: false); break;                        // ember burst (a=radius)
            case 22: Game.I.SpawnBloodMist(o, a, net: false); break;                         // blood mist (a=radius)
            case 23: Game.I.SpawnLightPillar(o, c, a, b, dir.X, net: false); break;          // light pillar (a=radius, b=height, dir.X=life)
            case 24: Game.I.SpawnAirColumn(o, a, b, dir.X, net: false); break;               // updraft air column (a=radius, b=height, dir.X=life)
            case 25: Game.I.SpawnBurnMark(o, c, a, b, net: false); break;                    // plasma scorch (a=size, b=life)
            case 26: Game.I.SpawnPollen(o, a, c, (int)dir.X, b, net: false); break;          // pollen motes (a=radius, dir.X=count, b=life)
            case 27: Game.I.SpawnMeadow(o, a, c, b, net: false); break;                      // holy meadow (a=radius, b=life)
            case 28: Game.I.SpawnBlightFlower(o, c, b, net: false); break;                   // grotesque blight flower (b=life)
            case 29: Game.I.SpawnDowndraft(o, a, c); break;                                   // Gale slam downdraft funnel + gusts
            case 30: Game.I.SpawnBloodColumn(o, c); break;                                    // Exsanguinate blood column
            case 31: Game.I.SpawnRotBubbles(o, a, c); break;                                  // BloodRot rot-bubbles (a=radius)
            case 32: Game.I.SpawnWindBullet(o, dir, a, b); break;                             // Wind Rush bullet (dir, a=dist, b=dur)
            case 33: Game.I.SpawnHolySweep(o, dir, a, b); break;                              // Divine descending holy ray (a=len, b=half)
            case 34:                                                                          // seed mine visual copy (a=trigger, b=life)
            {
                var m = new SeedMine { Remote = true, Trigger = a, Life = b };
                Game.I.AddChild(m); m.GlobalPosition = o;
                break;
            }
            case 35:                                                                          // Divine consecrated strip decal (a=length, b=halfW)
            {
                var hg = new HolyGround { Remote = true, Origin = o, Dir = dir, Length = a, HalfW = b, SweepDur = 1.2f, Dur = 2f, MaxDur = 2f };
                Game.I.AddChild(hg);
                break;
            }
            case 36:                                                                          // firework flare (c = witch colour)
            {
                var fw = new Firework(); Game.I.AddChild(fw); fw.Init(o, c);
                break;
            }
            case 37: Game.I.SpawnFairy(o, net: false); break;                                  // maze guide fairy (runs deterministically per-machine)
            case 38: Game.I.SpawnPortal(new Vector2I((int)a, (int)b), net: false); break;       // maze exit portal (a,b = cell)
            case 39: Game.I.DropFireworkWisp(o, c, net: false); break;                          // firework guide wisp (phase 2)
            case 40: Game.I.SpawnDust(o, dir); break;                                            // Taker charge dust burst
            case 41: Game.I.SpawnPestilence(o, a, b, remote: true, net: false); break;           // boss pestilence pool (ghost)
            case 42: Game.I.SpawnBossMineGhost(o, a, b); break;                                   // boss mine (ghost)
            case 43: Game.I.DetonateBossMineGhost(o); break;                                      // boss mine detonation (client)
            case 44: Game.I.SpawnBossRock(o, dir, a, remote: true, net: false); break;            // boss rock throw (ghost)
            case 45: Game.I.SpawnPoof(o, net: false); break;                                      // enemy spawn poof
            case 48: Game.I.SpawnFrostForm(o, a); Game.I.Sfx?.Freeze(o, false); break;            // enemy frozen (freeze crackle)
            case 49: Game.I.SpawnFrostShatter(o, a); Game.I.Sfx?.IceShatter(o, false); break;     // ice-block shatter
            case 50: Game.I.SpawnFrostBeamSeg(o, dir, a); break;                                  // frost witch freezing beam
            case 51: { var bz = new Blizzard(); Game.I.AddChild(bz); bz.Init(null, o, a, b, 0f, 0f, true); break; }        // Blizzard ghost
            case 52: { var df = new DeepFreeze(); Game.I.AddChild(df); df.Init(null, o, a, b, true, false, Mathf.Clamp((int)((a / 12f) - 1f), 0, 4)); break; }   // Glacial Sunder ghost (tier eyeballed from area for visual spear count)
            case 53:                                                                               // Frost Elemental ghost (repositioned via ReceiveFrostElemMove)
            {
                long sp53 = Multiplayer.GetRemoteSenderId();
                if (_frostElemGhosts.TryGetValue(sp53, out var old53) && GodotObject.IsInstanceValid(old53)) old53.QueueFree();
                var fe = new FrostElemental(); Game.I.AddChild(fe); fe.Init(null, o, a, b, 0f, true);
                _frostElemGhosts[sp53] = fe; break;
            }
            case 54: Game.I.Player?.SpawnIceSpikeCone(o, dir, a, c); break;                        // Ice Spikes cone
            case 55: Game.I.Player?.SpawnVaultIcicle(o, a, 0f, c, true); break;                    // Frost Vault icicle + burst ring (visual only)
            case 56: { var rt = new Vector3(dir.Z, 0, -dir.X).Normalized(); Game.I.Player?.SpawnFrostWalls(o, dir, rt, a, c); break; }   // Glacial Vise walls
            case 57: Game.I.SpawnCurseBeamSeg(o, dir.Normalized(), a); break;                       // Forsaken suck-beam
            case 58: Game.I.SpawnGroundSigil(o, a, c); Game.I.Sfx?.CurseCrush(o); break;                  // Forsaken voodoo-crush sigil + sound
            case 59: Game.I.SpawnGroundSigil(o, a, c); break;                                             // Hex Circle field pulse (a=radius) — repeats while it's up
            case 60: Game.I.VfxRing(o, c, a, 0.35f); break;                                               // Life Drain aura pulse (a=radius)
            case 61: Game.I.SpawnGroundSigil(o, a, c); Game.I.VfxRing(o, c, a, 0.7f); Game.I.Sfx?.CurseCrush(o); break;   // Life Curse / Life Drain release burst
            case 62: Game.I.SpawnMoonshard(o, a, remote: true, net: false); break;                                       // Moonfall asteroid ghost (a = size; visual only, host owns the damage)
            case 63: Game.I.SpawnScytheVfx(o, dir, a, c); Game.I.VfxRing(o, c, a, 0.5f); Game.I.Sfx?.CurseCrush(o); break; // Soul Reap scythe + ring + reap crunch
            case 64: Game.I.SpawnGroundSigil(o, a, c); Game.I.VfxRing(o, c, a, 0.5f); break;                              // Hex Chains burst (the cackle networks itself via WitchCackle)
            case 65: { var ds = new DoomSigil(); Game.I.AddChild(ds); ds.InitRemote(o, a, c); break; }                    // Doom Sigil ghost (telegraph + detonation are self-driven)
            case 66: Game.I.SpawnFlameCone(o, dir, a, c); break;                                                          // Ember flamethrower flame (a=reach)
            case 67: Game.I.SpawnEmberMeteorGhost(o, a, b > 0.1f ? b : 1.7f); break;                                     // Ember meteor ghost (a=radius, b=fall time; host owns damage)
            case 68: Game.I.SpawnEmberBurst(o, Mathf.Max(3f, a), false); Game.I.VfxRing(o, c, Mathf.Max(4f, a * 1.3f), 0.5f); break;   // Meteor Descent launch/impact (a=radius)
            case 69: Game.I.SpawnEmberBurst(o, Mathf.Max(4f, a), false); Game.I.VfxRing(o, c, a * 1.2f, 0.5f); break;    // Wildfire Rush activation tell
            case 70: Game.I.SpawnEmberBurst(o, Mathf.Max(3f, a), false); break;                                         // Phoenix aura pulse / activation / rebirth (a=radius)
            case 72: { var fw = new FireWall { Remote = true, Center = o, Radius = a, Dur = b }; Game.I.AddChild(fw); fw.GlobalPosition = o; break; }   // Ring of Fire ghost (a=radius, b=dur)
            case 73: { var fb = new Fireball { Remote = true, Dir = dir, Speed = a, BlastRadius = b }; Game.I.AddChild(fb); fb.GlobalPosition = o; break; }   // Fireball ghost (a=speed, b=blastR)
            case 76: Game.I.SpawnCrocBombGhost(o, dir, a); break;   // croc bomb ghost (o=impact, dir=origin, a=radius)
            case 77: Game.I.SpawnMoltenEruption(o, a, false); break;   // Eruption molten VFX ghost (a=radius)
            case 78: Game.I.SpawnArcaneBeamSeg(o, dir, a); break;      // Arcane witch beam segment — chain legs + arcs (ally-visible ghost; a=length)
            case 79: Game.I.SpawnArcaneRupture(o, a); break;          // Arcane rift/rupture burst (ally-visible ghost; a=radius)
            case 80: Game.I.SpawnArcaneKamehameha(o, dir, a, b, c); break;   // Arcane Torrent finisher beam (a=length, b=width)
            case 81: { var fw = new FrostWall(); Game.I.AddChild(fw); fw.Init(null, o, dir, a, b, 0f, a + 3f, 0, true); break; }   // Frost Wall remote copy — obstacle (host enemies avoid it) + visual, self-expires
            case 82: FrostWall.ShatterNearestRemote(o); break;              // Frost Wall networked shatter (visual only; damage was dealt by the caster)
        }
    }

    // Crescent Moon: the casting peer's blades orbit that peer's avatar on everyone's screen
    public void BroadcastCrescents(int count, float dur)
    {
        if (!Active) return;
        Rpc(nameof(ReceiveCrescents), count, dur);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveCrescents(int count, float dur)
    {
        if (Game.I == null) return;
        long sender = Multiplayer.GetRemoteSenderId();
        if (!_remotes.TryGetValue(sender, out var center) || !GodotObject.IsInstanceValid(center)) return;
        var list = new System.Collections.Generic.List<CrescentOrb>();
        for (int i = 0; i < count; i++)
        {
            var orb = new CrescentOrb { Remote = true, OrbitCenter = center, Angle = i / (float)Mathf.Max(1, count) * Mathf.Tau, OrbitR = 4.5f, Dmg = 0f, Life = dur };
            Game.I.AddChild(orb);
            list.Add(orb);
        }
        _rcrescents[sender] = list;
    }
    public void BroadcastCrescentFling(Vector3 dir)
    {
        if (!Active) return;
        Rpc(nameof(ReceiveCrescentFling), dir.X, dir.Z);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveCrescentFling(float dx, float dz)
    {
        long sender = Multiplayer.GetRemoteSenderId();
        if (!_rcrescents.TryGetValue(sender, out var list)) return;
        var d = new Vector3(dx, 0f, dz);
        foreach (var o in list) if (o != null && GodotObject.IsInstanceValid(o)) o.Fire(d);
    }

    // (NEW) exact Crescent orb positions from the caster → position-driven ghosts (orbit/fling/rotate all replicate)
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void CrescentSnapshot(float[] xs, float[] ys, float[] zs)
    {
        if (Game.I == null) return;
        long id = Multiplayer.GetRemoteSenderId();
        if (!_rcrescents.TryGetValue(id, out var list)) { list = new System.Collections.Generic.List<CrescentOrb>(); _rcrescents[id] = list; }
        int n = xs.Length;
        while (list.Count < n)
        {
            var g = new CrescentOrb { Remote = true, Dmg = 0f, Life = 999f };
            Game.I.AddChild(g);
            g.GlobalPosition = new Vector3(xs[list.Count], ys[list.Count], zs[list.Count]); g.GhostTarget = g.GlobalPosition;
            list.Add(g);
        }
        while (list.Count > n) { var g = list[list.Count - 1]; if (GodotObject.IsInstanceValid(g)) g.QueueFree(); list.RemoveAt(list.Count - 1); }
        for (int i = 0; i < n; i++) if (GodotObject.IsInstanceValid(list[i])) list[i].SetGhostTarget(new Vector3(xs[i], ys[i], zs[i]));
    }

    // lingering fields (Lunar Light / Judgement / combos) — a visual copy for allies
    public void BroadcastField(int type, Vector3 at, float radius, float dur, bool beam, Color tint, int dtype)
    {
        if (!Active) return;
        Rpc(nameof(ReceiveField), type, at.X, at.Z, radius, dur, beam, PackColor(tint), dtype);
    }
    // (WIND RUSH) a wind SPEED-ZONE — allies spawn a SpeedBoost field so THEY get the ×3 move boost standing in it too
    public void BroadcastWindZone(Vector3 at, float radius, float dur)
    {
        if (!Active) return;
        Rpc(nameof(ReceiveWindZone), at.X, at.Z, radius, dur);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveWindZone(float x, float z, float radius, float dur)
    {
        if (Game.I == null) return;
        var f = new GroundField { Remote = true, SpeedBoost = true, Type = FieldType.Hex, Radius = radius, Dur = dur, DType = DamageType.Wind, TintColor = DamageTypes.Col(DamageType.Wind) };
        Game.I.AddChild(f); f.GlobalPosition = new Vector3(x, 0.04f, z);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveField(int type, float x, float z, float radius, float dur, bool beam, int tint, int dtype)
    {
        if (Game.I == null) return;
        var f = new GroundField
        {
            Remote = true, Type = (FieldType)type, Radius = radius, Dur = dur, Beam = beam,
            DType = (DamageType)dtype, TintColor = UnpackColor(tint)
        };
        Game.I.AddChild(f);
        f.GlobalPosition = new Vector3(x, 0.04f, z);
    }

    // holy lance plunge VFX
    public void BroadcastLance(Vector3 at, float scale, float dur)
    {
        if (!Active) return;
        Rpc(nameof(ReceiveLance), at.X, at.Y, at.Z, scale, dur);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveLance(float x, float y, float z, float scale, float dur)
    {
        Game.I?.VfxLance(new Vector3(x, y, z), dur, scale);
    }

    // ---- downed / revive ----
    private readonly HashSet<long> _downed = new();
    private readonly HashSet<long> _mazePeers = new();   // host: peers currently inside the maze (drives maze-death spit-out vs game-over)
    public void MazeEnterAll() { if (!IsHost) return; _mazePeers.Clear(); _mazePeers.Add(Multiplayer.GetUniqueId()); foreach (var id in Multiplayer.GetPeers()) _mazePeers.Add(id); }
    public void ReportLeftMaze() { if (!Active) return; if (IsHost) { _mazePeers.Remove(Multiplayer.GetUniqueId()); EvalGameOver(); } else RpcId(1, nameof(HostLeftMaze)); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void HostLeftMaze() { if (!IsHost) return; _mazePeers.Remove(Multiplayer.GetRemoteSenderId()); EvalGameOver(); }
    public bool AnyDowned() => _downed.Count > 0;
    public float MinAllyHpFrac()   // (NEW) lowest ally HP frac — lets the director judge party-wide survival, not just the host's
    {
        float m = 1f;
        foreach (var kv in _remotes)
            if (kv.Value != null && GodotObject.IsInstanceValid(kv.Value)) m = Mathf.Min(m, kv.Value.Downed ? 0f : kv.Value.HpFrac);
        return m;
    }
    // (NEW) a specific ally's HP fraction (synced) — for the per-opener chest mercy-heal in MP. 1 if that peer isn't a known ally.
    public float PeerHpFrac(long peer)
    {
        if (_remotes.TryGetValue(peer, out var av) && av != null && GodotObject.IsInstanceValid(av)) return av.Downed ? 0f : av.HpFrac;
        return 1f;
    }
    // (MAGNET LUCK) a specific ally's synced Luck stat — 0 if that peer isn't a known ally
    public float PeerLuck(long peer)
    {
        if (_remotes.TryGetValue(peer, out var av) && av != null && GodotObject.IsInstanceValid(av)) return av.Luck;
        return 0f;
    }
    public System.Collections.Generic.List<Vector3> AllyPositions()
    {
        var list = new System.Collections.Generic.List<Vector3>();
        foreach (var kv in _remotes) if (kv.Value != null && GodotObject.IsInstanceValid(kv.Value)) list.Add(kv.Value.GlobalPosition);
        return list;
    }
    public bool AnyAllyDowned()   // (SMART DIRECTOR) is any co-op ally currently downed? (a prime moment to send a special)
    {
        foreach (var kv in _remotes) if (kv.Value != null && GodotObject.IsInstanceValid(kv.Value) && kv.Value.Downed) return true;
        return false;
    }
    // (NERFER Summoner) peer→position, for the unicorn's proximity-claim + T-recall follow
    public System.Collections.Generic.List<(long, Vector3)> AllyPeerPositions()
    {
        var list = new System.Collections.Generic.List<(long, Vector3)>();
        foreach (var kv in _remotes) if (kv.Value != null && GodotObject.IsInstanceValid(kv.Value)) list.Add((kv.Key, kv.Value.GlobalPosition));
        return list;
    }
    // (NERFER Summoner) same list, but only wardens who can actually HOLD ground — a downed ally doesn't keep the ward alive
    public System.Collections.Generic.List<(long, Vector3)> AliveAllyPositions()
    {
        var list = new System.Collections.Generic.List<(long, Vector3)>();
        foreach (var kv in _remotes) if (kv.Value != null && GodotObject.IsInstanceValid(kv.Value) && !kv.Value.Downed) list.Add((kv.Key, kv.Value.GlobalPosition));
        return list;
    }
    public Vector3 PeerPosition(long peer)
    {
        if (_remotes.TryGetValue(peer, out var av) && av != null && GodotObject.IsInstanceValid(av)) return av.GlobalPosition;
        return Vector3.Zero;
    }
    // (NEW) client-side XP-orb ghost positions, for the minimap specks (host reads Game.Orbs directly)
    public System.Collections.Generic.List<Vector3> RemoteOrbPositions()
    {
        var list = new System.Collections.Generic.List<Vector3>();
        foreach (var kv in _rpickups) if (kv.Value != null && GodotObject.IsInstanceValid(kv.Value) && kv.Value.Kind == 0) list.Add(kv.Value.GlobalPosition);
        return list;
    }

    // called by the LOCAL player when it goes down or gets revived
    public void LocalDowned(bool d)
    {
        if (!Active) return;
        long me = Multiplayer.GetUniqueId();
        Rpc(nameof(ReceiveDownedVisual), d);              // everyone updates my avatar's look
        if (IsHost) { if (d) _downed.Add(me); else _downed.Remove(me); EvalGameOver(); }
        else RpcId(1, nameof(HostReceiveDowned), d);      // host is the game-over authority
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveDownedVisual(bool d)
    {
        long s = Multiplayer.GetRemoteSenderId();
        if (_remotes.TryGetValue(s, out var av) && GodotObject.IsInstanceValid(av)) av.SetDowned(d);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void HostReceiveDowned(bool d)
    {
        if (!IsHost) return;
        long s = Multiplayer.GetRemoteSenderId();
        if (d) _downed.Add(s); else _downed.Remove(s);
        EvalGameOver();
    }
    private void EvalGameOver()
    {
        if (!IsHost) return;
        if (Game.I != null && Game.I.InSky) return;   // (NEW) sky ritual: game-over suspended — CheckSkyFalls ends it when everyone has fallen/died
        if (_mazePeers.Count > 0)   // a maze event is in progress — game-over is suspended; instead spit players out
        {
            foreach (var pid in _mazePeers) if (!_downed.Contains(pid)) return;   // someone still IN the maze is up → keep going
            Rpc(nameof(ReceiveMazeAllDown)); Game.I?.MazeDeathExit();   // everyone still in the maze is down → all spat out (covers "alone")
            return;
        }
        int total = _remotes.Count + 1;
        if (_downed.Count >= total) { Rpc(nameof(ReceiveGameOver)); Game.I?.GameOver(); }
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveGameOver() { Game.I?.GameOver(); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveMazeAllDown() { Game.I?.MazeDeathExit(); }

    // ---- end-of-run scoreboard sync (NEW): each warden broadcasts its RunStats block at game over ----
    public void BroadcastRunStats(RunStats s)
    {
        if (!Active || s == null) return;
        Rpc(nameof(ReceiveRunStats), s.DamageDealt, s.BossDamage, s.Healing, s.Flings, s.DamageTaken, s.Highlight, s.WitchIdx, s.Slot, s.TimesDowned, s.Revives, s.BestCombo, s.BiggestHit);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveRunStats(float dd, float bd, float heal, int flings, float taken, float hl, int witch, int slot, int downed, int revives, int bestCombo, float biggestHit)
    {
        if (Game.I == null) return;
        Game.I.AllStats[Multiplayer.GetRemoteSenderId()] = new RunStats
        { DamageDealt = dd, BossDamage = bd, Healing = heal, Flings = flings, DamageTaken = taken, Highlight = hl, WitchIdx = witch, Slot = slot, TimesDowned = downed, Revives = revives, BestCombo = bestCombo, BiggestHit = biggestHit };
    }

    // host → all: the authoritative kill tallies (the only exact way to attribute kills in HOST-OWNS-WORLD)
    public void BroadcastKillTally()
    {
        if (!Active || !IsHost) return;
        var peers = new System.Collections.Generic.List<long>(Game.I.KillTally.Keys);
        var ids = new long[peers.Count]; var kills = new int[peers.Count]; var night = new int[peers.Count];
        for (int i = 0; i < peers.Count; i++) { ids[i] = peers[i]; kills[i] = Game.I.KillTally[peers[i]]; night[i] = Game.I.NightKillTally.GetValueOrDefault(peers[i]); }
        Rpc(nameof(ReceiveKillTally), ids, kills, night);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveKillTally(long[] ids, int[] kills, int[] night)
    {
        if (Game.I == null) return;
        for (int i = 0; i < ids.Length; i++) { Game.I.KillTally[ids[i]] = kills[i]; Game.I.NightKillTally[ids[i]] = night[i]; }
    }

    // ---- char-select ready gate (NEW): every warden locks in, host waits for all, then broadcasts BeginRun ----
    private readonly System.Collections.Generic.HashSet<long> _ready = new();
    public void ResetReady() { _ready.Clear(); _downed.Clear(); if (Game.I != null) Game.I.ReadyCount = 0; }
    public void ReportReady()
    {
        if (!Active) return;
        if (IsHost) { _ready.Add(Multiplayer.GetUniqueId()); HostSyncReady(); }
        else RpcId(1, nameof(HostReceiveReady));
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void HostReceiveReady() { if (IsHost) { _ready.Add(Multiplayer.GetRemoteSenderId()); HostSyncReady(); } }
    private void HostSyncReady()
    {
        if (!IsHost) return;
        int total = PlayerCount();
        if (Game.I != null) Game.I.ReadyCount = _ready.Count;
        if (NetConnected()) Rpc(nameof(ReceiveReadyCount), _ready.Count);   // update everyone's "X/Y ready" tally
        if (_ready.Count >= total) { _ready.Clear(); if (NetConnected()) Rpc(nameof(ReceiveBeginRun)); Game.I?.BeginRunFromSelect(); }
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveReadyCount(int n) { if (Game.I != null) Game.I.ReadyCount = n; }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveBeginRun() { Game.I?.BeginRunFromSelect(); }

    // ---- MP game-over decision (NEW): host chooses for the group; 0 = char-select, 1 = retry same witches, 2 = end ----
    public void BroadcastGameOverChoice(int choice)
    {
        if (!IsHost) return;
        if (NetConnected()) Rpc(nameof(ReceiveGameOverChoice), choice);
        Game.I?.ApplyGameOverChoice(choice);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveGameOverChoice(int choice) { Game.I?.ApplyGameOverChoice(choice); }

    public bool NearestDownedAlly(Vector3 me, float range, out long peer, out float d2)
    {
        peer = 0; d2 = range * range; bool found = false;
        foreach (var kv in _remotes)
        {
            var av = kv.Value;
            if (av == null || !GodotObject.IsInstanceValid(av) || !av.Downed) continue;
            float d = (av.GlobalPosition - me).LengthSquared();
            if (d < d2) { d2 = d; peer = kv.Key; found = true; }
        }
        return found;
    }
    public void RevivePeer(long peer, float frac, bool beam)
    {
        if (!Active) return;
        RpcId(peer, nameof(ReceiveRevive), frac, beam);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveRevive(float frac, bool beam)
    {
        Game.I?.LocalPlayer?.ReviveMe(frac, beam);
    }

    // ---- damage numbers (host enemies → all clients) ----
    public void BroadcastPopup(Vector3 at, float amt, Color col, bool crit)
    {
        if (!Active) return;
        Rpc(nameof(ReceivePopup), at.X, at.Y, at.Z, amt, PackColor(col), crit);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
    private void ReceivePopup(float x, float y, float z, float amt, int col, bool crit)
    {
        if (Game.I == null || !Game.I.DmgNumbers) return;
        var pop = new DamagePopup();
        Game.I.AddChild(pop);
        pop.Init(amt, UnpackColor(col), new Vector3(x, y, z), crit);
    }

    // ---- holy rez beam column ----
    public void BroadcastRezBeam(Vector3 at)
    {
        if (!Active) return;
        Rpc(nameof(ReceiveRezBeam), at.X, at.Z);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveRezBeam(float x, float z)
    {
        Game.I?.RezBeamColumn(new Vector3(x, 0f, z));
    }

    public int RemoteAvatarsInRange(Vector3 pos, float r)
    {
        int n = 0;
        foreach (var kv in _remotes)
        {
            if (kv.Value == null || !GodotObject.IsInstanceValid(kv.Value)) continue;
            var d = kv.Value.GlobalPosition - pos; d.Y = 0;
            if (d.Length() < r) n++;
        }
        return n;
    }

    public int PlayerCount() => Active ? _remotes.Count + 1 : 1;

    // (NEW) vote-to-skip: a client tells the host it wants to skip the current rest/ritual. Host tallies against PlayerCount().
    public void VoteSkip()
    {
        if (!Active || IsHost) return;
        RpcId(1, nameof(ReceiveSkipVote));
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveSkipVote()
    {
        if (!IsHost || Game.I == null) return;
        Game.I.RegisterSkipVote(Multiplayer.GetRemoteSenderId());
    }

    // nearest connected ally avatar to a point (host-side enemy targeting)
    public bool NearestRemote(Vector3 pos, out long peer, out Vector3 rpos)
    {
        peer = 0; rpos = Vector3.Zero; float bd = float.MaxValue; bool found = false;
        foreach (var kv in _remotes)
        {
            if (!GodotObject.IsInstanceValid(kv.Value)) continue;
            float d = (kv.Value.GlobalPosition - pos).LengthSquared();
            if (d < bd) { bd = d; peer = kv.Key; rpos = kv.Value.GlobalPosition; found = true; }
        }
        return found;
    }

    // host -> a specific ally: apply enemy damage to their local player
    public void DamagePlayer(long peer, float dmg)
    {
        if (!Active || !IsHost) return;
        RpcId(peer, nameof(ReceivePlayerDamage), dmg);
    }

    // (HAUNT STORM) a lightning strike landing on wardens. Deliberately NOT HurtStunPlayersIn: that helper calls Hurt()
    // and Stun() independently, and Stun() ignores i-frames. A warden who dashed through the circle (0.3s i-frame) or who
    // was hit a moment ago (0.7s) would take ZERO damage and still be frozen in place — dodging correctly still costing
    // you the fight is exactly the chain-stun feel we keep designing out. Here it's ONE decision: mitigated → untouched.
    // Each machine judges its own player, because _iframe only exists locally.
    public void HauntBoltPlayersIn(Vector3 center, float radius, float dmg, float stun)
    {
        var lp = Game.I?.Player;
        if (lp != null && !lp.Downed && !lp.IFraming)
        { var d = lp.GlobalPosition - center; d.Y = 0f; if (d.Length() < radius) { lp.Hurt(dmg); lp.Stun(stun, center); } }
        if (!Active || !IsHost) return;
        foreach (var kv in _remotes)
        { if (!GodotObject.IsInstanceValid(kv.Value)) continue; var d = kv.Value.GlobalPosition - center; d.Y = 0f; if (d.Length() < radius) RpcId(kv.Key, nameof(ReceiveHauntBoltHit), dmg, stun, center); }
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveHauntBoltHit(float dmg, float stun, Vector3 from)
    {
        var lp = Game.I?.LocalPlayer;
        if (lp == null || lp.Downed || lp.IFraming) return;   // the client owns its own dodge window
        lp.Hurt(dmg); lp.Stun(stun, from);
    }

    // AoE from an enemy/boss: hurt the local player + every ally avatar inside a radius (works solo + MP)
    public void HurtPlayersIn(Vector3 center, float radius, float dmg)
    {
        if (Game.I?.Player != null && !Game.I.Player.Downed)
        { var d = Game.I.Player.GlobalPosition - center; d.Y = 0f; if (d.Length() < radius) Game.I.Player.Hurt(dmg); }
        if (!Active || !IsHost) return;
        foreach (var kv in _remotes)
        { if (!GodotObject.IsInstanceValid(kv.Value)) continue; var d = kv.Value.GlobalPosition - center; d.Y = 0f; if (d.Length() < radius) DamagePlayer(kv.Key, dmg); }
    }

    // host: damage every ally whose position matches a predicate (used by the maze veil — flooded corridor cells)
    public void DamageRemotesWhere(System.Func<Vector3, bool> pred, float dmg)
    {
        if (!Active || !IsHost || pred == null) return;
        foreach (var kv in _remotes)
            if (GodotObject.IsInstanceValid(kv.Value) && pred(kv.Value.GlobalPosition)) DamagePlayer(kv.Key, dmg);
    }

    // boss rock: damage + STUN everyone in range (works solo + MP)
    public void HurtStunPlayersIn(Vector3 center, float radius, float dmg, float stun)
    {
        if (Game.I?.Player != null && !Game.I.Player.Downed)
        { var d = Game.I.Player.GlobalPosition - center; d.Y = 0f; if (d.Length() < radius) { Game.I.Player.Hurt(dmg); Game.I.Player.Stun(stun); } }
        if (!Active || !IsHost) return;
        foreach (var kv in _remotes)
        { if (!GodotObject.IsInstanceValid(kv.Value)) continue; var d = kv.Value.GlobalPosition - center; d.Y = 0f; if (d.Length() < radius) { DamagePlayer(kv.Key, dmg); RpcId(kv.Key, nameof(ReceiveStun), stun); } }
    }

    // hexer: root the targeted ally over the network (host player handled directly by the caller)
    public void SnarePlayer(long peer, float dur)
    {
        if (!Active || !IsHost) return;
        RpcId(peer, nameof(ReceiveSnare), dur);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveSnare(float dur) { Game.I?.Player?.SnareMe(dur); }

    // Faith Shield: every machine spawns a copy. On the host it's AUTHORITATIVE (blocks enemies + deals the shatter); on
    // clients it's visual-only. Called by the caster; reaches all OTHER peers (the caster already spawned its own).
    public void SpawnFaithShield(Vector3 pos, float radius, float dur, float burstDmg, float knock, bool reflect)
    {
        if (!Active) return;
        Rpc(nameof(ReceiveFaithShield), pos, radius, dur, burstDmg, knock, reflect);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveFaithShield(Vector3 pos, float radius, float dur, float burstDmg, float knock, bool reflect)
    { Game.I?.SpawnRemoteFaithShield(pos, radius, dur, burstDmg, knock, reflect); }

    // ---- THE HOLLOW MOON, phase 2 ----
    // host -> clients: he entered phase 2 (or his untouchable flag flipped). `invuln` drives the proxy's HUD read-out and
    // makes client-side hits bounce, so a client can't keep pumping damage into a boss the host is ignoring.
    public void BroadcastBossPhase2(int netId, int phase, int invuln)
    {
        if (!Active || !IsHost) return;
        Rpc(nameof(ReceiveBossPhase2), netId, phase, invuln);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveBossPhase2(int netId, int phase, int invuln)
    {
        if (IsHost || Game.I == null) return;
        if (_renemies.TryGetValue(netId, out var e) && GodotObject.IsInstanceValid(e)) e.RemoteBossPhase2(phase, invuln != 0);
    }

    // host -> clients: spawn the vortex on every machine. Each copy pulls its OWN local witch (player position is
    // client-authoritative, so a per-frame pull RPC would fight the owner); only the host's copy deals damage.
    public void BroadcastBossVortex(Vector3 pos, float dur, float dps)
    {
        if (!Active || !IsHost) return;
        Rpc(nameof(ReceiveBossVortex), pos, dur, dps);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveBossVortex(Vector3 pos, float dur, float dps)
    {
        if (IsHost || Game.I == null) return;
        var v = new BossVortex();
        Game.I.AddChild(v);
        v.Init(pos, dur, dps, hostSim: false);
    }

    // host: the vortex's finishing stomp — a flat % of MAX health to every ally reeled inside `radius`
    public void VortexStomp(Vector3 at, float radius, float maxHpFrac)
    {
        if (!Active || !IsHost) return;
        foreach (var kv in _remotes)
        {
            if (!GodotObject.IsInstanceValid(kv.Value)) continue;
            var d = kv.Value.GlobalPosition - at; d.Y = 0f;
            if (d.Length() >= radius) continue;
            RpcId(kv.Key, nameof(ReceiveVortexStomp), at, maxHpFrac);
        }
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveVortexStomp(Vector3 at, float maxHpFrac)
    {
        var p = Game.I?.Player;
        if (p == null || p.Downed) return;
        p.Hurt(p.S.MaxHp * maxHpFrac, at, ignoreIFrame: true);   // resolved on the OWNER so shields/armor/immunity still apply
        p.Knockback(at, 26f);
    }

    // THE HOLLOW MOON's head-down charge: sweep everyone within `radius` of the segment a→b, hurt + shove them away from `a`.
    // `hit` remembers who this charge already caught (peer 0 = the local player) so a multi-frame dash can't hit twice.
    public void ChargeSweep(Vector3 a, Vector3 b, float radius, float dmg, float power, System.Collections.Generic.HashSet<long> hit)
    {
        if (Game.I?.Player != null && !Game.I.Player.Downed && !hit.Contains(0L) && SegDist(Game.I.Player.GlobalPosition, a, b) < radius)
        { hit.Add(0L); Game.I.Player.Hurt(dmg); Game.I.Player.Knockback(a, power); }
        if (!Active || !IsHost) return;
        foreach (var kv in _remotes)
        {
            if (!GodotObject.IsInstanceValid(kv.Value) || hit.Contains(kv.Key)) continue;
            if (SegDist(kv.Value.GlobalPosition, a, b) >= radius) continue;
            hit.Add(kv.Key);
            DamagePlayer(kv.Key, dmg);
            RpcId(kv.Key, nameof(ReceiveKnockback), a, power);
        }
    }

    // flat (XZ) distance from a point to the segment a→b — the charge is a ground sweep, height doesn't matter
    private static float SegDist(Vector3 p, Vector3 a, Vector3 b)
    {
        p.Y = 0f; a.Y = 0f; b.Y = 0f;
        var ab = b - a;
        float l2 = ab.LengthSquared();
        if (l2 < 0.0001f) return p.DistanceTo(a);
        float t = Mathf.Clamp((p - a).Dot(ab) / l2, 0f, 1f);
        return p.DistanceTo(a + ab * t);
    }

    // troll charge: shove the targeted ally back over the network
    public void KnockbackPlayer(long peer, Vector3 from, float power)
    {
        if (!Active || !IsHost) return;
        RpcId(peer, nameof(ReceiveKnockback), from, power);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveKnockback(Vector3 from, float power) { Game.I?.Player?.Knockback(from, power); }

    // swarmer: slow the targeted ally over the network
    public void SlowPlayer(long peer, float dur, float mul)
    {
        if (!Active || !IsHost) return;
        RpcId(peer, nameof(ReceiveSlow), dur, mul);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveSlow(float dur, float mul) { Game.I?.Player?.SlowMe(dur, mul); }

    public void DispelPlayer(long peer, float dur)
    {
        if (!Active || !IsHost) return;
        RpcId(peer, nameof(ReceiveDispel), dur);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveDispel(float dur) { Game.I?.Player?.Dispel(dur); }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceivePlayerDamage(float dmg)
    {
        if (Game.I?.LocalPlayer != null) Game.I.LocalPlayer.Hurt(dmg);
    }

    // (NEW) phalanx arrow-venom: stamp/refresh the poison on everyone standing in the volley circle. Mirrors
    // HurtPlayersIn — local player directly, allies over the wire — so it works solo and in co-op alike.
    public void VenomPlayersIn(Vector3 center, float radius, float dur, float dps)
    {
        if (Game.I?.Player != null && !Game.I.Player.Downed)
        { var d = Game.I.Player.GlobalPosition - center; d.Y = 0f; if (d.Length() < radius) Game.I.Player.ApplyVenom(dur, dps); }
        if (!Active || !IsHost) return;
        foreach (var kv in _remotes)
        { if (!GodotObject.IsInstanceValid(kv.Value)) continue; var d = kv.Value.GlobalPosition - center; d.Y = 0f; if (d.Length() < radius) RpcId(kv.Key, nameof(ReceiveVenom), dur, dps); }
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveVenom(float dur, float dps)
    {
        Game.I?.LocalPlayer?.ApplyVenom(dur, dps);
    }

    // ===== WARDED PHALANX (NEW) — the enemy snapshot has no spare status bits, so the ward rides its own tiny RPCs =====

    // host -> all, ~5Hz while a ward stands: the bearer's remaining ward fraction (drives the dome + the HUD bar)
    public void BroadcastWard(int netId, float frac)
    {
        if (!Active || !IsHost) return;
        Rpc(nameof(ReceiveWard), netId, frac);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    private void ReceiveWard(int netId, float frac) { FindRemoteEnemy(netId)?.SetRemoteWard(frac); }

    // host -> all, one-shot: this archer is now (or no longer) sheltered by a ward — clients need it to suppress
    // the local damage popup on a hit that the host is going to reject anyway
    public void BroadcastWardGuard(int netId, bool on)
    {
        if (!Active || !IsHost) return;
        Rpc(nameof(ReceiveWardGuard), netId, on);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveWardGuard(int netId, bool on) { FindRemoteEnemy(netId)?.SetRemoteGuarded(on); }

    // host -> all, one-shot: paint the volley circle on every machine (ghost copies are visual-only)
    public void BroadcastVolley(Vector3 pos, float radius, float dps, float venom)
    {
        if (!Active || !IsHost) return;
        Rpc(nameof(ReceiveVolley), pos, radius, dps, venom);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveVolley(Vector3 pos, float radius, float dps, float venom)
    {
        if (Game.I == null) return;
        var v = new ArrowVolley { Remote = true };
        Game.I.AddChild(v);
        v.Init(pos, radius, dps, venom);
    }

    // host -> all: an archer raised its bow to the sky (1, with a quip) or loosed (0). Purely cosmetic, but it IS the
    // volley's telegraph, so allies must see the same wind-up the host does.
    public void BroadcastArcherPose(int netId, int state)
    {
        if (!Active || !IsHost) return;
        Rpc(nameof(ReceiveArcherPose), netId, state);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveArcherPose(int netId, int state)
    {
        var e = FindRemoteEnemy(netId);
        if (e == null) return;
        if (state == 1) { e.AimSky(true); e.Quip(); } else e.Loose();
    }

    private Enemy FindRemoteEnemy(int netId)
    {
        if (Game.I == null) return null;
        var list = Game.I.Enemies;
        for (int i = 0; i < list.Count; i++)
        { var e = list[i]; if (e != null && GodotObject.IsInstanceValid(e) && e.NetId == netId) return e; }
        return null;
    }

    // host -> all: an enemy was banished; everyone gets the kill credit
    public void BroadcastKill(int score)
    {
        if (!Active || !IsHost) return;
        Rpc(nameof(ReceiveKill), score);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveKill(int score)
    {
        if (Game.I != null) Game.I.Score += score;
    }

    // shared ult tokens: a boss/miniboss kill credits every warden so all can upgrade/change their ult
    public void BroadcastBossToken(float amt)
    {
        if (!Active || !IsHost) return;
        Rpc(nameof(ReceiveBossToken), amt);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveBossToken(float amt) { if (Game.I != null) Game.I.BossTokens += amt; }

    // shared goblin loot: the loot pick opens for every warden (it's a gate, so the world freezes for all)
    public void BroadcastGoblinLoot(bool elite)
    {
        if (!Active || !IsHost) return;
        Rpc(nameof(ReceiveGoblinLoot), elite);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveGoblinLoot(bool elite) { Game.I?.GoblinLoot(elite); }

    // host -> clients: enemies died here, so any ally blood witch whose aura covers them banks a stack.
    // Batched: positions accumulate and flush together a few times a second.
    public void BroadcastEnemyDeath(Vector3 at)
    {
        if (!Active || !IsHost) return;
        _deathQueue.Add(at);
        if (_deathQueue.Count >= 48) FlushDeaths();   // safety valve for big swarms
    }
    private void FlushDeaths()
    {
        if (!Active || !IsHost || _deathQueue.Count == 0) return;
        int n = _deathQueue.Count;
        var xs = new float[n]; var ys = new float[n]; var zs = new float[n];
        for (int i = 0; i < n; i++) { xs[i] = _deathQueue[i].X; ys[i] = _deathQueue[i].Y; zs[i] = _deathQueue[i].Z; }
        _deathQueue.Clear();
        Rpc(nameof(ReceiveEnemyDeaths), xs, ys, zs);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
    private void ReceiveEnemyDeaths(float[] xs, float[] ys, float[] zs)
    {
        var p = Game.I?.Player;
        if (p == null || xs == null) return;
        for (int i = 0; i < xs.Length; i++)
        {
            var at = new Vector3(xs[i], ys[i], zs[i]);
            p.OnBloodAuraKill(at);
            Fx.SparkBurst(at + Vector3.Up * 0.6f, Vector3.Up, new Color(1f, 0.9f, 0.7f), 1.0f, 7);   // (MP) death-pop on clients (host shows its own in Die); no colour in the batch → neutral warm spark
        }
    }

    // every player shares a slice of the damage they deal as ult charge, so the team's ult meters
    // fill together — melee/AoE witches aren't starved next to a ranged witch farming chip damage.
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
    private void ReceiveTeamDamage(float dmg)
    {
        var p = Game.I?.Player;
        if (p == null || p.Ult == Player.UltKind.None || p.UltActive) return;
        p.UltCharge = Mathf.Min(1f, p.UltCharge + Mathf.Min(0.03f, dmg * 0.00008f) * (Game.I?.UltGainMul ?? 1f));   // (ULT COST RAMP) the shared slice gets dearer with difficulty too
    }

    // ---- connection lifecycle ----
    private void OnPeerConnected(long id)
    {
        if (IsHost) { Game.I?.ShowToast("A player connected!"); RpcId(id, nameof(ReceiveWorldSeed), Game.I != null ? Game.I.WorldSeed : 0L); }
    }
    // host -> a specific client: here's the map seed, rebuild your world to match ours (NEW)
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveWorldSeed(long seed) { if (!IsHost) Game.I?.ReseedWorld(seed); }

    // (NEW) level portal + advance-to-next-biome
    public void BroadcastPortal(Vector3 pos) { if (Active) Rpc(nameof(ReceivePortalRpc), pos.X, pos.Y, pos.Z); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceivePortalRpc(float x, float y, float z) { if (!IsHost) Game.I?.ReceivePortal(new Vector3(x, y, z)); }
    public void RequestAdvanceLevel() { if (Active && !IsHost) RpcId(1, nameof(ReqAdvanceLevel)); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReqAdvanceLevel() { if (IsHost) Game.I?.AdvanceLevel(); }
    public void BroadcastLevelAdvance(int level, int biome, long seed) { if (Active) Rpc(nameof(ReceiveLevelAdvanceRpc), level, biome, seed); }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveLevelAdvanceRpc(int level, int biome, long seed) { if (!IsHost) Game.I?.ApplyLevelAdvance(level, (Biome)biome, seed); }
    private void OnPeerDisconnected(long id)
    {
        if (_remotes.TryGetValue(id, out var av) && GodotObject.IsInstanceValid(av)) av.QueueFree();
        _remotes.Remove(id);
        _peerCat.Remove(id);
        if (_rcrescents.TryGetValue(id, out var cl)) { foreach (var o in cl) if (GodotObject.IsInstanceValid(o)) o.QueueFree(); _rcrescents.Remove(id); }
        if (_ghostEnts.TryGetValue(id, out var gl)) { foreach (var g in gl) if (GodotObject.IsInstanceValid(g)) g.QueueFree(); _ghostEnts.Remove(id); }
        if (_ghostGuardians.TryGetValue(id, out var gg)) { if (GodotObject.IsInstanceValid(gg)) gg.QueueFree(); _ghostGuardians.Remove(id); }
        if (IsHost) { _downed.Remove(id); EvalGameOver(); }
        if (IsHost) { _ready.Remove(id); if (Game.I != null && Game.I.State == GameState.CharSelect) HostSyncReady(); }   // don't wait on a warden who left char-select
        if (IsHost) Game.I?.ShowToast("A player disconnected.");
    }
    private void OnConnectedToServer() { Status = "connected"; Game.I?.ShowToast("Connected to host!"); }
    private void OnConnectionFailed() { Status = "connection failed"; Active = false; Game.I?.ShowToast("Connection failed."); }
    private void OnServerDisconnected() { Status = "host closed"; Game.I?.ShowToast("Host closed the game."); Disconnect(); }

    // ---- transform sync (broadcast position + facing) ----
    public override void _Process(double delta)
    {
        if (!Active) return;
        _sendT -= (float)delta;
        if (_sendT <= 0f)
        {
            _sendT = 1f / SendHz;
            var mp = Multiplayer.MultiplayerPeer;
            var p = Game.I?.LocalPlayer;
            if (p != null && GodotObject.IsInstanceValid(p) && mp != null
                && mp.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connected
                && Multiplayer.GetPeers().Length > 0)
            {
                var gp = p.GlobalPosition;
                Rpc(nameof(NetState), gp.X, gp.Y, gp.Z, p.Rotation.Y, p.Floating);
                int cn = p.CrescentOrbs.Count;   // (NEW) Lunar Crescent: sync exact orb positions (orbit/fling/rotate all replicate)
                if (cn > 0 || _hadCrescents)
                {
                    _hadCrescents = cn > 0;
                    var cxs = new float[cn]; var cys = new float[cn]; var czs = new float[cn];
                    for (int i = 0; i < cn; i++) { var o = p.CrescentOrbs[i]; cxs[i] = o.GlobalPosition.X; cys[i] = o.GlobalPosition.Y; czs[i] = o.GlobalPosition.Z; }
                    Rpc(nameof(CrescentSnapshot), cxs, cys, czs);
                }
                if (p.VerdantWitch && ++_minTick >= 2)   // ~10Hz: her tree-ents, so allies see them fight
                {
                    _minTick = 0;
                    p.CountEnts();
                    int mn = p.Ents.Count;
                    var mxs = new float[mn]; var mys = new float[mn]; var mzs = new float[mn]; var myaw = new float[mn]; var matk = new int[mn]; var mhp = new float[mn];
                    for (int i = 0; i < mn; i++)
                    {
                        var t = p.Ents[i];
                        mxs[i] = t.GlobalPosition.X; mys[i] = t.GlobalPosition.Y; mzs[i] = t.GlobalPosition.Z;
                        myaw[i] = t.BodyYaw; matk[i] = t.AtkPulse > 0f ? 1 : 0; mhp[i] = t.HpFrac;
                    }
                    Rpc(nameof(MinionSnapshot), mxs, mys, mzs, myaw, matk, mhp);
                    if (p.ActiveGuardian != null && GodotObject.IsInstanceValid(p.ActiveGuardian))   // sync the Ancient Guardian to allies
                    {
                        var g = p.ActiveGuardian; var ggp = g.GlobalPosition;
                        Rpc(nameof(GuardianState), ggp.X, ggp.Y, ggp.Z, g.BodyYaw, g.SlamPhase01());   // send the exact slam phase so ghosts replay the full wind-up + slam
                    }
                }
                if (++_vitalsTick >= 4)   // ~5Hz: HP/mana/shield/blessed/blood for ally HUD bars
                {
                    _vitalsTick = 0;
                    float hpf = p.S.MaxHp > 0 ? Mathf.Clamp(p.Hp / p.S.MaxHp, 0f, 1f) : 0f;
                    float mnf = p.S.ManaMax > 0 ? Mathf.Clamp(p.Mana / p.S.ManaMax, 0f, 1f) : 0f;
                    float shf = p.MaxShield > 0.5f ? Mathf.Clamp(p.Shield / p.MaxShield, 0f, 1f) : 0f;
                    int packed = p.ArmorPacked | (p.StunStateNet << 8) | ((Game.I != null && Game.I.MenuImmune) ? (1 << 10) : 0) | (p.SpecterActive ? (1 << 11) : 0);   // bit 10 = in a menu → bubble+meditation; bit 11 = Specter → violet projection
                    Rpc(nameof(NetVitals), hpf, mnf, shf, p.BlessedT, p.BloodStacks, packed, p.WitchIndex, p.BarkFrac, p.EclipseActive01, p.StormActive ? 1f : 0f, p.S.Luck);
                }
            }
        }
        foreach (var kv in _remotes) if (GodotObject.IsInstanceValid(kv.Value)) kv.Value.Tick((float)delta);
        _allyHealFlush -= (float)delta;
        if (_allyHealFlush <= 0f)
        {
            _allyHealFlush = 0.32f;
            if (_allyHealAccum.Count > 0)
            {
                foreach (var kv in _allyHealAccum)
                {
                    if (kv.Value < 1f) continue;
                    if (_remotes.TryGetValue(kv.Key, out var av) && GodotObject.IsInstanceValid(av) && Game.I != null)
                    {
                        var pop = new DamagePopup();
                        Game.I.AddChild(pop);
                        pop.Init(kv.Value, DamageTypes.Col(DamageType.Holy), av.GlobalPosition, false, true);
                    }
                }
                _allyHealAccum.Clear();
            }
        }

        _deathFlush -= (float)delta;
        if (_deathFlush <= 0f) { _deathFlush = 0.18f; FlushDeaths(); }

        _teamDmgFlush -= (float)delta;
        if (_teamDmgFlush <= 0f)
        {
            _teamDmgFlush = 0.5f;
            var lp = Game.I?.LocalPlayer;
            if (lp != null && lp.DmgWindow > 0f && Multiplayer.GetPeers().Length > 0)
            {
                Rpc(nameof(ReceiveTeamDamage), lp.DmgWindow);
                lp.DmgWindow = 0f;
            }
        }
        if (!IsHost) foreach (var kv in _rpickups) if (GodotObject.IsInstanceValid(kv.Value)) kv.Value.Tick((float)delta);

        // client: report our pause category so the host can decide whether the world runs
        if (!IsHost && Active && Multiplayer.MultiplayerPeer != null
            && Multiplayer.MultiplayerPeer.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connected)
        {
            _stateT -= (float)delta;
            if (_stateT <= 0f) { _stateT = 0.15f; if (Game.I != null) ReportCat(Game.I.LocalCat()); }
        }

        // host: broadcast a snapshot of all enemies so clients can render/position them
        bool connected = Multiplayer.MultiplayerPeer != null
            && Multiplayer.MultiplayerPeer.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connected
            && Multiplayer.GetPeers().Length > 0;
        if (IsHost && connected)
        {
            _enemyT -= (float)delta;
            if (_enemyT <= 0f)
            {
                _enemyT = 1f / EnemyHz;
                var es = Game.I.Enemies;
                // keep the packet under the network MTU (~1392B): sync at most the nearest ~34 enemies to the host
                var live = new System.Collections.Generic.List<Enemy>();
                foreach (var e in es) if (e != null && GodotObject.IsInstanceValid(e) && !e.Dead) live.Add(e);
                if (live.Count > 30)   // cap for MTU (~1392B): 30 enemies × 11 arrays ≈ 1245B
                {
                    // (MP FIX) rank by distance to the NEAREST warden (host + every ally), not just the host — so a foe
                    // chasing a client far from the host still gets synced, instead of clients seeing only the host's swarm.
                    var wardens = new System.Collections.Generic.List<Vector3>();
                    if (Game.I.Player != null) wardens.Add(Game.I.Player.GlobalPosition);
                    wardens.AddRange(AllyPositions());
                    float NearW(Vector3 p) { float best = float.MaxValue; foreach (var w in wardens) { float d = p.DistanceSquaredTo(w); if (d < best) best = d; } return best; }
                    // (MP FIX) a BOSS never loses its slot. It was ranked on raw distance like any swarmer, so in a busy
                    // fight 30 closer trash mobs pushed it out of the packet and it blinked out on every client.
                    float Rank(Enemy e) => e.IsBoss ? -1f : NearW(e.GlobalPosition);
                    live.Sort((a, b) => Rank(a).CompareTo(Rank(b)));
                    live.RemoveRange(30, live.Count - 30);
                }
                var ids = new System.Collections.Generic.List<int>();
                var tys = new System.Collections.Generic.List<int>();
                var eli = new System.Collections.Generic.List<int>();
                var hpf = new System.Collections.Generic.List<float>();
                var st = new System.Collections.Generic.List<int>();
                var af = new System.Collections.Generic.List<int>();
                var xs = new System.Collections.Generic.List<float>();
                var ys = new System.Collections.Generic.List<float>();   // (NEW) Y so clients see fling arcs / flyer height
                var zs = new System.Collections.Generic.List<float>();
                var brn = new System.Collections.Generic.List<int>();    // (NEW) Ember burn stacks for the client HUD
                var siz = new System.Collections.Generic.List<int>();    // (NEW) per-enemy size multiplier ×100 → clients render matching sizes + hitboxes
                var dm  = new System.Collections.Generic.List<int>();    // (DOOM) packed bank+fuse+puppet+lethal — StatusMask has no bits left
                foreach (var e in live)
                {
                    if (e == null || !GodotObject.IsInstanceValid(e) || e.Dead) continue;
                    ids.Add(e.NetId); tys.Add(e.TypeIdx); eli.Add(e.Elite ? 1 : 0);
                    hpf.Add(e.MaxHp > 0 ? e.Hp / e.MaxHp : 1f);
                    st.Add(e.StatusMask());
                    siz.Add(Mathf.RoundToInt(e.SizeMul * 100f));
                    af.Add(e.Affix);
                    xs.Add(e.GlobalPosition.X); ys.Add(e.GlobalPosition.Y); zs.Add(e.GlobalPosition.Z);
                    brn.Add(Mathf.CeilToInt(e.BurnStacks));
                    dm.Add(e.PackDoom());
                }
                Rpc(nameof(EnemySnapshot), ids.ToArray(), tys.ToArray(), eli.ToArray(), hpf.ToArray(), st.ToArray(), xs.ToArray(), ys.ToArray(), zs.ToArray(), af.ToArray(), brn.ToArray(), siz.ToArray(), _deadIds.ToArray(), dm.ToArray());
                _deadIds.Clear();
                NetEnemiesSynced = ids.Count; NetEnemyBytes = ids.Count * 12 * 4 + 48;   // 12 arrays × 4B/elem + RPC/header overhead (the 12th is Doom)

                // pickups: live orbs (kind 0) + unopened chests (kind 1)
                var pid = new System.Collections.Generic.List<int>();
                var pk = new System.Collections.Generic.List<int>();
                var px = new System.Collections.Generic.List<float>();
                var pz = new System.Collections.Generic.List<float>();
                var pcol = new System.Collections.Generic.List<int>();
                Game.I.Orbs.RemoveAll(o => o == null || !GodotObject.IsInstanceValid(o));
                // keep this packet under the network MTU (~1392B): in heavy combat XP orbs pile up unbounded, so
                // sync only the nearest ~48 to the local host player. Each entry is ~20B (5 arrays) → ~960B + chests,
                // safely under MTU. Distant off-screen orbs don't need client visuals anyway. (was uncapped → packet
                // fragmentation + loss → the "MP lag" in big fights)
                var orbs = Game.I.Orbs;
                if (orbs.Count > 48)
                {
                    var pc = Game.I.Player != null ? Game.I.Player.GlobalPosition : Vector3.Zero;
                    orbs = new System.Collections.Generic.List<Orb>(orbs);
                    orbs.Sort((a, b) => a.GlobalPosition.DistanceSquaredTo(pc).CompareTo(b.GlobalPosition.DistanceSquaredTo(pc)));
                    orbs.RemoveRange(48, orbs.Count - 48);
                }
                foreach (var o in orbs) { pid.Add(o.NetId); pk.Add(0); px.Add(o.GlobalPosition.X); pz.Add(o.GlobalPosition.Z); pcol.Add(PackColor(o.Tint)); }
                // chests are now scattered map-wide at load (up to 12/warden), so this unreliable packet can blow past MTU. Sync only
                // the nearest ~18 unopened chests to the host — plenty on-screen; the rest stream in as the party roams. (MP-only; solo
                // has no snapshot and sees all chests locally.) TODO: a one-shot reliable chest broadcast would remove this near-host bias.
                var chestList = new System.Collections.Generic.List<Chest>();
                foreach (var ch in Game.I.Chests) if (ch != null && GodotObject.IsInstanceValid(ch) && !ch.Opened) chestList.Add(ch);
                if (chestList.Count > 18)
                {
                    var cpc = Game.I.Player != null ? Game.I.Player.GlobalPosition : Vector3.Zero;
                    chestList.Sort((a, b) => a.GlobalPosition.DistanceSquaredTo(cpc).CompareTo(b.GlobalPosition.DistanceSquaredTo(cpc)));
                    chestList.RemoveRange(18, chestList.Count - 18);
                }
                foreach (var ch in chestList) { pid.Add(ch.NetId); pk.Add(ch.Hidden ? 2 : 1); px.Add(ch.GlobalPosition.X); pz.Add(ch.GlobalPosition.Z); pcol.Add(0); }
                Rpc(nameof(PickupSnapshot), pid.ToArray(), pk.ToArray(), px.ToArray(), pz.ToArray(), pcol.ToArray());
                NetOrbsSynced = orbs.Count; NetPickupBytes = pid.Count * 5 * 4 + 48;   // 5 arrays × 4B/elem + overhead (orbs + chests)

                // ritual circles: id, type, x, z, active, status
                var rid = new System.Collections.Generic.List<int>();
                var rty = new System.Collections.Generic.List<int>();
                var rx = new System.Collections.Generic.List<float>();
                var rz = new System.Collections.Generic.List<float>();
                var rac = new System.Collections.Generic.List<int>();
                var rst = new System.Collections.Generic.List<float>();
                foreach (var rc in Game.I.Rituals)
                {
                    if (rc == null || !GodotObject.IsInstanceValid(rc) || rc.Done) continue;
                    rid.Add(rc.NetId); rty.Add((int)rc.Type); rx.Add(rc.GlobalPosition.X); rz.Add(rc.GlobalPosition.Z);
                    rac.Add(rc.Active ? 1 : 0); rst.Add(rc.Status);
                }
                Rpc(nameof(RitualSnapshot), rid.ToArray(), rty.ToArray(), rx.ToArray(), rz.ToArray(), rac.ToArray(), rst.ToArray());

                // vendors: mystic (kind 0) + scroll-keeper (kind 1), only while still present
                var vid = new System.Collections.Generic.List<int>();
                var vk = new System.Collections.Generic.List<int>();
                var vx = new System.Collections.Generic.List<float>();
                var vz = new System.Collections.Generic.List<float>();
                var mys = Game.I.CurMystic;
                if (mys != null && GodotObject.IsInstanceValid(mys)) { vid.Add(mys.NetId); vk.Add(0); vx.Add(mys.GlobalPosition.X); vz.Add(mys.GlobalPosition.Z); }
                var scr = Game.I.CurScroll;
                if (scr != null && GodotObject.IsInstanceValid(scr)) { vid.Add(scr.NetId); vk.Add(1); vx.Add(scr.GlobalPosition.X); vz.Add(scr.GlobalPosition.Z); }
                var shp = Game.I.CurShop;   // peddler (kind 2) — not claimable; lingers so both players can shop
                if (shp != null && GodotObject.IsInstanceValid(shp)) { vid.Add(shp.NetId); vk.Add(2); vx.Add(shp.GlobalPosition.X); vz.Add(shp.GlobalPosition.Z); }
                Rpc(nameof(VendorSnapshot), vid.ToArray(), vk.ToArray(), vx.ToArray(), vz.ToArray());

                // roulette wheels (up to 3)
                var wid = new System.Collections.Generic.List<int>();
                var wx = new System.Collections.Generic.List<float>();
                var wz = new System.Collections.Generic.List<float>();
                foreach (var rm in Game.I.RouletteList)
                {
                    if (rm == null || !GodotObject.IsInstanceValid(rm)) continue;
                    wid.Add(rm.NetId); wx.Add(rm.GlobalPosition.X); wz.Add(rm.GlobalPosition.Z);
                }
                Rpc(nameof(RouletteSnapshot), wid.ToArray(), wx.ToArray(), wz.ToArray());
            }
        }
    }

    // ---- client hit -> host applies damage on the real enemy ----
    public void ReportHit(int netId, float dmg, int type, bool crit = false)
    {
        if (!Active || IsHost) return;
        RpcId(1, nameof(ReceiveHit), netId, dmg, type, crit);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveHit(int netId, float dmg, int type, bool crit)
    {
        if (!IsHost || Game.I == null) return;
        long sender = Multiplayer.GetRemoteSenderId();
        foreach (var e in Game.I.Enemies)
            if (e != null && GodotObject.IsInstanceValid(e) && !e.Dead && e.NetId == netId)
            {
                Game.I.AttackerPeer = sender;                       // (NEW) so a kill here credits the reporting client, exactly
                e.Hurt(dmg, (DamageType)type, true, crit);         // (NEW) carry crit → armor-bypass (Sentinel core) + crit plink resolve on the host
                Game.I.AttackerPeer = Game.I.LocalPeer;             // reset: the host's own subsequent hits credit the host
                break;
            }
    }

    // ---- Gale storm authority (Cyclone pull / Hurricane fling / area grind) ----
    // Enemies are host-owned, so a client's Cyclone/Hurricane can't move them directly. It asks the host to
    // apply a radial effect to the real enemies in an area; host/solo apply it immediately, and the motion
    // syncs to everyone via the (Y-bearing) enemy snapshot. mode: 0 pull-in, 1 fling-up, 2 area damage. (NEW)
    public void StormForce(Vector3 center, float radius, int mode, float power, float chance = 1f, float fallMul = 1f)
    {
        if (NetConnected() && !IsHost) { RpcId(1, nameof(ReqStormForce), center.X, center.Z, radius, mode, power, chance, fallMul); return; }
        ApplyStormForce(center, radius, mode, power, chance, fallMul);
    }

    // (PHOENIX) fire a phoenix dive. The HOST owns the enemy grab/carry/damage sim; every other machine flies a
    // deterministic visual bird. Mirrors StormForce's host-routing (client caster → RpcId(1) → host simulates + relays).
    public void FirePhoenixDive(Player caster, Vector3 origin, Vector3 dir, int tier, bool mod, float touchDmg, float grabDmg, float bossFrac, float baseUnit)
    {
        if (!NetConnected()) { Game.I.SpawnPhoenixDive(caster, origin, dir, tier, mod, true, touchDmg, grabDmg, bossFrac, baseUnit); return; }   // solo: simulate locally
        if (IsHost)
        {
            Game.I.SpawnPhoenixDive(caster, origin, dir, tier, mod, true, touchDmg, grabDmg, bossFrac, baseUnit);   // host simulates
            Rpc(nameof(ReceivePhoenixDive), origin.X, origin.Y, origin.Z, dir.X, dir.Z, tier, mod, touchDmg, grabDmg, bossFrac, baseUnit);   // clients fly the visual
        }
        else RpcId(1, nameof(ReqPhoenixDive), origin.X, origin.Y, origin.Z, dir.X, dir.Z, tier, mod, touchDmg, grabDmg, bossFrac, baseUnit);   // client caster asks the host to run it
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReqPhoenixDive(float ox, float oy, float oz, float dx, float dz, int tier, bool mod, float touchDmg, float grabDmg, float bossFrac, float baseUnit)
    {
        if (!IsHost) return;
        var origin = new Vector3(ox, oy, oz); var dir = new Vector3(dx, 0f, dz);
        Game.I.SpawnPhoenixDive(null, origin, dir, tier, mod, true, touchDmg, grabDmg, bossFrac, baseUnit);   // host simulates on behalf of the client caster
        Rpc(nameof(ReceivePhoenixDive), ox, oy, oz, dx, dz, tier, mod, touchDmg, grabDmg, bossFrac, baseUnit);   // relay the visual to all clients (incl. the caster)
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceivePhoenixDive(float ox, float oy, float oz, float dx, float dz, int tier, bool mod, float touchDmg, float grabDmg, float bossFrac, float baseUnit)
    {
        if (IsHost) return;   // the host already spawned the simulating instance
        Game.I.SpawnPhoenixDive(null, new Vector3(ox, oy, oz), new Vector3(dx, 0f, dz), tier, mod, false, touchDmg, grabDmg, bossFrac, baseUnit);   // visual-only bird
    }

    // (ARCANE STORM) host owns the rain-field strikes; every other machine renders a visual-only storm. Same routing as the phoenix dive.
    public void FireArcaneStorm(Player caster, Vector3 pos, float radius, float dur, bool mod, int tier, float baseDmg, float hpScale, float bossCapMul, float critChance, float critMul)
    {
        if (!NetConnected()) { Game.I.SpawnArcaneStorm(caster, pos, radius, dur, false, mod, tier, baseDmg, hpScale, bossCapMul, critChance, critMul); return; }
        if (IsHost)
        {
            Game.I.SpawnArcaneStorm(caster, pos, radius, dur, false, mod, tier, baseDmg, hpScale, bossCapMul, critChance, critMul);
            Rpc(nameof(ReceiveArcaneStorm), pos.X, pos.Y, pos.Z, radius, dur, mod, tier, baseDmg, hpScale, bossCapMul, critChance, critMul);
        }
        else RpcId(1, nameof(ReqArcaneStorm), pos.X, pos.Y, pos.Z, radius, dur, mod, tier, baseDmg, hpScale, bossCapMul, critChance, critMul);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReqArcaneStorm(float px, float py, float pz, float radius, float dur, bool mod, int tier, float baseDmg, float hpScale, float bossCapMul, float critChance, float critMul)
    {
        if (!IsHost) return;
        Game.I.SpawnArcaneStorm(null, new Vector3(px, py, pz), radius, dur, false, mod, tier, baseDmg, hpScale, bossCapMul, critChance, critMul);   // host simulates on behalf of the client caster
        Rpc(nameof(ReceiveArcaneStorm), px, py, pz, radius, dur, mod, tier, baseDmg, hpScale, bossCapMul, critChance, critMul);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReceiveArcaneStorm(float px, float py, float pz, float radius, float dur, bool mod, int tier, float baseDmg, float hpScale, float bossCapMul, float critChance, float critMul)
    {
        if (IsHost) return;
        Game.I.SpawnArcaneStorm(null, new Vector3(px, py, pz), radius, dur, true, mod, tier, baseDmg, hpScale, bossCapMul, critChance, critMul);   // visual-only storm
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    private void ReqStormForce(float cx, float cz, float radius, int mode, float power, float chance, float fallMul)
    {
        if (!IsHost) return;
        ApplyStormForce(new Vector3(cx, 0f, cz), radius, mode, power, chance, fallMul);
    }
    private void ApplyStormForce(Vector3 center, float radius, int mode, float power, float chance = 1f, float fallMul = 1f)
    {
        if (Game.I == null) return;
        foreach (var e in Game.I.Enemies.ToArray())
        {
            if (e == null || !GodotObject.IsInstanceValid(e) || e.Dead || e.Remote) continue;   // real enemies only
            Vector3 flat = e.GlobalPosition - center; flat.Y = 0; float d = flat.Length();
            if (d > radius + e.Radius) continue;
            if (mode == 1 && chance < 1f && GD.Randf() >= chance) continue;   // (NEW) Eruption: per-enemy fling chance (scales with rarity)
            if (mode == 0) e.PullToward(center, power);                                          // Cyclone drag-in
            else if (mode == 2) e.Hurt(power, DamageType.Wind, false);                           // area grind tick
            else if (mode == 4) e.Fling(Vector3.Up * power, fallMul);                             // Updraft: lift straight up (mass-scaled → big foes barely rise); fallMul = Tempest
            else if (mode == 3)                                                                  // Wind Rush: fling outward/back + slight lift
            {
                Vector3 outw = d > 0.1f ? flat.Normalized() : Vector3.Forward;
                e.Fling(outw * power + Vector3.Up * (power * 0.45f));
            }
            else                                                                                 // mode 1: fling up + swirl (Hurricane)
            {
                Vector3 outw = d > 0.1f ? flat.Normalized() : Vector3.Forward;
                Vector3 tang = new Vector3(-outw.Z, 0, outw.X);
                e.Fling(Vector3.Up * (power + GD.Randf() * 8f) + tang * 10f + outw * 4f);
            }
        }
    }

    // Eyewall (Hurricane legendary): the caster pulses its ground zone to everyone. Each machine buffs its OWN
    // player + minions (the real ones live there; ghost ents are visual only) if they're standing in it, so a
    // teammate fighting inside the storm — and their tree-ents — move/cast/charge faster. The buff self-decays,
    // so it lingers briefly after stepping out. (Note: in a 3+ player game with a CLIENT caster, a second client
    // only gets it if relayed; in solo/host-cast and 2-player it covers everyone.) (NEW)
    public void BroadcastWindZone(Vector3 center, float radius)
    {
        ApplyWindZoneLocal(center, radius);
        if (NetConnected()) Rpc(nameof(ReceiveWindZone), center.X, center.Z, radius);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    private void ReceiveWindZone(float cx, float cz, float radius) => ApplyWindZoneLocal(new Vector3(cx, 0f, cz), radius);
    private void ApplyWindZoneLocal(Vector3 center, float radius)
    {
        var p = Game.I?.Player;
        if (p == null) return;
        float r = radius + 1f;
        Vector3 dp = p.GlobalPosition - center; dp.Y = 0;
        if (dp.Length() <= r) p.GrantWindBoon(0.6f);
        foreach (var t in p.Ents.ToArray())   // the local player's own tree-ents (client-owned minions)
        {
            if (t == null || !GodotObject.IsInstanceValid(t)) continue;
            Vector3 dt = t.GlobalPosition - center; dt.Y = 0;
            if (dt.Length() <= r) t.GrantWindBoon(0.6f);
        }
    }

    // ---- client debuff -> host applies it on the real enemy (kind: 0 bleed,1 slow,2 root,3 mark) ----
    public void ReportStatus(int netId, int kind, float a, float b, float c)
    {
        if (!Active || IsHost) return;
        RpcId(1, nameof(ReceiveStatus), netId, kind, a, b, c);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveStatus(int netId, int kind, float a, float b, float c)
    {
        if (!IsHost || Game.I == null) return;
        foreach (var e in Game.I.Enemies)
        {
            if (e == null || !GodotObject.IsInstanceValid(e) || e.Dead || e.Remote || e.NetId != netId) continue;
            switch (kind)
            {
                case 0: { int fl = (int)(c + 0.01f); e.Bleed(a, b, (fl & 1) != 0, (int)Multiplayer.GetRemoteSenderId(), 1f, (fl & 2) != 0); break; }   // owner = the client who cast it; bit1 = rot, bit2 = persist (Blood Rot mod)
                case 1: e.Slow(a, b); break;
                case 2: e.Root(a); break;
                case 3: e.Mark(a, b, (int)c); break;
                case 4: e.Poison(a, b, (int)Multiplayer.GetRemoteSenderId()); break;   // (NEW) poison now routes to the host, attributed to its caster
                case 5: e.AddFreeze(a, b, c); break;   // (NEW) frost witch freeze stacks + caster's frost profile (threshMul=b, durBonus=c) — best-of on the host
                case 6: e.AddBurn(a, b, c, 0f, (int)Multiplayer.GetRemoteSenderId()); break;  // (NEW) Ember burn stacks (amt=a, perStackDps=b, bombFlat=c); owner = the client who cast it
                case 7: e.ConsumeCurse(a, b, c); break;   // (NEW) Forsaken voodoo crush: a=frac of stacks, b=damage per stack, c=effective-stack cap
                case 8: e.SetArcaneMark(a > 0.5f); break;   // (NEW) Arcane witch: a client caster's mark on/off on the host (a=1 on, 0 off)
                case 9: e.MarkConduit(a); break;            // (NEW) a client's conduit producer (Conduit Swarm / Chain Reaction) → host applies the self-timed brand
                case 10: e.AddDoom(a, (long)Multiplayer.GetRemoteSenderId()); break;   // (DOOM) a client banking Doom — the host owns the bank, fuse and detonation; owner = the caster so the kill credits them
                case 11:   // (DOOM) a client turning a foe: a = the victim's NetId, b = leash, c = the Doom each landed blow feeds
                {
                    int vid = Mathf.RoundToInt(a);
                    foreach (var v in Game.I.Enemies)
                        if (v != null && GodotObject.IsInstanceValid(v) && !v.Dead && !v.Remote && v.NetId == vid)
                        { e.Puppet(v, b, (long)Multiplayer.GetRemoteSenderId(), c); break; }
                    break;
                }
                // (DOOM) a client's Rout. The panic direction is "away from the fight"; the host's own witch is the best
                // stand-in it has for that, since the caster's exact position isn't carried in these three float slots.
                case 12: if (Game.I.Player != null) e.Flee(Game.I.Player.GlobalPosition, a, b > 0.5f); break;
            }
            return;
        }
    }

    // (NEW) Forsaken curse application (6 args, so it can't ride ReportStatus's 3 slots)
    public void ReportCurse(int netId, float amt, int group, int bonusType, float bonusMul, float shareFrac, int bonusType2 = -1)
    {
        if (!Active || IsHost) return;
        RpcId(1, nameof(ReceiveCurse), netId, amt, group, bonusType, bonusMul, shareFrac, bonusType2);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveCurse(int netId, float amt, int group, int bonusType, float bonusMul, float shareFrac, int bonusType2)
    {
        if (!IsHost || Game.I == null) return;
        foreach (var e in Game.I.Enemies)
        {
            if (e == null || !GodotObject.IsInstanceValid(e) || e.Dead || e.Remote || e.NetId != netId) continue;
            e.AddCurse(amt, group, (DamageType)bonusType, bonusMul, shareFrac, bonusType2);
            return;
        }
    }

    private static int PackColor(Color c) => ((int)(c.R * 255) << 16) | ((int)(c.G * 255) << 8) | (int)(c.B * 255);
    private static Color UnpackColor(int v) => new Color(((v >> 16) & 0xFF) / 255f, ((v >> 8) & 0xFF) / 255f, (v & 0xFF) / 255f);

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    private void PickupSnapshot(int[] ids, int[] kinds, float[] xs, float[] zs, int[] cols)
    {
        if (IsHost || Game.I == null) return;
        var seen = new HashSet<int>();
        for (int i = 0; i < ids.Length; i++)
        {
            int id = ids[i]; seen.Add(id);
            if (!_rpickups.TryGetValue(id, out var pu) || !GodotObject.IsInstanceValid(pu))
            {
                pu = new RemotePickup();
                Game.I.AddChild(pu);
                pu.Setup(kinds[i], UnpackColor(cols[i]));
                _rpickups[id] = pu;
            }
            pu.SetTarget(new Vector3(xs[i], kinds[i] == 0 ? 1.2f : 0f, zs[i]));
        }
        var gone = new List<int>();
        foreach (var kv in _rpickups) if (!seen.Contains(kv.Key)) gone.Add(kv.Key);
        foreach (var id in gone)
        {
            if (_rpickups.TryGetValue(id, out var pu) && GodotObject.IsInstanceValid(pu)) pu.QueueFree();
            _rpickups.Remove(id);
        }
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    private void EnemySnapshot(int[] ids, int[] types, int[] elite, float[] hpf, int[] status, float[] xs, float[] ys, float[] zs, int[] aff, int[] burn, int[] siz, int[] dead, int[] doom = null)
    {
        if (IsHost || Game.I == null) return;   // only clients render proxies
        ulong nowMs = Time.GetTicksMsec();
        int n = ids.Length;
        for (int i = 0; i < n; i++)
        {
            int id = ids[i];
            _enemySeen[id] = nowMs;
            if (!_renemies.TryGetValue(id, out var e) || !GodotObject.IsInstanceValid(e))
            {
                e = new Enemy();
                e.Configure(EnemyKinds.Types[types[i]], 1);   // configure BEFORE AddChild — _Ready builds the mesh from this
                if (elite[i] == 1) e.MakeElite();              // match the host's gold/larger elite look
                e.SizeMul = (siz != null && i < siz.Length) ? siz[i] / 100f : 1f;   // (NEW) use the HOST's size so the mesh + hitbox match — _Ready applies it before building
                e.Remote = true;
                e.NetId = id;
                e.TypeIdx = types[i];
                Game.I.AddChild(e);
                Game.I.Enemies.Add(e);                          // so the client's own attacks can hit it
                _renemies[id] = e;
            }
            e.SetRemoteTarget(new Vector3(xs[i], ys[i], zs[i]));   // (NEW) real height: fling arcs + flyers now show on clients
            e.Hp = hpf[i] * e.MaxHp;                            // reflect damage on the client health bar
            e.SetAffix(aff[i]);                                 // affix aura/visual
            e.SetRemoteStatus(status[i]);                       // reflect bleed/slow/root/mark tints & rings
            e.SetRemoteBurn(i < burn.Length ? burn[i] : 0);     // (NEW) Ember burn stacks for the HUD progress
            e.SetRemoteDoom(doom != null && i < doom.Length ? doom[i] : 0);   // (DOOM) the host's bank/fuse, so the overhead read matches on every machine
        }
        // (MP FIX) Reap ONLY what the host says actually died. Absence from a snapshot now means "capped out of this
        // packet", not "dead" — a foe pushed out by the 30-cap keeps its proxy and simply stops updating until it's
        // back in range. The old absence-means-dead rule destroyed and rebuilt ~25 proxies a tick in a big fight.
        var gone = new List<int>();
        if (dead != null) foreach (int id in dead) gone.Add(id);
        // safety net: if a despawn RPC is ever lost, don't leave a ghost standing there forever
        foreach (var kv in _enemySeen) if (nowMs - kv.Value > ProxyStaleMs) gone.Add(kv.Key);
        foreach (var id in gone)
        {
            if (_renemies.TryGetValue(id, out var e) && GodotObject.IsInstanceValid(e))
            { Game.I.Enemies.Remove(e); e.QueueFree(); }
            _renemies.Remove(id);
            _enemySeen.Remove(id);
        }
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    private void MinionSnapshot(float[] xs, float[] ys, float[] zs, float[] yaw, int[] atk, float[] hpf)
    {
        if (Game.I == null) return;
        long id = Multiplayer.GetRemoteSenderId();
        if (!_ghostEnts.TryGetValue(id, out var list)) { list = new System.Collections.Generic.List<Thornling>(); _ghostEnts[id] = list; }
        int n = xs.Length;
        // grow/shrink the ghost list to match the ally's live ent count
        while (list.Count < n)
        {
            var g = new Thornling { Ghost = true };
            Game.I.AddChild(g);
            g.GlobalPosition = new Vector3(xs[list.Count], ys[list.Count], zs[list.Count]);
            list.Add(g);
        }
        while (list.Count > n)
        {
            var g = list[list.Count - 1];
            if (GodotObject.IsInstanceValid(g)) g.QueueFree();
            list.RemoveAt(list.Count - 1);
        }
        bool barked = _remotes.TryGetValue(id, out var oav) && GodotObject.IsInstanceValid(oav) && oav.Bark > 0f;
        for (int i = 0; i < n; i++)
            if (GodotObject.IsInstanceValid(list[i])) { list[i].GhostHpFrac = hpf[i]; list[i].SetGhost(new Vector3(xs[i], ys[i], zs[i]), yaw[i], atk[i] == 1); list[i].SetThorns(barked); }
    }

    // host-side: let melee enemies consider other players' ents (ghosts) as aggro targets too
    public void ConsiderGhostMinions(Vector3 from, ref float bestSq, ref Vector3 pos, ref bool found)
    {
        foreach (var kv in _ghostEnts)
            foreach (var t in kv.Value)
            {
                if (t == null || !GodotObject.IsInstanceValid(t)) continue;
                float d = (t.GlobalPosition - from).LengthSquared();
                if (d < bestSq) { bestSq = d; pos = t.GlobalPosition; found = true; }
            }
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    private void GuardianState(float x, float y, float z, float yaw, float slamPhase)
    {
        if (Game.I == null) return;
        long id = Multiplayer.GetRemoteSenderId();
        if (!_ghostGuardians.TryGetValue(id, out var g) || !GodotObject.IsInstanceValid(g))
        {
            g = new Guardian { Ghost = true };
            Game.I.AddChild(g);
            g.GlobalPosition = new Vector3(x, y, z);
            _ghostGuardians[id] = g;
        }
        g.SetGhost(new Vector3(x, y, z), yaw, slamPhase);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    private void NetState(float x, float y, float z, float yaw, bool floating)
    {
        long id = Multiplayer.GetRemoteSenderId();
        if (!_remotes.TryGetValue(id, out var av) || !GodotObject.IsInstanceValid(av))
        {
            av = new RemoteAvatar();
            Game.I.AddChild(av);
            _remotes[id] = av;
            av.SetTeamColor((int)(id % 4));   // same peer id → same color on every machine
        }
        av.SetTarget(new Vector3(x, y, z), yaw);
        av.SetFloating(floating);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
    private void NetVitals(float hp, float mana, float shield, float blessed, int blood, int armor, int witch, float bark, float eclipse, float storm, float luck)
    {
        long id = Multiplayer.GetRemoteSenderId();
        if (_remotes.TryGetValue(id, out var av) && GodotObject.IsInstanceValid(av))
            av.SetVitals(hp, mana, shield, blessed, blood, armor, witch, bark, eclipse, storm, luck);
    }

    public System.Collections.Generic.List<RemoteAvatar> AllyAvatars()
    {
        var list = new System.Collections.Generic.List<RemoteAvatar>();
        foreach (var kv in _remotes) if (GodotObject.IsInstanceValid(kv.Value)) list.Add(kv.Value);
        return list;
    }
    public System.Collections.Generic.List<Vector3> GhostMinionPositions()
    {
        var list = new System.Collections.Generic.List<Vector3>();
        foreach (var kv in _ghostEnts) foreach (var t in kv.Value) if (GodotObject.IsInstanceValid(t)) list.Add(t.GlobalPosition);
        return list;
    }
}
