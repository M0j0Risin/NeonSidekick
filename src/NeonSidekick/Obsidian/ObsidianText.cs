using System.Globalization;
using NeonSidekick.Files;

namespace NeonSidekick.Obsidian;

/// <summary>Why a vault call was refused (2026-09-22); <see cref="ObsidianText.Error"/> turns each into its sentence.</summary>
public enum VaultOutcome
{
    Ok,
    NoVault,
    NotAVault,
    OutsideVault,
    HiddenPath,
    NotFound,
    NotANote,
    Exists,
    HeadingNotFound,
    TooLarge,
    Failed,
}

/// <summary>
/// The Obsidian tools' words (2026-09-22), the <see cref="FileText"/> shape: pure statics, every string
/// pinned by the tests; every refusal starts <c>Error:</c>; the first line of every result stands alone,
/// since it is the transcript's one-line note (<see cref="Note"/>). Paths are the vault's spelling:
/// relative, <c>/</c>-separated.
/// </summary>
public static class ObsidianText
{
    /// <summary>What every tool description says the vault is, so any of the words finds the tools.</summary>
    public const string VaultWords = "the user's Obsidian vault (their notes)";

    /// <summary>The note argument's schema description, shared by every tool that takes one; spliced into a JSON literal, so no double quote or backslash.</summary>
    public const string NoteArgument = "The note: its name (Plan), a [[wikilink]], or its path in the vault (Projects/Plan.md); resolved the way Obsidian resolves a link.";

    public const string NoVault = "Error: no Obsidian vault is set; the user sets one in Obsidian vault on the Obsidian tab of /tools.";

    public static string NotAVault(string root) => $"Error: {root} is not an Obsidian vault (it has no .obsidian folder).";

    public static string OutsideVault(string path) => $"Error: {path} is outside the vault.";

    public static string HiddenPath(string path) => $"Error: {path} is inside a dot-folder (.obsidian, .trash, …), which the vault tools leave to Obsidian.";

    public static string BadName(string name) => $"Error: \"{name}\" is not a name Obsidian allows (none of * \" \\ / < > : | ?, and no dot or space at the end).";

    public static string NotANote(string path) => $"Error: {path} is not a Markdown note (.md).";

    public static string Exists(string path) => $"Error: {path} already exists; use mode overwrite or append, or pick another name.";

    public static string TooLarge(string path) => $"Error: {path} is too large to edit (over {FileText.Size(WorkingDirectory.MaxTextFileBytes)}).";

    public static string Failed(string verb, string path, string detail) => $"Error: could not {verb} {path}: {detail}";

    /// <summary><c>Error: no note matches "Plan"</c>, with the names that contain it when there are any.</summary>
    public static string NotFound(string reference, IReadOnlyList<string> near)
    {
        ArgumentNullException.ThrowIfNull(near);
        string head = $"Error: no note matches \"{reference}\".";
        return near.Count == 0 ? head : head + " Similar: " + string.Join(", ", near) + ".";
    }

    /// <summary><c>Error: Plan.md has no heading "Budget"; its headings: Goals, Steps.</c></summary>
    public static string HeadingNotFound(string relative, string heading, IReadOnlyList<string> headings)
    {
        ArgumentNullException.ThrowIfNull(headings);
        string head = $"Error: {relative} has no heading \"{heading}\"";
        return headings.Count == 0 ? head + "; it has no headings." : head + "; its headings: " + string.Join(", ", headings) + ".";
    }

    public static string Error(VaultOutcome outcome, string detail) => outcome switch
    {
        VaultOutcome.NoVault => NoVault,
        VaultOutcome.NotAVault => NotAVault(detail),
        VaultOutcome.OutsideVault => OutsideVault(detail),
        VaultOutcome.HiddenPath => HiddenPath(detail),
        VaultOutcome.NotANote => NotANote(detail),
        VaultOutcome.Exists => Exists(detail),
        VaultOutcome.TooLarge => TooLarge(detail),
        _ => "Error: " + detail,
    };

    public static string Required(string argument) => $"Error: {argument} is required.";

    public static string BadChoice(string argument, string sent, IReadOnlyList<string> choices) =>
        $"Error: {argument} must be one of {string.Join(", ", choices)}; got \"{sent}\".";

    public const string SearchNeedsQuery = "Error: query is required; vault_list lists notes by folder, tag or property without one.";

    public static string BadDate(string sent) => $"Error: date must be YYYY-MM-DD, today, yesterday, tomorrow or +N / -N days; got \"{sent}\".";

    public static string BadProperty(string key) =>
        $"Error: property {key} must be a string, a number, true/false, null or a list of those; a nested object is not a property Obsidian shows.";

    public const string BadSet = "Error: set must be one object of property names and values, e.g. {\"status\": \"done\", \"tags\": [\"a\", \"b\"]}.";

    public const string BadRemove = "Error: remove must be a list of property names, e.g. [\"status\", \"due\"].";

