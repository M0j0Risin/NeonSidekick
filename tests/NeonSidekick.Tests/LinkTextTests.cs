using NeonSidekick.UI;
using NeonSidekick.UI.Markdown;
using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

/// <summary>
/// Clickable links and paths (2026-11-01, plan memoized-dancing-horizon item #2): the one rule a link is made by (<see cref="LinkText"/>),
/// the filter that keeps all but web links from the terminal (<see cref="LinkFilter"/>) and the links a reply's Markdown carries.
/// </summary>
public class LinkTextTests : IDisposable
{
    private readonly TestConsole _console = new();

    public LinkTextTests()
    {
        _console.Profile.Width = 80;
    }

    public void Dispose() => _console.Dispose();

    private List<SegmentLine> Render(IRenderable renderable, int width = 80) => ScreenPane.RenderLines(renderable, _console, width);

    private List<Segment> Segments(string markdown, Func<string, string?>? resolve = null) =>
        Render(new MarkdownView(MarkdigParser.Replies.Parse(markdown), resolve)).SelectMany(line => line).ToList();

    /// <summary>The text of the segments linked to <paramref name="url"/>, joined.</summary>
    private static string LinkedText(IEnumerable<Segment> segments, string url) =>
        string.Concat(segments.Where(s => s.Link?.Url == url).Select(s => s.Text));

    // ── LinkText ────────────────────────────────────────────────────────────

    [Fact]
    public void ChordFor_NamesTheChordTheTerminalLetsThrough()
    {
        Assert.Equal("Ctrl+click", LinkText.ChordFor(mac: false, null));
        Assert.Equal("Ctrl+click", LinkText.ChordFor(mac: false, "iTerm.app"));
        Assert.Equal("Shift+click", LinkText.ChordFor(mac: true, "iTerm.app"));
        Assert.Equal("Option+click", LinkText.ChordFor(mac: true, "Apple_Terminal"));
        Assert.Equal("Option+click", LinkText.ChordFor(mac: true, null));
    }

    [Fact]
    public void IsLinkable_RefusesControls_AndForAWebLink_SpacesAndLength()
    {
        Assert.True(LinkText.IsLinkable("https://a.b/c", web: true));
        Assert.False(LinkText.IsLinkable("https://a.b/\u001b]0;pwned\u0007", web: true));
        Assert.False(LinkText.IsLinkable("https://a.b/\u009b", web: true));
        Assert.False(LinkText.IsLinkable("https://a.b/c d", web: true));
        Assert.False(LinkText.IsLinkable("https://a.b/" + new string('x', LinkText.MaxWebLinkLength), web: true));
        Assert.True(LinkText.IsLinkable(LinkText.PathLink(@"C:\My Docs\a[1].txt"), web: false));   // a path keeps its space
        Assert.False(LinkText.IsLinkable(LinkText.PathLink("C:\\a\u0007"), web: false));
    }

    [Fact]
    public void WebTarget_KeepsWebSchemesAlone_AndMakesABareAddressMailto()
    {
        Assert.Equal("https://a.b/c", LinkText.WebTarget("https://a.b/c"));
        Assert.Equal("mailto:a@b.c", LinkText.WebTarget("mailto:a@b.c"));
        Assert.Equal("mailto:a@b.c", LinkText.WebTarget("a@b.c"));
        Assert.Null(LinkText.WebTarget("javascript:alert(1)"));
        Assert.Null(LinkText.WebTarget("file:///C:/Windows/notepad.exe"));
        Assert.Null(LinkText.WebTarget("neon-path:C:/Windows"));
    }

    [Fact]
    public void BareUrls_TrimTrailingPunctuation_AndKeepBalancedParentheses()
    {
        string text = "see https://a.b/c). and https://en.wikipedia.org/wiki/X_(y), done";
        var found = LinkText.BareUrls(text).Select(u => text.Substring(u.Start, u.Length)).ToArray();
        Assert.Equal(["https://a.b/c", "https://en.wikipedia.org/wiki/X_(y)"], found);
    }

    [Fact]
    public void PathCandidate_CutsALineAndColumn_NeverADrive()
    {
        Assert.Equal("src/Foo.cs", LinkText.PathCandidate("src/Foo.cs"));
        Assert.Equal("Foo.cs", LinkText.PathCandidate("Foo.cs:120"));
        Assert.Equal("Foo.cs", LinkText.PathCandidate("Foo.cs:120:5"));
        Assert.Equal(@"C:\x\Foo.cs", LinkText.PathCandidate(@"C:\x\Foo.cs:12"));
    }

