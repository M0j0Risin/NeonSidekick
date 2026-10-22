using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Obsidian;
using NeonSidekick.Settings;
using NeonSidekick.Skills;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The vault after the 2026-10-22 fixes (the fix plan's Phase 7): links resolved as Obsidian does (the linking note's folder first, relative
/// wikilinks, no alias, composed names, the volume's case rule, real file types), the scanner linear and right about parentheses, titles,
/// comments and properties, the properties' quoting and repeated keys, the daily note's formats and templates and settings, and the tools'
/// writes reaching only the note meant, its trash, its canvases, its caps and a move taken back when it fails.
/// </summary>
public sealed class VaultFixesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly ManualTimeProvider _time = new();
    private readonly AppSettingsData _settings = new() { ObsidianTools = true, ObsidianAllowDelete = true };
    private readonly ObsidianVault _vault;
    private readonly IReadOnlyList<AIFunction> _tools;

    public VaultFixesTests()
    {
        _root = Path.Combine(_dir, "vault");
        Directory.CreateDirectory(Path.Combine(_root, ".obsidian"));
        _settings.ObsidianVault = _root;
        _vault = new ObsidianVault(() => _settings.ObsidianVault, _time);
        _tools = App.ChatScreen.ObsidianTools(_vault, () => _settings);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private void Put(string relative, string content)
    {
        string full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private string Get(string relative) => File.ReadAllText(Path.Combine(_root, relative));

    private async Task<string> Invoke<T>(params (string Name, object? Value)[] pairs) where T : AIFunction =>
        (string)(await _tools.OfType<T>().Single().InvokeAsync(new AIFunctionArguments(pairs.ToDictionary(p => p.Name, p => p.Value))))!;

    private NoteIndex Index()
    {
        var index = new NoteIndex();
        index.Refresh(_root);
        return index;
    }

    // ── resolution ──

    [Fact]
    public void ABareName_FindsTheLinkingNotesFolderFirst()
    {
        Put("Plan.md", "root");
        Put("Projects/Plan.md", "mine");
        Put("Projects/Note.md", "[[Plan]]");
        Put("Projects/chart.png", "x");
        Put("chart.png", "x");
        var index = Index();
        var note = index.Get("Projects/Note.md")!;
        Assert.Equal("Projects/Plan.md", index.ResolveLink(note, note.Scan.Links[0]).Relative);
        Assert.Equal("Projects/chart.png", index.Resolve("chart.png", "Projects").Relative);
        Assert.Equal("Plan.md", index.Resolve("Plan", "").Relative);   // from the root, the root's
    }

    [Fact]
    public void ARelativeWikilink_ResolvesFromItsNote_Only()
    {
        Put("Archive/Plan.md", "old");
        Put("Projects/Note.md", "[[../Archive/Plan]] [[./Plan]]");
        Put("Plan.md", "root");
        var index = Index();
        var note = index.Get("Projects/Note.md")!;
        Assert.Equal("Archive/Plan.md", index.ResolveLink(note, note.Scan.Links[0]).Relative);
        Assert.False(index.ResolveLink(note, note.Scan.Links[1]).Found);   // ./Plan is Projects/Plan, which is not there
    }

    [Fact]
    public void AnAlias_IsNoLink_ButStillFindsANoteForARead()
    {
        Put("Plan.md", "---\naliases: [Roadmap]\n---\nbody");
        Put("Index.md", "[[Roadmap]]");
        var index = Index();
        var source = index.Get("Index.md")!;
        Assert.False(index.ResolveLink(source, source.Scan.Links[0]).Found);
        Assert.Equal("Plan.md", index.Resolve("Roadmap").Relative);
        Assert.False(index.Resolve("Roadmap", use: ResolveFor.Write).Found);
    }

    [Fact]
    public void AComposedAndADecomposedName_AreTheSameNote()
    {
        string decomposed = "Café";   // é as e + combining acute, as a Mac's Finder keeps it
        Put(decomposed + ".md", "x");
        var index = Index();
        Assert.Equal("Café.md", index.Resolve("Café").Relative);
        Assert.Equal("Café.md", VaultPaths.Normalize(decomposed + ".md"));
    }

    [Fact]
    public void OnlyRealFileTypes_AreNotNotes()
    {
        Assert.False(VaultPaths.HasFileExtension("Call Dr.Smith"));
        Assert.False(VaultPaths.HasFileExtension("Learning Node.js"));
        Assert.False(VaultPaths.HasFileExtension("Mon. Tue"));
        Assert.True(VaultPaths.HasFileExtension("diagram.PNG"));
        Assert.True(VaultPaths.HasFileExtension("Board.canvas"));
    }

    [Fact]
    public async Task ANoteNamedLikeAFile_CanBeCreated()
    {
        Assert.StartsWith("created", await Invoke<VaultWriteTool>(("note", "Call Dr.Smith"), ("content", "x")), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_root, "Call Dr.Smith.md")));
    }

    [Fact]
    public void OnACaseSensitiveVolume_TwoSpellingsAreTwoNotes()
    {
        using var rule = CaseRule.Override(_ => true);
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            // A real case-blind volume can't hold both files: the comparer is what this pins.
            Put("Plan.md", "x");
            var index = Index();
            Assert.Same(StringComparer.Ordinal, index.PathComparer);
            return;
        }

        Put("Plan.md", "a");
        Put("plan.md", "b");
        Assert.Equal(2, Index().Notes.Count);
    }

    [Fact]
    public void ASuffixLink_StillResolves_FromTheTable()
    {
        Put("Work/Projects/Plan.md", "x");
        Put("Work/img/a.png", "x");
        var index = Index();
        Assert.Equal("Work/Projects/Plan.md", index.Resolve("Projects/Plan").Relative);
        Assert.Equal("Work/img/a.png", index.Resolve("img/a.png").Relative);
        Assert.False(index.Resolve("Projects/Plan", use: ResolveFor.Write).Found);
    }

    [WindowsFact]
    public void AnUnreadableNote_IsReadAgain_OnceItCanBe()
    {
        Put("Plan.md", "[[Other]]");
        Put("Other.md", "x");
        var index = new NoteIndex();
        using (new FileStream(Path.Combine(_root, "Plan.md"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            index.Refresh(_root);
            Assert.Equal(NoteRead.Unreadable, index.Get("Plan.md")!.NotRead);
        }

        index.Refresh(_root);
        Assert.Null(index.Get("Plan.md")!.NotRead);
        Assert.Single(index.Get("Plan.md")!.Scan.Links);
    }

    // ── the scanner ──

    [Fact]
    public void AMarkdownLink_KeepsItsParentheses_AndItsTitle()
    {
        // Obsidian encodes a space and leaves the parentheses: the URL runs to the ')' that balances them.
        var scan = NoteScanner.Scan(["[x](Notes%20(old).md) and [y](Plan.md \"The plan\") and [z](<My Note.md> 'T')"]);
        Assert.Equal(["Notes (old).md", "Plan.md", "My Note.md"], scan.Links.Select(l => l.Target));
        Assert.Equal(["", "\"The plan\"", "'T'"], scan.Links.Select(l => l.Title));
        Assert.Equal("[x](Notes%20(old).md)", scan.Links[0].Raw);
    }

    [Fact]
    public void ALineOfUnclosedOpeners_IsScannedInLinearTime()
    {
        string line = string.Concat(Enumerable.Repeat("[a ![[b `c ", 100_000));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var scan = NoteScanner.Scan([line]);
        Assert.Empty(scan.Links);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), clock.Elapsed.ToString());
    }

    [Fact]
    public void ABlockIdInsideAComment_IsNoBlockId()
    {
        var scan = NoteScanner.Scan(["text ^real", "%% hidden ^secret", "still %% after ^kept"]);
        Assert.Equal(["real", "kept"], scan.BlockIds);
    }

    // ── properties ──

    [Fact]
    public void AKeyWithALineBreak_IsRefused()
    {
        using var doc = JsonDocument.Parse("\"x\"");
        Assert.False(NoteProperties.TryRender("a\n---\nb", doc.RootElement, out _));
        Assert.False(NoteProperties.TryRender("tab\tkey", doc.RootElement, out _));
        Assert.True(NoteProperties.TryRender("ok key", doc.RootElement, out _));
    }

    [Fact]
    public void ARepeatedKey_IsReadAsItsLast_AndSetOrRemovedWhole()
    {
        string[] lines = ["---", "status: draft", "tags: [a]", "status: done", "---", "body"];
        Assert.Equal("done", NoteProperties.Parse(lines).Find("status")!.Value);
        var set = NoteProperties.Set(lines, "status", ["status: final"]);
        Assert.Equal(["---", "tags: [a]", "status: final", "---", "body"], set);
        var removed = NoteProperties.Remove(lines, "status", out bool gone);
        Assert.True(gone);
        Assert.Equal(["---", "tags: [a]", "---", "body"], removed);
    }

    [Fact]
    public void ACommentAfterAValue_IsNoPartOfIt()
    {
        var front = NoteProperties.Parse(["---", "tags: [project, work] # areas", "status: \"done\" # note", "plain: yes # no", "list:", "  - one # first", "---"]);
        Assert.Equal(["project", "work"], NoteProperties.Tags(front));
        Assert.Equal("done", front.Find("status")!.Value);
        Assert.Equal("yes", front.Find("plain")!.Value);
        Assert.Equal(["one"], front.Find("list")!.Items);
        Assert.Equal("a", NoteProperties.WithoutComment("a # b # c"));
        Assert.Equal("#a, b", NoteProperties.WithoutComment("#a, b"));
    }

    [Fact]
    public void AValueThatWouldReadBackOtherwise_IsQuoted()
    {
        Assert.Equal("\" draft \"", SkillFrontmatter.Scalar(" draft "));
        Assert.Equal("\"done\\t#later\"", SkillFrontmatter.Scalar("done\t#later"));
        Assert.Equal("\"a\\x07b\"", SkillFrontmatter.Scalar("a\u0007b"));
        Assert.Equal("plain words", SkillFrontmatter.Scalar("plain words"));
    }

    // ── the daily note ──

    [Fact]
    public void ADayStep_PastItsRange_IsABadDate_NotAThrow()
    {
        var today = new DateOnly(2026, 10, 22);
        Assert.False(DailyNotes.TryParseDay("-2147483648", today, out _));
        Assert.False(DailyNotes.TryParseDay("+36501", today, out _));
        Assert.True(DailyNotes.TryParseDay("-3", today, out var back));
        Assert.Equal(new DateOnly(2026, 10, 19), back);
    }

    [Fact]
    public void Epochs_CountTheZone()
    {
        var moment = new DateTime(2026, 1, 1, 2, 0, 0);
        Assert.Equal("1767225600", DailyNotes.Format(moment, "X", TimeSpan.FromHours(2)));   // 2026-01-01T00:00Z
        Assert.Equal("1767232800", DailyNotes.Format(moment, "X"));
        Assert.Equal("+02:00 +0200", DailyNotes.Format(moment, "Z ZZ", TimeSpan.FromHours(2)));
    }

    [Fact]
    public void LocaleWeeks_AreSundayFirst_AndIsoWeeksIso()
    {
        var sunday = new DateTime(2026, 1, 4);
        Assert.Equal("2026-W02", DailyNotes.Format(sunday, "gggg-[W]ww"));
        Assert.Equal("2026-W01", DailyNotes.Format(sunday, "GGGG-[W]WW"));
        Assert.Equal("2026-W01", DailyNotes.Format(new DateTime(2025, 12, 31), "gggg-[W]ww"));
        Assert.Equal((2026, 1), DailyNotes.LocaleWeek(new DateTime(2025, 12, 28)));
    }

    [Fact]
    public void LongDates_AndTheRestOfMomentsTokens_Format()
    {
        var moment = new DateTime(2026, 1, 4, 0, 5, 9, 123);
        Assert.Equal("January 4, 2026", DailyNotes.Format(moment, "LL"));
        Assert.Equal("01/04/2026", DailyNotes.Format(moment, "L"));
        Assert.Equal("Sun, Jan 4, 2026 12:05 AM", DailyNotes.Format(moment, "llll"));
        Assert.Equal("123 7 0 24", DailyNotes.Format(moment, "SSS E e k"));
    }

    [Fact]
    public void ATemplate_IsFilledAsTheDailyNotesLibraryFillsIt()
    {
        var day = new DateOnly(2026, 10, 22);
        var now = new DateTime(2026, 10, 25, 9, 30, 0);
        string text = DailyNotes.ApplyTemplate("{{title}}|{{date}}|{{time}}|{{yesterday}}|{{tomorrow}}|{{date+1d:YYYY-MM-DD}}|{{date-1M}}|{{time:HH}}|{{other}}", "22.10.2026", day, now, "DD.MM.YYYY");
        Assert.Equal("22.10.2026|22.10.2026|09:30|21.10.2026|23.10.2026|2026-10-23|22.09.2026|09|{{other}}", text);
    }

    [Fact]
    public void PeriodicNotes_MakeTheDailySettings_WhileOn_AndAnOffCorePluginIsSaid()
    {
        Put(".obsidian/core-plugins.json", "{ \"daily-notes\": false }");
        Assert.True(_vault.DailySettings(_root).CoreOff);

        Put(".obsidian/community-plugins.json", "[\"periodic-notes\"]");
        Put(".obsidian/plugins/periodic-notes/data.json", "{ \"daily\": { \"enabled\": true, \"folder\": \"Journal/\", \"format\": \"YYYY/MM-DD\", \"template\": \"T/Day\" } }");
        var settings = _vault.DailySettings(_root);
        Assert.Equal(("Journal", "YYYY/MM-DD", "T/Day", DailyNotes.PeriodicSource), (settings.Folder, settings.Format, settings.Template, settings.Source));

        Put(".obsidian/core-plugins.json", "[\"file-explorer\", \"daily-notes\"]");
        Put(".obsidian/community-plugins.json", "[]");
        Assert.False(_vault.DailySettings(_root).CoreOff);
    }

    // ── the tools ──

    [Fact]
    public async Task AWriteToAPath_NeverReachesAnotherBySuffix_NorOneOfSeveralByName()
    {
        Put("Archive/2023/Projects/Plan.md", "old");
        Assert.StartsWith("created", await Invoke<VaultWriteTool>(("note", "Projects/Plan.md"), ("content", "new"), ("mode", "overwrite")), StringComparison.Ordinal);
        Assert.Equal("old", Get("Archive/2023/Projects/Plan.md"));
        Assert.Equal("new\n", Get("Projects/Plan.md"));

        string refused = await Invoke<VaultWriteTool>(("note", "Plan"), ("content", "x"), ("mode", "overwrite"));
        Assert.StartsWith("Error: \"Plan\" names 2 notes", refused, StringComparison.Ordinal);
        Assert.Equal("old", Get("Archive/2023/Projects/Plan.md"));
    }

    [Fact]
    public async Task AWrite_PastWriteFilesCap_IsRefused()
    {
        Assert.Equal(ObsidianText.ContentTooLong, await Invoke<VaultWriteTool>(("note", "Big"), ("content", new string('x', WorkingDirectory.MaxWriteChars + 1))));
        Assert.Equal(ObsidianText.ContentTooLong, await Invoke<VaultDailyTool>(("append", new string('x', WorkingDirectory.MaxWriteChars + 1))));
    }

    [Fact]
    public async Task AFolderNote_IsDeletedByName()
    {
        Put("Projects/Inside.md", "x");
        Put("Projects.md", "the folder's note");
        Assert.StartsWith("deleted Projects.md", await Invoke<VaultDeleteTool>(("note", "Projects")), StringComparison.Ordinal);
        Assert.True(Directory.Exists(Path.Combine(_root, "Projects")));
        Assert.Equal(ObsidianText.IsAFolder("Projects"), await Invoke<VaultDeleteTool>(("note", "Projects")));
    }

    [Fact]
    public async Task ADelete_GoesWhereTheVaultSays_NeverForGood()
    {
        var bin = new FakeRecycleBin();
        _vault.SystemTrash = () => bin;
        Put("A.md", "x");
        Assert.Contains("moved to the " + FileText.RecycleBinName, await Invoke<VaultDeleteTool>(("note", "A")), StringComparison.Ordinal);
        Assert.Single(bin.Moved);

        Put(".obsidian/app.json", "{ \"trashOption\": \"none\" }");
        Put("B.md", "x");
        Assert.Contains("moved to .trash/B.md", await Invoke<VaultDeleteTool>(("note", "B")), StringComparison.Ordinal);

        Put(".obsidian/app.json", "{ \"TrashOption\": \"local\" }");
        Put("C.md", "x");
        Assert.Contains("moved to .trash/C.md", await Invoke<VaultDeleteTool>(("note", "C")), StringComparison.Ordinal);
        Assert.Single(bin.Moved);
    }

    [Fact]
    public async Task AListValue_NeedsItsProperty_AndCapsSayTheTruth()
    {
        Assert.Equal(ObsidianText.ValueNeedsProperty, await Invoke<VaultListTool>(("value", "draft")));
        Assert.Contains("narrow it with folder or tag", ObsidianText.FirstShown(200, 200, 200), StringComparison.Ordinal);
        Assert.Contains("max_results raises it to 200", ObsidianText.FirstShown(50, 50, 200), StringComparison.Ordinal);
    }

    [Fact]
    public void RefusalLists_AreCapped()
    {
        var headings = Enumerable.Range(0, 1_000).Select(i => "H" + i).ToList();
        string refused = ObsidianText.HeadingNotFound("A.md", "X", headings);
        Assert.Contains("… 950 more", refused, StringComparison.Ordinal);
        Assert.Contains("… 5 more", ObsidianText.AlsoNamed("index", Enumerable.Range(0, 10).Select(i => i + "/index.md").ToList()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMove_RenamesThePropertiesLinks_AndTheCanvases()
    {
        Put("Plan.md", "x");
        Put("Index.md", "---\nrelated: \"[[Plan]]\"\n---\nbody [x](Plan.md \"title\")");
        Put("Board.canvas", "{\"nodes\":[{\"id\":\"1\",\"type\":\"file\",\"file\":\"Plan.md\",\"x\":0,\"y\":0,\"width\":400,\"height\":400},{\"id\":\"2\",\"type\":\"text\",\"text\":\"see [[Plan]]\",\"x\":1.5}],\"edges\":[]}");

        string moved = await Invoke<VaultMoveTool>(("note", "Plan"), ("to", "Roadmap"));
        Assert.StartsWith("moved Plan.md to Roadmap.md", moved, StringComparison.Ordinal);
        Assert.Equal("---\nrelated: \"[[Roadmap]]\"\n---\nbody [x](Roadmap.md \"title\")", Get("Index.md"));
        string canvas = Get("Board.canvas");
        Assert.Contains("\"file\": \"Roadmap.md\"", canvas, StringComparison.Ordinal);
        Assert.Contains("\"text\": \"see [[Roadmap]]\"", canvas, StringComparison.Ordinal);
        Assert.Contains("\"x\": 1.5", canvas, StringComparison.Ordinal);
        Assert.Contains("Board.canvas (canvas)", moved, StringComparison.Ordinal);
        Assert.Contains("Board.canvas", await Invoke<VaultDeleteTool>(("note", "Roadmap")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMove_OfANoteWithParenthesesInItsName_RewritesTheWholeLink()
    {
        Put("Notes (old).md", "x");
        Put("Index.md", "see [x](Notes%20(old).md) here");
        await Invoke<VaultMoveTool>(("note", "Notes (old)"), ("to", "New"));
        Assert.Equal("see [x](New.md) here", Get("Index.md"));
    }

    [WindowsFact]
    public async Task AMoveThatFails_PutsTheRewrittenNotesBack()
    {
        Put("Plan.md", "x");
        Put("Index.md", "[[Plan]]");
        string result;
        using (new FileStream(Path.Combine(_root, "Plan.md"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            result = await Invoke<VaultMoveTool>(("note", "Plan"), ("to", "Roadmap"));
        }

        Assert.StartsWith("Error: the move stopped", result, StringComparison.Ordinal);
        Assert.Contains("the note did not move, and no link was left changed", result, StringComparison.Ordinal);
        Assert.Equal("[[Plan]]", Get("Index.md"));
        Assert.True(File.Exists(Path.Combine(_root, "Plan.md")));
    }

    [Fact]
    public async Task AVaultCall_HonorsEsc()
    {
        Put("A.md", "x");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await _tools.OfType<VaultSearchTool>().Single().InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["query"] = "x" }), cancelled.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => _vault.Search("x", "", "", null, cancelled.Token));
    }

    [Fact]
    public void ACanvas_IsRewrittenOnlyWhereItChanges_AndKeepsTheRest()
    {
        const string json = "{\"nodes\":[{\"id\":\"1\",\"type\":\"file\",\"file\":\"A.md\",\"color\":\"4\"}],\"edges\":[{\"id\":\"e\",\"fromNode\":\"1\",\"toNode\":\"1\"}],\"extra\":{\"k\":[1,2.50]}}";
        Assert.Null(CanvasFile.Rewrite(json, _ => null, _ => null));
        string rewritten = CanvasFile.Rewrite(json, f => f == "A.md" ? "B/A.md" : null, _ => null)!;
        Assert.Contains("\"file\": \"B/A.md\"", rewritten, StringComparison.Ordinal);
        Assert.Contains("\"color\": \"4\"", rewritten, StringComparison.Ordinal);
        Assert.Contains("2.50", rewritten, StringComparison.Ordinal);
        Assert.Contains("\n\t\"nodes\"", rewritten, StringComparison.Ordinal);
        Assert.Equal([new CanvasNode("file", "A.md", null)], CanvasFile.Nodes(json));
        Assert.Empty(CanvasFile.Nodes("not json"));
    }
}
