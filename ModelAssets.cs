using Godot;
using System.Collections.Generic;

// ModelAssets.cs — Phase 4B (authored-mesh) pipeline hook.
//
// The game is 100% procedural today (WitchModel/Creature build characters from primitives). This is the receiving end for
// AUTHORED meshes (Meshy- or Blender-made .glb): callers ask ModelAssets for a character by KEY; if an imported
// res://assets/models/<key>.glb exists it's instantiated, otherwise null is returned so the caller falls back to its
// procedural build. That lets us drop authored characters in ONE AT A TIME without breaking anything that has no asset yet.
//
// Convention (see assets/models/README.md): +Z forward, +Y up, ~real-world scale in metres, pivot at the FEET (origin on
// the ground between the feet), single skinned mesh with a Skeleton3D for characters. Keys are lowercase, e.g.
// "witch_lunar", "enemy_goblin".
public static class ModelAssets
{
    private const string Root = "res://assets/models/";
    // Authored characters may arrive as .glb (Meshy) OR .fbx (Mixamo-rigged) — Godot 4.7 imports both natively. Resolve
    // whichever exists for a given base path; .glb wins if both are present.
    private static readonly string[] Exts = { ".glb", ".fbx" };

    // First existing "<basePathNoExt><ext>" for our supported extensions, or null.
    private static string Resolve(string basePathNoExt)
    {
        foreach (var e in Exts) if (ResourceLoader.Exists(basePathNoExt + e)) return basePathNoExt + e;
        return null;
    }

    // Authored MetaTailor/Meshy witches live here — each a GLB with its own Textures/ folder beside it (paths resolve in place).
    private const string WitchDir = "MetaTailoredModels/Witches/";

    // Per-witch model file overrides (manual escape hatch — e.g. a non-conventional filename). Checked FIRST. Beyond these,
    // any <key>.glb DROPPED into WitchDir is auto-discovered (zero code per witch); then the flat res://assets/models/<key>.
    private static readonly System.Collections.Generic.Dictionary<string, string> Overrides = new()
    {
        { "witch_lunar", "MetaTailoredModels/Witches/LunarWitchRobed.glb" },   // robed (MetaTailor+Meshy), GLB, same Mixamo skeleton
    };

    // The override GLB for `key`, or null if the witch has no authored model. Resolution order:
    //   1. Overrides dict (manual escape hatch)
    //   2. per-witch SUBFOLDER  WitchDir/<key>/*.glb   ← RECOMMENDED: isolates each witch's Textures/ (no collisions)
    //   3. flat key-named file   WitchDir/<key>.glb     (fine for a single quick drop; shares one Textures/ folder)
    private static string OverridePath(string key)
    {
        if (Overrides.TryGetValue(key, out var rel) && ResourceLoader.Exists(Root + rel)) return Root + rel;
        string sub = Root + WitchDir + key + "/";
        var da = DirAccess.Open(sub);
        if (da != null)
            foreach (var f in da.GetFiles())
                if (f.ToLower().EndsWith(".glb")) return sub + f;
        string flat = Root + WitchDir + key + ".glb";
        return ResourceLoader.Exists(flat) ? flat : null;
    }

    private static string PathFor(string key) => OverridePath(key) ?? Resolve(Root + key);

    public static bool Has(string key) => PathFor(key) != null;

    // Instantiate the authored model for `key`, or null if none is imported yet (→ caller uses its procedural model).
    public static Node3D TryLoad(string key)
    {
        string path = PathFor(key);
        if (path == null) return null;
        var scene = ResourceLoader.Load<PackedScene>(path);
        return scene != null ? scene.Instantiate<Node3D>() : null;
    }

    // Scale the root so the character stands `targetH` metres tall, and return its detected native height. For RIGGED
    // characters we measure the SKELETON's bone span (its rest pose reflects true height); a skinned mesh's GetAabb() reads
    // only the tiny mesh-local bounds and misses the skeleton scaling — that gave a 100× overshoot on the first witch.
    public static float FitHeight(Node3D root, float targetH)
    {
        // Measure the VISIBLE MESH (AABB of every VisualInstance3D, composed from LOCAL transforms so it works even BEFORE
        // the model is in the tree). The rendered geometry is the source of truth for size — this is immune to rigs whose
        // bone rest-pose scale doesn't match the baked mesh scale (e.g. a MetaTailor ×100 export, where bone-span reads
        // ~human height but the mesh is 100× giant). Includes hat/hair tip, so "height" is true silhouette top-to-bottom.
        bool any = false; float lo = 0f, hi = 0f;
        Collect(root, Transform3D.Identity, ref any, ref lo, ref hi);
        float meshH = hi - lo;
        if (meshH > 0.0001f)
        {
            root.Scale = Vector3.One * (targetH / meshH);   // grounding is done separately (GroundToFeet, once in-tree) — skinned
            return meshH;                                   // GetAabb() isn't a reliable feet reference
        }
        // fallback (no visible mesh instanced yet): skeleton bone span
        var skel = FindSkeleton(root);
        if (skel != null && skel.GetBoneCount() > 0)
        {
            Transform3D toRoot = Transform3D.Identity;
            for (Node n = skel; n != null && n != root; n = n.GetParent())
                if (n is Node3D n3) toRoot = n3.Transform * toRoot;
            bool a = false; float l = 0f, h2 = 0f;
            for (int i = 0; i < skel.GetBoneCount(); i++)
            {
                float y = (toRoot * skel.GetBoneGlobalPose(i).Origin).Y;
                if (!a) { l = h2 = y; a = true; } else { l = Mathf.Min(l, y); h2 = Mathf.Max(h2, y); }
            }
            float boneSpan = h2 - l;
            if (boneSpan > 0.0001f)
            {
                float visual = boneSpan / 0.88f;   // bones span ~88% of visual height (head bone below the crown, foot above the sole)
                root.Scale = Vector3.One * (targetH / visual);
                return visual;
            }
        }
        return 0f;
    }

