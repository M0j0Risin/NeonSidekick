using System.Text;
using NeonSidekick.Speech;

namespace NeonSidekick.Tests;

/// <summary>
/// <see cref="CodeBlockFilter"/> (2026-09-25): fenced code blocks never reach the voice — the fences and everything between
/// them become one newline — however the stream is split; every other line passes through unchanged.
/// </summary>
public class CodeBlockFilterTests
{
    private static string Run(params string[] deltas)
    {
        var filter = new CodeBlockFilter();
        var text = new StringBuilder();
        foreach (string delta in deltas)
        {
            text.Append(filter.Push(delta));
        }

        return text.Append(filter.Flush()).ToString();
    }

    [Fact]
    public void ABlock_IsDropped_WithItsFences_AndTheSentencesEitherSideStayApart()
    {
        Assert.Equal("Here:\n\nThat prints it.", Run("Here:\n```csharp\nConsole.WriteLine(1);\n```\nThat prints it."));
    }

    [Theory]
    [InlineData("a\n~~~\ncode\n~~~\nb", "a\n\nb")]                          // tildes
    [InlineData("a\n```\ncode\n`````\nb", "a\n\nb")]                        // a longer closing fence
    [InlineData("a\n````md\n```\ninner\n```\n````\nb", "a\n\nb")]           // a four-tick block holding three-tick fences
    [InlineData("a\n```\ncode\n~~~\nstill code\n```\nb", "a\n\nb")]         // the other character does not close it
    [InlineData("a\n   ```\ncode\n   ```\nb", "a\n\nb")]                    // up to three spaces of indent
    [InlineData("```\ncode\n```", "\n")]                                    // a block alone
    [InlineData("a\n```py\nnever closed.\nstill code", "a\n\n")]            // unclosed: code to the end
    [InlineData("a\n```", "a\n\n")]                                         // a fence line with nothing after it
    public void Fences_FollowTheCommonMarkRules(string text, string expected)
    {
        Assert.Equal(expected, Run(text));
    }

    [Theory]
    [InlineData("Use `x` here.\n")]
    [InlineData("``not a fence`` at all\n")]
    [InlineData("```x``` is inline code\n")]              // a backtick in a backtick fence's info string: no fence
    [InlineData("    ```\nindented four: not a fence\n")]
    [InlineData("   \nspaces only\n")]
    [InlineData("~~ strike ~~")]
    [InlineData("ends with ``")]
    public void AnythingElse_PassesThrough(string text)
    {
        Assert.Equal(text, Run(text));
    }

    [Fact]
    public void SplitAtEveryPosition_GivesTheSameResult()
    {
        const string reply = "Intro line.\n``\n```js\nlet a = 1;\n```\n~~ not a fence\n  ~~~\nx\n~~~~\nThe end.";
        string whole = Run(reply);
        Assert.Equal("Intro line.\n``\n\n~~ not a fence\n\nThe end.", whole);

        for (int i = 1; i < reply.Length; i++)
        {
            Assert.Equal(whole, Run(reply[..i], reply[i..]));
        }

        Assert.Equal(whole, Run(reply.Select(c => c.ToString()).ToArray()));
    }

    [Theory]
    [InlineData("Here:\n```csharp\nConsole.WriteLine(1);\n```\nThat prints it.")]
    [InlineData("a\n```py\nnever closed.\nstill code")]
    [InlineData("a\n~~~\n1\n~~~\nb\n```\n2\n```\nc")]
    [InlineData("no code at all")]
    [InlineData("a\n```")]
    public void Spans_AreWhatStripReplaces(string text)
    {
        var rebuilt = new StringBuilder();
        int cursor = 0;
        foreach (var (start, end) in CodeBlockFilter.Spans(text))
        {
            rebuilt.Append(text, cursor, start - cursor).Append('\n');
            cursor = end;
        }

        rebuilt.Append(text, cursor, text.Length - cursor);
        Assert.Equal(CodeBlockFilter.Strip(text), rebuilt.ToString());
    }
}
