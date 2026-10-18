using NeonSidekick.ActiveDirectory;
using NeonSidekick.ActiveDirectory.Ldap;
using NeonSidekick.Llm.Tools;

namespace NeonSidekick.Tests;

/// <summary>
/// Phase 2 of the Mac AD plan (2026-10-18): the hold after a refused runas password (both systems, the user's call), a Mac's account rules
/// (name@REALM at the call, the Kerberos ticket for <c>windows</c>), the fresh file's two texts, the transport line <c>--ad-check</c> and the
/// wizard's test add, and the managed client's error sentences.
/// </summary>
public sealed partial class ActiveDirectoryTests
{
    /// <summary>A runas entry with a password of its own, so the process-wide hold never meets another test's.</summary>
    private void RunAs(string user, string? password = null) =>
        _config = new DirectoryDomainConfig { Domain = "lab.example", Server = "localhost", Auth = "runas", User = user, Password = password ?? "pw-" + Guid.NewGuid().ToString("N") };

    // ── The hold ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Hold_ARefusedRunAsPassword_IsTriedOnce_ThenHeld_UntilItChanges()
    {
        RunAs("reader@LAB.EXAMPLE");
        _system.FailWith = 0x31;
        var source = Source();
        string first = await Call(new AdDomainTool(Access()));
        Assert.Equal(DirectoryErrors.Sentence(0x31, source.Where, source.Target.Server, source.Target.Port, source.Domain.Account, "fake failure"), first);
        Assert.Equal(1, _system.Runs);

        // The same password again, by any tool: answered here, the DC never asked.
        Assert.Equal(DirectoryText.Held(source.Where, source.Domain.Account), await Call(new AdDomainTool(Access())));
        Assert.Equal(DirectoryText.Held(source.Where, source.Domain.Account), await Call(new AdUserTool(Access()), ("name", "alice")));
        Assert.Equal(1, _system.Runs);

        // A new password is tried at once; once it is right, the domain reads again.
        RunAs("reader@LAB.EXAMPLE");
        _system.FailWith = null;
        Assert.Contains("functional level", await Call(new AdDomainTool(Access())));
        Assert.Equal(2, _system.Runs);
    }

    [Fact]
    public async Task Hold_NeverForWindowsSignIns_NtlmBlocked_AnUnreachableKdc_OrADcThatIsDown()
    {
        // A windows sign-in has no password to hold.
        _system.FailWith = 0x31;
        await Call(new AdDomainTool(Access()));
        await Call(new AdDomainTool(Access()));
        Assert.Equal(2, _system.Runs);

        // Windows' NTLM-blocked 0x31 is policy, not a wrong password.
        RunAs("reader@LAB.EXAMPLE");
        _system.FailMessage = "NTLM authentication is blocked";
        await Call(new AdDomainTool(Access()));
        Assert.DoesNotContain("not tried again", await Call(new AdDomainTool(Access())), StringComparison.Ordinal);
        Assert.Equal(4, _system.Runs);

        // A KDC or a DC that cannot be reached refused nothing.
        foreach (int code in new[] { LdapResultCodes.KdcUnreachable, LdapResultCodes.KerberosFailed, 0x51, LdapResultCodes.Timeout })
        {
            _system.FailWith = code;
            int before = _system.Runs;
            await Call(new AdDomainTool(Access()));
            await Call(new AdDomainTool(Access()));
            Assert.Equal(before + 2, _system.Runs);
        }
    }

    [Theory]
    [InlineData(0x31, "fake failure", true)]
    [InlineData(0x31, "Negotiate: NTLM is blocked by policy", false)]
    [InlineData(0x51, "", false)]
    [InlineData(LdapResultCodes.KdcUnreachable, "", false)]
    [InlineData(LdapResultCodes.KerberosFailed, "", false)]
    public void RefusedPassword_IsOnlyTheBadCredentialsCode(int code, string detail, bool refused) =>
        Assert.Equal(refused, DirectoryAccess.RefusedPassword(code, detail));

    [Fact]
    public void Held_NamesTheDomain_TheAccount_AndWhereToSetThePassword()
    {
        string held = DirectoryText.Held("lab.example", "reader@LAB.EXAMPLE");
        Assert.StartsWith("Error: lab.example refused the password for reader@LAB.EXAMPLE", held, StringComparison.Ordinal);
        Assert.Contains("Directory set password on " + DirectoryText.MenuPath, held, StringComparison.Ordinal);
    }

    // ── A Mac's accounts ───────────────────────────────────────────────────

    [MacFact]
    public void OnAMac_ARunAsAccount_IsNameAtRealm_TheRealmInCapitals_AndDomainSlashNameIsRefusedAtTheCall()
    {
        RunAs("reader@lab.example");
        Assert.Equal("reader@LAB.EXAMPLE", Source().Target.Login!.User);

        RunAs("LAB\\reader");
        Assert.Null(_config.Problem("lab"));   // the file still loads whole: an ad.json shared with a PC
        var (source, error) = Access().Domain(null, forceRedacted: true);
        Assert.Null(source);
        Assert.Equal("Error: " + NeonSidekick.Sql.SqlText.RunAsNeedsRealm("LAB\\reader"), error);
    }

