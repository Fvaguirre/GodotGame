# Forsaken Witch — Puppetry Rework (design spec)

**Status: DESIGN ONLY — nothing implemented.** Supersedes the current Forsaken kit (Hex Pulse,
Soul Reap, Hex Chains, Doom Sigil, Hex Mark, Cursefield), which the user rejected wholesale.

> She doesn't kill things. She makes them kill each other.

---

## 1. The rule: puppetry is not telekinesis

**Telekinesis moves bodies. Puppetry makes bodies move themselves.**

- A puppeted foe **never leaves the ground** and **never stops animating**. Every blow is thrown by
  the enemy's own arms, playing its own attack clip.
- Visual language: threads from her hands to their limbs. Audio: rope, wood-creak, tendon.
  No force waves, no whoosh, no ragdoll, no flight. If it flies, it's Gale's job — cut it.
- Cadence: puppets move on a slightly wrong beat. This codebase already layers procedural motion
  over authored clips (`GoblinSlashMod`, the `SkeletonModifier` work on the authored biped), so a
  jerky puppet gait is drivable with what exists.

**The corollary is the fun part:** puppetry only works on things with bodies, and it's *better on
better bodies*. Puppet an archer and you get arrows. A caster and you get spells. A sieger and you
get a charge. Telekinesis can never do that — this is the mechanic's whole identity.

## 2. What is fixed and may not be redesigned

- **LMB primary + RMB charge-and-release is a hard rule for all nine witches.** Mods fire on the
  full release; finishers fire off the spell combo. This spec changes what her two buttons *mean*,
  never their shape.
- **Finishers and mods are witch-agnostic.** Every Curse ability must apply its own strings so a
  non-Forsaken witch who equips two of them gets the loop — the same way Hemorrhage hands anyone
  bleed and Frost Nova hands anyone freeze. Nothing may assume her beam is present.
- Existing enemy state stays: `Enemy.CurseStacks`, `CurseT`, `CurseGroup`, `AddCurse`,
  `ConsumeCurse`, and the MP packing (stacks in mask bits 22-27, group in 28-30).

## 3. The resource: STRINGS

`CurseStacks` is **renamed, not replaced** — 0→5 strings, drawn as visible threads running from her
hand to the foe's limbs. The existing ☠ overhead counter becomes a thread count. No data change, no
save/MP break.

Two things it gains that the invisible stack counter never had:

1. **A free, visible payoff for channelling.** At 3+ strings a foe visibly *lurches* — attack
   wind-ups slow, its gait stutters. You can see you have a hold on it without reading a number.
2. **A meaning for `CurseGroup`.** Cross-strung foes now **share actions, not just damage**: when one
   puppet swings, its partners swing too, at a probability driven by the existing `CurseShareFrac`
   (so `Sympathy` / `Grim Chorus` / `Coven's Grip ★` keep working, but you can now *watch* them).
   Damage-sharing stays as-is underneath; this is additive.

## 4. Left click — THREAD (the channel)

Same shape as today: a held channel on the aimed foe. Reuses `UpdateCurseBeam` wholesale.

| | |
|---|---|
| **Does** | Attaches strings at `CurseRate`/sec (0→5). Small Curse DoT + `CurseBeamLifesteal` siphon, unchanged. |
| **Spread** | At 2+ strings, threads creep to nearby foes within `CurseSpreadRange`, capped by `MaxLinks` — the existing group machinery, now drawn as taut cords rather than a faint link. |
| **New** | 3+ strings = the lurch (slowed wind-ups, stuttering gait). This is her only "free" control and it's what makes channelling feel like it's doing something. |

## 5. Right click — PULL (charge and release)

Same shape as today: charge, release, mods fire at full. Replaces the flat per-stack damage detonation
with a **command**. Charge depth = how many strings you yank.

