using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using SolidWorks.Interop.sldworks;

namespace GeoSimplifier.GapFill
{
    /// <summary>
    /// Açık yüzey doldurma denemesi. Bir yüzey modelinin (ör. 3DWrap "Yüzey kabuk" sonucu) serbest kenar
    /// döngülerini bulur, her döngü için "kapatılmalı mı, nasıl?" tahmini yapar ve kapatılacakları
    /// SOLIDWORKS'ün kendi Düzlemsel yüzey / Dolgu yüzey unsurlarıyla kapatır. Sonuç gerçek B-rep kalır;
    /// display üçgenleri yalnızca sınıflandırmada (komşu gövde ve çakışma testi) kullanılır.
    /// Girdi dosyasına dokunmaz: önce çıktı klasörüne kopyalar, kopyayı açar ve kaydeder.
    /// </summary>
    internal static class Program
    {
        private static StreamWriter _log;
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // Ayarlar (mm cinsinden girilir, içeride metre)
        private static double _maxSize = 0.150;     // bundan büyük açıklıklar doldurulmaz (döngü kutusu köşegeni)
        private static double _neighbourTol = 0.002; // döngü noktası başka gövdeye bu kadar yakınsa "komşu kapatıyor"
        private static double _coincideTol = 0.0005; // kapak noktası mevcut yüzeye bu kadar yakınsa "çakışık"
        private static int _max = 600;
        private static bool _analyseOnly;
        private static string _exe = @"C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS (4)\SLDWORKS.exe";
        private static string _tag = "deneme";

        private static int Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Kullanım: GeoSimplifier.GapFill <girdi.SLDPRT> <çıktı klasörü> [--analiz] [--max N] [--sinir-mm 150] [--komsu-mm 2] [--etiket ad] [--exe yol]");
                return 2;
            }
            string input = args[0], outDir = args[1];
            for (int i = 2; i < args.Length; i++)
            {
                string a = args[i];
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException(a + " değer bekliyor");
                if (a == "--analiz") _analyseOnly = true;
                else if (a == "--max") _max = int.Parse(Next(), Inv);
                else if (a == "--sinir-mm") _maxSize = double.Parse(Next(), Inv) / 1000;
                else if (a == "--komsu-mm") _neighbourTol = double.Parse(Next(), Inv) / 1000;
                else if (a == "--etiket") _tag = Next();
                else if (a == "--exe") _exe = Next();
            }
            Directory.CreateDirectory(outDir);
            string baseName = Path.GetFileNameWithoutExtension(input) + "_BoslukDoldurma_" + _tag;
            _log = new StreamWriter(Path.Combine(outDir, baseName + ".log"), false, new UTF8Encoding(false)) { AutoFlush = true };
            Log($"girdi={input} çıktı={outDir} sınır={_maxSize * 1000:F0}mm komşu={_neighbourTol * 1000:F1}mm max={_max} analiz={_analyseOnly}");

            string work = Path.Combine(outDir, baseName + ".SLDPRT");
            if (Path.GetFullPath(work).Equals(Path.GetFullPath(input), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Çıktı girdinin üzerine yazılamaz.");
            bool assembly = input.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase);
            if (!assembly)
            {
                File.Copy(input, work, true);
                Log($"kopya: {work}");
            }

            _work = work;
            _trace = Path.Combine(outDir, baseName + "_iz.txt");
            try
            {
                (_sw, _proc) = StartSolidWorks();
                if (assembly && !AssemblyToPart(_sw, input, work)) return 1;
                var md = OpenWork();
                if (md == null) return 1;
                Run(md, outDir, baseName);
                return 0;
            }
            catch (Exception ex)
            {
                Log("HATA: " + ex);
                return 1;
            }
            finally
            {
                StopSolidWorks();
                Log("bitti");
            }
        }

        /// <summary>
        /// Ana montaj girdisi: montaj açılır (eksik referans uyarısı sessizce geçilir), "Parça olarak kaydet"
        /// ile TÜM bileşenlerin geometrisi (dış yüzler değil) yeni bir parçaya kopyalanır ve montaj kaydedilmeden
        /// kapatılır. Serkan'ın dosyalarına yazılmaz.
        /// </summary>
        private static ISldWorks _sw;
        private static Process _proc;
        private static string _work, _trace;
        private static int _restarts;

