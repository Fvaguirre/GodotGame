# Witch Cast → Animation Mapping

Fill in the **Anim** field under each cast with the clip you want. Browse the clips in-game with **`animview`** (`[` / `]` to step through) — the name shown above her is exactly what to paste here.

- **Primary** = left-click. **Secondary** = right-click (charge-and-hold, releases on let-go).
- Leave a field blank to keep it un-mapped for now. Add notes freely in the **Notes** lines.

## Clip palette (the 12 casting clips in the viewer)
```
standing 1H cast spell 01
Standing 1H Magic Attack 01
Standing 1H Magic Attack 02
Standing 1H Magic Attack 03
Standing 2H Cast Spell 01
Standing 2H Magic Attack 01
Standing 2H Magic Attack 02
Standing 2H Magic Attack 03
Standing 2H Magic Attack 04
Standing 2H Magic Attack 05
Standing 2H Magic Area Attack 01
Standing 2H Magic Area Attack 02
```
> These play on the RIGHT hand by default; mark **(mirror)** after a clip name if you want it flipped to the left hand.

---

## 0 — Lunar · *Lunar*  ·  **✅ CURRENTLY WIRED (the test witch)**

### Primary (Left-click) — Lunar Bolt
- **Does:** single straight lunar bolt (~50 u/s), builds combo; doubles at night.
- **Primitive look:** no gesture — just the alternating recoil kick + idle bob; glowing lunar bolt.
- **→ Anim:** `Standing 1H Magic Attack 01 (mirror)` — *hold-blend: left arm thrusts forward on press, holds extended while rapid-firing, recovers on release. Muzzle = left hand.*
- **Notes:** change this if you want a different primary clip.

### Secondary (Right-click, charged) — Focused Bolt / Crescent
- **Does:** charged high-damage bolt (up to ~4.8×). **Full charge = a wide horizontal crescent** that grows and cleaves (pierce 6+), drops a lunar ground sigil.
- **Primitive look:** generic charge — hands spread apart, center orb swells/whitens at full; both-hand muzzle flash on release.
- **→ Anim (charge/hold):** `Standing 2H Cast Spell 01` — *both hands gather; pose blends in by charge amount and holds while right-click is held.*
- **→ Anim (release):** `Standing 2H Magic Attack 01` — *2H thrust, played at 3.75× so it snaps out with the projectile. Muzzle = both hands.*
- **Notes:** the secondary uses a 2-part gather→release; other witches can too, or just a release clip.

---

## 1 — Divine · *Holy*

### Primary (Left-click) — Holy Mote
- **Does:** a light mote; **homes** if a foe is in the aim cone, else flies straight. Airborne (Radiant) it heals allies it passes.
- **Primitive look:** alternating recoil kick only; holy homing orb.
- **→ Anim:** `________________________`
- **Notes:**

### Secondary (Right-click, charged) — Holy Ray
- **Does:** a **column of light descends from the sky and sweeps forward** along the aim, searing at its leading edge and leaving a consecrated strip (sears foes / heals allies). Full charge blesses caster+allies; Divine also chains **Radiant Smite** pillars onto nearby foes.
- **Primitive look:** generic charge spread + center orb; the ray is a tall light-cylinder tweened forward.
- **→ Anim:** `________________________`
- **Notes:**

---

## 2 — Crimson · *Blood*

### Primary (Left-click) — Blood Lash *(melee)*
- **Does:** a ~57° forward **melee arc** (not a projectile), direct damage + light knockback, can crit / hit boss crit-zones.
- **Primitive look:** recoil kick + a flurry of **3 blade slashes** tweening in at random angles + ground slash decals.
- **→ Anim:** `________________________`
- **Notes:**

### Secondary (Right-click, charged) — Crimson Tide
- **Does:** a **radial blood-spin nova** centered on her (radius scales with charge), damage/knockback/slow; **costs ~4% HP**. Full charge = ritual sigil rings + lingering ground circle; consumes blood stacks to heal.
- **Primitive look:** dark blood orb bound in glowing ritual rings swells at her chest, spins, bursts with shock rings; both-hand muzzle on release.
- **→ Anim:** `________________________`
- **Notes:**

---

## 3 — Verdant · *Nature*

### Primary (Left-click) — Poison Needles
- **Does:** a burst of **3 thin poison needles**, fast/weak, random spread, applies poison DoT.
- **Primitive look:** recoil kick; thin purple bolts.
- **→ Anim:** `________________________`
- **Notes:**

### Secondary (Right-click, charged) — Thorn Spike
- **Does:** a knotted-wood **spike projectile** with travel time; damage/size scale with charge. Full charge pierces all, detonates her summoned ents (they do the rooting), drops a bramble patch.
- **Primitive look:** charge visual is a translucent **knotted-wood cone spike** forming at screen center (not the generic orb); fired spike is solid.
- **→ Anim:** `________________________`
- **Notes:**

---

## 4 — Gale · *Wind*

