using System.Net;
using Microsoft.Extensions.AI;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Network;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>Phase 9b of the admin review's fixes (2026-10-23): the network tools' probes, DNS, TLS preambles and answers.</summary>
public sealed partial class AdminFixesTests
{
    /// <summary>A reply's header: id 7, QR, the rcode, the question, answer and additional counts.</summary>
    private static List<byte> DnsHeader(int rcode, int questions, int answers, int additional = 0) =>
        [0, 7, 0x81, (byte)(0x80 | rcode), 0, (byte)questions, (byte)(answers >> 8), (byte)answers, 0, 0, 0, (byte)additional];

    private static void DnsName(List<byte> bytes, params string[] labels)
    {
        foreach (string label in labels)
        {
            bytes.Add((byte)label.Length);
            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(label));
        }

        bytes.Add(0);
    }

    /// <summary>P5: only a whole reverse name is an address.</summary>
    [Fact]
    public void P5_OnlyAWholeReverseName_IsAnAddress()
    {
        Assert.Null(DnsWire.ReverseAddress("in-addr.arpa"));
        Assert.Equal(IPAddress.Parse("192.168.0.0"), DnsWire.ReverseAddress("168.192.in-addr.arpa"));   // a zone stands for its network
        Assert.Equal(IPAddress.Parse("192.168.1.5"), DnsWire.ReverseAddress("5.1.168.192.in-addr.arpa"));
    }

    /// <summary>C12: a reply claiming 65,535 answers in 12 bytes is refused, not pre-sized.</summary>
    [Fact]
    public void C12_AHostileCount_IsRefused() =>
        Assert.Throws<DnsFormatException>(() => DnsWire.Parse(DnsHeader(0, 0, 0xFFFF).ToArray()));

    /// <summary>G11: a dot inside a label is escaped.</summary>
    [Fact]
    public void G11_ADotInsideALabel_IsEscaped()
    {
        var bytes = new List<byte>();
        DnsName(bytes, "login.bank.example", "attacker");
        int at = 0;
        Assert.Equal(@"login\.bank\.example.attacker", DnsWire.ReadName(bytes.ToArray(), ref at));
    }

    /// <summary>P18: a name of 256 octets with its root byte is refused; 255 fits.</summary>
    [Fact]
    public void P18_TheRootByteCounts()
    {
        var bytes = new List<byte>();
        DnsName(bytes, new string('a', 63), new string('b', 63), new string('c', 63), new string('d', 62));   // 64 × 3 + 63 + the root = 256
        int at = 0;
        Assert.Throws<DnsFormatException>(() => DnsWire.ReadName(bytes.ToArray(), ref at));
        var fits = new List<byte>();
        DnsName(fits, new string('a', 63), new string('b', 63), new string('c', 63), new string('d', 61));    // 255
        at = 0;
        Assert.Equal(253, DnsWire.ReadName(fits.ToArray(), ref at).Length);
    }

    /// <summary>C11: a name that runs past its RDATA is refused.</summary>
    [Fact]
    public void C11_ANamePastItsRdata_IsRefused()
    {
        var bytes = DnsHeader(0, 0, 1);
        DnsName(bytes, "x");
        bytes.AddRange([0, 2, 0, 1, 0, 0, 0, 60, 0, 1]);   // NS, IN, TTL 60, RDLENGTH 1
        DnsName(bytes, "target", "example");                  // a name longer than its RDLENGTH says
        Assert.Throws<DnsFormatException>(() => DnsWire.Parse(bytes.ToArray()));
    }

    /// <summary>P8: the OPT record's extended RCODE is read.</summary>
    [Fact]
    public void P8_TheExtendedRcode()
    {
        var bytes = DnsHeader(0, 0, 0, additional: 1);
        bytes.AddRange([0, 0, 41, 0x04, 0xD0, 1, 0, 0, 0, 0, 0]);   // OPT: the root, type 41, size 1232, TTL 0x01000000, RDLENGTH 0
        Assert.Equal(16, DnsWire.Parse(bytes.ToArray()).Rcode);
        Assert.Contains("BADVERS", NetworkText.Rcode(16));
    }

    /// <summary>H7: a server's refusal without a question still answers; the plain query has no OPT.</summary>
    [Fact]
    public void H7_ARefusalWithoutAQuestion_Answers()
    {
        Assert.True(DnsClient.Answers(DnsWire.Parse(DnsHeader(1, 0, 0).ToArray()), 7, "example.com", DnsWire.A));
        Assert.False(DnsClient.Answers(DnsWire.Parse(DnsHeader(0, 0, 0).ToArray()), 7, "example.com", DnsWire.A));
        Assert.Equal(DnsWire.Query(1, "example.com", DnsWire.A).Length - 11, DnsWire.Query(1, "example.com", DnsWire.A, edns: false).Length);
    }

    /// <summary>P12, P17: the LDAP preamble.</summary>
    [Fact]
    public void P12_P17_TheLdapPreamble()
    {
        Assert.Equal(TlsPreamble.LdapDisconnected, TlsPreamble.LdapResultCode([0x02, 0x01, 0x00, 0x78, 0x03, 0x0a, 0x01, 0x34]));
        Assert.Equal(-1, TlsPreamble.LdapResultCode([0x02, 0x01, 0x05, 0x78, 0x03, 0x0a, 0x01, 0x00]));
        Assert.Equal(StartTls.Ldap, TlsPreamble.Guess(3268));
    }

    /// <summary>K7, R17: the port list.</summary>
    [Fact]
    public async Task K7_R17_ThePortList()
    {
        Assert.NotNull(NetworkPorts.Parse(string.Join(',', Enumerable.Range(1, NetworkChecks.MaxPortsPerCall + 1))).Error);
        Assert.Equal([80, 443], NetworkPorts.Parse("80, 443, 80").Ports);
        var tool = new NetPortTool(Net(new AppSettingsData { NetworkToolsReach = "both" }), new NetworkBudget());
        Assert.Equal(NetworkText.Missing("ports"), (string)(await tool.InvokeAsync(new AIFunctionArguments { ["host"] = "public.example" }))!);
    }

    /// <summary>G14, S13, S19: the answers' words.</summary>
    [Fact]
    public void G14_S13_S19_TheWords()
    {
        var now = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        var soon = new CertificateView("s", "i", [], now.AddDays(3), now.AddDays(400), "AB", "1");
        Assert.Equal("not valid for 3 more days", NetworkRender.DaysLeft(soon, now));
        var hours = soon with { NotBefore = now.AddDays(-1), NotAfter = now.AddHours(5) };
        Assert.Equal("5 hours left", NetworkRender.DaysLeft(hours, now));
        Assert.Contains("time not measured", NetworkRender.Ping("h", IPAddress.Loopback, [new PingHop(PingStatus.Reply, IPAddress.Loopback, null)]));
        Assert.Contains("(the " + NetworkProbes.TraceBudget.TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " s budget)",
            NetworkRender.Trace("h", IPAddress.Loopback, 30, [new TraceHop(1, null, null, null, false, false)]));
    }

    /// <summary>H10: a SHA-1 thumbprint finds a certificate.</summary>
    [Fact]
    public void H10_ASha1Thumbprint_Finds()
    {
        var now = DateTimeOffset.UnixEpoch;
        var view = new CertificateView("CN=x", "CN=y", [], now, now.AddDays(1), "AAAA", "1", Sha1: "0123ABCD");
        Assert.Single(CertificateStores.Narrow([view], "0123abcd", null, now));
    }

    /// <summary>P14, C14: a quoted datagram must be ICMP, and a reply must carry our payload.</summary>
    [Fact]
    public void P14_C14_TheIcmpChecks()
    {
        var request = IcmpPacket.Echo(5, 9, [1, 2, 3, 4]);
        var exceeded = new byte[8 + 20 + 8];
        exceeded[0] = 11;          // Time Exceeded
        exceeded[8] = 0x45;        // the quoted IPv4 header
        exceeded[8 + 9] = 17;      // quoting a UDP datagram
        request.AsSpan(0, 8).CopyTo(exceeded.AsSpan(28));
        Assert.Equal(IcmpReplyKind.None, IcmpPacket.Parse(exceeded, 5, 9).Kind);
        exceeded[8 + 9] = 1;
        Assert.Equal(IcmpReplyKind.TimeExceeded, IcmpPacket.Parse(exceeded, 5, 9).Kind);
        var reply = (byte[])request.Clone();
        reply[0] = 0;
        Assert.True(IcmpPacket.SamePayload(reply, request));
        reply[^1] ^= 0xFF;
        Assert.False(IcmpPacket.SamePayload(reply, request));
    }

    /// <summary>R4: the rule names ping and trace only where they are offered.</summary>
    [Fact]
    public void R4_TheRule_NamesOnlyWhatIsOffered()
    {
        Assert.DoesNotContain(NetPingTool.ToolName, Assistant.AdminTail(AdminRules.Network, eventLog: false));
        Assert.Contains(NetPingTool.ToolName, Assistant.AdminTail(AdminRules.Network | AdminRules.NetworkEcho, eventLog: false));
    }

    /// <summary>I8, U17: an IPv6-only host where no IPv6 echo is sent says so, and nothing is sent.</summary>
    [Fact]
    public async Task I8_U17_AnIpv6OnlyHost_SaysSo()
    {
        _netNames["v6.example"] = [IPAddress.Parse("2606:4700:4700::1111")];
        var pinger = new FakePinger { SupportsIPv6 = false };
        var access = new NetworkAccess(() => new AppSettingsData { NetworkToolsReach = "both" }, pinger, _time,
            (host, _) => _netNames.TryGetValue(host, out var a) ? Task.FromResult(a) : throw new System.Net.Sockets.SocketException(11001));
        Assert.Equal(NetworkText.NoIpv6Host("v6.example"), (await NetworkChecks.PingAsync(access, new NetworkBudget(), "v6.example", 2, CancellationToken.None)).Text);
        Assert.Empty(pinger.Sent);
    }
}