        private static IModelDoc2 OpenWork()
        {
            int err = 0, warn = 0;
            var md = (IModelDoc2)_sw.OpenDoc6(_work, 1, 1, "", ref err, ref warn);
            Log(md == null ? $"HATA: açılamadı err={err} warn={warn}" : $"açıldı err={err} warn={warn}");
            return md;
        }

        private static void StopSolidWorks()
        {
            try { _sw?.CloseAllDocuments(true); _sw?.ExitApp(); } catch { }
            if (_proc != null && !_proc.WaitForExit(30000)) { try { _proc.Kill(); Log("SW kapatılamadı, sonlandırıldı"); } catch { } }
            _sw = null; _proc = null;
        }

        /// <summary>SOLIDWORKS process gone (it crashed on some geometry): start a new one and reopen the work file.</summary>
        private static bool Disconnected(Exception ex)
        {
            if (!(ex is COMException c)) return ex is InvalidComObjectException;
            uint h = (uint)c.HResult;
            return h == 0x80010108 || h == 0x800706BA || h == 0x800706BE || h == 0x80010007;
        }

        private static IModelDoc2 Restart(string why)
        {
            if (++_restarts > 8) throw new InvalidOperationException("SOLIDWORKS çok kez çöktü");
            Log($"SW bağlantısı koptu ({why}); yeniden başlatılıyor ({_restarts}. kez)");
            try { if (_proc != null && !_proc.HasExited) _proc.Kill(); } catch { }
            _sw = null; _proc = null;
            (_sw, _proc) = StartSolidWorks();
            var md = OpenWork();
            if (md == null) throw new InvalidOperationException("çalışma dosyası yeniden açılamadı");
            return md;
        }

        private static bool AssemblyToPart(ISldWorks sw, string asm, string part)
        {
            int err = 0, warn = 0;
            var w = Stopwatch.StartNew();
            var md = (IModelDoc2)sw.OpenDoc6(asm, 2, 1, "", ref err, ref warn);
            if (md == null) { Log($"HATA: montaj açılamadı err={err} warn={warn}"); return false; }
            Log($"montaj açıldı {w.Elapsed.TotalSeconds:F1} sn err={err} warn={warn}, bileşen {((IAssemblyDoc)md).GetComponentCount(false)}");
            md.ClearSelection2(true);
            var adv = md.Extension.GetAdvancedSaveAsOptions(0) as IAdvancedSaveAsOptions;
            if (adv == null) { Log("HATA: kayıt seçenekleri alınamadı"); return false; }
            adv.GeometryToSave = 0; // swGeometryToSave_AllComponents
            int e2 = 0, w2 = 0;
            bool ok = md.Extension.SaveAs3(part, 0, 2 | 1, null, adv, ref e2, ref w2);
            Log($"montaj parça olarak kaydedildi={ok} {w.Elapsed.TotalSeconds:F1} sn err={e2} warn={w2} " +
                (File.Exists(part) ? $"{new FileInfo(part).Length / 1048576.0:F1} MB" : "dosya yok"));
            sw.CloseDoc(md.GetTitle());
            return ok && File.Exists(part);
        }

        private sealed class FreeEdge
        {
            public List<V> Pts;          // start -> end
            public double Length;
            public bool Closed;
            public string FaceKey;
            public string EndKey;
            public int Index;            // position in the body's GetEdges(), to find the edge again after a restart
        }

        private sealed class Loop
        {
            public int Id, Body;
            public List<FreeEdge> Edges = new List<FreeEdge>();
            public List<V> Pts = new List<V>();
            public bool Open;            // chain did not close
            public double Length, Size, BodySize, PlanarDev, Area, Compact;
            public V Centre, Normal;
            public bool Inner;           // an inner loop of one face (a hole in the face)
            public int Faces;
            public double NeighbourFrac = -1, CoincideFrac = -1;
            public string Class = "", Action = "", Result = "";
            public double Ms;
            public List<byte[]> Refs;
            public bool Saved;
        }

