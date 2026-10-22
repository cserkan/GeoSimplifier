using System;
using System.Globalization;
using System.Linq;
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
            if (Array.IndexOf(args, "--quit") >= 0)
            {
                // Closes every document without saving and exits SOLIDWORKS, so a rebuilt add-in can be loaded.
                swApp.CloseAllDocuments(true);
                swApp.ExitApp();
                Console.WriteLine("SOLIDWORKS kapatıldı.");
                return 0;
            }
            if (Array.IndexOf(args, "--local-ops") >= 0)
            {
                // Runs SwAddin.TestLocalOps inside SOLIDWORKS; results go to %TEMP%\GeoSimplifier.log.
                var addin = swApp.GetAddInObject("GeoSimplifier.SwAddin");
                Console.WriteLine(addin.GetType().InvokeMember("TestLocalOps", System.Reflection.BindingFlags.InvokeMethod, null, addin, new object[0]));
                return 0;
            }
            int test = Array.IndexOf(args, "--page-test");
            if (test >= 0)
            {
                // Opens the Shrink Wrap page inside SOLIDWORKS and types limits into it: --page-test "50;30;80" [--ok].
                var addin = swApp.GetAddInObject("GeoSimplifier.SwAddin");
                if (addin == null) { Console.Error.WriteLine("Eklenti bulunamadı."); return 1; }
                int ex = Array.IndexOf(args, "--exclude");
                var callArgs = new object[] { args[test + 1], Array.IndexOf(args, "--ok") >= 0, ex >= 0 ? args[ex + 1] : "" };
                Console.WriteLine(addin.GetType().InvokeMember("TestPageExcluding", System.Reflection.BindingFlags.InvokeMethod, null, addin, callArgs));
                return 0;
            }
            if (Array.IndexOf(args, "--docs") >= 0)
            {
                // Lists open documents; for parts, every solid body's face count and volume.
                foreach (IModelDoc2 d in (object[])swApp.GetDocuments() ?? new object[0])
                {
                    Console.WriteLine($"{d.GetTitle()} ({d.GetPathName()})");
                    if (d is IPartDoc pd)
                        foreach (IBody2 b in (object[])pd.GetBodies2(0, true) ?? new object[0])
                            Console.WriteLine($"    gövde: {((object[])b.GetFaces()).Length} yüz, hacim {((double[])b.GetMassProperties(1))[3] * 1e9:F0} mm³");
                }
                return 0;
            }
            int make = Array.IndexOf(args, "--make");
            if (make >= 0 && make + 1 < args.Length)
            {
                // Builds a test part from boxes in a new document:
                //   --make emboss : a 100x60x5 mm plate with 1 mm high "letters" and a 10x10x5 mm boss on top (one body)
                //   --make gaps   : three 50x50x20 mm blocks 5 mm and 20 mm apart (three bodies)
                var ops = new GeoSimplifier.SolidWorks.Geometry.BodyOps(swApp);
                GeoSimplifier.Core.Geometry.Aabb Mm(double x0, double y0, double z0, double x1, double y1, double z1) =>
                    new GeoSimplifier.Core.Geometry.Aabb(new GeoSimplifier.Core.Geometry.Vec3(x0 / 1000, y0 / 1000, z0 / 1000),
                                                         new GeoSimplifier.Core.Geometry.Vec3(x1 / 1000, y1 / 1000, z1 / 1000));
                var parts = new System.Collections.Generic.List<IBody2>();
                if (args[make + 1] == "emboss")
                {
                    var plate = ops.Box(Mm(0, 0, 0, 100, 60, 5));
                    foreach (var letter in new[] { Mm(10, 10, 5, 12, 25, 6), Mm(12, 23, 5, 20, 25, 6), Mm(12, 16, 5, 18, 18, 6),
                                                   Mm(25, 10, 5, 27, 25, 6), Mm(27, 10, 5, 35, 12, 6), Mm(60, 30, 5, 70, 40, 10) })
                        plate = ops.Union(plate, ops.Box(letter)) ?? plate;
                    parts.Add(plate);
                }
                else
                {
                    parts.Add(ops.Box(Mm(0, 0, 0, 50, 50, 20)));
                    parts.Add(ops.Box(Mm(55, 0, 0, 105, 50, 20)));
                    parts.Add(ops.Box(Mm(125, 0, 0, 175, 50, 20)));
                }
                string template = swApp.GetUserPreferenceStringValue((int)global::SolidWorks.Interop.swconst.swUserPreferenceStringValue_e.swDefaultTemplatePart);
                var part = (IPartDoc)swApp.NewDocument(template, 0, 0, 0);
                foreach (var body in parts)
                    part.CreateFeatureFromBody3(body, false, 0);
                ((IModelDoc2)part).ViewZoomtofit2();
                Console.WriteLine($"Test parçası: {args[make + 1]}, {parts.Count} gövde, {((IModelDoc2)part).GetTitle()}");
                return 0;
            }
            int open = Array.IndexOf(args, "--open");
            if (open >= 0 && open + 1 < args.Length)
            {
                // Opens a part or assembly first (e.g. after SOLIDWORKS was restarted to load a rebuilt add-in).
                string path = args[open + 1];
                int type = path.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
                int errors = 0, warnings = 0;
                swApp.OpenDoc6(path, type, 0, "", ref errors, ref warnings);
                int activateErrors = 0;
                swApp.ActivateDoc3(System.IO.Path.GetFileName(path), false, 0, ref activateErrors);
                Console.WriteLine($"Açıldı: {path} (hata {errors}, uyarı {warnings})");
                return 0;
            }
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
            if (Array.IndexOf(args, "--boxtest") >= 0)
            {
                // Where BoxOnAxis and SolidCylinder put their bodies, for a few axis directions.
                var ops = new GeoSimplifier.SolidWorks.Geometry.BodyOps(swApp);
                foreach (var axis in new[] { new GeoSimplifier.Core.Geometry.Vec3(0, 0, 1), new GeoSimplifier.Core.Geometry.Vec3(0, 0, -1),
                                             new GeoSimplifier.Core.Geometry.Vec3(1, 0, 0), new GeoSimplifier.Core.Geometry.Vec3(-1, 0, 0) })
                {
                    var b = ops.BoxOnAxis(new GeoSimplifier.Core.Geometry.Vec3(0, 0, 0), axis, 0.002, 0.010);
                    var c = ops.SolidCylinder(new GeoSimplifier.Core.Geometry.Vec3(0, 0, 0), axis, 0.001, 0.010);
                    Console.WriteLine($"eksen {axis}: kutu {Box((double[])b.GetBodyBox())}, silindir {Box((double[])c.GetBodyBox())}");
                }
                return 0;
            }
            if (Array.IndexOf(args, "--cyl") >= 0)
            {
                // Lists small cylindrical faces of the united body with their neighbours, to see how holes are built.
                var ops = new GeoSimplifier.SolidWorks.Geometry.BodyOps(swApp);
                var lumps = ops.UnionAll(GeoSimplifier.SolidWorks.Geometry.BodyCollector.CopyBodies(doc), 1e-6);
                foreach (var lump in lumps)
                {
                    var faces = ((object[])lump.GetFaces()).Cast<IFace2>().ToArray();
                    for (int i = 0; i < faces.Length; i++) faces[i].SetFaceId(i + 1);
                    foreach (var f in faces)
                    {
                        var surf = (ISurface)f.GetSurface();
                        if (!surf.IsCylinder()) continue;
                        var cp = (double[])surf.CylinderParams; // origin, axis, radius
                        if (cp[6] > gapMm / 2000) continue;
                        var neighbours = new System.Collections.Generic.SortedSet<string>();
                        foreach (IEdge e in (object[])f.GetEdges())
                            foreach (IFace2 g in (object[])e.GetTwoAdjacentFaces2())
                                if (g != null && g.GetFaceId() != f.GetFaceId())
                                {
                                    var gs = (ISurface)g.GetSurface();
                                    string kind = gs.IsPlane() ? "düzlem" : gs.IsCylinder() ? $"silindir r{((double[])gs.CylinderParams)[6] * 1000:F1}" : gs.IsCone() ? "koni" : "diğer";
                                    neighbours.Add($"{g.GetFaceId()}:{kind}");
                                }
                        var box = (double[])f.GetBox();
                        Console.WriteLine($"Yüz {f.GetFaceId()}: r {cp[6] * 1000:F2} mm, eksen ({cp[3]:F2},{cp[4]:F2},{cp[5]:F2}), ters {f.FaceInSurfaceSense()}, " +
                                          $"kutu {Box(box)}, komşular {string.Join(" ", neighbours)}");
                    }
                }
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
            GeoSimplifier.SolidWorks.Geometry.WallHoleCloser.Verbose = Array.IndexOf(args, "--verbose") >= 0;
            var service = new ShrinkWrapService(swApp);
            // --exclude "Part-1;Part-2" keeps those components (Name2) out of the wrap.
            var exclusions = new GeoSimplifier.SolidWorks.Geometry.Exclusions();
            int exclude = Array.IndexOf(args, "--exclude");
            if (exclude >= 0 && exclude + 1 < args.Length)
                foreach (var name in args[exclude + 1].Split(';'))
                    exclusions.AddComponentName(name);
            // --emboss 2 flattens embossed details up to 2 mm high.
            int emboss = Array.IndexOf(args, "--emboss");
            double embossMm = emboss >= 0 && emboss + 1 < args.Length ? double.Parse(args[emboss + 1], CultureInfo.InvariantCulture) : 0;
            var result = service.Compute(doc, new ShrinkWrapOptions { GapWidth = gapMm / 1000, FillCavities = true, MaxEmbossHeight = embossMm / 1000 }, exclusions);
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
