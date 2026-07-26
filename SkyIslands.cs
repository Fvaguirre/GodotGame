using Godot;
using System.Collections.Generic;

// SkyIslands.cs — PURE generation + geometry for the jungle "Sky Islands" ritual (the aerial twin of the maze).
// A cluster of floating jungle islands is built above the live jungle at a seed-derived spot; players are whisked
// up by a ground whirlwind, platform island-to-island (jumping, dashing, and riding hanging vines that fling them
// skyward), light 3 effigies, then reach the cauldron on a far island. Falling drops you back onto the real jungle.
//
// This file only BUILDS the layout + meshes and registers walkable Decks / vine launch points. The ritual STATE
// MACHINE (enter/exit, director, heat, fall-out, objective, networking) lives in Game.cs, mirroring the maze.
// Determinism: Build is a pure function of (seed, playerCount, origin) so every peer generates the identical cluster.
public static class SkyIslands
{
    public const float TierRise = 85f;   // how far above the jungle floor the island cluster floats — a big airy gap so a fallen player can grab a hanging vine below and ride back up

    public struct Isle
    {
        public Vector3 Center;   // XZ + top Y
        public float Radius;
        public int Role;         // 0 normal, 1 entry, 2 effigy, 3 cauldron
    }

    public class SkyData
    {
        public readonly List<Isle> Isles = new();
        public Vector3 Entry;                       // where players arrive (top of the entry island + a bit)
        public Vector3 Cauldron;                    // cauldron world pos
        public readonly List<Vector3> Effigies = new();   // 3 effigy world positions
        public readonly List<Vector3> Chests = new();     // gold-chest world positions
        public float BaseY;
    }

    public class SkyRealized
    {
        public Node3D Root;
        public readonly List<Node3D> EffigyNodes = new();
        public Node3D CauldronNode;
        public Node3D CauldronBeam;   // hidden until the 3 effigies are lit
    }

    // ------------------------------------------------------------------ generation
    public static SkyData Build(ulong seed, int playerCount, Vector3 origin)
    {
        var rng = new RandomNumberGenerator { Seed = seed };
        var d = new SkyData { BaseY = origin.Y + TierRise };
        int count = 12 + playerCount * 3;                   // lots of islands (+ per player)
        float ox = origin.X, oz = origin.Z, baseY = d.BaseY;

        // a MASSIVE central entry island players land on
        d.Isles.Add(new Isle { Center = new Vector3(ox, baseY, oz), Radius = 12f + rng.Randf() * 3f, Role = 1 });

        // grow a connected chain: each new island hangs off a random earlier one within jump/dash+vine reach
        for (int i = 1; i < count; i++)
        {
            var parent = d.Isles[rng.RandiRange(0, d.Isles.Count - 1)];
            float radius = 3.8f + rng.Randf() * 3.2f;
            const float infl = 1.3f;   // islands inflate to ~1.3x in Realize (radX/radZ) — space using the REAL extent so they don't kiss
            Vector3 c = Vector3.Zero; bool ok = false;
            for (int tries = 0; tries < 16 && !ok; tries++)
            {
                float ang = rng.Randf() * Mathf.Tau;
                float gap = (radius + parent.Radius) * infl + 9f + rng.Randf() * 12f;   // edge gaps ~9-21u — clearly separated, crossed by dash/double-jump/vine
                float dy = (rng.Randf() - 0.45f) * 6f;
                c = new Vector3(parent.Center.X + Mathf.Cos(ang) * gap, Mathf.Clamp(parent.Center.Y + dy, baseY - 9f, baseY + 15f), parent.Center.Z + Mathf.Sin(ang) * gap);
                ok = true;
                foreach (var o in d.Isles) if (new Vector2(o.Center.X - c.X, o.Center.Z - c.Z).Length() < (o.Radius + radius) * infl + 8f) { ok = false; break; }   // keep a clear gap between islands
            }
            d.Isles.Add(new Isle { Center = c, Radius = radius, Role = 0 });
        }

        // cauldron = the island farthest (XZ) from entry
        int cauldronIdx = 1; float far = -1f;
        for (int i = 1; i < d.Isles.Count; i++)
        {
            float dd = new Vector2(d.Isles[i].Center.X - ox, d.Isles[i].Center.Z - oz).LengthSquared();
            if (dd > far) { far = dd; cauldronIdx = i; }
        }
        var ci = d.Isles[cauldronIdx]; ci.Role = 3; d.Isles[cauldronIdx] = ci;
        d.Cauldron = ci.Center + new Vector3(0, 0.1f, 0);

        // 3 effigies on spread-out middle islands (not entry, not cauldron)
        var pool = new List<int>();
        for (int i = 1; i < d.Isles.Count; i++) if (i != cauldronIdx) pool.Add(i);
        Shuffle(pool, rng);
        for (int k = 0; k < 3 && pool.Count > 0; k++)
        {
            int idx = pool[k];
            var e = d.Isles[idx]; e.Role = 2; d.Isles[idx] = e;
            d.Effigies.Add(e.Center + new Vector3(0, 0.1f, 0));
        }

        // gold chests on a couple more islands (scale with players); never the entry
        int chests = 1 + playerCount / 2;
        for (int k = 3; k < 3 + chests && k < pool.Count; k++)
            d.Chests.Add(d.Isles[pool[k]].Center + new Vector3(0, 0.4f, 0));

        d.Entry = d.Isles[0].Center + new Vector3(0, 1.2f, 0);
        return d;
    }

