namespace NeonSidekick.Files;

/// <summary>What <see cref="IRecycleBin.Move"/> did with a path.</summary>
public enum RecycleOutcome
{
    /// <summary>The item is in the Recycle bin (the Trash on a Mac), where the user can restore it.</summary>
    Recycled,

    /// <summary>The drive or volume has no Recycle bin (a network share, a removable drive): the caller deletes directly and says so.</summary>
    NoBin,

    /// <summary>The bin refused or did not answer: nothing was deleted, and the caller never deletes directly behind it.</summary>
    Failed,
}

/// <summary>A Recycle bin move's outcome and, for <see cref="RecycleOutcome.Failed"/>, the sentence why.</summary>
public sealed record RecycleResult(RecycleOutcome Outcome, string Message = "");

/// <summary>The system's Recycle bin (the Trash on a Mac): <see cref="WindowsRecycleBin"/>, <see cref="MacTrash"/>, a fake in the tests.</summary>
public interface IRecycleBin
{
    /// <summary>Moves the file or folder at <paramref name="fullPath"/> (a full path) to the bin; a folder goes whole.</summary>
    RecycleResult Move(string fullPath);

    /// <summary>
    /// Whether the bin could take items from the folder at <paramref name="fullPath"/>, judged without moving anything (2026-10-20, the code
    /// review: a purge promised the bin and then deleted for good on a drive with none): null to go on and try, else why not —
    /// <see cref="RecycleOutcome.NoBin"/> for a drive with no bin, <see cref="RecycleOutcome.Failed"/> with the sentence for a folder the bin can
    /// never take from. A purge asks this before its question, so the question says what will happen.
    /// </summary>
    RecycleResult? Probe(string fullPath);
}

/// <summary>How a delete went: for good, to the bin, or for good on a drive with no bin.</summary>
public enum DeleteWay
{
    Deleted,
    Recycled,
    DeletedNoBin,
}

/// <summary>
/// The setting <c>File deletes to Recycle bin</c> (later on 2026-10-15, the user's ask: on by default, every delete was permanent until then):
/// the system's bin for this OS (<see cref="Platform"/>), the checks a path passes before Windows' shell sees it (<see cref="CheckWindows"/>: the
/// documentation's full-path and <c>\\?\</c> rules, <c>MAX_PATH</c>), the result maps (<see cref="MapWindows"/>, <see cref="MapMac"/>) and the
/// time limit every call runs under (<see cref="RunCapped"/>). Pure but for <see cref="RunCapped"/>'s thread and <see cref="Platform"/>.
/// </summary>
public static class RecycleBin
{
    /// <summary>How long a bin call may take before it is <see cref="RecycleOutcome.Failed"/> and nothing deleted behind it.</summary>
    public static readonly TimeSpan Cap = TimeSpan.FromSeconds(30);

    /// <summary><c>NSFeatureUnsupportedError</c>: a volume with no Trash (a network or FAT volume) answers <c>trashItemAtURL</c> so.</summary>
    public const long MacNoTrashCode = 3328;

    /// <summary>The longest path the shell's <c>SHFileOperationW</c> takes (<c>MAX_PATH</c> less its terminator).</summary>
    public const int MaxWindowsPath = 259;

    /// <summary>This system's bin: Windows' shell, a Mac's Trash, none elsewhere.</summary>
    public static IRecycleBin? Platform() =>
        OperatingSystem.IsWindows() ? new WindowsRecycleBin()
        : OperatingSystem.IsMacOS() ? new MacTrash()
        : null;

