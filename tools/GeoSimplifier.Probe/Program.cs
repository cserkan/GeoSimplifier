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
            Console.WriteLine($"Dosya: {doc.GetPathName()}  kaydedilmemiş: {doc.GetSaveFlag()}");
            Console.WriteLine($"Belge: {doc.GetTitle()}  sınır: {gapMm} mm");

            if (Array.IndexOf(args, "--offset") >= 0)
            {
                var src = (IBody2)((object[])((IPartDoc)doc).GetBodies2(0, true))[0];
                foreach (double d in new[] { 0.0005, 0.001, 0.005 })
                    foreach (bool f in new[] { true, false })
                    {
                        var o = (IBody2)((IBody2)src.Copy()).MakeOffset(d, f);
                        Console.WriteLine($"MakeOffset({d}, {f}): {(o == null ? "null" : ((double[])o.GetBodyBox())[3] * 1000 + " mm xmax")}");
                    }
                var direct = (IBody2)src.MakeOffset(0.001, true);
                Console.WriteLine($"doğrudan: {(direct == null ? "null" : "ok")}");
                var modeler = (IModeler)swApp.GetModeler();
                var box = (IBody2)modeler.CreateBodyFromBox3(new[] { 0.0, 0, 0, 0, 0, 1, 0.1, 0.1, 0.1 });
                Console.WriteLine($"kutu ofset: {(box.MakeOffset(0.001, true) == null ? "null" : "ok")}");
                Console.WriteLine($"orijinal xmax {((double[])src.GetBodyBox())[3] * 1000} mm");
                return 0;
            }
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
            for (int i = 0; i < result.Envelope.Count; i++)
            {
                var e = (double[])result.Envelope[i].GetBodyBox();
                var b = (double[])result.Bodies[i].GetBodyBox();
                Console.WriteLine($"Zarf {i}: kutu farkı {(e[3] - e[0] - (b[3] - b[0])) * 1000:F3} / {(e[4] - e[1] - (b[4] - b[1])) * 1000:F3} / {(e[5] - e[2] - (b[5] - b[2])) * 1000:F3} mm");
            }
            if (commit)
                Console.WriteLine(service.Commit(result));
            return 0;
        }
    }
}