    // Plant the character on the ground: shift `modelRoot` vertically so its LOWEST bone (a foot/toe) sits at its PARENT's
    // origin (feet on the floor). Mixamo's armature carries a pivot/scale that fooled out-of-tree local math, so this uses the
    // skeleton's REAL world bone positions (skel.GlobalTransform * bone pose) — the same proven basis as FitHeight.
    // MUST be called IN-TREE and AFTER the parent's final position is set (it reads parent.GlobalPosition).
    public static void GroundToFeet(Node3D modelRoot)
    {
        if (modelRoot.GetParent() is not Node3D parent) return;
        var skel = FindSkeleton(modelRoot);
        if (skel == null || skel.GetBoneCount() == 0) return;
        Transform3D g = skel.GlobalTransform;
        bool any = false; float minY = 0f;
        for (int i = 0; i < skel.GetBoneCount(); i++)
        {
            // ANIMATED pose (GetBoneGlobalPose) — the borrowed idle/loco clip repositions the hips well below the rest pose, so
            // rest-based grounding leaves her sunk once the clip plays. MUST be called deferred (after the AnimationTree ticks)
            // or the pose isn't applied yet — GroundAuthored schedules that.
            float y = (g * skel.GetBoneGlobalPose(i).Origin).Y;   // true WORLD y of the bone in its current animated pose
            if (!any || y < minY) { minY = y; any = true; }
        }
        if (!any) return;
        // The parent (puppet, at the player's origin) rides the terrain every frame — line 1746 keeps player.Y = terrain — so
        // its Y IS the floor. Ground the lowest ANIMATED bone to it; the result is a constant local offset that stays correct
        // across hills (the player handles terrain-follow, this only fixes the mesh's origin→feet offset).
        float delta = parent.GlobalPosition.Y - minY;   // lift the lowest animated bone (a foot) onto the terrain (the parent's Y)
        modelRoot.Position += new Vector3(0f, delta, 0f);
    }

    // Overlay a fixed-world-size dot on every bone (via SkelViz, scale-proof) so the rig is visible through the mesh.
    // Returns a summary of the bone hierarchy for diagnosis.
    public static (string summary, SkelViz viz) ShowSkeleton(Node3D model)
    {
        var skel = FindSkeleton(model);
        if (skel == null) return ("no Skeleton3D found", null);
        var viz = new SkelViz();
        model.AddChild(viz);
        viz.Init(skel);
        viz.HideMesh(model);   // hide the robe/hat so only the bright numbered bones read
        var names = new List<string>();
        for (int i = 0; i < skel.GetBoneCount(); i++) names.Add($"{i}:{(string)skel.GetBoneName(i)} (parent {skel.GetBoneParent(i)})");
        return ($"{skel.GetBoneCount()} bones — {string.Join(", ", names)}", viz);
    }

    // For the first-person authored view: collapse everything that isn't the arms by zeroing those bones' REST scale (the
    // AnimationMixer resets to rest before applying tracks, so a zeroed rest scale sticks every frame without a per-frame
    // reapply). Bone scale trickles to children, so we scale the NON-arm branches — head/neck/hair (+ hat, skinned to them)
    // and the legs/feet — but NOT the spine/hips (the arms hang off the spine, so scaling it would collapse the arms too).
    // Per-instance: only this FP copy is affected, allies/tp keep the full body.
    public static void HideForFirstPerson(Node model)
    {
        var skel = FindSkeleton(model);
        if (skel == null) return;
        string[] hide = { "Head", "head", "Neck", "neck", "Hair", "hair", "UpLeg", "Leg", "Foot", "Toe" };
        for (int i = 0; i < skel.GetBoneCount(); i++)
        {
            string n = (string)skel.GetBoneName(i);
            bool kill = false;
            foreach (var k in hide) if (n.Contains(k)) { kill = true; break; }
            if (!kill) continue;
            var rest = skel.GetBoneRest(i);
            rest.Basis = rest.Basis.Scaled(new Vector3(0.001f, 0.001f, 0.001f));
            skel.SetBoneRest(i, rest);
        }
        skel.ResetBonePoses();
    }

    public static bool IsOverride(string key) => OverridePath(key) != null;

    // glTF keeps Mixamo bone names with a COLON (mixamorig:Hips), but our borrowed animation clips (from the FBX magic packs)
    // address bones with an UNDERSCORE (mixamorig_Hips). If they don't match, no track resolves and the mesh stays in its rest
    // (T-)pose. Rename colon→underscore so the borrowed tracks bind. Safe for skinning: glTF skins bind by joint INDEX, not name.
    public static void NormalizeBoneNames(Node root)
    {
        var skel = FindSkeleton(root);
        if (skel == null) return;
        for (int i = 0; i < skel.GetBoneCount(); i++)
        {
            string n = (string)skel.GetBoneName(i);
            if (n.Contains(':')) skel.SetBoneName(i, n.Replace(':', '_'));
        }
    }

    // Per-witch material pass for an authored override model. The GLB's OWN embedded textures are trusted (correct body + garment
    // maps — glTF binds them per-mesh), so we DON'T re-route by guessed mesh role. We only swap a hand-darkened skin map
    // ("Textures/body_dark.png", from the `skindark` dev command) onto the BODY mesh(es) when one exists — raw Meshy skin reads
    // too light in-engine. Garment/other meshes keep their embedded textures untouched.
    public static void RebindOverrideTextures(Node root, string key)
    {
        string glb = OverridePath(key);
        if (glb == null) return;
        string darkPath = glb.GetBaseDir() + "/Textures/body_dark.png";
        if (!ResourceLoader.Exists(darkPath)) return;                 // no darkened skin authored → embedded textures are already right
        var dark = ResourceLoader.Load<Texture2D>(darkPath);
        if (dark != null) ApplyBodyDark(root, dark);
    }

    // Words that mark a mesh as NON-SKIN (garment/headwear/accessory) → excluded from skin-darkening, which must only touch the
    // character body. Covers separate hat/gown/etc. meshes (some witches export the hat/gown as their own mesh, e.g. Divine).
    private static readonly string[] NonSkinWords = {
        "gown", "dress", "robe", "skirt", "cloth", "cloak", "cape", "tunic", "garment", "outfit", "attire", "corset", "apron",
        "hat", "cap", "hood", "crown", "veil", "mask", "boot", "shoe", "glove", "staff", "wand", "wing", "cane", "scarf" };
    private static bool IsGarmentMesh(MeshInstance3D mi)
    {
        string n = (mi.Name.ToString() + " " + (mi.Mesh?.ResourceName ?? "")).ToLower();
        foreach (var w in NonSkinWords) if (n.Contains(w)) return true;
        return false;
    }

