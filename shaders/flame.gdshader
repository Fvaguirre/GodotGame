shader_type spatial;
render_mode blend_add, unshaded, cull_disabled, depth_draw_never, shadows_disabled;

// Painterly animated flame for projectiles (Phase 3 VFX). Drawn on an elongated
// teardrop mesh so the projectile has a COMET SILHOUETTE, not a glowing sphere.
// Local +Z is the tail, local -Z is the hot leading head (mesh is oriented so -Z
// faces travel via LookAt). Flowing fbm noise + a head->tail colour ramp + wispy
// alpha give layered fire. Additive, unshaded, no depth write — cheap and bright.

uniform vec3  hot_color  : source_color = vec3(1.0, 0.95, 0.60);
uniform vec3  mid_color  : source_color = vec3(1.0, 0.45, 0.10);
uniform vec3  cool_color : source_color = vec3(0.60, 0.08, 0.02);
uniform float flow_speed = 3.0;
uniform float half_len   = 0.42;   // half the mesh length along the flame axis (local units)
uniform vec3  flame_axis = vec3(0.0, 0.0, -1.0);   // hot HEAD points this way (projectiles: -Z; upright tongues: -Y = hot base)

varying vec3 lpos;

float h3(vec3 p){ p = fract(p * 0.3183 + 0.1); p *= 17.0; return fract(p.x * p.y * p.z * (p.x + p.y + p.z)); }
float n3(vec3 p){ vec3 i = floor(p); vec3 f = fract(p); f = f * f * (3.0 - 2.0 * f);
	return mix(mix(mix(h3(i), h3(i+vec3(1,0,0)), f.x), mix(h3(i+vec3(0,1,0)), h3(i+vec3(1,1,0)), f.x), f.y),
	           mix(mix(h3(i+vec3(0,0,1)), h3(i+vec3(1,0,1)), f.x), mix(h3(i+vec3(0,1,1)), h3(i+vec3(1,1,1)), f.x), f.y), f.z); }
float fbm(vec3 p){ float a = 0.0; float m = 0.5; for (int i = 0; i < 3; i++){ a += m * n3(p); p *= 2.0; m *= 0.5; } return a; }

void vertex(){ lpos = VERTEX; }

void fragment(){
	// head->tail param along the flame axis: hot head = 1, wispy tail = 0
	float t = clamp((dot(lpos, flame_axis) / half_len) * 0.5 + 0.5, 0.0, 1.0);
	// flame noise flowing toward the tail + flickering in time
	float fn = fbm(lpos * 5.0 + vec3(0.0, 0.0, TIME * flow_speed) + vec3(TIME * 0.7, TIME * 0.5, 0.0));
	// colour ramp cool tail -> mid body -> hot head, perturbed by the noise
	vec3 col = mix(cool_color, mid_color, smoothstep(0.15, 0.6, t + fn * 0.30));
	col = mix(col, hot_color, smoothstep(0.60, 0.95, t + fn * 0.20));
	// wispy alpha: solid at the head, breaking into licks toward the tail
	float a = smoothstep(0.04, 0.35, t) * (0.55 + 0.7 * fn);
	EMISSION = col;
	ALBEDO = col;
	ALPHA = clamp(a, 0.0, 1.0);
}
