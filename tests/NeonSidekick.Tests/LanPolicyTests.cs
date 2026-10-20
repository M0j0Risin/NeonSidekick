using System.Net;
using NeonSidekick.Web;

namespace NeonSidekick.Tests;

public class LanPolicyTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.42.0.9")]
    [InlineData("10.0.0.1")]
    [InlineData("10.255.255.254")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.1")]
    [InlineData("192.168.1.229")]
    [InlineData("169.254.10.10")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.255")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("fe80::1")]
    [InlineData("ff02::1")]
    [InlineData("::ffff:192.168.0.1")]
    [InlineData("::ffff:10.0.0.7")]
    [InlineData("64:ff9b::c0a8:101")]
    [InlineData("64:ff9b::10.0.0.1")]
    [InlineData("64:ff9b:1::5")]
    [InlineData("2002:c0a8:101::1")]
    [InlineData("::192.168.1.1")]
    [InlineData("::0.0.0.2")]
    [InlineData("::ffff:0:192.168.1.1")]
    [InlineData("fec0::1")]
    public void Private_IsRefused(string address)
    {
        Assert.True(LanPolicy.IsPrivate(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("93.184.216.34")]
    [InlineData("172.15.0.1")]
    [InlineData("172.32.0.1")]
    [InlineData("192.169.0.1")]
    [InlineData("100.63.255.255")]
    [InlineData("100.128.0.1")]
    [InlineData("11.0.0.1")]
    [InlineData("2606:2800:220:1:248:1893:25c8:1946")]
    [InlineData("2001:db8::1")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("64:ff9b::8.8.8.8")]
    [InlineData("2002:808:808::1")]
    public void Public_IsAllowed(string address)
    {
        Assert.False(LanPolicy.IsPrivate(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("LOCALHOST.")]
    [InlineData("app.localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("[::1]")]
    [InlineData("192.168.1.229")]
    public void PrivateHost_ByName_OrLiteral(string host)
    {
        Assert.True(LanPolicy.IsPrivateHost(host));
    }

    [Theory]
    [InlineData("example.com")]
    [InlineData("localhost.example.com")]
    [InlineData("8.8.8.8")]
    [InlineData("[2001:db8::1]")]
    public void OtherHosts_AreNotJudgedByName(string host)
    {
        Assert.False(LanPolicy.IsPrivateHost(host));
    }

    [Fact]
    public void AnyPrivate_IsTheWorstAddress()
    {
        Assert.True(LanPolicy.AnyPrivate([IPAddress.Parse("8.8.8.8"), IPAddress.Parse("10.0.0.1")]));
        Assert.False(LanPolicy.AnyPrivate([IPAddress.Parse("8.8.8.8"), IPAddress.Parse("1.1.1.1")]));
        Assert.False(LanPolicy.AnyPrivate([]));
    }

    [Fact]
    public void Reach_IsReadLive()
    {
        var reach = NetworkReach.Internet;
        var policy = new LanPolicy(() => reach);

        Assert.Equal(NetworkReach.Internet, policy.Reach);
        reach = NetworkReach.LocalAreaNetwork;
        Assert.Equal(NetworkReach.LocalAreaNetwork, policy.Reach);
    }

    [Theory]
    [InlineData(NetworkReach.Internet, true, NetworkRefusal.Lan)]
    [InlineData(NetworkReach.Internet, false, NetworkRefusal.None)]
    [InlineData(NetworkReach.LocalAreaNetwork, true, NetworkRefusal.None)]
    [InlineData(NetworkReach.LocalAreaNetwork, false, NetworkRefusal.Internet)]
    [InlineData(NetworkReach.Both, true, NetworkRefusal.None)]
    [InlineData(NetworkReach.Both, false, NetworkRefusal.None)]
    public void Judge_InternetRefusesTheLan_LanRefusesTheInternet_BothRefusesNothing(NetworkReach reach, bool lan, NetworkRefusal expected)
    {
        Assert.Equal(expected, LanPolicy.Judge(reach, lan));
    }

    [Fact]
    public void Refusal_IsAnHttpRequestException_NamingTheHost_AndTheReason()
    {
        var lan = new NetworkRefusedException("router.lan", NetworkRefusal.Lan);
        Assert.Equal("router.lan", lan.Host);
        Assert.Equal(NetworkRefusal.Lan, lan.Reason);
        Assert.Equal(FetchOutcome.LanRefused, lan.Outcome);
        Assert.Equal(WebText.LanRefused("router.lan"), lan.Message);
        Assert.IsAssignableFrom<HttpRequestException>(lan);

        var internet = new NetworkRefusedException("example.com", NetworkRefusal.Internet);
        Assert.Equal(NetworkRefusal.Internet, internet.Reason);
        Assert.Equal(FetchOutcome.InternetRefused, internet.Outcome);
        Assert.Equal(WebText.InternetRefused("example.com"), internet.Message);
    }
}
