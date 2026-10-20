using System;
using GeoSimplifier.Core.Geometry;
using GeoSimplifier.Core.Voxels;

namespace GeoSimplifier.Core.ShrinkWrap
{
    /// <summary>
    /// Voxel shrink wrap: voxelize the surface, close small gaps, flood-fill the outside and keep
    /// everything the outside cannot reach. Internal cavities, holes and small gaps disappear and the
    /// result is a single closed envelope that lies on or slightly outside the original surface.
    /// </summary>
    public static class ShrinkWrapper
    {
        public static ShrinkWrapResult Run(TriangleMesh mesh, ShrinkWrapOptions options)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));
            if (options == null) throw new ArgumentNullException(nameof(options));

            double voxelSize = ResolveVoxelSize(mesh.Bounds, options);
            int gap = ResolveGapVoxels(voxelSize, options);

            // Padding keeps the flood fill seeded from outside even after dilation.
            var shell = Voxelizer.VoxelizeSurface(mesh, voxelSize, gap + 2, options.MaxCells);
            var closedShell = Morphology.Dilate(shell, gap);
            var solid = ExteriorFill.SolidFromShell(closedShell);
            solid = Morphology.Erode(solid, gap);

            // Erosion must not cut into the original surface: keep every surface voxel.
            for (int n = 0; n < solid.Length; n++)
                if (shell[n]) solid[n] = true;

            var fill = BuildFill(mesh, solid);

            int added = options.FixNonManifold ? ManifoldFixer.Fix(solid) : 0;
            var boxes = BoxMerger.Merge(solid);
            return new ShrinkWrapResult(solid, boxes, added, Connectivity.CountComponents(solid))
            {
                Fill = fill,
                FillBoxes = BoxMerger.Merge(fill),
            };
        }

        /// <summary>
        /// Cells to add to the original bodies so they become the wrapped solid while keeping the original
        /// exterior faces exact: closed holes, gaps and internal cavities, but nothing near the outside skin.
        /// </summary>
        private static VoxelGrid BuildFill(TriangleMesh mesh, VoxelGrid solid)
        {
            // A cell none of whose 26 neighbours is outside cannot cross the original exterior surface:
            // a cell that does always has an outside neighbour in the direction of the surface normal.
            var interior = Morphology.Erode(solid, 1);
            var inside = InsideClassifier.Classify(mesh, solid);
            var core = interior.Clone();
            for (int n = 0; n < core.Length; n++)
                if (inside[n]) core[n] = false;

            // Grow one cell into the material so plugs overlap the walls they close instead of leaving a
            // sliver gap, but stay within the interior.
            var fill = Morphology.Dilate(core, 1);
            for (int n = 0; n < fill.Length; n++)
                if (!interior[n]) fill[n] = false;
            return fill;
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
