using System.Globalization;
using System.Text;
using NeonSidekick.Files;

namespace NeonSidekick.App;

/// <summary>
/// The words for <c>/tree</c>: the walked folder's full path on the first line, then one line per
/// entry drawn with box-drawing branches the way <c>tree</c> prints them, a size after each file
/// when asked, and a tail line when the cap stopped the walk. Pure statics, every string pinned;
/// the caller prints each line as a transcript notice (the theme escapes the text, so a bracket in
/// a name is safe).
/// </summary>
public static class TreeText
{
    public const string Branch = "├── ";
    public const string LastBranch = "└── ";
    public const string Continuation = "│   ";
    public const string Gap = "    ";

    /// <summary>The one line under the header when the folder holds nothing.</summary>
    public const string EmptyLine = "(empty)";

    /// <summary>Between a file's name and its size.</summary>
    public const string SizeGap = "  ";

    /// <summary>The tail line when the walk stopped at the cap.</summary>
    public static string CutLine(int cap) =>
        "… only the first " + cap.ToString(CultureInfo.InvariantCulture) + " entries are shown (File /tree max length)";

    /// <summary>
    /// The info pane's label and tab title for <c>/tree</c> or <c>/vault</c> under a reply (later on 2026-09-27): the command
    /// and its path, <c>/tree docs</c>; the command alone for the root. Pinned.
    /// </summary>
    public static string PaneLabel(string command, string args) =>
        string.IsNullOrWhiteSpace(args) ? command : command + " " + args.Trim();

    /// <summary>The error sentence for a walk that did not reach the folder.</summary>
    public static string Error(FileTreeResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return FileText.Error(result.Outcome, result.Relative, "list", result.Detail);
    }

    /// <summary>
    /// The lines of an <see cref="FileOutcome.Ok"/> walk: the header, the entries, then the cut
    /// line under a truncated walk. <paramref name="cap"/> is the number the cut line quotes.
    /// </summary>
    public static IReadOnlyList<string> Lines(FileTreeResult result, bool showSizes, int cap)
    {
        ArgumentNullException.ThrowIfNull(result);
        var lines = new List<string>(result.Entries.Count + 2) { result.FullPath };
        if (result.Entries.Count == 0)
        {
            lines.Add(EmptyLine);
            return lines;
        }

        // Per depth above the current entry: whether that ancestor was the last of its siblings,
        // which decides between a vertical rule and a gap under it.
        var ancestorsLast = new List<bool>();
        var sb = new StringBuilder();
        foreach (var entry in result.Entries)
        {
            if (ancestorsLast.Count > entry.Depth - 1)
            {
                ancestorsLast.RemoveRange(entry.Depth - 1, ancestorsLast.Count - (entry.Depth - 1));
            }

            sb.Clear();
            foreach (bool last in ancestorsLast)
            {
                sb.Append(last ? Gap : Continuation);
            }

            sb.Append(entry.IsLast ? LastBranch : Branch).Append(entry.Name);
            if (entry.IsDirectory)
            {
                sb.Append(Path.DirectorySeparatorChar);
            }
            else if (showSizes)
            {
                sb.Append(SizeGap).Append(FileText.Size(entry.Length));
            }

            lines.Add(sb.ToString());
            ancestorsLast.Add(entry.IsLast);
        }

        if (result.Truncated)
        {
            lines.Add(CutLine(cap));
        }

        return lines;
    }
}
