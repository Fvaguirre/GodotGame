using Godot;
using System;
using System.Collections.Generic;

// Upgrade.cs — the level-up CARD POOL (UpgradePool). A card is an UpgradeCard {Title, Desc, Rarity,
// Apply, FinKind?, ModKind?, ...}. The pool is a list of UpgradeDef {Rars, Make}; Make(rarity, mag)
// builds the card. Helpers: Card() = plain stat card (mutates Player via Apply); FinCard() = grants a
// finisher (FinType); ModCard() = grants a charged-cast modifier (ModType). Ult-specific boon cards
// are generated from a switch(Player.Ult).
//
// Rarity weights + Luck biasing (toward Epic/Legendary) live in the roll functions. To add a card,
// append a Def(<rarities>, (r,m)=>...) entry. See DEV_GUIDE.md §5.5 and §6.3.
public enum Rarity { Common, Uncommon, Rare, Epic, Legendary }

public static class Rarities
{
    public static readonly Rarity[] Order = { Rarity.Common, Rarity.Uncommon, Rarity.Rare, Rarity.Epic, Rarity.Legendary };
    // base weights — rares are now genuinely rare; Luck pulls the high end back up
    public static int Weight(Rarity r) => r switch { Rarity.Common => 60, Rarity.Uncommon => 27, Rarity.Rare => 9, Rarity.Epic => 3, Rarity.Legendary => 1, _ => 1 };
    // per-tier luck multiplier: luck barely touches Common, strongly favors Epic/Legendary
    public static float LuckMult(Rarity r, float luck) => r switch {
        Rarity.Uncommon => 1f + luck * 0.30f,
        Rarity.Rare     => 1f + luck * 0.80f,
        Rarity.Epic     => 1f + luck * 1.60f,
        Rarity.Legendary=> 1f + luck * 2.60f,
        _ => 1f };
    public static float Mag(Rarity r) => r switch { Rarity.Common => 1f, Rarity.Uncommon => 1.7f, Rarity.Rare => 2.5f, Rarity.Epic => 3.8f, Rarity.Legendary => 5.5f, _ => 1f };
    public static Color Col(Rarity r) => r switch {
        Rarity.Common => new Color(0.74f, 0.71f, 0.91f), Rarity.Uncommon => new Color(0.37f, 0.89f, 0.60f),
        Rarity.Rare => new Color(0.50f, 0.82f, 1f), Rarity.Epic => new Color(0.725f, 0.553f, 1f),
        Rarity.Legendary => new Color(1f, 0.81f, 0.42f), _ => Colors.White };
    public static string Name(Rarity r) => r.ToString();
    public static Rarity Roll(RandomNumberGenerator rng, float luck = 0f)
    {
        float tot = 0f; foreach (var r in Order) tot += Weight(r) * LuckMult(r, luck);
        float x = rng.Randf() * tot;
        foreach (var r in Order) { x -= Weight(r) * LuckMult(r, luck); if (x < 0) return r; }
        return Rarity.Common;
    }
}

public class UpgradeCard
{
    public string Title; public string Desc; public Rarity Rarity; public Action<Player> Apply;
    public FinType? FinKind; public int FinEvery; public float FinPow;
    public ModType? ModKind; public float ModMag;
    public int AttuneSlot = -1;   // 0 = primary (left-click), 1 = secondary (charged), -1 = none
    public bool Hidden = false;   // true = not valid for the current witch; roll loops skip it
    public bool Affinity = false; // true = one of the current witch's own signature cards (gets a roll boost)
    public bool Unique = false;   // (NEW) single-rarity card (e.g. a one-off legendary) — cannot be banned
}
public class UpgradeDef { public Rarity[] Rars; public Func<Rarity, float, UpgradeCard> Make; }

public static class UpgradePool
{
    private static List<UpgradeDef> _defs;
    public static readonly System.Collections.Generic.HashSet<string> Banned = new();   // (NEW) card Titles disabled for this run (whole rarity hierarchy)
    // roulette: full pool (anything — stats, combos, modifiers, ult-mods), with a boosted legendary chance
    public static List<UpgradeCard> RollRoulette(Player p, RandomNumberGenerator rng, float legChance)
    {
        if (_defs == null) Build();
        float luck = p?.S?.Luck ?? 0f;
        var outl = new List<UpgradeCard>();
        int guard = 0;
        while (outl.Count < 3 && guard++ < 220)
        {
            var r = Rarities.Roll(rng, luck);
            var pool = _defs.FindAll(d => System.Array.IndexOf(d.Rars, r) >= 0);
            if (pool.Count == 0) continue;
            var d = pool[rng.RandiRange(0, pool.Count - 1)];
            var card = d.Make(r, Rarities.Mag(r));
            if (card.Hidden) continue;
            if (card.AttuneSlot >= 0) continue;             // skip attunes (need the element chooser)
            if (outl.Exists(c => c.Title == card.Title)) continue;
            outl.Add(card);
        }
        if (rng.Randf() < legChance)
        {
            var legPool = _defs.FindAll(d => System.Array.IndexOf(d.Rars, Rarity.Legendary) >= 0);
            if (legPool.Count > 0)
            {
                var d = legPool[rng.RandiRange(0, legPool.Count - 1)];
                var card = d.Make(Rarity.Legendary, Rarities.Mag(Rarity.Legendary));
                if (card.AttuneSlot < 0 && outl.Count > 0) outl[0] = card;
            }
        }
        if (p.Ult != Player.UltKind.None && rng.Randf() < 0.15f)
        {
            var mod = UltModCard(p);
            if (mod != null && outl.Count > 0) outl[rng.RandiRange(0, outl.Count - 1)] = mod;
        }
        return outl;
    }

