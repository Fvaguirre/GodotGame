# forsaken-witch-rework

Supersedes `forsaken-doom-rework.md` (deleted — this is the canonical handoff). Branch `main`,
uncommitted, in a tree that also carries substantial unrelated work (Haunt storm, boss phase 2, perk
trees, collider editor). **Scope any diff carefully — most of `Enemy.cs`/`PerkTree.cs`/`Net.cs`/
`AiTestRunner.cs`'s line count is not this feature.** Full design spec: the "Forsaken Witch — the Doom
kit" artifact.

## 1. Objective & intended behavior

Rebuild the Forsaken witch around **Doom** as her single mechanic, replacing the curse-stack/tether kit
the owner rejected outright. She doesn't kill the pack; she makes the pack turn on itself.

Doom (modelled on Soulstone Survivors): every application adds to ONE bank per foe and refreshes a 5s
fuse; the fuse detonates it in a burst; the instant the bank covers the distance to that foe's next
**floor** it fires early; detonation splashes into an area. Floor is 0 for anything ordinary — so an
execute is simply a kill — and the Hollow Moon's next authored gate for him, so a boss execute punches
him to his next stage rather than deleting content.

**Division of labour: the channel BUILDS, the charged release SPENDS *and* SPREADS, the fuse is what
every other witch lives on.** She is the only one who chooses when a bomb goes off.

## 2. Current status

All eleven planned pieces implemented + three owner-requested revisions. Build **0 Warning(s) 0 Error(s)**
throughout. `forsaken_doom` scenario **passes**, 9 captures, all inspected. Nothing committed.

## 3. Constraints & decisions (durable)

- **Focus ramps on HER, not the target.** A per-target ramp makes tunnelling one foe correct in a 40-enemy
  game — exactly what made her primary feel bad. Sweeping keeps the wind-up; only releasing drops it.
- **The channel does NOT auto-chain.** It used to creep Doom to a fresh foe every 0.5s out to 18u, loading
  six enemies you never aimed at with nothing on screen to explain it. Spreading is the charged release's
  job now, where it is deliberate and visible. `CurseSpreadRange` is vestigial as a result.
- **Charge depth drives spread as well as spend** — a tap is a contained pop, a full release seeds the
  area. This is what stops the right click feeling like a pure stack-dump.
- **The fuse keeps a base spread** because other witches have no crush; without it a borrowed curse card
  is a bomb that only ever pops in place.
- **Danse Macabre applies a FRACTION of max HP, capped.** Flat Doom exceeded a shade's whole health, so a
  trash crowd executed itself instantly and nobody ever danced (the scenario reported `of 0` twice). The
  cap stops a boss being handed a percentage of 4200 HP.
- **Fray amplifies rather than competes** — it keeps its active copy (what a non-Forsaken witch uses) and
  passively multiplies every detonation's spread, stamped onto the bank at application time.
- **Puppetry is not telekinesis.** A puppeted foe never leaves the ground and never stops animating; only
  its TARGET changes. That is why it needed no new AI or animation.
- **Chains are bounded by GENERATION, not luck**: splash-applied Doom is one generation deeper, and a
  gen-`DoomMaxGen` bank detonates but never splashes. Dance-fed and Fray-copied Doom are tagged at the
  deepest generation. Plus a per-frame detonation budget that **defers** overflow rather than dropping it.
- **Bosses are never puppeted but always doomable.**
- Cut: Death Knell (on-demand detonation is hers alone), Death March (needs a direction a one-press
  finisher cannot give), Deadman's Switch (circular).

## 4. Files & major symbols

- `Enemy.cs` — `DoomBank`/`DoomT`/`_doomGen`/`_doomOwner`/`_doomSpreadMul`/`_doomSpreadR`, `AddDoom`,
  `DetonateDoom(frac, crit, spreadMul)`, `DoomFloorHp()` (boss gate ladder), `PackDoom`/`SetRemoteDoom`,
  `Puppet`/`PuppetHurt`/`Puppeted`, `Flee` + `RoutT`, the walker
  (`_doomWalking`/`_doomWalkPayload`/`ReleaseDoomWalk`, `DoomWalkerCap`, released in `_ExitTree`),
  `Die()` interception, `HitTarget`'s `_tgtIsEnemy` branch.
