using Godot;
using System.Collections.Generic;
using System.Linq;

// PerkTree.cs — REWRITE to a directed-GRAPH perk system (per the spec).
// • Shared graph: 32 nodes (0-31) + 4 main keystones (32-35 = K1-K4). Same shape for every witch; each fills node effects.
// • In a run you buy nodes with ATTRIBUTE POINTS (14 cap, +1 every 4 levels). Buying a node applies its effect live.
// • AVAILABILITY is recomputed from the WHOLE owned set: a node is buyable if it's a root (0-3, always) OR any owned
//   node has a directed edge to it. (Not just the last-bought node.)
// • HIDDEN ROUTES (3 per witch): a RequiredNodeSet; when all its nodes are owned, a free bonus keystone unlocks
//   (0 points), stronger the more nodes it needs. First discovery is catalogued permanently (shown on the coven page +
//   level-up view thereafter). Dev: the `routes` console command toggles the whole catalogue on/off.
//
// NODE-DESIGN RULES (the trees were audited once already for being flat "+4% damage" sludge — keep them honest):
//  1. A node's EFFECT must be what its NAME promises. "Blink Step" lengthens the blink; it does not grant walk speed.
//  2. Each witch's four columns are four distinct BUILDS, not four colours of the same stat. Column A = her signature
//     offence, B = her second school, C = her tempo/mobility/utility, D = her guard. Read the per-witch header.
//  3. Prefer the knob the witch actually wins with (ent damage, shatter power, gust force, mark duration, dash
//     distance) over raw Atk/HP. Generic stats are the seasoning, not the meal.
//  4. Nothing here may grant a legendary-card gate (GravityWell, Bloodbath, MinionChain, ...) — those stay card-only,
//     or a 550-gold keystone would quietly delete a whole legendary from the pool.
//  5. S.Pierce is a NO-OP for Frost/Forsaken/Ember/Arcane (beam/cone/homing kits) — never put it in their trees.
public class PerkNode { public int Id; public string Name, Desc; public float Col, Row; public bool Keystone; public System.Action<Player> Apply; }
public class HiddenRoute { public string Name, Desc; public int[] Req; public System.Action<Player> Apply; }

public static class Perks
{
    public const int WitchCount = 9;
    public const int NodeCount = 36;          // 32 normal + 4 keystones
    public const int AttuneCap = 14;
    public static readonly int[] Roots = { 0, 1, 2, 3 };

    // directed adjacency (0-indexed from the spec)
    private static readonly int[][] EDGES = {
        new[]{4}, new[]{5}, new[]{6}, new[]{7},                       // 0-3 roots
        new[]{5,8,9}, new[]{4,6,10,11}, new[]{5,7,12,13}, new[]{6,14,15},  // 4-7 hub
        new[]{16}, new[]{16}, new[]{17}, new[]{17,12}, new[]{11,18}, new[]{18}, new[]{19}, new[]{19}, // 8-15 mids
        new[]{20}, new[]{21}, new[]{22}, new[]{23},                   // 16-19
        new[]{21,24,25}, new[]{20,26,27}, new[]{23,28,29}, new[]{22,30,31}, // 20-23
        new[]{25,32}, new[]{24,32}, new[]{27,33}, new[]{26,33}, new[]{29,34}, new[]{28,34}, new[]{31,35}, new[]{30,35}, // 24-31 → keystones
        new int[0], new int[0], new int[0], new int[0],              // 32-35 K1-K4
    };
    // grid positions (col 0-11, row 0-6)
    private static readonly (float c, float r)[] POS = {
        (1,0),(4,0),(7,0),(10,0),                                    // roots
        (1,1),(4,1),(7,1),(10,1),                                    // hub
        (0,2),(2,2),(3,2),(5,2),(6,2),(8,2),(9,2),(11,2),            // mids
        (1,3),(4,3),(7,3),(10,3),                                    // 16-19
        (1,4),(4,4),(7,4),(10,4),                                    // 20-23
        (0,5),(2,5),(3,5),(5,5),(6,5),(8,5),(9,5),(11,5),            // 24-31
        (1,6),(4,6),(7,6),(10,6),                                    // K1-K4
    };
    public static int[] EdgesOf(int id) => EDGES[id];
    public static (float c, float r) PosOf(int id) => POS[id];
    public static bool IsKeystone(int id) => id >= 32;

    // ---- gold-unlock layer (permanent, per witch): you buy nodes ONCE with gold; in a run you activate owned nodes
    //      with attribute points. Unlocking follows the graph too (root or reachable from a gold-owned node). ----
    private static readonly HashSet<int>[] _owned = new HashSet<int>[WitchCount];
    public static int NodeCost(int id) => id >= 32 ? 550 : (80 + (int)POS[id].r * 45);   // deeper = pricier; keystones 550
    public static bool Owned(int w, int id) => _owned[w] != null && _owned[w].Contains(id);
    public static bool UnlockReachable(int w, int id)
    {
        if (Owned(w, id)) return false;
        if (System.Array.IndexOf(Roots, id) >= 0) return true;
        foreach (int o in _owned[w]) if (System.Array.IndexOf(EDGES[o], id) >= 0) return true;
        return false;
    }
    public static bool CanUnlock(int w, int id, int gold) => UnlockReachable(w, id) && gold >= NodeCost(id);
    public static bool Unlock(int w, int id)
    {
        var g = Game.I; if (g == null || !CanUnlock(w, id, g.Gold)) return false;
        g.Gold -= NodeCost(id); _owned[w].Add(id); g.SavePerks(); return true;
    }

    // a node is buyable if it's a root, or ANY owned node points to it
    public static List<int> Available(int witch, ICollection<int> owned)
    {
        var outl = new List<int>();
        for (int n = 0; n < NodeCount; n++)
        {
            if (owned.Contains(n)) continue;
            bool ok = System.Array.IndexOf(Roots, n) >= 0;
            if (!ok) foreach (int o in owned) if (System.Array.IndexOf(EDGES[o], n) >= 0) { ok = true; break; }
            if (ok) outl.Add(n);
        }
        return outl;
    }

    // ---- per-witch data (built once) ----
    private static PerkNode[][] _nodes;
    private static HiddenRoute[][] _routes;
    public static PerkNode[] Nodes(int w) { Build(); return _nodes[Mathf.Clamp(w, 0, WitchCount - 1)]; }
    public static PerkNode Node(int w, int id) => Nodes(w)[id];
    public static HiddenRoute[] Routes(int w) { Build(); return _routes[Mathf.Clamp(w, 0, WitchCount - 1)]; }

