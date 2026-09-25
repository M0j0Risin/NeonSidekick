using NeonSidekick.Files;

namespace NeonSidekick.Tests;

public sealed class PathGlobTests
{
    [Theory]
    [InlineData("*.cs", false)]
    [InlineData("report*", false)]
    [InlineData("notes.txt", false)]
    [InlineData("src/*.cs", true)]
    [InlineData(@"src\*.cs", true)]
    [InlineData("**/*.cs", true)]
    [InlineData("**", true)]
    public void IsPathPattern_SeesASeparatorOrADoubleStar(string pattern, bool expected) =>
        Assert.Equal(expected, PathGlob.IsPathPattern(pattern));

    [Theory]
    [InlineData("src/**/*.cs", "src/a.cs", true)]
    [InlineData("src/**/*.cs", "src/deep/er/a.cs", true)]
    [InlineData("src/**/*.cs", @"src\deep\a.cs", true)]
    [InlineData("src/**/*.cs", "SRC/A.CS", true)]
    [InlineData("src/**/*.cs", "a.cs", false)]
    [InlineData("src/**/*.cs", "src/a.txt", false)]
    [InlineData("src/**/*.cs", "lib/src/a.cs", false)]
    [InlineData("**/src/*.cs", "lib/src/a.cs", true)]
    [InlineData("**/src/*.cs", "src/a.cs", true)]
    [InlineData("**/src/*.cs", "src/deep/a.cs", false)]
    [InlineData("src/*/*.cs", "src/deep/a.cs", true)]
    [InlineData("src/*/*.cs", "src/a.cs", false)]
    [InlineData("**", "anything/at/all", true)]
    [InlineData("**/*", "a", true)]
    [InlineData("docs/?.md", "docs/a.md", true)]
    [InlineData("docs/?.md", "docs/ab.md", false)]
    [InlineData("/src/*.cs", "src/a.cs", true)]
    [InlineData("src/**/**/*.cs", "src/a.cs", true)]
    [InlineData("src/", "src", true)]
    public void IsMatch_WalksSegments(string pattern, string path, bool expected) =>
        Assert.Equal(expected, PathGlob.IsMatch(pattern, path));

    [Theory]
    [InlineData("*.cs", "a.cs", true)]
    [InlineData("*.cs", "a.csx", false)]
    [InlineData("a*b*c", "aXXbYYc", true)]
    [InlineData("a*b*c", "abc", true)]
    [InlineData("a*b*c", "ac", false)]
    [InlineData("?.md", "a.md", true)]
    [InlineData("?.md", ".md", false)]
    [InlineData("*", "", true)]
    [InlineData("", "", true)]
    [InlineData("", "x", false)]
    [InlineData("README", "readme", true)]
    public void MatchSegment_StarAndQuestionMark_IgnoringCase(string pattern, string text, bool expected) =>
        Assert.Equal(expected, PathGlob.MatchSegment(pattern, text));

    [Theory]
    [InlineData("*.cs", "*.cs")]
    [InlineData("*.{png,jpg}", "*.png|*.jpg")]
    [InlineData("{src,lib}/**/*.{cs,md}", "src/**/*.cs|src/**/*.md|lib/**/*.cs|lib/**/*.md")]
    [InlineData("a{b,c{d,e}}", "ab|acd|ace")]
    [InlineData("a{,b}", "a|ab")]
    [InlineData("a{b", "a{b")]
    [InlineData("{x}", "{x}")]
    [InlineData("a}b{", "a}b{")]
    [InlineData("{x}.{a,b}", "{x}.a|{x}.b")]
    [InlineData("{a{b,c}", "{ab|{ac")]
    [InlineData("*.{PNG,png}", "*.PNG")]
    [InlineData("", "")]
    public void ExpandBraces_BashStyle(string pattern, string expected) =>
        Assert.Equal(expected.Split('|'), PathGlob.ExpandBraces(pattern));

    [Fact]
    public void ExpandBraces_PastTheCap_ComesBackWhole()
    {
        string many = "{a,b,c,d}{a,b,c,d}{a,b,c,d}";   // 64: at the cap
        Assert.Equal(PathGlob.MaxBraceExpansions, PathGlob.ExpandBraces(many).Count);
        string tooMany = many + "{a,b}";
        Assert.Equal(new[] { tooMany }, PathGlob.ExpandBraces(tooMany));
    }

    [Fact]
    public void NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => PathGlob.ExpandBraces(null!));
        Assert.Throws<ArgumentNullException>(() => PathGlob.IsMatch(null!, "a"));
        Assert.Throws<ArgumentNullException>(() => PathGlob.IsMatch("a", null!));
        Assert.Throws<ArgumentNullException>(() => PathGlob.MatchSegment(null!, "a"));
    }
}
