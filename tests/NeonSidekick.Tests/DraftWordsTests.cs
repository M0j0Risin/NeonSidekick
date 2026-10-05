using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>The word a double-click on the draft selects (2026-09-30, the user's ask): the clicked character's class decides the span.</summary>
public class DraftWordsTests
{
    private static string Word(string text, int index)
    {
        var (start, end) = DraftWords.At(text, index);
        return text[start..end];
    }

    [Theory]
    [InlineData(4)]   // q
    [InlineData(6)]   // i
    [InlineData(8)]   // k
    public void AnyLetterOfAWord_SelectsTheWord(int index)
    {
        Assert.Equal("quick", Word("the quick brown fox", index));
        Assert.Equal((4, 9), DraftWords.At("the quick brown fox", index));
    }

    [Theory]
    [InlineData("the quick, brown", 5, "quick")]        // punctuation ends a word
    [InlineData("quick-brown", 2, "quick")]             // a hyphen splits, as edit controls do
    [InlineData("quick-brown", 8, "brown")]
    [InlineData("a snake_case name", 5, "snake_case")]  // _ is a word character
    [InlineData("build v2 now", 7, "v2")]               // digits too
    [InlineData("a naïve plan", 4, "naïve")]           // a letter past ASCII
    [InlineData("see file.txt", 5, "file")]             // a dot splits
    public void TheWordCharacters_AreLettersDigitsAndUnderscore(string text, int index, string expected)
    {
        Assert.Equal(expected, Word(text, index));
    }

    [Theory]
    [InlineData("a   b", 2, "   ")]       // a run of blanks
    [InlineData("a, b", 1, ",")]          // a lone comma
    [InlineData("wait... ok", 5, "...")]  // a run of the same mark
    [InlineData("a -- b", 3, "--")]
    [InlineData("a .- b", 2, ".")]        // different marks are different runs
    public void BlanksAndPunctuation_SelectTheirOwnRun(string text, int index, string expected)
    {
        Assert.Equal(expected, Word(text, index));
    }

    [Fact]
    public void ATokenSelectsItselfAlone()
    {
        char token = (char)0xE000;   // PasteBlocks' first token: a collapsed paste
        string text = "see" + token + token + "now";

        Assert.True(PasteBlocks.IsToken(token));
        Assert.Equal((3, 4), DraftWords.At(text, 3));
        Assert.Equal((4, 5), DraftWords.At(text, 4));
        Assert.Equal("see", Word(text, 1));   // the word stops at the token
    }

    [Fact]
    public void TheEnd_OrALineBreak_TakesTheCharacterBefore()
    {
        Assert.Equal("fox", Word("the quick brown fox", 19));   // past the row's end
        Assert.Equal("one", Word("one\ntwo", 3));                 // on the line break
        Assert.Equal((4, 4), DraftWords.At("one\n\ntwo", 4));     // an empty line: nothing
        Assert.Equal((0, 0), DraftWords.At("", 0));              // an empty draft: nothing
        Assert.Equal("two", Word("one\ntwo", 5));                // a break is never taken
    }

    [Fact]
    public void AnEmoji_IsOneCluster_NeverSplitNorJoinedToAWord()
    {
        // Later on 2026-09-30 (the review's catch): by rune, the variation selector (a mark) joined the next word.
        string warn = "\u26A0\uFE0F";                            // the warning sign, emoji style
        string family = "\U0001F468\u200D\U0001F469";          // man ZWJ woman, one grapheme
        string text = warn + "warning " + family + " ok";

        Assert.Equal("warning", Word(text, 3));
        Assert.Equal(warn, Word(text, 0));
        Assert.Equal(warn, Word(text, 1));                         // on the selector: its cluster
        Assert.Equal(family, Word(text, 10));
        Assert.Equal(family, Word(text, 12));                      // inside the family
        Assert.Equal("cafe\u0301s", Word("a cafe\u0301s b", 5));  // a combining mark rides with its letter
    }

    [Fact]
    public void ALetterPastTheBmp_CountsAsOne()
    {
        string text = "x 𝒳𝒴z y";   // two mathematical script letters, each a surrogate pair

        Assert.Equal("𝒳𝒴z", Word(text, 2));
        Assert.Equal("𝒳𝒴z", Word(text, 3));   // on a pair's low half
        Assert.Equal("𝒳𝒴z", Word(text, 6));
    }

    /// <summary>The word moves' stops (2026-10-04): back to a word's start over blanks, on to the next word's start; a token one word; the ends.</summary>
    [Theory]
    [InlineData("the quick brown", 15, 10)]
    [InlineData("the quick brown", 10, 4)]
    [InlineData("the quick brown", 6, 4)]
    [InlineData("the quick brown", 0, 0)]
    [InlineData("the   quick", 6, 0)]
    [InlineData("a, b", 4, 3)]
    [InlineData("a, b", 3, 1)]
    [InlineData("one\ntwo", 4, 0)]
    public void PreviousStart_IsTheWordsStart(string text, int from, int expected) => Assert.Equal(expected, DraftWords.PreviousStart(text, from));

    [Theory]
    [InlineData("the quick brown", 0, 4)]
    [InlineData("the quick brown", 5, 10)]
    [InlineData("the quick brown", 10, 15)]
    [InlineData("the quick brown", 15, 15)]
    [InlineData("a, b", 0, 1)]
    [InlineData("a, b", 1, 3)]
    [InlineData("one\ntwo", 0, 4)]
    public void NextStart_IsTheNextWordsStart(string text, int from, int expected) => Assert.Equal(expected, DraftWords.NextStart(text, from));

    [Fact]
    public void ATokenIsAWordOfItsOwn()
    {
        string text = "see \uE000\uE001 now";
        Assert.Equal(5, DraftWords.PreviousStart(text, 6));
        Assert.Equal(5, DraftWords.NextStart(text, 4));
        Assert.Equal(7, DraftWords.NextStart(text, 5));
    }
}
