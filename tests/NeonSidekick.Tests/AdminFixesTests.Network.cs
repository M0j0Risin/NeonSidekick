using System.Net;
using NeonSidekick.Network;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Web;

namespace NeonSidekick.Tests;

/// <summary>Phase 9a of the admin review's fixes (2026-10-23): the network tools' reach, host lists and budget.</summary>
public sealed partial class AdminFixesTests
{
    private readonly Dictionary<string, IPAddress[]> _netNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["public.example"] = [IPAddress.Parse("93.184.215.14")],
        ["fs01.corp.example"] = [IPAddress.Parse("10.0.0.5")],
        ["corp.example"] = [IPAddress.Parse("10.0.0.1")],
        ["files.example"] = [IPAddress.Parse("93.184.215.20")],
    };

    private int _netLookups;

    private NetworkAccess Net(AppSettingsData settings) =>
        new(() => settings, null, _time, (host, _) =>
        {
            _netLookups++;
            return _netNames.TryGetValue(host, out var a) ? Task.FromResult(a) : throw new System.Net.Sockets.SocketException(11001);
        }, (_, _, _) => Task.FromResult(PortState.Open));

    /// <summary>B7, P4, U11, U13: the host lists read addresses and globs strictly.</summary>
    [Fact]
    public void B7_P4_U11_U13_TheListsReadStrictly()
    {
        var denied = HostRules.ReadList(["fe80::1"]);
        Assert.NotNull(HostRules.Match(denied, null, IPAddress.Parse("fe80::1%12")));
        Assert.Equal("", HostRules.NormalizeName("1234"));
        Assert.Equal("", HostRules.NormalizeName("10.1"));
        Assert.Equal("10.0.0.1", HostRules.NormalizeName("10.0.0.1"));
        Assert.NotNull(HostRules.Parse("*.1").Error);
        Assert.NotNull(HostRules.Parse("[fd00::]:80").Error);
        Assert.NotNull(HostRules.Parse("[fd00::]/8").Error);
        Assert.Null(HostRules.Parse("fd00::/8").Error);
    }

    /// <summary>H8: a denied glob denies its own domain, by name and by its addresses.</summary>
    [Fact]
    public async Task H8_ADeniedGlob_DeniesItsApex()
    {
        var access = Net(new AppSettingsData { NetworkToolsReach = "both", NetworkDeniedHosts = ["*.corp.example"] });
        Assert.StartsWith("Error: corp.example matches", (await access.TargetAsync("corp.example", new NetworkBudget(), [], CancellationToken.None)).Error);
        _netNames["dc.other.example"] = [IPAddress.Parse("10.0.0.1")];
        Assert.Contains("*.corp.example", (await access.TargetAsync("dc.other.example", new NetworkBudget(), [], CancellationToken.None)).Error);
    }

    /// <summary>U14: an allowed name's addresses are allowed under another name.</summary>
    [Fact]
    public async Task U14_AnAllowedNamesAddress_IsAllowed()
    {
        _netNames["alias.example"] = [IPAddress.Parse("93.184.215.20")];
        var access = Net(new AppSettingsData { NetworkToolsReach = "both", NetworkAllowedHosts = ["files.example"] });
        Assert.Null((await access.TargetAsync("alias.example", new NetworkBudget(), [], CancellationToken.None)).Error);
        Assert.Contains("is not in", (await access.TargetAsync("public.example", new NetworkBudget(), [], CancellationToken.None)).Error);
    }

    /// <summary>A10: a list's names are resolved once a minute, not per call.</summary>
    [Fact]
    public async Task A10_TheListsNames_AreResolvedOnce()
    {
        var access = Net(new AppSettingsData { NetworkToolsReach = "both", NetworkDeniedHosts = ["fs01.corp.example"] });
        await access.TargetAsync("public.example", new NetworkBudget(), [], CancellationToken.None);
        int after = _netLookups;
        await access.TargetAsync("public.example", new NetworkBudget(), [], CancellationToken.None);
        Assert.Equal(after + 1, _netLookups);   // the target alone
        _time.Advance(NetworkAccess.ListNamesTtl);
        await access.TargetAsync("public.example", new NetworkBudget(), [], CancellationToken.None);
        Assert.Equal(after + 3, _netLookups);
    }

    /// <summary>M7: a single-label name is refused under internet before it is resolved; U5: refusals spend the budget.</summary>
    [Fact]
    public async Task M7_U5_ALanSpelling_IsRefusedUnresolved_AndSpends()
    {
        var access = Net(new AppSettingsData { NetworkToolsReach = "internet" });
        var budget = new NetworkBudget();
        int before = _netLookups;
        var refused = await access.TargetAsync("fileserver", budget, [], CancellationToken.None);
        Assert.NotNull(refused.Error);
        Assert.Equal(before, _netLookups);
        Assert.Equal(1, budget.Hosts);
        await access.TargetAsync("nosuch.example", budget, [], CancellationToken.None);
        Assert.Equal(2, budget.Hosts);
        Assert.True(LanPolicy.SpelledLan("printer.local"));
        Assert.True(LanPolicy.SpelledLan("nas"));
        Assert.False(LanPolicy.SpelledLan("example.com"));
    }

    /// <summary>E1: a name whose answer moves to a new address counts again.</summary>
    [Fact]
    public void E1_ARotatingName_CountsEachNewAddress()
    {
        var budget = new NetworkBudget();
        Assert.Null(budget.Take("rot.example", [IPAddress.Parse("10.0.0.1")], [], 8, 64));
        Assert.Null(budget.Take("rot.example", [IPAddress.Parse("10.0.0.1")], [], 8, 64));
        Assert.Equal(1, budget.Hosts);
        Assert.Null(budget.Take("rot.example", [IPAddress.Parse("10.0.0.2")], [], 8, 64));
        Assert.Equal(2, budget.Hosts);
        Assert.Null(budget.Take("other.example", [IPAddress.Parse("10.0.0.2")], [], 8, 64));
        Assert.Equal(2, budget.Hosts);
    }

    /// <summary>DA8: a run's budget says so.</summary>
    [Fact]
    public void DA8_ARunsBudget_SaysSo()
    {
        var budget = new NetworkBudget();
        budget.Reset(run: true);
        Assert.Null(budget.Take("a.example", [IPAddress.Parse("10.0.0.1")], [], 1, 64));
        string spent = budget.Take("b.example", [IPAddress.Parse("10.0.0.2")], [], 1, 64)!;
        Assert.Contains("whole automation run", spent);
        Assert.True(NetworkText.IsBudgetSpent(spent));
    }

    /// <summary>C1: at most four addresses of one host are reached.</summary>
    [Fact]
    public async Task C1_AtMostFourAddresses()
    {
        _netNames["many.example"] = [.. Enumerable.Range(1, 9).Select(i => IPAddress.Parse("93.184.216." + i.ToString(System.Globalization.CultureInfo.InvariantCulture)))];
        var access = Net(new AppSettingsData { NetworkToolsReach = "both" });
        var target = (await access.TargetAsync("many.example", new NetworkBudget(), [80], CancellationToken.None)).Value!;
        Assert.Equal((NetworkAccess.MaxAddresses, 5), (target.Addresses.Count, target.LeftOut));
        var answer = await NetworkChecks.PortsAsync(access, new NetworkBudget(), "many.example", [80], CancellationToken.None);
        Assert.Contains(NetworkText.AddressesLeftOut(5), answer.Text);
    }

    /// <summary>J8, J13, U8, U3, Q10: the addresses and the reach.</summary>
    [Fact]
    public void J8_J13_U8_U3_TheAddresses()
    {
        Assert.Equal(IPAddress.Parse("10.0.0.5"), HostRules.Canonical(IPAddress.Parse("::ffff:10.0.0.5")));
        // Teredo: the client's address flipped in the last 32 bits; ISATAP after 0000:5efe.
        Assert.Equal(IPAddress.Parse("10.0.0.5"), LanPolicy.EmbeddedIPv4(IPAddress.Parse("2001:0:4136:e378:8000:63bf:f5ff:fffa")));
        Assert.True(LanPolicy.IsPrivate(IPAddress.Parse("fe80::5efe:192.168.1.10")));
        Assert.Equal(IPAddress.Parse("192.168.1.10"), LanPolicy.EmbeddedIPv4(IPAddress.Parse("2001:db9::5efe:192.168.1.10")));
        Assert.True(LanPolicy.IsPrivate(IPAddress.Parse("198.18.0.1")));
        Assert.True(LanPolicy.IsPrivate(IPAddress.Parse("192.0.0.8")));
        Assert.True(LanPolicy.IsPrivate(IPAddress.Parse("203.0.113.5")));
        var admission = LanPolicy.Admit("x.localhost", [IPAddress.Parse("93.184.215.14")], NetworkReach.LocalAreaNetwork);
        Assert.Equal(NetworkRefusal.Internet, admission.Refusal);
        var access = Net(new AppSettingsData { NetworkToolsReach = "both" });
        var aliases = new Dictionary<IPAddress, HostRule> { [IPAddress.Parse("10.0.0.5")] = HostRules.Parse("fs01.corp.example").Rule! };
        Assert.NotNull(access.Judge("nat64.example", [IPAddress.Parse("64:ff9b::a00:5")], aliases).Refusal);
    }

    /// <summary>DA3: an on-link prefix and this machine's own address are the LAN.</summary>
    [Fact]
    public void DA3_OnLinkPrefixes_AreTheLan()
    {
        using var scope = OnLinkNetworks.Use(OnLinkNetworks.Usable([new OnLinkPrefix(IPAddress.Parse("2a01:db8:1:2::10"), 64), new OnLinkPrefix(IPAddress.Parse("93.184.215.1"), 8)]));
        Assert.True(LanPolicy.IsLan(IPAddress.Parse("2a01:db8:1:2::99")));
        Assert.False(LanPolicy.IsLan(IPAddress.Parse("2a01:db8:1:3::99")));
        Assert.True(LanPolicy.IsLan(IPAddress.Parse("93.184.215.1")));    // this machine's own
        Assert.False(LanPolicy.IsLan(IPAddress.Parse("93.184.215.2")));   // a /8 is too short to count
        Assert.Equal(NetworkRefusal.Lan, LanPolicy.Admit("neighbor.example", [IPAddress.Parse("2a01:db8:1:2::99")], NetworkReach.Internet).Refusal);
    }

    /// <summary>U7, A1, B9: a lookup's family and answers.</summary>
    [Fact]
    public void A1_B9_TheAnswersTargets()
    {
        Assert.Equal("dc1.corp.local", NetworkChecks.TargetName(new DnsRecord("_ldap._tcp", DnsWire.Srv, 60, "0 100 389 dc1.corp.local.")));
        Assert.Equal("mail.example", NetworkChecks.TargetName(new DnsRecord("example", DnsWire.Mx, 60, "10 mail.example.")));
        Assert.Equal("ns1.example", NetworkChecks.TargetName(new DnsRecord("example", DnsWire.Soa, 60, "ns1.example. hostmaster.example. 1 2 3 4 5")));
        Assert.Null(NetworkChecks.TargetName(new DnsRecord("example", DnsWire.Txt, 60, "v=spf1")));
    }
}