    // ---- discovered hidden routes (persistent, per witch) ----
    private static readonly int[] _discovered = new int[WitchCount];   // bitmask of discovered route indices
    static Perks() { for (int w = 0; w < WitchCount; w++) _owned[w] = new HashSet<int>(); }
    public static bool RouteDiscovered(int w, int ri) => (_discovered[w] & (1 << ri)) != 0;
    public static void MarkDiscovered(int w, int ri) { _discovered[w] |= (1 << ri); Game.I?.SavePerks(); }
    // ---- dev: catalogue every hidden route at once (or wipe the catalogue). This is the DISCOVERY log — the routes
    //      themselves still fire in a run when you actually own their node-set; this just reveals name + path + the
    //      required nodes on the Coven page so they can be read without hunting for them. ----
    public static int DiscoveredCount { get { int n = 0; for (int w = 0; w < WitchCount; w++) for (int r = 0; r < 3; r++) if (RouteDiscovered(w, r)) n++; return n; } }
    public static int RouteTotal => WitchCount * 3;
    public static void SetAllDiscovered(bool on)
    {
        for (int w = 0; w < WitchCount; w++) _discovered[w] = on ? (1 << Routes(w).Length) - 1 : 0;
        Game.I?.SavePerks();
    }
    // snapshot/restore so a dev test can flip the catalogue without eating the player's real discovery log
    public static int[] DiscoveredSnapshot() { var a = new int[WitchCount]; System.Array.Copy(_discovered, a, WitchCount); return a; }
    public static void DiscoveredRestore(int[] a)
    {
        if (a == null) return;
        for (int w = 0; w < WitchCount && w < a.Length; w++) _discovered[w] = a[w];
        Game.I?.SavePerks();
    }
    public static void Save(ConfigFile cfg)
    {
        for (int w = 0; w < WitchCount; w++)
        {
            cfg.SetValue("perks3", $"disc{w}", _discovered[w]);
            cfg.SetValue("perks3", $"own{w}", string.Join(",", _owned[w].OrderBy(x => x)));
        }
    }
    public static void Load(ConfigFile cfg)
    {
        // one-time refund of any old gold-perk unlocks so nobody loses their investment on the redesign
        if ((int)cfg.GetValue("perks3", "ver", 0).AsInt32() < 1)
        {
            int refund = 0; int[] tier = { 150, 400, 850 };
            for (int w = 0; w < WitchCount; w++)
                foreach (var part in cfg.GetValue("perks", $"owned{w}", "").AsString().Split(','))
                    if (int.TryParse(part, out int v) && v >= 0 && v < 9) refund += tier[v % 3];
            for (int w = 0; w < WitchCount; w++)
                foreach (var part in cfg.GetValue("perks2", $"owned{w}", "").AsString().Split(','))
                    if (int.TryParse(part, out int v)) refund += (v % 12 == 5 || v % 12 == 11) ? 1000 : (100 + (v % 12) * 90);
            if (refund > 0 && Game.I != null) { Game.I.Gold += Mathf.Min(refund, 40000); GD.Print($"[perks] redesign refund: +{Mathf.Min(refund,40000)} gold"); }
            cfg.SetValue("perks3", "ver", 1);
        }
        for (int w = 0; w < WitchCount; w++)
        {
            _discovered[w] = (int)cfg.GetValue("perks3", $"disc{w}", 0).AsInt32();
            _owned[w].Clear();
            foreach (var part in cfg.GetValue("perks3", $"own{w}", "").AsString().Split(','))
                if (int.TryParse(part, out int v) && v >= 0 && v < NodeCount) _owned[w].Add(v);
        }
    }

    private static void Build()
    {
        if (_nodes != null) return;
        _nodes = new PerkNode[WitchCount][];
        _routes = new HiddenRoute[WitchCount][];
        for (int w = 0; w < WitchCount; w++)
        {
            var defs = w switch { 0 => LunarDefs(), 1 => DivineDefs(), 2 => CrimsonDefs(), 3 => VerdantDefs(), 4 => GaleDefs(), 5 => FrostDefs(), 6 => ForsakenDefs(), 7 => EmberDefs(), 8 => ArcaneDefs(), _ => GenericDefs(w) };
            // a SHORT table throws below; a LONG one would silently drop its tail, so say so out loud
            if (defs.Length != NodeCount) GD.PushError($"[perks] witch {w} declares {defs.Length} node defs, expected {NodeCount}");
            var arr = new PerkNode[NodeCount];
            for (int i = 0; i < NodeCount; i++)
                arr[i] = new PerkNode { Id = i, Name = defs[i].n, Desc = defs[i].d, Col = POS[i].c, Row = POS[i].r, Keystone = i >= 32, Apply = defs[i].a };
            _nodes[w] = arr;
            _routes[w] = w switch { 0 => LunarRoutes(), 1 => DivineRoutes(), 2 => CrimsonRoutes(), 3 => VerdantRoutes(), 4 => GaleRoutes(), 5 => FrostRoutes(), 6 => ForsakenRoutes(), 7 => EmberRoutes(), 8 => ArcaneRoutes(), _ => GenericRoutes(w) };
        }
    }

    // ---- shorthands ----------------------------------------------------------------------------------
    // These are the vocabulary the trees are written in. The rule for a node: its EFFECT must be the thing its
    // NAME promises. A perk called "Blink Step" moves you further per dash; it does not quietly grant walk speed.
    private static void A(Player p, float d) => p.S.Atk += d;
    private static void Crit(Player p, float c) => p.S.CritChance = Mathf.Min(1f, p.S.CritChance + c);
    private static void CritD(Player p, float c) => p.S.CritDamage += c;
    private static void HP(Player p, float h) => p.S.MaxHp += h;
    private static void Res(Player p, float r) => p.S.DmgResist = Mathf.Min(0.8f, p.S.DmgResist + r);
    private static void Area(Player p, float a) => p.S.SpellArea += a;
    private static void Ult(Player p, float u) => p.UltChargeMul = Mathf.Min(2.5f, p.UltChargeMul + u);
    // mobility
    private static void Dash(Player p, float d) => p.S.DashDist += d;             // ALSO lengthens Arcane's blink (max(9, DashDist*2.2))
    private static void DashCut(Player p, float f) => p.S.DashCd = Mathf.Max(0.9f, p.S.DashCd * f);
    private static void Jmp(Player p, float j) => p.S.JumpMul += j;
    // casting / projectiles
    private static void Cast(Player p, float f) => p.S.FireCd = Mathf.Max(0.08f, p.S.FireCd * f);
    private static void Chg(Player p, float c) => p.S.ChargeSpeed = Mathf.Min(2.5f, p.S.ChargeSpeed + c);   // fill RATE (the engine caps reads at 2.5)
    private static void Pow(Player p, float c) => p.S.MaxCharge = Mathf.Min(6f, p.S.MaxCharge + c);          // full-charge damage ceiling
    private static void Rng(Player p, float r) => p.S.SpellRange += r;
    private static void Prj(Player p, float s) => p.S.ProjSpeed = Mathf.Min(2.4f, p.S.ProjSpeed + s);
    private static void Prc(Player p, int n) => p.S.Pierce += n;                  // BOLT witches only (Lunar/Divine/Crimson/Verdant/Gale) — a no-op for beam/cone/missile kits
    // resources / economy
    private static void Mana(Player p, float m) => p.S.ManaMax += m;
    private static void Regen(Player p, float g) => p.S.ManaGain += g;
    private static void Cmb(Player p, float pow) => p.S.ComboPow += pow;
    private static void CmbCap(Player p, int n) => p.S.ComboCap += n;
    private static void CmbWin(Player p, float s) => p.S.ComboWindow += s;
    private static void Life(Player p, float l) => p.S.Lifesteal += l;
    // shields
    private static void Shd(Player p, float pct) => p.S.ShieldPct += pct;
    private static void ShdReg(Player p, float r) => p.S.ShieldRegen += r;
    private static void ShdFast(Player p, float f) => p.S.ShieldDelay = Mathf.Max(1.2f, p.S.ShieldDelay * f);
    // per-witch scalars that need a floor/ceiling
    private static void Thaw(Player p, float f) => p.FreezeThreshMul = Mathf.Max(0.35f, p.FreezeThreshMul * f);
    private static void Gust(Player p, float g) => p.GustPower = Mathf.Min(2.5f, p.GustPower + g);
    // (FIX) Soul Tether (the Forsaken legendary card) sets MaxLinks = 99 to mean "no limit". Any perk node that added
    // links afterwards used to re-clamp to 12, silently DOWNGRADING her from 99 — order-dependent and invisible.
    // The bool now guards it, which also gives SoulTether its only real read.
    private static void Links(Player p, int n) { if (p.SoulTether) return; p.MaxLinks = Mathf.Min(12, p.MaxLinks + n); }
    private static void Share(Player p, float f) => p.CurseShareFrac = Mathf.Min(1f, p.CurseShareFrac + f);
    private static void Beam(Player p, float f) => p.CurseBeamLifesteal = Mathf.Min(1f, p.CurseBeamLifesteal + f);
    private static void Ents(Player p, int n) => p.GroveBonusEnts = Mathf.Min(8, p.GroveBonusEnts + n);
    private static void Grow(Player p, int n) => p.GroveEvery = Mathf.Max(6, p.GroveEvery - n);
    private static void FinCost(Player p, float f) => p.FinHpCost = Mathf.Max(0.06f, p.FinHpCost * f);

    private static void Sp(Player p, float s) => p.S.Speed = Mathf.Min(18f, p.S.Speed + s);
    private static void Crescent(Player p, float s) => p.CrescentSizeMul = Mathf.Min(2.9f, p.CrescentSizeMul + s);