    public const string NothingToMove ="Error: to names the note's own path; nothing to move.";

    /// <summary>The transcript's one line: the result's first.</summary>
    public static string Note(string result)
    {
        ArgumentNullException.ThrowIfNull(result);
        int newline = result.IndexOf('\n');
        return newline < 0 ? result : result[..newline];
    }

    public static string Count(int n, string singular, string? plural = null) =>
        n.ToString("N0", CultureInfo.InvariantCulture) + " " + (n == 1 ? singular : plural ?? singular + "s");

    /// <summary>The line an ambiguous name adds: <c>(also named Plan: Work/Plan.md, Old/Plan.md — pass the path for another)</c>.</summary>
    public static string AlsoNamed(string name, IReadOnlyList<string> others)
    {
        ArgumentNullException.ThrowIfNull(others);
        return others.Count == 0 ? "" : $"(also named {name}: {string.Join(", ", others)} — pass the path for another)";
    }

    /// <summary>A result's header plus the ambiguity line when there is one.</summary>
    public static string WithAlso(string text, string also) => also.Length == 0 ? text : text + "\n" + also;

    /// <summary><c>, 9 lines, 182 words</c> for a note's new text.</summary>
    public static string Counts(string text)
    {
        WorkingDirectory.Count(text, out int lines, out int words);
        return FileText.Counts(lines, words);
    }

    public static string Wrote(string relative, bool created, string text) =>
        (created ? "created " : "replaced ") + relative + " (" + Counts(text).TrimStart(',', ' ') + ")";

    public static string Inserted(string relative, bool created, bool append, string heading, string text) =>
        (created ? "created " : (append ? "appended to " : "prepended to ")) + relative
        + (heading.Length > 0 ? " under \"" + heading + "\"" : "") + " (" + Counts(text).TrimStart(',', ' ') + ")";

    /// <summary><c>vault_delete</c> named a folder (2026-09-22): only a note or an attachment goes. Pinned.</summary>
    public static string IsAFolder(string path) => $"Error: {path} is a folder; vault_delete takes one note or attachment at a time.";

    /// <summary><c>vault_delete</c> called while the setting <c>Obsidian allow delete (.trash)</c> is off (2026-09-22; the row's name since 2026-09-23): the guard behind the offer. Pinned.</summary>
    public const string DeleteOff = "Error: deleting is off; the user can turn on Obsidian allow delete (.trash) on the Obsidian tab of /tools.";

    /// <summary>
    /// <c>vault_delete</c>'s result (2026-09-22): where the file went, then the notes whose links still point at it —
    /// left as they are, so Obsidian now shows them unresolved. <paramref name="linkedFrom"/> is the whole list;
    /// past <paramref name="max"/> the rest is counted. Pinned.
    /// </summary>
    public static string Deleted(string relative, string trashPath, IReadOnlyList<string> linkedFrom, int max = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(linkedFrom);
        string head = $"deleted {relative} (moved to {trashPath})";
        if (linkedFrom.Count == 0)
        {
            return head + "; nothing links to it";
        }

        string list = string.Join("\n", linkedFrom.Take(max));
        if (linkedFrom.Count > max)
        {
            list += "\n… " + Count(linkedFrom.Count - max, "more");
        }

        return head + "; " + Count(linkedFrom.Count, "note") + " still " + (linkedFrom.Count == 1 ? "links" : "link") + " to it:\n" + list;
    }

    public static string Moved(string from, string to, int links, int notes) =>
        links == 0
            ? $"moved {from} to {to}; no links pointed at it"
            : $"moved {from} to {to}; updated {Count(links, "link")} in {Count(notes, "note")}";

    public static string MoveFailedPartway(string detail, IReadOnlyList<string> done) =>
        $"Error: the move stopped partway ({detail}); already rewritten: " + (done.Count == 0 ? "none" : string.Join(", ", done)) + ".";

    public static string PropertiesHeader(string relative, int count) =>
        count == 0 ? relative + " has no properties" : relative + ": " + Count(count, "property", "properties");

    public static string PropertiesChanged(string relative, IReadOnlyList<string> set, IReadOnlyList<string> removed, IReadOnlyList<string> missing)
    {
        var parts = new List<string>(3);
        if (set.Count > 0)
        {
            parts.Add("set " + string.Join(", ", set));
        }

        if (removed.Count > 0)
        {
            parts.Add("removed " + string.Join(", ", removed));
        }

        if (missing.Count > 0)
        {
            parts.Add("no " + string.Join(", ", missing) + " to remove");
        }

        return string.Join("; ", parts) + " on " + relative;
    }

    public static string DailyHeader(string relative, string status, int lines) =>
        relative + " (" + (status.Length > 0 ? status + ", " : "") + FileText.Count(lines, "line", "lines") + "):";

    public static string CreatedFrom(string template) => template.Length == 0 ? "created" : "created from " + template;
}
