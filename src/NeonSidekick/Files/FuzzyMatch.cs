using System.Text;

namespace NeonSidekick.Files;

/// <summary>The nine ways <see cref="FuzzyMatch"/> looks for <c>old_text</c>, in the order it tries them: precise first, similarity last.</summary>
public enum MatchStrategy
{
    /// <summary>The text as written, non-overlapping occurrences.</summary>
    Exact,

    /// <summary>Every line compared with its leading and trailing whitespace trimmed.</summary>
    LineTrimmed,

    /// <summary>Runs of spaces and tabs collapsed to one space on both sides.</summary>
    WhitespaceNormalized,

    /// <summary>Every line compared with its indentation ignored.</summary>
    IndentationFlexible,

    /// <summary>Literal <c>\n</c> / <c>\t</c> / <c>\r</c> in the pattern read as the characters they name (a call serialised once too often).</summary>
    EscapeNormalized,

    /// <summary>The first and last lines trimmed, the lines between exact.</summary>
    TrimmedBoundary,

    /// <summary>Typographic quotes, dashes, the ellipsis and the space family read as their ASCII forms on both sides.</summary>
    UnicodeNormalized,

    /// <summary>The first and last lines anchored exactly (trimmed, Unicode-normalised), the lines between scored for similarity. Approximate.</summary>
    BlockAnchor,

    /// <summary>Every line scored for similarity against its counterpart, the first and last included. Approximate; the last resort.</summary>
    ContextAware,
}

/// <summary>A half-open character range <c>[Start, End)</c> in the text it was found in.</summary>
public readonly record struct TextSpan(int Start, int End)
{
    public int Length => End - Start;
}

/// <summary>Where a match sits: its first line's number and that line's text, trimmed (the refusal quotes it).</summary>
public readonly record struct MatchLocation(int Line, string Text);

/// <summary>A serialisation artefact the arguments carry and the matched text does not (<see cref="FuzzyMatch.DetectEscapeDrift"/>).</summary>
public enum EscapeDrift
{
    None,

    /// <summary><c>\'</c> in the arguments, not in the file.</summary>
    QuoteSingle,

    /// <summary><c>\"</c> in the arguments, not in the file.</summary>
    QuoteDouble,

    /// <summary>Every backslash run in <c>old_text</c> twice as long as the file's.</summary>
    DoubledBackslashes,
}

public enum MatchOutcome
{
    Replaced,

    /// <summary>Nothing matched, but <c>new_text</c> is already in the file and <c>old_text</c> is not: the edit was applied before.</summary>
    AlreadyApplied,

    /// <summary><c>old_text</c> empty or whitespace only.</summary>
    Empty,

    /// <summary><c>old_text</c> and <c>new_text</c> the same.</summary>
    Same,
    NotFound,

    /// <summary>Several matches without <c>replace_all</c>.</summary>
    Ambiguous,

    /// <summary>Several matches under <c>replace_all</c>, but by an approximate strategy.</summary>
    ApproximateAll,
    EscapeDrift,

    /// <summary>The edited text would pass <see cref="FuzzyMatch.MaxResultChars"/> (2026-10-22, review V2), nothing built.</summary>
    TooLarge,
}

/// <summary>
/// What <see cref="FuzzyMatch.Replace"/> did. <paramref name="Content"/> is the edited text (the input
/// when nothing changed); <paramref name="Strategy"/> the one that matched (<see cref="MatchStrategy.Exact"/>
/// when none did); <paramref name="Spans"/> the matches in the INPUT text, ascending; <paramref name="Placed"/>
/// where each replacement sits in <paramref name="Content"/>, one per span; <paramref name="Locations"/> the
/// first few matches for an <see cref="MatchOutcome.Ambiguous"/> or <see cref="MatchOutcome.ApproximateAll"/> refusal.
/// </summary>
public sealed record FuzzyResult(
    MatchOutcome Outcome,
    string Content,
    MatchStrategy Strategy,
    IReadOnlyList<TextSpan> Spans,
    IReadOnlyList<TextSpan> Placed,
    IReadOnlyList<MatchLocation> Locations,
    EscapeDrift Drift = EscapeDrift.None)
{
    public int Count => Spans.Count;
}

