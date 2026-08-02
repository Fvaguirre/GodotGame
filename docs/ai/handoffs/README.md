# Handoffs — current feature state

One file per active feature: `docs/ai/handoffs/<feature-name>.md`. This is how a **fresh session
recovers a feature without the old conversation**.

- Create/update with **`/checkpoint <feature-name>`**; resume with **`/resume-feature <feature-name>`**.
- **Replace** stale state — do not endlessly append. Keep durable decisions + unresolved work.
- Target **≤ ~120 lines**. Reference `File.cs:Symbol` and other docs; do NOT paste transcripts, raw
  logs, huge diffs, full file contents, or MCP payloads.
- Delete a handoff when its feature is fully shipped + documented in `architecture/`/`decisions/`.

### Required sections (see any handoff for the shape)
1. Objective & intended behavior 2. Current status 3. Constraints & decisions
4. Files & major symbols 5. Tests/validation performed 6. Current failures/uncertainties
7. Briefly-rejected approaches 8. Next 3 concrete actions
