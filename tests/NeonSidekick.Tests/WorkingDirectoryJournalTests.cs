using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using NeonSidekick.Files;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// Every tool write of <see cref="WorkingDirectory"/> goes through the checkpoint journal (2026-10-28, review P4-13: there is no single choke
/// point, the low-level writes being static and shared with the app and the restore): a scan holds the one hook-and-recheck pair, and each
/// public write method is undone back to the tree it started from.
/// </summary>
public sealed class WorkingDirectoryJournalTests : IDisposable
{
    private readonly CheckpointRig _rig = new();

    public void Dispose() => _rig.Dispose();

    /// <summary>
    /// <c>RecheckLinks(</c> appears only at its definition, in <c>PrepareWrite</c> (the hook's pair), <c>WriteRestored</c> (the restore, never
    /// journaled) and Unzip's two per-folder and per-file rechecks (its hook ran once for the whole plan): a new write calling it bare skipped the journal.
    /// </summary>
    [Fact]
    public void RecheckLinks_IsCalledOnlyThroughPrepareWrite_TheRestore_AndUnzip()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "NeonSidekick", "Files", "WorkingDirectory.cs"));

        Assert.Equal(5, Regex.Matches(source, @"\bRecheckLinks\(").Count);
        Assert.Matches(@"private JournalMark PrepareWrite\([^)]*\)\s*\{\s*var mark = hook\(\);\s*try\s*\{\s*RecheckLinks\(", source);
    }

    public static TheoryData<string> Writes => new()
    {
        "write", "write bytes", "begin write", "append", "edit", "create folder", "move", "copy", "copy between", "delete file",
        "delete folder", "zip", "unzip",
    };

    [Theory]
    [MemberData(nameof(Writes))]
    public void EachWriteMethod_IsUndone_ToTheTreeItStartedFrom(string write)
    {
        Directory.CreateDirectory(_rig.PathOf("dir/sub"));
        File.WriteAllText(_rig.PathOf("a.txt"), "a\n");
        File.WriteAllText(_rig.PathOf("dir/one.txt"), "1\n");
        File.WriteAllText(_rig.PathOf("dir/sub/two.txt"), "2\n");
        using (var archive = ZipFile.Open(_rig.PathOf("pack.zip"), ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("in/z.txt").Open());
            writer.Write("z");
        }

        string other = Path.Combine(_rig.Dir, "other");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "o.txt"), "o\n");
        var before = Tree(_rig.Root);

        string id = _rig.Turn(files =>
        {
            bool ok = write switch
            {
                "write" => files.WriteText("a.txt", "b\n", overwrite: true).Outcome == FileOutcome.Ok,
                "write bytes" => files.WriteBytes("new/b.bin", [1, 2, 3], overwrite: false).Outcome == FileOutcome.Ok,
                "begin write" => Begin(files),
                "append" => files.AppendText("a.txt", "more\n").Outcome == FileOutcome.Ok,
                "edit" => files.EditText("a.txt", "a", "e").Outcome == FileOutcome.Ok,
                "create folder" => files.CreateDirectory("made/deep").Outcome == FileOutcome.Ok,
                "move" => files.Move("dir", "moved/dir", overwrite: false).Outcome == FileOutcome.Ok,
                "copy" => files.Copy("dir", "a.txt", overwrite: true).Outcome == FileOutcome.Ok,
                "copy between" => WorkingDirectory.CopyBetween(new WorkingDirectory(() => other, TimeProvider.System), "o.txt", files, "dir/one.txt", overwrite: true).Outcome == FileOutcome.Ok,
                "delete file" => files.Delete("a.txt").Outcome == FileOutcome.Ok,
                "delete folder" => files.Delete("dir").Outcome == FileOutcome.Ok,
                "zip" => files.Zip("dir", "pack.zip", overwrite: true).Outcome == FileOutcome.Ok,
                "unzip" => files.Unzip("pack.zip", "dir", overwrite: true).Outcome == FileOutcome.Ok,
                _ => false,
            };
            Assert.True(ok, write);
        });
        Assert.NotEqual(before, Tree(_rig.Root));

        var report = _rig.Undo(false, id);

        Assert.Equal([id], report.Spent);
        Assert.Equal(before, Tree(_rig.Root));
    }

    private static bool Begin(WorkingDirectory files)
    {
        var result = files.BeginWrite("a.txt", overwrite: true, out var pending);
        using (pending)
        {
            pending!.Stream.Write("streamed\n"u8);
            return result.Outcome == FileOutcome.Ok && pending.Commit().Outcome == FileOutcome.Ok;
        }
    }

    /// <summary>Every path under <paramref name="root"/> with its bytes (base64), a folder as a trailing slash.</summary>
    private static string Tree(string root) =>
        string.Join("\n", Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(p => FileJournal.RelativeOf(root, p) + (Directory.Exists(p) ? "/" : "=" + Convert.ToBase64String(File.ReadAllBytes(p))))
            .Order(StringComparer.Ordinal));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NeonSidekick.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir.FullName;
    }
}
