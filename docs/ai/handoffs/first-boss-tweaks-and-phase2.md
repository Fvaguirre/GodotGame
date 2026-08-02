# Handoff: first-boss-tweaks-and-phase2

## 1. Objective & intended behavior
Rebuild THE HOLLOW MOON (the only world boss) on an authored Meshy GLB, then give him a second phase.
- **Phase 1 rework:** authored model + real animations, every existing attack kept and still telegraphed,
  each attack driving a specific clip; base speed ×2; shoulder-goblin fiction removed (crit zones stay);
  an obscuring arcane hand-glow that doubles as a universal telegraph; a new 20%-HP head-down charge;
  better nova VFX.
- **Phase 2:** killing him fakes his death — he falls, laughs on the ground, stands back up on half a bar
  with an arcane aura, ×1.5 damage, faster movement, a 3× charge combo every 25%, and a vortex-to-stomp
  every 33%. He is untouchable through the revival and the whole spin, shown clearly on the HUD.

## 2. Current status — BOTH PHASES WORKING (build 0/0, both scenarios pass)
- `assets/models/enemies/hollow_man.glb` — 13 Meshy per-clip GLBs (373 MB) merged to **one 29 MB asset**
  via headless Blender. Same 24-joint biped rig as goblin/zombie/ogre/taker, so all existing skeleton
  modifiers + the clip-library system apply unchanged.
- All 13 clips resolve; every attack's clip is asserted playing mid-wind-up.
- Phase 2 fully wired: fake death (no orbs, no lair payout) → prone 5s (laugh at 3s) → Stand_Up8 →
  3s unsteady laughing advance → triple charge → normal moveset → vortex at 66/33%.
- Nothing committed. Branch `main`, working tree dirty — it ALSO carries unrelated prior work
  (`nerf-shrine-adjustements`, collider editor, PropGlb, Sfx, colliders.json). Do not attribute those here.

## 3. Constraints & decisions (durable — the "why")
- **Owner's calls:** rock throw uses the `gripthrow` clip (a procedural overhead lift read badly);
  mines stays procedural; phase-2 pool = **50% of the phase-1 max** and all phase-2 thresholds are % of
  THAT pool; **finish-then-spin** (crossing a 1/3 threshold makes him untouchable IMMEDIATELY, he rides out
  the current charges, then spins); the 45%-max-HP stomp goes through normal mitigation; the phase-1 single
  20% charge is retired in phase 2.
- **×1.5 is the ONLY damage multiplier phase 2 adds.** There was never a 50%-HP damage buff — `enraged`
  only adds projectiles and shortens wind-ups. Don't "restore" one.
- `Player.Hurt` stamps a **0.7s i-frame** on every landed hit → any sustained-damage zone must tick at
  **≥0.8s** or it does literally nothing, and a signature finisher needs `ignoreIFrame: true`.
- Player damage lands on **Shield before HP** — assert on `Hp + Shield`, never bare HP.
- The vortex pull is applied **locally on each machine to its own witch** every frame (player position is
  client-authoritative); only the host ticks damage. Inverse of `Cyclone`'s enemy pull.
- Aura must be the **character's own skinned mesh**, inflated along normals — primitives read as primitives.
  Noise sampled in WORLD space (atlas UVs smear it). Gate the fresnel rim by the noise or a detailed mesh
  gets repainted one solid colour. Additive auras blind fast: keep `intensity`/`opacity` low.
- `VfxLance` is the DIVINE witch's holy lance — never reuse it for arcane effects.
- He has **no spin clip**: whipped at 26 rad/s + aura/funnel opacity raised so the missing pose can't read.

## 4. Files & major symbols
- `Creature.cs`: `HollowGlb`, `AuthoredHollow`, `AuthoredBiped(…heightMul/vary/graftShared/hollow)`,
  `PreCanon`, `BuildHollowExtras`, `BossPlay`/`BossEndClip`/`BossDie`/`BossClipLength`, `SetHandGlow`,
  `SetGesture`, `ShowHeldRock`, `UpdateHandGlow`, `SetPhase2`, `SetSpinning`, `UpdatePhase2Aura`,
  `_locoWalk`, `_gobMesh`. **`BipedLoco` no-ops while `_bAtkClip != null`** — see §6.
- `Enemy.cs`: `BossClipFor`, `BeginBossAnim`/`FireBossAnim`/`EndBossAnim`/`UpdateBossAnim`, `AnimFirePoint`;
  `DashDist`/`BossDashRun`/`NoteChargeThreshold`; **phase 2** → `BossPhase`, `P2HpFrac`, `P2DamageMul`,
  `P2SpeedMul`, `SpinDur`, `SpinDpsFrac`, `Invuln`/`BossInvuln`, `EnterPhase2`, `UpdatePhase2`,
  `HostStartSpin`, `NextTripleTarget`, `BossLaughAdvance`, `RemoteBossPhase2`; `Die()` intercept.
