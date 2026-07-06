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
        else if (kind == 3) { pl?.GrantRandomArmor(); Game.I.Hud?.Banner("an armor charge"); }
    }

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
    public void HealAlliesNear(Vector3 at, float r, float amt)
    {
        if (!Active) return;
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
            }
        }
        if (healedAlly) Game.I?.Player?.ComboFromDot();   // healing allies drips combo (reduced DoT rate)
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
            case 52: { var df = new DeepFreeze(); Game.I.AddChild(df); df.Init(null, o, a, b, true); break; }               // Deep Freeze ghost
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
    public bool AnyDowned() => _downed.Count > 0;
    public System.Collections.Generic.List<Vector3> AllyPositions()
    {
        var list = new System.Collections.Generic.List<Vector3>();
        foreach (var kv in _remotes) if (kv.Value != null && GodotObject.IsInstanceValid(kv.Value)) list.Add(kv.Value.GlobalPosition);
        return list;
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
        int total = _remotes.Count + 1;
        if (_downed.Count >= total) { Rpc(nameof(ReceiveGameOver)); Game.I?.GameOver(); }
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveGameOver() { Game.I?.GameOver(); }

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

    // AoE from an enemy/boss: hurt the local player + every ally avatar inside a radius (works solo + MP)
    public void HurtPlayersIn(Vector3 center, float radius, float dmg)
    {
        if (Game.I?.Player != null && !Game.I.Player.Downed)
        { var d = Game.I.Player.GlobalPosition - center; d.Y = 0f; if (d.Length() < radius) Game.I.Player.Hurt(dmg); }
        if (!Active || !IsHost) return;
        foreach (var kv in _remotes)
        { if (!GodotObject.IsInstanceValid(kv.Value)) continue; var d = kv.Value.GlobalPosition - center; d.Y = 0f; if (d.Length() < radius) DamagePlayer(kv.Key, dmg); }
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
        for (int i = 0; i < xs.Length; i++) p.OnBloodAuraKill(new Vector3(xs[i], ys[i], zs[i]));
    }

    // every player shares a slice of the damage they deal as ult charge, so the team's ult meters
    // fill together — melee/AoE witches aren't starved next to a ranged witch farming chip damage.
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
    private void ReceiveTeamDamage(float dmg)
    {
        var p = Game.I?.Player;
        if (p == null || p.Ult == Player.UltKind.None || p.UltActive) return;
        p.UltCharge = Mathf.Min(1f, p.UltCharge + Mathf.Min(0.03f, dmg * 0.00008f));
    }

    // ---- connection lifecycle ----
    private void OnPeerConnected(long id)
    {
        if (IsHost) { Game.I?.ShowToast("A player connected!"); RpcId(id, nameof(ReceiveWorldSeed), Game.I != null ? Game.I.WorldSeed : 0L); }
    }
    // host -> a specific client: here's the map seed, rebuild your world to match ours (NEW)
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
    private void ReceiveWorldSeed(long seed) { if (!IsHost) Game.I?.ReseedWorld(seed); }
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
                        Rpc(nameof(GuardianState), ggp.X, ggp.Y, ggp.Z, g.BodyYaw, g.TakeSlamFlag() ? 1 : 0);
                    }
                }
                if (++_vitalsTick >= 4)   // ~5Hz: HP/mana/shield/blessed/blood for ally HUD bars
                {
                    _vitalsTick = 0;
                    float hpf = p.S.MaxHp > 0 ? Mathf.Clamp(p.Hp / p.S.MaxHp, 0f, 1f) : 0f;
                    float mnf = p.S.ManaMax > 0 ? Mathf.Clamp(p.Mana / p.S.ManaMax, 0f, 1f) : 0f;
                    float shf = p.MaxShield > 0.5f ? Mathf.Clamp(p.Shield / p.MaxShield, 0f, 1f) : 0f;
                    Rpc(nameof(NetVitals), hpf, mnf, shf, p.BlessedT, p.BloodStacks, p.ArmorPacked | (p.StunStateNet << 8), p.WitchIndex, p.BarkFrac, p.EclipseActive01, p.StormActive ? 1f : 0f);
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
                if (live.Count > 34)
                {
                    var pc = Game.I.Player != null ? Game.I.Player.GlobalPosition : Vector3.Zero;
                    live.Sort((a, b) => a.GlobalPosition.DistanceSquaredTo(pc).CompareTo(b.GlobalPosition.DistanceSquaredTo(pc)));
                    live.RemoveRange(34, live.Count - 34);
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
                foreach (var e in live)
                {
                    if (e == null || !GodotObject.IsInstanceValid(e) || e.Dead) continue;
                    ids.Add(e.NetId); tys.Add(e.TypeIdx); eli.Add(e.Elite ? 1 : 0);
                    hpf.Add(e.MaxHp > 0 ? e.Hp / e.MaxHp : 1f);
                    st.Add(e.StatusMask());
                    af.Add(e.Affix);
                    xs.Add(e.GlobalPosition.X); ys.Add(e.GlobalPosition.Y); zs.Add(e.GlobalPosition.Z);
                }
                Rpc(nameof(EnemySnapshot), ids.ToArray(), tys.ToArray(), eli.ToArray(), hpf.ToArray(), st.ToArray(), xs.ToArray(), ys.ToArray(), zs.ToArray(), af.ToArray());

                // pickups: live orbs (kind 0) + unopened chests (kind 1)
                var pid = new System.Collections.Generic.List<int>();
                var pk = new System.Collections.Generic.List<int>();
                var px = new System.Collections.Generic.List<float>();
                var pz = new System.Collections.Generic.List<float>();
                var pcol = new System.Collections.Generic.List<int>();
                Game.I.Orbs.RemoveAll(o => o == null || !GodotObject.IsInstanceValid(o));
                foreach (var o in Game.I.Orbs) { pid.Add(o.NetId); pk.Add(0); px.Add(o.GlobalPosition.X); pz.Add(o.GlobalPosition.Z); pcol.Add(PackColor(o.Tint)); }
                foreach (var ch in Game.I.Chests)
                {
                    if (ch == null || !GodotObject.IsInstanceValid(ch) || ch.Opened) continue;
                    pid.Add(ch.NetId); pk.Add(1); px.Add(ch.GlobalPosition.X); pz.Add(ch.GlobalPosition.Z); pcol.Add(0);
                }
                Rpc(nameof(PickupSnapshot), pid.ToArray(), pk.ToArray(), px.ToArray(), pz.ToArray(), pcol.ToArray());

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
        foreach (var e in Game.I.Enemies)
            if (e != null && GodotObject.IsInstanceValid(e) && !e.Dead && e.NetId == netId)
            { e.Hurt(dmg, (DamageType)type, true, crit); break; }   // (NEW) carry crit → armor-bypass (Sentinel core) + crit plink resolve on the host
    }

    // ---- Gale storm authority (Cyclone pull / Hurricane fling / area grind) ----
    // Enemies are host-owned, so a client's Cyclone/Hurricane can't move them directly. It asks the host to
    // apply a radial effect to the real enemies in an area; host/solo apply it immediately, and the motion
    // syncs to everyone via the (Y-bearing) enemy snapshot. mode: 0 pull-in, 1 fling-up, 2 area damage. (NEW)
    public void StormForce(Vector3 center, float radius, int mode, float power)
    {
        if (NetConnected() && !IsHost) { RpcId(1, nameof(ReqStormForce), center.X, center.Z, radius, mode, power); return; }
        ApplyStormForce(center, radius, mode, power);
    }
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    private void ReqStormForce(float cx, float cz, float radius, int mode, float power)
    {
        if (!IsHost) return;
        ApplyStormForce(new Vector3(cx, 0f, cz), radius, mode, power);
    }
    private void ApplyStormForce(Vector3 center, float radius, int mode, float power)
    {
        if (Game.I == null) return;
        foreach (var e in Game.I.Enemies.ToArray())
        {
            if (e == null || !GodotObject.IsInstanceValid(e) || e.Dead || e.Remote) continue;   // real enemies only
            Vector3 flat = e.GlobalPosition - center; flat.Y = 0; float d = flat.Length();
            if (d > radius + e.Radius) continue;
            if (mode == 0) e.PullToward(center, power);                                          // Cyclone drag-in
            else if (mode == 2) e.Hurt(power, DamageType.Wind, false);                           // area grind tick
            else if (mode == 4) e.Fling(Vector3.Up * power);                                     // Updraft: lift straight up (mass-scaled → big foes barely rise)
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
                case 0: e.Bleed(a, b, c > 0.5f, (int)Multiplayer.GetRemoteSenderId()); break;   // (NEW) owner = the client who cast it
                case 1: e.Slow(a, b); break;
                case 2: e.Root(a); break;
                case 3: e.Mark(a, b, (int)c); break;
                case 4: e.Poison(a, b, (int)Multiplayer.GetRemoteSenderId()); break;   // (NEW) poison now routes to the host, attributed to its caster
                case 5: e.AddFreeze(a, b, c); break;   // (NEW) frost witch freeze stacks + caster's frost profile (threshMul=b, durBonus=c) — best-of on the host
                case 7: e.ConsumeCurse(a, b, c); break;   // (NEW) Forsaken voodoo crush: a=frac of stacks, b=damage per stack, c=effective-stack cap
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
    private void EnemySnapshot(int[] ids, int[] types, int[] elite, float[] hpf, int[] status, float[] xs, float[] ys, float[] zs, int[] aff)
    {
        if (IsHost || Game.I == null) return;   // only clients render proxies
        var seen = new HashSet<int>();
        int n = ids.Length;
        for (int i = 0; i < n; i++)
        {
            int id = ids[i];
            seen.Add(id);
            if (!_renemies.TryGetValue(id, out var e) || !GodotObject.IsInstanceValid(e))
            {
                e = new Enemy();
                e.Configure(EnemyKinds.Types[types[i]], 1);   // configure BEFORE AddChild — _Ready builds the mesh from this
                if (elite[i] == 1) e.MakeElite();              // match the host's gold/larger elite look
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
        }
        // anything not in this snapshot died/despawned on the host
        var gone = new List<int>();
        foreach (var kv in _renemies) if (!seen.Contains(kv.Key)) gone.Add(kv.Key);
        foreach (var id in gone)
        {
            if (_renemies.TryGetValue(id, out var e) && GodotObject.IsInstanceValid(e))
            { Game.I.Enemies.Remove(e); e.QueueFree(); }
            _renemies.Remove(id);
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
    private void GuardianState(float x, float y, float z, float yaw, int slam)
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
        g.SetGhost(new Vector3(x, y, z), yaw, slam == 1);
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
    private void NetVitals(float hp, float mana, float shield, float blessed, int blood, int armor, int witch, float bark, float eclipse, float storm)
    {
        long id = Multiplayer.GetRemoteSenderId();
        if (_remotes.TryGetValue(id, out var av) && GodotObject.IsInstanceValid(av))
            av.SetVitals(hp, mana, shield, blessed, blood, armor, witch, bark, eclipse, storm);
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
