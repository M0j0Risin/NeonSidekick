using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.IO.Enumeration;
using System.Text;
using System.Text.RegularExpressions;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Files;

/// <summary>Why a file operation did not do what was asked; <see cref="Ok"/> when it did.</summary>
public enum FileOutcome
{
    Ok,

    /// <summary>The path resolves outside the working directory (or a zip entry outside its destination).</summary>
    OutsideRoot,

    Missing,
    IsDirectory,
    IsAFile,
    NotText,
    NotAnArchive,

    /// <summary>Something is already at the destination and <c>overwrite</c> was not asked for.</summary>
    Exists,

    /// <summary>The text to edit was empty or whitespace only.</summary>
    Empty,
    EditNotFound,

    /// <summary>Several matches without <c>replace_all</c>; <c>Count</c> carries how many, <c>Locations</c> the first few.</summary>
    EditAmbiguous,

    /// <summary>A folder into its own subtree, or an archive inside the folder being packed.</summary>
    IntoItself,

    /// <summary>The content offered is over <see cref="WorkingDirectory.MaxWriteChars"/>.</summary>
    TooLong,

    /// <summary>The file on disk is over the size a text operation reads whole.</summary>
    TooBig,

    /// <summary>A search pattern the regex parser refused.</summary>
    BadPattern,

    /// <summary>An I/O or permission failure; <c>Detail</c> carries the message.</summary>
    Failed,

    /// <summary>The codecs could not decode the file as a picture (<see cref="WorkingDirectory.ReadImage"/>).</summary>
    NotAnImage,

    /// <summary>The picture is over <see cref="ImageFile.MaxFileBytes"/> or <see cref="ImageFile.MaxPixels"/>.</summary>
    ImageTooBig,

    /// <summary>A <c>patch_file</c> whose <c>new_text</c> is its <c>old_text</c>: nothing to change (2026-09-18).</summary>
    Same,

    /// <summary>A <c>patch_file</c> whose <c>old_text</c> is nowhere but whose <c>new_text</c> already is: the edit was applied before (2026-09-19). Not an error.</summary>
    AlreadyApplied,

    /// <summary>A <c>patch_file</c> with <c>replace_all</c> whose <c>old_text</c> matched several places only approximately (2026-09-19); <c>Count</c> carries how many.</summary>
    ApproximateAll,

    /// <summary>A <c>patch_file</c> whose arguments carry escapes the matched text does not (<see cref="EscapeDrift"/> on the result, 2026-09-19).</summary>
    EscapeDrift,

    /// <summary>A folder at a <c>move</c> / <c>copy</c> destination with <c>overwrite</c> (2026-09-20): a folder in the way is never replaced.</summary>
    FolderInTheWay,

    /// <summary>A <c>delete</c> of <c>.git</c>, of anything in it, or of a folder holding one (2026-09-23, the user's call): git's own store is never deleted.</summary>
    GitProtected,

    /// <summary>
    /// A change to a SQLite database file, or a <c>delete</c>/<c>move</c> of a folder holding one, while the SQLite tools are on
    /// (2026-10-05, the user's call; <see cref="WorkingDirectory.Databases"/>): a database changes only through <c>sqlite_execute</c>.
    /// </summary>
    DatabaseProtected,

    /// <summary>
    /// A path outside the working directory but inside the profile's images folder (2026-10-09, the user's call;
    /// <see cref="WorkingDirectory.ImagesFolder"/>): refused as <see cref="OutsideRoot"/> is, with a sentence naming the tools that
    /// do reach the folder by full path (<c>search_files</c>, <c>file_info</c> and the picture tools).
    /// </summary>
    InImagesFolder,

    /// <summary>
    /// A path outside the working directory but inside a skill folder (2026-10-17, the toki-pona session: a model read a skill's own file by
    /// its full path, was told only "outside the working directory", and found <c>load_skill</c> on its third try;
    /// <see cref="WorkingDirectory.SkillFolders"/>): refused as <see cref="OutsideRoot"/> is, with a sentence naming <c>load_skill</c> and
    /// <c>skill_editor</c>.
    /// </summary>
    InSkillFolder,

    /// <summary>A model write into <c>.neon</c>, whose plans, audits and loop files the app trusts (2026-10-22, review P4, <see cref="WriteGuard"/>).</summary>
    NeonFilesProtected,

    /// <summary>A model write into the app's home or a profile (2026-10-22, review P2/W1, <see cref="WriteGuard"/>): settings, configs and databases.</summary>
    AppFilesProtected,

    /// <summary>A model write into a skill root other than through <c>skill_editor</c> (2026-10-22, review P3, <see cref="WriteGuard"/>).</summary>
    SkillFilesProtected,

    /// <summary>
    /// A model write to the project file the system prompt reads (2026-10-22, review P1, the fix plan's D2): the turn asks the user and runs the
    /// call again on a yes; with nobody to ask, refused.
    /// </summary>
    ProjectFileAsk,

    /// <summary>A write onto a shopping or grocery list's name while the shopping tools are offered (the tripwire, in <see cref="WriteGuard"/> since 2026-10-22).</summary>
    ShoppingList,

    /// <summary>A path naming an NTFS stream (<c>a.txt:hidden</c>, 2026-10-22, review M2): hidden data, or a file's Mark of the Web.</summary>
    StreamName,

    /// <summary>A name Windows reserves for a device (<c>NUL</c>, <c>CON</c>, <c>COM1</c>…, 2026-10-22, review O10), not a file.</summary>
    DeviceName,

    /// <summary>A FIFO, socket or device file (2026-10-22, review N1/N2): a read of one waits forever.</summary>
    NotARegularFile,

    /// <summary>A home-folder (<c>~/x</c>) or <c>file:</c> URL path, which the file tools take as a relative name (2026-10-22, review S5).</summary>
    NotAPathHere,

    /// <summary>The working directory was deleted or renamed this session (2026-10-22, review S6): never made again behind the user's back.</summary>
    RootGone,

    /// <summary>A search needle with a line break in it (2026-10-22, review Q9): the search matches within one line.</summary>
    OneLineOnly,

    /// <summary>New text the file's own encoding can't hold (2026-10-22, review G4/Q1: a code page and an emoji): refused, never written as '?'.</summary>
    NotEncodable,
}

public readonly record struct DirectoryEntry(string Name, bool IsDirectory, long Length);

/// <summary>One line of <c>/tree</c>: a folder or file at <paramref name="Depth"/> (1 = right under the walked folder), <paramref name="IsLast"/> when it is the last of its siblings.</summary>
public readonly record struct FileTreeEntry(string Name, int Depth, bool IsDirectory, long Length, bool IsLast);

/// <param name="Before">The lines just above the hit when context was asked for (<c>search_files</c>'s <c>context</c>, 2026-09-17), clipped like the hit; empty otherwise.</param>
/// <param name="After">The lines just under it, the same way.</param>
public readonly record struct SearchHit(string RelativePath, int Line, string Text, IReadOnlyList<string> Before, IReadOnlyList<string> After)
{
    public SearchHit(string relativePath, int line, string text) : this(relativePath, line, text, [], []) { }
}

public readonly record struct RecentEntry(string RelativePath, DateTimeOffset Modified, long Length);

/// <summary>One file of a search under <see cref="SearchOutput.Files"/> (2026-09-19): its path and how many lines matched.</summary>
public readonly record struct SearchFileCount(string RelativePath, int Matches);

/// <summary>What a content search lists: every hit, or the files that hold one with their counts (<c>search_files</c>'s <c>output</c>, 2026-09-19).</summary>
public enum SearchOutput
{
    Content,
    Files,
}

public sealed record ListResult(FileOutcome Outcome, string Relative, IReadOnlyList<DirectoryEntry> Entries, bool Truncated, string Detail = "")
{
    /// <summary>The hidden and system entries left out (2026-10-22, review Q10, the fix plan's D14: every read-side walk leaves them out and says how many).</summary>
    public int Hidden { get; init; }
}

/// <summary>The <c>/tree</c> walk: <paramref name="FullPath"/> is the walked folder, full with a trailing separator (the header line); <paramref name="Truncated"/> when the cap stopped it.</summary>
public sealed record FileTreeResult(FileOutcome Outcome, string Relative, string FullPath, IReadOnlyList<FileTreeEntry> Entries, bool Truncated, string Detail = "");

public sealed record FindResult(FileOutcome Outcome, string Relative, IReadOnlyList<string> Paths, bool Truncated, string Detail = "")
{
    /// <summary>The hidden and system entries left out (D14).</summary>
    public int Hidden { get; init; }
}

/// <summary>
/// <see cref="WorkingDirectory.ListFiles"/>'s answer (2026-10-18): the files, sandbox-relative, and whether the walk was cut by its budget.
/// <paramref name="Stamps"/> (2026-10-20, a review: the Ralph loop's snapshot stat'ed every listed file again) holds each file's size and time, in
/// the order of <paramref name="Files"/>, when the walk was asked for them; else null.
/// </summary>
public sealed record FileListResult(FileOutcome Outcome, string Relative, IReadOnlyList<string> Files, bool Cut, string Detail = "", IReadOnlyList<(long Length, DateTime Written)>? Stamps = null);

/// <summary>
/// The @-mention completions for a typed query (<see cref="WorkingDirectory.Complete"/>): relative
/// paths spelled with <c>/</c>, a trailing <c>/</c> on a folder, folders first; <paramref name="Truncated"/>
/// when a cap stopped the walk or cut the list.
/// </summary>
public sealed record MentionResult(FileOutcome Outcome, IReadOnlyList<string> Paths, bool Truncated, string Detail = "");

public sealed record SearchResult(
    FileOutcome Outcome,
    string Relative,
    IReadOnlyList<SearchHit> Hits,
    int FilesSearched,
    int FilesMatched,
    int TimedOut,
    bool Truncated,
    TimeSpan Elapsed,
    string Detail = "",
    bool SingleFile = false,
    IReadOnlyList<SearchFileCount>? Files = null)
{
    /// <summary>The files that hold a hit with their counts, sorted by path — filled under <see cref="SearchOutput.Files"/> alone (2026-09-19).</summary>
    public IReadOnlyList<SearchFileCount> Files { get; init; } = Files ?? [];

    /// <summary>
    /// The walk stopped at <see cref="WorkingDirectoryOptions.SearchByteBudget"/> or <see cref="WorkingDirectoryOptions.MaxWalkEntries"/>
    /// (a share, 2026-09-30) before it had looked at everything; <see cref="Truncated"/> is set with it.
    /// </summary>
    public bool Budgeted { get; init; }

    /// <summary>The files left unread because their data is only in the cloud (2026-10-22, review O1, <see cref="WorkingDirectory.IsOffline"/>).</summary>
    public int CloudOnly { get; init; }

    /// <summary>The hidden and system entries left out (D14).</summary>
    public int Hidden { get; init; }

    /// <summary>Why a search aimed at one file did not search it (2026-10-22, review W3, V10): too big, not text, unreadable, cloud-only, filtered out.</summary>
    public string? Unsearched { get; init; }
}

public sealed record RecentResult(FileOutcome Outcome, string Relative, IReadOnlyList<RecentEntry> Entries, string Detail = "")
{
    /// <summary>The hidden and system entries left out (D14).</summary>
    public int Hidden { get; init; }

    /// <summary>The walk stopped at <see cref="WorkingDirectoryOptions.MaxWalkEntries"/> (a share, 2026-09-30): the newest of what was looked at, not of everything.</summary>
    public bool Budgeted { get; init; }
}

public sealed record InfoResult(
    FileOutcome Outcome,
    string Relative,
    bool IsDirectory,
    long Bytes,
    DateTimeOffset Modified,
    int? Lines,
    int? Words,
    int Files,
    int Folders,
    bool Truncated,
    string LineEnding = "",
    bool Bom = false,
    string Detail = "");

/// <param name="LineCut">A line longer than <see cref="WorkingDirectory.MaxReadChars"/> came back cut (2026-10-11).</param>
public sealed record ReadResult(FileOutcome Outcome, string Relative, string Text, int TotalLines, int FromLine, int ToLine, bool Truncated, string Detail = "", bool LineCut = false)
{
    /// <summary>The encoding the file was read in when it is not plain UTF-8 (<see cref="TextCodec.Label"/>, 2026-10-22, review Q1); null otherwise.</summary>
    public string? Encoding { get; init; }

    /// <summary>The stamp taken as the read began (2026-10-22, review J12): what <see cref="WorkingDirectory.MarkSeen(string, SeenStamp?)"/> records.</summary>
    public SeenStamp? Seen { get; init; }

    /// <summary>The first line the read cut, 0 for none, and how long it is (2026-10-22, review Q2: a cut was never said).</summary>
    public int CutLine { get; init; }

    /// <summary>The cut line's length in characters.</summary>
    public int CutLength { get; init; }

    /// <summary>
    /// Every line of the file came back as it is (2026-10-11, the review: a window or a cut read marked the whole file seen, and
    /// <c>write_file</c> without a mode then replaced it with the part the model had): only such a read marks the file seen.
    /// </summary>
    public bool Whole => Outcome == FileOutcome.Ok && !Truncated && !LineCut && (TotalLines == 0 || (FromLine == 1 && ToLine == TotalLines));
}

/// <param name="Image">The picture as the model gets it, its <see cref="ImageAttachment.Path"/> the relative one; null unless <paramref name="Outcome"/> is <see cref="FileOutcome.Ok"/>.</param>
/// <summary>
/// A file as the model saw it (2026-10-22, review J12/Q11): its size and write time as the read began, and a hash of the bytes read, so a
/// change the size and time can't show — the same second on a coarse file system — still counts as a change.
/// </summary>
public sealed record SeenStamp(long Length, DateTime Written, string Hash)
{
    /// <summary>The stamp for <paramref name="bytes"/> read with <paramref name="length"/> and <paramref name="written"/>.</summary>
    public static SeenStamp Of(long length, DateTime written, ReadOnlySpan<byte> bytes) =>
        new(length, written, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));
}

public sealed record ImageResult(FileOutcome Outcome, string Relative, ImageAttachment? Image, string Detail = "");

/// <param name="Bytes">The file's bytes, unchanged; null unless <paramref name="Outcome"/> is <see cref="FileOutcome.Ok"/>.</param>
public sealed record BytesResult(FileOutcome Outcome, string Relative, byte[]? Bytes, string Detail = "");

/// <param name="Replaced">A write replaced an existing file; an append created a new one when false.</param>
/// <param name="Lines">The file's line count after the write (2026-09-18, so the model needs no <c>file_info</c> to count); null for bytes or a file too big to count.</param>
/// <param name="Words">The file's whitespace-separated word count after the write, the same way.</param>
public sealed record WriteResult(FileOutcome Outcome, string Relative, long Bytes, bool Replaced, string Detail = "", int? Lines = null, int? Words = null)
{
    /// <summary>What the write changed, for the transcript (2026-10-03, <see cref="FileDiff"/>); null for bytes, a failure, or a replaced file that was not text.</summary>
    public FileDiff? Diff { get; init; }
}

/// <summary>
/// A file being written a piece at a time (2026-10-01, <c>download_file</c> streamed to disk, the user's ask: a cap past what
/// memory should hold): <see cref="Stream"/> writes a temporary sibling of the target, <see cref="Commit"/> puts it in the
/// target's place the way <see cref="WorkingDirectory.WriteBytes"/> does (a replace in place under
/// <see cref="WorkingDirectoryOptions.PreserveOnReplace"/>, else a move), and a dispose without a commit deletes it, so a
/// cancel, a stall or a refusal leaves nothing behind. Made by <see cref="WorkingDirectory.BeginWrite"/>.
/// </summary>
public sealed class PendingWrite : IDisposable
{
    private readonly string _full;
    private readonly string _temp;
    private readonly bool _overwrite;
    private readonly bool _preserve;
    private readonly FileStream _stream;
    private bool _done;

    internal PendingWrite(string full, string display, bool overwrite, bool preserve)
    {
        _full = full;
        _overwrite = overwrite;
        _preserve = preserve;
        Relative = display;
        _temp = WorkingDirectory.TempSibling(full);
        _stream = new FileStream(_temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
        WorkingDirectory.HideTemp(_temp);
    }

    /// <summary>The target as the result names it.</summary>
    public string Relative { get; }

    /// <summary>Where the bytes go: the temporary sibling, never the target.</summary>
    public Stream Stream => _stream;

    /// <summary>
    /// The written file in the target's place: <see cref="FileOutcome.Ok"/> with its length (<c>Replaced</c> when a file was
    /// there), <see cref="FileOutcome.Exists"/> when one appeared meanwhile and <c>overwrite</c> was not given, or
    /// <see cref="FileOutcome.Failed"/> with the file system's message. The temporary file goes either way. A <paramref name="mark"/>
    /// (<see cref="MarkOfTheWeb.ForDownload"/>) goes on the file once it is in place (2026-10-23, the Mac pass: a download came unmarked);
    /// a volume that takes none leaves it unmarked, said in the log.
    /// </summary>
    public WriteResult Commit(byte[]? mark = null)
    {
        ObjectDisposedException.ThrowIf(_done, this);
        _done = true;
        try
        {
            long length = _stream.Length;
            _stream.Dispose();
            bool existed = File.Exists(_full);
            if (existed && !_overwrite)
            {
                return new WriteResult(FileOutcome.Exists, Relative, 0, false);
            }

            // A new file only where nothing appeared since the write began (2026-10-22, review J3).
            if (!existed)
            {
                if (!WorkingDirectory.MoveTempNew(_temp, _full))
                {
                    return new WriteResult(FileOutcome.Exists, Relative, 0, false);
                }
            }
            else
            {
                WorkingDirectory.MoveIntoPlace(_temp, _full, _preserve);
            }

            if (mark is not null && !MarkOfTheWeb.Put(mark, _full))
            {
                DiagnosticLog.Warn("Files", Relative + " took no internet mark (the volume keeps none)");
            }

            return new WriteResult(FileOutcome.Ok, Relative, length, existed);
        }
        catch (Exception ex) when (WorkingDirectory.IsFileFailure(ex))
        {
            return new WriteResult(FileOutcome.Failed, Relative, 0, false, Detail: ex.Message);
        }
        finally
        {
            WorkingDirectory.DeleteQuietly(_temp);
        }
    }

    public void Dispose()
    {
        _stream.Dispose();
        if (!_done)
        {
            _done = true;
            WorkingDirectory.DeleteQuietly(_temp);
        }
    }
}

/// <summary>
/// What an edit did. <paramref name="Line"/> is the first line touched (the match's line);
/// <paramref name="Count"/> the occurrences replaced (a <c>replace_all</c> over several), or the matches found
/// under <see cref="FileOutcome.EditAmbiguous"/> / <see cref="FileOutcome.ApproximateAll"/>. After a successful edit <paramref name="NewFrom"/>–<paramref name="NewTo"/>
/// is where the new text sits (an empty range — <c>NewTo</c> = <c>NewFrom − 1</c> — for a deletion), <paramref name="TotalLines"/>
/// the file's line count then, and <paramref name="Region"/> the lines around it (<see cref="WorkingDirectory.EditContextLines"/>
/// on each side, at most <see cref="WorkingDirectory.MaxEditRegionLines"/> — empty when the region is over the cap or the edit was
/// a <c>replace_all</c>), <paramref name="RegionFrom"/> the number of its first line. <paramref name="Lines"/> lists the lines of
/// every occurrence a <c>replace_all</c> touched.
/// <paramref name="Words"/> is the file's word count after the edit (2026-09-18, beside <paramref name="TotalLines"/>).
/// <paramref name="Strategy"/> is the way <c>old_text</c> was found (2026-09-19; <see cref="MatchStrategy.Exact"/> when
/// as written), <paramref name="Locations"/> the first few matches an ambiguous refusal names, <paramref name="Drift"/> the
/// artefact an <see cref="FileOutcome.EscapeDrift"/> refusal found.
/// </summary>
public sealed record EditResult(
    FileOutcome Outcome,
    string Relative,
    int Line,
    int Count,
    int NewFrom = 0,
    int NewTo = 0,
    int TotalLines = 0,
    IReadOnlyList<string>? Region = null,
    int RegionFrom = 0,
    IReadOnlyList<int>? Lines = null,
    string Detail = "",
    int Words = 0,
    MatchStrategy Strategy = MatchStrategy.Exact,
    IReadOnlyList<MatchLocation>? Locations = null,
    EscapeDrift Drift = EscapeDrift.None)
{
    public IReadOnlyList<string> Region { get; init; } = Region ?? [];
    public IReadOnlyList<int> Lines { get; init; } = Lines ?? [];
    public IReadOnlyList<MatchLocation> Locations { get; init; } = Locations ?? [];

    /// <summary>What the edit changed, for the transcript (2026-10-03, <see cref="FileDiff"/>); null unless <see cref="FileOutcome.Ok"/>.</summary>
    public FileDiff? Diff { get; init; }
}

public sealed record CreateResult(FileOutcome Outcome, string Relative, string Detail = "");

/// <param name="Renamed">Only the last path segment changed.</param>
public sealed record MoveResult(FileOutcome Outcome, string From, string To, bool IsDirectory, bool Renamed, string Detail = "")
{
    /// <summary>A file at the destination was replaced, or a folder merged into (2026-10-22, review J11: an overwrite was never said).</summary>
    public bool Replaced { get; init; }

    /// <summary>What a folder copy left out (2026-10-22, the fix plan's D17): every <c>.git</c>, and the guarded files, by their paths.</summary>
    public IReadOnlyList<string> LeftOut { get; init; } = [];

    /// <summary>The refusal is the destination's (2026-10-22, review J10: a refused <c>to</c> was named by the source's path).</summary>
    public bool AtDestination { get; init; }
}

/// <summary>What a <see cref="WorkingDirectory.Delete"/> removed, for good (in place since 2026-10-01, when File safe edits and its <c>.trash</c> went, the user's call).</summary>
public sealed record DeleteResult(FileOutcome Outcome, string Relative, bool IsDirectory, string Detail = "")
{
    /// <summary>How it went (later on 2026-10-15): for good, to the Recycle bin, or for good on a drive with no bin.</summary>
    public DeleteWay Way { get; init; }
}

/// <summary>What <see cref="WorkingDirectory.PurgeFolder"/> removed: the files, the folders and their bytes (<c>PurgeResult</c> until 2026-10-01).</summary>
public sealed record PurgeResult(FileOutcome Outcome, int Files, int Folders, long Bytes, string Detail = "")
{
    /// <summary>A purge through the Recycle bin (2026-10-18, <c>File deletes to Recycle bin</c>): its entries went one by one, the folder kept.</summary>
    public bool ToBin { get; init; }

    /// <summary>The folder's own entries (files and subfolders at its top) the purge's listing held.</summary>
    public int Entries { get; init; }

    /// <summary>How many of <see cref="Entries"/> the purge reached before it ended or stopped (2026-10-20).</summary>
    public int Done { get; init; }

    /// <summary>How many of <see cref="Entries"/> went to the bin.</summary>
    public int Recycled { get; init; }

    /// <summary>How many of <see cref="Entries"/> were deleted for good (2026-10-20: a purge without the bin; a purge through it never deletes for good).</summary>
    public int Deleted { get; init; }

    /// <summary>
    /// How many of <see cref="Entries"/> were links, removed directly, the link and never what it points at (2026-10-20, the code review: they
    /// were counted as deleted on a drive with no bin, and the notice blamed a missing bin). Not in <see cref="PurgeResult.Files"/> or <see cref="PurgeResult.Folders"/>.
    /// </summary>
    public int Links { get; init; }

    /// <summary>How many of <see cref="Entries"/> were already gone when the purge came to them (2026-10-20).</summary>
    public int Vanished { get; init; }

    /// <summary>How many of <see cref="Entries"/> the bin or the delete refused, left in place (2026-10-20); the first reason is the <see cref="PurgeResult.Detail"/>.</summary>
    public int Left { get; init; }

    /// <summary>Why the purge stopped before its last entry, if it did (2026-10-20).</summary>
    public PurgeStop Stop { get; init; }

    /// <summary>The entries the purge took away: to the bin, for good, or a link removed.</summary>
    public int Removed => Recycled + Deleted + Links;

    /// <summary>
    /// Two runs over one listing as one (2026-10-20: a purge through the bin that met a volume with no bin before anything went there, then the
    /// rest for good after a second yes): the counts added, the way and the stop the second run's.
    /// </summary>
    public PurgeResult Then(PurgeResult next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return next with
        {
            Outcome = Left + next.Left == 0 && next.Stop == PurgeStop.None ? next.Outcome : FileOutcome.Failed,
            Files = Files + next.Files,
            Folders = Folders + next.Folders,
            Bytes = Bytes + next.Bytes,
            Entries = Entries,
            Done = Done + next.Done,
            Recycled = Recycled + next.Recycled,
            Deleted = Deleted + next.Deleted,
            Links = Links + next.Links,
            Vanished = Vanished + next.Vanished,
            Left = Left + next.Left,
            Detail = next.Detail.Length > 0 ? next.Detail : Detail,
        };
    }
}

/// <summary>Why a purge stopped before its last entry (2026-10-20, the code review).</summary>
public enum PurgeStop
{
    /// <summary>It reached every entry.</summary>
    None,

    /// <summary>Esc (or the Web UI's Stop), read between entries.</summary>
    Cancelled,

    /// <summary>The bin answered that the drive has none: nothing was deleted for good, since the question promised the bin.</summary>
    NoBin,

