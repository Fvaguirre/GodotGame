# ADR 0002: Painterly materials, no ink outlines

- **Status:** accepted
- **Date:** (recorded from the visual-overhaul program)

## Context
The look was drifting toward a flat cel/toon style with ink outlines. The target art language
(Avowed/Spellbreak/Zelda painterly) wants authored silhouettes + macro material variation, not
outline-driven readability. A fullscreen Kuwahara painterly filter was also trialled.

## Decision
The painterly look comes from **materials** — `Vis.Painterly(...)` (matte, world-space value/hue
fbm drift, roughness variation, masked emission) on `painterly.gdshader` — **not** from ink outlines
and **not** from a fullscreen post filter. The Kuwahara post-process was removed.

## Consequences
- Large surfaces stop reading uniform; per-witch recolor stays possible (tint, not baked texture).
- Do NOT re-add outlines (`Game.Toon`/`ToonEmissive` clash with painterly) or the Kuwahara filter
  without explicit approval.
- Meshy parts are re-skinned in-engine with the painterly shader, not textured in Meshy (per-witch
  recolor would break) — except a single neutral shared part may be baked.

## Alternatives rejected
- **Cel/toon + ink outlines** — wrong art language for authored silhouettes.
- **Fullscreen Kuwahara painterly filter** — owner disliked the look (both our shader + the Acerola alt).

## Relevant files / symbols
`VisualCore.cs:Vis.Painterly`, `shaders/painterly.gdshader`; `VISUAL_DIRECTION.md`, `SHADER_DIRECTION.md`.
