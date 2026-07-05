using Godot;
using System.Collections.Generic;

// Maze.cs — the "escape the hedge maze" interlude (L4D-director style). A procedural grid maze with tall
// hedges (solid + too high to jump), wide corridors for big mobs, and X+1 circular landmark chambers with
// elemental statues. Reuses the Expedition world-swap: Game.InExpedition flattens SurfaceHeight to y=0 and
// frees the streamed world + stops the wave loop; Game.InMaze gates maze-specific logic.
//
// PHASE 1: generation + F6 teleport in/out + a reachable exit portal. Later phases add the firework flare,
// the fairy + wisp navigation (BFS over this same grid), find-each-other, and the maze heat director.
// The grid (WallN/WallE/Open) is kept on MazeData precisely so those later phases get BFS pathfinding free.

public class MazeData
{
    public int W, H;                 // grid dimensions (cells)
    public float Cell = 9f;          // world units per cell (wide corridors for big mobs)
    public bool[,] WallN, WallE;     // is there a hedge on this cell's north / east edge? (thin-wall maze)
    public bool[,] Open;             // cell is carved/walkable (chambers clear whole discs)
    public Vector2I Start;           // spawn cell (solo / Spawns[0])
    public Vector2I Portal;          // exit cell
    public List<Vector2I> Spawns = new();   // one spread-out spawn per player (deterministic by seed)
    public List<Vector2I> Chambers = new();
    public List<int> ChamberElem = new();   // element index (DamageType) for each chamber's statue
    public Vector3 Origin;           // world-space offset of the maze's (0,0) corner

    public Vector3 CellCenter(Vector2I c) => Origin + new Vector3((c.X + 0.5f) * Cell, 0f, (c.Y + 0.5f) * Cell);
    public Vector3 PlayerSpawn => CellCenter(Start);
    public Vector3 PortalPos => CellCenter(Portal);
    public bool In(Vector2I c) => c.X >= 0 && c.X < W && c.Y >= 0 && c.Y < H;

    // is travel from cell a to an orthogonally-adjacent cell b blocked by a hedge? (used by later BFS phases)
    public bool Blocked(Vector2I a, Vector2I b)
    {
        var d = b - a;
        if (d == new Vector2I(0, 1)) return WallN[a.X, a.Y];
        if (d == new Vector2I(0, -1)) return WallN[b.X, b.Y];
        if (d == new Vector2I(1, 0)) return WallE[a.X, a.Y];
        if (d == new Vector2I(-1, 0)) return WallE[b.X, b.Y];
        return true;
    }
}

