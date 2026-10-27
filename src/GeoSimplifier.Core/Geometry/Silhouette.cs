using System;
using System.Collections.Generic;
using System.Linq;

namespace GeoSimplifier.Core.Geometry
{
    /// <summary>
    /// The outline of a part seen along one axis, for a coarse stand-in of a very detailed part (Serkan: the engine
    /// block and cylinder heads of TAG3 shown "in rough outline"): the part's triangles are drawn on a grid in the
    /// plane square to the axis, holes in the drawing are filled, and the edge of each separate region is traced and
    /// simplified to a polygon. Extruding the outlines of the three axes and intersecting them gives a body with the
    /// part's outer shape and none of its small detail (holes, bolts, fins, lettering).
    /// </summary>
    public static class Silhouette
    {
        /// <summary>
        /// The outer outlines seen along <paramref name="axis"/> (0 = X, 1 = Y, 2 = Z), each a closed polygon of
        /// (u, v) points in the plane, u along axis + 1 and v along axis + 2, counter-clockwise. The grid has about
        /// <paramref name="cellsAlongLongest"/> cells along the drawing's longest side; the polygons are simplified to
        /// within <paramref name="tolerance"/>. Regions smaller than <paramref name="minArea"/> are left out.
        /// </summary>
        public static List<List<(double u, double v)>> Outlines(IReadOnlyList<Vec3> triangles, int axis, int cellsAlongLongest = 120,
            double tolerance = 0, double minArea = 0)
        {
            var loops = new List<List<(double, double)>>();
            if (triangles.Count < 3)
                return loops;
            int ua = (axis + 1) % 3, va = (axis + 2) % 3;
            double uMin = double.MaxValue, vMin = double.MaxValue, uMax = double.MinValue, vMax = double.MinValue;
            foreach (var p in triangles)
            {
                uMin = Math.Min(uMin, p[ua]); uMax = Math.Max(uMax, p[ua]);
                vMin = Math.Min(vMin, p[va]); vMax = Math.Max(vMax, p[va]);
            }
            double longest = Math.Max(uMax - uMin, vMax - vMin);
            if (longest <= 0)
                return loops;
            double cell = longest / Math.Max(4, cellsAlongLongest);
            // One empty cell all round, so the outside is one connected region.
            int nu = (int)Math.Ceiling((uMax - uMin) / cell) + 2, nv = (int)Math.Ceiling((vMax - vMin) / cell) + 2;
            double u0 = uMin - cell, v0 = vMin - cell;
            var full = new bool[nu, nv];
            for (int t = 0; t + 2 < triangles.Count; t += 3)
                Draw(full, (triangles[t][ua] - u0) / cell, (triangles[t][va] - v0) / cell,
                     (triangles[t + 1][ua] - u0) / cell, (triangles[t + 1][va] - v0) / cell,
                     (triangles[t + 2][ua] - u0) / cell, (triangles[t + 2][va] - v0) / cell);
            // Holes are filled: whatever the outside does not reach is part of the drawing.
            var outside = new bool[nu, nv];
            var queue = new Queue<(int, int)>();
            queue.Enqueue((0, 0));
            outside[0, 0] = true;
            while (queue.Count > 0)
            {
                var (i, j) = queue.Dequeue();
                foreach (var (di, dj) in Steps)
                {
                    int a = i + di, b = j + dj;
                    if (a < 0 || b < 0 || a >= nu || b >= nv || outside[a, b] || full[a, b]) continue;
                    outside[a, b] = true;
                    queue.Enqueue((a, b));
                }
            }
            var inside = new bool[nu, nv];
            for (int i = 0; i < nu; i++)
                for (int j = 0; j < nv; j++)
                    inside[i, j] = !outside[i, j];
            // Each region (4-connected) gives one outline.
            var seen = new bool[nu, nv];
            for (int i = 0; i < nu; i++)
                for (int j = 0; j < nv; j++)
                {
                    if (!inside[i, j] || seen[i, j]) continue;
                    var region = new HashSet<(int, int)>();
                    queue.Enqueue((i, j));
                    seen[i, j] = true;
                    while (queue.Count > 0)
                    {
                        var (a, b) = queue.Dequeue();
                        region.Add((a, b));
                        foreach (var (di, dj) in Steps)
                        {
                            int x = a + di, y = b + dj;
                            if (x < 0 || y < 0 || x >= nu || y >= nv || seen[x, y] || !inside[x, y]) continue;
                            seen[x, y] = true;
                            queue.Enqueue((x, y));
                        }
                    }
                    if (region.Count * cell * cell < minArea)
                        continue;
                    var loop = Trace(region);
                    if (loop.Count < 3)
                        continue;
                    var points = loop.Select(c => (u0 + c.Item1 * cell, v0 + c.Item2 * cell)).ToList();
                    points = Simplify(points, Math.Max(tolerance, cell * 0.5));
                    if (points.Count >= 3)
                        loops.Add(points);
                }
            return loops;
        }