        private static void Run(IModelDoc2 md, string outDir, string baseName)
        {
            var part = (IPartDoc)md;
            var view = md.ActiveView as IModelView;
            Shot(md, outDir, baseName + "_00_once_izometrik", 7);

            var bodies = ((object[])part.GetBodies2(1, true) ?? new object[0]).Cast<IBody2>().ToList();
            var solids = ((object[])part.GetBodies2(0, true) ?? new object[0]).Length;
            Log($"gövde: {bodies.Count} yüzey + {solids} katı");

            // 1) Display triangles of every body, for the neighbour / coincidence tests.
            var grid = new TriGrid(0.02);
            for (int b = 0; b < bodies.Count; b++)
            {
                var tris = new List<float>();
                foreach (IFace2 f in (object[])bodies[b].GetFaces() ?? new object[0])
                    if (f.GetTessTriangles(true) is float[] t) tris.AddRange(t);
                grid.AddBody(b, tris.ToArray());
            }
            Log($"üçgen ağı: {grid.Triangles} üçgen");

            // 2) Free edges of each body, chained into loops. The tool runs out of process, so every COM call
            //    costs; per free edge only GetCurve + GetCurveParams2 + GetTessPts, and loops of a face are read
            //    only for faces that have free edges (faces told apart by their box).
            var loops = new List<Loop>();
            int freeEdges = 0, allEdges = 0;
            var stepWatch = Stopwatch.StartNew();
            var crashed = new List<int>();
            for (int b = 0; b < bodies.Count; b++)
            {
                // SOLIDWORKS keeps every object it handed out alive until the client releases it; ~100k edges
                // pile up and SW died after ~15 min (t2, t3). Release them regularly.
                if (b % 25 == 0) { GC.Collect(); GC.WaitForPendingFinalizers(); }
                if (b % 100 == 0) Log($"  gövde {b}/{bodies.Count}, serbest kenar {freeEdges}, döngü {loops.Count}, {stepWatch.Elapsed.TotalSeconds:F0} sn");
                try { File.WriteAllText(_trace, $"gövde {b}"); } catch { }
                try { BodyLoops(b, bodies[b], loops, ref freeEdges, ref allEdges); }
                catch (Exception ex) when (Disconnected(ex))
                {
                    crashed.Add(b);
                    Log($"  SW gövde {b} üzerinde çöktü ({ex.GetType().Name}); bu gövde atlanıyor");
                    loops.RemoveAll(l => l.Body == b);
                    md = Restart("gövde " + b);
                    part = (IPartDoc)md;
                    view = md.ActiveView as IModelView;
                    bodies = ((object[])part.GetBodies2(1, true) ?? new object[0]).Cast<IBody2>().ToList();
                }
            }
            Log($"kenar: {allEdges}, serbest: {freeEdges}, döngü: {loops.Count}, SW çöktüğü için atlanan gövde: {crashed.Count} [{string.Join(",", crashed)}]");

            // 3) Guess: which loops to close and how.
            foreach (var lp in loops) Classify(lp, grid);
            foreach (var g in loops.GroupBy(l => l.Class).OrderByDescending(g => g.Count()))
                Log($"  sınıf {g.Key}: {g.Count()}");

            var chosen = loops.Where(l => l.Action != "")
                .OrderBy(l => l.Action == "duz" ? 0 : 1).ThenBy(l => l.Size)
                .Take(_max).ToList();
            Log($"doldurulacak: {chosen.Count} (düz {chosen.Count(l => l.Action == "duz")}, dolgu {chosen.Count(l => l.Action == "dolgu")})");

            if (!_analyseOnly && chosen.Count > 0)
            {
                // persistent references first, from the edges as they are now (the session may have been
                // restarted since the loops were found): features added later may invalidate edge pointers.
                foreach (var grp in chosen.GroupBy(l => l.Body))
                {
                    var be = (object[])bodies[grp.Key].GetEdges() ?? new object[0];
                    foreach (var lp in grp)
                        lp.Refs = lp.Edges.Select(e => e.Index < be.Length ? (byte[])md.Extension.GetPersistReference3(be[e.Index]) : null).ToList();
                }
                {
                    int ez = 0, wz = 0;
                    md.Save3(1, ref ez, ref wz);   // so a restart during filling reopens a file whose references resolve
                }

                md.FeatureManager.EnableFeatureTree = false;
                if (view != null) view.EnableGraphicsUpdate = false;
                var fillWatch = Stopwatch.StartNew();
                int done = 0;
                foreach (var lp in chosen)
                {
                    var w = Stopwatch.StartNew();
                    try { File.WriteAllText(_trace, $"doldurma döngü {lp.Id}"); } catch { }
                    try { lp.Result = Fill(md, lp); }
                    catch (Exception ex) when (Disconnected(ex))
                    {
                        lp.Result = "sw-coktu";
                        Log($"  SW döngü {lp.Id} doldurulurken çöktü; son kayıttan devam");
                        md = Restart("döngü " + lp.Id);
                        view = md.ActiveView as IModelView;
                        md.FeatureManager.EnableFeatureTree = false;
                        if (view != null) view.EnableGraphicsUpdate = false;
                        // fills since the last save are lost
                        foreach (var x in chosen.Take(done).Where(x => x.Result.StartsWith("tamam") && !x.Saved)) x.Result = "kayboldu";
                    }
                    catch (Exception ex) { lp.Result = "hata:" + ex.GetType().Name; }
                    if (done % 100 == 99)
                    {
                        int ez = 0, wz = 0;
                        md.Save3(1, ref ez, ref wz);
                        foreach (var x in chosen.Take(done + 1)) x.Saved = true;
                    }
                    lp.Ms = w.Elapsed.TotalMilliseconds;
                    if (++done % 25 == 0)
                        Log($"  {done}/{chosen.Count} doldurma, {fillWatch.Elapsed.TotalSeconds:F0} sn, başarılı {chosen.Take(done).Count(l => l.Result.StartsWith("tamam"))}");
                }
                if (view != null) view.EnableGraphicsUpdate = true;
                md.FeatureManager.EnableFeatureTree = true;
                Log($"doldurma bitti {fillWatch.Elapsed.TotalSeconds:F1} sn: " +
                    string.Join(", ", chosen.GroupBy(l => l.Action + "/" + l.Result.Split(':')[0]).Select(g => $"{g.Key}={g.Count()}")));
                md.GraphicsRedraw2();

                int e2 = 0, w2 = 0;
                bool saved = md.Save3(1, ref e2, ref w2);
                Log($"kaydedildi={saved} err={e2} warn={w2} boyut={new FileInfo(md.GetPathName()).Length / 1048576.0:F1} MB");
            }

            WriteCsv(Path.Combine(outDir, baseName + "_donguler.csv"), loops, bodies);

            foreach (var (name, id) in new[] { ("izometrik", 7), ("on", 1), ("arka", 2), ("sol", 3), ("sag", 4), ("ust", 5) })
                Shot(md, outDir, baseName + "_10_sonra_" + name, id);
            // close-ups of the largest fills of each kind
            int n = 0;
            foreach (var lp in chosen.Where(l => l.Result.StartsWith("tamam")).GroupBy(l => l.Action)
                         .SelectMany(g => g.OrderByDescending(l => l.Size).Take(4)))
            {
                ZoomTo(md, lp);
                Shot(md, outDir, $"{baseName}_20_yakin_{++n:D2}_{lp.Action}_{lp.Id}", -1);
            }
        }

