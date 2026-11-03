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

        /// <summary>
        /// Parts (bodies) smaller than this percentage of the model's size (box diagonals) are left out of the wrap from
        /// the start, even on the outside: screws, nuts, washers, small fittings. Their holes are closed like any other.
        /// Lightens and speeds up the result; 0 keeps every part.
        /// </summary>
        public double MinPartPercent { get; set; }

        /// <summary>
        /// Modeled screw threads are filled with a plain cylinder of their outer diameter (a screw becomes a shank,
        /// a nut a solid ring), which removes most of their faces. Always on in the add-in (Serkan: threads are
        /// never wanted in a simplified model).
        /// </summary>
        public bool FillThreads { get; set; } = true;

        /// <summary>
        /// The result is wanted as its outer skin only (surface bodies): parts no ray from outside reaches are then
        /// left out from the start. A solid result keeps them, so its inside stays filled where they were.
        /// </summary>
        public bool SurfaceShell { get; set; }

        /// <summary>
        /// Unite all bodies into as few as possible (and bridge the gaps between them). Off, each body keeps its own
        /// plugs and filled insides but is not united with the others: many bodies, much faster.
        /// </summary>
        public bool ForceSingleBody { get; set; }

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
