---
description: Reconstruct the minimum context to continue a feature in a fresh session, from its handoff. Returns a compact execution brief.
argument-hint: <feature-name>
allowed-tools: Read, Grep, Glob, Bash(git status:*), Bash(git --no-pager diff:*), Task
---

You are resuming a feature in a fresh context. Load the **minimum** needed — do NOT read the repo broadly.

Feature name: **$ARGUMENTS**

If `$ARGUMENTS` is empty, STOP and reply: "Usage: `/resume-feature <feature-name>`. Available handoffs:" then list `docs/ai/handoffs/*.md` (excluding README). Do nothing else.

Otherwise:

1. Read `docs/ai/handoffs/<feature>.md` (slugify the name). If it doesn't exist, list the available
   handoffs and stop.
2. **Delegate the heavy reading to the `code-cartographer` agent** (keeps file contents out of this
   context). Ask it to: read ONLY the files/symbols the handoff names, read only the directly relevant
   `docs/ai/architecture/*` and `decisions/*` the handoff points to, locate the key symbols, and return
   a ≤800-word map (paths + symbols + data/control flow + risks) — no file dumps.
3. In this context, additionally check the **current diff** for those files: `git status --porcelain`
   and a focused `git --no-pager diff` on the handoff's files (summarize; don't paste large diffs).
4. Return a compact **execution brief (≤ ~1200 words)** with exactly:
   - **Objective** (current goal)
   - **Relevant architecture** (only what applies)
   - **Current state** (from the handoff + live diff)
   - **Important files & symbols**
   - **Known failures / uncertainties**
   - **Immediate recommended action** (the single next step)

Do not begin implementing until the brief is delivered and the user confirms direction (or the next
action is unambiguous). Do not read files the handoff doesn't reference unless the user asks.