    /// <summary>The bin didn't answer within <see cref="RecycleBin.Cap"/>: the next call would wait as long.</summary>
    NoAnswer,
}

/// <summary>
/// One of a purge's top entries (2026-10-20, <see cref="WorkingDirectory.ListPurge"/>): its full path, a folder or not, a link or not, and what it
/// holds (a file is one file; a folder counts itself and everything under it it could read; a link nothing).
/// </summary>
public sealed record PurgeEntry(string FullPath, bool IsDirectory, bool IsLink, int Files, int Folders, long Bytes);

/// <summary>
/// A folder listed once for its purge (2026-10-20, the code review: the question, the spinner's label and the work each listed it again):
/// what the question counts and what the purge works through. <see cref="FileOutcome.Missing"/> for no folder.
/// </summary>
public sealed class PurgeListing
{
    internal PurgeListing(FileOutcome outcome, string fullPath, IReadOnlyList<PurgeEntry> entries, string detail)
    {
        Outcome = outcome;
        FullPath = fullPath;
        Entries = entries;
        Detail = detail;
        foreach (var entry in entries)
        {
            Files += entry.Files;
            Folders += entry.Folders;
            Bytes += entry.Bytes;
            Links += entry.IsLink ? 1 : 0;
        }
    }

    internal static PurgeListing Refused(FileOutcome outcome, string fullPath, string detail = "") => new(outcome, fullPath, [], detail);

    public FileOutcome Outcome { get; }

    public string FullPath { get; }

    /// <summary>The folder's own entries in name order.</summary>
    public IReadOnlyList<PurgeEntry> Entries { get; }

    public string Detail { get; }

    public int Files { get; }

    public int Folders { get; }

    public long Bytes { get; }

    /// <summary>The top entries that are links (not in <see cref="Files"/> or <see cref="Folders"/>).</summary>
    public int Links { get; }

    /// <summary>The entries from <paramref name="skip"/> on, for a second run after the first stopped there.</summary>
    public PurgeListing Rest(int skip) => skip <= 0 ? this : new(Outcome, FullPath, Entries.Skip(skip).ToList(), Detail);
}

public sealed record ZipResult(FileOutcome Outcome, string Relative, string Archive, int Entries, long Bytes, string Detail = "")
{
    /// <summary>An archive (or, unzipped, a file) that was there before and was replaced (2026-10-22, review J11).</summary>
    public bool Replaced { get; init; }

    /// <summary>What was left out (2026-10-22, D17 and review N6): <c>.git</c>, guarded entries, a name a zip can't carry.</summary>
    public IReadOnlyList<string> LeftOut { get; init; } = [];

    /// <summary>The refusal is the archive's or the target folder's (review J10).</summary>
    public bool AtDestination { get; init; }
}

public sealed record OpenResult(FileOutcome Outcome, string Relative, bool IsDirectory, string Detail = "");

/// <summary>
/// How a <see cref="WorkingDirectory"/> treats its root (2026-09-30, the UNC tools: a share is a sandbox too, one per call,
/// with the same path check, caps and outcomes, but a remote folder someone else owns). <see cref="Sandbox"/> is the working
/// directory as it always was; <see cref="Share"/> is a UNC share's or an outside folder's.
/// </summary>
/// <param name="CreateRoot">A missing root is created by the first operation (the working directory); off, it is an error — a typo is never made into a folder.</param>
/// <param name="SearchParallelism">Files a content search reads at once; 0 is one per processor. A share's is 4, kind to the file server and the link.</param>
/// <param name="SearchByteBudget">Bytes a content search may read before it stops with what it has (<see cref="SearchResult.Budgeted"/>).</param>
/// <param name="MaxWalkEntries">Entries a search, find or recent walk looks at before it stops (<see cref="SearchResult.Budgeted"/>, <see cref="RecentResult.Budgeted"/>, a find's <c>Truncated</c>).</param>
/// <param name="PreserveOnReplace">An overwrite replaces the file in place (<see cref="File.Replace(string, string, string?, bool)"/>), keeping its ACL, owner and attributes, rather than moving a new file over it.</param>
/// <param name="FullPaths">
/// Every path this instance names is the full one, not one relative to the root (<see cref="WorkingDirectory.Relative"/>; 2026-10-09,
/// the profile's images folder): what it reports is what the model passes back, and a full path inside the root resolves as a
/// relative one does, so the image tools reach a picture there by the path a result gave (<see cref="WorkingDirectory.Contains"/>).
/// </param>
public sealed record WorkingDirectoryOptions(
    bool CreateRoot = true,
    int SearchParallelism = 0,
    long SearchByteBudget = long.MaxValue,
    int MaxWalkEntries = int.MaxValue,
    bool PreserveOnReplace = false,
    bool FullPaths = false)
{
    /// <summary>
    /// The profile's images folder (2026-10-09, the user's ask: the camera's, the screen's, ComfyUI's and the pasted pictures kept out of
    /// the working directory, under <c>&lt;profile&gt;/images</c>): created on first use as the working directory is, its paths named in full.
    /// </summary>
    public static readonly WorkingDirectoryOptions Images = new(FullPaths: true);

    /// <summary>The working directory: created on first use, no budgets.</summary>
    public static readonly WorkingDirectoryOptions Sandbox = new();

    /// <summary>A UNC share or outside folder (2026-09-30): never created, a search of 4 readers and 256 MB, walks of 100,000 entries, overwrites in place.</summary>
    public static readonly WorkingDirectoryOptions Share = new(
        CreateRoot: false,
        SearchParallelism: 4,
        SearchByteBudget: 256_000_000,
        MaxWalkEntries: 100_000,
        PreserveOnReplace: true);
}

/// <summary>
/// The per-profile working directory: the one folder the file tools may read and write. One
/// instance per app over a <c>Func&lt;string&gt;</c> that reads the live effective setting and
/// profile directory, so a <c>/cwd</c> save or a profile switch changes the root without a rebuild.
///
/// <para>Every operation takes a path relative to the root and resolves it through
/// <see cref="Resolve(string, bool, out string)"/>: <see cref="Path.GetFullPath(string)"/>, then a
/// prefix check against the root. Nothing here follows symlinks or junctions — the walks skip
/// reparse points and the check is on the spelling of the path, which is the documented limit.
/// A delete, an overwrite and an edit are permanent: the <c>.trash</c> folder that kept copies under File safe edits
/// went on 2026-10-01 (the user's call), and a <c>.trash</c> left behind is a folder like any other.</para>
///
/// <para>Nothing throws for a model's mistake: every result carries a <see cref="FileOutcome"/>,
/// and an <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> becomes
/// <see cref="FileOutcome.Failed"/> with the message. Nothing here touches the console.</para>
/// </summary>
public sealed class WorkingDirectory
{
    public const string DefaultFolderName = "files";

    /// <summary>The folder <c>delete</c> never touches, nor anything in it or a folder holding it (2026-09-23).</summary>
    public const string GitFolderName = ".git";
    public const string Category = "Files";

    /// <summary>Entries a directory listing shows by default (<c>search_files</c> with no <c>text</c> and no <c>files</c>; <c>/tree</c>'s neighbour).</summary>
    public const int MaxEntries = 200;

    /// <summary>
    /// The most rows any shape of <c>search_files</c> returns whatever its <c>limit</c> asks — hits, files with counts, the
    /// listing, the find and the recent list (2026-10-01, the user's ask: <c>MaxListLimit</c> and <c>MaxSearchLimit</c>, 200
    /// each, until then). The tools clamp a call to the <c>File search max results</c> setting first
    /// (<see cref="Settings.AppSettingsData.FileSearchMaxResults"/>), whose ceiling this is.
    /// </summary>
    public const int MaxResultLimit = 5000;

    /// <summary>The entries a reachability probe lists to count them (<c>unc_shares</c>, <c>--unc-check</c>, the UNC wizard's test): <c>MaxListLimit</c>'s old 200, fixed (2026-10-01).</summary>
    public const int ProbeListLimit = 200;

    /// <summary>The deepest a nested listing goes (<c>search_files</c>'s <c>depth</c> over the tree shape; <c>list_directory</c>'s until 2026-09-19).</summary>
    public const int MaxTreeDepth = 4;

    /// <summary>Paths a name search lists by default (<c>search_files</c> with <c>files</c> and no <c>text</c>; <c>MaxFindMatches</c> until 2026-09-19).</summary>
    public const int DefaultFindLimit = 100;

    /// <summary>Hits a content search returns by default (<c>MaxSearchMatches</c> until 2026-09-19, when <c>limit</c> came).</summary>
    public const int DefaultSearchLimit = 50;

    /// <summary>Files the recent list names by default (<c>search_files</c> with <c>order</c> modified).</summary>
    public const int DefaultRecent = 10;
    public const int MaxLineChars = 160;
    public const int MaxReadChars = 32_000;
    public const int MaxWriteChars = 200_000;

    /// <summary>Lines shown on each side of an edit in its result (2026-09-17).</summary>
    public const int EditContextLines = 2;

    /// <summary>The most lines an edit's result shows; a bigger region is left to <c>read_file</c>.</summary>
    public const int MaxEditRegionLines = 60;

    /// <summary>The most lines <c>search_files</c> shows on each side of a hit (<c>context</c>).</summary>
    public const int MaxSearchContext = 5;

    /// <summary>Characters of a line quoted in a refusal (the locations an ambiguous <c>patch_file</c> names).</summary>
    public const int MaxQuotedChars = 80;

    /// <summary>The largest file a text operation (search, info, edit) reads whole.</summary>
    public const long MaxTextFileBytes = 2_000_000;

    /// <summary>The largest file <see cref="ReadText"/> opens; a log beyond it is read by nothing here.</summary>
    public const long MaxReadFileBytes = 8_000_000;

    /// <summary>Entries a folder's <see cref="Info"/> walk counts before it stops.</summary>
    public const int MaxInfoEntries = 10_000;

    /// <summary>Rows a <see cref="Complete"/> keeps (the list above the input line).</summary>
    public const int MaxMentionMatches = 50;

    /// <summary>Entries a <see cref="Complete"/> walk looks at before it stops: the line's own task walks, so a huge tree must never hang it.</summary>
    public const int MaxMentionVisited = 5_000;

    /// <summary>How long one completion's walk may take before what it found is shown, cut (2026-10-22, review M5).</summary>
    public static readonly TimeSpan MaxMentionTime = TimeSpan.FromMilliseconds(250);

    /// <summary>The most files one completion's predicate is asked of (2026-10-22, review M5: /read's opens each one).</summary>
    public const int MaxMentionProbes = MaxMentionMatches * 4;

    /// <summary>The <c>File /tree max length</c> setting's range and default: entries a <see cref="FileTree"/> walk lists before it stops.</summary>
    public const int MinTreeLength = 1;
    public const int MaxTreeLength = 10_000;
    public const int DefaultTreeLength = 500;

    /// <summary>Bytes inspected for a NUL, which marks a file binary.</summary>
    public const int BinaryProbeBytes = 8_192;

    public static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Func<string> _root;
    private readonly TimeProvider _time;
    private readonly WorkingDirectoryOptions _options;

    // The files the model's own file tools read or wrote this session, each with the size and write time it had then (2026-10-10,
    // MarkSeen): write_file without a mode replaces one still as it was. Keyed by full path, case-blind where the file system is.
    private readonly Dictionary<string, SeenStamp> _seen = new(PathComparer);

    // The files read_file returned text of this session, a window too (2026-10-18, MarkViewed): a code audit's finding needs its file read.
    // The same stamp, the same SeenOff rule and the same lock as _seen.
    private readonly Dictionary<string, SeenStamp> _viewed = new(PathComparer);
    private readonly object _seenGate = new();

    /// <summary>
    /// How paths compare here (2026-10-18, exposed for the code audit's one path form): case-blind where the file system is — on Windows —
    /// and exact elsewhere, the comparer the seen files have always used.
    /// </summary>
    public static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    // Set for the flow of a turn that is not the main model's own (2026-10-11, the review: an advisor's read_file, over the chat's
    // own tools and this same instance, marked a file the main model never saw, and its write_file without a mode then replaced it;
    // a bot's the same both ways): such a flow neither marks a file nor finds one marked (SeenOff).
    private static readonly AsyncLocal<bool> s_seenOff = new();

    /// <param name="root">The resolved root, read on every call (<see cref="Resolve(string, string)"/> over the effective settings).</param>
    /// <param name="time">The clock behind the dates shown.</param>
    /// <param name="options">How the root is treated: <see cref="WorkingDirectoryOptions.Sandbox"/> (the default) for the working directory, <see cref="WorkingDirectoryOptions.Share"/> for a UNC share (2026-09-30).</param>
    public WorkingDirectory(Func<string> root, TimeProvider time, WorkingDirectoryOptions? options = null)
    {
        _root = root ?? throw new ArgumentNullException(nameof(root));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _options = options ?? WorkingDirectoryOptions.Sandbox;
    }

    /// <summary>
    /// A working directory whose root stays <paramref name="root"/> whatever the settings say later, with this one's options and guards (read live,
    /// as this one's are) and no files seen (2026-10-19, the second review of the Ralph loop: a loop's plan, log and lock are its own folder's, and a
    /// <c>/cwd</c> save or a profile switch mid-run sent its stop's writes, its judge and its unlock to the new root).
    /// </summary>
    public WorkingDirectory PinnedAt(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return new WorkingDirectory(() => root, _time, _options)
        {
            Databases = _databases,
            ShoppingListGuard = _shoppingListGuard,
            RecycleDeletes = _recycleDeletes,
            ServerDatabases = ServerDatabases,
            AdminFamilies = AdminFamilies,
            ImagesFolder = ImagesFolder,
            SkillFolders = SkillFolders,
        };
    }

    /// <summary>How this instance treats its root (<see cref="WorkingDirectoryOptions"/>).</summary>
    public WorkingDirectoryOptions Options => _options;

    /// <summary>
    /// The SQLite database files no file tool may change (2026-10-05, the user's call), read at every write: null, or a null
    /// answer, while the SQLite tools are off. Set on the working directory alone (never a share's); a write <see cref="Resolve(string, bool, out string)"/>
    /// to such a file is <see cref="FileOutcome.DatabaseProtected"/>, and a delete or move of a folder holding one too
    /// (<see cref="HoldsGuarded"/>), so a database changes only through <c>sqlite_execute</c>. The shell police reads it too.
    /// </summary>
    public Func<DatabaseGuard?>? Databases
    {
        // An instance with none of its own reads the process-wide facts (2026-10-22, review N4/W1: a share's, the skill editor's and every
        // other instance made fresh had no guard at all).
        get => _databases ?? WriteGuard.Facts?.Databases;
        set => _databases = value;
    }

    private Func<DatabaseGuard?>? _databases;

    /// <summary>
    /// Whether the shopping tools are offered (2026-10-17, the user's report), read at each write tool's call: while true, <c>write_file</c>,
    /// <c>patch_file</c>, <c>copy</c> and <c>move</c> refuse a destination named as a shopping or grocery list
    /// (<see cref="Shopping.ShoppingFileGuard"/>). Set on the working directory alone; null (a share's, a test's) never refuses. The check is the
    /// tools', not <see cref="Resolve(string, bool, out string)"/>'s: a delete, a move's source and the app's own writes
    /// (<c>/shopping export</c>) are never refused.
    /// </summary>
    public Func<bool>? ShoppingListGuard
    {
        get => _shoppingListGuard ?? WriteGuard.Facts?.ShoppingGuarded;
        set => _shoppingListGuard = value;
    }

    private Func<bool>? _shoppingListGuard;

    /// <summary>
    /// The bin a <see cref="Delete"/> moves to (later on 2026-10-15, <c>File deletes to Recycle bin</c>), read at each delete: the screen's and
    /// headless's working directory hand the system's bin while the setting is on, else null. Null (unset, a share's, a test's) deletes directly.
    /// </summary>
    public Func<IRecycleBin?>? RecycleDeletes
    {
        // The facts' bin when unset (2026-10-22, review V3/W4: the images folder's and a local share's deletes skipped the bin).
        get => _recycleDeletes ?? WriteGuard.Facts?.RecycleBin;
        set => _recycleDeletes = value;
    }

    private Func<IRecycleBin?>? _recycleDeletes;

    /// <summary>
    /// The server families the shell police guards (2026-10-05, the user's call: SQLite's rule mirrored for SQL Server, Oracle, MySQL
    /// and PostgreSQL), read at each shell call: null, or a null answer, while none of their tools is on. The file tools never read
    /// it (a server's database is no file here); the shell tools do (<c>Shell.ServerDatabasePolice</c>).
    /// </summary>
    public Func<Shell.ServerDatabaseGuard?>? ServerDatabases { get; set; }

    /// <summary>
    /// The admin families the shell police guards (2026-10-15, the admin-tools plan): services, tasks and the directory while their tools are
    /// on, read at each shell call; null, or a null answer, while none is. The shell tools read it (<c>Shell.AdminPolice</c>).
    /// </summary>
    public Func<Shell.AdminGuard?>? AdminFamilies { get; set; }

    /// <summary>
    /// The profile's images folder, a full path (2026-10-09, the user's call, after a model listed <c>images</c> and then the folder's
    /// full path with <c>search_files</c> and was told only "outside the working directory"): read when a path is refused as outside,
    /// so one inside that folder is <see cref="FileOutcome.InImagesFolder"/>, whose sentence points at the tools' descriptions, which say
    /// whether they reach it (it named six tools until code review, 2026-10-11, whatever the turn offered). Set on the
    /// working directory alone (never a share's, nor the images folder's own); null, or a null answer, keeps every refusal
    /// <see cref="FileOutcome.OutsideRoot"/>. It reaches nothing itself: those tools route a full path there (<see cref="Llm.Tools.FileTool.For"/>).
    /// </summary>
    public Func<string?>? ImagesFolder { get; set; }

    /// <summary>
    /// The skill roots whose skills the model reads with <c>load_skill</c> (2026-10-17, the toki-pona session: <c>read_file</c> and
    /// <c>file_info</c> on a skill's file by its full path were told only "outside the working directory"): read when a path is refused as
    /// outside, so one inside a root is <see cref="FileOutcome.InSkillFolder"/>, whose sentence names <c>load_skill</c>. The host returns
    /// the roots only while skills are offered, so the sentence never names a tool that is not. Set on the working directory alone; null,
    /// or an empty answer, keeps every refusal <see cref="FileOutcome.OutsideRoot"/>.
    /// </summary>
    public Func<IReadOnlyList<string>>? SkillFolders { get; set; }

