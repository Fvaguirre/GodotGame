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
//   level-up view thereafter).
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
            var arr = new PerkNode[NodeCount];
            for (int i = 0; i < NodeCount; i++)
                arr[i] = new PerkNode { Id = i, Name = defs[i].n, Desc = defs[i].d, Col = POS[i].c, Row = POS[i].r, Keystone = i >= 32, Apply = defs[i].a };
            _nodes[w] = arr;
            _routes[w] = w switch { 0 => LunarRoutes(), 1 => DivineRoutes(), 2 => CrimsonRoutes(), 3 => VerdantRoutes(), 4 => GaleRoutes(), 5 => FrostRoutes(), 6 => ForsakenRoutes(), 7 => EmberRoutes(), 8 => ArcaneRoutes(), _ => GenericRoutes(w) };
        }
    }

    // shorthands
    private static void A(Player p, float d) => p.S.Atk += d;
    private static void Crit(Player p, float c) => p.S.CritChance = Mathf.Min(1f, p.S.CritChance + c);
    private static void CritD(Player p, float c) => p.S.CritDamage += c;
    private static void HP(Player p, float h) => p.S.MaxHp += h;
    private static void Res(Player p, float r) => p.S.DmgResist = Mathf.Min(0.8f, p.S.DmgResist + r);
    private static void Area(Player p, float a) => p.S.SpellArea += a;
    private static void Ult(Player p, float u) => p.UltChargeMul = Mathf.Min(2.5f, p.UltChargeMul + u);

    // ===== LUNAR (template) =====
    private static (string n, string d, System.Action<Player> a)[] LunarDefs() => new (string, string, System.Action<Player>)[]{
        ("Keen Edge","+3% crit chance", p=>Crit(p,0.03f)), ("Moonbrand","+4% Lunar damage", p=>p.LunarBonus+=0.04f),
        ("Duskbound","+4% Lunar damage (2× night)", p=>p.LunarBonus+=0.04f), ("Nightward","+4% resistance", p=>Res(p,0.04f)),
        ("Silver Point","+12% crit damage", p=>CritD(p,0.12f)), ("Pale Light","+4% Lunar damage", p=>p.LunarBonus+=0.04f),
        ("Gloaming","+3% damage", p=>A(p,0.03f)), ("Shadowmantle","+4% resistance", p=>Res(p,0.04f)),
        ("Waxing Blade","+1 crescent pierce", p=>p.CrescentPierceBonus++), ("Sharp Sickle","+18% crescent size", p=>p.CrescentSizeMul=Mathf.Min(2.8f,p.CrescentSizeMul+0.18f)),
        ("Glimmer","+4% spell area", p=>Area(p,0.04f)), ("Moonveil","+4% spell area", p=>Area(p,0.04f)),
        ("Eventide","+8% ult charge", p=>Ult(p,0.08f)), ("Starfall","+8% ult charge", p=>Ult(p,0.08f)),
        ("Gloomskin","+4% resistance", p=>Res(p,0.04f)), ("Moonstone","+25 max health", p=>HP(p,25f)),
        ("Twin Crescent","+1 pierce, +3% crit", p=>{p.CrescentPierceBonus++; Crit(p,0.03f);}), ("Deep Brand","+5% Lunar damage", p=>p.LunarBonus+=0.05f),
        ("Twilight","+4% damage", p=>A(p,0.04f)), ("Duskguard","+4% resistance", p=>Res(p,0.04f)),
        ("Bright Point","+14% crit damage", p=>CritD(p,0.14f)), ("Nightbloom","+5% Lunar dmg, +6% ult", p=>{p.LunarBonus+=0.05f; Ult(p,0.06f);}),
        ("Moonlit Ward","+4% resist, +6% ult", p=>{Res(p,0.04f); Ult(p,0.06f);}), ("Heartmoon","+30 max health", p=>HP(p,30f)),
        ("Reaper's Arc","+1 crescent pierce", p=>p.CrescentPierceBonus++), ("Moonfire","+5% Lunar damage", p=>p.LunarBonus+=0.05f),
        ("Nightsong","+8% ult charge", p=>Ult(p,0.08f)), ("Umbral","+4% resistance", p=>Res(p,0.04f)),
        ("Starlight","+4% damage", p=>A(p,0.04f)), ("Moonshield","+25 max health", p=>HP(p,25f)),
        ("Duskblade","+12% crit damage", p=>CritD(p,0.12f)), ("Nightguard","+4% resistance", p=>Res(p,0.04f)),
        ("Full Moon ★","+2 pierce, +30% size, +8% crit", p=>{p.CrescentPierceBonus+=2; p.CrescentSizeMul=Mathf.Min(2.9f,p.CrescentSizeMul+0.3f); Crit(p,0.08f);}),   // K1
        ("Moonwell ★","+12% Lunar damage, +8% area", p=>{p.LunarBonus+=0.12f; Area(p,0.08f);}),                     // K2
        ("Eventide ★","+30% ult charge, +6% damage", p=>{Ult(p,0.3f); A(p,0.06f);}),                                 // K3
        ("Nightbulwark ★","+12% resistance, +60 health", p=>{Res(p,0.12f); HP(p,60f);}),                            // K4
    };
    private static HiddenRoute[] LunarRoutes() => new[]{
        new HiddenRoute { Name="Silver Reaper", Desc="hidden — a lean crescent-crit killer: +10% crit, +2 crescent pierce, +25% crit damage",
            Req=new[]{0,4,8,9,16}, Apply=p=>{Crit(p,0.1f); p.CrescentPierceBonus+=2; CritD(p,0.25f);} },
        new HiddenRoute { Name="Eclipse Warden", Desc="hidden — a night-shrouded bulwark: +12% resist, +50 health, +20% ult charge, +8% Lunar damage",
            Req=new[]{2,3,6,7,13,14,18,19,22}, Apply=p=>{Res(p,0.12f); HP(p,50f); Ult(p,0.2f); p.LunarBonus+=0.08f;} },
        new HiddenRoute { Name="Lunar Colossus", Desc="hidden — ascend into the moon itself: +15% damage, +15% Lunar dmg, +30% ult charge, +50 health, +8% crit",
            Req=new[]{0,4,8,9,16,20,24,25,32,5,10,17,21}, Apply=p=>{A(p,0.15f); p.LunarBonus+=0.15f; Ult(p,0.3f); HP(p,50f); Crit(p,0.08f);} },
    };

    // shared hidden-route node-sets (graph-connected: 5 / 9 / 13 nodes → weak / medium / strong)
    private static readonly int[] R5 = { 0, 4, 8, 9, 16 };
    private static readonly int[] R9 = { 2, 3, 6, 7, 13, 14, 18, 19, 22 };
    private static readonly int[] R13 = { 0, 4, 8, 9, 16, 20, 24, 25, 32, 5, 10, 17, 21 };
    private static void Sp(Player p, float s) => p.S.Speed = Mathf.Min(18f, p.S.Speed + s);

    // ===== DIVINE (1) — holy / shields / interventions =====
    private static (string n, string d, System.Action<Player> a)[] DivineDefs() => new (string, string, System.Action<Player>)[]{
        ("Sunfire","+4% damage", p=>A(p,0.04f)), ("Radiance","+4% damage", p=>A(p,0.04f)), ("Devout","+4% resistance", p=>Res(p,0.04f)), ("Warding","+25% shield cap", p=>p.S.ShieldPct+=0.05f),
        ("Piercing Light","+1 pierce", p=>p.S.Pierce+=1), ("Zeal","+4% crit chance", p=>Crit(p,0.04f)), ("Fervor","+1s blessing", p=>p.BlessBonus+=1f), ("Sanctified","+25 max health", p=>HP(p,25f)),
        ("Smite","+5% damage", p=>A(p,0.05f)), ("Consecrant","+shield regen", p=>p.S.ShieldRegen+=0.3f), ("Benediction","+1s blessing", p=>p.BlessBonus+=1f), ("Halo","+4% spell area", p=>Area(p,0.04f)),
        ("Tithe","+8% ult charge", p=>Ult(p,0.08f)), ("Grace","+4% resistance", p=>Res(p,0.04f)), ("Ordained","+30 max health", p=>HP(p,30f)), ("Aegis","+25% shield cap", p=>p.S.ShieldPct+=0.05f),
        ("Zealot","+6% damage, +4% crit", p=>{A(p,0.06f); Crit(p,0.04f);}), ("Reckoner","+5% damage", p=>A(p,0.05f)), ("Consecrate","+6% spell area", p=>Area(p,0.06f)), ("Bulwark","+4% resistance", p=>Res(p,0.04f)),
        ("Judgement","+14% crit damage", p=>CritD(p,0.14f)), ("Divine Might","+6% damage", p=>A(p,0.06f)), ("Sanctuary","+30 HP, +4% resist", p=>{HP(p,30f); Res(p,0.04f);}), ("Heartlight","+30 max health", p=>HP(p,30f)),
        ("Retribution","+6% damage", p=>A(p,0.06f)), ("Blessing","+1s blessing", p=>p.BlessBonus+=1f), ("Empyrean","+10% ult charge", p=>Ult(p,0.1f)), ("Devotion","+4% resistance", p=>Res(p,0.04f)),
        ("Sunblade","+5% damage", p=>A(p,0.05f)), ("Faithguard","+30 max health", p=>HP(p,30f)), ("Sunburst","+12% crit damage", p=>CritD(p,0.12f)), ("Sanctum","+5% resistance", p=>Res(p,0.05f)),
        ("Dawnbringer ★","+12% damage, +8% crit", p=>{A(p,0.12f); Crit(p,0.08f);}), ("Seraph ★","+1 Intervention, +8% resist", p=>{p.Interventions++; Res(p,0.08f);}),
        ("Empyreal ★","+25% ult charge, +6% damage", p=>{Ult(p,0.25f); A(p,0.06f);}), ("Bulwark of Dawn ★","+12% resistance, +60 health", p=>{Res(p,0.12f); HP(p,60f);}),
    };
    private static HiddenRoute[] DivineRoutes() => new[]{
        new HiddenRoute { Name="Sun Cleric", Desc="hidden — +8% damage, +8% crit, +20% crit damage", Req=R5, Apply=p=>{A(p,0.08f); Crit(p,0.08f); CritD(p,0.2f);} },
        new HiddenRoute { Name="Bulwark Saint", Desc="hidden — +12% resist, +60 health, +1 Intervention", Req=R9, Apply=p=>{Res(p,0.12f); HP(p,60f); p.Interventions++;} },
        new HiddenRoute { Name="Archon", Desc="hidden — +15% damage, +2 Interventions, +40 health, +8% crit", Req=R13, Apply=p=>{A(p,0.15f); p.Interventions+=2; HP(p,40f); Crit(p,0.08f);} },
    };

    // ===== CRIMSON (2) — blood / lifesteal / crit =====
    private static (string n, string d, System.Action<Player> a)[] CrimsonDefs() => new (string, string, System.Action<Player>)[]{
        ("Leech","+5% lifesteal", p=>p.S.Lifesteal+=0.05f), ("Blooded","+4% damage", p=>A(p,0.04f)), ("Thickskin","+4% resistance", p=>Res(p,0.04f)), ("Vital","+25 max health", p=>HP(p,25f)),
        ("Reckless","+4% crit chance", p=>Crit(p,0.04f)), ("Feast","+4% lifesteal", p=>p.S.Lifesteal+=0.04f), ("Wide Aura","+bigger blood aura", p=>p.AuraBonusR+=1.5f), ("Hardened","+25 max health", p=>HP(p,25f)),
        ("Savagery","+14% crit damage", p=>CritD(p,0.14f)), ("Gorge","+5% lifesteal", p=>p.S.Lifesteal+=0.05f), ("Bloodpact","+5% damage", p=>A(p,0.05f)), ("Crimson Reach","+4% spell area", p=>Area(p,0.04f)),
        ("Bloodhaste","+cast speed", p=>p.S.FireCd=Mathf.Max(0.1f,p.S.FireCd*0.94f)), ("Toughen","+4% resistance", p=>Res(p,0.04f)), ("Ironblood","+30 max health", p=>HP(p,30f)), ("Aura Well","+bigger blood aura", p=>p.AuraBonusR+=1.5f),
        ("Berserk","+6% damage, +4% crit", p=>{A(p,0.06f); Crit(p,0.04f);}), ("Ravage","+5% damage", p=>A(p,0.05f)), ("Crimson Tide","+6% spell area", p=>Area(p,0.06f)), ("Bloodguard","+4% resistance", p=>Res(p,0.04f)),
        ("Butchery","+16% crit damage", p=>CritD(p,0.16f)), ("Sanguine","+6% damage", p=>A(p,0.06f)), ("Bloodward","+30 HP, +4% resist", p=>{HP(p,30f); Res(p,0.04f);}), ("Lifeblood","+30 max health", p=>HP(p,30f)),
        ("Carnage","+6% damage", p=>A(p,0.06f)), ("Siphon","+5% lifesteal", p=>p.S.Lifesteal+=0.05f), ("Fleetblood","+move speed", p=>Sp(p,0.5f)), ("Crimson Skin","+4% resistance", p=>Res(p,0.04f)),
        ("Gash","+5% damage", p=>A(p,0.05f)), ("Bloodplate","+30 max health", p=>HP(p,30f)), ("Slaughter","+12% crit damage", p=>CritD(p,0.12f)), ("Clotguard","+5% resistance", p=>Res(p,0.05f)),
        ("Berserker ★","+12% crit, +40% crit damage", p=>{Crit(p,0.12f); CritD(p,0.4f);}), ("Vampiric ★","+12% lifesteal, +40 health", p=>{p.S.Lifesteal+=0.12f; HP(p,40f);}),
        ("Bloodlord ★","+15% damage, +8% lifesteal", p=>{A(p,0.15f); p.S.Lifesteal+=0.08f;}), ("Ironheart ★","+12% resistance, +60 health", p=>{Res(p,0.12f); HP(p,60f);}),
    };
    private static HiddenRoute[] CrimsonRoutes() => new[]{
        new HiddenRoute { Name="Bloodletter", Desc="hidden — +10% crit, +30% crit damage, +6% lifesteal", Req=R5, Apply=p=>{Crit(p,0.1f); CritD(p,0.3f); p.S.Lifesteal+=0.06f;} },
        new HiddenRoute { Name="Sanguine Lord", Desc="hidden — +12% lifesteal, +60 health, +8% damage", Req=R9, Apply=p=>{p.S.Lifesteal+=0.12f; HP(p,60f); A(p,0.08f);} },
        new HiddenRoute { Name="Crimson God", Desc="hidden — +18% damage, +15% lifesteal, +12% crit, +40 health", Req=R13, Apply=p=>{A(p,0.18f); p.S.Lifesteal+=0.15f; Crit(p,0.12f); HP(p,40f);} },
    };

    // ===== VERDANT (3) — grove / poison / bulk =====
    private static (string n, string d, System.Action<Player> a)[] VerdantDefs() => new (string, string, System.Action<Player>)[]{
        ("Blighttouch","+4% damage", p=>A(p,0.04f)), ("Sapling","ents grow faster", p=>p.GroveEvery=Mathf.Max(6,p.GroveEvery-1)), ("Barkhide","+4% resistance", p=>Res(p,0.04f)), ("Heartwood","+30 max health", p=>HP(p,30f)),
        ("Creeping Death","+5% damage", p=>A(p,0.05f)), ("Deep Roots","+1 max tree-ent", p=>p.GroveBonusEnts=Mathf.Min(8,p.GroveBonusEnts+1)), ("Thornmail","+4% resistance", p=>Res(p,0.04f)), ("Toughbark","+30 max health", p=>HP(p,30f)),
        ("Necrosis","+6% damage", p=>A(p,0.06f)), ("Seedfall","+1 max tree-ent", p=>p.GroveBonusEnts=Mathf.Min(8,p.GroveBonusEnts+1)), ("Spread","+6% spell area", p=>Area(p,0.06f)), ("Regrowth","+4% resistance", p=>Res(p,0.04f)),
        ("Overgrowth","+6% area", p=>Area(p,0.06f)), ("Ironroot","+5% resistance", p=>Res(p,0.05f)), ("Ancient Bark","+40 max health", p=>HP(p,40f)), ("Fast Grove","ents grow faster", p=>p.GroveEvery=Mathf.Max(6,p.GroveEvery-1)),
        ("Plaguetouch","+6% damage", p=>A(p,0.06f)), ("Virulence","+6% damage", p=>A(p,0.06f)), ("Wildgrowth","+8% spell area", p=>Area(p,0.08f)), ("Bulwark Bark","+4% resistance", p=>Res(p,0.04f)),
        ("Blight Bloom","+6% area, +4% dmg", p=>{Area(p,0.06f); A(p,0.04f);}), ("Rot","+6% damage", p=>A(p,0.06f)), ("Grove Ward","+40 HP, +4% resist", p=>{HP(p,40f); Res(p,0.04f);}), ("Vitality","+40 max health", p=>HP(p,40f)),
        ("Decay","+6% damage", p=>A(p,0.06f)), ("Elder Seed","+1 max tree-ent", p=>p.GroveBonusEnts=Mathf.Min(8,p.GroveBonusEnts+1)), ("Swiftgrove","ents grow faster", p=>p.GroveEvery=Mathf.Max(6,p.GroveEvery-1)), ("Mossguard","+4% resistance", p=>Res(p,0.04f)),
        ("Toxin","+5% damage", p=>A(p,0.05f)), ("Deadwood","+40 max health", p=>HP(p,40f)), ("Wither","+6% damage", p=>A(p,0.06f)), ("Barkplate","+5% resistance", p=>Res(p,0.05f)),
        ("Plaguelord ★","+15% damage, +12% spell area", p=>{A(p,0.15f); Area(p,0.12f);}), ("Elder Grove ★","+2 tree-ents, faster growth", p=>{p.GroveBonusEnts=Mathf.Min(8,p.GroveBonusEnts+2); p.GroveEvery=Mathf.Max(6,p.GroveEvery-2);}),
        ("Wildheart ★","+10% area, +40 health", p=>{Area(p,0.1f); HP(p,40f);}), ("Ironbark ★","+12% resistance, +80 health", p=>{Res(p,0.12f); HP(p,80f);}),
    };
    private static HiddenRoute[] VerdantRoutes() => new[]{
        new HiddenRoute { Name="Grovekeeper", Desc="hidden — +2 tree-ents, faster growth", Req=R5, Apply=p=>{p.GroveBonusEnts=Mathf.Min(8,p.GroveBonusEnts+2); p.GroveEvery=Mathf.Max(6,p.GroveEvery-1);} },
        new HiddenRoute { Name="Ancient Warden", Desc="hidden — +80 health, +12% resist, +8% damage", Req=R9, Apply=p=>{HP(p,80f); Res(p,0.12f); A(p,0.08f);} },
        new HiddenRoute { Name="Worldtree", Desc="hidden — +3 tree-ents, +15% damage, +12% area, +60 health", Req=R13, Apply=p=>{p.GroveBonusEnts=Mathf.Min(8,p.GroveBonusEnts+3); A(p,0.15f); Area(p,0.12f); HP(p,60f);} },
    };

    // ===== GALE (4) — wind / mobility / airborne =====
    private static (string n, string d, System.Action<Player> a)[] GaleDefs() => new (string, string, System.Action<Player>)[]{
        ("Gale Force","+4% damage", p=>A(p,0.04f)), ("Fleet","+move speed", p=>Sp(p,0.5f)), ("Windguard","+4% resistance", p=>Res(p,0.04f)), ("Airborne","+30 max health", p=>HP(p,30f)),
        ("Cutting Gust","+4% crit", p=>Crit(p,0.04f)), ("Slipwind","+1 dash charge", p=>p.S.DashCharges++), ("Tailwind","+move speed", p=>Sp(p,0.5f)), ("Featherfall","+8% jump height", p=>p.S.JumpMul+=0.08f),
        ("Buffet","+stronger gusts", p=>p.GustPower=Mathf.Min(2.5f,p.GustPower+0.15f)), ("Quickstep","faster dash cd", p=>p.S.DashCd=Mathf.Max(0.9f,p.S.DashCd*0.9f)), ("Updraft","+8% jump height", p=>p.S.JumpMul+=0.08f), ("Whirl","+6% spell area", p=>Area(p,0.06f)),
        ("Crosswind","+stronger gusts", p=>p.GustPower=Mathf.Min(2.5f,p.GustPower+0.15f)), ("Slipstream","+4% resistance", p=>Res(p,0.04f)), ("Skysong","+30 max health", p=>HP(p,30f)), ("Zephyr","+move speed", p=>Sp(p,0.5f)),
        ("Stormheart","+6% damage, +4% crit", p=>{A(p,0.06f); Crit(p,0.04f);}), ("Aloft","+5% damage", p=>A(p,0.05f)), ("Cyclone","+6% spell area", p=>Area(p,0.06f)), ("Gustguard","+4% resistance", p=>Res(p,0.04f)),
        ("Tempest","+14% crit damage", p=>CritD(p,0.14f)), ("Windblade","+6% damage", p=>A(p,0.06f)), ("Skyward","+30 HP, +8% jump", p=>{HP(p,30f); p.S.JumpMul+=0.08f;}), ("Gale Skin","+30 max health", p=>HP(p,30f)),
        ("Riptide","+6% damage", p=>A(p,0.06f)), ("Maelstrom","+stronger gusts", p=>p.GustPower=Mathf.Min(2.5f,p.GustPower+0.15f)), ("Second Wind","+move speed", p=>Sp(p,0.5f)), ("Eye Calm","+4% resistance", p=>Res(p,0.04f)),
        ("Downburst","+5% damage", p=>A(p,0.05f)), ("Windwall","+30 max health", p=>HP(p,30f)), ("Jetstream","+12% crit damage", p=>CritD(p,0.12f)), ("Stormguard","+5% resistance", p=>Res(p,0.05f)),
        ("Windwalker ★","+1 dash, +8% damage", p=>{p.S.DashCharges++; A(p,0.08f);}), ("Stormheart ★","+14% damage, +10% crit", p=>{A(p,0.14f); Crit(p,0.1f);}),
        ("Tempest Lord ★","+15% damage, +12% area", p=>{A(p,0.15f); Area(p,0.12f);}), ("Eye of Calm ★","+8% resistance, +1 dash charge", p=>{Res(p,0.08f); p.S.DashCharges++;}),
    };
    private static HiddenRoute[] GaleRoutes() => new[]{
        new HiddenRoute { Name="Duelist", Desc="hidden — +10% crit, +25% crit damage, +1 dash", Req=R5, Apply=p=>{Crit(p,0.1f); CritD(p,0.25f); p.S.DashCharges++;} },
        new HiddenRoute { Name="Skydancer", Desc="hidden — +move speed, +15% jump, +8% damage, +40 health", Req=R9, Apply=p=>{Sp(p,1.2f); p.S.JumpMul+=0.15f; A(p,0.08f); HP(p,40f);} },
        new HiddenRoute { Name="Storm Sovereign", Desc="hidden — +18% damage, +2 dash charges, +12% crit, +12% area", Req=R13, Apply=p=>{A(p,0.18f); p.S.DashCharges+=2; Crit(p,0.12f); Area(p,0.12f);} },
    };

    // ===== FROST (5) — freeze / shatter / snipe =====
    private static (string n, string d, System.Action<Player> a)[] FrostDefs() => new (string, string, System.Action<Player>)[]{
        ("Chillblade","+4% damage", p=>A(p,0.04f)), ("Hoarfrost","+freeze buildup", p=>p.FreezeRate+=0.2f), ("Frostmail","+4% resistance", p=>Res(p,0.04f)), ("Rimeguard","+25 max health", p=>HP(p,25f)),
        ("Longsight","+8% spell range", p=>p.S.SpellRange+=0.08f), ("Permafrost","+0.4s frozen", p=>p.FrostDurBonus+=0.4f), ("Coldsnap","+4% resistance", p=>Res(p,0.04f)), ("Frostskin","+25 max health", p=>HP(p,25f)),
        ("Coldsteel","+4% crit", p=>Crit(p,0.04f)), ("Flashfreeze","freeze sooner", p=>p.FreezeThreshMul=Mathf.Max(0.4f,p.FreezeThreshMul*0.95f)), ("Riftsplit","+12% shatter dmg", p=>p.ShatterPowerMul+=0.12f), ("Icebound","+6% spell area", p=>Area(p,0.06f)),
        ("Deepwinter","+5% damage", p=>A(p,0.05f)), ("Rimeplate","+4% resistance", p=>Res(p,0.04f)), ("Glacial HP","+30 max health", p=>HP(p,30f)), ("Farsight","+8% spell range", p=>p.S.SpellRange+=0.08f),
        ("Winter Bite","+6% damage, +4% crit", p=>{A(p,0.06f); Crit(p,0.04f);}), ("Shardburst","+shatter freeze", p=>p.ShatterFreezeStacks+=0.4f), ("Icefall","+12% shatter dmg", p=>p.ShatterPowerMul+=0.12f), ("Coldguard","+4% resistance", p=>Res(p,0.04f)),
        ("Frostbite","+14% crit damage", p=>CritD(p,0.14f)), ("Cryo","+6% damage", p=>A(p,0.06f)), ("Frost Ward","+30 HP, +4% resist", p=>{HP(p,30f); Res(p,0.04f);}), ("Snowdrift","+30 max health", p=>HP(p,30f)),
        ("Fracture","+12% shatter dmg", p=>p.ShatterPowerMul+=0.12f), ("Deep Rime","+freeze buildup", p=>p.FreezeRate+=0.2f), ("Cold Step","+move speed", p=>Sp(p,0.4f)), ("Iceguard","+4% resistance", p=>Res(p,0.04f)),
        ("Sleet","+5% damage", p=>A(p,0.05f)), ("Glacier HP","+30 max health", p=>HP(p,30f)), ("Splinter","+12% crit damage", p=>CritD(p,0.12f)), ("Frostwall","+5% resistance", p=>Res(p,0.05f)),
        ("Winter Sovereign ★","+14% damage, +10% crit", p=>{A(p,0.14f); Crit(p,0.1f);}), ("Zero Point ★","+freeze buildup, freeze sooner", p=>{p.FreezeRate+=0.5f; p.FrostDurBonus+=0.6f; p.FreezeThreshMul=Mathf.Max(0.35f,p.FreezeThreshMul*0.85f);}),
        ("Grand Shatter ★","+40% shatter damage, +8% damage", p=>{p.ShatterPowerMul+=0.4f; A(p,0.08f);}), ("Cold Sovereign ★","+12% resistance, +50 health", p=>{Res(p,0.12f); HP(p,50f);}),
    };
    private static HiddenRoute[] FrostRoutes() => new[]{
        new HiddenRoute { Name="Icebreaker", Desc="hidden — +30% shatter damage, +10% crit, +25% crit dmg", Req=R5, Apply=p=>{p.ShatterPowerMul+=0.3f; Crit(p,0.1f); CritD(p,0.25f);} },
        new HiddenRoute { Name="Frost Fortress", Desc="hidden — +12% resist, +60 health, +freeze buildup & duration", Req=R9, Apply=p=>{Res(p,0.12f); HP(p,60f); p.FreezeRate+=0.5f; p.FrostDurBonus+=0.6f;} },
        new HiddenRoute { Name="Absolute Zero", Desc="hidden — +50% shatter, +15% damage, foes freeze instantly, +40 health", Req=R13, Apply=p=>{p.ShatterPowerMul+=0.5f; A(p,0.15f); p.FreezeThreshMul=Mathf.Max(0.25f,p.FreezeThreshMul*0.6f); HP(p,40f);} },
    };

    // ===== FORSAKEN (6) — curse / tethers / siphon =====
    private static (string n, string d, System.Action<Player> a)[] ForsakenDefs() => new (string, string, System.Action<Player>)[]{
        ("Maleficence","+4% damage", p=>A(p,0.04f)), ("Blight","+curse buildup", p=>p.CurseRate+=0.4f), ("Insubstantial","+4% resistance", p=>Res(p,0.04f)), ("Dreadbone","+25 max health", p=>HP(p,25f)),
        ("Virulence","+8% dmg to cursed", p=>p.CurseBonusMul+=0.08f), ("Bindings","+1 tether", p=>p.MaxLinks=Mathf.Min(12,p.MaxLinks+1)), ("Ghoststep","+move speed", p=>Sp(p,0.4f)), ("Wraithskin","+25 max health", p=>HP(p,25f)),
        ("Anathema","+2 crush ceiling", p=>p.CurseStackCap+=2f), ("Contagion","+curse buildup", p=>p.CurseRate+=0.4f), ("Siphon","+beam lifesteal", p=>p.CurseBeamLifesteal=Mathf.Min(1f,p.CurseBeamLifesteal+0.08f)), ("Spread","+curse spread range", p=>p.CurseSpreadRange+=2f),
        ("Sympathy","+damage sharing", p=>p.CurseShareFrac=Mathf.Min(1f,p.CurseShareFrac+0.08f)), ("Rotplate","+4% resistance", p=>Res(p,0.04f)), ("Soulhide","+30 max health", p=>HP(p,30f)), ("Farhex","+curse spread range", p=>p.CurseSpreadRange+=2f),
        ("Doombrand","+8% dmg to cursed", p=>p.CurseBonusMul+=0.08f), ("Exsanguinate","+6% lifesteal", p=>p.S.Lifesteal+=0.06f), ("Wither","+6% spell area", p=>Area(p,0.06f)), ("Hexguard","+4% resistance", p=>Res(p,0.04f)),
        ("Malefic","+14% crit damage", p=>CritD(p,0.14f)), ("Reap","+6% damage", p=>A(p,0.06f)), ("Soulward","+30 HP, +4% resist", p=>{HP(p,30f); Res(p,0.04f);}), ("Deathbind","+30 max health", p=>HP(p,30f)),
        ("Plague","+6% damage", p=>A(p,0.06f)), ("Deep Curse","+curse buildup", p=>p.CurseRate+=0.4f), ("Revenant","+move speed", p=>Sp(p,0.4f)), ("Boneguard","+4% resistance", p=>Res(p,0.04f)),
        ("Blight II","+5% damage", p=>A(p,0.05f)), ("Gravemark","+30 max health", p=>HP(p,30f)), ("Torment","+8% dmg to cursed", p=>p.CurseBonusMul+=0.08f), ("Shroud","+5% resistance", p=>Res(p,0.05f)),
        ("Doomherald ★","+14% damage, +12% to cursed", p=>{A(p,0.14f); p.CurseBonusMul+=0.12f;}), ("Coven's Grip ★","+2 tethers, +damage sharing", p=>{p.MaxLinks=Mathf.Min(12,p.MaxLinks+2); p.CurseShareFrac=Mathf.Min(1f,p.CurseShareFrac+0.1f);}),
        ("Soul Glutton ★","+15% beam lifesteal, +10% damage", p=>{p.CurseBeamLifesteal=Mathf.Min(1f,p.CurseBeamLifesteal+0.15f); A(p,0.1f);}), ("Revenant King ★","+12% resistance, +60 health", p=>{Res(p,0.12f); HP(p,60f);}),
    };
    private static HiddenRoute[] ForsakenRoutes() => new[]{
        new HiddenRoute { Name="Hexer", Desc="hidden — +15% damage to cursed, +2 crush ceiling, +curse buildup", Req=R5, Apply=p=>{p.CurseBonusMul+=0.15f; p.CurseStackCap+=2f; p.CurseRate+=0.5f;} },
        new HiddenRoute { Name="Soulbinder", Desc="hidden — +3 tethers, +damage sharing, +12% beam lifesteal", Req=R9, Apply=p=>{p.MaxLinks=Mathf.Min(12,p.MaxLinks+3); p.CurseShareFrac=Mathf.Min(1f,p.CurseShareFrac+0.15f); p.CurseBeamLifesteal=Mathf.Min(1f,p.CurseBeamLifesteal+0.12f);} },
        new HiddenRoute { Name="Doom Herald", Desc="hidden — +18% damage, +18% to cursed, +15% siphon, +40 health", Req=R13, Apply=p=>{A(p,0.18f); p.CurseBonusMul+=0.18f; p.CurseBeamLifesteal=Mathf.Min(1f,p.CurseBeamLifesteal+0.15f); HP(p,40f);} },
    };

    // ===== EMBER (7) — burn / Living Bomb / meteor =====
    private static (string n, string d, System.Action<Player> a)[] EmberDefs() => new (string, string, System.Action<Player>)[]{
        ("Emberfeed","+12% burn damage", p=>p.EmberBurnMul+=0.12f), ("Kindling","+4% damage", p=>A(p,0.04f)), ("Heatshimmer","+4% resistance", p=>Res(p,0.04f)), ("Emberward","+25 max health", p=>HP(p,25f)),
        ("Scatterspark","+flame reach", p=>p.FlameReachMul+=0.1f), ("Slowburn","+6% spell area", p=>Area(p,0.06f)), ("Cinderskin","+4% resistance", p=>Res(p,0.04f)), ("Ashguard","+25 max health", p=>HP(p,25f)),
        ("Firestarter","+4% crit", p=>Crit(p,0.04f)), ("Contagion","+20% Living Bomb", p=>p.LivingBombMul+=0.2f), ("Wildheat","+5% damage", p=>A(p,0.05f)), ("Backdraft","+6% spell area", p=>Area(p,0.06f)),
        ("Overheat","+14% crit damage", p=>CritD(p,0.14f)), ("Heatplate","+4% resistance", p=>Res(p,0.04f)), ("Phoenix HP","+30 max health", p=>HP(p,30f)), ("Longflame","+flame reach", p=>p.FlameReachMul+=0.1f),
        ("Conflagrate","+12% burn dmg", p=>p.EmberBurnMul+=0.12f), ("Airburst","+5% damage", p=>A(p,0.05f)), ("Firestorm","+8% spell area", p=>Area(p,0.08f)), ("Cinderguard","+4% resistance", p=>Res(p,0.04f)),
        ("Detonator","+16% crit damage", p=>CritD(p,0.16f)), ("Pyre","+6% damage", p=>A(p,0.06f)), ("Ember Ward","+30 HP, +4% resist", p=>{HP(p,30f); Res(p,0.04f);}), ("Cinderheart","+30 max health", p=>HP(p,30f)),
        ("Falling Sky","+6% damage", p=>A(p,0.06f)), ("Chain Bomb","+20% Living Bomb", p=>p.LivingBombMul+=0.2f), ("Phoenix Step","+move speed", p=>Sp(p,0.4f)), ("Heatshield","+4% resistance", p=>Res(p,0.04f)),
        ("Scorch","+5% damage", p=>A(p,0.05f)), ("Ashplate","+30 max health", p=>HP(p,30f)), ("Meteoric","+12% crit damage", p=>CritD(p,0.12f)), ("Flamewall","+5% resistance", p=>Res(p,0.05f)),
        ("Conflagration ★","+40% burn damage, +8% area", p=>{p.EmberBurnMul+=0.4f; Area(p,0.08f);}), ("Chain Detonation ★","+50% Living Bomb, +reach", p=>{p.LivingBombMul+=0.5f; p.FlameReachMul+=0.15f;}),
        ("Falling Sky ★","+15% damage, +12% area, +30% crit dmg", p=>{A(p,0.15f); Area(p,0.12f); CritD(p,0.3f);}), ("Living Pyre ★","+12% resistance, +60 health", p=>{Res(p,0.12f); HP(p,60f);}),
    };
    private static HiddenRoute[] EmberRoutes() => new[]{
        new HiddenRoute { Name="Firestarter", Desc="hidden — +30% burn damage, +10% crit, +flame reach", Req=R5, Apply=p=>{p.EmberBurnMul+=0.3f; Crit(p,0.1f); p.FlameReachMul+=0.15f;} },
        new HiddenRoute { Name="Pyromancer", Desc="hidden — +40% Living Bomb, +12% area, +8% damage, +40 health", Req=R9, Apply=p=>{p.LivingBombMul+=0.4f; Area(p,0.12f); A(p,0.08f); HP(p,40f);} },
        new HiddenRoute { Name="Cataclysm", Desc="hidden — +50% burn, +40% Living Bomb, +15% damage, +40 health", Req=R13, Apply=p=>{p.EmberBurnMul+=0.5f; p.LivingBombMul+=0.4f; A(p,0.15f); HP(p,40f);} },
    };

    // ===== ARCANE (8) — missiles / marks / crit-heal =====
    private static (string n, string d, System.Action<Player> a)[] ArcaneDefs() => new (string, string, System.Action<Player>)[]{
        ("Volley","+4% damage", p=>A(p,0.04f)), ("Attune","+4% damage", p=>A(p,0.04f)), ("Arcane Shell","+4% resistance", p=>Res(p,0.04f)), ("Ward","+25 max health", p=>HP(p,25f)),
        ("Precision","+4% crit", p=>Crit(p,0.04f)), ("Swiftcast","+cast speed", p=>p.S.FireCd=Mathf.Max(0.08f,p.S.FireCd*0.94f)), ("Farcast","+8% spell range", p=>p.S.SpellRange+=0.08f), ("Recall","+move speed", p=>Sp(p,0.4f)),
        ("Multishot","+8% projectile speed", p=>p.S.ProjSpeed+=0.08f), ("Overload","+arcane mark duration", p=>p.ArcaneMarkDur+=0.6f), ("Empower","+14% crit damage", p=>CritD(p,0.14f)), ("Resonance","+6% spell range", p=>p.S.SpellRange+=0.06f),
        ("Feedback","+crit-heal", p=>p.ArcaneCritHealBonus+=0.04f), ("Sigilplate","+4% resistance", p=>Res(p,0.04f)), ("Arcane HP","+30 max health", p=>HP(p,30f)), ("Longcast","+8% spell range", p=>p.S.SpellRange+=0.08f),
        ("Barrage","+6% damage, +proj speed", p=>{A(p,0.06f); p.S.ProjSpeed+=0.06f;}), ("Amplify","+5% damage", p=>A(p,0.05f)), ("Conduit","+arcane mark duration", p=>p.ArcaneMarkDur+=0.6f), ("Shellguard","+4% resistance", p=>Res(p,0.04f)),
        ("Overcharge","+16% crit damage", p=>CritD(p,0.16f)), ("Attunement","+6% damage", p=>A(p,0.06f)), ("Sigil Ward","+30 HP, +4% resist", p=>{HP(p,30f); Res(p,0.04f);}), ("Focus","+30 max health", p=>HP(p,30f)),
        ("Singular","+6% damage", p=>A(p,0.06f)), ("Feedback Loop","+crit-heal", p=>p.ArcaneCritHealBonus+=0.04f), ("Blink Step","+move speed", p=>Sp(p,0.4f)), ("Shieldguard","+4% resistance", p=>Res(p,0.04f)),
        ("Missile","+5% damage", p=>A(p,0.05f)), ("Wardplate","+30 max health", p=>HP(p,30f)), ("Amplifier","+12% crit damage", p=>CritD(p,0.12f)), ("Runeguard","+5% resistance", p=>Res(p,0.05f)),
        ("Barrage ★","+15% damage, +10% proj speed", p=>{A(p,0.15f); p.S.ProjSpeed+=0.1f;}), ("Conduit Sovereign ★","+13% damage, +10% spell range", p=>{A(p,0.13f); p.S.SpellRange+=0.1f;}),
        ("Ascendant Mind ★","+15% damage, +10% crit, +40% crit damage", p=>{A(p,0.15f); Crit(p,0.1f); CritD(p,0.4f);}), ("Warpguard ★","+12% resistance, +60 health", p=>{Res(p,0.12f); HP(p,60f);}),
    };
    private static HiddenRoute[] ArcaneRoutes() => new[]{
        new HiddenRoute { Name="Spellblade", Desc="hidden — +12% crit, +30% crit damage, +crit-heal", Req=R5, Apply=p=>{Crit(p,0.12f); CritD(p,0.3f); p.ArcaneCritHealBonus+=0.06f;} },
        new HiddenRoute { Name="Archmage", Desc="hidden — +15% damage, +10% range, +40 health, +proj speed", Req=R9, Apply=p=>{A(p,0.15f); p.S.SpellRange+=0.1f; HP(p,40f); p.S.ProjSpeed+=0.1f;} },
        new HiddenRoute { Name="Singularity", Desc="hidden — +18% damage, +12% crit, +50% crit damage, +crit-heal", Req=R13, Apply=p=>{A(p,0.18f); Crit(p,0.12f); CritD(p,0.5f); p.ArcaneCritHealBonus+=0.08f;} },
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
