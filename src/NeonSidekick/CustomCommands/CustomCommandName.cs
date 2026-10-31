using System.Text;

namespace NeonSidekick.CustomCommands;

/// <summary>
/// A custom command's name (2026-10-30): the file's path under its <c>commands</c> folder, the folders and the stem lower-cased and joined by
/// <c>:</c> (Claude Code's rule: <c>frontend/component.md</c> is <c>/frontend:component</c>). Stricter than Claude's on purpose: each segment is
/// 1 to 64 of <c>a-z 0-9 _ -</c>, the first a letter or digit, the whole at most 128, and no segment a Windows device name, so a name types
/// and completes on any keyboard and a file pushes between a PC and a Mac. A name with <c>:</c> is never a built-in. Pure.
/// </summary>
public static class CustomCommandName
{
    /// <summary>The longest segment and the longest whole name.</summary>
    public const int MaxSegmentLength = 64;
    public const int MaxLength = 128;

    /// <summary>How many folders deep under a <c>commands</c> folder the scan reads (a name has one more segment than that at most).</summary>
    public const int MaxDepth = 4;

    /// <summary>The file extension a command file has (matched ignoring case, for a Mac volume's <c>.MD</c>).</summary>
    public const string Extension = ".md";

    /// <summary>The joiner between a name's folder segments and its stem.</summary>
    public const char Separator = ':';

    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "con", "prn", "aux", "nul",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    };

    /// <summary>Whether <paramref name="name"/> is a valid name, as typed after the <c>/</c>.</summary>
    public static bool IsValid(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxLength)
        {
            return false;
        }

        string[] segments = name.Split(Separator);
        return segments.Length <= MaxDepth + 1 && segments.All(IsValidSegment);
    }

    /// <summary>Whether one segment (a folder's or the stem's, lower-cased) is valid.</summary>
    public static bool IsValidSegment(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (segment.Length is 0 or > MaxSegmentLength || !(segment[0] is >= 'a' and <= 'z' or >= '0' and <= '9') || IsDeviceName(segment))
        {
            return false;
        }

        foreach (char c in segment)
        {
            if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether <paramref name="segment"/> is a Windows device name (<c>con</c>, <c>nul</c>, <c>com1</c> …), refused on both systems.</summary>
    public static bool IsDeviceName(string segment) => DeviceNames.Contains(segment);

    /// <summary>
    /// The name a file at <paramref name="relativePath"/> (under its folder, either separator) would have: its folders and stem lower-cased
    /// and joined by <c>:</c>, the extension dropped. Null when the result is no valid name (<see cref="IsValid"/>).
    /// </summary>
    public static string? FromRelativePath(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        string name = RawName(relativePath);
        return IsValid(name) ? name : null;
    }

    /// <summary>The name <paramref name="relativePath"/> spells, valid or not: lower-cased, joined by <c>:</c>, the extension dropped.</summary>
    public static string RawName(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        string[] parts = relativePath.Split('/', '\\');
        string stem = parts[^1];
        if (stem.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
        {
            stem = stem[..^Extension.Length];
        }

        parts[^1] = stem;
        return string.Join(Separator, parts.Select(p => p.ToLowerInvariant()));
    }

    /// <summary>
    /// What was typed made a name, the Skills pane's <c>KebabName</c> way per segment (<c>My Review</c> → <c>my-review</c>, <c>Front End:Card</c>
    /// → <c>front-end:card</c>, a <c>/</c> read as <c>:</c>): lower case, a run of anything else one hyphen, a segment starting with a letter or
    /// digit. Empty when nothing survives. Not checked for a device name or length: <see cref="IsValid"/> is.
    /// </summary>
    public static string Normalize(string typed)
    {
        ArgumentNullException.ThrowIfNull(typed);
        var segments = new List<string>();
        foreach (string part in typed.Trim().TrimStart('/').Split(Separator, '/', '\\'))
        {
            var kebab = new StringBuilder(part.Length);
            bool gap = false;
            foreach (char c in part.ToLowerInvariant())
            {
                if (c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_')
                {
                    if (c == '_' && kebab.Length == 0)
                    {
                        continue;
                    }

                    if (gap && kebab.Length > 0)
                    {
                        kebab.Append('-');
                    }

                    gap = false;
                    kebab.Append(c);
                }
                else
                {
                    gap = true;
                }
            }

            if (kebab.Length > 0)
            {
                segments.Add(kebab.ToString());
            }
        }

        return string.Join(Separator, segments);
    }

    /// <summary>The file's path under its folder for <paramref name="name"/>: <c>a:b</c> → <c>a/b.md</c> (this system's separator).</summary>
    public static string RelativePath(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string[] segments = name.Split(Separator);
        segments[^1] += Extension;
        return Path.Combine(segments);
    }

    /// <summary>
    /// The names in <paramref name="relativePaths"/> (one folder's files, in scan order) that repeat one before them once lower-cased: on a
    /// case-sensitive volume <c>Review.md</c> and <c>review.md</c> are two files with one name, and the second is skipped. Pure.
    /// </summary>
    public static IReadOnlySet<string> CaseDuplicates(IEnumerable<string> relativePaths)
    {
        ArgumentNullException.ThrowIfNull(relativePaths);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var twice = new HashSet<string>(StringComparer.Ordinal);
        foreach (string path in relativePaths)
        {
            if (!seen.Add(RawName(path)))
            {
                twice.Add(path);
            }
        }

        return twice;
    }
}
