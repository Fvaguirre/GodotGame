using Godot;
using System.Collections.Generic;

// PropField.cs — GPU-instanced ground scatter (rocks, reeds, ferns, monstera parts, mushrooms). Cousin of TreeField, but
// PER-CHUNK: one MultiMesh per (chunk, kind) with a chunk-sized AABB, so Godot FRUSTUM-CULLS whole chunks that are off-screen.
// (TreeField uses ONE global MultiMesh per variant with a world-spanning AABB — fine for a few big trees, but ground scatter is
// HIGH-count, and forcing every reed/leaf across the whole loaded ring to render every frame overwhelmed the GPU / lost the
// device. Per-chunk grouping keeps the draw-call win — ~a few calls per visible chunk instead of one node per part — AND culls.)
// Instances are stored in WORLD coords; the MMI sits at origin, and the custom AABB spans just this chunk's footprint so it
// culls correctly. Dropped per chunk on unload (mirrors TreeField's lifecycle sites in World).
public partial class PropField : Node3D
{
    public enum Kind { Rock, Reed, Fern, MonsteraStem, MonsteraLeaf, MonsteraNotch, MushroomStem, MushroomCap, Grass,
                       Lily, Cattail, Pebble }   // (NEW) shoreline dressing: floating pads, cattail heads, wet beach shingle

    private class Slot { public MultiMeshInstance3D MMI; public MultiMesh MM; public readonly List<Transform3D> X = new(); }
    private readonly Dictionary<(Vector2I chunk, int kind), Slot> _slots = new();
    private readonly HashSet<(Vector2I chunk, int kind)> _dirty = new();

    // shared mesh + material per kind, built once and reused across every chunk's MMI (Godot shares the resources)
    private static readonly Dictionary<int, (Mesh mesh, Material mat)> _kindMM = new();
    private static (Mesh, Material) KindMM(Kind k)
    {
        if (_kindMM.TryGetValue((int)k, out var v)) return v;
        v = BuildKind(k); _kindMM[(int)k] = v; return v;
    }

    public void Add(Kind kind, Transform3D worldX, Vector2I chunk)
    {
        var key = (chunk, (int)kind);
        if (!_slots.TryGetValue(key, out var slot)) { slot = new Slot(); _slots[key] = slot; }
        slot.X.Add(worldX);
        _dirty.Add(key);
    }

    public void DropChunk(Vector2I chunk)
    {
        // collect this chunk's keys, then free their MMIs and forget them
        List<(Vector2I, int)> gone = null;
        foreach (var kv in _slots)
            if (kv.Key.chunk == chunk) (gone ??= new()).Add(kv.Key);
        if (gone == null) return;
        foreach (var key in gone)
        {
            var s = _slots[key];
            if (s.MMI != null && GodotObject.IsInstanceValid(s.MMI)) s.MMI.QueueFree();
            _slots.Remove(key); _dirty.Remove(key);
        }
    }

    public void Clear()
    {
        foreach (var child in GetChildren()) child.QueueFree();
        _slots.Clear(); _dirty.Clear();
    }

    public void Flush()
    {
        if (_dirty.Count == 0) return;
        foreach (var key in _dirty) Rebuild(key);
        _dirty.Clear();
    }