    /// <summary>The setting's meaning: blank is the profile's <see cref="DefaultFolderName"/> folder, anything else a full path.</summary>
    public static string Resolve(string configured, string profileDirectory)
    {
        ArgumentNullException.ThrowIfNull(profileDirectory);
        string path = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(profileDirectory, DefaultFolderName)
            : configured.Trim();
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    public static bool IsDefault(string configured) => string.IsNullOrWhiteSpace(configured);

    /// <summary>The root, full and without a trailing separator.</summary>
    public string Root => Path.TrimEndingDirectorySeparator(Path.GetFullPath(_root()));

    /// <summary>
    /// Creates the root when it is missing and returns it. Throws on failure; the tools translate. Under
    /// <see cref="WorkingDirectoryOptions.CreateRoot"/> off (a share, 2026-09-30) a missing root is never created — a read
    /// that made a typo into a folder would be a write to a read-only share — and throws <see cref="DirectoryNotFoundException"/>.
    /// </summary>
    public string EnsureExists()
    {
        string root = Root;
        if (!_options.CreateRoot)
        {
            return Directory.Exists(root) ? root : throw new DirectoryNotFoundException(RootMissingMessage(root));
        }

        // A root seen this session and gone since is never made again (2026-10-22, review S6): every write's EnsureExists quietly
        // re-made a working directory the user deleted or renamed. The first use still makes one that never existed (<profile>\files).
        if (Directory.Exists(root))
        {
            NoteSeenRoot(root);
            return root;
        }

        if (RootSeen(root))
        {
            throw new DirectoryNotFoundException(FileText.RootGone);
        }

        Directory.CreateDirectory(root);
        NoteSeenRoot(root);
        return root;
    }

    // The roots this instance has seen exist (2026-10-22, review S6), each by its full path.
    private readonly HashSet<string> _seenRoots = new(PathComparer);

    private void NoteSeenRoot(string root)
    {
        lock (_seenRoots)
        {
            _seenRoots.Add(root);
        }
    }

    private bool RootSeen(string root)
    {
        lock (_seenRoots)
        {
            return _seenRoots.Contains(root);
        }
    }

    /// <summary>What a missing root that is not created says (<see cref="WorkingDirectoryOptions.CreateRoot"/> off). Pinned.</summary>
    public static string RootMissingMessage(string root) => $"{root} does not exist or cannot be reached";

    /// <summary>Whether the root exists, without creating it.</summary>
    public bool Exists => Directory.Exists(Root);

    /// <summary>
    /// Whether <paramref name="path"/> is a full path naming the root or something under it, by spelling (2026-10-09: which of the
    /// working directory and the profile's images folder a picture tool's path belongs to, <see cref="Llm.Tools.FileTool"/>). A
    /// relative path is never contained; <see cref="Resolve(string, bool, out string)"/> still judges the links.
    /// </summary>
    // Whether a refused candidate lies in the profile's images folder (ImagesFolder); a bad hook answer is no folder.
    private bool InImagesFolderOf(string candidate)
    {
        try
        {
            return ImagesFolder?.Invoke() is { Length: > 0 } images && IsInside(Path.TrimEndingDirectorySeparator(Path.GetFullPath(images)), candidate);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    // Whether a refused candidate lies in a skill root (SkillFolders); a bad hook answer is no root.
    private bool InSkillFolderOf(string candidate)
    {
        try
        {
            return SkillFolders?.Invoke() is { } roots
                && roots.Any(root => root.Length > 0 && IsInside(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), candidate));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public bool Contains(string path)
    {
        string text = (path ?? "").Trim();
        if (text.Length == 0 || !Path.IsPathFullyQualified(text))
        {
            return false;
        }

        try
        {
            return IsInside(Root, Path.TrimEndingDirectorySeparator(Path.GetFullPath(text)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// The sandbox: <paramref name="relative"/> against the root, accepted only when it stays
    /// inside. The one place a path is judged; <paramref name="forWrite"/> marks a caller that will write (no refusal of its own
    /// since the <c>.trash</c> went, 2026-10-01). Since 2026-10-03 (the user's pick) inside means by the links too: a junction
    /// or symlink under the root whose target lies outside it (<see cref="LinkEscape"/>) is <see cref="FileOutcome.OutsideRoot"/>,
    /// so <c>link\x</c> with <c>link → C:\</c> is refused as <c>C:\x</c> would be; until that day the spelling alone decided.
    /// </summary>
    public FileOutcome Resolve(string relative, bool forWrite, out string full) => Resolve(relative, forWrite, linkItself: false, out full);

    /// <summary>
    /// <see cref="Resolve(string, bool, out string)"/>, with <paramref name="linkItself"/> for a caller that acts on the last
    /// name itself and never through it (2026-10-03, the review: a delete or a move of a link that leads outside removes or
    /// renames the link, its target untouched — refused until then, so the model could not clear away the very link the rule
    /// complained of): that name is not asked where it leads; every folder before it still is.
    /// </summary>
    private FileOutcome Resolve(string relative, bool forWrite, bool linkItself, out string full)
    {
        full = "";
        string root = Root;
        string text = (relative ?? "").Trim();
        if (_options.CreateRoot && RootSeen(root) && !Directory.Exists(root))
        {
            return FileOutcome.RootGone;
        }

        if (text.Length == 0 || text == ".")
        {
            full = root;
            return FileOutcome.Ok;
        }

        // A home-folder or file: URL form (2026-10-22, review S5): read as a relative name it made a literal ~ folder, or a file: one.
        if (text == "~" || text.StartsWith("~/", StringComparison.Ordinal) || text.StartsWith("~\\", StringComparison.Ordinal)
            || text.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return FileOutcome.NotAPathHere;
        }

        // A name Windows keeps for a device (2026-10-22, review O10): GetFullPath made <dir>\NUL into \\.\NUL, read as outside.
        if (OperatingSystem.IsWindows() && HasDeviceName(text))
        {
            return FileOutcome.DeviceName;
        }

        string candidate;
        try
        {
            candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(root, text)));
        }
        catch (ArgumentException)
        {
            return FileOutcome.OutsideRoot;
        }
        catch (PathTooLongException)
        {
            return FileOutcome.OutsideRoot;
        }

        // The root's real spelling (/private/tmp/x for /tmp/x on a Mac) is the root too.
        candidate = InRootSpelling(root, candidate, RealPath.Of);

        // A short (8.3) name is judged as the long one it stands for (2026-10-22, review G2/G3: APPLIC~1.SQL passed the database guard,
        // GIT~1 the .git one). Only a path that holds a '~', and only its existing part.
        if (OperatingSystem.IsWindows() && candidate.Contains('~', StringComparison.Ordinal))
        {
            candidate = LongNames(root, candidate);
        }

        string walked = linkItself ? Path.GetDirectoryName(candidate) ?? candidate : candidate;
        if (!IsInside(root, candidate))
        {
            return InImagesFolderOf(candidate) ? FileOutcome.InImagesFolder
                : InSkillFolderOf(candidate) ? FileOutcome.InSkillFolder
                : FileOutcome.OutsideRoot;
        }

        // A stream name (2026-10-22, review M2): a.txt:payload hid data, and a delete of x.exe:Zone.Identifier stripped the Mark of the Web.
        // The root's own drive letter is the only ':' a Windows path here holds.
        if (OperatingSystem.IsWindows() && candidate.IndexOf(':', 2) >= 0)
        {
            return FileOutcome.StreamName;
        }

        if (LinkEscape(root, walked, RealLinkTarget) is not null)
        {
            return FileOutcome.OutsideRoot;
        }

        // The model's writes judged by the file they reach (2026-10-22, WriteGuard): .git, .neon, the app's home, the skill roots, the project
        // file (asked), a SQLite database (2026-10-05), a shopping list's name. A delete or a move's source takes the target away (Remove).
        if (forWrite && WriteGuard.Judge(root, candidate, linkItself ? WriteRole.Remove : WriteRole.Write, ShoppingListGuard, Databases) is { } guarded)
        {
            return guarded;
        }

        // Plain files only, reads too (2026-10-22, review N1/N2): a read of a FIFO waits forever, on the turn's thread or in the vault's lock.
        if (OperatingSystem.IsMacOS() && Platform.MacFiles.KindOf(candidate) == Platform.MacFiles.Kind.Other)
        {
            return FileOutcome.NotARegularFile;
        }

        full = candidate;
        return FileOutcome.Ok;
    }

    /// <summary>
    /// Whether a segment of <paramref name="text"/> is a name Windows keeps for a device (2026-10-22, review O10): <c>CON</c>, <c>PRN</c>,
    /// <c>AUX</c>, <c>NUL</c>, <c>COM0</c>–<c>COM9</c>, <c>LPT0</c>–<c>LPT9</c> (and the superscript digits), with or without an extension, trailing
    /// dots and spaces ignored. Pure.
    /// </summary>
    internal static bool HasDeviceName(string text)
    {
        foreach (string part in text.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            string bare = part.TrimEnd('.', ' ');
            int dot = bare.IndexOf('.');
            string stem = (dot >= 0 ? bare[..dot] : bare).TrimEnd(' ');
            if (stem.Length == 3 && (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                && (char.IsAsciiDigit(stem[3]) || stem[3] is '\u00B9' or '\u00B2' or '\u00B3'))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// <paramref name="candidate"/> with each existing segment under <paramref name="root"/> that holds a <c>~</c> replaced by its long name
    /// (Windows' directory search matches a short name and answers the long one; no native call). A segment that matches nothing, or holds a
    /// wildcard, ends the walk: the rest is a name still to be made.
    /// </summary>
    private static string LongNames(string root, string candidate)
    {
        if (!IsInside(root, candidate) || Under(root, candidate).Length == 0)
        {
            return candidate;
        }

        string current = root;
        string[] parts = Under(root, candidate).Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            if (part.Contains('~', StringComparison.Ordinal) && part.IndexOfAny(['*', '?']) < 0)
            {
                string? found = null;
                try
                {
                    found = new DirectoryInfo(current).EnumerateFileSystemInfos(part, new EnumerationOptions { IgnoreInaccessible = true, MatchType = MatchType.Win32 })
                        .Select(info => info.Name).FirstOrDefault();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
                {
                }

                if (found is null)
                {
                    return Path.Join([current, .. parts[i..]]);
                }

                part = found;
            }

            current = Path.Join(current, part);
        }

        return current;
    }

    /// <summary>How many links one judgement follows before it gives up and calls the path outside (a loop of links never ends otherwise).</summary>
    internal const int MaxLinkHops = 32;

    /// <summary>
    /// The link under <paramref name="root"/> that takes <paramref name="candidate"/> outside it, or null when none does
    /// (2026-10-03, the user's pick; the file tools through <see cref="Resolve"/>, the shell police through
    /// <see cref="Shell.PathPolice"/>). <paramref name="candidate"/> is full and inside the root by its spelling. Its folders are
    /// walked down from the root, each asked of <paramref name="linkTarget"/> (a link's full final target, else null): a link
    /// leading outside is named; one leading inside is followed — the rest of the path moved onto its target and the walk
    /// begun again, so a link inside the target is seen too — up to <see cref="MaxLinkHops"/>. The root itself is never
    /// asked: a working directory that is itself a link is the user's own pick. Pure but for <paramref name="linkTarget"/>
    /// (<see cref="RealLinkTarget"/> in the app, a fake in the tests).
    /// </summary>
    internal static string? LinkEscape(string root, string candidate, Func<string, string?> linkTarget)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(linkTarget);
        string path = candidate;
        int hops = 0;
        while (true)
        {
            if (!IsInside(root, path) || Under(root, path).Length == 0)
            {
                return null;
            }

            // This system's separators alone (2026-10-22, review N3: a Mac link named a\b was split in two and never asked).
            string[] parts = Under(root, path).Split(Separators, StringSplitOptions.RemoveEmptyEntries);
            string current = root;
            string? moved = null;
            for (int i = 0; i < parts.Length; i++)
            {
                string next = Path.Join(current, parts[i]);
                if (linkTarget(next) is { } target)
                {
                    string landed = Path.TrimEndingDirectorySeparator(target);
                    if (!IsInside(root, landed) || ++hops > MaxLinkHops)
                    {
                        return next;
                    }

                    moved = i + 1 < parts.Length ? Path.Join(landed, string.Join(Path.DirectorySeparatorChar, parts[(i + 1)..])) : landed;
                    break;
                }

                current = next;
            }

            if (moved is null)
            {
                return null;
            }

            path = moved;
        }
    }

    /// <summary>
    /// The full final target of the junction or symlink at <paramref name="path"/>, or null when it is not one — missing, a
    /// plain file or folder, or another kind of reparse point (a OneDrive placeholder, a deduplicated file), which
    /// <see cref="FileSystemInfo.LinkTarget"/> does not name. The attributes are the link's own, so a link whose target is
    /// missing is still seen. A link whose target cannot be worked out is the empty string, which no root contains: refused,
    /// never followed blind.
    /// </summary>
    internal static string? RealLinkTarget(string path)
    {
        // FileSystemInfo.Attributes reads a missing path as -1 without throwing, where File.GetAttributes threw and was caught:
        // the shell police asks this of every plain token of a script, most of them no file at all (2026-10-03, the review).
        FileAttributes attributes;
        try
        {
            attributes = new FileInfo(path).Attributes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }

        if ((int)attributes == -1 || (attributes & FileAttributes.ReparsePoint) == 0)
        {
            return null;
        }

        FileSystemInfo info = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(path) : new FileInfo(path);
        try
        {
            if (info.LinkTarget is not { } target)
            {
                return null;
            }

            return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                ?? Path.GetFullPath(target, Path.GetDirectoryName(path) ?? path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return "";
        }
    }

    /// <summary>
    /// The display form: relative to the root, <c>\</c> separated, a trailing <c>\</c> for a folder, empty for the root itself — or,
    /// under <see cref="WorkingDirectoryOptions.FullPaths"/>, the full path (a trailing separator for a folder, the root's own path).
    /// </summary>
    public string Relative(string full, bool isDirectory = false)
    {
        string root = Root;
        string trimmed = Path.TrimEndingDirectorySeparator(full);
        if (_options.FullPaths)
        {
            return isDirectory ? trimmed + Path.DirectorySeparatorChar : trimmed;
        }

        if (string.Equals(trimmed, root, CaseRule.Comparison(root)))
        {
            return "";
        }

        string rel = IsInside(root, trimmed) ? Under(root, trimmed) : trimmed;
        return isDirectory ? rel + Path.DirectorySeparatorChar : rel;
    }

    // ---- read side ----

    /// <summary>The folder's own entries, folders first; at most <paramref name="limit"/> (clamped to <see cref="MaxResultLimit"/>), <c>Truncated</c> past it.</summary>
    public ListResult List(string relative, int limit = MaxEntries) => List(relative, CancellationToken.None, limit);

    /// <summary><see cref="List(string, int)"/> under the call's token, the folder's first <paramref name="limit"/> kept in a heap (review I11: every entry was held and sorted).</summary>
    public ListResult List(string relative, CancellationToken cancellationToken, int limit = MaxEntries)
    {
        limit = Math.Clamp(limit, 1, MaxResultLimit);
        var outcome = Resolve(relative, forWrite: false, out string full);
        if (outcome != FileOutcome.Ok)
        {
            return new ListResult(outcome, relative, [], false);
        }

        string display = Relative(full, isDirectory: true);
        try
        {
            EnsureExists();
            if (File.Exists(full))
            {
                return new ListResult(FileOutcome.IsAFile, Relative(full), [], false);
            }

            if (!Directory.Exists(full))
            {
                return new ListResult(FileOutcome.Missing, display, [], false);
            }

            // The same entries every other walk sees (2026-10-22, review Q10: the listing showed hidden, system and link entries that find,
            // search and recent never reach, desktop.ini among them): those left out, the hidden counted, temp siblings never shown.
            var kept = new PriorityQueue<DirectoryEntry, DirectoryEntry>(limit + 2, Comparer<DirectoryEntry>.Create((a, b) => CompareEntries(b, a)));
            int hidden = 0, seen = 0;
            foreach (var info in new DirectoryInfo(full).EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }))
            {
                if ((++seen & 1023) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                bool isDirectory = (info.Attributes & FileAttributes.Directory) != 0;
                bool isHidden = OperatingSystem.IsWindows()
                    ? (info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0
                    : IsHiddenOffWindows(info.Attributes, info.Name);
                if (isHidden)
                {
                    hidden++;
                    continue;
                }

                if (IsLink(info) || IsTempSibling(info.Name))
                {
                    continue;
                }

                var added = new DirectoryEntry(info.Name, isDirectory, isDirectory ? 0 : ((FileInfo)info).Length);
                kept.Enqueue(added, added);
                if (kept.Count > limit + 1)
                {
                    kept.Dequeue();
                }
            }

            var entries = new List<DirectoryEntry>(kept.Count);
            while (kept.TryDequeue(out var entry, out _))
            {
                entries.Add(entry);
            }

            entries.Sort(CompareEntries);
            bool truncated = entries.Count > limit;
            if (truncated)
            {
                entries.RemoveRange(limit, entries.Count - limit);
            }

            return new ListResult(FileOutcome.Ok, display, entries, truncated) { Hidden = hidden };
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return new ListResult(FileOutcome.Failed, display, [], false, ex.Message);
        }
    }

    /// <summary>
    /// <c>/tree</c>: every folder and file under <paramref name="relative"/>, depth first, folders
    /// before files at each level, no depth limit. Stops at <paramref name="maxEntries"/> (clamped to
    /// <see cref="MinTreeLength"/>..<see cref="MaxTreeLength"/>) with <c>Truncated</c>. <paramref name="maxDepth"/>
    /// (2026-09-18, the nested listing's <c>depth</c>) stops the descent that many levels down; the default
    /// is every level. <paramref name="hideDotEntries"/> (2026-09-22, <c>/vault</c>) leaves out every file and
    /// folder whose name starts with a dot, at any depth — <c>.obsidian</c>, <c>.trash</c>, <c>.git</c>, the
    /// entries the vault tools leave to Obsidian. <paramref name="showHidden"/> (2026-09-23, <c>/tree</c> under
    /// <c>File browser/tree mode</c> <c>show-hidden</c>) lists the entries with the Hidden or System attribute too
    /// (<c>.git</c> on Windows), which every walk leaves out otherwise; reparse points stay out either way.
    /// <paramref name="hideGitFolders"/> (2026-09-30, <c>/tree</c>, the user's ask) leaves
    /// out every folder named <c>.git</c>, at any depth — a nested repository's or a submodule's too — whatever
    /// <paramref name="showHidden"/> says; the folder asked for itself is still walked.
    /// </summary>
    public FileTreeResult FileTree(string relative, int maxEntries, int maxDepth = int.MaxValue, bool hideDotEntries = false, bool showHidden = false, bool hideGitFolders = false)
    {
        var outcome = Resolve(relative, forWrite: false, out string full);
        if (outcome != FileOutcome.Ok)
        {
            return new FileTreeResult(outcome, relative, "", [], false);
        }

        string display = Relative(full, isDirectory: true);
        string header = full + Path.DirectorySeparatorChar;
        try
        {
            EnsureExists();
            if (File.Exists(full))
            {
                return new FileTreeResult(FileOutcome.IsAFile, Relative(full), header, [], false);
            }

            if (!Directory.Exists(full))
            {
                return new FileTreeResult(FileOutcome.Missing, display, header, [], false);
            }

            int cap = Math.Clamp(maxEntries, MinTreeLength, MaxTreeLength);
            var entries = new List<FileTreeEntry>();
            bool truncated = !DescendAll(full, 1, cap, Math.Max(1, maxDepth), hideDotEntries, showHidden, hideGitFolders, entries);
            return new FileTreeResult(FileOutcome.Ok, display, header, entries, truncated);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return new FileTreeResult(FileOutcome.Failed, display, header, [], false, ex.Message);
        }
    }

    /// <summary>Depth-first, folders first then names per level; false once <paramref name="cap"/> entries are listed and more remain.</summary>
    private bool DescendAll(string directory, int depth, int cap, int maxDepth, bool hideDotEntries, bool showHidden, bool hideGitFolders, List<FileTreeEntry> entries)
    {
        var children = new List<DirectoryEntry>();
        try
        {
            var options = WalkOptions(recurse: false);
            if (showHidden)
            {
                options.AttributesToSkip = 0;
            }

            foreach (var info in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", options))
            {
                bool isDirectory = (info.Attributes & FileAttributes.Directory) != 0;
                if (IsLink(info) || IsTempSibling(info.Name)
                    || (hideDotEntries && info.Name.StartsWith('.'))
                    || (!showHidden && IsHiddenOffWindows(info.Attributes, info.Name))
                    || (hideGitFolders && isDirectory && string.Equals(info.Name, GitFolderName, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                children.Add(new DirectoryEntry(info.Name, isDirectory, isDirectory ? 0 : ((FileInfo)info).Length));
            }
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            DiagnosticLog.Debug(Category, $"Skipped {directory}: {ex.Message}");
            return true;
        }

        children.Sort(CompareEntries);
        for (int i = 0; i < children.Count; i++)
        {
            if (entries.Count >= cap)
            {
                return false;
            }

            var child = children[i];
            entries.Add(new FileTreeEntry(child.Name, depth, child.IsDirectory, child.Length, i == children.Count - 1));
            if (child.IsDirectory && depth < maxDepth && !DescendAll(Path.Combine(directory, child.Name), depth + 1, cap, maxDepth, hideDotEntries, showHidden, hideGitFolders, entries))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The files whose names (or paths, under a path glob) match, every level down to <paramref name="maxDepth"/>, at most <paramref name="limit"/> (clamped to <see cref="MaxResultLimit"/>).</summary>
    public FindResult Find(string namePattern, string relative, int limit = DefaultFindLimit, int maxDepth = int.MaxValue) =>
        Find(namePattern, relative, CancellationToken.None, limit, maxDepth);

    /// <summary><see cref="Find(string, string, int, int)"/> under the call's token, its walk budgeted over every entry looked at (review G18).</summary>
    public FindResult Find(string namePattern, string relative, CancellationToken cancellationToken, int limit = DefaultFindLimit, int maxDepth = int.MaxValue)
    {
        limit = Math.Clamp(limit, 1, MaxResultLimit);
        var outcome = Resolve(relative, forWrite: false, out string full);
        if (outcome != FileOutcome.Ok)
        {
            return new FindResult(outcome, relative, [], false);
        }

        string display = Relative(full, isDirectory: true);
        string pattern = (namePattern ?? "").Trim();
        if (pattern.Length == 0)
        {
            pattern = "*";
        }

        try
        {
            EnsureExists();
            if (!Directory.Exists(full))
            {
                return new FindResult(File.Exists(full) ? FileOutcome.IsAFile : FileOutcome.Missing, display, [], false);
            }

            var paths = new List<string>();
            bool truncated = false;
            var hidden = new HiddenCount { Max = _options.MaxWalkEntries, Token = cancellationToken };
            try
            {
                foreach (var entry in Walk(full, pattern, recurse: true, hidden, maxDepth: maxDepth))
                {
                    if (paths.Count >= limit)
                    {
                        truncated = true;
                        break;
                    }

                    paths.Add(Relative(entry.FullPath));
                }
            }
            catch (WalkBudgetSpent)
            {
                truncated = true;
            }

            paths.Sort(StringComparer.OrdinalIgnoreCase);
            return new FindResult(FileOutcome.Ok, display, paths, truncated) { Hidden = hidden.Count };
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return new FindResult(FileOutcome.Failed, display, [], false, ex.Message);
        }
    }

    /// <summary>
    /// The @-mention completions for <paramref name="query"/>, the text typed after the <c>@</c>:
    /// the part before its last <c>/</c> (or <c>\</c>) is the folder to look in (none = the root),
    /// the part after it a name prefix. An empty prefix lists the folder's one level (a bare <c>@</c>,
    /// a folder just applied); a prefix walks the folder's whole subtree for every file and folder
    /// whose NAME starts with it, ignoring case. Paths come back relative, <c>/</c>-spelled, a
    /// trailing <c>/</c> on a folder, folders first then by name; at most <see cref="MaxMentionMatches"/>,
    /// the walk itself stopping after <see cref="MaxMentionVisited"/> entries (either cut sets
    /// <c>Truncated</c>). Like the other walks (and unlike <see cref="List"/>) hidden, system and
    /// reparse-point entries are skipped. A folder outside the root
    /// (<c>..</c>), missing or a file yields nothing with the outcome; the
    /// root is never created here. With <paramref name="files"/> (2026-09-17) a file is listed
    /// only when the predicate keeps its full path — <see cref="IsTextFile"/> for the <c>/read</c>
    /// list, the probe <see cref="ReadText"/> judges by, so what is offered will read;
    /// <see cref="ImageFile.IsImagePath"/> for <c>/view</c>'s; folders always pass, and the match cap
    /// counts what is shown.
    /// </summary>
    public MentionResult Complete(string query, Func<string, bool>? files = null)
    {
        string text = (query ?? "").Trim();
        int cut = text.LastIndexOfAny(['/', '\\']);
        string folder = cut < 0 ? "" : text[..cut];
        string prefix = cut < 0 ? text : text[(cut + 1)..];

        var outcome = Resolve(folder, forWrite: false, out string full);
        if (outcome != FileOutcome.Ok)
        {
            return new MentionResult(outcome, [], false);
        }

        try
        {
            if (!Directory.Exists(full))
            {
                return new MentionResult(File.Exists(full) ? FileOutcome.IsAFile : FileOutcome.Missing, [], false);
            }

            // This runs on the input thread at each keystroke (2026-10-22, review M5): the walk has a time budget as well as its entry
            // budget, a folder on a network or optical drive is listed one level only, and the file predicate (/read's IsTextFile opens
            // each file) is asked only of the names that would be shown, in their order, at most MaxMentionProbes times.
            bool slow = FolderCompleter.IsSlowTarget(full);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var named = new List<(DirectoryEntry Entry, string Full)>();
            bool truncated = false;
            int seen = 0;
            foreach (var entry in Walk(full, null, recurse: prefix.Length > 0 && !slow, includeDirectories: true))
            {
                if (++seen > MaxMentionVisited || ((seen & 63) == 0 && clock.Elapsed > MaxMentionTime))
                {
                    truncated = true;
                    break;
                }

                if (Path.GetFileName(entry.FullPath).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    named.Add((new DirectoryEntry(Relative(entry.FullPath, entry.IsDirectory).Replace('\\', '/'), entry.IsDirectory, 0), entry.FullPath));
                }
            }

            named.Sort((x, y) => CompareEntries(x.Entry, y.Entry));
            var entries = new List<DirectoryEntry>();
            int probes = 0;
            foreach (var (entry, path) in named)
            {
                if (entries.Count >= MaxMentionMatches)
                {
                    truncated = true;
                    break;
                }

                if (entry.IsDirectory || files is null)
                {
                    entries.Add(entry);
                    continue;
                }

                if (++probes > MaxMentionProbes || clock.Elapsed > MaxMentionTime)
                {
                    truncated = true;
                    break;
                }

                if (files(path))
                {
                    entries.Add(entry);
                }
            }

            return new MentionResult(FileOutcome.Ok, entries.Select(e => e.Name).ToList(), truncated);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return new MentionResult(FileOutcome.Failed, [], false, ex.Message);
        }
    }

    /// <summary>
    /// A parallel, bounded content search: every text file under the path (≤ <see cref="MaxTextFileBytes"/>,
    /// no NUL in its head) scanned line by line for <paramref name="text"/>, case-insensitive; the
    /// walk stops once <paramref name="limit"/> hits (clamped to <see cref="MaxResultLimit"/>) are in hand. Cancellation throws.
    /// <paramref name="context"/> lines (0–<see cref="MaxSearchContext"/>) on each side ride every hit
    /// (2026-09-17); they count toward nothing. <paramref name="filesPattern"/> is a name glob, or a
    /// path glob over the file's path under the searched folder (or under the root) when it holds a
    /// separator or <c>**</c> (<see cref="PathGlob"/>). <paramref name="maxDepth"/> stops the walk that many
    /// levels down. Under <see cref="SearchOutput.Files"/> (2026-09-19) no hit is kept: each file's matching
    /// lines are counted into <see cref="SearchResult.Files"/> and the walk stops at <paramref name="limit"/> files.
    /// </summary>
    public SearchResult Search(string text, string relative, string? filesPattern, bool regex, CancellationToken cancellationToken, int context = 0, int limit = DefaultSearchLimit, int maxDepth = int.MaxValue, SearchOutput output = SearchOutput.Content)
    {
        context = Math.Clamp(context, 0, MaxSearchContext);
        limit = Math.Clamp(limit, 1, MaxResultLimit);
        bool countOnly = output == SearchOutput.Files;
        var outcome = Resolve(relative, forWrite: false, out string full);
        if (outcome != FileOutcome.Ok)
        {
            return Empty(outcome, relative);
        }

        string display = Relative(full, isDirectory: true);
        // The needle as given (2026-10-22, review O8: it was trimmed, so "  return x" matched lines with no leading spaces and a regex lost
        // its own); only a trailing line break is taken off. A blank one is no search.
        string needle = (text ?? "").TrimEnd('\r', '\n');
        if (needle.Trim().Length == 0)
        {
            return Empty(FileOutcome.Empty, display);
        }

        // The search reads line by line, so a needle with a line break never matched, and the answer was "0 matches" (2026-10-22, review Q9).
        if (needle.Contains('\n', StringComparison.Ordinal) || needle.Contains('\r', StringComparison.Ordinal))
        {
            return Empty(FileOutcome.OneLineOnly, display);
        }

        Regex? expression = null;
        if (regex)
        {
            try
            {
                expression = new Regex(needle, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
            }
            catch (ArgumentException ex)
            {
                return Empty(FileOutcome.BadPattern, display, ex.Message);
            }
        }

        string pattern = string.IsNullOrWhiteSpace(filesPattern) ? "*" : filesPattern.Trim();
        long started = _time.GetTimestamp();
        try
        {
            EnsureExists();
            // One file named as the path is searched alone (2026-09-18; a model searched a 1,900-line
            // HTML by its path, was told it was a file, and searched the whole folder instead).
            bool singleFile = File.Exists(full);
            if (singleFile)
            {
                display = Relative(full);
            }
            else if (!Directory.Exists(full))
            {
                return Empty(FileOutcome.Missing, display);
            }

            // In path order, the volume's own case rule, every time (2026-10-22, review Q6, Q13: a capped search kept whatever the parallel
            // workers found first and called it "the first N"; A.txt and a.txt interleaved). Files are handed out one at a time in that order
            // and each one started is finished, so what comes back is the hits of a prefix of the files: the same first N on every run.
            var hidden = new HiddenCount();
            var comparer = CaseRule.Comparer(full);
            var entries = new List<WalkEntry>();
            bool budgeted = false;
            if (singleFile)
            {
                var info = new FileInfo(full);
                // A filter the one file doesn't match is said, never ignored (2026-10-22, review V10).
                if (!string.IsNullOrWhiteSpace(filesPattern) && !MatchesName(pattern, full, Path.GetDirectoryName(full)!, Root))
                {
                    return Empty(FileOutcome.Ok, display) with { SingleFile = true, Unsearched = FileText.SearchFilterMissesFile(display, pattern) };
                }

                entries.Add(new WalkEntry(full, info.Length, info.LastWriteTimeUtc, false, info.Attributes));
            }
            else
            {
                foreach (var entry in Walk(full, pattern, recurse: true, hidden, maxDepth: maxDepth))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entries.Count >= _options.MaxWalkEntries)
                    {
                        budgeted = true;
                        break;
                    }

                    entries.Add(entry);
                }

                entries.Sort((a, b) => comparer.Compare(a.FullPath, b.FullPath));
            }

            var fileHits = new List<SearchHit>?[entries.Count];
            var fileCounts = new int[entries.Count];
            int matches = 0, searched = 0, timedOut = 0, cloudOnly = 0, matchedFiles = 0;
            string? unsearched = null;
            long bytesRead = 0;
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = _options.SearchParallelism > 0 ? _options.SearchParallelism : Environment.ProcessorCount,
                CancellationToken = cancellationToken,
            };
            var ordered = System.Collections.Concurrent.Partitioner.Create(Enumerable.Range(0, entries.Count), System.Collections.Concurrent.EnumerablePartitionerOptions.NoBuffering);
            Parallel.ForEach(ordered, options, (index, state) =>
            {
                var entry = entries[index];
                if (entry.Length > MaxTextFileBytes)
                {
                    unsearched ??= singleFile ? FileText.SearchFileTooBig(display) : null;
                    return;
                }

                // A file whose data is in the cloud is never fetched by a search (2026-10-22, review O1); the result counts it.
                if (IsOffline(entry.FullPath, entry.Attributes))
                {
                    Interlocked.Increment(ref cloudOnly);
                    unsearched ??= singleFile ? FileText.SearchFileCloudOnly(display) : null;
                    return;
                }

                // A share's budget (2026-09-30): the bytes read over the network, counted before the read.
                if (Interlocked.Add(ref bytesRead, entry.Length) > _options.SearchByteBudget)
                {
                    budgeted = true;
                    state.Stop();
                    return;
                }

                // Read no further than the cap whatever the walk saw (2026-10-22, review N10: a log grown since was read whole, gigabytes on a share).
                byte[]? bytes;
                try
                {
                    bytes = RegularFiles.ReadAtMost(entry.FullPath, MaxTextFileBytes);
                }
                catch (Exception ex) when (IsFileFailure(ex))
                {
                    unsearched ??= singleFile ? FileText.SearchFileUnreadable(display, ex.Message) : null;
                    return;
                }

                if (bytes is null)
                {
                    unsearched ??= singleFile ? FileText.SearchFileTooBig(display) : null;
                    return;
                }

                if (LooksBinary(bytes))
                {
                    unsearched ??= singleFile ? FileText.SearchFileNotText(display) : null;
                    return;
                }

                Interlocked.Increment(ref searched);
                string content = Decode(bytes, out _);
                string relativePath = Relative(entry.FullPath);
                int line = 0, inFile = 0, fileTimeouts = 0;
                List<string>? all = null;
                List<SearchHit>? found = null;
                foreach (var span in content.AsSpan().EnumerateLines())
                {
                    line++;
                    // Esc inside a long file (2026-10-22, review G5: the token was read only between files).
                    if ((line & 1023) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    bool hit;
                    if (expression is null)
                    {
                        hit = span.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                    else
                    {
                        try
                        {
                            hit = expression.IsMatch(span);
                        }
                        catch (RegexMatchTimeoutException)
                        {
                            Interlocked.Increment(ref timedOut);
                            // A pattern that times out over and over gives the file up (review G5: (a+)+$ over a big log ran for hours).
                            if (++fileTimeouts >= MaxTimeoutsPerFile)
                            {
                                break;
                            }

                            continue;
                        }
                    }

                    if (!hit)
                    {
                        continue;
                    }

                    inFile++;
                    if (countOnly)
                    {
                        continue;
                    }

                    if (inFile > limit)
                    {
                        break;
                    }

                    found ??= [];
                    if (context == 0)
                    {
                        found.Add(new SearchHit(relativePath, line, ClipLine(span)));
                        continue;
                    }

                    // The file split into lines only at its first hit (2026-10-22, review W5: every file was split before knowing it had one).
                    all ??= SplitLines(content);
                    var before = new List<string>(context);
                    for (int i = Math.Max(0, line - 1 - context); i < line - 1 && i < all.Count; i++)
                    {
                        before.Add(ClipLine(all[i]));
                    }

                    var after = new List<string>(context);
                    for (int i = line; i < Math.Min(all.Count, line + context); i++)
                    {
                        after.Add(ClipLine(all[i]));
                    }

                    found.Add(new SearchHit(relativePath, line, ClipLine(span), before, after));
                }

                if (inFile > 0)
                {
                    fileHits[index] = found;
                    fileCounts[index] = inFile;
                    Interlocked.Increment(ref matchedFiles);
                    if ((countOnly ? Volatile.Read(ref matchedFiles) : Interlocked.Add(ref matches, Math.Min(inFile, limit))) > limit)
                    {
                        state.Stop();
                    }
                }
            });

            // The files done are a prefix of the ordered list (each started file was finished); their hits in order, the first N kept.
            bool stopped = false;
            if (countOnly)
            {
                var files = new List<SearchFileCount>();
                for (int i = 0; i < entries.Count && files.Count <= limit; i++)
                {
                    if (fileCounts[i] > 0)
                    {
                        files.Add(new SearchFileCount(Relative(entries[i].FullPath), fileCounts[i]));
                    }
                }

                stopped = files.Count > limit;
                if (stopped)
                {
                    files.RemoveRange(limit, files.Count - limit);
                }

                return new SearchResult(FileOutcome.Ok, display, [], searched, Math.Min(matchedFiles, limit), timedOut, stopped || budgeted, _time.GetElapsedTime(started), SingleFile: singleFile, Files: files)
                {
                    Budgeted = budgeted, CloudOnly = cloudOnly, Hidden = hidden.Count, Unsearched = unsearched,
                };
            }

            var ordered_hits = new List<SearchHit>(Math.Min(limit + 1, 1024));
            for (int i = 0; i < entries.Count && ordered_hits.Count <= limit; i++)
            {
                if (fileHits[i] is { } list)
                {
                    ordered_hits.AddRange(list);
                }
            }

            stopped = ordered_hits.Count > limit || fileCounts.Any(c => c > limit);
            if (ordered_hits.Count > limit)
            {
                ordered_hits.RemoveRange(limit, ordered_hits.Count - limit);
            }

            return new SearchResult(FileOutcome.Ok, display, ordered_hits, searched, matchedFiles, timedOut, stopped || budgeted, _time.GetElapsedTime(started), SingleFile: singleFile)
            {
                Budgeted = budgeted, CloudOnly = cloudOnly, Hidden = hidden.Count, Unsearched = unsearched,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return Empty(FileOutcome.Failed, display, ex.Message);
        }

        static SearchResult Empty(FileOutcome outcome, string relative, string detail = "") =>
            new(outcome, relative, [], 0, 0, 0, false, TimeSpan.Zero, detail);
    }

    /// <summary>How many regex timeouts one file may take before the search gives it up (2026-10-22, review G5).</summary>
    public const int MaxTimeoutsPerFile = 3;

    /// <summary>Whether <paramref name="full"/> matches a <c>files</c> pattern as a walk would judge it (review V10).</summary>
    private static bool MatchesName(string pattern, string full, string directory, string root) =>
        PathGlob.ExpandBraces(pattern).Any(alternative => PathGlob.IsPathPattern(alternative)
            ? PathGlob.IsMatch(alternative, Path.GetRelativePath(directory, full)) || PathGlob.IsMatch(alternative, Path.GetRelativePath(root, full))
            : FileSystemName.MatchesSimpleExpression(alternative, Path.GetFileName(full), ignoreCase: true));

    /// <summary>The <paramref name="count"/> most recently written files under the path (down to <paramref name="maxDepth"/> levels), newest first: one pass, a bounded top-N, at most <see cref="MaxResultLimit"/>.</summary>
    public RecentResult Recent(string relative, int count, int maxDepth = int.MaxValue) => Recent(relative, count, CancellationToken.None, maxDepth);

    /// <summary>
    /// <see cref="Recent(string, int, int)"/> under the call's token, with a <c>files</c> pattern (2026-10-22, review M9: order modified dropped it
    /// and listed every file), the newest kept in a heap (review W6: a sorted insert per entry was entries × limit) and the walk budgeted (G18).
    /// </summary>
    public RecentResult Recent(string relative, int count, CancellationToken cancellationToken, int maxDepth = int.MaxValue, string? namePattern = null)
    {
        var outcome = Resolve(relative, forWrite: false, out string full);
        if (outcome != FileOutcome.Ok)
        {
            return new RecentResult(outcome, relative, []);
        }

        string display = Relative(full, isDirectory: true);
        int limit = Math.Clamp(count, 1, MaxResultLimit);
        try
        {
            EnsureExists();
            if (!Directory.Exists(full))
            {
                return new RecentResult(File.Exists(full) ? FileOutcome.IsAFile : FileOutcome.Missing, display, []);
            }

            var heap = new PriorityQueue<WalkEntry, DateTime>(limit + 1);
            bool budgeted = false;
            var hidden = new HiddenCount { Max = _options.MaxWalkEntries, Token = cancellationToken };
            try
            {
                foreach (var entry in Walk(full, string.IsNullOrWhiteSpace(namePattern) ? "*" : namePattern.Trim(), recurse: true, hidden, maxDepth: maxDepth))
                {
                    if (heap.Count < limit)
                    {
                        heap.Enqueue(entry, entry.LastWriteTimeUtc);
                    }
                    else if (heap.TryPeek(out _, out var oldest) && entry.LastWriteTimeUtc > oldest)
                    {
                        heap.EnqueueDequeue(entry, entry.LastWriteTimeUtc);
                    }
                }
            }
            catch (WalkBudgetSpent)
            {
                budgeted = true;
            }

            var top = new List<WalkEntry>(heap.Count);
            while (heap.TryDequeue(out var entry, out _))
            {
                top.Add(entry);
            }

            top.Reverse();
            var entries = top.Select(e => new RecentEntry(Relative(e.FullPath), Local(e.LastWriteTimeUtc), e.Length)).ToList();
            return new RecentResult(FileOutcome.Ok, display, entries) { Budgeted = budgeted, Hidden = hidden.Count };
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return new RecentResult(FileOutcome.Failed, display, [], ex.Message);
        }
    }

    public InfoResult Info(string relative)
    {
        var outcome = Resolve(relative, forWrite: false, out string full);
        if (outcome != FileOutcome.Ok)
        {
            return Missing(outcome, relative);
        }

        try
        {
            EnsureExists();
            if (File.Exists(full))
            {
                var info = new FileInfo(full);
                int? lines = null, words = null;
                string ending = "";
                bool bom = false;
                // A cloud-only file's lines aren't counted (2026-10-22, review O1): a count is no reason to download it.
                if (info.Length <= MaxTextFileBytes && !IsOffline(full, info.Attributes))
                {
                    byte[] content = RegularFiles.ReadAllBytes(full);
                    if (!LooksBinary(content))
                    {
                        string text = Decode(content, out bom);
                        Count(text, out int l, out int w);
                        lines = l;
                        words = w;
                        ending = DescribeLineEnding(text);
                    }
                }

                return new InfoResult(FileOutcome.Ok, Relative(full), false, info.Length, Local(info.LastWriteTimeUtc), lines, words, 0, 0, false, ending, bom);
            }

            if (!Directory.Exists(full))
            {
                return Missing(FileOutcome.Missing, Relative(full));
            }

            int files = 0, folders = 0, seen = 0;
            long bytes = 0;
            DateTime newest = Directory.GetLastWriteTimeUtc(full);
            bool truncated = false;
            foreach (var entry in Walk(full, null, recurse: true, includeDirectories: true))
            {
                if (++seen > MaxInfoEntries)
                {
                    truncated = true;
                    break;
                }

                if (entry.IsDirectory)
                {
                    folders++;
                    continue;
                }

                files++;
                bytes += entry.Length;
                if (entry.LastWriteTimeUtc > newest)
                {
                    newest = entry.LastWriteTimeUtc;
                }
            }

            return new InfoResult(FileOutcome.Ok, Relative(full, isDirectory: true), true, bytes, Local(newest), null, null, files, folders, truncated);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return Missing(FileOutcome.Failed, Relative(full), ex.Message);
        }

        static InfoResult Missing(FileOutcome outcome, string relative, string detail = "") =>
            new(outcome, relative, false, 0, default, null, null, 0, 0, false, Detail: detail);
    }

    /// <summary>
    /// <paramref name="relative"/> as the model has now seen it (2026-10-10, the user's call after a run's <c>write_file</c> was refused
    /// twice for files the model had just read or written, each retry a whole request): <c>read_file</c> (a whole read only), <c>write_file</c>
    /// (create or overwrite, never an append: 2026-10-11, the review) and <c>patch_file</c> mark the file after they succeed, with its size
    /// and write time. Nothing for a file that is not there, nor under <see cref="SeenOff"/>.
    /// </summary>
    public void MarkSeen(string relative) => MarkSeen(relative, null);

    /// <summary>
    /// <see cref="MarkSeen(string)"/> with the stamp the read itself took (2026-10-22, review J12: the stamp was taken after the read, so a
    /// change between the two counted as seen and a modeless write then replaced it): <see cref="ReadResult.Seen"/>; null takes one now.
    /// </summary>
    public void MarkSeen(string relative, SeenStamp? taken)
    {
        if (s_seenOff.Value)
        {
            return;
        }

        if (Resolve(relative, forWrite: false, out string full) == FileOutcome.Ok && (taken ?? StampOf(full)) is { } stamp)
        {
            lock (_seenGate)
            {
                _seen[full] = stamp;
            }
        }
    }

    /// <summary>
    /// Whether the model saw <paramref name="relative"/> this session (<see cref="MarkSeen"/>) and it is unchanged since: the same size and
    /// write time. A file changed since — by the user, a build, another tool — is not, so <c>write_file</c> still refuses it.
    /// </summary>
    public bool WasSeen(string relative)
    {
        if (s_seenOff.Value || Resolve(relative, forWrite: false, out string full) != FileOutcome.Ok)
        {
            return false;
        }

        SeenStamp? seen;
        lock (_seenGate)
        {
            seen = _seen.GetValueOrDefault(full);
        }

        return seen is not null && StampOf(full) is { } now && now == seen;
    }

    /// <summary>
    /// <paramref name="relative"/> as <c>read_file</c> returned text of it, a window or the whole (2026-10-18, the code audit: <see cref="WasSeen"/>
    /// marks whole reads alone, <c>write_file</c>'s rule, so a <c>start_line</c>/<c>max_lines</c> window would never count). The same stamp and
    /// <see cref="SeenOff"/> rule as <see cref="MarkSeen"/>: an advisor's read marks nothing.
    /// </summary>
    public void MarkViewed(string relative) => MarkViewed(relative, null);

    /// <summary><see cref="MarkViewed(string)"/> with the stamp the read took (<see cref="MarkSeen(string, SeenStamp?)"/>'s rule).</summary>
    public void MarkViewed(string relative, SeenStamp? taken)
    {
        if (s_seenOff.Value)
        {
            return;
        }

        if (Resolve(relative, forWrite: false, out string full) == FileOutcome.Ok && (taken ?? StampOf(full)) is { } stamp)
        {
            lock (_seenGate)
            {
                _viewed[full] = stamp;
            }
        }
    }

    /// <summary>Whether <c>read_file</c> returned text of <paramref name="relative"/> this session (<see cref="MarkViewed"/>) and it is unchanged since.</summary>
    public bool WasViewed(string relative)
    {
        if (s_seenOff.Value || Resolve(relative, forWrite: false, out string full) != FileOutcome.Ok)
        {
            return false;
        }

        SeenStamp? viewed;
        lock (_seenGate)
        {
            viewed = _viewed.GetValueOrDefault(full);
        }

        return viewed is not null && StampOf(full) is { } now && now == viewed;
    }

    /// <summary>Every file forgotten as seen and as viewed: the session is (<c>/new</c>, <c>/clear</c>, a profile switch).</summary>
    public void ForgetSeen()
    {
        lock (_seenGate)
        {
            _seen.Clear();
            _viewed.Clear();
        }
    }

    /// <summary>The most entries <see cref="ListFiles"/> walks (2026-10-18), files and folders alike.</summary>
    public const int MaxListEntries = 100_000;

    /// <summary>The longest <see cref="ListFiles"/> walks before it stops and says it was cut.</summary>
    public static readonly TimeSpan ListFilesBudget = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Every file under <paramref name="relative"/> (2026-10-18, the code audit's scope), sandbox-relative, by the rules <see cref="Search"/> walks
    /// by — never into a reparse point (so a link out of the sandbox is never followed), the hidden rule off Windows — with a folder
    /// <paramref name="skip"/> says to (its name) left unentered; at most <paramref name="limit"/> files. The walk stops at
    /// <see cref="MaxListEntries"/> entries or <see cref="ListFilesBudget"/> and says it was cut; <paramref name="cancellationToken"/> throws.
    /// A file named as the path is listed alone. <paramref name="skipFolder"/> (2026-10-20, a review: a repository's untracked folder was walked
    /// into its ignored <c>node_modules</c>, whose files spent the budget before they were dropped) leaves a folder unentered by its full path too;
    /// <paramref name="withStamps"/> keeps each file's size and time (<see cref="FileListResult.Stamps"/>).
    /// </summary>
    public FileListResult ListFiles(string relative, int limit, Func<string, bool>? skip, CancellationToken cancellationToken, Func<string, bool>? skipFolder = null, bool withStamps = false)
    {
        var outcome = Resolve(relative, forWrite: false, out string full);
        if (outcome != FileOutcome.Ok)
        {
            return new FileListResult(outcome, relative, [], false);
        }

        try
        {
            EnsureExists();
            if (File.Exists(full))
            {
                var info = new FileInfo(full);
                return new FileListResult(FileOutcome.Ok, Relative(full), [Relative(full)], false, Stamps: withStamps ? [(info.Length, info.LastWriteTimeUtc)] : null);
            }

            if (!Directory.Exists(full))
            {
                return new FileListResult(FileOutcome.Missing, Relative(full, isDirectory: true), [], false);
            }

            long started = _time.GetTimestamp();
            var files = new List<string>();
            var stamps = withStamps ? new List<(long Length, DateTime Written)>() : null;
            int entries = 0;
            bool cut = false;
            var walk = new FileSystemEnumerable<WalkEntry>(
                full,
                (ref FileSystemEntry entry) => new WalkEntry(entry.ToFullPath(), entry.IsDirectory ? 0 : entry.Length, entry.LastWriteTimeUtc.UtcDateTime, entry.IsDirectory, entry.Attributes),
                WalkOptions(recurse: true))
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) => !IsHiddenOffWindows(entry.Attributes, entry.FileName) && !IsLink(ref entry) && !IsTempSibling(entry.FileName),
                ShouldRecursePredicate = (ref FileSystemEntry entry) =>
                    !IsHiddenOffWindows(entry.Attributes, entry.FileName) && !IsLink(ref entry) && !(skip?.Invoke(entry.FileName.ToString()) ?? false)
                    && !(skipFolder?.Invoke(entry.ToFullPath()) ?? false),
            };
            foreach (var entry in walk)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++entries > MaxListEntries || _time.GetElapsedTime(started) > ListFilesBudget)
                {
                    cut = true;
                    break;
                }

                if (entry.IsDirectory)
                {
                    continue;
                }

                if (files.Count >= limit)
                {
                    cut = true;
                    break;
                }

                files.Add(Relative(entry.FullPath));
                stamps?.Add((entry.Length, entry.LastWriteTimeUtc));
            }

            return new FileListResult(FileOutcome.Ok, Relative(full, isDirectory: true), files, cut, Stamps: stamps);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return new FileListResult(FileOutcome.Failed, Relative(full), [], false, ex.Message);
        }
    }

    /// <summary>
    /// Seen files off for the rest of the calling flow until disposed (2026-10-11, the review): an advisor's turn and a bot's reply run under
    /// it, so their reads mark nothing and their <c>write_file</c> without a mode finds nothing marked, as before 2026-10-10. Flows
    /// through awaits and into tasks started inside, never out to the caller.
    /// </summary>
    public static IDisposable SeenOff()
    {
        bool was = s_seenOff.Value;
        s_seenOff.Value = true;
        return new SeenRestore(was);
    }

    private sealed class SeenRestore(bool was) : IDisposable
    {
        public void Dispose() => s_seenOff.Value = was;
    }

    /// <summary>
    /// A file's identity for a cache without reading it (2026-10-14, <c>read_pdf</c>'s page texts): its full path, length and last write,
    /// or null when it is outside the sandbox, missing or unreadable. One metadata call, on a UNC share too.
    /// </summary>
    public (string Full, long Length, DateTime Written)? TryStamp(string relative)
    {
        if (Resolve(relative, forWrite: false, out string full) != FileOutcome.Ok)
        {
            return null;
        }

        try
        {
            var info = new FileInfo(full);
            return info.Exists ? (info.FullName, info.Length, info.LastWriteTimeUtc) : null;
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// The file at <paramref name="full"/> as it stands: its size, write time and content hash (2026-10-22, review Q11: on a file system whose
    /// times step by a second or two — FAT, HFS+, a NAS — a same-size change in the same tick passed the size-and-time check). Null for a file
    /// that is gone, unreadable or past <see cref="MaxReadFileBytes"/>, which no whole read can have seen.
    /// </summary>
    private static SeenStamp? StampOf(string full)
    {
        try
        {
            var info = new FileInfo(full);
            if (!info.Exists || info.Length > MaxReadFileBytes)
            {
                return null;
            }

            return SeenStamp.Of(info.Length, info.LastWriteTimeUtc, RegularFiles.ReadAllBytes(full));
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// A text file, or a window of it: <paramref name="startLine"/> is 1-based, a negative one
    /// counts from the end (<c>-20</c> = the last 20 lines); <paramref name="maxLines"/> null = all
    /// that fit in <paramref name="maxChars"/>. <paramref name="maxChars"/> is the window, a line longer than it cut
    /// (2026-10-17, the user's report: a 40,000-character plan "could not be read"): the model's tools keep
    /// <see cref="MaxReadChars"/>, and the app reading back a file it wrote passes <see cref="MaxWriteChars"/>.
    /// </summary>
    public ReadResult ReadText(string relative, int? startLine, int? maxLines, int maxChars = MaxReadChars)
    {
        var outcome = Resolve(relative, forWrite: false, out string full);
        if (outcome != FileOutcome.Ok)
        {
            return Fail(outcome, relative);
        }

        string display = Relative(full);
        try
        {
            EnsureExists();
            if (Directory.Exists(full))
            {
                return Fail(FileOutcome.IsDirectory, Relative(full, isDirectory: true));
            }

            if (!File.Exists(full))
            {
                return Fail(FileOutcome.Missing, display);
            }

            // The size and time before the bytes (2026-10-22, review J12): a change between them never counts as seen.
            var before = new FileInfo(full);
            if (before.Length > MaxReadFileBytes)
            {
                return Fail(FileOutcome.TooBig, display);
            }

            long stampLength = before.Length;
            DateTime stampWritten = before.LastWriteTimeUtc;
            byte[] bytes = RegularFiles.ReadAllBytes(full);
            if (LooksBinary(bytes))
            {
                return Fail(FileOutcome.NotText, display);
            }

            var seen = SeenStamp.Of(stampLength, stampWritten, bytes);
            var lines = SplitLines(TextFile.Decode(bytes, out var codec));
            int total = lines.Count;
            int from = startLine switch
            {
                null or 0 => 1,
                < 0 => Math.Max(1, total + startLine.Value + 1),
                _ => startLine.Value,
            };
            if (from > total)
            {
                return new ReadResult(FileOutcome.Ok, display, "", total, total == 0 ? 0 : from, total == 0 ? 0 : from - 1, false) { Encoding = codec.Label, Seen = seen };
            }

            // In long (2026-10-22, review G20: max_lines int.MaxValue made from + want - 1 wrap, and the window came back empty).
            long want = maxLines is > 0 ? maxLines.Value : total - from + 1;
            int to = (int)Math.Min(total, from + want - 1);
            var sb = new StringBuilder();
            bool truncated = false;
            bool lineCut = false;
            int cutLine = 0, cutLength = 0;
            int shown = from - 1;
            for (int i = from - 1; i < to; i++)
            {
                if (sb.Length + lines[i].Length + 1 > maxChars && sb.Length > 0)
                {
                    truncated = true;
                    break;
                }

                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }

                // A cut never splits a surrogate pair (review G20), and the header names the cut line (review Q2).
                if (lines[i].Length > maxChars && cutLine == 0)
                {
                    cutLine = i + 1;
                    cutLength = lines[i].Length;
                }

                lineCut |= lines[i].Length > maxChars;
                sb.Append(TextCut.Safe(lines[i], maxChars));
                shown = i + 1;
            }

            return new ReadResult(FileOutcome.Ok, display, sb.ToString(), total, from, shown, truncated, LineCut: lineCut)
            {
                Encoding = codec.Label,
                Seen = seen,
                CutLine = cutLine,
                CutLength = cutLength,
            };
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return Fail(FileOutcome.Failed, display, ex.Message);
        }

        static ReadResult Fail(FileOutcome outcome, string relative, string detail = "") =>
            new(outcome, relative, "", 0, 0, 0, false, detail);
    }

    /// <summary>
    /// A picture as the model gets it (<see cref="ImageFile.TryLoad"/>: the caps, the downscale),
    /// its path the relative one so the sentences never show the full path. No extension gate — the
    /// bytes decide, as for a dropped file. A file the
    /// codecs refuse is <see cref="FileOutcome.NotAnImage"/>, one over the caps <see cref="FileOutcome.ImageTooBig"/>.
    /// </summary>
    public ImageResult ReadImage(string relative)
    {
        var outcome = Resolve(relative, forWrite: false, out string full);
        if (outcome != FileOutcome.Ok)
        {
            return new ImageResult(outcome, relative, null);
        }

        string display = Relative(full);
        try
        {
            EnsureExists();
            if (Directory.Exists(full))
            {
                return new ImageResult(FileOutcome.IsDirectory, Relative(full, isDirectory: true), null);
            }

            if (!File.Exists(full))
            {
                return new ImageResult(FileOutcome.Missing, display, null);
            }

            if (ImageFile.TryLoad(full, out var image, out var failure, out string detail))
            {
                return new ImageResult(FileOutcome.Ok, display, image! with { Path = display });
            }

            // A read that failed, or no codecs here, is said as one (2026-10-22, review K11), never as a broken picture.
            return failure switch
            {
                ImageLoadFailure.NotFound => new ImageResult(FileOutcome.Missing, display, null),
                ImageLoadFailure.TooLarge => new ImageResult(FileOutcome.ImageTooBig, display, null),
                ImageLoadFailure.Unreadable => new ImageResult(FileOutcome.Failed, display, null, detail),
                ImageLoadFailure.Unsupported => new ImageResult(FileOutcome.Failed, display, null, FileText.NoPictureCodecs),
                _ => new ImageResult(FileOutcome.NotAnImage, display, null),
            };
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return new ImageResult(FileOutcome.Failed, display, null, ex.Message);
        }
    }

    /// <summary>
    /// A file's bytes as they are (2026-10-04, <c>image_edit</c>: <see cref="ReadImage"/> downscales for the model, an edit needs the
    /// original): the same guards as <see cref="ReadText"/> — the sandbox, a folder, nothing there — and over
    /// <paramref name="maxBytes"/> <see cref="FileOutcome.TooBig"/>, unread. No content check: the caller's decoder decides.
    /// </summary>
    public BytesResult ReadBytes(string relative, long maxBytes)
    {
        var outcome = Resolve(relative, forWrite: false, out string full);
        if (outcome != FileOutcome.Ok)
        {
            return new BytesResult(outcome, relative, null);
        }

        string display = Relative(full);
        try
        {
            EnsureExists();
            if (Directory.Exists(full))
            {
                return new BytesResult(FileOutcome.IsDirectory, Relative(full, isDirectory: true), null);
            }

            if (!File.Exists(full))
            {
                return new BytesResult(FileOutcome.Missing, display, null);
            }

            if (new FileInfo(full).Length > maxBytes)
            {
                return new BytesResult(FileOutcome.TooBig, display, null);
            }

            return new BytesResult(FileOutcome.Ok, display, RegularFiles.ReadAllBytes(full));
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return new BytesResult(FileOutcome.Failed, display, null, ex.Message);
        }
    }

    // ---- write side ----

    public WriteResult WriteText(string relative, string text, bool overwrite)
    {
        ArgumentNullException.ThrowIfNull(text);
        var outcome = Resolve(relative, forWrite: true, out string full);
        if (outcome != FileOutcome.Ok)
        {
            return new WriteResult(outcome, relative, 0, false);
        }

        string display = Relative(full);
        if (text.Length > MaxWriteChars)
        {
            return new WriteResult(FileOutcome.TooLong, display, 0, false);
        }

        try
        {
            EnsureExists();
            if (Directory.Exists(full))
            {
                return new WriteResult(FileOutcome.IsDirectory, Relative(full, isDirectory: true), 0, false);
            }

            bool existed = File.Exists(full);
            if (existed && !overwrite)
            {
                return new WriteResult(FileOutcome.Exists, display, 0, false);
            }

            // The text it replaces, for the transcript's diff (2026-10-03): only a file that reads as text within the cap. Its encoding, mark and
            // line ending go onto the new text (2026-10-22, review Q3: an overwrite wrote BOM-less UTF-8 with LF, which PowerShell 5.1 then
            // misread and a .bat's labels broke); a new file is plain UTF-8 as given.
            TextCodec? codec = null;
            string ending = "\n";
            string? before = existed ? PreviousText(full, out codec, out ending) : null;
            byte[]? encoded = before is null || codec is null ? Utf8NoBom.GetBytes(text) : codec.Encode(WithEnding(NormalizeNewlines(text), ending));
            if (encoded is null)
            {
                return new WriteResult(FileOutcome.NotEncodable, display, 0, false, Detail: codec!.Label ?? "UTF-8");
            }

            RecheckLinks(full);
            // A new file made only if it is still not there (2026-10-22, review J3: Exists then a move with overwrite clobbered one made between).
            if (!existed && !WriteNew(full, encoded))
            {
                return new WriteResult(FileOutcome.Exists, display, 0, false);
            }

            long bytes = existed ? WriteAtomically(full, encoded, _options.PreserveOnReplace) : encoded.LongLength;
            Count(text, out int lines, out int words);
            var diff = !existed || before is not null ? FileDiff.Of(display, before, text) : null;
            return new WriteResult(FileOutcome.Ok, display, bytes, existed, Lines: lines, Words: words) { Diff = diff };
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return new WriteResult(FileOutcome.Failed, display, 0, false, Detail: ex.Message);
        }
    }

    /// <summary>
    /// <see cref="WriteText"/> for bytes as they are (a download, 2026-09-18): the same guards —
    /// the sandbox, a folder in the way, a file there unless <paramref name="overwrite"/> — and the
    /// same atomic write; no length cap (the caller's is the download's).
    /// </summary>
    public WriteResult WriteBytes(string relative, byte[] bytes, bool overwrite)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var outcome = Resolve(relative, forWrite: true, out string full);
        if (outcome != FileOutcome.Ok)
        {
            return new WriteResult(outcome, relative, 0, false);
        }

        string display = Relative(full);
        try
        {
            EnsureExists();
            if (Directory.Exists(full))
            {
                return new WriteResult(FileOutcome.IsDirectory, Relative(full, isDirectory: true), 0, false);
            }

            bool existed = File.Exists(full);
            if (existed && !overwrite)
            {
                return new WriteResult(FileOutcome.Exists, display, 0, false);
            }

            RecheckLinks(full);
            if (!existed && !WriteNew(full, bytes))
            {
                return new WriteResult(FileOutcome.Exists, display, 0, false);
            }

            long written = existed ? WriteAtomically(full, bytes, _options.PreserveOnReplace) : bytes.LongLength;
            return new WriteResult(FileOutcome.Ok, display, written, existed);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return new WriteResult(FileOutcome.Failed, display, 0, false, Detail: ex.Message);
        }
    }

    /// <summary>
    /// <see cref="WriteBytes"/> a piece at a time (2026-10-01, <c>download_file</c> streamed to disk): the same guards — the
    /// sandbox, a folder in the way, a file there unless <paramref name="overwrite"/> — checked now, the missing folders made,
    /// and <paramref name="pending"/> the write to fill and commit (<see cref="PendingWrite"/>); null with the refusal as the
    /// result otherwise. The result of a write begun is <see cref="FileOutcome.Ok"/> with no bytes yet.
    /// </summary>
    public WriteResult BeginWrite(string relative, bool overwrite, out PendingWrite? pending)
    {
        pending = null;
        var outcome = Resolve(relative, forWrite: true, out string full);
        if (outcome != FileOutcome.Ok)
        {
            return new WriteResult(outcome, relative, 0, false);
        }

        string display = Relative(full);
        try
        {
            EnsureExists();
            if (Directory.Exists(full))
            {
                return new WriteResult(FileOutcome.IsDirectory, Relative(full, isDirectory: true), 0, false);
            }

            bool existed = File.Exists(full);
            if (existed && !overwrite)
            {
                return new WriteResult(FileOutcome.Exists, display, 0, false);
            }

            RecheckLinks(full);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            pending = new PendingWrite(full, display, overwrite, _options.PreserveOnReplace);
            return new WriteResult(FileOutcome.Ok, display, 0, existed);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return new WriteResult(FileOutcome.Failed, display, 0, false, Detail: ex.Message);
        }
    }

    /// <summary>
    /// Whether <paramref name="relative"/> names a folder that exists under the root (the root itself
    /// included); false for a file, nothing, or a path outside. A download's <c>path</c> that is a
    /// folder takes the file's own name inside it (2026-09-18).
    /// </summary>
    public bool IsExistingDirectory(string relative)
    {
        if (Resolve(relative, forWrite: false, out string full) != FileOutcome.Ok)
        {
            return false;
        }

        try
        {
            return Directory.Exists(full);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return false;
        }
    }

    /// <summary>Adds to the end (a newline first when the file does not end with one); creates a missing file.</summary>
    public WriteResult AppendText(string relative, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var outcome = Resolve(relative, forWrite: true, out string full);
        if (outcome != FileOutcome.Ok)
        {
            return new WriteResult(outcome, relative, 0, false);
        }

        string display = Relative(full);
        if (text.Length > MaxWriteChars)
        {
            return new WriteResult(FileOutcome.TooLong, display, 0, false);
        }

        try
        {
            EnsureExists();
            if (Directory.Exists(full))
            {
                return new WriteResult(FileOutcome.IsDirectory, Relative(full, isDirectory: true), 0, false);
            }

            // Read, added to and written back whole through a temp file (2026-10-22, review G12): it wrote in place — no binary or encoding
            // check, a bare '\n' and UTF-8 bytes onto a UTF-16 or CRLF file, through a hard link, and a fragment left by a failure partway
            // that a retry then doubled. Now the file's own encoding and main line ending, all or nothing, and no total past
            // MaxReadFileBytes (review P10: what read_file can still open), the counts from the text in hand (no second read).
            bool existed = File.Exists(full);
            SeenStamp? seen = null;
            string old = "";
            var codec = TextCodec.Utf8;
            string ending = "\n";
            long before = 0;
            if (existed)
            {
                before = new FileInfo(full).Length;
                if (before > MaxReadFileBytes)
                {
                    return new WriteResult(FileOutcome.TooBig, display, 0, false);
                }

                byte[] current = RegularFiles.ReadAllBytes(full);
                if (LooksBinary(current))
                {
                    return new WriteResult(FileOutcome.NotText, display, 0, false);
                }

                old = TextFile.Decode(current, out codec);
                ending = DetectLineEnding(old);
                before = current.LongLength;
                seen = SeenStamp.Of(current.LongLength, File.GetLastWriteTimeUtc(full), current);
            }

            string added = WithEnding(NormalizeNewlines(text), ending);
            string separator = old.Length > 0 && !old.EndsWith('\n') && !old.EndsWith('\r') ? ending : "";
            string whole = old + separator + added;
            if (codec.Encode(whole) is not { } encoded)
            {
                return new WriteResult(FileOutcome.NotEncodable, display, 0, false, Detail: codec.Label ?? "UTF-8");
            }

            if (encoded.LongLength > MaxReadFileBytes)
            {
                return new WriteResult(FileOutcome.TooBig, display, 0, false);
            }

            RecheckLinks(full);
            if (existed && StampOf(full) != seen)
            {
                return new WriteResult(FileOutcome.Failed, display, 0, false, Detail: FileText.ChangedWhileEditing);
            }

            if (existed)
            {
                WriteAtomically(full, encoded, _options.PreserveOnReplace);
            }
            else if (!WriteNew(full, encoded))
            {
                return new WriteResult(FileOutcome.Exists, display, 0, false);
            }

            long written = encoded.LongLength - before;
            Count(whole, out int total, out int words);
            int? lines = total;

            // The added lines numbered from where they landed (2026-10-03): the file's lines less the appended text's own.
            Count(text, out int addedLines, out _);
            var diff = FileDiff.Appended(display, Math.Max(0, total - addedLines), text, existed);

            return new WriteResult(FileOutcome.Ok, display, written, existed, Lines: lines, Words: words) { Diff = diff };
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return new WriteResult(FileOutcome.Failed, display, 0, false, Detail: ex.Message);
        }
    }

    /// <summary>
    /// The <see cref="EditResult"/> a match that replaced nothing answers (<see cref="FileText.Edited"/> words it), or null when it
    /// replaced: <see cref="EditText"/>'s, and <c>skill_editor</c>'s edit of a SKILL.md body, which is no file of a working directory
    /// (2026-10-17, <see cref="Skills.SkillEditor.EditBody"/>).
    /// </summary>
    public static EditResult? EditRefusal(FuzzyResult match, string display)
    {
        ArgumentNullException.ThrowIfNull(match);
        return match.Outcome switch
        {
            MatchOutcome.Empty => new EditResult(FileOutcome.Empty, display, 0, 0),
            MatchOutcome.Same => new EditResult(FileOutcome.Same, display, 0, 0),
            MatchOutcome.NotFound => new EditResult(FileOutcome.EditNotFound, display, 0, 0),
            MatchOutcome.AlreadyApplied => new EditResult(FileOutcome.AlreadyApplied, display, 0, 0),
            MatchOutcome.Ambiguous => new EditResult(FileOutcome.EditAmbiguous, display, 0, match.Count, Strategy: match.Strategy, Locations: match.Locations),
            MatchOutcome.ApproximateAll => new EditResult(FileOutcome.ApproximateAll, display, 0, match.Count, Strategy: match.Strategy, Locations: match.Locations),
            MatchOutcome.EscapeDrift => new EditResult(FileOutcome.EscapeDrift, display, 0, match.Count, Strategy: match.Strategy, Drift: match.Drift),
            MatchOutcome.TooLarge => new EditResult(FileOutcome.Failed, display, 0, match.Count, Detail: FileText.EditResultTooBig(match.Count)),
            _ => null,
        };
    }

    /// <summary>
    /// Replaces one occurrence of <paramref name="oldText"/> — exactly one unless
    /// <paramref name="replaceAll"/>, then every one — found by <see cref="FuzzyMatch.Replace"/>'s
    /// chain (2026-09-19: as written first, then with spacing, indentation, escapes and typographic
    /// characters tolerated, last by similarity; the result names the strategy). The match runs over the
    /// text with its line breaks normalised to <c>\n</c> (the form <see cref="ReadText"/> shows, so a
    /// multi-line <c>old_text</c> copied from a read matches a CRLF file too, 2026-09-17), and the file is
    /// written back with the line ending it had (<see cref="DetectLineEnding"/>: the first break
    /// decides, so a mixed file comes out uniform); a BOM is kept. An edit already in the file is
    /// <see cref="FileOutcome.AlreadyApplied"/>, nothing written.
    /// </summary>
    public EditResult EditText(string relative, string oldText, string newText, bool replaceAll = false)
    {
        ArgumentNullException.ThrowIfNull(oldText);
        ArgumentNullException.ThrowIfNull(newText);
        var outcome = Resolve(relative, forWrite: true, out string full);
        if (outcome != FileOutcome.Ok)
        {
            return new EditResult(outcome, relative, 0, 0);
        }

        string display = Relative(full);
        if (oldText.Trim().Length == 0)
        {
            return new EditResult(FileOutcome.Empty, display, 0, 0);
        }

        if (newText.Length > MaxWriteChars)
        {
            return new EditResult(FileOutcome.TooLong, display, 0, 0);
        }

        string needle = NormalizeNewlines(oldText);
        string replacement = NormalizeNewlines(newText);
        if (string.Equals(needle, replacement, StringComparison.Ordinal))
        {
            return new EditResult(FileOutcome.Same, display, 0, 0);
        }

        try
        {
            var loaded = LoadForEdit(full, display, out string content, out var codec, out string ending, out var before);
            if (loaded is not null)
            {
                return loaded;
            }

            var match = FuzzyMatch.Replace(content, needle, replacement, replaceAll);
            if (EditRefusal(match, display) is { } refused)
            {
                return refused;
            }

            string edited = match.Content;
            // Unchanged since it was read, or nothing written (2026-10-22, review J4: a patch or an append landing meanwhile was lost).
            if (StampOf(full) != before)
            {
                return new EditResult(FileOutcome.Failed, display, 0, 0, Detail: FileText.ChangedWhileEditing);
            }

            if (!Save(full, edited, codec, ending))
            {
                return new EditResult(FileOutcome.NotEncodable, display, 0, 0, Detail: codec.Label ?? "UTF-8");
            }
            var diff = FileDiff.Of(display, content, edited);

            if (match.Count > 1)
            {
                var lines = LineNumbers(content, match.Spans);
                Count(edited, out int totalLines, out int words);
                return new EditResult(FileOutcome.Ok, display, lines[0], match.Count, 0, 0, totalLines, null, 0, lines, Words: words, Strategy: match.Strategy) { Diff = diff };
            }

            // The new text's lines: from the placed replacement's first line through the last it reaches.
            var placed = match.Placed[0];
            int newFrom = 1 + edited.AsSpan(0, placed.Start).Count('\n');
            int newTo = newFrom + edited.AsSpan(placed.Start, placed.Length).Count('\n');
            if (placed.Length == 0)
            {
                newTo = newFrom - 1;
            }

            return Done(display, edited, newFrom, newTo, 1, match.Strategy) with { Diff = diff };
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return new EditResult(FileOutcome.Failed, display, 0, 0, Detail: ex.Message);
        }
    }

    /// <summary>
    /// Each span's first line, in one pass over the text (2026-10-22, review V1: counting from the file's start for every match was
    /// matches × size, a million one-character matches in 2 MB about 10^12 characters read).
    /// </summary>
    public static List<int> LineNumbers(string content, IReadOnlyList<TextSpan> spans)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(spans);
        var lines = new List<int>(spans.Count);
        int line = 1, at = 0;
        foreach (var span in spans)
        {
            line += content.AsSpan(at, span.Start - at).Count('\n');
            at = span.Start;
            lines.Add(line);
        }

        return lines;
    }

    /// <summary>The checks every edit makes before it reads the file: a folder, missing, too big, binary; null = go on, the content LF-normalised.</summary>
    private EditResult? LoadForEdit(string full, string display, out string content, out TextCodec codec, out string ending, out SeenStamp? stamp)
    {
        content = "";
        codec = TextCodec.Utf8;
        ending = "\n";
        stamp = null;
        EnsureExists();
        if (Directory.Exists(full))
        {
            return new EditResult(FileOutcome.IsDirectory, Relative(full, isDirectory: true), 0, 0);
        }

        if (!File.Exists(full))
        {
            return new EditResult(FileOutcome.Missing, display, 0, 0);
        }

        if (new FileInfo(full).Length > MaxTextFileBytes)
        {
            return new EditResult(FileOutcome.TooBig, display, 0, 0);
        }

        var info = new FileInfo(full);
        long length = info.Length;
        DateTime written = info.LastWriteTimeUtc;
        byte[] bytes = RegularFiles.ReadAllBytes(full);
        if (LooksBinary(bytes))
        {
            return new EditResult(FileOutcome.NotText, display, 0, 0);
        }

        stamp = SeenStamp.Of(length, written, bytes);

        // The file's own encoding (2026-10-22, review G4: a Windows-1252 file lost every non-ASCII character to U+FFFD, reported Ok).
        string raw = TextFile.Decode(bytes, out codec);
        ending = DetectLineEnding(raw);
        content = NormalizeNewlines(raw);
        return null;
    }

    /// <summary>
    /// A file's text before an overwrite replaces it, for the transcript's diff (2026-10-03): null for one over
    /// <see cref="MaxTextFileBytes"/>, one that looks binary, or one that cannot be read — the write goes on regardless.
    /// </summary>
    private static string? PreviousText(string full) => PreviousText(full, out _, out _);

    /// <summary><see cref="PreviousText(string)"/> with the file's encoding and its main line ending, for a write that keeps them (2026-10-22, review Q3).</summary>
    private static string? PreviousText(string full, out TextCodec? codec, out string ending)
    {
        codec = null;
        ending = "\n";
        try
        {
            if (new FileInfo(full).Length > MaxTextFileBytes)
            {
                return null;
            }

            byte[] bytes = RegularFiles.ReadAllBytes(full);
            if (LooksBinary(bytes))
            {
                return null;
            }

            string text = TextFile.Decode(bytes, out var found);
            codec = found;
            ending = DetectLineEnding(text);
            return text;
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return null;
        }
    }

    /// <summary>An LF-normalised text with <paramref name="ending"/> in place of each <c>\n</c>.</summary>
    private static string WithEnding(string normalised, string ending) =>
        ending == "\n" ? normalised : normalised.Replace("\n", ending, StringComparison.Ordinal);

    /// <summary>
    /// Writes an LF-normalised text back with the file's ending, encoding and mark (2026-10-22: the encoding too, <see cref="TextCodec"/>);
    /// false, nothing written, when the encoding can't hold the text.
    /// </summary>
    private bool Save(string full, string normalised, TextCodec codec, string ending)
    {
        if (codec.Encode(WithEnding(normalised, ending)) is not { } output)
        {
            return false;
        }

        RecheckLinks(full);
        WriteAtomically(full, output, _options.PreserveOnReplace);
        return true;
    }

    /// <summary>The successful edit's result with the region around the new text (<see cref="EditContextLines"/> each side, none over <see cref="MaxEditRegionLines"/>).</summary>
    private static EditResult Done(string display, string edited, int newFrom, int newTo, int count, MatchStrategy strategy = MatchStrategy.Exact)
    {
        var lines = SplitLines(edited);
        int total = lines.Count;
        int from = Math.Max(1, newFrom - EditContextLines);
        int to = Math.Min(total, Math.Max(newTo, newFrom - 1) + EditContextLines);
        List<string>? region = null;
        if (to >= from && to - from + 1 <= MaxEditRegionLines)
        {
            region = lines.GetRange(from - 1, to - from + 1);
        }

        Count(edited, out _, out int words);
        return new EditResult(FileOutcome.Ok, display, newFrom, count, newFrom, newTo, total, region, region is null ? 0 : from, null, Words: words, Strategy: strategy);
    }

    /// <summary>Every <c>\r\n</c> and lone <c>\r</c> as <c>\n</c>: the one form the edits match and splice in.</summary>
    public static string NormalizeNewlines(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Contains('\r') ? text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n') : text;
    }

    /// <summary>
    /// The line ending the file is written back with: the one it uses most (2026-10-22, review G12/Q3: the first break decided, so a mixed
    /// file took whatever came first), the first of a tie, <c>\n</c> when it has none.
    /// </summary>
    public static string DetectLineEnding(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int crlf = 0, lf = 0, cr = 0;
        string? first = null;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                bool pair = i + 1 < text.Length && text[i + 1] == '\n';
                first ??= pair ? "\r\n" : "\r";
                if (pair)
                {
                    crlf++;
                    i++;
                }
                else
                {
                    cr++;
                }
            }
            else if (text[i] == '\n')
            {
                first ??= "\n";
                lf++;
            }
        }

        int most = Math.Max(crlf, Math.Max(lf, cr));
        if (most == 0)
        {
            return "\n";
        }

        int tied = (crlf == most ? 1 : 0) + (lf == most ? 1 : 0) + (cr == most ? 1 : 0);
        return tied > 1 ? first! : crlf == most ? "\r\n" : lf == most ? "\n" : "\r";
    }

    /// <summary>The line endings a text uses, for <c>file_info</c>: <c>CRLF</c>, <c>LF</c>, <c>CR</c>, <c>mixed</c>, or empty with no line break.</summary>
    public static string DescribeLineEnding(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        bool crlf = false, lf = false, cr = false;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                lf = true;
            }
            else if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    crlf = true;
                    i++;
                }
                else
                {
                    cr = true;
                }
            }
        }

        int kinds = (crlf ? 1 : 0) + (lf ? 1 : 0) + (cr ? 1 : 0);
        return kinds switch
        {
            0 => "",
            1 => crlf ? "CRLF" : lf ? "LF" : "CR",
            _ => "mixed",
        };
    }

    public CreateResult CreateDirectory(string relative)
    {
        var outcome = Resolve(relative, forWrite: true, out string full);
        if (outcome != FileOutcome.Ok)
        {
            return new CreateResult(outcome, relative);
        }

        string display = Relative(full, isDirectory: true);
        try
        {
            EnsureExists();
            if (File.Exists(full))
            {
                return new CreateResult(FileOutcome.IsAFile, Relative(full));
            }

            if (Directory.Exists(full))
            {
                return new CreateResult(FileOutcome.Exists, display);
            }

            RecheckLinks(full);
            Directory.CreateDirectory(full);
            return new CreateResult(FileOutcome.Ok, display);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return new CreateResult(FileOutcome.Failed, display, ex.Message);
        }
    }

    /// <summary>
    /// Moves or renames a file or a folder. With <paramref name="overwrite"/> (2026-09-20) a file in the way is
    /// replaced in place and a folder in the way is <see cref="FileOutcome.FolderInTheWay"/>.
    /// </summary>
    public MoveResult Move(string from, string to, bool overwrite) => Transfer(from, to, overwrite, move: true);

    /// <summary>
    /// Copies a file or a folder (recursively, reparse points skipped). With <paramref name="overwrite"/>
    /// the rule is <see cref="Move"/>'s — but a folder copied over a folder merges into it (nothing is
    /// destroyed by a merge).
    /// </summary>
    public MoveResult Copy(string from, string to, bool overwrite) => Transfer(from, to, overwrite, move: false);

    private MoveResult Transfer(string from, string to, bool overwrite, bool move)
    {
        // A move renames a link itself (its target never moves); a copy reads through it, so a copy's source is judged whole.
        var outcome = Resolve(from, forWrite: move, linkItself: move, out string source);
        if (outcome != FileOutcome.Ok)
        {
            return new MoveResult(outcome, from, to, false, false);
        }

        // A folder moved takes its databases with it (2026-10-05): refused like moving one of them. Its .git may go along (a project moved).
        if (move && Directory.Exists(source) && RealLinkTarget(source) is null && Databases?.Invoke() is { } databases
            && HoldsGuarded(source, git: false, databases, CancellationToken.None) is { } held)
        {
            return new MoveResult(held, Relative(source, isDirectory: true), to, false, false);
        }

        outcome = Resolve(to, forWrite: true, out string destination);
        if (outcome != FileOutcome.Ok)
        {
            return new MoveResult(outcome, Relative(source), to, false, false) { AtDestination = true };
        }

        try
        {
            EnsureExists();
            bool isDirectory = Directory.Exists(source);
            if (!isDirectory && !File.Exists(source))
            {
                return new MoveResult(FileOutcome.Missing, Relative(source), Relative(destination), false, false);
            }

            string fromDisplay = Relative(source, isDirectory);
            string toDisplay = Relative(destination, isDirectory);
            if (string.Equals(source, destination, StringComparison.Ordinal) || string.Equals(source, Root, StringComparison.OrdinalIgnoreCase))
            {
                return new MoveResult(FileOutcome.Exists, fromDisplay, toDisplay, isDirectory, false);
            }

            // Into itself by where the paths lead, not their spelling (2026-10-22, review N5: a junction back into the source made a copy
            // recurse into its own output).
            if (isDirectory && IsInside(FinalPath(source), FinalPath(destination)))
            {
                return new MoveResult(FileOutcome.IntoItself, fromDisplay, toDisplay, isDirectory, false);
            }

            // A case-only change of name is a rename only where case is blind (2026-10-22, review G17: on a case-sensitive folder it
            // overwrote a different A.txt without overwrite).
            string destinationFolder = Path.GetDirectoryName(destination)!;
            bool caseOnly = !CaseRule.IsSensitive(destinationFolder) && string.Equals(source, destination, StringComparison.OrdinalIgnoreCase);
            bool inTheWay = !caseOnly && (File.Exists(destination) || Directory.Exists(destination));
            if (inTheWay && !overwrite)
            {
                return new MoveResult(FileOutcome.Exists, fromDisplay, toDisplay, isDirectory, false);
            }

            // What is in the way (2026-09-20): a file is replaced in place and a folder refused — except a
            // folder a folder is copied over, which merges.
            bool folderInTheWay = inTheWay && Directory.Exists(destination);
            bool merge = folderInTheWay && isDirectory && !move;
            if (folderInTheWay && !merge)
            {
                return new MoveResult(FileOutcome.FolderInTheWay, fromDisplay, Relative(destination, isDirectory: true), isDirectory, false);
            }

            // A folder's copy planned whole first (2026-10-22): its .git and the guarded files left out and listed (D17), the volume's free
            // space checked (D16), nothing written before every check passed.
            CopyPlan? plan = null;
            if (isDirectory && !move)
            {
                plan = PlanCopy(source, destination, this, CancellationToken.None);
                if (plan.Refusal is { } refused)
                {
                    return new MoveResult(refused.Outcome, fromDisplay, toDisplay, isDirectory, false, refused.Detail);
                }

                if (FreeSpaceRefusal(destinationFolder, plan.Bytes) is { } full)
                {
                    return new MoveResult(FileOutcome.Failed, fromDisplay, toDisplay, isDirectory, false, full);
                }
            }

            RecheckLinks(destination);
            Directory.CreateDirectory(destinationFolder);
            bool renamed = string.Equals(Path.GetDirectoryName(source), destinationFolder, StringComparison.OrdinalIgnoreCase);
            if (isDirectory)
            {
                // A file in the way of a folder is set aside, and deleted only once the folder is in (2026-10-22, review J7: it was deleted
                // first, and a failing move or copy then lost it).
                using var aside = inTheWay && !folderInTheWay ? Aside.Of(destination) : null;
                if (move)
                {
                    Directory.Move(source, destination);
                }
                else
                {
                    RunCopy(plan!);
                }

                aside?.Commit();
            }
            else if (move && inTheWay)
            {
                // Over a file: the one replace (mechanism 7), so its ACL and attributes stay and a read-only one is refused (review I4, O2).
                MoveIntoPlace(source, destination, _options.PreserveOnReplace, unhide: false);
            }
            else if (move)
            {
                File.Move(source, destination, overwrite: caseOnly);
            }
            else
            {
                CopyFile(source, destination, inTheWay, _options.PreserveOnReplace);
            }

            return new MoveResult(FileOutcome.Ok, fromDisplay, toDisplay, isDirectory, renamed)
            {
                Replaced = inTheWay,
                LeftOut = plan?.LeftOut ?? [],
            };
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return new MoveResult(FileOutcome.Failed, Relative(source), Relative(destination), false, false, ex.Message);
        }
    }

    /// <summary>The most bytes one <see cref="CopyBetween"/> carries (2026-09-30: <c>unc_fetch</c> and <c>unc_put</c>).</summary>
    public const long MaxTransferBytes = 500_000_000;

    /// <summary>The most files one <see cref="CopyBetween"/> of a folder carries.</summary>
    public const int MaxTransferFiles = 5_000;

    /// <summary>A <see cref="CopyBetween"/> over <see cref="MaxTransferBytes"/> or <see cref="MaxTransferFiles"/>: the detail of its <see cref="FileOutcome.Failed"/>. Pinned.</summary>
    public static string TransferCapDetail(int files, long bytes) =>
        string.Create(CultureInfo.InvariantCulture, $"{files:N0} files, {bytes:N0} bytes is over one transfer's cap of {MaxTransferFiles:N0} files and {MaxTransferBytes:N0} bytes; copy a smaller folder");

    /// <summary>
    /// Copies a file or a folder from one sandbox to another (2026-09-30, the UNC tools: a share to the working directory for
    /// <c>unc_fetch</c>, the other way for <c>unc_put</c>). Both paths go through their own <see cref="Resolve(string, bool, out string)"/>;
    /// a blank <paramref name="toRelative"/> is the source's own name at the target's root. The rest is <see cref="Copy"/>'s rule:
    /// something in the way only with <paramref name="overwrite"/>, a folder over a folder merged, a file in a folder's way
    /// <see cref="FileOutcome.FolderInTheWay"/>, a file over a file replaced. A file
    /// replaced on a target with <see cref="WorkingDirectoryOptions.PreserveOnReplace"/> keeps its ACL. One call carries at most
    /// <see cref="MaxTransferFiles"/> files and <see cref="MaxTransferBytes"/> bytes, counted before anything is written.
    /// </summary>
    public static MoveResult CopyBetween(WorkingDirectory from, string fromRelative, WorkingDirectory to, string? toRelative, bool overwrite) =>
        CopyBetween(from, fromRelative, to, toRelative, overwrite, CancellationToken.None);

    /// <summary>
    /// <see cref="CopyBetween(WorkingDirectory, string, WorkingDirectory, string?, bool)"/> under the call's token (2026-10-22, review H3:
    /// unc_fetch was abandoned on Esc while its copy ran on, unseen): read between files, and a folder's copy stopped there is taken back
    /// as a failed one is; the cancellation is thrown.
    /// </summary>
    public static MoveResult CopyBetween(WorkingDirectory from, string fromRelative, WorkingDirectory to, string? toRelative, bool overwrite, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        var outcome = from.Resolve(fromRelative, forWrite: false, out string source);
        if (outcome != FileOutcome.Ok)
        {
            return new MoveResult(outcome, fromRelative, toRelative ?? "", false, false);
        }

        string target = string.IsNullOrWhiteSpace(toRelative) ? Path.GetFileName(source) : toRelative.Trim();
        outcome = to.Resolve(target, forWrite: true, out string destination);
        if (outcome != FileOutcome.Ok)
        {
            return new MoveResult(outcome, from.Relative(source), target, false, false) { AtDestination = true };
        }

        try
        {
            from.EnsureExists();
            to.EnsureExists();
            bool isDirectory = Directory.Exists(source);
            if (!isDirectory && !File.Exists(source))
            {
                return new MoveResult(FileOutcome.Missing, from.Relative(source), to.Relative(destination), false, false);
            }

            string fromDisplay = from.Relative(source, isDirectory);
            string toDisplay = to.Relative(destination, isDirectory);
            if (string.Equals(destination, to.Root, StringComparison.OrdinalIgnoreCase))
            {
                return new MoveResult(FileOutcome.Exists, fromDisplay, toDisplay, isDirectory, false);
            }

            // A folder copied into itself across the two roots (2026-10-22, review I3: a share holding the working directory, or held by it,
            // made the copy recurse into its own output and fill the disk).
            if (isDirectory && IsInside(FinalPath(source), FinalPath(destination)))
            {
                return new MoveResult(FileOutcome.IntoItself, fromDisplay, toDisplay, isDirectory, false);
            }

            // Planned whole first (2026-10-22): folders counted with the files (review I5), every .git and guarded file left out (D17).
            CopyPlan? plan = isDirectory ? PlanCopy(source, destination, to, cancellationToken) : null;
            if (plan?.Refusal is { } refused)
            {
                return new MoveResult(refused.Outcome, fromDisplay, toDisplay, isDirectory, false, refused.Detail);
            }

            int files = plan is null ? 1 : plan.Files.Count + plan.Folders.Count;
            long bytes = plan?.Bytes ?? new FileInfo(source).Length;
            if (files > MaxTransferFiles || bytes > MaxTransferBytes)
            {
                return new MoveResult(FileOutcome.Failed, fromDisplay, toDisplay, isDirectory, false, TransferCapDetail(files, bytes));
            }

            bool inTheWay = File.Exists(destination) || Directory.Exists(destination);
            if (inTheWay && !overwrite)
            {
                return new MoveResult(FileOutcome.Exists, fromDisplay, toDisplay, isDirectory, false);
            }

            bool folderInTheWay = inTheWay && Directory.Exists(destination);
            if (folderInTheWay && !isDirectory)
            {
                return new MoveResult(FileOutcome.FolderInTheWay, fromDisplay, to.Relative(destination, isDirectory: true), isDirectory, false);
            }

            to.RecheckLinks(destination);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (isDirectory)
            {
                // A file in the way set aside until the folder is in (review J7).
                using var aside = inTheWay && !folderInTheWay ? Aside.Of(destination) : null;
                RunCopy(plan!, cancellationToken);
                aside?.Commit();
            }
            else
            {
                cancellationToken.ThrowIfCancellationRequested();
                CopyFile(source, destination, inTheWay, to._options.PreserveOnReplace);
            }

            return new MoveResult(FileOutcome.Ok, fromDisplay, toDisplay, isDirectory, false) { Replaced = inTheWay, LeftOut = plan?.LeftOut ?? [] };
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return new MoveResult(FileOutcome.Failed, from.Relative(source), to.Relative(destination), false, false, ex.Message);
        }
    }

    /// <summary>
    /// Removes a file or a folder (to the Recycle bin while <see cref="RecycleDeletes"/> hands one, later on 2026-10-15), a folder with everything in it: the one recursive delete in the sandbox
    /// (in place since 2026-09-20 with File safe edits off, and always since 2026-10-01, when that setting and its
    /// <c>.trash</c> went, the user's call). The root is refused, and so (2026-09-23, the user's call) are <c>.git</c>,
    /// anything in it and a folder with a <c>.git</c> anywhere under it — <see cref="FileOutcome.GitProtected"/>: a
    /// repository's history is not the model's to lose.
    /// </summary>
    public DeleteResult Delete(string relative) => Delete(relative, CancellationToken.None);

    /// <summary>
    /// <see cref="Delete(string)"/> with the walk under a folder cancellable (2026-10-22, review P9: the .git and database walks ran one after
    /// the other, unbounded, on the turn's thread; now one walk, read between entries, which the tool runs off that thread).
    /// </summary>
    public DeleteResult Delete(string relative, CancellationToken cancellationToken)
    {
        var outcome = Resolve(relative, forWrite: true, linkItself: true, out string full);
        if (outcome != FileOutcome.Ok)
        {
            return new DeleteResult(outcome, relative, false);
        }

        try
        {
            EnsureExists();
            bool isDirectory = Directory.Exists(full);
            if (!isDirectory && !File.Exists(full))
            {
                return new DeleteResult(FileOutcome.Missing, Relative(full), false);
            }

            string display = Relative(full, isDirectory);
            if (string.Equals(full, Root, StringComparison.OrdinalIgnoreCase))
            {
                return new DeleteResult(FileOutcome.IntoItself, display, true);
            }

            // A link (2026-10-03) goes alone: its target is neither searched for a .git nor emptied, only the link removed.
            bool isLink = RealLinkTarget(full) is not null;
            if (IsGitPath(Relative(full)))
            {
                return new DeleteResult(FileOutcome.GitProtected, display, isDirectory);
            }

            // Uncapped on purpose: a guard never passes what it didn't look at; a big folder (node_modules) still deletes after one walk.
            if (isDirectory && !isLink && HoldsGuarded(full, git: true, Databases?.Invoke(), cancellationToken) is { } held)
            {
                return new DeleteResult(held, display, isDirectory);
            }

            // To the bin while the setting is on (later on 2026-10-15); a link, a drive with no bin and the setting off delete directly.
            RecheckLinks(full, linkItself: true);
            var way = FileDeletes.Remove(full, isDirectory, isLink, RecycleDeletes?.Invoke());
            DiagnosticLog.Debug(Category, DeletedLogLine(Relative(full)) + (way == DeleteWay.Recycled ? " (to the " + FileText.RecycleBinName + ")" : ""));
            return new DeleteResult(FileOutcome.Ok, display, isDirectory) { Way = way };
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return new DeleteResult(FileOutcome.Failed, Relative(full), false, ex.Message);
        }
    }

    /// <summary>Whether a segment of <paramref name="relative"/> is <see cref="GitFolderName"/>, in any case: <c>.git</c> itself or anything under it. Pure.</summary>
    public static bool IsGitPath(string relative)
    {
        ArgumentNullException.ThrowIfNull(relative);
        return relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .Any(part => string.Equals(part, GitFolderName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// What under <paramref name="directory"/> a delete or move must not take (2026-10-22, review P9, one walk for both): with
    /// <paramref name="git"/>, a <c>.git</c> (a folder, or the file a worktree or submodule keeps in its place), and with
    /// <paramref name="databases"/>, a database file — the first hit ends the walk, never through a reparse point, an unreadable folder
    /// passed over. Null when there is neither. Reads <paramref name="cancellationToken"/> between entries.
    /// </summary>
    internal static FileOutcome? HoldsGuarded(string directory, bool git, DatabaseGuard? databases, CancellationToken cancellationToken)
    {
        if (!git && databases is null)
        {
            return null;
        }

        // A cloud placeholder is walked (review O1: a cloud-tagged .git passed); a real link never is.
        // A file's header is read only when it could be a database and is on this disk (2026-10-24, the review of these fixes: the walk opened every
        // file, hydrating each cloud placeholder of a OneDrive folder before its delete): at least one page (512 bytes), not offline or a recall.
        var entries = new FileSystemEnumerable<(string Path, bool IsDirectory, bool Sniff)>(
            directory,
            (ref FileSystemEntry entry) => (entry.ToFullPath(), entry.IsDirectory,
                !entry.IsDirectory && entry.Length >= 512 && (entry.Attributes & (FileAttributes.Offline | (FileAttributes)0x00040000 | (FileAttributes)0x00400000)) == 0),
            new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0, ReturnSpecialDirectories = false })
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) => !IsLink(ref entry),
            ShouldRecursePredicate = (ref FileSystemEntry entry) => !IsLink(ref entry),
        };
        foreach (var (path, isDirectory, sniff) in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (git && string.Equals(Path.GetFileName(path), GitFolderName, StringComparison.OrdinalIgnoreCase))
            {
                return FileOutcome.GitProtected;
            }

            // By content too since 2026-10-24 (the database review's B6: a database whose name says nothing went with its folder).
            if (databases is not null && !isDirectory && (databases.IsDatabaseFile(path) || (sniff && DatabaseGuard.HasSqliteHeader(path))))
            {
                return FileOutcome.DatabaseProtected;
            }
        }

        return null;
    }

    /// <summary>The <see cref="Delete"/>: <c>Deleted notes.md</c>. Pinned.</summary>
    public static string DeletedLogLine(string relative) => $"Deleted {relative}";

    /// <summary>
    /// Deletes everything under <paramref name="relative"/> for good; the folder itself stays (2026-09-24, the user's
    /// ask: <c>/comfy purge</c> empties the ComfyUI output folder, its <c>.pasted</c> inputs included), reached only by
    /// a slash command after a typed confirmation, never by a tool. Refused outside the sandbox, on the root itself (an
    /// empty output folder setting means the working directory — purging that would take everything) and on a file.
    /// A missing folder is nothing to do. <see cref="ListPurge"/> then <see cref="PurgeFolder(PurgeListing, IRecycleBin?, CancellationToken)"/>
    /// in one call, for a caller with no question between them.
    /// </summary>
    public PurgeResult PurgeFolder(string relative, IRecycleBin? bin = null) => PurgeFolder(ListPurge(relative), bin);

    /// <summary>
    /// A folder's purge listed once, before its question (2026-10-20, the code review: the question's count, the spinner's label and the work
    /// each listed the folder again, and could disagree): the folder's own entries in name order, each with its own files, folders and
    /// bytes, so the purge's notice adds up only what really went. Dot-names count off Windows too (<see cref="Info"/>'s walk leaves them out,
    /// and the purge takes them). A top entry that is a link is listed as one, never walked. A folder under a top entry that can't be read is
    /// left out of the count rather than failing the listing (the bin takes the top entry whole; a delete for good that fails on it is that
    /// entry's, reported, and the rest go on). Refused as <see cref="PurgeFolder(string, IRecycleBin?)"/> refuses; a missing folder is
    /// <see cref="FileOutcome.Missing"/>. <paramref name="cancellationToken"/> (later on 2026-10-20, the code review: the listing ran on the
    /// screen's thread before the question, with no cap and no Esc) is read between entries, the walk under a top folder too, and throws
    /// <see cref="OperationCanceledException"/>: the caller lists on a pool thread under the spinner, Esc watched.
    /// </summary>
    public PurgeListing ListPurge(string relative, CancellationToken cancellationToken = default)
    {
        var outcome = Resolve(relative, forWrite: true, out string full);
        if (outcome != FileOutcome.Ok)
        {
            return PurgeListing.Refused(outcome, full);
        }

        if (string.Equals(full, Root, StringComparison.OrdinalIgnoreCase))
        {
            return PurgeListing.Refused(FileOutcome.OutsideRoot, full, PurgeRootRefusal);
        }

        if (File.Exists(full))
        {
            return PurgeListing.Refused(FileOutcome.IsAFile, full);
        }

        if (!Directory.Exists(full))
        {
            return PurgeListing.Refused(FileOutcome.Missing, full);
        }

        try
        {
            // Everything at the top, hidden and system included (the default skips them) — nothing in the folder is spared.
            var top = new FileSystemEnumerable<PurgeEntry>(
                full,
                (ref FileSystemEntry entry) => new PurgeEntry(
                    entry.ToFullPath(),
                    entry.IsDirectory,
                    (entry.Attributes & FileAttributes.ReparsePoint) != 0,
                    entry.IsDirectory ? 0 : 1,
                    entry.IsDirectory ? 1 : 0,
                    entry.IsDirectory ? 0 : entry.Length),
                new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0, ReturnSpecialDirectories = false });
            var entries = new List<PurgeEntry>();
            foreach (var entry in top)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.IsLink)
                {
                    entries.Add(entry with { Files = 0, Folders = 0, Bytes = 0 });
                }
                else if (entry.IsDirectory)
                {
                    var (files, folders, bytes) = CountUnder(entry.FullPath, cancellationToken);
                    entries.Add(entry with { Files = files, Folders = folders + 1, Bytes = bytes });
                }
                else
                {
                    entries.Add(entry);
                }
            }

            entries.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.FullPath, b.FullPath));
            return new PurgeListing(FileOutcome.Ok, full, entries, "");
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return PurgeListing.Refused(FileOutcome.Failed, full, ex.Message);
        }
    }

    // A top folder's own count: everything under it, hidden included, never through a link, a folder it can't read left out — the top
    // folder itself too (unreadable, or gone mid-walk): what was counted stands, and the purge meets that entry on its own.
    private static (int Files, int Folders, long Bytes) CountUnder(string folder, CancellationToken cancellationToken)
    {
        int files = 0, folders = 0;
        long bytes = 0;
        try
        {
            foreach (var entry in new FileSystemEnumerable<WalkEntry>(
                folder,
                (ref FileSystemEntry entry) => new WalkEntry(entry.ToFullPath(), entry.IsDirectory ? 0 : entry.Length, default, entry.IsDirectory),
                new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint, ReturnSpecialDirectories = false }))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.IsDirectory)
                {
                    folders++;
                }
                else
                {
                    files++;
                    bytes += entry.Length;
                }
            }
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            // Counted as far as it could read; the listing goes on.
        }

        return (files, folders, bytes);
    }

