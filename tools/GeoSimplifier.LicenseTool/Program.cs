using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using GeoSimplifier.Core.Licensing;

namespace GeoSimplifier.LicenseTool
{
    /// <summary>
    /// 3DWrap license generator. Commands (Turkish, as used by the vendor):
    ///   anahtar-olustur [--acik dosya]           makes the key pair once (private key stays in %USERPROFILE%\3DWrap-Lisans)
    ///   uret --firma "ABC" --seri "9000 ..." [--seri ...] [--bitis 2027-12-31] [--cikti ABC.lic] [--anahtar ozel.xml]
    ///   kontrol dosya.lic [--seri "9000 ..."] [--acik acik.xml]
    ///   seri                                     this computer's SOLIDWORKS serial numbers
    /// </summary>
    internal static class Program
    {
        private static string Home => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "3DWrap-Lisans");
        private static string DefaultPrivateKey => Path.Combine(Home, "3DWrap-ozel-anahtar.xml");
        private static string DefaultPublicKey => Path.Combine(Home, "3DWrap-acik-anahtar.xml");

        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            try
            {
                if (args.Length == 0)
                    return Usage();
                var rest = args.Skip(1).ToList();
                switch (args[0].ToLowerInvariant())
                {
                    case "anahtar-olustur": return MakeKeys(rest);
                    case "uret": return Issue(rest);
                    case "kontrol": return Verify(rest);
                    case "seri": return ShowSerials();
                    default: return Usage();
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("HATA: " + ex.Message);
                return 1;
            }
        }

        private static int Usage()
        {
            Console.WriteLine(@"3DWrap lisans aracı

  3DWrap-Lisans anahtar-olustur [--acik <acik-anahtar.xml>]
      Anahtar çiftini BİR KEZ oluşturur. Özel anahtar: " + DefaultPrivateKey + @"
      (yedekleyin, kimseyle paylaşmayın). Açık anahtar eklentiye gömülür.

  3DWrap-Lisans uret --firma ""ABC Makina A.Ş."" --seri ""9000 0000 1111 2222 ABCD EFGH""
                     [--seri <ikinci seri>] [--bitis 2027-12-31] [--cikti ABC.lic] [--anahtar <ozel-anahtar.xml>]
      Lisans dosyası üretir. --bitis verilmezse süresizdir.

  3DWrap-Lisans kontrol <dosya.lic> [--seri <seri no>] [--acik <acik-anahtar.xml>]
      Bir lisans dosyasının imzasını (ve verilirse seri numarasını) kontrol eder.

  3DWrap-Lisans seri
      Bu bilgisayardaki SOLIDWORKS seri numaralarını gösterir.");
            return 2;
        }

        private static Dictionary<string, List<string>> Options(List<string> args, out List<string> positional)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            positional = new List<string>();
            for (int i = 0; i < args.Count; i++)
            {
                if (args[i].StartsWith("--"))
                {
                    if (i + 1 >= args.Count)
                        throw new ArgumentException($"{args[i]} için değer eksik.");
                    string name = args[i].Substring(2);
                    if (!result.TryGetValue(name, out var list))
                        result[name] = list = new List<string>();
                    list.Add(args[++i]);
                }
                else
                {
                    positional.Add(args[i]);
                }
            }
            return result;
        }

        private static string One(Dictionary<string, List<string>> o, string name, string fallback = null) =>
            o.TryGetValue(name, out var v) ? v.Last() : fallback;

        private static int MakeKeys(List<string> args)
        {
            var o = Options(args, out _);
            string publicPath = One(o, "acik", DefaultPublicKey);
            if (File.Exists(DefaultPrivateKey))
                throw new InvalidOperationException($"Özel anahtar zaten var: {DefaultPrivateKey}\n" +
                    "Yeni anahtar, eski anahtarla üretilmiş TÜM lisansları geçersiz kılar. Gerçekten istiyorsanız eski dosyayı önce elle taşıyın.");

            Directory.CreateDirectory(Home);
            using (var rsa = RSA.Create(2048))
            {
                if (rsa.KeySize < 2048)
                    throw new InvalidOperationException("2048 bit anahtar oluşturulamadı.");
                var p = rsa.ExportParameters(true);
                File.WriteAllText(DefaultPrivateKey, LicenseKeys.ToXml(p, true));
                string publicDir = Path.GetDirectoryName(Path.GetFullPath(publicPath));
                Directory.CreateDirectory(publicDir);
                File.WriteAllText(publicPath, LicenseKeys.ToXml(p, false));
            }
            Console.WriteLine("Özel anahtar : " + DefaultPrivateKey);
            Console.WriteLine("  -> Bunu güvenli bir yere YEDEKLEYİN (ör. şifreli USB). Kaybolursa yeni lisans üretilemez;");
            Console.WriteLine("     başkasının eline geçerse herkes lisans üretebilir. Git'e EKLEMEYİN.");
            Console.WriteLine("Açık anahtar : " + Path.GetFullPath(publicPath));
            return 0;
        }

