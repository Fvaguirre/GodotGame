using Godot;
using System.Collections.Generic;

// Tree.cs — procedural tree generator. Each tree is grown as a recursive branch hierarchy (a trunk whose child branches
// sprout at angles along a curved path, tapering as they go, with leaves at the tips) and then BAKED into two meshes:
// one for bark, one for leaves. The whole thing draws in two calls per tree and sways in the vertex shader.
//
// Wind: every vertex carries a "sway weight" in UV.y (0 = planted trunk base, 1 = flexible tip) plus a per-branch phase
// in UV.x. TreeWind reads those to bend the tree — so the base stays put and the tips flutter, correctly, no matter how
// high the terrain is under the tree (the old world-height hack made trees on hills sway from the ground up).
public static class ProcTree
{
    public enum Species { GroveOak, DeadOak, JungleGiant, JungleGnarled, Understory, Palm, CanopyGiant }

    // ---- species parameters --------------------------------------------------
    private struct Sp
    {
        public float TrunkLen, TrunkRad, LenRatio, RadiusRatio, SplitAngle, Curl, Droop, SwayStep, MinLen;
        public int MaxDepth, Sides;
        public Color Bark, Leaf;
        public bool HasLeaves, Buttress;
        public int LeafKind;           // 0 clustered shaped leaves, 1 broad tropical blades, 2 compound sprigs
        public int LeafProfile;        // silhouette of a clustered leaf: 0 lance, 1 oval, 2 oak-lobed, 3 broad
        public float Blocker;
    }

    private static Sp Preset(Species s, RandomNumberGenerator rng)
    {
        switch (s)
        {
            case Species.DeadOak:
                return new Sp { TrunkLen = rng.RandfRange(4.5f, 7f), TrunkRad = 0.55f, LenRatio = 0.74f, RadiusRatio = 0.58f,
                    SplitAngle = 0.7f, Curl = 0.32f, Droop = 0.05f, SwayStep = 0.26f, MinLen = 0.7f, MaxDepth = 3, Sides = 5,
                    Bark = new Color(0.10f, 0.08f, 0.06f), Leaf = default, HasLeaves = false, Buttress = false, LeafKind = 0, LeafProfile = 2, Blocker = 1.2f };
            case Species.JungleGiant:
                return new Sp { TrunkLen = rng.RandfRange(11f, 16f), TrunkRad = 0.95f, LenRatio = 0.72f, RadiusRatio = 0.6f,
                    SplitAngle = 0.55f, Curl = 0.14f, Droop = 0.12f, SwayStep = 0.2f, MinLen = 1.4f, MaxDepth = 3, Sides = 6,
                    Bark = new Color(0.12f, 0.10f, 0.07f), Leaf = new Color(0.06f, 0.30f, 0.12f), HasLeaves = true, Buttress = true, LeafKind = 1, LeafProfile = 3, Blocker = 1.5f };
            case Species.JungleGnarled:
                return new Sp { TrunkLen = rng.RandfRange(8f, 13f), TrunkRad = 0.7f, LenRatio = 0.74f, RadiusRatio = 0.6f,
                    SplitAngle = 0.72f, Curl = 0.3f, Droop = 0.18f, SwayStep = 0.22f, MinLen = 1.1f, MaxDepth = 3, Sides = 5,
                    Bark = new Color(0.10f, 0.09f, 0.06f), Leaf = new Color(0.07f, 0.28f, 0.11f), HasLeaves = true, Buttress = false, LeafKind = 2, LeafProfile = 1, Blocker = 1.3f };
            case Species.Understory:
                return new Sp { TrunkLen = rng.RandfRange(6f, 9f), TrunkRad = 0.34f, LenRatio = 0.72f, RadiusRatio = 0.58f,
                    SplitAngle = 0.62f, Curl = 0.2f, Droop = 0.1f, SwayStep = 0.28f, MinLen = 0.8f, MaxDepth = 3, Sides = 5,
                    Bark = new Color(0.11f, 0.10f, 0.07f), Leaf = new Color(0.08f, 0.34f, 0.14f), HasLeaves = true, Buttress = false, LeafKind = 0, LeafProfile = 1, Blocker = 1.1f };
            case Species.CanopyGiant:
                return new Sp { TrunkLen = rng.RandfRange(22f, 30f), TrunkRad = 1.3f, LenRatio = 0.7f, RadiusRatio = 0.62f,
                    SplitAngle = 0.5f, Curl = 0.1f, Droop = 0.1f, SwayStep = 0.16f, MinLen = 2.2f, MaxDepth = 3, Sides = 6,
                    Bark = new Color(0.11f, 0.09f, 0.07f), Leaf = new Color(0.06f, 0.28f, 0.11f), HasLeaves = true, Buttress = true, LeafKind = 1, LeafProfile = 3, Blocker = 1.6f };
            case Species.Palm:   // handled by BuildPalmMeshes; params carry blocker + sides
                return new Sp { TrunkLen = rng.RandfRange(11f, 17f), TrunkRad = 0.5f, LenRatio = 0f, RadiusRatio = 0.5f,
                    SplitAngle = 0f, Curl = 0f, Droop = 0f, SwayStep = 0f, MinLen = 0f, MaxDepth = 0, Sides = 6,
                    Bark = new Color(0.24f, 0.17f, 0.10f), Leaf = new Color(0.10f, 0.36f, 0.14f), HasLeaves = true, Buttress = false, LeafKind = 1, LeafProfile = 3, Blocker = 0.9f };
            default:   // GroveOak
                return new Sp { TrunkLen = rng.RandfRange(5f, 7.5f), TrunkRad = 0.5f, LenRatio = 0.72f, RadiusRatio = 0.6f,
                    SplitAngle = 0.6f, Curl = 0.16f, Droop = 0.14f, SwayStep = 0.24f, MinLen = 0.8f, MaxDepth = 3, Sides = 5,
                    Bark = new Color(0.11f, 0.09f, 0.06f), Leaf = new Color(0.05f, 0.20f, 0.09f).Lerp(new Color(0.07f, 0.14f, 0.16f), rng.Randf()), HasLeaves = true, Buttress = false, LeafKind = 0, LeafProfile = 2, Blocker = 1.2f };
        }
    }