    /// <summary>
    /// The purge of a <see cref="ListPurge"/> listing, the way its question said (2026-10-20, the code review): with <paramref name="bin"/>
    /// (2026-10-18, the user's pick: the purges follow <c>File deletes to Recycle bin</c>) each of the folder's own entries goes to the bin
    /// through <see cref="IRecycleBin.Move"/>, so each restores to its place, and the folder stays: the picture viewer and the thumbnail browser
    /// watch it, and a folder moved to the bin and made again would leave them watching the copy there. Without one each is deleted for good,
    /// read-only cleared where a delete is refused. The caller decided which before it asked (<see cref="IRecycleBin.Probe"/>), and the purge
    /// never deletes for good what the user agreed to send to the bin. One bad entry never stops it: a link is removed (the link goes, never
    /// what it points at) and counted apart; an entry gone since the listing is skipped and counted; an entry the bin refuses (a path too long
    /// for the shell among them) or a delete refuses is left, counted, its first reason kept. It stops only where going on can't help: a bin
    /// that didn't answer (30 s a call), a drive with no bin after all (a Mac's volume, which <see cref="MacTrash"/> can't tell beforehand:
    /// <see cref="PurgeStop.NoBin"/>, nothing deleted for good), and <paramref name="cancellationToken"/> (Esc), checked between entries.
    /// Never throws for a file failure or a cancel: the result says how far it got.
    /// </summary>
    public PurgeResult PurgeFolder(PurgeListing listing, IRecycleBin? bin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(listing);
        if (listing.Outcome == FileOutcome.Missing)
        {
            return new PurgeResult(FileOutcome.Ok, 0, 0, 0);
        }

        if (listing.Outcome != FileOutcome.Ok)
        {
            return new PurgeResult(listing.Outcome, 0, 0, 0, listing.Detail);
        }

        if (!IsInside(Root, listing.FullPath) || string.Equals(listing.FullPath, Root, StringComparison.OrdinalIgnoreCase))
        {
            return new PurgeResult(FileOutcome.OutsideRoot, 0, 0, 0);
        }

        var result = EmptyContents(listing, bin, cancellationToken);
        if (result.Outcome == FileOutcome.Ok)
        {
            DiagnosticLog.Info(Category, FolderPurgedLogLine(Relative(listing.FullPath, isDirectory: true), result.Files, result.Folders, result.Bytes));
        }

        return result;
    }

