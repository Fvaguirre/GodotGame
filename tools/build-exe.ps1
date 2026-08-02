<#
.SYNOPSIS
    Export a standalone Windows .exe of the game into build/.

.DESCRIPTION
    Wraps `godot --export-release "Windows Desktop"`. Resolves the Godot binary the same way
    run-ai-scenario.ps1 does: -GodotPath arg -> $env:GODOT_PATH -> the known download path.

    The output is a FOLDER you copy whole to the test machine — the .exe alone will not run,
    it needs the .pck and the .NET assemblies beside it.

.EXAMPLE
    ./tools/build-exe.ps1
    ./tools/build-exe.ps1 -Debug          # debug template: keeps the console + verbose errors
#>
param(
    [string]$GodotPath = $env:GODOT_PATH,
    [switch]$Debug,
    [string]$OutDir = "build"
)

$ErrorActionPreference = "Stop"
$ProjectRoot = Split-Path -Parent $PSScriptRoot

# --- resolve Godot executable (same order as run-ai-scenario.ps1) ---
if (-not $GodotPath) {
    $default = "C:\Users\Frank\Downloads\Godot_v4.7-stable_mono_win64\Godot_v4.7-stable_mono_win64\Godot_v4.7-stable_mono_win64_console.exe"
    if (Test-Path $default) { $GodotPath = $default }
}
if (-not $GodotPath -or -not (Test-Path $GodotPath)) {
    Write-Error "Godot executable not found. Pass -GodotPath or set `$env:GODOT_PATH. Tried: '$GodotPath'"
}

$OutPath  = Join-Path $ProjectRoot $OutDir
$ExeName  = "WardensOfTheMoonlitGrove.exe"
$ExePath  = Join-Path $OutPath $ExeName

Write-Host "Godot   : $GodotPath"
Write-Host "Project : $ProjectRoot"
Write-Host "Output  : $ExePath"
Write-Host ""

# A running copy of the previous build holds clrjit.dll and friends open, so the wipe below fails with a bare
# "Access to the path is denied" that says nothing about the real cause. Check for it and say so plainly.
$running = Get-Process -Name "WardensOfTheMoonlitGrove*" -ErrorAction SilentlyContinue
if ($running) {
    Write-Error "The exported game is still running (PID $($running.Id -join ', ')). Close it and re-run - its .NET runtime DLLs are locked."
}

# a stale build/ can leave orphaned assemblies that shadow the new ones
if (Test-Path $OutPath) {
    try { Remove-Item -Recurse -Force $OutPath -ErrorAction Stop }
    catch { Write-Error "Could not clear '$OutPath' - something has a file open there (a running game, an open Explorer window, or antivirus). Close it and re-run.`n$($_.Exception.Message)" }
}
New-Item -ItemType Directory -Force -Path $OutPath | Out-Null

# 1) compile C# first so a build break is reported plainly instead of as an opaque export failure
Write-Host "== dotnet build =="
& dotnet build -v quiet -nologo
if ($LASTEXITCODE -ne 0) { Write-Error "C# build failed - fix that before exporting." }

# 2) import assets headlessly (a cold .godot/ otherwise exports an incomplete pck)
Write-Host "== importing assets =="
& $GodotPath --headless --path $ProjectRoot --import *> (Join-Path $OutPath "import.log")

# 3) export
$mode = if ($Debug) { "--export-debug" } else { "--export-release" }
Write-Host "== $mode =="
& $GodotPath --headless --path $ProjectRoot $mode "Windows Desktop" $ExePath
$code = $LASTEXITCODE

if (-not (Test-Path $ExePath)) {
    Write-Error "Export produced no .exe (godot exit $code). Check that export templates for 4.7.stable.mono are installed."
}

Remove-Item (Join-Path $OutPath "import.log") -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "--- build/ contents ---"
Get-ChildItem $OutPath | Sort-Object Length -Descending |
    ForEach-Object { "{0,10:N1} MB  {1}" -f ($_.Length / 1MB), $_.Name }
$total = (Get-ChildItem $OutPath -Recurse | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("`nTotal: {0:N1} MB" -f $total)
Write-Host "Copy the WHOLE build/ folder to the test machine - the .exe needs the .pck next to it."
exit 0
