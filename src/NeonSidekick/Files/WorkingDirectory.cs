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

    /// <summary>The path is inside <c>.trash</c>, which only <c>delete</c> and <c>restore</c> may write.</summary>
    TrashReadOnly,

    Missing,
    IsDirectory,
    IsAFile,
    NotText,
    NotAnArchive,
    NotInTrash,

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

    /// <summary>A folder at a <c>move</c> / <c>copy</c> / <c>restore</c> destination with <c>overwrite</c> while <c>File safe edits</c> is off (2026-09-20): a folder is replaced only with a copy kept in <c>.trash</c>.</summary>
    FolderInTheWay,

    /// <summary>A <c>delete</c> of <c>.git</c>, of anything in it, or of a folder holding one (2026-09-23, the user's call): git's own store is never deleted.</summary>
    GitProtected,
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

public sealed record ListResult(FileOutcome Outcome, string Relative, IReadOnlyList<DirectoryEntry> Entries, bool Truncated, string Detail = "");

/// <summary>The <c>/tree</c> walk: <paramref name="FullPath"/> is the walked folder, full with a trailing separator (the header line); <paramref name="Truncated"/> when the cap stopped it.</summary>
public sealed record FileTreeResult(FileOutcome Outcome, string Relative, string FullPath, IReadOnlyList<FileTreeEntry> Entries, bool Truncated, string Detail = "");

public sealed record FindResult(FileOutcome Outcome, string Relative, IReadOnlyList<string> Paths, bool Truncated, string Detail = "");

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
}

public sealed record RecentResult(FileOutcome Outcome, string Relative, IReadOnlyList<RecentEntry> Entries, string Detail = "");

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

public sealed record ReadResult(FileOutcome Outcome, string Relative, string Text, int TotalLines, int FromLine, int ToLine, bool Truncated, string Detail = "");

/// <param name="Image">The picture as the model gets it, its <see cref="ImageAttachment.Path"/> the relative one; null unless <paramref name="Outcome"/> is <see cref="FileOutcome.Ok"/>.</param>
public sealed record ImageResult(FileOutcome Outcome, string Relative, ImageAttachment? Image, string Detail = "");

/// <param name="Replaced">A write replaced an existing file; an append created a new one when false.</param>
/// <param name="CopyKept">The previous version went into <c>.trash</c> first (<c>File safe edits</c>, 2026-09-17).</param>
/// <param name="Lines">The file's line count after the write (2026-09-18, so the model needs no <c>file_info</c> to count); null for bytes or a file too big to count.</param>
/// <param name="Words">The file's whitespace-separated word count after the write, the same way.</param>
public sealed record WriteResult(FileOutcome Outcome, string Relative, long Bytes, bool Replaced, bool CopyKept = false, string Detail = "", int? Lines = null, int? Words = null);

/// <summary>
/// What an edit did. <paramref name="Line"/> is the first line touched (the match's line);
/// <paramref name="Count"/> the occurrences replaced (a <c>replace_all</c> over several), or the matches found
/// under <see cref="FileOutcome.EditAmbiguous"/> / <see cref="FileOutcome.ApproximateAll"/>. After a successful edit <paramref name="NewFrom"/>–<paramref name="NewTo"/>
/// is where the new text sits (an empty range — <c>NewTo</c> = <c>NewFrom − 1</c> — for a deletion), <paramref name="TotalLines"/>
/// the file's line count then, and <paramref name="Region"/> the lines around it (<see cref="WorkingDirectory.EditContextLines"/>
/// on each side, at most <see cref="WorkingDirectory.MaxEditRegionLines"/> — empty when the region is over the cap or the edit was
/// a <c>replace_all</c>), <paramref name="RegionFrom"/> the number of its first line. <paramref name="Lines"/> lists the lines of
/// every occurrence a <c>replace_all</c> touched. <paramref name="CopyKept"/>: the previous version went into <c>.trash</c> first.
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
    bool CopyKept = false,
    string Detail = "",
    int Words = 0,
    MatchStrategy Strategy = MatchStrategy.Exact,
    IReadOnlyList<MatchLocation>? Locations = null,
    EscapeDrift Drift = EscapeDrift.None)
{
    public IReadOnlyList<string> Region { get; init; } = Region ?? [];
    public IReadOnlyList<int> Lines { get; init; } = Lines ?? [];
    public IReadOnlyList<MatchLocation> Locations { get; init; } = Locations ?? [];
}

public sealed record CreateResult(FileOutcome Outcome, string Relative, string Detail = "");

/// <param name="Renamed">Only the last path segment changed.</param>
public sealed record MoveResult(FileOutcome Outcome, string From, string To, bool IsDirectory, bool Renamed, string Detail = "", bool CopyKept = false);

/// <param name="TrashPath">The entry's path inside the working directory, under <c>.trash</c>; empty when <paramref name="Destroyed"/>.</param>
/// <param name="Destroyed">A <see cref="WorkingDirectory.Delete"/> in place (<c>File safe edits</c> off, 2026-09-20): nothing kept, nothing to restore.</param>
public sealed record TrashResult(FileOutcome Outcome, string Relative, string TrashPath, bool IsDirectory, string Detail = "", bool CopyKept = false, bool Destroyed = false);

/// <summary>What <see cref="WorkingDirectory.EmptyTrash"/> removed: the files, the folders and their bytes.</summary>
public sealed record EmptyTrashResult(FileOutcome Outcome, int Files, int Folders, long Bytes, string Detail = "");

public sealed record ZipResult(FileOutcome Outcome, string Relative, string Archive, int Entries, long Bytes, string Detail = "");

public sealed record OpenResult(FileOutcome Outcome, string Relative, bool IsDirectory, string Detail = "");

/// <summary>
/// The per-profile working directory: the one folder the file tools may read and write. One
/// instance per app over a <c>Func&lt;string&gt;</c> that reads the live effective setting and
/// profile directory, so a <c>/cwd</c> save or a profile switch changes the root without a rebuild.
///
/// <para>Every operation takes a path relative to the root and resolves it through
/// <see cref="Resolve(string, bool, out string)"/>: <see cref="Path.GetFullPath(string)"/>, then a
/// prefix check against the root. Nothing here follows symlinks or junctions — the walks skip
/// reparse points and the check is on the spelling of the path, which is the documented limit.
/// The <c>.trash</c> folder at the root is where <see cref="Delete"/> puts things: readable when
/// named, hidden from the root listing and every walk, writable by nothing but delete and restore.</para>
///
/// <para>Nothing throws for a model's mistake: every result carries a <see cref="FileOutcome"/>,
/// and an <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> becomes
/// <see cref="FileOutcome.Failed"/> with the message. Nothing here touches the console.</para>
/// </summary>
public sealed class WorkingDirectory
{
    public const string DefaultFolderName = "files";
    public const string TrashFolderName = ".trash";

    /// <summary>The folder <c>delete</c> never touches, nor anything in it or a folder holding it (2026-09-23).</summary>
    public const string GitFolderName = ".git";
    public const string Category = "Files";

    /// <summary>Entries a directory listing shows by default (<c>search_files</c> with no <c>text</c> and no <c>files</c>; <c>/tree</c>'s neighbour).</summary>
    public const int MaxEntries = 200;

    /// <summary>The most entries any no-<c>text</c> shape of <c>search_files</c> lists — the listing, the find and the recent list share it as their <c>limit</c> cap (2026-09-19).</summary>
    public const int MaxListLimit = 200;

    /// <summary>The deepest a nested listing goes (<c>search_files</c>'s <c>depth</c> over the tree shape; <c>list_directory</c>'s until 2026-09-19).</summary>
    public const int MaxTreeDepth = 4;

