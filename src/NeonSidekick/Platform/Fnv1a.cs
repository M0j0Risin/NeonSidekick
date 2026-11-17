namespace NeonSidekick.Platform;

/// <summary>
/// 32-bit FNV-1a over UTF-16 units (2026-11-17, the admin-themes-render review's #57: three copies made one): stable across runs, unlike
/// <see cref="string.GetHashCode()"/>. The Web UI's theme key, a long tool name's suffix for Anthropic, and the PDF print's fingerprint, whose
/// in-page twin (<c>Math.imul</c> over <c>charCodeAt</c>) it must keep matching. Pure.
/// </summary>
public static class Fnv1a
{
    /// <summary>The hash of <paramref name="text"/>'s UTF-16 units.</summary>
    public static uint Hash(ReadOnlySpan<char> text)
    {
        uint hash = 2166136261;
        foreach (char c in text)
        {
            hash = unchecked((hash ^ c) * 16777619);
        }

        return hash;
    }
}
