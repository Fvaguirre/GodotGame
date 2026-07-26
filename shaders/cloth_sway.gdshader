shader_type spatial;

// ============================================================================
// CLOTH-SWAY GOWN MATERIAL  —  gives a rigid skinned garment the read of cloth.
// See VISUAL_DIRECTION.md / SHADER_DIRECTION.md.
//
// Keeps the garment's PAINTED albedo (sampled from its own baked texture) and
// adds vertex flow:
//   - height looseness: anchored at the waist (high local Y), loose at the hem
//   - ambient idle wind: gentle ripple so she's never a stiff board when still
//   - motion drag: the hem lags OPPOSITE her travel direction
//   - billow: a little lift on jump/fall descent
//
// Perf discipline: a pure vertex offset on ONE small mesh + a textured MATTE
// fragment. No screen texture, no transparency, no added lights or draw calls.
// All amounts are uniforms → a Low quality tier can zero sway_strength/wind.
// ============================================================================

uniform sampler2D albedo_tex : source_color, filter_linear_mipmap;
uniform float sway_top = 1.0;        // local Y where sway begins (waist) — anchored above this
uniform float sway_bottom = 0.0;     // local Y of the hem — maximum sway
uniform vec3  move_dir = vec3(0.0);  // horizontal travel in LOCAL space × speed (0 = standing)
uniform float sway_strength = 0.35;  // hem drag amount when moving
uniform float wind_strength = 0.05;  // ambient idle sway
uniform float wind_speed = 1.6;
uniform float billow = 0.0;          // extra lift on jump/fall (0..1)
uniform float rough = 0.9;

varying vec2 uv;

void vertex() {
	uv = UV;
	float span = max(0.001, sway_top - sway_bottom);
	float loose = clamp((sway_top - VERTEX.y) / span, 0.0, 1.0);
	loose *= loose;   // waist anchored, motion concentrated toward the hem

	// ambient wind — phase varies along the hem so it RIPPLES rather than sliding rigidly
	float ph = VERTEX.x * 2.3 + VERTEX.z * 1.7;
	vec2 wind = vec2(sin(TIME * wind_speed + ph), cos(TIME * wind_speed * 0.8 + ph)) * wind_strength;

	// drag — the hem trails behind her travel direction
	vec2 drag = -move_dir.xz * sway_strength;

	VERTEX += vec3(wind.x + drag.x, billow * 0.25, wind.y + drag.y) * loose;
}

void fragment() {
	ALBEDO = texture(albedo_tex, uv).rgb;
	ROUGHNESS = rough;
	METALLIC = 0.0;
}
