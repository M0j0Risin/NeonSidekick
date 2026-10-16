using System.Text.Json;
using NeonSidekick.Prompts;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The saved prompts (2026-10-13, the user's ask): the list in <c>prompts.json</c> (<see cref="SavedPrompts"/>) and its wording
/// (<see cref="SavedPromptText"/>). <c>/prompts</c>' parse is <c>PromptsCommandTests</c>', the pane <c>ChatScreenTests.Prompts</c>'.
/// </summary>
public sealed class SavedPromptsTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 18, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick-prompts-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new() { UtcNow = Now };

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private SavedPrompts Prompts() => new(_dir, _time);

    private string FilePath => NeonSidekick.Settings.Profiles.ConfigPath(_dir, SavedPrompts.FileName);   // <profile>\configs since 2026-10-16

    private static IEnumerable<string> Texts(SavedPrompts prompts) => prompts.List().Select(p => p.Text);

    // ── The list ─────────────────────────────────────────────────────────────

    [Fact]
    public void Add_PutsANewOneLast_ADuplicateKeepsItsPlace()
    {
        var prompts = Prompts();
        Assert.Equal(SavedPromptOutcome.Done, prompts.Add("Summarize this.", out var a));
        Assert.Equal("Summarize this.", a!.Text);
        Assert.Equal(Now, a.Added);
        Assert.Equal(32, a.Id.Length);
        Assert.Equal(SavedPromptOutcome.Done, prompts.Add("Translate to French.", out _));
        Assert.Equal(SavedPromptOutcome.AlreadySaved, prompts.Add("  Summarize this.  ", out var known));
        Assert.Equal(a.Id, known!.Id);

        Assert.Equal(["Summarize this.", "Translate to French."], Texts(new SavedPrompts(_dir)));   // another instance reads it
        Assert.Equal("Translate to French.", prompts.Resolve("2")!.Text);
        Assert.Null(prompts.Resolve("3"));
        Assert.Null(prompts.Resolve("0"));
        Assert.Null(prompts.Resolve("one"));
    }

    [Fact]
    public void Clean_IsWhatAPasteBecomes_ThenTrimmed()
    {
        // Line breaks one \n, a tab four blanks, control characters dropped, the ends trimmed: nothing the draft or the pane cannot draw.
        Assert.Equal("one\ntwo\n    three", SavedPrompts.Clean("  one\r\ntwo\r    three\n\n"));
        Assert.Equal("ab", SavedPrompts.Clean("a\u0007b"));
        Assert.Equal("", SavedPrompts.Clean(" \t\r\n "));
        Assert.Equal("", SavedPrompts.Clean(null));

        var prompts = Prompts();
        Assert.Equal(SavedPromptOutcome.Done, prompts.Add("Line one\r\n\tLine two", out var saved));
        Assert.Equal("Line one\n    Line two", saved!.Text);
        Assert.Equal(2, SavedPromptText.LineCount(saved.Text));
    }

    [Fact]
    public void Add_RefusesBlank_AndPastTheCap()
    {
        var prompts = Prompts();
        Assert.Equal(SavedPromptOutcome.Empty, prompts.Add("   ", out _));
        Assert.Equal(SavedPromptOutcome.TooLong, prompts.Add(new string('x', SavedPrompts.MaxLength + 1), out _));
        Assert.Equal(SavedPromptOutcome.Done, prompts.Add(new string('x', SavedPrompts.MaxLength), out _));
        Assert.Single(prompts.List());
        Assert.StartsWith("Error: ", SavedPromptText.Refusal(SavedPromptOutcome.Empty));
        Assert.Contains("20,000", SavedPromptText.Refusal(SavedPromptOutcome.TooLong));
    }

    [Fact]
    public void Edit_ByItsId_RefusesBlankAndAnotherPromptsText()
    {
        var prompts = Prompts();
        prompts.Add("One", out var one);
        prompts.Add("Two", out var two);

        Assert.Equal(SavedPromptOutcome.Done, prompts.Edit(one!.Id, "One, changed\nand longer"));
        Assert.Equal(["One, changed\nand longer", "Two"], Texts(prompts));
        Assert.Equal(one.Id, prompts.List()[0].Id);   // the same prompt, its place kept
        Assert.Equal(SavedPromptOutcome.Duplicate, prompts.Edit(one.Id, "Two"));
        Assert.Equal(SavedPromptOutcome.Empty, prompts.Edit(two!.Id, "  "));   // never a removal
        Assert.Equal(2, prompts.List().Count);
        Assert.Equal(SavedPromptOutcome.NotSaved, prompts.Edit("nope", "Three"));
        Assert.Equal(SavedPromptOutcome.Done, prompts.Edit(two.Id, "Two"));   // the same text: nothing written, still done
    }

    [Fact]
    public void Remove_Move_AndClear()
    {
        var prompts = Prompts();
        foreach (string text in (string[])["A", "B", "C"])
        {
            prompts.Add(text, out _);
        }

        var b = prompts.Resolve("2")!;
        Assert.Equal(SavedPromptOutcome.Done, prompts.Move(b.Id, -1));
        Assert.Equal(["B", "A", "C"], Texts(prompts));
        Assert.Equal(SavedPromptOutcome.Done, prompts.Move(b.Id, int.MaxValue));
        Assert.Equal(["A", "C", "B"], Texts(prompts));
        Assert.Equal(SavedPromptOutcome.Done, prompts.Move(b.Id, 5));   // held at the end
        Assert.Equal(["A", "C", "B"], Texts(prompts));

        Assert.Equal(SavedPromptOutcome.Done, prompts.Remove(b.Id));
        Assert.Equal(["A", "C"], Texts(prompts));
        Assert.Equal(SavedPromptOutcome.NotSaved, prompts.Remove(b.Id));

        Assert.Equal(SavedPromptOutcome.Done, prompts.Clear(out int cleared));
        Assert.Equal(2, cleared);
        Assert.Equal(SavedPromptOutcome.Done, prompts.Clear(out cleared));
        Assert.Equal(0, cleared);
    }

    [Fact]
    public void Import_AddsWhatTheTargetLacks_OrReplacesWithOverwrite()
    {
        var source = new SavedPrompts(Path.Combine(_dir, "source"), _time);
        source.Add("A", out _);
        source.Add("B", out _);
        var target = new SavedPrompts(Path.Combine(_dir, "target"), _time);
        target.Add("B", out var b);
        target.Add("Z", out _);

        var added = target.Import(source.List(), overwrite: false);
        Assert.Equal(new SavedPromptImport(SavedPromptOutcome.Done, 1, 1), added);
        Assert.Equal(["B", "Z", "A"], Texts(target));
        Assert.Equal(b!.Id, target.List()[0].Id);
        Assert.DoesNotContain(target.List(), p => source.List().Any(s => s.Id == p.Id));   // new ids, never the source's

        var replaced = target.Import(source.List(), overwrite: true);
        Assert.Equal(new SavedPromptImport(SavedPromptOutcome.Done, 2, 0), replaced);
        Assert.Equal(["A", "B"], Texts(target));
        Assert.Equal(source.List().Select(p => p.Added), target.List().Select(p => p.Added));   // the dates kept
    }

    // ── The file ─────────────────────────────────────────────────────────────

    [Fact]
    public void TheFile_RoundTrips_ABrokenOneIsMovedAside_AtTheFirstWrite()
    {
        Prompts().Add("A", out _);
        using (var json = JsonDocument.Parse(File.ReadAllText(FilePath)))
        {
            // The contract: the file's keys.
            Assert.Equal(1, json.RootElement.GetProperty("version").GetInt32());
            var first = json.RootElement.GetProperty("prompts")[0];
            Assert.Equal("A", first.GetProperty("text").GetString());
            Assert.True(first.TryGetProperty("id", out _));
            Assert.True(first.TryGetProperty("added", out _));
        }

        File.WriteAllText(FilePath, "{ not json");
        Assert.Empty(Prompts().List());
        Assert.False(File.Exists(FilePath + ".bad"));                   // read alone, nothing moved
        Assert.Equal(SavedPromptOutcome.Done, Prompts().Add("B", out _));
        Assert.Equal("{ not json", File.ReadAllText(FilePath + ".bad"));   // the first write keeps it aside
        Assert.Equal(["B"], Texts(Prompts()));
    }

    [Fact]
    public void TheFile_FromANewerVersion_IsLeftAlone_AndRefusesEveryChange()
    {
        Directory.CreateDirectory(NeonSidekick.Settings.Profiles.ConfigsDirectory(_dir));
        const string Newer = "{ \"version\": 2, \"prompts\": [{ \"id\": \"a\", \"text\": \"A\", \"added\": \"2026-10-13T18:00:00+00:00\" }] }";
        File.WriteAllText(FilePath, Newer);

        var prompts = Prompts();
        Assert.Empty(prompts.List());
        Assert.False(prompts.Writable());
        Assert.Equal(SavedPromptOutcome.Newer, prompts.Add("B", out _));
        Assert.Equal(SavedPromptOutcome.Newer, prompts.Clear(out _));
        Assert.Equal(SavedPromptOutcome.Newer, prompts.Import([new SavedPrompt("x", "C", Now)], overwrite: true).Outcome);
        Assert.Equal(Newer, File.ReadAllText(FilePath));
        Assert.False(File.Exists(FilePath + ".bad"));
        Assert.Equal("Error: " + SavedPromptText.NewerFile, SavedPromptText.Refusal(SavedPromptOutcome.Newer));
    }

    [WindowsFact]
    public void TheFile_ThatCannotBeRead_IsNeverWrittenOver()
    {
        Prompts().Add("A", out _);
        string before = File.ReadAllText(FilePath);
        using (new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var prompts = Prompts();
            Assert.Empty(prompts.List());
            Assert.False(prompts.Writable());
            Assert.Equal(SavedPromptOutcome.Failed, prompts.Add("B", out _));
        }

        Assert.Equal(before, File.ReadAllText(FilePath));
        Assert.False(File.Exists(FilePath + ".bad"));
        Assert.Equal(["A"], Texts(Prompts()));
    }

    [Fact]
    public void TheFile_DropsAnEntryWithNoIdOrText_AndARepeat()
    {
        Directory.CreateDirectory(NeonSidekick.Settings.Profiles.ConfigsDirectory(_dir));
        File.WriteAllText(FilePath, """
            { "version": 1, "prompts": [
              { "id": "a", "text": "A", "added": "2026-10-13T18:00:00+00:00" },
              { "id": "", "text": "No id", "added": "2026-10-13T18:00:00+00:00" },
              { "id": "b", "text": "  ", "added": "2026-10-13T18:00:00+00:00" },
              { "id": "c", "text": "A", "added": "2026-10-13T18:00:00+00:00" },
              { "id": "d", "text": "D\r\n", "added": "2026-10-13T18:00:00+00:00" }
            ] }
            """);

        Assert.Equal(["A", "D"], Texts(Prompts()));
    }

    // ── The wording ──────────────────────────────────────────────────────────

    [Fact]
    public void PaneRow_TheNumberAligned_TheFirstLine_AndTheLinesAfterIt()
    {
        var one = new SavedPrompt("a", "Summarize this.", Now);
        var three = new SavedPrompt("b", "Review the diff\nfor bugs\nand style", Now);
        Assert.Equal(" 3. Summarize this.", SavedPromptText.PaneRow(3, one, 2));
        Assert.Equal("12. Review the diff +2 lines", SavedPromptText.PaneRow(12, three, 2));
        Assert.Equal(2, SavedPromptText.NumberWidth(12));
        Assert.Equal(1, SavedPromptText.NumberWidth(0));
        Assert.Equal("4. Review the diff +2 lines", SavedPromptText.PrintedRow(4, three));

        string row = SavedPromptText.PaneRow(1, new SavedPrompt("c", new string('w', 300), Now), 1);
        Assert.True(NeonSidekick.UI.TextCells.Width(row) <= SavedPromptText.RowWidth + 3);   // cut to the row's width, with an ellipsis
        Assert.EndsWith("…", row);
    }

    [Fact]
    public void Footer_KeepsLinesForAPromptOfSeveral_AndSaysWhenAndHowLong()
    {
        var one = SavedPromptText.Footer(new SavedPrompt("a", "Summarize this.", Now), TimeZoneInfo.Utc);
        Assert.False(one.KeepLines);
        Assert.Equal("Summarize this.", one.Text);
        Assert.Contains("1 line", one.Last);

        var three = SavedPromptText.Footer(new SavedPrompt("b", "One\nTwo\nThree", Now), TimeZoneInfo.Utc);
        Assert.True(three.KeepLines);
        Assert.Contains("3 lines", three.Last);
        Assert.Contains(NeonSidekick.Sessions.SessionText.Moment(Now, TimeZoneInfo.Utc), three.Last);
    }

    [Fact]
    public void Matches_TheTextHoldsTheFilter()
    {
        var prompt = new SavedPrompt("a", "Review the diff\nfor bugs", Now);
        Assert.True(SavedPromptText.Matches("", prompt));
        Assert.True(SavedPromptText.Matches("BUGS", prompt));
        Assert.False(SavedPromptText.Matches("style", prompt));
    }
}
