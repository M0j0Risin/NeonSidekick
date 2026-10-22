using NeonSidekick.Files;

namespace NeonSidekick.Tests;

/// <summary>The nine strategies and the orchestrator, over strings alone (the file side is <c>WorkingDirectoryTests</c>).</summary>
public class FuzzyMatchTests
{
    private static IReadOnlyList<TextSpan> Find(MatchStrategy strategy, string content, string pattern) => FuzzyMatch.Find(strategy, content, pattern);

    private static TextSpan Span(int start, int end) => new(start, end);

    [Fact]
    public void Chain_IsTheNineInOrder_TheLastTwoApproximate()
    {
        Assert.Equal(
            [
                MatchStrategy.Exact, MatchStrategy.LineTrimmed, MatchStrategy.WhitespaceNormalized, MatchStrategy.IndentationFlexible, MatchStrategy.EscapeNormalized,
                MatchStrategy.TrimmedBoundary, MatchStrategy.UnicodeNormalized, MatchStrategy.BlockAnchor, MatchStrategy.ContextAware,
            ],
            FuzzyMatch.Chain);
        Assert.Equal([MatchStrategy.BlockAnchor, MatchStrategy.ContextAware], FuzzyMatch.Chain.Where(FuzzyMatch.IsSimilarity));
        Assert.Equal((5, 8, 0.5, 0.7, 0.8), (FuzzyMatch.MaxLocations, FuzzyMatch.MinAppliedChars, FuzzyMatch.LoneAnchorThreshold, FuzzyMatch.ManyAnchorThreshold, FuzzyMatch.LineThreshold));
    }

    [Fact]
    public void Exact_IsNonOverlapping_EveryOccurrence()
    {
        Assert.Equal([Span(0, 2)], Find(MatchStrategy.Exact, "aaa", "aa"));
        Assert.Equal([Span(0, 1), Span(2, 3)], Find(MatchStrategy.Exact, "a a", "a"));
        Assert.Empty(Find(MatchStrategy.Exact, "abc", "x"));
        Assert.Empty(Find(MatchStrategy.Exact, "abc", ""));
    }

    [Fact]
    public void LineTrimmed_IgnoresEachLinesEdges_SkipsPastAHit_TakesTheTrailingNewline()
    {
        Assert.Equal([Span(0, 7)], Find(MatchStrategy.LineTrimmed, "  x\ny  \nz", "x\n y"));
        // Windows never overlap: a\na in a\na\na is one match at the top, and a replace_all over it never corrupts.
        Assert.Equal([Span(0, 3)], Find(MatchStrategy.LineTrimmed, "a\na\na", "a\na"));
        Assert.Equal([Span(0, 5), Span(6, 11)], Find(MatchStrategy.LineTrimmed, " a\n a\n a\n a", "a\na"));
        Assert.Equal(" b\n b\n b\n b", FuzzyMatch.Replace(" a\n a\n a\n a", "a\na", "b\nb", replaceAll: true).Content);
        // A trailing newline on the pattern is dropped for the comparison and taken back into the span, as the exact strategy would.
        Assert.Equal([Span(0, 4)], Find(MatchStrategy.LineTrimmed, "  x\nrest", "x\n"));
        Assert.Equal([Span(0, 3)], Find(MatchStrategy.LineTrimmed, "  x", "x\n"));
        Assert.Empty(Find(MatchStrategy.LineTrimmed, "x", "x\ny\nz"));
    }

    [Fact]
    public void WhitespaceNormalized_CollapsesRuns_AbsorbsATrailingRunOnlyWhenTheMatchEndedInASpace()
    {
        Assert.Equal([Span(0, 7)], Find(MatchStrategy.WhitespaceNormalized, "int   x = 1;", "int x"));
        Assert.Equal([Span(0, 5)], Find(MatchStrategy.WhitespaceNormalized, "foo  bar", "foo "));   // the match ended in a space: the whole run
        Assert.Equal([Span(0, 3)], Find(MatchStrategy.WhitespaceNormalized, "foo  bar", "foo"));    // it did not: the run stays
        Assert.Equal([Span(0, 5)], Find(MatchStrategy.WhitespaceNormalized, "a\t\t b", "a b"));
        Assert.Empty(Find(MatchStrategy.WhitespaceNormalized, "a b", "a b"));   // neither side changes: the exact strategy's job
        Assert.Equal("a b", FuzzyMatch.CollapseWhitespace("a \t  b", out int[] map));
        Assert.Equal([0, 1, 5, 6], map);
    }

