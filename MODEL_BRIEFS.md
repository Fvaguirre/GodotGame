# Model Briefs — Phase 4B (Authored Characters via Meshy)

Companion to `MODEL_DIRECTION.md`. This answers its 8-point pipeline inspection for THIS project and holds the
per-character asset briefs that double as Meshy generation prompts.

---

## Pipeline inspection (MODEL_DIRECTION.md §1–8)

1. **Authored in Blender / procedural / modular?** → **Authored (Meshy image-to-3D, cleaned in Blender).** The game is
   100% procedural today with no imported assets, so there is nothing modular to assemble from. Hero characters (9 witches,
   marquee bosses) are the right first authored targets; small/background enemies can stay procedural longer.
2. **Required mesh parts** → one skinned body mesh + separable material zones (skin, robe/cloth, hat, metal trim, emissive
   runes). Detachable accessories (staff/orb) as their own small meshes attached to hand sockets.
3. **Target tris** → LOD0 ≤ 18k (hero witch), LOD1 ≤ 5k, LOD2 ≤ 1.5k. Godot auto-LOD on import.
4. **Material slots + texture channels** → slots: `skin`, `cloth`, `trim`, `emissive`. Channels: albedo + roughness +
   normal (+ AO baked into roughness ok); emissive **masked** to runes/eyes only. Re-material onto project painterly
   shaders after import (drop toon/outline).
5. **Skeleton + sockets** → humanoid `Skeleton3D`; sockets `socket_hand_l`, `socket_hand_r`, `socket_head`, `socket_back`
   (see assets/models/README.md).
6. **Required animations** → idle, walk, run, cast (arm-raise), hurt, death. (Meshy auto-rig+animate gives basic
   idle/walk/run for HUMANOIDS; combat/cast clips are authored later or mapped from the existing code poses.)
7. **Godot import settings** → Scene import, Generate LODs on, Generate Tangents on, keep Skeleton.
8. **Runtime perf risks** → per-character skinned mesh + skeleton is heavier than the instanced/primitive path; enemies
   spawn in swarms, so authored ENEMIES need aggressive LODs + a distance cutoff to the procedural/imposter version.
   Witches (max 4 on screen in co-op) are safe. Validate FPS with a swarm before converting common enemies.

## Current status
- Receiving scaffolding is IN: `ModelAssets.TryLoad(key)` + `loadmodel <key>` dev command + `assets/models/`.
- **No meshes generated yet.** Characters remain procedural until `.glb` files land (graceful fallback).
- Animation wiring (AnimationTree, retarget, socket props) is authored per-character when its first rigged mesh arrives —
  NOT built speculatively.

## PROVEN RECIPE (from witch_lunar, first run)
- **Image→3D:** `meshy_image_to_3d` with a clean full-body front concept image. **CRITICAL: pass `should_remesh:true` +
  `target_polycount:25000`** in this call — meshy-6 otherwise outputs ~975k faces (over the 300k rig limit) and you waste
  a 5-credit remesh. Also set `pose_mode:"t-pose"`, `target_formats:["glb"]`. (~30 cr)
- **Rig:** `meshy_rig` on the (remeshed) task_id, `height_meters:1.8`. Includes walk+run. (~5 cr)
- **Download:** the rigging download returns URLs (NOT a local save via save_to). Fetch `rigged_character_glb_url` with
  Invoke-WebRequest → `assets/models/witch_lunar.glb`; grab `walking_glb_url`/`running_glb_url` → `assets/models/witches/`
  (URLs expire ~24h). Set scale/origin via remesh (`resize_height:1.8`, `origin_at:"bottom"`) — done, not a Blender step.
- **Import:** new .glb needs the GODOT EDITOR opened once to import (generate .import) before `ModelAssets`/`loadmodel` can
  load it. Enable Generate LODs + Tangents in the import dock.
- Net cost with the should_remesh fix ≈ **35 cr/witch** (image gen extra if not supplying your own concept).

## Workflow per character
1. Generate in Meshy from the brief below (image-to-3D from a concept image is far more style-consistent than text-only).
2. Auto-rig + auto-animate (humanoid) in Meshy → export `.glb`.
3. Drop into `assets/models/witches/` as `witch_<name>.glb`, import in Godot (settings above).
4. `loadmodel witch_<name>` — check scale (~1.8 m), forward axis (+Z), pivot at feet.
5. Re-material onto painterly shaders; wire into `WitchModel` behind `ModelAssets.Has(...)`; hook AnimationTree.

---

## BRIEF 001 — Lunar Witch (`witch_lunar`)  [TEMPLATE]

**Meshy prompt (text; pair with a concept image for style lock):**
> Stylized painterly fantasy witch, third-person game character, relaxed A-pose. Slender young sorceress of the moon:
> flowing deep-indigo and silver robe with a high collar, a wide-brim pointed witch hat with a crescent-moon silver
> clasp, subtle glowing crescent motifs on the hem. Hand-painted stylized textures (not photoreal, not low-poly, not
> flat-shaded), soft warm-cool lighting, clean readable silhouette. Cool moonlit palette with silver-white accents.
> Full body, feet on the ground, facing forward.

**Style refs (attach as images for consistency):** Avowed material richness + Spellbreak spell-caster silhouette +
Zelda: BotW/TotK stylized proportions. Keep the same reference set across ALL nine witches so the roster is cohesive.

**Per-witch palette (swap the robe/accent colours, keep the silhouette family):**
- Lunar: indigo/silver, crescent — Divine: white/gold, halo — Crimson: deep red/black, blood motif —
  Verdant: mossy green/bark, leaves — Gale: teal/white, swirl — Frost: pale blue/white, ice shards —
  Forsaken: violet/bone, hex sigils — Ember: orange/charcoal, embers — Arcane: magenta/violet, arcane runes.

**Post-generation cleanup checklist:**
- [ ] Scale to ~1.8 m tall; origin on the ground between the feet; facing +Z.
- [ ] Auto-rig (humanoid); verify hips/spine/arms/legs; add `socket_hand_r` (staff/orb), `socket_head` (hat), `socket_back`.
- [ ] Split materials into skin/cloth/trim/emissive; mask emissive to the crescent/runes only.
- [ ] Generate LODs on import; confirm ≤18k tris LOD0.
- [ ] `loadmodel witch_lunar` and eyeball scale/axis/pivot in-world.

**Then (code):** re-material onto painterly shaders, gate the swap in `WitchModel.Build` on `ModelAssets.Has("witch_lunar")`,
retarget idle/walk/run + map the existing cast poses to an AnimationTree.

---

## Remaining briefs
Duplicate BRIEF 001 per witch (swap palette/motif) and per hero enemy/boss as we produce them. Enemies get their own
briefs with non-humanoid rigs noted (spider = 8-leg, snake = spline, croc = quadruped).
