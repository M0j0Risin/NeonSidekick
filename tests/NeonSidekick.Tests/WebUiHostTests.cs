using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NeonSidekick.Diagnostics;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The Web UI's server (2026-10-14): a real Kestrel on loopback port 0 over a fake backend — the cookie gate, the sign-in, the headers, the
/// posts' cross-site guard, the event stream, the static files.
/// </summary>
public sealed class WebUiHostTests : IAsyncLifetime
{
    private readonly FakeWebUiBackend _backend = new();
    private readonly string _token = WebUiAuth.NewToken();
    private WebUiHost _host = null!;
    private HttpClient _client = null!;
    private Func<IPAddress, bool> _allow = _ => true;

    public async Task InitializeAsync() => await StartAsync(WebUiAudience.Local).ConfigureAwait(false);

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.DisposeAsync().ConfigureAwait(false);
    }

    private async Task StartAsync(WebUiAudience audience, string? token = null)
    {
        _host = new WebUiHost(_backend, Path.GetTempPath(), allowRemote: address => _allow(address));
        await _host.StartAsync(new WebUiBinding(audience, 0, token ?? _token, IPAddress.Loopback), CancellationToken.None).ConfigureAwait(false);
        _client = new HttpClient(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false })
        {
            BaseAddress = new Uri("http://127.0.0.1:" + _host.Port + "/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    private string Cookie => WebUiAuth.CookieName(_host.Port) + "=" + _token;

    private HttpRequestMessage Get(string path, bool signedIn = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (signedIn)
        {
            request.Headers.Add("Cookie", Cookie);
        }

        return request;
    }

    private HttpRequestMessage Post(string path, string json, bool csrf = true, string type = "application/json", bool signedIn = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(json, Encoding.UTF8) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(type);
        if (csrf)
        {
            request.Headers.Add(WebUiHost.CsrfHeader, "1");
        }

        if (signedIn)
        {
            request.Headers.Add("Cookie", Cookie);
        }

        return request;
    }

    [Fact]
    public async Task Health_AnswersWithoutTheCookie()
    {
        Assert.Equal("ok", await _client.GetStringAsync("/health"));
    }

    [Fact]
    public async Task WithoutTheCookie_APageIsTheSignInPage_AndAnApiIs401()
    {
        using var page = await _client.SendAsync(Get("/", signedIn: false));
        string html = await page.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("/webui", html, StringComparison.Ordinal);
        Assert.DoesNotContain("chat.js", html, StringComparison.Ordinal);

        using var api = await _client.SendAsync(Get("/api/shopping", signedIn: false));
        Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
        Assert.Contains(WebUiText.SignInNeeded, await api.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSignIn_SetsAPerPortCookie_LaxHttpOnlyForAYear_AndLeavesTheAddress()
    {
        var messages = new List<string>();
        void Record(DiagnosticEvent e) => messages.Add(e.Message);
        DiagnosticLog.Emitted += Record;
        try
        {
            using var response = await _client.GetAsync(WebUiAuth.SignInPath + "?t=" + _token);

            Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
            Assert.Equal("/", response.Headers.Location?.OriginalString);
            string cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
            Assert.StartsWith(WebUiAuth.CookieName(_host.Port) + "=" + _token + ";", cookie, StringComparison.Ordinal);
            Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("max-age=31536000", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Single(_backend.SignIns);
        }
        finally
        {
            DiagnosticLog.Emitted -= Record;
        }

        Assert.DoesNotContain(messages, m => m.Contains(_token, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0000")]
    public async Task AWrongToken_IsRefused(string token)
    {
        using var response = await _client.GetAsync(WebUiAuth.SignInPath + "?t=" + token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Empty(_backend.SignIns);
    }

    [Fact]
    public async Task AnEmptyToken_NeverSignsIn()
    {
        await _host.DisposeAsync();
        _client.Dispose();
        await StartAsync(WebUiAudience.Local, token: "");

        using var response = await _client.GetAsync(WebUiAuth.SignInPath + "?t=");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task EveryResponse_CarriesThePolicy_AndNoServerName()
    {
        using var response = await _client.GetAsync("/health");

        Assert.Contains("frame-ancestors 'none'", Assert.Single(response.Headers.GetValues("Content-Security-Policy")), StringComparison.Ordinal);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.False(response.Headers.Contains("Server"));
    }

    [Fact]
    public async Task APost_WithoutTheHeaderOrJson_IsRefused()
    {
        using var noHeader = await _client.SendAsync(Post("/api/send", "{\"text\":\"hi\"}", csrf: false));
        using var form = await _client.SendAsync(Post("/api/send", "text=hi", type: "application/x-www-form-urlencoded"));

        Assert.Equal(HttpStatusCode.Forbidden, noHeader.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, form.StatusCode);
        Assert.Empty(_backend.Sent);
    }

    [Fact]
    public async Task ABodyOverTheCap_Is413()
    {
        string text = new('x', (int)WebUiHost.MaxBodyBytes + 10);
        using var response = await _client.SendAsync(Post("/api/send", "{\"text\":\"" + text + "\"}"));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task ASend_ReachesTheBackend_AndItsOutcomeIsTheStatus()
    {
        using var ok = await _client.SendAsync(Post("/api/send", "{\"id\":\"a1\",\"text\":\"hi\"}"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.True(_backend.Sent.TryDequeue(out var sent));
        Assert.Equal(("a1", "hi"), (sent.Id, sent.Text));

        _backend.Reply = new WebReply(WebOutcome.Refused, WebUiText.SlashRefused);
        using var refused = await _client.SendAsync(Post("/api/send", "{\"text\":\"/clear\"}"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Contains(WebUiText.SlashRefused, await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        _backend.Reply = new WebReply(WebOutcome.TooMany, "full");
        using var full = await _client.SendAsync(Post("/api/send", "{\"text\":\"x\"}"));
        Assert.Equal(HttpStatusCode.TooManyRequests, full.StatusCode);

        using var bad = await _client.SendAsync(Post("/api/send", "{not json"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Stop_Answer_AndTheShoppingList_ReachTheBackend()
    {
        using var stop = await _client.SendAsync(Post("/api/stop", "{}"));
        using var answer = await _client.SendAsync(Post("/api/answer", "{\"askId\":\"a1\",\"choice\":\"once\"}"));
        using var check = await _client.SendAsync(Post("/api/shopping/check", "{\"id\":7,\"on\":true}"));
        using var list = await _client.SendAsync(Get("/api/shopping"));

        Assert.Equal(1, _backend.Stops);
        Assert.True(_backend.Answers.TryDequeue(out var a) && a.AskId == "a1" && a.Choice == "once");
        Assert.True(_backend.Checks.TryDequeue(out var c) && c.Id == 7 && c.On);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Contains("\"tickedHeading\":\"Ticked\"", await list.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal("no-store", list.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task UnderLan_AnAddressThatIsNotPrivate_IsRefused()
    {
        await _host.DisposeAsync();
        _client.Dispose();
        _allow = _ => false;
        await StartAsync(WebUiAudience.LocalAreaNetwork);

        using var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task EveryFileThePagesName_IsServed_WithItsTypeAndAnETag()
    {
        foreach (string name in WebUiAssets.Names)
        {
            var asset = WebUiAssets.Find(name);
            Assert.NotNull(asset);
            Assert.NotEmpty(asset.Bytes);
        }

        foreach (string page in new[] { WebUiAssets.ChatPage, WebUiAssets.ShoppingPage, WebUiAssets.SignInPage })
        {
            string html = Encoding.UTF8.GetString(WebUiAssets.Find(page)!.Bytes);
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(html, "(?:src|href)=\"/([a-z]+\\.[a-z]+)\""))
            {
                Assert.Contains(match.Groups[1].Value, WebUiAssets.Names);
            }
        }

        using var css = await _client.GetAsync("/app.css");
        Assert.Equal("text/css; charset=utf-8", css.Content.Headers.ContentType?.ToString());
        var etag = css.Headers.ETag;
        Assert.NotNull(etag);
        using var again = new HttpRequestMessage(HttpMethod.Get, "/app.css");
        again.Headers.IfNoneMatch.Add(etag);
        using var notModified = await _client.SendAsync(again);
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);

        using var chat = await _client.SendAsync(Get("/"));
        Assert.Contains("chat.js", await chat.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var shopping = await _client.SendAsync(Get("/shopping"));
        Assert.Contains("shopping.js", await shopping.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheHost_NeverTouchesTheConsole_ItsLifetimeIsANoOp()
    {
        Assert.IsType<WebUiLifetime>(_host.Services!.GetRequiredService<IHostLifetime>());
    }

    [Fact]
    public async Task TheEventStream_OpensWithASnapshot_CarriesWhatIsPublished_AndEndsWhenTheServerStops()
    {
        using var response = await _client.SendAsync(Get("/api/events"), HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());

        Assert.Equal("event: snapshot", await reader.ReadLineAsync());
        Assert.Contains("\"hello\"", await reader.ReadLineAsync() ?? "", StringComparison.Ordinal);
        Assert.Equal("", await reader.ReadLineAsync());

        _backend.Events.Publish(new WebEvent("state", () => "{\"x\":1}"));
        Assert.Equal("event: state", await reader.ReadLineAsync());
        Assert.Equal("data: {\"x\":1}", await reader.ReadLineAsync());
        Assert.Equal("", await reader.ReadLineAsync());

        var stopping = _host.StopAsync();
        Assert.Null(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)));
        await stopping.WaitAsync(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task OneStreamPastTheCap_ClosesTheOldest()
    {
        var responses = new List<HttpResponseMessage>();
        var readers = new List<StreamReader>();
        try
        {
            for (int i = 0; i <= WebUiHost.MaxStreams; i++)
            {
                var response = await _client.SendAsync(Get("/api/events"), HttpCompletionOption.ResponseHeadersRead);
                responses.Add(response);
                var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
                readers.Add(reader);
                Assert.Equal("event: snapshot", await reader.ReadLineAsync());
            }

            // The first stream ends: past its snapshot's two lines, the end of the stream.
            await readers[0].ReadLineAsync();
            await readers[0].ReadLineAsync();
            Assert.Null(await readers[0].ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)));
            Assert.Equal(WebUiHost.MaxStreams, _host.OpenStreams);
        }
        finally
        {
            readers.ForEach(r => r.Dispose());
            responses.ForEach(r => r.Dispose());
        }
    }
}