    // ---- hidden-route node-sets: ONE PER ROUTE PER WITCH, never shared ------------------------------------------
    // Every witch used to point at the same three sets (R5/R9/R13), so "discovering" a route on the Frost tree taught
    // you every other witch's routes too and the paths had nothing to do with what the route granted. Each set below
    // now traces the columns its OWN payoff comes from — Arcane's Phase Walker walks the blink column and ends on
    // Blinkmaster★; Gale's Skydancer walks the flight column; Verdant's Grovekeeper walks the grove column.
    //
    // A set must be REACHABILITY-CLOSED: every non-root node needs at least one of its EDGES[] predecessors in the
    // same set, or the route can never be completed in a run. Sizes stay under AttuneCap (14 points). The perk_audit
    // scenario enforces closure, the cap, and cross-witch uniqueness — don't hand-edit these without re-running it.
    // Columns: A = 0,4,8,9,16,20,24,25,32 · B = 1,5,10,11,17,21,26,27,33 · C = 2,6,12,13,18,22,28,29,34 · D = 3,7,14,15,19,23,30,31,35
    //                                                                    A-spine            into C
    private static readonly int[] LunA  = { 0, 4, 8, 9, 16 };
    private static readonly int[] LunCD = { 2, 3, 6, 7, 13, 14, 18, 19, 22 };
    private static readonly int[] LunAC = { 0, 4, 8, 9, 16, 20, 24, 25, 32, 2, 6, 13, 18 };
    private static readonly int[] DivA  = { 0, 4, 9, 16, 20 };
    private static readonly int[] DivC  = { 2, 6, 12, 13, 18, 22, 28, 29, 34 };            // the whole aegis column
    private static readonly int[] DivAD = { 0, 4, 8, 9, 16, 20, 24, 25, 32, 3, 7, 15, 19 };
    private static readonly int[] CrimA = { 0, 4, 8, 16, 20 };
    private static readonly int[] CrimCD= { 2, 3, 6, 7, 12, 14, 18, 19, 23 };
    private static readonly int[] CrimBA= { 0, 1, 4, 5, 8, 10, 11, 16, 17, 21, 26, 27, 33 };
    private static readonly int[] VerA  = { 0, 4, 8, 9, 16, 20 };                          // the whole grove line
    private static readonly int[] VerCD = { 2, 3, 6, 7, 13, 15, 18, 19, 22, 23 };
    private static readonly int[] VerAB = { 0, 4, 5, 8, 9, 10, 11, 16, 17, 20, 24, 25, 32 };
    private static readonly int[] GalA  = { 0, 4, 8, 16, 20, 24 };
    private static readonly int[] GalC  = { 2, 6, 12, 13, 18, 22, 28, 29 };                // the whole flight column
    private static readonly int[] GalBC = { 1, 2, 5, 6, 10, 11, 12, 13, 17, 21, 26, 27, 33 };
    private static readonly int[] FroA  = { 0, 4, 8, 9, 16, 20, 24 };
    private static readonly int[] FroCD = { 2, 3, 6, 7, 12, 13, 14, 15, 18, 19 };
    private static readonly int[] FroBC = { 1, 5, 6, 10, 11, 12, 13, 17, 18, 21, 26, 27, 33 };
    private static readonly int[] ForA  = { 0, 4, 9, 16, 20, 24 };
    private static readonly int[] ForCD = { 2, 3, 6, 7, 12, 14, 18, 19, 22, 28 };
    private static readonly int[] ForBA = { 1, 4, 5, 8, 9, 10, 11, 16, 17, 21, 26, 27, 33 };
    private static readonly int[] EmbA  = { 0, 4, 8, 9, 16, 20, 25 };
    private static readonly int[] EmbC  = { 2, 6, 12, 13, 18, 22, 29, 34 };
    private static readonly int[] EmbAB = { 0, 4, 5, 8, 9, 11, 16, 17, 20, 21, 24, 25, 32 };
    private static readonly int[] ArcA  = { 0, 4, 8, 16, 20, 25 };
    private static readonly int[] ArcC  = { 2, 6, 12, 13, 18, 22, 28, 34 };                // the whole blink column
    private static readonly int[] ArcAB = { 0, 4, 5, 8, 9, 10, 16, 17, 20, 21, 24, 25, 32 };

    // ===== LUNAR (0) — A crescents · B moonlight · C eclipse & tempo · D nightward =====
    private static (string n, string d, System.Action<Player> a)[] LunarDefs() => new (string, string, System.Action<Player>)[]{
        ("Keen Edge","+3% crit chance", p=>Crit(p,0.03f)), ("Moonbrand","+4% Lunar damage", p=>p.LunarBonus+=0.04f),
        ("Duskbound","+8% ult charge", p=>Ult(p,0.08f)), ("Nightward","+4% resistance", p=>Res(p,0.04f)),
        ("Silver Point","+12% crit damage", p=>CritD(p,0.12f)), ("Pale Light","+5% spell area", p=>Area(p,0.05f)),
        ("Gloaming","+0.3s combo window", p=>CmbWin(p,0.3f)), ("Shadowmantle","+25 max health", p=>HP(p,25f)),
        ("Waxing Blade","+1 crescent pierce", p=>p.CrescentPierceBonus++), ("Sharp Sickle","+18% crescent size", p=>Crescent(p,0.18f)),
        ("Glimmer","+8% spell range", p=>Rng(p,0.08f)), ("Moonveil","+5% Lunar damage", p=>p.LunarBonus+=0.05f),
        ("Eventide","+10% ult charge", p=>Ult(p,0.1f)), ("Starfall","+0.2 charge speed", p=>Chg(p,0.2f)),
        ("Gloomskin","+4% resistance", p=>Res(p,0.04f)), ("Moonstone","+25% shield capacity", p=>Shd(p,0.05f)),
        ("Twin Crescent","+1 crescent pierce, +4% crit", p=>{p.CrescentPierceBonus++; Crit(p,0.04f);}), ("Deep Brand","+6% Lunar dmg, +5% area", p=>{p.LunarBonus+=0.06f; Area(p,0.05f);}),
        ("Twilight","+0.03 combo power", p=>Cmb(p,0.03f)), ("Duskguard","+30 HP, shield recovers sooner", p=>{HP(p,30f); ShdFast(p,0.88f);}),
        ("Bright Point","+16% crit damage", p=>CritD(p,0.16f)), ("Nightbloom","+6% Lunar dmg, +8% ult", p=>{p.LunarBonus+=0.06f; Ult(p,0.08f);}),
        ("Moonlit Ward","+2 combo cap", p=>CmbCap(p,2)), ("Heartmoon","+35 max health", p=>HP(p,35f)),
        ("Reaper's Arc","+1 crescent pierce", p=>p.CrescentPierceBonus++), ("Moonfire","+22% crescent size", p=>Crescent(p,0.22f)),
        ("Nightsong","+7% Lunar damage", p=>p.LunarBonus+=0.07f), ("Starlight","+8% range, +6% area", p=>{Rng(p,0.08f); Area(p,0.06f);}),
        ("Umbral Tide","+12% ult charge", p=>Ult(p,0.12f)), ("Silver Step","+1.2 dash distance", p=>Dash(p,1.2f)),
        ("Moonshield","+0.5 shield regen", p=>ShdReg(p,0.5f)), ("Nightguard","+5% resistance", p=>Res(p,0.05f)),
        ("Full Moon ★","+2 crescent pierce, +30% size, +8% crit", p=>{p.CrescentPierceBonus+=2; Crescent(p,0.3f); Crit(p,0.08f);}),   // K1
        ("Moonwell ★","+14% Lunar damage, +10% area", p=>{p.LunarBonus+=0.14f; Area(p,0.1f);}),                                       // K2
        ("Eclipse Sovereign ★","+30% ult charge, +0.05 combo power, +3 cap", p=>{Ult(p,0.3f); Cmb(p,0.05f); CmbCap(p,3);}),           // K3
        ("Nightbulwark ★","+12% resist, +60 HP, +50% shield", p=>{Res(p,0.12f); HP(p,60f); Shd(p,0.1f);}),                            // K4
    };
    private static HiddenRoute[] LunarRoutes() => new[]{
        new HiddenRoute { Name="Silver Reaper", Desc="+10% crit, +2 crescent pierce, +25% crit dmg",
            Req=LunA, Apply=p=>{Crit(p,0.1f); p.CrescentPierceBonus+=2; Crescent(p,0.3f); CritD(p,0.25f);} },
        new HiddenRoute { Name="Eclipse Warden", Desc="+12% resist, +50 HP, +20% ult, +combo power",
            Req=LunCD, Apply=p=>{Res(p,0.12f); HP(p,50f); Ult(p,0.2f); Cmb(p,0.04f);} },
        new HiddenRoute { Name="Lunar Colossus", Desc="+15% Lunar dmg, +30% ult, +3 pierce, +50 HP",
            Req=LunAC, Apply=p=>{p.LunarBonus+=0.15f; Ult(p,0.3f); p.CrescentPierceBonus+=3; HP(p,50f); Crit(p,0.08f);} },
    };

