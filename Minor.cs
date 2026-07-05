using Godot;

// "Minor abilities": tiny passive spell-combos that auto-fire every N combo hits.
// They cost nothing, take no traditional slot, and stack infinitely (any witch can collect them).
// Minor.cs — 'minor' run boons (small stacking buffs awarded outside the main card flow). MinorType lists them; TickMinors applies.
public enum MinorType
{
    MoonMote, LunarFlare,       // Lunar
    ArcaneDart, ManaSpark,      // Arcane
    ThornSnap, Sporeling,       // Nature
    FrostNip, IcePrick,         // Frost
    HexWisp, RotTick,           // Curse
    Glimmer, RadiantMote,       // Holy
    Cinder, Ashflare,           // Ember
    Bloodlet, Clot,             // Blood
    Gust, Zephyr                // Wind (NEW)
}

public static class MinorMeta
{
    public static string Name(MinorType t) => t switch {
        MinorType.MoonMote => "Moon Mote", MinorType.LunarFlare => "Lunar Flare",
        MinorType.ArcaneDart => "Arcane Dart", MinorType.ManaSpark => "Mana Spark",
        MinorType.ThornSnap => "Thorn Snap", MinorType.Sporeling => "Sporeling",
        MinorType.FrostNip => "Frost Nip", MinorType.IcePrick => "Ice Prick",
        MinorType.HexWisp => "Hex Wisp", MinorType.RotTick => "Rot Tick",
        MinorType.Glimmer => "Glimmer", MinorType.RadiantMote => "Radiant Mote",
        MinorType.Cinder => "Cinder", MinorType.Ashflare => "Ashflare",
        MinorType.Bloodlet => "Bloodlet", MinorType.Clot => "Clot",
        MinorType.Gust => "Gust", MinorType.Zephyr => "Zephyr",
        _ => "?" };

    public static DamageType DType(MinorType t) => t switch {
        MinorType.MoonMote or MinorType.LunarFlare => DamageType.Lunar,
        MinorType.ArcaneDart or MinorType.ManaSpark => DamageType.Arcane,
        MinorType.ThornSnap or MinorType.Sporeling => DamageType.Nature,
        MinorType.FrostNip or MinorType.IcePrick => DamageType.Frost,
        MinorType.HexWisp or MinorType.RotTick => DamageType.Curse,
        MinorType.Glimmer or MinorType.RadiantMote => DamageType.Holy,
        MinorType.Cinder or MinorType.Ashflare => DamageType.Ember,
        MinorType.Bloodlet or MinorType.Clot => DamageType.Blood,
        MinorType.Gust or MinorType.Zephyr => DamageType.Wind,
        _ => DamageType.Arcane };

    // base combos-per-proc (stacks shorten this)
    public static int Every(MinorType t) => 12;

    public static Color Col(MinorType t) => DamageTypes.Col(DType(t));

    public static string Desc(MinorType t) => t switch {
        MinorType.MoonMote => "every so often, loose a small moon mote at a foe",
        MinorType.LunarFlare => "periodic faint lunar burst around you",
        MinorType.ArcaneDart => "periodic small arcane dart at a foe",
        MinorType.ManaSpark => "periodic tiny arcane burst around you",
        MinorType.ThornSnap => "periodically snare the nearest foe briefly",
        MinorType.Sporeling => "periodic small nature burst around you",
        MinorType.FrostNip => "periodically chill the nearest foe",
        MinorType.IcePrick => "periodic tiny frost burst around you",
        MinorType.HexWisp => "periodically hex the nearest foe",
        MinorType.RotTick => "periodic tiny curse burst around you",
        MinorType.Glimmer => "periodically mend a sliver of your health",
        MinorType.RadiantMote => "periodic small holy mote at a foe",
        MinorType.Cinder => "periodic small ember at a foe",
        MinorType.Ashflare => "periodic tiny ember burst around you",
        MinorType.Bloodlet => "periodic small blood bolt that leeches a little",
        MinorType.Clot => "periodic tiny blood burst; banks a little blood",
        MinorType.Gust => "periodic small wind dart that nudges a foe back",
        MinorType.Zephyr => "periodic tiny wind burst around you",
        _ => "a minor passive spell" };
}

// an owned minor: tracks its own combo charge; more stacks fire sooner and a touch harder
public class MinorSlot
{
    public MinorType Type;
    public int Stacks = 1;
    public int Charge = 0;
    public int Every => Mathf.Max(5, MinorMeta.Every(Type) - (Stacks - 1));
}
