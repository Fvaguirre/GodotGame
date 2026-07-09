using Godot;
using System.Collections.Generic;
using System.Linq;

// PerkTree.cs — the persistent, meta-progression PERK system (outside a run). Each witch has a 9-node tree: 3 lanes
// (LEFT playstyle / MIDDLE shared / RIGHT playstyle) × 3 tiers, each lane 2 minors → 1 major. You BUY perks with gold
// (permanent), then EQUIP up to a cap for a run. Owned+equipped sets persist in the save file. Effects apply at run start.
//
// Topology (index 0-8 = L1 L2 L3 | M1 M2 M3 | R1 R2 R3). "Supports" = nodes below that hold this one up; you need ≥1
// support EQUIPPED to equip a node (entries need none). Sides are single chains; the middle bridges both sides with 2
// supports each. Unequipping cascades: any equipped node that loses ALL its supports unequips too.
public class Perk
{
    public int Idx;              // 0..8 within its witch's tree
    public string Name, Desc;
    public int Lane;             // 0 left, 1 middle, 2 right
    public int Tier;             // 1,2,3 (3 = major)
    public bool Major;
    public int Cost;
    public int[] Supports;       // indices of nodes below (OR: need ≥1 equipped)
    public System.Action<Player> Apply;
}

public static class Perks
{
    public const int Cap = 6;        // equip at most 6 of 9 (~⅔) — you can't run everything
    public const int MaxMajors = 2;  // and at most 2 of the 3 majors
    public const int WitchCount = 8;

    // fixed support graph, shared by every witch's tree
    private static readonly int[][] SUP = {
        new int[0],       // 0 L1 (entry)
        new[]{0},         // 1 L2 ← L1
        new[]{1},         // 2 L3 ← L2  (major)
        new[]{0,6},       // 3 M1 ← L1 or R1  (bridge)
        new[]{1,7},       // 4 M2 ← L2 or R2  (bridge)
        new[]{3,4},       // 5 M3 ← M1 or M2  (major, bridge)
        new int[0],       // 6 R1 (entry)
        new[]{6},         // 7 R2 ← R1
        new[]{7},         // 8 R3 ← R2  (major)
    };
    private static readonly int[] TIER_COST = { 150, 400, 850 };

    private static Perk[][] _trees;
    private static readonly HashSet<int>[] _owned = new HashSet<int>[WitchCount];
    private static readonly HashSet<int>[] _equipped = new HashSet<int>[WitchCount];

    static Perks() { for (int i = 0; i < WitchCount; i++) { _owned[i] = new HashSet<int>(); _equipped[i] = new HashSet<int>(); } }

    public static Perk[] Tree(int witch) { Build(); return _trees[Mathf.Clamp(witch, 0, WitchCount - 1)]; }
    public static string[] LaneNames(int witch) => witch switch
    {
        1 => new[]{ "Radiance", "Faith", "Guardian" },
        2 => new[]{ "Bloodthirst", "Sanguine", "Frenzy" },
        3 => new[]{ "Grove", "Warden", "Blight" },
        4 => new[]{ "Tempest", "Skyborne", "Cyclone" },
        5 => new[]{ "Deep Freeze", "Winter", "Shatter" },
        6 => new[]{ "Hexweaver", "Malediction", "Soulrend" },
        7 => new[]{ "Pyre", "Ember", "Cataclysm" },
        _ => new[]{ "Crescent", "Moonlight", "Nightfall" },
    };

    // ---- state queries ----
    public static bool Owned(int w, int i) => _owned[w].Contains(i);
    public static bool Equipped(int w, int i) => _equipped[w].Contains(i);
    public static int EquippedCount(int w) => _equipped[w].Count;
    public static int MajorCount(int w) { Build(); return _equipped[w].Count(i => _trees[w][i].Major); }

