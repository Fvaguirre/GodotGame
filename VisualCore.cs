using Godot;
using System.Collections.Generic;

// ============================================================================
// Vis — Phase 0 foundation for the painterly visual overhaul.
// See VISUAL_DIRECTION.md (render identity: full painterly, outlines dropped).
//
// Central factory for the painterly master material (shaders/painterly.gdshader)
// plus a global quality tier. This SUPERSEDES the flat Game.Toon()/ToonEmissive()
// path as surfaces migrate onto it in Phase 1+. Purely additive — nothing here
// touches gameplay, logic, or networking.
//
// Materials are cached by quantized params so scattered repeats share one
// material (fixing the old per-instance StandardMaterial3D allocation); per-tree
// / per-prop / per-enemy variety comes from Vary() writing an INSTANCE uniform,
// so the cache stays small while every instance still looks a little different.
// ============================================================================
public static class Vis
{
	public enum Quality { Low, Medium, High }
	public static Quality Q = Quality.High;
	public static int QInt => (int)Q;

	private static Shader _painterly;
	private static Shader PainterlyShader => _painterly ??= GD.Load<Shader>("res://shaders/painterly.gdshader");

	private static readonly Dictionary<long, ShaderMaterial> _cache = new();
	private static readonly List<ShaderMaterial> _all = new();   // for live quality re-push

	// A painterly opaque surface. `emission*`/`fresnel*` default off so the common
	// case is a rich matte surface with world-space macro + roughness variation.
	public static ShaderMaterial Painterly(
		Color albedo, float rough = 0.9f, float roughVar = 0.15f,
		float macroValue = 0.12f, float macroHue = 0.04f, float macroScale = 0.06f,
		Color? emission = null, float emissionEnergy = 0f, float emissionThreshold = 0.5f,
		float fresnel = 0f, Color? fresnelCol = null,
		float detailScale = 3.0f, float detailValue = 0.09f, Vector3? detailAniso = null)
	{
		Color em = emission ?? new Color(0, 0, 0);
		Vector3 aniso = detailAniso ?? Vector3.One;
		long key = Key(albedo, rough, roughVar, macroValue, macroHue, macroScale,
			em, emissionEnergy, emissionThreshold, fresnel) * 31 + Q6(detailScale) * 7 + Q6(detailValue) * 3 + Q6(aniso.Y);
		if (_cache.TryGetValue(key, out var cached)) return cached;

		var m = new ShaderMaterial { Shader = PainterlyShader };
		m.SetShaderParameter("base_albedo", albedo);
		m.SetShaderParameter("rough", rough);
		m.SetShaderParameter("rough_var", roughVar);
		m.SetShaderParameter("macro_value", macroValue);
		m.SetShaderParameter("macro_hue", macroHue);
		m.SetShaderParameter("macro_scale", macroScale);
		m.SetShaderParameter("detail_scale", detailScale);
		m.SetShaderParameter("detail_value", detailValue);
		m.SetShaderParameter("detail_aniso", aniso);
		m.SetShaderParameter("quality", QInt);
		if (emissionEnergy > 0f)
		{
			m.SetShaderParameter("emission_color", em);
			m.SetShaderParameter("emission_energy", emissionEnergy);
			m.SetShaderParameter("emission_threshold", emissionThreshold);
		}
		if (fresnel > 0f)
		{
			m.SetShaderParameter("fresnel_amt", fresnel);
			m.SetShaderParameter("fresnel_color", fresnelCol ?? new Color(0.6f, 0.7f, 1f));
		}
		_cache[key] = m;
		_all.Add(m);
		return m;
	}

