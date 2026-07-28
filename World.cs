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
    public const float WorldRadius = 425f;   // (TUNE) the bounded overworld: play area is a disc of this radius around origin, walled by a mountain/cliff ring. Grove + Jungle. (700 → 625 → 575 → 425: −550 diameter total, tightening it up so the map reads full solo)
    public const float PlayerEdgeMargin = -2f;   // (FIX) player-movement clamp: stop at WorldRadius+2, just inside the cliff rock. The boundary ring now lands its GROUND-LEVEL faces at ~WorldRadius+3..6 consistently (see BuildBoundaryRing), so this puts the wall right at the mountains all the way around. Was -10 (and +3 before) → invisible wall short of the peaks.
    public const int LoadRadius = 2;          // full-detail chunks: 5x5 resident (props, grass, collision, gameplay)
    public static int FarRadius => Game.I != null ? Game.I.FarRing : 3;   // (NEW) lite-LOD ring radius — driven by the Render Distance setting (Low 3 / Med 4 / High 5). Trees are GPU-instanced (TreeField) so this is cheap.
    private const int BuildBudget = 3;        // (NEW) chunks built per frame — spreads the cost so streaming never hitches

    private readonly Dictionary<Vector2I, Node3D> _chunks = new();
    private readonly Dictionary<Vector2I, List<Blocker>> _chunkBlockers = new();
    private readonly Dictionary<Vector2I, List<Deck>> _chunkDecks = new();
    private readonly Dictionary<Vector2I, List<Ramp>> _chunkRamps = new();
    private readonly HashSet<Vector2I> _lite = new();                 // (NEW) chunks currently at LOD detail (no collision/detail)
    private readonly List<(Vector2I c, bool full)> _queue = new();    // (NEW) pending builds, processed nearest-first
    private bool _blockersDirty;
    private MeshInstance3D _farTerrain;      // (NEW) coarse Height()-sampled skirt beyond the streamed chunks — ground reaches the horizon
    private Node3D _mtnRing;                  // (NEW) distant low-poly mountains that follow the player, for horizon shape
    private Node3D _boundaryRing;             // (NEW) the FIXED, origin-anchored mountain/cliff wall at WorldRadius — the edge of the bounded overworld
    private TreeField _treeField;            // (NEW) global GPU-instanced tree renderer (one draw call per species-variant)
    private PropField _propField;            // (NEW) GPU-instanced ground scatter (rocks/reeds/ferns/monstera/mushrooms) — same idea for props
    private long _frames;                     // (DEBUG) heartbeat counter for the freeze trace log
    private Vector2I _last = new Vector2I(99999, 99999);
    private ulong _worldSeed = (ulong)GD.Randi() ^ 0x9E3779B97F4A7C15UL;
    public ulong Seed => _worldSeed;
    public void SetSeed(ulong s) { _worldSeed = s; }   // tree meshes are baked lazily on the main thread as chunks stream (see ProcTree.PoolFor)

    // drop every chunk and rebuild around the player with a new (synced) seed — called when the host's world
    // seed arrives on a client, so all machines share the exact same map. (NEW)
    public void Reseed(ulong s, Vector3 playerPos)
    {
        Dbg.Log($"Reseed seed={s} start");
        _worldSeed = s;
        foreach (var kv in _chunks) if (GodotObject.IsInstanceValid(kv.Value)) kv.Value.QueueFree();
        _chunks.Clear(); _chunkBlockers.Clear(); _chunkDecks.Clear(); _chunkRamps.Clear(); _chunkVines.Clear();
        _lite.Clear(); _queue.Clear(); _blockersDirty = false;
        _settleMemo.Clear(); _linkMemo.Clear();   // (NEW) the road network is seed-derived — forget the old world's villages
        _treeField?.Clear();   // drop all GPU tree instances for the old world
        _propField?.Clear();   // …and all GPU prop instances
        if (_mtnRing != null && GodotObject.IsInstanceValid(_mtnRing)) { _mtnRing.QueueFree(); _mtnRing = null; }   // fresh mountains for the new seed (far terrain re-meshes itself)
        if (_boundaryRing != null && GodotObject.IsInstanceValid(_boundaryRing)) { _boundaryRing.QueueFree(); _boundaryRing = null; }   // (NEW) rebuild the fixed cliff wall for the new world
        _last = new Vector2I(99999, 99999);
        Game.I?.Smashables.Clear();   // those pumpkins were children of the dropped chunks
        Game.I?.Flowers.Clear();      // and the flowers (NEW)
        RebuildBlockers();
        Update(playerPos);
        Dbg.Log($"Reseed seed={s} END (chunks={_chunks.Count})");
    }

    // called when the Render Distance setting changes — re-evaluate which chunks should be resident + refit the LOD fade
    public void RefreshStreaming() { _last = new Vector2I(99999, 99999); UpdateFade(); }
    private void UpdateFade()
    {
        float end = (FarRadius + 0.44f) * ChunkSize;   // trees dither-fade out just inside the LOD-ring edge, into the distant backdrop
        ProcTree.SetFade(end - 55f, end);
    }

    public void Update(Vector3 playerPos)
    {
        if ((_frames++ & 511) == 0) Dbg.Log($"heartbeat f={_frames} chunks={_chunks.Count} queue={_queue.Count} pos={playerPos.Round()}");   // ~every 8s: proves the loop is alive
        if (_treeField == null) { _treeField = new TreeField(); AddChild(_treeField); UpdateFade(); }
        if (_propField == null) { _propField = new PropField(); AddChild(_propField); }
        var cc = new Vector2I(Mathf.RoundToInt(playerPos.X / ChunkSize), Mathf.RoundToInt(playerPos.Z / ChunkSize));
        if (_chunks.Count == 0)   // fresh world (spawn / reseed / level-advance): a small solid bubble at once, then stream the rest
        {
            BuildChunk(cc, false);   // full ground under the player
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                    if (dx != 0 || dz != 0) BuildChunk(new Vector2I(cc.X + dx, cc.Y + dz), true);   // cheap lite neighbours; the queue upgrades them
        }
        if (cc != _last) { Dbg.Log($"Update cross to {cc} (chunks={_chunks.Count})"); _last = cc; Restream(cc); RefreshHorizon(cc); Dbg.Log($"Update cross to {cc} restreamed queue={_queue.Count}"); }

        // never let the player stand in an unbuilt or merely-LOD chunk — build/upgrade the one under them immediately
        if (!_chunks.ContainsKey(cc)) BuildChunk(cc, false);
        else if (_lite.Contains(cc)) UpgradeToFull(cc);

        ProcessQueue();
        _treeField.Flush();   // (NEW) rebuild any tree-instance buffers that changed this frame
        _propField?.Flush();  // (NEW) …and prop-instance buffers
        if (_blockersDirty) { RebuildBlockers(); _blockersDirty = false; }
        if (_mtnRing != null) _mtnRing.Position = new Vector3(playerPos.X, 0f, playerPos.Z);   // distant mountains track the player smoothly (skybox-style), so they never jump when crossing a chunk
    }

    // decide which chunks should be resident (full within LoadRadius, lite out to FarRadius), enqueue the missing/upgradable
    // ones, and drop everything that's fallen out of range. Only the desired STATE is computed here; builds happen budgeted.
    private void Restream(Vector2I cc)
    {
        var keep = new HashSet<Vector2I>();
        for (int dx = -FarRadius; dx <= FarRadius; dx++)
            for (int dz = -FarRadius; dz <= FarRadius; dz++)
            {
                var k = new Vector2I(cc.X + dx, cc.Y + dz);
                keep.Add(k);
                bool wantFull = Mathf.Abs(dx) <= LoadRadius && Mathf.Abs(dz) <= LoadRadius;
                bool have = _chunks.ContainsKey(k);
                if (!have) Enqueue(k, wantFull);
                else if (wantFull && _lite.Contains(k)) Enqueue(k, true);   // approaching a LOD chunk → upgrade it to full
            }
        var drop = new List<Vector2I>();
        foreach (var key in _chunks.Keys) if (!keep.Contains(key)) drop.Add(key);
        foreach (var key in drop) DropChunk(key);
        _queue.RemoveAll(e => !keep.Contains(e.c));                            // forget builds we no longer want
        _queue.Sort((a, b) => Dist2(a.c, cc).CompareTo(Dist2(b.c, cc)));       // nearest-first
    }

    private static int Dist2(Vector2I a, Vector2I b) { int dx = a.X - b.X, dz = a.Y - b.Y; return dx * dx + dz * dz; }

    private void Enqueue(Vector2I c, bool full)
    {
        for (int i = 0; i < _queue.Count; i++)
            if (_queue[i].c == c) { if (full && !_queue[i].full) _queue[i] = (c, true); return; }   // upgrade an already-queued lite request
        _queue.Add((c, full));
    }

    private void ProcessQueue()
    {
        int budget = BuildBudget;
        int guard = 0;
        while (budget > 0 && _queue.Count > 0)
        {
            if (++guard > 10000) { Dbg.Log($"ProcessQueue GUARD TRIPPED queue={_queue.Count}"); break; }   // safety: never spin forever
            var (k, full) = _queue[0]; _queue.RemoveAt(0);
            bool have = _chunks.ContainsKey(k);
            if (have && !(full && _lite.Contains(k))) continue;   // already satisfied — doesn't cost budget
            if (full && _lite.Contains(k)) UpgradeToFull(k);
            else BuildChunk(k, !full);
            budget--;
        }
    }

    private void UpgradeToFull(Vector2I c)
    {
        if (_chunks.TryGetValue(c, out var old) && GodotObject.IsInstanceValid(old)) old.QueueFree();
        _chunks.Remove(c); _lite.Remove(c);
        _treeField?.DropChunk(c);   // drop the lite chunk's tree instances before the full rebuild re-adds them
        _propField?.DropChunk(c);   // …and its prop instances
        BuildChunk(c, false);       // rebuilt from the same seed → trees/structures land identically, no visible shift
    }

    private void DropChunk(Vector2I key)
    {
        if (_chunks.TryGetValue(key, out var node) && GodotObject.IsInstanceValid(node)) node.QueueFree();
        _chunks.Remove(key); _lite.Remove(key);
        _chunkBlockers.Remove(key); _chunkDecks.Remove(key); _chunkRamps.Remove(key); _chunkVines.Remove(key);
        _treeField?.DropChunk(key);   // remove this chunk's GPU tree instances
        _propField?.DropChunk(key);   // …and its GPU prop instances
        _blockersDirty = true;
    }

    // ---- distant backdrop: the world beyond the streamed chunks --------------
    // Without this, climbing high (e.g. a vine) shows the streamed area as a floating flat disk in the void. This fills the
    // rest of the view: a coarse ground skirt sampling the SAME Height() as the chunks (so the ground continues seamlessly to
    // the horizon), ringed by low-poly mountains for shape. Both recentre on the player each time they cross a chunk, so the
    // horizon is always there; everything fades into the fog at distance. Purely cosmetic — no collision, no MP sync.
    private void RefreshHorizon(Vector2I cc)
    {
        Dbg.Log($"RefreshHorizon {cc} start");
        float cx = cc.X * ChunkSize, cz = cc.Y * ChunkSize;
        if (_farTerrain == null)
            _farTerrain = new MeshInstance3D { MaterialOverride = Matte(new Color(0.07f, 0.11f, 0.08f)) };   // muted ground; fog tints it with distance
        if (_farTerrain.GetParent() == null) AddChild(_farTerrain);
        _farTerrain.Mesh = BuildFarTerrain(cx, cz);
        _farTerrain.Position = new Vector3(cx, 0, cz);   // stays chunk-snapped (its vertices ARE Height-sampled for this centre)

        if (_mtnRing == null) BuildMountainRing();       // positioned every frame in Update (smooth follow)
        // (NEW) the fixed cliff wall exists only in the bounded overworld (the maze / expedition / sky are their own arenas)
        bool over = Game.I != null && Game.I.InOverworld;
        if (over && _boundaryRing == null) BuildBoundaryRing();
        else if (!over && _boundaryRing != null && GodotObject.IsInstanceValid(_boundaryRing)) { _boundaryRing.QueueFree(); _boundaryRing = null; }
        Dbg.Log($"RefreshHorizon {cc} end");
    }

    // a coarse ground mesh out to ~1300 units, sampling the SAME Height() as the chunks so it continues seamlessly. It sits
    // 4 units BELOW the true surface — with HillAmp≈5.5 the coarse (smoothed) surface can differ from the fine chunks by up
    // to ±HillAmp/2, so a 4-unit drop guarantees the detailed chunks always render on top in the overlap (no z-fighting, no
    // gap) while the far ground stays continuous beyond them.
    private ArrayMesh BuildFarTerrain(float cx, float cz)
    {
        int res = 48; float span = 2600f, cell = span / res, half = span * 0.5f;
        var verts = new List<Vector3>(); var norms = new List<Vector3>(); var idx = new List<int>();
        for (int gz = 0; gz <= res; gz++)
            for (int gx = 0; gx <= res; gx++)
            {
                float lx = -half + gx * cell, lz = -half + gz * cell;
                verts.Add(new Vector3(lx, Height(cx + lx, cz + lz) - 4f, lz));
                norms.Add(Vector3.Up);   // distant + fogged, so flat lighting is fine (and cheap)
            }
        int stride = res + 1;
        for (int gz = 0; gz < res; gz++)
            for (int gx = 0; gx < res; gx++)
            {
                int i0 = gz * stride + gx, i1 = i0 + 1, i2 = i0 + stride, i3 = i2 + 1;
                idx.Add(i0); idx.Add(i2); idx.Add(i1);
                idx.Add(i1); idx.Add(i2); idx.Add(i3);
            }
        var arr = new Godot.Collections.Array(); arr.Resize((int)Mesh.ArrayType.Max);
        arr[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        arr[(int)Mesh.ArrayType.Normal] = norms.ToArray();
        arr[(int)Mesh.ArrayType.Index] = idx.ToArray();
        var am = new ArrayMesh(); am.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr);
        return am;
    }

    private void BuildMountainRing()
    {
        _mtnRing = new Node3D(); AddChild(_mtnRing);
        var mtnMat = Matte(new Color(0.11f, 0.14f, 0.17f));   // hazy blue-grey distant rock; fog does the rest
        var rng = new RandomNumberGenerator { Seed = 0xA17C ^ _worldSeed };
        int n = 44;
        for (int i = 0; i < n; i++)
        {
            float a = i / (float)n * Mathf.Tau + rng.RandfRange(-0.06f, 0.06f);
            float r = rng.RandfRange(820f, 1200f);   // (NEW) pushed OUTSIDE the WorldRadius=700 cliff wall — distant peaks beyond the edge, pure backdrop
            float hgt = rng.RandfRange(140f, 320f), wid = rng.RandfRange(180f, 420f);
            var m = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = wid * 0.03f, BottomRadius = wid * 0.5f, Height = hgt, RadialSegments = 6, Rings = 0 }, MaterialOverride = mtnMat };
            m.Position = new Vector3(Mathf.Cos(a) * r, hgt * 0.5f - 6f, Mathf.Sin(a) * r);
            m.RotationDegrees = new Vector3(0, rng.Randf() * 60f, 0);
            _mtnRing.AddChild(m);
        }
    }

    // (NEW) the play boundary: a continuous, FIXED (origin-anchored) wall of steep cliff-mountains right at WorldRadius. The
    // player is hard-clamped just inside it (Game.ClampToWorld), so this is what you SEE when you reach the edge of the world.
    // Densely overlapped so there are no gaps, and rooted well below ground so no float on the rim's rolling terrain.
    private void BuildBoundaryRing()
    {
        _boundaryRing = new Node3D(); AddChild(_boundaryRing);
        var cliffMat = CliffMat();   // (NEW) real rock_face_03 texture, triplanar so the tall cones don't UV-stretch; dark-toned so fog lightens it with distance
        var rng = new RandomNumberGenerator { Seed = 0xC11FF ^ _worldSeed };
        int n = 120;                                    // enough overlap around the full circle to read as a solid wall
        for (int i = 0; i < n; i++)
        {
            float a = i / (float)n * Mathf.Tau;
            float hgt = rng.RandfRange(150f, 195f), wid = rng.RandfRange(88f, 104f);   // (TUNE) tighter ranges → consistent ground-level face radius so ONE player clamp meets the rock all the way around
            // (FIX) The cones are BURIED 40u, so at GROUND level the cone has narrowed to ~0.8·wid — its visible rock face there
            // sits further out than its buried base. Push the centre so that ground-level face lands ~WorldRadius+5 (just beyond
            // the player clamp), consistently, instead of the old +wid push which put the face 20-40u past the clamp → "invisible
            // wall way in front of the mountains".
            float groundFactor = 1f - 40f / (hgt + 40f);                     // cone radius at ground level as a fraction of wid
            float r = WorldRadius + wid * groundFactor + rng.RandfRange(4f, 8f);   // ground-level inner face ≈ WorldRadius + 4..8
            float gy = Height(Mathf.Cos(a) * r, Mathf.Sin(a) * r);
            var m = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = wid * 0.08f, BottomRadius = wid, Height = hgt + 40f, RadialSegments = 7, Rings = 0 }, MaterialOverride = cliffMat };
            m.Position = new Vector3(Mathf.Cos(a) * r, gy + (hgt + 40f) * 0.5f - 40f, Mathf.Sin(a) * r);   // buried 40u so it never floats on the rim's hills
            m.RotationDegrees = new Vector3(rng.RandfRange(-4f, 4f), rng.Randf() * 360f, rng.RandfRange(-4f, 4f));
            _boundaryRing.AddChild(m);
        }
    }

    public void MarkBlockersDirty() { _blockersDirty = true; }   // (NEW) force a Blockers/Decks/Vines re-flush next Update (used when sky-island persistent decks change)
    private void RebuildBlockers()
    {
        Game.I.Blockers.Clear();
        foreach (var kv in _chunkBlockers) Game.I.Blockers.AddRange(kv.Value);
        Game.I.Blockers.AddRange(Game.I.PersistentBlockers);   // structures that survive streaming (the maze well)
        Game.I.Blockers.AddRange(Game.I.PedestalRimBlockers);  // (NEW) the raised rune-block rims around pedestal daises
        Game.I.Decks.Clear();
        foreach (var kv in _chunkDecks) Game.I.Decks.AddRange(kv.Value);
        Game.I.Decks.AddRange(Game.I.PersistentDecks);   // (NEW) floating sky-island tops survive chunk streaming
        Game.I.Ramps.Clear();
        foreach (var kv in _chunkRamps) Game.I.Ramps.AddRange(kv.Value);
        Game.I.Ramps.AddRange(Game.I.PersistentRamps);   // (NEW) pedestal staircases survive chunk streaming (walkable up onto the dais)
        Game.I.Vines.Clear();   // (NEW) flatten jungle vine launch points, managed with chunks like blockers
        foreach (var kv in _chunkVines) Game.I.Vines.AddRange(kv.Value);
        Game.I.Vines.AddRange(Game.I.PersistentVines);    // (NEW) sky-island vines survive streaming too
    }
    private readonly System.Collections.Generic.Dictionary<Vector2I, List<VineGrab>> _chunkVines = new();

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
        => Vis.Stone(c);   // (PHASE 2) structures default to painterly STONE (statues/graveyard/shrines/temples/wells/altars/cliffs); wood & thatch bits call Vis.Wood/Vis.Thatch directly

    // (NEW) boundary-mountain rock: rock_face_03 diffuse + normal, TRIPLANAR world-space so the tall thin cones don't smear
    // their UVs vertically. Darkened + slightly cool so distance fog reads as depth, matching the old flat cliff tone.
    private static Material _cliffMat;
    private static Material CliffMat()
    {
        if (_cliffMat != null) return _cliffMat;
        var mat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.55f, 0.56f, 0.60f),      // knock the bright rock down toward the old dark craggy tone
            AlbedoTexture = TerrainTex("rock_face_03_diff_4k.jpg"),
            Roughness = 0.96f,
            MetallicSpecular = 0.1f,
            Uv1Triplanar = true,
            Uv1Scale = new Vector3(0.035f, 0.035f, 0.035f),    // world-space tiling over the ~150-195u cones
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
        };
        var nrm = TerrainTex("rock_face_03_nor_4k.png");   // real cracked-rock relief
        if (nrm != null) { mat.NormalEnabled = true; mat.NormalTexture = nrm; mat.NormalScale = 1.0f; }
        _cliffMat = mat;
        return mat;
    }

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
    // One material PER CHUNK now (they share the one Shader resource, and each chunk was already its own draw call, so this
    // costs nothing) — because each chunk carries its own baked PATH MASK texture, and sampler uniforms can't be per-instance.
    private static Shader _terrainShader;
    private static readonly System.Collections.Generic.Dictionary<string, Texture2D> _terrainTexCache = new();
    private static readonly System.Collections.Generic.List<ShaderMaterial> _terrainMats = new();   // live chunk terrain materials, so quality changes re-apply
    // Texture Quality tier: 2 High = full 2k source, 1 Medium = 1k cap, 0 Low = 512 cap. Lower tiers downscale the ground/rock
    // textures in-engine at load → less VRAM on weak GPUs, no extra files shipped. Set via Game.SetTextureQuality (menus + persisted).
    public static int TexQuality { get; private set; } = 2;

    // param name -> source file for every texture the terrain material samples (used to (re)apply at the current quality)
    private static readonly (string param, string file)[] _terrainTexParams =
    {
        ("tex_decay",  "dry_decay_leaves_diff_4k.jpg"), ("tex_ground", "forest_ground_06_diff_4k.jpg"),
        ("tex_leaves", "leaves_forest_ground_diff_4k.jpg"), ("tex_rock", "gray_rocks_diff_4k.jpg"),
        ("tex_sand",   "coast_sand_rocks_02_diff_4k.jpg"), ("tex_river", "dry_riverbed_rock_diff_4k.jpg"),
        ("nrm_decay",  "dry_decay_leaves_nor_4k.png"), ("nrm_rock", "gray_rocks_nor_4k.png"),
        ("nrm_sand",   "coast_sand_rocks_02_nor_4k.png"), ("nrm_river", "dry_riverbed_rock_nor_4k.png"),
    };

    // load a Poly Haven ground texture, downscaled to the current quality cap. Cached per (file, quality). Null-safe.
    private static Texture2D TerrainTex(string file)
    {
        string key = $"{file}@{TexQuality}";
        if (_terrainTexCache.TryGetValue(key, out var cached)) return cached;
        var src = GD.Load<Texture2D>($"res://assets/textures/terrain/{file}");
        Texture2D outT = src;
        if (src != null && TexQuality < 2)
        {
            int cap = TexQuality == 1 ? 1024 : 512;
            var img = src.GetImage();
            if (img != null)
            {
                if (img.IsCompressed()) img.Decompress();
                if (Mathf.Max(img.GetWidth(), img.GetHeight()) > cap) { img.Resize(cap, cap, Image.Interpolation.Lanczos); outT = ImageTexture.CreateFromImage(img); }
            }
        }
        _terrainTexCache[key] = outT;
        return outT;
    }
    private static void ApplyTerrainTextures(ShaderMaterial m)
    {
        foreach (var (param, file) in _terrainTexParams) m.SetShaderParameter(param, TerrainTex(file));
    }
    public static ShaderMaterial TerrainMat(Texture2D pathMask)
    {
        _terrainShader ??= new Shader { Code = TerrainCode };
        var m = new ShaderMaterial { Shader = _terrainShader };
        m.SetShaderParameter("water_level", WaterLevel);
        if (pathMask != null) m.SetShaderParameter("path_mask", pathMask);
        ApplyTerrainTextures(m);   // (NEW) DECAY forest floor base + soil/leaf/rock/sand/riverbed + normals, at the current texture quality
        if (_terrainMats.Count > 96) _terrainMats.RemoveAll(mm => !GodotObject.IsInstanceValid(mm));
        _terrainMats.Add(m);
        return m;
    }

    // (NEW) apply a Texture Quality tier live: re-scale the ground/rock textures and re-set them on every live terrain material + the cliff.
    public static void SetTexQuality(int q)
    {
        TexQuality = Mathf.Clamp(q, 0, 2);
        _terrainMats.RemoveAll(mm => !GodotObject.IsInstanceValid(mm));
        foreach (var mm in _terrainMats) ApplyTerrainTextures(mm);
        if (_cliffMat is StandardMaterial3D sm)
        {
            sm.AlbedoTexture = TerrainTex("rock_face_03_diff_4k.jpg");
            var n = TerrainTex("rock_face_03_nor_4k.png"); if (n != null) sm.NormalTexture = n;
        }
    }
    private const string TerrainCode = @"