/// <summary>
/// The <c>old_text</c> matcher behind <c>patch_file</c> (2026-09-19): a port of Hermes Agent's
/// <c>fuzzy_match.py</c>, an ordered chain of nine strategies — the first seven deterministic
/// transforms of exact matching, the last two approximate — and the orchestrator that applies the
/// first one to yield a usable match. Works over the LF-normalised text <c>WorkingDirectory.LoadForEdit</c>
/// hands it; every span is in that text. Pure, and regex-free on purpose: the three regexes Hermes
/// uses are each a loop here, so nothing is built at run time under AOT and nothing needs a
/// <c>[GeneratedRegex]</c>.
/// </summary>
public static class FuzzyMatch
{
    /// <summary>Locations a refusal names before <c>… and N more</c>.</summary>
    public const int MaxLocations = 5;

    /// <summary>The shortest <c>new_text</c> (trimmed) whose presence counts as the edit already applied.</summary>
    public const int MinAppliedChars = 8;

    /// <summary><see cref="MatchStrategy.BlockAnchor"/>'s middle similarity with exactly one candidate window.</summary>
    public const double LoneAnchorThreshold = 0.50;

    /// <summary><see cref="MatchStrategy.BlockAnchor"/>'s middle similarity with several candidates.</summary>
    public const double ManyAnchorThreshold = 0.70;

    /// <summary><see cref="MatchStrategy.ContextAware"/>'s per-line similarity.</summary>
    public const double LineThreshold = 0.80;

    /// <summary>The nine in the order tried; earlier is stricter.</summary>
    public static readonly IReadOnlyList<MatchStrategy> Chain =
    [
        MatchStrategy.Exact,
        MatchStrategy.LineTrimmed,
        MatchStrategy.WhitespaceNormalized,
        MatchStrategy.IndentationFlexible,
        MatchStrategy.EscapeNormalized,
        MatchStrategy.TrimmedBoundary,
        MatchStrategy.UnicodeNormalized,
        MatchStrategy.BlockAnchor,
        MatchStrategy.ContextAware,
    ];

    /// <summary>The two strategies that approximate: fine for one replacement, never for <c>replace_all</c>.</summary>
    public static bool IsSimilarity(MatchStrategy strategy) => strategy is MatchStrategy.BlockAnchor or MatchStrategy.ContextAware;

    /// <summary>
    /// The orchestrator: the pre-checks, then the chain; the first strategy with a match decides —
    /// several without <paramref name="replaceAll"/> is <see cref="MatchOutcome.Ambiguous"/>, several under
    /// it by an approximate strategy <see cref="MatchOutcome.ApproximateAll"/>, a non-exact match whose
    /// arguments carry escapes the file does not <see cref="MatchOutcome.EscapeDrift"/>; else every span is
    /// replaced by <paramref name="newText"/> shaped to the matched region (unescaped under
    /// <see cref="MatchStrategy.EscapeNormalized"/>, the file's typographic characters kept under
    /// <see cref="MatchStrategy.UnicodeNormalized"/>, re-indented to the file under every non-exact
    /// strategy). Nothing found: <see cref="MatchOutcome.AlreadyApplied"/> when the file holds
    /// <paramref name="newText"/> and not <paramref name="oldText"/>, else <see cref="MatchOutcome.NotFound"/>.
    /// </summary>
    public static FuzzyResult Replace(string content, string oldText, string newText, bool replaceAll)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(oldText);
        ArgumentNullException.ThrowIfNull(newText);
        if (oldText.Trim().Length == 0)
        {
            return Unchanged(MatchOutcome.Empty, content);
        }

        if (string.Equals(oldText, newText, StringComparison.Ordinal))
        {
            return Unchanged(MatchOutcome.Same, content);
        }

        // A retry of an edit whose new text holds the old (2026-10-22, review G10: the old text still matched inside the new one, so a
        // re-sent "foo" → "foo bar" made "foo bar bar"): done already when every place the old text stands is inside the new text.
        if (IsAppliedAround(content, oldText, newText))
        {
            return Unchanged(MatchOutcome.AlreadyApplied, content);
        }

