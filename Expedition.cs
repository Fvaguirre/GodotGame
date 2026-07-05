using Godot;
using System.Collections.Generic;

// Expedition.cs — the new co-op "Expedition" mode (L4D-style): traverse curated, semi-open
// connected arenas toward a beacon, light it, survive a surge, push to the safe room.
//
// This file owns ONLY THE MAP for now: an authored segment data format, a hand-built segment
// library, a linear stitcher, and a builder that realizes segments using the game's EXISTING
// collision primitives — so we add zero new collision code and combat is untouched:
//   * walls  -> tall Decks   (solid sides via Player/Enemy ClampPos, too high to climb, gap = doorway)
//   * cover  -> Blockers     (round pillars; player/enemy push-out + bolts collide for free)
//   * perch  -> Deck + Ramp  (raised walkable platform reached by a slope — verticality, already supported)
//
// The objective beacon, stationary-Heat director, and surge bolt on in later steps. Authoring a
// new room is just adding a SegmentDef in BuildLibrary(); everything is data, no scene editing.

public enum SegRole { SafeStart, Leg, Beacon, SafeEnd }

// Geometry is authored in LOCAL space: the segment origin sits at (0,0); the footprint spans
// X in [-SizeX/2, +SizeX/2] and Z in [-SizeZ/2, +SizeZ/2]. The stitcher translates to world space.
public struct ExpoWall  { public Vector2 Center; public Vector2 Half; public float Height; }       // solid box -> tall Deck
public struct ExpoCover { public Vector2 Center; public float Radius; public float Height; }        // pillar  -> Blocker + mesh
public struct ExpoPerch { public Vector2 Center; public Vector2 Half; public float TopY; }          // raised floor -> Deck
public struct ExpoRamp  { public Vector2 Center; public Vector2 Half; public float YLow; public float YHigh; public bool AlongX; } // -> Ramp

public class ExpoSegment
{
    public string Id;
    public SegRole Role;
    public float SizeX, SizeZ;
    public float DoorHalf = 2.4f;       // half-width of the centered doorway gap on entry/exit edges
    public bool OpenEntry = true;       // doorway gap on the -Z edge (toward the previous segment)
    public bool OpenExit  = true;       // doorway gap on the +Z edge (toward the next segment)
    public List<ExpoWall>  Walls   = new();
    public List<ExpoCover> Covers  = new();
    public List<ExpoPerch> Perches = new();
    public List<ExpoRamp>  Ramps   = new();
    public List<Vector2>   Spawns  = new();   // enemy spawn points (local) — consumed by later steps
}

// A segment placed into the world: Origin is the world (X,0,Z) added to every local coordinate.
public class ExpoPlaced { public ExpoSegment Def; public Vector3 Origin; }

public class ExpoLayout
{
    public List<ExpoPlaced> Segments = new();
    public Vector3 PlayerSpawn;
    public List<Vector3> Beacons = new();          // world beacon positions (one per Beacon segment)
    public List<ExpoPlaced> BeaconSegs = new();     // the placed segment for each beacon (for in-room surge spawns)
    public List<MeshInstance3D> BeaconMarkers = new(); // visual pillar per beacon (recolored when lit)
    public List<OmniLight3D> BeaconLights = new();     // light per beacon (recolored when lit)
    public Vector3 EndPos;
}