public static class Maze
{
    public static MazeData Build(ulong seed, int peers)
    {
        var rng = new RandomNumberGenerator { Seed = seed };
        int pc = Mathf.Max(1, peers);
        var m = new MazeData();
        m.Cell = 13f;
        int n = 15 + (pc - 1) * 6;   // bigger + more convoluted with more players (harder base)
        m.W = n; m.H = n;
        m.Origin = new Vector3(10000f, 0f, 10000f);   // far offset; InExpedition keeps the floor flat at y=0

        m.WallN = new bool[n, n];
        m.WallE = new bool[n, n];
        m.Open = new bool[n, n];
        for (int x = 0; x < n; x++)
            for (int y = 0; y < n; y++) { m.WallN[x, y] = true; m.WallE[x, y] = true; m.Open[x, y] = true; }

        // recursive backtracker (DFS) — long winding corridors + lots of dead ends
        var visited = new bool[n, n];
        var stack = new Stack<Vector2I>();
        var start = new Vector2I(rng.RandiRange(0, n - 1), rng.RandiRange(0, n - 1));
        visited[start.X, start.Y] = true; stack.Push(start);
        var dirs = new[] { new Vector2I(0, 1), new Vector2I(0, -1), new Vector2I(1, 0), new Vector2I(-1, 0) };
        while (stack.Count > 0)
        {
            var cur = stack.Peek();
            var nbrs = new List<Vector2I>();
            foreach (var d in dirs)
            {
                var np = cur + d;
                if (np.X >= 0 && np.X < n && np.Y >= 0 && np.Y < n && !visited[np.X, np.Y]) nbrs.Add(np);
            }
            if (nbrs.Count == 0) { stack.Pop(); continue; }
            var nx = nbrs[rng.RandiRange(0, nbrs.Count - 1)];
            Carve(m, cur, nx);
            visited[nx.X, nx.Y] = true; stack.Push(nx);
        }

        // X+1 circular chambers — spread out, hedges cleared within a disc, distinct elemental statue each
        int chambers = pc + 1;
        int cr = 2;   // chamber radius (cells)
        for (int i = 0; i < chambers; i++)
        {
            var cc = new Vector2I(rng.RandiRange(cr + 1, n - 2 - cr), rng.RandiRange(cr + 1, n - 2 - cr));
            m.Chambers.Add(cc); m.ChamberElem.Add(i % 10);
            for (int x = -cr; x <= cr; x++)
                for (int y = -cr; y <= cr; y++)
                {
                    if (x * x + y * y > cr * cr) continue;
                    var p = cc + new Vector2I(x, y);
                    if (!m.In(p)) continue;
                    if (p.X < n - 1) m.WallE[p.X, p.Y] = false;
                    if (p.X > 0) m.WallE[p.X - 1, p.Y] = false;
                    if (p.Y < n - 1) m.WallN[p.X, p.Y] = false;
                    if (p.Y > 0) m.WallN[p.X, p.Y - 1] = false;
                }
        }

        // one spread-out spawn per player (deterministic → identical on every machine)
        var ctr = new Vector2(n * 0.5f, n * 0.5f);
        float sr = n * 0.36f;
        for (int i = 0; i < pc; i++)
        {
            float ang = (i / (float)pc) * Mathf.Tau + rng.Randf() * 0.4f;
            m.Spawns.Add(new Vector2I(Mathf.Clamp((int)(ctr.X + Mathf.Cos(ang) * sr), 0, n - 1), Mathf.Clamp((int)(ctr.Y + Mathf.Sin(ang) * sr), 0, n - 1)));
        }
        m.Start = m.Spawns[0];
        m.Portal = PickPortal(m, m.Spawns);   // solo default; MP recomputes at find-each-other
        return m;
    }

    // Pick the exit-portal cell: reachable from the given cells, out of line-of-sight from all of them, and
    // the navigable-furthest (multi-source BFS → max of the min corridor distance to any player). Reused in
    // Phase 4 when the portal spawns on "found each other" — pass every player's current cell.
    public static Vector2I PickPortal(MazeData m, List<Vector2I> from)
    {
        var dist = new int[m.W, m.H];
        for (int x = 0; x < m.W; x++) for (int y = 0; y < m.H; y++) dist[x, y] = -1;
        var q = new Queue<Vector2I>();
        foreach (var f in from) if (m.In(f)) { dist[f.X, f.Y] = 0; q.Enqueue(f); }
        var dirs = new[] { new Vector2I(0, 1), new Vector2I(0, -1), new Vector2I(1, 0), new Vector2I(-1, 0) };
        while (q.Count > 0)
        {
            var c = q.Dequeue();
            foreach (var d in dirs)
            {
                var np = c + d;
                if (m.In(np) && dist[np.X, np.Y] < 0 && !m.Blocked(c, np)) { dist[np.X, np.Y] = dist[c.X, c.Y] + 1; q.Enqueue(np); }
            }
        }
        Vector2I best = from.Count > 0 ? from[0] : new Vector2I(0, 0), bestAny = best;
        int bestD = -1, bestAnyD = -1;
        for (int x = 0; x < m.W; x++)
            for (int y = 0; y < m.H; y++)
            {
                int dd = dist[x, y];
                if (dd < 0) continue;              // unreachable — never a portal
                var c = new Vector2I(x, y);
                if (dd > bestAnyD) { bestAnyD = dd; bestAny = c; }
                bool anyLoS = false;
                foreach (var f in from) if (HasLoS(m, c, f)) { anyLoS = true; break; }
                if (!anyLoS && dd > bestD) { bestD = dd; best = c; }
            }
        return bestD >= 0 ? best : bestAny;        // fall back to plain-furthest if every far cell had LoS (won't happen in a real maze)
    }

