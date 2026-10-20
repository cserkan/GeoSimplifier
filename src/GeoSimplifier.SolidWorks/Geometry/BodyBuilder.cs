using System;
using System.Collections.Generic;
using GeoSimplifier.Core.Geometry;
using GeoSimplifier.Core.Voxels;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace GeoSimplifier.SolidWorks.Geometry
{
    /// <summary>Turns voxel boxes (and optionally the original bodies) into SOLIDWORKS temporary solid bodies.</summary>
    public sealed class BodyBuilder
    {
        private readonly BoxFactory _boxes;
        private double _tolerance;

        public BodyBuilder(ISldWorks swApp)
        {
            _boxes = new BoxFactory((IModeler)swApp.GetModeler(), (IMathUtility)swApp.GetMathUtility());
        }

        /// <summary>Number of boolean unions SOLIDWORKS rejected during the last <see cref="Build"/>.</summary>
        public int FailedUnions { get; private set; }

        /// <summary>
        /// Unions the voxel boxes, then the optional seed bodies (copies of the original bodies when their
        /// faces are kept exactly). Returns one body per disconnected lump (normally exactly one).
        /// </summary>
        public List<IBody2> Build(IList<VoxelBox> boxes, VoxelGrid grid, IEnumerable<IBody2> seeds = null,
            Action<int, int> progress = null)
        {
            FailedUnions = 0;
            // Boxes are grown slightly so neighbours overlap instead of sharing coincident faces or edges.
            // Coincident-face and edge-only contacts make the kernel's booleans fail or produce
            // non-manifold intermediates, which silently dropped boxes (holes left open in the result).
            double overlap = grid.VoxelSize * 0.01;
            _tolerance = overlap;
            var grow = new Vec3(overlap, overlap, overlap);

            var items = new List<Aabb>(boxes.Count);
            foreach (var vb in boxes)
            {
                var cells = vb.ToWorld(grid);
                items.Add(new Aabb(cells.Min - grow, cells.Max + grow));
            }

            int created = 0;
            var lumps = items.Count > 0
                ? BuildRange(items, 0, items.Count, () => progress?.Invoke(++created, items.Count))
                : new List<Lump>();

            // Seeds go last: unioning many small boxes into a large exact body one by one is slow.
            if (seeds != null)
            {
                var seedLumps = new List<Lump>();
                foreach (var seed in seeds)
                {
                    var bb = (double[])seed.GetBodyBox();
                    seedLumps.Add(new Lump(seed, new Aabb(new Vec3(bb[0], bb[1], bb[2]), new Vec3(bb[3], bb[4], bb[5]))));
                }
                lumps = MergeLists(seedLumps, lumps);
            }

            var result = new List<IBody2>(lumps.Count);
            foreach (var lump in lumps)
                result.Add(lump.Body);
            return result;
        }

        /// <summary>
        /// Divide and conquer: split the boxes in two halves along the longest axis, build each half and
        /// union the results. Each boolean then works on bodies of similar, small size instead of adding
        /// every box to one ever-growing body, which made large models take minutes.
        /// </summary>
        private List<Lump> BuildRange(List<Aabb> items, int start, int count, Action created)
        {
            if (count == 1)
            {
                created();
                return new List<Lump> { new Lump(_boxes.Create(items[start]), items[start]) };
            }

            var bounds = items[start];
            for (int n = start + 1; n < start + count; n++)
                bounds = bounds.Union(items[n]);
            var size = bounds.Size;
            int axis = size.X >= size.Y && size.X >= size.Z ? 0 : size.Y >= size.Z ? 1 : 2;
            items.Sort(start, count, Comparer<Aabb>.Create((a, b) => (a.Min[axis] + a.Max[axis]).CompareTo(b.Min[axis] + b.Max[axis])));

            int half = count / 2;
            var left = BuildRange(items, start, half, created);
            var right = BuildRange(items, start + half, count - half, created);
            return MergeLists(left, right);
        }

        /// <summary>Unions every lump of <paramref name="b"/> into the overlapping lumps of <paramref name="a"/>.</summary>
        private List<Lump> MergeLists(List<Lump> a, List<Lump> b)
        {
            var lumps = new List<Lump>(a);
            foreach (var incoming in b)
            {
                var current = incoming;
                for (int n = lumps.Count - 1; n >= 0; n--)
                {
                    if (!Overlaps(lumps[n].Bounds, current.Bounds))
                        continue;
                    var merged = Union(lumps[n].Body, current.Body);
                    if (merged == null)
                        continue;
                    current = new Lump(merged, lumps[n].Bounds.Union(current.Bounds));
                    lumps.RemoveAt(n);
                }
                lumps.Add(current);
            }
            return lumps;
        }

        /// <returns>The single merged body, or null when the bodies stay separate or the union fails.</returns>
        private IBody2 Union(IBody2 a, IBody2 b)
        {
            // Boolean operations may modify their inputs, so operate on copies and keep the originals intact.
            var target = (IBody2)a.Copy();
            var tool = (IBody2)b.Copy();
            var output = (object[])target.Operations2((int)swBodyOperationType_e.SWBODYADD, tool, out int error);
            if (output == null || error != 0)
            {
                FailedUnions++;
                return null;
            }
            return output.Length == 1 ? (IBody2)output[0] : null;
        }

        // Touching counts as overlapping: seed bodies (e.g. assembly components) may only share a face.
        private bool Overlaps(Aabb a, Aabb b) =>
            a.Min.X <= b.Max.X + _tolerance && b.Min.X <= a.Max.X + _tolerance &&
            a.Min.Y <= b.Max.Y + _tolerance && b.Min.Y <= a.Max.Y + _tolerance &&
            a.Min.Z <= b.Max.Z + _tolerance && b.Min.Z <= a.Max.Z + _tolerance;

        private readonly struct Lump
        {
            public readonly IBody2 Body;
            public readonly Aabb Bounds;

            public Lump(IBody2 body, Aabb bounds)
            {
                Body = body;
                Bounds = bounds;
            }
        }
    }

    /// <summary>
    /// Creates axis-aligned box bodies with IModeler.CreateBodyFromBox3. The API takes a reference point,
    /// an axis and width/length/height; rather than rely on which of those maps to X/Y and whether the point
    /// is the box center or base center, a calibration box is measured once and the mapping derived from it.
    /// </summary>
    internal sealed class BoxFactory
    {
        private readonly IModeler _modeler;
        private readonly IMathUtility _math;
        private readonly int[] _paramForAxis = new int[3];   // which of (w, l, h) lies along X, Y, Z
        private readonly double[] _minFactor = new double[3]; // box min = refPoint + factor * size, per axis

        public BoxFactory(IModeler modeler, IMathUtility math)
        {
            _modeler = modeler;
            _math = math;
            Calibrate();
        }

        public IBody2 Create(Aabb box)
        {
            var size = box.Size;
            var dims = new double[3];
            for (int axis = 0; axis < 3; axis++)
                dims[_paramForAxis[axis]] = size[axis];

            var refPoint = new double[3];
            for (int axis = 0; axis < 3; axis++)
                refPoint[axis] = box.Min[axis] - _minFactor[axis] * size[axis];

            var body = (IBody2)_modeler.CreateBodyFromBox3(new[]
            {
                refPoint[0], refPoint[1], refPoint[2],
                0.0, 0.0, 1.0,
                dims[0], dims[1], dims[2],
            });
            if (body == null)
                throw new InvalidOperationException("CreateBodyFromBox3 failed.");
            return body;
        }

        private void Calibrate()
        {
            double[] p = { 1.0, 2.0, 3.0 };
            var body = (IBody2)_modeler.CreateBodyFromBox3(new[] { 0.0, 0.0, 0.0, 0.0, 0.0, 1.0, p[0], p[1], p[2] });
            var bb = (double[])body.GetBodyBox(); // xmin, ymin, zmin, xmax, ymax, zmax
            for (int axis = 0; axis < 3; axis++)
            {
                double extent = bb[axis + 3] - bb[axis];
                int param = Array.FindIndex(p, v => Math.Abs(v - extent) < 1e-6);
                if (param < 0)
                    throw new InvalidOperationException("Unexpected CreateBodyFromBox3 behaviour during calibration.");
                _paramForAxis[axis] = param;
                _minFactor[axis] = bb[axis] / extent;
            }
        }
    }
}