    private void Rebuild((Vector2I chunk, int kind) key)
    {
        if (!_slots.TryGetValue(key, out var slot)) return;
        if (slot.MMI == null)
        {
            var (mesh, mat) = KindMM((Kind)key.kind);
            // (PAINTERLY) per-instance colour jitter: painterly-material kinds read INSTANCE_CUSTOM so each scattered rock/prop
            // differs slightly in hue/value instead of being an identical clone. Harmless on WindMat kinds (they ignore it).
            slot.MM = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true, Mesh = mesh };
            slot.MMI = new MultiMeshInstance3D { Multimesh = slot.MM, MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };   // small scatter: no shadow pass
            // AABB covering just this chunk's footprint (instances are world-coord, MMI at origin) → Godot frustum-culls off-screen chunks
            float ox = key.chunk.X * World.ChunkSize, oz = key.chunk.Y * World.ChunkSize;
            slot.MMI.SetCustomAabb(new Aabb(new Vector3(ox - 28f, -30f, oz - 28f), new Vector3(World.ChunkSize + 56f, 100f, World.ChunkSize + 56f)));
            AddChild(slot.MMI);
        }
        int n = slot.X.Count;
        slot.MM.InstanceCount = n;
        for (int i = 0; i < n; i++)
        {
            slot.MM.SetInstanceTransform(i, slot.X[i]);
            var o = slot.X[i].Origin;
            int seed = (int)(o.X * 7.31f) * 92821 ^ (int)(o.Z * 13.17f);   // stable per-world-position seed
            slot.MM.SetInstanceCustomData(i, Vis.VaryColorSeeded(seed, 0.03f, 0.07f));
        }
    }

    // the one shared unit mesh + material per Kind — the per-instance size that used to be baked into a unique mesh is baked
    // into the instance transform's scale instead
    private static (Mesh mesh, Material mat) BuildKind(Kind k)
    {
        switch (k)
        {
            // (PAINTERLY DE-RISK SLICE) still ground props moved onto the painterly master material (Vis.Painterly): world-space
            // macro value/hue + roughness variation, no ink outline. Wind-swayed foliage (Reed/Fern/MonsteraLeaf/Grass/Cattail)
            // stays on World.WindMat as an untouched side-by-side control (it needs the vertex sway). Each scattered instance sits
            // at a different world position, so the world-space macro gives natural rock-to-rock variation for free.
            case Kind.Rock:         return (new BoxMesh { Size = Vector3.One }, Vis.Painterly(new Color(0.11f, 0.11f, 0.13f), rough: 0.95f, roughVar: 0.22f, macroValue: 0.24f, macroHue: 0.06f, macroScale: 1.1f));
            case Kind.Reed:         return (new BoxMesh { Size = Vector3.One }, World.WindMat(new Color(0.10f, 0.16f, 0.10f)));
            case Kind.Fern:         return (new BoxMesh { Size = Vector3.One }, World.WindMat(new Color(0.06f, 0.26f, 0.10f)));
            case Kind.MonsteraStem: return (new CylinderMesh { TopRadius = 0.03f, BottomRadius = 0.06f, Height = 1f }, Vis.Painterly(new Color(0.12f, 0.30f, 0.12f), rough: 0.85f, roughVar: 0.18f, macroValue: 0.16f, macroScale: 1.2f));
            case Kind.MonsteraLeaf: return (new SphereMesh { Radius = 1f, Height = 1f, RadialSegments = 8, Rings = 4 }, World.WindMat(new Color(0.07f, 0.36f, 0.14f)));
            case Kind.MonsteraNotch:return (new BoxMesh { Size = Vector3.One }, Vis.Painterly(new Color(0.03f, 0.16f, 0.06f), rough: 0.85f, macroValue: 0.16f, macroScale: 1.2f));
            case Kind.MushroomStem: return (new CylinderMesh { TopRadius = 0.08f, BottomRadius = 0.12f, Height = 1f }, Vis.Painterly(new Color(0.52f, 0.47f, 0.42f), rough: 0.9f, roughVar: 0.15f, macroValue: 0.14f, macroScale: 1.4f));
            case Kind.Grass:        return (new BoxMesh { Size = Vector3.One }, World.WindMat(new Color(0.12f, 0.22f, 0.10f), false));   // Grove grass blades (per-tuft colour variation dropped — one instanced material)
            // (NEW) shoreline set — a flat pad that floats on the water table, a dark velvet cattail head, wet beach shingle
            case Kind.Lily:         return (new CylinderMesh { TopRadius = 1f, BottomRadius = 1f, Height = 0.06f, RadialSegments = 7 }, Vis.Painterly(new Color(0.09f, 0.24f, 0.13f), rough: 0.72f, roughVar: 0.14f, macroValue: 0.14f, macroScale: 1.0f));
            case Kind.Cattail:      return (new CapsuleMesh { Radius = 1f, Height = 3f, RadialSegments = 6, Rings = 2 }, World.WindMat(new Color(0.19f, 0.11f, 0.05f), false));
            case Kind.Pebble:       return (new SphereMesh { Radius = 1f, Height = 1f, RadialSegments = 6, Rings = 3 }, Vis.Painterly(new Color(0.21f, 0.20f, 0.18f), rough: 0.9f, roughVar: 0.2f, macroValue: 0.2f, macroHue: 0.05f, macroScale: 1.6f));
            default:                return (new SphereMesh { Radius = 1f, Height = 1f }, Game.ToonEmissive(new Color(0.40f, 0.10f, 0.28f), 0.6f));   // MushroomCap
        }
    }
}
