using System.Text;
using System.Text.RegularExpressions;

namespace NeonSidekick.Plans;

/// <summary>
/// One step of a plan as the Ralph loop works it (2026-10-18, <see cref="PlanSteps"/>): a task line under <c>## Steps</c>, its place in the text
/// (<paramref name="Line"/>, 0-based over the text parsed, and its <paramref name="Indent"/> in characters), its words after the box, whether
/// it is ticked, its indented <c>Done when:</c> line, the paths and <c>ref-N</c> pictures it names, and the task lines indented under it
/// (<paramref name="Children"/>, one level). <paramref name="Index"/> is its number among the leaves, 1-based (a parent's is 0: a parent is
/// never a step the loop works; it is ticked once its children all stand). <paramref name="Occurrence"/> (2026-10-19, the review: two steps of the
/// same words were told apart by nothing) is its place among the task lines with its words, 0-based in the order of the text, and
/// <paramref name="Twins"/> how many there are, 1 for words used once.
/// </summary>
public sealed record PlanStep(int Index, int Line, int Indent, string Text, bool Ticked, string? DoneWhen, IReadOnlyList<string> NamedPaths, IReadOnlyList<string> NamedRefs, IReadOnlyList<PlanStep> Children, int Occurrence = 0, int Twins = 1)
{
    /// <summary>A step with no children: the kind the loop works.</summary>
    public bool IsLeaf => Children.Count == 0;
}

/// <summary>A sub-step a split writes (<see cref="PlanSteps.WithChildren"/>): its words and its <c>Done when:</c>.</summary>
public sealed record PlanSubstep(string Text, string DoneWhen);

/// <summary>
/// The steps of a plan file (2026-10-18, the Ralph loop's ground: one step per pass, the first unticked in order). <see cref="Parse"/> reads the
/// task lines under <c>## Steps</c> (to the next heading of level 1 or 2; a <c>###</c> inside groups steps and is passed over), else every
/// top-level task line of the text; a task line indented under a step is its child (one level: deeper ones are children too); an indented
/// <c>Done when:</c> line, a bullet or not, joins the step above it. Fenced code is never read (<see cref="PlanDocument.Walk"/>), and a
/// <c>Done when:</c> line is never a box, so <see cref="PlanDocument.Progress"/> is unchanged. <see cref="WithBox"/> and
/// <see cref="WithChildren"/> rewrite the text in place, keeping each line's own ending (CRLF or LF, a mixed file's per line). Pure.
/// A step's <see cref="PlanStep.Line"/> counts over the text it was parsed from: a step that will be written back is parsed from the plan
/// file whole (header and all), never from <see cref="PlanDocument.Body"/>, whose lines are not the file's (2026-10-19, the review).
/// </summary>
public static class PlanSteps
{
    /// <summary>The heading the steps live under, case aside.</summary>
    public const string StepsHeading = "Steps";

    /// <summary>The label of a step's check line, case aside.</summary>
    public const string DoneWhenLabel = "Done when:";