    // ===== DIVINE (1) — A judgement & motes · B consecration · C aegis (shields) · D devotion =====
    private static (string n, string d, System.Action<Player> a)[] DivineDefs() => new (string, string, System.Action<Player>)[]{
        ("Sunfire","+4% damage", p=>A(p,0.04f)), ("Radiance","+5% spell area", p=>Area(p,0.05f)), ("Warding","+25% shield capacity", p=>Shd(p,0.05f)), ("Devout","+4% resistance", p=>Res(p,0.04f)),
        ("Piercing Light","+1 pierce", p=>Prc(p,1)), ("Halo","+8% spell range", p=>Rng(p,0.08f)), ("Consecrant","+0.4 shield regen", p=>ShdReg(p,0.4f)), ("Sanctified","+25 max health", p=>HP(p,25f)),
        ("Zeal","+4% crit chance", p=>Crit(p,0.04f)), ("Twin Light","motes fork to +1 foe", p=>p.MoteFork++),
        ("Benediction","+1s blessing", p=>p.BlessBonus+=1f), ("Smite","+5% damage", p=>A(p,0.05f)),
        ("Aegis","shield recovers sooner", p=>ShdFast(p,0.86f)), ("Bastion","+25% shield capacity", p=>Shd(p,0.05f)),
        ("Grace","+4% resistance", p=>Res(p,0.04f)), ("Ordained","+30 max health", p=>HP(p,30f)),
        ("Zealot","+6% damage, +4% crit", p=>{A(p,0.06f); Crit(p,0.04f);}), ("Consecrate","+7% area, +1s blessing", p=>{Area(p,0.07f); p.BlessBonus+=1f;}),
        ("Sanctuary","+0.5 shield regen", p=>ShdReg(p,0.5f)), ("Bulwark","+30 HP, +4% resist", p=>{HP(p,30f); Res(p,0.04f);}),
        ("Judgement","+16% crit damage", p=>CritD(p,0.16f)), ("Reckoner","motes fork to +1 foe", p=>p.MoteFork++),
        ("Heartlight","+30% shield capacity", p=>Shd(p,0.06f)), ("Divine Might","+35 max health", p=>HP(p,35f)),
        ("Retribution","+6% damage", p=>A(p,0.06f)), ("Sunblade","+1 pierce, +12% crit damage", p=>{Prc(p,1); CritD(p,0.12f);}),
        ("Empyrean","+2s blessing", p=>p.BlessBonus+=2f), ("Dawnlight","+8% area, +8% range", p=>{Area(p,0.08f); Rng(p,0.08f);}),
        ("Faithguard","shield recovers sooner", p=>ShdFast(p,0.86f)), ("Sunburst","+0.6 shield regen", p=>ShdReg(p,0.6f)),
        ("Devotion","+5% resistance", p=>Res(p,0.05f)), ("Sanctum","+40 max health", p=>HP(p,40f)),
        ("Dawnbringer ★","+12% damage, +8% crit, +1 pierce", p=>{A(p,0.12f); Crit(p,0.08f); Prc(p,1);}),                            // K1
        ("Seraph ★","motes fork to +2 foes, +3s blessing", p=>{p.MoteFork+=2; p.BlessBonus+=3f;}),                                   // K2
        ("Aegis Eternal ★","+50% shield, +1 regen, recovers fast", p=>{Shd(p,0.1f); ShdReg(p,1f); ShdFast(p,0.75f);}),               // K3
        ("Bulwark of Dawn ★","+1 Intervention, +12% resist, +60 HP", p=>{p.Interventions++; Res(p,0.12f); HP(p,60f);}),              // K4
    };
    private static HiddenRoute[] DivineRoutes() => new[]{
        new HiddenRoute { Name="Sun Cleric", Desc="+2 mote forks, +8% crit, +20% crit dmg", Req=DivA, Apply=p=>{p.MoteFork+=2; Crit(p,0.08f); CritD(p,0.2f); A(p,0.06f);} },
        new HiddenRoute { Name="Bulwark Saint", Desc="+60% shield, +1 regen, +12% resist, +50 HP", Req=DivC, Apply=p=>{Shd(p,0.12f); ShdReg(p,1f); Res(p,0.12f); HP(p,50f);} },
        new HiddenRoute { Name="Archon", Desc="+2 Interventions, +3 mote forks, +40 HP", Req=DivAD, Apply=p=>{p.Interventions+=2; p.MoteFork+=3; HP(p,40f); Crit(p,0.08f);} },
    };

    // ===== CRIMSON (2) — A butchery (crit) · B sanguine (aura/leech) · C frenzy (tempo) · D ironblood (armor) =====
    private static (string n, string d, System.Action<Player> a)[] CrimsonDefs() => new (string, string, System.Action<Player>)[]{
        ("Reckless","+4% crit chance", p=>Crit(p,0.04f)), ("Leech","+5% lifesteal", p=>Life(p,0.05f)), ("Blooded","+4% damage", p=>A(p,0.04f)), ("Thickskin","+4% resistance", p=>Res(p,0.04f)),
        ("Savagery","+14% crit damage", p=>CritD(p,0.14f)), ("Wide Aura","+1.5 blood-aura radius", p=>p.AuraBonusR+=1.5f), ("Bloodhaste","+6% cast speed", p=>Cast(p,0.94f)), ("Vital","+30 max health", p=>HP(p,30f)),
        ("Butcher's Eye","+4% crit chance", p=>Crit(p,0.04f)), ("Gash","+1 pierce", p=>Prc(p,1)),
        ("Feast","+5% lifesteal", p=>Life(p,0.05f)), ("Communion","+20% aura healing", p=>p.AuraHealMul+=0.2f),
        ("Bloodpact","blood finishers cost less HP", p=>FinCost(p,0.88f)), ("Ravage","+0.03 combo power", p=>Cmb(p,0.03f)),
        ("Toughen","+4% resistance", p=>Res(p,0.04f)), ("Ironblood","+1 armor charge", p=>p.MaxArmor++),
        ("Berserk","+6% damage, +4% crit", p=>{A(p,0.06f); Crit(p,0.04f);}), ("Gorge","+6% lifesteal, +1.5 aura", p=>{Life(p,0.06f); p.AuraBonusR+=1.5f;}),
        ("Crimson Tide","+7% spell area", p=>Area(p,0.07f)), ("Bloodguard","+35 HP, +4% resist", p=>{HP(p,35f); Res(p,0.04f);}),
        ("Butchery","+18% crit damage", p=>CritD(p,0.18f)), ("Sanguine","+30% aura healing", p=>p.AuraHealMul+=0.3f),
        ("Frenzy","+2 combo cap, +0.3s window", p=>{CmbCap(p,2); CmbWin(p,0.3f);}), ("Lifeblood","+40 max health", p=>HP(p,40f)),
        ("Carnage","+7% damage", p=>A(p,0.07f)), ("Slaughter","+14% crit damage", p=>CritD(p,0.14f)),
        ("Siphon","+6% lifesteal", p=>Life(p,0.06f)), ("Bloodwell","blood finishers cost less HP", p=>FinCost(p,0.88f)),
        ("Fleetblood","+1.2 dash distance", p=>Dash(p,1.2f)), ("Gore Rush","+6% cast speed", p=>Cast(p,0.94f)),
        ("Crimson Skin","+5% resistance", p=>Res(p,0.05f)), ("Bloodplate","+1 armor charge", p=>p.MaxArmor++),
        ("Berserker ★","+12% crit, +40% crit damage", p=>{Crit(p,0.12f); CritD(p,0.4f);}),                                            // K1
        ("Vampiric ★","+12% lifesteal, +50% aura heal, +3 aura", p=>{Life(p,0.12f); p.AuraHealMul+=0.5f; p.AuraBonusR+=3f;}),          // K2
        ("Bloodlord ★","+15% dmg, +12% cast speed, +0.05 combo", p=>{A(p,0.15f); Cast(p,0.88f); Cmb(p,0.05f);}),                       // K3
        ("Ironheart ★","+12% resist, +60 HP, +1 armor charge", p=>{Res(p,0.12f); HP(p,60f); p.MaxArmor++;}),                           // K4
    };
    private static HiddenRoute[] CrimsonRoutes() => new[]{
        new HiddenRoute { Name="Bloodletter", Desc="+10% crit, +30% crit dmg, +1 pierce, +6% leech", Req=CrimA, Apply=p=>{Crit(p,0.1f); CritD(p,0.3f); Prc(p,1); Life(p,0.06f);} },
        new HiddenRoute { Name="Sanguine Lord", Desc="+2 armor, +combo power, +60 HP, +12% resist", Req=CrimCD, Apply=p=>{p.MaxArmor+=2; Cmb(p,0.05f); HP(p,60f); Res(p,0.12f);} },
        new HiddenRoute { Name="Crimson God", Desc="+15% lifesteal, +12% crit, cheap finishers", Req=CrimBA, Apply=p=>{Life(p,0.15f); Crit(p,0.12f); FinCost(p,0.55f); HP(p,40f); A(p,0.12f);} },
    };

