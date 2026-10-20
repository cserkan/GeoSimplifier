namespace GeoSimplifier.Core.ShrinkWrap
{
    public sealed class ShrinkWrapOptions
    {
        /// <summary>Number of voxels along the longest side of the model. Ignored when <see cref="VoxelSize"/> is set.</summary>
        public int Resolution { get; set; } = 96;

        /// <summary>Explicit voxel edge length in model units (meters for SolidWorks). Overrides <see cref="Resolution"/>.</summary>
        public double? VoxelSize { get; set; }

        /// <summary>
        /// Closing radius in voxels. Gaps and holes narrower than about twice this many voxels are
        /// bridged, so separate bodies close to each other become one. 0 disables closing.
        /// </summary>
        public int GapClosingVoxels { get; set; } = 2;

        /// <summary>
        /// Widest hole or gap to close, in model units. Overrides <see cref="GapClosingVoxels"/> when set;
        /// 0 keeps every hole open.
        /// </summary>
        public double? GapClosingWidth { get; set; }

        /// <summary>Fill edge/vertex-only contacts so the result is a valid manifold solid.</summary>
        public bool FixNonManifold { get; set; } = true;

        /// <summary>Safety limit on grid size (one byte per cell).</summary>
        public long MaxCells { get; set; } = 256L * 1024 * 1024;
    }
}
