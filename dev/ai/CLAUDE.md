# Rules: AI visual-test harness (`dev/ai/`)

Full guide: `docs/ai/architecture/ai-test-harness.md`. Inspection standard: `VISUAL_VALIDATION.md`.

- Harness is **dev-only** and inert unless launched with `-- --scenario <name>`. Don't let it affect
  the live game.
- Add a scenario in `AiTestRunner.cs`: register `{ "name", witchIdx }`, add a `case` in `Dispatch`,
  write the `async Task`. Map the witch in `ScenarioWitch`.
- Nodes expose state by joining the `ai_observable` group + implementing `IAiObservable.GetAiDebugState()`.
- After a visual change, RUN the scenario and INSPECT: open every screenshot, read `result.json` +
  `latest_state.json`, skim `godot.log`, scan each frame **adversarially** ("what is wrong here?").
  Rerun after a fix; report which captures you inspected. `status: passed` ≠ visually correct.
- The harness **clears `artifacts/ai/captures/` each run** — copy anything you need to keep first.
- Prefer close, clean framing so captures are judgeable (see `FloatingAvatarShowcase`).
