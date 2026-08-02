#requires -version 5
<#
  run-filtered.ps1 — run a build / test / validation command, write the FULL output to a git-ignored
  log under artifacts/logs/, and print only errors/warnings/failures (deduped, capped) to the console.
  Preserves the underlying command's exit code.

  Usage:
    ./tools/run-filtered.ps1 build
    ./tools/run-filtered.ps1 validate witch_cast_jump
    ./tools/run-filtered.ps1 test
#>
param(
  [Parameter(Mandatory = $true)][ValidateSet('build', 'test', 'validate')] [string]$Command,
  [string]$Scenario = 'witch_cast_jump',
  [int]$MaxLines = 120
)
$ErrorActionPreference = 'Continue'
$root   = Split-Path -Parent $PSScriptRoot          # tools/ is under the repo root
$logDir = Join-Path $root 'artifacts\logs'
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Force -Path $logDir | Out-Null }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$log   = Join-Path $logDir ("{0}-{1}.log" -f $Command, $stamp)

switch ($Command) {
  'build'    { $exe = 'dotnet'; $cmdArgs = @('build', '-v', 'quiet', '-nologo') }
  'test'     { $exe = 'dotnet'; $cmdArgs = @('test', '-v', 'quiet', '-nologo') }
  'validate' { $exe = 'powershell'; $cmdArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'run-ai-scenario.ps1'), '-Scenario', $Scenario) }
}

Write-Host ("[run-filtered] {0}{1} -> {2}" -f $Command, $(if ($Command -eq 'validate') { " $Scenario" } else { "" }), $log)

Push-Location $root
$output = & $exe @cmdArgs 2>&1          # merge stderr; native exit code still lands in $LASTEXITCODE
$code = $LASTEXITCODE
Pop-Location
$output | Out-File -LiteralPath $log -Encoding utf8

# Filter: keep error/warning/fail lines, collapse identical dupes with counts, cap to $MaxLines
$pattern = 'error|warning|fail|exception|unhandled|CS\d{4}|MSB\d{4}|"errors"|"warnings"|"status"'
$seen = @{}
$order = New-Object System.Collections.Generic.List[string]
foreach ($line in (Get-Content -LiteralPath $log)) {
  $t = ($line | Out-String).Trim()
  if ($t -eq '') { continue }
  if ($t -notmatch $pattern) { continue }
  if ($seen.ContainsKey($t)) { $seen[$t]++; continue }
  $seen[$t] = 1
  $order.Add($t) | Out-Null
  if ($order.Count -ge $MaxLines) { break }
}

if ($order.Count -eq 0) {
  Write-Host "[run-filtered] no errors/warnings matched. Last 12 lines:"
  Get-Content -LiteralPath $log -Tail 12
}
else {
  foreach ($t in $order) {
    if ($seen[$t] -gt 1) { Write-Host ("{0}   (x{1})" -f $t, $seen[$t]) } else { Write-Host $t }
  }
}
Write-Host ("[run-filtered] full log: {0}  (exit {1})" -f $log, $code)
exit $code