    /// <summary>Paths a name search lists by default (<c>search_files</c> with <c>files</c> and no <c>text</c>; <c>MaxFindMatches</c> until 2026-09-19).</summary>
    public const int DefaultFindLimit = 100;

    /// <summary>Hits a content search returns by default (<c>MaxSearchMatches</c> until 2026-09-19, when <c>limit</c> came).</summary>
    public const int DefaultSearchLimit = 50;

    /// <summary>The most hits (or files, under <see cref="SearchOutput.Files"/>) a content search returns whatever <c>limit</c> asks.</summary>
    public const int MaxSearchLimit = 200;

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

    /// <param name="root">The resolved root, read on every call (<see cref="Resolve(string, string)"/> over the effective settings).</param>
    /// <param name="time">The clock behind the trash stamp and the dates shown.</param>
    public WorkingDirectory(Func<string> root, TimeProvider time)
    {
        _root = root ?? throw new ArgumentNullException(nameof(root));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

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

    /// <summary>Creates the root when it is missing and returns it. Throws on failure; the tools translate.</summary>
    public string EnsureExists()
    {
        string root = Root;
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>Whether the root exists, without creating it.</summary>
    public bool Exists => Directory.Exists(Root);

    /// <summary>The root's <c>.trash</c> folder, full; it may not exist yet.</summary>
    public string TrashPath => Path.Combine(Root, TrashFolderName);

    /// <summary>
    /// The sandbox: <paramref name="relative"/> against the root, accepted only when it stays
    /// inside. <paramref name="forWrite"/> also refuses the trash. The one place a path is judged.
    /// </summary>
    public FileOutcome Resolve(string relative, bool forWrite, out string full)
    {
        full = "";
        string root = Root;
        string text = (relative ?? "").Trim();
        if (text.Length == 0 || text == ".")
        {
            full = root;
            return FileOutcome.Ok;
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

        if (!IsInside(root, candidate))
        {
            return FileOutcome.OutsideRoot;
        }

        full = candidate;
        if (forWrite && IsInTrash(root, candidate))
        {
            return FileOutcome.TrashReadOnly;
        }

        return FileOutcome.Ok;
    }

    /// <summary>The display form: relative to the root, <c>\</c> separated, a trailing <c>\</c> for a folder, empty for the root itself.</summary>
    public string Relative(string full, bool isDirectory = false)
    {
        string root = Root;
        string trimmed = Path.TrimEndingDirectorySeparator(full);
        if (string.Equals(trimmed, root, StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        string rel = IsInside(root, trimmed) ? trimmed[(root.Length + 1)..] : trimmed;
        return isDirectory ? rel + Path.DirectorySeparatorChar : rel;
    }

    // ---- read side ----

    /// <summary>The folder's own entries, folders first; at most <paramref name="limit"/> (clamped to <see cref="MaxListLimit"/>), <c>Truncated</c> past it.</summary>
    public ListResult List(string relative, int limit = MaxEntries)
    {
        limit = Math.Clamp(limit, 1, MaxListLimit);
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

            bool atRoot = string.Equals(full, Root, StringComparison.OrdinalIgnoreCase);
            var entries = new List<DirectoryEntry>();
            foreach (var info in new DirectoryInfo(full).EnumerateFileSystemInfos())
            {
                bool isDirectory = (info.Attributes & FileAttributes.Directory) != 0;
                if (atRoot && isDirectory && IsTrashName(info.Name))
                {
                    continue;
                }

                entries.Add(new DirectoryEntry(info.Name, isDirectory, isDirectory ? 0 : ((FileInfo)info).Length));
            }

            entries.Sort(CompareEntries);
            bool truncated = entries.Count > limit;
            if (truncated)
            {
                entries.RemoveRange(limit, entries.Count - limit);
            }

            return new ListResult(FileOutcome.Ok, display, entries, truncated);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return new ListResult(FileOutcome.Failed, display, [], false, ex.Message);
        }
    }

    /// <summary>
    /// <c>/tree</c>: every folder and file under <paramref name="relative"/>, depth first, folders
    /// before files at each level, no depth limit; the root's <c>.trash</c> left out unless it is
    /// the folder asked for. Stops at <paramref name="maxEntries"/> (clamped to
    /// <see cref="MinTreeLength"/>..<see cref="MaxTreeLength"/>) with <c>Truncated</c>. <paramref name="maxDepth"/>
    /// (2026-09-18, the nested listing's <c>depth</c>) stops the descent that many levels down; the default
    /// is every level. <paramref name="hideDotEntries"/> (2026-09-22, <c>/vault</c>) leaves out every file and
    /// folder whose name starts with a dot, at any depth — <c>.obsidian</c>, <c>.trash</c>, <c>.git</c>, the
    /// entries the vault tools leave to Obsidian. <paramref name="showHidden"/> (2026-09-23, <c>/tree</c> under
    /// <c>File browser/tree mode</c> <c>show-hidden</c>) lists the entries with the Hidden or System attribute too
    /// (<c>.git</c> on Windows), which every walk leaves out otherwise; reparse points stay out either way.
    /// <paramref name="hideGitFolders"/> (2026-09-30, <c>/tree</c>, the user's ask: "in the same way it ignores .trash") leaves
    /// out every folder named <c>.git</c>, at any depth — a nested repository's or a submodule's too — whatever
    /// <paramref name="showHidden"/> says; like <c>.trash</c>, the folder asked for itself is still walked.
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
        bool atRoot = string.Equals(directory, Root, StringComparison.OrdinalIgnoreCase);
        var children = new List<DirectoryEntry>();
        try
        {
            var options = WalkOptions(recurse: false);
            if (showHidden)
            {
                options.AttributesToSkip = FileAttributes.ReparsePoint;
            }

            foreach (var info in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", options))
            {
                bool isDirectory = (info.Attributes & FileAttributes.Directory) != 0;
                if ((atRoot && isDirectory && IsTrashName(info.Name)) || (hideDotEntries && info.Name.StartsWith('.'))
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

    /// <summary>The files whose names (or paths, under a path glob) match, every level down to <paramref name="maxDepth"/>, at most <paramref name="limit"/> (clamped to <see cref="MaxListLimit"/>).</summary>
    public FindResult Find(string namePattern, string relative, int limit = DefaultFindLimit, int maxDepth = int.MaxValue)
    {
        limit = Math.Clamp(limit, 1, MaxListLimit);
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
            foreach (var entry in Walk(full, pattern, recurse: true, maxDepth: maxDepth))
            {
                if (paths.Count >= limit)
                {
                    truncated = true;
                    break;
                }

                paths.Add(Relative(entry.FullPath));
            }

            paths.Sort(StringComparer.OrdinalIgnoreCase);
            return new FindResult(FileOutcome.Ok, display, paths, truncated);
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
    /// reparse-point entries are skipped, and the root's <c>.trash</c>. A folder outside the root
    /// (<c>..</c>), inside <c>.trash</c>, missing or a file yields nothing with the outcome; the
    /// root is never created here. With <paramref name="files"/> (2026-09-17) a file is listed
    /// only when the predicate keeps its full path — <see cref="IsTextFile"/> for the <c>/speak</c>
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

        if (IsInTrash(Root, full))
        {
            return new MentionResult(FileOutcome.Ok, [], false);
        }

        try
        {
            if (!Directory.Exists(full))
            {
                return new MentionResult(File.Exists(full) ? FileOutcome.IsAFile : FileOutcome.Missing, [], false);
            }

            var entries = new List<DirectoryEntry>();
            bool truncated = false;
            int seen = 0;
            foreach (var entry in Walk(full, null, recurse: prefix.Length > 0, includeDirectories: true))
            {
                if (++seen > MaxMentionVisited)
                {
                    truncated = true;
                    break;
                }

                if (Path.GetFileName(entry.FullPath).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && (entry.IsDirectory || files is null || files(entry.FullPath)))
                {
                    entries.Add(new DirectoryEntry(Relative(entry.FullPath, entry.IsDirectory).Replace('\\', '/'), entry.IsDirectory, 0));
                }
            }

            entries.Sort(CompareEntries);
            if (entries.Count > MaxMentionMatches)
            {
                entries.RemoveRange(MaxMentionMatches, entries.Count - MaxMentionMatches);
                truncated = true;
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
    /// walk stops once <paramref name="limit"/> hits (clamped to <see cref="MaxSearchLimit"/>) are in hand. Cancellation throws.
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
        limit = Math.Clamp(limit, 1, MaxSearchLimit);
        bool countOnly = output == SearchOutput.Files;
        var outcome = Resolve(relative, forWrite: false, out string full);
        if (outcome != FileOutcome.Ok)
        {
            return Empty(outcome, relative);
        }

        string display = Relative(full, isDirectory: true);
        string needle = (text ?? "").Trim();
        if (needle.Length == 0)
        {
            return Empty(FileOutcome.Empty, display);
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

            IEnumerable<WalkEntry> entries = singleFile
                ? [new WalkEntry(full, new FileInfo(full).Length, File.GetLastWriteTimeUtc(full), false)]
                : Walk(full, pattern, recurse: true, maxDepth: maxDepth);
            var hits = new ConcurrentBag<SearchHit>();
            var counts = new ConcurrentBag<SearchFileCount>();
            int matches = 0, searched = 0, matchedFiles = 0, timedOut = 0;
            var options = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = cancellationToken };
            Parallel.ForEach(entries, options, (entry, state) =>
            {
                if (entry.Length > MaxTextFileBytes)
                {
                    return;
                }

                byte[] bytes;
                try
                {
                    bytes = File.ReadAllBytes(entry.FullPath);
                }
                catch (Exception ex) when (IsFileFailure(ex))
                {
                    return;
                }

                if (LooksBinary(bytes))
                {
                    return;
                }

                Interlocked.Increment(ref searched);
                string content = Decode(bytes, out _);
                string relativePath = Relative(entry.FullPath);
                bool fileMatched = false;
                int line = 0, inFile = 0;
                List<string>? all = context > 0 && !countOnly ? SplitLines(content) : null;
                foreach (var span in content.AsSpan().EnumerateLines())
                {
                    line++;
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
                            continue;
                        }
                    }

                    if (!hit)
                    {
                        continue;
                    }

                    fileMatched = true;
                    if (countOnly)
                    {
                        // The file's own count; the cap is on files, checked once the file is done.
                        inFile++;
                        Interlocked.Increment(ref matches);
                        continue;
                    }

                    if (Interlocked.Increment(ref matches) > limit)
                    {
                        state.Stop();
                        break;
                    }

                    if (all is null)
                    {
                        hits.Add(new SearchHit(relativePath, line, ClipLine(span)));
                        continue;
                    }

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

                    hits.Add(new SearchHit(relativePath, line, ClipLine(span), before, after));
                }

                if (fileMatched)
                {
                    int nth = Interlocked.Increment(ref matchedFiles);
                    if (countOnly)
                    {
                        if (nth > limit)
                        {
                            state.Stop();
                            return;
                        }

                        counts.Add(new SearchFileCount(relativePath, inFile));
                    }
                }
            });

            if (countOnly)
            {
                var files = counts.ToList();
                files.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath));
                bool cut = files.Count > limit || Volatile.Read(ref matchedFiles) > limit;
                if (files.Count > limit)
                {
                    files.RemoveRange(limit, files.Count - limit);
                }

                return new SearchResult(FileOutcome.Ok, display, [], searched, Math.Min(matchedFiles, limit), timedOut, cut, _time.GetElapsedTime(started), SingleFile: singleFile, Files: files);
            }

            var sorted = hits.ToList();
            sorted.Sort(CompareHits);
            bool truncated = sorted.Count > limit || Volatile.Read(ref matches) > limit;
            if (sorted.Count > limit)
            {
                sorted.RemoveRange(limit, sorted.Count - limit);
            }

            return new SearchResult(FileOutcome.Ok, display, sorted, searched, matchedFiles, timedOut, truncated, _time.GetElapsedTime(started), SingleFile: singleFile);
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

    /// <summary>The <paramref name="count"/> most recently written files under the path (down to <paramref name="maxDepth"/> levels), newest first: one pass, a bounded top-N, at most <see cref="MaxListLimit"/>.</summary>
    public RecentResult Recent(string relative, int count, int maxDepth = int.MaxValue)
    {
        var outcome = Resolve(relative, forWrite: false, out string full);
        if (outcome != FileOutcome.Ok)
        {
            return new RecentResult(outcome, relative, []);
        }

        string display = Relative(full, isDirectory: true);
        int limit = Math.Clamp(count, 1, MaxListLimit);
        try
        {
            EnsureExists();
            if (!Directory.Exists(full))
            {
                return new RecentResult(File.Exists(full) ? FileOutcome.IsAFile : FileOutcome.Missing, display, []);
            }

            var top = new List<WalkEntry>(limit + 1);
            foreach (var entry in Walk(full, "*", recurse: true, maxDepth: maxDepth))
            {
                if (top.Count == limit && entry.LastWriteTimeUtc <= top[^1].LastWriteTimeUtc)
                {
                    continue;
                }

                int at = top.FindIndex(e => e.LastWriteTimeUtc < entry.LastWriteTimeUtc);
                top.Insert(at < 0 ? top.Count : at, entry);
                if (top.Count > limit)
                {
                    top.RemoveAt(top.Count - 1);
                }
            }

            var entries = top.Select(e => new RecentEntry(Relative(e.FullPath), Local(e.LastWriteTimeUtc), e.Length)).ToList();
            return new RecentResult(FileOutcome.Ok, display, entries);
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
                if (info.Length <= MaxTextFileBytes)
                {
                    byte[] content = File.ReadAllBytes(full);
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
    /// A text file, or a window of it: <paramref name="startLine"/> is 1-based, a negative one
    /// counts from the end (<c>-20</c> = the last 20 lines); <paramref name="maxLines"/> null = all
    /// that fit in <see cref="MaxReadChars"/>.
    /// </summary>
    public ReadResult ReadText(string relative, int? startLine, int? maxLines)
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

            if (new FileInfo(full).Length > MaxReadFileBytes)
            {
                return Fail(FileOutcome.TooBig, display);
            }

            byte[] bytes = File.ReadAllBytes(full);
            if (LooksBinary(bytes))
            {
                return Fail(FileOutcome.NotText, display);
            }

            var lines = SplitLines(Decode(bytes, out _));
            int total = lines.Count;
            int from = startLine switch
            {
                null or 0 => 1,
                < 0 => Math.Max(1, total + startLine.Value + 1),
                _ => startLine.Value,
            };
            if (from > total)
            {
                return new ReadResult(FileOutcome.Ok, display, "", total, total == 0 ? 0 : from, total == 0 ? 0 : from - 1, false);
            }

            int want = maxLines is > 0 ? maxLines.Value : total - from + 1;
            int to = Math.Min(total, from + want - 1);
            var sb = new StringBuilder();
            bool truncated = false;
            int shown = from - 1;
            for (int i = from - 1; i < to; i++)
            {
                if (sb.Length + lines[i].Length + 1 > MaxReadChars && sb.Length > 0)
                {
                    truncated = true;
                    break;
                }

                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }

                sb.Append(lines[i].Length > MaxReadChars ? lines[i][..MaxReadChars] : lines[i]);
                shown = i + 1;
            }

            return new ReadResult(FileOutcome.Ok, display, sb.ToString(), total, from, shown, truncated);
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
    /// bytes decide, as for a dropped file — and <c>.trash</c> is readable like any folder. A file the
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

            if (ImageFile.TryLoad(full, out var image, out var failure))
            {
                return new ImageResult(FileOutcome.Ok, display, image! with { Path = display });
            }

            return new ImageResult(failure switch
            {
                ImageLoadFailure.NotFound => FileOutcome.Missing,
                ImageLoadFailure.TooLarge => FileOutcome.ImageTooBig,
                _ => FileOutcome.NotAnImage,
            }, display, null);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return new ImageResult(FileOutcome.Failed, display, null, ex.Message);
        }
    }

    // ---- write side ----

    /// <param name="keepCopy">Copy the file being replaced into <c>.trash</c> first (<c>File safe edits</c>); a file over <see cref="MaxTextFileBytes"/> is not copied.</param>
    public WriteResult WriteText(string relative, string text, bool overwrite, bool keepCopy = false)
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

            bool copied = existed && keepCopy && CopyToTrash(full, display);
            long bytes = WriteAtomically(full, Utf8NoBom.GetBytes(text));
            Count(text, out int lines, out int words);
            return new WriteResult(FileOutcome.Ok, display, bytes, existed, copied, Lines: lines, Words: words);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return new WriteResult(FileOutcome.Failed, display, 0, false, Detail: ex.Message);
        }
    }

    /// <summary>
    /// <see cref="WriteText"/> for bytes as they are (a download, 2026-09-18): the same guards —
    /// the sandbox, a folder in the way, a file there unless <paramref name="overwrite"/>, the copy
    /// into <c>.trash</c> under <paramref name="keepCopy"/> (skipped over <see cref="MaxTextFileBytes"/>,
    /// as ever) — and the same atomic write; no length cap (the caller's is the download's).
    /// </summary>
    public WriteResult WriteBytes(string relative, byte[] bytes, bool overwrite, bool keepCopy = false)
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

            bool copied = existed && keepCopy && CopyToTrash(full, display);
            long written = WriteAtomically(full, bytes);
            return new WriteResult(FileOutcome.Ok, display, written, existed, copied);
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
    /// <param name="keepCopy">Copy the file into <c>.trash</c> first when it exists (<c>File safe edits</c>).</param>
    public WriteResult AppendText(string relative, string text, bool keepCopy = false)
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

            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            bool existed = File.Exists(full);
            bool copied = existed && keepCopy && CopyToTrash(full, display);
            using var stream = new FileStream(full, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            long written = 0;
            if (stream.Length > 0)
            {
                stream.Seek(-1, SeekOrigin.End);
                int last = stream.ReadByte();
                if (last != '\n')
                {
                    stream.WriteByte((byte)'\n');
                    written++;
                }
            }
            else
            {
                stream.Seek(0, SeekOrigin.End);
            }

            byte[] bytes = Utf8NoBom.GetBytes(text);
            stream.Write(bytes);
            written += bytes.Length;

            // The whole file's counts after the append (2026-09-18); a file too big to read as text reports none.
            int? lines = null, words = null;
            if (stream.Length <= MaxTextFileBytes)
            {
                stream.Seek(0, SeekOrigin.Begin);
                byte[] whole = new byte[stream.Length];
                stream.ReadExactly(whole);
                Count(Decode(whole, out _), out int l, out int w);
                lines = l;
                words = w;
            }

            return new WriteResult(FileOutcome.Ok, display, written, existed, copied, Lines: lines, Words: words);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return new WriteResult(FileOutcome.Failed, display, 0, false, Detail: ex.Message);
        }
    }

    /// <summary>
    /// Replaces one occurrence of <paramref name="oldText"/> — exactly one unless
    /// <paramref name="replaceAll"/>, then every one — found by <see cref="FuzzyMatch.Replace"/>'s
    /// chain (2026-09-19: as written first, then with spacing, indentation, escapes and typographic
    /// characters tolerated, last by similarity; the result names the strategy). The match runs over the
    /// text with its line breaks normalised to <c>\n</c> (the form <see cref="ReadText"/> shows, so a
    /// multi-line <c>old_text</c> copied from a read matches a CRLF file too, 2026-09-17), and the file is
    /// written back with the line ending it had (<see cref="DetectLineEnding"/>: the first break
    /// decides, so a mixed file comes out uniform); a BOM is kept. <paramref name="keepCopy"/> puts
    /// the previous version into <c>.trash</c> first. An edit already in the file is
    /// <see cref="FileOutcome.AlreadyApplied"/>, nothing written.
    /// </summary>
    public EditResult EditText(string relative, string oldText, string newText, bool replaceAll = false, bool keepCopy = false)
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
            var loaded = LoadForEdit(full, display, out string content, out bool bom, out string ending);
            if (loaded is not null)
            {
                return loaded;
            }

            var match = FuzzyMatch.Replace(content, needle, replacement, replaceAll);
            switch (match.Outcome)
            {
                case MatchOutcome.Empty:
                    return new EditResult(FileOutcome.Empty, display, 0, 0);
                case MatchOutcome.Same:
                    return new EditResult(FileOutcome.Same, display, 0, 0);
                case MatchOutcome.NotFound:
                    return new EditResult(FileOutcome.EditNotFound, display, 0, 0);
                case MatchOutcome.AlreadyApplied:
                    return new EditResult(FileOutcome.AlreadyApplied, display, 0, 0);
                case MatchOutcome.Ambiguous:
                    return new EditResult(FileOutcome.EditAmbiguous, display, 0, match.Count, Strategy: match.Strategy, Locations: match.Locations);
                case MatchOutcome.ApproximateAll:
                    return new EditResult(FileOutcome.ApproximateAll, display, 0, match.Count, Strategy: match.Strategy, Locations: match.Locations);
                case MatchOutcome.EscapeDrift:
                    return new EditResult(FileOutcome.EscapeDrift, display, 0, match.Count, Strategy: match.Strategy, Drift: match.Drift);
            }

            string edited = match.Content;
            bool kept = keepCopy && CopyToTrash(full, display);
            Save(full, edited, bom, ending);

            if (match.Count > 1)
            {
                var lines = match.Spans.Select(s => 1 + content.AsSpan(0, s.Start).Count('\n')).ToList();
                Count(edited, out int totalLines, out int words);
                return new EditResult(FileOutcome.Ok, display, lines[0], match.Count, 0, 0, totalLines, null, 0, lines, kept, Words: words, Strategy: match.Strategy);
            }

            // The new text's lines: from the placed replacement's first line through the last it reaches.
            var placed = match.Placed[0];
            int newFrom = 1 + edited.AsSpan(0, placed.Start).Count('\n');
            int newTo = newFrom + edited.AsSpan(placed.Start, placed.Length).Count('\n');
            if (placed.Length == 0)
            {
                newTo = newFrom - 1;
            }

            return Done(display, edited, newFrom, newTo, 1, kept, match.Strategy);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return new EditResult(FileOutcome.Failed, display, 0, 0, Detail: ex.Message);
        }
    }

    /// <summary>The checks every edit makes before it reads the file: a folder, missing, too big, binary; null = go on, the content LF-normalised.</summary>
    private EditResult? LoadForEdit(string full, string display, out string content, out bool bom, out string ending)
    {
        content = "";
        bom = false;
        ending = "\n";
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

        byte[] bytes = File.ReadAllBytes(full);
        if (LooksBinary(bytes))
        {
            return new EditResult(FileOutcome.NotText, display, 0, 0);
        }

        string raw = Decode(bytes, out bom);
        ending = DetectLineEnding(raw);
        content = NormalizeNewlines(raw);
        return null;
    }

    /// <summary>Writes an LF-normalised text back with the file's ending and its BOM.</summary>
    private static void Save(string full, string normalised, bool bom, string ending)
    {
        byte[] output = Utf8NoBom.GetBytes(ending == "\n" ? normalised : normalised.Replace("\n", ending, StringComparison.Ordinal));
        if (bom)
        {
            output = [0xEF, 0xBB, 0xBF, .. output];
        }

        WriteAtomically(full, output);
    }

    /// <summary>The successful edit's result with the region around the new text (<see cref="EditContextLines"/> each side, none over <see cref="MaxEditRegionLines"/>).</summary>
    private static EditResult Done(string display, string edited, int newFrom, int newTo, int count, bool kept, MatchStrategy strategy = MatchStrategy.Exact)
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
        return new EditResult(FileOutcome.Ok, display, newFrom, count, newFrom, newTo, total, region, region is null ? 0 : from, null, kept, Words: words, Strategy: strategy);
    }

    /// <summary>Every <c>\r\n</c> and lone <c>\r</c> as <c>\n</c>: the one form the edits match and splice in.</summary>
    public static string NormalizeNewlines(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Contains('\r') ? text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n') : text;
    }

    /// <summary>The line ending the file is written back with: what its first line break is (<c>\n</c> when it has none).</summary>
    public static string DetectLineEnding(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int at = text.IndexOfAny(['\r', '\n']);
        if (at < 0 || text[at] == '\n')
        {
            return "\n";
        }

        return at + 1 < text.Length && text[at + 1] == '\n' ? "\r\n" : "\r";
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

            Directory.CreateDirectory(full);
            return new CreateResult(FileOutcome.Ok, display);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return new CreateResult(FileOutcome.Failed, display, ex.Message);
        }
    }

