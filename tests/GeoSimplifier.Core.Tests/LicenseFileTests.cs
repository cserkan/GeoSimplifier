using System;
using System.Security.Cryptography;
using GeoSimplifier.Core.Licensing;
using Xunit;

namespace GeoSimplifier.Core.Tests
{
    public class LicenseFileTests
    {
        private static readonly RSA Key = RSA.Create(2048);
        private static readonly DateTime Today = new DateTime(2026, 11, 2);

        private static LicenseFile Signed(DateTime? expires = null, params string[] serials)
        {
            var lic = new LicenseFile
            {
                Company = "ABC Makina A.Ş.",
                Serials = serials.Length > 0 ? serials : new[] { "9000 0000 1111 2222 ABCD EFGH" },
                Expires = expires,
                Issued = Today,
            };
            lic.Sign(Key);
            return lic;
        }

        private static RSA PublicOnly() => LicenseKeys.FromXml(LicenseKeys.ToXml(Key.ExportParameters(false), false));

        [Fact]
        public void RoundTripThroughTextIsValid()
        {
            var lic = LicenseFile.Parse(Signed(new DateTime(2027, 12, 31)).ToText());
            Assert.Equal("ABC Makina A.Ş.", lic.Company);
            Assert.Equal(LicenseCheck.Valid, lic.Check(PublicOnly(), new[] { "90000000-11112222-abcdefgh" }, Today));
        }

        [Fact]
        public void EditedTextBreaksSignature()
        {
            string text = Signed(new DateTime(2027, 12, 31)).ToText().Replace("2027-12-31", "2099-12-31");
            Assert.Equal(LicenseCheck.BadSignature, LicenseFile.Parse(text).Check(PublicOnly(), new[] { "9000000011112222ABCDEFGH" }, Today));
            string other = Signed().ToText().Replace("ABC Makina", "XYZ Makina");
            Assert.Equal(LicenseCheck.BadSignature, LicenseFile.Parse(other).Check(PublicOnly(), new[] { "9000000011112222ABCDEFGH" }, Today));
        }

        [Fact]
        public void OtherSerialIsRefused() =>
            Assert.Equal(LicenseCheck.WrongSerial, Signed().Check(PublicOnly(), new[] { "9000 0000 1111 2222 ABCD EFGX" }, Today));

        [Fact]
        public void ExpiryIsInclusive()
        {
            var lic = Signed(Today);
            Assert.Equal(LicenseCheck.Valid, lic.Check(PublicOnly(), new[] { "9000000011112222ABCDEFGH" }, Today));
            Assert.Equal(LicenseCheck.Expired, lic.Check(PublicOnly(), new[] { "9000000011112222ABCDEFGH" }, Today.AddDays(1)));
        }

        [Fact]
        public void SeveralSerialsAndNoExpiry()
        {
            var lic = LicenseFile.Parse(Signed(null, "1111 2222 3333 4444", "5555 6666 7777 8888").ToText());
            Assert.Null(lic.Expires);
            Assert.Equal(LicenseCheck.Valid, lic.Check(PublicOnly(), new[] { "5555666677778888" }, Today.AddYears(50)));
        }

        [Fact]
        public void OtherKeyIsRefused()
        {
            using (var other = RSA.Create(2048))
                Assert.Equal(LicenseCheck.BadSignature, Signed().Check(other, new[] { "9000000011112222ABCDEFGH" }, Today));
        }

        [Fact]
        public void NotALicense() => Assert.Throws<FormatException>(() => LicenseFile.Parse("merhaba"));
    }
}
