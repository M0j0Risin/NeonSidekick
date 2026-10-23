using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NeonSidekick.ActiveDirectory;
using NeonSidekick.EventLog;
using NeonSidekick.Network;
using NeonSidekick.Plans;
using NeonSidekick.Settings;
using NeonSidekick.Tasks;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The admin-tools review's fixes (2026-10-23, the fix plan at <c>~/.claude/plans/logical-singing-hamming.md</c>): each test takes a
/// finding's failure scenario as its input. One partial class over several files, by area; this file holds Phase 1's (crashes, hangs and
/// the worst holes) that have a portable side.
/// </summary>
public sealed partial class AdminFixesTests
{
    private readonly ManualTimeProvider _time = new();

    // ── Phase 1 ────────────────────────────────────────────────────────────

    /// <summary>A9: a DATE outside its range is "never", not an ArgumentException that failed the whole task_list.</summary>
    [Fact]
    public void A9_ADateOutOfRange_IsNever()
    {
        Assert.Null(TaskWords.Date(3_000_000.0));
        Assert.Null(TaskWords.Date(double.NaN));
        Assert.Null(TaskWords.Date(-700_000.0));
        Assert.Equal(new DateTime(2026, 10, 23), TaskWords.Date(new DateTime(2026, 10, 23).ToOADate()));
    }

    /// <summary>A8, P7: a moment past the calendar's end is null, and a FILETIME near the top reads without the local zone throwing.</summary>
    [Fact]
    public void A8_P7_TimeSumsAndFileTimes_NeverThrow()
    {
        var at = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Null(AdValues.Plus(at, TimeSpan.MaxValue));
        Assert.Equal(at.AddDays(1), AdValues.Plus(at, TimeSpan.FromDays(1)));
        Assert.NotNull(AdValues.FileTime(DateTime.MaxValue.ToFileTimeUtc() - 1));
        Assert.Equal(TimeSpan.Zero, AdValues.FileTime(DateTime.MaxValue.ToFileTimeUtc() - 1)!.Value.Offset);
    }

    /// <summary>P13: a selected protocol with the high bit set is a protocol, not a failure; a huge failure code no longer overflows.</summary>
    [Fact]
    public void P13_TheRdpNegotiation_ReadsUnsigned()
    {
        byte[] Confirm(byte type, uint value)
        {
            var bytes = new byte[15];
            bytes[0] = 14;
            bytes[1] = 0xD0;
            bytes[7] = type;
            BitConverter.GetBytes(value).CopyTo(bytes, 11);
            return bytes;
        }

        Assert.Equal(2, TlsPreamble.RdpNegotiation(Confirm(0x02, 0x80000002)));
        Assert.Equal(-0xFFFF, TlsPreamble.RdpNegotiation(Confirm(0x03, 0x80000000)));
        Assert.Equal(-5, TlsPreamble.RdpNegotiation(Confirm(0x03, 5)));
        Assert.Equal(0, TlsPreamble.RdpNegotiation(Confirm(0x02, 0)));
    }

    /// <summary>U6: a connect that throws before it starts (no IPv6 stack) is that address's answer, not net_port's crash.</summary>
    [Fact]
    public async Task U6_AConnectThatThrows_IsUnreachable()
    {
        var results = await NetworkProbes.PortsAsync([IPAddress.Parse("2001:db8::1"), IPAddress.Loopback], [443],
            (address, _, _) => address.AddressFamily == AddressFamily.InterNetworkV6 ? throw new SocketException((int)SocketError.AddressFamilyNotSupported) : Task.FromResult(PortState.Open),
            CancellationToken.None);
        Assert.Equal([PortState.Unreachable, PortState.Open], results.Select(r => r.State));
    }

    /// <summary>E5: a certificate whose alternative names are malformed is read, its names said to be unreadable.</summary>
    [Fact]
    public void E5_AMalformedNameList_IsSaid_NotThrown()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=bad.example", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509Extension("2.5.29.17", [0x30, 0x05, 0x82, 0x09, 0x61], critical: false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var view = CertificateView.Of(certificate);
        Assert.True(view.NamesUnreadable);
        Assert.Empty(view.Names);
    }

    /// <summary>U1: a denied host named as the server name is refused before anything is reached, and the user's own /net is told.</summary>
    [Fact]
    public async Task U1_TheServerName_IsJudgedLikeAHost()
    {
        var settings = new AppSettingsData { NetworkTools = true, NetworkToolsReach = "both", NetworkDeniedHosts = ["secret.example"] };
        bool connected = false;
        var access = new NetworkAccess(() => settings, new FakePinger(), _time,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("203.0.113.7") }),
            (_, _, _) => { connected = true; return Task.FromResult(PortState.Open); });
        var refused = await NetworkChecks.TlsAsync(access, new NetworkBudget(), "front.example", 443, "secret.example", null, CancellationToken.None);
        Assert.StartsWith("Error:", refused.Text);
        Assert.Contains("secret.example", refused.Text);
        Assert.False(connected);
        var odd = await NetworkChecks.TlsAsync(access, new NetworkBudget(), "front.example", 443, "10.0.0.1", null, CancellationToken.None);
        Assert.Equal(NetworkText.BadServerName("10.0.0.1"), odd.Text);
    }

    /// <summary>L3, E3: an unattended Ralph pass is offered none of the confirm-gated admin writes; a gap pass keeps them.</summary>
    [Fact]
    public void L3_E3_TheConfirmingWrites_LeaveUnattendedPasses()
    {
        var withheld = PlanTools.WithoutAskingOrConfirming(new HashSet<string>(StringComparer.Ordinal) { "x" });
        foreach (string tool in new[] { "x", "ask_user", "service_control", "service_start_type", "task_control" })
        {
            Assert.Contains(tool, withheld);
        }

        Assert.DoesNotContain("service_control", PlanTools.WithoutAsking(new HashSet<string>(StringComparer.Ordinal)));
    }

    /// <summary>D6: a reader that finds only oversized events hands back empty rounds, and the search stops at its scan limit counting them.</summary>
    [Fact]
    public void D6_OversizedRounds_CountTowardTheScanLimit()
    {
        using var reader = new EmptyRounds();
        var page = EventLogSearch.Search(reader, new EventLogQuery { Log = "System", Levels = EventLevels.Error }, new EventScan(false, 100, TimeSpan.FromSeconds(10), _time), CancellationToken.None);
        Assert.True(page.More);
        Assert.True(page.Skipped >= 100);
        Assert.True(reader.Rounds < 10);
    }

    /// <summary>O5: a header longer than the event's budget leaves no room for the body, never a negative one that threw.</summary>
    [Fact]
    public void O5_ALongHeader_LeavesTheBodyEmpty_NotAThrow()
    {
        var source = new MacLogSource("install", UnifiedLogTarget.Local, Redacted: false, MacLogKind.Install);
        var view = new MacFileView("i1", DateTimeOffset.UnixEpoch, null, new string('p', EventLogRender.EventMaxChars + 100), 1, "the message");
        string text = MacLogRender.InstallEvent(source, view, TimeZoneInfo.Utc);
        Assert.Contains("message:", text);
        Assert.DoesNotContain("the message", text);
    }

    /// <summary>Every round 64 events too large to read, for ever.</summary>
    private sealed class EmptyRounds : IEventLogReader
    {
        public int Rounds { get; private set; }

        public int Skipped => Rounds * 64;

        public IReadOnlyList<RawEvent>? Next()
        {
            Rounds++;
            return Rounds > 1000 ? null : [];
        }

        public void Dispose()
        {
        }
    }
}
