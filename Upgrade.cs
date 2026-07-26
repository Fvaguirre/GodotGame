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
    public bool AbilityUp = false; // (OVERHAUL) an equipped-ability upgrade card (a stat path or an evolution)
    public Player.UltKind UltPick = Player.UltKind.None;   // (ULT CARDS) >None = an "equip this ult" Legendary card
    public bool UltTierUp = false;                          // (ULT CARDS) an "empower your ult +1 tier" Epic card
}
public class UpgradeDef { public Rarity[] Rars; public Func<Rarity, float, UpgradeCard> Make; }

// (OVERHAUL) upgrade-tree metadata for CONVERTED abilities. path 0-2 = the 3 stat paths, 3 = Epic evolution, 4 = Legendary evolution.
// Add an entry here (and convert the ability's handler to read its Stat/Evo stacks) as each witch is converted.
public static class AbilityUpg
{
    public struct Path { public string Name, Desc; public bool Evo; public Path(string n, string d, bool e = false) { Name = n; Desc = d; Evo = e; } }
    public static readonly System.Collections.Generic.Dictionary<ModType, Path[]> Mods = new()
    {
        [ModType.Meteor]   = new[] { new Path("Impact", "+impact damage"), new Path("Blast", "+blast radius"), new Path("Descent", "the meteor falls faster"), new Path("Meteor Shower", "+1 satellite meteor around the impact", true), new Path("Cataclysm", "impact leaves a re-igniting ember field", true) },
        [ModType.Eruption] = new[] { new Path("Magma", "+eruption damage"), new Path("Caldera", "+radius"), new Path("Upthrust", "+knockback & fling chance"), new Path("Fissure", "leaves a lingering burning crack", true), new Path("Volcano", "+1 delayed re-eruption", true) },
        [ModType.Sunder]   = new[] { new Path("Cleave", "+splash damage"), new Path("Shockwave", "+radius"), new Path("Cinders", "+burn on splash"), new Path("Shatter", "the splash ignites foes", true), new Path("Detonation", "the splash brands a Living Bomb", true) },
        [ModType.FrostWall] = new[] { new Path("Shatter", "+shatter damage"), new Path("Rampart", "+wall length"), new Path("Permafrost", "+duration & live-wall count"), new Path("Frostbite Wall", "chills foes hugging the wall", true), new Path("Glacier", "the wall pulses frost damage while it stands", true) },
        [ModType.FrostNova] = new[] { new Path("Coldsnap", "+nova damage"), new Path("Whiteout", "+radius"), new Path("Deep Freeze", "+freeze buildup"), new Path("Bitter Cold", "+1 delayed after-pulse", true), new Path("Flash Freeze", "the nova freezes foes instantly", true) },
        [ModType.Moonbeam]  = new[] { new Path("Radiance", "+tick damage"), new Path("Wellspring", "+radius"), new Path("Waning", "+duration"), new Path("Twin Wells", "+1 extra beam", true), new Path("Lunar Tide", "the shaft drags foes to its centre", true) },
        [ModType.Moonfall]  = new[] { new Path("Crater", "+nova damage"), new Path("Impact", "+radius"), new Path("Moonlight", "+crit chance on the nova"), new Path("Afterglow", "leaves a lingering scorch", true), new Path("Nightfall", "at night it craters again", true) },
        [ModType.Consecrate] = new[] { new Path("Wrath", "+enemy damage"), new Path("Sanctum", "+radius"), new Path("Grace", "+ally-heal power"), new Path("Hallowed", "consecration empowers your damage", true), new Path("Sanctified", "foes dying on it burst", true) },
        [ModType.Smite]      = new[] { new Path("Verdict", "+damage"), new Path("Far Reach", "+target range"), new Path("Retribution", "+slow & self-heal"), new Path("Condemn", "smited foes take bonus damage", true), new Path("Wrath of Heaven", "+1 smite target", true) },
        [ModType.Hemorrhage]    = new[] { new Path("Laceration", "+bleed dps"), new Path("Spray", "+radius"), new Path("Festering", "+bleed duration"), new Path("Rupture", "death-bursts hit harder", true), new Path("Crimson Plague", "bleed spreads to foes on death", true) },
        [ModType.CrimsonPool]   = new[] { new Path("Coagulate", "+tick damage"), new Path("Flood", "+radius"), new Path("Mire", "+slow strength"), new Path("Deep Well", "banks blood & heals faster", true), new Path("Bloodmire", "foes in the pool take bleed", true) },
        [ModType.SanguineSpikes] = new[] { new Path("Impale", "+damage"), new Path("Thicket", "+radius"), new Path("Harvest", "+blood per hit"), new Path("Barbs", "spikes root foes", true), new Path("Crimson Garden", "spikes persist & re-trigger", true) },
        [ModType.Bramble] = new[] { new Path("Snare", "+root duration"), new Path("Thornfield", "+patch radius"), new Path("Persistence", "+patch duration"), new Path("Thorn Snap", "the patch bursts on spawn", true), new Path("Overgrowth", "the patch spreads over time", true) },
        [ModType.Spore]   = new[] { new Path("Toxin", "+poison dps"), new Path("Billow", "+radius"), new Path("Lingering", "+duration"), new Path("Bursting Spores", "the cloud detonates when it ends", true), new Path("Fungal Bloom", "spawns a sporeling that fights for you", true) },
        [ModType.Implosion] = new[] { new Path("Rend", "+grind dps"), new Path("Event Horizon", "+radius"), new Path("Sustained", "+vortex duration"), new Path("Crush", "a heavier opening hit", true), new Path("Singularity", "pulls harder, ends in a burst", true) },
        [ModType.Whirlwind] = new[] { new Path("Shred", "+grind dps"), new Path("Funnel", "+radius"), new Path("Enduring", "+duration"), new Path("Launch Pad", "higher launch + it deals damage", true), new Path("Roaming Twister", "wanders toward foes", true) },
        [ModType.HexMark]   = new[] { new Path("Vulnerability", "+mark amplify"), new Path("Contagion", "+jump count"), new Path("Far Curse", "+range & duration"), new Path("Spreading Mark", "marks extra nearby foes", true), new Path("Doombrand", "a marked foe detonates on death", true) },
        [ModType.Cursefield] = new[] { new Path("Blight", "+tick damage"), new Path("Pall", "+radius"), new Path("Enfeeble", "+slow strength"), new Path("Deep Curse", "marks foes for amplified damage", true), new Path("Withering Field", "foes inside rot (stacking DoT)", true) },
        [ModType.ArcStorm]    = new[] { new Path("Voltage", "+per-bolt damage"), new Path("Arc", "+jump count"), new Path("Conductance", "+fork range"), new Path("Overcharge", "+1 chain jump", true), new Path("Chain Reaction", "struck foes marked for extra chains", true) },
        [ModType.ArcaneVortex] = new[] { new Path("Maelstrom", "+grind dps"), new Path("Expanse", "+radius"), new Path("Drag", "+slow strength"), new Path("Singularity", "the vortex pulls foes inward", true), new Path("Unstable Core", "collapses into a nova when it ends", true) },
    };
    public static readonly System.Collections.Generic.Dictionary<FinType, Path[]> Fins = new()
    {
        [FinType.FireWall]    = new[] { new Path("Blaze", "+ring damage"), new Path("Wide Ring", "+radius"), new Path("Everburn", "+duration"), new Path("Roaring Flames", "+40% burn", true), new Path("Expanding Inferno", "+1 concentric outer ring", true) },
        [FinType.Fireball]    = new[] { new Path("Scorch", "+direct & blast damage"), new Path("Detonation", "+blast radius"), new Path("Ignition", "+burn"), new Path("Split Shot", "+1 fireball", true), new Path("Cataclysm", "impact leaves an ember field", true) },
        [FinType.EmberFervor] = new[] { new Path("Frenzy", "+duration"), new Path("Fervour", "+crit bonus"), new Path("Swiftness", "+move speed"), new Path("Wildfire", "your hits ignite foes", true), new Path("Phoenix Heart", "heals you through the buff", true) },
        [FinType.IceSpike]    = new[] { new Path("Frostbite", "+cone damage"), new Path("Wide Cone", "+reach & angle"), new Path("Upheaval", "+fling force"), new Path("Rime", "the spikes freeze foes", true), new Path("Impaler", "shatters frozen foes for bonus damage", true) },
        [FinType.FrostVault]  = new[] { new Path("Shard", "+icicle damage"), new Path("Fracture", "+burst radius"), new Path("Numb", "+slow strength"), new Path("Flash Freeze", "the icicle freezes foes", true), new Path("Avalanche", "+1 extra icicle", true) },
        [FinType.FrostWalls]  = new[] { new Path("Crush", "+% max-HP crush"), new Path("Wide Vise", "+width"), new Path("Rimebite", "+slow strength"), new Path("Shatter Clap", "+flat clap damage", true), new Path("Absolute Vise", "freezes the foes it traps", true) },
        [FinType.LunarNova]     = new[] { new Path("Waxing", "+nova damage"), new Path("Full Moon", "+radius"), new Path("Deep Chill", "+slow strength & duration"), new Path("Eclipse Echo", "a second smaller pulse follows", true), new Path("Blood Moon", "the nova swells at night", true) },
        [FinType.CrescentStorm] = new[] { new Path("Keen Edge", "+per-blade damage"), new Path("Gibbous", "+blade count"), new Path("Sickle", "+pierce & blade size"), new Path("Splintering Moon", "blades split on first hit", true), new Path("Waxing Horde", "+1 orbiting blade", true) },
        [FinType.Halo]  = new[] { new Path("Radiance", "+nova damage"), new Path("Corona", "+radius"), new Path("Benediction", "+heal & Blessed duration"), new Path("Twin Halo", "+1 concentric ring", true), new Path("Sanctuary", "grants a protective shield", true) },
        [FinType.Lance] = new[] { new Path("Judgement", "+lance damage"), new Path("Legion", "+lance count"), new Path("Wide Aim", "+per-lance radius"), new Path("Condemn", "struck foes are stunned", true), new Path("Rain of Heaven", "+1 delayed volley", true) },
        [FinType.Heal]  = new[] { new Path("Blessing", "+heal power"), new Path("Grove", "+radius"), new Path("Evergreen", "+duration"), new Path("Consecrated", "foes inside take searing damage", true), new Path("Wellspring", "the field follows you", true) },
        [FinType.BloodNova]   = new[] { new Path("Rupture", "+nova damage"), new Path("Spatter", "+radius"), new Path("Repel", "+knockback"), new Path("Hemoclast", "the nova applies bleed", true), new Path("Sanguine Surge", "scales with your missing HP", true) },
        [FinType.CrimsonRush] = new[] { new Path("Onslaught", "+wave damage"), new Path("Momentum", "+dash distance & width"), new Path("Bulwark", "+blood-shield return chance"), new Path("Gore Trail", "leaves a bleeding trail", true), new Path("Tsunami", "+1 chasing wave", true) },
        [FinType.BloodCurse]  = new[] { new Path("Miasma", "+cone damage"), new Path("Wide Maw", "+cone width"), new Path("Contagion", "+hex bounces"), new Path("Plague", "the hex spreads & festers", true), new Path("Exsanguinate", "cursed foes bleed HP to you", true) },
        [FinType.PoisonField] = new[] { new Path("Virulence", "+poison ramp"), new Path("Overgrowth", "+radius"), new Path("Perennial", "+duration"), new Path("Toxic Bloom", "the field also slows", true), new Path("Miasma", "the field creeps toward enemies", true) },
        [FinType.SeedMine]    = new[] { new Path("Yield", "+mine damage"), new Path("Sowing", "+mine count"), new Path("Blast Cap", "+blast radius"), new Path("Chain Bloom", "mines detonate in a chain", true), new Path("Spore Mines", "each leaves a poison cloud", true) },
        [FinType.Root]        = new[] { new Path("Nettle", "+burst damage"), new Path("Thicket", "+radius"), new Path("Iron Root", "+root duration"), new Path("Thornburst", "rooted foes take poison", true), new Path("Ensnaring Grove", "roots ripple outward", true) },
        [FinType.ThornSkin]   = new[] { new Path("Barbs", "+burst damage"), new Path("Bramble", "+burst radius"), new Path("Bark", "+thorn charges banked per cast"), new Path("Snare Bark", "the burst roots foes", true), new Path("Ironbark", "briefly gain damage resist", true) },
        [FinType.Updraft]   = new[] { new Path("Squall", "+lift force"), new Path("Wide Gust", "+radius"), new Path("Ascend", "+self-launch height"), new Path("Cyclone Kick", "the launch deals damage", true), new Path("Tempest", "lifted foes take falling damage", true) },
        [FinType.WindRush]  = new[] { new Path("Buffet", "+damage"), new Path("Slipstream", "+dash distance"), new Path("Uplift", "+fling force"), new Path("Second Wind", "+dash refund chance", true), new Path("Gale Force", "leaves a damaging gust trail", true) },
        [FinType.WindSlice] = new[] { new Path("Edge", "+damage"), new Path("Broad Cut", "+width"), new Path("Far Throw", "+range & speed"), new Path("Cross Cut", "fires a full X-cross", true), new Path("Vortex Edge", "drags foes together in its wake", true) },
        [FinType.Wave]      = new[] { new Path("Wither", "+damage"), new Path("Expanse", "+radius"), new Path("Malediction", "+curse potency applied"), new Path("Echo Pulse", "a second ring follows outward", true), new Path("Doom Wave", "cursed foes caught explode", true) },
        [FinType.HexChains] = new[] { new Path("Lash", "+initial burst"), new Path("Weave", "+link count"), new Path("Sympathy", "+share fraction"), new Path("Bind", "chained foes are slowed", true), new Path("Torment", "+shared damage fraction", true) },
        [FinType.SoulReap]  = new[] { new Path("Scythe", "+base damage"), new Path("Reach", "+radius"), new Path("Execution", "+missing-HP scaling"), new Path("Glut", "much stronger lifesteal", true), new Path("Harvest", "kills refund the charge", true) },
        [FinType.DoomSigil] = new[] { new Path("Doom", "+base damage"), new Path("Sigil", "+brand radius"), new Path("Compounding", "+per-brand bonus"), new Path("Quickdoom", "shorter detonation delay", true), new Path("Cataclysm Sigil", "the blast re-brands & chains", true) },
        [FinType.Swarm]       = new[] { new Path("Spellbite", "+bolt damage"), new Path("Coven", "+bolt count"), new Path("Tracking", "sharper homing"), new Path("Conduit Swarm", "bolts leave conduit marks", true), new Path("Living Current", "bolts arc to another foe on hit", true) },
        [FinType.Volley]      = new[] { new Path("Volley", "+bolt damage"), new Path("Salvo", "+bolt count"), new Path("Velocity", "+bolt speed"), new Path("Seeking", "the bolts gain homing", true), new Path("Barrage", "+1 delayed volley", true) },
        [FinType.ArcaneBlast] = new[] { new Path("Surge", "+damage"), new Path("Breadth", "+width"), new Path("Distance", "+length & knockback"), new Path("Overcharge", "crit ramps as it fires", true), new Path("Cataclysm", "chains through conduit-marked foes", true) },
        [FinType.ArcaneBlink] = new[] { new Path("Rift", "+rift damage"), new Path("Warp", "+rift radius"), new Path("Phase", "+blink distance"), new Path("Triple Rift", "+1 rift mid-blink", true), new Path("Implode", "rifts pull foes in first", true) },
        [FinType.Beam]        = new[] { new Path("Bore", "+dps"), new Path("Beamwidth", "+width"), new Path("Channel", "+duration"), new Path("Overload", "crit climbs while held", true), new Path("Prism", "the lance splits into extra beams", true) },
    };
    public static bool IsMod(ModType t) => Mods.ContainsKey(t);
    public static bool IsFin(FinType t) => Fins.ContainsKey(t);
}

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
            if (card.FinKind.HasValue || card.ModKind.HasValue) continue;   // (OVERHAUL) abilities are Common-found + tree-deepened — never a roulette prize
            if (card.AttuneSlot == 0 || card.AttuneSlot == 1) continue;   // attack-retune attunes are Mystic-vendor-only; slot 2 (Cursebrand) rolls normally
            if (BlockedRarity(p, card)) continue;   // (NEW) never offer a finisher/modifier at ≤ the rarity you already own
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
                if (card.AttuneSlot < 0 && !card.FinKind.HasValue && !card.ModKind.HasValue && outl.Count > 0 && !BlockedRarity(p, card)) outl[0] = card;   // never a legendary ability
            }
        }
        // NOTE: ult-mod injection lives ONLY in Game.RollChoices now (cooldown- + grace-gated). It used to ALSO be here at
        // 15% with no cooldown, which double-dipped and ignored the post-bind grace — that's why legendary ult-mods flooded
        // right after level 10. Do not re-add it here.
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
            if (card.Hidden) continue;   // wrong-witch / already-owned legendaries return Hidden — don't hand one back
            if (card.FinKind.HasValue || card.ModKind.HasValue) continue;   // (OVERHAUL) never a legendary ability card — abilities are Common-found
            if (card.AttuneSlot == 0 || card.AttuneSlot == 1) continue;   // attack-retune attunes are vendor-only; slot 2 (Cursebrand) is fine
            return card;
        }
        return null;
    }

    // ==== SHOP (the peddler vendor) rollers ==========================================================
    // Cost by rarity, tuned so a Legendary lands at ~500 gold.
    public static int RarityCost(Rarity r) => r switch {
        Rarity.Common => 60, Rarity.Uncommon => 130, Rarity.Rare => 240, Rarity.Epic => 370, Rarity.Legendary => 500, _ => 60 };

    // §1 — boons: blessings + this witch's signature upgrade cards, plus the ult-mod if she owns an ult but not its mod yet.
    public static List<UpgradeCard> RollShopBoons(Player p, RandomNumberGenerator rng, int count)
    {
        if (_defs == null) Build();
        var outl = new List<UpgradeCard>();
        if (p.Ult != Player.UltKind.None) { var um = UltModCard(p); if (um != null) outl.Add(um); }   // ult upgrade card
        int guard = 0;
        while (outl.Count < count && guard++ < 400)
        {
            var r = Rarities.Roll(rng, p.S.Luck);
            var pool = _defs.FindAll(d => Array.IndexOf(d.Rars, r) >= 0);
            if (pool.Count == 0) continue;
            var card = pool[rng.RandiRange(0, pool.Count - 1)].Make(r, Rarities.Mag(r));
            if (card.Hidden || card.FinKind.HasValue || card.ModKind.HasValue) continue;   // boons only — finishers/modifiers have their own sections
            if (card.AttuneSlot == 0 || card.AttuneSlot == 1) continue;                     // attack-retunes stay Mystic-vendor-only
            if (outl.Exists(c => c.Title == card.Title)) continue;
            outl.Add(card);
        }
        return outl;
    }

    // §2 — finishers: 2 of the witch's element + 2 from the wider pool. Only ones she doesn't own OR a strict rarity upgrade.
    public static List<UpgradeCard> RollShopFinishers(Player p, RandomNumberGenerator rng)
    {
        var elem = new List<FinType>(); var other = new List<FinType>();
        foreach (FinType t in Enum.GetValues(typeof(FinType)))
        {
            if (t == FinType.Crescendo || t == FinType.Fullmod) continue;    // not offered (mirrors the scroll vendor)
            if (!AbilityUpg.IsFin(t)) continue;                              // (OVERHAUL) only CONVERTED abilities — power lives in their upgrade tree
            if (p.FinisherRank(t) >= 0) continue;                            // (OVERHAUL) unowned only — rarity upgrades are dead, so an owned finisher has nothing to sell
            (FinMeta.DType(t) == p.PrimaryType ? elem : other).Add(t);
        }
        var outl = new List<UpgradeCard>();
        void take(List<FinType> src, int want)
        {
            while (want > 0 && src.Count > 0)
            {
                var t = src[rng.RandiRange(0, src.Count - 1)]; src.Remove(t);
                // (OVERHAUL) found abilities ALWAYS show a Common frame at base stats — the shop no longer sells them at Rare/Epic/Leg
                outl.Add(FinCard(Rarity.Common, t, 8, 0.9f + Rarities.Mag(Rarity.Common) * 0.22f, FinMeta.Desc(t)));
                want--;
            }
        }
        take(elem, 2); take(other, 2);
        if (outl.Count < 4) { take(elem, 4 - outl.Count); take(other, 4 - outl.Count); }   // backfill if a bucket ran dry
        return outl;
    }

    // §3 — right-click modifiers: same rule as finishers.
    public static List<UpgradeCard> RollShopModifiers(Player p, RandomNumberGenerator rng)
    {
        var elem = new List<ModType>(); var other = new List<ModType>();
        foreach (ModType t in Enum.GetValues(typeof(ModType)))
        {
            if (!AbilityUpg.IsMod(t)) continue;                              // (OVERHAUL) only CONVERTED abilities
            if (p.ModifierRank(t) >= 0) continue;                            // (OVERHAUL) unowned only — an owned modifier has nothing to sell
            (ModMeta.DType(t) == p.PrimaryType ? elem : other).Add(t);
        }
        var outl = new List<UpgradeCard>();
        void take(List<ModType> src, int want)
        {
            while (want > 0 && src.Count > 0)
            {
                var t = src[rng.RandiRange(0, src.Count - 1)]; src.Remove(t);
                // (OVERHAUL) found abilities ALWAYS show a Common frame at base stats — no more Rare/Epic/Leg modifiers in the shop
                outl.Add(ModCard(Rarity.Common, t, Rarities.Mag(Rarity.Common), ModMeta.Desc(t)));
                want--;
            }
        }
        take(elem, 2); take(other, 2);
        if (outl.Count < 4) { take(elem, 4 - outl.Count); take(other, 4 - outl.Count); }
        return outl;
    }

    // (ULT CARDS) display name + one-line pitch per ult
    public static string UltName(Player.UltKind k) => k switch {
        Player.UltKind.Eclipse => "Eclipse", Player.UltKind.LunarLight => "Lunar Light", Player.UltKind.Crescent => "Crescent Blades",
        Player.UltKind.FaithShield => "Faith Shield", Player.UltKind.Judgement => "Judgement", Player.UltKind.Divinity => "Divinity",
        Player.UltKind.BloodTsunami => "Blood Tsunami", Player.UltKind.Exsanguinate => "Exsanguinate", Player.UltKind.BloodRot => "Blood Rot",
        Player.UltKind.GroveGuardian => "Grove Guardian", Player.UltKind.WildSwarm => "Wild Swarm", Player.UltKind.Barkskin => "Barkskin",
        Player.UltKind.Cyclone => "Cyclone", Player.UltKind.Hurricane => "Hurricane", Player.UltKind.Stormform => "Stormform",
        Player.UltKind.Blizzard => "Blizzard", Player.UltKind.FrostElemental => "Frost Elemental", Player.UltKind.DeepFreeze => "Glacial Sunder",
        Player.UltKind.HexCircle => "Hex Circle", Player.UltKind.LifeDrain => "Life Drain", Player.UltKind.LifeCurse => "Specter",
        Player.UltKind.MeteorDescent => "Meteor Descent", Player.UltKind.WildfireRush => "Wildfire Rush", Player.UltKind.PhoenixAscend => "Phoenix Ascendant",
        Player.UltKind.ArcaneAscend => "Arcane Ascension", Player.UltKind.ArcaneEruption => "Arcane Eruption", Player.UltKind.ArcaneOvercharge => "Arcane Storm",
        _ => "Ultimate" };
    public static string UltPitch(Player.UltKind k) => k switch {
        Player.UltKind.Eclipse => "become the void — +crit, arcane-blink, every hit detonates a nova",
        Player.UltKind.LunarLight => "a moonwell that heals allies and sears foes",
        Player.UltKind.Crescent => "summon blades you fly around the battlefield",
        Player.UltKind.FaithShield => "a sanctified dome that heals, sears, and shatters",
        Player.UltKind.Judgement => "pillars of light smite the worst of the horde",
        Player.UltKind.Divinity => "ascend untouchable, raining holy motes",
        Player.UltKind.BloodTsunami => "a wide, fast tidal wall of blood",
        Player.UltKind.Exsanguinate => "rip the blood out; the wounded pop — a kill heals you full",
        Player.UltKind.BloodRot => "a spreading pool of rot",
        Player.UltKind.GroveGuardian => "a treant that slams the earth",
        Player.UltKind.WildSwarm => "a stampede tramples a lane",
        Player.UltKind.Barkskin => "the coven turns to living bark — immune, healed, then bursts",
        Player.UltKind.Cyclone => "a vast tornado that eats the field",
        Player.UltKind.Hurricane => "pilot a storm, flinging everything below",
        Player.UltKind.Stormform => "become the gale — fast, unspent, kills extend it",
        Player.UltKind.Blizzard => "a freezing storm that grinds and freezes",
        Player.UltKind.FrostElemental => "a roaming ice titan",
        Player.UltKind.DeepFreeze => "erupt giant icicles — hit hard, fling foes, then radiate cold",
        Player.UltKind.HexCircle => "a curse ring that fuses their fates into one cascade",
        Player.UltKind.LifeDrain => "fly and siphon the crowd dry, then burst it back",
        Player.UltKind.LifeCurse => "phase out untouchable — heal, then release a soul-nova",
        Player.UltKind.MeteorDescent => "rise, aim, and come down as a meteor",
        Player.UltKind.WildfireRush => "a window of flaming, life-stealing dashes",
        Player.UltKind.PhoenixAscend => "hurl a phoenix that grabs the horde and skyburns them",
        Player.UltKind.ArcaneAscend => "ascend and chain raw lightning through the crowd",
        Player.UltKind.ArcaneEruption => "an unstable rupture that heaves foes skyward",
        Player.UltKind.ArcaneOvercharge => "a storm that rains arcane bolts — worse for the tough",
        _ => "your ultimate" };

    // (ULT CARDS) a LEGENDARY "equip this ult" card. Picking it binds the ult, restoring its saved tier if you'd had it.
    public static UpgradeCard UltEquipCard(Player p, Player.UltKind k)
    {
        int saved = p.UltTiers.TryGetValue(k, out int tr) ? tr : 0;
        string tail = saved > 0 ? $"  ·  returns at tier {saved}/4" : "";
        return new UpgradeCard { Rarity = Rarity.Legendary, Unique = true, UltPick = k,
            Title = "★ " + UltName(k), Desc = "ULTIMATE — " + UltPitch(k) + tail, Apply = pl => pl.EquipUlt(k) };
    }
    // (ULT CARDS) an EPIC "empower your ult" card — only meaningful while that ult is equipped and below max tier.
    public static UpgradeCard UltTierCard(Player p)
    {
        if (p.Ult == Player.UltKind.None || p.UltTier >= 4) return null;
        return new UpgradeCard { Rarity = Rarity.Legendary, Unique = true, UltTierUp = true,
            Title = UltName(p.Ult) + " +", Desc = $"empower your ultimate — tier {p.UltTier + 1}/4 (bigger, longer, deadlier)", Apply = pl => pl.UpgradeUltTier() };
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
                if (!p.ModShield) return Card(Rarity.Legendary, "Aegis Sanctum", "the dome heals more inside, sears pressing foes harder, and its shatter hits harder", x => x.ModShield = true);
                break;
            case Player.UltKind.Judgement:
                if (!p.ModJudge) return Card(Rarity.Legendary, "Final Verdict", "Judgement becomes ONE colossal lance — devastating core, pulsing holy field for 5s", x => x.ModJudge = true);
                break;
            case Player.UltKind.Divinity:
                if (!p.ModDivinity) return Card(Rarity.Legendary, "Ascendant", "divinity lasts longer; motes hit harder & leave holy ground", x => x.ModDivinity = true);
                break;
            case Player.UltKind.BloodTsunami:
                if (!p.ModTsunami) return Card(Rarity.Legendary, "Crimson Deluge", "the tsunami erupts RADIALLY — waves surge out in every direction, wider & harder", x => x.ModTsunami = true);
                break;
            case Player.UltKind.Exsanguinate:
                if (!p.ModExsang) return Card(Rarity.Legendary, "Bloodthirst", "the blood-harvest aura is larger and drains harder", x => x.ModExsang = true);
                break;
            case Player.UltKind.BloodRot:
                if (!p.ModRot) return Card(Rarity.Legendary, "Plague Bloom", "rot is larger & harder AND its DoT NEVER fades — it bleeds a foe until death", x => x.ModRot = true);
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
                if (!p.ModDeepFreeze) return Card(Rarity.Legendary, "Absolute Zero", "Glacial Sunder's icicles flash-freeze on impact, and their radiating cold shatters frozen foes for bonus damage", x => x.ModDeepFreeze = true);
                break;
            case Player.UltKind.HexCircle:   // (NEW)
                if (!p.ModPlague) return Card(Rarity.Legendary, "Plaguebearer", "the Hex Circle is larger and its ground festers, dealing a curse DoT to everyone inside", x => x.ModPlague = true);
                break;
            case Player.UltKind.LifeDrain:   // (NEW)
                if (!p.ModRapture) return Card(Rarity.Legendary, "Rapture", "Life Drain also drags every foe in range toward you as you channel", x => x.ModRapture = true);
                break;
            case Player.UltKind.LifeCurse:   // (NEW)
                if (!p.ModRite) return Card(Rarity.Legendary, "Soul Harrow", "while immaterial, drifting through a foe saddles it with a percent-max-HP curse that eats away over time", x => x.ModRite = true);
                break;
            case Player.UltKind.MeteorDescent:   // (NEW ember)
                if (!p.ModMeteorDesc) return Card(Rarity.Legendary, "Extinction Event", "the meteor lands bigger and harder, the inferno lingers, and satellite meteors rain around the impact", x => x.ModMeteorDesc = true);
                break;
            case Player.UltKind.WildfireRush:   // (NEW ember)
                if (!p.ModWildfire) return Card(Rarity.Legendary, "Firestorm", "+2 flame dashes, and the burning trails are longer, wider, and heal you more", x => x.ModWildfire = true);
                break;
            case Player.UltKind.PhoenixAscend:   // (NEW ember)
                if (!p.ModPhoenix) return Card(Rarity.Legendary, "Eternal Flame", "the phoenix grabs far more foes, its skyburst hits harder, and it rains a lingering inferno onto its landing spot", x => x.ModPhoenix = true);
                break;
            case Player.UltKind.ArcaneAscend:   // (NEW arcane)
                if (!p.ModArcStorm) return Card(Rarity.Legendary, "Storm Incarnate", "Ascension lasts longer, and its chain-lightning strikes far more foes at once and arcs to twice as many neighbours", x => x.ModArcStorm = true);
                break;
            case Player.UltKind.ArcaneEruption:   // (NEW arcane)
                if (!p.ModArcCataclysm) return Card(Rarity.Legendary, "Cataclysm", "the eruption is far bigger, unleashes a second delayed shockwave, and its lingering field rages longer + harder", x => x.ModArcCataclysm = true);
                break;
            case Player.UltKind.ArcaneOvercharge:   // (NEW arcane)
                if (!p.ModArcUnbound) return Card(Rarity.Legendary, "Singularity", "the Arcane Storm is far bigger, drags foes into its heart, and strikes each of them twice as often", x => x.ModArcUnbound = true);
                break;
        }
        return null;
    }

    private static UpgradeCard Card(Rarity r, string t, string d, Action<Player> a) => new UpgradeCard { Rarity = r, Title = t, Desc = d, Apply = a };
    private static bool Bw() => Game.I?.Player?.CrimsonWitch ?? false;   // is the current witch Crimson (the only one with Blood Stacks)?
    // a stat card that is one of the active witch's SIGNATURE cards — tagged so the affinity roll surfaces it more often
    private static UpgradeCard WitchCard(Rarity r, string t, string d, Action<Player> a) => new UpgradeCard { Rarity = r, Title = t, Desc = d, Apply = a, Affinity = true };
    // (NEW) rarity floor: once you own a finisher/modifier, you may only be offered a STRICTLY HIGHER rarity of it — an equal
    // or lesser one would just replace with no gain. (Minors are exempt — they stack, so they never come through here.)
    private static bool BlockedRarity(Player p, UpgradeCard card)
    {
        if (p == null || card == null) return false;
        if (card.FinKind.HasValue) { int owned = p.FinisherRank(card.FinKind.Value); return owned >= 0 && (int)card.Rarity <= owned; }
        if (card.ModKind.HasValue) { int owned = p.ModifierRank(card.ModKind.Value); return owned >= 0 && (int)card.Rarity <= owned; }
        return false;
    }

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
            Def(UncP,  (r,m)=>Card(r,"Full Heal",       "instantly restore ALL health",                   p=>p.Hp = p.S.MaxHp)),   // (EFFIGY) new survival card — binary, rarity is cosmetic
            Def(AllR,  (r,m)=>Card(r,"Moonglass Aegis", $"+{Mathf.RoundToInt(8*m)}% shield capacity",     p=>{p.S.ShieldPct += 0.08f*m; p.Shield = p.MaxShield;})),
            Def(AllR,  (r,m)=>Card(r,"Swift Mending",   $"shield recovers {Mathf.RoundToInt(12*m)}% sooner", p=>p.S.ShieldDelay *= 1-Mathf.Min(0.7f,0.12f*m))),
            Def(AllR,  (r,m)=>{ float b=r switch{Rarity.Common=>0.1f,Rarity.Uncommon=>0.3f,Rarity.Rare=>0.5f,Rarity.Epic=>0.7f,_=>1.0f}; return Card(r,"Quickening Ward", $"+{b:0.0} shield regen / sec", p=>p.S.ShieldRegen += b); }),
            Def(AllR,  (r,m)=>{ int add=r switch{Rarity.Common=>1,Rarity.Uncommon=>1,Rarity.Rare=>2,Rarity.Epic=>2,_=>3}; return Card(r,"Wind Step", $"+{add} dash distance", p=>p.S.DashDist = Mathf.Min(16f, p.S.DashDist + add)); }),
            Def(UncP,  (r,m)=>Card(r,"Fleet Step",      $"dash recharges {Mathf.RoundToInt(7*m)}% faster", p=>p.S.DashCd = Mathf.Max(0.9f, p.S.DashCd*(1-Mathf.Min(0.4f,0.07f*m))))),
            Def(LegP,  (r,m)=>Card(r,"Twin Step",       "+1 dash charge (max 3)",                          p=>{ if(p.S.DashCharges<3){p.S.DashCharges++; p.DashStock++;} })),
            Def(AllR,  (r,m)=>{ var pl=Game.I?.Player; if (pl!=null && pl.CrimsonWitch){ float red=r switch{Rarity.Common=>0.004f,Rarity.Uncommon=>0.008f,Rarity.Rare=>0.012f,Rarity.Epic=>0.016f,_=>0.022f}; return WitchCard(r,"Blood Efficiency", $"finishers cost {red*100:0.0}% less health", p=>p.FinHpCost=Mathf.Max(0.04f,p.FinHpCost-red)); } float add=r switch{Rarity.Common=>0.01f,Rarity.Uncommon=>0.03f,Rarity.Rare=>0.05f,Rarity.Epic=>0.07f,_=>0.1f}; return Card(r,"Mana Wellspring", $"+{add:0.00} mana per normal hit", p=>p.S.ManaGain += add); }),
            Def(EpicP, (r,m)=>{ var pl=Game.I?.Player; if (pl!=null && pl.CrimsonWitch) return WitchCard(r,"Blood Reserve","+8% max health", p=>{ p.S.MaxHp*=1.08f; p.Hp=Mathf.Min(p.S.MaxHp,p.Hp+p.S.MaxHp*0.08f); }); float add=r==Rarity.Legendary?1f:0.5f; return Card(r,"Deep Reserve",$"+{add:0.0} max mana (max 5)", p=>{ if(p.S.ManaMax<5f){p.S.ManaMax=Mathf.Min(5f,p.S.ManaMax+add); p.Mana=Mathf.Min(p.S.ManaMax,p.Mana+add);} }); }),
            Def(UncP,  (r,m)=>Card(r,"Siphon",          $"heal {(0.6f*m):0.0}% of damage dealt",           p=>p.S.Lifesteal += 0.006f*m)),
            Def(RareP, (r,m)=>{ var pl=Game.I?.Player; if (pl!=null && !pl.FiresBolts) return new UpgradeCard { Rarity=r, Hidden=true }; return Card(r,"Piercing Sigil", $"your bolts pierce +{(r==Rarity.Legendary?2:1)} more foes", p=>p.S.Pierce += (r==Rarity.Legendary?2:1)); }),   // (NEW) hidden for beam/cone/missile witches — bolt-pierce does nothing for them
            Def(RareP, (r,m)=>Card(r,"Cadence",         $"+{(2*m):0.0}% dmg per combo & +combo cap",        p=>{p.S.ComboPow += 0.02f*m; p.S.ComboCap += 2 + (r==Rarity.Legendary?2:0);})),
            Def(UncRareEpic,(r,m)=>Card(r,"Witch's Rhythm", $"combo window +{(0.25f*m):0.00}s",             p=>{p.S.ComboWindow += 0.25f*m; p.S.ComboPow += 0.008f*m;})),
            // (REMOVED) Crescendo — its passive-split design is outdated, especially for beam/channel witches; no longer offered
            // ---- finishers (Q/E/F) ----
            Def(ComUnc,     (r,m)=>FinCard(r,FinType.Wave,    r==Rarity.Common?8:6, 0.9f+m*0.22f, "burst a wave around you")),
            Def(ComUncRare, (r,m)=>FinCard(r,FinType.Volley,  r==Rarity.Common?8:(r==Rarity.Uncommon?7:6), 0.9f+m*0.22f, "fire 5+ aimed bolts")),
            Def(UncRare,    (r,m)=>FinCard(r,FinType.Beam,    r==Rarity.Uncommon?7:6, 0.9f+m*0.25f, "channel an aimable beam")),
            Def(UncRareEpic,(r,m)=>FinCard(r,FinType.Swarm,   r==Rarity.Uncommon?7:(r==Rarity.Rare?6:5), 0.9f+m*0.22f, "loose a homing swarm")),
            Def(RareP,      (r,m)=>FinCard(r,FinType.Root,    r==Rarity.Rare?6:5, 0.9f+m*0.22f, "root nearby foes")),
            Def(UncRareEpic,(r,m)=>FinCard(r,FinType.Heal,    r==Rarity.Uncommon?7:(r==Rarity.Rare?6:5), 0.9f+m*0.22f, "drop a healing circle")),
            Def(EpicP,      (r,m)=>FinCard(r,FinType.Fullmod, r==Rarity.Epic?6:5, 0.9f+m*0.22f, "erupt a full-power modded blast")),
            Def(RareP,      (r,m)=>FinCard(r,FinType.HexField,6, 0.9f+m*0.25f, "drop a lingering hex field (~5s)")),
            Def(UncRareEpic,(r,m)=>FinCard(r,FinType.LunarNova, r==Rarity.Uncommon?7:(r==Rarity.Rare?6:5), 0.9f+m*0.22f, "erupt a Lunar nova + slow")),      // (NEW Lunar)
            Def(UncRareEpic,(r,m)=>FinCard(r,FinType.CrescentStorm, r==Rarity.Uncommon?7:(r==Rarity.Rare?6:5), 0.9f+m*0.22f, "loose homing crescent blades")),   // (NEW Lunar)
            Def(LegP,       (r,m)=>Card(r,"Coven Bond",  "+1 finisher slot (max 5)",                        p=>{ if(p.S.FinSlots<5) p.S.FinSlots++; })),
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
            Def(UncRareEpic,(r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.FrostWitch) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Splinterfrost",$"shattering a foe implants +{(0.5f*m):0.0} freeze stacks into others caught in the burst", p=>p.ShatterFreezeStacks+=0.5f*m); }),
            Def(LegP,       (r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.FrostWitch || pl.ShatterCascade) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Shatter Cascade","shattering an ice block detonates every nearby frozen foe too — chain the whole crowd", p=>p.ShatterCascade=true); }),   // (NEW legendary)
            Def(LegP,       (r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.FrostWitch || pl.DeepWinter) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Deep Winter","frozen foes radiate cold, rapidly freezing the enemies around them", p=>p.DeepWinter=true); }),   // (NEW legendary)
            Def(LegP,       (r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.FrostWitch || pl.GlacialImpaler) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Glacial Impaler","your icicle spear pierces every foe in a line and shatters frozen ones at ANY charge", p=>p.GlacialImpaler=true); }),   // (NEW legendary)
            // --- Forsaken affinity (curse / tethers / siphon) (NEW) ---
            Def(UncRareEpic,(r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.ForsakenWitch) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Wasting Curse",$"+{(0.6f*m):0.0} curse stacks/sec from your beam", p=>p.CurseRate+=0.6f*m); }),
            Def(UncRareEpic,(r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.ForsakenWitch) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Deepening Hex",$"+{(1.5f*m):0.0} to your voodoo-crush stack ceiling — a bigger maximum detonation", p=>p.CurseStackCap+=1.5f*m); }),
            Def(UncRareEpic,(r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.ForsakenWitch) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Leeching Beam",$"+{Mathf.RoundToInt(12*m)}% of your beam damage healed back", p=>p.CurseBeamLifesteal=Mathf.Min(1f,p.CurseBeamLifesteal+0.12f*m)); }),
            Def(UncRareEpic,(r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.ForsakenWitch) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Sympathetic Pain",$"+{Mathf.RoundToInt(8*m)}% of damage shared across a tether group", p=>p.CurseShareFrac=Mathf.Min(1f,p.CurseShareFrac+0.08f*m)); }),
            Def(UncRareEpic,(r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.ForsakenWitch) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Virulent Hex",$"+{Mathf.RoundToInt(12*m)}% bonus damage to cursed foes", p=>p.CurseBonusMul+=0.12f*m); }),
            Def(UncRareEpic,(r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.ForsakenWitch || pl.MaxLinks>=12) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Binding Ritual",$"+1 max tether link & +{Mathf.RoundToInt(2*m)}u curse-spread range", p=>{ p.MaxLinks=Mathf.Min(12,p.MaxLinks+1); p.CurseSpreadRange+=2f*m; }); }),
            Def(LegP,       (r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.ForsakenWitch || pl.SoulTether) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Soul Tether","your curse groups have no link limit — bind the whole horde into one shared web of pain", p=>{ p.SoulTether=true; p.MaxLinks=99; }); }),   // (NEW legendary)
            Def(LegP,       (r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.ForsakenWitch || pl.WitheringPresence) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Withering Presence","your very presence curses and rots every foe near you, steadily draining their health", p=>p.WitheringPresence=true); }),   // (NEW legendary)
            Def(LegP,       (r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.ForsakenWitch || pl.CurseBonusType2>=0) return new UpgradeCard { Rarity=r, Hidden=true }; return AttuneCard(r, 2, "Cursebrand", "choose a 2nd damage type — cursed foes take your bonus damage from it too, on top of Curse"); }),   // (NEW legendary: opens the element chooser)
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
            // --- NEW legendaries: topping up the older witches to 3 each (fun + a little OP) ---
            Def(LegP,       (r,m)=>{ var pl=Game.I?.Player; if (pl==null || pl.WitchIndex!=0 || pl.S.ComboCap>=20) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Lunar Resonance","your combo climbs far higher and hits much harder (+15 combo cap, +combo power)", p=>{ p.S.ComboCap+=15; p.S.ComboPow+=0.03f; }); }),   // Lunar
            Def(LegP,       (r,m)=>{ var pl=Game.I?.Player; if (pl==null || pl.WitchIndex!=0 || pl.GravityWell) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Gravity Well","a slain foe collapses into a moonlit singularity, dragging nearby enemies together", p=>p.GravityWell=true); }),   // Lunar
            Def(LegP,       (r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.DivineWitch || pl.RadiantMote) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Radiant Ascension","while AIRBORNE your motes lock onto & mend allies (more with combo), passing THROUGH them to strike the foe behind", p=>p.RadiantMote=true); }),   // Divine
            Def(LegP,       (r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.DivineWitch || pl.GuardianAegis) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Guardian's Aegis","+2 Divine Interventions (cheat death) and +15% damage resistance", p=>{ p.GuardianAegis=true; p.Interventions=Mathf.Min(4,p.Interventions+2); p.S.DmgResist=Mathf.Min(0.75f,p.S.DmgResist+0.15f); }); }),   // Divine
            Def(LegP,       (r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.CrimsonWitch || pl.CrimsonFrenzy) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Crimson Frenzy","+15% lifesteal, +15% move speed, +10% crit — a relentless bloodthirsty rush", p=>{ p.CrimsonFrenzy=true; p.S.Lifesteal+=0.15f; p.S.Speed=Mathf.Min(16.5f,p.S.Speed*1.15f); p.S.CritChance=Mathf.Min(1f,p.S.CritChance+0.10f); }); }),   // Crimson
            Def(LegP,       (r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.CrimsonWitch || pl.Bloodbath) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Bloodbath","every kill bursts blood — mending you and savaging nearby foes", p=>p.Bloodbath=true); }),   // Crimson
            Def(LegP,       (r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.VerdantWitch || pl.AncientGrove) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Ancient Grove","+2 max tree-ents and they grow far faster — command a towering forest", p=>{ p.AncientGrove=true; p.GroveBonusEnts=Mathf.Min(6,p.GroveBonusEnts+2); p.GroveEvery=Mathf.Max(6,p.GroveEvery-4); }); }),   // Verdant
            Def(LegP,       (r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.VerdantWitch || pl.VerdantVitality) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Verdant Vitality","+80 max health and +15% damage resistance — as enduring as the old wood", p=>{ p.VerdantVitality=true; p.S.MaxHp+=80f; p.Hp+=80f; p.S.DmgResist=Mathf.Min(0.75f,p.S.DmgResist+0.15f); }); }),   // Verdant
            Def(LegP,       (r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.VerdantWitch || pl.EntElementChosen) return new UpgradeCard { Rarity=r, Hidden=true }; return AttuneCard(r, 3, "Grafted Element", "choose a damage type — your tree-ents' explosions deal it (with a fitting effect), and they take on its look for the rest of the run"); }),   // (NEW Verdant legendary: opens the element chooser)
            // --- Ember affinity (fire / burn / Living Bomb) (NEW) — Ember had ZERO signature cards ---
            Def(UncRareEpic,(r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.EmberWitch) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Kindling",$"+{Mathf.RoundToInt(20*m)}% burn damage (flame & meteor)", p=>p.EmberBurnMul+=0.2f*m); }),
            Def(UncRareEpic,(r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.EmberWitch) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Cinderreach",$"+{Mathf.RoundToInt(15*m)}% flamethrower reach", p=>p.FlameReachMul+=0.15f*m); }),
            Def(UncRareEpic,(r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.EmberWitch) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Detonator",$"+{Mathf.RoundToInt(25*m)}% Living Bomb blast damage", p=>p.LivingBombMul+=0.25f*m); }),
            Def(RareP,      (r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.EmberWitch) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Pyre Heart","+20% burn damage and +25% Living Bomb blast — feed the fire", p=>{ p.EmberBurnMul+=0.2f; p.LivingBombMul+=0.25f; }); }),
            Def(LegP,       (r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.EmberWitch || pl.EmberInferno) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Living Inferno","your flames rage: +60% burn, +40% flame reach, +50% Living Bomb blast", p=>{ p.EmberInferno=true; p.EmberBurnMul+=0.6f; p.FlameReachMul+=0.4f; p.LivingBombMul+=0.5f; }); }),
            // --- Arcane affinity + legendaries (NEW) ---
            Def(UncRareEpic,(r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.ArcaneWitch) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Amplifier",$"+{Mathf.RoundToInt(20*m)}% arcane spell damage (missiles, chain-lightning & ult)", p=>p.ArcanePowerMul+=0.2f*m); }),
            Def(UncRareEpic,(r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.ArcaneWitch) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Feedback Loop",$"+{Mathf.RoundToInt(6*m)}% of your crit damage healed back", p=>p.ArcaneCritHealBonus+=0.06f*m); }),
            Def(UncRareEpic,(r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.ArcaneWitch) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Overcharged Core",$"+{(2*m):0.0}% crit chance & +{Mathf.RoundToInt(15*m)}% crit damage", p=>{ p.S.CritChance=Mathf.Min(0.6f,p.S.CritChance+0.02f*m); p.S.CritDamage+=0.15f*m; }); }),
            Def(RareP,      (r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.ArcaneWitch) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Arcane Focus","+15% arcane spell damage and +20% crit damage — hone the raw power", p=>{ p.ArcanePowerMul+=0.15f; p.S.CritDamage+=0.2f; }); }),
            Def(LegP,       (r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.ArcaneWitch || pl.ArcaneLiving) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Living Current","raw power courses through you: +50% arcane spell damage, +8% crit-heal, +10% crit chance", p=>{ p.ArcaneLiving=true; p.ArcanePowerMul+=0.5f; p.ArcaneCritHealBonus+=0.08f; p.S.CritChance=Mathf.Min(0.6f,p.S.CritChance+0.1f); }); }),
            Def(LegP,       (r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.ArcaneWitch || pl.ArcaneChainReaction) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Chain Reaction","a foe slain by your arcane bursts in a violent nova, ripping through everything nearby", p=>p.ArcaneChainReaction=true); }),
            Def(LegP,       (r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.ArcaneWitch || pl.ArcanePersistMarks) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Unstable Mind","your Conduit marks no longer burn off when the chain fires — they last until the foe dies, so you can chain the same targets again and again", p=>p.ArcanePersistMarks=true); }),
            Def(new[]{Rarity.Common,Rarity.Uncommon,Rarity.Rare,Rarity.Epic}, (r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.ArcaneWitch) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Lingering Sigils",$"+{(0.7f*m):0.0}s arcane mark duration — your Conduit marks fade slower", p=>p.ArcaneMarkDur+=0.7f*m); }),
            // --- more Lunar affinity (it only had 2 non-legendary picks) ---
            Def(RareP,      (r,m)=>{ var pl=Game.I?.Player; if (pl==null || pl.WitchIndex!=0 || pl.CrescentPierceBonus>=6) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Waxing Gibbous","+2 crescent pierce — your blades tear through more foes in a line", p=>p.CrescentPierceBonus=Mathf.Min(6,p.CrescentPierceBonus+2)); }),
            Def(UncRareEpic,(r,m)=>{ var pl=Game.I?.Player; if (pl==null || pl.WitchIndex!=0) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Moonglow",$"+{Mathf.RoundToInt(5*m)}% Lunar damage", p=>p.LunarBonus+=0.05f*m); }),
            Def(UncRareEpic,(r,m)=>{ var pl=Game.I?.Player; if (pl==null || pl.WitchIndex!=0 || pl.CrescentSizeMul>=2.4f) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Silver Crescent","+25% crescent size — sweep a wider arc", p=>p.CrescentSizeMul=Mathf.Min(2.4f,p.CrescentSizeMul+0.25f)); }),
            // --- more Divine affinity (it only had 2 non-legendary picks) ---
            Def(RareP,      (r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.DivineWitch || pl.Interventions>=4) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Guiding Light","+1 Divine Intervention (an extra cheat-death)", p=>p.Interventions=Mathf.Min(4,p.Interventions+1)); }),
            Def(UncRareEpic,(r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.DivineWitch || pl.S.DmgResist>=0.75f) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Hallowed",$"+{Mathf.RoundToInt(6*m)}% damage resistance — armored in light", p=>p.S.DmgResist=Mathf.Min(0.75f,p.S.DmgResist+0.06f*m)); }),
            Def(UncRareEpic,(r,m)=>{ var pl=Game.I?.Player; if (pl==null || !pl.DivineWitch || pl.BlessBonus>=4f) return new UpgradeCard { Rarity=r, Hidden=true }; return WitchCard(r,"Seraph's Grace","+1s blessing duration — keep the light longer", p=>p.BlessBonus=Mathf.Min(4f,p.BlessBonus+1f)); }),
            // (removed: Primary/Secondary Attunement + the Mystic vendor — re-typing a witch's attacks muddied her identity.
            //  Cursebrand (AttuneSlot 2) is unrelated and stays — it only adds a 2nd curse-bonus type.)
            // ---- right-click charge modifiers (2 slots, 4 max) ----
            Def(AllR,       (r,m)=>ModCard(r,ModType.FrostWall, m, $"raise a frost wall that blocks foes & shatters for area damage after ~{5f+(int)r*1.5f:0}s ({(r==Rarity.Common?1:r==Rarity.Uncommon?2:r==Rarity.Rare?2:r==Rarity.Epic?3:4)} live at once)")),
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
            // ---- ember charged-mods (NEW) — universal, Ember damage, Uncommon→Legendary ----
            Def(UncP,       (r,m)=>ModCard(r,ModType.Meteor,     m, $"call down a meteor where the charge lands (~{Mathf.RoundToInt(6+m)}-unit blast + burn)")),
            Def(UncP,       (r,m)=>ModCard(r,ModType.Eruption,   m, r>=Rarity.Rare ? $"erupt molten rock + a flame ring — knock foes back and fling the small ones skyward" : $"erupt molten rock + a flame ring that knocks foes back")),
            Def(UncRareEpic,(r,m)=>ModCard(r,ModType.FrostNova,  m, $"burst a frost nova: damage, +{(1f+0.5f*m):0.0} freeze stacks & a slow around the impact")),   // (NEW Frost)
            Def(UncRareEpic,(r,m)=>ModCard(r,ModType.Spore,      m, $"release a spore cloud ({Mathf.RoundToInt(6+m)}-unit) that poisons foes for ~4s")),   // (NEW Nature)
            Def(UncRareEpic,(r,m)=>ModCard(r,ModType.Cursefield, m, $"open a cursed field ({Mathf.RoundToInt(5.5f+m)}-unit): marks & slows foes inside ~5s")),   // (NEW Curse)
            Def(RareP,      (r,m)=>ModCard(r,ModType.Moonfall,   m, $"call down a Lunar nova ({Mathf.RoundToInt(6.5f+m)}-unit) — can crit & briefly slows")),   // (NEW Lunar)
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
            // --- Curse finishers (NEW) — universal, but they lean into the curse fantasy ---
            Def(ComUncRare, (r,m)=>FinCard(r,FinType.SoulReap,  r==Rarity.Common?8:(r==Rarity.Uncommon?7:6), 0.9f+m*0.22f, "a reaping curse-nova — bites harder the more wounded a foe is, and siphons souls to mend you")),
            Def(UncRareEpic,(r,m)=>FinCard(r,FinType.HexChains, r==Rarity.Uncommon?7:(r==Rarity.Rare?6:5), 0.9f+m*0.22f, "bind nearby foes in a shared-pain web — damage to any one bleeds across the rest")),
            Def(EpicP,      (r,m)=>FinCard(r,FinType.DoomSigil, r==Rarity.Epic?6:5, 0.9f+m*0.24f, "brand nearby foes, then a delayed cursed detonation — the more branded, the bigger the blast")),
            // --- Ember finishers (NEW) — universal, the fire fantasy; each spans the whole rarity hierarchy ---
            Def(AllR, (r,m)=>FinCard(r,FinType.FireWall,    r==Rarity.Common?8:(r==Rarity.Uncommon?7:(r==Rarity.Rare?6:(r==Rarity.Epic?5:4))), 0.9f+m*0.22f, "raise a ring of fire that eats incoming enemy projectiles and burns foes standing in it")),
            Def(AllR, (r,m)=>FinCard(r,FinType.Fireball,    r==Rarity.Common?8:(r==Rarity.Uncommon?7:(r==Rarity.Rare?6:(r==Rarity.Epic?5:4))), 0.9f+m*0.22f, "hurl a fireball at your cursor — a heavy direct hit plus a medium blast on impact")),
            Def(AllR, (r,m)=>FinCard(r,FinType.EmberFervor, r==Rarity.Common?8:(r==Rarity.Uncommon?7:(r==Rarity.Rare?6:(r==Rarity.Epic?5:4))), 0.9f+m*0.22f, "ignite: a burst of crit chance + move speed for a few seconds; your fists and feet blaze")),
            // --- Arcane finishers + modifiers (NEW) — universal ---
            Def(AllR, (r,m)=>FinCard(r,FinType.ArcaneBlink, r==Rarity.Common?8:(r==Rarity.Uncommon?7:(r==Rarity.Rare?6:(r==Rarity.Epic?5:4))), 0.9f+m*0.22f, $"blink to your reticle (~{Mathf.RoundToInt(6+(int)r*3.5f)}u) — an arcane rift erupts where you left AND where you land, blasting a moment later")),
            Def(AllR, (r,m)=>FinCard(r,FinType.ArcaneBlast, r==Rarity.Common?8:(r==Rarity.Uncommon?7:(r==Rarity.Rare?6:(r==Rarity.Epic?5:4))), 0.9f+m*0.22f, "unleash a wide torrent of raw arcane in a broad line — hits everything in front and hurls them back")),
            Def(AllR, (r,m)=>ModCard(r,ModType.ArcaneVortex, m, $"tear open a swirling arcane vortex ({Mathf.RoundToInt(5+m)}-unit) that slows + grinds foes inside")),
            Def(AllR, (r,m)=>ModCard(r,ModType.ArcStorm,    m, $"loose arcane chain-lightning at a random foe in sight — forks {2+(int)r/2}x")),
            Def(LegP,       (r,m)=>Card(r,"Coven's Reach", "+1 charged-modifier slot (max 3)",               p=>{ if(p.S.ModSlots<3) p.S.ModSlots++; })),
            // ---- blessings ----
            Def(AllR,       (r,m)=>Card(r,"Featherfall",   $"+{Mathf.RoundToInt(8*m)}% jump height",          p=>p.S.JumpMul += 0.08f*m)),
            Def(AllR,       (r,m)=>Card(r,"Lodestone Heart", $"+{(0.9f*m):0.0}u XP-orb pickup range",          p=>p.S.PickupRange += 0.9f*m)),
            Def(AllR,       (r,m)=>Card(r,"Warded Skin",   $"+{Mathf.RoundToInt(4*m)}% damage resistance",     p=>p.S.DmgResist = Mathf.Min(0.75f, p.S.DmgResist + 0.04f*m))),
            // ---- crit / spell sizing / luck (small, gradual) ----
            Def(AllR,       (r,m)=>Card(r,"Keen Eye",      $"+{Mathf.RoundToInt(1*m)}% crit chance (direct hits)", p=>p.S.CritChance = Mathf.Min(0.6f, p.S.CritChance + 0.01f*m))),
            Def(AllR,       (r,m)=>Card(r,"Cruel Edge",    $"+{Mathf.RoundToInt(8*m)}% crit damage",            p=>p.S.CritDamage += 0.08f*m)),
            Def(AllR,       (r,m)=>Card(r,"Far Sight",     $"+{Mathf.RoundToInt(4*m)}% spell range",            p=>p.S.SpellRange = Mathf.Min(2.5f, p.S.SpellRange + 0.04f*m))),
            Def(AllR,       (r,m)=>Card(r,"Widening Hex",  $"+{Mathf.RoundToInt(4*m)}% spell area",             p=>p.S.SpellArea = Mathf.Min(2.5f, p.S.SpellArea + 0.04f*m))),
            Def(AllR,       (r,m)=>Card(r,"Black Cat",     $"+{Mathf.RoundToInt(3*m)}% luck — rarer cards AND better chest odds (more gold/armor/lodestones, fewer ambushes)", p=>p.S.Luck += 0.03f*m)),
            // ---- minor passive auto-finishers (no slot, stack infinitely) ----
            Def(AllR,       (r,m)=>MinorCard(r, (MinorType)(int)(GD.Randi() % 18))),
            Def(AllR,       (r,m)=>MinorCard(r, (MinorType)(int)(GD.Randi() % 18))),
        };
        _defs.AddRange(WitchLadder());   // (NEW) per-witch Common/Uncommon/Rare affinity cards for the Coven effigy
    }

    // (NEW) low-rarity WITCH-SPECIFIC affinity cards, so the Coven effigy has a proper Common/Uncommon/Rare spread of your
    // witch's flavour — not just her handful of Legendary signatures. Each scales YOUR element's damage (ElemDmgMul), and the
    // 2nd/3rd of each set combine in a signature stat (crit / a witch-appropriate stat). Gated to the witch (Hidden otherwise).
    private static UpgradeDef WCard(Func<Player, bool> isWitch, string title, Func<float, string> desc, Action<Player, float> apply)
        => Def(ComUncRare, (r, m) =>
        {
            var pl = Game.I?.Player;
            if (pl == null || !isWitch(pl)) return new UpgradeCard { Rarity = r, Hidden = true };
            return WitchCard(r, title, desc(m), p => apply(p, m));
        });

    private static List<UpgradeDef> WitchLadder()
    {
        var l = new List<UpgradeDef>();
        void Witch(Func<Player, bool> w, string el, string n1, string n2, string n3, string sigDesc, Action<Player, float> sig)
        {
            l.Add(WCard(w, n1, m => $"+{Mathf.RoundToInt(6 * m)}% {el} damage", (p, m) => p.S.Atk *= 1 + 0.06f * m));
            l.Add(WCard(w, n2, m => $"+{Mathf.RoundToInt(4 * m)}% {el} damage & +{Mathf.RoundToInt(2 * m)}% crit", (p, m) => { p.S.Atk *= 1 + 0.04f * m; p.S.CritChance += 0.02f * m; }));
            l.Add(WCard(w, n3, m => $"+{Mathf.RoundToInt(4 * m)}% {el} damage & {sigDesc}", (p, m) => { p.S.Atk *= 1 + 0.04f * m; sig(p, m); }));
        }
        void Hp(Player p, float m) { p.S.MaxHp *= 1 + 0.06f * m; p.Hp = Mathf.Min(p.S.MaxHp, p.Hp + p.S.MaxHp * 0.06f * m); }
        void Area(Player p, float m) => p.S.SpellArea *= 1 + 0.05f * m;
        void Range(Player p, float m) => p.S.SpellRange = Mathf.Min(2.4f, p.S.SpellRange * (1 + 0.06f * m));
        Witch(p => p.WitchIndex == 0,   "Lunar",  "Moonfire",       "Waxing Moon",    "Moonwell",         "+area",       Area);
        Witch(p => p.DivineWitch,       "Holy",   "Radiance",       "Zeal",           "Sanctuary",        "+max health", Hp);
        Witch(p => p.CrimsonWitch,      "Blood",  "Bloodlust",      "Sanguine Edge",  "Hemophage",        "+lifesteal",  (p, m) => p.S.Lifesteal += 0.005f * m);
        Witch(p => p.VerdantWitch,      "Nature", "Overgrowth",     "Bramble",        "Deeproot",         "+max health", Hp);
        Witch(p => p.GaleWitch,         "Wind",   "Gathering Gale", "Cutting Gust",   "Slipstream",       "+move speed", (p, m) => p.S.Speed = Mathf.Min(16.5f, p.S.Speed * (1 + 0.03f * m)));
        Witch(p => p.FrostWitch,        "Frost",  "Deepchill",      "Frostbite",      "Long Winter",      "+range",      Range);
        Witch(p => p.ForsakenWitch,     "Curse",  "Malediction",    "Wasting Hex",    "Spreading Blight", "+area",       Area);
        Witch(p => p.EmberWitch,        "Ember",  "Kindling",       "Scorch",         "Wildfire Spread",  "+area",       Area);
        Witch(p => p.ArcaneWitch,       "Arcane", "Arcane Might",   "Focused Bolt",   "Farcast",          "+range",      Range);
        return l;
    }

    // (OVERHAUL) build one ability-upgrade card. Stat paths show as Uncommon frames, evolutions as Epic/Legendary (cosmetic tier).
    private static UpgradeCard ModUpgCard(Player p, ModType t, int path)
    {
        var mp = AbilityUpg.Mods[t][path];
        Rarity r = path < 3 ? Rarity.Uncommon : (path == 3 ? Rarity.Epic : Rarity.Legendary);
        return new UpgradeCard { Rarity = r, AbilityUp = true, Title = $"{ModMeta.Name(t)}: {mp.Name}",
            Desc = $"{(mp.Evo ? "EVOLUTION" : "UPGRADE")} · {mp.Desc} · {p.ModUpg(t, path) + 1}/{Player.UpgCap}", Apply = pl => pl.UpgradeMod(t, path) };
    }
    private static UpgradeCard FinUpgCard(Player p, FinType t, int path)
    {
        var mp = AbilityUpg.Fins[t][path];
        Rarity r = path < 3 ? Rarity.Uncommon : (path == 3 ? Rarity.Epic : Rarity.Legendary);
        return new UpgradeCard { Rarity = r, AbilityUp = true, Title = $"{FinMeta.Name(t)}: {mp.Name}",
            Desc = $"{(mp.Evo ? "EVOLUTION" : "UPGRADE")} · {mp.Desc} · {p.FinUpg(t, path) + 1}/{Player.UpgCap}", Apply = pl => pl.UpgradeFin(t, path) };
    }
    private static float UpgWeight(int path) => path < 3 ? 1f : (path == 3 ? 0.30f : 0.15f);   // stat paths common; Epic evo rarer; Legendary evo rarest
    // every upgrade card the player's equipped converted abilities can still take (path with room). Evolutions weighted rarer.
    private static List<(UpgradeCard card, float w)> AvailableAbilityUpgrades(Player p)
    {
        var list = new List<(UpgradeCard, float)>();
        if (p == null) return list;
        foreach (var mod in p.Mods)
            if (AbilityUpg.IsMod(mod.Type))
                for (int path = 0; path < 5; path++)
                    if (p.ModUpg(mod.Type, path) < Player.UpgCap) list.Add((ModUpgCard(p, mod.Type, path), UpgWeight(path)));
        foreach (var fin in p.Fin)
            if (AbilityUpg.IsFin(fin.Type))
                for (int path = 0; path < 5; path++)
                    if (p.FinUpg(fin.Type, path) < Player.UpgCap) list.Add((FinUpgCard(p, fin.Type, path), UpgWeight(path)));
        return list;
    }
    private static UpgradeCard WeightedPick(List<(UpgradeCard card, float w)> list, RandomNumberGenerator rng)
    {
        float tot = 0f; foreach (var e in list) tot += e.w;
        float x = rng.Randf() * tot;
        foreach (var e in list) { x -= e.w; if (x <= 0f) return e.card; }
        return list[list.Count - 1].card;
    }

    // ===== ADAPTIVE ELEMENT AFFINITY (NEW) =====================================================================
    // Ability offers used to be element-blind: an Ember witch was exactly as likely to be shown a Frost modifier as an
    // Ember one, which made it hard to build toward a fantasy. Rather than hard-locking her to her own element (which
    // would kill builds like "gale ember witch"), the pool WATCHES WHAT YOU'VE BUILT and leans that way.
    //
    // The weights are derived from live state rather than accumulated, which means: nothing to save, nothing to sync in
    // co-op, and swapping an ability out at the peddler immediately re-aims the pool. Take one Gale ability on an Ember
    // witch and gale offers get more common — the game reads that as "she's building gale-ember" and feeds it.
    private const float AffSelf = 2.5f;      // your own witch's element starts elevated
    private const float AffPerOwned = 0.9f;  // …and every ability you OWN of an element pulls that element up
    private const int AffOwnedCap = 3;       // capped so one element can't run away with the whole pool
    private const float AffFloor = 0.25f;    // and nothing ever drops below a 1-in-4 chance — surprises stay possible

    private static float ElemWeight(Player p, DamageType e)
    {
        float w = (p != null && p.WitchDamage == e) ? AffSelf : 1f;
        int owned = 0;
        if (p != null)
        {
            foreach (var m in p.Mods) if (ModMeta.DType(m.Type) == e) owned++;
            foreach (var f in p.Fin) if (FinMeta.DType(f.Type) == e) owned++;
        }
        return w + AffPerOwned * Mathf.Min(owned, AffOwnedCap);
    }

    // should an offer of this element survive? Accepted in proportion to its weight against the strongest element, so
    // the element you're leaning into always passes and the rest thin out gracefully instead of vanishing.
    private static bool ElemPass(Player p, DamageType e, RandomNumberGenerator rng)
    {
        if (p == null) return true;
        float best = 0f;
        foreach (DamageType t in Enum.GetValues(typeof(DamageType))) { float w = ElemWeight(p, t); if (w > best) best = w; }
        if (best <= 0.001f) return true;
        return rng.Randf() < Mathf.Max(AffFloor, ElemWeight(p, e) / best);
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
            if (BlockedRarity(p, card)) continue;  // (NEW) never offer a modifier at ≤ the rarity you already own
            if (card.ModKind.HasValue && (!AbilityUpg.IsMod(card.ModKind.Value) || p.OwnsModifier(card.ModKind.Value))) continue;   // only CONVERTED, unowned mods surface as new abilities
            if (card.ModKind.HasValue)   // (OVERHAUL) new-ability taper: fewer new mods as your mod slots fill, none when full — then it's all "deepen what you have"
            {
                int modFree = p.S.ModSlots - p.Mods.Count;
                if (modFree <= 0 || rng.Randf() >= Mathf.Clamp(0.32f + 0.22f * modFree, 0f, 0.85f)) continue;   // (TUNE) offer new mods more readily while you still have open slots to fill
                if (modFree <= 1 && !ElemPass(p, ModMeta.DType(card.ModKind.Value), rng)) continue;   // element-lean ONLY once you're nearly full; while filling slots, let any ability fit
                card.Rarity = Rarity.Common;   // a FOUND ability always shows one consistent Common frame — power comes from the upgrade tree
            }
            if (outl.Exists(c => c.Title == card.Title)) continue;
            outl.Add(card);
        }
        // spell combos can still surface during normal upgrades — boosted until you own a usable one
        int usable = p.Fin.FindAll(f => !FinMeta.Passive(f.Type)).Count;
        int finFree = p.S.FinSlots - p.Fin.Count;   // (OVERHAUL) new-ability taper: no new finishers once the slots are full
        float finChance = finFree <= 0 ? 0f : (usable == 0 ? 0.62f : Mathf.Clamp(0.24f + 0.16f * finFree, 0f, 0.6f));   // (TUNE) offer new finishers more while slots are open — fill the kit faster
        if (outl.Count == 3 && rng.Randf() < finChance)
        {
            var fin = RollFinisher(p, rng, luck);
            if (fin != null && fin.FinKind.HasValue && (!AbilityUpg.IsFin(fin.FinKind.Value) || p.Fin.Exists(f => f.Type == fin.FinKind.Value))) fin = null;   // only CONVERTED, unowned finishers offered as new abilities
            if (fin != null && fin.FinKind.HasValue && finFree <= 1 && !ElemPass(p, FinMeta.DType(fin.FinKind.Value), rng)) fin = null;   // element-lean only when nearly full; while filling slots, let any finisher through
            if (fin != null) fin.Rarity = Rarity.Common;   // found abilities show one consistent Common frame — power = the upgrade tree
            if (fin != null && !Banned.Contains(fin.Title)) { fin.Unique = fin.FinKind.HasValue && false; outl[rng.RandiRange(0, 2)] = fin; }
        }
        // witch-affinity: occasionally surface the active witch's own SIGNATURE stat cards. (TUNE) dropped 0.10 → 0.02
        // base — it was showing up too often — plus a small Luck lean so a lucky build still sees its signatures a touch more.
        if (outl.Count == 3 && rng.Randf() < Mathf.Clamp(0.02f + luck * 0.03f, 0f, 0.12f))
        {
            var a = RollWitchAffinity(p, rng);
            // (TESTING) affinity surfaces the current witch's signature cards — but a mod/finisher card must be CONVERTED & unowned,
            // else an un-reworked witch (e.g. Gale's Wind kit) leaks its old-system abilities back into the pick-3.
            if (a != null && a.ModKind.HasValue && (!AbilityUpg.IsMod(a.ModKind.Value) || p.OwnsModifier(a.ModKind.Value) || (p.S.ModSlots - p.Mods.Count) <= 0)) a = null;   // (OVERHAUL) no new mods when slots are full
            if (a != null && a.FinKind.HasValue && (!AbilityUpg.IsFin(a.FinKind.Value) || p.Fin.Exists(f => f.Type == a.FinKind.Value) || (p.S.FinSlots - p.Fin.Count) <= 0)) a = null;
            if (a != null && (a.ModKind.HasValue || a.FinKind.HasValue)) a.Rarity = Rarity.Common;   // found abilities show one consistent Common frame
            if (a != null && !Banned.Contains(a.Title) && !outl.Exists(c => c.Title == a.Title)) outl[rng.RandiRange(0, 2)] = a;
        }
        // (OVERHAUL) equipped CONVERTED abilities inject their own upgrade cards — the dominant "deepen what you have" bucket.
        // Usually 1-2 of the 3 slots become upgrade offers when you've committed to a converted ability; leaves a slot for blessings.
        if (outl.Count == 3)
        {
            int equipped = 0;
            foreach (var mod in p.Mods) if (AbilityUpg.IsMod(mod.Type)) equipped++;
            foreach (var fin in p.Fin) if (AbilityUpg.IsFin(fin.Type)) equipped++;
            var avail = AvailableAbilityUpgrades(p);
            avail.RemoveAll(e => Banned.Contains(e.card.Title));   // (NEW) a disabled ability-upgrade path never re-offers
            int emptyAbil = (p.S.FinSlots - p.Fin.Count) + (p.S.ModSlots - p.Mods.Count);
            float pInject = Mathf.Clamp(0.42f + 0.18f * equipped - (emptyAbil > 0 ? 0.22f : 0f), 0f, 0.92f);   // (TUNE) while you still have empty ability slots, fewer upgrade injections so NEW abilities aren't crowded out
            int want = (avail.Count > 0 && rng.Randf() < pInject) ? ((equipped >= 2 && rng.Randf() < 0.5f) ? 2 : 1) : 0;
            for (int k = 0; k < want && avail.Count > 0; k++)
            {
                var pick = WeightedPick(avail, rng);
                avail.RemoveAll(e => e.card.Title == pick.Title);
                if (outl.Exists(c => c.Title == pick.Title)) continue;
                int slot = -1; for (int s = 0; s < 3; s++) if (!outl[s].AbilityUp) { slot = s; break; }   // don't overwrite an already-injected upgrade
                if (slot < 0) break;
                outl[slot] = pick;
            }
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

    private static UpgradeCard RollFinisher(Player p, RandomNumberGenerator rng, float luck)
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
            if (BlockedRarity(p, card)) continue;                // (NEW) never offer a finisher at ≤ the rarity you already own
            return card;
        }
        return null;
    }
    // (TESTING) transitional gate shared by every roll path: during the overhaul only CONVERTED & unowned mod/finisher cards
    // may surface as new abilities. Un-reworked witches (e.g. Frost-player still seeing Gale's Wind kit) stay out of the pool
    // until their handlers are stack-driven. Blessings/character cards (no ModKind/FinKind) are always allowed.
    private static bool NotConvertedYet(Player p, UpgradeCard card)
    {
        // a new ability can't surface if it's non-converted, already owned, OR its slot type is full (swap via the vendor instead).
        if (card.ModKind.HasValue) return !AbilityUpg.IsMod(card.ModKind.Value) || p.OwnsModifier(card.ModKind.Value) || p.Mods.Count >= p.S.ModSlots;
        if (card.FinKind.HasValue) return !AbilityUpg.IsFin(card.FinKind.Value) || p.Fin.Exists(f => f.Type == card.FinKind.Value) || p.Fin.Count >= p.S.FinSlots;
        return false;
    }

    // (EFFIGY) the universal-blessing themes each effigy guarantees, matched by card Title.
    public static readonly System.Collections.Generic.HashSet<string> ThemeSurvival = new() { "Heartwood", "Old Blood", "Full Heal", "Moonglass Aegis", "Swift Mending", "Quickening Ward", "Siphon", "Warded Skin", "Bulwark" };
    public static readonly System.Collections.Generic.HashSet<string> ThemePower    = new() { "Witchfire", "Cruel Edge", "Far Sight", "Widening Hex", "Piercing Sigil", "Swift Conjury", "Focus", "Overcharge", "Hex Tempo" };
    public static readonly System.Collections.Generic.HashSet<string> ThemeChance    = new() { "Keen Eye", "Black Cat" };
    public static readonly System.Collections.Generic.HashSet<string> ThemeMovement  = new() { "Quicksilver", "Featherfall", "Wind Step", "Fleet Step", "Twin Step", "Lodestone Heart" };

    // (EFFIGY) roll `count` guaranteed cards of an effigy theme. kind: 0 survival · 1 power · 2 chance · 3 movement · 4 witch-specific.
    // Rarities still roll normally (Epic/Legendary-only cards stay that way); cap/own guards on the cards self-limit.
    // Offers are deduped by Title+Rarity so a small theme (e.g. Chance's 2 cards) can still fill 3 slots at different tiers.
    public static List<UpgradeCard> RollEffigy(Player p, RandomNumberGenerator rng, int kind, int count)
    {
        if (_defs == null) Build();
        float luck = p?.S?.Luck ?? 0f;
        var outl = new List<UpgradeCard>();
        if (kind == 4)   // witch-specific ("coven"): the active witch's own cards, now rarity-WEIGHTED — mostly her low-rarity
        {                //  flavour; her Legendary signatures + the ult-mod only surface on an actual Legendary roll (like anywhere else)
            var aff = new List<UpgradeDef>();
            foreach (var d in _defs) { var probe = d.Make(d.Rars[0], Rarities.Mag(d.Rars[0])); if (probe.Affinity && !probe.Hidden) aff.Add(d); }
            var um = (p != null && p.Ult != Player.UltKind.None) ? UltModCard(p) : null;   // always Legendary — no longer a guaranteed lead
            int g = 0;
            while (outl.Count < count && g++ < 400)
            {
                var r = Rarities.Roll(rng, luck);
                if (r == Rarity.Legendary && um != null && !Banned.Contains(um.Title) && !outl.Exists(c => c.Title == um.Title) && rng.Randf() < 0.5f) { outl.Add(um); continue; }   // ult-mod is just a Legendary candidate now
                var cands = aff.FindAll(d => System.Array.IndexOf(d.Rars, r) >= 0);
                if (cands.Count == 0) continue;   // this witch has no card at the rolled rarity → reroll
                var card = cands[rng.RandiRange(0, cands.Count - 1)].Make(r, Rarities.Mag(r));
                if (card.Hidden || Banned.Contains(card.Title) || outl.Exists(c => c.Title == card.Title && c.Rarity == card.Rarity)) continue;   // dedupe by title+rarity
                outl.Add(card);
            }
            return outl;
        }
        var theme = kind == 0 ? ThemeSurvival : kind == 1 ? ThemePower : kind == 2 ? ThemeChance : ThemeMovement;
        int guard = 0;
        while (outl.Count < count && guard++ < 600)
        {
            var r = Rarities.Roll(rng, luck);
            var pool = _defs.FindAll(d => System.Array.IndexOf(d.Rars, r) >= 0);
            if (pool.Count == 0) continue;
            var card = pool[rng.RandiRange(0, pool.Count - 1)].Make(r, Rarities.Mag(r));
            if (card.Hidden || Banned.Contains(card.Title)) continue;
            if (!theme.Contains(card.Title)) continue;
            if (outl.Exists(c => c.Title == card.Title && c.Rarity == card.Rarity)) continue;   // dedupe by title+rarity
            outl.Add(card);
        }
        return outl;
    }

    public static List<UpgradeCard> RollCategory(Player p, RandomNumberGenerator rng, int cat, int count)
    {
        if (_defs == null) Build();
        if (p != null && cat == 1 && p.Fin.Count >= p.S.FinSlots) cat = 0;   // (OVERHAUL) finisher slots full → give a blessing instead of an un-equippable ability
        if (p != null && cat == 2 && p.Mods.Count >= p.S.ModSlots) cat = 0;
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
            if (card.Hidden || Banned.Contains(card.Title)) continue;   // (NEW) respect disabled families here too
            bool ok = cat == 0 ? (!card.FinKind.HasValue && !card.ModKind.HasValue && card.AttuneSlot < 0)
                    : cat == 1 ? card.FinKind.HasValue
                    : card.ModKind.HasValue;
            if (!ok) continue;
            if (NotConvertedYet(p, card)) continue;   // (TESTING) skip non-converted abilities during the overhaul
            if (card.ModKind.HasValue || card.FinKind.HasValue) card.Rarity = Rarity.Common;   // (OVERHAUL) found abilities show one consistent Common frame
            if (BlockedRarity(p, card)) continue;   // (NEW) never offer a finisher/modifier at ≤ the rarity you already own
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
            if (card.Hidden || Banned.Contains(card.Title)) continue;   // (NEW) respect disabled families here too
            if (NotConvertedYet(p, card)) continue;   // (TESTING) skip non-converted abilities during the overhaul
            if (card.ModKind.HasValue || card.FinKind.HasValue) card.Rarity = Rarity.Common;   // (OVERHAUL) found abilities show one consistent Common frame
            if (BlockedRarity(p, card)) continue;   // (NEW) never offer a finisher/modifier at ≤ the rarity you already own
            if (outl.Exists(c => c.Title == card.Title)) continue;
            outl.Add(card);
        }
        return outl;
    }
}
