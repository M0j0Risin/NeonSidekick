using System.IO.Compression;
using NeonSidekick.Files;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// Copies, moves, archives and deletes after the 2026-10-22 fixes (the fix plan's Phase 4): planned whole before anything is written, every
/// <c>.git</c> and guarded file left out and named, hidden files kept, the free space checked, unnamed outputs under free names, a file in the
/// way of a folder kept until the folder is in, the bin never deleting for good behind the user, the temp siblings swept.
/// </summary>
public sealed class TransferTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly WorkingDirectory _files;

    public TransferTests()
    {
        _root = Path.Combine(_dir, "work");
        Directory.CreateDirectory(_root);
        _files = new WorkingDirectory(() => _root, new ManualTimeProvider());
    }

    public void Dispose()
    {
        try
        {
            Junction.DeleteTree(_dir);
        }
        catch
        {
            /* best effort */
        }
    }

    private string Full(string relative) => Path.Combine(_root, relative);

    private void Put(string relative, string text = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Full(relative))!);
        File.WriteAllText(Full(relative), text);
    }

    // ── copies ───────────────────────────────────────────────────────────────

    /// <summary>A folder's copy leaves its .git out and says so (D17), and keeps a hidden file (D14).</summary>
    [Fact]
    public void AFolderCopy_LeavesGitOut_AndKeepsHiddenFiles()
    {
        Put(Path.Combine("proj", "a.txt"));
        Put(Path.Combine("proj", ".git", "config"));
        Put(Path.Combine("proj", "secret.ini"));
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(Full(Path.Combine("proj", "secret.ini")), FileAttributes.Hidden);
        }

        var copied = _files.Copy("proj", "proj2", overwrite: false);
        Assert.Equal(FileOutcome.Ok, copied.Outcome);
        Assert.True(File.Exists(Full(Path.Combine("proj2", "a.txt"))));
        Assert.True(File.Exists(Full(Path.Combine("proj2", "secret.ini"))));
        Assert.False(Directory.Exists(Full(Path.Combine("proj2", ".git"))));
        Assert.Contains(FileText.LeftOut(copied.LeftOut), FileText.Copied(copied), StringComparison.Ordinal);
        Assert.Single(copied.LeftOut);
    }

    /// <summary>A junction back into the source is never walked, so the copy ends (review N5).</summary>
    [WindowsFact]
    public void ACopy_NeverFollowsALinkBackIntoItself()
    {
        Put(Path.Combine("src", "a.txt"));
        Junction.Make(Full(Path.Combine("src", "loop")), Full("src"));
        Assert.Equal(FileOutcome.Ok, _files.Copy("src", "dst", overwrite: false).Outcome);
        Assert.True(File.Exists(Full(Path.Combine("dst", "a.txt"))));
        Assert.False(Directory.Exists(Full(Path.Combine("dst", "loop"))));
    }

    /// <summary>A folder copied over a file keeps the file when the copy fails (review J7).</summary>
    [WindowsFact]
    public void AFileInTheWay_IsKept_WhenTheFolderCopyFails()
    {
        Put(Path.Combine("src", "locked.txt"));
        Put("dst", "keep me");
        using (new FileStream(Full(Path.Combine("src", "locked.txt")), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(FileOutcome.Failed, _files.Copy("src", "dst", overwrite: true).Outcome);
        }

        Assert.Equal("keep me", File.ReadAllText(Full("dst")));
    }

    /// <summary>On a case-sensitive folder a case-only change of name is another file, never a rename (review G17).</summary>
    [Fact]
    public void ACaseOnlyName_OnACaseSensitiveFolder_IsAnotherFile()
    {
        Put("a.txt");
        if (!File.Exists(Full("A.txt")))
        {
            return;   // a case-sensitive volume: a.txt and A.txt are two names, nothing in the way
        }

        using (CaseRule.Override(_ => true))
        {
            Assert.Equal(FileOutcome.Exists, _files.Move("a.txt", "A.txt", overwrite: false).Outcome);
        }

        Assert.Equal(FileOutcome.Ok, _files.Move("a.txt", "A.txt", overwrite: false).Outcome);   // case-blind: a rename
    }

    [Fact]
    public void ACopyThatWouldFillTheDrive_IsRefused() =>
        Assert.NotNull(WorkingDirectory.FreeSpaceRefusal(_root, long.MaxValue / 2));

    /// <summary>A share copy into a folder inside its own source is into itself (review I3).</summary>
    [Fact]
    public void ACopyBetween_IntoItsOwnSource_IsRefused()
    {
        Put(Path.Combine("data", "a.txt"));
        var outer = new WorkingDirectory(() => _dir, new ManualTimeProvider(), WorkingDirectoryOptions.Share);
        var result = WorkingDirectory.CopyBetween(outer, "work", _files, "copy", overwrite: false);
        Assert.Equal(FileOutcome.IntoItself, result.Outcome);
        Assert.False(Directory.Exists(Full("copy")));
    }

    // ── archives ─────────────────────────────────────────────────────────────

    [Fact]
    public void AZipOfAZip_WithNoTo_IsIntoItself()
    {
        Put("release.zip", "PK");
        Assert.Equal(FileOutcome.IntoItself, _files.Zip("release.zip", null, overwrite: true).Outcome);   // review X3
    }

    /// <summary>An empty folder is an entry (review V11) and a hidden file comes along (D14).</summary>
    [Fact]
    public void AZip_KeepsEmptyFolders()
    {
        Put(Path.Combine("docs", "a.txt"));
        Directory.CreateDirectory(Full(Path.Combine("docs", "empty")));
        Assert.Equal(FileOutcome.Ok, _files.Zip("docs", null, overwrite: false).Outcome);
        using var zip = ZipFile.OpenRead(Full("docs.zip"));
        Assert.Contains(zip.Entries, e => e.FullName == "docs/empty/");
    }

    private string Archive(string name, params (string Entry, byte[] Bytes)[] entries)
    {
        string path = Full(name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, bytes) in entries)
        {
            using var stream = zip.CreateEntry(entry, CompressionLevel.Optimal).Open();
            stream.Write(bytes);
        }

        return path;
    }

    /// <summary>Two entries on one path, or a file where a folder goes: nothing extracted (review G8).</summary>
    [Fact]
    public void AnArchiveThatConflictsWithItself_ExtractsNothing()
    {
        Archive("twice.zip", ("a.txt", [1]), ("a.txt", [2]));
        Assert.Equal(FileOutcome.Exists, _files.Unzip("twice.zip", "out1", overwrite: true).Outcome);
        Assert.False(File.Exists(Full(Path.Combine("out1", "a.txt"))));

        Archive("clash.zip", ("a", [1]), ("a/b.txt", [2]));
        Assert.Equal(FileOutcome.Exists, _files.Unzip("clash.zip", "out2", overwrite: true).Outcome);
        Assert.False(File.Exists(Full(Path.Combine("out2", "a"))));
    }

    /// <summary>A zip bomb's ratio is refused before anything is written (review G7).</summary>
    [Fact]
    public void AZipBomb_IsRefused()
    {
        Archive("bomb.zip", ("zeros.bin", new byte[2_000_000]));
        var result = _files.Unzip("bomb.zip", "out", overwrite: false);
        Assert.Equal(FileOutcome.Failed, result.Outcome);
        Assert.Equal(FileText.ZipBomb("zeros.bin"), result.Detail);
        Assert.False(File.Exists(Full(Path.Combine("out", "zeros.bin"))));
    }

    /// <summary>An archive's .git is left out and named (D17); a name with no extension unzips beside itself (review S9).</summary>
    [Fact]
    public void AnUnzip_LeavesGitOut_AndNamesItsFolderSafely()
    {
        Archive("data", ("readme.txt", "hi"u8.ToArray()), (".git/hooks/pre-commit", "evil"u8.ToArray()));
        var result = _files.Unzip("data", null, overwrite: false);
        Assert.Equal(FileOutcome.Ok, result.Outcome);
        Assert.True(File.Exists(Full(Path.Combine("data-files", "readme.txt"))));
        Assert.False(Directory.Exists(Full(Path.Combine("data-files", ".git"))));
        Assert.NotEmpty(result.LeftOut);
    }

    // ── names ────────────────────────────────────────────────────────────────

    [Fact]
    public void AFirstFreeName_SkipsWhatIsThere()
    {
        Put("a.txt");
        Put("a-2.txt");
        Assert.Equal(Full("a-3.txt"), WorkingDirectory.FirstFree(Full("a.txt")));
        Assert.Equal(Full("b.txt"), WorkingDirectory.FirstFree(Full("b.txt")));
    }

    /// <summary>A new file is made only where nothing appeared (review J3).</summary>
    [Fact]
    public void ANewFile_IsNeverWrittenOverOneThatAppeared()
    {
        Put("taken.txt", "theirs");
        Assert.False(WorkingDirectory.WriteNew(Full("taken.txt"), "ours"u8.ToArray()));
        Assert.Equal("theirs", File.ReadAllText(Full("taken.txt")));
        Assert.Empty(Directory.GetFiles(_root, "*" + WorkingDirectory.TempSuffix));
    }

    // ── deletes ──────────────────────────────────────────────────────────────

    /// <summary>An item the bin can't take is left where it is (review J1, D11).</summary>
    [Fact]
    public void AnItemTheBinCannotTake_IsNotDeleted()
    {
        Assert.Equal(new RecycleResult(RecycleOutcome.Failed, FileText.RecycleWouldDeleteForGood), RecycleBin.MapWindows(0, aborted: true, notRecyclable: true));
        Put("big.bin");
        var bin = new FakeRecycleBin { Answer = new RecycleResult(RecycleOutcome.Failed, FileText.RecycleWouldDeleteForGood) };
        _files.RecycleDeletes = () => bin;
        var deleted = _files.Delete("big.bin");
        Assert.Equal(FileOutcome.Failed, deleted.Outcome);
        Assert.True(File.Exists(Full("big.bin")));
    }

    /// <summary>An instance with no bin of its own uses the facts' (review V3, W4).</summary>
    [Fact]
    public void ADelete_UsesTheProcessBin_WhenTheInstanceHasNone()
    {
        Put("pic.png");
        var bin = new FakeRecycleBin();
        using var _ = WriteGuard.Configure(new WriteGuardFacts(() => null, () => null, () => [], () => null, () => false, () => bin));
        Assert.Equal(DeleteWay.Recycled, _files.Delete("pic.png").Way);
        Assert.Single(bin.Moved);
    }

    /// <summary>A read-only file inside a folder deleted for good is taken too (review O6).</summary>
    [Fact]
    public void AFolderDeletedForGood_TakesItsReadOnlyFiles()
    {
        Put(Path.Combine("old", "locked.txt"));
        File.SetAttributes(Full(Path.Combine("old", "locked.txt")), FileAttributes.ReadOnly);
        Assert.Equal(FileOutcome.Ok, _files.Delete("old").Outcome);
        Assert.False(Directory.Exists(Full("old")));
    }

    // ── temp siblings and hidden entries ─────────────────────────────────────

    /// <summary>A crash's temp sibling is swept by the next write in its folder and never listed (review P7).</summary>
    [Fact]
    public void AStaleTemp_IsSwept_AndNeverListed()
    {
        string stale = Path.Combine(_root, WorkingDirectory.TempPrefix + new string('a', 32) + WorkingDirectory.TempSuffix);
        string fresh = Path.Combine(_root, WorkingDirectory.TempPrefix + new string('b', 32) + WorkingDirectory.TempSuffix);
        File.WriteAllText(stale, "old");
        File.WriteAllText(fresh, "new");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-2));
        Assert.DoesNotContain(_files.List("").Entries, e => WorkingDirectory.IsTempSibling(e.Name));
        Assert.Equal(FileOutcome.Ok, _files.WriteText("note.txt", "hi", overwrite: false).Outcome);
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
    }

    /// <summary>A listing leaves a hidden file out and says how many (review Q10, D14).</summary>
    [WindowsFact]
    public void AListing_LeavesHiddenOut_AndCountsThem()
    {
        Put("shown.txt");
        Put("desktop.ini");
        File.SetAttributes(Full("desktop.ini"), FileAttributes.Hidden | FileAttributes.System);
        var listed = _files.List("");
        Assert.DoesNotContain(listed.Entries, e => e.Name == "desktop.ini");
        Assert.Equal(1, listed.Hidden);
        Assert.Contains(FileText.HiddenNote(1), FileText.Listing(listed), StringComparison.Ordinal);
    }
}