    private static void Shuffle(List<int> list, RandomNumberGenerator rng)
    {
        for (int i = list.Count - 1; i > 0; i--) { int j = rng.RandiRange(0, i); (list[i], list[j]) = (list[j], list[i]); }
    }

    // a little jungle fern — blades of leaf fanning outward from a base
    private static void AddFern(Node3D root, Vector3 pos, Material mat, RandomNumberGenerator rng)
    {
        var fern = new Node3D { Position = pos };
        int blades = 4 + rng.RandiRange(0, 3);
        for (int i = 0; i < blades; i++)
        {
            float a = i / (float)blades * Mathf.Tau;
            var blade = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.13f, 0.9f + rng.Randf() * 0.5f, 0.04f) }, MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            blade.Position = new Vector3(Mathf.Cos(a) * 0.16f, 0.45f, Mathf.Sin(a) * 0.16f);
            blade.RotationDegrees = new Vector3(Mathf.Cos(a) * 38f, Mathf.RadToDeg(a), Mathf.Sin(a) * 38f);
            fern.AddChild(blade);
        }
        root.AddChild(fern);
    }

    // ------------------------------------------------------------------ geometry + collision registration
    public static SkyRealized Realize(Game g, SkyData d, ulong seed)
    {
        var rng = new RandomNumberGenerator { Seed = seed ^ 0x5EED };
        var r = new SkyRealized { Root = new Node3D { Name = "SkyRoot" } };
        g.AddChild(r.Root);

        var noShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        // jungle-matched palette (mirrors the Rainforest leaf/bark colours in Tree.cs)
        var rock = Game.ToonEmissive(new Color(0.22f, 0.18f, 0.14f), 0.1f, 0.02f);
        var grass = Game.ToonEmissive(new Color(0.10f, 0.36f, 0.15f), 0.3f, 0.02f);
        var vineMat = Game.ToonEmissive(new Color(0.14f, 0.4f, 0.18f), 0.4f, 0.02f);
        var fernMat = Game.ToonEmissive(new Color(0.08f, 0.44f, 0.17f), 0.45f, 0.02f);

        foreach (var isle in d.Isles)
        {
            var c = isle.Center; float rad = isle.Radius;
            float radX = rad * (0.8f + rng.Randf() * 0.5f), radZ = rad * (0.8f + rng.Randf() * 0.5f);   // non-square footprint
            float big = Mathf.Max(radX, radZ);

            // walkable grass top = EXACTLY the deck rectangle → every green tile you see is actually solid (no phantom edges)
            r.Root.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(radX * 2f, 0.8f, radZ * 2f) }, MaterialOverride = grass, CastShadow = noShadow, Position = new Vector3(c.X, c.Y - 0.4f, c.Z) });
            // a stepped-down grass shelf (within the footprint) for a bit of edge relief
            r.Root.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(radX * 1.55f, 0.6f, radZ * 1.55f) }, MaterialOverride = grass, CastShadow = noShadow, Position = new Vector3(c.X + (rng.Randf() - 0.5f) * radX * 0.3f, c.Y - 0.95f, c.Z + (rng.Randf() - 0.5f) * radZ * 0.3f) });
            // jagged rock body hanging beneath — DECORATIVE only (no collider), kept within the footprint so nothing sticks past the deck
            int chunks = 4 + rng.RandiRange(0, 3);
            for (int i = 0; i < chunks; i++)
            {
                float h = big * (0.7f + rng.Randf() * 1.4f);
                var chunk = new MeshInstance3D { Mesh = new PrismMesh { Size = new Vector3(1.4f + rng.Randf() * 2.2f, h, 1.4f + rng.Randf() * 2.2f) }, MaterialOverride = rock, CastShadow = noShadow };
                chunk.Position = new Vector3(c.X + (rng.Randf() - 0.5f) * radX * 0.8f, c.Y - 1.0f - h * 0.5f, c.Z + (rng.Randf() - 0.5f) * radZ * 0.8f);
                chunk.RotationDegrees = new Vector3(180f + (rng.Randf() - 0.5f) * 35f, rng.Randf() * 360f, (rng.Randf() - 0.5f) * 35f);
                r.Root.AddChild(chunk);
            }

            // jungle trees — scaled + placed so the whole canopy stays INSIDE the island (never overhanging onto a neighbour),
            // shorter species only, and kept off the centre where the effigy/chest/cauldron sit
            if (isle.Role != 3 && big > 4.8f)
            {
                int trees = big > 8f ? 2 : 1;
                for (int i = 0; i < trees; i++)
                {
                    var sp = new[] { ProcTree.Species.Understory, ProcTree.Species.JungleGnarled, ProcTree.Species.Palm }[rng.RandiRange(0, 2)];
                    var tree = ProcTree.Build(sp, rng, out float br, out float th, out _);
                    float sc = Mathf.Clamp(big * 0.085f, 0.5f, 0.8f);
                    tree.Scale = Vector3.One * sc;
                    float canopyR = Mathf.Max(br * 3.5f, th * 0.2f) * sc;        // approx canopy footprint (width AND lean)
                    float smallExt = Mathf.Min(radX, radZ);
                    float fMax = 0.92f - canopyR / Mathf.Max(1f, smallExt);      // keep the whole canopy within the island edge
                    if (fMax <= 0.34f) continue;                                 // island too small for this tree → skip it
                    float f = 0.34f + rng.Randf() * (fMax - 0.34f), ta = rng.Randf() * Mathf.Tau;
                    tree.Position = new Vector3(c.X + Mathf.Cos(ta) * radX * f, c.Y, c.Z + Mathf.Sin(ta) * radZ * f);
                    tree.RotationDegrees = new Vector3(0, rng.Randf() * 360f, 0);
                    r.Root.AddChild(tree);
                }
            }
            // ferns — kept out of the very centre too
            int ferns = 2 + (int)(rad * 0.6f);
            for (int i = 0; i < ferns; i++)
            {
                float a = rng.Randf() * Mathf.Tau, rr = 0.3f + rng.Randf() * 0.6f;
                AddFern(r.Root, new Vector3(c.X + Mathf.Cos(a) * radX * rr, c.Y + 0.05f, c.Z + Mathf.Sin(a) * radZ * rr), fernMat, rng);
            }

            // WALKABLE deck (rectangle, survives streaming) — Floating so its side-collision is only a thin rim, not an endless column below
            g.PersistentDecks.Add(new Deck { Center = new Vector3(c.X, c.Y, c.Z), Half = new Vector2(radX, radZ), TopY = c.Y, Floating = true });

            // a long vine hanging TANGENT off the island edge (top touching the underside), grabbable along its WHOLE length
            // while falling, and flinging you only back up to just above THIS island (not sky-high like the ground vines)
            if (isle.Role != 3)
            {
                float a = rng.Randf() * Mathf.Tau;
                var dir = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                var top = new Vector3(c.X + dir.X * radX, c.Y - 0.3f, c.Z + dir.Z * radZ);   // ON the island edge → hangs from the rock, not floating in air
                float vlen = 22f + rng.Randf() * 10f;   // long — dangles well down into the gap so a falling player has a real window to catch it
                var bottom = new Vector3(top.X, c.Y - vlen, top.Z);
                int gpts = 8;
                for (int k = 0; k <= gpts; k++)   // grab points down the WHOLE vine so any part of it catches you as you fall
                    g.PersistentVines.Add(new VineGrab { Pos = top.Lerp(bottom, k / (float)gpts), TopY = c.Y + 2f, Sky = true });
                r.Root.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.11f, BottomRadius = 0.08f, Height = vlen, RadialSegments = 6 }, MaterialOverride = vineMat, CastShadow = noShadow, Position = new Vector3(top.X, top.Y - vlen * 0.5f, top.Z) });
            }
        }

        // effigies (reuse the maze chamber-statue look: pillar + glowing orb + light) — start UNLIT (dim)
        foreach (var epos in d.Effigies)
        {
            var eff = new Node3D { Position = epos };
            r.Root.AddChild(eff);
            var col = new Color(0.82f, 0.36f, 0.90f);   // curse-violet, matches the ritual fantasy
            eff.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.28f, BottomRadius = 0.42f, Height = 2.2f }, MaterialOverride = Game.ToonEmissive(new Color(0.3f, 0.28f, 0.34f), 0.1f, 0.02f), Position = new Vector3(0, 1.1f, 0) });
            var orb = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.34f, Height = 0.68f }, MaterialOverride = Game.ToonEmissive(col, 0.6f, 0f), Position = new Vector3(0, 2.5f, 0) };
            eff.AddChild(orb);
            r.EffigyNodes.Add(eff);
        }

        // cauldron (reuse the maze ritual-statue idea: plinth + pot + brew + a skybeam hidden until armed)
        {
            var cn = new Node3D { Position = d.Cauldron };
            r.Root.AddChild(cn);
            var stone = Game.ToonEmissive(new Color(0.32f, 0.30f, 0.28f), 0.1f, 0.02f);
            cn.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 1.1f, BottomRadius = 1.3f, Height = 0.5f }, MaterialOverride = stone, Position = new Vector3(0, 0.25f, 0) });
            cn.AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.9f, Height = 1.4f }, MaterialOverride = Game.ToonEmissive(new Color(0.16f, 0.16f, 0.18f), 0.05f, 0.02f), Position = new Vector3(0, 1.1f, 0) });
            var brew = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.78f, BottomRadius = 0.78f, Height = 0.12f }, MaterialOverride = Game.ToonEmissive(new Color(0.4f, 0.95f, 0.5f), 2.2f, 0f), Position = new Vector3(0, 1.5f, 0) };
            cn.AddChild(brew);
            // a tall skybeam marking the cauldron once armed — hidden initially
            var beam = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.5f, BottomRadius = 1.0f, Height = 60f }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.4f, 0.95f, 0.5f, 0.16f), EmissionEnabled = true, Emission = new Color(0.4f, 0.95f, 0.5f), EmissionEnergyMultiplier = 1.5f, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded } };
            beam.Position = new Vector3(0, 30f, 0); beam.Visible = false;
            cn.AddChild(beam);
            r.CauldronNode = cn; r.CauldronBeam = beam;
        }

        return r;
    }
}
