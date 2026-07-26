# Wardens of the Moonlit Grove — Dev Handoff

Co-op FPS roguelite spellcaster. **Godot 4.7 / .NET (C#)**. ~95 flat `.cs` files at repo root.
Tested solo + 2 LAN PCs. This doc is the working-context handoff for a fresh Claude session.

---

## Build / verify

- Build with `dotnet build` (or Godot's build/hammer button). **Both LAN machines must rebuild** whenever
  an `[Rpc]`, a `BroadcastVfx` kind, or anything checksum-affecting changes.
- If editing without a compiler handy, sanity checks that catch most breakage:
  - brace balance per file
  - orphan-body scan: `awk 'prev ~ /^    \}$/ && $0 ~ /^    \{$/ {print NR} {prev=$0}' File.cs`

## Recurring gotchas (learned the hard way)

- `Net.ReceiveVfx` takes an `int col` param that is unpacked into a local `Color c` near the top.
  **Pass `c`, never the raw `col`, to spawner methods.** (Caused several `int`→`Godot.Color` compile errors.)
- Don't `sed` C# with a `|` delimiter — it breaks on `||`. Edit directly.
- Input actions (`pick1`..`pick7`, movement, etc.) are registered **in code** in `Game.cs` (~line 1069,
  `Action("name", new InputEventKey{...})`), NOT in `project.godot`.
- **Fan-out damage can combinatorially explode.** `Enemy.Hurt`'s curse-group SHARE re-fires per direct hit, and Shatter
  Cascade recurses (`ShatterInstant`→`ShatterFreeze`), each snapshotting the enemy list (`.ToArray()`). A big shatter into
  a large curse group (Curse-witch client) = O(hits×groupSize) `Hurt`+alloc → **froze the game in MP**. Both are now
  per-frame **budget-capped** (`_shareBudget` 1500, `_cascBudget` 24, self-reset via `Engine.GetProcessFrames()`), and
  `GD.PushWarning` fires once/frame when a cap trips. Any NEW AoE/on-death fan-out (e.g. Die()'s bleed-rupture) that can
  chain should be budgeted the same way. Note deaths mutate `Game.I.Enemies` synchronously (`RemoveEnemy` + splitter spawn),
  so iterate a `.ToArray()` snapshot, never the live list.

---

## Core architecture

- **DamageType enum:** Lunar0 Arcane1 Nature2 Frost3 Curse4 Holy5 Ember6 Physical7 Blood8 Wind9.
- **7 witches** by `Player.WitchIndex`: 0 Lunar, 1 Divine, 2 Crimson, 3 Verdant, 4 Gale, 5 Frost, 6 Forsaken.
  Identity = bool flags on `Player` (`FrostWitch`, `ForsakenWitch`, …; Lunar = no flags).
  `Game.ConfigureWitch(i)` sets the flag + `PrimaryType`/`SecondaryType`/`DamageMul`/`S.DmgResist`/`S.Speed`.
- **Damage spine:** `Base() = 10 * S.Atk * UltDmgMul * DamageMul * FrenzyMul() * JetstreamMul()`.
  Abilities multiply `Base()` by a coefficient and `ComboMul()`.
- **Cast flow:** `Combat(dt)` → `FireBolt`/`FireHolyRay`/`FireCrimsonTide`/`FireIcicleSpear`/`FireVoodooCrush`/
  beam updaters → `SpawnBolt` → `Bolt` hit → `Enemy.Hurt` → `OnHit`/`OnHitCore` (combo, mana, lifesteal, ult charge).
- **Finishers:** `FinType` enum + `FinMeta` (Finisher.cs). `Execute(f)` switch dispatch, keys 1–5, cost 1 mana
  (Crimson pays HP). `OnHitDirectNormal` = normal hit; `OnHitDirect` = charged. `ComboFromSource`/`ComboFromDot`
  build combo but NOT finisher charge.
- **Modifiers** (full-charge AoE): `ModType` enum, `ApplyChargedMods(pos)`.
- **Minors** (passive auto-finishers): `MinorType`, `FireMinor`.

## Multiplayer model

- **HOST-OWNS-WORLD, CLIENT-OWNS-AVATAR.** Enemies live on the host. A client's hit/status change routes to
  the host: `Net.ReportHit(netId, dmg, type, crit)`, `Net.ReportStatus(netId, kind, a, b, c)`, `Net.ReportCurse(...)`.
  (`ReportHit` now carries **crit** so armor-bypass — the Sentinel core / boss head — and the crit plink resolve on the host.)
- **Sentinel weakpoint:** `_type=="sentinel"` builds a bright pulsing caged **core** on the front chest (child of `_creature`,
  rides its facing). `IsCritZone` returns true within `Radius*0.9` of the core's world pos → a bolt there AUTO-CRITS, bypassing
  the 0.55 `_armorDR`. Projectile-only (checked in `Bolt`), like the boss head. Lever: the `0.9` core radius.
- Enemy status → clients via `Enemy.StatusMask()` (packed int) → `Enemy.SetRemoteStatus(mask)`.
  Bit map: 1 bleed, 2 slow, 4 root, 8 mark, 16 charge, 32 rot, 64 shield, 7–8 idle pose, 9 scream, 10 frozen,
  11–14 blueFrac, 15–20 freezeStacks, **21 cursed, 22–27 curseStacks, 28–30 curseGroup(low 3 bits)**.
- VFX: `Net.BroadcastVfx(kind, o, dir, a, b, col)` → `ReceiveVfx` (unpacks `col`→`c`; **use `c`**).
- Sounds: procedurally synthesized in `Sfx.cs` — `BuildX()` returns an `AudioStreamWav`; networked ones use a
  `Snd` enum + `BroadcastSfx`; one-shots can piggyback on a VFX kind's `ReceiveVfx` handler instead.

---

## Frost Witch (5) — COMPLETE

Freezing-beam primary, charged icicle-spear secondary, freeze→shatter passive, 3 ults
(Blizzard/FrostElemental/DeepFreeze) each with a legendary ult-mod (Whiteout / Avalanche / Absolute Zero),
affinity cards + 3 build legendaries (Shatter Cascade / Deep Winter / Glacial Impaler), 3 spell-combo finishers
(Ice Spikes / Frost Vault / Glacial Vise), full MP freeze sync.

Key rules: **shatter only triggers from a full-charge spear detonation** (or Glacial Impaler at any charge, or
Shatter Cascade / Absolute Zero chains). Damaging a frozen foe banks into the blue bar but does NOT shatter;
freeze-expiry just melts (applies banked damage, no explosion). Shatter seeds 1 flat freeze stack. Base
move speed `9.0 * 0.9`. Freeze threshold `(1 + MaxHp/120) * 1.25 * _freezeThreshMul`.

Shatter REDESIGN (recent): the **blue-bank mechanic is GONE** (it was lossy and unfun). A frozen foe now takes NORMAL damage
(`Enemy.Hurt` frozen branch removed) — freeze = a hard CC + a shatter target. A **charged-RMB spear on a frozen foe SHATTERS
it immediately** for `Player.ShatterBurstDmg()` (=`Base·7.0·Combo`, player-scaled — tuned so her single-target snipe edges
out the Forsaken crush, ~73 vs 68 at full HP) **+ `MaxHp·(0.05 + 0.15·missing)`** execute; AoE `shard = burst·0.3` (kept
modest so Forsaken owns AoE). Melt (freeze expiry) = 0 damage, just a crack VFX. `_frozenBlue*` fields + the HUD blue bar
removed (dead `FrozenBlueFrac`/StatusMask bits 11–14 left as 0). Levers: the `7.0` burst, the `0.05/0.15` execute.

## Graphics — procedural terrain (NEW)
The floor was flat toon paint over rolling-hill geometry. `World.TerrainMat()` is now a procedural spatial shader
(`TerrainCode`): world-space fbm noise → patchy earth, flat lit ground grasses green, dips go dirt-brown, steep faces rocky,
+ fine speckle. Seamless across chunks (WORLD pos). Per-chunk biome tint fed via `SetInstanceShaderParameter("base_color")`.
Palette is witchy/moonlit (cool enchanted moss on flats, violet-blue moonlight in dips, teal shimmer — not realistic dirt).
**Props too:** `World.Matte()` now routes to `World.PropMat()` — a cel-shaded (`diffuse_toon`/`specular_toon` + ink-outline
next_pass) SHADER with object-space fbm grain + rim glow, cached per colour. So trees/rocks/structures get surface dimension
instead of one flat tone. Characters/enemies (which use `Game.Toon` directly, not `Matte`) are untouched — stays clean.
**Floor decals/effects unaffected** (Field/HolyScorch are `Decal` nodes; impact marks/sigils/craters are separate quads).
**Both shaders are runtime-untested** (shaders only validate in Godot) — if terrain or props render magenta, suspect an
instance-uniform default (terrain) or a render-mode/token in `TerrainCode`/`PropCode`. **Deep Winter cascade fix:** only a REAL freeze (beam/shatter) radiates — a foe frozen
by the ambient aura has `_radiatesCold=false` (AddFreeze `canRadiate:false`), so the chill spreads ONE ring instead of
freezing the whole map. Lever: the `FreezeThreshold·0.12` drip / `7f` range in `Enemy._Process`.

---

## Forsaken Witch (6, Curse) — STAGE 1 DONE

A curse controller. Selectable (7th char-select card, key `7`). `DamageMul 0.85`, `DmgResist 0.15`.
Tuning fields on `Player`: `MaxLinks 6`, `CurseRate 2.5`, `CurseShareFrac 0.5`, `CurseSpreadRange 18`,
`CurseBonusType Curse`, `CurseBonusMul 1.5`.

**Primary (hold LMB) — curse-suck beam.** Moira-style sticky lock-on: `CurseLockValid` retains the target in a
~39° cone; `CurseAimTarget` acquires nearest-to-reticle (range 26u acquire / 30u hold, ×`SpellRange`).
Builds `CurseStacks` at `CurseRate/s`, low DoT (`Base*0.95`). At **2+ stacks** it anchors a **group**
(size = `floor(stacks)`, capped by `MaxLinks`); every 0.5s spreads curse to a foe within `CurseSpreadRange`,
tethering it in. `RefreshGroup` keeps the whole group's `CurseT` alive while beaming any member
(duration = stack count in seconds). 3-layer purple beam + omni light (`EnsureCurseBeam`/`PlaceCurseBeam`).

**Curse mechanics (Enemy.cs).** Fields `CurseStacks / CurseT / CurseGroup / _remoteCursed`;
`Cursed => Remote ? _remoteCursed : CurseT > 0`. In `Hurt`: cursed foes take `CurseBonusMul` extra from
`CurseBonusType`; damage to a grouped foe shares `CurseShareFrac` to group-mates (guarded by `_curseShareGuard`).
Countdown in `_Process` clears group+stacks on expiry. Visuals: purple glow, spinning curse ring,
overhead `☠N` Label3D (FontSize 40, PixelSize 0.006). `AddCurse(amt, group, bonusType, bonusMul, shareFrac)`
(dur derived from stacks; Remote→`ReportCurse`). `ConsumeCurse(frac, perStack)` (crush; Remote→`ReportStatus` kind 7).

**Secondary (charged RMB) — voodoo crush.** Hitscan, no projectile. Right hand **always holds a voodoo doll**
(burlap body/head/limbs, glowing curse pin, stitched-X eyes; chest `_voodooLight` brightens over cursed foes;
clenches with charge; pops on release scaling with charge). `FireVoodooCrush(charge)` consumes stacks by charge
(tap = 1, full = all), `perStack = Base*1.4` (~90 at 5 stacks after amp; propagates to the group via shared
damage before untethering). Uncursed target → base `Base*1.4` hit. Crush sound = `BuildCurseCrush`
(crunch + wet squelch + low thud + dark downsweep) — NOT the old scratchy `ModCurse` whoosh. Generic charge orb
+ generic charge hand-pose are suppressed for her (`AnimateHands` has a Forsaken branch).

**MP done.** cursed+stacks+group(3-bit) in `StatusMask`; tethers draw on ALL machines via `Game.SpawnCurseLink`
(read from synced group); beam (kind 57) + crush (kind 58, with sound) broadcast; curse/crush route to host.
Caveat: 3-bit group id can rarely visually-merge two groups' tethers on clients — cosmetic; host is exact.

### Forsaken — Ults (STAGE 2 DONE)
3 Curse ults in `UltChoiceSet` / `ActivateUlt` / `UpdateUlt`, each with a legendary mod (reset in `Game.ChooseUlt`):
- **Hex Circle** (`HexCircle`, `ModPlague`): ~10s ground field that follows her; every 0.25s curses all inside into ONE
  mega-group (`_hexGroup`) + builds stacks, so shared-damage cascades. Plaguebearer = bigger + curse DoT.
- **Life Drain** (`LifeDrain`, `ModRapture`): rises + **free flight** (`UpdateLifeDrain`, early-returns like Hurricane;
  Space/`jump` up, Ctrl/`descend` down, WASD drift, `ShowWings(true)`). Drains all in radius, heals half, banks it
  (`_drainBank`, capped); on end `EndLifeDrainBurst` detonates for the bank. **No DR/iframes while aloft** — her only
  defense is the drain healing (heal half of what she drains, so she's tanky only while foes are in range).
  Rapture = `StormForce` pull-in. Registers a new `descend` input action (Ctrl) in `Game.cs`.
- **Life Curse** (`LifeCurse`, `ModRite`): instant rune-nuke (`FireLifeCurse`). Per-foe dmg = `lerp(0.10,0.50,missing^1.3)`
  × enemy MaxHp; bosses/minibosses (`IsBoss`) capped at 0.22. Blood Rite = 5% lifesteal.
- MP: dmg/curse route to host as usual; ally VFX cues = kinds 59/60/61 in `Net.ReceiveVfx`.

### Forsaken — Affinity cards (DONE) + balance
6 growth cards + 2 legendaries in `Upgrade.cs` (gated `!pl.ForsakenWitch`): Wasting Curse (`CurseRate`), Deepening Hex
(`CurseStackCap`), Leeching Beam (`CurseBeamLifesteal`), Sympathetic Pain (`CurseShareFrac`), Virulent Hex (`CurseBonusMul`),
Binding Ritual (`MaxLinks`+`CurseSpreadRange`); legendaries **Soul Tether** (`MaxLinks=99`), **Withering Presence**
(passive: cursed foes near her rot — tick in `UpdateUlt`), and **Cursebrand** — an `AttuneSlot=2` card that opens the
element chooser (`DoElement` case) to set `Player.CurseBonusType2` (a 2nd `DamageType` that also gets `CurseBonusMul` vs
cursed foes; threaded through `AddCurse`/`ReportCurse`; Hurt checks both types). Slots 0/1 stay Mystic-vendor-only; slot 2
rolls as a normal legendary.
- **Stack-damage taper (NEW):** the crush no longer scales linearly with stacks — `ConsumeCurse` damage = `perStack ×
  stackCap·tanh(consumed/stackCap)`, i.e. tapers to a ceiling of `CurseStackCap` (base 5) effective stacks. Cap passed
  through `ReportStatus` kind 7 slot `c` for client casters. Deepening Hex raises the ceiling.
- **Primary lifesteal (NEW):** the suck-beam heals `CurseBeamLifesteal` (base 0.3) of its DoT back to her.

### Forsaken — TODO
- **Curse finishers** (spell combos).
- Playtest balance (levers: `CurseStackCap` 5, `CurseBeamLifesteal` 0.3; Life Drain now `7 + tier`s).
- Note: Life Curse / Drain-release "cross-arms + lift-knees" pose reuses the `crush` arm anim (no dedicated pose yet).

**Ground-effect terrain rule:** floor-laid effects must CONFORM to the bumpy terrain — use a `Decal` with `Game.FieldTex()`
(Size.Y = projection depth), NOT a flat `QuadMesh`/`TorusMesh` at one Y (those clip hills). Hex Circle's field now uses a
decal (`BuildHexField`); the transient burst sigils still use the game-standard flat `SpawnGroundSigil` flash.

### Beams — shared `SegBeam` renderer (NEW)
Curse beam, frost primary, and the arcane Spelllance (`FinType.Beam`) all render via one `SegBeam` helper (Player.cs):
N segments whose interior control points lag → the beam bows/whips (Moira-style) when either end moves. Curse beam
originates from the left hand. Universal hit tick / crit plink live in `Enemy.HitFeedback`; enemy hit test is now the
`Enemy.HitBy` capsule (spans feet→head) instead of a foot-level sphere.

---

## Named wave mutators — NEW
`WaveMutator` enum (None/BloodMoon/Eclipse/Surge/Moonfall/Volatile) + `Game.ActiveMutator`. Rolled in `NextWave`: host-only,
non-boss wave (`Wave%5!=0`), `Wave≥4`, `Heat > 1.22` (a hot streak), 35% chance → `RandiRange(1,5)`. Lasts the wave; resets
to None next `NextWave`. Effects: **Blood Moon** = enemy `Speed×1.3` (in `Enemy.Configure`, bosses excepted) + goblin chance
0.45 + red fog/moon; **Eclipse** = `FogDensity 0.05` + ambient×0.42 + dark fog (short sight); **Surge** = body-count `cm×1.7`
+ `Speed×1.18`; **Moonfall** = `MoonfallTick` rains `Moonshard` asteroids (telegraph → falling rock → direct-hit
`HurtPlayersIn` + a lingering molten crater; varied `size`; host-owned damage, VFX kind 62 → client ghosts); **Volatile** =
every non-boss/non-goblin foe `Explode()`s on death (players-only blast, reuses the volatile-affix path) + extra bombers. **Visuals live in `ApplyDayNight`** (keyed on `ActiveMutator`, so they compose with day/night and auto-restore).
**Reward:** clearing ANY mutator wave grants **every warden a pick-3 with a guaranteed legendary** — at the next `NextWave`,
`clearedMutator != None` → host `GrantMutatorRewardLocal()` + `NetMgr.BroadcastMutatorReward()` → each machine bumps
`_pendingLevels` + `_guaranteeLegCount`; `RollChoices` injects `RollOneLegendary` when the count is >0 and no legendary rolled.
**MP:** synced via `BroadcastWaveState`/`ApplyWaveState` (added a mutator int); enemy speed/density/loot are host-side, clients
just mirror the flag → same env visuals + `MutatorBanner`. Levers: the `0.35`/`1.22` trigger, `1.7`/`1.3` magnitudes.
(Room to add a Curse-themed 4th mutator for the Forsaken later.)

## The Peddler (shop vendor) — NEW
`ShopVendor.cs` (beacon like ScrollVendor, but **not consumed on open** — lingers ~2 waves so both players shop it).
- **Spawn:** `Game.ShopSpawnCheck()` (called from `NextWave`, host-only). Wave ≥ 4; guaranteed within every 10-wave
  window else ~18%/wave. Banners on spawn, at age 1 ("packing up — last wave"), despawns at age 2. Synced to clients via
  `VendorSnapshot` **kind 2** (Net.cs); `CurShop`/`RemoteShop`.
- **State `GameState.Shop`** (world keeps running — it's a local overlay). Offers are **instanced per machine**
  (`BuildShopOffer` rolls from the LOCAL player's witch), so both players shop the same node independently.
- **3 sections × 4 cards** (`UpgradePool.RollShopBoons/Finishers/Modifiers`): §1 blessings + witch cards + ult-mod;
  §2/§3 finishers/modifiers = 2 witch-`PrimaryType` + 2 pool, **only unowned OR a strict rarity upgrade**
  (`Player.FinisherRank`/`ModifierRank`). Priced by `UpgradePool.RarityCost` (Legendary 500). Buy = spend `Gold`,
  slot marks SOLD; buying all → `_shopCleanouts++` → next roll uses `Luck × (1+cleanouts)` (per-run).
- **Purchase routing** (`ApplyShopCard`): boons Apply directly; finisher/mod upgrade-in-place or equip if slot free,
  else the Swap screen with `_returnToShop` (also used by the Cursebrand element chooser) to come back to the shop.
- UI: `Hud.DrawShop` + `RShop`/`ShopAt`. Buying an unowned finisher/mod with full slots opens the **Swap** screen
  (`_returnToShop`); cancelling it ("Keep current") now **refunds the gold + restocks the slot** (`_shopBuyIdx`/`Price`).
- **Decline-for-gold:** the pick-3 (`GameState.LevelUp`) has a `DECLINE` button (Hud `DeclineBtnRect`, key `0`, `btn==4`)
  → `DeclineChoice()` grants `DeclineGold` (~40% of the best-rarity card's `RarityCost`, Common 24 → Legendary 200) and
  advances via `FinishStep`. Works for every pick-3 (level-up, loot, ritual, mutator reward).
- **Ult-mod roll fix:** removed the duplicate uncooldowned injector in `UpgradePool.RollChoices`; only the grace-gated
  one in `Game.RollChoices` remains (that's why legendary ult-mods used to flood right after level 10).

**Re-attunement REMOVED (functionally):** the Primary/Secondary Attunement cards (AttuneSlot 0/1) are deleted from the
pool and the **Mystic vendor no longer spawns** (`SpawnMystic()` call removed from `VendorSpawnChecks`) — retyping a
witch's attacks muddied her identity. The Mystic's node/UI/MP code (`Mystic.cs`, `DrawMystic`, `OpenMystic`/`MysticBuy`,
`GameState.Mystic`, `VendorSnapshot` kind 0, minimap ref) is now DEAD but intact — a full code delete is a ~40-ref
surgery, deferred. **Keep** `AttuneCard`, `DoElement`'s slot logic, and `GameState.Element` — **Cursebrand** (AttuneSlot 2)
still uses them.

## XP orbs — overhaul (NEW)
`Stats.PickupRange` (base **1.8u**, was a 10u auto-vacuum). Orbs now **persist** (no `Life` despawn) as small specks — they
only home+collect within `Game.PickupRange` (host's stat) unless `Game.MagnetActive`. **Minimap:** `Hud.DrawMinimap` plots
`g.Orbs` (host) + `NetMgr.RemoteOrbPositions()` (clients). **Cap:** `Game.AddXpOrb` soft-caps at 150 (frees oldest) so the
persistent hoard + its MP pickup-snapshot stay bounded — orb spawns go through it, not `Orbs.Add` directly. **Cards:**
"Lodestone Heart" (AllR) `+0.9·m·PickupRange`. **Chest magnet:** `OpenChestReward` ~8% band → `ActivateMagnet(4.5s)` →
every orb vacuums to the party. **MP caveat:** orb collection uses the HOST's `PickupRange` (clients' own pickup-range cards
don't apply to them; the magnet + host-range walk-over still work) — full fix would sync each player's range.

## Menus & MP flow — rebuilt (NEW)
Two Control-node screens now replace the old immediate-mode ones (Pause options are unchanged):
- **`Lobby.cs`** — home screen: Play Solo / Play Multiplayer (Host / Join-by-IP) / Options / Quit. **Options** is a
  `TabContainer`: **Graphics** (quality preset, bloom/SSAO/SSIL, damage numbers), **Sound** (music vol, look sens),
  **Screen** (window mode, resolution `Game.ResChoices`, V-Sync → `Game.ApplyWindow`). All wired to existing setters + persisted via `SaveGold`.
- **`CharSelect.cs`** — scrollable witch roster (left) + detail card (right: element badge, role, flavor, passives, Power/
  Resilience/Mobility bars). Witch data table is inline (indices match `ConfigureWitch`). Confirm → `Game.ConfirmWitch`.
  Shown/hidden by `Game` on `State==CharSelect` (the old `Hud.DrawCharSelect`/`RWitch` + `Game._Input` cases are now dead).
  Both are laid out with **auto-sizing containers** (`CenterContainer` for the home panels; `MarginContainer`→`VBox`→`HBox`
  with expand flags for CharSelect) — NO hardcoded pixel positions — so they fit any window size (the project sets no stretch
  mode, so Controls anchor to raw window pixels). `Game.ApplyWindow` clamps windowed size to `ScreenGetUsableRect` (never
  bigger than the screen) and is only applied on load if a save exists (first launch keeps the project-default window).

**MP ready gate:** `Game.ConfirmWitch` → solo starts immediately; MP calls `Net.ReportReady()`. Host tracks `_ready` set,
broadcasts the tally (`ReceiveReadyCount` → `Game.ReadyCount`, shown as "X/Y ready"); when `_ready.Count >= PlayerCount()`
it fires `ReceiveBeginRun` → all peers `Game.BeginRunFromSelect` → `StartGame`. Disconnect during select re-checks the gate.

**MP game-over:** host sees 3 buttons (Retry / Character Select / End) in `Hud` Over branch; clients see "waiting for host".
`Net.BroadcastGameOverChoice(0|1|2)` → `Game.ApplyGameOverChoice`: **2=End** disconnects + reloads scene → home; **0/1** call
**`Game.SoftResetRun()`** — clears Enemies/Orbs/Rituals/Chests + resets wave/heat + each warden's stats/loadout/level/ult/
downed IN PLACE (no scene reload, so the Net session survives), then char-select or retry. `StartGame` now also refills
vitals so a retry works. **Solo game-over unchanged** (scene reload). ⚠ Soft-reset + all these screens are **runtime-untested**
(compiles clean); lingering transient VFX/projectiles aren't force-cleared (they self-expire). Ready-gate caveat: a host who
confirms while alone (client mid-connect) starts immediately — "all others" = currently-connected peers.

## Witch legendaries — now ≥3 each (NEW)
Older witches were topped up to 3 legendary affinity cards (in `Upgrade.cs`, `LegP` + `!pl.XWitch → Hidden` pattern, gated by a
new bool flag on `Player`): **Lunar** = Lunar Eclipse + *Lunar Resonance* (combo cap+15/pow) + *Gravity Well* (`GravityWell`:
on-kill StormForce mode-0 pull, throttled by `_killProcCd`). **Divine** = Martyr's Grace + *Radiant Ascension* (`RadiantMote`)
+ *Guardian's Aegis* (Interventions+2, DR). **Crimson** = Hemoclast + *Crimson Frenzy* (stat) + *Bloodbath* (`Bloodbath`:
on-kill heal + StormForce mode-2 burst). **Verdant** = Wildfire Bloom + *Ancient Grove* (ents) + *Verdant Vitality* (HP/DR).
On-kill procs (`GravityWell`/`Bloodbath`) fire in `Player.OnHitCore`, throttled 0.25s.

**Radiant Ascension (Divine):** while `Airborne`, the Holy primary can lock onto allies (`AimAllyPos`) and the mote is flagged
`Bolt.RadiantHeal`/`HealAmt` (=`0.4 + 0.2·Combo`). In `Bolt._Process` it calls `NetMgr.HealAlliesNear` (now returns bool)
once per pass, then flies through the ally (allies aren't in `Enemies`, so pierce isn't spent) to strike the foe behind. Airborne-only.

**Taker** now drops its captive (`ReleaseGrab`) when flung (`Enemy.Fling`) or frozen (`Enemy.Freeze` — the game's hard stun);
slow/root don't. **Chest healing font** now sets `HealAllies=true` (clients only got a visual-only field copy). **Updraft**
launch `_vy 15→23`.

## Ally/minion x-ray + end-of-run scoreboard (NEW)
**Silhouette rework:** the fat capsule x-ray was replaced by a model-SHAPED ghost. `Game.SilhouetteMat(col)` + `Game.
AddModelSilhouette(model, mat)` clone each of a model's `MeshInstance3D`s as a translucent, `NoDepthTest`, RenderPriority-8
overlay parented to the real mesh (so it rides the animation). `AddFriendlySilhouette` now calls those (ents/Guardian keep
working). `RemoteAvatar` builds its ghost via `AddModelSilhouette(_model, _silMat)` and re-adds it in `BuildModel` on witch
swap; `_silMat` recolor still works. ⚠ per-mesh → more draw calls than the capsule (bounded: allies + ents).

**Scoreboard:** `RunStats.cs` (per-warden tally). `Game.MyStats` tracked locally: DamageDealt/BossDamage/Kills (+Lunar
night-kill & Crimson leech highlights) in `Player.OnHitCore`; Healing in `Player.Heal` + `Net.HealAlliesNear` (+Divine
highlight); DamageTaken in `Player.Hurt`; Flings via `Game.CountFlungNear` in the fling finishers; Gale-aloft in the player
update; Verdant ents in `Thornling.Detonate`; Frost shatters in `Bolt`; Forsaken curses in `UpdateCurseBeam`. At `GameOver`
each peer `Net.BroadcastRunStats` → `Game.AllStats[peer]` (personal stats). Over screen (`Hud`) draws one row per warden.

**KILLS are host-authoritative & exact** (damage was already per-player-exact — each tracks only its own OnHitCore). `Enemy.
Hurt` stamps `_lastAttackerPeer = Game.AttackerPeer`; `Enemy.Die` → `Game.CreditKill(peer, IsNight)` → `Game.KillTally` /
`NightKillTally`. `AttackerPeer` defaults to `LocalPeer` (host's own hits) and `Net.ReceiveHit` sets it to the reporting
client around the routed `Hurt` (so ALL client damage — direct hits, beams, curse-share — credits the client). Host
`BroadcastKillTally` → all; the Over screen reads `KillTally[peer]` for kills and `NightKillTally[peer]` for the Lunar
highlight. **Edge:** persistent DoTs (poison/bleed) applied by a client but *ticked* host-side credit the host — direct
hits, beams, shatters, crushes, curse-share are exact. Added stats: `TimesDowned` (`GoDown`), `Revives` (ally-revive act),
`BestCombo` (`Player.BestCombo`), `BiggestHit` (`OnHitCore` max). All reset in `StartGame`.

## Curse spell-combo finishers (NEW) — universal, curse-flavored
3 new `FinType`s (Curse `DType`), witch-agnostic like all finishers. Wired: `Finisher.cs` enum+Name/Desc/DType, `Player.
Execute` cases, `Fin*` methods (Player.cs), pool `Def(...FinCard...)` in `Upgrade.cs`, VFX kinds 63/64/65 in `Net.ReceiveVfx`.
- **Soul Reap** (`ComUncRare`): reaping curse-nova, `dmg × (1 + 1.6·missingHP)` execute + 5% soul-harvest heal (cap 18% MaxHp).
  VFX: `Game.SpawnScytheVfx` (crescent arc) + soul wisps + ring; SFX `CurseCrush`. Kind 63.
- **Hex Chains** (`UncRareEpic`): binds nearest `4+t` foes into a temp shared-pain group (`AddCurse` w/ new `_curseGroupSeq`,
  shareFrac 0.4) + hex burst. VFX: cursed chain cylinders + sigil + ring; SFX `WitchCackle` (self-networks). Kind 64.
- **Doom Sigil** (`EpicP`): brands foes (curse), spawns `DoomSigil.cs` node — a pentacle that fuses ~1.35s then detonates for
  `Base·2.4·pow × (1 + 0.12·(branded-1))` Curse damage + rising doom pillars. Remote ghost via kind 65 (visual). SFX `WitchCackle`
  cast / `CurseCrush` detonation (ghost self-plays). **MP:** damage via `e.Hurt`/`AddCurse` (route to host); VFX broadcast; the
  DoomSigil node is caster-owned (real) vs `InitRemote` (ghost, `_dmg=0`). All damage host-authoritative → attribution stays exact.

## Coven Perks — persistent meta-progression (NEW)
`PerkTree.cs` (`Perks` static): each witch has a **9-node tree** — 3 lanes (Left playstyle / Middle shared / Right playstyle)
× 3 tiers (2 minors → 1 major). Fixed support graph (`SUP`): sides are single chains, middle nodes bridge both sides with
2 supports (`M1←{L1,R1}`, `M2←{L2,R2}`, `M3←{M1,M2}`). **Buy** with gold (cost 150/400/850 by tier, permanent), **Equip** up
to `Cap=6` with `MaxMajors=2`. Equip needs ≥1 support EQUIPPED; `Unequip` cascades (drops any node that loses all supports).
Effects are `Action<Player>` (stat/scalar deltas leaning into each witch's 2 playstyles) applied in `StartGame` via
`Perks.ApplyEquipped(Player, witch)` AFTER `ConfigureWitch`, before vitals. State (owned+equipped per witch) persists in
`[perks]` of `grove_save.cfg` (`Perks.Save/Load`, hooked into `Game.SaveGold/LoadGold`; `Game.SavePerks()` = SaveGold).
UI: `PerkScreen.cs` (custom `_Draw` tree + connecting lines, witch tabs, left-click buy→equip / right-click unequip-cascade),
opened from the home menu's "Coven Perks" button (`Game.OpenPerks/ClosePerks`). Runtime-untested (Control `_Draw`/`_GuiInput`).

## Ember Witch (index 7, NEW) — flamethrower + meteor + Living Bomb
Registered everywhere: `Player.EmberWitch`/`WitchIndex`/`WitchDamage`, `Game.ConfigureWitch` case 7 (+ both flag-reset
chains), `WitchModel.WitchColor`/hat case 7, `CharSelect` roster, `RunStats` labels, `Perks.WitchCount=8` + `_trees[7]` +
`LaneNames` + `PerkScreen` (uses `Perks.WitchCount`), `DevConsole`. **Ult is still the Lunar default** (`UltChoiceSet` — TODO).
- **Burn + Living Bomb (Enemy.cs):** `AddBurn(amt, perStack, bombFlat, dur)` (routes for clients via `ReportStatus` kind 6 /
  `ReceiveStatus` case 6). Burn is a stacking Ember DoT (ticks `stacks·perStack`). `LivingBombThreshold` is HP-scaled like
  freeze; each crossing → `_livingBombStacks++`, burn drops by the threshold, `TriggerLivingBomb()` (flat base-scaled blast on
  THAT foe). On `Die()`: erupts an area for `MaxHp·0.16·stacks` to nearby foes (chains). Synced to clients via StatusMask
  **bits 11-14** (repurposed the dead blue-bar bits) → `LivingBombStacks`; HUD shows "LIVING BOMB xN" to all players (`Hud` enemy bars).
- **Primary flamethrower:** `Combat` EmberWitch branch → `UpdateFlameCone` → `FlameConeTick` (cone dmg + `AddBurn`, combo/finisher
  once per tick). Tick rate = `S.FireCd·0.6` (cast-speed cards speed it up); reach = `9·SpellArea`. VFX `Game.SpawnFlameCone` (kind 66).
- **Secondary meteor:** `Combat` EmberWitch charge branch → `UpdateEmberCharge` (ground aim ring, mirrors Gale) → `FireMeteor`
  → `EmberMeteor.cs` node (falling rock, AoE dmg + `burnStacks` to foes, Remote ghost via kind 67). All damage host-authoritative.

## Ember Ultimates (NEW) — all MP-synced, host-authoritative damage
`UltKind.MeteorDescent / WildfireRush / PhoenixAscend`; choice branch in `Game.UltChoiceSet`; names/descs in `Hud`.
Activation cases in `Player.ActivateUlt`; flight/aim hijacks dispatched at `Player.cs` movement tick (before `Combat(dt)` — so
flight ults fire their own weapons); countdowns in `UpdateUlt`. VFX tells = BroadcastVfx kinds **68/69/70** (`ReceiveVfx`).
- **Meteor Descent:** `_meteorAscend` → `UpdateMeteorAscend` (rise to baseY+18, `_iframe=999` invuln, `ShowEmberAimRing` reticle
  scaled to `MeteorUltRadius`=`(10+tier·1.5)·SpellArea`, 5s or [Q]/[LMB] confirm) → `MeteorLand`: tapering AoE
  (`Lerp(centerDmg, edgeDmg, (d/r)²)`), `AddBurn(threshold)` brands 1 Living Bomb on all, drops a 6s Ember `GroundField`
  (new `BurnAdd/BurnPer/BurnBomb/BurnOwner` fields stack burn every 0.6s).
- **Wildfire Rush:** `_flameDashCharges`(3-5) + 10s window; [Q] during the window → `TryUlt`→`FlameDash` (motion via `_flameDashT`
  in the movement block) laying an `EmberTrail.cs` (rectangular ~8×13·SpellArea, 10s, stacks burn, buffs allies). Burn-tick
  **lifesteal**: `Enemy._burnOwner` (set in `AddBurn`, or sender on the status relay) → burn tick calls `Game.AwardBurnLifesteal`
  → owner's `Player.TryBurnLifesteal` (heals 100% while `BurnLifestealT>0`). Ally speed+heal (never caster) via
  `Net.BuffAlliesInStrip` (strip geometry over `_remotes`) → `GrantWindBoon` + `Heal`. Trail ghost = `Net.BroadcastEmberTrail`.
- **Phoenix Ascendant:** `_phoenix`/`PhoenixActive` → `UpdatePhoenix` (free flight: WASD + Space/Ctrl, immolation aura ticks
  burn, fires the flamethrower itself since Combat is skipped; flame reach/dmg get a Phoenix bonus in `UpdateFlameCone`/
  `FlameConeTick`). One-shot cheat-death `PhoenixRebirth` hooked in the lethal check (`if PhoenixActive && _phoenixRebirth`).
  `BuildPhoenixAura` = wings+core VFX parented to her. `GoDown` cancels any Ember flight ult.

## Ember spell combos (finishers, NEW) — universal, all rarities
`FinType.FireWall / Fireball / EmberFervor` (Ember), pooled via `Def(AllR, …FinCard…)` in Upgrade.cs so any witch can equip
them at any rarity. Executed in `Player.ExecuteFinisher` → `FinFireWall/FinFireball/FinEmberFervor`.
- **Ring of Fire** (`FireWall.cs`): planted flame ring; burns foes in the band (owner-authoritative). Eats incoming enemy
  projectiles host-side — `Game.FireRings` list (registered by `RegisterFireRing`, a client routes via `Net.ReqFireRing`);
  `EnemyBolt` pops + puffs + crackles on entering a ring. Ghost via VFX kind 72.
- **Fireball** (`Fireball.cs`): med-speed projectile; direct enemy hit (heavy) + medium blast; ghost via kind 73 (visual-only
  explode). Both stack burn.
- **Ember Fervor** (self-buff): `Player.EmberFervorT/_emberFervorCrit/_emberFervorSpeed` fold into `RollCrit` + `MoveSpeedFactor`;
  can't recharge while active (guard in the finisher-charge loop in OnHitCore); HUD chip in `DrawBuffs`; fists/feet flames via
  `ShowFervorFlames` (view-model hands + body model, tracked in `_fervorFlames`); periodic kind-70 pulse so allies see it.

NOTE: `Hud.DrawFlameIcon` is now built from stacked `DrawCircle`s ONLY — `DrawColoredPolygon` triangulation is unreliable in
this Godot build (crashed even on 3-vertex triangles), so avoid it for HUD glyphs.

## Ember charged-cast modifiers (NEW) — universal, Uncommon→Legendary
`ModType.Meteor / Eruption` (Ember) in Modifier.cs; cases in `Player.ApplyChargedMods`; pooled via `Def(UncP, …ModCard…)`.
- **Meteor Strike:** full charge → `SpawnEmberMeteor` at the impact point (Ember witch → her meteor + this = two meteors).
- **Eruption:** molten `SpawnGroundSpikes` + `SpawnEmberBurst` flame ring + `NetMgr.StormForce(pos, r, 1, power)` (host-authoritative
  outward/up fling, mass-scaled → higher rarity flings small foes skyward). Damage/burn route to host; both are MP-visible.
- FIX: the Ember witch's `UpdateEmberCharge` full-charge release now calls `ApplyChargedMods` (it never did — she couldn't use ANY charged-mod before).

## Levels / Biomes + hard scaling + portal (NEW, big)
- **Hard ramp:** `Enemy.Configure` tail — `if (wave > 10)` compounds `MaxHp *= 1.062^(w-10)`, `Dmg *= 1.05^(w-10)`, `Speed +up to 50%`
  for ALL foes + bosses. Elite chance also ramps post-10 (`Game.SpawnEnemy`, cap 0.6). Carries across levels because `Wave` keeps climbing.
- **Biome/level:** `enum Biome { Grove, Rainforest }`; `Game.CurBiome` + `Game.LevelNum`. `World.BuildChunk` forks on
  `Game.I.CurBiome == Rainforest` → jungle ground palette + jungle scatter (`JungleGrove/RiverBank/PepperPatch/VineGrove`) +
  jungle props (`JungleTree/Monstera/Fern/VineTree`, `PepperBush.cs` (subclasses Pumpkin), `Firefly.cs` (clones Wisp)). Fog/env
  greened in `ApplyDayNight` (`CurBiome` branch).
- **Portal:** after each boss wave clears, `SpawnLevelPortal` (guard `_portalWave != Wave`). Interact (hold E in `UpdateInteract`)
  → `AdvanceLevel` → `ApplyLevelAdvance` (LevelNum++, biome, new seed, `_world.Reseed`, reposition to origin, keep party/upgrades/
  Wave/Heat, `ResetVendorCadence`). MP: `Net.BroadcastPortal`/`RequestAdvanceLevel`/`BroadcastLevelAdvance`.
- **Vine launch:** `Game.Vines` (managed with chunks like Blockers via `_chunkVines`); `VineTree` registers a launch point; hold-E
  in `UpdateInteract` → `Player.VineLaunch` (`_vy = 28`).
- **Jungle enemies (Enemy.cs + RemoteEnemy.Types 21-27):** jtroll (melee, staggers on hit), pigmy (fast fodder), pigmydart
  (ranged blowdart), ptero (EBehav.Zapper flying stun), bat (Diver), croc (new `EBehav.Lobber`→`MoveLobber`→`CrocBomb.cs` lobbed
  timed bomb, VFX kind 76), snake (`MaxHp` forced to 1, roots on touch). Taker still special-spawns. Wave roster forks on biome in `NextWave`.
- FOLLOW-UPS (not done): carved winding rivers (uses basin ponds + monstera banks), bespoke jungle ruins / tree-villages
  (reuses generic Fort/Ruins), a dedicated jungle boss/miniboss (reuses the Grove's, which now scale via the hard ramp).

## Arcane Witch (index 8, NEW) — burst-MARK → charged CHAIN-LIGHTNING through the marks — runtime-untested
**Fantasy:** other witches wield a *refined* form of magic; she channels the raw source (jagged plasma). Registered everywhere
(index-7 Ember checklist): `Player.ArcaneWitch`/`WitchIndex`(prepend `?8`)/`WitchDamage`(`8=>Arcane`), `Game.ConfigureWitch` case 8
(`DamageMul 0.95`, `DmgResist 0.14`) + both flag-reset chains, `WitchModel.WitchColor`/hat, `CharSelect` roster, `RunStats`
(`8=>"Foes Marked"`/`"Arcane"`), `PerkTree.WitchCount=9`+`_trees[8]`+`LaneNames`, `DevConsole` (`"arcane"`), `Hud.DrawEnemyBars` pip.
**Ults STUBBED to Lunar defaults** (falls through `UltChoiceSet`). `DamageType.Arcane` = enum 1, purple.
- **Primary — 3-round bolt burst (LEFT hand):** Combat `ArcaneWitch` branch (`_arcaneBurst`/`_arcaneBurstT`, 0.085s gap, `_fireCd=S.FireCd*1.7`).
  `FireArcaneMissile` fires tight w/ aim-assist to `AimTarget()`, `Bolt.ArcaneBurst=true`, restores mana on hit (`normal:true`→`OnHitCore`).
- **Mark:** `Player.OnHit`→`ArcaneStreakHit`; **3 consecutive same-foe hits** (within 1.2s) → `MarkArcane(e)`. Marks are a caster-side list
  `_arcaneMarks` — **persistent, cap `ArcaneMaxMarks=4`, FIFO (oldest evicted → `SetArcaneMark(false)`), cleared on death** (`PruneArcaneMarks`).
  `Enemy._arcaneMarked` bool (no timer); `ArcaneMarked` synced via **StatusMask bit 31** for the pip + client-caster chain targeting; client marks
  route on/off via `ReportStatus`/`ReceiveStatus` **kind 8** (`a=1 on / 0 off`)→`SetArcaneMark`.
- **Secondary — charged CHAIN-LIGHTNING (RIGHT hand):** charge-release → `FireArcaneChain(charge)`. Builds a greedy nearest-neighbor path
  (`OrderedMarkChain`) her→through each live mark; per leg, foes within `DistPointToSeg ≤ radius+0.8` are pierced (normal dmg, base `RollCrit`);
  each **marked endpoint** takes normal dmg at **2× crit chance** (two `RollCrit` rolls) + `OnHitDirect` (combo/ult/mana). **No marks → single
  hitscan at `AimTarget()`** (no bounce; wall-stop via `BeamSurfaceHit`). `dmg = Base·1.4·chargeMul·Combo` (`chargeMul` = charged-bolt ramp).
  **Mana −0.5/+1 on hit** (the `Combat` release sets `_chargedRefund`; `OnHitDirect` pays the +1 on the first connect; whiff clears the flag).
  **Charged-mods** (`ApplyChargedMods`) fire once on the FIRST foe hit, full-charge only. Jagged VFX `Game.SpawnArcaneLightning(pts,charge)` +
  per-leg `BroadcastVfx(78)` (`SpawnArcaneBeamSeg`) for allies.
- **Loop:** burst a foe → 3-on-one marks it (up to 4) → charged RMB zigzags lightning through all marks, critting them 2× as often.
- **MP:** damage host-authoritative via `Enemy.Hurt`→`ReportHit`; marks route via kind 8. **Runtime-untested** (compiles clean).
- **Tuning knobs:** chain `Base·1.4`, mark cap 4/streak 3, in-between hit radius `+0.8`, 2× crit = two rolls, burst dmg `Base·0.5`/gap/recovery.
  Note `Game.SpawnArcaneRupture` is now dead (kept, unused). Charged-mod full-charge gate is easy to loosen if she wants mods on any charge.

## Gamepad support (Xbox) — NEW, runtime-untested
Layered onto the SAME InputMap actions as keyboard/mouse, so **both input methods are always live**; a controller just works when plugged in (`Game.PadActive => Input.GetConnectedJoypads().Count>0`). Joypad events are added to the existing actions in `Game.SetupInput()` (movement=left stick, `cast`=LT, `charge`=RT, `jump`=A, `dash`+`descend`=B, `ult`=Y, `stats`=Back, `ultmenu`=L3, `release_mouse`=Start; trigger deadzone 0.5).
- **Right-stick look**: `Player.UpdatePadLook(dt)` (called before the flight/ult early-returns so it works aloft) — radial deadzone `PadLookDead`, squared response curve, exponential smoothing. Sensitivity `Player.PadSensMul` (static), slider "Gamepad Look" in Lobby Sound tab, persisted as `options/padsens`.
- **R3 = quick-turn 180°** (`_turn180`, snap-rotated in UpdatePadLook). **B while flying = descend** (dash is locked out mid-flight-ult, so no clash).
- **Spell slots = hold LB + face button**, handled in `Player._Input` (InputEventJoypadButton, chord = `IsJoyButtonPressed(LeftShoulder)` + button): LB+X/Y/B/A/RB → `FireFinisher(0..4)`. Because A/B/Y also drive jump/dash/ult, those consumers are guarded by `!Game.PadSpellHeld()` (jump/dash/float in `Player._Process`, ult in `Game._Process`, interact-X in `Game.UpdateInteract`). `PadSpellHeld()` is LB-held.
- **Menus**: `Game.UpdatePadCursor` drives a cursor **we own** (`_padCursor`, exposed as `Game.PadCursor`) — the left stick moves it (clamped), we `WarpMouse` the OS cursor to match, and it's drawn as a reticle by `PadCursor.cs` on a **CanvasLayer Layer 100** (above HUD layer 0 AND the lobby screens layer 50). We do NOT accumulate onto `GetMousePosition()` while steering: under **Parsec** the OS cursor is asserted remotely, so a read-back loop drifted off-screen. Idle → follow the real mouse (clamped) so a physical mouse still works. A = select / B = back via `Game.PadMenuButton`, which **mirrors the button's own down/up** into a left-click at `_padCursor` / an Escape (NOT a same-frame press+release — that misses polled `IsActionJustPressed` edges). Presses no-op during gameplay; releases always pass (so a click that enters gameplay still delivers its mouse-up → no stuck `cast`).
- **Debug**: `Game.PadDebug` (F3) draws a live readout in `Hud.DrawPadDebug` (connected joypads, stick/trigger values, pressed buttons). Default off.
- **NOT done / gaps**: no aim assist (deferred by request); Join-by-IP still needs a keyboard (no on-screen keypad); finisher rebinding (`BindKey`) is keyboard-only; no rumble; D-pad unbound. Controller detection + input **confirmed working through Parsec**; stick feel / chord timing still want a real-hardware pass.

## File map (the ones you'll touch most)

- `Player.cs` — the witch: movement, cast pipeline, every witch's primary/secondary/ults/finishers/mods/minors,
  combo + resource economies. Large; navigate by method name.
- `Enemy.cs` — enemy AI, movement (`MoveMelee`/`MoveRanged`/…), status, `Hurt`, `StatusMask`/`SetRemoteStatus`.
- `Game.cs` — `ConfigureWitch`, char-select, ults plumbing, VFX helpers (`SpawnCurseLink`, `SpawnFrostShatter`,
  `VfxRing`, `SpawnGroundSigil`…), input-action registration.
- `Net.cs` — RPCs, `ReceiveVfx`, `ReportStatus`/`ReportCurse`, `StormForce`.
- `Sfx.cs` — procedural audio (`BuildX` → `AudioStreamWav`).
- `Hud.cs` — HUD, char-select cards (`WitchCard`, `RWitch[]`), popups (`SafePoly`).
- `Finisher.cs` / `Upgrade.cs` — `FinType`/`FinMeta`; card definitions (`Def`, `Card`, `WitchCard`, `FinCard`).
- `Bolt.cs` — projectile visuals + hit registration (3D body-distance, not a plane).
