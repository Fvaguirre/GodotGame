shader_type spatial;
// Arcane energy that SWALLOWS the boss's hands. One of these rides each hand bone; `amount` ramps 0→1 as an attack winds up
// and holds until the attack resolves, so the glow is itself a telegraph. Alpha-blended (not additive) so it genuinely
// obscures the hand instead of just tinting it; the colour still pushes past 1.0 so bloom picks it up.
// Perf: two spheres on one enemy, fixed 3-octave value noise, no screen texture (see shaders/CLAUDE.md).
render_mode blend_mix, unshaded, cull_back, depth_draw_never, shadows_disabled;

uniform vec3 core_color : source_color = vec3(0.42, 0.20, 0.95);
uniform vec3 hot_color : source_color = vec3(0.80, 0.66, 1.00);
uniform float amount : hint_range(0.0, 1.0) = 0.0;
uniform float churn = 1.7;
uniform float intensity = 2.1;

float hash13(vec3 p) { return fract(sin(dot(p, vec3(17.13, 91.72, 47.31))) * 43758.5453); }

float vnoise(vec3 p)
{
    vec3 i = floor(p), f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = mix(mix(hash13(i), hash13(i + vec3(1, 0, 0)), f.x), mix(hash13(i + vec3(0, 1, 0)), hash13(i + vec3(1, 1, 0)), f.x), f.y);
    float b = mix(mix(hash13(i + vec3(0, 0, 1)), hash13(i + vec3(1, 0, 1)), f.x), mix(hash13(i + vec3(0, 1, 1)), hash13(i + vec3(1, 1, 1)), f.x), f.y);
    return mix(a, b, f.z);
}

float fbm(vec3 p) { return 0.55 * vnoise(p) + 0.30 * vnoise(p * 2.07) + 0.15 * vnoise(p * 4.13); }

varying vec3 v_local;

void vertex() { v_local = VERTEX; }

void fragment()
{
    float t = TIME * churn;
    float n = fbm(v_local * 3.1 + vec3(0.0, -t, t * 0.4));
    // facing ratio: opaque through the middle of the orb (hides the hand), soft + wispy at the silhouette
    float facing = clamp(dot(normalize(NORMAL), normalize(VIEW)), 0.0, 1.0);
    float a = amount * clamp(pow(facing, 0.7) * (0.45 + 1.05 * n), 0.0, 1.0);
    if (a < 0.015) discard;
    vec3 col = mix(core_color, hot_color, clamp(n * 1.5 - 0.2, 0.0, 1.0));
    ALBEDO = col * (intensity * (0.7 + 0.9 * n));
    ALPHA = a;
}