shader_type spatial;
render_mode cull_disabled;

instance uniform vec3 base_color = vec3(0.06, 0.07, 0.09);
uniform sampler2D path_mask : hint_default_black, filter_linear;   // R = trodden dirt track, G = laid cobblestone
uniform float water_level = -1.0;

// (NEW) real PBR ground textures (Poly Haven diffuse), blended by zone. Tiled in world space; anisotropic for grazing angles.
uniform sampler2D tex_decay  : source_color, filter_linear_mipmap_anisotropic;   // DOMINANT base: dry decaying forest floor
uniform sampler2D tex_ground : source_color, filter_linear_mipmap_anisotropic;   // secondary soil patches
uniform sampler2D tex_leaves : source_color, filter_linear_mipmap_anisotropic;   // dry-leaf drift overlay
uniform sampler2D tex_rock   : source_color, filter_linear_mipmap_anisotropic;   // slopes / rocky ground
uniform sampler2D tex_sand   : source_color, filter_linear_mipmap_anisotropic;   // shoreline sand
uniform sampler2D tex_river  : source_color, filter_linear_mipmap_anisotropic;   // dry riverbed near the water
// matching NORMAL maps (OpenGL convention, linear/non-color) — real surface relief, blended by the same zone weights
uniform sampler2D nrm_decay  : hint_normal, filter_linear_mipmap_anisotropic;
uniform sampler2D nrm_rock   : hint_normal, filter_linear_mipmap_anisotropic;
uniform sampler2D nrm_sand   : hint_normal, filter_linear_mipmap_anisotropic;
uniform sampler2D nrm_river  : hint_normal, filter_linear_mipmap_anisotropic;
uniform float tex_scale = 0.10;   // world→UV tiling frequency
uniform float tex_normal_str = 1.15;   // how hard the texture normals perturb the surface

// decode an OpenGL normal map to a world-XZ slope offset (terrain is ~+Y up, so tangent XY ≈ world XZ for this stylized floor)
vec2 nrm_slope(sampler2D t, vec2 uv) { vec2 n = texture(t, uv).xy * 2.0 - 1.0; return n; }

float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453123); }
vec2 hash2(vec2 p) { return fract(sin(vec2(dot(p, vec2(127.1, 311.7)), dot(p, vec2(269.5, 183.3)))) * 43758.5453); }
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
// micro-relief height used for the NORMAL bump — cheap (3 lookups) because it's sampled three times per fragment
float relief(vec2 q) { return vnoise(q * 3.0) * 0.55 + vnoise(q * 9.0) * 0.30 + vnoise(q * 31.0) * 0.15; }

// cobblestone cells: .x = per-stone id, .y = distance to the nearest cell BORDER (0 = grout line), .zw = fragment→centre
vec4 cobble(vec2 p) {
    vec2 ip = floor(p); vec2 fp = fract(p);
    float best = 8.0; float second = 8.0; vec2 bestC = vec2(0.0); vec2 bestR = vec2(0.0);
    for (int y = -1; y <= 1; y++) {
        for (int x = -1; x <= 1; x++) {
            vec2 g = vec2(float(x), float(y));
            vec2 o = 0.28 + 0.44 * hash2(ip + g);   // centres pulled toward the middle → rounder, more regular setts
            vec2 r = g + o - fp;
            float d = dot(r, r);
            if (d < best) { second = best; best = d; bestC = ip + g; bestR = r; }
            else if (d < second) { second = d; }
        }
    }
    return vec4(hash(bestC), sqrt(second) - sqrt(best), bestR);
}

varying vec3 wpos;
varying vec3 wnorm;
varying vec2 cuv;

void vertex() {
    wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    wnorm = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
    cuv = UV;   // 0..1 across this chunk — indexes the baked path mask
}

