using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The feed's bounds and its one rendering (2026-10-19, a high-effort review of the Web UI): a <c>/cmd</c> output entry is capped as a thinking
/// block is, the trim passes over the entry growing and the open run's head instead of stopping at them, and a finished reply's HTML is rendered
/// once for the <c>end</c> event and the snapshot together.
/// </summary>
public sealed partial class WebUiTests
{
    [Fact]
    public void Output_IsCappedOnce_ThenKeepsNothingMore_AndSaysNothingMore()
    {
        var feed = new TranscriptFeed();
        feed.AppendOutput(new string('a', TranscriptFeed.MaxOutputChars - 10), isError: false);
        feed.AppendOutput(new string('b', 50), isError: false);
        var changes = Watch(feed);
        feed.AppendOutput("more", isError: false);

        string text = Assert.Single(feed.Snapshot(m => m)).Text;
        Assert.Equal(TranscriptFeed.MaxOutputChars + 1, text.Length);
        Assert.EndsWith("bbb…", text, StringComparison.Ordinal);
        Assert.Empty(changes);   // no delta for a line kept out
    }

    [Fact]
    public void ALongToolRun_IsTrimmed_PastItsOpenHead()
    {
        var feed = new TranscriptFeed { ToolKeep = () => 1 };
        for (int i = 0; i < TranscriptFeed.MaxEntries + 50; i++)
        {
            feed.Add(FeedKind.Tool, "🛠️ grep " + i);
        }

        var entries = feed.Snapshot(m => m);
        Assert.Equal(TranscriptFeed.MaxEntries, entries.Count);
        Assert.Equal("run", entries[0].Kind);   // the open head stays
        Assert.Equal("🛠️ grep 51", entries[1].Text);   // the oldest of its lines went
        Assert.Equal("🛠️ grep " + (TranscriptFeed.MaxEntries + 49), entries[^1].Text);
    }

    [Fact]
    public void AStreamingReply_PastTheCap_DropsWhatIsBefore_NeverItself()
    {
        var feed = new TranscriptFeed();
        feed.Add(FeedKind.Notice, "old");
        feed.Begin(null);

        feed.Append(new string('x', TranscriptFeed.MaxChars + 10));

        var only = Assert.Single(feed.Snapshot(m => m));
        Assert.Equal("assistant", only.Kind);
    }

    [Fact]
    public void AFinishedReply_IsRenderedOnce_ForItsEndAndTheSnapshot()
    {
        var feed = new TranscriptFeed();
        int renders = 0;
        string Html(string markdown)
        {
            renders++;
            return "<p>" + markdown + "</p>";
        }

        feed.Begin(null);
        feed.Append("Hi");
        long seq = feed.Snapshot(m => m)[0].Seq;   // streaming: nothing rendered, nothing cached
        Assert.Null(feed.HtmlOf(seq, Html));
        feed.End();

        Assert.Equal("<p>Hi</p>", feed.HtmlOf(seq, Html));
        Assert.Equal("<p>Hi</p>", feed.Snapshot(Html)[0].Html);
        Assert.Equal("<p>Hi</p>", feed.EntryOf(seq, Html)?.Html);
        Assert.Equal(1, renders);
        Assert.Null(feed.HtmlOf(seq + 100, Html));
    }

    [Fact]
    public void TheSessionsEndEvent_CarriesTheReplysHtml()
    {
        using var subscription = _session.Events.Subscribe();
        _session.Feed.Begin(null);
        _session.Feed.Append("**bold**");
        _session.Feed.End();

        var end = Drain(subscription).Single(e => e.Name == "end");
        var parsed = System.Text.Json.JsonSerializer.Deserialize(end.Json.Value, WebUiJsonContext.Default.WebEnd)!;
        Assert.Equal(WebUiMarkdown.ToHtml("**bold**"), parsed.Html);
        Assert.Equal(parsed.Html, _session.Snapshot().Entries.Single().Html);
    }
}
