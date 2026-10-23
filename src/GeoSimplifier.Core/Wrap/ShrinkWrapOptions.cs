namespace GeoSimplifier.Core.Wrap
{
    /// <summary>Shrink wrap settings. Lengths are in model units (meters for SOLIDWORKS).</summary>
    public sealed class ShrinkWrapOptions
    {
        /// <summary>
        /// How far the wrap may not follow the model into openings: holes, slots and pockets whose mouth is
        /// at most this wide are closed flush with the surrounding faces, and separate bodies closer than this
        /// are bridged. 0 keeps every opening.
        /// </summary>
        public double GapWidth { get; set; } = 0.01;

        /// <summary>
        /// Raised details on flat faces (embossed text, symbols, small bosses) at most this high are flattened
        /// so the wrap does not follow them. 0 keeps them.
        /// </summary>
        public double MaxEmbossHeight { get; set; }

        /// <summary>Fill closed internal cavities so the result is solid inside.</summary>
        public bool FillCavities { get; set; } = true;

        /// <summary>
        /// A quick, rough result for the preview: plugs are found but not united with the bodies, the union of the
        /// bodies is reused between previews and cavities are not filled. The real result is computed on OK.
        /// </summary>
        public bool Preview { get; set; }

        /// <summary>
        /// Experimental voxel "bag" (VoxelClosing): fills open shells and gaps, but with a stepped surface where it
        /// bridges; off by default (it spoiled the outer faces of a motor).
        /// </summary>
        public bool Bag { get; set; }
    }
}