- `Player.cs` — `DoomFocus`/`FocusMax`/`DoomRate`/`DoomPower`/`DoomSpreadRadius`/`DoomSpreadMul()`,
  `BeamOrigin()`, `UpdateCurseBeam`, `FireVoodooCrush`, `FinDanseMacabre`/`FinRout`/`FinCurtainCall`,
  `ReticlePoint`/`NearestFoeTo`, Turncoat/Fray/Effigy in `ApplyChargedMods`, `EffigyTgt` + its feed hook
  inside `Enemy.Hurt`, `UpdateHexCircle`, the `"flame"` arm pose, `TestFireFinisher`/`LookAtForTest`.
- `EnemyBolt.cs` — `HitsEnemies`/`OwnerPeer`/`Shooter`. **`OwnerPeer`, not `Owner`** (collides with
  `Node.Owner` and warns).
- `Net.cs` — `ReportStatus` kinds **10** AddDoom, **11** Puppet, **12** Flee; `EnemySnapshot` gained a
  12th array carrying `PackDoom()`. Kind 9 was already `MarkConduit`.
- `Hud.cs:DrawEnemyBars` — the Doom segment on the enemy health bar + the fuse hairline.
- `Upgrade.cs` / `Finisher.cs` / `Modifier.cs` — six new abilities, trees, pool defs; six retired.
- `PerkTree.cs:ForsakenDefs` — A = Doom power, B = blast seeds/reach, C = focus, D = wraith.
- `dev/ai/AiTestRunner.cs:ForsakenDoom` — the scenario (`ScenarioWitch` entry `forsaken_doom` → witch 6).

## 5. Tests / validation performed

- `dotnet build -v quiet -nologo` → **0/0** after every step.
- `./tools/run-ai-scenario.ps1 -Scenario forsaken_doom` → **PASSED**, 9 captures, all opened and inspected;
  `godot.log` clean (no `SHADER ERROR`, no cascade-budget warnings). Measured: channel banks 13.4 in 4s ·
  Focus 1.0→2.01, **still 2.33 after a target switch** · `chained-onto-others=0` (no silent spreading) ·
  a partial charge spent 11.5→4.9 with the remainder re-fused · a full charge took core 8.0→0.3 and
  **seeded 3 neighbours** · an isolated fuse self-detonated · **Danse Macabre turned 6 / doomed 6 of 6
  trash** · Rout scattered 6/6 · the execute fired and killed.
- Verified visually: the Doom bar segment reads across a whole crowd; the beam artifact is gone.
- `grep` confirms the six retired abilities are unreachable and no perk node references a dead stat.

## 6. Current failures / uncertainties

- **Ember's `"flame"` hand pose and the first-person beam origins are UNVERIFIED** — `forsaken_doom` only
  runs her in third person. Needs an ember/FP scenario.
- **Suspected, unchecked:** the Effigy hook in `Enemy.Hurt` may double-feed off its own splash damage; a
  routing foe's 40u flee target may fight the arena clamp at the map edge.
- Effigy is attributed to `Game.I.LocalPeer` only → in MP it credits the host's witch. Needs per-peer.
- A client's Rout flees "away from the host's witch" rather than the actual caster (three float slots in
  `ReportStatus` can't carry a position).
- `CurseSpreadRange`, `CurseStackCap`, `CurseShareFrac`, `S.MarkAmp` are now vestigial — nothing reads
  them. Harmless; sweep once this is playtested.
- MP is otherwise unvalidated, like the rest of the in-flight work.

## 7. Briefly-rejected approaches

- Strings as a separate resource feeding Doom — two numbers where one would do; the owner cut it.
- Doom as a %-max-HP drain on the health bar — built on a bad memory of Soulstone; the real bank+execute
  mechanic is better and needs no percentage knob.
- Execute gated to the boss's last 20% — made the mechanic feel switched off for most of the fight.
- Melee-only puppetry — rejected by the owner; `EnemyBolt` got the enemy-collision work instead.
- An overhead number for the bank — the owner correctly called for a bar, since Doom *is* a slice of the
  health bar (the frozen-blue fraction was the existing precedent).

## 8. Next 3 concrete actions

1. Build an ember/first-person scenario to verify the `"flame"` pose and the FP beam origins.
2. Chase the two suspected bugs in §6 (Effigy double-feed, flee-vs-arena-clamp).
3. Playtest for feel: is the build→pop→spread rhythm too slow to threaten a crowd early in a fight?
