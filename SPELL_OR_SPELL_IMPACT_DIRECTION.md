# Spell / Spell Impact Direction

Do not use a single particle emitter as the whole effect.

Break it into these timed phases:
- anticipation
- release
- travel
- impact
- decay

Before coding, define:
- silhouette at each phase
- primary, secondary, and tertiary motion
- color hierarchy
- timing in milliseconds
- authored meshes required
- shader requirements
- particle counts
- transparency overlap risks
- light and shadow usage
- low-quality fallback

Implementation requirements:
- use GPUParticles3D for bulk particles
- use authored meshes for the dominant silhouette
- use curves for scale, alpha, velocity, and emission
- use one controller that synchronizes all layers
- expose duration, scale, color, intensity, and quality
- pool frequently spawned effect scenes
- avoid spawning new materials per instance
- use per-instance shader parameters where possible
- provide a stress-test scene with 1, 10, 50, and 100 simultaneous instances
