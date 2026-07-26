using Godot;

// DamageType.cs — the element system spine. The DamageType enum is referenced everywhere (witch
// primaries/secondaries, bolts, finishers, enemy resistances). DamageTypes provides display Name()
// and Col() (the canonical per-element color used by VFX, models, popups, and HUD). Add an element
// here first if you need a genuinely new damage school; most new content reuses existing types.
public enum DamageType { Lunar, Arcane, Nature, Frost, Curse, Holy, Ember, Physical, Blood, Wind }

public static class DamageTypes
{
    public static string Name(DamageType t) => t.ToString();

    public static Color Col(DamageType t) => t switch {
        DamageType.Lunar    => new Color(0.91f, 0.89f, 1.00f),
        DamageType.Arcane   => new Color(0.52f, 0.24f, 0.98f),   // deep saturated violet (was a pale neon lavender that bloomed to white)
        DamageType.Nature   => new Color(0.37f, 0.89f, 0.60f),
        DamageType.Frost    => new Color(0.55f, 0.85f, 1.0f),
        DamageType.Curse    => new Color(0.82f, 0.36f, 0.90f),
        DamageType.Holy     => new Color(1.0f, 0.93f, 0.70f),
        DamageType.Ember    => new Color(1.0f, 0.808f, 0.42f),
        DamageType.Physical => new Color(0.80f, 0.80f, 0.86f),
        DamageType.Blood    => new Color(0.78f, 0.07f, 0.12f),
        DamageType.Wind     => new Color(0.70f, 0.96f, 0.88f),
        _ => Colors.White };
}