### Primary (Left-click) — Wind Punch *(melee)*
- **Does:** a **frontal-arc wind punch** (~57°), area damage + knockback; stronger while airborne / vs airborne foes.
- **Primitive look:** `barrage` = **rapid alternating jabs** + recoil, popping **6 wind-fist spheres** out in front.
- **→ Anim:** `________________________`
- **Notes:**

### Secondary (Right-click, charged) — Gale Slam / Dive
- **Does:** hold to charge; on ground, release = **ground-slam radial Wind AoE + knockback**. In the **air**, holding hovers + aims a ground ring, release **rockets her down** to slam there. Full charge adds a lingering whirlwind (Tempest).
- **Primitive look:** `grdpunch` = **wind fists up, then drive down into the ground**; downdraft funnel + pressure rings + gust streaks erupt; teal ground aim-ring while air-charging.
- **→ Anim:** `________________________`
- **Notes:**

---

## 5 — Frost · *Frost*  ·  **channeled primary**

### Primary (Left-click, HELD) — Frost Beam
- **Does:** a **held channeled freezing beam** (up to ~46 range). Locked foe takes Frost DoT + **1 freeze stack/sec** + slow; splash-chills the surrounding pack.
- **Primitive look:** a cyan beam that **bows/whips** as she strafes, from her hand; no arm pose (beam draws itself).
- **→ Anim:** `________________________`
- **Notes:** *(held/channel — may want a hold pose rather than a one-shot)*

### Secondary (Right-click, charged) — Icicle Spear
- **Does:** a charged **icicle spear**, pierces 3 (20 w/ Glacial Impaler); full charge adds a 2nd crit roll + **shatters frozen foes**.
- **Primitive look:** a **ballista/bow pose** — right palm lifts up-and-out pulled back, left steadies forward — with a **nocked ice arrow** that draws + grows with charge; FOV zooms in as she draws.
- **→ Anim:** `________________________`
- **Notes:**

---

## 6 — Forsaken · *Curse*  ·  **channeled primary**

### Primary (Left-click, HELD) — Curse Beam
- **Does:** a **lock-on curse-suck beam** — sticky-locks the nearest reticle foe, builds curse stacks + low DoT; at 2 stacks **tethers foes into a shared-damage group** that spreads every 0.5s (drawn tether links).
- **Primitive look:** a purple beam pouring from her **left hand**, bowing/whipping; right hand always holds the **voodoo doll**.
- **→ Anim:** `________________________`
- **Notes:** *(held/channel — left hand)*

### Secondary (Right-click, charged) — Voodoo Crush
- **Does:** a **hitscan crush** on the cursed foe — consumes its curse stacks (tap=1, full=all) and **detonates** them, sharing to the group before breaking the tether.
- **Primitive look:** `crush` = **reach out, clasp, and YANK the curse in**; the held voodoo doll clenches, glows on-target, and **pops in her grip** on release + inward curse-shard implosion.
- **→ Anim:** `________________________`
- **Notes:**

---

## 7 — Ember · *Ember*  ·  **channeled primary**

### Primary (Left-click, HELD) — Flame Cone
- **Does:** a **held flamethrower cone** ticking at cast-speed; direct damage + stacks **Burn** toward Living Bomb; huge reach under Phoenix.
- **Primitive look:** continuous fire pouring **from her left hand** along the aim; no arm pose.
- **→ Anim:** `________________________`
- **Notes:** *(held/channel — left hand)*

### Secondary (Right-click, charged) — Meteor
- **Does:** **hold to aim a ground ring** under the reticle, release to **call a meteor** there; blast radius grows with hold, applies burn stacks.
- **Primitive look:** orange **ember aim-torus** on the ground scaled to blast radius; generic spread hands + center orb; camera kick on release.
- **→ Anim:** `________________________`
- **Notes:**

---

## 8 — Arcane · *Arcane*  ·  **burst primary**

### Primary (Left-click) — Arcane Missile Burst
- **Does:** a **3-round homing missile burst** from the left hand (0.085s cadence); each **homes** toward the cursor foe and **marks** it (uncapped, 3s); her crits heal her.
- **Primitive look:** `flick` = **left hand snaps forward+out**, flicking the burst; persistent **plasma crackle** on both hands.
- **→ Anim:** `________________________`
- **Notes:**

### Secondary (Right-click, charged) — Arcane Chain
- **Does:** **jagged chain-lightning** zigzagging from her right hand through **every marked foe** (piercing between), scaling with charge; marked endpoints get 2× crit; **burns off the marks**. No marks → single hitscan.
- **Primitive look:** charging conjures a **growing plasma orb between her palms** (hands cradling it, left below / right above); arms **fade translucent** near full; release draws the jagged bolt + right-hand kick.
- **→ Anim:** `________________________`
- **Notes:**

---

### General notes / requests
_(anything global — e.g. "all channeled primaries should hold a pose", preferred mirror side, etc.)_