    [Fact]
    public void IndentationFlexible_IgnoresLeadingWhitespace_AndTheReplacementTakesTheFilesIndent()
    {
        string css = ".a {\n    color: red;\n    margin: 0;\n}\n";
        Assert.Equal([Span(5, 35)], Find(MatchStrategy.IndentationFlexible, css, "color: red;\n  margin: 0;"));
        var result = FuzzyMatch.Replace(css, "  color: red;\n  margin: 0;", "  color: blue;\n\n  margin: 1px;", replaceAll: false);
        Assert.Equal(MatchOutcome.Replaced, result.Outcome);
        Assert.Equal(MatchStrategy.LineTrimmed, result.Strategy);   // the earlier strategy wins when both would
        Assert.Equal(".a {\n    color: blue;\n\n    margin: 1px;\n}\n", result.Content);   // re-indented, the blank line kept blank
        Assert.Equal([Span(5, 39)], result.Placed);
        // A trailing space on a line defeats LineTrimmed's exactness only where IndentationFlexible still matches.
        Assert.Equal([Span(0, 5)], Find(MatchStrategy.IndentationFlexible, "  x \nz", "x \n"));
    }

    [Fact]
    public void Reindent_MovesTheOldIndentToTheFiles_LeavesAgreeingIndentsAlone()
    {
        Assert.Equal("    b\n    c", FuzzyMatch.Reindent("    a", "  a", "  b\n  c"));
        Assert.Equal("    b\n\n    c", FuzzyMatch.Reindent("    a", "  a", "  b\n\n c"));   // a line without the old indent is trimmed then indented
        Assert.Equal("  b", FuzzyMatch.Reindent("  a", "  a", "  b"));
        Assert.Equal("b", FuzzyMatch.Reindent("a", "a", "b"));
        Assert.Equal("", FuzzyMatch.Reindent("    a", "a", ""));
        Assert.Equal("\tb", FuzzyMatch.Reindent("\ta", "a", "b"));
    }

    [Fact]
    public void EscapeNormalized_ReadsLiteralEscapes_OnlyWhenThePatternHasThem()
    {
        Assert.Equal([Span(0, 3)], Find(MatchStrategy.EscapeNormalized, "a\nb", "a\\nb"));
        Assert.Equal([Span(0, 3)], Find(MatchStrategy.EscapeNormalized, "a\tb", "a\\tb"));
        Assert.Empty(Find(MatchStrategy.EscapeNormalized, "a\nb", "a\nb"));   // no escapes: never masks the strategies after it
        Assert.Empty(Find(MatchStrategy.EscapeNormalized, "a\\nb", "a\\nb"));  // the file's own backslash-n is not a line break
        Assert.Equal("a\nb\tc\rd\\\\n", FuzzyMatch.Unescape("a\\nb\\tc\\rd\\\\n"));   // a doubled backslash stays as written
        Assert.True(FuzzyMatch.HasEscapes("x\\n"));
        Assert.False(FuzzyMatch.HasEscapes("x\\\\n"));
        // The replacement is unescaped under this strategy alone: the whole call was serialised that way.
        Assert.Equal("a\nc", FuzzyMatch.Replace("a\nb", "a\\nb", "a\\nc", false).Content);
        Assert.Equal("a\\nc", FuzzyMatch.Replace("a\nb", "a\nb", "a\\nc", false).Content);
    }

    [Fact]
    public void TrimmedBoundary_TrimsTheFirstAndLastLinesAlone()
    {
        Assert.Equal([Span(0, 13)], Find(MatchStrategy.TrimmedBoundary, "  a\n  b\n  c  ", "a\n  b\nc"));
        Assert.Empty(Find(MatchStrategy.TrimmedBoundary, "  a\n  b\n  c  ", "a\nb\nc"));   // the middle line's spacing still counts
        Assert.Equal([Span(0, 3)], Find(MatchStrategy.TrimmedBoundary, " a ", "a"));
    }

