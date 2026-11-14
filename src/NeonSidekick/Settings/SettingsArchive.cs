using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using NeonSidekick.App;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Help;
using NeonSidekick.Skills;
using NeonSidekick.Updates;

namespace NeonSidekick.Settings;

/// <summary>The <c>neon-settings.json</c> at a settings zip's root. <see cref="Profiles"/> is for a person reading the zip; the import goes by the entries.</summary>
public sealed record SettingsArchiveManifest
{
    [JsonPropertyName("format")]
    public int Format { get; init; }

    [JsonPropertyName("app")]
    public string App { get; init; } = "";

    [JsonPropertyName("os")]
    public string Os { get; init; } = "";

    [JsonPropertyName("created")]
    public string Created { get; init; } = "";

    [JsonPropertyName("profiles")]
    public List<string> Profiles { get; init; } = [];
}

/// <summary>
/// One line of a report: a notice, in the warning tone when <see cref="Warn"/>. <see cref="Command"/> marks a line meant to be copied (a bare
/// import's <c>/settings import &lt;name&gt;</c>), which the screen writes without the profile glyph (the review's #84).
/// </summary>
public sealed record SettingsReportLine(string Text, bool Warn = false, bool Command = false);

/// <summary>What an export or import said: its lines, or why it did nothing (<see cref="Error"/>). Both null-free; a cancel has neither.</summary>
public sealed record SettingsTransferOutcome(IReadOnlyList<SettingsReportLine> Lines, string? Error)
{
    public static SettingsTransferOutcome Failed(string error) => new([], error);
}

/// <summary>
/// The import's view of this computer (<see cref="Live"/> in the app; fakes in tests): its OS, the file and folder checks, the secret reader,
/// its default shell, and this app's version (the review's #59: the import read <c>SidekickApp.Version</c> itself while the export took it).
/// </summary>
public sealed record ImportEnvironment(string ThisOs, Func<string, bool> FileExists, Func<string, bool> DirectoryExists, Func<string, string?> Reveal, string PlatformShell, string AppVersion)
{
    public static ImportEnvironment Live => new(SettingsArchive.ThisOs, File.Exists, Directory.Exists, SettingsSecrets.Reveal, Shell.ShellKinds.PlatformDefault, SidekickApp.Version);
}

/// <summary>
/// <c>/settings export</c> and <c>/settings import</c> (2026-11-13, plan parsed-yawning-papert, the user's ask: carry the settings to another
/// computer, config files only, a small footprint; the fuller <c>/backup</c> of 2026-11-11 was dropped for its restore's cost). A zip of the
/// home's own relative paths and a <c>neon-settings.json</c>; one allowlist (<see cref="IsExported"/>) both sides use.
///
/// <para>The import never overwrites, so nothing is swapped: each profile lands as a new one (<c>&lt;name&gt;-imported</c> when the name is a
/// folder here already), and a home-level item is written only where none is, the unit being the item (a skill folder, a theme or voice by the
/// name its catalog gives it, a ComfyUI workflow's pair, a command or configs file by its path). Secrets travel encrypted as they are and the
/// import blanks any it can't read (<see cref="SettingsImportFixups"/>). The whole zip is vetted before anything is written; profiles and
/// skill folders are staged beside their destination (<c>.import-*</c>) and moved in whole.</para>
/// </summary>
public static class SettingsArchive
{
    public const string ManifestName = "neon-settings.json";
    public const int Format = 1;
    public const int MaxEntries = 5_000;
    /// <summary>The size limit, in the 1000s the app's sizes are told in, so it reads "100 MB" (the review's #78).</summary>
    public const long MaxBytes = 100_000_000;
    public const string WindowsOs = "windows";
    public const string MacOs = "macos";

    /// <summary>The stage folders' prefix, beside each destination; a dot name no catalog lists.</summary>
    public const string StagePrefix = ".import-";

    /// <summary>
    /// The home file of the zips <c>--import-settings</c> has taken, one SHA-256 a line (2026-11-13, the review's #3, the user's pick "remember
    /// and skip": a shortcut or script with the flag added the profiles again at every launch). A dot name, so the export never carries it.
    /// </summary>
    public const string StartMarkerName = ".settings-imported";

    /// <summary>How old a leftover stage must be before an import deletes it: a younger one may be another running copy's (the review's #5).</summary>
    private static readonly TimeSpan StageAge = TimeSpan.FromHours(1);

    /// <summary>
    /// The folders inside a skill the zip never carries: the catalog's (<see cref="SkillCatalog.SkippedFolders"/>) and a skill's packages and build
    /// output (2026-11-13, the review's #45, the user's pick: export and import only; the catalog's own list is unchanged).
    /// </summary>
    internal static readonly string[] SkillSkippedFolders = [.. SkillCatalog.SkippedFolders, ".venv", "venv", "__pycache__"];

    private const string Category = SettingsTransferText.Category;
    private const long MaxManifestBytes = 64 * 1024;

    /// <summary>The system this build runs on, as the manifest says it.</summary>
    public static string ThisOs => OperatingSystem.IsWindows() ? WindowsOs : MacOs;

    private static readonly string[] ProfileTopFiles = { Profiles.FileName, Llm.PersonaFile.FileName, Llm.OperataFile.FileName, Llm.VocaliaFile.FileName };
    private static readonly string[] JunkNames = { ".DS_Store", "Thumbs.db", "desktop.ini" };
    private static readonly char[] WindowsInvalid = { '<', '>', ':', '"', '|', '?', '*', '\\' };

    private const string ConfigsName = Profiles.ConfigsDirectoryName;
    private const string SkillsName = SkillRoots.DirectoryName;
    private const string CommandsName = CustomCommands.CustomCommandRoots.DirectoryName;
    private const string ComfyName = Comfy.ComfyWorkflowCatalog.DirectoryName;
    private const string ThemesName = UI.ThemeCatalog.DirectoryName;
    private const string VoicesName = Speech.VoicePresets.DirectoryName;

    // ─── the allowlist ───────────────────────────────────────────────────────────

    /// <summary>
    /// Whether <paramref name="relative"/> (a path under the home, <c>/</c>-separated) is a settings file the zip carries: the home's
    /// <c>configs/*.json</c>, themes and voice presets (top level and one non-dot subfolder), and skills, commands and ComfyUI workflows as their
    /// catalogs read them; each valid, non-temporary profile's <c>profile.json</c>, prompt files, <c>configs/*.json</c> but the browser's
    /// history, and its own skills, commands and workflows. Extensions and fixed names match ignoring case, as the catalogs read them. Pure.
    /// </summary>
    public static bool IsExported(string relative)
    {
        ArgumentNullException.ThrowIfNull(relative);
        string[] s = relative.Split('/');
        if (s.Length == 0 || s.Any(p => p.Length == 0) || IsJunk(s[^1]))
        {
            return false;
        }

        if (Is(s[0], Profiles.DirectoryName))
        {
            return s.Length >= 3 && Profiles.IsValidName(s[1]) && !Profiles.IsTemporary(s[1]) && IsProfileItem(s[2..]);
        }

        if (Is(s[0], ConfigsName))
        {
            return s.Length == 2 && HasExtension(s[1], ".json");
        }

        if (Is(s[0], ThemesName) || Is(s[0], VoicesName))
        {
            return (s.Length == 2 && HasExtension(s[1], ".json")) || (s.Length == 3 && !s[1].StartsWith('.') && HasExtension(s[2], ".json"));
        }

        return IsCatalogItem(s);
    }

    private static bool IsProfileItem(string[] s)
    {
        if (s.Length == 1)
        {
            return ProfileTopFiles.Any(f => Is(s[0], f));
        }

        if (Is(s[0], ConfigsName))
        {
            return s.Length == 2 && HasExtension(s[1], ".json") && !Is(s[1], Viewer.BrowserHistory.FileName);
        }

        return IsCatalogItem(s);
    }

