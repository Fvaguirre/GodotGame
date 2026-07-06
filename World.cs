using Godot;
using System.Collections.Generic;

// Procedurally streamed world. Chunks load/unload around the player; each chunk is
// deterministically seeded by its grid coords, so the world is stable when you return.
// World.cs — the arena. Builds the ground/terrain, props/scenery, and defines the play bounds.
// Provides the height data behind Game.SurfaceHeight (used by players, enemies, and Thornlings to sit
// on the ground). Edit here to change the map, arena size, or where things can stand.
public partial class World : Node3D
{
    public const float ChunkSize = 50f;
    public const int LoadRadius = 2;          // 5x5 chunks resident

    private readonly Dictionary<Vector2I, Node3D> _chunks = new();
    private readonly Dictionary<Vector2I, List<Blocker>> _chunkBlockers = new();
    private readonly Dictionary<Vector2I, List<Deck>> _chunkDecks = new();
    private readonly Dictionary<Vector2I, List<Ramp>> _chunkRamps = new();
    private Vector2I _last = new Vector2I(99999, 99999);
    private ulong _worldSeed = (ulong)GD.Randi() ^ 0x9E3779B97F4A7C15UL;
    public ulong Seed => _worldSeed;
    public void SetSeed(ulong s) { _worldSeed = s; }

    // drop every chunk and rebuild around the player with a new (synced) seed — called when the host's world
    // seed arrives on a client, so all machines share the exact same map. (NEW)
    public void Reseed(ulong s, Vector3 playerPos)
    {
        _worldSeed = s;
        foreach (var kv in _chunks) if (GodotObject.IsInstanceValid(kv.Value)) kv.Value.QueueFree();
        _chunks.Clear(); _chunkBlockers.Clear(); _chunkDecks.Clear(); _chunkRamps.Clear();
        _last = new Vector2I(99999, 99999);
        Game.I?.Smashables.Clear();   // those pumpkins were children of the dropped chunks
        Game.I?.Flowers.Clear();      // and the flowers (NEW)
        RebuildBlockers();
        Update(playerPos);
    }

    public void Update(Vector3 playerPos)
    {
        var cc = new Vector2I(Mathf.RoundToInt(playerPos.X / ChunkSize), Mathf.RoundToInt(playerPos.Z / ChunkSize));
        if (cc == _last && _chunks.Count > 0) return;
        _last = cc;

        bool changed = false;

        for (int dx = -LoadRadius; dx <= LoadRadius; dx++)
            for (int dz = -LoadRadius; dz <= LoadRadius; dz++)
            {
                var key = new Vector2I(cc.X + dx, cc.Y + dz);
                if (!_chunks.ContainsKey(key)) { BuildChunk(key); changed = true; }
            }

        var drop = new List<Vector2I>();
        foreach (var key in _chunks.Keys)
            if (Mathf.Abs(key.X - cc.X) > LoadRadius + 1 || Mathf.Abs(key.Y - cc.Y) > LoadRadius + 1) drop.Add(key);
        foreach (var key in drop)
        {
            if (_chunks.TryGetValue(key, out var node) && GodotObject.IsInstanceValid(node)) node.QueueFree();
            _chunks.Remove(key);
            _chunkBlockers.Remove(key);
            _chunkDecks.Remove(key);
            _chunkRamps.Remove(key);
            changed = true;
        }

        if (changed) RebuildBlockers();
    }

    private void RebuildBlockers()
    {
        Game.I.Blockers.Clear();
        foreach (var kv in _chunkBlockers) Game.I.Blockers.AddRange(kv.Value);
        Game.I.Decks.Clear();
        foreach (var kv in _chunkDecks) Game.I.Decks.AddRange(kv.Value);
        Game.I.Ramps.Clear();
        foreach (var kv in _chunkRamps) Game.I.Ramps.AddRange(kv.Value);
    }

    private RandomNumberGenerator Seeded(Vector2I c)
    {
        var rng = new RandomNumberGenerator();
        ulong h = _worldSeed;
        unchecked
        {
            h ^= (ulong)(c.X * 73856093);
            h ^= (ulong)(c.Y * 19349663) << 1;
            h *= 0x100000001B3UL;
        }
        rng.Seed = h;
        return rng;
    }

    // ---- materials --------------------------------------------------------
    private static Material Matte(Color c, float rough = 0.95f, bool outline = true)
        => PropMat(c, outline ? 0.03f : 0f);   // (NEW) procedural toon-detail prop material (was flat Game.Toon)

    // ---- water shader (NEW) -----------------------------------------------
    // A stylized surface: world-space gerstner-ish sine waves displace the mesh (so it visibly rolls and is
    // continuous across chunks), analytic wave normals catch the sun for moving glints, fresnel deepens the
    // colour toward grazing angles, and a drifting sparkle sells the shimmer. No screen/depth textures, so it
    // stays cheap and portable. Shared single material across every water tile.
    private static ShaderMaterial _waterMat;
    private static ShaderMaterial WaterMat()
    {
        if (_waterMat == null)
        {
            var sh = new Shader { Code = WaterCode };
            _waterMat = new ShaderMaterial { Shader = sh };
        }
        return _waterMat;
    }

    // ---- terrain shader (NEW) --------------------------------------------
    // Procedural ground detail so the floor reads as textured earth instead of flat paint: world-space fbm noise gives
    // patchy light/dark variation, flat lit areas grass over (green), dips go dirt-brown, steep faces turn rocky-dark, and a
    // fine speckle adds close-up grain. Uses WORLD position so it's seamless across chunks. One shared material; each chunk's
    // biome tint is fed in via the per-instance `base_color`. Pairs with the rolling-hill geometry + SSAO/SSIL for real depth.
    private static ShaderMaterial _terrainMat;
    public static ShaderMaterial TerrainMat()
    {
        if (_terrainMat == null) _terrainMat = new ShaderMaterial { Shader = new Shader { Code = TerrainCode } };
        return _terrainMat;
    }
    private const string TerrainCode = @"
shader_type spatial;
render_mode cull_disabled;

instance uniform vec3 base_color = vec3(0.06, 0.07, 0.09);

float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453123); }
float vnoise(vec2 p) {
    vec2 i = floor(p); vec2 f = fract(p);
    vec2 u = f * f * (3.0 - 2.0 * f);
    float a = hash(i);
    float b = hash(i + vec2(1.0, 0.0));
    float c = hash(i + vec2(0.0, 1.0));
    float d = hash(i + vec2(1.0, 1.0));
    return mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
}
float fbm(vec2 p) {
    float v = 0.0; float a = 0.5;
    for (int i = 0; i < 4; i++) { v += a * vnoise(p); p *= 2.03; a *= 0.5; }
    return v;
}

varying vec3 wpos;
varying vec3 wnorm;

void vertex() {
    wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    wnorm = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
}

