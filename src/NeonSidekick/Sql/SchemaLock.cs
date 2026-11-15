namespace NeonSidekick.Sql;

/// <summary>
/// The schema lock Oracle and PostgreSQL entries share (2026-11-15, the database-tools review's #44: the list's rules, the wizard's
/// <c>HoldTo</c> and the cap were written out once per family). Each family brings its own words, its own judgment of one name and its
/// own idea of when two names are one (Oracle's dictionary spelling, PostgreSQL's exact text).
/// </summary>
public static class SchemaLock
{
    /// <summary>The most schemas one entry's <c>schemas</c> may list (2026-11-14, feature idea #18).</summary>
    public const int MaxSchemas = 16;

    /// <summary>A family's refusals of a list: both keys given, an empty list, past <see cref="MaxSchemas"/>, a name listed twice.</summary>
    public sealed record Words(string BothKeys, string Empty, Func<int, string> TooMany, Func<string, string> Repeated);

    /// <summary>
    /// The reason a <c>schemas</c> list cannot hold the connection, or null, in this order: <paramref name="schema"/> given beside it, an
    /// empty list, more than <see cref="MaxSchemas"/>, then each name's <paramref name="nameProblem"/> and a repeat, two names one when
    /// their <paramref name="sameAs"/> (of the trimmed name) is.
    /// </summary>
    public static string? ListProblem(string? schema, IReadOnlyList<string?> schemas, Words words, Func<string?, string?> nameProblem, Func<string, string> sameAs)
    {
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(nameProblem);
        ArgumentNullException.ThrowIfNull(sameAs);
        if (!string.IsNullOrWhiteSpace(schema))
        {
            return words.BothKeys;
        }

        if (schemas.Count == 0)
        {
            return words.Empty;
        }

        if (schemas.Count > MaxSchemas)
        {
            return words.TooMany(MaxSchemas);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string? name in schemas)
        {
            if (nameProblem(name) is { } problem)
            {
                return problem;
            }

            string trimmed = name!.Trim();
            if (!seen.Add(sameAs(trimmed)))
            {
                return words.Repeated(trimmed);
            }
        }

        return null;
    }

    /// <summary>
    /// The two keys the wizard's pick of <paramref name="names"/> is saved as: none clears both, several are a <c>schemas</c> list, and one
    /// is a <c>schema</c> unless <paramref name="asList"/> (2026-11-15, the database-tools review's #29: an entry written with a one-name
    /// <c>schemas</c> list kept its form only until the wizard saved it).
    /// </summary>
    public static (string? Schema, List<string>? Schemas) Hold(IReadOnlyList<string> names, bool asList)
    {
        ArgumentNullException.ThrowIfNull(names);
        return names.Count == 0 ? (null, null) : names.Count == 1 && !asList ? (names[0], null) : (null, [.. names]);
    }
}