public static class Expedition
{
    // ---- LIBRARY: hand-authored segments. Add rooms here; the stitcher does the rest. ----
    public static List<ExpoSegment> BuildLibrary()
    {
        var lib = new List<ExpoSegment>();

        // Starting safe room: sealed behind you, open ahead. A calm box to gear up in.
        var safeStart = new ExpoSegment { Id = "safe_start", Role = SegRole.SafeStart, SizeX = 16, SizeZ = 16, OpenEntry = false, OpenExit = true };
        Perimeter(safeStart);
        safeStart.Covers.Add(new ExpoCover { Center = new Vector2(-4, 2),  Radius = 0.9f, Height = 1.6f });
        safeStart.Covers.Add(new ExpoCover { Center = new Vector2( 4, -2), Radius = 0.9f, Height = 1.6f });
        lib.Add(safeStart);

        // Connecting leg: a semi-open arena — big doorways at both ends, cover down the middle.
        var leg = new ExpoSegment { Id = "leg_a", Role = SegRole.Leg, SizeX = 15, SizeZ = 22 };
        Perimeter(leg);
        leg.Covers.Add(new ExpoCover { Center = new Vector2(-3.5f,  4), Radius = 1.1f, Height = 1.7f });
        leg.Covers.Add(new ExpoCover { Center = new Vector2( 3.5f, -1), Radius = 1.1f, Height = 1.7f });
        leg.Covers.Add(new ExpoCover { Center = new Vector2(-2.0f, -6), Radius = 1.0f, Height = 1.7f });
        leg.Spawns.Add(new Vector2(-5, 8)); leg.Spawns.Add(new Vector2(5, -8));
        lib.Add(leg);

        // Beacon arena: larger room with a raised center pad (perch + ramp) — the would-be beacon spot.
        var beacon = new ExpoSegment { Id = "beacon_a", Role = SegRole.Beacon, SizeX = 24, SizeZ = 24, DoorHalf = 2.8f };
        Perimeter(beacon, wallH: 4.5f);
        beacon.Perches.Add(new ExpoPerch { Center = new Vector2(0, 0), Half = new Vector2(4, 4), TopY = 1.4f });   // central pad
        beacon.Ramps.Add(new ExpoRamp { Center = new Vector2(0, -6), Half = new Vector2(2.4f, 2.0f), YLow = 0f, YHigh = 1.4f, AlongX = false }); // ramp up from the south
        beacon.Covers.Add(new ExpoCover { Center = new Vector2(-7, 6),  Radius = 1.2f, Height = 1.8f });
        beacon.Covers.Add(new ExpoCover { Center = new Vector2( 7, -6), Radius = 1.2f, Height = 1.8f });
        beacon.Covers.Add(new ExpoCover { Center = new Vector2( 7, 7),  Radius = 1.0f, Height = 1.8f });
        beacon.Covers.Add(new ExpoCover { Center = new Vector2(-7, -7), Radius = 1.0f, Height = 1.8f });
        beacon.Spawns.Add(new Vector2(-9, 9)); beacon.Spawns.Add(new Vector2(9, 9));
        beacon.Spawns.Add(new Vector2(-9, -9)); beacon.Spawns.Add(new Vector2(9, -9));
        lib.Add(beacon);

        // Closing safe room: open behind, sealed ahead — the exhale at the end of the leg.
        var safeEnd = new ExpoSegment { Id = "safe_end", Role = SegRole.SafeEnd, SizeX = 16, SizeZ = 16, OpenEntry = true, OpenExit = false };
        Perimeter(safeEnd);
        safeEnd.Covers.Add(new ExpoCover { Center = new Vector2(4, 3), Radius = 0.9f, Height = 1.6f });
        lib.Add(safeEnd);

        return lib;
    }

    // Build the 4 perimeter walls of a rectangular room, leaving centered door gaps where open.
    private static void Perimeter(ExpoSegment s, float wallH = 4f, float th = 0.5f)
    {
        float hx = s.SizeX / 2f, hz = s.SizeZ / 2f;
        // +X / -X side walls (full span along Z)
        s.Walls.Add(new ExpoWall { Center = new Vector2( hx, 0), Half = new Vector2(th, hz), Height = wallH });
        s.Walls.Add(new ExpoWall { Center = new Vector2(-hx, 0), Half = new Vector2(th, hz), Height = wallH });
        // -Z (entry) and +Z (exit) edges, each split around a centered doorway when open
        AddEdgeZ(s, -hz, th, hx, s.DoorHalf, s.OpenEntry, wallH);
        AddEdgeZ(s,  hz, th, hx, s.DoorHalf, s.OpenExit,  wallH);
    }

    private static void AddEdgeZ(ExpoSegment s, float z, float th, float hx, float dh, bool open, float wallH)
    {
        if (!open) { s.Walls.Add(new ExpoWall { Center = new Vector2(0, z), Half = new Vector2(hx, th), Height = wallH }); return; }
        float half = (hx - dh) / 2f;
        if (half > 0.05f)
        {
            s.Walls.Add(new ExpoWall { Center = new Vector2(-(hx + dh) / 2f, z), Half = new Vector2(half, th), Height = wallH });
            s.Walls.Add(new ExpoWall { Center = new Vector2( (hx + dh) / 2f, z), Half = new Vector2(half, th), Height = wallH });
        }
    }

