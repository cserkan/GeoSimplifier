using System;
using System.Collections.Generic;
using System.Linq;
using GeoSimplifier.Core.Geometry;
using SolidWorks.Interop.sldworks;

namespace GeoSimplifier.SolidWorks.Geometry
{
    /// <summary>
    /// Closes round holes found from their walls rather than from their mouths: holes whose mouth runs across
    /// several faces (for example a hole drilled on the edge between a flat face and a bend radius) are not an
    /// inner loop of any face, so <see cref="HoleCloser"/> does not see them.
    ///
    /// A hole is a group of coaxial cylinder and cone faces turned towards their axis that goes all the way
    /// round. The faces around it ("hosts") are grouped into mouths (hosts that touch each other). For each mouth
    /// the material side is built from simple solids: behind a plane, inside a convex cylinder, outside a concave
    /// one; a plane and a bend cylinder that meet tangentially are split at their tangent line. The plug is a
    /// box along the hole, cut by every mouth's material side, minus the body: the empty space of the hole,
    /// flush with the surfaces around each mouth.
    /// </summary>
    public sealed class WallHoleCloser
    {
        private readonly BodyOps _ops;
        private readonly Action<string> _log;

        public WallHoleCloser(BodyOps ops, Action<string> log)
        {
            _ops = ops;
            _log = log;
        }

        public int Closed { get; private set; }
        public int Failed { get; private set; }
        public readonly List<double> TooWide = new List<double>();

        /// <summary>The plugs found; see <see cref="HoleCloser.PlugsOnly"/>.</summary>
        public readonly List<IBody2> Plugs = new List<IBody2>();
        public readonly List<double> PlugWidths = new List<double>();
        public bool PlugsOnly;
        public bool Rough;

        private sealed class Hole
        {
            public readonly List<IFace2> Faces = new List<IFace2>();
            public Vec3 AxisPoint;
            public Vec3 Axis;
            public double Radius;
            public Aabb Bounds = Aabb.Empty;
            public readonly List<List<IFace2>> Mouths = new List<List<IFace2>>();
        }

        /// <summary>
        /// Finds wall holes in <paramref name="source"/>, whose faces carry IDs 1..n (faces whose IDs are in
        /// <paramref name="skip"/> belong to holes already handled), and plugs them into <paramref name="current"/>.
        /// </summary>
        public IBody2 Close(IBody2 source, IBody2 current, double maxWidth, ISet<int> skip)
        {
            _faces = ((object[])source.GetFaces() ?? new object[0]).Cast<IFace2>().ToDictionary(f => f.GetFaceId());
            foreach (var hole in Find(maxWidth, skip))
            {
                var next = Plug(current, hole);
                if (next != null)
                {
                    current = next;
                    Closed++;
                }
                else
                    Failed++;
            }
            return current;
        }

        private Dictionary<int, IFace2> _faces;

