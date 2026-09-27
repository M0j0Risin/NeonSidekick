using System.IO.Compression;
using NeonSidekick.Files;

namespace NeonSidekick.Skills;

/// <summary>One file of a <see cref="SkillCandidate"/>: its path under the skill folder (forward slashes), its entry in the archive and the size the archive declares.</summary>
public sealed record SkillArchiveFile(string Path, string EntryName, long Length);

/// <summary>
/// A folder of a <see cref="SkillArchive"/> holding a <c>SKILL.md</c>: where it sits in the
/// archive (<c>""</c> for the archive's root), the frontmatter and body when they read, and the
/// files that would be installed. <see cref="Refusal"/> is why it cannot be installed — a
/// frontmatter that does not read, a name that is not a skill name, a cap exceeded, a path no
/// Windows folder may hold; null when it can. <see cref="Skipped"/> lists the entries left out
/// (symbolic links).
/// </summary>
public sealed record SkillCandidate(string Folder, SkillFrontmatter? Frontmatter, string Body, string? Refusal, IReadOnlyList<SkillArchiveFile> Files, long Bytes, IReadOnlyList<string> Skipped)
{
    /// <summary>The folder's own name; the archive's name for its root.</summary>
    public string FolderName => Folder.Length == 0 ? "" : Folder[(Folder.LastIndexOf('/') + 1)..];

    /// <summary>The name to install under: the frontmatter's, else the folder's.</summary>
    public string Name => Frontmatter?.Name ?? FolderName;
}

/// <summary>
/// A skill archive (<c>/skills add</c>, 2026-09-26): a GitHub repository's file listing
/// (<see cref="FromTree"/>, the Git Trees API: paths and sizes, no content — the SKILL.md files and
/// then the one skill's files fetched one by one, <see cref="FetchSkillMdsAsync"/> and
/// <see cref="FetchAsync"/>), or a zip read in memory (<see cref="TryOpen"/>): a GitHub repository
/// as codeload zips it (every entry under one <c>&lt;repo&gt;-&lt;sha&gt;/</c> folder, which
/// <see cref="TryOpen"/> strips, and the commit's SHA as the archive comment, <see cref="Commit"/>)
/// or any zip of one or more skill folders. The listing came later on 2026-09-26 (the user's call:
/// repositories can be large, and past <see cref="Web.WebFetcher.MaxFileDownloadBytes"/> the whole
/// zip was refused, so a skill in a big monorepo could not be installed at all); the zip stays for a
/// zip URL and as the fallback when the API answers no (<see cref="SkillHub.OpenAsync"/>). <see cref="Candidates"/> finds every <c>SKILL.md</c> the
/// way the skills.sh CLI does — anywhere up to <see cref="MaxDepth"/> folders deep, <c>skills/</c>,
/// <c>.claude/skills/</c>, <c>.agents/skills/</c> and the root alike — skipping <c>.git</c> and
/// <c>node_modules</c> (<see cref="SkillCatalog.SkippedFolders"/>).
///
/// <para>The archive is someone else's, so a candidate is vetted whole before anything is written
/// (the all-or-nothing shape of <c>WorkingDirectory.Unzip</c>): a path with <c>..</c>, a drive, a
/// colon, a reserved device name or a trailing dot or space, two paths that differ only by case,
/// or a cap exceeded (<see cref="MaxSkillFiles"/>, <see cref="MaxSkillFileBytes"/>,
/// <see cref="MaxSkillBytes"/>, <see cref="MaxSkillMdBytes"/>) refuses the skill; a symbolic link
/// (git archive writes them as entries) is skipped and named. <see cref="Extract"/> counts the bytes
/// it actually copies, so a header that lies about a size cannot get past the cap either.</para>
/// </summary>
public sealed class SkillArchive : IDisposable
{
    /// <summary>Entries read at most (a zip bomb by count).</summary>
    public const int MaxArchiveEntries = 20_000;

    /// <summary>Files one skill may hold.</summary>
    public const int MaxSkillFiles = 200;

    /// <summary>The largest file of a skill.</summary>
    public const long MaxSkillFileBytes = 5_000_000;

