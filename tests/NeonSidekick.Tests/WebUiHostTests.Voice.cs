using System.Net;
using System.Text.Json;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The Chat tab's voice endpoints (2026-11-13, the webui-voice plan): each post reaches the voice side with its body and answers with the
/// listen's state; a backend without voice answers them all 404; a clip is a raw GET kept to this origin and never cached, and a page's stream
/// names its page and run so the voice events reach it alone.
/// </summary>
public sealed partial class WebUiHostTests
{
    private const string VoicePage = "page-0123456789abcdef";
    private const string VoiceListen = "listen-0123456789abcdef";

    private HttpRequestMessage ClipGet(string query, bool csrf = true)
    {
        var request = Get(WebUiHost.VoiceClipPath + query);
        if (csrf)
        {
            request.Headers.Add(WebUiHost.CsrfHeader, "1");
        }

        return request;
    }

    [Fact]
    public async Task EachVoicePost_ReachesTheVoiceSide_AndAnswersWithTheListensState()
    {
        var voice = new FakeWebVoice();
        _backend.Voice = voice;

        using var start = await _client.SendAsync(Post(WebUiHost.VoiceStartPath, $$"""{"page":"{{VoicePage}}","listen":"{{VoiceListen}}","speak":true}"""));
        using var chunk = await _client.SendAsync(Post(WebUiHost.VoiceChunkPath, $$"""{"page":"{{VoicePage}}","listen":"{{VoiceListen}}","seq":3,"pcm":"AAAA"}"""));
        using var end = await _client.SendAsync(Post(WebUiHost.VoiceEndPath, $$"""{"page":"{{VoicePage}}","listen":"{{VoiceListen}}","how":"finish"}"""));
        using var speak = await _client.SendAsync(Post(WebUiHost.VoiceSpeakPath, $$"""{"page":"{{VoicePage}}","on":false}"""));
        using var played = await _client.SendAsync(Post(WebUiHost.VoicePlayedPath, $$"""{"page":"{{VoicePage}}","turn":"turn-0123456789abcdef","clip":2,"offset":12000}"""));

        foreach (var response in new[] { start, chunk, end, speak, played })
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("listening", JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(), WebUiJsonContext.Default.WebResult)!.Listen);
        }

        var started = Assert.Single(voice.Starts);
        Assert.Equal((VoicePage, VoiceListen, true), (started.Page, started.Listen, started.Speak));
        var chunked = Assert.Single(voice.Chunks);
        Assert.Equal((3L, "AAAA"), (chunked.Seq, chunked.Pcm));
        Assert.Equal("finish", Assert.Single(voice.Ends).How);
        Assert.False(Assert.Single(voice.Speaks).On);
        Assert.Equal(2, Assert.Single(voice.Played).Clip);
    }

    [Fact]
    public async Task AVoicePostTheVoiceSideRefuses_CarriesItsStatus_AndSentence()
    {
        var voice = new FakeWebVoice { Once = new WebReply(WebOutcome.Conflict, WebUiText.VoiceBusy) };
        _backend.Voice = voice;

        using var refused = await _client.SendAsync(Post(WebUiHost.VoiceStartPath, $$"""{"page":"{{VoicePage}}","listen":"{{VoiceListen}}"}"""));

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(WebUiText.VoiceBusy, JsonSerializer.Deserialize(await refused.Content.ReadAsStringAsync(), WebUiJsonContext.Default.WebResult)!.Message);
    }

    [Fact]
    public async Task WithoutAVoiceSide_EveryVoicePath_Is404_AndNothingIsAsked()
    {
        _backend.Voice = null;

        using var start = await _client.SendAsync(Post(WebUiHost.VoiceStartPath, $$"""{"page":"{{VoicePage}}","listen":"{{VoiceListen}}"}"""));
        using var clip = await _client.SendAsync(ClipGet($"?page={VoicePage}&turn={VoiceListen}&n=0"));

        Assert.Equal(HttpStatusCode.NotFound, start.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, clip.StatusCode);
    }

    [Fact]
    public async Task AClip_IsServedRaw_NeverCached_AndKeptToThisOrigin()
    {
        var voice = new FakeWebVoice();
        voice.Clips[(VoicePage, VoiceListen, 4)] = [1, 2, 3, 4];
        _backend.Voice = voice;

        using var response = await _client.SendAsync(ClipGet($"?page={VoicePage}&turn={VoiceListen}&n=4"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal([1, 2, 3, 4], await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("same-origin", Assert.Single(response.Headers.GetValues("Cross-Origin-Resource-Policy")));
    }

    [Theory]
    [InlineData("?page=" + VoicePage + "&turn=" + VoiceListen + "&n=5")]    // not held
    [InlineData("?page=" + VoicePage + "&turn=" + VoiceListen + "&n=-1")]   // not a plain number
    [InlineData("?page=" + VoicePage + "&turn=" + VoiceListen + "&n=+4")]
    [InlineData("?page=" + VoicePage + "&turn=" + VoiceListen)]
    [InlineData("?page=short&turn=" + VoiceListen + "&n=4")]                 // not the id shape
    public async Task AClipNotHeld_OrAskedWrongly_Is404_WithItsSentence(string query)
    {
        var voice = new FakeWebVoice();
        voice.Clips[(VoicePage, VoiceListen, 4)] = [1, 2];
        _backend.Voice = voice;

        using var response = await _client.SendAsync(ClipGet(query));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(WebUiText.VoiceClipGone, JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(), WebUiJsonContext.Default.WebResult)!.Message);
    }

    [Fact]
    public async Task AClip_WithoutTheHeader_OrAsAHead_IsNeverServed()
    {
        var voice = new FakeWebVoice();
        voice.Clips[(VoicePage, VoiceListen, 4)] = [1, 2];
        _backend.Voice = voice;

        using var noHeader = await _client.SendAsync(ClipGet($"?page={VoicePage}&turn={VoiceListen}&n=4", csrf: false));
        using var head = ClipGet($"?page={VoicePage}&turn={VoiceListen}&n=4");
        head.Method = HttpMethod.Head;
        using var headed = await _client.SendAsync(head);

        Assert.Equal(HttpStatusCode.Forbidden, noHeader.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, headed.StatusCode);
        Assert.Empty(voice.ClipGets);
    }

    [Fact]
    public async Task AStreamNamingItsPage_GetsThatPagesSnapshot_AndItsVoiceEventsAlone()
    {
        const string Load = "load-0123456789abcdef";
        const string Other = "other-0123456789abcdef";
        using var response = await _client.SendAsync(Get("/api/events?page=" + VoicePage + "&load=" + Load), HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        Assert.Equal("event: snapshot", await reader.ReadLineAsync());
        Assert.Contains(VoicePage, _backend.SnapshotPages);

        _backend.Events.Publish(new WebEvent("voice", () => """{"to":"other"}""") { Page = Other });
        _backend.Events.Publish(new WebEvent("voice", () => """{"to":"mine"}""") { Page = VoicePage });

        var lines = new List<string>();
        string? line;
        while ((line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20))) is not null)
        {
            lines.Add(line);
            if (line.Contains("mine", StringComparison.Ordinal))
            {
                break;
            }
        }

        Assert.DoesNotContain(lines, l => l.Contains("other", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("mine", StringComparison.Ordinal));
    }
}