    // a single legendary upgrade (used for the Divine Witch's guaranteed-legendary-every-10-levels)
    public static UpgradeCard RollOneLegendary(Player p, RandomNumberGenerator rng)
    {
        if (_defs == null) Build();
        var legPool = _defs.FindAll(d => System.Array.IndexOf(d.Rars, Rarity.Legendary) >= 0);
        int guard = 0;
        while (legPool.Count > 0 && guard++ < 60)
        {
            var d = legPool[rng.RandiRange(0, legPool.Count - 1)];
            var card = d.Make(Rarity.Legendary, Rarities.Mag(Rarity.Legendary));
            if (card.AttuneSlot >= 0) continue;   // attunes need the element chooser
            return card;
        }
        return null;
    }

    // a legendary ultimate-modification, specific to the equipped ultimate (null if none available)
    public static UpgradeCard UltModCard(Player p)
    {
        switch (p.Ult)
        {
            case Player.UltKind.Eclipse:
                if (!p.ModEclipse) return Card(Rarity.Legendary, "Blood Moon", "eclipse also lifesteals & slows on hit", x => x.ModEclipse = true);
                break;
            case Player.UltKind.LunarLight:
                if (!p.ModLight) return Card(Rarity.Legendary, "Radiant Font", "lunar light is larger & heals more", x => x.ModLight = true);
                break;
            case Player.UltKind.Crescent:
                if (!p.ModCrescent) return Card(Rarity.Legendary, "Waxing Horde", "+2 crescents; fired blades pierce", x => x.ModCrescent = true);
                break;
            case Player.UltKind.FaithShield:
                if (!p.ModShield) return Card(Rarity.Legendary, "Aegis Sanctum", "shield is sturdier, sears harder & heals you inside", x => x.ModShield = true);
                break;
            case Player.UltKind.Judgement:
                if (!p.ModJudge) return Card(Rarity.Legendary, "Final Verdict", "Judgement becomes ONE colossal lance — devastating core, pulsing holy field for 5s", x => x.ModJudge = true);
                break;
            case Player.UltKind.Divinity:
                if (!p.ModDivinity) return Card(Rarity.Legendary, "Ascendant", "divinity lasts longer; motes hit harder & leave holy ground", x => x.ModDivinity = true);
                break;
            case Player.UltKind.BloodTsunami:
                if (!p.ModTsunami) return Card(Rarity.Legendary, "Crimson Deluge", "the wave is wider and hits much harder", x => x.ModTsunami = true);
                break;
            case Player.UltKind.Exsanguinate:
                if (!p.ModExsang) return Card(Rarity.Legendary, "Bloodthirst", "executes a much larger slice of the wounded", x => x.ModExsang = true);
                break;
            case Player.UltKind.BloodRot:
                if (!p.ModRot) return Card(Rarity.Legendary, "Plague Bloom", "rot bursts are larger and spread further", x => x.ModRot = true);
                break;
            case Player.UltKind.GroveGuardian:
                if (!p.ModGuardian) return Card(Rarity.Legendary, "Heartwood", "the Guardian slams more often over a wider area, rooting & poisoning each stomp", x => x.ModGuardian = true);
                break;
            case Player.UltKind.WildSwarm:
                if (!p.ModSwarm) return Card(Rarity.Legendary, "Teeming Grove", "the stampede is wider, more numerous, and charges for longer", x => x.ModSwarm = true);
                break;
            case Player.UltKind.Barkskin:
                if (!p.ModBark) return Card(Rarity.Legendary, "Ironheart", "Barkskin lasts longer, bursts wider with more spikes, and leaves a creeping poison field", x => x.ModBark = true);
                break;
            case Player.UltKind.Cyclone:   // (NEW)
                if (!p.ModCyclone) return Card(Rarity.Legendary, "Maelstrom", "the cyclone is bigger, lasts longer, and pulls foes in harder", x => x.ModCyclone = true);
                break;
            case Player.UltKind.Hurricane:   // (NEW)
                if (!p.ModHurricane) return Card(Rarity.Legendary, "Eyewall", "the hurricane lasts longer and allies caught in it gain cast/charge/move speed", x => x.ModHurricane = true);
                break;
            case Player.UltKind.Stormform:   // (NEW)
                if (!p.ModStorm) return Card(Rarity.Legendary, "Eye of the Storm", "while Stormform is up, moving leaves air-mines that launch foes skyward (impact + fall damage)", x => x.ModStorm = true);
                break;
            case Player.UltKind.Blizzard:   // (NEW)
                if (!p.ModBlizzard) return Card(Rarity.Legendary, "Whiteout", "the blizzard is bigger, hits harder, and its icicles always freeze what they strike", x => x.ModBlizzard = true);
                break;
            case Player.UltKind.FrostElemental:   // (NEW)
                if (!p.ModFrostElem) return Card(Rarity.Legendary, "Avalanche", "the elemental is larger and, when it melts, splits into two smaller ones that keep rolling", x => x.ModFrostElem = true);
                break;
            case Player.UltKind.DeepFreeze:   // (NEW)
                if (!p.ModDeepFreeze) return Card(Rarity.Legendary, "Absolute Zero", "Deep Freeze lasts longer, and any foe that dies inside it shatters, chaining frost to its neighbours", x => x.ModDeepFreeze = true);
                break;
        }
        return null;
    }

