# AI visual-test harness

Deterministic visual-feedback loop: boot a scenario headless, drive real input, capture
screenshots + runtime state, so a change can be inspected (a compile is not validation).

## Run
```
./tools/run-ai-scenario.ps1 -Scenario <name>            # e.g. witch_cast_jump, floating_avatar, fp_hands
```
Godot exe resolves `-GodotPath` → `$env:GODOT_PATH` → default download path.
For filtered console output: `./tools/run-filtered.ps1 validate <scenario>`.

## Artifacts (git-ignored under `artifacts/ai/`)
- `captures/<scenario>_<checkpoint>.png` (+ `latest.png`) — screenshots
- `captures/<scenario>_<checkpoint>.state.json` (+ `latest_state.json`) — runtime state
- `godot.log` — full stdout/stderr · `result.json` — `{ status, captures_written, errors, warnings }`

## You MUST, after a visual change
Open **every** screenshot, read `result.json` + `latest_state.json`, skim `godot.log`, and inspect
**adversarially** ("what is wrong here?") per `VISUAL_VALIDATION.md` (grounding, scale/proportion,
facing, clipping, is-it-actually-animating, attachment, textures, stray tint). Rerun after a fix and
report which captures you inspected. `status: passed` ≠ visually correct.

## Key components
- Harness code: `dev/ai/` (dev-only; inert unless launched with `-- --scenario <name>`).
- `dev/ai/AiTestRunner.cs` — scenario registry (`{ "name", witchIdx }`) + `Dispatch` switch +
  per-scenario `async Task` methods; `ScenarioWitch` maps the witch.
- Observation: nodes join the `ai_observable` group + implement `IAiObservable.GetAiDebugState()`.

## Add a scenario
1. Add `{ "my_scene", <witchIdx> }` to the registry map.
2. Add `case "my_scene": await MyScene(); break;` to `Dispatch`.
3. Write `private async Task MyScene()` — drive input / build subjects, `await Capture("00_x")`.
Prefer close, clean camera framing (see `FloatingAvatarShowcase`) so captures are judgeable.

## Hazards
- The harness **clears `artifacts/ai/captures/`** at the start of each run — copy anything you need first.
- Scenarios that spawn on the pond put the subject over water (transparent shader can obscure additive VFX).
