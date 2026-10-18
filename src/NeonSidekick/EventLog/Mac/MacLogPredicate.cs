namespace NeonSidekick.EventLog;

/// <summary>What one <c>%@</c> of a <see cref="MacLogPredicate"/> format stands for: a string, a number, a date or a list of strings.</summary>
public enum MacPredicateKind
{
    String,
    Number,
    Date,
    StringList,
}

/// <summary>One argument of a predicate, in the order of its <c>%@</c>s (2026-10-18).</summary>
public readonly record struct MacPredicateArgument(MacPredicateKind Kind, string? Text = null, long Number = 0, DateTimeOffset Date = default, IReadOnlyList<string>? List = null)
{
    public static MacPredicateArgument Of(string text) => new(MacPredicateKind.String, Text: text);

    public static MacPredicateArgument Of(long number) => new(MacPredicateKind.Number, Number: number);

    public static MacPredicateArgument Of(DateTimeOffset date) => new(MacPredicateKind.Date, Date: date);

    public static MacPredicateArgument Of(IReadOnlyList<string> list) => new(MacPredicateKind.StringList, List: list);
}

/// <summary>
/// The unified log's <c>NSPredicate</c> for a <see cref="MacLogFilter"/> (2026-10-18, the Mac event-log plan). Pure. The format is
/// joined only from the constant fragments below; every value the model or the user typed travels in the argument array
/// (<c>predicateWithFormat:argumentArray:</c>), its NULs stripped, so nothing typed can change the predicate's shape — and a malformed
/// format, an Objective-C exception, would end a NativeAOT process. The time limit is always the last clause, <c>OR date &lt; %@</c>:
/// the first entry older than <see cref="MacLogFilter.Since"/> matches, and the reader stops there (Phase 0: without it a predicate
/// with no match ran the full 300 s). Levels go by OSLog's type word (<c>logType == %@</c>; <c>level</c> is no field there).
/// </summary>
public static class MacLogPredicate
{
    /// <summary>OSLog's type words, by level number (1 debug … 5 fault); "default" is OSLog's "notice".</summary>
    public static readonly IReadOnlyList<string> LevelWords = ["debug", "info", "default", "error", "fault"];

    public const string ProcessTerm = "process == %@";
    public const string PidTerm = "processIdentifier == %@";
    public const string SubsystemTerm = "subsystem BEGINSWITH %@";
    public const string CategoryTerm = "category == %@";
    public const string TextTerm = "composedMessage CONTAINS[c] %@";
    public const string ExcludeTerm = "NOT (process IN %@)";
    public const string UntilTerm = "date <= %@";
    public const string LevelTerm = "logType == %@";
    public const string Everything = "TRUEPREDICATE";
    public const string TimeLimit = " OR date < %@";

    /// <summary>The format and its arguments, in order. Throws <see cref="ArgumentException"/> for a level word OSLog does not have.</summary>
    public static (string Format, IReadOnlyList<MacPredicateArgument> Arguments) Build(MacLogFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var terms = new List<string>();
        var arguments = new List<MacPredicateArgument>();
        var levels = filter.Levels.Select(l => l.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList();
        foreach (string level in levels)
        {
            if (!LevelWords.Contains(level, StringComparer.Ordinal))
            {
                throw new ArgumentException("not a level: " + level, nameof(filter));
            }
        }

        if (levels.Count is > 0 and < 5)
        {
            terms.Add("(" + string.Join(" OR ", levels.Select(_ => LevelTerm)) + ")");
            arguments.AddRange(levels.Select(MacPredicateArgument.Of));
        }

        AddText(terms, arguments, ProcessTerm, filter.Process);
        if (filter.Pid is { } pid)
        {
            terms.Add(PidTerm);
            arguments.Add(MacPredicateArgument.Of(pid));
        }

        AddText(terms, arguments, SubsystemTerm, filter.Subsystem);
        AddText(terms, arguments, CategoryTerm, filter.Category);
        AddText(terms, arguments, TextTerm, filter.Text);
        var excluded = (filter.ExcludeProcesses ?? []).Select(Clean).Where(p => p.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (excluded.Count > 0)
        {
            terms.Add(ExcludeTerm);
            arguments.Add(MacPredicateArgument.Of(excluded));
        }

        if (filter.Until is { } until)
        {
            terms.Add(UntilTerm);
            arguments.Add(MacPredicateArgument.Of(until));
        }

        string format = "(" + (terms.Count == 0 ? Everything : string.Join(" AND ", terms)) + ")" + TimeLimit;
        arguments.Add(MacPredicateArgument.Of(filter.Since));
        return (format, arguments);
    }

    /// <summary>How many <c>%@</c> a format holds: always the argument count (pinned by a test).</summary>
    public static int Placeholders(string format)
    {
        ArgumentNullException.ThrowIfNull(format);
        int count = 0;
        for (int at = format.IndexOf("%@", StringComparison.Ordinal); at >= 0; at = format.IndexOf("%@", at + 2, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>A level word's number (1 debug … 5 fault), or 0 for none.</summary>
    public static int LevelOf(string word) => LevelWords.ToList().IndexOf(word.Trim().ToLowerInvariant()) + 1;

    /// <summary>A value as it may travel: NULs stripped (an NSString built from UTF-8 ends at one) and trimmed.</summary>
    public static string Clean(string? value) => (value ?? "").Replace("\0", "", StringComparison.Ordinal).Trim();

    private static void AddText(List<string> terms, List<MacPredicateArgument> arguments, string term, string? value)
    {
        string clean = Clean(value);
        if (clean.Length > 0)
        {
            terms.Add(term);
            arguments.Add(MacPredicateArgument.Of(clean));
        }
    }
}

/// <summary>
/// The client-side check of every term (2026-10-18): what <see cref="MacLogPredicate"/> asked of OSLog, asked again of each entry the
/// reader copied out, so a predicate that matched too much (a store's quirk, a fake) still gives the right rows. Pure. The entry that
/// ends the read (older than <see cref="MacLogFilter.Since"/>) is never a match.
/// </summary>
public static class MacLogMatch
{
    public static bool Matches(MacLogEntry entry, MacLogFilter filter)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(filter);
        if (entry.Time < filter.Since || (filter.Until is { } until && entry.Time > until))
        {
            return false;
        }

        if (filter.Levels.Count > 0 && !filter.Levels.Any(l => MacLogPredicate.LevelOf(l) == entry.Level))
        {
            return false;
        }

        string process = MacLogPredicate.Clean(filter.Process);
        if (process.Length > 0 && !string.Equals(entry.Process, process, StringComparison.Ordinal))
        {
            return false;
        }

        if (filter.Pid is { } pid && entry.Pid != pid)
        {
            return false;
        }

        string subsystem = MacLogPredicate.Clean(filter.Subsystem);
        if (subsystem.Length > 0 && !entry.Subsystem.StartsWith(subsystem, StringComparison.Ordinal))
        {
            return false;
        }

        string category = MacLogPredicate.Clean(filter.Category);
        if (category.Length > 0 && !string.Equals(entry.Category, category, StringComparison.Ordinal))
        {
            return false;
        }

        string text = MacLogPredicate.Clean(filter.Text);
        if (text.Length > 0 && !entry.Message.Contains(text, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !(filter.ExcludeProcesses ?? []).Any(p => string.Equals(MacLogPredicate.Clean(p), entry.Process, StringComparison.Ordinal));
    }
}
