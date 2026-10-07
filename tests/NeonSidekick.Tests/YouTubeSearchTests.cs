using System.Net;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Web;
using NeonSidekick.YouTube;

namespace NeonSidekick.Tests;

/// <summary>
/// YouTube's half of the plan that needs no window (2026-10-05): a video named any way (<see cref="YouTubeIds"/>), the Data
/// API's answers read from fixtures in its shapes, the key kept out of the URL, the two calls and every refusal as its sentence.
/// </summary>
public class YouTubeSearchTests
{
    private const string Key = "AIzaTestKey-not-real";

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "youtube", name));

    // ── YouTubeIds ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("aqz-KE-bpKQ", "aqz-KE-bpKQ", 0)]
    [InlineData("  aqz-KE-bpKQ ", "aqz-KE-bpKQ", 0)]
    [InlineData("https://www.youtube.com/watch?v=aqz-KE-bpKQ", "aqz-KE-bpKQ", 0)]
    [InlineData("https://www.youtube.com/watch?v=aqz-KE-bpKQ&t=90s", "aqz-KE-bpKQ", 90)]
    [InlineData("https://www.youtube.com/watch?list=PL1&v=aqz-KE-bpKQ&t=1m30s&index=2", "aqz-KE-bpKQ", 90)]
    [InlineData("youtube.com/watch?v=aqz-KE-bpKQ", "aqz-KE-bpKQ", 0)]
    [InlineData("http://m.youtube.com/watch?v=aqz-KE-bpKQ&t=1h2m3s", "aqz-KE-bpKQ", 3723)]
    [InlineData("https://music.youtube.com/watch?v=aqz-KE-bpKQ", "aqz-KE-bpKQ", 0)]
    [InlineData("https://youtu.be/aqz-KE-bpKQ?t=42", "aqz-KE-bpKQ", 42)]
    [InlineData("https://youtu.be/aqz-KE-bpKQ?si=shareTracker", "aqz-KE-bpKQ", 0)]
    [InlineData("https://www.youtube.com/shorts/aqz-KE-bpKQ", "aqz-KE-bpKQ", 0)]
    [InlineData("https://www.youtube.com/embed/aqz-KE-bpKQ?start=120", "aqz-KE-bpKQ", 120)]
    [InlineData("https://www.youtube-nocookie.com/embed/aqz-KE-bpKQ", "aqz-KE-bpKQ", 0)]
    [InlineData("https://www.youtube.com/live/aqz-KE-bpKQ?feature=share", "aqz-KE-bpKQ", 0)]
    [InlineData("https://www.youtube.com/watch?v=aqz-KE-bpKQ#t=2:05", "aqz-KE-bpKQ", 125)]
    public void TryParse_ReadsTheIdAndTheStart(string text, string id, double start)
    {
        Assert.True(YouTubeIds.TryParse(text, out string parsed, out double at));
        Assert.Equal(id, parsed);
        Assert.Equal(start, at);
    }

    [Theory]
    [InlineData("")]
    [InlineData("lofi beats")]
    [InlineData("https://www.youtube.com/@Blender")]
    [InlineData("https://www.youtube.com/watch?v=short")]
    [InlineData("https://example.com/watch?v=aqz-KE-bpKQ")]
    [InlineData("https://vimeo.com/aqz-KE-bpKQ")]
    [InlineData("ftp://youtu.be/aqz-KE-bpKQ")]
    [InlineData(null)]
    public void TryParse_RefusesWhatNamesNoVideo(string? text)
    {
        Assert.False(YouTubeIds.TryParse(text, out _, out _));
    }

    [Theory]
    [InlineData("90", 90.0)]
    [InlineData("90.5", 90.5)]
    [InlineData("90s", 90.0)]
    [InlineData("2m", 120.0)]
    [InlineData("1m30s", 90.0)]
    [InlineData("1H2M3S", 3723.0)]
    [InlineData("1:30", 90.0)]
    [InlineData("1:02:03", 3723.0)]
    [InlineData("0:05", 5.0)]
    public void ParseTime_ReadsEveryForm(string text, double seconds)
    {
        Assert.Equal(seconds, YouTubeIds.ParseTime(text));
    }

    [Theory]
    [InlineData("-5")]
    [InlineData("1:75")]
    [InlineData("1::3")]
    [InlineData("soon")]
    [InlineData("1m30")]
    [InlineData("")]
    public void ParseTime_RefusesTheRest(string text)
    {
        Assert.Null(YouTubeIds.ParseTime(text));
    }

    [Fact]
    public void FormatTime_AndWatchUrl()
    {
        Assert.Equal("0:00", YouTubeIds.FormatTime(-3));
        Assert.Equal("3:07", YouTubeIds.FormatTime(187.9));
        Assert.Equal("1:02:03", YouTubeIds.FormatTime(3723));
        Assert.Equal("https://www.youtube.com/watch?v=aqz-KE-bpKQ", YouTubeIds.WatchUrl("aqz-KE-bpKQ"));
        Assert.Equal("https://www.youtube.com/watch?v=aqz-KE-bpKQ&t=90s", YouTubeIds.WatchUrl("aqz-KE-bpKQ", 90.7));
    }

    // ── The Data API's answers ───────────────────────────────────────────────

    [Fact]
    public void ParseSearch_TakesTheVideos_DecodesTheTitles_AndSkipsAChannel()
    {
        var hits = YouTubeDataApi.ParseSearch(Fixture("search.json"));

        Assert.Equal(["aqz-KE-bpKQ", "jfKfPfyJRdk", "jNQXAC9IVRw"], hits.Select(h => h.Id));
        Assert.Equal(new YouTubeHit("aqz-KE-bpKQ", "Big Buck Bunny 60fps 4K - Official Blender Foundation Short Film", "Blender", new DateTimeOffset(2014, 11, 10, 14, 5, 55, TimeSpan.Zero), null, null), hits[0]);
        Assert.Equal("lofi hip hop radio 📚 beats to relax/study to", hits[1].Title);
        Assert.Equal(YouTubeLive.Live, hits[1].Live);
        Assert.Equal("Me at the zoo & the elephants' \"trunks\"", hits[2].Title);
    }

    [Fact]
    public void ParseVideos_ReadsLengthsAndCounts()
    {
        var details = YouTubeDataApi.ParseVideos(Fixture("videos.json"));

        Assert.Equal((TimeSpan.FromSeconds(635), 21543678L), details["aqz-KE-bpKQ"]);
        Assert.Equal(((TimeSpan?)null, 947L), details["jfKfPfyJRdk"]);   // P0D: a broadcast has no length
        Assert.Equal((TimeSpan.FromSeconds(19), (long?)null), details["jNQXAC9IVRw"]);   // the count hidden
    }

    [Theory]
    [InlineData("PT10M35S", 635)]
    [InlineData("PT1H2M3S", 3723)]
    [InlineData("P1DT2H", 93600)]
    [InlineData("PT15S", 15)]
    [InlineData("PT4M", 240)]
    public void ParseDuration_ReadsYouTubesIso8601(string text, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), YouTubeDataApi.ParseDuration(text));
    }

    [Theory]
    [InlineData("P0D")]
    [InlineData("PT0S")]
    [InlineData("10:35")]
    [InlineData("")]
    [InlineData(null)]
    public void ParseDuration_IsNullForNoLength(string? text)
    {
        Assert.Null(YouTubeDataApi.ParseDuration(text));
    }

    [Theory]
    [InlineData("error-quota.json", HttpStatusCode.Forbidden, YouTubeFailure.Quota)]
    [InlineData("error-key.json", HttpStatusCode.BadRequest, YouTubeFailure.KeyInvalid)]
    [InlineData("error-disabled.json", HttpStatusCode.Forbidden, YouTubeFailure.NotEnabled)]
    [InlineData("error-referrer.json", HttpStatusCode.Forbidden, YouTubeFailure.KeyRestricted)]
    public void ParseError_ReadsGooglesReason(string fixture, HttpStatusCode status, YouTubeFailure expected)
    {
        var outcome = YouTubeDataApi.ParseError(status, Fixture(fixture));

        Assert.Equal(expected, outcome.Failure);
        Assert.StartsWith($"HTTP {(int)status}: ", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseError_OfSomethingElse_IsTheStatus()
    {
        Assert.Equal(YouTubeSearchOutcome.Failed(YouTubeFailure.Failed, "HTTP 502"), YouTubeDataApi.ParseError(HttpStatusCode.BadGateway, "<html>Bad gateway</html>"));
        Assert.Equal(YouTubeFailure.Failed, YouTubeDataApi.ParseError(HttpStatusCode.InternalServerError, "{\"error\":{\"code\":500,\"message\":\"Backend Error\",\"errors\":[{\"reason\":\"backendError\"}]}}").Failure);
    }

    // ── The calls ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Search_AsksForEmbeddableVideos_ThenTheirDetails_WithTheKeyInTheHeaderOnly()
    {
        var keys = new List<string?>();
        var stub = new StubHttpMessageHandler()
            .Map("https://www.googleapis.com/youtube/v3/search", (request, _) => Answer(request, keys, "search.json"))
            .Map("https://www.googleapis.com/youtube/v3/videos", (request, _) => Answer(request, keys, "videos.json"));
        var api = new YouTubeDataApi(new HttpClient(stub));

        var outcome = await api.SearchAsync(" big buck bunny ", 3, Key, CancellationToken.None);

        Assert.True(outcome.Ok);
        Assert.Equal(
            ["https://www.googleapis.com/youtube/v3/search?part=snippet&type=video&videoEmbeddable=true&maxResults=3&q=big%20buck%20bunny", "https://www.googleapis.com/youtube/v3/videos?part=contentDetails,statistics&id=aqz-KE-bpKQ,jfKfPfyJRdk,jNQXAC9IVRw"],
            stub.Requests.Select(r => r.Uri.AbsoluteUri));
        Assert.Equal([Key, Key], keys);
        Assert.DoesNotContain(stub.Requests, r => r.Uri.AbsoluteUri.Contains(Key, StringComparison.Ordinal));
        Assert.Equal(TimeSpan.FromSeconds(635), outcome.Hits[0].Duration);
        Assert.Equal(21543678L, outcome.Hits[0].Views);
        Assert.Null(outcome.Hits[1].Duration);
    }

    /// <summary>A saved video's title (2026-10-07): oEmbed with no key, then videos.list with the key only when oEmbed fails.</summary>
    [Fact]
    public async Task Lookup_AsksOEmbedWithoutAKey_AndFallsBackToTheDataApi_OnlyWithOne()
    {
        var keys = new List<string?>();
        var stub = new StubHttpMessageHandler()
            .Map("https://www.youtube.com/oembed", (request, _) => Answer(request, keys, "oembed.json"));
        var info = await new YouTubeDataApi(new HttpClient(stub)).LookupAsync("jNQXAC9IVRw", Key, CancellationToken.None);

        Assert.Equal(new YouTubeVideoInfo("jNQXAC9IVRw", "Me at the zoo", "jawed"), info);
        Assert.Equal("https://www.youtube.com/oembed?format=json&url=https%3A%2F%2Fwww.youtube.com%2Fwatch%3Fv%3DjNQXAC9IVRw", Assert.Single(stub.Requests).Uri.AbsoluteUri);
        Assert.Equal([null], keys);                                                  // oEmbed never gets the key

        // oEmbed refuses (a private or unembeddable video answers 401/404): the Data API, with the key in the header.
        keys.Clear();
        stub = new StubHttpMessageHandler()
            .Map("https://www.youtube.com/oembed", HttpStatusCode.Unauthorized, "Unauthorized")
            .Map("https://www.googleapis.com/youtube/v3/videos", (request, _) => Answer(request, keys, "video-snippet.json"));
        info = await new YouTubeDataApi(new HttpClient(stub)).LookupAsync("jNQXAC9IVRw", Key, CancellationToken.None);
        Assert.Equal(new YouTubeVideoInfo("jNQXAC9IVRw", "Me at the zoo & more", "jawed", 19), info);
        Assert.Equal("https://www.googleapis.com/youtube/v3/videos?part=snippet,contentDetails&id=jNQXAC9IVRw", stub.Requests[^1].Uri.AbsoluteUri);
        Assert.Equal([Key], keys);

        // Without a key, a refused oEmbed is the end; with one, a Data API that finds nothing is too.
        Assert.Null(await new YouTubeDataApi(new HttpClient(stub)).LookupAsync("jNQXAC9IVRw", null, CancellationToken.None));
        stub = new StubHttpMessageHandler()
            .Map("https://www.youtube.com/oembed", HttpStatusCode.NotFound, "Not Found")
            .Map("https://www.googleapis.com/youtube/v3/videos", HttpStatusCode.OK, "{ \"items\": [] }");
        Assert.Null(await new YouTubeDataApi(new HttpClient(stub)).LookupAsync("aqz-KE-bpKQ", Key, CancellationToken.None));
        Assert.Null(await new YouTubeDataApi(new HttpClient(new StubHttpMessageHandler())).LookupAsync("aqz-KE-bpKQ", null, CancellationToken.None));   // no connection: null, not thrown
        Assert.Null(YouTubeDataApi.ParseOEmbed("x", "{ \"title\": \"\" }"));
        Assert.Null(YouTubeDataApi.ParseOEmbed("x", "not json"));
    }

    [Fact]
    public async Task Search_KeepsTheHits_WhenTheDetailsFail()
    {
        var stub = new StubHttpMessageHandler()
            .Map("https://www.googleapis.com/youtube/v3/search", HttpStatusCode.OK, Fixture("search.json"))
            .Map("https://www.googleapis.com/youtube/v3/videos", HttpStatusCode.Forbidden, Fixture("error-quota.json"));

        var outcome = await new YouTubeDataApi(new HttpClient(stub)).SearchAsync("bunny", 5, Key, CancellationToken.None);

        Assert.True(outcome.Ok);
        Assert.Equal(3, outcome.Hits.Count);
        Assert.All(outcome.Hits, h => Assert.Null(h.Duration));
    }

    [Fact]
    public async Task Search_WithNoKey_AsksNothing()
    {
        var stub = new StubHttpMessageHandler();

        var outcome = await new YouTubeDataApi(new HttpClient(stub)).SearchAsync("bunny", 5, "  ", CancellationToken.None);

        Assert.Equal(YouTubeFailure.NoKey, outcome.Failure);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task Search_WithNoHits_SkipsTheDetails()
    {
        var stub = new StubHttpMessageHandler().Map("https://www.googleapis.com/youtube/v3/search", HttpStatusCode.OK, "{\"items\":[]}");

        var outcome = await new YouTubeDataApi(new HttpClient(stub)).SearchAsync("zzzz", 5, Key, CancellationToken.None);

        Assert.True(outcome.Ok);
        Assert.Empty(outcome.Hits);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task Search_SpentQuota_IsTheQuotaFailure()
    {
        var stub = new StubHttpMessageHandler().Map("https://www.googleapis.com/youtube/v3/search", HttpStatusCode.Forbidden, Fixture("error-quota.json"));

        var outcome = await new YouTubeDataApi(new HttpClient(stub)).SearchAsync("bunny", 5, Key, CancellationToken.None);

        Assert.Equal(YouTubeFailure.Quota, outcome.Failure);
        Assert.Single(stub.Requests);
    }

    /// <summary>The network mode reaches this call too: a refusal at the socket is its own sentence, as web_fetch's.</summary>
    [Fact]
    public async Task Search_RefusedByTheNetworkMode_SaysSo()
    {
        var stub = new StubHttpMessageHandler().Map("https://www.googleapis.com/", (_, _) => throw new HttpRequestException("connect failed", new NetworkRefusedException("www.googleapis.com", NetworkRefusal.Internet)));

        var outcome = await new YouTubeDataApi(new HttpClient(stub)).SearchAsync("bunny", 5, Key, CancellationToken.None);

        Assert.Equal(YouTubeFailure.Refused, outcome.Failure);
        Assert.Equal(WebText.InternetRefused("www.googleapis.com"), outcome.Detail);
        Assert.Equal(WebText.InternetRefused("www.googleapis.com"), YouTubeText.Failure(outcome.Failure, outcome.Detail));
    }

    // ── The words ────────────────────────────────────────────────────────────

    [Fact]
    public void Failure_IsASentencePerReason()
    {
        Assert.Contains("YouTube Data API key", YouTubeText.Failure(YouTubeFailure.NoKey, ""));
        Assert.Contains("needs no key", YouTubeText.NoKey);
        Assert.Contains("resets at midnight Pacific time", YouTubeText.Failure(YouTubeFailure.Quota, "HTTP 403: quota"));
        Assert.Contains("(HTTP 400: API key not valid", YouTubeText.Failure(YouTubeFailure.KeyInvalid, "HTTP 400: API key not valid"));
        Assert.Contains("APIs & Services › Library", YouTubeText.Failure(YouTubeFailure.NotEnabled, "HTTP 403: disabled"));
        Assert.Contains("application restrictions should be None", YouTubeText.Failure(YouTubeFailure.KeyRestricted, "HTTP 403"));
        Assert.Contains("within 20 seconds", YouTubeText.Failure(YouTubeFailure.Timeout, ""));
        Assert.Equal("Error: the YouTube search failed (HTTP 502).", YouTubeText.Failure(YouTubeFailure.Failed, "HTTP 502"));
        Assert.All(Enum.GetValues<YouTubeFailure>().Where(f => f != YouTubeFailure.None), f => Assert.StartsWith("Error: ", YouTubeText.Failure(f, ""), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData(947L, "947")]
    [InlineData(12_345L, "12K")]
    [InlineData(1_250L, "1.2K")]
    [InlineData(3_456_789L, "3.4M")]
    [InlineData(21_543_678L, "21M")]
    [InlineData(1_200_000_000L, "1.2B")]
    public void Views_IsTheShortCount(long? views, string text)
    {
        Assert.Equal(text, YouTubeText.Views(views));
    }

    [Fact]
    public void Length_IsTheDurationOrLive()
    {
        var hit = new YouTubeHit("aqz-KE-bpKQ", "t", "c", null, TimeSpan.FromSeconds(635), null);
        Assert.Equal("10:35", YouTubeText.Length(hit));
        Assert.Equal("live", YouTubeText.Length(hit with { Live = YouTubeLive.Live }));
        Assert.Equal("upcoming", YouTubeText.Length(hit with { Live = YouTubeLive.Upcoming, Duration = null }));
        Assert.Equal("", YouTubeText.Length(hit with { Duration = null }));
    }

    [Fact]
    public void Key_IsTheEffectiveOne_Decrypted_OrNull()
    {
        Assert.Null(YouTubeDataApi.Key(new Settings.AppSettingsData()));
        Assert.Null(YouTubeDataApi.Key(new Settings.AppSettingsData { YouTubeApiKey = "   " }));
        Assert.Equal("AIza-plain", YouTubeDataApi.Key(new Settings.AppSettingsData { YouTubeApiKey = " AIza-plain " }));
        if (OperatingSystem.IsWindows())
        {
            string stored = Settings.SettingsSecrets.Protect("AIza-secret", out _);
            Assert.Equal("AIza-secret", YouTubeDataApi.Key(new Settings.AppSettingsData { YouTubeApiKey = stored }));
        }
    }

    /// <summary>The real API (about 101 quota units): a search for the Blender film finds it, with its length.</summary>
    [LiveYouTubeFact]
    public async Task Live_ASearch_FindsBigBuckBunny_WithItsLength()
    {
        using var http = new HttpClient();
        var outcome = await new YouTubeDataApi(http).SearchAsync("big buck bunny blender", 5, LiveYouTube.Key!, CancellationToken.None);

        Assert.True(outcome.Ok, outcome.Failure + " " + outcome.Detail);
        Assert.NotEmpty(outcome.Hits);
        Assert.All(outcome.Hits, h => Assert.True(Viewer.VideoRequest.IsVideoId(h.Id)));
        Assert.Contains(outcome.Hits, h => h.Duration is { TotalSeconds: > 60 } && h.Channel.Length > 0);
    }

    private static Task<HttpResponseMessage> Answer(HttpRequestMessage request, List<string?> keys, string fixture)
    {
        keys.Add(request.Headers.TryGetValues("X-Goog-Api-Key", out var values) ? values.Single() : null);
        return Task.FromResult(StubHttpMessageHandler.Json(HttpStatusCode.OK, Fixture(fixture)));
    }
}

/// <summary>The live YouTube gate (2026-10-05): a Data API key in <see cref="Variable"/>; each run spends about 101 of its quota units.</summary>
internal static class LiveYouTube
{
    public const string Variable = "NEONSIDEKICK_TEST_YOUTUBE_API_KEY";

    public static readonly string? Key = Environment.GetEnvironmentVariable(Variable) is { } key && !string.IsNullOrWhiteSpace(key) ? key.Trim() : null;
}

public sealed class LiveYouTubeFactAttribute : FactAttribute
{
    public LiveYouTubeFactAttribute()
    {
        if (LiveYouTube.Key is null)
        {
            Skip = $"{LiveYouTube.Variable} is not set.";
        }
    }
}