        private static int Issue(List<string> args)
        {
            var o = Options(args, out _);
            string company = One(o, "firma") ?? throw new ArgumentException("--firma gerekli.");
            if (!o.TryGetValue("seri", out var serials))
                throw new ArgumentException("--seri gerekli (SOLIDWORKS seri numarası).");
            DateTime? expires = null;
            if (One(o, "bitis") is string b)
            {
                if (!DateTime.TryParseExact(b, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                    throw new ArgumentException($"Tarih okunamadı: '{b}' (beklenen: 2027-12-31).");
                expires = d;
            }
            string keyPath = One(o, "anahtar", DefaultPrivateKey);
            if (!File.Exists(keyPath))
                throw new FileNotFoundException($"Özel anahtar bulunamadı: {keyPath} (önce 'anahtar-olustur').");
            string xml = File.ReadAllText(keyPath);
            if (!LicenseKeys.HasPrivatePart(xml))
                throw new ArgumentException("Bu dosya açık anahtar; lisans üretmek için özel anahtar gerekir.");

            var lic = new LicenseFile
            {
                Company = company,
                Serials = serials.SelectMany(s => s.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)).Select(s => s.Trim()).ToList(),
                Expires = expires,
                Issued = DateTime.Today,
            };
            using (var rsa = LicenseKeys.FromXml(xml))
            {
                lic.Sign(rsa);
                if (!lic.SignatureValid(rsa))
                    throw new InvalidOperationException("İmza doğrulanamadı.");
            }

            string output = One(o, "cikti") ?? Path.Combine(Home, "Lisanslar", SafeName(company) + LicenseFile.Extension);
            if (!output.EndsWith(LicenseFile.Extension, StringComparison.OrdinalIgnoreCase))
                output += LicenseFile.Extension;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
            File.WriteAllText(output, lic.ToText(), new UTF8Encoding(false));
            Console.WriteLine("Lisans üretildi: " + Path.GetFullPath(output));
            Console.WriteLine($"  Firma : {lic.Company}");
            Console.WriteLine($"  SeriNo: {string.Join(", ", lic.Serials)}");
            Console.WriteLine($"  Bitiş : {(lic.Expires.HasValue ? lic.Expires.Value.ToString("dd.MM.yyyy") : "süresiz")}");
            return 0;
        }

        private static string SafeName(string s)
        {
            var bad = Path.GetInvalidFileNameChars();
            string n = new string(s.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim();
            n = n.TrimEnd('.', ' ');
            return n.Length == 0 ? "lisans" : n;
        }

        private static int Verify(List<string> args)
        {
            var o = Options(args, out var files);
            if (files.Count != 1)
                throw new ArgumentException("Kontrol edilecek tek bir .lic dosyası verin.");
            string keyPath = One(o, "acik") ?? (File.Exists(DefaultPublicKey) ? DefaultPublicKey : DefaultPrivateKey);
            var lic = LicenseFile.Parse(File.ReadAllText(files[0]));
            using (var rsa = LicenseKeys.FromXml(File.ReadAllText(keyPath)))
            {
                Console.WriteLine($"Firma : {lic.Company}");
                Console.WriteLine($"SeriNo: {string.Join(", ", lic.Serials)}");
                Console.WriteLine($"Bitiş : {(lic.Expires.HasValue ? lic.Expires.Value.ToString("dd.MM.yyyy") : "süresiz")}");
                if (o.TryGetValue("seri", out var serials))
                {
                    var check = lic.Check(rsa, serials, DateTime.Today);
                    Console.WriteLine("Sonuç : " + Describe(check));
                    return check == LicenseCheck.Valid ? 0 : 1;
                }
                bool ok = lic.SignatureValid(rsa);
                Console.WriteLine("İmza  : " + (ok ? "geçerli" : "GEÇERSİZ"));
                return ok ? 0 : 1;
            }
        }

        private static string Describe(LicenseCheck c)
        {
            switch (c)
            {
                case LicenseCheck.Valid: return "geçerli";
                case LicenseCheck.Expired: return "SÜRESİ DOLMUŞ";
                case LicenseCheck.WrongSerial: return "BU SERİ NUMARASI İÇİN DEĞİL";
                default: return "İMZA GEÇERSİZ (dosya değiştirilmiş ya da başka anahtarla üretilmiş)";
            }
        }

        private static int ShowSerials()
        {
#if NETFRAMEWORK
            bool any = false;
            foreach (var view in new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 })
            {
                using (var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, view))
                using (var key = hklm.OpenSubKey(@"SOFTWARE\SolidWorks\Licenses\Serial Numbers"))
                {
                    if (key == null)
                        continue;
                    foreach (string name in key.GetValueNames())
                    {
                        Console.WriteLine($"{name}: {key.GetValue(name)}");
                        any = true;
                    }
                }
                if (any)
                    break;
            }
            if (!any)
                Console.WriteLine("Bu bilgisayarda SOLIDWORKS seri numarası bulunamadı.");
            return any ? 0 : 1;
#else
            Console.WriteLine("Bu komut yalnızca Windows'ta (net48 sürümü) çalışır.");
            return 1;
#endif
        }
    }
}