    // ===== VERDANT (3) — A the grove (ents) · B blight (poison) · C wildgrowth (reach) · D bark (bulk) =====
    private static (string n, string d, System.Action<Player> a)[] VerdantDefs() => new (string, string, System.Action<Player>)[]{
        ("Sapling","tree-ents grow faster", p=>Grow(p,1)), ("Blighttouch","+15% poison damage", p=>p.PoisonMul+=0.15f), ("Spread","+6% spell area", p=>Area(p,0.06f)), ("Barkhide","+4% resistance", p=>Res(p,0.04f)),
        ("Deep Roots","+1 max tree-ent", p=>Ents(p,1)), ("Creeping Death","+5% damage", p=>A(p,0.05f)), ("Overgrowth","+7% spell area", p=>Area(p,0.07f)), ("Heartwood","+35 max health", p=>HP(p,35f)),
        ("Seedfall","+15% tree-ent damage", p=>p.MinionDmgMul+=0.15f), ("Quick Roots","tree-ents grow faster", p=>Grow(p,1)),
        ("Necrosis","+15% poison damage", p=>p.PoisonMul+=0.15f), ("Virulence","+6% damage", p=>A(p,0.06f)),
        ("Wildgrowth","+8% spell range", p=>Rng(p,0.08f)), ("Bloom","+7% spell area", p=>Area(p,0.07f)),
        ("Thornmail","+5% resistance", p=>Res(p,0.05f)), ("Toughbark","+0.5 shield regen", p=>ShdReg(p,0.5f)),
        ("Elder Seed","+1 tree-ent, +15% ent damage", p=>{Ents(p,1); p.MinionDmgMul+=0.15f;}), ("Plaguetouch","+20% poison, +5% damage", p=>{p.PoisonMul+=0.2f; A(p,0.05f);}),
        ("Canopy","+8% area, +8% range", p=>{Area(p,0.08f); Rng(p,0.08f);}), ("Ironroot","+40 HP, +4% resist", p=>{HP(p,40f); Res(p,0.04f);}),
        ("Grovewarden","+20% tree-ent damage", p=>p.MinionDmgMul+=0.2f), ("Rot","+7% damage", p=>A(p,0.07f)),
        ("Blight Bloom","+9% area, +5% damage", p=>{Area(p,0.09f); A(p,0.05f);}), ("Vitality","+45 max health", p=>HP(p,45f)),
        ("Worldseed","+1 max tree-ent", p=>Ents(p,1)), ("Swiftgrove","tree-ents grow faster", p=>Grow(p,1)),
        ("Wither","+25% poison damage", p=>p.PoisonMul+=0.25f), ("Decay","+7% damage", p=>A(p,0.07f)),
        ("Wildheart","+10% spell area", p=>Area(p,0.1f)), ("Longvine","+10% spell range", p=>Rng(p,0.1f)),
        ("Mossguard","+5% resistance", p=>Res(p,0.05f)), ("Deadwood","+45 max health", p=>HP(p,45f)),
        ("Elder Grove ★","+2 tree-ents, +40% ent dmg, grow fast", p=>{Ents(p,2); p.MinionDmgMul+=0.4f; Grow(p,2);}),                   // K1
        ("Plaguelord ★","+60% poison damage, +12% damage", p=>{p.PoisonMul+=0.6f; A(p,0.12f);}),                                       // K2
        ("Worldbloom ★","+14% spell area, +12% range", p=>{Area(p,0.14f); Rng(p,0.12f);}),                                             // K3
        ("Ironbark ★","+12% resistance, +80 health", p=>{Res(p,0.12f); HP(p,80f);}),                                                   // K4
    };
    private static HiddenRoute[] VerdantRoutes() => new[]{
        new HiddenRoute { Name="Grovekeeper", Desc="+2 tree-ents, +50% ent damage, grow faster", Req=VerA, Apply=p=>{Ents(p,2); p.MinionDmgMul+=0.5f; Grow(p,1);} },
        new HiddenRoute { Name="Ancient Warden", Desc="+80 HP, +12% resist, +12% area, +10% range", Req=VerCD, Apply=p=>{HP(p,80f); Res(p,0.12f); Area(p,0.12f); Rng(p,0.1f);} },
        new HiddenRoute { Name="Worldtree", Desc="+3 ents, +50% ent dmg, +60% poison, +60 HP", Req=VerAB, Apply=p=>{Ents(p,3); p.MinionDmgMul+=0.5f; p.PoisonMul+=0.6f; HP(p,60f);} },
    };

