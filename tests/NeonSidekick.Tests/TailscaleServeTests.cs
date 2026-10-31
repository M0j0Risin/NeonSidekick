using System.IO.Pipes;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using System.Text;
using NeonSidekick.Diagnostics;
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

    [Fact]
    public async Task TheDetector_AsksOnlyWhileTailscaleIsUp_AndLogsEachAnswerOnce()
    {
        var adapters = new List<WebUiAdapter>();
        int asked = 0;
        var answer = new TailscaleServeResult("https://h.ts.net", false, []);
        var detector = new TailscaleServeDetector(() => adapters, (_, _) => { asked++; return Task.FromResult<TailscaleServeResult?>(answer); });
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
            Assert.Equal(2, asked);
            Assert.Equal(2, lines.Count);   // the address once
            Assert.Equal(WebUiText.HttpsAddressLogLine("https://h.ts.net", false), lines[^1]);

            answer = new TailscaleServeResult(null, false, [8766]);
            await detector.FindAsync(8765, CancellationToken.None);
            Assert.Equal(WebUiText.ServeNoRuleLogLine(8765, [8766]), lines[^1]);
            Assert.Equal("Tailscale Serve: no rule for port 8765 (rules for 8766).", lines[^1]);
            Assert.DoesNotContain(lines, l => l.Contains("?t=", StringComparison.Ordinal));
        }
        finally
        {
            DiagnosticLog.Emitted -= Record;
        }
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
        await server.WaitForConnectionAsync();
        var head = new StringBuilder();
        var buffer = new byte[1024];
        while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            int read = await server.ReadAsync(buffer);
            if (read == 0)
            {
                break;
            }

            head.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }

        seen(head.ToString());
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        string response = "HTTP/1.1 " + status + "\r\nContent-Type: application/json\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n";
        await server.WriteAsync(Encoding.ASCII.GetBytes(response));
        await server.WriteAsync(bytes);
        await server.FlushAsync();
        server.WaitForPipeDrain();
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
    public async Task TheClient_A401_OrNotTheRules_IsNull()
    {
        foreach (var (status, body) in new[] { ("401 Unauthorized", "Unable to impersonate"), ("200 OK", "not json") })
        {
            string name = "neon-test-" + Guid.NewGuid().ToString("N");
            var serving = ServeOnceAsync(name, status, body, _ => { });
            using var client = new TailscaleServeClient(name);
            Assert.Null(await client.FindAsync(8765, CancellationToken.None));
            await serving;
        }
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public async Task TheClient_WithNoPipe_IsNull_WithinTheConnectBudget_AndTheCallersCancelThrows()
    {
        using var client = new TailscaleServeClient("neon-test-" + Guid.NewGuid().ToString("N"));
        var started = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(await client.FindAsync(8765, CancellationToken.None));
        Assert.True(started.Elapsed < TailscaleServeClient.RequestTimeout + TimeSpan.FromSeconds(1), started.Elapsed.ToString());

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
        Assert.True(WebUiHttpsSetting.Detects("auto"));
        Assert.True(WebUiHttpsSetting.Detects("nonsense"));   // a hand-broken saved value reads as auto
        Assert.False(WebUiHttpsSetting.Detects("OFF"));
        Assert.True(WebUiHttpsSetting.IsOff("OFF"));
        Assert.False(WebUiHttpsSetting.Detects("https://h.ts.net"));
        Assert.Equal("Web UI HTTPS address must be auto, off, or an https://host[:port] address; keeping auto.", WebUiText.HttpsAddressInvalid("auto"));
    }

    // ── The links ───────────────────────────────────────────────────────────

    [Fact]
    public void HttpsSignInLinks_AreSignInLinks_AndRedacted()
    {
        string link = WebUiAuth.SignInLinkAt("https://h.ts.net:8766", "tok");
        Assert.Equal("https://h.ts.net:8766/signin?t=tok", link);
        Assert.Equal("https://h.ts.net/signin?t=tok", WebUiAuth.SignInLinkAt("https://h.ts.net/", "tok"));
        Assert.Equal("https://h.ts.net/signin?once=c", WebUiAuth.OnceLinkAt("https://h.ts.net", "c"));
        Assert.True(WebUiAuth.IsSignInUrl(link));
        Assert.True(WebUiAuth.IsSignInUrl("https://h.ts.net/signin?once=c"));
        Assert.Equal("https://h.ts.net:8766/signin?t=…", WebUiAuth.Redacted(link));
        Assert.Equal("http://192.168.1.2:8765/signin?t=tok", WebUiAuth.SignInLink("192.168.1.2", 8765, "tok"));   // unchanged
        Assert.Equal("http://[::1]:8765/", WebUiAuth.Address("::1", 8765));
    }

    [Fact]
    public void TheHttpsTab_IsTitledHttps_AndHeadedByWhereItsAddressCameFrom()
    {
        var link = new WebUiLink("https://h.ts.net/signin?t=x", false, true);
        Assert.Equal("HTTPS", WebUiText.LinkTabTitle(link));
        Assert.Equal("100.78.20.125", WebUiText.LinkTabTitle(new WebUiLink("http://100.78.20.125:8765/signin?t=x", true)));
        Assert.Equal(WebUiLinkKind.Https, WebUiText.KindOf(link));

        var detected = new WebUiHttps("https://h.ts.net", true, false);
        Assert.Equal(WebUiText.HttpsDetectedNote + "\ns\n\n" + WebUiText.TailscaleScanHint + "\n", WebUiText.LinkTabText("s", WebUiLinkKind.Https, detected));
        Assert.Equal(WebUiText.HttpsDetectedNote + "\n" + WebUiText.FunnelNote + "\ns\n\n" + WebUiText.TailscaleScanHint + "\n",
            WebUiText.LinkTabText("s", WebUiLinkKind.Https, detected with { Public = true }));
        Assert.StartsWith(WebUiText.HttpsTypedNote + "\n", WebUiText.LinkTabText("s", WebUiLinkKind.Https, new WebUiHttps("https://p.lan", false, true)), StringComparison.Ordinal);   // a typed one is never judged public
        Assert.DoesNotContain(WebUiText.FunnelNote, WebUiText.LinkTabText("s", WebUiLinkKind.Https, new WebUiHttps("https://p.lan", false, true)), StringComparison.Ordinal);

        // Tailscale's tab beside an HTTPS tab is the fallback (pass 12); without one, as before.
        Assert.StartsWith(WebUiText.TailscaleFallbackNote + "\n", WebUiText.LinkTabText("s", WebUiLinkKind.Tailscale, detected), StringComparison.Ordinal);
        Assert.StartsWith(WebUiText.TailscaleNote + "\n", WebUiText.LinkTabText("s", WebUiLinkKind.Tailscale), StringComparison.Ordinal);
        Assert.Equal("Tailscale over plain http: works on your tailnet too, if the HTTPS address doesn't.", WebUiText.TailscaleFallbackNote);
        Assert.Equal("HTTPS through Tailscale Serve: works from any device on your tailnet, at home or away.", WebUiText.HttpsDetectedNote);
    }

    [Fact]
    public void TheStatusLines_SayLocalPlusHttps_AndNameServesOtherPorts()
    {
        var https = new WebUiHttps("https://h.ts.net", true, false);
        Assert.Equal("Reach: local — localhost only, plus the HTTPS address", WebUiText.StatusLines(true, true, 8765, WebUiAudience.Local, https)[2]);
        Assert.Equal("Reach: local — localhost only", WebUiText.StatusLines(true, true, 8765, WebUiAudience.Local)[2]);
        var lines = WebUiText.StatusLines(true, true, 8766, WebUiAudience.LocalAreaNetwork, null, [8765]);
        Assert.Equal("Tailscale Serve forwards to port 8765, not 8766: tailscale serve --bg --https=8766 8766 adds this one.", lines[^1]);
        Assert.Equal("Tailscale Serve forwards to ports 1, 2, 3, not 8766: tailscale serve --bg --https=8766 8766 adds this one.", WebUiText.ServeOtherPort([4, 3, 2, 1], 8766));
        Assert.Equal(3, WebUiText.StatusLines(true, true, 8766, WebUiAudience.LocalAreaNetwork, https, [8765]).Count);   // an address: no hint
    }

    [Fact]
    public void WithoutAPane_TheHttpsLinkIsMarked_AndTheLeadSaysWhereEachWorks()
    {
        WebUiLink[] links =
        [
            new("https://h.ts.net/signin?t=x", false, true),
            new("http://100.78.20.125:8765/signin?t=x", true),
            new("http://192.168.1.217:8765/signin?t=x", false),
        ];
        var lines = WebUiText.PaneLines(true, true, 8765, WebUiAudience.LocalAreaNetwork, links, new WebUiHttps("https://h.ts.net", true, false));
        Assert.Contains(lines, l => l.StartsWith("Open one of these in the browser (a phone on the same network, or anywhere on your tailnet for the ones marked HTTPS and Tailscale).", StringComparison.Ordinal));
        Assert.Contains("  https://h.ts.net/signin?t=x" + WebUiText.HttpsMark, lines);
        Assert.Contains("  http://100.78.20.125:8765/signin?t=x" + WebUiText.TailscaleMark, lines);
        Assert.Equal(" (HTTPS)", WebUiText.HttpsMark);

        var typed = WebUiText.PaneLines(true, true, 8765, WebUiAudience.LocalAreaNetwork, links, new WebUiHttps("https://p.lan", false, false));
        Assert.Contains(typed, l => l.StartsWith("Open one of these in the browser (a phone on the same network, the one marked HTTPS wherever your proxy reaches, and your tailnet for the one marked Tailscale).", StringComparison.Ordinal));
    }

    // ── Requests through a proxy ────────────────────────────────────────────

    [Fact]
    public void ThroughServe_TheOriginIsTheTailnetDevice()
    {
        var origin = WebUiOrigin.Of(IPAddress.Loopback, "hoopdiethink.tail9511ae.ts.net", "100.64.1.2", null);
        Assert.Equal(IPAddress.Parse("100.64.1.2"), origin.Remote);
        Assert.Equal(WebUiOrigin.TailscaleServeName, origin.Through);
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
        Assert.Equal(WebUiOrigin.TailscaleServeName, WebUiOrigin.Of(IPAddress.Loopback, host, "100.64.1.2", null).Through);

    [Fact]
    public void OtherProxies_AreNamed_TheLastForwardedEntryCounts_AndOnlyLoopbackIsTrusted()
    {
        Assert.Equal("the proxy at evil-ts.net", WebUiOrigin.Of(IPAddress.Loopback, "evil-ts.net", "100.64.1.2", null).Through);
        Assert.Equal("the proxy at caddy.lan", WebUiOrigin.Of(IPAddress.Loopback, "caddy.lan", "192.168.1.50", null).Through);
        Assert.Equal(IPAddress.Parse("192.168.1.50"), WebUiOrigin.Of(IPAddress.Loopback, "caddy.lan", "6.6.6.6, 192.168.1.50", null).Remote);
        Assert.Equal(IPAddress.Loopback, WebUiOrigin.Of(IPAddress.Loopback, "h.ts.net", "not an address", null).Remote);
        Assert.Equal(IPAddress.Loopback, WebUiOrigin.Of(IPAddress.Loopback, "h.ts.net", "", null).Remote);
        Assert.Equal(IPAddress.Parse("100.64.1.2"), WebUiOrigin.Of(IPAddress.Parse("::ffff:127.0.0.1"), "h.ts.net", "100.64.1.2", null).Remote);   // a mapped loopback is loopback

        var lan = IPAddress.Parse("192.168.1.40");
        Assert.Equal(new WebUiOrigin(lan), WebUiOrigin.Of(lan, "h.ts.net", "100.64.1.2", "?1"));   // a LAN browser's headers are ignored
        Assert.Equal(new WebUiOrigin(IPAddress.Loopback), WebUiOrigin.Of(IPAddress.Loopback, null, "100.64.1.2", null));   // no proxy: /webui browser

        string through = WebUiOrigin.Of(IPAddress.Loopback, "bad\u0007[red]host", "10.0.0.2", null).Through!;
        Assert.DoesNotContain('\u0007', through);
        Assert.Contains("[red]", through, StringComparison.Ordinal);   // printed as written: notices are escaped
        Assert.True(WebUiOrigin.Of(IPAddress.Loopback, new string('a', 300), "10.0.0.2", null).Through!.Length <= "the proxy at ".Length + WebUiOrigin.MaxHostChars + 1);
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