        // ---------------------------------------------------------------- free edges and loops

        private static void BodyLoops(int b, IBody2 body, List<Loop> loops, ref int freeEdges, ref int allEdges)
        {
                var bb = (double[])body.GetBodyBox();
                double bodySize = new V(bb[3] - bb[0], bb[4] - bb[1], bb[5] - bb[2]).Len;

                var free = new List<FreeEdge>();
                var freeFaces = new Dictionary<string, IFace2>();
                var bodyEdges = (object[])body.GetEdges() ?? new object[0];
                for (int ei = 0; ei < bodyEdges.Length; ei++)
                {
                    var e = (IEdge)bodyEdges[ei];
                    allEdges++;
                    var adj = (object[])e.GetTwoAdjacentFaces2() ?? new object[0];
                    IFace2 only = null; int nf = 0;
                    foreach (var o in adj) if (o != null) { nf++; only = (IFace2)o; }
                    if (nf != 1) continue;
                    var fe = Sample(e);
                    if (fe == null) continue;
                    string fk = FaceKey(only);
                    fe.FaceKey = fk;
                    fe.Index = ei;
                    freeFaces[fk] = only;
                    free.Add(fe);
                }
                if (free.Count == 0) return;
                freeEdges += free.Count;

                // edge keys of the inner loops of the faces that have free edges
                var innerKeys = new Dictionary<string, int>();
                int innerSerial = 0;
                foreach (var face in freeFaces.Values)
                    foreach (ILoop2 lp in (object[])face.GetLoops() ?? new object[0])
                    {
                        if (lp.IsOuter()) continue;
                        innerSerial++;
                        foreach (IEdge e in (object[])lp.GetEdges() ?? new object[0])
                            if (e.GetCurveParams2() is double[] cp && cp.Length >= 6)
                                innerKeys[EndKey(cp)] = innerSerial;
                    }

                foreach (var lp in Chain(free))
                {
                    lp.Body = b;
                    lp.BodySize = bodySize;
                    lp.Id = loops.Count + 1;
                    var serials = lp.Edges.Select(x => innerKeys.TryGetValue(x.EndKey, out int s) ? s : -1).Distinct().ToList();
                    lp.Inner = serials.Count == 1 && serials[0] > 0;
                    lp.Faces = lp.Edges.Select(x => x.FaceKey).Distinct().Count();
                    Measure(lp);
                    loops.Add(lp);
                }
        }


