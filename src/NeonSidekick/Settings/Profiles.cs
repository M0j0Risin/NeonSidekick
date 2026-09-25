using System.Text.Json;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Settings;

/// <summary>
/// The profile directory layout and its rules, as pure statics over a home directory:
/// <c>&lt;home&gt;\profiles\&lt;name&gt;\</c> holds one sidekick's <c>profile.json</c>,
/// <c>memory.json</c>, <c>persona.md</c>, <c>operata.md</c> and <c>vocalia.md</c>. Nothing here knows which profile is loaded; the
/// guards that need that (<see cref="DeleteRefusal"/>) take it as an argument, so every rule and
/// every sentence is testable without a screen.
///
/// <para>Names are compared ignoring case (the directory is on NTFS) and a typed name is
/// <see cref="Resolve"/>d to the directory's spelling before it is stored, so the pointer file
/// and the status panel always show the name the way the disk does. <see cref="DefaultName"/>
/// exists logically even when its directory does not: it is always listed, always resolves and
/// can never be deleted or renamed.</para>
/// </summary>
public static class Profiles
{
    public const string DirectoryName = "profiles";
    public const string DefaultName = "default";
    public const string FileName = "profile.json";
    public const int MaxNameLength = 32;

    /// <summary>The log category, shared with <see cref="AppSettings"/>.</summary>
    private const string Category = "Settings";

    /// <summary>
    /// The sidekick's own files beside <see cref="FileName"/>, in the order <see cref="CopySidekickFiles"/>
    /// takes them: the memories, the persona, the operating rules, the voice directive, the MCP servers
    /// (2026-09-20). Literals; <c>ProfilesTests</c> pins each to its owner's <c>FileName</c>.
    /// </summary>
    public static readonly string[] SidekickFiles = { "memory.json", "persona.md", "operata.md", "vocalia.md", "mcp.json" };

    /// <summary>
    /// The subset of <see cref="SidekickFiles"/> <c>/profile add</c> copies under the <c>basic</c>
    /// <see cref="NewProfileMode"/>: the memories alone (the user's call 2026-09-15).
    /// </summary>
    public static readonly string[] BasicSidekickFiles = { "memory.json" };

    /// <summary>The <c>/profile</c> subcommand words (<c>edit</c> and <c>reload</c> since 2026-09-21); a profile cannot be called any of them, so <c>/profile add</c> is never a switch and <c>/profile reset</c> is always the loaded one.</summary>
    public static readonly string[] ReservedNames = { "add", "delete", "edit", "reload", "rename", "reset" };

    /// <summary>
    /// The sidekick's own name, never a profile's (2026-09-21, the user's call): it is the default
    /// profile's window title (<c>ChatScreen.DefaultWindowTitle</c>) and the persona's name, so a
    /// profile called <c>neon</c> would read as the default one. The word alone, whatever the case;
    /// <c>neon2</c> or <c>neon_debug</c> are fine. Not a <see cref="ReservedNames"/> word: those are
    /// verbs, and <c>/profile neon</c> must stay a switch to a profile that is not there.
    /// </summary>
    public const string SidekickName = "neon";

    /// <summary>The wording for a name <see cref="IsValidName"/> refuses. Pinned.</summary>
    public const string NameError = "must be 1 to 32 letters, digits, - or _ (and not neon, add, delete, edit, reload, rename or reset)";

    /// <summary>Why <c>default</c> cannot be deleted. Pinned.</summary>
    public const string DefaultUndeletable = "The default profile cannot be deleted.";

    /// <summary>Why <c>default</c> cannot be reset from another profile (2026-09-22). Pinned.</summary>
    public const string DefaultUnresettable = "The default profile can only be reset while it is loaded.";

    /// <summary>Why the loaded profile cannot be deleted. Pinned.</summary>
    public static string CurrentUndeletable(string name) =>
        $"\"{name}\" is the current profile; switch to another (/profile <name>) before deleting it.";

    /// <summary>Why <c>default</c> cannot be renamed. Pinned.</summary>
    public const string DefaultUnrenamable = "The default profile cannot be renamed.";

    /// <summary>Why the loaded profile cannot be renamed. Pinned.</summary>
    public static string CurrentUnrenamable(string name) =>
        $"\"{name}\" is the current profile; switch to another (/profile <name>) before renaming it.";