    // clear line of sight between two cell centers = the straight segment crosses no hedge (and cuts no corner)
    public static bool HasLoS(MazeData m, Vector2I a, Vector2I b)
    {
        var pa = m.CellCenter(a); var pb = m.CellCenter(b);
        int steps = Mathf.Max(1, Mathf.CeilToInt((pb - pa).Length() / (m.Cell * 0.25f)));
        Vector2I prev = a;
        for (int i = 1; i <= steps; i++)
        {
            var pt = pa.Lerp(pb, i / (float)steps);
            var cell = new Vector2I(Mathf.FloorToInt((pt.X - m.Origin.X) / m.Cell), Mathf.FloorToInt((pt.Z - m.Origin.Z) / m.Cell));
            if (!m.In(cell)) return false;
            if (cell != prev)
            {
                var d = cell - prev;
                if (Mathf.Abs(d.X) + Mathf.Abs(d.Y) != 1) return false;   // diagonal corner cut → no clear sight
                if (m.Blocked(prev, cell)) return false;                  // a hedge is in the way
                prev = cell;
            }
        }
        return true;
    }

    private static void Carve(MazeData m, Vector2I a, Vector2I b)
    {
        var d = b - a;
        if (d == new Vector2I(0, 1)) m.WallN[a.X, a.Y] = false;
        else if (d == new Vector2I(0, -1)) m.WallN[b.X, b.Y] = false;
        else if (d == new Vector2I(1, 0)) m.WallE[a.X, a.Y] = false;
        else if (d == new Vector2I(-1, 0)) m.WallE[b.X, b.Y] = false;
    }

