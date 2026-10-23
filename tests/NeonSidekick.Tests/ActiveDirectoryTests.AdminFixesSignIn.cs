using System.Globalization;
using NeonSidekick.ActiveDirectory;

namespace NeonSidekick.Tests;

/// <summary>Phase 8 of the admin review's fixes (2026-10-23): the directory's sign-in, systems, values and <c>/ad</c>'s line.</summary>
public sealed partial class ActiveDirectoryTests
{
    /// <summary>J1: the name as given only for a host.</summary>
    [WindowsTheory]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    [InlineData("lab.example", "lab.example", false)]
    [InlineData("lab.example.", "lab.example", false)]
    [InlineData("dc1.lab.example", "lab.example", true)]
    [InlineData("localhost", "lab.example", true)]
    public void J1_ArecExclusive_OnlyForAHost(string server, string domain, bool exclusive) =>
        Assert.Equal(exclusive, WindowsDirectorySystem.NamesHost(new DirectoryTarget(server, 389, false, null, domain)));

    /// <summary>DP7 (the branch review, 2026-10-23): a revocation that could not be checked passes; a revoked certificate never does.</summary>
    [Fact]
    public void DP7_AnUncheckableRevocation_Passes_ARevokedOneNever()
    {
        const System.Security.Cryptography.X509Certificates.X509ChainStatusFlags unknown = System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.RevocationStatusUnknown;
        Assert.True(NeonSidekick.ActiveDirectory.Ldap.LdapTls.RevocationUnknownOnly(unknown | System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.OfflineRevocation));
        Assert.False(NeonSidekick.ActiveDirectory.Ldap.LdapTls.RevocationUnknownOnly(unknown | System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.Revoked));
        Assert.False(NeonSidekick.ActiveDirectory.Ldap.LdapTls.RevocationUnknownOnly(unknown | System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.UntrustedRoot));
        Assert.False(NeonSidekick.ActiveDirectory.Ldap.LdapTls.RevocationUnknownOnly(System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.NoError));
    }

    /// <summary>J3, J4, V11, D3: the new refusals' sentences.</summary>
    [Fact]
    public void J3_J4_V11_D3_TheSentences()
    {
        Assert.Contains("not signed and sealed", DirectoryErrors.Sentence(NeonSidekick.ActiveDirectory.Ldap.LdapResultCodes.NotSealed, "lab", "dc1", 389, "me", ""));
        Assert.Contains("or answered with a certificate", DirectoryErrors.Sentence(NeonSidekick.ActiveDirectory.Ldap.LdapResultCodes.TlsUnanswered, "lab", "dc1", 636, "me", ""));
        string kerberos = DirectoryErrors.Sentence(NeonSidekick.ActiveDirectory.Ldap.LdapResultCodes.KerberosFailed, "lab", "dc1", 389, "me", "");
        Assert.True(kerberos.IndexOf("named by its own name", StringComparison.Ordinal) < kerberos.IndexOf("kinit", StringComparison.Ordinal));
        Assert.Contains("no runas", kerberos);
        Assert.Contains("encryption type", DirectoryErrors.Sentence(0x31, "lab", "dc1", 389, "me", DirectoryText.NoTicketPrefix + " as me@LAB"));
    }

    /// <summary>S3: a chain's own reasons.</summary>
    [Fact]
    public void S3_TheChainsReasons()
    {
        var chain = System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors;
        Assert.Contains("expired", DirectoryText.CertificateRefused("dc1", chain, System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.NotTimeValid));
        Assert.Contains("revoked", DirectoryText.CertificateRefused("dc1", chain, System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.Revoked));
        Assert.Contains("could not be checked", DirectoryText.CertificateRefused("dc1", chain, System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.OfflineRevocation));
        Assert.Equal("no authority this machine trusts signed it", DirectoryText.CertificateRefused("dc1", chain));
    }

    /// <summary>S18: the test's count agrees.</summary>
    [Fact]
    public void S18_TheTestsCount_Agrees()
    {
        Assert.EndsWith("0 users read.", DirectoryText.TestOk("lab", "dc1", "lab.example", 0));
        Assert.EndsWith("1 user read.", DirectoryText.TestOk("lab", "dc1", "lab.example", 1));
    }

    /// <summary>J9, J14: a DOMAIN\name and a DNS name with its dot.</summary>
    [Fact]
    public void J9_J14_TheNamesPeopleType()
    {
        Assert.Contains("(sAMAccountName=carol)", LdapFilter.UserByName(@"LAB\carol"));
        Assert.Contains("(sAMAccountName=Ops)", LdapFilter.GroupByName(@"LAB\Ops"));
        Assert.DoesNotContain("(sAMAccountName=carol)", LdapFilter.UserByName("carol x"));
        // DP7 (the branch review, 2026-10-23): with the domain known, only its own prefix counts.
        Assert.Contains("(sAMAccountName=carol)", LdapFilter.UserByName(@"LAB\carol", "lab.neon.test"));
        Assert.Contains("(sAMAccountName=carol)", LdapFilter.UserByName(@"lab.neon.test\carol", "lab.neon.test."));
        Assert.DoesNotContain("(sAMAccountName=administrator)", LdapFilter.UserByName(@"OTHERDOM\administrator", "lab.neon.test"));
        Assert.DoesNotContain("(sAMAccountName=Ops)", LdapFilter.GroupByName(@"OTHERDOM\Ops", "lab.neon.test"));
        string computer = LdapFilter.ComputerByName("fs01.lab.neon.test.");
        Assert.Contains("(sAMAccountName=fs01$)(cn=fs01)(dNSHostName=fs01.lab.neon.test)", computer);
        Assert.Contains("(sAMAccountName=WS01$)(cn=WS01)", LdapFilter.ComputerByName("WS01$"));
        var source = Source();
        Assert.StartsWith("User ⟪carol⟫", Run(session => DirectoryQueries.UserAnswer(session, source, @"LAB\carol", _time.GetUtcNow(), TimeZoneInfo.Utc)).Head);
    }

