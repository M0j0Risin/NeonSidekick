using System.Text.Json;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Settings;

/// <summary>
/// The profile directory layout and its rules, as pure statics over a home directory:
/// <c>&lt;home&gt;\profiles\&lt;name&gt;\</c> holds one sidekick's <c>profile.json</c>, <c>persona.md</c>,
/// <c>operata.md</c> and <c>vocalia.md</c>, and every other settings file in <c>configs\</c> (<see cref="ConfigPath"/>:
/// <c>memory.json</c>, <c>mcp.json</c>, the connection files, the saved lists; 2026-10-16, the user's call, no migration:
/// a file left beside <c>profile.json</c> is not read; the home's own JSON files in <c>&lt;home&gt;\configs</c> too, <see cref="HomeConfigPath"/>). Nothing here knows which profile is loaded; the
/// guards that need that (<see cref="DeleteRefusal"/>) take it as an argument, so every rule and
/// every sentence is testable without a screen.
///
/// <para>Names are compared ignoring case (the directory is on NTFS) and a typed name is
/// <see cref="Resolve"/>d to the directory's spelling before it is stored, so the pointer file
/// and the status panel always show the name the way the disk does. <see cref="DefaultName"/>
/// exists logically even when its directory does not: it is always listed, always resolves and
/// can never be deleted or renamed.</para>
///
/// <para>The default is called <c>neon</c> since 2026-10-09 (the user's call): it was <c>default</c> until then, with the
/// window title (<c>Neon</c>) special-cased over it; now the title and <c>/botchat</c> show it as they show any profile.
/// No migration and no alias: <c>default</c> is an ordinary name now, and a home from before moves its folder by hand.</para>
/// </summary>
public static class Profiles
{
    public const string DirectoryName = "profiles";
    public const string DefaultName = "neon";
    public const string FileName = "profile.json";
    public const int MaxNameLength = 32;

    /// <summary>The log category, shared with <see cref="AppSettings"/>.</summary>
    private const string Category = "Settings";

    /// <summary>
    /// The sidekick's own files (the <c>.json</c> ones in <c>configs</c> since 2026-10-16, <see cref="SidekickFilePath"/>), in the order <see cref="CopySidekickFiles"/>
    /// takes them: the memories, the persona, the operating rules, the voice directive, the MCP servers
    /// (2026-09-20), the saved prompts (2026-10-13). Literals; <c>ProfilesTests</c> pins each to its owner's <c>FileName</c>. <c>/profile add</c>
    /// copies only <see cref="BasicSidekickFiles"/>; <c>/profile push|pull</c>'s sections move the rest (<c>mcp</c> since 2026-10-25).
    /// </summary>
    public static readonly string[] SidekickFiles = { "memory.json", "persona.md", "operata.md", "vocalia.md", "mcp.json", "prompts.json" };

    /// <summary>
    /// The subset of <see cref="SidekickFiles"/> <c>/profile add</c> copies: the memories alone (the user's call 2026-09-15, then the
    /// <c>basic</c> mode of <c>New profile mode</c>). Since 2026-10-10 (the user's call) it is the only way: the setting and its
    /// <c>advanced</c> mode (the persona, operating rules and voice directive too) were retired; <c>/profile push|pull</c> copies those. The saved prompts came along too from 2026-10-13 (the user's pick).
    /// </summary>
    public static readonly string[] BasicSidekickFiles = { "memory.json", "prompts.json" };

