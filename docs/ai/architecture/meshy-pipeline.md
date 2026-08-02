# Meshy / generated-asset pipeline

How generated 3D assets get into the game — and how to keep the (large, noisy) Meshy MCP traffic
**out of the coding session**. All Meshy work goes through the **`asset-pipeline`** subagent.

## Isolation rules (why this exists)
- Meshy MCP responses (generation payloads, repeated poll status, signed URLs) are huge and pollute
  context. MCP servers are session-global in Claude Code (can't be hard-scoped per agent), so the
  convention is: **only the `asset-pipeline` agent (or a dedicated throwaway session) invokes Meshy.**
  Root `CLAUDE.md` deliberately contains no Meshy API detail.
- The agent saves full API responses/metadata under **`.cache/meshy/`** (git-ignored) and returns only
  a compact summary (task id, status, local paths, texture paths, format, scale/orientation, rig
  status, import warnings, integration files changed).
- **Never invoke Meshy while setting up tooling.** Confirm credit cost before any credit-costing call.

## Cost gate (from the Meshy MCP guidance)
Before any credit-costing tool, present the cost and wait for confirmation. Rough costs:
text_to_3d 5–20 · image_to_3d 5–30 · text_to_image (nano-banana-pro) 9 · remesh 5 · rig 5 · animate 3.
Meshy keeps the raw ~400k-tri mesh unless you `meshy_remesh` — **always remesh/decimate**.
Do NOT texture in Meshy for per-witch-recolored parts (breaks recolor + clashes with painterly); a
single neutral shared part (e.g. the glove) MAY be baked/textured. `should_texture:false` otherwise.

## Integration (in-engine)
- `PropGlb.cs` normalizes a prop/structure GLB → unit-height/max-dim, baked, centered; instanced tint
  via `shaders/prop_instanced.gdshader`. Register a name in `PropGlb.Get()` (subdir + rough/wind flags).
- Authored bipeds (goblin/zombie/ogre/taker) share one Meshy rig; scale by mesh AABB (Armature-0.01
  gotcha); per-rig position retargeting in `Creature.cs`.
- Assets live under `assets/models/**` (see `assets/models/CLAUDE.md`). Validate a new asset in an
  **isolated comparison scenario** (`avatar_parts`, `*_showcase`), never the live scene.

## Key files
`PropGlb.cs`, `Creature.cs`, `FloatingAvatar.cs`, `shaders/prop_instanced.gdshader`,
`assets/models/**`. See `MODEL_DIRECTION.md` / `MODEL_BRIEFS.md` for art specs.