        private List<Hole> Find(double maxWidth, ISet<int> skip)
        {
            var faces = _faces.Values.ToList();
            var used = new HashSet<int>(skip);
            var holes = new List<Hole>();
            foreach (var seed in faces)
            {
                if (!TryAxis(seed, out var p0, out var d0, out double r0, cylinderOnly: true) || r0 > maxWidth)
                    continue;
                if (used.Contains(seed.GetFaceId()))
                {
                    if (Verbose) _log($"Duvar adayı {seed.GetFaceId()} (r {r0 * 1000:F1}): zaten işlendi.");
                    continue;
                }

                // Coaxial cylinders and cones next to each other form the wall (a hole is often split into halves).
                var hole = new Hole { AxisPoint = p0, Axis = d0, Radius = r0 };
                var queue = new Queue<IFace2>();
                queue.Enqueue(seed);
                used.Add(seed.GetFaceId());
                while (queue.Count > 0)
                {
                    var f = queue.Dequeue();
                    hole.Faces.Add(f);
                    foreach (var g in Neighbours(f))
                    {
                        if (used.Contains(g.GetFaceId()) || !TryAxis(g, out var p, out var d, out double r, cylinderOnly: false))
                            continue;
                        if (Math.Abs(Vec3.Dot(d, d0)) < 0.999 || DistanceToAxis(p, p0, d0) > 0.05 * r0)
                            continue;
                        used.Add(g.GetFaceId());
                        queue.Enqueue(g);
                        if (((ISurface)g.GetSurface()).IsCylinder())
                            hole.Radius = Math.Min(hole.Radius, r);
                    }
                }

                var points = hole.Faces.SelectMany(f => ((object[])f.GetEdges()).Cast<IEdge>()).SelectMany(Sample).ToList();
                double gap = AngularGap(hole.Faces.SelectMany(SampleSurface).ToList(), p0, d0);
                if (Verbose) _log($"Duvar adayı {seed.GetFaceId()} (r {r0 * 1000:F1}, {hole.Faces.Count} yüz): boşluk {gap:F0}°.");
                if (gap >= 100)
                    continue; // a fillet or a bend radius, not a hole
                // Bounds from the surfaces: the edge curves of a hole on a bend evaluate off the surface.
                hole.Bounds = Aabb.FromPoints(hole.Faces.SelectMany(SampleSurface).ToArray());
                if (2 * hole.Radius > maxWidth)
                {
                    TooWide.Add(2 * hole.Radius);
                    continue;
                }

                // Hosts: faces next to the wall; those touching each other make up one mouth.
                var wall = new HashSet<int>(hole.Faces.Select(f => f.GetFaceId()));
                var left = new HashSet<int>(hole.Faces.SelectMany(Neighbours).Select(g => g.GetFaceId()).Where(id => !wall.Contains(id)));
                while (left.Count > 0)
                {
                    var mouth = new List<IFace2>();
                    var q = new Queue<int>();
                    q.Enqueue(left.First());
                    left.Remove(left.First());
                    while (q.Count > 0)
                    {
                        var f = _faces[q.Dequeue()];
                        mouth.Add(f);
                        foreach (int g in Neighbours(f).Select(n => n.GetFaceId()).Where(left.Contains).Distinct().ToList())
                        {
                            left.Remove(g);
                            q.Enqueue(g);
                        }
                    }
                    hole.Mouths.Add(mouth);
                }
                _log($"Duvardan bulunan delik: {hole.Faces.Count} yüz, çap {2 * hole.Radius * 1000:F2} mm, " +
                     $"{hole.Mouths.Count} ağız ({string.Join(", ", hole.Mouths.Select(Describe))}).");
                holes.Add(hole);
            }
            return holes;
        }