        private static string Key(V p, double q) =>
            $"{Math.Round(p.X / q)}|{Math.Round(p.Y / q)}|{Math.Round(p.Z / q)}";

        private static string FaceKey(IFace2 f)
        {
            var b = (double[])f.GetBox();
            return b == null ? "?" : string.Join("|", b.Select(x => Math.Round(x * 1e5).ToString(Inv)));
        }

        /// <summary>Key of an edge from its start and end points (GetCurveParams2: start xyz, end xyz, ...).</summary>
        private static string EndKey(double[] cp)
        {
            // order-independent
            string a = Key(new V(cp[0], cp[1], cp[2]), 1e-5), b = Key(new V(cp[3], cp[4], cp[5]), 1e-5);
            return string.CompareOrdinal(a, b) < 0 ? a + "/" + b : b + "/" + a;
        }

        private static FreeEdge Sample(IEdge e)
        {
            var curve = (ICurve)e.GetCurve();
            var cp = e.GetCurveParams2() as double[];
            if (curve == null || cp == null || cp.Length < 8) return null;
            var start = new V(cp[0], cp[1], cp[2]);
            var end = new V(cp[3], cp[4], cp[5]);
            var pts = new List<V>();
            if (curve.GetTessPts(0.0005, 0.004, new[] { cp[0], cp[1], cp[2] }, new[] { cp[3], cp[4], cp[5] }) is double[] t)
                for (int i = 0; i + 2 < t.Length; i += 3) pts.Add(new V(t[i], t[i + 1], t[i + 2]));
            if (pts.Count == 0 || (pts[0] - start).Len > 1e-7) pts.Insert(0, start);
            if ((pts[pts.Count - 1] - end).Len > 1e-7 || pts.Count == 1) pts.Add(end);
            double len = 0;
            for (int i = 1; i < pts.Count; i++) len += (pts[i] - pts[i - 1]).Len;
            bool closed = (start - end).Len < 1e-6;
            if (closed && pts.Count < 4) return null; // closed edge but no tessellation: cannot measure
            return new FreeEdge { Pts = pts, Length = len, Closed = closed, EndKey = EndKey(cp) };
        }

