using System.Collections.Generic;
using System.Linq;
using GeoSimplifier.Core.Geometry;
using Xunit;

namespace GeoSimplifier.Core.Tests
{
    public class GrillesTests
    {
        private static Aabb Box(double x0, double y0, double z0, double x1, double y1, double z1) =>
            new Aabb(new Vec3(x0, y0, z0), new Vec3(x1, y1, z1));

        [Fact]
        public void No_surfaces_find_nothing()
        {
            var found = Grilles.Find(new List<Aabb>(), new List<string>(), Enumerable.Empty<int>(), 200);
            Assert.Empty(found);
        }

        [Fact]
        public void A_part_with_no_small_surfaces_finds_nothing()
        {
            // 200 surfaces of one part, each as long as the part: none under 5 % of it (a TAG3 variant failed here).
            var boxes = Enumerable.Range(0, 200).Select(i => Box(0, i * 0.01, 0, 1, i * 0.01 + 0.005, 1)).ToList();
            var owners = boxes.Select(_ => "plate-1").ToList();
            var found = Grilles.Find(boxes, owners, Enumerable.Range(0, boxes.Count), 200);
            Assert.Empty(found);
        }

        [Fact]
        public void A_dense_flat_layer_of_small_surfaces_is_one_panel_and_the_frame_stays()
        {
            // A 600 x 600 mm grille of 10 mm squares in the plane x = 0, inside a 2 m frame of four long bars.
            var boxes = new List<Aabb>();
            for (int u = 0; u < 60; u++)
                for (int v = 0; v < 60; v++)
                    boxes.Add(Box(0, u * 0.01, v * 0.01, 0.002, u * 0.01 + 0.008, v * 0.01 + 0.008));
            int grille = boxes.Count;
            boxes.Add(Box(0, -0.7, -0.7, 0.05, 1.3, -0.65));
            boxes.Add(Box(0, -0.7, 1.25, 0.05, 1.3, 1.3));
            boxes.Add(Box(0, -0.7, -0.7, 0.05, -0.65, 1.3));
            boxes.Add(Box(0, 1.25, -0.7, 0.05, 1.3, 1.3));
            var owners = boxes.Select(_ => "radiator-1").ToList();
            var found = Grilles.Find(boxes, owners, Enumerable.Range(0, boxes.Count), 200);
            Assert.Single(found);
            Assert.Equal(grille, found[0].bodies.Count);
            Assert.All(found[0].bodies, i => Assert.True(i < grille));
        }
    }
}
