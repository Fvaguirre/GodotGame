using Godot;
using System.Collections.Generic;

// PropGlb.cs — turns a Meshy-authored prop/structure GLB into pieces the game can reuse cheaply:
//   • a single MESH resource, BAKED so it stands exactly 1.0 unit tall with its base at y=0 and centred on XZ — the model's
//     arbitrary export transform/scale is baked into the vertices, so callers place it with a plain desired-height scale and a
//     clean origin (Position/Rotation are free to set), and
//   • a shared instanced ShaderMaterial (shaders/prop_instanced.gdshader) that samples the GLB's baked albedo/normal and
//     layers the game's per-instance hue/value jitter on top.
//
// Two consumers:
//   • MultiMesh scatter (PropField) — Mesh(name) + Mat(name); the mesh is already normalised, so instance transforms are just
//     a height scale. PropField writes INSTANCE_CUSTOM for per-instance colour variation.
//   • single MeshInstance3D nodes (Pumpkin, Flower, structures) — Instance(name, height) returns a ready node, tinted via node_var.
//
// Everything is cached by name; the GLB is instantiated once at first use and then discarded (we keep only the baked Mesh +
// textures). Meshy props are single-mesh / single-material, so we take the first MeshInstance3D and bake all its surfaces.
public static class PropGlb
{
    private class Entry { public Mesh Mesh; public ShaderMaterial Mat; public Vector2 Ext = Vector2.One; }
    private static readonly Dictionary<string, Entry> _cache = new();

    private static Shader _shader;
    private static Shader PropShader => _shader ??= GD.Load<Shader>("res://shaders/prop_instanced.gdshader");

    private static string PathFor(string name, string subdir) => $"res://assets/models/{subdir}/{name}.glb";

    private static Entry Load(string name, string subdir, float rough, float wind, bool tryNormal, bool byMaxDim = false)
    {
        string key = subdir + "/" + name;
        if (_cache.TryGetValue(key, out var e)) return e;
        e = new Entry();

        var scene = GD.Load<PackedScene>(PathFor(name, subdir));
        if (scene == null) { GD.PushWarning($"PropGlb: missing {PathFor(name, subdir)}"); _cache[key] = e; return e; }
        var root = scene.Instantiate<Node3D>();

        // find the first MeshInstance3D that carries geometry, and the transform accumulated from the scene root down to it
        MeshInstance3D mi = null; Transform3D acc = Transform3D.Identity;
        FindMesh(root, Transform3D.Identity, ref mi, ref acc);
        if (mi == null || mi.Mesh == null) { GD.PushWarning($"PropGlb: no mesh in {name}"); root.QueueFree(); _cache[key] = e; return e; }

        var src = mi.Mesh;

        // oriented AABB of the mesh in its authored (post-transform) space → normalise to unit height, base at 0, centred XZ
        var ab = src.GetAabb();
        Vector3 min = new(float.MaxValue, float.MaxValue, float.MaxValue), max = new(-float.MaxValue, -float.MaxValue, -float.MaxValue);
        for (int i = 0; i < 8; i++)
        {
            var corner = ab.Position + ab.Size * new Vector3((i & 1), (i >> 1) & 1, (i >> 2) & 1);
            var w = acc * corner;
            min = new Vector3(Mathf.Min(min.X, w.X), Mathf.Min(min.Y, w.Y), Mathf.Min(min.Z, w.Z));
            max = new Vector3(Mathf.Max(max.X, w.X), Mathf.Max(max.Y, w.Y), Mathf.Max(max.Z, w.Z));
        }
        // normalise by HEIGHT (default — so callers place by a plain desired height) OR by the LARGEST dimension (for FLAT props
        // like leaves/lilypads: normalising a thin-Y leaf by height would blow its width up boat-sized).
        float sizeY = max.Y - min.Y;
        float h = Mathf.Max(0.001f, byMaxDim ? Mathf.Max(sizeY, Mathf.Max(max.X - min.X, max.Z - min.Z)) : sizeY);
        var center = new Vector3((min.X + max.X) * 0.5f, min.Y, (min.Z + max.Z) * 0.5f);   // base at min.Y, centred XZ
        // norm(p) = ( acc*p - center ) / h   — bake this into every vertex so the mesh is unit-height, based & centred
        var norm = new Transform3D(Basis.Identity.Scaled(Vector3.One / h), Vector3.Zero)
                 * new Transform3D(Basis.Identity, -center) * acc;

        e.Mesh = BakeNormalized(src, norm);
        e.Ext = new Vector2((max.X - min.X) * 0.5f / h, (max.Z - min.Z) * 0.5f / h);   // normalised XZ half-extents (× world height = real footprint)

        // baked textures off surface 0's material (before we discard the source)
        Texture2D albedo = null, normal = null;
        if (src.SurfaceGetMaterial(0) is BaseMaterial3D bm) { albedo = bm.AlbedoTexture; normal = bm.NormalTexture; }

        var mat = new ShaderMaterial { Shader = PropShader };
        if (albedo != null) mat.SetShaderParameter("albedo_tex", albedo);
        if (tryNormal && normal != null) { mat.SetShaderParameter("normal_tex", normal); mat.SetShaderParameter("use_normal", true); }
        mat.SetShaderParameter("rough", rough);
        mat.SetShaderParameter("wind", wind);
        mat.SetShaderParameter("quality", Vis.QInt);
        e.Mat = mat;

        root.QueueFree();
        _cache[key] = e;
        return e;
    }

