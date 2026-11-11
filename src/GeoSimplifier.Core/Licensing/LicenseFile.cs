using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace GeoSimplifier.Core.Licensing
{
    /// <summary>
    /// A 3DWrap license (.lic): a short text file naming the company, the SOLIDWORKS serial number(s) it is for and an
    /// optional end date, signed with the vendor's RSA private key. The add-in carries only the public key, so it can
    /// check a file but never make one; changing any field breaks the signature.
    /// </summary>
    /// <example>
    /// # 3DWrap lisans dosyası. Değiştirmeyin: tek harf değişirse lisans geçersiz olur.
    /// Urun=3DWrap
    /// Firma=ABC Makina A.Ş.
    /// SeriNo=9000 0000 1111 2222 ABCD EFGH
    /// Bitis=2027-12-31
    /// Duzenleme=2026-11-02
    /// Imza=base64...
    /// </example>
    public sealed class LicenseFile
    {
        public const string Product = "3DWrap";
        public const string Extension = ".lic";
        private const string Header = "# 3DWrap lisans dosyası. Değiştirmeyin: tek harf değişirse lisans geçersiz olur.";
        private const string SignedPrefix = "3DWrap-Lisans-v1\n";

        public string Company { get; set; } = "";

        /// <summary>One or more serial numbers, as written (spaces and dashes are ignored when comparing).</summary>
        public IList<string> Serials { get; set; } = new List<string>();

        /// <summary>Last day the license works (inclusive); null = no end.</summary>
        public DateTime? Expires { get; set; }

        public DateTime Issued { get; set; } = DateTime.Today;

        /// <summary>Base64 RSA-SHA256 (PKCS#1 v1.5) signature of <see cref="SignedText"/>.</summary>
        public string Signature { get; set; } = "";

        /// <summary>Upper case letters and digits only, so "9000-0000 abcd" and "90000000ABCD" match.</summary>
        public static string NormalizeSerial(string serial) =>
            new string((serial ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

        private static string Date(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        /// <summary>The exact bytes the signature covers: every field, normalized, in a fixed order.</summary>
        public byte[] SignedText()
        {
            var sb = new StringBuilder(SignedPrefix);
            sb.Append("Urun=").Append(Product).Append('\n');
            sb.Append("Firma=").Append(Company.Trim()).Append('\n');
            sb.Append("SeriNo=").Append(string.Join(",", Serials.Select(NormalizeSerial))).Append('\n');
            sb.Append("Bitis=").Append(Expires.HasValue ? Date(Expires.Value) : "suresiz").Append('\n');
            sb.Append("Duzenleme=").Append(Date(Issued)).Append('\n');
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        public string ToText()
        {
            var sb = new StringBuilder();
            sb.Append(Header).Append("\r\n");
            sb.Append("Urun=").Append(Product).Append("\r\n");
            sb.Append("Firma=").Append(Company.Trim()).Append("\r\n");
            sb.Append("SeriNo=").Append(string.Join(", ", Serials.Select(s => s.Trim()))).Append("\r\n");
            sb.Append("Bitis=").Append(Expires.HasValue ? Date(Expires.Value) : "süresiz").Append("\r\n");
            sb.Append("Duzenleme=").Append(Date(Issued)).Append("\r\n");
            sb.Append("Imza=").Append(Signature).Append("\r\n");
            return sb.ToString();
        }

        /// <exception cref="FormatException">The text is not a license file.</exception>
        public static LicenseFile Parse(string text)
        {
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in (text ?? "").Split('\n'))
            {
                string line = raw.Trim().TrimStart('﻿');
                if (line.Length == 0 || line.StartsWith("#"))
                    continue;
                int eq = line.IndexOf('=');
                if (eq <= 0)
                    continue;
                fields[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }

            string Get(string key) => fields.TryGetValue(key, out var v) ? v : throw new FormatException($"Lisans dosyasında '{key}' satırı yok.");

            if (!string.Equals(Get("Urun"), Product, StringComparison.OrdinalIgnoreCase))
                throw new FormatException("Bu dosya bir 3DWrap lisansı değil.");

            var lic = new LicenseFile
            {
                Company = Get("Firma"),
                Serials = Get("SeriNo").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim()).Where(s => s.Length > 0).ToList(),
                Expires = ParseExpiry(Get("Bitis")),
                Issued = ParseDate(Get("Duzenleme")),
                Signature = Get("Imza"),
            };
            return lic;
        }

        private static DateTime? ParseExpiry(string s)
        {
            string n = s.Trim().ToLowerInvariant();
            if (n == "" || n == "süresiz" || n == "suresiz")
                return null;
            return ParseDate(s);
        }

        private static DateTime ParseDate(string s) =>
            DateTime.TryParseExact(s.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d : throw new FormatException($"Tarih okunamadı: '{s}' (beklenen biçim: 2027-12-31).");

        public void Sign(RSA privateKey)
        {
            if (Serials.Count == 0 || Serials.All(s => NormalizeSerial(s).Length == 0))
                throw new ArgumentException("En az bir seri numarası gerekli.");
            if (Company.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                throw new ArgumentException("Firma adı tek satır olmalı.");
            Signature = Convert.ToBase64String(privateKey.SignData(SignedText(), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        }

        public bool SignatureValid(RSA publicKey)
        {
            byte[] sig;
            try { sig = Convert.FromBase64String(Signature); }
            catch (FormatException) { return false; }
            return publicKey.VerifyData(SignedText(), sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }

        public bool CoversSerial(string serial)
        {
            string n = NormalizeSerial(serial);
            return n.Length > 0 && Serials.Any(s => NormalizeSerial(s) == n);
        }

        /// <summary>Checks this license for one of the machine's serial numbers on <paramref name="today"/>.</summary>
        public LicenseCheck Check(RSA publicKey, IEnumerable<string> machineSerials, DateTime today)
        {
            if (!SignatureValid(publicKey))
                return LicenseCheck.BadSignature;
            if (!machineSerials.Any(CoversSerial))
                return LicenseCheck.WrongSerial;
            if (Expires.HasValue && today.Date > Expires.Value.Date)
                return LicenseCheck.Expired;
            return LicenseCheck.Valid;
        }
    }

    public enum LicenseCheck
    {
        Valid,
        BadSignature,
        WrongSerial,
        Expired,
    }

    /// <summary>RSA keys as small XML files (the .NET "RSAKeyValue" layout), read without RSA.FromXmlString so it works everywhere.</summary>
    public static class LicenseKeys
    {
        public static string ToXml(RSAParameters p, bool includePrivate)
        {
            var root = new XElement("RSAKeyValue",
                new XElement("Modulus", Convert.ToBase64String(p.Modulus)),
                new XElement("Exponent", Convert.ToBase64String(p.Exponent)));
            if (includePrivate)
            {
                root.Add(new XElement("P", Convert.ToBase64String(p.P)),
                    new XElement("Q", Convert.ToBase64String(p.Q)),
                    new XElement("DP", Convert.ToBase64String(p.DP)),
                    new XElement("DQ", Convert.ToBase64String(p.DQ)),
                    new XElement("InverseQ", Convert.ToBase64String(p.InverseQ)),
                    new XElement("D", Convert.ToBase64String(p.D)));
            }
            return root.ToString();
        }

        public static RSA FromXml(string xml)
        {
            var root = XElement.Parse(xml);
            byte[] Part(string name) => root.Element(name) is XElement e ? Convert.FromBase64String(e.Value.Trim()) : null;
            var p = new RSAParameters
            {
                Modulus = Part("Modulus"),
                Exponent = Part("Exponent"),
                P = Part("P"),
                Q = Part("Q"),
                DP = Part("DP"),
                DQ = Part("DQ"),
                InverseQ = Part("InverseQ"),
                D = Part("D"),
            };
            if (p.Modulus == null || p.Exponent == null)
                throw new FormatException("Anahtar dosyası okunamadı.");
            var rsa = RSA.Create();
            rsa.ImportParameters(p);
            return rsa;
        }

        public static bool HasPrivatePart(string xml) => XElement.Parse(xml).Element("D") != null;
    }
}