    /// <summary>A skill's file (its folder one level down, never under <c>.git</c> or <c>node_modules</c>), a command, or a workflow file.</summary>
    private static bool IsCatalogItem(string[] s)
    {
        if (Is(s[0], SkillsName))
        {
            return s.Length >= 3 && !s[1].StartsWith('.') && !s[1..^1].Any(p => SkillSkippedFolders.Contains(p, StringComparer.OrdinalIgnoreCase));
        }

        if (Is(s[0], CommandsName))
        {
            return s.Length >= 2 && s.Length <= 2 + CustomCommands.CustomCommandName.MaxDepth && !s[1..].Any(p => p.StartsWith('.'))
                && HasExtension(s[^1], CustomCommands.CustomCommandName.Extension);
        }

        if (Is(s[0], ComfyName))
        {
            return s.Length == 2 && !s[1].StartsWith('.') && (HasExtension(s[1], ".json") || HasExtension(s[1], ".md"));
        }

        return false;
    }

    /// <summary>OS junk and temp files, never carried; a Mac's AppleDouble <c>._x</c> beside a copied file among them (the review's #91).</summary>
    internal static bool IsJunk(string name) =>
        JunkNames.Contains(name, StringComparer.OrdinalIgnoreCase) || HasExtension(name, ".tmp") || name.StartsWith("._", StringComparison.Ordinal);

    /// <summary>
    /// A relative path that lands on either system: <see cref="SkillArchive.IsSafePath"/> (no <c>..</c>, root, drive or device name) plus the
    /// characters Windows refuses, fixed, since on a Mac <c>Path.GetInvalidFileNameChars</c> is only <c>/</c> and NUL. Pure.
    /// </summary>
    public static bool IsPortablePath(string relative)
    {
        ArgumentNullException.ThrowIfNull(relative);
        return SkillArchive.IsSafePath(relative)
            && relative.Split('/').All(segment => segment.IndexOfAny(WindowsInvalid) < 0 && !segment.Any(char.IsControl));
    }

    // ─── export ──────────────────────────────────────────────────────────────────

    /// <summary>A file the export carries: its path in the zip, on disk, and its length.</summary>
    internal sealed record ExportFile(string Relative, string FullPath, long Length);

    /// <summary>What the export found: the files, the profiles they come from, and what it left out (named in the report).</summary>
    internal sealed record ExportCollection(List<ExportFile> Files, List<string> Profiles, List<string> LeftOut);

