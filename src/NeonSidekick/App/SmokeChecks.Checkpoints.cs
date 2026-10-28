using NeonSidekick.Files;

namespace NeonSidekick.App;

public static partial class SmokeChecks
{
    /// <summary>
    /// <c>checkpoints:roundtrip</c> (2026-10-28, the checkpoints plan): the file checkpoints' whole path on the published binary — a temp
    /// working folder and profile, a turn's journal over a file overwritten, a new file and a file deleted, its checkpoint committed into
    /// the profile's bare store (LibGit2Sharp's object and ref calls only, as <c>git:roundtrip</c> proves for the git tools), then found and
    /// replayed backwards: the bytes back, the new file gone, the deleted one back, and the spent ref removed. The JIT proves nothing about
    /// LibGit2Sharp under NativeAOT; this line does.
    /// </summary>
    public static SmokeCheck ProbeCheckpoints()
    {
        const string name = "checkpoints:roundtrip";
        string dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.smoke", Guid.NewGuid().ToString("N"));
        try
        {
            string root = Path.Combine(dir, "files");
            string profile = Path.Combine(dir, "profile");
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(profile);
            File.WriteAllText(Path.Combine(root, "notes.txt"), "one\n");
            File.WriteAllText(Path.Combine(root, "old.txt"), "kept\n");
            var files = new WorkingDirectory(() => root, TimeProvider.System);
            var store = new CheckpointStore(TimeProvider.System);
            string id = CheckpointTurns.NewId();
            var journal = new FileJournal(id, [files.Root], r => store.Begin(profile, r, id), CheckpointLimits.Default.LargestFileBytes);
            using (FileJournal.Enter(journal))
            {
                files.WriteText("notes.txt", "two\n", overwrite: true);
                files.WriteText("new.txt", "made\n", overwrite: false);
                files.Delete("old.txt");
            }

            var after = journal.Seal();
            var log = journal.Logs.SingleOrDefault(l => l.Entries.Count > 0);
            if (after is null || log?.Writer is not { } writer)
            {
                return new SmokeCheck(name, false, "journal: nothing recorded");
            }

            store.Commit(writer, new CheckpointRecord
            {
                Id = id,
                Root = log.Root,
                Message = "smoke",
                Time = DateTimeOffset.UtcNow,
                Entries = log.Entries,
                After = after[log.Root],
                SavedBytes = log.SavedBytes,
            }, CheckpointLimits.Default);
            store.FlushAsync().GetAwaiter().GetResult();
            var plan = FileRestore.Plan(store, profile, [id]);
            if (plan.IsEmpty || plan.Conflicts.Count > 0)
            {
                return new SmokeCheck(name, false, $"plan: {plan.Checkpoints.Count} found, {plan.Conflicts.Count} conflicts");
            }

            var report = FileRestore.Run(plan, store, files.PinnedAt, restoreOver: false, bin: null, CancellationToken.None);
            store.Remove(profile, report.Spent);
            bool back = File.ReadAllText(Path.Combine(root, "notes.txt")) == "one\n"
                && File.ReadAllText(Path.Combine(root, "old.txt")) == "kept\n"
                && !File.Exists(Path.Combine(root, "new.txt"));
            if (!back || report.Stopped)
            {
                return new SmokeCheck(name, false, "restore: " + FileRestore.Summary(report));
            }

            return store.Find(profile, [id]).Count == 0
                ? new SmokeCheck(name, true, $"{log.Entries.Count} entries committed, found and replayed: {FileRestore.Summary(report)}, ref spent")
                : new SmokeCheck(name, false, "spend: the ref is still there");
        }
        catch (Exception ex)
        {
            return new SmokeCheck(name, false, ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            DeleteTree(dir);
        }
    }
}
