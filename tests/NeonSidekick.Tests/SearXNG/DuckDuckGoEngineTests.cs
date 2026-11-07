// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Net;
using NeonSidekick.Tests.Fakes;
using SearXNG.Net.Engines;
using SearXNG.Net.Net;

namespace SearXNG.Net.Tests;

public class DuckDuckGoEngineTests
{
    private const string ApiLink = "https://links.duckduckgo.com/d.js?q=dotnet&amp;vqd=TOKEN&amp;dp=TOKEN";
    private const string FirstPage = "<html><head><link id=\"deep_preload_link\" rel=\"preload\" as=\"script\" href=\"" + ApiLink + "\"></head></html>";

    private readonly ManualTimeProvider _time = new();

    /// <summary>Answers the HTML page with <see cref="FirstPage"/> and anything on the API with the fixture.</summary>
    private static StubHandler Site(string? api = null, string page = FirstPage) => new(request =>
        StubHandler.Text(request.RequestUri!.AbsolutePath == "/d.js" ? api ?? Fixtures.Read("ddg-results.json") : page));

    private DuckDuckGoEngine Engine() => new(_time);

    [Fact]
    public async Task FirstPage_ScrapesTheApiLink_ThenAsksTheApiForJson()
    {
        var handler = Site();
        using var http = new HttpClient(handler);

        await Engine().SearchAsync(new EngineQuery("dotnet native aot"), http, CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("https://duckduckgo.com/?q=dotnet+native+aot&t=h_&ia=web", handler.Requests[0].RequestUri!.AbsoluteUri);
        Assert.Equal("https://links.duckduckgo.com/d.js?o=json&q=dotnet&vqd=TOKEN&dp=TOKEN", handler.Requests[1].RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task ApiRequest_LooksLikeTheScriptLoadABrowserMakes()
    {
        var handler = Site();
        using var http = new HttpClient(handler);

        await Engine().SearchAsync(new EngineQuery("x"), http, CancellationToken.None);

        var headers = handler.Requests[1].Headers;
        Assert.Equal("script", headers.GetValues("Sec-Fetch-Dest").Single());
        Assert.Equal("no-cors", headers.GetValues("Sec-Fetch-Mode").Single());
        Assert.Equal("same-site", headers.GetValues("Sec-Fetch-Site").Single());
        Assert.Equal("https://duckduckgo.com/", headers.Referrer!.AbsoluteUri);
        Assert.Contains("Firefox/", headers.UserAgent.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A real answer, reduced and scrubbed. Expected values were read from the fixture by hand.</summary>
    [Fact]
    public async Task TheCapturedAnswer_GivesTenResults_WithMarkupAndEntitiesGone()
    {
        using var http = new HttpClient(Site());

        var results = await Engine().SearchAsync(new EngineQuery("dotnet native aot"), http, CancellationToken.None);

        Assert.Equal(10, results.Count);
        Assert.Equal("https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/", results[0].Url);
        Assert.Equal("Native AOT deployment overview - .NET | Microsoft Learn", results[0].Title);
        Assert.StartsWith("Check out the Native AOT samples available in the dotnet/samples repository", results[0].Content, StringComparison.Ordinal);
        Assert.Contains("Let's explore how Native AOT transforms", results[6].Content, StringComparison.Ordinal);
        Assert.Equal("https://www.reddit.com/r/dotnet/comments/1d6lfjh/are_you_using_native_aot_what_is_your_experience/", results[9].Url);
    }

    [Fact]
    public async Task ASecondSearch_ReusesTheFirstPageLink_ForTwoHours()
    {
        var handler = Site();
        using var http = new HttpClient(handler);
        var engine = Engine();
        await engine.SearchAsync(new EngineQuery("x"), http, CancellationToken.None);
        handler.Requests.Clear();

        _time.Advance(TimeSpan.FromHours(1.9));
        await engine.SearchAsync(new EngineQuery("x"), http, CancellationToken.None);
        Assert.Single(handler.Requests);
        handler.Requests.Clear();

        _time.Advance(TimeSpan.FromHours(0.2));
        await engine.SearchAsync(new EngineQuery("x"), http, CancellationToken.None);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task AnotherQuery_GetsItsOwnLink()
    {
        var handler = Site();
        using var http = new HttpClient(handler);
        var engine = Engine();
        await engine.SearchAsync(new EngineQuery("one"), http, CancellationToken.None);
        handler.Requests.Clear();

        await engine.SearchAsync(new EngineQuery("two"), http, CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task PageTwo_UsesTheLinkPageOneReturned()
    {
        var handler = Site();
        using var http = new HttpClient(handler);
        var engine = Engine();
        await engine.SearchAsync(new EngineQuery("x"), http, CancellationToken.None);
        handler.Requests.Clear();

        await engine.SearchAsync(new EngineQuery("x", PageNumber: 2), http, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.StartsWith("https://duckduckgo.com/d.js?o=json&q=dotnet%20native%20aot&l=us-en&p=&s=10", request.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PageTwo_AsksNothing_WithoutPageOne()
    {
        var handler = Site();
        using var http = new HttpClient(handler);

        var results = await Engine().SearchAsync(new EngineQuery("x", PageNumber: 2), http, CancellationToken.None);

        Assert.Empty(results);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task PageTwo_AsksNothing_OnceItsLinkHasExpired()
    {
        var handler = Site();
        using var http = new HttpClient(handler);
        var engine = Engine();
        await engine.SearchAsync(new EngineQuery("x"), http, CancellationToken.None);
        handler.Requests.Clear();
        _time.Advance(TimeSpan.FromHours(1.1));

        var results = await engine.SearchAsync(new EngineQuery("x", PageNumber: 2), http, CancellationToken.None);

        Assert.Empty(results);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(499, 2)]
    [InlineData(500, 0)]
    public async Task AQueryOverLimit_IsNotSent(int length, int requests)
    {
        var handler = Site();
        using var http = new HttpClient(handler);

        await Engine().SearchAsync(new EngineQuery(new string('a', length)), http, CancellationToken.None);

        Assert.Equal(requests, handler.Requests.Count);
    }

    [Fact]
    public async Task ARefusedFirstPage_IsAFailure_NotAnEmptyAnswer()
    {
        using var http = new HttpClient(new StubHandler(_ => StubHandler.Text("no", HttpStatusCode.Forbidden)));

        var e = await Assert.ThrowsAsync<EngineException>(() => Engine().SearchAsync(new EngineQuery("x"), http, CancellationToken.None));

        Assert.Equal(EngineFailureKind.AccessDenied, e.Kind);
    }

    [Fact]
    public async Task AFirstPageWithoutTheLink_IsAFailure_WithoutARest()
    {
        using var http = new HttpClient(Site(page: "<html>changed</html>"));

        var e = await Assert.ThrowsAsync<EngineException>(() => Engine().SearchAsync(new EngineQuery("x"), http, CancellationToken.None));

        Assert.Equal(EngineFailureKind.UnexpectedResponse, e.Kind);
        Assert.Null(e.SuspendFor);
        Assert.Equal("https://duckduckgo.com/?q=x&t=h_&ia=web", e.RefusedUrl!.AbsoluteUri);
    }

    [Theory]
    [InlineData("https://attacker.example/d.js?q=x")]
    [InlineData("http://links.duckduckgo.com/d.js?q=x")]
    [InlineData("https://duckduckgo.com.attacker.example/d.js?q=x")]
    public async Task AFirstPageLinkingElsewhere_IsNotFollowed(string link)
    {
        var handler = Site(page: $"<link id=\"deep_preload_link\" href=\"{link}\">");
        using var http = new HttpClient(handler);

        var e = await Assert.ThrowsAsync<EngineException>(() => Engine().SearchAsync(new EngineQuery("x"), http, CancellationToken.None));

        Assert.Equal(EngineFailureKind.UnexpectedResponse, e.Kind);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"other\":1}")]
    [InlineData("{\"results\":5}")]
    public async Task AnUnreadableAnswer_IsAFailure(string answer)
    {
        using var http = new HttpClient(Site(answer));

        var e = await Assert.ThrowsAsync<EngineException>(() => Engine().SearchAsync(new EngineQuery("x"), http, CancellationToken.None));

        Assert.Equal(EngineFailureKind.UnexpectedResponse, e.Kind);
    }

    [Fact]
    public async Task EmptyResults_AreAnAnswer()
    {
        using var http = new HttpClient(Site("{\"results\":[]}"));

        Assert.Empty(await Engine().SearchAsync(new EngineQuery("x"), http, CancellationToken.None));
    }

    [Fact]
    public async Task ItemsWithoutAnAddress_AreSkipped()
    {
        using var http = new HttpClient(Site("{\"results\":[{\"t\":\"no url\"},7,{\"u\":\"https://a.example/\",\"t\":\"t\"}]}"));

        var result = Assert.Single(await Engine().SearchAsync(new EngineQuery("x"), http, CancellationToken.None));

        Assert.Equal(new EngineResult("https://a.example/", "t", ""), result);
    }

    private const string Challenge =
        "let double = function(num) { return num * 2; };\n" +
        "let snippet = function(num) { return num + `<p><div></p><p></div`.length; };\n" +
        "let jsa = 10;\n" +
        "jsa = double(jsa);\n" +
        "jsa = snippet(jsa);\n" +
        "DDG.deep.initialize('/d.js?q=x&o=json&jsa=');";

    [Fact]
    public async Task AChallenge_IsReported_NotAnswered()
    {
        var handler = Site(Challenge);
        using var http = new HttpClient(handler);

        var e = await Assert.ThrowsAsync<EngineException>(() => Engine().SearchAsync(new EngineQuery("x"), http, CancellationToken.None));

        Assert.Equal(EngineFailureKind.Captcha, e.Kind);
        Assert.Equal(EngineHttp.CaptchaRest, e.SuspendFor);
        Assert.Equal("links.duckduckgo.com", e.RefusedUrl!.Host);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task AChallenge_RestsTheEngine_SoTheNextSearchDoesNotAsk()
    {
        var handler = Site(Challenge);
        using var http = new HttpClient(handler);
        var search = new MetaSearch([Engine()], http);

        var first = await search.SearchAsync(new EngineQuery("x"));
        var second = await search.SearchAsync(new EngineQuery("x"));

        Assert.Equal(EngineFailureKind.Captcha, Assert.Single(first.Failures).Kind);
        Assert.Equal(EngineFailureKind.Suspended, Assert.Single(second.Failures).Kind);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task JsonThatMentionsTheChallenge_IsResults_NotACaptcha()
    {
        using var http = new HttpClient(Site("{\"results\":[{\"u\":\"https://a.example/\",\"t\":\"let jsa = 1\",\"a\":\"\"}]}"));

        var results = await Engine().SearchAsync(new EngineQuery("x"), http, CancellationToken.None);

        Assert.Equal("let jsa = 1", Assert.Single(results).Title);
    }

    [Fact]
    public async Task AnItemWhoseAddressIsNoString_IsSkipped()
    {
        using var http = new HttpClient(Site("{\"results\":[{\"u\":1,\"t\":\"A\",\"a\":\"\"},{\"u\":\"https://b.example/\",\"t\":\"B\",\"a\":\"\"}]}"));

        var results = await Engine().SearchAsync(new EngineQuery("x"), http, CancellationToken.None);

        Assert.Equal("https://b.example/", Assert.Single(results).Url);
    }

    [Theory]
    [InlineData("[1]")]
    [InlineData("1")]
    [InlineData("null")]
    public async Task AnAnswerThatIsNoObject_IsAFailure(string answer)
    {
        using var http = new HttpClient(Site(answer));

        var e = await Assert.ThrowsAsync<EngineException>(() => Engine().SearchAsync(new EngineQuery("x"), http, CancellationToken.None));

        Assert.Equal(EngineFailureKind.UnexpectedResponse, e.Kind);
    }

    [Fact]
    public async Task HalfASurrogatePair_LosesOnlyThatField()
    {
        using var http = new HttpClient(Site("{\"results\":[{\"u\":\"https://a.example/\\ud83d\",\"t\":\"A\",\"a\":\"\"},{\"u\":\"https://b.example/\",\"t\":\"\\ud83d\",\"a\":\"\"}]}"));

        var results = await Engine().SearchAsync(new EngineQuery("x"), http, CancellationToken.None);

        Assert.Equal("https://b.example/", Assert.Single(results).Url);
    }

    [Fact]
    public async Task AnAnswerItCouldNotUse_ForgetsTheLink()
    {
        int answers = 0;
        var handler = new StubHandler(request => StubHandler.Text(request.RequestUri!.AbsolutePath != "/d.js" ? FirstPage : ++answers == 1 ? "not json" : Fixtures.Read("ddg-results.json")));
        using var http = new HttpClient(handler);
        var engine = Engine();

        await Assert.ThrowsAsync<EngineException>(() => engine.SearchAsync(new EngineQuery("x"), http, CancellationToken.None));
        await engine.SearchAsync(new EngineQuery("x"), http, CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count(r => r.RequestUri!.AbsolutePath != "/d.js"));
    }

    [Fact]
    public async Task ALinkOffSite_IsForgotten()
    {
        int pages = 0;
        var handler = new StubHandler(request => StubHandler.Text(request.RequestUri!.AbsolutePath == "/d.js" ? Fixtures.Read("ddg-results.json")
            : ++pages == 1 ? "<link id=\"deep_preload_link\" href=\"https://evil.example/d.js\">"
            : FirstPage));
        using var http = new HttpClient(handler);
        var engine = Engine();

        await Assert.ThrowsAsync<EngineException>(() => engine.SearchAsync(new EngineQuery("x"), http, CancellationToken.None));

        Assert.NotEmpty(await engine.SearchAsync(new EngineQuery("x"), http, CancellationToken.None));
    }

    [Fact]
    public async Task ALaterPagesFailure_KeepsItsLink_AsNothingElseLeadsThere()
    {
        const string FirstAnswer = "{\"results\":[{\"u\":\"https://a.example/\",\"t\":\"A\",\"a\":\"\"},{\"n\":\"/d.js?q=x&s=10\"}]}";
        const string SecondAnswer = "{\"results\":[{\"u\":\"https://b.example/\",\"t\":\"B\",\"a\":\"\"}]}";
        int later = 0;
        using var http = new HttpClient(new StubHandler(request => request.RequestUri!.AbsolutePath != "/d.js" ? StubHandler.Text(FirstPage)
            : !request.RequestUri.Query.Contains("s=10", StringComparison.Ordinal) ? StubHandler.Text(FirstAnswer)
            : ++later == 1 ? StubHandler.Text("busy", HttpStatusCode.InternalServerError)
            : StubHandler.Text(SecondAnswer)));
        var engine = Engine();
        await engine.SearchAsync(new EngineQuery("x"), http, CancellationToken.None);

        await Assert.ThrowsAsync<EngineException>(() => engine.SearchAsync(new EngineQuery("x", PageNumber: 2), http, CancellationToken.None));
        var retried = await engine.SearchAsync(new EngineQuery("x", PageNumber: 2), http, CancellationToken.None);

        Assert.Equal("https://b.example/", Assert.Single(retried).Url);
    }
}