| Charge | Result |
|---|---|
| Tap | **Jerk** — one string. The foe stumbles and its current attack is interrupted. A cheap on-demand interrupt, always useful, no setup. |
| Partial | The foe swings its own weapon once per string consumed at whoever is nearest — including its allies. |
| Full | The full flurry, **plus your equipped mods fire** (as today). Cross-strung partners swing along at `CurseShareFrac`. |

Damage now comes from two places instead of one: the residual per-string hit (keep it, scaled down)
**and** what the puppets do to each other. `CurseStackCap` remains the ceiling on how much a single
pull can be worth, so `Anathema` / `Doombrand` / `Plague` / `Doomherald ★` all keep their meaning.

## 6. The three mods (fire on full charged release, on the struck foe)

### M1 — Conscript *(the headline)*
The struck foe turns and fights its allies for a few seconds with its own moveset, then collapses
dead. Archers shoot archers, casters cast at casters, siegers charge their own line.

### M2 — Pinned *(the cheap one, always good)*
The struck foe keeps fighting *you*, but its swings veer into whoever is standing beside it. No AI
takeover — just a retarget on its existing attack. This is the mod that carries a non-Forsaken witch.

### M3 — Bad Company *(control + applicator)*
The struck foe seizes its nearest ally in its own arms. Both are held in place, both gain strings,
both take rot while grappling. If either dies the other takes a heavy hit.

All three apply 1–2 strings on hit — this is what makes the family portable.

## 7. The three finishers (one cast, off the spell combo)

### F1 — Danse Macabre *(chaos)*
Every foe near you is forced into a few seconds of synchronised swinging, each hitting whoever is
beside it. Feet planted, own attack animations, total chaos. The crowd moment.

### F2 — Death March *(control / reposition)*
Several foes march on their own legs to a point you choose, trampling what they pass, and come apart
when they arrive. A gather that is also a delivery — uniquely puppetry, and explicitly *not* a pull.

### F3 — Cut the Strings *(payoff)*
Everything you have strung or puppeted drops at once. Not an explosion — the bodies simply fall, and
whatever they land on takes it. The payoff for a puppeteer is the *release*.

Three distinct verbs: chaos, control, payoff. None is a nova centred on you.

## 8. Upgrade paths (`AbilityUpg` format: 3 stat + Epic + Legendary, each stacking to 4)

**Finishers**

| Ability | Stat ① | Stat ② | Stat ③ | Epic | Legendary |
|---|---|---|---|---|---|
| **Danse Macabre** | Tempo — +duration | Ballroom — +radius | Fervor — +damage of their swings | **Encore** — a dancer that dies pulls the nearest foe into the dance | **Grand Finale** — when the music stops every survivor lands one maximum-force blow |
| **Death March** | Conscription — +foes marched | Forced March — +march speed & range | Trampling — +damage on arrival | **Pallbearers** — marchers trample everything on the route | **Mass Grave** — the destination becomes a pit that strings anything entering |
| **Cut the Strings** | Severance — +damage per string | Wide Cut — +foes affected | Deadfall — +damage to what they land on | **Puppet's Rest** — each collapse throws strings to the nearest foe | **Curtain Call** — the fallen rise briefly as husks and keep fighting for you |

**Mods**

| Ability | Stat ① | Stat ② | Stat ③ | Epic | Legendary |
|---|---|---|---|---|---|
| **Conscript** | Loyalty — +duration | Vigor — +puppet damage | Press-gang — +strings applied | **Understudy** — when a conscript dies another nearby foe is conscripted | **Warband** — hold two conscripts at once |
| **Pinned** | Barbs — +redirected damage | Deep Pin — +duration | Wide Pin — +chance to veer | **Crossed Pins** — the neighbour it hits gets pinned too | **Pincushion** — a pinned foe strikes *every* adjacent ally, not one |
| **Bad Company** | Grip — +hold duration | Rot — +damage while held | Company — +strings on both | **Chain Gang** — a third foe is dragged in | **Misery Loves** — if one dies the other dies with it |