    /// <summary>
    /// The <c>/profile</c> subcommand words (<c>edit</c> and <c>reload</c> since 2026-09-21, <c>push</c> and <c>pull</c> since 2026-09-28,
    /// and push and pull's section words since 2026-10-09, the prompt files' among them since 2026-10-10 and <c>memory</c> since 2026-10-13 — <c>ChatScreen.ProfileSectionWords</c> — so <c>/profile push keys work</c> never
    /// reads as a profile called keys); a profile cannot be called any of them, so <c>/profile add</c> is never a switch and
    /// <c>/profile reset</c> is always the loaded one. 2026-10-25 (the user's call): <c>links</c>, <c>mcp</c>, <c>monitors</c>, <c>skills</c> and
    /// <c>videos</c> with their sections; <c>avatar</c>, the first word of the two-word avatar sections, never reserved until then; and
    /// <c>overwrite</c>, the one word push and pull read where a name goes. 2026-10-26 (the user's ask): <c>barorder</c>, <c>homeassistant</c>,
    /// <c>perfbar</c>, <c>statusbar</c> and <c>toolbar</c> with their sections. A profile elsewhere with one of these names drops out of
    /// <see cref="List"/>, as one did when <c>memory</c> and <c>prompts</c> came; no migration. A to Z.
    /// </summary>
    public static readonly string[] ReservedNames = { "add", "allowed", "avatar", "barorder", "delete", "edit", "history", "homeassistant", "keys", "links", "mcp", "memory", "monitors", "operata", "overwrite", "perfbar", "persona", "policestrings", "prompts", "pull", "push", "reload", "rename", "reset", "servers", "skills", "statusbar", "toolbar", "videos", "vocalia" };

    /// <summary>
    /// The wording for a name <see cref="IsValidName"/> refuses, every <see cref="ReservedNames"/> word in it (2026-10-26, the review's R13/R46:
    /// a hand list named eight of the thirty, so a refused <c>toolbar</c> read as a mystery). Pinned by shape.
    /// </summary>
    public static readonly string NameError =
        "must be 1 to 32 letters, digits, - or _, not starting with -, and not a word /profile reads (" + string.Join(", ", ReservedNames) + ")";

    /// <summary>Why <c>neon</c> cannot be deleted. Pinned.</summary>
    public const string DefaultUndeletable = "The default profile cannot be deleted.";

    /// <summary>Why <c>neon</c> cannot be reset from another profile (2026-09-22). Pinned.</summary>
    public const string DefaultUnresettable = "The default profile can only be reset while it is loaded.";

    /// <summary>Why the loaded profile cannot be deleted. Pinned.</summary>
    public static string CurrentUndeletable(string name) =>
        $"\"{name}\" is the current profile; switch to another (/profile <name>) before deleting it.";

    /// <summary>Why <c>neon</c> cannot be renamed. Pinned.</summary>
    public const string DefaultUnrenamable = "The default profile cannot be renamed.";

    /// <summary>Why the loaded profile cannot be renamed. Pinned.</summary>
    public static string CurrentUnrenamable(string name) =>
        $"\"{name}\" is the current profile; switch to another (/profile <name>) before renaming it.";

