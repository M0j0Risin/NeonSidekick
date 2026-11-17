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

    private NetworkAccess Access(Func<NetworkTarget, HttpMessageHandler>? http = null) =>
        new(() => _settings, null, _time, (host, _) => _names.TryGetValue(host, out var a) ? Task.FromResult(a) : throw new System.Net.Sockets.SocketException(11001),
            http: http ?? (target =>
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
        Assert.StartsWith("HTTP https://example.com/: 200 (1 request, 0 ms)\n", answer);
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

    [Fact]
    public async Task AnUntrustedCertificate_IsAnAnswer_NotARefusal()
    {
        var failing = new StubHttpMessageHandler().Map("https://example.com/", (_, _) =>
            throw new HttpRequestException(HttpRequestError.SecureConnectionError, "The SSL connection could not be established.", new AuthenticationException("The remote certificate is invalid: RemoteCertificateChainErrors")));
        string answer = await Call(new NetHttpTool(Access(_ => failing), new NetworkBudget()), ("url", "https://example.com/"));
        Assert.StartsWith("HTTP https://example.com/: the server answered over TLS, but its certificate isn't trusted: ", answer);
        Assert.Contains("; net_tls shows it", answer);
        Assert.DoesNotContain("Error:", answer);
    }

    [Fact]
    public async Task ARefusedConnection_IsNoAnswer()
    {
        var failing = new StubHttpMessageHandler().Map("https://example.com/", (_, _) =>
            throw new HttpRequestException(HttpRequestError.ConnectionError, "refused", new System.Net.Sockets.SocketException(10061)));
        string answer = await Call(new NetHttpTool(Access(_ => failing), new NetworkBudget()), ("url", "https://example.com/"));
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
