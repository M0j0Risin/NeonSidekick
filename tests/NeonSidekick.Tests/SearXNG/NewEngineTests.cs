// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Net;
using NeonSidekick.Tests.Fakes;
using SearXNG.Net.Engines;
using SearXNG.Net.Html;

namespace SearXNG.Net.Tests;

public class JsLiteralTests
{
    private static List<object?> Read(string text) => JsLiteral.ParseArrayOfCalls(text, 0);

    [Fact]
    public void ReadsDataOfEveryKind()
    {
        var item = Assert.IsType<Dictionary<string, object?>>(Assert.Single(Read("[{a:1,'b':\"two\",c:[true,false,null,undefined,void 0,void(0)],d:-1.5e1,}]")));

        Assert.Equal(1.0, item["a"]);
        Assert.Equal("two", item["b"]);
        Assert.Equal(new object?[] { true, false, null, null, null, null }, Assert.IsType<List<object?>>(item["c"]));
        Assert.Equal(-15.0, item["d"]);
    }

    [Theory]
    [InlineData(@"""a\nb""", "a\nb")]
    [InlineData(@"""\x41\u0042\u{1F600}""", "AB\U0001F600")]
    [InlineData(@"'it\'s'", "it's")]
    [InlineData(@"""a\/b""", "a/b")]
    [InlineData("\"a\\\nb\"", "ab")]
    [InlineData("\"a\\\r\nb\"", "ab")]
    public void ReadsEscapes(string literal, string expected)
    {
        Assert.Equal(expected, Assert.Single(Read("[" + literal + "]")));
    }

    [Fact]
    public void BindsAFunctionsParameters_IncludingToObjects()
    {
        var item = Assert.IsType<Dictionary<string, object?>>(Assert.Single(Read("[(function(a,b,c){return {x:a,y:[b],z:c,w:missing}}(1,{k:\"v\"}))]")));

        Assert.Equal(1.0, item["x"]);
        Assert.Equal("v", Assert.IsType<Dictionary<string, object?>>(Assert.IsType<List<object?>>(item["y"])[0])["k"]);
        Assert.Null(item["z"]); // No argument was passed for it.
        Assert.Null(item["w"]); // Nothing binds it.
    }

    [Theory]
    [InlineData("[{a:1]")] // Unbalanced, so no closing bracket is found.
    [InlineData("[")]
    [InlineData("[alert(1)]")] // A call isn't data.
    [InlineData("[{a 1}]")]
    [InlineData("[1.2.3]")]
    [InlineData(@"[""\u12""]")]
    [InlineData(@"[""\u{110000}""]")]
    [InlineData(@"[""\xZZ""]")]
    public void RejectsWhatIsNotData(string text)
    {
        Assert.Throws<FormatException>(() => Read(text));
    }

    [Fact]
    public void RefusesToRecurseWithoutLimit()
    {
        var deep = "[" + new string('[', 5000) + new string(']', 5000) + "]";

        Assert.Throws<FormatException>(() => Read(deep));
    }
}

