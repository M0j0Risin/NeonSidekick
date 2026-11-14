using System.Globalization;
using NeonSidekick.Files;

namespace NeonSidekick.App;

/// <summary>
/// The words of <c>/settings export</c>, <c>/settings import</c> and <c>--import-settings</c> (2026-11-13, plan parsed-yawning-papert, the user's
/// ask: carry the settings to another computer with a small footprint). The report is one notice per group (a notice is one line), the
/// same list for the screen, headless and the start flag.
/// </summary>
public static class SettingsTransferText
{
    /// <summary>The diagnostic log's category.</summary>
    public const string Category = "SettingsTransfer";

    /// <summary>The <c>/settings</c> verbs.</summary>
    public const string ExportWord = "export";
    public const string ImportWord = "import";

    /// <summary>The file name's stem and the zip's extension: <c>neonsidekick-settings-&lt;yyyyMMdd-HHmmss&gt;.zip</c>.</summary>
    public const string FilePrefix = "neonsidekick-settings-";
    public const string ZipExtension = ".zip";

    /// <summary>How many zips a bare <c>/settings import</c> lists, newest first.</summary>
    public const int ListedZips = 5;

    /// <summary>How many names a notice lists before "+N more" (the whole list goes to the log).</summary>
    public const int ListedNames = 10;

    public const string ExportingSpinner = "Exporting settings…";
    public const string ImportingSpinner = "Importing settings…";

    /// <summary>The line an interactive <c>--import-settings</c> prints before the screen, as the start's update step does (the review's #108).</summary>
    public const string ImportingWait = "Importing settings before the start…";

    /// <summary>A typed path no file can have (a character the system refuses, or nothing left once unquoted).</summary>
    public static string BadPath(string typed) => $"{typed} isn't a path a file can have.";

    /// <summary>The default file name for <paramref name="localNow"/>.</summary>
    public static string DefaultFileName(DateTime localNow) =>
        FilePrefix + localNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ZipExtension;

    // ─── export ─────────────────────────────────────────────────────────────────

    public const string NothingToExport = "Nothing to export yet: no profile or home file holds a setting.";

    public static string Exported(string path, int profiles, long bytes) =>
        $"Settings exported to {path}: {Count(profiles, "profile", "profiles")}, {FileText.Size(bytes)}.";

    public static string LeftOut(IReadOnlyList<string> names) => $"Left out (not files the app reads as settings): {Listed(names)}.";

    /// <summary>The notes a left-out name carries.</summary>
    public const string LinkNote = " (a link)";
    public const string NotPortableNote = " (a name Windows can't hold)";
    public const string CaseNote = " (differs from another only by case)";
    public const string NoSkillFileNote = " (no SKILL.md)";
    public const string SkillBuildNote = " (a skill's packages or build output)";
    public const string GoneNote = " (removed during the export)";

    public const string KeepPrivate =
        "The zip holds your persona, memory and MCP env and headers as written: keep it private, and delete it once it's imported.";

    public static string TooBig(int files, long bytes, IReadOnlyList<string> biggest) =>
        $"Nothing exported: {Count(files, "file", "files")} and {FileText.Size(bytes)} is past the {Settings.SettingsArchive.MaxEntries.ToString("N0", CultureInfo.InvariantCulture)}-file, "
        + $"{FileText.Size(Settings.SettingsArchive.MaxBytes)} limit. The biggest: {string.Join(", ", biggest)}.";

    public static string Unreadable(string relative, string reason) => $"Nothing exported: {relative} can't be read ({reason}).";

    public static string InsideHome(string home) => $"The zip can't go inside the app's home ({home}); pick another folder.";

    public static string AlreadyThere(string path) => $"{path} already exists; pick another name.";

    public static string CannotWrite(string path, string reason) => $"Nothing exported: {path} can't be written ({reason}).";

    // ─── import: refusals ────────────────────────────────────────────────────────

    public static string Missing(string path) => $"There's no file at {path}.";

    public const string NotZip = "That isn't a zip.";

    public static string Damaged(string reason) => $"The zip is damaged or wasn't made by this app ({reason}); nothing was imported.";

    public const string NoManifest = "The zip wasn't made by /settings export (it has no neon-settings.json); nothing was imported.";

    public static string NewerFormat(int format) => $"The zip is format {format.ToString(CultureInfo.InvariantCulture)}, newer than this app reads; update the app first. Nothing was imported.";

    public static string UnsafePath(string name) => $"The zip holds a path no settings file has ({name}); nothing was imported.";

    public static string CaseClash(string name) => $"The zip holds two entries that differ only by case ({name}); nothing was imported.";

    public static string DuplicateEntry(string name) => $"The zip holds {name} twice; nothing was imported.";

    public const string ManifestTooBig = "its neon-settings.json is too big";

    /// <summary>The reason a damaged entry gives (an entry's body longer than its header says).</summary>
    public const string EntryTooLong = "an entry is longer than it says";

