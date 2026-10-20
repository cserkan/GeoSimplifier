using System.Collections.Generic;
using GeoSimplifier.Core.Voxels;

namespace GeoSimplifier.Core.ShrinkWrap
{
    public sealed class ShrinkWrapResult
    {
        public ShrinkWrapResult(VoxelGrid solid, List<VoxelBox> boxes, int manifoldCellsAdded, int bodyCount)
        {
            BodyCount = bodyCount;
            Solid = solid;
            Boxes = boxes;
            ManifoldCellsAdded = manifoldCellsAdded;
        }

        /// <summary>Filled voxels of the wrapped solid.</summary>
        public VoxelGrid Solid { get; }

        /// <summary>Disjoint boxes covering <see cref="Solid"/>; their union is the wrapped body.</summary>
        public List<VoxelBox> Boxes { get; }

        public int ManifoldCellsAdded { get; }

        /// <summary>
        /// Cells to union with the original bodies (closed holes, gaps, cavities) when the original exterior
        /// faces are kept exactly. Stays clear of the outside skin, so it never changes the visible surface
        /// except where a hole or gap is closed.
        /// </summary>
        public VoxelGrid Fill { get; internal set; }

        public List<VoxelBox> FillBoxes { get; internal set; }

        /// <summary>Number of separate solids. More than 1 means some parts are farther apart than the gap closing distance.</summary>
        public int BodyCount { get; }

        public double VoxelSize => Solid.VoxelSize;
    }
}