        /// <summary>
        /// The same filled drawing as <see cref="Outlines"/>, as rectangles (u0, v0, u1, v1) covering it: runs of
        /// cells along u, joined with the run above when they start and end alike. Extruded as boxes they make the
        /// outline's prism from boxes alone, where a trimmed planar sheet cannot be made.
        /// </summary>
        public static List<(double u0, double v0, double u1, double v1)> Rectangles(IReadOnlyList<Vec3> triangles, int axis, int cellsAlongLongest = 60)
        {
            var rects = new List<(double, double, double, double)>();
            var grid = Filled(triangles, axis, cellsAlongLongest, out double u0, out double v0, out double cell);
            if (grid == null)
                return rects;
            int nu = grid.GetLength(0), nv = grid.GetLength(1);
            var open = new Dictionary<(int, int), int>(); // run (start, end) → first row
            for (int j = 0; j <= nv; j++)
            {
                var runs = new HashSet<(int, int)>();
                for (int i = 0; j < nv && i < nu; i++)
                {
                    if (!grid[i, j]) continue;
                    int start = i;
                    while (i + 1 < nu && grid[i + 1, j]) i++;
                    runs.Add((start, i));
                }
                foreach (var run in open.Keys.Where(r => !runs.Contains(r)).ToList())
                {
                    rects.Add((u0 + run.Item1 * cell, v0 + open[run] * cell, u0 + (run.Item2 + 1) * cell, v0 + j * cell));
                    open.Remove(run);
                }
                foreach (var run in runs)
                    if (!open.ContainsKey(run)) open[run] = j;
            }
            return rects;
        }

        /// <summary>The drawing with its holes filled: cells inside the outline true. Null when there is nothing.</summary>
        private static bool[,] Filled(IReadOnlyList<Vec3> triangles, int axis, int cellsAlongLongest, out double u0, out double v0, out double cell)
        {
            u0 = v0 = cell = 0;
            if (triangles.Count < 3)
                return null;
            int ua = (axis + 1) % 3, va = (axis + 2) % 3;
            double uMin = double.MaxValue, vMin = double.MaxValue, uMax = double.MinValue, vMax = double.MinValue;
            foreach (var p in triangles)
            {
                uMin = Math.Min(uMin, p[ua]); uMax = Math.Max(uMax, p[ua]);
                vMin = Math.Min(vMin, p[va]); vMax = Math.Max(vMax, p[va]);
            }
            double longest = Math.Max(uMax - uMin, vMax - vMin);
            if (longest <= 0)
                return null;
            cell = longest / Math.Max(4, cellsAlongLongest);
            int nu = (int)Math.Ceiling((uMax - uMin) / cell) + 2, nv = (int)Math.Ceiling((vMax - vMin) / cell) + 2;
            u0 = uMin - cell; v0 = vMin - cell;
            var full = new bool[nu, nv];
            for (int t = 0; t + 2 < triangles.Count; t += 3)
                Draw(full, (triangles[t][ua] - u0) / cell, (triangles[t][va] - v0) / cell,
                     (triangles[t + 1][ua] - u0) / cell, (triangles[t + 1][va] - v0) / cell,
                     (triangles[t + 2][ua] - u0) / cell, (triangles[t + 2][va] - v0) / cell);
            var outside = new bool[nu, nv];
            var queue = new Queue<(int, int)>();
            queue.Enqueue((0, 0));
            outside[0, 0] = true;
            while (queue.Count > 0)
            {
                var (i, j) = queue.Dequeue();
                foreach (var (di, dj) in Steps)
                {
                    int a = i + di, b = j + dj;
                    if (a < 0 || b < 0 || a >= nu || b >= nv || outside[a, b] || full[a, b]) continue;
                    outside[a, b] = true;
                    queue.Enqueue((a, b));
                }
            }
            var inside = new bool[nu, nv];
            for (int i = 0; i < nu; i++)
                for (int j = 0; j < nv; j++)
                    inside[i, j] = !outside[i, j];
            return inside;
        }

        private static readonly (int, int)[] Steps = { (1, 0), (-1, 0), (0, 1), (0, -1) };

