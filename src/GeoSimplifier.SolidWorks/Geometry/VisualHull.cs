using System;
using System.Collections.Generic;
using System.Linq;
using GeoSimplifier.Core.Geometry;
using SolidWorks.Interop.sldworks;

namespace GeoSimplifier.SolidWorks.Geometry
{
    /// <summary>
    /// A very detailed solid (an engine block, a cylinder head) as its rough outline (Serkan: "show such detailed
    /// models in general lines, only as tight as a shrink wrap from outside"): the body's outline seen along each of
    /// the three axes is extruded through the body's box and the three are intersected. Holes, bolts, fins and
    /// lettering go; the shape as seen from each side stays. The result is an exact B-rep body.
    /// </summary>
    public sealed class VisualHull
    {
        private readonly IModeler _modeler;
        private readonly IMathUtility _math;
        private readonly BodyOps _ops;

        /// <summary>
        /// Outlines as trimmed planar sheets, extruded (exact polygons). Off, they are made of boxes, one per
        /// rectangle of the outline's grid, which is coarser but uses only boxes and booleans (a trimmed planar
        /// sheet once crashed SOLIDWORKS in hole closing).
        /// </summary>
        public bool UseSheets { get; set; } = false;

        /// <summary>
        /// Outlines cut from boxes: each convex piece's box trimmed along its edges by half-spaces. Exact polygons
        /// with boxes and booleans only (trimmed sheets fail on this installation). Takes precedence.
        /// </summary>
        public bool UseCuts { get; set; } = true;

        /// <summary>Grid cells along the longest side of each outline; fewer give a rougher result.</summary>
        public int Cells { get; set; } = 60;

        public string Note { get; private set; } = "";

        /// <summary>What failed while making the last hull, step by step.</summary>
        public List<string> Failures { get; } = new List<string>();

        public VisualHull(ISldWorks swApp, BodyOps ops)
        {
            _modeler = (IModeler)swApp.GetModeler();
            _math = (IMathUtility)swApp.GetMathUtility();
            _ops = ops;
        }

        /// <summary>The rough outline of <paramref name="body"/>, or null when it cannot be made.</summary>
        public IBody2 Make(IBody2 body)
        {
            var triangles = BodyOps.Triangles(new[] { body });
            var box = BodyOps.Bounds(body);
            if (triangles.Count < 3 || box.IsEmpty)
            {
                Note = "üçgen yok";
                return null;
            }
            double longest = box.LongestSide;
            IBody2 hull = null;
            // Along a long body (an engine block) the outline seen from its end is taken slice by slice: seen whole,
            // the block's end covers the valley between its banks; the slices in between show it.
            var order = Enumerable.Range(0, 3).OrderBy(a => box.Size[a]).ToArray();
            int lengthAxis = order[2];
            int slices = box.Size[lengthAxis] > 2 * box.Size[order[1]] ? Slices : 1;
            for (int axis = 0; axis < 3; axis++)
            {
                var prism = axis == lengthAxis && slices > 1
                    ? SlicedPrism(triangles, axis, box, longest, slices)
                    : Prism(triangles, axis, box, longest);
                if (prism == null)
                {
                    Note = $"{"XYZ"[axis]} yönünde siluet yapılamadı";
                    return null;
                }
                if (hull == null) { hull = prism; continue; }
                var common = _ops.Intersect(hull, prism)?.OfType<IBody2>()
                    .OrderByDescending(b => ((double[])b.GetMassProperties(1))?[3] ?? 0).FirstOrDefault();
                if (common == null)
                {
                    Note = $"{"XYZ"[axis]} yönünde kesişim olmadı";
                    return null;
                }
                hull = common;
            }
            Note = $"{body.GetFaceCount()} yüz → {hull.GetFaceCount()} yüz";
            return hull;
        }

