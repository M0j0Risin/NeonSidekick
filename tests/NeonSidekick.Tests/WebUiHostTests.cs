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
public sealed partial class WebUiHostTests : IAsyncLifetime
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

        _backend.Reply = new WebReply(WebOutcome.Refused, WebUiText.UnknownCommand("//tools"), AsText: true, Use: "/tools");
        using var refused = await _client.SendAsync(Post("/api/send", "{\"text\":\"//tools\"}"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        string body = await refused.Content.ReadAsStringAsync();
        Assert.Contains(WebUiText.UnknownCommand("//tools"), body, StringComparison.Ordinal);
        Assert.Contains("\"asText\":true", body, StringComparison.Ordinal);
        Assert.Contains("\"use\":\"/tools\"", body, StringComparison.Ordinal);

        using var asText = await _client.SendAsync(Post("/api/send", "{\"text\":\"/usr/bin\",\"asText\":true}"));
        Assert.True(_backend.Sent.TryDequeue(out _));
        Assert.True(_backend.Sent.TryDequeue(out var sentAsText) && sentAsText.AsText);

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

        foreach (string page in new[] { WebUiAssets.ChatPage, WebUiAssets.ShoppingPage, WebUiAssets.HomePage, WebUiAssets.SignInPage })
        {
            string html = Encoding.UTF8.GetString(WebUiAssets.Find(page)!.Bytes);
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(html, "(?:src|href)=\"/([a-z0-9-]+\\.[a-z]+)\""))
            {
                // The theme's sheet and the manifest are made per request (2026-10-15), not files.
                if (match.Groups[1].Value is not ("theme.css" or "manifest.webmanifest"))
                {
                    Assert.Contains(match.Groups[1].Value, WebUiAssets.Names);
                }
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
    public async Task APickedStore_ReachesTheBackend_BehindTheCookieAndTheHeader()
    {
        using var ok = await _client.SendAsync(Post("/api/shopping/store", "{\"id\":7}"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(7, Assert.Single(_backend.Stores).Id);

        _backend.Reply = new WebReply(WebOutcome.NotFound, WebUiText.NoSuchStore);
        using var gone = await _client.SendAsync(Post("/api/shopping/store", "{\"id\":8}"));
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Contains(WebUiText.NoSuchStore, await gone.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var forged = await _client.SendAsync(Post("/api/shopping/store", "{\"id\":9}", csrf: false));
        Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
        using var signedOut = await _client.SendAsync(Post("/api/shopping/store", "{\"id\":9}", signedIn: false));
        Assert.Equal(HttpStatusCode.Unauthorized, signedOut.StatusCode);
        Assert.Equal(2, _backend.Stores.Count);
    }

    /// <summary>The Home tab's side for the host's tests (2026-10-15): fixed answers, every call recorded.</summary>
    private sealed class FakeHome : IWebHome
    {
        public WebHome Tab { get; set; } = new("live", null, true, [new WebHomeTile("entity:light.desk", "💡", "Desk", "", "", "call", null, "off", null, 0, true)], [], null, null,
            new WebHomeModel("m", "host", 8000, false, false, null), new WebPulse(false, 0, null, "", "tag"), WebUiText.HomeWords);

        public List<string> Calls { get; } = [];

        public WebReply Next { get; set; } = WebReply.Ok;

        public WebHome Read() => Tab;

        public WebHomeSheet? Sheet(string reference) => reference == "entity:light.desk" ? new WebHomeSheet(reference, "Desk", "off", []) : null;

        public WebReply Tap(WebHomeTapRequest request, IPAddress? remote) { Calls.Add("tap " + request.Ref + " " + request.Confirmed); return Next; }

        public WebReply Act(WebHomeActRequest request, IPAddress? remote) { Calls.Add("act " + request.Entity); return Next; }

        public WebReply Scene(WebHomeSceneRequest request, IPAddress? remote) { Calls.Add("scene " + request.Id); return Next; }

        public Task<WebReply> SayAsync(WebHomeSayRequest request, IPAddress? remote, CancellationToken cancellationToken) { Calls.Add("say " + request.Text); return Task.FromResult(new WebReply(WebOutcome.Ok, "Done.")); }

        public WebReply Video(WebHomeVideoRequest request, IPAddress? remote) { Calls.Add("video " + request.Action); return Next; }
    }

    [Fact]
    public async Task TheHomePage_IsBehindTheCookie_AndItsReadIsA304WhenUnchanged()
    {
        var home = new FakeHome();
        _backend.Home = home;
        using var signedOut = await _client.SendAsync(Get("/home", signedIn: false));
        Assert.DoesNotContain("home.js", await signedOut.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var page = await _client.SendAsync(Get("/home"));
        Assert.Contains("home.js", await page.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var script = await _client.GetAsync("/home.js");
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);

        using var first = await _client.SendAsync(Get("/api/home"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Contains("\"ref\":\"entity:light.desk\"", await first.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var again = Get("/api/home");
        again.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        using var unchanged = await _client.SendAsync(again);
        Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);

        using var sheet = await _client.SendAsync(Get("/api/home/sheet?ref=entity%3Alight.desk"));
        Assert.Equal(HttpStatusCode.OK, sheet.StatusCode);
        using var outside = await _client.SendAsync(Get("/api/home/sheet?ref=entity%3Alock.back"));
        Assert.Equal(HttpStatusCode.NotFound, outside.StatusCode);
        using var pulse = await _client.SendAsync(Get("/api/pulse"));
        Assert.Equal(HttpStatusCode.OK, pulse.StatusCode);
    }

    [Fact]
    public async Task HomePosts_ReachTheTab_TheirAskAndRefusalsAsJson()
    {
        var home = new FakeHome();
        _backend.Home = home;

        home.Next = new WebReply(WebOutcome.Conflict, null, Prompt: "Unlock Front Door?");
        using var ask = await _client.SendAsync(Post("/api/home/tap", "{\"ref\":\"entity:lock.front\"}"));
        Assert.Equal(HttpStatusCode.Conflict, ask.StatusCode);
        string json = await ask.Content.ReadAsStringAsync();
        Assert.Contains("\"needsConfirm\":true", json, StringComparison.Ordinal);
        Assert.Contains("Unlock Front Door?", json, StringComparison.Ordinal);

        home.Next = new WebReply(WebOutcome.Forbidden, WebUiText.HomeControlsOff);
        using var off = await _client.SendAsync(Post("/api/home/video", "{\"action\":\"play\"}"));
        Assert.Equal(HttpStatusCode.Forbidden, off.StatusCode);

        using var said = await _client.SendAsync(Post("/api/home/say", "{\"text\":\"lights?\"}"));
        Assert.Contains("Done.", await said.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var forged = await _client.SendAsync(Post("/api/home/scene", "{\"id\":\"scene.x\"}", csrf: false));
        Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
        Assert.Equal(["tap entity:lock.front False", "video play", "say lights?"], home.Calls);

        _backend.Home = null;
        using var none = await _client.SendAsync(Get("/api/home"));
        Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);
    }

    [Fact]
    public async Task TheUsageCard_IsServedSignedIn_And401Otherwise()
    {
        using var signedOut = await _client.SendAsync(Get("/api/usage", signedIn: false));
        Assert.Equal(HttpStatusCode.Unauthorized, signedOut.StatusCode);

        using var card = await _client.SendAsync(Get("/api/usage"));
        Assert.Equal(HttpStatusCode.OK, card.StatusCode);
        var read = System.Text.Json.JsonSerializer.Deserialize(await card.Content.ReadAsStringAsync(), WebUiJsonContext.Default.WebUsageCard)!;
        Assert.Equal(_backend.Card.Lines, read.Lines);
    }

    [Fact]
    public async Task TheThemesSheet_TheManifest_AndTheIcons_NeedNoCookie_AndRevalidate()
    {
        using var css = await _client.GetAsync("/theme.css");
        Assert.Equal(HttpStatusCode.OK, css.StatusCode);
        Assert.Equal("text/css; charset=utf-8", css.Content.Headers.ContentType?.ToString());
        Assert.Equal(_backend.Css, await css.Content.ReadAsStringAsync());
        Assert.Equal("no-cache", css.Headers.CacheControl?.ToString());
        using var again = new HttpRequestMessage(HttpMethod.Get, "/theme.css");
        again.Headers.IfNoneMatch.Add(css.Headers.ETag!);
        using var same = await _client.SendAsync(again);
        Assert.Equal(HttpStatusCode.NotModified, same.StatusCode);

        _backend.Css = "";   // Web UI profile theme off: an empty sheet, its own ETag
        using var off = await _client.GetAsync("/theme.css");
        Assert.Equal("\"default\"", off.Headers.ETag?.ToString());
        Assert.Equal("", await off.Content.ReadAsStringAsync());

        using var manifest = await _client.GetAsync("/manifest.webmanifest");
        Assert.Equal("application/manifest+json; charset=utf-8", manifest.Content.Headers.ContentType?.ToString());
        Assert.Contains("\"display\": \"standalone\"", await manifest.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        foreach (string icon in new[] { "/icon-192.png", "/icon-512.png", "/apple-touch-icon.png" })
        {
            using var png = await _client.GetAsync(icon);
            Assert.Equal("image/png", png.Content.Headers.ContentType?.ToString());
            byte[] bytes = await png.Content.ReadAsByteArrayAsync();
            Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], bytes[..4]);
        }

        using var script = await _client.GetAsync("/signin.js");
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
    }

    [Fact]
    public async Task APastedSignIn_TakesTheLinkOrTheToken_NeedsTheHeader_AndNeverEchoesIt()
    {
        string link = WebUiAuth.SignInLink("127.0.0.1", _host.Port, _token);
        using var byLink = await _client.SendAsync(Post("/signin", "{\"link\":\"" + link + "\"}", signedIn: false));
        Assert.Equal(HttpStatusCode.OK, byLink.StatusCode);
        Assert.StartsWith(WebUiAuth.CookieName(_host.Port) + "=" + _token + ";", Assert.Single(byLink.Headers.GetValues("Set-Cookie")), StringComparison.Ordinal);

        using var byToken = await _client.SendAsync(Post("/signin", "{\"link\":\" " + _token + " \"}", signedIn: false));
        Assert.Equal(HttpStatusCode.OK, byToken.StatusCode);

        using var wrong = await _client.SendAsync(Post("/signin", "{\"link\":\"not-it\"}", signedIn: false));
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        string said = await wrong.Content.ReadAsStringAsync();
        Assert.Equal(WebUiText.SignInWrong, System.Text.Json.JsonSerializer.Deserialize(said, WebUiJsonContext.Default.WebResult)!.Message);
        Assert.DoesNotContain("not-it", said, StringComparison.Ordinal);

        using var forged = await _client.SendAsync(Post("/signin", "{\"link\":\"" + _token + "\"}", csrf: false, signedIn: false));
        Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
        Assert.False(forged.Headers.Contains("Set-Cookie"));
        Assert.Equal(2, _backend.SignIns.Count);
    }

    [Fact]
    public async Task ADeferredEntry_IsReadWhole_WhileTheTranscriptHoldsIt()
    {
        _backend.Entries[7] = new WebEntry(7, "thinking", "the whole of it", null, null, false) { Summary = "💭 thought for 1.0s", Ended = true };
        using var found = await _client.SendAsync(Get("/api/entry/7"));
        Assert.Equal(HttpStatusCode.OK, found.StatusCode);
        Assert.Contains("the whole of it", await found.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var gone = await _client.SendAsync(Get("/api/entry/8"));
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        using var signedOut = await _client.SendAsync(Get("/api/entry/7", signedIn: false));
        Assert.Equal(HttpStatusCode.Unauthorized, signedOut.StatusCode);
    }

    [Fact]
    public async Task APostPastItsLimit_Is429_WithRetryAfter_AndTheAppsSentence()
    {
        // Ask the house's bucket holds one every 2 seconds: the second at once is over, whatever the route answers.
        using var first = await _client.SendAsync(Post("/api/home/say", "{\"text\":\"hi\"}"));
        Assert.NotEqual(HttpStatusCode.TooManyRequests, first.StatusCode);

        using var second = await _client.SendAsync(Post("/api/home/say", "{\"text\":\"hi\"}"));

        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.True(second.Headers.RetryAfter?.Delta >= TimeSpan.FromSeconds(1));
        Assert.Contains(WebUiText.SlowDown, await second.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal("no-store", second.Headers.CacheControl?.ToString());
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

    // ── Pictures (later on 2026-10-14) ───────────────────────────────────────

    private string PictureUrl(int id, string size, string? run = null) => WebUiHost.PicturePrefix + (run ?? _backend.PictureRun) + "/" + id + "/" + size;

    private static byte[] Png(int width = 8, int height = 4) => NeonSidekick.Files.ImageFile.Load(NeonSidekick.App.SmokeChecks.SolidBmp(width, height), "x.bmp", out _)!.Bytes;

    private string TempFile(string name, byte[] bytes)
    {
        string dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private List<DiagnosticEvent> Errors(out Action stop)
    {
        var errors = new List<DiagnosticEvent>();
        void Record(DiagnosticEvent e)
        {
            if (e.Level >= DiagnosticLevel.Warning)
            {
                lock (errors)
                {
                    errors.Add(e);
                }
            }
        }

        DiagnosticLog.Emitted += Record;
        stop = () => DiagnosticLog.Emitted -= Record;
        return errors;
    }

    [WindowsFact]
    public async Task APicture_IsServed_AtEachSize_WithAnETag_ThatAnswers304()
    {
        byte[] png = Png();
        _backend.Pictures[3] = new WebPictureSource("a.png", null, png, "image/png");

        using var thumb = await _client.SendAsync(Get(PictureUrl(3, "thumb")));
        Assert.Equal(HttpStatusCode.OK, thumb.StatusCode);
        Assert.StartsWith("image/", thumb.Content.Headers.ContentType?.MediaType, StringComparison.Ordinal);
        Assert.True(thumb.Headers.CacheControl is { Private: true, NoCache: true, NoStore: false }, thumb.Headers.CacheControl?.ToString());
        Assert.Equal("same-origin", Assert.Single(thumb.Headers.GetValues("Cross-Origin-Resource-Policy")));
        var etag = thumb.Headers.ETag;
        Assert.NotNull(etag);

        using var again = Get(PictureUrl(3, "thumb"));
        again.Headers.IfNoneMatch.Add(etag);
        using var notModified = await _client.SendAsync(again);
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);

        using var mid = await _client.SendAsync(Get(PictureUrl(3, "mid")));
        Assert.Equal(png, await mid.Content.ReadAsByteArrayAsync());   // the attachment's own bytes
        using var full = await _client.SendAsync(Get(PictureUrl(3, "full")));
        Assert.Equal("image/png", full.Content.Headers.ContentType?.MediaType);
        Assert.Equal(png, await full.Content.ReadAsByteArrayAsync());   // no file: the bytes are the original
    }

    [Fact]
    public async Task APictureWithAFile_IsServedWhole_AndAHeadSendsNoBody()
    {
        byte[] png = Png();
        string path = TempFile("photo.png", png);
        _backend.Pictures[4] = new WebPictureSource("photo.png", path, null, "image/png");

        using var full = await _client.SendAsync(Get(PictureUrl(4, "full")));
        Assert.Equal(HttpStatusCode.OK, full.StatusCode);
        Assert.Equal("image/png", full.Content.Headers.ContentType?.MediaType);
        Assert.Equal(png, await full.Content.ReadAsByteArrayAsync());
        Assert.Equal(png.Length, full.Content.Headers.ContentLength);

        using var head = new HttpRequestMessage(HttpMethod.Head, PictureUrl(4, "thumb"));
        head.Headers.Add("Cookie", Cookie);
        using var headed = await _client.SendAsync(head);
        Assert.Equal(HttpStatusCode.OK, headed.StatusCode);
        Assert.Empty(await headed.Content.ReadAsByteArrayAsync());
        Assert.NotNull(headed.Headers.ETag);
    }

    [Fact]
    public async Task AnUnknownPicture_AStaleRun_ABadSize_OrAGoneFile_Is404_AndNoErrorLine()
    {
        var errors = Errors(out var stop);
        try
        {
            _backend.Pictures[5] = new WebPictureSource("gone.png", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".png"), null, "image/png");
            _backend.Pictures[6] = new WebPictureSource("a.png", null, [1, 2, 3], "image/png");

            foreach (string url in new[] { PictureUrl(99, "thumb"), PictureUrl(6, "thumb", run: new string('f', 32)), PictureUrl(6, "huge"), PictureUrl(5, "full"), PictureUrl(5, "thumb"), WebUiHost.PicturePrefix + "x" })
            {
                using var response = await _client.SendAsync(Get(url));
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            }

            using var unsigned = await _client.SendAsync(Get(PictureUrl(6, "full"), signedIn: false));
            Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);

            using var undecodable = await _client.SendAsync(Get(PictureUrl(6, "thumb")));
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, undecodable.StatusCode);
        }
        finally
        {
            stop();
        }

        Assert.Empty(errors);
    }

    [WindowsFact]
    public async Task ATiff_IsReEncodedAsJpeg_ForTheBrowser()
    {
        var settings = new PhotoSauce.MagicScaler.ProcessImageSettings();
        Assert.True(settings.TrySetEncoderFormat("image/tiff"));
        using var tiff = new MemoryStream();
        PhotoSauce.MagicScaler.MagicImageProcessor.ProcessImage(NeonSidekick.App.SmokeChecks.SolidBmp(20, 10), tiff, settings);
        string path = TempFile("scan.tiff", tiff.ToArray());
        _backend.Pictures[7] = new WebPictureSource("scan.tiff", path, null, "image/png");

        using var full = await _client.SendAsync(Get(PictureUrl(7, "full")));

        Assert.Equal(HttpStatusCode.OK, full.StatusCode);
        Assert.Equal("image/jpeg", full.Content.Headers.ContentType?.MediaType);
        var info = NeonSidekick.Images.ImageEditor.Info(await full.Content.ReadAsByteArrayAsync())!;
        Assert.Equal((20, 10), (info.Width, info.Height));
    }

    [Fact]
    public async Task TheDropdown_NeedsTheCookieAndTheHeader_AndAnswersItsJson()
    {
        _backend.Completion = new WebCompletion(0, 0, 3, [new WebCompletionItem("/tree", "a tree", "/tree ")], false);

        using var unsigned = await _client.SendAsync(Post("/api/complete", "{\"text\":\"/tr\",\"cursor\":3,\"seq\":1}", signedIn: false));
        using var cross = await _client.SendAsync(Post("/api/complete", "{\"text\":\"/tr\",\"cursor\":3,\"seq\":1}", csrf: false));
        using var bad = await _client.SendAsync(Post("/api/complete", "{nope"));
        using var ok = await _client.SendAsync(Post("/api/complete", "{\"text\":\"/tr\",\"cursor\":3,\"seq\":9}"));

        Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, cross.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        string json = await ok.Content.ReadAsStringAsync();
        Assert.Contains("\"seq\":9", json, StringComparison.Ordinal);
        Assert.Contains("\"insert\":\"/tree \"", json, StringComparison.Ordinal);
        Assert.True(_backend.Completed.TryDequeue(out var asked) && asked.Text == "/tr" && asked.Cursor == 3);
        Assert.Equal("no-store", ok.Headers.CacheControl?.ToString());
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
