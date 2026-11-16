namespace NeonSidekick.Llm.Tools;

/// <summary>
/// The nearest-name search (2026-11-15, the tool-arguments review's #144: it lived in <c>Computers.ConfigChecks</c>, and the argument check and
/// <c>patch_file</c>'s edit items reached into the admin families for it). A config file's unknown key and a tool call's misnamed argument are
/// answered by the same rule: case ignored, within a third of the key's length in edits, two neighbors swapped counting as one. Pure.
/// </summary>
public static class NearestName
{
    /// <summary>The known name nearest <paramref name="key"/> (case ignored, within a third of its length in edits; the first of equal ones), or null.</summary>
    public static string? Nearest(string key, IEnumerable<string> known)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(known);
        string wanted = key.Trim().ToLowerInvariant();
        string? best = null;
        int bestDistance = int.MaxValue;
        foreach (string name in known)
        {
            int distance = Distance(wanted, name.ToLowerInvariant());
            if (distance < bestDistance)
            {
                best = name;
                bestDistance = distance;
            }
        }

        return best is not null && bestDistance <= Math.Max(1, wanted.Length / 3) ? best : null;
    }

    /// <summary>
    /// The edit distance (insert, delete, change, and two neighbors swapped) between two short words. A swap counts as one edit (2026-11-15,
    /// feature idea #42: a tool argument typed <c>qurey</c> was two edits from <c>query</c>, past a five-letter key's third).
    /// </summary>
    public static int Distance(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var before = new int[b.Length + 1];
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int change = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                current[j] = Math.Min(change, Math.Min(previous[j] + 1, current[j - 1] + 1));
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    current[j] = Math.Min(current[j], before[j - 2] + 1);
                }
            }

            (before, previous, current) = (previous, current, before);
        }

        return previous[b.Length];
    }
}
