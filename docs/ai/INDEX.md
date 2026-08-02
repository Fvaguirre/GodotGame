# docs/ai — knowledge router

Read **only** the row(s) that match your task. Don't pre-load everything.

## By task type

| If the task is about… | Read |
|---|---|
| Getting oriented / architecture | `architecture/overview.md`, then `DEV_GUIDE.md` (repo root) |
| Multiplayer / RPCs / host authority | `architecture/overview.md` → Multiplayer; `DEV_HANDOFF.md` (gotchas) |
| Adding an enemy / witch / ability | `DEV_GUIDE.md` recipes; `COMBOS_AND_CARDS.md` |
| Damage / health / difficulty tuning | `DEV_GUIDE.md`; `DEV_HANDOFF.md` (fan-out budgets) |
| Visual / model / material / lighting work | `VISUAL_DIRECTION.md`, then `VISUAL_VALIDATION.md` (inspect adversarially) |
| Shaders / VFX | `SHADER_DIRECTION.md`; `shaders/CLAUDE.md` |
| Animation (cast/locomotion/procedural) | `ANIMATION_DIRECTION.md`, `WITCH_CAST_ANIMS.md` |
| Character models / Mixamo / silhouettes | `MODEL_DIRECTION.md`, `MODEL_BRIEFS.md` |
| Spell / impact VFX | `SPELL_OR_SPELL_IMPACT_DIRECTION.md` |
| Running / adding a validation scenario | `architecture/ai-test-harness.md`; `dev/ai/CLAUDE.md` |
| Meshy generation / generated-asset integration | `architecture/meshy-pipeline.md`; use the **asset-pipeline** agent |
| Continuing a feature already in progress | `handoffs/<feature>.md` (or `/resume-feature <feature>`) |
| Why a past architectural choice was made | `decisions/` (ADRs) |
| How to work efficiently in long sessions | `WORKFLOW.md` |

## Durable knowledge, at a glance
- **Architecture maps:** `architecture/` (compact, navigational — point to code, don't duplicate it).
- **Decisions:** `decisions/` (ADRs — durable choices only; template at `decisions/TEMPLATE.md`).
- **Current feature state:** `handoffs/` (one file per active feature; replace stale state, keep ≤~120 lines).

## Conventions for these docs
- Reference code by `File.cs:symbol`, not by pasting code.
- Reference the big root docs (`DEV_GUIDE.md`, `DEV_HANDOFF.md`, `*_DIRECTION.md`) rather than copying them.
- Keep each file compact. If it grows an encyclopedia, split it and add a row above.
