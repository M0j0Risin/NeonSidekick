using NeonSidekick.Files;

namespace NeonSidekick.App;

public static partial class SmokeChecks
{
    /// <summary>
    /// <c>files:recycle-bin</c> (later on 2026-10-15, <c>File deletes to Recycle bin</c>; <c>IFileOperation</c> with its sink since 2026-10-22, review J1) resolved and called in
    /// the published binary through <see cref="WindowsRecycleBin.Call"/>, on an STA thread under the bin's cap, on a path that does not exist in a
    /// fresh temp folder: the struct marshals and the call returns, and nothing is ever put in the bin. The shell has answered a missing file with
    /// more than one code over the years, so the code is reported, never pinned; the folder must be untouched. On a Mac <c>files:trash</c>
    /// instead (<see cref="MacTrash.Answers"/>: NSFileManager answers <c>trashItemAtURL:resultingItemURL:error:</c>; nothing trashed); skipped elsewhere.
    /// </summary>
    public static SmokeCheck ProbeRecycleBin()
    {
        if (OperatingSystem.IsMacOS())
        {
            try
            {
                bool answers = MacTrash.Answers();
                return new SmokeCheck("files:trash", answers, answers ? "NSFileManager answers " + MacTrash.TrashSelector : "NSFileManager does not answer " + MacTrash.TrashSelector);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                return new SmokeCheck("files:trash", false, $"{e.GetType().Name}: {e.Message}");
            }
        }

        const string name = "files:recycle-bin";
        if (!OperatingSystem.IsWindows())
        {
            return new SmokeCheck(name, true, "skipped: not Windows");
        }

        return ProbeShellRecycleBin(name);
    }

    /// <summary>
    /// <c>files:quarantine</c> (2026-10-22, review O5: unzip carries an archive's mark): on a Mac, libSystem's <c>setxattr</c> and
    /// <c>getxattr</c> round-trip <c>com.apple.quarantine</c> on a fresh temp file (<see cref="MacQuarantine"/>); skipped elsewhere.
    /// </summary>
    public static SmokeCheck ProbeQuarantine()
    {
        const string name = "files:quarantine";
        if (!OperatingSystem.IsMacOS())
        {
            return new SmokeCheck(name, true, "skipped: not macOS");
        }

        string folder = Path.Combine(Path.GetTempPath(), "NeonSidekick.Smoke", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, "a.txt");
            File.WriteAllText(file, "a");
            byte[] mark = "0081;00000000;NeonSidekick;"u8.ToArray();
            bool written = MacQuarantine.Write(file, MarkOfTheWeb.QuarantineAttribute, mark);
            byte[]? read = MacQuarantine.Read(file, MarkOfTheWeb.QuarantineAttribute);
            bool same = read is not null && read.AsSpan().SequenceEqual(mark);
            return new SmokeCheck(name, written && same, written && same ? "setxattr and getxattr round-trip com.apple.quarantine" : $"written {written}, read back {(read is null ? "nothing" : "other bytes")}");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new SmokeCheck(name, false, $"{e.GetType().Name}: {e.Message}");
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static SmokeCheck ProbeShellRecycleBin(string name)
    {
        string folder = Path.Combine(Path.GetTempPath(), "neonsidekick-smoke-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            string missing = Path.Combine(folder, "missing.txt");
            (int Code, bool Aborted, bool NotRecyclable) answer = default;
            var done = RecycleBin.RunCapped(() =>
            {
                answer = WindowsRecycleBin.Call(missing);
                return new RecycleResult(RecycleOutcome.Recycled);
            }, RecycleBin.Cap, sta: true);
            if (done.Outcome == RecycleOutcome.Failed)
            {
                return new SmokeCheck(name, false, "IFileOperation did not return: " + done.Message);
            }

            bool untouched = Directory.Exists(folder);
            return new SmokeCheck(name, untouched, untouched
                ? $"IFileOperation answered a missing file with code {answer.Code} (aborted: {answer.Aborted}), the folder untouched"
                : "the temp folder is gone after the call");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new SmokeCheck(name, false, $"{e.GetType().Name}: {e.Message}");
        }
        finally
        {
            WorkingDirectory.DeleteDirectoryQuietly(folder);
        }
    }
}
