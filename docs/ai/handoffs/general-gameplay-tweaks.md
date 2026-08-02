# general-gameplay-tweaks

Ability-upgrade-tree audit (finishers + charged modifiers), the six fixes it produced, and the
Forsaken/Curse kit redesign it queued up. Branch: `main` (uncommitted, alongside unrelated in-flight
work — Haunt storm, boss phase 2, perk trees; scope edits carefully).

## 1. Objective & intended behavior

Every equipped finisher/modifier has a 5-path upgrade tree (`AbilityUpg.Fins` / `.Mods`): 3 stat
paths (Uncommon frames) + 2 evolutions (Epic/Legendary), each stacking to `Player.UpgCap = 4`.
**Every card must do what its text says, on every stack.** The audit was triggered by the user
noticing Arcane Torrent's Epic evo claims "crit ramps as it fires" on an ability that never channels,
and that Frost Wall's live-wall-count upgrade appeared to do nothing.

## 2. Current status

Audit **complete**; all six fixes **applied and building 0/0**. Nothing committed.

Full sweep performed: 30 finisher trees + 20 modifier trees × 5 paths = 250 paths. A scripted scan
confirmed **no path is syntactically dead** (every `s0/s1/s2/e0/e1` is read), and each evolution's
payload field was traced into its consumer class (`GroundField.Creep/Follow/Pull/RotDps/DeathBurst/
BloodBankMul`, `Bolt.Splinter/Arc/MarkOnHit`, `SeedMine.Chain/CloudPoison`, `Fireball.Cataclysm`,
`FrostWall.Chill/Pulse`, `WindPad.Roam/LaunchMul`, `Cyclone.pullMul`, `DoomSigil.chain`, and the
Player-side flags `HolyEmpower*`, `Fervor*`, `_thorn*`, `FireWallT`) — all genuinely consumed. The
failures clustered in **text-vs-code drift** and **two integer divisions**, not in missing wiring.

Fixes shipped:
1. **Arcane Torrent · Overcharge** — renamed to "+crit chance & crit damage". The effect (flat
   `0.12*e0` crit chance + `1+0.1*e0` crit mult) was always correct; only the text lied. Cataclysm's
   text now also names its conduit prerequisite.
2. **Witch's Hollow dropped.** `Game.BuildScrollOffer` now gates on `AbilityUpg.IsFin/IsMod` instead
   of a hand-kept `t != Crescendo && t != Fullmod` list, so no untreed ability can ever be sold again.
3. **Permafrost / Bark** — `1 + s2 / 2` → `1 + s2` (int division made stacks 1 and 3 no-ops).
4. **Soul Tether** — `PerkTree.Links` bails when `p.SoulTether`, so perks can't re-clamp 99 → 12.
5. **Deep Freeze** — now also feeds `AddFreeze`'s `durBonus`, so it isn't fully nullified by Flash
   Freeze's guaranteed `100f` buildup.
6. **Implosion/Whirlwind** got `ModMeta.Tag` entries (`IP`/`WW`, were drawing as `?`); Frost Wall's
   pre-overhaul rarity-worded description rewritten.

## 3. Constraints & decisions

- **Abilities are always Common-framed.** Power lives entirely in the upgrade tree; rarity is
  cosmetic. Any description still saying "at higher rarity" is stale — Frost Wall was the last one
  found, but assume more exist in `FinMeta.Desc` / `ModMeta.Desc`.
- **An ability with no `AbilityUpg` entry must never be acquirable.** `AvailableAbilityUpgrades`
  iterates only `AbilityUpg`, so an untreed ability arrives permanently un-upgradable and eats a
  slot. This is now enforced structurally in one place (`BuildScrollOffer`) rather than by list.
- Retired by this: **Witch's Hollow (`HexField`)** and **Witching Hour (`Fullmod`)** are now
  unobtainable. Their `FinType` cases in `Player.ExecuteFinisher` stay for save/state compat; their
  pool `Def`s were removed (they were already unreachable — every roll path gates on `IsFin`).
- Never use integer division for a per-stack scalar. Both instances found are gone.
- Wall cap raised 3 → 5 deliberately: eviction *shatters* the oldest wall for damage, so more live
  walls means **less** damage throughput. Permafrost is a control path, not a damage path.
- `PerkTree.cs` has a **NODE-DESIGN RULES** header (added in the perk-tree work) that applies
  directly to the Forsaken redesign below — read it first.

## 4. Files & major symbols