    // ===== GALE (4) — A duelist · B gusts · C flight (dash/jump) · D stormguard =====
    private static (string n, string d, System.Action<Player> a)[] GaleDefs() => new (string, string, System.Action<Player>)[]{
        ("Cutting Gust","+4% crit chance", p=>Crit(p,0.04f)), ("Buffet","+15% gust power", p=>Gust(p,0.15f)), ("Fleet","+0.5 move speed", p=>Sp(p,0.5f)), ("Windguard","+4% resistance", p=>Res(p,0.04f)),
        ("Gale Force","+5% damage", p=>A(p,0.05f)), ("Whirl","+6% spell area", p=>Area(p,0.06f)), ("Slipwind","+1 dash charge", p=>p.S.DashCharges++), ("Airborne","+30 max health", p=>HP(p,30f)),
        ("Windblade","+1 pierce", p=>Prc(p,1)), ("Jetstream","+14% crit damage", p=>CritD(p,0.14f)),
        ("Crosswind","+15% gust power", p=>Gust(p,0.15f)), ("Downdraft","+8% spell range", p=>Rng(p,0.08f)),
        ("Quickstep","dash recovers faster", p=>DashCut(p,0.88f)), ("Updraft","+10% jump height", p=>Jmp(p,0.1f)),
        ("Slipstream","+4% resistance", p=>Res(p,0.04f)), ("Skysong","+25% shield capacity", p=>Shd(p,0.05f)),
        ("Stormheart","+6% damage, +4% crit", p=>{A(p,0.06f); Crit(p,0.04f);}), ("Maelstrom","+20% gust power, +6% area", p=>{Gust(p,0.2f); Area(p,0.06f);}),
        ("Tailwind","+1.5 dash distance", p=>Dash(p,1.5f)), ("Gustguard","+30 HP, +4% resist", p=>{HP(p,30f); Res(p,0.04f);}),
        ("Tempest","+16% crit damage", p=>CritD(p,0.16f)), ("Cyclone","+8% area, +8% range", p=>{Area(p,0.08f); Rng(p,0.08f);}),
        ("Zephyr","+0.8 move speed, +10% jump", p=>{Sp(p,0.8f); Jmp(p,0.1f);}), ("Gale Skin","+35 max health", p=>HP(p,35f)),
        ("Riptide","+7% damage", p=>A(p,0.07f)), ("Windshear","+1 pierce, +12% crit damage", p=>{Prc(p,1); CritD(p,0.12f);}),
        ("Eyewall","+20% gust power", p=>Gust(p,0.2f)), ("Downburst","+7% damage, +6% area", p=>{A(p,0.07f); Area(p,0.06f);}),
        ("Second Wind","+1 dash charge", p=>p.S.DashCharges++), ("Skydancer","+1.5 dash distance", p=>Dash(p,1.5f)),
        ("Eye Calm","+5% resistance", p=>Res(p,0.05f)), ("Windwall","+0.5 shield regen", p=>ShdReg(p,0.5f)),
        ("Stormheart ★","+14% damage, +10% crit, +1 pierce", p=>{A(p,0.14f); Crit(p,0.1f); Prc(p,1);}),                                // K1
        ("Tempest Lord ★","+60% gust power, +12% spell area", p=>{Gust(p,0.6f); Area(p,0.12f);}),                                      // K2
        ("Windwalker ★","+2 dash charges, +3 distance, +20% jump", p=>{p.S.DashCharges+=2; Dash(p,3f); Jmp(p,0.2f);}),                  // K3
        ("Eye of Calm ★","+12% resist, +60 HP, +50% shield", p=>{Res(p,0.12f); HP(p,60f); Shd(p,0.1f);}),                               // K4
    };
    private static HiddenRoute[] GaleRoutes() => new[]{
        new HiddenRoute { Name="Duelist", Desc="+10% crit, +25% crit dmg, +2 pierce, +8% dmg", Req=GalA, Apply=p=>{Crit(p,0.1f); CritD(p,0.25f); Prc(p,2); A(p,0.08f);} },
        new HiddenRoute { Name="Skydancer", Desc="+2 dash charges, +2.5 distance, +25% jump", Req=GalC, Apply=p=>{p.S.DashCharges+=2; Dash(p,2.5f); Jmp(p,0.25f); HP(p,40f);} },
        new HiddenRoute { Name="Storm Sovereign", Desc="+70% gust power, +18% dmg, +12% crit", Req=GalBC, Apply=p=>{Gust(p,0.7f); A(p,0.18f); Crit(p,0.12f); Area(p,0.12f);} },
    };

    // ===== FROST (5) — A the snipe (range/charge) · B rime (freeze) · C shatter · D rimeguard =====
    private static (string n, string d, System.Action<Player> a)[] FrostDefs() => new (string, string, System.Action<Player>)[]{
        ("Longsight","+8% spell range", p=>Rng(p,0.08f)), ("Hoarfrost","+freeze buildup", p=>p.FreezeRate+=0.2f), ("Riftsplit","+12% shatter damage", p=>p.ShatterPowerMul+=0.12f), ("Frostmail","+4% resistance", p=>Res(p,0.04f)),
        ("Coldsteel","+4% crit chance", p=>Crit(p,0.04f)), ("Permafrost","+0.4s frozen", p=>p.FrostDurBonus+=0.4f), ("Shardburst","shatters seed more freeze", p=>p.ShatterFreezeStacks+=0.4f), ("Rimeguard","+30 max health", p=>HP(p,30f)),
        ("Farsight","+8% spell range", p=>Rng(p,0.08f)), ("Chillblade","+14% crit damage", p=>CritD(p,0.14f)),
        ("Flashfreeze","foes freeze sooner", p=>Thaw(p,0.95f)), ("Deep Rime","+freeze buildup", p=>p.FreezeRate+=0.2f),
        ("Icefall","+12% shatter damage", p=>p.ShatterPowerMul+=0.12f), ("Icebound","+7% spell area", p=>Area(p,0.07f)),
        ("Coldsnap","+4% resistance", p=>Res(p,0.04f)), ("Frostskin","+25% shield capacity", p=>Shd(p,0.05f)),
        ("Winter Bite","+0.25 charge speed", p=>Chg(p,0.25f)), ("Lingering Ice","+0.6s frozen, +freeze buildup", p=>{p.FrostDurBonus+=0.6f; p.FreezeRate+=0.2f;}),
        ("Fracture","+15% shatter damage", p=>p.ShatterPowerMul+=0.15f), ("Rimeplate","+30 HP, +4% resist", p=>{HP(p,30f); Res(p,0.04f);}),
        ("Frostbite","+16% crit damage", p=>CritD(p,0.16f)), ("Brittle","foes freeze much sooner", p=>Thaw(p,0.92f)),
        ("Splinter","shatters seed more freeze", p=>p.ShatterFreezeStacks+=0.5f), ("Snowdrift","+35 max health", p=>HP(p,35f)),
        ("Deepwinter","+0.3 charged power", p=>Pow(p,0.3f)), ("Sleet","+8% range, +8% projectile speed", p=>{Rng(p,0.08f); Prj(p,0.08f);}),
        ("Zero Rime","+0.6s frozen", p=>p.FrostDurBonus+=0.6f), ("Cryo","+freeze buildup", p=>p.FreezeRate+=0.25f),
        ("Glacier","+18% shatter damage", p=>p.ShatterPowerMul+=0.18f), ("Cold Step","+1.2 dash distance", p=>Dash(p,1.2f)),
        ("Iceguard","+5% resistance", p=>Res(p,0.05f)), ("Frostwall","+0.5 shield regen", p=>ShdReg(p,0.5f)),
        ("Winter Sovereign ★","+0.5 charge speed, +0.5 power, +10% crit", p=>{Chg(p,0.5f); Pow(p,0.5f); Crit(p,0.1f);}),                 // K1
        ("Zero Point ★","+freeze buildup, +1s frozen, freeze fast", p=>{p.FreezeRate+=0.5f; p.FrostDurBonus+=1f; Thaw(p,0.85f);}),      // K2
        ("Grand Shatter ★","+45% shatter damage, +1 freeze seed", p=>{p.ShatterPowerMul+=0.45f; p.ShatterFreezeStacks+=1f;}),           // K3
        ("Cold Sovereign ★","+12% resist, +55 HP, +50% shield", p=>{Res(p,0.12f); HP(p,55f); Shd(p,0.1f);}),                            // K4
    };
    private static HiddenRoute[] FrostRoutes() => new[]{
        new HiddenRoute { Name="Frozen Sniper", Desc="+20% range, +0.6 charge, +0.5 power, +25% crit", Req=FroA, Apply=p=>{Rng(p,0.2f); Chg(p,0.6f); Pow(p,0.5f); CritD(p,0.25f);} },
        new HiddenRoute { Name="Frost Fortress", Desc="+40% shatter, +1.5 freeze seed, +60 HP", Req=FroCD, Apply=p=>{p.ShatterPowerMul+=0.4f; p.ShatterFreezeStacks+=1.5f; HP(p,60f); Res(p,0.12f);} },
        new HiddenRoute { Name="Absolute Zero", Desc="freeze near-instantly, +1.5s frozen, +50% shatter", Req=FroBC, Apply=p=>{Thaw(p,0.6f); p.FrostDurBonus+=1.5f; p.ShatterPowerMul+=0.5f; p.FreezeRate+=0.6f; HP(p,40f);} },
    };

