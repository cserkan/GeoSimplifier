using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using GeoSimplifier.Core.Licensing;
using GeoSimplifier.SolidWorks.UI;
using Microsoft.Win32;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace GeoSimplifier.SolidWorks.Licensing
{
    /// <summary>
    /// Lets the 3DWrap command run only with a valid .lic file for this computer's SOLIDWORKS serial number.
    /// Licenses are looked for (any *.lic) next to the add-in DLL, in %ProgramData%\3DWrap and in %AppData%\3DWrap.
    /// </summary>
    internal static class LicenseGate
    {
        private const string PublicKeyResource = "GeoSimplifier.SolidWorks.Licensing.3DWrap-acik-anahtar.xml";
        private const string SerialsKey = @"SOFTWARE\SolidWorks\Licenses\Serial Numbers";

        // A license found valid once is trusted for the rest of the SOLIDWORKS session.
        private static string _validFor;
        private static LicenseFile _valid;

        public static string AddinFolder => Path.GetDirectoryName(typeof(LicenseGate).Assembly.Location);
        public static string ProgramDataFolder => Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.CommonApplicationData), "3DWrap");
        public static string AppDataFolder => Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "3DWrap");

        private static IEnumerable<string> Folders => new[] { AddinFolder, ProgramDataFolder, AppDataFolder };

        /// <summary>
        /// The SOLIDWORKS serial numbers installed on this computer, as SOLIDWORKS' installer records them
        /// (HKLM\SOFTWARE\SolidWorks\Licenses\Serial Numbers; "SolidWorks" first).
        /// </summary>
        public static List<string> MachineSerials()
        {
            var result = new List<string>();
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (var key = hklm.OpenSubKey(SerialsKey))
                    {
                        if (key == null)
                            continue;
                        var names = key.GetValueNames()
                            .OrderBy(n => string.Equals(n, "SolidWorks", StringComparison.OrdinalIgnoreCase) ? 0 : 1);
                        foreach (string name in names)
                        {
                            if (key.GetValue(name) is string s && LicenseFile.NormalizeSerial(s).Length >= 8
                                && !result.Any(r => LicenseFile.NormalizeSerial(r) == LicenseFile.NormalizeSerial(s)))
                                result.Add(s.Trim());
                        }
                    }
                }
                catch (Exception ex)
                {
                    PageLog.Write("Lisans: seri numarası okunamadı: " + ex.Message);
                }
            }
            return result;
        }

        private static RSA PublicKey()
        {
            using (var stream = typeof(LicenseGate).Assembly.GetManifestResourceStream(PublicKeyResource))
            {
                if (stream == null)
                    return null;
                using (var reader = new StreamReader(stream))
                    return LicenseKeys.FromXml(reader.ReadToEnd());
            }
        }

        /// <summary>As <see cref="EnsureLicensed"/> without any message (for the test hooks).</summary>
        public static bool IsLicensedQuiet()
        {
            try
            {
                var serials = MachineSerials();
                var key = PublicKey();
                if (key == null || serials.Count == 0 || FindProblem(key, serials, out var found) != null)
                    return false;
                _valid = found;
                return true;
            }
            catch (Exception ex)
            {
                PageLog.Write("Lisans kontrolü başarısız: " + ex.Message);
                return false;
            }
        }

        /// <summary>One line for the page saying whose license this is and until when; null without a valid license.</summary>
        public static string Summary()
        {
            if (_valid == null && !IsLicensedQuiet())
                return null;
            return $"Lisans: {_valid.Company}, " + (_valid.Expires.HasValue ? $"bitiş {_valid.Expires.Value:dd.MM.yyyy}" : "süresiz");
        }

        /// <summary>True when a valid license is present; otherwise explains why in Turkish and offers to pick a .lic file.</summary>
        public static bool EnsureLicensed(ISldWorks swApp)
        {
            var serials = MachineSerials();
            string serialKey = string.Join(",", serials.Select(LicenseFile.NormalizeSerial));
            if (_validFor != null && _validFor == serialKey)
                return true;

            RSA key;
            try { key = PublicKey(); }
            catch (Exception ex) { key = null; PageLog.Write("Lisans: açık anahtar okunamadı: " + ex.Message); }
            if (key == null)
            {
                Show(swApp, "Bu 3DWrap sürümüne lisans anahtarı eklenmemiş, eklenti çalışamaz.\n\nLütfen 3DWrap sağlayıcınızla görüşün.",
                    swMessageBoxIcon_e.swMbStop, swMessageBoxBtn_e.swMbOk);
                return false;
            }

            string serialText = serials.Count == 0 ? "(okunamadı)" : string.Join(", ", serials);
            if (serials.Count == 0)
            {
                Show(swApp, "Bu bilgisayardaki SOLIDWORKS seri numarası okunamadı, bu yüzden 3DWrap lisansı kontrol edilemiyor.\n\n" +
                    "Lütfen 3DWrap sağlayıcınızla görüşün.", swMessageBoxIcon_e.swMbStop, swMessageBoxBtn_e.swMbOk);
                return false;
            }

            string problem = FindProblem(key, serials, out var found);
            if (problem == null)
            {
                _validFor = serialKey;
                _valid = found;
                PageLog.Write($"Lisans geçerli: {found.Company}, bitiş {(found.Expires.HasValue ? found.Expires.Value.ToString("yyyy-MM-dd") : "süresiz")}");
                return true;
            }

            TryCopySerialToClipboard(serialText);
            int answer = Show(swApp,
                problem + "\n\n" +
                "Bu bilgisayarın SOLIDWORKS seri numarası:\n    " + serialText + "\n" +
                "(panoya kopyalandı; lisans almak için bunu 3DWrap sağlayıcınıza gönderin)\n\n" +
                "Elinizde bir 3DWrap lisans dosyası (.lic) var mı? \"Evet\" derseniz dosyayı seçebilirsiniz.",
                swMessageBoxIcon_e.swMbWarning, swMessageBoxBtn_e.swMbYesNo);
            if (answer != (int)swMessageBoxResult_e.swMbHitYes)
                return false;

            return PickAndInstall(swApp, key, serials, serialKey);
        }

        /// <summary>Null when one of the license files found is valid; otherwise the most useful reason in Turkish.</summary>
        private static string FindProblem(RSA key, List<string> serials, out LicenseFile valid)
        {
            valid = null;
            var reasons = new List<(LicenseCheck, string)>();
            foreach (string folder in Folders.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string[] files;
                try { files = Directory.Exists(folder) ? Directory.GetFiles(folder, "*" + LicenseFile.Extension) : new string[0]; }
                catch { continue; }
                foreach (string file in files)
                {
                    var (check, lic, error) = CheckFile(key, serials, file);
                    PageLog.Write($"Lisans dosyası {file}: {(error ?? check.ToString())}");
                    if (error == null && check == LicenseCheck.Valid)
                    {
                        valid = lic;
                        return null;
                    }
                    reasons.Add((check, error ?? Explain(check, lic, file)));
                }
            }
            if (reasons.Count == 0)
                return "3DWrap lisans dosyası (.lic) bulunamadı. 3DWrap ancak geçerli bir lisans dosyasıyla çalışır.";
            // An expired license or one for another serial says more than a damaged file.
            var best = reasons.OrderBy(r => r.Item1 == LicenseCheck.Expired ? 0 : r.Item1 == LicenseCheck.WrongSerial ? 1 : 2).First();
            return best.Item2;
        }

        private static (LicenseCheck, LicenseFile, string) CheckFile(RSA key, List<string> serials, string file)
        {
            try
            {
                var lic = LicenseFile.Parse(File.ReadAllText(file));
                return (lic.Check(key, serials, DateTime.Today), lic, null);
            }
            catch (Exception ex)
            {
                return (LicenseCheck.BadSignature, null, $"Lisans dosyası okunamadı ({Path.GetFileName(file)}): {ex.Message}");
            }
        }

        private static string Explain(LicenseCheck check, LicenseFile lic, string file)
        {
            string name = Path.GetFileName(file);
            switch (check)
            {
                case LicenseCheck.Expired:
                    return $"3DWrap lisansınızın süresi {lic.Expires.Value:dd.MM.yyyy} tarihinde doldu ({lic.Company}, {name}).";
                case LicenseCheck.WrongSerial:
                    return $"Lisans dosyası ({name}) başka bir SOLIDWORKS seri numarası için düzenlenmiş: {string.Join(", ", lic.Serials)}.";
                default:
                    return $"Lisans dosyası ({name}) geçersiz: içeriği değiştirilmiş ya da bozulmuş.";
            }
        }

        private static bool PickAndInstall(ISldWorks swApp, RSA key, List<string> serials, string serialKey)
        {
            string picked;
            using (var dialog = new System.Windows.Forms.OpenFileDialog
            {
                Title = "3DWrap lisans dosyasını seçin",
                Filter = "3DWrap lisansı (*.lic)|*.lic",
                CheckFileExists = true,
            })
            {
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                    return false;
                picked = dialog.FileName;
            }

            var (check, lic, error) = CheckFile(key, serials, picked);
            if (error != null || check != LicenseCheck.Valid)
            {
                Show(swApp, (error ?? Explain(check, lic, picked)) + "\n\nBu bilgisayarın SOLIDWORKS seri numarası:\n    " + string.Join(", ", serials),
                    swMessageBoxIcon_e.swMbStop, swMessageBoxBtn_e.swMbOk);
                return false;
            }

            foreach (string folder in new[] { ProgramDataFolder, AppDataFolder })
            {
                try
                {
                    Directory.CreateDirectory(folder);
                    string target = Path.Combine(folder, Path.GetFileName(picked));
                    if (!string.Equals(Path.GetFullPath(target), Path.GetFullPath(picked), StringComparison.OrdinalIgnoreCase))
                        File.Copy(picked, target, true);
                    PageLog.Write("Lisans yüklendi: " + target);
                    _validFor = serialKey;
                    _valid = lic;
                    Show(swApp, $"3DWrap lisansı yüklendi.\n\nFirma: {lic.Company}\nBitiş: {(lic.Expires.HasValue ? lic.Expires.Value.ToString("dd.MM.yyyy") : "süresiz")}",
                        swMessageBoxIcon_e.swMbInformation, swMessageBoxBtn_e.swMbOk);
                    return true;
                }
                catch (Exception ex)
                {
                    PageLog.Write($"Lisans {folder} klasörüne kopyalanamadı: {ex.Message}");
                }
            }
            Show(swApp, "Lisans dosyası geçerli ama bilgisayara kopyalanamadı. Dosyayı elle şu klasöre koyun:\n    " + ProgramDataFolder,
                swMessageBoxIcon_e.swMbWarning, swMessageBoxBtn_e.swMbOk);
            return false;
        }

        private static void TryCopySerialToClipboard(string text)
        {
            try { System.Windows.Forms.Clipboard.SetText(text); }
            catch (Exception ex) { PageLog.Write("Lisans: panoya kopyalanamadı: " + ex.Message); }
        }

        private static int Show(ISldWorks swApp, string text, swMessageBoxIcon_e icon, swMessageBoxBtn_e buttons) =>
            swApp.SendMsgToUser2(text, (int)icon, (int)buttons);
    }
}
