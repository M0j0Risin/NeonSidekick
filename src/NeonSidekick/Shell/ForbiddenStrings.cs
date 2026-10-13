using System.Text;

namespace NeonSidekick.Shell;

/// <summary>
/// The shell police's second rule (2026-10-03, the user's idea): <c>Shell police forbidden strings</c>, a list the user keeps, and
/// a <c>run_command</c> line, <c>execute_code</c> script or <c>process</c> write that contains one of them is refused before the gate
/// is asked. Loose on purpose (the user's call): case ignored and every run of whitespace — spaces, tabs, line breaks — read as
/// one space on both sides, so <c>rm -rf</c> catches <c>RM   -RF</c> and a script's <c>rm</c> and <c>-rf</c> on two lines. A
/// tripwire, not a sandbox: text built in pieces (<c>"r" + "m"</c>, cmd's <c>r^m</c>) gets past it. The list keeps the user's case
/// (unlike <see cref="CommandAllowList.Merge"/>, which lower-cases), sorted A to Z ignoring case, no duplicates ignoring case.
/// </summary>
public static class ForbiddenStrings
{
    /// <summary>
    /// The manifest name <c>assets/police/strings.txt</c> is embedded under (2026-10-13, the user's ask: the starter list in a file,
    /// the fun verbs' way, so it changes without touching code). Its <c>[all]</c>, <c>[windows]</c> and <c>[mac]</c> sections are
    /// <see cref="SharedDefaults"/>, <see cref="WindowsDefaults"/> and <see cref="MacDefaults"/> (<see cref="Parse"/>); the reasons for
    /// each entry are the file's own comments.
    /// </summary>
    public const string ResourceName = "police/strings.txt";

    /// <summary>The file's three sections, read once.</summary>
    private static readonly (string[] Shared, string[] Windows, string[] Mac) Embedded = ReadEmbedded();

    private static (string[] Shared, string[] Windows, string[] Mac) ReadEmbedded()
    {
        using var stream = typeof(ForbiddenStrings).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The forbidden strings " + ResourceName + " are not embedded.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>
    /// The sections <paramref name="text"/> holds (2026-10-13): one string per line (CRLF or LF), each trimmed, blank lines skipped,
    /// a line whose first non-blank character is <c>#</c> a comment; a <c>[all]</c>, <c>[windows]</c> or <c>[mac]</c> line (any case)
    /// starts a section, the lines before any header are <c>[all]</c>'s, and an unknown header's lines are skipped. A repeat within a
    /// section is dropped (the first kept); everything else is the file's, kept as written. Pure.
    /// </summary>
    public static (string[] Shared, string[] Windows, string[] Mac) Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var shared = new List<string>();
        var windows = new List<string>();
        var mac = new List<string>();
        List<string>? into = shared;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            if (line[0] == '[' && line[^1] == ']')
            {
                into = line[1..^1].Trim().ToLowerInvariant() switch
                {
                    "all" => shared,
                    "windows" => windows,
                    "mac" => mac,
                    _ => null,
                };
                continue;
            }

            if (into is not null && !into.Contains(line, StringComparer.Ordinal))
            {
                into.Add(line);
            }
        }

        return (shared.ToArray(), windows.ToArray(), mac.ToArray());
    }

    /// <summary>
    /// The entries a fresh profile starts with on either system (2026-10-08, the user's call, from the review in
    /// <c>.notes/RECOMMENDED_FORBIDDEN_STRINGS.md</c>): the file's <c>[all]</c> section since 2026-10-13 (<see cref="ResourceName"/>).
    /// </summary>
    public static string[] SharedDefaults => [.. Embedded.Shared];

    /// <summary>Windows' own defaults beside <see cref="SharedDefaults"/>: the file's <c>[windows]</c> section.</summary>
    public static string[] WindowsDefaults => [.. Embedded.Windows];

    /// <summary>The Mac's own defaults beside <see cref="SharedDefaults"/>: the file's <c>[mac]</c> section.</summary>
    public static string[] MacDefaults => [.. Embedded.Mac];

    /// <summary>
    /// What <see cref="Settings.AppSettingsData.ShellPoliceForbiddenStrings"/> starts as: <see cref="SharedDefaults"/> and this
    /// system's own list (Windows', else the Mac's), <see cref="Sorted"/> as the editor saves it. Only a fresh profile gets it, and
    /// the restore button puts it back; a saved list stands, the fresh <c>ToolsDisabled</c>'s way, so a profile keeps its own list
    /// when the file changes.
    /// </summary>
    public static List<string> Defaults => Sorted([.. SharedDefaults, .. OperatingSystem.IsWindows() ? WindowsDefaults : MacDefaults]);

    /// <summary><paramref name="text"/> with every run of whitespace one space, trimmed: the form both sides are compared in.</summary>
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length);
        bool space = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                space = builder.Length > 0;
                continue;
            }

            if (space)
            {
                builder.Append(' ');
                space = false;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>
    /// The first entry of <paramref name="forbidden"/>, in list order and as the user typed it, that <paramref name="text"/>
    /// contains once both are <see cref="Normalize"/>d, case ignored; null when none does. A blank entry never matches.
    /// </summary>
    public static string? Find(string text, IReadOnlyList<string> forbidden)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(forbidden);
        if (forbidden.Count == 0)
        {
            return null;
        }

        string normalized = Normalize(text);
        foreach (string entry in forbidden)
        {
            string needle = Normalize(entry ?? "");
            if (needle.Length > 0 && normalized.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>The saved list as the editor shows it and saves it: each entry <see cref="Normalize"/>d, blanks gone, no duplicates ignoring case (the first kept), sorted A to Z ignoring case.</summary>
    public static List<string> Sorted(IReadOnlyList<string> saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (string entry in saved)
        {
            string normalized = Normalize(entry ?? "");
            if (normalized.Length > 0 && seen.Add(normalized))
            {
                list.Add(normalized);
            }
        }

        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    /// <summary>Whether <paramref name="entry"/> is already in <paramref name="saved"/>, both <see cref="Normalize"/>d, case ignored.</summary>
    public static bool Contains(IReadOnlyList<string> saved, string entry)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(entry);
        string normalized = Normalize(entry);
        return saved.Any(existing => string.Equals(Normalize(existing ?? ""), normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The saved list with <paramref name="entry"/> added (<see cref="Sorted"/>); a blank entry or one already there leaves it as it was.</summary>
    public static List<string> Add(IReadOnlyList<string> saved, string entry)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(entry);
        return Sorted([.. saved, entry]);
    }

    /// <summary>The saved list without <paramref name="entry"/> (both <see cref="Normalize"/>d, case ignored), <see cref="Sorted"/>: the editor's remove.</summary>
    public static List<string> Without(IReadOnlyList<string> saved, string entry)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(entry);
        string normalized = Normalize(entry);
        var list = Sorted(saved);
        list.RemoveAll(existing => string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase));
        return list;
    }
}
