// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Net;
using NeonSidekick.Tests.Fakes;
using SearXNG.Net.Engines;

namespace SearXNG.Net.Tests;

public class MetaSearchTests : IDisposable
{
    private readonly ManualTimeProvider _time = new();
    private readonly HttpClient _http = new(new StubHandler(_ => StubHandler.Text("")));

    public void Dispose()
    {
        _http.Dispose();
        GC.SuppressFinalize(this);
    }

    private MetaSearch Search(params IEngine[] engines) => new(engines, _http, time: _time);

    private static EngineResult Hit(string path) => new("https://example.com/" + path, path, "");

    private static FakeEngine Failing(string name, Exception e) => new(name, (_, _) => Task.FromException<IReadOnlyList<EngineResult>>(e));

    /// <summary>An engine that waits until it's canceled, as one stuck on a slow site does.</summary>
    private static FakeEngine Stuck(string name, TimeSpan? timeout = null) => new(name, async (_, token) =>
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        return [];
    }) { Timeout = timeout ?? TimeSpan.FromSeconds(3) };

    [Fact]
    public async Task MergesWhatEveryEngineFound()
    {
        var response = await Search(new FakeEngine("a", Hit("one"), Hit("shared")), new FakeEngine("b", Hit("shared"))).SearchAsync(new EngineQuery("x"));

        Assert.Equal(["https://example.com/shared", "https://example.com/one"], response.Results.Select(r => r.Url));
        Assert.Empty(response.Failures);
        Assert.Equal(["a", "b"], response.Timings.Select(t => t.Engine));
    }

    [Fact]
    public async Task EnginesRunAtTheSameTime()
    {
        var bothStarted = new TaskCompletionSource();
        int started = 0;
        async Task<IReadOnlyList<EngineResult>> WaitForTheOther(EngineQuery query, CancellationToken token)
        {
            if (Interlocked.Increment(ref started) == 2)
            {
                bothStarted.SetResult();
            }

            await bothStarted.Task.WaitAsync(token);
            return [];
        }

        var response = await Search(new FakeEngine("a", WaitForTheOther), new FakeEngine("b", WaitForTheOther)).SearchAsync(new EngineQuery("x"));

        Assert.Empty(response.Failures);
    }

    [Fact]
    public async Task ARankingTie_IsDecidedByEngineOrder_NotByWhoFinishedFirst()
    {
        var release = new TaskCompletionSource<IReadOnlyList<EngineResult>>();
        var slowFirst = new FakeEngine("slow", (_, _) => release.Task);
        var fast = new FakeEngine("fast", Hit("from-fast"));

        var pending = Search(slowFirst, fast).SearchAsync(new EngineQuery("x"));
        release.SetResult([Hit("from-slow")]);
        var response = await pending;

        Assert.Equal(["https://example.com/from-slow", "https://example.com/from-fast"], response.Results.Select(r => r.Url));
    }

    [Fact]
    public async Task OneFailure_DoesNotLoseTheOtherEnginesResults()
    {
        var response = await Search(Failing("bad", new EngineException(EngineFailureKind.Captcha, "CAPTCHA")), new FakeEngine("good", Hit("one"))).SearchAsync(new EngineQuery("x"));

        Assert.Single(response.Results);
        var failure = Assert.Single(response.Failures);
        Assert.Equal("bad", failure.Engine);
        Assert.Equal(EngineFailureKind.Captcha, failure.Kind);
        Assert.Equal(["good"], response.Timings.Select(t => t.Engine));
    }

    [Fact]
    public async Task AFailure_PassesOnTheRefusedPage()
    {
        var page = new Uri("https://example.com/check");

        var response = await Search(Failing("bad", new EngineException(EngineFailureKind.AccessDenied, "denied", refusedUrl: page))).SearchAsync(new EngineQuery("x"));

        Assert.Equal(page, Assert.Single(response.Failures).RefusedUrl);
    }

    public static TheoryData<Exception, EngineFailureKind, bool> Failures => new()
    {
        { new HttpRequestException("refused"), EngineFailureKind.Network, true },
        { new TaskCanceledException("the HttpClient's own timeout"), EngineFailureKind.Timeout, true },
        { new EngineException(EngineFailureKind.HttpError, "HTTP error 500"), EngineFailureKind.HttpError, true },
        { new InvalidOperationException("a bug"), EngineFailureKind.UnexpectedResponse, false },
        { new EngineException(EngineFailureKind.UnexpectedResponse, "changed", TimeSpan.Zero), EngineFailureKind.UnexpectedResponse, false },
        { new EngineException(EngineFailureKind.UnexpectedResponse, "changed"), EngineFailureKind.UnexpectedResponse, false },
        { new EngineException(EngineFailureKind.Captcha, "a check"), EngineFailureKind.Captcha, true },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task AFailure_IsClassified_AndRestedOnlyWhenItShouldBe(Exception e, EngineFailureKind kind, bool rests)
    {
        var engine = Failing("a", e);
        var search = Search(engine);

        var first = await search.SearchAsync(new EngineQuery("x"));
        Assert.Equal(kind, Assert.Single(first.Failures).Kind);

        var second = await search.SearchAsync(new EngineQuery("x"));
        Assert.Equal(rests ? EngineFailureKind.Suspended : kind, Assert.Single(second.Failures).Kind);
        Assert.Equal(rests ? 1 : 2, engine.Calls);
    }

    [Fact]
    public async Task ADefaultRest_EndsAfterFiveSeconds()
    {
        var engine = Failing("a", new HttpRequestException("refused"));
        var search = Search(engine);
        await search.SearchAsync(new EngineQuery("x"));

        _time.Advance(TimeSpan.FromSeconds(4.9));
        await search.SearchAsync(new EngineQuery("x"));
        Assert.Equal(1, engine.Calls);

        _time.Advance(TimeSpan.FromSeconds(0.2));
        await search.SearchAsync(new EngineQuery("x"));
        Assert.Equal(2, engine.Calls);
    }

    [Fact]
    public async Task AnEngineLeftOut_GetsNoRequest_AndNoFailure_RestingOrNot()
    {
        var wanted = new HashSet<string> { "wanted" };

        var resting = Failing("left", new EngineException(EngineFailureKind.Captcha, "CAPTCHA"));
        var search = Search(new FakeEngine("wanted", Hit("one")), resting);
        await search.SearchAsync(new EngineQuery("x"));
        Assert.Equal(1, resting.Calls);
        var whileResting = await search.SearchAsync(new EngineQuery("x", Engines: wanted));
        Assert.Empty(whileResting.Failures);
        Assert.Single(whileResting.Results);
        Assert.Equal(1, resting.Calls);

        var quiet = new FakeEngine("left", Hit("two"));
        var answered = await Search(new FakeEngine("wanted", Hit("one")), quiet).SearchAsync(new EngineQuery("x", Engines: wanted));
        Assert.Empty(answered.Failures);
        Assert.Single(answered.Results);
        Assert.Equal(0, quiet.Calls);
    }

    [Fact]
    public async Task AnEnginesOwnRest_OverridesTheDefault_AndItsReasonIsReported()
    {
        var engine = Failing("a", new EngineException(EngineFailureKind.TooManyRequests, "Too many requests", TimeSpan.FromSeconds(180)));
        var search = Search(engine);
        await search.SearchAsync(new EngineQuery("x"));

        _time.Advance(TimeSpan.FromSeconds(179));
        var resting = await search.SearchAsync(new EngineQuery("x"));
        var failure = Assert.Single(resting.Failures);
        Assert.Equal(EngineFailureKind.Suspended, failure.Kind);
        Assert.Equal("Too many requests", failure.Message);

        _time.Advance(TimeSpan.FromSeconds(2));
        await search.SearchAsync(new EngineQuery("x"));
        Assert.Equal(2, engine.Calls);
    }

    [Fact]
    public async Task ASuccess_ClearsARest()
    {
        int calls = 0;
        var engine = new FakeEngine("a", (_, _) => ++calls == 1
            ? Task.FromException<IReadOnlyList<EngineResult>>(new HttpRequestException("refused"))
            : Task.FromResult<IReadOnlyList<EngineResult>>([Hit("one")]));
        var search = Search(engine);
        await search.SearchAsync(new EngineQuery("x"));
        _time.Advance(TimeSpan.FromSeconds(6));

        Assert.Single((await search.SearchAsync(new EngineQuery("x"))).Results);
        Assert.Single((await search.SearchAsync(new EngineQuery("x"))).Results);
    }

    [Fact]
    public async Task AnEngineThatTakesTooLong_TimesOut_WhileTheOthersStillCount()
    {
        var search = Search(Stuck("slow"), new FakeEngine("fast", Hit("one")));

        var pending = search.SearchAsync(new EngineQuery("x"));
        _time.Advance(TimeSpan.FromSeconds(3));
        var response = await pending;

        Assert.Single(response.Results);
        var failure = Assert.Single(response.Failures);
        Assert.Equal("slow", failure.Engine);
        Assert.Equal(EngineFailureKind.Timeout, failure.Kind);
    }

    [Fact]
    public async Task ATimeout_RestsTheEngine()
    {
        var engine = Stuck("slow");
        var search = Search(engine);
        var pending = search.SearchAsync(new EngineQuery("x"));
        _time.Advance(TimeSpan.FromSeconds(3));
        await pending;

        var response = await search.SearchAsync(new EngineQuery("x"));

        Assert.Equal(EngineFailureKind.Suspended, Assert.Single(response.Failures).Kind);
        Assert.Equal(1, engine.Calls);
    }

    private async Task AssertTimesOutAt(double expectedSeconds, MetaSearch search, EngineQuery query)
    {
        var pending = search.SearchAsync(query);
        _time.Advance(TimeSpan.FromSeconds(expectedSeconds - 0.5));
        Assert.False(pending.IsCompleted);
        _time.Advance(TimeSpan.FromSeconds(0.5));
        Assert.Equal(EngineFailureKind.Timeout, (await pending).Failures[0].Kind);
    }

    [Fact]
    public Task Timeout_IsTheLongestAmongTheEngines() =>
        AssertTimesOutAt(7, Search(Stuck("a", TimeSpan.FromSeconds(2)), Stuck("b", TimeSpan.FromSeconds(7))), new EngineQuery("x"));

    [Fact]
    public Task Timeout_ANarrowerQueryLimitWins() =>
        AssertTimesOutAt(2, Search(Stuck("a")), new EngineQuery("x", TimeoutLimit: TimeSpan.FromSeconds(2)));

    [Fact]
    public Task Timeout_AWiderQueryLimitDoesNotExtendIt() =>
        AssertTimesOutAt(3, Search(Stuck("a")), new EngineQuery("x", TimeoutLimit: TimeSpan.FromSeconds(9)));

    [Fact]
    public Task Timeout_TheCapWins() =>
        AssertTimesOutAt(2, new MetaSearch([Stuck("a")], _http, new MetaSearchOptions { MaxRequestTimeout = TimeSpan.FromSeconds(2) }, _time), new EngineQuery("x"));

    [Fact]
    public Task Timeout_WithCapAndLimit_TheLimitCanExceedTheEnginesOwn() =>
        // As in _get_requests: with both set, the engines' own timeout isn't consulted.
        AssertTimesOutAt(5, new MetaSearch([Stuck("a")], _http, new MetaSearchOptions { MaxRequestTimeout = TimeSpan.FromSeconds(10) }, _time), new EngineQuery("x", TimeoutLimit: TimeSpan.FromSeconds(5)));

    [Fact]
    public async Task CancelingTheSearch_Throws_AndRestsNoEngine()
    {
        var engine = Stuck("a");
        var search = Search(engine);
        using var cancel = new CancellationTokenSource();

        var pending = search.SearchAsync(new EngineQuery("x"), cancel.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        var next = search.SearchAsync(new EngineQuery("x"));
        _time.Advance(TimeSpan.FromSeconds(3));
        await next;
        Assert.Equal(2, engine.Calls);
    }

    [Fact]
    public async Task ALaterPage_SkipsEnginesWithoutPaging()
    {
        var paged = new FakeEngine("paged", Hit("one")) { Paging = true };
        var unpaged = new FakeEngine("unpaged", Hit("two"));

        var response = await Search(paged, unpaged).SearchAsync(new EngineQuery("x", PageNumber: 2));

        Assert.Equal(0, unpaged.Calls);
        Assert.Single(response.Results);
        Assert.Empty(response.Failures);
        Assert.True(response.HasMorePages);
    }

    [Fact]
    public async Task HasMorePages_IsFalseWithoutAPagingEngine()
    {
        Assert.False((await Search(new FakeEngine("a", Hit("one"))).SearchAsync(new EngineQuery("x"))).HasMorePages);
    }

    [Fact]
    public async Task NoEnginesToAsk_IsAnEmptyAnswer()
    {
        var response = await Search().SearchAsync(new EngineQuery("x"));

        Assert.Empty(response.Results);
        Assert.Empty(response.Failures);
    }

    [Fact]
    public void TwoEnginesWithOneName_AreRefused()
    {
        Assert.Throws<ArgumentException>(() => Search(new FakeEngine("a"), new FakeEngine("a")));
    }

    [Fact]
    public async Task RealEnginesTogether_MergeTheCapturedPages()
    {
        var handler = new StubHandler(request => StubHandler.Text(request.RequestUri!.Host switch
        {
            "www.bing.com" => Fixtures.Read("bing-results.html"),
            "duckduckgo.com" => "<link id=\"deep_preload_link\" href=\"https://links.duckduckgo.com/d.js?q=x\">",
            "links.duckduckgo.com" => Fixtures.Read("ddg-results.json"),
            _ => throw new InvalidOperationException("unexpected host"),
        }));
        using var http = new HttpClient(handler);

        var response = await new MetaSearch([new BingEngine(), new DuckDuckGoEngine(_time)], http, time: _time).SearchAsync(new EngineQuery("dotnet native aot"));

        // Both captures found the same pages at the top: Microsoft's overview is first in each, so weight 2 over positions 1 and 1.
        Assert.Empty(response.Failures);
        Assert.Equal("https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/", response.Results[0].Url);
        Assert.Equal(4.0, response.Results[0].Score, 10);
        Assert.Equal(["bing", "duckduckgo"], response.Results[0].Engines.Order(StringComparer.Ordinal));

        // Read from the fixtures: both captures list the same ten addresses, so all ten merge.
        Assert.Equal(10, response.Results.Count);
        Assert.All(response.Results, r => Assert.Equal(2, r.Engines.Count));

        // The DuckDuckGo snippet is longer than Bing's clipped one, so it's the one kept.
        Assert.StartsWith("Check out the Native AOT samples", response.Results[0].Content, StringComparison.Ordinal);
        Assert.EndsWith("using containers.", response.Results[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusalFromARealEngine_RestsItForThreeMinutes()
    {
        using var http = new HttpClient(new StubHandler(_ => StubHandler.Text("no", HttpStatusCode.Forbidden)));
        var search = new MetaSearch([new BingEngine()], http, time: _time);
        await search.SearchAsync(new EngineQuery("x"));

        _time.Advance(TimeSpan.FromSeconds(179));
        Assert.Equal(EngineFailureKind.Suspended, Assert.Single((await search.SearchAsync(new EngineQuery("x"))).Failures).Kind);

        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(EngineFailureKind.AccessDenied, Assert.Single((await search.SearchAsync(new EngineQuery("x"))).Failures).Kind);
    }

    public static TheoryData<Exception, EngineFailureKind> RestCauses => new()
    {
        { new EngineException(EngineFailureKind.Captcha, "a check"), EngineFailureKind.Captcha },
        { new EngineException(EngineFailureKind.AccessDenied, "denied"), EngineFailureKind.AccessDenied },
        { new EngineException(EngineFailureKind.TooManyRequests, "slow down"), EngineFailureKind.TooManyRequests },
        { new HttpRequestException("refused"), EngineFailureKind.Network },
        { new TaskCanceledException("the HttpClient's own timeout"), EngineFailureKind.Timeout },
    };

    [Theory]
    [MemberData(nameof(RestCauses))]
    public async Task ARest_SaysWhatCausedIt(Exception e, EngineFailureKind cause)
    {
        var search = Search(Failing("a", e));
        await search.SearchAsync(new EngineQuery("x"));

        var resting = Assert.Single((await search.SearchAsync(new EngineQuery("x"))).Failures);

        Assert.Equal(EngineFailureKind.Suspended, resting.Kind);
        Assert.Equal(cause, resting.RestCause);
    }

    /// <summary>Two searches at once, whose engine runs are finished by hand in the order a test wants.</summary>
    private static (FakeEngine Engine, List<TaskCompletionSource<IReadOnlyList<EngineResult>>> Runs) Overlapping()
    {
        var runs = new List<TaskCompletionSource<IReadOnlyList<EngineResult>>>();
        var engine = new FakeEngine("a", (_, _) =>
        {
            // A later search answers at once, so a rest that wrongly ended fails the test rather than hanging it.
            if (runs.Count == 2)
            {
                return Task.FromResult<IReadOnlyList<EngineResult>>([]);
            }

            var run = new TaskCompletionSource<IReadOnlyList<EngineResult>>();
            runs.Add(run);
            return run.Task;
        });
        return (engine, runs);
    }

    [Fact]
    public async Task AShorterRest_DoesNotCutALongerOneShort()
    {
        var (engine, runs) = Overlapping();
        var search = Search(engine);
        var first = search.SearchAsync(new EngineQuery("x"));
        var second = search.SearchAsync(new EngineQuery("x"));

        runs[0].SetException(new EngineException(EngineFailureKind.Captcha, "a check", TimeSpan.FromHours(1)));
        runs[1].SetException(new HttpRequestException("refused"));
        await Task.WhenAll(first, second);
        _time.Advance(TimeSpan.FromSeconds(6));

        Assert.Equal(EngineFailureKind.Suspended, Assert.Single((await search.SearchAsync(new EngineQuery("x"))).Failures).Kind);
        Assert.Equal(2, engine.Calls);
    }

    [Fact]
    public async Task ASuccessFromASearchThatStartedBeforeARest_LeavesTheRest()
    {
        var (engine, runs) = Overlapping();
        var search = Search(engine);
        var first = search.SearchAsync(new EngineQuery("x"));
        var second = search.SearchAsync(new EngineQuery("x"));

        runs[0].SetException(new EngineException(EngineFailureKind.Captcha, "a check", TimeSpan.FromHours(1)));
        runs[1].SetResult([Hit("one")]);
        await Task.WhenAll(first, second);

        Assert.Equal(EngineFailureKind.Suspended, Assert.Single((await search.SearchAsync(new EngineQuery("x"))).Failures).Kind);
        Assert.Equal(2, engine.Calls);
    }

    /// <summary>An engine parsing a page: it ignores cancellation until told to finish, after yielding as a real engine's network await does.</summary>
    private static FakeEngine StillParsing(string name, Task finish) => new(name, async (_, _) =>
    {
        await Task.Yield();
        await finish;
        return [Hit(name)];
    });

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    [Fact]
    public async Task AParseThatOutlastsTheDeadline_DoesNotHoldUpTheSearch()
    {
        var finish = new TaskCompletionSource();
        var search = Search(StillParsing("slow", finish.Task), new FakeEngine("fast", Hit("one")));

        var pending = search.SearchAsync(new EngineQuery("x"));
        _time.Advance(TimeSpan.FromSeconds(3));
        var response = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        finish.SetResult();

        Assert.Equal("https://example.com/one", Assert.Single(response.Results).Url);
        var failure = Assert.Single(response.Failures);
        Assert.Equal("slow", failure.Engine);
        Assert.Equal(EngineFailureKind.Timeout, failure.Kind);
    }

    [Fact]
    public async Task AParse_DoesNotHoldUpTheCallersCancel()
    {
        var finish = new TaskCompletionSource();
        using var cancel = new CancellationTokenSource();
        var search = Search(StillParsing("slow", finish.Task));

        var pending = search.SearchAsync(new EngineQuery("x"), cancel.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
        finish.SetResult();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnEngineStillParsing_IsNotAskedAgain_UntilThatParseEnds(bool canceled)
    {
        var finish = new TaskCompletionSource();
        var engine = StillParsing("slow", finish.Task);
        var search = Search(engine);
        var query = new EngineQuery("x");
        using var cancel = new CancellationTokenSource();

        var pending = search.SearchAsync(query, cancel.Token);
        if (canceled)
        {
            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        else
        {
            _time.Advance(TimeSpan.FromSeconds(3));
            Assert.Equal(EngineFailureKind.Timeout, Assert.Single((await pending.WaitAsync(TimeSpan.FromSeconds(10))).Failures).Kind);
        }

        // Past any rest, so only the unfinished parse can be holding it back.
        _time.Advance(TimeSpan.FromSeconds(6));
        Assert.False(search.AnyAvailable(query));
        var held = Assert.Single((await search.SearchAsync(query)).Failures);
        Assert.Equal(EngineFailureKind.Suspended, held.Kind);
        Assert.Equal(EngineFailureKind.Timeout, held.RestCause);
        Assert.Equal(1, engine.Calls);

        finish.SetResult();
        await WaitUntil(() => search.AnyAvailable(query));
        Assert.Single((await search.SearchAsync(query)).Results);
        Assert.Equal(2, engine.Calls);
    }

    [Fact]
    public async Task AnyAvailable_SaysWhetherASearchWouldAskAnEngine()
    {
        var search = Search(Failing("a", new HttpRequestException("refused")));
        var query = new EngineQuery("x");

        Assert.True(search.AnyAvailable(query));
        Assert.False(search.AnyAvailable(new EngineQuery("x", Engines: new HashSet<string> { "other" })));
        Assert.False(search.AnyAvailable(new EngineQuery("x", PageNumber: 2)));

        await search.SearchAsync(query);

        Assert.False(search.AnyAvailable(query));
        _time.Advance(TimeSpan.FromSeconds(6));
        Assert.True(search.AnyAvailable(query));
    }
}
