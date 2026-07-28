# Visual Validation Checklist

When validating any visual change by looking at a screenshot (the AI harness in `dev/ai`, or a
user-provided image), inspect **adversarially**, not confirmationally.

- Wrong question: "Is the thing I built visible / does it look roughly right?"
- Right question: "What is WRONG in this frame? What would a skeptical player notice?"

Confirmational looking makes real defects read as "minor artifacts." Scan the WHOLE frame for
abnormalities first, THEN confirm the feature. Do this for **every** capture, not just the hero shot.

## Never rationalize an anomaly

If something looks off, it IS off until proven otherwise. Do not explain it away
("probably the water", "minor", "the angle"). Either:
1. **Investigate** it (measure via the state JSON / a log / a bone or AABB readout), or
2. **Flag** it explicitly to the user.

"Slightly floating", "a bit big", "looks a little off" are defects to run down — not things to ship.

## Per-frame scan (3D characters / props / enemies)

- **Ground contact** — do the feet/base actually touch the floor? Not floating, not sunk. Check the
  shadow and the contact point, not just "it's standing-ish". Measure the foot bone / mesh bottom if unsure.
- **Scale & proportion** — right size vs peers, hitbox, and any rings/auras? Not dwarfed by or
  overflowing its ground ring. Compare against the OLD version when replacing an asset.
- **Orientation / facing** — facing the intended direction (at the player, along travel)? Not backwards
  or sideways. Walk anim should move it the way it faces.
- **Clipping / intersection** — limbs through body/robe/hat, model through terrain/structures/other
  models, feet through the floor, arms through the cape.
- **Actually animating** — is it a live pose or a frozen bind/T-pose? Does the anim blend in/out or
  snap? Feet sliding (skating) vs planted? A "playing=true" flag is not proof it reads on screen.
- **Attachment alignment** — rings, auras, name labels, health bars, weapons, VFX positioned correctly
  relative to THIS model's size/origin (they're often sized off `Radius` / a hitbox, not the mesh).
- **Material / texture** — correct colors and the RIGHT texture on the RIGHT surface. No washed-out or
  swapped maps, no z-fighting / flicker, no missing (magenta/white) textures.
- **Lighting / tint** — any unexpected color cast? A stray colored light can tint a whole model
  (e.g. a hardcoded light making every witch's hat purple). If a color is wrong, suspect a light.
- **Silhouette read** — clear and readable at gameplay camera distance, not a mushy blob.

## When replacing or comparing assets

Explicitly compare NEW vs OLD for: grounding, scale, facing, and that all the wrapper systems
(affixes, names, types, size scaling, rings, hitboxes) still function — those usually live on a
wrapper node and must be re-verified, not assumed.

## Report what you actually inspected

State which captures you opened, what you scanned for, and any anomaly you found (and whether you
investigated or are flagging it). A clean compile and a "passed" scenario are NOT visual validation.