void fragment() {
    vec2 p = wpos.xz;
    float flatness = clamp(wnorm.y, 0.0, 1.0);

    // FAKE PARALLAX: shear the detail lookup along the view direction so the surface texture appears to sit BELOW the
    // polygon. Grazing angles shear most, which is exactly where flat ground used to read as painted-on wallpaper.
    vec3 vdir = normalize(wpos - CAMERA_POSITION_WORLD);
    vec2 par = vdir.xz * 0.22;
    vec2 pd = p + par;

    float n = fbm(p * 0.35) * 0.6 + fbm(p * 1.4) * 0.28 + fbm(pd * 5.5) * 0.12;   // multi-scale ground variation

    // ---- PATHS ------------------------------------------------------------
    // the baked mask is a clean rasterised ribbon; warping its threshold with noise gives it trodden, organic borders
    vec4 pm = texture(path_mask, cuv);
    float warp = (fbm(p * 0.85) - 0.5) * 0.30;
    float dirt = smoothstep(0.34, 0.66, clamp(pm.r + warp, 0.0, 1.0));
    float cob  = smoothstep(0.42, 0.72, clamp(pm.g + warp * 0.5, 0.0, 1.0));
    dirt = max(dirt, cob);   // the cobbles are laid ON cleared ground

    // ---- SHORELINE --------------------------------------------------------
    float above = wpos.y - water_level;
    float beach = (1.0 - smoothstep(0.10, 2.30, above)) * smoothstep(0.30, 0.80, flatness);   // sand only on gentle ground
    beach *= 0.55 + 0.45 * fbm(p * 0.7);            // ragged, uneven sand line rather than a contour ring
    float wet  = (1.0 - smoothstep(-0.55, 0.55, above)) * beach;                                // the darker, glossy tide line

    // ---- REAL PBR GROUND TEXTURES, blended by zone (world-space, seamless). Decay-leaf texture reads across MOST of the
    //      terrain for its leaf detail + relief, but the palette stays varied (greens/soil) — NOT tinted browner. --------------
    vec2 tuv = p * tex_scale;
    // mossy-green hollows (damp, flat) — keep the greens where the old palette had them
    float mossZone = smoothstep(0.50, 0.82, fbm(p * 0.013 + 7.0)) * smoothstep(0.42, 0.80, flatness);
    // sample the maps; two scales on the big ones hides obvious tiling
    vec3 t_decay  = mix(texture(tex_decay, tuv).rgb,       texture(tex_decay, tuv * 0.33 + vec2(2.7)).rgb, 0.4);
    vec3 t_ground = mix(texture(tex_ground, tuv).rgb,      texture(tex_ground, tuv * 0.35 + vec2(4.1)).rgb, 0.4);
    vec3 t_leaves = texture(tex_leaves, tuv * 0.75 + vec2(1.7)).rgb;
    vec3 t_rock   = mix(texture(tex_rock, tuv * 0.55).rgb, texture(tex_rock, tuv * 0.20 + vec2(9.0)).rgb, 0.4);
    vec3 t_river  = texture(tex_river, tuv * 0.65 + vec2(3.0)).rgb;
    // BASE = decay-leaf texture blended across MOST of the terrain (its leaf detail + relief reads widely), interwoven with
    // forest-ground soil so the colour stays varied instead of one flat brown. Decay stays prominent (~55-85% by noise).
    float decayMask = clamp(0.55 + 0.32 * fbm(p * 0.03 + 5.0), 0.0, 1.0);   // decay dominant, forest-ground soil weaves through
    vec3 tex = mix(t_ground, t_decay, decayMask);
    // green forest-leaf drift over the flats + damper hollows — brings the GREENS back so it isn't monochrome brown
    float leafMix = smoothstep(0.42, 0.85, fbm(p * 0.05 + 12.0)) * smoothstep(0.45, 0.85, flatness);
    tex = mix(tex, t_leaves, leafMix * 0.55 + mossZone * 0.35);
    // running weights for the NORMAL blend (start on the decay base)
    vec2 texSlope = nrm_slope(nrm_decay, tuv);
    // dry riverbed rock in the low flat band right around the water (above the wet sand)
    float river = clamp(1.0 - smoothstep(0.2, 2.2, above), 0.0, 1.0) * smoothstep(0.50, 0.85, flatness);
    tex = mix(tex, t_river, river * 0.7);
    texSlope = mix(texSlope, nrm_slope(nrm_river, tuv * 0.65 + vec2(3.0)), river * 0.7);
    // steep faces → rock
    float rockW = 1.0 - smoothstep(0.52, 0.85, flatness);
    tex = mix(tex, t_rock, rockW);
    texSlope = mix(texSlope, nrm_slope(nrm_rock, tuv * 0.55), rockW);
    // GENTLE tint only — keep the textures' own colour (do NOT push the whole floor browner); greens in the moss hollows
    tex *= mix(vec3(1.06, 1.02, 0.92), vec3(0.90, 1.06, 0.84), mossZone);
    vec3 col = mix(tex, base_color, 0.06);            // faint per-chunk biome tint
    col *= 0.78 + 0.42 * n;                           // macro value drift (painterly)
    col = mix(col * vec3(0.70, 0.66, 0.68), col, smoothstep(0.30, 0.70, flatness));   // damp shadowed earth on steep faces
    // fallen-leaf drifts — VIVID red/gold flecks pooling on the flat ground
    float litter = smoothstep(0.70, 0.93, fbm(pd * 6.0)) * smoothstep(0.45, 0.85, flatness);
    col = mix(col, mix(vec3(0.52, 0.13, 0.05), vec3(0.62, 0.43, 0.09), fbm(pd * 11.0)), litter * 0.45);
    col += vec3(0.09, 0.075, 0.025) * smoothstep(0.72, 1.0, n);   // warm sunlit highlights

    // ---- MICRO-RELIEF -----------------------------------------------------
    float e = 0.30;
    float h0 = relief(pd);
    float hx = relief(pd + vec2(e, 0.0));
    float hz = relief(pd + vec2(0.0, e));
    vec2 slope = vec2(hx - h0, hz - h0) / e;
    slope -= texSlope * tex_normal_str;   // (NEW) real material relief from the blended texture normals
    float bumpAmt = 0.55;
    float cavity = clamp(0.55 + 1.4 * (h0 - 0.5), 0.0, 1.0);   // crevices darken, crests catch the moon
    col *= 0.80 + 0.34 * cavity;
    col *= 0.94 + 0.12 * fbm(pd * 22.0);                        // fine close-up grain

    float rough = 0.95;
    float spec = 0.2;

    // ---- SAND (real coastal sand+rocks texture) ---------------------------
    vec3 sand = texture(tex_sand, tuv * 0.9).rgb * vec3(1.12, 1.04, 0.86);   // warmed a touch toward the grove
    sand *= 0.86 + 0.28 * cavity;
    col = mix(col, sand, beach);
    slope = mix(slope, slope - nrm_slope(nrm_sand, tuv * 0.9) * tex_normal_str, beach);   // shore-sand relief
    col = mix(col, col * vec3(0.52, 0.56, 0.62), wet);            // wet sand darkens and cools
    col += vec3(0.10, 0.10, 0.13) * step(0.977, vnoise(pd * 90.0)) * beach;   // mica sparkle in the dry sand
    rough = mix(rough, 0.80, beach);
    rough = mix(rough, 0.18, wet);                                 // glossy tide line catching the moon
    spec = mix(spec, 0.65, wet);
    bumpAmt = mix(bumpAmt, 0.22, beach);                           // sand is fine — flatten the coarse relief

    // ---- DIRT TRACK -------------------------------------------------------
    vec3 earth = mix(vec3(0.105, 0.078, 0.055), vec3(0.165, 0.128, 0.092), fbm(pd * 2.2));
    earth *= 0.82 + 0.30 * cavity;
    earth += vec3(0.05, 0.048, 0.044) * step(0.982, vnoise(pd * 44.0));   // scattered grit
    col = mix(col, earth, dirt);
    rough = mix(rough, 1.0, dirt);
    bumpAmt = mix(bumpAmt, 0.30, dirt);                            // packed flat by boots and cartwheels

    // ---- COBBLESTONE ------------------------------------------------------
    if (cob > 0.002) {
        vec4 cb = cobble(p * 1.25);                                // ~0.8u setts
        float grout = smoothstep(0.0, 0.085, cb.y);                // 0 inside the mortar gap
        vec3 stone = mix(vec3(0.112, 0.112, 0.132), vec3(0.205, 0.198, 0.222), cb.x);
        stone *= 0.88 + 0.24 * vnoise(p * 26.0);                   // per-stone weathering
        vec3 cobCol = mix(vec3(0.052, 0.055, 0.062), stone, grout);   // dark mortar between the setts
        cobCol = mix(cobCol, cobCol * vec3(0.75, 0.92, 0.78), 0.35 * (1.0 - grout));   // moss creeping into the joints
        col = mix(col, cobCol, cob);
        rough = mix(rough, 0.62, cob);
        spec = mix(spec, 0.40, cob);
        // each sett domes up: tilt the normal outward from its cell centre, and hard-crease it at the mortar line
        slope = mix(slope, -cb.zw * 5.0 * grout, cob);
        bumpAmt = mix(bumpAmt, 0.85, cob);
    }

    vec3 worldN = normalize(wnorm + vec3(-slope.x, 0.0, -slope.y) * bumpAmt);
    NORMAL = normalize((VIEW_MATRIX * vec4(worldN, 0.0)).xyz);

    ALBEDO = col;
    ROUGHNESS = rough;
    SPECULAR = spec;
    AO = clamp(0.55 + 0.45 * cavity, 0.0, 1.0);
    AO_LIGHT_AFFECT = 0.55;
}
";

    // ---- prop / structure material -------------------------------------------
    // (PAINTERLY) routed to the painterly master material (Vis.Painterly). Ink outlines DROPPED per the overhaul direction.
    // Backs the big readable surfaces — the cliff/boundary ring, distant mountains, far-terrain skirt, and all structures
    // (huts, statues, shrines, wells, fences). Bold value + roughness variation so the effect reads even on the dark moonlit
    // palette: the roughness break-up scatters the key-light specular, which shows far better than albedo on dark surfaces.
    // `outline` is accepted for call-site compatibility but ignored. macro_scale tuned for medium/large surfaces.
    public static ShaderMaterial PropMat(Color c, float outline = 0.03f)
        => Vis.Painterly(c, rough: 0.93f, roughVar: 0.1f, macroValue: 0.1f, macroHue: 0.03f, macroScale: 0.08f);

    // (NEW) FOLIAGE material — same look as PropMat but the vertex shader SWAYS in a light, gusting wind (self-animated via TIME).
    // Higher props sway more (canopies rustle, grass drifts a little); the whole mesh translates so it reads coherently.
    // Used for leaves/canopies/grass/ferns/fronds/monsteras/reeds; trunks/rocks/structures keep the still PropMat.
    private static Shader _windShader;
    private static readonly System.Collections.Generic.Dictionary<uint, ShaderMaterial> _windMats = new();
    // (HAUNT) the wind shaders reference global uniforms — they MUST be registered before those shaders compile, or the
    // foliage fails to build. Register once, lazily, before the first wind material is made.
    private static bool _hauntWindReg = false;
    public static void EnsureHauntWindGlobals()
    {
        if (_hauntWindReg) return;
        _hauntWindReg = true;
        // GlobalShaderParameterGet is EDITOR-ONLY (spams a perf error at runtime), so DON'T probe — just (re)Add. Add is
        // idempotent-safe: re-adding an existing global simply replaces it, which is fine on an editor assembly reload.
        RenderingServer.GlobalShaderParameterAdd("haunt_pos", RenderingServer.GlobalShaderParameterType.Vec3, Vector3.Zero);
        RenderingServer.GlobalShaderParameterAdd("haunt_rad", RenderingServer.GlobalShaderParameterType.Float, 0f);
        RenderingServer.GlobalShaderParameterAdd("haunt_gust", RenderingServer.GlobalShaderParameterType.Float, 1f);
    }
    public static ShaderMaterial WindMat(Color c, bool outline = true)
    {
        EnsureHauntWindGlobals();
        uint key = c.ToRgba32() ^ (outline ? 2u : 0u);
        if (_windMats.TryGetValue(key, out var cached)) return cached;
        _windShader ??= new Shader { Code = WindCode };
        var m = new ShaderMaterial { Shader = _windShader };
        m.SetShaderParameter("base_color", c);
        // (PAINTERLY) ink outline dropped per the overhaul direction — `outline` kept only for call-site compatibility.
        _windMats[key] = m;
        return m;
    }
    // shared wind: bends from the GROUND (a vertex's WORLD height drives its sway, so bases stay planted and tops arc over —
    // a tree-like bend, not a floating bob). Same code in the outline pass so the ink outline moves in lockstep.
    private const string WindBody = @"
global uniform vec3 haunt_pos;      // (HAUNT) the active hot-zone centre — trees near it whip in the storm wind
global uniform float haunt_rad;     // 0 = no active haunt
global uniform float haunt_gust;    // pulsing storm strength
float hash3(vec3 p){ return fract(sin(dot(p, vec3(12.9898,78.233,37.719)))*43758.5453); }
float n3(vec3 p){ vec3 i=floor(p); vec3 f=fract(p); f=f*f*(3.0-2.0*f);
    return mix(mix(mix(hash3(i),hash3(i+vec3(1,0,0)),f.x),mix(hash3(i+vec3(0,1,0)),hash3(i+vec3(1,1,0)),f.x),f.y),
               mix(mix(hash3(i+vec3(0,0,1)),hash3(i+vec3(1,0,1)),f.x),mix(hash3(i+vec3(0,1,1)),hash3(i+vec3(1,1,1)),f.x),f.y),f.z); }
float fbm3(vec3 p){ float a=0.0; float m=0.5; a+=m*n3(p); p*=2.02; m*=0.5; a+=m*n3(p); p*=2.03; m*=0.5; a+=m*n3(p); return a; }
vec3 wind_off(vec3 v, mat4 model, float tt){
    vec3 wv = (model * vec4(v, 1.0)).xyz;
    float hgt = max(wv.y, 0.0);
    float bend = smoothstep(0.3, 7.0, hgt);                         // planted low → full sway high
    float gust = sin(tt*0.5 + wv.x*0.10 + wv.z*0.08);               // slow rolling gust
    float rustle = sin(tt*2.3 + wv.x*0.6 + wv.z*0.55)*0.16;         // faint leaf rustle
    float amt = (gust + rustle) * bend * 0.16;                      // GENTLE — top of a tall tree sways ~0.15 units, not ~1.4
    vec3 off = vec3(amt*0.8, 0.0, amt*0.45);                        // subtle horizontal push along the wind
    // (HAUNT) inside the hot-zone the wind HOWLS — fast whipping sway, strongest at the crown (bend), strongest near centre
    if (haunt_rad > 0.5) {
        float hd = length(wv.xz - haunt_pos.xz);
        float infl = 1.0 - smoothstep(haunt_rad*0.35, haunt_rad*1.05, hd);
        if (infl > 0.001) {
            float whip = sin(tt*3.4 + wv.x*0.5 + wv.z*0.4) + 0.5*sin(tt*6.1 + wv.z*0.7);
            float s = whip * bend * bend * infl * haunt_gust;       // bend² → upper branches whip far more than lower
            off += vec3(s*1.1, 0.0, s*0.7);
        }
    }
    return off;
}";
    private const string WindCode = @"
shader_type spatial;
render_mode cull_back;                                  // (PAINTERLY) dropped diffuse_toon/specular_toon + ink outline
uniform vec4 base_color : source_color = vec4(0.4,0.4,0.4,1.0);
" + WindBody + @"
varying vec3 opos;
varying vec3 wpos;
void vertex(){ opos = VERTEX; wpos = (MODEL_MATRIX*vec4(VERTEX,1.0)).xyz; VERTEX += wind_off(VERTEX, MODEL_MATRIX, TIME); }
void fragment(){
    float macro = fbm3(wpos * 0.18);                    // clump-to-clump colour drift
    float fine  = fbm3(opos * 4.0);                     // blade/leaf mottle
    vec3 col = base_color.rgb * (0.80 + 0.28*macro + 0.16*fine);
    col.r += (macro-0.5)*0.06;                          // autumn: tips drift gold/russet
    col.g += (fine-0.5)*0.05;
    ALBEDO = clamp(col, vec3(0.0), vec3(1.0));
    ROUGHNESS = 0.9;
    float fres = pow(1.0 - clamp(dot(NORMAL, VIEW), 0.0, 1.0), 3.0);
    EMISSION = base_color.rgb * fres * 0.14;            // soft masked edge translucency (backlit foliage)
}
";
    private const string WindOutlineCode = @"
shader_type spatial;
render_mode cull_front, unshaded;
uniform float grow = 0.03;
" + WindBody + @"
void vertex(){ VERTEX += wind_off(VERTEX, MODEL_MATRIX, TIME); VERTEX += NORMAL * grow; }
void fragment(){ ALBEDO = vec3(0.03, 0.025, 0.04); }
";

    private const string WaterCode = @"
shader_type spatial;
render_mode cull_disabled, world_vertex_coords, specular_schlick_ggx;

uniform vec4 shallow_color : source_color = vec4(0.20, 0.54, 0.55, 1.0);
uniform vec4 deep_color : source_color = vec4(0.012, 0.085, 0.19, 1.0);
uniform vec4 foam_color : source_color = vec4(0.90, 0.97, 1.0, 1.0);
uniform vec4 caustic_color : source_color = vec4(0.42, 0.95, 0.88, 1.0);   // moonlit caustic bands on the bottom
uniform vec4 sky_tint : source_color = vec4(0.28, 0.32, 0.55, 1.0);        // what the surface reflects at grazing angles
uniform float wave_amp = 0.17;
uniform float wave_speed = 1.1;
uniform float depth_max = 1.6;     // depth (world units) at which the colour reaches 'deep'
uniform float foam_dist = 0.45;    // shoreline foam band width
uniform float edge_fade = 0.18;    // below this depth the water fades to nothing (kills buggy shallow sheets)
uniform float refraction = 0.045;  // how much the surface bends the view of the bottom

uniform sampler2D screen_tex : hint_screen_texture, filter_linear_mipmap;
uniform sampler2D depth_tex : hint_depth_texture, filter_nearest;

varying vec2 wxz;
varying float swell;      // baked: how much real, OPEN water is here → how much this surface is allowed to roll
varying float basin;      // baked: basin size alone (drives wavelength — puddles ripple, lakes swell)
varying vec3 wave_n;      // the wave normal in WORLD space, so the fragment stage can perturb it before converting once

float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453123); }
float vnoise(vec2 p) {
    vec2 i = floor(p); vec2 f = fract(p);
    vec2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i), hash(i + vec2(1.0, 0.0)), u.x),
               mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), u.x), u.y);
}

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
    // COLOR is baked per-vertex by World.BuildWaterMesh: .r = depth here, .g = how open the surrounding basin is.
    // A shin-deep pond scores near zero on both and stays glassy; only real open water gets a real swell.
    basin = COLOR.g;
    swell = COLOR.r * COLOR.g;
    float amp = wave_amp * (0.05 + 0.95 * swell);   // never dead-flat — a puddle keeps a whisper of movement
    // and the WAVELENGTH shortens on small water: ponds get tight ripples, lakes get long rolling swell
    float freq = mix(3.2, 1.0, basin);
    float t = TIME * wave_speed * mix(1.5, 1.0, basin);
    vec2 wp = VERTEX.xz * freq;
    float h = wsum(wp, t);
    VERTEX.y += h * amp;
    float e = 0.25;
    float hx = wsum(wp + vec2(e, 0.0) * freq, t);
    float hz = wsum(wp + vec2(0.0, e) * freq, t);
    wave_n = normalize(vec3(-(hx - h) * amp / e, 1.0, -(hz - h) * amp / e));
    NORMAL = wave_n;
}

