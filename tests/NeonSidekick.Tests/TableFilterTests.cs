using System.Text;
using NeonSidekick.Speech;

namespace NeonSidekick.Tests;

/// <summary>
/// <see cref="TableFilter"/> (2026-09-26): pipe tables never reach the voice — header, delimiter and body rows become one
/// newline — however the stream is split; every other line passes through unchanged.
/// </summary>
public class TableFilterTests
{
    private static string Run(params string[] deltas)
    {
        var filter = new TableFilter();
        var text = new StringBuilder();
        foreach (string delta in deltas)
        {
            text.Append(filter.Push(delta));
        }

        return text.Append(filter.Flush()).ToString();
    }

    [Fact]
    public void ATable_IsDropped_WithEveryRow_AndTheSentencesEitherSideStayApart()
    {
        Assert.Equal("Compare:\n\nThat is all.", Run("Compare:\n| Name | Role |\n|---|---|\n| Ada | Engineer |\n| Bob | Pilot |\nThat is all."));
    }

    [Theory]
    [InlineData("a\n|x|y|\n|:--|--:|\n|1|2|\nb", "a\n\nb")]                 // alignment colons; the body ends at a non-pipe line, kept
    [InlineData("a\n| x |\n| :-: |\n| 1 |\n\nb", "a\n\n\nb")]               // a centred column; a blank line ends the body and stays
    [InlineData("a\n   | x | y |\n   |---|---|\n   | 1 | 2 |\nb", "a\n\nb")] // up to three spaces of indent
    [InlineData("a\n| x |\n|---|", "a\n\n")]                                // a delimiter row at the very end, no newline
    [InlineData("a\n| x |\n|---|\n| 1 |", "a\n\n")]                         // a body row at the very end
    [InlineData("| x |\n|---|\n", "\n")]                                    // a table alone
    [InlineData("| lone |\n| x |\n|---|\n| 1 |\nb", "| lone |\n\nb")]       // a pipe line with no delimiter, then a table
    public void Tables_FollowTheRules(string text, string expected)
    {
        Assert.Equal(expected, Run(text));
    }

    [Theory]
    [InlineData("| a | b |\n")]                      // a lone row: no delimiter after it
    [InlineData("| a | b |\nThen prose.\n")]
    [InlineData("| a | b |")]                        // at the end, no newline
    [InlineData("| a |\n|\n")]                       // a bare pipe is no delimiter row
    [InlineData("| a |\n|-x-|\n")]
    [InlineData("| a |\n|::-|\n")]
    [InlineData("a || b is true\n")]                 // pipes mid-line
    [InlineData("x | y\n--|--\n1 | 2\n")]            // no leading pipes: not caught
    [InlineData("    | a |\n    |---|\n")]          // indented four: not caught
    [InlineData("   \nspaces only\n")]
    public void AnythingElse_PassesThrough(string text)
    {
        Assert.Equal(text, Run(text));
    }

    [Fact]
    public void SplitAtEveryPosition_GivesTheSameResult()
    {
        const string reply = "Intro line.\n| lone |\n|x|\n| a | b |\n|:-|-:|\n|1|2|\n\n  | c |\n  |---|\n| 3 |\nThe end. a || b";
        string whole = Run(reply);
        Assert.Equal("Intro line.\n| lone |\n|x|\n\n\n\nThe end. a || b", whole);

        for (int i = 1; i < reply.Length; i++)
        {
            Assert.Equal(whole, Run(reply[..i], reply[i..]));
        }

        Assert.Equal(whole, Run(reply.Select(c => c.ToString()).ToArray()));
    }

    [Theory]
    [InlineData("Compare:\n| Name | Role |\n|---|---|\n| Ada | Engineer |\nThat is all.")]
    [InlineData("a\n| x |\n|---|")]
    [InlineData("a\n| x |\n|---|\n| 1 |\n\n| y |\n|:-:|\nb")]
    [InlineData("| lone |\n| x |\n|---|\n| 1 |\nb")]
    [InlineData("no table at all | here")]
    [InlineData("| a |")]
    public void Spans_AreWhatStripReplaces(string text)
    {
        var rebuilt = new StringBuilder();
        int cursor = 0;
        foreach (var (start, end) in TableFilter.Spans(text))
        {
            rebuilt.Append(text, cursor, start - cursor).Append('\n');
            cursor = end;
        }

        rebuilt.Append(text, cursor, text.Length - cursor);
        Assert.Equal(TableFilter.Strip(text), rebuilt.ToString());
    }
}
