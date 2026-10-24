using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
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

        public IModeler Modeler => _modeler;
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
        /// <param name="progress">When given, called for each body of a round with its index and the round's size.</param>
        public List<IBody2> UnionMany(List<IBody2> bodies, Action<int, int> progress = null)
        {
            // Neighbours in space are next to each other in Morton (Z-curve) order of the box centres, so most
            // pairs touch; each body is united with the first of the next few whose box touches its own.
            var all = bodies.Where(b => b != null).Select(b => (body: b, box: AnyBounds(b))).Where(e => !e.box.IsEmpty).ToList();
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
                    progress?.Invoke(i, current.Count);
                    int partner = -1;
                    for (int j = i + 1; j < Math.Min(current.Count, i + 8) && partner < 0; j++)
                        if (!taken[j] && Overlaps(current[i].box, current[j].box, 1e-7))
                            partner = j;
                    if (partner < 0) { next.Add(current[i]); continue; }
                    taken[partner] = true;
                    // A union that leaves two bodies hands back altered copies (their faces imprinted where they
                    // touch), which later unions failed on (43 lumps instead of 9 on a motor); the originals are kept.
                    var merged = Union(current[i].body, current[partner].body);
                    if (merged == null) { next.Add(current[i]); next.Add(current[partner]); continue; }
                    next.Add((merged, AnyBounds(merged)));
                }
                bool shrank = next.Count < current.Count;
                current = next.OrderBy(e => Morton(e.box)).ToList();
                if (!shrank)
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
        /// The body's box, or when SOLIDWORKS gives none (some boolean results have none), its extents from its
        /// extreme points; empty only when neither is known.
        /// </summary>
        public static Aabb AnyBounds(IBody2 body)
        {
            var box = Bounds(body);
            return box.IsEmpty && body != null ? TightBounds(body) : box;
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
        /// <param name="added">When given, receives the bridges that were added.</param>
        public List<IBody2> BridgeByBoxes(List<IBody2> lumps, double gapWidth, double tolerance, out int bridges, List<IBody2> added = null)
        {
            bridges = 0;
            var current = new List<IBody2>(lumps);
            // Pairs that gave no bridge give none in a later round either (only the merged lump is new), so they are
            // not tried again: every round started over and tried them all (279 s for 147 lumps).
            var tried = new HashSet<(IBody2, IBody2)>();
            for (int round = 0; round < 50 && current.Count > 1; round++)
            {
                bool merged = false;
                for (int i = 0; i < current.Count && !merged; i++)
                    for (int j = i + 1; j < current.Count && !merged; j++)
                    {
                        if (!tried.Add((current[i], current[j])))
                            continue;
                        var bridge = BoxBridge(current[i], current[j], gapWidth, tolerance);
                        if (bridge == null)
                            continue;
                        var joined = Union(Union(current[i], bridge) ?? current[i], current[j]);
                        if (joined == null)
                            continue;
                        current[i] = joined;
                        current.RemoveAt(j);
                        bridges++;
                        added?.Add(bridge);
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
            int separated = 0, apartAxis = -1;
            for (int k = 0; k < 3; k++)
            {
                double lo = Math.Max(ba.Min[k], bb.Min[k]), hi = Math.Min(ba.Max[k], bb.Max[k]);
                if (lo > hi)
                {
                    // Apart along this axis: the gap between the boxes, widened below into the bodies (see below).
                    if (lo - hi > gapWidth)
                        return null;
                    separated++;
                    apartAxis = k;
                    min[k] = hi;
                    max[k] = lo;
                }
                else
                {
                    if (hi - lo < tolerance)
                        return null; // outlines only touch at an edge
                    min[k] = lo;
                    max[k] = hi;
                }
            }
            if (separated == 0)
                return InterleavedBridge(a, b, ba, bb, min, max, gapWidth, tolerance);
            if (separated > 1)
            {
                // Apart diagonally: the old way, the boxes spanned whole (rare).
                for (int k = 0; k < 3; k++)
                    if (Math.Max(ba.Min[k], bb.Min[k]) > Math.Min(ba.Max[k], bb.Max[k]))
                    {
                        min[k] = Math.Min(ba.Min[k], bb.Min[k]);
                        max[k] = Math.Max(ba.Max[k], bb.Max[k]);
                    }
                return BridgeIn(a, b, min, max);
            }
            // Apart along one axis: the air in the band between the boxes, reaching into each body only as deep as
            // needed to meet its facing surface (widened step by step). Spanning both bodies whole filled the air
            // around them too: on a press the motor on top disappeared in a block.
            double gap0 = min[apartAxis], gap1 = max[apartAxis];
            double outer0 = Math.Min(ba.Min[apartAxis], bb.Min[apartAxis]), outer1 = Math.Max(ba.Max[apartAxis], bb.Max[apartAxis]);
            for (double d = Math.Max(gap1 - gap0, 1e-3); ; d *= 2)
            {
                min[apartAxis] = Math.Max(outer0, gap0 - d);
                max[apartAxis] = Math.Min(outer1, gap1 + d);
                var bridge = BridgeIn(a, b, min, max);
                if (bridge != null || d >= gapWidth || (min[apartAxis] <= outer0 && max[apartAxis] >= outer1))
                    return bridge;
            }
        }

        /// <summary>
        /// Boxes that overlap on every axis but bodies that do not touch: a terminal box standing on springs above a
        /// housing whose lifting eye reaches up between them. Along the axis where the boxes overlap least (a thin
        /// band compared with the bodies) the space is bridged within that band, widened step by step up to the
        /// limit until a piece of air touches both bodies. Interleaved bodies (overlapping widely on every axis)
        /// are left alone.
        /// </summary>
        private IBody2 InterleavedBridge(IBody2 a, IBody2 b, Aabb ba, Aabb bb, double[] min, double[] max, double gapWidth, double tolerance)
        {
            int axis = -1;
            double best = 0.5;
            for (int k = 0; k < 3; k++)
            {
                double smaller = Math.Min(ba.Max[k] - ba.Min[k], bb.Max[k] - bb.Min[k]);
                double ratio = smaller > tolerance ? (max[k] - min[k]) / smaller : 1;
                if (ratio < best) { best = ratio; axis = k; }
            }
            if (axis < 0)
                return null;
            double lo = min[axis], hi = max[axis];
            double outer0 = Math.Min(ba.Min[axis], bb.Min[axis]), outer1 = Math.Max(ba.Max[axis], bb.Max[axis]);
            for (double d = Math.Max(hi - lo, 1e-3); ; d *= 2)
            {
                var m = (double[])min.Clone();
                var M = (double[])max.Clone();
                m[axis] = Math.Max(outer0, lo - Math.Min(d, gapWidth));
                M[axis] = Math.Min(outer1, hi + Math.Min(d, gapWidth));
                var bridge = BridgeIn(a, b, m, M);
                if (bridge != null || d >= gapWidth || (m[axis] <= outer0 && M[axis] >= outer1))
                    return bridge;
            }
        }

        /// <summary>The air within the box [min, max] between a and b, as far as it touches both of them.</summary>
        private IBody2 BridgeIn(IBody2 a, IBody2 b, double[] min, double[] max)
        {
            IBody2 region;
            try { region = Box(new Aabb(new Vec3(min[0], min[1], min[2]), new Vec3(max[0], max[1], max[2]))); }
            catch (InvalidOperationException) { return null; } // a box SOLIDWORKS cannot make (too thin) bridges nothing
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

        /// <summary>
        /// Unites bodies whose boxes overlap, round after round: in each round every body is united with at most
        /// one other (pairs with the smallest boxes first), so the lumps grow like a tree. A pair that does not
        /// unite is not tried again. Every round unites at least one pair or ends the loop.
        /// </summary>
        private List<IBody2> UnionByRounds(List<IBody2> bodies, double tolerance, Action<int, int> progress)
        {
            // Each lump has an id; apart[id] holds the lumps it is known not to touch. A lump made of two that both
            // do not touch X does not touch X either, so such pairs are not tried again after every merge (on a
            // 3145-body assembly most attempts were these).
            var apart = new List<HashSet<int>>();
            var items = bodies.Select(b => { apart.Add(new HashSet<int>()); return (body: b, box: AnyBounds(b), id: apart.Count - 1); }).ToList();
            var failed = new HashSet<(IBody2, IBody2)>();
            int total = items.Count, done = 0, attempts = 0, merges = 0, apartCount = 0, errors = 0, skipped = 0;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (items.Count > 1)
            {
                // Overlapping pairs by a sweep along x.
                var order = Enumerable.Range(0, items.Count).OrderBy(i => items[i].box.Min.X).ToList();
                var pairs = new List<(int a, int b, double size)>();
                for (int s = 0; s < order.Count; s++)
                {
                    var bi = items[order[s]].box;
                    for (int t = s + 1; t < order.Count && items[order[t]].box.Min.X <= bi.Max.X + tolerance; t++)
                    {
                        int a = order[s], b = order[t];
                        if (!Overlaps(bi, items[b].box, tolerance) || failed.Contains((items[a].body, items[b].body)))
                            continue;
                        if (apart[items[a].id].Contains(items[b].id)) { skipped++; continue; }
                        pairs.Add((a, b, Volume(bi) + Volume(items[b].box)));
                    }
                }
                if (pairs.Count == 0)
                    break;
                var taken = new bool[items.Count];
                var next = new List<(IBody2 body, Aabb box, int id)>();
                foreach (var (a, b, _) in pairs.OrderBy(p => p.size))
                {
                    if (taken[a] || taken[b])
                        continue;
                    progress?.Invoke(Math.Min(done++, total - 1), total);
                    attempts++;
                    var u = Union(items[a].body, items[b].body, out bool isApart);
                    if (u == null)
                    {
                        if (isApart)
                        {
                            apartCount++;
                            apart[items[a].id].Add(items[b].id);
                            apart[items[b].id].Add(items[a].id);
                        }
                        else
                        {
                            errors++;
                            failed.Add((items[a].body, items[b].body));
                            failed.Add((items[b].body, items[a].body));
                        }
                        continue;
                    }
                    merges++;
                    taken[a] = taken[b] = true;
                    var both = new HashSet<int>(apart[items[a].id]);
                    both.IntersectWith(apart[items[b].id]);
                    apart.Add(both);
                    int id = apart.Count - 1;
                    foreach (int x in both) apart[x].Add(id);
                    next.Add((u, AnyBounds(u), id));
                }
                bool merged = taken.Any(x => x);
                for (int i = 0; i < items.Count; i++)
                    if (!taken[i])
                        next.Add(items[i]);
                items = next;
                if (!merged)
                    break;
            }
            UI.PageLog.Write($"  birleştirme turları: {attempts} deneme, {merges} birleşti, {apartCount} değmiyor, {errors} hata, {skipped} değmediği bilindiği için denenmedi ({watch.Elapsed.TotalSeconds:F0} sn)");
            return items.Select(e => e.body).ToList();
        }

        private static double Volume(Aabb b) => b.Size.X * b.Size.Y * b.Size.Z;


        /// <summary>Unions every pair of bodies that touch or overlap. Returns one body per separate lump.</summary>
        /// <param name="progress">When given, called before each union with the number done and the total.</param>
        public List<IBody2> UnionAll(IEnumerable<IBody2> bodies, double tolerance, Action<int, int> progress = null)
        {
            var all = bodies.Where(b => b != null).ToList();
            // Uniting each body with the lump that grows from them made every boolean as large as the whole model
            // (223 s and 20 GB for 117 bodies). They are united in rounds instead, like a tree, so most booleans
            // stay small. Two lumps SOLIDWORKS cannot unite stay separate bodies (Serkan: a second body is fine
            // where the geometry does not unite); trying to force them (taking one apart, adding its bodies one by
            // one) took far longer and once crashed SOLIDWORKS. Bodies without a box are kept as they are rather
            // than lost (this once dropped a whole model).
            var boxed = all.Where(b => !AnyBounds(b).IsEmpty).ToList();
            var united = UnionByRounds(boxed, tolerance, progress);
            UI.PageLog.Write($"  turlar: {boxed.Count} gövde → {united.Count}");
            return united.Concat(all.Where(b => AnyBounds(b).IsEmpty)).ToList();
        }
        /// <summary>Logs failing booleans to the page log (for finding why bodies stay apart).</summary>
        public bool Trace;

        private static double Volume(IBody2 b) => (((double[])b?.GetMassProperties(1))?[3] ?? 0) * 1e9;

        /// <returns>The single merged body, or null when the bodies stay separate or the union fails.</returns>
        public IBody2 Union(IBody2 a, IBody2 b) => Union(a, b, out _);

        /// <param name="apart">True when SOLIDWORKS united them but they stayed separate bodies (they do not touch),
        /// false when the union itself failed.</param>
        public IBody2 Union(IBody2 a, IBody2 b, out bool apart)
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
            apart = output != null && output.Length > 1;
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
        /// <param name="added">When given, receives the cavity pieces that were filled.</param>
        public IBody2 FillCavities(IBody2 body, List<IBody2> added = null)
        {
            var bounds = AnyBounds(body);
            if (bounds.IsEmpty)
                return body;
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
                var united = Union(result, piece);
                if (united == null)
                    continue;
                result = united;
                added?.Add((IBody2)piece.Copy());
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
