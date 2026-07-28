using Godot;
using System.Collections.Generic;

// One authored collider on a Meshy model. Stored in the model's LOCAL, unit-height space (model base at y=0, model normalized to
// height 1 by PropGlb), so the same template applies at ANY spawn scale/position/yaw. P = center, S = half-extents.
public class EditCol
{
    public string Shape = "box";    // "box" | "cyl"
    public string Kind = "solid";   // "solid" (red — blocks, NOT standable) | "walk" (blue — stand on top) | "ramp" (green — walk up)
    public Vector3 P;                                    // local center
    public Vector3 S = new Vector3(0.3f, 0.3f, 0.3f);   // local half-extents (cyl: X = radius, Y = half-height)
    public float Yaw;                                    // local Y-rotation (radians)
    public EditCol Clone() => new EditCol { Shape = Shape, Kind = Kind, P = P, S = S, Yaw = Yaw };
}

// Authored collider templates for the Meshy structures — placed in-engine with the collider editor (dev cmd `cedit`), shipped as
// res://data/colliders.json (checked into the repo), and consumed at spawn time by World.StructureModel/StairModel/ClimbableKeep
// so each model gets hand-authored colliders instead of the old heuristic ones.
public static class ColliderTemplates
{
    private const string Path = "res://data/colliders.json";
    public static Dictionary<string, List<EditCol>> Templates = new();
    private static bool _loaded;

    public static bool Has(string name) { EnsureLoaded(); return Templates.TryGetValue(name, out var l) && l.Count > 0; }

    public static void EnsureLoaded() { if (!_loaded) { Load(); _loaded = true; } }

    public static void Load()
    {
        Templates = new();
        _loaded = true;
        if (!FileAccess.FileExists(Path)) return;
        using var f = FileAccess.Open(Path, FileAccess.ModeFlags.Read);
        if (f == null) return;
        var parsed = Json.ParseString(f.GetAsText());
        if (parsed.VariantType != Variant.Type.Dictionary) return;
        var dict = parsed.AsGodotDictionary();
        foreach (var key in dict.Keys)
        {
            var list = new List<EditCol>();
            foreach (var item in dict[key].AsGodotArray())
            {
                var o = item.AsGodotDictionary();
                list.Add(new EditCol
                {
                    Shape = o["shape"].AsString(),
                    Kind = o["kind"].AsString(),
                    P = Arr3(o["p"]),
                    S = Arr3(o["s"]),
                    Yaw = (float)o["yaw"].AsDouble(),
                });
            }
            Templates[key.AsString()] = list;
        }
    }

    public static void Save(Dictionary<string, List<EditCol>> data)
    {
        Templates = data; _loaded = true;
        var root = new Godot.Collections.Dictionary();
        foreach (var kv in data)
        {
            if (kv.Value.Count == 0) continue;
            var arr = new Godot.Collections.Array();
            foreach (var e in kv.Value)
                arr.Add(new Godot.Collections.Dictionary
                {
                    { "shape", e.Shape }, { "kind", e.Kind },
                    { "p", V(e.P) }, { "s", V(e.S) }, { "yaw", e.Yaw },
                });
            root[kv.Key] = arr;
        }
        DirAccess.MakeDirRecursiveAbsolute("res://data");
        using var f = FileAccess.Open(Path, FileAccess.ModeFlags.Write);
        if (f != null) f.StoreString(Json.Stringify(root, "\t"));
    }

    private static Vector3 Arr3(Variant v) { var a = v.AsGodotArray(); return new Vector3((float)a[0].AsDouble(), (float)a[1].AsDouble(), (float)a[2].AsDouble()); }
    private static Godot.Collections.Array V(Vector3 p) => new Godot.Collections.Array { p.X, p.Y, p.Z };

    // Compile a model's authored colliders into the engine lists at spawn. center = model origin XZ, gy = model origin Y (the
    // sunk/embedded base), height = the world height it's scaled to, structYaw = the model's spawn rotation.
    // Returns true if a template existed (so the caller can skip its old heuristic colliders).
    public static bool Emit(string name, Vector3 center, float gy, float height, float structYaw,
                            List<Blocker> blockers, List<Deck> decks, List<Ramp> ramps)
    {
        EnsureLoaded();
        if (!Templates.TryGetValue(name, out var list) || list.Count == 0) return false;
        // rotate the local offset EXACTLY like the model (which is rotated via RotationDegrees.Y = structYaw). Using a Godot Basis
        // guarantees the same handedness — hand-rolled sin/cos had the opposite sign, so colliders swung the wrong way at any yaw.
        var rot = Basis.FromEuler(new Vector3(0, structYaw, 0));
        foreach (var e in list)
        {
            Vector3 off = rot * new Vector3(e.P.X * height, 0f, e.P.Z * height);
            float wx = center.X + off.X;
            float wz = center.Z + off.Z;
            float wy = gy + e.P.Y * height;
            float yaw = structYaw + e.Yaw;
            float hx = Mathf.Max(0.05f, e.S.X * height);
            float hy = Mathf.Max(0.05f, e.S.Y * height);
            float hz = Mathf.Max(0.05f, e.S.Z * height);
            if (e.Kind == "ramp")
                ramps.Add(new Ramp { Center = new Vector3(wx, wy, wz), Half = new Vector2(hx, hz), YLow = wy - hy, YHigh = wy + hy, AlongX = true, Yaw = yaw });
            else if (e.Shape == "cyl" && e.Kind == "solid")
                blockers.Add(new Blocker { Pos = new Vector3(wx, 0, wz), Radius = hx, Top = wy + hy });
            else
                decks.Add(new Deck { Center = new Vector3(wx, wy, wz), Half = new Vector2(hx, hz), TopY = wy + hy, Yaw = yaw, Solid = e.Kind == "solid", Cyl = e.Shape == "cyl", Boxed = true, BotY = wy - hy });
        }
        return true;
    }
}