    [Fact]
    public void UnicodeNormalized_ReadsTypographicCharactersAsPlain_SpansOverTheOriginal()
    {
        Assert.Equal("\"it's\" -- fine... x", FuzzyMatch.UnicodeNormalize("“it’s” — fine… x"));
        Assert.Equal("plain", FuzzyMatch.UnicodeNormalize("plain"));
        string content = "say “hi” — now";
        // The em dash expands to two characters in the normalised text; the span still covers the one original character.
        Assert.Equal([Span(4, 8)], Find(MatchStrategy.UnicodeNormalized, content, "\"hi\""));
        Assert.Equal([Span(9, 10)], Find(MatchStrategy.UnicodeNormalized, content, "--"));
        Assert.Equal([Span(9, 10)], Find(MatchStrategy.UnicodeNormalized, content, "-"));   // a cut inside the expansion takes the whole dash
        Assert.Equal([Span(0, 14)], Find(MatchStrategy.UnicodeNormalized, content, "say \"hi\" -- now"));
        Assert.Equal([Span(0, 14)], Find(MatchStrategy.UnicodeNormalized, content, "  say \"hi\" -- now  "));   // then line-trimmed on the normalised pair
        Assert.Empty(Find(MatchStrategy.UnicodeNormalized, "plain text", "plain"));   // neither side changes
        FuzzyMatch.UnicodeNormalize("a—b", out int[] map);
        Assert.Equal([0, 1, 3, 4], map);
        Assert.Equal(Span(1, 2), FuzzyMatch.MapUnicodeSpan(map, Span(1, 3)));
        Assert.Equal(Span(1, 2), FuzzyMatch.MapUnicodeSpan(map, Span(2, 3)));
        Assert.Equal(Span(0, 3), FuzzyMatch.MapUnicodeSpan(map, Span(0, 4)));
        Assert.Equal(Span(3, 3), FuzzyMatch.MapUnicodeSpan(map, Span(4, 4)));
    }

    [Fact]
    public void PreserveUnicode_KeepsTheFilesCharactersWhereTheEditKeptTheText()
    {
        string region = "say “hi” — now";
        Assert.Equal("say “bye” — now", FuzzyMatch.PreserveUnicode(region, "say \"hi\" -- now", "say \"bye\" -- now"));
        Assert.Equal("say “hi” — later", FuzzyMatch.PreserveUnicode(region, "say \"hi\" -- now", "say \"hi\" -- later"));
        Assert.Equal("say “hi” - now", FuzzyMatch.PreserveUnicode(region, "say \"hi\" -- now", "say \"hi\" - now"));   // the dash changed: written as given; the quotes the edit kept stay the file's
        Assert.Equal("x", FuzzyMatch.PreserveUnicode(region, "something else", "x"));   // not the same text normalised: as written
        Assert.Equal("x", FuzzyMatch.PreserveUnicode("plain", "plain", "x"));
        var result = FuzzyMatch.Replace("He said “go”.", "He said \"go\".", "He said \"stop\".", false);
        Assert.Equal((MatchOutcome.Replaced, MatchStrategy.UnicodeNormalized, "He said “stop”."), (result.Outcome, result.Strategy, result.Content));
    }

