namespace NeonSidekick.Files;

/// <summary>
/// A path glob for <c>search_files</c> (2026-09-17; <c>find_files</c> shared it until it was folded in on 2026-09-18): <c>**</c> matches any
/// run of segments (none too), <c>*</c> and <c>?</c> match within one segment, <c>/</c> and
/// <c>\</c> are the same separator, case is ignored. A pattern with no separator and no <c>**</c>
/// is a plain name pattern the walks hand to <see cref="System.IO.Enumeration.FileSystemName"/>
/// as before — <see cref="IsPathPattern"/> is that test. Brace alternatives (<c>*.{png,jpg}</c>,
/// 2026-09-24) are expanded first by <see cref="ExpandBraces"/>, each alternative a pattern of its own.
/// </summary>
public static class PathGlob
{
    private static readonly char[] Separators = ['/', '\\'];

    /// <summary>
    /// The most alternatives <see cref="ExpandBraces"/> yields; a pattern that would expand past it
    /// is kept whole (literal braces) rather than turning one walk into thousands of matches.
    /// </summary>
    public const int MaxBraceExpansions = 64;

    /// <summary>
    /// <paramref name="pattern"/> with its brace groups expanded, bash-style: <c>*.{png,jpg}</c> is
    /// <c>*.png</c> and <c>*.jpg</c>; groups nest and multiply (<c>{src,lib}/**/*.{cs,md}</c> is four).
    /// A <c>{</c> without its <c>}</c>, or a group without a comma (<c>{x}</c>), stays literal; an empty
    /// alternative is allowed (<c>a{,b}</c>). Duplicates (ignoring case) are dropped; past
    /// <see cref="MaxBraceExpansions"/> the pattern comes back unexpanded. 2026-09-24: a model searched
    /// a folder with <c>*.{png,jpg,jpeg,gif,webp}</c> and found nothing — neither the Win32 name match
    /// nor <see cref="IsMatch"/> knew braces, so every character was taken literally.
    /// </summary>
    public static IReadOnlyList<string> ExpandBraces(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int steps = 0;
        return Expand(pattern, results, seen, ref steps) ? results : [pattern];
    }

    /// <summary>
    /// The most expansion steps one pattern may take (2026-10-22, review G6: the cap counted distinct results, so <c>{,}</c> forty times
    /// — one result — walked 2^40 paths before it was reached).
    /// </summary>
    public const int MaxBraceSteps = MaxBraceExpansions * 16;

