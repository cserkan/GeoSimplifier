using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace GeoSimplifier.SolidWorks.UI
{
    /// <summary>Draws the command icon strips SOLIDWORKS needs (one icon per command, one file per size).</summary>
    internal static class IconFactory
    {
        private static readonly int[] Sizes = { 20, 32, 40, 64, 96, 128 };

        public static string[] CreateIconStrips()
        {
            string dir = Path.Combine(Path.GetTempPath(), "GeoSimplifier");
            Directory.CreateDirectory(dir);
            var paths = new string[Sizes.Length];
            for (int n = 0; n < Sizes.Length; n++)
            {
                int s = Sizes[n];
                paths[n] = Path.Combine(dir, $"shrinkwrap_{s}.png");
                using (var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                    using (var part = new SolidBrush(Color.FromArgb(110, 110, 110)))
                    using (var envelope = new Pen(Color.FromArgb(0, 120, 215), s * 0.09f))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.Clear(Color.Transparent);
                        // A grey part with bumps, wrapped by a blue envelope.
                        g.FillRectangle(part, s * 0.3f, s * 0.3f, s * 0.4f, s * 0.4f);
                        g.FillEllipse(part, s * 0.2f, s * 0.2f, s * 0.24f, s * 0.24f);
                        g.FillEllipse(part, s * 0.56f, s * 0.56f, s * 0.24f, s * 0.24f);
                        float m = s * 0.1f;
                        g.DrawRectangle(envelope, m, m, s - 2 * m, s - 2 * m);
                    }
                    bmp.Save(paths[n], ImageFormat.Png);
                }
            }
            return paths;
        }
    }
}