    // ===== FORSAKEN (6) — A the Doom · B the spread · C the focus · D wraith =====
    // (DOOM REWORK) her columns kept their shape; two knobs changed meaning underneath. CurseStackCap was the crush's
    // effective-stack ceiling and became meaningless when strings went away, so the six nodes that fed it now feed
    // DoomPower — how hard everything she applies banks. Every node keeps its name, column and build identity.
    private static (string n, string d, System.Action<Player> a)[] ForsakenDefs() => new (string, string, System.Action<Player>)[]{
        ("Blight","+Doom buildup", p=>p.CurseRate+=0.4f), ("Bindings","+1 blast seed", p=>Links(p,1)), ("Siphon","+beam siphon", p=>Beam(p,0.06f)), ("Insubstantial","+4% resistance", p=>Res(p,0.04f)),
        ("Virulence","+10% damage to doomed", p=>p.CurseBonusMul+=0.1f), ("Farhex","+0.7 blast reach", p=>p.DoomSpreadRadius+=0.7f), ("Exsanguinate","+5% lifesteal", p=>Life(p,0.05f)), ("Dreadbone","+30 max health", p=>HP(p,30f)),
        ("Anathema","+15% Doom power", p=>p.DoomPower+=0.15f), ("Contagion","+Doom buildup", p=>p.CurseRate+=0.4f),
        ("Sympathy","+10% damage sharing", p=>Share(p,0.1f)), ("Coven Bind","+1 blast seed", p=>Links(p,1)),
        ("Soul Drain","+beam siphon", p=>Beam(p,0.06f)), ("Ghoststep","+0.5 move speed", p=>Sp(p,0.5f)),
        ("Rotplate","+4% resistance", p=>Res(p,0.04f)), ("Soulhide","+25% shield capacity", p=>Shd(p,0.05f)),
        ("Doombrand","+12% to doomed, +15% Doom power", p=>{p.CurseBonusMul+=0.12f; p.DoomPower+=0.15f;}), ("Soulbind","+1 blast seed, +0.9 reach", p=>{Links(p,1); p.DoomSpreadRadius+=0.9f;}),
        ("Rapture","+6% lifesteal", p=>Life(p,0.06f)), ("Hexguard","+30 HP, +4% resist", p=>{HP(p,30f); Res(p,0.04f);}),
        ("Malefic","+16% crit damage", p=>CritD(p,0.16f)), ("Grim Chorus","+12% damage sharing", p=>Share(p,0.12f)),
        ("Revenant","+1.2 dash distance", p=>Dash(p,1.2f)), ("Deathbind","+35 max health", p=>HP(p,35f)),
        ("Torment","+12% damage to doomed", p=>p.CurseBonusMul+=0.12f), ("Plague","+Doom buildup, +15% Doom power", p=>{p.CurseRate+=0.4f; p.DoomPower+=0.15f;}),
        ("Wraith Choir","+1 blast seed", p=>Links(p,1)), ("Deep Hex","+1.1 blast reach", p=>p.DoomSpreadRadius+=1.1f),
        ("Soul Glutton","+beam siphon", p=>Beam(p,0.07f)), ("Gravemark","+7% damage", p=>A(p,0.07f)),
        ("Boneguard","+5% resistance", p=>Res(p,0.05f)), ("Shroud","+0.5 shield regen", p=>ShdReg(p,0.5f)),
        ("Doomherald ★","+25% damage to doomed, +35% Doom power", p=>{p.CurseBonusMul+=0.25f; p.DoomPower+=0.35f;}),                  // K1
        ("Coven's Grip ★","+3 blast seeds, +2 reach", p=>{Links(p,3); Share(p,0.25f); p.DoomSpreadRadius+=2f;}),             // K2
        ("Soul Glutton ★","+15% beam siphon, +8% lifesteal", p=>{Beam(p,0.15f); Life(p,0.08f);}),                                       // K3
        ("Revenant King ★","+12% resist, +60 HP, +50% shield", p=>{Res(p,0.12f); HP(p,60f); Shd(p,0.1f);}),                             // K4
    };
    private static HiddenRoute[] ForsakenRoutes() => new[]{
        new HiddenRoute { Name="Hexer", Desc="+20% to doomed, +35% Doom power, +buildup", Req=ForA, Apply=p=>{p.CurseBonusMul+=0.2f; p.DoomPower+=0.35f; p.CurseRate+=0.5f;} },
        new HiddenRoute { Name="Soul Eater", Desc="+15% beam siphon, +10% lifesteal, +60 HP", Req=ForCD, Apply=p=>{Beam(p,0.15f); Life(p,0.1f); HP(p,60f); Res(p,0.12f);} },
        new HiddenRoute { Name="Doom Herald", Desc="+4 blast seeds, +25% to doomed, +1.5 reach", Req=ForBA, Apply=p=>{Links(p,4); p.CurseBonusMul+=0.25f; p.DoomSpreadRadius+=1.5f; Beam(p,0.15f); HP(p,40f);} },
    };

    // ===== EMBER (7) — A burn · B Living Bomb · C reach & meteor · D cinder =====
    private static (string n, string d, System.Action<Player> a)[] EmberDefs() => new (string, string, System.Action<Player>)[]{
        ("Emberfeed","+12% burn damage", p=>p.EmberBurnMul+=0.12f), ("Contagion","+20% Living Bomb", p=>p.LivingBombMul+=0.2f), ("Scatterspark","+10% flame reach", p=>p.FlameReachMul+=0.1f), ("Heatshimmer","+4% resistance", p=>Res(p,0.04f)),
        ("Kindling","+12% burn damage", p=>p.EmberBurnMul+=0.12f), ("Backdraft","+6% spell area", p=>Area(p,0.06f)), ("Longflame","+10% flame reach", p=>p.FlameReachMul+=0.1f), ("Emberward","+30 max health", p=>HP(p,30f)),
        ("Firestarter","+4% crit chance", p=>Crit(p,0.04f)), ("Slowburn","+14% burn damage", p=>p.EmberBurnMul+=0.14f),
        ("Chain Bomb","+20% Living Bomb", p=>p.LivingBombMul+=0.2f), ("Wildheat","+7% spell area", p=>Area(p,0.07f)),
        ("Airburst","+0.25 charge speed", p=>Chg(p,0.25f)), ("Falling Sky","+8% spell range", p=>Rng(p,0.08f)),
        ("Cinderskin","+4% resistance", p=>Res(p,0.04f)), ("Ashguard","+25% shield capacity", p=>Shd(p,0.05f)),
        ("Conflagrate","+16% burn damage, +4% crit", p=>{p.EmberBurnMul+=0.16f; Crit(p,0.04f);}), ("Detonator","+25% Living Bomb, +6% area", p=>{p.LivingBombMul+=0.25f; Area(p,0.06f);}),
        ("Meteoric","+0.3 charged power", p=>Pow(p,0.3f)), ("Heatplate","+30 HP, +4% resist", p=>{HP(p,30f); Res(p,0.04f);}),
        ("Overheat","+16% crit damage", p=>CritD(p,0.16f)), ("Firestorm","+9% spell area", p=>Area(p,0.09f)),
        ("Pyre Reach","+15% flame reach", p=>p.FlameReachMul+=0.15f), ("Cinderheart","+35 max health", p=>HP(p,35f)),
        ("Scorch","+18% burn damage", p=>p.EmberBurnMul+=0.18f), ("Wildfire","+7% damage", p=>A(p,0.07f)),
        ("Cataclysm","+25% Living Bomb", p=>p.LivingBombMul+=0.25f), ("Blast Wave","+9% area, +8% range", p=>{Area(p,0.09f); Rng(p,0.08f);}),
        ("Phoenix Step","+1.2 dash distance", p=>Dash(p,1.2f)), ("Sunflare","+15% flame reach", p=>p.FlameReachMul+=0.15f),
        ("Heatshield","+5% resistance", p=>Res(p,0.05f)), ("Ashplate","+0.5 shield regen", p=>ShdReg(p,0.5f)),
        ("Conflagration ★","+50% burn damage, +8% crit", p=>{p.EmberBurnMul+=0.5f; Crit(p,0.08f);}),                                    // K1
        ("Chain Detonation ★","+55% Living Bomb, +10% spell area", p=>{p.LivingBombMul+=0.55f; Area(p,0.1f);}),                          // K2
        ("Falling Sky ★","+35% reach, +0.5 charged power, +12% range", p=>{p.FlameReachMul+=0.35f; Pow(p,0.5f); Rng(p,0.12f);}),         // K3
        ("Living Pyre ★","+12% resist, +60 HP, +50% shield", p=>{Res(p,0.12f); HP(p,60f); Shd(p,0.1f);}),                                // K4
    };
    private static HiddenRoute[] EmberRoutes() => new[]{
        new HiddenRoute { Name="Firestarter", Desc="+60% burn damage, +10% crit, +25% crit dmg", Req=EmbA, Apply=p=>{p.EmberBurnMul+=0.6f; Crit(p,0.1f); CritD(p,0.25f);} },
        new HiddenRoute { Name="Skyfire", Desc="+40% flame reach, +0.6 power, +12% range", Req=EmbC, Apply=p=>{p.FlameReachMul+=0.4f; Pow(p,0.6f); Rng(p,0.12f); HP(p,40f);} },
        new HiddenRoute { Name="Cataclysm", Desc="+50% burn, +50% Living Bomb, +25% reach", Req=EmbAB, Apply=p=>{p.EmberBurnMul+=0.5f; p.LivingBombMul+=0.5f; p.FlameReachMul+=0.25f; HP(p,40f);} },
    };