public class BraveEngineTests
{
    [Fact]
    public async Task SearchAsync_SendsTheLocaleInCookies_AndParses()
    {
        var handler = new StubHandler(_ => StubHandler.Text(Fixtures.Read("brave-results.html")));
        using var http = new HttpClient(handler);

        var results = await new BraveEngine().SearchAsync(new EngineQuery("dotnet native aot", Locale: "de-DE"), http, CancellationToken.None);

        Assert.Equal(10, results.Count);
        Assert.Equal("https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/", results[0].Url);
        Assert.DoesNotContain("<", results[0].Content, StringComparison.Ordinal);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://search.brave.com/search?q=dotnet+native+aot&source=web", request.RequestUri!.AbsoluteUri);
        var cookie = request.Headers.NonValidated.TryGetValues("Cookie", out var values) ? values.ToString() : "";
        Assert.Contains("country=de;", cookie, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAsync_PastThePageLimit_AsksNothing()
    {
        var handler = new StubHandler(_ => StubHandler.Text(""));
        using var http = new HttpClient(handler);

        Assert.Empty(await new BraveEngine().SearchAsync(new EngineQuery("x", 11), http, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SearchAsync_ReportsARefusal()
    {
        using var http = new HttpClient(new StubHandler(_ => StubHandler.Text("no", HttpStatusCode.TooManyRequests)));

        var e = await Assert.ThrowsAsync<EngineException>(() => new BraveEngine().SearchAsync(new EngineQuery("x"), http, CancellationToken.None));

        Assert.Equal(EngineFailureKind.TooManyRequests, e.Kind);
    }

    [Fact]
    public void Parse_SkipsAHitWithNoAddress()
    {
        var page = "data: [{},{data:{body:{response:{web:{results:[{title:\"no address\"},{title:\"t\",url:\"https://a.example/\"}]}}}}}]";

        Assert.Equal("https://a.example/", Assert.Single(BraveEngine.Parse(page)).Url);
    }

    [Fact]
    public void Parse_OfAPageWithAResultsThatIsNotAList_Fails()
    {
        var page = "data: [{},{data:{body:{response:{web:{count:1}}}}}]";

        Assert.Equal(EngineFailureKind.UnexpectedResponse, Assert.Throws<EngineException>(() => BraveEngine.Parse(page)).Kind);
    }

    public static TheoryData<string> ManyVoids()
    {
        string many = string.Concat(Enumerable.Repeat("void ", 100_000));
        return new TheoryData<string>
        {
            "data: [" + many + "0]",
            "data: [null,[" + many + "0]]",
            "data: [null,{a:" + many + "0}]",
            "data: [(function(a){return {x:a}}(" + many + "0))]",
        };
    }

    /// <summary>Before the depth limit reached these, each one overflowed the stack and killed the process, so run this alone when it might fail.</summary>
    [Theory]
    [MemberData(nameof(ManyVoids))]
    public void Parse_OfVoidsPastTheDepthLimit_IsAFailure_NotACrash(string page)
    {
        Assert.Contains("couldn't be read", Assert.Throws<EngineException>(() => BraveEngine.Parse(page)).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("data: [null,{data:{noResults:void 0}}]")]
    [InlineData("data: [null,{data:{noResults:void(0)}}]")]
    [InlineData("data: [null,{data:{noResults:void void 0}}]")]
    public void Parse_ReadsAnOrdinaryVoid(string page)
    {
        Assert.DoesNotContain("couldn't be read", Assert.Throws<EngineException>(() => BraveEngine.Parse(page)).Message, StringComparison.Ordinal);
    }
}

public class StartpageEngineTests
{
    private const string Home = "<form id=\"search\"><input name=\"sc\" value=\"CODE\"></form>";

    private static HttpResponseMessage Respond(HttpRequestMessage request) =>
        StubHandler.Text(request.Method == HttpMethod.Get ? Home : Fixtures.Read("startpage-results.html"));

    [Fact]
    public async Task SearchAsync_PostsTheForm_WithTheHomePagesCode()
    {
        var handler = new StubHandler(Respond);
        using var http = new HttpClient(handler);

        var results = await new StartpageEngine().SearchAsync(new EngineQuery("dotnet native aot"), http, CancellationToken.None);

        Assert.Equal(10, results.Count);
        Assert.DoesNotContain(results, r => r.Title == "An advert");
        Assert.Equal("https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/", results[0].Url);
        Assert.Equal(2, handler.Requests.Count);
        var post = handler.Requests[1];
        Assert.Equal(HttpMethod.Post, post.Method);
        Assert.Equal("https://www.startpage.com/sp/search", post.RequestUri!.AbsoluteUri);
        Assert.True(post.Headers.NonValidated.TryGetValues("Cookie", out var cookie));
        Assert.StartsWith("preferences=date_timeEEEworld", cookie.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAsync_KeepsTheCodeForAnHour()
    {
        var time = new ManualTimeProvider();
        var handler = new StubHandler(Respond);
        using var http = new HttpClient(handler);
        var engine = new StartpageEngine(time);

        await engine.SearchAsync(new EngineQuery("a"), http, CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(59));
        await engine.SearchAsync(new EngineQuery("b"), http, CancellationToken.None);
        Assert.Equal(1, handler.Requests.Count(r => r.Method == HttpMethod.Get));

        time.Advance(TimeSpan.FromMinutes(2));
        await engine.SearchAsync(new EngineQuery("c"), http, CancellationToken.None);
        Assert.Equal(2, handler.Requests.Count(r => r.Method == HttpMethod.Get));
    }

    [Fact]
    public async Task SearchAsync_TreatsTheProofOfWorkChallengeAsACaptcha()
    {
        using var http = new HttpClient(new StubHandler(_ => StubHandler.Text("<script id=\"anubis_challenge\" type=\"application/json\">{}</script>")));

        var e = await Assert.ThrowsAsync<EngineException>(() => new StartpageEngine().SearchAsync(new EngineQuery("x"), http, CancellationToken.None));

        Assert.Equal(EngineFailureKind.Captcha, e.Kind);
        Assert.Equal(TimeSpan.FromHours(1), e.SuspendFor);
    }

    [Fact]
    public async Task SearchAsync_TreatsAHomePageWithNoCodeAsACaptcha()
    {
        using var http = new HttpClient(new StubHandler(_ => StubHandler.Text("<html></html>")));

        var e = await Assert.ThrowsAsync<EngineException>(() => new StartpageEngine().SearchAsync(new EngineQuery("x"), http, CancellationToken.None));

        Assert.Equal(EngineFailureKind.Captcha, e.Kind);
    }

    [Fact]
    public async Task SearchAsync_TreatsARedirectToTheCaptchaPageAsACaptcha()
    {
        var handler = new StubHandler(request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return StubHandler.Text(Home);
            }

            var redirect = StubHandler.Text("", HttpStatusCode.Found);
            redirect.Headers.Location = new Uri("https://www.startpage.com/sp/captcha?x=1");
            return redirect;
        });
        using var http = new HttpClient(handler);

        var e = await Assert.ThrowsAsync<EngineException>(() => new StartpageEngine().SearchAsync(new EngineQuery("x"), http, CancellationToken.None));

        Assert.Equal(EngineFailureKind.Captcha, e.Kind);
    }

    [Fact]
    public async Task SearchAsync_PastThePageLimit_AsksNothing()
    {
        var handler = new StubHandler(Respond);
        using var http = new HttpClient(handler);

        Assert.Empty(await new StartpageEngine().SearchAsync(new EngineQuery("x", 19), http, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void Parse_OfBadJson_Fails()
    {
        var page = "React.createElement(UIStartpage.AppSerpWeb, {not json}})";

        Assert.Equal(EngineFailureKind.UnexpectedResponse, Assert.Throws<EngineException>(() => StartpageEngine.Parse(page)).Kind);
    }

    private const string Results = "React.createElement(UIStartpage.AppSerpWeb, {\"render\":{\"presenter\":{\"regions\":";

    [Fact]
    public void Parse_OfAPageWithNoResultsRegion_IsAFailure_NotNoResults()
    {
        Assert.Equal(EngineFailureKind.UnexpectedResponse, Assert.Throws<EngineException>(() => StartpageEngine.Parse(Results + "{}}}})")).Kind);
    }

    /// <summary>The second is what Startpage really sends for a word nothing matches.</summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"display_type\":\"notice-noresults-empty\"}]")]
    public void Parse_OfAnEmptyResultsRegion_IsNoResults(string mainline)
    {
        Assert.Empty(StartpageEngine.Parse(Results + "{\"mainline\":" + mainline + "}}}})"));
    }

    [Fact]
    public void Parse_FindsTheEndByBrackets_SoASnippetCanHoldTheOldEndMarker()
    {
        var results = StartpageEngine.Parse(Results + "{\"mainline\":[{\"display_type\":\"web-google\",\"results\":[{\"clickUrl\":\"https://a.example/\",\"title\":\"A\",\"description\":\"x}})y\"}]}]}}}})");

        Assert.Equal("x}})y", Assert.Single(results).Content);
    }

    [Fact]
    public void Parse_OfUnbalancedData_IsAFailure()
    {
        Assert.Equal(EngineFailureKind.UnexpectedResponse, Assert.Throws<EngineException>(() => StartpageEngine.Parse(Results)).Kind);
    }

    [Fact]
    public void Parse_OfHalfASurrogatePair_LosesOnlyThatField()
    {
        var results = StartpageEngine.Parse(Results + "{\"mainline\":[{\"display_type\":\"web-google\",\"results\":[{\"clickUrl\":\"https://a.example/\",\"title\":\"\\ud83d\",\"description\":\"kept\"}]}]}}}})");

        Assert.Equal("kept", Assert.Single(results).Content);
    }

    [Fact]
    public async Task ARefusedCode_IsNotReused()
    {
        int posts = 0;
        var handler = new StubHandler(request => StubHandler.Text(request.Method == HttpMethod.Get ? Home : ++posts == 1 ? "no data here" : Fixtures.Read("startpage-results.html")));
        using var http = new HttpClient(handler);
        var engine = new StartpageEngine();

        await Assert.ThrowsAsync<EngineException>(() => engine.SearchAsync(new EngineQuery("x"), http, CancellationToken.None));
        await engine.SearchAsync(new EngineQuery("x"), http, CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count(r => r.Method == HttpMethod.Get));
    }

    [Fact]
    public async Task ANetworkError_KeepsTheCode()
    {
        int posts = 0;
        var handler = new StubHandler(request => request.Method == HttpMethod.Get ? StubHandler.Text(Home)
            : ++posts == 1 ? throw new HttpRequestException("reset")
            : StubHandler.Text(Fixtures.Read("startpage-results.html")));
        using var http = new HttpClient(handler);
        var engine = new StartpageEngine();

        await Assert.ThrowsAsync<HttpRequestException>(() => engine.SearchAsync(new EngineQuery("x"), http, CancellationToken.None));
        await engine.SearchAsync(new EngineQuery("x"), http, CancellationToken.None);

        Assert.Single(handler.Requests, r => r.Method == HttpMethod.Get);
    }

    /// <summary>Answers from a function that can wait, so a test can finish overlapping searches in the order it wants.</summary>
    private sealed class WaitingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }

    [Fact]
    public async Task AFailureWithAnOldCode_LeavesANewerCode()
    {
        var release = new TaskCompletionSource();
        int homes = 0, posts = 0;
        using var http = new HttpClient(new WaitingHandler(async request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return StubHandler.Text("<form id=\"search\"><input name=\"sc\" value=\"CODE" + ++homes + "\"></form>");
            }

            switch (++posts)
            {
                case 1:
                    await release.Task;
                    return StubHandler.Text("no data here");
                case 2:
                    return StubHandler.Text("no data here");
                default:
                    return StubHandler.Text(Fixtures.Read("startpage-results.html"));
            }
        }));
        var engine = new StartpageEngine();
        var query = new EngineQuery("x");

        var first = engine.SearchAsync(query, http, CancellationToken.None);
        await Assert.ThrowsAsync<EngineException>(() => engine.SearchAsync(query, http, CancellationToken.None));
        await engine.SearchAsync(query, http, CancellationToken.None);
        Assert.Equal(2, homes);

        release.SetResult();
        await Assert.ThrowsAsync<EngineException>(() => first);
        await engine.SearchAsync(query, http, CancellationToken.None);

        Assert.Equal(2, homes);
    }
}

public class WikipediaEngineTests
{
    [Fact]
    public async Task SearchAsync_GivesTheArticle_AndNamesTheClient()
    {
        var handler = new StubHandler(_ => StubHandler.Text(Fixtures.Read("wikipedia-summary.json")));
        using var http = new HttpClient(handler);

        var results = await new WikipediaEngine { UserAgent = "MyApp/1.0 (contact@example.com)" }.SearchAsync(new EngineQuery("rust (programming language)"), http, CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal("Rust (programming language)", result.Title);
        Assert.StartsWith("https://en.wikipedia.org/wiki/Rust", result.Url, StringComparison.Ordinal);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://en.wikipedia.org/api/rest_v1/page/summary/Rust%20%28Programming%20Language%29", request.RequestUri!.AbsoluteUri);
        Assert.True(request.Headers.NonValidated.TryGetValues("User-Agent", out var agent));
        Assert.Equal("MyApp/1.0 (contact@example.com)", agent.ToString());
    }

    [Fact]
    public async Task SearchAsync_OfNoArticle_IsEmpty_NotAFailure()
    {
        using var http = new HttpClient(new StubHandler(_ => StubHandler.Text("{\"status\":404}", HttpStatusCode.NotFound)));

        Assert.Empty(await new WikipediaEngine().SearchAsync(new EngineQuery("no such article"), http, CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, EngineFailureKind.TooManyRequests)]
    [InlineData(HttpStatusCode.Forbidden, EngineFailureKind.AccessDenied)]
    [InlineData(HttpStatusCode.BadRequest, EngineFailureKind.HttpError)]
    public async Task SearchAsync_ReportsAnError(HttpStatusCode status, EngineFailureKind kind)
    {
        using var http = new HttpClient(new StubHandler(_ => StubHandler.Text("not json", status)));

        var e = await Assert.ThrowsAsync<EngineException>(() => new WikipediaEngine().SearchAsync(new EngineQuery("x"), http, CancellationToken.None));

        Assert.Equal(kind, e.Kind);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("not json")]
    public void Parse_OfAnAnswerWithNoLink_Fails(string json)
    {
        Assert.Equal(EngineFailureKind.UnexpectedResponse, Assert.Throws<EngineException>(() => WikipediaEngine.Parse(json)).Kind);
    }

    /// <summary>A path of . or .. would be resolved away, asking for another address.</summary>
    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public async Task SearchAsync_OfADotTitle_AsksNothing(string title)
    {
        var handler = new StubHandler(_ => StubHandler.Text("{}"));
        using var http = new HttpClient(handler);

        Assert.Empty(await new WikipediaEngine().SearchAsync(new EngineQuery(title), http, CancellationToken.None));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void Parse_OfHalfASurrogatePair_LosesOnlyThatField()
    {
        var result = Assert.Single(WikipediaEngine.Parse("{\"title\":\"\\ud83d\",\"content_urls\":{\"desktop\":{\"page\":\"https://a.example/\"}}}"));

        Assert.Equal("https://a.example/", result.Url);
    }
}
