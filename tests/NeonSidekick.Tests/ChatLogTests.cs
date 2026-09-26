using NeonSidekick.App;

namespace NeonSidekick.Tests;

public class ChatLogTests
{
    [Fact]
    public void Add_TrimsBoth_AndSkipsABlankReply()
    {
        var log = new ChatLog();
        log.Add("  hi  ", "\n\nHello.\n");
        log.Add("errored", "   ");
        log.Add("", "a reply to nothing");

        Assert.Equal(2, log.Count);
        Assert.Equal("a reply to nothing", log.Markdown(1, includeUser: false));
        Assert.Equal("Hello.\n\n---\n\na reply to nothing", log.Markdown(2, includeUser: false));
        Assert.Equal("> hi\n\nHello.\n\n---\n\n>\n\na reply to nothing", log.Markdown(2, includeUser: true));
    }

    [Fact]
    public void Markdown_TakesTheLastN_OldestFirst_WithTheRuleBetween()
    {
        var log = new ChatLog();
        log.Add("q1", "r1");
        log.Add("q2", "r2\nline 2");
        log.Add("q3", "r3");

        Assert.Equal("\n\n---\n\n", ChatLog.Separator);
        Assert.Equal("r3", log.Markdown(1, false));
        Assert.Equal("r2\nline 2\n\n---\n\nr3", log.Markdown(2, false));
        Assert.Equal("r1\n\n---\n\nr2\nline 2\n\n---\n\nr3", log.Markdown(3, false));
        Assert.Equal("r1\n\n---\n\nr2\nline 2\n\n---\n\nr3", log.Markdown(99, false));
        Assert.Equal("r3", log.Markdown(0, false));
        Assert.Equal("r3", log.Markdown(-5, false));
        Assert.Equal("> q2\n\nr2\nline 2\n\n---\n\n> q3\n\nr3", log.Markdown(2, true));
    }

    [Fact]
    public void Take_ClampsToAtLeastOneAndAtMostEverything_ZeroWhenEmpty()
    {
        var log = new ChatLog();
        Assert.Equal(0, log.Take(1));
        Assert.Equal(0, log.Take(int.MaxValue));
        Assert.Equal("", log.Markdown(1, true));

        log.Add("q", "r");
        log.Add("q", "r");
        Assert.Equal(1, log.Take(0));
        Assert.Equal(1, log.Take(-1));
        Assert.Equal(1, log.Take(1));
        Assert.Equal(2, log.Take(2));
        Assert.Equal(2, log.Take(int.MaxValue));

        log.Clear();
        Assert.Equal(0, log.Count);
        Assert.Equal(0, log.Take(1));
    }

    // ── The thinking (/copy --thinking, 2026-09-26) ──────────────────────────

    [Fact]
    public void ThoughtTrail_JoinsAPieceToTheOpenBlock_AndOpensANewOneAfterEnd()
    {
        var trail = new ChatLog.ThoughtTrail();
        trail.Append(0, "Pondering ");
        trail.Append(0, "the sky. ");
        trail.End();
        trail.End();
        trail.Append(12, "   ");                // a blank block is left out
        trail.End();
        trail.Append(20, "\nThe tool said so.\n");

        Assert.Equal(
            new[] { new ChatLog.Thought(0, "Pondering the sky."), new ChatLog.Thought(20, "The tool said so.") },
            trail.Thoughts);
    }

    [Fact]
    public void Markdown_WithThinking_QuotesEachBlockWhereItHappened()
    {
        var log = new ChatLog();
        string reply = "\n\nLet me look.The answer is 4.";
        int second = reply.IndexOf("The", StringComparison.Ordinal);
        log.Add("q", reply, [new(0, " Hmm, a sum.\n\nTwo and two. "), new(second, "The tool agrees."), new(reply.Length, "Done.")]);

        string first = ChatLog.ThinkingHeader + "\n>\n> Hmm, a sum.\n>\n> Two and two.";
        Assert.Equal("> 💭 **Thinking**", ChatLog.ThinkingHeader);
        Assert.Equal(
            first + "\n\nLet me look.\n\n" + ChatLog.ThinkingQuote("The tool agrees.") + "\n\nThe answer is 4.\n\n" + ChatLog.ThinkingQuote("Done."),
            log.Markdown(1, includeUser: false, includeThinking: true));
        Assert.Equal("> q\n\n" + first, log.Markdown(1, includeUser: true, includeThinking: true)[..("> q\n\n" + first).Length]);

        // Without the switch: the reply as ever.
        Assert.Equal("Let me look.The answer is 4.", log.Markdown(1, includeUser: false));
    }

    [Fact]
    public void Markdown_WithThinking_OverSeveralExchanges_AndOnesWithout()
    {
        var log = new ChatLog();
        log.Add("q1", "r1");
        log.Add("q2", "r2", [new(0, "t2")]);
        log.Add("q3", "   ", [new(0, "lost with its blank reply")]);

        Assert.Equal(2, log.Count);
        Assert.Equal("r1" + ChatLog.Separator + ChatLog.ThinkingQuote("t2") + "\n\nr2", log.Markdown(2, false, includeThinking: true));
        Assert.Equal("r1" + ChatLog.Separator + "r2", log.Markdown(2, false));
    }

    [Fact]
    public void Quote_PrefixesEveryLine_AnEmptyLineIsABareMarker()
    {
        Assert.Equal("> one", ChatLog.Quote("one"));
        Assert.Equal("> a\n> b", ChatLog.Quote("a\nb"));
        Assert.Equal("> a\n> b", ChatLog.Quote("a\r\nb"));
        Assert.Equal("> a\n>\n> b", ChatLog.Quote("a\n\nb"));
        Assert.Equal(">", ChatLog.Quote(""));
    }
}
