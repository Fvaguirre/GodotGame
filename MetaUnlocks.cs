using Godot;

// MetaUnlocks.cs — the GENERAL (cross-witch) gold meta-tree. Permanent, persisted, applied to EVERY witch at run start.
// Three big-ticket unlocks at 2000 gold each: +1 base finisher slot (2→3), +1 base charged-mod slot (1→2), +1 base mana
// (2→3). The HIGHER tiers (4th/5th finisher, 3rd mod, 4th/5th mana) stay reachable only IN a run via Coven Bond / Coven's
// Reach / Deep Reserve (run-only). Owned flags live in the save file [meta] section, alongside gold + perks.
public static class MetaUnlocks
{
    public const int Cost = 2000;
    public static bool Fin = false, Mod = false, Mana = false;

    public static bool Owned(int i) => i == 0 ? Fin : i == 1 ? Mod : Mana;
    public static string Name(int i) => i == 0 ? "Coven Seat" : i == 1 ? "Focus Sigil" : "Deep Font";
    public static string Desc(int i) => i == 0 ? "+1 permanent finisher slot" : i == 1 ? "+1 permanent charged-mod slot" : "+1 permanent mana";

    public static bool CanBuy(int i, int gold) => !Owned(i) && gold >= Cost;
    public static bool Buy(int i)
    {
        var g = Game.I; if (g == null || Owned(i) || g.Gold < Cost) return false;
        g.Gold -= Cost;
        if (i == 0) Fin = true; else if (i == 1) Mod = true; else Mana = true;
        g.SaveGold();
        return true;
    }
    // applied at run start, after the fresh Stats + witch config + perks — adds to the base
    public static void Apply(Player pl)
    {
        if (pl == null) return;
        if (Fin) pl.S.FinSlots += 1;    // base 2 → 3
        if (Mod) pl.S.ModSlots += 1;    // base 1 → 2
        if (Mana) pl.S.ManaMax += 1f;   // base 2 → 3
    }
    public static void Save(ConfigFile cfg)
    {
        cfg.SetValue("meta", "fin", Fin);
        cfg.SetValue("meta", "mod", Mod);
        cfg.SetValue("meta", "mana", Mana);
    }
    public static void Load(ConfigFile cfg)
    {
        Fin = cfg.GetValue("meta", "fin", false).AsBool();
        Mod = cfg.GetValue("meta", "mod", false).AsBool();
        Mana = cfg.GetValue("meta", "mana", false).AsBool();
    }
}