    // ---- STITCHER: linear route for now. Stacks segments along +Z, centered on X, touching edges
    //      so the centered doorways line up and you can walk straight through. ----
    public static ExpoLayout Build(ulong seed)
    {
        var rng = new RandomNumberGenerator { Seed = seed };
        var lib = BuildLibrary();
        ExpoSegment Pick(SegRole role)
        {
            var pool = lib.FindAll(s => s.Role == role);
            return pool.Count == 0 ? null : pool[rng.RandiRange(0, pool.Count - 1)];
        }

        // First slice order: safe start -> leg -> beacon -> leg -> safe end.
        var order = new List<ExpoSegment> { Pick(SegRole.SafeStart), Pick(SegRole.Leg), Pick(SegRole.Beacon), Pick(SegRole.Leg), Pick(SegRole.SafeEnd) };

        var layout = new ExpoLayout();
        float z = 0f;
        for (int i = 0; i < order.Count; i++)
        {
            var def = order[i];
            if (def == null) continue;
            float hz = def.SizeZ / 2f;
            float centerZ = z + hz;                 // place so this segment's -Z edge sits at the running cursor
            var placed = new ExpoPlaced { Def = def, Origin = new Vector3(0, 0, centerZ) };
            layout.Segments.Add(placed);
            if (def.Role == SegRole.Beacon) { layout.Beacons.Add(placed.Origin); layout.BeaconSegs.Add(placed); }
            if (def.Role == SegRole.SafeStart) layout.PlayerSpawn = new Vector3(0, 0, centerZ);
            z += def.SizeZ;                          // next segment butts directly against this one's +Z edge
        }
        layout.EndPos = layout.Segments.Count > 0 ? layout.Segments[layout.Segments.Count - 1].Origin : Vector3.Zero;
        return layout;
    }

    // ---- BUILDER: realize the layout as visual meshes under a fresh root, and register the
    //      collision primitives into Game's live lists (cleared first). Returns the root node. ----
    public static Node3D Realize(Node3D parent, ExpoLayout layout)
    {
        var g = Game.I;
        g.Blockers.Clear(); g.Decks.Clear(); g.Ramps.Clear();

        var root = new Node3D { Name = "Expedition" };
        parent.AddChild(root);

        var matFloor = Mat(new Color(0.16f, 0.15f, 0.19f));
        var matWall  = Mat(new Color(0.27f, 0.26f, 0.31f));
        var matCover = Mat(new Color(0.34f, 0.30f, 0.27f));
        var matPerch = Mat(new Color(0.30f, 0.29f, 0.34f));

        foreach (var pl in layout.Segments)
        {
            var s = pl.Def; var o = pl.Origin;

            // floor (visual only — SurfaceHeight returns the y=0 ground plane by default inside the room)
            var floor = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(s.SizeX, s.SizeZ) }, MaterialOverride = matFloor };
            floor.Position = new Vector3(o.X, 0.02f, o.Z); root.AddChild(floor);

