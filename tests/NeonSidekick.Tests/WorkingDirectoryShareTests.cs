using System.Text;
using NeonSidekick.Files;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// <see cref="WorkingDirectoryOptions.Share"/> (2026-09-30, the UNC tools): a share's root is never created,
/// keeps a replaced file's attributes, and stops its walks at the budgets; <see cref="WorkingDirectory.CopyBetween"/> carries
/// a file or folder from one sandbox to the other.
/// </summary>
public sealed class WorkingDirectoryShareTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly ManualTimeProvider _time = new();
    private readonly WorkingDirectory _share;

    public WorkingDirectoryShareTests()
    {
        _root = Path.Combine(_dir, "share");
        _share = new WorkingDirectory(() => _root, _time, WorkingDirectoryOptions.Share);
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    private string Put(string root, string relative, string text)
    {
        string full = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text, new UTF8Encoding(false));
        return full;
    }

    [Fact]
    public void TheOptions_AreTheSandboxByDefault_AndTheSharesAsPinned()
    {
        Assert.Same(WorkingDirectoryOptions.Sandbox, new WorkingDirectory(() => _root, _time).Options);
        Assert.Equal(new WorkingDirectoryOptions(true, 0, long.MaxValue, int.MaxValue, false), WorkingDirectoryOptions.Sandbox);   // the Trash flag went 2026-10-01
        Assert.Equal(new WorkingDirectoryOptions(false, 4, 256_000_000, 100_000, true), WorkingDirectoryOptions.Share);
        Assert.Same(WorkingDirectoryOptions.Share, _share.Options);
    }

    [Fact]
    public void AMissingRoot_IsNeverCreated_EveryReadAndWriteFails_WithTheReason()
    {
        var thrown = Assert.Throws<DirectoryNotFoundException>(() => _share.EnsureExists());
        Assert.Equal(WorkingDirectory.RootMissingMessage(_root), thrown.Message);
        Assert.Equal(_root + " does not exist or cannot be reached", WorkingDirectory.RootMissingMessage(_root));

        Assert.Equal(FileOutcome.Failed, _share.List("").Outcome);
        Assert.Equal(FileOutcome.Failed, _share.Find("*", "").Outcome);
        Assert.Equal(FileOutcome.Failed, _share.Search("x", "", null, false, CancellationToken.None).Outcome);
        Assert.Equal(FileOutcome.Failed, _share.Recent("", 5).Outcome);
        Assert.Equal(FileOutcome.Failed, _share.ReadText("a.txt", null, null).Outcome);
        var write = _share.WriteText("a.txt", "x", overwrite: false);
        Assert.Equal(FileOutcome.Failed, write.Outcome);
        Assert.Equal(WorkingDirectory.RootMissingMessage(_root), write.Detail);
        Assert.False(Directory.Exists(_root));   // nothing made, by any of them
    }

    [WindowsFact]
    public void DotTrash_IsAPlainFolder_Listed_Walked_AndWritable()
    {
        Put(_root, @".trash\old.txt", "kept needle");
        Put(_root, "a.txt", "a");

        Assert.Contains(_share.List("").Entries, e => e.Name == ".trash" && e.IsDirectory);
        Assert.Equal([@".trash\old.txt"], _share.Find("old.txt", "").Paths);
        Assert.Single(_share.Search("needle", "", null, false, CancellationToken.None).Hits);
        Assert.Equal(FileOutcome.Ok, _share.WriteText(@".trash\new.txt", "n", overwrite: false).Outcome);
        Assert.Equal(FileOutcome.Ok, _share.Delete(@".trash\new.txt").Outcome);
    }

    [WindowsFact]
    public void Overwrites_Patches_AndDeletes_ArePermanent_NoCopyKept()
    {
        Put(_root, "a.txt", "one");
        Put(_root, "b.txt", "two");
        Put(_root, @"sub\c.txt", "three");

        Assert.Equal(FileOutcome.Ok, _share.WriteText("a.txt", "uno", overwrite: true).Outcome);
        Assert.Equal(FileOutcome.Ok, _share.EditText("b.txt", "two", "dos").Outcome);
        Assert.Equal(FileOutcome.Ok, _share.Delete("sub").Outcome);
        Assert.Equal(FileOutcome.Ok, _share.Copy("a.txt", "b.txt", overwrite: true).Outcome);

        Assert.Equal("uno", File.ReadAllText(Path.Combine(_root, "b.txt")));
        Assert.False(Directory.Exists(Path.Combine(_root, "sub")));
        Assert.False(Directory.Exists(Path.Combine(_root, ".trash")));
    }

    [WindowsFact]
    public void AnOverwrite_KeepsTheReplacedFilesAttributes_TheSandboxsDoesNot()
    {
        string onShare = Put(_root, "a.txt", "one");
        File.SetAttributes(onShare, FileAttributes.Hidden | FileAttributes.Archive);
        Assert.Equal(FileOutcome.Ok, _share.WriteText("a.txt", "two", overwrite: true).Outcome);
        Assert.Equal(FileOutcome.Ok, _share.EditText("a.txt", "two", "three").Outcome);
        Assert.Equal("three", File.ReadAllText(onShare));
        Assert.True(File.GetAttributes(onShare).HasFlag(FileAttributes.Hidden));   // File.Replace kept it

        string sandboxRoot = Path.Combine(_dir, "files");
        var sandbox = new WorkingDirectory(() => sandboxRoot, _time);
        string inSandbox = Put(sandboxRoot, "a.txt", "one");
        File.SetAttributes(inSandbox, FileAttributes.Hidden | FileAttributes.Archive);
        Assert.Equal(FileOutcome.Ok, sandbox.WriteText("a.txt", "two", overwrite: true).Outcome);
        Assert.False(File.GetAttributes(inSandbox).HasFlag(FileAttributes.Hidden));   // a new file moved over it, as ever
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp"));
    }

    [Fact]
    public void TheBudgets_StopASearch_AFind_AndARecentWalk_WithWhatTheyHave()
    {
        for (int i = 0; i < 20; i++)
        {
            Put(_root, $"f{i:D2}.txt", "needle " + new string('x', 100));
        }

        var tight = new WorkingDirectory(() => _root, _time, WorkingDirectoryOptions.Share with { SearchByteBudget = 500, MaxWalkEntries = 1_000 });
        var search = tight.Search("needle", "", null, false, CancellationToken.None, limit: 200);
        Assert.True(search.Budgeted);
        Assert.True(search.Truncated);
        Assert.InRange(search.FilesSearched, 1, 5);

        var walks = new WorkingDirectory(() => _root, _time, WorkingDirectoryOptions.Share with { MaxWalkEntries = 5 });
        var capped = walks.Search("needle", "", null, false, CancellationToken.None, limit: 200);
        Assert.True(capped.Budgeted);
        Assert.InRange(capped.FilesSearched, 1, 5);
        var found = walks.Find("*.txt", "", limit: 200);
        Assert.True(found.Truncated);
        Assert.Equal(5, found.Paths.Count);
        var recent = walks.Recent("", 10);
        Assert.True(recent.Budgeted);
        Assert.Equal(5, recent.Entries.Count);

        var whole = _share.Search("needle", "", null, false, CancellationToken.None, limit: 200);
        Assert.False(whole.Budgeted);
        Assert.Equal(20, whole.FilesSearched);
        Assert.False(_share.Recent("", 10).Budgeted);
    }

    [WindowsFact]
    public void CopyBetween_FetchesAFileOrAFolder_IntoTheSandbox_ItsNameAtTheRootByDefault()
    {
        string sandboxRoot = Path.Combine(_dir, "files");
        var sandbox = new WorkingDirectory(() => sandboxRoot, _time);
        Put(_root, @"reports\q3.txt", "q3");
        Put(_root, @"reports\deep\q4.txt", "q4");

        var file = WorkingDirectory.CopyBetween(_share, @"reports\q3.txt", sandbox, null, overwrite: false);
        Assert.Equal(FileOutcome.Ok, file.Outcome);
        Assert.Equal(@"reports\q3.txt", file.From);
        Assert.Equal("q3.txt", file.To);
        Assert.Equal("q3", File.ReadAllText(Path.Combine(sandboxRoot, "q3.txt")));

        var folder = WorkingDirectory.CopyBetween(_share, "reports", sandbox, @"in\reports", overwrite: false);
        Assert.Equal(FileOutcome.Ok, folder.Outcome);
        Assert.True(folder.IsDirectory);
        Assert.Equal("q4", File.ReadAllText(Path.Combine(sandboxRoot, @"in\reports\deep\q4.txt")));

        Assert.Equal(FileOutcome.Exists, WorkingDirectory.CopyBetween(_share, @"reports\q3.txt", sandbox, null, overwrite: false).Outcome);
        Put(_root, @"reports\q3.txt", "q3 v2");
        var again = WorkingDirectory.CopyBetween(_share, @"reports\q3.txt", sandbox, null, overwrite: true);
        Assert.Equal(FileOutcome.Ok, again.Outcome);
        Assert.False(Directory.Exists(Path.Combine(sandboxRoot, ".trash")));   // replaced in place (File safe edits' copy went 2026-10-01)
        Assert.Equal("q3 v2", File.ReadAllText(Path.Combine(sandboxRoot, "q3.txt")));
    }

    [WindowsFact]
    public void CopyBetween_PutsIntoTheShare_KeepingAReplacedFilesAttributes_AndNeverAnyCopy()
    {
        string sandboxRoot = Path.Combine(_dir, "files");
        var sandbox = new WorkingDirectory(() => sandboxRoot, _time);
        Put(sandboxRoot, "notes.txt", "new");
        string onShare = Put(_root, @"docs\notes.txt", "old");
        File.SetAttributes(onShare, FileAttributes.Hidden | FileAttributes.Archive);

        var put = WorkingDirectory.CopyBetween(sandbox, "notes.txt", _share, @"docs\notes.txt", overwrite: true);

        Assert.Equal(FileOutcome.Ok, put.Outcome);
        Assert.Equal("new", File.ReadAllText(onShare));
        Assert.True(File.GetAttributes(onShare).HasFlag(FileAttributes.Hidden));
        Assert.False(Directory.Exists(Path.Combine(_root, ".trash")));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "docs"), "*.tmp"));
    }

    [WindowsFact]
    public void CopyBetween_KeepsBothSandboxes_AndRefusesAFolderInAFilesWay_AndTheRoot()
    {
        string sandboxRoot = Path.Combine(_dir, "files");
        var sandbox = new WorkingDirectory(() => sandboxRoot, _time);
        Put(_root, "a.txt", "a");
        Directory.CreateDirectory(Path.Combine(sandboxRoot, "a.txt"));

        Assert.Equal(FileOutcome.OutsideRoot, WorkingDirectory.CopyBetween(_share, @"..\elsewhere.txt", sandbox, null, false).Outcome);
        Assert.Equal(FileOutcome.OutsideRoot, WorkingDirectory.CopyBetween(_share, "a.txt", sandbox, @"..\..\escape.txt", false).Outcome);
        Assert.Equal(FileOutcome.Missing, WorkingDirectory.CopyBetween(_share, "nope.txt", sandbox, null, false).Outcome);
        Assert.Equal(FileOutcome.FolderInTheWay, WorkingDirectory.CopyBetween(_share, "a.txt", sandbox, null, overwrite: true).Outcome);
        Assert.Equal(FileOutcome.Exists, WorkingDirectory.CopyBetween(_share, "a.txt", sandbox, ".", overwrite: true).Outcome);
        Assert.False(File.Exists(Path.Combine(_dir, "escape.txt")));
    }

    [Fact]
    public void TheTransferCap_IsPinned()
    {
        Assert.Equal(500_000_000, WorkingDirectory.MaxTransferBytes);
        Assert.Equal(5_000, WorkingDirectory.MaxTransferFiles);
        Assert.Equal("5,001 files, 12 bytes is over one transfer's cap of 5,000 files and 500,000,000 bytes; copy a smaller folder", WorkingDirectory.TransferCapDetail(5_001, 12));
    }
}
