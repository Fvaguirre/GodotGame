shader_type spatial;
// GHASTLY WITCH HAIR — soft flowing ENERGY-AURA mane (reference: saturated damage-colour body + thin white-cyan core, soft glow).
// ADDITIVE + soft feathered alpha (NOT hard scissor) so overlapping broad ribbons build into a soft glowing mass that reads as
// flowing energy hair, brightest where many strands overlap. Order-independent (additive), depth-tested, no depth write.
render_mode blend_add, cull_disabled, shadows_disabled, depth_draw_never, unshaded;

uniform vec3 flame_color : source_color = vec3(0.15, 0.85, 0.95);   // SATURATED per-witch damage colour (the hair BODY)
uniform float intensity = 0.9;            // per-strand brightness; overlaps add toward a white-hot core
uniform sampler2D noise_tex : repeat_enable, filter_linear;
uniform float pan_speed = 1.0;
uniform float displace_amt = 0.05;
uniform float flow_curve = 0.34;          // animated S-bend amplitude
uniform float static_wave = 0.12;         // BAKED resting S-curve (wavy even when idle)
uniform float scalp_hug = 0.1;            // curve the top of each strand DOWN over the head dome (attaches at the crown like real hair, not a rigid straight emergence)
uniform float tip_taper = 0.3;            // geometric width taper toward the tip
uniform float strand_count = 4.0;         // striate each broad ribbon into this many thin sub-strands (dark gaps + white cores → stranded hair, not a sheet)
instance uniform float phase = 0.0;

varying float v_len;
varying float v_across;
varying float v_phase;

void vertex() {
	v_len = UV.y;
	v_across = UV.x;
	v_phase = phase;
	float t = TIME * pan_speed + phase * 6.2831;
	VERTEX.x *= mix(1.0, tip_taper, smoothstep(0.0, 1.0, v_len));   // mild taper
	float k = v_len * v_len;
	// SCALP HUG: near the root, curve the strand DOWN over the head dome + hug inward, so it attaches at the crown and drapes over
	// the head (covers the "bald" top) instead of shooting straight out over the hat brim.
	float hug = 1.0 - smoothstep(0.0, 0.4, v_len);
	VERTEX.z += hug * scalp_hug;          // +Z = flow(down) → the top droops down over the crown
	VERTEX.y -= hug * scalp_hug * 0.5;    // hug inward toward the scalp
	VERTEX.x += sin(v_len * 9.0 + phase * 6.2831) * static_wave * k;   // BAKED resting S-curve, ~1.5 periods (per-strand phase varied) → clearly WAVY even idle
	VERTEX.z += cos(v_len * 9.0 + phase * 6.2831) * static_wave * 0.5 * k;   // waggle in depth too → not a flat combed sheet
	VERTEX.x += sin(t * 1.1 + v_len * 2.3) * flow_curve * k;           // + animated drift
	VERTEX.y += sin(t * 1.5 + v_len * 1.8 + 1.2) * flow_curve * 0.6 * k;
	vec2 nuv = UV * vec2(2.0, 1.5) + vec2(t * 0.25, -t);
	VERTEX.x += (texture(noise_tex, nuv).r - 0.5) * displace_amt * v_len;
}

void fragment() {
	float t = TIME * pan_speed + v_phase * 6.2831;
	vec2 nuv = vec2(v_across, v_len) * vec2(3.0, 2.5) + vec2(t * 0.4, -t * 1.4);
	float n = texture(noise_tex, nuv).r;
	float across = abs(v_across - 0.5) * 2.0;                 // 0 centre -> 1 width edge
	// SMOOTH soft wavy lock — each ribbon IS a single strand. NO in-shader striation (that produced a wireframe cross-hatch NET).
	float widthFall = 1.0 - smoothstep(0.35, 1.0, across);    // soft feathered edges
	float tipFall = 1.0 - smoothstep(0.82, 1.0, v_len);       // only the very tip wisps out
	float rootFall = smoothstep(0.0, 0.18, v_len);            // quick fade-in from the (hat-occluded) root
	float flick = 0.75 + 0.25 * n;                            // GENTLE noise variation along the strand (not stripes)
	float coreBright = 1.0 - smoothstep(0.0, 0.55, across);   // soft brighter centre, feathered to the edges (a lit core, no hard lines)
	float a = widthFall * tipFall * rootFall * flick * intensity;
	vec3 col = flame_color * (1.0 + coreBright * 1.1);        // luminous DAMAGE-colour core (never white)
	ALBEDO = col;
	ALPHA = clamp(a, 0.0, 1.0);
}
