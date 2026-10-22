using System.Globalization;
using System.Text;
using NeonSidekick.Files;

namespace NeonSidekick.Git;

/// <summary>
/// A unified diff of two texts, line by line, without libgit2 (2026-09-20): LibGit2Sharp's
/// <c>Diff.Compare&lt;T&gt;</c> kills the process under NativeAOT (libgit2sharp#2082 — an NRE inside a
/// callback native code invokes, which no <c>catch</c> reaches), so <see cref="GitAccess"/> never calls it
/// and every patch the git tools show is made here. Python's <c>difflib.unified_diff</c> to the line:
/// each distinct line is interned as one character and <see cref="SequenceSimilarity.Opcodes"/> (the
/// faithful <c>SequenceMatcher</c> port, <c>autojunk</c> included) is run over the two strings, then
/// <c>get_grouped_opcodes</c> makes the hunks. Both texts are LF-normalised first (the app's edit rule),
/// so a CRLF-only change shows nothing; renames are not detected. Pure.
/// </summary>
public static class UnifiedDiff
{
    /// <summary>Context lines on each side of a change, git's own default.</summary>
    public const int DefaultContext = 3;

    /// <summary>The most distinct lines the two texts may hold together: one <c>char</c> each.</summary>
    public const int MaxDistinctLines = 65_536;

    /// <summary>
    /// The most steps the line matcher takes before a diff is given up, null as for too many distinct lines (2026-10-22, review H13: a
    /// reordered file — a sorted 40,000-row CSV — kept the turn's thread for minutes after its write, for a diff only the transcript shows).
    /// </summary>
    public const long MaxDiffSteps = 50_000_000;

    /// <summary>git's marker under the last line of a side that does not end with a newline.</summary>
    public const string NoNewlineMarker = "\\ No newline at end of file";

    /// <summary>A text split for the diff: its lines without their terminators, and whether it ended with one.</summary>
    public readonly record struct Text(IReadOnlyList<string> Lines, bool EndsWithNewline)
    {
        /// <summary>The empty text: no lines, "ends with a newline" so no marker is printed for it.</summary>
        public static readonly Text Empty = new([], true);
    }

    /// <summary>One hunk: <c>@@ -OldStart,OldCount +NewStart,NewCount @@</c> (1-based) and its <c> </c> / <c>-</c> / <c>+</c> lines, the markers included.</summary>
    public readonly record struct Hunk(int OldStart, int OldCount, int NewStart, int NewCount, IReadOnlyList<string> Lines);

    /// <summary>Splits <paramref name="text"/> (CRLF and CR normalised to LF first) into its lines; a trailing newline ends the last line, never opens an empty one.</summary>
    public static Text Split(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return Text.Empty;
        }

        string normalised = WorkingDirectory.NormalizeNewlines(text);
        bool endsWithNewline = normalised.EndsWith('\n');
        var body = endsWithNewline ? normalised.AsSpan(0, normalised.Length - 1) : normalised.AsSpan();
        var lines = new List<string>();
        foreach (var range in body.Split('\n'))
        {
            lines.Add(body[range].ToString());
        }

