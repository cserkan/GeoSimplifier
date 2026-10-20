using System;
using System.Collections.Generic;
using GeoSimplifier.Core.Geometry;

namespace GeoSimplifier.Core.Voxels
{
    /// <summary>A hole found by <see cref="HoleFiller"/>: empty cells enclosed by material in slices across one axis.</summary>
    public sealed class HolePlug
    {
        internal HolePlug(int axis) => Axis = axis;

        /// <summary>The axis the hole runs along (0 = X, 1 = Y, 2 = Z).</summary>
        public int Axis { get; }

        /// <summary>Cell indices of the hole, including countersinks and counterbores.</summary>
        public List<int> Cells { get; } = new List<int>();

        /// <summary>Narrowest cross-section width in model units (the hole's throat).</summary>
        public double Throat { get; internal set; } = double.PositiveInfinity;

        /// <summary>Where the plug should end along <see cref="Axis"/> to sit flush with the faces around the hole.</summary>
        public double Lo { get; internal set; }

        public double Hi { get; internal set; }

        internal int SliceMin = int.MaxValue, SliceMax = int.MinValue;
        internal int UMin = int.MaxValue, UMax = int.MinValue, VMin = int.MaxValue, VMax = int.MinValue;
    }

    /// <summary>
    /// Finds holes, slots and pockets: in every slice across an axis, empty regions fully enclosed by material.
    /// Regions that overlap in neighbouring slices belong to the same hole, so a countersunk hole is one hole
    /// whose throat is the bolt diameter. Closing the whole hole (not only its narrow part) keeps the plug
    /// flush with the surface instead of leaving a pocket where the countersink was.
    /// </summary>
    public static class HoleFiller
    {
        public static List<HolePlug> Find(VoxelGrid material, TriangleMesh mesh, double maxWidth)
        {
            var plugs = new List<HolePlug>();
            if (!(maxWidth > 0))
                return plugs;
            var taken = new HashSet<int>();
            for (int axis = 0; axis < 3; axis++)
            {
                foreach (var plug in FindAlong(material, axis))
                {
                    if (plug.Throat > maxWidth + 1e-12)
                        continue;
                    // A cell can be enclosed across two axes (e.g. a pocket); fill it once.
                    plug.Cells.RemoveAll(c => !taken.Add(c));
                    if (plug.Cells.Count == 0)
                        continue;
                    FitEnds(plug, material, mesh);
                    plugs.Add(plug);
                }
            }
            return plugs;
        }

