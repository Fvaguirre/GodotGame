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
            { "zombie_showcase", 1 },   // authored zombie-goblin GLB: walk + the TWO-arm chop (both hands at once)
            { "ogre_showcase", 1 },     // authored buffoon-ogre GLB (Orc kind): walk + single-arm slash (like the plain goblin)
            { "taker_showcase", 1 },    // authored taker GLB (Taker kind): walk/run/grab-reach/climb/fall/wall-slam/stand-up action set
            { "biped_fling", 1 },       // REAL runtime paths: fling a biped through the arc→land→get-up, and peel a climbing one off a wall
            { "graft_retarget", 1 },    // force the grafted taker action clips onto the SMALL goblin + swarmer rigs — verify no mesh deform (retargeted translations)
            { "perf_haunt", 3 },        // PERF AUDIT: haunt + med/max enemies + firing spells, uncapped fps, sweep each graphics setting to find what eats frames
            { "conduit_check", 7 },     // verify the new cross-witch conduit state: MarkConduit → ArcaneMarked, then self-expires
            { "flyer_showcase", 1 },    // flyer/diver (Mosquito) — new translucent veined wing membranes + antennae + segmented body
            { "floating_avatar", 0 },   // PROTOTYPE: disembodied floating hat+eyes+hands+boots witch avatar (front lineup / walking / behind / FP)
            { "avatar_parts", 0 },      // preview the raw Meshy-authored avatar pieces (hat/hand/robe) one at a time to judge shape/orientation/quality
            { "fp_hands", 0 },          // FIRST-PERSON viewmodel: the unified authored glove hands (same asset as tp3) — idle + charge
            { "nerf_shrine", 0 },       // NERFER: single random shrine, escalating soul toll, and the Summoning's stand-in-the-circle countdown gate
            { "crimson_rite", 0 },      // NERFER Sacrifice: blood sigils → pentagram draws over the boss → horde cut down + spawn silence
            { "hollow_man", 1 },        // THE HOLLOW MOON: authored GLB + every attack's wind-up clip/gesture/hand-glow + the 20%-HP charge
            { "hollow_phase2", 1 },     // …his PHASE 2: fake death -> laugh -> rise -> arcane aura, triple charge, vortex-to-stomp
            { "withered_caster", 1 },   // THE WITHERED KING body on caster/stunner/healer/empowerer + the mage cast grafted onto the ogre-bodied bolt throwers
            { "wild_swarm", 3 },        // Verdant's Wild Swarm ult — the stampede critters must BE her tree-ents (baked ent body), not brown capsules
            { "perk_audit", 0 },        // every perk node + hidden route on all 9 witches: applies cleanly AND actually changes a stat (no dead nodes)
            { "card_conditions", 0 },   // the situational card bonuses: each one is OFF until its condition holds, and ON when it does
            { "card_pool_audit", 0 },   // every witch must actually HAVE her affinity cards + the Coven ladder — no witch left thin
            { "perf_cpu", 0 },          // CPU-vs-GPU split + per-enemy main-thread cost. Answers "are we CPU-bound?" — which perf_haunt can't
            { "drawcall_audit", 0 },    // WHERE do the ~1200 empty-world draw calls come from? Inventory by subsystem, then hide each and measure the real delta
            { "haunt_storm", 0 },       // Haunt lightning: purple/red telegraph reads, the bolt lands, and it hurts+stuns foes AND the witch
            { "haunt_vfx", 0 },         // the Haunt's own dressing up close: authored leaves, soft wisps (no cards), the phantoms' silhouette
            { "forsaken_doom", 6 },     // FORSAKEN REWORK: the Doom bank/fuse/execute loop, Focus across a target switch, charge-to-detonate, Danse Macabre + Rout, and the walking corpse
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
                case "zombie_showcase": await ZombieShowcase(); break;
                case "ogre_showcase": await OgreShowcase(); break;
                case "taker_showcase": await TakerShowcase(); break;
                case "biped_fling": await BipedFling(); break;
                case "graft_retarget": await GraftRetarget(); break;
                case "perf_haunt": await PerfHaunt(); break;
                case "conduit_check": await ConduitCheck(); break;
                case "flyer_showcase": await FlyerShowcase(); break;
                case "floating_avatar": await FloatingAvatarShowcase(); break;
                case "avatar_parts": await AvatarParts(); break;
                case "fp_hands": await FpHands(); break;
                case "nerf_shrine": await NerfShrineScenario(); break;
                case "crimson_rite": await CrimsonRiteScenario(); break;
                case "hollow_man": await HollowMoonScenario(); break;
                case "hollow_phase2": await HollowPhase2Scenario(); break;
                case "withered_caster": await WitheredCasterScenario(); break;
                case "wild_swarm": await WildSwarmScenario(); break;
                case "perk_audit": await PerkAudit(); break;
                case "card_conditions": await CardConditions(); break;
                case "card_pool_audit": await CardPoolAudit(); break;
                case "perf_cpu": await PerfCpu(); break;
                case "drawcall_audit": await DrawCallAudit(); break;
                case "haunt_storm": await HauntStorm(); break;
                case "haunt_vfx": await HauntVfx(); break;
                case "forsaken_doom": await ForsakenDoom(); break;
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

        // Authored zombie-goblin: spawn a swarmer (→ CreatureKind.Zombie → the rigged zombie GLB), pin it, capture idle / walk /
        // the TWO-arm chop (both hands swing together — the differentiator from the goblin's single random arm).
        private async Task ZombieShowcase()
        {
            var p = Game.I?.Player; if (p == null) { _errors.Add("no Player"); return; }
            Game.I.NoSpawn = true; Game.I.ClearEnemies();
            await WaitFrames(20);
            Vector3 fwd = -p.GlobalTransform.Basis.Z; fwd.Y = 0f; fwd = fwd.Normalized();
            Vector3 pin = p.GlobalPosition + fwd * 7f;
            Game.I.SpawnEnemyForTest("swarmer", pin);
            await WaitFrames(40);
            var z = FindGoblin();   // returns any authored biped — the swarmer is the only enemy here
            if (z == null) { _errors.Add("no authored zombie spawned"); return; }

            z.DebugWalk(0f); await PinHold(z, pin, 20); await Capture("00_idle");
            z.DebugWalk(1f); await PinHold(z, pin, 30); await Capture("01_walk");
            z.DebugWalk(0f); z.DebugSlash(true);              // two-arm chop (bothArms ignores the side)
            await PinHold(z, pin, 3);  await Capture("02_chop_windup");
            await PinHold(z, pin, 6);  await Capture("03_chop_impact");
            await PinHold(z, pin, 10); await Capture("04_after");
        }

        // Authored buffoon-ogre: spawn a brute (→ CreatureKind.Orc → the rigged ogre GLB), pin it further back (it's a big enemy),
        // capture idle / walk / the SINGLE-arm slash (same procedural swing as the plain goblin — the differentiator is just scale).
        private async Task OgreShowcase()
        {
            var p = Game.I?.Player; if (p == null) { _errors.Add("no Player"); return; }
            Game.I.NoSpawn = true; Game.I.ClearEnemies();
            await WaitFrames(20);
            Vector3 fwd = -p.GlobalTransform.Basis.Z; fwd.Y = 0f; fwd = fwd.Normalized();
            Vector3 pin = p.GlobalPosition + fwd * 11f;      // step back — the ogre is much larger than a goblin
            Game.I.SpawnEnemyForTest("brute", pin);
            await WaitFrames(40);
            var o = FindGoblin();   // any authored biped — the brute (ogre) is the only enemy here
            if (o == null) { _errors.Add("no authored ogre spawned"); return; }

            o.DebugWalk(0f); await PinHold(o, pin, 20); await Capture("00_idle");
            o.DebugWalk(1f); await PinHold(o, pin, 30); await Capture("01_walk");
            o.DebugWalk(0f); o.DebugSlash(true);             // single-arm slash (left)
            await PinHold(o, pin, 3);  await Capture("02_slash_left");
            await PinHold(o, pin, 12);
            o.DebugSlash(false);                             // slash (right)
            await PinHold(o, pin, 3);  await Capture("03_slash_right");
            await PinHold(o, pin, 10); await Capture("04_after");
        }

        // Authored taker: spawn a taker (→ CreatureKind.Taker → the taker GLB with the full merged action set), then FORCE each
        // action clip via the harness debug-hold and capture it: walk / run / grab-arms reach / climb / airborne fall / wall-slam
        // → stand-up. Verifies the 10-clip merge resolved AND each state reads on the model. Logs the resolved clip count.
        private async Task TakerShowcase()
        {
            var p = Game.I?.Player; if (p == null) { _errors.Add("no Player"); return; }
            Game.I.NoSpawn = true; Game.I.ClearEnemies();
            await WaitFrames(20);
            Vector3 fwd = -p.GlobalTransform.Basis.Z; fwd.Y = 0f; fwd = fwd.Normalized();
            Vector3 pin = p.GlobalPosition + fwd * 11f;      // step back — the taker is a big enemy
            Game.I.SpawnEnemyForTest("taker", pin);
            await WaitFrames(40);
            var t = FindGoblin();   // any authored biped — the taker is the only enemy here
            if (t == null) { _errors.Add("no authored taker spawned"); return; }
            _warnings.Add($"TAKER CLIPS resolved={t.DebugClipCount} (expect 10: walk/run/fall1/fall3/falldown/climb/climbfall/climbfall4/standup2/standup4)");
            if (t.DebugClipCount < 10) _errors.Add($"taker only resolved {t.DebugClipCount}/10 action clips — clip merge/graft failed");

            t.DebugBiped("walk");  await PinHold(t, pin, 24); await Capture("00_walk");
            t.DebugBiped("run");   await PinHold(t, pin, 24); await Capture("01_run");
            t.DebugBiped("reach"); await PinHold(t, pin, 30); await Capture("02_grab_reach");   // both arms forward telegraph
            t.DebugBiped("climb"); await PinHold(t, pin, 30); await Capture("03_climb");
            t.DebugBipedStart("fall"); await PinHold(t, pin, 20); await Capture("04_fall_air");
            t.DebugBipedStart("walldown"); await PinHold(t, pin, 10); await Capture("05_wall_slam");
            t.DebugBipedStart("standup");  await PinHold(t, pin, 6);  await Capture("06_standup_start");
            await PinHold(t, pin, 30); await Capture("07_standup_end");
            // one-arm melee punch (same slash the goblins use)
            t.DebugBiped("walk"); await PinHold(t, pin, 4);
            t.DebugSlash(true);
            await PinHold(t, pin, 4); await Capture("08_punch");

            // hurt flinch variants — trigger then capture near the impulse peak (it decays in ~0.25s)
            t.DebugWince(0); await PinHold(t, pin, 2); await Capture("09_wince_back");
            await PinHold(t, pin, 18);
            t.DebugWince(1); await PinHold(t, pin, 2); await Capture("10_wince_left");
            await PinHold(t, pin, 18);
            t.DebugWince(3); await PinHold(t, pin, 2); await Capture("11_wince_hunch");
        }

        // REAL runtime paths (not forced clips): (A) fling a biped as a wind ult would — arc → land → the AuthBiped get-up clip —
        // and (B) peel a CLIMBING biped off a wall (crit/knock) → PeelOffWall → Fling(fromClimb) → climb-slip fall → get-up. Uses
        // the durable taker (260 HP) so the repeated fall damage doesn't kill it; the Fling/land/get-up path is shared by every biped.
        private async Task BipedFling()
        {
            var p = Game.I?.Player; if (p == null) { _errors.Add("no Player"); return; }
            Game.I.NoSpawn = true; Game.I.ClearEnemies();
            await WaitFrames(20);
            Vector3 fwd = -p.GlobalTransform.Basis.Z; fwd.Y = 0f; fwd = fwd.Normalized();
            Vector3 spot = p.GlobalPosition + fwd * 12f; spot.Y = Game.I.SurfaceHeight(spot, 1e9f);
            Game.I.SpawnEnemyForTest("taker", spot);
            await WaitFrames(40);
            var e = FindGoblin();
            if (e == null) { _errors.Add("no biped spawned"); return; }

            async Task WaitWhile(System.Func<bool> cond, int cap) { int g = 0; while (e != null && GodotObject.IsInstanceValid(e) && cond() && g++ < cap) { await NextFrame(); _frame++; } }

            // ---- (A) real fling → arc → land → get-up ----
            e.DebugFling(-fwd * 3.5f + Vector3.Up * 11f);   // up + toward the camera so it stays framed
            _warnings.Add($"FLING: thrown={e.DebugThrown}");
            await WaitFrames(8);  await Capture("00_fling_rise");
            await WaitFrames(10); await Capture("01_fling_apex");
            await WaitFrames(12); await Capture("02_fling_descend");
            await WaitWhile(() => e.DebugThrown, 200);
            _warnings.Add($"LANDED: thrown={(e != null && e.DebugThrown)} gettingUp={(e != null && e.DebugGettingUp)}");
            await WaitFrames(3);  await Capture("03_land_getup");
            await WaitFrames(22); await Capture("04_getup_mid");
            await WaitWhile(() => e.DebugGettingUp, 200);
            _warnings.Add($"RECOVERED: gettingUp={(e != null && e.DebugGettingUp)}");
            await WaitFrames(10); await Capture("05_recovered");

            // ---- (B) peel a climbing biped off a wall → climb-slip fall → get-up ----
            if (e != null && GodotObject.IsInstanceValid(e))
            {
                e.GlobalPosition = spot; await WaitFrames(6);
                e.DebugClimbPeel(-fwd * 3f);
                _warnings.Add($"CLIMB-PEEL: thrown={e.DebugThrown}");
                await WaitFrames(8);  await Capture("06_peel_slip");
                await WaitFrames(12); await Capture("07_peel_fall");
                await WaitWhile(() => e.DebugThrown, 200);
                await WaitFrames(3);  await Capture("08_peel_land_getup");
                await WaitWhile(() => e.DebugGettingUp, 200);
                await WaitFrames(10); await Capture("09_peel_recovered");
                _warnings.Add($"PEEL DONE: alive={(e != null && GodotObject.IsInstanceValid(e) && !e.Dead)}");
            }
        }

        // Hold an enemy pinned at (pos.X,pos.Z) for `frames` (overriding its AI drift), keeping Y so it stays grounded.
        // THE HOLLOW MOON. Spawns the real boss (authored GLB + his 13-clip library), then drives EVERY attack through the real
        // BeginBossCharge → telegraph → FireBossPattern path and captures each wind-up at its readable moment plus the release.
        // Also exercises the new 20%-HP head-down charge end to end. Captures are paced off ChargeFrac (the wind-up's own
        // progress), never frame counts — this scene swings 40–110fps, so fixed waits land in the wrong part of a wind-up.
        private async Task HollowMoonScenario()
        {
            var g = Game.I; var p = g?.Player; if (p == null) { _errors.Add("no Player"); return; }
            g.NoSpawn = true; g.ClearEnemies();
            await WaitFrames(20);
            Vector3 fwd = -p.GlobalTransform.Basis.Z; fwd.Y = 0f; fwd = fwd.Normalized();
            // He's ~14u tall. 26u away he's a thumbnail you can't judge; 15u fills the frame from feet to antlers, which is
            // what the inspection standard needs (VISUAL_VALIDATION.md — close, clean framing).
            Vector3 pin = p.GlobalPosition + fwd * 15f;
            var b = g.SpawnEnemyForTest("boss", pin);
            await WaitFrames(45);
            if (b == null || !GodotObject.IsInstanceValid(b)) { _errors.Add("no boss spawned"); return; }

            _warnings.Add($"CLIPS resolved={b.DebugClipCount} (expect 13: walk/run/cast1/cast6/gripthrow/stomp/charge/death/standup/4 walk variants)");
            if (b.DebugClipCount < 13) _errors.Add($"hollow man resolved only {b.DebugClipCount}/13 clips — the merged GLB didn't register");
            if (!b.IsAuthoredGoblin) _errors.Add("boss did NOT load the authored GLB (fell back to the procedural half-orc body)");

            // hold him in place and keep him aimed at the camera for every capture
            async Task Hold(int n) { for (int i = 0; i < n; i++) { if (GodotObject.IsInstanceValid(b)) b.GlobalPosition = new Vector3(pin.X, b.GlobalPosition.Y, pin.Z); await NextFrame(); _frame++; } }
            // hold until the current wind-up passes `frac` of the way through (or we run out of patience)
            async Task Until(float frac, int cap = 400)
            { int i = 0; while (GodotObject.IsInstanceValid(b) && b.DebugBossWinding && b.DebugBossChargeFrac < frac && i++ < cap) await Hold(1); }

            await Hold(25); await Capture("00_idle_walk");

            // --- each attack: mid wind-up (clip + gesture + hand glow all readable) then the release ---
            (int pat, string tag, float dur)[] atk =
            {
                (0, "volley_cast6",    1.6f),
                (1, "radial_cast1",    1.6f),
                (4, "pestilence_grip", 2.0f),
                (5, "stomp",           2.0f),
                (6, "rockthrow_grip",  2.4f),
                (7, "mines_proc",      2.4f),
            };
            int n2 = 1;
            foreach (var a in atk)
            {
                b.DebugBossPattern(a.pat, a.dur);
                await Until(0.55f);
                // prove the CLIP is what's posing him mid-wind-up, not just the VFX firing over an idle walk
                string clip = b.DebugBossClipState;
                _warnings.Add($"ANIM {a.tag}: {clip}");
                if (a.pat != 7 && clip.StartsWith("walk")) _errors.Add($"{a.tag} never left the walk clip — its attack animation did not play");
                await Capture($"{n2:00}_{a.tag}_windup"); n2++;
                await Until(0.99f);
                await Hold(6);
                await Capture($"{n2:00}_{a.tag}_release"); n2++;
                await Hold(40);   // let the follow-through finish and the glow fade before the next one
            }

            // --- close NOVA: the reworked VFX, captured right on the burst ---
            b.DebugBossPattern(3, 0.9f);
            await Until(0.99f);
            await Hold(3); await Capture($"{n2:00}_nova_burst"); n2++;
            await Hold(45);

            // --- the 20%-HP head-down charge: real damage → threshold → wind-up → 30u dash ---
            // A queued charge does NOT interrupt an attack already telegraphing (that would be unfair — the lanes are
            // already drawn), so let whatever he's doing finish before asserting the charge is what comes next.
            int guard = 0;
            while (GodotObject.IsInstanceValid(b) && b.DebugBossWinding && guard++ < 500) await Hold(1);
            // push him back so the whole 30u dash stays in frame instead of running past the camera
            Vector3 far = p.GlobalPosition + fwd * 34f;
            b.GlobalPosition = new Vector3(far.X, b.GlobalPosition.Y, far.Z);
            await Hold(20);
            float hp0 = b.Hp;
            b.Hurt(b.MaxHp * 0.21f, DamageType.Lunar);   // strip past the first 20% boundary
            guard = 0;
            while (GodotObject.IsInstanceValid(b) && !b.DebugBossWinding && guard++ < 300) await Hold(1);
            _warnings.Add($"CHARGE ARM: hp {hp0:0}→{b.Hp:0} of {b.MaxHp:0}, winding={b.DebugBossWinding}, pat={b.BossAttackName}");
            if (!b.DebugBossWinding) _errors.Add("crossing 20% max HP did NOT arm the head-down charge");
            else if (b.BossAttackName != "CHARGE") _errors.Add($"threshold armed the wrong attack: {b.BossAttackName}");
            await Until(0.6f); await Capture($"{n2:00}_charge_windup"); n2++;

            // let the dash actually run — do NOT pin him, the whole point is that he travels
            Vector3 from = b.GlobalPosition;
            guard = 0; while (GodotObject.IsInstanceValid(b) && !b.DebugBossDashing && guard++ < 300) { await NextFrame(); _frame++; }
            await WaitFrames(14); await Capture($"{n2:00}_charge_dash"); n2++;
            guard = 0; while (GodotObject.IsInstanceValid(b) && b.DebugBossDashing && guard++ < 300) { await NextFrame(); _frame++; }
            float travelled = GodotObject.IsInstanceValid(b) ? new Vector2(b.GlobalPosition.X - from.X, b.GlobalPosition.Z - from.Z).Length() : 0f;
            float pushed = GodotObject.IsInstanceValid(b) ? b.DebugDashPushed : 0f;
            // `pushed` is the dash's own motion; `travelled` is net displacement, which is legitimately shorter when the
            // world's terrain/structure resolve stops him against something. Assert on the dash, report the gap.
            _warnings.Add($"CHARGE DASH: pushed {pushed:0.0}u, net displacement {travelled:0.0}u (DashDist={Enemy.DashDist:0})");
            if (pushed < Enemy.DashDist * 0.97f) _errors.Add($"charge dash only applied {pushed:0.0}u of {Enemy.DashDist:0}");
            else if (travelled < Enemy.DashDist * 0.8f) _warnings.Add($"NOTE: net travel {travelled:0.0}u < pushed {pushed:0.0}u — terrain/structure stopped him partway (expected on broken ground)");
            await WaitFrames(12); await Capture($"{n2:00}_charge_end"); n2++;

            // --- death: the fall-forward clip. Sampled EARLY (the sequence frees him ~2.7s in), and he's pinned back in
            //     frame first because the charge left him wherever it ended.
            if (GodotObject.IsInstanceValid(b))
            {
                b.GlobalPosition = new Vector3(pin.X, b.GlobalPosition.Y, pin.Z);
                await Hold(24);
                b.Hurt(b.MaxHp * 4f, DamageType.Lunar);
                await WaitFrames(6);  await Capture($"{n2:00}_death_pitch"); n2++;
                await WaitFrames(22); await Capture($"{n2:00}_death_falling"); n2++;
                await WaitFrames(34); await Capture($"{n2:00}_death_down");
                _warnings.Add($"DEATH: boss node still alive at ~1s = {GodotObject.IsInstanceValid(b)} (the fall clip runs ~2.7s before QueueFree)");
            }
        }

        // PHASE 2. Kills him once and then walks the whole second-phase contract: the fake death (no orbs, no payout),
        // the prone laugh, the stand-up, the untouchable laughing advance, the arcane aura, a three-charge set, the
        // vortex-to-stomp at 66%, and finally a real death that DOES end the fight.
        private async Task HollowPhase2Scenario()
        {
            var g = Game.I; var p = g?.Player; if (p == null) { _errors.Add("no Player"); return; }
            g.NoSpawn = true; g.ClearEnemies();
            await WaitFrames(20);
            Vector3 fwd = -p.GlobalTransform.Basis.Z; fwd.Y = 0f; fwd = fwd.Normalized();
            Vector3 pin = p.GlobalPosition + fwd * 17f;
            var b = g.SpawnEnemyForTest("boss", pin);
            await WaitFrames(45);
            if (b == null || !GodotObject.IsInstanceValid(b)) { _errors.Add("no boss spawned"); return; }

            async Task Hold(int n) { for (int i = 0; i < n; i++) { if (GodotObject.IsInstanceValid(b)) b.GlobalPosition = new Vector3(pin.X, b.GlobalPosition.Y, pin.Z); await NextFrame(); _frame++; } }
            async Task Until(System.Func<bool> c, int cap) { int i = 0; while (GodotObject.IsInstanceValid(b) && !c() && i++ < cap) await Hold(1); }
            bool Alive() => b != null && GodotObject.IsInstanceValid(b);

            float p1Max = b.MaxHp;
            int orbs0 = g.Orbs != null ? g.Orbs.Count : 0;
            await Hold(15); await Capture("00_phase1");

            // ---- (1) kill phase 1: he must NOT actually die ----
            b.Hurt(p1Max * 4f, DamageType.Lunar);
            await Hold(5);
            _warnings.Add($"AFTER 'KILL': phase={b.BossPhase} dead={b.Dead} hp={b.Hp:0}/{b.MaxHp:0} (phase-1 max was {p1Max:0}) invuln={b.BossInvuln} stage={b.DebugP2Stage}");
            if (b.BossPhase != 2) _errors.Add("killing him did not enter phase 2");
            if (b.Dead) _errors.Add("he actually DIED on the phase-1 kill");
            if (Mathf.Abs(b.MaxHp - p1Max * Enemy.P2HpFrac) > 1f) _errors.Add($"phase-2 pool is {b.MaxHp:0}, expected {p1Max * Enemy.P2HpFrac:0} (half the phase-1 max)");
            if (!b.BossInvuln) _errors.Add("he is not untouchable during the revival");
            int orbs1 = g.Orbs != null ? g.Orbs.Count : 0;
            if (orbs1 > orbs0) _errors.Add($"the fake death dropped {orbs1 - orbs0} XP orbs — it must drop none");
            await Capture("01_fallen");

            // untouchable check: damage while prone must not move the bar
            float hpBefore = b.Hp;
            b.Hurt(b.MaxHp * 0.5f, DamageType.Lunar);
            await Hold(3);
            if (b.Hp < hpBefore - 0.5f) _errors.Add($"took damage while invulnerable ({hpBefore:0} -> {b.Hp:0})");
            _warnings.Add($"INVULN CHECK (prone): {hpBefore:0} -> {b.Hp:0} (expect unchanged)");

            await Until(() => b.DebugP2Stage != 1, 900);
            await Hold(2); await Capture("02_standing_up");
            await Until(() => b.DebugP2Stage == 3, 900);
            await Hold(10); await Capture("03_laughing_advance");
            _warnings.Add($"ADVANCE: stage={b.DebugP2Stage} invuln={b.BossInvuln} (expect 3 / True)");
            if (!b.BossInvuln) _errors.Add("not invulnerable during the laughing advance");

            // ---- (2) revival ends -> straight into the first three-charge set ----
            await Until(() => !b.BossInvuln, 900);
            // NOTE: don't assert ==3 here — he starts the first charge on the very next tick, so this legitimately
            // reads 2 by the time we look. The real proof is the dash COUNT below.
            _warnings.Add($"RISEN: invuln={b.BossInvuln} tripleLeft={b.DebugTripleLeft} (expect False / 3 or 2)");
            if (b.DebugTripleLeft <= 0) _errors.Add($"standing up did not arm the 3x charge (tripleLeft={b.DebugTripleLeft})");
            await Hold(6); await Capture("04_risen_aura");

            // let all three charges run — he is NOT pinned here, he has to travel
            int seen = 0; int guard = 0;
            while (Alive() && seen < 3 && guard++ < 1800)
            {
                if (b.DebugBossDashing) { seen++; while (Alive() && b.DebugBossDashing && guard++ < 1800) { await NextFrame(); _frame++; } if (seen == 1) await Capture("05_triple_charge"); }
                await NextFrame(); _frame++;
            }
            _warnings.Add($"TRIPLE CHARGE: dashes observed={seen} (expect 3), tripleLeft={(Alive() ? b.DebugTripleLeft : -1)}");
            if (seen < 3) _errors.Add($"only {seen}/3 charges ran in the set");
            if (!Alive()) { _errors.Add("boss vanished during the charge set"); return; }

            // ---- (3) drop him past 66% -> the vortex ----
            // Reset the arena first: three charges shoved the witch across the map (she ended up outside the 50u pull
            // entirely) and left her near death, which would make both the pull and the stomp measurements meaningless.
            b.GlobalPosition = new Vector3(pin.X, b.GlobalPosition.Y, pin.Z);
            Vector3 stand = b.GlobalPosition - fwd * 28f;
            p.GlobalPosition = new Vector3(stand.X, g.SurfaceHeight(stand, 1e9f) + 0.2f, stand.Z);
            p.Heal(9999f);
            await Hold(10);
            b.Hurt(b.MaxHp * 0.36f, DamageType.Lunar);   // crosses the first 1/3 threshold
            await Hold(4);
            _warnings.Add($"SPIN ARM: hp={b.Hp:0}/{b.MaxHp:0} pending={b.DebugSpinPending} invuln={b.BossInvuln} (expect pending/invuln True)");
            if (!b.BossInvuln) _errors.Add("crossing the 1/3 threshold did not make him untouchable immediately");
            await Until(() => b.BossSpinning, 900);
            if (!b.BossSpinning) { _errors.Add("the vortex never started"); return; }
            if (!b.DebugVortexUp) _errors.Add("spinning but no BossVortex node exists");
            await Hold(20); await Capture("06_vortex");

            // the pull must actually drag the witch in — stand still and measure
            float d0 = new Vector2(p.GlobalPosition.X - b.GlobalPosition.X, p.GlobalPosition.Z - b.GlobalPosition.Z).Length();
            await Hold(90);
            float d1 = new Vector2(p.GlobalPosition.X - b.GlobalPosition.X, p.GlobalPosition.Z - b.GlobalPosition.Z).Length();
            _warnings.Add($"VORTEX PULL: {d0:0.0}u -> {d1:0.0}u while standing still (expect measurably closer)");
            if (d1 >= d0 - 1f) _errors.Add($"the vortex did not pull the witch in ({d0:0.0} -> {d1:0.0})");
            await Capture("07_pulled_in");

            // ride it out to the finishing stomp, sampling the grind so a silent no-op can't pass as "she dodged it"
            // Measure HP+SHIELD: the grind chews the shield first, so watching bare HP reports "no damage" for the
            // several seconds it takes to strip it — a false failure that hides whether the tick is landing at all.
            float poolBefore = p.Hp + p.Shield, hpBeforeStomp = p.Hp;
            float poolMid = poolBefore, distMid = 0f;
            int gi = 0;
            while (Alive() && b.BossSpinning && gi++ < 1400)
            {
                await Hold(1);
                if (gi == 150) { poolMid = p.Hp + p.Shield; distMid = new Vector2(p.GlobalPosition.X - b.GlobalPosition.X, p.GlobalPosition.Z - b.GlobalPosition.Z).Length(); }
            }
            float distEnd = new Vector2(p.GlobalPosition.X - b.GlobalPosition.X, p.GlobalPosition.Z - b.GlobalPosition.Z).Length();
            _warnings.Add($"GRIND: hp+shield {poolBefore:0} -> {poolMid:0} @ {distMid:0.0}u -> {p.Hp + p.Shield:0}; final dist {distEnd:0.0}u (stomp reaches {BossVortex.StompRange:0}u)");
            if (poolMid >= poolBefore) _errors.Add("the vortex dealt NO grind damage while she was inside it");
            await WaitFrames(3); await Capture("08_stomp");
            _warnings.Add($"STOMP: witch hp {hpBeforeStomp:0} -> {p.Hp:0} of {p.S.MaxHp:0} (expect a big flat hit if she was inside {BossVortex.StompRange:0}u)");
            if (distEnd < BossVortex.StompRange && p.Hp >= hpBeforeStomp - 1f) _errors.Add($"she was {distEnd:0.0}u from the eye but the finishing stomp did nothing");
            await WaitFrames(20);
            _warnings.Add($"POST-SPIN: invuln={b.BossInvuln} (expect False — vulnerable again once the spin ends)");
            if (b.BossInvuln) _errors.Add("still untouchable after the spin ended");
            await Capture("09_after_spin");

            // ---- (4) the REAL death ----
            b.GlobalPosition = new Vector3(pin.X, b.GlobalPosition.Y, pin.Z);
            await Hold(10);
            b.Hurt(b.MaxHp * 4f, DamageType.Lunar);
            await WaitFrames(10);
            _warnings.Add($"REAL DEATH: phase={b.BossPhase} dead={(GodotObject.IsInstanceValid(b) ? b.Dead.ToString() : "freed")}");
            if (GodotObject.IsInstanceValid(b) && !b.Dead) _errors.Add("the phase-2 kill did not actually kill him");
            await WaitFrames(24); await Capture("10_final_death");
        }

        // THE WITHERED KING body on the grove's spellcasters, plus the mage cast grafted onto the ogre-bodied bolt
        // throwers. For each one: prove it loaded the authored GLB, prove the RIGHT clip is what's posing it while the
        // ability winds up (not the walk cycle with VFX over the top), and capture the wind-up and the release.
        private async Task WitheredCasterScenario()
        {
            var g = Game.I; var p = g?.Player; if (p == null) { _errors.Add("no Player"); return; }
            g.NoSpawn = true; g.ClearEnemies();
            await WaitFrames(20);
            Vector3 fwd = -p.GlobalTransform.Basis.Z; fwd.Y = 0f; fwd = fwd.Normalized();
            int n = 0;

            // The witch boots into the river shallows, where a 3u caster stands waist-deep in water and can't be judged.
            // Walk outward until the ground is comfortably above the waterline and stage every probe on that dry spot.
            Vector3 stage = p.GlobalPosition;
            for (float r = 10f; r <= 90f && stage == p.GlobalPosition; r += 8f)
                for (int i = 0; i < 12; i++)
                {
                    float a = i * Mathf.Tau / 12f;
                    var q = p.GlobalPosition + new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * r;
                    float y = g.SurfaceHeight(q, 1e9f);
                    if (y >= World.WaterLevel + 1.2f) { stage = new Vector3(q.X, y, q.Z); break; }
                }
            _warnings.Add($"STAGE: {stage.X:0}, {stage.Y:0.0}, {stage.Z:0} (water level {World.WaterLevel:0.0})");

            // Put the foe on the dry stage and the witch `dist` behind it along her own facing, so it frames dead ahead.
            // `dist` is chosen per body size — these read at ~a third of frame height, which is what judging them needs.
            async Task Probe(string type, float dist, string wantClip, string tag, bool ally, int cap)
            {
                g.ClearEnemies();
                await WaitFrames(12);
                Vector3 pin = stage;
                Vector3 back = stage - fwd * dist;
                p.GlobalPosition = new Vector3(back.X, g.SurfaceHeight(back, 1e9f) + 0.2f, back.Z);
                p.Heal(9999f);
                await WaitFrames(6);
                var e = g.SpawnEnemyForTest(type, pin);
                Enemy pal = null;
                if (ally)   // the healer only mends a WOUNDED ally, so give it one
                {
                    pal = g.SpawnEnemyForTest("shade", pin + fwd.Rotated(Vector3.Up, 1.2f) * 4f);
                    await WaitFrames(10);
                    if (GodotObject.IsInstanceValid(pal)) pal.Hurt(pal.MaxHp * 0.6f, DamageType.Lunar);
                }
                await WaitFrames(35);
                if (e == null || !GodotObject.IsInstanceValid(e)) { _errors.Add($"{tag}: nothing spawned"); return; }

                bool authored = e.IsAuthoredGoblin;
                _warnings.Add($"{tag}: authored={authored} clips={e.DebugClipCount} has[{wantClip}]={e.DebugHasClip(wantClip)}");
                if (!authored) _errors.Add($"{tag}: did not load an authored biped GLB (fell back to the procedural body)");
                if (!e.DebugHasClip(wantClip)) _errors.Add($"{tag}: rig has no '{wantClip}' clip — the merge/graft did not register it");

                bool wound = true;   // keep the healer's ally hurt so it keeps casting; switched off before the release check
                async Task Hold(int k)
                {
                    for (int i = 0; i < k; i++)
                    {
                        if (GodotObject.IsInstanceValid(e)) e.GlobalPosition = new Vector3(pin.X, e.GlobalPosition.Y, pin.Z);
                        if (wound && GodotObject.IsInstanceValid(pal)) pal.Hp = Mathf.Min(pal.Hp, pal.MaxHp * 0.6f);
                        p.Heal(9999f);   // these foes really do shoot at her — don't let the probe end in a death
                        await NextFrame(); _frame++;
                    }
                }

                await Hold(10);
                float gapIdle = e.DebugFootGap;
                await Capture($"{n:00}_{tag}_idle"); n++;

                int guard = 0;
                while (GodotObject.IsInstanceValid(e) && !e.DebugCasting && guard++ < cap) await Hold(1);
                // The behavior switch starts the cast AFTER that frame's animation step, so the new clip isn't on the
                // AnimationPlayer until the next tick — read the pose a few frames in, not on the frame it was requested.
                await Hold(4);
                string state = GodotObject.IsInstanceValid(e) ? e.DebugBossClipState : "freed";
                _warnings.Add($"{tag}: casting={GodotObject.IsInstanceValid(e) && e.DebugCasting} after {guard}f — {state}");
                if (guard >= cap) _errors.Add($"{tag}: never started a cast clip within {cap} frames");
                else if (!state.StartsWith(wantClip + "@")) _errors.Add($"{tag}: winding up on '{state}' instead of the '{wantClip}' clip");
                // GROUNDING: a grafted clip carries the SOURCE rig's hip translations. If the retarget onto this body's
                // proportions is off, the cast pose floats or sinks — measure it instead of squinting at the capture.
                float gapCast = GodotObject.IsInstanceValid(e) ? e.DebugFootGap : 0f;
                _warnings.Add($"{tag}: foot gap idle {gapIdle:0.00}R -> cast {gapCast:0.00}R (0 = planted; radii)");
                if (Mathf.Abs(gapCast) > 0.35f) _errors.Add($"{tag}: the cast pose leaves its feet {gapCast:0.00} radii off the ground");
                await Capture($"{n:00}_{tag}_cast"); n++;

                // …and hold through the release so the follow-through and the return to the walk are both visible.
                // Stop giving it a reason to cast first: a healer whose ally is permanently wounded re-casts on the very
                // frame the last clip ends, so "still casting" would never be observably false.
                wound = false;
                if (GodotObject.IsInstanceValid(pal)) pal.Heal(pal.MaxHp);
                guard = 0;
                while (GodotObject.IsInstanceValid(e) && e.DebugCasting && guard++ < cap) await Hold(1);
                // Latch the verdict HERE. The totem and the healer cast on a duty cycle, so by the time the capture
                // below has run its frames they are legitimately mid-NEXT-cast — re-reading the flag then reports a
                // stuck body that isn't one.
                bool released = GodotObject.IsInstanceValid(e) && !e.DebugCasting;
                if (!released) _errors.Add($"{tag}: the cast clip never released — {e.DebugBossClipState} / {e.DebugCastState}");
                await Hold(3); await Capture($"{n:00}_{tag}_release"); n++;
            }

            await Probe("caster",    7f, "cast4",      "caster",    false, 420);
            await Probe("zapper",    7f, "castcharge", "stunner",   false, 500);
            await Probe("healer",    7f, "cast",       "healer",    true,  420);
            await Probe("totem",     8f, "cast",       "empowerer", false, 300);
            await Probe("hexer",     7f, "cast",       "hexer",     false, 500);
            await Probe("wardbane",  7f, "cast",       "dispeller", false, 500);
            await Probe("sieger",   11f, "cast4",      "sieger",    false, 500);
            await Probe("miniboss", 15f, "cast4",      "miniboss",  false, 600);

            // ---- MULTIPLAYER: the client-side proxy path ----
            // The harness is one process, so this drives exactly what the RPC drives: flag the foe as a client proxy
            // (host-driven position, no local AI) and hand it the same RemoteCast that ReceiveEnemyCast calls. What it
            // proves: the wire's clip index maps back to the right clip, the proxy's per-frame BipedLoco doesn't stomp
            // it, and UpdateCastAnim runs on the Remote branch so the proxy returns to its walk instead of freezing.
            async Task ProxyProbe(string type, int clipIdx, string wantClip, string tag)
            {
                g.ClearEnemies();
                await WaitFrames(12);
                Vector3 pin = stage;
                Vector3 back = stage - fwd * 7f;
                p.GlobalPosition = new Vector3(back.X, g.SurfaceHeight(back, 1e9f) + 0.2f, back.Z);
                var e = g.SpawnEnemyForTest(type, pin);
                if (e == null || !GodotObject.IsInstanceValid(e)) { _errors.Add($"proxy {tag}: nothing spawned"); return; }
                e.Remote = true;   // from here on it behaves like a client's copy: the host owns its position and its AI
                async Task Hold(int k)
                {
                    for (int i = 0; i < k; i++)
                    {
                        if (GodotObject.IsInstanceValid(e)) e.SetRemoteTarget(pin);   // the position stream a real client would get
                        await NextFrame(); _frame++;
                    }
                }
                await Hold(25);
                if (e.DebugCasting) { _errors.Add($"proxy {tag}: already casting before the host said anything"); return; }

                e.RemoteCast(clipIdx, 0.9f, 2.0f);   // ← exactly what Net.ReceiveEnemyCast does on a client
                await Hold(5);
                string state = e.DebugBossClipState;
                _warnings.Add($"proxy {tag}: clip idx {clipIdx} -> {state}, foot gap {e.DebugFootGap:0.00}R");
                if (!state.StartsWith(wantClip + "@")) _errors.Add($"proxy {tag}: idx {clipIdx} posed '{state}', expected the '{wantClip}' clip");
                await Capture($"{n:00}_proxy_{tag}_cast"); n++;

                int guard = 0;
                while (GodotObject.IsInstanceValid(e) && e.DebugCasting && guard++ < 300) await Hold(1);
                if (guard >= 300) _errors.Add($"proxy {tag}: the cast never released on the proxy — UpdateCastAnim isn't running on the Remote branch");
                else _warnings.Add($"proxy {tag}: released after {guard}f, back on {e.DebugBossClipState}");
                await Hold(4); await Capture($"{n:00}_proxy_{tag}_walk"); n++;
            }

            await ProxyProbe("caster", 1, "cast4",      "caster");
            await ProxyProbe("healer", 0, "cast",       "healer");
            await ProxyProbe("zapper", 2, "castcharge", "stunner");
        }

        // Verdant's Wild Swarm ult. The point of the scenario is a LIKENESS check: the stampede critters are supposed to be the
        // same cute tree-ents she summons, and a perf pass had quietly swapped them for bare brown capsules. So it stages a live
        // Thornling as the reference, then runs the herd head-on past that exact ent and side-on across the view.
        // Progress is gated on how far the herd has actually run (DebugLead), never on a frame count — the headless framerate
        // varies and the stampede advances on real delta.
        private async Task WildSwarmScenario()
        {
            var g = Game.I; var p = g?.Player; if (p == null) { _errors.Add("no Player"); return; }
            g.NoSpawn = true; g.ClearEnemies();
            await WaitFrames(25);
            if (!p.VerdantWitch) _warnings.Add("player is not the Verdant witch — ent tinting may differ");
            Vector3 fwd = -p.GlobalTransform.Basis.Z; fwd.Y = 0f; fwd = fwd.Normalized();
            Vector3 right = p.GlobalTransform.Basis.X; right.Y = 0f; right = right.Normalized();

            // she boots into the river shallows — find dry ground so the little guys aren't knee-deep in water
            Vector3 stage = p.GlobalPosition;
            for (float r = 10f; r <= 90f && stage == p.GlobalPosition; r += 8f)
                for (int i = 0; i < 12; i++)
                {
                    float a = i * Mathf.Tau / 12f;
                    var q = p.GlobalPosition + new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * r;
                    float y = g.SurfaceHeight(q, 1e9f);
                    if (y >= World.WaterLevel + 1.2f) { stage = new Vector3(q.X, y, q.Z); break; }
                }
            p.GlobalPosition = new Vector3(stage.X, g.SurfaceHeight(stage, 1e9f) + 0.2f, stage.Z);
            await WaitFrames(10);
            _warnings.Add($"STAGE: {stage.X:0}, {stage.Y:0.0}, {stage.Z:0} (water level {World.WaterLevel:0.0})");

            Thornling Ent(Vector3 at)
            {
                var t = new Thornling { Caster = p, Slot = p.Ents.Count };
                g.AddChild(t);
                t.GlobalPosition = new Vector3(at.X, g.SurfaceHeight(at, 1e9f), at.Z);
                p.Ents.Add(t);
                return t;
            }
            // with no foes alive an ent walks back to the witch's side, which drags it out of frame — hold the staged
            // ones on their marks every frame
            Vector3 refSpot = stage + fwd * 4.5f + right * 1.6f, markSpot = stage + fwd * 13f + right * 3f;
            Thornling refEnt = null, markEnt = null;
            void PinEnts()
            {
                if (GodotObject.IsInstanceValid(refEnt)) refEnt.GlobalPosition = new Vector3(refSpot.X, refEnt.GlobalPosition.Y, refSpot.Z);
                if (GodotObject.IsInstanceValid(markEnt)) markEnt.GlobalPosition = new Vector3(markSpot.X, markEnt.GlobalPosition.Y, markSpot.Z);
            }
            async Task PinFrames(int n) { for (int i = 0; i < n; i++) { PinEnts(); await NextFrame(); _frame++; } }

            // 00 — the REFERENCE: one of her real summoned tree-ents, close enough to read the face/tuft/arms
            refEnt = Ent(refSpot);
            await PinFrames(45);
            await Capture("00_ent_reference");

            // the ent the herd will run straight past, for a same-frame side-by-side
            markEnt = Ent(markSpot);

            Stampede Launch(Vector3 origin, Vector3 dir, float width, float dur)
            {
                var s = new Stampede();
                g.AddChild(s);
                s.Init(p, new Vector3(origin.X, g.SurfaceHeight(origin, 1e9f), origin.Z), dir, width, 0f, dur, false);
                return s;
            }
            async Task Run(Stampede s, float lead, string tag, int cap = 900)
            {
                int f = 0;
                while (f++ < cap && GodotObject.IsInstanceValid(s) && s.DebugLead < lead) { PinEnts(); await NextFrame(); _frame++; }
                if (f >= cap) _errors.Add($"{tag}: the herd never reached lead {lead} (stampede stalled or freed early)");
                await Capture(tag);
            }

            // ---- head-on: the herd charges from 48u out straight back at the camera, past markEnt at 13u ----
            var head = Launch(stage + fwd * 48f, -fwd, 9f, 9f);
            await WaitFrames(20);
            _warnings.Add($"HEAD-ON: surfaces={head.DebugSurfaces} live={head.DebugLive}");
            if (head.DebugSurfaces <= 1) _errors.Add($"critter mesh has {head.DebugSurfaces} surface(s) — that's a single-material blob, not the baked tree-ent body");
            if (head.DebugLive <= 0) _errors.Add("no critters spawned");

            await Run(head, 20f, "01_charge_far");            // front of the herd ~28u out
            await Run(head, 33f, "02_alongside_ent");         // front is level with markEnt — compare herd vs ent in ONE frame
            await Run(head, 42f, "03_charge_close");          // front ~6u out
            await Run(head, 46f, "04_charge_faceoff");        // front ~2u out — a critter at full size, next to the reference ent
            if (GodotObject.IsInstanceValid(head)) head.QueueFree();
            await WaitFrames(20);

            // ---- side-on: the herd crosses the view 22u ahead so the running silhouette + hop read in profile ----
            var side = Launch(stage + fwd * 22f + right * 38f, -right, 9f, 8f);
            await Run(side, 38f, "05_side_profile");
            await Run(side, 46f, "06_side_profile_b");
            if (GodotObject.IsInstanceValid(side)) side.QueueFree();
        }

        // Data audit for the coven perk trees. Walks all 9 witches × 36 nodes × 3 hidden routes and, for each, resets the
        // player to a known baseline, fingerprints every stat the trees can touch, applies the node, and fingerprints again.
        // A node that moves NOTHING is either a typo or a knob that doesn't exist — exactly the class of bug that made
        // "Blink Step" a move-speed perk. Also catches a mis-sized def table (Perks.Nodes throws / logs) and empty text.
        private async Task PerkAudit()
        {
            var g = Game.I; var p = g?.Player; if (p == null) { _errors.Add("no Player"); return; }
            g.NoSpawn = true; g.ClearEnemies();
            await WaitFrames(20);

            // every field any perk node is allowed to move, weighted so two offsetting changes can't cancel out
            double Fingerprint()
            {
                var s = p.S; double v = 0; int k = 1;
                void Add(double x) { v += x * k++; }
                Add(s.Atk); Add(s.FireCd); Add(s.Speed); Add(s.ChargeSpeed); Add(s.MaxCharge); Add(s.Pierce);
                Add(s.MaxHp); Add(s.Lifesteal); Add(s.DmgResist); Add(s.JumpMul); Add(s.PickupRange);
                Add(s.ComboPow); Add(s.ComboCap); Add(s.ComboWindow);
                Add(s.DashDist); Add(s.DashCd); Add(s.DashCharges); Add(s.ManaMax); Add(s.ManaGain);
                Add(s.CritChance); Add(s.CritDamage); Add(s.SpellRange); Add(s.SpellArea); Add(s.ProjSpeed); Add(s.Luck);
                Add(s.ShieldPct); Add(s.ShieldDelay); Add(s.ShieldRegen);
                Add(p.CrescentPierceBonus); Add(p.CrescentSizeMul); Add(p.LunarBonus); Add(p.UltChargeMul);
                Add(p.BlessBonus); Add(p.MoteFork); Add(p.Interventions);
                Add(p.AuraBonusR); Add(p.AuraHealMul); Add(p.FinHpCost); Add(p.MaxArmor);
                Add(p.GroveEvery); Add(p.GroveBonusEnts); Add(p.MinionDmgMul); Add(p.PoisonMul);
                Add(p.GustPower);
                Add(p.FreezeRate); Add(p.FrostDurBonus); Add(p.FreezeThreshMul); Add(p.ShatterPowerMul); Add(p.ShatterFreezeStacks);
                Add(p.MaxLinks); Add(p.CurseRate); Add(p.CurseShareFrac); Add(p.CurseSpreadRange);
                Add(p.CurseBonusMul); Add(p.CurseStackCap); Add(p.CurseBeamLifesteal);
                Add(p.EmberBurnMul); Add(p.FlameReachMul); Add(p.LivingBombMul);
                Add(p.ArcanePowerMul); Add(p.ArcaneCritHealBonus); Add(p.ArcaneMarkDur);
                // the run-scoped legendary gates a restart must also drop
                Add(p.MinionChain ? 1 : 0); Add(p.GravityWell ? 1 : 0); Add(p.Bloodbath ? 1 : 0); Add(p.SanguineFrenzy ? 1 : 0);
                Add(p.Hemoclast ? 1 : 0); Add(p.MartyrGrace ? 1 : 0); Add(p.RadiantMote ? 1 : 0); Add(p.GuardianAegis ? 1 : 0);
                Add(p.CrimsonFrenzy ? 1 : 0); Add(p.AncientGrove ? 1 : 0); Add(p.VerdantVitality ? 1 : 0);
                Add(p.TempestHeart ? 1 : 0); Add(p.Cloudfeather ? 1 : 0); Add(p.Downburst ? 1 : 0); Add(p.Jetstream ? 1 : 0);
                Add(p.ShatterCascade ? 1 : 0); Add(p.DeepWinter ? 1 : 0); Add(p.GlacialImpaler ? 1 : 0);
                Add(p.SoulTether ? 1 : 0); Add(p.WitheringPresence ? 1 : 0); Add(p.CurseBonusType2);
                Add(p.EmberInferno ? 1 : 0); Add(p.FervorWildfire); Add(p.FervorPhoenix);
                Add(p.ArcaneChainReaction ? 1 : 0); Add(p.ArcanePersistMarks ? 1 : 0); Add(p.ArcaneLiving ? 1 : 0);
                Add(p.EntElementChosen ? 1 : 0); Add((int)p.EntElement); Add((int)p.CurseBonusType);
                // (CARDS) the situational bonuses + on-hit behaviours a restart must also drop
                Add(p.SitNight); Add(p.SitAirborne); Add(p.SitLowHp); Add(p.SitHighHp); Add(p.SitComboCap);
                Add(p.SitPostDash); Add(p.SitGrove); Add(p.SitStill); Add(p.SitFullMana);
                Add(p.KillRefreshCombo ? 1 : 0); Add(p.KillHeal); Add(p.CritMana);
                return v;
            }
            // Baseline = exactly what a run restart does. It delegates to the SHIPPED reset, so `Fingerprint`'s
            // hand-written field list above stays an INDEPENDENT enumeration — if Player.ResetWitchScalars ever misses
            // a field the trees touch, the round-trip check below catches it instead of the test quietly agreeing.
            void Baseline() { p.S = new Stats(); p.ResetWitchScalars(); }

            int dead = 0, checkedNodes = 0;
            var longName = new List<string>();
            var reqSig = new List<(string who, string sig, int cost)>();   // for the cross-witch uniqueness check
            string Signature(int[] req) { var a = (int[])req.Clone(); System.Array.Sort(a); return string.Join(",", a); }
            for (int w = 0; w < Perks.WitchCount; w++)
            {
                PerkNode[] nodes;
                try { nodes = Perks.Nodes(w); }
                catch (System.Exception ex) { _errors.Add($"witch {w}: Perks.Nodes threw ({ex.GetType().Name}) — a def table is the wrong size"); continue; }
                if (nodes.Length != Perks.NodeCount) { _errors.Add($"witch {w}: {nodes.Length} nodes, expected {Perks.NodeCount}"); continue; }

                for (int id = 0; id < nodes.Length; id++)
                {
                    var n = nodes[id];
                    if (string.IsNullOrWhiteSpace(n.Name)) { _errors.Add($"witch {w} node {id}: empty name"); continue; }
                    if (string.IsNullOrWhiteSpace(n.Desc)) { _errors.Add($"witch {w} '{n.Name}': empty description"); continue; }
                    if (n.Apply == null) { _errors.Add($"witch {w} '{n.Name}': no effect at all"); continue; }
                    if (n.Name.Length > 22) longName.Add($"w{w} node '{n.Name}' ({n.Name.Length})");
                    if (n.Desc.Length > 46) longName.Add($"w{w} desc '{n.Desc}' ({n.Desc.Length})");

                    Baseline();
                    double before = Fingerprint();
                    try { n.Apply(p); }
                    catch (System.Exception ex) { _errors.Add($"witch {w} '{n.Name}': Apply threw {ex.GetType().Name}"); continue; }
                    checkedNodes++;
                    if (System.Math.Abs(Fingerprint() - before) < 1e-9)
                    { _errors.Add($"witch {w} '{n.Name}' ({n.Desc}) changed NOTHING — dead perk"); dead++; }
                }

                var routes = Perks.Routes(w);
                for (int ri = 0; ri < routes.Length; ri++)
                {
                    var r = routes[ri];
                    if (r.Apply == null || r.Req == null || r.Req.Length == 0) { _errors.Add($"witch {w} route '{r.Name}': malformed"); continue; }

                    // ---- can this route actually be COMPLETED in one run? ----
                    // (a) it can never cost more attribute points than a run grants
                    if (r.Req.Length > Perks.AttuneCap)
                        _errors.Add($"witch {w} route '{r.Name}': needs {r.Req.Length} nodes but a run only grants {Perks.AttuneCap} points");
                    // (b) walk it with the GAME's own availability rule, not a re-derivation of the graph. A set can be
                    //     'connected' and still be dead: {24,25} without 20 have each other as predecessors and neither
                    //     can ever be lit.
                    var lit = new HashSet<int>();
                    var want = new HashSet<int>(r.Req);
                    int spent = 0;
                    while (lit.Count < want.Count)
                    {
                        int next = -1;
                        foreach (int cand in Perks.Available(w, lit)) if (want.Contains(cand) && !lit.Contains(cand)) { next = cand; break; }
                        if (next < 0) break;
                        lit.Add(next); spent++;
                    }
                    if (lit.Count < want.Count)
                    {
                        var stuck = new List<int>(); foreach (int n in want) if (!lit.Contains(n)) stuck.Add(n);
                        _errors.Add($"witch {w} route '{r.Name}': UNREACHABLE — stalls with {string.Join(",", stuck)} unlightable (no predecessor in the set)");
                    }
                    else if (spent > Perks.AttuneCap)
                        _errors.Add($"witch {w} route '{r.Name}': costs {spent} points, over the {Perks.AttuneCap} cap");
                    else
                        reqSig.Add(($"w{w}:{r.Name}", Signature(r.Req), spent));

                    // the route panel is NARROWER than the node tooltip — it clipped the old descriptions mid-word
                    if (r.Desc != null && r.Desc.Length > 50) longName.Add($"w{w} route desc '{r.Desc}' ({r.Desc.Length}) — the panel clips past ~50");
                    if (r.Name != null && r.Name.Length > 18) longName.Add($"w{w} route name '{r.Name}' ({r.Name.Length})");
                    foreach (int req in r.Req) if (req < 0 || req >= Perks.NodeCount) _errors.Add($"witch {w} route '{r.Name}': bad required node {req}");
                    Baseline();
                    double before = Fingerprint();
                    try { r.Apply(p); }
                    catch (System.Exception ex) { _errors.Add($"witch {w} route '{r.Name}': Apply threw {ex.GetType().Name}"); continue; }
                    if (System.Math.Abs(Fingerprint() - before) < 1e-9) { _errors.Add($"witch {w} route '{r.Name}' changed NOTHING"); dead++; }
                }

                // ---- RESTART must wipe the slate ----
                // Light every node and fire every route on this witch, then do exactly what starting a fresh run does.
                // The fingerprint has to land back on the pristine value; if it doesn't, some field a perk moved is
                // surviving into the next run — which is what used to happen to all ~50 witch scalars.
                Baseline();
                double pristine = Fingerprint();
                foreach (var n in nodes) { try { n.Apply?.Invoke(p); } catch { } }
                foreach (var rt in routes) { try { rt.Apply?.Invoke(p); } catch { } }
                if (System.Math.Abs(Fingerprint() - pristine) < 1e-9)
                    _errors.Add($"witch {w}: lighting the WHOLE tree changed nothing — the audit isn't measuring anything");
                Baseline();
                if (System.Math.Abs(Fingerprint() - pristine) > 1e-9)
                    _errors.Add($"witch {w}: a restart does NOT clear the tree — Player.ResetWitchScalars is missing a field the perks touch");
            }

            Baseline();
            _warnings.Add($"audited {checkedNodes} nodes + {Perks.WitchCount * 3} routes across {Perks.WitchCount} witches — {dead} dead");
            foreach (var s in longName) _warnings.Add($"TEXT may clip: {s}");

            // Every witch used to point at the same three node-sets, so finding a route on one tree spoiled all nine
            // and the path never matched what the route granted. Each set must now be unique across the whole coven.
            for (int i = 0; i < reqSig.Count; i++)
                for (int j = i + 1; j < reqSig.Count; j++)
                    if (reqSig[i].sig == reqSig[j].sig)
                        _errors.Add($"route node-set SHARED by {reqSig[i].who} and {reqSig[j].who}: [{reqSig[i].sig}]");
            int minC = 99, maxC = 0;
            foreach (var s in reqSig) { if (s.cost < minC) minC = s.cost; if (s.cost > maxC) maxC = s.cost; }
            _warnings.Add($"routes: {reqSig.Count} completable, all node-sets unique, cost {minC}-{maxC} of {Perks.AttuneCap} points");
            _warnings.Add("restart round-trip: whole tree lit then reset → back to pristine on all 9 witches");

            // the dev toggle — snapshot first: this writes the REAL save, and wiping the player's discovery log would
            // be a genuinely destructive side effect of running a test
            var savedRoutes = Perks.DiscoveredSnapshot();
            Perks.SetAllDiscovered(false);
            if (Perks.DiscoveredCount != 0) _errors.Add($"routes off: {Perks.DiscoveredCount} still catalogued");
            Perks.SetAllDiscovered(true);
            if (Perks.DiscoveredCount != Perks.RouteTotal) _errors.Add($"routes on: {Perks.DiscoveredCount}/{Perks.RouteTotal} catalogued");
            _warnings.Add($"routes toggle: off→0, on→{Perks.DiscoveredCount}/{Perks.RouteTotal}");

            // render every witch's tree (routes catalogued, so the hidden-route panel shows real text) — this is what
            // the owner actually reads on the Coven page, so the new names have to fit under their nodes
            if (g.PerkScreenUi == null) _errors.Add("no PerkScreenUi to render");
            else
            {
                for (int w = 0; w < Perks.WitchCount; w++)
                {
                    g.PerkScreenUi.Show(w);
                    await WaitFrames(5);
                    string tag = RunStats.WitchName(w).ToLowerInvariant().Replace(" ", "_").Replace("'", "");
                    await Capture($"{w:00}_{tag}");
                }
                g.PerkScreenUi.Hide();
            }
            Perks.DiscoveredRestore(savedRoutes);
            _warnings.Add($"restored the real route catalogue: {Perks.DiscoveredCount}/{Perks.RouteTotal}");
        }

        // The situational card bonuses are the whole point of the card/perk split, and a conditional that silently never
        // fires is worse than a flat stat — it's a card that reads as power and gives none. So for each one: prove the
        // damage spine is UNCHANGED while the condition is false, and moves by exactly the bonus when it's true.
        private async Task CardConditions()
        {
            var g = Game.I; var p = g?.Player; if (p == null) { _errors.Add("no Player"); return; }
            g.NoSpawn = true; g.ClearEnemies();
            await WaitFrames(20);

            const float Bonus = 1.0f;   // +100%, so a working condition doubles Base() and rounding can't hide it
            int ok = 0;
            // name · arm the field · put the world in the FALSE state · put it in the TRUE state
            void Clear() { p.ResetWitchScalars(); p.Hp = p.S.MaxHp; p.Mana = p.S.ManaMax; p.Combo = 0; }
            void Check(string name, System.Action arm, System.Action makeFalse, System.Action makeTrue)
            {
                Clear();
                makeFalse();
                float bare = p.DebugBase;
                arm();
                float off = p.DebugBase;
                makeTrue();
                float on = p.DebugBase;
                if (Mathf.Abs(off - bare) > 0.001f)
                    _errors.Add($"{name}: fires when its condition is FALSE (base {bare:0.###} → {off:0.###})");
                else if (Mathf.Abs(on - bare * (1f + Bonus)) > 0.01f)
                    _errors.Add($"{name}: does NOT fire when its condition is TRUE (expected {bare * (1f + Bonus):0.###}, got {on:0.###})");
                else { ok++; _warnings.Add($"{name}: off={off:0.##} on={on:0.##} (base {bare:0.##}) OK"); }
                Clear();
            }

            Check("SitHighHp (health above 85%)", () => p.SitHighHp = Bonus,
                  () => p.Hp = p.S.MaxHp * 0.5f, () => p.Hp = p.S.MaxHp);
            Check("SitLowHp (health below 40%)", () => p.SitLowHp = Bonus,
                  () => p.Hp = p.S.MaxHp, () => p.Hp = p.S.MaxHp * 0.2f);
            Check("SitFullMana (mana full)", () => p.SitFullMana = Bonus,
                  () => p.Mana = 0f, () => p.Mana = p.S.ManaMax);
            Check("SitComboCap (combo at cap)", () => p.SitComboCap = Bonus,
                  () => { p.Combo = 0; }, () => { p.Combo = p.S.ComboCap + 1; p.ComboT = Game.GameClock; });
            Check("SitPostDash (4s after a dash)", () => p.SitPostDash = Bonus,
                  () => p.DebugSetPostDash(0f), () => p.DebugSetPostDash(3f));
            Check("SitStill (planted)", () => p.SitStill = Bonus,
                  () => p.DebugSetStill(0f), () => p.DebugSetStill(5f));
            Check("SitGrove (3+ tree-ents)", () => p.SitGrove = Bonus,
                  () => p.Ents.Clear(),
                  () => { p.Ents.Clear(); for (int i = 0; i < 3; i++) { var t = new Thornling { Caster = p, Slot = i }; g.AddChild(t); t.GlobalPosition = p.GlobalPosition; p.Ents.Add(t); } });
            // night/airborne can't be forced from here without faking world state — assert only that they're inert right now
            Clear();
            float b0 = p.DebugBase;
            p.SitNight = Bonus; p.SitAirborne = Bonus;
            float b1 = p.DebugBase;
            bool night = g.IsNight, air = p.Airborne;
            if (!night && !air && Mathf.Abs(b1 - b0) > 0.001f) _errors.Add("SitNight/SitAirborne fire while it is day and she is grounded");
            else _warnings.Add($"SitNight/SitAirborne: inert as expected (night={night} airborne={air}) — gating not force-tested");
            Clear();

            _warnings.Add($"{ok}/7 forceable situational conditions verified off→on");

            // and the restart still wipes them, including the new card fields
            p.SitNight = p.SitAirborne = p.SitLowHp = p.SitHighHp = p.SitComboCap = 0.5f;
            p.SitPostDash = p.SitGrove = p.SitStill = p.SitFullMana = 0.5f;
            p.KillRefreshCombo = true; p.KillHeal = 0.1f; p.CritMana = 0.2f;
            p.ResetWitchScalars();
            bool cleared = p.SitNight == 0f && p.SitAirborne == 0f && p.SitLowHp == 0f && p.SitHighHp == 0f
                        && p.SitComboCap == 0f && p.SitPostDash == 0f && p.SitGrove == 0f && p.SitStill == 0f
                        && p.SitFullMana == 0f && !p.KillRefreshCombo && p.KillHeal == 0f && p.CritMana == 0f;
            if (!cleared) _errors.Add("ResetWitchScalars does NOT clear the card fields — they'd leak into the next run");
            else _warnings.Add("restart clears every situational + on-hit card field");
            p.Ents.Clear();
            await WaitFrames(5);
            await Capture("00_card_conditions");
        }

        // Does every witch actually HAVE a witch-specific card pool, or do some ride on generic stats while others get six
        // signature cards? The defs gate themselves on Game.I.Player, so the only honest way to count is to become each
        // witch in turn and rebuild the pool. Checks the Coven ladder (3 low-rarity cards per witch) separately, since
        // that's the Common/Uncommon/Rare spread the purple effigy rolls from.
        private async Task CardPoolAudit()
        {
            var g = Game.I; var p = g?.Player; if (p == null) { _errors.Add("no Player"); return; }
            g.NoSpawn = true; g.ClearEnemies();
            await WaitFrames(20);

            var totals = new List<int>();
            var lowRarity = new List<int>();
            for (int w = 0; w < Perks.WitchCount; w++)
            {
                g.ChangeWitch(w);
                await WaitFrames(3);
                if (p.WitchIndex != w) { _errors.Add($"witch {w}: ChangeWitch left WitchIndex={p.WitchIndex}"); continue; }

                var titles = new HashSet<string>();
                var lowTitles = new HashSet<string>();
                var byTitle = new Dictionary<string, HashSet<string>>();   // Title → the distinct effects wearing it
                foreach (var c in UpgradePool.DebugPool())
                {
                    if (c == null || c.Hidden || !c.Affinity || string.IsNullOrEmpty(c.Title)) continue;
                    titles.Add(c.Title);
                    if (c.Rarity == Rarity.Common || c.Rarity == Rarity.Uncommon) lowTitles.Add(c.Title);
                    // rarity only changes the NUMBERS in a desc, so strip digits: what's left identifies the effect
                    var shape = new System.Text.StringBuilder();
                    foreach (char ch in c.Desc ?? "") if (!char.IsDigit(ch) && ch != '.') shape.Append(ch);
                    if (!byTitle.TryGetValue(c.Title, out var shapes)) byTitle[c.Title] = shapes = new HashSet<string>();
                    shapes.Add(shape.ToString());
                }
                // two different cards sharing one name is a real hazard: UpgradePool.Banned keys on Title, so banning
                // one would silently ban the other too
                foreach (var kv in byTitle)
                    if (kv.Value.Count > 1)
                        _errors.Add($"{RunStats.WitchName(w)}: '{kv.Key}' is used by {kv.Value.Count} DIFFERENT cards — Banned keys on Title, so they'd ban together");
                totals.Add(titles.Count); lowRarity.Add(lowTitles.Count);
                _warnings.Add($"{RunStats.WitchName(w)}: {titles.Count} affinity cards ({lowTitles.Count} at Common/Uncommon)");
                if (titles.Count == 0) _errors.Add($"{RunStats.WitchName(w)} has NO affinity cards at all");
                if (lowTitles.Count < 3) _errors.Add($"{RunStats.WitchName(w)} has only {lowTitles.Count} low-rarity affinity cards — the Coven ladder should give every witch 3");
            }

            if (totals.Count == Perks.WitchCount)
            {
                int lo = 99, hi = 0; foreach (int t in totals) { if (t < lo) lo = t; if (t > hi) hi = t; }
                _warnings.Add($"affinity-card spread across the coven: {lo}-{hi}");
                // a witch with a third of another's signature pool will just see generic cards on her rolls
                if (hi > lo * 2) _warnings.Add($"UNEVEN: the richest witch has {hi} affinity cards, the poorest {lo} — her roll-3 will lean generic");
            }
            g.ChangeWitch(0);
            await WaitFrames(5);
            await Capture("00_pool");
        }

        // Is the game CPU-bound or GPU-bound? perf_haunt sweeps GRAPHICS settings, so it can only ever answer the GPU
        // half. This one reads Godot's own monitors to split the frame: TimeProcess is main-thread SCRIPT time, and it
        // is RESOLUTION-INDEPENDENT — so measuring at 720p headless still tells you what's happening at 4K.
        // The enemy ramp gives the number that actually matters: main-thread milliseconds PER ENEMY.
        private async Task PerfCpu()
        {
            var g = Game.I; var p = g?.Player; if (p == null) { _errors.Add("no Player"); return; }
            Engine.MaxFps = 0;
            DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
            g.NoSpawn = true; g.ClearEnemies();

            void Guard()
            {
                if (g.State != GameState.Playing) { g.State = GameState.Playing; Input.MouseMode = Input.MouseModeEnum.Captured; }
                p.Hp = 100000f;
                Input.ParseInputEvent(new InputEventMouseMotion { Relative = new Vector2(11f, 0f) });   // pan so culling can't flatter us
            }
            async Task GWait(int n) { for (int i = 0; i < n; i++) { await NextFrame(); _frame++; Guard(); } }

            double Mon(Performance.Monitor m) { try { return Performance.GetMonitor(m); } catch { return -1; } }
            // MEDIAN, not mean: spawning a batch of enemies costs one enormous frame (authored GLBs get instanced), and
            // TimeProcess decays from that spike for a long time. A mean over the sample window reports the spike, not
            // the steady state — the first version of this scenario "measured" 85ms of script inside an 11.6ms frame.
            double Median(List<double> v) { if (v.Count == 0) return 0; v.Sort(); return v[v.Count / 2]; }
            async Task<(double fps, double ms, double proc)> Sample(string tag, int frames)
            {
                var fpsV = new List<double>(); var procV = new List<double>(); var physV = new List<double>();
                for (int i = 0; i < frames; i++)
                {
                    await NextFrame(); _frame++; Guard();
                    double f = Engine.GetFramesPerSecond();
                    if (f > 0)
                    {
                        fpsV.Add(f);
                        procV.Add(Mon(Performance.Monitor.TimeProcess) * 1000.0);
                        physV.Add(Mon(Performance.Monitor.TimePhysicsProcess) * 1000.0);
                    }
                }
                if (fpsV.Count == 0) { _warnings.Add($"{tag}: no frames"); return (0, 0, 0); }
                double fps = Median(fpsV), proc = Median(procV), phys = Median(physV);
                double frameMs = fps > 0 ? 1000.0 / fps : 0;
                double draws = Mon(Performance.Monitor.RenderTotalDrawCallsInFrame);
                double nodes = Mon(Performance.Monitor.ObjectNodeCount);
                // script > frame time means the monitor is still polluted; flag it rather than quietly reporting nonsense
                string warn = proc > frameMs + 0.5 ? "  [!! script > frame time — monitor unreliable, trust the ms column]" : "";
                _warnings.Add($"{tag}: {fps:0} fps ({frameMs:0.0}ms) | script {proc:0.00}ms phys {phys:0.00}ms | draws {draws:0} | nodes {nodes:0}{warn}");
                return (fps, frameMs, proc);
            }

            await GWait(180);   // long warmup: shader compile + streaming settle, or the first sample is a lie
            var baseline = await Sample("00 world only, no enemies", 120);

            string[] mix = { "shade", "swarmer", "caster", "archer", "brute", "zapper" };
            int spawned = 0;
            Vector3 c = p.GlobalPosition;
            void SpawnRing(int n)
            {
                for (int i = 0; i < n; i++)
                {
                    float a = spawned * 2.399963f, r = 6f + (i % 5) * 6f;
                    var pos = c + new Vector3(Mathf.Cos(a) * r, 0, Mathf.Sin(a) * r); pos.Y = g.SurfaceHeight(pos, 1e9f);
                    var e = g.SpawnEnemyForTest(mix[spawned % mix.Length], pos); if (e != null) e.WakeSilent(); spawned++;
                }
            }

            double lastMs = baseline.ms; int lastN = 0;
            foreach (int step in new[] { 20, 20, 20 })
            {
                SpawnRing(step);
                await GWait(240);   // let the spawn spike fully decay before sampling — 90 frames was not enough
                var s = await Sample($"{spawned:00} enemies", 150);
                lastMs = s.ms; lastN = spawned;
            }

            // THE decisive test: at a fixed enemy count, collapse the pixel cost. If the frame time barely moves, the
            // GPU was never the wall and no graphics setting will save us. If it drops hard, we're fill-rate bound.
            g.SetUpscaleMode(0);
            g.SetRenderScale(0.5f); await GWait(120);
            var half = await Sample($"{lastN:00} enemies @ renderScale 0.5", 150);
            g.SetRenderScale(0.25f); await GWait(120);
            var quarter = await Sample($"{lastN:00} enemies @ renderScale 0.25", 150);
            g.SetRenderScale(1f); await GWait(90);

            _warnings.Add("---- READING ----");
            _warnings.Add($"world alone {baseline.ms:0.0}ms → +{lastN} enemies {lastMs:0.0}ms  (enemies cost {lastMs - baseline.ms:0.0}ms total, {(lastMs - baseline.ms) / Mathf.Max(1, lastN):0.000}ms each)");
            _warnings.Add($"same {lastN} enemies at quarter pixels: {quarter.ms:0.0}ms (vs {lastMs:0.0}ms full)");
            double gpuShare = lastMs - quarter.ms;
            if (gpuShare < lastMs * 0.25)
                _warnings.Add($"VERDICT: CPU-BOUND. Cutting to 1/4 the pixels only saved {gpuShare:0.0}ms of {lastMs:0.0}ms — the frame is CPU work (draw-call submission / node _Process), so graphics settings can't fix it.");
            else
                _warnings.Add($"VERDICT: GPU/fill-bound at this resolution — {gpuShare:0.0}ms of {lastMs:0.0}ms was pixels.");
            _warnings.Add($"NOTE: measured at {DisplayServer.WindowGetSize().X}x{DisplayServer.WindowGetSize().Y}. CPU cost is resolution-independent, so the CPU figure carries to 4K; the GPU share does NOT (it grows).");
            await Capture("00_perf_cpu");
        }

        // WHERE do the ~1200 empty-world draw calls come from? Two passes that check each other:
        //  (a) a static inventory of every visible VisualInstance3D, bucketed by the subsystem node it hangs under,
        //      counting surfaces AND next-pass chains (an inverted-hull outline doubles a surface's draw calls), then
        //  (b) an EMPIRICAL pass that hides each big bucket and reads the real change in RenderTotalDrawCallsInFrame.
        // The camera is deliberately HELD STILL here — panning changes what's culled and would make deltas unreadable.
        private async Task DrawCallAudit()
        {
            var g = Game.I; var p = g?.Player; if (p == null) { _errors.Add("no Player"); return; }
            Engine.MaxFps = 0;
            DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
            g.NoSpawn = true; g.ClearEnemies();
            void Guard() { if (g.State != GameState.Playing) { g.State = GameState.Playing; Input.MouseMode = Input.MouseModeEnum.Captured; } p.Hp = 100000f; }
            async Task GWait(int n) { for (int i = 0; i < n; i++) { await NextFrame(); _frame++; Guard(); } }
            double Median(List<double> v) { if (v.Count == 0) return 0; v.Sort(); return v[v.Count / 2]; }
            async Task<(double draws, double fps)> Read(int frames = 60)
            {
                var d = new List<double>(); var f = new List<double>();
                for (int i = 0; i < frames; i++)
                {
                    await NextFrame(); _frame++; Guard();
                    d.Add(Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame));
                    double fp = Engine.GetFramesPerSecond(); if (fp > 0) f.Add(fp);
                }
                return (Median(d), Median(f));
            }

            await GWait(200);   // warm up: shaders + streaming, and let the camera settle
            var base0 = await Read(90);
            _warnings.Add($"BASELINE (empty world, camera still): {base0.draws:0} draw calls, {base0.fps:0} fps @ {DisplayServer.WindowGetSize().X}x{DisplayServer.WindowGetSize().Y}");

            // ---------- (a) static inventory ----------
            var buckets = new Dictionary<string, int[]>();   // name → [meshNodes, surfaces, passes(incl next_pass), multimeshNodes, particles]
            var bucketNodes = new Dictionary<string, List<Node3D>>();   // so the empirical pass can hide the whole bucket
            int outlinedSurfaces = 0, totalSurfaces = 0;
            void Bump(string k, int idx, int by) { if (!buckets.TryGetValue(k, out var a)) buckets[k] = a = new int[5]; a[idx] += by; }
            int PassCount(Material m) { int n = 0; while (m != null && n < 8) { n++; m = m.NextPass; } return Mathf.Max(1, n); }
            // GetActiveMaterial only exists on MeshInstance3D; resolve the effective material by hand for the rest
            Material MatOf(GeometryInstance3D gi, Mesh mesh, int s)
            {
                if (gi.MaterialOverride != null) return gi.MaterialOverride;
                if (gi is MeshInstance3D m3) { var so = m3.GetSurfaceOverrideMaterial(s); if (so != null) return so; }
                return mesh?.SurfaceGetMaterial(s);
            }
            void Walk(Node n, string bucket)
            {
                foreach (var ch in n.GetChildren())
                {
                    // bucket by the C# CLASS of the top-level node under Game — these are added as `AddChild(new Haunt())`
                    // etc., so the auto-generated node names (@Node3D@1353) are meaningless but the type is not.
                    string b = bucket;
                    if (b == null)
                    {
                        string tn = ch.GetType().Name;
                        b = tn == "Node3D" || tn == "Node" ? ch.Name.ToString() : tn;
                        if (ch is Node3D top) { if (!bucketNodes.TryGetValue(b, out var l)) bucketNodes[b] = l = new List<Node3D>(); l.Add(top); }
                    }
                    if (ch is Node3D n3 && !n3.IsVisibleInTree()) { continue; }   // hidden subtrees cost nothing
                    if (ch is MeshInstance3D mi && mi.Mesh != null)
                    {
                        int sc = mi.Mesh.GetSurfaceCount();
                        int passes = 0, outlined = 0;
                        for (int s = 0; s < sc; s++)
                        {
                            var mat = mi.GetActiveMaterial(s);
                            int pc = PassCount(mat);
                            passes += pc;
                            if (pc > 1) outlined++;
                        }
                        Bump(b, 0, 1); Bump(b, 1, sc); Bump(b, 2, passes);
                        totalSurfaces += sc; outlinedSurfaces += outlined;
                    }
                    else if (ch is MultiMeshInstance3D mmi && mmi.Multimesh?.Mesh != null)
                    {
                        int sc = mmi.Multimesh.Mesh.GetSurfaceCount();
                        int passes = 0;
                        for (int s = 0; s < sc; s++) passes += PassCount(MatOf(mmi, mmi.Multimesh.Mesh, s));
                        Bump(b, 3, 1); Bump(b, 1, sc); Bump(b, 2, passes);
                        totalSurfaces += sc;
                    }
                    else if (ch is GpuParticles3D) Bump(b, 4, 1);
                    Walk(ch, b);
                }
            }
            Walk(g, null);

            var rows = new List<(string name, int[] a)>();
            foreach (var kv in buckets) rows.Add((kv.Key, kv.Value));
            rows.Sort((x, y) => y.a[2].CompareTo(x.a[2]));
            _warnings.Add("---- INVENTORY (visible only; 'passes' = surfaces x next_pass chain = the real submit count) ----");
            for (int i = 0; i < rows.Count && i < 14; i++)
            {
                var a = rows[i].a;
                if (a[2] == 0 && a[4] == 0) continue;
                _warnings.Add($"  {rows[i].name}: passes {a[2]} (surfaces {a[1]}) · meshNodes {a[0]} · multimesh {a[3]}{(a[4] > 0 ? $" · particles {a[4]}" : "")}");
            }
            _warnings.Add($"  TOTAL visible surfaces {totalSurfaces}, of which {outlinedSurfaces} carry an extra next_pass (outline) = {outlinedSurfaces} bonus draw calls");

            // ---------- (b) the outline tax, measured ----------
            // Game.Toon() attaches an inverted-hull Outline() as next_pass by default. The art direction is "painterly,
            // NO ink outlines" — so if these are still rendering, every one is a wasted second submit of the same mesh.
            var stripped = new List<(Material mat, Material next)>();
            void Strip(Node n)
            {
                foreach (var ch in n.GetChildren())
                {
                    if (ch is GeometryInstance3D gi)
                    {
                        Mesh msh = gi is MeshInstance3D m2 ? m2.Mesh : gi is MultiMeshInstance3D mm ? mm.Multimesh?.Mesh : null;
                        int sc = msh != null ? msh.GetSurfaceCount() : 0;
                        for (int s = 0; s < sc; s++)
                        {
                            var mat = MatOf(gi, msh, s);
                            if (mat != null && mat.NextPass != null) { stripped.Add((mat, mat.NextPass)); mat.NextPass = null; }
                        }
                    }
                    Strip(ch);
                }
            }
            Strip(g);
            await GWait(30);
            var noOutline = await Read(90);
            _warnings.Add($"---- OUTLINE PASS STRIPPED: {noOutline.draws:0} draws ({noOutline.draws - base0.draws:+0;-0} vs baseline), {noOutline.fps:0} fps ({noOutline.fps - base0.fps:+0;-0}) · {stripped.Count} materials had one");
            foreach (var (mat, next) in stripped) if (GodotObject.IsInstanceValid(mat)) mat.NextPass = next;   // restore
            await GWait(30);

            // ---------- (c) hide each big subsystem and measure the REAL delta ----------
            _warnings.Add("---- MEASURED PER-SUBSYSTEM (hide it, read the change) ----");
            int probed = 0;
            foreach (var (name, a) in rows)
            {
                if (probed >= 10) break;
                if (a[2] < 8 && a[4] == 0) continue;
                if (!bucketNodes.TryGetValue(name, out var nodes) || nodes.Count == 0) continue;
                var hidden = new List<Node3D>();
                foreach (var nd in nodes) if (GodotObject.IsInstanceValid(nd) && nd.Visible) { nd.Visible = false; hidden.Add(nd); }
                if (hidden.Count == 0) continue;
                // NO Capture() in this loop: a 4K GetImage() readback stalls the pipeline and poisons the NEXT fps
                // sample (it read "hiding a chest costs 72fps"). Settle generously instead — GetFramesPerSecond is a
                // smoothed counter and needs time to reflect the new steady state.
                await GWait(90);
                var off = await Read(90);
                foreach (var nd in hidden) if (GodotObject.IsInstanceValid(nd)) nd.Visible = true;
                await GWait(90);
                // fps delta is the one that matters — a bucket can own hundreds of draw calls and cost nothing
                _warnings.Add($"  hiding {name} (x{hidden.Count}): draws {base0.draws:0}→{off.draws:0} ({base0.draws - off.draws:+0;-0}) · FPS {base0.fps:0}→{off.fps:0} ({off.fps - base0.fps:+0;-0})");
                probed++;
            }
            // re-read the baseline at the END: if it drifted from the opening read, the per-subsystem deltas above are
            // measured against a moving target and shouldn't be trusted
            var base1 = await Read(90);
            _warnings.Add($"BASELINE RECHECK: {base1.draws:0} draws, {base1.fps:0} fps (opened at {base0.draws:0}/{base0.fps:0}) — deltas are only meaningful if these agree");
            await Capture("00_world");
        }

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
            foreach (var lf in new[] { "leaf_a", "leaf_b", "leaf_c" })   // (LAYFLAT CHECK) a flat leaf must have its THINNEST axis vertical (Y) — else it stands on edge
            {
                var sz = PropGlb.GetMesh(lf).GetAabb().Size;
                bool flat = sz.Y <= sz.X && sz.Y <= sz.Z;
                _warnings.Add($"LEAF {lf} aabb=({sz.X:0.00},{sz.Y:0.00},{sz.Z:0.00}) thinAxis={(flat ? "Y (flat ✓)" : "NOT Y — stands on edge!")}");
                if (!flat) _errors.Add($"{lf} does not lie flat: thinnest axis is not vertical (aabb {sz.X:0.00},{sz.Y:0.00},{sz.Z:0.00})");
            }
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
            // (WIND GUST) force several downwind leaf gusts in front of the camera so the tumbling-leaf color reads (should be warm autumn, not white)
            for (int i = 0; i < 5; i++) { Game.I.SpawnWindGust(center); await WaitFrames(3); }
            await WaitFrames(8);  await Capture("02_windgust");
            await WaitFrames(14); await Capture("03_windgust_mid");
        }

        // NERFER shrine: the reworked single-shrine loop. Verifies (a) the top-left HUD toll line + hold-E prompt, (b) the
        // escalating soul cost, (c) the Summoning's stand-in-the-circle gate — the countdown MUST freeze when you walk out and
        // resume when you walk back in (the bug being fixed), and (d) that a SPENT shrine drops off the minimap.
        private async Task NerfShrineScenario()
        {
            var g = Game.I; var p = g?.Player; if (p == null) { _errors.Add("no Player"); return; }
            g.NoSpawn = true; g.ClearEnemies();
            await WaitFrames(40);

            // --- toll escalation is pure arithmetic; assert it before touching the world ---
            g.DebugPlaceNerfer(NerfKind.Sanctuary, p.GlobalPosition + new Vector3(0, 0, 6f), 0);
            int c0 = g.NerferCostEach;
            g.DebugPlaceNerfer(NerfKind.Sanctuary, p.GlobalPosition + new Vector3(0, 0, 6f), 1);
            int c1 = g.NerferCostEach;
            g.DebugPlaceNerfer(NerfKind.Sanctuary, p.GlobalPosition + new Vector3(0, 0, 6f), 2);
            int c2 = g.NerferCostEach;
            _warnings.Add($"TOLL/warden: use0={c0} use1={c1} use2={c2} (expect 100/200/400)");
            if (c0 != 100 || c1 != 200 || c2 != 400) _errors.Add($"soul toll doesn't double: {c0}/{c1}/{c2}");

            // lift the fog wide (map pins are fog-gated) and park one foe far away so the boot INTERMISSION overlay stops
            // painting over the middle of the frame — we need that band clear to judge the Summoning timer.
            g.DebugRevealAround(p.GlobalPosition, 260f);
            Vector3 fwd = -p.GlobalTransform.Basis.Z; fwd.Y = 0f; fwd = fwd.Normalized();
            var farFoe = p.GlobalPosition + fwd * 420f; farFoe.Y = g.SurfaceHeight(farFoe, 1e9f);
            g.SpawnEnemyForTest("shade", farFoe);

            // --- (1) a DORMANT Summoner shrine ahead: read the whole thing, its minimap pin, and the top-left toll line ---
            // NOTE: DebugPlaceNerfer drops the shrine on a RAW point (only Y is grounded) — it may land in water here. Real
            // placement goes through SpreadPointInWorld; the wet framing is a harness artifact, not a placement bug.
            Vector3 spot = p.GlobalPosition + fwd * 12f;
            var sh = g.DebugPlaceNerfer(NerfKind.Summoner, spot, 0);
            g.Souls = 100;
            await WaitFrames(40);
            await Capture("00_shrine_view");

            // step into interaction reach (NerfShrine.Radius) so the hold-E prompt + soul price show
            Vector3 near = sh.GlobalPosition - fwd * 3f;
            p.GlobalPosition = new Vector3(near.X, g.SurfaceHeight(near, 1e9f) + 0.2f, near.Z);
            await WaitFrames(20);
            await Capture("01_dormant_prompt");
            _warnings.Add($"DORMANT: hud='{g.NerferHudLine()}' state={sh.State} souls={g.Souls} cost={g.NerferCostEach} holdE={g.HoldEActive} prompt='{g.HoldEPrompt}'");
            if (!g.HoldEActive) _errors.Add("in reach of a dormant shrine but no hold-E action offered");

            // --- (2) pay the toll → the Summoning begins; stand IN the circle and let it tick ---
            g.DebugPayNerfer();
            await WaitFrames(20);
            float tA = g.SummonerTimeLeft; bool heldA = g.SummonerHeld;
            await WaitFrames(60);                     // ~1s inside the ward
            float tB = g.SummonerTimeLeft;
            await Capture("02_inside_ticking");
            _warnings.Add($"INSIDE: state={sh.State} held={heldA}->{g.SummonerHeld} t {tA:0.00}->{tB:0.00} (must DROP)");
            if (sh.State != 1) _errors.Add($"paying the toll didn't start the Summoning (state={sh.State})");
            if (!g.SummonerHeld) _errors.Add("standing in the circle but SummonerHeld=false");
            if (tB >= tA - 0.4f) _errors.Add($"countdown didn't run while held ({tA:0.00}->{tB:0.00})");

            // --- (3) THE FIX: back well outside the ward (still facing it) → the countdown must FREEZE ---
            Vector3 far = sh.GlobalPosition - fwd * (NerfShrine.WardRadius + 20f);
            p.GlobalPosition = new Vector3(far.X, g.SurfaceHeight(far, 1e9f) + 0.2f, far.Z);
            await WaitFrames(20);
            float tC = g.SummonerTimeLeft;
            await WaitFrames(90);                     // ~1.5s stood well outside
            float tD = g.SummonerTimeLeft;
            await Capture("03_outside_frozen");
            _warnings.Add($"OUTSIDE: held={g.SummonerHeld} t {tC:0.00}->{tD:0.00} (must be FROZEN)");
            if (g.SummonerHeld) _errors.Add("outside the ward but SummonerHeld=true");
            if (Mathf.Abs(tD - tC) > 0.05f) _errors.Add($"BUG: countdown kept running with nobody in the circle ({tC:0.00}->{tD:0.00})");

            // --- (4) walk back in → it resumes ---
            Vector3 back = sh.GlobalPosition - fwd * 6f;
            p.GlobalPosition = new Vector3(back.X, g.SurfaceHeight(back, 1e9f) + 0.2f, back.Z);
            await WaitFrames(20);
            float tE = g.SummonerTimeLeft;
            await WaitFrames(60);
            float tF = g.SummonerTimeLeft;
            await Capture("04_back_inside_resumed");
            _warnings.Add($"RESUME: held={g.SummonerHeld} t {tE:0.00}->{tF:0.00} (must DROP again)");
            if (tF >= tE - 0.4f) _errors.Add($"countdown didn't resume after re-entering ({tE:0.00}->{tF:0.00})");

            // --- (5) a SPENT (armed) shrine: HUD flips to "spent" and its minimap pin is gone ---
            sh.SetState(2);
            Vector3 away = sh.GlobalPosition - fwd * 26f;
            p.GlobalPosition = new Vector3(away.X, g.SurfaceHeight(away, 1e9f) + 0.2f, away.Z);
            await WaitFrames(24);
            await Capture("05_spent_no_minimap_pin");
            _warnings.Add($"SPENT: state={sh.State} hud='{g.NerferHudLine()}' uses={g.NerferUses}");

            // --- (6) minimap gale-pad clamping: pads must NOT ring the rim any more ---
            _warnings.Add($"GALEPADS in world={g.GalePads.Count} (rim should be clear of pad ticks in every capture)");
        }

        // CRIMSON RITE (Sacrifice nerfer payoff): stand up a real boss fight, light the blood sigil(s), watch the pentagram
        // draw itself over the boss, then verify the detonation's rules — every non-boss foe dies (INCLUDING the special
        // Taker), the boss and the miniboss SURVIVE, and spawns stall for 20s/warden.
        private async Task CrimsonRiteScenario()
        {
            var g = Game.I; var p = g?.Player; if (p == null) { _errors.Add("no Player"); return; }
            g.NoSpawn = true; g.ClearEnemies();
            p.GodMode = true;                              // a live boss + horde would otherwise flatten her mid-scenario
            await WaitFrames(40);
            g.DebugRevealAround(p.GlobalPosition, 260f);

            Vector3 fwd = -p.GlobalTransform.Basis.Z; fwd.Y = 0f; fwd = fwd.Normalized();
            Vector3 arena = p.GlobalPosition + fwd * 34f;
            g.DebugArmCrimsonRite(arena, 26);
            await WaitFrames(45);
            int addsBefore = g.DebugAliveCount(false), bossBefore = g.DebugAliveCount(true);
            await Capture("00_sigils_open");
            _warnings.Add($"OPEN: sigils={g.RiteTotal} lit={g.RiteLit} riteOpen={g.RiteOpen} adds={addsBefore} bossTier={bossBefore}");
            if (g.RiteTotal < 1) _errors.Add("no rite sigils spawned");
            if (bossBefore < 2) _errors.Add($"expected boss + miniboss alive, got {bossBefore}");

            // --- light every sigil by standing in each for its fill time (any warden, any sigil) ---
            int guard = 0;
            while (g.RiteLit < g.RiteTotal && guard++ < 8)
            {
                var sg = g.DebugFirstUnlitSigil(); if (sg == null) break;
                var stand = sg.GlobalPosition;
                p.GlobalPosition = new Vector3(stand.X, g.SurfaceHeight(stand, 1e9f) + 0.2f, stand.Z);
                await WaitFrames(30);
                await Capture($"01_charging_{guard}");   // mid-fill: rim teeth + minimap arc should be partly lit
                _warnings.Add($"CHARGING sigil#{guard}: charge={sg.Charge:0.00} lit={sg.Lit}");
                int f2 = 0;
                for (; f2 < 400 && !sg.Lit; f2++) { p.GlobalPosition = new Vector3(stand.X, g.SurfaceHeight(stand, 1e9f) + 0.2f, stand.Z); await NextFrame(); _frame++; }
                if (!sg.Lit)
                {
                    _errors.Add($"sigil {guard} never lit ({f2} frames, charge={sg.Charge:0.00}, state={g.State}, downed={p.Downed}, grabbed={p.GrabbedBy}, dist={p.GlobalPosition.DistanceTo(sg.GlobalPosition):0.0})");
                    break;
                }
            }
            // BeginRiteDraw clears the sigil set the instant it fires, so RiteTotal is 0 here by design — the fact that the
            // pentagram is drawing IS the proof every sigil lit.
            _warnings.Add($"LIT: drawing={g.RiteDrawing} (sigils cleared on fire, so Rite{{Lit,Total}} read 0)");
            if (!g.RiteDrawing) { _errors.Add("all sigils lit but the pentagram never started drawing"); return; }

            // --- back off AND up so the whole 52u figure is in frame (pinned each frame against gravity) ---
            Vector3 view = arena - fwd * 52f;
            void Perch() { p.GlobalPosition = new Vector3(view.X, g.SurfaceHeight(view, 1e9f) + 26f, view.Z); }
            // Pace these by the DRAW's own progress, not by frame counts — this scene runs 40-110fps depending on horde size,
            // so a fixed frame count landed all three captures inside the first second of a 3.4s draw.
            async Task PerchUntil(float prog, int maxFrames)
            {
                for (int i = 0; i < maxFrames && g.RiteDrawing && g.RiteDrawProgress < prog; i++) { Perch(); await NextFrame(); _frame++; }
            }
            Perch();
            await PerchUntil(0.30f, 900); await Capture("02_pentagram_early");   // circle sweeping
            await PerchUntil(0.65f, 900); await Capture("03_pentagram_mid");     // chords striking
            await PerchUntil(0.97f, 900); await Capture("04_pentagram_full");    // figure closed / about to dispel
            _warnings.Add($"DRAW: progress at final capture={g.RiteDrawProgress:0.00} (want ~1.0)");

            // the draw is a wall-clock timer (DrawDur), and screenshot readbacks stretch wall time unpredictably — so wait on
            // the ACTUAL detonation rather than a frame count, then let the metered kill queue drain.
            bool fired = await WaitUntil(() => g.SpawnStalled, 900);
            if (!fired) { _errors.Add("the pentagram never detonated"); return; }
            await WaitFrames(30);   // kill queue drains at 8/frame
            int addsAfter = g.DebugAliveCount(false), bossAfter = g.DebugAliveCount(true);
            await Capture("05_after_detonation");
            _warnings.Add($"AFTER: adds {addsBefore}->{addsAfter} (want 0) · bossTier {bossBefore}->{bossAfter} (want unchanged) · stall={g.SpawnStallT:0.0}s");
            if (addsAfter != 0) _errors.Add($"{addsAfter} non-boss foes survived the rite (expected 0)");
            if (bossAfter != bossBefore) _errors.Add($"boss-tier foes died to the rite: {bossBefore}->{bossAfter} (boss + miniboss must survive)");
            if (g.SpawnStallT < 15f) _errors.Add($"spawn stall too short: {g.SpawnStallT:0.0}s (expect ~20s solo, minus drain time)");

            // --- the silence: nothing new may arrive while the stall runs ---
            // 30 kills pops the level-up pick-3, which soft-pauses the sim — so the stall would sit frozen and "0 spawned"
            // would be a FALSE pass. Force Playing every frame so the directors actually run against the stall.
            g.NoSpawn = false;                       // re-arm the real spawn path; the stall alone must hold the line
            float stallAtStart = g.SpawnStallT;
            for (int i = 0; i < 240; i++)
            {
                if (g.State != GameState.Playing) { g.State = GameState.Playing; Input.MouseMode = Input.MouseModeEnum.Captured; }
                await NextFrame(); _frame++;
            }
            int during = g.DebugAliveCount(false);
            await Capture("06_silence_countdown");
            _warnings.Add($"SILENCE: spawned during stall={during} (want 0) · stall {stallAtStart:0.0}s -> {g.SpawnStallT:0.0}s (must TICK DOWN)");
            if (during != 0) _errors.Add($"{during} foes spawned during the silence — a director isn't gated on SpawnStallT");
            if (g.SpawnStallT >= stallAtStart - 0.5f) _errors.Add($"the silence timer isn't running ({stallAtStart:0.0} -> {g.SpawnStallT:0.0})");

            // --- and once it lapses, the world starts sending foes again ---
            // The countdown itself is just `SpawnStallT -= dt` and it's already proven to tick; sitting through the remaining
            // ~17s would blow the global frame budget, so fast-forward the clock. What's actually under test here is that no
            // director stays wedged off once the stall clears.
            g.SpawnStallT = 0.2f;
            for (int i = 0; i < 200; i++)
            {
                if (g.State != GameState.Playing) g.State = GameState.Playing;
                await NextFrame(); _frame++;
            }
            int after = g.DebugAliveCount(false);
            await Capture("07_spawns_resume");
            _warnings.Add($"RESUMED (lapse fast-forwarded): stalled={g.SpawnStalled} adds back={after} (want >0 — no director may stay wedged off)");
            if (g.SpawnStalled) _errors.Add("the silence never lapsed");
            else if (after == 0) _errors.Add("spawns never resumed after the silence — SpawnStallT left a director wedged off");
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

        // Force the GRAFTED taker action clips (standup/fall/climb/run) onto the SMALL goblin rig (and a swarmer) — these were
        // deforming before the per-rig translation retarget, since the taker rig is authored ~18% taller with different bone rests.
        private async Task GraftRetarget()
        {
            var p = Game.I?.Player; if (p == null) { _errors.Add("no Player"); return; }
            Game.I.NoSpawn = true; Game.I.ClearEnemies();
            await WaitFrames(20);
            Vector3 fwd = -p.GlobalTransform.Basis.Z; fwd.Y = 0f; fwd = fwd.Normalized();
            Vector3 pin = p.GlobalPosition + fwd * 6f;      // small goblin — frame it close
            Game.I.SpawnEnemyForTest("shade", pin);          // shade → CreatureKind.Goblin (grafts the taker action clips)
            await WaitFrames(40);
            var g = FindGoblin();
            if (g == null) { _errors.Add("no goblin spawned"); return; }
            _warnings.Add($"GOBLIN clips={g.DebugClipCount} (grafted + retargeted to the small rig)");

            g.DebugBiped("walk");  await PinHold(g, pin, 20); await Capture("00_gob_walk");     // own clip (control)
            g.DebugBiped("run");   await PinHold(g, pin, 20); await Capture("01_gob_run");      // grafted
            g.DebugBiped("climb"); await PinHold(g, pin, 24); await Capture("02_gob_climb");    // grafted
            g.DebugBipedStart("fall"); await PinHold(g, pin, 16); await Capture("03_gob_fall"); // grafted
            g.DebugBipedStart("standup"); await PinHold(g, pin, 6);  await Capture("04_gob_standup_a");   // grafted — the reported deform
            await PinHold(g, pin, 28); await Capture("05_gob_standup_b");
        }

        // PERF AUDIT — measure true render cost (uncapped, no vsync) at the window's native res. Warms up FIRST (compiles the VFX
        // shaders + loads textures so no one-time stall pollutes a sample), then: (1) isolates WORLD render vs full COMBAT load,
        // and (2) sweeps each graphics lever — including 3D RENDER SCALE (the fill-rate lever, since there's no in-game option yet).
        private async Task PerfHaunt()
        {
            var g = Game.I; var p = g?.Player; if (p == null) { _errors.Add("no Player"); return; }
            Engine.MaxFps = 0;
            DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
            void SetScale(float s) { g.SetRenderScale(s); }   // route through the REAL shipping API (validates SetRenderScale end-to-end)
            void High() { g.SetGfxQuality(2); g.SetTextureQuality(2); g.UpscaleMode = 0; SetScale(1f); }

            g.NoSpawn = true; g.ClearEnemies();
            // GUARD: kills/XP would pop the level-up pick-3 (soft-pausing the sim → junk fps), and 60 foes would down the player.
            // Force Playing + keep her topped up every frame so nothing interrupts the sweep.
            // GUARD also PANS the camera every frame (mouse-look) so the frustum sweeps the whole scene — a static view culls most
            // of the world and reports optimistically. A continuous ~360° sweep = representative overdraw / draw-call / streaming load.
            void Guard()
            {
                if (g.State != GameState.Playing) { g.State = GameState.Playing; Input.MouseMode = Input.MouseModeEnum.Captured; }
                p.Hp = 100000f;
                Input.ParseInputEvent(new InputEventMouseMotion { Relative = new Vector2(11f, 0f) });   // continuous yaw pan
            }
            async Task GWait(int n) { for (int i = 0; i < n; i++) { await NextFrame(); _frame++; Guard(); } }
            async Task<string> GSample(int n) { float lo = 1e9f, sum = 0f; int cnt = 0; for (int i = 0; i < n; i++) { await NextFrame(); _frame++; Guard(); float f = (float)Engine.GetFramesPerSecond(); if (f > 0) { lo = Mathf.Min(lo, f); sum += f; cnt++; } } return $"min={lo:0} avg={(cnt > 0 ? sum / cnt : 0):0}"; }
            await GWait(40);
            Vector3 c = p.GlobalPosition;
            High();
            var res = DisplayServer.WindowGetSize();

            string[] mix = { "shade", "swarmer", "caster", "archer", "brute", "zapper" };
            int spawned = 0;
            void SpawnRing(int n)
            {
                for (int i = 0; i < n; i++)
                {
                    float a = spawned * 2.399963f, r = 6f + (i % 5) * 6f;
                    var pos = c + new Vector3(Mathf.Cos(a) * r, 0, Mathf.Sin(a) * r); pos.Y = g.SurfaceHeight(pos, 1e9f);
                    var e = g.SpawnEnemyForTest(mix[spawned % mix.Length], pos); if (e != null) e.WakeSilent(); spawned++;
                }
            }
            async Task<string> Measure(string tag) { string f = await GSample(90); _warnings.Add($"PERF {tag}: {f}"); return f; }

            // ---- (1) WORLD-ONLY: just the streamed overworld at High, and at reduced render scale → is fill-rate the wall? ----
            await GWait(150);                       // long warmup: compile shaders + settle streaming
            await Measure($"[{res.X}x{res.Y}] world_only HIGH rs1.0");
            SetScale(0.75f); await GWait(70); await Measure($"[{res.X}x{res.Y}] world_only HIGH rs0.75");
            SetScale(0.5f); await GWait(70); await Measure($"[{res.X}x{res.Y}] world_only HIGH rs0.5");
            High(); await GWait(70);

            // ---- (2) full COMBAT load: haunt VFX + 60 enemies + firing spells ----
            g.SpawnHaunt(c); await GWait(20);
            SpawnRing(60); await GWait(60);
            Input.ActionPress("cast"); await GWait(120);   // warm the spell/VFX shaders under the real load

            var configs = new System.Collections.Generic.List<(string name, System.Action apply)>
            {
                ("HIGH",         () => High()),
                ("ULTRA_SSIL",   () => { g.SetGfxQuality(3); g.SetTextureQuality(2); SetScale(1f); }),   // Ultra now enables SSIL → should be slower than High
                ("rs0.67_FSR2",  () => { High(); g.SetUpscaleMode(2); g.SetRenderScale(0.67f); }),        // DLSS-equivalent temporal upscale
                ("rs0.67_Bilin", () => { High(); g.SetUpscaleMode(0); g.SetRenderScale(0.67f); }),
                ("rs0.75",       () => { High(); SetScale(0.75f); }),
                ("rs0.5",        () => { High(); SetScale(0.5f); }),
                ("minus_SSIL",   () => { High(); g.GfxSsil = false; g.ApplyGraphics(); }),
                ("minus_SSAO",   () => { High(); g.GfxSsao = false; g.ApplyGraphics(); }),
                ("minus_Bloom",  () => { High(); g.GfxBloom = false; g.ApplyGraphics(); }),
                ("Shadow_Low",   () => { High(); g.SetShadowQuality(0); }),
                ("all_post_off", () => { High(); g.GfxSsil = false; g.GfxSsao = false; g.GfxBloom = false; g.ApplyGraphics(); g.SetShadowQuality(0); }),
                ("LOW_preset",   () => { g.SetGfxQuality(0); g.SetTextureQuality(0); SetScale(1f); }),
                ("LOW+rs0.5",    () => { g.SetGfxQuality(0); g.SetTextureQuality(0); SetScale(0.5f); }),
            };
            foreach (var cfg in configs)
            {
                cfg.apply();
                await GWait(110);                 // generous settle so pipeline/texture/FSR2-init stalls don't land in the sample
                await Measure($"[{res.X}x{res.Y}] combat60 {cfg.name}");
            }
            Input.ActionRelease("cast");
            High();
            await GWait(10); await Capture("00_perf_done");   // one capture so the harness marks the run complete
        }

        // Verify the new cross-witch CONDUIT state: any producer can brand a foe (MarkConduit) → it reads ArcaneMarked (feeds
        // ArcaneBlast "Cataclysm") → it self-expires without the Arcane-witch mark manager.
        // FORSAKEN DOOM REWORK — the whole loop end to end, on real input where the game allows it.
        // Proves, in order: the beam banks and the fuse refreshes · Focus survives a target SWITCH (it rides on her, not
        // the target) · a charged release detonates a slice and re-fuses the rest · an untouched fuse goes off on its own ·
        // Danse Macabre turns a pack on itself (including a ranged foe shooting the wrong way) · Rout scatters ·
        // and the execute fires the instant the bank covers a foe's remaining HP, leaving a walking corpse behind.
        private async Task ForsakenDoom()
        {
            var g = Game.I; var p = g?.Player; if (p == null) { _errors.Add("no Player"); return; }
            g.NoSpawn = true; g.ClearEnemies();
            await WaitFrames(30);
            p.ToggleThirdPersonPlay();
            await WaitFrames(60);   // let grounding + the post-spawn structure settle finish before anything is framed

            Vector3 fwd = -p.GlobalTransform.Basis.Z; fwd.Y = 0f; fwd = fwd.Normalized();
            Vector3 right = new Vector3(fwd.Z, 0f, -fwd.X);
            Vector3 c = p.GlobalPosition + fwd * 11f;

            // ---- 1. the channel banks, and the label reads ----
            var a = g.SpawnEnemyForTest("brute", c);                 // tanky enough to survive a long channel
            var b = g.SpawnEnemyForTest("brute", c + right * 5f);
            await WaitFrames(10);
            if (a == null || b == null) { _errors.Add("no enemies spawned"); return; }
            if (a.Doomed) _errors.Add("enemy spawned already carrying Doom");

            Input.ActionPress("cast");
            await WaitFrames(240);                                    // ~4s of channel → Focus should be at its ceiling
            float bank1 = a.DoomBank, focus1 = p.DoomFocus;
            int chained = 0; foreach (var e2 in g.Enemies) if (e2 != null && !e2.Dead && e2 != a && e2.Doomed) chained++;
            _warnings.Add($"channel 4s: bank={bank1:0.0} fuse={a.DoomT:0.00} focus={focus1:0.00} chained-onto-others={chained} (expect 0 — the beam must NOT auto-chain)");
            if (chained > 0) _errors.Add($"the channel silently spread Doom to {chained} foe(s) it was never aimed at");
            if (bank1 <= 0f) _errors.Add("the channel banked NO Doom");
            if (focus1 < 2.0f) _errors.Add($"Focus never wound up (={focus1:0.00}, expect ~2.5)");
            await Capture("01_channel_banking");

            // ---- 2. Focus rides on HER: switching targets must not reset the wind-up ----
            if (b == null || !GodotObject.IsInstanceValid(b) || b.Dead) { _errors.Add("switch target died before the Focus check"); return; }
            p.LookAtForTest(b.GlobalPosition);
            await WaitFrames(45);
            float focus2 = p.DoomFocus;
            _warnings.Add($"after target switch: focus={focus2:0.00} (expect still high — the ramp is on her, not the foe)");
            if (focus2 < 2.0f) _errors.Add($"Focus dropped on a target SWITCH (={focus2:0.00}) — it must only decay when she stops firing");
            Input.ActionRelease("cast");
            await Capture("02_focus_across_switch");

            // ---- 3. charged release detonates a SLICE and re-fuses the remainder ----
            g.ClearEnemies();
            await WaitFrames(20);
            a = g.SpawnEnemyForTest("brute", c);   // a FRESH target: the first one has usually fused itself to death by now,
            await WaitFrames(15);                  // and reading a freed Enemy's GlobalPosition throws ObjectDisposedException
            if (a == null || !GodotObject.IsInstanceValid(a)) { _errors.Add("no charge-test target"); return; }
            p.LookAtForTest(a.GlobalPosition);
            Input.ActionPress("cast");
            await WaitFrames(150);            // re-bank first — by now the earlier bank has long since fused off
            Input.ActionRelease("cast");
            await WaitFrames(10);
            float before = a.DoomBank;
            _warnings.Add($"pre-charge bank={before:0.0} (needs to be well above 0 for this test to mean anything)");
            if (before < 5f) _errors.Add($"could not re-bank before the charge test (bank={before:0.0})");
            Input.ActionPress("charge");
            await WaitFrames(45);                                     // a real but partial hold → a slice, not the whole bank
            Input.ActionRelease("charge");
            await WaitFrames(30);
            _warnings.Add($"tap-release: bank {before:0.0} → {a.DoomBank:0.0} (expect lower, and NOT zero — the rest stays banked)");
            if (a.DoomBank >= before) _errors.Add("a charged release did not spend any of the bank");
            if (before > 20f && a.DoomBank <= 0.01f) _errors.Add("a TAP spent the whole bank — partial release must leave the remainder");
            await Capture("03_partial_detonation");

            // ---- 3b. a FULL charge detonates AND seeds the neighbours (the crush is the spreader now) ----
            g.ClearEnemies();
            await WaitFrames(20);
            var core = g.SpawnEnemyForTest("brute", c);
            await WaitFrames(15);
            if (core == null || !GodotObject.IsInstanceValid(core)) { _errors.Add("no spread-test core"); return; }
            p.LookAtForTest(core.GlobalPosition);
            Input.ActionPress("cast");
            await WaitFrames(150);
            Input.ActionRelease("cast");
            await WaitFrames(10);
            // Spawn the neighbours only NOW, around where the core actually ended up. Spawning them up front let the whole
            // group walk at her during the channel and bunch, so the reticle (and therefore the crush) landed on whichever
            // brute got closest — not the one carrying the bank. Re-aim at the core for the same reason.
            for (int i = 0; i < 3; i++) g.SpawnEnemyForTest("brute", core.GlobalPosition + right * (i * 2.4f - 2.4f) + fwd * 2.6f);
            await WaitFrames(8);
            p.LookAtForTest(core.GlobalPosition);
            await WaitFrames(6);
            int seededBefore = 0; foreach (var e2 in g.Enemies) if (e2 != null && !e2.Dead && e2 != core && e2.Doomed) seededBefore++;
            float coreBefore = core.DoomBank;
            Input.ActionPress("charge");
            await WaitFrames(40);
            float chargeMid = p.ChargeAmt; bool chargingMid = p.Charging; bool doomedMid = core.Doomed;
            await WaitFrames(40);                                     // a FULL charge
            float chargePeak = p.ChargeAmt;
            _warnings.Add($"charge probe: mid={chargeMid:0.00} charging={chargingMid} coreDoomed={doomedMid} peak={chargePeak:0.00}");
            p.LookAtForTest(core.GlobalPosition);   // it WALKS at her while you hold the charge — a reticle aimed a second
            await WaitFrames(2);                     // ago points past it at whatever is behind, which is what we were crushing
            Input.ActionRelease("charge");
            await WaitFrames(30);
            int seededAfter = 0; foreach (var e2 in g.Enemies) if (e2 != null && !e2.Dead && e2 != core && e2.Doomed) seededAfter++;
            _warnings.Add($"full-charge spread: core bank {coreBefore:0.0} -> {core.DoomBank:0.0} (must DROP = it detonated), neighbours doomed {seededBefore} -> {seededAfter}, charge={p.ChargeAmt:0.00}, mana={p.Mana:0.0}");
            if (coreBefore > 1f && core.DoomBank >= coreBefore - 0.01f) _errors.Add("the charged release never detonated the core at all");
            if (seededAfter <= seededBefore) _errors.Add("a full-charge detonation seeded nobody — the crush is supposed to BE the spreader now");
            await Capture("03b_charge_spread");

            // ---- 4. an untouched fuse goes off on its own (this is what makes Doom portable to other witches) ----
            // Isolate it: leftover foes from the spread test sit closer to her, so the beam locked onto one of THEM and
            // the intended target never got a bank. One enemy, nothing else alive, then leave it alone.
            g.ClearEnemies();
            await WaitFrames(20);
            var fuseTgt = g.SpawnEnemyForTest("brute", c);
            await WaitFrames(12);
            if (fuseTgt == null || !GodotObject.IsInstanceValid(fuseTgt)) { _errors.Add("no fuse-test target"); return; }
            p.LookAtForTest(fuseTgt.GlobalPosition);
            Input.ActionPress("cast");
            await WaitFrames(120);
            Input.ActionRelease("cast");
            await WaitFrames(5);
            float held = fuseTgt.DoomBank;
            if (held < 3f) _errors.Add($"could not bank onto the fuse-test target (held={held:0.0})");
            bool popped = await WaitUntil(() => fuseTgt.Dead || fuseTgt.DoomBank < held * 0.5f, 780);
            _warnings.Add($"left alone ~{Enemy.DoomFuse}s: popped={popped} held={held:0.0} bank={(fuseTgt.Dead ? 0f : fuseTgt.DoomBank):0.0}");
            if (!popped) _errors.Add("the fuse never detonated on its own — non-Forsaken witches would get nothing");
            await Capture("04_fuse_selfdetonates");

            // ---- 5. Danse Macabre: a pack turns on itself, ranged included ----
            g.ClearEnemies();
            await WaitFrames(20);
            // SHADES on purpose — the common case. Danse Macabre used to apply flat Doom that exceeded a shade's whole
            // health, so a trash crowd erased itself before anyone could dance; it now applies a FRACTION of max HP, so
            // this crowd must survive being doomed and actually fight each other.
            for (int i = 0; i < 5; i++) g.SpawnEnemyForTest("shade", c + right * (i * 2.8f - 5.6f));
            g.SpawnEnemyForTest("caster", c + right * 6.5f + fwd * 2f);   // a RANGED foe — its bolt must fly at an ally, tinted curse.
                                                                          // NOT "archer": that spawns a WARDED phalanx archer, which is
                                                                          // untouchable while its bearer's ward stands and so can never be turned.
            await WaitFrames(20);
            _warnings.Add($"spawned for the dance: Enemies.Count={g.Enemies.Count} (expect 6)");
            if (g.Enemies.Count < 5) _errors.Add($"the dance crowd never materialised (Enemies.Count={g.Enemies.Count})");
            p.EquipFinisher(FinType.DanseMacabre, 3, 1f, Rarity.Common);
            p.LookAtForTest(c);
            await WaitFrames(20);
            int dmIdx = p.Fin.FindIndex(f => f.Type == FinType.DanseMacabre);
            if (dmIdx < 0) { _errors.Add("Danse Macabre never equipped"); return; }
            p.TestFireFinisher(dmIdx);
            await WaitFrames(45);
            int turned = 0, doomed = 0;
            foreach (var e in g.Enemies) { if (e == null || e.Dead) continue; if (e.Puppeted) turned++; if (e.Doomed) doomed++; }
            _warnings.Add($"Danse Macabre: turned={turned} doomed={doomed} of {g.Enemies.Count} (trash must SURVIVE to dance)");
            if (g.Enemies.Count < 4) _errors.Add($"the dance wiped its own crowd — only {g.Enemies.Count} left, so nobody can dance");
            if (doomed == 0) _errors.Add("Danse Macabre doomed nobody");
            if (turned == 0) _errors.Add("Danse Macabre turned nobody — the puppet routing never engaged");
            await Capture("05_danse_macabre");
            await WaitFrames(90);
            await Capture("06_danse_infighting");   // mid-brawl: look for foes swinging at each other + a violet bolt

            // ---- 6. Rout: the pack scatters ----
            // Rout is a "get off me" button, so test it the way it's used: a tight ring actually ON her, not a crowd
            // loitering at the edge of its radius. The previous framing put every foe 11-12u out, which is not surrounded.
            g.ClearEnemies();
            await WaitFrames(20);
            for (int i = 0; i < 6; i++)
            {
                float ang = i / 6f * Mathf.Tau;
                g.SpawnEnemyForTest("shade", p.GlobalPosition + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * 5f);
            }
            p.EquipFinisher(FinType.Rout, 3, 1f, Rarity.Common);
            await WaitFrames(25);
            int rtIdx = p.Fin.FindIndex(f => f.Type == FinType.Rout);
            if (rtIdx < 0) { _errors.Add("Rout never equipped"); return; }
            p.TestFireFinisher(rtIdx);
            await WaitFrames(30);
            int routed = 0; foreach (var e in g.Enemies) if (e != null && !e.Dead && e.RoutT > 0f) routed++;
            _warnings.Add($"Rout: fleeing={routed}");
            if (routed == 0) _errors.Add("Rout scattered nobody");
            await Capture("07_rout_scatter");

            // ---- 7. the execute + the walking corpse ----
            g.ClearEnemies();
            await WaitFrames(20);
            var victim = g.SpawnEnemyForTest("shade", c);
            for (int i = 0; i < 3; i++) g.SpawnEnemyForTest("shade", c + right * 4f + fwd * (i * 2f));   // a crowd for the corpse to walk into
            await WaitFrames(20);
            if (victim == null) { _errors.Add("no execute victim"); return; }
            p.LookAtForTest(victim.GlobalPosition);
            Input.ActionPress("cast");
            bool executed = await WaitUntil(() => victim.Dead || !GodotObject.IsInstanceValid(victim), 900);
            Input.ActionRelease("cast");
            _warnings.Add($"execute: died={executed}");
            if (!executed) _errors.Add("the bank never covered the victim's HP — no execute fired");
            await Capture("08_execute_and_corpse");

            ReleaseAllInputs();
        }

        private async Task ConduitCheck()
        {
            var g = Game.I; var p = g?.Player; if (p == null) { _errors.Add("no Player"); return; }
            g.NoSpawn = true; g.ClearEnemies();
            await WaitFrames(30);
            Vector3 fwd = -p.GlobalTransform.Basis.Z; fwd.Y = 0f; fwd = fwd.Normalized();
            var e = g.SpawnEnemyForTest("shade", p.GlobalPosition + fwd * 8f);
            await WaitFrames(10);
            if (e == null) { _errors.Add("no enemy"); return; }

            _warnings.Add($"BEFORE: ArcaneMarked={e.ArcaneMarked} (expect False)");
            if (e.ArcaneMarked) _errors.Add("enemy started conduit-marked");
            e.MarkConduit(1.0f);   // a 1s conduit brand
            _warnings.Add($"AFTER MarkConduit(1.0): ArcaneMarked={e.ArcaneMarked} (expect True)");
            if (!e.ArcaneMarked) _errors.Add("MarkConduit did not set ArcaneMarked");
            await WaitFrames(40);   // ~0.6s — still marked
            _warnings.Add($"@~0.6s: ArcaneMarked={e.ArcaneMarked} (expect True)");
            await WaitFrames(80);   // total > 1s — expired
            bool expired = !e.ArcaneMarked;
            _warnings.Add($"@~2s: ArcaneMarked={e.ArcaneMarked} (expect False → self-expired)");
            if (!expired) _errors.Add("conduit brand did NOT self-expire (would linger forever on non-Arcane witches)");
        }

        // Flyer/diver (Mosquito) — inspect the new translucent veined wing membranes + antennae + segmented abdomen. Pins it in
        // frame at eye level and captures several frames so the wing flap + membrane shimmer read.
        private async Task FlyerShowcase()
        {
            var g = Game.I; var p = g?.Player; if (p == null) { _errors.Add("no Player"); return; }
            g.NoSpawn = true; g.ClearEnemies();
            await WaitFrames(30);
            Vector3 fwd = -p.GlobalTransform.Basis.Z; fwd.Y = 0f; fwd = fwd.Normalized();
            Vector3 pin = p.GlobalPosition + fwd * 3.2f + Vector3.Up * 0.7f;   // close-up so the wing membrane/veins read
            var fl = g.SpawnEnemyForTest("flyer", pin);
            if (fl == null) { _errors.Add("no flyer"); return; }
            async Task Hold(int n) { for (int i = 0; i < n; i++) { if (GodotObject.IsInstanceValid(fl)) fl.GlobalPosition = pin; await NextFrame(); _frame++; } }
            await Hold(20);
            await Hold(2); await Capture("00_hover_a");
            await Hold(3); await Capture("01_hover_b");   // different flap phase
            await Hold(3); await Capture("02_hover_c");
        }

        // PROTOTYPE viewer for the floating disembodied witch avatar. Lines up 3 witches (distinct element colour + hat style)
        // facing the camera (identity read), then walking (procedural stride), then from behind (TP read), then one at the camera
        // (FP hands read). No commitment to the live view yet — this is purely to SEE the look for the critic pass.
        private async Task FloatingAvatarShowcase()
        {
            var g = Game.I; var p = g?.Player; if (p == null) { _errors.Add("no Player"); return; }
            g.NoSpawn = true; g.ClearEnemies();
            p.DevSetFpArmsVisible(false);   // clean plate — no FP viewmodel cluttering the portrait
            await WaitFrames(30);
            Vector3 fwd = -p.GlobalTransform.Basis.Z; fwd.Y = 0f; fwd = fwd.Normalized();
            Vector3 right = p.GlobalTransform.Basis.X; right.Y = 0f; right = right.Normalized();
            // avatar +Z is its FRONT (face/hands/orb). To face the camera it must point back along -fwd → camYaw + PI.
            float camYaw = Mathf.Atan2(fwd.X, fwd.Z) + Mathf.Pi;

            // ---- ONE big avatar, CLOSE, orbited through the key angles so the hands/face read at portrait size ----
            Vector3 pos = p.GlobalPosition + fwd * 8f; pos.Y = g.SurfaceHeight(pos, 1e9f);
            var a = new FloatingAvatar(); g.AddChild(a); a.Build(4);   // Gale (teal) — reads clearly against the warm scene
            a.Scale = Vector3.One * 2.7f; a.GlobalPosition = pos;
            async Task Drive(int frames, float move, float fire = 0f, float charge = 0f)
            {
                for (int f = 0; f < frames; f++) { a.SetCast(fire, charge); a.Animate(0.016f, move); await NextFrame(); _frame++; }
            }
            (float deg, string name)[] views = { (0f, "front"), (45f, "three_quarter"), (90f, "side"), (180f, "behind") };
            for (int i = 0; i < views.Length; i++)
            {
                a.Rotation = new Vector3(0, camYaw + Mathf.DegToRad(views[i].deg), 0);
                await Drive(22, 0f); await Capture($"0{i}_{views[i].name}");
            }
            a.Rotation = new Vector3(0, camYaw + Mathf.DegToRad(35f), 0);
            await Drive(40, 0.9f); await Capture("04_walking");        // 3/4 walking → stride + lean read
            await Drive(28, 0.2f, fire: 1f, charge: 0.85f); await Capture("05_casting");   // orb flares + hands thrust

            // ---- palette check: the 3 hat/hue styles side by side (Lunar / Gale / Arcane) ----
            a.QueueFree();
            int[] idxs = { 0, 4, 8 };
            var avs = new System.Collections.Generic.List<FloatingAvatar>();
            Vector3 c = p.GlobalPosition + fwd * 9f;
            for (int i = 0; i < 3; i++)
            {
                var w = new FloatingAvatar(); g.AddChild(w); w.Build(idxs[i]);
                w.Scale = Vector3.One * 2.2f;
                Vector3 wp = c + right * ((i - 1) * 3.2f); wp.Y = g.SurfaceHeight(wp, 1e9f);
                w.GlobalPosition = wp; w.Rotation = new Vector3(0, camYaw, 0); avs.Add(w);
            }
            for (int f = 0; f < 24; f++) { foreach (var w in avs) w.Animate(0.016f, 0f); await NextFrame(); _frame++; }
            await Capture("06_palette");
            foreach (var w in avs) w.QueueFree();
            p.DevSetFpArmsVisible(true);
        }

        // Preview the raw Meshy avatar pieces (hat/hand/robe) one at a time, front + turned, to judge shape/orientation/quality
        // BEFORE assembling the authored avatar. Uses the PropGlb normalizer (unit-max-dim, based, centred) + baked material.
        private async Task AvatarParts()
        {
            var g = Game.I; var p = g?.Player; if (p == null) { _errors.Add("no Player"); return; }
            g.NoSpawn = true; g.ClearEnemies();
            await WaitFrames(20);
            Vector3 fwd = -p.GlobalTransform.Basis.Z; fwd.Y = 0f; fwd = fwd.Normalized();
            string[] names = { "hat", "hand", "robe" };
            for (int i = 0; i < names.Length; i++)
            {
                var m = PropGlb.Instance(names[i], 1.8f, seed: 100 + i);
                g.AddChild(m);
                m.GlobalPosition = p.GlobalPosition + fwd * 5f + Vector3.Up * 1.3f;
                var ext = PropGlb.NormExtents(names[i]);
                _warnings.Add($"{names[i]} normExtents=({ext.X:0.00},{ext.Y:0.00}) (× world height = footprint half-widths)");
                await WaitFrames(18); await Capture($"{i}0_{names[i]}_front");
                m.RotationDegrees = new Vector3(0, 145f, 0);
                await WaitFrames(10); await Capture($"{i}1_{names[i]}_turn");
                m.QueueFree(); await WaitFrames(3);
            }
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
            // (DIAG) top-down view of the keep colliders, matching the cedit authoring angle, to compare authored vs in-game
            p.GlobalPosition = new Vector3(keepCenter.X, roofY + 22f, keepCenter.Z + 16f); p.Rotation = new Vector3(0, 0, 0); p.EditorLookPitch(-0.95f);
            await WaitFrames(18); await Capture("collision_keep_top");
            p.EditorLookPitch(0f);
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

        // FIRST-PERSON viewmodel: stay in default FP (do NOT enter tp3) and capture the unified authored glove hands, idle + charging.
        private async Task FpHands()
        {
            var p = Game.I?.Player;
            if (p == null) { _errors.Add("no Player"); return; }
            await WaitFrames(45);
            await Capture("00_fp_idle");
            Input.ActionPress("charge");
            if (!await WaitUntil(() => p.Charging && p.ChargeAmt >= 0.6f, 360)) _warnings.Add("charge never reached 0.6");
            await Capture("01_fp_charge");
            Input.ActionRelease("charge");
            await WaitFrames(4);
            await Capture("02_fp_release");
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

        // HAUNT STORM — the Haunt's lightning. Three things have to hold and each gets its own evidence:
        //   (A) the TELEGRAPH reads: a purple disc ringed in red, sitting flat on the Haunt's uneven ground.
        //   (B) the BOLT lands where the circle was, and foes standing in it are hurt AND frozen in place.
        //   (C) it is NOT friendly — the witch takes the same hit + stun if she doesn't move.
        // Then the director runs free for a stretch so the cadence/concurrency can be read off a real sample
        // instead of trusted from the constants.
        private async Task HauntStorm()
        {
            var g = Game.I; var p = g?.Player; if (p == null) { _errors.Add("no Player"); return; }
            g.NoSpawn = true; g.ClearEnemies();
            // silence the free-running storm for parts A-C: a random strike landing near her seconds before the scripted
            // one leaves her i-framed, and the scripted hit then reads as "did no damage" when it was simply mitigated
            g.NoHauntBolts = true;
            await WaitFrames(25);

            // stage on dry, flat-ish ground: she boots into the shallows and a half-submerged strike is unjudgeable
            Vector3 stage = p.GlobalPosition;
            for (float r = 10f; r <= 90f && stage == p.GlobalPosition; r += 8f)
                for (int i = 0; i < 12; i++)
                {
                    float a = i * Mathf.Tau / 12f;
                    var q = p.GlobalPosition + new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * r;
                    float y = g.SurfaceHeight(q, 1e9f);
                    if (y >= World.WaterLevel + 1.5f) { stage = new Vector3(q.X, y, q.Z); break; }
                }
            p.GlobalPosition = new Vector3(stage.X, g.SurfaceHeight(stage, 1e9f) + 0.2f, stage.Z);
            // Centre the Haunt WELL ahead of her: its cyclone funnel pinches to a ~2.5u throat at the heart, and a camera
            // standing in that throat films the inside of the cone — a milky wash that makes every capture unjudgeable.
            {
                Vector3 f0 = -p.GlobalTransform.Basis.Z; f0.Y = 0f; f0 = f0.Normalized();
                g.SpawnHaunt(stage + f0 * 26f);
            }
            await WaitFrames(30);
            _warnings.Add($"STAGE {stage.X:0},{stage.Z:0} | haunt r={g.HauntRadius:0} | bolts@stage{g.DiffStage()}={g.HauntBoltCount} | bolt r={Game.HauntBoltRadius}");

            Vector3 fwd = -p.GlobalTransform.Basis.Z; fwd.Y = 0f; fwd = fwd.Normalized();
            Vector3 right = p.GlobalTransform.Basis.X; right.Y = 0f; right = right.Normalized();

            // keep her alive + un-interrupted: a strike will hit her on purpose in part (C), and a down/level-up
            // mid-run would soft-pause the sim and poison every later capture.
            void Guard() { if (g.State != GameState.Playing) g.State = GameState.Playing; }
            async Task GW(int n) { for (int i = 0; i < n; i++) { Guard(); await NextFrame(); _frame++; } }

            // ---- (A) + (B): a deterministic strike in front of her, with foes standing in it -----------------
            Vector3 mark = stage + fwd * 13f;
            var foes = new System.Collections.Generic.List<Enemy>();
            for (int i = 0; i < 6; i++)
            {
                float a = i * Mathf.Tau / 6f;
                var q = mark + new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * 2.6f; q.Y = g.SurfaceHeight(q, 1e9f);
                var e = g.SpawnEnemyForTest("shade", q);
                // a shade dies outright to a strike, and a dead foe can't be observed as STUNNED — fatten them so the
                // stun is actually readable. The damage assert below compares against their own recorded HP either way.
                if (e != null) { e.MaxHp = 4000f; e.Hp = 4000f; e.WakeSilent(); foes.Add(e); }
            }
            await GW(30);
            // pin them on the mark: a woken shade walks at the witch and would leave the circle before it lands
            async Task PinHold(int n)
            {
                for (int i = 0; i < n; i++)
                {
                    for (int k = 0; k < foes.Count; k++)
                    {
                        var e = foes[k]; if (!GodotObject.IsInstanceValid(e) || e.Dead) continue;
                        float a = k * Mathf.Tau / 6f;
                        var q = mark + new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * 2.6f;
                        e.GlobalPosition = new Vector3(q.X, e.GlobalPosition.Y, q.Z);
                    }
                    Guard(); await NextFrame(); _frame++;
                }
            }
            await PinHold(20);
            await Capture("00_before");

            float[] hpBefore = new float[foes.Count];
            for (int i = 0; i < foes.Count; i++) hpBefore[i] = GodotObject.IsInstanceValid(foes[i]) ? foes[i].Hp : 0f;

            g.DebugHauntBolt(mark);
            await PinHold(12); await Capture("01_telegraph_early");   // purple fill + red rim, slow pulse
            await PinHold(35); await Capture("02_telegraph_late");    // same circle, pulsing fast — it is about to land
            // WAIT FOR THE STRIKE rather than counting frames. The telegraph is 1.15 SECONDS and the arc only flashes for
            // ~0.3s; a fixed frame count lands inside or past that window depending on the harness framerate, which is why
            // one run showed the fork and the next showed an empty circle with damage numbers.
            HauntBolt hb = null;
            for (int i = 0; i < 300; i++)
            {
                hb = null; foreach (var ch in g.GetChildren()) if (ch is HauntBolt h2 && GodotObject.IsInstanceValid(h2)) hb = h2;
                if (hb != null && hb.DebugStruck) break;
                await PinHold(1);
            }
            _warnings.Add(hb == null ? "ARC: no HauntBolt alive at the impact capture"
                                     : $"ARC at impact: struck={hb.DebugStruck} segments={hb.DebugArcSegments}");
            if (hb == null || !hb.DebugStruck) _errors.Add("the strike never fired within the wait window");
            else if (hb.DebugArcSegments == 0) _errors.Add("the strike built no arc geometry");
            await PinHold(3); await Capture("03_impact");             // the fork, lit, with the shock ring opening
            await PinHold(30); await Capture("04_after");             // arc gone, rings expanding, foes held mid-stride

            int stunned = 0, hurt = 0;
            for (int i = 0; i < foes.Count; i++)
            {
                var e = foes[i]; if (!GodotObject.IsInstanceValid(e)) { hurt++; continue; }
                if (e.ShockT > 0f) stunned++;
                if (e.Dead || e.Hp < hpBefore[i] - 0.01f) hurt++;
            }
            _warnings.Add($"FOES in circle: {foes.Count} | hurt={hurt} | stunned={stunned}");
            if (hurt < foes.Count) _errors.Add($"strike did not damage every foe in its circle ({hurt}/{foes.Count})");
            if (stunned < foes.Count) _errors.Add($"strike did not stun every foe in its circle ({stunned}/{foes.Count})");

            // ---- (C) it hits the witch too ------------------------------------------------------------------
            foreach (var e in foes) if (GodotObject.IsInstanceValid(e)) e.QueueFree();
            await GW(20);
            Vector3 me = stage + right * 9f; me.Y = g.SurfaceHeight(me, 1e9f) + 0.2f;
            p.GlobalPosition = me;
            p.Hp = p.S.MaxHp;
            // Her SHIELD absorbs a hit this size before HP ever moves, and a single ARMOR charge eats one whole hit
            // outright — and both regenerate, so clearing them once and waiting doesn't hold. Strip them EVERY frame
            // through the strike, or "took no damage" is measuring her mitigation instead of the bolt.
            async Task Bare(int n) { for (int i = 0; i < n; i++) { p.Shield = 0f; p.Armor.Clear(); Guard(); await NextFrame(); _frame++; } }
            await Bare(15);
            float hpWas = p.Hp;
            g.DebugHauntBolt(me);
            await Bare(20); await Capture("05_witch_telegraph");   // she is standing in one — this is the "move" moment
            await Bare(60);                                        // must span the whole 69-frame telegraph so the impact
            await GW(20);                                          //   itself lands while her mitigation is stripped
            float hpNow = p.Hp; float stun = p.StunT;
            _warnings.Add($"WITCH hit: hp {hpWas:0.0} -> {hpNow:0.0} (dmg {hpWas - hpNow:0.0}) | StunT={stun:0.00} | shield={p.Shield:0.0} armor={p.Armor.Count} iframing={p.IFraming}");
            if (hpNow >= hpWas - 0.01f) _errors.Add("the strike did not damage the witch standing in it");
            if (stun <= 0f) _errors.Add("the strike did not stun the witch standing in it");
            await Capture("06_witch_struck");
            p.Hp = p.S.MaxHp;

            // ---- the DIRECTOR: let it run and read the real cadence off the counter -------------------------
            p.GlobalPosition = new Vector3(stage.X, g.SurfaceHeight(stage, 1e9f) + 0.2f, stage.Z);
            g.NoHauntBolts = false;
            int fired0 = g.HauntBoltsFired;
            const int sampleFrames = 60 * 24;   // ~24s at 60fps — long enough that a 2-10s cadence averages out
            int peak = 0;
            for (int i = 0; i < sampleFrames; i++)
            {
                Guard(); await NextFrame(); _frame++;
                if (i % 300 == 0) { p.Hp = p.S.MaxHp; }   // the storm is hitting her; don't let the sample down her
                int live = 0; foreach (var ch in g.GetChildren()) if (ch is HauntBolt lb && GodotObject.IsInstanceValid(lb)) live++;
                if (live > peak) peak = live;
                if (i == sampleFrames / 2) await Capture("07_storm_running");
            }
            int fired = g.HauntBoltsFired - fired0;
            float secs = sampleFrames / 60f;
            _warnings.Add($"DIRECTOR over {secs:0}s: strikes={fired} ({secs / Mathf.Max(1, fired):0.0}s apart avg) | emitters={g.HauntBoltCount} | peak concurrent={peak}");
            if (fired == 0) _errors.Add("the storm fired no strikes at all while a warden stood in the Haunt");
            await Capture("08_storm_end");
        }

        // HAUNT VFX — the zone's own dressing, framed close enough to judge. Three things were primitives that shipped:
        //   (A) the whirling leaves were flat untextured cards → must now be the authored Meshy leaf GLBs.
        //   (B) the spectral wisps were 1.5m hard-edged additive quads that plastered the screen when one spawned on the
        //       camera → must now have no visible square edge at ANY range.
        //   (C) the phantoms were a sphere with three hemispheres stuck to it → must now read as torn cloth.
        // The wisp/leaf shot deliberately stands at radius*0.7 from the heart — the exact emission ring, i.e. the worst
        // case that produced the screen-covering card.
        private async Task HauntVfx()
        {
            var g = Game.I; var p = g?.Player; if (p == null) { _errors.Add("no Player"); return; }
            g.NoSpawn = true; g.ClearEnemies(); g.NoHauntBolts = true;   // no strikes: this run is about the dressing
            await WaitFrames(25);

            Vector3 stage = p.GlobalPosition;
            for (float r = 10f; r <= 90f && stage == p.GlobalPosition; r += 8f)
                for (int i = 0; i < 12; i++)
                {
                    float a = i * Mathf.Tau / 12f;
                    var q = p.GlobalPosition + new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * r;
                    float y = g.SurfaceHeight(q, 1e9f);
                    if (y >= World.WaterLevel + 1.5f) { stage = new Vector3(q.X, y, q.Z); break; }
                }
            p.GlobalPosition = new Vector3(stage.X, g.SurfaceHeight(stage, 1e9f) + 0.2f, stage.Z);
            Vector3 fwd = -p.GlobalTransform.Basis.Z; fwd.Y = 0f; fwd = fwd.Normalized();

            void Guard() { if (g.State != GameState.Playing) g.State = GameState.Playing; p.Hp = p.S.MaxHp; }
            async Task GW(int n) { for (int i = 0; i < n; i++) { Guard(); await NextFrame(); _frame++; } }

            // (C) PHANTOMS — haunt centred far enough that the funnel haze isn't over the lens, then stage the lineup
            // close in front of her so the silhouette fills the frame.
            g.SpawnHaunt(stage + fwd * 60f);
            await GW(40);
            var h = g.TheHaunt;
            if (h == null) { _errors.Add("no Haunt spawned"); return; }
            _warnings.Add($"phantoms={h.GhostCount} | haunt r={g.HauntRadius:0}");
            // the leaf cards became REAL geometry — make sure a Meshy leaf isn't thousands of tris × 34 particles × 4 bands
            foreach (var lm in new[] { "leaf_a", "leaf_b", "leaf_c" })
            {
                var m = PropGlb.GetMesh(lm);
                if (m == null) { _errors.Add($"leaf model '{lm}' missing — the Haunt would have no leaves"); continue; }
                int tris = 0;
                for (int s = 0; s < m.GetSurfaceCount(); s++)
                {
                    var arr = m.SurfaceGetArrays(s);
                    var idx = arr[(int)Mesh.ArrayType.Index];
                    tris += idx.VariantType != Variant.Type.Nil ? idx.As<int[]>().Length / 3
                                                                : arr[(int)Mesh.ArrayType.Vertex].As<Vector3[]>().Length / 3;
                }
                _warnings.Add($"LEAF {lm}: {tris} tris × 34 particles = {tris * 34} tris for that band");
            }
            if (h.GhostCount == 0) _errors.Add("the Haunt built no phantoms");

            // anchor is LOCAL to the haunt: put the lineup just in front of the witch, at her eye level
            Vector3 anchorWorld = stage + fwd * 17f + new Vector3(0f, 2.6f, 0f);
            h.DebugGhostAnchor = anchorWorld - h.GlobalPosition;
            h.DebugStageGhosts = true;
            await GW(30);
            await Capture("00_phantoms");

            // a second, closer pass on one phantom — silhouette, torn hem, sleeve tendrils, eye slits
            h.DebugGhostAnchor = (stage + fwd * 8.5f + new Vector3(0f, 2.2f, 0f)) - h.GlobalPosition;
            await GW(25);
            await Capture("01_phantom_close");
            h.DebugStageGhosts = false;

            // (A)+(B) LEAVES + WISPS — stand ON the emission ring (radius*0.7), the worst case for near-camera quads
            {
                Vector3 c = g.HauntCenter;
                Vector3 toHeart = (c - stage); toHeart.Y = 0f; toHeart = toHeart.Normalized();
                Vector3 onRing = c - toHeart * (g.HauntRadius * 0.7f);
                onRing.Y = g.SurfaceHeight(onRing, 1e9f) + 0.2f;
                p.GlobalPosition = onRing;
            }
            await GW(120);   // let the emitters fill in around her
            await Capture("02_wisps_and_leaves");
            await GW(90);
            await Capture("03_wisps_and_leaves_b");   // a second draw of the RNG — one frame can flatter or libel a particle system

            // look UP at the storm deck. The cloud puffs only betray themselves as faceted slabs from underneath, which
            // is exactly the angle a witch fighting in the zone has and the one a forward-facing capture never shows.
            for (int i = 0; i < 34; i++)
            { Input.ParseInputEvent(new InputEventMouseMotion { Relative = new Vector2(0f, -9f) }); Guard(); await NextFrame(); _frame++; }
            await GW(25);
            await Capture("04_sky_deck");
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
