using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

/// <summary>
/// Tool lines link their argument alone (2026-11-01, plan memoized-dancing-horizon item #2, the user's call): in
/// <c>🛠️ read_file src/Foo.cs · 1 · 40</c> only <c>src/Foo.cs</c> carries the link — the path through the renderer's resolver, a web
/// tool's <c>url</c> as it is — whether the line goes into a tool run (<c>Hanging</c>) or on its own (<i>Tool collapse count</i> 0).
/// </summary>
public sealed class TranscriptRendererLinkTests : IDisposable
{
    private readonly TestConsole _console = new TestConsole().Interactive();
    private readonly ManualTimeProvider _time = new();
    private readonly ScreenPane _pane;
    private readonly TranscriptRenderer _t;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "neon-toollinks-" + Guid.NewGuid().ToString("N"));

    public TranscriptRendererLinkTests()
    {
        _console.Profile.Width = 80;
        _console.Profile.Height = 20;
        _pane = new ScreenPane(_console, new ScreenGeometry(() => null, () => null), _time);
        _t = new TranscriptRenderer(_pane) { ResolvePath = Resolve };
        _pane.Show();
        Directory.CreateDirectory(Path.Combine(_folder, "src"));
        Directory.CreateDirectory(Path.Combine(_folder, "My Docs"));
        File.WriteAllText(Path.Combine(_folder, "src", "Foo.cs"), "x");
        File.WriteAllText(Path.Combine(_folder, "My Docs", "a[1].txt"), "x");
    }

    public void Dispose()
    {
        _pane.Dispose();
        _console.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    /// <summary>A path inside the temp folder that exists, as its full form; anything else none (outside, missing).</summary>
    private string? Resolve(string text)
    {
        string full = Path.GetFullPath(Path.Combine(_folder, text));
        bool inside = full.StartsWith(_folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        return inside && (File.Exists(full) || Directory.Exists(full)) ? full : null;
    }

    private List<Segment> Stored => _pane.TranscriptRows().SelectMany(row => row).ToList();

    private static string Linked(IEnumerable<Segment> segments, string target) =>
        string.Concat(segments.Where(s => s.Link?.Url == target).Select(s => s.Text));

    private static string RowText(SegmentLine row) => string.Concat(row.Select(s => s.Text));

    [Fact]
    public void AReadFileLine_LinksItsPathAlone_NotTheNameOrTheOtherValues()
    {
        _t.Tool("read_file", "{\"path\":\"src/Foo.cs\",\"offset\":1,\"limit\":40}");

        string target = LinkText.PathLink(Path.Combine(_folder, "src", "Foo.cs"));
        Assert.Equal("src/Foo.cs", Linked(Stored, target));
        Assert.DoesNotContain(Stored, s => s.Link is not null && s.Link.Url != target);
    }

    [Fact]
    public void ARunsMember_LinksItsPathAlone_TooThroughHanging()
    {
        _t.ToolCollapseCount = () => 3;
        _t.BeginAssistant(markdown: true);
        _t.Tool("read_file", "{\"path\":\"src/Foo.cs\",\"offset\":1,\"limit\":40}");
        _t.Tool("read_file", "{\"path\":\"My Docs/a[1].txt\"}");
        _t.EndAssistant();

        Assert.Equal("src/Foo.cs", Linked(Stored, LinkText.PathLink(Path.Combine(_folder, "src", "Foo.cs"))));
        Assert.Equal("My Docs/a[1].txt", Linked(Stored, LinkText.PathLink(Path.Combine(_folder, "My Docs", "a[1].txt"))));   // a space and brackets, exact
    }

    [Fact]
    public void AWebFetch_LinksItsFullUrl_ItsShownPartAlone_WhenTheLineIsCut()
    {
        string url = "https://example.com/" + new string('a', 300);
        _t.Tool("web_fetch", "{\"url\":\"" + url + "\"}");

        string shown = Linked(Stored, url);
        Assert.True(shown.Length > 0);
        Assert.StartsWith("https://example.com/", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("…", shown, StringComparison.Ordinal);
        Assert.True(shown.Length < url.Length);
    }

    [Fact]
    public void AnIpv6Url_Links_AsItIs()
    {
        _t.Tool("web_fetch", "{\"url\":\"http://[::1]:8080/\"}");
        Assert.Equal("http://[::1]:8080/", Linked(Stored, "http://[::1]:8080/"));
    }

    [Fact]
    public void APathOutsideTheFolder_OrMissing_LinksNothing_AndNoResolverNone()
    {
        _t.Tool("read_file", "{\"path\":\"../elsewhere.txt\"}");
        _t.Tool("read_file", "{\"path\":\"src/Gone.cs\"}");
        Assert.DoesNotContain(Stored, s => s.Link is not null);

        _t.ResolvePath = null;
        _t.Tool("read_file", "{\"path\":\"src/Foo.cs\"}");
        Assert.DoesNotContain(Stored, s => s.Link is not null);
    }

    [Fact]
    public void ALinkedLine_ReadsAsTheUnlinkedOne_EmojiShortcodesToo()
    {
        _t.Tool("read_file", "{\"path\":\"src/Foo.cs\",\"note\":\":rocket:\"}");
        string linked = string.Join("\n", _pane.TranscriptRows().Select(RowText));

        using var console = new TestConsole().Interactive();
        console.Profile.Width = 80;
        console.Profile.Height = 20;
        using var pane = new ScreenPane(console, new ScreenGeometry(() => null, () => null), _time);
        var plain = new TranscriptRenderer(pane);
        pane.Show();
        plain.Tool("read_file", "{\"path\":\"src/Foo.cs\",\"note\":\":rocket:\"}");

        Assert.Equal(string.Join("\n", pane.TranscriptRows().Select(RowText)), linked);
        Assert.Contains("🚀", linked, StringComparison.Ordinal);
    }

    [Fact]
    public void ALongLinkedLine_WrapsUnderItsText_ItsIndentAndGlyphUnlinked()
    {
        string deep = string.Join("/", Enumerable.Repeat("folder", 12));
        Directory.CreateDirectory(Path.Combine(_folder, deep));
        string file = Path.GetFullPath(Path.Combine(_folder, deep, "Bar.cs"));
        File.WriteAllText(file, "x");
        _t.ToolCollapseCount = () => 3;
        _t.BeginAssistant(markdown: true);
        _t.Tool("read_file", "{\"path\":\"" + deep + "/Bar.cs\"}");
        _t.EndAssistant();

        var rows = _pane.TranscriptRows().Where(r => r.Any(s => s.Link is not null)).ToList();
        Assert.True(rows.Count >= 2);   // it wrapped
        string second = RowText(rows[1]);
        Assert.StartsWith("     ", second, StringComparison.Ordinal);   // under its text, not at column 0
        Assert.Null(rows[1].First().Link);   // the indent's blanks carry no link
        Assert.DoesNotContain(rows[0], s => s.Link is not null && s.Text.Contains("🛠", StringComparison.Ordinal));
        Assert.Equal(deep + "/Bar.cs", Linked(rows.SelectMany(r => r), LinkText.PathLink(file)));
    }

    [Fact]
    public void ThePlainPath_LinksTheUrl_OnALineOfItsOwn_AndAfterTheRepliesGlyph()
    {
        // Tool collapse count 0 (the default here): the line goes through WriteLine, not a run.
        _t.Tool("web_fetch", "{\"url\":\"https://a.b/c\"}");
        Assert.Equal("https://a.b/c", Linked(Stored, "https://a.b/c"));

        _t.BeginAssistant();
        _t.Tool("web_fetch", "{\"url\":\"https://a.b/d\"}");
        _t.EndAssistant();
        Assert.Equal("https://a.b/d", Linked(Stored, "https://a.b/d"));
    }
}