    /// <summary>
    /// Whether <paramref name="name"/> can name a profile: one to <see cref="MaxNameLength"/>
    /// ASCII letters, digits, hyphens or underscores, and not a <see cref="ReservedNames"/> word
    /// (<c>neon</c> was refused too from 2026-09-21 until it became <see cref="DefaultName"/> on 2026-10-09).
    /// Nothing that could be a path segment trick, a shell surprise or a non-ASCII directory
    /// (the Vosk path rule is a reminder of what those cost). Since 2026-10-25 (the user's call) never starting with <c>-</c>, so a
    /// profile can't look like a flag (<c>/profile reset --all</c>, <c>/botchat --resume</c>, <c>/shortcut --log</c>).
    /// </summary>
    public static bool IsValidName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength || name[0] == '-')
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

        return !ReservedNames.Contains(name, StringComparer.OrdinalIgnoreCase);
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

    /// <summary>The folder of a profile's settings files beside <see cref="FileName"/> (<see cref="ConfigsDirectory"/>).</summary>
    public const string ConfigsDirectoryName = "configs";

    /// <summary>
    /// <c>&lt;profile&gt;\configs</c> (2026-10-16, the user's call): where a profile keeps every JSON settings file but its
    /// <c>profile.json</c> — the memories, the MCP servers, the saved prompts, tests, videos and links, the browser's history, and
    /// the connection and computer files; the home's own files in <c>&lt;home&gt;\configs</c> too (<see cref="HomeConfigPath"/>, 2026-10-16, no migration).
    /// No migration: a file left at the profile's or the home's top is not read.
    /// </summary>
    public static string ConfigsDirectory(string profileDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);
        return Path.Combine(profileDirectory, ConfigsDirectoryName);
    }

    /// <summary><c>&lt;profile&gt;\configs\&lt;fileName&gt;</c>: a profile's settings file, every family's <c>ProfilePath</c> and every profile store's file.</summary>
    public static string ConfigPath(string profileDirectory, string fileName) => Path.Combine(ConfigsDirectory(profileDirectory), fileName);

    /// <summary>
    /// <c>&lt;home&gt;\configs\&lt;fileName&gt;</c> (2026-10-16, the user's call, no migration): the home's own JSON files, every family's
    /// <c>GlobalPath</c> (and <c>emoji.json</c> until 2026-10-17, a profile's since), in the home's <c>configs</c> as a profile's are in its own. A file left at the home's top is not read.
    /// </summary>
    public static string HomeConfigPath(string home, string fileName) => ConfigPath(home, fileName);

    /// <summary>Where one of <see cref="SidekickFiles"/> lives in <paramref name="profileDirectory"/>: a <c>.json</c> in <c>configs</c>, the prompt files at the top.</summary>
    public static string SidekickFilePath(string profileDirectory, string fileName) =>
        fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? ConfigPath(profileDirectory, fileName) : Path.Combine(profileDirectory, fileName);

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
    /// along only through <see cref="CopySidekickFiles"/>, which the caller runs over <see cref="BasicSidekickFiles"/>. Throws <see cref="IOException"/> /
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
    /// <paramref name="only"/> (<see cref="BasicSidekickFiles"/>) just the ones it names. Never the
    /// <c>files</c> sandbox: that is the sidekick's own. Throws
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

            string to = SidekickFilePath(target, fileName);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(SidekickFilePath(Path.GetFullPath(sourceDirectory), fileName), to);
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
            if (File.Exists(SidekickFilePath(root, fileName)))
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
        "prompts.json" => "saved prompts",
        _ => "",
    };

    /// <summary>
    /// Reads a <c>profile.json</c> that is not the loaded one, to edit and <see cref="WriteProfileFile"/>
    /// back (<c>/cmdcopy</c>, 2026-09-21; <c>/profile push|pull</c>'s sections since 2026-10-09, a pull reading its source). A missing file is the compiled defaults: the profile is
    /// logical, as <c>neon</c>'s can be, or was never saved, and that is what loading it would give.
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

        var data = JsonSerializer.Deserialize(File.ReadAllText(path), AppSettings.ReadInfo)
            ?? throw new JsonException("The settings file parsed to null.");
        RetiredWords.Upgrade(data);   // 2026-10-13: a retired setting word reads as its successor, as AppSettings.Load does
        return data;
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
    /// directory created when it was only logical, as <c>neon</c>'s can be) and nothing else touched
    /// — since 2026-09-20 (the user's call) the memories, the persona, the operating rules and the
    /// voice directive stay, like the <c>files</c> sandbox always did (every one
    /// of <see cref="SidekickFiles"/> went until then). No guard: <c>neon</c> may be reset, and the
    /// caller asks the user first. The loaded profile is reset through
    /// <c>AppSettings.ResetProfileAsync</c>, which flushes its pending save first and reloads.
    /// Since 2026-09-27 (the user's call) the settings that name the user's own setup —
    /// <see cref="ResetKeptSettings"/>, read back from the file there — survive it unless
    /// <paramref name="all"/> (<c>/profile reset --all</c>) asks for everything.
    /// Throws <see cref="IOException"/> / <see cref="UnauthorizedAccessException"/>; the caller reports.
    /// </summary>
    public static void Reset(string home, string name, bool all = false)
    {
        if (!IsValidName(name))
        {
            throw new ArgumentException(NameError, nameof(name));
        }

        string dir = Directory(home, name);
        System.IO.Directory.CreateDirectory(dir);
        string path = ProfileFile(home, name);
        var data = new AppSettingsData();
        if (!all)
        {
            try
            {
                KeepOnReset(ReadProfileFile(path), data);
            }
            catch (JsonException ex)
            {
                // A file that will not parse is what a reset cures: nothing to keep, the defaults whole.
                DiagnosticLog.Warn(Category, $"Reset profile \"{name}\": profile.json did not parse ({ex.Message}); nothing kept.");
            }
        }

        WriteProfileFile(path, data);
    }

    /// <summary>
    /// Copies one profile's settings over another's (2026-09-28, <c>/profile push</c> and <c>/profile pull</c>, the
    /// user's ask): <paramref name="from"/>'s <c>profile.json</c> written as <paramref name="to"/>'s, every setting but
    /// <see cref="AppSettingsData.WorkingDirectory"/> and <see cref="AppSettingsData.ModelAvatar"/> (2026-10-10), which the target keeps (the user's call: a sandbox is the
    /// sidekick's own, as <c>/profile add</c> already says by clearing it). The settings alone — the memories, the prompt
    /// files and <c>mcp.json</c> stay as they are, <c>/profile push|pull memory</c> (<c>/memory copy</c> until 2026-10-13), <c>persona</c>/<c>operata</c>/<c>vocalia</c>
    /// and <c>mcp</c> (2026-10-25) being the way to move those. The monitor settings go with the rest, replacing the target's; the
    /// <c>monitors</c> section (2026-10-25) merges them instead. The keys go as stored, protected or not (<see cref="KeepOnReset"/>'s reasoning). A missing source is the
    /// compiled defaults, as loading it would give; a corrupt one <b>throws</b> <see cref="JsonException"/> before anything
    /// is written (<see cref="ReadProfileFile"/>). A corrupt target is what the copy cures: nothing kept from it, logged.
    /// The target's folder is created when it is only logical, as <c>neon</c>'s can be. No guard, like <see cref="Reset"/>:
    /// any profile may overwrite any other, <c>neon</c> included (the user's call); the caller asks first. The loaded
    /// profile goes through <c>AppSettings.CopyProfileSettingsAsync</c>, which flushes its pending save first.
    /// Throws <see cref="IOException"/> / <see cref="UnauthorizedAccessException"/>; the caller reports.
    /// </summary>
    public static void CopySettings(string home, string from, string to)
    {
        if (!IsValidName(from))
        {
            throw new ArgumentException(NameError, nameof(from));
        }

        if (!IsValidName(to))
        {
            throw new ArgumentException(NameError, nameof(to));
        }

        var data = ReadProfileFile(ProfileFile(home, from));
        string path = ProfileFile(home, to);
        string keptDirectory = "";
        string keptAvatar = "";
        string keptToken = "";
        bool? keptOffered = null;
        try
        {
            var target = ReadProfileFile(path);
            keptDirectory = target.WorkingDirectory;
            keptAvatar = target.ModelAvatar;   // 2026-10-10: the target's own face, never the source's
            keptToken = target.WebUiToken;     // 2026-10-14: a Web UI token never travels between profiles (a new one is minted when it first serves)
            keptOffered = target.ShortcutOffered;   // 2026-10-16: whether the target was asked for its shortcut is its own
        }
        catch (JsonException ex)
        {
            DiagnosticLog.Warn(Category, $"Copy into profile \"{to}\": profile.json did not parse ({ex.Message}); nothing kept.");
        }

        data.WorkingDirectory = keptDirectory;
        data.ModelAvatar = keptAvatar;
        data.WebUiToken = keptToken;
        data.ShortcutOffered = keptOffered;
        System.IO.Directory.CreateDirectory(Directory(home, to));
        WriteProfileFile(path, data);
        DiagnosticLog.Info(Category, $"Copied profile \"{from}\"'s settings to \"{to}\".");
    }

    /// <summary>
    /// What a plain <c>/profile reset</c> keeps (2026-09-27, the user's call): the servers, paths and
    /// key that describe this machine rather than a taste — a reset that took the LLM's URL and model
    /// back to empty left the sidekick unable to answer (the LLM's API key joined them later that day,
    /// the user's call; the Home Assistant server and its token on 2026-09-28, beside ComfyUI's; the YouTube API key on 2026-10-05, the user's ask; whether the
    /// profile offered its shortcut on 2026-10-16; the monitors' input names and hidden inputs on 2026-10-23, the monitors plan: they describe
    /// this PC's cabling; which input of each is this PC's later that day). <c>--all</c> resets them too. The property
    /// names, in <see cref="KeepOnReset"/>'s order.
    /// </summary>
    public static readonly string[] ResetKeptSettings =
    {
        nameof(AppSettingsData.LlmUrl),
        nameof(AppSettingsData.LlmModel),
        nameof(AppSettingsData.LlmApiKey),
        nameof(AppSettingsData.TtsHttpUrl),
        nameof(AppSettingsData.AnthropicApiKey),
        nameof(AppSettingsData.OpenAIApiKey),
        nameof(AppSettingsData.WebBrowserPath),
        nameof(AppSettingsData.WebSearchMethod),
        nameof(AppSettingsData.WebSearxngUrl),
        nameof(AppSettingsData.ClaudeCodeExecutable),
        nameof(AppSettingsData.ObsidianVault),
        nameof(AppSettingsData.ComfyUrl),
        nameof(AppSettingsData.HomeAssistantUrl),
        nameof(AppSettingsData.HomeAssistantToken),
        nameof(AppSettingsData.YouTubeApiKey),
        nameof(AppSettingsData.ModelAvatar),
        nameof(AppSettingsData.UserAvatar),
        nameof(AppSettingsData.WebUiToken),
        nameof(AppSettingsData.ShortcutOffered),
        nameof(AppSettingsData.MonitorInputNames),
        nameof(AppSettingsData.MonitorInputsHidden),
        nameof(AppSettingsData.MonitorPcInputs),
    };

    /// <summary>Copies <see cref="ResetKeptSettings"/> from <paramref name="from"/> onto <paramref name="to"/>, one by one (no reflection under AOT). The keys go as stored, protected or not.</summary>
    internal static void KeepOnReset(AppSettingsData from, AppSettingsData to)
    {
        to.LlmUrl = from.LlmUrl;
        to.LlmModel = from.LlmModel;
        to.LlmApiKey = from.LlmApiKey;
        to.TtsHttpUrl = from.TtsHttpUrl;
        to.AnthropicApiKey = from.AnthropicApiKey;
        to.OpenAIApiKey = from.OpenAIApiKey;
        to.WebBrowserPath = from.WebBrowserPath;
        to.WebSearchMethod = from.WebSearchMethod;
        to.WebSearxngUrl = from.WebSearxngUrl;
        to.ClaudeCodeExecutable = from.ClaudeCodeExecutable;
        to.ObsidianVault = from.ObsidianVault;
        to.ComfyUrl = from.ComfyUrl;
        to.HomeAssistantUrl = from.HomeAssistantUrl;
        to.HomeAssistantToken = from.HomeAssistantToken;
        to.YouTubeApiKey = from.YouTubeApiKey;   // 2026-10-05, the user's ask: a plain reset keeps it, as the other keys
        to.ModelAvatar = from.ModelAvatar;       // 2026-10-10: the profile's face, as its name, not a taste a reset puts back
        to.UserAvatar = from.UserAvatar;         // 2026-10-12: the user's own face, kept as the model's is
        to.WebUiToken = from.WebUiToken;         // 2026-10-14: a phone signed in stays signed in once the Web UI is on again
        to.ShortcutOffered = from.ShortcutOffered; // 2026-10-16: a profile asked once is not asked again after a plain reset
        to.MonitorInputNames = from.MonitorInputNames;     // 2026-10-23: this PC's cabling, not a taste
        to.MonitorInputsHidden = from.MonitorInputsHidden;
        to.MonitorPcInputs = from.MonitorPcInputs;         // later on 2026-10-23: which input is this PC's, the same cabling
        // The embedded switch with a kept embedded URL (2026-10-22, a review: off by default since 2026-10-21, so a reset kept the URL and model and
        // took the switch away, and the saved URL then read as none — the failure the kept settings are for). Not one of ResetKeptSettings: it
        // follows the URL.
        if (EmbeddedLlm.EmbeddedEndpoint.IsEmbedded(from.LlmUrl))
        {
            to.EmbeddedLlmServer = from.EmbeddedLlmServer;
        }
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
    /// files, the <c>files</c> sandbox — under <paramref name="newName"/>.
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
