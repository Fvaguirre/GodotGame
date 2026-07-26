# Authored Models (Phase 4B) — Import Conventions

Drop imported `.glb` character/prop meshes here. Code loads them via `ModelAssets.TryLoad("<key>")`
(`res://assets/models/<key>.glb`); if a key has no file, the game falls back to its procedural model, so you
can add authored characters **one at a time** without breaking anything.

Test any import in-game: dev console → `loadmodel <key>` (spawns it 4u in front of you to check scale/axis/pivot).

## Keys
- Witches: `witch_lunar`, `witch_divine`, `witch_crimson`, `witch_verdant`, `witch_gale`, `witch_frost`, `witch_forsaken`, `witch_ember`, `witch_arcane`
- Enemies: `enemy_goblin`, `enemy_orc`, `enemy_troll`, `enemy_spider`, `enemy_snake`, `enemy_crocodile`, `enemy_mosquito`, … (one per `CreatureKind`)

## Geometry conventions (match these or the loader/placement will be off)
- **Up** = +Y. **Forward** = +Z (character faces +Z at rest).
- **Scale** = real metres. A witch ≈ 1.8 m tall; scale in Blender/Meshy so the mesh is that tall on import.
- **Pivot / origin** = on the **ground between the feet** (so placing at a world point stands it on the ground).
- **Pose** = relaxed A-pose (not T-pose) so hand/prop sockets read naturally.
- Single skinned mesh where possible; separate material slots for skin / robe / metal / emissive trim.

## Rig (characters)
- A `Skeleton3D` with a standard humanoid bone layout (hips, spine, chest, neck, head, shoulders/arms/hands, legs/feet).
- **Sockets** (empties parented to bones) named: `socket_hand_l`, `socket_hand_r` (staff/orb), `socket_head` (hat), `socket_back` (wings/cloak anchor).
- Non-humanoid enemies (spider/snake/croc): rig only if animated; otherwise a static mesh + code motion is fine to start.

## LOD (target triangle counts)
- LOD0 ≤ 12k tris (hero witches ≤ 18k), LOD1 ≤ 5k, LOD2 ≤ 1.5k. Godot can auto-generate LODs on import if the mesh is clean.

## Materials
- Author to the painterly look (see VISUAL_DIRECTION.md): albedo + roughness + normal; emissive **masked** to trim/runes only.
- After import, characters should be re-materialled onto the project's painterly materials (no flat toon, no ink outline) — see MODEL_BRIEFS.md.

## Import settings (Godot .glb)
- Import as **Scene**. Enable **Generate LODs**. Enable **Generate Tangents**. Root type: Node3D.
- For rigged characters keep the Skeleton; retarget/animation setup happens per-character when wired in.
