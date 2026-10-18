using System.Formats.Asn1;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using NeonSidekick.ActiveDirectory;
using NeonSidekick.ActiveDirectory.Ldap;
using NeonSidekick.Network;

namespace NeonSidekick.Tests;

/// <summary>
/// The managed LDAP client (2026-10-18, the Mac AD plan's phase 1): the filter codec, the messages against bytes Samba exchanged with
/// Phase 0's spike (the made-up <c>LAB.NEON.TEST</c>, cleartext, the bind's credentials replaced), the framing, SASL GSSAPI's exchange over a
/// fake wrapping layer, and <see cref="ManagedDirectorySystem"/> over a loopback fake server through its internal constructor — the DC's
/// Kerberos name, the KDC check behind the hold, paging, limits, references, the server leaving — and the read-only pins. Portable: no KDC,
/// no network beyond loopback.
/// </summary>
public sealed class LdapManagedTests
{
    /// <summary>A message Phase 0's spike exchanged with the local <c>samba_ad_dev</c> (<c>LAB.NEON.TEST</c>), by its name in the fixture.</summary>
    private static string Capture(string name) =>
        File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ldap", "samba-phase0.txt")).First(l => l.StartsWith(name + " ", StringComparison.Ordinal)).Split(' ')[1];

    private static int IdOf(byte[] message) => (int)new AsnReader(message, AsnEncodingRules.BER).ReadSequence().ReadInteger();

    // ── The filter codec ─────────────────────────────────────────────────────

    [Fact]
    public void Present_And_Equality_EncodeAsRfc4511Says()
    {
        Assert.Equal("870B6F626A656374436C617373", Convert.ToHexString(LdapFilterCodec.Encode("(objectClass=*)")));
        Assert.Equal("A3150405" + "636F6C6F72" + "040C" + "726564" + "00" + "2A" + "2829" + "5C" + "626C7565", Convert.ToHexString(LdapFilterCodec.Encode(@"(color=red\00\2a\28\29\5cblue)")));
    }