- `Upgrade.cs:AbilityUpg.Fins` / `.Mods` — the path name/desc tables (all card text lives here).
- `Upgrade.cs:UpgradePool.Build` — pool defs; the retired Fullmod/HexField defs are a comment block.
- `Upgrade.cs:AvailableAbilityUpgrades` / `FinUpgCard` / `ModUpgCard` — upgrade-card injection.
- `Game.cs:BuildScrollOffer` — scroll-vendor offer, now `AbilityUpg`-gated.
- `Player.cs:FinArcaneBlast` (~5258) — the single-sweep torrent; `critBonus`/`critMulBonus`.
- `Player.cs:StartBeam` / `UpdateBeam` (~5673) — Spelllance; the *real* crit ramp (`_beamHeld`).
- `Player.cs:SpawnFrostWallMod` (~4174) — `limit`/`dur`; `Player.cs:FinThornSkin` (~5983).
- `Player.cs:ApplyChargedMods` (~4602) — the big `switch (m.Type)` holding all 20 modifier bodies.
- `Player.cs:UpdateCurseBeam` (~3261) — Forsaken primary: stacks → `CurseGroup` → shared damage.
- `PerkTree.cs:Links` (~205); `Modifier.cs:ModMeta.Tag` / `.Desc`.

## 5. Tests / validation performed

- `dotnet build -v quiet -nologo` → **0 Warning(s) 0 Error(s)**.
- `./tools/run-ai-scenario.ps1 -Scenario card_pool_audit` → **passed**; affinity-card spread 8–11
  across all nine witches (unchanged by removing the two dead defs).
- `grep -iE "SHADER ERROR|Shader compilation" artifacts/ai/godot.log` → **0 hits**.
- Re-ran the dead-path scan after the edits: every stat/evo path still read; no `s2 / 2` remains.

## 6. Current failures / uncertainties

- **Not playtested.** All six fixes are static-verified only; nobody has taken Permafrost 4× in a
  real run to feel whether 5 concurrent walls is too much.
- **Arcane Torrent's Legendary "Cataclysm" is inert on most builds** — deliberately left as-is, only
  documented. It needs an `ArcaneMarked` source, and there are exactly three: the Arcane witch's own
  kit (`SetArcaneMark`), Coven Swarm's Epic "Conduit Swarm", and Arc Storm's Legendary "Chain
  Reaction". On any other witch without one of those it does literally nothing. Open design question.
- Minor, unfixed: Cataclysm's chain radius is a hard-coded `10f` ignoring SpellArea/SpellRange, and
  chained hits skip `OnHitDirect` (no crit, combo, lifesteal or mana). Volley's Epic "Seeking"
  silently also grants +1 bolt, undocumented.
- `HexMark`'s `SetModStats` writes the **global** `S.MarkAmp`/`S.MarkJumps`, which `FinBloodCurse`
  also reads — cross-contamination between two unrelated abilities. Pre-existing; not touched.

## 7. Briefly-rejected approaches

- Making Overcharge a *real* ramp (sort corridor hits by distance, climb crit down the beam) — the
  user chose the rename. Keep the idea; it's the better long-term fix.
- Giving Witch's Hollow an upgrade tree instead of retiring it — user chose to drop it.
- Keeping the scroll vendor's hand-written exclusion list and just adding `HexField` — rejected as
  the same bug waiting to recur; gated on `AbilityUpg` instead.
- Preserving the wall cap at 3 via `1 + (s2+1)/2` — still leaves two stacks flat.

## 8. Next 3 concrete actions

1. **Redesign the Forsaken/Curse kit** — user: *"I hate all of them."* Diagnosis delivered and
   agreed-pending: (a) five of six are the same verb — a nova centred on you (Hex Pulse, Soul Reap,
   Hex Chains, Doom Sigil-ahead-6u, Cursefield); Hex Pulse and Soul Reap are near-duplicates and Soul
   Reap is strictly better. (b) **None of them touch her actual mechanic.** Hex Pulse applies `Mark`
   (a different system), Soul Reap applies no curse at all, `FinDoomSigil` calls `AddCurse` with
   **group 0** so it never tethers, and `FinHexChains` spins its own throwaway 2s group her beam
   never feeds. Root cause: Ember has burn→Living Bomb, Frost freeze→shatter, Blood bleed→rupture;
   **Curse has `Mark` and `CurseGroup` as two unconnected systems with no bridge.** Recommended
   direction (awaiting user's go / their own fantasy): rebuild the four finishers around the tether
   loop and give each a distinct verb — one aimed, one zoning, one defensive/mobility, one payoff
   that consumes the group. **Do not invent a fantasy unprompted — that is exactly what the user
   said made the current kit bad.**
2. Decide the Cataclysm-conduit question (broaden conduit producers, or reword the card the way
   Overcharge was reworded).
3. Sweep `FinMeta.Desc` / `ModMeta.Desc` for any remaining pre-overhaul "at higher rarity" text.