    /// <summary>Walks <paramref name="home"/> for the allowlist's files; links, unknown folders and loose files are left out and named.</summary>
    internal static ExportCollection Collect(string home)
    {
        string root = Path.GetFullPath(home);
        var c = new ExportCollection([], [], []);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root))
        {
            return c;
        }

        var known = Known.Value;
        foreach (var dir in new DirectoryInfo(root).EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            string name = dir.Name;
            if (Is(name, Profiles.DirectoryName))
            {
                // A case-sensitive volume can hold Work and work; the import would merge them, so the later one is left out whole (the review's #106).
                var profileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var profile in dir.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.Name, StringComparer.Ordinal))
                {
                    if (!Profiles.IsValidName(profile.Name) || Profiles.IsTemporary(profile.Name))
                    {
                        continue;
                    }

                    string prefix = Profiles.DirectoryName + "/" + profile.Name + "/";
                    if (!profileNames.Add(profile.Name))
                    {
                        c.LeftOut.Add(prefix + SettingsTransferText.CaseNote);
                        continue;
                    }

                    if (IsLink(profile))
                    {
                        c.LeftOut.Add(prefix + SettingsTransferText.LinkNote);
                        continue;
                    }

                    int before = c.Files.Count;
                    CollectProfile(profile, prefix, c, seen, known);
                    if (c.Files.Count > before)
                    {
                        c.Profiles.Add(profile.Name);
                    }
                }
            }
            else if (Is(name, ConfigsName) || Is(name, ThemesName) || Is(name, VoicesName) || Is(name, SkillsName) || Is(name, CommandsName) || Is(name, ComfyName))
            {
                CollectFolder(dir, name + "/", c, seen);
            }
            else if (!name.StartsWith('.') && !known.HomeFolders.Contains(name))
            {
                c.LeftOut.Add(name + "/");
            }
        }

        foreach (var file in new DirectoryInfo(root).EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!file.Name.StartsWith('.') && !IsJunk(file.Name) && !known.HomeFiles.Contains(file.Name))
            {
                c.LeftOut.Add(file.Name);
            }
        }

        return c;
    }

    private static void CollectProfile(DirectoryInfo profile, string prefix, ExportCollection c, HashSet<string> seen, KnownNames known)
    {
        foreach (var file in profile.EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (IsExported(prefix + file.Name))
            {
                Add(file, prefix + file.Name, c, seen);
            }
            else if (!file.Name.StartsWith('.') && !IsJunk(file.Name) && !known.ProfileFiles.Contains(file.Name))
            {
                c.LeftOut.Add(prefix + file.Name);
            }
        }

        foreach (var dir in profile.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (Is(dir.Name, ConfigsName) || Is(dir.Name, SkillsName) || Is(dir.Name, CommandsName) || Is(dir.Name, ComfyName))
            {
                CollectFolder(dir, prefix + dir.Name + "/", c, seen);
            }
            else if (!dir.Name.StartsWith('.') && !known.ProfileFolders.Contains(dir.Name))
            {
                c.LeftOut.Add(prefix + dir.Name + "/");
            }
        }
    }

    /// <summary>One of the allowlist's folders (<c>configs</c>, <c>themes</c>, <c>voices</c>, <c>skills</c>, <c>commands</c>, <c>comfy</c>), at the home's or a profile's top.</summary>
    private static void CollectFolder(DirectoryInfo dir, string prefix, ExportCollection c, HashSet<string> seen)
    {
        // A linked top folder (a junctioned skills/ or configs/) is left out as a nested link is (the review's #20).
        if (IsLink(dir))
        {
            c.LeftOut.Add(prefix + SettingsTransferText.LinkNote);
            return;
        }

        string kind = dir.Name;
        if (Is(kind, SkillsName))
        {
            foreach (var skill in dir.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                // A dot folder (an import's stage, a .git) stays silent; a folder without SKILL.md is named (the review's #89).
                if (skill.Name.StartsWith('.'))
                {
                    continue;
                }

                if (IsLink(skill))
                {
                    c.LeftOut.Add(prefix + skill.Name + "/" + SettingsTransferText.LinkNote);
                    continue;
                }

                if (!File.Exists(Path.Combine(skill.FullName, SkillCatalog.FileName)))
                {
                    c.LeftOut.Add(prefix + skill.Name + "/" + SettingsTransferText.NoSkillFileNote);
                    continue;
                }

                CollectTree(skill, prefix + skill.Name + "/", int.MaxValue, skipDot: false, skill: true, c, seen);
            }

            return;
        }

        if (Is(kind, CommandsName))
        {
            CollectTree(dir, prefix, CustomCommands.CustomCommandName.MaxDepth, skipDot: true, skill: false, c, seen);
            return;
        }

        bool configs = Is(kind, ConfigsName);
        bool comfy = Is(kind, ComfyName);
        var files = dir.EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var jsonStems = comfy
            ? files.Where(f => HasExtension(f.Name, ".json")).Select(f => Path.GetFileNameWithoutExtension(f.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;
        foreach (var file in files)
        {
            string relative = prefix + file.Name;
            if (jsonStems is not null && HasExtension(file.Name, ".md") && !jsonStems.Contains(Path.GetFileNameWithoutExtension(file.Name)))
            {
                continue;
            }

            if (IsExported(relative))
            {
                Add(file, relative, c, seen);
            }
            else if (configs && !IsJunk(file.Name) && !Is(file.Name, Viewer.BrowserHistory.FileName))
            {
                c.LeftOut.Add(relative);
            }
        }

        if (Is(kind, ThemesName) || Is(kind, VoicesName))
        {
            foreach (var sub in dir.EnumerateDirectories().Where(d => !d.Name.StartsWith('.')).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (IsLink(sub))
                {
                    c.LeftOut.Add(prefix + sub.Name + "/" + SettingsTransferText.LinkNote);
                    continue;
                }

                foreach (var file in sub.EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                {
                    if (IsExported(prefix + sub.Name + "/" + file.Name))
                    {
                        Add(file, prefix + sub.Name + "/" + file.Name, c, seen);
                    }
                }
            }
        }
        else if (configs)
        {
            foreach (var sub in dir.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                c.LeftOut.Add(prefix + sub.Name + "/");
            }
        }
    }

    /// <summary>
    /// Every allowlisted file under <paramref name="dir"/>, <paramref name="depth"/> folders deep at most; links are left out and named. In a
    /// <paramref name="skill"/>, a package or build folder (<see cref="SkillSkippedFolders"/>) is left out and named too, a <c>.git</c> silently.
    /// </summary>
    private static void CollectTree(DirectoryInfo dir, string prefix, int depth, bool skipDot, bool skill, ExportCollection c, HashSet<string> seen)
    {
        foreach (var file in dir.EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (IsExported(prefix + file.Name))
            {
                Add(file, prefix + file.Name, c, seen);
            }
        }

        if (depth <= 0)
        {
            return;
        }

        foreach (var sub in dir.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (skipDot && sub.Name.StartsWith('.'))
            {
                continue;
            }

            if ((skill ? SkillSkippedFolders : SkillCatalog.SkippedFolders).Contains(sub.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (skill && !Is(sub.Name, ".git"))
                {
                    c.LeftOut.Add(prefix + sub.Name + "/" + SettingsTransferText.SkillBuildNote);
                }

                continue;
            }

            if (IsLink(sub))
            {
                c.LeftOut.Add(prefix + sub.Name + "/" + SettingsTransferText.LinkNote);
                continue;
            }

            CollectTree(sub, prefix + sub.Name + "/", depth - 1, skipDot, skill, c, seen);
        }
    }

    private static void Add(FileInfo file, string relative, ExportCollection c, HashSet<string> seen)
    {
        string name = relative.Normalize(NormalizationForm.FormC);
        if (IsLink(file))
        {
            c.LeftOut.Add(name + SettingsTransferText.LinkNote);
        }
        else if (!IsPortablePath(name))
        {
            c.LeftOut.Add(name + SettingsTransferText.NotPortableNote);
        }
        else if (!seen.Add(name))
        {
            c.LeftOut.Add(name + SettingsTransferText.CaseNote);
        }
        else
        {
            c.Files.Add(new ExportFile(name, file.FullName, file.Length));
        }
    }

    /// <summary>
    /// A symbolic link or junction (<see cref="WorkingDirectory.RealLinkTarget"/>'s test: a reparse point with a target, so a cloud placeholder is no
    /// link), the enumerated attributes asked first so a plain file costs no extra read (the review's #32).
    /// </summary>
    private static bool IsLink(FileSystemInfo info) =>
        (info.Attributes & FileAttributes.ReparsePoint) != 0 && WorkingDirectory.RealLinkTarget(info.FullName) is not null;

    /// <summary>
    /// Writes the zip of <paramref name="c"/> to <paramref name="output"/>: the manifest, then each file, built by hand (not
    /// <c>CreateEntryFromFile</c>, which opens with <c>FileShare.Read</c> and fails on a file another copy of the app holds open for writing):
    /// read with every share, its date clamped into the zip's 1980–2107, and on a Mac its mode recorded. Checks the token between files.
    /// Returns the error that refused it, else null. The manifest is dated by <paramref name="now"/>, the clock's own time (the review's #30).
    ///
    /// <para>Each file is opened before its entry is made (the review's #85 and #76): a file gone since the walk is left out and named, one that
    /// won't read refuses the export, and a failure writing the zip itself (a full disk) goes up to the caller's <c>CannotWrite</c>.</para>
    /// </summary>
    internal static string? Write(ExportCollection c, Stream output, string appVersion, DateTimeOffset now, CancellationToken cancellationToken)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        var manifest = new SettingsArchiveManifest
        {
            Format = Format,
            App = appVersion,
            Os = ThisOs,
            Created = now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            Profiles = [.. c.Profiles],
        };
        var head = zip.CreateEntry(ManifestName, CompressionLevel.Optimal);
        head.LastWriteTime = now;
        using (var stream = head.Open())
        {
            JsonSerializer.Serialize(stream, manifest, SettingsJsonContext.Default.SettingsArchiveManifest);
        }

        foreach (var file in c.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DateTimeOffset date;
            int attributes = 0;
            FileStream input;
            try
            {
                date = Clamp(File.GetLastWriteTime(file.FullPath));
                if (!OperatingSystem.IsWindows())
                {
                    attributes = unchecked((0x8000 | (int)File.GetUnixFileMode(file.FullPath)) << 16);
                }

                input = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                c.LeftOut.Add(file.Relative + SettingsTransferText.GoneNote);
                continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return SettingsTransferText.Unreadable(file.Relative, ex.Message);
            }

            using (input)
            {
                var entry = zip.CreateEntry(file.Relative, CompressionLevel.Optimal);
                entry.LastWriteTime = date;
                if (attributes != 0)
                {
                    entry.ExternalAttributes = attributes;
                }

                using var body = entry.Open();
                input.CopyTo(body);
            }
        }

        return null;
    }

    /// <summary>A file's date as a zip can hold it: outside 1980–2107 (a 1970 date in a downloaded skill), 1980-01-01, as <c>CreateEntryFromFile</c> does.</summary>
    internal static DateTimeOffset Clamp(DateTime local) =>
        local.Year is < 1980 or > 2107 ? new DateTimeOffset(new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Unspecified), TimeSpan.Zero) : new DateTimeOffset(local);

    /// <summary>The three biggest items of <paramref name="files"/> (a skill folder whole, else the file), for the over-the-cap refusal.</summary>
    internal static IReadOnlyList<string> Biggest(IReadOnlyList<ExportFile> files) =>
        files.GroupBy(f => ItemOf(f.Relative), StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, Bytes: g.Sum(f => f.Length)))
            .OrderByDescending(g => g.Bytes)
            .Take(3)
            .Select(g => $"{g.Name} ({FileText.Size(g.Bytes)})")
            .ToList();

    /// <summary>The item a file belongs to: its skill folder (<c>skills/x/</c> or <c>profiles/p/skills/x/</c>), else itself.</summary>
    private static string ItemOf(string relative)
    {
        string[] s = relative.Split('/');
        int at = Is(s[0], Profiles.DirectoryName) ? 2 : 0;
        return s.Length > at + 2 && Is(s[at], SkillsName) ? string.Join('/', s[..(at + 2)]) + "/" : relative;
    }

    /// <summary>
    /// Where <c>/settings export [path]</c> writes: the default name in <paramref name="downloads"/> with no path; a <c>~</c> path from the user's
    /// home and a relative one from <paramref name="downloads"/> (<see cref="Sqlite.SqliteNamedDatabase.Expand"/>, the review's #22); an existing
    /// folder, or a path ending in a separator, gets the default name inside it (#9); <c>.zip</c> added when missing. The default name, when taken,
    /// tries <c>-2</c>, <c>-3</c>… (#75: two exports in one second); a name the user typed is never bent. Refused inside the home (the next export
    /// would walk it), over an existing file, and for a path no file can have. Surrounding quotes are stripped (a pasted path).
    /// </summary>
    public static string? Destination(string home, string? typed, string downloads, DateTime localNow, out string? error)
    {
        error = null;
        string text = SettingsMenu.UnquotePath(typed ?? "");
        string name = SettingsTransferText.DefaultFileName(localNow);
        string path;
        if (text.Length == 0)
        {
            path = FreeDefault(downloads, name);
        }
        else if (Sqlite.SqliteNamedDatabase.Expand(text, downloads) is not { } full)
        {
            error = SettingsTransferText.BadPath(text);
            return null;
        }
        else if (Path.EndsInDirectorySeparator(text) || Path.EndsInDirectorySeparator(full) || Directory.Exists(full))
        {
            path = FreeDefault(full, name);
        }
        else
        {
            path = HasExtension(full, SettingsTransferText.ZipExtension) ? full : full + SettingsTransferText.ZipExtension;
        }

        string homeFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(home));
        if (path.StartsWith(homeFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            error = SettingsTransferText.InsideHome(homeFull);
            return null;
        }

        if (File.Exists(path) || Directory.Exists(path))
        {
            error = SettingsTransferText.AlreadyThere(path);
            return null;
        }

        return path;
    }

    /// <summary>The default name in <paramref name="folder"/>, or the first of <c>-2</c>, <c>-3</c>… that nothing has.</summary>
    private static string FreeDefault(string folder, string name)
    {
        string path = Path.Combine(folder, name);
        string stem = Path.GetFileNameWithoutExtension(name);
        for (int i = 2; (File.Exists(path) || Directory.Exists(path)) && i < 1000; i++)
        {
            path = Path.Combine(folder, stem + "-" + i.ToString(CultureInfo.InvariantCulture) + SettingsTransferText.ZipExtension);
        }

        return path;
    }

    /// <summary>
    /// The whole export: the destination, the walk, the caps, the zip written to a temp sibling and moved into place (never over a file).
    /// A cancel deletes the temp file and throws; the caller answers nothing, as <c>/sessions export</c> does.
    /// </summary>
    public static SettingsTransferOutcome ExportToFile(string home, string? typed, string downloads, string appVersion, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(downloads);
        // The clock's own wall time names the file (the review's #30: the machine's zone stood in for the TimeProvider's).
        if (Destination(home, typed, downloads, now.DateTime, out string? refusal) is not { } path)
        {
            return SettingsTransferOutcome.Failed(refusal!);
        }

        ExportCollection c;
        try
        {
            c = Collect(home);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return SettingsTransferOutcome.Failed(SettingsTransferText.Unreadable(home, ex.Message));   // the review's #2: the walk's failure escaped
        }

        if (c.Files.Count == 0)
        {
            return SettingsTransferOutcome.Failed(SettingsTransferText.NothingToExport);
        }

        // The manifest counts, as the import counts it (the review's #28 and #77): one entry and its room.
        long bytes = c.Files.Sum(f => f.Length);
        if (c.Files.Count + 1 > MaxEntries || bytes + MaxManifestBytes > MaxBytes)
        {
            return SettingsTransferOutcome.Failed(SettingsTransferText.TooBig(c.Files.Count + 1, bytes, Biggest(c.Files)));
        }

        // A temp name of its own, made new (the review's #49: a fixed "<zip>.tmp" was another copy's, or the user's).
        string temp = path + "." + Path.GetRandomFileName() + ".tmp";
        long size;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string? error;
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                error = Write(c, output, appVersion, now, cancellationToken);
                size = output.Length;
            }

            if (error is not null)
            {
                return SettingsTransferOutcome.Failed(error);
            }

            File.Move(temp, path, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return SettingsTransferOutcome.Failed(File.Exists(path) ? SettingsTransferText.AlreadyThere(path) : SettingsTransferText.CannotWrite(path, ex.Message));
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // best effort
            }
        }

        DiagnosticLog.Info(Category, $"Exported {c.Files.Count.ToString(CultureInfo.InvariantCulture)} files of {c.Profiles.Count.ToString(CultureInfo.InvariantCulture)} profiles ({string.Join(", ", c.Profiles)}) to {path}; left out: {(c.LeftOut.Count == 0 ? "none" : string.Join(", ", c.LeftOut))}.");
        var lines = new List<SettingsReportLine> { new(SettingsTransferText.Exported(path, c.Profiles.Count, size)) };
        if (c.LeftOut.Count > 0)
        {
            lines.Add(new SettingsReportLine(SettingsTransferText.LeftOut(c.LeftOut)));
        }

        lines.Add(new SettingsReportLine(SettingsTransferText.KeepPrivate));
        return new SettingsTransferOutcome(lines, null);
    }

    // ─── import: reading and vetting ─────────────────────────────────────────────

    /// <summary>A vetted zip: its manifest, the entries on the allowlist, and what was skipped (unknown entries, links).</summary>
    public sealed class Plan : IDisposable
    {
        private readonly ZipArchive _zip;

        internal Plan(ZipArchive zip, SettingsArchiveManifest manifest, List<(string Name, ZipArchiveEntry Entry)> entries, List<string> skipped)
        {
            _zip = zip;
            Manifest = manifest;
            Entries = entries;
            Skipped = skipped;
        }

        public SettingsArchiveManifest Manifest { get; }

        internal List<(string Name, ZipArchiveEntry Entry)> Entries { get; }

        internal List<string> Skipped { get; }

        public void Dispose() => _zip.Dispose();
    }

    /// <summary>
    /// Reads and vets the zip in <paramref name="stream"/> (seekable, left open): a zip's magic, at most <see cref="MaxEntries"/> entries and
    /// <see cref="MaxBytes"/> declared, no encrypted entry, every path portable and none differing from another only by case, a manifest of a
    /// format this version reads. A damaged zip is refused with .NET's reason. Nothing is written.
    /// </summary>
    public static Plan? Read(Stream stream, out string? error)
    {
        ArgumentNullException.ThrowIfNull(stream);
        error = null;
        Span<byte> head = stackalloc byte[4];
        int got = stream.ReadAtLeast(head, 4, throwOnEndOfStream: false);
        if (!ZipHead.Looks(head[..got]))
        {
            error = SettingsTransferText.NotZip;
            return null;
        }

        // A zip's compressed size never passes its contents by more than its headers: a longer one is refused before its central directory is
        // parsed at all (the review's #69: the entry cap was checked only after ZipArchive had read every entry).
        if (stream.Length > MaxBytes + (long)MaxEntries * 1024)
        {
            error = SettingsTransferText.TooLarge;
            return null;
        }

        stream.Position = 0;
        ZipArchive? zip = null;
        try
        {
            zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            if (zip.Entries.Count > MaxEntries)
            {
                error = SettingsTransferText.TooManyEntries(zip.Entries.Count);
                zip.Dispose();
                return null;
            }

            SettingsArchiveManifest? manifest = null;
            var entries = new List<(string, ZipArchiveEntry)>();
            var skipped = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long declared = 0;
            foreach (var entry in zip.Entries)
            {
                string raw = entry.FullName.Replace('\\', '/');
                if (raw.EndsWith('/'))
                {
                    continue;
                }

                string name = raw.Normalize(NormalizationForm.FormC);
                if (entry.IsEncrypted)
                {
                    error = SettingsTransferText.Encrypted;
                }
                else if (!IsPortablePath(name))
                {
                    error = SettingsTransferText.UnsafePath(name);
                }
                else if (seen.TryGetValue(name, out string? held))
                {
                    // The same name twice, or two names that differ only by case (the review's #109: both were called a case clash).
                    error = string.Equals(held, name, StringComparison.Ordinal) ? SettingsTransferText.DuplicateEntry(name) : SettingsTransferText.CaseClash(name);
                }
                else if ((declared += entry.Length) > MaxBytes)
                {
                    error = SettingsTransferText.TooLarge;
                }

                if (error is not null)
                {
                    zip.Dispose();
                    return null;
                }

                seen.Add(name);
                if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                {
                    skipped.Add(name + SettingsTransferText.LinkNote);
                }
                else if (name == ManifestName)
                {
                    manifest = ReadManifest(entry);
                }
                else if (IsExported(name))
                {
                    entries.Add((name, entry));
                }
                else
                {
                    skipped.Add(name);
                }
            }

            if (manifest is null || manifest.Format < 1)
            {
                error = SettingsTransferText.NoManifest;
            }
            else if (manifest.Format > Format)
            {
                error = SettingsTransferText.NewerFormat(manifest.Format);
            }

            if (error is not null)
            {
                zip.Dispose();
                return null;
            }

            return new Plan(zip, manifest!, entries, skipped);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException or ArgumentException or JsonException)
        {
            zip?.Dispose();
            error = SettingsTransferText.Damaged(ex.Message);
            return null;
        }
    }

    private static SettingsArchiveManifest? ReadManifest(ZipArchiveEntry entry)
    {
        using var buffer = new MemoryStream();
        using (var input = entry.Open())
        {
            if (!SkillArchive.CopyCapped(input, buffer, MaxManifestBytes))
            {
                throw new InvalidDataException(SettingsTransferText.ManifestTooBig);   // damaged, not missing (the review's #68)
            }
        }

        buffer.Position = 0;
        return JsonSerializer.Deserialize(buffer, SettingsJsonContext.Default.SettingsArchiveManifest);
    }

    // ─── import: writing ─────────────────────────────────────────────────────────

    /// <summary>What an import did, for the report.</summary>
    internal sealed class ImportReport
    {
        public List<string> Added { get; } = [];
        public List<string> AddedHome { get; } = [];
        public List<string> Skipped { get; } = [];
        public List<string> Reset { get; } = [];
        public List<string> Unparsable { get; } = [];

        /// <summary>What the second phase couldn't move or write, each with its reason (the review's #8: a failure read "already here").</summary>
        public List<string> Failed { get; } = [];

        /// <summary>The added <c>configs</c> files whose passwords sit in the other computer's store (the review's #34).</summary>
        public List<string> ConnectionFiles { get; } = [];

        /// <summary>Whether a reset setting is a key the person types, so the report says to type it again (the review's #38).</summary>
        public bool ResetKeys { get; set; }

        public bool WroteMcp { get; set; }
        public string? NewerApp { get; set; }

        public bool AddedAny => Added.Count > 0 || AddedHome.Count > 0;
    }

    /// <summary>A home-level file read from the zip, written in the second phase.</summary>
    private sealed record PendingFile(string Relative, byte[] Bytes, int Attributes);

    /// <summary>A profile staged in the first phase: the keys its fixups reset, whether its <c>profile.json</c> didn't read, its connection files.</summary>
    private sealed record StagedProfile(IReadOnlyList<string> Reset, bool Unparsable, IReadOnlyList<string> ConnectionFiles);

    /// <summary>
    /// Imports <paramref name="plan"/> into <paramref name="home"/>. The first phase, which honors the token, reads and stages everything:
    /// each profile into <c>profiles/.import-*</c> with its <c>profile.json</c> fixed up, each new skill folder into <c>skills/.import-*</c>, every
    /// other home file into memory. The second moves and writes them, ignoring the token (renames, quick), so a partial import is always reported.
    /// The stages are deleted at the end, and a crash's leftovers (an hour old, so never another copy's live one) at the start of the next import.
    /// </summary>
    internal static ImportReport Import(string home, Plan plan, ImportEnvironment environment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(environment);
        string root = Path.GetFullPath(home);
        string profilesRoot = Profiles.Root(root);
        string skillsRoot = Path.Combine(root, SkillsName);
        ClearStages(profilesRoot);
        ClearStages(skillsRoot);
        Directory.CreateDirectory(profilesRoot);
        Directory.CreateDirectory(Path.Combine(root, ConfigsName));

        var report = new ImportReport();
        report.Skipped.AddRange(plan.Skipped);
        if (UpdateVersion.TryParse(plan.Manifest.App, out var theirs) && UpdateVersion.TryParse(environment.AppVersion, out var ours) && theirs > ours)
        {
            report.NewerApp = plan.Manifest.App;
        }

        // Group the entries: per profile (the zip's spelling, first wins) and the home's own.
        var byProfile = new Dictionary<string, List<(string Inner, ZipArchiveEntry Entry)>>(StringComparer.OrdinalIgnoreCase);
        var homeEntries = new List<(string Name, ZipArchiveEntry Entry)>();
        foreach (var (name, entry) in plan.Entries)
        {
            string[] s = name.Split('/');
            if (Is(s[0], Profiles.DirectoryName))
            {
                if (!byProfile.TryGetValue(s[1], out var list))
                {
                    byProfile[s[1]] = list = [];
                }

                list.Add((string.Join('/', s[2..]), entry));
            }
            else
            {
                homeEntries.Add((name, entry));
            }
        }

        // A profile's name is taken only when a folder of it exists here (never Profiles.Resolve, which says neon always exists). Every zip name
        // free here is reserved first, then only the clashing ones get a suffix (the review's #73: local work with zip work and work-imported made
        // the zip's own work-imported a work-imported-imported).
        var zipNames = byProfile.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        var taken = new HashSet<string>(Directory.EnumerateDirectories(profilesRoot).Select(p => Path.GetFileName(p)), StringComparer.OrdinalIgnoreCase);
        var finalNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string zipName in zipNames.Where(n => !taken.Contains(n)).ToList())
        {
            finalNames[zipName] = zipName;
            taken.Add(zipName);
        }

        foreach (string zipName in zipNames.Where(n => !finalNames.ContainsKey(n)))
        {
            string chosen = FreeName(zipName, taken);
            taken.Add(chosen);
            finalNames[zipName] = chosen;
            renames[zipName] = chosen;
        }

        var context = new FixupContext(environment, plan.Manifest.Os, renames, profilesRoot);
        string stamp = Guid.NewGuid().ToString("N")[..8];
        string profileStage = Path.Combine(profilesRoot, StagePrefix + stamp);
        string skillStage = Path.Combine(skillsRoot, StagePrefix + stamp);
        var stagedSkills = new List<string>();
        var pending = new List<PendingFile>();
        var staged = new Dictionary<string, StagedProfile>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // ── phase 1: stage (cancellable) ──
            foreach (string zipName in zipNames)
            {
                string dir = Path.Combine(profileStage, zipName);
                Directory.CreateDirectory(dir);
                foreach (var (rest, entry) in byProfile[zipName])
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Extract(entry, Path.Combine(dir, Path.Combine(rest.Split('/'))));
                }

                // Each profile's own copy names its planned folder, for the working-folder check (the review's #64).
                FixupResult? fixup = FindFile(dir, Profiles.FileName) is { } profileJson
                    ? FixProfileFile(profileJson, context with { PlannedName = finalNames[zipName] })
                    : new FixupResult([], false);
                staged[zipName] = new StagedProfile(fixup?.Reset ?? [], fixup is null, StoredPasswordFiles(Path.Combine(dir, ConfigsName)));
            }

            StageHome(root, homeEntries, skillStage, stagedSkills, pending, report, cancellationToken);
        }
        catch
        {
            WorkingDirectory.DeleteDirectoryQuietly(profileStage);
            WorkingDirectory.DeleteDirectoryQuietly(skillStage);
            throw;
        }

        // ── phase 2: move into place (runs to the end) ──
        try
        {
            foreach (string zipName in zipNames)
            {
                if (MoveProfile(Path.Combine(profileStage, zipName), profilesRoot, zipName, finalNames[zipName], taken, out string? failure) is not { } name)
                {
                    report.Failed.Add(SettingsTransferText.FailedOne($"{Profiles.DirectoryName}/{zipName}/", failure ?? ""));
                    continue;
                }

                // Said under the name it landed with (the review's #7: the planned one could differ after a race).
                var profile = staged[zipName];
                report.Added.Add(Profiles.NameEquals(zipName, name) ? name : SettingsTransferText.Renamed(zipName, name));
                if (profile.Unparsable)
                {
                    report.Unparsable.Add(name);
                }

                if (profile.Reset.Count > 0)
                {
                    report.Reset.Add(SettingsTransferText.ResetOne(name, profile.Reset.Select(SettingsTransferText.SettingLabel).ToList()));
                    report.ResetKeys |= profile.Reset.Any(key => SettingsSecrets.Keys.Any(secret => secret.Key == key));
                }

                report.ConnectionFiles.AddRange(profile.ConnectionFiles.Select(file => $"{Profiles.DirectoryName}/{name}/{ConfigsName}/{file}"));
            }

            foreach (string skill in stagedSkills)
            {
                string target = Path.Combine(skillsRoot, skill);
                try
                {
                    if (Exists(skillsRoot, skill))
                    {
                        report.Skipped.Add(SkillsName + "/" + skill + "/");
                        continue;
                    }

                    Directory.Move(Path.Combine(skillStage, skill), target);
                    report.AddedHome.Add(SkillsName + "/" + skill + "/");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Reported, and the rest still lands (the review's #21: an access refusal escaped after the profiles had moved).
                    report.Failed.Add(SettingsTransferText.FailedOne(SkillsName + "/" + skill + "/", ex.Message));
                }
            }

            foreach (var file in pending)
            {
                WriteHomeFile(root, file, report);
            }
        }
        finally
        {
            WorkingDirectory.DeleteDirectoryQuietly(profileStage);
            WorkingDirectory.DeleteDirectoryQuietly(skillStage);
        }

        DiagnosticLog.Info(Category, $"Imported profiles: {(report.Added.Count == 0 ? "none" : string.Join(", ", report.Added))}; home items: {report.AddedHome.Count.ToString(CultureInfo.InvariantCulture)}; "
            + $"skipped: {(report.Skipped.Count == 0 ? "none" : string.Join(", ", report.Skipped))}; failed: {(report.Failed.Count == 0 ? "none" : string.Join(", ", report.Failed))}; "
            + $"reset: {(report.Reset.Count == 0 ? "none" : string.Join("; ", report.Reset))}.");
        return report;
    }

    /// <summary>
    /// Sorts the home's own entries into what lands and what is skipped, the unit being the item: a skill folder by its name and its
    /// <c>SKILL.md</c>'s (staged whole), a theme by its catalog name, a voice preset by its stem (both wherever they sit in the folder, since the
    /// first file to claim a name wins), a ComfyUI workflow by its stem (the <c>.json</c> and <c>.md</c> pair), a command or configs file by its
    /// path. The local names are read once each, and only when the zip holds that kind (the review's #19 and #53).
    /// </summary>
    private static void StageHome(string root, List<(string Name, ZipArchiveEntry Entry)> entries, string skillStage, List<string> stagedSkills,
        List<PendingFile> pending, ImportReport report, CancellationToken cancellationToken)
    {
        var listings = new Listings();
        string skillsRoot = Path.Combine(root, SkillsName);
        var themeNames = new Lazy<HashSet<string>>(() => LocalThemeNames(Path.Combine(root, ThemesName)));
        var voiceNames = new Lazy<HashSet<string>>(() => Speech.VoicePresets.Files(Path.Combine(root, VoicesName))
            .Select(f => Path.GetFileNameWithoutExtension(f.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase));
        var comfyStems = new Lazy<HashSet<string>>(() => LocalComfyStems(Path.Combine(root, ComfyName)));
        var skillNames = new Lazy<HashSet<string>>(() => LocalSkillNames(skillsRoot));
        var skills = entries.Where(e => Is(e.Name.Split('/')[0], SkillsName))
            .GroupBy(e => e.Name.Split('/')[1], StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var skill in skills)
        {
            string label = SkillsName + "/" + skill.Key + "/";
            var skillFile = skill.FirstOrDefault(e => e.Name.Split('/') is { Length: 3 } s && Is(s[2], SkillCatalog.FileName));
            if (skillFile.Entry is null || listings.Exists(skillsRoot, skill.Key))
            {
                report.Skipped.Add(label);
                continue;
            }

            // The catalog names a skill by its SKILL.md, so a differently named folder of the same skill is a duplicate too (the review's #87);
            // read before anything is extracted, and each staged name joins the set (two of one name in the zip: the first wins).
            if (!skillNames.Value.Add(SkillName(ReadBytes(skillFile.Entry)) ?? skill.Key))
            {
                report.Skipped.Add(label);
                continue;
            }

            foreach (var (name, entry) in skill)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Under the group's spelling, so a case-sensitive volume never splits one skill in two (the review's #67).
                Extract(entry, Path.Combine(skillStage, skill.Key, Path.Combine(name.Split('/')[2..])));
            }

            stagedSkills.Add(skill.Key);
        }

        foreach (var (name, entry) in entries.Where(e => !Is(e.Name.Split('/')[0], SkillsName)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string[] s = name.Split('/');
            string stem = Path.GetFileNameWithoutExtension(s[^1]);
            if (listings.Exists(root, name))
            {
                report.Skipped.Add(name);
                continue;
            }

            byte[] bytes = ReadBytes(entry);
            if (Is(s[0], ThemesName))
            {
                // Decoded as the theme loader reads a file: the mark dropped, UTF-16 by it (the review's #24).
                string? theme = UI.ThemeFile.Parse(WorkingDirectory.Decode(bytes, out _), out _) is { } data ? UI.ThemeFile.NameOf(data, s[^1]) : null;
                if (theme is not null && !themeNames.Value.Add(theme))
                {
                    report.Skipped.Add(name);
                    continue;
                }
            }
            else if (Is(s[0], VoicesName) && !voiceNames.Value.Add(stem))
            {
                report.Skipped.Add(name);
                continue;
            }
            else if (Is(s[0], ComfyName) && comfyStems.Value.Contains(stem))
            {
                report.Skipped.Add(name);
                continue;
            }

            pending.Add(new PendingFile(name, bytes, entry.ExternalAttributes));
        }
    }

    private static HashSet<string> LocalThemeNames(string folder)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(folder))
        {
            return names;
        }

        foreach (string file in UI.ThemeCatalog.JsonFiles(folder, []))
        {
            names.Add(UI.ThemeFile.Read(file, out _) is { } data ? UI.ThemeFile.NameOf(data, file) : Path.GetFileNameWithoutExtension(file).Trim().ToLowerInvariant());
        }

        return names;
    }

    /// <summary>The ComfyUI workflows' stems here, a <c>.json</c> or its <c>.md</c>.</summary>
    private static HashSet<string> LocalComfyStems(string folder)
    {
        var stems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(folder))
        {
            return stems;
        }

        foreach (string file in Directory.EnumerateFiles(folder))
        {
            if (HasExtension(file, ".json") || HasExtension(file, ".md"))
            {
                stems.Add(Path.GetFileNameWithoutExtension(file));
            }
        }

        return stems;
    }

    /// <summary>The home skills' names as the catalog gives them (the <c>SKILL.md</c> name, else the folder's), compared as its <c>seen</c> set is.</summary>
    private static HashSet<string> LocalSkillNames(string skillsRoot)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string folder in SkillCatalog.Folders(skillsRoot))
        {
            string? name = null;
            try
            {
                name = SkillName(File.ReadAllBytes(Path.Combine(folder, SkillCatalog.FileName)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // unreadable: the folder's name stands in
            }

            names.Add(name ?? Path.GetFileName(folder));
        }

        return names;
    }

    /// <summary>The skill's name from its <c>SKILL.md</c> bytes, decoded as the catalog reads them; null when it doesn't parse.</summary>
    private static string? SkillName(byte[] bytes) =>
        SkillFrontmatter.TryParse(WorkingDirectory.Decode(bytes, out _), out var frontmatter, out _, out _) ? frontmatter?.Name : null;

    /// <summary>
    /// Fixes up a staged <c>profile.json</c>: what was reset and whether anything changed, or null when it doesn't read or can't be written (then
    /// left as is). Written back only when something changed (the review's #93), by the one atomic replace, which keeps a Mac's mode (#43, #97);
    /// a duplicate key, a read-only file or an IO failure is the profile's alone (#25, #37), never the whole zip's.
    /// </summary>
    private static FixupResult? FixProfileFile(string path, FixupContext context)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path), documentOptions: SettingsImportFixups.ReadOptions) is not JsonObject root)
            {
                return null;
            }

            var result = SettingsImportFixups.Apply(root, context);
            if (result.Changed)
            {
                WorkingDirectory.WriteAtomically(path, Encoding.UTF8.GetBytes(root.ToJsonString(SettingsImportFixups.Indented)));
            }

            return result;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn(Category, $"An imported profile.json was left as it came: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The <c>*.json</c> files of <paramref name="configs"/> (a staged profile's) that keep a password in the other computer's store, by name; none
    /// for a missing folder.
    /// </summary>
    private static List<string> StoredPasswordFiles(string configs)
    {
        var names = new List<string>();
        if (!Directory.Exists(configs))
        {
            return names;
        }

        foreach (string file in Directory.EnumerateFiles(configs).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (HasExtension(file, ".json") && HoldsStoredPassword(File.ReadAllBytes(file)))
                {
                    names.Add(Path.GetFileName(file));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // a staged file that won't read is no reason to stop; the move reports the profile
            }
        }

        return names;
    }

    /// <summary>
    /// Whether a connection file keeps a password the other computer's store holds (2026-11-13, the review's #34: the files traveled byte for
    /// byte and nothing said their passwords might not open here): an encrypted value (<c>dpapi:</c>/<c>keychain:</c>) at any depth, or a
    /// <c>passwordStore</c> naming a store other than the file (Credential Manager, the Keychain). A file that doesn't parse says no. Pure.
    /// </summary>
    internal static bool HoldsStoredPassword(byte[] bytes)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(WorkingDirectory.Decode(bytes, out _), documentOptions: SettingsImportFixups.ReadOptions);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return false;
        }

        return Holds(node);

        static bool Holds(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject o:
                    foreach (var (key, value) in o)
                    {
                        if (Is(key, "passwordStore") && value is JsonValue store && store.GetValueKind() == JsonValueKind.String
                            && store.GetValue<string>().Trim() is { Length: > 0 } word && !Is(word, Sql.SqlConnectionConfig.FileStore))
                        {
                            return true;
                        }

                        if (Holds(value))
                        {
                            return true;
                        }
                    }

                    return false;
                case JsonArray a:
                    return a.Any(Holds);
                case JsonValue v:
                    return v.GetValueKind() == JsonValueKind.String && Sql.WindowsCredentials.IsProtected(v.GetValue<string>().Trim());
                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// Moves a staged profile in, returning the name it landed under; a name taken meanwhile (another running copy) tries the next suffix.
    /// Null, with the reason, when the move fails otherwise. An advisor already pointed at the planned name keeps it (a rare race, said in the log).
    /// </summary>
    private static string? MoveProfile(string staged, string profilesRoot, string zipName, string name, HashSet<string> taken, out string? failure)
    {
        failure = null;
        for (int attempt = 0; attempt < 100; attempt++)
        {
            string target = Path.Combine(profilesRoot, name);
            if (!Exists(profilesRoot, name))
            {
                try
                {
                    Directory.Move(staged, target);
                    return name;
                }
                catch (IOException) when (Directory.Exists(target))
                {
                    DiagnosticLog.Info(Category, $"{name} was taken during the import; trying the next name.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failure = ex.Message;
                    return null;
                }
            }

            taken.Add(name);
            name = FreeName(zipName, taken);
        }

        failure = SettingsTransferText.AlreadyThere(Path.Combine(profilesRoot, name));
        return null;
    }

    /// <summary>
    /// <paramref name="name"/> when no folder has it; else <c>&lt;name&gt;-imported</c>, <c>-imported-2</c>, …, cut to fit
    /// <see cref="Profiles.MaxNameLength"/>. Pure.
    /// </summary>
    internal static string FreeName(string name, ISet<string> taken)
    {
        if (!taken.Contains(name))
        {
            return name;
        }

        for (int i = 1; ; i++)
        {
            string suffix = i == 1 ? "-imported" : "-imported-" + i.ToString(CultureInfo.InvariantCulture);
            string stem = name.Length + suffix.Length > Profiles.MaxNameLength ? name[..(Profiles.MaxNameLength - suffix.Length)] : name;
            string candidate = stem + suffix;
            if (!taken.Contains(candidate) && Profiles.IsValidName(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// Writes a home file where none is. One that arrived since the first phase is skipped; any other failure is reported with its reason
    /// (the review's #8) and its partial file removed. A connection file written is checked for passwords this computer may not open (#34).
    /// </summary>
    private static void WriteHomeFile(string root, PendingFile file, ImportReport report)
    {
        string target = Path.Combine(root, Path.Combine(file.Relative.Split('/')));
        bool created = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                output.Write(file.Bytes);
            }

            ApplyMode(target, file.Attributes);
            report.AddedHome.Add(file.Relative);
            if (Is(file.Relative.Split('/')[0], ConfigsName))
            {
                Computers.ConfigCache.Invalidate(target);
                report.WroteMcp |= Is(file.Relative.Split('/')[^1], Mcp.McpConfigFile.FileName);
                if (HoldsStoredPassword(file.Bytes))
                {
                    report.ConnectionFiles.Add(file.Relative);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!created && File.Exists(target))
            {
                report.Skipped.Add(file.Relative);
                return;
            }

            if (created)
            {
                try
                {
                    File.Delete(target);
                }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                {
                    // best effort
                }
            }

            report.Failed.Add(SettingsTransferText.FailedOne(file.Relative, ex.Message));
        }
    }

    /// <summary>Copies <paramref name="entry"/> to <paramref name="target"/>, refusing a body longer than it declares; on a Mac its mode applied.</summary>
    private static void Extract(ZipArchiveEntry entry, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        bool ok;
        using (var input = entry.Open())
        using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            ok = SkillArchive.CopyCapped(input, output, entry.Length);
        }

        if (!ok)
        {
            throw new InvalidDataException(SettingsTransferText.EntryTooLong);
        }

        ApplyMode(target, entry.ExternalAttributes);
    }

    private static byte[] ReadBytes(ZipArchiveEntry entry)
    {
        using var input = entry.Open();
        using var buffer = new MemoryStream();
        return SkillArchive.CopyCapped(input, buffer, entry.Length) ? buffer.ToArray() : throw new InvalidDataException(SettingsTransferText.EntryTooLong);
    }

    /// <summary>
    /// On a Mac, a regular file's recorded mode: the read, write and execute bits, no group or other write, no setuid (<c>0755</c> at most),
    /// so a skill's script stays executable, and always the owner's read and write, so a zip's <c>0444</c> or <c>0200</c> never leaves a file the
    /// app can't rewrite or read (the review's #37 and #88). Windows ignores it; a zip without one keeps the default mode.
    /// </summary>
    private static void ApplyMode(string target, int attributes)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        int unix = (attributes >> 16) & 0xFFFF;
        if ((unix & 0xF000) != 0x8000)
        {
            return;
        }

        File.SetUnixFileMode(target, (UnixFileMode)((unix & 0b111_101_101) | 0b110_000_000));
    }

    // ─── the whole import, for the screen, headless and the start flag ───────────

    /// <summary>
    /// Imports the zip at <paramref name="path"/> into <paramref name="home"/>: the report's lines, or why nothing was imported. A cancel during
    /// the first phase throws <see cref="OperationCanceledException"/> with nothing written. <paramref name="atStart"/> for the start flag's report.
    /// </summary>
    public static SettingsTransferOutcome ImportFromFile(string home, string path, ImportEnvironment environment, CancellationToken cancellationToken, bool atStart = false)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path))
        {
            return SettingsTransferOutcome.Failed(SettingsTransferText.Missing(path));
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var plan = Read(stream, out string? error);
            if (plan is null)
            {
                DiagnosticLog.Info(Category, $"Refused {path}: {error}");
                return SettingsTransferOutcome.Failed(error!);
            }

            var report = Import(home, plan, environment, cancellationToken);
            return new SettingsTransferOutcome(Lines(report, path, atStart), null);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException)
        {
            return SettingsTransferOutcome.Failed(SettingsTransferText.Damaged(ex.Message));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return SettingsTransferOutcome.Failed(SettingsTransferText.ImportFailed(ex.Message));
        }
    }

    /// <summary>
    /// The report as notices, one per group: added, skipped, what failed and the connection files (warnings), reset (a warning), the
    /// newer-version and MCP lines, the trust line whenever anything landed (the review's #35: skills, commands and settings run as you too, not
    /// only MCP servers), and the delete advice. At the start, MCP connects anyway, so its "later" line is left out (#107).
    /// </summary>
    internal static List<SettingsReportLine> Lines(ImportReport report, string zipPath, bool atStart = false)
    {
        var lines = new List<SettingsReportLine>();
        if (report.Added.Count > 0)
        {
            lines.Add(new(SettingsTransferText.Added(report.Added)));
        }

        if (report.AddedHome.Count > 0)
        {
            lines.Add(new(SettingsTransferText.AddedHome(report.AddedHome)));
        }

        if (!report.AddedAny && report.Failed.Count == 0)
        {
            lines.Add(new(SettingsTransferText.NothingAdded));
        }

        if (report.Skipped.Count > 0)
        {
            lines.Add(new(SettingsTransferText.Skipped(report.Skipped)));
        }

        if (report.Failed.Count > 0)
        {
            lines.Add(new(SettingsTransferText.Failed(report.Failed), Warn: true));
        }

        if (report.ConnectionFiles.Count > 0)
        {
            lines.Add(new(SettingsTransferText.ConnectionPasswords(report.ConnectionFiles), Warn: true));
        }

        if (report.Reset.Count > 0)
        {
            lines.Add(new(SettingsTransferText.Reset(report.Reset, report.ResetKeys), Warn: true));
        }

        if (report.Unparsable.Count > 0)
        {
            lines.Add(new(SettingsTransferText.Unparsable(report.Unparsable), Warn: true));
        }

        if (report.NewerApp is { } newer)
        {
            lines.Add(new(SettingsTransferText.Newer(newer)));
        }

        if (report.WroteMcp && !atStart)
        {
            lines.Add(new(SettingsTransferText.McpLater));
        }

        if (report.AddedAny)
        {
            lines.Add(new(SettingsTransferText.Trust));
        }

        lines.Add(new(SettingsTransferText.DeleteZip(zipPath)));
        return lines;
    }

    /// <summary>
    /// A bare <c>/settings import</c>: the newest settings zips in <paramref name="downloads"/> with the command to type, or that there are none.
    /// The command lines are marked <see cref="SettingsReportLine.Command"/>, written bare so they copy as typed.
    /// </summary>
    public static IReadOnlyList<SettingsReportLine> ListZips(string downloads)
    {
        var names = Zips(downloads).Take(SettingsTransferText.ListedZips).ToList();
        if (names.Count == 0)
        {
            return [new(SettingsTransferText.NoZips(downloads))];
        }

        var lines = new List<SettingsReportLine> { new(SettingsTransferText.ZipsHeader(downloads)) };
        lines.AddRange(names.Select(n => new SettingsReportLine(SettingsTransferText.ZipLine(n), Command: true)));
        return lines;
    }

    /// <summary>The settings zips' names in <paramref name="downloads"/>, newest first (completion and the bare import).</summary>
    public static IReadOnlyList<string> Zips(string downloads)
    {
        try
        {
            return Directory.Exists(downloads)
                ? new DirectoryInfo(downloads).EnumerateFiles(SettingsTransferText.FilePrefix + "*" + SettingsTransferText.ZipExtension)
                    .OrderByDescending(f => f.LastWriteTimeUtc).ThenByDescending(f => f.Name, StringComparer.Ordinal).Select(f => f.Name).ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// The zip a typed <c>/settings import</c> path names: <c>~</c> from the user's home, relative from <paramref name="downloads"/>, quotes
    /// stripped (the review's #22 and #41); a name typed without <c>.zip</c> finds the zip that has it (#90). Null for a path no file can have.
    /// </summary>
    public static string? ImportPath(string typed, string downloads)
    {
        ArgumentNullException.ThrowIfNull(typed);
        if (Sqlite.SqliteNamedDatabase.Expand(SettingsMenu.UnquotePath(typed), downloads) is not { } path)
        {
            return null;
        }

        return !File.Exists(path) && Path.GetExtension(path).Length == 0 && File.Exists(path + SettingsTransferText.ZipExtension)
            ? path + SettingsTransferText.ZipExtension
            : path;
    }

    /// <summary>
    /// <c>--import-settings &lt;zip&gt;</c> (before the profile is resolved): the report's lines, or the refusal (then the launch exits 2). A relative
    /// path counts from the process's current folder, as the other command-line paths do. A zip this home took at an earlier start (its SHA-256
    /// in <see cref="StartMarkerName"/>) is skipped with one line and the launch carries on (2026-11-13, the review's #3, the user's pick);
    /// <c>/settings import</c> takes it again, never reading or writing the marker.
    /// </summary>
    public static SettingsTransferOutcome RunAtStart(string home, string zip, ImportEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(zip);
        string text = SettingsMenu.UnquotePath(zip);
        if (text.Length == 0)
        {
            return SettingsTransferOutcome.Failed(SettingsTransferText.MissingFlagValue);   // the review's #27: '""' threw at startup
        }

        if (Sqlite.SqliteNamedDatabase.Expand(text) is not { } path)
        {
            return SettingsTransferOutcome.Failed(SettingsTransferText.StartRefused(SettingsTransferText.BadPath(text)));
        }

        string marker = Path.Combine(home, StartMarkerName);
        string? hash = HashOf(path);
        if (hash is not null && Remembered(marker).Contains(hash))
        {
            DiagnosticLog.Info(Category, $"{path} was imported at an earlier start; skipped.");
            return new SettingsTransferOutcome([new(SettingsTransferText.AlreadyImported(path))], null);
        }

        var outcome = ImportFromFile(home, path, environment, CancellationToken.None, atStart: true);
        if (outcome.Error is { } error)
        {
            return SettingsTransferOutcome.Failed(SettingsTransferText.StartRefused(error));
        }

        if (hash is not null)
        {
            Remember(marker, hash);
        }

        return outcome;
    }

    /// <summary>The file's SHA-256 in hex; null when it is missing or won't read (then the import itself says why).</summary>
    private static string? HashOf(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The hashes in the marker; none when it is missing or won't read.</summary>
    private static HashSet<string> Remembered(string marker)
    {
        try
        {
            return File.Exists(marker)
                ? File.ReadAllLines(marker).Select(line => line.Trim()).Where(line => line.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Adds <paramref name="hash"/> to the marker; a failure is logged, the import having happened.</summary>
    private static void Remember(string marker, string hash)
    {
        try
        {
            File.AppendAllText(marker, hash + "\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn(Category, $"Couldn't note the imported zip in {marker}: {ex.Message}");
        }
    }

    // ─── helpers ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Deletes leftover <c>.import-*</c> stages under <paramref name="parent"/> (a crash mid-import) an hour old or more: a younger one may be
    /// another running copy's import under way (the review's #5). File times are the real clock's, so no <see cref="TimeProvider"/>.
    /// </summary>
    private static void ClearStages(string parent)
    {
        if (!Directory.Exists(parent))
        {
            return;
        }

        DateTime before = DateTime.UtcNow - StageAge;
        foreach (string dir in Directory.EnumerateDirectories(parent, StagePrefix + "*"))
        {
            if (Directory.GetLastWriteTimeUtc(dir) < before)
            {
                WorkingDirectory.DeleteDirectoryQuietly(dir);
            }
        }
    }

    /// <summary>
    /// One case-insensitive listing per folder, for the first phase's "already here" checks (the review's #19: each check listed every folder on
    /// its path again). The second phase asks the disk, its own moves making any listing stale.
    /// </summary>
    private sealed class Listings
    {
        private readonly Dictionary<string, HashSet<string>?> _byFolder = new(StringComparer.Ordinal);

        /// <summary>As <see cref="Exists(string, string)"/>, from the listings.</summary>
        public bool Exists(string root, string relative)
        {
            string current = root;
            foreach (string segment in relative.Split('/'))
            {
                if (Names(current) is not { } names || !names.TryGetValue(segment, out string? actual))
                {
                    return false;
                }

                current = Path.Combine(current, actual);
            }

            return true;
        }

        private HashSet<string>? Names(string folder)
        {
            if (!_byFolder.TryGetValue(folder, out var names))
            {
                names = Directory.Exists(folder)
                    ? Directory.EnumerateFileSystemEntries(folder).Select(p => Path.GetFileName(p)).ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : null;
                _byFolder[folder] = names;
            }

            return names;
        }
    }

    /// <summary>Whether <paramref name="relative"/> (<c>/</c>-separated) names a file or folder under <paramref name="root"/>, each segment matched ignoring case.</summary>
    internal static bool Exists(string root, string relative)
    {
        string current = root;
        foreach (string segment in relative.Split('/'))
        {
            if (!Directory.Exists(current))
            {
                return false;
            }

            string? match = Directory.EnumerateFileSystemEntries(current).FirstOrDefault(p => Is(Path.GetFileName(p), segment));
            if (match is null)
            {
                return false;
            }

            current = match;
        }

        return true;
    }

    private static string? FindFile(string dir, string name) =>
        Directory.EnumerateFiles(dir).FirstOrDefault(p => Is(Path.GetFileName(p), name));

    private static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool HasExtension(string name, string extension) => name.EndsWith(extension, StringComparison.OrdinalIgnoreCase) && name.Length > extension.Length;

    /// <summary>The folders and files the app keeps at the home's and a profile's top, from both systems' <c>/help files</c> rows, plus <c>claude-cli</c> and <c>mounts</c>: left out of the export without a word.</summary>
    internal sealed record KnownNames(HashSet<string> HomeFolders, HashSet<string> HomeFiles, HashSet<string> ProfileFolders, HashSet<string> ProfileFiles);

    internal static readonly Lazy<KnownNames> Known = new(() =>
    {
        var known = new KnownNames(new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase));
        foreach (bool windows in new[] { true, false })
        {
            foreach (var row in HelpFiles.Rows(windows))
            {
                if (row.Scope == HelpFileScope.WorkingFolder)
                {
                    continue;
                }

                int slash = row.Path.IndexOf('/', StringComparison.Ordinal);
                bool home = row.Scope == HelpFileScope.Home;
                if (slash < 0)
                {
                    (home ? known.HomeFiles : known.ProfileFiles).Add(row.Path);
                }
                else
                {
                    (home ? known.HomeFolders : known.ProfileFolders).Add(row.Path[..slash]);
                }
            }
        }

        known.HomeFolders.Add("claude-cli");
        known.HomeFolders.Add(Unc.MacMountRules.FolderName);
        return known;
    });
}