    // can BUY: not owned, and a support is OWNED (you clear the tier below first), and enough gold
    public static bool PrereqOwned(int w, int i) { Build(); var s = _trees[w][i].Supports; return s.Length == 0 || s.Any(x => Owned(w, x)); }
    public static bool CanBuy(int w, int i, int gold) { Build(); return !Owned(w, i) && PrereqOwned(w, i) && gold >= _trees[w][i].Cost; }
    // can EQUIP: owned, a support is EQUIPPED, room left, and the major limit
    public static bool CanEquip(int w, int i)
    {
        Build();
        if (!Owned(w, i) || Equipped(w, i) || EquippedCount(w) >= Cap) return false;
        var p = _trees[w][i];
        if (p.Major && MajorCount(w) >= MaxMajors) return false;
        return p.Supports.Length == 0 || p.Supports.Any(x => Equipped(w, x));
    }

    // ---- mutations ----
    public static bool Buy(int w, int i)   // spends gold on Game.I; returns success
    {
        var g = Game.I; if (g == null) return false;
        if (!CanBuy(w, i, g.Gold)) return false;
        g.Gold -= _trees[w][i].Cost; _owned[w].Add(i);
        g.SavePerks();
        return true;
    }
    public static bool Equip(int w, int i)
    {
        if (!CanEquip(w, i)) return false;
        _equipped[w].Add(i); Game.I?.SavePerks(); return true;
    }
    public static void Unequip(int w, int i)
    {
        Build();
        if (!_equipped[w].Remove(i)) return;
        bool changed = true;                                   // cascade: drop any node that just lost ALL its supports
        while (changed)
        {
            changed = false;
            foreach (var p in _trees[w])
            {
                if (!Equipped(w, p.Idx) || p.Supports.Length == 0) continue;
                if (!p.Supports.Any(x => Equipped(w, x))) { _equipped[w].Remove(p.Idx); changed = true; }
            }
        }
        Game.I?.SavePerks();
    }

    public static void ApplyEquipped(Player pl, int witch)
    {
        Build(); if (pl == null) return;
        foreach (int i in _equipped[Mathf.Clamp(witch, 0, WitchCount - 1)]) _trees[witch][i].Apply?.Invoke(pl);
    }

    // ---- persistence (called from Game save/load, section [perks]) ----
    public static void Save(ConfigFile cfg)
    {
        for (int w = 0; w < WitchCount; w++)
        {
            cfg.SetValue("perks", $"owned{w}", string.Join(",", _owned[w].OrderBy(x => x)));
            cfg.SetValue("perks", $"equip{w}", string.Join(",", _equipped[w].OrderBy(x => x)));
        }
    }
    public static void Load(ConfigFile cfg)
    {
        for (int w = 0; w < WitchCount; w++)
        {
            _owned[w].Clear(); _equipped[w].Clear();
            ParseInto(_owned[w], cfg.GetValue("perks", $"owned{w}", "").AsString());
            ParseInto(_equipped[w], cfg.GetValue("perks", $"equip{w}", "").AsString());
            _equipped[w].IntersectWith(_owned[w]);   // safety: never keep an equipped-but-not-owned id
        }
    }
    private static void ParseInto(HashSet<int> set, string s)
    {
        if (string.IsNullOrEmpty(s)) return;
        foreach (var part in s.Split(',')) if (int.TryParse(part, out int v) && v >= 0 && v < 9) set.Add(v);
    }

