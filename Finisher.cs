using Godot;

// Finisher.cs — FinType enum + FinMeta display metadata for spell-combo finishers. A finisher fires
// automatically every Nth combo cast and is WITCH-AGNOSTIC (any witch can equip any finisher).
// Granted by a FinCard (Upgrade.cs), equipped via Player.EquipFinisher, and EXECUTED in the big
// switch in Player.ExecuteFinisher (each case calls a Fin* method + an arm pose). To add one:
// extend this enum + FinMeta, add the execution case in Player, add a FinCard Def in UpgradePool.
public enum FinType { Wave, Beam, Volley, Fullmod, Heal, Root, Swarm, HexField, Crescendo, Halo, Lance, BloodNova, CrimsonRush, BloodCurse, PoisonField, SeedMine, ThornSkin, Updraft, WindRush, WindSlice, IceSpike, FrostVault, FrostWalls, SoulReap, HexChains, DoomSigil, FireWall, Fireball, EmberFervor, LunarNova, CrescentStorm, ArcaneBlink, ArcaneBlast }   // ArcaneBlink/ArcaneBlast = Arcane (NEW)

public static class FinMeta
{
    public static string Name(FinType t) => t switch {
        FinType.Wave => "Hex Pulse",
        FinType.Beam => "Spelllance",
        FinType.Volley => "Spellstorm",
        FinType.Fullmod => "Witching Hour",
        FinType.Heal => "Mending Grove",
        FinType.Root => "Snare Verse",
        FinType.Swarm => "Coven Swarm",
        FinType.HexField => "Witch's Hollow",
        FinType.Crescendo => "Crescendo",
        FinType.Halo => "Radiant Halo",
        FinType.Lance => "Heaven's Lances",
        FinType.BloodNova => "Blood Nova",
        FinType.CrimsonRush => "Crimson Rush",
        FinType.BloodCurse => "Blood Curse",
        FinType.PoisonField => "Creeping Blight",
        FinType.SeedMine => "Seed Mines",
        FinType.ThornSkin => "Thorn Skin",
        FinType.Updraft => "Updraft",
        FinType.WindRush => "Wind Rush",
        FinType.WindSlice => "Wind Slice",
        FinType.IceSpike => "Ice Spikes",
        FinType.FrostVault => "Frost Vault",
        FinType.FrostWalls => "Glacial Vise",
        FinType.SoulReap => "Soul Reap",
        FinType.HexChains => "Hex Chains",
        FinType.DoomSigil => "Doom Sigil",
        FinType.FireWall => "Ring of Fire",
        FinType.Fireball => "Fireball",
        FinType.EmberFervor => "Ember Fervor",
        FinType.LunarNova => "Lunar Nova",
        FinType.CrescentStorm => "Crescent Storm",
        FinType.ArcaneBlink => "Arcane Blink",
        FinType.ArcaneBlast => "Arcane Torrent",
        _ => "?" };

    public static bool Passive(FinType t) => t == FinType.Crescendo;   // no key-press, but still occupies a slot

