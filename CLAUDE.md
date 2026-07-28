# CLAUDE.md

## Visual work

For any task involving graphics, models, shaders, particles, animation, lighting,
materials, UI motion, or effects, read VISUAL_DIRECTION.md first.

Do not treat functional completion as visual completion.

Do not use primitive placeholder geometry in final-facing assets unless explicitly
approved.

Before implementing, produce a visual-layer breakdown and a performance budget.

## Visual validation (AI test harness)

A successful compile is NOT visual validation. For any change to visuals, animation,
camera, materials, character models, UI, effects, or scene composition, run a scenario
and INSPECT the artifacts.

Run (Windows PowerShell):

    .\tools\run-ai-scenario.ps1 -Scenario witch_cast_jump

Godot exe resolves from `-GodotPath` -> `$env:GODOT_PATH` -> the default download path.

Artifacts (git-ignored, safe to clear) land in `artifacts/ai/`:
- `captures/<scenario>_<checkpoint>.png` (+ `latest.png`) — screenshots
- `captures/<scenario>_<checkpoint>.state.json` + `latest_state.json` — runtime state
- `godot.log` — full Godot stdout/stderr
- `result.json` — `{ status, captures_written, errors, warnings }`

You MUST: open every screenshot, read `result.json`, read `latest_state.json`, and skim
`godot.log`. After a visual fix, RERUN the scenario and report which captures you inspected.

When inspecting screenshots, follow **VISUAL_VALIDATION.md** — scan each frame ADVERSARIALLY
("what is wrong here?"), not confirmationally ("is my feature visible?"). Check grounding,
scale/proportion vs rings & peers, facing, clipping, whether it's actually animating, attachment
alignment, textures, and stray lighting/tint. Never rationalize an anomaly ("minor", "probably the
water") — investigate it (measure via state JSON / a log) or flag it. A "passed" scenario is not
visual validation.

Harness code lives in `res://dev/ai/` (dev-only; inert unless launched with
`-- --scenario <name>`). Nodes opt into observation via the `ai_observable` group +
`IAiObservable.GetAiDebugState()`. Add scenarios in `AiTestRunner` (map witch in
`ScenarioWitch`, add a `case` in `Dispatch`).

Do NOT modify the approved Mixamo skeleton hierarchy or rerig to fix clothing/
presentation. Do NOT auto-replace approved production models/animations/materials. Stage
generated or replacement assets in an isolated comparison scenario, not the live scene.