    [Fact]
    public void BlockAnchor_AnchorsTheEnds_ScoresTheMiddle_AdaptsTheThreshold()
    {
        string one = "start\nthe middle line here\nend\n";
        string pattern = "start\nthe muddled line hare\nend";
        // One candidate: the middle passes at 0.50 (it scores 0.878 here); a two-line pattern needs no middle at all.
        Assert.Equal([Span(0, 30)], Find(MatchStrategy.BlockAnchor, one, pattern));
        Assert.Equal([Span(0, 9)], Find(MatchStrategy.BlockAnchor, "start\nend\n", "start\nend"));
        Assert.Empty(Find(MatchStrategy.BlockAnchor, one, "start"));                 // one line is not a block
        Assert.Empty(Find(MatchStrategy.BlockAnchor, one, "start\nzzz zzz zzz\nend"));   // the middle scores under 0.50
        // A middle at 0.55: one candidate matches, two do not (0.70).
        string a = "start\nabcdefghij\nend\n";
        string weak = "start\nabcdefzzzz\nend";   // ratio 0.6 to the middle above
        Assert.Single(Find(MatchStrategy.BlockAnchor, a, weak));
        Assert.Empty(Find(MatchStrategy.BlockAnchor, a + "\n" + a, weak));
        // Two exact-middle candidates both match; offsets come from the original lines, so an em dash in the middle shifts nothing.
        string dashed = "start\nthe — middle\nend\n\nstart\nthe — middle\nend\n";
        Assert.Equal([Span(0, 22), Span(24, 46)], Find(MatchStrategy.BlockAnchor, dashed, "start\nthe -- middle\nend"));
        Assert.Equal(MatchOutcome.ApproximateAll, FuzzyMatch.Replace(dashed, "start\nthe -- muddle\nend", "x", replaceAll: true).Outcome);
        // The anchors compare Unicode-normalised and trimmed.
        Assert.Equal([Span(0, 19)], Find(MatchStrategy.BlockAnchor, "  “start”\nmid\n  end", "\"start\"\nmid\nend"));
    }

    [Fact]
    public void ContextAware_NeedsEveryNonBlankLineSimilar()
    {
        string content = "alpha one\nbeta two\n\ngamma three\n";
        Assert.Equal([Span(0, 31)], Find(MatchStrategy.ContextAware, content, "alpha one\nbeta twos\n\ngamma three"));   // 0.94 on the second line
        Assert.Empty(Find(MatchStrategy.ContextAware, content, "alpha one\nzzzz\n\ngamma three"));                  // one line at 0.4 rejects the block
        Assert.Equal([Span(0, 31)], Find(MatchStrategy.ContextAware, content, "alpha one\nbeta two\n \ngamma three"));   // a blank pattern line is skipped
        Assert.Empty(Find(MatchStrategy.ContextAware, "a\nb", "a\nb\nc\nd"));
        // One line alone is never matched by likeness (2026-10-22, review G11: an absent "x = 1" edited "y = 1").
        Assert.Empty(Find(MatchStrategy.ContextAware, content, "alpha one"));
        Assert.Empty(Find(MatchStrategy.ContextAware, content, "alpha ones"));
        Assert.Equal(MatchOutcome.NotFound, FuzzyMatch.Replace("y = 1\n", "x = 1", "x = 2", false).Outcome);
    }

    [Fact]
    public void Replace_PreChecks_AndTheNotFoundTail()
    {
        Assert.Equal(MatchOutcome.Empty, FuzzyMatch.Replace("abc", "", "x", false).Outcome);
        Assert.Equal(MatchOutcome.Empty, FuzzyMatch.Replace("abc", " \n\t", "x", false).Outcome);
        Assert.Equal(MatchOutcome.Same, FuzzyMatch.Replace("abc", "b", "b", false).Outcome);
        var missing = FuzzyMatch.Replace("abc", "zzz zzz", "yyy", false);
        Assert.Equal((MatchOutcome.NotFound, MatchStrategy.Exact, "abc", 0), (missing.Outcome, missing.Strategy, missing.Content, missing.Count));
        // Already applied: new_text (8+ characters) is there exactly and old_text is not.
        var applied = FuzzyMatch.Replace("hello wide world", "goodbye cruel world", "hello wide world", false);
        Assert.Equal((MatchOutcome.AlreadyApplied, "hello wide world"), (applied.Outcome, applied.Content));
        Assert.Equal(MatchOutcome.NotFound, FuzzyMatch.Replace("hello", "goodbye cruel world", "hello", false).Outcome);   // too short to count
        Assert.True(FuzzyMatch.IsAlreadyApplied("x = 12345678;", "x = 0;", "x = 12345678;"));
        Assert.False(FuzzyMatch.IsAlreadyApplied("x = 12345678; x = 0;", "x = 0;", "x = 12345678;"));   // old_text still there
        Assert.False(FuzzyMatch.IsAlreadyApplied("abc", "zzz", ""));
    }

