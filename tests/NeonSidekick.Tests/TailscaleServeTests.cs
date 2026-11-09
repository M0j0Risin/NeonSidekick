using System.IO.Pipes;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using System.Text;
using NeonSidekick.Diagnostics;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>/webui</c>'s HTTPS address (2026-10-31, plan in-the-past-we-ve-iterative-seahorse): Tailscale Serve's rules read (<see cref="TailscaleServe.Match"/>),
/// the adapter gate, the detector's log lines, the pipe client against an in-process pipe, the setting's words, the https links, and who a
/// request came from through a proxy (<see cref="WebUiOrigin.Of"/>).
/// </summary>
public sealed class TailscaleServeTests
{
    /// <summary>The spike's body on this PC: 443 → 8765, 8766 → 8766.</summary>
    private const string Spike = """
        {"TCP":{"443":{"HTTPS":true},"8766":{"HTTPS":true}},
         "Web":{"hoopdiethink.tail9511ae.ts.net:443":{"Handlers":{"/":{"Proxy":"http://127.0.0.1:8765"}}},
                "hoopdiethink.tail9511ae.ts.net:8766":{"Handlers":{"/":{"Proxy":"http://127.0.0.1:8766"}}}}}
        """;

    // ── Match ───────────────────────────────────────────────────────────────

    [Fact]
    public void TheSpikesRules_GiveEachPortItsAddress_AndNoneForAnother()
    {
        Assert.Equal(new TailscaleServeResult("https://hoopdiethink.tail9511ae.ts.net", false, []), TailscaleServe.Match(Spike, 8765), ResultComparer.Instance);
        Assert.Equal("https://hoopdiethink.tail9511ae.ts.net:8766", TailscaleServe.Match(Spike, 8766)!.Address);
        var none = TailscaleServe.Match(Spike, 9000)!;
        Assert.Null(none.Address);
        Assert.Equal([8765, 8766], none.OtherPorts);
    }

    [Fact]
    public void ARuleForAnotherPortAlone_NamesThatPort()
    {
        const string only8765 = """{"TCP":{"443":{"HTTPS":true}},"Web":{"h.ts.net:443":{"Handlers":{"/":{"Proxy":"http://127.0.0.1:8765"}}}}}""";
        var result = TailscaleServe.Match(only8765, 8766)!;
        Assert.Null(result.Address);
        Assert.Equal([8765], result.OtherPorts);
        Assert.Empty(TailscaleServe.Match(Spike, 8765)!.OtherPorts);   // both rules: no hint
    }

    [Theory]
    [InlineData("http://localhost:8765")]
    [InlineData("http://[::1]:8765")]
    [InlineData("http://127.0.0.1:8765/")]
    public void LocalhostAndIpv6Proxies_Count(string proxy)
    {
        string json = """{"TCP":{"443":{"HTTPS":true}},"Web":{"h.ts.net:443":{"Handlers":{"/":{"Proxy":"PROXY"}}}}}""".Replace("PROXY", proxy, StringComparison.Ordinal);
        Assert.Equal("https://h.ts.net", TailscaleServe.Match(json, 8765)!.Address);
    }

    [Theory]
    [InlineData("""{"TCP":{"443":{"HTTPS":true}},"Web":{"h.ts.net:443":{"Handlers":{"/app":{"Proxy":"http://127.0.0.1:8765"}}}}}""")]   // a sub-path
    [InlineData("""{"TCP":{"443":{"HTTP":true}},"Web":{"h.ts.net:443":{"Handlers":{"/":{"Proxy":"http://127.0.0.1:8765"}}}}}""")]   // --http
    [InlineData("""{"TCP":{"443":{"HTTPS":true}},"Web":{"h.ts.net:443":{"Handlers":{"/":{"Proxy":"http://192.168.1.5:8765"}}}}}""")]   // not this PC
    [InlineData("""{"TCP":{"443":{"HTTPS":true}},"Web":{"h.ts.net:443":{"Handlers":{"/":{"Path":"C:\\site"}}}}}""")]   // files, not a proxy
    [InlineData("""{"Services":{"svc:web":{"TCP":{"443":{"HTTPS":true}},"Web":{"web.ts.net:443":{"Handlers":{"/":{"Proxy":"http://127.0.0.1:8765"}}}}}}}""")]
    public void RulesThatDontServeThePageAtTheRoot_OverHttps_GiveNoAddress(string json)
    {
        Assert.Null(TailscaleServe.Match(json, 8765)!.Address);
    }

    [Fact]
    public void Port443_WinsOverAnother_ThenTheLowestPort()
    {
        const string both = """
            {"TCP":{"443":{"HTTPS":true},"9443":{"HTTPS":true},"8443":{"HTTPS":true}},
             "Web":{"h.ts.net:9443":{"Handlers":{"/":{"Proxy":"http://127.0.0.1:8765"}}},
                    "h.ts.net:8443":{"Handlers":{"/":{"Proxy":"http://127.0.0.1:8765"}}},
                    "h.ts.net:443":{"Handlers":{"/":{"Proxy":"http://127.0.0.1:8765"}}}}}
            """;
        Assert.Equal("https://h.ts.net", TailscaleServe.Match(both, 8765)!.Address);
        Assert.Equal("https://h.ts.net:8443", TailscaleServe.Match(both.Replace("\"443\":{\"HTTPS\":true},", "", StringComparison.Ordinal), 8765)!.Address);
    }

