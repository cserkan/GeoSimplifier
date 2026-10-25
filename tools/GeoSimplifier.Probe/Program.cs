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
            int deps = Array.IndexOf(args, "--deps");
            if (deps >= 0 && deps + 1 < args.Length)
            {
                // --deps <file>: the documents a file references (all levels), their count and total size.
                var list = (string[])swApp.GetDocumentDependencies2(args[deps + 1], true, true, false) ?? new string[0];
                // Pairs of name and path.
                var paths = list.Where((v, i) => i % 2 == 1).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                long bytes = new System.IO.FileInfo(args[deps + 1]).Length + paths.Where(System.IO.File.Exists).Sum(p => new System.IO.FileInfo(p).Length);
                Console.WriteLine($"Bağımlılık: {paths.Count + 1} dosya, {bytes / 1048576.0:F1} MB");
                return 0;
            }
            int image = Array.IndexOf(args, "--image");
            if (image >= 0 && image + 1 < args.Length)
            {
                // --image <file.png>: saves the active document's view as a picture (works with SOLIDWORKS hidden).
                var md = (IModelDoc2)swApp.ActiveDoc;
                bool saved = md.SaveBMP(args[image + 1], 1600, 1000);
                Console.WriteLine($"Görüntü: {saved} ({args[image + 1]})");
                return 0;
            }
            int sewAt = Array.IndexOf(args, "--sew");
            if (sewAt >= 0 && sewAt + 1 < args.Length)
            {
                // --sew <part> [tol mm]: puts copies of the part's surface bodies into a new hidden part and knits
                // them with the Knit Surface feature (try to form solid); prints what comes out. Nothing is saved.
                double tol = sewAt + 2 < args.Length ? double.Parse(args[sewAt + 2], CultureInfo.InvariantCulture) / 1000 : 1e-4;
                int oe = 0, ow = 0;
                var src = (IPartDoc)swApp.OpenDoc6(args[sewAt + 1], 1, 1, "", ref oe, ref ow);
                var sheets = ((object[])src.GetBodies2(1, true) ?? new object[0]).Cast<IBody2>().ToList();
                var watch = System.Diagnostics.Stopwatch.StartNew();
                string template = swApp.GetUserPreferenceStringValue(8);
                swApp.DocumentVisible(false, 1);
                var temp = (IPartDoc)swApp.NewDocument(template, 0, 0, 0);
                swApp.DocumentVisible(true, 1);
                var md = (IModelDoc2)temp;
                md.FeatureManager.EnableFeatureTree = false;
                foreach (var s in sheets)
                    temp.CreateFeatureFromBody3((IBody2)s.Copy(), false, 0);
                double tPut = watch.Elapsed.TotalSeconds;
                md.ClearSelection2(true);
                int picked = 0;
                foreach (IBody2 b in (object[])temp.GetBodies2(1, true) ?? new object[0])
                    foreach (IFace2 face in (object[])b.GetFaces())
                        if (((IEntity)face).Select4(true, null)) picked++;
                Console.WriteLine($"seçilen yüz {picked}, seçim {((ISelectionMgr)md.SelectionManager).GetSelectedObjectCount2(-1)}");
                var feat = md.FeatureManager.InsertSewRefSurface(false, true, false, tol, tol);
                var solids = ((object[])temp.GetBodies2(0, true) ?? new object[0]).Cast<IBody2>().ToList();
                var rest = ((object[])temp.GetBodies2(1, true) ?? new object[0]).Cast<IBody2>().ToList();
                Console.WriteLine($"{sheets.Count} yüzey → dikme {(feat != null ? "oldu" : "OLMADI")}: {solids.Count} katı, {rest.Count} yüzey " +
                                  $"({rest.Sum(b => ((object[])b.GetFaces()).Length)} yüz); koyma {tPut:F1} sn, toplam {watch.Elapsed.TotalSeconds:F1} sn");
                swApp.CloseDoc(md.GetTitle());
                return 0;
            }
            int tessAt = Array.IndexOf(args, "--tess-speed");
            if (tessAt >= 0 && tessAt + 1 < args.Length)
            {
                // --tess-speed <open assembly path>: how long reading face triangles takes on the components' own
                // bodies and on transformed copies, and whether the coordinates agree.
                var tAsm = (IAssemblyDoc)swApp.GetOpenDocumentByName(args[tessAt + 1]);
                var comps = ((object[])tAsm.GetComponents(false) ?? new object[0]).Cast<IComponent2>()
                    .Where(c => c.GetSuppression2() != 0 && !(((object[])c.GetChildren())?.Length > 0)).Take(400).ToList();
                var w = System.Diagnostics.Stopwatch.StartNew();
                int faces = 0; long tris = 0;
                float[] firstOrig = null, firstCopy = null;
                foreach (var c in comps)
                    foreach (IBody2 b in (object[])c.GetBodies3(1, out _) ?? new object[0])
                        foreach (IFace2 f in (object[])b.GetFaces() ?? new object[0])
                        {
                            var t = f.GetTessTriangles(true) as float[];
                            var n = f.GetTessNorms() as float[];
                            faces++; tris += (t?.Length ?? 0) / 9;
                            if (firstOrig == null && t != null && t.Length > 0) firstOrig = t;
                        }
                double orig = w.Elapsed.TotalSeconds;
                Console.WriteLine($"Asıl gövdeler: {faces} yüz, {tris} üçgen, {orig:F1} sn ({orig * 1000 / Math.Max(1, faces):F2} ms/yüz)");
                w.Restart(); faces = 0; tris = 0;
                double copyOnly = 0;
                foreach (var c in comps)
                    foreach (IBody2 b in (object[])c.GetBodies3(1, out _) ?? new object[0])
                    {
                        var cw = System.Diagnostics.Stopwatch.StartNew();
                        var copy = (IBody2)b.Copy();
                        if (c.Transform2 != null) copy.ApplyTransform((MathTransform)c.Transform2);
                        copyOnly += cw.Elapsed.TotalSeconds;
                        foreach (IFace2 f in (object[])copy.GetFaces() ?? new object[0])
                        {
                            var t = f.GetTessTriangles(true) as float[];
                            var n = f.GetTessNorms() as float[];
                            faces++; tris += (t?.Length ?? 0) / 9;
                            if (firstCopy == null && t != null && t.Length > 0) firstCopy = t;
                        }
                    }
                double copies = w.Elapsed.TotalSeconds;
                Console.WriteLine($"Kopyalar: {faces} yüz, {tris} üçgen, {copies:F1} sn (kopyalama {copyOnly:F1} sn)");
                if (firstOrig != null && firstCopy != null)
                    Console.WriteLine($"İlk üçgen: asıl {firstOrig[0] * 1000:F1};{firstOrig[1] * 1000:F1};{firstOrig[2] * 1000:F1}, kopya {firstCopy[0] * 1000:F1};{firstCopy[1] * 1000:F1};{firstCopy[2] * 1000:F1} mm, " +
                                      $"bileşen öteleme {((double[])((IMathTransform)comps[0].Transform2).ArrayData)[9] * 1000:F1} mm");
                return 0;
            }
            int lowAt = Array.IndexOf(args, "--lowest");
            if (lowAt >= 0 && lowAt + 1 < args.Length)
            {
                // --lowest <open assembly path>: the leaf components lying lowest (Y up, then Z up), with their size
                // and surface count, to find feet and mounts.
                var lowAsm = (IAssemblyDoc)swApp.GetOpenDocumentByName(args[lowAt + 1]);
                var rows = new System.Collections.Generic.List<(string name, string file, double[] box, int bodies)>();
                foreach (IComponent2 comp in (object[])lowAsm.GetComponents(false) ?? new object[0])
                {
                    if (comp.GetSuppression2() == 0 || ((object[])comp.GetChildren())?.Length > 0) continue;
                    var box = (double[])comp.GetBox(false, false);
                    if (box == null) continue;
                    int n = (((object[])comp.GetBodies3(1, out _))?.Length ?? 0) + (((object[])comp.GetBodies3(0, out _))?.Length ?? 0);
                    rows.Add((comp.Name2, System.IO.Path.GetFileName(comp.GetPathName()), box, n));
                }
                double minY = rows.Min(r => r.box[1]), maxY = rows.Max(r => r.box[4]);
                double minZ = rows.Min(r => r.box[2]), maxZ = rows.Max(r => r.box[5]);
                Console.WriteLine($"Model Y {minY * 1000:F0}..{maxY * 1000:F0} mm, Z {minZ * 1000:F0}..{maxZ * 1000:F0} mm, {rows.Count} parça");
                foreach (var axis in new[] { 1, 2 })
                {
                    Console.WriteLine(axis == 1 ? "--- en alttakiler (Y)" : "--- en alttakiler (Z)");
                    foreach (var r in rows.OrderBy(r => r.box[axis]).Take(25))
                        Console.WriteLine($"  {r.name} ({r.file}): {Box(r.box)}, boy {(r.box[3] - r.box[0]) * 1000:F0}x{(r.box[4] - r.box[1]) * 1000:F0}x{(r.box[5] - r.box[2]) * 1000:F0} mm, {r.bodies} gövde");
                }
                return 0;
            }
            int addAt = Array.IndexOf(args, "--add-sheets");
            if (addAt >= 0 && addAt + 1 < args.Length)
            {
                // --add-sheets <part>: unites the part's surface bodies one by one with a boolean add, to see whether
                // separate surfaces can live in one body; prints how many bodies come out.
                int oe = 0, ow = 0;
                var src = (IPartDoc)swApp.OpenDoc6(args[addAt + 1], 1, 1, "", ref oe, ref ow);
                var sheets = ((object[])src.GetBodies2(1, true) ?? new object[0]).Cast<IBody2>().Select(b => (IBody2)b.Copy()).ToList();
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var acc = sheets[0];
                int failed = 0, pieces = 1;
                for (int i = 1; i < sheets.Count; i++)
                {
                    var res = (object[])acc.Operations2(15, sheets[i], out int err);
                    if (res == null || res.Length != 1) { failed++; if (failed <= 3) Console.WriteLine($"  {i}: sonuç {res?.Length ?? -1} gövde, hata {err}"); continue; }
                    acc = (IBody2)res[0];
                    pieces++;
                }
                Console.WriteLine($"{sheets.Count} yüzey → 1 gövdede {pieces} yüzey, {failed} eklenemedi; gövde {acc.GetFaceCount()} yüz, {watch.Elapsed.TotalSeconds:F1} sn");
                return 0;
            }
            int knitAt = Array.IndexOf(args, "--knit");
            if (knitAt >= 0 && knitAt + 1 < args.Length)
            {
                // --knit <part>: knits the part's surface bodies with CreateBodiesFromSheets2 for some options and
                // tolerances and prints what comes out (kind, faces, open edges) and whether it thickens.
                int oe = 0, ow = 0;
                var pd = (IPartDoc)swApp.OpenDoc6(args[knitAt + 1], 1, 1, "", ref oe, ref ow);
                var sheets = ((object[])pd.GetBodies2(1, true) ?? new object[0]).Cast<IBody2>().ToList();
                var modeler = (IModeler)swApp.GetModeler();
                Console.WriteLine($"{sheets.Count} yüzey gövdesi, {sheets.Sum(b => ((object[])b.GetFaces()).Length)} yüz");
                if (Array.IndexOf(args, "--from-faces") >= 0)
                {
                    // Fresh surfaces made from the faces (IFace2.CreateSheetBody) instead of the imported bodies.
                    sheets = sheets.SelectMany(b => ((object[])b.GetFaces()).Cast<IFace2>()).Select(f => f.CreateSheetBody() as IBody2).Where(b => b != null).ToList();
                    Console.WriteLine($"  yüzlerden yeni yüzey: {sheets.Count}");
                }
                {
                    // Each sheet alone: open edges, and whether it becomes a solid by itself.
                    int closedSheets = 0, alone = 0, aloneSolid = 0;
                    foreach (var s in sheets)
                    {
                        int openCount = ((object[])s.GetEdges() ?? new object[0]).Cast<IEdge>().Count(e => ((object[])e.GetTwoAdjacentFaces2()).Count(f => f != null) < 2);
                        if (openCount == 0) closedSheets++;
                        int err = 0;
                        var one = ((object[])modeler.CreateBodiesFromSheets2(new object[] { s.Copy() }, 0, 1e-4, ref err))?.Cast<IBody2>().ToList();
                        if (one != null) { alone++; if (one.Any(b => b.GetType() == 0)) aloneSolid++; }
                    }
                    Console.WriteLine($"  tek tek: {closedSheets} kapalı (açık kenarsız) yüzey, {alone} tanesi tek başına işlendi, {aloneSolid} tanesi katı oldu");
                    var first = sheets[0];
                    Console.WriteLine($"  ilk yüzey: {((object[])first.GetFaces()).Length} yüz, {((object[])first.GetEdges() ?? new object[0]).Length} kenar, kutu {Box((double[])first.GetBodyBox())}");
                }
                foreach (int option in new[] { 0, 1, 2 })
                    foreach (double tol in new[] { 1e-5, 1e-4, 1e-3, 3e-3 })
                    {
                        int error = 0;
                        var watch = System.Diagnostics.Stopwatch.StartNew();
                        var res = ((object[])modeler.CreateBodiesFromSheets2(sheets.Select(s => (object)s.Copy()).ToArray(), option, tol, ref error))?.Cast<IBody2>().ToList();
                        if (res == null) { Console.WriteLine($"  seçenek {option}, tol {tol * 1000} mm: sonuç yok (hata {error})"); continue; }
                        int solids = res.Count(b => b.GetType() == 0);
                        int thick = res.Where(b => b.GetType() != 0).Count(b => modeler.ThickenSheet((Body2)b, 1e-4, 0) != null);
                        int openEdges = res.Sum(b => ((object[])b.GetEdges() ?? new object[0]).Cast<IEdge>().Count(e => ((object[])e.GetTwoAdjacentFaces2()).Count(f => f != null) < 2));
                        Console.WriteLine($"  seçenek {option}, tol {tol * 1000} mm: {res.Count} gövde ({solids} katı), {res.Sum(b => ((object[])b.GetFaces()).Length)} yüz, " +
                                          $"{openEdges} açık kenar, kalınlaşan yüzey {thick}/{res.Count - solids}, hata {error}, {watch.Elapsed.TotalSeconds:F1} sn");
                    }
                return 0;
            }
            if (Array.IndexOf(args, "--sheets-by-part") >= 0)
            {
                // Which parts of the active assembly (or the open one named after the option) hold surface (sheet)
                // bodies: per part file, instances and bodies. Bodies with boxes over 100 m are listed too.
                int sbp = Array.IndexOf(args, "--sheets-by-part");
                var asmDoc = (IAssemblyDoc)(sbp + 1 < args.Length ? swApp.GetOpenDocumentByName(args[sbp + 1]) : swApp.ActiveDoc);
                var rows = new System.Collections.Generic.Dictionary<string, (int instances, int sheets, int solids)>();
                int leaves = 0, solidTotal = 0, sheetTotal = 0, noBodies = 0;
                foreach (IComponent2 comp in (object[])asmDoc.GetComponents(false) ?? new object[0])
                {
                    if (comp.GetSuppression2() == 0 || ((object[])comp.GetChildren())?.Length > 0) continue;
                    leaves++;
                    var shb = (object[])comp.GetBodies3(1, out _);
                    var sob = (object[])comp.GetBodies3(0, out _);
                    int sh = shb?.Length ?? 0;
                    int so = sob?.Length ?? 0;
                    if (sh + so == 0) noBodies++;
                    foreach (IBody2 b in sob ?? new object[0])
                    {
                        var t = (double[])((IMathTransform)comp.Transform2)?.ArrayData;
                        var copy = (IBody2)b.Copy();
                        if (comp.Transform2 != null) copy.ApplyTransform((MathTransform)comp.Transform2);
                        Console.WriteLine($"  KATI: {comp.Name2} kutu {Box((double[])b.GetBodyBox())} → montajda {Box((double[])copy.GetBodyBox())}" +
                                          (t == null ? "" : $", öteleme {t[9] * 1000:F0};{t[10] * 1000:F0};{t[11] * 1000:F0} mm, ölçek {t[12]:G4}"));
                    }
                    foreach (IBody2 b in (shb ?? new object[0]).Concat(sob ?? new object[0]))
                    {
                        var bb = (double[])b.GetBodyBox();
                        if (bb != null && Math.Max(bb[3] - bb[0], Math.Max(bb[4] - bb[1], bb[5] - bb[2])) > 100)
                            Console.WriteLine($"  DEV KUTU: {comp.Name2} {(b.GetType() == 0 ? "katı" : "yüzey")} {Box(bb)}");
                    }
                    solidTotal += so; sheetTotal += sh;
                    if (sh == 0) continue;
                    string key = System.IO.Path.GetFileName(comp.GetPathName());
                    rows.TryGetValue(key, out var r);
                    rows[key] = (r.instances + 1, r.sheets + sh, r.solids + so);
                }
                Console.WriteLine($"Yaprak bileşen {leaves} ({noBodies} gövdesiz), katı gövde {solidTotal}, yüzey gövdesi {sheetTotal}, yüzeyli parça dosyası {rows.Count}");
                foreach (var kv in rows.OrderByDescending(r => r.Value.sheets).Take(20))
                    Console.WriteLine($"  {kv.Key}: {kv.Value.instances} adet, {kv.Value.sheets} yüzey gövdesi, {kv.Value.solids} katı gövde");
                return 0;
            }
            if (Array.IndexOf(args, "--hide") >= 0)
            {
                // Keeps SOLIDWORKS off the screen while tests run in the background.
                swApp.Visible = false;
                return 0;
            }
            if (Array.IndexOf(args, "--close-all") >= 0)
            {
                // Closes every document without saving, keeping SOLIDWORKS open.
                swApp.CloseAllDocuments(true);
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
                // --config Simple chooses "Konfigürasyon oluştur" with that name.
                int cfg = Array.IndexOf(args, "--config");
                var callArgs = new object[] { args[test + 1], Array.IndexOf(args, "--ok") >= 0, ex >= 0 ? args[ex + 1] : "", cfg >= 0 ? args[cfg + 1] : "" };
                Console.WriteLine(addin.GetType().InvokeMember("TestPageOutput", System.Reflection.BindingFlags.InvokeMethod, null, addin, callArgs));
                return 0;
            }
            if (Array.IndexOf(args, "--docs") >= 0)
            {
                // Lists open documents; for parts, every solid body's face count and volume.
                foreach (IModelDoc2 d in (object[])swApp.GetDocuments() ?? new object[0])
                {
                    Console.WriteLine($"{d.GetTitle()} ({d.GetPathName()})");
                    Console.WriteLine($"    konfigürasyonlar: {string.Join(", ", (string[])d.GetConfigurationNames() ?? new string[0])}; etkin: {((IConfiguration)d.GetActiveConfiguration())?.Name}");
                    if (d is IPartDoc pd)
                        foreach (IBody2 b in (object[])pd.GetBodies2(0, true) ?? new object[0])
                            Console.WriteLine($"    gövde: {((object[])b.GetFaces()).Length} yüz, hacim {((double[])b.GetMassProperties(1))[3] * 1e9:F0} mm³, kutu {Box((double[])b.GetBodyBox())}");
                    if (d is IPartDoc sd)
                        Console.WriteLine($"    yüzey gövdesi: {((object[])sd.GetBodies2(1, true))?.Length ?? 0}, özellik: {d.GetFeatureCount()}");
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
                else if (args[make + 1] == "sheets")
                {
                    // --make sheets: six separate square wall surfaces of a 100 mm box, each 2 mm short of the edges.
                    var modeler = (IModeler)swApp.GetModeler();
                    double L = 0.1, h = 0.048; // half size of a wall: 50 mm less the 2 mm gap
                    var walls = new[]
                    {
                        (new[] { L / 2, L / 2, 0.0 }, new[] { 0.0, 0, 1 }, new[] { 1.0, 0, 0 }),
                        (new[] { L / 2, L / 2, L }, new[] { 0.0, 0, 1 }, new[] { 1.0, 0, 0 }),
                        (new[] { L / 2, 0.0, L / 2 }, new[] { 0.0, 1, 0 }, new[] { 1.0, 0, 0 }),
                        (new[] { L / 2, L, L / 2 }, new[] { 0.0, 1, 0 }, new[] { 1.0, 0, 0 }),
                        (new[] { 0.0, L / 2, L / 2 }, new[] { 1.0, 0, 0 }, new[] { 0.0, 1, 0 }),
                        (new[] { L, L / 2, L / 2 }, new[] { 1.0, 0, 0 }, new[] { 0.0, 1, 0 }),
                    };
                    foreach (var (root, normal, refDir) in walls)
                    {
                        var plane = (Surface)modeler.CreatePlanarSurface2(root, normal, refDir);
                        var sheet = (IBody2)modeler.CreateSheetFromSurface(plane, new[] { -h, h, -h, h });
                        Console.WriteLine($"  yüzey: {(sheet == null ? "oluşturulamadı" : "tamam")}");
                        if (sheet != null) parts.Add(sheet);
                    }
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
                    if (part.CreateFeatureFromBody3(body, false, 0) == null)
                        Console.WriteLine("  özellik oluşturulamadı");
                ((IModelDoc2)part).ViewZoomtofit2();
                int saveAt = Array.IndexOf(args, "--save");
                if (saveAt >= 0 && saveAt + 1 < args.Length)
                {
                    int e = 0, w = 0;
                    bool saved = ((IModelDoc2)part).Extension.SaveAs3(args[saveAt + 1], 0, 1, null, null, ref e, ref w);
                    Console.WriteLine($"Kaydedildi: {saved} ({args[saveAt + 1]})");
                }
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
                // --resolved: open an assembly fully resolved, overriding a lightweight default (64 without 128).
                int openOptions = Array.IndexOf(args, "--resolved") >= 0 ? 64 : 0;
                var openWatch = System.Diagnostics.Stopwatch.StartNew();
                var opened = swApp.OpenDoc6(path, type, openOptions, "", ref errors, ref warnings);
                Console.WriteLine($"Açılma süresi: {openWatch.Elapsed.TotalSeconds:F2} sn");
                if (opened is IAssemblyDoc openedAsm)
                {
                    var comps = ((object[])openedAsm.GetComponents(false) ?? new object[0]).Cast<IComponent2>().ToList();
                    Console.WriteLine($"Bileşen {comps.Count}, hafif (lightweight) {comps.Count(c => c.GetSuppression2() == 1)}, tam çözülmüş {comps.Count(c => c.GetSuppression2() == 2)}");
                }
                int activateErrors = 0;
                swApp.ActivateDoc3(System.IO.Path.GetFileName(path), false, 0, ref activateErrors);
                Console.WriteLine($"Açıldı: {path} (hata {errors}, uyarı {warnings})");
                return 0;
            }
            int namedView = Array.IndexOf(args, "--named-view");
            if (namedView >= 0 && namedView + 1 < args.Length)
            {
                // --named-view 1|3|7: front, right, isometric (swStandardViews_e), zoomed to fit.
                var md = (IModelDoc2)swApp.ActiveDoc;
                md.ShowNamedView2("", int.Parse(args[namedView + 1]));
                md.ViewZoomtofit2();
                md.GraphicsRedraw2();
                return 0;
            }
            if (Array.IndexOf(args, "--bodies") >= 0)
            {
                // The active part's bodies (or those of the part file given after --bodies): kind, faces, volume, box.
                int at = Array.IndexOf(args, "--bodies");
                IPartDoc pd;
                if (at + 1 < args.Length && args[at + 1].EndsWith(".SLDPRT", StringComparison.OrdinalIgnoreCase))
                {
                    int oe = 0, ow = 0;
                    pd = (IPartDoc)swApp.OpenDoc6(args[at + 1], 1, 1, "", ref oe, ref ow);
                }
                else
                    pd = (IPartDoc)swApp.ActiveDoc;
                Console.WriteLine(((IModelDoc2)pd).GetTitle());
                foreach (int kind in new[] { 0, 1 })
                {
                    var all = ((object[])pd.GetBodies2(kind, true) ?? new object[0]).Cast<IBody2>().ToList();
                    if (all.Count == 0) continue;
                    double volume = all.Sum(b => ((double[])b.GetMassProperties(1))?[3] ?? 0) * 1e9;
                    Console.WriteLine($"  TOPLAM {(kind == 0 ? "katı" : "yüzey")}: {all.Count} gövde, {all.Sum(b => ((object[])b.GetFaces()).Length)} yüz, hacim {volume:F0} mm³");
                }
                foreach (int kind in new[] { 0, 1 })
                    foreach (IBody2 b in (object[])pd.GetBodies2(kind, true) ?? new object[0])
                        Console.WriteLine($"  {(kind == 0 ? "katı" : "yüzey")}: {((object[])b.GetFaces()).Length} yüz, hacim {((double[])b.GetMassProperties(1))?[3] * 1e9:F0} mm³, kutu {Box((double[])b.GetBodyBox())}");
                return 0;
            }
            if (Array.IndexOf(args, "--threads") >= 0)
            {
                // Lists the leaf components of the active assembly with their faces by surface type, to find modeled
                // threads (helical faces are neither planes, cylinders, cones, spheres nor tori).
                var asmDoc = (IAssemblyDoc)swApp.ActiveDoc;
                foreach (IComponent2 comp in (object[])asmDoc.GetComponents(false))
                {
                    var bodies = (object[])comp.GetBodies3(0, out _);
                    if (bodies == null) continue;
                    foreach (IBody2 b in bodies)
                    {
                        var kinds = ((object[])b.GetFaces()).Cast<IFace2>().GroupBy(f => ((ISurface)f.GetSurface()).Identity()).OrderBy(g => g.Key)
                            .Select(g => $"{g.Key}:{g.Count()}");
                        var bb = (double[])b.GetBodyBox();
                        Console.WriteLine($"{comp.Name2}: {((object[])b.GetFaces()).Length} yüz [{string.Join(" ", kinds)}] boy {Math.Max(bb[3] - bb[0], Math.Max(bb[4] - bb[1], bb[5] - bb[2])) * 1000:F0} mm");
                    }
                }
                return 0;
            }
            int blends = Array.IndexOf(args, "--blends");
            if (blends >= 0 && blends + 1 < args.Length)
            {
                // --blends <mm>: deletes and patches cylinder, cone and torus faces of the active part's first solid
                // body whose radius is below the size (fillets, small holes, round bosses); prints the face counts.
                double r = double.Parse(args[blends + 1], CultureInfo.InvariantCulture) / 1000;
                var md = (IModelDoc2)swApp.ActiveDoc;
                var pd = (IPartDoc)md;
                var body = (IBody2)((object[])pd.GetBodies2(0, true))[0];
                var faces = ((object[])body.GetFaces()).Cast<IFace2>().ToList();
                var small = new System.Collections.Generic.List<IFace2>();
                foreach (var f in faces)
                {
                    var s = (ISurface)f.GetSurface();
                    double radius = double.PositiveInfinity;
                    if (s.IsCylinder()) radius = ((double[])s.CylinderParams)[6];
                    else if (s.IsTorus()) radius = ((double[])s.TorusParams)[7];
                    else if (s.IsCone()) radius = ((double[])s.ConeParams)[6];
                    if (radius < r) small.Add(f);
                }
                Console.WriteLine($"Yüz {faces.Count}, {r * 1000} mm altı yuvarlak yüz {small.Count}");
                if (Array.IndexOf(args, "--temp") >= 0)
                {
                    // Body level, on a copy: each small face deleted and healed on its own, every heal action.
                    foreach (int heal in new[] { 0, 1 })
                    {
                        var work = (IBody2)body.Copy();
                        var tw = System.Diagnostics.Stopwatch.StartNew();
                        int done = 0, failedOps = 0;
                        for (int k = 0; k < 40; k++)
                        {
                            var wf = ((object[])work.GetFaces()).Cast<IFace2>().Where(f =>
                            {
                                var s = (ISurface)f.GetSurface();
                                double rr = s.IsCylinder() ? ((double[])s.CylinderParams)[6] : s.IsTorus() ? ((double[])s.TorusParams)[7] : s.IsCone() ? ((double[])s.ConeParams)[6] : double.PositiveInfinity;
                                return rr < r;
                            }).Skip(failedOps).FirstOrDefault();
                            if (wf == null) break;
                            object bodyList; bool check;
                            bool okDel = work.DeleteFaces5(new object[] { wf }, heal, 2, false, out bodyList, out check);
                            if (okDel) done++; else failedOps++;
                        }
                        Console.WriteLine($"DeleteFaces5 iyileştirme {heal}: 40 denemede {done} başarılı, {failedOps} başarısız, {tw.Elapsed.TotalSeconds:F1} sn, kalan yüz {((object[])work.GetFaces()).Length}");
                    }
                    return 0;
                }
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var sel = (SelectData)((ISelectionMgr)md.SelectionManager).CreateSelectData();
                int selected = 0, okCount = 0;
                foreach (int opt in new[] { 1, 3 })
                    for (int k = 0; k < 15; k++)
                    {
                        // Faces are looked up again: a delete feature rebuilds the body.
                        var cur = ((object[])((IBody2)((object[])pd.GetBodies2(0, true))[0]).GetFaces()).Cast<IFace2>().Where(f =>
                        {
                            var s = (ISurface)f.GetSurface();
                            double rr = s.IsCylinder() ? ((double[])s.CylinderParams)[6] : s.IsTorus() ? ((double[])s.TorusParams)[7] : s.IsCone() ? ((double[])s.ConeParams)[6] : double.PositiveInfinity;
                            return rr < r;
                        }).Skip(k + 15 * (opt == 3 ? 1 : 0) - okCount).FirstOrDefault();
                        if (cur == null) break;
                        md.ClearSelection2(true);
                        if (((IEntity)cur).Select4(false, sel)) selected++;
                        bool one = opt == 1 ? md.Extension.InsertDeleteFace(1) : md.InsertDeleteFace2(1);
                        if (one) okCount++;
                    }
                bool ok = okCount > 0;
                Console.WriteLine($"tek tek: {okCount}/{selected} başarılı");
                var after = (object[])pd.GetBodies2(0, true);
                Console.WriteLine($"Seçilen {selected}, sil+yama {ok}, {watch.Elapsed.TotalSeconds:F1} sn; sonra {after?.Length} gövde, " +
                                  $"{after?.Cast<IBody2>().Sum(b => ((object[])b.GetFaces()).Length)} yüz");
                return 0;
            }
            if (Array.IndexOf(args, "--hidden") >= 0)
            {
                // Counts faces of the active part's first solid body from which no ray escapes: rays leave a few
                // points of each face (tessellation triangle centres) in directions around the face normal.
                var pd = (IPartDoc)swApp.ActiveDoc;
                var ext = ((IModelDoc2)pd).Extension;
                var body = (IBody2)((object[])pd.GetBodies2(0, true))[0];
                bool temp = Array.IndexOf(args, "--temp") >= 0;
                var target = temp ? (IBody2)body.Copy() : body;
                var faces = ((object[])target.GetFaces()).Cast<IFace2>().ToList();
                var pts = new System.Collections.Generic.List<double>();
                var dirs = new System.Collections.Generic.List<double>();
                var owner = new System.Collections.Generic.List<int>();
                var watch = System.Diagnostics.Stopwatch.StartNew();
                for (int fi = 0; fi < faces.Count; fi++)
                {
                    var tri = (float[])faces[fi].GetTessTriangles(true);
                    var nrm = (float[])faces[fi].GetTessNorms();
                    if (tri == null || nrm == null) continue;
                    int count = tri.Length / 9;
                    for (int k = 0; k < Math.Min(count, 3); k++)
                    {
                        int t = k * Math.Max(1, count / 3);
                        double cx = (tri[9 * t] + tri[9 * t + 3] + tri[9 * t + 6]) / 3, cy = (tri[9 * t + 1] + tri[9 * t + 4] + tri[9 * t + 7]) / 3, cz = (tri[9 * t + 2] + tri[9 * t + 5] + tri[9 * t + 8]) / 3;
                        double nx = nrm[9 * t], ny = nrm[9 * t + 1], nz = nrm[9 * t + 2];
                        // the normal and 8 directions 60 degrees off it
                        var (ux, uy, uz) = Math.Abs(nx) < 0.9 ? (0.0, -nz, ny) : (nz, 0.0, -nx);
                        double ul = Math.Sqrt(ux * ux + uy * uy + uz * uz); ux /= ul; uy /= ul; uz /= ul;
                        double vx = ny * uz - nz * uy, vy = nz * ux - nx * uz, vz = nx * uy - ny * ux;
                        for (int d = -1; d < 8; d++)
                        {
                            double a = d * Math.PI / 4, s = d < 0 ? 0 : Math.Sin(Math.PI / 3), c = d < 0 ? 1 : Math.Cos(Math.PI / 3);
                            double dx = c * nx + s * (Math.Cos(a) * ux + Math.Sin(a) * vx), dy = c * ny + s * (Math.Cos(a) * uy + Math.Sin(a) * vy), dz = c * nz + s * (Math.Cos(a) * uz + Math.Sin(a) * vz);
                            pts.AddRange(new[] { cx + nx * 2e-5, cy + ny * 2e-5, cz + nz * 2e-5 });
                            dirs.AddRange(new[] { dx, dy, dz });
                            owner.Add(fi);
                        }
                    }
                }
                Console.WriteLine($"Işın: {owner.Count}, hazırlık {watch.Elapsed.TotalSeconds:F1} sn");
                watch.Restart();
                {
                    var bb = (double[])target.GetBodyBox();
                    double cx = (bb[0] + bb[3]) / 2, cy = (bb[1] + bb[4]) / 2;
                    foreach (int opt in new[] { 1, 7, 0 })
                    {
                        try { int h = ext.RayIntersections(new object[] { new DispatchWrapper(target) }, new double[] { cx, cy, bb[5] + 0.1 }, new double[] { 0, 0, -1 }, opt, 1e-6, 0, false); Console.WriteLine($"tek ışın seçenek {opt}: {h} kesişim, {((double[])((IModelDoc2)pd).GetRayIntersectionsPoints())?.Length} veri"); }
                        catch (Exception ex) { Console.WriteLine($"tek ışın seçenek {opt}: {ex.Message}"); }
                    }
                }
                var blocked = new bool[owner.Count];
                int batch = 2000, hits = 0;
                for (int first = 0; first < owner.Count; first += batch)
                {
                    int n = Math.Min(batch, owner.Count - first);
                    hits += ext.RayIntersections(new object[] { new DispatchWrapper(target) }, pts.Skip(3 * first).Take(3 * n).ToArray(), dirs.Skip(3 * first).Take(3 * n).ToArray(), 1, 1e-7, 0, false);
                    var data = (double[])((IModelDoc2)pd).GetRayIntersectionsPoints();
                    if (data != null)
                        for (int i = 0; i + 8 < data.Length; i += 9)
                            blocked[first + (int)data[i + 1]] = true;
                    if (first == 0) Console.WriteLine($"ilk parti: {watch.Elapsed.TotalSeconds:F1} sn, veri {data?.Length}");
                }
                Console.WriteLine($"Kesişim: {hits}, {watch.Elapsed.TotalSeconds:F1} sn");
                var visible = new bool[faces.Count];
                for (int r = 0; r < owner.Count; r++)
                    if (!blocked[r]) visible[owner[r]] = true;
                int seen = visible.Count(v => v);
                Console.WriteLine($"Yüz {faces.Count}: görünen {seen}, gizli {faces.Count - seen}");
                return 0;
            }
            if (Array.IndexOf(args, "--sheettest") >= 0)
            {
                // Tries ways of turning the active part's first solid body into one surface body.
                var pd = (IPartDoc)swApp.ActiveDoc;
                var solid = (IBody2)((object[])pd.GetBodies2(0, true))[0];
                var faces = (object[])solid.GetFaces();
                var modeler = (IModeler)swApp.GetModeler();
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var a = modeler.CreateSheetFromFaces(faces) as IBody2;
                Console.WriteLine($"CreateSheetFromFaces (belgedeki gövde): {(a == null ? "null" : $"tür {a.GetType()}, {((object[])a.GetFaces()).Length} yüz")} {watch.Elapsed.TotalSeconds:F1} sn");
                var copy = (IBody2)solid.Copy();
                var b = modeler.CreateSheetFromFaces((object[])copy.GetFaces()) as IBody2;
                Console.WriteLine($"CreateSheetFromFaces (kopya): {(b == null ? "null" : $"tür {b.GetType()}, {((object[])b.GetFaces()).Length} yüz")}");
                watch.Restart();
                var sheets = faces.Cast<IFace2>().Select(f => f.CreateSheetBody() as IBody2).Where(s => s != null).ToArray();
                Console.WriteLine($"Yüz başına yüzey: {sheets.Length}/{faces.Length} {watch.Elapsed.TotalSeconds:F1} sn");
                foreach (int opt in new[] { 0, 1, 2 })
                {
                    watch.Restart();
                    int err = 0;
                    var knit = (object[])modeler.CreateBodiesFromSheets2(sheets, opt, 1e-5, ref err);
                    Console.WriteLine($"CreateBodiesFromSheets2 seçenek {opt}: hata {err}, {(knit == null ? "null" : string.Join(", ", knit.Cast<IBody2>().Select(k => $"tür {k.GetType()} {((object[])k.GetFaces()).Length} yüz")))} {watch.Elapsed.TotalSeconds:F1} sn");
                }
                return 0;
            }
            int saveAs = Array.IndexOf(args, "--saveas");
            if (saveAs >= 0 && saveAs + 1 < args.Length)
            {
                // Saves the active document under a new name (never over an existing file): --saveas <path>.
                string target = args[saveAs + 1];
                if (System.IO.File.Exists(target)) { Console.Error.WriteLine("Dosya zaten var: " + target); return 1; }
                // --saveas <path> <title>: activates the open document with that title first.
                if (saveAs + 2 < args.Length && !args[saveAs + 2].StartsWith("--"))
                {
                    int activateErrors = 0;
                    swApp.ActivateDoc3(args[saveAs + 2], false, 0, ref activateErrors);
                }
                var active = (IModelDoc2)swApp.ActiveDoc;
                int e = 0, w = 0;
                bool ok = active.Extension.SaveAs3(target, 0, 1, null, null, ref e, ref w);
                Console.WriteLine($"Kaydedildi: {ok} ({active.GetTitle()} → {target}, hata {e})");
                return ok ? 0 : 1;
            }
            int pattern = Array.IndexOf(args, "--pattern");
            if (pattern >= 0 && pattern + 5 < args.Length)
            {
                // Builds a large test assembly: --pattern <assembly> <count> <columns> <dx;dz mm> <save as>.
                // The assembly is placed count times on a grid in X and Z, so the copies stand a few mm apart.
                string src = args[pattern + 1];
                int count = int.Parse(args[pattern + 2]), columns = int.Parse(args[pattern + 3]);
                var step = args[pattern + 4].Split(';').Select(s => double.Parse(s, CultureInfo.InvariantCulture) / 1000).ToArray();
                int e1 = 0, w1 = 0;
                swApp.OpenDoc6(src, 2, 0, "", ref e1, ref w1);
                string template = swApp.GetUserPreferenceStringValue((int)global::SolidWorks.Interop.swconst.swUserPreferenceStringValue_e.swDefaultTemplateAssembly);
                var grid = (IAssemblyDoc)swApp.NewDocument(template, 0, 0, 0);
                int added = 0;
                for (int i = 0; i < count; i++)
                {
                    var comp = grid.AddComponent5(src, 0, "", false, "", (i % columns) * step[0], 0, (i / columns) * step[1]);
                    if (comp != null) added++;
                }
                int e = 0, w = 0;
                bool saved = ((IModelDoc2)grid).Extension.SaveAs3(args[pattern + 5], 0, 1, null, null, ref e, ref w);
                Console.WriteLine($"Desen: {added}/{count} kopya, kaydedildi {saved} ({args[pattern + 5]})");
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
            if (Array.IndexOf(args, "--insides") >= 0 && doc is IPartDoc insidePart)
            {
                // For the active part: the pieces of its own box minus the part, with the box sides they reach.
                var ops = new GeoSimplifier.SolidWorks.Geometry.BodyOps(swApp);
                foreach (IBody2 body in (object[])insidePart.GetBodies2(0, true) ?? new object[0])
                {
                    var b = (IBody2)body.Copy();
                    var tb = GeoSimplifier.SolidWorks.Geometry.BodyOps.TightBounds(b);
                    Console.WriteLine($"Gövde: kutu {Box(new[] { tb.Min.X, tb.Min.Y, tb.Min.Z, tb.Max.X, tb.Max.Y, tb.Max.Z })}, {((object[])b.GetFaces()).Length} yüz");
                    double eps = tb.LongestSide * 1e-5;
                    var inner = new GeoSimplifier.Core.Geometry.Aabb(tb.Min + new GeoSimplifier.Core.Geometry.Vec3(eps, eps, eps), tb.Max - new GeoSimplifier.Core.Geometry.Vec3(eps, eps, eps));
                    var pieces = ops.Subtract(ops.Box(inner), b);
                    foreach (IBody2 piece in pieces ?? new object[0])
                    {
                        var pb = (double[])piece.GetBodyBox();
                        var sides = new System.Collections.Generic.List<string>();
                        for (int a = 0; a < 3; a++)
                        {
                            if (pb[a] <= inner.Min[a] + eps) sides.Add("-" + "XYZ"[a]);
                            if (pb[a + 3] >= inner.Max[a] - eps) sides.Add("+" + "XYZ"[a]);
                        }
                        Console.WriteLine($"  hava parçası: {((double[])piece.GetMassProperties(1))[3] * 1e9:F0} mm³, kutu {Box(pb)}, kenarlar {string.Join(" ", sides)}");
                    }
                }
                return 0;
            }
            int viewAt = Array.IndexOf(args, "--view");
            if (viewAt >= 0 && viewAt + 1 < args.Length)
            {
                // Shows the active document from a standard view by number (1 front, 2 back, 3 left, 4 right, 5 top,
                // 6 bottom, 7 isometric): view names are translated in a Turkish SOLIDWORKS.
                doc.ShowNamedView2("", int.Parse(args[viewAt + 1]));
                doc.ViewZoomtofit2();
                // The Resources pane covers the right of the window: zoom out so all of the model shows.
                var mv = (IModelView)doc.ActiveView;
                mv.Scale2 *= 0.62;
                mv.GraphicsRedraw(null);
                return 0;
            }
            if (Array.IndexOf(args, "--halfcut") >= 0 && doc is IPartDoc cutPart)
            {
                // A section of the active part's solid bodies: the half beyond the middle (across the second longest
                // side) is cut away and the rest opened in a new part, so hollows inside can be seen.
                var ops = new GeoSimplifier.SolidWorks.Geometry.BodyOps(swApp);
                var halves = new System.Collections.Generic.List<IBody2>();
                foreach (IBody2 body in (object[])cutPart.GetBodies2(0, true) ?? new object[0])
                {
                    var bb = GeoSimplifier.SolidWorks.Geometry.BodyOps.Bounds(body);
                    var size = bb.Size;
                    int[] order = new[] { 0, 1, 2 }.OrderByDescending(i => size[i]).ToArray();
                    int acrossAt = Array.IndexOf(args, "--across");
                    int across = order[acrossAt >= 0 ? int.Parse(args[acrossAt + 1]) : 1];
                    var min = new double[] { bb.Min.X - 1, bb.Min.Y - 1, bb.Min.Z - 1 };
                    var max = new double[] { bb.Max.X + 1, bb.Max.Y + 1, bb.Max.Z + 1 };
                    min[across] = (bb.Min[across] + bb.Max[across]) / 2;
                    var cutter = ops.Box(new GeoSimplifier.Core.Geometry.Aabb(new GeoSimplifier.Core.Geometry.Vec3(min[0], min[1], min[2]),
                                                                            new GeoSimplifier.Core.Geometry.Vec3(max[0], max[1], max[2])));
                    var rest = ops.Subtract((IBody2)body.Copy(), cutter);
                    if (rest != null) halves.AddRange(rest.Cast<IBody2>());
                    Console.WriteLine($"Kesit: eksen {"XYZ"[across]} ortadan, {rest?.Length ?? 0} parça");
                }
                string tpl = swApp.GetUserPreferenceStringValue((int)global::SolidWorks.Interop.swconst.swUserPreferenceStringValue_e.swDefaultTemplatePart);
                var view = (IPartDoc)swApp.NewDocument(tpl, 0, 0, 0);
                foreach (var h in halves)
                    view.CreateFeatureFromBody3(h, false, 0);
                ((IModelDoc2)view).ShowNamedView2("*Isometric", 7);
                ((IModelDoc2)view).ViewZoomtofit2();
                return 0;
            }
            if (Array.IndexOf(args, "--configs") >= 0 && doc is IPartDoc)
            {
                // For each configuration of a part: how many features are suppressed, and the last feature's state.
                string active = ((IConfiguration)doc.GetActiveConfiguration()).Name;
                foreach (string name in (string[])doc.GetConfigurationNames())
                {
                    doc.ShowConfiguration2(name);
                    int suppressed = 0;
                    IFeature lastFeature = null;
                    for (var f = (IFeature)doc.FirstFeature(); f != null; f = (IFeature)f.GetNextFeature())
                    {
                        if (f.IsSuppressed()) suppressed++;
                        lastFeature = f;
                    }
                    var solid = (object[])((IPartDoc)doc).GetBodies2(0, true);
                    Console.WriteLine($"{name}: {suppressed} bastırılmış özellik, son özellik {lastFeature?.Name}{(lastFeature?.IsSuppressed() == true ? " (bastırılmış)" : "")}, " +
                                      $"görünen gövde {solid?.Length ?? 0}" + (solid != null && solid.Length > 0 ? $" ({((object[])((IBody2)solid[0]).GetFaces()).Length} yüz)" : ""));
                }
                doc.ShowConfiguration2(active);
                return 0;
            }
            if (Array.IndexOf(args, "--configs") >= 0 && doc is IAssemblyDoc ca)
            {
                // For each configuration: the top-level components and whether they are suppressed (switches configurations).
                string active = ((IConfiguration)doc.GetActiveConfiguration()).Name;
                foreach (string name in (string[])doc.GetConfigurationNames())
                {
                    doc.ShowConfiguration2(name);
                    var states = ((object[])ca.GetComponents(true) ?? new object[0]).Cast<IComponent2>()
                        .Select(c => $"{c.Name2}{(c.IsSuppressed() ? " (bastırılmış)" : "")}{(c.IsFixed() ? " (sabit)" : "")}");
                    Console.WriteLine($"{name}: {string.Join(", ", states)}");
                }
                doc.ShowConfiguration2(active);
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
            // --small 5 leaves out parts smaller than 5 % of the model's size.
            int smallAt = Array.IndexOf(args, "--small");
            double smallMm = smallAt >= 0 && smallAt + 1 < args.Length ? double.Parse(args[smallAt + 1], CultureInfo.InvariantCulture) : 0;
            var result = service.Compute(doc, new ShrinkWrapOptions { GapWidth = gapMm / 1000, FillCavities = true, MaxEmbossHeight = embossMm / 1000, MinPartPercent = smallMm, ForceSingleBody = Array.IndexOf(args, "--single") >= 0 }, exclusions);
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
            // The test scripts note the SOLIDWORKS they started in my_sw_pid.txt (next to the probe's folder); while
            // that one runs, only it is used, never one Serkan opened himself.
            string mine = null;
            var pidFile = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "my_sw_pid.txt");
            if (System.IO.File.Exists(pidFile) && int.TryParse(System.IO.File.ReadAllText(pidFile).Trim(), out int pid) &&
                System.Diagnostics.Process.GetProcesses().Any(p => p.Id == pid))
                mine = "SolidWorks_PID_" + pid;
            GetRunningObjectTable(0, out var rot);
            CreateBindCtx(0, out var ctx);
            rot.EnumRunning(out var monikers);
            var one = new System.Runtime.InteropServices.ComTypes.IMoniker[1];
            while (monikers.Next(1, one, IntPtr.Zero) == 0)
            {
                one[0].GetDisplayName(ctx, null, out string name);
                if (!name.StartsWith("SolidWorks_PID_", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (mine != null && !name.Equals(mine, StringComparison.OrdinalIgnoreCase))
                    continue;
                rot.GetObject(one[0], out object obj);
                if (obj is ISldWorks app)
                {
                    if (app.ActiveDoc != null)
                        return app;
                    fallback = fallback ?? app;
                }
            }
            if (fallback == null && mine != null)
                throw new InvalidOperationException("Testin başlattığı SOLIDWORKS bulunamadı; başka bir oturuma bağlanılmadı.");
            return fallback ?? (ISldWorks)Marshal.GetActiveObject("SldWorks.Application");
        }
    }
}
