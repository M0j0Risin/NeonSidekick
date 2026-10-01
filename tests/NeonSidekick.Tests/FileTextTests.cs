using NeonSidekick.Files;

namespace NeonSidekick.Tests;

public class FileTextTests
{
    private static readonly DateTimeOffset When = new(2026, 9, 12, 14, 5, 0, TimeSpan.FromHours(-7));

    [Fact]
    public void Describe_NamesTheDefault()
    {
        // The path quoted, the note its own clause: a model copied the bare "… (profile default)" into list_directory (2026-09-17).
        Assert.Equal(@"The working directory is 'D:\h\profiles\default\files' (the profile's default folder); every path you pass to a file tool is relative to it.", FileText.Describe(@"D:\h\profiles\default\files", isDefault: true));
        Assert.Equal(@"The working directory is 'D:\work'; every path you pass to a file tool is relative to it.", FileText.Describe(@"D:\work", isDefault: false));
        Assert.Equal("the working directory", FileText.Name(""));
        Assert.Equal(@"docs\", FileText.Name(@"docs\"));
    }

    [Fact]
    public void Errors_ArePinned_AndStartWithError()
    {
        Assert.Equal("Error: '..\\x' is outside the working directory; every path must stay inside it", FileText.OutsideRoot(@"..\x"));
        Assert.Equal("Error: nothing is at 'a.txt'", FileText.Missing("a.txt"));
        Assert.Equal("Error: 'docs\\' is a folder, not a file", FileText.IsDirectory(@"docs\"));
        Assert.Equal("Error: 'a.txt' is a file, not a folder", FileText.IsAFile("a.txt"));
        Assert.Equal("Error: 'a.bin' is not a text file", FileText.NotText("a.bin"));
        Assert.Equal("Error: 'a.PNG' is not a text file; use view_image to look at it", FileText.NotText("a.PNG"));
        Assert.Equal("Error: 'a.png' could not be read as an image", FileText.NotAnImage("a.png"));
        Assert.Equal("Error: 'a.png' is over 20 MB or 40 megapixels; too large to view", FileText.ImageTooBig("a.png"));
        Assert.Equal("Error: 'a.txt' is not a zip archive", FileText.NotAnArchive("a.txt"));
        Assert.Equal("Error: 'a.txt' already exists; call again with overwrite true to replace it", FileText.Exists("a.txt"));
        Assert.Equal("Error: old_text was not found in 'a.txt', even with spacing, indentation, quotes and dashes matched loosely; read the file again and copy the text as it is (the line numbers an edit result shows are not part of the file)", FileText.EditNotFound("a.txt"));
        Assert.Equal("Error: old_text appears 3 times in 'a.txt'; include enough surrounding text to make it unique, or pass replace_all true", FileText.EditAmbiguous("a.txt", 3));
        // The patch_file refusals (2026-09-19): the locations named, the approximate replace_all, the escape drifts, and the done-already sentence that is no error.
        Assert.Equal(
            "Error: old_text appears 3 times in 'a.txt'; include enough surrounding text to make it unique, or pass replace_all true:\n  line 4: int x = 1;\n  line 9: " + new string('b', 79) + "…\n  … and 1 more",
            FileText.EditAmbiguous("a.txt", 3, [new MatchLocation(4, "  int x = 1;  "), new MatchLocation(9, new string('b', 90))]));
        Assert.Equal(FileText.EditAmbiguous("a.txt", 3), FileText.EditAmbiguous("a.txt", 3, []));
        Assert.EndsWith("\n  line 1: a\n  … and 2 more", FileText.EditAmbiguous("a.txt", 3, [new MatchLocation(1, "a")]), StringComparison.Ordinal);
        Assert.Equal("  … and 7 more", FileText.AmbiguousMore(7));
        Assert.Equal("Error: old_text matched 2 places in 'a.txt' only approximately; replace_all needs the text as it is in the file — copy it exactly, spacing included", FileText.ApproximateAll("a.txt", 2));
        Assert.Equal("Error: old_text and new_text hold \\' but the matched text in 'a.txt' does not; the backslash is a serialisation artefact — read the file again and pass both without it", FileText.EscapeDriftQuote("a.txt", '\''));
        Assert.Equal("Error: old_text and new_text hold \\\" but the matched text in 'a.txt' does not; the backslash is a serialisation artefact — read the file again and pass both without it", FileText.EscapeDriftQuote("a.txt", '"'));
        Assert.Equal("Error: every backslash run in old_text is twice as long as in 'a.txt'; read the file again and pass old_text and new_text with the backslashes as the file has them", FileText.EscapeDriftDoubled("a.txt"));
        Assert.Equal(FileText.EscapeDriftQuote("a.txt", '\''), FileText.EscapeDriftSentence("a.txt", nameof(EscapeDrift.QuoteSingle)));
        Assert.Equal(FileText.EscapeDriftQuote("a.txt", '"'), FileText.EscapeDriftSentence("a.txt", nameof(EscapeDrift.QuoteDouble)));
        Assert.Equal(FileText.EscapeDriftDoubled("a.txt"), FileText.EscapeDriftSentence("a.txt", nameof(EscapeDrift.DoubledBackslashes)));
        Assert.Equal("nothing changed in a.txt: it already holds new_text and old_text is not in it, so the edit appears to be applied; do not send it again", FileText.AlreadyApplied("a.txt"));
        Assert.Equal("Error: 'a.txt' already exists; call again with mode overwrite to replace it, or mode append to add to its end", FileText.WriteExists("a.txt"));
        Assert.Equal("Error: 'upsert' is not one of create, overwrite or append for 'mode'", FileText.BadChoice("mode", " upsert ", "create, overwrite or append"));
        // The strategy notes: one per non-exact strategy, nothing for an exact match.
        Assert.Equal("", FileText.StrategyNote(MatchStrategy.Exact));
        Assert.Equal(" (old_text matched with each line's leading and trailing spaces ignored)", FileText.StrategyNote(MatchStrategy.LineTrimmed));
        Assert.Equal(" (old_text matched with runs of spaces collapsed)", FileText.StrategyNote(MatchStrategy.WhitespaceNormalized));
        Assert.Equal(" (old_text matched with its indentation ignored)", FileText.StrategyNote(MatchStrategy.IndentationFlexible));
        Assert.Equal(" (old_text matched with its \\n, \\t and \\r escapes read as line breaks and tabs)", FileText.StrategyNote(MatchStrategy.EscapeNormalized));
        Assert.Equal(" (old_text matched with its first and last lines' spaces ignored)", FileText.StrategyNote(MatchStrategy.TrimmedBoundary));
        Assert.Equal(" (old_text matched with quotes, dashes and spaces read as their plain forms; the file keeps its own)", FileText.StrategyNote(MatchStrategy.UnicodeNormalized));
        Assert.Equal(" (old_text matched by its first and last lines, the lines between similar; check the result)", FileText.StrategyNote(MatchStrategy.BlockAnchor));
        Assert.Equal(" (old_text matched line by line by similarity; check the result)", FileText.StrategyNote(MatchStrategy.ContextAware));
        Assert.All(FuzzyMatch.Chain.Skip(1), s => Assert.StartsWith(" (old_text matched ", FileText.StrategyNote(s), StringComparison.Ordinal));
        Assert.Equal(new string('a', 79) + "…", FileText.Quote(new string('a', 81)));
        Assert.Equal(new string('a', 80), FileText.Quote(new string('a', 80)));
        Assert.Equal("Error: cannot put 'docs\\' inside itself ('docs\\in\\')", FileText.IntoItself(@"docs\", @"docs\in\"));
        Assert.Equal("Error: cannot put 'the working directory' inside itself ('x\\')", FileText.IntoItself("", @"x\"));
        Assert.Equal("Error: 'big.log' is too large to handle as text", FileText.TooBig("big.log"));
        Assert.Equal("Error: the content is over 200,000 characters; write it in parts", FileText.TooLong);
        Assert.Equal("Error: the regular expression is invalid: bad", FileText.BadPattern("bad"));
        Assert.Equal("Error: 'maybe' is not true or false for 'overwrite'", FileText.BadBoolean("overwrite", " maybe "));
        Assert.Equal("Error: 'e.zip' holds an entry that would land outside the destination ('../x'); nothing was extracted", FileText.ZipSlip("e.zip", "../x"));
        Assert.Equal("Error: could not write 'a.txt': locked", FileText.CouldNot("write", "a.txt", "locked"));
        Assert.Equal("Error: could not list 'the working directory': gone", FileText.CouldNot("list", "", "gone"));
        Assert.Equal("Error: from is required: the file or folder, relative to the working directory", FileText.PathRequired("from"));
        Assert.Equal(FileText.EditSame, FileText.Error(FileOutcome.Same, "x", "edit"));
    }

    [Fact]
    public void Error_MapsEveryOutcome()
    {
        Assert.Equal(FileText.OutsideRoot("p"), FileText.Error(FileOutcome.OutsideRoot, "p", "read"));
        Assert.Equal(FileText.Missing("p"), FileText.Error(FileOutcome.Missing, "p", "read"));
        Assert.Equal(FileText.IsDirectory("p"), FileText.Error(FileOutcome.IsDirectory, "p", "read"));
        Assert.Equal(FileText.IsAFile("p"), FileText.Error(FileOutcome.IsAFile, "p", "read"));
        Assert.Equal(FileText.NotText("p"), FileText.Error(FileOutcome.NotText, "p", "read"));
        Assert.Equal(FileText.NotAnArchive("p"), FileText.Error(FileOutcome.NotAnArchive, "p", "read"));
        Assert.Equal(FileText.Exists("p"), FileText.Error(FileOutcome.Exists, "p", "read"));
        Assert.Equal(FileText.EditEmpty, FileText.Error(FileOutcome.Empty, "p", "read"));
        Assert.Equal(FileText.EditNotFound("p"), FileText.Error(FileOutcome.EditNotFound, "p", "read"));
        Assert.Equal(FileText.EditAmbiguous("p", 4), FileText.Error(FileOutcome.EditAmbiguous, "p", "read", "4"));
        Assert.Equal(FileText.IntoItself("p", "q"), FileText.Error(FileOutcome.IntoItself, "p", "read", "", "q"));
        Assert.Equal(FileText.TooLong, FileText.Error(FileOutcome.TooLong, "p", "read"));
        Assert.Equal(FileText.TooBig("p"), FileText.Error(FileOutcome.TooBig, "p", "read"));
        Assert.Equal(FileText.BadPattern("d"), FileText.Error(FileOutcome.BadPattern, "p", "read", "d"));
        Assert.Equal(FileText.CouldNot("read", "p", "d"), FileText.Error(FileOutcome.Failed, "p", "read", "d"));
        Assert.Equal(FileText.NotAnImage("p"), FileText.Error(FileOutcome.NotAnImage, "p", "view"));
        Assert.Equal(FileText.ImageTooBig("p"), FileText.Error(FileOutcome.ImageTooBig, "p", "view"));
        Assert.Equal(FileText.ApproximateAll("p", 2), FileText.Error(FileOutcome.ApproximateAll, "p", "edit", "2"));
        Assert.Equal(FileText.EscapeDriftDoubled("p"), FileText.Error(FileOutcome.EscapeDrift, "p", "edit", nameof(EscapeDrift.DoubledBackslashes)));
        Assert.Equal(FileText.AlreadyApplied("p"), FileText.Error(FileOutcome.AlreadyApplied, "p", "edit"));
        Assert.Equal(FileText.FolderInTheWay("p"), FileText.Error(FileOutcome.FolderInTheWay, "p", "move"));
    }

    [Fact]
    public void Image_IsTheNameSizeAndKind_ThenWhereThePictureIs()
    {
        var image = new ImageAttachment("shots/one.png", new byte[213_400], ImageFile.Png, 1024, 768);
        Assert.Equal("shots/one.png (1024×768 image/png, 213.4 KB): the picture is in the next message", FileText.Image(new ImageResult(FileOutcome.Ok, "shots/one.png", image)));
        Assert.Equal(FileText.NotAnImage("a.txt"), FileText.Image(new ImageResult(FileOutcome.NotAnImage, "a.txt", null)));
        Assert.Equal(FileText.CouldNot("view", "a.png", "locked"), FileText.Image(new ImageResult(FileOutcome.Failed, "a.png", null, "locked")));
        Assert.Equal(FileText.Missing("a.png"), FileText.Image(new ImageResult(FileOutcome.Missing, "a.png", null)));
        Assert.Throws<ArgumentNullException>(() => FileText.Image(null!));
    }

    [Fact]
    public void Listing_HeaderThenEntries()
    {
        var entries = new List<DirectoryEntry> { new("docs", true, 0), new("a.txt", false, 1234), new("b.txt", false, 5) };
        Assert.Equal("the working directory (3 entries):\ndocs\\\na.txt  1.2 KB\nb.txt  5 B", FileText.Listing(new ListResult(FileOutcome.Ok, "", entries, false)));
        Assert.Equal("docs\\ (0 entries): (empty)", FileText.Listing(new ListResult(FileOutcome.Ok, @"docs\", [], false)));
        Assert.EndsWith("\n… only the first 3 entries are shown", FileText.Listing(new ListResult(FileOutcome.Ok, "", entries, true)));
        Assert.Equal(FileText.Missing(@"nope\"), FileText.Listing(new ListResult(FileOutcome.Missing, @"nope\", [], false)));
        Assert.Equal("Error: could not list 'the working directory': boom", FileText.Listing(new ListResult(FileOutcome.Failed, "", [], false, "boom")));
    }

    [Fact]
    public void Listing_WithDepth_NestsByLevel()
    {
        // list_directory's depth (2026-09-18): the flat rows, indented two spaces per level under the first.
        var entries = new List<FileTreeEntry> { new("a", 1, true, 0, false), new("sub", 2, true, 0, false), new("deep.txt", 3, false, 5, true), new("b.txt", 1, false, 1234, true) };
        Assert.Equal("the working directory (4 entries, 3 levels):\na\\\n  sub\\\n    deep.txt  5 B\nb.txt  1.2 KB", FileText.Listing(new FileTreeResult(FileOutcome.Ok, "", "", entries, false), 3));
        Assert.Equal("docs\\ (0 entries, 2 levels): (empty)", FileText.Listing(new FileTreeResult(FileOutcome.Ok, @"docs\", "", [], false), 2));
        Assert.EndsWith("\n… only the first 4 entries are shown", FileText.Listing(new FileTreeResult(FileOutcome.Ok, "", "", entries, true), 4));
        Assert.Equal(FileText.IsAFile("a.txt"), FileText.Listing(new FileTreeResult(FileOutcome.IsAFile, "a.txt", "", [], false), 2));
    }

    [Fact]
    public void Found_CountsAndLists()
    {
        Assert.Equal("2 files match '*.md' under the working directory:\na.md\ndocs\\b.md", FileText.Found(new FindResult(FileOutcome.Ok, "", ["a.md", @"docs\b.md"], false), "*.md"));
        Assert.Equal("1 file matches 'a.md' under docs\\:\ndocs\\a.md", FileText.Found(new FindResult(FileOutcome.Ok, @"docs\", [@"docs\a.md"], false), "a.md"));
        Assert.Equal("0 files match 'z*' under the working directory: (none)", FileText.Found(new FindResult(FileOutcome.Ok, "", [], false), "z*"));
        Assert.EndsWith("… only the first 1 matches are shown", FileText.Found(new FindResult(FileOutcome.Ok, "", ["a"], true), "*"));
    }

    [Fact]
    public void SearchHits_HeaderHitsAndTails()
    {
        var hits = new List<SearchHit> { new("a.txt", 12, "the line"), new(@"docs\b.txt", 3, "another") };
        var result = new SearchResult(FileOutcome.Ok, "", hits, 140, 2, 0, false, TimeSpan.FromMilliseconds(80));
        Assert.Equal(
            "2 matches for 'needle' in 2 files (searched 140 files under the working directory in 0.08 s):\na.txt:12: the line\ndocs\\b.txt:3: another",
            FileText.SearchHits(result, "needle"));
        Assert.Equal(
            "0 matches for 'x' in 0 files (searched 1 file under docs\\ in 0.00 s): (no matches)",
            FileText.SearchHits(new SearchResult(FileOutcome.Ok, @"docs\", [], 1, 0, 0, false, TimeSpan.Zero), "x"));
        string truncated = FileText.SearchHits(result with { Truncated = true, TimedOut = 2 }, "needle");
        Assert.Contains("\n… only the first 2 matches are shown; narrow the search or the folder", truncated);   // the list's own count (2026-09-19): a cut list holds exactly the limit
        Assert.Equal("… only the first 50 matches are shown", FileText.OnlyFirst(50, "matches"));
        Assert.EndsWith("\n2 lines skipped: the expression took too long on them", truncated);
        // One file as the path (2026-09-18): the header names it, no file counts.
        Assert.Equal(
            "1 match for 'needle' in docs\\b.txt (0.08 s):\ndocs\\b.txt:3: another",
            FileText.SearchHits(new SearchResult(FileOutcome.Ok, @"docs\b.txt", [hits[1]], 1, 1, 0, false, TimeSpan.FromMilliseconds(80), SingleFile: true), "needle"));
        Assert.Equal(FileText.BadPattern("oops"), FileText.SearchHits(result with { Outcome = FileOutcome.BadPattern, Detail = "oops" }, "("));

        // output files (2026-09-19): one row per file with its count, the cut tail counting files.
        var counted = new SearchResult(FileOutcome.Ok, "", [], 140, 2, 0, false, TimeSpan.FromMilliseconds(80), Files: [new SearchFileCount("a.txt", 1), new SearchFileCount(@"docs\b.txt", 12)]);
        Assert.Equal(
            "2 files hold 'needle' (searched 140 files under the working directory in 0.08 s):\na.txt  1 match\ndocs\\b.txt  12 matches",
            FileText.SearchFiles(counted, "needle"));
        Assert.Equal("0 files hold 'x' (searched 1 file under docs\\ in 0.00 s): (no matches)", FileText.SearchFiles(new SearchResult(FileOutcome.Ok, @"docs\", [], 1, 0, 0, false, TimeSpan.Zero), "x"));
        Assert.EndsWith("\n… only the first 2 files are shown; narrow the search or the folder\n2 lines skipped: the expression took too long on them", FileText.SearchFiles(counted with { Truncated = true, TimedOut = 2 }, "needle"), StringComparison.Ordinal);
        Assert.Equal("1 file holds 'needle' in docs\\b.txt (0.08 s):\ndocs\\b.txt  12 matches", FileText.SearchFiles(new SearchResult(FileOutcome.Ok, @"docs\b.txt", [], 1, 1, 0, false, TimeSpan.FromMilliseconds(80), SingleFile: true, Files: [new SearchFileCount(@"docs\b.txt", 12)]), "needle"));
        Assert.Equal(FileText.BadPattern("oops"), FileText.SearchFiles(counted with { Outcome = FileOutcome.BadPattern, Detail = "oops" }, "("));

        // With context (2026-09-17): grep's shape — `file-11- text` around `file:12: text`, `--` between files, none between hits of one file.
        var withContext = new List<SearchHit>
        {
            new("a.txt", 12, "the line", ["before one", "before two"], ["after one"]),
            new("a.txt", 30, "again", ["b"], ["c", "d"]),
            new(@"docs\b.txt", 1, "another", [], ["next"]),
        };
        Assert.Equal(
            "3 matches for 'needle' in 2 files (searched 140 files under the working directory in 0.08 s):\n" +
            "a.txt-10- before one\na.txt-11- before two\na.txt:12: the line\na.txt-13- after one\n" +
            "a.txt-29- b\na.txt:30: again\na.txt-31- c\na.txt-32- d\n" +
            "--\ndocs\\b.txt:1: another\ndocs\\b.txt-2- next",
            FileText.SearchHits(new SearchResult(FileOutcome.Ok, "", withContext, 140, 2, 0, false, TimeSpan.FromMilliseconds(80)), "needle"));
    }

    [Fact]
    public void Recent_Info_AndRead()
    {
        var recent = new RecentResult(FileOutcome.Ok, "", [new("notes.txt", When, 1234)]);
        Assert.Equal("most recently changed under the working directory, newest first:\nnotes.txt  2026-09-12 14:05  1.2 KB", FileText.Recent(recent));
        Assert.Equal("most recently changed under docs\\, newest first: (no files)", FileText.Recent(new RecentResult(FileOutcome.Ok, @"docs\", [])));

        Assert.Equal(
            "docs\\notes.txt — 1,234 bytes, 48 lines, 310 words, modified 2026-09-12 14:05",
            FileText.Info(new InfoResult(FileOutcome.Ok, @"docs\notes.txt", false, 1234, When, 48, 310, 0, 0, false)));
        Assert.Equal(
            "a.bin — 1 byte, modified 2026-09-12 14:05",
            FileText.Info(new InfoResult(FileOutcome.Ok, "a.bin", false, 1, When, null, null, 0, 0, false)));
        Assert.Equal(
            "docs\\ — 12 files in 3 folders, 48.2 KB, last modified 2026-09-12 14:05",
            FileText.Info(new InfoResult(FileOutcome.Ok, @"docs\", true, 48_200, When, null, null, 12, 3, false)));
        Assert.EndsWith(" (counted the first 10,000 entries only)", FileText.Info(new InfoResult(FileOutcome.Ok, "", true, 1, When, null, null, 1, 1, true)));
        Assert.Equal(FileText.Missing("x"), FileText.Info(new InfoResult(FileOutcome.Missing, "x", false, 0, default, null, null, 0, 0, false)));

        Assert.Equal(
            "docs\\notes.txt — 1,234 bytes, 48 lines, 310 words, CRLF, UTF-8 BOM, modified 2026-09-12 14:05",
            FileText.Info(new InfoResult(FileOutcome.Ok, @"docs\notes.txt", false, 1234, When, 48, 310, 0, 0, false, "CRLF", true)));
        Assert.Equal(
            "docs\\notes.txt — 1,234 bytes, 48 lines, 310 words, mixed, modified 2026-09-12 14:05",
            FileText.Info(new InfoResult(FileOutcome.Ok, @"docs\notes.txt", false, 1234, When, 48, 310, 0, 0, false, "mixed", false)));

        Assert.Equal("notes.txt (48 lines):\nl1\nl2", FileText.Read(new ReadResult(FileOutcome.Ok, "notes.txt", "l1\nl2", 48, 1, 48, false)));
        // A read is never numbered (2026-09-19; a numbered read was an option from 2026-09-17): a window keeps its text bare too.
        Assert.Equal("notes.txt (lines 9–11 of 48; next: start_line 12):\nl9\n\nl11", FileText.Read(new ReadResult(FileOutcome.Ok, "notes.txt", "l9\n\nl11", 48, 9, 11, false)));
        // Numbered (2026-09-17, an edit result's region): the gutter right-aligned to the last line's width, `: ` then the line; a blank line keeps its number.
        Assert.Equal(" 9: l9\n10: \n11: l11", FileText.Numbered(["l9", "", "l11"], 9, 11));
        Assert.Equal("  9: a\n 10: b\n 11: c", FileText.Numbered(["a", "b", "c"], 9, 100));   // the width follows the widest number named
        Assert.Equal("", FileText.Numbered([], 1, 1));
        Assert.Equal("notes.txt (lines 1–40 of 48; next: start_line 41):", FileText.ReadHeader(new ReadResult(FileOutcome.Ok, "notes.txt", "x", 48, 1, 40, false)));
        Assert.Equal("; next: start_line 41", FileText.NextHint(41));
        Assert.Equal("notes.txt (lines 29–48 of 48):", FileText.ReadHeader(new ReadResult(FileOutcome.Ok, "notes.txt", "x", 48, 29, 48, false)));
        Assert.Equal("notes.txt (lines 1–30 of 48; cut at 32,000 characters; next: start_line 31):", FileText.ReadHeader(new ReadResult(FileOutcome.Ok, "notes.txt", "x", 48, 1, 30, true)));
        Assert.Equal("empty.txt (empty)", FileText.Read(new ReadResult(FileOutcome.Ok, "empty.txt", "", 0, 0, 0, false)));
        Assert.Equal("notes.txt has only 5 lines; nothing from line 9", FileText.Read(new ReadResult(FileOutcome.Ok, "notes.txt", "", 5, 9, 8, false)));
        Assert.Equal(FileText.NotText("a.bin"), FileText.Read(new ReadResult(FileOutcome.NotText, "a.bin", "", 0, 0, 0, false)));
    }

    [Fact]
    public void WriteSide_Sentences()
    {
        Assert.Equal("wrote notes.txt (1,234 bytes)", FileText.Wrote(new WriteResult(FileOutcome.Ok, "notes.txt", 1234, false)));
        // The counts (2026-09-18) when the write knows them; bytes and a file too big report none.
        Assert.Equal("wrote notes.txt (1,234 bytes, 1 line, 200 words)", FileText.Wrote(new WriteResult(FileOutcome.Ok, "notes.txt", 1234, false, Lines: 1, Words: 200)));
        Assert.Equal(", 2 lines, 1,000 words", FileText.Counts(2, 1000));
        Assert.Equal("", FileText.Counts(2, null));
        Assert.Equal("replaced notes.txt (1 byte)", FileText.Wrote(new WriteResult(FileOutcome.Ok, "notes.txt", 1, true)));
        Assert.Equal(FileText.WriteExists("notes.txt"), FileText.Wrote(new WriteResult(FileOutcome.Exists, "notes.txt", 0, false)));   // the mode form, not the overwrite one
        Assert.Equal("Error: could not write 'notes.txt': locked", FileText.Wrote(new WriteResult(FileOutcome.Failed, "notes.txt", 0, false, Detail: "locked")));

        Assert.Equal("appended 40 bytes to log.txt", FileText.Appended(new WriteResult(FileOutcome.Ok, "log.txt", 40, true)));
        Assert.Equal("appended 40 bytes to log.txt (now 12 lines, 90 words)", FileText.Appended(new WriteResult(FileOutcome.Ok, "log.txt", 40, true, Lines: 12, Words: 90)));
        Assert.Equal("created log.txt (40 bytes, 1 line, 7 words)", FileText.Appended(new WriteResult(FileOutcome.Ok, "log.txt", 40, false, Lines: 1, Words: 7)));
        Assert.Equal("created log.txt (40 bytes)", FileText.Appended(new WriteResult(FileOutcome.Ok, "log.txt", 40, false)));
        Assert.Equal("Error: could not append to 'log.txt': x", FileText.Appended(new WriteResult(FileOutcome.Failed, "log.txt", 0, false, Detail: "x")));

        Assert.Equal("replaced notes.txt (1 byte)", FileText.Wrote(new WriteResult(FileOutcome.Ok, "notes.txt", 1, true)));   // no kept-copy suffix since 2026-10-01 (File safe edits gone)

        // An edit (2026-09-17): the new lines' range, then the region numbered.
        Assert.Equal("edited notes.txt (line 12; now 40 lines, 0 words)", FileText.Edited(new EditResult(FileOutcome.Ok, "notes.txt", 12, 1, 12, 12, 40)));
        Assert.Equal("edited notes.txt (line 12; now 40 lines, 300 words)" + FileText.IndentNote, FileText.Edited(new EditResult(FileOutcome.Ok, "notes.txt", 12, 1, 12, 12, 40, Words: 300, Strategy: MatchStrategy.IndentationFlexible)));
        Assert.Equal("edited notes.txt (line 12; now 40 lines, 300 words)" + FileText.ContextNote, FileText.Edited(new EditResult(FileOutcome.Ok, "notes.txt", 12, 1, 12, 12, 40, Words: 300, Strategy: MatchStrategy.ContextAware)));
        Assert.Equal(
            "edited notes.txt (line 12; now 40 lines, 0 words):\n10: a\n11: b\n12: NEW\n13: d\n14: e",
            FileText.Edited(new EditResult(FileOutcome.Ok, "notes.txt", 12, 1, 12, 12, 40, ["a", "b", "NEW", "d", "e"], 10)));
        Assert.Equal(
            "edited notes.txt (lines 12–14; now 40 lines, 0 words):\n11: b\n12: x\n13: y\n14: z\n15: d",
            FileText.Edited(new EditResult(FileOutcome.Ok, "notes.txt", 12, 1, 12, 14, 40, ["b", "x", "y", "z", "d"], 11)));
        Assert.Equal(
            "edited notes.txt (removed at line 12; now 12 lines, 0 words):\n10: a\n11: b\n12: d",
            FileText.Edited(new EditResult(FileOutcome.Ok, "notes.txt", 12, 1, 12, 11, 12, ["a", "b", "d"], 10)));
        Assert.Equal("edited notes.txt (lines 12–89; now 100 lines, 0 words); read_file to see the lines", FileText.Edited(new EditResult(FileOutcome.Ok, "notes.txt", 12, 1, 12, 89, 100)));
        Assert.Equal("replaced 3 occurrences of old_text in notes.txt (lines 3, 9, 14; now 40 lines, 0 words)", FileText.Edited(new EditResult(FileOutcome.Ok, "notes.txt", 3, 3, 0, 0, 40, null, 0, [3, 9, 14])));
        Assert.Equal(
            "replaced 12 occurrences of old_text in notes.txt (lines 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, …; now 40 lines, 0 words)",
            FileText.Edited(new EditResult(FileOutcome.Ok, "notes.txt", 1, 12, 0, 0, 40, null, 0, Enumerable.Range(1, 12).ToList())));
        Assert.Equal(FileText.EditAmbiguous("notes.txt", 3), FileText.Edited(new EditResult(FileOutcome.EditAmbiguous, "notes.txt", 0, 3)));
        Assert.Equal(FileText.EditAmbiguous("notes.txt", 3, [new MatchLocation(4, "x")]), FileText.Edited(new EditResult(FileOutcome.EditAmbiguous, "notes.txt", 0, 3, Locations: [new MatchLocation(4, "x")])));
        Assert.Equal(FileText.ApproximateAll("notes.txt", 2), FileText.Edited(new EditResult(FileOutcome.ApproximateAll, "notes.txt", 0, 2)));
        Assert.Equal(FileText.EscapeDriftQuote("notes.txt", '\''), FileText.Edited(new EditResult(FileOutcome.EscapeDrift, "notes.txt", 0, 1, Drift: EscapeDrift.QuoteSingle)));
        Assert.Equal(FileText.EscapeDriftQuote("notes.txt", '"'), FileText.Edited(new EditResult(FileOutcome.EscapeDrift, "notes.txt", 0, 1, Drift: EscapeDrift.QuoteDouble)));
        Assert.Equal(FileText.AlreadyApplied("notes.txt"), FileText.Edited(new EditResult(FileOutcome.AlreadyApplied, "notes.txt", 0, 0)));
        Assert.Equal("replaced 3 occurrences of old_text in notes.txt (lines 3, 9, 14; now 40 lines, 0 words)" + FileText.LineTrimmedNote, FileText.Edited(new EditResult(FileOutcome.Ok, "notes.txt", 3, 3, 0, 0, 40, null, 0, [3, 9, 14], Strategy: MatchStrategy.LineTrimmed)));
        Assert.Equal(FileText.EditNotFound("notes.txt"), FileText.Edited(new EditResult(FileOutcome.EditNotFound, "notes.txt", 0, 0)));

        Assert.Equal("created docs\\notes\\", FileText.Created(new CreateResult(FileOutcome.Ok, @"docs\notes\")));
        Assert.Equal("docs\\ already exists", FileText.Created(new CreateResult(FileOutcome.Exists, @"docs\")));
        Assert.Equal(FileText.IsAFile("a.txt"), FileText.Created(new CreateResult(FileOutcome.IsAFile, "a.txt")));

        Assert.Equal("renamed a.txt to b.txt", FileText.Moved(new MoveResult(FileOutcome.Ok, "a.txt", "b.txt", false, true)));
        Assert.Equal("renamed drafts\\ to poems\\", FileText.Moved(new MoveResult(FileOutcome.Ok, @"drafts\", @"poems\", true, true)));
        Assert.Equal("moved a.txt to docs\\a.txt", FileText.Moved(new MoveResult(FileOutcome.Ok, "a.txt", @"docs\a.txt", false, false)));
        Assert.Equal(FileText.Exists("b.txt"), FileText.Moved(new MoveResult(FileOutcome.Exists, "a.txt", "b.txt", false, false)));
        Assert.Equal(FileText.IntoItself(@"d\", @"d\in\"), FileText.Moved(new MoveResult(FileOutcome.IntoItself, @"d\", @"d\in\", true, false)));
        Assert.Equal(FileText.Missing("a.txt"), FileText.Moved(new MoveResult(FileOutcome.Missing, "a.txt", "b.txt", false, false)));
        Assert.Equal("copied a.txt to b.txt", FileText.Copied(new MoveResult(FileOutcome.Ok, "a.txt", "b.txt", false, true)));
        Assert.Equal("Error: could not copy 'a.txt': x", FileText.Copied(new MoveResult(FileOutcome.Failed, "a.txt", "b.txt", false, false, "x")));

        Assert.Equal(FileText.RootItself, FileText.Deleted(new DeleteResult(FileOutcome.IntoItself, "", true)));
        // A delete (in place since 2026-09-20, always since 2026-10-01): the sentence says what is gone, a folder with everything in it — and since 2026-09-21 (the user's ask) no setting.
        Assert.Equal("deleted notes.txt", FileText.Deleted(new DeleteResult(FileOutcome.Ok, "notes.txt", false)));
        Assert.Equal("deleted the folder docs\\ and everything in it", FileText.Deleted(new DeleteResult(FileOutcome.Ok, @"docs\", true)));
        Assert.Equal(FileText.Missing("x"), FileText.Deleted(new DeleteResult(FileOutcome.Missing, "x", false)));
        // A folder in the way of an overwrite is refused, the destination named (2026-09-20).
        Assert.Equal(FileText.FolderInTheWay(@"b\"), FileText.Moved(new MoveResult(FileOutcome.FolderInTheWay, "a.txt", @"b\", false, false)));
        Assert.Equal(FileText.FolderInTheWay(@"b\"), FileText.Copied(new MoveResult(FileOutcome.FolderInTheWay, "a.txt", @"b\", false, false)));
        Assert.Equal("Error: 'b\\' is a folder in the way — move it aside first", FileText.FolderInTheWay(@"b\"));   // neither the setting nor .trash since 2026-09-21

        Assert.Equal("zipped docs\\ into docs.zip (12 entries, 40.1 KB)", FileText.Zipped(new ZipResult(FileOutcome.Ok, @"docs\", "docs.zip", 12, 40_100)));
        Assert.Equal("zipped a.txt into a.zip (1 entry, 100 B)", FileText.Zipped(new ZipResult(FileOutcome.Ok, "a.txt", "a.zip", 1, 100)));
        Assert.Equal(FileText.Exists("docs.zip"), FileText.Zipped(new ZipResult(FileOutcome.Exists, @"docs\", "docs.zip", 0, 0)));
        Assert.Equal(FileText.RootItself, FileText.Zipped(new ZipResult(FileOutcome.IntoItself, "", "files.zip", 0, 0)));
        Assert.Equal(FileText.IntoItself(@"d\", @"d\d.zip"), FileText.Zipped(new ZipResult(FileOutcome.IntoItself, @"d\", @"d\d.zip", 0, 0)));
        Assert.Equal("unzipped docs.zip into docs\\ (12 entries)", FileText.Unzipped(new ZipResult(FileOutcome.Ok, "docs.zip", @"docs\", 12, 0)));
        Assert.Equal(FileText.ZipSlip("e.zip", "../x"), FileText.Unzipped(new ZipResult(FileOutcome.OutsideRoot, "e.zip", @"safe\", 0, 0, "../x")));
        Assert.Equal(FileText.OutsideRoot(@"..\e.zip"), FileText.Unzipped(new ZipResult(FileOutcome.OutsideRoot, @"..\e.zip", "", 0, 0)));
        Assert.Equal(FileText.Exists(@"dst\b.txt"), FileText.Unzipped(new ZipResult(FileOutcome.Exists, "src.zip", @"dst\b.txt", 0, 0)));
        Assert.Equal(FileText.NotAnArchive("not.zip"), FileText.Unzipped(new ZipResult(FileOutcome.NotAnArchive, "not.zip", "", 0, 0)));

        Assert.Equal("opened notes.txt in the user's editor", FileText.Opened(new OpenResult(FileOutcome.Ok, "notes.txt", false)));
        Assert.Equal("opened docs\\ in Explorer", FileText.Opened(new OpenResult(FileOutcome.Ok, @"docs\", true)));
        Assert.Equal("opened the working directory in Explorer", FileText.Opened(new OpenResult(FileOutcome.Ok, "", true)));
        Assert.Equal("Error: could not open 'x': no app", FileText.Opened(new OpenResult(FileOutcome.Failed, "x", false, "no app")));
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(999, "999 B")]
    [InlineData(1_000, "1 KB")]
    [InlineData(1_234, "1.2 KB")]
    [InlineData(48_200, "48.2 KB")]
    [InlineData(999_950, "1000 KB")]
    [InlineData(1_000_000, "1 MB")]
    [InlineData(40_100_000, "40.1 MB")]
    [InlineData(2_300_000_000, "2.3 GB")]
    public void Size_IsPinned(long bytes, string expected) => Assert.Equal(expected, FileText.Size(bytes));

    [Fact]
    public void Bytes_Moment_Count()
    {
        Assert.Equal("1,234 bytes", FileText.Bytes(1234));
        Assert.Equal("1 byte", FileText.Bytes(1));
        Assert.Equal("0 bytes", FileText.Bytes(0));
        Assert.Equal("2026-09-12 14:05", FileText.Moment(When));
        Assert.Equal("1 file", FileText.Count(1, "file", "files"));
        Assert.Equal("1,200 files", FileText.Count(1200, "file", "files"));
        Assert.Equal("0 files", FileText.Count(0, "file", "files"));
    }
}