    private static Shader _clothShader;

    // Give an override witch's GARMENT meshes the cloth-sway material (keeps their painted albedo, adds vertex flow). Collects
    // the created ShaderMaterials into `outMats` so WitchModel can drive their motion uniforms each frame.
    public static void ApplyClothSway(Node root, System.Collections.Generic.List<ShaderMaterial> outMats)
    {
        if (_clothShader == null) _clothShader = ResourceLoader.Load<Shader>("res://shaders/cloth_sway.gdshader");
        if (_clothShader == null) return;
        ApplyClothRec(root, outMats);
    }

    private static void ApplyClothRec(Node node, System.Collections.Generic.List<ShaderMaterial> outMats)
    {
        if (node is MeshInstance3D mi && mi.Mesh != null && IsGarmentMesh(mi))
        {
            var aabb = mi.GetAabb();
            float top = aabb.Position.Y + aabb.Size.Y, bottom = aabb.Position.Y;
            for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
            {
                Texture2D tex = (mi.GetActiveMaterial(s) as StandardMaterial3D)?.AlbedoTexture;
                var sm = new ShaderMaterial { Shader = _clothShader };
                if (tex != null) sm.SetShaderParameter("albedo_tex", tex);
                sm.SetShaderParameter("sway_top", top);
                sm.SetShaderParameter("sway_bottom", bottom);
                mi.SetSurfaceOverrideMaterial(s, sm);
                outMats.Add(sm);
            }
        }
        foreach (var c in node.GetChildren()) ApplyClothRec(c, outMats);
    }

    private static void ApplyBodyDark(Node node, Texture2D dark)
    {
        if (node is MeshInstance3D mi && mi.Mesh != null && !IsGarmentMesh(mi))
            for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
            {
                var cur = mi.GetActiveMaterial(s) as StandardMaterial3D;
                var m = cur != null ? (StandardMaterial3D)cur.Duplicate() : new StandardMaterial3D();
                m.AlbedoTexture = dark;
                m.AlbedoColor = Colors.White;
                m.Metallic = 0f; m.Roughness = 0.9f;
                m.Transparency = BaseMaterial3D.TransparencyEnum.Disabled;
                m.CullMode = BaseMaterial3D.CullModeEnum.Back;
                mi.SetSurfaceOverrideMaterial(s, m);
            }
        foreach (var c in node.GetChildren()) ApplyBodyDark(c, dark);
    }