    // ---- baked variant pool (threaded) ---------------------------------------
    // Baking a tree (SurfaceTool → ArrayMesh) is the expensive part, and it used to run on the MAIN thread for every tree as
    // chunks streamed in — that was the stutter. Instead we bake a small POOL of variant meshes per species ONCE on a
    // background worker, then every tree just INSTANCES a shared mesh (cheap node creation, main thread). The pool is static
    // so it also survives level-advances — after the first warm-up nothing bakes on the main thread again. If a tree is
    // requested before its species is warm, we bake one inline as a fallback (rare, only at the very start).
    private struct Variant { public ArrayMesh Bark, Leaf; public float Blocker, Height; public bool HasLeaves; public Vector3 Crown; }
    private static readonly Dictionary<Species, List<Variant>> _pool = new();
    private const int VariantsPerSpecies = 7;

    // Get a species' variant pool, growing it by ONE variant if it isn't full yet. Called per tree, so the baking is spread
    // across the first several trees (no single hitch), all ON THE MAIN THREAD. (An earlier version baked variants on a
    // background Task, but creating Godot SurfaceTool/ArrayMesh objects off the main thread — concurrently with the main
    // thread doing the same during streaming — corrupted geometry into NaN vertices AND hard-froze the app. Main-thread only.)
    // The pool is static, so each variant bakes exactly once for the whole session and survives level-advances.
    private static List<Variant> PoolFor(Species sp)
    {
        if (!_pool.TryGetValue(sp, out var l)) { l = new List<Variant>(); _pool[sp] = l; }
        if (l.Count < VariantsPerSpecies)
        {
            var rng = new RandomNumberGenerator { Seed = (ulong)((int)sp + 1) * 2654435761UL + (ulong)l.Count * 40503UL };
            l.Add(BuildMeshes(sp, rng));
        }
        return l;
    }

    // Bake ONE variant's geometry (SurfaceTool → ArrayMesh). Main-thread only.
    private static Variant BuildMeshes(Species species, RandomNumberGenerator rng)
    {
        var sp = Preset(species, rng);
        var bark = new SurfaceTool(); bark.Begin(Mesh.PrimitiveType.Triangles);
        var leaf = new SurfaceTool(); leaf.Begin(Mesh.PrimitiveType.Triangles);
        float h = sp.TrunkLen; Vector3 crown = new Vector3(0f, sp.TrunkLen, 0f);   // fallback anchor = trunk top
        if (species == Species.Palm) BuildPalmMeshes(bark, leaf, sp, rng, ref h, out crown);
        else
        {
            Branch(bark, leaf, Vector3.Zero, Vector3.Up, sp.TrunkLen, sp.TrunkRad, sp.TrunkRad * sp.RadiusRatio, 0f, sp.SwayStep, 0, sp, rng, ref h, ref crown);
            if (sp.Buttress) Buttress(bark, sp, rng);
        }
        bark.GenerateNormals();
        var v = new Variant { Bark = bark.Commit(), Blocker = sp.Blocker, Height = h, HasLeaves = sp.HasLeaves, Crown = crown };
        if (sp.HasLeaves) { leaf.GenerateNormals(); v.Leaf = leaf.Commit(); }
        return v;
    }

    // ---- public API for the MultiMesh renderer (TreeField) --------------------
    // Pick (and lazily bake) a variant for this placement; returns its index + metadata. The caller registers the index +
    // world transform with TreeField, which draws all instances of that variant in one call. (rng advances identically
    // regardless of pool state, so chunk layout stays deterministic.)
    public static int PickVariant(Species sp, RandomNumberGenerator rng, out float blocker, out float height, out Vector3 anchor)
    {
        var l = PoolFor(sp);
        int idx = System.Math.Min(rng.RandiRange(0, VariantsPerSpecies - 1), l.Count - 1);
        var v = l[idx]; blocker = v.Blocker; height = v.Height; anchor = v.Crown;
        return idx;
    }
    public static (ArrayMesh bark, ArrayMesh leaf, bool hasLeaves) VariantMeshes(Species sp, int variant)
    { var v = _pool[sp][variant]; return (v.Bark, v.Leaf, v.HasLeaves); }
    public static (Material bark, Material leaf) SpeciesMats(Species sp)
    { var (bc, lc) = SpeciesColors(sp); return (BarkMat(bc), LeafMat(lc)); }

    // ---- node entry (used only for the vine tree, which carries a unique per-instance vine mesh) ----------------------
    public static Node3D Build(Species species, RandomNumberGenerator rng, out float blockerRadius, out float height, out Vector3 anchor)
    {
        int idx = PickVariant(species, rng, out blockerRadius, out height, out anchor);
        var v = _pool[species][idx];
        var (barkCol, leafCol) = SpeciesColors(species);
        var node = new Node3D();
        node.AddChild(new MeshInstance3D { Mesh = v.Bark, MaterialOverride = BarkMat(barkCol) });
        if (v.HasLeaves && v.Leaf != null) node.AddChild(new MeshInstance3D { Mesh = v.Leaf, MaterialOverride = LeafMat(leafCol) });
        return node;
    }