    [Fact]
    public void AForegroundRule_Matches_AndAFunnelKey_IsPublic()
    {
        const string foreground = """{"Foreground":{"s1":{"TCP":{"443":{"HTTPS":true}},"Web":{"h.ts.net:443":{"Handlers":{"/":{"Proxy":"http://127.0.0.1:8765"}}}}}}}""";
        var found = TailscaleServe.Match(foreground, 8765)!;
        Assert.Equal("https://h.ts.net", found.Address);
        Assert.False(found.Public);

        const string funnel = """
            {"TCP":{"443":{"HTTPS":true},"8766":{"HTTPS":true}},
             "Web":{"hoopdiethink.tail9511ae.ts.net:443":{"Handlers":{"/":{"Proxy":"http://127.0.0.1:8765"}}},
                    "hoopdiethink.tail9511ae.ts.net:8766":{"Handlers":{"/":{"Proxy":"http://127.0.0.1:8766"}}}},
             "AllowFunnel":{"hoopdiethink.tail9511ae.ts.net:443":true}}
            """;
        Assert.True(TailscaleServe.Match(funnel, 8765)!.Public);
        Assert.False(TailscaleServe.Match(funnel, 8766)!.Public);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    public void MalformedBodies_AreNull(string json) => Assert.Null(TailscaleServe.Match(json, 8765));

    [Fact]
    public void NoRulesAtAll_IsAnEmptyAnswer()
    {
        Assert.Equal(new TailscaleServeResult(null, false, []), TailscaleServe.Match("{}", 8765), ResultComparer.Instance);
        Assert.Equal(new TailscaleServeResult(null, false, []), TailscaleServe.Match("null", 8765), ResultComparer.Instance);
        Assert.False(TailscaleServe.Match("{}", 8765)!.InUse);   // nothing to hint at
    }

    /// <summary>2026-11-01, the review's P1-1: Funnel on any rule to the port warns, though the address shown is another rule's.</summary>
    [Fact]
    public void FunnelOnASecondRule_ToThePort_IsPublic()
    {
        const string two = """
            {"TCP":{"443":{"HTTPS":true},"8443":{"HTTPS":true}},
             "Web":{"h.ts.net:443":{"Handlers":{"/":{"Proxy":"http://127.0.0.1:8765"}}},
                    "h.ts.net:8443":{"Handlers":{"/":{"Proxy":"http://127.0.0.1:8765"}}}},
             "AllowFunnel":{"h.ts.net:8443":true}}
            """;
        var found = TailscaleServe.Match(two, 8765)!;
        Assert.Equal("https://h.ts.net", found.Address);
        Assert.True(found.Public);
    }

    /// <summary>2026-11-01, the review's P7-4: a rule to the port below the root gives no address, but its path for the hint.</summary>
    [Fact]
    public void ASubPathRule_IsReported()
    {
        const string sub = """{"TCP":{"443":{"HTTPS":true}},"Web":{"h.ts.net:443":{"Handlers":{"/neon":{"Proxy":"http://127.0.0.1:8765"}}}}}""";
        var found = TailscaleServe.Match(sub, 8765)!;
        Assert.Null(found.Address);
        Assert.Equal("/neon", found.SubPath);
        Assert.True(found.InUse);
        Assert.Equal(8443, found.FreeServePort);   // 443 is taken by the sub-path's rule
        Assert.Equal("Tailscale Serve forwards /neon to port 8765, but the Web UI lives at /: tailscale serve --bg --https=8443 8765 adds a rule at /.",
            WebUiText.ServeSubPathHint("/neon", 8765, 8443));
    }

    /// <summary>2026-11-01, the review's P8-2: only 127.0.0.1, ::1 and localhost are this PC's Web UI (ListenLocalhost binds just those).</summary>
    [Theory]
    [InlineData("http://127.0.0.2:8765")]
    [InlineData("http://127.1.2.3:8765")]
    public void OtherLoopbackAddresses_DontCount(string proxy)
    {
        string json = """{"TCP":{"443":{"HTTPS":true}},"Web":{"h.ts.net:443":{"Handlers":{"/":{"Proxy":"PROXY"}}}}}""".Replace("PROXY", proxy, StringComparison.Ordinal);
        Assert.Null(TailscaleServe.Match(json, 8765)!.Address);
    }

    /// <summary>2026-11-01, the review's P3-2: the suggested serve port is free in Serve and never the Web UI's own; none when all three are out.</summary>
    [Fact]
    public void TheSuggestedServePort_SkipsTakenPorts_AndTheWebUisOwn()
    {
        Assert.Equal(8443, TailscaleServe.Match(Spike, 9000)!.FreeServePort);   // the spike takes 443 and 8766
        const string taken443 = """{"TCP":{"443":{"HTTPS":true}},"Web":{"h.ts.net:443":{"Handlers":{"/":{"Proxy":"http://127.0.0.1:9000"}}}}}""";
        Assert.Equal(8443, TailscaleServe.Match(taken443, 8765)!.FreeServePort);
        Assert.Equal(10000, TailscaleServe.Match(taken443, 8443)!.FreeServePort);   // the Web UI's own port is never suggested
        const string all = """{"TCP":{"443":{"HTTPS":true},"8443":{"HTTPS":true}},"Foreground":{"s":{"TCP":{"10000":{"HTTPS":true}}}}}""";
        Assert.Null(TailscaleServe.Match(all, 8765)!.FreeServePort);   // a Foreground rule's port is taken too
        Assert.Equal("Tailscale Serve has no rule for port 8765.", WebUiText.ServeNoRuleHint(8765, null));
        Assert.Equal("Tailscale Serve has no rule for port 8765: tailscale serve --bg 8765 adds one.", WebUiText.ServeNoRuleHint(8765, 443));
        Assert.Equal("Tailscale Serve has no rule for port 8443: tailscale serve --bg --https=10000 8443 adds one.", WebUiText.ServeNoRuleHint(8443, 10000));
    }

    // ── The gate and the detector ───────────────────────────────────────────

    private static WebUiAdapter Adapter(string names, string address) =>
        new(NetworkInterfaceType.Unknown, [IPAddress.Parse(address)], false, 0, names);

    [Fact]
    public void TailscaleIsUp_OnlyWithItsOwnAdapter_HoldingATailnetAddress()
    {
        Assert.True(TailscaleServe.TailscaleUp([Adapter("Wi-Fi Intel", "192.168.1.217"), Adapter("Tailscale Tailscale Tunnel", "100.78.20.125")]));
        Assert.False(TailscaleServe.TailscaleUp([Adapter("Wi-Fi Intel", "192.168.1.217"), Adapter("NordLynx NordLynx Tunnel", "100.64.0.2")]));
        Assert.False(TailscaleServe.TailscaleUp([Adapter("Tailscale Tailscale Tunnel", "169.254.83.107")]));   // up, but no tailnet address
        Assert.False(TailscaleServe.TailscaleUp([]));
    }

    /// <summary>2026-11-01, the review's P7-2: Tailscale's IPv6 range opens the gate; the tabs stay IPv4 (no ULA or tailnet IPv6 tab).</summary>
    [Fact]
    public void TailscalesIpv6Range_OpensTheGate_ButTheTabsStayIpv4()
    {
        Assert.True(TailscaleServe.TailscaleUp([Adapter("Tailscale Tailscale Tunnel", "fd7a:115c:a1e0::1")]));
        Assert.False(TailscaleServe.TailscaleUp([Adapter("Tailscale Tailscale Tunnel", "fd00::1")]));
        Assert.True(WebUiController.IsTailnet(IPAddress.Parse("fd7a:115c:a1e0:ab12::5")));
        Assert.True(WebUiController.IsTailnet(IPAddress.Parse("::ffff:100.64.1.2")));
        Assert.False(WebUiController.IsTailnet(IPAddress.Parse("fd7a:115c:a1e1::1")));

        WebUiAdapter[] both =
        [
            new(NetworkInterfaceType.Unknown, [IPAddress.Parse("100.78.20.125"), IPAddress.Parse("fd7a:115c:a1e0::1")], false, 0, "Tailscale Tailscale Tunnel"),
            new(NetworkInterfaceType.Unknown, [IPAddress.Parse("192.168.1.217"), IPAddress.Parse("fd00::5")], false, 0, "Wi-Fi Intel"),
        ];
        Assert.Equal(["100.78.20.125", "192.168.1.217"], WebUiController.ChooseAddresses(both).Select(a => a.Host));
    }

    /// <summary>The detector: the gate, the 5 s reuse by the clock's timestamps, and one log line per change of answer (2026-11-01, the review's P2-5, P5-2, P9-3).</summary>
    [Fact]
    public async Task TheDetector_AsksOnlyWhileTailscaleIsUp_KeepsAnAnswer5s_AndLogsEachChangeOnce()
    {
        var adapters = new List<WebUiAdapter>();
        int asked = 0;
        var time = new ManualTimeProvider();
        var answer = new TailscaleServeResult("https://h.ts.net", false, []);
        var detector = new TailscaleServeDetector(() => adapters, (_, _) => { asked++; return Task.FromResult(answer); }, time);
        var lines = new List<string>();
        void Record(DiagnosticEvent e) => lines.Add(e.Message);
        DiagnosticLog.Emitted += Record;
        try
        {
            Assert.Null(await detector.FindAsync(8765, CancellationToken.None));
            Assert.Null(await detector.FindAsync(8765, CancellationToken.None));
            Assert.Equal(0, asked);
            Assert.Equal([WebUiText.ServeNoAdapterLogLine], lines);

            adapters.Add(Adapter("Tailscale", "100.78.20.125"));
            Assert.Equal("https://h.ts.net", (await detector.FindAsync(8765, CancellationToken.None))!.Address);
            Assert.Equal("https://h.ts.net", (await detector.FindAsync(8765, CancellationToken.None))!.Address);
            Assert.Equal(1, asked);   // kept 5 s
            Assert.Equal(2, lines.Count);
            Assert.Equal(WebUiText.HttpsAddressLogLine("https://h.ts.net", false), lines[^1]);

            time.Advance(TailscaleServeDetector.KeepFor + TimeSpan.FromSeconds(1));
            await detector.FindAsync(8765, CancellationToken.None);
            Assert.Equal(2, asked);
            Assert.Equal(2, lines.Count);   // the same answer: not written again

            await detector.FindAsync(8766, CancellationToken.None);
            Assert.Equal(3, asked);   // another port replaces the slot

            answer = new TailscaleServeResult(null, false, [8766]);
            time.Advance(TailscaleServeDetector.KeepFor + TimeSpan.FromSeconds(1));
            await detector.FindAsync(8765, CancellationToken.None);
            Assert.Equal("Tailscale Serve: no rule for port 8765 (rules for 8766).", lines[^1]);
            Assert.DoesNotContain(lines, l => l.Contains("?t=", StringComparison.Ordinal));
        }
        finally
        {
            DiagnosticLog.Emitted -= Record;
        }
    }

    /// <summary>2026-11-01, the review's P4-4, P9-3, P2-1: a failure is never kept, its line is written once, and a throwing ask is a failure.</summary>
    [Fact]
    public async Task TheDetector_KeepsNoFailure_LogsItOnce_AndTurnsAThrowIntoOne()
    {
        int asked = 0;
        bool fail = true;
        var detector = new TailscaleServeDetector(() => [Adapter("Tailscale", "100.78.20.125")], (_, _) =>
        {
            asked++;
            return fail ? throw new InvalidOperationException("odd") : Task.FromResult(new TailscaleServeResult("https://h.ts.net", false, []));
        }, new ManualTimeProvider());
        var lines = new List<string>();
        void Record(DiagnosticEvent e) => lines.Add(e.Message);
        DiagnosticLog.Emitted += Record;
        try
        {
            var first = await detector.FindAsync(8765, CancellationToken.None);
            var second = await detector.FindAsync(8765, CancellationToken.None);
            Assert.Equal(2, asked);   // not kept
            Assert.Equal("an error (InvalidOperationException)", first!.Failure);
            Assert.Equal(first.Failure, second!.Failure);
            Assert.Equal([WebUiText.ServeAskFailedLogLine("an error (InvalidOperationException)")], lines);

            fail = false;
            Assert.Equal("https://h.ts.net", (await detector.FindAsync(8765, CancellationToken.None))!.Address);
            Assert.Equal(2, lines.Count);
        }
        finally
        {
            DiagnosticLog.Emitted -= Record;
        }
    }

    /// <summary>
    /// The one throw the ask's catch lets through faults the waiters' task instead of leaving it pending, and the next ask for that port runs
    /// (2026-11-02, review P13-5: every later ask waited on the stuck task, and the HTTPS tab never answered).
    /// </summary>
    [Fact]
    public async Task AnAskThatThrowsPastItsCatch_EndsTheWaiters_AndTheNextAskRuns()
    {
        bool fail = true;
        var detector = new TailscaleServeDetector(() => [Adapter("Tailscale", "100.78.20.125")], (_, _) =>
            fail ? throw new OutOfMemoryException("test") : Task.FromResult(new TailscaleServeResult("https://h.ts.net", false, [])), new ManualTimeProvider());

        await Assert.ThrowsAsync<OutOfMemoryException>(() => detector.FindAsync(8765, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
        fail = false;
        Assert.Equal("https://h.ts.net", (await detector.FindAsync(8765, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)))!.Address);
    }

    /// <summary>2026-11-01, the review's P1-16: one ask in flight is shared, and one caller's cancel leaves the other's answer.</summary>
    [Fact]
    public async Task TheDetector_SharesOneAskInFlight_AndOneCallersCancel_LeavesTheOthersAnswer()
    {
        int asked = 0;
        var pending = new TaskCompletionSource<TailscaleServeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var detector = new TailscaleServeDetector(() => [Adapter("Tailscale", "100.78.20.125")], (_, token) =>
        {
            asked++;
            Assert.False(token.CanBeCanceled);   // under no caller's token
            return pending.Task;
        }, new ManualTimeProvider());

        using var cancel = new CancellationTokenSource();
        var first = detector.FindAsync(8765, cancel.Token);
        var second = detector.FindAsync(8765, CancellationToken.None);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        pending.SetResult(new TailscaleServeResult("https://h.ts.net", false, []));

        Assert.Equal("https://h.ts.net", (await second)!.Address);
        Assert.Equal(1, asked);
    }

    // ── The pipe client ─────────────────────────────────────────────────────

    /// <summary>
    /// Serves one HTTP answer on pipe <paramref name="name"/>: reads the request's head, hands it to <paramref name="seen"/>, then writes
    /// <paramref name="status"/> with <paramref name="body"/>.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static async Task ServeOnceAsync(string name, string status, string body, Action<string> seen)
    {
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        // Bounded (2026-11-01, the review's P7-6): a client that never connects fails the test instead of hanging it.
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await server.WaitForConnectionAsync(wait.Token);
        var head = new StringBuilder();
        var buffer = new byte[1024];
        while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            int read = await server.ReadAsync(buffer, wait.Token);
            if (read == 0)
            {
                break;
            }

            head.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }

        seen(head.ToString());
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        string response = "HTTP/1.1 " + status + "\r\nContent-Type: application/json\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n";
        await server.WriteAsync(Encoding.ASCII.GetBytes(response), wait.Token);
        await server.WriteAsync(bytes, wait.Token);
        await server.FlushAsync(wait.Token);
        // The client closes once it has read the answer (Connection: close); no WaitForPipeDrain, which blocks unbounded (the review's P7-7).
        try
        {
            while (await server.ReadAsync(buffer, wait.Token) > 0)
            {
            }
        }
        catch (IOException)
        {
            // The client's end went away: done.
        }
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public async Task TheClient_AsksThePipe_WithTailscalesHeader_AndReadsTheRules()
    {
        string name = "neon-test-" + Guid.NewGuid().ToString("N");
        string head = "";
        var serving = ServeOnceAsync(name, "200 OK", Spike, h => head = h);
        using var client = new TailscaleServeClient(name);

        var result = await client.FindAsync(8765, CancellationToken.None);
        await serving;

        Assert.Equal("https://hoopdiethink.tail9511ae.ts.net", result!.Address);
        Assert.StartsWith("GET /localapi/v0/serve-config HTTP/1.1", head, StringComparison.Ordinal);
        Assert.Contains("Sec-Tailscale: localapi", head, StringComparison.Ordinal);
        Assert.Contains("Host: local-tailscaled.sock", head, StringComparison.Ordinal);
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public async Task TheClient_A401_OrNotTheRules_IsAFailure()
    {
        foreach (var (status, body, failure) in new[] { ("401 Unauthorized", "Unable to impersonate", "HTTP 401"), ("200 OK", "not json", WebUiText.ServeFailureNotRules) })
        {
            string name = "neon-test-" + Guid.NewGuid().ToString("N");
            var serving = ServeOnceAsync(name, status, body, _ => { });
            using var client = new TailscaleServeClient(name);
            var result = await client.FindAsync(8765, CancellationToken.None);
            Assert.Null(result.Address);
            Assert.Equal(failure, result.Failure);
            await serving;
        }
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public async Task TheClient_WithNoPipe_IsAFailure_WithinTheRequestBudget_AndTheCallersCancelThrows()
    {
        using var client = new TailscaleServeClient("neon-test-" + Guid.NewGuid().ToString("N"));
        var started = System.Diagnostics.Stopwatch.StartNew();
        Assert.NotNull((await client.FindAsync(8765, CancellationToken.None)).Failure);
        // Widened (2026-11-01, the review's P6-4): a slow runner's scheduling is no failure of the budget.
        Assert.True(started.Elapsed < TailscaleServeClient.RequestTimeout + TimeSpan.FromSeconds(5), started.Elapsed.ToString());

        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.FindAsync(8765, cancel.Token));
    }

    // ── The setting ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("auto", true)]
    [InlineData("AUTO", true)]
    [InlineData(" off ", true)]
    [InlineData("https://h.ts.net", true)]
    [InlineData("https://h.ts.net/", true)]
    [InlineData("https://proxy.lan:8443", true)]
    [InlineData("http://h.ts.net", false)]
    [InlineData("https://h.ts.net/webui", false)]
    [InlineData("https://h.ts.net/?a=1", false)]
    [InlineData("https://h.ts.net/#top", false)]
    [InlineData("https://me:pw@h.ts.net", false)]
    [InlineData("h.ts.net", false)]
    [InlineData("https://h.ts.net:0", false)]
    [InlineData("", false)]
    public void TheSetting_TakesAutoOffOrAnHttpsAddress(string text, bool valid) => Assert.Equal(valid, WebUiHttpsSetting.IsValid(text));

    [Fact]
    public void TheSetting_ReadsItsWords_AndABrokenValueAsAuto()
    {
        Assert.Equal("auto", WebUiHttpsSetting.Default);
        Assert.Equal("auto", new Settings.AppSettingsData().WebUiHttpsAddress);
        Assert.Equal("https://h.ts.net", WebUiHttpsSetting.Address("https://H.ts.net/"));
        Assert.Equal("https://proxy.lan:8443", WebUiHttpsSetting.Address("https://proxy.lan:8443"));
        Assert.Equal("https://h.ts.net", WebUiHttpsSetting.Address("https://h.ts.net:443"));
        Assert.True(WebUiHttpsSetting.IsAuto("AUTO"));
        Assert.True(WebUiHttpsSetting.IsAuto(" "));   // blank is auto
        Assert.False(WebUiHttpsSetting.IsAuto("nonsense"));   // detected as auto is, with the pane's hint (2026-11-01)
        Assert.True(WebUiHttpsSetting.IsOff("OFF"));
        Assert.False(WebUiHttpsSetting.IsAuto("https://h.ts.net"));
        Assert.Equal("must be auto, off, or an https://host[:port] address of at most 99 characters", App.SettingsMenu.WebUiHttpsAddressError);

        // At most 99 characters as used (2026-11-01, the review's P8-1).
        string longest = "https://" + new string('a', WebUiHttpsSetting.MaxAddressChars - "https://".Length);
        Assert.Equal(longest, WebUiHttpsSetting.Address(longest));
        Assert.Null(WebUiHttpsSetting.Address(longest + "a"));
    }

    // ── The links ───────────────────────────────────────────────────────────

    [Fact]
    public void HttpsSignInLinks_AreSignInLinks_AndRedacted()
    {
        string link = WebUiAuth.SignInLinkAt("https://h.ts.net:8766", "tok");
        Assert.Equal("https://h.ts.net:8766/signin?t=tok", link);
        Assert.Equal("https://h.ts.net/signin?t=tok", WebUiAuth.SignInLinkAt("https://h.ts.net/", "tok"));
        Assert.Equal("https://h.ts.net/signin?once=c", WebUiAuth.OnceLinkAt("https://h.ts.net", "c"));
        string token = WebUiAuth.NewToken();
        Assert.True(WebUiAuth.IsSignInUrl(WebUiAuth.SignInLinkAt("https://h.ts.net:8766", token)));
        Assert.True(WebUiAuth.IsSignInUrl(WebUiAuth.OnceLinkAt("https://h.ts.net", new WebUiOneTimeCodes(TimeProvider.System).Mint())));
        Assert.Equal("https://h.ts.net:8766/signin?t=…", WebUiAuth.Redacted(link));
        Assert.Equal("http://192.168.1.2:8765/signin?t=tok", WebUiAuth.SignInLink("192.168.1.2", 8765, "tok"));   // unchanged
        Assert.Equal("http://[::1]:8765/", WebUiAuth.Address("::1", 8765));
    }

    /// <summary>2026-11-01, the review's P1-6: a stranger's https sign-in page isn't ours unless its value is one of ours in shape, under its whole key.</summary>
    [Fact]
    public void AStrangersHttpsSignInPage_IsNotASignInLink()
    {
        string token = WebUiAuth.NewToken();
        Assert.False(WebUiAuth.IsSignInUrl("https://shop.example/signin?t=abc"));
        Assert.False(WebUiAuth.IsSignInUrl("https://shop.example/signin?xt=" + token));
        Assert.False(WebUiAuth.IsSignInUrl("https://shop.example/signin?once=c"));
        Assert.True(WebUiAuth.IsSignInUrl("https://shop.example/signin?a=1&t=" + token));
        Assert.True(WebUiAuth.IsSignInUrl("http://192.168.1.2:8765/signin?t=abc"));   // http as before
    }

    [Fact]
    public void TheHttpsTab_IsTitledHttps_AndHeadedByWhereItsAddressCameFrom()
    {
        var link = new WebUiLink("https://h.ts.net/signin?t=x", WebUiLinkKind.Https);
        Assert.Equal("HTTPS", WebUiText.LinkTabTitle(link));
        Assert.Equal("100.78.20.125", WebUiText.LinkTabTitle(new WebUiLink("http://100.78.20.125:8765/signin?t=x", WebUiLinkKind.Tailscale)));

        var detected = new WebUiHttps("https://h.ts.net", true, false);
        Assert.Equal(WebUiText.HttpsDetectedNote + "\ns\n\n" + WebUiText.TailscaleScanHint + "\n", WebUiText.LinkTabText("s", WebUiLinkKind.Https, detected));
        // Funnel's note follows the reach (2026-11-01, the review's P1-3): local lets the internet in, lan refuses it.
        Assert.Equal(WebUiText.HttpsDetectedNote + "\n" + WebUiText.FunnelNote + "\ns\n\n" + WebUiText.TailscaleScanHint + "\n",
            WebUiText.LinkTabText("s", WebUiLinkKind.Https, detected with { Public = true }, WebUiAudience.Local));
        Assert.Equal(WebUiText.HttpsDetectedNote + "\n" + WebUiText.FunnelRefusedNote + "\ns\n\n" + WebUiText.TailscaleScanHint + "\n",
            WebUiText.LinkTabText("s", WebUiLinkKind.Https, detected with { Public = true }, WebUiAudience.LocalAreaNetwork));
        Assert.Equal("Funnel is on: anyone on the internet can reach the Web UI. The token is all that keeps them out.", WebUiText.FunnelNote);
        Assert.Equal("Funnel is on for the Web UI, but Web UI reach lan refuses requests from the internet.", WebUiText.FunnelRefusedNote);
        Assert.StartsWith(WebUiText.HttpsTypedNote + "\n", WebUiText.LinkTabText("s", WebUiLinkKind.Https, new WebUiHttps("https://p.lan", false, true)), StringComparison.Ordinal);   // a typed one is never judged public
        Assert.DoesNotContain(WebUiText.FunnelNote, WebUiText.LinkTabText("s", WebUiLinkKind.Https, new WebUiHttps("https://p.lan", false, true)), StringComparison.Ordinal);

        // Tailscale's tab beside a detected HTTPS tab is the fallback (pass 12); beside a typed one (2026-11-01, the review's P4-2) or none, as before.
        Assert.StartsWith(WebUiText.TailscaleFallbackNote + "\n", WebUiText.LinkTabText("s", WebUiLinkKind.Tailscale, detected), StringComparison.Ordinal);
        Assert.StartsWith(WebUiText.TailscaleNote + "\n", WebUiText.LinkTabText("s", WebUiLinkKind.Tailscale, new WebUiHttps("https://p.lan", false, false)), StringComparison.Ordinal);
        Assert.StartsWith(WebUiText.TailscaleNote + "\n", WebUiText.LinkTabText("s", WebUiLinkKind.Tailscale), StringComparison.Ordinal);
        Assert.Equal("Tailscale over plain http: works on your tailnet too, if the HTTPS address doesn't.", WebUiText.TailscaleFallbackNote);
        Assert.Equal("HTTPS through Tailscale Serve: works from any device on your tailnet, at home or away.", WebUiText.HttpsDetectedNote);
    }

    [Fact]
    public void TheStatusLines_SayLocalPlusHttps_AndEndWithTheHint()
    {
        var https = new WebUiHttps("https://h.ts.net", true, false);
        Assert.Equal("Reach: local — localhost only, plus the HTTPS address", WebUiText.StatusLines(true, true, 8765, WebUiAudience.Local, https)[2]);
        Assert.Equal("Reach: local — localhost only", WebUiText.StatusLines(true, true, 8765, WebUiAudience.Local)[2]);
        var lines = WebUiText.StatusLines(true, true, 8766, WebUiAudience.LocalAreaNetwork, null, WebUiText.ServeNoRuleHint(8766, 443));
        Assert.Equal("Tailscale Serve has no rule for port 8766: tailscale serve --bg 8766 adds one.", lines[^1]);
        Assert.Equal(3, WebUiText.StatusLines(true, true, 8766, WebUiAudience.LocalAreaNetwork, https).Count);   // no hint: three lines
        Assert.Equal(5, WebUiText.StatusLines(true, true, 8766, WebUiAudience.LocalAreaNetwork, null, "a\nb").Count);   // a line per \n
        Assert.Equal("Tailscale Serve couldn't be asked (HTTP 403): no HTTPS tab.", WebUiText.ServeAskFailedHint(WebUiText.ServeFailureStatus(403)));
        Assert.Equal("Web UI HTTPS address isn't auto, off or an https address: detecting instead.", WebUiText.HttpsSettingBrokenHint);
    }

    /// <summary>
    /// Under <c>local</c> with no HTTPS address (2026-11-08, the default since that day) the line after the reach says how a phone gets in;
    /// not while off, under <c>lan</c>, or with an HTTPS address (the reach line names it then).
    /// </summary>
    [Fact]
    public void TheStatusLines_UnderLocalAlone_SayHowAPhoneGetsIn()
    {
        Assert.Equal("Only this computer can open it. For a phone, switch to lan (l in /webui) or keep local and turn on Tailscale Serve.", WebUiText.LocalOnlyNote);
        Assert.Equal(WebUiText.LocalOnlyNote, WebUiText.StatusLines(true, true, 8765, WebUiAudience.Local)[3]);
        Assert.DoesNotContain(WebUiText.LocalOnlyNote, WebUiText.StatusLines(false, false, 8765, WebUiAudience.Local));
        Assert.DoesNotContain(WebUiText.LocalOnlyNote, WebUiText.StatusLines(true, true, 8765, WebUiAudience.LocalAreaNetwork));
        Assert.DoesNotContain(WebUiText.LocalOnlyNote, WebUiText.StatusLines(true, true, 8765, WebUiAudience.Local, new WebUiHttps("https://h.ts.net", true, false)));
    }

    [Fact]
    public void WithoutAPane_TheHttpsLinkIsMarked_AndTheLeadSaysWhereEachWorks()
    {
        WebUiLink[] links =
        [
            new("https://h.ts.net/signin?t=x", WebUiLinkKind.Https),
            new("http://100.78.20.125:8765/signin?t=x", WebUiLinkKind.Tailscale),
            new("http://192.168.1.217:8765/signin?t=x"),
        ];
        var lines = WebUiText.PaneLines(true, true, 8765, WebUiAudience.LocalAreaNetwork, links, new WebUiHttps("https://h.ts.net", true, false));
        Assert.Contains(lines, l => l.StartsWith("Open one of these in the browser (a phone on the same network, or anywhere on your tailnet for the ones marked HTTPS and Tailscale).", StringComparison.Ordinal));
        Assert.Contains("  https://h.ts.net/signin?t=x" + WebUiText.HttpsMark, lines);
        Assert.Contains("  http://100.78.20.125:8765/signin?t=x" + WebUiText.TailscaleMark, lines);
        Assert.Equal(" (HTTPS)", WebUiText.HttpsMark);

        var typed = WebUiText.PaneLines(true, true, 8765, WebUiAudience.LocalAreaNetwork, links, new WebUiHttps("https://p.lan", false, false));
        Assert.Contains(typed, l => l.StartsWith("Open one of these in the browser (a phone on the same network, the one marked HTTPS wherever your proxy reaches, and on your tailnet for the one marked Tailscale).", StringComparison.Ordinal));
    }

    /// <summary>The lead from parts (2026-11-01, the review's P1-13, P1-4): "this computer" under local, Serve's or a typed HTTPS link, Tailscale's.</summary>
    [Fact]
    public void TheLead_IsBuiltFromParts()
    {
        var detected = new WebUiHttps("https://h.ts.net", true, false);
        var typed = new WebUiHttps("https://p.lan", false, false);
        Assert.Equal("Open one of these in the browser (this computer, or anywhere on your tailnet for the one marked HTTPS). The link signs it in and leaves the address bar at once:",
            WebUiText.Lead(WebUiAudience.Local, detected, tailscale: false));
        Assert.Equal("Open one of these in the browser (this computer, or the one marked HTTPS wherever your proxy reaches). The link signs it in and leaves the address bar at once:",
            WebUiText.Lead(WebUiAudience.Local, typed, tailscale: false));
        Assert.Equal("Open one of these in the browser (a phone on the same network, or anywhere on your tailnet for the one marked HTTPS). The link signs it in and leaves the address bar at once:",
            WebUiText.Lead(WebUiAudience.LocalAreaNetwork, detected, tailscale: false));
        Assert.Equal("Open one of these in the browser (this computer). The link signs it in and leaves the address bar at once:",
            WebUiText.Lead(WebUiAudience.Local, null, tailscale: false));
    }

    /// <summary>2026-11-01, the review's P3-1: without a pane, a funneled HTTPS link is marked and followed by the note its reach picks.</summary>
    [Fact]
    public void WithoutAPane_AFunneledLink_IsMarked_AndNoted()
    {
        var funneled = new WebUiHttps("https://h.ts.net", true, true);
        WebUiLink[] links = [new("https://h.ts.net/signin?t=x", WebUiLinkKind.Https), new("http://localhost:8765/signin?t=x")];
        var local = WebUiText.PaneLines(true, true, 8765, WebUiAudience.Local, links, funneled);
        Assert.Contains("  https://h.ts.net/signin?t=x" + WebUiText.FunnelMark, local);
        Assert.Contains(WebUiText.FunnelNote, local);
        var lan = WebUiText.PaneLines(true, true, 8765, WebUiAudience.LocalAreaNetwork, links, funneled);
        Assert.Contains("  https://h.ts.net/signin?t=x" + WebUiText.HttpsMark, lan);
        Assert.Contains(WebUiText.FunnelRefusedNote, lan);
    }

    // ── Requests through a proxy ────────────────────────────────────────────

    [Fact]
    public void ThroughServe_TheOriginIsTheTailnetDevice()
    {
        var origin = WebUiOrigin.Of(IPAddress.Loopback, "hoopdiethink.tail9511ae.ts.net", "100.64.1.2", null);
        Assert.Equal(IPAddress.Parse("100.64.1.2"), origin.Remote);
        Assert.Equal(WebUiText.TailscaleServeName, origin.Through);
        Assert.False(origin.Funnel);
        Assert.Equal("(" + WebUiText.Glyph + "a browser signed in to the Web UI from 100.64.1.2 through Tailscale Serve)", WebUiText.SignedInNotice(origin));

        var funnel = WebUiOrigin.Of(IPAddress.Loopback, "h.ts.net", "203.0.113.9", "?1");
        Assert.True(funnel.Funnel);
        Assert.Equal("(" + WebUiText.Glyph + "a browser signed in to the Web UI from 203.0.113.9 through Tailscale Funnel, from the internet)", WebUiText.SignedInNotice(funnel));
    }

    [Theory]
    [InlineData("hoopdiethink.tail9511ae.ts.net:8766")]
    [InlineData("HOOPDIETHINK.TAIL9511AE.TS.NET.")]
    public void AServeHost_WithAPortOrATrailingDot_IsStillServe(string host) =>
        Assert.Equal(WebUiText.TailscaleServeName, WebUiOrigin.Of(IPAddress.Loopback, host, "100.64.1.2", null).Through);

    [Fact]
    public void OtherProxies_AreNamed_TheLastForwardedEntryCounts_AndOnlyLoopbackIsTrusted()
    {
        Assert.Equal("the proxy at evil-ts.net", WebUiOrigin.Of(IPAddress.Loopback, "evil-ts.net", "192.168.1.50", null).Through);
        Assert.Equal("the proxy at caddy.lan", WebUiOrigin.Of(IPAddress.Loopback, "caddy.lan", "192.168.1.50", null).Through);
        Assert.Equal(IPAddress.Parse("192.168.1.50"), WebUiOrigin.Of(IPAddress.Loopback, "caddy.lan", "6.6.6.6, 192.168.1.50", null).Remote);
        Assert.Equal(IPAddress.Loopback, WebUiOrigin.Of(IPAddress.Loopback, "h.ts.net", "not an address", null).Remote);
        Assert.Equal(IPAddress.Loopback, WebUiOrigin.Of(IPAddress.Loopback, "h.ts.net", "", null).Remote);
        Assert.Equal(IPAddress.Parse("100.64.1.2"), WebUiOrigin.Of(IPAddress.Parse("::ffff:127.0.0.1"), "h.ts.net", "100.64.1.2", null).Remote);   // a mapped loopback is loopback

        var lan = IPAddress.Parse("192.168.1.40");
        Assert.Equal(new WebUiOrigin(lan), WebUiOrigin.Of(lan, "h.ts.net", "100.64.1.2", "?1"));   // a LAN browser's headers are ignored
        Assert.Equal(new WebUiOrigin(IPAddress.Loopback), WebUiOrigin.Of(IPAddress.Loopback, null, null, null));   // no proxy: /webui browser

        string through = WebUiOrigin.Of(IPAddress.Loopback, "bad\u0007[red]host", "10.0.0.2", null).Through!;
        Assert.DoesNotContain('\u0007', through);
        Assert.Contains("[red]", through, StringComparison.Ordinal);   // printed as written: notices are escaped
        Assert.True(WebUiOrigin.Of(IPAddress.Loopback, new string('a', 300), "10.0.0.2", null).Through!.Length <= "the proxy at ".Length + WebUiOrigin.MaxHostChars + 1);
    }

    /// <summary>2026-11-01, the review's P2-3, P3-4, P1-7, P7-5: the forwarded headers' other shapes.</summary>
    [Fact]
    public void TheForwardedHeaders_OtherShapes_AreRead()
    {
        // X-Forwarded-For without a host: a proxy on this computer.
        Assert.Equal(new WebUiOrigin(IPAddress.Parse("100.64.1.2"), WebUiText.ProxyOnThisComputer), WebUiOrigin.Of(IPAddress.Loopback, null, "100.64.1.2", null));
        Assert.Equal("(" + WebUiText.Glyph + "a browser signed in to the Web UI from 100.64.1.2 through a proxy on this computer)",
            WebUiText.SignedInNotice(WebUiOrigin.Of(IPAddress.Loopback, "", "100.64.1.2", null)));

        // An entry with a port, IPv4 or bracketed IPv6; a bare IPv6 address is never cut at its last colon.
        Assert.Equal(IPAddress.Parse("100.64.1.2"), WebUiOrigin.Of(IPAddress.Loopback, "h.ts.net", "100.64.1.2:5555", null).Remote);
        Assert.Equal(IPAddress.Parse("fd7a:115c:a1e0::1"), WebUiOrigin.Of(IPAddress.Loopback, "h.ts.net", "[fd7a:115c:a1e0::1]:443", null).Remote);
        Assert.Equal(IPAddress.Parse("fd7a:115c:a1e0::1"), WebUiOrigin.Of(IPAddress.Loopback, "h.ts.net", "fd7a:115c:a1e0::1", null).Remote);
        Assert.Equal(IPAddress.Loopback, WebUiOrigin.Of(IPAddress.Loopback, "h.ts.net", "12345", null).Remote);   // a bare number is no address

        // The host's first entry alone.
        Assert.Equal(WebUiText.TailscaleServeName, WebUiOrigin.Of(IPAddress.Loopback, "h.ts.net, caddy.lan", "192.168.1.5", null).Through);
        Assert.Equal("the proxy at caddy.lan", WebUiOrigin.Of(IPAddress.Loopback, "caddy.lan:8443, h.ts.net", "192.168.1.5", null).Through);

        // A MagicDNS short name is Serve when the address is a tailnet one.
        Assert.Equal(WebUiText.TailscaleServeName, WebUiOrigin.Of(IPAddress.Loopback, "hoopdiethink", "100.64.1.2", null).Through);
        Assert.Equal(WebUiText.TailscaleServeName, WebUiOrigin.Of(IPAddress.Loopback, "hoopdiethink:8766", "fd7a:115c:a1e0::9", null).Through);
        Assert.Equal("the proxy at hoopdiethink", WebUiOrigin.Of(IPAddress.Loopback, "hoopdiethink", "192.168.1.5", null).Through);
    }

    /// <summary>Records compared by value, their lists element by element.</summary>
    private sealed class ResultComparer : IEqualityComparer<TailscaleServeResult?>
    {
        public static ResultComparer Instance { get; } = new();

        public bool Equals(TailscaleServeResult? x, TailscaleServeResult? y) =>
            x is null ? y is null : y is not null && x.Address == y.Address && x.Public == y.Public && x.OtherPorts.SequenceEqual(y.OtherPorts);

        public int GetHashCode(TailscaleServeResult? obj) => obj?.Address?.GetHashCode(StringComparison.Ordinal) ?? 0;
    }
}