        private IBody2 Plug(IBody2 body, Hole hole)
        {
            var size = hole.Bounds.Size;
            double reach = Math.Max(size.X, Math.Max(size.Y, size.Z));
            double big = 4 * reach + 0.01;

            // A box along the hole axis that covers the wall and a little more at both ends.
            double lo = double.PositiveInfinity, hi = double.NegativeInfinity, radial = 0;
            foreach (var p in BoundsCorners(hole.Bounds))
            {
                double t = Vec3.Dot(p - hole.AxisPoint, hole.Axis);
                lo = Math.Min(lo, t);
                hi = Math.Max(hi, t);
            }
            if (PlugsOnly && Rough)
            {
                // Quick preview: a box the size of the hole along its wall, not cut to the surfaces around it.
                var rough = _ops.BoxOnAxis(hole.AxisPoint + hole.Axis * lo, hole.Axis, 2 * hole.Radius, hi - lo);
                if (rough == null)
                    return Fail("kutu oluşturulamadı");
                Plugs.Add(rough);
                PlugWidths.Add(2 * hole.Radius);
                return body;
            }
            radial = hole.Radius * 1.5 + 1e-4;
            double margin = 0.25 * (hi - lo) + 1e-3;
            var region = new List<IBody2>
            {
                _ops.BoxOnAxis(hole.AxisPoint + hole.Axis * (lo - margin), hole.Axis, 2 * radial, hi - lo + 2 * margin),
            };
            if (region[0] == null)
                return Fail("kutu oluşturulamadı");

            var center = hole.AxisPoint + hole.Axis * ((lo + hi) / 2);
            foreach (var mouth in hole.Mouths)
            {
                var material = MaterialSide(mouth, center, big);
                if (material == null)
                    return Fail("ağız yüzeyi desteklenmiyor (" + Describe(mouth) + ")");
                var next = new List<IBody2>();
                foreach (var r in region)
                    foreach (var m in material)
                    {
                        var common = _ops.Intersect(r, m);
                        if (common == null)
                            return Fail("kesişim alınamadı");
                        next.AddRange(common.Cast<IBody2>());
                    }
                region = next;
            }

            // The region minus the body is the empty space of the hole; pieces outside the hole stay out.
            double tol = 0.05 * reach + 5e-4;
            IBody2 plug = null;
            foreach (var r in region)
            {
                var pieces = _ops.Subtract(r, body);
                if (pieces == null)
                    return Fail("gövde çıkarılamadı");
                foreach (IBody2 piece in pieces)
                {
                    var pb = BodyOps.Bounds(piece);
                    bool inside = true;
                    for (int a = 0; a < 3; a++)
                        inside &= pb.Min[a] >= hole.Bounds.Min[a] - tol && pb.Max[a] <= hole.Bounds.Max[a] + tol;
                    var mass = (double[])piece.GetMassProperties(1);
                    double volume = mass?[3] ?? 0;
                    if (Verbose)
                        _log($"  parça: hacim {volume * 1e9:F3} mm³, boyut {pb.Size.X * 1000:F2} x {pb.Size.Y * 1000:F2} x {pb.Size.Z * 1000:F2} mm, içeride {inside}");
                    if (!inside)
                        continue;
                    // The pieces (split at a bend's tangent line) are joined first, then the plug to the body.
                    plug = plug == null ? piece : _ops.Union(plug, piece) ?? _ops.Union(piece, plug);
                    if (plug == null)
                        return Fail("tapa parçaları birleştirilemedi");
                }
            }
            if (plug == null)
                return Fail("delik boşluğu bulunamadı");
            Plugs.Add(plug);
            PlugWidths.Add(2 * hole.Radius);
            if (PlugsOnly)
                return body;
            return _ops.Union(body, plug) ?? _ops.Union(plug, body) ?? Fail("tapa gövdeyle birleştirilemedi");
        }

        /// <summary>
        /// The material side of one mouth near <paramref name="center"/>, as solids whose union it is; null when a
        /// host face is of a kind that is not supported.
        /// </summary>
        private List<IBody2> MaterialSide(List<IFace2> mouth, Vec3 center, double big)
        {
            var planes = new List<(IFace2 face, Vec3 point, Vec3 normal)>();
            var cylinders = new List<(IFace2 face, Vec3 point, Vec3 axis, double radius, bool convex)>();
            foreach (var f in mouth)
            {
                var surface = (ISurface)f.GetSurface();
                if (surface.IsPlane())
                {
                    var n = (double[])f.Normal;
                    var normal = new Vec3(n[0], n[1], n[2]).Normalized();
                    var pp = (double[])surface.PlaneParams; // normal, root point
                    var root = new Vec3(pp[3], pp[4], pp[5]);
                    var sn = new Vec3(pp[0], pp[1], pp[2]).Normalized() * (f.FaceInSurfaceSense() ? -1 : 1);
                    if (Verbose)
                        _log($"  düzlem {f.GetFaceId()}: Normal ({normal.X:F2},{normal.Y:F2},{normal.Z:F2}), yüzeyden ({sn.X:F2},{sn.Y:F2},{sn.Z:F2}), kök z {root.Z * 1000:F2}");
                    planes.Add((f, center - normal * Vec3.Dot(center - root, normal), normal));
                }
                else if (surface.IsCylinder())
                {
                    var cp = (double[])surface.CylinderParams; // origin, axis, radius
                    var o = new Vec3(cp[0], cp[1], cp[2]);
                    var d = new Vec3(cp[3], cp[4], cp[5]).Normalized();
                    // The face normal points away from the axis (convex, material inside) unless the face is reversed.
                    cylinders.Add((f, o + d * Vec3.Dot(center - o, d), d, cp[6], !f.FaceInSurfaceSense()));
                }
                else
                    return null;
            }

            var parts = new List<IBody2>();
            foreach (var p in planes)
            {
                var solid = _ops.HalfSpace(p.point, p.normal * -1, big);
                var pieces = new List<IBody2> { solid };
                // Where a bend cylinder meets this plane tangentially, the plane only counts on its own side.
                foreach (var c in cylinders.Where(c => Touch(c.face, p.face)))
                    pieces = Cut(pieces, _ops.HalfSpace(c.point, SplitDirection(c, p.normal) * -1, big));
                if (pieces == null) return null;
                parts.AddRange(pieces);
            }
            foreach (var c in cylinders)
            {
                var solid = _ops.SolidCylinder(c.point, c.axis, c.radius, big);
                if (solid == null) return null;
                var pieces = new List<IBody2>();
                if (c.convex)
                    pieces.Add(solid);
                else
                {
                    var outside = _ops.Subtract(_ops.Box(new Aabb(center - new Vec3(big, big, big), center + new Vec3(big, big, big))), solid);
                    if (outside == null) return null;
                    pieces.AddRange(outside.Cast<IBody2>());
                }
                foreach (var p in planes.Where(p => Touch(c.face, p.face)))
                    pieces = Cut(pieces, _ops.HalfSpace(c.point, SplitDirection(c, p.normal), big));
                if (pieces == null) return null;
                parts.AddRange(pieces);
            }
            return parts;
        }

