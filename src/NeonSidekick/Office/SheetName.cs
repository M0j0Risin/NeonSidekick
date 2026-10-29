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

    /// <summary>Whether <paramref name="name"/> breaks a rule: blank, too long, a forbidden character, a quote at an end, or <c>History</c>.</summary>
    public static bool Problem(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.Trim().Length == 0 || name.Length > MaxLength || name.AsSpan().IndexOfAny(Forbidden) >= 0
            || name.StartsWith('\'') || name.EndsWith('\'') || name.Equals(Reserved, StringComparison.OrdinalIgnoreCase)
            || name.Any(c => char.IsControl(c) || char.IsSurrogate(c));
    }

    /// <summary>
    /// <paramref name="wanted"/> made fit to be a sheet's name: forbidden and control characters as <c>-</c>, end quotes and spaces dropped,
    /// cut to <see cref="MaxLength"/>, <paramref name="fallback"/> when nothing is left; then unique against <paramref name="taken"/>
    /// ignoring case (<c>Query (2)</c>).
    /// </summary>
    public static string Fit(string? wanted, IEnumerable<string> taken, string fallback = "Sheet1")
    {
        ArgumentNullException.ThrowIfNull(taken);
        var chars = (wanted ?? "").Select(c => Forbidden.Contains(c, StringComparison.Ordinal) || char.IsControl(c) || char.IsSurrogate(c) ? '-' : c).ToArray();
        string name = new string(chars).Trim().Trim('\'').Trim();
        if (name.Length > MaxLength)
        {
            name = name[..MaxLength].TrimEnd().TrimEnd('\'');
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
            string candidate = (name.Length + suffix.Length > MaxLength ? name[..(MaxLength - suffix.Length)] : name) + suffix;
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }
    }
}
