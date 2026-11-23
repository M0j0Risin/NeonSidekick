using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Sql;

namespace NeonSidekick.Skills;

/// <summary>One folder's trust in <c>project-trust.json</c>: the kinds trusted (only <see cref="ProjectSkillTrust.SkillsKind"/> today), and the skills disabled there.</summary>
public sealed class ProjectTrustEntry
{
    /// <summary>The folder's full path as <see cref="ProjectSkillTrust.KeyOf"/> spells it.</summary>
    [JsonPropertyName("folder")]
    public string Folder { get; set; } = "";

    [JsonPropertyName("kinds")]
    public Dictionary<string, ProjectTrustKind> Kinds { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The names of this folder's project skills the user disabled (kept in the profile, never a marker in the repo).</summary>
    [JsonPropertyName("disabled")]
    public List<string> Disabled { get; set; } = [];
}

/// <summary>One kind's trust: the folder's fingerprint when it was given, when, and each skill's own hash by its key (<c>.agents/skills/x</c>).</summary>
public sealed class ProjectTrustKind
{
    [JsonPropertyName("fingerprint")]
    public string Fingerprint { get; set; } = "";

    [JsonPropertyName("trustedAt")]
    public string TrustedAt { get; set; } = "";

    [JsonPropertyName("skills")]
    public Dictionary<string, string> Skills { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// A folder's skills as a trust question read them: the fingerprint, each skill's hash and the <c>SKILL.md</c> texts to keep as copies, and every skill
/// folder's key in the scan's order (past the 50 cap too; <see cref="Hashes"/> holds the first 50's).
/// </summary>
public sealed record ProjectFolderState(string Fingerprint, IReadOnlyDictionary<string, string> Hashes, IReadOnlyDictionary<string, string> Copies, IReadOnlyList<string>? Keys = null)
{
    /// <summary>The keys in order: <see cref="Keys"/>, or the hashed ones' when none were given.</summary>
    public IReadOnlyList<string> Order => Keys ?? Hashes.Keys.ToList();
}

/// <summary>What a trust save did.</summary>
public enum ProjectTrustSave
{
    /// <summary>Saved.</summary>
    Saved,

    /// <summary>The folder changed since the question read it: nothing saved, ask again.</summary>
    Changed,
}

/// <summary>
/// Which folders' skills this profile trusts (2026-11-23, feature idea #16, the user's calls): <c>configs/project-trust.json</c>, and beside it
/// <c>configs/project-trust/</c>, a copy of each trusted <c>SKILL.md</c> the re-trust question diffs against.
/// <list type="bullet">
/// <item>Keys are full paths (<see cref="KeyOf"/>: <see cref="WriteGuard"/>'s trim, NFC on a Mac), found by a scan with <see cref="CaseRule.Comparison"/>,
/// never a dictionary lookup: the case rule is per volume. A save keeps one entry per folder, a case twin merged (the newest <c>trustedAt</c>).</item>
/// <item>Two copies of the app on one profile (the screen and a headless run): a read checks the file's stamp and re-reads when it moved, as
/// <see cref="Mcp.McpTokenStore"/> does; a write re-reads under <see cref="ConnectionsFileEdit.Hold"/> and changes only its own folder's entry.
/// Reads never wait (a zero-wait hold only to move a broken file aside); a save that can't hold the file throws <see cref="IOException"/> and grants
/// nothing. <c>Hold</c> is thread-affine and its wait sleeps: the callers run a save in one <c>Task.Run</c>, never on the screen thread.</item>
/// <item>A file that won't parse is moved to <c>project-trust.json.bad</c> and read as nothing trusted. Both are left out of the settings zip.</item>
/// <item>An entry whose folder is gone from a volume that is there is pruned at the next save; one on an unplugged drive or an offline share is kept.</item>
/// </list>
/// </summary>
public sealed class ProjectSkillTrust
{
    /// <summary>The file's name under the profile's <c>configs</c>.</summary>
    public const string FileName = "project-trust.json";

    /// <summary>The copies folder's name beside it.</summary>
    public const string CopiesFolderName = "project-trust";

    /// <summary>A broken file's suffix.</summary>
    public const string BadSuffix = ".bad";

    /// <summary>The one kind this branch writes. A future kind gets its own question: trusting skills never grants anything else.</summary>
    public const string SkillsKind = "skills";

    /// <summary>A project <c>SKILL.md</c> larger than this is never read (listed as a problem), before any trust.</summary>
    public const long MaxSkillFileBytes = 1024 * 1024;

    /// <summary>Content bytes one skill's hash reads (<see cref="ProjectSkillHasher"/>); past it a file counts by its stamp.</summary>
    public const long SkillHashBudget = 128 * 1024;

    /// <summary>At most this many project skills are offered, in scan order.</summary>
    public const int ProjectSkillCap = 50;

    private readonly record struct FileStamp(DateTime Written, long Length);

    private readonly Func<string> _configs;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private string? _path;
    private FileStamp? _stamp;
    private List<ProjectTrustEntry> _entries = [];
    private bool _loaded;

    /// <param name="configs">The loaded profile's <c>configs</c> folder, read at each call: a profile switch moves it.</param>
    public ProjectSkillTrust(Func<string> configs, TimeProvider? time = null)
    {
        _configs = configs ?? throw new ArgumentNullException(nameof(configs));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The trust file's path right now.</summary>
    public string FilePath => Path.Combine(_configs(), FileName);

    /// <summary>The copies folder's path right now.</summary>
    public string CopiesPath => Path.Combine(_configs(), CopiesFolderName);

    /// <summary><paramref name="folder"/>'s trust of skills, or null; read again when the file moved.</summary>
    public ProjectTrustKind? Trusted(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        Refresh();
        lock (_gate)
        {
            return Find(_entries, KeyOf(folder))?.Kinds.GetValueOrDefault(SkillsKind);
        }
    }

    /// <summary>The names of <paramref name="folder"/>'s project skills the user disabled.</summary>
    public IReadOnlySet<string> DisabledIn(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        Refresh();
        lock (_gate)
        {
            return Find(_entries, KeyOf(folder)) is { } entry ? entry.Disabled.ToHashSet(StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
        }
    }

    /// <summary>The kept copy of the trusted <c>SKILL.md</c> for <paramref name="key"/> (<c>.agents/skills/x</c>) in <paramref name="folder"/>, or null.</summary>
    public string? CopyOf(string folder, string key)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(key);
        try
        {
            string file = Path.Combine(CopiesPath, FolderHash(KeyOf(folder)), FileHash(key));
            return File.Exists(file) ? File.ReadAllText(file) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Trusts <paramref name="folder"/>'s skills as the question showed them (<paramref name="shown"/>). Inside the hold, <paramref name="read"/> reads the
    /// folder again: when its fingerprint differs nothing is saved (<see cref="ProjectTrustSave.Changed"/>), so a version nobody saw is never trusted.
    /// Otherwise the copies are written into a temp folder swapped in, then the entry. Synchronous; throws <see cref="IOException"/> when the file
    /// can't be held or written, and nothing is trusted.
    /// </summary>
    public ProjectTrustSave Trust(string folder, string shown, Func<ProjectFolderState> read)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(shown);
        ArgumentNullException.ThrowIfNull(read);
        string key = KeyOf(folder);
        var result = ProjectTrustSave.Saved;
        int count = 0;
        Write(entries =>
        {
            var state = read();
            count = state.Hashes.Count;
            if (!string.Equals(state.Fingerprint, shown, StringComparison.Ordinal))
            {
                result = ProjectTrustSave.Changed;
                return false;
            }

            WriteCopies(key, state.Copies);
            var entry = Find(entries, key) ?? Add(entries, key);
            entry.Kinds[SkillsKind] = new ProjectTrustKind
            {
                Fingerprint = state.Fingerprint,
                TrustedAt = _time.GetUtcNow().UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
                Skills = new Dictionary<string, string>(state.Hashes, StringComparer.Ordinal),
            };
            return true;
        });

        if (result == ProjectTrustSave.Saved)
        {
            DiagnosticLog.Info(SkillCatalog.Category, $"Project skills trusted: {key} ({count.ToString(CultureInfo.InvariantCulture)} skills).");
        }

        return result;
    }

    /// <summary>Takes back <paramref name="folder"/>'s trust and deletes its copies; its disabled list stays for a later trust. Throws as a save does.</summary>
    public void Untrust(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        string key = KeyOf(folder);
        Write(entries =>
        {
            if (Find(entries, key) is not { } entry)
            {
                return false;
            }

            entry.Kinds.Remove(SkillsKind);
            DeleteCopies(key);
            if (entry.Kinds.Count == 0 && entry.Disabled.Count == 0)
            {
                entries.Remove(entry);
            }

            return true;
        });
        DiagnosticLog.Info(SkillCatalog.Category, $"Project skills untrusted: {key}.");
    }

    /// <summary>Disables or enables the project skill <paramref name="name"/> in <paramref name="folder"/>, kept in the profile. Throws as a save does.</summary>
    public void SetDisabled(string folder, string name, bool disabled)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(name);
        string key = KeyOf(folder);
        Write(entries =>
        {
            var entry = Find(entries, key);
            if (disabled)
            {
                entry ??= Add(entries, key);
                if (!entry.Disabled.Contains(name, StringComparer.Ordinal))
                {
                    entry.Disabled.Add(name);
                }

                return true;
            }

            if (entry is null || entry.Disabled.RemoveAll(n => string.Equals(n, name, StringComparison.Ordinal)) == 0)
            {
                return false;
            }

            if (entry.Kinds.Count == 0 && entry.Disabled.Count == 0)
            {
                entries.Remove(entry);
            }

            return true;
        });
    }

    /// <summary>
    /// A folder as an entry's key: the full path, trailing separator off, NFC on a Mac (<see cref="WriteGuard"/>'s spelling). Pure apart from
    /// <see cref="Path.GetFullPath(string)"/>.
    /// </summary>
    public static string KeyOf(string folder)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return OperatingSystem.IsMacOS() ? full.Normalize(NormalizationForm.FormC) : full;
    }

    /// <summary>
    /// The volume <paramref name="path"/> lies on: on Windows its root (<c>D:\</c>, <c>\\server\share</c>); elsewhere <c>/Volumes/&lt;name&gt;</c> for a
    /// path under it, else <c>/</c>. An entry is pruned only while its volume is there. Pure.
    /// </summary>
    public static string VolumeOf(string path, bool? windows = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (windows ?? OperatingSystem.IsWindows())
        {
            return Path.GetPathRoot(path) ?? path;
        }

        const string volumes = "/Volumes/";
        if (path.StartsWith(volumes, StringComparison.Ordinal))
        {
            int end = path.IndexOf('/', volumes.Length);
            return end < 0 ? path : path[..end];
        }

        return "/";
    }

    /// <summary>A folder key's copies folder name: the first 32 hex of its SHA-256.</summary>
    public static string FolderHash(string key) => ProjectSkillHasher.Hex(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];

    /// <summary>A skill key's copy file name: the first 32 hex of its SHA-256, <c>.md</c> (never the skill's name: a repo's <c>name: ../x</c> is no path).</summary>
    public static string FileHash(string key) => ProjectSkillHasher.Hex(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32] + ".md";

    // ── The file ─────────────────────────────────────────────────────────

    private static ProjectTrustEntry? Find(List<ProjectTrustEntry> entries, string key) =>
        entries.FirstOrDefault(e => string.Equals(e.Folder, key, CaseRule.Comparison(key)));

    private static ProjectTrustEntry Add(List<ProjectTrustEntry> entries, string key)
    {
        var entry = new ProjectTrustEntry { Folder = key };
        entries.Add(entry);
        return entry;
    }

    // The read before a lookup: the file again when its path or stamp moved; a broken one reads as nothing trusted and is moved aside with no wait.
    private void Refresh()
    {
        bool corrupt = false;
        string path = FilePath;
        lock (_gate)
        {
            var stamp = Stamp(path);
            if (_loaded && string.Equals(_path, path, StringComparison.Ordinal) && stamp == _stamp)
            {
                return;
            }

            var (entries, kind) = TryRead(path);
            if (kind == ReadKind.Unreadable)
            {
                // Keep what was read before (a passing failure); a profile switch's unreadable file trusts nothing.
                if (!string.Equals(_path, path, StringComparison.Ordinal))
                {
                    _entries = [];
                    _path = path;
                    _loaded = true;
                }

                return;
            }

            _entries = entries ?? [];
            _path = path;
            _stamp = stamp;
            _loaded = true;
            corrupt = kind == ReadKind.Corrupt;
        }

        if (corrupt)
        {
            try
            {
                using (ConnectionsFileEdit.Hold(path, TimeSpan.Zero))
                {
                    if (TryRead(path).Kind == ReadKind.Corrupt)
                    {
                        MoveAside(path);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DiagnosticLog.Debug(SkillCatalog.Category, $"Could not move {path} aside yet: {LogText.Excerpt(ex.Message)}");
            }
        }
    }

    private enum ReadKind
    {
        Ok,
        Corrupt,
        Unreadable,
    }

    private static (List<ProjectTrustEntry>? Entries, ReadKind Kind) TryRead(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return ([], ReadKind.Ok);
            }

            var read = JsonSerializer.Deserialize(File.ReadAllText(path), SkillsJsonContext.Default.ListProjectTrustEntry);
            return ((read ?? []).Where(e => e is not null && e.Folder.Length > 0).ToList(), ReadKind.Ok);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            DiagnosticLog.Warn(SkillCatalog.Category, $"{path} won't parse, so no folder's skills are trusted: {LogText.Excerpt(ex.Message)}");
            return (null, ReadKind.Corrupt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn(SkillCatalog.Category, $"Could not read {path}: {LogText.Excerpt(ex.Message)}");
            return (null, ReadKind.Unreadable);
        }
    }

    private static void MoveAside(string path)
    {
        File.Move(path, path + BadSuffix, overwrite: true);
        DiagnosticLog.Warn(SkillCatalog.Category, $"{path} was moved to {Path.GetFileName(path) + BadSuffix}; no folder's skills are trusted.");
    }

    private static FileStamp? Stamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new FileStamp(info.LastWriteTimeUtc, info.Length) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // One read-modify-write under the hold: the file read again strictly (an IO failure throws), change applied, twins merged, gone folders pruned,
    // written atomically; nothing written when change says so.
    private void Write(Func<List<ProjectTrustEntry>, bool> change)
    {
        string path = FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            using (ConnectionsFileEdit.Hold(path))
            {
                var (read, kind) = TryRead(path);
                if (kind == ReadKind.Unreadable)
                {
                    throw new IOException($"Could not read {path}.");
                }

                if (kind == ReadKind.Corrupt)
                {
                    MoveAside(path);
                }

                var entries = read ?? [];
                if (!change(entries))
                {
                    return;
                }

                entries = Tidy(entries);
                SweepOrphanCopies(entries);
                string json = JsonSerializer.Serialize(entries, SkillsJsonContext.Default.ListProjectTrustEntry);
                WorkingDirectory.WriteAtomically(path, Encoding.UTF8.GetBytes(json));
                lock (_gate)
                {
                    _entries = entries;
                    _path = path;
                    _stamp = Stamp(path);
                    _loaded = true;
                }
            }
        }
        catch (IOException ex)
        {
            DiagnosticLog.Warn(SkillCatalog.Category, $"Could not save {path}: {LogText.Excerpt(ex.Message)}");
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            DiagnosticLog.Warn(SkillCatalog.Category, $"Could not save {path}: {LogText.Excerpt(ex.Message)}");
            throw new IOException(ex.Message, ex);
        }
    }

    // One entry per folder (a case twin merged, the newest trust kept, the dropped twin's copies deleted), and a folder gone from a volume that is
    // there pruned with its copies. Runs inside the save's hold, off the screen thread (an offline share's Exists may wait out SMB's timeout).
    private List<ProjectTrustEntry> Tidy(List<ProjectTrustEntry> entries)
    {
        var kept = new List<ProjectTrustEntry>();
        foreach (var entry in entries)
        {
            entry.Folder = KeyOf(entry.Folder);
            if (Find(kept, entry.Folder) is { } twin)
            {
                var newer = Newer(entry, twin);
                var older = ReferenceEquals(newer, entry) ? twin : entry;
                foreach (string name in older.Disabled.Where(n => !newer.Disabled.Contains(n, StringComparer.Ordinal)))
                {
                    newer.Disabled.Add(name);
                }

                if (!string.Equals(older.Folder, newer.Folder, StringComparison.Ordinal))
                {
                    DeleteCopies(older.Folder);
                }

                kept.Remove(twin);
                kept.Add(newer);
                continue;
            }

            if (Gone(entry.Folder))
            {
                DeleteCopies(entry.Folder);
                continue;
            }

            kept.Add(entry);
        }

        return kept;
    }

    private static ProjectTrustEntry Newer(ProjectTrustEntry a, ProjectTrustEntry b)
    {
        string at = a.Kinds.GetValueOrDefault(SkillsKind)?.TrustedAt ?? "";
        string bt = b.Kinds.GetValueOrDefault(SkillsKind)?.TrustedAt ?? "";
        return string.CompareOrdinal(at, bt) >= 0 ? a : b;
    }

    private static bool Gone(string folder)
    {
        try
        {
            return Directory.Exists(VolumeOf(folder)) && !Directory.Exists(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    // The copies go into a temp folder swapped in, inside the save's hold, before the entry is written: an entry never points at copies that aren't there.
    private void WriteCopies(string key, IReadOnlyDictionary<string, string> copies)
    {
        string root = CopiesPath;
        string final = Path.Combine(root, FolderHash(key));
        string temp = final + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        Directory.CreateDirectory(temp);
        foreach (var (skillKey, text) in copies)
        {
            string capped = text.Length <= SkillCatalog.MaxBodyChars ? text : text[..SkillCatalog.MaxBodyChars];
            WorkingDirectory.WriteAtomically(Path.Combine(temp, FileHash(skillKey)), Encoding.UTF8.GetBytes(capped));
        }

        if (Directory.Exists(final))
        {
            Directory.Delete(final, recursive: true);
        }

        Directory.Move(temp, final);
        foreach (string stale in Directory.EnumerateDirectories(root, "*.tmp-*"))
        {
            TryDelete(stale);
        }
    }

    // Copies with no trusted entry (a crash between the swap and the entry's write) go at the next save.
    private void SweepOrphanCopies(List<ProjectTrustEntry> entries)
    {
        string root = CopiesPath;
        try
        {
            if (!Directory.Exists(root))
            {
                return;
            }

            var wanted = entries.Where(e => e.Kinds.ContainsKey(SkillsKind)).Select(e => FolderHash(e.Folder)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string folder in Directory.EnumerateDirectories(root))
            {
                string name = Path.GetFileName(folder);
                if (!name.Contains(".tmp-", StringComparison.Ordinal) && !wanted.Contains(name))
                {
                    TryDelete(folder);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Debug(SkillCatalog.Category, $"Could not tidy {root}: {LogText.Excerpt(ex.Message)}");
        }
    }

    private void DeleteCopies(string key) => TryDelete(Path.Combine(CopiesPath, FolderHash(key)));

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Debug(SkillCatalog.Category, $"Could not delete {folder}: {LogText.Excerpt(ex.Message)}");
        }
    }
}
