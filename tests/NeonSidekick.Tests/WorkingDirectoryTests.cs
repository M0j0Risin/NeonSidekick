using System.IO.Compression;
using System.Text;
using NeonSidekick.Files;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

public sealed class WorkingDirectoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly ManualTimeProvider _time = new();
    private readonly WorkingDirectory _files;

    public WorkingDirectoryTests()
    {
        _root = Path.Combine(_dir, "files");
        _files = new WorkingDirectory(() => _root, _time);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string Put(string relative, string text)
    {
        string full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text, new UTF8Encoding(false));
        return full;
    }

    private string Full(string relative) => Path.Combine(_root, relative);

    // ---- the root ----

    [Fact]
    public void Resolve_BlankIsTheProfilesFilesFolder_ElseTheFullPath()
    {
        Assert.Equal(Path.Combine(@"D:\home\profiles\default", "files"), WorkingDirectory.Resolve("", @"D:\home\profiles\default"));
        Assert.Equal(Path.Combine(@"D:\home\profiles\default", "files"), WorkingDirectory.Resolve("   ", @"D:\home\profiles\default"));
        Assert.Equal(@"D:\work\notes", WorkingDirectory.Resolve(@" D:\work\notes\ ", @"D:\home\profiles\default"));
        Assert.Equal(@"D:\work\notes", WorkingDirectory.Resolve(@"D:\work\sub\..\notes", @"D:\home\profiles\default"));
        Assert.True(WorkingDirectory.IsDefault(""));
        Assert.False(WorkingDirectory.IsDefault(@"D:\work"));
    }

    [Fact]
    public void EnsureExists_CreatesTheRoot_OnceOnly()
    {
        Assert.False(_files.Exists);
        Assert.Equal(_root, _files.EnsureExists());
        Assert.True(_files.Exists);
        Assert.Equal(_root, _files.EnsureExists());
        Assert.Equal(_root, _files.Root);
    }

    // ---- the sandbox ----

    [Theory]
    [InlineData("", "")]
    [InlineData(".", "")]
    [InlineData("notes.txt", "notes.txt")]
    [InlineData(@"docs\a.md", @"docs\a.md")]
    [InlineData("docs/a.md", @"docs\a.md")]
    [InlineData(@"docs\..\notes.txt", "notes.txt")]
    [InlineData(@".\docs\.\a.md", @"docs\a.md")]
    [InlineData(@"  docs\  ", "docs")]
    public void Resolve_AcceptsPathsInsideTheRoot(string relative, string expected)
    {
        Assert.Equal(FileOutcome.Ok, _files.Resolve(relative, forWrite: true, out string full));
        Assert.Equal(Path.Combine(_root, expected).TrimEnd('\\'), full);
    }

    [Fact]
    public void Resolve_AcceptsARootedPathThatLandsInside_ByAnyCase()
    {
        string inside = Path.Combine(_root.ToUpperInvariant(), "Docs", "a.md");
        Assert.Equal(FileOutcome.Ok, _files.Resolve(inside, forWrite: true, out string full));
        Assert.Equal(inside, full);
    }

    [Theory]
    [InlineData("..")]
    [InlineData(@"..\profile.json")]
    [InlineData(@"docs\..\..\profile.json")]
    [InlineData(@"C:\Windows\system.ini")]
    [InlineData(@"\\server\share\x")]
    [InlineData(@"\top")]
    public void Resolve_RefusesPathsOutsideTheRoot(string relative)
    {
        Assert.Equal(FileOutcome.OutsideRoot, _files.Resolve(relative, forWrite: false, out string full));
        Assert.Equal("", full);
    }

    [Fact]
    public void Resolve_RefusesASiblingWhoseNameStartsWithTheRoots()
    {
        // "files2" starts with "files" but is not inside it: the prefix check is on whole segments.
        Assert.Equal(FileOutcome.OutsideRoot, _files.Resolve(_root + "2", forWrite: false, out _));
    }

    [Fact]
    public void Resolve_ADotTrash_IsAFolderLikeAnyOther()
    {
        // Written by nothing since File safe edits went (2026-10-01, the user's call): a .trash left from before reads and writes as any folder.
        Assert.Equal(FileOutcome.Ok, _files.Resolve(@".trash\x.txt", forWrite: false, out _));
        Assert.Equal(FileOutcome.Ok, _files.Resolve(@".trash\x.txt", forWrite: true, out string full));
        Assert.Equal(Path.Combine(_root, ".trash", "x.txt"), full);
        Assert.Equal(FileOutcome.Ok, _files.Resolve(".trash", forWrite: true, out _));
    }

    [Fact]
    public void Relative_IsTheDisplayForm()
    {
        Assert.Equal("", _files.Relative(_root));
        Assert.Equal("", _files.Relative(_root + @"\"));
        Assert.Equal(@"docs\a.md", _files.Relative(Path.Combine(_root, "docs", "a.md")));
        Assert.Equal(@"docs\", _files.Relative(Path.Combine(_root, "docs"), isDirectory: true));
    }

    // ---- listing ----

    [Fact]
    public void List_FoldersFirstThenFiles_ByName()
    {
        Put("b.txt", "bb");
        Put("A.txt", "a");
        Put(@"zed\x.txt", "");
        Put(@"alpha\y.txt", "");

        var result = _files.List("");

        Assert.Equal(FileOutcome.Ok, result.Outcome);
        Assert.Equal("", result.Relative);
        Assert.Equal(new[] { "alpha", "zed", "A.txt", "b.txt" }, result.Entries.Select(e => e.Name));
        Assert.Equal(new[] { true, true, false, false }, result.Entries.Select(e => e.IsDirectory));
        Assert.Equal(2, result.Entries[3].Length);
        Assert.False(result.Truncated);

        // A .trash left from before 2026-10-01 is listed like any folder (it was hidden at the root while File safe edits kept copies there).
        Put(@".trash\20260911-140530\old.txt", "old");
        Assert.Equal(new[] { ".trash", "alpha", "zed", "A.txt", "b.txt" }, _files.List("").Entries.Select(e => e.Name));
    }

    [Fact]
    public void List_ASubfolder_Empty_Missing_AndAFile()
    {
        Directory.CreateDirectory(Full("empty"));
        Put("f.txt", "x");

        Assert.Equal(FileOutcome.Ok, _files.List("empty").Outcome);
        Assert.Empty(_files.List("empty").Entries);
        Assert.Equal(@"empty\", _files.List("empty").Relative);
        Assert.Equal(FileOutcome.Missing, _files.List("nope").Outcome);
        Assert.Equal(FileOutcome.IsAFile, _files.List("f.txt").Outcome);
        Assert.Equal(FileOutcome.OutsideRoot, _files.List("..").Outcome);
    }

    [Fact]
    public void List_CapsAtMaxEntries()
    {
        for (int i = 0; i < WorkingDirectory.MaxEntries + 5; i++)
        {
            Put($"f{i:000}.txt", "");
        }

        var result = _files.List("");
        Assert.Equal(WorkingDirectory.MaxEntries, result.Entries.Count);
        Assert.True(result.Truncated);
    }

    // ---- tree ----

    [Fact]
    public void FileTree_MaxDepth_StopsTheDescent()
    {
        // list_directory's depth (2026-09-18): the walk stops that many levels down, files and folders alike; the default is every level.
        Put(@"b\deep\deeper\deepest\bottom\x.txt", "");
        Put(@"a\one.txt", "");
        Put(@"a\sub\two.txt", "");

        Assert.Equal(new[] { "a", "b" }, _files.FileTree("", WorkingDirectory.MaxEntries, 1).Entries.Select(e => e.Name));
        Assert.Equal(new[] { ("a", 1), ("sub", 2), ("one.txt", 2), ("b", 1), ("deep", 2) }, _files.FileTree("", WorkingDirectory.MaxEntries, 2).Entries.Select(e => (e.Name, e.Depth)));
        Assert.Equal(4, _files.FileTree("", WorkingDirectory.MaxEntries, 4).Entries.Max(e => e.Depth));
        Assert.Equal(6, _files.FileTree("", WorkingDirectory.MaxEntries).Entries.Max(e => e.Depth));   // no limit by default
        Assert.Equal(1, _files.FileTree("", WorkingDirectory.MaxEntries, 0).Entries.Max(e => e.Depth));   // below 1 reads as 1
        Assert.Equal(new[] { "deep" }, _files.FileTree("b", WorkingDirectory.MaxEntries, 1).Entries.Select(e => e.Name));
        Assert.Equal(FileOutcome.Missing, _files.FileTree("nope", 10, 1).Outcome);
        Assert.Equal(FileOutcome.IsAFile, _files.FileTree(@"a\one.txt", 10, 1).Outcome);
    }

    [Fact]
    public void FileTree_CapsAtMaxEntries_WithADepth()
    {
        for (int i = 0; i < WorkingDirectory.MaxEntries + 3; i++)
        {
            Directory.CreateDirectory(Full($"d{i:000}"));
        }

        var result = _files.FileTree("", WorkingDirectory.MaxEntries, 1);
        Assert.Equal(WorkingDirectory.MaxEntries, result.Entries.Count);
        Assert.True(result.Truncated);
    }

    // ---- /tree ----

    [Fact]
    public void FileTree_FoldersFirstThenFiles_DepthFirst_WithSizesAndIsLast()
    {
        Put(@"b\deep\bottom.txt", "12345");
        Put(@"a\one.txt", "1");
        Put(@"a\sub\two.txt", "22");
        Put("zed.md", "");
        Put("Alpha.md", "abc");
        File.SetAttributes(Put("hidden.txt", ""), FileAttributes.Hidden);

        var result = _files.FileTree("", WorkingDirectory.DefaultTreeLength);

        Assert.Equal(FileOutcome.Ok, result.Outcome);
        Assert.Equal("", result.Relative);
        Assert.Equal(_root + @"\", result.FullPath);
        Assert.Equal(
            new[]
            {
                ("a", 1, true, 0L, false),
                ("sub", 2, true, 0L, false),
                ("two.txt", 3, false, 2L, true),
                ("one.txt", 2, false, 1L, true),
                ("b", 1, true, 0L, false),
                ("deep", 2, true, 0L, true),
                ("bottom.txt", 3, false, 5L, true),
                ("Alpha.md", 1, false, 3L, false),
                ("zed.md", 1, false, 0L, true),
            },
            result.Entries.Select(e => (e.Name, e.Depth, e.IsDirectory, e.Length, e.IsLast)));
        Assert.False(result.Truncated);

        var sub = _files.FileTree("a", WorkingDirectory.DefaultTreeLength);
        Assert.Equal(@"a\", sub.Relative);
        Assert.Equal(Full("a") + @"\", sub.FullPath);
        Assert.Equal(new[] { "sub", "two.txt", "one.txt" }, sub.Entries.Select(e => e.Name));

        Assert.Equal(FileOutcome.Missing, _files.FileTree("nope", 10).Outcome);
        Assert.Equal(FileOutcome.IsAFile, _files.FileTree(@"a\one.txt", 10).Outcome);
        Assert.Equal(FileOutcome.OutsideRoot, _files.FileTree(@"..\outside", 10).Outcome);
    }

    [Fact]
    public void FileTree_HideDotEntries_LeavesOutEveryDotName_AtEveryDepth()
    {
        // /vault (2026-09-22): .obsidian, .trash, .git and any dot-file, at the root or deeper, as the vault tools leave them.
        Put(@".obsidian\app.json", "{}");
        Put(@".git\HEAD", "");
        Put(@".trash\old.md", "");
        Put(@"Notes\.draft.md", "");
        Put(@"Notes\.cache\x.md", "");
        Put(@"Notes\Plan.md", "");
        Put("Home.md", "");
        Put(".hidden.md", "");

        var result = _files.FileTree("", WorkingDirectory.DefaultTreeLength, hideDotEntries: true);

        Assert.Equal(new[] { ("Notes", 1, false), ("Plan.md", 2, true), ("Home.md", 1, true) }, result.Entries.Select(e => (e.Name, e.Depth, e.IsLast)));
        // Without the switch the dot-names are there as ever (the root's .trash too since 2026-10-01).
        Assert.Contains(_files.FileTree("", WorkingDirectory.DefaultTreeLength).Entries, e => e.Name == ".obsidian");
        Assert.Contains(_files.FileTree("", WorkingDirectory.DefaultTreeLength).Entries, e => e.Name == ".trash");
    }

    [Fact]
    public void FileTree_ShowHidden_ListsHiddenAndDotEntries()
    {
        // /tree under File browser/tree mode show-hidden (2026-09-23, the user's call): the Hidden .git and a hidden folder too; the
        // default (hideDotEntries, the walk's own Hidden skip) leaves out both and the plain dot-file.
        Put(@".git\HEAD", "");
        Put(".config", "");
        Put(@"secret\x.txt", "");
        Put("a.txt", "");
        File.SetAttributes(Full(".git"), FileAttributes.Directory | FileAttributes.Hidden);
        File.SetAttributes(Full("secret"), FileAttributes.Directory | FileAttributes.Hidden);

        Assert.Equal(new[] { ".git", "HEAD", "secret", "x.txt", ".config", "a.txt" }, _files.FileTree("", WorkingDirectory.DefaultTreeLength, showHidden: true).Entries.Select(e => e.Name));
        Assert.Equal(new[] { "a.txt" }, _files.FileTree("", WorkingDirectory.DefaultTreeLength, hideDotEntries: true).Entries.Select(e => e.Name));
        Assert.Equal(new[] { ".config", "a.txt" }, _files.FileTree("", WorkingDirectory.DefaultTreeLength).Entries.Select(e => e.Name));   // the model's listing, untouched
    }

    [Fact]
    public void FileTree_HideGitFolders_LeavesOutEveryDotGitFolder_EvenUnderShowHidden_ButWalksOneAskedFor()
    {
        // /tree (2026-09-30, the user's ask: "in the same way it ignores .trash", as it did then): the root's .git and a nested repository's,
        // whatever show-hidden says; a .git file (a submodule's pointer) is no folder and stays; /tree .git still walks it.
        Put(@".git\HEAD", "");
        Put(@"sub\.git\config", "");
        Put(@"mod\.git", "gitdir: ../.git/modules/mod");
        Put("a.txt", "");
        File.SetAttributes(Full(".git"), FileAttributes.Directory | FileAttributes.Hidden);

        var names = _files.FileTree("", WorkingDirectory.DefaultTreeLength, showHidden: true, hideGitFolders: true).Entries.Select(e => e.Name);
        Assert.Equal(new[] { "mod", ".git", "sub", "a.txt" }, names);
        Assert.Equal(new[] { "HEAD" }, _files.FileTree(".git", WorkingDirectory.DefaultTreeLength, showHidden: true, hideGitFolders: true).Entries.Select(e => e.Name));
        Assert.Contains(_files.FileTree("", WorkingDirectory.DefaultTreeLength, showHidden: true).Entries, e => e.Name == ".git" && e.IsDirectory);   // off by default
    }

    [Fact]
    public void FileTree_EmptyFolder_HasNoEntries()
    {
        Directory.CreateDirectory(Full("empty"));

        var result = _files.FileTree("empty", 10);
        Assert.Equal(FileOutcome.Ok, result.Outcome);
        Assert.Empty(result.Entries);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void FileTree_StopsAtTheCap_AndClampsIt()
    {
        Put(@"a\one.txt", "");
        Put(@"a\two.txt", "");
        Put(@"b\three.txt", "");
        Put("four.txt", "");

        var cut = _files.FileTree("", 3);
        Assert.Equal(new[] { "a", "one.txt", "two.txt" }, cut.Entries.Select(e => e.Name));
        Assert.True(cut.Truncated);

        var exact = _files.FileTree("", 6);
        Assert.Equal(6, exact.Entries.Count);
        Assert.False(exact.Truncated);              // every entry fitted: nothing was cut

        Assert.Single(_files.FileTree("", 0).Entries);   // below the range reads as the least
        Assert.True(_files.FileTree("", 0).Truncated);
        Assert.Equal(6, _files.FileTree("", int.MaxValue).Entries.Count);
    }

    // ---- find ----

    [Fact]
    public void Find_ByGlob_Recursive_Sorted()
    {
        Put("readme.md", "");
        Put(@"docs\notes.md", "");
        Put(@"docs\deep\more.MD", "");
        Put(@"docs\other.txt", "");

        var result = _files.Find("*.md", "");
        Assert.Equal(FileOutcome.Ok, result.Outcome);
        Assert.Equal(new[] { @"docs\deep\more.MD", @"docs\notes.md", "readme.md" }, result.Paths);

        Assert.Equal(new[] { @"docs\notes.md" }, _files.Find("notes.*", "docs").Paths);
        Assert.Equal(new[] { @"docs\deep\more.MD", @"docs\notes.md", @"docs\other.txt", "readme.md" }, _files.Find("", "").Paths);
        Assert.Empty(_files.Find("zzz*", "").Paths);
        Assert.Equal(FileOutcome.Missing, _files.Find("*", "nope").Outcome);
    }

    [Fact]
    public void Find_CapsAtMaxMatches()
    {
        for (int i = 0; i < WorkingDirectory.DefaultFindLimit + 2; i++)
        {
            Put($"f{i:000}.txt", "");
        }

        var result = _files.Find("*.txt", "");
        Assert.Equal(WorkingDirectory.DefaultFindLimit, result.Paths.Count);
        Assert.True(result.Truncated);

        // limit (2026-09-19) is the model's cap, clamped to MaxListLimit; the default stands without it.
        Assert.Equal(3, _files.Find("*.txt", "", limit: 3).Paths.Count);
        Assert.Equal(WorkingDirectory.DefaultFindLimit + 2, _files.Find("*.txt", "", limit: 999).Paths.Count);
        Assert.Single(_files.Find("*.txt", "", limit: -5).Paths);
    }

    [Fact]
    public void Walks_HonourMaxDepth()
    {
        Put("top.txt", "needle");
        Put(@"a\mid.txt", "needle");
        Put(@"a\b\deep.txt", "needle");

        Assert.Equal(new[] { "top.txt" }, _files.Find("*.txt", "", maxDepth: 1).Paths);
        Assert.Equal(new[] { @"a\mid.txt", "top.txt" }, _files.Find("*.txt", "", maxDepth: 2).Paths);
        Assert.Equal(3, _files.Find("*.txt", "").Paths.Count);
        Assert.Equal(new[] { @"a\mid.txt" }, _files.Find("*.txt", "a", maxDepth: 1).Paths);
        Assert.Single(_files.Recent("", 10, maxDepth: 1).Entries);
        Assert.Equal(2, _files.Recent("", 10, maxDepth: 2).Entries.Count);
        Assert.Equal(1, _files.Search("needle", "", null, false, CancellationToken.None, maxDepth: 1).FilesSearched);
        Assert.Equal(3, _files.Search("needle", "", null, false, CancellationToken.None).FilesSearched);
    }

    // ---- complete (@-mentions) ----

    [Fact]
    public void Complete_EmptyPrefix_ListsOneLevel_FoldersFirst_ForwardSlashes()
    {
        Put(@"test\thing.txt", "");
        Put(@"test\bling.txt", "");
        Put("zed.md", "");
        Put("Alpha.md", "");
        File.SetAttributes(Put("hidden.txt", ""), FileAttributes.Hidden);

        var result = _files.Complete("");

        Assert.Equal(FileOutcome.Ok, result.Outcome);
        // One level only (nothing of test/ inside), folders first, then by name ignoring case; a hidden file skipped.
        Assert.Equal(new[] { "test/", "Alpha.md", "zed.md" }, result.Paths);
        Assert.False(result.Truncated);
        Assert.Equal(new[] { "test/bling.txt", "test/thing.txt" }, _files.Complete("test/").Paths);
        Assert.Equal(new[] { "test/bling.txt", "test/thing.txt" }, _files.Complete(@"test\").Paths);
    }

    [Fact]
    public void Complete_NamePrefix_WalksTheSubtree_IgnoringCase()
    {
        Put(@"test\thing.txt", "");
        Put(@"test\bling.txt", "");
        Put(@"test\deep\tail.md", "");
        Put("top.txt", "");

        // The user's cases (2026-09-16): @t → the folder and the files whose NAME starts with t, anywhere; @b → the one file.
        Assert.Equal(new[] { "test/", "test/deep/tail.md", "test/thing.txt", "top.txt" }, _files.Complete("t").Paths);
        Assert.Equal(new[] { "test/bling.txt" }, _files.Complete("b").Paths);
        Assert.Equal(new[] { "test/bling.txt" }, _files.Complete("B").Paths);
        // A folder segment narrows the walk to that folder; the prefix still matches names below it.
        Assert.Equal(new[] { "test/deep/tail.md", "test/thing.txt" }, _files.Complete("test/t").Paths);
        Assert.Equal(new[] { "test/thing.txt" }, _files.Complete("test/th").Paths);
        Assert.Equal(new[] { "test/thing.txt" }, _files.Complete(@"test\th").Paths);
        Assert.Empty(_files.Complete("zzz").Paths);
    }

    [Fact]
    public void Complete_RefusesOutsideTheRoot_AndNeverCreatesIt()
    {
        Assert.Equal(FileOutcome.Missing, _files.Complete("").Outcome);
        Assert.False(_files.Exists);

        Put("a.txt", "");
        Assert.Equal(FileOutcome.OutsideRoot, _files.Complete("../x").Outcome);
        Assert.Equal(FileOutcome.Missing, _files.Complete("nope/").Outcome);
        Assert.Equal(FileOutcome.IsAFile, _files.Complete("a.txt/").Outcome);
    }

    [Fact]
    public void Complete_TextFilesOnly_DropsBinaries_KeepsFoldersEmptyAndExtensionlessFiles()
    {
        Put("notes.md", "# Notes");
        Put("empty.txt", "");
        Put("LICENSE", "MIT");
        Put(@"docs\a.md", "a");
        File.WriteAllBytes(Full("pic.png"), [0x89, (byte)'P', (byte)'N', (byte)'G', 0, 0, 0, 13]);
        File.WriteAllBytes(Full(@"docs\deep.bin"), [1, 2, 0, 3]);

        // The /speak list (2026-09-17): the NUL probe ReadText judges by; folders pass, an empty file is text.
        Assert.Equal(new[] { "docs/", "empty.txt", "LICENSE", "notes.md" }, _files.Complete("", WorkingDirectory.IsTextFile).Paths);
        Assert.Equal(new[] { "docs/a.md" }, _files.Complete("docs/", WorkingDirectory.IsTextFile).Paths);
        Assert.Equal(new[] { "docs/", "docs/deep.bin" }, _files.Complete("d").Paths);   // the default keeps everything
        Assert.Equal(new[] { "docs/" }, _files.Complete("d", WorkingDirectory.IsTextFile).Paths);
        // The /view list: images by extension, folders still.
        Assert.Equal(new[] { "docs/", "pic.png" }, _files.Complete("", ImageFile.IsImagePath).Paths);
        Assert.Empty(_files.Complete("docs/", ImageFile.IsImagePath).Paths);
        Assert.True(WorkingDirectory.IsTextFile(Full("LICENSE")));
        Assert.True(WorkingDirectory.IsTextFile(Full("empty.txt")));
        Assert.False(WorkingDirectory.IsTextFile(Full("pic.png")));
        Assert.False(WorkingDirectory.IsTextFile(Full("missing.txt")));   // cannot be opened: not text
    }

    [Fact]
    public void Complete_CapsTheList()
    {
        for (int i = 0; i < WorkingDirectory.MaxMentionMatches + 2; i++)
        {
            Put($"f{i:000}.txt", "");
        }

        var result = _files.Complete("f");
        Assert.Equal(WorkingDirectory.MaxMentionMatches, result.Paths.Count);
        Assert.Equal("f000.txt", result.Paths[0]);
        Assert.True(result.Truncated);
    }

    // ---- search ----

    [Fact]
    public void Search_CaseInsensitive_LineNumbered_SortedByPathThenLine_TextFilesOnly()
    {
        Put("b.txt", "nothing here\nThe NEEDLE is on line two\nneedle again");
        Put(@"a\c.txt", "  a needle, trimmed   ");
        Put("bin.dat", "needle\0binary");
        Put("big.txt", new string('x', (int)WorkingDirectory.MaxTextFileBytes + 1) + " needle");

        var result = _files.Search("needle", "", null, regex: false, CancellationToken.None);

        Assert.Equal(FileOutcome.Ok, result.Outcome);
        Assert.Equal(
            new[] { (@"a\c.txt", 1, "a needle, trimmed"), ("b.txt", 2, "The NEEDLE is on line two"), ("b.txt", 3, "needle again") },
            result.Hits.Select(h => (h.RelativePath, h.Line, h.Text)));
        Assert.Equal(2, result.FilesMatched);
        Assert.Equal(2, result.FilesSearched);   // the binary and the big file were skipped before counting
        Assert.False(result.Truncated);
        Assert.Equal(0, result.TimedOut);
    }

    [Fact]
    public void Search_FilesPattern_Subfolder_Empty_AndNoHits()
    {
        Put("a.md", "word");
        Put("a.txt", "word");
        Put(@"sub\b.txt", "word");

        Assert.Equal(new[] { "a.md" }, _files.Search("word", "", "*.md", false, CancellationToken.None).Hits.Select(h => h.RelativePath));
        Assert.Equal(new[] { @"sub\b.txt" }, _files.Search("word", "sub", null, false, CancellationToken.None).Hits.Select(h => h.RelativePath));
        Assert.Empty(_files.Search("absent", "", null, false, CancellationToken.None).Hits);
        Assert.Equal(FileOutcome.Empty, _files.Search("  ", "", null, false, CancellationToken.None).Outcome);
        Assert.Equal(FileOutcome.Missing, _files.Search("word", "nope", null, false, CancellationToken.None).Outcome);
        // One file as the path (2026-09-18): searched alone, the files pattern ignored, the display its own path.
        var single = _files.Search("word", @"sub\b.txt", "*.md", false, CancellationToken.None);
        Assert.Equal(FileOutcome.Ok, single.Outcome);
        Assert.True(single.SingleFile);
        Assert.Equal(@"sub\b.txt", single.Relative);
        Assert.Equal((1, 1), (single.FilesSearched, single.FilesMatched));
        Assert.Equal(new[] { @"sub\b.txt" }, single.Hits.Select(h => h.RelativePath));
        Assert.False(_files.Search("word", "sub", null, false, CancellationToken.None).SingleFile);
        Assert.Equal(FileOutcome.OutsideRoot, _files.Search("word", "..", null, false, CancellationToken.None).Outcome);

        // A path glob (2026-09-17): matched on the path under the searched folder, or under the root, so it works from either.
        Put(@"src\deep\x.cs", "word");
        Put(@"src\y.cs", "word");
        Assert.Equal(new[] { @"src\deep\x.cs", @"src\y.cs" }, _files.Search("word", "", "src/**/*.cs", false, CancellationToken.None).Hits.Select(h => h.RelativePath));
        Assert.Equal(new[] { @"src\deep\x.cs", @"src\y.cs" }, _files.Search("word", "src", "src/**/*.cs", false, CancellationToken.None).Hits.Select(h => h.RelativePath));
        Assert.Equal(new[] { @"src\deep\x.cs", @"src\y.cs" }, _files.Search("word", "src", "**/*.cs", false, CancellationToken.None).Hits.Select(h => h.RelativePath));
        Assert.Equal(new[] { @"src\deep\x.cs" }, _files.Search("word", "", "src/*/*.cs", false, CancellationToken.None).Hits.Select(h => h.RelativePath));
        Assert.Equal(new[] { @"sub\b.txt" }, _files.Search("word", "", "sub\\*.txt", false, CancellationToken.None).Hits.Select(h => h.RelativePath));
        Assert.Equal(new[] { @"src\deep\x.cs" }, _files.Find("src/deep/*.cs", "").Paths);
    }

    [Fact]
    public void FilesPattern_BraceAlternatives_NameAndPathMixed()
    {
        // 2026-09-24: a model's *.{png,jpg,jpeg,gif,webp} found nothing — braces were taken literally.
        Put("a.png", "x");
        Put("b.JPG", "x");
        Put("c.txt", "x");
        Put(@"d\e.webp", "x");
        Put(@"d\f.txt", "x");

        Assert.Equal(new[] { "a.png", "b.JPG", @"d\e.webp" }, _files.Find("*.{png,jpg,jpeg,gif,webp}", "").Paths);
        Assert.Equal(new[] { "c.txt", @"d\e.webp" }, _files.Find("{d/*.webp,c.*}", "").Paths);
        Assert.Equal(new[] { @"d\e.webp", @"d\f.txt" }, _files.Find("d/*.{webp,txt}", "").Paths);
        Assert.Equal(new[] { "c.txt", @"d\f.txt" }, _files.Search("x", "", "*.{txt,md}", false, CancellationToken.None).Hits.Select(h => h.RelativePath));
        Assert.Empty(_files.Find("*.{png", "").Paths);   // an unclosed brace stays literal
    }

    [Fact]
    public void Search_Context_RidesEachHit_ClippedLikeTheHit_AndCountsForNothing()
    {
        Put("a.txt", "l1\nl2 needle\nl3\nl4\nl5\nl6 needle\nl7");

        var two = _files.Search("needle", "", null, false, CancellationToken.None, context: 2);
        Assert.Equal(2, two.Hits.Count);
        Assert.Equal(["l1"], two.Hits[0].Before);
        Assert.Equal(["l3", "l4"], two.Hits[0].After);
        Assert.Equal(["l4", "l5"], two.Hits[1].Before);
        Assert.Equal(["l7"], two.Hits[1].After);
        var none = _files.Search("needle", "", null, false, CancellationToken.None);
        Assert.Empty(none.Hits[0].Before);
        Assert.Empty(none.Hits[0].After);
        var clamped = _files.Search("needle", "", null, false, CancellationToken.None, context: 99);   // clamped to MaxSearchContext
        Assert.Equal(["l1"], clamped.Hits[0].Before);
        Assert.Equal(["l3", "l4", "l5", "l6 needle", "l7"], clamped.Hits[0].After);
        Assert.Empty(_files.Search("needle", "", null, false, CancellationToken.None, context: -3).Hits[0].Before);   // clamped to none

        // Context lines are clipped like a hit and never count toward the cap.
        Put("long.txt", "needle\n" + new string('y', WorkingDirectory.MaxLineChars + 20));
        var clipped = _files.Search("needle", "", "long.txt", false, CancellationToken.None, context: 1);
        Assert.Equal(WorkingDirectory.MaxLineChars, clipped.Hits[0].After[0].Length);
        Assert.EndsWith("…", clipped.Hits[0].After[0]);
        Put("many.txt", string.Join('\n', Enumerable.Repeat("needle", WorkingDirectory.DefaultSearchLimit)));
        var full = _files.Search("needle", "", "many.txt", false, CancellationToken.None, context: 3);
        Assert.Equal(WorkingDirectory.DefaultSearchLimit, full.Hits.Count);
        Assert.False(full.Truncated);
    }

    [Fact]
    public void Search_Regex_AndABadPattern()
    {
        Put("a.txt", "cat\ncot\ncut\ndog");

        var result = _files.Search("c.t", "", null, regex: true, CancellationToken.None);
        Assert.Equal(new[] { 1, 2, 3 }, result.Hits.Select(h => h.Line));
        Assert.Empty(_files.Search("c.t", "", null, regex: false, CancellationToken.None).Hits);

        var bad = _files.Search("c(t", "", null, regex: true, CancellationToken.None);
        Assert.Equal(FileOutcome.BadPattern, bad.Outcome);
        Assert.NotEmpty(bad.Detail);
    }

    [Fact]
    public void Search_StopsAtMaxMatches_BeforeTheWalkEnds()
    {
        for (int i = 0; i < 500; i++)
        {
            Put($"f{i:000}.txt", "needle one\nneedle two");
        }

        var result = _files.Search("needle", "", null, false, CancellationToken.None);

        Assert.Equal(WorkingDirectory.DefaultSearchLimit, result.Hits.Count);
        Assert.True(result.Truncated);
        Assert.True(result.FilesSearched < 500, $"searched {result.FilesSearched}");
        Assert.Equal(result.Hits.OrderBy(h => h.RelativePath, StringComparer.OrdinalIgnoreCase).ThenBy(h => h.Line), result.Hits);

        // limit (2026-09-19): the model's cap, clamped to MaxResultLimit (200 until 2026-10-01; the tools clamp to the setting first).
        Assert.Equal(7, _files.Search("needle", "", null, false, CancellationToken.None, limit: 7).Hits.Count);
        Assert.Equal(300, _files.Search("needle", "", null, false, CancellationToken.None, limit: 300).Hits.Count);
        Assert.Equal(1000, _files.Search("needle", "", null, false, CancellationToken.None, limit: 99_999).Hits.Count);
        Assert.Equal(Settings.AppSettingsData.MaxFileSearchMaxResults, WorkingDirectory.MaxResultLimit);
        Assert.Single(_files.Search("needle", "", null, false, CancellationToken.None, limit: 0).Hits);
    }

    [Fact]
    public void Search_OutputFiles_CountsPerFile_StopsAtLimit()
    {
        Put("a.txt", "needle one\nneedle two\nnothing");
        Put(@"sub\b.txt", "Needle");
        Put("c.txt", "nothing");

        var result = _files.Search("needle", "", null, false, CancellationToken.None, output: SearchOutput.Files);
        Assert.Equal(FileOutcome.Ok, result.Outcome);
        Assert.Empty(result.Hits);
        Assert.Equal(new[] { new SearchFileCount("a.txt", 2), new SearchFileCount(@"sub\b.txt", 1) }, result.Files);
        Assert.Equal((2, 3), (result.FilesMatched, result.FilesSearched));
        Assert.False(result.Truncated);

        var cut = _files.Search("needle", "", null, false, CancellationToken.None, limit: 1, output: SearchOutput.Files);
        Assert.Single(cut.Files);
        Assert.True(cut.Truncated);
        Assert.Equal(1, cut.FilesMatched);
        Assert.Empty(_files.Search("needle", "", null, false, CancellationToken.None, output: SearchOutput.Content).Files);
    }

    [Fact]
    public void Search_ClipsLongLines()
    {
        Put("a.txt", new string('a', 300) + " needle");
        var hit = Assert.Single(_files.Search("needle", "", null, false, CancellationToken.None).Hits);
        Assert.Equal(WorkingDirectory.MaxLineChars, hit.Text.Length);
        Assert.EndsWith("…", hit.Text);
    }

    [Fact]
    public void Search_Cancelled_Throws()
    {
        Put("a.txt", "needle");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => _files.Search("needle", "", null, false, cts.Token));
    }

    // ---- recent ----

    [Fact]
    public void Recent_NewestFirst_Capped()
    {
        var t0 = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 5; i++)
        {
            string full = Put($"f{i}.txt", new string('x', i));
            File.SetLastWriteTimeUtc(full, t0.AddMinutes(i));
        }

        string sub = Put(@"sub\g.txt", "sub");
        File.SetLastWriteTimeUtc(sub, t0.AddMinutes(10));

        var result = _files.Recent("", 3);

        Assert.Equal(FileOutcome.Ok, result.Outcome);
        Assert.Equal(new[] { @"sub\g.txt", "f4.txt", "f3.txt" }, result.Entries.Select(e => e.RelativePath));
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 5, 10, 0, TimeSpan.FromHours(-7)), result.Entries[0].Modified);   // the test zone
        Assert.Equal(3, result.Entries[0].Length);
        Assert.Equal(6, _files.Recent("", 99).Entries.Count);   // count clamped to the cap, which is above 6
        Assert.Single(_files.Recent("sub", 1).Entries);
        Assert.Empty(_files.Recent("sub", 0).Entries.Skip(1));
        Assert.Equal(FileOutcome.Missing, _files.Recent("nope", 1).Outcome);
    }

    // ---- info ----

    [Fact]
    public void Info_AFile_HasSizeLinesWordsAndTime()
    {
        string full = Put("notes.txt", "one two\nthree\n");
        File.SetLastWriteTimeUtc(full, new DateTime(2026, 9, 11, 21, 5, 0, DateTimeKind.Utc));

        var result = _files.Info("notes.txt");

        Assert.Equal(FileOutcome.Ok, result.Outcome);
        Assert.False(result.IsDirectory);
        Assert.Equal("notes.txt", result.Relative);
        Assert.Equal(14, result.Bytes);
        Assert.Equal(2, result.Lines);
        Assert.Equal(3, result.Words);
        Assert.Equal(new DateTimeOffset(2026, 9, 11, 14, 5, 0, TimeSpan.FromHours(-7)), result.Modified);

        Put("bin.dat", "a\0b");
        var binary = _files.Info("bin.dat");
        Assert.Null(binary.Lines);
        Assert.Null(binary.Words);
        Assert.Equal(FileOutcome.Missing, _files.Info("nope").Outcome);
        Assert.Equal(FileOutcome.OutsideRoot, _files.Info("..").Outcome);
    }

    [Fact]
    public void Info_AFolder_CountsFilesFoldersAndBytes()
    {
        Put(@"docs\a.txt", "12345");
        Put(@"docs\sub\b.txt", "123");
        Put(@"docs\sub\deep\c.txt", "1");

        var result = _files.Info("docs");
        Assert.True(result.IsDirectory);
        Assert.Equal(@"docs\", result.Relative);
        Assert.Equal(3, result.Files);
        Assert.Equal(2, result.Folders);
        Assert.Equal(9, result.Bytes);
        Assert.False(result.Truncated);

        var root = _files.Info("");
        Assert.Equal(3, root.Files);
        Assert.Equal(3, root.Folders);
    }

    // ---- read ----

    [Fact]
    public void ReadText_WholeFile_Windows_AndTheTail()
    {
        Put("n.txt", "l1\r\nl2\nl3\nl4\nl5\n");

        var all = _files.ReadText("n.txt", null, null);
        Assert.Equal(FileOutcome.Ok, all.Outcome);
        Assert.Equal("l1\nl2\nl3\nl4\nl5", all.Text);
        Assert.Equal((5, 1, 5, false), (all.TotalLines, all.FromLine, all.ToLine, all.Truncated));

        var window = _files.ReadText("n.txt", 2, 2);
        Assert.Equal("l2\nl3", window.Text);
        Assert.Equal((2, 3), (window.FromLine, window.ToLine));

        var tail = _files.ReadText("n.txt", -2, null);
        Assert.Equal("l4\nl5", tail.Text);
        Assert.Equal((4, 5), (tail.FromLine, tail.ToLine));

        var tailCapped = _files.ReadText("n.txt", -3, 1);
        Assert.Equal("l3", tailCapped.Text);

        var beyond = _files.ReadText("n.txt", 9, null);
        Assert.Equal("", beyond.Text);
        Assert.Equal((5, 9, 8), (beyond.TotalLines, beyond.FromLine, beyond.ToLine));

        var farTail = _files.ReadText("n.txt", -99, null);
        Assert.Equal(1, farTail.FromLine);
    }

    [Fact]
    public void ReadText_Empty_Missing_Directory_Binary_TooBig_Outside()
    {
        Put("empty.txt", "");
        Put("bin.dat", "x\0y");
        Directory.CreateDirectory(Full("dir"));
        using (var big = new FileStream(Full("big.log"), FileMode.Create))
        {
            big.SetLength(WorkingDirectory.MaxReadFileBytes + 1);
        }

        var empty = _files.ReadText("empty.txt", null, null);
        Assert.Equal(FileOutcome.Ok, empty.Outcome);
        Assert.Equal(0, empty.TotalLines);
        Assert.Equal("", empty.Text);
        Assert.Equal(FileOutcome.Missing, _files.ReadText("nope.txt", null, null).Outcome);
        Assert.Equal(FileOutcome.IsDirectory, _files.ReadText("dir", null, null).Outcome);
        Assert.Equal(@"dir\", _files.ReadText("dir", null, null).Relative);
        Assert.Equal(FileOutcome.NotText, _files.ReadText("bin.dat", null, null).Outcome);
        Assert.Equal(FileOutcome.TooBig, _files.ReadText("big.log", null, null).Outcome);
        Assert.Equal(FileOutcome.OutsideRoot, _files.ReadText(@"..\x", null, null).Outcome);
    }

    [Fact]
    public void ReadText_Search_Info_DropABom()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Full("bom.txt"), "first\nsecond", new UTF8Encoding(true));

        var read = _files.ReadText("bom.txt", null, null);
        Assert.Equal("first\nsecond", read.Text);
        Assert.Equal("first", _files.Search("first", "", null, false, CancellationToken.None).Hits.Single().Text);
        Assert.Equal(2, _files.Info("bom.txt").Words);
        Assert.False(WorkingDirectory.Decode("plain"u8.ToArray(), out bool bom).StartsWith((char)0xFEFF));
        Assert.False(bom);
        Assert.Equal("x", WorkingDirectory.Decode([0xEF, 0xBB, 0xBF, (byte)'x'], out bom));
        Assert.True(bom);
    }

    [Fact]
    public void ReadText_CutsAtMaxReadChars_WholeLines()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 2000; i++)
        {
            sb.Append("line ").Append(i).Append(' ', 20).Append('\n');
        }

        Put("long.txt", sb.ToString());

        var result = _files.ReadText("long.txt", null, null);
        Assert.True(result.Truncated);
        Assert.True(result.Text.Length <= WorkingDirectory.MaxReadChars);
        Assert.True(result.ToLine < result.TotalLines);
        Assert.Equal(2000, result.TotalLines);
        Assert.EndsWith(" ", result.Text);   // whole lines: the cut is between lines, never inside one

        var next = _files.ReadText("long.txt", result.ToLine + 1, 5);
        Assert.StartsWith("line " + result.ToLine, next.Text, StringComparison.Ordinal);
    }

    // ---- write ----

    [Fact]
    public void WriteText_Creates_RefusesToReplace_ThenReplacesWithOverwrite()
    {
        var first = _files.WriteText(@"docs\notes.txt", "hello", overwrite: false);
        Assert.Equal(FileOutcome.Ok, first.Outcome);
        Assert.Equal(@"docs\notes.txt", first.Relative);
        Assert.Equal(5, first.Bytes);
        Assert.False(first.Replaced);
        Assert.Equal("hello", File.ReadAllText(Full(@"docs\notes.txt")));
        Assert.Equal(new byte[] { (byte)'h' }, File.ReadAllBytes(Full(@"docs\notes.txt")).Take(1));   // no BOM

        var refused = _files.WriteText(@"docs\notes.txt", "bye", overwrite: false);
        Assert.Equal(FileOutcome.Exists, refused.Outcome);
        Assert.Equal("hello", File.ReadAllText(Full(@"docs\notes.txt")));

        var replaced = _files.WriteText("docs/notes.txt", "bye", overwrite: true);
        Assert.Equal(FileOutcome.Ok, replaced.Outcome);
        Assert.True(replaced.Replaced);
        Assert.Equal("bye", File.ReadAllText(Full(@"docs\notes.txt")));
        Assert.Empty(Directory.GetFiles(Full("docs"), "*.tmp"));
    }

    [Fact]
    public void WriteText_Refusals()
    {
        Directory.CreateDirectory(Full("dir"));
        Assert.Equal(FileOutcome.IsDirectory, _files.WriteText("dir", "x", true).Outcome);
        Assert.Equal(FileOutcome.TooLong, _files.WriteText("x.txt", new string('x', WorkingDirectory.MaxWriteChars + 1), true).Outcome);
        Assert.Equal(FileOutcome.OutsideRoot, _files.WriteText(@"..\x.txt", "x", true).Outcome);
        Assert.False(File.Exists(Full("x.txt")));
    }

    [Fact]
    public void WriteBytes_Creates_RefusesToReplace_ThenReplacesWithOverwrite_AndIsExistingDirectoryAnswers()
    {
        // 2026-09-18: the download's sink — WriteText's guards over bytes as they are, no length cap of its own.
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0];
        var first = _files.WriteBytes(@"img\cat.png", png, overwrite: false);
        Assert.Equal(FileOutcome.Ok, first.Outcome);
        Assert.Equal(@"img\cat.png", first.Relative);
        Assert.Equal(5, first.Bytes);
        Assert.False(first.Replaced);
        Assert.Equal(png, File.ReadAllBytes(Full(@"img\cat.png")));

        Assert.Equal(FileOutcome.Exists, _files.WriteBytes(@"img\cat.png", [1], overwrite: false).Outcome);
        Assert.Equal(png, File.ReadAllBytes(Full(@"img\cat.png")));

        var replaced = _files.WriteBytes("img/cat.png", [1, 2], overwrite: true);
        Assert.Equal(FileOutcome.Ok, replaced.Outcome);
        Assert.True(replaced.Replaced);
        Assert.Equal(new byte[] { 1, 2 }, File.ReadAllBytes(Full(@"img\cat.png")));
        Assert.False(Directory.Exists(Full(".trash")));   // nothing kept (File safe edits went 2026-10-01)
        Assert.Empty(Directory.GetFiles(Full("img"), "*.tmp"));

        Directory.CreateDirectory(Full("dir"));
        Assert.Equal(FileOutcome.IsDirectory, _files.WriteBytes("dir", [1], true).Outcome);
        Assert.Equal(FileOutcome.OutsideRoot, _files.WriteBytes(@"..\x.bin", [1], true).Outcome);

        Assert.True(_files.IsExistingDirectory("dir"));
        Assert.True(_files.IsExistingDirectory(""));
        Assert.True(_files.IsExistingDirectory("img/"));
        Assert.False(_files.IsExistingDirectory("img/cat.png"));
        Assert.False(_files.IsExistingDirectory("nowhere"));
        Assert.False(_files.IsExistingDirectory(".."));
    }

    [Fact]
    public void BeginWrite_GuardsAsWriteBytes_WritesASibling_CommitsInPlace_OrLeavesNothing()
    {
        // 2026-10-01, download_file streamed to disk: WriteBytes' guards up front, the bytes in a temporary sibling, the target only at the commit.
        Assert.Equal(FileOutcome.OutsideRoot, _files.BeginWrite(@"..\x.bin", true, out var none).Outcome);
        Assert.Null(none);
        Directory.CreateDirectory(Full("dir"));
        Assert.Equal(FileOutcome.IsDirectory, _files.BeginWrite("dir", true, out none).Outcome);
        Assert.Null(none);

        var begun = _files.BeginWrite(@"deep\new\a.bin", overwrite: false, out var pending);
        Assert.Equal(FileOutcome.Ok, begun.Outcome);
        using (pending)
        {
            pending!.Stream.Write([1, 2, 3]);
            Assert.False(File.Exists(Full(@"deep\new\a.bin")));   // not yet: only the sibling
            Assert.Single(Directory.GetFiles(Full(@"deep\new"), "*.tmp"));
            var done = pending.Commit();
            Assert.Equal(new WriteResult(FileOutcome.Ok, @"deep\new\a.bin", 3, false), done);
        }

        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Full(@"deep\new\a.bin")));
        Assert.Empty(Directory.GetFiles(Full(@"deep\new"), "*.tmp"));

        // A file there: refused without overwrite; with it, replaced at the commit.
        Assert.Equal(FileOutcome.Exists, _files.BeginWrite(@"deep\new\a.bin", false, out none).Outcome);
        using (var again = Begin(@"deep\new\a.bin", overwrite: true))
        {
            again.Stream.Write([9]);
            Assert.Equal(new WriteResult(FileOutcome.Ok, @"deep\new\a.bin", 1, true), again.Commit());
        }

        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(Full(@"deep\new\a.bin")));

        // Disposed without a commit (a cancel, a stall, an oversize): the sibling goes and the target is untouched.
        using (var dropped = Begin(@"deep\new\a.bin", overwrite: true))
        {
            dropped.Stream.Write([7, 7]);
        }

        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(Full(@"deep\new\a.bin")));
        Assert.Empty(Directory.GetFiles(Full(@"deep\new"), "*.tmp"));

        // A file that appeared meanwhile is not clobbered without overwrite.
        using (var raced = Begin("late.bin", overwrite: false))
        {
            File.WriteAllBytes(Full("late.bin"), [5]);
            raced.Stream.Write([6]);
            Assert.Equal(FileOutcome.Exists, raced.Commit().Outcome);
        }

        Assert.Equal(new byte[] { 5 }, File.ReadAllBytes(Full("late.bin")));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    private PendingWrite Begin(string relative, bool overwrite)
    {
        Assert.Equal(FileOutcome.Ok, _files.BeginWrite(relative, overwrite, out var pending).Outcome);
        return pending!;
    }

    [Fact]
    public void AppendText_AddsOnANewLine_CreatesWhenMissing()
    {
        var created = _files.AppendText("log.txt", "first");
        Assert.Equal(FileOutcome.Ok, created.Outcome);
        Assert.False(created.Replaced);
        Assert.Equal(5, created.Bytes);
        Assert.Equal("first", File.ReadAllText(Full("log.txt")));

        var appended = _files.AppendText("log.txt", "second");
        Assert.True(appended.Replaced);
        Assert.Equal(7, appended.Bytes);   // the separating newline counts
        Assert.Equal("first\nsecond", File.ReadAllText(Full("log.txt")));

        File.WriteAllText(Full("log.txt"), "ends\n");
        _files.AppendText("log.txt", "third");
        Assert.Equal("ends\nthird", File.ReadAllText(Full("log.txt")));

        Directory.CreateDirectory(Full("dir"));
        Assert.Equal(FileOutcome.IsDirectory, _files.AppendText("dir", "x").Outcome);
        Assert.Equal(FileOutcome.TooLong, _files.AppendText("log.txt", new string('x', WorkingDirectory.MaxWriteChars + 1)).Outcome);
    }

    [Fact]
    public void EditText_ReplacesTheOneOccurrence_KeepsBomAndLineEndings()
    {
        Put("a.txt", "one\r\ntwo\r\nthree");
        var result = _files.EditText("a.txt", "two", "2");
        Assert.Equal(FileOutcome.Ok, result.Outcome);
        Assert.Equal(2, result.Line);
        Assert.Equal("one\r\n2\r\nthree", File.ReadAllText(Full("a.txt")));

        File.WriteAllText(Full("bom.txt"), "x = 1", new UTF8Encoding(true));
        Assert.Equal(FileOutcome.Ok, _files.EditText("bom.txt", "1", "2").Outcome);
        byte[] bytes = File.ReadAllBytes(Full("bom.txt"));
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3));
        Assert.Equal("x = 2", Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));

        Assert.Equal(FileOutcome.Ok, _files.EditText("a.txt", "2\r\n", "").Outcome);   // removal
        Assert.Equal("one\r\nthree", File.ReadAllText(Full("a.txt")));
    }

    [Fact]
    public void EditText_MatchesAcrossLineEndings_AndWritesTheFilesOwn()
    {
        // The bug (2026-09-17): a multi-line old_text copied from a read (LF-joined) never matched a CRLF file.
        Put("a.txt", "one\r\ntwo\r\nthree\r\nfour");
        var result = _files.EditText("a.txt", "two\nthree", "2\n3\n3b");
        Assert.Equal(FileOutcome.Ok, result.Outcome);
        Assert.Equal((2, 2, 4, 5), (result.Line, result.NewFrom, result.NewTo, result.TotalLines));
        Assert.Equal("one\r\n2\r\n3\r\n3b\r\nfour", File.ReadAllText(Full("a.txt")));
        // The region: two lines each side of the new text, numbered from RegionFrom.
        Assert.Equal(1, result.RegionFrom);
        Assert.Equal(["one", "2", "3", "3b", "four"], result.Region);

        // A CRLF old_text against an LF file matches too; the file stays LF.
        Put("b.txt", "one\ntwo\n");
        Assert.Equal(FileOutcome.Ok, _files.EditText("b.txt", "one\r\ntwo", "x").Outcome);
        Assert.Equal("x\n", File.ReadAllText(Full("b.txt")));

        // A mixed file comes out uniform, on its first line break.
        Put("mixed.txt", "a\r\nb\nc\rd");
        Assert.Equal(FileOutcome.Ok, _files.EditText("mixed.txt", "b", "B").Outcome);
        Assert.Equal("a\r\nB\r\nc\r\nd", File.ReadAllText(Full("mixed.txt")));

        // replace_all: every occurrence, the lines listed, no region.
        Put("all.txt", "x\ny x\nz\nx");
        var all = _files.EditText("all.txt", "x", "Q", replaceAll: true);
        Assert.Equal(FileOutcome.Ok, all.Outcome);
        Assert.Equal(3, all.Count);
        Assert.Equal([1, 2, 4], all.Lines);
        Assert.Empty(all.Region);
        Assert.Equal("Q\ny Q\nz\nQ", File.ReadAllText(Full("all.txt")));
        Assert.Equal(FileOutcome.EditAmbiguous, _files.EditText("all.txt", "Q", "x").Outcome);   // without the flag, as before
        Assert.False(Directory.Exists(Full(".trash")));   // an edit keeps no copy (File safe edits went 2026-10-01)
    }

    [Fact]
    public void NewlineHelpers_ArePinned()
    {
        Assert.Equal("a\nb\nc\nd", WorkingDirectory.NormalizeNewlines("a\r\nb\nc\rd"));
        Assert.Same("plain", WorkingDirectory.NormalizeNewlines("plain"));
        Assert.Equal("\r\n", WorkingDirectory.DetectLineEnding("a\r\nb\nc"));
        Assert.Equal("\n", WorkingDirectory.DetectLineEnding("a\nb\r\nc"));
        Assert.Equal("\r", WorkingDirectory.DetectLineEnding("a\rb"));
        Assert.Equal("\n", WorkingDirectory.DetectLineEnding("none"));
        Assert.Equal("CRLF", WorkingDirectory.DescribeLineEnding("a\r\nb\r\n"));
        Assert.Equal("LF", WorkingDirectory.DescribeLineEnding("a\nb"));
        Assert.Equal("CR", WorkingDirectory.DescribeLineEnding("a\rb"));
        Assert.Equal("mixed", WorkingDirectory.DescribeLineEnding("a\r\nb\nc"));
        Assert.Equal("", WorkingDirectory.DescribeLineEnding("one line"));
        Assert.Equal(2, WorkingDirectory.EditContextLines);
        Assert.Equal(5, WorkingDirectory.MaxSearchContext);
        Assert.Equal(60, WorkingDirectory.MaxEditRegionLines);
        Assert.Equal(80, WorkingDirectory.MaxQuotedChars);
    }

    [Fact]
    public void EditText_Refusals()
    {
        Put("a.txt", "x x");
        Put("bin.dat", "x\0");
        Directory.CreateDirectory(Full("dir"));

        var ambiguous = _files.EditText("a.txt", "x", "y");
        Assert.Equal(FileOutcome.EditAmbiguous, ambiguous.Outcome);
        Assert.Equal(2, ambiguous.Count);
        Assert.Equal(FileOutcome.EditNotFound, _files.EditText("a.txt", "z", "y").Outcome);
        Assert.Equal(FileOutcome.Empty, _files.EditText("a.txt", "", "y").Outcome);
        Assert.Equal(FileOutcome.Missing, _files.EditText("nope.txt", "x", "y").Outcome);
        Assert.Equal(FileOutcome.IsDirectory, _files.EditText("dir", "x", "y").Outcome);
        Assert.Equal(FileOutcome.NotText, _files.EditText("bin.dat", "x", "y").Outcome);
        Assert.Equal(FileOutcome.TooLong, _files.EditText("a.txt", "x x", new string('y', WorkingDirectory.MaxWriteChars + 1)).Outcome);
        Assert.Equal("x x", File.ReadAllText(Full("a.txt")));

        // The matcher's outcomes map one to one (2026-09-19): the locations ride an ambiguous refusal, the strategy every result.
        Assert.Equal(new[] { new MatchLocation(1, "x x"), new MatchLocation(1, "x x") }, ambiguous.Locations);
        Assert.Equal(FileOutcome.Empty, _files.EditText("a.txt", " \t ", "y").Outcome);
        Assert.Equal(FileOutcome.Same, _files.EditText("a.txt", "x", "x").Outcome);
        Assert.Equal(FileOutcome.Same, _files.EditText("nope.txt", "x", "x").Outcome);   // ahead of the load, as before
        Put("done.txt", "hello wide world\n");
        Assert.Equal(FileOutcome.AlreadyApplied, _files.EditText("done.txt", "goodbye cruel world", "hello wide world").Outcome);
        Assert.Equal("hello wide world\n", File.ReadAllText(Full("done.txt")));
        Put("loose.txt", "  a\n a\n");
        var loose = _files.EditText("loose.txt", "    a", "b");
        Assert.Equal((FileOutcome.EditAmbiguous, MatchStrategy.LineTrimmed, 2), (loose.Outcome, loose.Strategy, loose.Count));
        Put("anchor.txt", "start\nthe middle line here\nend\n\nstart\nthe middle line here\nend\n");
        var approximate = _files.EditText("anchor.txt", "start\nthe muddled line hare\nend", "x", replaceAll: true);
        Assert.Equal((FileOutcome.ApproximateAll, MatchStrategy.BlockAnchor, 2), (approximate.Outcome, approximate.Strategy, approximate.Count));
        Put("quote.txt", "say 'hi'\nnext line\n");
        var drift = _files.EditText("quote.txt", "say \\'hi\\'\nnext line", "say \\'bye\\'\nnext line");
        Assert.Equal((FileOutcome.EscapeDrift, EscapeDrift.QuoteSingle), (drift.Outcome, drift.Drift));
        Assert.Equal("say 'hi'\nnext line\n", File.ReadAllText(Full("quote.txt")));
        var found = _files.EditText("loose.txt", "   a\n a", "b\nb");
        Assert.Equal((FileOutcome.Ok, MatchStrategy.LineTrimmed, 1, 2), (found.Outcome, found.Strategy, found.NewFrom, found.NewTo));
        Assert.Equal("  b\n  b\n", File.ReadAllText(Full("loose.txt")));   // every new line takes the first matched line's indentation
    }

    [Fact]
    public void CreateDirectory_New_Exists_IsAFile_Parents()
    {
        var created = _files.CreateDirectory(@"a\b\c");
        Assert.Equal(FileOutcome.Ok, created.Outcome);
        Assert.Equal(@"a\b\c\", created.Relative);
        Assert.True(Directory.Exists(Full(@"a\b\c")));
        Assert.Equal(FileOutcome.Exists, _files.CreateDirectory(@"a\b").Outcome);
        Assert.Equal(FileOutcome.Exists, _files.CreateDirectory("").Outcome);
        Put("f.txt", "");
        Assert.Equal(FileOutcome.IsAFile, _files.CreateDirectory("f.txt").Outcome);
    }

    // ---- move / copy ----

    [Fact]
    public void Move_RenamesAFile_MovesAFile_AndSaysWhich()
    {
        Put("a.txt", "a");

        var renamed = _files.Move("a.txt", "b.txt", overwrite: false);
        Assert.Equal(FileOutcome.Ok, renamed.Outcome);
        Assert.True(renamed.Renamed);
        Assert.False(renamed.IsDirectory);
        Assert.Equal(("a.txt", "b.txt"), (renamed.From, renamed.To));
        Assert.True(File.Exists(Full("b.txt")));

        var moved = _files.Move("b.txt", @"docs\b.txt", overwrite: false);
        Assert.Equal(FileOutcome.Ok, moved.Outcome);
        Assert.False(moved.Renamed);
        Assert.True(File.Exists(Full(@"docs\b.txt")));   // the parent was created
    }

    [Fact]
    public void Move_RenamesAFolder_RefusesIntoItself_AndTheRoot()
    {
        Put(@"drafts\x.txt", "");

        var renamed = _files.Move("drafts", "poems", false);
        Assert.Equal(FileOutcome.Ok, renamed.Outcome);
        Assert.True(renamed.IsDirectory);
        Assert.True(renamed.Renamed);
        Assert.Equal((@"drafts\", @"poems\"), (renamed.From, renamed.To));
        Assert.True(File.Exists(Full(@"poems\x.txt")));

        Assert.Equal(FileOutcome.IntoItself, _files.Move("poems", @"poems\inner", false).Outcome);
        Assert.Equal(FileOutcome.Exists, _files.Move("", "elsewhere", false).Outcome);
        Assert.Equal(FileOutcome.Missing, _files.Move("nope", "x", false).Outcome);
        Assert.Equal(FileOutcome.Exists, _files.Move("poems", "poems", false).Outcome);
    }

    [Fact]
    public void Move_RefusesAnOccupiedDestination_UnlessOverwrite_AFileReplacedInPlace_AFolderRefused()
    {
        Put("a.txt", "a");
        Put("b.txt", "b");
        Assert.Equal(FileOutcome.Exists, _files.Move("a.txt", "b.txt", false).Outcome);
        Assert.Equal("b", File.ReadAllText(Full("b.txt")));

        // A file in the way: replaced in place, nothing kept (2026-09-20; the .trash copy under File safe edits went 2026-10-01).
        Assert.Equal(FileOutcome.Ok, _files.Move("a.txt", "b.txt", true).Outcome);
        Assert.Equal("a", File.ReadAllText(Full("b.txt")));
        Assert.False(File.Exists(Full("a.txt")));
        Assert.False(Directory.Exists(Full(".trash")));

        // A folder in the way of a folder: refused, nothing destroyed.
        Put(@"old\keep.txt", "old");
        Put(@"new\fresh.txt", "new");
        var refused = _files.Move("new", "old", true);
        Assert.Equal((FileOutcome.FolderInTheWay, @"new\", @"old\"), (refused.Outcome, refused.From, refused.To));
        Assert.True(File.Exists(Full(@"old\keep.txt")));
        Assert.True(File.Exists(Full(@"new\fresh.txt")));

        // A folder in the way of a file: the same rule; a file in the way of a folder is replaced in place.
        Put("d.txt", "d");
        Directory.CreateDirectory(Full("spot"));
        Assert.Equal(FileOutcome.FolderInTheWay, _files.Move("d.txt", "spot", true).Outcome);
        Assert.True(File.Exists(Full("d.txt")));
        Put(@"dir\x.txt", "x");
        Put("flat.txt", "flat");
        Assert.Equal(FileOutcome.Ok, _files.Move("dir", "flat.txt", true).Outcome);
        Assert.True(File.Exists(Full(@"flat.txt\x.txt")));
        Assert.Equal(FileText.FolderInTheWay(@"old\"), FileText.Moved(refused));
        Assert.False(Directory.Exists(Full(".trash")));
    }

    [Fact]
    public void Copy_WithOverwrite_FollowsTheMoveRule_ButAFolderOverAFolderMerges()
    {
        Put("a.txt", "a");
        Put("b.txt", "b");
        Assert.Equal(FileOutcome.Ok, _files.Copy("a.txt", "b.txt", true).Outcome);
        Assert.Equal("a", File.ReadAllText(Full("b.txt")));

        Directory.CreateDirectory(Full("spot"));
        Assert.Equal(FileOutcome.FolderInTheWay, _files.Copy("a.txt", "spot", true).Outcome);
        Assert.Equal(FileText.FolderInTheWay(@"spot\"), FileText.Copied(_files.Copy("a.txt", "spot", true)));
        Assert.True(Directory.Exists(Full("spot")));

        // A folder copied over a folder merges: nothing is destroyed by a merge.
        Put(@"src\new.txt", "new");
        Put(@"dst\old.txt", "old");
        Assert.Equal(FileOutcome.Ok, _files.Copy("src", "dst", true).Outcome);
        Assert.True(File.Exists(Full(@"dst\old.txt")) && File.Exists(Full(@"dst\new.txt")));
        Assert.False(Directory.Exists(Full(".trash")));
    }

    [Fact]
    public void Copy_AFile_AndAFolderRecursively()
    {
        Put(@"src\a.txt", "a");
        Put(@"src\sub\b.txt", "b");

        var file = _files.Copy(@"src\a.txt", "a2.txt", false);
        Assert.Equal(FileOutcome.Ok, file.Outcome);
        Assert.Equal("a", File.ReadAllText(Full("a2.txt")));
        Assert.True(File.Exists(Full(@"src\a.txt")));

        var folder = _files.Copy("src", "dst", false);
        Assert.Equal(FileOutcome.Ok, folder.Outcome);
        Assert.True(folder.IsDirectory);
        Assert.Equal("b", File.ReadAllText(Full(@"dst\sub\b.txt")));

        Assert.Equal(FileOutcome.Exists, _files.Copy("src", "dst", false).Outcome);
        Put(@"src\c.txt", "c");
        Assert.Equal(FileOutcome.Ok, _files.Copy("src", "dst", true).Outcome);   // merged
        Assert.True(File.Exists(Full(@"dst\c.txt")));
        Assert.Equal(FileOutcome.IntoItself, _files.Copy("src", @"src\copy", false).Outcome);
    }

    // ---- delete ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Delete_NeverTouchesGit_ItsContents_OrAFolderHoldingIt(bool nested)
    {
        // 2026-09-23, the user's call: .git, anything in it, and a folder with a .git anywhere under it (a folder, or a worktree's
        // .git file) are refused, in any case; nothing is removed. Run at the root and one folder down.
        string at = nested ? "outer" : "";
        string P(string relative) => at.Length == 0 ? relative : Path.Combine(at, relative);
        Put(P(@".git\config"), "[core]");
        Put(P(@"repo\sub\.git\HEAD"), "ref: refs/heads/main");
        Put(P(@"repo\readme.md"), "hi");
        Put(P(@"wt\.git"), "gitdir: ../.git/worktrees/wt");
        Put(P(@"plain\a.txt"), "a");

        foreach (string path in new[] { ".git", @".git\config", @".GIT\config", "repo", @"repo\sub", @"repo\sub\.git", @"repo\sub\.git\HEAD", "wt", @"wt\.git" })
        {
            Assert.Equal(FileOutcome.GitProtected, _files.Delete(P(path)).Outcome);
        }

        Assert.True(File.Exists(Full(P(@".git\config"))));
        Assert.True(File.Exists(Full(P(@"repo\sub\.git\HEAD"))));
        Assert.True(File.Exists(Full(P(@"wt\.git"))));

        Assert.Equal(FileOutcome.Ok, _files.Delete(P(@"repo\readme.md")).Outcome);   // a file beside a .git is the user's own
        Assert.Equal(FileOutcome.Ok, _files.Delete(P("plain")).Outcome);
        Assert.Equal($@"Error: '{P(@"repo\")}' is or holds a .git folder, which delete never removes", FileText.Deleted(_files.Delete(P("repo"))));
        Assert.True(WorkingDirectory.IsGitPath(@"a\.Git\b"));
        Assert.False(WorkingDirectory.IsGitPath(@"a\.github\b"));
        Assert.False(WorkingDirectory.IsGitPath(".gitignore"));
    }

    [Fact]
    public void Delete_RemovesForGood_AFolderWithEverythingInIt_TheGuardsStand()
    {
        // A file goes for good, a folder with everything in it — the one recursive delete in the sandbox (in place under File safe edits
        // off since 2026-09-20, always since 2026-10-01, when that setting and its .trash went, the user's call); the root and a missing entry are refused.
        Put(@"docs\notes.txt", "gone");
        Put(@"proj\sub\a.txt", "v1");
        Put(@"proj\b.txt", "v2");

        var file = _files.Delete(@"docs\notes.txt");
        Assert.Equal(FileOutcome.Ok, file.Outcome);
        Assert.Equal(@"docs\notes.txt", file.Relative);
        Assert.False(file.IsDirectory);
        Assert.False(File.Exists(Full(@"docs\notes.txt")));
        Assert.True(Directory.Exists(Full("docs")));   // the parent stays
        Assert.False(Directory.Exists(Full(".trash")));

        var folder = _files.Delete("proj");
        Assert.Equal(FileOutcome.Ok, folder.Outcome);
        Assert.Equal(@"proj\", folder.Relative);
        Assert.True(folder.IsDirectory);
        Assert.False(Directory.Exists(Full("proj")));
        Assert.False(Directory.Exists(Full(".trash")));

        Assert.Equal(FileOutcome.Missing, _files.Delete("nope").Outcome);
        Assert.Equal(FileOutcome.IntoItself, _files.Delete("").Outcome);
        // A .trash left from before 2026-10-01 is a folder like any other.
        Put(@".trash\20260911-140530\x.txt", "kept");
        Assert.Equal(FileOutcome.Ok, _files.Delete(@".trash\20260911-140530\x.txt").Outcome);
        Assert.Equal(FileOutcome.Ok, _files.Delete(".trash").Outcome);
        Assert.False(Directory.Exists(Full(".trash")));
        Assert.Equal("Deleted docs\\notes.txt", WorkingDirectory.DeletedLogLine(@"docs\notes.txt"));
    }

    // ---- purge ----

    /// <summary>PurgeFolder (later still on 2026-09-24, <c>/comfy purge</c>): everything under the folder, dot-folders included, the folder kept, nothing beside it touched.</summary>
    [Fact]
    public void PurgeFolder_RemovesEverythingUnderTheFolder_KeepsTheFolder()
    {
        Put("keep.txt", "stays");
        Put(@"comfy_images\a.png", "12345");
        Put(@"comfy_images\.pasted\pasted-1.png", "678");
        File.SetAttributes(Full(@"comfy_images\a.png"), FileAttributes.ReadOnly);

        var result = _files.PurgeFolder("comfy_images");

        Assert.Equal(FileOutcome.Ok, result.Outcome);
        Assert.Equal((2, 1, 8L), (result.Files, result.Folders, result.Bytes));
        Assert.True(Directory.Exists(Full("comfy_images")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Full("comfy_images")));
        Assert.Equal("stays", File.ReadAllText(Full("keep.txt")));
    }

    [Fact]
    public void PurgeFolder_RefusesTheRoot_OutsideAndAFile_MissingIsOk()
    {
        Put("keep.txt", "stays");

        var root = _files.PurgeFolder(".");
        Assert.Equal((FileOutcome.OutsideRoot, WorkingDirectory.PurgeRootRefusal), (root.Outcome, root.Detail));
        Assert.Equal(FileOutcome.OutsideRoot, _files.PurgeFolder("").Outcome);
        Assert.Equal(FileOutcome.OutsideRoot, _files.PurgeFolder(@"sub\..").Outcome);
        Assert.Equal(FileOutcome.OutsideRoot, _files.PurgeFolder("..").Outcome);
        Assert.Equal(FileOutcome.IsAFile, _files.PurgeFolder("keep.txt").Outcome);
        Assert.Equal(FileOutcome.Ok, _files.PurgeFolder("nothing-here").Outcome);
        Assert.Equal("stays", File.ReadAllText(Full("keep.txt")));
    }

    [Fact]
    public void PurgeFolder_NoFolder_IsOkWithZeros_AndCreatesNothing()
    {
        _files.EnsureExists();
        var result = _files.PurgeFolder("out");

        Assert.Equal(FileOutcome.Ok, result.Outcome);
        Assert.Equal((0, 0, 0L), (result.Files, result.Folders, result.Bytes));
        Assert.False(Directory.Exists(Full("out")));
    }

    [Fact]
    public void PurgeFolder_ReadOnlyAndHiddenFiles_GoToo()
    {
        string locked = Put(@"out\locked.txt", "ro");
        string hidden = Put(@"out\dir\hidden.txt", "h");
        File.SetAttributes(locked, FileAttributes.ReadOnly);
        File.SetAttributes(hidden, FileAttributes.Hidden);

        var result = _files.PurgeFolder("out");

        Assert.Equal(FileOutcome.Ok, result.Outcome);
        Assert.Equal(2, result.Files);
        Assert.Equal(1, result.Folders);
        Assert.Equal(3, result.Bytes);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Full("out")));
    }

    // ---- zip / unzip ----

    [Fact]
    public void Zip_AFolder_ThenUnzip_RoundTrips()
    {
        Put(@"docs\a.txt", "alpha");
        Put(@"docs\sub\b.txt", "beta");

        var zipped = _files.Zip("docs", null, false);
        Assert.Equal(FileOutcome.Ok, zipped.Outcome);
        Assert.Equal(@"docs\", zipped.Relative);
        Assert.Equal("docs.zip", zipped.Archive);
        Assert.Equal(2, zipped.Entries);
        Assert.True(zipped.Bytes > 0);
        using (var archive = ZipFile.OpenRead(Full("docs.zip")))
        {
            Assert.Equal(new[] { "docs/a.txt", "docs/sub/b.txt" }, archive.Entries.Select(e => e.FullName).Order());
        }

        Assert.Equal(FileOutcome.Exists, _files.Zip("docs", null, false).Outcome);
        Assert.Equal(FileOutcome.Ok, _files.Zip("docs", null, true).Outcome);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));

        var unzipped = _files.Unzip("docs.zip", "restore", false);
        Assert.Equal(FileOutcome.Ok, unzipped.Outcome);
        Assert.Equal("docs.zip", unzipped.Relative);
        Assert.Equal(@"restore\", unzipped.Archive);
        Assert.Equal(2, unzipped.Entries);
        Assert.Equal("beta", File.ReadAllText(Full(@"restore\docs\sub\b.txt")));

        var defaulted = _files.Unzip("docs.zip", null, false);
        Assert.Equal(FileOutcome.Ok, defaulted.Outcome);
        Assert.Equal(@"docs\", defaulted.Archive);   // next to the archive, named after it — over the existing folder, no clash
        Assert.True(File.Exists(Full(@"docs\docs\a.txt")));
    }

    [Fact]
    public void Zip_AFile_IntoItself_AndTheRoot()
    {
        Put("a.txt", "x");
        var zipped = _files.Zip("a.txt", null, false);
        Assert.Equal("a.zip", zipped.Archive);
        Assert.Equal(1, zipped.Entries);
        Assert.Equal(FileOutcome.Ok, _files.Zip("a.txt", @"out\named.zip", false).Outcome);
        Assert.True(File.Exists(Full(@"out\named.zip")));

        Put(@"d\x.txt", "");
        Assert.Equal(FileOutcome.IntoItself, _files.Zip("d", @"d\d.zip", false).Outcome);
        Assert.Equal(FileOutcome.IntoItself, _files.Zip("", null, false).Outcome);
        Assert.Equal(FileOutcome.Missing, _files.Zip("nope", null, false).Outcome);
    }

    [Fact]
    public void Unzip_AllOrNothing_OnAClash_AndOnZipSlip()
    {
        Put(@"src\a.txt", "a");
        Put(@"src\b.txt", "b");
        _files.Zip("src", "src.zip", false);
        Put(@"dst\src\b.txt", "already");

        var clash = _files.Unzip("src.zip", "dst", false);
        Assert.Equal(FileOutcome.Exists, clash.Outcome);
        Assert.Equal(@"dst\src\b.txt", clash.Archive);
        Assert.False(File.Exists(Full(@"dst\src\a.txt")));   // nothing written
        Assert.Equal("already", File.ReadAllText(Full(@"dst\src\b.txt")));

        Assert.Equal(FileOutcome.Ok, _files.Unzip("src.zip", "dst", true).Outcome);
        Assert.Equal("b", File.ReadAllText(Full(@"dst\src\b.txt")));

        using (var evil = ZipFile.Open(Full("evil.zip"), ZipArchiveMode.Create))
        {
            evil.CreateEntry("fine.txt");
            evil.CreateEntry("../evil.txt");
        }

        var slip = _files.Unzip("evil.zip", "safe", false);
        Assert.Equal(FileOutcome.OutsideRoot, slip.Outcome);
        Assert.Equal("../evil.txt", slip.Detail);
        Assert.False(Directory.Exists(Full("safe")));
        Assert.False(File.Exists(Full("evil.txt")));

        Put("not.zip", "plain text");
        Assert.Equal(FileOutcome.NotAnArchive, _files.Unzip("not.zip", null, false).Outcome);
        Assert.Equal(FileOutcome.Missing, _files.Unzip("nope.zip", null, false).Outcome);
        Assert.Equal(FileOutcome.IsDirectory, _files.Unzip("src", null, false).Outcome);
        Put("f.txt", "");
        Assert.Equal(FileOutcome.IsAFile, _files.Unzip("src.zip", "f.txt", false).Outcome);
    }

    // ---- open ----

    [Fact]
    public void Open_HandsTheFullPathToTheOpener_FilesFoldersAndTheRoot()
    {
        Put(@"docs\a.txt", "");
        var opened = new List<string>();

        var file = _files.Open(@"docs\a.txt", opened.Add);
        Assert.Equal(FileOutcome.Ok, file.Outcome);
        Assert.False(file.IsDirectory);
        Assert.Equal(@"docs\a.txt", file.Relative);

        var folder = _files.Open("docs", opened.Add);
        Assert.True(folder.IsDirectory);
        Assert.Equal(@"docs\", folder.Relative);

        var root = _files.Open("", opened.Add);
        Assert.True(root.IsDirectory);
        Assert.Equal("", root.Relative);

        Assert.Equal(new[] { Full(@"docs\a.txt"), Full("docs"), _root }, opened);
        Assert.Equal(FileOutcome.Missing, _files.Open("nope", opened.Add).Outcome);
        Assert.Equal(FileOutcome.OutsideRoot, _files.Open("..", opened.Add).Outcome);
        Assert.Equal(3, opened.Count);

        var failed = _files.Open("docs", _ => throw new System.ComponentModel.Win32Exception("no app"));
        Assert.Equal(FileOutcome.Failed, failed.Outcome);
        Assert.Equal("no app", failed.Detail);
    }

    [Fact]
    public void Open_FoldersOnly_RefusesAFileBeforeTheOpener_AndOpensAFolder()
    {
        Put(@"docs\a.txt", "");
        var opened = new List<string>();

        var file = _files.Open(@"docs\a.txt", opened.Add, foldersOnly: true);
        Assert.Equal(FileOutcome.IsAFile, file.Outcome);
        Assert.Equal(@"docs\a.txt", file.Relative);
        Assert.Empty(opened);

        var folder = _files.Open("docs", opened.Add, foldersOnly: true);
        Assert.Equal(FileOutcome.Ok, folder.Outcome);
        Assert.True(folder.IsDirectory);
        Assert.Equal(@"docs\", folder.Relative);

        var root = _files.Open("", opened.Add, foldersOnly: true);
        Assert.Equal(FileOutcome.Ok, root.Outcome);
        Assert.Equal("", root.Relative);
        Assert.Equal(new[] { Full("docs"), _root }, opened);

        // Missing stays missing: the file check comes after the existence one.
        Assert.Equal(FileOutcome.Missing, _files.Open("nope", opened.Add, foldersOnly: true).Outcome);
    }

    // ---- helpers ----

    [Fact]
    public void SplitLines_AndCount()
    {
        Assert.Empty(WorkingDirectory.SplitLines(""));
        Assert.Equal(new[] { "a" }, WorkingDirectory.SplitLines("a"));
        Assert.Equal(new[] { "a" }, WorkingDirectory.SplitLines("a\n"));
        Assert.Equal(new[] { "a", "" }, WorkingDirectory.SplitLines("a\n\n"));
        Assert.Equal(new[] { "a", "b" }, WorkingDirectory.SplitLines("a\r\nb"));
        WorkingDirectory.Count("one two\n three\n", out int lines, out int words);
        Assert.Equal((2, 3), (lines, words));
        WorkingDirectory.Count("", out lines, out words);
        Assert.Equal((0, 0), (lines, words));
        Assert.True(WorkingDirectory.LooksBinary("a\0b"u8));
        Assert.False(WorkingDirectory.LooksBinary("plain"u8));
        Assert.False(WorkingDirectory.LooksBinary(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void AFailure_IsAResult_NotAThrow()
    {
        // A root that cannot be created (a file is in the way) fails every operation the same way.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_root, "not a folder");
        Assert.Equal(FileOutcome.Failed, _files.List("").Outcome);
        Assert.Equal(FileOutcome.Failed, _files.WriteText("x.txt", "x", false).Outcome);
        Assert.NotEmpty(_files.WriteText("x.txt", "x", false).Detail);
        Assert.Throws<IOException>(() => _files.EnsureExists());
    }

    [Fact]
    public void ReadImage_Ok_Missing_Directory_NotAnImage_TooBig_Outside_DotFolder()
    {
        Directory.CreateDirectory(Full("shots"));
        File.WriteAllBytes(Full(@"shots\square.bmp"), NeonSidekick.App.SmokeChecks.SolidBmp(4, 4));
        Put("notes.txt", "not a picture");
        Directory.CreateDirectory(Full("dir"));
        Directory.CreateDirectory(Full(".trash"));
        File.WriteAllBytes(Full(@".trash\old.bmp"), NeonSidekick.App.SmokeChecks.SolidBmp(2, 2));
        using (var big = new FileStream(Full("huge.png"), FileMode.Create))
        {
            big.SetLength(ImageFile.MaxFileBytes + 1);
        }

        var ok = _files.ReadImage(@"shots\square.bmp");
        Assert.Equal(FileOutcome.Ok, ok.Outcome);
        Assert.Equal(@"shots\square.bmp", ok.Relative);
        Assert.Equal(@"shots\square.bmp", ok.Image!.Path);   // the relative path, never the full one
        Assert.Equal(ImageFile.Png, ok.Image.MediaType);
        Assert.Equal((4, 4), (ok.Image.Width, ok.Image.Height));

        Assert.Equal(FileOutcome.Missing, _files.ReadImage("nope.png").Outcome);
        Assert.Equal(FileOutcome.IsDirectory, _files.ReadImage("dir").Outcome);
        Assert.Equal(@"dir\", _files.ReadImage("dir").Relative);
        Assert.Equal(FileOutcome.NotAnImage, _files.ReadImage("notes.txt").Outcome);
        Assert.Null(_files.ReadImage("notes.txt").Image);
        Assert.Equal(FileOutcome.ImageTooBig, _files.ReadImage("huge.png").Outcome);
        Assert.Equal(FileOutcome.OutsideRoot, _files.ReadImage(@"..\x.png").Outcome);
        Assert.Equal(FileOutcome.Ok, _files.ReadImage(@".trash\old.bmp").Outcome);   // a dot-folder reads like any other
    }
    [Fact]
    public void Delete_LogsWhatWent_AndTheLineIsPinned()
    {
        // One Debug line per delete; the trash's Trashed, Kept, Restored and emptied lines went with File safe edits (2026-10-01, the user's call).
        Put("a.txt", "x");
        var lines = new List<NeonSidekick.Diagnostics.DiagnosticEvent>();
        Action<NeonSidekick.Diagnostics.DiagnosticEvent> capture = e => { if (e.Category == WorkingDirectory.Category && e.Message.StartsWith("Deleted ", StringComparison.Ordinal)) lines.Add(e); };
        NeonSidekick.Diagnostics.DiagnosticLog.Emitted += capture;
        try
        {
            Assert.Equal(FileOutcome.Ok, _files.Delete("a.txt").Outcome);
        }
        finally
        {
            NeonSidekick.Diagnostics.DiagnosticLog.Emitted -= capture;
        }

        Assert.Equal((NeonSidekick.Diagnostics.DiagnosticLevel.Debug, "Deleted a.txt"), (Assert.Single(lines).Level, lines[0].Message));
    }
}