            // walls -> tall Decks (solid sides, too high to climb)
            foreach (var w in s.Walls)
            {
                var m = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(w.Half.X * 2f, w.Height, w.Half.Y * 2f) }, MaterialOverride = matWall };
                m.Position = new Vector3(o.X + w.Center.X, w.Height / 2f, o.Z + w.Center.Y); root.AddChild(m);
                g.Decks.Add(new Deck { Center = new Vector3(o.X + w.Center.X, 0, o.Z + w.Center.Y), Half = w.Half, TopY = w.Height });
            }

            // cover -> Blockers (round pillars)
            foreach (var c in s.Covers)
            {
                var m = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = c.Radius, BottomRadius = c.Radius, Height = c.Height }, MaterialOverride = matCover };
                m.Position = new Vector3(o.X + c.Center.X, c.Height / 2f, o.Z + c.Center.Y); root.AddChild(m);
                g.Blockers.Add(new Blocker { Pos = new Vector3(o.X + c.Center.X, 0, o.Z + c.Center.Y), Radius = c.Radius });
            }

            // perches -> Decks (walkable raised pads)
            foreach (var p in s.Perches)
            {
                var m = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(p.Half.X * 2f, p.TopY, p.Half.Y * 2f) }, MaterialOverride = matPerch };
                m.Position = new Vector3(o.X + p.Center.X, p.TopY / 2f, o.Z + p.Center.Y); root.AddChild(m);
                var cap = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(p.Half.X * 2f + 0.3f, 0.3f, p.Half.Y * 2f + 0.3f) }, MaterialOverride = matPerch };
                cap.Position = new Vector3(o.X + p.Center.X, p.TopY, o.Z + p.Center.Y); root.AddChild(cap);
                g.Decks.Add(new Deck { Center = new Vector3(o.X + p.Center.X, 0, o.Z + p.Center.Y), Half = p.Half, TopY = p.TopY });
            }

            // ramps -> Ramps (sloped walkways) — visual is an approximate flat slab for now
            foreach (var r in s.Ramps)
            {
                var m = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(r.Half.X * 2f, 0.3f, r.Half.Y * 2f) }, MaterialOverride = matPerch };
                m.Position = new Vector3(o.X + r.Center.X, (r.YLow + r.YHigh) / 2f, o.Z + r.Center.Y);
                m.RotationDegrees = new Vector3(r.AlongX ? 0 : Mathf.RadToDeg(Mathf.Atan2(r.YHigh - r.YLow, r.Half.Y * 2f)), 0, r.AlongX ? -Mathf.RadToDeg(Mathf.Atan2(r.YHigh - r.YLow, r.Half.X * 2f)) : 0);
                root.AddChild(m);
                g.Ramps.Add(new Ramp { Center = new Vector3(o.X + r.Center.X, 0, o.Z + r.Center.Y), Half = r.Half, YLow = r.YLow, YHigh = r.YHigh, AlongX = r.AlongX });
            }
        }

        // beacon markers (recolored from violet to lit-orange when activated)
        foreach (var b in layout.Beacons)
        {
            var bmat = Mat(new Color(0.55f, 0.40f, 0.95f), emit: 0.7f);
            var m = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.5f, BottomRadius = 0.8f, Height = 2.2f }, MaterialOverride = bmat };
            m.Position = new Vector3(b.X, 2.5f, b.Z); root.AddChild(m);
            var light = new OmniLight3D { LightColor = new Color(0.6f, 0.45f, 1f), LightEnergy = 1.4f, OmniRange = 10f };
            light.Position = new Vector3(b.X, 3.2f, b.Z); root.AddChild(light);
            layout.BeaconMarkers.Add(m);
            layout.BeaconLights.Add(light);
        }

        return root;
    }

    private static StandardMaterial3D Mat(Color c, float emit = 0f)
    {
        var m = new StandardMaterial3D { AlbedoColor = c, Roughness = 0.9f, Metallic = 0f };
        if (emit > 0f) { m.EmissionEnabled = true; m.Emission = c; m.EmissionEnergyMultiplier = emit; }
        return m;
    }

    // ---- NAVIGATION (linear leg) ----
    // Enemies don't pathfind; they beeline at a target position. In a walled leg a straight line
    // would drive them into walls, so when the target is in a DIFFERENT room we hand back a doorway
    // waypoint that moves them one room closer. Because segments are stacked in index order along +Z,
    // "the doorway toward the target" is just the shared edge on the +Z or -Z side of the current room.
    // Once the enemy shares the target's room we return the real target and the existing beeline + cover
    // push-out take over. (Branching maps later upgrade this to A* over the same door graph.)
    //
    // STEERING POLISH + LATERAL NUDGE (lateral01 is a stable per-enemy value in [0,1]):
    //   * lateral nudge — fan enemies across the passable width of the gap instead of stacking dead-center.
    //   * staging glide — while still approaching, aim just BEFORE the gap to line up; only commit THROUGH
    //     it once close, so off-axis enemies slide toward the opening rather than cutting into the jamb.
    public static Vector3 NavTarget(ExpoLayout lay, Vector3 from, Vector3 to, float lateral01 = 0.5f)
    {
        if (lay == null || lay.Segments.Count < 2) return to;
        int si = SegIndexAt(lay, from);
        int ti = SegIndexAt(lay, to);
        if (si < 0 || ti < 0 || si == ti) return to;   // same room or off-map -> beeline (cover handled by push-out)

        var cur = lay.Segments[si];
        bool toPlus = ti > si;
        float hz = cur.Def.SizeZ / 2f;
        float doorZ = cur.Origin.Z + (toPlus ? hz : -hz);     // doorway plane on the side toward the target's room

        // lateral nudge: spread across the gap, kept well inside the jambs so the clamp never fights it
        float spread = Mathf.Max(0f, cur.Def.DoorHalf - 1.4f);
        float laneX = cur.Origin.X + (lateral01 * 2f - 1f) * spread;

        // staging glide: line up in front of the gap first, commit through only when close to the plane
        float beforeDoor = toPlus ? doorZ - from.Z : from.Z - doorZ;
        float aimZ = beforeDoor > 2.5f ? doorZ - (toPlus ? 1f : -1f) : doorZ + (toPlus ? 1.2f : -1.2f);
        return new Vector3(laneX, 0f, aimZ);
    }

    private static int SegIndexAt(ExpoLayout lay, Vector3 p)
    {
        for (int i = 0; i < lay.Segments.Count; i++)
        {
            var s = lay.Segments[i];
            if (Mathf.Abs(p.X - s.Origin.X) <= s.Def.SizeX / 2f + 0.5f && Mathf.Abs(p.Z - s.Origin.Z) <= s.Def.SizeZ / 2f + 0.5f)
                return i;
        }
        return -1;
    }
}

