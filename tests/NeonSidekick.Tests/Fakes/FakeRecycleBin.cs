using NeonSidekick.Files;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// A Recycle bin for tests (later on 2026-10-15): records every path it is handed and answers <see cref="Answer"/>; on
/// <see cref="RecycleOutcome.Recycled"/> it moves the item out of the way (into <see cref="Folder"/> when set, else deletes it), as the real
/// bin takes it from its folder, so a watcher or a listing sees it go. Never touches the system's bin.
/// </summary>
public sealed class FakeRecycleBin : IRecycleBin
{
    public List<string> Moved { get; } = [];

    public RecycleResult Answer { get; set; } = new(RecycleOutcome.Recycled);

    public string? Folder { get; set; }

    /// <summary>An answer per call (2026-10-18, a purge refused part-way), read after the path is recorded; null: <see cref="Answer"/>.</summary>
    public Func<string, RecycleResult>? AnswerFor { get; set; }

    public RecycleResult Move(string fullPath)
    {
        lock (Moved)
        {
            Moved.Add(fullPath);
        }

        var answer = AnswerFor?.Invoke(fullPath) ?? Answer;
        if (answer.Outcome == RecycleOutcome.Recycled)
        {
            if (Directory.Exists(fullPath))
            {
                if (Folder is { } folder)
                {
                    Directory.CreateDirectory(folder);
                    Directory.Move(fullPath, Path.Combine(folder, Path.GetFileName(fullPath) + "-" + Guid.NewGuid().ToString("N")));
                }
                else
                {
                    Directory.Delete(fullPath, recursive: true);
                }
            }
            else if (File.Exists(fullPath))
            {
                if (Folder is { } folder)
                {
                    Directory.CreateDirectory(folder);
                    File.Move(fullPath, Path.Combine(folder, Guid.NewGuid().ToString("N") + "-" + Path.GetFileName(fullPath)));
                }
                else
                {
                    File.Delete(fullPath);
                }
            }
        }

        return answer;
    }
}
