using System;
using System.Globalization;
using System.Runtime.InteropServices;
using GeoSimplifier.Core.Wrap;
using GeoSimplifier.SolidWorks;
using SolidWorks.Interop.sldworks;

namespace GeoSimplifier.Probe
{
    /// <summary>Runs the wrap on the active document of a running SOLIDWORKS and prints what it found.</summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            double gapMm = args.Length > 0 ? double.Parse(args[0], CultureInfo.InvariantCulture) : 10;
            bool commit = Array.IndexOf(args, "--commit") >= 0;

            var swApp = (ISldWorks)Marshal.GetActiveObject("SldWorks.Application");
            var doc = (IModelDoc2)swApp.ActiveDoc;
            if (doc == null)
            {
                Console.Error.WriteLine("SOLIDWORKS'te açık belge yok.");
                return 1;
            }
            Console.WriteLine($"Belge: {doc.GetTitle()}  sınır: {gapMm} mm");

            var service = new ShrinkWrapService(swApp);
            var result = service.Compute(doc, new ShrinkWrapOptions { GapWidth = gapMm / 1000, FillCavities = true });
            Console.WriteLine(result.Summary());
            foreach (var line in result.Log)
                Console.WriteLine("  " + line);
            Console.WriteLine($"Eklenen gövde: {result.Added.Count}");
            foreach (var body in result.Bodies)
            {
                var mass = (double[])body.GetMassProperties(1);
                Console.WriteLine($"Sonuç gövdesi: {((object[])body.GetFaces()).Length} yüz, hacim {mass?[3] * 1e9:F0} mm³");
            }
            if (commit)
                Console.WriteLine(service.Commit(result));
            return 0;
        }
    }
}
