shader_type spatial;
render_mode unshaded, cull_disabled;

// Draw material for pooled GPU spark SHARDS (Phase 3 infra experiment). Solid emissive cones (not additive billboards),
// tinted per-particle: the GpuParticles3D feeds each particle's COLOR (process-material Color × the fade ColorRamp), and
// this reads it as both albedo and emission so the shard is bright + defined, then fades via COLOR.a over its lifetime.
uniform float energy = 2.6;

void fragment() {
	ALBEDO = COLOR.rgb;
	EMISSION = COLOR.rgb * energy;
	ALPHA = COLOR.a;
}