## 9. Perk tree (36 nodes; columns are four distinct builds per `PerkTree.cs` NODE-DESIGN RULES)

Retheme the columns from *A hex · B tethers · C siphon · D wraith* to:

- **A — The Hand** (command power): pull damage, `CurseStackCap`, `CurseBonusMul`.
  Blight · Virulence · Anathema · Doombrand · Malefic · Torment · Gravemark → **Puppetmaster ★**
  (*a pull makes the puppet swing at every adjacent foe, not just the nearest*).
- **B — The Company** (how many puppets you hold): `MaxLinks` **repurposed as the concurrent-puppet
  cap**, conscript duration, action-sharing.
  Bindings · Coven Bind · Soulbind · Sympathy · Grim Chorus · Wraith Choir → **Full Company ★**
  (*+3 concurrent puppets, +conscript duration, +25% action sharing*).
- **C — The Threads** (how fast you string things): `CurseRate`, `CurseSpreadRange`, siphon.
  Contagion · Farhex · Deep Hex · Siphon · Soul Drain · Soul Glutton → **Marionettist ★**
  (*strings attach at a distance and spread twice as fast*).
- **D — The Wraith** (guard): unchanged — Insubstantial · Dreadbone · Rotplate · Soulhide ·
  Hexguard · Boneguard · Shroud → **Revenant King ★**.

Hidden routes retarget the same way: *Hexer* → command power, *Doom Herald* → company size,
*Soul Eater* → unchanged.

Constraints carried over: no node may grant a legendary-card gate (rule 4), and **`S.Pierce` stays
out of her tree** (rule 5 — it is a no-op for beam kits).

## 10. Ults

| Ult | Verdict |
|---|---|
| **HexCircle → Grand Guignol** | Rework. A stage ring: everything inside is strung and turns on itself until one remains, and the survivor walks out as your puppet until killed. Bosses inside are strung but never conscripted — they eat the accumulated damage instead. |
| **LifeDrain → Cat's Cradle** | Rework, cheap. Keeps the existing rise-drain-release shape (`UpdateLifeDrain` + `EndLifeDrainBurst`): while aloft every foe below is strung to her and lurches into its neighbours; the release snaps every string at once for the banked damage. |
| **LifeCurse / Specter** | **Leave alone.** Recent deliberate rework, works, and it's her only escape valve. One optional tie-in: foes she drifts through while immaterial come out strung. |

## 11. Engineering dependencies (the real cost)

1. **Enemy-vs-enemy damage does not exist yet.** A puppet's attack must damage enemies, be
   **host-authoritative**, and credit the owning player so XP / souls / kill credit / Highlight all
   work. Cheapest viable path: swap the puppeted foe's *target* and let its existing attack code run
   — do not write new AI.
2. **The fan-out must be budget-capped.** Danse Macabre + Grand Guignol + action-sharing are exactly
   the uncapped-cascade shape that froze MP once already (`DEV_HANDOFF.md`; see the existing
   `_curseShareGuard` / per-frame ceiling at `Enemy.cs:921`).
3. **MP sync needs its own RPC — `StatusMask` is out of bits.** Curse stacks already occupy bits
   22-27 and the group id 28-30. Puppet state (owner + remaining duration) must ride a dedicated
   small RPC, the way melee swings had to.
4. **Boss / miniboss policy.** Bosses must not be conscriptable. Proposal: they can be strung and
   pinned (redirect, reduced) but never taken over.

## 12. Open questions

- Do puppets take damage from their former allies normally? (Recommended: yes — that's the point, and
  it keeps Conscript from being a free tank.)
- Does a puppet killed *by you* still count for souls/XP, or only ally kills? (Recommended: both.)
- Warded / armoured foes — does a ward block stringing outright, or just slow it?
- Should `Pinned` work on ranged foes (arrow veers into an ally) or melee only?