    /// <summary>
    /// Moves or renames a file or a folder. With <paramref name="overwrite"/>, what is in the way goes
    /// to <c>.trash</c> first under <paramref name="keepCopy"/> (<c>File safe edits</c>, 2026-09-20); without
    /// it a file in the way is replaced in place and a folder in the way is <see cref="FileOutcome.FolderInTheWay"/>.
    /// </summary>
    public MoveResult Move(string from, string to, bool overwrite, bool keepCopy = false) => Transfer(from, to, overwrite, move: true, keepCopy);

    /// <summary>
    /// Copies a file or a folder (recursively, reparse points skipped). With <paramref name="overwrite"/>
    /// the rule is <see cref="Move"/>'s — but a folder copied over a folder merges into it, under either
    /// setting (nothing is destroyed by a merge).
    /// </summary>
    public MoveResult Copy(string from, string to, bool overwrite, bool keepCopy = false) => Transfer(from, to, overwrite, move: false, keepCopy);

    private MoveResult Transfer(string from, string to, bool overwrite, bool move, bool keepCopy)
    {
        var outcome = Resolve(from, forWrite: move, out string source);
        if (outcome != FileOutcome.Ok)
        {
            return new MoveResult(outcome, from, to, false, false);
        }

        outcome = Resolve(to, forWrite: true, out string destination);
        if (outcome != FileOutcome.Ok)
        {
            return new MoveResult(outcome, Relative(source), to, false, false);
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

            if (isDirectory && IsInside(source, destination))
            {
                return new MoveResult(FileOutcome.IntoItself, fromDisplay, toDisplay, isDirectory, false);
            }

            bool caseOnly = string.Equals(source, destination, StringComparison.OrdinalIgnoreCase);
            bool inTheWay = !caseOnly && (File.Exists(destination) || Directory.Exists(destination));
            if (inTheWay && !overwrite)
            {
                return new MoveResult(FileOutcome.Exists, fromDisplay, toDisplay, isDirectory, false);
            }

            // What is in the way (2026-09-20): kept in .trash under File safe edits; else a file is
            // replaced in place and a folder refused — except a folder a folder is copied over, which merges.
            bool folderInTheWay = inTheWay && Directory.Exists(destination);
            bool merge = folderInTheWay && isDirectory && !move;
            bool kept = false;
            if (inTheWay && !merge)
            {
                if (keepCopy)
                {
                    MoveToTrash(destination, Relative(destination));
                    kept = true;
                }
                else if (folderInTheWay)
                {
                    return new MoveResult(FileOutcome.FolderInTheWay, fromDisplay, Relative(destination, isDirectory: true), isDirectory, false);
                }
                else if (isDirectory)
                {
                    File.Delete(destination);
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            bool renamed = string.Equals(Path.GetDirectoryName(source), Path.GetDirectoryName(destination), StringComparison.OrdinalIgnoreCase);
            if (isDirectory)
            {
                if (move)
                {
                    Directory.Move(source, destination);
                }
                else
                {
                    CopyDirectory(source, destination);
                }
            }
            else if (move)
            {
                File.Move(source, destination, overwrite: true);
            }
            else
            {
                File.Copy(source, destination, overwrite: true);
            }

            return new MoveResult(FileOutcome.Ok, fromDisplay, toDisplay, isDirectory, renamed, CopyKept: kept);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return new MoveResult(FileOutcome.Failed, Relative(source), Relative(destination), false, false, ex.Message);
        }
    }

    /// <summary>
    /// Moves a file or folder into <c>.trash\&lt;stamp&gt;\&lt;relative&gt;</c>: a same-volume move, nothing destroyed —
    /// while <paramref name="toTrash"/> (<c>File safe edits</c>). Without it (2026-09-20, the user's call) the entry is
    /// removed in place, a folder with everything in it: the one recursive delete in the sandbox, behind the setting.
    /// Either way the root and anything under <c>.trash</c> are refused (<c>/emptytrash</c> alone clears the trash), and so
    /// (2026-09-23, the user's call) are <c>.git</c>, anything in it and a folder with a <c>.git</c> anywhere under it —
    /// <see cref="FileOutcome.GitProtected"/>, whichever the setting: a repository's history is not the model's to lose.
    /// </summary>
    public TrashResult Delete(string relative, bool toTrash = true)
    {
        var outcome = Resolve(relative, forWrite: true, out string full);
        if (outcome != FileOutcome.Ok)
        {
            return new TrashResult(outcome, relative, "", false);
        }

        try
        {
            EnsureExists();
            bool isDirectory = Directory.Exists(full);
            if (!isDirectory && !File.Exists(full))
            {
                return new TrashResult(FileOutcome.Missing, Relative(full), "", false);
            }

            string display = Relative(full, isDirectory);
            if (string.Equals(full, Root, StringComparison.OrdinalIgnoreCase))
            {
                return new TrashResult(FileOutcome.IntoItself, display, "", true);
            }

            if (IsGitPath(Relative(full)) || (isDirectory && HoldsGit(full)))
            {
                return new TrashResult(FileOutcome.GitProtected, display, "", isDirectory);
            }

            if (!toTrash)
            {
                if (isDirectory)
                {
                    Directory.Delete(full, recursive: true);
                }
                else
                {
                    File.Delete(full);
                }

                DiagnosticLog.Debug(Category, DeletedLogLine(Relative(full)));
                return new TrashResult(FileOutcome.Ok, display, "", isDirectory, Destroyed: true);
            }

            string trashed = MoveToTrash(full, Relative(full));
            return new TrashResult(FileOutcome.Ok, display, Relative(trashed, isDirectory), isDirectory);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return new TrashResult(FileOutcome.Failed, Relative(full), "", false, ex.Message);
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
    /// Whether a <c>.git</c> is anywhere under <paramref name="directory"/> — a folder, or the file a worktree or a
    /// submodule keeps in its place — never through a reparse point; the first hit ends the walk. An unreadable
    /// subfolder is passed over (the delete that follows fails on it anyway).
    /// </summary>
    private static bool HoldsGit(string directory) =>
        Directory.EnumerateFileSystemEntries(directory, GitFolderName, new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            MatchCasing = MatchCasing.CaseInsensitive,
            ReturnSpecialDirectories = false,
        }).Any();

    /// <summary>The move behind <see cref="Delete"/> and an overwritten folder; returns the full path inside the trash.</summary>
    private string MoveToTrash(string full, string relative)
    {
        string candidate = NextCopy(relative);
        if (Directory.Exists(full))
        {
            Directory.Move(full, candidate);
        }
        else
        {
            File.Move(full, candidate);
        }

        DiagnosticLog.Debug(Category, TrashedLogLine(relative, Relative(candidate)));
        return candidate;
    }

    /// <summary><c>Trashed notes.md as .trash\20260919-140500\notes.md</c>. Pinned.</summary>
    public static string TrashedLogLine(string relative, string copy) => $"Trashed {relative} as {copy}";

    /// <summary>The in-place <see cref="Delete"/>: <c>Deleted notes.md in place (File safe edits off)</c>. Pinned.</summary>
    public static string DeletedLogLine(string relative) => $"Deleted {relative} in place (File safe edits off)";

    /// <summary>The <c>File safe edits</c> copy: <c>Kept the previous notes.md as .trash\…\notes.md</c>. Pinned.</summary>
    public static string KeptLogLine(string relative, string copy) => $"Kept the previous {relative} as {copy}";

    /// <summary><c>Restored notes.md from .trash\20260919-140500\</c>. Pinned.</summary>
    public static string RestoredLogLine(string relative, string stamp) => $"Restored {relative} from {stamp}";

    /// <summary><c>Trash emptied: 12 files, 3 folders, 340,000 bytes</c>. Pinned.</summary>
    public static string TrashEmptiedLogLine(int files, int folders, long bytes) =>
        string.Create(CultureInfo.InvariantCulture, $"Trash emptied: {files} files, {folders} folders, {bytes:N0} bytes");

    /// <summary>The entry at <paramref name="target"/> or its highest <c> (n)</c> sibling, whichever exists with the biggest n; null with none.</summary>
    private static string? NewestCopy(string target)
    {
        string? parent = Path.GetDirectoryName(target);
        if (parent is null || !Directory.Exists(parent))
        {
            return null;
        }

        string name = Path.GetFileName(target);
        string? best = File.Exists(target) || Directory.Exists(target) ? target : null;
        int bestN = 1;
        foreach (string entry in Directory.EnumerateFileSystemEntries(parent, name + " (*)"))
        {
            string tail = Path.GetFileName(entry).AsSpan(name.Length).ToString();
            if (tail.Length > 3 && tail.StartsWith(" (", StringComparison.Ordinal) && tail.EndsWith(')')
                && int.TryParse(tail.AsSpan(2, tail.Length - 3), NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > bestN)
            {
                best = entry;
                bestN = n;
            }
        }

        return best;
    }

    /// <summary>
    /// A copy of a file about to be changed into <c>.trash\&lt;stamp&gt;\&lt;relative&gt;</c> (<c>File safe edits</c>,
    /// 2026-09-17), the same stamp and <c>(n)</c> rule as <see cref="MoveToTrash"/>; false when the file is over
    /// <see cref="MaxTextFileBytes"/> (never copied). A failure to copy is the edit's failure.
    /// </summary>
    private bool CopyToTrash(string full, string relative)
    {
        if (new FileInfo(full).Length > MaxTextFileBytes)
        {
            return false;
        }

        string copy = NextCopy(relative);
        File.Copy(full, copy);
        DiagnosticLog.Debug(Category, KeptLogLine(relative, Relative(copy)));
        return true;
    }

    /// <summary>
    /// Where the next copy of <paramref name="relative"/> lands in this second's stamp: the plain
    /// name, else one past the highest <c> (n)</c> there — never a gap left by a restore, so the
    /// numbers stay in the order the copies were taken (2026-09-17). The stamp folder is created.
    /// </summary>
    private string NextCopy(string relative)
    {
        string stamp = Local(_time.GetUtcNow().UtcDateTime).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string target = Path.Combine(Root, TrashFolderName, stamp, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string? newest = NewestCopy(target);
        if (newest is null)
        {
            return target;
        }

        int n = newest.Length == target.Length ? 1 : int.Parse(newest.AsSpan(target.Length + 2, newest.Length - target.Length - 3), NumberStyles.None, CultureInfo.InvariantCulture);
        return target + " (" + (n + 1).ToString(CultureInfo.InvariantCulture) + ")";
    }

    /// <summary>
    /// Puts back the newest trashed copy of <paramref name="relative"/>; refuses when something is at that
    /// path again unless <paramref name="overwrite"/>: under <paramref name="keepCopy"/> (<c>File safe edits</c>) what is there is trashed first (the way an edit
    /// is undone, 2026-09-17) — the copy is found BEFORE that move, or the just-trashed entry would be the newest and come straight back —
    /// without it a live file is replaced in place and a live folder is <see cref="FileOutcome.FolderInTheWay"/> (2026-09-20).
    /// </summary>
    public TrashResult Restore(string relative, bool overwrite = false, bool keepCopy = false)
    {
        var outcome = Resolve(relative, forWrite: true, out string full);
        if (outcome != FileOutcome.Ok)
        {
            return new TrashResult(outcome, relative, "", false);
        }

        string display = Relative(full);
        try
        {
            EnsureExists();
            bool occupied = File.Exists(full) || Directory.Exists(full);
            if (occupied && !overwrite)
            {
                return new TrashResult(FileOutcome.Exists, Relative(full, Directory.Exists(full)), "", Directory.Exists(full));
            }

            string trash = Path.Combine(Root, TrashFolderName);
            string sub = Relative(full);
            if (!Directory.Exists(trash))
            {
                return new TrashResult(FileOutcome.NotInTrash, display, "", false);
            }

            var stamps = Directory.GetDirectories(trash).Select(Path.GetFileName).OfType<string>().ToList();
            stamps.Sort(StringComparer.Ordinal);
            stamps.Reverse();
            foreach (var stamp in stamps)
            {
                string stampPath = Path.Combine(trash, stamp);
                // The plain name, then " (2)", " (3)"…: the highest suffix is the latest of the
                // copies that landed in this stamp, and the stamps are visited newest first. A
                // restore leaves a gap in the run, so the highest that exists wins, not the last
                // of a contiguous run (2026-09-17).
                string? found = NewestCopy(Path.Combine(stampPath, sub));
                if (found is null)
                {
                    continue;
                }

                bool isDirectory = Directory.Exists(found);
                bool kept = false;
                if (occupied)
                {
                    // The live entry (2026-09-20): kept in .trash under File safe edits; else a file is replaced in place, a folder refused.
                    if (keepCopy)
                    {
                        MoveToTrash(full, sub);
                        kept = true;
                    }
                    else if (Directory.Exists(full))
                    {
                        return new TrashResult(FileOutcome.FolderInTheWay, Relative(full, isDirectory: true), "", true);
                    }
                    else
                    {
                        File.Delete(full);
                    }
                }

                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                if (isDirectory)
                {
                    Directory.Move(found, full);
                }
                else
                {
                    File.Move(found, full);
                }

                if (!Directory.EnumerateFileSystemEntries(stampPath, "*", SearchOption.AllDirectories).Any(File.Exists))
                {
                    Directory.Delete(stampPath, recursive: true);
                }

                DiagnosticLog.Debug(Category, RestoredLogLine(Relative(full, isDirectory), Relative(stampPath, isDirectory: true)));
                return new TrashResult(FileOutcome.Ok, Relative(full, isDirectory), Relative(stampPath, isDirectory: true), isDirectory, CopyKept: kept);
            }

            return new TrashResult(FileOutcome.NotInTrash, display, "", false);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return new TrashResult(FileOutcome.Failed, display, "", false, ex.Message);
        }
    }

    /// <summary>
    /// Deletes everything under the root's <c>.trash</c> for good; the folder itself stays. The
    /// one destructive operation here: reached only by <c>/emptytrash</c> after a typed
    /// confirmation, never by a tool. Junctions and symlinks inside the trash are removed as
    /// links, not followed (the count and the delete agree on that). A read-only file that was
    /// moved in is made writable first, so it cannot stop the run. No trash folder = nothing to do.
    /// </summary>
    public EmptyTrashResult EmptyTrash()
    {
        var result = EmptyContents(TrashPath);
        if (result.Outcome == FileOutcome.Ok && Directory.Exists(TrashPath))
        {
            DiagnosticLog.Info(Category, TrashEmptiedLogLine(result.Files, result.Folders, result.Bytes));
        }

        return result;
    }

    /// <summary>
    /// Deletes everything under <paramref name="relative"/> for good; the folder itself stays (2026-09-24, the user's
    /// ask: <c>/comfy purge</c> empties the ComfyUI output folder, its <c>.pasted</c> inputs included). The second
    /// destructive operation here, after <see cref="EmptyTrash"/>, and like it reached only by a slash command after a
    /// typed confirmation, never by a tool. Refused outside the sandbox, inside the trash, on the root itself (an
    /// empty output folder setting means the working directory — purging that would take everything) and on a file.
    /// A missing folder is nothing to do.
    /// </summary>
    public EmptyTrashResult PurgeFolder(string relative)
    {
        var outcome = Resolve(relative, forWrite: true, out string full);
        if (outcome != FileOutcome.Ok)
        {
            return new EmptyTrashResult(outcome, 0, 0, 0);
        }

        if (string.Equals(full, Root, StringComparison.OrdinalIgnoreCase))
        {
            return new EmptyTrashResult(FileOutcome.OutsideRoot, 0, 0, 0, PurgeRootRefusal);
        }

        if (File.Exists(full))
        {
            return new EmptyTrashResult(FileOutcome.IsAFile, 0, 0, 0);
        }

        var result = EmptyContents(full);
        if (result.Outcome == FileOutcome.Ok && Directory.Exists(full))
        {
            DiagnosticLog.Info(Category, FolderPurgedLogLine(Relative(full, isDirectory: true), result.Files, result.Folders, result.Bytes));
        }

        return result;
    }

    /// <summary>Why <see cref="PurgeFolder"/> will not empty the root. Pinned.</summary>
    public const string PurgeRootRefusal = "that is the working directory itself";

    public static string FolderPurgedLogLine(string relative, int files, int folders, long bytes) =>
        string.Create(CultureInfo.InvariantCulture, $"Purged {relative}: {files} files, {folders} folders, {bytes:N0} bytes");

    /// <summary>
    /// The walk-and-delete behind <see cref="EmptyTrash"/> and <see cref="PurgeFolder"/>: everything under
    /// <paramref name="target"/> counted, read-only cleared, then deleted; the folder itself stays. Absent = nothing to do.
    /// </summary>
    private static EmptyTrashResult EmptyContents(string target)
    {
        if (!Directory.Exists(target))
        {
            return new EmptyTrashResult(FileOutcome.Ok, 0, 0, 0);
        }

        int files = 0, folders = 0;
        long bytes = 0;
        try
        {
            // Everything, hidden and system included — nothing in the folder is spared — but never
            // through a reparse point, which Directory.Delete below removes as a link too.
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = false,
                AttributesToSkip = FileAttributes.ReparsePoint,
                ReturnSpecialDirectories = false,
            };
            foreach (var entry in new FileSystemEnumerable<WalkEntry>(
                target,
                (ref FileSystemEntry entry) => new WalkEntry(entry.ToFullPath(), entry.IsDirectory ? 0 : entry.Length, entry.LastWriteTimeUtc.UtcDateTime, entry.IsDirectory),
                options))
            {
                if (entry.IsDirectory)
                {
                    folders++;
                    continue;
                }

                files++;
                bytes += entry.Length;
                var attributes = File.GetAttributes(entry.FullPath);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(entry.FullPath, attributes & ~FileAttributes.ReadOnly);
                }
            }

            foreach (var folder in Directory.EnumerateDirectories(target))
            {
                Directory.Delete(folder, recursive: true);
            }

            foreach (var file in Directory.EnumerateFiles(target))
            {
                File.Delete(file);
            }

            return new EmptyTrashResult(FileOutcome.Ok, files, folders, bytes);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return new EmptyTrashResult(FileOutcome.Failed, files, folders, bytes, ex.Message);
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
            if (string.IsNullOrWhiteSpace(to))
            {
                archive = isDirectory ? source + ".zip" : Path.ChangeExtension(source, ".zip");
                if (string.Equals(source, Root, StringComparison.OrdinalIgnoreCase))
                {
                    return new ZipResult(FileOutcome.IntoItself, display, Relative(archive), 0, 0);
                }
            }
            else
            {
                outcome = Resolve(to, forWrite: true, out archive);
                if (outcome != FileOutcome.Ok)
                {
                    return new ZipResult(outcome, display, to, 0, 0);
                }
            }

            if (isDirectory && IsInside(source, archive))
            {
                return new ZipResult(FileOutcome.IntoItself, display, Relative(archive), 0, 0);
            }

            if (Directory.Exists(archive))
            {
                return new ZipResult(FileOutcome.IsDirectory, display, Relative(archive, isDirectory: true), 0, 0);
            }

            if (File.Exists(archive) && !overwrite)
            {
                return new ZipResult(FileOutcome.Exists, display, Relative(archive), 0, 0);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
            string temp = TempSibling(archive);
            int entries = 0;
            try
            {
                using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
                {
                    if (isDirectory)
                    {
                        string baseName = Path.GetFileName(source);
                        foreach (var entry in Walk(source, null, recurse: true))
                        {
                            string name = baseName + "/" + entry.FullPath[(source.Length + 1)..].Replace('\\', '/');
                            zip.CreateEntryFromFile(entry.FullPath, name, CompressionLevel.Optimal);
                            entries++;
                        }
                    }
                    else
                    {
                        zip.CreateEntryFromFile(source, Path.GetFileName(source), CompressionLevel.Optimal);
                        entries = 1;
                    }
                }

                File.Move(temp, archive, overwrite: true);
            }
            finally
            {
                DeleteQuietly(temp);
            }

            return new ZipResult(FileOutcome.Ok, display, Relative(archive), entries, new FileInfo(archive).Length);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return new ZipResult(FileOutcome.Failed, Relative(source), to ?? "", 0, 0, ex.Message);
        }
    }

    /// <summary>
    /// Extracts a zip into a folder (blank <paramref name="to"/> = one named after the archive,
    /// next to it). All or nothing: every entry's destination is checked to lie under the folder
    /// (zip-slip) and, without <paramref name="overwrite"/>, to be free, before anything is written.
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
                folder = Path.Combine(Path.GetDirectoryName(source)!, Path.GetFileNameWithoutExtension(source));
            }
            else
            {
                outcome = Resolve(to, forWrite: true, out folder);
                if (outcome != FileOutcome.Ok)
                {
                    return new ZipResult(outcome, display, to, 0, 0);
                }
            }

            string folderDisplay = Relative(folder, isDirectory: true);
            if (File.Exists(folder))
            {
                return new ZipResult(FileOutcome.IsAFile, display, Relative(folder), 0, 0);
            }

            using var zip = ZipFile.OpenRead(source);
            var plan = new List<(ZipArchiveEntry Entry, string Target)>();
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    continue;
                }

                string target = Path.GetFullPath(Path.Combine(folder, entry.FullName));
                if (!IsInside(folder, target))
                {
                    return new ZipResult(FileOutcome.OutsideRoot, display, folderDisplay, 0, 0, entry.FullName);
                }

                if (!overwrite && (File.Exists(target) || Directory.Exists(target)))
                {
                    return new ZipResult(FileOutcome.Exists, display, Relative(target), 0, 0);
                }

                plan.Add((entry, target));
            }

            Directory.CreateDirectory(folder);
            foreach (var (entry, target) in plan)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }

