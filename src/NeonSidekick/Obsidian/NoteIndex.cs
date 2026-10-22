using System.Text;
using NeonSidekick.Files;

namespace NeonSidekick.Obsidian;

/// <summary>
/// One Markdown note of the vault as the index last read it: its vault-relative path (<c>/</c>-separated,
/// with <c>.md</c>), its name (the file name without <c>.md</c>), when and how big it was, its
/// properties, their tags and aliases, and what <see cref="NoteScanner"/> found in its body.
/// </summary>
public sealed record NoteEntry(string Relative, string Full, DateTime ModifiedUtc, long Length, NoteFrontmatter Frontmatter, NoteScan Scan)
{
    public string Name { get; } = VaultPaths.NameOf(Relative);

    public string Folder { get; } = VaultPaths.FolderOf(Relative);

    public IReadOnlyList<string> Aliases { get; } = NoteProperties.Aliases(Frontmatter);

    /// <summary>Every tag the note carries, inline and in its properties, first spelling kept, no duplicates.</summary>
    public IReadOnlyList<string> Tags { get; } = NoteProperties.Tags(Frontmatter).Concat(Scan.Tags.Select(t => t.Name)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Why the note's text was not read, or null when it was (2026-10-22, review K4: a sharing violation was kept as an empty entry until
    /// the note changed, its links, tags and aliases lost; an unreadable note is read again at the next walk). <see cref="NoteRead"/>'s word.
    /// </summary>
    public NoteRead? NotRead { get; init; }

    /// <summary>Whether the note carries <paramref name="tag"/> or a tag nested under it (<c>project</c> matches <c>project/alpha</c>).</summary>
    public bool HasTag(string tag)
    {
        string wanted = tag.Trim().TrimStart('#');
        return Tags.Any(t => string.Equals(t, wanted, StringComparison.OrdinalIgnoreCase) || t.StartsWith(wanted + "/", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Why a note's lines could not be had (2026-10-22, review K4, H7): too big to read whole, not text, or a read that failed.</summary>
public enum NoteRead
{
    TooLarge,
    NotText,
    Unreadable,
}

/// <summary>How a link or a <c>note</c> argument found its note: by its path, by its name, by an alias.</summary>
public enum ResolveBy
{
    None,
    Path,
    RelativePath,
    Name,
    Alias,
}

/// <summary>
/// What a link or a <c>note</c> argument names: the note (or, for an attachment, the file's relative
/// path), how it was found, and the other notes of the same name a short link could also have meant.
/// </summary>
public sealed record Resolution(NoteEntry? Note, string? Attachment, ResolveBy By, IReadOnlyList<string> Others)
{
    public static Resolution Unresolved { get; } = new(null, null, ResolveBy.None, []);

    public bool Found => Note is not null || Attachment is not null;

    /// <summary>The vault-relative path of what was found, or null.</summary>
    public string? Relative => Note?.Relative ?? Attachment;
}

/// <summary>The vault's path spelling (2026-09-22): <c>/</c>-separated and relative, as Obsidian's links and its own UI write them.</summary>
public static class VaultPaths
{
    public const string NoteExtension = ".md";

    /// <summary>
    /// A typed path as the vault spells it: <c>\</c> as <c>/</c>, no leading <c>./</c> or <c>/</c>, no trailing <c>/</c>, in Unicode's composed
    /// form (2026-10-22, review J9: a Mac's Finder and iCloud keep names decomposed, the model writes them composed, and the two never matched).
    /// </summary>
    public static string Normalize(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string p = Composed(path.Trim().Replace('\\', '/'));
        while (p.StartsWith("./", StringComparison.Ordinal))
        {
            p = p[2..];
        }

        return p.Trim('/');
    }

    /// <summary><paramref name="text"/> in Unicode's composed form (NFC), as the index keys every path (review J9).</summary>
    public static string Composed(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            return text.IsNormalized(NormalizationForm.FormC) ? text : text.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return text;   // an unpaired surrogate: kept as written
        }
    }

    public static bool IsNote(string path) => path.EndsWith(NoteExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The extensions that make a name a file other than a note (2026-10-22, review Q15: any one to six letters after a dot counted, so
    /// "Call Dr.Smith" and "Learning Node.js" could not be made): what Obsidian opens or embeds, and the documents a vault keeps beside notes.
    /// </summary>
    public static readonly IReadOnlySet<string> FileExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "md", "canvas", "base", "pdf",
        "png", "jpg", "jpeg", "gif", "bmp", "svg", "webp", "avif", "heic", "heif", "tif", "tiff", "ico",
        "mp3", "wav", "m4a", "ogg", "oga", "flac", "3gp", "opus", "aac",
        "mp4", "webm", "ogv", "mov", "mkv", "avi", "m4v",
        "txt", "csv", "tsv", "json", "xml", "yaml", "yml", "html", "htm", "epub", "zip", "excalidraw",
        "doc", "docx", "xls", "xlsx", "ppt", "pptx", "odt", "ods", "odp", "rtf",
    };

    /// <summary>
    /// Whether the name ends in a file's extension (<see cref="FileExtensions"/>: <c>.png</c>, <c>.mp4</c>, <c>.canvas</c>), so a dotted
    /// note name (<c>2026.09.22</c>, <c>v1.2 plan</c>, <c>Call Dr.Smith</c>) is a note's name, not a file type.
    /// </summary>
    public static bool HasFileExtension(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        int slash = path.LastIndexOf('/');
        int dot = path.LastIndexOf('.');
        if (dot <= slash + 1 || dot == path.Length - 1)
        {
            return false;
        }

        return FileExtensions.Contains(path[(dot + 1)..]);
    }

    /// <summary>Obsidian's rule for a file name: none of <c>* " \ / &lt; &gt; : | ?</c> (a <c>:</c> would also be an NTFS stream), no control character, not ending in a dot or space.</summary>
    public static bool IsValidName(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (segment.Length == 0 || segment.EndsWith('.') || segment.EndsWith(' '))
        {
            return false;
        }

        foreach (char c in segment)
        {
            if (c is '*' or '"' or '\\' or '/' or '<' or '>' or ':' or '|' or '?' || char.IsControl(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary><c>Projects/Plan.md</c> as <c>Plan</c>; an attachment keeps its extension (<c>diagram.png</c>).</summary>
    public static string NameOf(string relative)
    {
        ArgumentNullException.ThrowIfNull(relative);
        int slash = relative.LastIndexOf('/');
        string name = slash < 0 ? relative : relative[(slash + 1)..];
        return IsNote(name) ? name[..^NoteExtension.Length] : name;
    }

    /// <summary><c>Projects/Plan.md</c> as <c>Projects</c>; empty at the root.</summary>
    public static string FolderOf(string relative)
    {
        ArgumentNullException.ThrowIfNull(relative);
        int slash = relative.LastIndexOf('/');
        return slash < 0 ? "" : relative[..slash];
    }

    public static string Join(string folder, string name) => folder.Length == 0 ? name : folder + "/" + name;

    /// <summary><c>../x/y.md</c> against <paramref name="folder"/>, <c>.</c> and <c>..</c> folded; null when it climbs above the vault.</summary>
    public static string? Combine(string folder, string relative)
    {
        var parts = new List<string>(folder.Length == 0 ? [] : folder.Split('/'));
        foreach (var part in relative.Replace('\\', '/').Split('/'))
        {
            if (part.Length == 0 || part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (parts.Count == 0)
                {
                    return null;
                }

                parts.RemoveAt(parts.Count - 1);
                continue;
            }

            parts.Add(part);
        }

        return string.Join('/', parts);
    }

    /// <summary>Whether a link's target is written relative to its note (<c>./Plan</c>, <c>../Archive/Plan</c>): Obsidian resolves it from there (review U2).</summary>
    public static bool IsExplicitlyRelative(string reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        string r = reference.Trim().Replace('\\', '/');
        return r.StartsWith("./", StringComparison.Ordinal) || r.StartsWith("../", StringComparison.Ordinal) || r is "." or "..";
    }

    /// <summary>The path from <paramref name="fromFolder"/> to <paramref name="target"/>: <c>../Archive/Plan.md</c>.</summary>
    public static string RelativeFrom(string fromFolder, string target)
    {
        var from = fromFolder.Length == 0 ? Array.Empty<string>() : fromFolder.Split('/');
        var to = target.Split('/');
        int common = 0;
        while (common < from.Length && common < to.Length - 1 && string.Equals(from[common], to[common], StringComparison.OrdinalIgnoreCase))
        {
            common++;
        }

        var sb = new StringBuilder();
        for (int i = common; i < from.Length; i++)
        {
            sb.Append("../");
        }

        sb.Append(string.Join('/', to.Skip(common)));
        return sb.ToString();
    }

    /// <summary>Whether a vault-relative path touches a dot-folder or dot-file (<c>.obsidian</c>, <c>.trash</c>, <c>.git</c>): Obsidian's own, never a note.</summary>
    public static bool IsHidden(string relative) =>
        relative.Split('/').Any(part => part.StartsWith('.') && part != "." && part != "..");
}

/// <summary>
/// What a resolution may use (2026-10-22): a link's own rules, or a tool argument's — and one that writes takes a path as written and a
/// name alone, never an alias or a suffix match (review J5: an overwrite of a missing <c>Projects/Plan.md</c> wrote
/// <c>Archive/2023/Projects/Plan.md</c>, and an alias reached a different note).
/// </summary>
public enum ResolveFor
{
    /// <summary>A link in a note: Obsidian's rules, no alias (review U11: stock Obsidian never resolves <c>[[alias]]</c>).</summary>
    Link,

    /// <summary>A tool's argument that reads: a link's rules, an alias too.</summary>
    Read,

    /// <summary>A tool's argument that changes the note: its path as written, else its name; no suffix, no alias.</summary>
    Write,
}

/// <summary>
/// The vault as the tools see it (2026-09-22): every Markdown note and every other file, walked from the
/// root with the dot-folders (<c>.obsidian</c>, <c>.trash</c>, <c>.git</c>) and hidden, system and
/// reparse-point entries skipped. <see cref="Refresh"/> re-walks on each call and re-reads a note only when
/// its size or time changed, so a vault edited in Obsidian between two calls is current without a watcher.
///
/// <para>Resolution is Obsidian's (<see cref="Resolve"/>): a target written relative to its note (<c>./x</c>, <c>../x</c>, a Markdown
/// link's usual form) from there; for a bare name, a note of that name in the linking note's own folder (D7, 2026-10-22); then an exact
/// vault path (with or without <c>.md</c>), then a path suffix (<c>Projects/Plan</c> for <c>Work/Projects/Plan.md</c>), then the name, then
/// (for a tool's read argument only) an alias; among several of one name the linking note's own folder wins, then the shortest path, then
/// the first in order — and the result names the others. Paths are keyed composed (NFC) and by the vault volume's case rule. Not
/// thread-safe: <see cref="ObsidianVault"/> holds the lock.</para>
/// </summary>
public sealed class NoteIndex
{
    /// <summary>The most notes read into the index; a bigger vault is cut there and says so.</summary>
    public const int MaxNotes = 20_000;

    /// <summary>The most attachment paths kept for resolution.</summary>
    public const int MaxAttachments = 50_000;

    private Dictionary<string, NoteEntry> _notes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _attachments = [];
    private Dictionary<string, List<NoteEntry>> _byName = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, List<NoteEntry>> _byAlias = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, List<NoteEntry>> _bySuffix = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, List<string>> _attachmentsByName = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, List<string>> _attachmentsBySuffix = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _attachmentsByPath = new(StringComparer.OrdinalIgnoreCase);
    private StringComparer _paths = StringComparer.OrdinalIgnoreCase;
    private string _root = "";

    /// <summary>The notes in path order.</summary>
    public IReadOnlyList<NoteEntry> Notes { get; private set; } = [];

    public IReadOnlyList<string> Attachments => _attachments;

    /// <summary>Whether the last walk stopped at <see cref="MaxNotes"/> (or at <see cref="MaxAttachments"/>, past which it stops looking).</summary>
    public bool Truncated { get; private set; }

    /// <summary>
    /// How paths compare in this vault: its volume's case rule (2026-10-22, review K14: keyed without case, <c>Plan.md</c> and <c>plan.md</c>
    /// on a case-sensitive volume collapsed into one and a call could reach the wrong file).
    /// </summary>
    public StringComparer PathComparer => _paths;

    /// <summary>Re-walks <paramref name="root"/>: new and changed notes read, gone ones dropped, the name tables rebuilt.</summary>
    public void Refresh(string root, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (!string.Equals(root, _root, StringComparison.OrdinalIgnoreCase))
        {
            _paths = CaseRule.Comparer(root);
            _notes = new Dictionary<string, NoteEntry>(_paths);
            _root = root;
        }

        _attachments.Clear();
        Truncated = false;
        var seen = new HashSet<string>(_paths);
        foreach (var full in Walk(root, cancellationToken))
        {
            string relative = VaultPaths.Composed(Path.GetRelativePath(root, full).Replace('\\', '/'));
            if (!VaultPaths.IsNote(relative))
            {
                if (_attachments.Count < MaxAttachments)
                {
                    _attachments.Add(relative);
                }

                continue;
            }

            if (seen.Count >= MaxNotes)
            {
                Truncated = true;
                // Both lists full: nothing more the walk could keep (2026-10-22, review H9: it walked the whole vault past both caps).
                if (_attachments.Count >= MaxAttachments)
                {
                    break;
                }

                continue;
            }

            seen.Add(relative);
            FileInfo info;
            try
            {
                info = new FileInfo(full);
                _ = info.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (_notes.TryGetValue(relative, out var known) && known.NotRead is not NoteRead.Unreadable
                && known.Length == info.Length && known.ModifiedUtc == info.LastWriteTimeUtc)
            {
                continue;
            }

            _notes[relative] = Read(relative, full, info);
        }

        foreach (var gone in _notes.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _notes.Remove(gone);
        }

        Rebuild();
    }

    /// <summary>Re-reads one note now (after a tool wrote it), or drops it when it is gone.</summary>
    public void Update(string relative, string full)
    {
        relative = VaultPaths.Composed(relative);
        try
        {
            var info = new FileInfo(full);
            if (!info.Exists)
            {
                _notes.Remove(relative);
            }
            else
            {
                _notes[relative] = Read(relative, full, info);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _notes.Remove(relative);
        }

        Rebuild();
    }

    private void Rebuild()
    {
        Notes = _notes.Values.OrderBy(n => n.Relative, StringComparer.OrdinalIgnoreCase).ThenBy(n => n.Relative, StringComparer.Ordinal).ToList();
        _byName = Group(Notes, n => [n.Name]);
        _byAlias = Group(Notes, n => n.Aliases);
        _bySuffix = Group(Notes, n => Suffixes(n.Relative));
        _attachmentsByName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        _attachmentsBySuffix = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        _attachmentsByPath = new Dictionary<string, string>(_paths);
        foreach (var a in _attachments)
        {
            _attachmentsByPath[a] = a;
            Add(_attachmentsByName, VaultPaths.NameOf(a), a);
            foreach (string suffix in Suffixes(a))
            {
                Add(_attachmentsBySuffix, suffix, a);
            }
        }

        static void Add(Dictionary<string, List<string>> map, string key, string value)
        {
            if (!map.TryGetValue(key, out var list))
            {
                map[key] = list = [];
            }

            list.Add(value);
        }

        static Dictionary<string, List<NoteEntry>> Group(IEnumerable<NoteEntry> notes, Func<NoteEntry, IEnumerable<string>> keys)
        {
            var map = new Dictionary<string, List<NoteEntry>>(StringComparer.OrdinalIgnoreCase);
            foreach (var note in notes)
            {
                foreach (var key in keys(note))
                {
                    if (!map.TryGetValue(key, out var list))
                    {
                        map[key] = list = [];
                    }

                    if (!list.Contains(note))
                    {
                        list.Add(note);
                    }
                }
            }

            return map;
        }
    }

    /// <summary>
    /// Every tail of <paramref name="relative"/> that starts after a <c>/</c> (<c>Work/Projects/Plan.md</c> → <c>Projects/Plan.md</c>,
    /// <c>Plan.md</c>): a suffix link's lookup, one table read instead of a pass over every note (2026-10-22, review H8).
    /// </summary>
    private static IEnumerable<string> Suffixes(string relative)
    {
        for (int slash = relative.IndexOf('/'); slash >= 0; slash = relative.IndexOf('/', slash + 1))
        {
            yield return relative[(slash + 1)..];
        }
    }

    public NoteEntry? Get(string relative) => _notes.GetValueOrDefault(VaultPaths.Composed(relative));

    private static NoteEntry Read(string relative, string full, FileInfo info)
    {
        if (TryReadLines(full, out var lines, out _) is { } why)
        {
            return new NoteEntry(relative, full, info.LastWriteTimeUtc, info.Length, NoteFrontmatter.None, NoteScan.Empty) { NotRead = why };
        }

        return new NoteEntry(relative, full, info.LastWriteTimeUtc, info.Length, NoteProperties.Parse(lines), NoteScanner.Scan(lines));
    }

    /// <summary>A note's lines (BOM dropped), or null when it cannot be read, is too big or is not text.</summary>
    public static List<string>? ReadLines(string full) => TryReadLines(full, out var lines, out _) is null ? lines : null;

    /// <summary>
    /// A note's lines, or why not (2026-10-22, review H7: a note was read whole whatever its size, under the vault's lock, and one past 2 GB
    /// was skipped without a word): read no further than <see cref="WorkingDirectory.MaxTextFileBytes"/>, the bytes counted as they come.
    /// <paramref name="detail"/> is the failed read's message.
    /// </summary>
    public static NoteRead? TryReadLines(string full, out List<string> lines, out string detail)
    {
        lines = [];
        detail = "";
        try
        {
            if (RegularFiles.ReadAtMost(full, WorkingDirectory.MaxTextFileBytes) is not { } bytes)
            {
                return NoteRead.TooLarge;
            }

            if (WorkingDirectory.LooksBinary(bytes))
            {
                return NoteRead.NotText;
            }

            lines = WorkingDirectory.SplitLines(WorkingDirectory.Decode(bytes, out _));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            detail = ex.Message;
            return NoteRead.Unreadable;
        }
    }

    private static IEnumerable<string> Walk(string root, CancellationToken cancellationToken)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
        };
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            // Esc between folders (2026-10-22, review V4, H9: the walk ran to its end under the vault's lock).
            cancellationToken.ThrowIfCancellationRequested();
            string folder = pending.Pop();
            IEnumerable<string> files;
            IEnumerable<string> folders;
            try
            {
                files = Directory.EnumerateFiles(folder, "*", options).ToList();
                folders = Directory.EnumerateDirectories(folder, "*", options).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                if (!Path.GetFileName(file).StartsWith('.'))
                {
                    yield return file;
                }
            }

            foreach (var sub in folders.OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase))
            {
                if (!Path.GetFileName(sub).StartsWith('.'))
                {
                    pending.Push(sub);
                }
            }
        }
    }

    /// <summary>
    /// What <paramref name="reference"/> names, seen from the note in <paramref name="fromFolder"/> (null
    /// for a tool's own argument, which has no linking note). The reference is a link's target as
    /// written — no <c>#</c> part, no alias — or a <c>note</c> argument; <paramref name="use"/> says which rules hold.
    /// </summary>
    public Resolution Resolve(string reference, string? fromFolder = null, bool markdown = false, ResolveFor use = ResolveFor.Read)
    {
        ArgumentNullException.ThrowIfNull(reference);
        string target = VaultPaths.Normalize(reference);
        if (target.Length == 0)
        {
            return Resolution.Unresolved;
        }

        bool hasExtension = VaultPaths.HasFileExtension(target) && !VaultPaths.IsNote(target);
        string asNote = VaultPaths.IsNote(target) ? target : target + VaultPaths.NoteExtension;

        // A target written relative to its note (./Plan, ../Archive/Plan) is read from there alone (2026-10-22, review U2: a wikilink's
        // never was, and ./Plan found any note named Plan).
        if (fromFolder is not null && VaultPaths.IsExplicitlyRelative(reference))
        {
            return VaultPaths.Combine(fromFolder, VaultPaths.Composed(reference.Trim())) is { } relative && Found(relative, asNoteToo: true) is { } hit
                ? hit with { By = ResolveBy.RelativePath }
                : Resolution.Unresolved;
        }

        // A Markdown link is relative to its note first (Obsidian writes them so under "relative path to file").
        if (markdown && fromFolder is not null && VaultPaths.Combine(fromFolder, target) is { } near)
        {
            if (Found(near, asNoteToo: true) is { } hit)
            {
                return hit with { By = ResolveBy.RelativePath };
            }
        }

        bool bare = !target.Contains('/', StringComparison.Ordinal);

        // A bare name: the linking note's own folder first, before a root note of that name (2026-10-22, review U1, the fix plan's D7: the
        // root's matched "by path" ahead of the folder's, so a move rewrote links to the wrong note), an attachment's the same.
        if (bare && fromFolder is { Length: > 0 } folder)
        {
            if (!hasExtension && _byName.TryGetValue(VaultPaths.NameOf(asNote), out var named)
                && named.Any(n => string.Equals(n.Folder, folder, StringComparison.OrdinalIgnoreCase)))
            {
                return Pick(named, fromFolder, ResolveBy.Name);
            }

            if (_attachmentsByName.TryGetValue(target, out var files) && files.Any(a => string.Equals(VaultPaths.FolderOf(a), folder, StringComparison.OrdinalIgnoreCase)))
            {
                return Attachment(files, fromFolder, ResolveBy.Name);
            }
        }

        if (Found(target, asNoteToo: true) is { } exact)
        {
            return exact;
        }

        if (!bare)
        {
            // A path names that path only when a call changes it (review J5).
            if (use == ResolveFor.Write)
            {
                return Resolution.Unresolved;
            }

            if (_bySuffix.TryGetValue(asNote, out var byNoteSuffix))
            {
                return Pick(byNoteSuffix, fromFolder, ResolveBy.Path);
            }

            return _attachmentsBySuffix.TryGetValue(target, out var byAttachmentSuffix)
                ? Attachment(byAttachmentSuffix, fromFolder, ResolveBy.Path)
                : Resolution.Unresolved;
        }

        if (hasExtension && _attachmentsByName.TryGetValue(target, out var typed))
        {
            return Attachment(typed, fromFolder, ResolveBy.Name);
        }

        if (_byName.TryGetValue(VaultPaths.NameOf(asNote), out var byName))
        {
            return Pick(byName, fromFolder, ResolveBy.Name);
        }

        // An alias only for a tool's read argument (review U11: a link never resolves by one in Obsidian; review J5: a write never does).
        if (use == ResolveFor.Read && _byAlias.TryGetValue(target, out var aliased))
        {
            return Pick(aliased, fromFolder, ResolveBy.Alias);
        }

        return _attachmentsByName.TryGetValue(target, out var other) ? Attachment(other, fromFolder, ResolveBy.Name) : Resolution.Unresolved;

        Resolution? Found(string path, bool asNoteToo)
        {
            if (_notes.TryGetValue(path, out var note) || (asNoteToo && _notes.TryGetValue(path + VaultPaths.NoteExtension, out note)))
            {
                return new Resolution(note, null, ResolveBy.Path, []);
            }

            return _attachmentsByPath.TryGetValue(path, out var attachment) ? new Resolution(null, attachment, ResolveBy.Path, []) : null;
        }
    }

    /// <summary>One of several attachments: the linking note's folder first, then the shortest path, then path order; the rest named (review U1).</summary>
    private static Resolution Attachment(List<string> candidates, string? fromFolder, ResolveBy by)
    {
        var ordered = candidates
            .OrderBy(a => fromFolder is not null && string.Equals(VaultPaths.FolderOf(a), fromFolder, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(a => a.Length)
            .ThenBy(a => a, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new Resolution(null, ordered[0], by, ordered.Skip(1).ToList());
    }

    /// <summary>One of several candidates: the linking note's folder first, then the shortest path, then path order; the rest named.</summary>
    private static Resolution Pick(List<NoteEntry> candidates, string? fromFolder, ResolveBy by)
    {
        var ordered = candidates
            .OrderBy(n => fromFolder is not null && string.Equals(n.Folder, fromFolder, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(n => n.Relative.Count(c => c == '/'))
            .ThenBy(n => n.Relative.Length)
            .ThenBy(n => n.Relative, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new Resolution(ordered[0], null, by, ordered.Skip(1).Select(n => n.Relative).ToList());
    }

    /// <summary>Every link in the vault that resolves to <paramref name="note"/>, with the note it is in.</summary>
    public IReadOnlyList<(NoteEntry Source, NoteLink Link)> Backlinks(NoteEntry note, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(note);
        var found = new List<(NoteEntry, NoteLink)>();
        foreach (var source in Notes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var link in source.Scan.Links)
            {
                if (ResolveLink(source, link).Note is { } target && ReferenceEquals(target, note))
                {
                    found.Add((source, link));
                }
            }
        }

        return found;
    }

    /// <summary>A link as its note sees it (an empty target is the note itself: <c>[[#Heading]]</c>).</summary>
    public Resolution ResolveLink(NoteEntry source, NoteLink link)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(link);
        return link.Target.Length == 0
            ? new Resolution(source, null, ResolveBy.Path, [])
            : Resolve(link.Target, source.Folder, markdown: !link.IsWiki, ResolveFor.Link);
    }
}
