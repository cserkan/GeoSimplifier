using System;
using System.Collections.Generic;
using System.Linq;
using GeoSimplifier.Core.Geometry;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace GeoSimplifier.SolidWorks.Geometry
{
    /// <summary>
    /// Closes holes, slots and pockets exactly by deleting their faces and letting the surrounding faces grow
    /// over the opening (like Delete Face > Delete and Patch), so the patch is flush with the surface.
    /// </summary>
    public sealed class HoleCloser
    {
        public int Closed { get; private set; }
        public int Failed { get; private set; }

        /// <summary>A hole: connected faces bounded only by inner loops of the faces around it.</summary>
        private sealed class Hole
        {
            public readonly List<int> FaceIds = new List<int>();
            public double Throat = double.PositiveInfinity;
            public bool Depression = true;
            public string Key = "";
        }

        private const int MaxHoleFaces = 400;

        /// <summary>Closes every hole whose narrowest mouth is at most <paramref name="maxWidth"/> wide.</summary>
        public IBody2 CloseHoles(IBody2 body, double maxWidth)
        {
            var failedKeys = new HashSet<string>();
            var current = body;

            // First try all holes in one operation; if SOLIDWORKS refuses, close them one at a time.
            var work = (IBody2)current.Copy();
            var holes = FindHoles(work, maxWidth, out var faces);
            if (holes.Count == 0)
                return current;
            if (Delete(work, holes.SelectMany(h => h.FaceIds).Select(id => faces[id])))
            {
                Closed += holes.Count;
                return work;
            }

            for (int guard = 0; guard < 1000; guard++)
            {
                work = (IBody2)current.Copy();
                holes = FindHoles(work, maxWidth, out faces).Where(h => !failedKeys.Contains(h.Key)).ToList();
                if (holes.Count == 0)
                    break;
                var hole = holes[0];
                if (Delete(work, hole.FaceIds.Select(id => faces[id])))
                {
                    current = work;
                    Closed++;
                }
                else
                {
                    failedKeys.Add(hole.Key);
                    Failed++;
                }
            }
            return current;
        }

        /// <summary>
        /// Deletes the faces and heals the opening: first by growing the surrounding faces over it (flush),
        /// then with a cap. The caller passes a working copy and throws it away when this returns false.
        /// </summary>
        private static bool Delete(IBody2 body, IEnumerable<IFace2> faces)
        {
            var list = faces.Cast<object>().ToArray();
            foreach (var heal in new[] { swHealActionType_e.swHealAction_GrowParent, swHealActionType_e.swHealAction_Cap })
            {
                bool ok = body.DeleteFaces5(list, (int)heal, (int)swLoopProcessOption_e.swLoopProcess_Auto,
                    true, out _, out bool valid);
                if (ok)
                    return valid;
            }
            return false;
        }

        private List<Hole> FindHoles(IBody2 body, double maxWidth, out Dictionary<int, IFace2> faces)
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
            var seen = new HashSet<int>();
            foreach (var (host, loopEdges) in innerLoops)
            {
                var seeds = loopEdges.Select(e => OtherFace(e, host)).Where(id => id > 0 && !seen.Contains(id)).Distinct().ToList();
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
                    continue;

                if (!Measure(hole, boundary, innerLoops, faces, faceEdges))
                    continue;
                if (hole.Depression && hole.Throat <= maxWidth)
                    holes.Add(hole);
            }
            return holes;
        }

        /// <summary>Throat = narrowest boundary loop; only loops on planar faces are measured.</summary>
        private static bool Measure(Hole hole, List<(int face, int edge)> boundary, List<(int face, List<IEdge> edges)> innerLoops,
            Dictionary<int, IFace2> faces, Dictionary<int, List<IEdge>> faceEdges)
        {
            var holePoints = hole.FaceIds.SelectMany(f => faceEdges[f]).SelectMany(e => Sample(e)).ToList();
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
                double width = LoopMetrics.MinWidth(loop, normal);
                if (width < hole.Throat)
                {
                    hole.Throat = width;
                    var c = LoopMetrics.Centroid(loop);
                    hole.Key = $"{c.X:F6};{c.Y:F6};{c.Z:F6}";
                }
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