    private static readonly Regex s_refs = new(@"\bref-(\d+)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex s_backticks = new("`([^`\\s]+)`", RegexOptions.CultureInvariant);
    private static readonly Regex s_barePath = new(@"(?<![\w/.-])((?:\.{1,2}/)?[\w.-]+(?:/[\w.-]+)+)", RegexOptions.CultureInvariant);

    /// <summary>The steps of <paramref name="text"/> (a plan file whole, or its body), in order.</summary>
    public static IReadOnlyList<PlanStep> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = PlanDocument.Walk(text).ToList();
        bool headed = lines.Any(l => IsStepsHeading(l.Text));
        bool inSteps = !headed;
        var tops = new List<Builder>();
        Builder? lastTask = null;
        int topIndent = -1;
        foreach (var (index, raw) in lines)
        {
            string line = raw.TrimEnd('\r');
            string trimmed = line.TrimStart();
            if (HeadingLevel(trimmed) is int level and > 0)
            {
                if (headed && level <= 2)
                {
                    inSteps = IsStepsHeading(line);
                    lastTask = null;
                }

                continue;
            }

            if (!inSteps)
            {
                continue;
            }

            int indent = line.Length - trimmed.Length;
            if (TaskBox(trimmed) is { } task)
            {
                if (topIndent < 0 || indent <= topIndent)
                {
                    topIndent = topIndent < 0 ? indent : topIndent;
                    var top = new Builder(index, indent, task.Text, task.Ticked);
                    tops.Add(top);
                    lastTask = top;
                }
                else if (tops.Count > 0)
                {
                    var child = new Builder(index, indent, task.Text, task.Ticked);
                    tops[^1].Children.Add(child);
                    lastTask = child;
                }

                continue;
            }

            if (lastTask is not null && indent > lastTask.Indent && DoneWhenOf(trimmed) is { } doneWhen)
            {
                lastTask.DoneWhen = lastTask.DoneWhen is null ? doneWhen : lastTask.DoneWhen + " " + doneWhen;
            }
        }

        // Each task line's place among those with its words, in the order of the text (a parent before its children).
        var order = tops.SelectMany(t => t.Children.Prepend(t)).ToList();
        var twins = order.GroupBy(b => b.Text, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var builder in order)
        {
            builder.Occurrence = seen.GetValueOrDefault(builder.Text);
            builder.Twins = twins[builder.Text];
            seen[builder.Text] = builder.Occurrence + 1;
        }

        int leaf = 0;
        var steps = new List<PlanStep>(tops.Count);
        foreach (var top in tops)
        {
            var children = top.Children.Select(c => c.Build(++leaf, [])).ToList();
            steps.Add(top.Build(children.Count == 0 ? ++leaf : 0, children));
        }

        return steps;
    }

    /// <summary>The leaves in the order the loop works them: each top-level step's children, else the step itself.</summary>
    public static IEnumerable<PlanStep> Leaves(IReadOnlyList<PlanStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        foreach (var step in steps)
        {
            if (step.IsLeaf)
            {
                yield return step;
            }
            else
            {
                foreach (var child in step.Children)
                {
                    yield return child;
                }
            }
        }
    }

    /// <summary>The first unticked leaf (a child before its parent), or null when every leaf is ticked.</summary>
    public static PlanStep? FirstUnticked(IReadOnlyList<PlanStep> steps) => Leaves(steps).FirstOrDefault(s => !s.Ticked);

    /// <summary>The ticked leaves and all of them: the Ralph loop's <c>progress:</c>.</summary>
    public static (int Done, int Total) Counts(IReadOnlyList<PlanStep> steps)
    {
        var leaves = Leaves(steps).ToList();
        return (leaves.Count(s => s.Ticked), leaves.Count);
    }

    /// <summary>The top-level step holding <paramref name="child"/>, or null when it is one itself.</summary>
    public static PlanStep? ParentOf(IReadOnlyList<PlanStep> steps, PlanStep child)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(child);
        return steps.FirstOrDefault(s => s.Children.Any(c => c.Line == child.Line));
    }

    /// <summary>
    /// The step at <paramref name="step"/>'s place in <paramref name="text"/> again (the file may have moved under a pass): the task on its line
    /// with its words (for words used more than once, the same one of them), else the one task anywhere with its words, else the one at its
    /// <see cref="PlanStep.Occurrence"/> while there are as many as there were; null when it cannot be told.
    /// </summary>
    public static PlanStep? Find(string text, PlanStep step)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(step);
        var all = Parse(text).SelectMany(s => s.Children.Prepend(s)).ToList();
        // A twin on its line must be the same twin: after a move the other one may stand on that line.
        if (all.FirstOrDefault(s => s.Line == step.Line && s.Text == step.Text && (step.Twins <= 1 || (s.Occurrence == step.Occurrence && s.Twins == step.Twins))) is { } there)
        {
            return there;
        }

        var same = all.Where(s => s.Text == step.Text).ToList();
        return same.Count == 1 ? same[0]
            : same.Count > 1 && same.Count == step.Twins && step.Occurrence < same.Count ? same[step.Occurrence]
            : null;
    }

    /// <summary>
    /// <paramref name="text"/> with <paramref name="step"/>'s box set to <paramref name="ticked"/> (<c>[x]</c> or <c>[ ]</c>), every other byte as it
    /// was; null when the step is not where it was (<see cref="Find"/>).
    /// </summary>
    public static string? WithBox(string text, PlanStep step, bool ticked)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Find(text, step) is not { } found)
        {
            return null;
        }

        var lines = SplitKeepingEndings(text);
        var (content, ending) = lines[found.Line];
        string trimmed = content.TrimStart();
        int marker = PlanDocument.ListMarker(trimmed);
        int box = content.Length - trimmed.Length + marker + 1;
        lines[found.Line] = (string.Concat(content.AsSpan(0, box), ticked ? "x" : " ", content.AsSpan(box + 1)), ending);
        return Join(lines);
    }

    /// <summary>
    /// <paramref name="text"/> with <paramref name="substeps"/> written under <paramref name="step"/> as indented checkboxes, each with its indented
    /// <c>Done when:</c> line, after the step's own lines (its <c>Done when:</c> and notes); every line written with the step line's ending. Null
    /// when the step is not where it was, or has children already.
    /// </summary>
    public static string? WithChildren(string text, PlanStep step, IReadOnlyList<PlanSubstep> substeps)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(substeps);
        if (Find(text, step) is not { IsLeaf: true } found)
        {
            return null;
        }

        var lines = SplitKeepingEndings(text);
        string ending = lines[found.Line].Ending is { Length: > 0 } own ? own : lines.FirstOrDefault(l => l.Ending.Length > 0).Ending ?? "\n";
        if (ending.Length == 0)
        {
            ending = "\n";
        }

        int last = found.Line;
        for (int i = found.Line + 1; i < lines.Count; i++)
        {
            string content = lines[i].Content.TrimEnd('\r');
            string trimmed = content.TrimStart();
            if (trimmed.Length == 0)
            {
                // A blank line ends the block unless an indented line of it follows.
                continue;
            }

            if (content.Length - trimmed.Length <= found.Indent || HeadingLevel(trimmed) > 0)
            {
                break;
            }

            last = i;
        }

        if (lines[last].Ending.Length == 0)
        {
            lines[last] = (lines[last].Content, ending);
        }

        string pad = new(' ', found.Indent + 2);
        var added = new List<(string, string)>(substeps.Count * 2);
        foreach (var sub in substeps)
        {
            added.Add((pad + "- [ ] " + Flat(sub.Text), ending));
            added.Add((pad + "  " + DoneWhenLabel + " " + Flat(sub.DoneWhen), ending));
        }

        lines.InsertRange(last + 1, added);
        return Join(lines);
    }

    /// <summary>Every task line of <paramref name="steps"/>, a parent before its children: the order of the text.</summary>
    public static IReadOnlyList<PlanStep> Flat(IReadOnlyList<PlanStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        return steps.SelectMany(s => s.Children.Prepend(s)).ToList();
    }

    /// <summary>
    /// Each of <paramref name="after"/>'s task lines paired with the one of <paramref name="before"/> it was, for telling which boxes a pass changed
    /// (2026-10-19, the second review of the Ralph loop: boxes matched by their words let a pass that reworded, added or deleted a line escape the
    /// put-back, and a reworded step could not be unticked). The longest run of lines with the same words in the same order is kept; between two
    /// kept lines (and before the first, after the last) a stretch with as many lines on both sides is paired in order — lines reworded in place;
    /// -1 for a line that is new. Both lists are <see cref="Flat"/>, in the order of the text. Pure.
    /// </summary>
    public static int[] Align(IReadOnlyList<PlanStep> before, IReadOnlyList<PlanStep> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        int n = before.Count;
        int m = after.Count;
        // The longest common run by words, from the ends (lcs[i, j]: of before[i..] and after[j..]).
        var lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
        {
            for (int j = m - 1; j >= 0; j--)
            {
                lcs[i, j] = string.Equals(before[i].Text, after[j].Text, StringComparison.Ordinal) ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var pairs = new int[m];
        Array.Fill(pairs, -1);
        int b = 0;
        int a = 0;
        int gapB = 0;
        int gapA = 0;

        void CloseGap(int endB, int endA)
        {
            if (endB - gapB == endA - gapA)
            {
                for (int k = 0; k < endA - gapA; k++)
                {
                    pairs[gapA + k] = gapB + k;
                }
            }
        }

        while (b < n && a < m)
        {
            if (string.Equals(before[b].Text, after[a].Text, StringComparison.Ordinal))
            {
                CloseGap(b, a);
                pairs[a] = b;
                b++;
                a++;
                gapB = b;
                gapA = a;
            }
            else if (lcs[b + 1, a] >= lcs[b, a + 1])
            {
                b++;
            }
            else
            {
                a++;
            }
        }

        CloseGap(n, m);
        return pairs;
    }

    private sealed class Builder(int line, int indent, string text, bool ticked)
    {
        public int Line { get; } = line;
        public int Indent { get; } = indent;
        public string Text { get; } = text;
        public bool Ticked { get; } = ticked;
        public string? DoneWhen { get; set; }
        public int Occurrence { get; set; }
        public int Twins { get; set; } = 1;
        public List<Builder> Children { get; } = [];

        public PlanStep Build(int index, IReadOnlyList<PlanStep> children)
        {
            string words = Text + (DoneWhen is { } d ? " " + d : "");
            return new PlanStep(index, Line, Indent, Text, Ticked, DoneWhen, PathsIn(words), RefsIn(words), children, Occurrence, Twins);
        }
    }

    /// <summary>The <c>ref-N</c> names in <paramref name="text"/>, lowercase, in order, once each.</summary>
    public static IReadOnlyList<string> RefsIn(string text) =>
        s_refs.Matches(text).Select(m => "ref-" + m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// The paths <paramref name="text"/> names: a backticked word with a slash or an extension, and a bare word with a slash whose last part has an
    /// extension; once each, in order, as written.
    /// </summary>
    public static IReadOnlyList<string> PathsIn(string text)
    {
        var found = new List<string>();
        foreach (Match m in s_backticks.Matches(text))
        {
            string word = m.Groups[1].Value.TrimEnd(':', ',', ';', ')');
            if (LooksLikePath(word, bare: false))
            {
                found.Add(word);
            }
        }

        string outside = s_backticks.Replace(text, " ");
        foreach (Match m in s_barePath.Matches(outside))
        {
            string word = m.Groups[1].Value.TrimEnd('.', ',', ';', ':', ')');
            if (LooksLikePath(word, bare: true))
            {
                found.Add(word);
            }
        }

        return found.Distinct(StringComparer.Ordinal).ToList();
    }

    private static bool LooksLikePath(string word, bool bare)
    {
        if (word.Length < 3 || word.Contains("://", StringComparison.Ordinal) || word.StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        string last = word.Replace('\\', '/').Split('/')[^1];
        int dot = last.LastIndexOf('.');
        // a.cs, build.yml, and a dot file (.gitignore) too.
        bool extension = dot >= 0 && dot < last.Length - 1 && last[(dot + 1)..].All(char.IsAsciiLetterOrDigit) && last[(dot + 1)..].Any(char.IsAsciiLetter);
        bool slash = word.Contains('/', StringComparison.Ordinal) || word.Contains('\\', StringComparison.Ordinal);
        return bare ? slash && extension : slash || extension;
    }

    private static bool IsStepsHeading(string line)
    {
        string trimmed = line.Trim();
        return HeadingLevel(trimmed) == 2 && string.Equals(trimmed.TrimStart('#').Trim().TrimEnd(':'), StepsHeading, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The level of a Markdown heading (<c>## x</c> is 2), or 0.</summary>
    private static int HeadingLevel(string trimmed)
    {
        int level = 0;
        while (level < trimmed.Length && trimmed[level] == '#')
        {
            level++;
        }

        return level is > 0 and <= 6 && (level == trimmed.Length || trimmed[level] == ' ') ? level : 0;
    }

    /// <summary>A task line's words and box, or null.</summary>
    private static (string Text, bool Ticked)? TaskBox(string trimmed)
    {
        int marker = PlanDocument.ListMarker(trimmed);
        if (marker == 0)
        {
            return null;
        }

        string rest = trimmed[marker..];
        if (rest.Length >= 3 && rest[0] == '[' && rest[2] == ']' && (rest.Length == 3 || char.IsWhiteSpace(rest[3])) && rest[1] is ' ' or 'x' or 'X')
        {
            return (rest[3..].Trim(), rest[1] != ' ');
        }

        return null;
    }

    /// <summary>The words after a <c>Done when:</c> label (a bullet allowed before it, bold or not), or null.</summary>
    private static string? DoneWhenOf(string trimmed)
    {
        int marker = PlanDocument.ListMarker(trimmed);
        string rest = trimmed[marker..].Replace("**", "", StringComparison.Ordinal).Replace("__", "", StringComparison.Ordinal).Replace("*", "", StringComparison.Ordinal).TrimStart();
        return rest.StartsWith(DoneWhenLabel, StringComparison.OrdinalIgnoreCase) && rest[DoneWhenLabel.Length..].Trim() is { Length: > 0 } words ? words : null;
    }

    private static string Flat(string text) => string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>The lines of <paramref name="text"/>, each with its own ending (<c>\r\n</c>, <c>\n</c>, or none on the last).</summary>
    private static List<(string Content, string Ending)> SplitKeepingEndings(string text)
    {
        var lines = new List<(string, string)>();
        int at = 0;
        while (at <= text.Length)
        {
            int end = text.IndexOf('\n', at);
            if (end < 0)
            {
                lines.Add((text[at..], ""));
                break;
            }

            bool crlf = end > at && text[end - 1] == '\r';
            lines.Add((text[at..(crlf ? end - 1 : end)], crlf ? "\r\n" : "\n"));
            at = end + 1;
        }

        return lines;
    }

    private static string Join(List<(string Content, string Ending)> lines)
    {
        var sb = new StringBuilder();
        foreach (var (content, ending) in lines)
        {
            sb.Append(content).Append(ending);
        }

        return sb.ToString();
    }
}
