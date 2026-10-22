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

            var swApp = FindSolidWorksWithDocument();
            var doc = (IModelDoc2)swApp?.ActiveDoc;
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
            if (doc is IAssemblyDoc asm)
            {
                // What BodyCollector sees in an assembly: every component, its state and body boxes before and after Transform2.
                foreach (IComponent2 comp in (object[])asm.GetComponents(false) ?? new object[0])
                {
                    var children = (object[])comp.GetChildren();
                    var bodies = (object[])comp.GetBodies3(0, out _);
                    Console.WriteLine($"Bileşen {comp.Name2}: bastırılmış {comp.IsSuppressed()}, durum {comp.GetSuppression2()}, görünür {comp.Visible}, " +
                                      $"alt {children?.Length ?? 0}, gövde {bodies?.Length ?? 0}");
                    if (bodies == null) continue;
                    foreach (IBody2 body in bodies)
                    {
                        var copy = (IBody2)body.Copy();
                        var raw = (double[])copy.GetBodyBox();
                        if (comp.Transform2 != null) copy.ApplyTransform(comp.Transform2);
                        var moved = (double[])copy.GetBodyBox();
                        Console.WriteLine($"    kutu {Box(raw)} → dönüşümle {Box(moved)}");
                    }
                }
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

        private static string Box(double[] b) =>
            b == null ? "-" : string.Format(CultureInfo.InvariantCulture, "[{0:F1} {1:F1} {2:F1} .. {3:F1} {4:F1} {5:F1}]",
                b[0] * 1000, b[1] * 1000, b[2] * 1000, b[3] * 1000, b[4] * 1000, b[5] * 1000);

        [DllImport("ole32.dll")]
        private static extern int GetRunningObjectTable(int reserved, out System.Runtime.InteropServices.ComTypes.IRunningObjectTable rot);

        [DllImport("ole32.dll")]
        private static extern int CreateBindCtx(int reserved, out System.Runtime.InteropServices.ComTypes.IBindCtx ctx);

        /// <summary>
        /// The running SOLIDWORKS that has an active document. With several instances open, GetActiveObject returns
        /// whichever registered first, which may be an empty one.
        /// </summary>
        private static ISldWorks FindSolidWorksWithDocument()
        {
            ISldWorks fallback = null;
            GetRunningObjectTable(0, out var rot);
            CreateBindCtx(0, out var ctx);
            rot.EnumRunning(out var monikers);
            var one = new System.Runtime.InteropServices.ComTypes.IMoniker[1];
            while (monikers.Next(1, one, IntPtr.Zero) == 0)
            {
                one[0].GetDisplayName(ctx, null, out string name);
                if (!name.StartsWith("SolidWorks_PID_", StringComparison.OrdinalIgnoreCase))
                    continue;
                rot.GetObject(one[0], out object obj);
                if (obj is ISldWorks app)
                {
                    if (app.ActiveDoc != null)
                        return app;
                    fallback = fallback ?? app;
                }
            }
            return fallback ?? (ISldWorks)Marshal.GetActiveObject("SldWorks.Application");
        }
    }
}
