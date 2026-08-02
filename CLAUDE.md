# CLAUDE.md — Wardens of the Moonlit Grove

Co-op FPS/3rd-person roguelite spellcaster. **Godot 4.7 / .NET 8 (C#)**. ~120 `.cs` files
live **flat at the repo root**; `Main.tscn`'s root `Node3D` "Game" runs `Game.cs`.

This file is universal startup context only. **Read task-specific docs on demand — do not
pre-load them.** Routing lives in **`docs/ai/INDEX.md`** (read it when a task needs depth).

## Architectural boundaries (violating these causes the "why is MP broken?" class of bugs)
- **Host-authoritative.** The host simulates the world (enemies, waves, bosses, loot); clients
  own only their own avatar and route damage to the host. When in doubt, see
  `docs/ai/architecture/overview.md` → Multiplayer.
- **Player is a plain `Node3D`** (not a `CharacterBody3D`). FP camera `_cam`; 3rd-person is `tp3`.
- **Input actions are registered in C#** (`Game.cs`), NOT in `project.godot`.
- **New AoE / on-death fan-out must be budget-capped** (see `DEV_HANDOFF.md` gotchas) — an
  uncapped cascade froze MP.
- **Scatter/foliage is GPU-instanced** via `TreeField`/`PropField` (MultiMesh); logic-bearing
  props stay nodes.

## Non-negotiable conventions
- Match the surrounding code's style (flat files, `Nullable` disabled, terse comments).
- Painterly material system (`Vis.Painterly`), **no ink outlines**; do not re-add removed looks
  (e.g. Kuwahara) without asking.
- Do NOT modify the approved Mixamo skeleton hierarchy or auto-replace approved production
  models/animations/materials. Stage generated/replacement assets in an isolated comparison
  scenario, never the live scene.
- A successful compile is **NOT** visual validation (see below).

## Canonical commands
- **Build:** `dotnet build -v quiet -nologo`  (must end `0 Warning(s) 0 Error(s)`)
- **Visual/gameplay validation (Windows PowerShell):**
  `./tools/run-ai-scenario.ps1 -Scenario <name>` → artifacts in `artifacts/ai/`
- **Filtered build/test/validate** (full log to disk, only errors on console):
  `./tools/run-filtered.ps1 <build|validate> [scenario]`
- Shells: **PowerShell** (primary) and **Bash** (POSIX) — each takes its own syntax.

## Where detailed context lives (read only what the task needs)
- `docs/ai/INDEX.md` — router: task type → which doc to read.
- `docs/ai/architecture/` — subsystem maps (overview, ai-test-harness, meshy-pipeline).
- `docs/ai/decisions/` — durable architecture decisions (ADRs).
- `docs/ai/handoffs/` — current per-feature state (resume a feature from here).
- `DEV_GUIDE.md` (architecture + recipes), `DEV_HANDOFF.md` (hard-won gotchas).
- `*_DIRECTION.md` + `VISUAL_VALIDATION.md` — art/visual specs (see INDEX for which).
- Nested `CLAUDE.md` files (`shaders/`, `dev/ai/`, `tools/`, `assets/models/`) auto-load when
  you work in that subtree — that's where subsystem-specific rules live.

## Session hygiene (keeps long work cheap — full guide in `docs/ai/WORKFLOW.md`)
- One session per coherent feature/incident. **Before you `/clear` or `/compact` a substantial
  task, run `/checkpoint <feature>`** so the state is durable outside the conversation.
- Start a fresh session with `/resume-feature <feature>` instead of scrolling old context.
- Delegate broad exploration to the **`code-cartographer`** agent, log triage to **`log-sifter`**,
  and all Meshy/generated-asset work to **`asset-pipeline`** — they return summaries, not dumps,
  and run on cheaper models. Keep Meshy MCP out of coding sessions.

## Verification expectations
- After any change: build clean (`0/0`). After any **visual/animation/camera/material/model/UI/
  VFX/scene** change: run the relevant scenario, **open every screenshot**, read `result.json` +
  `latest_state.json`, skim `godot.log`, and inspect **adversarially** ("what is wrong here?")
  per `VISUAL_VALIDATION.md`. Rerun after a fix; report which captures you inspected.
- Report outcomes honestly — if a step was skipped or a test failed, say so.

## Safety
- Don't commit/push unless asked; branch first if on `main`.
- Before deleting/overwriting a file you didn't create, look at it first and surface conflicts.
- Never commit secrets: `.mcp.json` holds the Meshy API key and is git-ignored — keep it that way.
- Confirm hard-to-reverse or outward-facing actions before doing them.