    [Fact]
    public void HoverHint_SaysWhereTheClickGoes_CutToFit()
    {
        string chord = LinkText.Chord;
        Assert.Equal(chord + " opens https://example.com/", LinkText.HoverHint("https://example.com/", null, outside: false, LinkText.HintWidth));
        Assert.Equal(chord + @" shows src\Foo.cs in /browse", LinkText.HoverHint(LinkText.PathLink(@"C:\w\src\Foo.cs"), @"src\Foo.cs", outside: false, LinkText.HintWidth));
        Assert.Equal(@"C:\old\Foo.cs is outside the working folder", LinkText.HoverHint(LinkText.PathLink(@"C:\old\Foo.cs"), null, outside: true, LinkText.HintWidth));

        string longUrl = "https://example.com/" + new string('a', 300);
        string cut = LinkText.HoverHint(longUrl, null, outside: false, LinkText.HintWidth)!;
        Assert.True(TextCells.Width(cut) <= LinkText.HintWidth);
        Assert.EndsWith("…", cut, StringComparison.Ordinal);
    }

    /// <summary>A bidi override or a zero-width character makes no link: what's read isn't what opens (2026-11-02, review P1-9).</summary>
    [Fact]
    public void IsLinkable_RefusesBidiAndZeroWidth_AndTheHintNamesTheAsciiHost()
    {
        Assert.False(LinkText.IsLinkable("https://a.b/\u202Etxt.exe", web: true));
        Assert.False(LinkText.IsLinkable("https://pay\u200Bpal.com/", web: true));
        Assert.Null(LinkText.WebTarget("https://a.b/\u202Ec"));
        Assert.Equal(LinkText.Chord + " opens https://xn--pple-43d.com/", LinkText.HoverHint("https://аpple.com/", null, outside: false, LinkText.HintWidth));
        Assert.DoesNotContain("\u202E", LinkText.HoverHint(LinkText.PathLink("C:\\old\\a\u202Etxt.exe"), null, outside: true, LinkText.HintWidth), StringComparison.Ordinal);
    }

    /// <summary>A /find mark over a linked stretch keeps the link on every piece it cuts (2026-11-02, review P2-5).</summary>
    [Fact]
    public void FindsMark_KeepsTheLink_OnEveryPiece()
    {
        var link = new Link("https://a.b/c");
        var marked = TextFind.Mark([new Segment("see https://a.b/c now", Style.Plain, link)], [4], 5, current: 4);
        Assert.True(marked.Count >= 3);
        Assert.All(marked, segment => Assert.Equal("https://a.b/c", segment.Link?.Url));
    }

    /// <summary>What a reply's resolver would be asked, gathered with no layout (2026-11-02, review P2-4): code spans, once each, never an address.</summary>
    [Fact]
    public void PathCandidates_AreTheInlineCode_InListsQuotesAndTables_OnceEach()
    {
        var document = MarkdigParser.Replies.Parse("See `a.cs`.\n\n- `b.cs:12`\n\n> `c.cs`\n\n| x |\n|---|\n| `d.cs` |\n\n`a.cs` and `https://a.b/` and `e.cs`\n\n```\nnot.cs\n```");
        Assert.Equal(["a.cs", "b.cs", "c.cs", "d.cs", "e.cs"], MarkdownView.PathCandidates(document));
    }

    // ── LinkFilter ──────────────────────────────────────────────────────────

    [Fact]
    public void Filter_KeepsAWebLink_AndDropsAnyOther_TheTextAndStyleKept()
    {
        var style = new Style(Color.Red);
        Assert.Equal("https://a.b/", LinkFilter.Filter(new Segment("x", style, new Link("https://a.b/"))).Link?.Url);
        foreach (string other in new[] { LinkText.PathLink(@"C:\a.txt"), "file:///C:/a.exe", "javascript:alert(1)", "https://a.b/\u001b]0;x\u0007" })
        {
            var filtered = LinkFilter.Filter(new Segment("x", style, new Link(other)));
            Assert.Null(filtered.Link);
            Assert.Equal("x", filtered.Text);
            Assert.Equal(style, filtered.Style);
        }
    }