        private static IEnumerable<Loop> Chain(List<FreeEdge> edges)
        {
            const double tol = 2e-5;
            var used = new bool[edges.Count];
            // endpoint index by coarse key
            var ends = new Dictionary<string, List<int>>();
            void AddEnd(V p, int i)
            {
                var k = Key(p, 1e-4);
                if (!ends.TryGetValue(k, out var l)) ends[k] = l = new List<int>();
                l.Add(i);
            }
            for (int i = 0; i < edges.Count; i++)
                if (!edges[i].Closed) { AddEnd(edges[i].Pts[0], i); AddEnd(edges[i].Pts[edges[i].Pts.Count - 1], i); }

            IEnumerable<int> Near(V p)
            {
                double q = 1e-4;
                long x = (long)Math.Round(p.X / q), y = (long)Math.Round(p.Y / q), z = (long)Math.Round(p.Z / q);
                for (long dx = -1; dx <= 1; dx++)
                    for (long dy = -1; dy <= 1; dy++)
                        for (long dz = -1; dz <= 1; dz++)
                            if (ends.TryGetValue($"{x + dx}|{y + dy}|{z + dz}", out var l))
                                foreach (int i in l) yield return i;
            }

            for (int i = 0; i < edges.Count; i++)
            {
                if (used[i]) continue;
                used[i] = true;
                var lp = new Loop();
                lp.Edges.Add(edges[i]);
                lp.Pts.AddRange(edges[i].Pts);
                if (edges[i].Closed) { lp.Pts.RemoveAt(lp.Pts.Count - 1); yield return lp; continue; }
                V start = edges[i].Pts[0], cur = edges[i].Pts[edges[i].Pts.Count - 1];
                bool closed = false;
                for (int guard = 0; guard < 100000; guard++)
                {
                    if ((cur - start).Len < tol) { closed = true; break; }
                    int next = -1; bool reverse = false;
                    foreach (int j in Near(cur))
                    {
                        if (used[j]) continue;
                        var e = edges[j];
                        if ((e.Pts[0] - cur).Len < tol) { next = j; reverse = false; break; }
                        if ((e.Pts[e.Pts.Count - 1] - cur).Len < tol) { next = j; reverse = true; break; }
                    }
                    if (next < 0) break;
                    used[next] = true;
                    var pts = new List<V>(edges[next].Pts);
                    if (reverse) pts.Reverse();
                    lp.Edges.Add(edges[next]);
                    lp.Pts.AddRange(pts.Skip(1));
                    cur = pts[pts.Count - 1];
                }
                lp.Open = !closed;
                if (closed && lp.Pts.Count > 1) lp.Pts.RemoveAt(lp.Pts.Count - 1);
                yield return lp;
            }
        }

        private static void Measure(Loop lp)
        {
            lp.Length = lp.Edges.Sum(e => e.Length);
            double x0 = lp.Pts.Min(p => p.X), x1 = lp.Pts.Max(p => p.X), y0 = lp.Pts.Min(p => p.Y), y1 = lp.Pts.Max(p => p.Y), z0 = lp.Pts.Min(p => p.Z), z1 = lp.Pts.Max(p => p.Z);
            lp.Size = new V(x1 - x0, y1 - y0, z1 - z0).Len;
            var (c, n) = Shape.FitPlane(lp.Pts);
            lp.Centre = c; lp.Normal = n;
            lp.PlanarDev = lp.Pts.Max(p => Math.Abs((p - c).Dot(n)));
            var (u, v) = Shape.Basis(n);
            var poly = Shape.Project(lp.Pts, c, u, v);
            lp.Area = Shape.Area(poly);
            lp.Compact = lp.Length > 0 ? 4 * Math.PI * lp.Area / (lp.Length * lp.Length) : 0;
        }

        // ---------------------------------------------------------------- the guess

        /// <summary>
        /// Kural tabanlı tahmin (ilk sürüm). Sınıflar:
        ///   acik-zincir   kapanmayan kenar zinciri, dokunulmaz
        ///   komsu-kapali  döngü başka bir gövdeye yaslanıyor (dikişsiz komşu yüz ya da dayanan parça): açıklık değil
        ///   cakisik       düz kapak mevcut bir yüzeyin üstüne gelir (ör. sacın dış çevresi): gerek yok
        ///   cok-buyuk     sınırdan büyük açıklık: bilerek açık bırakılmış olabilir
        ///   yarik         çok ince uzun açıklık (dikiş/yarık): kapak anlamsız
        ///   govde-siniri  düzlemsel olmayan ve gövdenin neredeyse tamamını saran sınır
        ///   delik-duz     yüz içindeki düzlemsel delik → Düzlemsel yüzey
        ///   agiz-duz      gövdenin düzlemsel açık ağzı (boru ucu, kutu ağzı) → Düzlemsel yüzey
        ///   dolgu         düzlemsel olmayan, toplu açıklık → Dolgu yüzey
        /// </summary>
        private static void Classify(Loop lp, TriGrid grid)
        {
            if (lp.Open || lp.Pts.Count < 3) { lp.Class = "acik-zincir"; return; }
            if (lp.Size > _maxSize) { lp.Class = "cok-buyuk"; return; }

            // how much of the rim rests on another body
            int near = 0, step = Math.Max(1, lp.Pts.Count / 48), count = 0;
            for (int i = 0; i < lp.Pts.Count; i += step)
            {
                count++;
                if (grid.Nearest(lp.Pts[i], _neighbourTol, lp.Body, out _) <= _neighbourTol) near++;
            }
            lp.NeighbourFrac = count > 0 ? (double)near / count : 0;
            if (lp.NeighbourFrac >= 0.7) { lp.Class = "komsu-kapali"; return; }
            if (lp.Compact < 0.06 && !lp.Inner) { lp.Class = "yarik"; return; }

            bool planar = lp.PlanarDev <= Math.Max(0.0003, 0.02 * lp.Size);
            if (planar)
            {
                lp.CoincideFrac = Coincide(lp, grid);
                if (lp.CoincideFrac >= 0.5) { lp.Class = "cakisik"; return; }
                lp.Class = lp.Inner ? "delik-duz" : "agiz-duz";
                lp.Action = "duz";
                return;
            }
            if (!lp.Inner && lp.Size > 0.8 * lp.BodySize) { lp.Class = "govde-siniri"; return; }
            if (lp.Compact < 0.15) { lp.Class = "yarik"; return; }
            lp.Class = "dolgu";
            lp.Action = "dolgu";
        }

