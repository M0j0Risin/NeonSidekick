namespace NeonSidekick.Help;

/// <summary>
/// One whitespace-separated piece of a <c>neon_help</c> query as the tool, flag, variable and file steps read it (2026-10-25, the neon_help
/// plan: <c>NeonHelp.Words</c> drops <c>_</c>, so those steps read the raw query): quotes, backticks, brackets and closing punctuation
/// trimmed (<c>`web_fetch()`</c> is <c>web_fetch</c>, <c>sql.json?</c> is <c>sql.json</c>), whether it sat in backticks, and the words
/// beside it in lower case ("copy tool", "HOME variable").
/// </summary>
internal readonly record struct HelpToken(string Text, bool Ticked, string? Previous, string? Next)
{
    private static readonly char[] s_trimStart = ['`', '"', '\'', '(', '[', '{', '<', '*', '“', '‘'];
    private static readonly char[] s_trimEnd = ['`', '"', '\'', '(', ')', '[', ']', '{', '}', '<', '>', ',', ';', ':', '?', '!', '.', '*', '”', '’'];

    /// <summary>The query's tokens in order, empty pieces dropped.</summary>
    public static IReadOnlyList<HelpToken> Read(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var pieces = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var cleaned = pieces.Select(Clean).ToArray();
        var tokens = new List<HelpToken>(pieces.Length);
        for (int i = 0; i < pieces.Length; i++)
        {
            if (cleaned[i].Length == 0)
            {
                continue;
            }

            tokens.Add(new HelpToken(
                cleaned[i],
                pieces[i].Contains('`', StringComparison.Ordinal),
                i > 0 ? cleaned[i - 1].ToLowerInvariant() : null,
                i + 1 < pieces.Length ? cleaned[i + 1].ToLowerInvariant() : null));
        }

        return tokens;
    }

    private static string Clean(string piece)
    {
        string text = piece.TrimStart(s_trimStart).TrimEnd(s_trimEnd);
        return text.EndsWith("()", StringComparison.Ordinal) ? text[..^2] : text;
    }
}

/// <summary>An entry the free-word search scores (2026-10-25): its name (three points a word), its text (one), and its answer, built only when listed.</summary>
internal sealed record HelpItem(string Name, string Text, Func<string> Answer);
