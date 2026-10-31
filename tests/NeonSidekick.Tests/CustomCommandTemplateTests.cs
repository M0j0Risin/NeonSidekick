using NeonSidekick.CustomCommands;

namespace NeonSidekick.Tests;

/// <summary>
/// The placeholders as Claude Code reads them (code.claude.com/docs/en/slash-commands, read 2026-10-30): 0-based words, shell-style quoting,
/// named arguments read whole, the one-backslash escape, one pass, and the <c>ARGUMENTS:</c> line when nothing took the text.
/// </summary>
public class CustomCommandTemplateTests
{
    private static string Expand(string body, string args, params string[] names) => CustomCommandTemplate.Expand(body, args, names).Text;

    [Fact]
    public void Arguments_IsTheTypedText_Trimmed()
    {
        Assert.Equal("Fix issue 123 high", Expand("Fix issue $ARGUMENTS", "  123 high "));
    }

    [Fact]
    public void IndexedPlaceholders_AreZeroBased()
    {
        Assert.Equal("first=a second=b", Expand("first=$0 second=$1", "a b"));
        Assert.Equal("first=a second=b", Expand("first=$ARGUMENTS[0] second=$ARGUMENTS[1]", "a b"));
    }

    [Fact]
    public void Quotes_GroupWords_AndAreTakenOff()
    {
        Assert.Equal(["hello world", "second"], CustomCommandTemplate.SplitWords("\"hello world\" second"));
        Assert.Equal(["single quoted", "x"], CustomCommandTemplate.SplitWords("'single quoted' x"));
        Assert.Equal(["don't", "stop"], CustomCommandTemplate.SplitWords("don't stop"));   // a quote mid-word opens nothing
        Assert.Equal(["\"open"], CustomCommandTemplate.SplitWords("\"open"));            // no match: kept as typed
        Assert.Equal("[hello world]", Expand("[$0]", "\"hello world\" second"));
    }

    [Fact]
    public void AMissingIndexedWord_StaysAsWritten()
    {
        Assert.Equal("a and $2", Expand("$0 and $2", "a b"));
        Assert.Equal("$ARGUMENTS[5] then a", Expand("$ARGUMENTS[5] then $0", "a"));
    }

    [Fact]
    public void ManyDigits_AreOneIndex()
    {
        var words = string.Join(' ', Enumerable.Range(0, 11).Select(i => "w" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal("w10", Expand("$10", words));
    }

    [Fact]
    public void NamedArguments_TakeTheWordsInOrder_AndAMissingOneIsEmpty()
    {
        Assert.Equal("issue 42 on main", Expand("issue $issue on $branch", "42 main", "issue", "branch"));
        Assert.Equal("issue 42 on .", Expand("issue $issue on $branch.", "42", "issue", "branch"));
    }

    [Fact]
    public void ANamedArgument_IsReadWhole()
    {
        // $pathname is never $path + "name"; a $word that is no declared name (a tool mention) is left alone.
        Assert.Equal("$pathname and x.cs", Expand("$pathname and $path", "x.cs", "path"));
        Assert.Equal("use $web_search for x", Expand("use $web_search for $0", "x"));
    }

    [Fact]
    public void OneBackslash_MakesAPlaceholderLiteral()
    {
        Assert.Equal("costs $1.00 for a", Expand("costs \\$1.00 for $0", "a"));
        Assert.Equal("\\\\a", Expand("\\\\$0", "a"));       // two backslashes: both kept, and it still expands
        Assert.Equal("\\$x a", Expand("\\$x $0", "a"));      // before any other $: left as it is
    }

    [Fact]
    public void ATypedValue_IsNeverExpandedAgain()
    {
        Assert.Equal("got $1 and $ARGUMENTS", Expand("got $0", "\"$1 and $ARGUMENTS\""));
    }

    [Fact]
    public void ArgumentsLine_IsAddedOnlyWhenNothingTookTheText()
    {
        Assert.Equal("Review the code.\n\nARGUMENTS: src/x.cs", Expand("Review the code.\n", "src/x.cs"));
        Assert.Equal("Review the code.", Expand("Review the code.", "  "));                       // nothing typed: nothing added
        Assert.Equal("Look at .", Expand("Look at $focus.", "", "focus"));                        // a named one counts even when empty
        Assert.Equal("Look at .", Expand("Look at $focus.", "", "focus"));
        Assert.Equal("Fix $3\n\nARGUMENTS: a", Expand("Fix $3", "a"));                           // an indexed one left as written doesn't
        Assert.True(CustomCommandTemplate.Expand("$0", "a", []).Filled);
        Assert.False(CustomCommandTemplate.Expand("plain", "a", []).Filled);
    }

    [Fact]
    public void PictureLabels_AreKeptAsTheirText()
    {
        Assert.Equal("Describe [Image #1] please", Expand("Describe $ARGUMENTS please", "[Image #1]"));
    }

    [Fact]
    public void Escape_WritesTextBackAsItWasSent()
    {
        string text = "costs $5, all of $ARGUMENTS, $web_search stays";
        string escaped = CustomCommandTemplate.Escape(text, out bool may);
        Assert.False(may);
        Assert.Equal("costs \\$5, all of \\$ARGUMENTS, $web_search stays", escaped);
        Assert.Equal(text, Expand(escaped, ""));

        CustomCommandTemplate.Escape("already \\$1", out may);
        Assert.True(may);   // \\$1 still expands: the notice says it may read differently
    }
}