    // a one-line "what does this spell do" used by hover tooltips on the swap / grimoire screens
    public static string Desc(FinType t) => t switch {
        FinType.Wave => "A ring of hexing force pulses outward, damaging and cursing everything around you.",
        FinType.Beam => "Channels a piercing lance of arcane energy straight ahead for a few seconds.",
        FinType.Volley => "Looses a storm of aimed bolts at the nearest foes.",
        FinType.Fullmod => "Fires a full-power charged cast carrying every charge-modifier you own at once.",
        FinType.Heal => "Grows a mending grove at your feet — heals you over time and sears foes inside it.",
        FinType.Root => "Roots every nearby enemy in place and deals a burst of Nature damage.",
        FinType.Swarm => "Summons a swarm of homing spectral bolts that chase down foes.",
        FinType.HexField => "Opens a cursed hollow that damages and weakens enemies standing in it.",
        FinType.Crescendo => "Passive: every Nth combo cast erupts on its own for bonus lunar damage.",
        FinType.Halo => "A radiant nova that sears nearby foes, heals you, and blesses you.",
        FinType.Lance => "Calls down holy lances across a swath of ground, leaving healing light where they fall.",
        FinType.BloodNova => "A close blood detonation — strong damage and knockback; kills feed your blood.",
        FinType.CrimsonRush => "Dash forward on a blood wave, bowling over and slowing everything in your path.",
        FinType.BloodCurse => "A cone of misty blood that hexes foes; each hex banks a stack (Crimson) or mends you.",
        FinType.PoisonField => "Drops a creeping poison field that keeps stacking poison the longer foes stand in it.",
        FinType.SeedMine => "Scatters proximity seed-mines that blast foes who wander too close.",
        FinType.ThornSkin => "Banks a bark shield (up to 3). Each charge eats one hit, then bursts for Nature damage.",
        FinType.Updraft => "Launch straight up and carry nearby small/medium foes aloft with you — set up air combos.",
        FinType.WindRush => "Dash forward on a gust, lightly damaging and flinging foes aside; ~50% chance to refund your dashes if it connects.",
        FinType.WindSlice => "Hurl a travelling X of wind that cuts through and damages every foe in its path.",
        FinType.IceSpike => "Erupt a cone of ice spikes ahead of you — damages foes and flings the small/medium ones skyward.",
        FinType.FrostVault => "Kick off an icicle to vault up and back to safety; the icicle bursts, slowing the foes it leaves behind.",
        FinType.FrostWalls => "Clap two ice walls together in front of you, crushing the foes between them for a chunk of their max health.",
        FinType.SoulReap => "A reaping curse-nova that bites harder the more wounded each foe is — and siphons their souls to mend you.",
        FinType.HexChains => "Binds nearby foes in cursed chains: for a few seconds a share of ALL damage any of them takes bleeds to the rest.",
        FinType.DoomSigil => "Brands nearby foes with a doom sigil that detonates a moment later for heavy Curse damage — the more branded, the bigger the blast.",
        FinType.FireWall => "Raise a ring of fire around you for a few seconds — it eats incoming enemy projectiles (puff + crackle) and burns anything standing in it.",
        FinType.Fireball => "Hurl a fireball at your cursor — a heavy direct hit, plus a medium blast where it lands.",
        FinType.EmberFervor => "Ignite yourself: a burst of crit chance and move speed for a few seconds (both scale with rarity). Fists and feet blaze while it lasts; can't recharge until it fades.",
        FinType.LunarNova => "A nova of moonlight erupts around you — heavy Lunar damage and a slow to everything nearby.",
        FinType.CrescentStorm => "Looses a storm of homing crescent blades that arc out and scythe through nearby foes.",
        FinType.ArcaneBlink => "Blink the way you're moving in a flash of raw arcane (reach grows with rarity, ~9→23u). An arcane rift erupts where you left AND where you land, detonating a moment later for area damage.",
        FinType.ArcaneBlast => "Unleash a wide torrent of raw arcane straight ahead — hits everything in a broad line and hurls them back.",
        _ => "" };

    public static DamageType DType(FinType t) => t switch {
        FinType.Wave => DamageType.Curse,
        FinType.Beam => DamageType.Arcane,
        FinType.Volley => DamageType.Arcane,
        FinType.Fullmod => DamageType.Lunar,
        FinType.Heal => DamageType.Holy,
        FinType.Root => DamageType.Nature,
        FinType.Swarm => DamageType.Arcane,
        FinType.HexField => DamageType.Curse,
        FinType.Crescendo => DamageType.Lunar,
        FinType.Halo => DamageType.Holy,
        FinType.Lance => DamageType.Holy,
        FinType.BloodNova => DamageType.Blood,
        FinType.CrimsonRush => DamageType.Blood,
        FinType.BloodCurse => DamageType.Blood,
        FinType.PoisonField => DamageType.Nature,
        FinType.SeedMine => DamageType.Nature,
        FinType.ThornSkin => DamageType.Nature,
        FinType.Updraft => DamageType.Wind,
        FinType.WindRush => DamageType.Wind,
        FinType.WindSlice => DamageType.Wind,
        FinType.IceSpike => DamageType.Frost,
        FinType.FrostVault => DamageType.Frost,
        FinType.FrostWalls => DamageType.Frost,
        FinType.SoulReap => DamageType.Curse,
        FinType.HexChains => DamageType.Curse,
        FinType.DoomSigil => DamageType.Curse,
        FinType.FireWall => DamageType.Ember,
        FinType.Fireball => DamageType.Ember,
        FinType.EmberFervor => DamageType.Ember,
        FinType.LunarNova => DamageType.Lunar,
        FinType.CrescentStorm => DamageType.Lunar,
        FinType.ArcaneBlink => DamageType.Arcane,
        FinType.ArcaneBlast => DamageType.Arcane,
        _ => DamageType.Arcane };

    public static Color Col(FinType t) => DamageTypes.Col(DType(t));
}

// an equipped finisher: charges as you build combo, arms, then fires on its key
public class FinisherSlot
{
    public FinType Type;
    public Rarity Rarity;
    public float Pow = 1f;
    public int Every = 6;
    public int Charge = 0;
    public bool Armed = false;
    public float Window = 0f;
    public Key Bind = Key.None;   // key bound to fire this finisher
    public float NotReadyFlash = 0f;   // >0 briefly when you press its key while it's still charging
    public int[] Stat = new int[3];   // (OVERHAUL) per-ability upgrade tree: 3 stat paths, each stacks 0-4
    public int[] Evo = new int[2];    // 2 evolutions, each stacks 0-4 (unlock → amplify)
}