        /// <summary>Marks every cell a triangle (in cell units) touches.</summary>
        private static void Draw(bool[,] full, double ax, double ay, double bx, double by, double cx, double cy)
        {
            int nu = full.GetLength(0), nv = full.GetLength(1);
            int x0 = Math.Max(0, (int)Math.Floor(Math.Min(ax, Math.Min(bx, cx)))), x1 = Math.Min(nu - 1, (int)Math.Floor(Math.Max(ax, Math.Max(bx, cx))));
            int y0 = Math.Max(0, (int)Math.Floor(Math.Min(ay, Math.Min(by, cy)))), y1 = Math.Min(nv - 1, (int)Math.Floor(Math.Max(ay, Math.Max(by, cy))));
            double area = (bx - ax) * (cy - ay) - (cx - ax) * (by - ay);
            for (int i = x0; i <= x1; i++)
                for (int j = y0; j <= y1; j++)
                {
                    // A triangle seen edge-on, or smaller than a cell, marks the cells its box covers.
                    if (Math.Abs(area) < 1e-9 || (x1 - x0 < 1 && y1 - y0 < 1)) { full[i, j] = true; continue; }
                    double px = i + 0.5, py = j + 0.5;
                    double w0 = (bx - ax) * (py - ay) - (by - ay) * (px - ax);
                    double w1 = (cx - bx) * (py - by) - (cy - by) * (px - bx);
                    double w2 = (ax - cx) * (py - cy) - (ay - cy) * (px - cx);
                    // Within half a cell of the triangle counts, so thin parts do not break apart.
                    double r = 0.75 * Math.Sqrt(2);
                    bool In(double w, double ex, double ey) => (area > 0 ? w : -w) >= -r * Math.Sqrt(ex * ex + ey * ey);
                    if (In(w0, bx - ax, by - ay) && In(w1, cx - bx, cy - by) && In(w2, ax - cx, ay - cy))
                        full[i, j] = true;
                }
        }

        /// <summary>
        /// The outer edge of a region of cells as a closed list of grid corners, counter-clockwise, following the
        /// cell edges with the region on the left. Holes have been filled, so the outer edge is the only one.
        /// </summary>
        private static List<(int, int)> Trace(HashSet<(int, int)> region)
        {
            // Directed edges with the region on the left: for each cell side that faces outside.
            var next = new Dictionary<(int, int), List<(int, int)>>();
            void Edge((int, int) from, (int, int) to)
            {
                if (!next.TryGetValue(from, out var list)) next[from] = list = new List<(int, int)>();
                list.Add(to);
            }
            foreach (var (i, j) in region)
            {
                if (!region.Contains((i, j - 1))) Edge((i, j), (i + 1, j));
                if (!region.Contains((i + 1, j))) Edge((i + 1, j), (i + 1, j + 1));
                if (!region.Contains((i, j + 1))) Edge((i + 1, j + 1), (i, j + 1));
                if (!region.Contains((i - 1, j))) Edge((i, j + 1), (i, j));
            }
            // Start at the lowest-left corner, which is on the outer edge.
            var start = next.Keys.OrderBy(k => k.Item2).ThenBy(k => k.Item1).First();
            var loop = new List<(int, int)> { start };
            var at = start;
            (int, int) heading = (1, 0);
            for (int guard = 0; guard < 4 * region.Count + 8; guard++)
            {
                if (!next.TryGetValue(at, out var outs) || outs.Count == 0) break;
                // Where two edges leave one corner (regions touching at a corner), the one turning most to the left
                // keeps the trace on this region's outer edge.
                var pick = outs.OrderByDescending(o => Turn(heading, (o.Item1 - at.Item1, o.Item2 - at.Item2))).First();
                outs.Remove(pick);
                heading = (pick.Item1 - at.Item1, pick.Item2 - at.Item2);
                at = pick;
                if (at == start) break;
                loop.Add(at);
            }
            return loop;
        }

        /// <summary>Left turn 1, straight 0, right turn -1, back -2.</summary>
        private static int Turn((int, int) from, (int, int) to)
        {
            int cross = from.Item1 * to.Item2 - from.Item2 * to.Item1;
            int dot = from.Item1 * to.Item1 + from.Item2 * to.Item2;
            return cross > 0 ? 1 : cross < 0 ? -1 : dot > 0 ? 0 : -2;
        }

        /// <summary>A closed polygon simplified to within <paramref name="tolerance"/> (Douglas-Peucker, both halves).</summary>
        public static List<(double u, double v)> Simplify(List<(double u, double v)> points, double tolerance)
        {
            if (points.Count < 4)
                return points;
            // Split at the point farthest from the first, so both halves are open chains.
            int far = 0;
            double best = -1;
            for (int i = 1; i < points.Count; i++)
            {
                double d = Sq(points[i].u - points[0].u) + Sq(points[i].v - points[0].v);
                if (d > best) { best = d; far = i; }
            }
            var first = points.Take(far + 1).ToList();
            var second = points.Skip(far).Concat(new[] { points[0] }).ToList();
            var a = Chain(first, tolerance);
            var b = Chain(second, tolerance);
            var result = a.Take(a.Count - 1).Concat(b.Take(b.Count - 1)).ToList();
            return result;
        }

