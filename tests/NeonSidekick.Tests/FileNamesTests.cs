using NeonSidekick.Files;

namespace NeonSidekick.Tests;

/// <summary><see cref="FileNames"/>: the made-up names the exports and writers give their files.</summary>
public sealed class FileNamesTests
{
    /// <summary>
    /// An invisible format character is a hyphen (2026-10-30, review F87: a right-to-left override showed a name's end backwards), but a
    /// zero-width joiner inside an emoji sequence stays, so a title keeps its emoji.
    /// </summary>
    [Fact]
    public void Safe_MapsFormatCharacters_ButKeepsAnEmojisJoiners()
    {
        Assert.Equal("a-exe.txt", FileNames.Safe("a\u202Eexe.txt"));
        Assert.Equal("a-b", FileNames.Safe("a\u200Bb"));
        Assert.Equal("a-b", FileNames.Safe("a\u2066b\u2069"));
        Assert.Equal("a-b", FileNames.Safe("a\uFEFFb"));
        Assert.Equal("a-b", FileNames.Safe("a \u200D b"));   // a stray joiner between spaces

        string family = "\U0001F468\u200D\U0001F469\u200D\U0001F467";
        Assert.Equal("Family-" + family, FileNames.Safe("Family " + family, lowerCase: false));
    }

    /// <summary>The last part of a path, split on either OS's separator (2026-10-30, review F39).</summary>
    [Theory]
    [InlineData(@"C:\Users\me\a.png", "a.png")]
    [InlineData("/Users/me/a.png", "a.png")]
    [InlineData(@"comfy\dir/b.jpg", "b.jpg")]
    [InlineData("plain.png", "plain.png")]
    [InlineData(@"C:\folder\", "")]
    public void LastPart_SplitsOnEitherSeparator(string path, string expected) => Assert.Equal(expected, FileNames.LastPart(path));
}
