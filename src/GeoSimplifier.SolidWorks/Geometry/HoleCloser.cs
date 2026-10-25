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

        /// <summary>Counts the holes of a body that repeats one already closed; the caller adds its plugs.</summary>
        public void CountRepeat(int closed, int failed)
        {
            Closed += closed;
            Failed += failed;
        }

        /// <summary>Raised details (embossed text, symbols) flattened by <see cref="RemoveBosses"/>.</summary>
        public int BossesRemoved { get; private set; }

        /// <summary>What hole detection saw on the first pass of each body, for the summary and for debugging.</summary>
        public readonly List<string> Log = new List<string>();

        /// <summary>Openings that were found but are wider than the limit (meters).</summary>
        public readonly List<double> TooWide = new List<double>();

        /// <summary>The plugs that were (or, with <see cref="PlugsOnly"/>, would be) united with the bodies.</summary>
        public readonly List<IBody2> Plugs = new List<IBody2>();

        /// <summary>The width of the opening each of <see cref="Plugs"/> closes (meters).</summary>
        public readonly List<double> PlugWidths = new List<double>();

        /// <summary>
        /// For the quick preview: find the plugs but leave the bodies as they are, and use only the first mouth of a
        /// hole (it reaches through the hole), so the expensive unions with the body are skipped.
        /// </summary>
        public bool PlugsOnly;

        /// <summary>
        /// For the quick preview of large models: with <see cref="PlugsOnly"/>, the plug is just the box placed in
        /// the hole, not cut to the hole's shape (cutting it from a large body took most of a preview's time).
        /// </summary>
        public bool Rough;

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
            public List<IEdge> Edges; // the loop on the face around the hole
        }

        /// <summary>
        /// The mouths of the holes at most <paramref name="maxWidth"/> wide, each as a closed polygon in its plane
        /// (points along its edges, in order), with the plane's outward normal; for a quick preview that draws a
        /// lid on each mouth without any boolean. Only the first mouth of a through hole.
        /// </summary>
        public List<(List<Vec3> loop, Vec3 normal)> MouthLoops(IBody2 body, double maxWidth)
        {
            var result = new List<(List<Vec3>, Vec3)>();
            foreach (var hole in FindHoles(body, maxWidth, out _))
            {
                var mouth = hole.Mouths.FirstOrDefault();
                if (mouth?.Edges == null || mouth.Edges.Count == 0)
                    continue;
                var loop = Chain(mouth.Edges.Select(e => Sample(e, 12).ToList()).ToList());
                if (loop.Count >= 3)
                    result.Add((loop, mouth.Normal));
            }
            return result;
        }

        /// <summary>
        /// A thin lid on the mouth of every hole at most <paramref name="maxWidth"/> wide: a disc 0.3 mm thick just
        /// inside the mouth, as wide as the mouth reaches from its centre. No boolean is needed, so it is quick; for
        /// the preview. Only the first mouth of a through hole. (A sheet trimmed to the mouth's own edges crashed
        /// SOLIDWORKS.)
        /// </summary>
        /// <summary>Mouths <see cref="MouthCaps"/> could not draw.</summary>
        public int SkippedCaps { get; private set; }

        /// <summary>
        /// A thin plate under the mouth: the rectangle holding the loop in its plane, along the loop's main direction
        /// (from its spread), made as a flat sheet and thickened inward.
        /// </summary>
        private IBody2 Plate(List<Vec3> loop, Vec3 centre, Vec3 normal, double thickness)
        {
            // Main direction in the plane: of the in-plane offsets, the one the others spread along most.
            var flat = loop.Select(p => { var d = p - centre; return d - normal * Vec3.Dot(d, normal); }).ToList();
            double sxx = 0, sxy = 0, syy = 0;
            var a = Math.Abs(normal.X) < 0.9 ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0);
            a = (a - normal * Vec3.Dot(a, normal)).Normalized();
            var b = Vec3.Cross(normal, a);
            foreach (var d in flat)
            {
                double x = Vec3.Dot(d, a), y = Vec3.Dot(d, b);
                sxx += x * x; sxy += x * y; syy += y * y;
            }
            double angle = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
            var u = a * Math.Cos(angle) + b * Math.Sin(angle);
            var v = Vec3.Cross(normal, u);
            double u0 = flat.Min(d => Vec3.Dot(d, u)), u1 = flat.Max(d => Vec3.Dot(d, u));
            double v0 = flat.Min(d => Vec3.Dot(d, v)), v1 = flat.Max(d => Vec3.Dot(d, v));
            // Only a mouth that mostly fills its rectangle (a slot, a rectangular opening); an L-shaped or irregular
            // one would get a plate well beyond it.
            double area = 0;
            for (int i = 0; i < flat.Count; i++)
            {
                var p = flat[i];
                var q = flat[(i + 1) % flat.Count];
                area += Vec3.Dot(p, u) * Vec3.Dot(q, v) - Vec3.Dot(q, u) * Vec3.Dot(p, v);
            }
            if (Math.Abs(area) / 2 < 0.6 * (u1 - u0) * (v1 - v0))
            {
                SkippedCaps++;
                return null;
            }
            var modeler = (IModeler)_ops.Modeler;
            var plane = modeler.CreatePlanarSurface2(new[] { centre.X, centre.Y, centre.Z }, new[] { normal.X, normal.Y, normal.Z },
                                                     new[] { u.X, u.Y, u.Z }) as Surface;
            var sheet = plane == null ? null : modeler.CreateSheetFromSurface(plane, new[] { u0, u1, v0, v1 }) as IBody2;
            if (sheet == null)
            {
                SkippedCaps++;
                return null;
            }
            // Thickened to one side, into the hole (the sheet's normal is the mouth's, pointing out).
            return modeler.ThickenSheet((Body2)sheet, thickness, (int)swThickenDirection_e.swThickenDirection_Side2) as IBody2;
        }

        /// <summary>
        /// Flat surface lids for the holes of a surface body: every inner loop of a flat face at most
        /// <paramref name="maxWidth"/> wide gets a disc (round loops) or a rectangle along the loop's main
        /// direction (loops that mostly fill it), as a surface body, with the loop's points for drawing it as
        /// triangles. Imported surface models have no solid to plug, so the hole's walls stay as they are; they
        /// are hidden behind the lid.
        /// </summary>
        public List<(IBody2 lid, List<Vec3> outline, Vec3 normal)> SheetLids(IBody2 sheet, double maxWidth)
        {
            var lids = new List<(IBody2 lid, List<Vec3> outline, Vec3 normal)>();
            var modeler = (IModeler)_ops.Modeler;
            foreach (IFace2 face in (object[])sheet.GetFaces() ?? new object[0])
            {
                var surface = (ISurface)face.GetSurface();
                if (surface == null || !surface.IsPlane())
                    continue;
                var p = (double[])surface.PlaneParams;
                var normal = new Vec3(p[0], p[1], p[2]).Normalized();
                foreach (ILoop2 loop in (object[])face.GetLoops() ?? new object[0])
                {
                    if (loop.IsOuter())
                        continue;
                    var points = Chain(((object[])loop.GetEdges() ?? new object[0]).Cast<IEdge>().Select(e => Sample(e, 12).ToList()).ToList());
                    if (points.Count < 3)
                        continue;
                    var box = Aabb.FromPoints(points.ToArray());
                    var centre = (box.Min + box.Max) * 0.5;
                    // In-plane extent along the loop's main direction and across it.
                    var a = Math.Abs(normal.X) < 0.9 ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0);
                    a = (a - normal * Vec3.Dot(a, normal)).Normalized();
                    var b = Vec3.Cross(normal, a);
                    double sxx = 0, sxy = 0, syy = 0;
                    foreach (var q in points)
                    {
                        double x = Vec3.Dot(q - centre, a), y = Vec3.Dot(q - centre, b);
                        sxx += x * x; sxy += x * y; syy += y * y;
                    }
                    double angle = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
                    var u = a * Math.Cos(angle) + b * Math.Sin(angle);
                    var v = Vec3.Cross(normal, u);
                    double u0 = points.Min(q => Vec3.Dot(q - centre, u)), u1 = points.Max(q => Vec3.Dot(q - centre, u));
                    double v0 = points.Min(q => Vec3.Dot(q - centre, v)), v1 = points.Max(q => Vec3.Dot(q - centre, v));
                    if (Math.Min(u1 - u0, v1 - v0) > maxWidth || Math.Min(u1 - u0, v1 - v0) < 1e-5)
                        continue;
                    var reach = points.Select(q => { var d = q - centre; return (d - normal * Vec3.Dot(d, normal)).Length; }).ToList();
                    IBody2 lid = null;
                    List<Vec3> outline = null;
                    if (reach.Min() >= 0.92 * reach.Max())
                    {
                        // A disc: the end face of a thin cylinder, as a surface.
                        double radius = reach.Max();
                        var disc = _ops.SolidCylinder(centre, normal, radius, 1e-4);
                        lid = ((object[])disc?.GetFaces() ?? new object[0]).OfType<IFace2>()
                            .Where(f => ((ISurface)f.GetSurface())?.IsPlane() == true)
                            .Select(f => f.CreateSheetBody() as IBody2).FirstOrDefault(s => s != null);
                        outline = Enumerable.Range(0, 24).Select(k => centre + (u * Math.Cos(k * Math.PI / 12) + v * Math.Sin(k * Math.PI / 12)) * radius).ToList();
                    }
                    if (lid == null)
                    {
                        double area = 0;
                        for (int i = 0; i < points.Count; i++)
                        {
                            var q0 = points[i] - centre;
                            var q1 = points[(i + 1) % points.Count] - centre;
                            area += Vec3.Dot(q0, u) * Vec3.Dot(q1, v) - Vec3.Dot(q1, u) * Vec3.Dot(q0, v);
                        }
                        if (Math.Abs(area) / 2 < 0.6 * (u1 - u0) * (v1 - v0))
                        {
                            SkippedCaps++;
                            continue;
                        }
                        var plane = modeler.CreatePlanarSurface2(new[] { centre.X, centre.Y, centre.Z }, new[] { normal.X, normal.Y, normal.Z },
                                                                 new[] { u.X, u.Y, u.Z }) as Surface;
                        lid = plane == null ? null : modeler.CreateSheetFromSurface(plane, new[] { u0, u1, v0, v1 }) as IBody2;
                        outline = new List<Vec3> { centre + u * u0 + v * v0, centre + u * u1 + v * v0, centre + u * u1 + v * v1, centre + u * u0 + v * v1 };
                    }
                    if (lid == null)
                    {
                        SkippedCaps++;
                        continue;
                    }
                    lids.Add((lid, outline, normal));
                    Closed++;
                }
            }
            return lids;
        }

        public List<IBody2> MouthCaps(IBody2 body, double maxWidth)
        {
            const double thickness = 3e-4;
            var caps = new List<IBody2>();
            foreach (var hole in FindHoles(body, maxWidth, out _))
            {
                var mouth = hole.Mouths.FirstOrDefault();
                if (mouth?.Edges == null || mouth.Edges.Count == 0)
                    continue;
                var loop = mouth.Edges.SelectMany(edge => Sample(edge, 12)).ToList();
                if (loop.Count == 0)
                    continue;
                // The centre of the loop's box: the mouth's centroid leans toward where a closed edge starts (its
                // first point is sampled twice), which made round mouths look oval.
                var box = Aabb.FromPoints(loop.ToArray());
                var centre = (box.Min + box.Max) * 0.5;
                var reach = loop.Select(p => { var d = p - centre; return (d - mouth.Normal * Vec3.Dot(d, mouth.Normal)).Length; }).ToList();
                double radius = reach.Max();
                // Only round mouths get a disc; on a slot or an irregular opening a disc reaching its farthest point
                // stuck far out of the part (seen edge-on as long lines). Those are closed on OK only.
                if (radius < 1e-5)
                    continue;
                // A round mouth gets a disc. Any other (a slot, a rectangular opening) gets a thin plate over the
                // rectangle that holds it, turned along the mouth's own longest direction: a disc reaching its
                // farthest point stuck far out of the part (seen edge-on as long lines).
                var cap = reach.Min() >= 0.92 * radius
                    ? _ops.SolidCylinder(centre - mouth.Normal * (thickness / 2), mouth.Normal, radius, thickness)
                    : Plate(loop, centre, mouth.Normal, thickness);
                if (cap == null)
                    continue;
                // A lid covers a mouth of this body, so it lies within the body's box.
                var limit = BodyOps.AnyBounds(body);
                var cb = BodyOps.AnyBounds(cap);
                double m = Math.Max(5e-4, limit.LongestSide * 0.02);
                if (cb.Min.X < limit.Min.X - m || cb.Min.Y < limit.Min.Y - m || cb.Min.Z < limit.Min.Z - m ||
                    cb.Max.X > limit.Max.X + m || cb.Max.Y > limit.Max.Y + m || cb.Max.Z > limit.Max.Z + m)
                {
                    SkippedCaps++;
                    continue;
                }
                caps.Add(cap);
                Closed++;
            }
            return caps;
        }
        /// <summary>Joins the edges' point runs end to start, turning runs round where needed.</summary>
        private static List<Vec3> Chain(List<List<Vec3>> runs)
        {
            var loop = new List<Vec3>();
            var left = runs.Where(r => r.Count > 0).ToList();
            if (left.Count == 0)
                return loop;
            loop.AddRange(left[0]);
            left.RemoveAt(0);
            while (left.Count > 0)
            {
                var end = loop[loop.Count - 1];
                int best = 0;
                bool flip = false;
                double bestDistance = double.PositiveInfinity;
                for (int i = 0; i < left.Count; i++)
                {
                    double toStart = (left[i][0] - end).Length, toEnd = (left[i][left[i].Count - 1] - end).Length;
                    if (toStart < bestDistance) { bestDistance = toStart; best = i; flip = false; }
                    if (toEnd < bestDistance) { bestDistance = toEnd; best = i; flip = true; }
                }
                var run = left[best];
                if (flip) run.Reverse();
                loop.AddRange(run.Skip(1));
                left.RemoveAt(best);
            }
            return loop;
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
                foreach (var mouth in PlugsOnly ? hole.Mouths.Take(1) : hole.Mouths)
                {
                    var next = Fill(current, hole, mouth);
                    if (next == null) { ok = false; break; }
                    current = next;
                }
                if (ok) Closed++;
                else Failed++;
            }
            // Holes whose mouth runs across several faces (e.g. on a bend radius) are found from their walls.
            var walls = new WallHoleCloser(_ops, Log.Add) { PlugsOnly = PlugsOnly, Rough = Rough };
            current = walls.Close(body, current, maxWidth, planarFaces);
            Plugs.AddRange(walls.Plugs);
            PlugWidths.AddRange(walls.PlugWidths);
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
            if (PlugsOnly && Rough)
            {
                Plugs.Add(box);
                PlugWidths.Add(hole.Throat);
                return body;
            }
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
                Plugs.Add(piece);
                PlugWidths.Add(hole.Throat);
                if (PlugsOnly)
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
                hole.Mouths.Add(new Mouth { Normal = normal, Centroid = c, Depth = depth, Height = height, Radius = radius, Edges = edges });
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
