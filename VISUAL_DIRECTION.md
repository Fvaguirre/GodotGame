# Visual Direction

## Related Docs

For authoring any enemy, character, or prop model, follow [MODEL_DIRECTION.md](MODEL_DIRECTION.md) — the model-authoring pipeline, mesh/LOD/material/skeleton/animation/import spec, and asset-placeholder + asset-brief workflow.

For authoring any shader or ShaderMaterial, follow [SHADER_DIRECTION.md](SHADER_DIRECTION.md) — required shader layers, performance constraints, High/Medium/Low variants, and the .gdshader + material + demo-scene + parameter-doc deliverables.

For authoring any spell or spell-impact effect, follow [SPELL_OR_SPELL_IMPACT_DIRECTION.md](SPELL_OR_SPELL_IMPACT_DIRECTION.md) — the five timed phases (anticipation/release/travel/impact/decay), pre-code definition checklist, authored-mesh + GPUParticles3D + curve-driven layered implementation, pooling, and the 1/10/50/100-instance stress-test scene.

For all character, combat, casting, locomotion, reaction, and ability animation work, read and follow [ANIMATION_DIRECTION.md](ANIMATION_DIRECTION.md).

## Target

The game should look like a polished modern stylized witchy / fantasy action game.

It should not look:
- low-poly
- flat-shaded
- mobile-game generic
- voxel-like
- like primitive Godot placeholder geometry
- like every effect is a glowing sphere
- like particle systems are being used without authored shapes

## Art Direction

Overall style:
Stylized painterly fantasy

Visual references:
- Avowed: use its material richness and lighting
- Spellbreak: use its spell silhouettes and color separation
- Spellbreak and Avowed: use its animation timing and impact
- Grounded 2: use its environmental density
- Legend of Zelda: use its style for models and assets 

Do not copy assets or exact designs. Use the references only to identify:
- silhouette language
- shape language
- lighting contrast
- material treatment
- effect density
- animation timing
- environmental detail level

## Shape Language

Characters:
- Distinct silhouette at gameplay camera distance
- Large primary forms
- Medium secondary forms
- Limited tiny detail
- Avoid uniform cylindrical limbs and generic capsule bodies
- Avoid perfectly symmetrical costume details

Environment:
- Large readable architectural masses
- Layered trim and secondary detail
- Repeated assets must vary in scale, rotation, and material parameters
- Avoid visibly tiled empty floors and walls

Effects:
- Every major effect requires a recognizable silhouette
- Use directional shapes, arcs, ribbons, cones, crescents, shards, rings, or authored meshes
- Do not rely on circular particle clouds as the main shape
- Effects require anticipation, activation, impact, and dissipation phases

## Material Direction

Materials should generally include:
- albedo variation
- roughness variation
- normal detail where appropriate
- controlled emission
- edge or Fresnel treatment only when stylistically appropriate
- macro variation so large surfaces do not look uniform

Avoid:
- maximum emission
- pure black shadows
- pure white highlights
- identical roughness across every surface
- excessive transparency
- excessive screen-space distortion

## Lighting Direction

Use:
- one clear primary lighting direction
- deliberate warm/cool contrast
- restrained world ambient light
- contact shadows
- localized accent lighting for major gameplay events
- fog or depth separation where appropriate

Do not solve poor materials by adding more lights.

## Animation Direction

Movement should include:
- anticipation
- clear primary action
- overshoot or recoil
- recovery
- asymmetric posing
- overlapping motion
- varied timing

Avoid:
- linear interpolation
- simultaneous movement of all body parts
- motion beginning and ending on the same frame
- attacks that lack recoil or follow-through
- idle animations where only the entire body moves vertically

## Performance Targets

Target platform:
[desktop]

Target frame rate:
[120 FPS]

Target resolution:
[1080p, 1440p, 4k]

Rendering backend:
[Forward+]

For every visual feature:
- state expected draw calls
- state particle count
- state transparent-material usage
- state dynamic-light usage
- state shadow usage
- include a low-quality fallback when expensive
- expose quality parameters
- profile before and after

## Review Requirement

Before implementing a substantial visual feature:
1. Inspect the existing project structure.
2. Describe the proposed visual layers.
3. Identify any required authored assets.
4. Identify performance risks.
5. Implement the smallest complete vertical slice.
6. Capture screenshots or video frames for review.
7. Revise based on visual critique.
