# Architecture overview

Navigational map only. Full recipes live in `DEV_GUIDE.md`; hard-won gotchas in `DEV_HANDOFF.md`.

## Purpose
Co-op FPS/3rd-person roguelite spellcaster. Godot 4.7 / .NET 8 (C#). LAN ENet, ≤4 players, port 7777.
`.cs` files are **flat at the repo root**; `Main.tscn` root `Node3D` "Game" runs `Game.cs`.

## Key components (symbols to grep)
- `Game.cs` — root orchestrator: world sim, waves/director, spawning, input-action registration
  (`Action("name", …)`), MP session, level-up/shop, map population.
- `Player.cs` — the player (a plain `Node3D`, not `CharacterBody3D`). FP camera `_cam`; 3rd-person
  `tp3` (`ToggleThirdPersonPlay`); FP viewmodel hands (`BuildArm`/`AnimateHands`); charge/cast.
- `Enemy.cs` / `Creature.cs` — enemies + authored-biped animation state machine (goblin/zombie/ogre/taker).
- `Net`/RPC layer — host-authoritative networking (see below).
- `VisualCore.cs` (`Vis.Painterly`) — painterly material system (matte, world-space value/hue drift,
  no ink outlines).
- `TreeField`/`PropField` — GPU-instanced (MultiMesh) scatter for trees/foliage/props.
- `PropGlb.cs` — normalizes Meshy prop/structure GLBs (unit-height, baked) + instanced tint shader.
- `FloatingAvatar.cs` — the 3rd-person floating witch avatar (+ shared FP glove hands).
- `dev/ai/` — the AI visual-test harness (dev-only; see `architecture/ai-test-harness.md`).

## Control / data flow
1. `Game.cs` boots the world, registers input, runs the difficulty director + credit spawn stream.
2. **Host** simulates all enemies/waves/bosses/loot; **clients** own only their own `Player` and
   route their damage to the host via RPC. Almost every MP bug traces to breaking this.
3. Damage: `Enemy.Hurt(...)` (with `direct` flag) → status/curse groups, fan-out, death → `RemoveEnemy`
   (mutates `Game.I.Enemies` synchronously — iterate a `.ToArray()` snapshot, never the live list).

## Extension points
- New enemy/witch/ability: follow the `DEV_GUIDE.md` recipes (there are step lists).
- New scatter/foliage: add to `TreeField`/`PropField`, never per-part nodes.
- New validation scenario: `dev/ai/AiTestRunner.cs` (`ScenarioWitch` map + a `case` in `Dispatch`).

## Invariants / hazards
- Host authority (above). Input actions in C#, not `project.godot`.
- **New AoE / on-death fan-out MUST be budget-capped** (`_shareBudget`, `_cascBudget` pattern in
  `Enemy.Hurt`) — an uncapped shatter×curse cascade froze MP. See `DEV_HANDOFF.md`.
- Don't `sed` C# with a `|` delimiter (breaks on `||`); edit directly.
- Scale authored GLBs by mesh AABB, not `FitHeight` (Armature-0.01 gotcha) — see `MODEL_*`.
- A clean compile is not visual validation.
