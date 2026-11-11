using System;

namespace GeoSimplifier.Core.Geometry
{
    /// <summary>Axis-aligned bounding box.</summary>
    public readonly struct Aabb
    {
        public static readonly Aabb Empty = new Aabb(
            new Vec3(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity),
            new Vec3(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity));

        public readonly Vec3 Min;
        public readonly Vec3 Max;

        public Aabb(Vec3 min, Vec3 max)
        {
            Min = min;
            Max = max;
        }

        public bool IsEmpty => Min.X > Max.X || Min.Y > Max.Y || Min.Z > Max.Z;

        public Vec3 Size => IsEmpty ? new Vec3(0, 0, 0) : Max - Min;

        public double LongestSide => Math.Max(Size.X, Math.Max(Size.Y, Size.Z));

        public Aabb Include(Vec3 p) => new Aabb(Vec3.Min(Min, p), Vec3.Max(Max, p));

        public Aabb Union(Aabb other) => new Aabb(Vec3.Min(Min, other.Min), Vec3.Max(Max, other.Max));

        public static Aabb FromPoints(params Vec3[] points)
        {
            var box = Empty;
            foreach (var p in points)
                box = box.Include(p);
            return box;
        }

        public override string ToString() => $"[{Min} .. {Max}]";
    }
}
