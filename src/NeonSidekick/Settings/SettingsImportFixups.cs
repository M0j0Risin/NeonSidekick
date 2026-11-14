using System.Text.Json;
using System.Text.Json.Nodes;
using NeonSidekick.Shell;
using NeonSidekick.Sql;
using NeonSidekick.WebUi;

namespace NeonSidekick.Settings;

/// <summary>
/// What an imported profile's facts about this computer are (2026-11-13, plan parsed-yawning-papert): this computer's view
/// (<see cref="ImportEnvironment"/>: its OS, the file and folder checks, the secret reader, its default shell), the OS the zip came from
/// (<see cref="SettingsArchive.WindowsOs"/> or <see cref="SettingsArchive.MacOs"/>), the profile renames the import made, and, for the working
/// folder, this home's profiles folder and the name phase 1 planned for this profile (the review's #60 and #64: the environment was copied
/// field by field; a folder of another profile here was kept). Passed in, so tests drive both systems with no DPAPI or Keychain.
/// </summary>
public sealed record FixupContext(
    ImportEnvironment Environment,
    string SourceOs,
    IReadOnlyDictionary<string, string> Renames,
    string ProfilesRoot = "",
    string PlannedName = "")
{
    /// <summary>The OS the profile lands on.</summary>
    public string ThisOs => Environment.ThisOs;
}

/// <summary>What <see cref="SettingsImportFixups.Apply"/> did: the keys it reset (for the report) and whether it changed the file at all, silent edits included.</summary>
public sealed record FixupResult(IReadOnlyList<string> Reset, bool Changed);