void fragment() {
    // how much water the view ray travels through: scene-behind distance minus this fragment's distance
    float draw_d = texture(depth_tex, SCREEN_UV).x;
    vec4 upos = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, draw_d, 1.0);
    float scene_z = -(upos.z / upos.w);
    float wd = max(scene_z + VERTEX.z, 0.0);   // VERTEX.z is negative (view space) → scene_z - frag_dist
    float dn = clamp(wd / depth_max, 0.0, 1.0);

    // FINE SURFACE DETAIL — two drifting noise octaves perturb the normal well below vertex resolution, so the water
    // holds its texture right up close instead of going to smooth plastic. Scaled by swell, like everything else.
    vec2 d1 = wxz * 1.7 + vec2(TIME * 0.35, TIME * 0.21);
    vec2 d2 = wxz * 4.3 - vec2(TIME * 0.27, TIME * 0.44);
    float e2 = 0.09;
    float rn = vnoise(d1) * 0.65 + vnoise(d2) * 0.35;
    float rx = vnoise(d1 + vec2(e2, 0.0)) * 0.65 + vnoise(d2 + vec2(e2, 0.0)) * 0.35;
    float rz = vnoise(d1 + vec2(0.0, e2)) * 0.65 + vnoise(d2 + vec2(0.0, e2)) * 0.35;
    float ripple = 0.30 + 0.70 * swell;
    // perturb in WORLD space (where the noise lives), then convert to view space ONCE — otherwise the ripples light
    // as if the camera's axes were the world's, and the detail reads as smeared plastic instead of moving water
    vec3 wN = normalize(wave_n + vec3(-(rx - rn), 0.0, -(rz - rn)) * 5.5 * ripple);
    vec3 N = normalize((VIEW_MATRIX * vec4(wN, 0.0)).xyz);

    // screen-space refraction: nudge the sample by the surface normal, scaled by depth so shallow barely bends
    vec2 ruv = SCREEN_UV + wN.xz * refraction * clamp(wd, 0.0, 1.5);
    vec3 refr = texture(screen_tex, ruv).rgb;

    vec3 watercol = mix(shallow_color.rgb, deep_color.rgb, dn);
    vec3 col = mix(refr, watercol, clamp(dn * 0.80 + 0.18, 0.0, 1.0));   // shallow = mostly the refracted bottom, deep = water colour

    // CAUSTICS — interfering wave fronts focus the moonlight into shifting bright veins on the bottom. Strongest in
    // the shallows (where you can actually see the floor) and gone in deep water.
    vec2 cp = wxz * 1.15;
    float c1 = sin(dot(cp, vec2(0.92, 0.39)) * 2.1 + TIME * 0.9);
    float c2 = sin(dot(cp, vec2(-0.44, 0.90)) * 2.6 - TIME * 1.15);
    float c3 = sin(dot(cp, vec2(0.62, 0.78)) * 3.4 + TIME * 0.7);
    float caust = pow(clamp(0.5 + 0.5 * (c1 * c2 + c3 * 0.4), 0.0, 1.0), 7.0);
    col += caustic_color.rgb * caust * (1.0 - dn) * 0.85 * smoothstep(0.03, 0.35, wd);

    // FRESNEL — grazing angles stop showing the bottom and start reflecting the night sky
    float fres = pow(1.0 - clamp(dot(N, normalize(VIEW)), 0.0, 1.0), 4.0);
    col = mix(col, sky_tint.rgb, fres * 0.55);

    // MOON GLITTER — sparse, twinkling specular chips riding the crests (a broad highlight alone reads as plastic)
    float glint = pow(max(rn, 0.0), 6.0) * (0.35 + 0.65 * swell);
    float twinkle = step(0.86, vnoise(wxz * 12.0 + vec2(TIME * 0.8, -TIME * 0.6)));
    col += vec3(0.55, 0.72, 0.85) * glint * twinkle * 1.6;

    // shoreline foam — strongest where the water is shallow, churned by drifting diagonal bands
    float foam = 1.0 - smoothstep(0.0, foam_dist, wd);
    float churn = 0.5 + 0.25 * (sin(dot(wxz, vec2(0.9, 0.4)) * 2.3 + TIME * 1.9)
                              + sin(dot(wxz, vec2(-0.5, 0.85)) * 3.1 - TIME * 1.4));
    churn += (vnoise(wxz * 3.1 + vec2(TIME * 0.4, 0.0)) - 0.5) * 0.5;   // break up the banding so it reads as froth
    foam = smoothstep(0.4, 0.95, foam * churn + foam * 0.25);
    col = mix(col, foam_color.rgb, foam);

    NORMAL = N;
    ALBEDO = col;
    ROUGHNESS = mix(0.14, 0.04, 0.3 + 0.7 * swell);   // still water is a mirror; churned water scatters
    METALLIC = 0.0;
    SPECULAR = 0.75;
    ALPHA = max(clamp(wd / edge_fade, 0.0, 1.0), foam);   // fade out at the very shore → no buggy thin sheet
}
";

    // (NEW) The water surface used to be a flat PlaneMesh, so every pond rolled with the exact same ocean swell as a
    // full lake — a shin-deep puddle heaving like open sea. The mesh now bakes two values per vertex that the shader
    // reads to scale the waves:
    //   COLOR.r = DEPTH here (0..1 over 3u)     — no swell where there's no water under it
    //   COLOR.g = BASIN SIZE (0..1)             — how much of the surrounding 10-22u is also submerged
    // A big open lake scores high on both and rolls properly; a small pool scores low on size and stays glassy with
    // only a fine ripple, no matter how deep it is. Continuous across chunk seams because both are pure Height() reads.
    private ArrayMesh BuildWaterMesh(Vector2I c)
    {
        const int res = 32;   // (TUNE) denser water grid → smoother wave surface + normals (was 24, read a bit blocky/pixelly up close)
        float half = ChunkSize * 0.5f, ox = c.X * ChunkSize, oz = c.Y * ChunkSize;
        var verts = new List<Vector3>(); var norms = new List<Vector3>();
        var uvs = new List<Vector2>(); var cols = new List<Color>(); var idx = new List<int>();
        for (int gz = 0; gz <= res; gz++)
            for (int gx = 0; gx <= res; gx++)
            {
                float lx = -half + gx / (float)res * ChunkSize;
                float lz = -half + gz / (float)res * ChunkSize;
                float wx = ox + lx, wz = oz + lz;
                float depth01 = Mathf.Clamp((WaterLevel - Height(wx, wz)) / 3f, 0f, 1f);
                // two rings of probes: the near ring decides "is this a pool or a lake edge", the far ring "is this open water"
                float open = 0f;
                for (int k = 0; k < 6; k++)
                {
                    float a = k * Mathf.Tau / 6f + 0.4f;
                    if (Height(wx + Mathf.Cos(a) * 10f, wz + Mathf.Sin(a) * 10f) < WaterLevel) open += 0.4f / 6f;
                    if (Height(wx + Mathf.Cos(a) * 22f, wz + Mathf.Sin(a) * 22f) < WaterLevel) open += 0.6f / 6f;
                }
                verts.Add(new Vector3(lx, 0f, lz));
                norms.Add(Vector3.Up);
                uvs.Add(new Vector2(gx / (float)res, gz / (float)res));
                cols.Add(new Color(depth01, Mathf.Clamp(open, 0f, 1f), 0f, 1f));
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
        arr[(int)Mesh.ArrayType.Color] = cols.ToArray();
        arr[(int)Mesh.ArrayType.Index] = idx.ToArray();
        var am = new ArrayMesh();
        am.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr);
        return am;
    }

    // ---- village paths (NEW) ----------------------------------------------
    // The Grove floor read as one flat painted sheet partly because nothing on it was MADE by anyone. So: settlements are
    // now joined by a real road network. Chunks whose biome roll lands on House(5) or Hamlet(9) are "settlements"; each one
    // links to its nearest neighbouring settlement, and those links are baked — as a winding, hand-drawn-feeling ribbon —
    // into a small per-chunk mask texture that the terrain shader reads (R = dirt track, G = cobbled village square).
    // Deterministic from the world seed and computed in WORLD space, so adjacent chunks always agree at their shared edge.
    private const int MaskRes = 48;                       // ~1u per texel across a 50u chunk; the shader warps the edges anyway
    private const float PathHalf = 2.7f;                  // track half-width
    private const float SquareRadius = 15f;               // cobbled area around a settlement centre
    private const int PathReach = 3;                      // longest link, in chunks (~150u)
    private readonly Dictionary<Vector2I, bool> _settleMemo = new();
    private readonly Dictionary<Vector2I, Vector2I> _linkMemo = new();

    // a chunk is a settlement if its biome roll is House or Hamlet — same first roll BuildChunk makes, so it always matches
    private bool IsSettlement(Vector2I c)
    {
        if (_settleMemo.TryGetValue(c, out bool v)) return v;
        int b = Seeded(c).RandiRange(0, 10);
        v = b == 5 || b == 9;
        _settleMemo[c] = v;
        return v;
    }
    private static Vector3 SettleCenter(Vector2I c) => new Vector3(c.X * ChunkSize, 0f, c.Y * ChunkSize);

    // the one settlement each settlement links to: its nearest neighbour, searched in a window around ITSELF (never around
    // the chunk we happen to be rendering) so every chunk derives the identical global graph
    private bool LinkOf(Vector2I a, out Vector2I b)
    {
        if (_linkMemo.TryGetValue(a, out b)) return b != a;
        Vector2I best = a; float bd = float.MaxValue;
        for (int dz = -PathReach; dz <= PathReach; dz++)
            for (int dx = -PathReach; dx <= PathReach; dx++)
            {
                if (dx == 0 && dz == 0) continue;
                var o = new Vector2I(a.X + dx, a.Y + dz);
                if (!IsSettlement(o)) continue;
                float d = dx * dx + dz * dz;
                // ties broken by coordinate so both endpoints agree on the pairing
                if (d < bd || (d == bd && (o.X < best.X || (o.X == best.X && o.Y < best.Y)))) { bd = d; best = o; }
            }
        _linkMemo[a] = best; b = best;
        return best != a;
    }

    // bake this chunk's slice of the road network. Returns null when nothing crosses it (the shader's black default = no path).
    private ImageTexture BuildPathMask(Vector2I c)
    {
        if (Game.I != null && Game.I.CurBiome == Biome.Rainforest) return null;   // no roads in the jungle — it's untamed
        float ox = c.X * ChunkSize, oz = c.Y * ChunkSize, half = ChunkSize * 0.5f;

        // every settlement whose link could possibly cross this chunk
        var edges = new HashSet<(Vector2I, Vector2I)>();
        var squares = new List<Vector2I>();
        int scan = PathReach + 1;
        for (int dz = -scan; dz <= scan; dz++)
            for (int dx = -scan; dx <= scan; dx++)
            {
                var a = new Vector2I(c.X + dx, c.Y + dz);
                if (!IsSettlement(a)) continue;
                if (Mathf.Abs(dx) <= 1 && Mathf.Abs(dz) <= 1) squares.Add(a);
                if (!LinkOf(a, out var b)) continue;
                var key = (a.X < b.X || (a.X == b.X && a.Y < b.Y)) ? (a, b) : (b, a);   // dedupe both directions
                edges.Add(key);
            }
        if (edges.Count == 0 && squares.Count == 0) return null;

        var data = new byte[MaskRes * MaskRes * 4];
        float px2w = ChunkSize / (MaskRes - 1f);

        // stamp a soft disc into a channel, iterating only the texels the disc can touch
        void Stamp(float wx, float wz, float radius, float soft, int channel)
        {
            float fx = (wx - (ox - half)) / px2w, fz = (wz - (oz - half)) / px2w;
            int r = Mathf.CeilToInt((radius + soft) / px2w) + 1;
            int x0 = Mathf.Max(0, Mathf.FloorToInt(fx) - r), x1 = Mathf.Min(MaskRes - 1, Mathf.CeilToInt(fx) + r);
            int z0 = Mathf.Max(0, Mathf.FloorToInt(fz) - r), z1 = Mathf.Min(MaskRes - 1, Mathf.CeilToInt(fz) + r);
            for (int iz = z0; iz <= z1; iz++)
                for (int ix = x0; ix <= x1; ix++)
                {
                    float ddx = (ix - fx) * px2w, ddz = (iz - fz) * px2w;
                    float d = Mathf.Sqrt(ddx * ddx + ddz * ddz);
                    float v = 1f - Mathf.Clamp((d - radius) / Mathf.Max(soft, 0.01f), 0f, 1f);
                    if (v <= 0f) continue;
                    int idx = (iz * MaskRes + ix) * 4 + channel;
                    byte b = (byte)Mathf.RoundToInt(v * 255f);
                    if (b > data[idx]) data[idx] = b;
                }
        }

        foreach (var (a, b) in edges)
        {
            Vector3 pa = SettleCenter(a), pb = SettleCenter(b);
            Vector3 seg = pb - pa; float len = seg.Length();
            if (len < 1f) continue;
            Vector3 dir = seg / len;
            var side = new Vector3(-dir.Z, 0f, dir.X);
            // a stable per-link wobble so the road MEANDERS instead of ruling a straight line between two dots
            float ph = Hash01(a.X * 31 + b.X, a.Y * 17 + b.Y, _worldSeed) * Mathf.Tau;
            float amp = 5f + Hash01(b.X, a.Y, _worldSeed ^ 0x5DEECE66DUL) * 7f;
            int steps = Mathf.Max(8, Mathf.RoundToInt(len / 1.6f));
            for (int s = 0; s <= steps; s++)
            {
                float t = s / (float)steps;
                float taper = Mathf.Sin(t * Mathf.Pi);   // pinned at both settlements, free to wander in between
                Vector3 q = pa + dir * (len * t) + side * (Mathf.Sin(t * Mathf.Tau * 1.35f + ph) * amp * taper);
                if (q.X < ox - half - 6f || q.X > ox + half + 6f || q.Z < oz - half - 6f || q.Z > oz + half + 6f) continue;
                Stamp(q.X, q.Z, PathHalf, 1.6f, 0);
            }
        }
        foreach (var s in squares)
        {
            var q = SettleCenter(s);
            Stamp(q.X, q.Z, SquareRadius + 4f, 5f, 0);   // the trodden apron the village sits on
            Stamp(q.X, q.Z, SquareRadius, 3.5f, 1);      // laid cobbles in the middle
        }

        var img = Image.CreateFromData(MaskRes, MaskRes, false, Image.Format.Rgba8, data);
        return ImageTexture.CreateFromImage(img);
    }

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
    // Build one chunk. lite=true → an LOD chunk: terrain + trees + big structures (for a populated horizon) but NO ground
    // detail (grass/ferns/flowers/wisps/pickups) and NO collision/deck/vine registration. Trees + structures are placed by
    // the SAME seed+code as a full chunk, so upgrading a lite chunk to full never shifts anything.
    private void BuildChunk(Vector2I c, bool lite)
    {
        var rng = Seeded(c);
        var root = new Node3D();
        AddChild(root);
        root.Position = new Vector3(c.X * ChunkSize, 0, c.Y * ChunkSize);

        int biome = rng.RandiRange(0, 10);
        // (FIX) chunks hugging the boundary cliffs get NO big buildings/structures — their footprints would clip into the wall.
        // Trees are handled per-tree (InCliffBand); this just keeps forts/houses/altars/temples out of the rim band.
        bool edgeChunk = InCliffBand(c.X * ChunkSize, c.Y * ChunkSize, 40f);
        Dbg.Log($"BuildChunk {c} lite={lite} biome={biome} start");
        var blockers = new List<Blocker>();
        var vines = new List<VineGrab>();
        bool jungle = Game.I != null && Game.I.CurBiome == Biome.Rainforest;   // (NEW) Rainforest theming fork
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
        // jungle: lush wet greens over dark loam (overrides the grove palette)
        if (jungle)
            ground = biome switch
            {
                3 => new Color(0.05f, 0.09f, 0.07f),         // riverbank mud
                4 => new Color(0.06f, 0.11f, 0.05f),         // pepper-patch loam
                _ => new Color(0.04f, 0.11f, 0.06f)          // jungle floor
            };
        // pull every biome toward a shared base so neighbouring tiles differ less (kills the obvious grid), then a
        // gentle per-chunk variance on top (NEW)
        ground = ground.Lerp(jungle ? new Color(0.05f, 0.10f, 0.06f) : new Color(0.06f, 0.07f, 0.07f), 0.45f);
        ground = ground.Lerp(new Color(rng.Randf() * 0.05f, rng.Randf() * 0.06f, rng.Randf() * 0.05f), 0.10f);

        // displaced ground patch (rolling hills); double-sided so winding never hides it (NEW)
        var floor = new MeshInstance3D { Mesh = BuildTerrainMesh(c) };
        floor.MaterialOverride = TerrainMat(BuildPathMask(c));   // (NEW) procedural textured ground + this chunk's baked road network
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
            var water = new MeshInstance3D { Mesh = BuildWaterMesh(c) };   // (NEW) carries baked depth + basin-size per vertex
            water.MaterialOverride = WaterMat();
            water.Position = new Vector3(0, WaterLevel, 0);
            root.AddChild(water);
        }

        var decks = new List<Deck>();
        var ramps = new List<Ramp>();
        _emitDecks = decks; _emitRamps = ramps;   // (AUTHORED) so StructureModel can emit template decks/ramps even when a caller (House/Altar/…) only passes blockers

        // --- trees + terrain structures: placed in BOTH full and lite chunks (this is the silhouette that fills the view) ---
        if (jungle)
        {
            switch (biome)
            {
                case 1: JungleGrove(root, rng, blockers, c, 14); break;
                case 2: JungleGrove(root, rng, blockers, c, 22); break;   // very dense
                case 3: RiverBank(root, rng, blockers, c); break;
                case 4: PepperPatch(root, rng, blockers, c); break;
                case 5: VineGrove(root, rng, blockers, vines, c); break;  // tall vine-launch trees
                case 6: VineGrove(root, rng, blockers, vines, c); break;
                case 7: if (edgeChunk) JungleGrove(root, rng, blockers, c, 16); else JungleTemple(root, rng, blockers, c); break;   // ruined jungle temple (not against the wall)
                case 8: JungleGrove(root, rng, blockers, c, 16); break;
                default: JungleClearing(root, rng, blockers, c); break;
            }
        }
        else
        {
            switch (biome)
            {
                case 1: Forest(root, rng, blockers, c, 6); break;
                case 2: Forest(root, rng, blockers, c, 11); break;     // denser variation
                case 3: Marsh(root, rng, blockers, c); break;
                case 4: PumpkinPatch(root, rng, blockers, c); break;
                case 5: if (edgeChunk) Forest(root, rng, blockers, c, 6); else House(root, rng, blockers, c); break;
                case 6: if (edgeChunk) Forest(root, rng, blockers, c, 6); else Altar(root, rng, blockers, c); break;
                case 7: Graveyard(root, rng, blockers, c); break;
                case 8: MushroomGrove(root, rng, blockers, c); break;
                case 9: if (edgeChunk) Forest(root, rng, blockers, c, 6); else Hamlet(root, rng, blockers, c); break;   // a little village (NEW)
                case 10: Meadow(root, rng, blockers, c); break;         // open flower field (NEW)
                default: Clearing(root, rng, blockers, c); break;
            }
        }

        Dbg.Log($"BuildChunk {c} trees done");
        // big structures roll HERE (right after trees, before detail) so lite chunks — which skip detail — roll the SAME
        // structures as their full counterpart (rng is identical up to this point).
        float structRoll = rng.Randf();
        if (edgeChunk) { }   // (FIX) no extra structures in the boundary-cliff band — their footprints would clip the wall
        else if (jungle)
        {
            if (biome != 7 && structRoll < 0.14f) JungleTemple(root, rng, blockers, c);   // stepped stone temple (case 7 already places one)
            else if (structRoll < 0.30f) JungleRuins(root, rng, blockers, c);              // mossy broken pillars & idols
        }
        else if (biome != 5 && biome != 6 && biome != 9)   // not on house/altar/hamlet chunks
        {
            if (structRoll < 0.12f) Fort(root, rng, blockers, decks, ramps, c);
            else if (structRoll < 0.26f) Ruins(root, rng, blockers, decks, ramps, c);
        }

        Dbg.Log($"BuildChunk {c} structs done");
        _scatterBlockers = blockers;   // (NEW) so the ground-detail scatter below avoids the structures just placed
        // --- ground detail: grass, ferns, flowers, wisps, fireflies, pickups. FULL chunks ONLY — this is the bulk of the
        //     per-chunk node cost, and its rng comes LAST so skipping it never shifts the trees/structures above. ---
        if (!lite)
        {
            if (jungle)
            {
                int jd = rng.RandiRange(12, 20);
                for (int i = 0; i < jd; i++) { if (rng.Randf() < 0.28f) Monstera(root, rng, R(rng), R(rng)); else Fern(root, rng, R(rng), R(rng)); }
                if (rng.Randf() < 0.3f) SpawnPepperBush(root, rng, R(rng), R(rng));
                int flies = rng.RandiRange(2, 5);
                for (int i = 0; i < flies; i++) SpawnFirefly(root, rng, R(rng), R(rng));
            }
            else
            {
                int detail = rng.RandiRange(4, 9);
                for (int i = 0; i < detail; i++) GrassTuft(root, rng, R(rng), R(rng));
                if (biome != 3) { int fl = rng.RandiRange(0, 3); for (int i = 0; i < fl; i++) Flowers(root, rng, R(rng), R(rng)); }
                { int nf = rng.RandiRange(0, 2); for (int i = 0; i < nf; i++) GroveFern(root, rng, R(rng), R(rng)); }   // (MESHY) real fern/bush clusters on the Grove floor
                // (NEW autumn detail) warm dry grass tufts + fallen-leaf litter, with the occasional raked pile
                { int fgc = rng.RandiRange(2, 5); for (int i = 0; i < fgc; i++) FallGrassClump(root, rng, R(rng), R(rng)); }
                { int lsc = rng.RandiRange(3, 6); for (int i = 0; i < lsc; i++) LeafScatter(root, rng, R(rng), R(rng), rng.RandiRange(4, 9)); }
                if (rng.Randf() < 0.3f) LeafPile(root, rng, R(rng), R(rng));
                if (biome != 3 && biome != 6 && rng.Randf() < 0.2f) SpawnPumpkin(root, rng, R(rng), R(rng));   // the odd wild pumpkin
                bool wispy = biome == 1 || biome == 2 || biome == 3 || biome == 8 || biome == 10;   // forest/marsh/mushroom/meadow
                int wisps = wispy ? rng.RandiRange(1, 2) : (rng.Randf() < 0.15f ? 1 : 0);
                for (int i = 0; i < wisps; i++) SpawnWisp(root, rng, R(rng), R(rng));
            }
            Shoreline(root, rng, c);   // (NEW) reeds, cattails, lilypads + wet shingle wherever this chunk meets the water table
        }

        _chunks[c] = root;
        if (lite) { _lite.Add(c); }
        else   // full chunk: register collision/decks/ramps/vines (lite chunks are far away and need none of it)
        {
            _chunkBlockers[c] = blockers;
            _chunkVines[c] = vines;
            _chunkDecks[c] = decks;
            _chunkRamps[c] = ramps;
            _blockersDirty = true;
        }
        Dbg.Log($"BuildChunk {c} END");
    }

    // a solid raised stone keep, reached by a stepped ramp
    private void Fort(Node3D root, RandomNumberGenerator rng, List<Blocker> bl, List<Deck> decks, List<Ramp> ramps, Vector2I c)
    {
        float ox = c.X * ChunkSize, oz = c.Y * ChunkSize;
        float lx = rng.RandfRange(-8, 8), lz = rng.RandfRange(-8, 8);
        if (!DryLand(ox + lx, oz + lz, 0.6f)) return;   // (WATER) keeps/forts never in the water
        if (StructureBlocked(root, lx, lz, bl, 8f)) return;   // don't drop a keep/fort on a tree / structure / map feature
        float baseY = Height(ox + lx, oz + lz);
        if (rng.Randf() < 0.5f)
        {
            // (MESHY) decorative ruined tower — a tall landmark, coarse blocker, not climbable
            float fh = rng.RandfRange(11f, 15f);   // (SCALE) bigger — was 6.5-9.5, read too small
            StructureModel(root, "fort", new Vector3(lx, baseY, lz), fh, rng.Randf() * Mathf.Tau, bl, (int)rng.Randi(), decks, flatTopFrac: 0.82f);   // solid + you can stand on the flat battlement top
        }
        else ClimbableKeep(root, rng, bl, decks, ramps, c, lx, lz, rng.RandfRange(16f, 20f));   // (MESHY) the climbable keep variant — walkable roof + stairs (bigger again)
    }

    // (DEV) line up every Meshy structure at a world point with its collision REGISTERED, so a scenario can toggle the collision
    // viz and audit each. Returns each structure's world position (+ .Y=height) for the scenario to frame.
    public Godot.Collections.Array<Vector3> DebugStructureAudit(Vector3 center)
    {
        var outp = new Godot.Collections.Array<Vector3>();
        var c = new Vector2I(Mathf.RoundToInt(center.X / ChunkSize), Mathf.RoundToInt(center.Z / ChunkSize));
        if (!_chunks.TryGetValue(c, out var root)) return outp;
        if (!_chunkDecks.TryGetValue(c, out var decks)) { decks = new List<Deck>(); _chunkDecks[c] = decks; }
        if (!_chunkRamps.TryGetValue(c, out var ramps)) { ramps = new List<Ramp>(); _chunkRamps[c] = ramps; }
        if (!_chunkBlockers.TryGetValue(c, out var bl)) { bl = new List<Blocker>(); _chunkBlockers[c] = bl; }
        _emitDecks = decks; _emitRamps = ramps;   // (AUTHORED) so StructureModel/StairModel use the saved collider templates in the audit too
        var rng = new RandomNumberGenerator { Seed = 42 };
        float bz = center.Z - root.Position.Z;
        var specs = new (string name, float h, bool arch, float flatTop)[]
        {
            ("cottage_a", 14f, false, 0f), ("cottage_b", 14f, false, 0f), ("fort", 13f, false, 0.82f),
            ("ruin", 12f, true, 0f), ("staircase", 8f, false, 0.9f), ("altar", 2.6f, false, 0f),
            ("well", 3f, false, 0f), ("gravestones", 2.2f, false, 0f), ("platform", 2.0f, false, 0.6f),
        };
        float sp = 24f, x0 = center.X - root.Position.X - (specs.Length) * sp * 0.5f;
        for (int i = 0; i < specs.Length; i++)
        {
            float lx = x0 + i * sp, gyy = Height(root.Position.X + lx, root.Position.Z + bz);
            float auditYaw = specs[i].name == "fort" ? 0.7f : 0f;   // (AUTHORED) rotate the templated fort to verify authored colliders track the model's spawn rotation
            if (specs[i].name == "staircase") StairModel(root, "staircase", new Vector3(lx, gyy, bz), specs[i].h, bl, decks, ramps, i * 13 + 3);
            else StructureModel(root, specs[i].name, new Vector3(lx, gyy, bz), specs[i].h, auditYaw, bl, i * 13 + 3, decks, specs[i].flatTop, specs[i].arch);
            outp.Add(new Vector3(root.Position.X + lx, gyy + specs[i].h, root.Position.Z + bz));
        }
        float kx = x0 + specs.Length * sp;   // climbable keep at the end
        ClimbableKeep(root, rng, bl, decks, ramps, c, kx, bz, 16f);
        outp.Add(new Vector3(root.Position.X + kx, Height(root.Position.X + kx, root.Position.Z + bz) + 16f, root.Position.Z + bz));
        MarkBlockersDirty();
        return outp;
    }

    // (DEV) place a climbable keep at a world point with its Deck/Ramp REGISTERED into the chunk collision (so a scenario can
    // actually walk up it). Returns (roofY, stairFarWorldZ) so the caller can position the player at the stair base. Fixed H.
    public Vector3 DebugSpawnClimbableKeep(Vector3 center)
    {
        var c = new Vector2I(Mathf.RoundToInt(center.X / ChunkSize), Mathf.RoundToInt(center.Z / ChunkSize));
        if (!_chunks.TryGetValue(c, out var root)) return new Vector3(center.Y, center.X, center.Z);
        if (!_chunkDecks.TryGetValue(c, out var decks)) { decks = new List<Deck>(); _chunkDecks[c] = decks; }
        if (!_chunkRamps.TryGetValue(c, out var ramps)) { ramps = new List<Ramp>(); _chunkRamps[c] = ramps; }
        if (!_chunkBlockers.TryGetValue(c, out var bl)) { bl = new List<Blocker>(); _chunkBlockers[c] = bl; }
        float lx = center.X - root.Position.X, lz = center.Z - root.Position.Z;
        const float H = 18f;
        ClimbableKeep(root, new RandomNumberGenerator { Seed = 7 }, bl, decks, ramps, c, lx, lz, H);
        MarkBlockersDirty();
        float ground = Height(center.X, center.Z);
        float placeY = ground - Mathf.Max(0.6f, H * 0.04f);
        var ext = PropGlb.NormExtents("keep_climb");
        float halfX = H * ext.X, halfZ = H * ext.Y;
        float roofY = placeY + H * 0.60f;
        float rise = roofY - ground;
        float xStairWorld = center.X - halfX * 0.42f;                    // the +Z-face staircase's X (offset to -X)
        float stairFarZ = center.Z + halfZ * 0.92f + rise * 1.5f;         // far (ground) end of the +Z staircase
        return new Vector3(roofY, xStairWorld, stairFarZ);
    }

    // (MESHY) a CLIMBABLE keep: the keep_climb model (flat battlement roof) as the body, a registered walkable-roof Deck, and a
    // visible stone staircase up the +Z face (Ramp) so it keeps its verticality. Stairs start at the model edge and run OUTWARD,
    // so they read as an external stair and never clip the walls. Deck footprint is kept inside the (wider) model so you never
    // stand on empty air; roof sits just under the battlement crenellations.
    private void ClimbableKeep(Node3D root, RandomNumberGenerator rng, List<Blocker> bl, List<Deck> decks, List<Ramp> ramps, Vector2I c, float lx, float lz, float H)
    {
        float ox = c.X * ChunkSize, oz = c.Y * ChunkSize;
        float ground = Height(ox + lx, oz + lz);
        float placeY = ground - Mathf.Max(0.6f, H * 0.04f);   // (FIX) SINK the model base into the terrain so it's embedded, not floating
        float roofY = placeY + H * 0.60f;     // the model's actual roof FLOOR (~0.6H above the base)
        var ext = PropGlb.NormExtents("keep_climb");
        float halfX = H * ext.X, halfZ = H * ext.Y;   // the model's REAL footprint (so the stair starts at the true wall, not inside it)
        var keep = PropGlb.Instance("keep_climb", H, (int)rng.Randi());
        keep.Position = new Vector3(lx, placeY, lz);
        root.AddChild(keep);
        if (ColliderTemplates.Emit("keep_climb", new Vector3(ox + lx, 0, oz + lz), placeY, H, 0f, bl, decks, ramps)) return;   // (AUTHORED) hand-placed template wins
        // walkable roof = the model's full top footprint (rail below stops you at the parapet).
        float deckHX = halfX * 0.92f, deckHZ = halfZ * 0.92f;
        decks.Add(new Deck { Center = new Vector3(ox + lx, 0, oz + lz), Half = new Vector2(deckHX, deckHZ), TopY = roofY });
        // Use the model's OWN built-in staircase: it's on the +Z face, offset to the -X side. We place ONLY a matching invisible
        // Ramp over it (no primitive steps of our own → no double staircase). Ramp runs from the TERRAIN (ground) up to the roof
        // so you step onto it seamlessly from the ground and aren't floating.
        float rise = roofY - ground, runLen = rise * 1.5f;
        float xStair = lx - halfX * 0.42f;      // stair sits on the -X portion of the +Z face
        float z0 = lz + deckHZ;                 // +Z roof edge, where the stairs meet the roof
        float rw = halfX * 0.58f;               // stair width (along X)
        ramps.Add(new Ramp { Center = new Vector3(ox + xStair, 0, oz + (z0 + runLen / 2f)), Half = new Vector2(rw / 2f, runLen / 2f), YLow = roofY, YHigh = ground, AlongX = false });
        // (RAIL) RECTANGULAR parapet — collidable to just above the roof (no walking off) but jump-overable. Along the 4 real
        // edges; the +Z-edge blockers over the staircase are skipped so the stairs stay open.
        float railTop = roofY + 1.15f, rbr = Mathf.Min(halfX, halfZ) * 0.5f;
        for (int e = 0; e < 4; e++)
            for (int s = -1; s <= 1; s++)
            {
                float ex = e == 1 ? lx - halfX : e == 2 ? lx + halfX : lx + s * halfX;
                float ez = e == 0 ? lz - halfZ : e == 3 ? lz + halfZ : lz + s * halfZ;
                if (e == 3 && Mathf.Abs(ex - xStair) < halfX * 0.62f) continue;   // +Z edge over the stair entry → leave open
                bl.Add(new Blocker { Pos = new Vector3(ox + ex, 0, oz + ez), Radius = rbr, Top = railTop });
            }
    }

    // scattered broken walls plus a solid jump-height platform
    private void Ruins(Node3D root, RandomNumberGenerator rng, List<Blocker> bl, List<Deck> decks, List<Ramp> ramps, Vector2I c)
    {
        float ox = c.X * ChunkSize, oz = c.Y * ChunkSize;
        var stone = Matte(new Color(0.17f, 0.16f, 0.19f), 0.9f, false);

        // (MESHY) the hero ruin — a real authored broken-temple structure as the centrepiece landmark, replacing the old
        // scatter of box walls. A coarse central blocker keeps you from walking through its solid mass.
        float rlx = rng.RandfRange(-14, 14), rlz = rng.RandfRange(-14, 14);
        if (StructureBlocked(root, rlx, rlz, bl, 8f)) return;   // don't drop the ruin on a tree / structure / map feature
        float rh = rng.RandfRange(9f, 14f);   // (SCALE) bigger arch
        StructureModel(root, "ruin", new Vector3(rlx, Height(ox + rlx, oz + rlz), rlz), rh, rng.Randf() * Mathf.Tau, bl, (int)rng.Randi(), arch: true);   // solid pillars + walk UNDER the arch

        // an occasional weathered staircase nearby — another authored landmark (coarse blocker; not yet a walkable ramp)
        if (rng.Randf() < 0.5f)
        {
            float slx = rng.RandfRange(-16, 16), slz = rng.RandfRange(-16, 16);
            StairModel(root, "staircase", new Vector3(slx, Height(ox + slx, oz + slz), slz), rng.RandfRange(6f, 9f), bl, decks, ramps, (int)rng.Randi());   // WALKABLE up (yaw 0)
        }

        // a raised stone slab you can actually climb onto — verticality/perch (kept from the old ruins)
        float plx = rng.RandfRange(-10, 10), plz = rng.RandfRange(-10, 10), pY = rng.RandfRange(1.6f, 2.6f);
        float pBaseY = Height(ox + plx, oz + plz);
        var slab = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(7, pY, 7) } };
        slab.MaterialOverride = stone; slab.Position = new Vector3(plx, pY / 2f + pBaseY, plz); root.AddChild(slab);
        var cap = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(7.3f, 0.4f, 7.3f) } };
        cap.MaterialOverride = Matte(new Color(0.22f, 0.21f, 0.24f), 0.9f, false); cap.Position = new Vector3(plx, pY + pBaseY, plz); root.AddChild(cap);
        decks.Add(new Deck { Center = new Vector3(ox + plx, 0, oz + plz), Half = new Vector2(3.5f, 3.5f), TopY = pY + pBaseY });
    }

    // (MESHY) place an authored structure GLB (ruin/staircase) as a landmark: real 3D model at (localPos, yaw) scaled to
    // `height`, plus a coarse central collision blocker so its solid core is impassable. Skipped in the boundary-cliff band.
    // Place an authored structure GLB embedded in the terrain, and give it COLLISION derived from the model so what looks solid
    // IS solid and what looks flat can be stood on:
    //   • solid=true (default) → a footprint Blocker sized from the model's real extents (can't walk through the walls)
    //   • arch=true            → two solid PILLARS at the arch legs, leaving the opening walkable-UNDER
    //   • flatTopFrac>0 + decks → a walkable Deck at that fraction of the height (jump up → stand on the flat top)
    // (AUTHORED) the current chunk's deck/ramp lists, so StructureModel can emit template decks/ramps even for callers that only pass blockers
    private List<Deck> _emitDecks; private List<Ramp> _emitRamps;

    private void StructureModel(Node3D root, string name, Vector3 localPos, float height, float yaw, List<Blocker> bl, int seed,
                                List<Deck> decks = null, float flatTopFrac = 0f, bool arch = false, bool solid = true)
    {
        if (InCliffBand(root.Position.X + localPos.X, root.Position.Z + localPos.Z, 14f)) return;
        var node = PropGlb.Instance(name, height, seed);
        node.Position = new Vector3(localPos.X, localPos.Y - Mathf.Max(0.4f, height * 0.06f), localPos.Z);   // SINK into the terrain so it's embedded, not floating
        node.RotationDegrees = new Vector3(0, Mathf.RadToDeg(yaw), 0);
        root.AddChild(node);

        var ext = PropGlb.NormExtents(name);
        float hx = height * ext.X, hz = height * ext.Y;   // real footprint half-extents
        float wx = root.Position.X + localPos.X, wz = root.Position.Z + localPos.Z, gy = localPos.Y;
        // (AUTHORED) if this model has a hand-placed collider template, use it instead of the heuristic colliders below
        var eDecks = decks ?? _emitDecks; var eRamps = _emitRamps;
        if (eDecks != null && eRamps != null && ColliderTemplates.Emit(name, new Vector3(wx, 0, wz), node.Position.Y, height, yaw, bl, eDecks, eRamps)) return;
        if (arch)
        {
            // solid pillars at the two arch legs. AUDIT (colliders viz) showed this model's legs run along its NARROWER footprint
            // axis (opening is along the wider one), so the pillars go on the narrow axis; the wide axis stays walkable-under.
            bool spanX = hx >= hz; float span = spanX ? hx : hz;
            var dir = spanX ? new Vector2(-Mathf.Sin(yaw), Mathf.Cos(yaw)) : new Vector2(Mathf.Cos(yaw), Mathf.Sin(yaw));
            for (int s = -1; s <= 1; s += 2)
                bl.Add(new Blocker { Pos = new Vector3(wx + dir.X * span * 0.82f * s, 0, wz + dir.Y * span * 0.82f * s), Radius = span * 0.32f, Top = gy + height });
        }
        else if (solid)
            bl.Add(new Blocker { Pos = new Vector3(wx, 0, wz), Radius = Mathf.Max(hx, hz) * 1.05f, Top = gy + height * 0.9f });
        if (flatTopFrac > 0f && decks != null)
            decks.Add(new Deck { Center = new Vector3(wx, 0, wz), Half = new Vector2(hx * 0.78f, hz * 0.78f), TopY = gy + height * flatTopFrac });
    }

    // (MESHY) a WALKABLE staircase: the model + a Ramp matching its steps + a small top-landing Deck + side blockers for the
    // flanking columns. Placed axis-aligned (yaw 0 — steps ascend toward -Z) so the Ramp lines up with the visual steps. Sunk
    // into the terrain like other structures. Top height / width tuned against the `colliders` audit.
    private void StairModel(Node3D root, string name, Vector3 localPos, float height, List<Blocker> bl, List<Deck> decks, List<Ramp> ramps, int seed)
    {
        if (InCliffBand(root.Position.X + localPos.X, root.Position.Z + localPos.Z, 14f)) return;
        var node = PropGlb.Instance(name, height, seed);
        node.Position = new Vector3(localPos.X, localPos.Y - Mathf.Max(0.4f, height * 0.06f), localPos.Z);
        root.AddChild(node);   // yaw 0

        var ext = PropGlb.NormExtents(name);
        float hx = height * ext.X, hz = height * ext.Y;
        float wx = root.Position.X + localPos.X, wz = root.Position.Z + localPos.Z, gy = localPos.Y;
        if (ColliderTemplates.Emit(name, new Vector3(wx, 0, wz), node.Position.Y, height, 0f, bl, decks, ramps)) return;   // (AUTHORED) hand-placed template wins
        float topY = gy + height * 0.62f;   // top-landing height (TUNE via audit)
        float rw = hx * 1.1f;
        // Ramp spans the run (Z). AlongX=false: t=0 at low-Z (= back, top), t=1 at high-Z (= front, ground).
        ramps.Add(new Ramp { Center = new Vector3(wx, 0, wz), Half = new Vector2(rw * 0.5f, hz * 0.95f), YLow = topY, YHigh = gy, AlongX = false });
        // top landing Deck — placed BEHIND the ramp's high end (so its side wall never blocks the climb), at topY
        decks.Add(new Deck { Center = new Vector3(wx, 0, wz - hz * 1.15f), Half = new Vector2(rw * 0.5f, hz * 0.25f), TopY = topY });
        for (int s = -1; s <= 1; s += 2)   // the two flanking columns are solid
            bl.Add(new Blocker { Pos = new Vector3(wx + s * hx * 0.85f, 0, wz), Radius = hx * 0.28f, Top = gy + height });
    }

    // (WATER) true when this world XZ is dry LAND, comfortably above the water table. Only reeds, lilypads and the (already
    // ruined) ruin/staircase are allowed in/at the water; every other prop & structure gates on this so nothing spawns
    // silly-looking standing in a pond.
    public bool DryLand(float wx, float wz, float margin = 0.35f) => Height(wx, wz) > WaterLevel + margin;

    // (NEW) the current chunk's structure blockers, set before the ground-detail scatter so props don't land INSIDE big
    // structures (houses/forts/wells/altars). Only BIG blockers count — small ones (graves, trees) are fine to scatter around.
    private List<Blocker> _scatterBlockers;
    private bool ClearOfStructures(float wx, float wz)
    {
        if (_scatterBlockers == null) return true;
        foreach (var b in _scatterBlockers)
        {
            if (b.Radius < 2f) continue;                       // ignore small footprints (gravestones, trees) — props may sit near them
            float r = b.Radius + 1.2f, dx = wx - b.Pos.X, dz = wz - b.Pos.Z;
            if (dx * dx + dz * dz < r * r) return false;
        }
        return true;
    }
    // scatter gate: on dry land AND not inside a big structure's footprint
    private bool Scatterable(float wx, float wz, float margin = 0.35f) => DryLand(wx, wz, margin) && ClearOfStructures(wx, wz);

    // true if placing something of radius `rad` at (root+lx,lz) would overlap an existing BIG blocker (a tree or an
    // already-placed structure in this chunk) OR a map-wide feature → used to stop structures/trees stacking on each other.
    private bool StructureBlocked(Node3D root, float lx, float lz, List<Blocker> bl, float rad)
    {
        float wx = root.Position.X + lx, wz = root.Position.Z + lz;
        foreach (var b in bl)
        {
            if (b.Radius < 1.5f) continue;
            float d = rad + b.Radius, dx = wx - b.Pos.X, dz = wz - b.Pos.Z;
            if (dx * dx + dz * dz < d * d) return true;
        }
        return Game.I != null && Game.I.NearMapFeature(new Vector3(wx, 0, wz), rad + 8f);
    }

    // (FIX) true when a world XZ sits in the cliff-wall band (or beyond) — used to keep trees/structures/scenery from
    // clipping into the boundary mountains. Only applies in the bounded overworld.
    private bool InCliffBand(float wx, float wz, float margin)
    {
        if (Game.I == null || !Game.I.InOverworld) return false;
        float rr = WorldRadius - margin;
        return wx * wx + wz * wz > rr * rr;
    }

    // local (chunk-space) → world position for a blocker
    private Vector3 World3(Vector2I c, float lx, float lz) => new Vector3(c.X * ChunkSize + lx, 0, c.Y * ChunkSize + lz);
    private static Vector2I ChunkOf(Node3D root) => new Vector2I(Mathf.RoundToInt(root.Position.X / ChunkSize), Mathf.RoundToInt(root.Position.Z / ChunkSize));   // (NEW) chunk key for PropField instance tracking (root.Position == chunk origin)
    private float R(RandomNumberGenerator rng) => rng.RandfRange(-ChunkSize * 0.45f, ChunkSize * 0.45f);

    // ---- props ------------------------------------------------------------
    private void KnottedTree(Node3D root, RandomNumberGenerator rng, List<Blocker> bl, Vector2I c, float lx, float lz, bool dead)
    {
        if (InCliffBand(root.Position.X + lx, root.Position.Z + lz, 10f)) return;   // (FIX) don't plant trees in the boundary cliffs
        float gy = GY(root, lx, lz);
        var sp = dead ? ProcTree.Species.DeadOak : ProcTree.Species.GroveOak;
        int variant = ProcTree.PickVariant(sp, rng, out float br, out float th, out _);
        if (StructureBlocked(root, lx, lz, bl, br)) return;   // (FIX) no trees stacked on other trees / structures / map features
        var xform = new Transform3D(new Basis(Vector3.Up, rng.Randf() * Mathf.Tau), new Vector3(root.Position.X + lx, gy, root.Position.Z + lz));   // random yaw so the fleet isn't clones
        _treeField.Add(sp, variant, xform, c);   // GPU-instanced — one draw call for all trees of this variant
        bl.Add(new Blocker { Pos = World3(c, lx, lz), Radius = br, Top = gy + th });   // finite height → you fly over it once clear of the canopy
    }

    private void Rock(Node3D root, RandomNumberGenerator rng, float lx, float lz)
    {
        float s = rng.RandfRange(0.6f, 1.6f);
        var pos = new Vector3(root.Position.X + lx, s * 0.3f + GY(root, lx, lz), root.Position.Z + lz);
        var basis = new Basis(Vector3.Up, rng.Randf() * 6f).Scaled(new Vector3(s, s * 0.7f, s * rng.RandfRange(0.8f, 1.3f)));
        _propField.Add(PropField.Kind.Rock, new Transform3D(basis, pos), ChunkOf(root));   // (PERF) GPU-instanced
    }

    private void Mushroom(Node3D root, RandomNumberGenerator rng, float lx, float lz)
    {
        if (!Scatterable(root.Position.X + lx, root.Position.Z + lz)) return;   // (WATER) mushrooms only on dry land
        // (MESHY) real 3D mushroom model, GPU-instanced. Normalised to unit height by PropGlb, so we just scale by a target
        // height + random yaw + a little squash variety; base sits on the terrain. Per-instance colour jitter is automatic.
        float h = rng.RandfRange(0.5f, 1.4f);
        float gy = GY(root, lx, lz);
        var c = ChunkOf(root);
        float ox = root.Position.X + lx, oz = root.Position.Z + lz;
        float squash = rng.RandfRange(0.85f, 1.12f);   // some caps broader/shorter than others
        var basis = new Basis(Vector3.Up, rng.Randf() * Mathf.Tau).Scaled(new Vector3(h * squash, h, h * squash));
        _propField.Add(PropField.Kind.GlbMushroom, new Transform3D(basis, new Vector3(ox, gy, oz)), c);
    }

    // (MESHY) a stand of real reed CLUMPS — the GLB reed is already a multi-stalk clump with cattail heads, so a couple per
    // spot reads as a full reed bed (was: a handful of primitive box stalks). GPU-instanced; base sits on the terrain.
    private void Reed(Node3D root, RandomNumberGenerator rng, float lx, float lz)
    {
        var c = ChunkOf(root);
        int n = rng.RandiRange(1, 2);
        for (int i = 0; i < n; i++)
        {
            float jx = rng.RandfRange(-0.6f, 0.6f), jz = rng.RandfRange(-0.6f, 0.6f);
            float h = rng.RandfRange(1.9f, 2.9f);
            float gy = GY(root, lx + jx, lz + jz);
            var pos = new Vector3(root.Position.X + lx + jx, gy, root.Position.Z + lz + jz);
            var basis = new Basis(Vector3.Up, rng.Randf() * Mathf.Tau).Scaled(new Vector3(h, h, h));
            _propField.Add(PropField.Kind.GlbReeds, new Transform3D(basis, pos), c);
        }
    }

    // (MESHY) a small fern/bush cluster for the Grove floor — a few real fern models, GPU-instanced.
    private void GroveFern(Node3D root, RandomNumberGenerator rng, float lx, float lz)
    {
        if (!Scatterable(root.Position.X + lx, root.Position.Z + lz)) return;   // (WATER) ferns/bushes only on dry land
        var c = ChunkOf(root);
        int n = rng.RandiRange(1, 3);
        for (int i = 0; i < n; i++)
        {
            float jx = rng.RandfRange(-0.9f, 0.9f), jz = rng.RandfRange(-0.9f, 0.9f);
            float h = rng.RandfRange(0.7f, 1.3f);
            float gy = GY(root, lx + jx, lz + jz);
            var pos = new Vector3(root.Position.X + lx + jx, gy, root.Position.Z + lz + jz);
            var basis = new Basis(Vector3.Up, rng.Randf() * Mathf.Tau).Scaled(new Vector3(h, h, h));
            _propField.Add(PropField.Kind.GlbFern, new Transform3D(basis, pos), c);
        }
    }

    // (NEW autumn detail) a tuft of warm DRY grass that sways — a clump is either mostly-tall or mostly-short. GPU-instanced.
    private void FallGrassClump(Node3D root, RandomNumberGenerator rng, float lx, float lz)
    {
        if (!Scatterable(root.Position.X + lx, root.Position.Z + lz)) return;   // (WATER) dry grass only on dry land
        var c = ChunkOf(root);
        float gy = GY(root, lx, lz);
        int blades = rng.RandiRange(4, 8);
        bool tall = rng.Randf() < 0.4f;
        for (int i = 0; i < blades; i++)
        {
            float h = tall ? rng.RandfRange(1.2f, 2.2f) : rng.RandfRange(0.4f, 0.9f);
            float px = root.Position.X + lx + rng.RandfRange(-0.4f, 0.4f), pz = root.Position.Z + lz + rng.RandfRange(-0.4f, 0.4f);
            var basis = Basis.FromEuler(new Vector3(rng.RandfRange(-0.22f, 0.22f), rng.Randf() * 6f, rng.RandfRange(-0.22f, 0.22f))).Scaled(new Vector3(0.055f, h, 0.055f));
            _propField.Add(PropField.Kind.FallGrass, new Transform3D(basis, new Vector3(px, h / 2f + gy, pz)), c);
        }
    }

    // (MESHY) real single fallen-leaf models (3 kinds) scattered over the ground around (lx,lz), each with a little wind flutter.
    private void LeafScatter(Node3D root, RandomNumberGenerator rng, float lx, float lz, int n)
    {
        var c = ChunkOf(root);
        for (int i = 0; i < n; i++)
        {
            float jx = rng.RandfRange(-2.4f, 2.4f), jz = rng.RandfRange(-2.4f, 2.4f);
            if (!Scatterable(root.Position.X + lx + jx, root.Position.Z + lz + jz)) continue;
            float r = rng.Randf();
            var kind = r < 0.34f ? PropField.Kind.GlbLeafA : r < 0.67f ? PropField.Kind.GlbLeafB : PropField.Kind.GlbLeafC;
            float s = rng.RandfRange(0.3f, 0.55f);
            float gx = root.Position.X + lx + jx, gz = root.Position.Z + lz + jz;
            var basis = new Basis(Vector3.Up, rng.Randf() * Mathf.Tau).Scaled(new Vector3(s, s, s));
            _propField.Add(kind, new Transform3D(basis, new Vector3(gx, GY(root, lx + jx, lz + jz) + 0.02f, gz)), c);
        }
    }

    // (MESHY) a real raked leaf-PILE model (2 kinds) at a spot.
    private void LeafPile(Node3D root, RandomNumberGenerator rng, float lx, float lz)
    {
        if (!Scatterable(root.Position.X + lx, root.Position.Z + lz)) return;
        var c = ChunkOf(root);
        var kind = rng.Randf() < 0.5f ? PropField.Kind.GlbLeafPileA : PropField.Kind.GlbLeafPileB;
        float s = rng.RandfRange(0.9f, 1.5f);
        var basis = new Basis(Vector3.Up, rng.Randf() * Mathf.Tau).Scaled(new Vector3(s, s * rng.RandfRange(0.7f, 1f), s));
        _propField.Add(kind, new Transform3D(basis, new Vector3(root.Position.X + lx, GY(root, lx, lz) + 0.02f, root.Position.Z + lz)), c);
    }

    // (DEV) scatter a representative Grove patch around a world point — exercises the REAL placement helpers (instanced
    // mushroom/fern/reeds + flower/pumpkin nodes + ruin & staircase structures) so a scenario can validate the shipping path,
    // not a hand-placed lineup. No-op if the target chunk isn't streamed in yet.
    public void DebugGrovePatch(Vector3 center)
    {
        var c = new Vector2I(Mathf.RoundToInt(center.X / ChunkSize), Mathf.RoundToInt(center.Z / ChunkSize));
        if (!_chunks.TryGetValue(c, out var root)) return;
        var rng = new RandomNumberGenerator { Seed = 0xB00B5 };
        float bx = center.X - root.Position.X, bz = center.Z - root.Position.Z;
        for (int i = 0; i < 10; i++) Mushroom(root, rng, bx + rng.RandfRange(-6f, 6f), bz + rng.RandfRange(-6f, 6f));
        for (int i = 0; i < 4; i++)  Flowers(root, rng, bx + rng.RandfRange(-7f, 7f), bz + rng.RandfRange(-7f, 7f));
        for (int i = 0; i < 5; i++)  GroveFern(root, rng, bx + rng.RandfRange(-7f, 7f), bz + rng.RandfRange(-7f, 7f));
        for (int i = 0; i < 4; i++)  Reed(root, rng, bx + rng.RandfRange(-8f, 8f), bz + rng.RandfRange(-8f, 8f));
        for (int i = 0; i < 3; i++)  SpawnPumpkin(root, rng, bx + rng.RandfRange(-6f, 6f), bz + rng.RandfRange(-6f, 6f));
        for (int i = 0; i < 8; i++)  FallGrassClump(root, rng, bx + rng.RandfRange(-7f, 7f), bz + rng.RandfRange(-7f, 7f));
        for (int i = 0; i < 6; i++)  LeafScatter(root, rng, bx + rng.RandfRange(-7f, 7f), bz + rng.RandfRange(-7f, 7f), 8);
        LeafPile(root, rng, bx + 3f, bz - 2f);
        var bl = new List<Blocker>();
        StructureModel(root, "ruin", new Vector3(bx - 10f, GY(root, bx - 10f, bz + 3f), bz + 3f), 6.5f, 0.6f, bl, 11, arch: true);
        StructureModel(root, "staircase", new Vector3(bx + 10f, GY(root, bx + 10f, bz + 3f), bz + 3f), 4.5f, -0.6f, bl, 22);
        _propField.Flush();
    }

    // (NEW) SHORELINE DRESSING — the water table used to just stop against bare ground. Now every chunk that touches it gets
    // planted: reeds and cattails standing in the shallows, lilypads floating out on the surface, and a scatter of wet shingle
    // on the sand the shader paints there. Rejection-sampled against the terrain height, so it hugs the real waterline —
    // dense right at the edge, thinning out as the bank rises.
    private void Shoreline(Node3D root, RandomNumberGenerator rng, Vector2I c)
    {
        float ox = root.Position.X, oz = root.Position.Z;
        int tries = 34;
        for (int i = 0; i < tries; i++)
        {
            float lx = R(rng), lz = R(rng);
            float h = Height(ox + lx, oz + lz);
            float above = h - WaterLevel;
            if (above > 1.5f || above < -2.6f) continue;   // only the band where land meets water

            if (above < -0.35f)
            {
                // out on the open water: a raft of lilypads
                int pads = rng.RandiRange(2, 5);
                for (int k = 0; k < pads; k++)
                {
                    float r = rng.RandfRange(0.55f, 1.15f);
                    var pos = new Vector3(ox + lx + rng.RandfRange(-2.2f, 2.2f), WaterLevel + 0.05f, oz + lz + rng.RandfRange(-2.2f, 2.2f));
                    if (Height(pos.X, pos.Z) > WaterLevel - 0.25f) continue;   // don't beach a pad on a shoal
                    var basis = Basis.FromEuler(new Vector3(0f, rng.Randf() * Mathf.Tau, 0f)).Scaled(new Vector3(r, 1f, r));
                    _propField.Add(PropField.Kind.Lily, new Transform3D(basis, pos), c);
                }
            }
            else
            {
                // the shallows and the bank: a few real reed CLUMPS (GLB already has cattail heads → no separate head prop)
                int n = rng.RandiRange(1, 3);
                for (int k = 0; k < n; k++)
                {
                    float sh = rng.RandfRange(1.9f, 3.0f);
                    float jx = rng.RandfRange(-1.4f, 1.4f), jz = rng.RandfRange(-1.4f, 1.4f);
                    float gy = Height(ox + lx + jx, oz + lz + jz);
                    if (gy - WaterLevel > 1.7f) continue;
                    float footY = Mathf.Min(gy, WaterLevel);   // reeds rooted underwater still break the surface
                    var pos = new Vector3(ox + lx + jx, footY, oz + lz + jz);   // GLB base sits at foot
                    var basis = new Basis(Vector3.Up, rng.Randf() * Mathf.Tau).Scaled(new Vector3(sh, sh, sh));
                    _propField.Add(PropField.Kind.GlbReeds, new Transform3D(basis, pos), c);
                }
                // wet shingle on the sand
                if (above > 0.1f)
                {
                    int peb = rng.RandiRange(2, 5);
                    for (int k = 0; k < peb; k++)
                    {
                        float pr = rng.RandfRange(0.09f, 0.24f);
                        float jx = rng.RandfRange(-2.6f, 2.6f), jz = rng.RandfRange(-2.6f, 2.6f);
                        var pos = new Vector3(ox + lx + jx, Height(ox + lx + jx, oz + lz + jz) + pr * 0.35f, oz + lz + jz);
                        var basis = Basis.FromEuler(new Vector3(rng.Randf() * 3f, rng.Randf() * 6f, rng.Randf() * 3f))
                                         .Scaled(new Vector3(pr * 1.6f, pr * 0.7f, pr * 1.25f));
                        _propField.Add(PropField.Kind.Pebble, new Transform3D(basis, pos), c);
                    }
                }
            }
        }
    }

    // ---- jungle props (Rainforest biome, NEW) -----------------------------
    private void JungleTree(Node3D root, RandomNumberGenerator rng, List<Blocker> bl, Vector2I c, float lx, float lz)
    {
        if (InCliffBand(root.Position.X + lx, root.Position.Z + lz, 10f)) return;   // (FIX) don't plant trees in the boundary cliffs
        int variant = rng.RandiRange(0, 3);
        float gy = GY(root, lx, lz);
        var sp = variant == 1 ? ProcTree.Species.Palm
               : variant == 2 ? ProcTree.Species.Understory
               : variant == 3 ? ProcTree.Species.JungleGnarled
               : ProcTree.Species.JungleGiant;
        int tv = ProcTree.PickVariant(sp, rng, out float br, out float th, out _);
        var xform = new Transform3D(new Basis(Vector3.Up, rng.Randf() * Mathf.Tau), new Vector3(root.Position.X + lx, gy, root.Position.Z + lz));
        _treeField.Add(sp, tv, xform, c);   // GPU-instanced
        bl.Add(new Blocker { Pos = World3(c, lx, lz), Radius = br, Top = gy + th });
    }

    // a monstera plant: several stalks fanning from a base, each carrying a big flat tropical BLADE with the classic split notches
    private void Monstera(Node3D root, RandomNumberGenerator rng, float lx, float lz)
    {
        float gy = GY(root, lx, lz);
        var c = ChunkOf(root);
        float ox0 = root.Position.X + lx, oz0 = root.Position.Z + lz;
        int n = rng.RandiRange(4, 7);
        for (int i = 0; i < n; i++)
        {
            float a = i / (float)n * Mathf.Tau + rng.Randf() * 0.5f;
            float stemLen = 0.8f + rng.Randf() * 1.2f;
            float outr = 0.4f + rng.Randf() * 0.5f;
            var stemPos = new Vector3(ox0 + Mathf.Cos(a) * outr * 0.5f, gy + stemLen * 0.5f, oz0 + Mathf.Sin(a) * outr * 0.5f);
            var stemBasis = Basis.FromEuler(new Vector3(Mathf.Sin(a) * 0.4f, 0, -Mathf.Cos(a) * 0.4f)).Scaled(new Vector3(1f, stemLen, 1f));
            _propField.Add(PropField.Kind.MonsteraStem, new Transform3D(stemBasis, stemPos), c);
            float ls = 0.8f + rng.Randf() * 0.7f;
            var tip = new Vector3(ox0 + Mathf.Cos(a) * outr, gy + stemLen + 0.1f, oz0 + Mathf.Sin(a) * outr);
            var leafBasis = Basis.FromEuler(new Vector3(-0.6f, a, 0)).Scaled(new Vector3(ls, ls * 0.1f, ls * 1.5f));   // flat wide blade
            _propField.Add(PropField.Kind.MonsteraLeaf, new Transform3D(leafBasis, tip), c);
            for (int k = -1; k <= 1; k += 2)   // the classic monstera split notches
            {
                var notchPos = tip + new Vector3(k * ls * 0.28f * Mathf.Sin(a), 0.03f, -k * ls * 0.28f * Mathf.Cos(a));
                var notchBasis = Basis.FromEuler(new Vector3(-0.6f, a, 0)).Scaled(new Vector3(0.07f, 0.16f, ls * 0.7f));
                _propField.Add(PropField.Kind.MonsteraNotch, new Transform3D(notchBasis, notchPos), c);
            }
        }
    }

    private void Fern(Node3D root, RandomNumberGenerator rng, float lx, float lz)
    {
        float gy = GY(root, lx, lz);
        var c = ChunkOf(root);
        int n = rng.RandiRange(4, 8);
        for (int i = 0; i < n; i++)
        {
            float a = rng.Randf() * Mathf.Tau; float len = rng.RandfRange(0.6f, 1.4f);
            var pos = new Vector3(root.Position.X + lx + Mathf.Cos(a) * len * 0.3f, rng.RandfRange(0.15f, 0.5f) + gy, root.Position.Z + lz + Mathf.Sin(a) * len * 0.3f);
            var basis = Basis.FromEuler(new Vector3(rng.RandfRange(-0.7f, -0.3f), a, 0)).Scaled(new Vector3(0.12f, 0.04f, len));
            _propField.Add(PropField.Kind.Fern, new Transform3D(basis, pos), c);
        }
    }

    private void VineTree(Node3D root, RandomNumberGenerator rng, List<Blocker> bl, List<VineGrab> vines, Vector2I c, float lx, float lz)
    {
        float gy = GY(root, lx, lz);
        float yaw = rng.Randf() * Mathf.Tau;
        var tree = ProcTree.Build(ProcTree.Species.CanopyGiant, rng, out _, out float h, out Vector3 anchor);   // super tall canopy giant; anchor = its highest bark tip
        tree.Position = new Vector3(lx, gy, lz);
        tree.Rotation = new Vector3(0, yaw, 0);
        root.AddChild(tree);
        // THE grapple vine — grips the tree's HIGHEST bark point and hangs straight down to a low handhold, swaying in
        // lockstep with the tree (it's a child of the tree using the same wind shader), so it never floats in mid-air.
        float vineBottom = 1.4f;
        Vector3 vineAnchor = new Vector3(anchor.X + 1.5f, anchor.Y, anchor.Z);   // just OUTSIDE the trunk surface so the vine is visible hanging alongside it, not buried inside
        Vector3 localBottom = ProcTree.AddVine(tree, vineAnchor, vineBottom, true, rng);
        int nv = rng.RandiRange(2, 4);   // a few decorative shorter vines dangling off the trunk at various heights
        for (int i = 0; i < nv; i++)
        {
            float a = rng.Randf() * Mathf.Tau, r = 1.4f + rng.Randf() * 0.5f, ty = h * rng.RandfRange(0.45f, 0.8f);   // r>trunk radius so they hang OUTSIDE the trunk, not inside it
            ProcTree.AddVine(tree, new Vector3(Mathf.Cos(a) * r, ty, Mathf.Sin(a) * r), h * rng.RandfRange(0.15f, 0.35f), false, rng);
        }
        // world grab point = the handhold's local offset rotated by the tree's yaw, placed at the tree's world position
        Vector3 rb = new Basis(Vector3.Up, yaw) * localBottom;
        vines.Add(new VineGrab { Pos = World3(c, lx + rb.X, lz + rb.Z) + new Vector3(0, gy + vineBottom, 0), TopY = gy + anchor.Y });   // grab low, get carried up to the canopy
        bl.Add(new Blocker { Pos = World3(c, lx, lz), Radius = 1.6f, Top = gy + h });
    }

    private void SpawnPepperBush(Node3D root, RandomNumberGenerator rng, float lx, float lz)
    {
        var pb = new PepperBush();
        root.AddChild(pb);
        pb.Position = new Vector3(lx, GY(root, lx, lz), lz);
        pb.Init(rng.RandfRange(0.6f, 1.0f), false, (ulong)rng.Randi() ^ 0x9E37F0A1u);
        if (Game.I != null) Game.I.Smashables.Add(pb);
    }

    private void SpawnFirefly(Node3D root, RandomNumberGenerator rng, float lx, float lz)
    {
        var f = new Firefly();
        root.AddChild(f);
        f.Position = new Vector3(lx, GY(root, lx, lz), lz);
        f.Init(new Color(0.82f, 0.95f, 0.35f), rng.RandfRange(0.5f, 0.9f), rng.RandfRange(3.5f, 5.5f), rng.Randf() * 6.2831853f);
    }

    // ---- jungle scenery scatters ----
    private void JungleClearing(Node3D root, RandomNumberGenerator rng, List<Blocker> bl, Vector2I c)
    {
        int t = rng.RandiRange(2, 4); for (int i = 0; i < t; i++) JungleTree(root, rng, bl, c, R(rng), R(rng));
        int rk = rng.RandiRange(1, 3); for (int i = 0; i < rk; i++) Rock(root, rng, R(rng), R(rng));
    }
    private void JungleGrove(Node3D root, RandomNumberGenerator rng, List<Blocker> bl, Vector2I c, int count)
    {
        for (int i = 0; i < count; i++) JungleTree(root, rng, bl, c, R(rng), R(rng));
        int rk = rng.RandiRange(1, 3); for (int i = 0; i < rk; i++) Rock(root, rng, R(rng), R(rng));
    }
    private void RiverBank(Node3D root, RandomNumberGenerator rng, List<Blocker> bl, Vector2I c)
    {
        int t = rng.RandiRange(3, 6); for (int i = 0; i < t; i++) JungleTree(root, rng, bl, c, R(rng), R(rng));
        int mo = rng.RandiRange(4, 8); for (int i = 0; i < mo; i++) Monstera(root, rng, R(rng), R(rng));
        int rd = rng.RandiRange(3, 6); for (int i = 0; i < rd; i++) Reed(root, rng, R(rng), R(rng));
    }
    private void PepperPatch(Node3D root, RandomNumberGenerator rng, List<Blocker> bl, Vector2I c)
    {
        int n = rng.RandiRange(6, 12); for (int i = 0; i < n; i++) SpawnPepperBush(root, rng, R(rng), R(rng));
        int t = rng.RandiRange(1, 3); for (int i = 0; i < t; i++) JungleTree(root, rng, bl, c, R(rng), R(rng));
    }
    private void VineGrove(Node3D root, RandomNumberGenerator rng, List<Blocker> bl, List<VineGrab> vines, Vector2I c)
    {
        int n = rng.RandiRange(2, 4); for (int i = 0; i < n; i++) VineTree(root, rng, bl, vines, c, R(rng), R(rng));
        int jt = rng.RandiRange(2, 5); for (int i = 0; i < jt; i++) JungleTree(root, rng, bl, c, R(rng), R(rng));
    }

    // a stepped stone temple (Aztec/Mayan-style pyramid), stone + moss tiers, a shrine on top, idol pillars around it
    private void JungleTemple(Node3D root, RandomNumberGenerator rng, List<Blocker> bl, Vector2I c)
    {
        float cx = R(rng) * 0.3f, cz = R(rng) * 0.3f, baseGy = GY(root, cx, cz);
        var stone = new Color(0.30f, 0.31f, 0.27f); var moss = new Color(0.18f, 0.34f, 0.18f);
        int tiers = rng.RandiRange(3, 5); float w0 = 9f, th = 1.6f;
        for (int t = 0; t < tiers; t++)
        {
            float w = w0 - t * 1.8f;
            var block = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(w, th, w) }, MaterialOverride = Matte(t % 2 == 0 ? stone : moss) };
            block.Position = new Vector3(cx, baseGy + th * 0.5f + t * th, cz); root.AddChild(block);
        }
        var shrine = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(2.6f, 2.6f, 2.6f) }, MaterialOverride = Matte(stone) };
        shrine.Position = new Vector3(cx, baseGy + tiers * th + 1.3f, cz); root.AddChild(shrine);
        var door = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(1.1f, 1.6f, 0.4f) }, MaterialOverride = Matte(new Color(0.05f, 0.06f, 0.05f)) };
        door.Position = new Vector3(cx, baseGy + tiers * th + 0.9f, cz + 1.3f); root.AddChild(door);
        for (int i = 0; i < 4; i++)   // idol pillars around the base
        {
            float a = i / 4f * Mathf.Tau, pr = w0 * 0.62f, px = cx + Mathf.Cos(a) * pr, pz = cz + Mathf.Sin(a) * pr, pgy = GY(root, px, pz);
            float ph = rng.RandfRange(3f, 5f);
            var pil = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.8f, ph, 0.8f) }, MaterialOverride = Matte(moss) };
            pil.Position = new Vector3(px, pgy + 2f, pz); pil.Rotation = new Vector3(rng.RandfRange(-0.1f, 0.1f), rng.Randf() * 6f, rng.RandfRange(-0.1f, 0.1f)); root.AddChild(pil);
            bl.Add(new Blocker { Pos = World3(c, px, pz), Radius = 0.8f, Top = pgy + 2f + ph * 0.5f });
        }
        bl.Add(new Blocker { Pos = World3(c, cx, cz), Radius = w0 * 0.5f, Top = baseGy + tiers * th + 3f });
    }

    // scattered mossy ruins: broken wall fragments + a toppled idol head
    private void JungleRuins(Node3D root, RandomNumberGenerator rng, List<Blocker> bl, Vector2I c)
    {
        var stone = new Color(0.31f, 0.32f, 0.28f); var moss = new Color(0.16f, 0.32f, 0.16f);
        int walls = rng.RandiRange(3, 6);
        for (int i = 0; i < walls; i++)
        {
            float wx = R(rng), wz = R(rng), gy = GY(root, wx, wz), wh = rng.RandfRange(1.5f, 3.5f);
            var wl = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(rng.RandfRange(2f, 4f), wh, 0.6f) }, MaterialOverride = Matte(rng.Randf() < 0.5f ? stone : moss) };
            wl.Position = new Vector3(wx, gy + 1.2f, wz); wl.Rotation = new Vector3(rng.RandfRange(-0.15f, 0.15f), rng.Randf() * 6f, rng.RandfRange(-0.1f, 0.1f)); root.AddChild(wl);
            bl.Add(new Blocker { Pos = World3(c, wx, wz), Radius = 1.2f, Top = gy + 1.2f + wh * 0.5f });
        }
        float ix = R(rng), iz = R(rng), igy = GY(root, ix, iz);
        var head = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(2f, 2.2f, 2f) }, MaterialOverride = Matte(moss) };
        head.Position = new Vector3(ix, igy + 0.9f, iz); head.Rotation = new Vector3(rng.RandfRange(-0.3f, 0.3f), rng.Randf() * 6f, 0); root.AddChild(head);
        head.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(1.8f, 0.3f, 0.25f) }, MaterialOverride = Matte(stone), Position = new Vector3(0, 0.5f, 1.0f) });   // brow
        head.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.4f, 0.4f, 0.25f) }, MaterialOverride = Matte(new Color(0.05f, 0.06f, 0.05f)), Position = new Vector3(0.4f, 0.1f, 1.0f) });   // eye
        head.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.4f, 0.4f, 0.25f) }, MaterialOverride = Matte(new Color(0.05f, 0.06f, 0.05f)), Position = new Vector3(-0.4f, 0.1f, 1.0f) });
        bl.Add(new Blocker { Pos = World3(c, ix, iz), Radius = 1.3f, Top = igy + 2.4f });
    }

    // ---- biomes -----------------------------------------------------------
    private void Clearing(Node3D root, RandomNumberGenerator rng, List<Blocker> bl, Vector2I c)
    {
        int trees = rng.RandiRange(1, 3);
        for (int i = 0; i < trees; i++) KnottedTree(root, rng, bl, c, R(rng), R(rng), rng.Randf() < 0.1f);   // (AUTUMN) mostly leafy, few bare
        int rocks = rng.RandiRange(1, 4);
        for (int i = 0; i < rocks; i++) Rock(root, rng, R(rng), R(rng));
    }

    private void Forest(Node3D root, RandomNumberGenerator rng, List<Blocker> bl, Vector2I c, int count)
    {
        for (int i = 0; i < count; i++) KnottedTree(root, rng, bl, c, R(rng), R(rng), rng.Randf() < 0.06f);   // (AUTUMN) forest is mostly leafy vibrant trees, only the odd bare one
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

    private void House(Node3D root, RandomNumberGenerator rng, List<Blocker> bl, Vector2I c, Vector2? at = null)
    {
        float lx = at?.X ?? rng.RandfRange(-8f, 8f), lz = at?.Y ?? rng.RandfRange(-8f, 8f);
        if (!DryLand(root.Position.X + lx, root.Position.Z + lz, 0.6f)) return;   // (WATER) cottages never in the water
        if (StructureBlocked(root, lx, lz, bl, 7f)) return;                        // don't drop a cottage on a tree / another structure / a map feature
        float baseY = GY(root, lx, lz);
        // (MESHY) a real cottage model — one of two variants, per-instance tint/scale/yaw so a hamlet isn't clones.
        float ch = rng.RandfRange(12f, 16f);   // (SCALE) cottages read too small — bumped again
        string cot = rng.Randf() < 0.5f ? "cottage_a" : "cottage_b";
        StructureModel(root, cot, new Vector3(lx, baseY, lz), ch, rng.Randf() * Mathf.Tau, bl, (int)rng.Randi());   // solid walls (sloped roof → not standable)
        // a warm window glow for lit cottages (the model has its own windows; this just casts light into the scene)
        if (rng.Randf() < 0.6f)
            root.AddChild(new OmniLight3D { OmniRange = 9f, LightColor = new Color(0.9f, 0.6f, 0.25f), LightEnergy = 1.3f, Position = new Vector3(lx, ch * 0.4f + baseY, lz) });
        // surrounding fence posts + a tree or two
        int posts = rng.RandiRange(4, 8);
        for (int i = 0; i < posts; i++)
        {
            var fp = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.18f, rng.RandfRange(1.0f, 1.6f), 0.18f) } };
            fp.MaterialOverride = Vis.Wood(new Color(0.08f, 0.07f, 0.05f));   // (PHASE 2) wooden fence posts
            float fx = R(rng), fz = R(rng);
            fp.Position = new Vector3(fx, 0.7f + GY(root, fx, fz), fz);
            fp.Rotation = new Vector3(rng.RandfRange(-0.2f, 0.2f), 0, rng.RandfRange(-0.2f, 0.2f));
            root.AddChild(fp);
        }
        if (rng.Randf() < 0.7f) KnottedTree(root, rng, bl, c, R(rng), R(rng), rng.Randf() < 0.2f);
    }

    private void Altar(Node3D root, RandomNumberGenerator rng, List<Blocker> bl, Vector2I c)
    {
        float lx = rng.RandfRange(-6f, 6f), lz = rng.RandfRange(-6f, 6f);
        if (!DryLand(root.Position.X + lx, root.Position.Z + lz, 0.6f)) return;   // (WATER) altars never in the water
        if (StructureBlocked(root, lx, lz, bl, 5f)) return;
        float baseY = GY(root, lx, lz);
        var glow = rng.Randf() < 0.5f ? DamageTypes.Col(DamageType.Curse) : DamageTypes.Col(DamageType.Arcane);
        // (MESHY) the real altar shrine model as the centrepiece (its own candles/bowl baked in)
        StructureModel(root, "altar", new Vector3(lx, baseY, lz), rng.RandfRange(2.3f, 2.9f), rng.Randf() * Mathf.Tau, bl, (int)rng.Randi());   // solid
        // faded sigil glowing on the ground around it
        var sigil = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 2.6f, BottomRadius = 2.6f, Height = 0.04f } };
        var sm = Game.Emissive(glow, 0.5f);
        sm.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        var sc = sm.AlbedoColor; sc.A = 0.25f; sm.AlbedoColor = sc;
        sigil.MaterialOverride = sm;
        sigil.Position = new Vector3(lx, 0.06f + baseY, lz);
        root.AddChild(sigil);
        // candle glow motes clustered on the shrine
        int candles = rng.RandiRange(2, 4);
        for (int i = 0; i < candles; i++)
            root.AddChild(new OmniLight3D { OmniRange = 6f, LightColor = glow, LightEnergy = 1.2f, Position = new Vector3(lx + rng.RandfRange(-1.5f, 1.5f), 1.2f + baseY, lz + rng.RandfRange(-1.5f, 1.5f)) });
    }

    private void Graveyard(Node3D root, RandomNumberGenerator rng, List<Blocker> bl, Vector2I c)
    {
        // (MESHY) a cemetery PLOT — gravestones CLUMPED together in one spot and loosely ROWED (all facing the plot's heading
        // ± jitter), instead of sprinkled randomly across the whole chunk. Dry land only.
        float plx = rng.RandfRange(-10f, 10f), plz = rng.RandfRange(-10f, 10f);
        if (!DryLand(root.Position.X + plx, root.Position.Z + plz, 0.5f)) return;   // (WATER) no graveyards in ponds
        float plotYaw = rng.Randf() * Mathf.Tau;
        var fwd = new Vector2(Mathf.Cos(plotYaw), Mathf.Sin(plotYaw));
        var side = new Vector2(-fwd.Y, fwd.X);
        float half = 7.5f;                       // ~15u plot
        // laid out in ROWS (with walkable aisles between them) but deliberately UNtidy — jittered, a few missing, headstones
        // roughly but not exactly aligned to the plot heading. A cemetery you can wander, not a perfect grid.
        int rows = rng.RandiRange(3, 4);
        int perRow = rng.RandiRange(4, 6);
        float rowGap = (2f * half) / rows;       // aisle spacing between rows
        float colGap = (2f * half) / perRow;
        for (int r = 0; r < rows; r++)
            for (int col = 0; col < perRow; col++)
            {
                if (rng.Randf() < 0.14f) continue;   // gaps in the rows → not too clean
                float rowT = -half + (r + 0.5f) * rowGap + rng.RandfRange(-0.45f, 0.45f);   // along the plot heading (which row)
                float colT = -half + (col + 0.5f) * colGap + rng.RandfRange(-0.7f, 0.7f);   // across the row (position in it)
                float lx = plx + fwd.X * rowT + side.X * colT, lz = plz + fwd.Y * rowT + side.Y * colT;
                if (!DryLand(root.Position.X + lx, root.Position.Z + lz, 0.3f)) continue;
                StructureModel(root, "gravestones", new Vector3(lx, GY(root, lx, lz), lz), rng.RandfRange(1.5f, 2.2f), plotYaw + rng.RandfRange(-0.22f, 0.22f), bl, (int)rng.Randi());   // solid stones
            }
        int dead = rng.RandiRange(1, 3);
        for (int i = 0; i < dead; i++) KnottedTree(root, rng, bl, c, plx + rng.RandfRange(-half, half), plz + rng.RandfRange(-half, half), true);
        int pk = rng.RandiRange(2, 5);   // a scatter of pumpkins among the graves — spooky cemetery dressing
        for (int i = 0; i < pk; i++) SpawnPumpkin(root, rng, plx + rng.RandfRange(-half, half), plz + rng.RandfRange(-half, half));
        if (rng.Randf() < 0.5f) root.AddChild(new OmniLight3D { OmniRange = 12f, LightColor = new Color(0.4f, 0.8f, 0.6f), LightEnergy = 0.6f, Position = new Vector3(plx, 1.6f + GY(root, plx, plz), plz) });
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
        if (!Scatterable(root.Position.X + lx, root.Position.Z + lz)) return;   // (WATER) pumpkins only on dry land
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
        if (!Scatterable(root.Position.X + lx, root.Position.Z + lz)) return;   // (WATER) flowers only on dry land
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
        if (!Scatterable(root.Position.X + lx, root.Position.Z + lz)) return;   // (WATER) grass only on dry land
        var c = ChunkOf(root);
        float gy = GY(root, lx, lz);
        int blades = rng.RandiRange(3, 6);
        for (int i = 0; i < blades; i++)
        {
            float h = rng.RandfRange(0.4f, 0.9f);
            var pos = new Vector3(root.Position.X + lx + rng.RandfRange(-0.3f, 0.3f), h / 2f + gy, root.Position.Z + lz + rng.RandfRange(-0.3f, 0.3f));
            var basis = Basis.FromEuler(new Vector3(rng.RandfRange(-0.25f, 0.25f), rng.Randf() * 6f, rng.RandfRange(-0.25f, 0.25f))).Scaled(new Vector3(0.05f, h, 0.05f));
            _propField.Add(PropField.Kind.Grass, new Transform3D(basis, pos), c);   // (PERF) GPU-instanced grass (was a MeshInstance per blade, each with its own random-colour material → batch-breaking)
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
        if (!Scatterable(root.Position.X + lx, root.Position.Z + lz)) return;   // (WATER) lanterns only on dry land
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
        if (!Scatterable(root.Position.X + lx, root.Position.Z + lz)) return;   // (WATER) carts only on dry land
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
        bl.Add(new Blocker { Pos = World3(c, lx, lz), Radius = 1.4f, Top = by + 2.5f });
    }

    private void Well(Node3D root, RandomNumberGenerator rng, List<Blocker> bl, Vector2I c, float lx, float lz)
    {
        // (MESHY) the real roofed stone well model (its own bucket/rope baked in) + a coarse blocker for its solid base
        if (!DryLand(root.Position.X + lx, root.Position.Z + lz, 0.6f)) return;   // (WATER) wells never in the water
        if (StructureBlocked(root, lx, lz, bl, 3f)) return;
        float by = GY(root, lx, lz);
        StructureModel(root, "well", new Vector3(lx, by, lz), rng.RandfRange(2.7f, 3.3f), rng.Randf() * Mathf.Tau, bl, (int)rng.Randi());   // solid
    }

    // a little cluster of cottages with lanterns, a well, fences and grass — a lived-in village (NEW)
    private void Hamlet(Node3D root, RandomNumberGenerator rng, List<Blocker> bl, Vector2I c)
    {
        int houses = rng.RandiRange(2, 3);
        var placed = new List<Vector2>();
        for (int i = 0; i < houses; i++)
        {
            Vector2 pos = new Vector2(rng.RandfRange(-16f, 16f), rng.RandfRange(-16f, 16f));
            for (int t = 0; t < 16; t++)   // reject spots too close to an already-placed cottage → no houses inside each other
            {
                var cand = new Vector2(rng.RandfRange(-16f, 16f), rng.RandfRange(-16f, 16f));
                bool ok = true;
                foreach (var q in placed) if (cand.DistanceTo(q) < 17f) { ok = false; break; }
                if (ok) { pos = cand; break; }
            }
            placed.Add(pos);
            House(root, rng, bl, c, pos);
        }
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
