using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace NeonSidekick.Computers;

/// <summary>
/// The checks every admin family's loader makes before an entry is used (2026-10-23, the admin review's mechanism 1): a key the entry does
/// not know (H5, decision DA1: <c>controlAction</c> for <c>controlActions</c> read as "every action"; now the entry is refused naming the key
/// and the nearest known one, the other entries still loading), and the file-level keys beside the section likewise, as a problem the menus
/// list while the entries load. Known keys come from the family's source-generated <see cref="JsonTypeInfo"/>, case ignored as the
/// deserializer reads them. Pure.
/// </summary>
public static class ConfigChecks
{
    /// <summary>A file's bytes past a UTF-8 byte-order mark.</summary>
    public static ReadOnlySpan<byte> WithoutBom(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? bytes.AsSpan(3) : bytes;
    }

    /// <summary>The first key of <paramref name="extra"/> (an entry's unknown keys) as a problem, or null when there is none.</summary>
    public static string? UnknownKey(IReadOnlyDictionary<string, JsonElement>? extra, JsonTypeInfo entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (extra is not { Count: > 0 })
        {
            return null;
        }

        string key = extra.Keys.First();
        return ComputerText.UnknownKey(key, Nearest(key, KnownKeys(entry)));
    }

    /// <summary>The file-level keys beside <paramref name="section"/> as problems (one each).</summary>
    public static IEnumerable<string> UnknownFileKeys(IReadOnlyDictionary<string, JsonElement>? extra, string section)
    {
        ArgumentNullException.ThrowIfNull(section);
        return extra is not { Count: > 0 } ? [] : extra.Keys.Select(key => ComputerText.UnknownFileKey(key, section));
    }

    /// <summary>The JSON names <paramref name="entry"/> reads (its extension bag left out).</summary>
    public static IReadOnlyList<string> KnownKeys(JsonTypeInfo entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Properties.Where(p => !p.IsExtensionData).Select(p => p.Name).ToList();
    }

    /// <summary>The known key nearest <paramref name="key"/> (case ignored, within a third of its length in edits), or null.</summary>
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

    /// <summary>The edit distance (insert, delete, change) between two short words.</summary>
    private static int Distance(string a, string b)
    {
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
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