    private static (Color bark, Color leaf) SpeciesColors(Species s) => s switch
    {
        Species.DeadOak => (new Color(0.10f, 0.08f, 0.06f), default),
        Species.JungleGiant => (new Color(0.12f, 0.10f, 0.07f), new Color(0.06f, 0.30f, 0.12f)),
        Species.JungleGnarled => (new Color(0.10f, 0.09f, 0.06f), new Color(0.07f, 0.28f, 0.11f)),
        Species.Understory => (new Color(0.11f, 0.10f, 0.07f), new Color(0.08f, 0.34f, 0.14f)),
        Species.CanopyGiant => (new Color(0.11f, 0.09f, 0.07f), new Color(0.06f, 0.28f, 0.11f)),
        Species.Palm => (new Color(0.24f, 0.17f, 0.10f), new Color(0.10f, 0.36f, 0.14f)),
        _ => (new Color(0.11f, 0.09f, 0.06f), new Color(0.68f, 0.34f, 0.07f)),   // GroveOak — VIVID autumn orange (shader drifts whole trees red↔gold per instance)
    };

    // A hanging vine: a thin tapering tube from a real bark anchor (a high trunk/branch tip) straight DOWN to a low handhold,
    // parented to the tree so it rides the tree's transform AND sways in lockstep with it (same leaf wind shader, planted at
    // the bottom by its object-space height). `topAnchor` is a tree-LOCAL point ON the tree; the vine drops from there so it
    // never floats in mid-air. When `handhold`, it drops a grab knot + a leaf wrap where it grips the canopy. Returns the
    // LOCAL bottom position (rotate it through the tree's transform for the world grab point).
    public static Vector3 AddVine(Node3D parent, Vector3 topAnchor, float bottomY, bool handhold, RandomNumberGenerator rng)
    {
        float cx = topAnchor.X, cz = topAnchor.Z, topY = topAnchor.Y;
        var st = new SurfaceTool(); st.Begin(Mesh.PrimitiveType.Triangles);
        int segs = Mathf.Clamp((int)((topY - bottomY) / 2.2f) + 3, 4, 12);
        var pts = new List<Vector3>(); var rads = new List<float>(); var sws = new List<float>();
        for (int i = 0; i <= segs; i++)
        {
            float t = i / (float)segs;
            float y = Mathf.Lerp(topY, bottomY, t);
            float bow = Mathf.Sin(t * Mathf.Pi) * 0.3f;   // gentle mid-bow; pinned EXACTLY at the top anchor (t=0) and the handhold (t=1)
            pts.Add(new Vector3(cx + Mathf.Sin(t * 3.4f) * bow, y, cz + Mathf.Cos(t * 2.8f) * bow));
            rads.Add(Mathf.Lerp(0.13f, 0.07f, t));   // thicker where it grips, thinner at the dangling end
            sws.Add(0f);
        }
        EmitTube(st, pts, rads, sws, 0f, 5);
        if (handhold)
            for (int i = 0; i < 5; i++)   // a small leaf wrap at the grip so the attachment reads as grown-in
            {
                var od = new Vector3(rng.RandfRange(-1f, 1f), rng.RandfRange(-0.3f, 1f), rng.RandfRange(-1f, 1f)).Normalized();
                LeafCard(st, topAnchor + od * rng.RandfRange(0.2f, 0.7f), od, rng.RandfRange(0.6f, 1f), rng.RandfRange(0.5f, 0.8f), 0.9f, rng);
            }
        st.GenerateNormals();
        parent.AddChild(new MeshInstance3D { Mesh = st.Commit(), MaterialOverride = LeafMat(new Color(0.10f, 0.26f, 0.12f)) });
        return new Vector3(cx, bottomY, cz);   // (the dangling vine end IS the handhold now — no more ugly knot sphere)
    }

