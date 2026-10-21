using System;
using System.Collections.Generic;
using GeoSimplifier.Core.Geometry;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace GeoSimplifier.SolidWorks.Geometry
{
    /// <summary>Exact boolean and offset operations on temporary bodies.</summary>
    public sealed class BodyOps
    {
        private readonly BoxFactory _boxes;
        private bool? _outwardFlag;

        public BodyOps(ISldWorks swApp)
        {
            _boxes = new BoxFactory((IModeler)swApp.GetModeler());
        }

        /// <summary>Boolean operations SOLIDWORKS rejected.</summary>
        public int FailedBooleans { get; private set; }

        public static Aabb Bounds(IBody2 body)
        {
            var bb = (double[])body.GetBodyBox();
            return new Aabb(new Vec3(bb[0], bb[1], bb[2]), new Vec3(bb[3], bb[4], bb[5]));
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
        public IBody2 Union(IBody2 a, IBody2 b)
        {
            var output = Boolean(swBodyOperationType_e.SWBODYADD, a, b);
            return output != null && output.Length == 1 ? (IBody2)output[0] : null;
        }

        /// <summary>a minus b; null when the operation fails, empty when nothing is left.</summary>
        public object[] Subtract(IBody2 a, IBody2 b) => Boolean(swBodyOperationType_e.SWBODYCUT, a, b);

        private object[] Boolean(swBodyOperationType_e type, IBody2 a, IBody2 b)
        {
            // Boolean operations may modify their inputs, so operate on copies and keep the originals intact.
            var target = (IBody2)a.Copy();
            var tool = (IBody2)b.Copy();
            var output = (object[])target.Operations2((int)type, tool, out int error);
            if (error != 0)
            {
                FailedBooleans++;
                return null;
            }
            return output ?? new object[0];
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
        private IBody2 Offset(IBody2 body, double distance, bool outward)
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

        private static bool Overlaps(Aabb a, Aabb b, double tol) =>
            a.Min.X <= b.Max.X + tol && b.Min.X <= a.Max.X + tol &&
            a.Min.Y <= b.Max.Y + tol && b.Min.Y <= a.Max.Y + tol &&
            a.Min.Z <= b.Max.Z + tol && b.Min.Z <= a.Max.Z + tol;
    }
}
