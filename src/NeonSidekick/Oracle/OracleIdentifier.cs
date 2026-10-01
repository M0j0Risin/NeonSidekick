namespace NeonSidekick.Oracle;

/// <summary>
/// Oracle names as the dictionary holds them (2026-09-30): an unquoted name is folded to upper case (<c>hr</c> is
/// <c>HR</c>), a <c>"quoted"</c> one is kept as written. The app's own statements put a schema in quotes
/// (<see cref="Quote"/>) — never a model's text spliced into SQL — and bind every other name.
/// </summary>
public static class OracleIdentifier
{
    /// <summary>The longest name Oracle takes (12.2 and later).</summary>
    public const int MaxLength = 128;

    /// <summary>
    /// <paramref name="name"/> as the dictionary spells it: unquoted and simple (a letter, then letters, digits, <c>_</c>,
    /// <c>$</c>, <c>#</c>) upper-cased; <c>"quoted"</c> as it is inside the quotes. Null for anything else — a blank, a
    /// quote inside, a space in an unquoted name, past <see cref="MaxLength"/>.
    /// </summary>
    public static string? Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string text = name.Trim();
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
        {
            string inner = text[1..^1];
            return inner.Length is > 0 and <= MaxLength && !inner.Contains('"', StringComparison.Ordinal) && !inner.Contains('\0', StringComparison.Ordinal) ? inner : null;
        }

        if (text.Length > MaxLength || !char.IsAsciiLetter(text[0]))
        {
            return null;
        }

        foreach (char c in text)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '$' or '#'))
            {
                return null;
            }
        }

        return text.ToUpperInvariant();
    }

    /// <summary>The name <see cref="Normalize"/>d, in double quotes — safe to write into the app's own <c>ALTER SESSION</c>. Null when it is no name.</summary>
    public static string? Quote(string? name) => Normalize(name) is { } normal ? "\"" + normal + "\"" : null;

    /// <summary>
    /// A table reference split into owner and name, each <see cref="Normalize"/>d: <c>hr.employees</c> is
    /// <c>(HR, EMPLOYEES)</c>, <c>employees</c> is <c>(null, EMPLOYEES)</c>, <c>"Hr"."Emp.Log"</c> keeps its dot inside
    /// the quotes. Null when it is not one or two names.
    /// </summary>
    public static (string? Owner, string Name)? Split(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        bool quoted = false;
        foreach (char c in reference.Trim())
        {
            if (c == '"')
            {
                quoted = !quoted;
            }

            if (c == '.' && !quoted)
            {
                parts.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        parts.Add(current.ToString());
        if (quoted || parts.Count > 2)
        {
            return null;
        }

        var names = parts.Select(Normalize).ToList();
        if (names.Any(n => n is null))
        {
            return null;
        }

        return names.Count == 1 ? (null, names[0]!) : (names[0], names[1]!);
    }
}
