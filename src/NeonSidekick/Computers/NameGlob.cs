namespace NeonSidekick.Computers;

/// <summary>
/// The allow-lists' one pattern rule (2026-10-15, the admin-tools plan: lifted from <c>EventLogCatalog</c>'s, which forwards here): case
/// ignored, <c>*</c> any run of characters that does not cross <paramref name="separator"/> — <c>/</c> for an event log's name
/// (<c>Microsoft-Windows-*/Operational</c>), <c>\</c> for a task's path (<c>\Backup\*</c>), none (<c>'\0'</c>) for a service's name
/// (<c>MSSQL*</c>). Iterative with one backtrack point per star, so a hostile pattern cannot blow up. Pure.
/// </summary>
public static class NameGlob
{
    /// <summary>The separator that means none: a star then runs over anything.</summary>
    public const char NoSeparator = '\0';

    /// <summary>Whether <paramref name="pattern"/> matches the whole of <paramref name="name"/>.</summary>
    public static bool Matches(string pattern, string name, char separator = NoSeparator)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(name);

        // A lone star is everything (2026-10-23, the admin review's J12): it never crossed the separator, so control: ["*"] matched no task
        // path (each starts with \) and logs: ["*"] no channel with a /, granting far less than it says, silently.
        if (pattern == "*")
        {
            return true;
        }

        int p = 0, n = 0, star = -1, mark = 0;
        while (n < name.Length)
        {
            if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = n;
            }
            else if (p < pattern.Length && char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(name[n]))
            {
                p++;
                n++;
            }
            else if (star >= 0 && (separator == NoSeparator || name[mark] != separator))
            {
                p = star + 1;
                n = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }

    /// <summary>Whether any of <paramref name="patterns"/> matches <paramref name="name"/>, each trimmed; false for none.</summary>
    public static bool Any(IEnumerable<string> patterns, string name, char separator = NoSeparator)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        ArgumentNullException.ThrowIfNull(name);
        return patterns.Any(p => Matches(p.Trim(), name.Trim(), separator));
    }
}
