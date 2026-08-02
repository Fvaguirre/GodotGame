# Rules: shaders & VFX (`shaders/`)

Durable conventions only. Art intent: `SHADER_DIRECTION.md`; overall look: `VISUAL_DIRECTION.md`.

- The look comes from **materials, not outlines** — build on `painterly.gdshader` / `Vis.Painterly`.
  Do NOT add ink outlines or re-introduce the removed Kuwahara post-filter (see `docs/ai/decisions/0002`).
- Perf discipline: no screen texture, no variable-count loops (fixed-octave fbm), no transparency
  unless intended; gate detail on the `quality` uniform. Additive VFX that must sit over the pond water
  needs a higher `render_priority` (transparent water sorts otherwise).
- Instanced props sample baked albedo/normal via `prop_instanced.gdshader` + per-instance jitter — keep
  that path for scattered repeats (`PropField`).
- Any shader change is a **visual** change → validate with a scenario and inspect adversarially
  (`docs/ai/architecture/ai-test-harness.md` + `VISUAL_VALIDATION.md`). A compile is not validation.
- When a mesh's local axes are unknown, don't guess — use an RGB axis gizmo or an in-engine poser.
