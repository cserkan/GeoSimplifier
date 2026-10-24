using System;
using System.Collections.Generic;
using System.Linq;
using GeoSimplifier.Core.Geometry;
using SolidWorks.Interop.sldworks;

namespace GeoSimplifier.SolidWorks.Geometry
{
    /// <summary>
    /// Fills modeled screw threads: a band of many cone or freeform faces around one axis (thread flanks) is covered
    /// by a solid cylinder of the thread's outer diameter over its length, so a screw becomes a plain shank and a
    /// nut a solid ring. Nothing is deleted; the cylinder is united with the body.
    /// </summary>
    public sealed class ThreadFiller
    {
        private const int Cone = 4003, Cylinder = 4002, Freeform = 4006;

        // A thread has many flank faces; fewer are ordinary chamfers and countersinks.
        private const int MinFlanks = 8;

        // Threads up to M60; a wider band of flanks is something else (a housing's grooves).
        private const double MaxRadius = 0.03;

        private readonly BodyOps _ops;

        public ThreadFiller(BodyOps ops)
        {
            _ops = ops;
        }

        public int Filled { get; private set; }

        public IBody2 Fill(IBody2 body)
        {
            var faces = ((object[])body.GetFaces() ?? new object[0]).OfType<IFace2>().ToList();
            var flanks = faces.Where(f => Identity(f) == Cone || Identity(f) == Freeform).ToList();
            if (flanks.Count < MinFlanks)
                return body;

            // Axes to try: those of the body's cones and cylinders.
            var axes = new List<(Vec3 origin, Vec3 dir)>();
            foreach (var f in faces)
            {
                var s = (ISurface)f.GetSurface();
                double[] p = Identity(f) == Cone ? (double[])s.ConeParams : Identity(f) == Cylinder ? (double[])s.CylinderParams : null;
                // Only axes of thread-sized cones and cylinders (the radius is at index 6 for both).
                if (p == null || p[6] > MaxRadius) continue;
                var dir = new Vec3(p[3], p[4], p[5]).Normalized();
                var origin = new Vec3(p[0], p[1], p[2]);
                if (!axes.Any(a => Math.Abs(Dot(a.dir, dir)) > 0.9999 && Distance(origin, a) < 1e-5))
                    axes.Add((origin, dir));
            }

            var current = body;
            var used = new HashSet<IFace2>();
            // Each face is sampled once; a large casting has many axes to try.
            var samples = flanks.ToDictionary(f => f, Sample);
            foreach (var axis in axes)
            {
                // The flanks around this axis: all their points at about the same distance from it.
                var band = new List<(IFace2 face, List<Vec3> points)>();
                foreach (var f in flanks.Where(f => !used.Contains(f)))
                {
                    var points = samples[f];
                    if (points.Count == 0) continue;
                    var radii = points.Select(p => Distance(p, axis)).ToList();
                    if (radii.Max() - radii.Min() < 0.35 * radii.Max())
                        band.Add((f, points));
                }
                // Of those, the largest group at about one radius (a thread is a few tenths of a pitch deep); faces
                // at other radii around the same axis (a housing's chamfers) are not part of it.
                var byRadius = band.Select(b => (b.face, b.points, r: b.points.Average(p => Distance(p, axis)))).OrderBy(b => b.r).ToList();
                int bestStart = 0, bestCount = 0;
                for (int i = 0; i < byRadius.Count; i++)
                {
                    int j = i;
                    while (j < byRadius.Count && byRadius[j].r <= byRadius[i].r * 1.35)
                        j++;
                    if (j - i > bestCount) { bestCount = j - i; bestStart = i; }
                }
                band = byRadius.Skip(bestStart).Take(bestCount).Select(b => (b.face, b.points)).ToList();
                if (band.Count < MinFlanks)
                    continue;
                var all = band.SelectMany(b => b.points).ToList();
                double rMax = all.Max(p => Distance(p, axis));
                if (rMax > MaxRadius)
                    continue;
                double t0 = all.Min(p => Dot(p - axis.origin, axis.dir)), t1 = all.Max(p => Dot(p - axis.origin, axis.dir));
                if (t1 - t0 < 1e-4 || rMax < 1e-4)
                    continue;
                var centre = axis.origin + axis.dir * ((t0 + t1) / 2);
                var cylinder = _ops.SolidCylinder(centre, axis.dir, rMax, t1 - t0);
                // The cylinder covers the thread's own faces, so it lies within the body's box; one that does not
                // came from a misread band and is not used.
                var limit = BodyOps.AnyBounds(body);
                double m = Math.Max(5e-4, limit.LongestSide * 0.02);
                var cb = BodyOps.AnyBounds(cylinder);
                if (cylinder != null && (cb.Min.X < limit.Min.X - m || cb.Min.Y < limit.Min.Y - m || cb.Min.Z < limit.Min.Z - m ||
                                         cb.Max.X > limit.Max.X + m || cb.Max.Y > limit.Max.Y + m || cb.Max.Z > limit.Max.Z + m))
                {
                    UI.PageLog.Write($"  diş silindiri gövdenin kutusundan taştı, kullanılmadı (r {rMax * 1000:F1} mm, boy {(t1 - t0) * 1000:F1} mm)");
                    continue;
                }
                var united = cylinder == null ? null : _ops.Union(current, cylinder);
                if (united == null)
                    continue;
                // Filling a thread adds only its grooves, a small part of the cylinder; a cylinder that adds mostly
                // air was put where there is no thread (a housing's feet once got two large round lumps).
                double added = Volume(united) - Volume(current), whole = Volume(cylinder);
                if (whole <= 0 || added > 0.35 * whole)
                {
                    UI.PageLog.Write($"  diş silindiri çoğunlukla boşluğa denk geldi, kullanılmadı (r {rMax * 1000:F1} mm, eklenen %{100 * added / Math.Max(whole, 1e-12):F0})");
                    continue;
                }
                current = united;
                Filled++;
                foreach (var b in band) used.Add(b.face);
            }
            return current;
        }

        private static int Identity(IFace2 f) => ((ISurface)f.GetSurface()).Identity();

        private static double Volume(IBody2 b) => ((double[])b?.GetMassProperties(1))?[3] ?? 0;

        /// <summary>Points on the face: its surface evaluated on a grid over its parameter range.</summary>
        private static List<Vec3> Sample(IFace2 face)
        {
            var result = new List<Vec3>();
            var uv = (double[])face.GetUVBounds();
            var s = (ISurface)face.GetSurface();
            if (uv == null || s == null) return result;
            for (int i = 0; i <= 4; i++)
                for (int j = 0; j <= 4; j++)
                {
                    double u = uv[0] + (uv[1] - uv[0]) * i / 4, v = uv[2] + (uv[3] - uv[2]) * j / 4;
                    if (s.Evaluate(u, v, 0, 0) is double[] p && p.Length >= 3)
                        result.Add(new Vec3(p[0], p[1], p[2]));
                }
            return result;
        }

        private static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        private static double Distance(Vec3 p, (Vec3 origin, Vec3 dir) axis)
        {
            var d = p - axis.origin;
            var along = axis.dir * Dot(d, axis.dir);
            return (d - along).Length;
        }
    }
}
