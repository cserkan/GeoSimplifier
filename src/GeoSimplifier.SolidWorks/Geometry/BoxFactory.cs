using System;
using GeoSimplifier.Core.Geometry;
using SolidWorks.Interop.sldworks;

namespace GeoSimplifier.SolidWorks.Geometry
{
    /// <summary>
    /// Creates axis-aligned box bodies with IModeler.CreateBodyFromBox3. The API takes a reference point,
    /// an axis and width/length/height; rather than rely on which of those maps to X/Y and whether the point
    /// is the box center or base center, a calibration box is measured once and the mapping derived from it.
    /// </summary>
    internal sealed class BoxFactory
    {
        private readonly IModeler _modeler;
        private readonly int[] _paramForAxis = new int[3];   // which of (w, l, h) lies along X, Y, Z
        private readonly double[] _minFactor = new double[3]; // box min = refPoint + factor * size, per axis

        public BoxFactory(IModeler modeler)
        {
            _modeler = modeler;
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

        /// <summary>
        /// A box with a square <paramref name="side"/> x <paramref name="side"/> base centered on
        /// <paramref name="baseCenter"/>, reaching <paramref name="height"/> along <paramref name="axis"/>. How the
        /// square is turned about the axis is up to SOLIDWORKS. Null if the calibrated box is not centered sideways.
        /// </summary>
        public IBody2 CreateOnAxis(Vec3 baseCenter, Vec3 axis, double side, double height)
        {
            if (Math.Abs(_minFactor[0] + 0.5) > 1e-6 || Math.Abs(_minFactor[1] + 0.5) > 1e-6)
                return null;
            var dims = new double[3];
            dims[_paramForAxis[0]] = side;
            dims[_paramForAxis[1]] = side;
            dims[_paramForAxis[2]] = height;
            var a = axis.Normalized();
            var r = baseCenter - a * (_minFactor[2] * height);
            return (IBody2)_modeler.CreateBodyFromBox3(new[] { r.X, r.Y, r.Z, a.X, a.Y, a.Z, dims[0], dims[1], dims[2] });
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