        /// <summary>Share of points inside the planar cap that already lie on some surface (any body).</summary>
        private static double Coincide(Loop lp, TriGrid grid)
        {
            var (u, v) = Shape.Basis(lp.Normal);
            var poly = Shape.Project(lp.Pts, lp.Centre, u, v);
            double minX = poly.Min(p => p.x), maxX = poly.Max(p => p.x), minY = poly.Min(p => p.y), maxY = poly.Max(p => p.y);
            double margin = Math.Min(0.003, 0.1 * lp.Size);
            int inside = 0, on = 0;
            for (int i = 1; i < 12; i++)
                for (int j = 1; j < 12; j++)
                {
                    double x = minX + (maxX - minX) * i / 12, y = minY + (maxY - minY) * j / 12;
                    if (!Shape.Inside(poly, x, y) || Shape.DistToPolyline(poly, x, y) < margin) continue;
                    inside++;
                    var p = lp.Centre + u * x + v * y;
                    if (grid.Nearest(p, _coincideTol, -1, out _) <= _coincideTol) on++;
                }
            return inside > 0 ? (double)on / inside : 0;
        }

        // ---------------------------------------------------------------- filling with SOLIDWORKS features

        private static readonly double[] Orange = { 1.0, 0.45, 0.0, 1, 1, 0.3, 0.3, 0, 0 };
        private static readonly double[] Magenta = { 0.85, 0.0, 0.85, 1, 1, 0.3, 0.3, 0, 0 };

        private static string Fill(IModelDoc2 md, Loop lp)
        {
            var edges = new List<IEdge>();
            foreach (var r in lp.Refs)
            {
                if (r == null) return "kenar-yok";
                int err;
                var o = md.Extension.GetObjectByPersistReference3(r, out err) as IEdge;
                if (o == null) return "kenar-yok";
                edges.Add(o);
            }
            var before = md.FeatureByPositionReverse(0) as IFeature;
            string how = lp.Action;
            bool ok = false;
            if (lp.Action == "duz")
            {
                md.ClearSelection2(true);
                bool all = edges.All(e => ((IEntity)e).Select4(true, null));
                ok = all && md.InsertPlanarRefSurface();
                if (!ok) how = "dolgu"; // fall back to a fill surface
            }
            if (!ok)
            {
                md.ClearSelection2(true);
                var wrapped = edges.Select(e => (object)new DispatchWrapper(e)).ToArray();
                var contact = new int[edges.Count]; // swContact
                object feat = md.FeatureManager.InsertFillSurface2(1, 1, wrapped, contact, null, null);
                ok = feat != null;
            }
            md.ClearSelection2(true);
            var after = md.FeatureByPositionReverse(0) as IFeature;
            if (!ok || after == null || (before != null && after.GetID() == before.GetID()))
                return "olmadi";
            var colour = how == "duz" ? Orange : Magenta;
            // SetMaterialPropertyValues2 returns void in the SW 2026 interop: fall back to face colours on exception
            try { after.SetMaterialPropertyValues2(colour, 2, null); }
            catch
            {
                foreach (IFace2 f in (object[])after.GetFaces() ?? new object[0]) f.MaterialPropertyValues = colour;
            }
            return "tamam:" + how;
        }

        // ---------------------------------------------------------------- pictures, csv, log

