namespace NeonSidekick.Office;

/// <summary>
/// Excel's rules for a sheet's name (2026-10-29, the Office writing plan): at most <see cref="MaxLength"/> characters, none of
/// <c>: \ / ? * [ ]</c>, no <c>'</c> first or last, not <c>History</c> (Excel's own), unique ignoring case. A name the model or the user
/// gives is refused with the rule (<see cref="Problem"/>); a name the app makes up is made fit (<see cref="Fit"/>). Pure.
/// </summary>
public static class SheetName
{
    public const int MaxLength = 31;

    /// <summary>The characters Excel refuses in a sheet's name.</summary>
    public const string Forbidden = ":\\/?*[]";

    /// <summary>The name Excel keeps for itself.</summary>
    public const string Reserved = "History";

    /// <summary>
    /// Whether <paramref name="name"/> breaks a rule: blank, too long, a forbidden or control character, half a surrogate pair, a quote at an
    /// end, or <c>History</c>. An emoji is allowed (2026-10-29, review P2-7: every surrogate was refused, so "Sales 📈" was); its length is
    /// counted in UTF-16 units, two for an emoji, the safe side of Excel's 31.
    /// </summary>
    public static bool Problem(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.Trim().Length == 0 || name.Length > MaxLength || name.AsSpan().IndexOfAny(Forbidden) >= 0
            || name.StartsWith('\'') || name.EndsWith('\'') || name.Equals(Reserved, StringComparison.OrdinalIgnoreCase)
            || name.Any(char.IsControl) || Lone(name).Count > 0;
    }

    /// <summary>The indexes of <paramref name="text"/>'s surrogates that aren't half of a pair.</summary>
    private static HashSet<int> Lone(string text)
    {
        var lone = new HashSet<int>();
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(text[i]))
            {
                lone.Add(i);
            }
        }

        return lone;
    }

    /// <summary>
    /// <paramref name="wanted"/> made fit to be a sheet's name: forbidden and control characters and half pairs as <c>-</c>, end quotes and
    /// spaces dropped, cut to <see cref="MaxLength"/>, <paramref name="fallback"/> when nothing is left; then unique against
    /// <paramref name="taken"/> ignoring case (<c>Query (2)</c>). Both cuts keep an emoji whole (2026-10-29, review P2-7).
    /// </summary>
    public static string Fit(string? wanted, IEnumerable<string> taken, string fallback = "Sheet1")
    {
        ArgumentNullException.ThrowIfNull(taken);
        string given = wanted ?? "";
        var lone = Lone(given);
        var chars = given.Select((c, i) => Forbidden.Contains(c, StringComparison.Ordinal) || char.IsControl(c) || lone.Contains(i) ? '-' : c).ToArray();
        string name = new string(chars).Trim().Trim('\'').Trim();
        if (name.Length > MaxLength)
        {
            name = Files.TextCut.Safe(name, MaxLength).TrimEnd().TrimEnd('\'');
        }

        if (name.Length == 0 || name.Equals(Reserved, StringComparison.OrdinalIgnoreCase))
        {
            name = fallback;
        }

        var used = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(name))
        {
            return name;
        }

        for (int n = 2; ; n++)
        {
            string suffix = " (" + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
            string candidate = (name.Length + suffix.Length > MaxLength ? Files.TextCut.Safe(name, MaxLength - suffix.Length) : name) + suffix;
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }
    }
}