void fragment() {
    vec2 p = wpos.xz;
    float n = fbm(p * 0.35) * 0.6 + fbm(p * 1.4) * 0.28 + fbm(p * 5.5) * 0.12;   // multi-scale ground variation
    vec3 col = base_color * (0.66 + 0.66 * n);                                    // patchy moonlit earth
    float flatness = clamp(wnorm.y, 0.0, 1.0);
    // witchy/fairytale palette: cool enchanted moss on flat lit ground, moonlight pooling violet-blue in the dips
    vec3 moss = col * vec3(0.72, 1.16, 0.92);        // faintly luminous magical green
    vec3 dip  = col * vec3(0.86, 0.80, 1.10);        // cool violet-blue where moonlight settles
    col = mix(dip, moss, smoothstep(0.42, 0.96, flatness) * (0.4 + 0.6 * n));
    col = mix(vec3(0.085, 0.085, 0.125), col, smoothstep(0.34, 0.72, flatness));  // shadowed blue-violet stone on steep faces (not drab grey)
    col += vec3(0.015, 0.045, 0.05) * smoothstep(0.74, 1.0, n);                    // a whisper of moonlit teal shimmer in the brightest patches
    col *= 0.94 + 0.12 * fbm(p * 22.0);                                            // fine close-up speckle
    ALBEDO = col;
    ROUGHNESS = 0.95;
    SPECULAR = 0.2;
}
";

    // ---- prop shader (NEW) -----------------------------------------------
    // Cel-shaded like the old toon material (diffuse_toon/specular_toon + ink outline via next_pass), but with object-space
    // fbm grain layered onto the flat colour so trees/rocks/structures read with surface dimension instead of one flat tone,
    // plus a rim glow for the fairytale feel. Cached per colour so a forest reuses one material.
    private static Shader _propShader;
    private static readonly System.Collections.Generic.Dictionary<uint, ShaderMaterial> _propMats = new();
    public static ShaderMaterial PropMat(Color c, float outline = 0.03f)
    {
        uint key = c.ToRgba32() ^ (outline > 0f ? 1u : 0u);
        if (_propMats.TryGetValue(key, out var cached)) return cached;
        _propShader ??= new Shader { Code = PropCode };
        var m = new ShaderMaterial { Shader = _propShader };
        m.SetShaderParameter("base_color", c);
        if (outline > 0f) m.NextPass = Game.Outline(outline);
        _propMats[key] = m;
        return m;
    }
    private const string PropCode = @"
shader_type spatial;
render_mode cull_back, diffuse_toon, specular_toon;

uniform vec4 base_color : source_color = vec4(0.4, 0.4, 0.4, 1.0);
uniform float rim_amt = 0.28;

float hash3(vec3 p) { return fract(sin(dot(p, vec3(12.9898, 78.233, 37.719))) * 43758.5453); }
float n3(vec3 p) {
    vec3 i = floor(p); vec3 f = fract(p); f = f * f * (3.0 - 2.0 * f);
    return mix(mix(mix(hash3(i + vec3(0,0,0)), hash3(i + vec3(1,0,0)), f.x),
                   mix(hash3(i + vec3(0,1,0)), hash3(i + vec3(1,1,0)), f.x), f.y),
               mix(mix(hash3(i + vec3(0,0,1)), hash3(i + vec3(1,0,1)), f.x),
                   mix(hash3(i + vec3(0,1,1)), hash3(i + vec3(1,1,1)), f.x), f.y), f.z);
}
varying vec3 opos;
void vertex() { opos = VERTEX; }
void fragment() {
    float nz = n3(opos * 3.4) * 0.62 + n3(opos * 11.0) * 0.38;   // object-space grain → dimension on the flat toon colour
    ALBEDO = base_color.rgb * (0.80 + 0.34 * nz);
    ROUGHNESS = 0.92;
    RIM = rim_amt;
    RIM_TINT = 0.4;
}
";

    private const string WaterCode = @"
shader_type spatial;
render_mode cull_disabled, world_vertex_coords, specular_schlick_ggx;

uniform vec4 shallow_color : source_color = vec4(0.22, 0.55, 0.60, 1.0);
uniform vec4 deep_color : source_color = vec4(0.02, 0.12, 0.22, 1.0);
uniform vec4 foam_color : source_color = vec4(0.90, 0.97, 1.0, 1.0);
uniform float wave_amp = 0.13;
uniform float wave_speed = 1.2;
uniform float depth_max = 1.1;     // depth (world units) at which the colour reaches 'deep'
uniform float foam_dist = 0.40;    // shoreline foam band width
uniform float edge_fade = 0.18;    // below this depth the water fades to nothing (kills buggy shallow sheets) (NEW: was 0.07)
uniform float refraction = 0.035;  // how much the surface bends the view of the bottom

uniform sampler2D screen_tex : hint_screen_texture, filter_linear_mipmap;
uniform sampler2D depth_tex : hint_depth_texture, filter_nearest;

varying vec2 wxz;

float wsum(vec2 p, float t) {
    float h = 0.0;
    h += sin(dot(p, vec2(0.95, 0.30)) * 0.55 + t) * 1.00;
    h += sin(dot(p, vec2(-0.38, 0.92)) * 0.85 + t * 1.30) * 0.60;
    h += sin(dot(p, vec2(0.70, -0.70)) * 1.60 + t * 0.80) * 0.35;
    h += sin(dot(p, vec2(0.10, 0.99)) * 2.70 + t * 1.70) * 0.18;   // fine chop
    return h;
}

void vertex() {
    wxz = VERTEX.xz;
    float t = TIME * wave_speed;
    float h = wsum(VERTEX.xz, t);
    VERTEX.y += h * wave_amp;
    float e = 0.25;
    float hx = wsum(VERTEX.xz + vec2(e, 0.0), t);
    float hz = wsum(VERTEX.xz + vec2(0.0, e), t);
    NORMAL = normalize(vec3(-(hx - h) * wave_amp / e, 1.0, -(hz - h) * wave_amp / e));
}

