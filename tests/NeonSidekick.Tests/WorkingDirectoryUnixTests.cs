using System.IO.Compression;
using System.Text;
using NeonSidekick.Files;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The sandbox on macOS (2026-10-06, the macOS build): the Unix twins of the <see cref="WorkingDirectoryTests"/> spelled with
/// Windows paths — <c>\</c> separators, drive letters, junctions — which are Windows-only. The same cases with <c>/</c>, Unix
/// roots and symbolic links: on Unix a backslash is a character of a name, so the display form and every path here use <c>/</c>.
/// Skipped on Windows.
/// </summary>
public sealed class WorkingDirectoryUnixTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly ManualTimeProvider _time = new();
    private readonly WorkingDirectory _files;

    public WorkingDirectoryUnixTests()
    {
        _root = Path.Combine(_dir, "files");
        _files = new WorkingDirectory(() => _root, _time);
    }

    public void Dispose()
    {
        try { Junction.DeleteTree(_dir); } catch { /* best effort */ }
    }

    private string Put(string relative, string text)
    {
        string full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text, new UTF8Encoding(false));
        return full;
    }

    private string Full(string relative) => Path.Combine(_root, relative);

    // The links of the LinkEscape twins: out leads to /x, in to the root's sub, hop to out, loop to itself.
    private static string? Links(string path) => path switch
    {
        "/r/out" => "/x",
        "/r/in" => "/r/sub",
        "/r/hop" => "/r/out",
        "/r/loop" => "/r/loop",
        "/r" => "/elsewhere",   // the root itself: never asked
        _ => null,
    };

    private PendingWrite Begin(string relative, bool overwrite)
    {
        Assert.Equal(FileOutcome.Ok, _files.BeginWrite(relative, overwrite, out var pending).Outcome);
        return pending!;
    }

    [UnixFact]
    public void ALinkLeadingOutside_CanBeDeletedAndMoved_ItsTargetUntouched_Unix()
    {
        string outside = Path.Combine(_dir, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "mine");
        _files.EnsureExists();
        Directory.CreateSymbolicLink(Full("away"), outside);

        var moved = _files.Move("away", "gone", overwrite: false);
        Assert.Equal(FileOutcome.Ok, moved.Outcome);
        Assert.False(Directory.Exists(Full("away")));
        Assert.Equal(outside, WorkingDirectory.RealLinkTarget(Full("gone")));   // the link moved, still a link
        Assert.Equal(FileOutcome.OutsideRoot, _files.Resolve(@"gone/keep.txt", forWrite: false, out _));   // and still refused to go through

        Assert.Equal(FileOutcome.OutsideRoot, _files.Copy("gone", "copied", overwrite: false).Outcome);   // a copy reads through it: refused
        Assert.Equal(FileOutcome.OutsideRoot, _files.Delete(@"gone/keep.txt").Outcome);   // what lies beyond it is still out of reach

        var deleted = _files.Delete("gone");
        Assert.Equal(FileOutcome.Ok, deleted.Outcome);
        Assert.False(Directory.Exists(Full("gone")));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(outside, "keep.txt")));   // the target and its file untouched
    }

    [UnixFact]
    public void BeginWrite_GuardsAsWriteBytes_WritesASibling_CommitsInPlace_OrLeavesNothing_Unix()
    {
        // 2026-10-01, download_file streamed to disk: WriteBytes' guards up front, the bytes in a temporary sibling, the target only at the commit.
        Assert.Equal(FileOutcome.OutsideRoot, _files.BeginWrite(@"../x.bin", true, out var none).Outcome);
        Assert.Null(none);
        Directory.CreateDirectory(Full("dir"));
        Assert.Equal(FileOutcome.IsDirectory, _files.BeginWrite("dir", true, out none).Outcome);
        Assert.Null(none);

        var begun = _files.BeginWrite(@"deep/new/a.bin", overwrite: false, out var pending);
        Assert.Equal(FileOutcome.Ok, begun.Outcome);
        using (pending)
        {
            pending!.Stream.Write([1, 2, 3]);
            Assert.False(File.Exists(Full(@"deep/new/a.bin")));   // not yet: only the sibling
            Assert.Single(Directory.GetFiles(Full(@"deep/new"), "*.tmp"));
            var done = pending.Commit();
            Assert.Equal(new WriteResult(FileOutcome.Ok, @"deep/new/a.bin", 3, false), done);
        }

        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Full(@"deep/new/a.bin")));
        Assert.Empty(Directory.GetFiles(Full(@"deep/new"), "*.tmp"));

        // A file there: refused without overwrite; with it, replaced at the commit.
        Assert.Equal(FileOutcome.Exists, _files.BeginWrite(@"deep/new/a.bin", false, out none).Outcome);
        using (var again = Begin(@"deep/new/a.bin", overwrite: true))
        {
            again.Stream.Write([9]);
            Assert.Equal(new WriteResult(FileOutcome.Ok, @"deep/new/a.bin", 1, true), again.Commit());
        }

        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(Full(@"deep/new/a.bin")));

        // Disposed without a commit (a cancel, a stall, an oversize): the sibling goes and the target is untouched.
        using (var dropped = Begin(@"deep/new/a.bin", overwrite: true))
        {
            dropped.Stream.Write([7, 7]);
        }

        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(Full(@"deep/new/a.bin")));
        Assert.Empty(Directory.GetFiles(Full(@"deep/new"), "*.tmp"));

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

    [UnixFact]
    public void Complete_EmptyPrefix_ListsOneLevel_FoldersFirst_ForwardSlashes_Unix()
    {
        Put(@"test/thing.txt", "");
        Put(@"test/bling.txt", "");
        Put("zed.md", "");
        Put("Alpha.md", "");
        File.SetAttributes(Put("hidden.txt", ""), FileAttributes.Hidden);

        var result = _files.Complete("");

        Assert.Equal(FileOutcome.Ok, result.Outcome);
        // One level only (nothing of test/ inside), folders first, then by name ignoring case; a hidden file skipped.
        Assert.Equal(new[] { "test/", "Alpha.md", "zed.md" }, result.Paths);
        Assert.False(result.Truncated);
        Assert.Equal(new[] { "test/bling.txt", "test/thing.txt" }, _files.Complete("test/").Paths);
        Assert.Equal(new[] { "test/bling.txt", "test/thing.txt" }, _files.Complete(@"test/").Paths);
    }

    [UnixFact]
    public void Complete_NamePrefix_WalksTheSubtree_IgnoringCase_Unix()
    {
        Put(@"test/thing.txt", "");
        Put(@"test/bling.txt", "");
        Put(@"test/deep/tail.md", "");
        Put("top.txt", "");

        // The user's cases (2026-09-16): @t → the folder and the files whose NAME starts with t, anywhere; @b → the one file.
        Assert.Equal(new[] { "test/", "test/deep/tail.md", "test/thing.txt", "top.txt" }, _files.Complete("t").Paths);
        Assert.Equal(new[] { "test/bling.txt" }, _files.Complete("b").Paths);
        Assert.Equal(new[] { "test/bling.txt" }, _files.Complete("B").Paths);
        // A folder segment narrows the walk to that folder; the prefix still matches names below it.
        Assert.Equal(new[] { "test/deep/tail.md", "test/thing.txt" }, _files.Complete("test/t").Paths);
        Assert.Equal(new[] { "test/thing.txt" }, _files.Complete("test/th").Paths);
        Assert.Equal(new[] { "test/thing.txt" }, _files.Complete(@"test/th").Paths);
        Assert.Empty(_files.Complete("zzz").Paths);
    }

    [UnixFact]
    public void Complete_TextFilesOnly_DropsBinaries_KeepsFoldersEmptyAndExtensionlessFiles_Unix()
    {
        Put("notes.md", "# Notes");
        Put("empty.txt", "");
        Put("LICENSE", "MIT");
        Put(@"docs/a.md", "a");
        File.WriteAllBytes(Full("pic.png"), [0x89, (byte)'P', (byte)'N', (byte)'G', 0, 0, 0, 13]);
        File.WriteAllBytes(Full(@"docs/deep.bin"), [1, 2, 0, 3]);

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

    /// <summary>
    /// A dot-name is walked like any other name, as on Windows (2026-10-06, the macOS build): .NET reads every dot-name as Hidden on
    /// Unix, and the walks' Windows skip left <c>.gitignore</c> out of a listing and a folder's copy. The macOS hidden flag is still skipped.
    /// </summary>
    [UnixFact]
    public void DotNames_AreListedAndCopied_TheHiddenFlagIsNot()
    {
        Put("src/.gitignore", "bin/");
        Put("src/.github/workflows/ci.yml", "on: push");
        Put("src/a.txt", "a");
        string flagged = Put("src/flagged.txt", "f");
        File.SetAttributes(flagged, FileAttributes.Hidden);   // chflags hidden

        Assert.Equal(new[] { "src/.github/workflows/ci.yml", "src/.gitignore", "src/a.txt" }, _files.Find("", "").Paths);
        Assert.Equal(new[] { "src/.github/workflows/ci.yml" }, _files.Find("*.yml", "").Paths);
        Assert.Equal(FileOutcome.Ok, _files.Copy("src", "dst", false).Outcome);
        Assert.Equal("bin/", File.ReadAllText(Full("dst/.gitignore")));
        Assert.True(File.Exists(Full("dst/.github/workflows/ci.yml")));
        Assert.False(File.Exists(Full("dst/flagged.txt")));
        Assert.Equal(3, _files.Info("src").Files);
        Assert.Contains(_files.FileTree("", WorkingDirectory.DefaultTreeLength).Entries, e => e.Name == ".gitignore");
    }

    [UnixFact]
    public void Copy_AFile_AndAFolderRecursively_Unix()
    {
        Put(@"src/a.txt", "a");
        Put(@"src/sub/b.txt", "b");

        var file = _files.Copy(@"src/a.txt", "a2.txt", false);
        Assert.Equal(FileOutcome.Ok, file.Outcome);
        Assert.Equal("a", File.ReadAllText(Full("a2.txt")));
        Assert.True(File.Exists(Full(@"src/a.txt")));

        var folder = _files.Copy("src", "dst", false);
        Assert.Equal(FileOutcome.Ok, folder.Outcome);
        Assert.True(folder.IsDirectory);
        Assert.Equal("b", File.ReadAllText(Full(@"dst/sub/b.txt")));

        Assert.Equal(FileOutcome.Exists, _files.Copy("src", "dst", false).Outcome);
        Put(@"src/c.txt", "c");
        Assert.Equal(FileOutcome.Ok, _files.Copy("src", "dst", true).Outcome);   // merged
        Assert.True(File.Exists(Full(@"dst/c.txt")));
        Assert.Equal(FileOutcome.IntoItself, _files.Copy("src", @"src/copy", false).Outcome);
    }

    [UnixFact]
    public void Copy_WithOverwrite_FollowsTheMoveRule_ButAFolderOverAFolderMerges_Unix()
    {
        Put("a.txt", "a");
        Put("b.txt", "b");
        Assert.Equal(FileOutcome.Ok, _files.Copy("a.txt", "b.txt", true).Outcome);
        Assert.Equal("a", File.ReadAllText(Full("b.txt")));

        Directory.CreateDirectory(Full("spot"));
        Assert.Equal(FileOutcome.FolderInTheWay, _files.Copy("a.txt", "spot", true).Outcome);
        Assert.Equal(FileText.FolderInTheWay(@"spot/"), FileText.Copied(_files.Copy("a.txt", "spot", true)));
        Assert.True(Directory.Exists(Full("spot")));

        // A folder copied over a folder merges: nothing is destroyed by a merge.
        Put(@"src/new.txt", "new");
        Put(@"dst/old.txt", "old");
        Assert.Equal(FileOutcome.Ok, _files.Copy("src", "dst", true).Outcome);
        Assert.True(File.Exists(Full(@"dst/old.txt")) && File.Exists(Full(@"dst/new.txt")));
        Assert.False(Directory.Exists(Full(".trash")));
    }

    [UnixFact]
    public void CreateDirectory_New_Exists_IsAFile_Parents_Unix()
    {
        var created = _files.CreateDirectory(@"a/b/c");
        Assert.Equal(FileOutcome.Ok, created.Outcome);
        Assert.Equal(@"a/b/c/", created.Relative);
        Assert.True(Directory.Exists(Full(@"a/b/c")));
        Assert.Equal(FileOutcome.Exists, _files.CreateDirectory(@"a/b").Outcome);
        Assert.Equal(FileOutcome.Exists, _files.CreateDirectory("").Outcome);
        Put("f.txt", "");
        Assert.Equal(FileOutcome.IsAFile, _files.CreateDirectory("f.txt").Outcome);
    }

    [UnixTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Delete_NeverTouchesGit_ItsContents_OrAFolderHoldingIt_Unix(bool nested)
    {
        // 2026-09-23, the user's call: .git, anything in it, and a folder with a .git anywhere under it (a folder, or a worktree's
        // .git file) are refused, in any case; nothing is removed. Run at the root and one folder down.
        string at = nested ? "outer" : "";
        string P(string relative) => at.Length == 0 ? relative : Path.Combine(at, relative);
        Put(P(@".git/config"), "[core]");
        Put(P(@"repo/sub/.git/HEAD"), "ref: refs/heads/main");
        Put(P(@"repo/readme.md"), "hi");
        Put(P(@"wt/.git"), "gitdir: ../.git/worktrees/wt");
        Put(P(@"plain/a.txt"), "a");

        foreach (string path in new[] { ".git", @".git/config", @".GIT/config", "repo", @"repo/sub", @"repo/sub/.git", @"repo/sub/.git/HEAD", "wt", @"wt/.git" })
        {
            Assert.Equal(FileOutcome.GitProtected, _files.Delete(P(path)).Outcome);
        }

        Assert.True(File.Exists(Full(P(@".git/config"))));
        Assert.True(File.Exists(Full(P(@"repo/sub/.git/HEAD"))));
        Assert.True(File.Exists(Full(P(@"wt/.git"))));

        Assert.Equal(FileOutcome.Ok, _files.Delete(P(@"repo/readme.md")).Outcome);   // a file beside a .git is the user's own
        Assert.Equal(FileOutcome.Ok, _files.Delete(P("plain")).Outcome);
        Assert.Equal($@"Error: '{P("repo/")}' is or holds a .git folder, which delete never removes", FileText.Deleted(_files.Delete(P("repo"))));
        Assert.True(WorkingDirectory.IsGitPath(@"a/.Git/b"));
        Assert.False(WorkingDirectory.IsGitPath(@"a/.github/b"));
        Assert.False(WorkingDirectory.IsGitPath(".gitignore"));
    }

    [UnixFact]
    public void Delete_RemovesForGood_AFolderWithEverythingInIt_TheGuardsStand_Unix()
    {
        // A file goes for good, a folder with everything in it — the one recursive delete in the sandbox (in place under File safe edits
        // off since 2026-09-20, always since 2026-10-01, when that setting and its .trash went, the user's call); the root and a missing entry are refused.
        Put(@"docs/notes.txt", "gone");
        Put(@"proj/sub/a.txt", "v1");
        Put(@"proj/b.txt", "v2");

        var file = _files.Delete(@"docs/notes.txt");
        Assert.Equal(FileOutcome.Ok, file.Outcome);
        Assert.Equal(@"docs/notes.txt", file.Relative);
        Assert.False(file.IsDirectory);
        Assert.False(File.Exists(Full(@"docs/notes.txt")));
        Assert.True(Directory.Exists(Full("docs")));   // the parent stays
        Assert.False(Directory.Exists(Full(".trash")));

        var folder = _files.Delete("proj");
        Assert.Equal(FileOutcome.Ok, folder.Outcome);
        Assert.Equal(@"proj/", folder.Relative);
        Assert.True(folder.IsDirectory);
        Assert.False(Directory.Exists(Full("proj")));
        Assert.False(Directory.Exists(Full(".trash")));

        Assert.Equal(FileOutcome.Missing, _files.Delete("nope").Outcome);
        Assert.Equal(FileOutcome.IntoItself, _files.Delete("").Outcome);
        // A .trash left from before 2026-10-01 is a folder like any other.
        Put(@".trash/20260911-140530/x.txt", "kept");
        Assert.Equal(FileOutcome.Ok, _files.Delete(@".trash/20260911-140530/x.txt").Outcome);
        Assert.Equal(FileOutcome.Ok, _files.Delete(".trash").Outcome);
        Assert.False(Directory.Exists(Full(".trash")));
        Assert.Equal("Deleted docs/notes.txt", WorkingDirectory.DeletedLogLine(@"docs/notes.txt"));
    }

    [UnixFact]
    public void FileTree_FoldersFirstThenFiles_DepthFirst_WithSizesAndIsLast_Unix()
    {
        Put(@"b/deep/bottom.txt", "12345");
        Put(@"a/one.txt", "1");
        Put(@"a/sub/two.txt", "22");
        Put("zed.md", "");
        Put("Alpha.md", "abc");
        File.SetAttributes(Put("hidden.txt", ""), FileAttributes.Hidden);

        var result = _files.FileTree("", WorkingDirectory.DefaultTreeLength);

        Assert.Equal(FileOutcome.Ok, result.Outcome);
        Assert.Equal("", result.Relative);
        Assert.Equal(_root + @"/", result.FullPath);
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
        Assert.Equal(@"a/", sub.Relative);
        Assert.Equal(Full("a") + @"/", sub.FullPath);
        Assert.Equal(new[] { "sub", "two.txt", "one.txt" }, sub.Entries.Select(e => e.Name));

        Assert.Equal(FileOutcome.Missing, _files.FileTree("nope", 10).Outcome);
        Assert.Equal(FileOutcome.IsAFile, _files.FileTree(@"a/one.txt", 10).Outcome);
        Assert.Equal(FileOutcome.OutsideRoot, _files.FileTree(@"../outside", 10).Outcome);
    }

    [UnixFact]
    public void FileTree_HideDotEntries_LeavesOutEveryDotName_AtEveryDepth_Unix()
    {
        // /vault (2026-09-22): .obsidian, .trash, .git and any dot-file, at the root or deeper, as the vault tools leave them.
        Put(@".obsidian/app.json", "{}");
        Put(@".git/HEAD", "");
        Put(@".trash/old.md", "");
        Put(@"Notes/.draft.md", "");
        Put(@"Notes/.cache/x.md", "");
        Put(@"Notes/Plan.md", "");
        Put("Home.md", "");
        Put(".hidden.md", "");

        var result = _files.FileTree("", WorkingDirectory.DefaultTreeLength, hideDotEntries: true);

        Assert.Equal(new[] { ("Notes", 1, false), ("Plan.md", 2, true), ("Home.md", 1, true) }, result.Entries.Select(e => (e.Name, e.Depth, e.IsLast)));
        // Without the switch the dot-names are there as ever (the root's .trash too since 2026-10-01).
        Assert.Contains(_files.FileTree("", WorkingDirectory.DefaultTreeLength).Entries, e => e.Name == ".obsidian");
        Assert.Contains(_files.FileTree("", WorkingDirectory.DefaultTreeLength).Entries, e => e.Name == ".trash");
    }

    [UnixFact]
    public void FileTree_HideGitFolders_LeavesOutEveryDotGitFolder_EvenUnderShowHidden_ButWalksOneAskedFor_Unix()
    {
        // /tree (2026-09-30, the user's ask: "in the same way it ignores .trash", as it did then): the root's .git and a nested repository's,
        // whatever show-hidden says; a .git file (a submodule's pointer) is no folder and stays; /tree .git still walks it.
        Put(@".git/HEAD", "");
        Put(@"sub/.git/config", "");
        Put(@"mod/.git", "gitdir: ../.git/modules/mod");
        Put("a.txt", "");
        File.SetAttributes(Full(".git"), FileAttributes.Directory | FileAttributes.Hidden);

        var names = _files.FileTree("", WorkingDirectory.DefaultTreeLength, showHidden: true, hideGitFolders: true).Entries.Select(e => e.Name);
        Assert.Equal(new[] { "mod", ".git", "sub", "a.txt" }, names);
        Assert.Equal(new[] { "HEAD" }, _files.FileTree(".git", WorkingDirectory.DefaultTreeLength, showHidden: true, hideGitFolders: true).Entries.Select(e => e.Name));
        Assert.Contains(_files.FileTree("", WorkingDirectory.DefaultTreeLength, showHidden: true).Entries, e => e.Name == ".git" && e.IsDirectory);   // off by default
    }

    [UnixFact]
    public void FileTree_MaxDepth_StopsTheDescent_Unix()
    {
        // list_directory's depth (2026-09-18): the walk stops that many levels down, files and folders alike; the default is every level.
        Put(@"b/deep/deeper/deepest/bottom/x.txt", "");
        Put(@"a/one.txt", "");
        Put(@"a/sub/two.txt", "");

        Assert.Equal(new[] { "a", "b" }, _files.FileTree("", WorkingDirectory.MaxEntries, 1).Entries.Select(e => e.Name));
        Assert.Equal(new[] { ("a", 1), ("sub", 2), ("one.txt", 2), ("b", 1), ("deep", 2) }, _files.FileTree("", WorkingDirectory.MaxEntries, 2).Entries.Select(e => (e.Name, e.Depth)));
        Assert.Equal(4, _files.FileTree("", WorkingDirectory.MaxEntries, 4).Entries.Max(e => e.Depth));
        Assert.Equal(6, _files.FileTree("", WorkingDirectory.MaxEntries).Entries.Max(e => e.Depth));   // no limit by default
        Assert.Equal(1, _files.FileTree("", WorkingDirectory.MaxEntries, 0).Entries.Max(e => e.Depth));   // below 1 reads as 1
        Assert.Equal(new[] { "deep" }, _files.FileTree("b", WorkingDirectory.MaxEntries, 1).Entries.Select(e => e.Name));
        Assert.Equal(FileOutcome.Missing, _files.FileTree("nope", 10, 1).Outcome);
        Assert.Equal(FileOutcome.IsAFile, _files.FileTree(@"a/one.txt", 10, 1).Outcome);
    }

    [UnixFact]
    public void FileTree_ShowHidden_ListsHiddenAndDotEntries_Unix()
    {
        // /tree under File browser/tree mode show-hidden (2026-09-23, the user's call): the Hidden .git and a hidden folder too; the
        // default (hideDotEntries, the walk's own Hidden skip) leaves out both and the plain dot-file.
        Put(@".git/HEAD", "");
        Put(".config", "");
        Put(@"secret/x.txt", "");
        Put("a.txt", "");
        File.SetAttributes(Full(".git"), FileAttributes.Directory | FileAttributes.Hidden);
        File.SetAttributes(Full("secret"), FileAttributes.Directory | FileAttributes.Hidden);

        Assert.Equal(new[] { ".git", "HEAD", "secret", "x.txt", ".config", "a.txt" }, _files.FileTree("", WorkingDirectory.DefaultTreeLength, showHidden: true).Entries.Select(e => e.Name));
        Assert.Equal(new[] { "a.txt" }, _files.FileTree("", WorkingDirectory.DefaultTreeLength, hideDotEntries: true).Entries.Select(e => e.Name));
        Assert.Equal(new[] { ".config", "a.txt" }, _files.FileTree("", WorkingDirectory.DefaultTreeLength).Entries.Select(e => e.Name));   // the model's listing, untouched
    }

    [UnixFact]
    public void FileTree_StopsAtTheCap_AndClampsIt_Unix()
    {
        Put(@"a/one.txt", "");
        Put(@"a/two.txt", "");
        Put(@"b/three.txt", "");
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

    [UnixFact]
    public void Find_ByGlob_Recursive_Sorted_Unix()
    {
        Put("readme.md", "");
        Put(@"docs/notes.md", "");
        Put(@"docs/deep/more.MD", "");
        Put(@"docs/other.txt", "");

        var result = _files.Find("*.md", "");
        Assert.Equal(FileOutcome.Ok, result.Outcome);
        Assert.Equal(new[] { @"docs/deep/more.MD", @"docs/notes.md", "readme.md" }, result.Paths);

        Assert.Equal(new[] { @"docs/notes.md" }, _files.Find("notes.*", "docs").Paths);
        Assert.Equal(new[] { @"docs/deep/more.MD", @"docs/notes.md", @"docs/other.txt", "readme.md" }, _files.Find("", "").Paths);
        Assert.Empty(_files.Find("zzz*", "").Paths);
        Assert.Equal(FileOutcome.Missing, _files.Find("*", "nope").Outcome);
    }

    [UnixFact]
    public void Info_AFolder_CountsFilesFoldersAndBytes_Unix()
    {
        Put(@"docs/a.txt", "12345");
        Put(@"docs/sub/b.txt", "123");
        Put(@"docs/sub/deep/c.txt", "1");

        var result = _files.Info("docs");
        Assert.True(result.IsDirectory);
        Assert.Equal(@"docs/", result.Relative);
        Assert.Equal(3, result.Files);
        Assert.Equal(2, result.Folders);
        Assert.Equal(9, result.Bytes);
        Assert.False(result.Truncated);

        var root = _files.Info("");
        Assert.Equal(3, root.Files);
        Assert.Equal(3, root.Folders);
    }

    [UnixFact]
    public void LinkEscape_GivesUpOnALoop_Unix()
    {
        Assert.Equal(@"/r/loop", WorkingDirectory.LinkEscape(@"/r", @"/r/loop/a", Links));
        Assert.Equal(32, WorkingDirectory.MaxLinkHops);
    }

    [UnixTheory]
    [InlineData(@"/r/out", @"/r/out")]
    [InlineData(@"/r/out/a/b.txt", @"/r/out")]
    [InlineData(@"/r/hop/a", @"/r/out")]   // followed into the root, then out
    [InlineData(@"/r/in/../out/a", @"/r/out")]
    public void LinkEscape_NamesTheLinkThatLeadsOutside_Unix(string candidate, string link) =>
        Assert.Equal(link, WorkingDirectory.LinkEscape(@"/r", Path.GetFullPath(candidate), Links));

    [UnixFact]
    public void List_ASubfolder_Empty_Missing_AndAFile_Unix()
    {
        Directory.CreateDirectory(Full("empty"));
        Put("f.txt", "x");

        Assert.Equal(FileOutcome.Ok, _files.List("empty").Outcome);
        Assert.Empty(_files.List("empty").Entries);
        Assert.Equal(@"empty/", _files.List("empty").Relative);
        Assert.Equal(FileOutcome.Missing, _files.List("nope").Outcome);
        Assert.Equal(FileOutcome.IsAFile, _files.List("f.txt").Outcome);
        Assert.Equal(FileOutcome.OutsideRoot, _files.List("..").Outcome);
    }

    [UnixFact]
    public void List_FoldersFirstThenFiles_ByName_Unix()
    {
        Put("b.txt", "bb");
        Put("A.txt", "a");
        Put(@"zed/x.txt", "");
        Put(@"alpha/y.txt", "");

        var result = _files.List("");

        Assert.Equal(FileOutcome.Ok, result.Outcome);
        Assert.Equal("", result.Relative);
        Assert.Equal(new[] { "alpha", "zed", "A.txt", "b.txt" }, result.Entries.Select(e => e.Name));
        Assert.Equal(new[] { true, true, false, false }, result.Entries.Select(e => e.IsDirectory));
        Assert.Equal(2, result.Entries[3].Length);
        Assert.False(result.Truncated);

        // A .trash left from before 2026-10-01 is listed like any folder (it was hidden at the root while File safe edits kept copies there).
        Put(@".trash/20260911-140530/old.txt", "old");
        Assert.Equal(new[] { ".trash", "alpha", "zed", "A.txt", "b.txt" }, _files.List("").Entries.Select(e => e.Name));
    }

    [UnixFact]
    public void Move_RefusesAnOccupiedDestination_UnlessOverwrite_AFileReplacedInPlace_AFolderRefused_Unix()
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
        Put(@"old/keep.txt", "old");
        Put(@"new/fresh.txt", "new");
        var refused = _files.Move("new", "old", true);
        Assert.Equal((FileOutcome.FolderInTheWay, @"new/", @"old/"), (refused.Outcome, refused.From, refused.To));
        Assert.True(File.Exists(Full(@"old/keep.txt")));
        Assert.True(File.Exists(Full(@"new/fresh.txt")));

        // A folder in the way of a file: the same rule; a file in the way of a folder is replaced in place.
        Put("d.txt", "d");
        Directory.CreateDirectory(Full("spot"));
        Assert.Equal(FileOutcome.FolderInTheWay, _files.Move("d.txt", "spot", true).Outcome);
        Assert.True(File.Exists(Full("d.txt")));
        Put(@"dir/x.txt", "x");
        Put("flat.txt", "flat");
        Assert.Equal(FileOutcome.Ok, _files.Move("dir", "flat.txt", true).Outcome);
        Assert.True(File.Exists(Full(@"flat.txt/x.txt")));
        Assert.Equal(FileText.FolderInTheWay(@"old/"), FileText.Moved(refused));
        Assert.False(Directory.Exists(Full(".trash")));
    }

    [UnixFact]
    public void Move_RenamesAFile_MovesAFile_AndSaysWhich_Unix()
    {
        Put("a.txt", "a");

        var renamed = _files.Move("a.txt", "b.txt", overwrite: false);
        Assert.Equal(FileOutcome.Ok, renamed.Outcome);
        Assert.True(renamed.Renamed);
        Assert.False(renamed.IsDirectory);
        Assert.Equal(("a.txt", "b.txt"), (renamed.From, renamed.To));
        Assert.True(File.Exists(Full("b.txt")));

        var moved = _files.Move("b.txt", @"docs/b.txt", overwrite: false);
        Assert.Equal(FileOutcome.Ok, moved.Outcome);
        Assert.False(moved.Renamed);
        Assert.True(File.Exists(Full(@"docs/b.txt")));   // the parent was created
    }

    [UnixFact]
    public void Move_RenamesAFolder_RefusesIntoItself_AndTheRoot_Unix()
    {
        Put(@"drafts/x.txt", "");

        var renamed = _files.Move("drafts", "poems", false);
        Assert.Equal(FileOutcome.Ok, renamed.Outcome);
        Assert.True(renamed.IsDirectory);
        Assert.True(renamed.Renamed);
        Assert.Equal((@"drafts/", @"poems/"), (renamed.From, renamed.To));
        Assert.True(File.Exists(Full(@"poems/x.txt")));

        Assert.Equal(FileOutcome.IntoItself, _files.Move("poems", @"poems/inner", false).Outcome);
        Assert.Equal(FileOutcome.Exists, _files.Move("", "elsewhere", false).Outcome);
        Assert.Equal(FileOutcome.Missing, _files.Move("nope", "x", false).Outcome);
        Assert.Equal(FileOutcome.Exists, _files.Move("poems", "poems", false).Outcome);
    }

    [UnixFact]
    public void Open_FoldersOnly_RefusesAFileBeforeTheOpener_AndOpensAFolder_Unix()
    {
        Put(@"docs/a.txt", "");
        var opened = new List<string>();

        var file = _files.Open(@"docs/a.txt", opened.Add, foldersOnly: true);
        Assert.Equal(FileOutcome.IsAFile, file.Outcome);
        Assert.Equal(@"docs/a.txt", file.Relative);
        Assert.Empty(opened);

        var folder = _files.Open("docs", opened.Add, foldersOnly: true);
        Assert.Equal(FileOutcome.Ok, folder.Outcome);
        Assert.True(folder.IsDirectory);
        Assert.Equal(@"docs/", folder.Relative);

        var root = _files.Open("", opened.Add, foldersOnly: true);
        Assert.Equal(FileOutcome.Ok, root.Outcome);
        Assert.Equal("", root.Relative);
        Assert.Equal(new[] { Full("docs"), _root }, opened);

        // Missing stays missing: the file check comes after the existence one.
        Assert.Equal(FileOutcome.Missing, _files.Open("nope", opened.Add, foldersOnly: true).Outcome);
    }

    [UnixFact]
    public void Open_HandsTheFullPathToTheOpener_FilesFoldersAndTheRoot_Unix()
    {
        Put(@"docs/a.txt", "");
        var opened = new List<string>();

        var file = _files.Open(@"docs/a.txt", opened.Add);
        Assert.Equal(FileOutcome.Ok, file.Outcome);
        Assert.False(file.IsDirectory);
        Assert.Equal(@"docs/a.txt", file.Relative);

        var folder = _files.Open("docs", opened.Add);
        Assert.True(folder.IsDirectory);
        Assert.Equal(@"docs/", folder.Relative);

        var root = _files.Open("", opened.Add);
        Assert.True(root.IsDirectory);
        Assert.Equal("", root.Relative);

        Assert.Equal(new[] { Full(@"docs/a.txt"), Full("docs"), _root }, opened);
        Assert.Equal(FileOutcome.Missing, _files.Open("nope", opened.Add).Outcome);
        Assert.Equal(FileOutcome.OutsideRoot, _files.Open("..", opened.Add).Outcome);
        Assert.Equal(3, opened.Count);

        var failed = _files.Open("docs", _ => throw new System.ComponentModel.Win32Exception("no app"));
        Assert.Equal(FileOutcome.Failed, failed.Outcome);
        Assert.Equal("no app", failed.Detail);
    }

    [UnixFact]
    public void PurgeFolder_ReadOnlyAndHiddenFiles_GoToo_Unix()
    {
        string locked = Put(@"out/locked.txt", "ro");
        string hidden = Put(@"out/dir/hidden.txt", "h");
        File.SetAttributes(locked, FileAttributes.ReadOnly);
        File.SetAttributes(hidden, FileAttributes.Hidden);

        var result = _files.PurgeFolder("out");

        Assert.Equal(FileOutcome.Ok, result.Outcome);
        Assert.Equal(2, result.Files);
        Assert.Equal(1, result.Folders);
        Assert.Equal(3, result.Bytes);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Full("out")));
    }

    [UnixFact]
    public void PurgeFolder_RefusesTheRoot_OutsideAndAFile_MissingIsOk_Unix()
    {
        Put("keep.txt", "stays");

        var root = _files.PurgeFolder(".");
        Assert.Equal((FileOutcome.OutsideRoot, WorkingDirectory.PurgeRootRefusal), (root.Outcome, root.Detail));
        Assert.Equal(FileOutcome.OutsideRoot, _files.PurgeFolder("").Outcome);
        Assert.Equal(FileOutcome.OutsideRoot, _files.PurgeFolder(@"sub/..").Outcome);
        Assert.Equal(FileOutcome.OutsideRoot, _files.PurgeFolder("..").Outcome);
        Assert.Equal(FileOutcome.IsAFile, _files.PurgeFolder("keep.txt").Outcome);
        Assert.Equal(FileOutcome.Ok, _files.PurgeFolder("nothing-here").Outcome);
        Assert.Equal("stays", File.ReadAllText(Full("keep.txt")));
    }

    [UnixFact]
    public void PurgeFolder_RemovesEverythingUnderTheFolder_KeepsTheFolder_Unix()
    {
        Put("keep.txt", "stays");
        Put(@"comfy_images/a.png", "12345");
        Put(@"comfy_images/.pasted/pasted-1.png", "678");
        File.SetAttributes(Full(@"comfy_images/a.png"), FileAttributes.ReadOnly);

        var result = _files.PurgeFolder("comfy_images");

        Assert.Equal(FileOutcome.Ok, result.Outcome);
        Assert.Equal((2, 1, 8L), (result.Files, result.Folders, result.Bytes));
        Assert.True(Directory.Exists(Full("comfy_images")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Full("comfy_images")));
        Assert.Equal("stays", File.ReadAllText(Full("keep.txt")));
    }

    [UnixFact]
    public void ReadText_Empty_Missing_Directory_Binary_TooBig_Outside_Unix()
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
        Assert.Equal(@"dir/", _files.ReadText("dir", null, null).Relative);
        Assert.Equal(FileOutcome.NotText, _files.ReadText("bin.dat", null, null).Outcome);
        Assert.Equal(FileOutcome.TooBig, _files.ReadText("big.log", null, null).Outcome);
        Assert.Equal(FileOutcome.OutsideRoot, _files.ReadText(@"../x", null, null).Outcome);
    }

    [UnixFact]
    public void Recent_NewestFirst_Capped_Unix()
    {
        var t0 = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 5; i++)
        {
            string full = Put($"f{i}.txt", new string('x', i));
            File.SetLastWriteTimeUtc(full, t0.AddMinutes(i));
        }

        string sub = Put(@"sub/g.txt", "sub");
        File.SetLastWriteTimeUtc(sub, t0.AddMinutes(10));

        var result = _files.Recent("", 3);

        Assert.Equal(FileOutcome.Ok, result.Outcome);
        Assert.Equal(new[] { @"sub/g.txt", "f4.txt", "f3.txt" }, result.Entries.Select(e => e.RelativePath));
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 5, 10, 0, TimeSpan.FromHours(-7)), result.Entries[0].Modified);   // the test zone
        Assert.Equal(3, result.Entries[0].Length);
        Assert.Equal(6, _files.Recent("", 99).Entries.Count);   // count clamped to the cap, which is above 6
        Assert.Single(_files.Recent("sub", 1).Entries);
        Assert.Empty(_files.Recent("sub", 0).Entries.Skip(1));
        Assert.Equal(FileOutcome.Missing, _files.Recent("nope", 1).Outcome);
    }

    [UnixFact]
    public void Relative_IsTheDisplayForm_Unix()
    {
        Assert.Equal("", _files.Relative(_root));
        Assert.Equal("", _files.Relative(_root + @"/"));
        Assert.Equal(@"docs/a.md", _files.Relative(Path.Combine(_root, "docs", "a.md")));
        Assert.Equal(@"docs/", _files.Relative(Path.Combine(_root, "docs"), isDirectory: true));
    }

    [UnixTheory]
    [InlineData("", "")]
    [InlineData(".", "")]
    [InlineData("notes.txt", "notes.txt")]
    [InlineData("docs/a.md", "docs/a.md")]
    [InlineData(@"docs\a.md", @"docs\a.md")]   // on Unix a backslash is a character of a name
    [InlineData(@"docs/../notes.txt", "notes.txt")]
    [InlineData(@"./docs/./a.md", @"docs/a.md")]
    [InlineData(@"  docs/  ", "docs")]
    public void Resolve_AcceptsPathsInsideTheRoot_Unix(string relative, string expected)
    {
        Assert.Equal(FileOutcome.Ok, _files.Resolve(relative, forWrite: true, out string full));
        Assert.Equal(Path.Combine(_root, expected).TrimEnd('/'), full);
    }

    [UnixFact]
    public void Resolve_ADotTrash_IsAFolderLikeAnyOther_Unix()
    {
        // Written by nothing since File safe edits went (2026-10-01, the user's call): a .trash left from before reads and writes as any folder.
        Assert.Equal(FileOutcome.Ok, _files.Resolve(@".trash/x.txt", forWrite: false, out _));
        Assert.Equal(FileOutcome.Ok, _files.Resolve(@".trash/x.txt", forWrite: true, out string full));
        Assert.Equal(Path.Combine(_root, ".trash", "x.txt"), full);
        Assert.Equal(FileOutcome.Ok, _files.Resolve(".trash", forWrite: true, out _));
    }

    [UnixFact]
    public void Resolve_BlankIsTheProfilesFilesFolder_ElseTheFullPath_Unix()
    {
        Assert.Equal(Path.Combine(@"/home/profiles/default", "files"), WorkingDirectory.Resolve("", @"/home/profiles/default"));
        Assert.Equal(Path.Combine(@"/home/profiles/default", "files"), WorkingDirectory.Resolve("   ", @"/home/profiles/default"));
        Assert.Equal(@"/work/notes", WorkingDirectory.Resolve(@" /work/notes/ ", @"/home/profiles/default"));
        Assert.Equal(@"/work/notes", WorkingDirectory.Resolve(@"/work/sub/../notes", @"/home/profiles/default"));
        Assert.True(WorkingDirectory.IsDefault(""));
        Assert.False(WorkingDirectory.IsDefault(@"/work"));
    }

    [UnixFact]
    public void Resolve_RefusesAJunctionThatLeadsOutside_AndFollowsOneThatStaysIn_Unix()
    {
        string outside = Path.Combine(_dir, "outside");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(Full("sub"));
        Directory.CreateSymbolicLink(Full("away"), outside);
        Directory.CreateSymbolicLink(Full("near"), Full("sub"));
        Assert.Equal(outside, WorkingDirectory.RealLinkTarget(Full("away")));
        Assert.Equal(FileOutcome.OutsideRoot, _files.Resolve(@"away/x.txt", forWrite: true, out _));
        Assert.Equal(FileOutcome.OutsideRoot, _files.Resolve("away", forWrite: false, out _));
        Assert.Equal(FileOutcome.Ok, _files.Resolve(@"near/x.txt", forWrite: true, out string full));
        Assert.Equal(Full(@"near/x.txt"), full);   // the spelling is kept: the link stays in
        Assert.Equal(FileOutcome.OutsideRoot, _files.WriteText(@"away/planted.txt", "x", overwrite: true).Outcome);
        Assert.False(File.Exists(Path.Combine(outside, "planted.txt")));   // nothing written through it
    }

    [UnixTheory]
    [InlineData("..")]
    [InlineData(@"../profile.json")]
    [InlineData(@"docs/../../profile.json")]
    [InlineData(@"/etc/passwd")]
    [InlineData(@"//server/share/x")]
    [InlineData(@"/top")]
    public void Resolve_RefusesPathsOutsideTheRoot_Unix(string relative)
    {
        Assert.Equal(FileOutcome.OutsideRoot, _files.Resolve(relative, forWrite: false, out string full));
        Assert.Equal("", full);
    }

    [UnixFact]
    public void Search_FilesPattern_Subfolder_Empty_AndNoHits_Unix()
    {
        Put("a.md", "word");
        Put("a.txt", "word");
        Put(@"sub/b.txt", "word");

        Assert.Equal(new[] { "a.md" }, _files.Search("word", "", "*.md", false, CancellationToken.None).Hits.Select(h => h.RelativePath));
        Assert.Equal(new[] { @"sub/b.txt" }, _files.Search("word", "sub", null, false, CancellationToken.None).Hits.Select(h => h.RelativePath));
        Assert.Empty(_files.Search("absent", "", null, false, CancellationToken.None).Hits);
        Assert.Equal(FileOutcome.Empty, _files.Search("  ", "", null, false, CancellationToken.None).Outcome);
        Assert.Equal(FileOutcome.Missing, _files.Search("word", "nope", null, false, CancellationToken.None).Outcome);
        // One file as the path (2026-09-18): searched alone, the files pattern ignored, the display its own path.
        var single = _files.Search("word", @"sub/b.txt", "*.md", false, CancellationToken.None);
        Assert.Equal(FileOutcome.Ok, single.Outcome);
        Assert.True(single.SingleFile);
        Assert.Equal(@"sub/b.txt", single.Relative);
        Assert.Equal((1, 1), (single.FilesSearched, single.FilesMatched));
        Assert.Equal(new[] { @"sub/b.txt" }, single.Hits.Select(h => h.RelativePath));
        Assert.False(_files.Search("word", "sub", null, false, CancellationToken.None).SingleFile);
        Assert.Equal(FileOutcome.OutsideRoot, _files.Search("word", "..", null, false, CancellationToken.None).Outcome);

        // A path glob (2026-09-17): matched on the path under the searched folder, or under the root, so it works from either.
        Put(@"src/deep/x.cs", "word");
        Put(@"src/y.cs", "word");
        Assert.Equal(new[] { @"src/deep/x.cs", @"src/y.cs" }, _files.Search("word", "", "src/**/*.cs", false, CancellationToken.None).Hits.Select(h => h.RelativePath));
        Assert.Equal(new[] { @"src/deep/x.cs", @"src/y.cs" }, _files.Search("word", "src", "src/**/*.cs", false, CancellationToken.None).Hits.Select(h => h.RelativePath));
        Assert.Equal(new[] { @"src/deep/x.cs", @"src/y.cs" }, _files.Search("word", "src", "**/*.cs", false, CancellationToken.None).Hits.Select(h => h.RelativePath));
        Assert.Equal(new[] { @"src/deep/x.cs" }, _files.Search("word", "", "src/*/*.cs", false, CancellationToken.None).Hits.Select(h => h.RelativePath));
        Assert.Equal(new[] { @"sub/b.txt" }, _files.Search("word", "", "sub\\*.txt", false, CancellationToken.None).Hits.Select(h => h.RelativePath));
        Assert.Equal(new[] { @"src/deep/x.cs" }, _files.Find("src/deep/*.cs", "").Paths);
    }

    [UnixFact]
    public void Unzip_AllOrNothing_OnAClash_AndOnZipSlip_Unix()
    {
        Put(@"src/a.txt", "a");
        Put(@"src/b.txt", "b");
        _files.Zip("src", "src.zip", false);
        Put(@"dst/src/b.txt", "already");

        var clash = _files.Unzip("src.zip", "dst", false);
        Assert.Equal(FileOutcome.Exists, clash.Outcome);
        Assert.Equal(@"dst/src/b.txt", clash.Archive);
        Assert.False(File.Exists(Full(@"dst/src/a.txt")));   // nothing written
        Assert.Equal("already", File.ReadAllText(Full(@"dst/src/b.txt")));

        Assert.Equal(FileOutcome.Ok, _files.Unzip("src.zip", "dst", true).Outcome);
        Assert.Equal("b", File.ReadAllText(Full(@"dst/src/b.txt")));

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

    [UnixFact]
    public void Unzip_RefusesAnEntryThatWouldLandThroughALinkLeadingOutside_Unix()
    {
        string outside = Path.Combine(_dir, "outside");
        Directory.CreateDirectory(outside);
        _files.EnsureExists();
        Directory.CreateSymbolicLink(Full("out"), outside);
        using (var evil = ZipFile.Open(Full("evil.zip"), ZipArchiveMode.Create))
        {
            evil.CreateEntry("fine.txt");
            evil.CreateEntry("out/planted.txt");
        }

        var slip = _files.Unzip("evil.zip", ".", false);
        Assert.Equal(FileOutcome.OutsideRoot, slip.Outcome);
        Assert.Equal("out/planted.txt", slip.Detail);
        Assert.False(File.Exists(Path.Combine(outside, "planted.txt")));
        Assert.False(File.Exists(Full("fine.txt")));   // all or nothing

        // A blank destination is the archive's own name beside it: a link by that name is no way out either.
        using (var plain = ZipFile.Open(Full("away.zip"), ZipArchiveMode.Create))
        {
            plain.CreateEntry("planted.txt");
        }

        Directory.CreateSymbolicLink(Full("away"), outside);
        Assert.Equal(FileOutcome.OutsideRoot, _files.Unzip("away.zip", null, false).Outcome);
        Assert.False(File.Exists(Path.Combine(outside, "planted.txt")));
    }

    [UnixFact]
    public void Walks_HonourMaxDepth_Unix()
    {
        Put("top.txt", "needle");
        Put(@"a/mid.txt", "needle");
        Put(@"a/b/deep.txt", "needle");

        Assert.Equal(new[] { "top.txt" }, _files.Find("*.txt", "", maxDepth: 1).Paths);
        Assert.Equal(new[] { @"a/mid.txt", "top.txt" }, _files.Find("*.txt", "", maxDepth: 2).Paths);
        Assert.Equal(3, _files.Find("*.txt", "").Paths.Count);
        Assert.Equal(new[] { @"a/mid.txt" }, _files.Find("*.txt", "a", maxDepth: 1).Paths);
        Assert.Single(_files.Recent("", 10, maxDepth: 1).Entries);
        Assert.Equal(2, _files.Recent("", 10, maxDepth: 2).Entries.Count);
        Assert.Equal(1, _files.Search("needle", "", null, false, CancellationToken.None, maxDepth: 1).FilesSearched);
        Assert.Equal(3, _files.Search("needle", "", null, false, CancellationToken.None).FilesSearched);
    }

    [UnixFact]
    public void WriteBytes_Creates_RefusesToReplace_ThenReplacesWithOverwrite_AndIsExistingDirectoryAnswers_Unix()
    {
        // 2026-09-18: the download's sink — WriteText's guards over bytes as they are, no length cap of its own.
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0];
        var first = _files.WriteBytes(@"img/cat.png", png, overwrite: false);
        Assert.Equal(FileOutcome.Ok, first.Outcome);
        Assert.Equal(@"img/cat.png", first.Relative);
        Assert.Equal(5, first.Bytes);
        Assert.False(first.Replaced);
        Assert.Equal(png, File.ReadAllBytes(Full(@"img/cat.png")));

        Assert.Equal(FileOutcome.Exists, _files.WriteBytes(@"img/cat.png", [1], overwrite: false).Outcome);
        Assert.Equal(png, File.ReadAllBytes(Full(@"img/cat.png")));

        var replaced = _files.WriteBytes("img/cat.png", [1, 2], overwrite: true);
        Assert.Equal(FileOutcome.Ok, replaced.Outcome);
        Assert.True(replaced.Replaced);
        Assert.Equal(new byte[] { 1, 2 }, File.ReadAllBytes(Full(@"img/cat.png")));
        Assert.False(Directory.Exists(Full(".trash")));   // nothing kept (File safe edits went 2026-10-01)
        Assert.Empty(Directory.GetFiles(Full("img"), "*.tmp"));

        Directory.CreateDirectory(Full("dir"));
        Assert.Equal(FileOutcome.IsDirectory, _files.WriteBytes("dir", [1], true).Outcome);
        Assert.Equal(FileOutcome.OutsideRoot, _files.WriteBytes(@"../x.bin", [1], true).Outcome);

        Assert.True(_files.IsExistingDirectory("dir"));
        Assert.True(_files.IsExistingDirectory(""));
        Assert.True(_files.IsExistingDirectory("img/"));
        Assert.False(_files.IsExistingDirectory("img/cat.png"));
        Assert.False(_files.IsExistingDirectory("nowhere"));
        Assert.False(_files.IsExistingDirectory(".."));
    }

    [UnixFact]
    public void WriteText_Creates_RefusesToReplace_ThenReplacesWithOverwrite_Unix()
    {
        var first = _files.WriteText(@"docs/notes.txt", "hello", overwrite: false);
        Assert.Equal(FileOutcome.Ok, first.Outcome);
        Assert.Equal(@"docs/notes.txt", first.Relative);
        Assert.Equal(5, first.Bytes);
        Assert.False(first.Replaced);
        Assert.Equal("hello", File.ReadAllText(Full(@"docs/notes.txt")));
        Assert.Equal(new byte[] { (byte)'h' }, File.ReadAllBytes(Full(@"docs/notes.txt")).Take(1));   // no BOM

        var refused = _files.WriteText(@"docs/notes.txt", "bye", overwrite: false);
        Assert.Equal(FileOutcome.Exists, refused.Outcome);
        Assert.Equal("hello", File.ReadAllText(Full(@"docs/notes.txt")));

        var replaced = _files.WriteText("docs/notes.txt", "bye", overwrite: true);
        Assert.Equal(FileOutcome.Ok, replaced.Outcome);
        Assert.True(replaced.Replaced);
        Assert.Equal("bye", File.ReadAllText(Full(@"docs/notes.txt")));
        Assert.Empty(Directory.GetFiles(Full("docs"), "*.tmp"));
    }

    [UnixFact]
    public void WriteText_Refusals_Unix()
    {
        Directory.CreateDirectory(Full("dir"));
        Assert.Equal(FileOutcome.IsDirectory, _files.WriteText("dir", "x", true).Outcome);
        Assert.Equal(FileOutcome.TooLong, _files.WriteText("x.txt", new string('x', WorkingDirectory.MaxWriteChars + 1), true).Outcome);
        Assert.Equal(FileOutcome.OutsideRoot, _files.WriteText(@"../x.txt", "x", true).Outcome);
        Assert.False(File.Exists(Full("x.txt")));
    }

    [UnixFact]
    public void Zip_AFile_IntoItself_AndTheRoot_Unix()
    {
        Put("a.txt", "x");
        var zipped = _files.Zip("a.txt", null, false);
        Assert.Equal("a.zip", zipped.Archive);
        Assert.Equal(1, zipped.Entries);
        Assert.Equal(FileOutcome.Ok, _files.Zip("a.txt", @"out/named.zip", false).Outcome);
        Assert.True(File.Exists(Full(@"out/named.zip")));

        Put(@"d/x.txt", "");
        Assert.Equal(FileOutcome.IntoItself, _files.Zip("d", @"d/d.zip", false).Outcome);
        Assert.Equal(FileOutcome.IntoItself, _files.Zip("", null, false).Outcome);
        Assert.Equal(FileOutcome.Missing, _files.Zip("nope", null, false).Outcome);
    }

    [UnixFact]
    public void Zip_AFolder_ThenUnzip_RoundTrips_Unix()
    {
        Put(@"docs/a.txt", "alpha");
        Put(@"docs/sub/b.txt", "beta");

        var zipped = _files.Zip("docs", null, false);
        Assert.Equal(FileOutcome.Ok, zipped.Outcome);
        Assert.Equal(@"docs/", zipped.Relative);
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
        Assert.Equal(@"restore/", unzipped.Archive);
        Assert.Equal(2, unzipped.Entries);
        Assert.Equal("beta", File.ReadAllText(Full(@"restore/docs/sub/b.txt")));

        var defaulted = _files.Unzip("docs.zip", null, false);
        Assert.Equal(FileOutcome.Ok, defaulted.Outcome);
        Assert.Equal(@"docs/", defaulted.Archive);   // next to the archive, named after it — over the existing folder, no clash
        Assert.True(File.Exists(Full(@"docs/docs/a.txt")));
    }
}
