# Wardens of the Moonlit Grove — Combos & Cards Reference

A catalog of the spell-combo layers and the upgrade card pool. Combos are element-flavored but
mostly roll for any witch; the **witch-specific** cards are gated to a single witch (see bottom).

Sources in code: `Minor.cs` (MinorType), `Finisher.cs` (FinType), `Mod.cs` (ModType),
`Upgrade.cs` (card pool), `Game.cs` (`UltChoiceSet`, `RitualReward`).

---

## 1. Minor combos
Passive auto-finishers. They **stack infinitely** and fire automatically every ~12 combos
(faster as you stack more). Granted by **Ward** rituals and level-up cards. Two per element —
a "bolt at a foe" and a "burst around you":

| Element | Bolt variant | Burst / utility variant |
|---|---|---|
| Lunar  | Moon Mote (mote at a foe)        | Lunar Flare (faint lunar burst) |
| Arcane | Arcane Dart (dart at a foe)      | Mana Spark (tiny arcane burst) |
| Nature | Thorn Snap (snares nearest foe)  | Sporeling (small nature burst) |
| Frost  | Frost Nip (chills nearest foe)   | Ice Prick (tiny frost burst) |
| Curse  | Hex Wisp (hexes nearest foe)     | Rot Tick (tiny curse burst) |
| Holy   | Radiant Mote (mote at a foe)     | Glimmer (mends a sliver of HP) |
| Ember  | Cinder (ember at a foe)          | Ashflare (tiny ember burst) |
| Blood  | Bloodlet (leeching blood bolt)   | Clot (tiny burst; banks blood) |
| Wind   | Gust (dart + nudge-back)         | Zephyr (tiny wind burst) |

---

## 2. Finishers
Keyed combo spells that occupy spell slots and fire on their bound key. Granted by **Summon**
rituals and level-up. Exact offerings are weighted toward your witch's element.

**Universal pool**
- **Hex Pulse** — a ring of hexing force pulses out, damaging + cursing everything around you.
- **Spelllance** — channels a piercing lance of arcane energy straight ahead for a few seconds.
- **Spellstorm** — looses a storm of aimed bolts at the nearest foes.
- **Coven Swarm** — summons homing spectral bolts that chase foes down.
- **Snare Verse** — roots every nearby enemy and deals a Nature burst.
- **Mending Grove** — grows a circle that heals you over time and sears foes inside it.
- **Witching Hour** — fires a full-power charged cast carrying *every* charge-modifier you own at once.
- **Witch's Hollow** — opens a cursed hollow (~5s) that damages and weakens foes standing in it.
- **Crescendo** — *passive* (holds a slot): every Nth combo cast erupts on its own for bonus lunar damage.
- **Coven Bond** — *(not a spell)* +1 finisher slot, so you can chain another finisher.

**Element / witch-leaning**
- **Radiant Halo** (Holy) — a radiant nova that sears foes, heals you, and blesses you.
- **Heaven's Lances** (Holy) — calls down holy lances across a swath, leaving healing light.
- **Blood Nova** (Blood) — a close blood detonation; strong damage + knockback; kills feed your blood.
- **Crimson Rush** (Blood) — dash forward on a blood wave, bowling over and slowing everything in your path.
- **Blood Curse** (Blood) — a cone of misty blood that hexes foes; each hex banks a stack (Crimson) or mends you.
- **Creeping Blight** (Nature) — drops a creeping poison field that keeps stacking poison the longer foes linger.
- **Seed Mines** (Nature) — scatters proximity seed-mines that blast foes who wander too close.
- **Thorn Skin** (Nature) — banks a bark shield (up to 3); each charge eats a hit, then bursts for Nature damage.
- **Updraft** (Wind) — launch straight up and carry small/medium foes aloft with you; set up air follow-ups.
- **Wind Rush** (Wind) — dash forward on a gust, lightly damaging + flinging foes aside (big ones resist); ~50% to refund all dashes on a hit. Scales harder/farther with rarity.
- **Wind Slice** (Wind) — hurl a travelling X of wind that pierces and damages every foe in its path.

---

## 3. Charged-cast modifiers
Attach to your charged shot (hold to charge, release to fire). Granted by **Cleanse** rituals.

- **Frost Veil** — chills foes in an area (slow scales with rarity).
- **Bramble Root** — roots foes caught in the blast.
- **Sunder Burst** — erupts a large AoE blast.
- **Hex Mark** — marks a foe (+dmg taken); the mark leaps to a new foe on death.
- **Moonwell Beam** — leaves a moonbeam burning the area (~6s).
- **Consecrated Ground** — leaves consecrated ground that sears foes and heals you (~5s).
- **Smite** — smites the nearest foe with a holy lance + slow.
- **Hemorrhage** — hits bleed; a bleeding foe ruptures on death.
- **Crimson Pool** — leaves a blood pool that slows foes and banks blood (Crimson) or mends you.
- **Sanguine Spikes** — erupts blood spikes; each hit banks blood (Crimson) or mends you.
- **Implosion** (Wind) — a full charge damages the area, then yanks the survivors inward.
- **Whirlwind** (Wind) — a full charge spawns a stationary tornado that grinds foes and works as a jump pad any player can launch off.