    // Bake a darkened skin map for `key`'s authored model: multiply the skin-tone pixels of its body texture by `factor` and
    // write Textures/body_dark.png (the loader then applies it to the body mesh). Non-skin pixels (white gown/hair/hat) untouched.
    // After baking, focus the editor to reimport the PNG, then re-enter tp3. Returns a status line for the dev console.
    public static string BakeBodyDark(string key, float factor)
    {
        string glb = OverridePath(key);
        if (glb == null) return $"no authored model for {key} (drop {key}.glb in {WitchDir}).";
        string dir = glb.GetBaseDir() + "/Textures/";
        string src = null;
        var da = DirAccess.Open(dir);
        if (da != null)
            foreach (var f in da.GetFiles())
            {
                string lf = f.ToLower();
                if (!lf.EndsWith(".png")) continue;
                // skip non-albedo maps AND non-skin meshes' textures (hat/gown/etc.) so we land on the character BODY albedo
                if (lf.Contains("base_color") || lf.Contains("body_dark") || lf.Contains("normal") || lf.Contains("rough")
                    || lf.Contains("metal") || lf.Contains("orm") || lf.Contains("emiss") || lf.Contains("_ao")) continue;
                bool nonSkin = false; foreach (var nw in NonSkinWords) if (lf.Contains(nw)) { nonSkin = true; break; }
                if (nonSkin) continue;
                src = f; break;   // the Meshy character (body) texture
            }
        if (src == null) return $"no body texture found in {dir}";
        var img = Image.LoadFromFile(ProjectSettings.GlobalizePath(dir + src));
        if (img == null) return $"failed to load {src}";
        if (img.IsCompressed()) img.Decompress();
        img.Convert(Image.Format.Rgba8);
        int w = img.GetWidth(), h = img.GetHeight(); long n = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var c = img.GetPixel(x, y);
                float mx = Mathf.Max(c.R, Mathf.Max(c.G, c.B)), mn = Mathf.Min(c.R, Mathf.Min(c.G, c.B));
                // skin heuristic: warm (R≥G≥B), moderately saturated, mid-brightness — spares white gown/hair and black shadows
                if (c.R >= c.G && c.G >= c.B && (c.R - c.B) >= 0.07f && mx < 0.96f && mx > 0.22f && (mx - mn) >= 0.06f)
                {
                    img.SetPixel(x, y, new Color(c.R * factor, c.G * factor, c.B * factor, c.A));
                    n++;
                }
            }
        img.SavePng(ProjectSettings.GlobalizePath(dir + "body_dark.png"));
        return $"baked body_dark.png (×{factor:0.00}, {100L * n / ((long)w * h)}% pixels) from {src} — focus the editor to reimport, then re-enter tp3.";
    }

    public static Skeleton3D FindSkeleton(Node n)
    {
        if (n is Skeleton3D s) return s;
        foreach (var c in n.GetChildren()) { var r = FindSkeleton(c); if (r != null) return r; }
        return null;
    }

    private static void Collect(Node node, Transform3D acc, ref bool any, ref float lo, ref float hi)
    {
        if (node is VisualInstance3D vi)
        {
            var box = vi.GetAabb();
            for (int i = 0; i < 8; i++)
            {
                var corner = box.Position + new Vector3((i & 1) != 0 ? box.Size.X : 0f, (i & 2) != 0 ? box.Size.Y : 0f, (i & 4) != 0 ? box.Size.Z : 0f);
                float y = (acc * corner).Y;
                if (!any) { lo = hi = y; any = true; } else { lo = Mathf.Min(lo, y); hi = Mathf.Max(hi, y); }
            }
        }
        foreach (var c in node.GetChildren())
        {
            var t = c is Node3D n3 ? n3.Transform : Transform3D.Identity;
            Collect(c, acc * t, ref any, ref lo, ref hi);
        }
    }

    // De-ghost an imported Meshy/glTF character: force its materials OPAQUE + MATTE so it reads as a solid painted
    // character instead of translucent glossy glass. Keeps the baked albedo texture (the character's actual colours).
    public static void Painterlify(Node node)
    {
        if (node is MeshInstance3D mi && mi.Mesh != null)
        {
            for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
            {
                if (mi.GetActiveMaterial(s) is StandardMaterial3D sm)
                {
                    sm.Transparency = BaseMaterial3D.TransparencyEnum.Disabled;   // solid, not see-through
                    sm.Metallic = 0f;
                    sm.MetallicSpecular = 0.3f;
                    sm.Roughness = 0.9f;                                          // matte, kills the glass sheen
                    sm.EmissionEnabled = false;
                    sm.RimEnabled = false;
                    sm.ClearcoatEnabled = false;
                    sm.CullMode = BaseMaterial3D.CullModeEnum.Back;
                }
            }
        }
        foreach (var c in node.GetChildren()) Painterlify(c);
    }

    // Mixamo re-exports frequently DROP the source texture (the FBX comes in as a flat white mesh). If an "<key>_albedo.png"
    // sits next to the model, assign it to any surface whose material has no albedo texture, so the character keeps her paint.
    public static void ApplyFallbackAlbedo(Node node, string key)
    {
        string texPath = Root + key + "_albedo.png";
        if (!ResourceLoader.Exists(texPath)) return;
        var tex = ResourceLoader.Load<Texture2D>(texPath);
        if (tex == null) return;
        ApplyAlbedoRec(node, tex);
    }

    private static void ApplyAlbedoRec(Node node, Texture2D tex)
    {
        if (node is MeshInstance3D mi && mi.Mesh != null)
        {
            for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
            {
                var mat = mi.GetActiveMaterial(s);
                if (mat is StandardMaterial3D sm && sm.AlbedoTexture == null)
                {
                    var m = (StandardMaterial3D)sm.Duplicate();   // clone so we don't scribble on a shared imported material
                    m.AlbedoTexture = tex;
                    mi.SetSurfaceOverrideMaterial(s, m);
                }
                else if (mat == null)   // surface came in with NO material (renders white) → give it one with the texture
                    mi.SetSurfaceOverrideMaterial(s, new StandardMaterial3D { AlbedoTexture = tex, Metallic = 0f, Roughness = 0.9f });
            }
        }
        foreach (var c in node.GetChildren()) ApplyAlbedoRec(c, tex);
    }

    public static AnimationPlayer FindAnimPlayer(Node n)
    {
        if (n is AnimationPlayer ap) return ap;
        foreach (var c in n.GetChildren()) { var r = FindAnimPlayer(c); if (r != null) return r; }
        return null;
    }

    private static List<string> ListAnims(AnimationPlayer ap)
    {
        var names = new List<string>();
        foreach (StringName lib in ap.GetAnimationLibraryList())
            foreach (StringName a in ap.GetAnimationLibrary(lib).GetAnimationList())
                names.Add(((string)lib).Length == 0 ? (string)a : $"{lib}/{a}");
        return names;
    }

    // Freeze the Hips horizontal (X/Z) translation in every clip while keeping vertical (Y = standing height + walk bob), so
    // locomotion plays IN PLACE — no root-motion drift/teleport, and no ground-sink (which full root-motion extraction caused).
    // We drive world position from gameplay, so the clip must not translate the body horizontally.
    public static void StripHorizontalDrift(AnimationPlayer ap)
    {
        foreach (StringName lib in ap.GetAnimationLibraryList())
        {
            var l = ap.GetAnimationLibrary(lib);
            foreach (StringName a in l.GetAnimationList())
            {
                var anim = l.GetAnimation(a);
                for (int i = 0; i < anim.GetTrackCount(); i++)
                {
                    if (anim.TrackGetType(i) != Animation.TrackType.Position3D) continue;
                    if (!anim.TrackGetPath(i).ToString().Contains("Hips")) continue;
                    int kc = anim.TrackGetKeyCount(i);
                    if (kc == 0) continue;
                    var first = (Vector3)anim.TrackGetKeyValue(i, 0);
                    for (int k = 0; k < kc; k++)
                    {
                        var v = (Vector3)anim.TrackGetKeyValue(i, k);
                        v.X = first.X; v.Z = first.Z;   // hold horizontal at the clip's start; keep Y
                        anim.TrackSetKeyValue(i, k, v);
                    }
                }
            }
        }
    }

    private static void LoopAll(AnimationPlayer ap)
    {
        foreach (StringName lib in ap.GetAnimationLibraryList())
        {
            var l = ap.GetAnimationLibrary(lib);
            foreach (StringName a in l.GetAnimationList())
                l.GetAnimation(a).LoopMode = Animation.LoopModeEnum.Linear;
        }
    }

    // add the animation(s) from an anim-glb into `ap` under library `libName` (bypasses the base glb's static clip0).
    // Returns the play-key of the first animation, or null. The tracks resolve against ap's existing RootNode — fine because
    // all clips come from the SAME Meshy character (identical skeleton path + bone names).
    private static string BorrowLib(AnimationPlayer ap, string glbPath, string libName)
    {
        if (glbPath == null || !ResourceLoader.Exists(glbPath)) return null;
        var inst = ResourceLoader.Load<PackedScene>(glbPath).Instantiate();
        var src = FindAnimPlayer(inst);
        string first = null;
        if (src != null)
        {
            var lib = new AnimationLibrary();
            foreach (StringName sl in src.GetAnimationLibraryList())
            {
                var l = src.GetAnimationLibrary(sl);
                foreach (StringName a in l.GetAnimationList())
                {
                    lib.AddAnimation(a, l.GetAnimation(a));
                    if (first == null) first = $"{libName}/{a}";
                }
            }
            if (ap.HasAnimationLibrary(libName)) ap.RemoveAnimationLibrary(libName);
            ap.AddAnimationLibrary(libName, lib);
        }
        inst.QueueFree();
        return first;
    }

    // Borrow the library-quality idle/walk/run clips (assets/models/witches/<key>_{idle,walk,run}.glb) onto the character's
    // AnimationPlayer under libs "idle"/"walk"/"run". Returns the player + each clip's play-key (null if that glb is absent).
    public static (AnimationPlayer ap, string idle, string walk, string run) SetupLocomotion(Node3D model, string key)
    {
        var ap = FindAnimPlayer(model);
        if (ap == null) { ap = new AnimationPlayer(); model.AddChild(ap); ap.RootNode = ap.GetPathTo(model); }
        string idle = BorrowLib(ap, Resolve(Root + "witches/" + key + "_idle"), "idle");
        string walk = BorrowLib(ap, Resolve(Root + "witches/" + key + "_walk"), "walk");
        string run = BorrowLib(ap, Resolve(Root + "witches/" + key + "_run"), "run");
        LoopAll(ap);
        return (ap, idle, walk, run);
    }

    // 8-way directional locomotion clips + idle (Mixamo "Magic Locomotion" pack, shared across witches — same mixamorig
    // skeleton). Borrowed from assets/models/witches/anims/locomotion/. Any missing clip comes back null (caller falls back).
    public class DirLoco { public AnimationPlayer Ap; public string Idle, WF, WB, WL, WR, RF, RB, RL, RR; }

    public static DirLoco SetupDirectionalLocomotion(Node3D model)
    {
        var ap = FindAnimPlayer(model);
        if (ap == null) { ap = new AnimationPlayer(); model.AddChild(ap); ap.RootNode = ap.GetPathTo(model); }
        string d = Root + "witches/anims/locomotion/";
        var r = new DirLoco { Ap = ap };
        r.Idle = BorrowLib(ap, d + "standing idle.fbx", "idle");
        r.WF = BorrowLib(ap, d + "Standing Walk Forward.fbx", "wF");
        r.WB = BorrowLib(ap, d + "Standing Walk Back.fbx", "wB");
        r.WL = BorrowLib(ap, d + "Standing Walk Left.fbx", "wL");
        r.WR = BorrowLib(ap, d + "Standing Walk Right.fbx", "wR");
        r.RF = BorrowLib(ap, d + "Standing Run Forward.fbx", "rF");
        r.RB = BorrowLib(ap, d + "Standing Run Back.fbx", "rB");
        r.RL = BorrowLib(ap, d + "Standing Run Left.fbx", "rL");
        r.RR = BorrowLib(ap, d + "Standing Run Right.fbx", "rR");
        LoopAll(ap);
        return r;
    }

    // (DEV anim viewer) Borrow a set of clips into `ap` (looped, in-place) for browsing. Returns (playKey, displayName) per
    // clip. Play a key directly on the AnimationPlayer with the character's AnimationTree disabled.
    public static System.Collections.Generic.List<(string key, string name)> LoadViewerAnims(AnimationPlayer ap, string[] files)
    {
        var list = new System.Collections.Generic.List<(string, string)>();
        for (int i = 0; i < files.Length; i++)
        {
            string key = BorrowLib(ap, Root + files[i], $"view{i}");
            if (key == null) continue;
            var a = ap.GetAnimation(key);
            if (a != null) a.LoopMode = Animation.LoopModeEnum.Linear;   // loop for viewing
            list.Add((key, System.IO.Path.GetFileNameWithoutExtension(files[i])));
        }
        StripHorizontalDrift(ap);   // keep her in place (some attacks step)
        return list;
    }

    // Build the character AnimationTree:
    //   loco (BlendSpace2D: idle@origin, walk@unit ring, run@×2 ring) → speed (TimeScale) → cast (OneShot, UPPER-BODY filtered)
    // The OneShot layers a cast clip over locomotion but only on spine/arms/head/fingers (filter derived from the cast clip's
    // OWN track paths, so bone names match exactly) → she casts with her arms while her legs keep walking.
    // Param paths: "parameters/speed/scale" (float), "parameters/loco/blend_position" (Vector2 x=right,y=forward),
    // "parameters/cast/request" (fire the OneShot). Returns null if locomotion clips are missing.
    // CRITICAL: the tree resolves bone tracks against its OWN RootNode — mirror the AnimationPlayer's root or it T-poses.
    public static AnimationTree BuildLocomotionTree(Node3D model, DirLoco d, string leftCastFile = null, string chargeFile = null, string releaseFile = null, string jumpFile = null, string jumpRunFile = null)
    {
        if (d.Ap == null || d.Idle == null) return null;
        // Filter paths (cast mask / jump) must be built from the SKELETON's real path — the same path RetargetBoneTracks rewrites
        // the clip tracks to. Deriving them from a clip's own track paths breaks on GLB witches (retarget changes those paths
        // AFTER the filters are set → stale filter → the layer forces to zero → cast mask / jump silently do nothing).
        var fSkel = FindSkeleton(model);
        Node fApRoot = d.Ap.GetNodeOrNull(d.Ap.RootNode);
        string fSkelPath = (fApRoot != null && fSkel != null) ? fApRoot.GetPathTo(fSkel).ToString() : "Skeleton3D";
        var bs = new AnimationNodeBlendSpace2D { MinSpace = new Vector2(-2f, -2f), MaxSpace = new Vector2(2f, 2f), Snap = new Vector2(0.15f, 0.15f) };
        int bpIdx = 0;
        void P(string clip, Vector2 pos) { if (clip != null) { var a = new AnimationNodeAnimation(); a.Animation = clip; bs.AddBlendPoint(a, pos, -1, $"bp{bpIdx++}"); } }
        P(d.Idle, Vector2.Zero);
        P(d.WF, new Vector2(0f, 1f)); P(d.WB, new Vector2(0f, -1f)); P(d.WL, new Vector2(-1f, 0f)); P(d.WR, new Vector2(1f, 0f));
        P(d.RF, new Vector2(0f, 2f)); P(d.RB, new Vector2(0f, -2f)); P(d.RL, new Vector2(-2f, 0f)); P(d.RR, new Vector2(2f, 0f));

        StripHorizontalDrift(d.Ap);   // freeze Hips X/Z in every clip (keep Y) → in-place locomotion, no ground-sink, no drift

        var blend = new AnimationNodeBlendTree();
        blend.AddNode("loco", bs, new Vector2(0f, 160f));
        blend.AddNode("speed", new AnimationNodeTimeScale(), new Vector2(240f, 160f));
        blend.ConnectNode("speed", 0, "loco");
        string prev = "speed";   // node feeding the next upper-body layer

        // borrow the cast clips; LEFT primaries are right-handed clips MIRRORED to the left hand
        string chargeKey = chargeFile != null ? BorrowLib(d.Ap, Root + chargeFile, "chg") : null;
        string releaseKey = releaseFile != null ? BorrowLib(d.Ap, Root + releaseFile, "rel") : null;

        var chargeAnim = chargeKey != null ? d.Ap.GetAnimation(chargeKey) : null;
        var releaseAnim = releaseKey != null ? d.Ap.GetAnimation(releaseKey) : null;
        if (releaseAnim != null) releaseAnim.LoopMode = Animation.LoopModeEnum.None;
        // upper-body cast mask — built from the SKELETON (matches the retargeted clip tracks, unlike clip-derived paths)
        void Filter(AnimationNode n) { foreach (var p in BoneFilterPaths(fSkel, fSkelPath, IsUpperBody)) n.SetFilterPath(p, true); }

        // (natural idle at rest — no always-on ready pose; the charge/left-fire/release layers bring the arms up on demand)
        // (2) CHARGE: both-hands gather pose, blended in by ChargeAmt (0→1) — holds while right-click is held
        if (chargeAnim != null)
        {
            var cNode = new AnimationNodeAnimation { Animation = BakeReadyPose(d.Ap, chargeKey, 0.55f, "chargepose") };
            var charge = new AnimationNodeBlend2 { FilterEnabled = true };
            blend.AddNode("chargepose", cNode); blend.AddNode("charge", charge);
            blend.ConnectNode("charge", 0, prev); blend.ConnectNode("charge", 1, "chargepose");
            Filter(charge); prev = "charge";
        }
        // (3) LEFT FIRE: a hold-blend into the mirrored 1H attack's ARM-EXTENDED frame. Player ramps its blend fast on
        //     LMB-press (snappy thrust) and holds at 1 while LMB is held (rapid fire = arm stays extended), then ramps back
        //     to 0 on release (recovery). Param: "parameters/leftfire/blend_amount".
        if (leftCastFile != null)
        {
            string src = BorrowLib(d.Ap, Root + leftCastFile, "castLsrc");
            string mir = src != null ? BakeMirror(d.Ap, src, "castLmir") : null;
            if (mir != null)
            {
                var pNode = new AnimationNodeAnimation { Animation = BakeReadyPose(d.Ap, mir, 0.62f, "leftpose") };   // extended frame
                var lf = new AnimationNodeBlend2 { FilterEnabled = true };
                blend.AddNode("leftpose", pNode); blend.AddNode("leftfire", lf);
                blend.ConnectNode("leftfire", 0, prev); blend.ConnectNode("leftfire", 1, "leftpose");
                Filter(lf); prev = "leftfire";
            }
        }
        // (4) RELEASE: 2H thrust OneShot, fired on charge release — sped up ~3× (via TimeScale) so it snaps out with the
        //     projectile instead of dragging on after it, and fades out quickly.
        if (releaseAnim != null)
        {
            var relClip = new AnimationNodeAnimation { Animation = releaseKey };
            var relSpeed = new AnimationNodeTimeScale();
            var release = new AnimationNodeOneShot { FadeInTime = 0.04, FadeOutTime = 0.12, FilterEnabled = true };
            blend.AddNode("releaseclip", relClip); blend.AddNode("relspeed", relSpeed); blend.AddNode("release", release);
            blend.ConnectNode("relspeed", 0, "releaseclip");   // relspeed ← clip (3× faster)
            blend.ConnectNode("release", 0, prev);             // release.in   ← base
            blend.ConnectNode("release", 1, "relspeed");       // release.shot ← sped-up clip
            Filter(release); prev = "release";
        }

        // (5) JUMP: a WHOLE-BODY held falling pose (baked mid-jump) blended in while airborne — freezes her mid-fall. Two
        //     variants (still vs running takeoff) picked by "parameters/jumpsel/blend_amount"; airborne via "parameters/jump/blend_amount".
        if (jumpFile != null)
        {
            string jk = BorrowLib(d.Ap, Root + jumpFile, "jmp");
            string jrk = jumpRunFile != null ? BorrowLib(d.Ap, Root + jumpRunFile, "jmpr") : null;
            if (jk != null)
            {
                // use the ACTUAL clips (scrubbed by seek), not a static frame, so the jump PLAYS toward ~85% over the air time
                string jrmir = jrk != null ? BakeMirror(d.Ap, jrk, "jumprunmir") : null;   // mirror the run-jump clip (L/R variety)
                var ja = d.Ap.GetAnimation(jk); if (ja != null) ja.LoopMode = Animation.LoopModeEnum.None;
                var jra = jrk != null ? d.Ap.GetAnimation(jrk) : null; if (jra != null) jra.LoopMode = Animation.LoopModeEnum.None;
                var jma = jrmir != null ? d.Ap.GetAnimation(jrmir) : null; if (jma != null) jma.LoopMode = Animation.LoopModeEnum.None;
                // run(0) ↔ run-mirror(1) ("parameters/runjmpsel/blend_amount")
                blend.AddNode("jumprunpose", new AnimationNodeAnimation { Animation = jrk ?? jk });
                blend.AddNode("jumprunmir", new AnimationNodeAnimation { Animation = jrmir ?? jrk ?? jk });
                blend.AddNode("runjmpsel", new AnimationNodeBlend2());
                blend.ConnectNode("runjmpsel", 0, "jumprunpose"); blend.ConnectNode("runjmpsel", 1, "jumprunmir");
                // still(0) ↔ running(1) ("parameters/jumpsel/blend_amount")
                blend.AddNode("jumppose", new AnimationNodeAnimation { Animation = jk });
                blend.AddNode("jumpsel", new AnimationNodeBlend2());
                blend.ConnectNode("jumpsel", 0, "jumppose"); blend.ConnectNode("jumpsel", 1, "runjmpsel");
                // TimeSeek: the player scrubs the clip time via "parameters/jumpseek/seek_request" (played to ~85%, then held)
                blend.AddNode("jumpseek", new AnimationNodeTimeSeek());
                blend.ConnectNode("jumpseek", 0, "jumpsel");
                // LEGS-ONLY override (filter = leg bones) so the arms/spine/hands stay free to cast in the air; hips translation
                // unfiltered → stays at the locomotion standing height (no sinking).
                var jump = new AnimationNodeBlend2 { FilterEnabled = true };
                blend.AddNode("jump", jump);
                blend.ConnectNode("jump", 0, prev); blend.ConnectNode("jump", 1, "jumpseek");
                foreach (var p in BoneFilterPaths(fSkel, fSkelPath, IsJumpBody)) jump.SetFilterPath(p, true);   // legs+hips+spine (skeleton-derived)
                prev = "jump";
            }
        }
        blend.ConnectNode("output", 0, prev);

        var tree = new AnimationTree();
        model.AddChild(tree);
        tree.TreeRoot = blend;
        tree.AnimPlayer = tree.GetPathTo(d.Ap);
        Node apRoot = d.Ap.GetNodeOrNull(d.Ap.RootNode);          // resolve tracks against the SAME node the player uses
        if (apRoot != null) tree.RootNode = tree.GetPathTo(apRoot);
        RetargetBoneTracks(d.Ap, apRoot ?? model, FindSkeleton(model));   // repath borrowed "Skeleton3D:bone" tracks to THIS model's real skeleton
        tree.Active = true;
        if (releaseAnim != null) tree.Set("parameters/relspeed/scale", 3.75f);   // release plays 3.75× — snappy, matches the projectile
        return tree;
    }

    // Clips borrowed from the FBX magic packs path their bone tracks as "Skeleton3D:bone" — assuming a skeleton node named
    // exactly "Skeleton3D" sitting where these clips' source had it. A glTF import can name/nest the skeleton differently, so
    // those paths don't resolve and the mesh stays in T-pose. Rewrite every real-bone track's NODE part to THIS model's actual
    // skeleton path. Only clips that need it are touched, and each is DUPLICATED first so the shared source resource (reused by
    // other witches whose skeleton IS "Skeleton3D") is never mutated.
    private static void RetargetBoneTracks(AnimationPlayer ap, Node apRoot, Skeleton3D skel)
    {
        if (ap == null || apRoot == null || skel == null) return;
        string skelPath = apRoot.GetPathTo(skel);
        foreach (StringName libName in ap.GetAnimationLibraryList())
        {
            var lib = ap.GetAnimationLibrary(libName);
            foreach (StringName animName in lib.GetAnimationList())
            {
                var anim = lib.GetAnimation(animName);
                bool needs = false;
                for (int t = 0; t < anim.GetTrackCount(); t++)
                {
                    string path = anim.TrackGetPath(t).ToString();
                    int c = path.IndexOf(':'); if (c < 0) continue;
                    string bone = path.Substring(c + 1);
                    if (skel.FindBone(bone) >= 0 && path != skelPath + ":" + bone) { needs = true; break; }
                }
                if (!needs) continue;
                var dup = (Animation)anim.Duplicate();
                for (int t = 0; t < dup.GetTrackCount(); t++)
                {
                    string path = dup.TrackGetPath(t).ToString();
                    int c = path.IndexOf(':'); if (c < 0) continue;
                    string bone = path.Substring(c + 1);
                    if (skel.FindBone(bone) >= 0) dup.TrackSetPath(t, skelPath + ":" + bone);
                }
                lib.RemoveAnimation(animName);
                lib.AddAnimation(animName, dup);
            }
        }
    }

    private static System.Collections.Generic.IEnumerable<NodePath> UpperBodyTracks(Animation anim)
    {
        for (int i = 0; i < anim.GetTrackCount(); i++)
        {
            var p = anim.TrackGetPath(i);
            if (IsUpperBody(p.ToString())) yield return p;
        }
    }

    // Bake a static upper-body pose: sample the UPPER-BODY bone tracks of `srcKey` at `frac` of its length into a 1-key
    // looping clip (held over locomotion). `libName` names the library it's stored under. Returns the new clip's key.
    private static string BakeReadyPose(AnimationPlayer ap, string srcKey, float frac, string libName, bool upperOnly = true)
    {
        var cast = ap.GetAnimation(srcKey);
        if (cast == null) return null;
        float t = (float)cast.Length * Mathf.Clamp(frac, 0f, 1f);
        var ready = new Animation { Length = 0.2f, LoopMode = Animation.LoopModeEnum.Linear };
        for (int i = 0; i < cast.GetTrackCount(); i++)
        {
            var path = cast.TrackGetPath(i);
            var type = cast.TrackGetType(i);
            if (type != Animation.TrackType.Rotation3D && type != Animation.TrackType.Position3D && type != Animation.TrackType.Scale3D) continue;
            string ps = path.ToString();
            if (upperOnly) { if (!IsUpperBody(ps)) continue; }                                     // arms/spine only
            else if (type == Animation.TrackType.Position3D && ps.Contains("Hips")) continue;      // full body, but keep hips translation neutral
            int kc = cast.TrackGetKeyCount(i);
            if (kc == 0) continue;
            int nearest = 0; float best = 1e30f;
            for (int k = 0; k < kc; k++) { float dtk = Mathf.Abs((float)cast.TrackGetKeyTime(i, k) - t); if (dtk < best) { best = dtk; nearest = k; } }
            var val = cast.TrackGetKeyValue(i, nearest);
            int nt = ready.AddTrack(type);
            ready.TrackSetPath(nt, path);
            ready.TrackInsertKey(nt, 0.0, val);
        }
        var lib = new AnimationLibrary();
        lib.AddAnimation("pose", ready);
        if (ap.HasAnimationLibrary(libName)) ap.RemoveAnimationLibrary(libName);
        ap.AddAnimationLibrary(libName, lib);
        return libName + "/pose";
    }

    // Bake a LEFT-RIGHT MIRROR of a clip (a right-handed cast → left-handed): swap Left/Right bone tracks and reflect across
    // the sagittal plane (position X negated; rotation quaternion (x,-y,-z,w)). This is the "quick" local-space mirror — good
    // for roughly-symmetric Mixamo rigs; if joints look twisted, the axis negation is what to adjust. Returns the clip key.
    private static string BakeMirror(AnimationPlayer ap, string srcKey, string libName)
    {
        var src = ap.GetAnimation(srcKey);
        if (src == null) return null;
        var m = new Animation { Length = src.Length, LoopMode = src.LoopMode };
        for (int i = 0; i < src.GetTrackCount(); i++)
        {
            var type = src.TrackGetType(i);
            if (type != Animation.TrackType.Position3D && type != Animation.TrackType.Rotation3D && type != Animation.TrackType.Scale3D) continue;
            string path = src.TrackGetPath(i).ToString();
            string np = path.Contains("Left") ? path.Replace("Left", "Right") : path.Contains("Right") ? path.Replace("Right", "Left") : path;
            int nt = m.AddTrack(type);
            m.TrackSetPath(nt, np);
            int kc = src.TrackGetKeyCount(i);
            for (int k = 0; k < kc; k++)
            {
                double tk = src.TrackGetKeyTime(i, k);
                var val = src.TrackGetKeyValue(i, k);
                if (type == Animation.TrackType.Position3D) { var v = (Vector3)val; v.X = -v.X; val = v; }
                else if (type == Animation.TrackType.Rotation3D) { var q = (Quaternion)val; val = new Quaternion(q.X, -q.Y, -q.Z, q.W); }
                m.TrackInsertKey(nt, tk, val);
            }
        }
        var lib = new AnimationLibrary();
        lib.AddAnimation("clip", m);
        if (ap.HasAnimationLibrary(libName)) ap.RemoveAnimationLibrary(libName);
        ap.AddAnimationLibrary(libName, lib);
        return libName + "/clip";
    }

    // Leg bones (the jump tucks these; arms/spine stay free for casting in the air).
    // Filter NodePaths ("<skelPath>:<bone>") for every skeleton bone matching `pred`. Built from the skeleton itself (not a
    // clip) so the paths equal what RetargetBoneTracks rewrites the clip tracks to — the ONLY way the filter reliably matches
    // on GLB witches whose skeleton node isn't the borrowed clips' assumed "Skeleton3D".
    private static System.Collections.Generic.IEnumerable<NodePath> BoneFilterPaths(Skeleton3D skel, string skelPath, System.Func<string, bool> pred)
    {
        if (skel == null) yield break;
        for (int i = 0; i < skel.GetBoneCount(); i++)
        {
            string full = skelPath + ":" + (string)skel.GetBoneName(i);
            if (pred(full)) yield return new NodePath(full);
        }
    }

    private static bool IsLowerBody(string trackPath)
        => trackPath.Contains("UpLeg") || trackPath.Contains("Leg") || trackPath.Contains("Foot") || trackPath.Contains("Toe");

    // The jump drives the legs PLUS hips + spine so it READS on robed witches — a gown is skinned to the hips/spine, so a
    // legs-only tuck is invisible under a dress. Still excludes arms/hands/head/neck so casting stays free mid-air.
    private static bool IsJumpBody(string trackPath)
        => IsLowerBody(trackPath) || trackPath.Contains("Hips") || trackPath.Contains("Spine");

    // A bone track belongs to the upper body (cast should override it) if it's spine/neck/head/shoulder/arm/hand/fingers —
    // and explicitly NOT hips or any leg bone (those stay driven by locomotion).
    private static bool IsUpperBody(string trackPath)
    {
        if (trackPath.Contains("UpLeg") || trackPath.Contains("Leg") || trackPath.Contains("Foot") ||
            trackPath.Contains("Toe") || trackPath.Contains("Hips")) return false;
        return trackPath.Contains("Spine") || trackPath.Contains("Neck") || trackPath.Contains("Head") ||
               trackPath.Contains("Shoulder") || trackPath.Contains("Arm") || trackPath.Contains("Hand") ||
               trackPath.Contains("Thumb") || trackPath.Contains("Index") || trackPath.Contains("Middle") ||
               trackPath.Contains("Ring") || trackPath.Contains("Pinky") || trackPath.Contains("Finger");
    }

    // Preview a SPECIFIC animation glb on the model (for auditioning clips before committing). animFile is relative to
    // assets/models/witches/, e.g. "witch_lunar_idle_a11.glb".
    public static string PlayFrom(Node3D model, string animFile)
    {
        var ap = FindAnimPlayer(model);
        if (ap == null) { ap = new AnimationPlayer(); model.AddChild(ap); ap.RootNode = ap.GetPathTo(model); }
        string path = Root + "witches/" + animFile;
        if (!ResourceLoader.Exists(path)) return $"not found (imported?): {path}";
        string key = BorrowLib(ap, path, "preview");
        if (key == null) return $"no animation inside {animFile}";
        LoopAll(ap);
        ap.Play(key);
        return $"playing '{key}' from {animFile}";
    }

    // Diagnostic helper (loadmodel): set up locomotion and play idle. Returns what it found.
    public static string Animate(Node3D model, string key)
    {
        var (ap, idle, walk, run) = SetupLocomotion(model, key);
        if (ap == null) return "no AnimationPlayer";
        string first = idle ?? walk ?? run;
        if (first != null) { ap.Play(first); return $"idle={idle}, walk={walk}, run={run} → playing {first}"; }
        var own = ListAnims(ap);
        if (own.Count > 0) { ap.Play(own[0]); return $"no anim glbs → own '{own[0]}'"; }
        return "no anims found";
    }
}