    /// <summary>Appends <paramref name="pattern"/>'s expansions; false once the cap of results or of steps is passed.</summary>
    private static bool Expand(string pattern, List<string> results, HashSet<string> seen, ref int steps)
    {
        if (++steps > MaxBraceSteps)
        {
            return false;
        }

        if (!TryFindGroup(pattern, out int open, out int close, out List<string> alternatives))
        {
            if (seen.Add(pattern))
            {
                results.Add(pattern);
            }

            return results.Count <= MaxBraceExpansions;
        }

        string prefix = pattern[..open];
        string suffix = pattern[(close + 1)..];
        foreach (string alternative in alternatives)
        {
            if (!Expand(prefix + alternative + suffix, results, seen, ref steps))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The first brace group that has a top-level comma: its <c>{</c>, its matching <c>}</c> and the
    /// alternatives between (split only at depth one, so nested groups ride along for the next pass).
    /// </summary>
    private static bool TryFindGroup(string pattern, out int open, out int close, out List<string> alternatives)
    {
        for (open = pattern.IndexOf('{', StringComparison.Ordinal); open >= 0; open = pattern.IndexOf('{', open + 1))
        {
            alternatives = [];
            int depth = 0, start = open + 1;
            for (close = open; close < pattern.Length; close++)
            {
                char c = pattern[close];
                if (c == '{')
                {
                    depth++;
                }
                else if (c == '}' && --depth == 0)
                {
                    break;
                }
                else if (c == ',' && depth == 1)
                {
                    alternatives.Add(pattern[start..close]);
                    start = close + 1;
                }
            }

            if (close < pattern.Length && alternatives.Count > 0)
            {
                alternatives.Add(pattern[start..close]);
                return true;
            }
        }

        close = -1;
        alternatives = [];
        return false;
    }

    /// <summary>Whether <paramref name="pattern"/> speaks of folders (a separator or <c>**</c>) and must be matched by <see cref="IsMatch"/> over the whole relative path.</summary>
    public static bool IsPathPattern(string pattern) =>
        pattern.IndexOfAny(Separators) >= 0 || pattern.Contains("**", StringComparison.Ordinal);

    /// <summary>Whether <paramref name="relativePath"/> (any separator, no leading one) matches <paramref name="pattern"/> whole.</summary>
    public static bool IsMatch(string pattern, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(relativePath);
        string[] parts = Parts(pattern);
        string[] segments = relativePath.Trim(Separators).Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        var memo = new sbyte[(parts.Length + 1) * (segments.Length + 1)];
        return MatchSegments(parts, 0, segments, 0, memo);
    }

    /// <summary>
    /// A pattern's segments as the matcher takes them: a <c>.</c> segment dropped (2026-10-22, review Q7: <c>./**/*.md</c> matched nothing), and
    /// a <c>**</c> fused into a segment split out of it (review Q8: <c>**.cs</c> and <c>src/**.cs</c> matched one level only) — <c>**.cs</c> is
    /// <c>**</c> then <c>*.cs</c>, <c>src**</c> is <c>src*</c> then <c>**</c>; any other <c>**</c> inside a segment is a <c>*</c>.
    /// </summary>
    private static string[] Parts(string pattern)
    {
        var parts = new List<string>();
        foreach (string raw in pattern.Trim().Trim(Separators).Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw == ".")
            {
                continue;
            }

            if (raw == "**" || !raw.Contains("**", StringComparison.Ordinal))
            {
                parts.Add(raw);
                continue;
            }

            if (raw.StartsWith("**", StringComparison.Ordinal))
            {
                parts.Add("**");
                parts.Add("*" + raw[2..].Replace("**", "*", StringComparison.Ordinal));
            }
            else if (raw.EndsWith("**", StringComparison.Ordinal))
            {
                parts.Add(raw[..^2].Replace("**", "*", StringComparison.Ordinal) + "*");
                parts.Add("**");
            }
            else
            {
                parts.Add(raw.Replace("**", "*", StringComparison.Ordinal));
            }
        }

        return [.. parts];
    }

    /// <summary>
    /// Whether <paramref name="parts"/> from <paramref name="p"/> match <paramref name="segments"/> from <paramref name="s"/>, each (p, s) worked
    /// out once (2026-10-22, review G6: many <c>**</c> over a deep path retried the same splits exponentially): 0 unknown, 1 yes, -1 no.
    /// </summary>
    private static bool MatchSegments(string[] parts, int p, string[] segments, int s, sbyte[] memo)
    {
        int key = p * (segments.Length + 1) + s;
        if (memo[key] != 0)
        {
            return memo[key] > 0;
        }

        bool result = Match(parts, p, segments, s, memo);
        memo[key] = result ? (sbyte)1 : (sbyte)-1;
        return result;
    }

    private static bool Match(string[] parts, int p, string[] segments, int s, sbyte[] memo)
    {
        while (p < parts.Length)
        {
            if (parts[p] == "**")
            {
                // Collapse a run of ** and try every split of the rest.
                while (p < parts.Length && parts[p] == "**")
                {
                    p++;
                }

                if (p == parts.Length)
                {
                    return true;
                }

                for (int i = s; i <= segments.Length; i++)
                {
                    if (MatchSegments(parts, p, segments, i, memo))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (s >= segments.Length || !MatchSegment(parts[p], segments[s]))
            {
                return false;
            }

            p++;
            s++;
        }

        return s == segments.Length;
    }

    /// <summary>One segment against one glob segment: <c>*</c> any run, <c>?</c> one character, case ignored.</summary>
    public static bool MatchSegment(string pattern, string text)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(text);
        int p = 0, t = 0, starP = -1, starT = -1;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(text[t])))
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starP = p++;
                starT = t;
            }
            else if (starP >= 0)
            {
                p = starP + 1;
                t = ++starT;
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
}