    /// <summary>
    /// What a Windows path gets before the shell sees it, or null to go on: a path that is not fully qualified is refused (the shell deletes
    /// one for good, its documentation says), a <c>\\?\</c> or <c>\\.\</c> path or one past <see cref="MaxWindowsPath"/> is refused with the
    /// too-long sentence (the shell fails on them), and a UNC path has no bin. Pure.
    /// </summary>
    public static RecycleResult? CheckWindows(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal) || path.Length > MaxWindowsPath)
        {
            return new RecycleResult(RecycleOutcome.Failed, FileText.RecycleTooLong);
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
        {
            return new RecycleResult(RecycleOutcome.NoBin);
        }

        if (!IsFullyQualifiedOnWindows(path))
        {
            return new RecycleResult(RecycleOutcome.Failed, FileText.RecycleNeedsFullPath);
        }

        return null;
    }

    /// <summary>
    /// The shell's answer read whole: an item the bin could not take refuses first (2026-10-22, review J1: it was deleted for good and called
    /// recycled), then 0 with nothing aborted is the bin; an abort (the docs: 0 can come back on a cancel) or a code refuses.
    /// </summary>
    public static RecycleResult MapWindows(int code, bool aborted, bool notRecyclable = false) =>
        notRecyclable ? new RecycleResult(RecycleOutcome.Failed, FileText.RecycleWouldDeleteForGood)
        : aborted ? new RecycleResult(RecycleOutcome.Failed, FileText.RecycleCancelled)
        : code == 0 ? new RecycleResult(RecycleOutcome.Recycled)
        : new RecycleResult(RecycleOutcome.Failed, FileText.RecycleRefused(code));

    /// <summary><c>trashItemAtURL</c>'s NSError read: <see cref="MacNoTrashCode"/> has no Trash; any other (513, no permission, among them) refuses.</summary>
    public static RecycleResult MapMac(long code, string? message) =>
        code == MacNoTrashCode ? new RecycleResult(RecycleOutcome.NoBin)
        : new RecycleResult(RecycleOutcome.Failed, string.IsNullOrWhiteSpace(message) ? FileText.RecycleRefused(code) : message.Trim());

    /// <summary>
    /// <paramref name="work"/> on a thread of its own (an STA one for Windows' shell, whose COM objects it drives, as <c>/shortcut</c>'s ShellLink),
    /// joined for at most <paramref name="cap"/>: past it, <see cref="RecycleOutcome.Failed"/> with <see cref="FileText.RecycleNoAnswer"/> and the
    /// thread left to finish alone (a background thread). An exception inside is <see cref="RecycleOutcome.Failed"/> with its message.
    /// </summary>
    public static RecycleResult RunCapped(Func<RecycleResult> work, TimeSpan cap, bool sta)
    {
        ArgumentNullException.ThrowIfNull(work);
        RecycleResult? result = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                result = new RecycleResult(RecycleOutcome.Failed, ex.Message);
            }
        })
        {
            IsBackground = true,
            Name = "NeonSidekick recycle bin",
        };
        if (sta && OperatingSystem.IsWindows())
        {
            thread.SetApartmentState(ApartmentState.STA);
        }

        thread.Start();
        // Past the cap the move may still finish (2026-10-22, review H18: reported failed, the folder then vanished): said so, never "failed".
        return thread.Join(cap) && result is { } done ? done : new RecycleResult(RecycleOutcome.Failed, FileText.RecycleNoAnswer);
    }

    // Path.IsPathFullyQualified on Windows' rules, whatever runs the test: a drive letter and a separator, or a UNC start.
    private static bool IsFullyQualifiedOnWindows(string path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/');
}

/// <summary>
/// The one delete under <c>File deletes to Recycle bin</c> (later on 2026-10-15): the model's <c>delete</c> (<see cref="WorkingDirectory.Delete"/>)
/// and the pictures' (the viewer's and the thumbnail browser's double-Del, the picture menu's Delete, the Web UI's 🗑️). With a bin it moves the
/// item there; a drive with no bin is deleted directly (<see cref="DeleteWay.DeletedNoBin"/>); a refusal throws an <see cref="IOException"/> with
/// the bin's sentence and deletes nothing. A link is always removed directly: the link goes, never what it points at. Without a bin it deletes
/// directly, as every delete did before.
/// </summary>
public static class FileDeletes
{
    public static DeleteWay Remove(string fullPath, bool isDirectory, bool isLink, IRecycleBin? bin)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        bool noBin = false;
        if (bin is not null && !isLink)
        {
            var moved = bin.Move(fullPath);
            switch (moved.Outcome)
            {
                case RecycleOutcome.Recycled:
                    return DeleteWay.Recycled;
                case RecycleOutcome.Failed:
                    throw new IOException(moved.Message);
                default:
                    noBin = true;
                    break;
            }
        }

        if (isDirectory && isLink)
        {
            Directory.Delete(fullPath);
        }
        else if (isDirectory)
        {
            DeleteTreeForGood(fullPath);
        }
        else
        {
            DeleteFileForGood(fullPath);
        }

        return noBin ? DeleteWay.DeletedNoBin : DeleteWay.Deleted;
    }

    /// <summary>
    /// A file deleted for good, once more with its read-only mark off when the first try is refused (2026-10-22, review O6: the purge's
    /// delete had that retry, the model's did not).
    /// </summary>
    private static void DeleteFileForGood(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (UnauthorizedAccessException) when (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            File.Delete(path);
        }
    }

    /// <summary>
    /// A folder and all in it deleted for good (2026-10-22, review O6): a refusal partway takes the read-only marks off what is left and tries
    /// once more; what still stands is said honestly — how much went and how much is left — rather than a bare "access denied" over a tree
    /// already mostly gone. Never through a link inside it.
    /// </summary>
    private static void DeleteTreeForGood(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ClearReadOnly(path);
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            int left = Directory.Exists(path)
                ? new DirectoryInfo(path).EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }).Take(100_000).Count()
                : 0;
            throw new IOException(FileText.PartlyDeleted(left, ex.Message), ex);
        }
    }

    private static void ClearReadOnly(string path)
    {
        foreach (var info in new DirectoryInfo(path).EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            try
            {
                if ((info.Attributes & FileAttributes.ReadOnly) != 0)
                {
                    info.Attributes &= ~FileAttributes.ReadOnly;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