    // rebuild the mesh with `xform` baked into positions (and its rotation into normals/tangents). Preserves UVs/indices/etc.
    private static ArrayMesh BakeNormalized(Mesh src, Transform3D xform)
    {
        var outMesh = new ArrayMesh();
        var basis = xform.Basis;
        for (int s = 0; s < src.GetSurfaceCount(); s++)
        {
            var arr = src.SurfaceGetArrays(s);
            var vv = arr[(int)Mesh.ArrayType.Vertex];
            if (vv.VariantType != Variant.Type.Nil)
            {
                var verts = vv.As<Vector3[]>();
                for (int i = 0; i < verts.Length; i++) verts[i] = xform * verts[i];
                arr[(int)Mesh.ArrayType.Vertex] = verts;
            }
            var nn = arr[(int)Mesh.ArrayType.Normal];
            if (nn.VariantType != Variant.Type.Nil)
            {
                var norms = nn.As<Vector3[]>();
                for (int i = 0; i < norms.Length; i++) norms[i] = (basis * norms[i]).Normalized();
                arr[(int)Mesh.ArrayType.Normal] = norms;
            }
            var prim = src is ArrayMesh am ? am.SurfaceGetPrimitiveType(s) : Mesh.PrimitiveType.Triangles;
            outMesh.AddSurfaceFromArrays(prim, arr);
        }
        return outMesh;
    }

    private static void FindMesh(Node n, Transform3D acc, ref MeshInstance3D found, ref Transform3D foundX)
    {
        if (found != null) return;
        var local = acc;
        if (n is Node3D n3) local = acc * n3.Transform;
        if (n is MeshInstance3D mi && mi.Mesh != null && mi.Mesh.GetSurfaceCount() > 0) { found = mi; foundX = local; return; }
        foreach (var c in n.GetChildren()) { FindMesh(c, local, ref found, ref foundX); if (found != null) return; }
    }

    // ---- scatter (MultiMesh) accessors ----------------------------------------------------------------
    public static Mesh GetMesh(string name) => Get(name).Mesh;
    public static ShaderMaterial Mat(string name) => Get(name).Mat;
    // normalised XZ half-extents (multiply by the world height you scale to → real footprint half-widths on X and Z)
    public static Vector2 NormExtents(string name) => Get(name).Ext;

    // ---- single-node accessor -------------------------------------------------------------------------
    // A ready MeshInstance3D standing `height` units tall, feet at the node origin (Position/Rotation free to set).
    // `seed` gives it a stable colour jitter.
    public static MeshInstance3D Instance(string name, float height, int seed = 0)
    {
        var e = Get(name);
        var node = new MeshInstance3D { Mesh = e.Mesh, MaterialOverride = e.Mat };
        node.Scale = new Vector3(height, height, height);   // baked mesh is unit-height → clean uniform scale, origin at feet
        if (seed != 0) node.SetInstanceShaderParameter("node_var", Vis.VaryColorSeeded(seed, 0.035f, 0.08f));
        return node;
    }

    // preset wind/roughness per known prop
    private static Entry Get(string name)
    {
        switch (name)
        {
            case "flower":    return Load(name, "props", 0.7f, 0.010f, false);
            case "mushroom":  return Load(name, "props", 0.85f, 0f, false);
            case "fern":      return Load(name, "props", 0.8f, 0.018f, false);
            case "reeds":     return Load(name, "props", 0.8f, 0.030f, false);
            case "pumpkin":   return Load(name, "props", 0.75f, 0f, false);
            case "leaf_a": case "leaf_b": case "leaf_c": return Load(name, "props", 0.7f, 0.02f, false, byMaxDim: true);   // single leaves — normalise by MAX dim (flat) so they don't blow up; a little wind flutter
            case "leafpile_a": case "leafpile_b":        return Load(name, "props", 0.8f, 0f, false, byMaxDim: true);
            case "ruin":      return Load(name, "structures", 0.95f, 0f, true);
            case "staircase": return Load(name, "structures", 0.95f, 0f, true);
            case "cottage_a": return Load(name, "structures", 0.9f, 0f, true);
            case "cottage_b": return Load(name, "structures", 0.9f, 0f, true);
            case "platform":  return Load(name, "structures", 0.95f, 0f, true);
            case "fort":      return Load(name, "structures", 0.95f, 0f, true);
            case "altar":     return Load(name, "structures", 0.9f, 0f, true);
            case "well":      return Load(name, "structures", 0.9f, 0f, true);
            case "gravestones": return Load(name, "structures", 0.95f, 0f, true);
            case "keep_climb": return Load(name, "structures", 0.95f, 0f, true);
            default:          return Load(name, "props", 0.85f, 0f, false);
        }
    }
}
