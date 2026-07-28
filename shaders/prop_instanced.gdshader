shader_type spatial;

// ============================================================================
// PROP INSTANCED MATERIAL — for Meshy-authored Grove props (pumpkin, mushroom,
// flower, fern, reeds). Unlike painterly.gdshader (which SYNTHESISES albedo and
// so can't carry a baked texture), this one SAMPLES the GLB's baked albedo (and
// optional normal) and layers the game's per-instance hue/value jitter on top so
// a MultiMesh field of the same prop varies instance-to-instance instead of being
// identical clones. See VISUAL_DIRECTION.md / SHADER_DIRECTION.md.
//
// Perf discipline: opaque (no transparency), one texture fetch (+1 for normal at
// High), vertex-only wind. Wind is a cheap world-phase sine gated to >0, so still
// props pass wind=0 and pay nothing. Tint comes from TWO sources summed:
//   - INSTANCE_CUSTOM  → MultiMesh scatter (PropField.SetInstanceCustomData)
//   - node_var (instance uniform) → single MeshInstance3D via SetInstanceShaderParameter
// A plain instance with neither set reads 0 → neutral, so this is always safe.
// ============================================================================

uniform sampler2D albedo_tex : source_color, filter_linear_mipmap, hint_default_white;
uniform sampler2D normal_tex : hint_normal, filter_linear_mipmap;
uniform bool use_normal = false;
uniform float rough = 0.85;
uniform float metallic = 0.0;
uniform vec3 base_tint : source_color = vec3(1.0, 1.0, 1.0);
uniform float wind = 0.0;          // 0 = static; >0 = sway amount (world units at unit height)
uniform float wind_speed = 1.4;
uniform int quality = 2;           // 0 low, 1 med, 2 high (gates normal map)

// per-single-node jitter (MeshInstance3D). xyz = signed albedo offset (r=v+h, g=v, b=v-h), w unused.
instance uniform vec4 node_var = vec4(0.0);

varying vec4 icustom;

void vertex() {
	icustom = INSTANCE_CUSTOM;
	if (wind > 0.0001) {
		// sway upper verts; normalised meshes stand ~0..1+ in Y, so VERTEX.y is a natural height weight
		float h = clamp(VERTEX.y, 0.0, 1.6);
		vec3 wp = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
		float phase = wp.x * 0.6 + wp.z * 0.6 + TIME * wind_speed;
		VERTEX.x += sin(phase) * wind * h;
		VERTEX.z += cos(phase * 0.9) * wind * h * 0.6;
	}
}

void fragment() {
	vec4 tex = texture(albedo_tex, UV);
	vec3 col = tex.rgb * base_tint + icustom.rgb + node_var.rgb;
	ALBEDO = clamp(col, vec3(0.0), vec3(1.0));
	ROUGHNESS = rough;
	METALLIC = metallic;
	if (quality >= 1 && use_normal) {
		NORMAL_MAP = texture(normal_tex, UV).rgb;
	}
}