    // ---- the trees (built once) ----
    private static void Build()
    {
        if (_trees != null) return;
        _trees = new Perk[WitchCount][];

        // LUNAR (0) — Crescent (crit/orbs) / Moonlight (power+ult) / Nightfall (night+ult)
        _trees[0] = Make(
            ("Keen Edge", "+7% crit chance", p => p.S.CritChance = Mathf.Min(1f, p.S.CritChance + 0.07f)),
            ("Silver Point", "+25% crit damage", p => p.S.CritDamage += 0.25f),
            ("Full Moon", "+1 crescent pierce, +30% crescent size, +8% crit", p => { p.CrescentPierceBonus++; p.CrescentSizeMul = Mathf.Min(2.6f, p.CrescentSizeMul + 0.3f); p.S.CritChance = Mathf.Min(1f, p.S.CritChance + 0.08f); }),
            ("Moonlit", "+6% damage", p => p.S.Atk += 0.06f),
            ("Tidal Pull", "+0.5 max mana, +8% spell area", p => { p.S.ManaMax += 0.5f; p.S.SpellArea += 0.08f; }),
            ("Lunar Ascendant", "+12% damage, +20% ult charge", p => { p.S.Atk += 0.12f; p.UltChargeMul = Mathf.Min(2.5f, p.UltChargeMul + 0.2f); }),
            ("Duskbound", "+8% Lunar damage (doubled at night)", p => p.LunarBonus += 0.08f),
            ("Eventide", "+15% ult charge", p => p.UltChargeMul = Mathf.Min(2.5f, p.UltChargeMul + 0.15f)),
            ("Eclipse Heart", "+30% ult charge, +8% damage", p => { p.UltChargeMul = Mathf.Min(2.5f, p.UltChargeMul + 0.3f); p.S.Atk += 0.08f; }));

        // DIVINE (1) — Radiance (offense) / Faith (durability+power) / Guardian (support)
        _trees[1] = Make(
            ("Sunfire", "+7% damage", p => p.S.Atk += 0.07f),
            ("Piercing Light", "+1 pierce", p => p.S.Pierce += 1),
            ("Zealot", "+12% damage, +10% crit, +25% crit damage", p => { p.S.Atk += 0.12f; p.S.CritChance = Mathf.Min(1f, p.S.CritChance + 0.1f); p.S.CritDamage += 0.25f; }),
            ("Devout", "+8% damage resistance", p => p.S.DmgResist = Mathf.Min(0.8f, p.S.DmgResist + 0.08f)),
            ("Sanctified", "+50 max health", p => p.S.MaxHp += 50f),
            ("Ordained", "+100 max health, +12% resist, +1 Intervention", p => { p.S.MaxHp += 100f; p.S.DmgResist = Mathf.Min(0.8f, p.S.DmgResist + 0.12f); p.Interventions++; }),
            ("Warding", "+25% shield capacity", p => p.S.ShieldPct += 0.05f),
            ("Benevolence", "+1 Divine Intervention", p => p.Interventions++),
            ("Seraph", "+2 Interventions, +15% resist", p => { p.Interventions += 2; p.S.DmgResist = Mathf.Min(0.8f, p.S.DmgResist + 0.15f); }));

        // CRIMSON (2) — Bloodthirst (lifesteal) / Sanguine (power) / Frenzy (crit)
        _trees[2] = Make(
            ("Leech", "+6% lifesteal", p => p.S.Lifesteal += 0.06f),
            ("Vital Feast", "+40 health, +4% lifesteal", p => { p.S.MaxHp += 40f; p.S.Lifesteal += 0.04f; }),
            ("Vampiric", "+12% lifesteal, +50 health", p => { p.S.Lifesteal += 0.12f; p.S.MaxHp += 50f; }),
            ("Blooded", "+7% damage", p => p.S.Atk += 0.07f),
            ("Crimson Might", "+10% damage, +6% spell area", p => { p.S.Atk += 0.1f; p.S.SpellArea += 0.06f; }),
            ("Bloodlord", "+15% damage, +8% lifesteal", p => { p.S.Atk += 0.15f; p.S.Lifesteal += 0.08f; }),
            ("Reckless", "+8% crit chance", p => p.S.CritChance = Mathf.Min(1f, p.S.CritChance + 0.08f)),
            ("Savagery", "+40% crit damage", p => p.S.CritDamage += 0.4f),
            ("Berserker", "+12% crit, +60% crit damage, +8% damage", p => { p.S.CritChance = Mathf.Min(1f, p.S.CritChance + 0.12f); p.S.CritDamage += 0.6f; p.S.Atk += 0.08f; }));

        // VERDANT (3) — Grove (ents) / Warden (durability+power) / Blight (poison/damage)
        _trees[3] = Make(
            ("Sapling", "tree-ents grow faster", p => p.GroveEvery = Mathf.Max(6, p.GroveEvery - 2)),
            ("Deep Roots", "+1 max tree-ent", p => p.GroveBonusEnts = Mathf.Min(6, p.GroveBonusEnts + 1)),
            ("Elder Grove", "+2 max tree-ents, faster growth", p => { p.GroveBonusEnts = Mathf.Min(6, p.GroveBonusEnts + 2); p.GroveEvery = Mathf.Max(6, p.GroveEvery - 2); }),
            ("Barkskin", "+8% damage resistance", p => p.S.DmgResist = Mathf.Min(0.8f, p.S.DmgResist + 0.08f)),
            ("Heartwood", "+60 max health", p => p.S.MaxHp += 60f),
            ("Ancient", "+100 health, +12% resist, +8% damage", p => { p.S.MaxHp += 100f; p.S.DmgResist = Mathf.Min(0.8f, p.S.DmgResist + 0.12f); p.S.Atk += 0.08f; }),
            ("Blighttouched", "+8% damage", p => p.S.Atk += 0.08f),
            ("Creeping Death", "+10% spell area, +6% damage", p => { p.S.SpellArea += 0.1f; p.S.Atk += 0.06f; }),
            ("Plaguebringer", "+15% damage, +12% spell area", p => { p.S.Atk += 0.15f; p.S.SpellArea += 0.12f; }));

        // GALE (4) — Tempest (mobility) / Skyborne (airborne+power) / Cyclone (control)
        _trees[4] = Make(
            ("Fleet", "+move speed", p => p.S.Speed = Mathf.Min(18f, p.S.Speed + 0.6f)),
            ("Slipwind", "+1 dash charge", p => p.S.DashCharges++),
            ("Windwalker", "+1 dash, faster dash cooldown, +8% damage", p => { p.S.DashCharges++; p.S.DashCd = Mathf.Max(0.9f, p.S.DashCd * 0.8f); p.S.Atk += 0.08f; }),
            ("Gale Force", "+7% damage", p => p.S.Atk += 0.07f),
            ("Updraft", "+8% jump height, +6% spell area", p => { p.S.JumpMul += 0.08f; p.S.SpellArea += 0.06f; }),
            ("Stormheart", "+14% damage, +10% crit", p => { p.S.Atk += 0.14f; p.S.CritChance = Mathf.Min(1f, p.S.CritChance + 0.1f); }),
            ("Buffet", "+stronger gusts/knockback", p => p.GustPower = Mathf.Min(2.5f, p.GustPower + 0.2f)),
            ("Whirl", "+10% spell area, +8% damage", p => { p.S.SpellArea += 0.1f; p.S.Atk += 0.08f; }),
            ("Tempest Lord", "+15% damage, +12% area, +stronger gusts", p => { p.S.Atk += 0.15f; p.S.SpellArea += 0.12f; p.GustPower = Mathf.Min(2.5f, p.GustPower + 0.3f); }));

        // FROST (5) — Deep Freeze (control) / Winter (power) / Shatter (burst)
        _trees[5] = Make(
            ("Rime", "+freeze buildup", p => p.FreezeRate += 0.3f),
            ("Permafrost", "+0.6s frozen duration", p => p.FrostDurBonus += 0.6f),
            ("Absolute Zero", "+freeze buildup & duration, foes freeze sooner", p => { p.FreezeRate += 0.5f; p.FrostDurBonus += 0.8f; p.FreezeThreshMul = Mathf.Max(0.3f, p.FreezeThreshMul * 0.9f); }),
            ("Frostbite", "+7% damage", p => p.S.Atk += 0.07f),
            ("Cold Snap", "+8% crit, +6% spell area", p => { p.S.CritChance = Mathf.Min(1f, p.S.CritChance + 0.08f); p.S.SpellArea += 0.06f; }),
            ("Winter's Wrath", "+14% damage, +10% crit", p => { p.S.Atk += 0.14f; p.S.CritChance = Mathf.Min(1f, p.S.CritChance + 0.1f); }),
            ("Fracture", "+15% shatter damage", p => p.ShatterPowerMul += 0.15f),
            ("Splinter", "+shatter seeds more freeze", p => p.ShatterFreezeStacks += 0.5f),
            ("Cataclysm", "+40% shatter damage, +8% damage", p => { p.ShatterPowerMul += 0.4f; p.S.Atk += 0.08f; }));

        // FORSAKEN (6) — Hexweaver (tethers/spread) / Malediction (power/curse) / Soulrend (drain)
        _trees[6] = Make(
            ("Wasting", "+curse buildup", p => p.CurseRate += 0.6f),
            ("Binding", "+1 tether link, +curse-spread range", p => { p.MaxLinks = Mathf.Min(12, p.MaxLinks + 1); p.CurseSpreadRange += 2f; }),
            ("Puppetmaster", "+curse buildup, +2 links, +damage sharing", p => { p.CurseRate += 0.6f; p.MaxLinks = Mathf.Min(12, p.MaxLinks + 2); p.CurseShareFrac = Mathf.Min(1f, p.CurseShareFrac + 0.1f); }),
            ("Cursed", "+7% damage", p => p.S.Atk += 0.07f),
            ("Virulent", "+12% bonus damage to cursed foes", p => p.CurseBonusMul += 0.12f),
            ("Doombringer", "+14% damage, +2 crush ceiling", p => { p.S.Atk += 0.14f; p.CurseStackCap += 2f; }),
            ("Siphon", "+beam lifesteal", p => p.CurseBeamLifesteal = Mathf.Min(1f, p.CurseBeamLifesteal + 0.1f)),
            ("Drain", "+8% lifesteal, +6% damage", p => { p.S.Lifesteal += 0.08f; p.S.Atk += 0.06f; }),
            ("Souleater", "+15% beam lifesteal, +10% damage", p => { p.CurseBeamLifesteal = Mathf.Min(1f, p.CurseBeamLifesteal + 0.15f); p.S.Atk += 0.1f; }));

        // EMBER (7) — Pyre (burn/sustained) / Ember (power) / Cataclysm (meteor/burst). Effects are stat-based (boost Base → burn & bombs scale too).
        _trees[7] = Make(
            ("Kindling", "+7% damage (burn scales with it)", p => p.S.Atk += 0.07f),
            ("Slow Burn", "+10% spell area (wider flame cone)", p => p.S.SpellArea += 0.1f),
            ("Inferno", "+15% damage, +10% spell area", p => { p.S.Atk += 0.15f; p.S.SpellArea += 0.1f; }),
            ("Smolder", "+6% damage", p => p.S.Atk += 0.06f),
            ("Heat Haze", "+8% crit, +40 health", p => { p.S.CritChance = Mathf.Min(1f, p.S.CritChance + 0.08f); p.S.MaxHp += 40f; }),
            ("Wildfire", "+12% damage, +10% crit", p => { p.S.Atk += 0.12f; p.S.CritChance = Mathf.Min(1f, p.S.CritChance + 0.1f); }),
            ("Spark", "+8% crit chance", p => p.S.CritChance = Mathf.Min(1f, p.S.CritChance + 0.08f)),
            ("Detonator", "+30% crit damage", p => p.S.CritDamage += 0.3f),
            ("Meteoric", "+15% damage, +12% area, +40% crit damage", p => { p.S.Atk += 0.15f; p.S.SpellArea += 0.12f; p.S.CritDamage += 0.4f; }));
    }

    private static Perk[] Make(params (string name, string desc, System.Action<Player> apply)[] defs)
    {
        var arr = new Perk[9];
        for (int i = 0; i < 9; i++)
            arr[i] = new Perk { Idx = i, Name = defs[i].name, Desc = defs[i].desc, Lane = i / 3, Tier = i % 3 + 1, Major = i % 3 == 2, Cost = TIER_COST[i % 3], Supports = SUP[i], Apply = defs[i].apply };
        return arr;
    }
}