    private static UpgradeCard Card(Rarity r, string t, string d, Action<Player> a) => new UpgradeCard { Rarity = r, Title = t, Desc = d, Apply = a };
    private static bool Bw() => Game.I?.Player?.CrimsonWitch ?? false;   // is the current witch Crimson (the only one with Blood Stacks)?
    // a stat card that is one of the active witch's SIGNATURE cards — tagged so the affinity roll surfaces it more often
    private static UpgradeCard WitchCard(Rarity r, string t, string d, Action<Player> a) => new UpgradeCard { Rarity = r, Title = t, Desc = d, Apply = a, Affinity = true };
    private static UpgradeCard FinCard(Rarity r, FinType t, int every, float pow, string body) => new UpgradeCard { Rarity = r, Title = FinMeta.Name(t), Desc = FinDesc(t, every, body), FinKind = t, FinEvery = every, FinPow = pow };
    private static UpgradeCard ModCard(Rarity r, ModType t, float mag, string body) => new UpgradeCard { Rarity = r, Title = ModMeta.Name(t), Desc = ModDesc(t, body), ModKind = t, ModMag = mag };
    private static UpgradeCard MinorCard(Rarity r, MinorType t)
    {
        string body = MinorMeta.Desc(t);
        if (t == MinorType.Clot && !Bw()) body = "periodic tiny blood burst; mends a little";   // non-Crimson heals instead of banking
        return new UpgradeCard { Rarity = r, Title = MinorMeta.Name(t), Desc = $"MINOR · stacks \u00b7 auto every {MinorMeta.Every(t)} combos \u2014 {body}", Apply = p => p.AddMinor(t) };
    }
    private static UpgradeCard AttuneCard(Rarity r, int slot, string t, string d) => new UpgradeCard { Rarity = r, Title = t, Desc = d, AttuneSlot = slot };
    private static UpgradeDef Def(Rarity[] rars, Func<Rarity, float, UpgradeCard> make) => new UpgradeDef { Rars = rars, Make = make };

    private static readonly Rarity[] AllR = { Rarity.Common, Rarity.Uncommon, Rarity.Rare, Rarity.Epic, Rarity.Legendary };
    private static readonly Rarity[] UncP = { Rarity.Uncommon, Rarity.Rare, Rarity.Epic, Rarity.Legendary };
    private static readonly Rarity[] RareP = { Rarity.Rare, Rarity.Epic, Rarity.Legendary };
    private static readonly Rarity[] EpicP = { Rarity.Epic, Rarity.Legendary };
    private static readonly Rarity[] LegP = { Rarity.Legendary };
    private static readonly Rarity[] ComUnc = { Rarity.Common, Rarity.Uncommon };
    private static readonly Rarity[] ComUncRare = { Rarity.Common, Rarity.Uncommon, Rarity.Rare };
    private static readonly Rarity[] UncRare = { Rarity.Uncommon, Rarity.Rare };
    private static readonly Rarity[] UncRareEpic = { Rarity.Uncommon, Rarity.Rare, Rarity.Epic };
    private static readonly Rarity[] ComEpic = { Rarity.Common, Rarity.Uncommon, Rarity.Rare, Rarity.Epic };

    private static string FinDesc(FinType t, int every, string body) => $"every {every} combo · {body} · {DamageTypes.Name(FinMeta.DType(t))}";
    private static string ModDesc(ModType t, string body) => $"full charge: {body} · {DamageTypes.Name(ModMeta.DType(t))}";

