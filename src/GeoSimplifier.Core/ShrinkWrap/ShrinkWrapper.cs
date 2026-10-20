using System;
using System.Collections.Generic;
using GeoSimplifier.Core.Geometry;
using GeoSimplifier.Core.Voxels;

namespace GeoSimplifier.Core.ShrinkWrap
{
    /// <summary>
    /// Voxel shrink wrap: voxelize the surface, flood-fill the outside and keep everything the outside cannot
    /// reach (so internal cavities disappear), plug holes up to the requested width flush with their faces, and
    /// bridge gaps between separate bodies.
    /// </summary>
    public static class ShrinkWrapper
    {
        public static ShrinkWrapResult Run(TriangleMesh mesh, ShrinkWrapOptions options)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            if (options == null) throw new ArgumentNullException(nameof(options));

            double voxelSize = ResolveVoxelSize(mesh.Bounds, options);
            int gap = ResolveGapVoxels(voxelSize, options);
            double maxWidth = options.GapClosingWidth ?? 2 * gap * voxelSize;

            // Padding keeps the flood fill seeded from outside even after dilation.
            var shell = Voxelizer.VoxelizeSurface(mesh, voxelSize, gap + 2, options.MaxCells);
            var material = ExteriorFill.SolidFromShell(shell);

            // Holes, slots and pockets narrower than the limit are filled completely.
            var plugs = HoleFiller.Find(material, mesh, maxWidth);
            var solid = material.Clone();
            foreach (var plug in plugs)
                foreach (int cell in plug.Cells)
                    solid[cell] = true;

            // Separate bodies closer than the limit are bridged by a morphological closing. Only then: on a
            // single body the closing would also round off every concave edge.
            VoxelGrid bridge = null;
            if (gap > 0 && Connectivity.CountComponents(solid) > 1)
            {
                var closed = Morphology.Erode(ExteriorFill.SolidFromShell(Morphology.Dilate(solid, gap)), gap);
                bridge = solid.CloneEmpty();
                for (int n = 0; n < solid.Length; n++)
                {
                    if (closed[n] && !solid[n])
                    {
                        bridge[n] = true;
                        solid[n] = true;
                    }
                }
            }

            var exactFill = new List<Aabb>();
            foreach (var plug in plugs)
                exactFill.AddRange(PlugBoxes(plug, solid));
            if (bridge != null)
                exactFill.AddRange(BridgeBoxes(bridge, solid));

            int added = options.FixNonManifold ? ManifoldFixer.Fix(solid) : 0;
            var boxes = BoxMerger.Merge(solid);
            return new ShrinkWrapResult(solid, boxes, added, Connectivity.CountComponents(solid))
            {
                Holes = plugs,
                ExactFillBoxes = exactFill,
            };
        }

        /// <summary>
        /// Boxes that plug a hole when unioned with the original bodies: grown one cell sideways so they overlap
        /// the hole walls, and cut along the hole axis to end flush with the faces around the hole mouths.
        /// </summary>
        private static IEnumerable<Aabb> PlugBoxes(HolePlug plug, VoxelGrid like)
        {
            var sub = SubGrid(plug.Cells, like, out int i0, out int j0, out int k0);
            double s = like.VoxelSize;
            int axis = plug.Axis;
            foreach (var b in BoxMerger.Merge(sub))
            {
                var min = like.CellMin(b.I0 + i0, b.J0 + j0, b.K0 + k0);
                var max = like.CellMin(b.I1 + i0, b.J1 + j0, b.K1 + k0);
                var lo = new double[3];
                var hi = new double[3];
                for (int a = 0; a < 3; a++)
                {
                    // Stacked boxes overlap slightly along the axis too, but never past the mouth faces.
                    lo[a] = a == axis ? Math.Max(min[a] - 0.01 * s, plug.Lo) : min[a] - s;
                    hi[a] = a == axis ? Math.Min(max[a] + 0.01 * s, plug.Hi) : max[a] + s;
                }
                if (hi[axis] > lo[axis])
                    yield return new Aabb(new Vec3(lo[0], lo[1], lo[2]), new Vec3(hi[0], hi[1], hi[2]));
            }
        }

        /// <summary>
        /// Bridge cells that cannot cross the original exterior: a cell none of whose 26 neighbours is outside
        /// never does. Grown one cell so the bridge overlaps the bodies it connects.
        /// </summary>
        private static IEnumerable<Aabb> BridgeBoxes(VoxelGrid bridge, VoxelGrid solid)
        {
            var interior = Morphology.Erode(solid, 1);
            var core = bridge.Clone();
            for (int n = 0; n < core.Length; n++)
                if (!interior[n]) core[n] = false;
            var fill = Morphology.Dilate(core, 1);
            for (int n = 0; n < fill.Length; n++)
                if (!interior[n]) fill[n] = false;
            double e = 0.01 * fill.VoxelSize;
            var grow = new Vec3(e, e, e);
            foreach (var b in BoxMerger.Merge(fill))
            {
                var w = b.ToWorld(fill);
                yield return new Aabb(w.Min - grow, w.Max + grow);
            }
        }

        private static VoxelGrid SubGrid(List<int> cells, VoxelGrid like, out int i0, out int j0, out int k0)
        {
            int nx = like.Nx, ny = like.Ny;
            int imin = int.MaxValue, jmin = int.MaxValue, kmin = int.MaxValue, imax = 0, jmax = 0, kmax = 0;
            foreach (int c in cells)
            {
                int i = c % nx, j = c / nx % ny, k = c / (nx * ny);
                imin = Math.Min(imin, i); imax = Math.Max(imax, i);
                jmin = Math.Min(jmin, j); jmax = Math.Max(jmax, j);
                kmin = Math.Min(kmin, k); kmax = Math.Max(kmax, k);
            }
            var sub = new VoxelGrid(imax - imin + 1, jmax - jmin + 1, kmax - kmin + 1, like.CellMin(imin, jmin, kmin), like.VoxelSize);
            foreach (int c in cells)
                sub[c % nx - imin, c / nx % ny - jmin, c / (nx * ny) - kmin] = true;
            i0 = imin; j0 = jmin; k0 = kmin;
            return sub;
        }

        /// <summary>Closing radius in voxels; a closing of radius r bridges openings up to about 2r voxels wide.</summary>
        public static int ResolveGapVoxels(double voxelSize, ShrinkWrapOptions options)
        {
            if (options.GapClosingWidth.HasValue)
                return Math.Max(0, (int)Math.Ceiling(options.GapClosingWidth.Value / (2 * voxelSize)));
            return Math.Max(0, options.GapClosingVoxels);
        }

        public static double ResolveVoxelSize(Aabb bounds, ShrinkWrapOptions options)
        {
            if (options.VoxelSize.HasValue)
            {
                if (options.VoxelSize.Value <= 0)
                    throw new ArgumentOutOfRangeException(nameof(options), "VoxelSize must be positive.");
                return options.VoxelSize.Value;
            }
            if (options.Resolution < 2)
                throw new ArgumentOutOfRangeException(nameof(options), "Resolution must be at least 2.");
            double longest = bounds.LongestSide;
            if (!(longest > 0))
                throw new ArgumentException("Model has zero size.", nameof(bounds));
            return longest / options.Resolution;
        }
    }
}
