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
            int gap = Math.Max(0, options.GapClosingVoxels);

            // Padding keeps the flood fill seeded from outside even after dilation.
            var shell = Voxelizer.VoxelizeSurface(mesh, voxelSize, gap + 2, options.MaxCells);
            var closedShell = Morphology.Dilate(shell, gap);
            var solid = ExteriorFill.SolidFromShell(closedShell);
            solid = Morphology.Erode(solid, gap);

            // Erosion must not cut into the original surface: keep every surface voxel.
            for (int n = 0; n < solid.Length; n++)
                if (shell[n]) solid[n] = true;

            int added = options.FixNonManifold ? ManifoldFixer.Fix(solid) : 0;
            var boxes = BoxMerger.Merge(solid);
            return new ShrinkWrapResult(solid, boxes, added, Connectivity.CountComponents(solid));
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
