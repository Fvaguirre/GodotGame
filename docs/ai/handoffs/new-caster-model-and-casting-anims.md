# Handoff: new-caster-model-and-casting-anims

## 1. Objective & intended behavior
Put an authored model — THE WITHERED KING — on the Grove's whole spellcaster family, and give every one of
their abilities a real cast ANIMATION instead of firing out of a walk cycle.
- Caster, stunner, healer, empowerer, hexer and dispeller all wear the new GLB and walk on its walk clip.
- Stunner's stun telegraph = the charged-spell clip. Healer/empowerer/hexer/dispeller = the mage cast.
  Any projectile = mage spell cast 4.
- **Retrofit**: the ogre-bodied bolt throwers (sieger, mini-boss) get `cast4` grafted onto their much larger
  rig, "tweaked for how big the shooter is" — i.e. properly retargeted, not just replayed.
- Multiplayer must show the same wind-up on client proxies.

## 2. Current status — DONE AND VALIDATED (build 0/0, all scenarios pass)
- `assets/models/enemies/withered_king.glb` (6.4 MB) merged from 6 Meshy per-clip GLBs (~38 MB).
- All six foe types load it; all resolve their clips (14 for the withered body, 13 for the grafted ogre).
- Every ability is now a telegraphed WIND-UP whose effect lands on the clip's release frame, at **no DPS cost**
  (the cooldown starts when the wind-up starts, so it only phase-shifts).
- MP wired end to end and exercised through the proxy path.
- Nothing committed. Branch `main`, tree dirty — it ALSO carries a lot of unrelated prior work
  (hollow-moon phase 2, nerf shrine/crimson rite, collider editor, PropGlb, Sfx). Do not attribute those here.
  This feature's own footprint: `Creature.cs`, `Enemy.cs`, `Net.cs`, `dev/ai/AiTestRunner.cs`, plus the GLB
  and Godot's extracted `withered_king_texture_0.png` (11.6 MB of new untracked binary).

## 3. Constraints & decisions (durable — the "why")
- **Owner's calls:** the whole caster family shares this body (hexer + wardbane were initially left as spiders
  and then explicitly pulled in); hexer/dispeller use the SAME clip as the healer; MP is a requirement, not a
  follow-up.
- **Never crush a cast by speed alone.** These are ~2.2s clips; squeezing one into a 0.4s wind-up needs 4x and
  reads as a spasm. `CastSpeedMax = 1.8` — past that the clip plays slower than "release at 72%" and the bolt
  leaves while the arms are still rising. That still reads as a cast.
- **Cap the follow-through against the foe's own cadence** (`cadence * 0.7`). Without it the healer's clip
  outlasted its 1.4s heal interval, so it re-cast on the frame it released and NEVER returned to its walk.
- A grafted clip carries the REST POSE of the rig it was authored on. With two graft libraries now (taker
  actions + withered casts) the source rest must travel WITH the clip — see §4.
- MP is **cosmetic only**: the bolt/heal/curse is host-authoritative on its own existing path, so the cast RPC
  is unreliable and carries an int clip index, not a string. A proxy animates; it never fires
  (`RemoteCast` deliberately leaves `_castPend` at 0).
- FROZEN discards a cast mid-wind-up rather than firing it out of a block of ice.
- Tuning that moved to make animation read (both balance-neutral): totem pulse 0.9s → 1.6s with its haste
  1.1 → 1.9 to keep the buff unbroken.
- `CreatureKind.Spider` is now unreachable, and so is `CreatureKind.Zapper`'s procedural hooded wizard.
  Both KEPT as fallbacks, not deleted.

## 4. Files & major symbols
- `Creature.cs`: `CreatureKind.Withered`, `WitheredGlb`, `AuthoredWithered`,
  `AuthoredBiped(… graftCast:)`, `PreCanon` += `cast`/`cast4`/`castcharge`;
  **graft refactor** → `struct GraftSrc {Anim, Rest, HipsY}`, `LoadGraftLib(glb, want)`,
  `SharedActionClips()` (taker), `SharedCastClips()` (withered king), `Graft(src)`,
  `RetargetGraftedPositions(anim, srcRest, srcHipsY)`;
  `BossPlay`/`BossEndClip` guard widened `_hollow` → `_gobAuthored`, aliased `CastPlay`/`CastEnd`/`CastLength`;
  `Casting`, `DebugFootGap`.