    /// <summary>P9: GeneralizedTime's forms.</summary>
    [Theory]
    [InlineData("20261015123456.0Z", "2026-10-15T12:34:56Z")]
    [InlineData("20261015123456Z", "2026-10-15T12:34:56Z")]
    [InlineData("202610151234Z", "2026-10-15T12:34:00Z")]
    [InlineData("2026101512Z", "2026-10-15T12:00:00Z")]
    [InlineData("20261015143456+0200", "2026-10-15T12:34:56Z")]
    [InlineData("20261015073456.5-05", "2026-10-15T12:34:56Z")]
    [InlineData("20261015123456", "2026-10-15T12:34:56Z")]
    [InlineData("2026101512345Z", null)]
    [InlineData("20261015123456+2500", null)]
    [InlineData("soon", null)]
    public void P9_GeneralizedTime_TheForms(string text, string? utc) =>
        Assert.Equal(utc is null ? null : DateTimeOffset.Parse(utc, CultureInfo.InvariantCulture), AdValues.GeneralizedTime(text));

    /// <summary>P11: an unknown account on a skewed KDC is unknown, not a clock.</summary>
    [Fact]
    public void P11_TheAccountsAnswer_BeforeTheClock()
    {
        var now = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        byte[] reply = KrbError(KdcProbe.PrincipalUnknown, now.AddHours(1));
        Assert.Equal(KdcVerdict.UnknownAccount, KdcProbe.Read(reply, now).Verdict);
    }

    /// <summary>A KRB-ERROR with an stime and an error code, as a KDC sends it (RFC 4120 §5.9.1).</summary>
    private static byte[] KrbError(int code, DateTimeOffset stime)
    {
        var writer = new System.Formats.Asn1.AsnWriter(System.Formats.Asn1.AsnEncodingRules.DER);
        using (writer.PushSequence(new System.Formats.Asn1.Asn1Tag(System.Formats.Asn1.TagClass.Application, 30, isConstructed: true)))
        using (writer.PushSequence())
        {
            void Int(int field, int value)
            {
                using (writer.PushSequence(new System.Formats.Asn1.Asn1Tag(System.Formats.Asn1.TagClass.ContextSpecific, field, isConstructed: true)))
                {
                    writer.WriteInteger(value);
                }
            }

            Int(0, 5);
            Int(1, 30);
            using (writer.PushSequence(new System.Formats.Asn1.Asn1Tag(System.Formats.Asn1.TagClass.ContextSpecific, 4, isConstructed: true)))
            {
                writer.WriteGeneralizedTime(stime, omitFractionalSeconds: true);
            }

            Int(5, 0);
            Int(6, code);
        }

        return writer.Encode();
    }

    /// <summary>S14, K5: a date carries its own offset, and the CSV header names the zone.</summary>
    [Fact]
    public void S14_K5_ADate_CarriesItsOffset()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test+2", TimeSpan.FromHours(2), "Test+2", "Test+2");
        Assert.Equal("2026-10-23 14:00 +02:00", DirectoryRender.Date(new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero), zone));
        Assert.Equal("UTC", DirectoryCsv.ZoneName(TimeZoneInfo.Utc));
        Assert.Equal("Test+2", DirectoryCsv.ZoneName(zone));
    }

    /// <summary>P2, P6, V6, V7: /ad's line.</summary>
    [Fact]
    public void P2_P6_V6_V7_TheLine()
    {
        var (domainAdmins, _) = DirectoryCommand.Parse("Domain Admins");
        Assert.Equal(("name", "Domain Admins"), (domainAdmins!.Verb, domainAdmins.Argument));
        var (gpo, _) = DirectoryCommand.Parse("Group Policy Creator Owners");
        Assert.Equal(("name", "Group Policy Creator Owners"), (gpo!.Verb, gpo.Argument));
        var (lower, _) = DirectoryCommand.Parse("domain admins");
        Assert.Equal("name", lower!.Verb);
        Assert.Equal("domain", DirectoryCommand.Parse("domain").Args!.Verb);
        var (named, _) = DirectoryCommand.Parse("group Nested Admins");
        Assert.Equal(("Nested Admins", false), (named!.Argument, named.Nested));
        var (walked, _) = DirectoryCommand.Parse("group Staff nested");
        Assert.Equal(("Staff", true), (walked!.Argument, walked.Nested));
        Assert.Equal(DirectoryText.KeyNeedsValue("domain"), DirectoryCommand.Parse("find locked domain:").Error);
        Assert.Contains("days", DirectoryCommand.Parse("find stale_users 9999").Error);
    }
}
