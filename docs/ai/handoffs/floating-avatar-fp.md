# Handoff: floating-avatar-fp

## 1. Objective & intended behavior
A disembodied floating witch avatar unifying 3rd-person (tp3) and first-person: same authored glove
hands in both views (Far Far West style). TP = full avatar (hat, ghostly wraith head + glowing
damage-color eyes, energy hair, grey robe, gloves gripping an orb). FP = the same gloves as a
viewmodel + the charge orb.

## 2. Current status — WORKING (validated)
- TP avatar built in `FloatingAvatar.cs`; ghastly energy **hair** = `shaders/ghastly_hair.gdshader`
  (additive smooth ribbons, damage-colored, back-anchored cascade). Owner accepted ~7/10.
- FP unification done: `Player.BuildArm` now instances the same `PropGlb "hand"` glove (no primitive
  arms); FP charge choreography in `AnimateHands` (palms cup → spread on charge → shake at full →
  palms forward + extend on release), gated to generic witches.
- FP hand **rest pose** posed via the in-engine `fppose` tool and baked: `_fpRestL=(260,-50,0)` not
  mirrored, `_fpRestR=(260,55,0)` mirrored. TP scale dropped 2.7→2.15 (was "towering").
- Build 0/0; validated via `floating_avatar`, `witch_locomotion`, `fp_hands` scenarios.

## 3. Constraints & decisions
- Hair: additive + smooth ribbons (NOT striated → read as fishnet; NOT cones → read as spikes);
  damage-color body, no white core; roots tucked under the hat; render_priority 8 (over pond water).
- Owner HATES: hoods, pale skin face, white/uncolored hair lines. Eyes = damage-color almond, tamed.
- FP glove axes were unknown — solved with the `fppose` tool (dev cmd) + save file, NOT euler guessing.
  One hand MUST be mirrored (negative Scale.X) or the pair can't be anatomically correct.

## 4. Files & major symbols
- `FloatingAvatar.cs` (`Build`, `BuildGhastlyHair`, `Animate`), `shaders/ghastly_hair.gdshader`.
- `Player.cs`: `BuildArm`, `AnimateHands`, `ToggleThirdPersonPlay` (`_favatar.Scale`), `ToggleFpHandPose`/
  `UpdateFpHandPose`/`SaveFpHandPose`/`LoadFpHandPose`, `_fpRestL`/`_fpRestR`, `_fpPoseMirror*`.
- `PropGlb.cs` "hand"/"hat"/"robe"; `dev/ai/AiTestRunner.cs` (`fp_hands`, `floating_avatar`).
- Pose file: `res://data/fp_hand_pose.json` (saved by `fppose`).

## 5. Tests/validation performed
- `dotnet build` 0/0. Scenarios `floating_avatar` (front/back/side/3-4/walk/cast/palette), `fp_hands`
  (idle/charge/release), `witch_locomotion` — screenshots inspected adversarially each iteration.

## 6. Current failures / uncertainties
- Hair is ~7/10 vs the owner's 2D reference; a literal match likely needs an authored hair MESH skinned
  with `ghastly_hair.gdshader` (procedural loop asymptotes). Owner accepted procedural for now.
- FP release palm-rotation is a modest yaw ease; may want tuning. Charge shake only visible in motion.

## 7. Briefly-rejected approaches
- Cone hair (spikes), striated ribbons (fishnet), white cores (owner hated), euler-guessing the glove
  axes (endless — use `fppose`), authoring separate L/R hand meshes (mirror one instead).

## 8. Next 3 concrete actions
1. If pursuing a closer hair match: author a hair-card/sculpted MESH, skin with `ghastly_hair.gdshader`.
2. Tune FP release amount (extend distance / palm yaw) if it feels off in play.
3. Optionally add the hat brim peeking at the top of the FP view (further FP/TP unification).
