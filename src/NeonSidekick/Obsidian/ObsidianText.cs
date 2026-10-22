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

    /// <summary>A heading with mode create or overwrite (2026-10-22, review X1). Prompt text.</summary>
    public const string HeadingNeedsInsert =
        "Error: heading names where append or prepend adds text; create and overwrite write the whole note. To change one section, read the note with vault_read and overwrite it whole with the section changed";

    /// <summary>A note whose text no longer matches the links a move read in it (2026-10-22, review H1): the move stops, nothing written there.</summary>
    public const string NoteChangedUnderMove = "a note changed while its links were being rewritten";

    /// <summary>A <c>start_line</c> past a heading's section (2026-10-22, review M10). Prompt text.</summary>
    public static string PastSection(string path, string heading, int startLine, int from, int to) =>
        $"Error: start_line {startLine.ToString(System.Globalization.CultureInfo.InvariantCulture)} is past the section '{heading}' of {path}, which is lines {from.ToString(System.Globalization.CultureInfo.InvariantCulture)}–{to.ToString(System.Globalization.CultureInfo.InvariantCulture)}; leave heading out to read past it";

    public static string OutsideVault(string path) => $"Error: {path} is outside the vault.";

    public static string HiddenPath(string path) => $"Error: {path} is inside a dot-folder (.obsidian, .trash, …), which the vault tools leave to Obsidian.";

    public static string BadName(string name) => $"Error: \"{name}\" is not a name Obsidian allows (none of * \" \\ / < > : | ?, and no dot or space at the end).";

    public static string NotANote(string path) => $"Error: {path} is not a Markdown note (.md).";

    public static string Exists(string path) => $"Error: {path} already exists; use mode overwrite or append, or pick another name.";

    /// <summary><c>vault_move</c> onto a note that is there (2026-10-22, review R3: it said "use mode overwrite", which vault_move has none of). Prompt text.</summary>
    public static string MoveTargetExists(string path) => $"Error: {path} already exists; pick another name or folder for to.";

    /// <summary>
    /// A change named a note several notes are called (2026-10-22, review J5): never one picked for it. At most <see cref="MaxNamedShown"/>
    /// paths. Prompt text.
    /// </summary>
    public static string AmbiguousChange(string reference, IReadOnlyList<string> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return $"Error: \"{reference}\" names {Count(candidates.Count, "note")} ({Listed(candidates, MaxNamedShown)}); pass the path of the one to change.";
    }

    /// <summary>The most headings a refusal lists (2026-10-22, review X2: every heading went in, about a megabyte from 100,000).</summary>
    public const int MaxHeadingsShown = 50;

    /// <summary>The most same-named notes a line lists (review X2).</summary>
    public const int MaxNamedShown = 5;

    /// <summary><paramref name="items"/> joined, at most <paramref name="max"/>, the rest counted.</summary>
    private static string Listed(IReadOnlyList<string> items, int max) =>
        string.Join(", ", items.Take(max)) + (items.Count > max ? ", … " + Count(items.Count - max, "more") : "");

    /// <summary>A note past what vault_read reads (2026-10-22, review H7). Prompt text.</summary>
    public static string TooLargeToRead(string path) => $"Error: {path} is over {FileText.Size(WorkingDirectory.MaxTextFileBytes)}, more than the vault tools read.";

    /// <summary>A write's content past write_file's cap (2026-10-22, review K6). Prompt text.</summary>
    public static readonly string ContentTooLong =
        "Error: the content is over " + WorkingDirectory.MaxWriteChars.ToString("N0", CultureInfo.InvariantCulture) + " characters; write it in parts";

    /// <summary>An append or prepend that would make the note too big for every later edit (review K6). Prompt text.</summary>
    public static string WouldBeTooLarge(string path) =>
        $"Error: that would make {path} over {FileText.Size(WorkingDirectory.MaxTextFileBytes)}, past what the vault tools read; nothing was written.";

    /// <summary>The notes a search left unread, too big or unreadable (review H7). Prompt text.</summary>
    public static string NotSearched(int notes) => $" ({Count(notes, "note")} too big or unreadable not searched)";

    /// <summary>A capped search's note (2026-10-22, review Q14: it offered max_results even at its top).</summary>
    public static string FirstShown(int shown, int cap, int max) =>
        $" (first {shown.ToString(CultureInfo.InvariantCulture)} shown; " + (cap < max ? $"max_results raises it to {max.ToString(CultureInfo.InvariantCulture)})" : "narrow it with folder or tag)");

    /// <summary>A capped list's tail note (review Q14).</summary>
    public static string MoreHint(int cap, int max) =>
        cap < max ? $" (max_results raises the cap to {max.ToString(CultureInfo.InvariantCulture)})" : " (narrow it with folder, tag or property)";

    /// <summary><c>vault_list</c>'s value with no property (2026-10-22, review V9). Prompt text.</summary>
    public const string ValueNeedsProperty = "Error: value filters by a property's value; give property too (vault_list with what properties lists them).";

    /// <summary>A result cut at read_file's cap (review M4).</summary>
    public const string ResultCut = "(cut at 32,000 characters: narrow it)";

    /// <summary>The daily note's header when Periodic Notes made the settings (2026-10-22, review U10). Pinned.</summary>
    public const string DailyFromPeriodic = "Periodic Notes' settings";

    /// <summary>The daily note's header when the core Daily notes plugin is off (review U10). Pinned.</summary>
    public const string DailyNotesOff = "the Daily notes plugin is off in this vault";

    /// <summary>A move whose own link rewrite failed after the note moved (2026-10-22, review N8). Prompt text.</summary>
    public static string MovedOwnLinksFailed(string from, string to, string detail) =>
        $"Error: moved {from} to {to} and updated the links to it, but its own relative links could not be re-aimed ({detail}).";

    /// <summary>What a move could not look into (2026-10-22, review H10): unread notes and a vault past the index. Empty for neither.</summary>
    public static string MoveUnchecked(int unread, bool truncated)
    {
        var parts = new List<string>(2);
        if (unread > 0)
        {
            parts.Add(Count(unread, "note") + " too big or unreadable");
        }

        if (truncated)
        {
            parts.Add("the notes past the first " + NoteIndex.MaxNotes.ToString("N0", CultureInfo.InvariantCulture));
        }

        return parts.Count == 0 ? "" : "\n(links in " + string.Join(" and ", parts) + " were not checked)";
    }

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
        return headings.Count == 0 ? head + "; it has no headings." : head + "; its headings: " + Listed(headings, MaxHeadingsShown) + ".";
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
        return others.Count == 0 ? "" : $"(also named {name}: {Listed(others, MaxNamedShown)} — pass the path for another)";
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

    /// <summary>
    /// A move that failed before the note moved (2026-10-22, review N8): the note is where it was, and every note rewritten was put back but
    /// <paramref name="notPutBack"/>. Prompt text.
    /// </summary>
    public static string MoveFailedPartway(string detail, IReadOnlyList<string> notPutBack, bool movedNote) =>
        $"Error: the move stopped ({detail}); the note {(movedNote ? "moved" : "did not move")}, and "
        + (notPutBack.Count == 0 ? "no link was left changed." : "these notes keep links to the new path: " + string.Join(", ", notPutBack) + ".");

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
