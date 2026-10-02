using System.Globalization;
using NeonSidekick.Sessions;

namespace NeonSidekick.Skills;

/// <summary>
/// The words of the skill records and <c>/skills purge</c> (2026-09-30, the user's ask). <c>/skills purge list &lt;age&gt;</c> only
/// lists the skills unused for that long, and <c>/skills purge commit &lt;age&gt;</c> deletes them after a yes/no (the user's
/// grammar, revised twice the same day). The usage error, the notices, the yes/no and the log lines live here, pinned.
/// </summary>
public static class SkillRecordText
{
    public const string PurgeWord = "purge";
    public const string ListWord = "list";
    public const string CommitWord = "commit";

    /// <summary>The screen's <c>/skills</c> usage error: the installer's, and the purge's two forms. Pinned.</summary>
    public const string SkillsUsageError = SkillInstallText.UsageError + ", /skills purge list|commit <age>, or /skills revert <name>";

    /// <summary>The completion notes on <c>purge list</c> and <c>purge commit</c>. Pinned.</summary>
    public const string ListNote = "show the skills not used for that long: an age, 30 (days), 12h, 90m";

    public const string CommitNote = "delete the skills not used for that long, after a yes/no";

    /// <summary>The completion note on <c>purge</c>. Pinned.</summary>
    public const string PurgeNote = "purge list <age> shows the skills unused for that long; purge commit <age> deletes them";

    /// <summary>The broom the purge's notices wear.</summary>
    public const string Glyph = "🧹 ";

    /// <summary>What <c>/skills purge</c> was given, read by <see cref="ParsePurge"/>.</summary>
    public enum PurgeKind
    {
        /// <summary>Not a purge at all: the caller's other words, or its usage error.</summary>
        None,

        /// <summary><c>purge</c> with words that do not parse: the usage error.</summary>
        Invalid,

        /// <summary><c>purge list &lt;age&gt;</c>: the dry run.</summary>
        List,

        /// <summary><c>purge commit &lt;age&gt;</c>: the delete, after a yes/no.</summary>
        Commit,
    }

    /// <summary>
    /// <c>/skills</c>' argument as a purge: <c>purge list &lt;age&gt;</c> or <c>purge commit &lt;age&gt;</c>, words without case, the age
    /// <see cref="SessionText.TryParseAge"/>'s (<c>30</c> days, <c>12h</c>, <c>90m</c>, <c>1d 6h</c>). <see cref="PurgeKind.None"/> when the
    /// first word is not <c>purge</c>. Pure; pinned.
    /// </summary>
    public static (PurgeKind Kind, TimeSpan Age) ParsePurge(string args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string[] words = args.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || !words[0].Equals(PurgeWord, StringComparison.OrdinalIgnoreCase))
        {
            return (PurgeKind.None, TimeSpan.Zero);
        }

        if (words.Length < 3)
        {
            return (PurgeKind.Invalid, TimeSpan.Zero);
        }

