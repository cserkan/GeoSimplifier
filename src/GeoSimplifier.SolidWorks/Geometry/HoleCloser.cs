using System;
using System.Collections.Generic;
using System.Linq;
using GeoSimplifier.Core.Geometry;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace GeoSimplifier.SolidWorks.Geometry
{
    /// <summary>
    /// Closes holes, slots and pockets exactly with plugs: a box standing on each planar mouth of a hole and
    /// reaching down to the bottom of the hole, minus the body, is the empty space of the hole. That space is
    /// united with the body, so the plug's faces are the hole walls and the mouth plane (flush).
    /// </summary>
    /// <remarks>
    /// IBody2.DeleteFaces5 (Delete Face > Delete and Patch) would be the natural tool, but it returns false
    /// for every face even on a plain plate with through holes, and building the mouth as a trimmed planar
    /// sheet (ISurface.CreateTrimmedSheet4) crashes SOLIDWORKS, so only boxes and booleans are used.
    /// </remarks>
    public sealed class HoleCloser
    {
        private readonly BodyOps _ops;

        public HoleCloser(BodyOps ops)
        {
            _ops = ops;
        }

        public int Closed { get; private set; }
        public int Failed { get; private set; }

        /// <summary>Raised details (embossed text, symbols) flattened by <see cref="RemoveBosses"/>.</summary>
        public int BossesRemoved { get; private set; }

        /// <summary>What hole detection saw on the first pass of each body, for the summary and for debugging.</summary>
        public readonly List<string> Log = new List<string>();

        /// <summary>Openings that were found but are wider than the limit (meters).</summary>
        public readonly List<double> TooWide = new List<double>();

        private bool _logging;

        /// <summary>A hole: connected faces bounded only by inner loops of the faces around it.</summary>
        private sealed class Hole
        {
            public readonly List<int> FaceIds = new List<int>();
            public double Throat = double.PositiveInfinity;
            public bool Depression = true;
            public Aabb Bounds = Aabb.Empty;
            public readonly List<Mouth> Mouths = new List<Mouth>();
        }

        /// <summary>A boundary loop of a hole on a planar face: where a plug box stands and how big it must be.</summary>
        private sealed class Mouth
        {
            public Vec3 Normal;     // outward normal of the face holding the loop
            public Vec3 Centroid;
            public double Depth;    // how far the hole goes below the loop plane
            public double Height;   // how far a boss rises above the loop plane
            public double Radius;   // how far the hole reaches sideways from the centroid
        }

        private const int MaxHoleFaces = 400;

        // Faces of every opening the inner-loop search looked at, closed or not; the wall search skips them.
        private HashSet<int> _seen = new HashSet<int>();

        /// <summary>Closes every hole whose narrowest mouth is at most <paramref name="maxWidth"/> wide.</summary>
        public IBody2 CloseHoles(IBody2 body, double maxWidth)
        {
            _logging = true;
            var holes = FindHoles(body, maxWidth, out _);
            _logging = false;
            var planarFaces = new HashSet<int>(_seen);

            var current = body;
            foreach (var hole in holes)
            {
                // A through hole has a mouth on each side; the first plug usually fills it and the others add nothing.
                bool ok = true;
                foreach (var mouth in hole.Mouths)
                {
                    var next = Fill(current, hole, mouth);
                    if (next == null) { ok = false; break; }
                    current = next;
                }
                if (ok) Closed++;
                else Failed++;
            }
            // Holes whose mouth runs across several faces (e.g. on a bend radius) are found from their walls.
            var walls = new WallHoleCloser(_ops, Log.Add);
            current = walls.Close(body, current, maxWidth, planarFaces);
            Closed += walls.Closed;
            Failed += walls.Failed;
            TooWide.AddRange(walls.TooWide);
            return current;
        }

        /// <summary>
        /// Flattens raised details standing on a planar face (embossed text, symbols, small bosses) that rise at
        /// most <paramref name="maxHeight"/> above it: the part of the body inside a box standing on the face over
        /// the detail is cut away, leaving the face flat.
        /// </summary>
        public IBody2 RemoveBosses(IBody2 body, double maxHeight)
        {
            var bosses = new List<Hole>();
            FindHoles(body, 0, out _, bosses, maxHeight);
            var current = body;
            foreach (var boss in bosses)
            {
                var next = Cut(current, boss, boss.Mouths[0]);
                if (next == null)
                    continue;
                current = next;
                BossesRemoved++;
            }
            return current;
        }

        /// <summary>Cuts the material of <paramref name="boss"/> above <paramref name="mouth"/>; null on failure.</summary>
        private IBody2 Cut(IBody2 body, Hole boss, Mouth mouth)
        {
            double side = 2 * mouth.Radius * 1.05 + 1e-5;
            var box = _ops.BoxOnAxis(mouth.Centroid, mouth.Normal, side, mouth.Height * 1.02 + 1e-5);
            if (box == null)
                return Fail("kabartma kutusu oluşturulamadı");
            var pieces = _ops.Intersect(box, body);
            if (pieces == null)
                return Fail("kabartma ayrılamadı");

            double tol = boss.Bounds.LongestSide * 0.05 + 1e-6;
            var result = body;
            foreach (IBody2 piece in pieces)
            {
                if (!Inside(BodyOps.Bounds(piece), boss.Bounds, tol))
                    continue;
                var rest = _ops.Subtract(result, piece);
                if (rest == null || rest.Length == 0)
                    return Fail("kabartma gövdeden çıkarılamadı");
                // A cut never splits the part; if it does, keep the main body and drop slivers.
                result = rest.Cast<IBody2>().OrderByDescending(b => BodyOps.Bounds(b).LongestSide).First();
            }
            return result;
        }

        private static bool Inside(Aabb inner, Aabb outer, double tol)
        {
            bool inside = true;
            for (int a = 0; a < 3; a++)
                inside &= inner.Min[a] >= outer.Min[a] - tol && inner.Max[a] <= outer.Max[a] + tol;
            return inside;
        }

        /// <summary>Unites the empty space of the hole under <paramref name="mouth"/> with the body; null on failure.</summary>
        private IBody2 Fill(IBody2 body, Hole hole, Mouth mouth)
        {
            double side = 2 * mouth.Radius * 1.05 + 1e-5;
            var box = _ops.BoxOnAxis(mouth.Centroid, mouth.Normal * -1, side, mouth.Depth);
            if (box == null)
                return Fail("kutu oluşturulamadı");
            var pieces = _ops.Subtract(box, body);
            if (pieces == null)
                return Fail("kutudan gövde çıkarılamadı");

            // Keep the space inside the hole; pieces past the edge of the part or in other openings stay out.
            double tol = hole.Bounds.LongestSide * 0.05 + 1e-6;
            var result = body;
            foreach (IBody2 piece in pieces)
            {
                var pb = BodyOps.Bounds(piece);
                bool inside = true;
                for (int a = 0; a < 3; a++)
                    inside &= pb.Min[a] >= hole.Bounds.Min[a] - tol && pb.Max[a] <= hole.Bounds.Max[a] + tol;
                if (!inside)
                    continue;
                result = _ops.Union(result, piece);
                if (result == null)
                    return Fail("tapa gövdeyle birleştirilemedi");
            }
            return result;
        }

        private IBody2 Fail(string reason)
        {
            Log.Add("Tapa: " + reason + ".");
            return null;
        }

        private List<Hole> FindHoles(IBody2 body, double maxWidth, out Dictionary<int, IFace2> faces,
            List<Hole> bosses = null, double maxBossHeight = 0)
        {
            faces = new Dictionary<int, IFace2>();
            var faceArray = (object[])body.GetFaces() ?? new object[0];
            for (int i = 0; i < faceArray.Length; i++)
            {
                var f = (IFace2)faceArray[i];
                f.SetFaceId(i + 1);
                faces[i + 1] = f;
            }
            var edgeArray = (object[])body.GetEdges() ?? new object[0];
            for (int i = 0; i < edgeArray.Length; i++)
                ((IEdge)edgeArray[i]).SetId(i + 1);
            if (_logging && faceArray.Length > 0 && edgeArray.Length > 0)
            {
                int fid = ((IFace2)faceArray[0]).GetFaceId(), eid = ((IEdge)edgeArray[0]).GetID();
                if (fid != 1 || eid != 1)
                    Log.Add($"Kimlikler ayarlanamadı (yüz {fid}, kenar {eid}).");
            }

            // For every face: its edges, and which of them lie on its inner loops (loop index per edge).
            var faceEdges = new Dictionary<int, List<IEdge>>();
            var innerLoopEdge = new HashSet<(int face, int edge)>();
            var innerLoops = new List<(int face, List<IEdge> edges)>();
            foreach (var kv in faces)
            {
                var edges = new List<IEdge>();
                foreach (ILoop2 loop in (object[])kv.Value.GetLoops() ?? new object[0])
                {
                    var loopEdges = ((object[])loop.GetEdges() ?? new object[0]).Cast<IEdge>().ToList();
                    edges.AddRange(loopEdges);
                    if (!loop.IsOuter())
                    {
                        innerLoops.Add((kv.Key, loopEdges));
                        foreach (var e in loopEdges)
                            innerLoopEdge.Add((kv.Key, e.GetID()));
                    }
                }
                faceEdges[kv.Key] = edges;
            }
            if (_logging)
                Log.Add($"{faces.Count} yüz, {edgeArray.Length} kenar, {innerLoops.Count} iç döngü.");

            int OtherFace(IEdge e, int face)
            {
                foreach (IFace2 f in (object[])e.GetTwoAdjacentFaces2() ?? new object[0])
                {
                    if (f == null) continue;
                    int id = f.GetFaceId();
                    if (id != face) return id;
                }
                return 0;
            }

            var holes = new List<Hole>();
            var seen = _seen = new HashSet<int>();
            foreach (var (host, loopEdges) in innerLoops)
            {
                var neighbours = loopEdges.Select(e => OtherFace(e, host)).Where(id => id > 0).Distinct().ToList();
                if (neighbours.Count == 0)
                {
                    if (_logging) Log.Add($"Yüz {host}: iç döngünün komşu yüzü bulunamadı.");
                    continue;
                }
                // The other mouth of a through hole that was already found from its first mouth.
                var seeds = neighbours.Where(id => !seen.Contains(id)).ToList();
                if (seeds.Count == 0)
                    continue;

                // Flood the hole faces; stop at faces that hold the hole edges on an inner loop (the faces around it).
                var hole = new Hole();
                var boundary = new List<(int face, int edge)>();
                var queue = new Queue<int>(seeds);
                var inHole = new HashSet<int>(seeds);
                bool bounded = true;
                while (queue.Count > 0 && bounded)
                {
                    int f = queue.Dequeue();
                    hole.FaceIds.Add(f);
                    if (hole.FaceIds.Count > MaxHoleFaces) { bounded = false; break; }
                    foreach (var e in faceEdges[f])
                    {
                        int other = OtherFace(e, f);
                        if (other == 0 || inHole.Contains(other))
                            continue;
                        if (innerLoopEdge.Contains((other, e.GetID())))
                        {
                            boundary.Add((other, e.GetID()));
                            continue;
                        }
                        if (innerLoopEdge.Contains((f, e.GetID())))
                        {
                            // The hole face itself has an inner loop here: a smaller hole inside, or a boss on it.
                            continue;
                        }
                        inHole.Add(other);
                        queue.Enqueue(other);
                    }
                }
                foreach (int f in inHole) seen.Add(f);
                if (!bounded || boundary.Count == 0)
                {
                    if (_logging) Log.Add($"Yüz {host}: delik sınırlanamadı ({hole.FaceIds.Count} yüz).");
                    continue;
                }

                if (!Measure(hole, boundary, innerLoops, faces, faceEdges))
                {
                    if (_logging) Log.Add($"Yüz {host}: ağız düzlemsel değil, ölçülmedi.");
                    continue;
                }
                if (_logging)
                    Log.Add($"Yüz {host}: {hole.FaceIds.Count} yüzlü {(hole.Depression ? "delik" : "çıkıntı")}, ağız {hole.Throat * 1000:F2} mm.");
                if (!hole.Depression && bosses != null && hole.Mouths.Count > 0 && hole.Mouths[0].Height <= maxBossHeight)
                    bosses.Add(hole);
                if (hole.Depression && hole.Throat <= maxWidth)
                    holes.Add(hole);
                else if (hole.Depression && _logging)
                    TooWide.Add(hole.Throat);
            }
            return holes;
        }

        /// <summary>Throat = narrowest boundary loop; only loops on planar faces are measured.</summary>
        private static bool Measure(Hole hole, List<(int face, int edge)> boundary, List<(int face, List<IEdge> edges)> innerLoops,
            Dictionary<int, IFace2> faces, Dictionary<int, List<IEdge>> faceEdges)
        {
            var holePoints = hole.FaceIds.SelectMany(f => faceEdges[f]).SelectMany(e => Sample(e)).ToList();
            hole.Bounds = Aabb.FromPoints(holePoints.ToArray());
            var boundaryEdges = new HashSet<(int, int)>(boundary);
            bool measured = false;
            foreach (var (face, edges) in innerLoops)
            {
                if (!edges.Any(e => boundaryEdges.Contains((face, e.GetID()))))
                    continue;
                var host = faces[face];
                var surface = (ISurface)host.GetSurface();
                if (surface == null || !surface.IsPlane())
                    return false;
                var n = (double[])host.Normal;
                var normal = new Vec3(n[0], n[1], n[2]).Normalized();
                var loop = edges.SelectMany(e => Sample(e)).ToList();
                hole.Throat = Math.Min(hole.Throat, LoopMetrics.MinWidth(loop, normal));
                var c = LoopMetrics.Centroid(loop);
                double depth = holePoints.Max(p => -Vec3.Dot(p - c, normal));
                double height = holePoints.Max(p => Vec3.Dot(p - c, normal));
                double radius = holePoints.Max(p => { var d = p - c; return (d - normal * Vec3.Dot(d, normal)).Length; });
                hole.Mouths.Add(new Mouth { Normal = normal, Centroid = c, Depth = depth, Height = height, Radius = radius });
                if (!measured)
                    hole.Depression = LoopMetrics.IsDepression(loop, normal, holePoints);
                measured = true;
            }
            return measured;
        }

        private static IEnumerable<Vec3> Sample(IEdge edge, int segments = 16)
        {
            var curve = (ICurve)edge.GetCurve();
            var range = edge.GetCurveParams3();
            if (curve == null || range == null)
                yield break;
            for (int i = 0; i <= segments; i++)
            {
                double t = range.UMinValue + (range.UMaxValue - range.UMinValue) * i / segments;
                var p = (double[])curve.Evaluate2(t, 0);
                if (p != null && p.Length >= 3)
                    yield return new Vec3(p[0], p[1], p[2]);
            }
        }
    }
}
