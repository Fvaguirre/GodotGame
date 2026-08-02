# Rules: generated / imported models (`assets/models/`)

Pipeline detail: `docs/ai/architecture/meshy-pipeline.md`. Art specs: `MODEL_DIRECTION.md`, `MODEL_BRIEFS.md`.

- **All Meshy generation goes through the `asset-pipeline` agent** — never run Meshy MCP calls from a
  coding session (payloads/polling flood context). Full responses cache to `.cache/meshy/` (git-ignored).
  Confirm credit cost before any credit-costing call.
- Normalize props/structures through `PropGlb.cs` (baked unit-height/max-dim, centered) + register the
  name in `PropGlb.Get()`. Tint via `prop_instanced.gdshader`, don't bake per-witch color.
- **Scale authored GLBs by mesh AABB, not `FitHeight`** (the Armature-0.01 gotcha). Authored bipeds share
  one rig; per-rig position retargeting lives in `Creature.cs`.
- Re-skin in-engine with the painterly shader; do NOT texture per-witch-recolored parts in Meshy (breaks
  recolor). A single neutral shared part (e.g. the glove) may be baked/textured.
- Do NOT auto-replace approved production models or modify the approved Mixamo skeleton. Validate a new
  asset in an isolated comparison scenario (`avatar_parts`, `*_showcase`), never the live scene.
