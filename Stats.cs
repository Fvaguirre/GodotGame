// All tunable player stats. Defaults mirror the web build.
// Stats.cs — the full per-run stat block. One instance lives at Player.S.
// THIS IS THE FIRST STOP FOR BALANCE. Defaults here are the "base witch"; Game.ConfigureWitch and
// level-up cards (Upgrade.cs) modify from these. Grouped: damage/cast, vitals/defense, mana, crit,
// spell scaling, shield, combo, dash, ability slots, mark. See DEV_GUIDE.md §7 for what each tunes.
public class Stats
{
    public float Atk = 1.0f;
    public float FireCd = 0.28f;
    public float Speed = 9.0f;
    public float ChargeSpeed = 1.4f;
    public float MaxCharge = 3.0f;
    public int   Pierce = 0;
    public float MaxHp = 100f;
    public float Lifesteal = 0f;
    public float DmgResist = 0f;       // fraction of incoming damage ignored (0..~0.8)
    public float JumpMul = 1f;         // jump-height multiplier
    public float PickupRange = 8.0f;   // (TUNE) XP-orb collection radius — generous base for every witch (was 1.8); Lodestone cards + magnets extend it further

    public float ComboPow = 0.03f;
    public int   ComboCap = 8;
    public float ComboWindow = 1.4f;
    public int   SplitEvery = 0;       // Crescendo: every Nth combo cast splits

    public float DashDist = 7f;
    public float DashCd = 2.6f;
    public int   DashCharges = 1;

    public float ManaMax = 2f;   // (META) base 2 → 3 permanently via the gold meta-tree, → 5 in-run via Deep Reserve
    public float ManaGain = 0.2f;

    // crit (direct hits only — not DoTs, not AoE), spell sizing, and luck
    public float CritChance = 0.05f;   // chance a direct hit crits
    public float CritDamage = 0.5f;    // extra damage on crit (×(1+this))
    public float SpellRange = 1.0f;    // bolt/beam/projectile range multiplier
    public float SpellArea = 1.0f;     // AoE size multiplier (fields, blood lash/tide, blast ults)
    public float ProjSpeed = 1.0f;     // projectile travel-speed multiplier (all non-hitscan bolts)
    public float Luck = 0f;            // biases upgrade-card rarity upward

    // shields
    public float ShieldPct = 0.20f;    // shield capacity as fraction of max HP
    public float ShieldDelay = 4.0f;   // seconds after a hit before regen
    public float ShieldRegen = 0.8f;   // shield/sec once regenerating

    // resonance (lunar damage meter)

    // finishers / modifiers
    public int   FinSlots = 2;   // (META) base 2 → 3 permanently via the gold meta-tree, → 5 in-run via Coven Bond
    public int   ModSlots = 1;   // (META) base 1 → 2 permanently via the gold meta-tree, → 3 in-run via Coven's Reach

    // hex mark
    public int   MarkJumps = 1;
    public float MarkAmp = 1.3f;
}
