# Handoff: nerf-shrine-adjustements

## 1. Objective & intended behavior
Rework the boss cycle + the 3 boss-nerfer shrines:
- Ult "cost" (its charge meter) rises as difficulty ramps.
- Difficulty clock runs ×1.5 while the world boss is up, reverting when he falls.
- **ONE** nerf shrine stands in the world at a time, rolled at random from the 3 kinds, single-use.
  All 3 now cost souls: **100 per warden, doubling each use in a run** (100→200→400…), every warden
  paying an equal share before it fires.
- Boss defeated + party stays in-world → the lair AND a fresh random shrine re-seed elsewhere.
- Fix: the Summoning's 45s countdown used to run with nobody in the circle.
- Minimap: spent shrines drop their pin; gale pads stop ringing the rim.
- The 3 nerfers must sit on **three different axes** (see §3).

## 2. Current status — WORKING (validated, build 0/0)
- Ult cost ramp, ×1.5 boss difficulty, single-shrine + doubling toll, lair/shrine re-seed: done.
- Summoning countdown is host-authoritative and gated on presence; HUD shows a pulsing red
  "STAND IN THE CIRCLE!" and the ward stops turning when stalled.
- Minimap fixes done. Top-left `x/3 SHRINES` counter replaced by the live shrine + its toll.
- **Sanctuary** retuned: flat 2 HP/s → `max(2, MaxHp * SanctuaryRegenFrac)` (1%/s).
- **Sacrifice** fully replaced (old `BossDrainSigil.cs` DELETED) by the **CRIMSON RITE** — see §3.
- Nothing committed. Branch `main`, working tree dirty (also carries unrelated prior-session work in
  `Creature.cs`, `Sfx.cs`, `PropGlb.cs`, `data/colliders.json`, etc. — do not attribute those here).

## 3. Constraints & decisions (durable)
- **Three axes rule (owner).** Summoner = *damage* (unicorn nuke), Sanctuary = *sustain* (regen),
  Sacrifice = *the siege*. Owner rejected several Sacrifice proposals (bonus damage taken, lifesteal,
  stacking bleed, extra revive) as "the same in one way or another" — do NOT re-propose damage- or
  sustain-flavored effects for Sacrifice.
- **Crimson Rite spec (owner-authored).** On boss summon: one `RiteSigil` per warden ringed around the
  arena, shown on the minimap. **Any** warden can charge **any** sigil (3s standing; progress does not
  decay) — deliberately any-warden so a downed ally can't deadlock the set. All lit → a pentagram
  draws itself over the boss in real time → flares → shockwave kills every foe **except boss-tier**
  (`IsBoss` covers world boss AND miniboss; specials Taker/Phalanx DO die) with crimson slashes, then
  **stalls all foe spawns for 20s/warden, capped 50s**. Once per boss fight.
- **Death callouts on the purge are WANTED** (`SPLRT!/VISCERA!/…`). A suppression flag was added then
  explicitly reverted at owner's request — do not re-add.
- Soul toll counts **activations** (`_nerferUses`), not shrines spawned, so skipping one isn't punished.
- Sacrifice keeps its 40% HP price + guardian minibosses on top of the soul toll (its identity).
- Kill burst is metered (8 foes/frame) per the uncapped-AoE-cascade gotcha in `DEV_HANDOFF.md`.
- `Hud.Banner` clips past ~50 chars — keep banners short.

## 4. Files & major symbols
- `Game.cs`: `UltCostMul`/`UltGainMul`, `BossDiffRate`/`BossFightActive`, `UpdateDifficulty`;
  `SpawnNerfers` (single random kind, `_lastNerfKind`), `NerferCostEach`/`NerferCostTotal`/`_nerferUses`,
  `TryActivateNerfer`→`HostNerferPaid`→`HostStartSummoner`/`HostBeginSacrifice`/`HostArmSanctuary`,
  `NerferHudLine`, `AnyWardenInWard`, `SummonerHeld`/`SummonerDur`, `ReseedBossCycle`,
  `SanctuaryRegenFrac`; **rite**: `RiteSigils`, `SpawnRiteSigils`, `UpdateCrimsonRite`,
  `AnyWardenInSigil`, `BeginRiteDraw`, `RiteDetonate`, `DrainRiteKillQueue`, `SpawnStallT`/
  `SpawnStalled`, `RiteOpen`/`RiteLit`/`RiteTotal`/`RiteDrawing`/`RiteDrawProgress`;
  dev: `DebugPlaceNerfer`, `DebugPayNerfer`, `DebugArmCrimsonRite`, `DebugAliveCount`,
  `DebugFirstUnlitSigil`, `DebugRevealAround`.
