using System.Net;
using System.Security.Authentication;
using Microsoft.Extensions.AI;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Network;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Web;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>net_http</c> and <c>/net http</c> (2026-11-16, plan sharded-hugging-puppy #15, "is this site up?") over a stub handler: the status and the
/// telling headers fenced, a redirect chain with every hop's host judged and counted, a redirect to a denied or LAN host stopped there, the URL
/// rules (user info, legacy spellings, schemes), HEAD, nofollow, the browser's headers or the app's own, a certificate failure worded as an
/// answer, and the command's parse.
/// </summary>
public sealed class NetHttpTests
{
    private readonly ManualTimeProvider _time = new();
    private readonly AppSettingsData _settings = new() { NetworkTools = true, NetworkToolsReach = "both" };
    private readonly StubHttpMessageHandler _stub = new();
    private readonly List<NetworkTarget> _targets = [];
    private readonly Dictionary<string, IPAddress[]> _names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["example.com"] = [IPAddress.Parse("93.184.215.14")],
        ["www.example.com"] = [IPAddress.Parse("93.184.215.15")],
        ["cdn.example.net"] = [IPAddress.Parse("93.184.215.16")],
        ["blocked.example"] = [IPAddress.Parse("93.184.215.17")],
        ["nas.lan"] = [IPAddress.Parse("192.168.1.20")],
    };

    private NetworkAccess Access(Func<NetworkTarget, TlsVerdict, HttpMessageHandler>? http = null, Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null) =>
        new(() => _settings, null, _time, resolve ?? ((host, _) => _names.TryGetValue(host, out var a) ? Task.FromResult(a) : throw new System.Net.Sockets.SocketException(11001)),
            http: http ?? ((target, _) =>
            {
                _targets.Add(target);
                return _stub;
            }),
            identity: () => BrowserIdentity.Fallback);

    private static async Task<string> Call(AIFunction tool, params (string Name, object? Value)[] arguments)
    {
        var args = new AIFunctionArguments();
        foreach (var (name, value) in arguments)
        {
            args[name] = value;
        }

        return (string)(await tool.InvokeAsync(args))!;
    }

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.MovedPermanently) { ReasonPhrase = "Moved Permanently" };
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    [Fact]
    public async Task Status_AndTheTellingHeaders_Fenced()
    {
        _stub.Map("https://example.com/", (_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { ReasonPhrase = "OK", Content = new StringContent("<html>never read</html>") };
            response.Headers.TryAddWithoutValidation("Server", "nginx");
            response.Headers.TryAddWithoutValidation("Cache-Control", "max-age=60");
            response.Headers.TryAddWithoutValidation("X-Not-Telling", "secret");
            return Task.FromResult(response);
        });
        var budget = new NetworkBudget();
        string answer = await Call(new NetHttpTool(Access(), budget), ("url", "example.com"));
        Assert.StartsWith("HTTP https://example.com/: 200 (1 request, <1 ms)\n", answer);
        Assert.Contains("\n  1. GET https://example.com/ → 200 ⟪OK⟫, ", answer);
        Assert.Contains("\n  server: ⟪nginx⟫", answer);
        Assert.Contains("\n  cache-control: ⟪max-age=60⟫", answer);
        Assert.Contains("\n  content-type: ⟪text/plain; charset=utf-8⟫", answer);
        Assert.DoesNotContain("never read", answer);
        Assert.DoesNotContain("x-not-telling", answer);
        Assert.EndsWith(UntrustedText.QuoteNote, answer);
        Assert.Equal(1, budget.Hosts);
        Assert.Equal([IPAddress.Parse("93.184.215.14")], _targets.Single().Addresses);
    }

    [Fact]
    public async Task AChain_EveryHopJudgedAndCounted_RelativeAndAbsolute()
    {
        _stub.Map("https://www.example.com/home", (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));   // first: the stub matches by prefix
        _stub.Map("http://example.com/", (_, _) => Task.FromResult(Redirect("https://www.example.com/")));
        _stub.Map("https://www.example.com/", (_, _) => Task.FromResult(Redirect("/home")));
        var budget = new NetworkBudget();
        string answer = await Call(new NetHttpTool(Access(), budget), ("url", "http://example.com/"));
        Assert.StartsWith("HTTP http://example.com/: 200 (3 requests, ", answer);
        Assert.Contains("\n  1. GET http://example.com/ → 301 ⟪Moved Permanently⟫, ", answer);
        Assert.Contains(" → ⟪https://www.example.com/⟫", answer);
        Assert.Contains("\n  2. GET ⟪https://www.example.com/⟫ → 301 ", answer);
        Assert.Contains("\n  3. GET ⟪https://www.example.com/home⟫ → 200", answer);
        Assert.Equal(["example.com", "www.example.com", "www.example.com"], _targets.Select(t => t.Host));
        Assert.Equal(2, budget.Hosts);   // www.example.com counted once, as any host is
    }

    [Fact]
    public async Task ARedirectLoop_StopsAtTheCap_AndSaysSo()
    {
        _stub.Map("https://example.com/", (_, _) => Task.FromResult(Redirect("/")));
        string answer = await Call(new NetHttpTool(Access(), new NetworkBudget()), ("url", "https://example.com/"));
        Assert.Equal(HttpProbe.MaxHops, _stub.Requests.Count);
        Assert.Contains("\n  " + NetworkText.HttpTooManyHops(HttpProbe.MaxHops), answer);
    }

    [Fact]
    public async Task ARedirect_ToADeniedHost_StopsThere()
    {
        _settings.NetworkDeniedHosts = ["blocked.example"];
        _stub.Map("https://example.com/", (_, _) => Task.FromResult(Redirect("https://blocked.example/x")));
        string answer = await Call(new NetHttpTool(Access(), new NetworkBudget()), ("url", "https://example.com/"));
        Assert.Contains("\n  2. GET ⟪https://blocked.example/x⟫ → refused: ", answer);
        Assert.Single(_targets);   // the denied host was never connected to
        Assert.StartsWith("HTTP https://example.com/: 301, then refused: ", answer);
    }

    [Fact]
    public async Task ARedirect_ToTheLan_IsRefused_UnderInternet()
    {
        _settings.NetworkToolsReach = "internet";
        _stub.Map("https://example.com/", (_, _) => Task.FromResult(Redirect("http://nas.lan/admin")));
        string answer = await Call(new NetHttpTool(Access(), new NetworkBudget()), ("url", "https://example.com/"));
        Assert.Contains("\n  2. GET ⟪http://nas.lan/admin⟫ → refused: ", answer);
        Assert.Single(_targets);
        // The first host refused is the whole answer, as the other tools' refusals are.
        Assert.StartsWith("Error: ", await Call(new NetHttpTool(Access(), new NetworkBudget()), ("url", "http://nas.lan/")));
    }

    [Theory]
    [InlineData("https://user:pw@example.com/", NetworkText.UrlUserInfo)]
    [InlineData("ftp://example.com/", "Error: 'ftp://example.com/' is not an http or https address.")]
    [InlineData("http://0x7f.1/", "Error: 'http://0x7f.1/' is not a web address with one host name or address.")]
    [InlineData("http://10.1/", "Error: 'http://10.1/' is not a web address with one host name or address.")]
    [InlineData("http://exa mple.com/", "Error: 'http://exa mple.com/' is not a web address with one host name or address.")]
    public async Task Urls_TheModelWroteBadly_AreRefused_BeforeAnyRequest(string url, string error)
    {
        Assert.Equal(error, await Call(new NetHttpTool(Access(), new NetworkBudget()), ("url", url)));
        Assert.Empty(_targets);
    }

    [Fact]
    public void ParseUrl_ReadsBareHosts_Ipv6_AndScopeRelativeRedirects()
    {
        Assert.Equal("https://example.com/", HttpProbe.ParseUrl("example.com").Url!.AbsoluteUri);
        Assert.Equal("[::1]", HttpProbe.WrittenHost("http://[::1]:8080/x"));
        Assert.Equal("::1", HttpProbe.ParseUrl("http://[::1]:8080/x").Url!.IdnHost);
        var base_ = new Uri("https://example.com/a/b");
        Assert.Equal("https://example.com/c", HttpProbe.ParseUrl("/c", base_).Url!.AbsoluteUri);
        Assert.Equal("https://cdn.example.net/x", HttpProbe.ParseUrl("//cdn.example.net/x", base_).Url!.AbsoluteUri);
        Assert.NotNull(HttpProbe.ParseUrl("//0x7f.1/x", base_).Error);
        Assert.Equal("xn--bcher-kva.example", HttpProbe.ParseUrl("https://bücher.example/").Url!.IdnHost);
    }

    [Fact]
    public async Task Head_NoFollow_AndTheAppsOwnName()
    {
        _stub.Map("https://example.com/", (_, _) => Task.FromResult(Redirect("https://www.example.com/")));
        string answer = await Call(new NetHttpTool(Access(), new NetworkBudget()), ("url", "https://example.com/"), ("method", "head"), ("follow", false));
        Assert.Contains("\n  1. HEAD https://example.com/ → 301 ", answer);
        Assert.Contains("; " + NetworkText.HttpNotFollowed, answer);
        Assert.Single(_stub.Requests);
        Assert.Equal(HttpMethod.Head, _stub.Requests[0].Method);
        Assert.Equal(NetworkText.BadMethod("post"), await Call(new NetHttpTool(Access(), new NetworkBudget()), ("url", "example.com"), ("method", "post")));
    }

    [Fact]
    public async Task Browser_SendsABrowsersHeaders_ElseTheAppsName()
    {
        var agents = new List<string>();
        _stub.Map("https://example.com/", (request, _) =>
        {
            agents.Add(string.Join(' ', request.Headers.GetValues("User-Agent")));
            Assert.Equal(new Version(2, 0), request.Version);
            Assert.Equal(HttpVersionPolicy.RequestVersionOrLower, request.VersionPolicy);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        await Call(new NetHttpTool(Access(), new NetworkBudget()), ("url", "https://example.com/"));
        await Call(new NetHttpTool(Access(), new NetworkBudget()), ("url", "https://example.com/"), ("browser", true));
        Assert.StartsWith("NeonSidekick/", agents[0], StringComparison.Ordinal);
        Assert.Equal(BrowserIdentity.Fallback.UserAgent, agents[1]);
    }

    /// <summary>
    /// A certificate the validation callback found errors in is an answer worded as <c>net_tls</c> words it (2026-11-17, the review's #63):
    /// the fake records a name mismatch in the hop's verdict, as the real callback would, then throws as the handshake does.
    /// </summary>
    [Fact]
    public async Task AnUntrustedCertificate_IsAnAnswer_NotARefusal()
    {
        HttpMessageHandler Failing(TlsVerdict verdict) => new StubHttpMessageHandler().Map("https://example.com/", (_, _) =>
        {
            verdict.Errors = System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch;
            throw new HttpRequestException(HttpRequestError.SecureConnectionError, "The SSL connection could not be established.", new AuthenticationException("The remote certificate is invalid"));
        });
        string answer = await Call(new NetHttpTool(Access((_, verdict) => Failing(verdict)), new NetworkBudget()), ("url", "https://example.com/"));
        Assert.StartsWith("HTTP https://example.com/: " + NetworkText.HttpUntrusted("name mismatch", command: false) + " (1 request, ", answer);
        Assert.DoesNotContain("Error:", answer);
        var command = await NetworkCommand.RunAsync(Access((_, verdict) => Failing(verdict)), "http https://example.com/", CancellationToken.None);
        Assert.Contains(command.Lines, l => l.Contains("; /net tls shows it", StringComparison.Ordinal));
    }

    /// <summary>A handshake that failed with nothing recorded by the callback is a TLS failure, not an untrusted certificate (#63).</summary>
    [Fact]
    public async Task AHandshakeFailure_WithNoCertificateVerdict_IsATlsFailure()
    {
        var failing = new StubHttpMessageHandler().Map("https://example.com/", (_, _) =>
            throw new HttpRequestException(HttpRequestError.SecureConnectionError, "The SSL connection could not be established.", new AuthenticationException("no shared cipher")));
        string answer = await Call(new NetHttpTool(Access((_, _) => failing), new NetworkBudget()), ("url", "https://example.com/"));
        Assert.Contains("\n  1. GET https://example.com/ → the TLS handshake failed: ⟪no shared cipher⟫", answer);
        Assert.EndsWith(UntrustedText.QuoteNote, answer);
    }

    [Fact]
    public async Task ARefusedConnection_IsNoAnswer()
    {
        var failing = new StubHttpMessageHandler().Map("https://example.com/", (_, _) =>
            throw new HttpRequestException(HttpRequestError.ConnectionError, "refused", new System.Net.Sockets.SocketException(10061)));
        string answer = await Call(new NetHttpTool(Access((_, _) => failing), new NetworkBudget()), ("url", "https://example.com/"));
        Assert.Contains("\n  1. GET https://example.com/ → no answer: ", answer);
    }

    [Fact]
    public async Task RetryAfter_OnlyOn429And503_AndAuthenticateByScheme()
    {
        _stub.Map("https://example.com/", (_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            response.Headers.TryAddWithoutValidation("Retry-After", "120");
            response.Headers.TryAddWithoutValidation("WWW-Authenticate", "Bearer realm=\"secret stuff\"");
            return Task.FromResult(response);
        });
        string answer = await Call(new NetHttpTool(Access(), new NetworkBudget()), ("url", "https://example.com/"));
        Assert.Contains("\n  retry-after: ⟪120⟫", answer);
        Assert.Contains("\n  www-authenticate: ⟪Bearer⟫", answer);
        Assert.DoesNotContain("secret stuff", answer);
    }

    [Fact]
    public async Task Command_NetHttp_ParsesItsWords_AndIsTheUsersHand()
    {
        _settings.NetworkDeniedHosts = ["example.com"];
        _stub.Map("https://example.com/", (_, _) => Task.FromResult(Redirect("https://www.example.com/")));
        var result = await NetworkCommand.RunAsync(Access(), "http example.com nofollow head", CancellationToken.None);
        Assert.False(result.Failed);
        Assert.Contains(result.Lines, l => l.StartsWith("  1. HEAD https://example.com/ → 301", StringComparison.Ordinal));
        Assert.NotNull(result.Note);   // the user's own hand: run, and told what the model would meet
        Assert.True((await NetworkCommand.RunAsync(Access(), "http example.com sideways", CancellationToken.None)).Failed);
        Assert.Contains("http", NetworkText.Verbs);
        Assert.Contains("http <url> [head] [browser] [nofollow]", NetworkText.Usage);
        Assert.Equal(["http x browser", "http x head", "http x nofollow"], NetworkCommand.Complete("http x ").Select(c => c.Text).Order(StringComparer.Ordinal));
    }

    /// <summary>A Location with <c>://</c> in its query is relative, and followed (2026-11-17, #3): only a scheme at its start makes it absolute.</summary>
    [Fact]
    public async Task ARelativeLocation_WithASchemeInItsQuery_IsFollowed()
    {
        _stub.Map("https://example.com/login?next=", (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        _stub.Map("https://example.com/", (_, _) => Task.FromResult(Redirect("/login?next=https://other.example/")));
        string answer = await Call(new NetHttpTool(Access(), new NetworkBudget()), ("url", "https://example.com/"));
        Assert.StartsWith("HTTP https://example.com/: 200 (2 requests, ", answer);
        Assert.Equal(["example.com", "example.com"], _targets.Select(t => t.Host));
    }

    /// <summary>
    /// A typed <c>host:port</c> is a host with a port, not a scheme (2026-11-17, #3, #206), while a Location is read by the scheme pattern
    /// alone: <c>mailto:</c> stops as not HTTP, the server's text fenced (#19); <c>&lt;https://x&gt;</c> reads as web_fetch reads it (#166).
    /// </summary>
    [Fact]
    public void ParseUrl_HostsWithPorts_SchemesInLocations_AndAngleBrackets()
    {
        Assert.Equal("https://example.com:8080/", HttpProbe.ParseUrl("example.com:8080").Url!.AbsoluteUri);
        Assert.Equal("https://localhost:8080/x", HttpProbe.ParseUrl("localhost:8080/x").Url!.AbsoluteUri);
        Assert.Equal("https://example.com/", HttpProbe.ParseUrl("<https://example.com/>").Url!.AbsoluteUri);
        var base_ = new Uri("https://example.com/");
        Assert.Equal(NetworkText.LocationNotHttp("mailto:x@example.com"), HttpProbe.ParseUrl("mailto:x@example.com", base_).Error);
        Assert.Contains(UntrustedText.QuoteOpen, NetworkText.LocationNotHttp("mailto:x@example.com"), StringComparison.Ordinal);
        Assert.Equal(NetworkText.RedirectUserInfo, HttpProbe.ParseUrl("https://user:pw@example.com/", base_).Error);
        Assert.Equal(NetworkText.UrlUserInfo, HttpProbe.ParseUrl("https://user:pw@example.com/").Error);
    }

    [Fact]
    public async Task AMailtoLocation_StopsTheChain_Fenced()
    {
        _stub.Map("https://example.com/", (_, _) => Task.FromResult(Redirect("mailto:x@example.com")));
        string answer = await Call(new NetHttpTool(Access(), new NetworkBudget()), ("url", "https://example.com/"));
        Assert.Contains("; " + NetworkText.HttpNotFollowedBecause(NetworkText.WithoutError(NetworkText.LocationNotHttp("mailto:x@example.com"))), answer);
        Assert.Single(_stub.Requests);
    }

    /// <summary>A Location .NET can't parse is read as sent (#36), still a redirect, and refused by name rather than read as no Location.</summary>
    [Fact]
    public async Task AnUnparsableLocation_IsStillARedirect_RefusedByName()
    {
        _stub.Map("https://example.com/", (_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.TryAddWithoutValidation("Location", "http://exa mple.com/x");
            return Task.FromResult(response);
        });
        string answer = await Call(new NetHttpTool(Access(), new NetworkBudget()), ("url", "https://example.com/"));
        Assert.Contains(" → ⟪http://exa mple.com/x⟫; redirect not followed: the server redirected to ", answer);
        Assert.Single(_stub.Requests);
    }

    /// <summary>
    /// <c>/net http</c>'s later hops meet the reach and the lists (2026-11-17, #121, the user's call): a redirect into the LAN under an
    /// <c>internet</c> reach is refused, while the typed address isn't; and they aren't counted against the turn's hosts.
    /// </summary>
    [Fact]
    public async Task Command_LaterHops_AreJudged_ButNotCounted()
    {
        _settings.NetworkToolsReach = "internet";
        _stub.Map("https://example.com/", (_, _) => Task.FromResult(Redirect("http://nas.lan/admin")));
        var lan = await NetworkCommand.RunAsync(Access(), "http https://example.com/", CancellationToken.None);
        Assert.Contains(lan.Lines, l => l.StartsWith("  2. GET ⟪http://nas.lan/admin⟫ → refused: ", StringComparison.Ordinal));
        Assert.Single(_targets);

        _settings.NetworkToolsReach = "both";
        _settings.NetworkMaxHostsPerTurn = AppSettingsData.MinNetworkMaxHosts;
        _names["a.example"] = [IPAddress.Parse("93.184.216.1")];
        _names["b.example"] = [IPAddress.Parse("93.184.216.2")];
        _names["c.example"] = [IPAddress.Parse("93.184.216.3")];
        _stub.Map("https://a.example/", (_, _) => Task.FromResult(Redirect("https://b.example/")));
        _stub.Map("https://b.example/", (_, _) => Task.FromResult(Redirect("https://c.example/")));
        _stub.Map("https://c.example/", (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var hosts = await NetworkCommand.RunAsync(Access(), "http https://a.example/", CancellationToken.None);
        Assert.StartsWith("HTTP https://a.example/: 200 (3 requests, ", hosts.Lines[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// A port browsers refuse (2026-11-17, #121): refused on a redirect and on a model's first hop; a port the user typed into <c>/net http</c>
    /// is kept.
    /// </summary>
    [Fact]
    public async Task BlockedPorts_AreRefused_ButTheUsersTypedPortIsKept()
    {
        _stub.Map("https://example.com/", (_, _) => Task.FromResult(Redirect("http://example.com:25/")));
        string redirected = await Call(new NetHttpTool(Access(), new NetworkBudget()), ("url", "https://example.com/"));
        Assert.Contains("\n  2. GET ⟪http://example.com:25/⟫ → " + NetworkText.HopBadPort(25), redirected);
        Assert.StartsWith("HTTP https://example.com/: 301, then " + NetworkText.HopBadPort(25) + " (1 request, ", redirected);
        Assert.Single(_stub.Requests);

        Assert.Equal(NetworkText.BadPort(25), await Call(new NetHttpTool(Access(), new NetworkBudget()), ("url", "http://example.com:25/")));
        _stub.Map("http://example.com:25/", (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var typed = await NetworkCommand.RunAsync(Access(), "http http://example.com:25/", CancellationToken.None);
        Assert.StartsWith("HTTP http://example.com:25/: 200 ", typed.Lines[0], StringComparison.Ordinal);
        Assert.Equal(83, HttpProbe.BadPorts.Count);
    }

    /// <summary>
    /// A slow lookup counts against the hop's budget, on the app's clock (2026-11-17, #4, #71): the hop says it ran out, sends nothing, and the
    /// check answers rather than throws.
    /// </summary>
    [Fact]
    public async Task ASlowLookup_EndsInAHopLine_OnTheAppsClock()
    {
        var looking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<IPAddress[]> Slow(string host, CancellationToken cancellationToken)
        {
            looking.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return [];
        }

        var call = NetworkChecks.HttpAsync(Access(resolve: Slow), new NetworkBudget(), "https://slow.example/", head: false, follow: true, browser: false, CancellationToken.None);
        await looking.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _time.Advance(HttpProbe.HopTimeout);
        var answer = await call.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.StartsWith("HTTP https://slow.example/: " + NetworkText.HttpTimedOut(HttpProbe.HopTimeout) + " (0 requests, ", answer.Text);
    }

    /// <summary>A judged note shows once, however many hops meet it (2026-11-17, #29).</summary>
    [Fact]
    public async Task Command_TheModelsNote_ShowsOnce()
    {
        _settings.NetworkDeniedHosts = ["example.com"];
        _stub.Map("https://example.com/", (_, _) => Task.FromResult(Redirect("/next")));
        var result = await NetworkCommand.RunAsync(Access(), "http https://example.com/", CancellationToken.None);
        Assert.NotNull(result.Note);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(result.Note!, System.Text.RegularExpressions.Regex.Escape(NetworkText.ModelWouldBeRefused("x")[..12])));
    }

    /// <summary>
    /// A failed last hop shows no earlier hop's headers (2026-11-17, #47), and a Location beside a status outside 3xx isn't drawn as a hop
    /// (#208).
    /// </summary>
    [Fact]
    public async Task AFailedLastHop_ShowsNoOldHeaders_AndOnlyARedirectShowsWhereItGoes()
    {
        _stub.Map("https://www.example.com/", (_, _) => throw new HttpRequestException(HttpRequestError.ConnectionError, "reset"));
        _stub.Map("https://example.com/", (_, _) =>
        {
            var response = Redirect("https://www.example.com/");
            response.Headers.TryAddWithoutValidation("Server", "first-hop");
            return Task.FromResult(response);
        });
        string failed = await Call(new NetHttpTool(Access(), new NetworkBudget()), ("url", "https://example.com/"));
        Assert.DoesNotContain("first-hop", failed);

        _stub.Map("https://cdn.example.net/", (_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Created);
            response.Headers.Location = new Uri("https://cdn.example.net/thing/1");
            return Task.FromResult(response);
        });
        string created = await Call(new NetHttpTool(Access(), new NetworkBudget()), ("url", "https://cdn.example.net/"));
        Assert.DoesNotContain("thing/1", created);
    }

    /// <summary>Completion leaves out a flag already typed (2026-11-17, #54).</summary>
    [Fact]
    public void Command_Completion_LeavesOutTypedFlags()
    {
        Assert.Equal(["http x head browser", "http x head nofollow"], NetworkCommand.Complete("http x head ").Select(c => c.Text).Order(StringComparer.Ordinal));
    }

    /// <summary>The browser mode sends no Referer, <c>Sec-Fetch-Site: none</c> on every hop, and asks for the body as is (#53, #215).</summary>
    [Fact]
    public async Task Browser_EveryHop_IsAUserNavigation_WithNoReferer()
    {
        var seen = new List<(string? Site, bool Referer, string Encoding)>();
        HttpResponseMessage Note(HttpRequestMessage request, HttpResponseMessage response)
        {
            seen.Add((request.Headers.TryGetValues("Sec-Fetch-Site", out var site) ? site.Single() : null, request.Headers.Contains("Referer"),
                string.Join(",", request.Headers.GetValues("Accept-Encoding"))));
            return response;
        }

        _stub.Map("https://www.example.com/", (request, _) => Task.FromResult(Note(request, new HttpResponseMessage(HttpStatusCode.OK))));
        _stub.Map("https://example.com/", (request, _) => Task.FromResult(Note(request, Redirect("https://www.example.com/"))));
        await Call(new NetHttpTool(Access(), new NetworkBudget()), ("url", "https://example.com/"), ("browser", true));
        Assert.Equal([("none", false, "identity"), ("none", false, "identity")], seen);
    }

    [Fact]
    public void Registration_OfferedAsTheOtherNetworkToolsAre()
    {
        Assert.Contains(NetHttpTool.ToolName, App.ChatScreen.NetworkToolNames);
        Assert.Contains(App.ChatScreen.NetworkTools(Access(), new NetworkBudget()), t => t.Name == NetHttpTool.ToolName);
        Assert.Contains(NetHttpTool.ToolName, Plans.PlanTools.ReadOnly);
        Assert.Contains(NetHttpTool.ToolName, Assistant.NetworkRule);
        Assert.Contains(NetHttpTool.ToolName, Assistant.NetworkRuleNoEcho);
    }
}