        return new Text(lines, endsWithNewline);
    }

    /// <summary>
    /// The hunks turning <paramref name="a"/> into <paramref name="b"/> with <paramref name="context"/> lines
    /// around each change (difflib's <c>get_grouped_opcodes</c>); empty when the texts are equal, null when
    /// they hold more than <see cref="MaxDistinctLines"/> distinct lines together or the matcher passes <see cref="MaxDiffSteps"/>.
    /// </summary>
    public static IReadOnlyList<Hunk>? Hunks(Text a, Text b, int context = DefaultContext)
    {
        ArgumentNullException.ThrowIfNull(a.Lines);
        ArgumentNullException.ThrowIfNull(b.Lines);
        ArgumentOutOfRangeException.ThrowIfNegative(context);
        if (!TryIntern(a, b, out string sa, out string sb))
        {
            return null;
        }

        if (SequenceSimilarity.Opcodes(sa, sb, MaxDiffSteps) is not { } codes)
        {
            return null;
        }

        if (codes.Count == 0 || codes.All(c => c.Tag == SequenceSimilarity.Op.Equal))
        {
            return [];
        }

        var groups = Group(codes, context);
        var hunks = new List<Hunk>(groups.Count);
        foreach (var group in groups)
        {
            var first = group[0];
            var last = group[^1];
            var lines = new List<string>();
            foreach (var code in group)
            {
                if (code.Tag == SequenceSimilarity.Op.Equal)
                {
                    for (int i = code.I1; i < code.I2; i++)
                    {
                        lines.Add(" " + a.Lines[i]);
                        // The one line both sides end without a newline: one marker, git's shape.
                        if (i == a.Lines.Count - 1 && !a.EndsWithNewline)
                        {
                            lines.Add(NoNewlineMarker);
                        }
                    }

                    continue;
                }

                if (code.Tag is SequenceSimilarity.Op.Replace or SequenceSimilarity.Op.Delete)
                {
                    for (int i = code.I1; i < code.I2; i++)
                    {
                        lines.Add("-" + a.Lines[i]);
                        if (i == a.Lines.Count - 1 && !a.EndsWithNewline)
                        {
                            lines.Add(NoNewlineMarker);
                        }
                    }
                }

                if (code.Tag is SequenceSimilarity.Op.Replace or SequenceSimilarity.Op.Insert)
                {
                    for (int j = code.J1; j < code.J2; j++)
                    {
                        lines.Add("+" + b.Lines[j]);
                        if (j == b.Lines.Count - 1 && !b.EndsWithNewline)
                        {
                            lines.Add(NoNewlineMarker);
                        }
                    }
                }
            }

            hunks.Add(new Hunk(Start(first.I1, last.I2), last.I2 - first.I1, Start(first.J1, last.J2), last.J2 - first.J1, lines));
        }

        return hunks;
    }

    /// <summary>The patch text: the two <c>---</c> / <c>+++</c> labels, then every hunk; empty for no hunks.</summary>
    public static string Format(string oldLabel, string newLabel, IReadOnlyList<Hunk> hunks)
    {
        ArgumentNullException.ThrowIfNull(oldLabel);
        ArgumentNullException.ThrowIfNull(newLabel);
        ArgumentNullException.ThrowIfNull(hunks);
        if (hunks.Count == 0)
        {
            return "";
        }

        var sb = new StringBuilder();
        sb.Append("--- ").Append(oldLabel).Append('\n');
        sb.Append("+++ ").Append(newLabel).Append('\n');
        foreach (var hunk in hunks)
        {
            sb.Append("@@ -").Append(Range(hunk.OldStart, hunk.OldCount)).Append(" +").Append(Range(hunk.NewStart, hunk.NewCount)).Append(" @@\n");
            foreach (var line in hunk.Lines)
            {
                sb.Append(line).Append('\n');
            }
        }

        return sb.ToString();
    }

    /// <summary>The added and deleted line counts over <paramref name="hunks"/> (the markers not counted).</summary>
    public static (int Added, int Deleted) Count(IReadOnlyList<Hunk> hunks)
    {
        ArgumentNullException.ThrowIfNull(hunks);
        int added = 0, deleted = 0;
        foreach (var hunk in hunks)
        {
            foreach (var line in hunk.Lines)
            {
                if (line.Length > 0 && line[0] == '+')
                {
                    added++;
                }
                else if (line.Length > 0 && line[0] == '-')
                {
                    deleted++;
                }
            }
        }

        return (added, deleted);
    }

    /// <summary>difflib's <c>_format_range_unified</c>: <c>start</c> alone for one line, <c>start,length</c> otherwise (<c>start − 1</c> for an empty range).</summary>
    private static string Range(int start, int length)
    {
        if (length == 1)
        {
            return start.ToString(CultureInfo.InvariantCulture);
        }

        int beginning = length == 0 ? start - 1 : start;
        return beginning.ToString(CultureInfo.InvariantCulture) + "," + length.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The 1-based start of a range; difflib prints <c>start</c> as <c>i1 + 1</c> and the empty-range fix-up happens in <see cref="Range"/>.</summary>
    private static int Start(int from, int to) => from + 1;

    /// <summary>Each distinct line as one character, the last line of a side without a trailing newline distinct from the same text with one (git's rule).</summary>
    private static bool TryIntern(Text a, Text b, out string sa, out string sb)
    {
        var keys = new Dictionary<string, char>(StringComparer.Ordinal);
        sa = sb = "";
        return TryIntern(a, keys, out sa) && TryIntern(b, keys, out sb);
    }

    private static bool TryIntern(Text text, Dictionary<string, char> keys, out string interned)
    {
        var sb = new StringBuilder(text.Lines.Count);
        for (int i = 0; i < text.Lines.Count; i++)
        {
            string key = i == text.Lines.Count - 1 && !text.EndsWithNewline ? text.Lines[i] + "\0" : text.Lines[i];
            if (!keys.TryGetValue(key, out char c))
            {
                if (keys.Count >= MaxDistinctLines)
                {
                    interned = "";
                    return false;
                }

                c = (char)keys.Count;
                keys[key] = c;
            }

            sb.Append(c);
        }

        interned = sb.ToString();
        return true;
    }

    /// <summary>difflib's <c>get_grouped_opcodes(n)</c>: the opcodes cut into groups around each run of changes, <paramref name="context"/> equal lines kept on either side.</summary>
    private static List<List<SequenceSimilarity.Opcode>> Group(IReadOnlyList<SequenceSimilarity.Opcode> opcodes, int context)
    {
        var codes = opcodes.ToList();
        if (codes[0].Tag == SequenceSimilarity.Op.Equal)
        {
            var c = codes[0];
            codes[0] = c with { I1 = Math.Max(c.I1, c.I2 - context), J1 = Math.Max(c.J1, c.J2 - context) };
        }

        if (codes[^1].Tag == SequenceSimilarity.Op.Equal)
        {
            var c = codes[^1];
            codes[^1] = c with { I2 = Math.Min(c.I2, c.I1 + context), J2 = Math.Min(c.J2, c.J1 + context) };
        }

        int nn = context + context;
        var groups = new List<List<SequenceSimilarity.Opcode>>();
        var group = new List<SequenceSimilarity.Opcode>();
        foreach (var code in codes)
        {
            var current = code;
            if (current.Tag == SequenceSimilarity.Op.Equal && current.I2 - current.I1 > nn)
            {
                group.Add(current with { I2 = Math.Min(current.I2, current.I1 + context), J2 = Math.Min(current.J2, current.J1 + context) });
                groups.Add(group);
                group = [];
                current = current with { I1 = Math.Max(current.I1, current.I2 - context), J1 = Math.Max(current.J1, current.J2 - context) };
            }

            group.Add(current);
        }

        if (group.Count > 0 && !(group.Count == 1 && group[0].Tag == SequenceSimilarity.Op.Equal))
        {
            groups.Add(group);
        }

        return groups;
    }
}
