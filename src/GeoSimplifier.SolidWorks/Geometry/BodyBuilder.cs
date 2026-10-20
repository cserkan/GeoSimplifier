using System;
using System.Collections.Generic;
using GeoSimplifier.Core.Geometry;
using GeoSimplifier.Core.ShrinkWrap;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace GeoSimplifier.SolidWorks.Geometry
{
    /// <summary>Turns the voxel boxes of a shrink wrap result into SOLIDWORKS temporary solid bodies.</summary>
    public sealed class BodyBuilder
    {
        private readonly IModeler _modeler;
        private readonly BoxFactory _boxes;

        public BodyBuilder(ISldWorks swApp)
        {
            _modeler = (IModeler)swApp.GetModeler();
            _boxes = new BoxFactory(_modeler, (IMathUtility)swApp.GetMathUtility());
        }

        /// <summary>Number of boolean unions SOLIDWORKS rejected during the last <see cref="Build"/>.</summary>
        public int FailedUnions { get; private set; }

        /// <summary>Unions all boxes. Returns one body per disconnected lump (normally exactly one).</summary>
        public List<IBody2> Build(ShrinkWrapResult result, Action<int, int> progress = null)
        {
            var lumps = new List<IBody2>();
            var lumpBoxes = new List<Aabb>();
            // Boxes are grown slightly so neighbours overlap instead of sharing coincident faces or edges.
            // Coincident-face and edge-only contacts make the kernel's booleans fail or produce
            // non-manifold intermediates, which silently dropped boxes (holes left open in the result).
            double overlap = result.VoxelSize * 0.01;
            var grow = new Vec3(overlap, overlap, overlap);
            int done = 0;
            FailedUnions = 0;

            foreach (var vb in result.Boxes)
            {
                var cells = vb.ToWorld(result.Solid);
                var box = new Aabb(cells.Min - grow, cells.Max + grow);
                IBody2 body = _boxes.Create(box);

                // Union with every existing lump this box overlaps.
                for (int n = lumps.Count - 1; n >= 0; n--)
                {
                    if (!Overlaps(lumpBoxes[n], box))
                        continue;
                    var merged = Union(lumps[n], body);
                    if (merged == null)
                        continue;
                    body = merged;
                    box = box.Union(lumpBoxes[n]);
                    lumps.RemoveAt(n);
                    lumpBoxes.RemoveAt(n);
                }
                lumps.Add(body);
                lumpBoxes.Add(box);
                progress?.Invoke(++done, result.Boxes.Count);
            }

            MergeRemainingLumps(lumps, lumpBoxes);
            return lumps;
        }

        /// <summary>Second chance for lumps that stayed apart because a union failed earlier.</summary>
        private void MergeRemainingLumps(List<IBody2> lumps, List<Aabb> lumpBoxes)
        {
            bool changed = true;
            while (changed && lumps.Count > 1)
            {
                changed = false;
                for (int a = 0; a < lumps.Count && !changed; a++)
                for (int b = a + 1; b < lumps.Count && !changed; b++)
                {
                    if (!Overlaps(lumpBoxes[a], lumpBoxes[b]))
                        continue;
                    var merged = Union(lumps[a], lumps[b]);
                    if (merged == null)
                        continue;
                    lumps[a] = merged;
                    lumpBoxes[a] = lumpBoxes[a].Union(lumpBoxes[b]);
                    lumps.RemoveAt(b);
                    lumpBoxes.RemoveAt(b);
                    changed = true;
                }
            }
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

        private static bool Overlaps(Aabb a, Aabb b) =>
            a.Min.X < b.Max.X && b.Min.X < a.Max.X &&
            a.Min.Y < b.Max.Y && b.Min.Y < a.Max.Y &&
            a.Min.Z < b.Max.Z && b.Min.Z < a.Max.Z;
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