/// <summary>
/// The resets an imported <c>profile.json</c> gets (2026-11-13, plan parsed-yawning-papert, "What the import resets"), applied to the file's
/// <see cref="JsonObject"/> so a key a newer version wrote is kept, never dropped by a typed round trip. Only the keys named here are touched:
/// secrets that don't decrypt here (blanked; the LLM key to its <c>empty</c> placeholder), the Web UI token always and a typed HTTPS address, a
/// working folder, Obsidian vault, browser or Claude Code path that isn't on this computer (or a folder of another profile here), and across
/// systems the default shell when this one lacks it, Docker's pipe, the editors and the forbidden shell commands' system section. The
/// desktop-shortcut flag and this PC's monitor cabling go silently; advisor names follow the import's renames. Values are never returned or logged.
/// </summary>
public static class SettingsImportFixups
{
    /// <summary>How the file is written back: indented, as <c>SettingsJsonContext</c> writes it; the resolver the AOT build takes (the <c>ComfyGraphs</c> precedent).</summary>
    internal static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, TypeInfoResolver = System.Text.Json.Serialization.Metadata.JsonTypeInfoResolver.Combine() };

    /// <summary>How the file is read: comments and trailing commas allowed, as a hand-edited file may have them.</summary>
    internal static readonly JsonDocumentOptions ReadOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>The shells each system always has (2026-11-13, the review's #52): kept across systems; pwsh on a Mac or bash on Windows may be missing.</summary>
    private static readonly string[] MacShells = { "zsh", "bash" };
    private static readonly string[] WindowsShells = { "powershell", "cmd" };

    /// <summary>
    /// The keys <see cref="Apply"/> looks at, beside the secrets (<see cref="SettingsSecrets.Keys"/>). With <see cref="CarriedOnPurpose"/> it
    /// covers <see cref="Profiles.ResetKeptSettings"/> (the machine facts a plain reset keeps), which a test holds (the review's #31).
    /// </summary>
    internal static readonly string[] Handled =
    {
        nameof(AppSettingsData.WebUiToken),
        nameof(AppSettingsData.WebUiHttpsAddress),
        nameof(AppSettingsData.WorkingDirectory),
        nameof(AppSettingsData.ObsidianVault),
        nameof(AppSettingsData.WebBrowserPath),
        nameof(AppSettingsData.ClaudeCodeExecutable),
        nameof(AppSettingsData.ShortcutOffered),
        nameof(AppSettingsData.MonitorPcInputs),
        nameof(AppSettingsData.MonitorInputsSeen),
        nameof(AppSettingsData.ShellDefault),
        nameof(AppSettingsData.DockerEnginePipe),
        nameof(AppSettingsData.ImageEditor),
        nameof(AppSettingsData.DraftEditor),
        nameof(AppSettingsData.ShellPoliceForbiddenStrings),
        nameof(AppSettingsData.AdvisorList),
    };

    /// <summary>
    /// Machine facts a plain reset keeps that the import carries as they are: servers reached the same way from either computer (the LLM, TTS,
    /// SearXNG, ComfyUI, Home Assistant), the search method, the avatars (a profile's face, not this PC's), and the monitors' input names and
    /// hidden inputs (they name the monitor's inputs; which one is this PC's goes, <see cref="AppSettingsData.MonitorPcInputs"/>).
    /// </summary>
    internal static readonly string[] CarriedOnPurpose =
    {
        nameof(AppSettingsData.LlmUrl),
        nameof(AppSettingsData.LlmModel),
        nameof(AppSettingsData.TtsHttpUrl),
        nameof(AppSettingsData.WebSearchMethod),
        nameof(AppSettingsData.WebSearxngUrl),
        nameof(AppSettingsData.ComfyUrl),
        nameof(AppSettingsData.HomeAssistantUrl),
        nameof(AppSettingsData.ModelAvatar),
        nameof(AppSettingsData.UserAvatar),
        nameof(AppSettingsData.MonitorInputNames),
        nameof(AppSettingsData.MonitorInputsHidden),
    };

    /// <summary>Applies the resets to <paramref name="root"/> in place: the keys reset, in a stable order, and whether anything changed.</summary>
    public static FixupResult Apply(JsonObject root, FixupContext context)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(context);
        var reset = new List<string>();
        bool changed = false;
        bool windows = context.ThisOs == SettingsArchive.WindowsOs;
        bool crossOs = context.SourceOs != context.ThisOs;

        foreach (var (key, fallback) in SettingsSecrets.Keys)
        {
            if (Text(root, key, out string actual) is not { } value || value.Trim().Length == 0)
            {
                continue;
            }

            string trimmed = value.Trim();
            if (key == nameof(AppSettingsData.LlmApiKey) && trimmed == Llm.LlmEndpoint.DefaultApiKey)
            {
                continue;
            }

            if (!Readable(trimmed, windows, context.Environment.Reveal))
            {
                root[actual] = fallback;
                reset.Add(key);
            }
        }

        // The Web UI's token never travels between profiles (the Profiles.CopySettings rule): blank, minted at the first serve.
        if (Text(root, nameof(AppSettingsData.WebUiToken), out string tokenKey) is { } token && token.Trim().Length > 0)
        {
            root[tokenKey] = "";
            reset.Add(nameof(AppSettingsData.WebUiToken));
        }

        // A typed HTTPS address is the old PC's (the setting's own doc: "the PC's alone"); the new token's link would go there (the review's #65).
        if (Text(root, nameof(AppSettingsData.WebUiHttpsAddress), out string httpsKey) is { } https && WebUiHttpsSetting.Address(https) is not null)
        {
            root[httpsKey] = WebUiHttpsSetting.Default;
            reset.Add(nameof(AppSettingsData.WebUiHttpsAddress));
        }

        ResetMissingFolder(root, nameof(AppSettingsData.WorkingDirectory), context, windows, reset, otherProfiles: true);
        ResetMissingFolder(root, nameof(AppSettingsData.ObsidianVault), context, windows, reset, otherProfiles: false);
        ResetMissingFile(root, nameof(AppSettingsData.WebBrowserPath), v => v.Trim(), context, reset);
        ResetMissingFile(root, nameof(AppSettingsData.ClaudeCodeExecutable), v => v.Trim().Trim('"'), context, reset);

        // Machine facts, not reported: this computer hasn't offered the desktop shortcut yet, and which monitor input is this PC's (and the
        // inputs the pane has seen, a cache it rebuilds) describes the old PC's cabling (the review's #23).
        changed |= Remove(root, nameof(AppSettingsData.ShortcutOffered));
        changed |= Remove(root, nameof(AppSettingsData.MonitorPcInputs));
        changed |= Remove(root, nameof(AppSettingsData.MonitorInputsSeen));

        if (crossOs)
        {
            // Blank means the default (the review's #56); a shell this system always has stays (#52).
            if (Text(root, nameof(AppSettingsData.ShellDefault), out string shellKey) is { } shell && shell.Trim().Length > 0
                && !(windows ? WindowsShells : MacShells).Contains(shell.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                root[shellKey] = context.Environment.PlatformShell;
                reset.Add(nameof(AppSettingsData.ShellDefault));
            }

            if (Text(root, nameof(AppSettingsData.DockerEnginePipe), out string pipeKey) is { } pipe && pipe.Trim().Length > 0
                && !string.Equals(pipe.Trim(), Docker.DockerPipe.DefaultName, StringComparison.Ordinal))
            {
                root[pipeKey] = Docker.DockerPipe.DefaultName;
                reset.Add(nameof(AppSettingsData.DockerEnginePipe));
            }

            // An editor is a program of the other system (the review's #46): back to the system's default.
            foreach (string editor in new[] { nameof(AppSettingsData.ImageEditor), nameof(AppSettingsData.DraftEditor) })
            {
                if (Text(root, editor, out string editorKey) is { } value && value.Trim().Length > 0)
                {
                    root[editorKey] = "";
                    reset.Add(editor);
                }
            }

            if (SwapForbiddenStrings(root, windows))
            {
                reset.Add(nameof(AppSettingsData.ShellPoliceForbiddenStrings));
            }
        }

        // Advisors follow the renames; a correction, not a reset (the review's #38).
        changed |= RenameAdvisors(root, context.Renames);
        return new FixupResult(reset, changed || reset.Count > 0);
    }

    /// <summary>
    /// Whether a stored secret reads on this computer: plain text does; a value encrypted on the other system never does, told by its prefix
    /// alone (so a Mac never makes its Keychain key for a Windows blob); one of this system's reads when <paramref name="reveal"/> opens it.
    /// </summary>
    internal static bool Readable(string value, bool windows, Func<string, string?> reveal)
    {
        if (!WindowsCredentials.IsProtected(value))
        {
            return true;
        }

        bool foreign = windows
            ? value.StartsWith(WindowsCredentials.KeychainPrefix, StringComparison.Ordinal)
            : value.StartsWith(WindowsCredentials.ProtectedPrefix, StringComparison.Ordinal);
        return !foreign && reveal(value) is not null;
    }

    /// <summary>
    /// A folder setting reset when it isn't a full path to a folder here. On Windows a share (<c>\\server\share</c>) is kept as typed, unprobed:
    /// it may be offline or slow at import (the review's #55); on a Mac a share is a <c>/Volumes</c> mount, so one is reset, told by
    /// <see cref="FixupContext.ThisOs"/> and not the host's path rules (a test on Windows drives a Mac). With <paramref name="otherProfiles"/>, a
    /// folder inside this home's <c>profiles/</c> under another profile's name is reset too (#64: <c>work-imported</c> sharing <c>work</c>'s sandbox).
    /// </summary>
    private static void ResetMissingFolder(JsonObject root, string key, FixupContext context, bool windows, List<string> reset, bool otherProfiles)
    {
        if (Text(root, key, out string actual) is not { } value || value.Trim().Length == 0)
        {
            return;
        }

        string path = value.Trim();
        bool unc = path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);
        bool keep = unc
            ? windows
            : Path.IsPathFullyQualified(path) && context.Environment.DirectoryExists(path) && !(otherProfiles && InAnotherProfile(path, context, windows));
        if (!keep)
        {
            root[actual] = "";
            reset.Add(key);
        }
    }

    /// <summary>Whether <paramref name="path"/> is inside this home's profiles folder, under a profile other than the one being imported.</summary>
    private static bool InAnotherProfile(string path, FixupContext context, bool windows)
    {
        if (context.ProfilesRoot.Length == 0 || context.PlannedName.Length == 0)
        {
            return false;
        }

        var comparison = windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string root = Path.TrimEndingDirectorySeparator(context.ProfilesRoot);
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        foreach (char separator in new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar })
        {
            if (full.StartsWith(root + separator, comparison))
            {
                string profile = full[(root.Length + 1)..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                return profile.Length > 0 && !Profiles.NameEquals(profile, context.PlannedName);
            }
        }

        return false;
    }

    private static void ResetMissingFile(JsonObject root, string key, Func<string, string> normalize, FixupContext context, List<string> reset)
    {
        if (Text(root, key, out string actual) is not { } value || value.Trim().Length == 0)
        {
            return;
        }

        string path = normalize(value);
        if (!Path.IsPathFullyQualified(path) || !context.Environment.FileExists(path))
        {
            root[actual] = "";
            reset.Add(key);
        }
    }

    /// <summary>
    /// Across systems, the forbidden shell commands' system section swapped (2026-11-13, the review's #104: a full list saved on Windows kept
    /// Windows' entries and lacked the Mac's, a safety layer weakened): an entry only the source system's section has goes, each of this system's
    /// is added, the user's own stay; written back in the editor's order. An empty list (a profile from before the defaults chose it) and a
    /// missing key (the loader gives this system's defaults) are left alone. True when the list changed.
    /// </summary>
    private static bool SwapForbiddenStrings(JsonObject root, bool windows)
    {
        if (Key(root, nameof(AppSettingsData.ShellPoliceForbiddenStrings)) is not { } key || root[key] is not JsonArray array || array.Count == 0)
        {
            return false;
        }

        var saved = new List<string>();
        foreach (var item in array)
        {
            if (item is not JsonValue value || value.GetValueKind() != JsonValueKind.String)
            {
                return false;   // not a list of strings: the loader decides, the import doesn't guess
            }

            saved.Add(value.GetValue<string>());
        }

        string[] shared = ForbiddenStrings.SharedDefaults;
        string[] source = windows ? ForbiddenStrings.MacDefaults : ForbiddenStrings.WindowsDefaults;
        string[] target = windows ? ForbiddenStrings.WindowsDefaults : ForbiddenStrings.MacDefaults;
        var before = ForbiddenStrings.Sorted(saved);
        var list = before.Where(entry => !(ForbiddenStrings.Contains(source, entry) && !ForbiddenStrings.Contains(shared, entry) && !ForbiddenStrings.Contains(target, entry))).ToList();
        foreach (string entry in target)
        {
            list = ForbiddenStrings.Add(list, entry);
        }

        list = ForbiddenStrings.Sorted(list);
        if (list.SequenceEqual(before, StringComparer.OrdinalIgnoreCase))
        {
            return false;   // the same entries (order or spacing aside): nothing changed, and the file is left as it came
        }

        root[key] = new JsonArray([.. list.Select(entry => (JsonNode?)JsonValue.Create(entry))]);
        return true;
    }

    /// <summary>Removes <paramref name="name"/> (any case); true when it was there.</summary>
    private static bool Remove(JsonObject root, string name)
    {
        if (Key(root, name) is not { } key)
        {
            return false;
        }

        root.Remove(key);
        return true;
    }

    /// <summary>Points each advisor naming a renamed zip profile at its new name; true when any changed.</summary>
    private static bool RenameAdvisors(JsonObject root, IReadOnlyDictionary<string, string> renames)
    {
        if (renames.Count == 0 || Key(root, nameof(AppSettingsData.AdvisorList)) is not { } listKey || root[listKey] is not JsonArray list)
        {
            return false;
        }

        bool changed = false;
        foreach (var item in list)
        {
            if (item is JsonObject advisor && Text(advisor, "Profile", out string profileKey) is { } name
                && renames.FirstOrDefault(r => Profiles.NameEquals(r.Key, name.Trim())) is { Key: not null } rename)
            {
                advisor[profileKey] = rename.Value;
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// The key of <paramref name="root"/> matching <paramref name="name"/>, exactly or else ignoring case, or null: a hand-edited file may spell a
    /// key differently, so the reset finds it where it sits.
    /// </summary>
    private static string? Key(JsonObject root, string name)
    {
        if (root.ContainsKey(name))
        {
            return name;
        }

        foreach (var pair in root)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Key;
            }
        }

        return null;
    }

    /// <summary>The string value under <paramref name="name"/> (any case), with the key it sits under; null when absent or not a string.</summary>
    private static string? Text(JsonObject root, string name, out string actual)
    {
        actual = Key(root, name) ?? name;
        return root[actual] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
    }
}
