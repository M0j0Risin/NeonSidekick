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

    /// <summary>Only the working folder's tools link a path (2026-11-02, review P2-6): a share's <c>src/Foo.cs</c> isn't the local one.</summary>
    [Fact]
    public void AToolOutsideTheWorkingFolder_LinksNoPath_BesideASameNamedLocalFile()
    {
        _t.WorkingFolderTools = new HashSet<string>(StringComparer.Ordinal) { "read_file" };
        _t.Tool("unc_read", "{\"path\":\"src/Foo.cs\"}");
        Assert.DoesNotContain(Stored, s => s.Link is not null);

        _t.Tool("read_file", "{\"path\":\"src/Foo.cs\"}");
        Assert.Equal("src/Foo.cs", Linked(Stored, LinkText.PathLink(Path.Combine(_folder, "src", "Foo.cs"))));
    }

    // ── A reply's paths, asked off the layout (2026-11-02, review P2-4) ─────

    /// <summary>A look that blocks never stalls the tick that asked it; once it answers the reply is laid out again with the link.</summary>
    [Fact]
    public async Task AReplysPath_IsAskedOffTheLayout_ABlockedLookStallsNoTick_AndLinksOnceItAnswers()
    {
        using var gate = new ManualResetEventSlim();
        Task? worker = null;
        _t.ResolvePath = text => gate.Wait(TimeSpan.FromSeconds(30)) ? Resolve(text) : null;
        _t.RunResolver = work => worker = Task.Run(work);
        _t.BeginAssistant(markdown: true);
        _t.AppendDelta("See `src/Foo.cs` for it.");

        await Task.Run(() => _time.Advance(ScreenPane.Tick)).WaitAsync(TimeSpan.FromSeconds(10));   // laid out without waiting on the look
        Assert.NotNull(worker);
        string target = LinkText.PathLink(Path.Combine(_folder, "src", "Foo.cs"));
        var before = _pane.LiveForTests!;
        Assert.Empty(Linked(LiveSegments(before), target));

        gate.Set();
        await worker!.WaitAsync(TimeSpan.FromSeconds(10));
        var after = _pane.LiveForTests!;
        Assert.NotSame(before, after);   // laid out again, so the next tick draws the link
        Assert.Equal("src/Foo.cs", Linked(LiveSegments(after), target));
        _time.Advance(ScreenPane.Tick);
        _t.EndAssistant();
        Assert.Equal("src/Foo.cs", Linked(Stored, target));
    }

    private List<Segment> LiveSegments(IRenderable live) =>
        live.Render(RenderOptions.Create(_console, _console.Profile.Capabilities), 80).ToList();

    /// <summary>A reply that commits in the tick its words came in, the worker never run, still links: its spans are asked before the commit.</summary>
    [Fact]
    public void AReplyCommittedAtOnce_StillLinksItsPath()
    {
        _t.RunResolver = static _ => { };
        _t.BeginAssistant(markdown: true);
        _t.AppendDelta("Open `src/Foo.cs` and `src/Gone.cs`.");
        _t.EndAssistant();

        Assert.Equal("src/Foo.cs", Linked(Stored, LinkText.PathLink(Path.Combine(_folder, "src", "Foo.cs"))));
        Assert.Single(Stored.Where(s => s.Link is not null).Select(s => s.Link!.Url).Distinct());
    }

    /// <summary>A file named before it exists links in a later reply once it's written: a miss is forgotten with its reply.</summary>
    [Fact]
    public void AFileNamedBeforeItExists_LinksInALaterReply()
    {
        _t.RunResolver = static work => work();
        string file = Path.Combine(_folder, "src", "New.cs");
        string target = LinkText.PathLink(file);
        _t.BeginAssistant(markdown: true);
        _t.AppendDelta("I'll write `src/New.cs`.");
        _time.Advance(ScreenPane.Tick);
        _t.EndAssistant();
        Assert.Empty(Linked(Stored, target));

        File.WriteAllText(file, "x");
        _t.BeginAssistant(markdown: true);
        _t.AppendDelta("Wrote `src/New.cs`.");
        _time.Advance(ScreenPane.Tick);
        _t.EndAssistant();
        Assert.Equal("src/New.cs", Linked(Stored, target));
    }

    /// <summary>A found path is kept for the session, never asked again, until <see cref="TranscriptRenderer.ForgetPaths"/> (a <c>/cwd</c>).</summary>
    [Fact]
    public void AFoundPath_IsAskedOnce_UntilThePathsAreForgotten()
    {
        int asks = 0;
        _t.ResolvePath = text =>
        {
            Interlocked.Increment(ref asks);
            return Resolve(text);
        };
        _t.RunResolver = static work => work();
        void Reply()
        {
            _t.BeginAssistant(markdown: true);
            _t.AppendDelta("See `src/Foo.cs`.");
            _time.Advance(ScreenPane.Tick);
            _t.EndAssistant();
        }

        Reply();
        Assert.Equal(1, asks);
        Reply();
        Assert.Equal(1, asks);   // the session's answer

        _t.ForgetPaths();
        Reply();
        Assert.Equal(2, asks);
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
