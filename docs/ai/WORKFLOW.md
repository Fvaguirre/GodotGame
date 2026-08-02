# Working efficiently in long sessions

The point: keep deep project knowledge **on disk** (in `docs/ai/` + the root docs), so you can
checkpoint, clear, and resume in a fresh cheap context instead of paying to keep a 150k-token,
8-hour conversation alive.

## Session lifecycle
- **One session per coherent feature or debugging incident.** Name it after the feature.
- **Before `/clear` or a big `/compact`:** run `/checkpoint <feature>` → writes/refreshes
  `docs/ai/handoffs/<feature>.md`.
- **Start a fresh session** with `/resume-feature <feature>` (reconstructs the minimum context).
- `/compact` **only** to keep going on the *same* task; `/clear` when switching subsystems.
- When compacting, drop: raw logs, obsolete plans, exploratory dead ends, full tool/file dumps,
  MCP payloads. Keep: current objective, decisions, files/symbols, next actions.

## Delegate to keep the main context lean
| Need | Use | Why |
|---|---|---|
| "Where is X / how does Y work / where do I extend?" | **code-cartographer** agent | reads across files, returns a ≤1000-word map (paths+symbols), never file dumps; runs on `sonnet` |
| Triage a compiler/test/Godot/import log | **log-sifter** agent | collapses cascades → the real error + fix; runs on `haiku`; never echoes the log |
| Meshy generation / integrate a generated asset | **asset-pipeline** agent | keeps huge MCP traffic out of context; caches to `.cache/meshy/`; returns a compact summary |
| Filtered build/test/validate | `./tools/run-filtered.ps1` | full log to `artifacts/logs/`, only errors on console |

## Model routing (cost)
- **Opus-level reasoning** only for hard architecture, complex debugging, or final review.
- **Cheaper models** (`sonnet` for implementation/exploration, `haiku` for logs/formatting/repetitive
  validation) for everything else. The specialized agents pin their own cheaper model in frontmatter —
  don't let a subagent silently inherit Opus.
- A session default model can be set in `.claude/settings.json` (`model`); per-agent models live in
  `.claude/agents/*.md` frontmatter.

## Read on demand
Root `CLAUDE.md` is universal only. Everything else is loaded when the task needs it — start from
`docs/ai/INDEX.md`. Nested `CLAUDE.md` files (`shaders/`, `dev/ai/`, `tools/`, `assets/models/`)
auto-load when you edit files in that subtree.