        /// <summary>The body's outline along <paramref name="axis"/>, extruded a little past the box on both ends.</summary>
        private IBody2 Prism(List<Vec3> triangles, int axis, Aabb box, double longest, bool exact = false)
        {
            int ua = (axis + 1) % 3, va = (axis + 2) % 3;
            // A slice's prism ends at the slice (its neighbours continue it); a whole body's reaches a little past.
            double margin = exact ? 0 : 0.02 * longest, from = box.Min[axis] - margin, length = box.Size[axis] + 2 * margin;
            Vec3 Point(double u, double v, double w)
            {
                var p = new double[3];
                p[axis] = w; p[ua] = u; p[va] = v;
                return new Vec3(p[0], p[1], p[2]);
            }
            var parts = new List<IBody2>();
            if (UseCuts)
            {
                // Each convex piece of the outline: its box, cut flat along each of its edges (a half-space is a large
                // box), so slopes stay slopes with boxes and booleans only.
                foreach (var loop in Silhouette.Outlines(triangles, axis, Cells, tolerance: 0.005 * longest, minArea: Math.Pow(0.02 * longest, 2)))
                    foreach (var piece in Silhouette.ConvexPieces(loop))
                    {
                        double pu0 = piece.Min(p => p.u), pu1 = piece.Max(p => p.u), pv0 = piece.Min(p => p.v), pv1 = piece.Max(p => p.v);
                        var lo = Point(pu0, pv0, from);
                        var hi = Point(pu1, pv1, from + length);
                        var body = _ops.Box(new Aabb(Vec3.Min(lo, hi), Vec3.Max(lo, hi)));
                        for (int k = 0; k < piece.Count && body != null; k++)
                        {
                            var (au, av) = piece[k];
                            var (bu, bv) = piece[(k + 1) % piece.Count];
                            // Edges along the box's own sides cut nothing.
                            if (Math.Abs(au - bu) < 1e-9 && (Math.Abs(au - pu0) < 1e-9 || Math.Abs(au - pu1) < 1e-9)) continue;
                            if (Math.Abs(av - bv) < 1e-9 && (Math.Abs(av - pv0) < 1e-9 || Math.Abs(av - pv1) < 1e-9)) continue;
                            double du = bu - au, dv = bv - av, len = Math.Sqrt(du * du + dv * dv);
                            if (len < 1e-9) continue;
                            var inward = Point(-dv / len, du / len, 0) - Point(0, 0, 0);
                            var middle = Point((au + bu) / 2, (av + bv) / 2, from + length / 2);
                            var half = _ops.HalfSpace(middle, inward, 4 * longest);
                            var kept = half == null ? null : _ops.Intersect(body, half)?.OfType<IBody2>()
                                .OrderByDescending(b => ((double[])b.GetMassProperties(1))?[3] ?? 0).FirstOrDefault();
                            if (kept == null) Failures.Add($"{"XYZ"[axis]}: eğik kesim olmadı");
                            else body = kept;
                        }
                        if (body != null) parts.Add(body);
                    }
                return parts.Count > 0 ? UnionAll(parts) : PrismOfBoxes(triangles, axis, from, length, Point);
            }
            if (UseSheets)
            {
                var direction = new double[3]; direction[axis] = 1;
                var dir = (MathVector)_math.CreateVector(direction);
                foreach (var loop in Silhouette.Outlines(triangles, axis, Cells, tolerance: 0.005 * longest, minArea: Math.Pow(0.02 * longest, 2)))
                {
                    var curves = new List<object>();
                    for (int i = 0; i < loop.Count; i++)
                    {
                        var a = Point(loop[i].u, loop[i].v, from);
                        var b = Point(loop[(i + 1) % loop.Count].u, loop[(i + 1) % loop.Count].v, from);
                        var d = b - a;
                        if (d.Length < 1e-7) continue;
                        var line = _modeler.CreateLine(new[] { a.X, a.Y, a.Z }, new[] { d.X, d.Y, d.Z }) as Curve;
                        var piece = line?.CreateTrimmedCurve2(a.X, a.Y, a.Z, b.X, b.Y, b.Z);
                        if (piece != null) curves.Add(piece);
                    }
                    if (curves.Count < 3) continue;
                    var reference = new double[3]; reference[ua] = 1;
                    var root = Point(loop[0].u, loop[0].v, from);
                    string step = "düzlem";
                    try
                    {
                        var plane = _modeler.CreatePlanarSurface2(new[] { root.X, root.Y, root.Z }, direction, reference) as Surface;
                        step = $"kırpılmış yüzey ({curves.Count} kenar)";
                        // CreateTrimmedSheet4 fails on this installation (server fault on every outline); the
                        // version with a tolerance and the oldest one are tried in turn.
                        IBody2 sheet = null;
                        foreach (var attempt in new Func<IBody2>[]
                        {
                            () => plane?.CreateTrimmedSheet5(curves.ToArray(), true, 1e-6) as IBody2,
                            () => plane?.CreateTrimmedSheet(curves.ToArray()) as IBody2,
                        })
                        {
                            try { sheet = attempt(); }
                            catch (System.Runtime.InteropServices.COMException ex) { Failures.Add($"{"XYZ"[axis]}: {step} hata {ex.ErrorCode:X}"); }
                            if (sheet != null) break;
                        }
                        if (sheet == null)
                            return PrismOfBoxes(triangles, axis, from, length, Point);
                        step = "ekstrüzyon";
                        var solid = sheet == null ? null : _modeler.CreateExtrudedBody((Body2)sheet, dir, length) as IBody2;
                        if (solid != null) parts.Add(solid);
                        else Failures.Add($"{"XYZ"[axis]}: {step} boş döndü");
                    }
                    catch (System.Runtime.InteropServices.COMException ex)
                    {
                        Failures.Add($"{"XYZ"[axis]}: {step} hata {ex.ErrorCode:X}");
                        throw;
                    }
                }
            }
            else
                return PrismOfBoxes(triangles, axis, from, length, Point);
            return UnionAll(parts);
        }

