using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace GeoSimplifier.SolidWorks.UI
{
    /// <summary>
    /// The 3DWrap logo as SOLIDWORKS wants it. SOLIDWORKS reads images from files, so the PNGs embedded in the add-in
    /// (Resources\Logo; the .svg files there are their source) are written out when they are needed.
    /// </summary>
    internal static class IconFactory
    {
        private static readonly int[] Sizes = { 20, 32, 40, 64, 96, 128 };

        /// <summary>The name of the Add-Ins dialog's icon, written next to the DLL when the add-in is registered.</summary>
        public const string AddinListIconName = "3dwrap_16.bmp";

        private static string TempFolder()
        {
            string dir = Path.Combine(Path.GetTempPath(), "3DWrap");
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static Stream Logo(int size) =>
            typeof(IconFactory).Assembly.GetManifestResourceStream($"GeoSimplifier.SolidWorks.Logo.3dwrap_{size}.png")
            ?? throw new InvalidOperationException($"Logo {size} px is not embedded in the add-in.");

        /// <summary>The command icon strips (one icon per command, one file per size).</summary>
        public static string[] CreateIconStrips()
        {
            string dir = TempFolder();
            var paths = new string[Sizes.Length];
            for (int n = 0; n < Sizes.Length; n++)
            {
                paths[n] = Path.Combine(dir, $"3dwrap_{Sizes[n]}.png");
                using (var logo = Logo(Sizes[n]))
                using (var file = File.Create(paths[n]))
                    logo.CopyTo(file);
            }
            return paths;
        }

        /// <summary>The bitmap in the PropertyManager page's title.</summary>
        public static string CreatePageTitleBitmap()
        {
            string path = Path.Combine(TempFolder(), "3dwrap_title.bmp");
            WriteBitmap(path, 20, Color.FromArgb(247, 247, 247));
            return path;
        }

        /// <summary>The icon of the Add-Ins dialog, next to the DLL. Returns its path.</summary>
        public static string WriteAddinListIcon(string folder)
        {
            string path = Path.Combine(folder, AddinListIconName);
            WriteBitmap(path, 16, Color.White);
            return path;
        }

        // Bitmap files have no transparency, so the logo is drawn on the colour of what is behind it.
        private static void WriteBitmap(string path, int size, Color background)
        {
            using (var logo = Logo(20))
            using (var source = Image.FromStream(logo))
            using (var bmp = new Bitmap(size, size, PixelFormat.Format24bppRgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(background);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(source, 0, 0, size, size);
                }
                bmp.Save(path, ImageFormat.Bmp);
            }
        }
    }
}
