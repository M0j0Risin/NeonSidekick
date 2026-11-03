using System.Buffers.Binary;
using System.Net;
using System.Text;
using NeonSidekick.Network;
using Xunit;

namespace NeonSidekick.Tests;

/// <summary>
/// A Mac's system resolver (2026-11-03, a VPN domain's report): <see cref="DnsServiceReplies"/> reading <c>DNSServiceQueryRecord</c>'s
/// callbacks in the shapes a probe of mDNSResponder showed, <see cref="DnsWire.RecordData"/> on a record's RDATA alone, the answer's wording,
/// and on a Mac the real daemon.
/// </summary>
public sealed class DnsServiceTests
{
    private const uint More = DnsServiceReplies.MoreComing | DnsServiceReplies.AddFlag;
    private const uint Last = DnsServiceReplies.AddFlag;

    private static byte[] Name(string name)
    {
        var bytes = new List<byte>();
        foreach (string label in name.TrimEnd('.').Split('.'))
        {
            bytes.Add((byte)label.Length);
            bytes.AddRange(Encoding.ASCII.GetBytes(label));
        }

        bytes.Add(0);
        return [.. bytes];
    }

    private static byte[] Srv(ushort priority, ushort weight, ushort port, string target)
    {
        var head = new byte[6];
        BinaryPrimitives.WriteUInt16BigEndian(head, priority);
        BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(2), weight);
        BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(4), port);
        return [.. head, .. Name(target)];
    }

    [Fact]
    public void FiveSrvRecords_InOneBatch_AreTheAnswer()
    {
        const string asked = "_ldap._tcp.dc._msdcs.corp.example";
        var replies = new DnsServiceReplies(asked, DnsWire.Srv);
        for (int i = 1; i <= 5; i++)
        {
            Assert.False(replies.Done);
            replies.Add(i < 5 ? More : Last, DnsServiceReplies.NoError, asked + ".", DnsWire.Srv, Srv(0, 100, 389, "dc" + i + ".corp.example"), 600);
        }

        Assert.True(replies.Done);
        var outcome = replies.Outcome();
        Assert.Null(outcome.Error);
        Assert.Null(outcome.Server);
        Assert.Equal(NetworkText.SystemResolver, outcome.Resolver);
        Assert.Equal(0, outcome.Reply!.Rcode);
        Assert.Equal(["0 100 389 dc1.corp.example", "0 100 389 dc2.corp.example", "0 100 389 dc3.corp.example", "0 100 389 dc4.corp.example", "0 100 389 dc5.corp.example"],
            outcome.Reply.Answers.Select(a => a.Data));
        Assert.All(outcome.Reply.Answers, a => Assert.Equal(600u, a.Ttl));
        Assert.All(outcome.Reply.Answers, a => Assert.Equal(asked, a.Name));
    }

    [Fact]
    public void NoSuchRecord_IsAnEmptyAnswer_AndNoSuchName_IsNxdomain()
    {
        var missing = new DnsServiceReplies("nowhere.corp.example", DnsWire.A);
        missing.Add(Last, DnsServiceReplies.NoSuchRecord, "nowhere.corp.example.", DnsWire.A, [], 600);
        Assert.True(missing.Done);
        var empty = missing.Outcome().Reply!;
        Assert.Equal(0, empty.Rcode);
        Assert.Empty(empty.Answers);

        var nxdomain = new DnsServiceReplies("nowhere.corp.example", DnsWire.A);
        nxdomain.Add(Last, DnsServiceReplies.NoSuchName, "nowhere.corp.example.", DnsWire.A, [], 600);
        Assert.Equal(3, nxdomain.Outcome().Reply!.Rcode);
    }

    [Fact]
    public void ACnameAndItsTarget_InOneBatch_AreBothAnswers()
    {
        var replies = new DnsServiceReplies("www.corp.example", DnsWire.A);
        replies.Add(More, DnsServiceReplies.NoError, "www.corp.example.", DnsWire.Cname, Name("corp.example"), 3600);
        replies.Add(Last, DnsServiceReplies.NoError, "corp.example.", DnsWire.A, [192, 0, 2, 7], 60);
        Assert.True(replies.Done);
        var answers = replies.Outcome().Reply!.Answers;
        Assert.Equal([(DnsWire.Cname, "corp.example"), (DnsWire.A, "192.0.2.7")], answers.Select(a => (a.Type, a.Data)));
    }

    [Fact]
    public void ABatchOfCnamesAlone_WaitsForTheTarget_UnlessACnameWasAsked()
    {
        var replies = new DnsServiceReplies("www.corp.example", DnsWire.A);
        replies.Add(Last, DnsServiceReplies.NoError, "www.corp.example.", DnsWire.Cname, Name("corp.example"), 3600);
        Assert.False(replies.Done);
        Assert.Equal(NetworkText.DnsTookTooLong, replies.Outcome().Error);
        replies.Add(Last, DnsServiceReplies.NoSuchRecord, "corp.example.", DnsWire.A, [], 60);
        Assert.True(replies.Done);
        Assert.Equal([DnsWire.Cname], replies.Outcome().Reply!.Answers.Select(a => a.Type));

        var cname = new DnsServiceReplies("www.corp.example", DnsWire.Cname);
        cname.Add(Last, DnsServiceReplies.NoError, "www.corp.example.", DnsWire.Cname, Name("corp.example"), 3600);
        Assert.True(cname.Done);
    }

    [Fact]
    public void ARemoval_IsIgnored_AndAnUnreadableRecord_IsLeftOut()
    {
        var replies = new DnsServiceReplies("corp.example", DnsWire.A);
        replies.Add(DnsServiceReplies.MoreComing, DnsServiceReplies.NoError, "corp.example.", DnsWire.A, [192, 0, 2, 1], 60);
        replies.Add(More, DnsServiceReplies.NoError, "corp.example.", DnsWire.Mx, [0, 10, 0xC0, 0x0C], 60);
        replies.Add(Last, DnsServiceReplies.NoError, "corp.example.", DnsWire.A, [192, 0, 2, 2], 60);
        Assert.True(replies.Done);
        Assert.Equal(["192.0.2.2"], replies.Outcome().Reply!.Answers.Select(a => a.Data));
    }

    [Fact]
    public void ATimeoutAndAnotherError_AreWorded_AndNothingAfterTheEndCounts()
    {
        var timedOut = new DnsServiceReplies("corp.example", DnsWire.Srv);
        timedOut.Add(Last, DnsServiceReplies.Timeout, "corp.example.", DnsWire.Srv, [], 0);
        Assert.True(timedOut.Done);
        Assert.Equal(NetworkText.DnsTookTooLong, timedOut.Outcome().Error);

        var refused = new DnsServiceReplies("corp.example", DnsWire.Srv);
        refused.Add(Last, -65570, "corp.example.", DnsWire.Srv, [], 0);
        Assert.Equal("Error: the system resolver failed the lookup (DNS-SD error -65570).", refused.Outcome().Error);
        refused.Add(Last, DnsServiceReplies.NoError, "corp.example.", DnsWire.Srv, Srv(0, 0, 389, "dc1.corp.example"), 60);
        Assert.Null(refused.Outcome().Reply);
    }

    [Fact]
    public void RecordData_ReadsUncompressedRdata_AndRefusesAPointer()
    {
        Assert.Equal("10 20 636 dc1.corp.example", DnsWire.RecordData(Srv(10, 20, 636, "dc1.corp.example"), DnsWire.Srv));
        Assert.Equal("localhost", DnsWire.RecordData(Name("localhost"), DnsWire.Ptr).TrimEnd('.'));
        Assert.Equal("5 mail.corp.example", DnsWire.RecordData([0, 5, .. Name("mail.corp.example")], DnsWire.Mx).TrimEnd('.'));
        Assert.Equal("v=spf1 -all", DnsWire.RecordData([11, .. Encoding.ASCII.GetBytes("v=spf1 -all")], DnsWire.Txt));
        Assert.Throws<DnsFormatException>(() => DnsWire.RecordData([0xC0, 0x0C], DnsWire.Ptr));
    }

    [Fact]
    public void ALookupWithNoServer_SaysTheSystemResolverAnswered()
    {
        var reply = new DnsMessage(0, true, false, 0, "_ldap._tcp.corp.example", DnsWire.Srv, DnsWire.ClassIn,
            [new DnsRecord("_ldap._tcp.corp.example.", DnsWire.Srv, 600, "0 100 389 dc1.corp.example.")]);
        string text = NetworkRender.Lookup("_ldap._tcp.corp.example", DnsWire.Srv, new DnsOutcome(reply, null, null, Resolver: NetworkText.SystemResolver), systemServer: true);
        Assert.StartsWith("DNS _ldap._tcp.corp.example SRV via the system resolver: 1 record", text);
    }

    /// <summary>The real daemon: 127.0.0.1's reverse name, which mDNSResponder answers itself (no network).</summary>
    [MacFact]
    public async Task MacDnsService_LoopbackPtr_IsLocalhost()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var outcome = await MacDnsService.QueryAsync(DnsWire.ReverseName(IPAddress.Loopback), DnsWire.Ptr, CancellationToken.None);
        Assert.NotNull(outcome);
        Assert.Equal("localhost", outcome!.Reply!.Answers.First(a => a.Type == DnsWire.Ptr).Data.TrimEnd('.'));
        Assert.Equal(NetworkText.SystemResolver, outcome.Resolver);
    }

    /// <summary>A cancelled lookup ends the wait (Esc), and the daemon's reference is let go on its own thread.</summary>
    [MacFact]
    public async Task MacDnsService_ACancelledLookup_Throws()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var lookup = MacDnsService.QueryAsync("corp.example", DnsWire.Srv, cancelled.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup);
    }
}