    public static string TooManyEntries(int count) =>
        $"The zip holds {count.ToString("N0", CultureInfo.InvariantCulture)} entries, past the {Settings.SettingsArchive.MaxEntries.ToString("N0", CultureInfo.InvariantCulture)} limit; nothing was imported.";

    public static string TooLarge => $"The zip unpacks past the {FileText.Size(Settings.SettingsArchive.MaxBytes)} limit; nothing was imported.";

    public const string Encrypted = "The zip holds an encrypted entry, which the app can't read; nothing was imported.";

    public static string ImportFailed(string reason) => $"Nothing was imported: {reason}.";

    public const string Cancelled = "Import canceled; nothing was written.";

    // ─── import: the list a bare /settings import shows ──────────────────────────

    public static string NoZips(string folder) =>
        $"No settings zips ({FilePrefix}*.zip) in {folder}. Type /settings import <zip> with the zip's path.";

    public static string ZipsHeader(string folder) => $"Settings zips in {folder}, newest first; type one of these to import it:";

    public static string ZipLine(string name) => $"/settings import {name}";

    // ─── import: the report ──────────────────────────────────────────────────────

    public static string Added(IReadOnlyList<string> profiles) =>
        $"Added {(profiles.Count == 1 ? "profile" : "profiles")}: {Listed(profiles)}. Switch with /profile <name>.";

    public static string Renamed(string from, string to) => $"{from} as {to}";

    public static string AddedHome(IReadOnlyList<string> items) => $"Added to the home: {Listed(items)}.";

    public const string NothingAdded = "Nothing imported: everything in the zip is already here.";

    /// <summary>What couldn't be moved or written in the second phase, each with its reason (the review's #8).</summary>
    public static string Failed(IReadOnlyList<string> items) => $"Couldn't be added: {Listed(items)}.";

    public static string FailedOne(string item, string reason) => $"{item} ({reason})";

    /// <summary>Connection files whose passwords sit in the other computer's store (the review's #34).</summary>
    public static string ConnectionPasswords(IReadOnlyList<string> files) =>
        $"Connection files whose passwords may need setting again (kept in the other computer's store): {Listed(files)}.";

    public static string Skipped(IReadOnlyList<string> items) => $"Skipped (already here, or not a settings file): {Listed(items)}.";

    /// <summary>The reset settings per profile; the advice to retype only when a key the person types was reset (the review's #38).</summary>
    public static string Reset(IReadOnlyList<string> items, bool keys) =>
        $"Reset for this computer: {Listed(items)}." + (keys ? " Type the keys again in /settings." : "");

    public static string ResetOne(string profile, IReadOnlyList<string> settings) => $"{profile} ({string.Join(", ", settings)})";

    public static string Unparsable(IReadOnlyList<string> profiles) =>
        $"Imported as is, since its profile.json doesn't read (the app uses the defaults): {string.Join(", ", profiles)}.";

    public static string Newer(string app) => $"Made by version {app}; settings this version doesn't know are kept but unused.";

    public const string McpLater = "The imported mcp.json's servers connect at the next profile switch or /mcp reload.";

    public const string Trust =
        "Import only your own zips: an imported profile's settings and MCP servers run as you once you switch to it, and a home configs file applies to every profile.";

    public static string DeleteZip(string path) => $"Delete {path} once you're done: it holds your persona, memory and MCP env and headers as written.";

    // ─── the start flag ──────────────────────────────────────────────────────────

    public static string StartRefused(string error) => $"--import-settings: {error}";

    public static string MissingFlagValue => "--import-settings needs a zip's path.";

    /// <summary>A zip the start has already imported (its SHA-256 in the home's marker), skipped so a shortcut doesn't add it again (the review's #3).</summary>
    public static string AlreadyImported(string path) =>
        $"--import-settings: {path} was imported at an earlier start, so it was skipped. Type /settings import {path} to add it again.";

    // ─── the reset settings' names (as the settings rows call them) ─────────────

    /// <summary>
    /// The name a reset setting is reported by: its row's label (<see cref="SettingsMenu.FieldName"/>, so the two never drift, the review's #14),
    /// else the Web UI token's own words, else its key.
    /// </summary>
    public static string SettingLabel(string key) =>
        key == nameof(Settings.AppSettingsData.WebUiToken) ? "Web UI token"
        : Enum.TryParse(key, ignoreCase: false, out SettingsField field) && !int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ? SettingsMenu.FieldName(field)
        : key;

    // ─── helpers ─────────────────────────────────────────────────────────────────

    /// <summary>The first <see cref="ListedNames"/> of <paramref name="names"/>, then "+N more".</summary>
    public static string Listed(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        string shown = string.Join(", ", names.Take(ListedNames));
        return names.Count > ListedNames ? $"{shown} +{(names.Count - ListedNames).ToString(CultureInfo.InvariantCulture)} more" : shown;
    }

    private static string Count(int n, string one, string many) => $"{n.ToString(CultureInfo.InvariantCulture)} {(n == 1 ? one : many)}";
}