    // ---- recursion -----------------------------------------------------------
    private static void Branch(SurfaceTool bark, SurfaceTool leaf, Vector3 pos, Vector3 dir, float len, float rBase, float rTip,
                               float swayStart, float swayEnd, int depth, Sp sp, RandomNumberGenerator rng, ref float height, ref Vector3 topTip)
    {
        int segs = Mathf.Clamp((int)(len / 1.6f) + 2, 3, 7);
        var pts = new List<Vector3>(segs + 1);
        var rads = new List<float>(segs + 1);
        var sws = new List<float>(segs + 1);
        Vector3 p = pos, d = dir.Normalized();
        // a per-branch bend axis (perpendicular to the growth direction) gives each branch its own gentle curve
        Vector3 bendAxis = Perp(d, rng.Randf() * Mathf.Tau);
        float curl = sp.Curl * rng.RandfRange(0.4f, 1.4f) * (rng.Randf() < 0.5f ? 1f : -1f);
        float step = len / segs;
        for (int i = 0; i <= segs; i++)
        {
            float t = i / (float)segs;
            pts.Add(p); rads.Add(Mathf.Lerp(rBase, rTip, t)); sws.Add(Mathf.Lerp(swayStart, swayEnd, t));
            p += d * step;
            d = d.Rotated(bendAxis, curl * step).Normalized();           // curve the path
            if (depth > 0) d = d.Lerp(Vector3.Down, sp.Droop * 0.12f).Normalized();   // branches droop under their own weight
        }
        float phase = rng.Randf();
        EmitTube(bark, pts, rads, sws, phase, sp.Sides);

        Vector3 tip = pts[pts.Count - 1];
        Vector3 tipDir = d;
        if (tip.Y > height) height = tip.Y;                        // tree height tracks the highest of ANY branch
        if (depth == 0) topTip = pts[Mathf.Clamp((int)(segs * 0.72f), 1, segs)];   // vine anchor: a point on the UPPER TRUNK (central + thick) so vines hang DOWN the tree, not off a far-reaching branch tip out in the air
        if (depth >= sp.MaxDepth || len < sp.MinLen)
        {
            if (sp.HasLeaves) EmitLeaves(leaf, tip, tipDir, len, sp, rng, true);   // terminal branch → a full cluster
            return;
        }

        int n = depth == 0 ? rng.RandiRange(3, 4) : rng.RandiRange(2, 3);
        float azBase = rng.Randf() * Mathf.Tau;
        for (int i = 0; i < n; i++)
        {
            // trunk (depth 0) sprouts its limbs from the UPPER stretch; deeper branches fork from the tip
            Vector3 cpos; Vector3 cdir;
            if (depth == 0)
            {
                int idx = rng.RandiRange(Mathf.Max(1, segs / 2), segs);
                cpos = pts[idx];
                cdir = (pts[Mathf.Min(idx, segs)] - pts[Mathf.Max(0, idx - 1)]).Normalized();
            }
            else { cpos = tip; cdir = tipDir; }
            float az = azBase + i / (float)n * Mathf.Tau + rng.RandfRange(-0.4f, 0.4f);
            float ang = sp.SplitAngle * rng.RandfRange(0.7f, 1.3f);
            Vector3 childDir = cdir.Rotated(Perp(cdir, az), ang).Normalized();
            childDir = childDir.Lerp(Vector3.Up, 0.12f).Normalized();     // branches still reach a little skyward
            float clen = len * sp.LenRatio * rng.RandfRange(0.85f, 1.12f);
            Branch(bark, leaf, cpos, childDir, clen, rTip, rTip * sp.RadiusRatio,
                   swayEnd, Mathf.Min(1f, swayEnd + sp.SwayStep), depth + 1, sp, rng, ref height, ref topTip);
        }
        // a lighter interior tuft where a mid branch ends, for fullness
        if (sp.HasLeaves && depth >= 1) EmitLeaves(leaf, tip, tipDir, len, sp, rng, false);
    }

    // buttress roots flaring out at the base of a rainforest giant
    private static void Buttress(SurfaceTool bark, Sp sp, RandomNumberGenerator rng)
    {
        int n = rng.RandiRange(4, 6);
        for (int i = 0; i < n; i++)
        {
            float a = i / (float)n * Mathf.Tau + rng.RandfRange(-0.2f, 0.2f);
            var outward = new Vector3(Mathf.Cos(a), 0.9f, Mathf.Sin(a)).Normalized();
            var pts = new List<Vector3> { new Vector3(Mathf.Cos(a) * sp.TrunkRad * 0.6f, 2.6f, Mathf.Sin(a) * sp.TrunkRad * 0.6f),
                                          new Vector3(Mathf.Cos(a) * sp.TrunkRad * 1.9f, 0.05f, Mathf.Sin(a) * sp.TrunkRad * 1.9f) };
            var rads = new List<float> { sp.TrunkRad * 0.45f, sp.TrunkRad * 0.28f };
            var sws = new List<float> { 0f, 0f };   // roots never sway
            EmitTube(bark, pts, rads, sws, rng.Randf(), sp.Sides);
        }
    }

    // ---- palm (trunk + crown of drooping fronds) -----------------------------
    private static void BuildPalmMeshes(SurfaceTool bark, SurfaceTool leaf, Sp sp, RandomNumberGenerator rng, ref float height, out Vector3 crown)
    {
        float h = sp.TrunkLen;
        float lean = rng.RandfRange(-0.12f, 0.12f);
        int segs = 8;
        var pts = new List<Vector3>(); var rads = new List<float>(); var sws = new List<float>();
        for (int i = 0; i <= segs; i++)
        {
            float t = i / (float)segs;
            // a gentle S-lean, thicker at the base
            pts.Add(new Vector3(Mathf.Sin(t * 1.3f) * lean * h, t * h, 0f));
            rads.Add(Mathf.Lerp(0.55f, 0.28f, t));
            sws.Add(t * t * 0.7f);   // palms bend mostly near the crown
        }
        EmitTube(bark, pts, rads, sws, rng.Randf(), sp.Sides);
        Vector3 top = pts[pts.Count - 1];
        height = h; crown = top;   // the coconuts (main-thread primitives) get placed here at instance time

        int fronds = rng.RandiRange(9, 13);
        for (int i = 0; i < fronds; i++)
        {
            float a = i / (float)fronds * Mathf.Tau + rng.RandfRange(-0.12f, 0.12f);
            float pitch = rng.RandfRange(-0.15f, 0.25f);   // some fronds up, most arcing down
            var outDir = new Vector3(Mathf.Cos(a) * Mathf.Cos(pitch), Mathf.Sin(pitch), Mathf.Sin(a) * Mathf.Cos(pitch)).Normalized();
            EmitFrond(leaf, top, outDir, rng.RandfRange(3.8f, 5.2f), sp, rng);
        }
    }