    [Fact]
    public void Substrings_And_Composites_Nest()
    {
        var reader = new AsnReader(LdapFilterCodec.Encode("(&(|(cn=*ann*)(sn=Lee*))(!(mail=*x))(uSNChanged>=5)(uSNChanged<=9)(cn~=ann))"), AsnEncodingRules.BER);
        var and = reader.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true));
        var or = and.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 1, true));
        var any = or.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 4, true));
        Assert.Equal("cn", Encoding.UTF8.GetString(any.ReadOctetString()));
        var parts = any.ReadSequence();
        Assert.Equal("ann", Encoding.UTF8.GetString(parts.ReadOctetString(new Asn1Tag(TagClass.ContextSpecific, 1))));
        Assert.False(parts.HasData);
        var initial = or.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 4, true));
        initial.ReadOctetString();
        Assert.Equal("Lee", Encoding.UTF8.GetString(initial.ReadSequence().ReadOctetString(new Asn1Tag(TagClass.ContextSpecific, 0))));
        var not = and.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 2, true));
        var final = not.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 4, true));
        final.ReadOctetString();
        Assert.Equal("x", Encoding.UTF8.GetString(final.ReadSequence().ReadOctetString(new Asn1Tag(TagClass.ContextSpecific, 2))));
        and.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 5, true));
        and.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 6, true));
        and.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 8, true));
        Assert.False(and.HasData);
    }

    [Fact]
    public void ExtensibleMatch_CarriesRuleAttributeAndValue()
    {
        var match = new AsnReader(LdapFilterCodec.Encode("(userAccountControl:1.2.840.113556.1.4.803:=2)"), AsnEncodingRules.BER).ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 9, true));

        Assert.Equal("1.2.840.113556.1.4.803", Encoding.UTF8.GetString(match.ReadOctetString(new Asn1Tag(TagClass.ContextSpecific, 1))));
        Assert.Equal("userAccountControl", Encoding.UTF8.GetString(match.ReadOctetString(new Asn1Tag(TagClass.ContextSpecific, 2))));
        Assert.Equal("2", Encoding.UTF8.GetString(match.ReadOctetString(new Asn1Tag(TagClass.ContextSpecific, 3))));
        Assert.False(match.HasData);
        Assert.NotEmpty(LdapFilterCodec.Encode("(member:dn:1.2.840.113556.1.4.1941:=CN=x)"));
    }

    [Fact]
    public void ABinaryValue_SurvivesItsEscapes()
    {
        byte[] sid = [1, 5, 0, 0, 0, 0, 0, 5, 21, 0, 0, 0, 0x2A, 0x28, 0x5C, 0xFF];
        string filter = "(objectSid=" + LdapFilter.EscapeBytes(sid) + ")";
        var ava = new AsnReader(LdapFilterCodec.Encode(filter), AsnEncodingRules.BER).ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 3, true));
        ava.ReadOctetString();

        Assert.Equal(sid, ava.ReadOctetString());
    }

    [Theory]
    [InlineData("objectClass=*")]
    [InlineData("(objectClass=*")]
    [InlineData("(&)")]
    [InlineData("(cn=a)(cn=b)")]
    [InlineData(@"(cn=\4)")]
    [InlineData(@"(cn=\zz)")]
    [InlineData("(c n=a)")]
    [InlineData("(cn=a(b)")]
    [InlineData("(cn=a**b)")]
    [InlineData("(:=x)")]
    [InlineData("")]
    public void AMalformedFilter_IsRefusedBeforeSending(string filter)
    {
        var refused = Assert.Throws<DirectoryException>(() => LdapFilterCodec.Encode(filter));
        Assert.Equal(LdapResultCodes.FilterError, refused.Code);
    }

    [Fact]
    public void NestingPastTheLimit_IsRefused()
    {
        string deep = string.Concat(Enumerable.Repeat("(!", LdapFilterCodec.MaxDepth + 2)) + "(cn=a)" + new string(')', LdapFilterCodec.MaxDepth + 2);
        Assert.Throws<DirectoryException>(() => LdapFilterCodec.Encode(deep));
        string fine = string.Concat(Enumerable.Repeat("(!", 10)) + "(cn=a)" + new string(')', 10);
        Assert.NotEmpty(LdapFilterCodec.Encode(fine));
    }

    /// <summary>Every filter the directory queries build encodes: the constants (fragments wrapped in an AND) and each builder with hostile input.</summary>
    [Fact]
    public void EveryFilterTheQueriesBuild_Encodes()
    {
        var constants = typeof(LdapFilter).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!).ToList();
        Assert.NotEmpty(constants);
        foreach (string constant in constants.Where(c => c.StartsWith('(')))
        {
            Assert.NotEmpty(LdapFilterCodec.Encode("(&" + constant + ")"));
        }

        string hostile = "a*b(c)\\d\0é";
        foreach (string built in new[]
        {
            LdapFilter.UserByName(hostile), LdapFilter.GroupByName(hostile), LdapFilter.ComputerByName(hostile), LdapFilter.NameContains(hostile),
            LdapFilter.NameContains(hostile, "user"), LdapFilter.OsContains(hostile), LdapFilter.OuNamed(hostile), LdapFilter.ByDns(["CN=x,DC=a", "CN=" + hostile]),
            LdapFilter.BySids([[1, 2, 3]]), LdapFilter.PrimaryGroupIn(null, [513, 1104]), LdapFilter.AccountExpiresBetween(1, 2),
            LdapFilter.CreatedSince(null, DateTimeOffset.UnixEpoch), LdapFilter.ByDn(LdapFilter.Groups, "CN=" + hostile),
        })
        {
            Assert.NotEmpty(LdapFilterCodec.Encode(built));
        }
    }

    // ── The messages ─────────────────────────────────────────────────────────

    /// <summary>The RootDSE request, a bind and a paged search, byte for byte what the spike sent Samba and Samba answered.</summary>
    [Fact]
    public void Requests_AreByteForByteWhatSambaTook()
    {
        byte[] root = Convert.FromHexString(Capture("rootdse-request"));
        byte[] bind = Convert.FromHexString(Capture("bind-request"));
        byte[] paged = Convert.FromHexString(Capture("paged-search-request"));

        Assert.Equal(root, LdapMessages.Search(IdOf(root), "", DirectoryScope.Base, "(objectClass=*)", ["dnsHostName", "defaultNamingContext"], 1000, 30));
        Assert.Equal(bind, LdapMessages.Bind(IdOf(bind), Encoding.ASCII.GetBytes("<sasl credentials>")));
        Assert.Equal(paged, LdapMessages.Search(IdOf(paged), "DC=lab,DC=neon,DC=test", DirectoryScope.Subtree, "(sAMAccountName=*)", ["sAMAccountName", "member;range=0-*"], 1000, 30, pageSize: 2));
    }

    [Fact]
    public void Replies_AreReadAsSambaSentThem()
    {
        var entry = LdapMessages.Read(Convert.FromHexString(Capture("rootdse-entry")));
        var done = LdapMessages.Read(Convert.FromHexString(Capture("rootdse-done")));
        var member = LdapMessages.Read(Convert.FromHexString(Capture("search-entry")));
        var page = LdapMessages.Read(Convert.FromHexString(Capture("search-done")));

        Assert.Equal(LdapOp.SearchResultEntry, entry.Op);
        Assert.Equal("", entry.Dn);
        Assert.Contains(entry.Attributes!, a => a.Name == "dnsHostName" && Encoding.UTF8.GetString(a.Values[0]) == "DC1.lab.neon.test");
        Assert.Equal(LdapOp.SearchResultDone, done.Op);
        Assert.Equal(0, done.ResultCode);
        Assert.Equal("CN=Protected Users,CN=Users,DC=lab,DC=neon,DC=test", member.Dn);
        Assert.Equal(LdapOp.SearchResultDone, page.Op);
        Assert.NotNull(page.Cookie);
        Assert.NotEmpty(page.Cookie!);
    }

    [Fact]
    public void MessageLength_ReadsShortAndLongForms_AndRefusesTheRest()
    {
        Assert.Null(LdapMessages.MessageLength([0x30], 100));
        Assert.Equal(7, LdapMessages.MessageLength([0x30, 0x05], 100));
        Assert.Null(LdapMessages.MessageLength([0x30, 0x82, 0x01], 1000));
        Assert.Equal(4 + 0x0123, LdapMessages.MessageLength([0x30, 0x82, 0x01, 0x23], 1000));
        Assert.Throws<DirectoryException>(() => LdapMessages.MessageLength([0x31, 0x05], 100));
        Assert.Throws<DirectoryException>(() => LdapMessages.MessageLength([0x30, 0x80], 100));
        Assert.Throws<DirectoryException>(() => LdapMessages.MessageLength([0x30, 0x84, 0x7F, 0, 0, 0], 1000));
    }

    [Fact]
    public void AGarbledReply_IsADecodingError_NeverACrash()
    {
        Assert.Equal(LdapResultCodes.DecodingError, Assert.Throws<DirectoryException>(() => LdapMessages.Read(new byte[] { 0x30, 0x03, 0x02, 0x01, 0x01 })).Code);
        Assert.Equal(LdapResultCodes.DecodingError, Assert.Throws<DirectoryException>(() => LdapMessages.Read(new byte[] { 0x30, 0x05, 0x02, 0x01, 0x01, 0x04, 0x00 })).Code);
    }

    // ── Read-only by construction ───────────────────────────────────────────

    /// <summary>The client writes a bind, a search and an unbind, nothing else: no add, modify, delete, rename, compare or extended request.</summary>
    [Fact]
    public void TheClient_WritesNoChangingOperation()
    {
        var writers = typeof(LdapMessages).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.ReturnType == typeof(byte[])).Select(m => m.Name).Order().ToList();
        Assert.Equal(["Bind", "Search", "Unbind"], writers);

        string folder = Path.Combine(RepoRoot(), "src", "NeonSidekick", "ActiveDirectory", "Ldap");
        string source = File.ReadAllText(Path.Combine(folder, "LdapMessages.cs"));
        var written = Regex.Matches(source, @"new Asn1Tag\(TagClass\.Application, (\d+)").Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToHashSet();
        Assert.Subset(new HashSet<int> { 0, 2, 3 }, written);   // bind, unbind, search; replies are read by their own op
        foreach (string file in Directory.EnumerateFiles(folder, "*.cs").Where(f => !f.EndsWith("LdapMessages.cs", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain("TagClass.Application", File.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    /// <summary>The app builds the system only through its public constructor; the layer with no cryptography never leaves the smoke.</summary>
    [Fact]
    public void TheApp_UsesOnlyThePublicConstructor()
    {
        string src = Path.Combine(RepoRoot(), "src", "NeonSidekick");
        foreach (string file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
        {
            string text = File.ReadAllText(file);
            foreach (Match built in Regex.Matches(text, @"new ManagedDirectorySystem\((.*?)\)"))
            {
                Assert.True(built.Groups[1].Value.Length == 0, file + " builds ManagedDirectorySystem with arguments");
            }

            if (!file.EndsWith("SmokeChecks.Ldap.cs", StringComparison.Ordinal))
            {
                Assert.DoesNotContain("LoopbackSaslLayer", text, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>The smoke's <c>ad:ldap</c> under the JIT: the loopback bind, RootDSE and two pages (and on a Mac a made-up realm refused).</summary>
    [Fact]
    public void Smoke_ProbeLdapClient_Passes()
    {
        var check = NeonSidekick.App.SmokeChecks.ProbeLdapClient();
        Assert.True(check.Passed, check.Detail);
        Assert.Equal("ad:ldap", check.Name);
        Assert.Contains("2 entries over 2 pages", check.Detail, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "NeonSidekick.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new InvalidOperationException("the repository root was not found");
    }

    // ── Names, discovery ────────────────────────────────────────────────────

    [Theory]
    [InlineData("DC1.lab.neon.test", "localhost", "lab.neon.test", "DC1.lab.neon.test")]
    [InlineData("dc1.LAB.neon.test.", "10.0.0.5", "lab.neon.test", "dc1.LAB.neon.test")]
    [InlineData("evil.example", "dc1.lab.neon.test", "lab.neon.test", "dc1.lab.neon.test")]
    [InlineData("notlab.neon.test", "dc1.lab.neon.test", "lab.neon.test", "dc1.lab.neon.test")]
    [InlineData(null, "dc1.lab.neon.test", "lab.neon.test", "dc1.lab.neon.test")]
    [InlineData("DC1.lab.neon.test", "localhost", null, "DC1.lab.neon.test")]
    public void TheKerberosHost_IsTheDcsOwnName_OnlyInsideTheDomain(string? dnsHostName, string connected, string? domain, string expected) =>
        Assert.Equal(expected, ManagedDirectorySystem.KerberosHost(dnsHostName, connected, domain));

    [Fact]
    public void TheServiceName_IsHostBased_InLowerCase() =>
        Assert.Equal("ldap/dc1.lab.neon.test", NegotiateSaslLayer.ServiceName(" DC1.Lab.Neon.Test. "));

    [Fact]
    public void Candidates_AreTheServerNamed_OrTheDomainsDcs_ThenItsName()
    {
        IReadOnlyList<string> Locate(string d) => ["dc2.lab.neon.test", "dc1.lab.neon.test", "dc3.lab.neon.test", "dc4.lab.neon.test"];

        Assert.Equal(["dc9.lab.neon.test"], ManagedDirectorySystem.Candidates(new DirectoryTarget("dc9.lab.neon.test", 389, false, null, "lab.neon.test"), Locate));
        Assert.Equal(["dc2.lab.neon.test", "dc1.lab.neon.test", "dc3.lab.neon.test", "lab.neon.test"], ManagedDirectorySystem.Candidates(new DirectoryTarget("lab.neon.test", 389, false, null, "lab.neon.test"), Locate));
        Assert.Equal(["localhost"], ManagedDirectorySystem.Candidates(new DirectoryTarget("localhost", 389, false, null), Locate));
    }

    [Fact]
    public void SrvTargets_AreByPriority_ThenWeight()
    {
        var answers = new List<DnsRecord>
        {
            new("_ldap", DnsWire.Srv, 60, "10 50 389 b.lab.neon.test."),
            new("_ldap", DnsWire.Srv, 60, "0 10 389 c.lab.neon.test."),
            new("_ldap", DnsWire.Srv, 60, "0 90 389 a.lab.neon.test."),
            new("_ldap", DnsWire.Srv, 60, "0 0 389 ."),
            new("_ldap", 1, 60, "10.0.0.1"),
            new("_ldap", DnsWire.Srv, 60, "garbage"),
        };

        Assert.Equal(["a.lab.neon.test", "c.lab.neon.test", "b.lab.neon.test"], ManagedDirectorySystem.SrvTargets(answers));
    }

    // ── SASL GSSAPI over a fake layer ───────────────────────────────────────

    [Theory]
    [InlineData(new byte[] { 7, 0, 0x10, 0 }, false, true, true, SaslSecurity.Confidentiality)]
    [InlineData(new byte[] { 7, 0, 0x10, 0 }, false, true, false, SaslSecurity.Integrity)]
    [InlineData(new byte[] { 3, 0, 0x10, 0 }, false, true, true, SaslSecurity.Integrity)]
    [InlineData(new byte[] { 0x7F, 0, 0x10, 0 }, false, true, true, SaslSecurity.Confidentiality)]
    [InlineData(new byte[] { 7, 0, 0x10, 0 }, true, true, true, SaslSecurity.None)]
    public void TheLayerChoice_IsTheStrongestTheContextHas_OrNoneUnderTls(byte[] offer, bool tls, bool signed, bool encrypted, SaslSecurity expected)
    {
        var layer = new FakeLayer { Signed = signed, Encrypted = encrypted };
        Assert.Equal(expected, SaslGssapiExchange.Choose(offer, tls, layer).Security);
    }

    [Theory]
    [InlineData(new byte[] { 7, 0, 0x10 }, false)]
    [InlineData(new byte[] { 7, 0, 0x10, 0, 0 }, false)]
    [InlineData(new byte[] { 1, 0, 0, 0 }, false)]
    [InlineData(new byte[] { 1, 0, 1, 0 }, false)]
    [InlineData(new byte[] { 6, 0, 1, 0 }, true)]
    public void AnUnacceptableOffer_IsRefused(byte[] offer, bool tls)
    {
        Assert.Throws<DirectoryException>(() => SaslGssapiExchange.Choose(offer, tls, new FakeLayer()));
    }

    [Fact]
    public void TheReply_CarriesTheChoiceAndThisClientsMaximum()
    {
        Assert.Equal([4, 0xFF, 0xFF, 0xFF], SaslGssapiExchange.Reply(SaslSecurity.Confidentiality));
        Assert.Equal([1, 0, 0, 0], SaslGssapiExchange.Reply(SaslSecurity.None));
    }

    /// <summary>The whole exchange and a sealed paged search over a loopback server: two tokens, the offer, the reply, then three pages wrapped.</summary>
    [Fact]
    public async Task AFullBind_ThenPages_OverAWrappedConnection()
    {
        await using var server = FakeLdapServer.Start(async s =>
        {
            await s.ServeBindAsync(offer: [7, 0, 0x10, 0]);
            await s.ServeRootDseAsync(wrapped: true);
            for (int page = 0; page < 3; page++)
            {
                var request = await s.ReadAsync(wrapped: true);
                await s.SendAsync(Replies.Entry(request.Id, "CN=u" + page + ",DC=lab,DC=neon,DC=test", ("sAMAccountName", ["u" + page]), ("member;range=0-1499", ["CN=a", "CN=b"]), ("unicodePwd", ["x"])), wrapped: true);
                await s.SendAsync(Replies.Reference(request.Id), wrapped: true);
                await s.SendAsync(Replies.Done(request.Id, 0, cookie: page < 2 ? [(byte)page, 9] : []), wrapped: true);
            }
        });

        var layer = new FakeLayer { Signed = true, Encrypted = true };
        using var connection = new LdapConnection(server.Connect());
        var (security, maximum) = SaslGssapiExchange.Bind(connection, layer, tls: false);
        connection.StartSecurity(layer, security, maximum);

        Assert.Equal(SaslSecurity.Confidentiality, security);
        Assert.Equal(0x1000, maximum);
        Assert.Equal(2, layer.Steps);
        var session = SessionOver(connection);
        Assert.Equal("DC1.lab.neon.test", session.RootDse().Text("dnsHostName"));
        var results = session.Search("DC=lab,DC=neon,DC=test", DirectoryScope.Subtree, "(objectClass=user)", ["sAMAccountName", "member"], 1000, paged: true);
        Assert.Equal(["u0", "u1", "u2"], results.Entries.Select(e => e.Text("sAMAccountName")));
        Assert.Equal("member;range=0-1499", results.Entries[0].RangedName("member"));
        Assert.Empty(results.Entries[0].Values("unicodePwd"));
        Assert.False(results.Truncated);
        Assert.All(server.WrappedRequests, w => Assert.True(w));
    }

    [Fact]
    public async Task TheFinalBindReply_AndTheNextWrappedBuffer_InOneWrite_AreBothRead()
    {
        await using var server = FakeLdapServer.Start(async s =>
        {
            await s.ServeBindAsync(offer: [7, 0, 0x10, 0], glueRootDse: true);
        });

        var layer = new FakeLayer { Signed = true, Encrypted = true };
        using var connection = new LdapConnection(server.Connect());
        var (security, maximum) = SaslGssapiExchange.Bind(connection, layer, tls: false);
        connection.StartSecurity(layer, security, maximum);

        var reply = connection.Receive();
        Assert.Equal(LdapOp.SearchResultEntry, reply.Op);
    }

    [Fact]
    public async Task AnUnencryptedReply_OnASealedConnection_IsRefused()
    {
        await using var server = FakeLdapServer.Start(async s =>
        {
            await s.ServeBindAsync(offer: [7, 0, 0x10, 0]);
            var request = await s.ReadAsync(wrapped: true);
            await s.SendAsync(Replies.Done(request.Id, 0), wrapped: true, encrypted: false);
        });

        var layer = new FakeLayer { Signed = true, Encrypted = true };
        using var connection = new LdapConnection(server.Connect());
        var (security, maximum) = SaslGssapiExchange.Bind(connection, layer, tls: false);
        connection.StartSecurity(layer, security, maximum);

        var refused = Assert.Throws<DirectoryException>(() => SessionOver(connection).RootDse());
        Assert.Equal(LdapResultCodes.AuthUnknown, refused.Code);
    }

    [Fact]
    public async Task ARefusedBind_KeepsTheServersCode()
    {
        await using var server = FakeLdapServer.Start(async s =>
        {
            var bind = await s.ReadAsync(wrapped: false);
            await s.SendAsync(Replies.Bind(bind.Id, LdapResultCodes.InvalidCredentials, diagnostic: "80090308: LdapErr"), wrapped: false);
        });

        using var connection = new LdapConnection(server.Connect());
        var refused = Assert.Throws<DirectoryException>(() => SaslGssapiExchange.Bind(connection, new FakeLayer(), tls: false));
        Assert.Equal(LdapResultCodes.InvalidCredentials, refused.Code);
        Assert.Contains("80090308", refused.Message, StringComparison.Ordinal);
    }

    // ── The system over the fake server ─────────────────────────────────────

    [Fact]
    public async Task Run_SignsInAsTheDcsOwnName_AndUnbinds()
    {
        await using var server = FakeLdapServer.Start(async s =>
        {
            await s.ServeRootDseAsync(wrapped: false, dnsHostName: "DC1.lab.neon.test");
            await s.ServeBindAsync(offer: [7, 0, 0x10, 0]);
            await s.ServeRootDseAsync(wrapped: true);
            var unbind = await s.ReadAsync(wrapped: true);
            Assert.Equal(2, unbind.Op);
        });

        string? host = null;
        var layer = new FakeLayer { Signed = true, Encrypted = true };
        var system = new ManagedDirectorySystem((_, h, _, _) => { host = h; return layer; }, _ => true, _ => [], (_, _, _) => new LdapConnection(server.Connect()));

        string? name = system.Run(new DirectoryTarget("localhost", 389, false, null, "lab.neon.test"), s => s.RootDse().Text("dnsHostName"));

        Assert.Equal("DC1.lab.neon.test", name);
        Assert.Equal("DC1.lab.neon.test", host);
        Assert.True(layer.Disposed);
        await server.Finished;
    }

    [Theory]
    [InlineData(true, LdapResultCodes.InvalidCredentials)]
    [InlineData(false, LdapResultCodes.KdcUnreachable)]
    public async Task ARunasSignInThatFailsBeforeTheBind_IsRefusedOnlyWhenTheKdcAnswers(bool kdcAnswers, int expected)
    {
        await using var server = FakeLdapServer.Start(s => s.ServeRootDseAsync(wrapped: false));
        var layer = new FakeLayer { FailAtStep = 1 };
        string? probed = null;
        var system = new ManagedDirectorySystem((_, _, _, _) => layer, address => { probed = address; return kdcAnswers; }, _ => [], (_, _, _) => new LdapConnection(server.Connect()));

        var refused = Assert.Throws<DirectoryException>(() => system.Run(new DirectoryTarget("localhost", 389, false, new DirectoryLogin("reader@LAB.NEON.TEST", "x"), "lab.neon.test"), _ => 0));

        Assert.Equal(expected, refused.Code);
        Assert.Equal("localhost", probed);
    }

    [Fact]
    public async Task AWindowsSignInThatFails_IsNeverTakenForARefusedPassword()
    {
        await using var server = FakeLdapServer.Start(s => s.ServeRootDseAsync(wrapped: false));
        bool probed = false;
        var system = new ManagedDirectorySystem((_, _, _, _) => new FakeLayer { FailAtStep = 1 }, _ => probed = true, _ => [], (_, _, _) => new LdapConnection(server.Connect()));

        var refused = Assert.Throws<DirectoryException>(() => system.Run(new DirectoryTarget("localhost", 389, false, null, "lab.neon.test"), _ => 0));

        Assert.Equal(LdapResultCodes.KerberosFailed, refused.Code);
        Assert.False(probed);
    }

    [Theory]
    [InlineData(LdapResultCodes.SizeLimitExceeded, true, false)]
    [InlineData(LdapResultCodes.TimeLimitExceeded, true, true)]
    public async Task ALimitedSearch_IsTruncated_AndSaysWhichLimit(int code, bool truncated, bool timedOut)
    {
        await using var server = FakeLdapServer.Start(async s =>
        {
            await s.ServeBindAsync(offer: [7, 0, 0x10, 0]);
            var request = await s.ReadAsync(wrapped: true);
            await s.SendAsync(Replies.Entry(request.Id, "CN=a"), wrapped: true);
            await s.SendAsync(Replies.Done(request.Id, code), wrapped: true);
        });

        var connection = BoundOver(server);
        var results = SessionOver(connection).Search("DC=x", DirectoryScope.Subtree, "(cn=*)", ["cn"], 5, paged: false);

        Assert.Single(results.Entries);
        Assert.Equal(truncated, results.Truncated);
        Assert.Equal(timedOut, results.TimedOut);
    }

    [Fact]
    public async Task TheSizeLimit_EndsPaging_AndMarksItCut()
    {
        await using var server = FakeLdapServer.Start(async s =>
        {
            await s.ServeBindAsync(offer: [7, 0, 0x10, 0]);
            var request = await s.ReadAsync(wrapped: true);
            for (int i = 0; i < 3; i++)
            {
                await s.SendAsync(Replies.Entry(request.Id, "CN=e" + i), wrapped: true);
            }

            await s.SendAsync(Replies.Done(request.Id, 0, cookie: [1]), wrapped: true);
        });

        var results = SessionOver(BoundOver(server)).Search("DC=x", DirectoryScope.Subtree, "(cn=*)", ["cn"], 2, paged: true);

        Assert.Equal(2, results.Entries.Count);
        Assert.True(results.Truncated);
    }

    [Theory]
    [InlineData("leaving", LdapResultCodes.ServerDown)]
    [InlineData("other-id", LdapResultCodes.DecodingError)]
    [InlineData("no-such-object", LdapResultCodes.NoSuchObject)]
    [InlineData("closed", LdapResultCodes.ServerDown)]
    public async Task AServerThatMisbehaves_IsWorded_NeverHangs(string how, int expected)
    {
        await using var server = FakeLdapServer.Start(async s =>
        {
            await s.ServeBindAsync(offer: [7, 0, 0x10, 0]);
            var request = await s.ReadAsync(wrapped: true);
            switch (how)
            {
                case "leaving":
                    await s.SendAsync(Replies.Notice(), wrapped: true);
                    break;
                case "other-id":
                    await s.SendAsync(Replies.Done(request.Id + 7, 0), wrapped: true);
                    break;
                case "no-such-object":
                    await s.SendAsync(Replies.Done(request.Id, LdapResultCodes.NoSuchObject, diagnostic: "0000208D"), wrapped: true);
                    break;
                default:
                    s.Close();
                    break;
            }
        });

        var refused = Assert.Throws<DirectoryException>(() => SessionOver(BoundOver(server)).Search("DC=x", DirectoryScope.Base, "(cn=*)", ["cn"], 1, paged: false));
        Assert.Equal(expected, refused.Code);
    }

    [Fact]
    public async Task ASlowServer_IsATimeout()
    {
        await using var server = FakeLdapServer.Start(async s =>
        {
            await s.ServeBindAsync(offer: [7, 0, 0x10, 0]);
            await s.ReadAsync(wrapped: true);
            await Task.Delay(TimeSpan.FromSeconds(10));
        });

        var refused = Assert.Throws<DirectoryException>(() => SessionOver(BoundOver(server)).Search("DC=x", DirectoryScope.Base, "(cn=*)", ["cn"], 1, paged: false, TimeSpan.FromMilliseconds(1)));
        Assert.Equal(LdapResultCodes.Timeout, refused.Code);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>A bound, sealed connection to <paramref name="server"/>, whose script starts with <c>ServeBindAsync</c>.</summary>
    private static LdapConnection BoundOver(FakeLdapServer server)
    {
        var layer = new FakeLayer { Signed = true, Encrypted = true };
        var connection = new LdapConnection(server.Connect());
        var (security, maximum) = SaslGssapiExchange.Bind(connection, layer, tls: false);
        connection.StartSecurity(layer, security, maximum);
        return connection;
    }

    /// <summary>The system's own session over a connection already bound.</summary>
    private static IDirectorySession SessionOver(LdapConnection connection) => ManagedDirectorySystem.SessionOver(connection);

    /// <summary>A layer with no KDC: tokens <c>t1</c>, <c>t2</c>; wraps as <c>[0xFA, encrypted, …]</c>, the fake server's own format.</summary>
    private sealed class FakeLayer : ISaslLayer
    {
        public int Steps { get; private set; }

        public int FailAtStep { get; init; }

        public bool Signed { get; init; } = true;

        public bool Encrypted { get; init; } = true;

        public bool Disposed { get; private set; }

        public SaslStep Step(ReadOnlySpan<byte> incoming)
        {
            Steps++;
            if (Steps == FailAtStep)
            {
                throw new DirectoryException(LdapResultCodes.KerberosFailed, "the fake sign-in failed");
            }

            return Steps == 1 ? new SaslStep("t1"u8.ToArray(), false) : new SaslStep([], true);
        }

        public byte[] Wrap(ReadOnlySpan<byte> data, bool encrypt, out bool encrypted)
        {
            encrypted = encrypt;
            return [0xFA, (byte)(encrypt ? 1 : 0), .. data];
        }

        public byte[] Unwrap(ReadOnlySpan<byte> data, out bool wasEncrypted)
        {
            if (data.Length < 2 || data[0] != 0xFA)
            {
                throw new DirectoryException(LdapResultCodes.DecodingError, "not a fake buffer");
            }

            wasEncrypted = data[1] == 1;
            return data[2..].ToArray();
        }

        public void Dispose() => Disposed = true;
    }

    /// <summary>A request the fake server read: its id and op tag.</summary>
    private readonly record struct Request(int Id, int Op);

    /// <summary>A loopback LDAP server running one script for one client.</summary>
    private sealed class FakeLdapServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private TcpClient? _client;
        private NetworkStream? _stream;

        private FakeLdapServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
        }

        public Task Finished { get; private set; } = Task.CompletedTask;

        public List<bool> WrappedRequests { get; } = [];

        public static FakeLdapServer Start(Func<FakeLdapServer, Task> script)
        {
            var server = new FakeLdapServer();
            server.Finished = Task.Run(async () =>
            {
                server._client = await server._listener.AcceptTcpClientAsync();
                server._stream = server._client.GetStream();
                try
                {
                    await script(server);
                }
                catch (IOException)
                {
                }
            });
            return server;
        }

        public Stream Connect()
        {
            var client = new TcpClient();
            client.Connect(IPAddress.Loopback, ((IPEndPoint)_listener.LocalEndpoint).Port);
            client.ReceiveTimeout = 15_000;
            return client.GetStream();
        }

        public void Close() => _client?.Close();

        public async Task<Request> ReadAsync(bool wrapped)
        {
            byte[] message;
            if (wrapped)
            {
                var head = new byte[4];
                await _stream!.ReadExactlyAsync(head);
                var token = new byte[System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(head)];
                await _stream.ReadExactlyAsync(token);
                Assert.Equal(0xFA, token[0]);
                WrappedRequests.Add(token[1] == 1);
                message = token[2..];
            }
            else
            {
                var head = new List<byte>();
                int? length = null;
                while (length is null)
                {
                    var one = new byte[1];
                    await _stream!.ReadExactlyAsync(one);
                    head.Add(one[0]);
                    length = LdapMessages.MessageLength(head.ToArray(), 1 << 20);
                }

                message = new byte[length.Value];
                head.CopyTo(message);
                await _stream!.ReadExactlyAsync(message.AsMemory(head.Count));
            }

            var outer = new AsnReader(message, AsnEncodingRules.BER).ReadSequence();
            int id = (int)outer.ReadInteger();
            return new Request(id, outer.PeekTag().TagValue);
        }

        public Task SendAsync(byte[] message, bool wrapped, bool encrypted = true) => _stream!.WriteAsync(wrapped ? Frame(message, encrypted) : message).AsTask();

        private static byte[] Frame(byte[] message, bool encrypted)
        {
            byte[] token = [0xFA, (byte)(encrypted ? 1 : 0), .. message];
            var head = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(head, token.Length);
            return [.. head, .. token];
        }

        /// <summary>Two bind round trips, then the offer, then success; the reply is checked to be the fake's unencrypted wrap of 4+ bytes.</summary>
        public async Task ServeBindAsync(byte[] offer, bool glueRootDse = false)
        {
            var first = await ReadAsync(wrapped: false);
            await SendAsync(Replies.Bind(first.Id, LdapResultCodes.SaslBindInProgress, creds: "s1"u8.ToArray()), wrapped: false);
            var second = await ReadAsync(wrapped: false);
            await SendAsync(Replies.Bind(second.Id, LdapResultCodes.SaslBindInProgress, creds: [0xFA, 0, .. offer]), wrapped: false);
            var third = await ReadAsync(wrapped: false);
            byte[] ok = Replies.Bind(third.Id, LdapResultCodes.Success);
            await _stream!.WriteAsync(glueRootDse ? [.. ok, .. Frame(Replies.Entry(9, "", ("dnsHostName", ["DC1.lab.neon.test"])), true)] : ok);
        }

        public async Task ServeRootDseAsync(bool wrapped, string dnsHostName = "DC1.lab.neon.test")
        {
            var request = await ReadAsync(wrapped);
            await SendAsync(Replies.Entry(request.Id, "", ("dnsHostName", [dnsHostName]), ("defaultNamingContext", ["DC=lab,DC=neon,DC=test"])), wrapped);
            await SendAsync(Replies.Done(request.Id, 0), wrapped);
        }

        public async ValueTask DisposeAsync()
        {
            _client?.Dispose();
            _listener.Stop();
            try
            {
                await Finished.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex) when (ex is TimeoutException or SocketException or ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>Server replies, BER.</summary>
    private static class Replies
    {
        public static byte[] Bind(int id, int code, byte[]? creds = null, string diagnostic = "") => Result(id, 1, code, diagnostic, creds);

        public static byte[] Done(int id, int code, byte[]? cookie = null, string diagnostic = "")
        {
            var writer = new AsnWriter(AsnEncodingRules.BER);
            using (writer.PushSequence())
            {
                writer.WriteInteger(id);
                WriteResult(writer, 5, code, diagnostic, null);
                if (cookie is not null)
                {
                    using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
                    using (writer.PushSequence())
                    {
                        writer.WriteOctetString(Encoding.UTF8.GetBytes(LdapMessages.PagedResultsOid));
                        var value = new AsnWriter(AsnEncodingRules.BER);
                        using (value.PushSequence())
                        {
                            value.WriteInteger(0);
                            value.WriteOctetString(cookie);
                        }

                        writer.WriteOctetString(value.Encode());
                    }
                }
            }

            return writer.Encode();
        }

        public static byte[] Notice() => Result(0, 24, LdapResultCodes.ServerDown + 0, "the server is shutting down", null);

        public static byte[] Reference(int id)
        {
            var writer = new AsnWriter(AsnEncodingRules.BER);
            using (writer.PushSequence())
            {
                writer.WriteInteger(id);
                using (writer.PushSequence(new Asn1Tag(TagClass.Application, 19, true)))
                {
                    writer.WriteOctetString("ldap://elsewhere/DC=x"u8);
                }
            }

            return writer.Encode();
        }

        public static byte[] Entry(int id, string dn, params (string Name, string[] Values)[] attributes)
        {
            var writer = new AsnWriter(AsnEncodingRules.BER);
            using (writer.PushSequence())
            {
                writer.WriteInteger(id);
                using (writer.PushSequence(new Asn1Tag(TagClass.Application, 4, true)))
                {
                    writer.WriteOctetString(Encoding.UTF8.GetBytes(dn));
                    using (writer.PushSequence())
                    {
                        foreach (var (name, values) in attributes)
                        {
                            using (writer.PushSequence())
                            {
                                writer.WriteOctetString(Encoding.UTF8.GetBytes(name));
                                using (writer.PushSetOf())
                                {
                                    foreach (string value in values)
                                    {
                                        writer.WriteOctetString(Encoding.UTF8.GetBytes(value));
                                    }
                                }
                            }
                        }
                    }
                }
            }

            return writer.Encode();
        }

        private static byte[] Result(int id, int op, int code, string diagnostic, byte[]? creds)
        {
            var writer = new AsnWriter(AsnEncodingRules.BER);
            using (writer.PushSequence())
            {
                writer.WriteInteger(id);
                WriteResult(writer, op, code, diagnostic, creds);
            }

            return writer.Encode();
        }

        private static void WriteResult(AsnWriter writer, int op, int code, string diagnostic, byte[]? creds)
        {
            using (writer.PushSequence(new Asn1Tag(TagClass.Application, op, true)))
            {
                var enumerated = new AsnWriter(AsnEncodingRules.BER);
                enumerated.WriteInteger(code);
                byte[] bytes = enumerated.Encode();
                bytes[0] = 0x0A;
                writer.WriteEncodedValue(bytes);
                writer.WriteOctetString([]);
                writer.WriteOctetString(Encoding.UTF8.GetBytes(diagnostic));
                if (creds is not null)
                {
                    writer.WriteOctetString(creds, new Asn1Tag(TagClass.ContextSpecific, 7));
                }
            }
        }
    }
}
