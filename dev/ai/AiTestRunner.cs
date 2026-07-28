using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Grove.Dev.Ai
{
    // DEV-ONLY visual-test runner. Activated ONLY when the game is launched with `-- --scenario <name>` (TryBoot, called at the
    // end of Game._Ready). It boots a deterministic run, drives scripted INPUT ACTIONS (the real player path — no OS automation),
    // captures screenshots + structured state at state-driven checkpoints, writes a machine-readable result, and quits with a
    // nonzero exit code on failure. Artifacts land in <project>/artifacts/ai/. Nothing here runs during normal play.
    public partial class AiTestRunner : Node
    {
        // scenario -> witch index (0 Lunar … 8 Arcane). Only authored witches are meaningful for visual checks.
        private static readonly Dictionary<string, int> ScenarioWitch = new()
        {
            { "witch_cast_jump", 1 },   // Divine — exercises multi-mesh (body+hat+gown) + the jump/filter fix
            { "witch_locomotion", 1 },  // Divine — directional strafe blends + gown at speed
            { "goblin_showcase", 1 },   // authored goblin: walk + procedural slash (L/R)
            { "enemy_affix_showcase", 1 },  // goblin/orc/archer + affixes/elite — verify types/affixes/names still work
            { "prop_preview", 0 },      // lineup of the authored Meshy Grove props/structures — eyeball look/grounding/scale
            { "grove_showcase", 0 },    // props/structures scattered IN-WORLD via the real placement path (DebugGrovePatch)
            { "structure_stress", 0 },  // walk up the climbable keep + fps sampling + pedestal/effigy & ritual grounding checks
            { "collision_audit", 0 },   // line up all structures with the collision viz ON — audit solid/deck/ramp vs the models
            { "pause_menu", 1 },        // ESC pause menu (Options/Quit Run/Restart Run + rebinder) → Options overlay over the paused game
            { "collider_editor", 0 },   // in-engine collider authoring: model lineup, spawn a collider via the palette, move it, save to res://data
        };

        private string _scenario = "witch_cast_jump";
        private readonly Godot.Collections.Array<string> _errors = new();
        private readonly Godot.Collections.Array<string> _warnings = new();
        private int _capturesWritten;
        private int _frame;
        private ulong _startMs;
        private const int GlobalTimeoutFrames = 6000;   // hard cap (~50s @120fps) so a hang can never wedge the run

        public const long DefaultScenarioSeed = 733;   // a known map that frames well at spawn; override with `-- --seed <n>`

        // Fixed world seed for deterministic scenario framing (null if not a scenario launch). Read at Game._Ready BEFORE BuildWorld.
        public static long? ScenarioWorldSeed()
        {
            var args = OS.GetCmdlineUserArgs();
            bool scenario = false; long seed = DefaultScenarioSeed;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--scenario") scenario = true;
                else if (args[i] == "--seed" && i + 1 < args.Length && long.TryParse(args[i + 1], out var s)) seed = s;
            }
            return scenario ? seed : (long?)null;
        }

        // Parse the launch args; if a scenario was requested, boot it and attach the runner. Returns true if it took over boot.
        public static bool TryBoot(Game game)
        {
            string scenario = null;
            var args = OS.GetCmdlineUserArgs();   // args after the `--` separator
            for (int i = 0; i < args.Length; i++)
                if (args[i] == "--scenario" && i + 1 < args.Length) { scenario = args[i + 1]; break; }
            if (scenario == null) return false;

            int witch = ScenarioWitch.TryGetValue(scenario, out var w) ? w : 0;
            GD.Print($"[AiTestRunner] booting scenario '{scenario}' (witch {witch})");
            game.StartScenarioRun(witch);

            var runner = new AiTestRunner { _scenario = scenario, Name = "AiTestRunner" };
            game.AddChild(runner);
            return true;
        }

        private static string ArtifactsDir => Path.Combine(ProjectSettings.GlobalizePath("res://"), "artifacts", "ai");
        private static string CapturesDir => Path.Combine(ArtifactsDir, "captures");

        public override async void _Ready()
        {
            _startMs = Time.GetTicksMsec();
            try
            {
                Directory.CreateDirectory(CapturesDir);
                await Dispatch();
            }
            catch (Exception e)
            {
                _errors.Add($"unhandled: {e}");
                CrashLogger.LogFile($"[AiTestRunner] unhandled: {e}");
            }
            finally
            {
                ReleaseAllInputs();
                Finish();
            }
        }

        private async Task Dispatch()
        {
            switch (_scenario)
            {
                case "witch_cast_jump": await WitchCastJump(); break;
                case "witch_locomotion": await WitchLocomotion(); break;
                case "goblin_showcase": await GoblinShowcase(); break;
                case "enemy_affix_showcase": await EnemyAffixShowcase(); break;
                case "prop_preview": await PropPreview(); break;
                case "grove_showcase": await GroveShowcase(); break;
                case "structure_stress": await StructureStress(); break;
                case "collision_audit": await CollisionAudit(); break;
                case "pause_menu": await PauseMenu(); break;
                case "collider_editor": await ColliderEditorScenario(); break;
                default: _errors.Add($"unknown scenario '{_scenario}'"); break;
            }
        }

        // Directional locomotion: hold each move direction long enough for the 2D blend to settle (and to actually travel), and
        // capture the strafe pose. The per-capture state records tp3_puppet.blend so the blend vector is inspectable too.
        private async Task WitchLocomotion()
        {
            var p = Game.I?.Player;
            if (p == null) { _errors.Add("no Player"); return; }

            await WaitFrames(30);
            p.ToggleThirdPersonPlay();
            await WaitFrames(60);          // let grounding + the post-spawn structure-settle relocate her onto open ground first
            await Capture("00_idle");

            // hold each direction long enough to reach RUN speed and actually travel (clear ground now), so the blend hits the
            // run ring (~2), not a stuck walk. State JSON records tp3_puppet.blend so the reached radius is inspectable.
            await Hold("move_forward", 90, "01_run_forward");
            await Hold("move_left",    80, "02_strafe_left");
            await Hold("move_right",   80, "03_strafe_right");
            await Hold("move_back",    80, "04_back");
        }

        // Authored goblin: spawn one in front, PINNED in frame (its AI drift is overridden), and capture idle / walk-in-place /
        // each-arm slash. It faces the player via the normal AI facing even while pinned.
        private async Task GoblinShowcase()
        {
            var p = Game.I?.Player; if (p == null) { _errors.Add("no Player"); return; }
            Game.I.NoSpawn = true; Game.I.ClearEnemies();
            await WaitFrames(20);
            Vector3 fwd = -p.GlobalTransform.Basis.Z; fwd.Y = 0f; fwd = fwd.Normalized();
            Vector3 pin = p.GlobalPosition + fwd * 7f;
            Game.I.SpawnEnemyForTest("shade", pin);
            await WaitFrames(40);
            var gob = FindGoblin();
            if (gob == null) { _errors.Add("no authored goblin spawned"); return; }

            gob.DebugWalk(0f);                                     // idle shuffle
            await PinHold(gob, pin, 20); await Capture("00_idle");
            gob.DebugWalk(1f);                                     // full walk cycle, in place
            await PinHold(gob, pin, 30); await Capture("01_walk");
            gob.DebugWalk(0f); gob.DebugSlash(true);
            await PinHold(gob, pin, 3);  await Capture("02_slash_left");
            await PinHold(gob, pin, 12);
            gob.DebugSlash(false);
            await PinHold(gob, pin, 3);  await Capture("03_slash_right");
        }

        // Hold an enemy pinned at (pos.X,pos.Z) for `frames` (overriding its AI drift), keeping Y so it stays grounded.
        private async Task PinHold(Enemy e, Vector3 pos, int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                if (e != null && GodotObject.IsInstanceValid(e)) e.GlobalPosition = new Vector3(pos.X, e.GlobalPosition.Y, pos.Z);
                await NextFrame(); _frame++;
            }
        }

        // A lineup of enemy types + affixes/elite to verify the affix aura, elite ring, name labels, other-type models and size
        // scaling all still work with the goblin model in. Inspect the capture + the per-actor state JSON.
        private async Task EnemyAffixShowcase()
        {
            var p = Game.I?.Player; if (p == null) { _errors.Add("no Player"); return; }
            Game.I.NoSpawn = true; Game.I.ClearEnemies();
            await WaitFrames(20);
            Vector3 fwd = -p.GlobalTransform.Basis.Z; fwd.Y = 0f; fwd = fwd.Normalized();
            Vector3 right = p.GlobalTransform.Basis.X; right.Y = 0f; right = right.Normalized();
            Vector3 c = p.GlobalPosition + fwd * 13f;
            Game.I.SpawnEnemyForTest("shade", c - right * 8f);           // plain goblin
            Game.I.SpawnEnemyForTest("shade", c - right * 4f, 1);        // affix 1 (shielded)
            Game.I.SpawnEnemyForTest("shade", c, 3);                     // affix 3 (vampiric)
            Game.I.SpawnEnemyForTest("shade", c + right * 4f, 0, true);  // elite
            Game.I.SpawnEnemyForTest("brute", c + right * 8f);           // orc (its own model, unchanged)
            Game.I.SpawnEnemyForTest("archer", c + right * 11f);         // archer (Goblin kind + bow overlay)
            await WaitFrames(70);
            await Capture("00_lineup");
        }

        // Lineup the authored Meshy props/structures in front of the player (uniform preview height, grounded) to eyeball look.
        private async Task PropPreview()
        {
            var p = Game.I?.Player; if (p == null) { _errors.Add("no Player"); return; }
            Game.I.NoSpawn = true; Game.I.ClearEnemies();
            await WaitFrames(20);
            Vector3 fwd = -p.GlobalTransform.Basis.Z; fwd.Y = 0f; fwd = fwd.Normalized();
            // (name, preview height) — exercises the REAL PropGlb pipeline (baked-normalised mesh + prop_instanced shader + tint)
            (string name, float h)[] props = { ("leaf_a", 0.6f), ("leaf_b", 0.6f), ("leaf_c", 0.6f), ("leafpile_a", 1.2f), ("leafpile_b", 1.0f) };
            for (int i = 0; i < props.Length; i++)   // clean centered close-up of each, one at a time
            {
                var m = PropGlb.Instance(props[i].name, props[i].h, seed: 12345 + i);
                Game.I.AddChild(m);
                Vector3 pos = p.GlobalPosition + fwd * (5.0f + props[i].h * 1.7f);   // step back proportional to height so it frames whole
                pos.Y = Game.I.SurfaceHeight(pos, 1e9f);
                m.GlobalPosition = pos;
                await WaitFrames(6);
                await Capture($"{i:00}_{props[i].name}");
                m.QueueFree();
                await WaitFrames(2);
            }
        }

        // Scatter a representative Grove patch IN-WORLD (real placement path: instanced mushroom/fern/reeds + flower/pumpkin
        // nodes + ruin & staircase) a few metres ahead, then capture. This validates the shipping scatter — including the
        // MultiMesh instanced path — not a hand-placed lineup.
        private async Task GroveShowcase()
        {
            var p = Game.I?.Player; if (p == null) { _errors.Add("no Player"); return; }
            Game.I.NoSpawn = true; Game.I.ClearEnemies();
            await WaitFrames(30);
            Vector3 fwd = -p.GlobalTransform.Basis.Z; fwd.Y = 0f; fwd = fwd.Normalized();
            Vector3 center = p.GlobalPosition + fwd * 11f;
            center.Y = Game.I.SurfaceHeight(center, 1e9f);
            Game.I.DebugGrovePatch(center);
            await WaitFrames(12);                 // let the MultiMesh flush + a frame render
            await Capture("00_patch");
            await WaitFrames(30);                 // settle (any node tweens / particles)
            await Capture("01_settled");
        }

        // Pause menu: open the ESC pause overlay (PAUSED + Options / Quit Run / Restart Run + spell-combo rebinder), then the
        // Options overlay (the full main-menu options page) transparently over the paused game, then back out. Eyeball layout.
        private async Task PauseMenu()
        {
            var g = Game.I; var p = g?.Player; if (p == null) { _errors.Add("no Player"); return; }
            g.NoSpawn = true; g.ClearEnemies();
            await WaitFrames(40);                 // let the run settle + grounding relocate onto open ground

            g.State = GameState.Pause;            // enter pause exactly as Esc does (soft-pause + mouse visible)
            Input.MouseMode = Input.MouseModeEnum.Visible;
            await WaitFrames(10);
            await Capture("00_pause");
            _warnings.Add($"PAUSE: canRestart(solo)={g.CanRestartRun()} combos={p.Fin.Count} inGameOptions={g.InGameOptions}");

            g.OpenInGameOptions();               // the exact main-menu options page, transparent over the paused game
            await WaitFrames(24);                // Lobby rebuilds the panel + renders
            await Capture("01_options");
            _warnings.Add($"OPTIONS: inGameOptions={g.InGameOptions} lobbyVisible={(g.LobbyUi != null && g.LobbyUi.Visible)}");

            g.CloseInGameOptions();              // Back → pause menu
            await WaitFrames(16);
            await Capture("02_back_to_pause");
            _warnings.Add($"BACK: inGameOptions={g.InGameOptions} lobbyVisible={(g.LobbyUi != null && g.LobbyUi.Visible)} state={g.State}");

            g.State = GameState.Playing;         // resume
            Input.MouseMode = Input.MouseModeEnum.Captured;
        }

        private static void Tap(Key k)
        {
            Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = k, Pressed = true });
            Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = k, Pressed = false });
        }

        // Collider editor: enter, eyeball the model lineup, spawn a collider via the palette, move it, save to res://data, then
        // verify the template round-trips through ColliderTemplates.Emit into real engine colliders.
        private async Task ColliderEditorScenario()
        {
            var g = Game.I; var p = g?.Player; if (p == null) { _errors.Add("no Player"); return; }
            var ed = g.ColEditor; if (ed == null) { _errors.Add("no ColEditor"); return; }
            g.NoSpawn = true; g.ClearEnemies();
            // BACK UP any real authored data before the test (the test writes/deletes colliders.json) — restored at the end so we NEVER destroy the user's work
            const string cpath = "res://data/colliders.json";
            string backup = null;
            if (Godot.FileAccess.FileExists(cpath)) { using var bf = Godot.FileAccess.Open(cpath, Godot.FileAccess.ModeFlags.Read); backup = bf?.GetAsText(); }
            if (Godot.FileAccess.FileExists(cpath)) Godot.DirAccess.RemoveAbsolute(cpath);   // clean slate for the test
            ColliderTemplates.Load();
            await WaitFrames(30);

            ed.Enter();
            await WaitFrames(20); await Capture("00_lineup");
            _warnings.Add($"ENTER: active={ed.Active} state={g.State} colliders={ed.SelectedCount}");

            Tap(Key.M);            // open palette
            await WaitFrames(4); await Capture("01_palette");
            _warnings.Add($"PALETTE: open={ed.PaletteOpen} shape={ed.PalShape} kind={ed.PalKind}");

            Tap(Key.Right);        // kind → walk(blue)
            Tap(Key.Enter);        // spawn on the nearest model
            await WaitFrames(6); await Capture("02_spawned");
            _warnings.Add($"SPAWN: colliders={ed.SelectedCount} sel={ed.SelectedIndex} paletteOpen={ed.PaletteOpen}");

            for (int i = 0; i < 4; i++) { Tap(Key.Up); await WaitFrames(1); }     // MOVE mode (default): arrows move -Z
            for (int i = 0; i < 3; i++) { Tap(Key.E); await WaitFrames(1); }      // Q/E move +Y
            _warnings.Add($"AFTER MOVE: {ed.SelInfo().Replace('\n', ' ')}");
            Tap(Key.T);                                                          // SCALE mode
            for (int i = 0; i < 2; i++) { Tap(Key.E); await WaitFrames(1); }      // scale +Y
            for (int i = 0; i < 2; i++) { Tap(Key.Right); await WaitFrames(1); }  // scale +X
            await WaitFrames(4); await Capture("03_moved");
            _warnings.Add($"AFTER SCALE (mode={ed.ModeName}): {ed.SelInfo().Replace('\n', ' ')}");

            Tap(Key.G);            // back to MOVE mode
            Tap(Key.C);            // cycle color → ramp(green)
            Tap(Key.V);            // cycle shape → cyl
            await WaitFrames(4); await Capture("04_recolored");

            Tap(Key.K);            // SAVE to res://data/colliders.json
            await WaitFrames(6);
            bool fileOk = Godot.FileAccess.FileExists("res://data/colliders.json");
            _warnings.Add($"SAVE: fileExists={fileOk}  status='{ed.Status}'");

            // round-trip: reload the template and emit it into fresh engine lists to prove the spawn path consumes it
            ColliderTemplates.Load();
            var bl = new System.Collections.Generic.List<Blocker>();
            var dk = new System.Collections.Generic.List<Deck>();
            var rm = new System.Collections.Generic.List<Ramp>();
            string near = ed.NearestModelName();
            bool emitted = ColliderTemplates.Emit(near, new Vector3(0, 0, 0), 0f, 12f, 0f, bl, dk, rm);
            _warnings.Add($"EMIT {near}: template={emitted} → blockers={bl.Count} decks={dk.Count} ramps={rm.Count}");

            // ROTATION ALIGNMENT: emit the same template at yaw 0 and yaw 0.9; every collider must be the yaw-0 one rotated by the
            // model's Basis (i.e. it tracks the rotated model). Catches the sign/handedness bug where colliders swung the wrong way.
            var d0 = new System.Collections.Generic.List<Deck>(); var b0 = new System.Collections.Generic.List<Blocker>(); var r0 = new System.Collections.Generic.List<Ramp>();
            var d1 = new System.Collections.Generic.List<Deck>(); var b1 = new System.Collections.Generic.List<Blocker>(); var r1 = new System.Collections.Generic.List<Ramp>();
            ColliderTemplates.Emit(near, Vector3.Zero, 0f, 12f, 0f, b0, d0, r0);
            ColliderTemplates.Emit(near, Vector3.Zero, 0f, 12f, 0.9f, b1, d1, r1);
            var rot = Basis.FromEuler(new Vector3(0, 0.9f, 0));
            float maxErr = 0f;
            for (int i = 0; i < d0.Count && i < d1.Count; i++) maxErr = Mathf.Max(maxErr, (rot * d0[i].Center - d1[i].Center).Length());
            for (int i = 0; i < b0.Count && i < b1.Count; i++) maxErr = Mathf.Max(maxErr, (rot * b0[i].Pos - b1[i].Pos).Length());
            _warnings.Add($"ROTATE ALIGN: maxErr={maxErr:0.0000} (expect ~0 — colliders track the rotated model)");
            if (maxErr > 0.01f) _errors.Add($"rotation misalignment: colliders don't track model rotation (err {maxErr:0.00})");

            ed.Exit();
            await WaitFrames(10);
            _warnings.Add($"EXIT: active={ed.Active} state={g.State}");
            // RESTORE the user's real authored data (or clear if there was none) — the test must never leave its own data behind
            if (Godot.FileAccess.FileExists(cpath)) Godot.DirAccess.RemoveAbsolute(cpath);
            if (backup != null) { using var wf = Godot.FileAccess.Open(cpath, Godot.FileAccess.ModeFlags.Write); wf?.StoreString(backup); }
            ColliderTemplates.Load();
            _warnings.Add($"RESTORE: backup={(backup != null ? "restored" : "none")}");
        }

        // A/B the painterly shaders on the SAME framing: ours at Full, then the off-the-shelf Acerola/Calrsdr port, then back to
        // ours — after changing the setting to Half mid-comparison, to prove switching back HONOURS the current Off/Half/Full setting.
        // Validate the new structures behave: (1) walk UP the climbable keep's stairs and confirm we reach the roof grounded,
        // (2) sample REAL fps in a dense prop/structure cluster (no capture during the sample → not capture-bound), (3) capture
        // a real pedestal+effigy and a ritual circle to eyeball floating / clipping. Findings go to _warnings (→ result.json).
        private async Task StructureStress()
        {
            var g = Game.I; var p = g?.Player; if (p == null) { _errors.Add("no Player"); return; }
            g.NoSpawn = true; g.ClearEnemies();
            await WaitFrames(45);

            // ---- (1) climbable keep walk-up — DENSE captures so the climb path / any clipping is visible frame-by-frame ----
            p.Rotation = new Vector3(0, 0, 0);                                   // face -Z (forward), stairs are on +Z of the keep
            Vector3 anchor = p.GlobalPosition;
            Vector3 keepCenter = new Vector3(anchor.X, 0, anchor.Z - 28f);
            keepCenter.Y = g.SurfaceHeight(keepCenter, 1e9f);
            Vector3 geo = g.DebugSpawnClimbableKeep(keepCenter);                 // (roofY, xStairWorld, stairFarZ) — stairs on the +Z face
            float roofY = geo.X, xStair = geo.Y, stairFarZ = geo.Z;
            await WaitFrames(25);                                               // let the deck/ramp re-flush into collision
            Vector3 startPos = new Vector3(xStair, 0, stairFarZ + 5f);
            startPos.Y = g.SurfaceHeight(startPos, 1e9f) + 1.2f;
            p.GlobalPosition = startPos; p.Rotation = new Vector3(0, 0, 0);      // face -Z, toward the keep + its +Z staircase
            await WaitFrames(12);
            await Capture("00_climb");
            float baseY = p.GlobalPosition.Y, peakY = baseY; bool everRoof = false;
            Input.ActionPress("move_forward");
            for (int i = 1; i <= 12; i++)
            {
                await WaitFrames(9);
                float y = p.GlobalPosition.Y; peakY = Mathf.Max(peakY, y);
                if (y > roofY - 1.0f && p.Grounded) everRoof = true;
                _warnings.Add($"climb {i:00}: z={p.GlobalPosition.Z - keepCenter.Z:0.0} y={y:0.00} grounded={p.Grounded}");
                await Capture($"{i:00}_climb");
            }
            Input.ActionRelease("move_forward");
            _warnings.Add($"KEEP: baseY={baseY:0.00} peakY={peakY:0.00} roofY={roofY:0.00} finalY={p.GlobalPosition.Y:0.00} REACHED_ROOF={everRoof}");

            // ---- collision-bounds VIZ — draw the keep's deck (blue) / ramp (green) / rail (red) vs the visual model ----
            g.ColDebug?.Toggle();
            p.GlobalPosition = new Vector3(xStair, roofY + 3f, stairFarZ + 13f); p.Rotation = new Vector3(0, 0, 0);
            await WaitFrames(18); await Capture("collision_keep");
            g.ColDebug?.Toggle();

            // ---- (1b) THIRD-PERSON grounding checks — teleport the witch onto the roof + a mid-stair and let her settle, so we
            //      SEE the character's feet vs the surface (drive-climb doesn't reground reliably right after the tp3 toggle) ----
            p.ToggleThirdPersonPlay();
            await WaitFrames(45);                                               // tp3 authored-witch grounding is deferred
            p.GlobalPosition = new Vector3(keepCenter.X, roofY + 1.0f, keepCenter.Z); p.Rotation = new Vector3(0, 0, 0);
            await WaitFrames(30); await Capture("tp3_roof");
            _warnings.Add($"TP3 roof: roofY={roofY:0.00} restY={p.GlobalPosition.Y:0.00} grounded={p.Grounded}");
            float midZ = (keepCenter.Z + stairFarZ) * 0.5f;
            float g2 = g.SurfaceHeight(new Vector3(xStair, 0, midZ), 1e9f);
            float midY = Mathf.Lerp(roofY, g2, 0.5f);
            p.GlobalPosition = new Vector3(xStair, midY + 1.0f, midZ); p.Rotation = new Vector3(0, 0, 0);
            await WaitFrames(30); await Capture("tp3_stair");
            _warnings.Add($"TP3 stair: approxRampY={midY:0.00} restY={p.GlobalPosition.Y:0.00}");
            p.ToggleThirdPersonPlay();                                          // back to FP for the rest

            // ---- (2) EDGE-OF-MAP test — walk straight at the boundary and see where you stop vs where the cliff rock is ----
            g.ClearEnemies();
            float R = World.WorldRadius;
            Vector3 estart = new Vector3(R - 22f, 0, 0);
            estart.Y = g.SurfaceHeight(estart, 1e9f) + 1.5f;
            p.GlobalPosition = estart;
            p.Rotation = new Vector3(0, -Mathf.Pi / 2f, 0);                      // face +X (outward toward the rim)
            await WaitFrames(14);
            _warnings.Add($"EDGE start radius={Mathf.Sqrt(p.GlobalPosition.X * p.GlobalPosition.X + p.GlobalPosition.Z * p.GlobalPosition.Z):0.0} (WorldRadius={R:0})");
            await Capture("e0_edge");
            Input.ActionPress("move_forward");
            float lastRad = 0f;
            for (int i = 1; i <= 8; i++)
            {
                await WaitFrames(16);
                lastRad = Mathf.Sqrt(p.GlobalPosition.X * p.GlobalPosition.X + p.GlobalPosition.Z * p.GlobalPosition.Z);
                _warnings.Add($"edge {i}: radius={lastRad:0.0}");
                await Capture($"e{i}_edge");
            }
            Input.ActionRelease("move_forward");
            _warnings.Add($"EDGE stopped at radius={lastRad:0.0} (WorldRadius={R:0}, expected clamp ~{R + 10f:0})");

            // ---- (3) real pedestal+effigy & ritual grounding (spawned by PopulateMap) ----
            var ped = NearestPedestal(p.GlobalPosition);
            if (ped != null)
            {
                Vector3 pv = ped.GlobalPosition;
                g.ColDebug?.Toggle();                                            // collision viz on
                Vector3 eye = pv + new Vector3(0, 3f, 8f);
                p.GlobalPosition = new Vector3(eye.X, g.SurfaceHeight(eye, 1e9f) + 3f, eye.Z); p.Rotation = new Vector3(0, 0, 0);
                await WaitFrames(14); await Capture("10_pedestal_collision");
                p.ToggleThirdPersonPlay(); await WaitFrames(35);                 // stand ON it in tp3
                p.GlobalPosition = new Vector3(pv.X, pv.Y + Pedestal.TopH + 1f, pv.Z); p.Rotation = new Vector3(0, 0, 0);
                await WaitFrames(30); await Capture("10b_pedestal_tp3");
                _warnings.Add($"PEDESTAL top y={pv.Y + Pedestal.TopH:0.00} restY={p.GlobalPosition.Y:0.00} grounded={p.Grounded}");
                p.ToggleThirdPersonPlay(); g.ColDebug?.Toggle();
            }
            else _warnings.Add("no pedestal found");
            var rit = NearestRitual(p.GlobalPosition);
            if (rit != null)
            {
                Vector3 rv = rit.GlobalPosition;
                p.GlobalPosition = new Vector3(rv.X, g.SurfaceHeight(rv + new Vector3(0, 0, 12f), 1e9f) + 3f, rv.Z + 12f);
                p.Rotation = new Vector3(0, 0, 0);
                await WaitFrames(12); await Capture("11_ritual_circle");
            }
            else _warnings.Add("no ritual found");
        }

        // Average/min fps over `frames` plain frames (no capture in between → representative render cost on this machine).
        private async Task<string> SampleFps(int frames)
        {
            float lo = 1e9f, sum = 0f; int n = 0;
            for (int i = 0; i < frames; i++) { await NextFrame(); _frame++; float f = (float)Engine.GetFramesPerSecond(); if (f > 0) { lo = Mathf.Min(lo, f); sum += f; n++; } }
            return $"min={lo:0} avg={(n > 0 ? sum / n : 0):0} (windowed; vsync may cap)";
        }

        private Pedestal NearestPedestal(Vector3 from)
        {
            Pedestal best = null; float bd = 1e18f;
            foreach (var pd in Game.I.Pedestals)
                if (pd != null && GodotObject.IsInstanceValid(pd)) { float d = (pd.GlobalPosition - from).LengthSquared(); if (d < bd) { bd = d; best = pd; } }
            return best;
        }
        private RitualCircle NearestRitual(Vector3 from)
        {
            RitualCircle best = null; float bd = 1e18f;
            foreach (var r in Game.I.Rituals)
                if (r != null && GodotObject.IsInstanceValid(r)) { float d = (r.GlobalPosition - from).LengthSquared(); if (d < bd) { bd = d; best = r; } }
            return best;
        }

        // Line up every structure, turn the collision viz ON, and capture each so its collision (red solid / blue deck / green
        // ramp) can be audited against the visual model.
        private async Task CollisionAudit()
        {
            var g = Game.I; var p = g?.Player; if (p == null) { _errors.Add("no Player"); return; }
            g.NoSpawn = true; g.ClearEnemies();
            await WaitFrames(30);
            Vector3 fwd = -p.GlobalTransform.Basis.Z; fwd.Y = 0f; fwd = fwd.Normalized();
            Vector3 center = p.GlobalPosition + fwd * 45f;
            center.Y = g.SurfaceHeight(center, 1e9f);
            var pts = g.DebugStructureAudit(center);
            if (pts.Count == 0) { _errors.Add("no chunk for audit"); return; }
            await WaitFrames(20);
            g.ColDebug?.Toggle();
            await WaitFrames(10);
            string[] names = { "cottage_a", "cottage_b", "fort", "ruin", "staircase", "altar", "well", "gravestones", "platform", "keep" };
            for (int i = 0; i < pts.Count; i++)
            {
                Vector3 s = pts[i];
                float h = Mathf.Max(2f, s.Y - g.SurfaceHeight(s, 1e9f));
                float back = Mathf.Max(11f, h * 1.5f);
                Vector3 eye = new Vector3(s.X, 0f, s.Z + back);
                eye.Y = g.SurfaceHeight(eye, 1e9f) + Mathf.Max(2.5f, h * 0.45f);
                p.GlobalPosition = eye; p.Rotation = new Vector3(0, 0, 0);
                await WaitFrames(14);
                await Capture($"col_{i:00}_{names[Mathf.Min(i, names.Length - 1)]}");
            }
            // walk-test the standalone staircase (index 4): approach from +Z and walk -Z up it, measure the climb
            if (pts.Count > 4)
            {
                Vector3 st = pts[4]; float sg = g.SurfaceHeight(st, 1e9f);
                p.GlobalPosition = new Vector3(st.X, sg + 1.2f, st.Z + 11f); p.Rotation = new Vector3(0, 0, 0);
                await WaitFrames(14);
                float sbY = p.GlobalPosition.Y, peak = sbY;
                Input.ActionPress("move_forward");
                for (int k = 0; k < 9; k++) { await WaitFrames(13); peak = Mathf.Max(peak, p.GlobalPosition.Y); }
                Input.ActionRelease("move_forward");
                await WaitFrames(10);
                _warnings.Add($"STAIRCASE walk: baseY={sbY:0.00} peakY={peak:0.00} climbed={peak - sbY:0.00} finalGrounded={p.Grounded}");
                await Capture("col_stairwalk");
            }
            g.ColDebug?.Toggle();
        }

        private Enemy FindGoblin()
        {
            var es = Game.I?.Enemies; if (es == null) return null;
            for (int i = es.Count - 1; i >= 0; i--)
                if (es[i] != null && GodotObject.IsInstanceValid(es[i]) && es[i].IsAuthoredGoblin) return es[i];
            return null;
        }

        // Press an action, hold `frames` (speed builds + real travel), capture, then release + ease before the next direction.
        private async Task Hold(string action, int frames, string checkpoint)
        {
            Input.ActionPress(action);
            await WaitFrames(frames);
            await Capture(checkpoint);
            Input.ActionRelease(action);
            await WaitFrames(20);
        }

        // ------------------------------------------------------------------ scenario --------------------------------------
        private async Task WitchCastJump()
        {
            var p = Game.I?.Player;
            if (p == null) { _errors.Add("no Player"); return; }

            await WaitFrames(30);                     // let the world + first render settle
            p.ToggleThirdPersonPlay();                // over-shoulder authored-witch view (the visual subject)
            await WaitFrames(45);                     // grounding is deferred ~0.15s after entering tp3 — wait past it, then settle
            await Capture("00_idle");

            // (1) anticipation: hold the chargeable secondary until it's clearly built
            Input.ActionPress("charge");
            if (!await WaitUntil(() => p.Charging && p.ChargeAmt >= 0.6f, 360))
                _warnings.Add("charge never reached 0.6 (captured whatever built)");
            await Capture("01_charge");

            // (2) release: let go and grab the extension/impact frame
            Input.ActionRelease("charge");
            await WaitFrames(3);
            await Capture("02_release");

            await WaitFrames(45);                     // recover to idle before jumping

            // (3) jump apex: press+release jump, capture when airborne and vertical velocity has topped out
            Input.ActionPress("jump");
            await WaitFrames(2);
            Input.ActionRelease("jump");
            if (!await WaitUntil(() => !p.Grounded && p.VyDebug <= 0.5f, 240))
                _warnings.Add("never detected a jump apex (airborne & vy<=0.5)");
            await Capture("03_jump_apex");

            // (4) recovery: back on the ground, settled
            if (!await WaitUntil(() => p.Grounded, 360))
                _warnings.Add("never returned to grounded");
            await WaitFrames(20);
            await Capture("04_recovery");
        }

        // ------------------------------------------------------------------ helpers ----------------------------------------
        private SignalAwaiter NextFrame() => ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        private async Task WaitFrames(int n)
        {
            for (int i = 0; i < n; i++) { await NextFrame(); _frame++; if (Bailed()) return; }
        }

        // Await until `cond` is true or `maxFrames` elapse. Returns whether the condition was met.
        private async Task<bool> WaitUntil(Func<bool> cond, int maxFrames)
        {
            for (int i = 0; i < maxFrames; i++)
            {
                if (Bailed()) return false;
                try { if (cond()) return true; } catch { /* actor may be mid-transition */ }
                await NextFrame(); _frame++;
            }
            return false;
        }

        private bool Bailed()
        {
            if (_frame <= GlobalTimeoutFrames) return false;
            if (!_errors.Contains("global frame timeout")) _errors.Add("global frame timeout");
            return true;
        }

        private async Task Capture(string checkpoint)
        {
            // Snapshot the actor state BEFORE the screenshot — GetImage() forces a GPU→CPU readback that stalls the frame, and a
            // stalled frame perturbs time-based values (e.g. the locomotion blend snaps to that frame's instantaneous speed). We
            // want the state of the real, sustained frame, not the readback-stalled one.
            var actors = AiObservable.CollectActors(GetTree());

            string png = Path.Combine(CapturesDir, $"{_scenario}_{checkpoint}.png");
            var res = await AiCaptureService.Capture(this, png);
            if (res.Ok)
            {
                _capturesWritten++;
                var latest = await AiCaptureService.Capture(this, Path.Combine(CapturesDir, "latest.png"));
                if (!latest.Ok) _warnings.Add($"latest.png: {latest.Error}");
            }
            else _errors.Add($"capture '{checkpoint}': {res.Error}");

            WriteState(checkpoint, res.MeanLuma, actors);
        }

        private void WriteState(string checkpoint, float meanLuma, Godot.Collections.Dictionary actors)
        {
            var state = new Godot.Collections.Dictionary
            {
                { "scenario", _scenario },
                { "scene", GetTree()?.CurrentScene?.SceneFilePath ?? "" },
                { "capture", checkpoint },
                { "timestamp_seconds", (Time.GetTicksMsec() - _startMs) / 1000.0 },
                { "frame", _frame },
                { "fps", Engine.GetFramesPerSecond() },
                { "mean_luma", meanLuma },
                { "errors", _errors.Duplicate() },
                { "actors", actors },
            };
            string json = Json.Stringify(state, "  ");
            TryWrite(Path.Combine(CapturesDir, $"{_scenario}_{checkpoint}.state.json"), json);
            TryWrite(Path.Combine(ArtifactsDir, "latest_state.json"), json);   // convenient single-file mirror
        }

        private void Finish()
        {
            bool passed = _errors.Count == 0 && _capturesWritten > 0;
            var result = new Godot.Collections.Dictionary
            {
                { "scenario", _scenario },
                { "status", passed ? "passed" : "failed" },
                { "captures_written", _capturesWritten },
                { "errors", _errors },
                { "warnings", _warnings },
            };
            TryWrite(Path.Combine(ArtifactsDir, "result.json"), Json.Stringify(result, "  "));
            GD.Print($"[AiTestRunner] scenario '{_scenario}' {(passed ? "PASSED" : "FAILED")} — {_capturesWritten} captures, {_errors.Count} errors");
            GetTree().Quit(passed ? 0 : 1);
        }

        private void TryWrite(string absPath, string contents)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(absPath));
                File.WriteAllText(absPath, contents);
            }
            catch (Exception e) { _errors.Add($"write '{Path.GetFileName(absPath)}': {e.Message}"); }
        }

        // Every simulated press MUST be released — including on failure — so a killed run leaves no stuck inputs.
        private void ReleaseAllInputs()
        {
            foreach (var a in new[] { "charge", "cast", "jump", "move_forward", "move_back", "move_left", "move_right" })
                if (InputMap.HasAction(a)) Input.ActionRelease(a);
        }
    }
}