	// ---- typed material presets (Phase 2 differentiation) --------------------------------------------------
	// Distinct painterly treatments so wood reads as wood, stone as stone, etc. — instead of one generic surface.
	// WOOD: warm, matte, LONG VERTICAL grain streaks (detail stretched on Y) + planky value drift.
	public static ShaderMaterial Wood(Color c) =>
		Painterly(c, rough: 0.9f, roughVar: 0.12f, macroValue: 0.14f, macroHue: 0.03f, macroScale: 0.5f,
			detailScale: 5.0f, detailValue: 0.12f, detailAniso: new Vector3(1.0f, 0.18f, 1.0f));
	// STONE: cool-neutral, very matte, fine isotropic grain, gentle mottle.
	public static ShaderMaterial Stone(Color c) =>
		Painterly(c, rough: 0.95f, roughVar: 0.08f, macroValue: 0.11f, macroHue: 0.02f, macroScale: 0.12f,
			detailScale: 3.2f, detailValue: 0.08f);
	// THATCH / straw roof: warm, very rough, fibrous streaks raked along the slope (Y-stretched), stronger mottle.
	public static ShaderMaterial Thatch(Color c) =>
		Painterly(c, rough: 1.0f, roughVar: 0.06f, macroValue: 0.16f, macroHue: 0.04f, macroScale: 0.35f,
			detailScale: 7.0f, detailValue: 0.15f, detailAniso: new Vector3(1.0f, 0.3f, 1.0f));

	// Per-instance hue/value/emission jitter for scattered repeats (foliage, props,
	// enemies). Call AFTER assigning a Painterly material. `hue`/`value` are small
	// (~0.02–0.06); emissionMul adds a fraction of extra glow to that instance.
	public static void Vary(GeometryInstance3D gi, float hue, float value, float emissionMul = 0f)
	{
		gi.SetInstanceShaderParameter("inst_var", new Vector4(value + hue, value, value - hue, emissionMul));
	}

	// Deterministic small jitter from an integer seed (so a given instance is stable).
	public static void VarySeeded(GeometryInstance3D gi, int seed, float hueAmt = 0.03f, float valAmt = 0.06f)
	{
		float h = (Frac(seed * 0.1031f) - 0.5f) * 2f * hueAmt;
		float v = (Frac(seed * 0.0973f + 0.37f) - 0.5f) * 2f * valAmt;
		Vary(gi, h, v);
	}

	// Same jitter, packed as a signed-offset Color for MultiMesh.SetInstanceCustomData (INSTANCE_CUSTOM in the shader).
	// Values are small and signed; a MultiMesh WITHOUT custom data reads 0 → neutral, so this is safe to always set.
	public static Color VaryColorSeeded(int seed, float hueAmt = 0.03f, float valAmt = 0.06f)
	{
		float h = (Frac(seed * 0.1031f) - 0.5f) * 2f * hueAmt;
		float v = (Frac(seed * 0.0973f + 0.37f) - 0.5f) * 2f * valAmt;
		return new Color(v + h, v, v - h, 0f);
	}

	// Live global quality change — re-push the `quality` uniform to every cached mat.
	public static void SetQuality(Quality q)
	{
		Q = q;
		foreach (var m in _all) m.SetShaderParameter("quality", QInt);
	}

	private static float Frac(float x) => x - Mathf.Floor(x);

	private static long Key(Color a, float rough, float roughVar, float mv, float mh, float ms,
		Color em, float ee, float et, float fr)
	{
		unchecked
		{
			long h = 17;
			h = h * 31 + Q6(a.R); h = h * 31 + Q6(a.G); h = h * 31 + Q6(a.B);
			h = h * 31 + Q6(rough); h = h * 31 + Q6(roughVar);
			h = h * 31 + Q6(mv); h = h * 31 + Q6(mh); h = h * 31 + Q6(ms);
			h = h * 31 + Q6(em.R); h = h * 31 + Q6(em.G); h = h * 31 + Q6(em.B);
			h = h * 31 + Q6(ee); h = h * 31 + Q6(et); h = h * 31 + Q6(fr);
			return h;
		}
	}
	private static long Q6(float v) => (long)Mathf.RoundToInt(v * 64f);
}