// ExpoRun — the objective state machine that turns the authored leg into the L4D loop:
// Travel toward the active beacon -> light it (hold E) -> a Surge erupts in-room, hold the line ->
// the way opens, push to the next beacon or the closing safe room. Heat ramps while you linger on a
// leg and bleeds when you make forward progress, so standing still is never the safe play.
// Host-authoritative (mirrors the wave director); surge bodies are real enemies that sync as usual.
public class ExpoRun
{
    public enum Phase { Travel, Surge, Complete }
    public Phase Cur = Phase.Travel;
    public int ActiveBeacon = 0;
    public bool[] Lit;
    public string ObjectiveText = "Reach the beacon and light it";

    private readonly ExpoLayout _lay;
    private readonly Queue<string> _surge = new();
    private float _spawnT = 0f, _surgeT = 0f, _bannerT = 6f;
    private float _minDist = 99999f, _stallT = 0f;

    public ExpoRun(ExpoLayout lay) { _lay = lay; Lit = new bool[Mathf.Max(1, lay.Beacons.Count)]; }

    // Where the party is currently headed: the active beacon, or (once all are lit) the end safe room.
    public Vector3 ActivePos => ActiveBeacon < _lay.Beacons.Count ? _lay.Beacons[ActiveBeacon] : _lay.EndPos;
    public bool BeaconReady => Cur == Phase.Travel && ActiveBeacon < _lay.Beacons.Count && !Lit[ActiveBeacon];

    public void LightBeacon(Game g)
    {
        if (!BeaconReady) return;
        Lit[ActiveBeacon] = true;
        Cur = Phase.Surge;
        LitVisual(ActiveBeacon);

        // build the surge: scales with how deep we are and how many wardens are present
        int wardens = Mathf.Max(1, g.WardenCount);
        int n = 10 + ActiveBeacon * 5 + (wardens - 1) * 5;
        string[] pool = { "shade", "wisp", "caster", "shade", "diver" };
        for (int i = 0; i < n; i++) _surge.Enqueue(pool[(int)(GD.Randi() % (uint)pool.Length)]);
        if (wardens > 1 || ActiveBeacon > 0) _surge.Enqueue("hexer");

        _surgeT = 36f; _spawnT = 0f;
        g.Heat = Mathf.Min(1.6f, g.Heat + 0.35f);
        ObjectiveText = "Survive the surge — hold the line!";
        g.Hud?.Banner("the beacon flares — hold the line!");
        g.Sfx?.Clink();
    }

    // flip a beacon's visual from dormant violet to a blazing lit-orange (host on light, client on sync)
    private void LitVisual(int i)
    {
        if (i >= 0 && i < _lay.BeaconLights.Count && GodotObject.IsInstanceValid(_lay.BeaconLights[i]))
        { _lay.BeaconLights[i].LightColor = new Color(1f, 0.55f, 0.2f); _lay.BeaconLights[i].LightEnergy = 2.6f; }
        if (i >= 0 && i < _lay.BeaconMarkers.Count && GodotObject.IsInstanceValid(_lay.BeaconMarkers[i])
            && _lay.BeaconMarkers[i].MaterialOverride is StandardMaterial3D sm)
        { sm.AlbedoColor = new Color(1f, 0.55f, 0.2f); sm.Emission = new Color(1f, 0.5f, 0.15f); sm.EmissionEnergyMultiplier = 1.3f; }
    }