    // a palm frond: a tapering strip of leaf quads arcing outward then drooping down
    private static void EmitFrond(SurfaceTool leaf, Vector3 baseP, Vector3 dir, float len, Sp sp, RandomNumberGenerator rng)
    {
        int segs = 5;
        Vector3 p = baseP, d = dir.Normalized();
        Vector3 side = d.Cross(Vector3.Up).Normalized(); if (side.LengthSquared() < 0.01f) side = Vector3.Right;
        float phase = rng.Randf();
        float seg = len / segs;
        for (int i = 0; i < segs; i++)
        {
            float t0 = i / (float)segs, t1 = (i + 1) / (float)segs;
            float w0 = Mathf.Lerp(0.55f, 0.08f, t0), w1 = Mathf.Lerp(0.55f, 0.08f, t1);
            Vector3 p1 = p + d * seg;
            float sw0 = 0.4f + t0 * 0.6f, sw1 = 0.4f + t1 * 0.6f;
            AddQuad(leaf, p - side * w0, p + side * w0, p1 + side * w1, p1 - side * w1, phase, sw0, sw0, sw1, sw1);
            p = p1;
            d = d.Lerp(Vector3.Down, 0.3f).Normalized();   // droop more the further out
        }
    }

    // ---- leaves --------------------------------------------------------------
    // Each species grows its own kind of foliage. `full` = a terminal-branch cluster (dense); otherwise a lighter interior
    // tuft. Everything bakes into the one leaf mesh, so more leaves = more verts but still ONE draw call per tree.
    private static void EmitLeaves(SurfaceTool leaf, Vector3 tip, Vector3 dir, float branchLen, Sp sp, RandomNumberGenerator rng, bool full)
    {
        if (sp.LeafKind == 1)   // broad tropical blades — few but large
        {
            int n = full ? rng.RandiRange(4, 6) : rng.RandiRange(1, 2);
            for (int i = 0; i < n; i++)
                LeafBlade(leaf, tip, RandLeafDir(rng), rng.RandfRange(1.7f, 2.7f), rng.RandfRange(1.0f, 1.5f), 3, rng);
        }
        else if (sp.LeafKind == 2)   // compound sprigs — a rachis lined with many small leaflets (ferny/acacia look)
        {
            int n = full ? rng.RandiRange(1, 2) : 0;
            for (int i = 0; i < n; i++)
                LeafSprig(leaf, tip, RandLeafDir(rng), rng.RandfRange(1.5f, 2.3f), rng);
        }
        else   // clustered shaped leaves — MANY small blades, silhouette per species (oak-lobed / oval / …)
        {
            int n = full ? rng.RandiRange(13, 18) : rng.RandiRange(6, 9);   // (FULLER) lush autumn canopy, not sparse winter twigs
            for (int i = 0; i < n; i++)
            {
                var od = RandLeafDir(rng);
                var c = tip + od * rng.RandfRange(0.15f, 1.5f);   // bigger fuller clusters
                LeafBlade(leaf, c, od, rng.RandfRange(0.7f, 1.3f), rng.RandfRange(0.48f, 0.82f), sp.LeafProfile, rng);
            }
        }
    }

    private static Vector3 RandLeafDir(RandomNumberGenerator rng)
        => new Vector3(rng.RandfRange(-1f, 1f), rng.RandfRange(-0.35f, 1f), rng.RandfRange(-1f, 1f)).Normalized();

    // A flat SHAPED leaf built as a short strip of triangles along a midrib. The silhouette comes from LeafWidth(profile),
    // so the same builder makes lance, oval, oak-lobed, or broad tropical leaves. A gentle cup keeps them from reading flat.
    private static void LeafBlade(SurfaceTool st, Vector3 at, Vector3 dir, float len, float width, int profile, RandomNumberGenerator rng)
    {
        Vector3 fwd = dir.Lerp(Vector3.Down, 0.18f).Normalized();
        Vector3 side = fwd.Cross(Vector3.Up); if (side.LengthSquared() < 0.001f) side = Vector3.Right; side = side.Normalized();
        Vector3 nrm = side.Cross(fwd).Normalized();
        int segs = profile == 2 ? 4 : 2;   // keep leaves cheap (they bake on the main thread); lobed needs a couple extra to scallop
        Vector3 pl = Vector3.Zero, pr = Vector3.Zero; bool have = false;
        for (int i = 0; i <= segs; i++)
        {
            float s = i / (float)segs;
            // floor the width so the base/tip never collapse to a single point — a zero-width cross-section makes zero-area
            // triangles, which GenerateNormals can't normalize (that was the flood of "Vector3 cannot be normalized" warnings).
            float w = Mathf.Max(LeafWidth(profile, s) * width, width * 0.07f);
            Vector3 c = at + fwd * (len * s) + nrm * (Mathf.Sin(s * Mathf.Pi) * width * 0.14f);   // cup
            Vector3 l = c + side * (w * 0.5f), r = c - side * (w * 0.5f);
            if (have) AddQuad(st, pl, l, r, pr, 0f, 0f, 0f, 0f, 0f);
            pl = l; pr = r; have = true;
        }
    }