    [Fact]
    public void AWebLink_WritesOsc8_WhereLinksAreOn_AndNoOtherLinkEver()
    {
        using var console = new TestConsole().EmitAnsiSequences();
        console.Profile.Capabilities.Links = true;
        var filtered = new LinkFilter(console);
        filtered.Write(new Paragraph().Append("web", Style.Plain, new Link("https://a.b/")));
        Assert.Contains("\u001b]8;", console.Output, StringComparison.Ordinal);

        using var other = new TestConsole().EmitAnsiSequences();
        other.Profile.Capabilities.Links = true;
        var guarded = new LinkFilter(other);
        guarded.Write(new Paragraph()
            .Append("path", Style.Plain, new Link(LinkText.PathLink(@"C:\a.txt")))
            .Append("file", Style.Plain, new Link("file:///C:/a.exe"))
            .Append("bad", Style.Plain, new Link("https://a.b/\u001b]0;pwned\u0007")));
        Assert.DoesNotContain("]8;", other.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("pwned", other.Output, StringComparison.Ordinal);
        Assert.Contains("pathfilebad", other.Output, StringComparison.Ordinal);

        using var off = new TestConsole().EmitAnsiSequences();
        off.Profile.Capabilities.Links = false;   // another terminal: none written, the app's opener answers
        new LinkFilter(off).Write(new Paragraph().Append("web", Style.Plain, new Link("https://a.b/")));
        Assert.DoesNotContain("]8;", off.Output, StringComparison.Ordinal);
    }

    // ── A reply's Markdown ──────────────────────────────────────────────────

    [Fact]
    public void ALink_CarriesItsAddress_OnItsTextAndItsUrl()
    {
        var segments = Segments("Read [the docs](https://a.b/docs) now.");
        Assert.Equal("the docs (https://a.b/docs)", LinkedText(segments, "https://a.b/docs"));
        Assert.Null(segments.First(s => s.Text.Contains("Read", StringComparison.Ordinal)).Link);
    }

    [Fact]
    public void ABareUrl_Links_WithoutItsTrailingPunctuation()
    {
        var segments = Segments("Go to https://a.b/c). Then stop.");
        Assert.Equal("https://a.b/c", LinkedText(segments, "https://a.b/c"));
        Assert.DoesNotContain(segments, s => s.Link is not null && s.Text.Contains(')', StringComparison.Ordinal));
    }

    [Fact]
    public void OtherSchemes_GetNoLink_AndAnEmailAutolinkLinksMailto()
    {
        Assert.DoesNotContain(Segments("[x](javascript:alert(1)) and [y](file:///C:/a.exe) and [z](neon-path:C:/Windows)"), s => s.Link is not null);
        var mail = Segments("Write to <a@b.c>.");
        Assert.Contains(mail, s => s.Link?.Url == "mailto:a@b.c");
    }

    [Fact]
    public void InlineCode_ThatIsAUrl_Links_ACodeBlocksDoesNot_ATableCellsDoes()
    {
        Assert.Equal("https://a.b/c", LinkedText(Segments("Try `https://a.b/c` there."), "https://a.b/c"));
        Assert.DoesNotContain(Segments("```\nhttps://a.b/c\n```"), s => s.Link is not null);
        Assert.Contains(Segments("| a | b |\n|---|---|\n| [x](https://a.b/t) | y |"), s => s.Link?.Url == "https://a.b/t");
    }

    [Fact]
    public void InlineCode_NamingAnExistingPath_LinksItsFullPath_ElseStaysPlain()
    {
        string folder = Path.Combine(Path.GetTempPath(), "neon-links-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "src"));
        try
        {
            string file = Path.Combine(folder, "src", "Foo.cs");
            File.WriteAllText(file, "x");
            string? Resolve(string text)
            {
                string full = Path.GetFullPath(Path.Combine(folder, text));
                return File.Exists(full) || Directory.Exists(full) ? full : null;
            }

            var segments = Segments("See `src/Foo.cs:120` and `src/Gone.cs`.", Resolve);
            var linked = Assert.Single(segments, s => s.Link is not null);
            Assert.Equal("src/Foo.cs:120", linked.Text);
            Assert.Equal(LinkText.PathLink(file), linked.Link!.Url);
            Assert.Equal(Decoration.Underline, linked.Style.Decoration & Decoration.Underline);

            Assert.DoesNotContain(Segments("See `src/Foo.cs`."), s => s.Link is not null);   // no resolver: no path links
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
