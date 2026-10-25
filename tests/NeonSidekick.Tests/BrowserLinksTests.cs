using NeonSidekick.App;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Viewer;
using NeonSidekick.YouTube;

namespace NeonSidekick.Tests;

/// <summary>
/// The Browser window's saved links (later on 2026-10-13, the user's ask): the list in <c>browser-links.json</c>
/// (<see cref="BrowserLinks"/>), its ☆ menu, its wording (<see cref="BrowserLinkText"/>) and <c>/browser</c>'s parse
/// (<see cref="BrowserCommand"/>). The window that shows them is the smoke's; the pane is <c>ChatScreenTests.Browser</c>'s.
/// </summary>
public sealed class BrowserLinksTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 18, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick-links-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new() { UtcNow = Now };

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private BrowserLinks Links() => new(_dir, _time);

    private string FilePath => NeonSidekick.Settings.Profiles.ConfigPath(_dir, BrowserLinks.FileName);   // <profile>\configs since 2026-10-16

    // ── The list ─────────────────────────────────────────────────────────────

    [Fact]
    public void Add_PutsANewOneLast_ADuplicateKeepsItsPlace_AndOnlyFillsAMissingName()
    {
        var links = Links();
        Assert.Equal(BrowserLinkOutcome.Done, links.Add("https://a.example/", "A", out var a));
        Assert.Equal(new BrowserLink("https://a.example/", "A", Now), a);
        Assert.Equal(BrowserLinkOutcome.Done, links.Add("https://b.example/", null, out _));
        Assert.Equal(BrowserLinkOutcome.AlreadySaved, links.Add("https://b.example/", "  B  ", out var filled));
        Assert.Equal("B", filled!.Name);
        Assert.Equal(BrowserLinkOutcome.AlreadySaved, links.Add("https://a.example/", "Other", out var kept));
        Assert.Equal("A", kept!.Name);   // a name given is never overwritten by a save

        Assert.Equal(["https://a.example/", "https://b.example/"], new BrowserLinks(_dir).List().Select(l => l.Url));   // another instance reads it
        Assert.Equal("B", new BrowserLinks(_dir).Find("https://b.example/")!.Name);
    }

    [Fact]
    public void Add_RefusesAnythingButAWebAddress_AndAnAddressWithCredentials()
    {
        var links = Links();
        Assert.Equal(BrowserLinkOutcome.NotWeb, links.Add("file:///C:/x", null, out _));
        Assert.Equal(BrowserLinkOutcome.NotWeb, links.Add("about:blank", null, out _));
        Assert.Equal(BrowserLinkOutcome.Credentials, links.Add("https://user:pass@example.com/", null, out _));
        Assert.True(BrowserLinks.HasCredentials("https://user@example.com/"));
        Assert.False(BrowserLinks.HasCredentials("https://example.com/"));
        Assert.Empty(links.List());
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void Remove_Rename_Readdress_AndClear()
    {
        var links = Links();
        links.Add("https://a.example/", "A", out _);
        links.Add("https://b.example/", "B", out _);
        links.Add("https://c.example/", "C", out _);

        Assert.Equal(BrowserLinkOutcome.Done, links.Rename("https://a.example/", " Alpha "));
        Assert.Equal("Alpha", links.Find("https://a.example/")!.Name);
        Assert.Equal(BrowserLinkOutcome.Done, links.Rename("https://a.example/", "  "));   // a blank name takes it off
        Assert.Null(links.Find("https://a.example/")!.Name);

        Assert.Equal(BrowserLinkOutcome.Duplicate, links.Readdress("https://a.example/", "https://b.example/"));
        Assert.Equal(BrowserLinkOutcome.Done, links.Readdress("https://a.example/", "https://z.example/"));
        Assert.Equal(["https://z.example/", "https://b.example/", "https://c.example/"], links.List().Select(l => l.Url));
        Assert.Equal(BrowserLinkOutcome.NotWeb, links.Readdress("https://z.example/", "javascript:alert(1)"));

        Assert.Equal(BrowserLinkOutcome.Done, links.Remove("https://b.example/"));
        Assert.Equal(BrowserLinkOutcome.NotSaved, links.Remove("https://b.example/"));
        Assert.Equal(BrowserLinkOutcome.Done, links.Clear(out int cleared));
        Assert.Equal(2, cleared);
        Assert.Equal(BrowserLinkOutcome.Done, links.Clear(out cleared));
        Assert.Equal(0, cleared);
    }

    [Fact]
    public void Move_EachWay_HeldAtTheEnds_AndToTheTopOrBottom()
    {
        var links = Links();
        foreach (string name in (string[])["a", "b", "c", "d"])
        {
            links.Add($"https://{name}.example/", null, out _);
        }

        string Order() => string.Concat(links.List().Select(l => l.Url[8]));
        Assert.Equal(BrowserLinkOutcome.Done, links.Move("https://c.example/", -1));
        Assert.Equal("acbd", Order());
        Assert.Equal(BrowserLinkOutcome.Done, links.Move("https://a.example/", -1));   // held at the top
        Assert.Equal("acbd", Order());
        Assert.Equal(BrowserLinkOutcome.Done, links.Move("https://a.example/", int.MaxValue));
        Assert.Equal("cbda", Order());
        Assert.Equal(BrowserLinkOutcome.Done, links.Move("https://d.example/", int.MinValue));
        Assert.Equal("dcba", Order());
        Assert.Equal(BrowserLinkOutcome.NotSaved, links.Move("https://x.example/", 1));
        Assert.Equal([0, 3, 2, 1], new[] { (0, -1), (2, int.MaxValue), (3, -1), (1, 0) }.Select(m => YouTubeLibrary.MovedTo(m.Item1, m.Item2, 4)));
    }

    [Fact]
    public void FillName_NamesALinkWithNone_OnlyWithATitleThatIsNoAddress()
    {
        var links = Links();
        links.Add("https://example.com/page", null, out _);
        links.Add("https://named.example/", "Mine", out _);

        Assert.False(links.FillName("https://example.com/page", "example.com/page"));   // WebView2's stand-in title
        Assert.False(links.FillName("https://named.example/", "Their title"));         // the user's name stays
        Assert.True(links.FillName("https://example.com/page", "The Page"));
        Assert.False(links.FillName("https://example.com/page", "Later"));              // filled once
        Assert.Equal("The Page", links.Find("https://example.com/page")!.Name);
        Assert.Equal("Mine", links.Find("https://named.example/")!.Name);
    }

    [Fact]
    public void UsableTitle_IsNoneForTheAddress_AndCleanNameTrimsAndCaps()
    {
        const string Url = "https://example.com/a/";
        Assert.Null(BrowserLinks.UsableTitle(Url, Url));
        Assert.Null(BrowserLinks.UsableTitle("example.com/a/", Url));
        Assert.Null(BrowserLinks.UsableTitle("example.com/a", Url));
        Assert.Null(BrowserLinks.UsableTitle("EXAMPLE.COM", Url));
        Assert.Null(BrowserLinks.UsableTitle("   ", Url));
        Assert.Equal("Example", BrowserLinks.UsableTitle(" Example ", Url));

        Assert.Equal("a b", BrowserLinks.CleanName("a\tb\n"));
        Assert.Null(BrowserLinks.CleanName("\r\n"));
        Assert.Equal(BrowserLinks.NameLength, BrowserLinks.CleanName(new string('x', 300))!.Length);
        Assert.Equal(200, BrowserLinks.NameLength);
    }

    [Fact]
    public void Key_MatchesAPunycodeHostToItsUnicodeForm_AndKeepsTheFragment()
    {
        var links = Links();
        Assert.Equal(BrowserLinkOutcome.Done, links.Add("https://bücher.example/", "Books", out _));
        Assert.Equal("Books", links.Find("https://xn--bcher-kva.example/")!.Name);   // as WebView2 reports it
        links.Add("https://xn--caf-dma.example/menu", null, out _);
        Assert.NotNull(links.Find("https://café.example/menu"));

        Assert.NotEqual(BrowserLinks.Key("https://mail.example/#/inbox"), BrowserLinks.Key("https://mail.example/#/sent"));
        Assert.Equal(BrowserLinks.Key("https://EXAMPLE.com/"), BrowserLinks.Key("https://example.com"));
        Assert.NotEqual(BrowserLinks.Key("https://example.com/"), BrowserLinks.Key("http://example.com/"));
        Assert.Null(BrowserLinks.Key("file:///C:/"));
    }

    [Fact]
    public void Resolve_ByNumberOrAddress()
    {
        var links = Links();
        links.Add("https://a.example/", "A", out _);
        links.Add("https://b.example/x", "B", out _);

        Assert.Equal("A", links.Resolve("1")!.Name);
        Assert.Equal("B", links.Resolve(" 2 ")!.Name);
        Assert.Null(links.Resolve("3"));
        Assert.Null(links.Resolve("0"));
        Assert.Equal("B", links.Resolve("b.example/x")!.Name);
        Assert.Null(links.Resolve("nothing"));
    }

    // ── The file ────────────────────────────────────────────────────────────

    [Fact]
    public void TheFile_RoundTrips_ABrokenOneIsMovedAside_AtTheFirstWrite()
    {
        var links = Links();
        links.Add("https://a.example/", "A", out _);
        string json = File.ReadAllText(FilePath);
        Assert.Contains("\"version\": 1", json, StringComparison.Ordinal);
        Assert.Contains("\"links\": [", json, StringComparison.Ordinal);
        Assert.Contains("\"url\": \"https://a.example/\"", json, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(NeonSidekick.Settings.Profiles.ConfigsDirectory(_dir), "*.tmp"));

        File.WriteAllText(FilePath, "{ not json");
        Assert.Empty(links.List());
        Assert.False(File.Exists(FilePath + ".bad"));                   // read alone, nothing moved
        Assert.Equal(BrowserLinkOutcome.Done, links.Add("https://b.example/", null, out _));
        Assert.Equal("{ not json", File.ReadAllText(FilePath + ".bad"));   // the first write keeps it aside
        Assert.Equal(["https://b.example/"], links.List().Select(l => l.Url));
    }

    [Fact]
    public void TheFile_FromANewerVersion_IsLeftAlone_AndRefusesEveryChange()
    {
        Directory.CreateDirectory(NeonSidekick.Settings.Profiles.ConfigsDirectory(_dir));
        const string Newer = "{ \"version\": 2, \"links\": [{ \"url\": \"https://a.example/\", \"name\": \"A\", \"added\": \"2026-10-13T18:00:00+00:00\" }] }";
        File.WriteAllText(FilePath, Newer);
        var links = Links();

        Assert.Empty(links.List());
        Assert.False(links.Writable());
        Assert.Equal(BrowserLinkOutcome.Newer, links.Add("https://b.example/", null, out _));
        Assert.Equal(BrowserLinkOutcome.Newer, links.Clear(out _));
        Assert.Equal(Newer, File.ReadAllText(FilePath));
        Assert.False(File.Exists(FilePath + ".bad"));
        Assert.Equal("Error: " + BrowserLinkText.NewerFile, BrowserLinkText.Refusal(BrowserLinkOutcome.Newer));
        Assert.Equal("browser-links.json was saved by a newer NeonSidekick; update to change it.", BrowserLinkText.NewerFile);
    }

    // ── The ☆ menu ──────────────────────────────────────────────────────────

    [Fact]
    public void MenuItems_Empty_ASavedPage_AndAPageThatCannotBeSaved()
    {
        var empty = BrowserLinks.MenuItems([], "https://a.example/");
        Assert.Equal(["Save this page", "", "No saved links yet"], empty.Select(i => i.Label));
        Assert.True(empty[0].Enabled);
        Assert.Equal(BrowserLinks.ToggleCommand, empty[0].Command);
        Assert.False(empty[2].Enabled);

        var list = new[] { new BrowserLink("https://a.example/", "A", Now) };
        Assert.Equal("Remove this page", BrowserLinks.MenuItems(list, "https://a.example/")[0].Label);
        Assert.False(BrowserLinks.MenuItems(list, "about:blank")[0].Enabled);
        Assert.False(BrowserLinks.MenuItems(list, "https://u:p@a.example/")[0].Enabled);
        Assert.False(BrowserLinks.MenuItems(list, "https://b.example/", writable: false)[0].Enabled);

        // On a blank page the first selectable row is the first link (Ctrl+Shift+O's keyboard lands there).
        var blank = BrowserLinks.MenuItems(list, "about:blank");
        Assert.Equal("A · a.example", blank.First(i => i.Selectable).Label);
        Assert.Equal(BrowserLinks.RowBase, blank.First(i => i.Selectable).Command);
    }

    [Fact]
    public void MenuItems_Fifteen_ThenSubmenusOf25_ThenTheNoteOfMore()
    {
        var list = Enumerable.Range(1, 200).Select(i => new BrowserLink($"https://l{i}.example/", $"Link {i}", Now)).ToList();
        var items = BrowserLinks.MenuItems(list, null);

        Assert.Equal(15, items.Count(i => i.Children is null && i.Selectable) - 0);
        Assert.Equal("Link 1 · l1.example", items[2].Label);
        Assert.Equal(BrowserLinks.RowBase, items[2].Command);
        Assert.Equal(["16–40", "41–65", "66–90", "91–115", "116–140", "141–165", "166–190"], items.Where(i => i.Children is not null).Select(i => i.Label));
        var first = items.First(i => i.Children is not null).Children!;
        Assert.Equal(25, first.Count);
        Assert.Equal(BrowserLinks.RowBase + 15, first[0].Command);
        Assert.Equal("10 more in /browser list", items[^1].Label);
        Assert.False(items[^1].Enabled);
        Assert.Equal(25, items.Count);   // the toggle, a separator, 15, 7 submenus and the note

        var few = BrowserLinks.MenuItems(list.Take(20).ToList(), null);
        Assert.Equal("16–20", few[^1].Label);
        Assert.Equal(5, few[^1].Children!.Count);
    }

    [Fact]
    public void RowLabel_NameAndHost_OrTheAddressWithoutItsScheme()
    {
        Assert.Equal("News · example.com", BrowserLinks.RowLabel(new BrowserLink("https://example.com/news", "News", Now)));
        Assert.Equal("example.com/news", BrowserLinks.RowLabel(new BrowserLink("https://example.com/news", null, Now)));
        Assert.Equal(new string('a', 59) + "… · example.com", BrowserLinks.RowLabel(new BrowserLink("https://example.com/", new string('a', 70), Now)));
    }

    // ── The wording ─────────────────────────────────────────────────────────

    [Fact]
    public void Wording_PaneRows_Footer_Answers()
    {
        var named = new BrowserLink("https://example.com/news", "News", Now);
        var bare = new BrowserLink("https://example.com/", null, Now);
        var columns = BrowserLinkText.Columns([named, bare]);
        Assert.Equal((4, 16), columns);
        Assert.Equal("News  example.com/news", BrowserLinkText.PaneRow(named, columns.Name, columns.Address));
        Assert.Equal("      example.com/", BrowserLinkText.PaneRow(bare, columns.Name, columns.Address));
        Assert.Equal(new UI.MenuFooter("News", "https://example.com/news · saved 2026-10-13 18:00"), BrowserLinkText.Footer(named, TimeZoneInfo.Utc));
        Assert.Equal("3. News — https://example.com/news", BrowserLinkText.PrintedRow(3, named));
        Assert.Equal("1. https://example.com/", BrowserLinkText.PrintedRow(1, bare));

        Assert.Equal("⭐ Saved links", BrowserLinkText.PaneTitle);
        Assert.Equal("https://example.com/news is saved already, as \"News\"; ✎ name on /browser list renames it.", BrowserLinkText.AlreadySaved(named));
        Assert.Equal("Error: There is no saved link 9: the list runs 1 to 2.", BrowserLinkText.NotSaved("9", 2));
        Assert.Equal("Error: There is no saved link 1: none is saved yet.", BrowserLinkText.NotSaved("1", 0));
        Assert.Equal("Error: \"x.example\" is no saved link: give its number in /browser list or its address.", BrowserLinkText.NotSaved("x.example", 2));
        Assert.Equal("⭐ Remove all 3 saved links?", BrowserLinkText.ClearPrompt(3));
        Assert.Null(BrowserLinkText.Refusal(BrowserLinkOutcome.Done));
        Assert.True(BrowserLinkText.Matches("exam", bare));
        Assert.True(BrowserLinkText.Matches("NEWS", named));
        Assert.False(BrowserLinkText.Matches("zzz", named));
    }

    // ── /browser's parse ────────────────────────────────────────────────────

    [Theory]
    [InlineData("", BrowserVerb.Open)]
    [InlineData("example.com", BrowserVerb.Open)]
    [InlineData("3", BrowserVerb.OpenSaved)]
    [InlineData("save", BrowserVerb.Save)]
    [InlineData("save example.com", BrowserVerb.Save)]
    [InlineData("SAVE example.com My page", BrowserVerb.Save)]
    [InlineData("list", BrowserVerb.List)]
    [InlineData("list --CLEAR", BrowserVerb.ClearList)]
    [InlineData("unsave 2", BrowserVerb.Unsave)]
    [InlineData("unsave https://a.example/", BrowserVerb.Unsave)]
    [InlineData("clear history", BrowserVerb.Clear)]
    [InlineData("clear ALL", BrowserVerb.Clear)]
    [InlineData("save notaurl", BrowserVerb.Unknown)]
    [InlineData("unsave", BrowserVerb.Unknown)]
    [InlineData("list extra", BrowserVerb.Unknown)]
    [InlineData("clear", BrowserVerb.Unknown)]
    [InlineData("clear cookies", BrowserVerb.Unknown)]
    [InlineData("not a url", BrowserVerb.Unknown)]
    public void Parse_TheForms(string args, BrowserVerb verb)
    {
        var line = BrowserCommand.Parse(args);
        Assert.Equal(verb, line.Verb);
        Assert.Equal(verb == BrowserVerb.Unknown, line.Error is not null);
    }

    [Fact]
    public void Parse_WhatEachFormCarries_AndWhichArePanes()
    {
        var save = BrowserCommand.Parse("save example.com/a  My  page ");
        Assert.Equal("https://example.com/a", save.Url!.AbsoluteUri);
        Assert.Equal("My  page", save.Name);
        Assert.Null(BrowserCommand.Parse("save").Url);
        Assert.Equal(3, BrowserCommand.Parse("3").Number);
        Assert.Equal(BrowserClear.All, BrowserCommand.Parse("clear all").Clear);
        Assert.Equal("2", BrowserCommand.Parse("unsave 2").Text);
        Assert.StartsWith("'notaurl' is not an http or https address. Usage: /browser save", BrowserCommand.Parse("save notaurl").Error);
        Assert.StartsWith("'not a url' is not an http or https address. Usage: /browser [url]", BrowserCommand.Parse("not a url").Error);

        Assert.Equal([true, true, true, true, false, false, false, false, false],
            new[] { "list", "list --clear", "clear all", "save", "save a.example", "unsave 1", "1", "clear history", "example.com" }.Select(BrowserCommand.IsPane));
    }

    [Fact]
    public void Complete_TheVerbs_ThenListsSwitch_AndClearsWords()
    {
        Assert.Equal(["save", "list", "unsave", "clear"], BrowserCommand.Complete("").Select(c => c.Text));
        Assert.Equal(["list --clear"], BrowserCommand.Complete("list ").Select(c => c.Text));
        Assert.Equal(["clear history", "clear all"], BrowserCommand.Complete("clear ").Select(c => c.Text));
        Assert.Empty(BrowserCommand.Complete("save example.com "));
    }

    /// <summary>The words with no Browser window (2026-10-17, the Mac pass, the user's call): Enter opens a link in the default browser, and nothing names the window or its Ctrl+D.</summary>
    [Fact]
    public void WithNoWindow_TheCaptionAndTheEmptyLinesNameNoWindow()
    {
        Assert.Equal("2 saved links; Enter opens one in the Browser window.", BrowserLinkText.Caption(2));
        Assert.Equal("2 saved links; Enter opens one in your browser.", BrowserLinkText.Caption(2, window: false));
        Assert.Equal("No saved links yet: a adds one.", BrowserLinkText.EmptyRowNoWindow);
        Assert.Equal("No saved links yet: /browser save <url> adds one.", BrowserLinkText.NoneSavedNoWindow);
        Assert.Equal(BrowserLinkText.NoneSaved, BrowserLinkText.NoneSavedFor(true));
        Assert.Equal(BrowserLinkText.NoneSavedNoWindow, BrowserLinkText.NoneSavedFor(false));
        var notes = BrowserCommand.Words.ToDictionary(w => w.Word, w => w.Note);
        Assert.Equal(BrowserCommand.WindowHere(), notes[BrowserCommand.ListWord].Contains("Browser window", StringComparison.Ordinal));
        Assert.Equal(BrowserCommand.WindowHere(), notes[BrowserCommand.SaveWord].Contains("Browser window", StringComparison.Ordinal));
    }

    [Fact]
    public void Headless_SavesListsClearsAndUnsaves_AndRefusesTheWindowsForms()
    {
        var links = Links();
        Assert.Equal(["Saved \"Ex\" (https://example.com/)."], BrowserCommand.Headless(links, "save example.com Ex"));
        // Off Windows the caption has no Browser window to promise (2026-10-17, the Mac pass).
        Assert.Equal([BrowserCommand.WindowHere() ? "1 saved link; Enter opens one in the Browser window." : "1 saved link; Enter opens one in your browser.", "1. Ex — https://example.com/"], BrowserCommand.Headless(links, "list"));
        Assert.Equal(["Removed \"Ex\" from the saved links."], BrowserCommand.Headless(links, "unsave 1"));
        Assert.Equal([BrowserLinkText.NoneSavedFor(BrowserCommand.WindowHere())], BrowserCommand.Headless(links, "list"));
        links.Add("https://a.example/", null, out _);
        Assert.Equal(["Removed 1 saved link."], BrowserCommand.Headless(links, "list --clear"));   // headless: at once
        Assert.Equal(["Error: " + Web.BotCheckText.BrowserNeedsScreen], BrowserCommand.Headless(links, "example.com"));
        Assert.Equal(["Error: " + Web.BotCheckText.BrowserNeedsScreen], BrowserCommand.Headless(links, "save"));
        Assert.Equal(["Error: " + Web.BotCheckText.ClearUsage], BrowserCommand.Headless(links, "clear"));
    }
    // ── Import (2026-10-25, /profile push|pull links) ───────────────────────

    [Fact]
    public void Import_Appends_SkipsByKey_KeepsTheRecords_AndOverwriteReplaces()
    {
        var links = Links();
        links.Add("https://b.example/", "Mine", out _);
        var older = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        BrowserLink[] theirs = [new("https://a.example/", "A", older), new("https://B.example/", "Theirs", older), new("ftp://x.example/", "F", older)];

        var result = links.Import(theirs, overwrite: false);

        Assert.Equal(new BrowserLinkImport(BrowserLinkOutcome.Done, 1, 1), result);   // the ftp one dropped, never counted
        Assert.Equal([new BrowserLink("https://b.example/", "Mine", Now), new BrowserLink("https://a.example/", "A", older)], links.List());

        Assert.Equal(new BrowserLinkImport(BrowserLinkOutcome.Done, 2, 0), links.Import(theirs, overwrite: true));
        Assert.Equal(["https://a.example/", "https://B.example/"], links.List().Select(l => l.Url));
    }

    [Fact]
    public void Import_IntoANewerFile_IsRefused_AndABrokenOneIsMovedAside()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, """{ "version": 99, "links": [] }""");
        BrowserLink[] theirs = [new("https://a.example/", "A", Now)];
        Assert.Equal(BrowserLinkOutcome.Newer, Links().Import(theirs, overwrite: false).Outcome);
        Assert.Contains("99", File.ReadAllText(FilePath));

        File.WriteAllText(FilePath, "{ not json");
        Assert.Equal(new BrowserLinkImport(BrowserLinkOutcome.Done, 1, 0), Links().Import(theirs, overwrite: false));
        Assert.Equal("{ not json", File.ReadAllText(FilePath + ".bad"));
    }
}