    /// <summary>Why <see cref="PurgeFolder(string, IRecycleBin?)"/> will not empty the root. Pinned.</summary>
    public const string PurgeRootRefusal = "that is the working directory itself";

    public static string FolderPurgedLogLine(string relative, int files, int folders, long bytes) =>
        string.Create(CultureInfo.InvariantCulture, $"Purged {relative}: {files} files, {folders} folders, {bytes:N0} bytes");

    // The walk-and-delete behind PurgeFolder, one entry at a time in the listing's order; see PurgeFolder(PurgeListing, …) for the rules.
    private static PurgeResult EmptyContents(PurgeListing listing, IRecycleBin? bin, CancellationToken cancellationToken)
    {
        int files = 0, folders = 0, recycled = 0, deleted = 0, links = 0, vanished = 0, left = 0, done = 0;
        long bytes = 0;
        string why = "";
        var stop = PurgeStop.None;
        foreach (var entry in listing.Entries)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                stop = PurgeStop.Cancelled;
                break;
            }

            try
            {
                if (entry.IsLink)
                {
                    FileDeletes.Remove(entry.FullPath, entry.IsDirectory, isLink: true, bin: null);
                    links++;
                }
                else if (!(entry.IsDirectory ? Directory.Exists(entry.FullPath) : File.Exists(entry.FullPath)))
                {
                    vanished++;
                }
                else if (bin is not null)
                {
                    var moved = bin.Move(entry.FullPath);
                    if (moved.Outcome == RecycleOutcome.Recycled)
                    {
                        recycled++;
                        Went(entry);
                    }
                    else if (moved.Outcome == RecycleOutcome.NoBin)
                    {
                        stop = PurgeStop.NoBin;
                        break;
                    }
                    else if (moved.Message == FileText.RecycleNoAnswer)
                    {
                        stop = PurgeStop.NoAnswer;
                        why = moved.Message;
                        break;
                    }
                    else
                    {
                        Left(moved.Message);
                    }
                }
                else
                {
                    DeleteForGood(entry);
                    deleted++;
                    Went(entry);
                }
            }
            catch (Exception ex) when (IsFileFailure(ex))
            {
                if (Present(entry.FullPath))
                {
                    Left(ex.Message);
                }
                else if (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    // Taken by someone else between the check and the delete.
                    vanished++;
                }
                else if (entry.IsLink)
                {
                    links++;
                }
                else
                {
                    // A delete for good that got there in the end (a last step refused, then the item gone anyway).
                    deleted++;
                    Went(entry);
                }
            }