    /// <summary>A skill's files together, uncompressed.</summary>
    public const long MaxSkillBytes = 20_000_000;

    /// <summary>The largest <c>SKILL.md</c> read.</summary>
    public const long MaxSkillMdBytes = 1_000_000;

    /// <summary>Folders deep a <c>SKILL.md</c> is looked for.</summary>
    public const int MaxDepth = 6;

    /// <summary>The SKILL.md files a listing fetches at most to offer a pick; past it the skill has to be named.</summary>
    public const int MaxListedSkills = 100;

    /// <summary>Files of a listing fetched at once.</summary>
    public const int MaxParallelFetches = 6;

    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    private readonly ZipArchive? _zip;
    private readonly Dictionary<string, Entry> _entries;
    private readonly HashSet<string> _links;
    private readonly Func<string, CancellationToken, Task<(byte[]? Bytes, string? Error)>>? _fetch;

    private SkillArchive(ZipArchive? zip, Dictionary<string, Entry> entries, HashSet<string> links, string? commit, Uri? url, Func<string, CancellationToken, Task<(byte[]? Bytes, string? Error)>>? fetch)
    {
        _zip = zip;
        _entries = entries;
        _links = links;
        _fetch = fetch;
        Commit = commit;
        Url = url;
    }

    /// <summary>One file: a zip's entry, or a listing's path whose bytes arrive when fetched.</summary>
    private sealed class Entry(ZipArchiveEntry? zip, long length)
    {
        public ZipArchiveEntry? Zip { get; } = zip;

        public long Length { get; } = length;

        public byte[]? Bytes { get; set; }

        public bool Readable => Zip is not null || Bytes is not null;

        public Stream Open() => Zip?.Open() ?? new MemoryStream(Bytes ?? throw new InvalidOperationException("The file was never fetched."), writable: false);
    }

    /// <summary>The commit the archive was cut from (codeload's archive comment, or the commit the listing is of; 40 hex digits), or null.</summary>
    public string? Commit { get; }

    /// <summary>Where it came from, when the opener said: the zip's URL, or the repository's tree at the commit.</summary>
    public Uri? Url { get; }

    /// <summary>Whether it is a listing whose files are fetched one by one (<see cref="FromTree"/>).</summary>
    public bool Remote => _fetch is not null;

    /// <summary>
    /// A repository's listing: the files (<paramref name="blobs"/>, paths with forward slashes and
    /// their sizes) and the symbolic links, at <paramref name="commit"/>; <paramref name="fetch"/>
    /// brings one path's bytes or the error. No entry cap here, unlike a zip's: a path costs a few
    /// bytes, nothing is inflated, and a big repository is the very case the listing is for.
    /// </summary>
    public static SkillArchive FromTree(IEnumerable<(string Path, long Size)> blobs, IEnumerable<string> links, string commit, Uri url, Func<string, CancellationToken, Task<(byte[]? Bytes, string? Error)>> fetch)
    {
        ArgumentNullException.ThrowIfNull(blobs);
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(fetch);
        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var (path, size) in blobs)
        {
            entries.TryAdd(path, new Entry(null, size));
        }

