using System.Text;

namespace NeonSidekick.Shell;

/// <summary>
/// The shell police's second rule (2026-10-03, the user's idea): <c>Shell police forbidden strings</c>, a list the user keeps, and
/// a <c>run_command</c> line, <c>execute_code</c> script or <c>process</c> write that contains one of them is refused before the gate
/// is asked. Loose on purpose (the user's call): case ignored and every run of whitespace — spaces, tabs, line breaks — read as
/// one space on both sides, so <c>rm -rf</c> catches <c>RM   -RF</c> and a script's <c>rm</c> and <c>-rf</c> on two lines. A
/// tripwire, not a sandbox: text built in pieces (<c>"r" + "m"</c>, cmd's <c>r^m</c>) gets past it. The list keeps the user's case
/// (unlike <see cref="CommandAllowList.Merge"/>, which lower-cases), sorted A to Z ignoring case, no duplicates ignoring case.
/// </summary>
public static class ForbiddenStrings
{
    /// <summary><paramref name="text"/> with every run of whitespace one space, trimmed: the form both sides are compared in.</summary>
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length);
        bool space = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                space = builder.Length > 0;
                continue;
            }

            if (space)
            {
                builder.Append(' ');
                space = false;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>
    /// The first entry of <paramref name="forbidden"/>, in list order and as the user typed it, that <paramref name="text"/>
    /// contains once both are <see cref="Normalize"/>d, case ignored; null when none does. A blank entry never matches.
    /// </summary>
    public static string? Find(string text, IReadOnlyList<string> forbidden)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(forbidden);
        if (forbidden.Count == 0)
        {
            return null;
        }

        string normalized = Normalize(text);
        foreach (string entry in forbidden)
        {
            string needle = Normalize(entry ?? "");
            if (needle.Length > 0 && normalized.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>The saved list as the editor shows it and saves it: each entry <see cref="Normalize"/>d, blanks gone, no duplicates ignoring case (the first kept), sorted A to Z ignoring case.</summary>
    public static List<string> Sorted(IReadOnlyList<string> saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (string entry in saved)
        {
            string normalized = Normalize(entry ?? "");
            if (normalized.Length > 0 && seen.Add(normalized))
            {
                list.Add(normalized);
            }
        }

        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    /// <summary>Whether <paramref name="entry"/> is already in <paramref name="saved"/>, both <see cref="Normalize"/>d, case ignored.</summary>
    public static bool Contains(IReadOnlyList<string> saved, string entry)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(entry);
        string normalized = Normalize(entry);
        return saved.Any(existing => string.Equals(Normalize(existing ?? ""), normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The saved list with <paramref name="entry"/> added (<see cref="Sorted"/>); a blank entry or one already there leaves it as it was.</summary>
    public static List<string> Add(IReadOnlyList<string> saved, string entry)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(entry);
        return Sorted([.. saved, entry]);
    }

    /// <summary>The saved list without <paramref name="entry"/> (both <see cref="Normalize"/>d, case ignored), <see cref="Sorted"/>: the editor's remove.</summary>
    public static List<string> Without(IReadOnlyList<string> saved, string entry)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(entry);
        string normalized = Normalize(entry);
        var list = Sorted(saved);
        list.RemoveAll(existing => string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase));
        return list;
    }
}
