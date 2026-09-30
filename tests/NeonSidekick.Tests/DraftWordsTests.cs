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
    public void ALetterPastTheBmp_CountsAsOne()
    {
        string text = "x 𝒳𝒴z y";   // two mathematical script letters, each a surrogate pair

        Assert.Equal("𝒳𝒴z", Word(text, 2));
        Assert.Equal("𝒳𝒴z", Word(text, 3));   // on a pair's low half
        Assert.Equal("𝒳𝒴z", Word(text, 6));
    }
}