        /// <summary>
        /// Unit vector across the bend axis, perpendicular to the plane's normal, pointing from the plane's side
        /// of the tangent line towards the cylinder face.
        /// </summary>
        private static Vec3 SplitDirection((IFace2 face, Vec3 point, Vec3 axis, double radius, bool convex) c, Vec3 planeNormal)
        {
            var s = Vec3.Cross(c.axis, planeNormal).Normalized();
            var box = (double[])c.face.GetBox();
            var mid = new Vec3((box[0] + box[3]) / 2, (box[1] + box[4]) / 2, (box[2] + box[5]) / 2);
            return Vec3.Dot(mid - c.point, s) < 0 ? s * -1 : s;
        }

        private List<IBody2> Cut(List<IBody2> pieces, IBody2 halfSpace)
        {
            if (pieces == null || halfSpace == null)
                return null;
            var result = new List<IBody2>();
            foreach (var piece in pieces)
            {
                var common = _ops.Intersect(piece, halfSpace);
                if (common == null)
                    return null;
                result.AddRange(common.Cast<IBody2>());
            }
            return result;
        }

        private IBody2 Fail(string reason)
        {
            _log("Duvar deliği tapası: " + reason + ".");
            return null;
        }

        private static bool Touch(IFace2 a, IFace2 b) => Neighbours(a).Any(n => n.GetFaceId() == b.GetFaceId());

        private static IEnumerable<IFace2> Neighbours(IFace2 face)
        {
            int id = face.GetFaceId();
            foreach (IEdge e in (object[])face.GetEdges() ?? new object[0])
                foreach (IFace2 g in (object[])e.GetTwoAdjacentFaces2() ?? new object[0])
                    if (g != null && g.GetFaceId() != id)
                        yield return g;
        }

        /// <summary>Axis of a cylinder (or, unless <paramref name="cylinderOnly"/>, a cone) face turned towards its axis.</summary>
        private static bool TryAxis(IFace2 face, out Vec3 point, out Vec3 axis, out double radius, bool cylinderOnly)
        {
            point = axis = new Vec3(0, 0, 0);
            radius = 0;
            var surface = (ISurface)face.GetSurface();
            double[] p;
            if (surface.IsCylinder())
                p = (double[])surface.CylinderParams;
            else if (!cylinderOnly && surface.IsCone())
                p = (double[])surface.ConeParams;
            else
                return false;
            // Reversed faces of a cylinder look towards the axis: the wall of a hole, not of a boss.
            if (surface.IsCylinder() && !face.FaceInSurfaceSense())
                return false;
            point = new Vec3(p[0], p[1], p[2]);
            axis = new Vec3(p[3], p[4], p[5]).Normalized();
            radius = p[6];
            return true;
        }