    public static Node3D Realize(Node3D parent, MazeData m)
    {
        var g = Game.I;
        g.Blockers.Clear(); g.Decks.Clear(); g.Ramps.Clear();
        var root = new Node3D { Name = "MazeRoot" };
        parent.AddChild(root);

        float cell = m.Cell;
        float wallH = 28f;     // well above any jump-combo / extra-jump apex → can't be climbed or jumped
        float th = 1.2f;       // hedge thickness

        // floor
        float fw = m.W * cell, fh = m.H * cell;
        var floor = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(fw + cell * 4f, fh + cell * 4f) }, MaterialOverride = Mat(new Color(0.10f, 0.13f, 0.10f)) };
        floor.Position = new Vector3(m.Origin.X + fw * 0.5f, 0.02f, m.Origin.Z + fh * 0.5f);
        root.AddChild(floor);

        var hedgeMat = Mat(new Color(0.09f, 0.19f, 0.11f));

        // one wall segment = a tall hedge mesh + a Deck (rectangular horizontal collision, standable-but-unreachable top)
        void Wall(float cx, float cz, float hx, float hz)
        {
            var box = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(hx * 2f, wallH, hz * 2f) }, MaterialOverride = hedgeMat };
            box.Position = new Vector3(cx, wallH * 0.5f, cz);
            root.AddChild(box);
            g.Decks.Add(new Deck { Center = new Vector3(cx, 0f, cz), Half = new Vector2(hx, hz), TopY = wallH });
        }

        // interior hedges from the grid
        for (int x = 0; x < m.W; x++)
            for (int y = 0; y < m.H; y++)
            {
                float ox = m.Origin.X + x * cell, oz = m.Origin.Z + y * cell;
                if (m.WallE[x, y] && x < m.W - 1) Wall(ox + cell, oz + cell * 0.5f, th * 0.5f, cell * 0.5f + th * 0.5f);
                if (m.WallN[x, y] && y < m.H - 1) Wall(ox + cell * 0.5f, oz + cell, cell * 0.5f + th * 0.5f, th * 0.5f);
            }
        // outer perimeter
        for (int x = 0; x < m.W; x++)
        {
            float ox = m.Origin.X + x * cell;
            Wall(ox + cell * 0.5f, m.Origin.Z, cell * 0.5f + th * 0.5f, th * 0.5f);
            Wall(ox + cell * 0.5f, m.Origin.Z + m.H * cell, cell * 0.5f + th * 0.5f, th * 0.5f);
        }
        for (int y = 0; y < m.H; y++)
        {
            float oz = m.Origin.Z + y * cell;
            Wall(m.Origin.X, oz + cell * 0.5f, th * 0.5f, cell * 0.5f + th * 0.5f);
            Wall(m.Origin.X + m.W * cell, oz + cell * 0.5f, th * 0.5f, cell * 0.5f + th * 0.5f);
        }

        // chamber landmarks — elemental statue (pillar + glowing orb + light) + a couple of trees
        for (int i = 0; i < m.Chambers.Count; i++)
        {
            var cpos = m.CellCenter(m.Chambers[i]);
            var col = DamageTypes.Col((DamageType)m.ChamberElem[i]);
            var pillar = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.4f, BottomRadius = 0.8f, Height = 4.2f }, MaterialOverride = Game.ToonEmissive(col.Lerp(new Color(0.15f, 0.15f, 0.17f), 0.55f), 0.6f, 0.03f) };
            pillar.Position = cpos + new Vector3(0, 2.1f, 0); root.AddChild(pillar);
            var orb = new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.7f, Height = 1.4f }, MaterialOverride = Game.Emissive(col, 2.6f) };
            orb.Position = cpos + new Vector3(0, 4.9f, 0); root.AddChild(orb);
            root.AddChild(new OmniLight3D { Position = cpos + new Vector3(0, 4.9f, 0), OmniRange = cell * 3f, LightColor = col, LightEnergy = 2.2f });
            g.Blockers.Add(new Blocker { Pos = cpos, Radius = 0.9f });
            var treeMat = Mat(new Color(0.10f, 0.17f, 0.12f));
            var trunkMat = Mat(new Color(0.16f, 0.11f, 0.07f));
            for (int t = 0; t < 2; t++)
            {
                float ta = (i * 1.7f + t * 3.1f);
                var tp = cpos + new Vector3(Mathf.Cos(ta) * cell * 1.1f, 0, Mathf.Sin(ta) * cell * 1.1f);
                var trunk = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.18f, BottomRadius = 0.28f, Height = 3f }, MaterialOverride = trunkMat };
                trunk.Position = tp + new Vector3(0, 1.5f, 0); root.AddChild(trunk);
                var canopy = new MeshInstance3D { Mesh = new SphereMesh { Radius = 1.6f, Height = 2.4f }, MaterialOverride = treeMat };
                canopy.Position = tp + new Vector3(0, 3.6f, 0); root.AddChild(canopy);
                g.Blockers.Add(new Blocker { Pos = tp, Radius = 0.4f });
            }
        }

        return root;
    }

    private static StandardMaterial3D Mat(Color c) => new StandardMaterial3D { AlbedoColor = c, Roughness = 0.95f, Metallic = 0f };

    // ---- navigation (fairy wisps + wrong-direction spawns) --------------------------------------------
    public static Vector2I CellOf(MazeData m, Vector3 pos)
    {
        int x = Mathf.Clamp(Mathf.FloorToInt((pos.X - m.Origin.X) / m.Cell), 0, m.W - 1);
        int y = Mathf.Clamp(Mathf.FloorToInt((pos.Z - m.Origin.Z) / m.Cell), 0, m.H - 1);
        return new Vector2I(x, y);
    }

    // corridor-distance from every cell to `portal` (−1 = unreachable). BFS respecting hedges.
    public static int[,] DistField(MazeData m, Vector2I portal) => DistField(m, new List<Vector2I> { portal });

    // distance to the NEAREST source (portal, or all players for the chase field). BFS respecting hedges.
    public static int[,] DistField(MazeData m, List<Vector2I> sources)
    {
        var dist = new int[m.W, m.H];
        for (int x = 0; x < m.W; x++) for (int y = 0; y < m.H; y++) dist[x, y] = -1;
        var q = new Queue<Vector2I>();
        foreach (var s in sources) if (m.In(s) && dist[s.X, s.Y] < 0) { dist[s.X, s.Y] = 0; q.Enqueue(s); }
        var dirs = new[] { new Vector2I(0, 1), new Vector2I(0, -1), new Vector2I(1, 0), new Vector2I(-1, 0) };
        while (q.Count > 0)
        {
            var c = q.Dequeue();
            foreach (var d in dirs)
            {
                var n = c + d;
                if (m.In(n) && dist[n.X, n.Y] < 0 && !m.Blocked(c, n)) { dist[n.X, n.Y] = dist[c.X, c.Y] + 1; q.Enqueue(n); }
            }
        }
        return dist;
    }

    // pick a spawn cell a few cells from a random player, OUT OF SIGHT of everyone, biased to the WRONG
    // direction (farther from the portal). portalDist may be null (find-each-other phase → no direction bias).
    public static bool PickSpawnCell(MazeData m, int[,] portalDist, List<Vector2I> players, RandomNumberGenerator rng, out Vector2I cell)
    {
        cell = default;
        if (players.Count == 0) return false;
        Vector2I bestCell = default; int bestScore = int.MinValue; bool found = false;
        for (int tries = 0; tries < 24; tries++)
        {
            var pc = players[rng.RandiRange(0, players.Count - 1)];
            int rr = rng.RandiRange(3, 6);
            float ang = rng.Randf() * Mathf.Tau;
            var cand = new Vector2I(Mathf.Clamp(pc.X + Mathf.RoundToInt(Mathf.Cos(ang) * rr), 0, m.W - 1), Mathf.Clamp(pc.Y + Mathf.RoundToInt(Mathf.Sin(ang) * rr), 0, m.H - 1));
            bool tooClose = false;
            foreach (var pp in players) if (Mathf.Abs(cand.X - pp.X) + Mathf.Abs(cand.Y - pp.Y) < 3) { tooClose = true; break; }   // clamp near an edge can drop it right on a player
            if (tooClose) continue;
            if (portalDist != null && portalDist[cand.X, cand.Y] < 0) continue;   // unreachable
            bool seen = false;
            foreach (var p in players) if (HasLoS(m, cand, p)) { seen = true; break; }
            if (seen) continue;                                                    // must be out of sight of everyone
            int score = (portalDist != null) ? portalDist[cand.X, cand.Y] - portalDist[pc.X, pc.Y] : 0;   // wrong-direction bias
            if (!found || score > bestScore) { found = true; bestScore = score; bestCell = cand; }
        }
        cell = bestCell; return found;
    }

    // world direction from `cell` down the corridor that gets closest to the portal (the navigable step)
    public static Vector3 PathDir(MazeData m, int[,] dist, Vector2I cell)
    {
        if (!m.In(cell) || dist[cell.X, cell.Y] < 0) return Vector3.Zero;
        Vector2I best = cell; int bd = dist[cell.X, cell.Y];
        foreach (var d in new[] { new Vector2I(0, 1), new Vector2I(0, -1), new Vector2I(1, 0), new Vector2I(-1, 0) })
        {
            var n = cell + d;
            if (m.In(n) && !m.Blocked(cell, n) && dist[n.X, n.Y] >= 0 && dist[n.X, n.Y] < bd) { bd = dist[n.X, n.Y]; best = n; }
        }
        if (best == cell) return Vector3.Zero;
        var dir = m.CellCenter(best) - m.CellCenter(cell); dir.Y = 0;
        return dir.Normalized();
    }
}
