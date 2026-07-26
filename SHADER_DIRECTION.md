# Shader Direction

Visual target:
Always explicitly ask the user what they want visually first and ask for a reference if both of these aren't given

Required layers:
- base albedo texture
- normal map
- ORM or separate roughness/metallic/AO inputs
- world-space macro variation
- optional detail normal
- subtle Fresnel controlled by a uniform
- masked emission, not full-surface emission
- damage/dissolve mask support
- per-instance hue and intensity variation

Performance:
- no screen texture sampling
- no loops with variable iteration counts
- avoid unnecessary transparency
- expose expensive features as shader_feature-style toggles or separate variants
- create High, Medium, and Low variants
- explain estimated texture samples and expensive operations

Deliver:
- .gdshader
- ShaderMaterial resource
- demo scene
- parameter documentation
- default values that look restrained rather than exaggerated