void fragment() {
    // how much water the view ray travels through: scene-behind distance minus this fragment's distance
    float draw_d = texture(depth_tex, SCREEN_UV).x;
    vec4 upos = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, draw_d, 1.0);
    float scene_z = -(upos.z / upos.w);
    float wd = max(scene_z + VERTEX.z, 0.0);   // VERTEX.z is negative (view space) → scene_z - frag_dist
    float dn = clamp(wd / depth_max, 0.0, 1.0);

    // screen-space refraction: nudge the sample by the wave normal, scaled by depth so shallow barely bends
    vec2 ruv = SCREEN_UV + NORMAL.xz * refraction * clamp(wd, 0.0, 1.5);
    vec3 refr = texture(screen_tex, ruv).rgb;

    vec3 watercol = mix(shallow_color.rgb, deep_color.rgb, dn);
    vec3 col = mix(refr, watercol, clamp(dn * 0.80 + 0.18, 0.0, 1.0));   // shallow = mostly the refracted bottom, deep = water colour

    float fres = pow(1.0 - clamp(dot(normalize(NORMAL), normalize(VIEW)), 0.0, 1.0), 4.0);
    col += fres * 0.16;

    // shoreline foam — strongest where the water is shallow, churned by drifting diagonal bands (NEW: was an axis-aligned sin*sin product → grid pattern)
    float foam = 1.0 - smoothstep(0.0, foam_dist, wd);
    float churn = 0.5 + 0.25 * (sin(dot(wxz, vec2(0.9, 0.4)) * 2.3 + TIME * 1.9)
                              + sin(dot(wxz, vec2(-0.5, 0.85)) * 3.1 - TIME * 1.4));
    foam = smoothstep(0.4, 0.95, foam * churn + foam * 0.25);
    col = mix(col, foam_color.rgb, foam);

    ALBEDO = col;
    ROUGHNESS = 0.04;
    METALLIC = 0.0;
    SPECULAR = 0.7;
    ALPHA = max(clamp(wd / edge_fade, 0.0, 1.0), foam);   // fade out at the very shore → no buggy thin sheet
}
";

    // ---- terrain height (NEW) ---------------------------------------------
    public const float HillAmp = 5.5f;   // peak-to-trough hill height; more dramatic relief so basins hold deeper water (NEW)
    public const float WaterLevel = -1.0f;   // global still-water surface Y; basins below this hold water that's deepest in the middle (NEW)

    private static float Hash01(int x, int z, ulong seed)
    {
        ulong h = seed + 0x9E3779B97F4A7C15UL;
        unchecked
        {
            h ^= (ulong)(x * 374761393); h ^= (ulong)(z * 668265263) << 1;
            h *= 0x100000001B3UL; h ^= h >> 29;
        }
        return ((h >> 16) & 0xFFFFFF) / (float)0xFFFFFF;   // 0..1
    }

    private float ValueNoise(float x, float z)
    {
        int x0 = Mathf.FloorToInt(x), z0 = Mathf.FloorToInt(z);
        float fx = x - x0, fz = z - z0;
        float u = fx * fx * (3f - 2f * fx), v = fz * fz * (3f - 2f * fz);   // smoothstep interpolation
        float a = Hash01(x0, z0, _worldSeed), b = Hash01(x0 + 1, z0, _worldSeed);
        float cc = Hash01(x0, z0 + 1, _worldSeed), d = Hash01(x0 + 1, z0 + 1, _worldSeed);
        return Mathf.Lerp(Mathf.Lerp(a, b, u), Mathf.Lerp(cc, d, u), v);
    }

    // smooth rolling terrain height at a world XZ — two octaves of value noise, deterministic from the synced seed
    public float Height(float wx, float wz)
    {
        float n = ValueNoise(wx / 34f, wz / 34f) * 1.0f + ValueNoise(wx / 13f + 50f, wz / 13f + 50f) * 0.4f;
        n /= 1.4f;                          // back to 0..1
        return (n - 0.5f) * HillAmp;        // centered around 0
    }

    private Vector3 HeightNormal(float wx, float wz)
    {
        float e = 0.6f;
        float hx = Height(wx + e, wz) - Height(wx - e, wz);
        float hz = Height(wx, wz + e) - Height(wx, wz - e);
        return new Vector3(-hx, 2f * e, -hz).Normalized();
    }

    // ground Y for a prop placed at chunk-local (lx,lz) — root.Position is the chunk's world origin
    private float GY(Node3D root, float lx, float lz) => Height(root.Position.X + lx, root.Position.Z + lz);

    // a displaced ground patch for one chunk; edges line up with neighbours because Height is continuous
    private ArrayMesh BuildTerrainMesh(Vector2I c)
    {
        int res = 14;
        float half = ChunkSize * 0.5f, ox = c.X * ChunkSize, oz = c.Y * ChunkSize;
        var verts = new List<Vector3>(); var norms = new List<Vector3>();
        var uvs = new List<Vector2>(); var idx = new List<int>();
        for (int gz = 0; gz <= res; gz++)
            for (int gx = 0; gx <= res; gx++)
            {
                float lx = -half + gx / (float)res * ChunkSize;
                float lz = -half + gz / (float)res * ChunkSize;
                verts.Add(new Vector3(lx, Height(ox + lx, oz + lz), lz));
                norms.Add(HeightNormal(ox + lx, oz + lz));
                uvs.Add(new Vector2(gx / (float)res, gz / (float)res));
            }
        int stride = res + 1;
        for (int gz = 0; gz < res; gz++)
            for (int gx = 0; gx < res; gx++)
            {
                int i0 = gz * stride + gx, i1 = i0 + 1, i2 = i0 + stride, i3 = i2 + 1;
                idx.Add(i0); idx.Add(i2); idx.Add(i1);
                idx.Add(i1); idx.Add(i2); idx.Add(i3);
            }
        var arr = new Godot.Collections.Array();
        arr.Resize((int)Mesh.ArrayType.Max);
        arr[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        arr[(int)Mesh.ArrayType.Normal] = norms.ToArray();
        arr[(int)Mesh.ArrayType.TexUV] = uvs.ToArray();
        arr[(int)Mesh.ArrayType.Index] = idx.ToArray();
        var am = new ArrayMesh();
        am.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr);
        return am;
    }

    // ---- chunk build ------------------------------------------------------
    private void BuildChunk(Vector2I c)
    {
        var rng = Seeded(c);
        var root = new Node3D();
        AddChild(root);
        root.Position = new Vector3(c.X * ChunkSize, 0, c.Y * ChunkSize);

        int biome = rng.RandiRange(0, 10);
        var blockers = new List<Blocker>();
        Color ground = biome switch
        {
            1 or 2 => new Color(0.05f, 0.08f, 0.06f),    // forest floor
            3 => new Color(0.04f, 0.07f, 0.09f),         // marsh
            4 => new Color(0.10f, 0.07f, 0.04f),         // pumpkin field dirt
            6 => new Color(0.07f, 0.05f, 0.09f),         // altar ground
            7 => new Color(0.06f, 0.06f, 0.07f),         // graveyard
            8 => new Color(0.05f, 0.07f, 0.05f),         // mushroom grove
            9 => new Color(0.09f, 0.08f, 0.06f),         // hamlet packed dirt (NEW)
            10 => new Color(0.06f, 0.10f, 0.06f),        // meadow green (NEW)
            _ => new Color(0.06f, 0.07f, 0.09f)          // clearing
        };
        // pull every biome toward a shared base so neighbouring tiles differ less (kills the obvious grid), then a
        // gentle per-chunk variance on top (NEW)
        ground = ground.Lerp(new Color(0.06f, 0.07f, 0.07f), 0.45f);
        ground = ground.Lerp(new Color(rng.Randf() * 0.05f, rng.Randf() * 0.05f, rng.Randf() * 0.06f), 0.10f);

        // displaced ground patch (rolling hills); double-sided so winding never hides it (NEW)
        var floor = new MeshInstance3D { Mesh = BuildTerrainMesh(c) };
        floor.MaterialOverride = TerrainMat();   // (NEW) procedural textured ground, seamless across chunks
        floor.SetInstanceShaderParameter("base_color", new Vector3(ground.R, ground.G, ground.B));   // feed this chunk's biome tint
        root.AddChild(floor);

        // Water is a per-chunk plane, so neighbouring chunks must AGREE at their shared edge or the surface ends
        // in a hard straight line. The 5×5 grid samples the chunk's EDGES too, and Height() is continuous, so two
        // adjacent chunks read identical edge values — place water wherever the chunk's lowest point dips below the
        // table. The only chunks left dry have water shallower than the shader's edge_fade everywhere (faded to
        // nothing), so two chunks can only ever disagree where the water is already invisible → no visible seam. (NEW)
        float wh = ChunkSize * 0.5f;
        float deepest = 1e9f;
        for (int sx = 0; sx <= 4; sx++)
            for (int sz = 0; sz <= 4; sz++)
            {
                float hgt = Height(c.X * ChunkSize + (-wh + sx * wh * 0.5f), c.Y * ChunkSize + (-wh + sz * wh * 0.5f));
                if (hgt < deepest) deepest = hgt;
            }
        bool hasWater = deepest < WaterLevel - 0.1f;   // any real dip holds water; shores fade via edge_fade, continuous across seams (NEW)
        if (hasWater)
        {
            // a heavily-subdivided plane so the shader's vertex waves actually show; shared shader material (NEW)
            var water = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(ChunkSize, ChunkSize), SubdivideWidth = 32, SubdivideDepth = 32 } };
            water.MaterialOverride = WaterMat();
            water.Position = new Vector3(0, WaterLevel, 0);
            root.AddChild(water);
        }

        switch (biome)
        {
            case 1: Forest(root, rng, blockers, c, 6); break;
            case 2: Forest(root, rng, blockers, c, 11); break;     // denser variation
            case 3: Marsh(root, rng, blockers, c); break;
            case 4: PumpkinPatch(root, rng, blockers, c); break;
            case 5: House(root, rng, blockers, c); break;
            case 6: Altar(root, rng, blockers, c); break;
            case 7: Graveyard(root, rng, blockers, c); break;
            case 8: MushroomGrove(root, rng, blockers, c); break;
            case 9: Hamlet(root, rng, blockers, c); break;          // a little village (NEW)
            case 10: Meadow(root, rng, blockers, c); break;         // open flower field (NEW)
            default: Clearing(root, rng, blockers, c); break;
        }

        // universal ground detail — grass/flowers everywhere break up the flat tiles and hide the chunk seams (NEW)
        int detail = rng.RandiRange(4, 9);
        for (int i = 0; i < detail; i++) GrassTuft(root, rng, R(rng), R(rng));
        if (biome != 3)
        {
            int fl = rng.RandiRange(0, 3);
            for (int i = 0; i < fl; i++) Flowers(root, rng, R(rng), R(rng));
        }
        if (biome != 3 && biome != 6 && rng.Randf() < 0.2f) SpawnPumpkin(root, rng, R(rng), R(rng));   // the odd wild pumpkin

        // will-o'-wisps drift through the foliage — magical fill light that also gives SSIL colour to bounce (NEW)
        bool wispy = biome == 1 || biome == 2 || biome == 3 || biome == 8 || biome == 10;   // forest/marsh/mushroom/meadow
        int wisps = wispy ? rng.RandiRange(1, 2) : (rng.Randf() < 0.15f ? 1 : 0);
        for (int i = 0; i < wisps; i++) SpawnWisp(root, rng, R(rng), R(rng));

        _chunks[c] = root;
        _chunkBlockers[c] = blockers;

        var decks = new List<Deck>();
        var ramps = new List<Ramp>();
        float structRoll = rng.Randf();
        if (biome != 5 && biome != 6 && biome != 9)   // not on house/altar/hamlet chunks
        {
            if (structRoll < 0.12f) Fort(root, rng, blockers, decks, ramps, c);
            else if (structRoll < 0.26f) Ruins(root, rng, blockers, decks, c);
        }
        _chunkDecks[c] = decks;
        _chunkRamps[c] = ramps;
    }

    // a solid raised stone keep, reached by a stepped ramp
    private void Fort(Node3D root, RandomNumberGenerator rng, List<Blocker> bl, List<Deck> decks, List<Ramp> ramps, Vector2I c)
    {
        float ox = c.X * ChunkSize, oz = c.Y * ChunkSize;
        float lx = rng.RandfRange(-8, 8), lz = rng.RandfRange(-8, 8);
        float baseY = Height(ox + lx, oz + lz);
        float topY = rng.RandfRange(4.5f, 6.5f);
        float hx = rng.RandfRange(6f, 8f), hz = rng.RandfRange(6f, 8f);
        var top = Matte(new Color(0.22f, 0.21f, 0.24f), 0.9f, false);
        var stone = Matte(new Color(0.15f, 0.14f, 0.17f), 0.9f, false);

        // solid body
        float found = 3.5f;   // buried foundation skirt: keep the top where it is, extend the base down so the keep never floats on the downhill side of a slope (NEW)
        var body = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(hx * 2, topY + found, hz * 2) } };
        body.MaterialOverride = stone; body.Position = new Vector3(lx, baseY + (topY - found) / 2f, lz); root.AddChild(body);
        var cap = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(hx * 2 + 0.4f, 0.5f, hz * 2 + 0.4f) } };
        cap.MaterialOverride = top; cap.Position = new Vector3(lx, topY + baseY, lz); root.AddChild(cap);
        // battlements
        foreach (var (mx, mz, sx, sz) in new[] { (0f, hz, hx * 2, 0.6f), (0f, -hz, hx * 2, 0.6f), (hx, 0f, 0.6f, hz * 2), (-hx, 0f, 0.6f, hz * 2) })
        {
            var w = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(sx, 1.1f, sz) } };
            w.MaterialOverride = stone; w.Position = new Vector3(lx + mx, topY + 0.55f + baseY, lz + mz); root.AddChild(w);
        }
        decks.Add(new Deck { Center = new Vector3(ox + lx, 0, oz + lz), Half = new Vector2(hx, hz), TopY = topY + baseY });

        // stepped ramp up the +Z side
        int steps = 8;
        float runLen = topY * 2.8f, rw = 3.6f;
        float z0 = lz + hz;
        for (int i = 0; i < steps; i++)
        {
            float frac = (i + 0.5f) / steps;
            float sy = topY * (1f - frac);
            float sz = z0 + runLen * frac;
            float vis = Mathf.Max(0.4f, sy), stepH = vis + 2.5f;   // buried skirt under each step so the ramp never floats on a slope (NEW)
            var st = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(rw, stepH, 1.2f + runLen / steps) } };
            st.MaterialOverride = top; st.Position = new Vector3(lx, baseY + vis - stepH / 2f, sz); root.AddChild(st);
        }
        ramps.Add(new Ramp { Center = new Vector3(ox + lx, 0, oz + (z0 + runLen / 2f)), Half = new Vector2(rw / 2f, runLen / 2f), YLow = topY + baseY, YHigh = baseY, AlongX = false });
        // notch the battlement where the ramp meets the top so you can step on
        var gap = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(rw + 0.4f, 1.4f, 1.0f) } };
        gap.MaterialOverride = top; gap.Position = new Vector3(lx, topY + 0.55f + baseY, lz + hz); root.AddChild(gap);
    }

    // scattered broken walls plus a solid jump-height platform
    private void Ruins(Node3D root, RandomNumberGenerator rng, List<Blocker> bl, List<Deck> decks, Vector2I c)
    {
        float ox = c.X * ChunkSize, oz = c.Y * ChunkSize;
        var stone = Matte(new Color(0.17f, 0.16f, 0.19f), 0.9f, false);
        int n = rng.RandiRange(4, 7);
        for (int i = 0; i < n; i++)
        {
            float lx = rng.RandfRange(-16, 16), lz = rng.RandfRange(-16, 16);
            float h = rng.RandfRange(1.4f, 3.6f), w = rng.RandfRange(2.5f, 5.5f);
            float yawDeg = rng.RandfRange(0, 180);
            var wall = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(w, h, 1.0f) } };
            wall.MaterialOverride = stone;
            wall.Position = new Vector3(lx, h / 2f + Height(ox + lx, oz + lz), lz);
            wall.RotationDegrees = new Vector3(0, yawDeg, rng.RandfRange(-7, 7));
            root.AddChild(wall);
            // solid the whole wall: a line of overlapping blockers along its long axis
            float yr = Mathf.DegToRad(yawDeg);
            var d = new Vector3(Mathf.Cos(yr), 0, -Mathf.Sin(yr));
            int segs = Mathf.Max(1, Mathf.CeilToInt(w / 1.3f));
            for (int s = 0; s < segs; s++)
            {
                float tt = segs == 1 ? 0f : (s / (float)(segs - 1) - 0.5f);
                var bp = new Vector3(lx, 0, lz) + d * (tt * w);
                bl.Add(new Blocker { Pos = new Vector3(ox + bp.X, 0, oz + bp.Z), Radius = 0.7f });
            }
        }
        float plx = rng.RandfRange(-10, 10), plz = rng.RandfRange(-10, 10), pY = rng.RandfRange(1.6f, 2.6f);
        float pBaseY = Height(ox + plx, oz + plz);
        var slab = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(7, pY, 7) } };
        slab.MaterialOverride = stone; slab.Position = new Vector3(plx, pY / 2f + pBaseY, plz); root.AddChild(slab);
        var cap = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(7.3f, 0.4f, 7.3f) } };
        cap.MaterialOverride = Matte(new Color(0.22f, 0.21f, 0.24f), 0.9f, false); cap.Position = new Vector3(plx, pY + pBaseY, plz); root.AddChild(cap);
        decks.Add(new Deck { Center = new Vector3(ox + plx, 0, oz + plz), Half = new Vector2(3.5f, 3.5f), TopY = pY + pBaseY });
    }

    // local (chunk-space) → world position for a blocker
    private Vector3 World3(Vector2I c, float lx, float lz) => new Vector3(c.X * ChunkSize + lx, 0, c.Y * ChunkSize + lz);
    private float R(RandomNumberGenerator rng) => rng.RandfRange(-ChunkSize * 0.45f, ChunkSize * 0.45f);

    // ---- props ------------------------------------------------------------
    private void KnottedTree(Node3D root, RandomNumberGenerator rng, List<Blocker> bl, Vector2I c, float lx, float lz, bool dead)
    {
        float h = rng.RandfRange(4f, 8f);
        float lean = rng.RandfRange(-0.18f, 0.18f);
        var trunkCol = new Color(0.10f, 0.08f, 0.06f).Lerp(new Color(0.06f, 0.05f, 0.05f), rng.Randf());
        float gy = GY(root, lx, lz);
        var trunk = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.4f, BottomRadius = 0.7f, Height = h } };
        trunk.MaterialOverride = Matte(trunkCol);
        trunk.Position = new Vector3(lx, h / 2f + gy, lz);
        trunk.Rotation = new Vector3(lean, rng.Randf() * 6f, lean * 0.5f);
        root.AddChild(trunk);
        // a couple knotted branches
        int br = rng.RandiRange(2, 4);
        for (int i = 0; i < br; i++)
        {
            var b = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.12f, BottomRadius = 0.22f, Height = rng.RandfRange(1.5f, 3f) } };
            b.MaterialOverride = Matte(trunkCol);
            b.Position = new Vector3(lx, h * rng.RandfRange(0.55f, 0.9f) + gy, lz);
            b.Rotation = new Vector3(rng.RandfRange(0.6f, 1.2f), rng.Randf() * 6f, rng.RandfRange(-0.6f, 0.6f));
            root.AddChild(b);
        }
        if (!dead)
        {
            var canopyCol = new Color(0.04f, 0.13f, 0.08f).Lerp(new Color(0.06f, 0.10f, 0.14f), rng.Randf());
            int blobs = rng.RandiRange(2, 4);
            for (int i = 0; i < blobs; i++)
            {
                float cr = rng.RandfRange(1.6f, 2.6f);
                var canopy = new MeshInstance3D { Mesh = new SphereMesh { Radius = cr, Height = cr * 1.8f } };
                canopy.MaterialOverride = Matte(canopyCol, 1f);
                canopy.Position = new Vector3(lx + rng.RandfRange(-1.2f, 1.2f), h + rng.RandfRange(-0.4f, 1.2f) + gy, lz + rng.RandfRange(-1.2f, 1.2f));
                root.AddChild(canopy);
            }
        }
        bl.Add(new Blocker { Pos = World3(c, lx, lz), Radius = 1.2f });
    }

    private void Rock(Node3D root, RandomNumberGenerator rng, float lx, float lz)
    {
        float s = rng.RandfRange(0.6f, 1.6f);
        var rock = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(s, s * 0.7f, s * rng.RandfRange(0.8f, 1.3f)) } };
        rock.MaterialOverride = Matte(new Color(0.10f, 0.10f, 0.12f));
        rock.Position = new Vector3(lx, s * 0.3f + GY(root, lx, lz), lz);
        rock.Rotation = new Vector3(0, rng.Randf() * 6f, 0);
        root.AddChild(rock);
    }

    private void Mushroom(Node3D root, RandomNumberGenerator rng, float lx, float lz)
    {
        float h = rng.RandfRange(0.4f, 1.1f);
        var stem = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.08f, BottomRadius = 0.12f, Height = h } };
        stem.MaterialOverride = Matte(new Color(0.5f, 0.45f, 0.4f));
        float gy = GY(root, lx, lz);
        stem.Position = new Vector3(lx, h / 2f + gy, lz);
        root.AddChild(stem);
        var cap = new MeshInstance3D { Mesh = new SphereMesh { Radius = rng.RandfRange(0.25f, 0.5f), Height = rng.RandfRange(0.3f, 0.5f) } };
        var capCol = rng.Randf() < 0.5f ? new Color(0.45f, 0.07f, 0.10f) : new Color(0.30f, 0.12f, 0.40f);
        cap.MaterialOverride = Game.ToonEmissive(capCol, 0.6f);
        cap.Position = new Vector3(lx, h + gy, lz);
        root.AddChild(cap);
    }

    private void Reed(Node3D root, RandomNumberGenerator rng, float lx, float lz)
    {
        int n = rng.RandiRange(3, 6);
        for (int i = 0; i < n; i++)
        {
            float h = rng.RandfRange(1.2f, 2.4f);
            var r = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.06f, h, 0.06f) } };
            r.MaterialOverride = Matte(new Color(0.10f, 0.16f, 0.10f));
            r.Position = new Vector3(lx + rng.RandfRange(-0.5f, 0.5f), h / 2f + GY(root, lx, lz), lz + rng.RandfRange(-0.5f, 0.5f));
            r.Rotation = new Vector3(rng.RandfRange(-0.15f, 0.15f), 0, rng.RandfRange(-0.15f, 0.15f));
            root.AddChild(r);
        }
    }

    // ---- biomes -----------------------------------------------------------
    private void Clearing(Node3D root, RandomNumberGenerator rng, List<Blocker> bl, Vector2I c)
    {
        int trees = rng.RandiRange(1, 3);
        for (int i = 0; i < trees; i++) KnottedTree(root, rng, bl, c, R(rng), R(rng), rng.Randf() < 0.3f);
        int rocks = rng.RandiRange(1, 4);
        for (int i = 0; i < rocks; i++) Rock(root, rng, R(rng), R(rng));
    }

    private void Forest(Node3D root, RandomNumberGenerator rng, List<Blocker> bl, Vector2I c, int count)
    {
        for (int i = 0; i < count; i++) KnottedTree(root, rng, bl, c, R(rng), R(rng), rng.Randf() < 0.25f);
        int m = rng.RandiRange(0, 4);
        for (int i = 0; i < m; i++) Mushroom(root, rng, R(rng), R(rng));
    }

    private void Marsh(Node3D root, RandomNumberGenerator rng, List<Blocker> bl, Vector2I c)
    {
        // no own water plane any more — the global water table fills the low ground here, so no double water (NEW)
        int clumps = rng.RandiRange(5, 9);
        for (int i = 0; i < clumps; i++) Reed(root, rng, R(rng), R(rng));
        int dead = rng.RandiRange(1, 3);
        for (int i = 0; i < dead; i++) KnottedTree(root, rng, bl, c, R(rng), R(rng), true);
    }

    private void PumpkinPatch(Node3D root, RandomNumberGenerator rng, List<Blocker> bl, Vector2I c)
    {
        int n = rng.RandiRange(8, 16);
        for (int i = 0; i < n; i++) SpawnPumpkin(root, rng, R(rng), R(rng));   // smashable now (NEW)
        if (rng.Randf() < 0.5f) KnottedTree(root, rng, bl, c, R(rng), R(rng), true);   // a lone gnarled tree
    }

    private void House(Node3D root, RandomNumberGenerator rng, List<Blocker> bl, Vector2I c)
    {
        float lx = rng.RandfRange(-8f, 8f), lz = rng.RandfRange(-8f, 8f);
        float baseY = GY(root, lx, lz);
        float w = rng.RandfRange(6f, 9f), d = rng.RandfRange(6f, 9f), bodyH = rng.RandfRange(4f, 5.5f);
        var bodyCol = new Color(0.09f, 0.07f, 0.06f).Lerp(new Color(0.06f, 0.06f, 0.08f), rng.Randf());
        var body = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(w, bodyH, d) } };
        body.MaterialOverride = Matte(bodyCol);
        body.Position = new Vector3(lx, bodyH / 2f + baseY, lz);
        body.RotationDegrees = new Vector3(0, rng.RandfRange(0, 90), 0);
        root.AddChild(body);
        // sagging roof
        var roof = new MeshInstance3D { Mesh = new PrismMesh { Size = new Vector3(w * 1.15f, bodyH * 0.7f, d * 1.15f) } };
        roof.MaterialOverride = Matte(new Color(0.05f, 0.04f, 0.05f));
        roof.Position = new Vector3(lx, bodyH + bodyH * 0.35f + baseY, lz);
        roof.RotationDegrees = body.RotationDegrees;
        roof.Rotation += new Vector3(0, 0, rng.RandfRange(-0.06f, 0.06f));   // sag
        root.AddChild(roof);
        // a dim window glow (lit or abandoned-dark)
        if (rng.Randf() < 0.6f)
        {
            var win = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(1.0f, 1.0f, 0.1f) } };
            win.MaterialOverride = Game.ToonEmissive(new Color(0.95f, 0.65f, 0.25f), 1.2f);
            win.Position = new Vector3(lx, bodyH * 0.55f + baseY, lz + d / 2f + 0.05f);
            win.RotationDegrees = body.RotationDegrees;
            root.AddChild(win);
            root.AddChild(new OmniLight3D { OmniRange = 9f, LightColor = new Color(0.9f, 0.6f, 0.25f), LightEnergy = 1.3f, Position = new Vector3(lx, bodyH * 0.6f + baseY, lz) });
        }
        bl.Add(new Blocker { Pos = World3(c, lx, lz), Radius = Mathf.Max(w, d) * 0.62f });
        // surrounding fence posts + a tree or two
        int posts = rng.RandiRange(4, 8);
        for (int i = 0; i < posts; i++)
        {
            var fp = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.18f, rng.RandfRange(1.0f, 1.6f), 0.18f) } };
            fp.MaterialOverride = Matte(new Color(0.08f, 0.07f, 0.05f));
            float fx = R(rng), fz = R(rng);
            fp.Position = new Vector3(fx, 0.7f + GY(root, fx, fz), fz);
            fp.Rotation = new Vector3(rng.RandfRange(-0.2f, 0.2f), 0, rng.RandfRange(-0.2f, 0.2f));
            root.AddChild(fp);
        }
        if (rng.Randf() < 0.7f) KnottedTree(root, rng, bl, c, R(rng), R(rng), rng.Randf() < 0.5f);
    }

    private void Altar(Node3D root, RandomNumberGenerator rng, List<Blocker> bl, Vector2I c)
    {
        float lx = rng.RandfRange(-6f, 6f), lz = rng.RandfRange(-6f, 6f);
        float baseY = GY(root, lx, lz);
        var glow = rng.Randf() < 0.5f ? DamageTypes.Col(DamageType.Curse) : DamageTypes.Col(DamageType.Arcane);
        // broken stone ring
        var ring = new MeshInstance3D { Mesh = new TorusMesh { InnerRadius = 3.0f, OuterRadius = 3.6f } };
        ring.MaterialOverride = Matte(new Color(0.12f, 0.12f, 0.14f));
        ring.Position = new Vector3(lx, 0.15f + baseY, lz);
        ring.RotationDegrees = new Vector3(90, 0, 0);
        root.AddChild(ring);
        // faded sigil on the ground
        var sigil = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 2.6f, BottomRadius = 2.6f, Height = 0.04f } };
        var sm = Game.Emissive(glow, 0.5f);
        sm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        var sc = sm.AlbedoColor; sc.A = 0.25f; sm.AlbedoColor = sc;
        sigil.MaterialOverride = sm;
        sigil.Position = new Vector3(lx, 0.06f + baseY, lz);
        root.AddChild(sigil);
        // standing stones
        int stones = rng.RandiRange(3, 6);
        for (int i = 0; i < stones; i++)
        {
            float a = i / (float)stones * Mathf.Tau + rng.RandfRange(-0.2f, 0.2f);
            float sh = rng.RandfRange(2.2f, 3.8f);
            var s = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.8f, sh, 0.5f) } };
            s.MaterialOverride = Matte(new Color(0.11f, 0.11f, 0.13f));
            float sx = lx + Mathf.Cos(a) * 4.2f, sz = lz + Mathf.Sin(a) * 4.2f;
            s.Position = new Vector3(sx, sh / 2f + GY(root, sx, sz), sz);
            s.Rotation = new Vector3(rng.RandfRange(-0.1f, 0.1f), a, rng.RandfRange(-0.12f, 0.12f));
            root.AddChild(s);
            bl.Add(new Blocker { Pos = World3(c, sx, sz), Radius = 0.7f });
        }
        // candle motes
        int candles = rng.RandiRange(2, 4);
        for (int i = 0; i < candles; i++)
            root.AddChild(new OmniLight3D { OmniRange = 6f, LightColor = glow, LightEnergy = 1.2f, Position = new Vector3(lx + R(rng) * 0.3f, 1.2f + baseY, lz + R(rng) * 0.3f) });
        var pillar = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(1.4f, 1.0f, 1.4f) } };
        pillar.MaterialOverride = Game.ToonEmissive(glow, 0.4f);
        pillar.Position = new Vector3(lx, 0.5f + baseY, lz);
        root.AddChild(pillar);
        bl.Add(new Blocker { Pos = World3(c, lx, lz), Radius = 1.2f });
    }

    private void Graveyard(Node3D root, RandomNumberGenerator rng, List<Blocker> bl, Vector2I c)
    {
        int graves = rng.RandiRange(6, 12);
        for (int i = 0; i < graves; i++)
        {
            float lx = R(rng), lz = R(rng);
            float gh = rng.RandfRange(0.9f, 1.6f);
            var g = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(rng.RandfRange(0.6f, 1.0f), gh, 0.2f) } };
            g.MaterialOverride = Matte(new Color(0.13f, 0.13f, 0.15f));
            g.Position = new Vector3(lx, gh / 2f + GY(root, lx, lz), lz);
            g.Rotation = new Vector3(rng.RandfRange(-0.25f, 0.25f), rng.Randf() * 6f, rng.RandfRange(-0.18f, 0.18f));
            root.AddChild(g);
        }
        int dead = rng.RandiRange(1, 3);
        for (int i = 0; i < dead; i++) KnottedTree(root, rng, bl, c, R(rng), R(rng), true);
        if (rng.Randf() < 0.4f) root.AddChild(new OmniLight3D { OmniRange = 10f, LightColor = new Color(0.4f, 0.8f, 0.6f), LightEnergy = 0.6f, Position = new Vector3(R(rng) * 0.4f, 1.5f, R(rng) * 0.4f) });
    }

    private void MushroomGrove(Node3D root, RandomNumberGenerator rng, List<Blocker> bl, Vector2I c)
    {
        int n = rng.RandiRange(12, 22);
        for (int i = 0; i < n; i++) Mushroom(root, rng, R(rng), R(rng));
        int trees = rng.RandiRange(2, 4);
        for (int i = 0; i < trees; i++) KnottedTree(root, rng, bl, c, R(rng), R(rng), false);
    }

    // ---- new props & POIs (NEW) -------------------------------------------
    private void SpawnPumpkin(Node3D root, RandomNumberGenerator rng, float lx, float lz)
    {
        float s = rng.RandfRange(0.4f, 1.0f);
        bool lit = rng.Randf() < 0.22f;
        var pk = new Pumpkin();
        root.AddChild(pk);
        pk.Position = new Vector3(lx, GY(root, lx, lz), lz);
        pk.Init(s, lit, (ulong)rng.Randi() ^ 0x53C3F0A1u);
        if (Game.I != null) Game.I.Smashables.Add(pk);
    }

    private void Flowers(Node3D root, RandomNumberGenerator rng, float lx, float lz)
    {
        var palette = new[]
        {
            new Color(0.9f, 0.3f, 0.5f), new Color(0.85f, 0.8f, 0.35f), new Color(0.6f, 0.4f, 0.9f),
            new Color(0.95f, 0.55f, 0.7f), new Color(0.5f, 0.7f, 1f), new Color(0.95f, 0.6f, 0.25f)
        };
        var pc = palette[rng.RandiRange(0, palette.Length - 1)];
        int nflw = rng.RandiRange(3, 7);
        for (int i = 0; i < nflw; i++)
        {
            float fx = lx + rng.RandfRange(-1.2f, 1.2f), fz = lz + rng.RandfRange(-1.2f, 1.2f);
            float h = rng.RandfRange(0.3f, 0.7f);
            var fl = new Flower();
            root.AddChild(fl);
            fl.Position = new Vector3(fx, GY(root, fx, fz), fz);   // anchored to the terrain
            fl.Init(pc, h, (ulong)rng.Randi());
            if (Game.I != null) Game.I.Flowers.Add(fl);   // register so activity nearby can light them up (NEW)
        }
    }

    private void GrassTuft(Node3D root, RandomNumberGenerator rng, float lx, float lz)
    {
        var gc = new Color(0.10f, 0.20f, 0.10f).Lerp(new Color(0.14f, 0.24f, 0.10f), rng.Randf());
        var gm = Matte(gc, 0.95f, false);
        int blades = rng.RandiRange(3, 6);
        for (int i = 0; i < blades; i++)
        {
            float h = rng.RandfRange(0.4f, 0.9f);
            var b = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.05f, h, 0.05f) } };
            b.MaterialOverride = gm;
            b.Position = new Vector3(lx + rng.RandfRange(-0.3f, 0.3f), h / 2f + GY(root, lx, lz), lz + rng.RandfRange(-0.3f, 0.3f));
            b.Rotation = new Vector3(rng.RandfRange(-0.25f, 0.25f), rng.Randf() * 6f, rng.RandfRange(-0.25f, 0.25f));
            root.AddChild(b);
        }
    }

    // a will-o'-wisp anchored at (lx,lz); mostly cool teal, occasionally a warm amber spark (NEW)
    private void SpawnWisp(Node3D root, RandomNumberGenerator rng, float lx, float lz)
    {
        var w = new Wisp();
        root.AddChild(w);
        w.Position = new Vector3(lx, GY(root, lx, lz), lz);
        Color col = rng.Randf() < 0.7f ? new Color(0.45f, 0.85f, 0.80f) : new Color(0.95f, 0.74f, 0.42f);
        w.Init(col, rng.RandfRange(0.7f, 1.1f), rng.RandfRange(4.5f, 6.5f), rng.Randf() * 6.2831853f);
    }

    private void Lantern(Node3D root, RandomNumberGenerator rng, float lx, float lz)
    {
        float h = rng.RandfRange(1.8f, 2.6f);
        var post = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.07f, BottomRadius = 0.09f, Height = h } };
        post.MaterialOverride = Matte(new Color(0.07f, 0.06f, 0.05f));
        float gy = GY(root, lx, lz);
        post.Position = new Vector3(lx, h / 2f + gy, lz);
        root.AddChild(post);
        var glowCol = new Color(0.95f, 0.6f, 0.25f);
        var lamp = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.35f, 0.45f, 0.35f) } };
        lamp.MaterialOverride = Game.ToonEmissive(glowCol, 1.4f);
        lamp.Position = new Vector3(lx, h + 0.15f + gy, lz);
        root.AddChild(lamp);
        root.AddChild(new OmniLight3D { OmniRange = 8f, LightColor = glowCol, LightEnergy = 1.2f, Position = new Vector3(lx, h + 0.15f + gy, lz) });
    }

    private void Cart(Node3D root, RandomNumberGenerator rng, List<Blocker> bl, Vector2I c, float lx, float lz)
    {
        var wood = Matte(new Color(0.10f, 0.08f, 0.06f));
        float yaw = rng.Randf() * 6f;
        float by = GY(root, lx, lz);
        var body = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(2.6f, 0.9f, 1.5f) } };
        body.MaterialOverride = wood; body.Position = new Vector3(lx, 0.95f + by, lz); body.Rotation = new Vector3(0, yaw, 0);
        root.AddChild(body);
        for (int sgn = -1; sgn <= 1; sgn += 2)
        {
            var wheel = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.55f, BottomRadius = 0.55f, Height = 0.18f } };
            wheel.MaterialOverride = wood;
            wheel.Position = new Vector3(lx + Mathf.Cos(yaw) * 0.9f * sgn, 0.55f + by, lz + Mathf.Sin(yaw) * 0.9f * sgn);
            wheel.RotationDegrees = new Vector3(0, 0, 90);
            root.AddChild(wheel);
        }
        bl.Add(new Blocker { Pos = World3(c, lx, lz), Radius = 1.4f });
    }

    private void Well(Node3D root, RandomNumberGenerator rng, List<Blocker> bl, Vector2I c, float lx, float lz)
    {
        var stone = Matte(new Color(0.16f, 0.15f, 0.17f));
        float by = GY(root, lx, lz);
        var ring = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 1.1f, BottomRadius = 1.1f, Height = 1.0f } };
        ring.MaterialOverride = stone; ring.Position = new Vector3(lx, 0.5f + by, lz); root.AddChild(ring);
        var hole = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.85f, BottomRadius = 0.85f, Height = 1.04f } };
        hole.MaterialOverride = Matte(new Color(0.02f, 0.02f, 0.03f), 1f, false); hole.Position = new Vector3(lx, 0.52f + by, lz); root.AddChild(hole);
        for (int sgn = -1; sgn <= 1; sgn += 2)
        {
            var post = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.14f, 2.0f, 0.14f) } };
            post.MaterialOverride = Matte(new Color(0.08f, 0.07f, 0.05f));
            post.Position = new Vector3(lx + sgn * 0.9f, 1.0f + by, lz); root.AddChild(post);
        }
        var roof = new MeshInstance3D { Mesh = new PrismMesh { Size = new Vector3(2.6f, 0.8f, 1.6f) } };
        roof.MaterialOverride = Matte(new Color(0.05f, 0.04f, 0.05f)); roof.Position = new Vector3(lx, 2.4f + by, lz); root.AddChild(roof);
        bl.Add(new Blocker { Pos = World3(c, lx, lz), Radius = 1.2f });
    }

    // a little cluster of cottages with lanterns, a well, fences and grass — a lived-in village (NEW)
    private void Hamlet(Node3D root, RandomNumberGenerator rng, List<Blocker> bl, Vector2I c)
    {
        int houses = rng.RandiRange(2, 3);
        for (int i = 0; i < houses; i++) House(root, rng, bl, c);
        int lanterns = rng.RandiRange(2, 4);
        for (int i = 0; i < lanterns; i++) Lantern(root, rng, R(rng), R(rng));
        if (rng.Randf() < 0.7f) Well(root, rng, bl, c, R(rng) * 0.5f, R(rng) * 0.5f);
        if (rng.Randf() < 0.6f) Cart(root, rng, bl, c, R(rng), R(rng));
        int fences = rng.RandiRange(4, 8);
        for (int i = 0; i < fences; i++)
        {
            var fp = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.16f, rng.RandfRange(0.9f, 1.5f), 0.16f) } };
            fp.MaterialOverride = Matte(new Color(0.08f, 0.07f, 0.05f));
            fp.Position = new Vector3(R(rng), 0.65f, R(rng));
            fp.Rotation = new Vector3(rng.RandfRange(-0.15f, 0.15f), 0, rng.RandfRange(-0.15f, 0.15f));
            root.AddChild(fp);
        }
        int gr = rng.RandiRange(3, 6);
        for (int i = 0; i < gr; i++) SpawnPumpkin(root, rng, R(rng), R(rng));   // hamlets have pumpkins by the houses
    }

    // an open flower field — colour + life, easy on enemies (good breather chunk) (NEW)
    private void Meadow(Node3D root, RandomNumberGenerator rng, List<Blocker> bl, Vector2I c)
    {
        int patches = rng.RandiRange(8, 14);
        for (int i = 0; i < patches; i++) Flowers(root, rng, R(rng), R(rng));
        int grass = rng.RandiRange(10, 18);
        for (int i = 0; i < grass; i++) GrassTuft(root, rng, R(rng), R(rng));
        if (rng.Randf() < 0.4f) KnottedTree(root, rng, bl, c, R(rng), R(rng), false);
        int pk = rng.RandiRange(0, 3);
        for (int i = 0; i < pk; i++) SpawnPumpkin(root, rng, R(rng), R(rng));
    }
}