    // half-width of a leaf at arc position s∈[0,1] along its midrib, per silhouette
    private static float LeafWidth(int profile, float s)
    {
        switch (profile)
        {
            case 1:  return Mathf.Pow(Mathf.Sin(s * Mathf.Pi), 0.6f);                                              // oval / rounded
            case 2:  return Mathf.Max(0.05f, Mathf.Sin(s * Mathf.Pi) * (0.8f + 0.32f * Mathf.Cos(s * Mathf.Pi * 5f)));  // oak — scalloped lobes
            case 3:  return Mathf.Sin(Mathf.Clamp(s, 0f, 1f) * Mathf.Pi * 0.92f + 0.08f);                          // broad tropical (wide, rounded)
            default: return Mathf.Sin(s * Mathf.Pi) * (1f - 0.35f * s);                                            // 0 lance / pointed
        }
    }

    // a compound leaf: a central rachis with paired oval leaflets down its length + a terminal leaflet
    private static void LeafSprig(SurfaceTool st, Vector3 at, Vector3 dir, float len, RandomNumberGenerator rng)
    {
        Vector3 fwd = dir.Lerp(Vector3.Down, 0.1f).Normalized();
        Vector3 side = fwd.Cross(Vector3.Up); if (side.LengthSquared() < 0.001f) side = Vector3.Right; side = side.Normalized();
        int pairs = rng.RandiRange(4, 6);
        for (int i = 1; i <= pairs; i++)
        {
            float s = i / (float)(pairs + 1);
            Vector3 c = at + fwd * (len * s);
            float ll = len * 0.30f * (1f - 0.35f * s);
            for (int k = -1; k <= 1; k += 2)
            {
                Vector3 ld = (fwd * 0.45f + side * (k * 0.95f) + Vector3.Down * 0.12f).Normalized();
                LeafBlade(st, c, ld, ll, ll * 0.42f, 1, rng);
            }
        }
        LeafBlade(st, at + fwd * (len * 0.9f), fwd, len * 0.28f, len * 0.11f, 1, rng);
    }

    // a simple pointed leaf quad — still used for the little wraps on hanging vines
    private static void LeafCard(SurfaceTool leaf, Vector3 at, Vector3 dir, float l, float w, float sway, RandomNumberGenerator rng)
    {
        Vector3 fwd = dir.Lerp(Vector3.Down, 0.25f).Normalized();
        Vector3 side = fwd.Cross(Vector3.Up).Normalized(); if (side.LengthSquared() < 0.01f) side = Vector3.Right;
        Vector3 mid = at + fwd * (l * 0.5f);
        Vector3 tip = at + fwd * l;
        AddQuad(leaf, at, mid + side * (w * 0.5f), tip, mid - side * (w * 0.5f), 0f, 0f, 0f, 0f, 0f);
    }

    // ---- geometry helpers ----------------------------------------------------
    // a tapered tube along a centreline; each ring carries its own sway weight
    private static void EmitTube(SurfaceTool st, List<Vector3> pts, List<float> rads, List<float> sws, float phase, int sides)
    {
        Vector3[] prev = null; float prevSw = 0f;
        Vector3 nrm = Vector3.Zero;
        for (int i = 0; i < pts.Count; i++)
        {
            Vector3 tan = (i < pts.Count - 1 ? pts[i + 1] - pts[i] : pts[i] - pts[i - 1]).Normalized();
            if (i == 0)
            {
                Vector3 up = Mathf.Abs(tan.Y) > 0.99f ? Vector3.Right : Vector3.Up;   // seed the first ring's frame
                nrm = up.Cross(tan).Normalized();
            }
            else
            {
                // PARALLEL TRANSPORT the frame: project the previous normal onto the plane ⊥ the new tangent. Keeps the ring
                // orientation CONTINUOUS from segment to segment, so it can never flip 90° into a bowtie/X twist (the old code
                // rebuilt the frame from a fixed up-vector that jumped from Up→Right as the trunk passed vertical).
                nrm = nrm - tan * nrm.Dot(tan);
                if (nrm.LengthSquared() < 1e-6f) { Vector3 up = Mathf.Abs(tan.Y) > 0.99f ? Vector3.Right : Vector3.Up; nrm = up.Cross(tan); }
                nrm = nrm.Normalized();
            }
            Vector3 bin = tan.Cross(nrm).Normalized();
            var ring = new Vector3[sides];
            for (int s = 0; s < sides; s++)
            {
                float a = s / (float)sides * Mathf.Tau;
                // (RELIEF) gnarl the cross-section with a vertical-ridge displacement so the trunk is bumpy bark that catches
                // light (GenerateNormals then gives real per-facet normals), not a smooth cylinder. Scales with radius, so
                // thick trunks read gnarled and thin twigs stay clean.
                float ridge = Mathf.Sin(a * 7f + pts[i].Y * 0.3f) * 0.6f + Mathf.Sin(a * 15f + pts[i].Y * 0.11f) * 0.4f;
                ring[s] = pts[i] + (Mathf.Cos(a) * nrm + Mathf.Sin(a) * bin) * rads[i] * (1f + ridge * 0.13f);
            }
            if (prev != null)
                for (int s = 0; s < sides; s++)
                {
                    int s2 = (s + 1) % sides;
                    AddQuad(st, prev[s], prev[s2], ring[s2], ring[s], phase, prevSw, prevSw, sws[i], sws[i]);
                }
            prev = ring; prevSw = sws[i];
        }
    }

