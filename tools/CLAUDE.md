# Rules: scripts (`tools/`)

- Target environment is **Windows PowerShell 5.1**. No `&&`/`||` pipeline chaining, no ternary/`??`,
  no here-string indentation. Use `if/else`, `Get-Date`, `Join-Path`; write files with `-Encoding utf8`.
- `run-ai-scenario.ps1` — the Godot headless validation harness (don't break its `-Scenario`/`-GodotPath`
  contract; artifacts land in `artifacts/ai/`).
- `run-filtered.ps1` — wraps build/test/validate: FULL output to `artifacts/logs/` (git-ignored),
  filtered errors to console, **exit code preserved**. Prefer it (or delegate log triage to the
  `log-sifter` agent) over dumping long output into the session.
- Improve/wrap existing scripts rather than replacing reliable ones. Keep cross-platform scripts working.
