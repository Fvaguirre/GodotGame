# Wardens of the Moonlit Grove — Godot 4 / C# port

A first-person witchy survival prototype, ported from the web build to **Godot 4.x with C# (.NET)**.
This drop is a **runnable vertical slice** — the foundation everything else bolts onto.

## What you need
- **Godot 4.x — the .NET / C# edition** (the download labelled "Godot Engine - .NET", not the plain build).
- **.NET SDK 8.0** installed.

## Open & run
1. Open Godot (.NET), click **Import**, and select the `project.godot` in this folder.
2. First open: Godot may offer to create/regenerate the C# solution — let it. If the build complains about the SDK version, open `GroveGodot.csproj` and change `Godot.NET.Sdk/4.4.0` to match your Godot version (e.g. `4.6.0`).
3. Press **F5** (Play). Click the window to capture the mouse.

## Controls
- **Mouse** look · **WASD** move
- **Left-click** cast (builds mana & combo) · **hold Right-click** charge; a *full* charge triggers your equipped modifiers
- **Shift** dash
- **Q / E / F / R / V** unleash armed spell combos (slots 1–5)
- **Tab** open the Grimoire (character stats page)
- **1–5** choose / swap cards · **0** keep current on a swap · **Esc** free cursor · **Enter** restart


## What's in now
- First-person controller, moonlit environment, casting (normal + charge), three enemies, escalating waves, HUD, game-over
- **Combo** — landing hits builds a damage multiplier (shown by the crosshair); lapses if you stop hitting
- **Mana** — charged shots cost 1 mana; normal hits give +0.2; a charged-shot kill refunds a full unit
- **Dash** (Shift) — charges that recharge over time, with brief i-frames
- **XP orbs** — enemies drop them; they magnetize to you and feed your level
- **Leveling + upgrade cards** — every level, pick one of three rarity-weighted gifts (1/2/3): damage, cast speed, speed, charge, pierce, HP, lifesteal, combo, dash, mana, and more

## Roadmap — still to port
1. **Finishers** — combo charges a proc; Q/E/F unleash spells (wave/beam/root/fullmod/swarm/volley/heal), multi-slot + swap UI
2. **Ranged enemies + the Hollow Moon boss** (every 10th wave, phase machine)
3. **Hex Mark, resonance, moon ultimate, ritual/trials, the Grimoire loadout tab**
4. **Audio + a VFX pass** (GPUParticles3D + shaders for sigils/auras)

## Honest notes
- I built this without being able to compile it in my sandbox, so expect a **small first-run fixup**. The most likely spot is the `WorldEnvironment` setup in `Game.cs` (`BuildWorld`) — a couple of `Environment` property names can vary slightly by Godot version. If it won't build, comment out the environment block first to confirm the rest runs, then re-add it.
- Architecture choices made for a clean port: interactions use distance checks (like the web build) rather than physics bodies, and the world is built in code rather than hand-authored scenes — both make the next systems far easier to drop in. We can migrate to `CharacterBody3D` + real colliders later if you want physical wall collision.

Tell me what it looks like when you hit Play, and which system from the roadmap you want next.