        private static List<HolePlug> FindAlong(VoxelGrid g, int axis)
        {
            int u = axis == 0 ? 1 : 0;
            int v = axis == 2 ? 1 : 2;
            int nu = g.Dim(u), nv = g.Dim(v), nt = g.Dim(axis);
            var c = new int[3];
            int Cell(int t, int a, int b)
            {
                c[axis] = t; c[u] = a; c[v] = b;
                return g.Index(c[0], c[1], c[2]);
            }

            var labels = new int[nu * nv];
            var prevLabels = new int[nu * nv];
            var prevRegionPlug = new List<int>();
            var parent = new List<int>();      // union-find over plugs
            var plugs = new List<HolePlug>();
            var stack = new Stack<int>();

            int Find(int x)
            {
                while (parent[x] != x) x = parent[x] = parent[parent[x]];
                return x;
            }

            for (int t = 0; t < nt; t++)
            {
                for (int n = 0; n < labels.Length; n++) labels[n] = -1;
                var regionPlug = new List<int>();

                for (int b = 0; b < nv; b++)
                for (int a = 0; a < nu; a++)
                {
                    int p = a + nu * b;
                    if (labels[p] != -1 || g[Cell(t, a, b)])
                        continue;

                    // Flood the empty 2D region (4-connected) and see whether it reaches the slice border.
                    int region = regionPlug.Count;
                    var cells = new List<int>();
                    bool open = false;
                    int umin = a, umax = a, vmin = b, vmax = b;
                    labels[p] = region;
                    stack.Push(p);
                    while (stack.Count > 0)
                    {
                        int q = stack.Pop();
                        int qa = q % nu, qb = q / nu;
                        cells.Add(q);
                        if (qa == 0 || qb == 0 || qa == nu - 1 || qb == nv - 1) open = true;
                        if (qa < umin) umin = qa;
                        if (qa > umax) umax = qa;
                        if (qb < vmin) vmin = qb;
                        if (qb > vmax) vmax = qb;
                        void Visit(int na, int nb)
                        {
                            if (na < 0 || nb < 0 || na >= nu || nb >= nv) return;
                            int r = na + nu * nb;
                            if (labels[r] != -1 || g[Cell(t, na, nb)]) return;
                            labels[r] = region;
                            stack.Push(r);
                        }
                        Visit(qa - 1, qb); Visit(qa + 1, qb); Visit(qa, qb - 1); Visit(qa, qb + 1);
                    }

                    if (open)
                    {
                        regionPlug.Add(-1);
                        continue;
                    }

                    var plug = new HolePlug(axis);
                    int id = plugs.Count;
                    plugs.Add(plug);
                    parent.Add(id);
                    regionPlug.Add(id);
                    double width = Math.Min(umax - umin + 1, vmax - vmin + 1) * g.VoxelSize;
                    plug.Throat = width;
                    plug.SliceMin = plug.SliceMax = t;
                    plug.UMin = umin; plug.UMax = umax; plug.VMin = vmin; plug.VMax = vmax;
                    foreach (int q in cells)
                    {
                        plug.Cells.Add(Cell(t, q % nu, q / nu));
                        // Same hole as an enclosed region at the same spot in the previous slice.
                        int prev = t > 0 ? prevLabels[q] : -1;
                        if (prev >= 0 && prevRegionPlug[prev] >= 0)
                        {
                            int ra = Find(prevRegionPlug[prev]), rb = Find(id);
                            if (ra != rb) parent[rb] = ra;
                        }
                    }
                }

                var swap = prevLabels; prevLabels = labels; labels = swap;
                prevRegionPlug = regionPlug;
            }

            // Merge the per-slice regions of each hole.
            var merged = new Dictionary<int, HolePlug>();
            for (int id = 0; id < plugs.Count; id++)
            {
                int root = Find(id);
                var src = plugs[id];
                if (!merged.TryGetValue(root, out var dst))
                {
                    merged[root] = src;
                    continue;
                }
                dst.Cells.AddRange(src.Cells);
                dst.Throat = Math.Min(dst.Throat, src.Throat);
                dst.SliceMin = Math.Min(dst.SliceMin, src.SliceMin);
                dst.SliceMax = Math.Max(dst.SliceMax, src.SliceMax);
                dst.UMin = Math.Min(dst.UMin, src.UMin); dst.UMax = Math.Max(dst.UMax, src.UMax);
                dst.VMin = Math.Min(dst.VMin, src.VMin); dst.VMax = Math.Max(dst.VMax, src.VMax);
            }
            return new List<HolePlug>(merged.Values);
        }

        /// <summary>
        /// The end slices of a hole contain the faces around its mouths; take the exact height of those faces
        /// from the mesh so a plug built from boxes ends flush with them instead of on the voxel grid.
        /// </summary>
        private static void FitEnds(HolePlug plug, VoxelGrid g, TriangleMesh mesh)
        {
            int axis = plug.Axis;
            int u = axis == 0 ? 1 : 0;
            int v = axis == 2 ? 1 : 2;
            double s = g.VoxelSize;
            double o = g.Origin[axis], ou = g.Origin[u], ov = g.Origin[v];
            double topLo = o + plug.SliceMax * s, topHi = topLo + s;
            double botLo = o + plug.SliceMin * s, botHi = botLo + s;
            double fu0 = ou + (plug.UMin - 1) * s, fu1 = ou + (plug.UMax + 2) * s;
            double fv0 = ov + (plug.VMin - 1) * s, fv1 = ov + (plug.VMax + 2) * s;

            double hi = double.NegativeInfinity, lo = double.PositiveInfinity;
            foreach (var t in mesh.Triangles)
            {
                var b = t.Bounds;
                if (b.Max[u] < fu0 || b.Min[u] > fu1 || b.Max[v] < fv0 || b.Min[v] > fv1)
                    continue;
                if (b.Max[axis] >= topLo && b.Min[axis] <= topHi)
                    hi = Math.Max(hi, Math.Min(b.Max[axis], topHi));
                if (b.Max[axis] >= botLo && b.Min[axis] <= botHi)
                    lo = Math.Min(lo, Math.Max(b.Min[axis], botLo));
            }
            plug.Hi = double.IsNegativeInfinity(hi) ? topHi : hi;
            plug.Lo = double.IsPositiveInfinity(lo) ? botLo : lo;
        }
    }
}