    [MacFact]
    public void OnAMac_AWindowsSignIn_IsTheKerberosTicket() =>
        Assert.Equal(DirectoryText.MacTicketAccount, new DirectoryDomain("lab", _config).Account);

    [WindowsFact]
    public void OnWindows_ARunAsAccount_IsPassedAsWritten()
    {
        RunAs("LAB\\reader");
        Assert.Equal("LAB\\reader", Source().Target.Login!.User);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EmptyText_BothSystems_LoadEmpty_AndEveryExampleLoadsOnceUncommented(bool mac)
    {
        string text = mac ? DirectoryConfigFile.MacEmptyText : DirectoryConfigFile.WindowsEmptyText;
        string root = Path.Combine(Path.GetTempPath(), "ns-ad-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "ad.json");
            File.WriteAllText(path, text);
            var (none, problems) = DirectoryConfigFile.Load(path);
            Assert.Empty(none);
            Assert.Empty(problems);

            var examples = text.Split('\n').Select(l => l.TrimStart())
                .Where(l => System.Text.RegularExpressions.Regex.IsMatch(l, "^// \"[\\w-]+\": \\{")).Select(l => l[2..]).ToList();
            File.WriteAllText(path, "{ \"domains\": {\n" + string.Join("\n", examples) + "\n} }");
            var (loaded, wrong) = DirectoryConfigFile.Load(path);
            Assert.Empty(wrong);
            Assert.Equal(["corp", "lab"], loaded.Select(d => d.Name).Order(StringComparer.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        if (mac)
        {
            Assert.Contains("reader@LAB.EXAMPLE", text, StringComparison.Ordinal);
            Assert.Contains("kinit", text, StringComparison.Ordinal);
            foreach (string windows in new[] { "dpapi", "cmdkey", "NTLM", "LAB\\\\reader", "\"current\" is" })
            {
                Assert.DoesNotContain(windows, text, StringComparison.Ordinal);
            }
        }

        Assert.Equal(OperatingSystem.IsMacOS() ? DirectoryConfigFile.MacEmptyText : DirectoryConfigFile.WindowsEmptyText, DirectoryConfigFile.EmptyText);
    }

    // ── How it connected ───────────────────────────────────────────────────

    [Fact]
    public void Transport_NamesThePort_TheKerberosName_AndTheLayer()
    {
        Assert.Equal("389, Kerberos as ldap/dc1.corp.example, sealed", DirectoryText.Transport(389, false, "ldap/dc1.corp.example", DirectoryText.LayerWord("Confidentiality")));
        Assert.Equal("636 TLS, Kerberos as ldap/dc1.corp.example, no SASL layer", DirectoryText.Transport(636, true, "ldap/dc1.corp.example", DirectoryText.LayerWord("None")));
        Assert.Equal("signed", DirectoryText.LayerWord("Integrity"));
    }

    [Fact]
    public async Task TestAsync_AddsTheTransport_WhenTheBackendNamesOne()
    {
        var domain = new DirectoryDomain("lab", _config);
        var (ok, plain) = await DirectoryAccess.TestAsync(_system, domain, _time, CancellationToken.None);
        Assert.True(ok);
        Assert.DoesNotContain("Kerberos as", plain, StringComparison.Ordinal);   // wldap32 names none: Windows' line as before

        _system.Transport = "389, Kerberos as ldap/dc1.lab.example, sealed";
        var (stillOk, named) = await DirectoryAccess.TestAsync(_system, domain, _time, CancellationToken.None);
        Assert.True(stillOk);
        Assert.EndsWith(".", plain);
        Assert.Equal(plain[..^1] + "; 389, Kerberos as ldap/dc1.lab.example, sealed.", named);
    }

    // ── The managed client's sentences ─────────────────────────────────────

    [Fact]
    public void Errors_TheManagedClientsCodes_AreWorded()
    {
        string kdc = DirectoryErrors.Sentence(LdapResultCodes.KdcUnreachable, "lab.example", "dc1.lab.example", 389, "reader@LAB.EXAMPLE", "");
        Assert.Contains("could not reach its KDC on dc1.lab.example (port 88)", kdc, StringComparison.Ordinal);
        string kerberos = DirectoryErrors.Sentence(LdapResultCodes.KerberosFailed, "lab.example", "dc1.lab.example", 389, "reader@LAB.EXAMPLE", "");
        Assert.Contains("as reader@LAB.EXAMPLE failed. Run kinit", kerberos, StringComparison.Ordinal);
        string unknown = DirectoryErrors.Sentence(0x56, "lab.example", "dc1.lab.example", 389, "x", "");
        Assert.Contains(OperatingSystem.IsMacOS() ? "no signed or sealed connection this Mac takes" : "NTLM must be allowed", unknown, StringComparison.Ordinal);
    }
}
