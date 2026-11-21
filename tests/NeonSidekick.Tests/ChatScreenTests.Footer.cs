using System.Net;
using NeonSidekick.App;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// The reply footer at the screen (2026-11-01, feature idea #4): off by default; on, a dim line under each reply that stands (a cancelled one
/// under its notice), none for a withdrawn message; a slow tool's time on its result line, less the time a pane inside it waited on the user.
/// </summary>
public partial class ChatScreenTests
{
    /// <summary><c>web_fetch</c> of a page whose server takes <paramref name="took"/> on the screen's clock, then the reply "Read it.".</summary>
    private void SlowFetch(TimeSpan took)
    {
        _http.Map("https://example.com/", (_, _) =>
        {
            _time.Advance(took);
            return Task.FromResult(StubHttpMessageHandler.Json(HttpStatusCode.OK, "<html><body><p>Hello from the page.</p></body></html>", "text/html"));
        });
        _chat.Enqueue(FakeChatClient.Call("c1", WebFetchTool.ToolName, new Dictionary<string, object?> { [WebFetchTool.UrlArgument] = "https://example.com/" }), FakeChatClient.Usage(100, 20));
        _chat.Enqueue(FakeChatClient.Text("Read it."), FakeChatClient.Usage(200, 812));
    }

    [Fact]
    public async Task ReplyFooter_IsOffByDefault_NoLineAndNoToolTime()
    {
        _settings.Update(d => { d.TtsOutput = false; d.WebTools = true; });
        SlowFetch(TimeSpan.FromSeconds(3));
        LinesWhenIdle("read it", "/exit");

        string output = await RunAsync();

        Assert.Contains("Read it.", output);
        Assert.DoesNotContain("832 tok", output);
        Assert.DoesNotContain(" · 3.0s", output);
    }

    [Fact]
    public async Task ReplyFooter_On_TimeTokensAndTools_UnderTheReply_AndASlowToolsTimeOnItsLine()
    {
        _settings.Update(d => { d.TtsOutput = false; d.WebTools = true; d.ReplyFooter = true; d.WebFooter = true; });
        SlowFetch(TimeSpan.FromSeconds(3));
        LinesWhenIdle("read it", "/exit");

        string output = await RunAsync();

        Assert.Matches(@"Hello from the page\. · 3\.0s\n", output);   // web_fetch's one-line note ends with the tool's time
        Assert.Matches(@"Read it\.\n📚 [^\n]+\n3\.0s · 832 tok · (\d+ tok/s · )?1 tool call( · [^\n]+)?\n\n", output);   // both requests' output; one model call; the page read listed above it (2026-11-15)
    }

    [Fact]
    public async Task ReplyFooter_AFastTool_ShowsNoTime_AndTheFooterStill()
    {
        _settings.Update(d => { d.TtsOutput = false; d.WebTools = true; d.ReplyFooter = true; d.WebFooter = true; });
        SlowFetch(TimeSpan.FromSeconds(1.5));
        LinesWhenIdle("read it", "/exit");

        string output = await RunAsync();

        Assert.DoesNotContain(" · 1.5s", output);
        Assert.Matches(@"Read it\.\n📚 [^\n]+\n1\.5s · 832 tok", output);
    }

    /// <summary>Web footer off (the default since 2026-11-21, the user's ask): a page read leaves no 📚 line, at the PC or on the page; the reply footer still drawn.</summary>
    [Fact]
    public async Task WebFooter_IsOffByDefault_APageReadLeavesNoSourcesLine_TheReplyFooterStill()
    {
        _settings.Update(d => { d.TtsOutput = false; d.WebTools = true; d.ReplyFooter = true; });
        SlowFetch(TimeSpan.FromSeconds(3));
        LinesWhenIdle("read it", "/exit");

        string output = await RunAsync();

        Assert.DoesNotContain("📚", output);
        Assert.Matches(@"Read it\.\n3\.0s · 832 tok", output);
        Assert.DoesNotContain(Web.Feed.Snapshot(m => m), e => e.Kind == "sources");
    }

    /// <summary>A stored turn's sources (2026-11-21): a loaded session draws no 📚 line with Web footer off, and draws it, at the PC and on the page, with it on.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WebFooter_ARestoredTurnsSources_FollowTheSetting(bool on)
    {
        _settings.Update(d => { d.TtsOutput = false; d.WebFooter = on; });
        long id = SeedSession(sources: [new NeonSidekick.Web.WebSource(NeonSidekick.Web.WebSourceKind.Page, "https://example.com/vosk", "Vosk models")]);
        PushLine("/sessions " + id);
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("Pass the folder to the detector.", output);
        Assert.Equal(on, output.Contains("📚", StringComparison.Ordinal));
        Assert.Equal(on, output.Contains("Vosk models", StringComparison.Ordinal));
        Assert.Equal(on, Web.Feed.Snapshot(m => m).Any(e => e.Kind == "sources"));
    }

    /// <summary>A pane inside a tool (the bot check's ask) answered 30 s later: neither the tool's time nor the footer's counts that wait.</summary>
    [Fact]
    public async Task ReplyFooter_ATimeWaitingOnAPaneInsideATool_IsLeftOut()
    {
        BotCheckFixture([Keys.Enter], "Understood.");
        _settings.Update(d => d.ReplyFooter = true);
        var input = _scripted!;
        var answer = input.OnWait!;
        bool waited = false;
        input.OnWait = () =>
        {
            if (!waited && _keys is { PendingLine.IsCompleted: false })
            {
                waited = true;
                _time.Advance(TimeSpan.FromSeconds(30));   // the user thinks it over
            }

            answer();
        };

        string output = await RunAsync();

        Assert.True(waited);
        Assert.DoesNotContain("30.0s", output);
        Assert.Matches(@"Understood\.\n0\.0s · ", output);
    }

    [Fact]
    public async Task ReplyFooter_ACancelledReply_HasItUnderTheNotice()
    {
        _settings.Update(d => { d.TtsOutput = false; d.ReplyFooter = true; });
        _chat.EnqueueText("One. ", "Two.");
        _chat.BeforeUpdate = async (i, ct) =>
        {
            if (i == 1)
            {
                _time.Advance(TimeSpan.FromSeconds(4));
                Scripted().Push(Keys.Escape);
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(5, CancellationToken.None);
                }
            }
        };
        LinesWhenIdle("hi", "/exit");

        string output = await RunAsync();

        Assert.Matches(@"● One\.\n  · " + System.Text.RegularExpressions.Regex.Escape(ChatScreen.CancelledNotice) + @"\n4\.0s[^\n]*\n\n", output);
    }

    [Fact]
    public async Task ReplyFooter_AWithdrawnMessage_HasNone()
    {
        _settings.Update(d => { d.TtsOutput = false; d.ReplyFooter = true; });
        _chat.EnqueueText("One.").EnqueueText("Two.");
        _chat.BeforeUpdate = async (i, ct) =>
        {
            if (i == 0 && _chat.Requests.Count == 1)
            {
                _time.Advance(TimeSpan.FromSeconds(7));
                _scripted!.Push(Keys.Escape);
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(5, CancellationToken.None);
                }
            }
        };
        StepsWhenIdle(Line("hi"), Key(Keys.Enter), Line("/exit"));

        string output = await RunAsync();

        Assert.Contains("● " + ChatScreen.WithdrawnNotice, output);
        Assert.DoesNotContain("7.0s", output);
        Assert.Matches(@"● Two\.\n0\.0s[^\n]*\n\n", output);   // the re-sent message's reply has its own
    }
}