        var kind = words[1].Equals(ListWord, StringComparison.OrdinalIgnoreCase) ? PurgeKind.List
            : words[1].Equals(CommitWord, StringComparison.OrdinalIgnoreCase) ? PurgeKind.Commit
            : PurgeKind.Invalid;
        return kind != PurgeKind.Invalid && SessionText.TryParseAge(string.Join(' ', words.Skip(2)), out var age) ? (kind, age) : (PurgeKind.Invalid, TimeSpan.Zero);
    }

    /// <summary><c>1 skill</c> / <c>3 skills</c>.</summary>
    public static string Skills(int count) => count.ToString(CultureInfo.InvariantCulture) + (count == 1 ? " skill" : " skills");

    /// <summary>With nothing that old: <c>(🧹 no skills unused for 30 days)</c>. Pinned.</summary>
    public static string NoneNotice(TimeSpan age) => "(" + Glyph + "no skills unused for " + SessionText.Age(age) + ")";

    /// <summary>The dry run's head line: <c>(🧹 2 skills unused for 30 days; /skills purge commit 30 days deletes them)</c>. Pinned.</summary>
    public static string ListNotice(int count, TimeSpan age, string ageWords) =>
        "(" + Glyph + Skills(count) + " unused for " + SessionText.Age(age) + "; /skills purge commit " + ageWords + " deletes " + (count == 1 ? "it" : "them") + ")";

    /// <summary>
    /// One skill under the dry run's head line, and in the yes/no's caption:
    /// <c>pdf · global · last used 2026-08-01 14:05</c>, or <c>· never used, modified 2026-07-30 09:12</c>. Pinned.
    /// </summary>
    public static string Line(SkillRecord record, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(zone);
        string when = record.LastUsed is { } used
            ? "last used " + SessionText.Moment(used, zone)
            : "never used, modified " + SessionText.Moment(record.Reference, zone);
        return record.Folder + " · " + SkillScopes.Name(record.Scope) + " · " + when;
    }

    /// <summary>The table's column titles, in order. Pinned.</summary>
    public static readonly IReadOnlyList<string> TableHeaders = ["Skill", "Scope", "Last used", "Modified"];

    /// <summary>The last-used cell of a skill never loaded. Pinned.</summary>
    public const string NeverUsed = "never";

    /// <summary>The space between two columns of <see cref="Table"/>.</summary>
    public const int ColumnGap = 3;

    /// <summary>
    /// The dry run's list as a table (2026-09-30, the user's ask: straighter columns than one dotted line per skill). The rows
    /// are a header (<see cref="TableHeaders"/>), then one per skill: its folder, <c>global</c> or <c>profile</c>, when it was last
    /// used (<see cref="NeverUsed"/> for never), and when it was last changed. Each column is padded to its widest cell in
    /// terminal cells (<see cref="UI.TextCells.Width"/>), so a wide character in a folder name keeps the columns straight. The
    /// last column carries no padding.
    /// </summary>
    public static IReadOnlyList<string> Table(IReadOnlyList<SkillRecord> records, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(zone);
        var rows = new List<string[]> { TableHeaders.ToArray() };
        foreach (var record in records)
        {
            rows.Add(
            [
                record.Folder,
                SkillScopes.Name(record.Scope),
                record.LastUsed is { } used ? SessionText.Moment(used, zone) : NeverUsed,
                SessionText.Moment(record.Modified > record.Created ? record.Modified : record.Created, zone),
            ]);
        }

        int columns = TableHeaders.Count;
        var widths = new int[columns];
        foreach (var row in rows)
        {
            for (int c = 0; c < columns; c++)
            {
                widths[c] = Math.Max(widths[c], UI.TextCells.Width(row[c]));
            }
        }

        var lines = new List<string>(rows.Count);
        foreach (var row in rows)
        {
            var line = new System.Text.StringBuilder();
            for (int c = 0; c < columns; c++)
            {
                line.Append(row[c]);
                if (c < columns - 1)
                {
                    line.Append(' ', widths[c] - UI.TextCells.Width(row[c]) + ColumnGap);
                }
            }

            lines.Add(line.ToString());
        }

        return lines;
    }

    /// <summary>The yes/no: <c>🧹 Delete 2 skills unused for 30 days?</c> Pinned.</summary>
    public static string CommitPrompt(int count, TimeSpan age) => Glyph + "Delete " + Skills(count) + " unused for " + SessionText.Age(age) + "?";

    /// <summary>After the commit: <c>(🗑️ deleted 2 skills unused for 30 days: pdf, haiku)</c>. Pinned.</summary>
    public static string PurgedNotice(IReadOnlyList<string> folders, TimeSpan age)
    {
        ArgumentNullException.ThrowIfNull(folders);
        return "(🗑️ deleted " + Skills(folders.Count) + " unused for " + SessionText.Age(age) + ": " + string.Join(", ", folders) + ")";
    }

    /// <summary>A skill whose folder would not go: <c>Could not delete the skill pdf (global): …</c>; its record stays. Pinned.</summary>
    public static string PurgeFailedError(SkillRecord record, string detail)
    {
        ArgumentNullException.ThrowIfNull(record);
        return "Could not delete the skill " + record.Folder + " (" + SkillScopes.Name(record.Scope) + ")" + (string.IsNullOrWhiteSpace(detail) ? "." : ": " + detail);
    }

    // ── log lines (Skills) ──────────────────────────────────────────────────

    public static string CreatedLogLine(SkillScope scope, string folder) => "Skill record: " + Key(scope, folder) + " created";

    public static string ModifiedLogLine(SkillScope scope, string folder) => "Skill record: " + Key(scope, folder) + " modified";

    public static string UsedLogLine(SkillScope scope, string folder) => "Skill record: " + Key(scope, folder) + " used";

    public static string MovedLogLine(SkillScope from, SkillScope to, string folder) => "Skill record: " + Key(from, folder) + " moved to " + SkillScopes.Name(to);

    public static string RenamedLogLine(SkillScope scope, string from, string to) => "Skill record: " + Key(scope, from) + " renamed to " + to;

    public static string DeletedLogLine(SkillScope scope, string folder) => "Skill record: " + Key(scope, folder) + " deleted";

    public static string FoundLogLine(SkillScope scope, string folder) => "Skill record: " + Key(scope, folder) + " found on disk, added";

    public static string GoneLogLine(SkillScope scope, string folder) => "Skill record: " + Key(scope, folder) + " gone from disk, removed";

    public static string ProfileRenamedLogLine(string from, string to, int rows) =>
        string.Create(CultureInfo.InvariantCulture, $"Skill records: profile \"{from}\" renamed to \"{to}\" ({rows} rows)");

    public static string ProfileForgottenLogLine(string profile) => "Skill records: profile \"" + profile + "\" no longer exists, its rows removed";

    /// <summary><c>Skill record: global/pdf data/big.json's earlier text is too long to keep as a revision</c>. Pinned.</summary>
    public static string RevisionSkippedLogLine(SkillScope scope, string folder, string path) =>
        "Skill record: " + Key(scope, folder) + " " + path + "'s earlier text is too long to keep as a revision";

    /// <summary><c>Skill reverted: profile/haiku SKILL.md to before a reflection write at 2026-10-02T…</c>. Pinned.</summary>
    public static string RevertedLogLine(SkillScope scope, string folder, SkillRevision revision)
    {
        ArgumentNullException.ThrowIfNull(revision);
        return "Skill reverted: " + Key(scope, folder) + " " + revision.Path + " to before a " + revision.Actor + " write at " + SessionStore.Stamp(revision.At);
    }

    /// <summary><c>Skill records: imported 14 events from profile "neon"'s sessions.db</c>. Pinned.</summary>
    public static string ImportedLogLine(string profile, int events) =>
        string.Create(CultureInfo.InvariantCulture, $"Skill records: imported {events} events from profile \"{profile}\"'s {SessionStore.FileName}");

    // ── /skills revert (2026-10-02) ─────────────────────────────────────────

    public const string RevertWord = "revert";

    /// <summary>The completion note on <c>revert</c>. Pinned.</summary>
    public const string RevertNote = "put a skill back as it was before its last change (a model's, a reflection's, an install's)";

    /// <summary>
    /// The one place a revert's outcome becomes words (the command's and the pane's): the notice for a revert done, else the error. The
    /// bool says which.
    /// </summary>
    public static (bool Ok, string Text) RevertText(string name, SkillRevert revert, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(revert);
        return revert.Outcome switch
        {
            SkillRevertOutcome.Reverted => (true, RevertedNotice(name, revert.Revision!, zone)),
            SkillRevertOutcome.NoRevision => (false, NoRevisionError(name)),
            SkillRevertOutcome.HandEdited => (false, RevertHandEditedError(name)),
            _ => (false, RevertFailedError(name, revert.Edit?.Detail ?? "")),
        };
    }

    /// <summary>The usage error for <c>/skills revert</c> with no name. Pinned.</summary>
    public const string RevertUsageError = "/skills revert <name>: the skill to put back as it was before its last change.";

    /// <summary>The undo glyph the revert's notice wears.</summary>
    public const string RevertGlyph = "↩️ ";

    /// <summary>A name no skill in the catalog has. Pinned.</summary>
    public static string RevertUnknownError(string name) => "No skill named " + name + ".";

    /// <summary>A skill with nothing to put back. Pinned.</summary>
    public static string NoRevisionError(string name) => "Nothing to revert for " + name + ": no earlier version is kept (only the app's own changes keep one).";

    /// <summary>After the revert: <c>(↩️ haiku: SKILL.md is back as it was before a reflection's change at 2026-10-02 14:05)</c>; <c>… removed, as before …</c> for a file the change created. Pinned.</summary>
    public static string RevertedNotice(string name, SkillRevision revision, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(zone);
        string what = revision.Content is null ? revision.Path + " is removed, as" : revision.Path + " is back as it was";
        return "(" + RevertGlyph + name + ": " + what + " before " + ActorPhrase(revision.Actor) + " change at " + SessionText.Moment(revision.At, zone) + ")";
    }

    /// <summary>The revert refused over a hand edit. Pinned.</summary>
    public static string RevertHandEditedError(string name) =>
        "Not reverted: " + name + " was edited by hand since the app last changed it, and a revert would lose that edit; change it by hand instead.";

    /// <summary>The revert that could not write: <c>Could not revert haiku: …</c>. Pinned.</summary>
    public static string RevertFailedError(string name, string detail) => "Could not revert " + name + (string.IsNullOrWhiteSpace(detail) ? "." : ": " + detail);

    /// <summary><c>a reflection's</c>, <c>the model's</c>, <c>an install's</c>, <c>your</c>.</summary>
    public static string ActorPhrase(string actor) => actor switch
    {
        SkillActors.Reflection => "a reflection's",
        SkillActors.Model => "the model's",
        SkillActors.Install => "an install's",
        _ => "your",
    };

    /// <summary>The install update page's warning when reflections changed the installed skill (2026-10-02). Pinned.</summary>
    public static string ChangedSinceInstallWarning(int writes) =>
        string.Create(CultureInfo.InvariantCulture, $"A reflection changed this skill {writes}× since it was installed; updating replaces that (/skills revert brings the SKILL.md back).");

    /// <summary><c>Skills reconciled: 2 added, 1 removed, 1 modified (global + profile neon)</c>. Pinned.</summary>
    public static string ReconciledLogLine(SkillReconcile result, string profile) =>
        string.Create(CultureInfo.InvariantCulture, $"Skills reconciled: {result.Added} added, {result.Removed} removed, {result.Modified} modified{(result.ProfilesForgotten > 0 ? $", {result.ProfilesForgotten} profiles forgotten" : "")} (global + profile {profile})");

    /// <summary><c>Skill purged: global/pdf (last used never, modified 2026-07-30T…)</c>. Pinned.</summary>
    public static string PurgedLogLine(SkillRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return "Skill purged: " + Key(record.Scope, record.Folder) + " (last used " + (record.LastUsed is { } used ? SessionStore.Stamp(used) : "never") + ", modified " + SessionStore.Stamp(record.Modified) + ")";
    }

    /// <summary><c>Skills purge: 2 deleted, 0 failed, unused for 30 days</c>. Pinned.</summary>
    public static string PurgeSummaryLogLine(int deleted, int failed, TimeSpan age) =>
        string.Create(CultureInfo.InvariantCulture, $"Skills purge: {deleted} deleted, {failed} failed, unused for {SessionText.Age(age)}");

    private static string Key(SkillScope scope, string folder) => SkillScopes.Name(scope) + "/" + folder;
}
