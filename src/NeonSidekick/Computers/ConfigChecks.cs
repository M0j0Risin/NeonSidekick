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
        return ComputerText.UnknownKey(key, Llm.Tools.NearestName.Nearest(key, KnownKeys(entry)));
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
}
