using NeonSidekick.UI;

namespace NeonSidekick.Tests;

public class PasteTextTests
{
    [Theory]
    [InlineData("hello", "hello")]
    [InlineData("one\r\ntwo\nthree\rfour", "one\ntwo\nthree\nfour")]
    [InlineData("a\tb", "a    b")]
    [InlineData("  keep  ", "  keep  ")]
    [InlineData("trailing\r\n\r\n", "trailing")]
    [InlineData("\n\nleading", "\n\nleading")]
    [InlineData("\r\n", "")]
    [InlineData("bell\a here\x1b[0m", "bell here[0m")]
    [InlineData("token\uE000\uF8FF here", "token\uE000\uF8FF here")]   // private use kept since 2026-10-04 (Nerd Font glyphs); the line makes them literal tokens
    [InlineData("", "")]
    [InlineData("日本\n😀", "日本\n😀")]
    public void Normalize_IsPinned(string text, string expected) => Assert.Equal(expected, PasteText.Normalize(text));

    [Theory]
    [InlineData("hello", "hello")]
    [InlineData("one\r\ntwo\nthree\rfour", "one two three four")]
    [InlineData("a\tb", "a b")]
    [InlineData("  keep  ", "  keep  ")]
    [InlineData("trailing\r\n", "trailing ")]
    [InlineData("bell\a here\x1b[0m", "bell here[0m")]
    [InlineData("token\uE000 here", "token\uE000 here")]
    [InlineData("", "")]
    [InlineData("日本\n😀", "日本 😀")]
    public void Flatten_IsPinned(string text, string expected) => Assert.Equal(expected, PasteText.Flatten(text));

    [Fact]
    public void Null_IsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => PasteText.Normalize(null!));
        Assert.Throws<ArgumentNullException>(() => PasteText.Flatten(null!));
    }
}