        return new SkillArchive(null, entries, new HashSet<string>(links, StringComparer.Ordinal), commit, url, fetch);
    }

    /// <summary>Whether <paramref name="bytes"/> open as a zip: the local-header magic <c>PK\x03\x04</c>, or an empty archive's <c>PK\x05\x06</c>.</summary>
    public static bool LooksLikeZip(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 4 && bytes[0] == 'P' && bytes[1] == 'K' && ((bytes[2] == 3 && bytes[3] == 4) || (bytes[2] == 5 && bytes[3] == 6));

    /// <summary>The archive in <paramref name="bytes"/> (from <paramref name="url"/>), or null with the error (not a zip, unreadable, too many entries). Never throws.</summary>
    public static SkillArchive? TryOpen(byte[] bytes, out string? error, Uri? url = null)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        error = null;
        if (!LooksLikeZip(bytes))
        {
            error = SkillInstallText.NotZipError;
            return null;
        }

        ZipArchive? zip = null;
        try
        {
            zip = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
            if (zip.Entries.Count > MaxArchiveEntries)
            {
                error = SkillInstallText.TooManyEntriesError(zip.Entries.Count, MaxArchiveEntries);
                zip.Dispose();
                return null;
            }

            var names = zip.Entries.Select(e => e.FullName.Replace('\\', '/')).ToList();
            string root = CommonRoot(names);
            var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
            var links = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < names.Count; i++)
            {
                string name = names[i][root.Length..];
                if (name.Length == 0 || name.EndsWith('/'))
                {
                    continue;
                }

                var entry = zip.Entries[i];
                if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                {
                    links.Add(name);
                    continue;
                }

                entries.TryAdd(name, new Entry(entry, entry.Length));
            }

            string comment = zip.Comment.Trim();
            string? commit = comment.Length == 40 && comment.All(char.IsAsciiHexDigit) ? comment.ToLowerInvariant() : null;
            return new SkillArchive(zip, entries, links, commit, url, null);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException or ArgumentException)
        {
            zip?.Dispose();
            error = SkillInstallText.UnreadableZipError(ex.Message);
            return null;
        }
    }

    /// <summary>The one top folder every entry sits under, with its slash (<c>repo-sha/</c>); <c>""</c> when there is none.</summary>
    private static string CommonRoot(IReadOnlyList<string> names)
    {
        string? root = null;
        foreach (string name in names)
        {
            int slash = name.IndexOf('/', StringComparison.Ordinal);
            if (slash <= 0)
            {
                return "";
            }

            string first = name[..(slash + 1)];
            if (root is null)
            {
                root = first;
            }
            else if (!string.Equals(root, first, StringComparison.Ordinal))
            {
                return "";
            }
        }

        return root ?? "";
    }

    /// <summary>
    /// Every folder holding a <c>SKILL.md</c>, by folder path, under <paramref name="subPath"/> when
    /// one is given (the folder itself or below it). A candidate's files are those under its folder
    /// less any nested skill's; each is vetted (<see cref="SkillCandidate.Refusal"/>).
    /// </summary>
    /// <remarks>A listing offers only the folders whose SKILL.md was fetched (<see cref="FetchSkillMdsAsync"/>), or is over the cap and refused unread.</remarks>
    public IReadOnlyList<SkillCandidate> Candidates(string? subPath = null)
    {
        var folders = SkillFolders(subPath).Where(f => _entries[SkillMdOf(f)] is var md && (md.Readable || md.Length > MaxSkillMdBytes)).ToList();
        var all = _entries.Keys.Where(k => k.EndsWith("/" + SkillCatalog.FileName, StringComparison.Ordinal) || k == SkillCatalog.FileName)
            .Select(k => k.Length == SkillCatalog.FileName.Length ? "" : k[..^(SkillCatalog.FileName.Length + 1)])
            .ToList();
        return folders.Select(folder => Build(folder, all)).ToList();
    }

    /// <summary>
    /// A listing's SKILL.md files under <paramref name="subPath"/>, fetched so <see cref="Candidates"/>
    /// can read them; with <paramref name="skillId"/>, only the folders of that name when there are
    /// any (a named skill costs one request, not one per skill). Null when done (always, for a zip),
    /// else the error: a fetch that failed, or more than <see cref="MaxListedSkills"/> to fetch.
    /// </summary>
    public async Task<string?> FetchSkillMdsAsync(string? subPath, string? skillId, CancellationToken cancellationToken)
    {
        if (_fetch is null)
        {
            return null;
        }

        var folders = SkillFolders(subPath);
        if (skillId is not null && folders.Where(f => f[(f.LastIndexOf('/') + 1)..] == skillId).ToList() is { Count: > 0 } named)
        {
            folders = named;
        }

        if (folders.Count > MaxListedSkills)
        {
            return SkillInstallText.TooManySkillsError(folders.Count, MaxListedSkills);
        }

        return await FetchAllAsync(folders.Select(SkillMdOf).Where(n => _entries[n].Length <= MaxSkillMdBytes), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A listing's files of <paramref name="candidate"/>, fetched so <see cref="Extract"/> can write them. Null when done (always, for a zip), else the error.</summary>
    public Task<string?> FetchAsync(SkillCandidate candidate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return _fetch is null ? Task.FromResult<string?>(null) : FetchAllAsync(candidate.Files.Select(f => f.EntryName), cancellationToken);
    }

    /// <summary>Fetches every path of <paramref name="names"/> not fetched yet, <see cref="MaxParallelFetches"/> at once; the first error in order, or null.</summary>
    private async Task<string?> FetchAllAsync(IEnumerable<string> names, CancellationToken cancellationToken)
    {
        var wanted = names.Where(n => !_entries[n].Readable).ToList();
        using var gate = new SemaphoreSlim(MaxParallelFetches);
        var errors = await Task.WhenAll(wanted.Select(async name =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var (bytes, error) = await _fetch!(name, cancellationToken).ConfigureAwait(false);
                if (bytes is not null)
                {
                    _entries[name].Bytes = bytes;
                }

                return error;
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);
        return errors.FirstOrDefault(e => e is not null);
    }

    private static string SkillMdOf(string folder) => folder.Length == 0 ? SkillCatalog.FileName : folder + "/" + SkillCatalog.FileName;

    /// <summary>Every folder holding a <c>SKILL.md</c> within <see cref="MaxDepth"/>, outside the skipped folders, under <paramref name="subPath"/> when one is given; sorted.</summary>
    private List<string> SkillFolders(string? subPath)
    {
        string? under = string.IsNullOrWhiteSpace(subPath) ? null : subPath.Trim('/');
        var folders = new List<string>();
        foreach (string name in _entries.Keys)
        {
            string[] segments = name.Split('/');
            if (segments[^1] != SkillCatalog.FileName || segments.Length - 1 > MaxDepth || segments.Any(IsSkippedFolder))
            {
                continue;
            }

            string folder = string.Join('/', segments[..^1]);
            if (under is not null && !(folder == under || folder.StartsWith(under + "/", StringComparison.Ordinal)))
            {
                continue;
            }

            folders.Add(folder);
        }

        folders.Sort(StringComparer.Ordinal);
        return folders;
    }

    /// <summary>The candidate named <paramref name="skillId"/>: by frontmatter name first, then by folder name; ordinal. Null when none.</summary>
    public static SkillCandidate? Match(IReadOnlyList<SkillCandidate> candidates, string skillId)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(skillId);
        return candidates.FirstOrDefault(c => string.Equals(c.Frontmatter?.Name, skillId, StringComparison.Ordinal))
            ?? candidates.FirstOrDefault(c => string.Equals(c.FolderName, skillId, StringComparison.Ordinal));
    }

    private SkillCandidate Build(string folder, IReadOnlyList<string> skillFolders)
    {
        string prefix = folder.Length == 0 ? "" : folder + "/";
        var nested = skillFolders.Where(f => f != folder && (prefix.Length == 0 || f.StartsWith(prefix, StringComparison.Ordinal))).Select(f => f + "/").ToList();
        var files = new List<SkillArchiveFile>();
        var skipped = new List<string>();
        long bytes = 0;
        foreach (var (name, entry) in _entries)
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || nested.Any(n => name.StartsWith(n, StringComparison.Ordinal)))
            {
                continue;
            }

            string relative = name[prefix.Length..];
            if (relative.Split('/')[..^1].Any(IsSkippedFolder))
            {
                continue;
            }

            files.Add(new SkillArchiveFile(relative, name, entry.Length));
            bytes += entry.Length;
        }

        foreach (string link in _links)
        {
            if (link.StartsWith(prefix, StringComparison.Ordinal) && !nested.Any(n => link.StartsWith(n, StringComparison.Ordinal)))
            {
                skipped.Add(link[prefix.Length..]);
            }
        }

        files.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path));
        skipped.Sort(StringComparer.OrdinalIgnoreCase);

        var skillMd = _entries[SkillMdOf(folder)];
        SkillFrontmatter? frontmatter = null;
        string body = "";
        string? refusal = null;
        if (skillMd.Length > MaxSkillMdBytes)
        {
            refusal = SkillInstallText.SkillMdTooBigRefusal(skillMd.Length, MaxSkillMdBytes);
        }
        else if (ReadAll(skillMd, MaxSkillMdBytes) is not { } raw)
        {
            refusal = SkillInstallText.SkillMdTooBigRefusal(MaxSkillMdBytes + 1, MaxSkillMdBytes);
        }
        else if (!SkillFrontmatter.TryParse(WorkingDirectory.Decode(raw, out _), out frontmatter, out body, out string? problem))
        {
            refusal = problem;
        }

        refusal ??= Vet(frontmatter!, files, bytes);
        return new SkillCandidate(folder, frontmatter, body, refusal, files, bytes, skipped);
    }

    /// <summary>Why the skill cannot be installed as it stands, or null: its name, the caps, then each path.</summary>
    private static string? Vet(SkillFrontmatter frontmatter, IReadOnlyList<SkillArchiveFile> files, long bytes)
    {
        if (!SkillFrontmatter.IsValidName(frontmatter.Name))
        {
            return SkillInstallText.BadNameRefusal(frontmatter.Name);
        }

        if (files.Count > MaxSkillFiles)
        {
            return SkillInstallText.TooManyFilesRefusal(files.Count, MaxSkillFiles);
        }

        if (bytes > MaxSkillBytes)
        {
            return SkillInstallText.TooBigRefusal(bytes, MaxSkillBytes);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (file.Length > MaxSkillFileBytes)
            {
                return SkillInstallText.FileTooBigRefusal(file.Path, file.Length, MaxSkillFileBytes);
            }

            if (!IsSafePath(file.Path))
            {
                return SkillInstallText.UnsafePathRefusal(file.Path);
            }

            if (!seen.Add(file.Path))
            {
                return SkillInstallText.CaseCollisionRefusal(file.Path);
            }
        }

        return null;
    }

    /// <summary>A relative path every segment of which a Windows folder may hold: no <c>.</c> / <c>..</c>, no drive or colon, no invalid character, no reserved device name, no trailing dot or space. Pure.</summary>
    public static bool IsSafePath(string relative)
    {
        ArgumentNullException.ThrowIfNull(relative);
        if (relative.Length == 0 || relative[0] == '/')
        {
            return false;
        }

        char[] invalid = Path.GetInvalidFileNameChars();
        foreach (string segment in relative.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.IndexOfAny(invalid) >= 0 || segment.Contains(':', StringComparison.Ordinal)
                || segment[^1] is '.' or ' ')
            {
                return false;
            }

            int dot = segment.IndexOf('.', StringComparison.Ordinal);
            string stem = dot < 0 ? segment : segment[..dot];
            if (ReservedNames.Contains(stem.TrimEnd(), StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Writes <paramref name="file"/> to <paramref name="target"/>, refusing (false, the file removed) a body past <see cref="MaxSkillFileBytes"/> or longer than declared.</summary>
    public bool Extract(SkillArchiveFile file, string target)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(target);
        var entry = _entries[file.EntryName];
        long limit = Math.Min(MaxSkillFileBytes, file.Length);
        bool ok;
        using (var input = entry.Open())
        using (var output = File.Create(target))
        {
            ok = CopyCapped(input, output, limit);
        }

        if (!ok)
        {
            File.Delete(target);
        }

        return ok;
    }

    private static bool CopyCapped(Stream input, Stream output, long limit)
    {
        byte[] buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > limit)
            {
                return false;
            }

            output.Write(buffer, 0, read);
        }

        return true;
    }

    private static byte[]? ReadAll(Entry entry, long limit)
    {
        using var input = entry.Open();
        using var buffer = new MemoryStream();
        return CopyCapped(input, buffer, limit) ? buffer.ToArray() : null;
    }

    private static bool IsSkippedFolder(string segment) =>
        SkillCatalog.SkippedFolders.Contains(segment, StringComparer.OrdinalIgnoreCase);

    public void Dispose() => _zip?.Dispose();
}