- `BossVortex.cs` (new) — pull/grind/finishing stomp + `Eruption`. `BossGestureMod.cs` (new) — mine gesture
  + hand-orb/palm pinning. `shaders/arcane_hands.gdshader`, `shaders/arcane_aura.gdshader` (new).
- `Player.cs`: `VortexPull`, `Hurt(dmg, src, ignoreIFrame)`.
- `Net.cs`: `ChargeSweep`/`SegDist`, `BroadcastBossPhase2`, `BroadcastBossVortex`, `VortexStomp`.
- `Hud.cs`: boss bar 🔒 IMMUNE pulse. `Game.cs`: `RockMat()`. `BossRock.cs`: uses `RockMat()`.
- `dev/ai/AiTestRunner.cs`: `HollowMoonScenario`, `HollowPhase2Scenario`.

## 5. Tests / validation performed
- `dotnet build -v quiet -nologo` → **0 Warning(s) 0 Error(s)**.
- `./tools/run-ai-scenario.ps1 -Scenario hollow_man -TimeoutSeconds 300` → **passed** (20 captures).
  Asserts 13/13 clips; each attack's clip playing mid-wind-up (`cast6`/`cast1`/`gripthrow`/`stomp`);
  charge pushes exactly 30.0u; death clip plays. All captures opened + inspected.
- `./tools/run-ai-scenario.ps1 -Scenario hollow_phase2 -TimeoutSeconds 300` → **passed** (11 captures).
  Asserts fake death drops 0 orbs and doesn't set `Dead`; pool 4620→2310; damage while prone 2310→2310;
  invuln through prone/rise/advance; 3/3 charges; pull 25.5u→4.9u standing still; grind 135→59 (hp+shield);
  45% stomp lands; vulnerable after the spin; phase-2 kill really kills.
- Visual iterations driven by inspection: aura primitives → skinned-mesh clone; aura moved to AFTER standup;
  aura intensity/opacity cut twice; hand orbs re-pinned via the modifier, shrunk, offset onto the palm;
  rock throw swapped to `gripthrow`; nova dome dropped; `VfxLance` replaced with an arcane eruption.

## 6. Current failures / uncertainties
- **MULTIPLAYER IS ENTIRELY UNVALIDATED.** All runs were solo/host. Unexercised: `BroadcastBossPhase2`
  (phase + invuln mirroring, and the client-side "aura ignites when invuln clears" heuristic),
  `BroadcastBossVortex` (per-client pull copies), `VortexStomp`, `ChargeSweep` across real peers,
  and `NextTripleTarget` with >1 living witch (solo always re-targets the same one).
- `AnimStep`/the proxy path call `BipedLoco` EVERY frame; only the `_bAtkClip` guard stops it eating attack
  clips. Any new one-shot clip state must survive that call.
- Charge net displacement is ~20u of the 30u pushed on broken ground (terrain/structure resolve stops him).
  Believed correct, never confirmed on open ground.
- Tuning unplaytested: phase-2 ×1.35 speed, 3.5%/s grind, `VortexPullOuter/Inner` (4.5→17 u/s).
- `hollow_man` needs `-TimeoutSeconds 300`; the default 90s kills it before `result.json` is written.
- The 29 MB GLB is untracked and NOT committed; `hollow_man_texture_0.png` (Godot's extracted copy) too.

## 7. Briefly-rejected approaches
- Procedural overhead two-hand rock lift (owner: "so bad") → `gripthrow` clip + a rock in front of him.
- Primitive cone/capsule auras — read exactly as the primitives they were.
- A dome on the nova and on the vortex-stomp — a translucent hemisphere over a boss reads as a SHIELD.
- Positioning the hand orbs from Enemy's tick — gives the PRE-modifier pose; they froze at his chest.
- Keeping 13 separate GLBs (373 MB) or downscaling the texture below 4k (it's already 4k; "12k" is polys).

## 8. Next 3 concrete actions
1. **Run a real 2+ warden MP session against phase 2**: phase/invuln mirroring, per-client vortex pull,
   the stomp RPC, and triple-charge re-targeting across distinct witches.
2. Playtest phase-2 tuning — ×1.35 speed, the 10s/3.5%-per-second vortex, and whether 45% max HP on the
   finisher is punishing-but-fair once a real build with shields/armor is involved.
3. Decide whether `hollow_man.glb` (29 MB) + its extracted texture go into plain git as-is (precedent:
   `zombie.glb` 28 MB, `taker.glb` 28 MB already do) before committing any of this.