    // two triangles a-b-c, a-c-d (the phase/sway params are legacy — the shaders drive sway from VERTEX.y now, so no UV needed)
    private static void AddQuad(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c, Vector3 d, float phase, float sa, float sb, float sc, float sd)
    {
        AddTri(st, a, b, c);
        AddTri(st, a, c, d);
    }
    // add a triangle, skipping it if it has ~zero area — a degenerate (collapsed) triangle makes GenerateNormals try to
    // normalize a (0,0,0) cross-product, which is the flood of "Vector3 cannot be normalized" warnings.
    private static void AddTri(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c)
    {
        if (!Finite(a) || !Finite(b) || !Finite(c)) return;           // NaN/Inf vertex → GenerateNormals can't normalize it
        if ((b - a).Cross(c - a).LengthSquared() < 1e-8f) return;     // zero-area (collapsed) triangle → same problem
        st.AddVertex(a); st.AddVertex(b); st.AddVertex(c);
    }
    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    // a unit vector perpendicular to `dir`, rotated by `az` around dir (picks a tilt direction for a child branch)
    private static Vector3 Perp(Vector3 dir, float az)
    {
        Vector3 n = dir.Cross(Vector3.Up);
        if (n.LengthSquared() < 0.001f) n = dir.Cross(Vector3.Right);
        n = n.Normalized();
        return n.Rotated(dir.Normalized(), az).Normalized();
    }

    // ---- materials (shared, cached) -----------------------------------------
    private static readonly Dictionary<uint, ShaderMaterial> _barkMats = new();
    private static readonly Dictionary<uint, ShaderMaterial> _leafMats = new();
    private static Shader _barkShader, _leafShader;
    private static float _fadeStart = 125f, _fadeEnd = 172f;   // camera-distance LOD fade band (set from the render-distance setting)

    // set the LOD fade band on all tree materials (and remember it for materials created later)
    public static void SetFade(float start, float end)
    {
        _fadeStart = start; _fadeEnd = end;
        foreach (var m in _barkMats.Values) { m.SetShaderParameter("fade_start", start); m.SetShaderParameter("fade_end", end); }
        foreach (var m in _leafMats.Values) { m.SetShaderParameter("fade_start", start); m.SetShaderParameter("fade_end", end); }
    }

    private static ShaderMaterial BarkMat(Color c)
    {
        uint k = c.ToRgba32();
        if (_barkMats.TryGetValue(k, out var m)) return m;
        _barkShader ??= new Shader { Code = BarkCode };
        m = new ShaderMaterial { Shader = _barkShader };
        m.SetShaderParameter("base_color", c);
        m.SetShaderParameter("fade_start", _fadeStart); m.SetShaderParameter("fade_end", _fadeEnd);
        _barkMats[k] = m; return m;
    }
    private static ShaderMaterial LeafMat(Color c)
    {
        uint k = c.ToRgba32();
        if (_leafMats.TryGetValue(k, out var m)) return m;
        _leafShader ??= new Shader { Code = LeafCode };
        m = new ShaderMaterial { Shader = _leafShader };
        m.SetShaderParameter("base_color", c);
        m.SetShaderParameter("fade_start", _fadeStart); m.SetShaderParameter("fade_end", _fadeEnd);
        _leafMats[k] = m; return m;
    }