    /// <summary>
    /// Whether <paramref name="name"/> can name a profile: one to <see cref="MaxNameLength"/>
    /// ASCII letters, digits, hyphens or underscores, and not a <see cref="ReservedNames"/> word
    /// nor <see cref="SidekickName"/> (2026-09-21).
    /// Nothing that could be a path segment trick, a shell surprise or a non-ASCII directory
    /// (the Vosk path rule is a reminder of what those cost).
    /// </summary>
    public static bool IsValidName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength)
        {
            return false;
        }

        foreach (char c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')
            {
                return false;
            }
        }

        return !ReservedNames.Contains(name, StringComparer.OrdinalIgnoreCase) && !NameEquals(name, SidekickName);
    }

    public static bool NameEquals(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The first character of a temporary profile's name (<see cref="IsTemporary"/>).</summary>
    public const char TemporaryPrefix = '_';

    /// <summary>
    /// Whether <paramref name="name"/> is a temporary profile's (2026-09-24, the user's ask: a <c>_test</c> to try
    /// things in): its name starts with <see cref="TemporaryPrefix"/>. Loaded and used like any other within a session,
    /// but a launch never lands in it — <see cref="AppSettings"/> opens <see cref="DefaultName"/> instead and points
    /// the pointer back there. Its directory and files are kept, so <c>/profile _test</c> picks up where it left off.
    /// </summary>
    public static bool IsTemporary(string? name) => !string.IsNullOrEmpty(name) && name[0] == TemporaryPrefix;

    public static bool IsDefault(string? name) => NameEquals(name, DefaultName);

    /// <summary>The home a profile folder sits in: <c>&lt;home&gt;\profiles\&lt;name&gt;</c> two levels up (2026-09-23, for the SQL tab's global row, whose static value knows only the profile's folder).</summary>
    public static string HomeOf(string profileDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profileDirectory));
        return Path.GetDirectoryName(Path.GetDirectoryName(full) ?? full) ?? full;
    }

    /// <summary><c>&lt;home&gt;\profiles</c>.</summary>
    public static string Root(string home) => Path.Combine(Path.GetFullPath(home), DirectoryName);

    /// <summary><c>&lt;home&gt;\profiles\&lt;name&gt;</c>.</summary>
    public static string Directory(string home, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Path.Combine(Root(home), name);
    }

    /// <summary><c>&lt;home&gt;\profiles\&lt;name&gt;\profile.json</c>.</summary>
    public static string ProfileFile(string home, string name) => Path.Combine(Directory(home, name), FileName);

    /// <summary>
    /// Every profile, <see cref="DefaultName"/> first (listed even when its directory is missing),
    /// the rest sorted ignoring case. Directories with a name <see cref="IsValidName"/> refuses are
    /// not profiles and are skipped. A missing <c>profiles</c> directory lists the default alone.
    /// </summary>
    public static IReadOnlyList<string> List(string home)
    {
        var names = new List<string> { DefaultName };
        string root = Root(home);
        if (System.IO.Directory.Exists(root))
        {
            var others = new List<string>();
            foreach (var path in System.IO.Directory.EnumerateDirectories(root))
            {
                string name = Path.GetFileName(path);
                if (IsValidName(name) && !IsDefault(name))
                {
                    others.Add(name);
                }
            }

            others.Sort(StringComparer.OrdinalIgnoreCase);
            names.AddRange(others);
        }

        return names;
    }

    /// <summary>
    /// The listed spelling of <paramref name="typed"/>, or null when no such profile exists.
    /// <see cref="DefaultName"/> always resolves (to its canonical lowercase spelling).
    /// </summary>
    public static string? Resolve(string home, string? typed)
    {
        if (!IsValidName(typed))
        {
            return null;
        }

        foreach (var name in List(home))
        {
            if (NameEquals(name, typed))
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>Whether a profile of that name exists (the default always does).</summary>
    public static bool Exists(string home, string? name) => Resolve(home, name) is not null;

    /// <summary>
    /// Creates the directory and writes <paramref name="seed"/> as its <c>profile.json</c> (a temp
    /// file then a move, like every settings write). The settings alone: the sidekick's files come
    /// along only through <see cref="CopySidekickFiles"/>, which the caller runs over the files the
    /// <see cref="NewProfileMode"/> names (<see cref="NewProfileMode.FilesFor"/>). Throws <see cref="IOException"/> /
    /// <see cref="UnauthorizedAccessException"/>; the caller reports.
    /// </summary>
    public static void Create(string home, string name, AppSettingsData seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        if (!IsValidName(name))
        {
            throw new ArgumentException(NameError, nameof(name));
        }

        System.IO.Directory.CreateDirectory(Directory(home, name));
        WriteProfileFile(ProfileFile(home, name), seed);
        DiagnosticLog.Info(Category, $"Created profile \"{name}\".");
    }

    /// <summary>
    /// Copies each of <see cref="SidekickFiles"/> that exists in <paramref name="sourceDirectory"/>
    /// into the profile <paramref name="name"/> (just <see cref="Create"/>d, so nothing is
    /// overwritten) and returns the names copied, in <see cref="SidekickFiles"/> order; with
    /// <paramref name="only"/> (<see cref="NewProfileMode.FilesFor"/>) just the ones it names. Never the
    /// <c>files</c> sandbox or its <c>.trash</c>: those are the sidekick's own. Throws
    /// <see cref="IOException"/> / <see cref="UnauthorizedAccessException"/>; the caller reports.
    /// </summary>
    public static IReadOnlyList<string> CopySidekickFiles(string sourceDirectory, string home, string name, IReadOnlyList<string>? only = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        if (!IsValidName(name))
        {
            throw new ArgumentException(NameError, nameof(name));
        }

        string target = Directory(home, name);
        System.IO.Directory.CreateDirectory(target);
        var copied = new List<string>();
        foreach (var fileName in PresentSidekickFiles(sourceDirectory))
        {
            if (only is not null && !only.Contains(fileName, StringComparer.Ordinal))
            {
                continue;
            }

            File.Copy(Path.Combine(Path.GetFullPath(sourceDirectory), fileName), Path.Combine(target, fileName));
            copied.Add(fileName);
        }

        return copied;
    }

    /// <summary>
    /// The <see cref="SidekickFiles"/> that exist in <paramref name="directory"/>, in that order:
    /// what a copy takes along. A missing directory has none.
    /// </summary>
    public static IReadOnlyList<string> PresentSidekickFiles(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        string root = Path.GetFullPath(directory);
        var present = new List<string>();
        foreach (var fileName in SidekickFiles)
        {
            if (File.Exists(Path.Combine(root, fileName)))
            {
                present.Add(fileName);
            }
        }

        return present;
    }

    /// <summary>The word for one of <see cref="SidekickFiles"/> in a notice (the delete prompt's words); <c>""</c> for anything else. Pinned.</summary>
    public static string Describe(string fileName) => fileName switch
    {
        "memory.json" => "memories",
        "persona.md" => "persona",
        "operata.md" => "operating rules",
        "vocalia.md" => "voice directive",
        "mcp.json" => "MCP servers",
        _ => "",
    };

    /// <summary>
    /// Reads a <c>profile.json</c> that is not the loaded one, to edit and <see cref="WriteProfileFile"/>
    /// back (<c>/cmdcopy</c>, 2026-09-21). A missing file is the compiled defaults: the profile is
    /// logical, as <c>default</c>'s can be, or was never saved, and that is what loading it would give.
    /// A corrupt file, or one that parses to null, <b>throws</b> <see cref="JsonException"/> — the
    /// opposite of <c>AppSettings.Load</c>'s fall-back, because a copy that quietly fell back would
    /// then overwrite the user's file with the defaults plus the copied field. The caller reports.
    /// <see cref="IOException"/> / <see cref="UnauthorizedAccessException"/> pass through too.
    /// </summary>
    public static AppSettingsData ReadProfileFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return new AppSettingsData();
        }

        return JsonSerializer.Deserialize(File.ReadAllText(path), SettingsJsonContext.Default.AppSettingsData)
            ?? throw new JsonException("The settings file parsed to null.");
    }

    /// <summary>Writes <paramref name="data"/> to <paramref name="path"/> atomically. Throws; the temp file is removed on failure.</summary>
    public static void WriteProfileFile(string path, AppSettingsData data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(data);
        string tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(tempPath, JsonSerializer.Serialize(data, SettingsJsonContext.Default.AppSettingsData));
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { /* best effort */ }
            throw;
        }
    }

    /// <summary>
    /// Removes the profile's directory and everything in it. No guard here: <see cref="DeleteRefusal"/>
    /// is the guard, and the caller runs it (and asks the user) first. Throws
    /// <see cref="IOException"/> / <see cref="UnauthorizedAccessException"/>.
    /// </summary>
    public static void Delete(string home, string name)
    {
        if (!IsValidName(name))
        {
            throw new ArgumentException(NameError, nameof(name));
        }

        string dir = Directory(home, name);
        if (System.IO.Directory.Exists(dir))
        {
            System.IO.Directory.Delete(dir, recursive: true);
        }

        DiagnosticLog.Info(Category, $"Deleted profile \"{name}\".");
    }

    /// <summary>
    /// Takes the profile's settings back to the compiled defaults: <c>profile.json</c> rewritten (the
    /// directory created when it was only logical, as <c>default</c>'s can be) and nothing else touched
    /// — since 2026-09-20 (the user's call) the memories, the persona, the operating rules and the
    /// voice directive stay, like the <c>files</c> sandbox and its <c>.trash</c> always did (every one
    /// of <see cref="SidekickFiles"/> went until then). No guard: <c>default</c> may be reset, and the
    /// caller asks the user first. The loaded profile is reset through
    /// <c>AppSettings.ResetProfileAsync</c>, which flushes its pending save first and reloads.
    /// Throws <see cref="IOException"/> / <see cref="UnauthorizedAccessException"/>; the caller reports.
    /// </summary>
    public static void Reset(string home, string name)
    {
        if (!IsValidName(name))
        {
            throw new ArgumentException(NameError, nameof(name));
        }

        string dir = Directory(home, name);
        System.IO.Directory.CreateDirectory(dir);
        WriteProfileFile(ProfileFile(home, name), new AppSettingsData());
    }

    /// <summary>
    /// Why <paramref name="name"/> may not be deleted while <paramref name="current"/> is loaded,
    /// or null when it may: the default never, the loaded one never (switch first, so the loaded
    /// configuration can never be pulled from under the running app).
    /// </summary>
    public static string? DeleteRefusal(string name, string current)
    {
        if (IsDefault(name))
        {
            return DefaultUndeletable;
        }

        if (NameEquals(name, current))
        {
            return CurrentUndeletable(name);
        }

        return null;
    }

    /// <summary>
    /// Why <paramref name="name"/> may not be reset while <paramref name="current"/> is loaded, or
    /// null when it may: the default only from itself (2026-09-22, the user's call — another
    /// profile cannot wipe the default's settings; <c>/profile reset</c> while it is loaded still
    /// can). Every other profile resets from anywhere.
    /// </summary>
    public static string? ResetRefusal(string name, string current) =>
        IsDefault(name) && !NameEquals(name, current) ? DefaultUnresettable : null;

    /// <summary>
    /// Moves the profile's directory — and everything in it: <c>profile.json</c>, the sidekick's
    /// files, the <c>files</c> sandbox with its <c>.trash</c> — under <paramref name="newName"/>.
    /// No guard here, like <see cref="Delete"/>: <see cref="RenameRefusal"/> and the
    /// <see cref="Exists"/> check on the new name are the caller's. Throws <see cref="IOException"/>
    /// (a target directory that is already there among them) / <see cref="UnauthorizedAccessException"/>;
    /// the caller reports.
    /// </summary>
    public static void Rename(string home, string name, string newName)
    {
        if (!IsValidName(name))
        {
            throw new ArgumentException(NameError, nameof(name));
        }

        if (!IsValidName(newName))
        {
            throw new ArgumentException(NameError, nameof(newName));
        }

        System.IO.Directory.Move(Directory(home, name), Directory(home, newName));
        DiagnosticLog.Info(Category, $"Renamed profile \"{name}\" to \"{newName}\".");
    }

    /// <summary>
    /// Why <paramref name="name"/> may not be renamed while <paramref name="current"/> is loaded,
    /// or null when it may: the default never (the pointer's fallback and the one profile that is
    /// listed without a directory), the loaded one never (its directory is in use — switch first).
    /// </summary>
    public static string? RenameRefusal(string name, string current)
    {
        if (IsDefault(name))
        {
            return DefaultUnrenamable;
        }

        if (NameEquals(name, current))
        {
            return CurrentUnrenamable(name);
        }

        return null;
    }
}
