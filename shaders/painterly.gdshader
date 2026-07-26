shader_type spatial;

// ============================================================================
// PAINTERLY MASTER MATERIAL  —  Phase 0 foundation for the visual overhaul.
// See VISUAL_DIRECTION.md / SHADER_DIRECTION.md.
//
// Replaces the old flat cel/toon look. Provides the required painterly layers:
//   - base albedo + WORLD-SPACE macro value/hue variation (large surfaces stop
//     looking uniform)
//   - roughness variation
//   - optional subtle detail normal (quality-gated, off by default)
//   - controlled uniform Fresnel rim
//   - MASKED emission (veins/patches), never full-surface
//   - per-instance hue / value / emission jitter for scattered repeats
//
// Perf discipline (SHADER_DIRECTION.md): no screen texture, no variable-count
// loops (fixed 3-octave fbm), no transparency. `quality` gates the detail normal.
// ============================================================================

uniform vec3  base_albedo : source_color = vec3(0.5, 0.5, 0.5);
uniform float rough = 0.9;
uniform float rough_var = 0.15;          // roughness breakup amount
uniform float macro_scale = 0.06;        // world-space frequency of macro variation
uniform float macro_value = 0.12;        // brightness drift across a surface
uniform float macro_hue = 0.04;          // subtle warm/cool hue drift
uniform float detail_scale = 3.0;        // fine grain frequency (surface texture)
uniform vec3  detail_aniso = vec3(1.0);  // per-axis grain stretch — e.g. (1.0, 0.22, 1.0) = long vertical wood streaks
uniform float detail_value = 0.09;       // fine albedo mottle amount (kept gentle so it reads as grain, not camo)
uniform float normal_strength = 0.22;    // detail-normal bump strength (0 = off; primitives have tangents)
uniform float fresnel_amt = 0.0;         // rim strength (0 = off)
uniform vec3  fresnel_color : source_color = vec3(0.6, 0.7, 1.0);
uniform float fresnel_power = 4.0;
uniform vec3  emission_color : source_color = vec3(0.0, 0.0, 0.0);
uniform float emission_energy = 0.0;     // 0 = no emission
uniform float emission_mask_scale = 0.4; // world-space frequency of the emission mask
uniform float emission_threshold = 0.5;  // higher = tighter emissive veins
uniform int   quality = 2;               // 0 low, 1 med, 2 high

// per-instance jitter for single GeometryInstance3D nodes: xyz = small albedo offset, w = emission mult bonus
instance uniform vec4 inst_var = vec4(0.0, 0.0, 0.0, 0.0);

varying vec3 world_pos;
// per-MultiMesh-instance jitter via INSTANCE_CUSTOM (signed offsets; defaults to 0 = neutral when no custom data)
varying vec4 icustom;

float hash13(vec3 p) {
	p = fract(p * 0.1031);
	p += dot(p, p.yzx + 33.33);
	return fract((p.x + p.y) * p.z);
}

float vnoise(vec3 p) {
	vec3 i = floor(p);
	vec3 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	float n000 = hash13(i + vec3(0.0, 0.0, 0.0));
	float n100 = hash13(i + vec3(1.0, 0.0, 0.0));
	float n010 = hash13(i + vec3(0.0, 1.0, 0.0));
	float n110 = hash13(i + vec3(1.0, 1.0, 0.0));
	float n001 = hash13(i + vec3(0.0, 0.0, 1.0));
	float n101 = hash13(i + vec3(1.0, 0.0, 1.0));
	float n011 = hash13(i + vec3(0.0, 1.0, 1.0));
	float n111 = hash13(i + vec3(1.0, 1.0, 1.0));
	float nx00 = mix(n000, n100, f.x);
	float nx10 = mix(n010, n110, f.x);
	float nx01 = mix(n001, n101, f.x);
	float nx11 = mix(n011, n111, f.x);
	float nxy0 = mix(nx00, nx10, f.y);
	float nxy1 = mix(nx01, nx11, f.y);
	return mix(nxy0, nxy1, f.z);
}

float fbm(vec3 p) {
	float a = 0.0;
	float amp = 0.5;
	a += amp * vnoise(p); p *= 2.02; amp *= 0.5;
	a += amp * vnoise(p); p *= 2.03; amp *= 0.5;
	a += amp * vnoise(p);
	return a;
}

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	icustom = INSTANCE_CUSTOM;
}

void fragment() {
	vec3 wp = world_pos;

	// --- macro variation (large soft painterly gradients) ---
	float m  = fbm(wp * macro_scale);
	float m2 = fbm(wp * macro_scale * 0.37 + vec3(11.7));
	// --- fine detail grain (surface TEXTURE — makes rock read as rock, not a smooth blob) ---
	float d  = fbm(wp * detail_scale * detail_aniso);

	vec3 col = base_albedo + inst_var.rgb + icustom.rgb;
	col *= 1.0 + (m - 0.5) * 2.0 * macro_value;   // large value drift
	col *= 1.0 + (d - 0.5) * 2.0 * detail_value;  // fine mottle
	col.r += (m2 - 0.5) * macro_hue;              // subtle warm/cool split
	col.b -= (m2 - 0.5) * macro_hue;
	ALBEDO = clamp(col, vec3(0.0), vec3(1.0));

	// --- roughness variation (macro + fine, so the key-light spec breaks up) ---
	ROUGHNESS = clamp(rough + (m2 - 0.5) * 2.0 * rough_var + (d - 0.5) * rough_var, 0.04, 1.0);
	METALLIC = 0.0;

	// --- detail-normal bump: real surface relief from the fine grain (quality-gated) ---
	if (quality >= 1 && normal_strength > 0.001) {
		float e = 0.25 / detail_scale;   // sample offset ~a fraction of a grain feature
		float hx = fbm((wp + vec3(e, 0.0, 0.0)) * detail_scale * detail_aniso);
		float hz = fbm((wp + vec3(0.0, 0.0, e)) * detail_scale * detail_aniso);
		vec3 n = normalize(vec3(-(hx - d) * normal_strength * 6.0, -(hz - d) * normal_strength * 6.0, 1.0));
		NORMAL_MAP = n * 0.5 + 0.5;   // encode tangent-space normal (Godot decodes *2-1)
	}

	// --- controlled Fresnel rim ---
	if (fresnel_amt > 0.001) {
		float fr = pow(1.0 - clamp(dot(NORMAL, VIEW), 0.0, 1.0), fresnel_power);
		EMISSION += fresnel_color * fr * fresnel_amt;
	}

	// --- MASKED emission (patches/veins, not the whole surface) ---
	if (emission_energy > 0.001) {
		float mask = fbm(wp * emission_mask_scale);
		float e = smoothstep(emission_threshold, emission_threshold + 0.15, mask);
		EMISSION += emission_color * e * emission_energy * (1.0 + inst_var.w + icustom.a);
	}
}
