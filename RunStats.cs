// RunStats.cs — per-warden performance tally for the end-of-run scoreboard. Each machine tracks its OWN player's stats
// locally (best-effort in co-op); at game over everyone broadcasts their block so the Over screen shows the whole party.
public class RunStats
{
    public int Kills;             // enemies you landed the final hit on
    public float DamageDealt;     // total damage you dealt this run
    public float BossDamage;      // of that, how much landed on bosses
    public float Healing;         // total healing you did (self + allies: lifesteal, abilities, fields…)
    public int Flings;            // enemies you flung
    public float DamageTaken;     // total damage you took
    public float Highlight;       // witch-signature stat (meaning set by WitchIdx)
    public int WitchIdx;          // which witch (for the highlight label + your row color)
    public int Slot;              // party order (0 = host); used to sort rows
    public int TimesDowned;       // times you were incapacitated
    public int Revives;           // times you revived a fallen ally
    public int BestCombo;         // your biggest combo this run
    public float BiggestHit;      // your single hardest hit

    public static string HighlightLabel(int witch) => witch switch
    {
        1 => "Allies Mended",     // Divine
        2 => "Health Leeched",    // Crimson
        3 => "Ents Detonated",    // Verdant
        4 => "Seconds Aloft",     // Gale
        5 => "Foes Shattered",    // Frost
        6 => "Foes Cursed",       // Forsaken
        7 => "Bombs Detonated",   // Ember
        8 => "Foes Marked",       // Arcane
        _ => "Night Kills",       // Lunar
    };
    public string HighlightValue() => WitchIdx == 4 ? $"{Highlight:0}s" : $"{(int)Highlight}";

    public static string WitchName(int witch) => witch switch
    {
        1 => "Divine", 2 => "Crimson", 3 => "Verdant", 4 => "Gale", 5 => "Frost", 6 => "Forsaken", 7 => "Ember", 8 => "Arcane", _ => "Lunar",
    };
}