    // ---- multiplayer: host packs this state and clients apply it (display only; host owns the logic) ----
    public int LitMask() { int m = 0; for (int i = 0; i < Lit.Length; i++) if (Lit[i]) m |= 1 << i; return m; }
    public void ApplyNetState(int activeBeacon, int phase, int litMask, string objective)
    {
        ActiveBeacon = activeBeacon;
        Cur = (Phase)phase;
        ObjectiveText = objective;
        for (int i = 0; i < Lit.Length; i++)
        {
            bool lit = (litMask & (1 << i)) != 0;
            if (lit && !Lit[i]) LitVisual(i);   // newly lit -> flip its visual on the client
            Lit[i] = lit;
        }
    }

    public void Tick(Game g, float dt)
    {
        if (Cur == Phase.Complete) return;
        Vector3 me = g.Player != null ? g.Player.GlobalPosition : Vector3.Zero;

        if (Cur == Phase.Travel)
        {
            // stationary-Heat: progress toward the objective bleeds pressure; lingering ramps it
            float d = (me - ActivePos).Length();
            if (d < _minDist - 0.5f) { _minDist = d; _stallT = 0f; if (g.Heat > 1f) g.Heat = Mathf.Max(1f, g.Heat - 0.06f * dt); }
            else { _stallT += dt; if (_stallT > 4f) g.Heat = Mathf.Min(1.6f, g.Heat + 0.06f * dt); }

            // arriving at the end safe room (after the last beacon is lit) completes the run
            if (ActiveBeacon >= _lay.Beacons.Count && (me - _lay.EndPos).Length() < 6f)
            {
                Cur = Phase.Complete;
                ObjectiveText = "Expedition complete!";
                g.Hud?.Banner("the safe room — you made it!");
                g.Sfx?.Clink();
                return;
            }
        }
        else if (Cur == Phase.Surge)
        {
            _spawnT -= dt;
            if (_surge.Count > 0 && _spawnT <= 0f)
            {
                g.SpawnEnemyAt(_surge.Dequeue(), SurgeSpawnPos(g));
                _spawnT = (float)GD.RandRange(0.35, 0.7);
            }
            _surgeT -= dt;
            // the surge resolves once every body has spawned and either the timer elapses or the room is cleared
            if (_surge.Count == 0 && (_surgeT <= 0f || g.Enemies.Count == 0))
            {
                g.Heat = Mathf.Max(1f, g.Heat - 0.3f);
                ActiveBeacon++;
                Cur = Phase.Travel; _minDist = 99999f; _stallT = 0f; _bannerT = 0.1f;
                ObjectiveText = ActiveBeacon < _lay.Beacons.Count ? "Reach the next beacon and light it" : "Reach the safe room";
                g.Hud?.Banner(ActiveBeacon < _lay.Beacons.Count ? "the way opens — push on" : "the way opens — reach the safe room");
            }
        }

        _bannerT -= dt;
        if (_bannerT <= 0f) { _bannerT = 9f; g.Hud?.Banner(ObjectiveText); }
    }

    // Surge bodies erupt from the active beacon room's authored spawn points, so they engage in-room
    // rather than beelining through walls from outside (cross-room pathing is the later AI step).
    private Vector3 SurgeSpawnPos(Game g)
    {
        if (ActiveBeacon < _lay.BeaconSegs.Count)
        {
            var ps = _lay.BeaconSegs[ActiveBeacon];
            if (ps.Def.Spawns.Count > 0)
            {
                var sp = ps.Def.Spawns[(int)(GD.Randi() % (uint)ps.Def.Spawns.Count)];
                return new Vector3(ps.Origin.X + sp.X, 0, ps.Origin.Z + sp.Y);
            }
            return ps.Origin;
        }
        return g.Player != null ? g.Player.GlobalPosition : Vector3.Zero;
    }
}
