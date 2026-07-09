using Godot;

// Modifier.cs — ModType enum + ModMeta metadata for CHARGED-CAST MODIFIERS (riders that fire on your
// charged release: chill/root/blast/ground/etc.). Granted by a ModCard (Upgrade.cs), equipped via
// Player.EquipModifier, and applied in Player.ApplyChargedMods (switch on m.Type, reading magnitude
// from the equipped Modifier). To add one: extend this enum + ModMeta, handle it in ApplyChargedMods,
// add a ModCard Def in UpgradePool. Witch-agnostic, like finishers. See DEV_GUIDE.md §6.3.
public enum ModType { Frost, Bramble, Sunder, HexMark, Moonbeam, Consecrate, Smite, Hemorrhage, CrimsonPool, SanguineSpikes, Implosion, Whirlwind, Meteor, Eruption }   // Implosion/Whirlwind = Wind; Meteor/Eruption = Ember (NEW)

public static class ModMeta
{
    public static string Name(ModType t) => t switch {
        ModType.Frost => "Frost Veil",
        ModType.Bramble => "Bramble Root",
        ModType.Sunder => "Sunder Burst",
        ModType.HexMark => "Hex Mark",
        ModType.Moonbeam => "Moonwell Beam",
        ModType.Consecrate => "Consecrated Ground",
        ModType.Smite => "Smite",
        ModType.Hemorrhage => "Hemorrhage",
        ModType.CrimsonPool => "Crimson Pool",
        ModType.SanguineSpikes => "Sanguine Spikes",
        ModType.Implosion => "Implosion",
        ModType.Whirlwind => "Whirlwind",
        ModType.Meteor => "Meteor Strike",
        ModType.Eruption => "Eruption",
        _ => "?" };

    // hover-tooltip description for the swap / grimoire screens
    public static string Desc(ModType t) => t switch {
        ModType.Frost => "Charged casts chill and slow whatever they strike.",
        ModType.Bramble => "Charged casts entangle, briefly rooting foes they hit.",
        ModType.Sunder => "Charged casts erupt on impact for bonus Ember splash damage.",
        ModType.HexMark => "Charged casts mark foes; the mark amplifies damage and leaps to nearby enemies.",
        ModType.Moonbeam => "A full charge leaves a burning shaft of moonlight on the ground.",
        ModType.Consecrate => "A full charge consecrates the ground, searing foes who stand on it.",
        ModType.Smite => "Charged casts call down a holy smite on the struck foe.",
        ModType.Hemorrhage => "Charged casts inflict bleeding that ticks for damage over time.",
        ModType.CrimsonPool => "A full charge leaves a blood pool that slows foes and banks stacks for you.",
        ModType.SanguineSpikes => "Charged casts loose blood spikes; kills bank a stack (Crimson) or mend others.",
        ModType.Implosion => "A full charge damages the area, then yanks the survivors inward.",
        ModType.Whirlwind => "A full charge spawns a stationary tornado: it grinds foes, and any player can launch off it like a jump pad.",
        ModType.Meteor => "A full charge calls down a meteor where it lands — a heavy Ember blast that stacks burn.",
        ModType.Eruption => "A full charge erupts the ground: molten rock heaves up and a flame ring blasts outward, knocking foes back (higher rarities fling the small ones skyward).",
        _ => "" };

    public static string Tag(ModType t) => t switch {
        ModType.Frost => "FV", ModType.Bramble => "BR", ModType.Sunder => "SB",
        ModType.HexMark => "HX", ModType.Moonbeam => "MW",
        ModType.Consecrate => "CG", ModType.Smite => "SM",
        ModType.Hemorrhage => "HM", ModType.CrimsonPool => "CP", ModType.SanguineSpikes => "SS",
        ModType.Meteor => "MT", ModType.Eruption => "ER", _ => "?" };

    public static DamageType DType(ModType t) => t switch {
        ModType.Frost => DamageType.Frost,
        ModType.Bramble => DamageType.Nature,
        ModType.Sunder => DamageType.Ember,
        ModType.HexMark => DamageType.Curse,
        ModType.Moonbeam => DamageType.Lunar,
        ModType.Consecrate => DamageType.Holy,
        ModType.Smite => DamageType.Holy,
        ModType.Hemorrhage => DamageType.Blood,
        ModType.CrimsonPool => DamageType.Blood,
        ModType.SanguineSpikes => DamageType.Blood,
        ModType.Implosion => DamageType.Wind,
        ModType.Whirlwind => DamageType.Wind,
        ModType.Meteor => DamageType.Ember,
        ModType.Eruption => DamageType.Ember,
        _ => DamageType.Arcane };

    public static Color Col(ModType t) => DamageTypes.Col(DType(t));
}

public class Modifier
{
    public ModType Type;
    public float Mag;
    public Rarity Rarity;
}