        private static List<(double u, double v)> Chain(List<(double u, double v)> points, double tolerance)
        {
            if (points.Count < 3)
                return points;
            int index = -1;
            double worst = 0;
            var (au, av) = points[0];
            var (bu, bv) = points[points.Count - 1];
            double length = Math.Sqrt(Sq(bu - au) + Sq(bv - av));
            for (int i = 1; i < points.Count - 1; i++)
            {
                double d = length < 1e-12 ? Math.Sqrt(Sq(points[i].u - au) + Sq(points[i].v - av))
                    : Math.Abs((bu - au) * (av - points[i].v) - (au - points[i].u) * (bv - av)) / length;
                if (d > worst) { worst = d; index = i; }
            }
            if (worst <= tolerance)
                return new List<(double, double)> { points[0], points[points.Count - 1] };
            var left = Chain(points.Take(index + 1).ToList(), tolerance);
            var right = Chain(points.Skip(index).ToList(), tolerance);
            return left.Take(left.Count - 1).Concat(right).ToList();
        }

        private static double Sq(double x) => x * x;

        /// <summary>
        /// A simple counter-clockwise polygon split into convex counter-clockwise pieces: triangulated by ear
        /// clipping, then neighbouring pieces joined while the join stays convex (Hertel-Mehlhorn). Each piece can
        /// be cut out of a box with one flat cut per edge.
        /// </summary>
        public static List<List<(double u, double v)>> ConvexPieces(List<(double u, double v)> polygon)
        {
            var pieces = new List<List<int>>();
            int n = polygon.Count;
            if (n < 3)
                return new List<List<(double, double)>>();
            if (SignedArea(polygon) < 0)
                polygon = Enumerable.Reverse(polygon).ToList();
            double Cross((double u, double v) o, (double u, double v) a, (double u, double v) b) =>
                (a.u - o.u) * (b.v - o.v) - (a.v - o.v) * (b.u - o.u);
            // Ear clipping.
            var left = Enumerable.Range(0, n).ToList();
            for (int guard = 0; left.Count > 3 && guard < n * n; guard++)
            {
                bool cut = false;
                for (int k = 0; k < left.Count; k++)
                {
                    int a = left[(k + left.Count - 1) % left.Count], b = left[k], c = left[(k + 1) % left.Count];
                    if (Cross(polygon[a], polygon[b], polygon[c]) <= 1e-15) continue;
                    bool inside = false;
                    foreach (int p in left)
                    {
                        if (p == a || p == b || p == c) continue;
                        if (Cross(polygon[a], polygon[b], polygon[p]) >= 0 && Cross(polygon[b], polygon[c], polygon[p]) >= 0 &&
                            Cross(polygon[c], polygon[a], polygon[p]) >= 0) { inside = true; break; }
                    }
                    if (inside) continue;
                    pieces.Add(new List<int> { a, b, c });
                    left.RemoveAt(k);
                    cut = true;
                    break;
                }
                if (!cut) break;
            }
            if (left.Count == 3)
                pieces.Add(new List<int>(left));
            // Joining: two pieces sharing an edge become one when the corners at both ends of the edge stay convex.
            bool Convex(List<int> piece)
            {
                for (int k = 0; k < piece.Count; k++)
                    if (Cross(polygon[piece[k]], polygon[piece[(k + 1) % piece.Count]], polygon[piece[(k + 2) % piece.Count]]) < -1e-15)
                        return false;
                return true;
            }
            for (bool joined = true; joined;)
            {
                joined = false;
                for (int x = 0; x < pieces.Count && !joined; x++)
                    for (int y = x + 1; y < pieces.Count && !joined; y++)
                    {
                        var merged = Join(pieces[x], pieces[y]);
                        if (merged == null || !Convex(merged)) continue;
                        pieces[x] = merged;
                        pieces.RemoveAt(y);
                        joined = true;
                    }
            }
            return pieces.Select(p => p.Select(i => polygon[i]).ToList()).ToList();
        }

        /// <summary>Two pieces sharing an edge (a→b in one, b→a in the other) as one, or null.</summary>
        private static List<int> Join(List<int> p, List<int> q)
        {
            for (int i = 0; i < p.Count; i++)
            {
                int a = p[i], b = p[(i + 1) % p.Count];
                for (int j = 0; j < q.Count; j++)
                {
                    if (q[j] != b || q[(j + 1) % q.Count] != a) continue;
                    var merged = new List<int>();
                    for (int k = 0; k < p.Count; k++) merged.Add(p[(i + 1 + k) % p.Count]); // b ... a
                    for (int k = 2; k < q.Count; k++) merged.Add(q[(j + k) % q.Count]);      // after a in q, up to before b
                    return merged;
                }
            }
            return null;
        }

        private static double SignedArea(List<(double u, double v)> p) =>
            0.5 * Enumerable.Range(0, p.Count).Sum(i => p[i].u * p[(i + 1) % p.Count].v - p[(i + 1) % p.Count].u * p[i].v);
    }
}