    // ===== ARCANE (8) — A the barrage · B sigils (marks) · C the blink · D ward =====
    // NOTE: her missiles are homing and her beam is hitscan, so S.Pierce is deliberately absent from this tree.
    private static (string n, string d, System.Action<Player> a)[] ArcaneDefs() => new (string, string, System.Action<Player>)[]{
        ("Volley","+8% projectile speed", p=>Prj(p,0.08f)), ("Attune","+6% arcane damage", p=>p.ArcanePowerMul+=0.06f), ("Recall","+0.8 dash distance", p=>Dash(p,0.8f)), ("Arcane Shell","+4% resistance", p=>Res(p,0.04f)),
        ("Precision","+4% crit chance", p=>Crit(p,0.04f)), ("Farcast","+8% spell range", p=>Rng(p,0.08f)), ("Swiftcast","+6% cast speed", p=>Cast(p,0.94f)), ("Ward","+30 max health", p=>HP(p,30f)),
        ("Multishot","+10% projectile speed", p=>Prj(p,0.1f)), ("Empower","+14% crit damage", p=>CritD(p,0.14f)),
        ("Overload","+0.6s arcane mark", p=>p.ArcaneMarkDur+=0.6f), ("Resonance","+7% arcane damage", p=>p.ArcanePowerMul+=0.07f),
        ("Blink Step","+1.2 dash distance", p=>Dash(p,1.2f)), ("Deep Reserve","+0.5 max mana", p=>Mana(p,0.5f)),
        ("Sigilplate","+4% resistance", p=>Res(p,0.04f)), ("Feedback","+4% crit-heal", p=>p.ArcaneCritHealBonus+=0.04f),
        ("Barrage","+8% arcane dmg, +8% proj speed", p=>{p.ArcanePowerMul+=0.08f; Prj(p,0.08f);}), ("Conduit","+0.8s mark, +8% range", p=>{p.ArcaneMarkDur+=0.8f; Rng(p,0.08f);}),
        ("Phase Step","dash recovers faster", p=>DashCut(p,0.86f)), ("Shellguard","+30 HP, +4% resist", p=>{HP(p,30f); Res(p,0.04f);}),
        ("Overcharge","+18% crit damage", p=>CritD(p,0.18f)), ("Amplify","+9% arcane damage", p=>p.ArcanePowerMul+=0.09f),
        ("Warp","+1 dash charge", p=>p.S.DashCharges++), ("Focus","+35 max health", p=>HP(p,35f)),
        ("Singular","+14% projectile speed", p=>Prj(p,0.14f)), ("Missile","+8% arcane damage", p=>p.ArcanePowerMul+=0.08f),
        ("Sigil Storm","+1s arcane mark", p=>p.ArcaneMarkDur+=1f), ("Attunement","+9% range, +7% area", p=>{Rng(p,0.09f); Area(p,0.07f);}),
        ("Flicker","+1.6 dash distance", p=>Dash(p,1.6f)), ("Mana Font","+0.05 mana per hit", p=>Regen(p,0.05f)),
        ("Runeguard","+5% resistance", p=>Res(p,0.05f)), ("Feedback Loop","+5% crit-heal", p=>p.ArcaneCritHealBonus+=0.05f),
        ("Barrage ★","+18% arcane damage, +20% proj speed", p=>{p.ArcanePowerMul+=0.18f; Prj(p,0.2f);}),                                 // K1
        ("Conduit Sovereign ★","+2s mark, +12% range, +12% arcane dmg", p=>{p.ArcaneMarkDur+=2f; Rng(p,0.12f); p.ArcanePowerMul+=0.12f;}), // K2
        ("Blinkmaster ★","+3 dash distance, +1 charge, fast recovery", p=>{Dash(p,3f); p.S.DashCharges++; DashCut(p,0.75f);}),           // K3
        ("Warpguard ★","+12% resist, +60 HP, +8% crit-heal", p=>{Res(p,0.12f); HP(p,60f); p.ArcaneCritHealBonus+=0.08f;}),               // K4
    };
    private static HiddenRoute[] ArcaneRoutes() => new[]{
        new HiddenRoute { Name="Spellblade", Desc="+12% crit, +30% crit dmg, +25% proj speed", Req=ArcA, Apply=p=>{Crit(p,0.12f); CritD(p,0.3f); Prj(p,0.25f); p.ArcaneCritHealBonus+=0.06f;} },
        new HiddenRoute { Name="Phase Walker", Desc="+3 dash distance, +1 charge, +1 mana", Req=ArcC, Apply=p=>{Dash(p,3f); p.S.DashCharges++; DashCut(p,0.8f); Mana(p,1f); Res(p,0.12f);} },
        new HiddenRoute { Name="Singularity", Desc="+25% arcane dmg, +12% crit, +50% crit dmg", Req=ArcAB, Apply=p=>{p.ArcanePowerMul+=0.25f; Crit(p,0.12f); CritD(p,0.5f); p.ArcaneCritHealBonus+=0.08f;} },
    };

    // ===== fallback generic (unused now that all 9 are themed) =====
    private static (string n, string d, System.Action<Player> a)[] GenericDefs(int w)
    {
        var d = new (string, string, System.Action<Player>)[NodeCount];
        for (int i = 0; i < 32; i++)
        {
            switch (i % 6)
            {
                case 0: d[i] = ($"Might {i}", "+4% damage", p=>A(p,0.04f)); break;
                case 1: d[i] = ($"Edge {i}", "+4% crit chance", p=>Crit(p,0.04f)); break;
                case 2: d[i] = ($"Guard {i}", "+4% resistance", p=>Res(p,0.04f)); break;
                case 3: d[i] = ($"Vigor {i}", "+25 max health", p=>HP(p,25f)); break;
                case 4: d[i] = ($"Focus {i}", "+12% crit damage", p=>CritD(p,0.12f)); break;
                default: d[i] = ($"Reach {i}", "+4% spell area", p=>Area(p,0.04f)); break;
            }
        }
        d[32] = ("Ascendant ★","+12% damage, +8% crit", p=>{A(p,0.12f); Crit(p,0.08f);});
        d[33] = ("Bulwark ★","+12% resistance, +60 health", p=>{Res(p,0.12f); HP(p,60f);});
        d[34] = ("Ruin ★","+15% damage, +20% crit damage", p=>{A(p,0.15f); CritD(p,0.2f);});
        d[35] = ("Aegis ★","+40 health, +8% resist, +8% damage", p=>{HP(p,40f); Res(p,0.08f); A(p,0.08f);});
        return d;
    }
    private static HiddenRoute[] GenericRoutes(int w) => new[]{
        new HiddenRoute { Name="Duelist", Desc="hidden — +10% crit, +25% crit damage", Req=new[]{0,4,8,9,16}, Apply=p=>{Crit(p,0.1f); CritD(p,0.25f);} },
        new HiddenRoute { Name="Sentinel", Desc="hidden — +12% resist, +60 health, +6% damage", Req=new[]{2,3,6,7,13,14,18,19,22}, Apply=p=>{Res(p,0.12f); HP(p,60f); A(p,0.06f);} },
        new HiddenRoute { Name="Ascendant", Desc="hidden — +18% damage, +50 health, +8% crit, +8% area", Req=new[]{0,4,8,9,16,20,24,25,32,5,10,17,21}, Apply=p=>{A(p,0.18f); HP(p,50f); Crit(p,0.08f); Area(p,0.08f);} },
    };
}
