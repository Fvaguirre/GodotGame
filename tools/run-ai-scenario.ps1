<#
    run-ai-scenario.ps1 — launch a deterministic AI visual-test scenario and collect artifacts.

    Usage:
        .\tools\run-ai-scenario.ps1 -Scenario witch_cast_jump
        .\tools\run-ai-scenario.ps1 -Scenario witch_cast_jump -GodotPath "C:\path\to\Godot_console.exe"

    Godot executable resolution order: -GodotPath arg -> $env:GODOT_PATH -> known default download path.
    Non-destructive: only clears the artifacts/ai output folder. Never touches source assets.
    Returns the Godot process exit code (0 = scenario passed). Fails clearly if Godot can't be found or the run times out.
#>
param(
    [string]$Scenario = "witch_cast_jump",
    [string]$GodotPath = $env:GODOT_PATH,
    [int]$Resolution_W = 1280,
    [int]$Resolution_H = 720,
    [int]$TimeoutSeconds = 90,
    [long]$Seed = 0          # 0 = use the scenario's built-in default world seed; otherwise force this map seed
)

$ErrorActionPreference = "Stop"

# --- paths ---
$ProjectRoot = Split-Path $PSScriptRoot -Parent          # tools/ sits directly under the project root
$ArtifactsDir = Join-Path $ProjectRoot "artifacts\ai"
$CapturesDir  = Join-Path $ArtifactsDir "captures"
$LogPath      = Join-Path $ArtifactsDir "godot.log"
$ErrPath      = Join-Path $ArtifactsDir "godot.err"
$ResultPath   = Join-Path $ArtifactsDir "result.json"

# --- resolve Godot executable ---
if (-not $GodotPath) {
    $default = "C:\Users\Frank\Downloads\Godot_v4.7-stable_mono_win64\Godot_v4.7-stable_mono_win64\Godot_v4.7-stable_mono_win64_console.exe"
    if (Test-Path $default) { $GodotPath = $default }
}
if (-not $GodotPath -or -not (Test-Path $GodotPath)) {
    Write-Error "Godot executable not found. Pass -GodotPath or set `$env:GODOT_PATH. Tried: '$GodotPath'"
    exit 2
}

# --- prepare artifact dir (safe clear of previous run only) ---
New-Item -ItemType Directory -Force -Path $CapturesDir | Out-Null
Get-ChildItem $CapturesDir -File -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
foreach ($f in @($LogPath, $ErrPath, $ResultPath)) { if (Test-Path $f) { Remove-Item $f -Force } }

Write-Host "Godot   : $GodotPath"
Write-Host "Project : $ProjectRoot"
Write-Host "Scenario: $Scenario  ($Resolution_W x $Resolution_H)"

# Import any new/changed assets first (running the game does NOT import them). Only reimports changed files, so it's fast.
Write-Host "Importing assets..."
& $GodotPath --headless --path $ProjectRoot --import *> (Join-Path $ArtifactsDir "import.log")

# engine args, then '--' separates our user args (read via OS.GetCmdlineUserArgs in AiTestRunner)
$godotArgs = @(
    "--path", $ProjectRoot,
    "--resolution", "$($Resolution_W)x$($Resolution_H)",
    "--", "--scenario", $Scenario
)
if ($Seed -ne 0) { $godotArgs += @("--seed", "$Seed") }

$proc = Start-Process -FilePath $GodotPath -ArgumentList $godotArgs -PassThru `
    -RedirectStandardOutput $LogPath -RedirectStandardError $ErrPath

# Wait-Process is reliable under NonInteractive PowerShell (unlike Process.WaitForExit(int) with redirected streams).
$proc | Wait-Process -Timeout $TimeoutSeconds -ErrorAction SilentlyContinue
$killed = $false
if (-not $proc.HasExited) {
    Write-Output "Timed out after $TimeoutSeconds s - killing Godot."
    try { $proc.Kill() } catch {}
    Start-Sleep -Milliseconds 500
    "`n[launcher] KILLED after ${TimeoutSeconds}s timeout" | Add-Content $LogPath
    $killed = $true
}

# merge stderr into the main log for one-stop inspection
if (Test-Path $ErrPath) { "`n=== STDERR ===" | Add-Content $LogPath; Get-Content $ErrPath | Add-Content $LogPath }

# --- report (Write-Output is captured reliably by both the console and file redirection) ---
Write-Output "`n--- Artifacts ---"
Write-Output "log    : $LogPath"
Write-Output "result : $ResultPath"
Write-Output "state  : $(Join-Path $ArtifactsDir 'latest_state.json')"
if (Test-Path $CapturesDir) {
    Get-ChildItem $CapturesDir -Filter *.png | ForEach-Object { Write-Output ("capture: " + $_.FullName) }
}

# result.json is the game's AUTHORITATIVE pass/fail (redirected-stream process ExitCode is unreliable via Start-Process)
$exit = 1
if ($killed) {
    $exit = 124
} elseif (Test-Path $ResultPath) {
    Write-Output "`n--- result.json ---"
    $raw = Get-Content $ResultPath -Raw
    Write-Output $raw
    try { if (($raw | ConvertFrom-Json).status -eq "passed") { $exit = 0 } } catch {}
} else {
    Write-Output "WARNING: no result.json produced - the scenario did not complete cleanly (see $LogPath)."
    $exit = 3
}

Write-Output "`nExit code: $exit"
exit $exit
