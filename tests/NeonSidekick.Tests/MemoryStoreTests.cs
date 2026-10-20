using NeonSidekick.Diagnostics;
using NeonSidekick.Memory;

namespace NeonSidekick.Tests;

public class MemoryStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string FilePath => NeonSidekick.Settings.Profiles.ConfigPath(_dir, MemoryStore.FileName);   // <profile>\configs since 2026-10-16

    [Fact]
    public void Add_NormalisesAndWritesTheFile_AndANewStoreReadsItBack()
    {
        var store = new MemoryStore(_dir);
        Assert.Equal(0, store.Count);
        Assert.False(Directory.Exists(_dir));

        var result = store.Add("  Their name\n  is   Chris. ");

        Assert.Equal(new MemoryAddResult(MemoryAddOutcome.Added, "Their name is Chris."), result);
        Assert.Equal(FilePath, store.FilePath);
        Assert.True(File.Exists(FilePath));
        string json = File.ReadAllText(FilePath);
        Assert.Contains("\"SchemaVersion\": 1", json);
        Assert.Contains("\"Text\": \"Their name is Chris.\"", json);
        Assert.Contains("\"SavedAt\": \"", json);
        Assert.Empty(Directory.GetFiles(NeonSidekick.Settings.Profiles.ConfigsDirectory(_dir), "*.tmp"));

        var reloaded = new MemoryStore(_dir);
        Assert.Equal(new[] { "Their name is Chris." }, reloaded.Snapshot());
        Assert.Equal(1, reloaded.Count);
    }

    [Fact]
    public void EveryChange_IsLogged_WithoutTheMemorysText()
    {
        // 2026-10-19 (the code review): the log is shareable, so a change's line says what happened and never the text.
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        Action<DiagnosticEvent> capture = e => { if (e.Category == "Memory") lines.Enqueue(e.Message); };
        DiagnosticLog.Emitted += capture;
        try
        {
            var store = new MemoryStore(_dir);
            store.Add("my bank PIN is 1234");
            store.SetDisabled("my bank PIN is 1234", true);
            store.Add("my bank PIN is 1234", enableDisabled: true);
            store.Edit("my bank PIN is 1234", "my locker code is 5678");
            store.Remove("my locker code is 5678");
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        Assert.Equal(
            ["Remembered a memory (19 characters).", "Disabled a memory (19 characters).", "Enabled a memory (19 characters).", "Edited a memory (22 characters).", "Removed a memory (22 characters)."],
            lines.Where(l => l.Contains(" a memory (", StringComparison.Ordinal)));
        Assert.DoesNotContain(lines, l => l.Contains("1234", StringComparison.Ordinal) || l.Contains("5678", StringComparison.Ordinal));
    }

    [Fact]
    public void Add_KeepsTheOrder_OldestFirst()
    {
        var store = new MemoryStore(_dir);
        store.Add("one");
        store.Add("two");
        store.Add("three");

        Assert.Equal(new[] { "one", "two", "three" }, store.Snapshot());
        Assert.Equal(new[] { "one", "two", "three" }, new MemoryStore(_dir).Snapshot());
    }

    [Fact]
    public void Add_Duplicate_IgnoresCaseAndWhitespace_AndChangesNothing()
    {
        var store = new MemoryStore(_dir);
        store.Add("They like tea.");
        var before = File.GetLastWriteTimeUtc(FilePath);

        var result = store.Add("  they LIKE   tea. ");

        Assert.Equal(new MemoryAddResult(MemoryAddOutcome.Duplicate, "They like tea."), result);
        Assert.Equal(1, store.Count);
        Assert.Equal(before, File.GetLastWriteTimeUtc(FilePath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\t ")]
    public void Add_Empty_WritesNothing(string? text)
    {
        var store = new MemoryStore(_dir);

        Assert.Equal(MemoryAddOutcome.Empty, store.Add(text).Outcome);

        Assert.Equal(0, store.Count);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void Add_AtTheCap_IsFull_AndChangesNothing()
    {
        var store = new MemoryStore(_dir);
        for (int i = 0; i < MemoryStore.MaxEntries; i++)
        {
            Assert.Equal(MemoryAddOutcome.Added, store.Add("fact " + i).Outcome);
        }

        var result = store.Add("one more");

        Assert.Equal(new MemoryAddResult(MemoryAddOutcome.Full, "one more"), result);
        Assert.Equal(MemoryStore.MaxEntries, store.Count);
        Assert.Equal(MemoryStore.MaxEntries, new MemoryStore(_dir).Count);
    }

    /// <summary>
    /// A disabled memory's text added again (2026-10-13, the code review): the model's add is told so and changes nothing — never
    /// "already remembered" of a fact it cannot see —, the user's own add enables it again.
    /// </summary>
    [Fact]
    public void Add_OfADisabledMemory_IsDisabledForTheModel_EnabledForTheUser()
    {
        var store = new MemoryStore(_dir);
        store.Add("Prefers metric units.");
        Assert.True(store.SetDisabled("Prefers metric units.", disabled: true));

        Assert.Equal(new MemoryAddResult(MemoryAddOutcome.Disabled, "Prefers metric units."), store.Add("prefers  metric units."));
        Assert.Empty(store.Snapshot());
        Assert.StartsWith("not saved: the user has disabled this memory", NeonSidekick.Llm.Tools.SaveMemoryTool.Describe(store.Add("Prefers metric units.")), StringComparison.Ordinal);

        Assert.Equal(new MemoryAddResult(MemoryAddOutcome.Enabled, "Prefers metric units."), store.Add("Prefers metric units.", enableDisabled: true));
        Assert.Equal(["Prefers metric units."], new MemoryStore(_dir).Snapshot());
        Assert.Equal(1, store.Count);
    }

    /// <summary>The cap is the prompt's (2026-10-13, the code review): a disabled memory does not count toward it.</summary>
    [Fact]
    public void Add_TheCap_CountsTheEnabledAlone()
    {
        var store = new MemoryStore(_dir);
        for (int i = 0; i < MemoryStore.MaxEntries; i++)
        {
            store.Add("fact " + i);
        }

        Assert.True(store.SetDisabled("fact 0", disabled: true));

        Assert.Equal(MemoryAddOutcome.Added, store.Add("one more").Outcome);
        Assert.Equal(MemoryStore.MaxEntries, store.Snapshot().Count);
        Assert.Equal(MemoryAddOutcome.Full, store.Add("fact 0", enableDisabled: true).Outcome);   // enabling it again would pass the cap
    }

    [Fact]
    public void Normalize_IsPinned()
    {
        Assert.Equal("", MemoryStore.Normalize(null));
        Assert.Equal("", MemoryStore.Normalize("  \r\n "));
        Assert.Equal("a b c", MemoryStore.Normalize("  a \r\n\n b\t\tc  "));
        Assert.Equal("- not a list", MemoryStore.Normalize("- not a list"));

        string longText = new string('x', MemoryStore.MaxTextLength + 50);
        string cut = MemoryStore.Normalize(longText);
        Assert.Equal(MemoryStore.MaxTextLength, cut.Length);
        Assert.EndsWith("…", cut);
        Assert.Equal(new string('x', MemoryStore.MaxTextLength), MemoryStore.Normalize(new string('x', MemoryStore.MaxTextLength)));
    }

    [Fact]
    public void Clear_DeletesTheFile_AndReturnsHowManyItHeld()
    {
        var store = new MemoryStore(_dir);
        store.Add("a");
        store.Add("b");

        Assert.Equal(2, store.Clear());

        Assert.False(File.Exists(FilePath));
        Assert.Equal(0, store.Count);
        Assert.Empty(store.Snapshot());
        Assert.Equal(0, new MemoryStore(_dir).Count);
    }

    [Fact]
    public void Clear_WithNothingStored_ReturnsZero_AndCreatesNothing()
    {
        var store = new MemoryStore(_dir);

        Assert.Equal(0, store.Clear());

        Assert.False(Directory.Exists(_dir));
    }

    [WindowsFact]
    public void Clear_WhenTheFileIsLocked_Throws_AndKeepsTheEntries()
    {
        var store = new MemoryStore(_dir);
        store.Add("a");
        using (File.Open(FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => store.Clear());
        }

        Assert.Equal(1, store.Count);
        Assert.True(File.Exists(FilePath));
    }

    [Fact]
    public void CorruptFile_IsTreatedAsEmpty_WithOneWarning_AndTheNextAddReplacesIt()
    {
        Directory.CreateDirectory(NeonSidekick.Settings.Profiles.ConfigsDirectory(_dir));
        File.WriteAllText(FilePath, "{ not json");
        var warnings = new List<string>();
        Action<DiagnosticEvent> capture = e => { if (e.Category == "Memory" && e.Level == DiagnosticLevel.Warning) warnings.Add(e.Message); };
        DiagnosticLog.Emitted += capture;
        try
        {
            var store = new MemoryStore(_dir);
            Assert.Equal(0, store.Count);
            Assert.Empty(store.Snapshot());
            Assert.Equal(MemoryAddOutcome.Added, store.Add("fresh").Outcome);
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        string warning = Assert.Single(warnings);
        Assert.Contains(MemoryStore.FileName, warning);
        Assert.Equal(new[] { "fresh" }, new MemoryStore(_dir).Snapshot());
    }

    [Fact]
    public void AFileChangedOutside_IsReadBack_AndTheNextAddBuildsOnIt()
    {
        // 2026-09-23, /memory edit: the user's editor rewrites the file behind a loaded store.
        var store = new MemoryStore(_dir);
        store.Add("one");
        store.Add("two");
        File.WriteAllText(FilePath, "{ \"SchemaVersion\": 1, \"Entries\": [ { \"Text\": \"edited  by\\nhand\" } ] }");

        Assert.Equal(new[] { "edited by hand" }, store.Snapshot());
        Assert.Equal(MemoryAddOutcome.Added, store.Add("three").Outcome);
        Assert.Equal(new[] { "edited by hand", "three" }, new MemoryStore(_dir).Snapshot());

        File.Delete(FilePath);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void AChangedFileThatWillNotParse_KeepsTheLastGoodList_WithOneWarning()
    {
        var store = new MemoryStore(_dir);
        store.Add("keep me");
        File.WriteAllText(FilePath, "{ \"Entries\": [ oops");
        var warnings = new List<string>();
        Action<DiagnosticEvent> capture = e => { if (e.Category == "Memory" && e.Level == DiagnosticLevel.Warning) warnings.Add(e.Message); };
        DiagnosticLog.Emitted += capture;
        try
        {
            Assert.Equal(new[] { "keep me" }, store.Snapshot());
            Assert.Equal(1, store.Count);   // the same bad file: not warned twice
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        Assert.Contains(MemoryStore.FileName, Assert.Single(warnings));
    }

    [Fact]
    public void EnsureFile_WritesAnEmptyFileOnce_AndKeepsOneThatIsThere()
    {
        var store = new MemoryStore(_dir);

        Assert.False(store.EnsureFile());
        Assert.True(File.Exists(FilePath));
        Assert.Contains("\"Entries\": []", File.ReadAllText(FilePath));
        Assert.True(store.EnsureFile());

        store.Add("a");
        Assert.True(store.EnsureFile());
        Assert.Equal(new[] { "a" }, new MemoryStore(_dir).Snapshot());
    }

    [Fact]
    public void OldFile_WithBlankOrUntidyEntries_IsCleanedOnLoad()
    {
        Directory.CreateDirectory(NeonSidekick.Settings.Profiles.ConfigsDirectory(_dir));
        File.WriteAllText(FilePath, "{ \"SchemaVersion\": 1, \"Entries\": [ { \"Text\": \"  two\\nlines \" }, { \"Text\": \"   \" }, { \"Text\": \"ok\", \"SavedAt\": \"2026-09-11T10:00:00+00:00\" } ] }");

        Assert.Equal(new[] { "two lines", "ok" }, new MemoryStore(_dir).Snapshot());
    }

    [Fact]
    public void EntriesSnapshot_AreCopies_OldestFirst_WithADate()
    {
        var store = new MemoryStore(_dir);
        var before = DateTimeOffset.UtcNow.AddMinutes(-1);
        store.Add("one");
        store.Add("two");

        var entries = store.EntriesSnapshot();

        Assert.Equal(new[] { "one", "two" }, entries.Select(e => e.Text));
        Assert.All(entries, e => Assert.True(e.SavedAt >= before));
        entries[0].Text = "mutated";
        Assert.Equal(new[] { "one", "two" }, store.Snapshot());
    }

    [Fact]
    public void Remove_RewritesTheFile_AndANewStoreAgrees()
    {
        var store = new MemoryStore(_dir);
        store.Add("one");
        store.Add("two");
        store.Add("three");

        Assert.True(store.Remove("two"));

        Assert.Equal(new[] { "one", "three" }, store.Snapshot());
        Assert.Equal(new[] { "one", "three" }, new MemoryStore(_dir).Snapshot());
        Assert.Empty(Directory.GetFiles(NeonSidekick.Settings.Profiles.ConfigsDirectory(_dir), "*.tmp"));
    }

    [Fact]
    public void Remove_MatchesLikeADuplicate_IgnoringCaseAndWhitespace()
    {
        var store = new MemoryStore(_dir);
        store.Add("They like tea.");

        Assert.True(store.Remove("  they LIKE   tea. "));

        Assert.Equal(0, store.Count);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("")]
    [InlineData(null)]
    public void Remove_NotFound_ReturnsFalse_AndWritesNothing(string? text)
    {
        var store = new MemoryStore(_dir);
        store.Add("one");
        var before = File.GetLastWriteTimeUtc(FilePath);

        Assert.False(store.Remove(text));

        Assert.Equal(1, store.Count);
        Assert.Equal(before, File.GetLastWriteTimeUtc(FilePath));
    }

    [WindowsFact]
    public void Remove_WhenTheFileIsLocked_Throws_AndKeepsTheEntryInPlace()
    {
        var store = new MemoryStore(_dir);
        store.Add("one");
        store.Add("two");
        store.Add("three");
        using (File.Open(FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // A move over a locked file surfaces as UnauthorizedAccessException on Windows; a delete as IOException.
            var ex = Record.Exception(() => store.Remove("two"));
            Assert.True(ex is IOException or UnauthorizedAccessException, ex?.GetType().Name ?? "no exception");
        }

        Assert.Equal(new[] { "one", "two", "three" }, store.Snapshot());
        Assert.Equal(new[] { "one", "two", "three" }, new MemoryStore(_dir).Snapshot());
        Assert.Empty(Directory.GetFiles(NeonSidekick.Settings.Profiles.ConfigsDirectory(_dir), "*.tmp"));
    }

    // ── Import (/memory copy, 2026-09-17 as /memcopy) ───────────────────────

    private static MemoryEntry Entry(string text, int day = 1) => new() { Text = text, SavedAt = new DateTimeOffset(2026, 9, day, 0, 0, 0, TimeSpan.Zero) };

    [Fact]
    public void Import_AppendsAfterWhatIsThere_SkipsTheDuplicates_KeepsTheSourceDates()
    {
        var store = new MemoryStore(_dir);
        store.Add("Likes tea.");
        store.Add("Lives in Oslo.");

        var result = store.Import([Entry("  likes   TEA. ", 3), Entry("Has a dog.", 4), Entry("has a DOG.", 5), Entry("Plays chess.", 6), Entry("   ")], overwrite: false);

        // Two duplicates: one against the target, one against an earlier entry of the same batch; the blank is nothing.
        Assert.Equal(new MemoryImportResult(Added: 2, Duplicates: 2, Dropped: 0), result);
        Assert.Equal(new[] { "Likes tea.", "Lives in Oslo.", "Has a dog.", "Plays chess." }, store.Snapshot());
        var reloaded = new MemoryStore(_dir).EntriesSnapshot();
        Assert.Equal(new[] { "Likes tea.", "Lives in Oslo.", "Has a dog.", "Plays chess." }, reloaded.Select(e => e.Text));
        Assert.Equal(new DateTimeOffset(2026, 9, 4, 0, 0, 0, TimeSpan.Zero), reloaded[2].SavedAt);
        Assert.Empty(Directory.GetFiles(NeonSidekick.Settings.Profiles.ConfigsDirectory(_dir), "*.tmp"));
    }

    [Fact]
    public void Import_Overwrite_ReplacesTheList_EvenWithNothing()
    {
        var store = new MemoryStore(_dir);
        store.Add("Likes tea.");

        Assert.Equal(new MemoryImportResult(2, 0, 0), store.Import([Entry("Has a dog."), Entry("Plays chess.")], overwrite: true));
        Assert.Equal(new[] { "Has a dog.", "Plays chess." }, new MemoryStore(_dir).Snapshot());

        Assert.Equal(new MemoryImportResult(0, 0, 0), store.Import([], overwrite: true));
        Assert.Equal(0, store.Count);
        Assert.Empty(new MemoryStore(_dir).Snapshot());
        Assert.True(File.Exists(FilePath));
    }

    [Fact]
    public void Import_DropsWhatDoesNotFit_UnderTheCap()
    {
        var store = new MemoryStore(_dir);
        for (int i = 0; i < MemoryStore.MaxEntries - 1; i++)
        {
            store.Add("memory " + i);
        }

        var result = store.Import([Entry("memory 0"), Entry("one more"), Entry("too many"), Entry("and another")], overwrite: false);

        Assert.Equal(new MemoryImportResult(Added: 1, Duplicates: 1, Dropped: 2), result);
        Assert.Equal(MemoryStore.MaxEntries, store.Count);
        Assert.Equal("one more", store.Snapshot()[^1]);
    }

    [WindowsFact]
    public void Import_WhenTheFileIsLocked_Throws_AndKeepsTheEntries()
    {
        var store = new MemoryStore(_dir);
        store.Add("a");
        using (File.Open(FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // File.Move over a locked file: UnauthorizedAccessException on Windows; the handler catches both.
            var ex = Record.Exception(() => store.Import([Entry("b")], overwrite: true));
            Assert.True(ex is IOException or UnauthorizedAccessException, ex?.GetType().Name);
        }

        Assert.Equal(new[] { "a" }, store.Snapshot());
        Assert.Equal(new[] { "a" }, new MemoryStore(_dir).Snapshot());
    }

    [Fact]
    public void Constants_ArePinned()
    {
        Assert.Equal("memory.json", MemoryStore.FileName);
        Assert.Equal(200, MemoryStore.MaxEntries);
        Assert.Equal(300, MemoryStore.MaxTextLength);
    }

    /// <summary>
    /// Disable (2026-10-13, the user's ask): kept, listed, but not in <see cref="MemoryStore.Snapshot"/> — what the model is offered; the
    /// mark on disk only while set; enable brings it back; a second disable or a missing text changes nothing.
    /// </summary>
    [Fact]
    public void SetDisabled_HidesTheMemoryFromTheModel_KeepsItListed_AndWritesTheMarkOnlyWhenSet()
    {
        var store = new MemoryStore(_dir);
        store.Add("one");
        store.Add("two");
        Assert.DoesNotContain("Disabled", File.ReadAllText(FilePath));   // a file without disabled memories reads as it always did

        Assert.True(store.SetDisabled("TWO", true));
        Assert.False(store.SetDisabled("two", true));
        Assert.False(store.SetDisabled("three", true));
        Assert.Equal(["one"], store.Snapshot());
        Assert.Equal(2, store.Count);
        Assert.True(store.EntriesSnapshot()[1].Disabled);
        Assert.Contains("\"Disabled\": true", File.ReadAllText(FilePath));
        Assert.Equal(["one"], new MemoryStore(_dir).Snapshot());

        Assert.True(store.SetDisabled("two", false));
        Assert.Equal(["one", "two"], store.Snapshot());
        Assert.DoesNotContain("Disabled", File.ReadAllText(FilePath));
    }

    /// <summary>Edit (2026-10-13, the user's ask): the text normalised, its date and mark kept; a duplicate, an empty text or a missing one change nothing.</summary>
    [Fact]
    public void Edit_ChangesTheText_KeepsTheDateAndTheMark_AndRefusesADuplicate()
    {
        var store = new MemoryStore(_dir);
        store.Add("one");
        store.Add("two");
        store.SetDisabled("two", true);
        var saved = store.EntriesSnapshot()[1].SavedAt;

        Assert.Equal(new MemoryEditResult(MemoryEditOutcome.Edited, "two too"), store.Edit("two", "  two\n too "));
        Assert.Equal("two too", store.EntriesSnapshot()[1].Text);
        Assert.Equal(saved, store.EntriesSnapshot()[1].SavedAt);
        Assert.True(store.EntriesSnapshot()[1].Disabled);
        Assert.Equal(new MemoryEditResult(MemoryEditOutcome.Duplicate, "one"), store.Edit("two too", "ONE"));
        Assert.Equal(MemoryEditOutcome.Unchanged, store.Edit("one", "one").Outcome);
        Assert.Equal(MemoryEditOutcome.Empty, store.Edit("one", "   ").Outcome);
        Assert.Equal(MemoryEditOutcome.Missing, store.Edit("gone", "x").Outcome);
        Assert.Equal(["one", "two too"], new MemoryStore(_dir).EntriesSnapshot().Select(e => e.Text));
    }
}
