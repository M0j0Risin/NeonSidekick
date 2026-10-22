using System.Text;
using NeonSidekick.Files;
using NeonSidekick.Git;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// Search, listing, patch matching and the diff after the 2026-10-22 fixes (the fix plan's Phase 5): the same first hits on every run, the
/// needle as given, a one-file search that says why it read nothing, a result held to read_file's cap, globs that can't blow up and read
/// <c>./</c> and fused <c>**</c>, walks budgeted over every entry, the newest and the first kept in heaps, a retried patch not applied twice,
/// one line never matched by likeness, the matcher's and the diff's work bounded, and completion asking its predicate only of what it shows.
/// </summary>
public sealed class SearchLimitsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _root;

    public SearchLimitsTests()
    {
        _root = Path.Combine(_dir, "work");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            /* best effort */
        }
    }

    private WorkingDirectory Files(WorkingDirectoryOptions? options = null) => new(() => _root, new ManualTimeProvider(), options);

    private string Put(string relative, string text)
    {
        string full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        return full;
    }

    // ── Search ─────────────────────────────────────────────────────────────

    [Fact]
    public void ACappedSearch_KeepsTheFirstFilesInPathOrder_EveryRun()
    {
        for (int i = 0; i < 40; i++)
        {
            Put($"f{i:D2}.txt", "word\nword\n");
        }

        var files = Files(new WorkingDirectoryOptions(SearchParallelism: 8));
        for (int run = 0; run < 5; run++)
        {
            var result = files.Search("word", "", null, false, CancellationToken.None, limit: 5);
            Assert.True(result.Truncated);
            Assert.Equal(["f00.txt", "f00.txt", "f01.txt", "f01.txt", "f02.txt"], result.Hits.Select(h => h.RelativePath));
            Assert.Equal([1, 2, 1, 2, 1], result.Hits.Select(h => h.Line));
        }
    }

    [Fact]
    public void TheNeedle_IsSearchedAsGiven_ItsLeadingSpacesKept()
    {
        Put("a.cs", "return x;\n    return x;\n");
        var files = Files();
        Assert.Equal([2], files.Search("    return x", "", null, false, CancellationToken.None).Hits.Select(h => h.Line));
        Assert.Equal([1, 2], files.Search("return x", "", null, false, CancellationToken.None).Hits.Select(h => h.Line));
        Assert.Equal([2], files.Search(@"^\s+return", "", null, true, CancellationToken.None).Hits.Select(h => h.Line));
        Assert.Equal(FileOutcome.Empty, files.Search("   ", "", null, false, CancellationToken.None).Outcome);
    }

    [Fact]
    public void AOneFileSearch_ThatReadsNothing_SaysWhy()
    {
        var files = Files();
        Put("big.log", new string('x', (int)WorkingDirectory.MaxTextFileBytes + 10));
        File.WriteAllBytes(Path.Combine(_root, "blob.bin"), [1, 0, 2, 0, 3]);

        var big = files.Search("x", "big.log", null, false, CancellationToken.None);
        Assert.Equal((0, FileText.SearchFileTooBig("big.log")), (big.FilesSearched, big.Unsearched));
        Assert.Contains(FileText.SearchFileTooBig("big.log"), FileText.SearchHits(big, "x"));
        Assert.Equal(FileText.SearchFileNotText("blob.bin"), files.Search("x", "blob.bin", null, false, CancellationToken.None).Unsearched);
        // A folder's search names none of its skipped files that way.
        Assert.Null(files.Search("x", "", null, false, CancellationToken.None).Unsearched);
    }

    [Fact]
    public void ASearchsAnswer_IsHeldToReadFilesCap()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 2_000; i++)
        {
            sb.Append("needle ").Append('y', 180).Append('\n');
        }

        Put("long.txt", sb.ToString());
        var result = Files().Search("needle", "", null, false, CancellationToken.None, context: 5, limit: WorkingDirectory.MaxResultLimit);
        string answer = FileText.SearchHits(result, "needle");
        Assert.True(answer.Length <= WorkingDirectory.MaxReadChars, answer.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains("(cut at 32,000 characters", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void ACancelledSearch_Throws()
    {
        Put("a.txt", "word");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Files().Search("word", "", null, false, cancelled.Token));
    }

    // ── Globs ──────────────────────────────────────────────────────────────

    [Fact]
    public void Braces_AreCappedBySteps_NotOnlyByResults()
    {
        string pattern = string.Concat(Enumerable.Repeat("{,}", 40)) + "*.cs";
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal([pattern], PathGlob.ExpandBraces(pattern));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), clock.Elapsed.ToString());
        Assert.Equal(["a.cs", "b.cs"], PathGlob.ExpandBraces("{a,b}.cs"));
    }

    [Fact]
    public void ManyDoubleStars_OverADeepPath_AreWorkedOutOnce()
    {
        string pattern = string.Concat(Enumerable.Repeat("**/", 12)) + "nope.cs";
        string path = string.Join('/', Enumerable.Range(0, 30).Select(i => "d" + i)) + "/x.cs";
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(PathGlob.IsMatch(pattern, path));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), clock.Elapsed.ToString());
        Assert.True(PathGlob.IsMatch(string.Concat(Enumerable.Repeat("**/", 12)) + "x.cs", path));
    }

    [Fact]
    public void ADotSegment_IsDropped_AndAFusedDoubleStar_ReachesEveryLevel()
    {
        Assert.True(PathGlob.IsMatch("./**/*.md", "a/b/c.md"));
        Assert.True(PathGlob.IsMatch("./*.md", "c.md"));
        Assert.True(PathGlob.IsMatch("**.cs", "a/b/c.cs"));
        Assert.True(PathGlob.IsMatch("**.cs", "c.cs"));
        Assert.True(PathGlob.IsMatch("src/**.cs", "src/a/b.cs"));
        Assert.False(PathGlob.IsMatch("src/**.cs", "lib/a/b.cs"));
        Assert.True(PathGlob.IsMatch("src**", "src2/a/b.cs"));
    }

    // ── Walks ──────────────────────────────────────────────────────────────

    [Fact]
    public void Find_CountsEveryEntryItLooksAt_AgainstTheBudget()
    {
        for (int i = 0; i < 30; i++)
        {
            Put($"d/f{i:D2}.txt", "x");
        }

        var files = Files(new WorkingDirectoryOptions(MaxWalkEntries: 10));
        var none = files.Find("*.nothing", "");
        Assert.True(none.Truncated);
        Assert.Empty(none.Paths);
        Assert.False(Files().Find("*.nothing", "").Truncated);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Files().Find("*.txt", "", cancelled.Token));
    }

    [Fact]
    public void Recent_KeepsTheNewest_AndHonorsTheFilesPattern()
    {
        var start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 6; i++)
        {
            File.SetLastWriteTimeUtc(Put($"n{i}.md", "x"), start.AddHours(i));
            File.SetLastWriteTimeUtc(Put($"n{i}.txt", "x"), start.AddHours(i).AddMinutes(30));
        }

        var files = Files();
        Assert.Equal(["n5.txt", "n5.md", "n4.txt"], files.Recent("", 3).Entries.Select(e => e.RelativePath));
        Assert.Equal(["n5.md", "n4.md"], files.Recent("", 2, CancellationToken.None, namePattern: "*.md").Entries.Select(e => e.RelativePath));
    }

    [Fact]
    public void List_KeepsTheFirstEntries_FoldersFirst()
    {
        foreach (string name in new[] { "e.txt", "b.txt", "a.txt", "d.txt", "c.txt" })
        {
            Put(name, "x");
        }

        Directory.CreateDirectory(Path.Combine(_root, "zdir"));
        var listed = Files().List("", 3);
        Assert.True(listed.Truncated);
        Assert.Equal(["zdir", "a.txt", "b.txt"], listed.Entries.Select(e => e.Name));
        Assert.Equal(6, Files().List("").Entries.Count);
    }

    [Fact]
    public void Complete_AsksItsPredicate_OnlyOfWhatItShows()
    {
        for (int i = 0; i < 400; i++)
        {
            Put($"p{i:D3}.txt", "x");
        }

        int asked = 0;
        var shown = Files().Complete("p", path => { asked++; return false; });
        Assert.Empty(shown.Paths);
        Assert.True(shown.Truncated);
        Assert.True(asked <= WorkingDirectory.MaxMentionProbes, asked.ToString(System.Globalization.CultureInfo.InvariantCulture));

        asked = 0;
        var all = Files().Complete("p", path => { asked++; return true; });
        Assert.Equal(WorkingDirectory.MaxMentionMatches, all.Paths.Count);
        Assert.Equal("p000.txt", all.Paths[0]);
        Assert.Equal(WorkingDirectory.MaxMentionMatches, asked);
    }

    // ── Patch matching ─────────────────────────────────────────────────────

    [Fact]
    public void ARetriedPatch_WhoseNewTextHoldsTheOld_IsAlreadyApplied()
    {
        const string old = "Console.Write(name);";
        const string @new = "Console.Write(name);\nConsole.Write(age);";
        var first = FuzzyMatch.Replace("x\n" + old + "\ny\n", old, @new, replaceAll: false);
        Assert.Equal(MatchOutcome.Replaced, first.Outcome);
        var again = FuzzyMatch.Replace(first.Content, old, @new, replaceAll: false);
        Assert.Equal(MatchOutcome.AlreadyApplied, again.Outcome);
        Assert.Equal(first.Content, again.Content);
        // An old text standing outside the new one as well is still an edit to make.
        Assert.False(FuzzyMatch.IsAppliedAround(first.Content + old + "\n", old, @new));
    }

    [Fact]
    public void TheBlockAnchor_GivesUpPastItsCandidateCap()
    {
        string content = string.Concat(Enumerable.Repeat("{\n    body();\n}\n", FuzzyMatch.MaxAnchorCandidates + 5));
        Assert.Empty(FuzzyMatch.Find(MatchStrategy.BlockAnchor, content, "{\n    other();\n}"));
        string few = string.Concat(Enumerable.Repeat("{\n    body();\n}\n", 3));
        Assert.NotEmpty(FuzzyMatch.Find(MatchStrategy.BlockAnchor, few, "{\n    bodies();\n}"));
    }

    [Fact]
    public void AReplaceAll_ThatWouldPassTheReadCap_IsRefused_NothingBuilt()
    {
        string content = string.Concat(Enumerable.Repeat("a\n", 1_000));
        var result = FuzzyMatch.Replace(content, "a", new string('b', 10_000), replaceAll: true);
        Assert.Equal(MatchOutcome.TooLarge, result.Outcome);
        Assert.Same(content, result.Content);

        Put("many.txt", content);
        var edit = Files().EditText("many.txt", "a", new string('b', 10_000), replaceAll: true);
        Assert.Equal(FileOutcome.Failed, edit.Outcome);
        Assert.Equal(FileText.EditResultTooBig(1_000), edit.Detail);
        Assert.Equal(content, File.ReadAllText(Path.Combine(_root, "many.txt")));
    }

    [Fact]
    public void LineNumbers_AreCountedInOnePass()
    {
        const string content = "a\nb a\n\na\n";
        Assert.Equal([1, 2, 4], WorkingDirectory.LineNumbers(content, [new TextSpan(0, 1), new TextSpan(4, 5), new TextSpan(7, 8)]));
        Put("r.txt", content);
        Assert.Equal([1, 2, 4], Files().EditText("r.txt", "a", "z", replaceAll: true).Lines);
    }

    // ── The diff after a write ─────────────────────────────────────────────

    [Fact]
    public void AReorderedFile_GivesUpItsDiff_WithinTheStepBudget()
    {
        var lines = Enumerable.Range(0, 20_000).Select(i => "row " + i.ToString("D5", System.Globalization.CultureInfo.InvariantCulture)).ToList();
        string before = string.Join('\n', lines) + "\n";
        // Each pair swapped: the matcher peels one line a call over the whole rest, n² / 2 steps (a reversal is two calls).
        string after = string.Join('\n', Enumerable.Range(0, lines.Count).Select(i => lines[i ^ 1])) + "\n";
        Assert.Null(UnifiedDiff.Hunks(UnifiedDiff.Split(before), UnifiedDiff.Split(after)));
        Assert.Null(SequenceSimilarity.Opcodes("abcdef", "fedcba", budget: 3));
        Assert.Equal(SequenceSimilarity.Opcodes("abcdef", "abXdef"), SequenceSimilarity.Opcodes("abcdef", "abXdef", budget: 1_000));
        Assert.NotEmpty(UnifiedDiff.Hunks(UnifiedDiff.Split("a\nb\nc\n"), UnifiedDiff.Split("a\nB\nc\n"))!);
    }
}
