shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_never, shadows_disabled;

// Roiling arcane storm-cloud canopy (Phase 3). Applied to a wide squashed dome hanging over the battlefield, viewed from
// below. Animated fbm turbulence churns a dark violet cloud mass shot through with bright plasma veins that pulse and flow.
// Alpha-blended (so the DARK cloud reads, not just glow), unshaded, no depth write. Cheap: one dome, fixed-octave fbm.

uniform vec3  cloud_dark   : source_color = vec3(0.16, 0.05, 0.32);
uniform vec3  cloud_bright : source_color = vec3(0.78, 0.52, 1.0);
uniform float churn = 0.5;
uniform float density = 0.6;

varying vec3 lpos;
varying vec3 wnorm;

float h3(vec3 p){ p = fract(p * 0.3183 + 0.1); p *= 17.0; return fract(p.x * p.y * p.z * (p.x + p.y + p.z)); }
float n3(vec3 p){ vec3 i = floor(p); vec3 f = fract(p); f = f * f * (3.0 - 2.0 * f);
	return mix(mix(mix(h3(i), h3(i+vec3(1,0,0)), f.x), mix(h3(i+vec3(0,1,0)), h3(i+vec3(1,1,0)), f.x), f.y),
	           mix(mix(h3(i+vec3(0,0,1)), h3(i+vec3(1,0,1)), f.x), mix(h3(i+vec3(0,1,1)), h3(i+vec3(1,1,1)), f.x), f.y), f.z); }
float fbm(vec3 p){ float a = 0.0; float m = 0.5; for (int i = 0; i < 4; i++){ a += m * n3(p); p *= 2.0; m *= 0.5; } return a; }

void vertex(){
	lpos = VERTEX;
	wnorm = NORMAL;
}

void fragment(){
	vec3 p = lpos * 0.30;
	float f  = fbm(p + vec3(0.0, -TIME * churn, TIME * churn * 0.5));
	float f2 = fbm(p * 2.3 + vec3(TIME * 0.3, TIME * 0.2, 0.0));
	float dense = f * 0.6 + f2 * 0.4;
	float vein = smoothstep(0.60, 0.85, dense);                 // bright plasma cracks where turbulence peaks
	vec3 col = mix(cloud_dark, cloud_bright, vein);
	col += cloud_bright * pow(vein, 3.0) * 0.7;                 // hot vein cores
	// rim/edge softening so the canopy dissolves rather than ending on a hard disc edge
	float fres = pow(1.0 - abs(dot(normalize(wnorm), normalize(VIEW))), 1.4);
	float a = (density * (0.35 + 0.65 * dense)) * (0.45 + 0.55 * fres);
	ALBEDO = col;
	EMISSION = cloud_bright * pow(vein, 2.0) * 0.9;             // veins glow; the dark mass stays dark
	ALPHA = clamp(a, 0.0, 0.9);
}