---

## 4. Witch-specific cards & ultimates

Each witch has **3 ult choices**, each with a **Legendary ult-mod** card that upgrades it, plus a
set of **affinity cards** that only appear for that witch.

### 🌙 Lunar  (Arcane / night affinity)
**Ults → mod:** Lunar Eclipse → *Blood Moon* (lifesteal + slow on hit) · Lunar Light → *Radiant Font* (larger, heals more) · Crescent Moon → *Waxing Horde* (+2 crescents; forward blades pierce)
**Cards:**
- Waxing Crescent — +1 crescent pierce & +20% crescent size
- Nightfall's Gift — +Lunar damage (doubled at night)
- Lunar Eclipse — +25% ultimate charge rate (faster still at night)

### ✝️ Divine  (Holy)
**Ults → mod:** Faith Shield → *Aegis Sanctum* · Judgement → *Final Verdict* (one colossal lance + holy field) · Divinity → *Ascendant* (lasts longer; harder motes + holy ground)
**Cards:**
- Benediction — +1s blessing duration; mend a little whenever you bless
- Twin Light — your Holy mote forks to a nearby foe on hit
- Martyr's Grace — Divine Intervention erupts: full shield, heals allies, blasts foes back

### 🩸 Crimson / Blood
**Ults → mod:** Blood Tsunami → *Crimson Deluge* (wider, harder) · Exsanguinate → *Bloodthirst* (executes more) · Blood Rot → *Plague Bloom* (bigger, spreads further)
**Cards:**
- Blood Efficiency — finishers cost less health
- Blood Reserve — +8% max health
- Sanguine Frenzy — up to +25% damage the lower your health falls
- Crimson Communion — +aura radius; aura kills heal more
- Hemoclast — spending Blood Stacks also erupts a blood nova (scales with stacks spent)

### 🌿 Verdant  (Nature)
**Ults → mod:** Ancient Guardian → *Heartwood* (more slams, wider, root+poison) · Wild Swarm → *Teeming Grove* (stampede is wider, more numerous, charges longer) · Barkskin → *Ironheart* (lasts longer, wider burst, leaves a poison field)
**Cards:**
- Wildfire Bloom — your tree-ents chain-detonate (each ent explosion sets off nearby ents)
- Deepening Grove — +1 max tree-ent
- Quick Roots — tree-ents grow faster (less combo per ent)

### 🌬️ Gale  (Wind)
A hit-and-run controller: fast twin slashes on the primary, a charged gust-cone that hurls foes back, and high mobility.
**Passive — Tailwind:** starts faster with an extra dash charge, and gets a brief evasive window (~45% damage reduction) right after dashing.
**Ults → mod:** Cyclone → *Maelstrom* (bigger, longer, pulls harder) · Hurricane → *Eyewall* (lasts longer; allies AND their minions in it gain cast/charge/move speed) · Stormform → *Eye of the Storm* (while moving, leave air-mines that launch foes up for impact + fall damage)
- Cyclone — a parked tornado that drags in and grinds foes, then bursts
- Hurricane — leap aloft and pilot a steerable storm that grinds + flings enemies (they take fall damage on landing; big ones resist); she drops when it ends
- Stormform — self-buff: +50% move speed & ~40% faster casts; kills extend it; wind gusts show to all allies
**Cards:**
- Slipstream — +dash distance & faster dash recharge
- Crosswind — +gust knockback & reach
- Tempest Heart (Legendary) — full-charge gusts leave a lingering whirlwind
- Cloudfeather (Legendary) — passively mend HP while airborne
- Downburst (Legendary) — landing from a height slams a Wind shockwave (damage + fling, scales with fall speed)
- Jetstream (Legendary) — +25% damage while airborne

---

## 5. Universal stat cards  (any witch)
Witchfire (+spell dmg) · Quicksilver (+move speed) · Hex Tempo (faster casting) · Swift Conjury
(+projectile speed) · Focus (faster charge) · Overcharge (+max charged dmg) · Heartwood (+max HP) ·
Old Blood (+raw HP) · Moonglass Aegis (+shield capacity) · Swift Mending (shield recovers sooner) ·
Quickening Ward (+shield regen) · Wind Step (+dash distance) · Fleet Step (faster dash recharge) ·
Twin Step (+1 dash charge) · Mana Wellspring / Blood Efficiency (resource per hit; form swaps by witch) ·
Deep Reserve / Blood Reserve (+max mana / +HP by witch) · Siphon (lifesteal) · Piercing Sigil (+pierce) ·
Cadence (+dmg per combo & +combo cap) · Witch's Rhythm (+combo window).

---

## Ritual → reward mapping
- **Ward** ritual → a stat / minor card (category 0)
- **Summon** ritual → a finisher card (category 1)
- **Cleanse** ritual → a charged-cast modifier card (category 2)