- `Enemy.cs`: `_Ready` kind routing (6 types → `Withered`); `CastWind`/`CastSpeedMax`/`CastTail`,
  `BeginCastAnim(clip, dur, cadence, net)`, `BeginCast`, `ReleaseCast`, `UpdateCastAnim`,
  `CastIdx`/`CastClipOf`, `RemoteCast`; `HealPulse`/`TotemPulse` split out of `MoveHealer`/`MoveTotem`;
  hooks in `MoveRanged`, `MoveZapper`, `MoveHealer`, `MoveTotem`, `MoveHexer`, `MoveSapper`, `BeginBossAnim`;
  `DebugCasting`/`DebugHasClip`/`DebugFootGap`/`DebugCastState`.
- `Net.cs`: `BroadcastEnemyCast` / `ReceiveEnemyCast` (mirrors `BroadcastBossTell`'s `_renemies` lookup).
- `dev/ai/AiTestRunner.cs`: `withered_caster` scenario — `WitheredCasterScenario`, its `Probe` and `ProxyProbe`.
- Merge script pattern: `scratchpad/merge_withered.py` (same headless-Blender NLA recipe as the boss).

## 5. Tests / validation performed
- `dotnet build -v quiet -nologo` → **0 Warning(s) 0 Error(s)**.
- `./tools/run-ai-scenario.ps1 -Scenario withered_caster -TimeoutSeconds 500` → **passed**, 30 captures.
  8 host probes (caster/stunner/healer/empowerer/hexer/dispeller/sieger/mini-boss): authored GLB loaded, the
  RIGHT clip posing the body mid-wind-up, and a numeric grounding check — **every cast within 0.04 radii of
  planted** (`DebugFootGap`), which is what proves the ogre graft retarget. 3 proxy probes: wire indices
  0/1/2 map to `cast`/`cast4`/`castcharge`, and all released back to the walk (73f/72f/87f).
- Regressions: `graft_retarget` → passed (goblin still resolves 10 clips, no deform);
  `hollow_man` → passed (13/13 clips, every attack clip still driving the pose, charge pushes 30.0u).
- Captures opened + inspected adversarially: caster idle/cast/release, stunner cast/release, healer
  cast/release, empowerer cast, hexer cast, dispeller cast, sieger idle/cast/release, mini-boss cast,
  proxy caster cast.

## 6. Current failures / uncertainties
- **No real two-peer networked run.** The harness is single-process; the proxy probes drive everything
  `ReceiveEnemyCast` does EXCEPT the wire hop. That hop is a 2-line mirror of six working broadcasts, but it
  is unproven. A genuine host+client harness would close this and the boss phase-2 MP gap at once.
- RPC volume is untested at scale: a big pack of casters emits ~1 unreliable RPC per foe per ~2s.
- The 11.6 MB of new binary (GLB + extracted texture) is untracked; no decision yet on committing it.
- Cast tuning (0.9s default wind-up, 1.8x speed cap, 0.7 cadence cap) is scenario-validated, not playtested
  in a real fight.

## 7. Briefly-rejected approaches
- Speeding a 2.2s clip to 3–4x to fit a short wind-up — reads as a spasm; clamp and let it under-run instead.
- Leaving hexer/wardbane as neon spiders "so the pack reads as several species" — owner overruled.
- Grafting the cast library onto EVERY biped — it's opt-in (`graftCast`) so the goblin/zombie/taker don't pay
  a per-spawn clip duplicate + retarget they'd never play.
- Reliable transfer for the cast RPC — it's cosmetic; a dropped one costs a single wind-up pose.
- Asserting "not casting" AFTER a `Capture()` — the totem/healer duty-cycle re-casts by then. This was a TEST
  bug that cost two cycles; latch the verdict when the wait loop exits.

## 8. Next 3 concrete actions
1. Build a real 2-peer MP harness (host + client process) and verify `BroadcastEnemyCast` over the wire —
   then reuse it for the still-unvalidated phase-2 boss MP (see `first-boss-tweaks-and-phase2.md`).
2. Playtest a live wave with a caster pack: does the 0.9s wind-up make them feel readable-but-fair, and is the
   RPC chatter invisible at 20+ casters?
3. Decide whether `withered_king.glb` + its extracted texture go into plain git (precedent: zombie/taker/ogre
   /hollow_man all already do) before committing any of this.