        /// <summary>Slices along a long body's length, each with the outline of its own part of the body.</summary>
        public int Slices { get; set; } = 8;

        /// <summary>
        /// The prism along <paramref name="axis"/> slice by slice: the triangles reaching into each slice give that
        /// slice's outline, extruded through the slice only; the slices are joined.
        /// </summary>
        private IBody2 SlicedPrism(List<Vec3> triangles, int axis, Aabb box, double longest, int slices)
        {
            double step = box.Size[axis] / slices;
            var parts = new List<IBody2>();
            for (int k = 0; k < slices; k++)
            {
                double lo = box.Min[axis] + k * step, hi = lo + step;
                var inSlice = new List<Vec3>();
                for (int t = 0; t + 2 < triangles.Count; t += 3)
                {
                    double a = triangles[t][axis], b = triangles[t + 1][axis], c = triangles[t + 2][axis];
                    if (Math.Max(a, Math.Max(b, c)) < lo || Math.Min(a, Math.Min(b, c)) > hi) continue;
                    inSlice.Add(triangles[t]); inSlice.Add(triangles[t + 1]); inSlice.Add(triangles[t + 2]);
                }
                if (inSlice.Count < 3) continue;
                var sliceBox = new Aabb(SetAxis(box.Min, axis, lo - (k == 0 ? 0.02 * longest : 0)),
                                        SetAxis(box.Max, axis, hi + (k == slices - 1 ? 0.02 * longest : 0)));
                var part = Prism(inSlice, axis, sliceBox, longest, exact: true);
                if (part != null) parts.Add(part);
            }
            return UnionAll(parts);
        }

        private static Vec3 SetAxis(Vec3 p, int axis, double value)
        {
            var c = new[] { p.X, p.Y, p.Z };
            c[axis] = value;
            return new Vec3(c[0], c[1], c[2]);
        }

        /// <summary>The outline's prism from boxes, one per rectangle of its grid.</summary>
        private IBody2 PrismOfBoxes(List<Vec3> triangles, int axis, double from, double length, Func<double, double, double, Vec3> point)
        {
            var parts = new List<IBody2>();
            foreach (var (u0, v0, u1, v1) in Silhouette.Rectangles(triangles, axis, Cells))
            {
                var min = point(u0, v0, from);
                var max = point(u1, v1, from + length);
                var b = _ops.Box(new Aabb(Vec3.Min(min, max), Vec3.Max(min, max)));
                if (b != null) parts.Add(b);
            }
            return UnionAll(parts);
        }

        private IBody2 UnionAll(List<IBody2> parts)
        {
            if (parts.Count == 0)
                return null;
            var all = parts[0];
            foreach (var p in parts.Skip(1))
                all = _ops.Union(all, p) ?? all;
            return all;
        }
    }
}