        foreach (var strategy in Chain)
        {
            var spans = Find(strategy, content, oldText);
            if (spans.Count == 0)
            {
                continue;
            }

            if (spans.Count > 1 && !replaceAll)
            {
                return new FuzzyResult(MatchOutcome.Ambiguous, content, strategy, spans, [], Locate(content, spans));
            }

            if (spans.Count > 1 && IsSimilarity(strategy))
            {
                return new FuzzyResult(MatchOutcome.ApproximateAll, content, strategy, spans, [], Locate(content, spans));
            }

            if (strategy != MatchStrategy.Exact)
            {
                var drift = DetectEscapeDrift(string.Join("\n", spans.Select(s => content[s.Start..s.End])), oldText, newText);
                if (drift != EscapeDrift.None)
                {
                    return new FuzzyResult(MatchOutcome.EscapeDrift, content, strategy, spans, [], [], drift);
                }
            }

            // The result's size before it is built (2026-10-22, review V2: replace_all of 40,000 matches with a 50,000-character new_text
            // grew a StringBuilder past 2 GB). The shaped text can differ from new_text by its indentation, so the bound is new_text's length.
            long projected = content.Length;
            foreach (var span in spans)
            {
                projected += newText.Length - span.Length;
            }

            if (projected > MaxResultChars)
            {
                return new FuzzyResult(MatchOutcome.TooLarge, content, strategy, spans, [], []);
            }

            var sb = new StringBuilder((int)Math.Max(projected, 0));
            var placed = new List<TextSpan>(spans.Count);
            int copied = 0;
            foreach (var span in spans)
            {
                string region = content[span.Start..span.End];
                string replacement = Shape(strategy, region, oldText, newText);
                sb.Append(content, copied, span.Start - copied);
                placed.Add(new TextSpan(sb.Length, sb.Length + replacement.Length));
                sb.Append(replacement);
                copied = span.End;
            }

            sb.Append(content, copied, content.Length - copied);
            return new FuzzyResult(MatchOutcome.Replaced, sb.ToString(), strategy, spans, placed, []);
        }

