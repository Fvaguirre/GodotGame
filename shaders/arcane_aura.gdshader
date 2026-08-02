shader_type spatial;
// THE HOLLOW MOON, phase 2: the arcane corona that sheathes him for the rest of the fight. Energy licks UP the surface
// (fbm scrolled against the vertical UV), the silhouette is fresnel-rimmed so the shape reads from any angle, and the
// top dissolves into wisps instead of ending on a hard mesh edge. Additive + unshaded so it glows without lighting cost.
// Also reused as the eruption flash on the vortex's finishing stomp.
// Perf: fixed 3-octave fbm, no screen texture, no transparency sorting surprises (see shaders/CLAUDE.md).
render_mode blend_add, unshaded, cull_disabled, depth_draw_never, shadows_disabled;

uniform vec3 tint : source_color = vec3(0.42, 0.20, 0.95);
uniform vec3 hot : source_color = vec3(0.86, 0.78, 1.00);
uniform float amount : hint_range(0.0, 1.0) = 1.0;   // master fade (spawn-in / burst-out)
uniform float speed = 1.0;                            // how fast the energy climbs
uniform float density : hint_range(0.2, 3.0) = 1.0;   // finer vs. broader tongues
uniform float wisp : hint_range(0.0, 1.0) = 0.55;     // how much the top frays out
uniform float intensity = 0.55;                       // HDR punch. Keep low on a body-hugging corona — this is additive
                                                      // and sits over the character, so it blows him out fast.
uniform float opacity : hint_range(0.0, 1.0) = 0.6;   // master alpha cap, so he stays readable through it

float h21(vec2 p) { return fract(sin(dot(p, vec2(41.31, 289.07))) * 43758.5453); }

float vn(vec2 p)
{
    vec2 i = floor(p), f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    return mix(mix(h21(i), h21(i + vec2(1, 0)), f.x), mix(h21(i + vec2(0, 1)), h21(i + vec2(1, 1)), f.x), f.y);
}

float fbm(vec2 p) { return 0.55 * vn(p) + 0.30 * vn(p * 2.11 + 3.7) + 0.15 * vn(p * 4.27 + 11.3); }

// `grow` inflates the surface along its own normals. Applied to a COPY OF THE CHARACTER'S OWN SKINNED MESH, that turns
// the model itself into the aura shell — the corona then has his exact silhouette (horns, ribcage, coat) and animates
// with him, instead of being a primitive capsule bolted around him.
uniform float grow = 0.0;

varying vec3 v_norm;
varying vec3 v_world;

void vertex()
{
    VERTEX += normalize(NORMAL) * grow;
    v_norm = NORMAL;
    v_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment()
{
    float t = TIME * speed;
    // Noise sampled in WORLD space and scrolled upward, so it works on ANY mesh — a skinned character has an arbitrary
    // texture-atlas UV layout, and driving the flow off UV.y would smear the fire across unrelated body parts.
    vec2 q = vec2((v_world.x + v_world.z) * 0.55 * density, v_world.y * 0.5 * density - t * 2.0);
    float n = fbm(q) * 0.65 + fbm(q * 2.3 + 7.1) * 0.35;

    float fres = pow(1.0 - abs(dot(normalize(v_norm), normalize(v_world - CAMERA_POSITION_WORLD))), 2.6);
    float tongues = smoothstep(0.34 + wisp * 0.34, 0.88, n);   // energy licking off the surface, broken into tongues

    // Weighted toward the RIM so the corona reads at the silhouette — but the rim term is ALSO gated by the noise.
    // A flat fresnel wash over a detailed character mesh hits grazing angles almost everywhere and just repaints him
    // one solid colour; multiplying it by n keeps the glow broken into moving licks with the model visible between them.
    float alpha = clamp((tongues * 0.55 + fres * 0.75 * (0.25 + 0.75 * n)) * amount * opacity, 0.0, 1.0);
    if (alpha < 0.01) discard;

    vec3 col = mix(tint, hot, clamp(n * 1.5 - 0.15, 0.0, 1.0)) + fres * hot * 0.25;
    ALBEDO = col * intensity * (0.75 + 0.9 * n) * amount;
    ALPHA = alpha;
}
