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

        /// <summary>Fill closed internal cavities so the result is solid inside.</summary>
        public bool FillCavities { get; set; } = true;
    }
}
