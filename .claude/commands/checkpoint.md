---
description: Save durable feature state to docs/ai/handoffs/<feature>.md so the session can be cleared and resumed. Does NOT commit.
argument-hint: <feature-name>
allowed-tools: Read, Write, Edit, Grep, Glob, Bash(git status:*), Bash(git --no-pager diff:*), Bash(git --no-pager log:*), Bash(git branch:*)
---

You are checkpointing the current feature so its state survives clearing the conversation.

Feature name: **$ARGUMENTS**

If `$ARGUMENTS` is empty, STOP and reply: "Usage: `/checkpoint <feature-name>` — please name the feature." Do nothing else.

Otherwise:

1. Determine the current task from the conversation so far (objective, what changed, what's open).
2. Inspect relevant state (read-only — do NOT commit, push, stage, or modify tracked source):
   - `git status --porcelain` and `git branch --show-current`
   - `git --no-pager diff --stat` and, for the touched files, a focused `git --no-pager diff` (summarize it — never paste large diffs).
3. Create or **overwrite** `docs/ai/handoffs/<feature>.md` (slugify the name: lowercase, spaces→`-`). Replace stale content; do not endlessly append. Keep it **≤ ~120 lines**. Use exactly these sections:
   1. **Objective & intended behavior**
   2. **Current status** (what works / is in progress)
   3. **Constraints & decisions** (durable — the "why")
   4. **Files & major symbols** (`File.cs:Symbol`, exact paths)
   5. **Tests / validation performed** (commands run + results)
   6. **Current failures / uncertainties**
   7. **Briefly-rejected approaches**
   8. **Next 3 concrete actions**
4. EXCLUDE: raw logs, large diffs, full file contents, MCP payloads, transcripts, or anything already
   captured in `docs/ai/architecture/` or `decisions/` (reference those instead).
5. Follow `docs/ai/handoffs/README.md` for the shape. If a matching handoff exists, preserve its
   durable decisions + unresolved items while refreshing the state.
6. Reply with the path written and a 3-line summary. **Do not commit or push.**