            done++;
        }

        bool finished = stop == PurgeStop.None && left == 0;
        return new PurgeResult(finished ? FileOutcome.Ok : FileOutcome.Failed, files, folders, bytes, why)
        {
            ToBin = bin is not null,
            Entries = listing.Entries.Count,
            Done = done,
            Recycled = recycled,
            Deleted = deleted,
            Links = links,
            Vanished = vanished,
            Left = left,
            Stop = stop,
        };

        void Went(PurgeEntry entry)
        {
            files += entry.Files;
            folders += entry.Folders;
            bytes += entry.Bytes;
        }

        void Left(string reason)
        {
            left++;
            if (why.Length == 0)
            {
                why = reason;
            }
        }
    }

    // One entry for good: a delete refused (a read-only file on Windows, a folder with no write permission on a Mac) clears read-only
    // under it and tries once more, as the walk before every delete did until 2026-10-20 — now only where it is needed.
    private static void DeleteForGood(PurgeEntry entry)
    {
        try
        {
            Remove(entry);
        }
        catch (UnauthorizedAccessException)
        {
            ClearReadOnly(entry.FullPath, entry.IsDirectory);
            Remove(entry);
        }

        static void Remove(PurgeEntry entry)
        {
            if (entry.IsDirectory)
            {
                Directory.Delete(entry.FullPath, recursive: true);
            }
            else
            {
                File.Delete(entry.FullPath);
            }
        }
    }

    private static void ClearReadOnly(string path, bool isDirectory)
    {
        Clear(path);
        if (!isDirectory)
        {
            return;
        }

        foreach (string inner in new FileSystemEnumerable<string>(
            path,
            (ref FileSystemEntry entry) => entry.ToFullPath(),
            new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint, ReturnSpecialDirectories = false }))
        {
            Clear(inner);
        }

        static void Clear(string path)
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            }
        }
    }

    // Whether anything is at the path, a dangling link included (File.Exists and Directory.Exists follow it).
    private static bool Present(string path)
    {
        try
        {
            return File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null;
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return false;
        }
    }

    // ---- archives ----

    /// <summary>Packs a file or folder into a zip (the folder's own name as the top-level entry); blank <paramref name="to"/> = next to it.</summary>
    public ZipResult Zip(string relative, string? to, bool overwrite)
    {
        var outcome = Resolve(relative, forWrite: false, out string source);
        if (outcome != FileOutcome.Ok)
        {
            return new ZipResult(outcome, relative, to ?? "", 0, 0);
        }

        try
        {
            EnsureExists();
            bool isDirectory = Directory.Exists(source);
            if (!isDirectory && !File.Exists(source))
            {
                return new ZipResult(FileOutcome.Missing, Relative(source), to ?? "", 0, 0);
            }

            string display = Relative(source, isDirectory);
            string archive;
            bool named = !string.IsNullOrWhiteSpace(to);
            if (!named)
            {
                archive = isDirectory ? source + ".zip" : Path.ChangeExtension(source, ".zip");
                // A .zip zipped with no to named the archive itself (2026-10-22, review X3: with overwrite, release.zip became a zip of
                // the old release.zip): into itself.
                if (string.Equals(source, Root, StringComparison.OrdinalIgnoreCase) || string.Equals(archive, source, StringComparison.OrdinalIgnoreCase))
                {
                    return new ZipResult(FileOutcome.IntoItself, display, Relative(archive), 0, 0);
                }

                // An unnamed archive is never a replace (2026-10-22, review R12, the fix plan's mechanism 4): the first free name, -2, -3…;
                // overwrite is for a named to alone.
                archive = FirstFree(archive);
            }
            else
            {
                outcome = Resolve(to!, forWrite: true, out archive);
                if (outcome != FileOutcome.Ok)
                {
                    return new ZipResult(outcome, display, to!, 0, 0) { AtDestination = true };
                }
            }

            // Into itself by where the paths lead (2026-10-22, review N5: a junction let the archive be written into the folder it zips).
            if (isDirectory && IsInside(FinalPath(source), FinalPath(archive)))
            {
                return new ZipResult(FileOutcome.IntoItself, display, Relative(archive), 0, 0);
            }

            if (Directory.Exists(archive))
            {
                return new ZipResult(FileOutcome.IsDirectory, display, Relative(archive, isDirectory: true), 0, 0);
            }

            bool replaced = File.Exists(archive);
            if (replaced && !overwrite)
            {
                return new ZipResult(FileOutcome.Exists, display, Relative(archive), 0, 0);
            }

            // The entries planned first (2026-10-22): hidden files and dot-names kept (D14), empty folders as entries (review V11), links and
            // temp siblings never, a Mac name with a backslash left out (review N6: it became a path in the archive), and the caps counted
            // before anything is written, folders with the files (review G7).
            var leftOut = new List<string>();
            var plan = isDirectory ? ZipEntries(source, leftOut) : [(source, Path.GetFileName(source), false)];
            int counted = isDirectory ? plan.Count + CountFolders(source) : 1;
            long bytes = plan.Where(e => !e.Folder).Sum(e => new FileInfo(e.Full).Length);
            if (counted > MaxTransferFiles || bytes > MaxTransferBytes)
            {
                return new ZipResult(FileOutcome.Failed, display, Relative(archive), 0, 0, ZipCapDetail(counted, bytes));
            }

            RecheckLinks(archive);
            Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
            string temp = TempSibling(archive);
            try
            {
                using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
                {
                    foreach (var (full, name, folder) in plan)
                    {
                        if (folder)
                        {
                            zip.CreateEntry(name);
                        }
                        else
                        {
                            zip.CreateEntryFromFile(full, name, CompressionLevel.Optimal);
                        }
                    }
                }

                if (replaced)
                {
                    MoveIntoPlace(temp, archive, _options.PreserveOnReplace);
                }
                else
                {
                    MoveTempOver(temp, archive);
                }
            }
            finally
            {
                DeleteQuietly(temp);
            }

            return new ZipResult(FileOutcome.Ok, display, Relative(archive), plan.Count, new FileInfo(archive).Length) { Replaced = replaced, LeftOut = leftOut };
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return new ZipResult(FileOutcome.Failed, Relative(source), to ?? "", 0, 0, ex.Message);
        }
    }

    /// <summary>
    /// A folder's zip entries, sorted by name, ordinal (2026-10-06, the macOS build: the same folder makes the same zip on both systems): its
    /// files and its empty folders (a trailing '/'), walked without recursion, never through a link or onto a temp sibling, hidden ones kept.
    /// </summary>
    private static List<(string Full, string Name, bool Folder)> ZipEntries(string source, List<string> leftOut)
    {
        string baseName = Path.GetFileName(source);
        var entries = new List<(string Full, string Name, bool Folder)>();
        var pending = new Stack<string>();
        pending.Push(source);
        while (pending.Count > 0)
        {
            string folder = pending.Pop();
            bool empty = true;
            foreach (var info in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }))
            {
                if (IsLink(info) || IsTempSibling(info.Name))
                {
                    continue;
                }

                empty = false;
                string under = Path.GetRelativePath(source, info.FullName);
                if (!OperatingSystem.IsWindows() && under.Contains('\\', StringComparison.Ordinal))
                {
                    leftOut.Add(under);
                    continue;
                }

                if ((info.Attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(info.FullName);
                }
                else
                {
                    entries.Add((info.FullName, baseName + "/" + under.Replace('\\', '/'), false));
                }
            }

            if (empty && !string.Equals(folder, source, StringComparison.Ordinal))
            {
                entries.Add((folder, baseName + "/" + Path.GetRelativePath(source, folder).Replace('\\', '/') + "/", true));
            }
        }

        entries.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
        return entries;
    }

    private static int CountFolders(string source) =>
        new DirectoryInfo(source).EnumerateDirectories("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).Take(MaxTransferFiles + 1).Count();

    /// <summary>A zip or unzip past the caps (2026-10-22, review G7): the entries and bytes, against one transfer's caps.</summary>
    public static string ZipCapDetail(int entries, long bytes) =>
        string.Create(CultureInfo.InvariantCulture, $"{entries:N0} entries, {bytes:N0} bytes is over the cap of {MaxTransferFiles:N0} entries and {MaxTransferBytes:N0} bytes; nothing was written");

    /// <summary>The ratio past which an entry over <see cref="BombEntryBytes"/> is a zip bomb's (2026-10-22, review G7).</summary>
    public const int MaxCompressionRatio = 100;

    /// <summary>The size an entry must pass before its ratio is judged.</summary>
    public const long BombEntryBytes = 1_000_000;

    /// <summary>
    /// The first free path of <paramref name="full"/>'s spelling (2026-10-22, the fix plan's mechanism 4, PdfEditing.Write's rule of 2026-10-20):
    /// itself, then <c>name-2.ext</c>, <c>name-3.ext</c>… — what an unnamed default output takes, so it never replaces anything.
    /// </summary>
    public static string FirstFree(string full)
    {
        ArgumentNullException.ThrowIfNull(full);
        for (int n = 1; n < 1000; n++)
        {
            string candidate = n == 1 ? full : Images.ImageOutput.Numbered(full, n);
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return full;
    }

    /// <summary>
    /// Extracts a zip into a folder (blank <paramref name="to"/> = one named after the archive, next to it). All or nothing since 2026-10-22
    /// (review G8): every entry is planned before anything is written — inside the folder (zip-slip) with no link on its way leading out,
    /// free without <paramref name="overwrite"/>, no two entries on one path and no file where a folder goes, the caps and a bomb's ratio
    /// (review G7), every <c>.git</c> and every guarded target left out and listed (D17) — then each file is extracted to a temp sibling, and
    /// only once all of them are is each moved into place; a failure on the way takes the temps and the new folders back.
    /// </summary>
    public ZipResult Unzip(string relative, string? to, bool overwrite)
    {
        var outcome = Resolve(relative, forWrite: false, out string source);
        if (outcome != FileOutcome.Ok)
        {
            return new ZipResult(outcome, relative, to ?? "", 0, 0);
        }

        string display = Relative(source);
        try
        {
            EnsureExists();
            if (Directory.Exists(source))
            {
                return new ZipResult(FileOutcome.IsDirectory, Relative(source, isDirectory: true), to ?? "", 0, 0);
            }

            if (!File.Exists(source))
            {
                return new ZipResult(FileOutcome.Missing, display, to ?? "", 0, 0);
            }

            if (!LooksLikeZip(source))
            {
                return new ZipResult(FileOutcome.NotAnArchive, display, to ?? "", 0, 0);
            }

            string folder;
            if (string.IsNullOrWhiteSpace(to))
            {
                // A name with no stem (".zip") or no extension ("data") named the archive's own folder or the archive itself (2026-10-22,
                // review S9): such an archive unzips into "<name>-files".
                string stem = Path.GetFileNameWithoutExtension(source);
                folder = Path.Combine(Path.GetDirectoryName(source)!, stem);
                if (stem.Length == 0 || string.Equals(folder, source, StringComparison.OrdinalIgnoreCase))
                {
                    folder = source + "-files";
                }
            }
            else
            {
                outcome = Resolve(to, forWrite: true, out folder);
                if (outcome != FileOutcome.Ok)
                {
                    return new ZipResult(outcome, display, to, 0, 0) { AtDestination = true };
                }
            }

            string folderDisplay = Relative(folder, isDirectory: true);
            if (File.Exists(folder))
            {
                return new ZipResult(FileOutcome.IsAFile, display, Relative(folder), 0, 0);
            }

            // Inside by the links too (2026-10-03, the review): a blank destination is named after the archive and never went
            // through Resolve, and an entry's folders may be links already there (out/x with out → C:\Users).
            string root = Root;
            if (LinkEscape(root, folder, RealLinkTarget) is not null)
            {
                return new ZipResult(FileOutcome.OutsideRoot, display, folderDisplay, 0, 0);
            }

            using var zip = ZipFile.OpenRead(source);
            var files = new List<(ZipArchiveEntry Entry, string Target, bool Exists)>();
            var folders = new List<string> { folder };
            var leftOut = new List<string>();
            var comparer = CaseRule.Comparer(Path.GetDirectoryName(folder) ?? folder);
            var taken = new HashSet<string>(comparer);
            var asFolders = new HashSet<string>(comparer);
            long total = 0;
            if (zip.Entries.Count > MaxTransferFiles)
            {
                return new ZipResult(FileOutcome.Failed, display, folderDisplay, 0, 0, ZipCapDetail(zip.Entries.Count, 0));
            }

            foreach (var entry in zip.Entries)
            {
                bool isFolderEntry = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
                string target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(folder, entry.FullName)));
                if (!IsInside(folder, target) || LinkEscape(root, target, RealLinkTarget) is not null)
                {
                    return new ZipResult(FileOutcome.OutsideRoot, display, folderDisplay, 0, 0, entry.FullName);
                }

                // A repository's data and the guarded files are left out and said (D17): no hooks or config planted by an archive.
                if (IsGitPath(Path.GetRelativePath(folder, target)) || WriteGuard.Judge(root, target, WriteRole.Write, ShoppingListGuard, Databases) is not null)
                {
                    leftOut.Add(Relative(target, isFolderEntry));
                    continue;
                }

                if (isFolderEntry)
                {
                    if (File.Exists(target) || taken.Contains(target))
                    {
                        return new ZipResult(FileOutcome.IsAFile, display, Relative(target), 0, 0);
                    }

                    asFolders.Add(target);
                    folders.Add(target);
                    continue;
                }

                // A bomb's ratio and the total, counted before the write (review G7).
                if (entry.Length > BombEntryBytes && entry.CompressedLength > 0 && entry.Length / entry.CompressedLength > MaxCompressionRatio)
                {
                    return new ZipResult(FileOutcome.Failed, display, folderDisplay, 0, 0, FileText.ZipBomb(entry.FullName));
                }

                total += entry.Length;
                if (total > MaxTransferBytes)
                {
                    return new ZipResult(FileOutcome.Failed, display, folderDisplay, 0, 0, ZipCapDetail(zip.Entries.Count, total));
                }

                // Two entries on one path, or a file where another entry's folder goes (review G8: the first was written, then the second
                // failed with the tree half made).
                if (!taken.Add(target) || asFolders.Contains(target) || IsUnderAny(target, taken, comparer))
                {
                    return new ZipResult(FileOutcome.Exists, display, Relative(target), 0, 0, FileText.ZipConflict(entry.FullName));
                }

                if (Directory.Exists(target))
                {
                    return new ZipResult(FileOutcome.FolderInTheWay, display, Relative(target, isDirectory: true), 0, 0);
                }

                bool exists = File.Exists(target);
                if (!overwrite && exists)
                {
                    return new ZipResult(FileOutcome.Exists, display, Relative(target), 0, 0);
                }

                files.Add((entry, target, exists));
            }

            // The archive's internet mark goes on what comes out of it (2026-10-22, review O5): SmartScreen, Protected View and Gatekeeper
            // still ask about a downloaded archive's program or document.
            byte[]? mark = MarkOfTheWeb.Read(source);
            var made = new List<string>();
            var temps = new List<(string Temp, string Target, bool Exists)>();
            try
            {
                foreach (string each in folders.Concat(files.Select(f => Path.GetDirectoryName(f.Target)!)).Distinct(comparer).OrderBy(f => f.Length))
                {
                    if (!Directory.Exists(each))
                    {
                        RecheckLinks(each);
                        Directory.CreateDirectory(each);
                        made.Add(each);
                    }
                }

                foreach (var (entry, target, exists) in files)
                {
                    RecheckLinks(target);
                    string temp = TempSibling(target);
                    temps.Add((temp, target, exists));
                    entry.ExtractToFile(temp, overwrite: false);
                }
            }
            catch (Exception ex) when (IsFileFailure(ex) || ex is InvalidDataException)
            {
                // Nothing left behind: the temps and the folders this call made go (deepest first).
                foreach (var (temp, _, _) in temps)
                {
                    DeleteQuietly(temp);
                }

                foreach (string each in made.OrderByDescending(f => f.Length))
                {
                    try
                    {
                        Directory.Delete(each);
                    }
                    catch (Exception cleanup) when (IsFileFailure(cleanup))
                    {
                    }
                }

                throw;
            }

            bool replaced = false;
            foreach (var (temp, target, exists) in temps)
            {
                if (exists)
                {
                    MoveIntoPlace(temp, target, _options.PreserveOnReplace);
                    replaced = true;
                }
                else
                {
                    MoveTempOver(temp, target);
                }
            }

            MarkOfTheWeb.Carry(mark, files.Select(f => f.Target));
            return new ZipResult(FileOutcome.Ok, display, folderDisplay, files.Count, new FileInfo(source).Length) { Replaced = replaced, LeftOut = leftOut };
        }
        catch (InvalidDataException ex)
        {
            return new ZipResult(FileOutcome.NotAnArchive, display, to ?? "", 0, 0, ex.Message);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return new ZipResult(FileOutcome.Failed, display, to ?? "", 0, 0, ex.Message);
        }
    }

    /// <summary>Whether <paramref name="target"/> lies under one of the files already planned (a file where a folder must go).</summary>
    private static bool IsUnderAny(string target, HashSet<string> files, StringComparer comparer)
    {
        for (string? parent = Path.GetDirectoryName(target); parent is not null; parent = Path.GetDirectoryName(parent))
        {
            if (files.Contains(parent))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Hands a file or folder (blank = the root) to <paramref name="opener"/>, the shell's "open with" call.
    /// <paramref name="foldersOnly"/> refuses a file with <see cref="FileOutcome.IsAFile"/> before the opener is
    /// called (the <c>/explore</c> command browses; the <c>open</c> tool takes either). <paramref name="filesOnly"/> is the
    /// mirror (2026-10-09, <c>/open</c> and <c>/browse</c>): a folder is <see cref="FileOutcome.IsDirectory"/>, the opener never called.
    /// </summary>
    public OpenResult Open(string relative, Action<string> opener, bool foldersOnly = false, bool filesOnly = false)
    {
        ArgumentNullException.ThrowIfNull(opener);
        var outcome = Resolve(relative, forWrite: false, out string full);
        if (outcome != FileOutcome.Ok)
        {
            return new OpenResult(outcome, relative, false);
        }

        try
        {
            EnsureExists();
            bool isDirectory = Directory.Exists(full);
            if (!isDirectory && !File.Exists(full))
            {
                return new OpenResult(FileOutcome.Missing, Relative(full), false);
            }

            if (foldersOnly && !isDirectory)
            {
                return new OpenResult(FileOutcome.IsAFile, Relative(full), false);
            }

            if (filesOnly && isDirectory)
            {
                return new OpenResult(FileOutcome.IsDirectory, Relative(full, isDirectory), true);
            }

            opener(full);
            return new OpenResult(FileOutcome.Ok, Relative(full, isDirectory), isDirectory);
        }
        catch (Exception ex) when (IsFileFailure(ex) || ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new OpenResult(FileOutcome.Failed, Relative(full), false, ex.Message);
        }
    }

    // ---- helpers ----

    private readonly record struct WalkEntry(string FullPath, long Length, DateTime LastWriteTimeUtc, bool IsDirectory, FileAttributes Attributes = 0);

    /// <summary>
    /// The walks' options. Reparse points are no longer skipped here (2026-10-22, review O1: every walk skipped them all, so OneDrive's
    /// placeholders — 41 of 42 entries under this PC's Documents — and deduplicated files were invisible to search, find, tree, info,
    /// copy and zip, and to the .git and database guards): each walk skips a real link alone (<see cref="IsLink"/>) in its predicates.
    /// </summary>
    private static EnumerationOptions WalkOptions(bool recurse) => new()
    {
        RecurseSubdirectories = recurse,
        IgnoreInaccessible = true,
        AttributesToSkip = OperatingSystem.IsWindows() ? FileAttributes.Hidden | FileAttributes.System : 0,
        ReturnSpecialDirectories = false,
    };

    /// <summary>
    /// Whether a walked entry is a real link — a symlink or junction, which a walk never follows or lists — rather than another kind of
    /// reparse point, a cloud placeholder or a deduplicated file, which is the user's file like any other (2026-10-22, review O1). Only a
    /// reparse point is asked where it leads.
    /// </summary>
    private static bool IsLink(ref FileSystemEntry entry) =>
        (entry.Attributes & FileAttributes.ReparsePoint) != 0 && RealLinkTarget(entry.ToFullPath()) is not null;

    /// <summary><see cref="IsLink(ref FileSystemEntry)"/> for an entry already in hand.</summary>
    private static bool IsLink(FileSystemInfo info) => (info.Attributes & FileAttributes.ReparsePoint) != 0 && RealLinkTarget(info.FullName) is not null;

    private const FileAttributes RecallOnOpen = (FileAttributes)0x0004_0000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x0040_0000;

    /// <summary>
    /// Whether a file's data is not on this PC — a cloud placeholder (<c>RECALL_ON_DATA_ACCESS</c>, <c>RECALL_ON_OPEN</c>) or an offline
    /// file (2026-10-22, review O1): it is listed and counted, and a read, copy or zip of that one file fetches it, but a search, a line
    /// count or a picture's header probe never reads it, so no walk downloads gigabytes. <see cref="OfflineOverride"/> stands in for the
    /// attribute in the tests.
    /// </summary>
    internal static bool IsOffline(string path, FileAttributes attributes) =>
        OfflineOverride is { } probe ? probe(path) : (attributes & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0;

    /// <summary>The tests' seam for <see cref="IsOffline"/> (no test can make a cloud placeholder); null in the app.</summary>
    internal static Func<string, bool>? OfflineOverride { get; set; }

    /// <summary>
    /// Whether a walk leaves an entry out as hidden off Windows (2026-10-06, the macOS build). .NET reads every dot-name as
    /// <see cref="FileAttributes.Hidden"/> on Unix, so the Windows skip in <see cref="WalkOptions"/> hid <c>.gitignore</c>,
    /// <c>.github</c> and <c>.env</c> from every listing, search and count and left them out of a folder's copy — on Windows a
    /// dot-name is a name like any other. Off Windows the options skip no Hidden and this skips what Hidden means without the dot:
    /// the macOS hidden flag (<c>chflags hidden</c>), as Windows skips its Hidden attribute; and <c>.git</c>, which Git for
    /// Windows marks Hidden (<c>core.hideDotFiles = dotGitOnly</c>), so a search never reads a repository's objects there
    /// either. Always false on Windows.
    /// </summary>
    private static bool IsHiddenOffWindows(FileAttributes attributes, ReadOnlySpan<char> name) =>
        !OperatingSystem.IsWindows()
        && ((attributes & FileAttributes.Hidden) != 0 && !name.StartsWith('.') || name.Equals(GitFolderName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// One enumeration for every walk: files matching <paramref name="namePattern"/> (a Win32
    /// glob over the name, null = all; a path glob — a separator or <c>**</c> in it — over the
    /// file's path under <paramref name="directory"/> or under the root, <see cref="PathGlob"/>,
    /// 2026-09-17), folders too when asked, never into a reparse point, never below <paramref name="maxDepth"/> levels (1 = the folder's own entries; <c>search_files</c>'s
    /// <c>depth</c>, 2026-09-19). Each entry's size and write time come with it — no second stat.
    /// Brace groups are expanded first (<see cref="PathGlob.ExpandBraces"/>, 2026-09-24) and each
    /// alternative is judged name-or-path on its own, so <c>{src/*.cs,*.md}</c> mixes the two.
    /// </summary>
    /// <summary>
    /// What a read-side walk left out as hidden (2026-10-22, D14), counted as it goes; with <see cref="Max"/> also the walk's budget over every
    /// entry it looks at, matching or not (review G18: find counted its matches alone, so a pattern that matched nothing walked everything),
    /// and the call's token read between entries (Esc inside the walk).
    /// </summary>
    private sealed class HiddenCount
    {
        public int Count;
        public int Seen;
        public int Max = int.MaxValue;
        public CancellationToken Token;

        public void Look()
        {
            Token.ThrowIfCancellationRequested();
            if (++Seen > Max)
            {
                throw new WalkBudgetSpent();
            }
        }
    }

    /// <summary>A walk past its budget (<see cref="HiddenCount.Max"/>): the caller's result is cut.</summary>
    private sealed class WalkBudgetSpent : Exception;

    /// <summary>Whether a walked entry is hidden: Windows' Hidden or System attribute, a Mac's hidden flag and .git (<see cref="IsHiddenOffWindows"/>).</summary>
    private static bool IsHiddenEntry(ref FileSystemEntry entry) =>
        OperatingSystem.IsWindows()
            ? (entry.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0
            : IsHiddenOffWindows(entry.Attributes, entry.FileName);

    private FileSystemEnumerable<WalkEntry> Walk(string directory, string? namePattern, bool recurse, bool includeDirectories = false, int maxDepth = int.MaxValue) =>
        Walk(directory, namePattern, recurse, null, includeDirectories, maxDepth);

    /// <summary>
    /// <see cref="Walk(string, string?, bool, bool, int)"/> counting into <paramref name="hidden"/> the hidden entries it leaves out (D14: a
    /// listing, find, search and recent all leave them out and say how many) — the attributes skip moved into the predicates so they can be
    /// counted — and never a temp sibling (review P7).
    /// </summary>
    private FileSystemEnumerable<WalkEntry> Walk(string directory, string? namePattern, bool recurse, HiddenCount? hidden, bool includeDirectories = false, int maxDepth = int.MaxValue)
    {
        string root = Root;
        GlobAlternative[]? alternatives = namePattern is null
            ? null
            : [.. PathGlob.ExpandBraces(namePattern).Select(p => new GlobAlternative(p, PathGlob.IsPathPattern(p)))];
        var options = WalkOptions(recurse);
        options.AttributesToSkip = 0;
        return new FileSystemEnumerable<WalkEntry>(
            directory,
            (ref FileSystemEntry entry) => new WalkEntry(entry.ToFullPath(), entry.IsDirectory ? 0 : entry.Length, entry.LastWriteTimeUtc.UtcDateTime, entry.IsDirectory, entry.Attributes),
            options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) =>
            {
                hidden?.Look();
                if (IsHiddenEntry(ref entry))
                {
                    if (hidden is not null)
                    {
                        Interlocked.Increment(ref hidden.Count);
                    }

                    return false;
                }

                return (includeDirectories || !entry.IsDirectory)
                    && !IsTempSibling(entry.FileName)
                    && (alternatives is null || entry.IsDirectory || MatchesAny(alternatives, ref entry, directory, root))
                    && !IsLink(ref entry);
            },
            ShouldRecursePredicate = (ref FileSystemEntry entry) => DepthUnder(entry.Directory, directory) < maxDepth && !IsHiddenEntry(ref entry) && !IsLink(ref entry),
        };
    }

    /// <summary>How deep an entry in <paramref name="parent"/> sits under the walked <paramref name="directory"/>: 1 for its own entries, one more per folder between.</summary>
    private static int DepthUnder(ReadOnlySpan<char> parent, string directory)
    {
        if (parent.Length <= directory.Length)
        {
            return 1;
        }

        int segments = 0;
        bool inSegment = false;
        foreach (char c in parent[directory.Length..])
        {
            if (c is '\\' or '/')
            {
                inSegment = false;
            }
            else if (!inSegment)
            {
                inSegment = true;
                segments++;
            }
        }

        return 1 + segments;
    }

    /// <summary>One brace alternative of a walk's pattern, with whether it is a path glob.</summary>
    private readonly record struct GlobAlternative(string Pattern, bool ByPath);

    /// <summary>Whether the entry matches any alternative; its full path is built only if a path glob asks for it.</summary>
    private static bool MatchesAny(GlobAlternative[] alternatives, ref FileSystemEntry entry, string directory, string root)
    {
        string? fullPath = null;
        foreach (var alternative in alternatives)
        {
            if (alternative.ByPath
                ? MatchesPath(alternative.Pattern, fullPath ??= entry.ToFullPath(), directory, root)
                : FileSystemName.MatchesSimpleExpression(alternative.Pattern, entry.FileName, ignoreCase: true))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A path glob against the entry's path under the walked folder, else under the root (so <c>src/**/*.cs</c> works from either).</summary>
    private static bool MatchesPath(string pattern, string fullPath, string directory, string root) =>
        PathGlob.IsMatch(pattern, Path.GetRelativePath(directory, fullPath))
        || (!string.Equals(directory, root, StringComparison.OrdinalIgnoreCase) && PathGlob.IsMatch(pattern, Path.GetRelativePath(root, fullPath)));

    /// <summary>
    /// <paramref name="path"/>, a full path, spelled the way <paramref name="root"/> is (2026-10-06, the first Mac run): on macOS
    /// <c>/var</c>, <c>/tmp</c> and <c>/etc</c> are links into <c>/private</c>, and libgit2, <c>pwd</c> and the like answer the real
    /// path, so <c>/private/tmp/x/a.txt</c> under a root of <c>/tmp/x</c> was refused as outside it — by the git tools (above the
    /// sandbox), the file tools (<see cref="Resolve(string, bool, out string)"/>) and the shell police alike. The same place, only
    /// spelled another way: the mapping never moves a path, so the link rules still judge it after. The root and each folder above it are resolved in turn
    /// (<paramref name="realPath"/>: <see cref="RealPath.Of"/> in the app, a fake in the tests); the first whose real path holds
    /// <paramref name="path"/> has that prefix swapped back for its own spelling, so a repository above the root is named the
    /// user's way too. A path already inside the root by spelling, or under none of them, comes back as it was. Pure but for
    /// <paramref name="realPath"/>.
    /// </summary>
    internal static string InRootSpelling(string root, string path, Func<string, string?> realPath)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(realPath);
        if (WorkingDirectory.IsInside(root, path))
        {
            return path;
        }

        for (string? folder = root; folder is not null; folder = Path.GetDirectoryName(folder))
        {
            if (realPath(folder) is not { Length: > 0 } resolved)
            {
                continue;
            }

            string real = Path.TrimEndingDirectorySeparator(resolved);
            if (!string.Equals(real, folder, StringComparison.Ordinal) && WorkingDirectory.IsInside(real, path))
            {
                return folder + path[real.Length..];
            }
        }

        return path;
    }

    /// <summary><paramref name="path"/> equals <paramref name="root"/> or lies under it, by spelling.</summary>
    internal static bool IsInside(string root, string path)
    {
        // The root's own case rule (2026-10-22, review G16: case-blind everywhere, so ../FILES counted as inside "files" on a
        // case-sensitive volume), and a root that ends in a separator, a drive's or a share's root, holds what starts with it (review I6:
        // E:\ refused every path, its next character being no separator).
        var comparison = CaseRule.Comparison(root);
        if (string.Equals(path, root, comparison))
        {
            return true;
        }

        if (path.Length <= root.Length || !path.StartsWith(root, comparison))
        {
            return false;
        }

        return EndsInSeparator(root) || IsSeparator(path[root.Length]);
    }

    /// <summary>
    /// The part of <paramref name="path"/> under <paramref name="root"/>, no separator first; empty for the root itself (2026-10-22, review
    /// I6: <c>path[(root.Length + 1)..]</c> dropped a character after a root that ends in a separator). <paramref name="path"/> inside
    /// <paramref name="root"/> (<see cref="IsInside"/>).
    /// </summary>
    internal static string Under(string root, string path)
    {
        int cut = root.Length;
        if (cut < path.Length && !EndsInSeparator(root) && IsSeparator(path[cut]))
        {
            cut++;
        }

        return cut >= path.Length ? "" : path[cut..];
    }

    /// <summary>
    /// The links on the way to <paramref name="full"/> asked again just before a write lands (2026-10-22, review G13): <see cref="Resolve(string, bool, out string)"/>
    /// asked them once, and a folder swapped for a junction since — while a file was read, diffed or a copy planned — took the write outside.
    /// What is left is a race inside the one system call itself (documented). With <paramref name="linkItself"/> the last name is never asked
    /// (a delete or move of a link). Throws <see cref="IOException"/>, which every writer already reports.
    /// </summary>
    internal void RecheckLinks(string full, bool linkItself = false)
    {
        string walked = linkItself ? Path.GetDirectoryName(full) ?? full : full;
        if (LinkEscape(Root, walked, RealLinkTarget) is not null)
        {
            throw new IOException(FileText.LinkMovedDetail);
        }
    }

    /// <summary>The separators this system's paths use (2026-10-22, review N3: on a Mac a backslash is a name's character, never one).</summary>
    internal static readonly char[] Separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    private static bool IsSeparator(char c) => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar;

    private static bool EndsInSeparator(string root) => root.Length > 0 && IsSeparator(root[^1]);

    /// <summary>
    /// A folder copy planned before anything is written (2026-10-22: reviews G14, G15, I3, I5, N5 and the fix plan's D14, D16, D17): its files and
    /// folders by an iterative walk (a 15,000-deep tree overflowed the recursion's stack), never through a link, hidden files and dot-names
    /// kept (a copy then delete lost what the walk had skipped), every <c>.git</c> and temp sibling left out, each target judged by the write
    /// guard of <paramref name="to"/> and left out when it refuses (a merge wrote over a database), and a target that is the wrong kind (a
    /// folder where a file goes, or the other way) refusing the whole copy.
    /// </summary>
    private sealed record CopyPlan(List<(string Source, string Target, long Length, bool Exists)> Files, List<string> Folders, List<string> LeftOut, long Bytes, (FileOutcome Outcome, string Detail)? Refusal);

    private static CopyPlan PlanCopy(string source, string destination, WorkingDirectory to, CancellationToken cancellationToken)
    {
        var files = new List<(string, string, long, bool)>();
        var folders = new List<string> { destination };
        var leftOut = new List<string>();
        long bytes = 0;
        var pending = new Stack<(string From, string Into)>();
        pending.Push((source, destination));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (from, into) = pending.Pop();
            foreach (var info in new DirectoryInfo(from).EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0, ReturnSpecialDirectories = false }))
            {
                if (IsLink(info) || IsTempSibling(info.Name))
                {
                    continue;
                }

                string target = Path.Join(into, info.Name);
                bool isFolder = (info.Attributes & FileAttributes.Directory) != 0;
                if (string.Equals(info.Name, GitFolderName, StringComparison.OrdinalIgnoreCase))
                {
                    leftOut.Add(to.Relative(target, isFolder));
                    continue;
                }

                if (WriteGuard.Judge(to.Root, target, WriteRole.Write, to.ShoppingListGuard, to.Databases) is not null)
                {
                    leftOut.Add(to.Relative(target, isFolder));
                    continue;
                }

                if (isFolder)
                {
                    if (File.Exists(target))
                    {
                        return new CopyPlan(files, folders, leftOut, bytes, (FileOutcome.IsAFile, to.Relative(target)));
                    }

                    folders.Add(target);
                    pending.Push((info.FullName, target));
                }
                else
                {
                    if (Directory.Exists(target))
                    {
                        return new CopyPlan(files, folders, leftOut, bytes, (FileOutcome.FolderInTheWay, to.Relative(target, isDirectory: true)));
                    }

                    long length = ((FileInfo)info).Length;
                    files.Add((info.FullName, target, length, File.Exists(target)));
                    bytes += length;
                }
            }
        }

        return new CopyPlan(files, folders, leftOut, bytes, null);
    }

    /// <summary>
    /// A planned copy carried out: the folders made, each file copied — over one already there by the one replace (mechanism 7). A failure
    /// partway takes back the new files and folders it made (2026-10-22, review J7: a half-made folder stood where the copy failed), then
    /// throws; a file it replaced stays replaced.
    /// </summary>
    private static void RunCopy(CopyPlan plan, CancellationToken cancellationToken = default)
    {
        var madeFolders = new List<string>();
        var madeFiles = new List<string>();
        try
        {
            foreach (string folder in plan.Folders)
            {
                if (!Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                    madeFolders.Add(folder);
                }
            }

            foreach (var (source, target, _, exists) in plan.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CopyFile(source, target, exists, preserve: true);
                if (!exists)
                {
                    madeFiles.Add(target);
                }
            }
        }
        catch (Exception ex) when (IsFileFailure(ex) || ex is OperationCanceledException)
        {
            foreach (string file in madeFiles)
            {
                DeleteQuietly(file);
            }

            foreach (string folder in madeFolders.OrderByDescending(f => f.Length))
            {
                try
                {
                    Directory.Delete(folder);
                }
                catch (Exception cleanup) when (IsFileFailure(cleanup))
                {
                    DiagnosticLog.Debug(Category, $"Could not take back {folder}: {cleanup.Message}");
                }
            }

            throw;
        }
    }

    /// <summary>
    /// One file copied: a new one straight, one already there by a temp sibling and the one replace, so it keeps its ACL and attributes and a
    /// read-only one is refused (2026-10-22, review I4, O2, V5: an overwrite and a folder merge were File.Copy over it).
    /// </summary>
    private static void CopyFile(string source, string target, bool exists, bool preserve)
    {
        if (!exists)
        {
            File.Copy(source, target, overwrite: false);
            return;
        }

        string temp = TempSibling(target);
        try
        {
            File.Copy(source, temp);
            MoveIntoPlace(temp, target, preserve);
        }
        finally
        {
            DeleteQuietly(temp);
        }
    }

    /// <summary>
    /// The space a local folder copy may take (2026-10-22, the fix plan's D16: no count cap, the volume's free space less 1 GB): null when it
    /// fits or the volume can't be asked, else the refusal's detail.
    /// </summary>
    internal static string? FreeSpaceRefusal(string folder, long bytes)
    {
        try
        {
            if (Path.GetPathRoot(Path.GetFullPath(folder)) is not { Length: > 0 } volume)
            {
                return null;
            }

            long free = new DriveInfo(volume).AvailableFreeSpace;
            return bytes > free - FreeSpaceReserve ? FileText.NoRoomToCopy(bytes, free) : null;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>What a local copy always leaves free on its volume (D16).</summary>
    public const long FreeSpaceReserve = 1_000_000_000;

    /// <summary>A file set aside under a temp name while a folder takes its place, put back unless <see cref="Commit"/> (review J7).</summary>
    private sealed class Aside : IDisposable
    {
        private readonly string _path;
        private readonly string _held;
        private bool _committed;

        private Aside(string path, string held)
        {
            _path = path;
            _held = held;
        }

        public static Aside Of(string path)
        {
            string held = TempSibling(path);
            File.Move(path, held);
            return new Aside(path, held);
        }

        public void Commit()
        {
            _committed = true;
            DeleteQuietly(_held);
        }

        public void Dispose()
        {
            if (_committed)
            {
                return;
            }

            // Never discarded uncommitted: put back, or, with something in its place, kept under its temp name and said.
            try
            {
                if (!File.Exists(_path) && !Directory.Exists(_path))
                {
                    File.Move(_held, _path);
                    return;
                }
            }
            catch (Exception ex) when (IsFileFailure(ex))
            {
                DiagnosticLog.Warn(Category, $"Could not put {_path} back from {_held}: {ex.Message}");
                return;
            }

            DiagnosticLog.Warn(Category, $"{_path} is taken; the file that was there is kept as {_held}");
        }
    }

    /// <summary>
    /// Where <paramref name="path"/> really leads (2026-10-22, review N5, I3): each existing segment from the volume down asked where it links
    /// (<see cref="RealLinkTarget"/>), up to <see cref="MaxLinkHops"/>; the rest spelled as given. Two folders compared by this are compared by
    /// what they are.
    /// </summary>
    internal static string FinalPath(string path)
    {
        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }

        string? volume = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(volume))
        {
            return full;
        }

        string current = volume;
        var parts = new Queue<string>(full[volume.Length..].Split(Separators, StringSplitOptions.RemoveEmptyEntries));
        int hops = 0;
        while (parts.Count > 0)
        {
            string next = Path.Join(current, parts.Dequeue());
            if (hops < MaxLinkHops && RealLinkTarget(next) is { Length: > 0 } target)
            {
                hops++;
                current = Path.TrimEndingDirectorySeparator(target);
                continue;
            }

            current = next;
        }

        return current;
    }

    /// <summary>
    /// A temp sibling written whole, then moved over the target: a reader never sees half a file. Under
    /// <paramref name="preserve"/> (<see cref="WorkingDirectoryOptions.PreserveOnReplace"/>, a share, 2026-09-30) an existing
    /// target is replaced with <see cref="File.Replace(string, string, string?, bool)"/> (Win32 <c>ReplaceFile</c>), which keeps
    /// its ACL, owner, attributes and streams — a move would give it the temp file's inherited security instead, and reset a
    /// file server's per-file permissions on every edit; a refusal falls back to the move.
    /// </summary>
    internal static long WriteAtomically(string full, byte[] bytes, bool preserve = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        SweepStaleTemps(Path.GetDirectoryName(full)!);
        // A file with other names is written in place (2026-10-22, review I4): a temp and a rename would give this name a new file and
        // leave the others on the old text, silently.
        if (File.Exists(full))
        {
            RefuseReadOnly(full);
            if (FileLinks.Count(full) > 1)
            {
                using var stream = new FileStream(full, FileMode.Truncate, FileAccess.Write, FileShare.None);
                stream.Write(bytes);
                return bytes.LongLength;
            }
        }

        string temp = TempSibling(full);
        try
        {
            File.WriteAllBytes(temp, bytes);
            HideTemp(temp);
            MoveIntoPlace(temp, full, preserve);
        }
        finally
        {
            DeleteQuietly(temp);
        }

        return bytes.LongLength;
    }

    /// <summary>
    /// The written <paramref name="temp"/> in <paramref name="full"/>'s place (<see cref="WriteAtomically"/>'s step, shared with
    /// <see cref="PendingWrite.Commit"/>) — since 2026-10-22 the fix plan's mechanism 7, for every working directory, not a share's alone
    /// (review I4, O2, V5): a read-only target is refused on both systems (a Mac's rename went over a chmod 444 file); on Windows an existing
    /// target is replaced with <c>ReplaceFile</c>, which keeps its ACL, owner, attributes and streams (a move gave it the folder's inherited
    /// security); on a Mac the target's mode goes onto the new file first (a script kept its +x, a 0600 secret stayed 0600).
    /// <paramref name="preserve"/> is kept for the callers; every replace preserves now.
    /// </summary>
    internal static void MoveIntoPlace(string temp, string full, bool preserve, bool unhide = true)
    {
        _ = preserve;
        if (File.Exists(full))
        {
            RefuseReadOnly(full);
            if (OperatingSystem.IsWindows() && TryReplace(temp, full))
            {
                return;
            }

            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    File.SetUnixFileMode(temp, File.GetUnixFileMode(full));
                }
                catch (Exception ex) when (IsFileFailure(ex))
                {
                    DiagnosticLog.Debug(Category, $"Could not carry the mode of {full}: {ex.Message}");
                }
            }
        }

        if (unhide)
        {
            MoveTempOver(temp, full);
        }
        else
        {
            File.Move(temp, full, overwrite: true);
        }
    }

    /// <summary>
    /// A new file written whole and put in place only if nothing is there yet (2026-10-22, review J3): false, nothing written, when a file
    /// appeared at <paramref name="full"/> meanwhile — the move with overwrite off is the one check that can't be raced.
    /// </summary>
    internal static bool WriteNew(string full, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        SweepStaleTemps(Path.GetDirectoryName(full)!);
        string temp = TempSibling(full);
        try
        {
            File.WriteAllBytes(temp, bytes);
            HideTemp(temp);
            return MoveTempNew(temp, full);
        }
        finally
        {
            DeleteQuietly(temp);
        }
    }

    /// <summary>A temp file moved to <paramref name="target"/> only when nothing is there: false when something is (review J3).</summary>
    internal static bool MoveTempNew(string temp, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                File.SetAttributes(temp, File.GetAttributes(temp) & ~FileAttributes.Hidden);
            }
            catch (Exception ex) when (IsFileFailure(ex))
            {
                DiagnosticLog.Debug(Category, $"Could not unhide {temp}: {ex.Message}");
            }
        }

        try
        {
            File.Move(temp, target, overwrite: false);
            return true;
        }
        catch (IOException) when (File.Exists(target) || Directory.Exists(target))
        {
            return false;
        }
    }

    /// <summary>A temp file moved over <paramref name="target"/>, its hidden mark taken off first (a new name keeps the temp's attributes).</summary>
    internal static void MoveTempOver(string temp, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                File.SetAttributes(temp, File.GetAttributes(temp) & ~FileAttributes.Hidden);
            }
            catch (Exception ex) when (IsFileFailure(ex))
            {
                DiagnosticLog.Debug(Category, $"Could not unhide {temp}: {ex.Message}");
            }
        }

        File.Move(temp, target, overwrite: true);
    }

    /// <summary>
    /// Refuses a read-only target (2026-10-22, review O2: a Mac's rename went over a chmod 444 file, Windows' refused): the read-only attribute
    /// on Windows, no owner write on a Mac.
    /// </summary>
    private static void RefuseReadOnly(string full)
    {
        bool readOnly = OperatingSystem.IsWindows()
            ? (File.GetAttributes(full) & FileAttributes.ReadOnly) != 0
            : (File.GetUnixFileMode(full) & UnixFileMode.UserWrite) == 0;
        if (readOnly)
        {
            throw new UnauthorizedAccessException(FileText.ReadOnlyDetail);
        }
    }

    /// <summary><see cref="File.Replace(string, string, string?, bool)"/> of <paramref name="full"/> by <paramref name="temp"/>; false (logged) when the file system refuses it.</summary>
    private static bool TryReplace(string temp, string full)
    {
        try
        {
            File.Replace(temp, full, destinationBackupFileName: null, ignoreMetadataErrors: true);
            return true;
        }
        catch (Exception ex) when (IsFileFailure(ex) || ex is PlatformNotSupportedException)
        {
            DiagnosticLog.Debug(Category, $"Could not replace {full} in place, moving over it: {ex.Message}");
            return false;
        }
    }

    /// <summary>The prefix and suffix of every temp sibling the file tools write (2026-10-22, review P7/K9, <see cref="TempSibling"/>).</summary>
    public const string TempPrefix = ".~";

    /// <summary>The temp siblings' suffix.</summary>
    public const string TempSuffix = ".neon-tmp";

    /// <summary>
    /// A temp sibling's path in <paramref name="full"/>'s folder (2026-10-22, review P7/K9): <c>.~&lt;32 hex&gt;.neon-tmp</c>, 43 characters
    /// whatever the target's name — a name within 37 of the 255 limit could not be written when the temp added 37 to it — and one shape every
    /// walk can leave out (<see cref="IsTempSibling"/>). Hidden on Windows once made (<see cref="HideTemp"/>), the mark taken off at the move.
    /// </summary>
    internal static string TempSibling(string full) => Path.Join(Path.GetDirectoryName(full), TempPrefix + Guid.NewGuid().ToString("N") + TempSuffix);

    /// <summary>How old a temp sibling must be before a write in its folder sweeps it: a crash's, never a write still running.</summary>
    public static readonly TimeSpan StaleTemp = TimeSpan.FromHours(1);

    /// <summary>
    /// The temp siblings in <paramref name="folder"/> older than <see cref="StaleTemp"/> deleted (2026-10-22, review P7: a crash's were never
    /// swept, and were searched, zipped, copied and counted ever after). Each write sweeps its own folder; there is no walk at start. Best effort.
    /// </summary>
    internal static void SweepStaleTemps(string folder)
    {
        try
        {
            foreach (var info in new DirectoryInfo(folder).EnumerateFiles(TempPrefix + "*" + TempSuffix, new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true }))
            {
                if (IsTempSibling(info.Name) && DateTime.UtcNow - info.LastWriteTimeUtc > StaleTemp)
                {
                    DeleteQuietly(info.FullName);
                }
            }
        }
        catch (Exception ex) when (IsFileFailure(ex) || ex is System.Security.SecurityException)
        {
        }
    }

    /// <summary>Whether a file name is a temp sibling's (<see cref="TempSibling"/>): a crash's leftover, never the user's file.</summary>
    public static bool IsTempSibling(ReadOnlySpan<char> name) =>
        name.Length == TempPrefix.Length + 32 + TempSuffix.Length && name.StartsWith(TempPrefix, StringComparison.Ordinal) && name.EndsWith(TempSuffix, StringComparison.Ordinal);

    /// <summary>Marks a new temp sibling hidden on Windows (best effort): Explorer and a listing don't show a write in flight.</summary>
    internal static void HideTemp(string temp)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetAttributes(temp, File.GetAttributes(temp) | FileAttributes.Hidden);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            DiagnosticLog.Debug(Category, $"Could not hide {temp}: {ex.Message}");
        }
    }

    internal static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            DiagnosticLog.Debug(Category, $"Could not remove {path}: {ex.Message}");
        }
    }

    /// <summary>
    /// <see cref="DeleteQuietly"/> for a folder and all in it (code review, 2026-10-11: the model downloads' and the smoke's browser
    /// probes' copies of this made one): gone when it can be, a debug line when not.
    /// </summary>
    internal static void DeleteDirectoryQuietly(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            DiagnosticLog.Debug(Category, $"Could not remove {path}: {ex.Message}");
        }
    }

    /// <summary>
    /// A text file's bytes as text, the mark dropped (the model must never see U+FEFF): since 2026-10-22 in the encoding the file is in
    /// (<see cref="TextFile"/>: UTF-8, UTF-16 or UTF-32 by the mark or the NULs, else a code page that keeps every byte), UTF-8 alone before.
    /// <paramref name="bom"/> says whether it had a mark; a caller that writes the text back takes the codec instead (<see cref="TextFile.Decode"/>).
    /// </summary>
    public static string Decode(byte[] bytes, out bool bom)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        string text = TextFile.Decode(bytes, out var codec);
        bom = codec.HasBom;
        return text;
    }

    /// <summary>
    /// Whether <paramref name="bytes"/> are not text: a NUL in the first <see cref="BinaryProbeBytes"/>, or a PDF's header at the first
    /// byte (2026-10-14, the PDF plan: a PDF with no early NUL was read as its raw syntax; <see cref="PdfFile.LooksLikePdf"/>, offset 0
    /// only, so a text that mentions the header further down stays text).
    /// </summary>
    public static bool LooksBinary(ReadOnlySpan<byte> bytes) => TextFile.LooksBinary(bytes);

    /// <summary>
    /// Whether the file at <paramref name="full"/> would read as text: its first
    /// <see cref="BinaryProbeBytes"/> hold no NUL (<see cref="LooksBinary"/>; an empty file is
    /// text). A file that cannot be opened (locked, gone under the walk) is not.
    /// </summary>
    public static bool IsTextFile(string full)
    {
        ArgumentNullException.ThrowIfNull(full);
        try
        {
            using var stream = RegularFiles.OpenRead(full, FileShare.ReadWrite, BinaryProbeBytes);
            Span<byte> head = stackalloc byte[BinaryProbeBytes];
            int read = stream.ReadAtLeast(head, BinaryProbeBytes, throwOnEndOfStream: false);
            return !LooksBinary(head[..read]);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return false;
        }
    }

    private static bool LooksLikeZip(string path)
    {
        try
        {
            using var stream = RegularFiles.OpenRead(path);
            Span<byte> head = stackalloc byte[2];
            return stream.Read(head) == 2 && head[0] == (byte)'P' && head[1] == (byte)'K';
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return false;
        }
    }

    /// <remarks>
    /// An <see cref="ArgumentOutOfRangeException"/> or <see cref="ArgumentNullException"/> is a bug, not the file system's answer (2026-10-22,
    /// review K18: a slicing mistake read as a file failure, its message handed to the model), and is left to throw.
    /// </remarks>
    internal static bool IsFileFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or NotSupportedException || (ex is ArgumentException && ex is not (ArgumentOutOfRangeException or ArgumentNullException));

    /// <summary>
    /// Lines by <c>\r\n</c>, <c>\n</c> or a lone <c>\r</c>; a trailing newline adds no empty last line. Only those three since 2026-10-22
    /// (review N9, H1, H6): .NET's line enumeration also broke at a form feed, U+0085, U+2028 and U+2029, which <c>patch_file</c>
    /// (<see cref="NormalizeNewlines"/>) never does, so a read showed lines an edit could not match, an edit's region was off by a line per
    /// such break, and a vault note rebuilt from its lines turned them into newlines.
    /// </summary>
    public static List<string> SplitLines(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = new List<string>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is not ('\n' or '\r'))
            {
                continue;
            }

            lines.Add(text[start..i]);
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                i++;
            }

            start = i + 1;
        }

        if (start < text.Length)
        {
            lines.Add(text[start..]);
        }

        return lines;
    }

    /// <summary>Line and whitespace-separated word counts of a text; an empty text has none of either.</summary>
    public static void Count(string text, out int lines, out int words)
    {
        ArgumentNullException.ThrowIfNull(text);
        lines = SplitLines(text).Count;
        words = 0;
        bool inWord = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                inWord = false;
            }
            else if (!inWord)
            {
                inWord = true;
                words++;
            }
        }
    }

    private static string ClipLine(ReadOnlySpan<char> line)
    {
        var trimmed = line.Trim();
        // Never inside a surrogate pair (2026-10-22, review G20).
        return trimmed.Length <= MaxLineChars ? trimmed.ToString() : string.Concat(TextCut.Safe(trimmed, MaxLineChars - 1), "…");
    }

    private DateTimeOffset Local(DateTime utc) =>
        TimeZoneInfo.ConvertTime(new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)), _time.LocalTimeZone);

    private static int CompareEntries(DirectoryEntry a, DirectoryEntry b)
    {
        if (a.IsDirectory != b.IsDirectory)
        {
            return a.IsDirectory ? -1 : 1;
        }

        return StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
    }
}
