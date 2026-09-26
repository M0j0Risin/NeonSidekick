using System.Net;
using NeonSidekick.Llm;

namespace NeonSidekick.Tests;

public class LanHostsTests
{
    private static readonly IPAddress Address = IPAddress.Parse("192.168.1.37");
    private static readonly IPAddress Slash24 = IPAddress.Parse("255.255.255.0");

    [Fact]
    public void Expand_Slash24_IsEveryOtherHost_NoNetworkOrBroadcast_Ascending()
    {
        var hosts = LanHosts.Expand(Address, Slash24);

        Assert.Equal(253, hosts.Count);
        Assert.Equal("192.168.1.1", hosts[0].ToString());
        Assert.Equal("192.168.1.254", hosts[^1].ToString());
        Assert.DoesNotContain(IPAddress.Parse("192.168.1.0"), hosts);
        Assert.DoesNotContain(IPAddress.Parse("192.168.1.255"), hosts);
        Assert.DoesNotContain(Address, hosts);
        Assert.Equal(hosts.OrderBy(h => BitConverter.ToUInt32(h.GetAddressBytes().Reverse().ToArray(), 0)), hosts);
    }

    [Theory]
    [InlineData("255.255.0.0")]
    [InlineData("255.0.0.0")]
    [InlineData("0.0.0.0")]
    public void Expand_AWiderMask_IsClampedToTheSlash24AroundTheAddress(string mask)
    {
        Assert.Equal(LanHosts.Expand(Address, Slash24), LanHosts.Expand(Address, IPAddress.Parse(mask)));
        Assert.Equal(24, LanHosts.MaxPrefix);
    }

    [Fact]
    public void Expand_ANarrowerMask_StaysNarrow()
    {
        // /30: .36 network, .37 (us) and .38 hosts, .39 broadcast.
        var hosts = LanHosts.Expand(Address, IPAddress.Parse("255.255.255.252"));
        Assert.Equal(new[] { "192.168.1.38" }, hosts.Select(h => h.ToString()));

        // /32: nobody else.
        Assert.Empty(LanHosts.Expand(Address, IPAddress.Parse("255.255.255.255")));
    }

    [Fact]
    public void Expand_NotIPv4_OrNotAMask_IsEmpty()
    {
        Assert.Empty(LanHosts.Expand(IPAddress.Parse("fe80::1"), Slash24));
        Assert.Empty(LanHosts.Expand(Address, IPAddress.Parse("255.255.0.255")));   // not contiguous
    }

    [Fact]
    public void Describe_IsTheClampedSubnet()
    {
        Assert.Equal("192.168.1.0/24", LanHosts.Describe(Address, Slash24));
        Assert.Equal("192.168.1.0/24", LanHosts.Describe(Address, IPAddress.Parse("255.255.0.0")));
        Assert.Equal("192.168.1.36/30", LanHosts.Describe(Address, IPAddress.Parse("255.255.255.252")));
        Assert.Equal("10.0.0.0/24", LanHosts.Describe(IPAddress.Parse("10.0.0.5"), IPAddress.Parse("255.255.255.0")));
        Assert.Equal("", LanHosts.Describe(IPAddress.Parse("fe80::1"), Slash24));
    }

    [Fact]
    public void None_HasNothingToProbe()
    {
        Assert.Empty(LanHosts.None.Hosts);
        Assert.Empty(LanHosts.None.Subnets);
    }

    [Fact]
    public void From_LeavesOutAnAddressADownAdapterHolds_InsideAScannedSubnet()
    {
        // 2026-09-26: Ethernet up at .217, Wi-Fi down but still holding .103 in the same /24.
        var lan = LanHosts.From(
        [
            new LanHosts.Adapter(Scanned: true, [(IPAddress.Parse("192.168.1.217"), Slash24)]),
            new LanHosts.Adapter(Scanned: false, [(IPAddress.Parse("192.168.1.103"), Slash24)]),
        ]);

        Assert.Equal(["192.168.1.0/24"], lan.Subnets);
        Assert.Equal(252, lan.Hosts.Count);
        Assert.DoesNotContain(IPAddress.Parse("192.168.1.103"), lan.Hosts);
        Assert.DoesNotContain(IPAddress.Parse("192.168.1.217"), lan.Hosts);
    }

    [Fact]
    public void From_ScansOnlyScannedAdapters_OutsideLinkLocal_EachSubnetOnce()
    {
        var lan = LanHosts.From(
        [
            new LanHosts.Adapter(Scanned: false, [(IPAddress.Parse("10.0.0.5"), Slash24)]),
            new LanHosts.Adapter(Scanned: true, [(IPAddress.Parse("169.254.3.4"), IPAddress.Parse("255.255.0.0")), (IPAddress.Parse("fe80::1"), IPAddress.Any)]),
            new LanHosts.Adapter(Scanned: true, [(Address, Slash24), (IPAddress.Parse("192.168.1.40"), Slash24)]),
        ]);

        Assert.Equal(["192.168.1.0/24"], lan.Subnets);
        Assert.Equal(252, lan.Hosts.Count);   // 254 minus .37 and .40, both this machine's
        Assert.Equal(lan.Hosts.Count, lan.Hosts.Distinct().Count());
    }

    [Fact]
    public void Discover_NeverThrows_AndNeverListsThisMachine()
    {
        // The real adapters: whatever they hold, no own address and no loopback / link-local host is in the list.
        var lan = LanHosts.Discover();
        Assert.NotNull(lan);
        var own = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(u => u.Address)).ToHashSet();
        Assert.DoesNotContain(lan.Hosts, h => own.Contains(h) || IPAddress.IsLoopback(h) || h.GetAddressBytes() is [169, 254, ..]);
        Assert.Equal(lan.Hosts.Count, lan.Hosts.Distinct().Count());
        Assert.All(lan.Subnets, s => Assert.Matches(@"^\d+\.\d+\.\d+\.\d+/\d+$", s));
    }
}
