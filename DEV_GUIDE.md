# Wardens of the Moonlit Grove — Developer Guide

A first-person roguelite spellcaster built on **Godot 4.7 / .NET 8 (C#)**. This document maps the
architecture, then gives step-by-step recipes for the changes you'll actually make: adding enemies,
witches, and abilities, and tuning damage / health / difficulty.

> **Golden rule for this codebase:** it is **host-authoritative**. The host simulates the world
> (enemies, waves, bosses, loot). Clients own only their own avatar and route their damage to the
> host. Almost every "why doesn't this work in multiplayer?" bug traces back to forgetting that.
> See [Multiplayer model](#multiplayer-model).

---

## 1. Build & run

- Open the project in **Godot 4.7 (.NET/Mono build)**. `project.godot` sets the main scene to
  `res://Main.tscn`, whose root is a `Node3D` named **Game** running `Game.cs`.
- `.cs` files live **flat** in the project root. `GroveGodot.csproj` targets `net8.0`,
  `Godot.NET.Sdk/4.7.0`, `<Nullable>disable</Nullable>`.
- Multiplayer is **LAN ENet**, up to 4 players, port **7777**. Host from the lobby; clients join by IP.
- Testing is done in-editor across two PCs on the LAN. There is no headless CI; **compile in the
  editor and read the Errors panel** — that is the source of truth.

---

## 2. File map

Files are grouped by role. Sizes are approximate; the four big ones (`Player`, `Game`, `Hud`,
`Enemy`, `Net`) are where most logic lives.

**Core loop & state**
- `Game.cs` — the brain. State machine (`GameState`), wave spawner, enemy bookkeeping, all menu/UI
  routing, input dispatch, witch/ult selection, run economy (XP, boss tokens), VFX helpers
  (`VfxRing`, `Emissive`, `ToonEmissive`, `SurfaceHeight`).
- `World.cs` — the arena: terrain/ground, props, arena bounds, `SurfaceHeight` source data.
- `Lobby.cs` — pre-game lobby & host/join flow.

**The player**
- `Player.cs` — the witch: movement, casting pipeline, all four witches' primaries/secondaries,
  finishers, modifiers, ultimates, combos, mana/blood/shield economies, leveling.
- `Stats.cs` — the full stat block (every tunable per-run number). One instance per player (`Player.S`).
- `WitchModel.cs` — procedural witch body (robe, hat, wings) + per-witch color.
- `RemoteAvatar.cs` — how an **ally** is drawn on your screen (their witch model + nameplate + x-ray).

**Enemies**
- `Enemy.cs` — every enemy: stat table (`Configure`), AI behaviors (`EBehav`), status effects
  (bleed/poison/slow/root/mark), elite **affixes**, death/loot.
- `Creature.cs` — procedural enemy bodies (`CreatureKind`: Orc, Mosquito, Spider, …).
- `RemoteEnemy.cs` — the **client-side enemy proxy table** (`EnemyKinds`: string↔index↔color) used to
  render host enemies on clients.
- `EnemyBolt.cs` — enemy projectiles.

**Abilities & progression**
- `Finisher.cs` — `FinType` enum + display metadata for spell-combo finishers.
- `Modifier.cs` — `ModType` enum + metadata for charged-cast modifiers.
- `Upgrade.cs` — the **card pool** (`UpgradePool`): every level-up card, rarity weights, luck biasing.
- `Bolt.cs` — the universal projectile (all witch bolts, needles, thorns). Visual styles live here.
- VFX/ability set pieces: `BloodWave`, `CrescentOrb`, `EclipseBurst`, `EclipseVfx`, `ElementBeam`,
  `FaithShield`, `Field` (ground fields), `HolyGround`, `HolyPulse`, `Orb`, `RitualCircle`, `Vfx`.
- `Thornling.cs` — the Verdant witch's tree-ent minion (owner AI + network ghost mode).

**Networking**
- `Net.cs` — all multiplayer: connection, the 20 Hz broadcast loop, every RPC, proxy reconciliation.

**UI / feedback**
- `Hud.cs` — all on-screen drawing (health/mana/combo, enemy bars, char-select, level-up cards,
  ult menu, banners, the Verdant Grove widget).
- `DamagePopup.cs` — floating damage numbers (incl. crit/amp styling).
- `Sfx.cs` — procedural audio.

**Run events & vendors**
- `RouletteMachine.cs`, `Mystic.cs`, `ScrollVendor.cs`, `Chest.cs`, `RemotePickup.cs` — between-wave
  shops/events and pickups.

**Shared**
- `DamageType.cs` — the `DamageType` enum + `DamageTypes` helper (names, colors). The spine of the
  whole element system.

---

## 3. Multiplayer model

Read this before touching anything that spawns, damages, or heals.

**Roles**
- **Host owns the world.** Enemies, waves, bosses, loot goblins, and run progression are *real* only
  on the host. The host's `Enemy` objects have authoritative HP/AI.
- **Clients own their avatar.** Each client simulates only its own `Player`. Every other entity it
  sees is a **proxy** (ghost) driven by host snapshots.

**The proxies (all in `Net.cs`)**
- `RemoteAvatar` — one per other player; driven by `NetState` (20 Hz pos+yaw+floating) and `NetVitals`
  (~5 Hz hp/mana/shield/blessed/blood/witchIndex, used for ally HUD bars & correct witch color).
- Remote enemies — `EnemySnapshot` RPC sends ids/types/elite/**hp-fraction**/status/x/z/affix. Clients
  create/update/destroy `Enemy` proxies (`e.Remote = true`) to match. *HP is sent as a fraction so the
  client health bar is correct even though the proxy was configured at wave 1.*
- Remote bolts — `BroadcastPBolt` → `ReceivePBolt` makes a visual-only ghost `Bolt`.
- Remote VFX — `BroadcastVfx(kind,o,dir,a,b,col)` → `ReceiveVfx` (kinds 0–9: ring, beam, orb, lash,
  aura, dome, burst, etc.).
- Remote minions — `MinionSnapshot` (Verdant ents): each Verdant player broadcasts its ents
  (pos/yaw/attack-pulse) at ~10 Hz; everyone else renders **ghost `Thornling`s** that follow + lunge.

**Damage flow (the important part)**
- On the **host**, a hit calls `Enemy.Hurt(...)` directly — authoritative.
- On a **client**, the client's bolt collides with a *proxy*; `Enemy.Hurt` on a proxy **routes to the
  host** via `ReportHit` instead of killing locally. The host applies the real damage and the result
  comes back in the next `EnemySnapshot`. **Clients never `Die()` an enemy locally.**
- Enemy→player damage is host-authoritative too: `DamagePlayer(peer,dmg)` → `ReceivePlayerDamage`.

**Practical rule:** any new damage/heal/kill must work through these paths. If you write
`enemy.Die()` or apply a DoT directly on a client, it desyncs. DoT *ticks* are intentionally
host-side (see [Tuning › DoTs](#dots-poison-bleed)).

---

## 4. The combat pipeline

A single cast travels through these stages (all in `Player.cs` unless noted):

1. **Input** → `Combat(dt)` reads fire/charge. A tap fires the primary; a hold charges and the
   release fires the secondary.
2. **Dispatch** → `Combat` routes a charged release by `SecondaryType`
   (`Holy`→`FireHolyRay`, `Blood`→`FireCrimsonTide`, else→`FireBolt`). Taps call `FireBolt` with
   `isNormal = true`.
3. **`FireBolt(charge)`** → branches by `PrimaryType`/`SecondaryType`:
   - Blood primary → `FireBloodLash`; Lunar full-charge → flat crescent; Nature primary → purple
     **needle** `SpawnBolt`; Nature secondary → `FireThorn` (knotted-wood projectile); else a generic
     `SpawnBolt`.
4. **`SpawnBolt(...)`** → constructs a `Bolt`, rolls **crit** (`S.CritChance`/`CritMult()`), scales
   range by `S.SpellRange`, and broadcasts a ghost copy (`BroadcastPBolt`).
5. **`Bolt._Process`** → moves, optionally homes, checks collisions. On hit it calls
   `enemy.Hurt(Dmg, DType, FromCombo, Crit)`, applies on-hit riders (poison/root), then `Src.OnHit`.
6. **`Player.OnHit` / `OnHitCore`** → mana refund, combo gain (`AddCombo`), lifesteal, ult charge,
   blood-stack banking. **This is where combo and ult charge come from**, so any new "real" attack
   should funnel through it (or call `OnHitDirect` for hitscans).
7. **`Enemy.Hurt`** → applies resist/armor, crit/mark amplification, spawns the damage popup, and on
   lethal calls `Die()` (host only) → score/XP/loot/`Explode` (Volatile affix) etc.

**Damage math:** every ability's damage is built on `Base()`:
```
Base() = 10 * S.Atk * UltDmgMul * DamageMul     // Player.cs
```
- `S.Atk` — the run-long damage stat (level-ups multiply it).
- `UltDmgMul` — 1.0 normally, **2.0 while an ult is active** (set in `ActivateUlt`).
- `DamageMul` — the **per-witch** balance knob (set in `Game.ConfigureWitch`).

An ability then multiplies `Base()` by its own coefficient and `ComboMul()`. Example (Verdant thorn):
`dmg = Base() * (0.6f + c * 1.6f) * ComboMul()`.

---

## 5. System deep-dives

### 5.1 Stats (`Stats.cs`)
One `Stats` per player at `Player.S`. Every per-run tunable lives here; this is your first stop for
balance. Key fields: `Atk`, `FireCd` (cast cooldown), `Speed`, `ChargeSpeed`, `MaxCharge`, `MaxHp`,
`Lifesteal`, `DmgResist`, `ManaMax`/`ManaGain`, `CritChance`/`CritDamage`, `SpellRange`/`SpellArea`,
`Luck`, shield (`ShieldPct`/`ShieldDelay`/`ShieldRegen`), combo (`ComboPow`/`ComboCap`/`ComboWindow`),
dash (`DashDist`/`DashCd`/`DashCharges`), `FinSlots`/`ModSlots`, mark (`MarkJumps`/`MarkAmp`).
Defaults here are the **base witch**; `ConfigureWitch` and level-up cards modify from there.

### 5.2 Player & witches (`Player.cs`)
- **Witch identity** is a set of flags (`DivineWitch`, `CrimsonWitch`, `VerdantWitch`; Lunar = none)
  plus `PrimaryType`/`SecondaryType`/`NightAffinity`, all set in `Game.ConfigureWitch(i)`.
  `WitchIndex` derives 0–3 from the flags and drives model color + ally sync.
- **Economies:** mana (`Mana`/`S.ManaMax`), Crimson blood (`BloodStacks`, `FinHpCost`), Verdant Grove
  (`Ents`, `_entCombo`, `MaxEnts`), shield (`Shield`/`MaxShield`).
- **Combos:** `AddCombo(gain)` accrues toward `ComboCap`; `ComboMul()` scales combo casts. Verdant
  hooks Grove summons here.
- **Ults:** `Ult` (`UltKind`), `UltTier` (0–4), `UltCharge`. `TryUlt`/`ActivateUlt` fire them;
  `UltDmgMul` doubles damage while active. Mod flags (`ModEclipse`, `ModRot`, …) toggle legendary
  ult upgrades.

### 5.3 Enemies (`Enemy.cs`)
- **`Configure(type, wave)`** is the stat table — a big `switch (type)` setting `MaxHp`, `Speed`,
  `Dmg`, `Score`, `Radius`, `Col`, `_behav`, and any specials (`_armorDR`, `_splitter`, `_range`,
  `_flyY`, etc.). HP/damage scaling is applied here (see [Tuning › Difficulty](#difficulty-scaling)).
- **`EBehav`** picks the AI each frame: `Melee`, `Ranged`, `Charged`, `Flyer`, `Healer`, `Goblin`,
  `Boss`, `Zapper`, `Bomber`, `Diver`, `Hexer`, `Totem` (each has a `Move*` method).
- **Status effects:** `Bleed`, `Poison` (additive + slow), `Slow`, `Root`, `Mark`, `Knockback`.
  Status is packed into a bitmask synced to clients (`UpdateStatusVisual`/`SetRemoteStatus`).
- **Affixes (elites):** `Affix` int 0–5 (Shielded/Frenzied/Vampiric/Volatile/Armored) via `MakeAffix`,
  rolled in `Game.SpawnEnemy`, synced in the snapshot, drawn as a colored aura.
- **Body:** `Enemy._Ready` maps `_type` → `CreatureKind` and builds a `Creature`.

### 5.4 Game loop (`Game.cs`)
- **`GameState`** drives everything (Lobby, CharSelect, Playing, LevelUp, Ult, Roulette, Pause, Over…).
  Input handling and `Hud` drawing both branch on it.
- **Wave spawning** (the `add(...)` block, ~`Game.cs:828`): each wave builds a list via per-type
  count formulas, shuffles it, queues it, then adds boss/miniboss/roulette/goblin/ritual events.
  `WardenCount` inflates counts (`cm = 1 + 0.55*(WardenCount-1)`).
- **`SpawnEnemy(type)`** / **`SpawnEnemyAt(type,pos)`** create + register enemies (host); elite/affix
  rolls happen here.
- **Economy:** XP/levels (`Player.LevelUp` → `OpenLevelUp` → cards), `BossTokens` (ult upgrades).

### 5.5 Cards & progression (`Upgrade.cs`)
- A card is an `UpgradeCard { Title, Desc, Rarity, Apply, FinKind?, ModKind?, … }`.
- The pool is a list of `UpgradeDef { Rars, Make }`. `Make(rarity, magnitude)` builds the card.
  Helpers: `Card(...)` (a plain stat card via `Apply`), `FinCard(...)` (grants a finisher),
  `ModCard(...)` (grants a modifier).
- **Rarity** weights and **Luck** biasing live in the roll functions; higher Luck shifts odds toward
  Epic/Legendary.
- Ult-specific upgrade cards are generated from a `switch (Player.Ult)` so each ult has its own boons.

### 5.6 Finishers, modifiers, ults
- **Finisher** (`FinType`, `Finisher.cs`) = a spell-combo that fires every Nth combo cast. Granted by a
  `FinCard`, equipped via `Player.EquipFinisher(type, every, pow, rarity)`, executed in the big
  `switch` in `ExecuteFinisher` (`Player.cs:~1346`). **Witch-agnostic** — any witch can equip any
  finisher.
- **Modifier** (`ModType`, `Modifier.cs`) = a rider on your charged cast. Granted by a `ModCard`,
  equipped via `Player.EquipModifier(type, mag, rarity)`, applied in `ApplyChargedMods`.
- **Ult** (`UltKind`) = the big cooldown/charge ability, chosen once (`Game.ChooseUlt`), upgraded with
  boss tokens (`UltTier` 0–4), fired in `ActivateUlt`'s `switch`. Legendary ult mods are the
  `Mod*` bool flags.

---

## 6. Recipes

### 6.1 Add a new enemy type

Worked example: a `"stalker"` melee that occasionally blinks toward you.

1. **Stat row** — `Enemy.Configure`, add a `case "stalker":` with `MaxHp`, `Speed`, `Dmg`, `Score`,
   `Radius`, `Col`, and `_behav`. Use `* hs` on HP so it scales (`* bhs` for boss-tier).
   ```csharp
   case "stalker": MaxHp = 18 * hs; Speed = 6.0f; Dmg = 14; Score = 26;
       Radius = 1.0f; Col = new Color(0.4f,0.4f,0.5f); _behav = EBehav.Melee; break;
   ```
2. **Behavior** — reuse an existing `EBehav` (above) or add a new one:
   - extend the `EBehav` enum (`Enemy.cs:3`),
   - add a `MoveStalker(dt, …)` method,
   - call it from the behavior switch in `Enemy._Process`.
3. **Body** — in `Enemy._Ready`, map `_type == "stalker"` to a `CreatureKind` (add a new kind in
   `Creature.cs` if you want a distinct silhouette).
4. **Client rendering** — add `"stalker"` to the `EnemyKinds.Types` table (`RemoteEnemy.cs`) and give it
   a `Col(idx)` entry, so clients can build the proxy. **The string must be in the table or clients
   can't spawn it.**
5. **Put it in waves** — add an `add("stalker", <count formula>)` line in the wave block
   (`Game.cs:~828`), gated by a `Wave >=` threshold.
6. **(Optional) specials** — armor (`_armorDR`), splitting (`_splitter` + `SpawnEnemyAt` children),
   ranged (`_range`/`_fireEvery`), flight (`_flyY`), etc., are all set in `Configure`.

**Checklist:** Configure row ✔ · behavior ✔ · `_Ready` body map ✔ · `EnemyKinds` table+color ✔ ·
wave formula ✔. Miss the `EnemyKinds` entry and it works on host but is invisible/crashes on clients.

### 6.2 Add a new playable witch

Worked example mirrors how Verdant (index 3) was added.

1. **Identity flag** — add `public bool MyWitch = false;` in `Player.cs` and extend `WitchIndex`
   (the `=> ... ? 3 : ...` chain) to return your new index.
2. **Config** — add a `case N:` in `Game.ConfigureWitch` setting `PrimaryType`, `SecondaryType`,
   `NightAffinity`, your flag, `DamageMul`, and `S.DmgResist`. End calls `RetintHands()`.
3. **Color** — add your index to `WitchModel.WitchColor(idx)` (drives model + hands + ally proxy).
4. **Primary/secondary** — in `FireBolt`, add branches for your `PrimaryType` (tap) and the charged
   release. If your element overlaps an existing one, gate on the witch flag too.
5. **Passive** — hook it wherever it triggers (e.g. Verdant's Grove is hooked in `AddCombo`; Divine's
   intervention in level-up/wave code). Add any per-witch economy fields to `Player`.
6. **Character select** — `Hud.DrawCharSelect`: bump the card layout, add a `WitchCard(...)` with your
   blurb and `[N]` hint, and ensure `RWitch[]` has a slot. Wire the click (`Game` → `ChooseWitch(N)`)
   and the number key (`pickN`).
7. **(If MP-visible minions/objects)** — add a snapshot RPC like `MinionSnapshot` and a ghost mode,
   per the [Multiplayer model](#multiplayer-model).

**Checklist:** flag+`WitchIndex` ✔ · `ConfigureWitch` ✔ · `WitchColor` ✔ · primary/secondary in
`FireBolt` ✔ · passive hook ✔ · char-select card+click+key ✔.

### 6.3 Add a new ability

Pick the slot:

**Primary fire (tap) or charged secondary** — both live in `Player.FireBolt`. Add a branch on the
element/witch and either call `SpawnBolt(...)` (projectile) or write a hitscan that calls
`enemy.Hurt(...)` + `OnHitDirect(...)`. To make it crit, route through `SpawnBolt` (auto) or roll
`S.CritChance` + `CritMult()` yourself. Cost/refund is handled in `Combat` (charged release spends
0.5 mana, or `FinHpCost` HP for Crimson).

**Spell-combo finisher (witch-agnostic, fires every Nth combo)**
1. Add a value to `FinType` (`Finisher.cs`) and a display name in `FinMeta`.
2. Add the execution case to the `switch` in `ExecuteFinisher` (`Player.cs:~1346`), e.g.
   `case FinType.MyFin: FinMyThing(pow, t, col); SetArm("thrust", 0.4f); break;` and write `FinMyThing`.
3. Add it to the card pool: a `Def(<rarities>, (r,m)=>FinCard(r, FinType.MyFin, <every>, <pow>, "desc"))`
   line in `UpgradePool` (`Upgrade.cs`).
4. (Optional) a legendary modifier card that buffs it — see "effects" below.

**Charged-cast modifier (rider on the charged release)**
1. Add to `ModType` (`Modifier.cs`) + `ModMeta` name.
2. Handle it in `ApplyChargedMods` (the `switch (m.Type)`), reading the magnitude from the equipped
   `Modifier`.
3. Add a `ModCard` `Def(...)` to `UpgradePool`. `EquipModifier` already stores it.

**Passive spell-combo** — model it as a `FinType` whose execution sets a persistent flag/state instead
of firing once (Crescendo is the template: it holds a finisher slot but changes behavior passively).

**Ultimate**
1. Add to `UltKind` (`Player.cs:45`).
2. Add an execution `case` in `ActivateUlt` (`Player.cs:~1586`). Remember `UltDmgMul = 2f` is set on
   activation and reset when it ends.
3. Add it to `Game.UltChoiceSet()` so it can be chosen, and give it ult-upgrade cards in the
   `switch (Player.Ult)` block in `Upgrade.cs`.
4. Add a `Mod<Name>` bool flag (reset in `Game.ChooseUlt`) for its legendary upgrade.

### 6.4 Add a new effect for an ability

"Effects" are the riders that fire on hit / on cast. Patterns already in the codebase:

- **On-hit status** — give the projectile a field and apply it in `Bolt._Process` on hit. Template:
  `Bolt.Poison`/`PoisonDur` (additive poison) and `Bolt.RootOnHit`. Add a field, set it via a new
  `SpawnBolt` parameter, apply it next to the `e.Hurt(...)` call, and **broadcast it if it's visual**
  (e.g. `Bolt.Style` is sent through `BroadcastPBolt`/`ReceivePBolt`).
- **On-hit world interaction** — `Bolt.DetonatesEnts` is the template: each frame the bolt scans
  `Src.Ents` and triggers `Detonate()`. Use this shape for "passes through X and does Y".
- **Visual style for a projectile** — add an int to `Bolt.Style`, build the mesh in `Bolt._Ready`
  (needle = style 1, wood spike = style 2), and thread `style` through `SpawnBolt` →
  `BroadcastPBolt` → `ReceivePBolt` so clients see it too.
- **Ground field / lingering effect** — copy `Field.cs` (ticking AoE). Owner-authoritative radius via
  `S.SpellArea`; ghosts read the broadcast radius.
- **A modifier that adds an effect to *every* charged cast** — handle it in `ApplyChargedMods`
  (see Frost/Bramble/Sunder/Consecrate for chill/root/blast/ground templates).

> Multiplayer reminder: an effect that **deals damage** must run host-side or route via `Hurt`→
> `ReportHit`. An effect that is **purely visual** must be broadcast (`BroadcastVfx`/`BroadcastPBolt`)
> or allies won't see it.

---

## 7. Tuning reference

### 7.1 Damage numbers

- **Global player damage scalar:** `Base() = 10 * S.Atk * UltDmgMul * DamageMul` (`Player.cs`). Change
  the `10` to move *all* witches at once; change `S.Atk` growth (level cards) to move the run curve.
- **Per-witch balance:** `DamageMul` in `Game.ConfigureWitch` — currently Lunar **1.0**, Divine
  **0.815**, Crimson **1.15**, Verdant **0.9**. This is the cleanest per-witch power dial.
- **Per-ability coefficient:** the multiplier on `Base()` inside each ability. Examples to find by
  grep: `MinionDamage()` (`0.6`), `MinionBurst()` (`3.0`), `PoisonDps()` (`0.22`), Verdant primary
  (`* 0.5`), thorn (`0.6 + c*1.6`), Crimson lash/tide, Holy ray, finisher `pow`, etc.
- **Ult damage:** doubled globally via `UltDmgMul = 2f` while active; per-ult coefficients live in
  `ActivateUlt`. Ult duration grows with tier: `6 + UltTier*1.6` seconds.
- **Crit:** `S.CritChance` / `S.CritDamage`; `CritMult()` soft-caps crit damage past +150%. Crits
  apply to **direct** hits only (projectiles, holy ray, blood lash/tide initial) — **not** DoTs/fields.
- **Combo:** `ComboMul()` scales combo casts; tune `S.ComboPow` (per-stack power) and `S.ComboCap`.

### 7.2 Health

- **Player HP:** `Stats.MaxHp` (base 100) + level-up cards. **Damage taken** is reduced by
  `S.DmgResist` (per-witch base in `ConfigureWitch`: Lunar 0.22, Divine 0.15, Crimson 0.08,
  Verdant 0.18) and absorbed by shield (`ShieldPct`/`ShieldDelay`/`ShieldRegen`).
- **Enemy HP:** the `MaxHp = N * hs` numbers in `Enemy.Configure` (or `* bhs` for boss-tier). The base
  `N` is the wave-1 HP; the multiplier handles scaling (next section). Bosses: miniboss `680*bhs`,
  boss `4200*bhs`.

### 7.3 Difficulty scaling

All in `Enemy.Configure` and the `Game` wave block:

- **Enemy HP curve** (`Enemy.cs:156-157`):
  ```
  sw  = min(wave, 30)
  hs  = 1.075^sw * (1 + max(0, wave-30)*0.10)   // trash: ~+7.5%/wave compounding, linear tail past 30
  bhs = 1.10^sw  * (1 + max(0, wave-30)*0.12)   // bosses scale harder
  ```
  Raise the `1.075`/`1.10` bases for a steeper curve; change the cap (`30`) or tail slopes for the
  late game.
- **Enemy damage curve** (`Enemy.cs:158,187`): `ds = 1 + min(wave,30)*0.035`, applied to `Dmg` and
  `_boltDmg`. This is host-authoritative so it's MP-consistent.
- **Spawn volume & composition** (`Game.cs:~828`): the per-type `add(type, formula)` lines. Each
  formula is roughly `floor((wave - threshold) * rate)`, many with `Min(...)` caps. Earlier
  thresholds / higher rates = harder, denser waves.
- **Co-op scaling:** `cm = 1 + 0.55*(WardenCount-1)` multiplies every spawn count (2p ≈ 1.55×,
  4p ≈ 2.65×).
- **Elites & affixes** (`Game.cs:~896,901`): `eliteChance = 0.08 + Wave*0.004`;
  `affChance = min(0.35, 0.08 + Wave*0.012)`. Affix HP/score bumps live in `MakeAffix`.
- **Bosses/events:** boss every 10th wave, miniboss every 5th, roulette every 10th (capped), loot
  goblin ~14%/wave, rituals front-loaded then tapering (see the ritual block).
- **Level pacing:** `XpNext = 28 + (Level-1)*22` (`Player.cs:1905`). Lower it for faster power spikes.

### 7.4 Other systems worth tuning

- **Cast feel:** `S.FireCd` (primary rate), `S.ChargeSpeed` / `S.MaxCharge` (charged casts).
- **Mana economy:** `S.ManaMax`, `S.ManaGain` (per normal hit); charged release costs **0.5 mana**
  (refunds +1 on hit). Crimson uses HP instead: finisher cost = `S.MaxHp * FinHpCost` (base 0.18).
- **Mobility:** `S.Speed`, `S.DashDist`/`S.DashCd`/`S.DashCharges`, `S.JumpMul`; Verdant float/glide is
  in `Player` movement.
- **Ult charge:** how fast `UltCharge` fills (on-hit in `OnHitCore`); `UltTier` (0–4) bought with boss
  tokens at cost `UltTier+1`.
- <a name="dots-poison-bleed"></a>**DoTs (poison/bleed):** tick rate and per-tick damage are in
  `Enemy._Process` (poison: 0.4 s ticks, additive `_poiDps` capped at 60; slow refreshed while
  ticking). DoT ticks are host-authoritative; the value applied per Verdant hit is `PoisonDps()`.
- **Grove (Verdant):** `GroveEvery` (combo per ent, 14), `MaxEnts` (`3 + min(2,(Level-1)/12)`), minion
  attack cadence (0.8 s) and entangle (`Root(0.5)`) in `Thornling`.
- **Crimson:** `MaxBloodStacks`, lifesteal aura, `FinHpCost`.
- **Loot/economy cadence:** roulette/mystic/scroll spawn chances in the wave block.

---

## 8. Gotchas

- **Editing a method's last line with `str_replace`** can drop the closing brace — always include the
  trailing `}` in the replacement. Watch for accidental **duplicate method definitions**.
- **New enemy not on clients?** You forgot the `EnemyKinds.Types`/`Col` entry (`RemoteEnemy.cs`).
- **New VFX/projectile invisible to allies?** You didn't broadcast it (`BroadcastVfx`/`BroadcastPBolt`),
  or you added a field to the bolt without threading it through the PBolt RPC.
- **DoT does nothing in co-op for clients?** DoT ticks are host-side; a client applying a DoT to a
  proxy only tints/targets locally. Route real damage through `Hurt`→`ReportHit`.
- **Crit on a DoT?** Intentionally excluded; crit is direct-hit only.
- **Ult feels too strong/weak across the board?** It's the global `UltDmgMul = 2f`, not the per-ult
  coefficient.

---

*Generated as an architecture reference for the current build. When in doubt, grep the symbol names
in this guide — they're the real method/field names in the source.*
