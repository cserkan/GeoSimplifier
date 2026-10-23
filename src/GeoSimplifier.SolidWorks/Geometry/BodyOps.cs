using System;
using System.Collections.Generic;
using System.Linq;
using GeoSimplifier.Core.Geometry;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace GeoSimplifier.SolidWorks.Geometry
{
    /// <summary>Exact boolean and offset operations on temporary bodies.</summary>
    public sealed class BodyOps
    {
        private readonly BoxFactory _boxes;
        private readonly IModeler _modeler;
        private readonly IMathUtility _math;
        private double? _cylinderBaseFactor;
        private bool? _outwardFlag;

        public BodyOps(ISldWorks swApp)
        {
            _modeler = (IModeler)swApp.GetModeler();
            _math = (IMathUtility)swApp.GetMathUtility();
            _boxes = new BoxFactory(_modeler);
        }

        /// <inheritdoc cref="BoxFactory.CreateOnAxis"/>
        public IBody2 BoxOnAxis(Vec3 baseCenter, Vec3 axis, double side, double height) =>
            _boxes.CreateOnAxis(baseCenter, axis, side, height);

        public IBody2 Box(Aabb box) => _boxes.Create(box);

        /// <summary>
        /// A large box standing on <paramref name="point"/> and reaching <paramref name="size"/> along
        /// <paramref name="inward"/>: locally, the half-space behind a plane through the point.
        /// </summary>
        public IBody2 HalfSpace(Vec3 point, Vec3 inward, double size) => _boxes.CreateOnAxis(point, inward, size, size);

        /// <summary>A solid cylinder about the axis through <paramref name="center"/>, reaching length/2 to each side of it.</summary>
        public IBody2 SolidCylinder(Vec3 center, Vec3 axis, double radius, double length)
        {
            var a = axis.Normalized();
            if (_cylinderBaseFactor == null)
            {
                // Whether CreateBodyFromCyl's point is the base or the middle of the cylinder is measured once.
                var test = (IBody2)_modeler.CreateBodyFromCyl(new[] { 0.0, 0, 0, 0, 0, 1, 1, 2 });
                var bb = (double[])test?.GetBodyBox();
                if (bb == null)
                    return null;
                _cylinderBaseFactor = bb[2] / 2; // 0 when the point is the base, -0.5 when it is the middle
            }
            var start = center - a * (length / 2) - a * (_cylinderBaseFactor.Value * length);
            return (IBody2)_modeler.CreateBodyFromCyl(new[] { start.X, start.Y, start.Z, a.X, a.Y, a.Z, radius, length });
        }

        /// <summary>Surface triangles of the bodies (three points each), from SOLIDWORKS' tessellation.</summary>
        public static List<Vec3> Triangles(IEnumerable<IBody2> bodies)
        {
            var points = new List<Vec3>();
            foreach (var body in bodies)
            {
                foreach (IFace2 face in (object[])body.GetFaces() ?? new object[0])
                {
                    if (!(face.GetTessTriangles(true) is float[] t))
                        continue;
                    for (int i = 0; i + 8 < t.Length; i += 9)
                    {
                        points.Add(new Vec3(t[i], t[i + 1], t[i + 2]));
                        points.Add(new Vec3(t[i + 3], t[i + 4], t[i + 5]));
                        points.Add(new Vec3(t[i + 6], t[i + 7], t[i + 8]));
                    }
                }
                if (points.Count > 0)
                    continue;
                // Temporary bodies may have no display tessellation; tessellate them explicitly.
                var tess = (ITessellation)body.GetTessellation(body.GetFaces());
                if (tess == null || !tess.Tessellate())
                    continue;
                for (int f = 0; f < tess.GetFacetCount(); f++)
                {
                    var corners = new List<int>();
                    foreach (int fin in (int[])tess.GetFacetFins(f))
                        foreach (int v in (int[])tess.GetFinVertices(fin))
                            if (!corners.Contains(v)) corners.Add(v);
                    if (corners.Count < 3)
                        continue;
                    for (int c = 0; c < 3; c++)
                    {
                        var p = (double[])tess.GetVertexPoint(corners[c]);
                        points.Add(new Vec3(p[0], p[1], p[2]));
                    }
                }
            }
            return points;
        }

        /// <summary>
        /// Unites many bodies (e.g. touching boxes) in pairs, round after round, so each boolean stays small;
        /// returns the separate lumps that remain.
        /// </summary>
        public List<IBody2> UnionMany(List<IBody2> bodies)
        {
            // Neighbours in space are next to each other in Morton (Z-curve) order of the box centres, so most
            // pairs touch; each body is united with the first of the next few whose box touches its own.
            var all = bodies.Where(b => b != null).Select(b => (body: b, box: Bounds(b))).Where(e => !e.box.IsEmpty).ToList();
            if (all.Count == 0)
                return new List<IBody2>();
            var extent = all.Select(e => e.box).Aggregate((a, b) => a.Union(b));
            ulong Morton(Aabb b)
            {
                var c = (b.Min + b.Max) * 0.5;
                ulong code = 0;
                var q = new ulong[3];
                for (int a = 0; a < 3; a++)
                    q[a] = (ulong)(1023 * (c[a] - extent.Min[a]) / Math.Max(extent.Size[a], 1e-9));
                for (int bit = 0; bit < 10; bit++)
                    for (int a = 0; a < 3; a++)
                        code |= ((q[a] >> bit) & 1) << (3 * bit + a);
                return code;
            }
            var current = all.OrderBy(e => Morton(e.box)).ToList();
            for (int round = 0; round < 64 && current.Count > 1; round++)
            {
                var next = new List<(IBody2 body, Aabb box)>();
                var taken = new bool[current.Count];
                for (int i = 0; i < current.Count; i++)
                {
                    if (taken[i]) continue;
                    taken[i] = true;
                    int partner = -1;
                    for (int j = i + 1; j < Math.Min(current.Count, i + 8) && partner < 0; j++)
                        if (!taken[j] && Overlaps(current[i].box, current[j].box, 1e-7))
                            partner = j;
                    if (partner < 0) { next.Add(current[i]); continue; }
                    taken[partner] = true;
                    var output = Boolean(swBodyOperationType_e.SWBODYADD, current[i].body, current[partner].body)?.OfType<IBody2>().ToList();
                    if (output == null || output.Count == 0) { next.Add(current[i]); next.Add(current[partner]); continue; }
                    next.AddRange(output.Select(o => (o, Bounds(o))).Where(e => !e.Item2.IsEmpty));
                }
                bool progress = next.Count < current.Count;
                current = next.OrderBy(e => Morton(e.box)).ToList();
                if (!progress)
                    break;
            }
            return current.Select(e => e.body).ToList();
        }

        /// <summary>The common part of a and b; null when the operation fails, empty when they do not overlap.</summary>
        public object[] Intersect(IBody2 a, IBody2 b) => Boolean(swBodyOperationType_e.SWBODYINTERSECT, a, b);

        /// <summary>Boolean operations SOLIDWORKS rejected.</summary>
        public int FailedBooleans { get; private set; }

        public static Aabb Bounds(IBody2 body)
        {
            // A body SOLIDWORKS could not build properly has no box; it counts as empty.
            var bb = (double[])body?.GetBodyBox();
            if (bb == null || bb.Length < 6)
                return Aabb.Empty;
            return new Aabb(new Vec3(bb[0], bb[1], bb[2]), new Vec3(bb[3], bb[4], bb[5]));
        }

        /// <summary>
        /// Turns open surface bodies into thin solids so the wrap goes over them like any other part; they are
        /// not sewn. Surfaces SOLIDWORKS cannot thicken are counted in <paramref name="failed"/>.
        /// </summary>
        public List<IBody2> ThickenSheets(List<IBody2> sheets, double thickness, out int failed)
        {
            var solids = new List<IBody2>();
            failed = 0;
            foreach (var sheet in sheets)
            {
                var solid = _modeler.ThickenSheet((Body2)sheet, thickness, (int)swThickenDirection_e.swThickenDirection_Both);
                if (solid != null)
                    solids.Add(solid);
                else
                    failed++;
            }
            return solids;
        }

        /// <summary>
        /// Bridges lumps without offsets (MakeOffset fails on some installations), like a bag pulled over them:
        /// for two lumps whose boxes are at most <paramref name="gapWidth"/> apart along some axis, the space between
        /// them within the overlap of their outlines is filled, keeping only pieces that touch both lumps.
        /// </summary>
        public List<IBody2> BridgeByBoxes(List<IBody2> lumps, double gapWidth, double tolerance, out int bridges)
        {
            bridges = 0;
            var current = new List<IBody2>(lumps);
            for (int round = 0; round < 50 && current.Count > 1; round++)
            {
                bool merged = false;
                for (int i = 0; i < current.Count && !merged; i++)
                    for (int j = i + 1; j < current.Count && !merged; j++)
                    {
                        var bridge = BoxBridge(current[i], current[j], gapWidth, tolerance);
                        if (bridge == null)
                            continue;
                        var joined = Union(Union(current[i], bridge) ?? current[i], current[j]);
                        if (joined == null)
                            continue;
                        current[i] = joined;
                        current.RemoveAt(j);
                        bridges++;
                        merged = true;
                    }
                if (!merged)
                    break;
            }
            return current;
        }

        /// <summary>The exact extents of a body (GetBodyBox may be a little loose), from its extreme points.</summary>
        public static Aabb TightBounds(IBody2 body)
        {
            var min = new double[3];
            var max = new double[3];
            for (int a = 0; a < 3; a++)
            {
                var d = new double[3];
                d[a] = 1;
                if (!body.GetExtremePoint(d[0], d[1], d[2], out double x, out double y, out double z))
                    return Bounds(body);
                max[a] = new[] { x, y, z }[a];
                d[a] = -1;
                if (!body.GetExtremePoint(d[0], d[1], d[2], out x, out y, out z))
                    return Bounds(body);
                min[a] = new[] { x, y, z }[a];
            }
            return new Aabb(new Vec3(min[0], min[1], min[2]), new Vec3(max[0], max[1], max[2]));
        }

        /// <summary>
        /// The inside of a part that its neighbours close off: a terminal box between its lid and the housing, a
        /// fan cover against the housing, a housing between its end shields. The part's own bounding box minus the
        /// part gives the air around and inside it; a piece that reaches at most two sides of the box, each of
        /// which another part covers just outside, is an inside, cut off flat in the plane of the part's rim.
        /// Air around the part reaches more sides, and an opening nothing covers stays open. Exact: the pieces'
        /// faces are the part's own faces and the flat mouths.
        /// </summary>
        public List<IBody2> CoveredInsides(IBody2 part, IList<IBody2> others, double tolerance)
        {
            var result = new List<IBody2>();
            var bb = TightBounds(part);
            if (bb.IsEmpty || bb.Size.X < tolerance || bb.Size.Y < tolerance || bb.Size.Z < tolerance)
                return result;
            // The box is a hair smaller than the part: with its faces in the planes of the part's outermost faces
            // the subtraction fails (coincident faces); the mouths end up a few microns below the rim.
            double eps = Math.Max(1e-6, bb.LongestSide * 1e-5);
            var inner = new Aabb(bb.Min + new Vec3(eps, eps, eps), bb.Max - new Vec3(eps, eps, eps));
            var pieces = Subtract(Box(inner), part);
            if (pieces == null)
                return result;
            // The neighbours inside the box are taken out too: a cable gland closes the hole it sits in, a terminal
            // block takes up room inside the box.
            var around = new List<IBody2>(pieces.OfType<IBody2>());
            foreach (var other in others)
            {
                if (ReferenceEquals(other, part) || !Overlaps(inner, Bounds(other), 0))
                    continue;
                var next = new List<IBody2>();
                foreach (var piece in around)
                {
                    var cut = Subtract(piece, other);
                    if (cut == null) { next.Add(piece); continue; }
                    next.AddRange(cut.OfType<IBody2>());
                }
                around = next;
            }
            double slab = Math.Max(5e-4, bb.LongestSide * 0.01);
            foreach (var piece in around)
            {
                var pb = Bounds(piece);
                if (pb.IsEmpty)
                    continue;
                var sides = new List<(int axis, int dir)>();
                for (int a = 0; a < 3; a++)
                {
                    if (pb.Min[a] <= inner.Min[a] + eps) sides.Add((a, -1));
                    if (pb.Max[a] >= inner.Max[a] - eps) sides.Add((a, 1));
                }
                if (sides.Count > 2)
                    continue;
                if (Trace)
                    UI.PageLog.Write($"  iç aday: parça kutusu {bb.Size.X * 1000:F0}x{bb.Size.Y * 1000:F0}x{bb.Size.Z * 1000:F0} mm, " +
                                     $"hava {Volume(piece):F0} mm³, kenarlar {string.Join(" ", sides.Select(sd => (sd.dir > 0 ? "+" : "-") + "XYZ"[sd.axis]))}");
                bool covered = true;
                foreach (var (a, dir) in sides)
                {
                    // A thin slab just outside the mouth, over the piece's outline: another part must be in it.
                    var min = pb.Min;
                    var max = pb.Max;
                    var lo = new double[] { min.X, min.Y, min.Z };
                    var hi = new double[] { max.X, max.Y, max.Z };
                    if (dir > 0) { lo[a] = bb.Max[a] + eps; hi[a] = bb.Max[a] + slab; }
                    else { lo[a] = bb.Min[a] - slab; hi[a] = bb.Min[a] - eps; }
                    var region = new Aabb(new Vec3(lo[0], lo[1], lo[2]), new Vec3(hi[0], hi[1], hi[2]));
                    bool any = false;
                    foreach (var other in others)
                    {
                        if (ReferenceEquals(other, part) || !Overlaps(region, Bounds(other), 0))
                            continue;
                        var common = Intersect(Box(region), other);
                        if (common != null && common.Length > 0) { any = true; break; }
                    }
                    if (!any) { covered = false; break; }
                }
                if (Trace)
                    UI.PageLog.Write($"    örtülü: {covered}");
                if (covered)
                    result.Add(piece);
            }
            return result;
        }

        /// <summary>The material that joins <paramref name="a"/> and <paramref name="b"/>, or null when they are too far apart.</summary>
        private IBody2 BoxBridge(IBody2 a, IBody2 b, double gapWidth, double tolerance)
        {
            Aabb ba = Bounds(a), bb = Bounds(b);
            var min = new double[3];
            var max = new double[3];
            int separated = 0;
            for (int k = 0; k < 3; k++)
            {
                double lo = Math.Max(ba.Min[k], bb.Min[k]), hi = Math.Min(ba.Max[k], bb.Max[k]);
                if (lo > hi)
                {
                    // Apart along this axis: span both bodies so the faces that look at each other are reached.
                    if (lo - hi > gapWidth)
                        return null;
                    separated++;
                    min[k] = Math.Min(ba.Min[k], bb.Min[k]);
                    max[k] = Math.Max(ba.Max[k], bb.Max[k]);
                }
                else
                {
                    if (hi - lo < tolerance)
                        return null; // outlines only touch at an edge
                    min[k] = lo;
                    max[k] = hi;
                }
            }
            // Boxes that overlap on every axis are interleaved bodies, not a gap a bag would span.
            if (separated == 0)
                return null;

            var region = Box(new Aabb(new Vec3(min[0], min[1], min[2]), new Vec3(max[0], max[1], max[2])));
            if (region == null)
                return null;
            var pieces = new List<IBody2> { region };
            foreach (var body in new[] { a, b })
            {
                var next = new List<IBody2>();
                foreach (var p in pieces)
                {
                    var cut = Subtract(p, body);
                    if (cut == null) { next.Add(p); continue; }
                    foreach (IBody2 q in cut) next.Add(q);
                }
                pieces = next;
            }

            IBody2 bridge = null;
            foreach (var piece in pieces)
            {
                if (Union(piece, a) == null || Union(piece, b) == null)
                    continue;
                bridge = bridge == null ? piece : Union(bridge, piece) ?? bridge;
            }
            return bridge;
        }

        /// <summary>Unions every pair of bodies that touch or overlap. Returns one body per separate lump.</summary>
        public List<IBody2> UnionAll(IEnumerable<IBody2> bodies, double tolerance)
        {
            var lumps = new List<IBody2>();
            var boxes = new List<Aabb>();
            foreach (var body in bodies)
            {
                var current = body;
                var box = Bounds(body);
                if (box.IsEmpty)
                {
                    // No box to compare: keep the body as it is rather than lose it (this once dropped a whole model).
                    if (body != null)
                    {
                        lumps.Add(body);
                        boxes.Add(Aabb.Empty);
                        if (Trace) UI.PageLog.Write("  kutusu olmayan gövde ayrı tutuldu");
                    }
                    continue;
                }
                for (int n = lumps.Count - 1; n >= 0; n--)
                {
                    if (!Overlaps(boxes[n], box, tolerance))
                        continue;
                    var merged = Union(lumps[n], current);
                    if (merged == null)
                        continue;
                    current = merged;
                    box = box.Union(boxes[n]);
                    lumps.RemoveAt(n);
                    boxes.RemoveAt(n);
                }
                lumps.Add(current);
                boxes.Add(box);
            }
            return lumps;
        }

        /// <returns>The single merged body, or null when the bodies stay separate or the union fails.</returns>
        /// <summary>Logs failing booleans to the page log (for finding why bodies stay apart).</summary>
        public bool Trace;

        private static double Volume(IBody2 b) => (((double[])b?.GetMassProperties(1))?[3] ?? 0) * 1e9;

                public IBody2 Union(IBody2 a, IBody2 b)
        {
            var output = Boolean(swBodyOperationType_e.SWBODYADD, a, b);
            if (output == null && _math != null)
            {
                // A union that fails outright is usually two bodies sharing faces exactly (a gland's thread in its
                // threaded hole); moved by 2 microns they overlap instead, and that SOLIDWORKS can unite.
                var moved = (IBody2)b.Copy();
                var shift = (MathTransform)_math.CreateTransform(new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, 1.2e-6, 1.1e-6, 1.3e-6, 1, 0, 0, 0 });
                if (moved != null && shift != null && moved.ApplyTransform(shift))
                    output = Boolean(swBodyOperationType_e.SWBODYADD, a, moved);
            }
            if (Trace && (output == null || output.Length != 1))
                UI.PageLog.Write($"  birleşim: {(output == null ? "hata" : output.Length + " gövde")} " +
                                 $"(a {Volume(a):F0}, b {Volume(b):F0} mm³ → {(output == null ? "-" : string.Join(", ", output.OfType<IBody2>().Select(o => Volume(o).ToString("F0"))))})");
            return output != null && output.Length == 1 ? (IBody2)output[0] : null;
        }

        /// <summary>a minus b; null when the operation fails, empty when nothing is left.</summary>
        public object[] Subtract(IBody2 a, IBody2 b) => Boolean(swBodyOperationType_e.SWBODYCUT, a, b);

        private object[] Boolean(swBodyOperationType_e type, IBody2 a, IBody2 b)
        {
            // Boolean operations may modify their inputs, so operate on copies and keep the originals intact.
            var target = (IBody2)a?.Copy();
            var tool = (IBody2)b?.Copy();
            if (target == null || tool == null)
            {
                FailedBooleans++;
                return null;
            }
            var output = (object[])target.Operations2((int)type, tool, out int error);
            if (error != 0)
            {
                FailedBooleans++;
                if (Trace) UI.PageLog.Write($"  boolean {type} hatası {error}");
                return null;
            }
            return output ?? new object[0];
        }

        /// <summary>Pieces of <paramref name="result"/> that are not in any of <paramref name="original"/>.</summary>
        public List<IBody2> Difference(IEnumerable<IBody2> result, IEnumerable<IBody2> original)
        {
            var pieces = new List<IBody2>(result);
            foreach (var o in original)
            {
                var ob = Bounds(o);
                var next = new List<IBody2>();
                foreach (var p in pieces)
                {
                    if (!Overlaps(Bounds(p), ob, 0)) { next.Add(p); continue; }
                    var cut = Subtract(p, o);
                    if (cut == null) continue;
                    foreach (IBody2 c in cut) next.Add(c);
                }
                pieces = next;
            }
            return pieces;
        }

        /// <summary>
        /// Fills closed internal cavities exactly: subtract the body from a box around it; every piece of the
        /// remainder except the one touching the box is a cavity, and is added back.
        /// </summary>
        public IBody2 FillCavities(IBody2 body)
        {
            var bounds = Bounds(body);
            double margin = Math.Max(bounds.LongestSide * 0.1, 1e-3);
            var grow = new Vec3(margin, margin, margin);
            var box = _boxes.Create(new Aabb(bounds.Min - grow, bounds.Max + grow));

            var pieces = Subtract(box, body);
            if (pieces == null || pieces.Length <= 1)
                return body;

            var result = body;
            foreach (IBody2 piece in pieces)
            {
                // The outside piece reaches into the margin around the body; cavities stay within its bounds.
                var pb = Bounds(piece);
                bool outside = false;
                for (int a = 0; a < 3; a++)
                    outside |= pb.Min[a] < bounds.Min[a] - margin * 0.5 || pb.Max[a] > bounds.Max[a] + margin * 0.5;
                if (outside)
                    continue;
                result = Union(result, piece) ?? result;
            }
            return result;
        }

        /// <summary>
        /// Bridges separate lumps closer than <paramref name="gapWidth"/> exactly: grow every lump by half the gap,
        /// union, shrink back (a closing), then keep only the added pieces that touch two or more lumps. Pieces
        /// that touch a single lump would only round off its concave edges, so they are dropped and the
        /// original detail stays.
        /// </summary>
        /// <returns>The bridged lumps, or null when SOLIDWORKS could not offset the geometry.</returns>
        public List<IBody2> Bridge(List<IBody2> lumps, double gapWidth, double tolerance)
        {
            double d = gapWidth / 2;
            var grown = new List<IBody2>();
            foreach (var lump in lumps)
            {
                var g = Offset(lump, d, outward: true);
                if (g == null)
                    return null;
                grown.Add(g);
            }

            var closed = new List<IBody2>();
            foreach (var g in UnionAll(grown, tolerance))
            {
                var shrunk = Offset(FillCavities(g), d, outward: false);
                if (shrunk == null)
                    return null;
                closed.Add(shrunk);
            }

            var bridges = new List<IBody2>();
            foreach (var c in closed)
            {
                var remainder = new List<IBody2> { c };
                foreach (var lump in lumps)
                {
                    var next = new List<IBody2>();
                    foreach (var r in remainder)
                    {
                        var cut = Subtract(r, lump);
                        if (cut == null) { next.Add(r); continue; }
                        foreach (IBody2 piece in cut) next.Add(piece);
                    }
                    remainder = next;
                }
                foreach (var piece in remainder)
                {
                    int touching = 0;
                    var pb = Bounds(piece);
                    foreach (var lump in lumps)
                        if (Overlaps(pb, Bounds(lump), tolerance) && Union(piece, lump) != null)
                            touching++;
                    if (touching >= 2)
                        bridges.Add(piece);
                }
            }

            var all = new List<IBody2>(lumps);
            all.AddRange(bridges);
            return UnionAll(all, tolerance);
        }

        /// <summary>Solid offset. Which value of MakeOffset's direction flag grows the body is detected once.</summary>
        public IBody2 Offset(IBody2 body, double distance, bool outward)
        {
            if (_outwardFlag == null)
            {
                var test = body.MakeOffset(distance, true);
                if (test == null)
                    return null;
                _outwardFlag = Bounds(test).LongestSide > Bounds(body).LongestSide;
                if (outward == _outwardFlag)
                    return test;
            }
            return body.MakeOffset(distance, outward == _outwardFlag.Value);
        }

        public static bool Overlaps(Aabb a, Aabb b, double tol) =>
            a.Min.X <= b.Max.X + tol && b.Min.X <= a.Max.X + tol &&
            a.Min.Y <= b.Max.Y + tol && b.Min.Y <= a.Max.Y + tol &&
            a.Min.Z <= b.Max.Z + tol && b.Min.Z <= a.Max.Z + tol;
    }
}