- `RiteSigil.cs` (new) — segmented fill ring + beacon. `CrimsonPentagram.cs` (new) — `DrawDur`,
  self-drawing circle+chords, vertical light **curtains**/pillars, `Burst()`.
- `NerfShrine.cs`: `Stalled` (ward dims/stops when unheld) + rewritten header comment.
- `Enemy.cs`: `RiteSlash()`. `Player.cs`: ult charge sites ×`Game.I.UltGainMul`.
- `Net.cs`: `BroadcastNerfers(list, uses)`, `RequestNerferPay`/`BroadcastNerferPaid`,
  `BroadcastSummonerTick`, `BroadcastRiteSigils`/`RiteCharge`/`RiteFire`/`RiteDetonate`,
  `AliveAllyPositions()`. Removed: `BroadcastDrainSigil`, `RequestSanctuaryPay`, `RequestNerfer`.
- `Hud.cs`: `DrawMinimap` (skip `State==2` nerfers, un-clamp gale pads, rite sigil pins),
  `DrawCrimsonRite`, `DrawSummonerTimer`, top-left `NerferHudLine`.
- `dev/ai/AiTestRunner.cs`: `NerfShrineScenario`, `CrimsonRiteScenario`.
- DELETED: `BossDrainSigil.cs` (+ `.uid`).

## 5. Tests / validation performed
- `dotnet build -v quiet -nologo` → **0 Warning(s) 0 Error(s)**.
- `./tools/run-ai-scenario.ps1 -Scenario nerf_shrine` → **passed**. Asserts toll 100/200/400;
  hold-E prompt in reach; countdown 44.71→44.21 held / **44.07→44.07 frozen outside** / resumes on
  re-entry; spent shrine drops its minimap pin. All 6 captures opened + inspected.
- `./tools/run-ai-scenario.ps1 -Scenario crimson_rite` → **passed**. Asserts 32 adds→0, boss-tier
  2→2 (boss + miniboss survive), stall 19.5s, 0 spawns during the silence (timer ticking), spawns
  resume after. All 8 captures opened + inspected.
- Visual fixes driven by inspection: pentagram needed vertical curtains (a flat ground figure is
  invisible at FP eye height); rite HUD gated on `State==Playing` (the purge pops the level-up menu);
  progress bar sums partial fills instead of snapping.

## 6. Current failures / uncertainties
- **MP is entirely unexercised** — all validation was solo/host. Multi-warden sigil sets, the paid
  share tally, `BroadcastSummonerTick`, and the rite RPCs have never run against a real client.
- Client pays its soul share **before** host confirmation with no refund path if the host rejects
  (pre-existing pattern inherited from the old Sanctuary flow; race window looks unreachable).
- `SpawnStallT` ticks only while `State == Playing`; in MP a client sitting in a menu will see its
  local silence countdown freeze while the host's keeps running (HUD-only desync).
- `Hud.AddKill` caps at 40 pops — a 4-warden purge (~90 foes) would silently drop the oldest callouts.
- `crimson_rite` fast-forwards the last ~17s of the stall (`g.SpawnStallT = 0.2f`) to stay inside the
  6000-frame harness budget; the full wall-clock lapse is not sat through.

## 7. Briefly-rejected approaches
- Sacrifice as +35% damage taken / lifesteal / stacking bleed / a free revive — all rejected by owner
  as overlapping the existing damage + sustain axes.
- "Silent Vigil" (passive: no adds + no ×1.5 for the whole fight) — owner: "too OP" as a passive.
  The Rite is the earned, time-boxed version of the same idea.
- Suppressing per-kill callouts during the purge — implemented, then reverted at owner's request.
- Decaying sigil charge to force simultaneous activation — not asked for; adds frustration.

## 8. Next 3 concrete actions
1. Validate the Rite in real **multiplayer** (2+ wardens): sigil set size, the "n/m shares paid"
   tally, per-sigil fill streaming, and that the pentagram + purge land on clients.
2. Playtest the toll curve (100→200→400 per warden) against actual Haunt soul income — confirm the
   3rd shrine in a run is expensive-but-reachable rather than dead content.
3. Decide whether to raise the `Hud.AddKill` 40-pop cap so a 4-player purge shows a callout per kill.