        return Unchanged(IsAlreadyApplied(content, oldText, newText) ? MatchOutcome.AlreadyApplied : MatchOutcome.NotFound, content);
    }

    /// <summary>The most characters an edited text may hold: what <c>read_file</c> can still open (review V2).</summary>
    public const long MaxResultChars = WorkingDirectory.MaxReadFileBytes;

    private static FuzzyResult Unchanged(MatchOutcome outcome, string content) => new(outcome, content, MatchStrategy.Exact, [], [], []);

    /// <summary>The replacement as one strategy lands it in the matched region.</summary>
    private static string Shape(MatchStrategy strategy, string region, string oldText, string newText)
    {
        string shaped = strategy switch
        {
            MatchStrategy.Exact => newText,
            MatchStrategy.EscapeNormalized => WorkingDirectory.NormalizeNewlines(Unescape(newText)),
            MatchStrategy.UnicodeNormalized => PreserveUnicode(region, oldText, newText),
            _ => newText,
        };
        return strategy == MatchStrategy.Exact ? shaped : Reindent(region, oldText, shaped);
    }

    /// <summary>One strategy's matches in <paramref name="content"/>, ascending and non-overlapping; empty when it does not apply.</summary>
    public static IReadOnlyList<TextSpan> Find(MatchStrategy strategy, string content, string pattern)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(pattern);
        if (pattern.Length == 0)
        {
            return [];
        }

        return strategy switch
        {
            MatchStrategy.Exact => Exact(content, pattern),
            MatchStrategy.LineTrimmed => MatchLines(content, pattern, (line, _) => line.Trim()),
            MatchStrategy.WhitespaceNormalized => WhitespaceNormalized(content, pattern),
            MatchStrategy.IndentationFlexible => MatchLines(content, pattern, (line, _) => line.TrimStart()),
            MatchStrategy.EscapeNormalized => EscapeNormalized(content, pattern),
            MatchStrategy.TrimmedBoundary => MatchLines(content, pattern, (line, boundary) => boundary ? line.Trim() : line),
            MatchStrategy.UnicodeNormalized => UnicodeNormalized(content, pattern),
            MatchStrategy.BlockAnchor => BlockAnchor(content, pattern),
            MatchStrategy.ContextAware => ContextAware(content, pattern),
            _ => throw new ArgumentOutOfRangeException(nameof(strategy)),
        };
    }

    // ---- 1. exact ----

    private static List<TextSpan> Exact(string content, string pattern)
    {
        var spans = new List<TextSpan>();
        for (int at = content.IndexOf(pattern, StringComparison.Ordinal); at >= 0; at = content.IndexOf(pattern, at + pattern.Length, StringComparison.Ordinal))
        {
            spans.Add(new TextSpan(at, at + pattern.Length));
        }

        return spans;
    }

    // ---- 2, 4, 6. the line-window strategies ----

    /// <summary>
    /// The shared line-block matcher: both texts split on <c>\n</c>, an n-line window slid over the
    /// content, each line compared through <paramref name="transform"/> (given whether it is the
    /// window's first or last line). A window that matches is skipped past, so two matches never
    /// overlap. A trailing newline on the pattern is dropped for the comparison and taken back into the span.
    /// </summary>
    private static List<TextSpan> MatchLines(string content, string pattern, Func<string, bool, string> transform)
    {
        bool trailing = pattern.EndsWith('\n');
        string[] want = (trailing ? pattern[..^1] : pattern).Split('\n');
        string[] lines = content.Split('\n');
        int n = want.Length;
        var spans = new List<TextSpan>();
        if (n > lines.Length)
        {
            return spans;
        }

        var wanted = new string[n];
        for (int k = 0; k < n; k++)
        {
            wanted[k] = transform(want[k], k == 0 || k == n - 1);
        }

        int[] starts = LineStarts(lines);
        for (int i = 0; i + n <= lines.Length; i++)
        {
            bool all = true;
            for (int k = 0; k < n && all; k++)
            {
                all = string.Equals(transform(lines[i + k], k == 0 || k == n - 1), wanted[k], StringComparison.Ordinal);
            }

            if (!all)
            {
                continue;
            }

            spans.Add(WindowSpan(content, starts, lines, i, n, trailing));
            i += n - 1;
        }

        return spans;
    }

    /// <summary>The offset where each line of <paramref name="lines"/> (split on <c>\n</c>) starts.</summary>
    private static int[] LineStarts(string[] lines)
    {
        int[] starts = new int[lines.Length];
        int at = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            starts[i] = at;
            at += lines[i].Length + 1;
        }

        return starts;
    }

    /// <summary>The span of lines <paramref name="first"/> .. <paramref name="first"/> + <paramref name="count"/> − 1, the newline after the last one included when <paramref name="withNewline"/> and there is one.</summary>
    private static TextSpan WindowSpan(string content, int[] starts, string[] lines, int first, int count, bool withNewline)
    {
        int last = first + count - 1;
        int end = starts[last] + lines[last].Length;
        if (withNewline && end < content.Length && content[end] == '\n')
        {
            end++;
        }

        return new TextSpan(starts[first], Math.Min(end, content.Length));
    }

    // ---- 3. whitespace normalised ----

    /// <summary>Runs of spaces and tabs collapsed to one space; <paramref name="map"/>[i] is the original offset the collapsed character i came from (a run's first), with one more entry for the end.</summary>
    public static string CollapseWhitespace(string text, out int[] map)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sb = new StringBuilder(text.Length);
        var positions = new List<int>(text.Length + 1);
        bool inRun = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is ' ' or '\t')
            {
                if (inRun)
                {
                    continue;
                }

                inRun = true;
                sb.Append(' ');
                positions.Add(i);
                continue;
            }

            inRun = false;
            sb.Append(c);
            positions.Add(i);
        }

        positions.Add(text.Length);
        map = [.. positions];
        return sb.ToString();
    }

    private static List<TextSpan> WhitespaceNormalized(string content, string pattern)
    {
        string normalized = CollapseWhitespace(content, out int[] map);
        string wanted = CollapseWhitespace(pattern, out _);
        if (string.Equals(normalized, content, StringComparison.Ordinal) && string.Equals(wanted, pattern, StringComparison.Ordinal))
        {
            return [];
        }

        var spans = new List<TextSpan>();
        foreach (var span in Exact(normalized, wanted))
        {
            int start = map[span.Start];
            int end = map[span.End - 1] + 1;
            // The rest of a collapsed run is taken only when the match itself ended in a space.
            if (normalized[span.End - 1] == ' ')
            {
                while (end < content.Length && content[end] is ' ' or '\t')
                {
                    end++;
                }
            }

            spans.Add(new TextSpan(start, end));
        }

        return spans;
    }

    // ---- 5. escapes ----

    /// <summary>Literal <c>\n</c>, <c>\t</c> and <c>\r</c> as the characters they name; a doubled backslash stays as written, so <c>\\n</c> is a backslash and an n.</summary>
    public static string Unescape(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!text.Contains('\\'))
        {
            return text;
        }

        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < text.Length)
            {
                char next = text[i + 1];
                switch (next)
                {
                    case 'n':
                        sb.Append('\n');
                        i++;
                        continue;
                    case 't':
                        sb.Append('\t');
                        i++;
                        continue;
                    case 'r':
                        sb.Append('\r');
                        i++;
                        continue;
                    case '\\':
                        sb.Append('\\').Append('\\');
                        i++;
                        continue;
                }
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>Whether <paramref name="text"/> holds a literal <c>\n</c>, <c>\t</c> or <c>\r</c> that <see cref="Unescape"/> would change.</summary>
    public static bool HasEscapes(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return !string.Equals(Unescape(text), text, StringComparison.Ordinal);
    }

    private static List<TextSpan> EscapeNormalized(string content, string pattern)
    {
        if (!HasEscapes(pattern))
        {
            return [];
        }

        return Exact(content, WorkingDirectory.NormalizeNewlines(Unescape(pattern)));
    }

    // ---- 7. unicode normalised ----

    /// <summary>A typographic character's plain form, or null for one that stays.</summary>
    private static string? Plain(char c) => c switch
    {
        '‘' or '’' or '‚' or '‛' or '′' => "'",
        '“' or '”' or '„' or '‟' or '″' => "\"",
        '‐' or '‑' or '‒' or '–' or '−' => "-",
        '—' or '―' => "--",
        '…' => "...",
        ' ' or ' ' or ' ' or ' ' or '　' or (>= ' ' and <= ' ') => " ",
        _ => null,
    };

    /// <summary>Smart quotes, dashes, the ellipsis and the space family as their ASCII forms; everything else as written.</summary>
    public static string UnicodeNormalize(string text) => UnicodeNormalize(text, out _);

    /// <summary>
    /// <see cref="UnicodeNormalize(string)"/> with the map back: <paramref name="originalToNormalized"/>[i]
    /// is where original character i starts in the result, its last entry the result's length (an
    /// em dash spans two entries' worth of <c>-</c>, so the map is monotonic and never one-to-one there).
    /// </summary>
    public static string UnicodeNormalize(string text, out int[] originalToNormalized)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sb = new StringBuilder(text.Length);
        var map = new int[text.Length + 1];
        for (int i = 0; i < text.Length; i++)
        {
            map[i] = sb.Length;
            string? plain = Plain(text[i]);
            if (plain is null)
            {
                sb.Append(text[i]);
            }
            else
            {
                sb.Append(plain);
            }
        }

        map[text.Length] = sb.Length;
        originalToNormalized = map;
        return sb.ToString();
    }

    /// <summary>
    /// A span in the normalised text back onto the original: the character owning each end (a match
    /// that starts or ends inside an expansion — after the first <c>-</c> of an em dash — takes the
    /// whole character). An empty span stays empty.
    /// </summary>
    public static TextSpan MapUnicodeSpan(int[] originalToNormalized, TextSpan normalized)
    {
        ArgumentNullException.ThrowIfNull(originalToNormalized);
        if (normalized.End <= normalized.Start)
        {
            int at = Owner(originalToNormalized, normalized.Start);
            return new TextSpan(at, at);
        }

        return new TextSpan(Owner(originalToNormalized, normalized.Start), Owner(originalToNormalized, normalized.End - 1) + 1);
    }

    /// <summary>The original index whose normalised run holds <paramref name="position"/>: the largest with a start at or before it (the map is strictly increasing, every character yields at least one).</summary>
    private static int Owner(int[] map, int position)
    {
        int lo = 0, hi = map.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (map[mid] <= position)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return lo;
    }

    private static List<TextSpan> UnicodeNormalized(string content, string pattern)
    {
        string normalized = UnicodeNormalize(content, out int[] map);
        string wanted = UnicodeNormalize(pattern);
        if (string.Equals(normalized, content, StringComparison.Ordinal) && string.Equals(wanted, pattern, StringComparison.Ordinal))
        {
            return [];
        }

        var found = Exact(normalized, wanted);
        if (found.Count == 0)
        {
            found = MatchLines(normalized, wanted, (line, _) => line.Trim());
        }

        var spans = new List<TextSpan>(found.Count);
        foreach (var span in found)
        {
            var mapped = MapUnicodeSpan(map, span);
            // Two matches inside one expanded character (each `-` of an em dash) are the one original character.
            if (mapped.End > mapped.Start && (spans.Count == 0 || mapped.Start >= spans[^1].End))
            {
                spans.Add(mapped);
            }
        }

        return spans;
    }

    // ---- 8. block anchor ----

    private static List<TextSpan> BlockAnchor(string content, string pattern)
    {
        bool trailing = pattern.EndsWith('\n');
        string[] want = (trailing ? pattern[..^1] : pattern).Split('\n');
        int n = want.Length;
        string[] lines = content.Split('\n');
        if (n < 2 || n > lines.Length)
        {
            return [];
        }

        string first = Anchor(want[0]);
        string last = Anchor(want[n - 1]);
        // Each line's anchor once, and the candidates capped (2026-10-22, review V7: a 30-line patch on a 40,000-line file scored every
        // "}" … "}" pair with a full ratio, seconds to minutes).
        string[] anchors = lines.Select(Anchor).ToArray();
        var candidates = new List<int>();
        for (int i = 0; i + n <= lines.Length && candidates.Count <= MaxAnchorCandidates; i++)
        {
            if (string.Equals(anchors[i], first, StringComparison.Ordinal) && string.Equals(anchors[i + n - 1], last, StringComparison.Ordinal))
            {
                candidates.Add(i);
            }
        }

        if (candidates.Count > MaxAnchorCandidates)
        {
            return [];
        }

        if (candidates.Count == 0)
        {
            return [];
        }

        double threshold = candidates.Count == 1 ? LoneAnchorThreshold : ManyAnchorThreshold;
        string middle = n > 2 ? string.Join('\n', want, 1, n - 2) : "";
        int[] starts = LineStarts(lines);
        var spans = new List<TextSpan>();
        int skipPast = -1;
        foreach (int i in candidates)
        {
            if (i < skipPast)
            {
                continue;
            }

            string between = n > 2 ? string.Join('\n', lines, i + 1, n - 2) : "";
            double score = n <= 2 ? 1.0 : CanReach(between, middle, threshold) ? SequenceSimilarity.Ratio(between, middle) : 0;
            if (score >= threshold)
            {
                spans.Add(WindowSpan(content, starts, lines, i, n, trailing));
                skipPast = i + n;
            }
        }

        return spans;
    }

    /// <summary>A line as the anchors compare it: Unicode-normalised and trimmed.</summary>
    private static string Anchor(string line) => UnicodeNormalize(line).Trim();

    // ---- 9. context aware ----

    private static List<TextSpan> ContextAware(string content, string pattern)
    {
        bool trailing = pattern.EndsWith('\n');
        string[] want = (trailing ? pattern[..^1] : pattern).Split('\n');
        int n = want.Length;
        string[] lines = content.Split('\n');
        // One line alone is never matched by its likeness (2026-10-22, review G11: an absent "x = 1" edited "y = 1"): a similarity is only
        // trusted with lines around it to agree.
        if (n < 2 || n > lines.Length)
        {
            return [];
        }

        string[] wanted = want.Select(l => l.Trim()).ToArray();
        int[] starts = LineStarts(lines);
        var spans = new List<TextSpan>();
        for (int i = 0; i + n <= lines.Length; i++)
        {
            bool all = LineSimilar(lines[i], wanted[0]) && LineSimilar(lines[i + n - 1], wanted[n - 1]);
            for (int k = 1; k < n - 1 && all; k++)
            {
                all = wanted[k].Length == 0 || LineSimilar(lines[i + k], wanted[k]);
            }

            if (!all)
            {
                continue;
            }

            spans.Add(WindowSpan(content, starts, lines, i, n, trailing));
            i += n - 1;
        }

        return spans;
    }

    private static bool LineSimilar(string line, string wantedTrimmed)
    {
        string have = line.Trim();
        return string.Equals(have, wantedTrimmed, StringComparison.Ordinal)
            || (CanReach(have, wantedTrimmed, LineThreshold) && SequenceSimilarity.Ratio(have, wantedTrimmed) >= LineThreshold);
    }

    /// <summary>
    /// Whether two texts' lengths allow a ratio of <paramref name="threshold"/> at all — 2·min ÷ (a + b) is the most a ratio can be — so the
    /// full ratio is never worked out for a pair that can't reach it (2026-10-22, review V7).
    /// </summary>
    private static bool CanReach(string a, string b, double threshold)
    {
        int total = a.Length + b.Length;
        return total == 0 || 2.0 * Math.Min(a.Length, b.Length) / total >= threshold;
    }

    /// <summary>The most anchor pairs a block match scores; past it the match is too loose to trust (2026-10-22, review V7).</summary>
    public const int MaxAnchorCandidates = 200;

    // ---- the orchestrator's helpers ----

    /// <summary>
    /// Whether a re-sent edit is done already: <paramref name="newText"/> (at least <see cref="MinAppliedChars"/>
    /// trimmed) is in the text exactly and <paramref name="oldText"/> is not.
    /// </summary>
    public static bool IsAppliedAround(string content, string oldText, string newText)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(oldText);
        ArgumentNullException.ThrowIfNull(newText);
        int inner = newText.IndexOf(oldText, StringComparison.Ordinal);
        if (inner < 0 || newText.Trim().Length < MinAppliedChars || !content.Contains(newText, StringComparison.Ordinal))
        {
            return false;
        }

        // Where the new text stands, and where the old text would sit inside each.
        var covered = new HashSet<int>();
        for (int at = content.IndexOf(newText, StringComparison.Ordinal); at >= 0; at = content.IndexOf(newText, at + 1, StringComparison.Ordinal))
        {
            for (int o = newText.IndexOf(oldText, StringComparison.Ordinal); o >= 0; o = newText.IndexOf(oldText, o + 1, StringComparison.Ordinal))
            {
                covered.Add(at + o);
            }
        }

        for (int at = content.IndexOf(oldText, StringComparison.Ordinal); at >= 0; at = content.IndexOf(oldText, at + 1, StringComparison.Ordinal))
        {
            if (!covered.Contains(at))
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsAlreadyApplied(string content, string oldText, string newText)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(oldText);
        ArgumentNullException.ThrowIfNull(newText);
        return newText.Trim().Length >= MinAppliedChars
            && content.Contains(newText, StringComparison.Ordinal)
            && !content.Contains(oldText, StringComparison.Ordinal);
    }

    /// <summary>
    /// The guard on a non-exact match: <c>\'</c> or <c>\"</c> in the arguments that the matched text
    /// does not hold, or every backslash run in <paramref name="oldText"/> exactly twice the matched
    /// text's — the shape of a call escaped once too often, which a loose match would write into the file.
    /// </summary>
    public static EscapeDrift DetectEscapeDrift(string matched, string oldText, string newText)
    {
        ArgumentNullException.ThrowIfNull(matched);
        ArgumentNullException.ThrowIfNull(oldText);
        ArgumentNullException.ThrowIfNull(newText);
        if ((oldText.Contains("\\'", StringComparison.Ordinal) || newText.Contains("\\'", StringComparison.Ordinal)) && !matched.Contains("\\'", StringComparison.Ordinal))
        {
            return EscapeDrift.QuoteSingle;
        }

        if ((oldText.Contains("\\\"", StringComparison.Ordinal) || newText.Contains("\\\"", StringComparison.Ordinal)) && !matched.Contains("\\\"", StringComparison.Ordinal))
        {
            return EscapeDrift.QuoteDouble;
        }

        var ours = BackslashRuns(oldText);
        var theirs = BackslashRuns(matched);
        if (ours.Count > 0 && ours.Count == theirs.Count)
        {
            bool doubled = true;
            for (int i = 0; i < ours.Count && doubled; i++)
            {
                doubled = ours[i] == 2 * theirs[i];
            }

            if (doubled)
            {
                return EscapeDrift.DoubledBackslashes;
            }
        }

        return EscapeDrift.None;
    }

    private static List<int> BackslashRuns(string text)
    {
        var runs = new List<int>();
        int run = 0;
        foreach (char c in text)
        {
            if (c == '\\')
            {
                run++;
            }
            else if (run > 0)
            {
                runs.Add(run);
                run = 0;
            }
        }

        if (run > 0)
        {
            runs.Add(run);
        }

        return runs;
    }

    /// <summary>
    /// <paramref name="newText"/> moved from the indentation <paramref name="oldText"/>'s first line has
    /// to the one the matched <paramref name="region"/>'s first line has: the old indent stripped off
    /// the front of each line when present (else the line's own), the file's put there; a blank line
    /// stays blank; nothing changes when the two indents agree.
    /// </summary>
    public static string Reindent(string region, string oldText, string newText)
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(oldText);
        ArgumentNullException.ThrowIfNull(newText);
        string to = LeadingWhitespace(region);
        string from = LeadingWhitespace(oldText);
        if (string.Equals(to, from, StringComparison.Ordinal) || newText.Length == 0)
        {
            return newText;
        }

        var lines = newText.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Trim().Length == 0)
            {
                continue;
            }

            string rest = line.StartsWith(from, StringComparison.Ordinal) ? line[from.Length..] : line.TrimStart(' ', '\t');
            lines[i] = to + rest;
        }

        return string.Join('\n', lines);
    }

    /// <summary>The spaces and tabs a text's first line opens with.</summary>
    private static string LeadingWhitespace(string text)
    {
        int i = 0;
        while (i < text.Length && text[i] is ' ' or '\t')
        {
            i++;
        }

        return text[..i];
    }

    /// <summary>
    /// Under <see cref="MatchStrategy.UnicodeNormalized"/>: the parts of <paramref name="newText"/> that
    /// <paramref name="oldText"/> already had come from the matched <paramref name="region"/>, so the file's
    /// <c>“ ” — ’</c> stay where the arguments spelt them plain; what the edit changes is written as given.
    /// Only when the region and <paramref name="oldText"/> normalise to the same text; else as given.
    /// </summary>
    public static string PreserveUnicode(string region, string oldText, string newText)
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(oldText);
        ArgumentNullException.ThrowIfNull(newText);
        string normalizedRegion = UnicodeNormalize(region, out int[] map);
        if (!string.Equals(normalizedRegion, UnicodeNormalize(oldText), StringComparison.Ordinal) || string.Equals(normalizedRegion, region, StringComparison.Ordinal))
        {
            return newText;
        }

        // Which original character each normalised position belongs to, and where each original starts.
        int[] owner = new int[normalizedRegion.Length];
        for (int i = 0; i < region.Length; i++)
        {
            for (int p = map[i]; p < map[i + 1]; p++)
            {
                owner[p] = i;
            }
        }

        var sb = new StringBuilder(newText.Length);
        foreach (var code in SequenceSimilarity.Opcodes(normalizedRegion, newText))
        {
            if (code.Tag != SequenceSimilarity.Op.Equal)
            {
                sb.Append(newText, code.J1, code.J2 - code.J1);
                continue;
            }

            for (int p = code.I1; p < code.I2; p++)
            {
                int o = owner[p];
                bool whole = map[o] >= code.I1 && map[o + 1] <= code.I2;
                if (!whole)
                {
                    sb.Append(newText[code.J1 + (p - code.I1)]);
                }
                else if (p == map[o])
                {
                    sb.Append(region[o]);
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>The first <paramref name="cap"/> spans as line number + that line's text, trimmed.</summary>
    public static IReadOnlyList<MatchLocation> Locate(string content, IReadOnlyList<TextSpan> spans, int cap = MaxLocations)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(spans);
        var locations = new List<MatchLocation>(Math.Min(cap, spans.Count));
        foreach (var span in spans.Take(cap))
        {
            int start = Math.Min(span.Start, content.Length);
            int lineStart = start == 0 ? 0 : content.LastIndexOf('\n', start - 1) + 1;
            int lineEnd = content.IndexOf('\n', start);
            if (lineEnd < 0)
            {
                lineEnd = content.Length;
            }

            int line = 1 + content.AsSpan(0, start).Count('\n');
            locations.Add(new MatchLocation(line, content[lineStart..lineEnd].Trim()));
        }

        return locations;
    }
}
