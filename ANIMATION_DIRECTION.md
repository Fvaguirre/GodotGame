# Animation Direction

Use this document as the default standard for all character animation, including locomotion, attacks, spellcasting, reactions, abilities, idles, and transitions.

## Core Principles

Every animation should communicate intent clearly through:

- anticipation
- primary action
- extension or overshoot
- recoil or follow-through
- recovery

Do not treat animation as simple movement between two poses.

## Body Mechanics

- Motion should usually begin from the hips or center of mass, then travel through the torso, shoulders, arms, hands, and head.
- Avoid moving the entire body as one rigid unit.
- Avoid starting and stopping every body part on the same frame.
- Use overlapping motion between body regions.
- Use counter-motion from the opposite arm, shoulder, hip, or leg where appropriate.
- Keep the character balanced unless imbalance is intentional.
- Preserve weight, momentum, and direction through the whole action.
- Prefer asymmetrical poses over perfectly mirrored ones.

## Timing and Spacing

- Do not use linear timing for major body motion.
- Use ease-in, ease-out, acceleration, deceleration, holds, and sharp changes where appropriate.
- Strong actions should have a readable anticipation phase.
- Impactful actions should have a clear release or contact moment.
- Recovery should not snap directly back to idle.
- Different body parts should begin and end their motion at different times.
- Adjust timing to the action’s weight, speed, power, and gameplay purpose.

## Readability

- The animation must remain readable from the actual gameplay camera.
- Important poses should have a clear silhouette.
- Avoid poses where limbs overlap so much that the action becomes unclear.
- Exaggerate key poses enough to survive gameplay distance and motion.
- Preserve the direction of attacks, movement, and targeting.
- Head and eye direction should support the intended target or motion.

## Secondary Motion

Secondary elements should react after the primary body motion:

- hair
- cloaks
- coats
- straps
- jewelry
- weapon ornaments
- loose clothing
- tails
- accessories

These elements should overlap and follow through rather than moving in perfect sync with the body.

## Combat and Ability Animation

For attacks, spells, and abilities:

- establish a clear anticipation pose
- create a readable release or contact moment
- use extension, recoil, and recovery
- synchronize gameplay events with the intended visual release frame
- align VFX, audio, hitboxes, projectiles, and camera feedback with the animation
- avoid effects triggering noticeably before or after the character motion
- preserve responsiveness even when using strong anticipation
- support animation cancellation or chaining when gameplay requires it

## Locomotion

Locomotion should:

- reflect character weight and personality
- preserve clear foot contact
- avoid foot sliding
- use proper hip and torso motion
- include natural arm counter-swing
- transition cleanly between idle, walk, run, sprint, turn, stop, jump, and land
- avoid blending states so heavily that movement loses impact or foot placement

## Idle Animation

Idle animations should:

- feel alive without becoming distracting
- use subtle breathing, balance shifts, head movement, and secondary motion
- avoid simple whole-body vertical bobbing
- preserve the character’s personality and combat readiness
- vary timing so the motion does not feel mechanical

## Reactions and Hit Animations

Hit reactions should:

- respond to the direction and strength of impact
- show force traveling through the body
- avoid identical reactions for every hit
- preserve gameplay readability
- return cleanly to locomotion or combat state
- use stronger displacement only when gameplay allows it

## Godot Implementation

Use:

- **AnimationPlayer** for clips, imported animations, method tracks, and event timing.
- **AnimationTree** for:
  - state transitions
  - locomotion blending
  - one-shot attacks
  - additive layers
  - upper-body overlays
  - blend spaces
  - chained actions

Keep gameplay events explicit and inspectable.

Use method tracks, call-method tracks, or a dedicated animation-event system for:

- projectile spawning
- hitbox activation
- spell release
- VFX triggers
- audio events
- camera impulses
- resource consumption
- combo windows

Do not estimate these events indirectly from elapsed gameplay time when they can be authored directly in the animation.

## Root Motion

Root motion should remain disabled by default.

Only use root motion when:

- the action specifically requires authored displacement
- gameplay movement and collision remain authoritative
- the result has been tested for responsiveness and networking implications

## Review Checklist

Before considering an animation complete, verify:

- the intent is immediately readable
- the key poses have clear silhouettes
- anticipation, action, follow-through, and recovery are present where appropriate
- the character shows believable weight and momentum
- body parts do not all move simultaneously
- secondary motion overlaps the main action
- the animation reads from gameplay camera distance
- there is no obvious foot sliding
- transitions do not snap
- gameplay events align with the visual action
- the animation integrates cleanly with locomotion and state transitions
- the final in-game playback speed still feels correct