        private static void Shot(IModelDoc2 md, string dir, string name, int view)
        {
            try
            {
                if (view > 0) { md.ShowNamedView2("", view); md.ViewZoomtofit2(); }
                md.GraphicsRedraw2();
                string bmp = Path.Combine(dir, name + ".bmp");
                bool ok = md.SaveBMP(bmp, 1600, 1000);
                if (ok && File.Exists(bmp))
                {
                    using (var img = System.Drawing.Image.FromFile(bmp))
                        img.Save(Path.Combine(dir, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                    File.Delete(bmp);
                }
                Log($"görüntü {name}: {ok}");
            }
            catch (Exception ex) { Log($"görüntü {name}: {ex.Message}"); }
        }

        private static void ZoomTo(IModelDoc2 md, Loop lp)
        {
            md.ShowNamedView2("", 7);
            double r = Math.Max(0.08, lp.Size * 2.5);
            var c = lp.Centre;
            md.ViewZoomTo2(c.X - r, c.Y - r, c.Z - r, c.X + r, c.Y + r, c.Z + r);
        }

        private static void WriteCsv(string path, List<Loop> loops, List<IBody2> bodies)
        {
            var sb = new StringBuilder();
            sb.AppendLine("id;govde;govde_adi;kenar;yuz;cevre_mm;boyut_mm;govde_boyut_mm;duzlemsizlik_mm;alan_mm2;tokluk;ic_dongu;komsu_oran;cakisma_oran;sinif;islem;sonuc;ms;merkez_mm");
            foreach (var l in loops)
            {
                string name = "";
                try { name = bodies[l.Body].Name; } catch { }
                sb.AppendLine(string.Join(";", new object[]
                {
                    l.Id, l.Body, name, l.Edges.Count, l.Faces, F(l.Length * 1000), F(l.Size * 1000), F(l.BodySize * 1000),
                    F(l.PlanarDev * 1000), F(l.Area * 1e6), F(l.Compact), l.Inner ? 1 : 0, F(l.NeighbourFrac), F(l.CoincideFrac),
                    l.Class, l.Action, l.Result, F(l.Ms), l.Centre.ToString()
                }));
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            Log($"csv: {path}");
        }

        private static string F(double v) => v.ToString("0.###", Inv);

        private static void Log(string s)
        {
            string line = $"{DateTime.Now:HH:mm:ss} [{Clock.Elapsed.TotalSeconds,7:F1}] {s}";
            Console.WriteLine(line);
            _log?.WriteLine(line);
        }

        // ---------------------------------------------------------------- starting our own SOLIDWORKS

        [DllImport("ole32.dll")] private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable rot);
        [DllImport("ole32.dll")] private static extern int CreateBindCtx(int reserved, out IBindCtx ctx);

        /// <summary>Starts a new SOLIDWORKS 2026 process (never attaches to one Serkan opened) and hides it.</summary>
        private static (ISldWorks, Process) StartSolidWorks()
        {
            if (!File.Exists(_exe)) throw new FileNotFoundException("SOLIDWORKS bulunamadı", _exe);
            var proc = Process.Start(new ProcessStartInfo(_exe) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Minimized });
            Log($"SW başlatıldı pid {proc.Id}");
            string moniker = "SolidWorks_PID_" + proc.Id;
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed.TotalSeconds < 180)
            {
                System.Threading.Thread.Sleep(1000);
                GetRunningObjectTable(0, out var rot);
                CreateBindCtx(0, out var ctx);
                rot.EnumRunning(out var monikers);
                var one = new IMoniker[1];
                while (monikers.Next(1, one, IntPtr.Zero) == 0)
                {
                    one[0].GetDisplayName(ctx, null, out string name);
                    if (!string.Equals(name, moniker, StringComparison.OrdinalIgnoreCase)) continue;
                    rot.GetObject(one[0], out object o);
                    var sw = (ISldWorks)o;
                    while (!sw.StartupProcessCompleted && watch.Elapsed.TotalSeconds < 180) System.Threading.Thread.Sleep(500);
                    sw.Visible = false;
                    sw.UserControl = false;
                    Log($"SW hazır {watch.Elapsed.TotalSeconds:F1} sn, sürüm {sw.RevisionNumber()}");
                    return (sw, proc);
                }
            }
            try { proc.Kill(); } catch { }
            throw new TimeoutException("SOLIDWORKS açılmadı");
        }
    }
}