        private static double DistanceToAxis(Vec3 p, Vec3 origin, Vec3 axis)
        {
            var d = p - origin;
            return (d - axis * Vec3.Dot(d, axis)).Length;
        }

        /// <summary>Logs every candidate wall (for the probe).</summary>
        public static bool Verbose;

        /// <summary>
        /// Largest angle (degrees) around the axis not covered by points on the wall: small for the wall of a
        /// hole, which goes all the way round, large for a fillet or a bend.
        /// </summary>
        private static double AngularGap(List<Vec3> points, Vec3 origin, Vec3 axis)
        {
            var helper = Math.Abs(axis.X) < 0.9 ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0);
            var u = Vec3.Cross(axis, helper).Normalized();
            var v = Vec3.Cross(axis, u).Normalized();
            var angles = points.Select(p => { var d = p - origin; return Math.Atan2(Vec3.Dot(d, v), Vec3.Dot(d, u)); })
                               .OrderBy(a => a).ToList();
            if (angles.Count < 3)
                return 360;
            double gap = angles[0] + 2 * Math.PI - angles[angles.Count - 1];
            for (int i = 1; i < angles.Count; i++)
                gap = Math.Max(gap, angles[i] - angles[i - 1]);
            return gap * 180 / Math.PI;
        }

        private static IEnumerable<Vec3> BoundsCorners(Aabb b)
        {
            for (int i = 0; i < 8; i++)
                yield return new Vec3((i & 1) == 0 ? b.Min.X : b.Max.X, (i & 2) == 0 ? b.Min.Y : b.Max.Y, (i & 4) == 0 ? b.Min.Z : b.Max.Z);
        }

        private static string Describe(List<IFace2> mouth) => string.Join("+", mouth.Select(f =>
        {
            var s = (ISurface)f.GetSurface();
            return s.IsPlane() ? "düzlem" : s.IsCylinder() ? (f.FaceInSurfaceSense() ? "iç silindir" : "dış silindir") : s.IsCone() ? "koni" : "diğer";
        }));

        /// <summary>
        /// Points over the face's parameter range. Edge curves are no good for this: the intersection curve of a
        /// hole with a bend evaluates off the surface.
        /// </summary>
        private static IEnumerable<Vec3> SampleSurface(IFace2 face)
        {
            var uv = (double[])face.GetUVBounds();
            var surface = (ISurface)face.GetSurface();
            if (uv == null || surface == null)
                yield break;
            for (int i = 0; i <= 24; i++)
                for (int j = 0; j <= 4; j++)
                {
                    double u = uv[0] + (uv[1] - uv[0]) * i / 24, v = uv[2] + (uv[3] - uv[2]) * j / 4;
                    var p = (double[])surface.Evaluate(u, v, 0, 0);
                    if (p != null && p.Length >= 3)
                        yield return new Vec3(p[0], p[1], p[2]);
                }
            // The parameters may run the other way round (v around the axis).
            for (int i = 0; i <= 4; i++)
                for (int j = 0; j <= 24; j++)
                {
                    double u = uv[0] + (uv[1] - uv[0]) * i / 4, v = uv[2] + (uv[3] - uv[2]) * j / 24;
                    var p = (double[])surface.Evaluate(u, v, 0, 0);
                    if (p != null && p.Length >= 3)
                        yield return new Vec3(p[0], p[1], p[2]);
                }
        }

        private static IEnumerable<Vec3> Sample(IEdge edge)
        {
            var curve = (ICurve)edge.GetCurve();
            var range = edge.GetCurveParams3();
            if (curve == null || range == null)
                yield break;
            for (int i = 0; i <= 16; i++)
            {
                double t = range.UMinValue + (range.UMaxValue - range.UMinValue) * i / 16;
                var p = (double[])curve.Evaluate2(t, 0);
                if (p != null && p.Length >= 3)
                    yield return new Vec3(p[0], p[1], p[2]);
            }
        }
    }
}