    [Fact]
    public void Replace_Ambiguous_NamesTheFirstFiveLocations()
    {
        string content = string.Join('\n', Enumerable.Range(1, 7).Select(i => $"line {i} has x in it"));
        var result = FuzzyMatch.Replace(content, "x", "y", false);
        Assert.Equal((MatchOutcome.Ambiguous, 7), (result.Outcome, result.Count));
        Assert.Equal(Enumerable.Range(1, 5).Select(i => new MatchLocation(i, $"line {i} has x in it")), result.Locations);
        Assert.Equal(content, result.Content);
        Assert.Equal([new MatchLocation(1, "a b"), new MatchLocation(2, "c")], FuzzyMatch.Locate("a b\nc\n", [new TextSpan(2, 3), new TextSpan(4, 5)]));
        Assert.Equal([new MatchLocation(1, "a")], FuzzyMatch.Locate("a", [new TextSpan(0, 1), new TextSpan(0, 1)], cap: 1));
        // replace_all over an exact ambiguity is every occurrence; the placed spans follow the edited text.
        var all = FuzzyMatch.Replace("x\nx", "x", "yy", true);
        Assert.Equal(("yy\nyy", 2), (all.Content, all.Count));
        Assert.Equal([Span(0, 2), Span(3, 5)], all.Placed);
        Assert.Equal([Span(0, 1), Span(2, 3)], all.Spans);
    }

    [Fact]
    public void Replace_EscapeDrift_IsCaughtOnANonExactMatch()
    {
        // The file has a plain quote; the arguments carry \' — a similarity match would write the backslash in.
        string content = "say 'hi'\nnext line";
        var single = FuzzyMatch.Replace(content, "say \\'hi\\'\nnext line", "say \\'bye\\'\nnext line", false);
        Assert.Equal((MatchOutcome.EscapeDrift, EscapeDrift.QuoteSingle, MatchStrategy.ContextAware, content), (single.Outcome, single.Drift, single.Strategy, single.Content));
        Assert.Equal(EscapeDrift.QuoteDouble, FuzzyMatch.DetectEscapeDrift("say \"hi\"", "say \\\"hi\\\"", "x"));
        Assert.Equal(EscapeDrift.None, FuzzyMatch.DetectEscapeDrift("say \\'hi\\'", "say \\'hi\\'", "say \\'bye\\'"));   // the file has them too
        Assert.Equal(EscapeDrift.DoubledBackslashes, FuzzyMatch.DetectEscapeDrift("a\\b c\\\\d", "a\\\\b c\\\\\\\\d", "x"));
        Assert.Equal(EscapeDrift.None, FuzzyMatch.DetectEscapeDrift("a\\b c\\\\d", "a\\\\b c\\\\d", "x"));   // not every run doubled
        Assert.Equal(EscapeDrift.None, FuzzyMatch.DetectEscapeDrift("a\\b", "a\\b", "x"));
        Assert.Equal(EscapeDrift.None, FuzzyMatch.DetectEscapeDrift("ab", "ab", "x"));
        // An exact match is never judged: what the file holds is what the model copied.
        Assert.Equal(MatchOutcome.Replaced, FuzzyMatch.Replace("say \\'hi\\'", "say \\'hi\\'", "say \\'bye\\'", false).Outcome);
    }

    [Fact]
    public void Replace_PlacesEachReplacement_AndReportsTheStrategy()
    {
        var result = FuzzyMatch.Replace("one\n  two\nthree", "two", "2\n2b", false);
        Assert.Equal((MatchOutcome.Replaced, MatchStrategy.Exact, "one\n  2\n2b\nthree"), (result.Outcome, result.Strategy, result.Content));
        Assert.Equal([Span(6, 10)], result.Placed);
        Assert.Equal([Span(6, 9)], result.Spans);
        var deletion = FuzzyMatch.Replace("a\nb\nc", "b\n", "", false);
        Assert.Equal(("a\nc", Span(2, 2)), (deletion.Content, deletion.Placed[0]));
        var inside = FuzzyMatch.Replace("    keep\n    old\n", "  old", "  new", false);
        Assert.Equal((MatchStrategy.Exact, "    keep\n    new\n"), (inside.Strategy, inside.Content));   // a substring is an exact match, indentation and all
        var loose = FuzzyMatch.Replace("    keep\n    old\n", "\told", "\tnew", false);
        Assert.Equal((MatchStrategy.LineTrimmed, "    keep\n    new\n"), (loose.Strategy, loose.Content));
    }
}
