using Godot;
using System.Collections.Generic;

// TreeField.cs — global GPU-instanced tree renderer. Every tree of a given (species, variant) across ALL loaded chunks is
// drawn in ONE call via a MultiMesh, so total tree draw calls are ~2 per variant (≈100 for the whole world) instead of 2
// per tree (thousands). That's the structural fix for the streaming FPS: rendering was draw-call (CPU) bound, which is why
// dense tiles tanked FPS while open tiles didn't. Instances are tracked per chunk so they're dropped when a chunk unloads.
public partial class TreeField : Node3D
{
    private class Slot { public MultiMesh BarkMM, LeafMM; }
    private readonly Dictionary<long, Slot> _slots = new();
    private readonly Dictionary<long, List<(Vector2I chunk, Transform3D x)>> _inst = new();
    private readonly HashSet<long> _dirty = new();

    private static long Key(ProcTree.Species sp, int v) => (long)sp * 100 + v;

    // A MultiMesh's auto AABB can be stale/tiny far from the origin in an endless world, which would wrongly frustum-cull
    // every instance. Give each MMI a huge custom AABB so its instances always render (MultiMesh doesn't per-instance cull
    // anyway — the win is draw calls, not culling).
    private void AddMMI(MultiMesh mm, Material mat)
    {
        var mmi = new MultiMeshInstance3D { Multimesh = mm, MaterialOverride = mat };   // shadows on (matches the old node trees); MultiMesh batches the shadow pass too
        mmi.SetCustomAabb(new Aabb(new Vector3(-1e5f, -1e5f, -1e5f), new Vector3(2e5f, 2e5f, 2e5f)));
        AddChild(mmi);
    }

    // register one tree instance (called by World's tree placers instead of adding a node)
    public void Add(ProcTree.Species sp, int variant, Transform3D x, Vector2I chunk)
    {
        long k = Key(sp, variant);
        if (!_inst.TryGetValue(k, out var list)) { list = new List<(Vector2I, Transform3D)>(); _inst[k] = list; }
        list.Add((chunk, x));
        _dirty.Add(k);
    }

    // drop every instance a chunk contributed (on unload or before a lite→full rebuild)
    public void DropChunk(Vector2I chunk)
    {
        foreach (var kv in _inst)
        {
            int before = kv.Value.Count;
            kv.Value.RemoveAll(e => e.chunk == chunk);
            if (kv.Value.Count != before) _dirty.Add(kv.Key);
        }
    }

    public void Clear()
    {
        foreach (var child in GetChildren()) child.QueueFree();   // the MultiMeshInstance3D nodes
        _slots.Clear(); _inst.Clear(); _dirty.Clear();
    }

    // rebuild the buffers of any variant whose instance set changed this frame (called once per frame from World.Update)
    public void Flush()
    {
        if (_dirty.Count == 0) return;
        Dbg.Log($"TreeField.Flush dirty={_dirty.Count}");
        foreach (long k in _dirty) Rebuild(k);
        _dirty.Clear();
        Dbg.Log("TreeField.Flush done");
    }

    private void Rebuild(long k)
    {
        var list = _inst.TryGetValue(k, out var l) ? l : null;
        if (!_slots.TryGetValue(k, out var slot))
        {
            var sp = (ProcTree.Species)(k / 100); int v = (int)(k % 100);
            var (bark, leaf, hasLeaves) = ProcTree.VariantMeshes(sp, v);
            var (bmat, lmat) = ProcTree.SpeciesMats(sp);
            slot = new Slot();
            slot.BarkMM = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = bark };
            AddMMI(slot.BarkMM, bmat);
            if (hasLeaves && leaf != null)
            {
                slot.LeafMM = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true, Mesh = leaf };   // (AUTUMN) per-tree colour offset
                AddMMI(slot.LeafMM, lmat);
            }
            _slots[k] = slot;
        }
        int n = list?.Count ?? 0;
        if (n > 100000) { Dbg.Log($"  WARN runaway instance count {n} for sp={(ProcTree.Species)(k / 100)} — clamping (likely the freeze cause: instances not being dropped)"); n = 100000; }
        Dbg.Log($"  rebuild variant sp={(ProcTree.Species)(k / 100)} v={k % 100} n={n}");
        slot.BarkMM.InstanceCount = n;
        if (slot.LeafMM != null) slot.LeafMM.InstanceCount = n;
        for (int i = 0; i < n; i++)
        {
            var x = list[i].x;
            slot.BarkMM.SetInstanceTransform(i, x);
            if (slot.LeafMM != null)
            {
                slot.LeafMM.SetInstanceTransform(i, x);
                // (AUTUMN) per-tree tone from a stable position hash: .r = red(−)↔gold(+) drift, .g = brightness offset.
                var o = x.Origin;
                float seed = Frac(Mathf.Sin(o.X * 12.98f + o.Z * 78.23f) * 43758.5f);
                float tone = (seed - 0.5f) * 1.2f;                        // whole-tree lean toward red or gold (wide → vivid crimson↔gold spread)
                float val = (Frac(seed * 7.13f + 0.3f) - 0.5f) * 0.16f;   // some trees lusher / more faded
                slot.LeafMM.SetInstanceCustomData(i, new Color(tone, val, 0f, 0f));
            }
        }
    }

    private static float Frac(float x) => x - Mathf.Floor(x);
}