    // Wind. The sway offset is a PURE CONTINUOUS FUNCTION OF POSITION: the weight comes from object-space height (VERTEX.y,
    // i.e. height above the tree's own base) and the gust from world XZ — no per-branch phase. That's the whole trick for
    // clean joints: any two co-located vertices (a parent branch's tip ring and the child branch growing out of it) read
    // the SAME weight and SAME gust, so they move together and the joint can never pull apart. Using object-space height
    // (not world height) also keeps the base planted no matter how high the terrain is under the tree.
    private const string BarkCode = @"
shader_type spatial;
render_mode cull_disabled;                             // (PAINTERLY) dropped diffuse_toon/specular_toon — soft painterly lighting now
uniform vec4 base_color : source_color = vec4(0.3,0.25,0.15,1.0);
uniform float sway_strength = 0.2;
uniform float fade_start = 125.0;   // trees dither-fade between these camera distances so they don't POP in/out at the streaming edge
uniform float fade_end = 172.0;
varying vec3 opos;
varying vec3 wpos;
float h13(vec3 p){ p = fract(p*0.1031); p += dot(p, p.yzx+33.33); return fract((p.x+p.y)*p.z); }
float vn(vec3 p){ vec3 i=floor(p); vec3 f=fract(p); f=f*f*(3.0-2.0*f);
  return mix(mix(mix(h13(i),h13(i+vec3(1,0,0)),f.x),mix(h13(i+vec3(0,1,0)),h13(i+vec3(1,1,0)),f.x),f.y),
             mix(mix(h13(i+vec3(0,0,1)),h13(i+vec3(1,0,1)),f.x),mix(h13(i+vec3(0,1,1)),h13(i+vec3(1,1,1)),f.x),f.y),f.z); }
float fbm(vec3 p){ float a=0.0; float m=0.5; a+=m*vn(p); p*=2.02; m*=0.5; a+=m*vn(p); p*=2.03; m*=0.5; a+=m*vn(p); return a; }
void vertex(){
    opos = VERTEX;
    vec3 wp = (MODEL_MATRIX * vec4(VERTEX,1.0)).xyz;
    wpos = wp;
    float w = smoothstep(0.6, 6.5, VERTEX.y);          // planted base -> swaying canopy (continuous -> no joint gaps)
    float g = sin(TIME*0.5 + wp.x*0.14 + wp.z*0.12);   // one coherent gust per tree, drifting across the world
    float amt = g * w * sway_strength;
    VERTEX.x += amt * 0.9;
    VERTEX.z += amt * 0.5;
}
void fragment(){
    float fade = 1.0 - smoothstep(fade_start, fade_end, distance(wpos, CAMERA_POSITION_WORLD));
    if (fade < 0.999){ float dth = fract(sin(dot(floor(FRAGCOORD.xy), vec2(12.9898,78.233)))*43758.5453); if (fade < dth) discard; }
    float macro  = fbm(wpos * 0.16);                   // large tonal variation + tree-to-tree drift
    // VERTICAL BARK BANDS: noise stretched hard up the trunk axis, sharpened, then mixed between a lit CREST colour and a
    // dark GROOVE colour. Absolute-colour contrast (not multiplicative) so it survives the ambient wash that made the old
    // grain invisible on near-black bark.
    float ridge  = fbm(opos * vec3(12.0, 0.7, 12.0));
    float groove = smoothstep(0.32, 0.68, abs(ridge - 0.5) * 2.0);
    float fine   = fbm(opos * 30.0);                   // close-up bark grain
    vec3 barkLit = base_color.rgb * 1.7 + vec3(0.05, 0.035, 0.02);   // warm raised ridge crest
    vec3 barkDrk = base_color.rgb * 0.5;                             // deep shadowed groove
    vec3 col = mix(barkLit, barkDrk, groove);
    col *= 0.90 + 0.30*(macro - 0.5);                  // large drift + tree-to-tree variation
    col *= 0.94 + 0.12*fine;                           // fine grain
    col.r += (macro-0.5)*0.05;
    ALBEDO = clamp(col, vec3(0.02), vec3(1.0));
    ROUGHNESS = clamp(0.9 + groove*0.06, 0.6, 1.0);
}";
    private const string LeafCode = @"
shader_type spatial;
render_mode cull_disabled;                             // (PAINTERLY) dropped toon shading — soft lighting + masked rim translucency
uniform vec4 base_color : source_color = vec4(0.1,0.3,0.12,1.0);
uniform float sway_strength = 0.2;
uniform float fade_start = 125.0;
uniform float fade_end = 172.0;
varying vec3 opos;
varying vec3 wpos;
varying vec4 icustom;   // (AUTUMN) per-tree colour offset from MultiMesh custom data
float h13(vec3 p){ p = fract(p*0.1031); p += dot(p, p.yzx+33.33); return fract((p.x+p.y)*p.z); }
float vn(vec3 p){ vec3 i=floor(p); vec3 f=fract(p); f=f*f*(3.0-2.0*f);
  return mix(mix(mix(h13(i),h13(i+vec3(1,0,0)),f.x),mix(h13(i+vec3(0,1,0)),h13(i+vec3(1,1,0)),f.x),f.y),
             mix(mix(h13(i+vec3(0,0,1)),h13(i+vec3(1,0,1)),f.x),mix(h13(i+vec3(0,1,1)),h13(i+vec3(1,1,1)),f.x),f.y),f.z); }
float fbm(vec3 p){ float a=0.0; float m=0.5; a+=m*vn(p); p*=2.02; m*=0.5; a+=m*vn(p); p*=2.03; m*=0.5; a+=m*vn(p); return a; }
void vertex(){
    opos = VERTEX;
    icustom = INSTANCE_CUSTOM;
    vec3 wp = (MODEL_MATRIX * vec4(VERTEX,1.0)).xyz;
    wpos = wp;
    float w = max(smoothstep(0.6, 6.5, VERTEX.y), 0.32);   // leaves always flutter a little (separate cards, no joint concern)
    float g = sin(TIME*0.5 + wp.x*0.14 + wp.z*0.12);
    float fl = sin(TIME*2.4 + wp.x*0.6 + wp.z*0.55)*0.35;   // extra leaf flutter
    float amt = (g + fl) * w * sway_strength;
    VERTEX.x += amt * 0.9;
    VERTEX.z += amt * 0.5;
    VERTEX.y += fl * w * sway_strength * 0.25;
}
void fragment(){
    float fade = 1.0 - smoothstep(fade_start, fade_end, distance(wpos, CAMERA_POSITION_WORLD));   // fade leaves in/out at the streaming edge (no harsh pop)
    if (fade < 0.999){ float dth = fract(sin(dot(floor(FRAGCOORD.xy), vec2(12.9898,78.233)))*43758.5453); if (fade < dth) discard; }
    float macro = fbm(wpos * 0.22 + vec3(5.0));         // clump-to-clump colour drift
    float fine  = fbm(opos * 4.5);                       // dappled leaf mottle
    vec3 col = base_color.rgb * (0.78 + 0.30*macro + 0.16*fine);
    // AUTUMN drift — some clumps turn redder/russet, others hold gold; a touch browner in the shade
    col.r += (macro-0.5)*0.10;
    col.g += (macro-0.5)*0.04 + (fine-0.5)*0.05;
    // PER-TREE tone (MultiMesh custom data): shift the GREEN channel so a whole tree leans red(−) ↔ gold/yellow(+) while R
    // stays high (reds stay VIVID crimson, not brown); .g nudges brightness.
    col.g += icustom.r * 0.32;
    col *= 1.0 + icustom.g;
    ALBEDO = clamp(col, vec3(0.0), vec3(1.0));
    ROUGHNESS = 0.9;
    float fres = pow(1.0 - clamp(dot(NORMAL, VIEW), 0.0, 1.0), 3.0);   // leaf EDGES catch a little green backlight (masked, not full-surface)
    EMISSION = base_color.rgb * fres * 0.18;
}";
}