    private static void Build()
    {
        _defs = new List<UpgradeDef>
        {
            // ---- core stats ----
            Def(AllR,  (r,m)=>Card(r,"Witchfire",       $"+{Mathf.RoundToInt(6*m)}% spell damage",        p=>p.S.Atk *= 1+0.06f*m)),
            Def(AllR,  (r,m)=>Card(r,"Quicksilver",     $"+{Mathf.RoundToInt(3*m)}% movement speed",      p=>p.S.Speed = Mathf.Min(16.5f, p.S.Speed*(1+0.03f*m)))),
            Def(AllR,  (r,m)=>Card(r,"Hex Tempo",       $"{Mathf.RoundToInt(4.5f*m)}% faster casting",       p=>p.S.FireCd *= 1-Mathf.Min(0.45f,0.045f*m))),
            Def(AllR,  (r,m)=>Card(r,"Swift Conjury",   $"+{Mathf.RoundToInt(8*m)}% projectile speed",       p=>p.S.ProjSpeed = Mathf.Min(2.4f, p.S.ProjSpeed*(1+0.08f*m)))),
            Def(ComEpic,(r,m)=>Card(r,"Focus",          $"{Mathf.RoundToInt(6*m)}% faster charge",        p=>p.S.ChargeSpeed *= 1+0.06f*m)),
            Def(UncP,  (r,m)=>Card(r,"Overcharge",      $"+{(0.4f*m):0.0}x max charged damage",           p=>p.S.MaxCharge += 0.4f*m)),
            Def(AllR,  (r,m)=>Card(r,"Heartwood",       $"+{Mathf.RoundToInt(10*m)}% max health (+heal)", p=>{p.S.MaxHp *= 1+0.10f*m; p.Hp = Mathf.Min(p.S.MaxHp, p.Hp + p.S.MaxHp*0.12f*m);})),
            Def(RareP, (r,m)=>Card(r,"Old Blood",       $"+{Mathf.RoundToInt(14*m)} raw max health",      p=>{p.S.MaxHp += 14*m; p.Hp += 14*m;})),
            Def(AllR,  (r,m)=>Card(r,"Moonglass Aegis", $"+{Mathf.RoundToInt(8*m)}% shield capacity",     p=>{p.S.ShieldPct += 0.08f*m; p.Shield = p.MaxShield;})),
            Def(AllR,  (r,m)=>Card(r,"Swift Mending",   $"shield recovers {Mathf.RoundToInt(12*m)}% sooner", p=>p.S.ShieldDelay *= 1-Mathf.Min(0.7f,0.12f*m))),
            Def(AllR,  (r,m)=>{ float b=r switch{Rarity.Common=>0.1f,Rarity.Uncommon=>0.3f,Rarity.Rare=>0.5f,Rarity.Epic=>0.7f,_=>1.0f}; return Card(r,"Quickening Ward", $"+{b:0.0} shield regen / sec", p=>p.S.ShieldRegen += b); }),
            Def(AllR,  (r,m)=>{ int add=r switch{Rarity.Common=>1,Rarity.Uncommon=>1,Rarity.Rare=>2,Rarity.Epic=>2,_=>3}; return Card(r,"Wind Step", $"+{add} dash distance", p=>p.S.DashDist = Mathf.Min(16f, p.S.DashDist + add)); }),
            Def(UncP,  (r,m)=>Card(r,"Fleet Step",      $"dash recharges {Mathf.RoundToInt(7*m)}% faster", p=>p.S.DashCd = Mathf.Max(0.9f, p.S.DashCd*(1-Mathf.Min(0.4f,0.07f*m))))),
            Def(LegP,  (r,m)=>Card(r,"Twin Step",       "+1 dash charge (max 3)",                          p=>{ if(p.S.DashCharges<3){p.S.DashCharges++; p.DashStock++;} })),
            Def(AllR,  (r,m)=>{ var pl=Game.I?.Player; if (pl!=null && pl.CrimsonWitch){ float red=r switch{Rarity.Common=>0.004f,Rarity.Uncommon=>0.008f,Rarity.Rare=>0.012f,Rarity.Epic=>0.016f,_=>0.022f}; return WitchCard(r,"Blood Efficiency", $"finishers cost {red*100:0.0}% less health", p=>p.FinHpCost=Mathf.Max(0.04f,p.FinHpCost-red)); } float add=r switch{Rarity.Common=>0.01f,Rarity.Uncommon=>0.03f,Rarity.Rare=>0.05f,Rarity.Epic=>0.07f,_=>0.1f}; return Card(r,"Mana Wellspring", $"+{add:0.00} mana per normal hit", p=>p.S.ManaGain += add); }),
            Def(EpicP, (r,m)=>{ var pl=Game.I?.Player; if (pl!=null && pl.CrimsonWitch) return WitchCard(r,"Blood Reserve","+8% max health", p=>{ p.S.MaxHp*=1.08f; p.Hp=Mathf.Min(p.S.MaxHp,p.Hp+p.S.MaxHp*0.08f); }); return Card(r,"Deep Reserve","+1 max mana (max 5)", p=>{ if(p.S.ManaMax<5){p.S.ManaMax++; p.Mana=Mathf.Min(p.S.ManaMax,p.Mana+1);} }); }),
            Def(UncP,  (r,m)=>Card(r,"Siphon",          $"heal {(0.6f*m):0.0}% of damage dealt",           p=>p.S.Lifesteal += 0.006f*m)),
            Def(RareP, (r,m)=>Card(r,"Piercing Sigil",  $"bolts pierce +{(r==Rarity.Legendary?2:1)}",      p=>p.S.Pierce += (r==Rarity.Legendary?2:1))),
            Def(RareP, (r,m)=>Card(r,"Cadence",         $"+{(2*m):0.0}% dmg per combo & +combo cap",        p=>{p.S.ComboPow += 0.02f*m; p.S.ComboCap += 2 + (r==Rarity.Legendary?2:0);})),
            Def(UncRareEpic,(r,m)=>Card(r,"Witch's Rhythm", $"combo window +{(0.25f*m):0.00}s",             p=>{p.S.ComboWindow += 0.25f*m; p.S.ComboPow += 0.008f*m;})),
            Def(EpicP, (r,m)=>FinCard(r,FinType.Crescendo, r==Rarity.Legendary?4:5, 1f, "every Nth combo cast splits into 3 — passive, holds a spell slot")),
            // ---- finishers (Q/E/F) ----
            Def(ComUnc,     (r,m)=>FinCard(r,FinType.Wave,    r==Rarity.Common?8:6, 0.9f+m*0.22f, "burst a wave around you")),
            Def(ComUncRare, (r,m)=>FinCard(r,FinType.Volley,  r==Rarity.Common?8:(r==Rarity.Uncommon?7:6), 0.9f+m*0.22f, "fire 5+ aimed bolts")),
            Def(UncRare,    (r,m)=>FinCard(r,FinType.Beam,    r==Rarity.Uncommon?7:6, 0.9f+m*0.25f, "channel an aimable beam")),
            Def(UncRareEpic,(r,m)=>FinCard(r,FinType.Swarm,   r==Rarity.Uncommon?7:(r==Rarity.Rare?6:5), 0.9f+m*0.22f, "loose a homing swarm")),
            Def(RareP,      (r,m)=>FinCard(r,FinType.Root,    r==Rarity.Rare?6:5, 0.9f+m*0.22f, "root nearby foes")),
            Def(UncRareEpic,(r,m)=>FinCard(r,FinType.Heal,    r==Rarity.Uncommon?7:(r==Rarity.Rare?6:5), 0.9f+m*0.22f, "drop a healing circle")),
            Def(EpicP,      (r,m)=>FinCard(r,FinType.Fullmod, r==Rarity.Epic?6:5, 0.9f+m*0.22f, "erupt a full-power modded blast")),
            Def(RareP,      (r,m)=>FinCard(r,FinType.HexField,6, 0.9f+m*0.25f, "drop a lingering hex field (~5s)")),
            Def(LegP,       (r,m)=>Card(r,"Coven Bond",  "+1 finisher slot (chain another spell)",          p=>{ if(p.S.FinSlots<5) p.S.FinSlots++; })),
            Def(LegP,       (r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.VerdantWitch) return new UpgradeCard { Rarity=r, Hidden=true }; if (pl.MinionChain) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Wildfire Bloom", "your tree-ents chain-detonate — each ent explosion sets off nearby ents", p=>p.MinionChain=true); }),
            Def(UncRareEpic,(r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.VerdantWitch || pl.GroveBonusEnts >= 4) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Deepening Grove", "+1 max tree-ent (grow a larger Grove)", p=>{ if (p.GroveBonusEnts < 4) p.GroveBonusEnts++; }); }),
            Def(EpicP,      (r,m)=>{ var pl=Game.I?.Player; if (pl==null || pl.MaxArmor >= Player.ArmorCeil) return new UpgradeCard { Rarity=r, Hidden=true }; return Card(r,"Bulwark", "+1 max armor charge (toughen your shared shield pool)", p=>{ if (p.MaxArmor < Player.ArmorCeil) p.MaxArmor++; }); }),
            Def(AllR,       (r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.VerdantWitch) return new UpgradeCard { Rarity=r, Hidden=true }; int cut = 1 + Mathf.RoundToInt(m*0.5f); return WitchCard(r,"Quick Roots", $"tree-ents grow faster (-{cut} combo per ent)", p=>p.GroveEvery = Mathf.Max(6, p.GroveEvery - cut)); }),
            // --- Gale affinity (wind / mobility / knockback) (NEW) ---
            Def(UncRareEpic,(r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.GaleWitch) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Slipstream",$"+{(2f+m):0.0} dash distance & dash recharges {Mathf.RoundToInt(8*m)}% faster", p=>{ p.S.DashDist=Mathf.Min(16f,p.S.DashDist+(2f+m)); p.S.DashCd=Mathf.Max(0.9f,p.S.DashCd*(1-Mathf.Min(0.4f,0.08f*m))); }); }),
            Def(UncRareEpic,(r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.GaleWitch) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Crosswind",$"+{Mathf.RoundToInt(15*m)}% gust knockback & reach", p=>p.GustPower=Mathf.Min(2.5f,p.GustPower+0.15f*m)); }),
            Def(LegP,       (r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.GaleWitch || pl.TempestHeart) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Tempest Heart","full-charge gusts leave a lingering whirlwind that grinds foes in place", p=>p.TempestHeart=true); }),
            Def(LegP,       (r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.GaleWitch || pl.Cloudfeather) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Cloudfeather","steadily mend health while you're airborne", p=>p.Cloudfeather=true); }),   // (NEW)
            Def(LegP,       (r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.GaleWitch || pl.Downburst) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Downburst","landing from a height slams a Wind shockwave that damages & flings foes", p=>p.Downburst=true); }),   // (NEW)
            Def(LegP,       (r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.GaleWitch || pl.Jetstream) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Jetstream","+25% damage while you're airborne", p=>p.Jetstream=true); }),   // (NEW)
            // --- Frost affinity (freeze / frozen / shatter) (NEW) ---
            Def(UncRareEpic,(r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.FrostWitch) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Rime",$"+{(0.3f*m):0.0} freeze stacks/sec from your beam", p=>p.FreezeRate+=0.3f*m); }),
            Def(UncRareEpic,(r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.FrostWitch) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Lingering Frost",$"+{(0.6f*m):0.0}s frozen duration", p=>p.FrostDurBonus+=0.6f*m); }),
            Def(UncRareEpic,(r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.FrostWitch) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Brittle",$"enemies freeze {Mathf.RoundToInt(8*m)}% sooner", p=>p.FreezeThreshMul=Mathf.Max(0.3f,p.FreezeThreshMul*(1-Mathf.Min(0.4f,0.08f*m)))); }),
            Def(UncRareEpic,(r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.FrostWitch) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Shatterpoint",$"+{Mathf.RoundToInt(15*m)}% shatter damage", p=>p.ShatterPowerMul+=0.15f*m); }),
            Def(UncRareEpic,(r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.FrostWitch) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Splinterfrost",$"shatters seed +{(0.5f*m):0.0} more freeze stacks into foes they hit", p=>p.ShatterFreezeStacks+=0.5f*m); }),
            Def(LegP,       (r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.FrostWitch || pl.ShatterCascade) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Shatter Cascade","shattering an ice block detonates every nearby frozen foe too — chain the whole crowd", p=>p.ShatterCascade=true); }),   // (NEW legendary)
            Def(LegP,       (r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.FrostWitch || pl.DeepWinter) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Deep Winter","frozen foes radiate cold, rapidly freezing the enemies around them", p=>p.DeepWinter=true); }),   // (NEW legendary)
            Def(LegP,       (r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.FrostWitch || pl.GlacialImpaler) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Glacial Impaler","your icicle spear pierces every foe in a line and shatters frozen ones at ANY charge", p=>p.GlacialImpaler=true); }),   // (NEW legendary)
            // --- Lunar affinity (moon / night / crescent) ---
            Def(UncRareEpic,(r,m)=>{ var pl=Game.I?.Player; if (pl==null || pl.WitchIndex!=0) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Waxing Crescent","+1 crescent pierce & +20% crescent size", p=>{ p.CrescentPierceBonus++; p.CrescentSizeMul=Mathf.Min(2.4f,p.CrescentSizeMul+0.2f); }); }),
            Def(UncRareEpic,(r,m)=>{ var pl=Game.I?.Player; if (pl==null || pl.WitchIndex!=0) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Nightfall's Gift",$"+{Mathf.RoundToInt(6*m)}% Lunar damage (doubled at night)", p=>p.LunarBonus+=0.06f*m); }),
            Def(LegP,       (r,m)=>{ var pl=Game.I?.Player; if (pl==null || pl.WitchIndex!=0 || pl.UltChargeMul>=2f) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Lunar Eclipse","+25% ultimate charge rate (faster still at night)", p=>p.UltChargeMul=Mathf.Min(2f,p.UltChargeMul+0.25f)); }),
            // --- Divine affinity (holy support / smite) ---
            Def(UncRareEpic,(r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.DivineWitch) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Benediction","+1s blessing duration; you mend a little whenever you bless", p=>p.BlessBonus=Mathf.Min(4f,p.BlessBonus+1f)); }),
            Def(UncRareEpic,(r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.DivineWitch || pl.MoteFork>=2) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Twin Light","your Holy mote forks to a nearby foe on hit", p=>{ if (p.MoteFork<2) p.MoteFork++; }); }),
            Def(LegP,       (r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.DivineWitch || pl.MartyrGrace) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Martyr's Grace","Divine Intervention erupts with light: full shield, heals allies, blasts foes back", p=>p.MartyrGrace=true); }),
            // --- Crimson affinity (blood / glass cannon) ---
            Def(UncRareEpic,(r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.CrimsonWitch || pl.SanguineFrenzy) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Sanguine Frenzy","deal up to +25% damage the lower your health falls", p=>p.SanguineFrenzy=true); }),
            Def(UncRareEpic,(r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.CrimsonWitch) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Crimson Communion","+aura radius; aura kills heal more", p=>{ p.AuraBonusR=Mathf.Min(4f,p.AuraBonusR+1f); p.AuraHealMul=Mathf.Min(2.5f,p.AuraHealMul+0.3f); }); }),
            Def(LegP,       (r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.CrimsonWitch || pl.Hemoclast) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Hemoclast","spending Blood Stacks also erupts a blood nova (scales with stacks spent)", p=>p.Hemoclast=true); }),
            // ---- elemental attunement (re-type the witch's lunar attacks) ----
            Def(EpicP,      (r,m)=>AttuneCard(r, 0, "Primary Attunement",   "retune your left-click to an element of your choosing")),
            Def(EpicP,      (r,m)=>AttuneCard(r, 1, "Secondary Attunement", "retune your charged right-click to an element of your choosing")),
            // ---- right-click charge modifiers (2 slots, 4 max) ----
            Def(UncRareEpic,(r,m)=>ModCard(r,ModType.Frost,    m, $"chill foes in an area ({Mathf.RoundToInt(40+10*m)}% slow)")),
            Def(RareP,      (r,m)=>ModCard(r,ModType.Bramble,  m, $"root foes in the blast {(1.6f+0.3f*m):0.0}s")),
            Def(EpicP,      (r,m)=>ModCard(r,ModType.Sunder,   m, $"erupt a {Mathf.RoundToInt(7+m)}-unit blast")),
            Def(LegP,       (r,m)=>ModCard(r,ModType.Moonbeam, m, "leave a moonbeam burning the area ~6s")),
            Def(ComEpic,    (r,m)=>ModCard(r,ModType.HexMark,  m, $"mark a foe (+{Mathf.RoundToInt((0.25f+0.03f*m)*100)}% dmg taken); leaps on death")),
            Def(UncRareEpic,(r,m)=>ModCard(r,ModType.Consecrate, m, "leave consecrated ground: sears foes & heals you ~5s")),
            Def(RareP,      (r,m)=>ModCard(r,ModType.Smite,      m, "smite the nearest foe with a holy lance + slow")),
            Def(ComEpic,    (r,m)=>ModCard(r,ModType.Hemorrhage, m, "hits bleed; a bleeding foe ruptures on death")),
            Def(UncRareEpic,(r,m)=>ModCard(r,ModType.CrimsonPool, m, Bw() ? "leave a blood pool: slows foes & banks you Blood Stacks" : "leave a blood pool: slows foes & mends you")),
            Def(EpicP,      (r,m)=>ModCard(r,ModType.SanguineSpikes, m, $"erupt blood spikes ({Mathf.RoundToInt(6+m)}-unit); each hit {(Bw() ? "banks blood" : "mends you")}")),
            Def(RareP,      (r,m)=>ModCard(r,ModType.Implosion,  m, $"damage a {Mathf.RoundToInt(8+m)}-unit area, then yank survivors inward")),   // (NEW)
            Def(EpicP,      (r,m)=>ModCard(r,ModType.Whirlwind,  m, "spawn a stationary tornado: grinds foes & launches any player who jumps on it")),   // (NEW)
            // ---- holy finishers ----
            Def(ComUncRare, (r,m)=>FinCard(r,FinType.Halo,  r==Rarity.Common?8:6, 0.9f+m*0.22f, "radiant nova — sears foes, heals you, blesses you")),
            Def(UncRareEpic,(r,m)=>FinCard(r,FinType.Lance, r==Rarity.Uncommon?7:6, 0.9f+m*0.22f, "call a fan of holy lances at the aim")),
            // ---- blood finishers ----
            Def(UncRareEpic,(r,m)=>FinCard(r,FinType.BloodNova,  6, 0.9f+m*0.22f, "ring detonation — strong blood blast + knockback")),
            Def(ComUncRare, (r,m)=>FinCard(r,FinType.CrimsonRush, r==Rarity.Common?8:6, 0.9f+m*0.22f, "dash forward on a blood wave, bowling foes over; each foe struck may return a blood shield (chance rises with rarity)")),
            Def(RareP,      (r,m)=>FinCard(r,FinType.BloodCurse, 6, 0.9f+m*0.22f, Bw() ? "cone of misty blood hexes foes; bank a stack per hex" : "cone of misty blood hexes foes; mends you per hex")),
            // --- Verdant finishers (equippable by ANY witch) ---
            Def(UncRareEpic,(r,m)=>FinCard(r,FinType.PoisonField, r==Rarity.Uncommon?7:6, 0.9f+m*0.22f, "drop a creeping poison field — stacks the longer foes stand in it")),
            Def(ComUncRare, (r,m)=>FinCard(r,FinType.SeedMine, r==Rarity.Common?8:6, 0.9f+m*0.22f, "scatter proximity seed-mines that blast foes who step near")),
            Def(UncRareEpic,(r,m)=>FinCard(r,FinType.ThornSkin, r==Rarity.Uncommon?7:6, 0.9f+m*0.22f, "bank a bark shield (max 3) that eats a hit and bursts")),
            Def(ComUncRare, (r,m)=>FinCard(r,FinType.Updraft,   r==Rarity.Common?8:6, 0.9f+m*0.22f, "launch up & carry small/medium foes aloft — set up air combos")),   // (NEW)
            Def(ComUncRare, (r,m)=>FinCard(r,FinType.WindRush,  r==Rarity.Common?8:6, 0.9f+m*0.22f, "dash forward, fling foes aside; ~50% to refund dashes on a hit")),   // (NEW)
            Def(UncRareEpic,(r,m)=>FinCard(r,FinType.WindSlice, r==Rarity.Uncommon?7:6, 0.9f+m*0.22f, "hurl a travelling X of wind that cuts through foes in its path")),   // (NEW)
            Def(ComUncRare, (r,m)=>FinCard(r,FinType.IceSpike,   r==Rarity.Common?8:6, 0.9f+m*0.22f, "erupt a cone of ice spikes ahead — flings small/medium foes up")),   // (NEW)
            Def(UncRareEpic,(r,m)=>FinCard(r,FinType.FrostVault, r==Rarity.Uncommon?7:6, 0.9f+m*0.22f, "vault up & back off an icicle that bursts to slow the foes left behind")),   // (NEW)
            Def(UncRareEpic,(r,m)=>FinCard(r,FinType.FrostWalls, r==Rarity.Uncommon?7:6, 0.9f+m*0.22f, "clap two ice walls together, crushing foes between them for % of their max HP")),   // (NEW)
            Def(LegP,       (r,m)=>Card(r,"Coven's Reach", "+1 charged-modifier slot (max 4)",               p=>{ if(p.S.ModSlots<4) p.S.ModSlots++; })),
            // ---- blessings ----
            Def(AllR,       (r,m)=>Card(r,"Featherfall",   $"+{Mathf.RoundToInt(8*m)}% jump height",          p=>p.S.JumpMul += 0.08f*m)),
            Def(AllR,       (r,m)=>Card(r,"Warded Skin",   $"+{Mathf.RoundToInt(4*m)}% damage resistance",     p=>p.S.DmgResist = Mathf.Min(0.75f, p.S.DmgResist + 0.04f*m))),
            // ---- crit / spell sizing / luck (small, gradual) ----
            Def(AllR,       (r,m)=>Card(r,"Keen Eye",      $"+{Mathf.RoundToInt(1*m)}% crit chance (direct hits)", p=>p.S.CritChance = Mathf.Min(0.6f, p.S.CritChance + 0.01f*m))),
            Def(AllR,       (r,m)=>Card(r,"Cruel Edge",    $"+{Mathf.RoundToInt(8*m)}% crit damage",            p=>p.S.CritDamage += 0.08f*m)),
            Def(AllR,       (r,m)=>Card(r,"Far Sight",     $"+{Mathf.RoundToInt(4*m)}% spell range",            p=>p.S.SpellRange = Mathf.Min(2.5f, p.S.SpellRange + 0.04f*m))),
            Def(AllR,       (r,m)=>Card(r,"Widening Hex",  $"+{Mathf.RoundToInt(4*m)}% spell area",             p=>p.S.SpellArea = Mathf.Min(2.5f, p.S.SpellArea + 0.04f*m))),
            Def(AllR,       (r,m)=>Card(r,"Black Cat",     $"+{Mathf.RoundToInt(3*m)}% luck — rarer finds appear more often", p=>p.S.Luck += 0.03f*m)),
            // ---- minor passive auto-finishers (no slot, stack infinitely) ----
            Def(AllR,       (r,m)=>MinorCard(r, (MinorType)(int)(GD.Randi() % 18))),
            Def(AllR,       (r,m)=>MinorCard(r, (MinorType)(int)(GD.Randi() % 18))),
        };
    }

    public static List<UpgradeCard> RollThree(Player p, RandomNumberGenerator rng)
    {
        if (_defs == null) Build();
        float luck = p?.S?.Luck ?? 0f;
        var outl = new List<UpgradeCard>();
        int guard = 0;
        while (outl.Count < 3 && guard++ < 120)
        {
            var r = Rarities.Roll(rng, luck);
            var pool = _defs.FindAll(d => System.Array.IndexOf(d.Rars, r) >= 0);
            if (pool.Count == 0) continue;
            var d = pool[rng.RandiRange(0, pool.Count - 1)];
            var card = d.Make(r, Rarities.Mag(r));
            card.Unique = d.Rars.Length == 1;                              // (NEW) single-rarity cards are unique → unbannable
            if (Banned.Contains(card.Title)) continue;                     // (NEW) run-disabled family
            if (card.Hidden) continue;
            if (card.FinKind.HasValue) continue;   // finishers handled by the injection below
            if (outl.Exists(c => c.Title == card.Title)) continue;
            outl.Add(card);
        }
        // spell combos can still surface during normal upgrades — boosted until you own a usable one
        int usable = p.Fin.FindAll(f => !FinMeta.Passive(f.Type)).Count;
        float finChance = usable == 0 ? 0.5f : 0.25f;
        if (outl.Count == 3 && rng.Randf() < finChance)
        {
            var fin = RollFinisher(rng, luck);
            if (fin != null && !Banned.Contains(fin.Title)) { fin.Unique = fin.FinKind.HasValue && false; outl[rng.RandiRange(0, 2)] = fin; }
        }
        // witch-affinity: surface the active witch's own signature cards far more often than the shared pool would
        if (outl.Count == 3 && rng.Randf() < 0.10f)
        {
            var a = RollWitchAffinity(p, rng);
            if (a != null && !Banned.Contains(a.Title) && !outl.Exists(c => c.Title == a.Title)) outl[rng.RandiRange(0, 2)] = a;
        }
        return outl;
    }

    // pick one of the CURRENT witch's signature cards (Affinity-tagged, not hidden). Returns null if she has none.
    private static UpgradeCard RollWitchAffinity(Player p, RandomNumberGenerator rng)
    {
        if (_defs == null) Build();
        float luck = p?.S?.Luck ?? 0f;
        var aff = new List<UpgradeDef>();
        foreach (var d in _defs)
        {
            var probe = d.Make(d.Rars[0], Rarities.Mag(d.Rars[0]));   // tag/hidden are witch-state driven, not rarity driven
            if (probe.Affinity && !probe.Hidden) aff.Add(d);
        }
        if (aff.Count == 0) return null;
        var pick = aff[rng.RandiRange(0, aff.Count - 1)];
        var rolled = Rarities.Roll(rng, luck);
        Rarity use = System.Array.IndexOf(pick.Rars, rolled) >= 0 ? rolled : pick.Rars[0];   // else lowest allowed
        var card = pick.Make(use, Rarities.Mag(use));
        return card.Hidden ? null : card;
    }

    private static UpgradeCard RollFinisher(RandomNumberGenerator rng, float luck)
    {
        int guard = 0;
        while (guard++ < 160)
        {
            var r = Rarities.Roll(rng, luck);
            var pool = _defs.FindAll(d => System.Array.IndexOf(d.Rars, r) >= 0);
            if (pool.Count == 0) continue;
            var d = pool[rng.RandiRange(0, pool.Count - 1)];
            var card = d.Make(r, Rarities.Mag(r));
            if (card.Hidden) continue;
            if (!card.FinKind.HasValue) continue;
            if (FinMeta.Passive(card.FinKind.Value)) continue;   // don't hand out passive Crescendo here
            return card;
        }
        return null;
    }
    public static List<UpgradeCard> RollCategory(Player p, RandomNumberGenerator rng, int cat, int count)
    {
        if (_defs == null) Build();
        float luck = p?.S?.Luck ?? 0f;
        var outl = new List<UpgradeCard>();
        int guard = 0;
        while (outl.Count < count && guard++ < 900)
        {
            var r = Rarities.Roll(rng, luck);
            var pool = _defs.FindAll(d => System.Array.IndexOf(d.Rars, r) >= 0);
            if (pool.Count == 0) continue;
            var d = pool[rng.RandiRange(0, pool.Count - 1)];
            var card = d.Make(r, Rarities.Mag(r));
            if (card.Hidden) continue;
            bool ok = cat == 0 ? (!card.FinKind.HasValue && !card.ModKind.HasValue && card.AttuneSlot < 0)
                    : cat == 1 ? card.FinKind.HasValue
                    : card.ModKind.HasValue;
            if (!ok) continue;
            if (outl.Exists(c => c.Title == card.Title)) continue;
            outl.Add(card);
        }
        return outl;
    }

    public static List<UpgradeCard> RollFiltered(Player p, RandomNumberGenerator rng, Rarity min, int count)
    {
        if (_defs == null) Build();
        float luck = p?.S?.Luck ?? 0f;
        var outl = new List<UpgradeCard>();
        int guard = 0;
        while (outl.Count < count && guard++ < 500)
        {
            var r = Rarities.Roll(rng, luck);
            if ((int)r < (int)min) continue;
            var pool = _defs.FindAll(d => System.Array.IndexOf(d.Rars, r) >= 0);
            if (pool.Count == 0) continue;
            var d = pool[rng.RandiRange(0, pool.Count - 1)];
            var card = d.Make(r, Rarities.Mag(r));
            if (card.Hidden) continue;
            if (outl.Exists(c => c.Title == card.Title)) continue;
            outl.Add(card);
        }
        return outl;
    }
}