            return new ZipResult(FileOutcome.Ok, display, folderDisplay, plan.Count, new FileInfo(source).Length);
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

    /// <summary>
    /// Hands a file or folder (blank = the root) to <paramref name="opener"/>, the shell's "open with" call.
    /// <paramref name="foldersOnly"/> refuses a file with <see cref="FileOutcome.IsAFile"/> before the opener is
    /// called (the <c>/explore</c> command browses; the <c>open</c> tool takes either).
    /// </summary>
    public OpenResult Open(string relative, Action<string> opener, bool foldersOnly = false)
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

            opener(full);
            return new OpenResult(FileOutcome.Ok, Relative(full, isDirectory), isDirectory);
        }
        catch (Exception ex) when (IsFileFailure(ex) || ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new OpenResult(FileOutcome.Failed, Relative(full), false, ex.Message);
        }
    }

    // ---- helpers ----

    private readonly record struct WalkEntry(string FullPath, long Length, DateTime LastWriteTimeUtc, bool IsDirectory);

    private static EnumerationOptions WalkOptions(bool recurse) => new()
    {
        RecurseSubdirectories = recurse,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false,
    };

    /// <summary>
    /// One enumeration for every walk: files matching <paramref name="namePattern"/> (a Win32
    /// glob over the name, null = all; a path glob — a separator or <c>**</c> in it — over the
    /// file's path under <paramref name="directory"/> or under the root, <see cref="PathGlob"/>,
    /// 2026-09-17), folders too when asked, never into a reparse point, never into the root's
    /// <c>.trash</c>, never below <paramref name="maxDepth"/> levels (1 = the folder's own entries; <c>search_files</c>'s
    /// <c>depth</c>, 2026-09-19). Each entry's size and write time come with it — no second stat.
    /// Brace groups are expanded first (<see cref="PathGlob.ExpandBraces"/>, 2026-09-24) and each
    /// alternative is judged name-or-path on its own, so <c>{src/*.cs,*.md}</c> mixes the two.
    /// </summary>
    private FileSystemEnumerable<WalkEntry> Walk(string directory, string? namePattern, bool recurse, bool includeDirectories = false, int maxDepth = int.MaxValue)
    {
        string root = Root;
        GlobAlternative[]? alternatives = namePattern is null
            ? null
            : [.. PathGlob.ExpandBraces(namePattern).Select(p => new GlobAlternative(p, PathGlob.IsPathPattern(p)))];
        return new FileSystemEnumerable<WalkEntry>(
            directory,
            (ref FileSystemEntry entry) => new WalkEntry(entry.ToFullPath(), entry.IsDirectory ? 0 : entry.Length, entry.LastWriteTimeUtc.UtcDateTime, entry.IsDirectory),
            WalkOptions(recurse))
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                (includeDirectories || !entry.IsDirectory)
                && !IsRootTrash(ref entry, root)
                && (alternatives is null || entry.IsDirectory || MatchesAny(alternatives, ref entry, directory, root)),
            ShouldRecursePredicate = (ref FileSystemEntry entry) => !IsRootTrash(ref entry, root) && DepthUnder(entry.Directory, directory) < maxDepth,
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

    private static bool IsRootTrash(ref FileSystemEntry entry, string root) =>
        entry.IsDirectory
        && entry.FileName.Equals(TrashFolderName, StringComparison.OrdinalIgnoreCase)
        && entry.Directory.TrimEnd(Path.DirectorySeparatorChar).Equals(root, StringComparison.OrdinalIgnoreCase);

    private static bool IsTrashName(string name) => string.Equals(name, TrashFolderName, StringComparison.OrdinalIgnoreCase);

    /// <summary><paramref name="path"/> equals <paramref name="root"/> or lies under it, by spelling.</summary>
    internal static bool IsInside(string root, string path) =>
        string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
        || (path.Length > root.Length
            && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            && (path[root.Length] == Path.DirectorySeparatorChar || path[root.Length] == Path.AltDirectorySeparatorChar));

    private static bool IsInTrash(string root, string full)
    {
        if (full.Length <= root.Length)
        {
            return false;
        }

        var rest = full.AsSpan(root.Length + 1);
        int cut = rest.IndexOfAny('\\', '/');
        var first = cut < 0 ? rest : rest[..cut];
        return first.Equals(TrashFolderName, StringComparison.OrdinalIgnoreCase);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", WalkOptions(recurse: false)))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var folder in Directory.EnumerateDirectories(source, "*", WalkOptions(recurse: false)))
        {
            CopyDirectory(folder, Path.Combine(destination, Path.GetFileName(folder)));
        }
    }

    /// <summary>A temp sibling written whole, then moved over the target: a reader never sees half a file.</summary>
    internal static long WriteAtomically(string full, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string temp = TempSibling(full);
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            DeleteQuietly(temp);
        }

        return bytes.LongLength;
    }

    private static string TempSibling(string full) => full + "." + Guid.NewGuid().ToString("N") + ".tmp";

    private static void DeleteQuietly(string path)
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

    /// <summary>UTF-8 without a leading byte-order mark (PowerShell writes one; the model must never see U+FEFF).</summary>
    public static string Decode(byte[] bytes, out bool bom)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        return bom ? Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3) : Encoding.UTF8.GetString(bytes);
    }

    public static bool LooksBinary(ReadOnlySpan<byte> bytes) =>
        bytes[..Math.Min(BinaryProbeBytes, bytes.Length)].IndexOf((byte)0) >= 0;

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
            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BinaryProbeBytes);
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
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> head = stackalloc byte[2];
            return stream.Read(head) == 2 && head[0] == (byte)'P' && head[1] == (byte)'K';
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return false;
        }
    }

    private static bool IsFileFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException;

    /// <summary>Lines by <c>\n</c> with a <c>\r</c> before it dropped; a trailing newline adds no empty last line.</summary>
    public static List<string> SplitLines(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = new List<string>();
        if (text.Length == 0)
        {
            return lines;
        }

        foreach (var line in text.AsSpan().EnumerateLines())
        {
            lines.Add(line.ToString());
        }

        if (lines.Count > 0 && lines[^1].Length == 0 && text.Length > 0 && (text[^1] == '\n' || text[^1] == '\r'))
        {
            lines.RemoveAt(lines.Count - 1);
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
        return trimmed.Length <= MaxLineChars ? trimmed.ToString() : string.Concat(trimmed[..(MaxLineChars - 1)], "…");
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

    private static int CompareHits(SearchHit a, SearchHit b)
    {
        int byPath = StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath);
        return byPath != 0 ? byPath : a.Line.CompareTo(b.Line);
    }
}
