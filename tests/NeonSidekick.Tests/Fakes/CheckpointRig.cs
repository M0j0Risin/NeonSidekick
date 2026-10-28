using NeonSidekick.App;
using NeonSidekick.Files;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// A working folder, a profile and a checkpoint store in a temp folder (2026-10-28, the checkpoints plan): <see cref="Turn"/> runs writes
/// under a turn's journal and commits its checkpoint as the screen does, <see cref="Undo"/> plans and replays checkpoints newest first.
/// The store is <see cref="FakeCheckpointStore"/> unless a test hands the real one; the bin is always a <see cref="FakeRecycleBin"/>.
/// Disposing deletes the folder through <see cref="GitAccessTests.DeleteTree"/> (libgit2's objects are read-only).
/// </summary>
public sealed class CheckpointRig : IDisposable
{
    public CheckpointRig(ICheckpointStore? store = null)
    {
        Dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", "checkpoints-" + Guid.NewGuid().ToString("N"));
        Root = Path.Combine(Dir, "work");
        Profile = Path.Combine(Dir, "profile");
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Profile);
        Files = new WorkingDirectory(() => Root, TimeProvider.System);
        Store = store ?? new FakeCheckpointStore();
    }

    public string Dir { get; }

    public string Root { get; }

    public string Profile { get; }

    public WorkingDirectory Files { get; }

    public ICheckpointStore Store { get; }

    public FakeRecycleBin Bin { get; } = new();

    public CheckpointLimits Limits { get; set; } = CheckpointLimits.Default;

    /// <summary>The last turn's journal, for the tests that read its log.</summary>
    public FileJournal? Journal { get; private set; }

    public string PathOf(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Runs <paramref name="act"/> under a turn's journal, seals it and commits each root's checkpoint; returns the id.</summary>
    public string Turn(Action<WorkingDirectory> act, Func<string, long>? freeSpace = null, string session = CheckpointStore.NoSession, string message = "a turn", IEnumerable<string>? roots = null)
    {
        string id = CheckpointTurns.NewId();
        var journal = new FileJournal(id, roots ?? [Root], r => Store.Begin(Profile, r, id), Limits.LargestFileBytes, freeSpace);
        Journal = journal;
        using (FileJournal.Enter(journal))
        {
            act(Files);
        }

        var after = journal.Seal();
        foreach (var log in journal.Logs.Where(l => l.Entries.Count > 0))
        {
            if (log.Writer is { } writer)
            {
                Store.Commit(writer, new CheckpointRecord
                {
                    Id = id,
                    Root = log.Root,
                    Session = session,
                    Message = message,
                    Time = DateTimeOffset.UtcNow,
                    Tools = ["write_file"],
                    Entries = log.Entries,
                    After = after![log.Root],
                    SavedBytes = log.SavedBytes,
                    Incomplete = journal.Incomplete,
                }, Limits);
            }
        }

        Store.FlushAsync().GetAwaiter().GetResult();
        return id;
    }

    public RestorePlan Plan(params string[] idsNewestFirst) => FileRestore.Plan(Store, Profile, idsNewestFirst);

    /// <summary>Plans and replays <paramref name="idsNewestFirst"/>, then spends what came back, as <c>/undo</c> does.</summary>
    public RestoreReport Undo(bool restoreOver, params string[] idsNewestFirst)
    {
        var report = FileRestore.Run(Plan(idsNewestFirst), Store, Files.PinnedAt, restoreOver, Bin, CancellationToken.None);
        Store.Remove(Profile, report.Spent);
        return report;
    }

    public void Dispose()
    {
        Store.FlushAsync().GetAwaiter().GetResult();
        GitAccessTests.DeleteTree(Dir);
    }
}
